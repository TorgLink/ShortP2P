#!/usr/bin/env python3
"""SVG cards for ShortP2P message-type documentation."""

from __future__ import annotations

from pathlib import Path

from _svg_lib import Box, caption, draw_box, write_svg

ROOT = Path(__file__).resolve().parent

# stem, title on diagram, layout lines
CARDS: list[tuple[str, str, tuple[str, ...]]] = [
    (
        "01-text",
        "Text (0x01)",
        ("S2P1 | kind | u32 LE len | UTF-8", "Chat message body shown in UI"),
    ),
    (
        "02-image",
        "Image (0x02)",
        ("S2P1 | kind | mime | image bytes", "Inline image attachment"),
    ),
    (
        "03-file",
        "File (0x03)",
        ("S2P1 | kind | mime | name | bytes", "Inline small file attachment"),
    ),
    (
        "04-transfer-offer",
        "Transfer offer (0x04)",
        ("S2P1 | kind | u32 | JSON UTF-8", "Out-of-band TCP/blob transfer offer"),
    ),
    (
        "05-transfer-control",
        "Transfer control (0x05)",
        ("S2P1 | kind | u32 | JSON UTF-8", "Accept / reject / error for a transfer"),
    ),
    (
        "06-user-info",
        "User info (0x06)",
        ("S2P1 | kind | about | avatar", "Profile push; not chat history"),
    ),
    (
        "07-vocab-offer",
        "Vocab offer (0x07)",
        ("S2P1 | kind | UTF-8 offer text", "Dictionary versions peer can use"),
    ),
    (
        "08-vocab-accept",
        "Vocab accept (0x08)",
        ("S2P1 | kind | UTF-8 accept text", "Chosen dictionary or UNCOMPRESSED"),
    ),
    (
        "09-compressed-text",
        "Compressed text (0x09)",
        ("S2P1 | kind | lang | ver | payload", "Dictionary-compressed chat text"),
    ),
    (
        "10-session-probe",
        "Session probe (0x0A)",
        ("S2P1 | kind | ver|kind|challenge8", "Crypto session liveness check"),
    ),
    (
        "11-legacy-utf8",
        "Legacy UTF-8 (no S2P1)",
        ("Raw UTF-8 bytes only", "Old clients; treated as text"),
    ),
    (
        "12-delivery-ack",
        "Delivery ACK (0xAC)",
        ("0xAC | Guid(16)", "Messenger-internal full delivery"),
    ),
    (
        "13-delivery-nack",
        "Delivery NACK (0xAD)",
        ("0xAD | Guid | count | indices BE", "Request missing message chunks"),
    ),
]


def main() -> None:
    for stem, title, lines in CARDS:
        body: list[str] = []
        body.extend(
            draw_box(
                Box(40, 40, 680, 90, title, lines, fill="#E8F1F8"),
            )
        )
        body.extend(caption(40, 160, "Unencrypted application plaintext (before session/server envelope)."))
        write_svg(ROOT / f"{stem}.svg", 760, 200, title, body)
        print(f"wrote {stem}.svg")


if __name__ == "__main__":
    main()
