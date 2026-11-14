using PortScanner.Maui.Core.Net;
using PortScanner.Server;

namespace PortScanner.Maui;

public static class MauiProgram
{
    // Адрес локального API: Kestrel внутри приложения, UI ходит на него через HttpClient
    public static string ApiUrl { get; private set; } = "";

    public static MauiApp CreateMauiApp()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler", e.Exception);
            e.SetObserved();
        };

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        ApiUrl = StartLocalApi();
        builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(ApiUrl) });

        return builder.Build();
    }

    private static string StartLocalApi()
    {
        var scans = new ScanService();
        var server = new MiniHttpServer(scans);
        server.Start();
        return $"http://127.0.0.1:{server.Port}";
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = FileSystem.AppDataDirectory;
            File.AppendAllText(Path.Combine(dir, "crashes.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
