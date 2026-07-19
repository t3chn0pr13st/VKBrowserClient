namespace VkBrowserClient;

/// <summary>Результат загрузки фото на сервер загрузки VK — данные для photos.save*Photo.</summary>
public readonly record struct PhotoUploadResult(long Server, string Photo, string Hash);
