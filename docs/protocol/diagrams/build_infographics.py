#!/usr/bin/env python3
"""Per-section educational infographics for the ShortP2P protocol document.

Style: definition box, colored flow stages with arrows, code example box,
and an "Important" warning box with check/cross items. One SVG per numbered
section (1..6) per language: secN-<name>.<code>.svg
"""

from __future__ import annotations

import textwrap
from html import escape
from pathlib import Path

import _svg_lib
from _svg_lib import write_svg
from build_diagrams import LANG_FONTS

ROOT = Path(__file__).resolve().parent

BLUE = "#3E6DB5"
PURPLE = "#7A5FB5"
GREEN = "#3F9A6E"
STAGE_BODY = {"blue": "#EBF1F9", "purple": "#F1EDF9", "green": "#EAF5F0"}
STAGE_HEAD = {"blue": BLUE, "purple": PURPLE, "green": GREEN}
STROKE = "#9AA7B8"

# msyh has no U+2713/U+2717; use U+221A / U+00D7 for Chinese instead.
MARKS = {
    "en": ("✓", "✗"),
    "ru": ("✓", "✗"),
    "fr": ("✓", "✗"),
    "es": ("✓", "✗"),
    "de": ("✓", "✗"),
    "zh": ("√", "×"),
}

LABELS = {
    "en": {"def": "DEFINITION", "example": "EXAMPLE", "important": "IMPORTANT"},
    "ru": {"def": "ОПРЕДЕЛЕНИЕ", "example": "ПРИМЕР", "important": "ВАЖНО"},
    "fr": {"def": "DÉFINITION", "example": "EXEMPLE", "important": "IMPORTANT"},
    "es": {"def": "DEFINICIÓN", "example": "EJEMPLO", "important": "IMPORTANTE"},
    "de": {"def": "DEFINITION", "example": "BEISPIEL", "important": "WICHTIG"},
    "zh": {"def": "定义", "example": "示例", "important": "注意"},
}

# Code examples are technical and shared across languages.
CODE = {
    "sec1-identity": (
        "NetworkId : 12 B (base64url in UI/QR)",
        "RSA pub   : { \"M\": ..., \"E\": ... }",
        "invite    : 0x30 SP2I v1 | NetworkId",
        "            | nick | RSA pub JSON",
    ),
    "sec2-transports": (
        "0x30 invite    | 0x31 presence",
        "0x01 handshake | 0x04 setup req",
        "0x02 cipher    | discovery: 17890",
        "BLE / mesh: same logical frames",
        "server: HTTPS/TLS + JWT always",
    ),
    "sec3-session": (
        "0x04 || NetworkId(12)",
        "0x01 || RSA-OAEP-SHA1(128) // keys 48 B",
        "0x02 || IV(16) || AES-CBC || tag(16)",
        "packet <= 1024 B | plain <= 991 B",
    ),
    "sec4-messages": (
        "53 32 50 31 01 05 00 00 00 48 65 6C 6C 6F",
        " S2P1      kind len LE      H  e  l  l  o",
        "0x04/0x05: JSON | 0x06: about+avatar",
        "0x09: dict payload | 0x0A: ver|kind|chal(8)",
    ),
    "sec5-server": (
        "v2 kind 1: establish RSA + AES-256-GCM",
        "v2 kind 2: SessionId | nonce(12) | CT",
        "v2 kind 4/5: probe ack / rekey",
        "v1: RSA-OAEP-SHA256(key||nonce) | CT",
    ),
    "sec6-discovery": (
        "0x40 probe   | 0x41 ack",
        "0x42 routes  | 0x43 reply",
        "0x44 profile | 0x45 reply (about+avatar)",
        "RELAY 0x22: outer clear, inner opaque",
    ),
}

INFOGRAPHICS: dict[str, list[dict]] = {
    "en": [
        {
            "stem": "sec1-identity",
            "def": ("NetworkId identifies the user on the network;", "the RSA-1024 key pair identifies him cryptographically."),
            "stages": [
                ("blue", "NETWORK ID", ("12 random bytes", "short text in UI / QR")),
                ("purple", "RSA-1024 KEY PAIR", ("public — free to share", "private — stays on device")),
                ("green", "CONTACT EXCHANGE", ("invite 0x30 or QR", "carries only the public key")),
            ],
            "warn": [
                ("ok", "sharing the public key is safe"),
                ("no", "never send the private key anywhere"),
                ("no", "smaller NetworkId = handshake leader"),
            ],
        },
        {
            "stem": "sec2-transports",
            "def": ("The same logical frames travel over several transports;", "only the Messenger Server link is always HTTPS."),
            "stages": [
                ("blue", "UDP 17500", ("chat data channel", "handshake + cipher 0x02")),
                ("purple", "UDP 17501/17502", ("presence / invites", "plaintext control")),
                ("green", "UDP 17890", ("discovery / gossip", "plaintext control")),
                ("blue", "BLE / MESH", ("same logical frames", "when enabled")),
            ],
            "warn": [
                ("ok", "chat content only inside the 0x02 cipher"),
                ("no", "invite / presence / gossip are not encrypted"),
                ("no", "server link is always HTTPS + JWT"),
            ],
        },
        {
            "stem": "sec3-session",
            "def": ("The leader sends two random session secrets wrapped in the peer's public key;", "afterwards both sides encrypt with the same AES key."),
            "stages": [
                ("blue", "REQUEST 0x04", ("follower: 'I want a session'",)),
                ("purple", "RNG 48 BYTES", ("AES key 16 B + MAC key 32 B",)),
                ("purple", "RSA-OAEP 0x01", ("wrapped with peer public key", "exactly 128 B")),
                ("green", "SHARED SECRETS", ("both sides hold the same keys",)),
            ],
            "warn": [
                ("ok", "verify the HMAC tag before decrypting"),
                ("no", "leader ignores a foreign incoming 0x01"),
                ("no", "IV must be fresh random for every packet"),
            ],
        },
        {
            "stem": "sec4-messages",
            "def": ("Inside the encryption travels a ChatWire frame: 4-byte magic S2P1,", "then the kind byte, then the fields."),
            "stages": [
                ("blue", "MAGIC S2P1", ("ASCII header of the frame", "lets the parser recognize it")),
                ("purple", "KIND BYTE 0x01..0x0A", ("text / image / file / transfer", "vocab / probe / user info")),
                ("green", "FIELDS", ("lengths little-endian", "payload bytes last")),
            ],
            "warn": [
                ("ok", "raw UTF-8 without magic is still accepted as text"),
                ("no", "ACK 0xAC / NACK 0xAD are not ChatWire kinds"),
                ("ok", "one page per kind in docs/messages"),
            ],
        },
        {
            "stem": "sec5-server",
            "def": ("The server relays opaque encrypted blobs over HTTPS; human peers prefer codec v2,", "bots always use v1."),
            "stages": [
                ("blue", "TLS + JWT", ("secure transport to server", "server sees no chat content")),
                ("purple", "CODEC V2", ("RSA once to start session", "then AES-256-GCM + nonce 12 B")),
                ("green", "CODEC V1", ("fresh AES key per message", "key wrapped in RSA (bots)")),
            ],
            "warn": [
                ("ok", "the server cannot decrypt chat bodies"),
                ("no", "bots never switch to v2"),
                ("ok", "client pins the server certificate fingerprint"),
            ],
        },
        {
            "stem": "sec6-discovery",
            "def": ("Discovery frames are plaintext control traffic: they find peers and routes", "and never carry chat secrets."),
            "stages": [
                ("blue", "PROBE / ACK 0x40/0x41", ("who is reachable on the LAN",)),
                ("purple", "ROUTES 0x42/0x43", ("path table towards the peer",)),
                ("green", "PROFILE 0x44/0x45", ("about + avatar", "stored locally only")),
            ],
            "warn": [
                ("ok", "profiles are cached locally only"),
                ("no", "do not confuse control traffic with chat content"),
                ("ok", "RELAY may carry opaque 0x02 inside"),
            ],
        },
    ],
    "ru": [
        {
            "stem": "sec1-identity",
            "def": ("NetworkId определяет пользователя в сети;", "пара ключей RSA-1024 определяет его криптографически."),
            "stages": [
                ("blue", "ИДЕНТИФИКАТОР", ("12 случайных байт", "короткая строка в UI / QR")),
                ("purple", "ПАРА КЛЮЧЕЙ RSA-1024", ("публичный — можно передавать", "приватный — на устройстве")),
                ("green", "ОБМЕН КОНТАКТАМИ", ("приглашение 0x30 или QR", "передаётся только публичный")),
            ],
            "warn": [
                ("ok", "передавать публичный ключ безопасно"),
                ("no", "приватный ключ нельзя никуда отправлять"),
                ("no", "меньший NetworkId — лидер рукопожатия"),
            ],
        },
        {
            "stem": "sec2-transports",
            "def": ("Одни и те же логические кадры ходят по нескольким транспортам;", "только связь с Messenger Server всегда идёт по HTTPS."),
            "stages": [
                ("blue", "UDP 17500", ("канал данных чата", "рукопожатие + шифр 0x02")),
                ("purple", "UDP 17501/17502", ("присутствие / приглашения", "открытые служебные")),
                ("green", "UDP 17890", ("обнаружение / gossip", "открытые служебные")),
                ("blue", "BLE / MESH", ("те же логические кадры", "при включении")),
            ],
            "warn": [
                ("ok", "содержимое чата — только внутри шифра 0x02"),
                ("no", "приглашения / присутствие / gossip не шифруются"),
                ("no", "связь с сервером — всегда HTTPS + JWT"),
            ],
        },
        {
            "stem": "sec3-session",
            "def": ("Лидер отправляет два случайных секрета, запертые публичным ключом пира;", "дальше обе стороны шифруют одним и тем же AES-ключом."),
            "stages": [
                ("blue", "ЗАПРОС 0x04", ("подписчик: «хочу сессию»",)),
                ("purple", "СЛУЧАЙНЫЕ 48 БАЙТ", ("AES-ключ 16 Б + MAC-ключ 32 Б",)),
                ("purple", "RSA-OAEP 0x01", ("заперто публичным ключом", "ровно 128 Б")),
                ("green", "ОБЩИЕ СЕКРЕТЫ", ("у обеих сторон одни ключи",)),
            ],
            "warn": [
                ("ok", "сначала проверить метку HMAC, потом расшифровать"),
                ("no", "лидер игнорирует чужой входящий 0x01"),
                ("no", "IV — свежие случайные байты на каждый пакет"),
            ],
        },
        {
            "stem": "sec4-messages",
            "def": ("Внутри шифрования передаётся кадр ChatWire: магия S2P1 (4 байта),", "затем байт типа, затем поля."),
            "stages": [
                ("blue", "МАГИЯ S2P1", ("ASCII-заголовок кадра", "разборщик узнаёт формат")),
                ("purple", "БАЙТ ТИПА 0x01..0x0A", ("текст / картинка / файл", "словари / проба / профиль")),
                ("green", "ПОЛЯ", ("длины little-endian", "байты данных в конце")),
            ],
            "warn": [
                ("ok", "сырой UTF-8 без магии принимается как текст"),
                ("no", "ACK 0xAC / NACK 0xAD — не кадры ChatWire"),
                ("ok", "по странице на тип в docs/messages"),
            ],
        },
        {
            "stem": "sec5-server",
            "def": ("Сервер пересылает непрозрачные зашифрованные блоки по HTTPS; между людьми", "предпочтителен кодек v2, боты всегда используют v1."),
            "stages": [
                ("blue", "TLS + JWT", ("защищённый канал до сервера", "сервер не видит содержимое")),
                ("purple", "КОДЕК V2", ("RSA один раз для сессии", "затем AES-256-GCM + nonce 12 Б")),
                ("green", "КОДЕК V1", ("свежий AES-ключ на сообщение", "ключ заперт в RSA (боты)")),
            ],
            "warn": [
                ("ok", "сервер не может расшифровать тела чата"),
                ("no", "боты никогда не переходят на v2"),
                ("ok", "клиент закрепляет отпечаток сертификата"),
            ],
        },
        {
            "stem": "sec6-discovery",
            "def": ("Кадры обнаружения — открытый служебный трафик: они ищут пиров и маршруты", "и никогда не несут секретов чата."),
            "stages": [
                ("blue", "ПРОБА / ОТВЕТ 0x40/0x41", ("кто достижим в локальной сети",)),
                ("purple", "МАРШРУТЫ 0x42/0x43", ("таблица пути до пира",)),
                ("green", "ПРОФИЛЬ 0x44/0x45", ("«о себе» + аватар", "хранится только локально")),
            ],
            "warn": [
                ("ok", "профили кэшируются только локально"),
                ("no", "не путать служебный трафик с содержимым чата"),
                ("ok", "внутри RELAY может быть непрозрачный 0x02"),
            ],
        },
    ],
    "fr": [
        {
            "stem": "sec1-identity",
            "def": ("Le NetworkId identifie l'utilisateur sur le réseau ;", "la paire de clés RSA-1024 l'identifie cryptographiquement."),
            "stages": [
                ("blue", "IDENTIFIANT", ("12 octets aléatoires", "texte court en UI / QR")),
                ("purple", "PAIRE RSA-1024", ("publique — partageable", "privée — sur l'appareil")),
                ("green", "ÉCHANGE DE CONTACTS", ("invitation 0x30 ou QR", "seule la clé publique circule")),
            ],
            "warn": [
                ("ok", "partager la clé publique est sans danger"),
                ("no", "n'envoyez jamais la clé privée"),
                ("no", "NetworkId le plus petit = leader"),
            ],
        },
        {
            "stem": "sec2-transports",
            "def": ("Les mêmes trames logiques circulent sur plusieurs transports ;", "seul le lien vers Messenger Server est toujours en HTTPS."),
            "stages": [
                ("blue", "UDP 17500", ("canal de données du chat", "poignée de main + chiffre 0x02")),
                ("purple", "UDP 17501/17502", ("présence / invitations", "contrôle en clair")),
                ("green", "UDP 17890", ("discovery / gossip", "contrôle en clair")),
                ("blue", "BLE / MESH", ("mêmes trames logiques", "si activés")),
            ],
            "warn": [
                ("ok", "le contenu du chat uniquement dans le chiffre 0x02"),
                ("no", "invitations / présence / gossip non chiffrés"),
                ("no", "lien serveur toujours HTTPS + JWT"),
            ],
        },
        {
            "stem": "sec3-session",
            "def": ("Le leader envoie deux secrets aléatoires enveloppés dans la clé publique du pair ;", "ensuite les deux côtés chiffrent avec la même clé AES."),
            "stages": [
                ("blue", "REQUÊTE 0x04", ("suiveur : « je veux une session »",)),
                ("purple", "48 OCTETS ALÉATOIRES", ("clé AES 16 o + clé MAC 32 o",)),
                ("purple", "RSA-OAEP 0x01", ("enveloppé avec la clé publique", "exactement 128 o")),
                ("green", "SECRETS PARTAGÉS", ("les deux côtés, mêmes clés",)),
            ],
            "warn": [
                ("ok", "vérifier l'étiquette HMAC avant de déchiffrer"),
                ("no", "le leader ignore un 0x01 étranger"),
                ("no", "IV aléatoire neuf pour chaque paquet"),
            ],
        },
        {
            "stem": "sec4-messages",
            "def": ("Dans le chiffrement voyage une trame ChatWire : magie S2P1 (4 octets),", "puis l'octet de type, puis les champs."),
            "stages": [
                ("blue", "MAGIE S2P1", ("en-tête ASCII de la trame", "reconnaître le format")),
                ("purple", "TYPE 0x01..0x0A", ("texte / image / fichier", "vocab / sonde / profil")),
                ("green", "CHAMPS", ("longueurs little-endian", "octets de charge à la fin")),
            ],
            "warn": [
                ("ok", "l'UTF-8 brut sans magie reste accepté"),
                ("no", "ACK 0xAC / NACK 0xAD hors ChatWire"),
                ("ok", "une page par type dans docs/messages"),
            ],
        },
        {
            "stem": "sec5-server",
            "def": ("Le serveur relaie des blocs chiffrés opaques via HTTPS ; entre humains le codec v2", "est privilégié, les bots utilisent toujours v1."),
            "stages": [
                ("blue", "TLS + JWT", ("transport sécurisé", "le serveur ne voit rien")),
                ("purple", "CODEC V2", ("RSA une fois pour établir", "puis AES-256-GCM + nonce 12 o")),
                ("green", "CODEC V1", ("clé AES neuve par message", "clé enveloppée en RSA (bots)")),
            ],
            "warn": [
                ("ok", "le serveur ne peut pas déchiffrer les corps"),
                ("no", "les bots ne passent jamais en v2"),
                ("ok", "le client épingle l'empreinte du certificat"),
            ],
        },
        {
            "stem": "sec6-discovery",
            "def": ("Les trames de discovery sont du trafic de contrôle en clair : elles trouvent", "les pairs et les routes, jamais les secrets du chat."),
            "stages": [
                ("blue", "SONDE / ACCUSÉ 0x40/0x41", ("qui est joignable sur le LAN",)),
                ("purple", "ROUTES 0x42/0x43", ("table de chemin vers le pair",)),
                ("green", "PROFIL 0x44/0x45", ("à propos + avatar", "stocké localement seulement")),
            ],
            "warn": [
                ("ok", "les profils ne sont mis en cache que localement"),
                ("no", "ne pas confondre contrôle et contenu du chat"),
                ("ok", "RELAY peut contenir un 0x02 opaque"),
            ],
        },
    ],
    "es": [
        {
            "stem": "sec1-identity",
            "def": ("El NetworkId identifica al usuario en la red;", "el par de claves RSA-1024 lo identifica criptográficamente."),
            "stages": [
                ("blue", "IDENTIFICADOR", ("12 bytes aleatorios", "texto corto en UI / QR")),
                ("purple", "PAR DE CLAVES RSA-1024", ("pública — se puede compartir", "privada — nunca sale")),
                ("green", "INTERCAMBIO DE CONTACTOS", ("invitación 0x30 o QR", "solo viaja la clave pública")),
            ],
            "warn": [
                ("ok", "compartir la clave pública es seguro"),
                ("no", "nunca envíes la clave privada"),
                ("no", "menor NetworkId = líder del handshake"),
            ],
        },
        {
            "stem": "sec2-transports",
            "def": ("Las mismas tramas lógicas viajan por varios transportes;", "solo el enlace con Messenger Server es siempre HTTPS."),
            "stages": [
                ("blue", "UDP 17500", ("canal de datos del chat", "handshake + cifrado 0x02")),
                ("purple", "UDP 17501/17502", ("presencia / invitaciones", "control en claro")),
                ("green", "UDP 17890", ("descubrimiento / gossip", "control en claro")),
                ("blue", "BLE / MESH", ("mismas tramas lógicas", "si están activos")),
            ],
            "warn": [
                ("ok", "el contenido del chat solo dentro del cifrado 0x02"),
                ("no", "invitación / presencia / gossip sin cifrar"),
                ("no", "enlace con el servidor siempre HTTPS + JWT"),
            ],
        },
        {
            "stem": "sec3-session",
            "def": ("El líder envía dos secretos aleatorios envueltos en la clave pública del par;", "después ambos lados cifran con la misma clave AES."),
            "stages": [
                ("blue", "PETICIÓN 0x04", ("seguidor: «quiero una sesión»",)),
                ("purple", "48 BYTES ALEATORIOS", ("clave AES 16 B + clave MAC 32 B",)),
                ("purple", "RSA-OAEP 0x01", ("envuelto con la clave pública", "exactamente 128 B")),
                ("green", "SECRETOS COMPARTIDOS", ("ambos lados, mismas claves",)),
            ],
            "warn": [
                ("ok", "verificar la etiqueta HMAC antes de descifrar"),
                ("no", "el líder ignora un 0x01 ajeno"),
                ("no", "IV aleatorio nuevo en cada paquete"),
            ],
        },
        {
            "stem": "sec4-messages",
            "def": ("Dentro del cifrado viaja una trama ChatWire: magia S2P1 (4 bytes),", "luego el byte de tipo, luego los campos."),
            "stages": [
                ("blue", "MAGIA S2P1", ("cabecera ASCII de la trama", "permite reconocer el formato")),
                ("purple", "TIPO 0x01..0x0A", ("texto / imagen / archivo", "vocabulario / sonda / perfil")),
                ("green", "CAMPOS", ("longitudes little-endian", "bytes de carga al final")),
            ],
            "warn": [
                ("ok", "el UTF-8 crudo sin magia se acepta como texto"),
                ("no", "ACK 0xAC / NACK 0xAD no son tramas ChatWire"),
                ("ok", "una página por tipo en docs/messages"),
            ],
        },
        {
            "stem": "sec5-server",
            "def": ("El servidor reenvía bloques cifrados opacos por HTTPS; entre humanos se prefiere", "el códec v2, los bots usan siempre v1."),
            "stages": [
                ("blue", "TLS + JWT", ("transporte seguro al servidor", "el servidor no ve el contenido")),
                ("purple", "CÓDEC V2", ("RSA una vez para establecer", "luego AES-256-GCM + nonce 12 B")),
                ("green", "CÓDEC V1", ("clave AES nueva por mensaje", "clave envuelta en RSA (bots)")),
            ],
            "warn": [
                ("ok", "el servidor no puede descifrar los cuerpos"),
                ("no", "los bots nunca pasan a v2"),
                ("ok", "el cliente fija la huella del certificado"),
            ],
        },
        {
            "stem": "sec6-discovery",
            "def": ("Las tramas de descubrimiento son tráfico de control en claro: buscan pares", "y rutas y nunca llevan secretos del chat."),
            "stages": [
                ("blue", "SONDEO / ACUSE 0x40/0x41", ("quién es alcanzable en la LAN",)),
                ("purple", "RUTAS 0x42/0x43", ("tabla de camino hacia el par",)),
                ("green", "PERFIL 0x44/0x45", ("acerca de + avatar", "solo se guarda localmente")),
            ],
            "warn": [
                ("ok", "los perfiles solo se almacenan localmente"),
                ("no", "no confundir control con contenido del chat"),
                ("ok", "RELAY puede llevar un 0x02 opaco"),
            ],
        },
    ],
    "de": [
        {
            "stem": "sec1-identity",
            "def": ("Die NetworkId identifiziert den Nutzer im Netz;", "das RSA-1024-Schlüsselpaar identifiziert ihn kryptografisch."),
            "stages": [
                ("blue", "KENNUNG", ("12 Zufallsbytes", "kurzer Text in UI / QR")),
                ("purple", "SCHLÜSSELPaar RSA-1024", ("öffentlich — frei teilbar", "privat — bleibt auf dem Gerät")),
                ("green", "KONTAKTAUSTAUSCH", ("Invite 0x30 oder QR", "es reist nur der öffentliche")),
            ],
            "warn": [
                ("ok", "den öffentlichen Schlüssel zu teilen ist sicher"),
                ("no", "den privaten Schlüssel niemals versenden"),
                ("no", "kleinere NetworkId = Handshake-Leader"),
            ],
        },
        {
            "stem": "sec2-transports",
            "def": ("Dieselben logischen Frames laufen über mehrere Transporte;", "nur die Verbindung zum Messenger Server ist immer HTTPS."),
            "stages": [
                ("blue", "UDP 17500", ("Datenkanal des Chats", "Handshake + Chiffre 0x02")),
                ("purple", "UDP 17501/17502", ("Präsenz / Einladungen", "Klartext-Steuerung")),
                ("green", "UDP 17890", ("Discovery / Gossip", "Klartext-Steuerung")),
                ("blue", "BLE / MESH", ("gleiche logische Frames", "falls aktiviert")),
            ],
            "warn": [
                ("ok", "Chatinhalt nur innerhalb der Chiffre 0x02"),
                ("no", "Invite / Präsenz / Gossip sind offen"),
                ("no", "Serververbindung immer HTTPS + JWT"),
            ],
        },
        {
            "stem": "sec3-session",
            "def": ("Der Leader sendet zwei Zufallsgeheimnisse im öffentlichen Schlüssel des Peers;", "danach verschlüsseln beide Seiten mit demselben AES-Schlüssel."),
            "stages": [
                ("blue", "ANFRAGE 0x04", ("Follower: „Ich möchte eine Sitzung“",)),
                ("purple", "48 ZUFALLSBYTES", ("AES-Schlüssel 16 B + MAC 32 B",)),
                ("purple", "RSA-OAEP 0x01", ("im öffentlichen Schlüssel", "genau 128 B")),
                ("green", "GEMEINSAME GEHEIMNISSE", ("beide Seiten, dieselben Schlüssel",)),
            ],
            "warn": [
                ("ok", "erst das HMAC-Tag prüfen, dann entschlüsseln"),
                ("no", "der Leader ignoriert fremde 0x01"),
                ("no", "IV pro Paket frisch zufällig"),
            ],
        },
        {
            "stem": "sec4-messages",
            "def": ("Innerhalb der Verschlüsselung reist ein ChatWire-Frame: Magie S2P1 (4 Bytes),", "dann das Kind-Byte, dann die Felder."),
            "stages": [
                ("blue", "MAGIE S2P1", ("ASCII-Header des Frames", "erkennt das Format")),
                ("purple", "KIND-BYTE 0x01..0x0A", ("Text / Bild / Datei", "Vokabular / Sonde / Profil")),
                ("green", "FELDER", ("Längen Little-Endian", "Nutzbytes am Ende")),
            ],
            "warn": [
                ("ok", "rohes UTF-8 ohne Magie gilt als Text"),
                ("no", "ACK 0xAC / NACK 0xAD sind keine ChatWire-Frames"),
                ("ok", "eine Seite pro Typ in docs/messages"),
            ],
        },
        {
            "stem": "sec5-server",
            "def": ("Der Server leitet opake verschlüsselte Blöcke per HTTPS weiter; zwischen Menschen", "ist Codec v2 bevorzugt, Bots nutzen immer v1."),
            "stages": [
                ("blue", "TLS + JWT", ("sichere Verbindung zum Server", "der Server sieht keinen Inhalt")),
                ("purple", "CODEC V2", ("RSA einmalig zum Aufbau", "dann AES-256-GCM + Nonce 12 B")),
                ("green", "CODEC V1", ("frischer AES-Schlüssel je Nachricht", "Schlüssel in RSA (Bots)")),
            ],
            "warn": [
                ("ok", "der Server kann Inhalte nicht entschlüsseln"),
                ("no", "Bots wechseln nie auf v2"),
                ("ok", "der Client pinnt das Server-Zertifikat"),
            ],
        },
        {
            "stem": "sec6-discovery",
            "def": ("Discovery-Frames sind Klartext-Kontrollverkehr: Sie finden Peers und Routen,", "nie Chat-Geheimnisse."),
            "stages": [
                ("blue", "SONDE / ANTWORT 0x40/0x41", ("wer im LAN erreichbar ist",)),
                ("purple", "ROUTEN 0x42/0x43", ("Pfadtabelle zum Peer",)),
                ("green", "PROFIL 0x44/0x45", ("Über-mich + Avatar", "nur lokal gespeichert")),
            ],
            "warn": [
                ("ok", "Profile werden nur lokal gespeichert"),
                ("no", "Kontrollverkehr nicht mit Chatinhalt verwechseln"),
                ("ok", "RELAY kann ein opakes 0x02 enthalten"),
            ],
        },
    ],
    "zh": [
        {
            "stem": "sec1-identity",
            "def": ("NetworkId 在网络中标识用户；", "RSA-1024 密钥对从密码学上标识他。"),
            "stages": [
                ("blue", "标识符", ("12 个随机字节", "界面/二维码中的短文本")),
                ("purple", "RSA-1024 密钥对", ("公钥——可以公开", "私钥——不离开设备")),
                ("green", "联系人交换", ("邀请 0x30 或二维码", "只传递公钥")),
            ],
            "warn": [
                ("ok", "分享公钥是安全的"),
                ("no", "绝不发送私钥"),
                ("no", "较小的 NetworkId 为握手领导者"),
            ],
        },
        {
            "stem": "sec2-transports",
            "def": ("相同的逻辑帧可通过多种传输方式传递；", "只有到 Messenger Server 的连接始终使用 HTTPS。"),
            "stages": [
                ("blue", "UDP 17500", ("聊天数据通道", "握手 + 加密 0x02")),
                ("purple", "UDP 17501/17502", ("在线通告 / 邀请", "明文控制")),
                ("green", "UDP 17890", ("发现 / gossip", "明文控制")),
                ("blue", "BLE / mesh", ("相同逻辑帧", "启用时")),
            ],
            "warn": [
                ("ok", "聊天内容只在 0x02 加密内"),
                ("no", "邀请 / 在线通告 / gossip 不加密"),
                ("no", "与服务器连接始终 HTTPS + JWT"),
            ],
        },
        {
            "stem": "sec3-session",
            "def": ("领导者把两个随机会话密钥用对方的公钥封装发送；", "此后双方用同一个 AES 密钥加密。"),
            "stages": [
                ("blue", "请求 0x04", ("跟随者：“我想要一个会话”",)),
                ("purple", "48 个随机字节", ("AES 密钥 16 字节 + MAC 32 字节",)),
                ("purple", "RSA-OAEP 0x01", ("用对方公钥封装", "正好 128 字节")),
                ("green", "共享密钥", ("双方持有相同密钥",)),
            ],
            "warn": [
                ("ok", "解密前先验证 HMAC 标签"),
                ("no", "领导者忽略外来的 0x01"),
                ("no", "IV 每个包都要新随机"),
            ],
        },
        {
            "stem": "sec4-messages",
            "def": ("加密内部传递的是 ChatWire 帧：魔数 S2P1（4 字节）、", "类型字节、各字段。"),
            "stages": [
                ("blue", "魔数 S2P1", ("帧的 ASCII 头", "解析器据此识别格式")),
                ("purple", "类型字节 0x01..0x0A", ("文本 / 图片 / 文件", "词典 / 探测 / 资料")),
                ("green", "字段", ("长度为小端序", "数据字节在最后")),
            ],
            "warn": [
                ("ok", "无魔数的原始 UTF-8 仍按文本接受"),
                ("no", "ACK 0xAC / NACK 0xAD 不是 ChatWire 帧"),
                ("ok", "每种类型一页见 docs/messages"),
            ],
        },
        {
            "stem": "sec5-server",
            "def": ("服务器经 HTTPS 转发不透明的加密数据块；", "人与人优先使用编码 v2，机器人始终使用 v1。"),
            "stages": [
                ("blue", "TLS + JWT", ("到服务器的安全通道", "服务器看不到内容")),
                ("purple", "编码 V2", ("RSA 只在建会话时用一次", "之后 AES-256-GCM + nonce 12 字节")),
                ("green", "编码 V1", ("每条消息新的 AES 密钥", "密钥用 RSA 封装（机器人）")),
            ],
            "warn": [
                ("ok", "服务器无法解密聊天正文"),
                ("no", "机器人从不使用 v2"),
                ("ok", "客户端固定服务器证书指纹"),
            ],
        },
        {
            "stem": "sec6-discovery",
            "def": ("发现帧是明文控制流量：寻找对等端和路由，", "从不携带聊天秘密。"),
            "stages": [
                ("blue", "探测 / 应答 0x40/0x41", ("局域网内谁可达",)),
                ("purple", "路由 0x42/0x43", ("到对等端的路径表",)),
                ("green", "资料 0x44/0x45", ("关于我 + 头像", "仅本地保存")),
            ],
            "warn": [
                ("ok", "资料只缓存在本地"),
                ("no", "不要把控制流量当成聊天内容"),
                ("ok", "RELAY 内可携带不透明的 0x02"),
            ],
        },
    ],
}


def _text(x: float, y: float, s: str, *, size: float, fill: str, weight: str = "normal", anchor: str = "start", mono: bool = False) -> str:
    family = _svg_lib.FONT_MONO if mono else _svg_lib.FONT_SANS
    return (
        f'  <text x="{x}" y="{y}" text-anchor="{anchor}" font-family="{family}" '
        f'font-size="{size}" font-weight="{weight}" fill="{fill}">{escape(s)}</text>'
    )


def _rect(x: float, y: float, w: float, h: float, fill: str, stroke: str, rx: float = 8) -> str:
    return (
        f'  <rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" ry="{rx}" '
        f'fill="{fill}" stroke="{stroke}" stroke-width="1"/>'
    )


def _stage_arrows(xs: list[float], widths: list[float], y: float) -> list[str]:
    out: list[str] = []
    for i in range(len(xs) - 1):
        x1 = xs[i] + widths[i] + 2
        x2 = xs[i + 1] - 2
        out.append(
            f'  <line x1="{x1}" y1="{y}" x2="{x2}" y2="{y}" '
            f'stroke="#1F3A5F" stroke-width="1.5" marker-end="url(#arrow)"/>'
        )
    return out


def build_one(code: str, data: dict) -> None:
    labels = LABELS[code]
    mark_ok, mark_no = MARKS[code]
    sans, mono = LANG_FONTS[code]
    _svg_lib.FONT_SANS = sans
    _svg_lib.FONT_MONO = mono

    W, H = 760, 468
    body: list[str] = []

    # Definition box
    body.append(_rect(20, 12, 720, 70, "#E7EEF7", "#B9CCE1"))
    body.append(_text(36, 34, labels["def"], size=11, fill="#2B4C7E", weight="700"))
    body.append(_text(36, 54, data["def"][0], size=10.5, fill="#243B5A"))
    if len(data["def"]) > 1:
        body.append(_text(36, 70, data["def"][1], size=10.5, fill="#243B5A"))

    # Stage boxes
    stages = data["stages"]
    n = len(stages)
    if n == 4:
        xs = [20, 204, 388, 572]
        widths = [167, 167, 167, 167]
    else:
        xs = [20, 270, 520]
        widths = [220, 220, 220]
    y0, h0 = 96, 104
    for (color, header, lines), x, w in zip(stages, xs, widths, strict=True):
        body.append(_rect(x, y0, w, h0, STAGE_BODY[color], STROKE))
        body.append(_rect(x, y0, w, 24, STAGE_HEAD[color], STAGE_HEAD[color]))
        body.append(_text(x + w / 2, y0 + 16, header, size=10.5, fill="#FFFFFF", weight="700", anchor="middle"))
        for j, line in enumerate(lines):
            body.append(_text(x + w / 2, y0 + 42 + j * 14, line, size=9.5, fill="#2A3546", anchor="middle"))
    body.extend(_stage_arrows(xs, widths, y0 + 64))

    # Code example box
    body.append(_rect(20, 218, 430, 230, "#F2F5F9", "#C4CFDD"))
    body.append(_text(36, 242, labels["example"], size=10.5, fill="#2B4C7E", weight="700"))
    for j, line in enumerate(CODE[data["stem"]]):
        body.append(_text(36, 262 + j * 15, line, size=9.5, fill="#2A3546", mono=True))

    # Important / warning box
    body.append(_rect(470, 218, 270, 230, "#FBECEA", "#E7BCB6"))
    body.append(_text(486, 242, labels["important"], size=11, fill="#B3261E", weight="700"))
    wrap_w = 24 if code == "zh" else 40
    yy = 262
    for kind, text in data["warn"]:
        mark = mark_ok if kind == "ok" else mark_no
        color = "#2E7D32" if kind == "ok" else "#C62828"
        wrapped = textwrap.wrap(text, wrap_w) or [text]
        body.append(_text(486, yy, mark, size=10, fill=color, weight="700"))
        body.append(_text(500, yy, wrapped[0], size=9.5, fill="#3A2A28"))
        yy += 14
        for cont in wrapped[1:]:
            body.append(_text(500, yy, cont, size=9.5, fill="#3A2A28"))
            yy += 14
        yy += 5

    write_svg(ROOT / f'{data["stem"]}.{code}.svg', W, H, data["stem"], body)


def main() -> None:
    for code, sections in INFOGRAPHICS.items():
        for data in sections:
            build_one(code, data)
        print(f"wrote {code} section infographics")


if __name__ == "__main__":
    main()
