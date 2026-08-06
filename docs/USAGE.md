# Использование

## Установка

Добавьте пакет из [GitHub Releases](NUGET.md) или сошлитесь на проект.
Для интерактивного входа нужен браузер Chromium от Playwright — при первом запуске он
скачивается автоматически.

## Быстрый старт

```csharp
using VkBrowserClient;

await using var client = VkClient.Create("session.json", o => o.StatusCallback = Console.WriteLine);

// 1-й запуск откроет браузер для входа VK ID. Дальше — тихо, токен обновляется сам.
await client.EnsureAuthenticatedAsync();
Console.WriteLine($"user_id = {client.UserId}");
```

`peer_id` в примерах ниже: `id` пользователя, отрицательный `id` сообщества
или `2000000000 + chat_id` для бесед.

## Диалоги

```csharp
var page = await client.Messages.GetConversationsAsync(count: 10);
Console.WriteLine($"Всего бесед: {page.TotalCount}");
foreach (var c in page.Items)
    Console.WriteLine($"[{c.PeerType}] peer={c.PeerId}  {c.Title}  (непроч.: {c.UnreadCount})");
```

## История сообщений (с фото)

```csharp
var history = await client.Messages.GetHistoryAsync(peerId: 100, count: 20);
foreach (var m in history.Items)
{
    var who = m.IsOutgoing ? "Вы" : (m.SenderName ?? $"id{m.FromId}");
    Console.WriteLine($"[{m.Date:g}] {who}: {m.Text}");
    foreach (var photo in m.Photos)
        Console.WriteLine($"    фото {photo.Width}x{photo.Height}: {photo.Url}");
}
```

## Отправка сообщения (с фото)

```csharp
// только текст
await client.Messages.SendMessageAsync(peerId: 100, text: "Привет!");

// стабильный random_id для безопасного повтора фоновой задачи
await client.Messages.SendMessageAsync(
    peerId: 100,
    text: "Привет!",
    photos: null,
    randomId: 184204,
    cancellationToken: cancellationToken);

// текст + несколько фото (загрузятся автоматически)
var images = new[] { VkImage.FromFile("a.jpg"), VkImage.FromFile("b.png") };
long messageId = await client.Messages.SendMessageAsync(peerId: 100, text: "Смотри", photos: images);

// фото из байтов
var img = VkImage.FromBytes(bytes, "photo.jpg");
await client.Messages.SendMessageAsync(peerId: 2000000061, text: null, photos: new[] { img });
```

## Публикация записи на стене (с фото)

```csharp
var post = await client.Wall.PostAsync(
    text: "Пост из VkBrowserClient",
    photos: new[] { VkImage.FromFile("cover.jpg") });

Console.WriteLine($"Опубликовано: {post.Url}");   // https://vk.ru/wall{owner}_{post}
```

Публикация фото, карусели или смешанного набора медиа от имени сообщества:

```csharp
var attachments = new[]
{
    VkAttachmentSource.Photo("first.jpg"),
    VkAttachmentSource.Video("reel.mp4", name: "Reel"),
    VkAttachmentSource.Photo("second.jpg"),
};
var communityPost = await client.Wall.PostToCommunityAsync(
    communityId: 12345,
    text: "Подпись",
    attachments: attachments);

Console.WriteLine(communityPost.Reference); // wall-12345_{post_id}
```

Изменение подписи сохраняет текущие вложения: библиотека сначала читает их через
`wall.getById`, затем передаёт обратно в `wall.edit`. Если вложение нельзя безопасно
восстановить, изменение отменяется с `VkClientException`.

```csharp
await client.Wall.EditTextAsync(communityPost.OwnerId, communityPost.PostId, "Новая подпись");
```

## Документы, видео и клипы

Кроме фото можно прикладывать документы (файлы, GIF, аудиосообщения) и видео (в т.ч.
вертикальные короткие — «клипы») через `VkAttachmentSource`. Медиа загружаются автоматически
тем же способом, что и в веб-клиенте.

```csharp
var attachments = new[]
{
    VkAttachmentSource.Photo("photo.jpg"),
    VkAttachmentSource.Document("report.pdf"),
    VkAttachmentSource.Document("voice.ogg", VkDocType.AudioMessage),
    VkAttachmentSource.Video("clip.mp4", name: "Мой клип"),
};

// в сообщение
await client.Messages.SendMessageAsync(peerId: 100, text: "Файлы и видео", attachments);

// на стену
await client.Wall.PostAsync("Пост с видео", new[] { VkAttachmentSource.Video("clip.mp4") });
```

Из байтов (без файла на диске):

```csharp
VkAttachmentSource.Document(bytes, "file.bin");
VkAttachmentSource.Video(bytes, "clip.mp4", name: "Клип", description: "…");
```

Для файлов из собственного хранилища используйте повторно открываемый потоковый источник.
Фабрика вызывается заново при сетевом ретрае:

```csharp
var source = VkUploadSource.Create(
    fileName: "reel.mp4",
    contentType: "video/mp4",
    length: asset.SizeBytes,
    openReadAsync: async ct => await storage.OpenReadAsync(asset.Path, ct));

var attachment = VkAttachmentSource.Video(source, name: "Reel");
```

Вложение `VkAttachmentSource.Video` — это обычное видео. Отдельная публикация **клипа**
(VK Клипы) — ниже.

## Клипы (VK Клипы)

Публикация клипа повторяет флоу веб-клиента: `shortVideo.create` → загрузка на CDN →
`shortVideo.encodeProgress` (ожидание кодирования) → `shortVideo.edit` (описание/приватность) →
`shortVideo.publish`.

```csharp
var clip = await client.Clips.PublishFromFileAsync("clip.mp4",
    new VkClipPublishOptions { Description = "Мой клип" });
Console.WriteLine(clip.Url);   // https://vk.ru/clip{owner}_{id}
```

Все параметры публикации (соответствуют галочкам в окне VK):

```csharp
await client.Clips.PublishFromFileAsync("clip.mp4", new VkClipPublishOptions
{
    Description = "…",
    View = VkClipPrivacy.Friends,                 // кто может смотреть
    Comment = VkClipPrivacy.OnlyMe,               // кто может комментировать
    AllowDuets = false,                           // разрешить дуэты
    PostToWall = false,                           // также разместить на стене
    GroupId = 12345,                              // от имени сообщества
    PublishAt = DateTimeOffset.Now.AddHours(2),   // отложенная публикация
});

// из байтов:
await client.Clips.PublishAsync(bytes, "clip.mp4", new VkClipPublishOptions { Description = "…" });

// из повторно открываемого потока без буферизации всего ролика:
await client.Clips.PublishAsync(source, new VkClipPublishOptions
{
    GroupId = 12345,
    Description = "…"
});
```

Для долговечного фонового задания разделите операцию на этапы и сохраняйте сессию
между ними (она сериализуется обычным `System.Text.Json`):

```csharp
var session = await client.Clips.CreateUploadSessionAsync(source, options);
SaveEncrypted(JsonSerializer.Serialize(session)); // upload URL является секретом

session = await client.Clips.UploadAsync(session, source);
SaveEncrypted(JsonSerializer.Serialize(session)); // provider id уже известен

var clip = await client.Clips.CompletePublishAsync(session, options);
```

Повтор этапов использует зарезервированный `video{owner}_{id}`: новый Clip не создаётся.

Изменить описание уже опубликованного клипа (приватность и прочее не сбрасываются):

```csharp
await client.Clips.EditDescriptionAsync(ownerId, videoId, "Новое описание");
// или по результату публикации:
await client.Clips.EditDescriptionAsync(clip, "Новое описание");

var status = await client.Clips.GetProcessingStatusAsync(clip);
if (status.State == VkVideoProcessingState.Processing)
    Console.WriteLine("VK ещё обрабатывает клип");
```

Ограничения: минимальный размер файла — **16 КБ**; крупные клипы веб грузит чанками
(здесь потоковый одиночный POST); выбор конкретного кадра
обложки не реализован (берётся кадр по умолчанию).

## Прямые трансляции VK Видео

`client.Live` закрывает полный provider lifecycle типизированными методами, чтобы приложение
не вызывало `video.*` вручную. Контракт соответствует официальной
[VK API schema](https://github.com/VKCOM/vk-api-schema/blob/master/video/methods.json).

Подготовить эфир без записи на стене и сохранить provider anchor вместе с секретными ingest-полями:

```csharp
var live = await client.Live.StartStreamingAsync(new VkLiveStartOptions
{
    Name = "Утренняя практика",
    Description = "Описание эфира",
    GroupId = 12345,                  // null — личный профиль
    CategoryId = 7,                  // из GetCategoriesAsync()
    ViewPrivacy = VkLivePrivacy.All,
    CommentPrivacy = VkLivePrivacy.All,
    DisableComments = false,
    Publish = false,                 // предварительная подготовка
    PostToWall = false,
});

SaveEncrypted(new
{
    live.OwnerId,
    live.VideoId,
    live.AccessKey,
    live.Ingest.Url,
    live.Ingest.Key,
});
```

`AccessKey`, `Ingest.Url`, `Ingest.Key`, `OkmpUrl` и `WebRtcUrl` могут давать доступ к
непубличному объекту или входному потоку. Храните их как секреты и не включайте в логи,
exception telemetry и operator-facing историю. `Reference` и `Url` специально не содержат
`AccessKey`.

У `video.startStreaming` нет idempotency key. При timeout первого create-запроса без ответа
нельзя слепо повторять его: отметьте исход как неопределённый и проведите reconciliation.
Если `video_id` уже сохранён, его можно явно передать как `VkLiveStartOptions.VideoId`, чтобы
адресовать тот же provider object.

Категории, изменение, состояние, остановка и удаление:

```csharp
var categories = await client.Live.GetCategoriesAsync(cancellationToken);
var reference = live.ToReference();

await client.Live.UpdateAsync(reference, new VkLiveUpdateOptions
{
    Name = "Новое название",
    Description = "Новое описание",
    ViewPrivacy = VkLivePrivacy.OnlyMe,
});

var status = await client.Live.GetStatusAsync(reference, cancellationToken);
// Upcoming / Live / Processing / Ready / NotFound / Unknown

var stopped = await client.Live.StopStreamingAsync(reference, cancellationToken);
Console.WriteLine($"Уникальных зрителей: {stopped.UniqueViewers}");

await client.Live.DeleteAsync(reference, cancellationToken); // отдельное подтверждаемое действие приложения
```

Для непубличного объекта всегда используйте `VkLiveReference` с сохранённым `AccessKey`:
`GetStatusAsync` передаст VK полную API-ссылку, но безопасные отображаемые ссылки ключа не содержат.

### Обложка прямого эфира

Удобный полный вызов:

```csharp
await client.Live.SetThumbnailAsync(
    live.ToReference(),
    VkUploadSource.FromFile("cover.jpg", "image/jpeg"),
    cancellationToken);
```

Для durable worker разделите его на сохраняемые этапы. Новый provider id при retry не создаётся,
а повторный upload заново открывает поток того же `VkUploadSource`:

```csharp
var uploadSession = await client.Live.CreateThumbnailUploadSessionAsync(
    live.OwnerId, live.VideoId, cancellationToken);
SaveEncrypted(uploadSession); // UploadUrl подписан и является секретом

var uploaded = await client.Live.UploadThumbnailAsync(uploadSession, cover, cancellationToken);
SaveEncrypted(uploaded);      // ThumbJson предназначен только для saveUploadedThumb

var thumbnail = await client.Live.SaveThumbnailAsync(uploaded, cancellationToken);
```

Источник обложки должен иметь непустое содержимое и MIME `image/*`. Нормализацию размера,
кроп и проверку magic bytes выполняйте до вызова wrapper в доменном приложении.

## Обработка ошибок

| Исключение | Когда | Что делать |
|------------|-------|------------|
| `VkAuthenticationException` | вход не завершён (таймаут, закрыто окно) | повторить вход |
| `VkSessionExpiredException` | cookies больше не годятся | заново пройти браузерный вход |
| `VkApiException` | метод API вернул ошибку (`ErrorCode`, `Method`) | смотреть код ошибки VK |
| `VkClientException` | базовое (сеть, неожиданный ответ) | — |

## Консольный пример (CLI)

```bash
dotnet run --project samples/VkBrowserClient.ConsoleSample -- [--session <имя>] <команда>
```

| Команда | Описание |
|---------|----------|
| `dialogs` (по умолчанию) | 10 последних диалогов |
| `history <peer_id> [count]` | история сообщений |
| `send <peer_id> <текст> [--photo путь]` | отправить сообщение (можно с фото) |
| `post <текст> [--photo путь]` | опубликовать запись |
| `postgroup <group_id> <текст> [медиа]` | опубликовать запись сообщества |
| `clipgroup <group_id> <видео> [описание]` | опубликовать Клип сообщества |
| `export <файл>` | выгрузить выбранную сессию (для сервера) |
| `import <файл>` | загрузить сессию из файла в выбранную |
| `sessions` | список сохранённых сессий |
| `help` | справка |

### Несколько аккаунтов (именованные сессии)

Каждая сессия — отдельный файл `sessions/<имя>.json`. При запуске без `--session`
(и не в пайпе) предлагается выбрать существующую сессию или новый вход. Сессия из
прошлых версий автоматически становится `default`.

```bash
# войти/работать под аккаунтом «work» (откроет браузер, если сессии ещё нет)
dotnet run --project samples/VkBrowserClient.ConsoleSample -- --session work dialogs

# список сохранённых сессий
dotnet run --project samples/VkBrowserClient.ConsoleSample -- sessions
```

`VK_SESSION_PATH` задаёт конкретный файл сессии в обход выбора.

О переносе сессии на сервер без браузера — см. [SERVER.md](SERVER.md).
