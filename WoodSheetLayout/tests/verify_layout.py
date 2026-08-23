#!/usr/bin/env python3
"""Static and mathematical checks for Wood Sheet Layout 2.0."""

from pathlib import Path
import math
import re


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "WoodSheetLayout"


def polygon_area(points):
    return abs(sum(
        points[i][0] * points[i + 1][1] - points[i + 1][0] * points[i][1]
        for i in range(len(points) - 1)
    )) * 0.5


def point_in_polygon(polygon, point):
    inside = False
    for i in range(len(polygon) - 1):
        a, b = polygon[i], polygon[i + 1]
        if (a[1] > point[1]) == (b[1] > point[1]):
            continue
        x = (b[0] - a[0]) * (point[1] - a[1]) / (b[1] - a[1]) + a[0]
        if point[0] < x:
            inside = not inside
    return inside


def strip_csharp(text):
    return re.sub(r'//.*?$|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"', "", text, flags=re.M | re.S)


def main():
    # A3 landscape with 4 mm frame bleed leaves 412 x 289 mm.
    assert math.isclose(420.0 - 2 * 4.0, 412.0)
    assert math.isclose(297.0 - 2 * 4.0, 289.0)

    # True contour utilization subtracts a hole, unlike a rectangular bounding box.
    outer = [(0, 0), (100, 0), (100, 80), (0, 80), (0, 0)]
    hole = [(20, 20), (80, 20), (80, 60), (20, 60), (20, 20)]
    net_area = polygon_area(outer) - polygon_area(hole)
    assert math.isclose(net_area, 5600.0)
    assert point_in_polygon(hole, (50, 40))
    assert not point_in_polygon(hole, (10, 10))

    # Neutral-layer formula: a 2 mm board at K=0.5 unfolds on radius Rin+1 mm.
    inner_radius = 20.0
    thickness = 2.0
    angle = math.pi / 2
    neutral_length = (inner_radius + thickness * 0.5) * angle
    assert math.isclose(neutral_length, 21.0 * math.pi / 2)

    models = (SRC / "Core" / "LayoutModels.cs").read_text(encoding="utf-8")
    analyzer = (SRC / "Core" / "BoardAnalyzer.cs").read_text(encoding="utf-8")
    bent = (SRC / "Core" / "BentBoardUnroller.cs").read_text(encoding="utf-8")
    outline = (SRC / "Core" / "OutlineGeometry.cs").read_text(encoding="utf-8")
    engine = (SRC / "Core" / "LayoutEngine.cs").read_text(encoding="utf-8")
    packer = (SRC / "Core" / "SheetPacker.cs").read_text(encoding="utf-8")
    commands = (SRC / "Commands" / "LayFlatCommands.cs").read_text(encoding="utf-8")
    project = (SRC / "WoodSheetLayout.csproj").read_text(encoding="utf-8")

    for token in [
        "SheetKind.Custom", "CustomWidthMillimeters", "CustomHeightMillimeters",
        "PartGapMillimeters", "FrameMarginMillimeters", "NeutralFactor",
        "RotationMode", "Free", "GrainDirectionLocked"
    ]:
        assert token in models or token in commands, token
    for token in ["DuplicateNakedEdgeCurves", "TextWouldFaceDown", "BentBoardUnroller.TryCreatePart"]:
        assert token in analyzer, token
    for token in [
        "CreateFromOffsetFace", "PerformUnroll", "Pullback", "Pushup",
        "NeutralFactor", "面积变形", "TextFacesAgainstPatch"
    ]:
        assert token in bent, token
    for token in ["PointInRegion", "BoundaryDistanceLessThan", "IsNestedInsideHole", "NetArea"]:
        assert token in outline, token
    for token in ["CandidateTranslations", "NestedInsideHole", "RotationAnglesRadians", "BuildThicknessBuckets"]:
        assert token in packer, token
    for token in ["WoodSheetLayout_2.0", "真实轮廓利用率", "问题标记_黄色", "未排入"]:
        assert token in engine, token
    for command in ["WoodSheetLayout", "WSLayFlatA3", "WSLayFlatA4"]:
        assert command in commands, command
    assert "net48;net8.0" in project
    assert "<Version>2.0.0</Version>" in project

    for path in SRC.rglob("*.cs"):
        stripped = strip_csharp(path.read_text(encoding="utf-8"))
        assert stripped.count("{") == stripped.count("}"), f"unbalanced braces: {path}"

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for phrase in [
        "真实外轮廓", "孔洞", "Custom", "4 mm", "NeutralFactor",
        "折弯", "Rhino 7", "Rhino 8", "不修改原模型"
    ]:
        assert phrase in readme, phrase
    print("Wood Sheet Layout 2.0 static/mathematical checks passed.")


if __name__ == "__main__":
    main()
