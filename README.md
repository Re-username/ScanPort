# Сканер открытых портов

Проверка открытых TCP/UDP-портов на устройстве в локальной сети: TCP-сканирование соединений, UDP-пробы, захват баннеров служб, ICMP-ping, сортировка результатов, журнал событий, тёмная/светлая тема.

## Структура

- `src/PortScanner/` — веб-клиент (Blazor WebAssembly). Общий UI для всех платформ
- `src/PortScanner.Server/` — самодостаточный сервер для Windows: **один файл** `ScanPort.exe` (win-x64, не требует установки .NET) поднимает API сканирования и отдаёт веб-интерфейс. Флаги: `--no-browser` (не открывать браузер), `--urls` (другой адрес/порт)
- `src/PortScanner.Maui/` — приложение для **Android** (MAUI Blazor Hybrid): тот же UI и тот же движок сканирования, локальный API внутри приложения. Сборка APK — см. ниже
- `tools/` — иконки, тестовые скрипты (`tcp-banner-test.ps1`, `udp-echo-test.ps1` — локальные заглушки служб для проверки захвата баннеров) и `build-release.ps1`
- `release/` — готовые сборки текущей версии; история версий — на [странице Releases](../../releases)

## Сборка

```bash
dotnet build src/PortScanner/PortScanner.csproj -c Release          # клиент
dotnet build src/PortScanner.Server/PortScanner.Server.csproj -c Release  # сервер

# полный релиз (веб-zip + exe одним файлом) в release/:
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1

# APK для Android (нужны JDK 17, Android SDK и workload maui-android):
powershell -ExecutionPolicy Bypass -File tools/build-android.ps1
```

## Планы

- Приложение для Android — после основного релиза
