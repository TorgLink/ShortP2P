# ShortP2P — описание сетевого протокола

Документ описывает форматы кадров и логику обмена, реализованные в репозитории ShortP2P (клиент, discovery, крипто-слой). Числа целочисленные, если не указано иное: **BE** — big-endian, **LE** — little-endian.

---

## 1. Идентификация пиров

- **Identity v2**: пара **Ed25519** (подпись / отпечаток) + **X25519** (ECDH для handshake и server sealed-box).
- **NetworkId** — **12 байт** = первые 12 байт `SHA-256(Ed25519 public)` (`CompressedNetworkId.FromIdentityPublicKey`).
- В UI и в JSON QR — **короткая строка** (`CompressedNetworkId.ToShortString()`): base64url от 12 байт (без padding, ~16 символов).
- Публичный ключ в invite/QR — JSON `{"v":2,"ed":"...","x":"..."}`; приём **обязан** проверить `NetworkId == f(ed)`.
- Первый контакт (LAN invite / QR) — **TOFU** (ключ закрепляется). Смена ключа у известного контакта — только после явного Accept в UI.
- **BLE-реклама**: Manufacturer `[0x02][12 байт]`, GATT Service Data — `12 байт` (UUID сервиса в AD отдельно).
- Сравнение ролей в крипто-сессии чата: лексикографическое сравнение **12 байт** (`CompressedNetworkId.CompareTo`): пир с **меньшим** NetworkId — **лидер** handshake; пир с **большим** — только запрос установки сессии (см. §6).
- Миграция со старого RSA: при логине генерируются новые ключи и новый NetworkId, локальные чаты удаляются; бэкап личности — только `.tlp` со схемой v2.

---

## 2. Транспорты и порты UDP

| Назначение | Порт (константа в коде) | Примечание |
|------------|-------------------------|------------|
| Presence / LAN-«пинг» присутствия | **17501** (`PresencePingCodec.UdpPort`) | Широковещательно / с адреса пира; не data-чат. |
| Data-канал мессенджера (UDP) | **17500** по умолчанию (`PresencePingCodec.DefaultDataUdpPort`) | Задаётся в профиле пользователя (`UserEntity.DataUdpPort`); handshake, шифр, служебное. |
| Приглашение в чат (invite) | **17502** (`ChatInviteCodec.InviteUdpPort`) | Отдельный сокет на клиенте; в пакете invite указывается порт для **ответного** invite (обычно тоже 17502). |
| Discovery / gossip / маршрутная таблица | **17890** (`UdpPeerDiscoveryOptions.DefaultDiscoveryUdpPort`) | Beacon, probe, маршруты — см. §8. |

Помимо UDP поддерживается **Bluetooth** (`TransportKind.Bluetooth`): адреса в виде MAC; те же логические кадры invite / data могут передаваться по BT при включённом транспорте.

---

## 3. Presence ping (`0x31`)

Кадр объявляет узел в LAN: кто он, какой у него порт data-UDP, пресет «канала» и маска возможностей.

**Формат (минимально совместимые варианты длины):**

| Смещение | Поле |
|----------|------|
| 0 | `0x31` |
| 1–16 | NetworkId (16 байт) |
| 17–18 | Длина ника UTF-8, **uint16 BE** |
| 19… | Ник UTF-8 (до 512 байт) |
| После ника | `uint16 BE` — data UDP port (если поле отсутствует у очень старых клиентов — принимается **17500**) |
| +1 байт | `LinkTechnologyPreset` (enum byte) |
| +2 байта | `PresencePeerCapabilities`, **uint16 BE** (маска; бит `Chat` принудительно считается выставленным при разборе/сборке) |

Короткие датаграммы (только 17 байт и т.д.) поддерживаются для обратной совместимости — см. `PresencePingCodec.TryParse`.

---

## 4. Приглашение в чат — `ChatInviteCodec` (`0x30`)

Отправляется на **17502** (или по Bluetooth на адрес пира). После приёма пир добавляет/обновляет контакт в локальной БД и может ответить симметричным invite.

**Структура:**

| Смещение | Поле |
|----------|------|
| 0 | `0x30` |
| 1–4 | Магия ASCII `SP2I` |
| 5 | Версия wire: **1** |
| 6–21 | NetworkId инициатора (16 байт) |
| 22–23 | Длина ника UTF-8, **uint16 BE** |
| … | Ник |
| … | Длина identity public key JSON, **uint32 BE** |
| … | UTF-8 JSON публичного ключа (`IdentityKeySerializer`, `v:2`) |
| … | Длина списка хостов UTF-8, **uint16 BE** |
| … | Хосты (часто IPv4 через запятую; могут включать MAC для BT) |
| … | Порт для ответного invite / достижимости, **uint16 BE** (не порт data-чата) |

Поля строк кодируются в **UTF-8**. Длины — **BE**, кроме отдельно оговорённых форматов.

---

## 5. Канал данных чата (один UDP-сокет на пользователя)

На порту **data** (`UserEntity.DataUdpPort`, по умолчанию 17500) слушает **один** `UdpTransport` на `IPAddress.Any`; входящие датаграммы мультиплексируются по **первому байту** полезной нагрузки.

### 5.1. Допустимые первые байты на data-канале

| Байт | Имя | Назначение |
|------|-----|------------|
| `0x30` | Chat invite | Обрабатывается `IncomingChatInviteHandler` (тот же формат, что §4). |
| `0x01` | Identity handshake | `0x01` + `P2PHandshake.HandshakePacketBytes` (X25519 sealed session keys). |
| `0x04` | Session setup request | Ровно **17** байт: `0x04` + **16** байт NetworkId отправителя (wire Guid). |
| `0x02` | Cipher | `0x02` + внутренний шифропакет `P2PSession` (см. §7). |

Иные кадры с префиксом `0x02` и длиной > 1 идут в расшифровку и далее в messenger.

### 5.2. Отправка

Исходящие шифросообщения: один байт `0x02` + ciphertext от `P2PSession.Encrypt`. Invite и handshake уходят «сырыми» по тому же транспорту маршрутизации к адресам пира (UDP/BT + при необходимости relay, см. §9).

---

## 6. Установка крипто-сессии (X25519 → общий AES)

- Лидер генерирует ephemeral X25519, ECDH с **статическим X25519** пира, HKDF, AES-GCM оборачивает сессионный материал (**16 байт** AES-128 + **32 байта** MAC).
- Кадр handshake: **`0x01` + `P2PHandshake.HandshakePacketBytes`** (ver=2 | eph_pub | nonce | ciphertext | tag).

### 6.1. Роли по NetworkId

- Сравниваются wire-байты NetworkId локального пользователя и пира из чата.
- **Лидер** (меньший id): `P2PCrypto.CreateHandshakeInitiation`, отправляет **`0x01` + handshake body**.
- **Подписчик** (больший id): **не** шлёт `0x01`; отправляет только **`0x04` + 12 байт** своего NetworkId и ждёт handshake от лидера.
- Лидер при приёме **`0x04`** проверяет, что id совпадает с ожидаемым пиром, затем при необходимости выполняет свой handshake.
- Лидер **игнорирует** входящий `0x01`.
- Подписчик при приёме `0x01` вызывает `P2PCrypto.CreateSession(localPrivate, handshakeBody)` и поднимает `MessengerService`.

---

## 7. Симметричное шифрование (`P2PSession`)

После handshake стороны используют общий AES-128 + HMAC.

- **Пакет**: `IV (16)` || `ciphertext` || `tag (16)` — длина ciphertext кратна 16 (PKCS7), **общая длина пакета ≤ 128 байт**.
- **AES**: CBC, PKCS7.
- **Tag**: первые 16 байт HMAC-SHA256 от конкатенации IV||ciphertext с ключом MAC.

Ограничение размера: максимальный plaintext подбирается так, чтобы зашифрованный блок укладывался в 128 байт (**до 95 байт** полезной нагрузки в одном пакете — см. `P2PSession.MaxPlaintextBytes`).

---

## 8. Discovery / gossip (UDP 17890)

Общий порт с настройкой `UdpPeerDiscoveryOptions.DiscoveryPort` (по умолчанию **17890**).

### 8.1. Gossip probe / ack

| Кадр | Первый байт | Описание |
|------|-------------|----------|
| Probe | `0x40` | `nonce int64 LE` + `senderNetworkId 16` + `targetNetworkId 16` (длина фиксирована, см. `GossipWireCodec.ProbeLength`). |
| Ack | `0x41` | `nonce LE` + `responderNetworkId 16` + `dataUdpPort uint16 BE` + `nickLen uint16 BE` + UTF-8 nick. |

### 8.2. Маршрутная таблица по wire

| Кадр | Первый байт | Описание |
|------|-------------|----------|
| Request | `0x42` | `nonce LE` + `senderNetworkId 16`. |
| Reply | `0x43` | Заголовок с nonce, id ответчика, флагами и количеством маршрутов + сериализованные маршруты (`RouteTableWireCodec`). |

### 8.3. Профиль абонента (AboutMe + Avatar)

| Кадр | Первый байт | Описание |
|------|-------------|----------|
| Request | `0x44` | `nonce int64 LE` + `senderNetworkId` (длина как у route-table request). |
| Reply | `0x45` | `nonce LE` + `responderNetworkId` + `aboutLen uint16 BE` + AboutMe UTF-8 (≤250 символов / ≤1000 UTF-8 байт) + `avatarLen uint16 BE` + Avatar blob (≤12288 байт / 12 КБ). |

Запрос уходит unicast на discovery-порт пира при LAN-скане / presence; ответ хранится **только локально** (`peer_profiles`). Ошибки загрузки/разбора/записи — best-effort (лог + продолжение скана). Кодек: `PeerProfileWireCodec`.

### 8.4. Профиль через MessengerServer — `POST /api/v1/forward`

Для пиров, известных по серверу, но **не** в LAN: сервер только **эфемерно пересылает** (dumb pipe) online-абоненту. **Не** store-and-forward (`MessageDto` / `Blobs`). Кэш профиля — только локальный `peer_profiles`. JWT как у остальных `/api/v1/*`.

**Запрос** `POST /api/v1/forward` (JSON, camelCase):

| Поле | Тип | Обяз. | Описание |
|------|-----|-------|----------|
| `srcNetworkId` | string | да | Короткий NetworkId; должен совпадать с JWT |
| `tgtNetworkId` | string | да | Короткий NetworkId получателя |
| `kind` | string enum | да | `PeerProfileRequest` \| `PeerProfileReply` |
| `payloadBase64` | string | да | Полный кадр **`0x44`/`0x45`** (см. §8.3), без обрезки первого байта; v1 **без** `MessengerServerPayloadCodec` |
| `forwardId` | string | да | Клиентский id (UUID и т.п.) для идемпотентности POST |

Лимиты тела: как LAN — AboutMe ≤250 символов / ≤1000 UTF-8 байт, Avatar ≤12 KB (`PeerProfileLimits`); max decoded payload ≈ 13 313 байт (`0x45`).

**Ответы POST:**

| Код | Тело | Когда |
|-----|------|--------|
| `202` | пусто | принято в эфемерный inbox получателя |
| `409` | `ApiError { code: "PeerOffline", message }` | `tgt` не online → **без** очереди |
| `413` | `ApiError { code: "PayloadTooLarge", message }` | payload / avatar / about сверх лимита |
| `400` | `ApiError { code: "Validation", message }` | неверный kind, base64, несовпадение kind↔первый байт кадра, пустой `forwardId`, `src` ≠ JWT и т.п. |

**Доставка:** `GET /api/v1/events/poll` → `EventsPollResponse.forwards[]` (`ForwardDto`):

| Поле | Тип | Описание |
|------|-----|----------|
| `forwardId` | string | id пересылки |
| `srcNetworkId` | string | отправитель |
| `tgtNetworkId` | string | получатель (caller) |
| `kind` | string enum | те же значения, что в POST |
| `payloadBase64` | string | тот же полный `0x44`/`0x45` |
| `createdUtc` | DateTime | UTC штамп сервера при приёме |

После выдачи в poll — удалить из RAM (TTL ~60 с). **Без** durable store профиля. Шифрование оболочки — **не** в v1 (только TLS).

**Логирование (сервер):** на Information/Release — только `src`/`tgt` NetworkId, `forwardId`, идентификатор сервера (host), hash аватара и hash секции AboutMe (из разобранного `0x45`; для `0x44` — пусто). Полные тела — только Debug. Сервер **не** сохраняет AboutMe/Avatar в БД.

---

## 9. LAN-маршрутизация чата (`LanRoutingCodec`)

Используется для поиска пира и UDP-relay цепочек (до **3** хопов).

| Байт | Кадр | Магия / версия |
|------|------|----------------|
| `0x10` | FIND | Тело: `SP2F` + версия **1** + тип сообщения + searchId + targetNetworkId + ник + TTL + visited + path hops (UDP-адреса с префиксом длины **uint16 BE**). |
| `0x11` | FOUND | Аналогично с магией `SP2F`, публичный ключ JSON, host, порт, опционально relay hop и strip path. |
| `0x22` | RELAY | `hopCount` (1 байт) + для каждого хопа `uint16 BE` длина + raw UDP endpoint + **внутренняя** полезная нагрузка (например `0x02`+шифртекст). |

FIND: TTL **1…3**, число visited **≤ 8**, длина пути **≤ 3** UDP-адреса.

---

## 10. Сообщения мессенджера внутри шифра

Plaintext для `P2PSession` — либо **устаревший** сырой UTF-8 текста, либо бинарный кадр **`ChatWireCodec`**:

- Магия: ASCII **`S2P1`** (4 байта).
- Далее байт **типа**: `0x01` текст, `0x02` изображение, `0x03` файл.
- Длины внутри кадра — **little-endian** (`uint32` для текста/данных файла, `uint16` для MIME и имени файла).

Текст кодируется как UTF-8.

### 10.1. Служебный зонд крипто-сессии (не в истории UI)

После успешного handshake **лидер** (тот, кто слал `0x01`) может гонять проверку по зашифрованному каналу текстовыми строками:

- `CHAT <srcNetworkIdShort> <tgtNetworkIdShort> ACK`
- `CHAT <srcNetworkIdShort> <tgtNetworkIdShort> OK`

Формат разбора: `SessionCryptoProbe` (префикс `CHAT `, три токена, третий `ACK` или `OK`). Строки упаковываются через `ChatWireCodec.EncodeText` и передаются внутри `0x02`. Не сохраняются как обычные сообщения чата в БД.

---

## 11. QR контакта (вне UDP, JSON текст)

Версия **`v`: 1**. Поля (`PeerQrPayload`): `n` ник, `id` короткий NetworkId, `k` identity public key JSON (`v:2`), `p` порт, `h`/`ha` IPv4/IPv6, `b`/`ba` Bluetooth MAC. `id` должен совпадать с `f(k)`. Сериализация: `System.Text.Json` (`PeerQrCodec`).

---

## 12. Порядок типичного сценария «открыли чат»

1. Стороны обмениваются **invite** (`0x30`) на **17502** (и/или по BT), в БД появляется контакт с ключом и адресами.
2. Поднимается **data**-сессия: `StartAsync` шлёт invite на маршрут пира, затем выполняется логика §6 (лидер — `0x01`, подписчик — `0x04` и ожидание).
3. После `P2PSession` — обмен сообщениями кадрами **`0x02`**, внутри — `ChatWireCodec` или legacy UTF-8.
4. Опционально лидер запускает **crypto probe** ACK/OK по зашифрованному тексту.

---

## 13. Замечания по совместимости

- Расширения presence-пинга (порт, link preset, capabilities) опциональны; старые клиенты шлют короткие пакеты.
- Invite требует магию `SP2I` и версию **1**.
- LAN FIND/FOUND требуют магию `SP2F` и версию **1**.
- Ответчик с **большим** NetworkId не должен слать handshake первым.

---

## 14. Ссылки на код

| Тема | Файлы / типы |
|------|----------------|
| Invite | `ShortP2P.Client/Routing/ChatInviteCodec.cs` |
| Presence | `ShortP2P.Client/Routing/PresencePingCodec.cs` (кадр + `Transceiver`), `LinkTechnologyPreset`, `PresencePeerCapabilities` |
| Data кадры чата | `ShortP2P.Client/Services/ChatP2pSession.cs` |
| Identity / AES | `ShortP2P.Crypto/Identity*.cs`, `P2PHandshake.cs`, `P2PSession.cs`, `P2PCrypto.cs` |
| LAN find / relay | `ShortP2P.Client/Routing/LanRoutingCodec.cs` |
| Wire чата | `ShortP2P.Client/ChatMedia/ChatWireCodec.cs` |
| Зонд ACK/OK | `ShortP2P.Client/Services/SessionCryptoProbe.cs` |
| Gossip / route table | `ShortP2P.Discovery/Gossip/GossipWireCodec.cs`, `RouteTableWireCodec.cs` |
| Порт discovery | `ShortP2P.Discovery/UdpPeerDiscoveryOptions.cs` |
| QR JSON | `ShortP2P.Client/Qr/PeerQrCodec.cs`, `PeerQrPayload.cs` |
| NetworkId | `ShortP2P.Auth/Data/CompressedNetworkId.cs` |

Дополнительно: README проекта **ShortP2P.Discovery** (узлы и capability-маски в продуктовой терминологии).
