# Работа на сервере без браузера

Браузер нужен **только для первого входа** (пройти VK ID, снять cookies). После этого
сессию можно перенести на сервер и работать в фоне без UI: web-токен обновляется из cookies
по сети.

## Схема

```
[Машина с UI]                         [Сервер без UI]
 браузерный вход  ─── export ──▶  session.portable.json ─── import/копирование ──▶  фоновая работа
 (cookies сняты)                 (или base64 в секрете)                             (токен рефрешится сам)
```

## Вариант 1: файл сессии

На машине с браузером:

```csharp
await using var client = VkClient.Create("session.json", o => o.StatusCallback = Console.WriteLine);
await client.EnsureAuthenticatedAsync();                 // интерактивный вход
await client.ExportSessionAsync("session.portable.json"); // портируемый файл
```

Скопируйте `session.portable.json` на сервер (файл содержит cookies — это ключ от аккаунта,
передавайте безопасно). На сервере:

```csharp
await using var client = VkClient.Create("/var/lib/vkbot/session.json");
await client.ImportSessionAsync("/secure/session.portable.json");
await client.EnsureAuthenticatedAsync();                 // без браузера: рефреш токена из cookies
var page = await client.Messages.GetConversationsAsync(10);
```

Проще всего: сам файл `session.json` уже самодостаточен — можно просто скопировать его на сервер
и указать путь (или `VK_SESSION_PATH`), тогда `import` не нужен.

## Вариант 2: base64 в переменной окружения / секрете

Удобно для контейнеров и секрет-менеджеров — без файла на диске.

Экспорт:

```csharp
string portable = await client.ExportSessionToBase64Async();
// сохраните в секрет (например, переменную окружения VK_SESSION)
```

На сервере:

```csharp
await using var client = VkClient.Create("/tmp/vk-session.json");
await client.ImportSessionFromBase64Async(Environment.GetEnvironmentVariable("VK_SESSION")!);
await client.EnsureAuthenticatedAsync();
```

## Когда сессия «протухнет»

Cookies живут долго, но не вечно (смена пароля, разлогин всех устройств, срок жизни).
Тогда любой вызов бросит `VkSessionExpiredException`. Обработайте это на сервере как сигнал
«нужен новый вход»: повторите экспорт с машины с браузером и обновите сессию на сервере.

```csharp
try
{
    var page = await client.Messages.GetConversationsAsync(10);
}
catch (VkSessionExpiredException)
{
    // уведомить оператора: требуется повторный браузерный вход и обновление сессии
}
```

## Безопасность

- Файл/строка сессии = **пароль от аккаунта** (cookies + токен). Храните в секрете.
- `FileSessionStore` на Unix создаёт файл сразу с правами `0600` и каталог `0700`.
- Для продакшена рассмотрите шифрование содержимого — реализуйте свой `ISessionStore`.
- Не коммитьте файлы сессии (уже в `.gitignore`).
