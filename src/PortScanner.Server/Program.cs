using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using PortScanner.Server;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["Urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");

builder.Services.AddSingleton<ScanService>();

var app = builder.Build();

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(webRoot))
{
    var files = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
}
else
{
    app.Logger.LogWarning("wwwroot не найден рядом с приложением — веб-интерфейс недоступен.");
}

app.MapPost("/api/scan/start", (ScanService scans, ScanService.ScanRequest req) =>
{
    var error = scans.Start(req);
    return Results.Json(new { error });
});

app.MapGet("/api/scan/status", (ScanService scans) => Results.Json(scans.GetStatus()));

app.MapPost("/api/scan/cancel", (ScanService scans) =>
{
    scans.Cancel();
    return Results.Ok();
});

var url = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5080";
Console.WriteLine($"Сканер портов запущен: {url}  (Ctrl+C — выход)");
if (!args.Contains("--no-browser"))
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* браузер не открылся — пользователь перейдёт сам */ }
}

app.Run();
