# Использование

## Установка

Добавьте пакет (см. [NUGET.md](NUGET.md) о приватном фиде GitHub Packages) или сошлитесь на проект.
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

## Обработка ошибок

| Исключение | Когда | Что делать |
|------------|-------|------------|
| `VkAuthenticationException` | вход не завершён (таймаут, закрыто окно) | повторить вход |
| `VkSessionExpiredException` | cookies больше не годятся | заново пройти браузерный вход |
| `VkApiException` | метод API вернул ошибку (`ErrorCode`, `Method`) | смотреть код ошибки VK |
| `VkClientException` | базовое (сеть, неожиданный ответ) | — |

## Консольный пример (CLI)

```bash
dotnet run --project samples/VkBrowserClient.ConsoleSample -- <команда>
```

| Команда | Описание |
|---------|----------|
| `dialogs` (по умолчанию) | 10 последних диалогов |
| `history <peer_id> [count]` | история сообщений |
| `send <peer_id> <текст> [--photo путь]` | отправить сообщение (можно с фото) |
| `post <текст> [--photo путь]` | опубликовать запись |
| `export <файл>` | выгрузить сессию (для сервера) |
| `import <файл>` | загрузить сессию |
| `help` | справка |

Путь к файлу сессии задаётся переменной `VK_SESSION_PATH`.

О переносе сессии на сервер без браузера — см. [SERVER.md](SERVER.md).
