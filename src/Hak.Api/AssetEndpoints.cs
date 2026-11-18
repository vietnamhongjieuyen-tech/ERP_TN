using Hak.Application.Assets;

namespace Hak.Api;

public sealed record UsedByRequest(string? UsedBy);
public sealed record DisposeAssetRequest(DateOnly Date, Guid ResidualAccountId, string Description);

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/assets");
        api.MapGet("", async (HttpContext http, DateOnly asOf, AssetService s) => Results.Ok(await s.RegisterAsync(await http.SessionAsync(), asOf)));
        api.MapPost("", async (HttpContext http, AssetInput input, AssetService s) => Results.Ok(await s.CreateAsync(await http.SessionAsync(), input)));
        api.MapGet("/{id:guid}", async (HttpContext http, Guid id, AssetService s) => Results.Ok(await s.DetailAsync(await http.SessionAsync(), id)));
        api.MapPost("/{id:guid}/activate", async (HttpContext http, Guid id, AssetService s) => Results.Ok(await s.ActivateAsync(await http.SessionAsync(), id)));
        api.MapPost("/{id:guid}/cancel", async (HttpContext http, Guid id, ReasonRequest r, AssetService s) =>
        { await s.CancelDraftAsync(await http.SessionAsync(), id, r.Reason); return Results.NoContent(); });
        api.MapPut("/{id:guid}/used-by", async (HttpContext http, Guid id, UsedByRequest r, AssetService s) =>
        { await s.SetUsedByAsync(await http.SessionAsync(), id, r.UsedBy); return Results.NoContent(); });
        api.MapPost("/{id:guid}/dispose", async (HttpContext http, Guid id, DisposeAssetRequest r, AssetService s) =>
            Results.Ok(await s.DisposeAsync(await http.SessionAsync(), id, r.Date, r.ResidualAccountId, r.Description)));
        api.MapPost("/{id:guid}/dispose/reverse", async (HttpContext http, Guid id, ReasonRequest r, AssetService s) =>
            Results.Ok(await s.ReverseDisposalAsync(await http.SessionAsync(), id, r.Reason)));
        api.MapGet("/cost-candidates", async (HttpContext http, Guid accountId, AssetService s) =>
            Results.Ok(await s.CostCandidatesAsync(await http.SessionAsync(), accountId)));
        api.MapGet("/reconciliation", async (HttpContext http, DateOnly asOf, AssetService s) =>
            Results.Ok(await s.ReconcileAsync(await http.SessionAsync(), asOf)));

        api.MapGet("/depreciation-runs", async (HttpContext http, AssetService s) => Results.Ok(await s.RunsAsync(await http.SessionAsync())));
        api.MapPost("/depreciation-runs", async (HttpContext http, Guid periodId, AssetService s) => Results.Ok(await s.RunAsync(await http.SessionAsync(), periodId)));
        api.MapPost("/depreciation-runs/{id:guid}/reverse", async (HttpContext http, Guid id, ReasonRequest r, AssetService s) =>
        { await s.ReverseRunAsync(await http.SessionAsync(), id, r.Reason); return Results.NoContent(); });
    }
}
