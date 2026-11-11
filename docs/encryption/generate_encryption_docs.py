#!/usr/bin/env python3
"""Generate ODT + PDF for ShortP2P encryption docs (SHO-30).

Vector sources live in diagrams/*.svg (plus *.txt transcriptions).
Documents embed the figures and the text form of each diagram.
"""

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
TITLE = "Encryption in ShortP2P"
STATUS = "DRAFT — vector sources in docs/encryption/diagrams/*.svg."

# title, svg stem, narrative notes (not the vector text — that comes from .txt)
SCHEMES: list[dict[str, str]] = [
    {
        "title": "1. P2P session establishment (RSA to session keys)",
        "svg": "01-p2p-session",
        "notes": """
Applies to mesh / UDP / BLE before ciphertext messages.
Roles: smaller CompressedNetworkId (12 B) = leader; larger = follower.
Session material: aesKey[16] || macKey[32] (RNG), wrapped with RSA-OAEP(SHA-1).
Wire: leader 0x01||CT(128); follower may send 0x04||NetworkId(12).
Sources: P2PHandshake.cs, P2PCrypto.cs, ChatP2pSession / HandshakeTransceiver.
""".strip(),
    },
    {
        "title": "2. P2P symmetric packet (AES-CBC + HMAC)",
        "svg": "02-p2p-packet",
        "notes": """
Shared by UDP / BLE / mesh after session establishment.
AES-128-CBC PKCS7, IV 16 RNG; HMAC-SHA256 truncated to 16; packet IV||CT||TAG <= 1024.
Transport prefix 0x02. Optional ChunkCodec before encrypt.
Sources: P2PSession.cs, ChunkCodec, MessageTransceiver.
""".strip(),
    },
    {
        "title": "3. P2P over UDP (data / invite / relay)",
        "svg": "03-p2p-udp",
        "notes": """
Ports: 17500 data, 17502 invite, 17501 presence, 17890 discovery.
Encrypted chat uses Schemes 1-2 on 17500; RELAY 0x22 may carry opaque 0x02.
Invite/presence/gossip remain plaintext.
""".strip(),
    },
    {
        "title": "4. P2P over BLE",
        "svg": "04-p2p-ble",
        "notes": """
Same logical crypto packets as UDP (Schemes 1-2) over Bluetooth transport.
Identity advertisements and invite RSA public JSON stay clear.
""".strip(),
    },
    {
        "title": "5. P2P over mesh (LoRa / Meshtastic)",
        "svg": "05-p2p-mesh",
        "notes": """
Application crypto remains Schemes 1-2; host mesh framing is outside ShortP2P.Crypto.
Smaller MTU may increase ChunkCodec fragmentation.
""".strip(),
    },
    {
        "title": "6. Client to Server transport (TLS + JWT)",
        "svg": "06-client-server-tls",
        "notes": """
HTTPS/TLS with optional cert SHA-256 pin; Bearer JWT (HMAC-SHA256).
Server does not decrypt E2E chat bodies (opaque Base64 fields).
""".strip(),
    },
    {
        "title": "7. Client to Client via Server — payload v2",
        "svg": "07-server-payload-v2",
        "notes": """
Preferred human-peer path: AES-256-GCM session; RSA-OAEP-SHA256 once on establish.
Kinds: 1 establish, 2 data, 4 probe ack, 5 rekey. Then Scheme 6 transport.
Sources: MessengerServerPayloadCodec, ServerCipherSessionStore.
""".strip(),
    },
    {
        "title": "8. Client to Client via Server — payload v1",
        "svg": "08-server-payload-v1",
        "notes": """
Per-message hybrid: AES-256-GCM + RSA-OAEP-SHA256 wrap of key||nonce.
Required for bots; blob fallback for humans.
""".strip(),
    },
    {
        "title": "9. Client to Server to Bot",
        "svg": "09-client-server-bot",
        "notes": """
E2E uses payload v1 only (no v2 session). Server is opaque relay with TLS+JWT.
Bot auth (cert fingerprint / bot secret) is separate from message E2E.
""".strip(),
    },
    {
        "title": "10. Message types inside encrypted channels",
        "svg": "10-message-types",
        "notes": """
ChatWire magic S2P1 kinds 0x01-0x0A plus delivery ACK/NACK 0xAC/0xAD.
These are plaintext inputs to Schemes 2 / 7 / 8 / 9.
Source: ChatWireCodec.cs.
""".strip(),
    },
    {
        "title": "11. Plaintext frames (no application E2E)",
        "svg": "11-plaintext-frames",
        "notes": """
Invite, presence, gossip, FIND/FOUND, BLE ads, forward profiles (TLS only).
Do not confuse with encrypted ChatWire payloads.
""".strip(),
    },
    {
        "title": "12. Ancillary crypto (non-chat wire)",
        "svg": "12-ancillary",
        "notes": """
SafetyNumber, profile .tlp backup (PBKDF2 + ChaCha20-Poly1305), RSA-1024 identity.
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
        "margintop": "0.12cm",
        "marginbottom": "0.12cm",
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
            margintop="1.8cm",
            marginbottom="1.8cm",
            marginleft="1.8cm",
            marginright="1.8cm",
        )
    )
    doc.automaticstyles.addElement(pl)
    doc.masterstyles.addElement(MasterPage(name="Standard", pagelayoutname=pl))

    _add_para_style(doc, "Title", size="28pt", bold=True, align="center", font="Liberation Sans")
    _add_para_style(doc, "Subtitle", size="10pt", bold=False, align="center", font="Liberation Sans")
    _add_para_style(doc, "Meta", size="10pt", bold=False, align="end", font="Liberation Sans")
    _add_para_style(doc, "H1", size="14pt", bold=True, align="start", font="Liberation Sans", break_before_page=True)
    _add_para_style(doc, "Body", size="9pt", bold=False, align="start", font="Liberation Sans")
    _add_para_style(doc, "Mono", size="8pt", bold=False, align="start", font="Liberation Mono")
    _add_para_style(doc, "TOC", size="11pt", bold=False, align="start", font="Liberation Sans")

    for _ in range(7):
        doc.text.addElement(P(stylename="Body", text=""))
    doc.text.addElement(P(stylename="Title", text=TITLE))
    doc.text.addElement(P(stylename="Subtitle", text=STATUS))
    for _ in range(9):
        doc.text.addElement(P(stylename="Body", text=""))
    doc.text.addElement(P(stylename="Meta", text=f"Author: {AUTHOR}"))
    doc.text.addElement(P(stylename="Meta", text=f"Repository: {REPO_URL}"))
    doc.text.addElement(P(stylename="Meta", text=f"Created: {CREATED.isoformat()}"))
    doc.text.addElement(P(stylename="Meta", text=f"Modified: {MODIFIED.isoformat()}"))
    doc.text.addElement(P(stylename="Meta", text="Vector sources: docs/encryption/diagrams/*.svg"))

    doc.text.addElement(H(outlinelevel=1, stylename="H1", text="Contents"))
    doc.text.addElement(P(stylename="TOC", text="Title page"))
    doc.text.addElement(P(stylename="TOC", text="Contents"))
    for scheme in SCHEMES:
        doc.text.addElement(P(stylename="TOC", text=scheme["title"]))

    for scheme in SCHEMES:
        stem = scheme["svg"]
        doc.text.addElement(H(outlinelevel=1, stylename="H1", text=scheme["title"]))

        # Embed original SVG (vector) into the ODT package.
        href = doc.addPicture(str(svg_path(stem)))
        frame = Frame(width="17cm", height="6.8cm", anchortype="paragraph")
        frame.addElement(OdfImage(href=href))
        p_img = P(stylename="Body")
        p_img.addElement(frame)
        doc.text.addElement(p_img)

        for line in scheme["notes"].splitlines():
            doc.text.addElement(P(stylename="Body", text=line if line else " "))

    out_base = str(path)
    if out_base.lower().endswith(".odt"):
        out_base = out_base[:-4]
    doc.save(out_base, True)
    produced = Path(out_base + ".odt")
    if produced.resolve() != path.resolve() and produced.exists():
        produced.replace(path)


def build_pdf(path: Path) -> None:
    styles = getSampleStyleSheet()
    title_style = ParagraphStyle("DocTitle", parent=styles["Title"], fontSize=26, leading=32, alignment=TA_CENTER, spaceAfter=14)
    subtitle = ParagraphStyle("DocSub", parent=styles["Normal"], fontSize=9, leading=12, alignment=TA_CENTER, spaceAfter=10)
    meta = ParagraphStyle("DocMeta", parent=styles["Normal"], fontSize=10, leading=13, alignment=TA_RIGHT, spaceBefore=3)
    h1 = ParagraphStyle("DocH1", parent=styles["Heading1"], fontSize=13, leading=16, spaceAfter=8, alignment=TA_LEFT)
    mono = ParagraphStyle("DocMono", parent=styles["Code"], fontSize=7.5, leading=9.5, fontName="Courier")
    toc_style = ParagraphStyle("DocToc", parent=styles["Normal"], fontSize=10, leading=14, leftIndent=8)

    story: list = []
    story.append(Spacer(1, 5.5 * cm))
    story.append(Paragraph(TITLE, title_style))
    story.append(Paragraph(STATUS.replace("—", "-"), subtitle))
    story.append(Spacer(1, 7 * cm))
    story.append(Paragraph(f"Author: {AUTHOR}", meta))
    story.append(Paragraph(f"Repository: {REPO_URL}", meta))
    story.append(Paragraph(f"Created: {CREATED.isoformat()}", meta))
    story.append(Paragraph(f"Modified: {MODIFIED.isoformat()}", meta))
    story.append(Paragraph("Vector sources: docs/encryption/diagrams/*.svg", meta))
    story.append(RLPageBreak())

    story.append(Paragraph("Contents", h1))
    story.append(Paragraph("Title page", toc_style))
    story.append(Paragraph("Contents", toc_style))
    for scheme in SCHEMES:
        story.append(Paragraph(scheme["title"].replace("&", "&amp;"), toc_style))
    story.append(RLPageBreak())

    page_width = A4[0] - 3.6 * cm
    for i, scheme in enumerate(SCHEMES):
        stem = scheme["svg"]
        story.append(Paragraph(scheme["title"].replace("&", "&amp;"), h1))
        story.append(load_drawing(stem, page_width))
        story.append(Spacer(1, 0.2 * cm))
        story.append(Preformatted(scheme["notes"], mono))
        if i != len(SCHEMES) - 1:
            story.append(RLPageBreak())

    doc = SimpleDocTemplate(
        str(path),
        pagesize=A4,
        leftMargin=1.8 * cm,
        rightMargin=1.8 * cm,
        topMargin=1.8 * cm,
        bottomMargin=1.8 * cm,
        title=TITLE,
        author=AUTHOR,
    )
    doc.build(story)


def main() -> None:
    ensure_diagrams()
    for scheme in SCHEMES:
        stem = scheme["svg"]
        if not svg_path(stem).exists():
            raise FileNotFoundError(svg_path(stem))

    odt_path = ROOT / "Encryption-in-ShortP2P.odt"
    pdf_path = ROOT / "Encryption-in-ShortP2P.pdf"
    build_odt(odt_path)
    build_pdf(pdf_path)
    print(f"Wrote {odt_path}")
    print(f"Wrote {pdf_path}")
    print(f"SVG sources in {DIAGRAMS}")


if __name__ == "__main__":
    main()
