using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using PortScanner.Server;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
// Слушаем все интерфейсы: к серверу могут подключаться другие устройства в локальной сети.
// Ограничить только этим компьютером можно: ScanPort.exe --urls http://127.0.0.1:5080
if (string.IsNullOrEmpty(builder.Configuration["Urls"]))
    builder.WebHost.UseUrls("http://0.0.0.0:5080");

builder.Services.AddSingleton<ScanService>();

var app = builder.Build();

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
// В одиночном exe (single-file) папки wwwroot рядом нет — сайт отдаём из встроенных ресурсов сборки
var files = Directory.Exists(webRoot)
    ? (IFileProvider)new PhysicalFileProvider(webRoot)
    : new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
if (!Directory.Exists(webRoot))
    app.Logger.LogInformation("Веб-интерфейс отдаётся из ресурсов сборки (single-file).");

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });

ScanApi.Map(app);

var url = app.Urls.FirstOrDefault() ?? "http://0.0.0.0:5080";
Console.WriteLine($"Сканер портов запущен: {url}  (Ctrl+C — выход)");
var localIps = ScanService.GetLocalIPv4();
if (localIps.Count > 0)
    Console.WriteLine($"Этот компьютер в сети: {string.Join(", ", localIps)}");
Console.WriteLine("Другие устройства в LAN могут открыть эту страницу по любому из этих адресов.");
if (!args.Contains("--no-browser"))
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* браузер не открылся — пользователь перейдёт сам */ }
}

app.Run();
