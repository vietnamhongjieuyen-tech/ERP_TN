using Hak.Api;
using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Banking;
using Hak.Application.Common;
using Hak.Application.Exchange;
using Hak.Application.Inventory;
using Hak.Application.Rules;
using Hak.Application.Settlement;
using Hak.Application.Security;
using Hak.Application.Setup;
using Hak.Application.Tax;
using Hak.Application.Trade;
using Hak.Data;
using Hak.Domain.Common;

var builder = WebApplication.CreateBuilder(args);

var runtime = new HakRuntimeOptions { Environment = builder.Configuration["Hak:Environment"] ?? "Development" };

// Storage (docs/02 ADR-04): Sqlite = one machine (default); PostgreSQL = LAN server mode. The PostgreSQL connection string comes
// from ConnectionStrings:Hak (environment variable ConnectionStrings__Hak or a protected settings file), never from the repository.
var provider = builder.Configuration["Hak:Provider"] ?? "Sqlite";
var usePostgres = provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) || provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);
if (!usePostgres && !provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Hak:Provider '{provider}' không hợp lệ (Sqlite | PostgreSQL).");
string storage;
if (usePostgres)
{
    var cs = builder.Configuration.GetConnectionString("Hak");
    if (string.IsNullOrWhiteSpace(cs))
        throw new InvalidOperationException("Hak:Provider=PostgreSQL cần chuỗi kết nối ConnectionStrings:Hak (biến môi trường ConnectionStrings__Hak).");
    var csb = new Npgsql.NpgsqlConnectionStringBuilder(cs);
    if (runtime.IsProduction && csb.SslMode is Npgsql.SslMode.Disable)
        throw new InvalidOperationException("Production trên mạng LAN không được tắt SSL tới PostgreSQL (SslMode=Disable).");
    storage = $"PostgreSQL {csb.Host}:{csb.Port}/{csb.Database}";   // never log the password
    builder.Services.AddScoped(_ => new HakDbContext(HakDb.PostgresOptions(cs)));
}
else
{
    var dbPath = builder.Configuration["Hak:DatabasePath"]
                 ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAK", runtime.Environment, "hak.db");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
    storage = $"SQLite {dbPath}";
    builder.Services.AddScoped(_ => new HakDbContext(HakDb.SqliteOptions(dbPath)));
}

builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<CatalogImportService>();
builder.Services.AddScoped<AccountingEngine>();
builder.Services.AddScoped<LedgerQueryService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<JournalVoucherPackageService>();
builder.Services.AddScoped<SettlementService>();
builder.Services.AddScoped<PostingRuleService>();
builder.Services.AddScoped<BusinessDocumentService>();
builder.Services.AddScoped<ClosingService>();
builder.Services.AddScoped<PaymentOrderService>();
builder.Services.AddScoped<BankReconciliationService>();
builder.Services.AddScoped<StatementVoucherService>();
builder.Services.AddScoped<StockService>();
builder.Services.AddScoped<DrynessService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<VatService>();
builder.Services.AddScoped<Hak.Application.Assets.AssetService>();
builder.Services.AddScoped<Hak.Application.Fx.FxService>();
builder.Services.AddScoped<Hak.Application.Fx.FxPaymentService>();
builder.Services.AddScoped<Hak.Application.Loans.LoanService>();
builder.Services.AddScoped<Hak.Application.Payroll.PayrollService>();
builder.Services.AddScoped<Hak.Application.Exchange.ExchangeKeyService>();
builder.Services.AddScoped<Hak.Application.Production.ProductionCostService>();
builder.Services.AddSingleton(new Hak.Application.Backup.BackupOptions
{
    Directory = builder.Configuration["Hak:BackupPath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAK", runtime.Environment, "backups"),
});
builder.Services.AddScoped<Hak.Application.Backup.BackupService>();
builder.Services.AddScoped<Hak.Application.Backup.BackupScheduleService>();
builder.Services.AddHostedService<Hak.Api.BackupScheduler>();
builder.Services.AddScoped<Hak.Application.Reports.ReportService>();
builder.Services.AddScoped<Hak.Application.Overview.OverviewService>();
builder.Services.AddScoped<WorkflowSetupService>();
builder.Services.AddScoped<VoucherAttachmentService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new DecimalAsStringConverter()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HakDbContext>();
    HakDb.Migrate(db);
    await scope.ServiceProvider.GetRequiredService<SetupService>().EnsureReferenceDataAsync();
    var wrapped = await scope.ServiceProvider.GetRequiredService<ExchangeKeyService>().ProtectStoredSecretsAsync();
    if (wrapped > 0) app.Logger.LogInformation("Protected {Count} exchange key secret(s) at rest.", wrapped);
    var stale = Hak.Application.Backup.BackupService.DeleteStaleWorkFiles(scope.ServiceProvider.GetRequiredService<Hak.Application.Backup.BackupOptions>().Directory);
    if (stale > 0) app.Logger.LogWarning("Removed {Count} unencrypted work file(s) left in the backup folder by an interrupted backup.", stale);

    // Local administrator recovery (docs/08 §8): Hak.Api.exe --reset-admin-password <user>; the password is typed twice, never echoed.
    var at = Array.IndexOf(args, "--reset-admin-password");
    if (at >= 0)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (at + 1 >= args.Length || Console.IsInputRedirected) { Console.Error.WriteLine("Dùng: Hak.Api.exe --reset-admin-password <tên đăng nhập> (chạy trong cửa sổ dòng lệnh)."); return 2; }
        Console.WriteLine("Gõ mật khẩu mới rồi bấm Enter (mỗi ký tự hiện một dấu *).");
        var first = ConsoleSecret.Read("Mật khẩu mới: ");
        if (first != ConsoleSecret.Read("Nhập lại: ")) { Console.Error.WriteLine("Hai lần nhập không khớp."); return 1; }
        try
        {
            await scope.ServiceProvider.GetRequiredService<SetupService>().RecoverAdminPasswordAsync(args[at + 1], first);
            Console.WriteLine("Đã đặt lại mật khẩu và mở khóa. Hành động đã ghi vào nhật ký kiểm toán.");
            return 0;
        }
        catch (DomainException ex) { Console.Error.WriteLine($"{ex.Code}: {ex.Message}"); return 1; }
    }
}

// Packaged mode (docs/08): the API also serves the built web UI from wwwroot beside the executable (or Hak:WebRoot), same origin,
// so the browser needs no proxy. Development keeps using the Vite dev server.
var webRoot = builder.Configuration["Hak:WebRoot"] ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (File.Exists(Path.Combine(webRoot, "index.html")))
{
    app.Use(async (http, next) =>
    {
        var h = http.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        // script-src: 'wasm-unsafe-eval' lets the OCR engine (WebAssembly, served from this origin) compile; no remote or inline scripts.
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; worker-src 'self' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'none'";
        await next();
    });
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(webRoot));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    // .mjs (PDF reader worker) and .gz (OCR language data) are not in every default content-type table.
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    types.Mappings[".mjs"] = "text/javascript";
    types.Mappings[".gz"] = "application/gzip";
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types });
    app.MapFallback(async http =>
    {
        if (http.Request.Path.StartsWithSegments("/api")) { http.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.SendFileAsync(files.GetFileInfo("index.html"));
    });
}

app.UseMiddleware<DomainExceptionMiddleware>();
app.MapHakEndpoints();
app.MapPhase2Endpoints();
app.MapBankingEndpoints();
app.MapInventoryEndpoints();
app.MapDrynessEndpoints();
app.MapTradeEndpoints();
app.MapVatEndpoints();
app.MapAssetEndpoints();
app.MapFxEndpoints();
app.MapLoanEndpoints();
app.MapPayrollEndpoints();
app.MapProductionEndpoints();
app.MapBackupEndpoints();
app.MapReportEngineEndpoints();
app.Logger.LogInformation("HAK API started. Environment={Env} Storage={Storage}", runtime.Environment, storage);
app.Run();
return 0;

public partial class Program;

internal static class ConsoleSecret
{
    /// <summary>Reads a line from the console showing one * per character (the text itself is never shown).</summary>
    public static string Read(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        for (ConsoleKeyInfo k; (k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter;)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); } }
            else if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
