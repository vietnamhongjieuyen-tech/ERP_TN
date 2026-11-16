using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Integration;
using Hak.Domain.Organization;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Json.Schema;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Exchange;

public sealed record ImportError(string Code, string Message, int? Line = null);

public sealed record ImportItemResult(string SourceId, string Outcome, Guid? VoucherId, string? VoucherNumber, IReadOnlyList<ImportError> Errors);

public sealed record ImportCounts(int Total, int Valid, int Created, int Duplicate, int Conflict, int Invalid);

public sealed record ImportResult(string SchemaVersion, string PackageId, string Mode, string Status, ImportCounts Counts, IReadOnlyList<ImportItemResult> Items)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Signing helpers shared by senders, receivers and tests (docs/04 §3).</summary>
public static class PackageCrypto
{
    public static string PayloadChecksum(JsonNode payload)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(payload))));

    /// <summary>Signed bytes = JCS(package with auth.signature removed); method and key_id are therefore signed too.</summary>
    public static string Signature(JsonObject package, byte[] secret)
    {
        var copy = package.DeepClone().AsObject();
        if (copy["auth"] is JsonObject auth) auth.Remove("signature");
        return Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(copy))));
    }

    /// <summary>Fills checksum and signature in place (sender side).</summary>
    public static void Seal(JsonObject package, string keyId, byte[] secret)
    {
        package["checksum"] = new JsonObject { ["algorithm"] = "SHA-256", ["value"] = PayloadChecksum(package["payload"]!) };
        package["auth"] = new JsonObject { ["method"] = "HMAC-SHA256", ["key_id"] = keyId, ["signature"] = "" };
        package["auth"]!["signature"] = Signature(package, secret);
    }
}

/// <summary>
/// JSON Exchange for journal vouchers: authenticate → schema → checksum → replay → mapping →
/// duplicate/conflict detection → validation → (dry-run preview | commit as Draft vouchers) → audit.
/// Imported vouchers are never posted automatically and never overwrite existing vouchers (master prompt §29).
/// </summary>
public sealed class JournalVoucherPackageService(HakDbContext db, AccountingEngine engine, AuditService audit, IClock clock)
{


    public async Task<ImportResult> ProcessAsync(SessionContext ctx, string json, bool commit)
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
        var keyId = (string)package["auth"]!["key_id"]!;
        var key = await db.ExchangePartnerKeys.AsNoTracking().FirstOrDefaultAsync(k =>
            k.CompanyId == ctx.CompanyId && k.SourceSystem == sourceSystem && k.KeyId == keyId && k.IsActive)
            ?? throw new DomainException(ErrorCodes.UnknownSource, $"Không có khóa xác thực cho nguồn {sourceSystem}/{keyId}.");

        var expectedSig = Convert.FromBase64String(PackageCrypto.Signature(package, SecretProtector.Unprotect(key.SecretBase64)));
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, Convert.FromBase64String((string)package["auth"]!["signature"]!)))
            throw new DomainException(ErrorCodes.PackageSignatureInvalid, "Chữ ký gói dữ liệu không hợp lệ.");
        if (PackageCrypto.PayloadChecksum(package["payload"]!) != (string?)package["checksum"]!["value"])
            throw new DomainException(ErrorCodes.PackageChecksumMismatch, "Checksum payload không khớp.");
        if (await db.ImportBatches.AnyAsync(b => b.CompanyId == ctx.CompanyId && b.PackageId == packageId))
            throw new DomainException(ErrorCodes.PackageReplayed, $"Gói {packageId} đã được nhập trước đây.");

        var plans = await PlanAsync(ctx, company, sourceSystem, package["payload"]!["vouchers"]!.AsArray());
        var invalid = plans.Count(p => p.Outcome is "INVALID");
        var conflict = plans.Count(p => p.Outcome is "CONFLICT");
        var duplicate = plans.Count(p => p.Outcome is "DUPLICATE_IDENTICAL");

        if (!commit)
        {
            var items = plans.Select(p => p.ToResult()).ToList();
            return new ImportResult("1.0", packageId, "DRY_RUN", invalid + conflict == 0 ? "OK" : "HAS_ERRORS",
                new ImportCounts(plans.Count, plans.Count(p => p.Outcome == "VALID"), 0, duplicate, conflict, invalid), items);
        }

        if (invalid + conflict > 0)
        {
            // All-or-nothing: nothing is written except the audit trail of the refusal.
            await db.InTransactionAsync(async () =>
            {
                await audit.AppendAsync(ctx.CompanyId, ctx, "import.reject", "import_package", packageId, new { sourceSystem, invalid, conflict }, result: "REJECTED");
                await db.SaveChangesAsync();
            });
            return new ImportResult("1.0", packageId, "COMMIT", "REJECTED",
                new ImportCounts(plans.Count, plans.Count(p => p.Outcome == "VALID"), 0, duplicate, conflict, invalid), plans.Select(p => p.ToResult()).ToList());
        }

        return await db.InTransactionAsync(async () =>
        {
            var results = new List<ImportItemResult>();
            foreach (var p in plans)
            {
                if (p.Outcome != "VALID") { results.Add(p.ToResult()); continue; }
                var v = await engine.CreateDraftAsync(ctx, p.Input!);
                results.Add(new ImportItemResult(p.SourceId, "CREATED", v.Id, v.Number, []));
            }
            var created = results.Count(r => r.Outcome == "CREATED");
            var result = new ImportResult("1.0", packageId, "COMMIT", "COMMITTED",
                new ImportCounts(plans.Count, created, created, duplicate, 0, 0), results);
            db.ImportBatches.Add(new ImportBatch
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, PackageId = packageId, SourceSystem = sourceSystem, DocumentType = "journal-voucher",
                SchemaVersion = "1.0", Checksum = (string)package["checksum"]!["value"]!, Status = ImportBatchStatus.Committed,
                ItemCount = plans.Count, CreatedCount = created, DuplicateCount = duplicate, ResultJson = result.ToJson(),
                ReceivedAt = clock.UtcNow, ReceivedBy = ctx.UserId,
            });
            await audit.AppendAsync(ctx.CompanyId, ctx, "import.commit", "import_package", packageId, new { sourceSystem, created, duplicate });
            await db.SaveChangesAsync();
            return result;
        });
    }

    private static void ValidateSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var res = HakSchemas.Get("journal-voucher.schema.json").Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (res.IsValid) return;
        var errors = (res.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => new ValidationError(ErrorCodes.PackageSchemaInvalid, $"{d.InstanceLocation}: {e.Value}")))
            .Take(50).ToList();
        throw new DomainException(ErrorCodes.PackageSchemaInvalid,
            "Gói dữ liệu không đúng JSON Schema: " + string.Join("; ", errors.Take(5).Select(e => e.Message)), errors);
    }

    private sealed record Plan(string SourceId, string Outcome, VoucherInput? Input, List<ImportError> Errors, Guid? ExistingId = null, string? ExistingNumber = null)
    {
        public ImportItemResult ToResult() => new(SourceId, Outcome, ExistingId, ExistingNumber, Errors);
    }

    private async Task<List<Plan>> PlanAsync(SessionContext ctx, Company company, string sourceSystem, JsonArray vouchers)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).ToListAsync();
        var partners = await db.BusinessPartners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).ToListAsync();
        var accByCode = accounts.ToDictionary(a => a.Code);
        var partnerByCode = partners.ToDictionary(p => p.Code);
        var accById = accounts.ToDictionary(a => a.Id);
        var partnerById = partners.ToDictionary(p => p.Id);
        var dp = await db.Currencies.Where(c => c.Code == company.FunctionalCurrency).Select(c => c.DecimalPlaces).FirstAsync();
        var periods = await db.AccountingPeriods.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).ToListAsync();
        var sourceIds = vouchers.Select(v => (string)v!["source_id"]!).ToList();
        var existing = await db.Vouchers.AsNoTracking()
            .Where(v => v.CompanyId == ctx.CompanyId && v.SourceSystem == sourceSystem && v.SourceId != null && sourceIds.Contains(v.SourceId) && v.Status != VoucherStatus.Cancelled)
            .Select(v => new { v.Id, v.Number, v.SourceId, v.SourceVersion, v.Status }).ToDictionaryAsync(v => v.SourceId!);

        var seen = new HashSet<string>();
        var plans = new List<Plan>();
        foreach (var node in vouchers)
        {
            var v = node!.AsObject();
            var sourceId = (string)v["source_id"]!;
            var sourceVersion = (string)v["source_version"]!;
            var errors = new List<ImportError>();

            if (!seen.Add(sourceId))
            {
                plans.Add(new Plan(sourceId, "INVALID", null, [new(ErrorCodes.DuplicateSource, "source_id lặp lại trong cùng gói.")]));
                continue;
            }
            if (existing.TryGetValue(sourceId, out var ex))
            {
                plans.Add(ex.SourceVersion == sourceVersion
                    ? new Plan(sourceId, "DUPLICATE_IDENTICAL", null, [], ex.Id, ex.Number)
                    : new Plan(sourceId, "CONFLICT", null,
                        [new(ErrorCodes.DuplicateSource, $"Đã tồn tại chứng từ {ex.Number} (phiên bản nguồn {ex.SourceVersion}, trạng thái {ex.Status}); " +
                            "bản cập nhật không được tự ghi đè, cần xử lý trong hàng chờ xung đột.")], ex.Id, ex.Number));
                continue;
            }

            if (!DateOnly.TryParseExact((string)v["voucher_date"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var voucherDate) ||
                !DateOnly.TryParseExact((string)v["posting_date"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var postingDate))
            {
                plans.Add(new Plan(sourceId, "INVALID", null, [new(ErrorCodes.ValidationFailed, "Ngày không hợp lệ.")]));
                continue;
            }

            var lines = new List<VoucherLineInput>();
            var lineNo = 0;
            foreach (var ln in v["lines"]!.AsArray())
            {
                lineNo++;
                var accCode = (string)ln!["account_code"]!;
                if (!accByCode.TryGetValue(accCode, out var acc))
                {
                    errors.Add(new(ErrorCodes.AccountNotFound, $"Tài khoản {accCode} không có trong danh mục.", lineNo));
                    continue;
                }
                Guid? partnerId = null;
                if ((string?)ln["partner_code"] is { } pc)
                {
                    if (partnerByCode.TryGetValue(pc, out var p)) partnerId = p.Id;
                    else { errors.Add(new(ErrorCodes.PartnerWrongCompany, $"Đối tượng {pc} không có trong danh mục.", lineNo)); continue; }
                }
                lines.Add(new VoucherLineInput(acc.Id, (string)ln["side"]! == "D" ? Side.Debit : Side.Credit, Dec((string)ln["amount"]!), partnerId,
                    (string?)ln["foreign_currency"], DecN((string?)ln["foreign_amount"]), DecN((string?)ln["exchange_rate"]), (string?)ln["description"]));
            }

            var input = new VoucherInput((string)v["voucher_type"]!, voucherDate, postingDate, (string)v["description"]!, lines,
                SourceSystem: sourceSystem, SourceId: sourceId, SourceVersion: sourceVersion);

            if (errors.Count == 0)
            {
                var probe = new Voucher { CompanyId = ctx.CompanyId, Lines = lines.Select((l, i) => new VoucherLine
                {
                    LineNo = i + 1, AccountId = l.AccountId, PartnerId = l.PartnerId, Side = l.Side, Amount = l.Amount,
                    ForeignCurrency = l.ForeignCurrency, ForeignAmount = l.ForeignAmount, ExchangeRate = l.ExchangeRate,
                }).ToList() };
                errors.AddRange(VoucherValidator.Validate(probe, accById, partnerById, dp).Select(e => new ImportError(e.Code, e.Message, e.LineNo)));
                var period = periods.FirstOrDefault(p => p.Contains(postingDate));
                if (period is null) errors.Add(new(ErrorCodes.PeriodNotFound, $"Không có kỳ kế toán chứa ngày {postingDate}."));
                else if (period.Status == PeriodStatus.Locked) errors.Add(new(ErrorCodes.PeriodLocked, $"Kỳ {period.StartDate:MM/yyyy} đã khóa."));
            }
            plans.Add(new Plan(sourceId, errors.Count == 0 ? "VALID" : "INVALID", errors.Count == 0 ? input : null, errors));
        }
        return plans;
    }

    private static decimal Dec(string s) => decimal.Parse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    private static decimal? DecN(string? s) => s is null ? null : Dec(s);
}
