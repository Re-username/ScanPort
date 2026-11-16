using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
        bool Tcp, bool Udp, bool Banner, bool Ping,
        string Profile = "lan"); // lan | wan | auto

    public sealed record PortResult(int Port, string Protocol, string Status, string? Banner);

    public sealed record ScanStatus(
        int Scanned, int Total, IReadOnlyList<PortResult> Results,
        bool Running, string? Error, string? Ping, bool Paused, int Timeout);

    private static readonly byte[] UdpProbe = { 0x00 };

    // SNMP v2c Get sysDescr (community public) — сетевое оборудование отвечает
    // строкой вида "Omron ... Switch", это самый информативный опрос для железа.
    private static readonly byte[] SnmpSysDescrProbe =
    {
        0x30, 0x29, 0x02, 0x01, 0x01, 0x04, 0x06, 0x70, 0x75, 0x62, 0x6C, 0x69, 0x63,
        0xA0, 0x1C, 0x02, 0x04, 0x00, 0x00, 0x00, 0x01, 0x02, 0x01, 0x00, 0x02, 0x01, 0x00,
        0x30, 0x0E, 0x30, 0x0C, 0x06, 0x08, 0x2B, 0x06, 0x01, 0x02, 0x01, 0x01, 0x01, 0x05, 0x00
    };

    // EtherNet/IP ListIdentity (encapsulation command 0x0063): устройство отвечает
    // Vendor ID, Product Code и именем продукта — так Shodan узнаёт «Omron NX1P2».
    private static readonly byte[] CipListIdentityProbe = CreateCipProbe();

    private static byte[] CreateCipProbe()
    {
        var probe = new byte[24]; // command 0x0063 = bytes 0x63, 0x00
        probe[0] = 0x63;
        return probe;
    }

    // Порты, где сначала пробуем TLS, а потом уже HTTP: отправка открытого текста
    // TLS-серверу часто приводит к разрыву соединения.
    private static readonly HashSet<int> TlsPorts = new()
        { 443, 465, 563, 636, 853, 989, 990, 5061, 5223, 8443, 8883, 8884 };

    private const int MaxParallelism = 128;

    // Профили сети: LAN — скорость, WAN — достоверность на дальних каналах,
    // AUTO — автоматическое регулирование таймаута по замеренному пингу.
    private static int ProfileParallelism(string profile) => profile switch
    {
        "wan" => 48,
        "auto" => 96,
        _ => MaxParallelism,
    };

    private static int ProfileRetries(string profile) => profile switch
    {
        "wan" => 3,
        "auto" => 2,
        _ => 1,
    };

    private readonly object _gate = new();
    private readonly List<PortResult> _results = new();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private volatile int _adaptiveTimeout; // AUTO: регулируется по пингу, читается при старте каждой проверки
    private volatile int _lastRtt = 500;   // последний замер пинга, мс (стартовое предположение)
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
            return new ScanStatus(_scanned, _total, snapshot.ToArray(), _running, _error, _ping, _paused, _adaptiveTimeout);
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
        _adaptiveTimeout = req.TimeoutMs;
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
            _ = Task.Run(() => PingLoopAsync(ips, req.TimeoutMs, req.Profile, ct), ct);

        try
        {
            await Parallel.ForEachAsync(checks,
                new ParallelOptions { MaxDegreeOfParallelism = ProfileParallelism(req.Profile), CancellationToken = ct },
                async (check, token) =>
                {
                    try
                    {
                        // пауза: не запускаем новые проверки, уже идущие завершаются
                        while (_paused && !token.IsCancellationRequested)
                            await Task.Delay(150, token);

                        // AUTO: таймаут, откалиброванный последним пингом; профили LAN/WAN — заданный
                        var effectiveTimeout = req.Profile == "auto" ? _adaptiveTimeout : req.TimeoutMs;

                        PortResult? result = check.Proto == "tcp"
                            ? await CheckTcpAsync(ips, check.Port, effectiveTimeout, req.Banner, includeClosed: true, ProfileRetries(req.Profile), req.Host ?? string.Empty, token)
                            : await CheckUdpAsync(ips, check.Port, effectiveTimeout, token);
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

    private async Task PingLoopAsync(IPAddress[] ips, int baseTimeoutMs, string profile, CancellationToken ct)
    {
        var consecutiveFails = 0; // гистерезис: «не отвечает» только после 2 промахов подряд
        while (!ct.IsCancellationRequested)
        {
            string text;
            try
            {
                // срок ожидания пинга адаптируется к сети: AUTO — от последнего RTT
                // (в коридоре 200–2000 мс), профили — от базового таймаута
                var pingWait = profile == "auto"
                    ? (int)Math.Clamp(_lastRtt * 2 + 100, 200, 2000)
                    : Math.Min(baseTimeoutMs, 2000);

                // пингуем все адреса хоста параллельно — достаточно одного ответившего
                var tasks = ips.Select(async ip =>
                {
                    try
                    {
                        using var ping = new Ping();
                        var reply = await ping.SendPingAsync(ip, pingWait);
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

                if (alive is not null)
                {
                    consecutiveFails = 0;
                    _lastRtt = (int)rtt;
                    // AUTO: регулируем таймаут проверок портов по замеру канала.
                    // ×8 — запас на джиттер: пинг 100 мс → таймаут 800 мс; жёсткий коридор 500–10000 мс.
                    if (profile == "auto")
                        _adaptiveTimeout = (int)Math.Clamp(rtt * 8, 500, 10000);
                    text = profile == "auto"
                        ? $"Хост отвечает на ping, {rtt} мс · таймаут {_adaptiveTimeout} мс · {alive}"
                        : $"Хост отвечает на ping, {rtt} мс · {alive}";
                }
                else
                {
                    consecutiveFails++;
                    // одиночная потеря — ещё не «молчит» (показываем последний живой статус)
                    if (consecutiveFails < 2 && _ping is not null)
                        text = _ping;
                    else
                        text = "Хост не отвечает на ping (ICMP может быть запрещён)";
                }
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
        IPAddress[] ips, int port, int timeoutMs, bool grabBanner, bool includeClosed, int retries, string hostName, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        // пробуем подключиться по каждому адресу хоста — кто первым ответил, тот и есть
        var attempts = ips.Select(ip => TryConnectAsync(ip, port, timeout, retries, ct)).ToList();

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
                banner = await ReadBannerAsync(winner, port, timeoutMs, hostName, ct);

            return new PortResult(port, "tcp", "open", banner);
        }
    }

    private static async Task<TcpClient?> TryConnectAsync(
        IPAddress ip, int port, TimeSpan timeout, int retries, CancellationToken ct)
    {
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var tcp = new TcpClient(ip.AddressFamily);
            try
            {
                await tcp.ConnectAsync(ip, port, ct).AsTask().WaitAsync(timeout, ct);
                return tcp;
            }
            catch (TimeoutException) when (attempt < retries - 1)
            {
                // Таймаут — не отказ. На нестабильных каналах (интернет) пакет мог потеряться,
                // а порт на самом деле открыт: повторная попытка (профилем WAN/AUTO). Отказ (RST) не повторяем.
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
        TcpClient tcp, int port, int timeoutMs, string hostName, CancellationToken ct)
    {
        try
        {
            var stream = tcp.GetStream();
            var buffer = new byte[1024];

            // На классических TLS-портах начинаем сразу с TLS: пассивное чтение тут
            // только ждёт таймаут впустую, а некоторые серверы рвут простаивающее
            // соединение раньше, чем успеет начаться хендшейк.
            var text = TlsPorts.Contains(port)
                ? await TryTlsBannerAsync(tcp, buffer, timeoutMs, hostName, ct)
                : await TryReadAsync(stream, buffer, timeoutMs, ct);

            // Активные промышленные пробы: служба отвечает на запрос своей идентификацией —
            // так определяется реальное устройство, а не догадка по номеру порта.
            if (string.IsNullOrEmpty(text) && !TlsPorts.Contains(port) && port == 502)
            {
                // Modbus Read Device Identification (func 0x2B/0x0E)
                var req = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, 0xFF, 0x2B, 0x0E, 0x01, 0x00 };
                text = await ProbeAsync(stream, buffer, req, timeoutMs, ct, ParseModbusIdentification);
            }

            if (string.IsNullOrEmpty(text) && !TlsPorts.Contains(port) && port == 44818)
            {
                // EtherNet/IP ListIdentity — устройство само называет вендора и продукт
                text = await ProbeAsync(stream, buffer, CipListIdentityProbe, timeoutMs, ct, ParseCipIdentity);
            }

            // HTTP-serveр не шлёт приветствие сам — спрашиваем на любом открытом порту:
            // так находятся веб-морды на нестандартных портах (перенесённые службы, пробросы).
            if (string.IsNullOrEmpty(text))
            {
                var request = Encoding.ASCII.GetBytes("HEAD / HTTP/1.0\r\n\r\n");
                await stream.WriteAsync(request, 0, request.Length, ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
                text = ExtractHttpBanner(await TryReadAsync(stream, buffer, timeoutMs, ct) ?? "");
            }

            // TLS: шифрованные службы не дают читаемого баннера, но показывают сертификат.
            // Вторая попытка — для TLS на нестандартном порту (после неудачного HTTP).
            if (string.IsNullOrEmpty(text) && !TlsPorts.Contains(port))
                text = await TryTlsBannerAsync(tcp, buffer, timeoutMs, hostName, ct);

            return Sanitize(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> ProbeAsync(
        Stream stream, byte[] buffer, byte[] request, int timeoutMs, CancellationToken ct,
        Func<byte[], int, string?> parser)
    {
        try
        {
            await stream.WriteAsync(request, 0, request.Length, ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            var n = await stream.ReadAsync(buffer, 0, buffer.Length, ct)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            return n > 0 ? parser(buffer, n) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> TryTlsBannerAsync(TcpClient tcp, byte[] buffer, int timeoutMs, string hostName, CancellationToken ct)
    {
        try
        {
            // SNI: CDN и виртуальные хосты требуют имя хоста в хендшейке
            var sni = Uri.CheckHostName(hostName) == UriHostNameType.Dns ? hostName : string.Empty;

            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: true, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
            }, ct).WaitAsync(TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 6000)), ct);

            var parts = new List<string>();

            if (ssl.RemoteCertificate is { } remote)
            {
                try
                {
                    var cert = new X509Certificate2(remote);
                    var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
                    parts.Add($"TLS: {cn}");
                }
                catch
                {
                    parts.Add($"TLS: {remote.Subject}");
                }
            }

            // HTTPS поверх TLS — заодно серверный заголовок
            var head = Encoding.ASCII.GetBytes("HEAD / HTTP/1.0\r\n\r\n");
            var http = ExtractHttpBanner(await ProbeAsync(ssl, buffer, head, timeoutMs, ct, (b, n) => Encoding.ASCII.GetString(b, 0, n)) ?? "");
            if (!string.IsNullOrEmpty(http))
                parts.Insert(0, http);

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>EtherNet/IP ListIdentity → «Vendor · Product» (так Shodan видит Omron NX1P2).</summary>
    private static string? ParseCipIdentity(byte[] d, int n)
    {
        // заголовок инкапсуляции 24 б; далее: itemCount(2), itemType(2), itemLen(2),
        // protoVer(2), sockaddr(16), vendorId(2), deviceType(2), productCode(2),
        // revision(2), status(2), serial(4), nameLen(1), name
        if (n < 64 || d[0] != 0x63)
            return null;
        var vendor = BitConverter.ToUInt16(d, 48);
        var nameLen = d[62];
        if (63 + nameLen > n)
            return null;
        var name = Encoding.ASCII.GetString(d, 63, nameLen);
        if (name.Length == 0)
            return null;
        var vendorName = vendor switch
        {
            1 => "Allen-Bradley/Rockwell",
            47 => "Omron",
            _ => $"VendorId {vendor}",
        };
        return $"{vendorName} · {name}";
    }

    /// <summary>Modbus Read Device Identification → «Vendor Product Rev».</summary>
    private static string? ParseModbusIdentification(byte[] d, int n)
    {
        if (n < 14 || d[7] != 0x2B)
            return null;
        var parts = new List<string>();
        var i = 13; // unit(1)+func(1)+mei(1)+rdic(1)+conformity(1)+more(1)+nextObj(1)+numObj(1) = с 8 по 14
        var count = Math.Min((int)d[12], 8);
        for (var k = 0; k < count && i + 2 <= n; k++)
        {
            var id = d[i];
            var len = d[i + 1];
            i += 2;
            if (i + len > n)
                break;
            var value = Encoding.ASCII.GetString(d, i, len);
            i += len;
            if (id <= 2 && value.Length > 0)
                parts.Add(value); // 0 = VendorName, 1 = ProductCode, 2 = Revision
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>SNMP-ответ → sysDescr: первая содержательная строка из OCTET STRING.</summary>
    private static string? ParseSnmpSysDescr(byte[] d, int n)
    {
        for (var i = 0; i + 6 < n; i++)
        {
            if (d[i] != 0x04)
                continue;
            var len = d[i + 1];
            if (len < 6 || i + 2 + len > n || len > 200)
                continue;
            var candidate = Encoding.UTF8.GetString(d, i + 2, len);
            var printable = candidate.Count(c => !char.IsControl(c));
            if (printable >= len * 0.9)
                return candidate.Trim();
        }
        return null;
    }

    private static async Task<string?> TryReadAsync(
        Stream stream, byte[] buffer, int timeoutMs, CancellationToken ct)
    {
        // Важно: чтение отменяется по таймауту через токен, а не через WaitAsync —
        // брошенный «висючий» ReadAsync остаётся на сокете и ворует байты ответа
        // следующих запросов (ломал TLS-хендшейк и HTTP-пробы).
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var n = await stream.ReadAsync(buffer, 0, buffer.Length, timeoutCts.Token);
            return n > 0 ? Encoding.UTF8.GetString(buffer, 0, n) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null; // таймаут чтения или отмена сканирования — баннера нет
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
            // Промышленные порты опрашиваются осмысленным запросом: устройство отвечает
            // своей идентификацией (SNMP sysDescr, EtherNet/IP ListIdentity), а не тишиной.
            var probe = port switch
            {
                161 => SnmpSysDescrProbe,
                44818 => CipListIdentityProbe,
                _ => UdpProbe,
            };

            udp.Connect(ip, port);
            await udp.SendAsync(probe, probe.Length);
            var result = await udp.ReceiveAsync().WaitAsync(timeout, ct);

            string? banner = null;
            if (port == 161)
                banner = ParseSnmpSysDescr(result.Buffer, result.Buffer.Length);
            else if (port == 44818)
                banner = ParseCipIdentity(result.Buffer, result.Buffer.Length);

            banner ??= Sanitize(Encoding.UTF8.GetString(result.Buffer, 0, Math.Min(result.Buffer.Length, 256)));
            return ("open", banner);
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
