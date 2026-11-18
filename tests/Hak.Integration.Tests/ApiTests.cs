using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hak.Integration.Tests;

/// <summary>End-to-end over HTTP: proves permissions and money formatting hold at the API boundary, not only in services.</summary>
public class ApiTests
{
    private const string Pw = "Api-Password-123";

    private static async Task<JsonNode> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static HttpClient As(WebApplicationFactory<Program> f, string token, string? company = null)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (company is not null) c.DefaultRequestHeaders.Add("X-Company-Id", company);
        return c;
    }

    private static async Task<string> Login(HttpClient c, string user)
        => (string)(await Json(await c.PostAsJsonAsync("/api/auth/login", new { userName = user, password = Pw })))["token"]!;

    [Theory]
    [InlineData("0.0000", "0")]
    [InlineData("-0.0000", "0")]
    [InlineData("1250000.0000", "1250000")]
    [InlineData("25133.333300", "25133.3333")]
    [InlineData("100", "100")]
    [InlineData("-7.2500", "-7.25")]
    public void MoneyIsSerialisedInCanonicalForm(string stored, string expected)
        => Assert.Equal(expected, Hak.Api.DecimalAsStringConverter.Canonical(decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task FullFlow_OverHttp()
    {
        var dbPath = Path.Combine(TestHost.Root, $"api-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(TestHost.Root);
        // With HAK_TEST_PG the same flow runs against the API in PostgreSQL (LAN) mode on a throw-away hak_test_* database.
        using var pg = TestHost.PostgresServer is null ? null : ScratchPostgresDatabase.Create();
        await using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Hak:Environment", "Test");
            if (pg is null) b.UseSetting("Hak:DatabasePath", dbPath);
            else
            {
                b.UseSetting("Hak:Provider", "PostgreSQL");
                b.UseSetting("ConnectionStrings:Hak", pg.ConnectionString);
            }
        });
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/accounts")).StatusCode);

        await Json(await anon.PostAsJsonAsync("/api/setup/bootstrap", new { userName = "admin", fullName = "Admin", password = Pw }));
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.PostAsJsonAsync("/api/setup/bootstrap", new { userName = "x", fullName = "x", password = Pw })).StatusCode);
        var adminToken = await Login(anon, "admin");
        var admin = As(f, adminToken);

        var regime = (string)(await Json(await admin.PostAsJsonAsync("/api/admin/regime-versions", new
        {
            regimeCode = "TEST-REGIME", regimeName = "Kiểm thử", versionCode = "TEST-1", effectiveFrom = "2020-01-01", effectiveTo = (string?)null,
            sources = new[] { new { ruleGroup = "TEST", documentNo = "TEST-FIXTURE" } },
        })))["id"]!;
        var company = (string)(await Json(await admin.PostAsJsonAsync("/api/admin/companies",
            new { code = "API01", name = "Công ty API", functionalCurrency = "VND", regimeVersionId = regime })))["id"]!;
        var lapId = (string)(await Json(await admin.PostAsJsonAsync("/api/admin/users", new { userName = "lap", fullName = "Lập", password = Pw })))["id"]!;
        var kttId = (string)(await Json(await admin.PostAsJsonAsync("/api/admin/users", new { userName = "ktt", fullName = "KTT", password = Pw })))["id"]!;
        foreach (var (uid, role) in new[] { (lapId, "GL_ACCOUNTANT"), (kttId, "CHIEF_ACCOUNTANT") })
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/admin/grants", new { userId = uid, companyId = company, roleCode = role })).StatusCode);

        var lap = As(f, await Login(anon, "lap"), company);
        var ktt = As(f, await Login(anon, "ktt"), company);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(f, adminToken, company).GetAsync("/api/accounts")).StatusCode); // admin has no role in API01

        // CHIEF_ACCOUNTANT has no config.manage by default; fiscal-year creation needs the ADMIN role in the company.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ktt.PostAsJsonAsync("/api/fiscal-years", new { code = "2026", startDate = "2026-01-01", endDate = "2026-12-31" })).StatusCode);
        var grantAdmin = await admin.PostAsJsonAsync("/api/admin/grants", new { userId = kttId, companyId = company, roleCode = "ADMIN" });
        Assert.Equal(HttpStatusCode.NoContent, grantAdmin.StatusCode);
        await Json(await ktt.PostAsJsonAsync("/api/fiscal-years", new { code = "2026", startDate = "2026-01-01", endDate = "2026-12-31" }));
        var cash = (string)(await Json(await ktt.PostAsJsonAsync("/api/accounts", new { code = "CASH", name = "Tiền mặt" })))["id"]!;
        var rev = (string)(await Json(await ktt.PostAsJsonAsync("/api/accounts", new { code = "REV", name = "Doanh thu" })))["id"]!;

        // Money as JSON number is refused; as string it is accepted and echoed back as string.
        var asNumber = await lap.PostAsync("/api/vouchers", new StringContent(
            $$"""{"voucherType":"GJ","voucherDate":"2026-03-01","postingDate":"2026-03-01","description":"x","lines":[{"accountId":"{{cash}}","side":"D","amount":100}]}""",
            System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, asNumber.StatusCode);

        var v = await Json(await lap.PostAsJsonAsync("/api/vouchers", new
        {
            voucherType = "GJ", voucherDate = "2026-03-01", postingDate = "2026-03-01", description = "Bán lẻ thu tiền mặt",
            lines = new object[] { new { accountId = cash, side = "D", amount = "12345678901" }, new { accountId = rev, side = "C", amount = "12345678901" } },
        }));
        Assert.Equal(JsonValueKind.String, v["totalDebit"]!.GetValueKind());
        Assert.Equal("12345678901", (string)v["totalDebit"]!);
        var id = (string)v["id"]!;

        await Json(await lap.PostAsync($"/api/vouchers/{id}/submit", null));
        Assert.Equal(HttpStatusCode.Forbidden, (await lap.PostAsync($"/api/vouchers/{id}/approve", null)).StatusCode);
        await Json(await ktt.PostAsync($"/api/vouchers/{id}/approve", null));
        Assert.Equal(HttpStatusCode.Conflict, (await ktt.PostAsync($"/api/vouchers/{id}/post?rowVersion=1", null)).StatusCode);
        await Json(await ktt.PostAsync($"/api/vouchers/{id}/post", null));
        Assert.Equal(HttpStatusCode.Conflict, (await ktt.PostAsync($"/api/vouchers/{id}/post", null)).StatusCode);

        var tb = await Json(await ktt.GetAsync("/api/reports/trial-balance?from=2026-03-01&to=2026-03-31"));
        Assert.True((bool)tb["isBalanced"]!);
        Assert.Equal("12345678901", (string)tb["totals"]!["totalPeriodDebit"]!);
        Assert.True((bool)(await Json(await ktt.GetAsync("/api/audit/verify")))["intact"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await lap.GetAsync("/api/audit/verify")).StatusCode);

        // Paged voucher list with filters.
        for (var i = 1; i <= 2; i++)
            await Json(await lap.PostAsJsonAsync("/api/vouchers", new
            {
                voucherType = "GJ", voucherDate = $"2026-03-0{i + 1}", postingDate = $"2026-03-0{i + 1}", description = $"Chi phí văn phòng {i}",
                lines = new object[] { new { accountId = rev, side = "D", amount = "10" }, new { accountId = cash, side = "C", amount = "10" } },
            }));
        var page = await Json(await ktt.GetAsync("/api/vouchers?from=2026-03-01&to=2026-03-31&take=2"));
        Assert.Equal((3, 2), ((int)page["total"]!, page["items"]!.AsArray().Count));
        var second = await Json(await ktt.GetAsync("/api/vouchers?from=2026-03-01&to=2026-03-31&take=2&skip=2"));
        Assert.Single(second["items"]!.AsArray());
        var found = await Json(await ktt.GetAsync("/api/vouchers?text=" + Uri.EscapeDataString("văn phòng 2")));
        Assert.Equal(1, (int)found["total"]!);
        // Case and accents are ignored, also for upper-case Vietnamese letters (SQLite lower() only folds A–Z).
        foreach (var typed in new[] { "CHI PHÍ VĂN PHÒNG", "chi phi van phong", "Văn Phòng" })
            Assert.Equal(2, (int)(await Json(await ktt.GetAsync("/api/vouchers?text=" + Uri.EscapeDataString(typed))))["total"]!);
        Assert.Equal(0, (int)(await Json(await ktt.GetAsync("/api/vouchers?text=" + Uri.EscapeDataString("không có"))))["total"]!);
        Assert.Equal(1, (int)(await Json(await ktt.GetAsync("/api/vouchers?status=Posted")))["total"]!);

        // Idempotency-Key: the reply was lost and "Lưu" is sent again — one voucher, the same reply; other content with the
        // same key is refused; the key belongs to its user.
        HttpRequestMessage Save(string amount, string key, HttpClient? _ = null)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "/api/vouchers")
            {
                Content = JsonContent.Create(new
                {
                    voucherType = "GJ", voucherDate = "2026-03-05", postingDate = "2026-03-05", description = "Gửi lại khi mất phản hồi",
                    lines = new object[] { new { accountId = rev, side = "D", amount }, new { accountId = cash, side = "C", amount } },
                }),
            };
            m.Headers.Add("Idempotency-Key", key);
            return m;
        }
        var first = await lap.SendAsync(Save("77", "k-1"));
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, firstBody);
        var again = await lap.SendAsync(Save("77", "k-1"));
        Assert.Equal(first.StatusCode, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal((string)JsonNode.Parse(firstBody)!["id"]!, (string)JsonNode.Parse(await again.Content.ReadAsStringAsync())!["id"]!);
        Assert.Equal(4, (int)(await Json(await ktt.GetAsync("/api/vouchers?from=2026-03-01&to=2026-03-31")))["total"]!);   // not 5
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lap.SendAsync(Save("78", "k-1"))).StatusCode);
        Assert.True((await ktt.SendAsync(Save("77", "k-1"))).IsSuccessStatusCode);   // same key, other user: a new request
        Assert.Equal(5, (int)(await Json(await ktt.GetAsync("/api/vouchers?from=2026-03-01&to=2026-03-31")))["total"]!);
        // A failed request is not stored: after fixing nothing, the same key may run again (and fails again on its own).
        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/vouchers") { Content = JsonContent.Create(new { voucherType = "GJ", voucherDate = "2026-03-05", postingDate = "2026-03-05", description = "", lines = Array.Empty<object>() }) };
        bad.Headers.Add("Idempotency-Key", "k-bad");
        Assert.False((await lap.SendAsync(bad)).IsSuccessStatusCode);
        var bad2 = new HttpRequestMessage(HttpMethod.Post, "/api/vouchers") { Content = JsonContent.Create(new { voucherType = "GJ", voucherDate = "2026-03-05", postingDate = "2026-03-05", description = "", lines = Array.Empty<object>() }) };
        bad2.Headers.Add("Idempotency-Key", "k-bad");
        Assert.False((await lap.SendAsync(bad2)).Headers.Contains("Idempotent-Replay"));
    }
}
