using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PortScanner.Server;

/// <summary>
/// HTTP API сканирования на Kestrel (десктопный сервер). Общая логика — в ScanApiCore.
/// </summary>
public static class ScanApi
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/scan/start", (ScanService scans, ScanService.ScanRequest req) =>
        {
            var (_, json) = ScanApiCore.Start(scans, req);
            return Results.Content(json, "application/json; charset=utf-8");
        });

        app.MapGet("/api/scan/status", (ScanService scans, bool all) =>
        {
            var (_, json) = ScanApiCore.Status(scans, all);
            return Results.Content(json, "application/json; charset=utf-8");
        });

        app.MapPost("/api/scan/cancel", (ScanService scans) =>
        {
            var (_, json) = ScanApiCore.Cancel(scans);
            return Results.Content(json, "application/json; charset=utf-8");
        });

        app.MapGet("/api/scan/addresses", () =>
        {
            var (_, json) = ScanApiCore.Addresses();
            return Results.Content(json, "application/json; charset=utf-8");
        });

        app.MapPost("/api/scan/pause", (ScanService scans) =>
        {
            var (_, json) = ScanApiCore.Pause(scans);
            return Results.Content(json, "application/json; charset=utf-8");
        });
    }
}
