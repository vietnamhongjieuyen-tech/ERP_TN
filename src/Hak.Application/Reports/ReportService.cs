using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Reports;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Reports;

public sealed record ReportLineInput(string Code, string Name, string? Formula = null, int Indent = 0, bool Bold = false, string? Note = null);
public sealed record ReportCheckInput(string Rule, string Message);

/// <summary>Template as exchanged in JSON files and edited on screen.</summary>
public sealed record ReportDefinitionInput(string Code, string Name, string Category, string? LegalReference,
    IReadOnlyList<ReportLineInput> Lines, IReadOnlyList<ReportCheckInput>? Checks = null, IReadOnlyList<string>? Columns = null);

public sealed record ReportDefinitionInfo(Guid Id, string Code, int Version, string Name, string Category, string? LegalReference, string Status,
    string? VerificationNote, DateTime? VerifiedAt, int LineCount, int Snapshots, DateTime CreatedAt);

public sealed record ReportRunLine(int LineNo, string Code, string Name, int Indent, bool Bold, string? Note,
    decimal? Value, decimal? CompareValue, IReadOnlyList<Contribution> Contributions, string? Error,
    IReadOnlyList<decimal?>? Values = null, IReadOnlyList<decimal?>? CompareValues = null);

public sealed record ReportRunCheck(string Rule, string Message, decimal Left, decimal Right, bool Passed, string? Error,
    decimal? CompareLeft, decimal? CompareRight, bool? ComparePassed);

/// <summary>Result of running a template. <see cref="Verified"/> false = print "CHƯA XÁC MINH" on every page.</summary>
public sealed record ReportRun(Guid DefinitionId, string Code, int Version, string Name, string? LegalReference, bool Verified, string CompanyName,
    DateOnly FromDate, DateOnly ToDate, DateOnly? CompareFrom, DateOnly? CompareTo,
    IReadOnlyList<ReportRunLine> Lines, IReadOnlyList<ReportRunCheck> Checks, IReadOnlyList<string>? Columns = null)
{
    public bool HasErrors => Lines.Any(l => l.Error is not null) || Checks.Any(c => c.Error is not null);
    public bool ChecksPassed => Checks.All(c => c.Passed && c.ComparePassed != false);
}

public sealed record ReportSnapshotInfo(Guid Id, string DefinitionCode, int DefinitionVersion, bool DefinitionVerified, DateOnly FromDate, DateOnly ToDate,
    DateOnly? CompareFrom, DateOnly? CompareTo, string Sha256, string Note, DateTime CreatedAt, string CreatedBy);

/// <summary>
/// Report Engine (docs/05 §1). Templates are company data: drafted (catalog.manage), verified by a second person
/// (period.lock, maker-checker), then frozen. Running reads posted journal lines only. A snapshot stores the exact result
/// (JSON + SHA-256) when figures are finalised; it can be re-displayed and its hash re-checked at any time.
/// </summary>
public sealed class ReportService(HakDbContext db, AuditService audit, IClock clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    // ---------------- Templates ----------------

    public async Task<IReadOnlyList<ReportDefinitionInfo>> ListAsync(SessionContext ctx)
    {
        ctx.Require(Permissions.ReportView);
        var defs = await db.ReportDefinitions.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId)
            .Select(d => new { d.Id, d.Code, d.Version, d.Name, d.Category, d.LegalReference, d.Status, d.VerificationNote, d.VerifiedAt, Lines = d.Lines.Count, d.CreatedAt })
            .ToListAsync();
        var snaps = await db.ReportSnapshots.Where(s => s.CompanyId == ctx.CompanyId).GroupBy(s => s.DefinitionId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return defs.OrderBy(d => d.Code, StringComparer.Ordinal).ThenByDescending(d => d.Version)
            .Select(d => new ReportDefinitionInfo(d.Id, d.Code, d.Version, d.Name, d.Category, d.LegalReference, d.Status.ToString(), d.VerificationNote,
                d.VerifiedAt, d.Lines, snaps.GetValueOrDefault(d.Id), d.CreatedAt)).ToList();
    }

    public async Task<ReportDefinitionInput> ExportAsync(SessionContext ctx, Guid id)
    {
        ctx.Require(Permissions.ReportView);
        var d = await LoadAsync(ctx, id);
        return new ReportDefinitionInput(d.Code, d.Name, d.Category, d.LegalReference,
            d.Lines.OrderBy(l => l.LineNo).Select(l => new ReportLineInput(l.Code, l.Name, l.Formula, l.Indent, l.Bold, l.Note)).ToList(),
            d.Checks.OrderBy(c => c.LineNo).Select(c => new ReportCheckInput(c.Rule, c.Message)).ToList(), ReportColumns.Parse(d.Columns) is { Count: > 0 } cols ? cols : null);
    }

    /// <summary>
    /// Saves a template as a Draft: replaces the latest version while it is a Draft without snapshots, otherwise creates the
    /// next version. Every formula is validated first; all problems are reported together.
    /// </summary>
    public async Task<ReportDefinition> SaveDraftAsync(SessionContext ctx, ReportDefinitionInput input)
    {
        ctx.Require(Permissions.CatalogManage);
        var code = input.Code.Trim().ToUpperInvariant();
        if (!Setup.SetupService.IsValidCode(code)) throw new DomainException(ErrorCodes.ValidationFailed, $"Mã mẫu '{code}' không hợp lệ (A–Z, 0–9, _ . -; tối đa 30 ký tự).");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new DomainException(ErrorCodes.ValidationFailed, "Tên mẫu bắt buộc.");
        if (input.Lines.Count == 0) throw new DomainException(ErrorCodes.ValidationFailed, "Mẫu phải có ít nhất một chỉ tiêu.");
        var lines = input.Lines.Select(l => l with
        {
            Code = l.Code.Trim().ToUpperInvariant(), Name = l.Name.Trim(), Formula = string.IsNullOrWhiteSpace(l.Formula) ? null : l.Formula.Trim(),
            Note = string.IsNullOrWhiteSpace(l.Note) ? null : l.Note.Trim(),
        }).ToList();
        var checks = (input.Checks ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Rule)).Select(c => c with { Rule = c.Rule.Trim(), Message = c.Message.Trim() }).ToList();
        var columns = (input.Columns ?? []).Select(c => c.Trim()).ToList();
        var problems = new List<string>();
        if (columns.Count > ReportColumns.Max) problems.Add($"Tối đa {ReportColumns.Max} cột số liệu.");
        foreach (var c in columns.Where(c => c.Length == 0 || c.Length > ReportColumns.MaxNameLength || c.Contains('|')))
            problems.Add($"Tên cột '{c}' phải có 1–{ReportColumns.MaxNameLength} ký tự, không chứa '|'.");
        foreach (var l in lines.Where(l => l.Code.Length == 0 || l.Code.Length > 30)) problems.Add($"Chỉ tiêu '{l.Name}': mã chỉ tiêu trống hoặc dài quá 30 ký tự.");
        foreach (var l in lines.Where(l => l.Indent is < 0 or > 6)) problems.Add($"Chỉ tiêu {l.Code}: mức thụt lề 0–6.");
        if (problems.Count == 0)
        {
            var slots = new List<IReadOnlyList<string?>>();
            foreach (var l in lines)
            {
                try { slots.Add(ReportColumns.Slots(l.Formula, columns.Count)); }
                catch (DomainException ex) { problems.Add($"Chỉ tiêu {l.Code}: {ex.Message}"); slots.Add(ReportColumns.Slots(null, columns.Count)); }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var k = 0; k < Math.Max(columns.Count, 1); k++)
                foreach (var p in ReportEvaluator.Validate(lines.Select((l, i) => (l.Code, slots[i][k])).ToList(), checks.Select(c => c.Rule).ToList()))
                    if (seen.Add(p)) problems.Add(columns.Count > 0 ? $"[{columns[k]}] {p}" : p);
        }
        if (problems.Count > 0) throw new DomainException(ErrorCodes.ReportFormulaInvalid, string.Join(" | ", problems));

        return await db.InTransactionAsync(async () =>
        {
            var latest = await db.ReportDefinitions.Include(d => d.Lines).Include(d => d.Checks)
                .Where(d => d.CompanyId == ctx.CompanyId && d.Code == code).OrderByDescending(d => d.Version).FirstOrDefaultAsync();
            ReportDefinition def;
            if (latest is { Status: ReportDefinitionStatus.Draft } && !await db.ReportSnapshots.AnyAsync(s => s.DefinitionId == latest.Id))
            {
                def = latest;
                db.RemoveRange(def.Lines);
                db.RemoveRange(def.Checks);
                await db.SaveChangesAsync();
                def.Lines.Clear();
                def.Checks.Clear();
                def.RowVersion++;
            }
            else
            {
                def = new ReportDefinition { Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, Version = (latest?.Version ?? 0) + 1, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
                db.ReportDefinitions.Add(def);
            }
            def.Name = input.Name.Trim();
            def.Category = input.Category.Trim();
            def.LegalReference = string.IsNullOrWhiteSpace(input.LegalReference) ? null : input.LegalReference.Trim();
            def.Columns = columns.Count == 0 ? null : string.Join("|", columns);
            def.CreatedBy = ctx.UserId;      // the maker of this content (the verifier must be someone else)
            def.CreatedAt = clock.UtcNow;
            // Added through the context: children with preset keys found only via a tracked parent's navigation would be taken for existing rows.
            db.AddRange(lines.Select((l, i) => new ReportLine
            {
                Id = Ids.New(), DefinitionId = def.Id, LineNo = i + 1, Code = l.Code, Name = l.Name, Formula = l.Formula, Indent = l.Indent, Bold = l.Bold, Note = l.Note,
            }));
            db.AddRange(checks.Select((c, i) => new ReportCheck { Id = Ids.New(), DefinitionId = def.Id, LineNo = i + 1, Rule = c.Rule, Message = c.Message }));
            await audit.AppendAsync(ctx.CompanyId, ctx, "report.definition.save", "report_definition", def.Id.ToString(),
                new { code, def.Version, lines = lines.Count, checks = checks.Count, Sha256 = Hash(JsonSerializer.Serialize(input, Json)) }, def.RowVersion);
            await db.SaveChangesAsync();
            return def;
        });
    }

    /// <summary>Marks a Draft as checked (against the legal form, if it claims one) by a second person; it is frozen from then on.</summary>
    public async Task VerifyAsync(SessionContext ctx, Guid id, string note)
    {
        ctx.Require(Permissions.PeriodLock);
        if (string.IsNullOrWhiteSpace(note)) throw new DomainException(ErrorCodes.ReasonRequired, "Ghi rõ nội dung xác minh (đối chiếu văn bản / mẫu nào, ai chịu trách nhiệm).");
        await db.InTransactionAsync(async () =>
        {
            var d = await db.ReportDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Mẫu báo cáo không tồn tại.");
            if (d.Status != ReportDefinitionStatus.Draft) throw new DomainException(ErrorCodes.InvalidStateTransition, "Mẫu đã được xác minh.");
            if (d.CreatedBy == ctx.UserId && await db.Companies.Where(c => c.Id == ctx.CompanyId).Select(c => c.RequireDistinctApprover).FirstAsync()) throw new DomainException(ErrorCodes.SodViolation, "Người soạn mẫu không được tự xác minh mẫu của mình.");
            d.Status = ReportDefinitionStatus.Verified;
            d.VerificationNote = note.Trim();
            d.VerifiedBy = ctx.UserId;
            d.VerifiedAt = clock.UtcNow;
            d.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "report.definition.verify", "report_definition", d.Id.ToString(), new { d.Code, d.Version, note }, d.RowVersion);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>Deletes a Draft that has no snapshot (a verified template is kept for ever).</summary>
    public async Task DiscardDraftAsync(SessionContext ctx, Guid id)
    {
        ctx.Require(Permissions.CatalogManage);
        await db.InTransactionAsync(async () =>
        {
            var d = await db.ReportDefinitions.Include(x => x.Lines).Include(x => x.Checks).FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Mẫu báo cáo không tồn tại.");
            if (d.Status != ReportDefinitionStatus.Draft || await db.ReportSnapshots.AnyAsync(s => s.DefinitionId == id))
                throw new DomainException(ErrorCodes.InvalidStateTransition, "Chỉ xóa được bản nháp chưa có kết quả đã chốt.");
            db.ReportDefinitions.Remove(d);
            await audit.AppendAsync(ctx.CompanyId, ctx, "report.definition.discard", "report_definition", d.Id.ToString(), new { d.Code, d.Version });
            await db.SaveChangesAsync();
        });
    }

    // ---------------- Running ----------------

    public async Task<ReportRun> RunAsync(SessionContext ctx, Guid id, DateOnly from, DateOnly to, DateOnly? compareFrom = null, DateOnly? compareTo = null)
    {
        ctx.Require(Permissions.ReportView);
        if (to < from) throw new DomainException(ErrorCodes.ValidationFailed, "Đến ngày phải sau từ ngày.");
        if ((compareFrom is null) != (compareTo is null) || compareTo < compareFrom)
            throw new DomainException(ErrorCodes.ValidationFailed, "Kỳ so sánh cần đủ từ ngày và đến ngày hợp lệ.");
        var d = await LoadAsync(ctx, id);
        var company = await db.Companies.Where(c => c.Id == ctx.CompanyId).Select(c => c.Name).FirstAsync();
        var lines = d.Lines.OrderBy(l => l.LineNo).ToList();
        var checks = d.Checks.OrderBy(c => c.LineNo).Select(c => (c.Rule, c.Message)).ToList();
        var columns = ReportColumns.Parse(d.Columns);
        if (columns.Count > 0) return await RunColumnsAsync(ctx, d, company, lines, checks, columns, from, to, compareFrom, compareTo);
        var defs = lines.Select(l => (l.Code, l.Formula)).ToList();
        var current = ReportEvaluator.Evaluate(defs, checks, await FactsAsync(ctx.CompanyId, from, to));
        var compare = compareFrom is null ? default : ReportEvaluator.Evaluate(defs, checks, await FactsAsync(ctx.CompanyId, compareFrom.Value, compareTo!.Value));
        var runLines = lines.Select((l, i) => new ReportRunLine(l.LineNo, l.Code, l.Name, l.Indent, l.Bold, l.Note,
            current.Lines[i].Value, compare.Lines?[i].Value, current.Lines[i].Contributions,
            current.Lines[i].Error ?? (compare.Lines?[i].Error is { } ce ? $"Kỳ so sánh: {ce}" : null))).ToList();
        var runChecks = current.Checks.Select((c, i) => new ReportRunCheck(c.Rule, c.Message, c.Left, c.Right, c.Passed, c.Error,
            compare.Checks?[i].Left, compare.Checks?[i].Right, compare.Checks?[i].Passed)).ToList();
        return new ReportRun(d.Id, d.Code, d.Version, d.Name, d.LegalReference, d.Status == ReportDefinitionStatus.Verified, company,
            from, to, compareFrom, compareTo, runLines, runChecks);
    }

    /// <summary>
    /// Multi-column template: each column is evaluated as its own report (LINE() refers to the same column); every check must
    /// hold in every column. Drill-down sources are prefixed with the column name.
    /// </summary>
    private async Task<ReportRun> RunColumnsAsync(SessionContext ctx, ReportDefinition d, string company, List<ReportLine> lines,
        List<(string Rule, string Message)> checks, IReadOnlyList<string> columns, DateOnly from, DateOnly to, DateOnly? compareFrom, DateOnly? compareTo)
    {
        var slots = lines.Select(l => ReportColumns.Slots(l.Formula, columns.Count)).ToList();
        var facts = await FactsAsync(ctx.CompanyId, from, to);
        var compareFacts = compareFrom is null ? null : await FactsAsync(ctx.CompanyId, compareFrom.Value, compareTo!.Value);
        var current = new List<(IReadOnlyList<LineResult> Lines, IReadOnlyList<CheckResult> Checks)>();
        var compare = new List<(IReadOnlyList<LineResult> Lines, IReadOnlyList<CheckResult> Checks)>();
        for (var k = 0; k < columns.Count; k++)
        {
            var defs = lines.Select((l, i) => (l.Code, slots[i][k])).ToList();
            current.Add(ReportEvaluator.Evaluate(defs, checks, facts));
            if (compareFacts is not null) compare.Add(ReportEvaluator.Evaluate(defs, checks, compareFacts));
        }
        var runLines = lines.Select((l, i) =>
        {
            var errors = Enumerable.Range(0, columns.Count)
                .Select(k => current[k].Lines[i].Error is { } e ? $"[{columns[k]}] {e}" : compare.Count > 0 && compare[k].Lines[i].Error is { } ce ? $"[{columns[k]}] Kỳ so sánh: {ce}" : null)
                .Where(e => e is not null).Distinct().ToList();
            return new ReportRunLine(l.LineNo, l.Code, l.Name, l.Indent, l.Bold, l.Note, null, null,
                Enumerable.Range(0, columns.Count).SelectMany(k => current[k].Lines[i].Contributions.Select(c => c with { Source = $"[{columns[k]}] {c.Source}" })).ToList(),
                errors.Count == 0 ? null : string.Join("; ", errors),
                Enumerable.Range(0, columns.Count).Select(k => current[k].Lines[i].Value).ToList(),
                compare.Count == 0 ? null : Enumerable.Range(0, columns.Count).Select(k => compare[k].Lines[i].Value).ToList());
        }).ToList();
        var runChecks = Enumerable.Range(0, columns.Count).SelectMany(k => current[k].Checks.Select((c, i) => new ReportRunCheck(c.Rule, $"{c.Message} [{columns[k]}]",
            c.Left, c.Right, c.Passed, c.Error, compare.Count > 0 ? compare[k].Checks[i].Left : null, compare.Count > 0 ? compare[k].Checks[i].Right : null,
            compare.Count > 0 ? compare[k].Checks[i].Passed : null))).ToList();
        return new ReportRun(d.Id, d.Code, d.Version, d.Name, d.LegalReference, d.Status == ReportDefinitionStatus.Verified, company,
            from, to, compareFrom, compareTo, runLines, runChecks, columns);
    }

    /// <summary>
    /// Per account and partner: opening (before <paramref name="fromDate"/>, plus opening-balance vouchers OPB), debit and credit within the period, and the same
    /// movements without closing vouchers (CLS) for NET_D / NET_C, on revaluation vouchers (FXR) and on reversal vouchers (REV);
    /// plus each account's report tags.
    /// </summary>
    private async Task<ReportFacts> FactsAsync(Guid companyId, DateOnly fromDate, DateOnly toDate)
    {
        var rows = await (from l in db.JournalLines
                          join e in db.JournalEntries on l.JournalEntryId equals e.Id
                          join a in db.Accounts on l.AccountId equals a.Id
                          where l.CompanyId == companyId && l.PostingDate <= toDate
                          group l by new { a.Code, l.PartnerId, Before = l.PostingDate < fromDate || e.VoucherType == Domain.Vouchers.VoucherTypes.OpeningBalance, l.Side, Closing = e.VoucherType == Domain.Vouchers.VoucherTypes.Closing,
                                           Reval = e.VoucherType == Domain.Vouchers.VoucherTypes.Revaluation, Rev = e.VoucherType == Domain.Vouchers.VoucherTypes.Reversal } into g
                          select new { g.Key.Code, g.Key.PartnerId, g.Key.Before, g.Key.Side, g.Key.Closing, g.Key.Reval, g.Key.Rev, Total = g.Sum(x => x.Amount) }).AsNoTracking().ToListAsync();
        var facts = rows.GroupBy(r => (r.Code, r.PartnerId))
            .Select(g => new AccountFact(g.Key.Code, g.Key.PartnerId,
                g.Where(r => r.Before).Sum(r => (int)r.Side * r.Total),
                g.Where(r => !r.Before && r.Side == Domain.Vouchers.Side.Debit).Sum(r => r.Total),
                g.Where(r => !r.Before && r.Side == Domain.Vouchers.Side.Credit).Sum(r => r.Total),
                g.Where(r => !r.Before && !r.Closing && r.Side == Domain.Vouchers.Side.Debit).Sum(r => r.Total),
                g.Where(r => !r.Before && !r.Closing && r.Side == Domain.Vouchers.Side.Credit).Sum(r => r.Total),
                g.Where(r => !r.Before && r.Reval).Sum(r => (int)r.Side * r.Total),
                g.Where(r => !r.Before && r.Rev && r.Side == Domain.Vouchers.Side.Debit).Sum(r => r.Total),
                g.Where(r => !r.Before && r.Rev && r.Side == Domain.Vouchers.Side.Credit).Sum(r => r.Total)))
            .ToList();
        var tags = (await db.Accounts.AsNoTracking().Where(a => a.CompanyId == companyId && a.ReportTags != null).Select(a => new { a.Code, a.ReportTags }).ToListAsync())
            .ToDictionary(a => a.Code, a => (IReadOnlySet<string>)a.ReportTags!.Split(',').ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var grossByPartner = (await db.Accounts.AsNoTracking().Where(a => a.CompanyId == companyId && a.BalanceDisplay == Domain.Catalog.BalanceDisplay.GrossByPartner)
            .Select(a => a.Code).ToListAsync()).ToHashSet(StringComparer.Ordinal);
        return new ReportFacts(facts, () =>
        {
            var lines = (from l in db.JournalLines
                         join e in db.JournalEntries on l.JournalEntryId equals e.Id
                         join a in db.Accounts on l.AccountId equals a.Id
                         where l.CompanyId == companyId && l.PostingDate >= fromDate && l.PostingDate <= toDate && e.VoucherType != Domain.Vouchers.VoucherTypes.OpeningBalance
                         select new { l.JournalEntryId, e.VoucherNumber, a.Code, l.Side, l.Amount }).AsNoTracking().ToList();
            var moves = new List<CounterMove>();
            var ambiguous = new List<AmbiguousEntry>();
            foreach (var g in lines.GroupBy(x => (x.JournalEntryId, x.VoucherNumber)))
                ReportFacts.PairEntry(g.Key.VoucherNumber, g.Select(x => (x.Code, x.Side == Domain.Vouchers.Side.Debit, x.Amount)).ToList(), moves, ambiguous);
            return (moves, ambiguous);
        }, tags, () =>
        {
            // Cash = cash accounts (CashKind) + cash equivalents (tag TDT), excluding restricted cash (tag HCSD).
            var kinds = db.Accounts.AsNoTracking().Where(a => a.CompanyId == companyId).Select(a => new { a.Code, a.CashKind }).ToDictionary(a => a.Code, a => a.CashKind, StringComparer.Ordinal);
            IReadOnlySet<string> TagsOf(string code) => tags.GetValueOrDefault(code) ?? new HashSet<string>();
            bool IsCash(string code) => !TagsOf(code).Contains("HCSD") && (kinds.GetValueOrDefault(code) != Domain.Catalog.CashKind.None || TagsOf(code).Contains("TDT"));
            var lines = (from l in db.JournalLines
                         join e in db.JournalEntries on l.JournalEntryId equals e.Id
                         join a in db.Accounts on l.AccountId equals a.Id
                         where l.CompanyId == companyId && l.PostingDate >= fromDate && l.PostingDate <= toDate && e.VoucherType != Domain.Vouchers.VoucherTypes.OpeningBalance
                         select new { l.JournalEntryId, e.VoucherNumber, e.VoucherType, a.Code, l.Side, l.Amount, l.CashFlowCode }).AsNoTracking().ToList();
            var items = new List<CashFlowItem>();
            var unclassified = new List<string>();
            // Revaluation of cash in foreign currency is never a receipt or payment: it is Mã 61 whatever the counter-account.
            foreach (var g in lines.GroupBy(x => (x.JournalEntryId, x.VoucherNumber)))
                CashFlowBuilder.AddEntry(g.Key.VoucherNumber, g.Select(x => (x.Code, x.Side == Domain.Vouchers.Side.Debit, x.Amount,
                        x.VoucherType == Domain.Vouchers.VoucherTypes.Revaluation ? "61" : (string?)x.CashFlowCode)).ToList(),
                    IsCash, TagsOf, items, unclassified);
            return (items, unclassified);
        }, () =>
        {
            var rows = (from l in db.JournalLines
                        join e in db.JournalEntries on l.JournalEntryId equals e.Id
                        join a in db.Accounts on l.AccountId equals a.Id
                        where l.CompanyId == companyId && l.PostingDate >= fromDate && l.PostingDate <= toDate && e.VoucherType != Domain.Vouchers.VoucherTypes.OpeningBalance
                        group l by new { a.Code, e.VoucherType, l.Side } into g
                        select new { g.Key.Code, g.Key.VoucherType, g.Key.Side, Total = g.Sum(x => x.Amount) }).AsNoTracking().ToList();
            return rows.GroupBy(r => (r.Code, r.VoucherType))
                .Select(g => new TypeMove(g.Key.Code, g.Key.VoucherType,
                    g.Where(r => r.Side == Domain.Vouchers.Side.Debit).Sum(r => r.Total), g.Where(r => r.Side == Domain.Vouchers.Side.Credit).Sum(r => r.Total)))
                .ToList();
        }, grossByPartner);
    }

    // ---------------- Snapshots ----------------

    /// <summary>Finalises the figures: runs the report and stores the exact result. Refused while any line or check fails.</summary>
    public async Task<ReportSnapshot> SnapshotAsync(SessionContext ctx, Guid id, DateOnly from, DateOnly to, DateOnly? compareFrom, DateOnly? compareTo, string note)
    {
        ctx.Require(Permissions.PeriodLock);
        var run = await RunAsync(ctx, id, from, to, compareFrom, compareTo);
        if (run.HasErrors) throw new DomainException(ErrorCodes.ReportFormulaInvalid, "Báo cáo còn chỉ tiêu lỗi; sửa trước khi chốt.");
        if (!run.ChecksPassed)
            throw new DomainException(ErrorCodes.ValidationFailed, "Quy tắc kiểm tra chưa đạt: " + string.Join("; ", run.Checks.Where(c => !c.Passed || c.ComparePassed == false).Select(c => c.Message)));
        var json = JsonSerializer.Serialize(run, Json);
        return await db.InTransactionAsync(async () =>
        {
            var s = new ReportSnapshot
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, DefinitionId = run.DefinitionId, DefinitionCode = run.Code, DefinitionVersion = run.Version,
                DefinitionVerified = run.Verified, FromDate = from, ToDate = to, CompareFrom = compareFrom, CompareTo = compareTo,
                ResultJson = json, Sha256 = Hash(json), Note = note?.Trim() ?? "", CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
            };
            db.ReportSnapshots.Add(s);
            await audit.AppendAsync(ctx.CompanyId, ctx, "report.snapshot", "report_snapshot", s.Id.ToString(),
                new { run.Code, run.Version, from, to, compareFrom, compareTo, s.Sha256, run.Verified });
            await db.SaveChangesAsync();
            return s;
        });
    }

    public async Task<IReadOnlyList<ReportSnapshotInfo>> SnapshotsAsync(SessionContext ctx, Guid? definitionId = null)
    {
        ctx.Require(Permissions.ReportView);
        var q = from s in db.ReportSnapshots
                join u in db.Users on s.CreatedBy equals u.Id
                where s.CompanyId == ctx.CompanyId && (definitionId == null || s.DefinitionId == definitionId)
                orderby s.CreatedAt descending
                select new ReportSnapshotInfo(s.Id, s.DefinitionCode, s.DefinitionVersion, s.DefinitionVerified, s.FromDate, s.ToDate, s.CompareFrom, s.CompareTo,
                    s.Sha256, s.Note, s.CreatedAt, u.FullName);
        return await q.AsNoTracking().ToListAsync();
    }

    /// <summary>The stored result exactly as finalised, with a fresh check that it still matches its hash.</summary>
    public async Task<(ReportRun Run, bool HashValid, ReportSnapshotInfo Info)> SnapshotAsync(SessionContext ctx, Guid snapshotId)
    {
        ctx.Require(Permissions.ReportView);
        var s = await db.ReportSnapshots.AsNoTracking().FirstOrDefaultAsync(x => x.Id == snapshotId && x.CompanyId == ctx.CompanyId)
                ?? throw new DomainException(ErrorCodes.NotFound, "Kết quả đã chốt không tồn tại.");
        var by = await db.Users.Where(u => u.Id == s.CreatedBy).Select(u => u.FullName).FirstOrDefaultAsync() ?? "";
        var run = JsonSerializer.Deserialize<ReportRun>(s.ResultJson, Json)!;
        return (run, Hash(s.ResultJson) == s.Sha256, new ReportSnapshotInfo(s.Id, s.DefinitionCode, s.DefinitionVersion, s.DefinitionVerified,
            s.FromDate, s.ToDate, s.CompareFrom, s.CompareTo, s.Sha256, s.Note, s.CreatedAt, by));
    }

    private async Task<ReportDefinition> LoadAsync(SessionContext ctx, Guid id) =>
        await db.ReportDefinitions.AsNoTracking().Include(d => d.Lines).Include(d => d.Checks).FirstOrDefaultAsync(d => d.Id == id && d.CompanyId == ctx.CompanyId)
        ?? throw new DomainException(ErrorCodes.NotFound, "Mẫu báo cáo không tồn tại.");

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
