namespace VkBrowserClient;

/// <summary>Хранилище сессии между запусками.</summary>
public interface ISessionStore
{
    /// <summary>Загрузить сохранённую сессию или <c>null</c>, если её нет.</summary>
    Task<VkSession?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Сохранить (перезаписать) сессию.</summary>
    Task SaveAsync(VkSession session, CancellationToken cancellationToken = default);

    /// <summary>Удалить сохранённую сессию.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
