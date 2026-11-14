# Сканер открытых портов

Проверка открытых TCP/UDP-портов на устройстве в локальной сети: TCP-сканирование соединений, UDP-пробы, захват баннеров служб, ICMP-ping, сортировка результатов, журнал событий, тёмная/светлая тема.

## Структура

- `src/PortScanner/` — веб-клиент (Blazor WebAssembly)
- `src/PortScanner.Server/` — самодостаточный сервер для Windows: поднимает API сканирования и отдаёт веб-интерфейс. Распакуйте сборку из `release/` и запустите `ScanPort.exe` — браузер откроется сам (можно отключить флагом `--no-browser`, адрес меняется через `--urls`)
- `tools/` — иконки, тестовые скрипты (`tcp-banner-test.ps1`, `udp-echo-test.ps1` — локальные заглушки служб для проверки захвата баннеров) и `build-release.ps1`
- `release/` — готовые сборки текущей версии; история версий — на [странице Releases](../../releases)

## Сборка

```bash
dotnet build src/PortScanner/PortScanner.csproj -c Release          # клиент
dotnet build src/PortScanner.Server/PortScanner.Server.csproj -c Release  # сервер

# полный релиз (веб-zip + self-contained exe) в release/:
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
```

## Планы

- Приложение для Android — после основного релиза
