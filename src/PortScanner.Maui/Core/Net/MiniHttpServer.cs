using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PortScanner.Server;

namespace PortScanner.Maui.Core.Net;

/// <summary>
/// Минимальный HTTP-сервер для локального API сканирования внутри приложения.
/// Kestrel недоступен на Android (нет runtime pack Microsoft.AspNetCore.App),
/// поэтому обрабатываем три endpoint'а api/scan/* напрямую через TcpListener.
/// Поддерживает Content-Length и chunked (так .NET HttpClient шлёт POST).
/// </summary>
public sealed class MiniHttpServer
{
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
                var reader = new RequestReader(client.GetStream());
                var (method, path, query, body) = await reader.ReadRequestAsync();
                if (method is null)
                    return;

                var (status, json) = (method, path) switch
                {
                    ("POST", "/api/scan/start") => ScanApiCore.Start(_scans, body ?? ""),
                    ("GET", "/api/scan/status") => ScanApiCore.Status(_scans, query.Contains("all=1") || query.Contains("all=true")),
                    ("POST", "/api/scan/cancel") => ScanApiCore.Cancel(_scans),
                    ("POST", "/api/scan/pause") => ScanApiCore.Pause(_scans),
                    ("GET", "/api/scan/addresses") => ScanApiCore.Addresses(),
                    ("OPTIONS", _) => (200, "{}"),
                    _ => (404, """{"error":"not found"}"""),
                };

                await WriteResponseAsync(client.GetStream(), status, json);
            }
            catch
            {
                // один сломанный запрос не должен ронять сервер
            }
        }
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

    /// <summary>Потоковое чтение HTTP-запроса: заголовки + тело (Content-Length или chunked).</summary>
    private sealed class RequestReader
    {
        private readonly NetworkStream _stream;
        private readonly List<byte> _buffer = new();

        public RequestReader(NetworkStream stream)
        {
            _stream = stream;
        }

        public async Task<(string? Method, string Path, string Query, string? Body)> ReadRequestAsync()
        {
            var headerText = await ReadBlockAsync();
            if (headerText is null)
                return (null, "", "", null);

            var lines = headerText.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2)
                return (null, "", "", null);

            var method = parts[0].ToUpperInvariant();
            string path, query;
            var qIdx = parts[1].IndexOf('?');
            if (qIdx >= 0)
            {
                path = parts[1][..qIdx].TrimEnd('/');
                query = parts[1][(qIdx + 1)..];
            }
            else
            {
                path = parts[1].TrimEnd('/');
                query = "";
            }

            var contentLength = 0;
            var chunked = false;
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0)
                    continue;
                var name = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out contentLength);
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                         value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                    chunked = true;
            }

            if (chunked)
                return (method, path, query, await ReadChunkedBodyAsync());
            if (contentLength > 0)
                return (method, path, query, Encoding.UTF8.GetString(await ReadBytesAsync(contentLength)));
            return (method, path, query, null);
        }

        /// <summary>Читает до пустой строки (\r\n\r\n) — блок заголовков без завершающего разделителя.</summary>
        private async Task<string?> ReadBlockAsync()
        {
            var data = new List<byte>();
            while (data.Count < 65536)
            {
                var (line, ok) = await ReadLineBytesAsync();
                if (!ok)
                    return data.Count == 0 ? null : Encoding.UTF8.GetString(data.ToArray());
                if (line.Count == 0)
                    return Encoding.UTF8.GetString(data.ToArray());
                data.AddRange(line);
                data.Add(13);
                data.Add(10);
            }
            return null;
        }

        private async Task<string> ReadChunkedBodyAsync()
        {
            var body = new List<byte>();
            while (true)
            {
                var sizeLine = await ReadLineAsync();
                if (sizeLine is null)
                    break;
                var semicolon = sizeLine.IndexOf(';');
                var sizeText = (semicolon >= 0 ? sizeLine[..semicolon] : sizeLine).Trim();
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
                    break;
                if (size == 0)
                {
                    // trailers — до пустой строки
                    while (true)
                    {
                        var trailer = await ReadLineAsync();
                        if (trailer is null || trailer.Length == 0)
                            break;
                    }
                    break;
                }

                body.AddRange(await ReadBytesAsync(size));
                await ReadBytesAsync(2); // CRLF после чанка
            }
            return Encoding.UTF8.GetString(body.ToArray());
        }

        private async Task<string?> ReadLineAsync()
        {
            var bytes = await ReadLineBytesAsync();
            return bytes.Ok ? Encoding.UTF8.GetString(bytes.Line.ToArray()) : null;
        }

        private async Task<(List<byte> Line, bool Ok)> ReadLineBytesAsync()
        {
            var line = new List<byte>();
            while (true)
            {
                if (_buffer.Count > 0)
                {
                    // ищем CRLF в буфере
                    for (var i = 0; i + 1 < _buffer.Count; i++)
                    {
                        if (_buffer[i] == 13 && _buffer[i + 1] == 10)
                        {
                            line.AddRange(_buffer.GetRange(0, i));
                            _buffer.RemoveRange(0, i + 2);
                            return (line, true);
                        }
                    }
                    // CRLF ещё не доехал — выносим всё, кроме возможного начала CRLF
                    var keep = _buffer.Count > 0 && _buffer[^1] == 13 ? 1 : 0;
                    line.AddRange(_buffer.GetRange(0, _buffer.Count - keep));
                    _buffer.RemoveRange(0, _buffer.Count - keep);
                }

                var tmp = new byte[8192];
                var n = await _stream.ReadAsync(tmp, 0, tmp.Length);
                if (n <= 0)
                    return (line, false);
                _buffer.AddRange(tmp.Take(n));
            }
        }

        private async Task<byte[]> ReadBytesAsync(int count)
        {
            var result = new List<byte>(count);
            while (result.Count < count)
            {
                if (_buffer.Count > 0)
                {
                    var take = Math.Min(count - result.Count, _buffer.Count);
                    result.AddRange(_buffer.GetRange(0, take));
                    _buffer.RemoveRange(0, take);
                    continue;
                }

                var tmp = new byte[Math.Min(8192, count - result.Count)];
                var n = await _stream.ReadAsync(tmp, 0, tmp.Length);
                if (n <= 0)
                    break;
                result.AddRange(tmp.Take(n));
            }
            return result.ToArray();
        }
    }
}
