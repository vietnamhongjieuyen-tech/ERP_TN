using System.Globalization;
using System.Text.Json;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Ledger;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Setup;

/// <summary>One line of a template; Side is "D" or "C"; Amount may be left empty to be typed each time.</summary>
public sealed record TemplateLine(Guid AccountId, string Side, decimal? Amount, Guid? PartnerId = null, string? Description = null,
    string? CashFlowCode = null, Guid? AnalysisCodeId = null);

public sealed record TemplateInfo(Guid Id, string Name, string VoucherType, string Description, IReadOnlyList<TemplateLine> Lines, DateTime UpdatedAt);

public sealed record AnalysisCodeInfo(Guid Id, string Kind, string Code, string Name, bool IsActive);

/// <summary>Movements of one account under one analysis code in a date range (closing entries excluded).</summary>
public sealed record AnalysisReportRow(Guid AnalysisCodeId, string Kind, string Code, string Name, string AccountCode, string AccountName, decimal Debit, decimal Credit);

/// <summary>
/// Data-entry and approval set-up that sits beside the chart of accounts: voucher templates (định khoản mẫu), analysis codes
/// (mã phân tích) with their report, and the company's approval limit (hạn mức duyệt).
/// </summary>
public sealed class WorkflowSetupService(HakDbContext db, AuditService audit, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] TemplateTypes =
        [VoucherTypes.General, VoucherTypes.Adjustment, VoucherTypes.Allocation, VoucherTypes.OpeningBalance, VoucherTypes.Closing, VoucherTypes.Revaluation];

    // ---------------------------------------------------------------- voucher templates

    public async Task<IReadOnlyList<TemplateInfo>> TemplatesAsync(SessionContext ctx)
    {
        ctx.Require(Permissions.VoucherView);
        var rows = await db.VoucherTemplates.AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId && t.IsActive).OrderBy(t => t.Name).ToListAsync();
        return rows.Select(Info).ToList();
    }

    /// <summary>Creates the template, or replaces the active template of the same name.</summary>
    public async Task<TemplateInfo> SaveTemplateAsync(SessionContext ctx, string name, string voucherType, string? description, IReadOnlyList<TemplateLine> lines)
    {
        ctx.Require(Permissions.VoucherCreate);
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 100) throw new DomainException(ErrorCodes.ValidationFailed, "Tên mẫu bắt buộc, tối đa 100 ký tự.");
        if (!TemplateTypes.Contains(voucherType)) throw new DomainException(ErrorCodes.ValidationFailed, $"Loại chứng từ '{voucherType}' không lập bằng mẫu.");
        if (lines is null || lines.Count is 0 or > 200) throw new DomainException(ErrorCodes.ValidationFailed, "Mẫu cần từ 1 đến 200 dòng.");
        foreach (var (l, i) in lines.Select((l, i) => (l, i + 1)))
        {
            if (l.Side is not ("D" or "C")) throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {i}: chiều phải là D hoặc C.");
            if (l.Amount is < 0) throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {i}: số tiền không được âm.");
        }
        return await db.InTransactionAsync(async () =>
        {
            var accountIds = lines.Select(l => l.AccountId).Distinct().ToList();
            var known = await db.Accounts.Where(a => a.CompanyId == ctx.CompanyId && accountIds.Contains(a.Id) && a.IsPostable).Select(a => a.Id).ToListAsync();
            if (known.Count != accountIds.Count) throw new DomainException(ErrorCodes.AccountNotFound, "Mẫu có tài khoản không tồn tại hoặc không phải tài khoản chi tiết.");
            var partnerIds = lines.Where(l => l.PartnerId.HasValue).Select(l => l.PartnerId!.Value).Distinct().ToList();
            if (await db.BusinessPartners.CountAsync(p => p.CompanyId == ctx.CompanyId && partnerIds.Contains(p.Id)) != partnerIds.Count)
                throw new DomainException(ErrorCodes.ValidationFailed, "Mẫu có đối tượng không tồn tại.");
            var codeIds = lines.Where(l => l.AnalysisCodeId.HasValue).Select(l => l.AnalysisCodeId!.Value).Distinct().ToList();
            if (await db.AnalysisCodes.CountAsync(c => c.CompanyId == ctx.CompanyId && codeIds.Contains(c.Id)) != codeIds.Count)
                throw new DomainException(ErrorCodes.ValidationFailed, "Mẫu có mã phân tích không tồn tại.");

            var t = await db.VoucherTemplates.FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.Name == name);
            var created = t is null;
            if (t is null)
            {
                t = new VoucherTemplate { Id = Ids.New(), CompanyId = ctx.CompanyId, Name = name, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
                db.VoucherTemplates.Add(t);
            }
            t.VoucherType = voucherType;
            t.Description = (description ?? "").Trim();
            t.LinesJson = JsonSerializer.Serialize(lines.Select(l => new StoredLine(l.AccountId, l.Side, l.Amount?.ToString(CultureInfo.InvariantCulture), l.PartnerId,
                l.Description, Domain.Reports.CashFlowCodes.Normalize(l.CashFlowCode), l.AnalysisCodeId)), Json);
            t.IsActive = true;
            t.UpdatedAt = clock.UtcNow;
            t.UpdatedBy = ctx.UserId;
            await audit.AppendAsync(ctx.CompanyId, ctx, created ? "voucher_template.create" : "voucher_template.update", "voucher_template", t.Id.ToString(), new { name, voucherType, lines = lines.Count });
            await db.SaveChangesAsync();
            return Info(t);
        });
    }

    /// <summary>Takes a template out of the list; its name becomes free again.</summary>
    public async Task DeleteTemplateAsync(SessionContext ctx, Guid id)
    {
        ctx.Require(Permissions.VoucherCreate);
        await db.InTransactionAsync(async () =>
        {
            var t = await db.VoucherTemplates.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Mẫu không tồn tại.");
            db.VoucherTemplates.Remove(t);
            await audit.AppendAsync(ctx.CompanyId, ctx, "voucher_template.delete", "voucher_template", t.Id.ToString(), new { t.Name });
            await db.SaveChangesAsync();
        });
    }

    private sealed record StoredLine(Guid AccountId, string Side, string? Amount, Guid? PartnerId, string? Description, string? CashFlowCode, Guid? AnalysisCodeId);

    private static TemplateInfo Info(VoucherTemplate t) => new(t.Id, t.Name, t.VoucherType, t.Description,
        (JsonSerializer.Deserialize<List<StoredLine>>(t.LinesJson, Json) ?? []).Select(l => new TemplateLine(l.AccountId, l.Side,
            l.Amount is null ? null : decimal.Parse(l.Amount, CultureInfo.InvariantCulture), l.PartnerId, l.Description, l.CashFlowCode, l.AnalysisCodeId)).ToList(), t.UpdatedAt);

    // ---------------------------------------------------------------- analysis codes

    public async Task<IReadOnlyList<AnalysisCodeInfo>> AnalysisCodesAsync(SessionContext ctx)
    {
        ctx.Require(Permissions.VoucherView);
        return await db.AnalysisCodes.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).OrderBy(c => c.Kind).ThenBy(c => c.Code)
            .Select(c => new AnalysisCodeInfo(c.Id, c.Kind, c.Code, c.Name, c.IsActive)).ToListAsync();
    }

    public async Task<AnalysisCodeInfo> CreateAnalysisCodeAsync(SessionContext ctx, string kind, string code, string name)
    {
        ctx.Require(Permissions.CatalogManage);
        kind = (kind ?? "").Trim(); name = (name ?? "").Trim();
        code = (code ?? "").Trim().ToUpperInvariant();
        if (kind.Length is 0 or > 50) throw new DomainException(ErrorCodes.ValidationFailed, "Nhóm mã phân tích bắt buộc, tối đa 50 ký tự (ví dụ: Chuyến tàu, Bộ phận).");
        if (code.Length is 0 or > 30 || code.Any(char.IsWhiteSpace)) throw new DomainException(ErrorCodes.ValidationFailed, "Mã bắt buộc, tối đa 30 ký tự, không có khoảng trắng.");
        if (name.Length is 0 or > 250) throw new DomainException(ErrorCodes.ValidationFailed, "Tên bắt buộc, tối đa 250 ký tự.");
        return await db.InTransactionAsync(async () =>
        {
            if (await db.AnalysisCodes.AnyAsync(c => c.CompanyId == ctx.CompanyId && c.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Mã phân tích {code} đã tồn tại.");
            var c = new Domain.Vouchers.AnalysisCode { Id = Ids.New(), CompanyId = ctx.CompanyId, Kind = kind, Code = code, Name = name, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
            db.AnalysisCodes.Add(c);
            await audit.AppendAsync(ctx.CompanyId, ctx, "analysis_code.create", "analysis_code", c.Id.ToString(), new { kind, code, name });
            await db.SaveChangesAsync();
            return new AnalysisCodeInfo(c.Id, c.Kind, c.Code, c.Name, c.IsActive);
        });
    }

    /// <summary>Renames a code or takes it out of use. Codes are never deleted: posted lines keep pointing at them.</summary>
    public async Task UpdateAnalysisCodeAsync(SessionContext ctx, Guid id, string name, bool isActive)
    {
        ctx.Require(Permissions.CatalogManage);
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 250) throw new DomainException(ErrorCodes.ValidationFailed, "Tên bắt buộc, tối đa 250 ký tự.");
        await db.InTransactionAsync(async () =>
        {
            var c = await db.AnalysisCodes.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Mã phân tích không tồn tại.");
            var before = new { c.Name, c.IsActive };
            c.Name = name;
            c.IsActive = isActive;
            await audit.AppendAsync(ctx.CompanyId, ctx, "analysis_code.update", "analysis_code", c.Id.ToString(), new { c.Code, before, after = new { c.Name, c.IsActive } });
            await db.SaveChangesAsync();
        });
    }

    public async Task<IReadOnlyList<AnalysisReportRow>> AnalysisReportAsync(SessionContext ctx, DateOnly fromDate, DateOnly toDate)
    {
        ctx.Require(Permissions.ReportView);
        if (toDate < fromDate) throw new DomainException(ErrorCodes.ValidationFailed, "Khoảng ngày không hợp lệ.");
        var sums = await (from l in db.JournalLines
                          join e in db.JournalEntries on l.JournalEntryId equals e.Id
                          where l.CompanyId == ctx.CompanyId && l.AnalysisCodeId != null && l.PostingDate >= fromDate && l.PostingDate <= toDate
                                && e.VoucherType != VoucherTypes.Closing
                          group l by new { l.AnalysisCodeId, l.AccountId, l.Side } into g
                          select new { g.Key.AnalysisCodeId, g.Key.AccountId, g.Key.Side, Total = g.Sum(x => x.Amount) }).ToListAsync();
        var codes = await db.AnalysisCodes.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).ToDictionaryAsync(c => c.Id);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).ToDictionaryAsync(a => a.Id);
        return sums.GroupBy(s => (s.AnalysisCodeId, s.AccountId)).Select(g =>
            {
                var c = codes[g.Key.AnalysisCodeId!.Value];
                var a = accounts[g.Key.AccountId];
                return new AnalysisReportRow(c.Id, c.Kind, c.Code, c.Name, a.Code, a.Name,
                    g.Where(x => x.Side == Side.Debit).Sum(x => x.Total), g.Where(x => x.Side == Side.Credit).Sum(x => x.Total));
            })
            .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Code, StringComparer.Ordinal).ThenBy(r => r.AccountCode, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- print details (company address, bank account numbers)

    /// <summary>Registered address of the company, printed on vouchers and payment orders.</summary>
    public async Task SetCompanyAddressAsync(SessionContext ctx, string? address)
    {
        ctx.Require(Permissions.ConfigManage);
        address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        if (address is { Length: > 300 }) throw new DomainException(ErrorCodes.ValidationFailed, "Địa chỉ tối đa 300 ký tự.");
        await db.InTransactionAsync(async () =>
        {
            var company = await db.Companies.FirstAsync(c => c.Id == ctx.CompanyId);
            var before = company.Address;
            company.Address = address;
            company.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "company.address", "company", company.Id.ToString(), new { before, after = address }, company.RowVersion);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>Account number and bank name of a bank account of the chart (printed on payment orders). Nothing is posted.</summary>
    public async Task SetBankInfoAsync(SessionContext ctx, Guid accountId, string? bankAccountNo, string? bankName)
    {
        ctx.Require(Permissions.CatalogManage);
        bankAccountNo = string.IsNullOrWhiteSpace(bankAccountNo) ? null : bankAccountNo.Trim();
        bankName = string.IsNullOrWhiteSpace(bankName) ? null : bankName.Trim();
        if (bankAccountNo is { Length: > 50 } || bankName is { Length: > 200 })
            throw new DomainException(ErrorCodes.ValidationFailed, "Số tài khoản tối đa 50 ký tự, tên ngân hàng tối đa 200 ký tự.");
        await db.InTransactionAsync(async () =>
        {
            var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.AccountNotFound, "Tài khoản không tồn tại.");
            if (acc.CashKind != Domain.Catalog.CashKind.Bank)
                throw new DomainException(ErrorCodes.ValidationFailed, $"TK {acc.Code} không phải tài khoản tiền gửi ngân hàng.");
            var before = new { acc.BankAccountNo, acc.BankName };
            acc.BankAccountNo = bankAccountNo;
            acc.BankName = bankName;
            acc.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "account.bank_info", "account", acc.Id.ToString(), new { acc.Code, before, after = new { bankAccountNo, bankName } }, acc.RowVersion);
            await db.SaveChangesAsync();
        });
    }

    // ---------------------------------------------------------------- approval limit

    public async Task<decimal?> ApprovalLimitAsync(SessionContext ctx)
    {
        ctx.Require(Permissions.VoucherView);
        return await db.Companies.Where(c => c.Id == ctx.CompanyId).Select(c => c.ApprovalLimit).FirstAsync();
    }

    /// <summary>Sets (or with null removes) the amount above which approving a voucher needs voucher.approve_high.</summary>
    public async Task SetApprovalLimitAsync(SessionContext ctx, decimal? limit)
    {
        ctx.Require(Permissions.ConfigManage);
        if (limit is <= 0) throw new DomainException(ErrorCodes.ValidationFailed, "Hạn mức duyệt phải lớn hơn 0 (để trống nếu không áp dụng).");
        await db.InTransactionAsync(async () =>
        {
            var company = await db.Companies.FirstAsync(c => c.Id == ctx.CompanyId);
            var before = company.ApprovalLimit;
            company.ApprovalLimit = limit;
            company.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "company.approval_limit", "company", company.Id.ToString(), new { before, after = limit }, company.RowVersion);
            await db.SaveChangesAsync();
        });
    }
}
