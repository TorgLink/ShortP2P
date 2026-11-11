#!/usr/bin/env python3
"""Minimal SVG flowchart helpers (font switchable per language)."""

from __future__ import annotations

from dataclasses import dataclass
from html import escape
from pathlib import Path

# Overridden by build_diagrams before writing each language's SVGs.
FONT_SANS = "Segoe UI, Arial, sans-serif"
FONT_MONO = "Consolas, Courier New, monospace"


@dataclass
class Box:
    x: float
    y: float
    w: float
    h: float
    title: str
    lines: tuple[str, ...] = ()
    fill: str = "#F4F7FA"
    stroke: str = "#1F3A5F"


def svg_header(width: int, height: int, title: str) -> list[str]:
    return [
        '<?xml version="1.0" encoding="UTF-8"?>',
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" '
        f'viewBox="0 0 {width} {height}" role="img">',
        f"  <title>{escape(title)}</title>",
        '  <rect width="100%" height="100%" fill="#FFFFFF"/>',
        '  <defs>',
        '    <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" '
        'markerWidth="8" markerHeight="8" orient="auto-start-reverse">',
        '      <path d="M 0 0 L 10 5 L 0 10 z" fill="#1F3A5F"/>',
        "    </marker>",
        "  </defs>",
    ]


def svg_footer() -> list[str]:
    return ["</svg>"]


def draw_box(b: Box) -> list[str]:
    lines = [
        f'  <rect x="{b.x}" y="{b.y}" width="{b.w}" height="{b.h}" rx="6" ry="6" '
        f'fill="{b.fill}" stroke="{b.stroke}" stroke-width="1.5"/>',
        f'  <text x="{b.x + b.w / 2}" y="{b.y + 18}" text-anchor="middle" '
        f'font-family="{FONT_SANS}" font-size="12" font-weight="700" '
        f'fill="#12233A">{escape(b.title)}</text>',
    ]
    for i, line in enumerate(b.lines):
        lines.append(
            f'  <text x="{b.x + b.w / 2}" y="{b.y + 36 + i * 14}" text-anchor="middle" '
            f'font-family="{FONT_MONO}" font-size="10" '
            f'fill="#243447">{escape(line)}</text>'
        )
    return lines


def arrow(x1: float, y1: float, x2: float, y2: float, label: str = "") -> list[str]:
    out = [
        f'  <line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" '
        f'stroke="#1F3A5F" stroke-width="1.5" marker-end="url(#arrow)"/>'
    ]
    if label:
        mx, my = (x1 + x2) / 2, (y1 + y2) / 2 - 6
        out.append(
            f'  <text x="{mx}" y="{my}" text-anchor="middle" '
            f'font-family="{FONT_SANS}" font-size="10" fill="#334E68">'
            f"{escape(label)}</text>"
        )
    return out


def caption(x: float, y: float, text: str) -> list[str]:
    return [
        f'  <text x="{x}" y="{y}" font-family="{FONT_SANS}" '
        f'font-size="11" fill="#486581">{escape(text)}</text>'
    ]


def write_svg(path: Path, width: int, height: int, title: str, body: list[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    parts = svg_header(width, height, title) + body + svg_footer()
    path.write_text("\n".join(parts) + "\n", encoding="utf-8")
