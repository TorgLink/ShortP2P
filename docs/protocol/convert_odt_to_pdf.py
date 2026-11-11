#!/usr/bin/env python3
"""Convert protocol ODT documents to PDF via LibreOffice (250 DPI export).

Overwrites the .pdf files next to the .odt sources.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SOFFICE = Path(r"E:\LibreOffice26.2\program\soffice.exe")

# Images inside the ODTs are vector (SVG); the 250 DPI cap only affects
# raster images if any ever appear.
FILTER = (
    'pdf:writer_pdf_Export:{'
    '"ReduceImageResolution":{"type":"boolean","value":true},'
    '"MaxImageResolution":{"type":"long","value":250}'
    "}"
)

ODT_STEMS = [
    "ShortP2P-Protocol-en",
    "ShortP2P-Protocol-ru",
    "ShortP2P-Protocol-fr",
    "ShortP2P-Protocol-es",
    "ShortP2P-Protocol-de",
    "ShortP2P-Protocol-zh-Hans",
]


def main() -> int:
    if not SOFFICE.exists():
        print(f"ERROR: LibreOffice not found at {SOFFICE}")
        return 1

    for stem in ODT_STEMS:
        odt = ROOT / f"{stem}.odt"
        pdf = ROOT / f"{stem}.pdf"
        if not odt.exists():
            print(f"ERROR: missing {odt.name}")
            return 1
        cmd = [
            str(SOFFICE),
            "--headless",
            "--norestore",
            "--convert-to",
            FILTER,
            "--outdir",
            str(ROOT),
            str(odt),
        ]
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
        if result.returncode != 0 or not pdf.exists():
            print(f"FAIL {stem}: rc={result.returncode} out={result.stdout.strip()} err={result.stderr.strip()}")
            return 1
        print(f"converted {stem}.odt -> {stem}.pdf ({pdf.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
