using System.Text.Json.Nodes;
using Hak.Application.Exchange;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Integration.Tests;

/// <summary>
/// Payroll import: signed package → checks → one Draft voucher through PAYROLL_* roles, immutable sheet, per-employee report.
/// Amounts below are illustrative test values, not statutory contribution rates.
/// </summary>
public class PayrollTests
{
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;

    private static async Task<TestHost> HostAsync(bool mapUnemployment = true)
    {
        var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        foreach (var (code, name) in new[] { ("SAL_GEN", "Chi phí lương chung"), ("SAL_PROD", "Chi phí lương sản xuất"), ("SI_PAY", "BHXH phải nộp"),
                     ("HI_PAY", "BHYT phải nộp"), ("UI_PAY", "BHTN phải nộp"), ("PIT_PAY", "Thuế TNCN phải nộp"), ("UNION_PAY", "KPCĐ phải nộp") })
            h.Acc[code] = (await s.Setup.CreateAccountAsync(h.Admin, code, name + " (mã thử)")).Id;
        h.Acc["SAL_PAY"] = (await s.Setup.CreateAccountAsync(h.Admin, "SAL_PAY", "Phải trả người lao động (mã thử)", requiresPartner: true,
            display: BalanceDisplay.GrossByPartner, openItemNature: OpenItemNature.Payable)).Id;
        foreach (var (role, code) in new[] { ("PAYROLL_EXPENSE_GENERAL", "SAL_GEN"), ("PAYROLL_EXPENSE_PRODUCTION", "SAL_PROD"), ("PAYROLL_PAYABLE", "SAL_PAY"),
                     ("PAYROLL_SI_PAYABLE", "SI_PAY"), ("PAYROLL_HI_PAYABLE", "HI_PAY"), ("PAYROLL_PIT_PAYABLE", "PIT_PAY"), ("PAYROLL_UNION_PAYABLE", "UNION_PAY") })
            await s.Rules.SetRoleMappingAsync(h.Admin, role, code);
        if (mapUnemployment) await s.Rules.SetRoleMappingAsync(h.Admin, "PAYROLL_UI_PAYABLE", "UI_PAY");
        h.Partner["NV01"] = (await s.Setup.CreatePartnerAsync(h.Admin, "NV01", "Nhân viên 1", PartnerRoles.Employee)).Id;
        h.Partner["NV02"] = (await s.Setup.CreatePartnerAsync(h.Admin, "NV02", "Nhân viên 2", PartnerRoles.Employee)).Id;
        return h;
    }

    private static JsonObject Employee(string code, string group, string gross, string si, string hi, string ui, string pit, string net,
        string esi, string ehi, string eui, string union) => new()
    {
        ["employee_code"] = code, ["expense_group"] = group, ["gross"] = gross, ["net"] = net,
        ["deductions"] = new JsonObject { ["social_insurance"] = si, ["health_insurance"] = hi, ["unemployment_insurance"] = ui, ["personal_income_tax"] = pit },
        ["employer"] = new JsonObject { ["social_insurance"] = esi, ["health_insurance"] = ehi, ["unemployment_insurance"] = eui, ["union_fee"] = union },
    };

    private static JsonObject[] March() =>
    [
        Employee("NV01", "PRODUCTION", "10000000", "800000", "150000", "100000", "200000", "8750000", "1750000", "300000", "100000", "200000"),
        Employee("NV02", "ADMIN", "20000000", "1600000", "300000", "200000", "1000000", "16900000", "3500000", "600000", "200000", "400000"),
    ];

    private static string Package(TestHost h, string packageId, string version, params JsonObject[] employees)
    {
        var p = new JsonObject
        {
            ["schema_version"] = "1.0", ["package_id"] = packageId, ["event_id"] = "evt-" + packageId, ["source_system"] = TestHost.ExchangeSource,
            ["company_code"] = "TEST01", ["created_at"] = "2026-04-01T09:00:00+07:00", ["document_type"] = "payroll",
            ["source_id"] = "BL-2026-03", ["source_version"] = version,
            ["payload"] = new JsonObject
            {
                ["period_from"] = "2026-03-01", ["period_to"] = "2026-03-31", ["posting_date"] = "2026-03-31", ["description"] = "Bảng lương tháng 3",
                ["employees"] = new JsonArray(employees.Select(e => (JsonNode)e).ToArray()),
            },
        };
        PackageCrypto.Seal(p, TestHost.ExchangeKeyId, h.ExchangeSecret);
        return p.ToJsonString();
    }

    private static async Task<decimal> BalanceAsync(Scope s, Guid account)
    {
        var lines = await s.Db.JournalLines.AsNoTracking().Where(l => l.AccountId == account).Select(l => new { l.Side, l.Amount }).ToListAsync();
        return lines.Sum(l => l.Side == Side.Debit ? l.Amount : -l.Amount);
    }

    private static async Task PostAsync(TestHost h, Scope s, Guid voucherId)
    {
        await s.Engine.SubmitAsync(h.Preparer, voucherId);
        await s.Engine.ApproveAsync(h.Approver, voucherId);
        await s.Engine.PostAsync(h.Approver, voucherId);
    }

    [Fact]
    public async Task DryRun_ThenCommit_BooksThroughRoles_AndReportsPerEmployeeOnceposted()
    {
        using var h = await HostAsync();
        using var s = h.NewScope();
        var dry = await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0001", "1", March()), commit: false);
        Assert.Equal(("VALID", 30_000_000m, 25_650_000m, 1_200_000m, 7_050_000m), (dry.Outcome, dry.Totals!.Gross, dry.Totals.Net, dry.Totals.PersonalIncomeTax, dry.Totals.EmployerContributions));
        Assert.Equal(dry.PreviewLines.Where(l => l.Side == Side.Debit).Sum(l => l.Amount), dry.PreviewLines.Where(l => l.Side == Side.Credit).Sum(l => l.Amount));
        Assert.Empty(await s.Db.PayrollSheets.ToListAsync());

        var result = await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0002", "1", March()), commit: true);
        Assert.Equal("CREATED", result.Outcome);
        Assert.Empty(await s.Payroll.SummaryAsync(h.Auditor, new(2026, 1, 1), new(2026, 12, 31)));      // Draft voucher: not in reports yet
        await PostAsync(h, s, result.VoucherId!.Value);

        Assert.Equal(12_350_000m, await BalanceAsync(s, h.Acc["SAL_PROD"]));     // 10,000,000 + employer 2,350,000
        Assert.Equal(24_700_000m, await BalanceAsync(s, h.Acc["SAL_GEN"]));      // ADMIN has no own role → GENERAL
        Assert.Equal(-7_650_000m, await BalanceAsync(s, h.Acc["SI_PAY"]));       // employee + employer parts
        Assert.Equal(-1_200_000m, await BalanceAsync(s, h.Acc["PIT_PAY"]));
        Assert.Equal(-25_650_000m, await BalanceAsync(s, h.Acc["SAL_PAY"]));
        Assert.Equal(2, (await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["SAL_PAY"], null, new(2026, 3, 31))).Count);   // net payable per employee

        var summary = await s.Payroll.SummaryAsync(h.Auditor, new(2026, 1, 1), new(2026, 12, 31));
        Assert.Equal((10_000_000m, 200_000m, 8_750_000m), (summary[0].Gross, summary[0].PersonalIncomeTax, summary[0].Net));
        Assert.Equal("NV02", summary[1].EmployeeCode);
        Assert.Equal("Posted", Assert.Single(await s.Payroll.SheetsAsync(h.Auditor, new(2026, 3, 1), new(2026, 3, 31))).VoucherStatus);
    }

    [Fact]
    public async Task Replay_Duplicate_Conflict_AndNewVersionAfterReversal()
    {
        using var h = await HostAsync();
        using var s = h.NewScope();
        var first = await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0001", "1", March()), commit: true);
        await PostAsync(h, s, first.VoucherId!.Value);

        Assert.Equal(ErrorCodes.PackageReplayed, await CodeOf(() => s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0001", "1", March()), commit: true)));
        Assert.Equal("DUPLICATE", (await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0002", "1", March()), commit: true)).Outcome);
        var conflict = await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0003", "2", March()), commit: true);
        Assert.Equal(("CONFLICT", first.VoucherId), (conflict.Outcome, conflict.VoucherId));

        await s.Engine.ReverseAsync(h.Approver, first.VoucherId!.Value, new(2026, 3, 31), "Tính lại lương tháng 3");
        var second = await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0004", "2", March()), commit: true);
        Assert.Equal("CREATED", second.Outcome);
        Assert.Equal(2, await s.Db.PayrollSheets.CountAsync());
    }

    [Fact]
    public async Task InvalidSheets_AreRejectedWithReasons_AndNothingIsWritten()
    {
        using var h = await HostAsync(mapUnemployment: false);
        using var s = h.NewScope();
        await s.Setup.CreatePartnerAsync(h.Admin, "KH09", "Khách hàng", PartnerRoles.Customer);
        var bad = await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0001", "1",
            Employee("NV01", "PRODUCTION", "10000000", "800000", "150000", "100000", "200000", "8750001", "0", "0", "0", "0"),   // net ≠ gross − deductions
            Employee("KH09", "GENERAL", "1000000", "0", "0", "0", "0", "1000000", "0", "0", "0", "0"),                        // not an employee
            Employee("NV99", "GENERAL", "1000000", "0", "0", "0", "0", "1000000", "0", "0", "0", "0")), commit: true);        // unknown
        Assert.Equal("INVALID", bad.Outcome);
        Assert.Equal(3, bad.Errors.Count);
        Assert.Empty(await s.Db.PayrollSheets.ToListAsync());
        Assert.Empty(await s.Db.ImportBatches.ToListAsync());

        // Unemployment insurance present but its role is not mapped.
        var unmapped = await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0002", "1", March()), commit: false);
        Assert.Equal(ErrorCodes.RuleRoleUnmapped, Assert.Single(unmapped.Errors).Code);

        var tampered = JsonNode.Parse(Package(h, "PKG-PAY-0003", "1", March()))!.AsObject();
        tampered["payload"]!["employees"]![0]!["net"] = "9000000";
        Assert.Equal(ErrorCodes.PackageSignatureInvalid, await CodeOf(() => s.Payroll.ProcessAsync(h.Preparer, tampered.ToJsonString(), commit: false)));
        var noNet = JsonNode.Parse(Package(h, "PKG-PAY-0004", "1", March()))!.AsObject();
        noNet["payload"]!["employees"]![0]!.AsObject().Remove("net");
        Assert.Equal(ErrorCodes.PackageSchemaInvalid, await CodeOf(() => s.Payroll.ProcessAsync(h.Preparer, noNet.ToJsonString(), commit: false)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Payroll.ProcessAsync(h.Auditor, Package(h, "PKG-PAY-0005", "1", March()), commit: false)));
    }

    [Fact]
    public async Task ExchangeKeys_IssuedOnce_UsableForSigning_DeactivationRevokes_SecretNeverAudited()
    {
        using var h = await HostAsync();
        using var s = h.NewScope();
        var keys = new ExchangeKeyService(s.Db, s.Audit);
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => keys.IssueAsync(h.Preparer, "HRM", "2026-01")));
        var issued = await keys.IssueAsync(h.Admin, "hrm", "2026-01");
        Assert.Equal("HRM", issued.SourceSystem);
        Assert.Equal(32, Convert.FromBase64String(issued.SecretBase64).Length);
        // At rest the secret is wrapped (Windows DPAPI), never the Base64 handed to the sending system; legacy plain rows are upgraded.
        var stored = await s.Db.ExchangePartnerKeys.AsNoTracking().SingleAsync(k => k.Id == issued.Id);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("dpapi:", stored.SecretBase64);
            Assert.DoesNotContain(issued.SecretBase64, stored.SecretBase64);
            Assert.True(await keys.ProtectStoredSecretsAsync() >= 1);    // the plain test-host key
            Assert.Equal(0, await keys.ProtectStoredSecretsAsync());
            Assert.All(await s.Db.ExchangePartnerKeys.AsNoTracking().ToListAsync(), k => Assert.StartsWith("dpapi:", k.SecretBase64));
        }
        Assert.Equal(ErrorCodes.DuplicateCode, await CodeOf(() => keys.IssueAsync(h.Admin, "HRM", "2026-01")));

        string Signed(string packageId)
        {
            var p = JsonNode.Parse(Package(h, packageId, "1", March()))!.AsObject();
            p["source_system"] = "HRM";
            PackageCrypto.Seal(p, "2026-01", Convert.FromBase64String(issued.SecretBase64));
            return p.ToJsonString();
        }
        Assert.Equal("CREATED", (await s.Payroll.ProcessAsync(h.Preparer, Signed("PKG-HRM-0001"), commit: true)).Outcome);

        await keys.DeactivateAsync(h.Admin, issued.Id, "Đổi khóa định kỳ");
        Assert.Equal(ErrorCodes.UnknownSource, await CodeOf(() => s.Payroll.ProcessAsync(h.Preparer, Signed("PKG-HRM-0002"), commit: false)));
        Assert.DoesNotContain(issued.SecretBase64, string.Join("|", await s.Db.AuditLogs.Select(a => a.DataJson ?? "").ToListAsync()));
        Assert.Contains(await keys.ListAsync(h.Admin), k => k.KeyId == "2026-01" && !k.IsActive);
    }

    [Fact]
    public async Task ImportedSheet_IsImmutable()
    {
        using var h = await HostAsync();
        using var s = h.NewScope();
        await s.Payroll.ProcessAsync(h.Preparer, Package(h, "PKG-PAY-0001", "1", March()), commit: true);
        Assert.Contains("LEDGER_IMMUTABLE", (await Assert.ThrowsAnyAsync<Exception>(() =>
            s.Db.Database.ExecuteSqlRawAsync("UPDATE payroll_sheet_lines SET personal_income_tax = 0, net = gross"))).Message);
        Assert.Contains("LEDGER_IMMUTABLE", (await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlRawAsync("DELETE FROM payroll_sheets"))).Message);
    }
}
