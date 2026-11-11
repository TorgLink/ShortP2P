#!/usr/bin/env python3
"""Build SVG vector sources + text transcriptions for encryption schemes."""

from __future__ import annotations

from pathlib import Path

from _svg_lib import Box, arrow, caption, draw_box, write_svg

ROOT = Path(__file__).resolve().parent


def scheme_01_session() -> tuple[str, str]:
    title = "P2P session establishment (RSA to session keys)"
    boxes = [
        Box(40, 40, 150, 70, "RNG", ("48 random bytes", "aesKey16||macKey32"), fill="#E8F1F8"),
        Box(240, 40, 200, 70, "RSA-OAEP SHA-1", ("peer RSA-1024 pub", "ciphertext 128 B"), fill="#D9E8F5"),
        Box(500, 40, 170, 70, "Wire 0x01", ("0x01 || CT(128)", "total 129 B"), fill="#F7F1E8"),
        Box(240, 160, 200, 70, "RSA decrypt", ("local RSA priv", "sessionKeys 48 B"), fill="#D9E8F5"),
        Box(500, 160, 170, 70, "P2PSession", ("AES-128 key", "HMAC key 32 B"), fill="#E6F4EA"),
        Box(40, 160, 150, 70, "Follower 0x04", ("0x04||NetId(12)", "plaintext request"), fill="#F7F1E8"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(190, 75, 240, 75, "sessionKeys"))
    body.extend(arrow(440, 75, 500, 75, "encrypt"))
    body.extend(arrow(585, 110, 585, 160, "to peer"))
    body.extend(arrow(440, 195, 500, 195, "split keys"))
    body.extend(arrow(190, 195, 240, 195, "optional"))
    body.extend(caption(40, 270, "Leader = smaller NetworkId (12 B). Follower waits for 0x01 after 0x04."))
    write_svg(ROOT / "01-p2p-session.svg", 720, 300, title, body)
    text = """
VECTOR TEXT: 01-p2p-session.svg
[RNG 48B aes||mac] -> [RSA-OAEP-SHA1 peer pub] -> [wire 0x01||CT128]
                                                      |
peer: [0x01] -> [RSA-OAEP-SHA1 local priv] -> [P2PSession AES+HMAC]
follower may send [0x04||NetworkId12] first (plaintext wrapper)
""".strip()
    return "01-p2p-session.svg", text


def scheme_02_packet() -> tuple[str, str]:
    title = "P2P symmetric packet (AES-CBC + HMAC)"
    boxes = [
        Box(30, 50, 130, 80, "Plaintext", ("ChatWire / ACK", "optional Chunk"), fill="#E8F1F8"),
        Box(200, 50, 140, 80, "AES-128-CBC", ("PKCS7", "IV = RNG 16"), fill="#D9E8F5"),
        Box(380, 50, 150, 80, "HMAC-SHA256", ("macKey", "tag = first 16 B"), fill="#D9E8F5"),
        Box(570, 50, 130, 80, "Packet", ("IV||CT||TAG", "<= 1024 B"), fill="#E6F4EA"),
        Box(300, 180, 160, 60, "Wire 0x02", ("0x02 || packet",), fill="#F7F1E8"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(160, 90, 200, 90))
    body.extend(arrow(340, 90, 380, 90))
    body.extend(arrow(530, 90, 570, 90))
    body.extend(arrow(635, 130, 380, 180, "frame"))
    body.extend(caption(30, 270, "Decrypt inverse: strip 0x02, verify tag (constant-time), AES-CBC decrypt, unchunk."))
    write_svg(ROOT / "02-p2p-packet.svg", 740, 300, title, body)
    text = """
VECTOR TEXT: 02-p2p-packet.svg
[plain (+chunk)] -> [AES-128-CBC + IV16] -> [HMAC-SHA256 tag16] -> [IV||CT||TAG<=1024] -> [0x02||packet]
decrypt: reverse order with constant-time tag check
""".strip()
    return "02-p2p-packet.svg", text


def scheme_03_udp() -> tuple[str, str]:
    title = "P2P over UDP"
    boxes = [
        Box(40, 40, 160, 70, "App", ("ChatWire", "session setup"), fill="#E8F1F8"),
        Box(260, 40, 180, 70, "Crypto Sch.1+2", ("0x01/0x04/0x02",), fill="#D9E8F5"),
        Box(500, 40, 180, 70, "UDP 17500", ("data port",), fill="#F7F1E8"),
        Box(260, 160, 180, 70, "Optional RELAY", ("0x22 outer clear", "inner 0x02 opaque"), fill="#F7F1E8"),
        Box(500, 160, 180, 70, "Peer App", ("decrypt Sch.2",), fill="#E6F4EA"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(200, 75, 260, 75))
    body.extend(arrow(440, 75, 500, 75))
    body.extend(arrow(590, 110, 590, 160))
    body.extend(arrow(350, 110, 350, 160, "or via"))
    body.extend(caption(40, 260, "Invite 17502 / presence 17501 / discovery 17890 stay plaintext."))
    write_svg(ROOT / "03-p2p-udp.svg", 740, 300, title, body)
    text = """
VECTOR TEXT: 03-p2p-udp.svg
[App] -> [Sch.1+2 frames] -> [UDP 17500] -> [Peer]
optional path: [RELAY 0x22] carries opaque inner 0x02
plaintext: invite/presence/discovery
""".strip()
    return "03-p2p-udp.svg", text


def scheme_04_ble() -> tuple[str, str]:
    title = "P2P over BLE"
    boxes = [
        Box(40, 50, 170, 70, "App plaintext", ("ChatWire",), fill="#E8F1F8"),
        Box(260, 50, 180, 70, "Same Sch.1+2", ("0x01 / 0x04 / 0x02",), fill="#D9E8F5"),
        Box(500, 50, 180, 70, "BLE transport", ("RFCOMM/GATT", "MAC addressing"), fill="#F7F1E8"),
        Box(260, 170, 180, 60, "Peer decrypt", ("P2PSession",), fill="#E6F4EA"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(210, 85, 260, 85))
    body.extend(arrow(440, 85, 500, 85))
    body.extend(arrow(590, 120, 350, 170))
    body.extend(caption(40, 260, "Identity ads / invite RSA public JSON remain clear on BLE."))
    write_svg(ROOT / "04-p2p-ble.svg", 740, 300, title, body)
    text = """
VECTOR TEXT: 04-p2p-ble.svg
[App] -> [Sch.1+2 identical packets] -> [BLE] -> [Peer decrypt]
crypto unchanged vs UDP; transport only differs
""".strip()
    return "04-p2p-ble.svg", text


def scheme_05_mesh() -> tuple[str, str]:
    title = "P2P over mesh / LoRa"
    boxes = [
        Box(30, 50, 140, 70, "App", ("often small", "ChatWire"), fill="#E8F1F8"),
        Box(200, 50, 150, 70, "Chunk+Sch.2", ("more chunks", "if small MTU"), fill="#D9E8F5"),
        Box(380, 50, 140, 70, "Host mesh TX", ("LoRa/Meshtastic", "framing"), fill="#F7F1E8"),
        Box(550, 50, 140, 70, "Hops", ("opaque cipher",), fill="#F7F1E8"),
        Box(380, 170, 140, 60, "Host mesh RX", ("to Sch.2 dec",), fill="#E6F4EA"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(170, 85, 200, 85))
    body.extend(arrow(350, 85, 380, 85))
    body.extend(arrow(520, 85, 550, 85))
    body.extend(arrow(620, 120, 450, 170))
    body.extend(caption(30, 260, "Application crypto = Schemes 1-2; mesh host adds transport framing."))
    write_svg(ROOT / "05-p2p-mesh.svg", 740, 300, title, body)
    text = """
VECTOR TEXT: 05-p2p-mesh.svg
[App] -> [Chunk+Sch.2] -> [mesh TX] -> [hops] -> [mesh RX] -> [Sch.2 decrypt] -> [App]
""".strip()
    return "05-p2p-mesh.svg", text


def scheme_06_tls() -> tuple[str, str]:
    title = "Client to Server transport (TLS + JWT)"
    boxes = [
        Box(40, 60, 150, 70, "Client", ("HTTPS", "cert pin SHA-256"), fill="#E8F1F8"),
        Box(260, 60, 180, 70, "TLS channel", ("Bearer JWT", "HMAC-SHA256"), fill="#D9E8F5"),
        Box(510, 60, 170, 70, "Messenger API", ("opaque E2E fields", "no body decrypt"), fill="#F7F1E8"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(190, 95, 260, 95, "TLS"))
    body.extend(arrow(440, 95, 510, 95, "JWT"))
    body.extend(caption(40, 180, "Protects transport only. Chat secrecy is Schemes 7-9."))
    write_svg(ROOT / "06-client-server-tls.svg", 740, 230, title, body)
    text = """
VECTOR TEXT: 06-client-server-tls.svg
[Client] --TLS+JWT--> [Messenger Server API]
server stores/forwards EncryptedDataBase64 opaque
""".strip()
    return "06-client-server-tls.svg", text


def scheme_07_v2() -> tuple[str, str]:
    title = "Server payload v2 (session AES-GCM)"
    boxes = [
        Box(20, 40, 140, 70, "ChatWire", ("+ probeToken", "on establish"), fill="#E8F1F8"),
        Box(190, 40, 160, 70, "AES-256-GCM", ("nonce RNG 12", "tag 16"), fill="#D9E8F5"),
        Box(380, 40, 170, 70, "RSA wrap once", ("OAEP-SHA256", "SessionId||key"), fill="#D9E8F5"),
        Box(580, 40, 130, 70, "Blob v2", ("kind 1/2/4/5", "Base64"), fill="#E6F4EA"),
        Box(300, 160, 200, 55, "TLS to Server to Peer", ("Scheme 6",), fill="#F7F1E8"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(160, 75, 190, 75))
    body.extend(arrow(350, 75, 380, 75, "kind1"))
    body.extend(arrow(550, 75, 580, 75))
    body.extend(arrow(645, 110, 400, 160))
    body.extend(caption(20, 250, "Later data kind=2 uses SessionId + AES-GCM only (no RSA)."))
    write_svg(ROOT / "07-server-payload-v2.svg", 740, 290, title, body)
    text = """
VECTOR TEXT: 07-server-payload-v2.svg
establish: [ChatWire||probe] -> AES-256-GCM -> RSA-OAEP-SHA256(SessionId||key) -> v2 kind1 -> Base64 -> TLS
data: [ChatWire] -> AES-256-GCM -> v2 kind2 SessionId||nonce||CT||tag
""".strip()
    return "07-server-payload-v2.svg", text


def scheme_08_v1() -> tuple[str, str]:
    title = "Server payload v1 (hybrid AES-GCM + RSA)"
    boxes = [
        Box(40, 50, 140, 70, "Plain", ("ChatWire/blob",), fill="#E8F1F8"),
        Box(220, 50, 170, 70, "AES-256-GCM", ("key32 + nonce12", "RNG per message"), fill="#D9E8F5"),
        Box(430, 50, 180, 70, "RSA-OAEP-SHA256", ("wrap key||nonce", "recipient pub"), fill="#D9E8F5"),
        Box(250, 170, 200, 55, "v1 blob Base64 / TLS", ("server opaque",), fill="#E6F4EA"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(180, 85, 220, 85))
    body.extend(arrow(390, 85, 430, 85))
    body.extend(arrow(520, 120, 350, 170))
    body.extend(caption(40, 260, "Used for bots and as blob fallback."))
    write_svg(ROOT / "08-server-payload-v1.svg", 740, 300, title, body)
    text = """
VECTOR TEXT: 08-server-payload-v1.svg
[plain] -> [AES-256-GCM] -> [RSA-OAEP-SHA256 wrap key||nonce] -> [v1 blob] -> Base64 -> TLS -> peer reverse
""".strip()
    return "08-server-payload-v1.svg", text


def scheme_09_bot() -> tuple[str, str]:
    title = "Client to Server to Bot"
    boxes = [
        Box(40, 60, 140, 70, "Client", ("ChatWire", "v1 encrypt"), fill="#E8F1F8"),
        Box(250, 60, 200, 70, "Server", ("opaque store", "TLS+JWT only"), fill="#F7F1E8"),
        Box(520, 60, 160, 70, "Bot", ("v1 decrypt", "bot RSA priv"), fill="#E6F4EA"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(arrow(180, 95, 250, 95, "v1 E2E"))
    body.extend(arrow(450, 95, 520, 95, "v1 E2E"))
    body.extend(caption(40, 180, "Reply path mirrors: Bot --v1--> Server --v1--> Client."))
    write_svg(ROOT / "09-client-server-bot.svg", 740, 230, title, body)
    text = """
VECTOR TEXT: 09-client-server-bot.svg
[Client] --payload v1 E2E--> [Server opaque] --v1 E2E--> [Bot]
transport on both hops: TLS + JWT (Scheme 6); no v2 session for bots
""".strip()
    return "09-client-server-bot.svg", text


def scheme_10_types() -> tuple[str, str]:
    title = "Message types inside encrypted channels"
    # table-like boxes
    kinds = [
        ("0x01 Text", 40),
        ("0x02 Image", 160),
        ("0x03 File", 280),
        ("0x04 Offer", 400),
        ("0x05 Ctrl", 520),
        ("0x06 Info", 640),
    ]
    body: list[str] = [
        '  <text x="40" y="30" font-family="Segoe UI, Arial, sans-serif" font-size="13" '
        'font-weight="700" fill="#12233A">ChatWire magic S2P1 — plaintext before Sch.2/7/8/9</text>'
    ]
    for label, x in kinds:
        body.extend(draw_box(Box(x, 50, 100, 45, label, fill="#E8F1F8")))
    row2 = [
        ("0x07 VocabOff", 40),
        ("0x08 VocabAcc", 160),
        ("0x09 CompText", 280),
        ("0x0A Probe", 400),
        ("0xAC ACK", 520),
        ("0xAD NACK", 640),
    ]
    for label, x in row2:
        body.extend(draw_box(Box(x, 120, 100, 45, label, fill="#D9E8F5")))
    body.extend(draw_box(Box(200, 200, 340, 55, "Encrypt envelope", ("P2P Sch.2 / Server v2 or v1 / Bot v1",), fill="#E6F4EA")))
    write_svg(ROOT / "10-message-types.svg", 780, 290, title, body)
    text = """
VECTOR TEXT: 10-message-types.svg
S2P1 kinds: 01 text, 02 image, 03 file, 04 offer, 05 control, 06 userinfo,
07/08 vocab, 09 compressed text, 0A session probe; plus ACK 0xAC / NACK 0xAD
All are plaintext inputs to Sch.2 (P2P) or Sch.7/8/9 (server/bot).
""".strip()
    return "10-message-types.svg", text


def scheme_11_plain() -> tuple[str, str]:
    title = "Plaintext frames (no application E2E)"
    boxes = [
        Box(40, 40, 160, 60, "Invite 0x30", ("RSA pub JSON",), fill="#F7F1E8"),
        Box(220, 40, 160, 60, "Presence 0x31", ("LAN announce",), fill="#F7F1E8"),
        Box(400, 40, 160, 60, "Gossip 0x40-45", ("discovery",), fill="#F7F1E8"),
        Box(580, 40, 140, 60, "FIND/FOUND", ("0x10/0x11",), fill="#F7F1E8"),
        Box(130, 140, 200, 60, "BLE NetId ads", ("identity only",), fill="#F7F1E8"),
        Box(360, 140, 220, 60, "Forward profile", ("TLS only, no E2E",), fill="#F7F1E8"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(caption(40, 240, "Clear control/identity must not be confused with encrypted ChatWire paths."))
    write_svg(ROOT / "11-plaintext-frames.svg", 760, 280, title, body)
    text = """
VECTOR TEXT: 11-plaintext-frames.svg
clear on wire: invite, presence, gossip/profile, FIND/FOUND, BLE ads, forward API profiles
secret ChatWire only via Schemes 2 / 7 / 8 / 9
""".strip()
    return "11-plaintext-frames.svg", text


def scheme_12_ancillary() -> tuple[str, str]:
    title = "Ancillary crypto (non-chat wire)"
    boxes = [
        Box(40, 50, 200, 80, "Safety number", ("SHA-256(mod||exp)", "emoji glyphs"), fill="#E8F1F8"),
        Box(280, 50, 220, 80, "Profile .tlp backup", ("PBKDF2-SHA256 600k", "ChaCha20-Poly1305"), fill="#D9E8F5"),
        Box(540, 50, 180, 80, "Identity RSA", ("RSA-1024", "register keys"), fill="#E6F4EA"),
    ]
    body: list[str] = []
    for b in boxes:
        body.extend(draw_box(b))
    body.extend(caption(40, 180, "Not used as the chat packet cipher path."))
    write_svg(ROOT / "12-ancillary.svg", 760, 230, title, body)
    text = """
VECTOR TEXT: 12-ancillary.svg
[SafetyNumber SHA-256] | [Profile backup PBKDF2 + ChaCha20-Poly1305] | [RSA-1024 identity]
""".strip()
    return "12-ancillary.svg", text


BUILDERS = [
    scheme_01_session,
    scheme_02_packet,
    scheme_03_udp,
    scheme_04_ble,
    scheme_05_mesh,
    scheme_06_tls,
    scheme_07_v2,
    scheme_08_v1,
    scheme_09_bot,
    scheme_10_types,
    scheme_11_plain,
    scheme_12_ancillary,
]


def main() -> dict[str, str]:
    mapping: dict[str, str] = {}
    for builder in BUILDERS:
        name, text = builder()
        txt_path = ROOT / (Path(name).stem + ".txt")
        txt_path.write_text(text + "\n", encoding="utf-8")
        mapping[name] = text
        print(f"wrote {name} and {txt_path.name}")
    return mapping


if __name__ == "__main__":
    main()
