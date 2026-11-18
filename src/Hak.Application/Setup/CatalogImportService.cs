using System.Globalization;
using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Setup;

/// <summary>One row of a chart-of-accounts sheet. Text fields as read from the file; parsed and validated by the server.</summary>
public sealed record AccountImportRow(int Line, string? Code, string? Name, string? ParentCode = null, string? IsPostable = null,
    string? RequiresPartner = null, string? OpenItem = null, string? CashKind = null, string? NegativePolicy = null, string? BalanceDisplay = null,
    string? ReportTags = null);

public sealed record PartnerImportRow(int Line, string? Code, string? Name, string? Roles = null, string? TaxCode = null);

/// <summary>One opening-balance line: one of Debit / Credit; Currency + ForeignAmount for foreign-currency items.</summary>
public sealed record OpeningImportRow(int Line, string? AccountCode, string? PartnerCode = null, string? Debit = null, string? Credit = null,
    string? Currency = null, string? ForeignAmount = null, string? DueDate = null, string? Description = null);

public sealed record ImportIssue(int Line, string Field, string Message);

/// <summary>
/// Result of a dry run or an import. <see cref="Committed"/> is true only when every row was valid and everything was written in
/// one transaction; any issue means nothing was written.
/// </summary>
public sealed record ImportResult(bool Committed, int Valid, int Skipped, IReadOnlyList<ImportIssue> Issues, IReadOnlyList<string> Notes,
    Guid? VoucherId = null, string? VoucherNumber = null, decimal TotalDebit = 0, decimal TotalCredit = 0, IReadOnlyList<string>? Documents = null);

/// <summary>
/// Bulk import of the chart of accounts, partners and opening balances (migration from another system). The client reads the
/// spreadsheet / CSV and sends text cells; this service owns every rule: codes, hierarchy order, enum aliases, decimal format.
/// All-or-nothing: a dry run lists every problem; an import writes nothing unless the whole file is valid. Existing codes are
/// skipped, never overwritten. Opening balances become one OPB draft voucher that still goes through submit → approve → post,
/// so the ledger keeps its maker-checker control.
/// </summary>
public sealed partial class CatalogImportService(HakDbContext db, SetupService setup, AccountingEngine engine, AuditService audit, Inventory.StockService stock, Assets.AssetService assets)
{
    public const int MaxRows = 20_000;

    // ---------------- Chart of accounts ----------------

    public async Task<ImportResult> ImportAccountsAsync(SessionContext ctx, IReadOnlyList<AccountImportRow> rows, bool commit)
    {
        ctx.Require(Permissions.CatalogManage);
        RequireSize(rows.Count);
        var issues = new List<ImportIssue>();
        var notes = new List<string>();
        var existing = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId)
            .ToDictionaryAsync(a => a.Code, a => (a.Name, a.IsPostable), StringComparer.Ordinal);
        var parsed = new List<(AccountImportRow Row, string Code, string Name, string? Parent, bool Postable, bool Partner, OpenItemNature Open,
            CashKind Cash, NegativeBalancePolicy Neg, BalanceDisplay Display, string? Tags)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var r in rows)
        {
            var code = Norm(r.Code)?.ToUpperInvariant();
            var name = Norm(r.Name);
            if (code is null) { issues.Add(new(r.Line, "Mã TK", "Thiếu mã tài khoản.")); continue; }
            if (!SetupService.IsValidCode(code)) { issues.Add(new(r.Line, "Mã TK", $"Mã '{code}' không hợp lệ (chỉ A–Z, 0–9, _ . -; tối đa 30 ký tự).")); continue; }
            if (seen.TryGetValue(code, out var first)) { issues.Add(new(r.Line, "Mã TK", $"Mã {code} trùng với dòng {first}.")); continue; }
            seen[code] = r.Line;
            if (existing.TryGetValue(code, out var old))
            {
                skipped++;
                if (name is not null && name != old.Name) notes.Add($"Dòng {r.Line}: TK {code} đã có tên '{old.Name}' — giữ nguyên, không ghi đè.");
                continue;
            }
            if (name is null) { issues.Add(new(r.Line, "Tên TK", "Thiếu tên tài khoản.")); continue; }
            var before = issues.Count;
            var postable = Bool(r.IsPostable, true, r.Line, "Chi tiết", issues);
            var partner = Bool(r.RequiresPartner, false, r.Line, "Theo dõi đối tượng", issues);
            var open = Choice(r.OpenItem, OpenItemNature.None, r.Line, "Công nợ", issues,
                (OpenItemNature.Receivable, ["RECEIVABLE", "PHẢI THU", "PHAI THU", "THU"]), (OpenItemNature.Payable, ["PAYABLE", "PHẢI TRẢ", "PHAI TRA", "TRẢ", "TRA"]));
            var cash = Choice(r.CashKind, CashKind.None, r.Line, "Loại tiền", issues,
                (CashKind.Cash, ["CASH", "TIỀN MẶT", "TIEN MAT", "TM"]), (CashKind.Bank, ["BANK", "NGÂN HÀNG", "NGAN HANG", "NH", "TGNH"]),
                (CashKind.InTransit, ["INTRANSIT", "ĐANG CHUYỂN", "DANG CHUYEN"]));
            var neg = Choice(r.NegativePolicy, NegativeBalancePolicy.None, r.Line, "Số dư âm", issues,
                (NegativeBalancePolicy.Warn, ["WARN", "CẢNH BÁO", "CANH BAO"]), (NegativeBalancePolicy.Block, ["BLOCK", "CHẶN", "CHAN"]));
            var display = Choice(r.BalanceDisplay, Domain.Catalog.BalanceDisplay.Net, r.Line, "Trình bày số dư", issues,
                (Domain.Catalog.BalanceDisplay.GrossByPartner, ["GROSS", "GROSSBYPARTNER", "LƯỠNG TÍNH", "LUONG TINH", "THEO ĐỐI TƯỢNG"]));
            if (open != OpenItemNature.None && !partner) issues.Add(new(r.Line, "Công nợ", "Tài khoản theo dõi công nợ theo chứng từ phải bắt buộc đối tượng."));
            if (open != OpenItemNature.None && cash != CashKind.None) issues.Add(new(r.Line, "Loại tiền", "Tài khoản tiền không thể đồng thời là tài khoản công nợ."));
            if (!postable && (partner || open != OpenItemNature.None || cash != CashKind.None))
                issues.Add(new(r.Line, "Chi tiết", "Tài khoản tổng hợp không nhận bút toán nên không khai báo đối tượng / công nợ / loại tiền."));
            string? tags = null;
            try { tags = SetupService.NormalizeTags(r.ReportTags); }
            catch (DomainException ex) { issues.Add(new(r.Line, "Nhãn BC", ex.Message)); }
            if (issues.Count == before)
                parsed.Add((r, code, name, Norm(r.ParentCode)?.ToUpperInvariant(), postable, partner, open, cash, neg, display, tags));
        }

        // Parents: an existing summary account or a summary account in the file; create in hierarchy order (parents first).
        var inFile = parsed.ToDictionary(p => p.Code, StringComparer.Ordinal);
        foreach (var p in parsed.Where(p => p.Parent is not null))
        {
            if (p.Parent == p.Code) issues.Add(new(p.Row.Line, "TK cấp trên", "Tài khoản không thể là cấp trên của chính nó."));
            else if (inFile.TryGetValue(p.Parent!, out var fp) && fp.Postable)
                issues.Add(new(p.Row.Line, "TK cấp trên", $"TK cấp trên {p.Parent} (dòng {fp.Row.Line}) là TK chi tiết; đánh dấu nó là tổng hợp."));
            else if (!inFile.ContainsKey(p.Parent!) && !existing.ContainsKey(p.Parent!) && !seen.ContainsKey(p.Parent!))
                issues.Add(new(p.Row.Line, "TK cấp trên", $"TK cấp trên {p.Parent} không có trong hệ thống và trong tệp."));
            else if (!inFile.ContainsKey(p.Parent!) && existing.TryGetValue(p.Parent!, out var ep) && ep.IsPostable)
                issues.Add(new(p.Row.Line, "TK cấp trên", $"TK cấp trên {p.Parent} đang là TK chi tiết."));
        }
        var ordered = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = visiting, 2 = done
        foreach (var code in inFile.Keys) Visit(code);
        void Visit(string code)
        {
            if (state.TryGetValue(code, out var st)) { if (st == 1) issues.Add(new(inFile[code].Row.Line, "TK cấp trên", $"Vòng lặp cấp trên tại {code}.")); return; }
            state[code] = 1;
            var parent = inFile[code].Parent;
            if (parent is not null && parent != code && inFile.ContainsKey(parent)) Visit(parent);
            state[code] = 2;
            ordered.Add(code);
        }

        var result = new ImportResult(false, parsed.Count, skipped, Sorted(issues), notes);
        if (!commit || issues.Count > 0 || parsed.Count == 0) return result;
        await db.InTransactionAsync(async () =>
        {
            foreach (var code in ordered)
            {
                var p = inFile[code];
                var created = await WithLine(p.Row.Line, () => setup.CreateAccountAsync(ctx, p.Code, p.Name, p.Parent, p.Postable, p.Partner, p.Display, p.Open, p.Cash, p.Neg));
                if (p.Tags is not null) await setup.SetAccountTagsAsync(ctx, created.Id, p.Tags);
            }
            await audit.AppendAsync(ctx.CompanyId, ctx, "import.accounts", "company", ctx.CompanyId.ToString(), new { created = parsed.Count, skipped });
            await db.SaveChangesAsync();
        });
        return result with { Committed = true };
    }

    // ---------------- Partners ----------------

    public async Task<ImportResult> ImportPartnersAsync(SessionContext ctx, IReadOnlyList<PartnerImportRow> rows, bool commit)
    {
        ctx.Require(Permissions.CatalogManage);
        RequireSize(rows.Count);
        var issues = new List<ImportIssue>();
        var notes = new List<string>();
        var existing = await db.BusinessPartners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId)
            .ToDictionaryAsync(p => p.Code, p => p.Name, StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var valid = new List<(int Line, string Code, string Name, PartnerRoles Roles, string? Tax)>();
        var skipped = 0;
        foreach (var r in rows)
        {
            var code = Norm(r.Code)?.ToUpperInvariant();
            var name = Norm(r.Name);
            if (code is null) { issues.Add(new(r.Line, "Mã", "Thiếu mã đối tượng.")); continue; }
            if (!SetupService.IsValidCode(code)) { issues.Add(new(r.Line, "Mã", $"Mã '{code}' không hợp lệ (chỉ A–Z, 0–9, _ . -; tối đa 30 ký tự).")); continue; }
            if (seen.TryGetValue(code, out var first)) { issues.Add(new(r.Line, "Mã", $"Mã {code} trùng với dòng {first}.")); continue; }
            seen[code] = r.Line;
            if (existing.TryGetValue(code, out var oldName))
            {
                skipped++;
                if (name is not null && name != oldName) notes.Add($"Dòng {r.Line}: đối tượng {code} đã có tên '{oldName}' — giữ nguyên, không ghi đè.");
                continue;
            }
            if (name is null) { issues.Add(new(r.Line, "Tên", "Thiếu tên đối tượng.")); continue; }
            var roles = PartnerRoles.None;
            var bad = false;
            foreach (var part in (Norm(r.Roles) ?? "").Split([',', ';', '+', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var role = Key(part) switch
                {
                    "KH" or "KHÁCH HÀNG" or "KHACH HANG" or "CUSTOMER" => PartnerRoles.Customer,
                    "NCC" or "NHÀ CUNG CẤP" or "NHA CUNG CAP" or "SUPPLIER" => PartnerRoles.Supplier,
                    "NV" or "NHÂN VIÊN" or "NHAN VIEN" or "EMPLOYEE" => PartnerRoles.Employee,
                    "KHAC" or "KHÁC" or "OTHER" => PartnerRoles.Other,
                    _ => PartnerRoles.None,
                };
                if (role == PartnerRoles.None) { issues.Add(new(r.Line, "Vai trò", $"Vai trò '{part}' không nhận ra (KH, NCC, NV, KHAC).")); bad = true; }
                roles |= role;
            }
            if (!bad) valid.Add((r.Line, code, name, roles == PartnerRoles.None ? PartnerRoles.Other : roles, Norm(r.TaxCode)));
        }
        var result = new ImportResult(false, valid.Count, skipped, Sorted(issues), notes);
        if (!commit || issues.Count > 0 || valid.Count == 0) return result;
        await db.InTransactionAsync(async () =>
        {
            foreach (var p in valid) await WithLine(p.Line, () => setup.CreatePartnerAsync(ctx, p.Code, p.Name, p.Roles, p.Tax));
            await audit.AppendAsync(ctx.CompanyId, ctx, "import.partners", "company", ctx.CompanyId.ToString(), new { created = valid.Count, skipped });
            await db.SaveChangesAsync();
        });
        return result with { Committed = true };
    }

    // ---------------- Opening balances ----------------

    /// <summary>Validates the lines and (commit) creates one OPB draft voucher dated <paramref name="date"/>.</summary>
    public async Task<ImportResult> ImportOpeningBalancesAsync(SessionContext ctx, DateOnly date, string? description, IReadOnlyList<OpeningImportRow> rows, bool commit)
    {
        ctx.Require(Permissions.VoucherCreate);
        RequireSize(rows.Count);
        var issues = new List<ImportIssue>();
        var notes = new List<string>();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).ToDictionaryAsync(a => a.Code, StringComparer.Ordinal);
        var partners = await db.BusinessPartners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).ToDictionaryAsync(p => p.Code, StringComparer.Ordinal);
        var functional = await db.Companies.Where(c => c.Id == ctx.CompanyId).Select(c => c.FunctionalCurrency).FirstAsync();
        var currencies = await db.Currencies.AsNoTracking().ToDictionaryAsync(c => c.Code, c => c.DecimalPlaces, StringComparer.Ordinal);
        var dp = currencies[functional];
        var lines = new List<VoucherLineInput>();
        foreach (var r in rows)
        {
            var before = issues.Count;
            var code = Norm(r.AccountCode)?.ToUpperInvariant();
            Account? acc = null;
            if (code is null) issues.Add(new(r.Line, "Mã TK", "Thiếu mã tài khoản."));
            else if (!accounts.TryGetValue(code, out acc)) issues.Add(new(r.Line, "Mã TK", $"TK {code} không tồn tại (nhập hệ thống TK trước)."));
            else if (!acc.IsPostable) issues.Add(new(r.Line, "Mã TK", $"TK {code} là TK tổng hợp; số dư phải nhập vào TK chi tiết."));
            else if (!acc.IsActive) issues.Add(new(r.Line, "Mã TK", $"TK {code} đã ngừng dùng."));

            var partnerCode = Norm(r.PartnerCode)?.ToUpperInvariant();
            BusinessPartner? partner = null;
            if (partnerCode is not null && !partners.TryGetValue(partnerCode, out partner)) issues.Add(new(r.Line, "Đối tượng", $"Đối tượng {partnerCode} không tồn tại."));
            else if (partner is { IsActive: false }) issues.Add(new(r.Line, "Đối tượng", $"Đối tượng {partnerCode} đã ngừng theo dõi."));
            if (acc is { RequiresPartner: true } && partnerCode is null) issues.Add(new(r.Line, "Đối tượng", $"TK {code} bắt buộc đối tượng."));

            var parseErrors = issues.Count;
            var debit = Amount(r.Debit, r.Line, "Nợ", issues);
            var credit = Amount(r.Credit, r.Line, "Có", issues);
            if (issues.Count > parseErrors) { }
            else if (debit < 0 || credit < 0) issues.Add(new(r.Line, "Số tiền", "Số tiền không được âm; số dư bên kia ghi vào cột còn lại."));
            else if ((debit > 0) == (credit > 0)) issues.Add(new(r.Line, "Số tiền", "Mỗi dòng ghi đúng một bên: Nợ hoặc Có."));
            var amount = debit > 0 ? debit : credit;
            if (amount > 0 && !MoneyMath.HasAtMostDecimals(amount, dp)) issues.Add(new(r.Line, "Số tiền", $"Số tiền {functional} tối đa {dp} chữ số thập phân."));

            var currency = Norm(r.Currency)?.ToUpperInvariant();
            if (currency == functional) currency = null;
            decimal? foreign = null, rate = null;
            if (currency is not null)
            {
                foreign = Amount(r.ForeignAmount, r.Line, "Nguyên tệ", issues);
                if (!currencies.TryGetValue(currency, out var fdp)) issues.Add(new(r.Line, "Loại tiền", $"Loại tiền {currency} chưa khai báo."));
                else if (foreign <= 0) issues.Add(new(r.Line, "Nguyên tệ", "Dòng ngoại tệ phải có số nguyên tệ dương."));
                else if (!MoneyMath.HasAtMostDecimals(foreign.Value, fdp)) issues.Add(new(r.Line, "Nguyên tệ", $"{currency} tối đa {fdp} chữ số thập phân."));
                else if (amount > 0 && (rate = Domain.Fx.FxSettlementMath.ExactRate(foreign.Value, amount, dp)) is null)
                    issues.Add(new(r.Line, "Nguyên tệ", $"Không có tỷ giá 6 chữ số nào quy đổi {foreign} {currency} ra đúng {amount}; tách dòng hoặc kiểm tra số liệu."));
            }
            else if (Norm(r.ForeignAmount) is not null) issues.Add(new(r.Line, "Loại tiền", "Có số nguyên tệ nhưng thiếu mã loại tiền."));

            DateOnly? due = null;
            if (Norm(r.DueDate) is { } d)
            {
                if (TryDate(d, out var dd)) due = dd;
                else issues.Add(new(r.Line, "Hạn thanh toán", $"Ngày '{d}' không hợp lệ (dùng yyyy-MM-dd hoặc dd/MM/yyyy)."));
            }
            if (issues.Count == before && acc is not null)
                lines.Add(new VoucherLineInput(acc.Id, debit > 0 ? Side.Debit : Side.Credit, amount, partner?.Id, currency, foreign, rate,
                    Norm(r.Description) ?? $"Số dư đầu kỳ {acc.Code}{(partner is null ? "" : " – " + partner.Code)}", due));
        }
        var totalDebit = lines.Where(l => l.Side == Side.Debit).Sum(l => l.Amount);
        var totalCredit = lines.Where(l => l.Side == Side.Credit).Sum(l => l.Amount);
        if (issues.Count == 0 && totalDebit != totalCredit)
            issues.Add(new(0, "Tổng", $"Tổng Nợ {totalDebit} khác tổng Có {totalCredit} (chênh {totalDebit - totalCredit})."));
        if (lines.Count == 0 && issues.Count == 0) issues.Add(new(0, "Tổng", "Không có dòng số dư nào."));
        var existing = await db.Vouchers.AsNoTracking()
            .Where(v => v.CompanyId == ctx.CompanyId && v.VoucherType == VoucherTypes.OpeningBalance && v.Status != VoucherStatus.Cancelled && v.Status != VoucherStatus.Reversed)
            .Select(v => new { v.Id, v.Number }).ToListAsync();
        if (existing.Count > 0)
        {
            // The same file imported twice: an active OPB voucher with exactly these lines (account, side, amount, partner, currency, foreign amount).
            static string Key(Guid account, Side side, decimal amount, Guid? partner, string? currency, decimal? foreign) =>
                $"{account}|{(int)side}|{amount:0.####}|{partner}|{currency}|{foreign:0.####}";
            var mine = lines.Select(l => Key(l.AccountId, l.Side, l.Amount, l.PartnerId, l.ForeignCurrency, l.ForeignAmount)).Order(StringComparer.Ordinal).ToList();
            var ids = existing.Select(e => e.Id).ToList();
            var theirs = (await db.VoucherLines.AsNoTracking().Where(l => ids.Contains(l.VoucherId))
                    .Select(l => new { l.VoucherId, l.AccountId, l.Side, l.Amount, l.PartnerId, l.ForeignCurrency, l.ForeignAmount }).ToListAsync())
                .GroupBy(l => l.VoucherId)
                .ToDictionary(g => g.Key, g => g.Select(l => Key(l.AccountId, l.Side, l.Amount, l.PartnerId, l.ForeignCurrency, l.ForeignAmount)).Order(StringComparer.Ordinal).ToList());
            var same = existing.FirstOrDefault(e => mine.Count > 0 && theirs.TryGetValue(e.Id, out var t) && t.SequenceEqual(mine, StringComparer.Ordinal));
            if (same is not null)
                issues.Add(new(0, "Trùng", $"Số dư này đã được nhập ở chứng từ {same.Number} (cùng tài khoản, đối tượng, số tiền). Không nhập lại; nếu cần sửa thì hủy hoặc đảo chứng từ đó trước."));
            else
                notes.Add($"Công ty đã có chứng từ số dư đầu kỳ (OPB) khác: {string.Join(", ", existing.Select(e => e.Number))} — kiểm tra để tránh nhập trùng.");
        }

        var result = new ImportResult(false, lines.Count, 0, Sorted(issues), notes, TotalDebit: totalDebit, TotalCredit: totalCredit);
        if (!commit || issues.Count > 0) return result;
        var v = await engine.CreateDraftAsync(ctx, new VoucherInput(VoucherTypes.OpeningBalance, date, date,
            Norm(description) ?? $"Số dư đầu kỳ ngày {date:dd/MM/yyyy}", lines));
        return result with { Committed = true, VoucherId = v.Id, VoucherNumber = v.Number };
    }

    // ---------------- helpers ----------------

    private static void RequireSize(int count)
    {
        if (count > MaxRows) throw new DomainException(ErrorCodes.ValidationFailed, $"Tệp quá lớn: tối đa {MaxRows} dòng mỗi lần nhập.");
    }

    private static IReadOnlyList<ImportIssue> Sorted(List<ImportIssue> issues) => issues.OrderBy(i => i.Line).ToList();

    private static async Task<T> WithLine<T>(int line, Func<Task<T>> work)
    {
        try { return await work(); }
        catch (DomainException ex) { throw new DomainException(ex.Code, $"Dòng {line}: {ex.Message}"); }
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Key(string s) => s.Trim().ToUpperInvariant();

    private static bool Bool(string? raw, bool fallback, int line, string field, List<ImportIssue> issues)
    {
        if (Norm(raw) is not { } s) return fallback;
        switch (Key(s))
        {
            case "1" or "X" or "C" or "CÓ" or "CO" or "Y" or "YES" or "TRUE": return true;
            case "0" or "K" or "KHÔNG" or "KHONG" or "N" or "NO" or "FALSE": return false;
            default: issues.Add(new(line, field, $"Giá trị '{s}' không hiểu (dùng Có/Không, 1/0, x).")); return fallback;
        }
    }

    private static T Choice<T>(string? raw, T fallback, int line, string field, List<ImportIssue> issues, params (T Value, string[] Aliases)[] options)
    {
        if (Norm(raw) is not { } s) return fallback;
        var k = Key(s);
        if (k is "0" or "KHÔNG" or "KHONG" or "NONE" or "-") return fallback;
        foreach (var (value, aliases) in options) if (aliases.Contains(k)) return value;
        issues.Add(new(line, field, $"Giá trị '{s}' không hiểu (dùng: {string.Join(", ", options.Select(o => o.Aliases[1 < o.Aliases.Length ? 1 : 0]))} hoặc để trống)."));
        return fallback;
    }

    /// <summary>
    /// Plain decimal text. Thousands separators are accepted only when unambiguous: "1.234.567,5" / "1,234,567.5" (both separators,
    /// the last one is the decimal point) or a separator repeated ("1.000.000"). A single separator is the decimal point ("12.5",
    /// "1,5") unless exactly three digits follow it — "1.000" / "1,500" could mean either, so they are refused.
    /// </summary>
    public static decimal ParseAmount(string s, out string? error)
    {
        error = null;
        var t = s.Replace(" ", "").Replace(" ", "");
        var neg = t.StartsWith('(') && t.EndsWith(')');
        if (neg) t = t[1..^1];
        int dots = t.Count(c => c == '.'), commas = t.Count(c => c == ',');
        if (dots > 0 && commas > 0)
        {
            var decimalSep = t.LastIndexOf('.') > t.LastIndexOf(',') ? '.' : ',';
            var thousands = decimalSep == '.' ? ',' : '.';
            if (t.Count(c => c == decimalSep) > 1) { error = $"'{s}' có nhiều dấu thập phân."; return 0; }
            t = t.Replace(thousands.ToString(), "").Replace(',', '.');
        }
        else if (dots > 1) t = t.Replace(".", "");
        else if (commas > 1) t = t.Replace(",", "");
        else if (dots + commas == 1)
        {
            var sep = dots == 1 ? '.' : ',';
            if (t.Length - t.IndexOf(sep) - 1 == 3)
            {
                error = $"'{s}': không rõ dấu '{sep}' là phân cách nghìn hay thập phân; bỏ dấu phân cách nghìn (1000000) hoặc ghi đủ (1.000.000).";
                return 0;
            }
            t = t.Replace(',', '.');
        }
        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
        {
            error = $"'{s}' không phải số.";
            return 0;
        }
        return neg ? -v : v;
    }

    private static decimal Amount(string? raw, int line, string field, List<ImportIssue> issues)
    {
        if (Norm(raw) is not { } s) return 0;
        var v = ParseAmount(s, out var error);
        if (error is not null) issues.Add(new(line, field, error));
        return v;
    }

    private static bool TryDate(string s, out DateOnly d) =>
        DateOnly.TryParseExact(s, ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
}
