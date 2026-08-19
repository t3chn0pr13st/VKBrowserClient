# Архитектура и протокол

Библиотека не использует публичное API ВК (с регистрацией приложения). Вместо этого она
повторяет то, что делает **веб-мессенджер `vk.ru`** в браузере: тот же `client_id`, тот же
хост API, тот же способ авторизации. Ниже — восстановленный по живому трафику протокол.

## Действующие лица

| Что | Значение |
|-----|----------|
| `client_id` веб-приложения | `6287487` |
| Версия API | `5.282` |
| Хост методов API | `https://web.api.vk.ru/method/*` |
| Хост выдачи токена | `https://login.vk.ru/?act=web_token` |
| Вход (VK ID) | `https://id.vk.ru`, `https://login.vk.ru` |
| Серверы загрузки фото | `https://pu.vk.ru/...` (URL выдаёт API) |
| Реалтайм (не реализовано) | LongPoll `https://queuev4.vk.ru/...` |

## Два уровня сессии

1. **Cookies — долговременный ключ.** Вход через VK ID ставит cookie-сессию на `.vk.ru`.
   Главная — httpOnly `remixsid`. Живёт долго (недели), но её нельзя прочитать из JS —
   поэтому её снимает управляемый браузер (Playwright) при первом входе.
2. **web-токен — короткоживущий (`~18–20 минут`).** Мятится из cookies и используется как
   `access_token` для методов API. Это и есть «refresh»: обновление токена не требует UI,
   только cookies.

```
[Браузерный вход VK ID]  ->  cookies (remixsid, ...)          # надолго, снимаем один раз
        cookies          ->  POST login.vk.ru/?act=web_token   # web-токен, ~18 мин
        web-токен        ->  web.api.vk.ru/method/*            # вызовы API
```

Если cookies перестают работать (разлогин), обновление токена отдаёт редирект/не-`okay` —
клиент бросает `VkSessionExpiredException`, и нужен повторный браузерный вход.

> Только cookie **недостаточно**: прямой вызов `web.api.vk.ru` без токена возвращает
> `error_code 5` («client_secret is incorrect»). Токен обязателен.

## Обновление web-токена

```http
POST https://login.vk.ru/?act=web_token
Content-Type: application/x-www-form-urlencoded
Origin: https://vk.ru

version=1&app_id=6287487
```

Ответ:

```json
{ "type": "okay", "data": {
  "access_token": "vk1.a....",
  "expires": 1784473661,          // абсолютное unix-время (секунды)
  "user_id": 123456789,
  "logout_hash": "..." } }
```

## Вызов метода

```http
POST https://web.api.vk.ru/method/messages.getConversations?v=5.282&client_id=6287487
Content-Type: application/x-www-form-urlencoded

count=10&extended=1&fields=first_name,last_name,name&v=5.282&access_token=vk1.a...
```

Стандартный формат ответа VK: `{ "response": ... }` либо `{ "error": { "error_code", "error_msg" } }`.
`error_code 5` = проблема авторизации (в т.ч. «токен истёк») → клиент обновляет токен и повторяет вызов один раз.

Проверенные и используемые методы (доступны с web-токеном; права `photos`, `messages`, `wall` подтверждены):

| Возможность | Методы |
|-------------|--------|
| Список диалогов | `messages.getConversations` |
| История сообщений | `messages.getHistory` |
| Отправка сообщения | `messages.send` |
| Публикация записи | `wall.post` |
| Загрузка фото | `photos.get{Messages,Wall}UploadServer` → upload → `photos.save{Messages,Wall}Photo` |
| Загрузка документов | `docs.get{Messages,Wall}UploadServer` → upload → `docs.save` |
| Прямой эфир | `video.startStreaming` → `video.get` → `video.stopStreaming` |
| Обложка эфира | `video.getThumbUploadUrl` → signed upload → `video.saveUploadedThumb` |
| Загрузка видео | `video.save` → upload (`ovu.mycdn.me`) |
| Публикация клипа | `shortVideo.create` → upload → `encodeProgress` → `edit` → `publish` |

## Загрузка фотографий (3 шага, как в вебе)

```
1) photos.getMessagesUploadServer(peer_id)   →  { upload_url, ... }
   photos.getWallUploadServer()              →  { upload_url, ... }
2) POST multipart/form-data на upload_url, ПОЛЕ ФАЙЛА = "photo"   # проверено на живом сервере
                                             →  { server, photo, hash }
3) photos.saveMessagesPhoto / saveWallPhoto(photo, server, hash)
                                             →  [ { id, owner_id, access_key } ]
   →  ссылка-вложение: photo{owner_id}_{id}[_{access_key}]
```

Затем ссылки объединяются через запятую и передаются в `messages.send` (параметр `attachment`)
или `wall.post` (параметр `attachments`).

Нюансы, найденные при проверке:
- Поле файла именно **`photo`** (не `file1`). Пустой `"photo":""`/`"[]"` в ответе = файл не принят.
- Слишком маленькие изображения (например 1×1) сервер **отклоняет** — нужен нормальный размер.
- Сам `upload_url` уже подписан: cookies/токен для POST файла не нужны.

## Загрузка документов и видео

**Документы** (файлы, GIF, аудиосообщения) — тот же 3-шаговый паттерн, поле файла **`file`**:

```
docs.getMessagesUploadServer(peer_id, type) / docs.getWallUploadServer()  →  { upload_url }
POST multipart на upload_url, поле "file"                                  →  { file: "<token>" }
docs.save(file, title)   →  { type, <type>: { id, owner_id, access_key? } }
   →  вложение: doc{owner_id}_{id}[_{access_key}]
```

**Видео** (в т.ч. клипы) — через `video.save`, загрузка на CDN OK/mycdn, поле **`video_file`**:

```
video.save(name, description, is_private, wallpost)
   →  { upload_url (ovu.mycdn.me), video_id, owner_id, access_key, upload_config }
POST multipart на upload_url, поле "video_file"   →  { video_hash, size, direct_link, ... }
   →  вложение: video{owner_id}_{video_id}[_{access_key}]
```

Проверено на живых серверах: поля `file` и `video_file`. Видео обрабатывается асинхронно,
но ссылка-вложение валидна сразу. `upload_config` (каналы/ретраи) — оптимизация параллельной
загрузки; одиночного POST достаточно для небольших файлов.

**Клипы** (VK Клипы) — отдельный флоу `shortVideo.*` (снят с реальной публикации в вебе):

```
shortVideo.create(file_size, group_id)  →  { owner_id, video_id, upload_url (ovu.mycdn.me) }   # file_size ≥ 16384
POST video_file на upload_url           →  { video_hash, size, owner_id, video_id }             # мелкие — 1 POST, крупные — чанки
shortVideo.encodeProgress(video_id, owner_id, hash=video_hash)  →  { percents, is_ready, image:[кадры обложки] }   # опрос до is_ready
shortVideo.edit(video_id, owner_id, description, privacy_view, privacy_comment)                 # метаданные/обложка

# Приватность видеозаписи сообщества — приложение «VK Видео», свой хост и свой токен.
# Снято с живого редактора 19.08.2026: тот же video.edit под токеном мессенджера на
# web.api.vk.ru отвечает успехом и настройку не применяет.
POST vkvideo.ru/al_video.php?act=web_token  (version=1, app_id=52461373)      # токен приложения
POST api.vkvideo.ru/method/video.edit?v=5.285&client_id=52461373
     owner_id, video_id, privacy_view=by_link|all|only_me[, name, desc]        # приватность записи
shortVideo.publish(video_id, owner_id, wallpost, publish_date=0, license_agree=1, ref)          # публикация
```

Публичный API делит этот флоу на `CreateUploadSessionAsync`, `UploadAsync` и
`CompletePublishAsync`. `VkClipUploadSession` можно сохранить между рестартами; её
`upload_url` считается секретом. Атомарный `PublishAsync` оставлен как совместимая обёртка.

Крупные клипы веб грузит на `upload.do` чанками (byte-range, 4 канала). Библиотека отправляет
один потоковый multipart POST полем `video_file`: файл не буферизуется целиком в памяти,
а `VkUploadSource` повторно открывает поток для сетевого ретрая.

## Публикация в сообщество

Для записи сообщества используется положительный `group_id` на этапах загрузки медиа и
отрицательный `owner_id` на этапе публикации:

```
photos.getWallUploadServer(group_id) → photos.saveWallPhoto(group_id, ...)
docs.getWallUploadServer(group_id)   → docs.save(...)
video.save(group_id, ...)            → upload
wall.post(owner_id=-group_id, from_group=1, attachments=..., guid=<stable-idempotency-key>)
```

Порядок ссылок в `attachments` совпадает с порядком `VkAttachmentSource`. При изменении
подписи `wall.getById` восстанавливает текущие ссылки вложений, после чего `wall.edit`
получает новый текст вместе с теми же вложениями.

## Разбор входящих фото

В `messages.getHistory` фото приходит как вложение `{ "type": "photo", "photo": { ... } }`:

```
photo.{ id, owner_id, access_key, sizes:[{type,url,width,height}], orig_photo:{url,width,height} }
```

Клиент выбирает наибольший по площади размер как основной URL (`VkPhotoAttachment.Url`),
сохраняя все размеры в `Sizes`.

## Что не реализовано

- **Реалтайм** (LongPoll на `queuev4.vk.ru`) — только модель запрос/ответ.
- Прочие типы вложений (видео, документы, аудио) при чтении лишь подсчитываются
  (`VkMessage.OtherAttachmentsCount`), но не разбираются.
