# Сканер открытых портов

Проверка открытых TCP/UDP-портов на устройстве в локальной сети: TCP-сканирование соединений, UDP-пробы, захват баннеров служб, ICMP-ping, сортировка результатов, журнал событий, тёмная/светлая тема.

## Структура

- `src/PortScanner/` — веб-клиент (Blazor WebAssembly). Общий UI для всех платформ
- `src/PortScanner.Maui/` — **приложение** для Windows и Android (MAUI Blazor Hybrid, как FileChat): нативное окно, тот же UI и движок сканирования, локальный API внутри приложения
- `src/PortScanner.Server/` — сервер для хостинга в локальной сети: **один файл** `ScanPort.exe` (win-x64, не требует .NET) поднимает API и отдаёт веб-интерфейс браузеру. Флаги: `--no-browser`, `--urls`
- `tools/` — иконки, тестовые скрипты (`tcp-banner-test.ps1`, `udp-echo-test.ps1` — локальные заглушки служб для проверки захвата баннеров) и скрипты сборки
- `release/` — готовые сборки текущей версии; история версий — на [странице Releases](../../releases)

## Сборка

```bash
dotnet build src/PortScanner/PortScanner.csproj -c Release          # клиент
dotnet build src/PortScanner.Server/PortScanner.Server.csproj -c Release  # сервер

# полный релиз (приложение для Windows, сервер одним файлом, веб-zip) в release/:
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1

# APK для Android (нужны JDK 17, Android SDK и workload maui-android):
powershell -ExecutionPolicy Bypass -File tools/build-android.ps1
```

## Планы

- Приложение для Android — после основного релиза
