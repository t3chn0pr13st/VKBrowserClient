using System.Text.Json;
using VkBrowserClient;

// ---------------------------------------------------------------------------
// Демонстрация возможностей библиотеки.
//
// Именованные сессии: каждая сессия — отдельный файл в каталоге sessions/.
// При запуске без указания сессии предлагается выбрать её (или новый вход).
// Существующая сессия из старой версии автоматически становится «default».
//
// Глобальная опция:
//   --session <имя> | -s <имя>            выбрать сессию по имени (без интерактива)
//
// Команды:
//   (без аргументов) | dialogs            — 10 последних диалогов
//   history <peer_id> [count]             — история сообщений диалога
//   send <peer_id> <текст> [медиа]        — отправить сообщение (--photo/--doc/--video)
//   post <текст> [медиа]                  — опубликовать запись (--photo/--doc/--video)
//   export <файл>                         — выгрузить выбранную сессию в файл
//   import <файл>                         — загрузить сессию из файла в выбранную
//   sessions                              — список сохранённых сессий
//   help                                  — справка
//
// Переменная VK_SESSION_PATH задаёт конкретный файл сессии в обход выбора.
// peer_id: id пользователя, отрицательный id сообщества или 2000000000+chat_id.
// ---------------------------------------------------------------------------

Console.OutputEncoding = System.Text.Encoding.UTF8;

var baseDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VkBrowserClient");
var sessionsDir = Path.Combine(baseDir, "sessions");

// Разбор аргументов: сначала вынимаем глобальную опцию --session/-s, затем команду.
var (sessionArg, restArgs) = ExtractSessionOption(args);
var command = restArgs.Length > 0 ? restArgs[0].ToLowerInvariant() : "dialogs";
var (positionals, attachments) = SplitArgs(restArgs.Skip(1).ToArray());

// Команды, не требующие сессии/авторизации.
if (command is "help" or "-h" or "--help") { PrintHelp(); return 0; }
if (command is "sessions") { ListSessions(); return 0; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

try
{
    // Выбор файла сессии.
    string sessionPath;
    string sessionLabel;
    var envPath = Environment.GetEnvironmentVariable("VK_SESSION_PATH");
    if (!string.IsNullOrEmpty(envPath))
    {
        sessionPath = envPath;
        sessionLabel = $"VK_SESSION_PATH → {envPath}";
    }
    else
    {
        Directory.CreateDirectory(sessionsDir);
        MigrateLegacyDefault();
        // Имя сессии: из --session, иначе интерактивный выбор, иначе (в пайпе/скрипте) — default.
        var name = SanitizeSessionName(sessionArg ?? (Console.IsInputRedirected ? "default" : PickSession()));
        sessionPath = Path.Combine(sessionsDir, name + ".json");
        sessionLabel = $"'{name}'";
    }

    await using var client = VkClient.Create(sessionPath, options => options.StatusCallback = Console.WriteLine);

    switch (command)
    {
        case "export":
            RequireArg(positionals, 0, "export <файл>");
            await client.EnsureAuthenticatedAsync(ct);
            await client.ExportSessionAsync(positionals[0], ct);
            Console.WriteLine($"Сессия {sessionLabel} выгружена в {positionals[0]}");
            return 0;

        case "import":
            RequireArg(positionals, 0, "import <файл>");
            await client.ImportSessionAsync(positionals[0], ct);
            await client.EnsureAuthenticatedAsync(ct);
            Console.WriteLine($"Сессия загружена в {sessionLabel}. user_id = {client.UserId}");
            return 0;
    }

    Console.WriteLine($"Сессия: {sessionLabel}");
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
            RequireArg(positionals, 0, "send <peer_id> <текст> [--photo|--doc|--video путь]");
            var sendPeer = long.Parse(positionals[0]);
            var text = string.Join(' ', positionals.Skip(1));
            var msgId = await client.Messages.SendMessageAsync(sendPeer, text.Length > 0 ? text : null, attachments, cancellationToken: ct);
            Console.WriteLine($"Сообщение отправлено. id = {msgId}");
            break;

        case "post":
            var postText = string.Join(' ', positionals);
            var post = await client.Wall.PostAsync(postText.Length > 0 ? postText : null, attachments, cancellationToken: ct);
            Console.WriteLine($"Запись опубликована: {post.Url}");
            break;

        case "clip":
            RequireArg(positionals, 0, "clip <путь-к-видео> [описание]");
            var clipDesc = positionals.Count > 1 ? string.Join(' ', positionals.Skip(1)) : null;
            Console.WriteLine("Публикую клип (создание → загрузка → кодирование → публикация)…");
            var clip = await client.Clips.PublishFromFileAsync(positionals[0], clipDesc, cancellationToken: ct);
            Console.WriteLine($"Клип опубликован: {clip.Url}");
            break;

        default:
            Console.Error.WriteLine($"Неизвестная команда «{command}».\n");
            PrintHelp();
            return 1;
    }

    return 0;
}
catch (VkAuthenticationException ex) { return Fail("Вход не завершён", ex, 2); }
catch (VkSessionExpiredException ex) { return Fail("Сессия недействительна (выберите новый вход)", ex, 3); }
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

// --- выбор сессии ------------------------------------------------------------

// Переносит сессию из старой версии (baseDir/session.json) в sessions/default.json.
void MigrateLegacyDefault()
{
    var legacy = Path.Combine(baseDir, "session.json");
    var target = Path.Combine(sessionsDir, "default.json");
    if (File.Exists(legacy) && !File.Exists(target))
        File.Move(legacy, target);
}

string[] SessionNames() => Directory.Exists(sessionsDir)
    ? Directory.GetFiles(sessionsDir, "*.json")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(n => !string.IsNullOrEmpty(n))
        .Select(n => n!)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToArray()
    : [];

string PickSession()
{
    var names = SessionNames();
    Console.WriteLine("Выберите сессию:");
    for (var i = 0; i < names.Length; i++)
        Console.WriteLine($"  {i + 1}) {names[i]}{UserIdSuffix(names[i])}");
    Console.WriteLine($"  {names.Length + 1}) Новый вход (задать имя)");
    Console.Write("Номер: ");

    var input = Console.ReadLine()?.Trim();
    if (int.TryParse(input, out var n))
    {
        if (n >= 1 && n <= names.Length) return names[n - 1];
        if (n == names.Length + 1)
        {
            Console.Write("Имя новой сессии: ");
            var name = Console.ReadLine()?.Trim();
            return string.IsNullOrWhiteSpace(name) ? "default" : name;
        }
    }
    Console.WriteLine("Не распознано — использую «default».");
    return "default";
}

void ListSessions()
{
    Directory.CreateDirectory(sessionsDir);
    MigrateLegacyDefault();
    var names = SessionNames();
    if (names.Length == 0) { Console.WriteLine("Сохранённых сессий нет. Запустите без аргументов для входа."); return; }
    Console.WriteLine("Сохранённые сессии:");
    foreach (var n in names)
        Console.WriteLine($"  {n}{UserIdSuffix(n)}");
}

string UserIdSuffix(string name)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionsDir, name + ".json")));
        if (doc.RootElement.TryGetProperty("UserId", out var uid) && uid.TryGetInt64(out var v) && v > 0)
            return $"  (user_id {v})";
    }
    catch { /* повреждённый/недоступный файл — просто без подписи */ }
    return "";
}

static string SanitizeSessionName(string name)
{
    name = name.Trim();
    if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        throw new VkClientException($"Недопустимое имя сессии «{name}». Разрешены буквы, цифры, '-' и '_'.");
    return name;
}

// --- утилиты -----------------------------------------------------------------

static (string? sessionName, string[] rest) ExtractSessionOption(string[] args)
{
    string? name = null;
    var rest = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--session" or "-s" && i + 1 < args.Length)
            name = args[++i];
        else
            rest.Add(args[i]);
    }
    return (name, rest.ToArray());
}

static (List<string> positionals, List<VkAttachmentSource> attachments) SplitArgs(string[] a)
{
    var pos = new List<string>();
    var att = new List<VkAttachmentSource>();
    for (var i = 0; i < a.Length; i++)
    {
        if (a[i] is "--photo" or "-p" && i + 1 < a.Length) att.Add(VkAttachmentSource.Photo(a[++i]));
        else if (a[i] is "--doc" && i + 1 < a.Length) att.Add(VkAttachmentSource.Document(a[++i]));
        else if (a[i] is "--video" && i + 1 < a.Length) att.Add(VkAttachmentSource.Video(a[++i]));
        else pos.Add(a[i]);
    }
    return (pos, att);
}

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
        Использование: [--session <имя>] <команда> [аргументы]

        Команды:
          dialogs                                 10 последних диалогов (по умолчанию)
          history <peer_id> [count]               история сообщений диалога
          send <peer_id> <текст> [медиа]          отправить сообщение (с медиа)
          post <текст> [медиа]                    опубликовать запись (с медиа)
          clip <путь-к-видео> [описание]          опубликовать клип
          export <файл>                           выгрузить выбранную сессию в файл
          import <файл>                           загрузить сессию из файла в выбранную
          sessions                                список сохранённых сессий
          help                                    эта справка

        Сессии:
          --session <имя> | -s <имя>              выбрать сессию по имени
          без опции и не в пайпе                  интерактивный выбор при запуске
          существующая сессия                     доступна под именем «default»

        медиа: --photo <путь> | --doc <путь> | --video <путь> (можно несколько).
        peer_id: id пользователя, -id сообщества, или 2000000000+chat_id.
        VK_SESSION_PATH задаёт конкретный файл сессии в обход выбора.
        """);
}
