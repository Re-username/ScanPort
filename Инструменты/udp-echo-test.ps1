# UDP echo listener for testing: receives one datagram and echoes it back.
param([int]$Port = 5020, [int]$AliveSeconds = 20)
$u = New-Object System.Net.Sockets.UdpClient $Port
$u.Client.ReceiveTimeout = $AliveSeconds * 1000
try {
    $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    $bytes = $u.Receive([ref]$remote)
    $u.Send($bytes, $bytes.Length, $remote) | Out-Null
    Start-Sleep 2
} catch { }
$u.Close()
