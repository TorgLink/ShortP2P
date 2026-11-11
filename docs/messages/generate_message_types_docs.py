#!/usr/bin/env python3
"""Generate ODT + PDF: Message Types in ShortP2P (name, description, plaintext example)."""

from __future__ import annotations

import sys
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
from reportlab.graphics.shapes import Drawing
from reportlab.lib.enums import TA_CENTER, TA_LEFT, TA_RIGHT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import cm
from reportlab.platypus import PageBreak as RLPageBreak
from reportlab.platypus import Paragraph, Preformatted, SimpleDocTemplate, Spacer
from svglib.svglib import svg2rlg

ROOT = Path(__file__).resolve().parent
DIAGRAMS = ROOT / "diagrams"
AUTHOR = "Igor Evdokimov"
REPO_URL = "https://github.com/IskraMessenger/ShortP2P"
CREATED = date(2026, 11, 11)
MODIFIED = date(2026, 11, 11)
TITLE = "Message Types in ShortP2P"
STATUS = (
    "DRAFT — ChatWire and messenger-internal plaintext types. "
    "Examples are unencrypted application payloads (before P2P/server envelopes)."
)

# One page each: name, description, unencrypted example, svg stem
TYPES: list[dict[str, str]] = [
    {
        "title": "1. Text — ChatWire kind 0x01",
        "svg": "01-text",
        "name": "Text (ChatWireText)",
        "description": """
Human-readable chat text shown in the conversation UI.
Wire: magic ASCII "S2P1" (4) + kind 0x01 + text length u32 little-endian + UTF-8 bytes.
Source type: ChatWireText / ChatWireCodec.EncodeText.
""".strip(),
        "example": """
Logical: text = "Hello"

Hex (unencrypted):
  53 32 50 31  01  05 00 00 00  48 65 6C 6C 6F
  | S2P1   |kind| len=5 LE   |  H  e  l  l  o

Decoded: S2P1 | Text | "Hello"
""".strip(),
    },
    {
        "title": "2. Image — ChatWire kind 0x02",
        "svg": "02-image",
        "name": "Image (ChatWireImage)",
        "description": """
Inline image attachment (bytes travel inside the encrypted chat channel).
Wire: S2P1 + 0x02 + mimeLen u16 LE + MIME UTF-8 + imageLen u32 LE + image bytes.
Source type: ChatWireImage / EncodeImage.
""".strip(),
        "example": """
Logical: mimeType = "image/png", imageBytes = <4 demo bytes>

Hex (unencrypted, truncated image body):
  53 32 50 31  02
  09 00                          # mimeLen = 9
  69 6D 61 67 65 2F 70 6E 67     # "image/png"
  04 00 00 00                    # imageLen = 4
  89 50 4E 47                    # demo payload (not a full PNG)

Decoded: S2P1 | Image | mime=image/png | 4 bytes
""".strip(),
    },
    {
        "title": "3. File — ChatWire kind 0x03",
        "svg": "03-file",
        "name": "File (ChatWireFile)",
        "description": """
Inline file attachment with original file name (small files in-band).
Wire: S2P1 + 0x03 + mimeLen u16 LE + MIME + nameLen u16 LE + file name UTF-8
      + fileLen u32 LE + file bytes. File name is Path.GetFileName-normalized.
Source type: ChatWireFile / EncodeFile.
""".strip(),
        "example": """
Logical: fileName = "note.txt", mimeType = "text/plain", fileBytes = "hi"

Hex (unencrypted):
  53 32 50 31  03
  0A 00  74 65 78 74 2F 70 6C 61 69 6E   # mime "text/plain"
  08 00  6E 6F 74 65 2E 74 78 74         # name "note.txt"
  02 00 00 00  68 69                     # len=2, "hi"

Decoded: S2P1 | File | text/plain | note.txt | "hi"
""".strip(),
    },
    {
        "title": "4. Transfer offer — ChatWire kind 0x04",
        "svg": "04-transfer-offer",
        "name": "Transfer offer (ChatWireTransferOffer)",
        "description": """
Offers a large out-of-band transfer (TCP host/port and/or server blob), negotiated
inside the encrypted chat. Payload after kind is JSON UTF-8 with length u32 LE.
Fields include TransferId, TransferToken, PayloadKind, FileName, MimeType,
SizeBytes, Host, Port, ExpiresUtcTicks, optional BlobId / TotalChunks / CiphertextSha256.
Source: ChatWireTransferOffer / EncodeTransferOffer.
""".strip(),
        "example": """
Logical JSON (unencrypted body after S2P1|0x04|len):
  {
    "TransferId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "TransferToken": "tok-demo-001",
    "PayloadKind": "file",
    "FileName": "report.pdf",
    "MimeType": "application/pdf",
    "SizeBytes": 1048576,
    "Host": "192.168.1.20",
    "Port": 18700,
    "ExpiresUtcTicks": 638000000000000000,
    "BlobId": null,
    "TotalChunks": 0,
    "CiphertextSha256": null
  }

Frame: S2P1 | 0x04 | u32 LE(json length) | JSON UTF-8
""".strip(),
    },
    {
        "title": "5. Transfer control — ChatWire kind 0x05",
        "svg": "05-transfer-control",
        "name": "Transfer control (ChatWireTransferControl)",
        "description": """
Control plane for an offered transfer (accept, reject, error, endpoint update).
Wire: S2P1 + 0x05 + u32 LE + JSON UTF-8.
Fields: Command, TransferId, TransferToken, Host, Port, ExpiresUtcTicks, ErrorCode.
Source: ChatWireTransferControl / EncodeTransferControl.
""".strip(),
        "example": """
Logical JSON (unencrypted):
  {
    "Command": "accept",
    "TransferId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "TransferToken": "tok-demo-001",
    "Host": "192.168.1.20",
    "Port": 18700,
    "ExpiresUtcTicks": 638000000000000000,
    "ErrorCode": ""
  }

Frame: S2P1 | 0x05 | u32 LE(json length) | JSON UTF-8
""".strip(),
    },
    {
        "title": "6. User info — ChatWire kind 0x06",
        "svg": "06-user-info",
        "name": "User info (ChatWireUserInfo)",
        "description": """
Pushes the sender's AboutMe text and optional avatar image to the peer.
Not stored as a normal chat bubble in history. Oversized avatars may be JPEG-compressed
to PeerProfileLimits.MaxAvatarBytes before encode.
Wire: S2P1 + 0x06 + aboutLen u16 LE + AboutMe UTF-8 + avatarLen u32 LE + avatar bytes.
Source: ChatWireUserInfo / EncodeUserInfo.
""".strip(),
        "example": """
Logical: aboutMe = "Hi", avatar = empty

Hex (unencrypted):
  53 32 50 31  06
  02 00              # aboutLen = 2
  48 69              # "Hi"
  00 00 00 00        # avatarLen = 0

Decoded: S2P1 | UserInfo | about="Hi" | no avatar
""".strip(),
    },
    {
        "title": "7. Vocabulary offer — ChatWire kind 0x07",
        "svg": "07-vocab-offer",
        "name": "Vocabulary offer (ChatWireVocabOffer)",
        "description": """
Announces which compression dictionaries (language + version + BLAKE3 hex) this peer
can use. Format of the UTF-8 body (VocabularyNegotiation):
  LANG:ver,hash;ver,hash|LANG2:ver,hash
Example languages are upper-case codes such as RU, EN.
Source: ChatWireVocabOffer / EncodeVocabOffer.
""".strip(),
        "example": """
Logical OfferText:
  RU:1,aabbccddeeff0011|EN:1,1122334455667788

Hex (unencrypted, first bytes):
  53 32 50 31  07  <u32 LE length>  <UTF-8 OfferText...>

Decoded: S2P1 | VocabOffer | RU dict v1 + EN dict v1 hashes
""".strip(),
    },
    {
        "title": "8. Vocabulary accept — ChatWire kind 0x08",
        "svg": "08-vocab-accept",
        "name": "Vocabulary accept (ChatWireVocabAccept)",
        "description": """
Selects dictionaries for compressed text, or UNCOMPRESSED per language.
Accept text format: LANG:ver,hash|LANG2:UNCOMPRESSED
Source: ChatWireVocabAccept / EncodeVocabAccept.
""".strip(),
        "example": """
Logical AcceptText:
  RU:1,aabbccddeeff0011|EN:UNCOMPRESSED

Frame: S2P1 | 0x08 | u32 LE | UTF-8 AcceptText

Decoded: use RU dictionary v1; leave EN uncompressed
""".strip(),
    },
    {
        "title": "9. Compressed text — ChatWire kind 0x09",
        "svg": "09-compressed-text",
        "name": "Compressed text (ChatWireCompressedText)",
        "description": """
Chat text encoded with an agreed vocabulary pack instead of raw UTF-8.
Wire: S2P1 + 0x09 + langLen u8 + language UTF-8 (upper) + dictVersion u16 LE
      + payloadLen u32 LE + compressed payload bytes.
Source: ChatWireCompressedText / EncodeCompressedText.
""".strip(),
        "example": """
Logical: language = "RU", dictVersion = 1, payload = AA BB (demo)

Hex (unencrypted):
  53 32 50 31  09
  02  52 55           # langLen=2, "RU"
  01 00               # dictVersion = 1 LE
  02 00 00 00         # payloadLen = 2
  AA BB

Decoded: S2P1 | CompressedText | RU v1 | 2-byte payload
""".strip(),
    },
    {
        "title": "10. Session probe — ChatWire kind 0x0A",
        "svg": "10-session-probe",
        "name": "Session probe (ChatWireSessionProbe)",
        "description": """
Liveness / crypto-session check after handshake; not shown as chat history.
Body (after kind + u32 length): version=1 | probeKind | challenge[8].
probeKind: 1 = send (KindSend), 2 = response (KindResponse).
Sized so one messenger chunk stays within a small encrypted radio budget (~80 B).
Source: ChatWireSessionProbe / SessionCryptoProbe / EncodeSessionProbe.
""".strip(),
        "example": """
Logical: probeKind = 1 (send), challenge = 01..08

Hex (unencrypted):
  53 32 50 31  0A  0A 00 00 00
  01              # body version
  01              # KindSend
  01 02 03 04 05 06 07 08

Decoded: S2P1 | SessionProbe | send | challenge 0102030405060708
""".strip(),
    },
    {
        "title": "11. Legacy UTF-8 text (no S2P1 magic)",
        "svg": "11-legacy-utf8",
        "name": "Legacy plaintext UTF-8",
        "description": """
Older peers may send chat text without the S2P1 frame. If ChatWireCodec.TryParse fails
and the payload does not look like a framed wire, the messenger treats the bytes as UTF-8 text.
Still carried inside the encrypted P2P/server envelope; only the application framing is absent.
""".strip(),
        "example": """
Logical: "Hello"

Hex (unencrypted application payload):
  48 65 6C 6C 6F

Decoded: UTF-8 "Hello" (no S2P1 / kind byte)
""".strip(),
    },
    {
        "title": "12. Delivery ACK — messenger 0xAC",
        "svg": "12-delivery-ack",
        "name": "Delivery ACK (DeliveryAckCodec)",
        "description": """
Messenger-internal receipt: the peer fully reassembled all chunks of a message.
Not a ChatWire kind; plaintext starts with marker 0xAC. Encrypted under the same
P2P session as chat (Scheme 2), then prefixed 0x02 on the wire.
Layout: 0xAC + messageId Guid (16 bytes, Guid.TryWriteBytes order). Total 17 bytes.
""".strip(),
        "example": """
Logical: messageId Guid bytes as written by .NET Guid.TryWriteBytes

Hex (unencrypted messenger plaintext):
  AC  <16 Guid bytes>

Example pattern:
  AC 00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF
  (illustrative Guid layout; use actual TryWriteBytes in implementations)

Decoded: ACK for messageId
""".strip(),
    },
    {
        "title": "13. Delivery NACK — messenger 0xAD",
        "svg": "13-delivery-nack",
        "name": "Delivery NACK (DeliveryNackCodec)",
        "description": """
Messenger-internal request to retransmit missing chunks of a multi-chunk message.
Layout: 0xAD + messageId Guid(16) + count u16 big-endian + count × int32 big-endian indices.
Encrypted like ACK under the P2P session.
""".strip(),
        "example": """
Logical: messageId = <Guid>, missing chunk indices = [0, 2]

Hex (unencrypted):
  AD  <16 Guid bytes>
  00 02                 # count = 2 (BE)
  00 00 00 00           # index 0 (BE)
  00 00 00 02           # index 2 (BE)

Decoded: NACK — please resend chunks 0 and 2 for messageId
""".strip(),
    },
]


def ensure_diagrams() -> None:
    sys.path.insert(0, str(DIAGRAMS))
    import build_diagrams  # noqa: WPS433

    build_diagrams.main()


def svg_path(stem: str) -> Path:
    return DIAGRAMS / f"{stem}.svg"


def load_drawing(stem: str, target_width: float) -> Drawing:
    drawing = svg2rlg(str(svg_path(stem)))
    if drawing is None:
        raise RuntimeError(f"Failed to parse SVG: {stem}")
    scale = target_width / float(drawing.width)
    drawing.width = target_width
    drawing.height = float(drawing.height) * scale
    drawing.scale(scale, scale)
    return drawing


def _add_para_style(doc, name: str, *, size: str, bold: bool, align: str, font: str, break_before_page: bool = False) -> None:
    st = Style(name=name, family="paragraph")
    props = {
        "textalign": {"start": "start", "center": "center", "end": "end"}[align],
        "margintop": "0.1cm",
        "marginbottom": "0.1cm",
    }
    if break_before_page:
        props["breakbefore"] = "page"
    st.addElement(ParagraphProperties(**props))
    st.addElement(
        TextProperties(
            fontsize=size,
            fontweight="bold" if bold else "normal",
            fontname=font,
        )
    )
    doc.styles.addElement(st)


def build_odt(path: Path) -> None:
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

    _add_para_style(doc, "Title", size="26pt", bold=True, align="center", font="Liberation Sans")
    _add_para_style(doc, "Subtitle", size="10pt", bold=False, align="center", font="Liberation Sans")
    _add_para_style(doc, "Meta", size="10pt", bold=False, align="end", font="Liberation Sans")
    _add_para_style(doc, "H1", size="13pt", bold=True, align="start", font="Liberation Sans", break_before_page=True)
    _add_para_style(doc, "Label", size="9pt", bold=True, align="start", font="Liberation Sans")
    _add_para_style(doc, "Body", size="9pt", bold=False, align="start", font="Liberation Sans")
    _add_para_style(doc, "Mono", size="7.5pt", bold=False, align="start", font="Liberation Mono")
    _add_para_style(doc, "TOC", size="10pt", bold=False, align="start", font="Liberation Sans")

    for _ in range(7):
        doc.text.addElement(P(stylename="Body", text=""))
    doc.text.addElement(P(stylename="Title", text=TITLE))
    doc.text.addElement(P(stylename="Subtitle", text=STATUS))
    for _ in range(8):
        doc.text.addElement(P(stylename="Body", text=""))
    doc.text.addElement(P(stylename="Meta", text=f"Author: {AUTHOR}"))
    doc.text.addElement(P(stylename="Meta", text=f"Repository: {REPO_URL}"))
    doc.text.addElement(P(stylename="Meta", text=f"Created: {CREATED.isoformat()}"))
    doc.text.addElement(P(stylename="Meta", text=f"Modified: {MODIFIED.isoformat()}"))
    doc.text.addElement(P(stylename="Meta", text="Vector sources: docs/messages/diagrams/*.svg"))

    doc.text.addElement(H(outlinelevel=1, stylename="H1", text="Contents"))
    doc.text.addElement(P(stylename="TOC", text="Title page"))
    doc.text.addElement(P(stylename="TOC", text="Contents"))
    for item in TYPES:
        doc.text.addElement(P(stylename="TOC", text=item["title"]))

    for item in TYPES:
        stem = item["svg"]
        doc.text.addElement(H(outlinelevel=1, stylename="H1", text=item["title"]))
        href = doc.addPicture(str(svg_path(stem)))
        frame = Frame(width="17.5cm", height="4.2cm", anchortype="paragraph")
        frame.addElement(OdfImage(href=href))
        p_img = P(stylename="Body")
        p_img.addElement(frame)
        doc.text.addElement(p_img)

        doc.text.addElement(P(stylename="Label", text="Name"))
        doc.text.addElement(P(stylename="Body", text=item["name"]))
        doc.text.addElement(P(stylename="Label", text="Description"))
        for line in item["description"].splitlines():
            doc.text.addElement(P(stylename="Body", text=line if line else " "))
        doc.text.addElement(P(stylename="Label", text="Unencrypted example"))
        for line in item["example"].splitlines():
            doc.text.addElement(P(stylename="Mono", text=line if line else " "))

    out_base = str(path)
    if out_base.lower().endswith(".odt"):
        out_base = out_base[:-4]
    doc.save(out_base, True)
    produced = Path(out_base + ".odt")
    if produced.resolve() != path.resolve() and produced.exists():
        produced.replace(path)


def build_pdf(path: Path) -> None:
    styles = getSampleStyleSheet()
    title_style = ParagraphStyle("DocTitle", parent=styles["Title"], fontSize=24, leading=30, alignment=TA_CENTER, spaceAfter=12)
    subtitle = ParagraphStyle("DocSub", parent=styles["Normal"], fontSize=9, leading=11, alignment=TA_CENTER, spaceAfter=8)
    meta = ParagraphStyle("DocMeta", parent=styles["Normal"], fontSize=10, leading=12, alignment=TA_RIGHT, spaceBefore=3)
    h1 = ParagraphStyle("DocH1", parent=styles["Heading1"], fontSize=12, leading=15, spaceAfter=6, alignment=TA_LEFT)
    label = ParagraphStyle("DocLabel", parent=styles["Normal"], fontSize=9, leading=11, fontName="Helvetica-Bold", spaceBefore=4, spaceAfter=2)
    body = ParagraphStyle("DocBody", parent=styles["Normal"], fontSize=8.5, leading=11, fontName="Helvetica")
    mono = ParagraphStyle("DocMono", parent=styles["Code"], fontSize=7, leading=9, fontName="Courier")
    toc_style = ParagraphStyle("DocToc", parent=styles["Normal"], fontSize=10, leading=13, leftIndent=8)

    story: list = []
    story.append(Spacer(1, 5 * cm))
    story.append(Paragraph(TITLE, title_style))
    story.append(Paragraph(STATUS.replace("—", "-"), subtitle))
    story.append(Spacer(1, 6.5 * cm))
    story.append(Paragraph(f"Author: {AUTHOR}", meta))
    story.append(Paragraph(f"Repository: {REPO_URL}", meta))
    story.append(Paragraph(f"Created: {CREATED.isoformat()}", meta))
    story.append(Paragraph(f"Modified: {MODIFIED.isoformat()}", meta))
    story.append(Paragraph("Vector sources: docs/messages/diagrams/*.svg", meta))
    story.append(RLPageBreak())

    story.append(Paragraph("Contents", h1))
    story.append(Paragraph("Title page", toc_style))
    story.append(Paragraph("Contents", toc_style))
    for item in TYPES:
        story.append(Paragraph(item["title"].replace("&", "&amp;"), toc_style))
    story.append(RLPageBreak())

    page_width = A4[0] - 3.2 * cm
    for i, item in enumerate(TYPES):
        story.append(Paragraph(item["title"].replace("&", "&amp;"), h1))
        story.append(load_drawing(item["svg"], page_width))
        story.append(Spacer(1, 0.15 * cm))
        story.append(Paragraph("Name", label))
        story.append(Paragraph(item["name"].replace("&", "&amp;"), body))
        story.append(Paragraph("Description", label))
        story.append(Preformatted(item["description"], mono))
        story.append(Paragraph("Unencrypted example", label))
        story.append(Preformatted(item["example"], mono))
        if i != len(TYPES) - 1:
            story.append(RLPageBreak())

    doc = SimpleDocTemplate(
        str(path),
        pagesize=A4,
        leftMargin=1.6 * cm,
        rightMargin=1.6 * cm,
        topMargin=1.6 * cm,
        bottomMargin=1.6 * cm,
        title=TITLE,
        author=AUTHOR,
    )
    doc.build(story)


def main() -> None:
    ensure_diagrams()
    for item in TYPES:
        if not svg_path(item["svg"]).exists():
            raise FileNotFoundError(svg_path(item["svg"]))

    odt_path = ROOT / "Message-Types-in-ShortP2P.odt"
    pdf_path = ROOT / "Message-Types-in-ShortP2P.pdf"
    build_odt(odt_path)
    build_pdf(pdf_path)
    print(f"Wrote {odt_path}")
    print(f"Wrote {pdf_path}")


if __name__ == "__main__":
    main()
