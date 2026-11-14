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

        app.MapGet("/api/scan/status", (ScanService scans) =>
        {
            var (_, json) = ScanApiCore.Status(scans);
            return Results.Content(json, "application/json; charset=utf-8");
        });

        app.MapPost("/api/scan/cancel", (ScanService scans) =>
        {
            var (_, json) = ScanApiCore.Cancel(scans);
            return Results.Content(json, "application/json; charset=utf-8");
        });
    }
}
