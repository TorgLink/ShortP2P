#!/usr/bin/env python3
"""Generate per-language ShortP2P protocol overview documents (ODT + PDF).

- One ODT + one PDF per language (en, ru, fr, es, de, zh-Hans).
- PDFs embed Unicode TTF fonts so Cyrillic / accents / CJK render correctly.
- Diagrams are rebuilt per language from diagrams/build_diagrams.py.
- The translation disclaimer (Chinese + English) appears only on the Chinese edition.
- The description is written for readers without a crypto background
  (junior developer level): every term is defined before it is used.
"""

from __future__ import annotations

import re
import sys
from collections.abc import Callable
from datetime import date
from pathlib import Path

from odf.draw import Frame, Image as OdfImage
from odf.opendocument import OpenDocumentText
from odf.style import (
    MasterPage,
    PageLayout,
    PageLayoutProperties,
    ParagraphProperties,
    Style,
    TextProperties,
)
from odf.text import H, P
from reportlab.graphics.shapes import Drawing, String
from reportlab.lib.enums import TA_CENTER, TA_LEFT, TA_RIGHT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import cm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import PageBreak as RLPageBreak
from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer
from svglib.svglib import svg2rlg

ROOT = Path(__file__).resolve().parent
DIAGRAMS = ROOT / "diagrams"
sys.path.insert(0, str(DIAGRAMS))
import build_diagrams  # noqa: E402
import build_infographics  # noqa: E402

AUTHOR = "Igor Evdokimov"
REPO_URL = "https://github.com/IskraMessenger/ShortP2P"
CREATED = date(2026, 11, 11)
MODIFIED = date(2026, 11, 11)
TITLE = "ShortP2P Protocol"
STATUS_EN = (
    "(Translation may contain inaccuracies; the English technical description "
    "and the source code are authoritative.)"
)
STATUS_ZH = "（译文可能存在不准确之处；以英文技术说明与源代码为准。）"

# Fonts for the PDF body.
FONT_EURO = Path(r"C:\Windows\Fonts\DejaVuSans.ttf")
FONT_EURO_BOLD = Path(r"C:\Windows\Fonts\DejaVuSans-Bold.ttf")
FONT_CJK = Path(r"C:\Windows\Fonts\msyh.ttc")
FONT_FALLBACK = Path(r"C:\Windows\Fonts\arial.ttf")
# Serif fonts for non-heading text: Times New Roman; SimSun for Chinese.
FONT_SERIF = Path(r"C:\Windows\Fonts\times.ttf")
FONT_SERIF_ZH = Path(r"C:\Windows\Fonts\simsun.ttc")

# Interface strings per document language.
UI_STRINGS: dict[str, dict[str, str]] = {
    "en": {
        "language": "Language",
        "contents": "Contents",
        "diagrams": "Shared diagrams",
        "description": "Protocol description",
        "cover_page": "Title page",
    },
    "ru": {
        "language": "Язык",
        "contents": "Содержание",
        "diagrams": "Общие схемы",
        "description": "Описание протокола",
        "cover_page": "Титульная страница",
    },
    "fr": {
        "language": "Langue",
        "contents": "Sommaire",
        "diagrams": "Schémas communs",
        "description": "Description du protocole",
        "cover_page": "Page de titre",
    },
    "es": {
        "language": "Idioma",
        "contents": "Contenido",
        "diagrams": "Esquemas compartidos",
        "description": "Descripción del protocolo",
        "cover_page": "Página de título",
    },
    "de": {
        "language": "Sprache",
        "contents": "Inhalt",
        "diagrams": "Gemeinsame Diagramme",
        "description": "Protokollbeschreibung",
        "cover_page": "Titelseite",
    },
    "zh": {
        "language": "语言",
        "contents": "目录",
        "diagrams": "通用图示",
        "description": "协议说明",
        "cover_page": "封面",
    },
}

# Numbered sections (1..6) and their infographic SVG stems.
SECTION_STEMS: dict[int, str] = {
    1: "sec1-identity",
    2: "sec2-transports",
    3: "sec3-session",
    4: "sec4-messages",
    5: "sec5-server",
    6: "sec6-discovery",
}

# Non-numbered headings that are also rendered bold.
EXTRA_HEADINGS: dict[str, set[str]] = {
    "en": {"BASIC TERMS (no prior cryptography knowledge required)", "FURTHER READING"},
    "ru": {"ОСНОВНЫЕ ПОНЯТИЯ (знание криптографии не требуется)", "ЧТО ЧИТАТЬ ДАЛЬШЕ"},
    "fr": {"NOTIONS DE BASE (aucune connaissance en cryptographie requise)", "POUR ALLER PLUS LOIN"},
    "es": {"CONCEPTOS BÁSICOS (sin conocimiento previo de criptografía)", "PARA SEGUIR LEYENDO"},
    "de": {"GRUNDBEGRIFFE (keine Krypto-Vorkenntnisse nötig)", "WEITERFÜHRENDES"},
    "zh": {"基础概念（无需任何密码学知识）", "延伸阅读"},
}


def numbered_section(line: str) -> int | None:
    match = re.match(r"^([1-6])\)", line)
    return int(match.group(1)) if match else None


# ---------------------------------------------------------------------------
# Description texts (junior-developer level; terms defined before use)
# ---------------------------------------------------------------------------

def lang_block_en() -> str:
    return """
ShortP2P is a peer-to-peer messaging stack. In plain words: two devices (peers) find each
other, agree on secret keys, and then exchange chat messages that nobody in between can
read. Transport can be direct (UDP, Bluetooth, mesh/LoRa radio) or indirect, through an
optional Messenger Server that only relays already-encrypted data.

BASIC TERMS (no prior cryptography knowledge required)

- Peer: the other device / person you chat with.
- NetworkId: 12 random bytes that identify a user, similar to a network address. In the UI
  and QR codes it is shown as a short base64url text (~16 characters).
- RSA-1024: an asymmetric algorithm — a pair of keys. The public key is shared openly (it
  travels inside invites and QR codes); the private key never leaves the device. Data
  encrypted with the public key can be decrypted only with the matching private key. In
  ShortP2P it is used once per session, to deliver the session secrets to the peer.
- Session keys: two random byte strings created for one chat session: an AES key (16
  bytes) and a MAC key (32 bytes). After the handshake both sides hold the same two strings.
- AES-128-CBC: a symmetric cipher — the same key encrypts and decrypts. CBC mode chains
  blocks so identical plaintext pieces look different; PKCS7 padding aligns the length to a
  multiple of 16 bytes.
- IV (initialization vector): 16 fresh random bytes generated for every packet, so the
  same text sent twice produces different ciphertext.
- HMAC-SHA256 tag: a keyed checksum (16 bytes are used) computed over IV + ciphertext.
  The receiver recomputes it and compares in constant time; a mismatch means the packet
  was tampered with or corrupted, and it is rejected.
- ChatWire: ShortP2P's own binary format for chat content. It is what gets encrypted and
  sent; the receiver decrypts it first, then parses it.
- S2P1: a 4-byte "magic signature" (ASCII characters "S2P1") at the start of every
  ChatWire frame. It works like a file header — the first thing the parser checks to
  recognize the format.
- Kind byte: the byte right after the magic. Its value says which message type follows.
- ACK / NACK: internal delivery reports. ACK = "message fully received"; NACK = "please
  resend the chunks with these numbers".
- TLS/HTTPS: the standard transport encryption between a client and the Messenger Server
  (the padlock icon in browsers).
- JWT: a signed token issued by the server at login. The client attaches it to every API
  request; the server verifies the signature instead of asking for the password again.
- End-to-end (E2E): only the two chatting devices can read the content. The Messenger
  Server stores and relays it as an opaque encrypted blob (Base64 text) and cannot read it.
- Bot: a program account on the server that clients can talk to; it also receives only
  encrypted content.
- Gossip / discovery: auxiliary traffic answering "who is nearby and how to reach them"
  (UDP port 17890).
- RELAY: a LAN routing frame that can carry an already-encrypted payload across
  intermediate nodes.

1) IDENTITY
Every user has a NetworkId (12 random bytes) and a long-term RSA-1024 key pair generated
at registration. The public key travels in plaintext inside chat invites (0x30) or QR
codes — this is safe, a public key is meant to be shared. Rule: the peer with the smaller
NetworkId becomes the "leader" of the crypto handshake; the other side waits.

2) TRANSPORTS
UDP ports by default: 17500 — chat data channel (handshake and encrypted packets), 17501
— presence ("I am online") announcements, 17502 — chat invites, 17890 — discovery/gossip.
Bluetooth and mesh/LoRa transports carry the same logical frames when enabled.
Communication with the Messenger Server always goes over HTTPS/TLS with JWT authentication.

3) SESSION (direct P2P), step by step
a. The follower may send frame 0x04 with its NetworkId: "I want a session".
b. The leader generates two random secrets (AES key 16 B + MAC key 32 B) and encrypts
   this 48-byte bundle with the peer's RSA public key using RSA-OAEP(SHA-1). The result
   is exactly 128 bytes and is sent as frame 0x01.
c. The follower decrypts it with its RSA private key; now both sides share the same
   two secrets.
d. Every following data frame is 0x02 + packet, where packet = IV(16) + AES-CBC
   ciphertext + HMAC tag(16), at most 1024 bytes. The receiver verifies the tag first,
   then decrypts.

4) APPLICATION MESSAGES (inside the encryption)
The plaintext fed into the cipher is usually a ChatWire frame: magic "S2P1", then the
kind byte, then the fields (lengths are little-endian unless noted otherwise).

The kind byte values, expanded:
- 0x01 Text — a plain chat text message (UTF-8), what a person types.
- 0x02 Image — an inline picture: MIME type (like "image/png") plus the image bytes.
- 0x03 File — a small file sent inside the chat: file name, MIME type, bytes.
- 0x04 Transfer offer — a JSON description of a big file offered for download outside the
  chat (which host/port to connect to, a one-time token, size, expiry).
- 0x05 Transfer control — the JSON answer to an offer: accept, reject, or an error.
- 0x06 User info — the sender's profile ("about me" text and avatar image), not shown as
  a chat bubble.
- 0x07 Vocabulary offer — a list of compression dictionaries the sender supports.
- 0x08 Vocabulary accept — the choice of dictionaries for this chat (or "UNCOMPRESSED").
- 0x09 Compressed text — a text encoded with the chosen dictionary to save traffic
  (useful on slow radio links).
- 0x0A Session probe — a small "ping" through the encrypted channel to confirm that
  both ends decrypt correctly.

Not ChatWire frames, but also sent encrypted: 0xAC ACK (message fully received, 17
bytes) and 0xAD NACK (please resend the listed chunk numbers). Very old clients send raw
UTF-8 without the S2P1 magic; the receiver still treats it as plain text.

One page per type with byte layouts and unencrypted examples: docs/messages. Crypto
pipelines with diagrams: docs/encryption.

5) MESSENGER SERVER AND BOTS
When peers cannot connect directly, they use the Messenger Server over HTTPS/TLS+JWT.
The chat body is end-to-end encrypted, so the server stores only an opaque Base64 blob.
- Codec v2 (preferred between people): RSA is used once to start a session; afterwards
  each message is AES-256-GCM (an authenticated cipher: encryption and integrity check
  in one algorithm) with a fresh 12-byte random nonce per message.
- Codec v1 (used for bots and as a fallback): every message gets a fresh AES-256-GCM key
  and nonce, and the key+nonce are wrapped with the recipient's RSA public key
  (RSA-OAEP-SHA256). Bots always use v1.
In both cases the server cannot decrypt the content.

6) DISCOVERY
Frames 0x40..0x45 on UDP 17890 (probe/ack, route tables, peer profiles) are mostly
plaintext control traffic: they help find peers and must not be confused with secret chat
content. LAN FIND/FOUND frames locate a peer's address; a RELAY frame can carry an
already-encrypted 0x02 payload through intermediate nodes.

FURTHER READING
- docs/encryption — crypto pipelines as vector diagrams (session setup, packet formats,
  server paths).
- docs/messages — one page per message type with unencrypted byte-level examples.
- protocol.md — detailed frame-level reference (in Russian).
""".strip()


def lang_block_ru() -> str:
    return """
ShortP2P — стек обмена сообщениями между устройствами напрямую. Простыми словами: два
устройства (пира) находят друг друга, договариваются о секретных ключах и дальше
обмениваются сообщениями чата, которые никто посередине прочитать не может. Передача
может идти напрямую (UDP, Bluetooth, радио mesh/LoRa) или через необязательный Messenger
Server, который пересылает уже зашифрованные данные.

ОСНОВНЫЕ ПОНЯТИЯ (знание криптографии не требуется)

- Пир (peer): второе устройство или человек, с которым вы общаетесь.
- NetworkId: 12 случайных байт — идентификатор пользователя, похож на сетевой адрес. В
  интерфейсе и QR-кодах показывается короткой строкой base64url (~16 символов).
- RSA-1024: асимметричный алгоритм — пара ключей. Публичный ключ открыто передаётся (в
  приглашениях и QR-кодах); приватный никогда не покидает устройство. То, что
  зашифровано публичным ключом, может расшифровать только владелец приватного. В
  ShortP2P он используется один раз за сессию, чтобы передать пиру секреты сессии.
- Ключи сессии: две случайные строки байтов, созданные на один сеанс чата: AES-ключ (16
  байт) и MAC-ключ (32 байта). После рукопожатия обе стороны держат одни и те же строки.
- AES-128-CBC: симметричный шифр — один и тот же ключ шифрует и расшифровывает. Режим
  CBC связывает блоки так, что одинаковые куски текста выглядят по-разному; дополнение
  PKCS7 выравнивает длину до кратной 16 байтам.
- IV (вектор инициализации): 16 свежих случайных байт на каждый пакет, чтобы одинаковый
  текст при каждом отправлении давал разный шифротекст.
- Метка HMAC-SHA256: контрольная сумма с ключом (используются 16 байт), считается по
  IV + шифротексту. Получатель пересчитывает и сравнивает за постоянное время;
  несовпадение означает, что пакет подменён или повреждён, и он отбрасывается.
- ChatWire: собственный бинарный формат ShortP2P для содержимого чата. Именно он
  шифруется и отправляется; получатель сначала расшифровывает, потом разбирает.
- S2P1: 4-байтовая «магическая подпись» (символы ASCII "S2P1") в начале каждого кадра
  ChatWire. Работает как заголовок файла — первое, что проверяет разборщик, чтобы узнать
  формат.
- Байт типа (kind): байт сразу после магии. Его значение говорит, какое сообщение идёт
  дальше.
- ACK / NACK: внутренние отчёты о доставке. ACK = «сообщение получено полностью»; NACK =
  «пришлите заново куски с такими номерами».
- TLS/HTTPS: стандартное шифрование канала между клиентом и Messenger Server (значок
  замка в браузерах).
- JWT: подписанный маркер, который сервер выдаёт при входе. Клиент прикладывает его к
  каждому запросу; сервер проверяет подпись, не спрашивая пароль заново.
- Сквозное шифрование (E2E): прочитать содержимое могут только два общающихся
  устройства. Messenger Server хранит и пересылает его как непрозрачную зашифрованную
  последовательность байт (текст Base64) и сам прочитать не может.
- Бот: программный аккаунт на сервере, с которым могут общаться клиенты; он тоже
  получает только зашифрованное.
- Gossip / discovery: вспомогательный трафик, отвечающий на вопрос «кто рядом и как до
  него достучаться» (UDP-порт 17890).
- RELAY: кадр маршрутизации в локальной сети, который может нести уже зашифрованные
  данные через промежуточные узлы.

1) ИДЕНТИЧНОСТЬ
У каждого пользователя есть NetworkId (12 случайных байт) и долгосрочная пара ключей
RSA-1024, созданная при регистрации. Публичный ключ передаётся открыто в приглашениях
чата (0x30) или QR-кодах — это безопасно, публичный ключ и задуман для передачи.
Правило: пир с меньшим NetworkId становится «лидером» крипторукопожатия, вторая сторона
ждёт.

2) ТРАНСПОРТЫ
UDP-порты по умолчанию: 17500 — канал данных чата (рукопожатие и зашифрованные пакеты),
17501 — объявления присутствия («я в сети»), 17502 — приглашения в чат, 17890 —
discovery/gossip. Bluetooth и mesh/LoRa переносят те же логические кадры при включении.
Связь с Messenger Server всегда идёт по HTTPS/TLS с аутентификацией JWT.

3) СЕССИЯ (прямой P2P), по шагам
a. Подписчик может отправить кадр 0x04 со своим NetworkId: «хочу сессию».
b. Лидер генерирует два случайных секрета (AES-ключ 16 Б + MAC-ключ 32 Б) и шифрует эту
   48-байтовую связку публичным ключом пира по RSA-OAEP(SHA-1). Результат — ровно 128
   байт, отправляется кадром 0x01.
c. Подписчик расшифровывает своим приватным RSA-ключом; теперь у обеих сторон одни и те
   же два секрета.
d. Каждый следующий кадр данных — 0x02 + пакет, где пакет = IV(16) + шифротекст AES-CBC
   + метка HMAC(16), максимум 1024 байта. Получатель сначала проверяет метку, потом
   расшифровывает.

4) ПРИКЛАДНЫЕ СООБЩЕНИЯ (внутри шифрования)
Открытый текст, попадающий в шифр, — обычно кадр ChatWire: магия "S2P1", затем байт
типа, затем поля (длины little-endian, если не указано иное).

Значения байта типа, раскрыто:
- 0x01 Text — обычное текстовое сообщение чата (UTF-8), то, что набирает человек.
- 0x02 Image — картинка, передаваемая в самом чате: тип MIME (например "image/png") и
  байты изображения.
- 0x03 File — небольшой файл внутри чата: имя, тип MIME, байты.
- 0x04 Transfer offer — JSON-описание большого файла, предложенного для загрузки вне
  чата (к какому узлу/порту подключаться, одноразовый маркер, размер, срок действия).
- 0x05 Transfer control — JSON-ответ на предложение: принять, отклонить или ошибка.
- 0x06 User info — профиль отправителя («о себе» и аватар), не показывается как
  сообщение чата.
- 0x07 Vocabulary offer — список словарей сжатия, которые поддерживает отправитель.
- 0x08 Vocabulary accept — выбор словарей для этого чата (или "UNCOMPRESSED").
- 0x09 Compressed text — текст, закодированный выбранным словарём для экономии трафика
  (полезно на медленных радиоканалах).
- 0x0A Session probe — маленький проверочный сигнал («ping») через зашифрованный канал,
  чтобы убедиться, что обе стороны корректно расшифровывают.

Не кадры ChatWire, но тоже отправляются зашифрованно: 0xAC ACK (сообщение получено
полностью, 17 байт) и 0xAD NACK (перешлите перечисленные номера кусков). Очень старые
клиенты отправляют сырой UTF-8 без магии S2P1; получатель всё равно считает это
текстом.

По одной странице на тип с байтовыми раскладками и незашифрованными примерами:
docs/messages. Криптоконвейеры со схемами: docs/encryption.

5) MESSENGER SERVER И БОТЫ
Если пиры не могут соединиться напрямую, используется Messenger Server по HTTPS/TLS+JWT.
Тело чата зашифровано сквозным образом, поэтому сервер хранит только непрозрачный блок
Base64.
- Кодек v2 (формат упаковки, предпочтительный между людьми): RSA используется один раз
  для заведения сессии; дальше каждое сообщение — AES-256-GCM (аутентифицированный
  шифр: шифрование и проверку целостности выполняет один алгоритм) со свежим случайным
  12-байтовым nonce (одноразовым числом) на каждое сообщение.
- Кодек v1 (для ботов и как запасной путь): у каждого сообщения свой свежий ключ
  AES-256-GCM и nonce, а ключ+nonce оборачиваются публичным ключом получателя
  (RSA-OAEP-SHA256). Боты всегда используют v1.
В обоих случаях сервер не может расшифровать содержимое.

6) DISCOVERY
Кадры 0x40..0x45 на UDP 17890 (проверки/ответы, таблицы маршрутов, профили пиров) — в
основном открытый служебный трафик: он помогает находить пиров, и его не надо путать с
секретным содержимым чата. Кадры LAN FIND/FOUND находят адрес пира; кадр RELAY может
нести уже зашифрованные данные 0x02 через промежуточные узлы.

ЧТО ЧИТАТЬ ДАЛЬШЕ
- docs/encryption — криптоконвейеры в виде векторных схем (создание сессии, форматы
  пакетов, серверные пути).
- docs/messages — по странице на тип сообщения с незашифрованными примерами на уровне
  байтов.
- protocol.md — подробное описание кадров (на русском).
""".strip()


def lang_block_fr() -> str:
    return """
ShortP2P est une pile de messagerie entre pairs. En mots simples : deux appareils
(pairs) se trouvent, se mettent d'accord sur des clés secrètes, puis échangent des
messages de chat que personne au milieu ne peut lire. Le transport peut être direct
(UDP, Bluetooth, radio mesh/LoRa) ou passer par un Messenger Server facultatif qui ne
relaie que des données déjà chiffrées.

NOTIONS DE BASE (aucune connaissance en cryptographie requise)

- Pair (peer) : l'autre appareil / la personne avec qui vous discutez.
- NetworkId : 12 octets aléatoires identifiant un utilisateur, comme une adresse réseau.
  Dans l'interface et les QR codes, il apparaît en texte base64url court (~16 caractères).
- RSA-1024 : un algorithme asymétrique — une paire de clés. La clé publique se partage
  ouvertement (invitations, QR codes) ; la clé privée ne quitte jamais l'appareil. Ce qui
  est chiffré avec la clé publique ne peut être déchiffré qu'avec la clé privée
  correspondante. Dans ShortP2P, on l'utilise une fois par session pour envoyer les
  secrets de session au pair.
- Clés de session : deux chaînes d'octets aléatoires créées pour une session de chat :
  une clé AES (16 octets) et une clé MAC (32 octets). Après la poignée de main, les deux
  côtés possèdent les mêmes deux chaînes.
- AES-128-CBC : un chiffrement symétrique — la même clé chiffre et déchiffre. Le mode
  CBC enchaîne les blocs pour que deux fragments identiques paraissent différents ; le
  remplissage PKCS7 aligne la longueur sur un multiple de 16 octets.
- IV (vecteur d'initialisation) : 16 octets aléatoires neufs pour chaque paquet, afin
  que le même texte produise un texte chiffré différent à chaque envoi.
- Étiquette HMAC-SHA256 : une somme de contrôle avec clé (16 octets utilisés) calculée
  sur IV + texte chiffré. Le destinataire recalcule et compare en temps constant ; une
  différence signifie un paquet altéré ou corrompu, il est rejeté.
- ChatWire : le format binaire propre de ShortP2P pour le contenu du chat. C'est lui qui
  est chiffré et envoyé ; le destinataire déchiffre d'abord, puis l'analyse.
- S2P1 : une « signature magique » de 4 octets (ASCII "S2P1") au début de chaque trame
  ChatWire. Elle sert d'en-tête : la première chose que l'analyseur vérifie pour
  reconnaître le format.
- Octet de type (kind) : l'octet juste après la magie. Sa valeur indique quel type de
  message suit.
- ACK / NACK : rapports internes de livraison. ACK = « message entièrement reçu » ;
  NACK = « renvoyez les morceaux portant ces numéros ».
- TLS/HTTPS : le chiffrement de transport standard entre le client et le Messenger
  Server (le cadenas des navigateurs).
- JWT : un jeton signé remis par le serveur à la connexion. Le client le joint à chaque
  requête ; le serveur vérifie la signature au lieu de redemander le mot de passe.
- Chiffrement de bout en bout (E2E) : seuls les deux appareils qui discutent peuvent
  lire le contenu. Le Messenger Server le stocke et le relaie comme un bloc chiffré
  opaque (texte Base64) et ne peut pas le lire.
- Bot : un compte programme sur le serveur avec lequel les clients peuvent parler ; lui
  aussi ne reçoit que du contenu chiffré.
- Gossip / discovery : le trafic annexe qui répond à « qui est là et comment l'atteindre
  » (UDP 17890).
- RELAY : une trame de routage LAN capable de transporter une charge déjà chiffrée via
  des nœuds intermédiaires.

1) IDENTITÉ
Chaque utilisateur possède un NetworkId (12 octets aléatoires) et une paire RSA-1024
créée à l'inscription. La clé publique circule en clair dans les invitations (0x30) ou
les QR codes — c'est sans danger, une clé publique est faite pour être partagée. Règle :
le pair au NetworkId le plus petit devient le « leader » de la poignée de main ; l'autre
attend.

2) TRANSPORTS
Ports UDP par défaut : 17500 — canal de données du chat (poignée de main et paquets
chiffrés), 17501 — annonces de présence, 17502 — invitations, 17890 — discovery/gossip.
Bluetooth et mesh/LoRa transportent les mêmes trames logiques quand ils sont actifs. La
communication avec le Messenger Server passe toujours par HTTPS/TLS avec
authentification JWT.

3) SESSION (P2P direct), étape par étape
a. Le suiveur peut envoyer la trame 0x04 avec son NetworkId : « je veux une session ».
b. Le leader génère deux secrets aléatoires (clé AES 16 o + clé MAC 32 o) et chiffre ce
   paquet de 48 octets avec la clé publique RSA du pair via RSA-OAEP(SHA-1). Le résultat
   fait exactement 128 octets et part en trame 0x01.
c. Le suiveur déchiffre avec sa clé privée RSA ; les deux côtés partagent alors les
   mêmes secrets.
d. Chaque trame de données suivante est 0x02 + paquet, où paquet = IV(16) + texte
   chiffré AES-CBC + étiquette HMAC(16), au plus 1024 octets. Le destinataire vérifie
   d'abord l'étiquette, puis déchiffre.

4) MESSAGES APPLICATIFS (dans le chiffrement)
Le texte clair passé au chiffrement est généralement une trame ChatWire : magie "S2P1",
puis l'octet de type, puis les champs (longueurs en little-endian sauf mention
contraire).

Valeurs de l'octet de type, en détail :
- 0x01 Text — un simple message texte (UTF-8), ce qu'une personne tape.
- 0x02 Image — une image dans le chat : type MIME (ex. "image/png") et octets de
  l'image.
- 0x03 File — un petit fichier envoyé dans le chat : nom, type MIME, octets.
- 0x04 Transfer offer — un JSON décrivant un gros fichier proposé au téléchargement
  hors du chat (hôte/port, jeton à usage unique, taille, expiration).
- 0x05 Transfer control — la réponse JSON à une offre : accepter, refuser, ou erreur.
- 0x06 User info — le profil de l'expéditeur (texte « à propos » et avatar), non
  affiché comme bulle de chat.
- 0x07 Vocabulary offer — la liste des dictionnaires de compression que l'expéditeur
  gère.
- 0x08 Vocabulary accept — le choix des dictionnaires pour ce chat (ou
  "UNCOMPRESSED").
- 0x09 Compressed text — un texte encodé avec le dictionnaire choisi pour économiser
  de la bande passante (utile sur radio lente).
- 0x0A Session probe — un petit « ping » dans le canal chiffré pour confirmer que les
  deux bouts déchiffrent correctement.

Hors ChatWire, mais eux aussi chiffrés : 0xAC ACK (message entièrement reçu, 17 octets)
et 0xAD NACK (renvoyer les numéros de morceaux listés). Les très vieux clients envoient
de l'UTF-8 brut sans la magie S2P1 ; le destinataire le traite quand même comme du
texte.

Une page par type avec structures d'octets et exemples non chiffrés : docs/messages. Les
pipelines chiffrés en schémas : docs/encryption.

5) MESSENGER SERVER ET BOTS
Quand les pairs ne peuvent pas se joindre directement, ils passent par le Messenger
Server en HTTPS/TLS+JWT. Le corps du chat est chiffré de bout en bout : le serveur ne
stocke qu'un bloc Base64 opaque.
- Codec v2 (préféré entre humains) : RSA sert une fois pour établir la session ;
  ensuite chaque message est en AES-256-GCM (un chiffrement authentifié : chiffrement et
  contrôle d'intégrité en un seul algorithme) avec un nonce aléatoire de 12 octets par
  message.
- Codec v1 (pour les bots et en secours) : chaque message reçoit une clé AES-256-GCM et
  un nonce neufs, et la clé+nonce sont enveloppés avec la clé publique RSA du
  destinataire (RSA-OAEP-SHA256). Les bots utilisent toujours v1.
Dans les deux cas, le serveur ne peut pas déchiffrer le contenu.

6) DISCOVERY
Les trames 0x40..0x45 sur UDP 17890 (sondes/accusés, tables de routes, profils) sont
surtout du trafic de contrôle en clair : elles servent à trouver les pairs et ne doivent
pas être confondues avec le contenu secret du chat. Les trames LAN FIND/FOUND localisent
l'adresse d'un pair ; une trame RELAY peut transporter une charge 0x02 déjà chiffrée
via des nœuds intermédiaires.

POUR ALLER PLUS LOIN
- docs/encryption — les pipelines chiffrés en diagrammes vectoriels (établissement de
  session, formats de paquets, chemins serveur).
- docs/messages — une page par type de message avec exemples d'octets non chiffrés.
- protocol.md — référence détaillée des trames (en russe).
""".strip()


def lang_block_es() -> str:
    return """
ShortP2P es una pila de mensajería entre pares. En palabras simples: dos dispositivos
(pares) se encuentran, acuerdan claves secretas y luego intercambian mensajes de chat
que nadie en medio puede leer. El transporte puede ser directo (UDP, Bluetooth, radio
mesh/LoRa) o pasar por un Messenger Server opcional que solo reenvía datos ya cifrados.

CONCEPTOS BÁSICOS (sin conocimiento previo de criptografía)

- Par (peer): el otro dispositivo / la persona con quien conversa.
- NetworkId: 12 bytes aleatorios que identifican a un usuario, como una dirección de
  red. En la interfaz y los códigos QR aparece como texto base64url corto (~16
  caracteres).
- RSA-1024: un algoritmo asimétrico — un par de claves. La clave pública se comparte
  abiertamente (invitaciones, QR); la clave privada nunca sale del dispositivo. Lo
  cifrado con la clave pública solo puede descifrarse con la clave privada
  correspondiente. En ShortP2P se usa una vez por sesión para enviar los secretos de
  sesión al par.
- Claves de sesión: dos cadenas de bytes aleatorias creadas para una sesión de chat:
  una clave AES (16 bytes) y una clave MAC (32 bytes). Tras el handshake, ambos lados
  poseen las mismas dos cadenas.
- AES-128-CBC: un cifrado simétrico — la misma clave cifra y descifra. El modo CBC
  encadena bloques para que fragmentos idénticos parezcan distintos; el relleno PKCS7
  alinea la longitud a múltiplos de 16 bytes.
- IV (vector de inicialización): 16 bytes aleatorios nuevos por paquete, para que el
  mismo texto produzca un cifrado distinto en cada envío.
- Etiqueta HMAC-SHA256: una suma de control con clave (se usan 16 bytes) calculada
  sobre IV + texto cifrado. El destinatario recalcula y compara en tiempo constante;
  una diferencia significa paquete alterado o corrupto, y se rechaza.
- ChatWire: el formato binario propio de ShortP2P para el contenido del chat. Es lo que
  se cifra y envía; el destinatario primero descifra y luego lo analiza.
- S2P1: una «firma mágica» de 4 bytes (ASCII "S2P1") al inicio de cada trama ChatWire.
  Funciona como cabecera: lo primero que el analizador comprueba para reconocer el
  formato.
- Byte de tipo (kind): el byte justo después de la magia. Su valor indica qué tipo de
  mensaje sigue.
- ACK / NACK: informes internos de entrega. ACK = «mensaje recibido por completo»;
  NACK = «reenvíen los fragmentos con estos números».
- TLS/HTTPS: el cifrado de transporte estándar entre cliente y Messenger Server (el
  candado de los navegadores).
- JWT: un token firmado que el servidor entrega al iniciar sesión. El cliente lo
  adjunta en cada solicitud; el servidor verifica la firma en vez de pedir la
  contraseña otra vez.
- Cifrado de extremo a extremo (E2E): solo los dos dispositivos que conversan pueden
  leer el contenido. El Messenger Server lo almacena y reenvía como un bloque cifrado
  opaco (texto Base64) y no puede leerlo.
- Bot: una cuenta de programa en el servidor con la que los clientes pueden hablar;
  también recibe solo contenido cifrado.
- Gossip / discovery: el tráfico auxiliar que responde «quién está cerca y cómo
  alcanzarlo» (UDP 17890).
- RELAY: una trama de enrutamiento LAN capaz de transportar una carga ya cifrada a
  través de nodos intermedios.

1) IDENTIDAD
Cada usuario tiene un NetworkId (12 bytes aleatorios) y un par de claves RSA-1024 creado
al registrarse. La clave pública viaja en claro en las invitaciones (0x30) o códigos QR
— es seguro, una clave pública está hecha para compartirse. Regla: el par con el
NetworkId menor es el «líder» del handshake; el otro espera.

2) TRANSPORTES
Puertos UDP por defecto: 17500 — canal de datos del chat (handshake y paquetes
cifrados), 17501 — anuncios de presencia, 17502 — invitaciones, 17890 —
discovery/gossip. Bluetooth y mesh/LoRa transportan las mismas tramas lógicas si están
activos. La comunicación con el Messenger Server siempre va por HTTPS/TLS con
autenticación JWT.

3) SESIÓN (P2P directo), paso a paso
a. El seguidor puede enviar la trama 0x04 con su NetworkId: «quiero una sesión».
b. El líder genera dos secretos aleatorios (clave AES 16 B + clave MAC 32 B) y cifra
   este paquete de 48 bytes con la clave pública RSA del par mediante RSA-OAEP(SHA-1).
   El resultado mide exactamente 128 bytes y se envía como trama 0x01.
c. El seguidor descifra con su clave privada RSA; ahora ambos lados comparten los
   mismos secretos.
d. Cada trama de datos siguiente es 0x02 + paquete, donde paquete = IV(16) + texto
   cifrado AES-CBC + etiqueta HMAC(16), como máximo 1024 bytes. El destinatario
   verifica primero la etiqueta y luego descifra.

4) MENSAJES DE APLICACIÓN (dentro del cifrado)
El texto claro que entra al cifrado normalmente es una trama ChatWire: magia "S2P1",
luego el byte de tipo, luego los campos (longitudes little-endian salvo indicación
contraria).

Valores del byte de tipo, ampliados:
- 0x01 Text — un mensaje de texto normal (UTF-8), lo que escribe una persona.
- 0x02 Image — una imagen en el chat: tipo MIME (p. ej. "image/png") y los bytes de la
  imagen.
- 0x03 File — un archivo pequeño dentro del chat: nombre, tipo MIME, bytes.
- 0x04 Transfer offer — un JSON que describe un archivo grande ofrecido para descarga
  fuera del chat (host/puerto, token de un solo uso, tamaño, expiración).
- 0x05 Transfer control — la respuesta JSON a una oferta: aceptar, rechazar o error.
- 0x06 User info — el perfil del remitente (texto «acerca de» y avatar), no se muestra
  como burbuja de chat.
- 0x07 Vocabulary offer — la lista de diccionarios de compresión que soporta el
  remitente.
- 0x08 Vocabulary accept — la elección de diccionarios para este chat (o
  "UNCOMPRESSED").
- 0x09 Compressed text — un texto codificado con el diccionario elegido para ahorrar
  tráfico (útil en radio lenta).
- 0x0A Session probe — un pequeño «ping» por el canal cifrado para confirmar que ambos
  extremos descifran correctamente.

No son tramas ChatWire, pero también se envían cifrados: 0xAC ACK (mensaje recibido por
completo, 17 bytes) y 0xAD NACK (reenvíen los números de fragmentos listados). Los
clientes muy antiguos envían UTF-8 crudo sin la magia S2P1; el destinatario lo trata
igualmente como texto.

Una página por tipo con disposiciones de bytes y ejemplos sin cifrar: docs/messages. Los
procesos de cifrado en esquemas: docs/encryption.

5) MESSENGER SERVER Y BOTS
Cuando los pares no pueden conectarse directamente, usan el Messenger Server por
HTTPS/TLS+JWT. El cuerpo del chat está cifrado de extremo a extremo: el servidor solo
guarda un bloque Base64 opaco.
- Códec v2 (preferido entre personas): RSA se usa una vez para establecer la sesión;
  después cada mensaje va en AES-256-GCM (un cifrado autenticado: cifrado e integridad
  en un solo algoritmo) con un nonce aleatorio de 12 bytes por mensaje.
- Códec v1 (para bots y como respaldo): cada mensaje recibe una clave AES-256-GCM y un
  nonce nuevos, y la clave+nonce se envuelven con la clave pública RSA del destinatario
  (RSA-OAEP-SHA256). Los bots siempre usan v1.
En ambos casos el servidor no puede descifrar el contenido.

6) DISCOVERY
Las tramas 0x40..0x45 en UDP 17890 (sondeos/acuses, tablas de rutas, perfiles) son sobre
todo tráfico de control en claro: sirven para encontrar pares y no deben confundirse
con el contenido secreto del chat. Las tramas LAN FIND/FOUND localizan la dirección de
un par; una trama RELAY puede transportar una carga 0x02 ya cifrada a través de nodos
intermedios.

PARA SEGUIR LEYENDO
- docs/encryption — los procesos de cifrado como diagramas vectoriales (establecimiento
  de sesión, formatos de paquetes, rutas por servidor).
- docs/messages — una página por tipo de mensaje con ejemplos de bytes sin cifrar.
- protocol.md — referencia detallada de tramas (en ruso).
""".strip()


def lang_block_de() -> str:
    return """
ShortP2P ist ein Peer-to-Peer-Messaging-Stack. Einfach gesagt: Zwei Geräte (Peers)
finden einander, vereinbaren geheime Schlüssel und tauschen dann Nachrichten aus, die
dazwischen niemand lesen kann. Der Transport kann direkt sein (UDP, Bluetooth,
Mesh/LoRa-Funk) oder über einen optionalen Messenger Server laufen, der nur bereits
verschlüsselte Daten weiterleitet.

GRUNDBEGRIFFE (keine Krypto-Vorkenntnisse nötig)

- Peer: das andere Gerät / die Person, mit der man schreibt.
- NetworkId: 12 Zufallsbytes, die einen Nutzer identifizieren — wie eine
  Netzwerkadresse. In UI und QR-Codes als kurzer base64url-Text (~16 Zeichen).
- RSA-1024: ein asymmetrisches Verfahren — ein Schlüsselpaar. Der öffentliche Schlüssel
  wird offen geteilt (in Einladungen und QR-Codes); der private verlässt das Gerät nie.
  Was mit dem öffentlichen Schlüssel verschlüsselt wurde, kann nur mit dem zugehörigen
  privaten Schlüssel entschlüsselt werden. In ShortP2P wird dies einmal pro Sitzung
  benutzt, um dem Peer die Sitzungsgeheimnisse zu senden.
- Sitzungsschlüssel: zwei zufällige Byteketten, erzeugt für eine Chat-Sitzung: ein
  AES-Schlüssel (16 Bytes) und ein MAC-Schlüssel (32 Bytes). Danach besitzen beide
  Seiten dieselben zwei Ketten.
- AES-128-CBC: eine symmetrische Verschlüsselung — derselbe Schlüssel ver- und
  entschlüsselt. Der CBC-Modus verkettet Blöcke, damit identische Textstücke unterschiedlich
  aussehen; PKCS7-Padding richtet die Länge auf ein Vielfaches von 16 Bytes aus.
- IV (Initialisierungsvektor): 16 frische Zufallsbytes pro Paket, damit derselbe Text
  bei jedem Senden anders verschlüsselt aussieht.
- HMAC-SHA256-Tag: eine schlüsselgestützte Prüfsumme (16 Bytes werden verwendet) über
  IV + Geheimtext. Der Empfänger rechnet sie neu und vergleicht in konstanter Zeit; eine
  Abweichung heißt: Paket verändert oder beschädigt — es wird verworfen.
- ChatWire: ShortP2Ps eigenes Binärformat für Chatinhalte. Genau das wird verschlüsselt
  und gesendet; der Empfänger entschlüsselt zuerst und zerlegt es danach.
- S2P1: eine 4-Byte-„magische Signatur“ (ASCII "S2P1") am Anfang jedes ChatWire-Frames.
  Sie wirkt wie ein Datei-Header — das Erste, was der Parser prüft, um das Format zu
  erkennen.
- Kind-Byte: das Byte direkt nach der Magie. Sein Wert sagt, welcher Nachrichtentyp
  folgt.
- ACK / NACK: interne Zustellberichte. ACK = „Nachricht vollständig empfangen“; NACK =
  „bitte die Datenblöcke mit diesen Nummern erneut senden“.
- TLS/HTTPS: die übliche Transportverschlüsselung zwischen Client und Messenger Server
  (das Schloss im Browser).
- JWT: ein signiertes Token, das der Server beim Login ausstellt. Der Client hängt es
  an jede Anfrage; der Server prüft die Signatur, statt erneut das Passwort zu
  verlangen.
- Ende-zu-Ende (E2E): den Inhalt können nur die beiden chattenden Geräte lesen. Der
  Messenger Server speichert und leitet ihn als opaken verschlüsselten Block (Base64-
  Text) weiter und kann ihn selbst nicht lesen.
- Bot: ein Programm-Account auf dem Server, mit dem Clients reden können; auch er
  erhält nur verschlüsselten Inhalt.
- Gossip / Discovery: Hilfsverkehr, der „wer ist in der Nähe und wie erreiche ich ihn“
  beantwortet (UDP 17890).
- RELAY: ein LAN-Routing-Frame, der bereits verschlüsselte Nutzdaten über Zwischenknoten
  tragen kann.

1) IDENTITÄT
Jeder Nutzer hat eine NetworkId (12 Zufallsbytes) und ein bei der Registrierung
erzeugtes RSA-1024-Schlüsselpaar. Der öffentliche Schlüssel reist im Klartext in
Chat-Einladungen (0x30) oder QR-Codes — das ist unkritisch, denn ein öffentlicher
Schlüssel ist zum Teilen da. Regel: Der Peer mit der kleineren NetworkId wird „Leader“
des Handshakes; die andere Seite wartet.

2) TRANSPORTE
UDP-Ports standardmäßig: 17500 — Datenkanal des Chats (Handshake und verschlüsselte
Pakete), 17501 — Anwesenheitsmeldungen, 17502 — Einladungen, 17890 — Discovery/Gossip.
Bluetooth und Mesh/LoRa tragen dieselben logischen Frames, wenn aktiviert. Die
Verbindung zum Messenger Server läuft immer über HTTPS/TLS mit JWT-Authentifizierung.

3) SITZUNG (direktes P2P), Schritt für Schritt
a. Der Follower kann Frame 0x04 mit seiner NetworkId senden: „Ich möchte eine
   Sitzung“.
b. Der Leader erzeugt zwei Zufallsgeheimnisse (AES-Schlüssel 16 B + MAC-Schlüssel 32 B)
   und verschlüsselt dieses 48-Byte-Bündel mit dem öffentlichen RSA-Schlüssel des Peers
   per RSA-OAEP(SHA-1). Das Ergebnis ist genau 128 Bytes und wird als Frame 0x01
   gesendet.
c. Der Follower entschlüsselt mit seinem privaten RSA-Schlüssel; danach haben beide
   Seiten dieselben Geheimnisse.
d. Jeder weitere Datenframe ist 0x02 + Paket, wobei Paket = IV(16) + AES-CBC-Geheimtext
   + HMAC-Tag(16), höchstens 1024 Bytes. Der Empfänger prüft zuerst das Tag, dann
   entschlüsselt er.

4) ANWENDUNGSNACHRICHTEN (innerhalb der Verschlüsselung)
Der Klartext, der in die Verschlüsselung geht, ist meist ein ChatWire-Frame: Magie
"S2P1", dann das Kind-Byte, dann Felder (Längen Little-Endian, sofern nicht anders
angegeben).

Die Kind-Byte-Werte, ausgeführt:
- 0x01 Text — eine normale Textnachricht (UTF-8), was ein Mensch eintippt.
- 0x02 Image — ein Bild im Chat: MIME-Typ (z. B. "image/png") plus die Bilddaten.
- 0x03 File — eine kleine Datei im Chat: Name, MIME-Typ, Bytes.
- 0x04 Transfer offer — eine JSON-Beschreibung einer großen Datei, die außerhalb des
  Chats zum Download angeboten wird (Host/Port, Einmal-Token, Größe, Ablauf).
- 0x05 Transfer control — die JSON-Antwort auf ein Angebot: annehmen, ablehnen oder
  Fehler.
- 0x06 User info — das Profil des Absenders („Über mich“-Text und Avatar), nicht als
  Chat-Bubble sichtbar.
- 0x07 Vocabulary offer — die Liste der Kompressionswörterbücher, die der Absender
  beherrscht.
- 0x08 Vocabulary accept — die Wahl der Wörterbücher für diesen Chat (oder
  "UNCOMPRESSED").
- 0x09 Compressed text — ein mit dem gewählten Wörterbuch kodierter Text, um Traffic zu
  sparen (nützlich auf langsamen Funkstrecken).
- 0x0A Session probe — ein kleiner „Ping“ durch den verschlüsselten Kanal, um zu
  bestätigen, dass beide Seiten korrekt entschlüsseln.

Keine ChatWire-Frames, aber ebenfalls verschlüsselt: 0xAC ACK (Nachricht vollständig
empfangen, 17 Bytes) und 0xAD NACK (bitte die gelisteten Block-Nummern erneut senden).
Sehr alte Clients senden rohes UTF-8 ohne die S2P1-Magie; der Empfänger behandelt es
trotzdem als Text.

Eine Seite pro Typ mit Byte-Layouts und unverschlüsselten Beispielen: docs/messages.
Krypto-Pipelines als Diagramme: docs/encryption.

5) MESSENGER SERVER UND BOTS
Wenn Peers sich nicht direkt verbinden können, läuft es über den Messenger Server per
HTTPS/TLS+JWT. Der Chatinhalt ist Ende-zu-Ende-verschlüsselt: Der Server speichert nur
einen opaken Base64-Block.
- Codec v2 (zwischen Menschen bevorzugt): RSA wird einmalig zum Sitzungsaufbau genutzt;
  danach ist jede Nachricht AES-256-GCM (eine authentisierte Verschlüsselung:
  Verschlüsselung und Integritätsprüfung in einem Algorithmus) mit einer frischen
  12-Byte-Zufalls-Nonce pro Nachricht.
- Codec v1 (für Bots und als Fallback): jede Nachricht erhält einen frischen
  AES-256-GCM-Schlüssel und eine Nonce; Schlüssel+Nonce werden mit dem öffentlichen
  RSA-Schlüssel des Empfängers eingepackt (RSA-OAEP-SHA256). Bots nutzen immer v1.
In beiden Fällen kann der Server den Inhalt nicht entschlüsseln.

6) DISCOVERY
Frames 0x40..0x45 auf UDP 17890 (Sonnen/Antworten, Routentabellen, Profile) sind meist
Klartext-Kontrollverkehr: Sie helfen, Peers zu finden, und dürfen nicht mit geheimem
Chatinhalt verwechselt werden. LAN-FIND/FOUND-Frames ermitteln die Adresse eines Peers;
ein RELAY-Frame kann bereits verschlüsselte 0x02-Daten über Zwischenknoten tragen.

WEITERFÜHRENDES
- docs/encryption — Krypto-Pipelines als Vektordiagramme (Sitzungsaufbau,
  Paketformate, Serverpfade).
- docs/messages — eine Seite pro Nachrichtentyp mit unverschlüsselten Byte-Beispielen.
- protocol.md — detaillierte Frame-Referenz (auf Russisch).
""".strip()


def lang_block_zh() -> str:
    return """
ShortP2P 是一个点对点消息协议栈。通俗地说：两台设备（对等端）互相找到对方，协商出秘密密钥，
之后交换任何中间人都无法读取的聊天消息。传输可以直接进行（UDP、蓝牙、mesh/LoRa 无线电），
也可以经由可选的 Messenger Server 中转——它只转发已经加密的数据。

基础概念（无需任何密码学知识）

- 对等端（peer）：与你聊天的另一台设备或人。
- NetworkId：12 个随机字节，用来标识一个用户，类似于网络地址。在界面和二维码中以短
  base64url 文本（约 16 个字符）显示。
- RSA-1024：一种非对称算法——一对密钥。公钥可以公开传递（在邀请和二维码中）；私钥
  永不离开设备。用公钥加密的内容只有对应的私钥才能解密。在 ShortP2P 中，它每个会话
  只用一次，用来把会话密钥发给对方。
- 会话密钥：为一次聊天会话随机生成的两串字节：AES 密钥（16 字节）和 MAC 密钥（32
  字节）。握手之后，双方持有相同的两串密钥。
- AES-128-CBC：一种对称加密——同一个密钥既加密也解密。CBC 模式把块链接起来，使相同
  的明文片段看起来不同；PKCS7 填充把长度补齐到 16 字节的倍数。
- IV（初始向量）：每个包 16 个全新的随机字节，使同一段文本每次加密的结果都不一样。
- HMAC-SHA256 标签：带密钥的校验和（截取 16 字节），对 IV+密文计算。接收方重新计算
  并以恒定时间比较；不一致说明包被篡改或损坏，直接丢弃。
- ChatWire：ShortP2P 自有的聊天内容二进制格式。被加密并发送的就是它；接收方先解密，
  再解析。
- S2P1：每个 ChatWire 帧开头的 4 字节“魔数签名”（ASCII 字符 "S2P1"）。它像文件头一样
  工作——解析器最先检查它来识别格式。
- 类型字节（kind）：紧跟魔数之后的那个字节，它的值说明后面是哪种消息。
- ACK / NACK：内部的投递回执。ACK = “消息已完整收到”；NACK = “请重发这些编号的
  分块”。
- TLS/HTTPS：客户端与 Messenger Server 之间标准的传输加密（浏览器里那把锁）。
- JWT：登录时服务器签发的令牌。客户端在每个请求中附带它；服务器验证签名，而不必再要
  密码。
- 端到端加密（E2E）：只有两台聊天设备能读内容。Messenger Server 只存储并转发不透明的
  加密字节块（Base64 文本），自己也读不了。
- 机器人（bot）：服务器上的程序账号，客户端可以与它对话；它同样只能收到加密内容。
- Gossip / discovery：回答“谁在附近、如何连到它”的辅助流量（UDP 17890）。
- RELAY：一种局域网路由帧，可以携带已加密的数据穿过中间节点。

1) 身份
每个用户有一个 NetworkId（12 个随机字节），以及注册时生成的 RSA-1024 密钥对。公钥以
明文形式随聊天邀请（0x30）或二维码传递——这是安全的，公钥本来就是用来公开的。规则：
NetworkId 较小的一方成为握手“领导者”，另一方等待。

2) 传输
默认 UDP 端口：17500——聊天数据通道（握手与加密包）；17501——在线通告；17502——聊天
邀请；17890——发现/gossip。启用时，蓝牙与 mesh/LoRa 承载相同的逻辑帧。与 Messenger
Server 的通信始终走 HTTPS/TLS 并使用 JWT 认证。

3) 会话（直连 P2P），逐步说明
a. 跟随者可以发送带自己 NetworkId 的 0x04 帧：“我想要一个会话”。
b. 领导者生成两个随机密钥（AES 密钥 16 字节 + MAC 密钥 32 字节），用对方的 RSA 公钥
   以 RSA-OAEP(SHA-1) 加密这 48 字节。结果正好 128 字节，作为 0x01 帧发送。
c. 跟随者用自己的 RSA 私钥解密；此后双方共享同样的两个密钥。
d. 之后每个数据帧都是 0x02 + 包，包 = IV(16) + AES-CBC 密文 + HMAC 标签(16)，最大
   1024 字节。接收方先验证标签，再解密。

4) 应用消息（加密内部）
送进加密的明文通常是一个 ChatWire 帧：魔数 "S2P1"，然后是类型字节，然后是各字段（长度
除特别说明外均为小端序）。

类型字节的取值，逐一说明：
- 0x01 Text——普通的文字消息（UTF-8），即人输入的内容。
- 0x02 Image——聊天中的内嵌图片：MIME 类型（如 "image/png"）加上图片字节。
- 0x03 File——随聊天发送的小文件：文件名、MIME 类型、字节。
- 0x04 Transfer offer——一份 JSON 描述：一个大文件在聊天之外下载（连接哪个主机/
  端口、一次性令牌、大小、有效期）。
- 0x05 Transfer control——对 offer 的 JSON 回复：接受、拒绝或错误。
- 0x06 User info——发送者的资料（“关于我”文字和头像），不显示为聊天气泡。
- 0x07 Vocabulary offer——发送者支持的压缩词典列表。
- 0x08 Vocabulary accept——本次聊天选定的词典（或 "UNCOMPRESSED"）。
- 0x09 Compressed text——用选定词典编码的文字，节省流量（适合慢速无线链路）。
- 0x0A Session probe——穿过加密信道的小“ping”，确认双方都能正确解密。

不是 ChatWire 帧、但同样加密发送的：0xAC ACK（消息已完整收到，17 字节）和 0xAD NACK
（请重发列出的分块编号）。很旧的客户端发送不带 S2P1 魔数的原始 UTF-8；接收方仍按文本
处理。

每种消息一页、含字节布局与未加密示例：docs/messages。加密流程图：docs/encryption。

5) Messenger Server 与机器人
当双方无法直连时，走 Messenger Server（HTTPS/TLS+JWT）。聊天正文是端到端加密的，服务
器只保存不透明的 Base64 字节块。
- 编码 v2（人与人之间首选）：RSA 只在建会话时用一次；之后每条消息用 AES-256-GCM
  （一种认证加密：一个算法同时完成加密和完整性校验），每条消息一个 12 字节随机
  nonce（一次性随机数）。
- 编码 v1（用于机器人及后备）：每条消息都生成新的 AES-256-GCM 密钥和 nonce，并用接
  收者的 RSA 公钥包裹密钥+nonce（RSA-OAEP-SHA256）。机器人始终用 v1。
两种情况下服务器都无法解密内容。

6) 发现
UDP 17890 上的 0x40..0x45 帧（探测/应答、路由表、资料）大多是明文控制流量：用于寻找
对等端，不要与聊天的秘密内容混淆。局域网 FIND/FOUND 帧找到对方地址；RELAY 帧可以携带
已加密的 0x02 负载穿过中间节点。

延伸阅读
- docs/encryption——以矢量图展示的加密流程（会话建立、包格式、服务器路径）。
- docs/messages——每种消息一页，含未加密的字节级示例。
- protocol.md——详细的帧参考（俄文）。
""".strip()


LANGUAGES: list[dict[str, object]] = [
    {
        "code": "en",
        "label": "English",
        "file_stem": "ShortP2P-Protocol-en",
        "odt_font": "Arial",
        "pdf_font": "ProtocolEuro",
        "cjk": False,
        "body": lang_block_en,
    },
    {
        "code": "ru",
        "label": "Russian / Русский",
        "file_stem": "ShortP2P-Protocol-ru",
        "odt_font": "Arial",
        "pdf_font": "ProtocolEuro",
        "cjk": False,
        "body": lang_block_ru,
    },
    {
        "code": "fr",
        "label": "French / Français",
        "file_stem": "ShortP2P-Protocol-fr",
        "odt_font": "Arial",
        "pdf_font": "ProtocolEuro",
        "cjk": False,
        "body": lang_block_fr,
    },
    {
        "code": "es",
        "label": "Spanish / Español",
        "file_stem": "ShortP2P-Protocol-es",
        "odt_font": "Arial",
        "pdf_font": "ProtocolEuro",
        "cjk": False,
        "body": lang_block_es,
    },
    {
        "code": "de",
        "label": "German / Deutsch",
        "file_stem": "ShortP2P-Protocol-de",
        "odt_font": "Arial",
        "pdf_font": "ProtocolEuro",
        "cjk": False,
        "body": lang_block_de,
    },
    {
        "code": "zh",
        "label": "Chinese Simplified / 简体中文",
        "file_stem": "ShortP2P-Protocol-zh-Hans",
        "odt_font": "Microsoft YaHei",
        "pdf_font": "ProtocolCJK",
        "cjk": True,
        "body": lang_block_zh,
    },
]


# ---------------------------------------------------------------------------
# Diagram plumbing
# ---------------------------------------------------------------------------

def ensure_diagrams() -> None:
    build_diagrams.main()
    build_infographics.main()


def svg_path(stem: str, code: str) -> Path:
    return DIAGRAMS / f"{stem}.{code}.svg"


def register_pdf_fonts() -> None:
    euro = FONT_EURO if FONT_EURO.exists() else FONT_FALLBACK
    if not euro.exists():
        raise FileNotFoundError("No European Unicode TTF found (DejaVuSans/Arial).")
    pdfmetrics.registerFont(TTFont("ProtocolEuro", str(euro)))
    bold = FONT_EURO_BOLD if FONT_EURO_BOLD.exists() else euro
    pdfmetrics.registerFont(TTFont("ProtocolEuro-Bold", str(bold)))

    if FONT_CJK.exists():
        pdfmetrics.registerFont(TTFont("ProtocolCJK", str(FONT_CJK), subfontIndex=0))
    else:
        pdfmetrics.registerFont(TTFont("ProtocolCJK", str(euro)))
        print("WARNING: Microsoft YaHei not found; Chinese PDF may be incomplete")

    if FONT_SERIF.exists():
        pdfmetrics.registerFont(TTFont("ProtocolSerif", str(FONT_SERIF)))
    else:
        pdfmetrics.registerFont(TTFont("ProtocolSerif", str(euro)))
        print("WARNING: Times New Roman not found; serif body falls back to DejaVu")
    if FONT_SERIF_ZH.exists():
        pdfmetrics.registerFont(TTFont("ProtocolSerifZh", str(FONT_SERIF_ZH), subfontIndex=0))


def serif_font(cjk: bool) -> str:
    """Serif face for non-heading text: Times New Roman; SimSun for Chinese."""
    if cjk and FONT_SERIF_ZH.exists():
        return "ProtocolSerifZh"
    return "ProtocolSerif"


def _map_font(name: str, cjk: bool) -> str:
    n = (name or "").lower()
    if cjk:
        # Courier and DejaVu lack CJK glyphs; use the CJK font for zh diagrams.
        return "ProtocolCJK"
    if "mono" in n or "courier" in n or "consolas" in n:
        return "Courier"
    if "bold" in n:
        return "ProtocolEuro-Bold"
    return "ProtocolEuro"


def force_drawing_fonts(drawing: Drawing, cjk: bool) -> None:
    """Force reportlab font names svglib could not resolve (Cyrillic/CJK)."""
    stack = [drawing]
    while stack:
        obj = stack.pop()
        contents = getattr(obj, "contents", None)
        if contents is None:
            continue
        for item in contents:
            if isinstance(item, String):
                item.fontName = _map_font(item.fontName, cjk)
            else:
                stack.append(item)


def load_drawing(stem: str, code: str, target_width: float, cjk: bool) -> Drawing:
    drawing = svg2rlg(str(svg_path(stem, code)))
    if drawing is None:
        raise RuntimeError(f"Failed to parse SVG: {stem}.{code}")
    force_drawing_fonts(drawing, cjk)
    scale = target_width / float(drawing.width)
    drawing.width = target_width
    drawing.height = float(drawing.height) * scale
    drawing.scale(scale, scale)
    return drawing


# ---------------------------------------------------------------------------
# ODT
# ---------------------------------------------------------------------------

def _add_para_style(
    doc,
    name: str,
    *,
    size: str,
    bold: bool,
    align: str,
    font: str,
    break_before_page: bool = False,
    keep_with_next: bool = False,
) -> None:
    st = Style(name=name, family="paragraph")
    props = {
        "textalign": {"start": "start", "center": "center", "end": "end"}[align],
        "margintop": "0.1cm",
        "marginbottom": "0.1cm",
    }
    if break_before_page:
        props["breakbefore"] = "page"
    if keep_with_next:
        # Never leave a subtitle stranded at the bottom of a page.
        props["keepwithnext"] = "always"
    st.addElement(ParagraphProperties(**props))
    st.addElement(
        TextProperties(
            fontsize=size,
            fontweight="bold" if bold else "normal",
            fontname=font,
        )
    )
    doc.styles.addElement(st)


def xml_escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def build_odt_for_lang(path: Path, lang: dict[str, object]) -> None:
    code = str(lang["code"])
    font = str(lang["odt_font"])
    body_font = "SimSun" if (bool(lang["cjk"]) and FONT_SERIF_ZH.exists()) else "Times New Roman"
    label = str(lang["label"])
    ui = UI_STRINGS[code]
    body_fn: Callable[[], str] = lang["body"]  # type: ignore[assignment]
    body_text = body_fn()
    figures = build_diagrams.DIAGRAM_TEXTS[code]["figures"]

    doc = OpenDocumentText()
    pl = PageLayout(name="A4")
    pl.addElement(
        PageLayoutProperties(
            pagewidth="21cm",
            pageheight="29.7cm",
            margintop="1.6cm",
            marginbottom="1.6cm",
            marginleft="1.6cm",
            marginright="1.6cm",
        )
    )
    doc.automaticstyles.addElement(pl)
    doc.masterstyles.addElement(MasterPage(name="Standard", pagelayoutname=pl))

    _add_para_style(doc, "Title", size="26pt", bold=True, align="center", font=font)
    _add_para_style(doc, "WarnZh", size="10pt", bold=False, align="center", font=body_font)
    _add_para_style(doc, "WarnEn", size="10pt", bold=False, align="center", font=body_font)
    _add_para_style(doc, "Meta", size="10pt", bold=False, align="end", font=body_font)
    _add_para_style(doc, "H1", size="13pt", bold=True, align="start", font=font, break_before_page=True)
    _add_para_style(doc, "H2", size="11pt", bold=True, align="start", font=font, keep_with_next=True)
    _add_para_style(doc, "Body", size="10pt", bold=False, align="start", font=body_font)
    _add_para_style(doc, "HeadBold", size="12pt", bold=True, align="start", font=font, keep_with_next=True)
    _add_para_style(doc, "TOC", size="10pt", bold=False, align="start", font=body_font)

    for _ in range(6):
        doc.text.addElement(P(stylename="Body", text=""))
    doc.text.addElement(P(stylename="Title", text=TITLE))
    # Translation disclaimer (Chinese + English) only on the Chinese edition.
    if bool(lang["cjk"]):
        doc.text.addElement(P(stylename="WarnZh", text=STATUS_ZH))
        doc.text.addElement(P(stylename="WarnEn", text=STATUS_EN))
    doc.text.addElement(P(stylename="Body", text=""))
    doc.text.addElement(P(stylename="WarnEn", text=f'{ui["language"]}: {label}'))
    for _ in range(5):
        doc.text.addElement(P(stylename="Body", text=""))
    doc.text.addElement(P(stylename="Meta", text=f"Author: {AUTHOR}"))
    doc.text.addElement(P(stylename="Meta", text=f"Repository: {REPO_URL}"))
    doc.text.addElement(P(stylename="Meta", text=f"Created: {CREATED.isoformat()}"))
    doc.text.addElement(P(stylename="Meta", text=f"Modified: {MODIFIED.isoformat()}"))
    doc.text.addElement(P(stylename="Meta", text="Related: docs/encryption, docs/messages, protocol.md"))

    doc.text.addElement(H(outlinelevel=1, stylename="H1", text=ui["contents"]))
    doc.text.addElement(P(stylename="TOC", text=ui["cover_page"]))
    doc.text.addElement(P(stylename="TOC", text=ui["contents"]))
    doc.text.addElement(P(stylename="TOC", text=ui["diagrams"]))
    doc.text.addElement(P(stylename="TOC", text=ui["description"]))

    doc.text.addElement(H(outlinelevel=1, stylename="H1", text=ui["diagrams"]))
    for stem, caption_text in figures:
        doc.text.addElement(P(stylename="H2", text=caption_text))
        href = doc.addPicture(str(svg_path(stem, code)))
        height = "7.2cm" if stem == "01-layers" else "5.2cm"
        # as-char (inline) anchoring: the image is part of the paragraph, so
        # keep-with-next on the caption moves caption + image together.
        frame = Frame(width="17.5cm", height=height, anchortype="as-char")
        frame.addElement(OdfImage(href=href))
        p_img = P(stylename="Body")
        p_img.addElement(frame)
        doc.text.addElement(p_img)

    doc.text.addElement(H(outlinelevel=1, stylename="H1", text=ui["description"]))
    lines = body_text.splitlines()
    for i, line in enumerate(lines):
        section_no = numbered_section(line)
        if section_no is not None or line in EXTRA_HEADINGS[code]:
            # Bold subtitle preceded by a blank line.
            prev_blank = i == 0 or not lines[i - 1].strip()
            if not prev_blank:
                doc.text.addElement(P(stylename="Body", text=""))
            doc.text.addElement(P(stylename="HeadBold", text=line if line else " "))
            if section_no is not None:
                # Section infographic right under the subtitle.
                stem = SECTION_STEMS[section_no]
                href = doc.addPicture(str(svg_path(stem, code)))
                # Inline anchoring keeps the subtitle and the infographic together.
                frame = Frame(width="17.5cm", height="10.9cm", anchortype="as-char")
                frame.addElement(OdfImage(href=href))
                p_img = P(stylename="Body")
                p_img.addElement(frame)
                doc.text.addElement(p_img)
        else:
            doc.text.addElement(P(stylename="Body", text=line if line else " "))

    out_base = str(path)
    if out_base.lower().endswith(".odt"):
        out_base = out_base[:-4]
    doc.save(out_base, True)
    produced = Path(out_base + ".odt")
    if produced.resolve() != path.resolve() and produced.exists():
        produced.replace(path)


# ---------------------------------------------------------------------------
# PDF
# ---------------------------------------------------------------------------

def build_pdf_for_lang(path: Path, lang: dict[str, object]) -> None:
    code = str(lang["code"])
    pdf_font = str(lang["pdf_font"])
    pdf_bold = "ProtocolEuro-Bold" if pdf_font == "ProtocolEuro" else pdf_font
    label = str(lang["label"])
    cjk = bool(lang["cjk"])
    serif = serif_font(cjk)
    ui = UI_STRINGS[code]
    body_fn: Callable[[], str] = lang["body"]  # type: ignore[assignment]
    body_text = body_fn()
    figures = build_diagrams.DIAGRAM_TEXTS[code]["figures"]

    styles = getSampleStyleSheet()
    title_style = ParagraphStyle(
        "Title", parent=styles["Title"], fontName=pdf_bold, fontSize=24, leading=30,
        alignment=TA_CENTER, spaceAfter=12,
    )
    warn_zh = ParagraphStyle(
        "WarnZh", parent=styles["Normal"], fontName=serif, fontSize=9,
        leading=12, alignment=TA_CENTER, spaceAfter=4,
    )
    warn_en = ParagraphStyle(
        "WarnEn", parent=styles["Normal"], fontName=serif, fontSize=9, leading=12,
        alignment=TA_CENTER, spaceAfter=4,
    )
    meta = ParagraphStyle(
        "Meta", parent=styles["Normal"], fontName=serif, fontSize=10, leading=12,
        alignment=TA_RIGHT, spaceBefore=3,
    )
    h1 = ParagraphStyle(
        "H1", parent=styles["Heading1"], fontName=pdf_bold, fontSize=13, leading=16,
        spaceAfter=8, alignment=TA_LEFT, keepWithNext=1,
    )
    h2 = ParagraphStyle(
        "H2", parent=styles["Heading2"], fontName=pdf_bold, fontSize=11, leading=14,
        spaceBefore=6, spaceAfter=4, keepWithNext=1,
    )
    body = ParagraphStyle(
        "Body", parent=styles["Normal"], fontName=serif, fontSize=10, leading=14,
        alignment=TA_LEFT,
    )
    head_style = ParagraphStyle(
        "HeadBold", parent=styles["Normal"], fontName=pdf_bold, fontSize=11.5,
        leading=15, spaceBefore=14, spaceAfter=6, alignment=TA_LEFT, keepWithNext=1,
    )
    toc = ParagraphStyle(
        "Toc", parent=styles["Normal"], fontName=serif, fontSize=10, leading=13,
        leftIndent=8,
    )

    story: list = []
    story.append(Spacer(1, 4.5 * cm))
    story.append(Paragraph(TITLE, title_style))
    # Translation disclaimer (Chinese + English) only on the Chinese edition.
    if cjk:
        story.append(Paragraph(STATUS_ZH, warn_zh))
        story.append(Paragraph(STATUS_EN, warn_en))
    story.append(Spacer(1, 0.4 * cm))
    story.append(Paragraph(xml_escape(f'{ui["language"]}: {label}'), warn_en))
    story.append(Spacer(1, 5.5 * cm))
    story.append(Paragraph(f"Author: {AUTHOR}", meta))
    story.append(Paragraph(f"Repository: {REPO_URL}", meta))
    story.append(Paragraph(f"Created: {CREATED.isoformat()}", meta))
    story.append(Paragraph(f"Modified: {MODIFIED.isoformat()}", meta))
    story.append(Paragraph("Related: docs/encryption, docs/messages, protocol.md", meta))
    story.append(RLPageBreak())

    story.append(Paragraph(ui["contents"], h1))
    for line in (ui["cover_page"], ui["contents"], ui["diagrams"], ui["description"]):
        story.append(Paragraph(xml_escape(line), toc))
    story.append(RLPageBreak())

    page_width = A4[0] - 3.2 * cm
    story.append(Paragraph(ui["diagrams"], h1))
    for stem, caption_text in figures:
        story.append(Paragraph(xml_escape(caption_text), h2))
        story.append(load_drawing(stem, code, page_width, cjk))
        story.append(Spacer(1, 0.25 * cm))
    story.append(RLPageBreak())

    story.append(Paragraph(xml_escape(ui["description"]), h1))
    chunk_lines: list[str] = []

    def flush() -> None:
        if chunk_lines:
            chunk = xml_escape("\n".join(chunk_lines)).replace("\n", "<br/>")
            story.append(Paragraph(chunk, body))
            story.append(Spacer(1, 0.2 * cm))
            chunk_lines.clear()

    for line in body_text.splitlines():
        section_no = numbered_section(line)
        if section_no is not None or line in EXTRA_HEADINGS[code]:
            flush()
            # Bold subtitle preceded by extra spacing (\n\n feel).
            story.append(Paragraph(xml_escape(line), head_style))
            if section_no is not None:
                # Section infographic right under the subtitle.
                story.append(load_drawing(SECTION_STEMS[section_no], code, page_width, cjk))
                story.append(Spacer(1, 0.25 * cm))
        elif line.strip():
            chunk_lines.append(line)
        else:
            flush()
    flush()

    doc = SimpleDocTemplate(
        str(path),
        pagesize=A4,
        leftMargin=1.6 * cm,
        rightMargin=1.6 * cm,
        topMargin=1.6 * cm,
        bottomMargin=1.6 * cm,
        title=f"{TITLE} ({label})",
        author=AUTHOR,
    )
    doc.build(story)


def main() -> None:
    ensure_diagrams()
    for lang in LANGUAGES:
        code = str(lang["code"])
        for stem, _caption in build_diagrams.DIAGRAM_TEXTS[code]["figures"]:
            if not svg_path(stem, code).exists():
                raise FileNotFoundError(svg_path(stem, code))
        for stem in SECTION_STEMS.values():
            if not svg_path(stem, code).exists():
                raise FileNotFoundError(svg_path(stem, code))

    register_pdf_fonts()

    for lang in LANGUAGES:
        stem = str(lang["file_stem"])
        odt_path = ROOT / f"{stem}.odt"
        pdf_path = ROOT / f"{stem}.pdf"
        build_odt_for_lang(odt_path, lang)
        build_pdf_for_lang(pdf_path, lang)
        print(f"Wrote {odt_path.name} and {pdf_path.name}")


if __name__ == "__main__":
    main()
