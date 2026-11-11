#!/usr/bin/env python3
"""Layout verification for the protocol documents (visual-order checks).

Checks that:
1. Every ODT keeps subtitles with the following content
   (fo:keep-with-next on the HeadBold / H2 paragraph styles) and anchors
   images inline (text:anchor-type="as-char").
2. In every converted PDF (LibreOffice):
   - no page ends (visually) with a stranded subtitle;
   - every numbered subtitle has its infographic below it on the same page.
"""

from __future__ import annotations

import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
import generate_protocol_docs as gen  # noqa: E402
import build_infographics  # noqa: E402

import pymupdf  # noqa: E402

# File-stem suffix -> language code used in the data dicts.
STEM_TO_CODE = {"zh-Hans": "zh"}


def check_odt(stem: str) -> list[str]:
    problems: list[str] = []
    z = zipfile.ZipFile(ROOT / f"{stem}.odt")
    styles = z.read("styles.xml").decode("utf-8")
    for style_name in ("HeadBold", "H2"):
        m = re.search(
            r'<style:style[^>]*style:name="' + style_name + r'".*?</style:style>',
            styles,
            re.DOTALL,
        )
        if not m:
            problems.append(f"style {style_name} missing")
        elif 'fo:keep-with-next="always"' not in m.group(0):
            problems.append(f"style {style_name} has no keep-with-next")

    content = z.read("content.xml").decode("utf-8")
    inline = content.count('text:anchor-type="as-char"')
    floating = content.count('text:anchor-type="paragraph"')
    if floating:
        problems.append(f"{floating} floating image anchor(s) remain")
    if inline < 9:  # 3 shared diagrams + 6 section infographics
        problems.append(f"only {inline} inline images, expected >= 9")
    return problems


def check_pdf(stem: str) -> list[str]:
    problems: list[str] = []
    suffix = stem.replace("ShortP2P-Protocol-", "")
    code = STEM_TO_CODE.get(suffix, suffix)
    headings = set(gen.EXTRA_HEADINGS.get(code, set()))
    def_label = build_infographics.LABELS[code]["def"]

    doc = pymupdf.open(str(ROOT / f"{stem}.pdf"))
    producer = doc.metadata.get("producer") or ""
    if "LibreOffice" not in producer:
        problems.append(f"producer is not LibreOffice: {producer!r}")

    for pno, page in enumerate(doc, 1):
        blocks = [b for b in page.get_text("blocks") if b[6] == 0 and b[4].strip()]
        if not blocks:
            continue

        # 1) no stranded subtitle at the visual bottom of the page
        bottom = max(blocks, key=lambda b: b[3])
        last_line = bottom[4].strip().splitlines()[-1].strip()
        if gen.numbered_section(last_line) is not None or last_line in headings:
            problems.append(f"page {pno}: stranded heading at page bottom: {last_line[:40]}")

        # 2) every numbered subtitle has its infographic below it on the same page
        for b in blocks:
            for ln in b[4].splitlines():
                ln = ln.strip()
                if gen.numbered_section(ln) is not None:
                    has_def_below = any(
                        def_label in ob[4] and ob[1] >= b[3] for ob in blocks
                    )
                    if not has_def_below:
                        problems.append(f"page {pno}: no infographic below heading: {ln[:40]}")
    doc.close()
    return problems


def main() -> int:
    ok = True
    for code in ("en", "ru", "fr", "es", "de", "zh-Hans"):
        stem = f"ShortP2P-Protocol-{code}"
        problems = check_odt(stem) + check_pdf(stem)
        if problems:
            ok = False
            print(f"FAIL {stem}:")
            for p in problems:
                print(f"  - {p}")
        else:
            print(f"PASS {stem}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
