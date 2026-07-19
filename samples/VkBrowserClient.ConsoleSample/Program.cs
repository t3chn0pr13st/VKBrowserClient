using VkBrowserClient;

// ---------------------------------------------------------------------------
// Демонстрация возможностей библиотеки. Команды:
//   (без аргументов) | dialogs            — 10 последних диалогов
//   history <peer_id> [count]             — история сообщений диалога
//   send <peer_id> <текст> [--photo путь] — отправить сообщение (можно с фото)
//   post <текст> [--photo путь]           — опубликовать запись на стене
//   export <файл>                         — выгрузить сессию в файл (для сервера)
//   import <файл>                         — загрузить сессию из файла
//   help                                  — показать эту справку
//
// Путь к файлу сессии: переменная VK_SESSION_PATH (или каталог по умолчанию).
// peer_id: id пользователя, отрицательный id сообщества или 2000000000+chat_id.
// ---------------------------------------------------------------------------

Console.OutputEncoding = System.Text.Encoding.UTF8;

var sessionPath =
    Environment.GetEnvironmentVariable("VK_SESSION_PATH")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VkBrowserClient", "session.json");

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "dialogs";
var rest = args.Skip(1).ToArray();
var (positionals, photoPaths) = SplitArgs(rest);

await using var client = VkClient.Create(sessionPath, options => options.StatusCallback = Console.WriteLine);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

try
{
    switch (command)
    {
        case "help" or "-h" or "--help":
            PrintHelp();
            return 0;

        case "export":
            RequireArg(positionals, 0, "export <файл>");
            await client.EnsureAuthenticatedAsync(ct);
            await client.ExportSessionAsync(positionals[0], ct);
            Console.WriteLine($"Сессия выгружена в {positionals[0]}");
            return 0;

        case "import":
            RequireArg(positionals, 0, "import <файл>");
            await client.ImportSessionAsync(positionals[0], ct);
            await client.EnsureAuthenticatedAsync(ct);
            Console.WriteLine($"Сессия загружена. user_id = {client.UserId}");
            return 0;
    }

    Console.WriteLine($"Файл сессии: {sessionPath}");
    await client.EnsureAuthenticatedAsync(ct);
    Console.WriteLine($"Авторизация ок. user_id = {client.UserId}\n");

    switch (command)
    {
        case "dialogs":
            await ShowDialogs(client, ct);
            break;

        case "history":
            RequireArg(positionals, 0, "history <peer_id> [count]");
            var peer = long.Parse(positionals[0]);
            var count = positionals.Count > 1 ? int.Parse(positionals[1]) : 20;
            await ShowHistory(client, peer, count, ct);
            break;

        case "send":
            RequireArg(positionals, 0, "send <peer_id> <текст> [--photo путь]");
            var sendPeer = long.Parse(positionals[0]);
            var text = string.Join(' ', positionals.Skip(1));
            var images = LoadImages(photoPaths);
            var msgId = await client.Messages.SendMessageAsync(sendPeer, text.Length > 0 ? text : null, images, ct);
            Console.WriteLine($"Сообщение отправлено. id = {msgId}");
            break;

        case "post":
            var postText = string.Join(' ', positionals);
            var postImages = LoadImages(photoPaths);
            var post = await client.Wall.PostAsync(postText.Length > 0 ? postText : null, postImages, cancellationToken: ct);
            Console.WriteLine($"Запись опубликована: {post.Url}");
            break;

        default:
            Console.Error.WriteLine($"Неизвестная команда «{command}».\n");
            PrintHelp();
            return 1;
    }

    return 0;
}
catch (VkAuthenticationException ex) { return Fail("Вход не завершён", ex, 2); }
catch (VkSessionExpiredException ex) { return Fail("Сессия недействительна (удалите файл сессии и войдите заново)", ex, 3); }
catch (VkApiException ex)            { return Fail("Ошибка метода VK", ex, 4); }
catch (VkClientException ex)         { return Fail("Ошибка клиента VK", ex, 4); }
catch (OperationCanceledException)   { Console.Error.WriteLine("Отменено."); return 130; }

// --- команды -----------------------------------------------------------------

static async Task ShowDialogs(VkClient client, CancellationToken ct)
{
    var page = await client.Messages.GetConversationsAsync(10, ct);
    Console.WriteLine($"Всего бесед: {page.TotalCount}. Последние {page.Items.Count}:");
    Console.WriteLine(new string('─', 48));
    var i = 1;
    foreach (var c in page.Items)
    {
        var unread = c.UnreadCount > 0 ? $"  ({c.UnreadCount} непроч.)" : "";
        Console.WriteLine($"{i++,2}. [{TypeLabel(c.PeerType)}] peer={c.PeerId}  {c.Title}{unread}");
    }
}

static async Task ShowHistory(VkClient client, long peerId, int count, CancellationToken ct)
{
    var history = await client.Messages.GetHistoryAsync(peerId, count, cancellationToken: ct);
    Console.WriteLine($"История peer={peerId} (всего {history.TotalCount}), показано {history.Items.Count}:");
    Console.WriteLine(new string('─', 48));
    foreach (var m in history.Items.OrderBy(x => x.Date))
    {
        var who = m.IsOutgoing ? "Вы" : (m.SenderName ?? $"id{m.FromId}");
        Console.WriteLine($"[{m.Date.LocalDateTime:dd.MM HH:mm}] {who}: {m.Text}");
        foreach (var ph in m.Photos)
            Console.WriteLine($"        🖼  {ph.Width}x{ph.Height}  {ph.Url}");
        if (m.OtherAttachmentsCount > 0)
            Console.WriteLine($"        📎 вложений (не фото): {m.OtherAttachmentsCount}");
    }
}

// --- утилиты -----------------------------------------------------------------

static (List<string> positionals, List<string> photos) SplitArgs(string[] a)
{
    var pos = new List<string>();
    var photos = new List<string>();
    for (var i = 0; i < a.Length; i++)
    {
        if (a[i] is "--photo" or "-p" && i + 1 < a.Length)
            photos.Add(a[++i]);
        else
            pos.Add(a[i]);
    }
    return (pos, photos);
}

static IReadOnlyList<VkImage> LoadImages(IReadOnlyList<string> paths)
    => paths.Select(VkImage.FromFile).ToList();

static void RequireArg(List<string> positionals, int index, string usage)
{
    if (positionals.Count <= index)
        throw new VkClientException($"Недостаточно аргументов. Использование: {usage}");
}

static string TypeLabel(VkPeerType type) => type switch
{
    VkPeerType.User => "лс   ",
    VkPeerType.Chat => "чат  ",
    VkPeerType.Group => "сообщ",
    _ => "?    ",
};

static int Fail(string prefix, Exception ex, int code)
{
    Console.Error.WriteLine($"{prefix}: {ex.Message}");
    return code;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Команды:
          dialogs                                 10 последних диалогов (по умолчанию)
          history <peer_id> [count]               история сообщений диалога
          send <peer_id> <текст> [--photo путь]   отправить сообщение (можно с фото)
          post <текст> [--photo путь]             опубликовать запись на стене
          export <файл>                           выгрузить сессию в файл (для сервера)
          import <файл>                           загрузить сессию из файла
          help                                    эта справка

        peer_id: id пользователя, -id сообщества, или 2000000000+chat_id.
        Путь к сессии: переменная окружения VK_SESSION_PATH.
        """);
}
