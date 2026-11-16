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
        string? Host, int[]? Ports, int TimeoutMs,
        bool Tcp, bool Udp, bool Banner, bool Ping);

    public sealed record PortResult(int Port, string Protocol, string Status, string? Banner);

    public sealed record ScanStatus(
        int Scanned, int Total, IReadOnlyList<PortResult> Results,
        bool Running, string? Error, string? Ping, bool Paused);

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
    private volatile bool _paused;
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
        if (req.Ports is null || req.Ports.Length == 0)
            return "Укажите хотя бы один порт.";
        if (req.Ports.Any(p => p is < 1 or > 65535))
            return "Порты должны быть в диапазоне 1–65535.";
        if (req.TimeoutMs is < 100 or > 30000)
            return "Таймаут должен быть от 100 до 30000 мс.";
        if (!req.Tcp && !req.Udp)
            return "Выберите хотя бы один метод: TCP и/или UDP.";

        _cts = new CancellationTokenSource();
        _paused = false;
        _ = Task.Run(() => RunAsync(req, _cts.Token));
        return null;
    }

    public bool TogglePause()
    {
        _paused = !_paused;
        return _paused;
    }

    public ScanStatus GetStatus(bool includeClosed)
    {
        lock (_gate)
        {
            // порядок не важен: клиент сортирует для отображения сам.
            // Закрытые порты (режим «все») отдаём только по запросу — иначе каждый
            // опрос тащил бы десятки тысяч строк, даже когда показаны только открытые.
            IEnumerable<PortResult> snapshot = _results;
            if (!includeClosed)
                snapshot = snapshot.Where(r => r.Status != "closed");
            return new ScanStatus(_scanned, _total, snapshot.ToArray(), _running, _error, _ping, _paused);
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
        IPAddress[] ips;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(req.Host!, ct);
            // предпочитаем IPv4; их может быть несколько — устройство в нескольких сетях
            ips = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            if (ips.Length == 0)
                ips = addrs;
        }
        catch (Exception ex)
        {
            Finish($"Не удалось разрешить имя хоста: {ex.Message}");
            return;
        }

        if (ips.Length == 0)
        {
            Finish("Не удалось разрешить имя хоста.");
            return;
        }

        var checks = new List<(string Proto, int Port)>();
        foreach (var port in req.Ports!.Distinct().OrderBy(p => p))
        {
            if (req.Tcp) checks.Add(("tcp", port));
            if (req.Udp) checks.Add(("udp", port));
        }
        lock (_gate) { _total = checks.Count; }

        if (req.Ping)
            _ = Task.Run(() => PingLoopAsync(ips, req.TimeoutMs, ct), ct);

        try
        {
            await Parallel.ForEachAsync(checks,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = ct },
                async (check, token) =>
                {
                    try
                    {
                        // пауза: не запускаем новые проверки, уже идущие завершаются
                        while (_paused && !token.IsCancellationRequested)
                            await Task.Delay(150, token);

                        PortResult? result = check.Proto == "tcp"
                            ? await CheckTcpAsync(ips, check.Port, req.TimeoutMs, req.Banner, includeClosed: true, token)
                            : await CheckUdpAsync(ips, check.Port, req.TimeoutMs, token);
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

    private async Task PingLoopAsync(IPAddress[] ips, int timeoutMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string text;
            try
            {
                // пингуем все адреса хоста параллельно — достаточно одного ответившего
                var tasks = ips.Select(async ip =>
                {
                    try
                    {
                        using var ping = new Ping();
                        var reply = await ping.SendPingAsync(ip, Math.Min(timeoutMs, 2000));
                        return reply.Status == IPStatus.Success ? (ip, reply.RoundtripTime) : ((IPAddress?)null, 0L);
                    }
                    catch
                    {
                        return ((IPAddress?)null, 0L);
                    }
                }).ToList();

                IPAddress? alive = null;
                long rtt = 0;
                foreach (var task in tasks)
                {
                    var (ip, time) = await task;
                    if (ip is not null)
                    {
                        alive = ip;
                        rtt = time;
                        break;
                    }
                }

                text = alive is not null
                    ? $"Хост отвечает на ping, {rtt} мс · {alive}"
                    : "Хост не отвечает на ping (ICMP может быть запрещён)";
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
        IPAddress[] ips, int port, int timeoutMs, bool grabBanner, bool includeClosed, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        // пробуем подключиться по каждому адресу хоста — кто первым ответил, тот и есть
        var attempts = ips.Select(ip => TryConnectAsync(ip, port, timeout, ct)).ToList();

        TcpClient? winner = null;
        var rest = new List<Task<TcpClient?>>(attempts);
        while (rest.Count > 0)
        {
            var done = await Task.WhenAny(rest);
            rest.Remove(done);
            var client = done.Result;
            if (client is not null)
            {
                winner = client;
                break;
            }
        }

        // проигравшие попытки: если кто-то всё же подключится — закроем сокет
        foreach (var task in rest)
        {
            _ = task.ContinueWith(t =>
            {
                try { t.Result?.Dispose(); }
                catch { }
            });
        }

        if (winner is null)
        {
            // закрыт или отфильтрован на всех адресах
            return includeClosed ? new PortResult(port, "tcp", "closed", null) : null;
        }

        using (winner)
        {
            string? banner = null;
            if (grabBanner)
                banner = await ReadBannerAsync(winner, port, timeoutMs, ct);

            return new PortResult(port, "tcp", "open", banner);
        }
    }

    private static async Task<TcpClient?> TryConnectAsync(
        IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var tcp = new TcpClient(ip.AddressFamily);
            try
            {
                await tcp.ConnectAsync(ip, port, ct).AsTask().WaitAsync(timeout, ct);
                return tcp;
            }
            catch (TimeoutException) when (attempt == 0)
            {
                // Таймаут — не отказ. На нестабильных каналах (интернет) пакет мог потеряться,
                // а порт на самом деле открыт: одна повторная попытка. Отказ (RST) не повторяем.
                tcp.Dispose();
            }
            catch
            {
                tcp.Dispose();
                return null;
            }
        }
        return null;
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
        IPAddress[] ips, int port, int timeoutMs, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var attempts = ips.Select(ip => TryUdpAsync(ip, port, timeout, ct)).ToList();

        (string Status, string? Banner)? winner = null;
        var rest = new List<Task<(string Status, string? Banner)?>>(attempts);
        while (rest.Count > 0)
        {
            var done = await Task.WhenAny(rest);
            rest.Remove(done);
            var result = done.Result;
            if (result is not null)
            {
                winner = result;
                break;
            }
        }

        // ответа не было ни на одном адресе — результат неопределён
        var (status, banner) = winner ?? ("unknown", null);
        return new PortResult(port, "udp", status, banner);
    }

    private static async Task<(string Status, string? Banner)?> TryUdpAsync(
        IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(ip.AddressFamily);
        try
        {
            udp.Connect(ip, port);
            await udp.SendAsync(UdpProbe, UdpProbe.Length);
            var result = await udp.ReceiveAsync().WaitAsync(timeout, ct);
            var raw = Encoding.UTF8.GetString(result.Buffer, 0, Math.Min(result.Buffer.Length, 256));
            var printable = new string(raw.Where(c => !char.IsControl(c)).ToArray());
            return ("open", Sanitize(printable));
        }
        catch
        {
            return null; // тишина на этом адресе
        }
    }

    /// <summary>IPv4-адреса этого устройства из всех активных сетевых интерфейсов (кроме loopback и туннелей).</summary>
    public static IReadOnlyList<string> GetLocalIPv4()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var address in ni.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address))
                        result.Add(address.Address.ToString());
                }
            }
        }
        catch { /* список адресов — подсказка, не критично */ }
        return result.Distinct().ToList();
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
