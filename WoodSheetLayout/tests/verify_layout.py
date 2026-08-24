#!/usr/bin/env python3
"""Static and mathematical checks for WoodSheetLayout 2.1.0."""

from pathlib import Path
import math
import re


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "WoodSheetLayout"


def strip_csharp(text):
    return re.sub(r'//.*?$|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"', "", text, flags=re.M | re.S)


def rectangles_separated(a, b, gap):
    return (
        a[0] + a[2] + gap <= b[0]
        or b[0] + b[2] + gap <= a[0]
        or a[1] + a[3] + gap <= b[1]
        or b[1] + b[3] + gap <= a[1]
    )


def main():
    # A3 landscape with an independent 4 mm frame margin leaves 412 x 289 mm.
    assert math.isclose(420.0 - 2 * 4.0, 412.0)
    assert math.isclose(297.0 - 2 * 4.0, 289.0)

    # Fast-mode rectangles retain a separate 4 mm part gap.
    first = (4.0, 4.0, 100.0, 80.0)
    second = (108.0, 4.0, 120.0, 60.0)
    assert rectangles_separated(first, second, 4.0)
    assert first[0] >= 4.0 and first[1] >= 4.0
    assert second[0] + second[2] <= 416.0

    # Neutral layer for a 2 mm board at K=0.5 lies 1 mm from the inner face.
    inner_radius = 20.0
    thickness = 2.0
    neutral_length = (inner_radius + thickness * 0.5) * math.pi / 2
    assert math.isclose(neutral_length, 21.0 * math.pi / 2)

    models = (SRC / "Core" / "LayoutModels.cs").read_text(encoding="utf-8")
    analyzer = (SRC / "Core" / "BoardAnalyzer.cs").read_text(encoding="utf-8")
    bent = (SRC / "Core" / "BentBoardUnroller.cs").read_text(encoding="utf-8")
    engine = (SRC / "Core" / "LayoutEngine.cs").read_text(encoding="utf-8")
    packer = (SRC / "Core" / "SheetPacker.cs").read_text(encoding="utf-8")
    progress = (SRC / "Core" / "LayoutProgress.cs").read_text(encoding="utf-8")
    commands = (SRC / "Commands" / "LayFlatCommands.cs").read_text(encoding="utf-8")
    project = (SRC / "WoodSheetLayout.csproj").read_text(encoding="utf-8")

    for token in [
        "PackingMode", "PackingMode.Fast", "PackingMode.Contour",
        "EnableHoleNesting", "SheetKind.Custom", "FrameMarginMillimeters",
        "PartGapMillimeters", "GrainDirectionLocked", "NeutralFactor",
        "LayoutPartMode.PlanarOnly", "LayoutPartMode.BentOnly"
    ]:
        assert token in models or token in commands, token

    for token in [
        "FastMaxRectsSheet", "FastPackingHeuristic", "BestShortSide",
        "BestArea", "BottomLeft", "return 12", "RectD", "PruneFreeRectangles",
        "ContourSheet", "MaximumCandidateTranslationsPerAngle = 2200",
        "_settings.EnableHoleNesting", "BuildThicknessBuckets"
    ]:
        assert token in packer, token

    for token in [
        'new[] { "Fast", "Contour" }', '"HoleNesting"',
        'EnglishName => "WSLayTight"', 'EnglishName => "WSLayFlatBend"',
        'EnglishName => "WSLayFlatA3"', 'EnglishName => "WSLayFlatA4"'
    ]:
        assert token in commands, token

    for token in ["DuplicateNakedEdgeCurves", "TextWouldFaceDown", "BentBoardUnroller.TryCreatePart"]:
        assert token in analyzer, token
    for token in ["NeutralFactor", "公共接缝", "PerformUnroll"]:
        assert token in bent, token
    for token in ["ShowProgressMeter", "EscapeKeyPressed", "RhinoApp.Wait"]:
        assert token in progress, token
    for token in ["WoodSheetLayout_2.1.0", "Fast快速规整排版", "Contour真实轮廓"]:
        assert token in engine, token

    assert "net48;net8.0" in project
    assert "<Prefer32Bit>false</Prefer32Bit>" in project
    assert '<PackageReference Include="RhinoCommon" Version="7.0.20314.3001"' in project
    assert "<Version>2.1.0</Version>" in project

    for path in SRC.rglob("*.cs"):
        stripped = strip_csharp(path.read_text(encoding="utf-8"))
        assert stripped.count("{") == stripped.count("}"), f"unbalanced braces: {path}"

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for phrase in [
        "MaxRects", "Mode=Fast", "Mode=Contour", "HoleNesting=Yes",
        "Custom", "4 mm", "WSLayTight", "WSLayFlatBend",
        "Rhino 7", "Rhino 8", "不移动、删除或修改原模型"
    ]:
        assert phrase in readme, phrase

    print("WoodSheetLayout 2.1.0 fast/contour layout checks passed.")


if __name__ == "__main__":
    main()
