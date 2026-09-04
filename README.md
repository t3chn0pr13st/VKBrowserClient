# VkBrowserClient

[![ci](https://github.com/t3chn0pr13st/VKBrowserClient/actions/workflows/ci.yml/badge.svg)](https://github.com/t3chn0pr13st/VKBrowserClient/actions/workflows/ci.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Небольшой **неофициальный** клиент `vk.ru` на C# / .NET 10. Не использует публичное API ВК
(с регистрацией приложения) — вместо этого повторяет то, что делает веб-клиент в браузере:
вход через VK ID, сохранение cookie-сессии и обновление короткоживущего web-токена. После
однократного входа работает **в фоне без UI** (например, на сервере).

> ⚠️ Личный/исследовательский инструмент для доступа к **своему** аккаунту. Опирается на
> недокументированное поведение веб-клиента ВК, которое может измениться в любой момент.
> Соблюдайте правила пользования ВКонтакте.

## Возможности

- 🔐 Вход через управляемый браузер (Playwright): пароль/2FA/капчу вводите вы сами.
- 💾 Сохранение cookie-сессии и **авто-обновление web-токена** из cookies (фон без UI).
- 💬 Чтение диалогов и истории сообщений (с фотографиями).
- 📤 Отправка сообщений с медиа: фото, документы (файлы/GIF/аудиосообщения), видео.
- 🖼 Публикация записей на личной стене и в сообществах с фото, каруселями, документами и видео.
- 🎬 Публикация клипов (VK Клипы) — полный флоу `shortVideo.*`, сохраняемые этапы загрузки, изменение описания и проверка обработки.
- 📼 Длинные VOD — сохраняемые этапы `video.save` → CDN upload → подтверждённый `privacy_view=by_link`, без обязательной записи на стене.
- 🔴 Прямые трансляции VK Видео — typed lifecycle `video.startStreaming` / `stopStreaming`, категории, метаданные, текущие зрители/просмотры, статус/запись, удаление и обложки.
- 🔒 Live-SDK слоты сообществ — создание с заданной permission, безопасный patch существующего слота через полный `PUT` и обязательный readback фактических настроек.
- Обложка live-эфира передаёт полный JSON-ответ upload-сервера в `video.saveUploadedThumb.thumb_json`, не логируя его значения.
- 🌊 Потоковая загрузка медиа без чтения больших файлов целиком в память, с безопасным повторным открытием при ретраях.
- 📦 Экспорт/импорт сессии (файл или base64) для переноса на сервер без браузера.
- 🧩 Готов к подключению как NuGet-пакет из публичных GitHub Releases.

## Как это работает (кратко)

1. Вход VK ID ставит cookie-сессию (главное — httpOnly `remixsid` на `.vk.ru`) — долговременный ключ.
2. Из cookies мятится короткоживущий web-токен (`~18 мин`): `POST login.vk.ru/?act=web_token`.
3. Методы зовутся на `web.api.vk.ru/method/*` с этим токеном (`client_id=6287487`, `v=5.282`).

Подробный разбор протокола (включая загрузку фото) — в [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Требования

- .NET SDK 10
- Chromium для Playwright (нужен только для входа; ставится автоматически при первом запуске)

## Быстрый старт

```bash
dotnet build
dotnet run --project samples/VkBrowserClient.ConsoleSample          # 10 последних диалогов
```

Первый запуск откроет окно браузера — пройдите вход VK ID. Дальше сессия сохраняется и
браузер не открывается.

Другие команды примера:

```bash
dotnet run --project samples/VkBrowserClient.ConsoleSample -- history 100 20
dotnet run --project samples/VkBrowserClient.ConsoleSample -- send 100 "Привет" --photo pic.jpg
dotnet run --project samples/VkBrowserClient.ConsoleSample -- post "Мой пост" --photo cover.jpg
dotnet run --project samples/VkBrowserClient.ConsoleSample -- export session.portable.json
```

## Пример в коде

```csharp
using VkBrowserClient;

await using var client = VkClient.Create("session.json", o => o.StatusCallback = Console.WriteLine);
await client.EnsureAuthenticatedAsync();

var dialogs = await client.Messages.GetConversationsAsync(10);
foreach (var c in dialogs.Items) Console.WriteLine($"[{c.PeerType}] {c.Title}");

await client.Messages.SendMessageAsync(peerId: 100, text: "Привет", photos: new[] { VkImage.FromFile("pic.jpg") });
await client.Wall.PostAsync("Пост с картинкой", new[] { VkImage.FromFile("cover.jpg") });
await client.Wall.PostToCommunityAsync(12345, "Пост", [VkAttachmentSource.Photo("cover.jpg")]);

var video = await client.Videos.UploadFromFileAsync("class.mp4", new VkVideoUploadOptions
{
    GroupId = 12345,
    Name = "Запись класса",
    ViewPrivacy = VkLivePrivacy.ByLink,
});

var live = await client.Live.StartStreamingAsync(new VkLiveStartOptions
{
    Name = "Практика в прямом эфире",
    GroupId = 12345,
    Publish = false,
    PostToWall = false,
});
Console.WriteLine($"RTMP server: {live.Ingest.Url}");
// live.Ingest.Key — секрет; не выводите его в обычный лог.
```

Для фонового worker публикацию Клипа можно разделить на сохраняемые этапы
`CreateUploadSessionAsync` → `UploadAsync` → `CompletePublishAsync`: после рестарта
продолжается тот же Clip id без повторного `shortVideo.create`.

Полное руководство по API и CLI — в [docs/USAGE.md](docs/USAGE.md).

## Документация

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — вскрытый протокол VK-веба (auth, токен, методы, загрузка фото).
- [docs/USAGE.md](docs/USAGE.md) — API и команды CLI с примерами.
- [docs/SERVER.md](docs/SERVER.md) — перенос сессии на сервер и фоновая работа без браузера.
- [docs/NUGET.md](docs/NUGET.md) — сборка и подключение NuGet-пакета из GitHub Releases.

## Структура

```
src/VkBrowserClient/            — библиотека (net10.0)
  VkClient.cs                   — фасад: вход, сессия, .Messages, .Wall, .Clips, .Live, экспорт/импорт
  VkClientOptions.cs            — настройки (по умолчанию = как у веб-клиента)
  Api/VkWebApi.cs               — web_token + вызовы web.api.vk.ru + загрузка фото
  Auth/PlaywrightAuthenticator  — интерактивный вход через браузер
  Session/                      — VkSession, ISessionStore, FileSessionStore, сериализация
  Services/                     — Messages, Wall/Media, Clips и typed VkLiveService
  Messages/                     — разбор ответов (диалоги, история)
  Models/                       — Conversation, VkMessage, VkImage, WallPostResult, …
samples/VkBrowserClient.ConsoleSample/   — консольный пример со всеми командами
docs/                           — документация
```

## Безопасность

Файл сессии = **пароль от аккаунта** (cookies + токен). `FileSessionStore` на Unix создаёт его
сразу с правами `0600`. Не коммитьте сессии (уже в `.gitignore`). Для продакшена рассмотрите
шифрование (свой `ISessionStore`). Подробнее — в [docs/SERVER.md](docs/SERVER.md).

## Ограничения

- Опирается на недокументированные эндпоинты веб-ВК — возможны поломки при изменениях на их стороне.
- Реалтайм (LongPoll `queuev4.vk.ru`) не реализован — только запрос/ответ.
- При чтении разбираются только фото-вложения; прочие типы лишь подсчитываются.
- Клипы: потоковый одиночный POST (крупные клипы веб грузит чанками); выбор кадра обложки не реализован.

## Лицензия

[MIT](LICENSE).
