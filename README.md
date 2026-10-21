# Create Launcher

Портативный лаунчер для **Minecraft 1.21.1 · NeoForge 21.1.264**.

[![Build and release](https://github.com/pressftorun/minecraftlauncher/actions/workflows/build.yml/badge.svg)](https://github.com/pressftorun/minecraftlauncher/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/pressftorun/minecraftlauncher)](https://github.com/pressftorun/minecraftlauncher/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Скачать

| Система | Лаунчер 0.3.1 |
| --- | --- |
| Windows x64 | [Скачать ZIP](https://github.com/pressftorun/minecraftlauncher/releases/download/v0.3.1/CreateLauncher-0.3.1-win-x64.zip) |
| Linux x64 | [Скачать ZIP](https://github.com/pressftorun/minecraftlauncher/releases/download/v0.3.1/CreateLauncher-0.3.1-linux-x64.zip) |

[Все релизы](https://github.com/pressftorun/minecraftlauncher/releases) · [Изменения](CHANGELOG.md) · [Клиентская сборка](https://github.com/pressftorun/minecraftversion)

.NET и Python устанавливать не нужно. Клиент, Java 21 и NeoForge скачиваются автоматически. Контрольные суммы архивов находятся в `SHA256SUMS.txt` рядом с файлами релиза.

## Первый запуск

1. Распакуйте **весь архив** в доступную для записи папку. Соседние DLL/SO и папку `Assets` оставьте рядом с программой.
2. Windows: запустите `CreateLauncher.exe`. Linux: выполните `chmod +x CreateLauncher`, затем `./CreateLauncher`.
3. Выберите профиль по нику, Ely.by или TLauncher.org и нажмите **«Установить и играть»**.

Нужен интернет. На Linux требуется графическая сессия и библиотеки X11/fontconfig/OpenGL. Файлы игры и настройки сохраняются в `data/` рядом с лаунчером. Сервер `46.146.228.125:25565` добавляется в мультиплеер с сохранением существующего списка.

Карточка сервера показывает доступность и число игроков, обновляется раз в 30 секунд и по кнопке. Кнопка BlueMap открывает [карту мира](http://46.146.228.125:8100/).

## Обновление и восстановление

Закройте игру, сохраните резервную копию `data/` и распакуйте новый лаунчер поверх старого. Нажмите **«Проверить файлы»**, чтобы обновить пакет и восстановить Minecraft, Java и NeoForge.

Клиент загружается из последнего стабильного [релиза minecraftversion](https://github.com/pressftorun/minecraftversion/releases/latest). Лаунчер сверяет SHA-256 и повторно использует архив с совпадающим хешем. Устаревшие неизменённые моды пакета удаляются; дополнительные моды и изменённые конфиги игрока сохраняются. Новые значения в изменённых конфигах нужно переносить вручную.

Во время работы игры проверка и обновление заблокированы. Тайм-аут загрузки файла — пять минут. Журнал игры: `data/logs/game.log`.

## Профили и настройки

- По нику: 3–16 латинских букв, цифр или `_`. Возможность входа зависит от настроек сервера.
- Ely.by и TLauncher.org: данные проверяются сервисом. Пароли и токены на диск не записываются; после перезапуска лаунчера требуется повторный вход. Ely.by также требует поддержки на сервере.
- Можно переключаться между сохранёнными профилями. Головы скинов загружаются по сети и кэшируются локально.
- Память: 2048–16384 МБ. Java 21 x64 скачивается автоматически; можно выбрать установленную Java вручную. Внутренние пути сохраняются относительно папки лаунчера.

В `data/profile.json` и `data/profiles.json` сохраняются имена, UUID и провайдеры. Не публикуйте папку `data/`: игра и моды могут записывать туда личные сведения.

## Источник клиента

`launcher.json` содержит имя, сервер, версии и адреса загрузки:

- `pack_release_url` — GitHub API последнего стабильного релиза клиента. Публичный доступ, токен не нужен.
- `pack_url` + `pack_sha256` — явный HTTPS-адрес архива и SHA-256; имеют приоритет над GitHub-релизом.
- Для установки из соседнего `client.zip` очистите `pack_release_url` и `pack_url`.
- `bluemap_url` — адрес карты мира.

Моды, шейдеры, конфиги и меню игры находятся в отдельном [репозитории клиента](https://github.com/pressftorun/minecraftversion). Их нет в исходниках и ZIP-архивах лаунчера.

## Разработка

**C# · .NET SDK 10 · Avalonia 12**. Для упаковки нужен PowerShell 7.

```powershell
dotnet restore desktop/CreateLauncher.csproj --configfile NuGet.Config
dotnet run --project desktop/CreateLauncher.csproj
dotnet run --project desktop/CreateLauncher.csproj -- --self-test
pwsh ./build-native.ps1 -Runtime win-x64
pwsh ./build-native.ps1 -Runtime linux-x64
```

Архивы появляются в `dist/CreateLauncher-<версия>-<платформа>.zip`. По умолчанию пакет клиента скачивается при установке. Для специальной сборки с вложенным пакетом передайте `-PackArchive <путь к client.zip>`.

`desktop/MainWindow.axaml` — интерфейс, `LauncherService.cs` — установка и запуск, `SelfTests.cs` — встроенные проверки. GitHub Actions собирает и тестирует Windows/Linux и публикует релиз по версии из `CreateLauncher.csproj`, если такой релиз ещё не существует.

Windows проверен реальным запуском игры; Linux проходит сборку и встроенные тесты в CI. Вход на сервер и графический запуск Linux требуют отдельной проверки.

## Лицензия

Оригинальный код и оформление: [MIT](LICENSE). Зависимости, моды и шейдеры сохраняют лицензии авторов — [NOTICE.md](NOTICE.md).
