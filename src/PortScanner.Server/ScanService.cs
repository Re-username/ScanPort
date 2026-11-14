using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PortScanner.Server;

/// <summary>
/// Фоновое сканирование портов. Состояние отдаётся по HTTP клиенту,
/// который опрашивает /api/scan/status каждые 400 мс.
/// </summary>
public sealed class ScanService
{
    public sealed record ScanRequest(
        string? Host, int StartPort, int EndPort, int TimeoutMs,
        bool Tcp, bool Udp, bool Banner, bool Ping);

    public sealed record PortResult(int Port, string Protocol, string Status, string? Banner);

    public sealed record ScanStatus(
        int Scanned, int Total, IReadOnlyList<PortResult> Results,
        bool Running, string? Error, string? Ping);

    private static readonly HashSet<int> WebPorts = new()
        { 80, 81, 82, 83, 88, 3000, 3128, 5000, 5080, 8000, 8008, 8080, 8081, 8888, 9000, 10000 };

    private static readonly byte[] UdpProbe = { 0x00 };

    private const int MaxParallelism = 128;

    private readonly object _gate = new();
    private readonly List<PortResult> _results = new();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private int _scanned;
    private int _total;
    private bool _running;
    private string? _error;
    private string? _ping;

    public string? Start(ScanRequest req)
    {
        lock (_gate)
        {
            if (_running)
                return "Сканирование уже выполняется. Дождитесь завершения или отмените его.";
        }

        if (string.IsNullOrWhiteSpace(req.Host))
            return "Введите IP-адрес или имя хоста.";
        if (req.StartPort < 1 || req.StartPort > 65535 || req.EndPort < 1 || req.EndPort > 65535 || req.StartPort > req.EndPort)
            return "Некорректный диапазон портов (допустимо: 1–65535, от ≤ до).";
        if (req.TimeoutMs is < 100 or > 30000)
            return "Таймаут должен быть от 100 до 30000 мс.";
        if (!req.Tcp && !req.Udp)
            return "Выберите хотя бы один метод: TCP и/или UDP.";

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(req, _cts.Token));
        return null;
    }

    public ScanStatus GetStatus()
    {
        lock (_gate)
        {
            var results = _results.ToArray();
            Array.Sort(results, static (a, b) =>
                a.Port != b.Port ? a.Port.CompareTo(b.Port) : string.CompareOrdinal(a.Protocol, b.Protocol));
            return new ScanStatus(_scanned, _total, results, _running, _error, _ping);
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(ScanRequest req, CancellationToken ct)
    {
        lock (_gate)
        {
            _results.Clear();
            _keys.Clear();
            _scanned = 0;
            _error = null;
            _ping = null;
            _running = true;
        }

        IPAddress? ip;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(req.Host!, ct);
            ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? addrs.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Finish($"Не удалось разрешить имя хоста: {ex.Message}");
            return;
        }

        if (ip is null)
        {
            Finish("Не удалось разрешить имя хоста.");
            return;
        }

        var checks = new List<(string Proto, int Port)>();
        for (var port = req.StartPort; port <= req.EndPort; port++)
        {
            if (req.Tcp) checks.Add(("tcp", port));
            if (req.Udp) checks.Add(("udp", port));
        }
        lock (_gate) { _total = checks.Count; }

        if (req.Ping)
            _ = Task.Run(() => PingLoopAsync(ip, req.TimeoutMs, ct), ct);

        try
        {
            await Parallel.ForEachAsync(checks,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = ct },
                async (check, token) =>
                {
                    try
                    {
                        PortResult? result = check.Proto == "tcp"
                            ? await CheckTcpAsync(ip, check.Port, req.TimeoutMs, req.Banner, token)
                            : await CheckUdpAsync(ip, check.Port, req.TimeoutMs, token);
                        if (result is not null)
                            AddResult(result);
                    }
                    catch (OperationCanceledException) { }
                    catch { /* один порт не должен ронять всё сканирование */ }
                    finally
                    {
                        Interlocked.Increment(ref _scanned);
                    }
                });
        }
        catch (OperationCanceledException) { }

        Finish(error: null);
    }

    private async Task PingLoopAsync(IPAddress ip, int timeoutMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string text;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, Math.Min(timeoutMs, 2000));
                text = reply.Status == IPStatus.Success
                    ? $"Хост отвечает на ping, {reply.RoundtripTime} мс · {ip}"
                    : $"Хост не отвечает на ping · {ip}";
            }
            catch (Exception ex)
            {
                text = $"Ping недоступен: {ex.Message}";
            }

            lock (_gate) { _ping = text; }

            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<PortResult?> CheckTcpAsync(
        IPAddress ip, int port, int timeoutMs, bool grabBanner, CancellationToken ct)
    {
        using var tcp = new TcpClient(ip.AddressFamily);
        try
        {
            var timeout = TimeSpan.FromMilliseconds(timeoutMs);
            await tcp.ConnectAsync(ip, port, ct).AsTask().WaitAsync(timeout, ct);

            string? banner = null;
            if (grabBanner)
                banner = await ReadBannerAsync(tcp, port, timeoutMs, ct);

            return new PortResult(port, "tcp", "open", banner);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // закрыт или отфильтрован — в результаты не попадает
        }
    }

    private static async Task<string?> ReadBannerAsync(
        TcpClient tcp, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var stream = tcp.GetStream();
            var buffer = new byte[512];
            var text = await TryReadAsync(stream, buffer, timeoutMs, ct);

            if (string.IsNullOrEmpty(text) && WebPorts.Contains(port))
            {
                var request = Encoding.ASCII.GetBytes("HEAD / HTTP/1.0\r\n\r\n");
                await stream.WriteAsync(request, 0, request.Length, ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
                text = ExtractHttpBanner(await TryReadAsync(stream, buffer, timeoutMs, ct) ?? "");
            }

            return Sanitize(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> TryReadAsync(
        NetworkStream stream, byte[] buffer, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var n = await stream.ReadAsync(buffer, 0, buffer.Length, ct)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            return n > 0 ? Encoding.UTF8.GetString(buffer, 0, n) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? ExtractHttpBanner(string raw)
    {
        var lines = raw.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var first = lines.FirstOrDefault();
        var server = lines.FirstOrDefault(l => l.StartsWith("Server:", StringComparison.OrdinalIgnoreCase));
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(first)) parts.Add(first);
        if (server is not null) parts.Add(server["Server:".Length..].Trim());
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static async Task<PortResult> CheckUdpAsync(
        IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var udp = new UdpClient(ip.AddressFamily);
        string? banner = null;
        var status = "unknown"; // тишина — результат неопределён (может быть отфильтрован)
        try
        {
            udp.Connect(ip, port);
            await udp.SendAsync(UdpProbe, UdpProbe.Length);
            var result = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            status = "open";
            banner = Sanitize(Encoding.UTF8.GetString(result.Buffer, 0, Math.Min(result.Buffer.Length, 256)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // тишина — оставляем "unknown"
        }

        return new PortResult(port, "udp", status, banner);
    }

    private static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var cleaned = string.Join(" · ",
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length > 120 ? cleaned[..120] + "…" : cleaned;
    }

    private void AddResult(PortResult result)
    {
        lock (_gate)
        {
            var key = result.Protocol + ":" + result.Port;
            if (!_keys.Add(key))
                return;
            _results.Add(result);
        }
    }

    private void Finish(string? error)
    {
        lock (_gate)
        {
            _running = false;
            _error = error;
        }
    }
}
