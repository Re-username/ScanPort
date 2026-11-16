using System.Text.Json;

namespace PortScanner.Server;

/// <summary>
/// Ядро HTTP API сканирования без привязки к веб-серверу: принимает уже разобранный
/// запрос и возвращает (код, JSON). Десктопный сервер (Kestrel) и Android-приложение
/// (MiniHttpServer) оба дергают эти методы — поведение гарантированно одинаковое.
/// </summary>
public static class ScanApiCore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static (int Status, string Json) Start(ScanService scans, string bodyJson)
    {
        ScanService.ScanRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<ScanService.ScanRequest>(bodyJson, JsonOptions);
        }
        catch
        {
            req = null;
        }

        if (req is null)
            return (400, """{"error":"Некорректный запрос."}""");

        return Start(scans, req);
    }

    public static (int Status, string Json) Start(ScanService scans, ScanService.ScanRequest req)
    {
        var error = scans.Start(req);
        return (200, JsonSerializer.Serialize(new { error }, JsonOptions));
    }

    public static (int Status, string Json) Status(ScanService scans, bool includeClosed) =>
        (200, JsonSerializer.Serialize(scans.GetStatus(includeClosed), JsonOptions));

    public static (int Status, string Json) Cancel(ScanService scans)
    {
        scans.Cancel();
        return (200, "{}");
    }

    public static (int Status, string Json) Pause(ScanService scans)
    {
        var paused = scans.TogglePause();
        return (200, JsonSerializer.Serialize(new { paused }, JsonOptions));
    }

    public static (int Status, string Json) Addresses() =>
        (200, JsonSerializer.Serialize(new { addresses = ScanService.GetLocalIPv4() }, JsonOptions));
}
