using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Exchange;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Integration;
using Hak.Domain.Organization;
using Hak.Domain.Payroll;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Json.Schema;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Payroll;

public sealed record PayrollTotals(int Employees, decimal Gross, decimal SocialInsurance, decimal HealthInsurance, decimal UnemploymentInsurance,
    decimal PersonalIncomeTax, decimal OtherDeduction, decimal Net, decimal EmployerContributions);

/// <summary>Outcome: VALID (dry run), CREATED, DUPLICATE (same version already imported), CONFLICT (another version still in effect), INVALID.</summary>
public sealed record PayrollImportResult(string PackageId, string SourceId, string SourceVersion, string Mode, string Outcome, PayrollTotals? Totals,
    Guid? VoucherId, string? VoucherNumber, IReadOnlyList<ImportError> Errors, IReadOnlyList<VoucherLineInput> PreviewLines);

public sealed record PayrollSummaryRow(Guid EmployeeId, string EmployeeCode, string EmployeeName, decimal Gross, decimal SocialInsurance, decimal HealthInsurance,
    decimal UnemploymentInsurance, decimal PersonalIncomeTax, decimal OtherDeduction, decimal Net, decimal EmployerContributions);

public sealed record PayrollSheetRow(Guid Id, string SourceId, string SourceVersion, DateOnly PeriodFrom, DateOnly PeriodTo, DateOnly PostingDate, string Description,
    string VoucherNumber, string VoucherStatus, int Employees, decimal Gross, decimal Net);

/// <summary>
/// Payroll import (master prompt: PAY, JSON exchange). The payroll system computes every amount; HAK checks the package
/// (signature, checksum, schema, replay), each employee (in the catalog with the Employee role, gross − deductions = net),
/// and books one Draft voucher through account roles — never legal account codes, never statutory rates [CẦN XÁC MINH]:
/// Dr PAYROLL_EXPENSE_&lt;GROUP&gt; (gross + employer contributions) / Cr PAYROLL_PAYABLE per employee (net) /
/// Cr PAYROLL_SI_PAYABLE, PAYROLL_HI_PAYABLE, PAYROLL_UI_PAYABLE (employee + employer parts), PAYROLL_PIT_PAYABLE,
/// PAYROLL_OTHER_DEDUCTION, PAYROLL_UNION_PAYABLE. A group without its own role falls back to PAYROLL_EXPENSE_GENERAL.
/// </summary>
public sealed class PayrollService(HakDbContext db, AccountingEngine engine, AuditService audit, IClock clock)
{
    public async Task<PayrollImportResult> ProcessAsync(SessionContext ctx, string json, bool commit)
    {
        ctx.Require(Permissions.DataImport);
        if (commit) ctx.Require(Permissions.VoucherCreate);
        JsonObject package;
        try { package = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("empty"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { throw new DomainException(ErrorCodes.PackageSchemaInvalid, $"Không đọc được JSON: {ex.Message}"); }
        ValidateSchema(json);

        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
        if ((string?)package["company_code"] != company.Code)
            throw new DomainException(ErrorCodes.Forbidden, "Gói dữ liệu thuộc công ty khác với phiên làm việc.");
        var sourceSystem = (string)package["source_system"]!;
        var packageId = (string)package["package_id"]!;
        var sourceId = (string)package["source_id"]!;
        var sourceVersion = (string)package["source_version"]!;
        var keyId = (string)package["auth"]!["key_id"]!;
        var key = await db.ExchangePartnerKeys.AsNoTracking().FirstOrDefaultAsync(k =>
                      k.CompanyId == ctx.CompanyId && k.SourceSystem == sourceSystem && k.KeyId == keyId && k.IsActive)
                  ?? throw new DomainException(ErrorCodes.UnknownSource, $"Không có khóa xác thực cho nguồn {sourceSystem}/{keyId}.");
        var expected = Convert.FromBase64String(PackageCrypto.Signature(package, Exchange.SecretProtector.Unprotect(key.SecretBase64)));
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String((string)package["auth"]!["signature"]!)))
            throw new DomainException(ErrorCodes.PackageSignatureInvalid, "Chữ ký gói dữ liệu không hợp lệ.");
        if (PackageCrypto.PayloadChecksum(package["payload"]!) != (string?)package["checksum"]!["value"])
            throw new DomainException(ErrorCodes.PackageChecksumMismatch, "Checksum payload không khớp.");
        if (await db.ImportBatches.AnyAsync(b => b.CompanyId == ctx.CompanyId && b.PackageId == packageId))
            throw new DomainException(ErrorCodes.PackageReplayed, $"Gói {packageId} đã được nhập trước đây.");

        PayrollImportResult Result(string outcome, PayrollTotals? totals = null, IReadOnlyList<ImportError>? errors = null, Guid? voucherId = null,
            string? voucherNumber = null, IReadOnlyList<VoucherLineInput>? lines = null)
            => new(packageId, sourceId, sourceVersion, commit ? "COMMIT" : "DRY_RUN", outcome, totals, voucherId, voucherNumber, errors ?? [], lines ?? []);

        // An earlier version still in effect (voucher not cancelled / reversed) blocks a new one: no silent overwrite.
        var previous = await (from s in db.PayrollSheets
                              join v in db.Vouchers on s.VoucherId equals v.Id
                              where s.CompanyId == ctx.CompanyId && s.SourceSystem == sourceSystem && s.SourceId == sourceId
                                    && v.Status != VoucherStatus.Cancelled && v.Status != VoucherStatus.Reversed
                              select new { s.SourceVersion, v.Id, v.Number, v.Status }).AsNoTracking().FirstOrDefaultAsync();
        if (previous is not null)
            return previous.SourceVersion == sourceVersion
                ? Result("DUPLICATE", voucherId: previous.Id, voucherNumber: previous.Number)
                : Result("CONFLICT", errors: [new(ErrorCodes.DuplicateSource,
                    $"Bảng lương {sourceId} phiên bản {previous.SourceVersion} đang có hiệu lực (chứng từ {previous.Number}, {previous.Status}); hủy/đảo chứng từ đó trước khi nhận phiên bản {sourceVersion}.")],
                    voucherId: previous.Id, voucherNumber: previous.Number);

        var (sheet, errors) = await BuildSheetAsync(ctx, package, sourceSystem, sourceId, sourceVersion, packageId);
        List<VoucherLineInput> lines = [];
        if (errors.Count == 0)
        {
            try { lines = await VoucherLinesAsync(ctx.CompanyId, sheet); }
            catch (DomainException ex) { errors.Add(new(ex.Code, ex.Message)); }
        }
        var totals = Totals(sheet.Lines);
        if (errors.Count > 0)
        {
            if (commit)
                await db.InTransactionAsync(async () =>
                {
                    await audit.AppendAsync(ctx.CompanyId, ctx, "payroll.reject", "import_package", packageId, new { sourceSystem, sourceId, errors = errors.Count }, result: "REJECTED");
                    await db.SaveChangesAsync();
                });
            return Result("INVALID", totals, errors, lines: lines);
        }
        if (!commit) return Result("VALID", totals, lines: lines);

        return await db.InTransactionAsync(async () =>
        {
            var v = await engine.CreateDraftAsync(ctx, new VoucherInput(VoucherTypes.General, sheet.PostingDate, sheet.PostingDate,
                $"{sheet.Description} ({sheet.PeriodFrom:dd/MM}–{sheet.PeriodTo:dd/MM/yyyy})", lines,
                SourceSystem: sourceSystem, SourceId: $"{sourceId}@{sourceVersion}", SourceVersion: sourceVersion));
            sheet.VoucherId = v.Id;
            db.PayrollSheets.Add(sheet);
            var result = Result("CREATED", totals, voucherId: v.Id, voucherNumber: v.Number, lines: lines);
            db.ImportBatches.Add(new ImportBatch
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, PackageId = packageId, SourceSystem = sourceSystem, DocumentType = "payroll",
                SchemaVersion = "1.0", Checksum = (string)package["checksum"]!["value"]!, Status = ImportBatchStatus.Committed,
                ItemCount = sheet.Lines.Count, CreatedCount = 1, DuplicateCount = 0, ResultJson = JsonSerializer.Serialize(new { result.Outcome, totals, v.Number }),
                ReceivedAt = clock.UtcNow, ReceivedBy = ctx.UserId,
            });
            await audit.AppendAsync(ctx.CompanyId, ctx, "payroll.import", "payroll_sheet", sheet.Id.ToString(),
                new { packageId, sourceSystem, sourceId, sourceVersion, voucher = v.Number, totals });
            await db.SaveChangesAsync();
            return result;
        });
    }

    public async Task<IReadOnlyList<PayrollSheetRow>> SheetsAsync(SessionContext ctx, DateOnly fromDate, DateOnly toDate)
    {
        ctx.Require(Permissions.ReportView);
        var rows = await (from s in db.PayrollSheets.Include(x => x.Lines)
                          join v in db.Vouchers on s.VoucherId equals v.Id
                          where s.CompanyId == ctx.CompanyId && s.PostingDate >= fromDate && s.PostingDate <= toDate
                          orderby s.PostingDate, s.SourceId
                          select new { s, v.Number, v.Status }).AsNoTracking().ToListAsync();
        return rows.Select(r => new PayrollSheetRow(r.s.Id, r.s.SourceId, r.s.SourceVersion, r.s.PeriodFrom, r.s.PeriodTo, r.s.PostingDate, r.s.Description,
            r.Number, r.Status.ToString(), r.s.Lines.Count, r.s.Lines.Sum(l => l.Gross), r.s.Lines.Sum(l => l.Net))).ToList();
    }

    /// <summary>Per-employee totals of posted payroll sheets in the range (thu nhập, khấu trừ, thuế TNCN đã khấu trừ).</summary>
    public async Task<IReadOnlyList<PayrollSummaryRow>> SummaryAsync(SessionContext ctx, DateOnly fromDate, DateOnly toDate)
    {
        ctx.Require(Permissions.ReportView);
        var lines = await (from l in db.PayrollSheetLines
                           join s in db.PayrollSheets on l.SheetId equals s.Id
                           join v in db.Vouchers on s.VoucherId equals v.Id
                           where s.CompanyId == ctx.CompanyId && v.Status == VoucherStatus.Posted && s.PostingDate >= fromDate && s.PostingDate <= toDate
                           select l).AsNoTracking().ToListAsync();
        var partners = await db.BusinessPartners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).ToDictionaryAsync(p => p.Id);
        return lines.GroupBy(l => l.EmployeeId).Select(g => new PayrollSummaryRow(g.Key, partners[g.Key].Code, partners[g.Key].Name,
                g.Sum(l => l.Gross), g.Sum(l => l.SocialInsurance), g.Sum(l => l.HealthInsurance), g.Sum(l => l.UnemploymentInsurance),
                g.Sum(l => l.PersonalIncomeTax), g.Sum(l => l.OtherDeduction), g.Sum(l => l.Net), g.Sum(l => l.EmployerContributions)))
            .OrderBy(r => r.EmployeeCode, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(PayrollSheet Sheet, List<ImportError> Errors)> BuildSheetAsync(SessionContext ctx, JsonObject package, string sourceSystem,
        string sourceId, string sourceVersion, string packageId)
    {
        var payload = package["payload"]!.AsObject();
        var errors = new List<ImportError>();
        DateOnly Date(string field)
        {
            if (DateOnly.TryParseExact((string)payload[field]!, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            errors.Add(new(ErrorCodes.ValidationFailed, $"Ngày {field} không hợp lệ."));
            return default;
        }
        var sheet = new PayrollSheet
        {
            Id = Ids.New(), CompanyId = ctx.CompanyId, SourceSystem = sourceSystem, SourceId = sourceId, SourceVersion = sourceVersion, PackageId = packageId,
            PeriodFrom = Date("period_from"), PeriodTo = Date("period_to"), PostingDate = Date("posting_date"),
            Description = ((string)payload["description"]!).Trim(), CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
        };
        if (errors.Count == 0 && sheet.PeriodTo < sheet.PeriodFrom) errors.Add(new(ErrorCodes.ValidationFailed, "Kỳ lương: ngày kết thúc trước ngày bắt đầu."));
        if (errors.Count == 0)
        {
            var period = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == ctx.CompanyId && p.StartDate <= sheet.PostingDate && p.EndDate >= sheet.PostingDate);
            if (period is null) errors.Add(new(ErrorCodes.PeriodNotFound, $"Không có kỳ kế toán chứa ngày {sheet.PostingDate}."));
            else if (period.Status == PeriodStatus.Locked) errors.Add(new(ErrorCodes.PeriodLocked, $"Kỳ {period.StartDate:MM/yyyy} đã khóa."));
        }

        var partners = await db.BusinessPartners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).ToDictionaryAsync(p => p.Code);
        var functional = await db.Companies.Where(x => x.Id == ctx.CompanyId).Select(x => x.FunctionalCurrency).FirstAsync();
        var dp = await db.Currencies.Where(c => c.Code == functional).Select(c => c.DecimalPlaces).FirstAsync();
        var seen = new HashSet<string>();
        var n = 0;
        foreach (var node in payload["employees"]!.AsArray())
        {
            n++;
            var e = node!.AsObject();
            var code = (string)e["employee_code"]!;
            if (!seen.Add(code)) { errors.Add(new(ErrorCodes.DuplicateSource, $"Nhân viên {code} lặp lại trong bảng lương.", n)); continue; }
            if (!partners.TryGetValue(code, out var p)) { errors.Add(new(ErrorCodes.PartnerWrongCompany, $"Nhân viên {code} chưa có trong danh mục đối tượng.", n)); continue; }
            if (!p.Roles.HasFlag(PartnerRoles.Employee) || !p.IsActive)
            { errors.Add(new(ErrorCodes.ValidationFailed, $"Đối tượng {code} không phải nhân viên đang hoạt động.", n)); continue; }
            decimal Amt(JsonNode? parent, string field) => parent?[field] is { } v ? decimal.Parse((string)v!, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : 0m;
            var d = e["deductions"];
            var r = e["employer"];
            var line = new PayrollSheetLine
            {
                Id = Ids.New(), SheetId = sheet.Id, CompanyId = ctx.CompanyId, LineNo = n, EmployeeId = p.Id,
                ExpenseGroup = (string)e["expense_group"]! switch { "PRODUCTION" => PayrollExpenseGroup.Production, "SELLING" => PayrollExpenseGroup.Selling, "ADMIN" => PayrollExpenseGroup.Admin, _ => PayrollExpenseGroup.General },
                Gross = Amt(e, "gross"), Net = Amt(e, "net"),
                SocialInsurance = Amt(d, "social_insurance"), HealthInsurance = Amt(d, "health_insurance"), UnemploymentInsurance = Amt(d, "unemployment_insurance"),
                PersonalIncomeTax = Amt(d, "personal_income_tax"), OtherDeduction = Amt(d, "other"),
                EmployerSocialInsurance = Amt(r, "social_insurance"), EmployerHealthInsurance = Amt(r, "health_insurance"),
                EmployerUnemploymentInsurance = Amt(r, "unemployment_insurance"), EmployerUnionFee = Amt(r, "union_fee"),
            };
            var amounts = new[] { line.Gross, line.Net, line.EmployeeDeductions, line.EmployerContributions };
            if (amounts.Any(a => !MoneyMath.HasAtMostDecimals(a, dp)))
                errors.Add(new(ErrorCodes.AmountPrecision, $"Nhân viên {code}: số tiền vượt quá {dp} chữ số thập phân.", n));
            else if (line.Gross - line.EmployeeDeductions != line.Net)
                errors.Add(new(ErrorCodes.ValidationFailed, $"Nhân viên {code}: lương gộp {line.Gross} − khấu trừ {line.EmployeeDeductions} ≠ thực lĩnh {line.Net}.", n));
            else sheet.Lines.Add(line);
        }
        return (sheet, errors);
    }

    private async Task<List<VoucherLineInput>> VoucherLinesAsync(Guid companyId, PayrollSheet sheet)
    {
        var roles = await db.AccountRoleMappings.AsNoTracking().Where(m => m.CompanyId == companyId && m.RoleCode.StartsWith("PAYROLL_"))
            .ToDictionaryAsync(m => m.RoleCode, m => m.AccountId);
        Guid Role(string code) => roles.TryGetValue(code, out var id) ? id
            : throw new DomainException(ErrorCodes.RuleRoleUnmapped, $"Chưa ánh xạ vai trò tài khoản {code}.");
        Guid ExpenseRole(PayrollExpenseGroup g)
            => roles.TryGetValue($"PAYROLL_EXPENSE_{g.ToString().ToUpperInvariant()}", out var id) ? id : Role("PAYROLL_EXPENSE_GENERAL");

        var label = $"Lương {sheet.PeriodFrom:MM/yyyy}";
        var lines = new List<VoucherLineInput>();
        foreach (var g in sheet.Lines.GroupBy(l => ExpenseRole(l.ExpenseGroup)))
        {
            var gross = g.Sum(l => l.Gross);
            var employer = g.Sum(l => l.EmployerContributions);
            if (gross > 0) lines.Add(new VoucherLineInput(g.Key, Side.Debit, gross, Description: $"{label} – tiền lương"));
            if (employer > 0) lines.Add(new VoucherLineInput(g.Key, Side.Debit, employer, Description: $"{label} – doanh nghiệp đóng bảo hiểm, kinh phí công đoàn"));
        }
        foreach (var l in sheet.Lines.Where(l => l.Net > 0))
            lines.Add(new VoucherLineInput(Role("PAYROLL_PAYABLE"), Side.Credit, l.Net, l.EmployeeId, Description: $"{label} – thực lĩnh"));
        void Credit(string role, decimal amount, string text) { if (amount > 0) lines.Add(new VoucherLineInput(Role(role), Side.Credit, amount, Description: $"{label} – {text}")); }
        Credit("PAYROLL_SI_PAYABLE", sheet.Lines.Sum(l => l.SocialInsurance + l.EmployerSocialInsurance), "bảo hiểm xã hội");
        Credit("PAYROLL_HI_PAYABLE", sheet.Lines.Sum(l => l.HealthInsurance + l.EmployerHealthInsurance), "bảo hiểm y tế");
        Credit("PAYROLL_UI_PAYABLE", sheet.Lines.Sum(l => l.UnemploymentInsurance + l.EmployerUnemploymentInsurance), "bảo hiểm thất nghiệp");
        Credit("PAYROLL_PIT_PAYABLE", sheet.Lines.Sum(l => l.PersonalIncomeTax), "thuế TNCN khấu trừ");
        Credit("PAYROLL_OTHER_DEDUCTION", sheet.Lines.Sum(l => l.OtherDeduction), "khấu trừ khác");
        Credit("PAYROLL_UNION_PAYABLE", sheet.Lines.Sum(l => l.EmployerUnionFee), "kinh phí công đoàn");
        if (lines.Count == 0) throw new DomainException(ErrorCodes.VoucherNoLines, "Bảng lương không có số tiền nào để hạch toán.");
        return lines;
    }

    private static PayrollTotals Totals(List<PayrollSheetLine> l) => new(l.Count, l.Sum(x => x.Gross), l.Sum(x => x.SocialInsurance), l.Sum(x => x.HealthInsurance),
        l.Sum(x => x.UnemploymentInsurance), l.Sum(x => x.PersonalIncomeTax), l.Sum(x => x.OtherDeduction), l.Sum(x => x.Net), l.Sum(x => x.EmployerContributions));

    private static void ValidateSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var res = HakSchemas.Get("payroll.schema.json").Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (res.IsValid) return;
        var errors = (res.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => new ValidationError(ErrorCodes.PackageSchemaInvalid, $"{d.InstanceLocation}: {e.Value}")))
            .Take(50).ToList();
        throw new DomainException(ErrorCodes.PackageSchemaInvalid,
            "Gói bảng lương không đúng JSON Schema: " + string.Join("; ", errors.Take(5).Select(e => e.Message)), errors);
    }
}
