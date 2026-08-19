# NuGet-пакет

Пакет `VkBrowserClient` предназначен для подключения к другим проектам. Готовые `.nupkg` и
`.snupkg` прикладываются к [GitHub Releases](https://github.com/t3chn0pr13st/VKBrowserClient/releases),
а в публичный `nuget.org` пакет пока не публикуется.

## Локальная сборка пакета

```bash
dotnet pack src/VkBrowserClient/VkBrowserClient.csproj -c Release -o artifacts
# -> artifacts/VkBrowserClient.<версия>.nupkg (+ .snupkg с символами)
```

Подключить локально можно, добавив папку `artifacts` как источник:

```bash
dotnet nuget add source "/путь/к/VKBrowserClient/artifacts" --name vkbc-local
dotnet add <ваш-проект> package VkBrowserClient
```

## Подключение пакета из GitHub Releases

1. Скачайте `VkBrowserClient.<version>.nupkg` из нужного релиза.
2. Положите пакет в локальный каталог, например `vendor/vk-browser-client`.
3. Добавьте этот каталог как NuGet source и подключите пакет:

```bash
dotnet nuget add source "$PWD/vendor/vk-browser-client" --name vkbc-release
dotnet add <ваш-проект> package VkBrowserClient --version <version>
```

Для воспроизводимой сборки зафиксируйте версию и SHA-256 скачанного пакета в consuming-проекте.

## После установки

Библиотека тянет за собой `Microsoft.Playwright`. Для интерактивного входа нужен Chromium;
при необходимости установите его командой `pwsh <output>/playwright.ps1 install chromium`.

## Выпуск пакета — локально, не через CI

**GitHub Actions у аккаунта не выполняются: биллинг выключен и включать его не планируется.**
Workflow здесь падает, не начав работу, поэтому пакет собирается и выпускается с машины
разработчика:

```bash
dotnet test VKBrowserClient.slnx -c Release
dotnet pack src/VkBrowserClient/VkBrowserClient.csproj -c Release -o artifacts -p:Version=X.Y.Z
gh release create vX.Y.Z artifacts/*.nupkg artifacts/*.snupkg --generate-notes
```

Потребитель — KundaliniHub — берёт пакет не из фида, а из `.nupkg`, лежащего в его репозитории
и запиннованного по версии и SHA-256. После выпуска там надо обновить пин:

```bash
cd ~/Projects/KundaliniHub
./scripts/update-pinned-package.sh VkBrowserClient X.Y.Z ~/Projects/VKBrowserClient/artifacts
```

`dotnet pack` недетерминирован: пересборка той же версии даёт `.nupkg` с другим SHA-256.
Копируйте в Hub файл из того же `artifacts/`, что выложили в релиз, иначе сборка Hub упадёт
на несовпадении хеша.
