using System.Text.Json;
using Hak.Application.Accounting;
using Hak.Application.Rules;
using Hak.Application.Settlement;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Hak.Api;

public sealed record RoleMappingRequest(string RoleCode, string AccountCode);
public sealed record RuleRequest(string Code, string DocumentType, string BusinessEvent, int Priority, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    JsonElement Definition, JsonElement TestCases, string? Description);
public sealed record DocumentLineRequest(decimal Amount, Guid? CounterAccountId, Guid? SettleJournalLineId, string? Description, DateOnly? DueDate);
public sealed record DocumentRequest(string DocumentType, string BusinessEvent, DateOnly VoucherDate, DateOnly PostingDate, string Description,
    Guid? PartnerId, Guid? MainAccountId, List<DocumentLineRequest> Lines);
public sealed record AllocateRequest(Guid LineA, Guid LineB, decimal Amount, DateOnly? Date, decimal? ForeignAmount = null);
public sealed record AutoAllocateRequest(Guid AccountId, Guid PartnerId, DateOnly Date);
public sealed record ClosingRuleSetRequest(string Code, DateOnly EffectiveFrom, DateOnly? EffectiveTo, List<ClosingStepInput> Steps, string? Description);
public sealed record PeriodRequest(Guid PeriodId);

public static class Phase2Endpoints
{
    private static BusinessDocument ToDoc(DocumentRequest r) => new(r.DocumentType, r.BusinessEvent, r.VoucherDate, r.PostingDate, r.Description,
        r.PartnerId, r.MainAccountId, r.Lines.Select(l => new BusinessDocumentLine(l.Amount, l.CounterAccountId, l.SettleJournalLineId, l.Description, l.DueDate)).ToList());

    public static void MapPhase2Endpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- posting rule engine ----
        api.MapGet("/rules/role-mappings", async (HttpContext http, HakDbContext db) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            return Results.Ok(await (from m in db.AccountRoleMappings
                                     join a in db.Accounts on m.AccountId equals a.Id
                                     where m.CompanyId == ctx.CompanyId
                                     orderby m.RoleCode
                                     select new { m.RoleCode, accountId = a.Id, accountCode = a.Code }).ToListAsync());
        });
        api.MapPost("/rules/role-mappings", async (HttpContext http, RoleMappingRequest r, PostingRuleService s) =>
        { await s.SetRoleMappingAsync(await http.SessionAsync(), r.RoleCode, r.AccountCode); return Results.NoContent(); });
        api.MapGet("/rules", async (HttpContext http, HakDbContext db) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            return Results.Ok(await db.PostingRules.Where(r => r.CompanyId == ctx.CompanyId).OrderBy(r => r.Code).ThenBy(r => r.Version)
                .Select(r => new { r.Id, r.Code, r.Version, r.DocumentType, r.BusinessEvent, r.Priority, r.EffectiveFrom, r.EffectiveTo,
                    status = r.Status.ToString(), r.DefinitionJson, r.TestCasesJson, r.LastTestRunJson, r.Description }).ToListAsync());
        });
        api.MapPost("/rules", async (HttpContext http, RuleRequest r, PostingRuleService s) =>
        {
            var rule = await s.CreateDraftAsync(await http.SessionAsync(), r.Code, r.DocumentType, r.BusinessEvent, r.Priority, r.EffectiveFrom, r.EffectiveTo,
                r.Definition.GetRawText(), r.TestCases.ValueKind == JsonValueKind.Undefined ? "[]" : r.TestCases.GetRawText(), r.Description);
            return Results.Ok(new { rule.Id, rule.Version });
        });
        api.MapPost("/rules/{id:guid}/test", async (HttpContext http, Guid id, PostingRuleService s) => Results.Ok(await s.RunTestsAsync(await http.SessionAsync(), id)));
        api.MapPost("/rules/{id:guid}/activate", async (HttpContext http, Guid id, PostingRuleService s) =>
        {
            var run = await s.ActivateAsync(await http.SessionAsync(), id);
            return run.Passed ? Results.Ok(run) : Results.UnprocessableEntity(new { code = ErrorCodes.RuleTestFailed, message = "Quy tắc chưa qua bộ test, không kích hoạt.", run });
        });
        api.MapPost("/rules/{id:guid}/retire", async (HttpContext http, Guid id, PostingRuleService s) => { await s.RetireAsync(await http.SessionAsync(), id); return Results.NoContent(); });

        // ---- cash & bank documents ----
        api.MapPost("/documents", async (HttpContext http, DocumentRequest r, BusinessDocumentService s) =>
            Results.Ok(VoucherDto.From(await s.CreateAsync(await http.SessionAsync(), ToDoc(r)))));
        api.MapPut("/documents/{id:guid}", async (HttpContext http, Guid id, long rowVersion, DocumentRequest r, BusinessDocumentService s) =>
            Results.Ok(VoucherDto.From(await s.UpdateAsync(await http.SessionAsync(), id, rowVersion, ToDoc(r)))));

        // ---- receivables / payables ----
        api.MapGet("/settlement/open-items", async (HttpContext http, Guid accountId, Guid? partnerId, DateOnly asOf, bool? includeSettled, SettlementService s) =>
            Results.Ok(await s.OpenItemsAsync(await http.SessionAsync(), accountId, partnerId, asOf, includeSettled ?? false)));
        api.MapPost("/settlement/allocate", async (HttpContext http, AllocateRequest r, SettlementService s) =>
        {
            var a = await s.AllocateAsync(await http.SessionAsync(), r.LineA, r.LineB, r.Amount, r.Date, r.ForeignAmount);
            return Results.Ok(new { a.Id, a.Amount, a.ForeignAmount, a.AllocationDate });
        });
        api.MapPost("/settlement/auto", async (HttpContext http, AutoAllocateRequest r, SettlementService s) =>
        {
            var list = await s.AutoAllocateAsync(await http.SessionAsync(), r.AccountId, r.PartnerId, r.Date);
            return Results.Ok(list.Select(a => new { a.Id, a.DebitLineId, a.CreditLineId, a.Amount }));
        });
        api.MapGet("/settlement/allocations", async (HttpContext http, Guid accountId, Guid partnerId, SettlementService s) =>
            Results.Ok(await s.AllocationsAsync(await http.SessionAsync(), accountId, partnerId)));
        api.MapPost("/settlement/{id:guid}/void", async (HttpContext http, Guid id, ReasonRequest r, SettlementService s) =>
        { await s.VoidAsync(await http.SessionAsync(), id, r.Reason); return Results.NoContent(); });
        api.MapGet("/reports/aging", async (HttpContext http, Guid accountId, DateOnly asOf, SettlementService s) =>
            Results.Ok(await s.AgingAsync(await http.SessionAsync(), accountId, asOf)));
        api.MapGet("/reports/subledger-reconciliation", async (HttpContext http, DateOnly asOf, SettlementService s) =>
        {
            var r = await s.ReconcileAsync(await http.SessionAsync(), asOf);
            return Results.Ok(new { r.AsOf, r.IsReconciled, r.Rows });
        });
        api.MapGet("/reports/account-ledger", async (HttpContext http, Guid accountId, DateOnly from, DateOnly to, Guid? partnerId, LedgerQueryService q) =>
            Results.Ok(await q.AccountLedgerAsync(await http.SessionAsync(), accountId, from, to, partnerId)));

        // ---- period-end closing ----
        api.MapPost("/closing/rule-sets", async (HttpContext http, ClosingRuleSetRequest r, ClosingService s) =>
        {
            var set = await s.CreateRuleSetAsync(await http.SessionAsync(), r.Code, r.EffectiveFrom, r.EffectiveTo, r.Steps, r.Description);
            return Results.Ok(new { set.Id, set.Version });
        });
        api.MapPost("/closing/rule-sets/{id:guid}/activate", async (HttpContext http, Guid id, ClosingService s) =>
        { await s.ActivateRuleSetAsync(await http.SessionAsync(), id); return Results.NoContent(); });
        api.MapGet("/closing/preview", async (HttpContext http, Guid periodId, ClosingService s) =>
        {
            var (set, lines) = await s.PreviewAsync(await http.SessionAsync(), periodId);
            return Results.Ok(new { ruleSet = $"{set.Code} v{set.Version}", lines });
        });
        api.MapPost("/closing/generate", async (HttpContext http, PeriodRequest r, ClosingService s) =>
            Results.Ok(VoucherDto.From(await s.GenerateAsync(await http.SessionAsync(), r.PeriodId))));
    }
}
