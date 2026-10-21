# Разработка Create Launcher

## Подготовка

Нужны .NET SDK 10, PowerShell 7 и Git. Клиентская сборка хранится отдельно в [minecraftversion](https://github.com/pressftorun/minecraftversion).

```powershell
git clone https://github.com/pressftorun/minecraftlauncher.git
cd minecraftlauncher
dotnet restore desktop/CreateLauncher.csproj --configfile NuGet.Config
dotnet run --project desktop/CreateLauncher.csproj
```

## Проверка изменений

```powershell
dotnet run --project desktop/CreateLauncher.csproj -c Release -- --self-test
pwsh ./build-native.ps1 -Runtime win-x64
pwsh ./build-native.ps1 -Runtime linux-x64
```

Для графических изменений приложите снимок окна без личных данных. Установку, восстановление файлов и запуск игры проверяйте на отдельной копии. Сборка и self-test не подтверждают успешный вход на сервер.

## Pull request

Опишите проблему, итоговое поведение и выполненные проверки. Держите одну задачу в одном PR. Изменение версий или источников загрузки требует проверки чистой установки и обновления существующей.

Не добавляйте `data/`, архивы релизов, локальные логи, секреты или бинарные моды. Не отключайте проверки путей, хешей и HTTPS ради устранения ошибки.

Пользовательские изменения описывайте в [CHANGELOG.md](CHANGELOG.md). Порядок выпуска — [docs/RELEASING.md](docs/RELEASING.md). Об уязвимостях сообщайте по [SECURITY.md](SECURITY.md).
