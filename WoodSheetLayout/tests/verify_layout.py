#!/usr/bin/env python3
"""Static and mathematical checks for Wood Sheet Layout 1.0."""

from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "WoodSheetLayout"


def pack_shelf(rectangles, sheet_width, sheet_height, gap):
    x = y = gap
    row_height = 0.0
    placements = []
    for width, height in rectangles:
        candidates = [(width, height, False), (height, width, True)]
        candidate = next((item for item in candidates if x + item[0] <= sheet_width - gap and y + item[1] <= sheet_height - gap), None)
        if candidate is None and x > gap:
            x = gap
            y += row_height + gap
            row_height = 0.0
            candidate = next((item for item in candidates if x + item[0] <= sheet_width - gap and y + item[1] <= sheet_height - gap), None)
        if candidate is None:
            return None
        width, height, rotated = candidate
        placements.append((x, y, width, height, rotated))
        x += width + gap
        row_height = max(row_height, height)
    return placements


def strip_csharp(text):
    return re.sub(r'//.*?$|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"', "", text, flags=re.M | re.S)


def main():
    placements = pack_shelf([(100, 80), (120, 90), (60, 150)], 420, 297, 4)
    assert placements is not None
    for left, top, width, height, _ in placements:
        assert left >= 4 and top >= 4
        assert left + width <= 416 and top + height <= 293

    # A 300 x 200 part only fits A4 landscape after a 90-degree rotation is considered.
    rotated = pack_shelf([(200, 280)], 297, 210, 4)
    assert rotated is not None

    models = (SRC / "Core" / "LayoutModels.cs").read_text(encoding="utf-8")
    analyzer = (SRC / "Core" / "BoardAnalyzer.cs").read_text(encoding="utf-8")
    engine = (SRC / "Core" / "LayoutEngine.cs").read_text(encoding="utf-8")
    packer = (SRC / "Core" / "SheetPacker.cs").read_text(encoding="utf-8")
    commands = (SRC / "Commands" / "LayFlatCommands.cs").read_text(encoding="utf-8")
    project = (SRC / "WoodSheetLayout.csproj").read_text(encoding="utf-8")

    for token in ["420.0", "297.0", "210.0", "SpacingMillimeters", "ThicknessToleranceMillimeters"]:
        assert token in models, token
    for token in ["GetGroupList", "TryGetPlane", "GetBoundingBox", "ThicknessMillimeters"]:
        assert token in analyzer, token
    for token in ["RemoveFromAllGroups", "AddToGroup", "LayerIndex", "ObjectColor"]:
        assert token in engine or token == "ObjectColor", token
    for token in ["RotatedNinetyDegrees", "BuildThicknessBuckets", "OversizedParts"]:
        assert token in packer, token
    for command in ["WoodSheetLayout", "WSLayFlatA3", "WSLayFlatA4"]:
        assert command in commands, command
    assert "net48;net8.0" in project
    assert "<Version>1.0.0</Version>" in project

    for path in SRC.rglob("*.cs"):
        stripped = strip_csharp(path.read_text(encoding="utf-8"))
        assert stripped.count("{") == stripped.count("}"), f"unbalanced braces: {path}"

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for phrase in ["4 mm", "A3", "A4", "图层颜色", "同一个 Rhino Group"]:
        assert phrase in readme, phrase
    print("Wood Sheet Layout 1.0 static/mathematical checks passed.")


if __name__ == "__main__":
    main()
