#!/usr/bin/env python3
"""Per-language overview SVG diagrams for the ShortP2P protocol document.

Each language gets its own set of vector sources:
    01-layers.<code>.svg, 02-typical-flow.<code>.svg, 03-ports.<code>.svg
Technical tokens (UDP, RSA, AES, ChatWire, port numbers, 0x..) stay unchanged.
"""

from __future__ import annotations

from pathlib import Path

import _svg_lib
from _svg_lib import Box, arrow, caption, draw_box, write_svg

ROOT = Path(__file__).resolve().parent

# SVG font families per language (must exist as system fonts for ODT rendering).
LANG_FONTS: dict[str, tuple[str, str]] = {
    "en": ("Segoe UI, Arial, sans-serif", "Consolas, Courier New, monospace"),
    "ru": ("DejaVu Sans", "DejaVu Sans Mono"),
    "fr": ("DejaVu Sans", "DejaVu Sans Mono"),
    "es": ("DejaVu Sans", "DejaVu Sans Mono"),
    "de": ("DejaVu Sans", "DejaVu Sans Mono"),
    "zh": ("Microsoft YaHei", "Microsoft YaHei"),
}

DIAGRAM_TEXTS: dict[str, dict] = {
    "en": {
        "figures": [
            ("01-layers", "Protocol layers"),
            ("02-typical-flow", "Typical direct chat flow"),
            ("03-ports", "Default UDP ports"),
        ],
        "layers": {
            "title": "ShortP2P protocol layers",
            "boxes": [
                ("Application", ("ChatWire message types / messenger ACK-NACK",)),
                ("Session crypto", ("RSA-OAEP handshake then AES-CBC + HMAC (P2P)",)),
                ("Transport", ("UDP / BLE / mesh  |  HTTPS TLS to Messenger Server",)),
                ("Identity", ("NetworkId 12 B  |  RSA-1024 long-term keys",)),
            ],
            "caption": "Logical stack (not OS layers). Details: docs/encryption, docs/messages.",
        },
        "flow": {
            "title": "Typical direct chat flow",
            "boxes": [
                ("1. Contact", ("invite 0x30", "or QR")),
                ("2. Session", ("0x04 / 0x01", "RSA keys")),
                ("3. Cipher", ("0x02 + AES", "ChatWire")),
                ("4. Optional", ("server / bot", "mesh relay")),
            ],
            "note": ("Same app crypto on UDP, BLE, mesh",),
        },
        "ports": {
            "title": "Default UDP ports",
            "boxes": [
                ("17500", ("data chat", "handshake/cipher")),
                ("17501", ("presence", "LAN ping")),
                ("17502", ("chat invite",)),
                ("17890", ("discovery", "gossip")),
            ],
            "caption": "Bluetooth and mesh reuse logical frames; HTTPS used for Messenger Server.",
        },
    },
    "ru": {
        "figures": [
            ("01-layers", "Уровни протокола"),
            ("02-typical-flow", "Типичный обмен в прямом чате"),
            ("03-ports", "UDP-порты по умолчанию"),
        ],
        "layers": {
            "title": "Уровни протокола ShortP2P",
            "boxes": [
                ("Приложение", ("Типы сообщений ChatWire / ACK-NACK мессенджера",)),
                ("Шифрование сессии", ("Рукопожатие RSA-OAEP, затем AES-CBC + HMAC (P2P)",)),
                ("Транспорт", ("UDP / BLE / mesh  |  HTTPS TLS до Messenger Server",)),
                ("Идентичность", ("NetworkId 12 Б  |  RSA-1024 долгосрочные ключи",)),
            ],
            "caption": "Логический стек (не уровни ОС). Подробности: docs/encryption, docs/messages.",
        },
        "flow": {
            "title": "Типичный обмен в прямом чате",
            "boxes": [
                ("1. Контакт", ("invite 0x30", "или QR")),
                ("2. Сессия", ("0x04 / 0x01", "ключи RSA")),
                ("3. Шифр", ("0x02 + AES", "ChatWire")),
                ("4. Опционально", ("сервер / бот", "mesh-ретрансляция")),
            ],
            "note": ("Одни и те же шифры приложения в UDP, BLE, mesh",),
        },
        "ports": {
            "title": "UDP-порты по умолчанию",
            "boxes": [
                ("17500", ("данные чата", "рукопожатие/шифр")),
                ("17501", ("присутствие", "проверка в LAN")),
                ("17502", ("приглашение в чат",)),
                ("17890", ("обнаружение", "gossip")),
            ],
            "caption": "Bluetooth и mesh используют те же кадры; для Messenger Server — HTTPS.",
        },
    },
    "fr": {
        "figures": [
            ("01-layers", "Couches du protocole"),
            ("02-typical-flow", "Flux type d'un chat direct"),
            ("03-ports", "Ports UDP par défaut"),
        ],
        "layers": {
            "title": "Couches du protocole ShortP2P",
            "boxes": [
                ("Application", ("Types de messages ChatWire / ACK-NACK du messager",)),
                ("Cryptographie de session", ("Poignée de main RSA-OAEP puis AES-CBC + HMAC (P2P)",)),
                ("Transport", ("UDP / BLE / mesh  |  HTTPS TLS vers Messenger Server",)),
                ("Identité", ("NetworkId 12 o  |  Clés RSA-1024 long terme",)),
            ],
            "caption": "Pile logique (pas des couches OS). Détails : docs/encryption, docs/messages.",
        },
        "flow": {
            "title": "Flux type d'un chat direct",
            "boxes": [
                ("1. Contact", ("invite 0x30", "ou QR")),
                ("2. Session", ("0x04 / 0x01", "clés RSA")),
                ("3. Chiffre", ("0x02 + AES", "ChatWire")),
                ("4. Optionnel", ("serveur / bot", "relais mesh")),
            ],
            "note": ("Même crypto applicative sur UDP, BLE, mesh",),
        },
        "ports": {
            "title": "Ports UDP par défaut",
            "boxes": [
                ("17500", ("données du chat", "poignée de main/chiffre")),
                ("17501", ("présence", "ping LAN")),
                ("17502", ("invitation au chat",)),
                ("17890", ("découverte", "gossip")),
            ],
            "caption": "Bluetooth et mesh réutilisent les mêmes trames ; HTTPS pour Messenger Server.",
        },
    },
    "es": {
        "figures": [
            ("01-layers", "Capas del protocolo"),
            ("02-typical-flow", "Flujo típico de chat directo"),
            ("03-ports", "Puertos UDP por defecto"),
        ],
        "layers": {
            "title": "Capas del protocolo ShortP2P",
            "boxes": [
                ("Aplicación", ("Tipos de mensajes ChatWire / ACK-NACK del mensajero",)),
                ("Cifrado de sesión", ("Handshake RSA-OAEP, luego AES-CBC + HMAC (P2P)",)),
                ("Transporte", ("UDP / BLE / mesh  |  HTTPS TLS al Messenger Server",)),
                ("Identidad", ("NetworkId 12 B  |  Claves RSA-1024 a largo plazo",)),
            ],
            "caption": "Pila lógica (no capas del SO). Detalles: docs/encryption, docs/messages.",
        },
        "flow": {
            "title": "Flujo típico de chat directo",
            "boxes": [
                ("1. Contacto", ("invite 0x30", "o QR")),
                ("2. Sesión", ("0x04 / 0x01", "claves RSA")),
                ("3. Cifrado", ("0x02 + AES", "ChatWire")),
                ("4. Opcional", ("servidor / bot", "relé mesh")),
            ],
            "note": ("El mismo cifrado de aplicación en UDP, BLE, mesh",),
        },
        "ports": {
            "title": "Puertos UDP por defecto",
            "boxes": [
                ("17500", ("datos del chat", "handshake/cifrado")),
                ("17501", ("presencia", "ping LAN")),
                ("17502", ("invitación al chat",)),
                ("17890", ("descubrimiento", "gossip")),
            ],
            "caption": "Bluetooth y mesh reutilizan las mismas tramas; HTTPS para Messenger Server.",
        },
    },
    "de": {
        "figures": [
            ("01-layers", "Protokoll-Schichten"),
            ("02-typical-flow", "Typischer direkter Chat-Ablauf"),
            ("03-ports", "Standard-UDP-Ports"),
        ],
        "layers": {
            "title": "ShortP2P-Protokoll-Schichten",
            "boxes": [
                ("Anwendung", ("ChatWire-Nachrichtentypen / Messenger-ACK-NACK",)),
                ("Sitzungskrypto", ("RSA-OAEP-Handshake, danach AES-CBC + HMAC (P2P)",)),
                ("Transport", ("UDP / BLE / Mesh  |  HTTPS-TLS zum Messenger Server",)),
                ("Identität", ("NetworkId 12 B  |  RSA-1024 Langzeit-Schlüssel",)),
            ],
            "caption": "Logischer Stack (keine OS-Schichten). Details: docs/encryption, docs/messages.",
        },
        "flow": {
            "title": "Typischer direkter Chat-Ablauf",
            "boxes": [
                ("1. Kontakt", ("Invite 0x30", "oder QR")),
                ("2. Sitzung", ("0x04 / 0x01", "RSA-Schlüssel")),
                ("3. Chiffre", ("0x02 + AES", "ChatWire")),
                ("4. Optional", ("Server / Bot", "Mesh-Relais")),
            ],
            "note": ("Gleiche App-Krypto über UDP, BLE, Mesh",),
        },
        "ports": {
            "title": "Standard-UDP-Ports",
            "boxes": [
                ("17500", ("Chat-Daten", "Handshake/Chiffre")),
                ("17501", ("Präsenz", "LAN-Ping")),
                ("17502", ("Chat-Einladung",)),
                ("17890", ("Discovery", "Gossip")),
            ],
            "caption": "Bluetooth und Mesh nutzen dieselben Frames; HTTPS für Messenger Server.",
        },
    },
    "zh": {
        "figures": [
            ("01-layers", "协议分层"),
            ("02-typical-flow", "典型直连聊天流程"),
            ("03-ports", "默认 UDP 端口"),
        ],
        "layers": {
            "title": "ShortP2P 协议分层",
            "boxes": [
                ("应用层", ("ChatWire 消息类型 / 信使 ACK-NACK",)),
                ("会话加密", ("RSA-OAEP 握手，之后 AES-CBC + HMAC（P2P）",)),
                ("传输", ("UDP / BLE / mesh  |  到 Messenger Server 的 HTTPS TLS",)),
                ("身份", ("NetworkId 12 字节  |  RSA-1024 长期密钥",)),
            ],
            "caption": "逻辑栈（非操作系统层）。详见 docs/encryption、docs/messages。",
        },
        "flow": {
            "title": "典型直连聊天流程",
            "boxes": [
                ("1. 联系人", ("invite 0x30", "或二维码")),
                ("2. 会话", ("0x04 / 0x01", "RSA 密钥")),
                ("3. 加密", ("0x02 + AES", "ChatWire")),
                ("4. 可选", ("服务器 / 机器人", "mesh 中继")),
            ],
            "note": ("UDP、BLE、mesh 使用相同的应用加密",),
        },
        "ports": {
            "title": "默认 UDP 端口",
            "boxes": [
                ("17500", ("聊天数据", "握手/加密")),
                ("17501", ("在线通告", "局域网 ping")),
                ("17502", ("聊天邀请",)),
                ("17890", ("发现", "gossip")),
            ],
            "caption": "蓝牙与 mesh 复用相同逻辑帧；Messenger Server 使用 HTTPS。",
        },
    },
}


def _set_fonts(code: str) -> None:
    sans, mono = LANG_FONTS[code]
    _svg_lib.FONT_SANS = sans
    _svg_lib.FONT_MONO = mono


def _build_layers(code: str, data: dict) -> None:
    fills = ("#E8F1F8", "#D9E8F5", "#F7F1E8", "#E6F4EA")
    boxes = [
        Box(40, 30 + i * 70, 680, 50, title, lines, fill=fills[i])
        for i, (title, lines) in enumerate(data["boxes"])
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(caption(40, 320, data["caption"]))
    write_svg(ROOT / f"01-layers.{code}.svg", 760, 350, data["title"], body)


def _build_flow(code: str, data: dict) -> None:
    xs = (20, 170, 330, 500)
    widths = (130, 140, 150, 140)
    fills = ("#E8F1F8", "#D9E8F5", "#D9E8F5", "#F7F1E8")
    boxes = [
        Box(xs[i], 40, widths[i], 70, title, lines, fill=fills[i])
        for i, (title, lines) in enumerate(data["boxes"])
    ]
    note = Box(170, 160, 300, 55, data["note"][0], (), fill="#E6F4EA")
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(150, 75, 170, 75))
    body.extend(arrow(310, 75, 330, 75))
    body.extend(arrow(480, 75, 500, 75))
    body.extend(draw_box(note))
    write_svg(ROOT / f"02-typical-flow.{code}.svg", 760, 260, data["title"], body)


def _build_ports(code: str, data: dict) -> None:
    xs = (30, 210, 390, 570)
    fills = ("#E8F1F8", "#F7F1E8", "#F7F1E8", "#F7F1E8")
    boxes = [
        Box(xs[i], 40, 160, 70, title, lines, fill=fills[i])
        for i, (title, lines) in enumerate(data["boxes"])
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(caption(30, 150, data["caption"]))
    write_svg(ROOT / f"03-ports.{code}.svg", 760, 200, data["title"], body)


def build_language(code: str) -> None:
    _set_fonts(code)
    data = DIAGRAM_TEXTS[code]
    _build_layers(code, data["layers"])
    _build_flow(code, data["flow"])
    _build_ports(code, data["ports"])


def main() -> None:
    for code in LANG_FONTS:
        build_language(code)
        print(f"wrote {code} diagram SVGs")


if __name__ == "__main__":
    main()
