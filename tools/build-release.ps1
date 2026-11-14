# Сборка артефактов релиза. Запуск из корня репозитория:
#   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$ver = '1.2.0'

Write-Host "1/4 Публикация веб-клиента..."
dotnet publish (Join-Path $root 'src/PortScanner/PortScanner.csproj') -c Release -o (Join-Path $root 'build/web') | Out-Null

Write-Host "2/4 Копирование клиента в wwwroot сервера..."
New-Item -ItemType Directory -Force (Join-Path $root 'src/PortScanner.Server/wwwroot') | Out-Null
robocopy (Join-Path $root 'build/web') (Join-Path $root 'src/PortScanner.Server/wwwroot') /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "robocopy завершился с ошибкой $LASTEXITCODE" }

Write-Host "3/4 Публикация self-contained сервера (win-x64)..."
dotnet publish (Join-Path $root 'src/PortScanner.Server/PortScanner.Server.csproj') -c Release -r win-x64 --self-contained -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $root 'build/ScanPort-windows-x64') | Out-Null

Write-Host "4/4 Архивы в release/..."
Compress-Archive -Path (Join-Path $root 'build/web/*') -DestinationPath (Join-Path $root "release/PortScanner-web-v$ver.zip") -Force
Compress-Archive -Path (Join-Path $root 'build/ScanPort-windows-x64/*') -DestinationPath (Join-Path $root "release/ScanPort-windows-x64-v$ver.zip") -Force

Write-Host "Готово:"
Write-Host "  release/PortScanner-web-v$ver.zip"
Write-Host "  release/ScanPort-windows-x64-v$ver.zip"
