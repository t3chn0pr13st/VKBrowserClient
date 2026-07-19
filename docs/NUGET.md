# NuGet-пакет

Пакет `VkBrowserClient` предназначен для подключения к другим проектам. Он остаётся **приватным**:
публикуется в GitHub Packages в рамках приватного репозитория (не в публичный nuget.org).

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

## Публикация в GitHub Packages (приватно)

Автоматически через GitHub Actions — workflow-шаблоны лежат в [`ci/`](../ci/) и активируются
одной командой (см. [ci/README.md](../ci/README.md)); их нужно перенести в `.github/workflows/`.
После активации публикация происходит при пуше тега версии:

```bash
git tag v0.2.0
git push origin v0.2.0
# Actions соберёт и запушит пакет в GitHub Packages владельца репозитория
```

Публикация использует встроенный `GITHUB_TOKEN` (scope `packages: write`) — отдельные секреты не нужны.

## Потребление приватного пакета в другом проекте

1. Создайте Personal Access Token (classic) со scope `read:packages`.
2. Добавьте источник NuGet (замените `OWNER` на владельца репозитория, напр. `t3chn0pr13st`):

   `nuget.config` в вашем проекте:
   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <add key="github" value="https://nuget.pkg.github.com/OWNER/index.json" />
     </packageSources>
     <packageSourceCredentials>
       <github>
         <add key="Username" value="OWNER" />
         <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
       </github>
     </packageSourceCredentials>
   </configuration>
   ```
   (токен лучше подставлять из переменной окружения, не хранить в файле).

3. Подключите пакет:
   ```bash
   dotnet add package VkBrowserClient
   ```

## После установки

Библиотека тянет за собой `Microsoft.Playwright`. Для интерактивного входа нужен браузер
Chromium — он ставится автоматически при первом запуске (или вручную:
`pwsh <output>/playwright.ps1 install chromium`).
