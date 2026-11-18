using System.Text.Json;
using Hak.Application.Accounting;
using Hak.Application.Rules;
using Hak.Application.Settlement;
using Hak.Application.Setup;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Hak.Api;

public sealed record RoleMappingRequest(string RoleCode, string AccountCode);
public sealed record RuleRequest(string Code, string DocumentType, string BusinessEvent, int Priority, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    JsonElement Definition, JsonElement TestCases, string? Description);
public sealed record DocumentLineRequest(decimal Amount, Guid? CounterAccountId, Guid? SettleJournalLineId, string? Description, DateOnly? DueDate, Guid? AnalysisCodeId = null);
public sealed record DocumentRequest(string DocumentType, string BusinessEvent, DateOnly VoucherDate, DateOnly PostingDate, string Description,
    Guid? PartnerId, Guid? MainAccountId, List<DocumentLineRequest> Lines);
public sealed record AllocateRequest(Guid LineA, Guid LineB, decimal Amount, DateOnly? Date, decimal? ForeignAmount = null);
public sealed record AutoAllocateRequest(Guid AccountId, Guid PartnerId, DateOnly Date);
public sealed record ClosingRuleSetRequest(string Code, DateOnly EffectiveFrom, DateOnly? EffectiveTo, List<ClosingStepInput> Steps, string? Description);
public sealed record PeriodRequest(Guid PeriodId);

public sealed record TemplateRequest(string Name, string VoucherType, string? Description, List<Hak.Application.Setup.TemplateLine> Lines);
public sealed record AnalysisCodeRequest(string Kind, string Code, string Name);
public sealed record AnalysisCodeUpdateRequest(string Name, bool IsActive);
public sealed record ApprovalLimitRequest(decimal? Limit);
public sealed record SegregationRequest(bool RequireDistinctApprover, string? Reason);
public sealed record CompanyAddressRequest(string? Address);
public sealed record BankInfoRequest(string? BankAccountNo, string? BankName);

public static class Phase2Endpoints
{
    private static BusinessDocument ToDoc(DocumentRequest r) => new(r.DocumentType, r.BusinessEvent, r.VoucherDate, r.PostingDate, r.Description,
        r.PartnerId, r.MainAccountId, (r.Lines ?? []).Select(l => new BusinessDocumentLine(l.Amount, l.CounterAccountId, l.SettleJournalLineId, l.Description, l.DueDate, l.AnalysisCodeId)).ToList());

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
                r.Definition.ValueKind == JsonValueKind.Undefined ? "" : r.Definition.GetRawText(), r.TestCases.ValueKind == JsonValueKind.Undefined ? "[]" : r.TestCases.GetRawText(), r.Description);
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
        // ---- voucher templates, analysis codes, approval limit ----
        api.MapGet("/voucher-templates", async (HttpContext http, WorkflowSetupService s) => Results.Ok(await s.TemplatesAsync(await http.SessionAsync())));
        api.MapPost("/voucher-templates", async (HttpContext http, TemplateRequest r, WorkflowSetupService s) =>
            Results.Ok(await s.SaveTemplateAsync(await http.SessionAsync(), r.Name, r.VoucherType, r.Description, r.Lines)));
        api.MapPost("/voucher-templates/{id:guid}/delete", async (HttpContext http, Guid id, WorkflowSetupService s) =>
        { await s.DeleteTemplateAsync(await http.SessionAsync(), id); return Results.NoContent(); });
        api.MapGet("/analysis-codes", async (HttpContext http, WorkflowSetupService s) => Results.Ok(await s.AnalysisCodesAsync(await http.SessionAsync())));
        api.MapPost("/analysis-codes", async (HttpContext http, AnalysisCodeRequest r, WorkflowSetupService s) =>
            Results.Ok(await s.CreateAnalysisCodeAsync(await http.SessionAsync(), r.Kind, r.Code, r.Name)));
        api.MapPut("/analysis-codes/{id:guid}", async (HttpContext http, Guid id, AnalysisCodeUpdateRequest r, WorkflowSetupService s) =>
        { await s.UpdateAnalysisCodeAsync(await http.SessionAsync(), id, r.Name, r.IsActive); return Results.NoContent(); });
        api.MapGet("/reports/analysis", async (HttpContext http, DateOnly from, DateOnly to, WorkflowSetupService s) =>
            Results.Ok(await s.AnalysisReportAsync(await http.SessionAsync(), from, to)));
        api.MapGet("/company/approval-limit", async (HttpContext http, WorkflowSetupService s) =>
            Results.Ok(new { limit = await s.ApprovalLimitAsync(await http.SessionAsync()) }));
        api.MapPut("/company/address", async (HttpContext http, CompanyAddressRequest r, WorkflowSetupService s) =>
        { await s.SetCompanyAddressAsync(await http.SessionAsync(), r.Address); return Results.NoContent(); });
        api.MapPut("/accounts/{id:guid}/bank-info", async (HttpContext http, Guid id, BankInfoRequest r, WorkflowSetupService s) =>
        { await s.SetBankInfoAsync(await http.SessionAsync(), id, r.BankAccountNo, r.BankName); return Results.NoContent(); });
        api.MapGet("/company/segregation", async (HttpContext http, WorkflowSetupService s) =>
            Results.Ok(new { requireDistinctApprover = await s.RequireDistinctApproverAsync(await http.SessionAsync()) }));
        api.MapPut("/company/segregation", async (HttpContext http, SegregationRequest r, WorkflowSetupService s) =>
        { await s.SetRequireDistinctApproverAsync(await http.SessionAsync(), r.RequireDistinctApprover, r.Reason); return Results.NoContent(); });
        api.MapPut("/company/approval-limit", async (HttpContext http, ApprovalLimitRequest r, WorkflowSetupService s) =>
        { await s.SetApprovalLimitAsync(await http.SessionAsync(), r.Limit); return Results.NoContent(); });

        // ---- voucher attachments: the request body is the file itself ----
        api.MapGet("/vouchers/{id:guid}/attachments", async (HttpContext http, Guid id, VoucherAttachmentService s) =>
            Results.Ok(await s.ListAsync(await http.SessionAsync(), id)));
        api.MapPost("/vouchers/{id:guid}/attachments", async (HttpContext http, Guid id, string fileName, VoucherAttachmentService s) =>
        {
            var ctx = await http.SessionAsync();
            if (http.Request.ContentLength > VoucherAttachmentService.MaxBytes)
                throw new DomainException(ErrorCodes.ValidationFailed, $"Tệp lớn hơn {VoucherAttachmentService.MaxBytes / 1024 / 1024} MB.");
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            for (int n; (n = await http.Request.Body.ReadAsync(chunk)) > 0;)
            {
                if (buffer.Length + n > VoucherAttachmentService.MaxBytes)
                    throw new DomainException(ErrorCodes.ValidationFailed, $"Tệp lớn hơn {VoucherAttachmentService.MaxBytes / 1024 / 1024} MB.");
                buffer.Write(chunk, 0, n);
            }
            return Results.Ok(await s.AddAsync(ctx, id, fileName, buffer.ToArray()));
        });
        api.MapGet("/attachments/{id:guid}", async (HttpContext http, Guid id, VoucherAttachmentService s) =>
        {
            var f = await s.DownloadAsync(await http.SessionAsync(), id);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(f.Content, f.ContentType, f.FileName);
        });
        api.MapPost("/attachments/{id:guid}/remove", async (HttpContext http, Guid id, ReasonRequest r, VoucherAttachmentService s) =>
        { await s.RemoveAsync(await http.SessionAsync(), id, r.Reason); return Results.NoContent(); });

        // ---- overviews (read-only) ----
        api.MapGet("/dashboard", async (HttpContext http, DateOnly? asOf, Hak.Application.Overview.OverviewService s, IClock clock) =>
            Results.Ok(await s.DashboardAsync(await http.SessionAsync(), asOf ?? DateOnly.FromDateTime(clock.UtcNow.ToLocalTime()))));
        api.MapGet("/integrity", async (HttpContext http, DateOnly? asOf, Hak.Application.Overview.IntegrityService s, IClock clock) =>
            Results.Ok(await s.RunAsync(await http.SessionAsync(), asOf ?? DateOnly.FromDateTime(clock.UtcNow))));
        api.MapGet("/closing/checklist", async (HttpContext http, Guid periodId, Hak.Application.Overview.OverviewService s) =>
        {
            var c = await s.ClosingChecklistAsync(await http.SessionAsync(), periodId);
            return Results.Ok(new { c.Period, c.ReadyToLock, c.Items });
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
