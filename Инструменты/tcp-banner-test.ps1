# TCP greeting listener for banner test: greets every client with an FTP-style banner.
param([int]$Port = 5021, [int]$AliveSeconds = 30)
$l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
$l.Start()
$deadline = (Get-Date).AddSeconds($AliveSeconds)
$msg = [Text.Encoding]::ASCII.GetBytes("220 Test FTP Service Ready`r`n")
while ((Get-Date) -lt $deadline) {
    if ($l.Pending()) {
        $client = $l.AcceptTcpClient()
        $client.GetStream().Write($msg, 0, $msg.Length)
        Start-Sleep -Milliseconds 400
        $client.Close()
    } else {
        Start-Sleep -Milliseconds 100
    }
}
$l.Stop()
