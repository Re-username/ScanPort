using System.Net;
using System.Net.Sockets;
using System.Text;
using PortScanner.Server;

namespace PortScanner.Maui.Core.Net;

/// <summary>
/// Минимальный HTTP-сервер для локального API сканирования внутри приложения.
/// Kestrel недоступен на Android (нет runtime pack Microsoft.AspNetCore.App),
/// поэтому обрабатываем три endpoint'а api/scan/* напрямую через TcpListener.
/// </summary>
public sealed class MiniHttpServer
{
    private static readonly byte[] HeaderSeparator = { 13, 10, 13, 10 };

    private readonly ScanService _scans;
    private TcpListener? _listener;

    public MiniHttpServer(ScanService scans)
    {
        _scans = scans;
    }

    public int Port { get; private set; }

    public void Start()
    {
        for (var port = 5080; port < 5100; port++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                Port = port;
                break;
            }
            catch (SocketException)
            {
                // порт занят — пробуем следующий
            }
        }

        if (_listener is null)
            throw new InvalidOperationException("Не найден свободный порт в диапазоне 5080–5099");

        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync();
            }
            catch
            {
                break; // listener остановлен
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (method, path, body) = await ReadRequestAsync(stream);
                if (method is null)
                    return;

                var (status, json) = (method, path) switch
                {
                    ("POST", "/api/scan/start") => ScanApiCore.Start(_scans, body ?? ""),
                    ("GET", "/api/scan/status") => ScanApiCore.Status(_scans),
                    ("POST", "/api/scan/cancel") => ScanApiCore.Cancel(_scans),
                    ("OPTIONS", _) => (200, "{}"),
                    _ => (404, """{"error":"not found"}"""),
                };

                await WriteResponseAsync(stream, status, json);
            }
            catch
            {
                // один сломанный запрос не должен ронять сервер
            }
        }
    }

    private static async Task<(string? Method, string Path, string? Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var data = new List<byte>();
        var headerEnd = -1;

        while (headerEnd < 0 && data.Count < 65536)
        {
            var n = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (n <= 0)
                return (null, "", null);
            data.AddRange(buffer.Take(n));
            headerEnd = IndexOf(data, HeaderSeparator);
        }

        if (headerEnd < 0)
            return (null, "", null);

        var headerText = Encoding.UTF8.GetString(data.GetRange(0, headerEnd).ToArray());
        var lines = headerText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return (null, "", null);

        var method = parts[0].ToUpperInvariant();
        var path = parts[1].Split('?')[0].TrimEnd('/');

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
                continue;
            var name = line[..idx].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(idx + 1)..].Trim(), out contentLength);
        }

        var bodyStart = headerEnd + HeaderSeparator.Length;
        var bodyBytes = data.GetRange(Math.Min(bodyStart, data.Count), Math.Max(0, data.Count - bodyStart)).ToList();
        while (bodyBytes.Count < contentLength)
        {
            var n = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, contentLength - bodyBytes.Count));
            if (n <= 0)
                break;
            bodyBytes.AddRange(buffer.Take(n));
        }

        return (method, path, Encoding.UTF8.GetString(bodyBytes.ToArray()));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Connection: close\r\n" +
            "\r\n");

        await stream.WriteAsync(head, 0, head.Length);
        await stream.WriteAsync(payload, 0, payload.Length);
    }

    private static int IndexOf(List<byte> data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Count; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }
}
