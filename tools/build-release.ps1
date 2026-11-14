﻿# Сборка артефактов релиза. Запуск из корня репозитория:
#   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$ver = '1.4.9'

# NuGet-кэш для MAUI-сборок (как в build-android.ps1)
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = 'D:\nuget-packages' }

Write-Host "1/5 Публикация веб-клиента..."
Remove-Item -Recurse -Force (Join-Path $root 'build/web') -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src/PortScanner/PortScanner.csproj') -c Release -o (Join-Path $root 'build/web') | Out-Null

# dotnet publish кладёт сайт в build/web/wwwroot — нормализуем путь к index.html
$webOut = Join-Path $root 'build/web'
$siteDir = Join-Path $webOut 'wwwroot'
if (-not (Test-Path (Join-Path $siteDir 'index.html'))) { $siteDir = $webOut }
if (-not (Test-Path (Join-Path $siteDir 'index.html'))) { throw "index.html не найден в выводе публикации клиента" }

Write-Host "2/5 Копирование клиента в wwwroot сервера..."
New-Item -ItemType Directory -Force (Join-Path $root 'src/PortScanner.Server/wwwroot') | Out-Null
robocopy $siteDir (Join-Path $root 'src/PortScanner.Server/wwwroot') /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "robocopy завершился с ошибкой $LASTEXITCODE" }

Write-Host "3/5 Публикация приложения для Windows (MAUI, нативное окно)..."
Remove-Item -Recurse -Force (Join-Path $root 'build/ScanPort-app') -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src/PortScanner.Maui/PortScanner.Maui.csproj') -f net10.0-windows10.0.19041.0 -c Release -o (Join-Path $root 'build/ScanPort-app') | Out-Null

Write-Host "4/5 Сборка одного exe (SFX-лаунчер, как у FileChat)..."
$sfx = Join-Path $root 'build/sfx'
Remove-Item -Recurse -Force $sfx -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $sfx | Out-Null
Copy-Item (Join-Path $root 'tools/sfxstub/*') $sfx -Recurse -Force
Push-Location (Join-Path $root 'build/ScanPort-app')
& 'D:\android-tools\7za.exe' a -tzip -mx=5 (Join-Path $sfx 'app.zip') '*' -r '-x!*.pdb' | Out-Null
$zipCode = $LASTEXITCODE
Pop-Location
if ($zipCode -gt 1) { throw "7za завершился с ошибкой $zipCode" }
dotnet build (Join-Path $sfx 'sfxstub.csproj') -c Release | Out-Null
Copy-Item (Join-Path $sfx 'bin/Release/net472/ScanPort.exe') (Join-Path $root "release/ScanPort-app-windows-x64-v$ver.exe") -Force

Write-Host "5/5 Публикация сервера (win-x64, один файл, для хостинга в LAN)..."
Remove-Item -Recurse -Force (Join-Path $root 'build/ScanPort-server-windows-x64') -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src/PortScanner.Server/PortScanner.Server.csproj') -c Release -r win-x64 --self-contained -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $root 'build/ScanPort-server-windows-x64') | Out-Null
# ANCM-модуль для IIS в локальный exe не нужен
Remove-Item -Force (Join-Path $root 'build/ScanPort-server-windows-x64/aspnetcorev2_inprocess.dll') -ErrorAction SilentlyContinue

Write-Host "Архивы в release/..."
Compress-Archive -Path (Join-Path $siteDir '*') -DestinationPath (Join-Path $root "release/PortScanner-web-v$ver.zip") -Force
Compress-Archive -Path (Join-Path $root 'build/ScanPort-server-windows-x64/*') -DestinationPath (Join-Path $root "release/ScanPort-server-windows-x64-v$ver.zip") -Force

Write-Host "Готово:"
Write-Host "  release/ScanPort-app-windows-x64-v$ver.exe"
Write-Host "  release/PortScanner-web-v$ver.zip"
Write-Host "  release/ScanPort-server-windows-x64-v$ver.zip"
