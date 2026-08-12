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
//   postgroup <group_id> <текст> [медиа]  — опубликовать запись от имени сообщества
//   clipgroup <group_id> <видео> [опис.]  — опубликовать клип сообщества
//   livesdk <group_id> [заголовок]        — создать эфир сообщества через live-SDK
//                                           (--public/--followers/--admins, по умолчанию по ссылке)
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

        case "postgroup":
            RequireArg(positionals, 0, "postgroup <group_id> <текст> [--photo|--doc|--video путь]");
            var postGroupId = long.Parse(positionals[0]);
            var groupPostText = string.Join(' ', positionals.Skip(1));
            var groupPost = await client.Wall.PostToCommunityAsync(
                postGroupId,
                groupPostText.Length > 0 ? groupPostText : null,
                attachments,
                ct);
            Console.WriteLine($"Запись сообщества опубликована: {groupPost.Url}");
            break;

        case "clip":
            RequireArg(positionals, 0, "clip <путь-к-видео> [описание]");
            var clipDesc = positionals.Count > 1 ? string.Join(' ', positionals.Skip(1)) : null;
            Console.WriteLine("Публикую клип (создание → загрузка → кодирование → публикация)…");
            var clip = await client.Clips.PublishFromFileAsync(
                positionals[0], new VkClipPublishOptions { Description = clipDesc }, ct);
            Console.WriteLine($"Клип опубликован: {clip.Url}");
            break;

        case "clipgroup":
            RequireArg(positionals, 1, "clipgroup <group_id> <путь-к-видео> [описание]");
            var clipGroupId = long.Parse(positionals[0]);
            var groupClipDesc = positionals.Count > 2 ? string.Join(' ', positionals.Skip(2)) : null;
            Console.WriteLine("Публикую клип сообщества (создание → потоковая загрузка → кодирование → публикация)…");
            var groupClip = await client.Clips.PublishFromFileAsync(
                positionals[1],
                new VkClipPublishOptions { GroupId = clipGroupId, Description = groupClipDesc },
                ct);
            Console.WriteLine($"Клип сообщества опубликован: {groupClip.Url}");
            break;

        case "livesdk":
            RequireArg(positionals, 0, "livesdk <group_id> [заголовок]");
            // Флаги приватности сюда попадают позиционными — в заголовок им не надо.
            var liveTitle = positionals.Skip(1)
                .Where(p => !p.StartsWith("--", StringComparison.Ordinal))
                .ToArray();
            await CreateSdkStream(
                client,
                long.Parse(positionals[0]),
                liveTitle.Length > 0 ? string.Join(' ', liveTitle) : "Тестовый эфир",
                PermissionFromArgs(restArgs),
                ct);
            break;

        case "livesdkperm":
            RequireArg(positionals, 1, "livesdkperm <channel_url> <slot_url>");
            var settings = await client.LiveSdk.GetStreamSettingsAsync(positionals[0], positionals[1], ct);
            Console.WriteLine($"Приватность слота: {settings.Permission}");
            Console.WriteLine($"Заголовок: «{settings.Title}»");
            break;

        case "editclip":
            RequireArg(positionals, 1, "editclip <owner_id> <video_id> <новое описание>");
            var applied = await client.Clips.EditDescriptionAsync(
                long.Parse(positionals[0]), long.Parse(positionals[1]), string.Join(' ', positionals.Skip(2)), ct);
            Console.WriteLine($"Описание клипа обновлено: {applied}");
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
catch (VkApiException ex) { return Fail("Ошибка метода VK", ex, 4); }
catch (VkClientException ex) { return Fail("Ошибка клиента VK", ex, 4); }
catch (OperationCanceledException) { Console.Error.WriteLine("Отменено."); return 130; }

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

// Создаёт эфир сообщества через live-SDK и тут же перечитывает приватность со слота.
// Перечитывание — не украшение: именно так работает fail-closed проверка, и смысл прогона
// в том, чтобы увидеть, что сервер согласен с запрошенным значением, а не поверить запросу.
static async Task CreateSdkStream(
    VkClient client,
    long groupId,
    string title,
    VkLiveSdkPermission permission,
    CancellationToken ct)
{
    Console.WriteLine($"Создаю эфир сообщества {groupId} с приватностью «{permission}»…");

    var stream = await client.LiveSdk.CreateGroupStreamAsync(new VkLiveSdkCreateOptions
    {
        GroupId = groupId,
        Title = title,
        Permission = permission,
    }, ct);

    Console.WriteLine("Создан:");
    Console.WriteLine($"  канал      {stream.ChannelUrl}");
    Console.WriteLine($"  слот       {stream.SlotUrl} (id {stream.SlotId})");
    Console.WriteLine($"  VK-видео   {stream.VkOwnerId}_{stream.VkVideoId}");
    Console.WriteLine($"  страница   {stream.Url}");
    Console.WriteLine($"  приватность {stream.Permission}");
    Console.WriteLine($"  одноразовый ключ: {stream.IsTemporary}");
    // Ключ потока — секрет: показываем только факт наличия и длину.
    Console.WriteLine($"  ingest     {stream.Ingest.Url}  ключ: {stream.Ingest.Key.Length} симв.");

    var actual = await client.LiveSdk.GetStreamPermissionAsync(stream.ChannelUrl, stream.SlotUrl, ct);
    Console.WriteLine($"Приватность, перечитанная со слота: {actual}");
    Console.WriteLine(actual == permission
        ? "OK: сервер подтвердил запрошенную приватность."
        : $"ВНИМАНИЕ: запрошено «{permission}», на слоте «{actual}» — эфир публиковать нельзя.");
}

static VkLiveSdkPermission PermissionFromArgs(string[] args) =>
    args.Contains("--public") ? VkLiveSdkPermission.Public
    : args.Contains("--followers") ? VkLiveSdkPermission.Followers
    : args.Contains("--admins") ? VkLiveSdkPermission.Admins
    : VkLiveSdkPermission.ByLink;

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
          postgroup <group_id> <текст> [медиа]    запись от имени сообщества
          clip <путь-к-видео> [описание]          опубликовать клип
          clipgroup <group_id> <видео> [опис.]    клип от имени сообщества
          editclip <owner_id> <video_id> <опис.> изменить описание клипа
          livesdk <group_id> [заголовок]          эфир сообщества через live-SDK
          export <файл>                           выгрузить выбранную сессию в файл
          import <файл>                           загрузить сессию из файла в выбранную
          sessions                                список сохранённых сессий
          help                                    эта справка

        Сессии:
          --session <имя> | -s <имя>              выбрать сессию по имени
          без опции и не в пайпе                  интерактивный выбор при запуске
          существующая сессия                     доступна под именем «default»

        медиа: --photo <путь> | --doc <путь> | --video <путь> (можно несколько).
        приватность эфира: --public | --followers | --admins (по умолчанию — по ссылке).
        peer_id: id пользователя, -id сообщества, или 2000000000+chat_id.
        VK_SESSION_PATH задаёт конкретный файл сессии в обход выбора.
        """);
}
