using Hak.Application.Accounting;
using Hak.Application.Reports;
using Hak.Domain.Common;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Integration.Tests;

/// <summary>Report engine with a TEST template — not a legal financial statement (TT 99/2025 forms are not seeded).</summary>
public class ReportEngineTests
{
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;

    private static readonly ReportDefinitionInput Template = new("TEST-BS", "Bảng tình hình tài chính (mẫu thử)", "Kiểm thử", null,
    [
        new("100", "TÀI SẢN", Bold: true),
        new("110", "Tiền", "BAL_D(CASH, BANK*)", 1),
        new("120", "Phải thu khách hàng", "GROSS_D(AR)", 1),
        new("200", "Tổng tài sản", "LINE(110) + LINE(120)", 0, true),
        new("310", "Người mua trả tiền trước", "GROSS_C(AR)", 1),
        new("320", "Vốn chủ sở hữu", "BAL_C(EQUITY)", 1),
        new("330", "Lợi nhuận", "BAL_C(REV) - BAL_D(EXP)", 1),
        new("300", "Tổng nguồn vốn", "LINE(310) + LINE(320) + LINE(330)", 0, true),
        new("400", "Doanh thu bán chịu", "MOV_C(REV ; AR)"),
    ], [new("LINE(200) = LINE(300)", "Tổng tài sản = Tổng nguồn vốn")]);

    private static async Task SeedAsync(TestHost h, Scope s)
    {
        VoucherInput V(int month, string text, params VoucherLineInput[] lines) => new(VoucherTypes.General, new(2026, month, 5), new(2026, month, 5), text, lines);
        await h.PostedAsync(s, V(1, "Góp vốn", new(h.Acc["CASH"], Side.Debit, 1_000), new(h.Acc["EQUITY"], Side.Credit, 1_000)));
        await h.PostedAsync(s, V(2, "Bán chịu", new(h.Acc["AR"], Side.Debit, 500, h.Partner["KH01"]), new(h.Acc["REV"], Side.Credit, 500)));
        await h.PostedAsync(s, V(3, "Khách trả trước", new(h.Acc["CASH"], Side.Debit, 50), new(h.Acc["AR"], Side.Credit, 50, h.Partner["KH02"])));
        await h.PostedAsync(s, V(3, "Chi phí", new(h.Acc["EXP"], Side.Debit, 200), new(h.Acc["CASH"], Side.Credit, 200)));
    }

    [Fact]
    public async Task Template_RunsOverPostedLines_WithComparePeriod_ChecksAndDrillDown()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await SeedAsync(h, s);
        var def = await s.Reports.SaveDraftAsync(h.Preparer, Template);

        var run = await s.Reports.RunAsync(h.Auditor, def.Id, new(2026, 1, 1), new(2026, 12, 31), new(2026, 1, 1), new(2026, 1, 31));
        Assert.False(run.Verified);
        decimal? V(string code) => run.Lines.Single(l => l.Code == code).Value;
        decimal? C(string code) => run.Lines.Single(l => l.Code == code).CompareValue;
        Assert.Null(V("100"));
        Assert.Equal((850m, 500m, 1_350m, 50m, 1_000m, 300m, 1_350m, 500m), (V("110"), V("120"), V("200"), V("310"), V("320"), V("330"), V("300"), V("400")));
        Assert.Equal((1_000m, 0m, 1_000m), (C("110"), C("120"), C("300")));
        var check = Assert.Single(run.Checks);
        Assert.True(check.Passed && check.ComparePassed == true);
        Assert.Equal([new Domain.Reports.Contribution("CASH", 850)], run.Lines.Single(l => l.Code == "110").Contributions);
        Assert.Contains(new Domain.Reports.Contribution("REV / AR", 500), run.Lines.Single(l => l.Code == "400").Contributions);
    }

    [Fact]
    public async Task Templates_AreValidated_VerifiedByASecondPerson_ThenFrozen_NewVersionsAreDrafts()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var bad = Template with { Lines = [.. Template.Lines, new("500", "Lỗi", "BAL_D(") , new("510", "Lỗi", "LINE(999)")] };
        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Reports.SaveDraftAsync(h.Preparer, bad));
        Assert.Equal(ErrorCodes.ReportFormulaInvalid, ex.Code);
        Assert.Contains("500", ex.Message);
        Assert.Contains("LINE(999)", ex.Message);

        var def = await s.Reports.SaveDraftAsync(h.Preparer, Template);
        var again = await s.Reports.SaveDraftAsync(h.Preparer, Template with { Name = "Đổi tên" });        // same draft replaced
        Assert.Equal((def.Id, 1), (again.Id, again.Version));
        Assert.Equal("FORBIDDEN", await CodeOf(() => s.Reports.VerifyAsync(h.Preparer, def.Id, "tự xác minh")));   // no period.lock
        await s.Reports.SaveDraftAsync(h.Approver, Template);                                                     // approver is now the maker
        Assert.Equal(ErrorCodes.SodViolation, await CodeOf(() => s.Reports.VerifyAsync(h.Approver, def.Id, "x")));
        await s.Reports.SaveDraftAsync(h.Preparer, Template);
        await s.Reports.VerifyAsync(h.Approver, def.Id, "Đối chiếu mẫu thử nội bộ — kiểm thử");

        // Frozen in the database itself, whatever the application does.
        await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlRawAsync(
            $"UPDATE report_lines SET formula = 'BAL_D(EXP)' WHERE definition_id = {h.SqlGuidLiteral(def.Id)}"));
        await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM report_definitions WHERE id = {h.SqlGuidLiteral(def.Id)}"));

        var v2 = await s.Reports.SaveDraftAsync(h.Preparer, Template with { Name = "Phiên bản 2" });
        Assert.Equal(2, v2.Version);
        var list = await s.Reports.ListAsync(h.Auditor);
        Assert.Equal(["Draft", "Verified"], list.Select(x => x.Status));
        await s.Reports.DiscardDraftAsync(h.Preparer, v2.Id);
        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => s.Reports.DiscardDraftAsync(h.Preparer, def.Id)));
        Assert.Equal(Template.Lines.Count, (await s.Reports.ExportAsync(h.Auditor, def.Id)).Lines.Count);
    }

    internal static string LegalDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "legal", "TT99-2025"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "legal", "TT99-2025");
    }

    /// <summary>B01-DN / B02-DN templates built from TT 99/2025 Phụ lục IV, run on the TT99 chart with a small set of transactions.</summary>
    [Fact]
    public async Task Tt99Statements_B01AndB02_ComputeFromTheChart_AndBalance()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var csv = File.ReadAllLines(Path.Combine(LegalDir(), "nhap-he-thong-tk-de-xuat.csv")).Skip(1).Select(l => l.Split(';'))
            .Select((c, i) => new Hak.Application.Setup.AccountImportRow(i + 2, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8])).ToList();
        Assert.True((await s.CatalogImport.ImportAccountsAsync(h.Admin, csv, commit: true)).Committed);
        var acc = await s.Db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Id);
        await s.Setup.SetAccountTagsAsync(h.Admin, acc["1281"], "tdt");

        VoucherLineInput D(string a, decimal x, string? p = null) => new(acc[a], Side.Debit, x, p is null ? null : h.Partner[p]);
        VoucherLineInput C(string a, decimal x, string? p = null) => new(acc[a], Side.Credit, x, p is null ? null : h.Partner[p]);
        async Task Post(int month, string type, params VoucherLineInput[] lines) =>
            await h.PostedAsync(s, new VoucherInput(type, new(2026, month, 10), new(2026, month, 10), "Nghiệp vụ mẫu", lines));
        await Post(1, VoucherTypes.General, D("112", 1_000), C("41111", 1_000));                       // góp vốn
        await Post(2, VoucherTypes.General, D("131", 550, "KH01"), C("511", 500), C("33311", 50));      // bán chịu
        await Post(2, VoucherTypes.General, D("112", 100), C("131", 100, "KH02"));                     // khách trả trước
        await Post(3, VoucherTypes.General, D("156", 300), C("331", 300, "NCC01"));                     // mua hàng
        await Post(3, VoucherTypes.General, D("632", 200), C("156", 200));                             // giá vốn
        await Post(4, VoucherTypes.General, D("6421", 50), C("112", 50));                              // chi phí QLDN
        await Post(5, VoucherTypes.General, D("1281", 200), C("112", 200));                            // tiền gửi ≤ 3 tháng (TDT)
        await Post(6, VoucherTypes.General, D("331", 100, "NCC01"), C("112", 100) with { CashFlowCode = "21" }); // trả nhà thầu XDCB, mã chọn tay
        await Post(12, VoucherTypes.Closing, D("511", 500), C("911", 500), D("911", 250), C("632", 200), C("6421", 50),
            D("911", 250), C("4212", 250));                                                             // kết chuyển (CLS)

        async Task<ReportRun> Run(string file)
        {
            var input = System.Text.Json.JsonSerializer.Deserialize<ReportDefinitionInput>(File.ReadAllText(Path.Combine(LegalDir(), file)), ReportService.Json)!;
            var def = await s.Reports.SaveDraftAsync(h.Preparer, input);
            return await s.Reports.RunAsync(h.Auditor, def.Id, new(2026, 1, 1), new(2026, 12, 31));
        }
        var b01 = await Run("mau-B01-DN.json");
        decimal? V(ReportRun r, string code) => r.Lines.Single(l => l.Code == code).Value;
        Assert.All(b01.Lines, l => Assert.Null(l.Error));
        Assert.Equal((750m, 200m, 950m, 550m, 100m), (V(b01, "111"), V(b01, "112"), V(b01, "110"), V(b01, "131"), V(b01, "141")));
        Assert.Equal((100m, 200m, 50m, 1_000m, 1_000m, 250m), (V(b01, "312"), V(b01, "311"), V(b01, "314"), V(b01, "411"), V(b01, "411A"), V(b01, "420B")));
        Assert.Equal((1_600m, 1_600m), (V(b01, "280"), V(b01, "440")));
        Assert.True(Assert.Single(b01.Checks).Passed);

        var b02 = await Run("mau-B02-DN.json");
        Assert.All(b02.Lines, l => Assert.Null(l.Error));
        Assert.Equal((500m, 500m, 200m, 300m, 50m, 250m, 250m), (V(b02, "01"), V(b02, "10"), V(b02, "11"), V(b02, "20"), V(b02, "26"), V(b02, "50"), V(b02, "60")));
        Assert.Null(V(b02, "70"));

        // B03-DN: góp vốn → 31; khách trả trước → 01; chi phí → 02; gửi tiền ≤ 3 tháng là tiền ↔ tương đương tiền (không tính); trả XDCB chọn tay → 21.
        var b03 = await Run("mau-B03-DN.json");
        Assert.All(b03.Lines, l => Assert.Null(l.Error));
        Assert.Equal((100m, -50m, -100m, 1_000m), (V(b03, "01"), V(b03, "02"), V(b03, "21"), V(b03, "31")));
        Assert.Equal((50m, -100m, 1_000m, 950m, 0m, 950m), (V(b03, "20"), V(b03, "30"), V(b03, "40"), V(b03, "50"), V(b03, "60"), V(b03, "70")));
        Assert.True(Assert.Single(b03.Checks).Passed);                                                   // 70 = Mã 110 cuối kỳ

        // Gián tiếp: khoản trả 100 chọn mã 21 nhưng đối ứng 331 của nhà cung cấp hàng hóa (không nhãn DT) → kiểm tra 20 gián tiếp ≠ trực tiếp báo lệch.
        var b03i = await Run("mau-B03-DN-gian-tiep.json");
        Assert.All(b03i.Lines, l => Assert.Null(l.Error));
        // Phần D mục 4.1.2: Mã 09 lấy số dư Nợ từng đối tượng của 131 / 331, Mã 11 số dư Có (khách KH02 trả trước 100 thuộc 11,
        // không bù trừ với phải thu KH01 550): 09 = −550, 11 = 50 thuế + 200 NCC01 + 100 KH02 = 350; tổng 09 + 11 như số thuần.
        Assert.Equal((250m, -550m, -100m, 350m, -50m), (V(b03i, "01"), V(b03i, "09"), V(b03i, "10"), V(b03i, "11"), V(b03i, "20")));
        var mismatch = b03i.Checks.Single(c => c.Rule.StartsWith("LINE(20)", StringComparison.Ordinal));
        Assert.Equal((false, -50m, 50m), (mismatch.Passed, mismatch.Left, mismatch.Right));
    }

    /// <summary>
    /// B03-DN gián tiếp trên hệ thống TK TT99: khấu hao (có bút toán đảo), dự phòng, đánh giá lại tỷ giá, lãi vay, thanh lý TSCĐ,
    /// phải trả XDCB, lãi tiền gửi, nộp thuế, chi quỹ — luồng tiền HĐKD gián tiếp phải bằng trực tiếp và tiền cuối kỳ khớp sổ.
    /// </summary>
    [Fact]
    public async Task Tt99CashFlow_IndirectMethod_ReconcilesWithDirectMethod()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var csv = File.ReadAllLines(Path.Combine(LegalDir(), "nhap-he-thong-tk-de-xuat.csv")).Skip(1).Select(l => l.Split(';'))
            .Select((c, i) => new Hak.Application.Setup.AccountImportRow(i + 2, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8])).ToList();
        Assert.True((await s.CatalogImport.ImportAccountsAsync(h.Admin, csv, commit: true)).Committed);
        foreach (var (code, name, tags) in new[]
                 {
                     ("5158", "Lãi tiền gửi (thử)", "DT"), ("5159", "Lãi đánh giá lại tỷ giá (thử)", "DGL"), ("6358", "Lãi vay (thử)", "LV"),
                     ("3358", "Lãi vay phải trả (thử)", "LV"), ("3318", "Phải trả nhà thầu XDCB (thử)", "DT,CF21"),
                     ("7118", "Thu thanh lý TSCĐ (thử)", "TLTS"), ("8118", "Giá trị còn lại TSCĐ thanh lý (thử)", "TLTS"),
                 })
        {
            var a = await s.Setup.CreateAccountAsync(h.Admin, code, name);
            await s.Setup.SetAccountTagsAsync(h.Admin, a.Id, tags);
        }
        var acc = await s.Db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Id);
        VoucherLineInput D(string a, decimal x, string? p = null) => new(acc[a], Side.Debit, x, p is null ? null : h.Partner[p]);
        VoucherLineInput C(string a, decimal x, string? p = null) => new(acc[a], Side.Credit, x, p is null ? null : h.Partner[p]);
        async Task<Voucher> Post(int month, string type, params VoucherLineInput[] lines) =>
            await h.PostedAsync(s, new VoucherInput(type, new(2026, month, 10), new(2026, month, 10), "Nghiệp vụ mẫu", lines));
        const string G = VoucherTypes.General;

        // Tháng 1 (trước kỳ báo cáo): góp vốn, mua TSCĐ, bán chịu.
        await Post(1, G, D("112", 2_000), C("41111", 2_000));
        await Post(1, G, D("211", 600), C("112", 600));
        await Post(1, G, D("131", 300, "KH01"), C("511", 300));
        // Kỳ báo cáo 01/02 – 31/12.
        await Post(2, G, D("131", 1_100, "KH01"), C("511", 1_000), C("33311", 100));      // bán chịu
        await Post(3, G, D("112", 800), C("131", 800, "KH01"));                             // thu nợ → 01
        await Post(3, G, D("156", 400), D("1331", 40), C("331", 440, "NCC01"));             // mua hàng
        await Post(4, G, D("632", 300), C("156", 300));
        await Post(4, G, D("331", 240, "NCC01"), C("112", 240));                            // trả người bán → 02
        await Post(5, G, D("6424", 60), C("2141", 60));                                     // khấu hao
        var wrong = await Post(5, G, D("6424", 25), C("2141", 25));                         // khấu hao ghi nhầm, đảo lại
        await s.Engine.ReverseAsync(h.Approver, wrong.Id, new(2026, 5, 20), "Ghi nhầm");
        await Post(5, G, D("6421", 100), C("334", 100, "NCC01"));                                    // lương phải trả
        await Post(5, G, D("334", 70, "NCC01"), C("112", 70));                                       // trả lương → 03
        await Post(6, G, D("6421", 30), C("2293", 30));                                     // dự phòng phải thu khó đòi
        await Post(6, VoucherTypes.Revaluation, D("112", 20), D("131", 15, "KH01"), C("5159", 35)); // đánh giá lại tỷ giá → tiền: 61
        await Post(7, G, D("6358", 50), C("3358", 50));                                     // lãi vay phải trả
        await Post(7, G, D("3358", 40), C("112", 40));                                      // trả lãi vay → 04
        await Post(7, G, D("112", 500), C("3411", 500));                                    // đi vay → 33
        await Post(8, G, D("2412", 200), C("3318", 200));                                   // XDCB chưa trả
        await Post(8, G, D("3318", 150), C("112", 150));                                    // trả nhà thầu → 21 (nhãn CF21)
        await Post(8, G, D("112", 12), C("5158", 12));                                      // lãi tiền gửi → 27 (nhãn DT)
        await Post(9, G, D("2141", 60), D("8118", 540), C("211", 600));                    // thanh lý TSCĐ
        await Post(9, G, D("112", 700), C("7118", 700));                                    // tiền thu thanh lý → 22 (nhãn TLTS)
        await Post(10, G, D("3334", 20), C("112", 20));                                     // nộp thuế TNDN → 05
        await Post(11, G, D("4212", 30), C("3531", 30));                                    // trích quỹ khen thưởng
        await Post(11, G, D("3531", 10), C("112", 10));                                     // chi quỹ → 07 / gián tiếp 17

        async Task<ReportRun> Run(string file)
        {
            var input = System.Text.Json.JsonSerializer.Deserialize<ReportDefinitionInput>(File.ReadAllText(Path.Combine(LegalDir(), file)), ReportService.Json)!;
            var def = await s.Reports.SaveDraftAsync(h.Preparer, input);
            return await s.Reports.RunAsync(h.Auditor, def.Id, new(2026, 2, 1), new(2026, 12, 31));
        }
        decimal? V(ReportRun r, string code) => r.Lines.Single(l => l.Code == code).Value;

        var direct = await Run("mau-B03-DN.json");
        Assert.All(direct.Lines, l => Assert.Null(l.Error));
        Assert.Equal((800m, -240m, -70m, -40m, -20m, -10m, 420m), (V(direct, "01"), V(direct, "02"), V(direct, "03"), V(direct, "04"), V(direct, "05"), V(direct, "07"), V(direct, "20")));
        Assert.Equal((-150m, 700m, 12m, 500m, 20m), (V(direct, "21"), V(direct, "22"), V(direct, "27"), V(direct, "33"), V(direct, "61")));

        var b = await Run("mau-B03-DN-gian-tiep.json");
        Assert.All(b.Lines, l => Assert.Null(l.Error));
        Assert.Equal((667m, 60m, 30m, -35m, -172m, 50m, 0m, 600m), (V(b, "01"), V(b, "02"), V(b, "03"), V(b, "04"), V(b, "05"), V(b, "06"), V(b, "07"), V(b, "08")));
        Assert.Equal((-340m, -100m, 330m, 0m, 0m, -40m, -20m, 0m, -10m, 420m),
            (V(b, "09"), V(b, "10"), V(b, "11"), V(b, "12"), V(b, "13"), V(b, "14"), V(b, "15"), V(b, "16"), V(b, "17"), V(b, "20")));
        Assert.Equal((562m, 500m, 1_482m, 1_400m, 20m, 2_902m), (V(b, "30"), V(b, "40"), V(b, "50"), V(b, "60"), V(b, "61"), V(b, "70")));
        Assert.All(b.Checks, c => Assert.True(c.Passed, c.Message));
    }

    [Fact]
    public async Task Snapshot_StoresTheExactResult_IsAppendOnly_AndRefusesFailingChecks()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await SeedAsync(h, s);
        var def = await s.Reports.SaveDraftAsync(h.Preparer, Template);
        Assert.Equal("FORBIDDEN", await CodeOf(() => s.Reports.SnapshotAsync(h.Preparer, def.Id, new(2026, 1, 1), new(2026, 12, 31), null, null, "x")));
        var snap = await s.Reports.SnapshotAsync(h.Approver, def.Id, new(2026, 1, 1), new(2026, 12, 31), null, null, "Chốt số liệu quý");

        // Later postings change a new run, never the stored snapshot.
        await h.PostedAsync(s, new VoucherInput(VoucherTypes.General, new(2026, 6, 1), new(2026, 6, 1), "Thêm chi phí",
            [new VoucherLineInput(h.Acc["EXP"], Side.Debit, 10), new VoucherLineInput(h.Acc["CASH"], Side.Credit, 10)]));
        var (stored, valid, info) = await s.Reports.SnapshotAsync(h.Auditor, snap.Id);
        Assert.True(valid);
        Assert.Equal((850m, false), (stored.Lines.Single(l => l.Code == "110").Value!.Value, info.DefinitionVerified));
        Assert.Single(await s.Reports.SnapshotsAsync(h.Auditor, def.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlRawAsync(
            $"UPDATE report_snapshots SET note = 'sửa' WHERE id = {h.SqlGuidLiteral(snap.Id)}"));

        var broken = Template with { Checks = [new("LINE(200) = LINE(310)", "Cố ý sai")] };
        var d2 = await s.Reports.SaveDraftAsync(h.Preparer, broken);
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Reports.SnapshotAsync(h.Approver, d2.Id, new(2026, 1, 1), new(2026, 12, 31), null, null, "x")));
    }
}
