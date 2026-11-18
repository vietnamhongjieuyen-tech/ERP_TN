using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Rules;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Rules;

/// <summary>A business document before it becomes a voucher (phiếu thu/chi, báo Nợ/Có...).</summary>
public sealed record BusinessDocument(
    string DocumentType, string BusinessEvent, DateOnly VoucherDate, DateOnly PostingDate, string Description,
    Guid? PartnerId, Guid? MainAccountId, IReadOnlyList<BusinessDocumentLine> Lines,
    string? SourceSystem = null, string? SourceId = null);

public sealed record BusinessDocumentLine(decimal Amount, Guid? CounterAccountId = null, Guid? SettleJournalLineId = null,
    string? Description = null, DateOnly? DueDate = null, Guid? AnalysisCodeId = null);

public sealed class RuleDefinition
{
    [JsonPropertyName("lines")] public List<RuleLineTemplate> Lines { get; set; } = [];
}

/// <summary>
/// One template applied to every document line. Expressions:
/// account  — "ROLE:&lt;code&gt;" | "DOC:MAIN_ACCOUNT" | "LINE:COUNTER_ACCOUNT", alternatives separated by "|";
/// partner  — "DOC:PARTNER" or null; settle — "LINE:SETTLE" or null; due_date — "LINE:DUE_DATE" or null;
/// constraint — null | "CASH_EQUIVALENT" | "NOT_CASH_EQUIVALENT" (checked on the resolved account).
/// </summary>
public sealed class RuleLineTemplate
{
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("partner")] public string? Partner { get; set; }
    [JsonPropertyName("settle")] public string? Settle { get; set; }
    [JsonPropertyName("due_date")] public string? DueDate { get; set; }
    [JsonPropertyName("constraint")] public string? Constraint { get; set; }
}

public sealed class RuleTestCase
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("document")] public RuleTestDocument Document { get; set; } = new();
    [JsonPropertyName("expected")] public List<RuleTestExpectedLine> Expected { get; set; } = [];
    /// <summary>When set, the case passes only if evaluation fails with this error code (negative test).</summary>
    [JsonPropertyName("expect_error")] public string? ExpectError { get; set; }
}

public sealed class RuleTestDocument
{
    [JsonPropertyName("partner_code")] public string? PartnerCode { get; set; }
    [JsonPropertyName("main_account_code")] public string? MainAccountCode { get; set; }
    [JsonPropertyName("lines")] public List<RuleTestDocLine> Lines { get; set; } = [];
}

public sealed class RuleTestDocLine
{
    [JsonPropertyName("amount")] public string Amount { get; set; } = "0";
    [JsonPropertyName("counter_account_code")] public string? CounterAccountCode { get; set; }
}

public sealed class RuleTestExpectedLine
{
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("account_code")] public string AccountCode { get; set; } = "";
    [JsonPropertyName("amount")] public string Amount { get; set; } = "0";
    [JsonPropertyName("partner_code")] public string? PartnerCode { get; set; }
}

public sealed record RuleTestCaseResult(string Name, bool Passed, string? Detail);
public sealed record RuleTestRun(Guid RuleId, int Version, bool Passed, DateTime RanAt, IReadOnlyList<RuleTestCaseResult> Cases);

/// <summary>Accounting Rule Engine (master prompt §25, docs/03 §7).</summary>
public sealed class PostingRuleService(HakDbContext db, AuditService audit, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    public async Task SetRoleMappingAsync(SessionContext ctx, string roleCode, string accountCode)
    {
        ctx.Require(Permissions.ConfigManage);
        await db.InTransactionAsync(async () =>
        {
            var acc = await db.Accounts.FirstOrDefaultAsync(a => a.CompanyId == ctx.CompanyId && a.Code == accountCode)
                      ?? throw new DomainException(ErrorCodes.AccountNotFound, $"Tài khoản {accountCode} không tồn tại.");
            if (!acc.IsPostable) throw new DomainException(ErrorCodes.AccountNotPostable, $"Tài khoản {accountCode} không phải tài khoản chi tiết.");
            var m = await db.AccountRoleMappings.FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.RoleCode == roleCode);
            var old = m?.AccountId;
            if (m is null)
                db.AccountRoleMappings.Add(m = new AccountRoleMapping { Id = Ids.New(), CompanyId = ctx.CompanyId, RoleCode = roleCode, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId });
            m.AccountId = acc.Id;
            m.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "rule.role_mapping", "account_role_mapping", roleCode, new { accountCode, oldAccountId = old });
            await db.SaveChangesAsync();
        });
    }

    public async Task<PostingRule> CreateDraftAsync(SessionContext ctx, string code, string documentType, string businessEvent, int priority,
        DateOnly effectiveFrom, DateOnly? effectiveTo, string definitionJson, string testCasesJson, string? description = null)
    {
        ctx.Require(Permissions.ConfigManage);
        if (!VoucherTypes.RuleDriven.Contains(documentType))
            throw new DomainException(ErrorCodes.RuleInvalid, $"Loại chứng từ {documentType} không dùng quy tắc định khoản.");
        ParseDefinition(definitionJson);
        ParseTests(testCasesJson);
        return await db.InTransactionAsync(async () =>
        {
            var version = (await db.PostingRules.Where(r => r.CompanyId == ctx.CompanyId && r.Code == code).MaxAsync(r => (int?)r.Version) ?? 0) + 1;
            var rule = new PostingRule
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, Version = version, DocumentType = documentType, BusinessEvent = businessEvent,
                Priority = priority, EffectiveFrom = effectiveFrom, EffectiveTo = effectiveTo, DefinitionJson = definitionJson,
                TestCasesJson = testCasesJson, Description = description, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId, RowVersion = 1,
            };
            db.PostingRules.Add(rule);
            await audit.AppendAsync(ctx.CompanyId, ctx, "rule.create", "posting_rule", rule.Id.ToString(), new { code, version, documentType, businessEvent, definitionJson });
            await db.SaveChangesAsync();
            return rule;
        });
    }

    /// <summary>Runs the rule's test cases against the company's current catalog and role mappings (no writes).</summary>
    public async Task<RuleTestRun> RunTestsAsync(SessionContext ctx, Guid ruleId)
    {
        ctx.Require(Permissions.ConfigManage);
        var rule = await db.PostingRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == ctx.CompanyId)
                   ?? throw new DomainException(ErrorCodes.NotFound, "Quy tắc không tồn tại.");
        return await RunTestsCoreAsync(rule);
    }

    /// <summary>
    /// Activates a Draft rule only when it has at least one test case and all pass. The previously active
    /// version of the same code is retired in the same transaction. The run result is stored either way.
    /// </summary>
    public async Task<RuleTestRun> ActivateAsync(SessionContext ctx, Guid ruleId)
    {
        ctx.Require(Permissions.ConfigManage);
        return await db.InTransactionAsync(async () =>
        {
            var rule = await db.PostingRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == ctx.CompanyId)
                       ?? throw new DomainException(ErrorCodes.NotFound, "Quy tắc không tồn tại.");
            if (rule.Status != RuleStatus.Draft) throw new DomainException(ErrorCodes.InvalidStateTransition, "Chỉ kích hoạt được quy tắc ở trạng thái Draft.");
            var run = await RunTestsCoreAsync(rule);
            rule.LastTestRunJson = JsonSerializer.Serialize(run);
            if (run.Passed)
            {
                foreach (var old in await db.PostingRules.Where(r => r.CompanyId == ctx.CompanyId && r.Code == rule.Code && r.Status == RuleStatus.Active).ToListAsync())
                {
                    old.Status = RuleStatus.Retired;
                    old.RowVersion++;
                }
                rule.Status = RuleStatus.Active;
                rule.ActivatedAt = clock.UtcNow;
                rule.ActivatedBy = ctx.UserId;
            }
            rule.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "rule.activate", "posting_rule", rule.Id.ToString(),
                new { rule.Code, rule.Version, run.Passed, failed = run.Cases.Where(c => !c.Passed).Select(c => c.Name) }, result: run.Passed ? "OK" : "TEST_FAILED");
            await db.SaveChangesAsync();
            return run;
        });
    }

    public async Task RetireAsync(SessionContext ctx, Guid ruleId)
    {
        ctx.Require(Permissions.ConfigManage);
        await db.InTransactionAsync(async () =>
        {
            var rule = await db.PostingRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == ctx.CompanyId)
                       ?? throw new DomainException(ErrorCodes.NotFound, "Quy tắc không tồn tại.");
            rule.Status = RuleStatus.Retired;
            rule.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "rule.retire", "posting_rule", rule.Id.ToString(), new { rule.Code, rule.Version });
            await db.SaveChangesAsync();
        });
    }

    /// <summary>Selects the single active rule for (type, event) effective on the date; none or a tie is an error.</summary>
    public async Task<PostingRule> ResolveAsync(Guid companyId, string documentType, string businessEvent, DateOnly date)
    {
        var candidates = await db.PostingRules.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.DocumentType == documentType && r.BusinessEvent == businessEvent && r.Status == RuleStatus.Active
                        && r.EffectiveFrom <= date && (r.EffectiveTo == null || r.EffectiveTo >= date))
            .ToListAsync();
        if (candidates.Count == 0)
            throw new DomainException(ErrorCodes.RuleMissing, $"Chưa có quy tắc định khoản đang hiệu lực cho {documentType}/{businessEvent} ngày {date:dd/MM/yyyy}.");
        var top = candidates.Max(r => r.Priority);
        var best = candidates.Where(r => r.Priority == top).ToList();
        if (best.Count > 1)
            throw new DomainException(ErrorCodes.RuleAmbiguous, $"Có {best.Count} quy tắc cùng độ ưu tiên cho {documentType}/{businessEvent}: {string.Join(", ", best.Select(r => $"{r.Code} v{r.Version}"))}.");
        return best[0];
    }

    /// <summary>Applies a rule to a document. Pure apart from reading role mappings and accounts.</summary>
    public async Task<List<VoucherLineInput>> EvaluateAsync(PostingRule rule, BusinessDocument doc)
    {
        var def = ParseDefinition(rule.DefinitionJson);
        var roles = await db.AccountRoleMappings.AsNoTracking().Where(m => m.CompanyId == rule.CompanyId).ToDictionaryAsync(m => m.RoleCode, m => m.AccountId);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == rule.CompanyId).ToDictionaryAsync(a => a.Id);


        var result = new List<VoucherLineInput>();
        foreach (var line in doc.Lines)
        {
            foreach (var t in def.Lines)
            {
                var accountId = ResolveAccount(t.Account, doc, line, roles);
                if (t.Constraint is not null)
                {
                    var isCash = accounts.TryGetValue(accountId, out var acc) && acc.CashKind != CashKind.None;
                    if ((t.Constraint == "CASH_EQUIVALENT" && !isCash) || (t.Constraint == "NOT_CASH_EQUIVALENT" && isCash))
                        throw new DomainException(ErrorCodes.RuleConstraint,
                            $"Tài khoản {(acc?.Code ?? accountId.ToString())} không thỏa ràng buộc {t.Constraint} của quy tắc {rule.Code} v{rule.Version}.");
                }
                result.Add(new VoucherLineInput(accountId, t.Side == "D" ? Side.Debit : Side.Credit, line.Amount,
                    t.Partner == "DOC:PARTNER" ? doc.PartnerId : null, Description: line.Description,
                    DueDate: t.DueDate == "LINE:DUE_DATE" ? line.DueDate : null,
                    SettleJournalLineId: t.Settle == "LINE:SETTLE" ? line.SettleJournalLineId : null,
                    // Mã phân tích of the document line goes on the non-cash side (the expense, income or partner line), not on the cash account.
                    AnalysisCodeId: accounts.TryGetValue(accountId, out var target) && target.CashKind == CashKind.None ? line.AnalysisCodeId : null));
            }
        }
        return result;
    }

    private static Guid ResolveAccount(string expr, BusinessDocument doc, BusinessDocumentLine line, IReadOnlyDictionary<string, Guid> roles)
    {
        foreach (var alt in expr.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (alt == "DOC:MAIN_ACCOUNT" && doc.MainAccountId is { } m) return m;
            if (alt == "LINE:COUNTER_ACCOUNT" && line.CounterAccountId is { } c) return c;
            if (alt.StartsWith("ROLE:", StringComparison.Ordinal) && roles.TryGetValue(alt[5..], out var r)) return r;
        }
        var roleAlts = expr.Split('|').Where(a => a.Trim().StartsWith("ROLE:", StringComparison.Ordinal)).ToList();
        throw new DomainException(roleAlts.Count > 0 ? ErrorCodes.RuleRoleUnmapped : ErrorCodes.RuleInvalid,
            $"Không xác định được tài khoản cho biểu thức '{expr}' (thiếu dữ liệu chứng từ hoặc vai trò tài khoản chưa ánh xạ).");
    }

    public static RuleDefinition ParseDefinition(string json)
    {
        RuleDefinition? def;
        try { def = JsonSerializer.Deserialize<RuleDefinition>(json, Json); }
        catch (JsonException ex) { throw new DomainException(ErrorCodes.RuleInvalid, $"Định nghĩa quy tắc không phải JSON hợp lệ: {ex.Message}"); }
        if (def is null || def.Lines.Count == 0) throw new DomainException(ErrorCodes.RuleInvalid, "Quy tắc phải có ít nhất một dòng mẫu.");
        if (def.Lines.Any(l => l.Side is not ("D" or "C") || string.IsNullOrWhiteSpace(l.Account)))
            throw new DomainException(ErrorCodes.RuleInvalid, "Mỗi dòng mẫu phải có side D/C và biểu thức tài khoản.");
        if (def.Lines.Count(l => l.Side == "D") != def.Lines.Count(l => l.Side == "C"))
            throw new DomainException(ErrorCodes.RuleInvalid, "Số dòng mẫu Nợ phải bằng số dòng mẫu Có để bút toán luôn cân.");
        var allowed = new[] { "DOC:MAIN_ACCOUNT", "LINE:COUNTER_ACCOUNT" };
        foreach (var l in def.Lines)
        {
            if (l.Account.Split('|').Select(a => a.Trim()).Any(a => !allowed.Contains(a) && !(a.StartsWith("ROLE:", StringComparison.Ordinal) && a.Length > 5)))
                throw new DomainException(ErrorCodes.RuleInvalid, $"Biểu thức tài khoản không hợp lệ: '{l.Account}'.");
            if (l.Partner is not (null or "DOC:PARTNER") || l.Settle is not (null or "LINE:SETTLE") || l.DueDate is not (null or "LINE:DUE_DATE")
                || l.Constraint is not (null or "CASH_EQUIVALENT" or "NOT_CASH_EQUIVALENT"))
                throw new DomainException(ErrorCodes.RuleInvalid, "Biểu thức partner/settle/due_date/constraint không hợp lệ.");
        }
        return def;
    }

    private static List<RuleTestCase> ParseTests(string json)
    {
        try { return JsonSerializer.Deserialize<List<RuleTestCase>>(json, Json) ?? []; }
        catch (JsonException ex) { throw new DomainException(ErrorCodes.RuleInvalid, $"Bộ test của quy tắc không phải JSON hợp lệ: {ex.Message}"); }
    }

    private async Task<RuleTestRun> RunTestsCoreAsync(PostingRule rule)
    {
        var cases = ParseTests(rule.TestCasesJson);
        var results = new List<RuleTestCaseResult>();
        if (cases.Count == 0) results.Add(new("(không có test)", false, "Quy tắc phải có ít nhất một test case trước khi kích hoạt."));

        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == rule.CompanyId).ToListAsync();
        var byCode = accounts.ToDictionary(a => a.Code);
        var byId = accounts.ToDictionary(a => a.Id);
        var partners = await db.BusinessPartners.AsNoTracking().Where(p => p.CompanyId == rule.CompanyId).ToListAsync();
        var pByCode = partners.ToDictionary(p => p.Code);
        var pById = partners.ToDictionary(p => p.Id);

        foreach (var tc in cases)
        {
            try
            {
                Guid? Acc(string? code) => code is null ? null : byCode.TryGetValue(code, out var a) ? a.Id : throw new DomainException(ErrorCodes.AccountNotFound, $"TK {code} không có.");
                Guid? Par(string? code) => code is null ? null : pByCode.TryGetValue(code, out var p) ? p.Id : throw new DomainException(ErrorCodes.NotFound, $"Đối tượng {code} không có.");
                var doc = new BusinessDocument(rule.DocumentType, rule.BusinessEvent, rule.EffectiveFrom, rule.EffectiveFrom, tc.Name,
                    Par(tc.Document.PartnerCode), Acc(tc.Document.MainAccountCode),
                    tc.Document.Lines.Select(l => new BusinessDocumentLine(decimal.Parse(l.Amount, CultureInfo.InvariantCulture), Acc(l.CounterAccountCode))).ToList());
                var lines = await EvaluateAsync(rule, doc);
                if (tc.ExpectError is not null) { results.Add(new(tc.Name, false, $"Mong đợi lỗi {tc.ExpectError} nhưng quy tắc chạy thành công.")); continue; }

                var actual = lines.Select(l => $"{(l.Side == Side.Debit ? "D" : "C")}|{byId[l.AccountId].Code}|{l.Amount.ToString(CultureInfo.InvariantCulture)}|{(l.PartnerId is { } p ? pById[p].Code : "")}")
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                var expected = tc.Expected.Select(e => $"{e.Side}|{e.AccountCode}|{decimal.Parse(e.Amount, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}|{e.PartnerCode ?? ""}")
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                var ok = actual.SequenceEqual(expected);
                results.Add(new(tc.Name, ok, ok ? null : $"Kỳ vọng [{string.Join("; ", expected)}] nhưng nhận [{string.Join("; ", actual)}]"));
            }
            catch (DomainException ex)
            {
                var ok = tc.ExpectError == ex.Code;
                results.Add(new(tc.Name, ok, ok ? null : $"{ex.Code}: {ex.Message}"));
            }
        }
        return new RuleTestRun(rule.Id, rule.Version, results.All(r => r.Passed), clock.UtcNow, results);
    }
}
