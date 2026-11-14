# Релизы

Здесь лежат готовые сборки **текущей** версии. История версий и старые сборки — на странице Releases:
https://github.com/Re-username/ScanPort/releases

- `PortScanner-web-v*.zip` — статический веб-сайт. Сам по себе не сканирует: нужен хост, который отвечает на `api/scan/*` (например, сервер из `src/PortScanner.Server`).
- `ScanPort-windows-x64-v*.zip` — самодостаточное приложение для Windows (не требует установки .NET). Распаковать и запустить `ScanPort.exe`: поднимется API и откроется веб-интерфейс в браузере.

Пересборка: `powershell -ExecutionPolicy Bypass -File ../tools/build-release.ps1` (из корня репозитория).
