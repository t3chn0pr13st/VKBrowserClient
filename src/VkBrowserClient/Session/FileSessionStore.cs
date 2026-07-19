using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Простое файловое хранилище сессии в формате JSON.
///
/// Файл содержит cookies и токен — то есть фактически ключи от аккаунта.
/// Поэтому на Unix-системах файл создаётся сразу с правами 0600 (доступ только владельцу):
/// секрет не «мелькает» с правами по умолчанию. Если выставить права не удалось —
/// об этом сообщается через <c>onWarning</c> (молча не проглатываем).
///
/// Для продакшена рекомендуется дополнительно шифровать содержимое
/// (реализовав свой <see cref="ISessionStore"/>) и хранить в защищённом каталоге.
/// </summary>
public sealed class FileSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Action<string>? _onWarning;

    /// <param name="path">Путь к файлу сессии.</param>
    /// <param name="onWarning">Необязательный колбэк для предупреждений (например, если не удалось ограничить права файла).</param>
    public FileSessionStore(string path, Action<string>? onWarning = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Путь к файлу сессии не задан.", nameof(path))
            : Path.GetFullPath(path);
        _onWarning = onWarning;
    }

    public async Task<VkSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            await using var stream = File.OpenRead(_path);
            // Валидацию (есть ли cookies, живой ли токен) делает VkClient, здесь просто читаем.
            var session = await JsonSerializer.DeserializeAsync<VkSession>(stream, JsonOptions, cancellationToken)
                                              .ConfigureAwait(false);
            // Нормализуем: "Cookies": null во внешне-отредактированном файле не должен ронять клиент.
            if (session is not null)
                session.Cookies ??= new List<VkCookie>();
            return session;
        }
        catch (JsonException)
        {
            // Повреждённый файл — считаем, что сессии нет.
            return null;
        }
    }

    public async Task SaveAsync(VkSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            RestrictDirectoryPermissions(dir);
        }

        // Атомарная запись: во временный файл, затем move поверх.
        var tmp = _path + ".tmp";
        try
        {
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            // Создаём файл сразу с правами 0600 — секрет не появляется на диске с правами по умолчанию.
            if (!OperatingSystem.IsWindows())
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var stream = new FileStream(tmp, streamOptions))
            {
                await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            // rename сохраняет права и inode временного файла (та же ФС).
            File.Move(tmp, _path, overwrite: true);
            RestrictPermissions(_path); // на случай, если целевой файл уже существовал с другими правами
        }
        catch
        {
            TryDelete(tmp); // не оставляем «осиротевший» temp с секретом при сбое
            throw;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        TryDelete(_path);
        return Task.CompletedTask;
    }

    private void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            // 0600 — читать/писать может только владелец.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            _onWarning?.Invoke(
                $"Не удалось ограничить права файла сессии ({path}) до 0600: {ex.Message}. " +
                "Файл может быть доступен другим пользователям системы.");
        }
    }

    private void RestrictDirectoryPermissions(string dir)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            // 0700 — каталог доступен только владельцу.
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // Не критично: каталог мог быть создан заранее с нужными правами.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort
        }
    }
}
