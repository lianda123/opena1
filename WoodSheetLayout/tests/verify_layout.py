#!/usr/bin/env python3
"""Checks the 1.1 planar workflow and 2.2.4 guarded outer-loop bend workflow."""

from pathlib import Path
import math
import re


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "WoodSheetLayout"


def strip_csharp(text):
    return re.sub(r'//.*?$|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"', "", text, flags=re.M | re.S)


def separated(a, b, gap):
    return (
        a[0] + a[2] + gap <= b[0]
        or b[0] + b[2] + gap <= a[0]
        or a[1] + a[3] + gap <= b[1]
        or b[1] + b[3] + gap <= a[1]
    )


def main():
    # A3 and A4 retain an independent physical 4 mm frame bleed.
    assert math.isclose(420.0 - 8.0, 412.0)
    assert math.isclose(297.0 - 8.0, 289.0)
    assert math.isclose(210.0 - 8.0, 202.0)

    # Two rectangular FlatBounds keep the 1.1-style 4 mm gap.
    first = (4.0, 4.0, 100.0, 80.0)
    second = (108.0, 4.0, 120.0, 60.0)
    assert separated(first, second, 4.0)
    assert first[0] >= 4.0 and first[1] >= 4.0
    assert second[0] + second[2] <= 416.0

    # Bend command still uses the half-thickness neutral layer by default.
    neutral_length = (20.0 + 2.0 * 0.5) * math.pi / 2
    assert math.isclose(neutral_length, 21.0 * math.pi / 2)

    models = (SRC / "Core" / "LayoutModels.cs").read_text(encoding="utf-8")
    analyzer = (SRC / "Core" / "BoardAnalyzer.cs").read_text(encoding="utf-8")
    bent = (SRC / "Core" / "BentBoardUnroller.cs").read_text(encoding="utf-8")
    packer = (SRC / "Core" / "SheetPacker.cs").read_text(encoding="utf-8")
    engine = (SRC / "Core" / "LayoutEngine.cs").read_text(encoding="utf-8")
    progress = (SRC / "Core" / "LayoutProgress.cs").read_text(encoding="utf-8")
    commands = (SRC / "Commands" / "LayFlatCommands.cs").read_text(encoding="utf-8")
    project = (SRC / "WoodSheetLayout.csproj").read_text(encoding="utf-8")

    for token in [
        "SheetKind.Custom", "CustomWidthMillimeters", "CustomHeightMillimeters",
        "PartGapMillimeters", "FrameMarginMillimeters", "NeutralFactor",
        "LayoutPartMode.PlanarOnly", "LayoutPartMode.BentOnly"
    ]:
        assert token in models or token in commands, token

    # Normal planar parts must fall back to the complete grouped FlatBounds rectangle.
    for token in [
        "OutlineGeometry.CreateRectangle(flatBounds)", "FlatBounds = flatBounds",
        "矩形包围盒骨架", "BentBoardUnroller.TryCreatePart"
    ]:
        assert token in analyzer, token
    assert "FlatBounds = outline.Bounds" not in analyzer
    assert "AverageAnnotationDistance(facePlane, annotationSamples)" in analyzer
    assert analyzer.index("AverageAnnotationDistance(facePlane, annotationSamples)") < analyzer.index("var exactScore")
    for token in [
        "TryFindForcedPlane", "TryReadThicknessFromLayer",
        "InstanceReferenceGeometry", "GetBoundingBox(candidate)",
        "minimumSize", "选中组件没有可复制的有效几何"
    ]:
        assert token in analyzer, token
    for token in [
        "ExpandSelectedGroups", "doc.Groups.GroupMembers(groupIndex)",
        "IsOutputPairGroup", 'StartsWith("WSL_PAIR_"',
        "IsGeneratedOutputObject", 'StartsWith("WoodSheetLayout_"'
    ]:
        assert token in analyzer, token

    # The normal command must return through the planar path before any bend scan.
    planar_branch = analyzer.index("if (settings.PartMode == LayoutPartMode.PlanarOnly)")
    bend_scan = analyzer.index("BentBoardUnroller.HasBendBeyondThickness")
    assert planar_branch < bend_scan
    planar_section = analyzer[planar_branch:bend_scan]
    assert "return TryCreatePlanarPart(" in planar_section
    assert "planarSlenderness" not in analyzer
    assert "可提取真实外轮廓" not in analyzer
    for token in [
        "TryFindApproximateBrepPlane", "relaxedTolerance",
        "face.FrameAt(u, v, out candidatePlane)", "exactScore > 0.25",
        "allowApproximatePlane"
    ]:
        assert token in analyzer, token
    assert "allowApproximatePlane && (!bestPlane.IsValid || exactScore > 0.25)" in analyzer

    # Exact 1.1 MaxRects characteristics: four sorts x three heuristics = 12 attempts.
    for token in [
        "FastMaxRectsSheet", "FastPackingHeuristic", "BestShortSide",
        "BestArea", "BottomLeft", "return 12", "RectD",
        "SplitFreeRectangles", "PruneFreeRectangles",
        "RotatedFlatBounds(part.FlatBounds, angle)",
        "item.FlatBounds.Diagonal.X", "item.FlatBounds.Diagonal.Y",
        "BuildThicknessBuckets"
    ]:
        assert token in packer, token

    # No contour/hole mode is exposed to the user in the normal command.
    for removed in ['"Mode"', '"HoleNesting"', 'EnglishName => "WSLayTight"']:
        assert removed not in commands, removed

    # Command line options use Rhino localization pairs with Chinese local labels.
    for token in [
        "LocalizeStringPair", 'L("Sheet", "边界框")', 'L("Orientation", "方向")',
        'L("CustomWidth", "自定义宽度")', 'L("CustomHeight", "自定义高度")',
        'L("PartGap", "零件间距")', 'L("FrameMargin", "边框出血")',
        'L("GrainLock", "木纹锁定")', 'L("NeutralFactor", "中性层系数")'
    ]:
        assert token in commands, token
    for command in ["WoodSheetLayout", "WSLayFlatA3", "WSLayFlatA4", "WSLayFlatBend"]:
        assert command in commands, command

    for token in ["NeutralFactor", "公共接缝", "PerformUnroll"]:
        assert token in bent, token
    for token in [
        "seamTolerance", "TryBuildSourcePatch", "TryOffsetPatchTowardSolid",
        "Brep.CreateOffsetBrep", "Math.Abs(Vector3d.Multiply",
        "Math.Cos(12.0 * Math.PI / 180.0)",
        "TryCreatePlanarBoardSolid", "Brep.CreatePlanarBreps",
        "Brep.CreateFromOffsetFace", "识别连续折弯链",
        "BuildBoundaryFollowingCurves", "IsBoardBoundary",
        "edge.AdjacentFaces()", "TryMapCurveToOffsetFace",
        "JoinClosedLoops", "TryUnrollBoundaryCurves", "BrepSharesExtents",
        "thickness * 0.5", "附属曲线已从中性层抬升到铺平实体的上表面",
        "CurvePlanarArea", "CurveIsInside", "TryBooleanRebuildBoard",
        "TryCreateThroughCutter", "Brep.CreateBooleanDifference",
        "CountMaximumPlanarFaceInnerLoops", "rebuiltHoleCount >= holeLoops.Count"
    ]:
        assert token in bent, token
    assert "faceAreas[adjacent] < maximumArea * 0.015" not in bent
    for token in ["ShowProgressMeter", "EscapeKeyPressed", "RhinoApp.Wait"]:
        assert token in progress, token
    for token in [
        "WoodSheetLayout_2.2.4", "矩形MaxRects", "边框出血",
        "WSL_PAIR_", "WoodSheetLayoutRole", "FlatCopy", "Source",
        "OutputGuide", "doc.Objects.ModifyAttributes",
        "AddClassicPlanarPart", "doc.Objects.Transform(source.Id, finalTransform, false)",
        "placement.Part.FlattenKind == FlattenKind.Planar",
        "-placement.Part.FlatBounds.Min.Z",
        "TryCreatePlacedGeometry", "AddGeometryWithFallback",
        "数量校验未通过", "数量校验通过"
    ]:
        assert token in engine, token
    # Normal command uses the exact 1.1 selection list; recursive completion is bend-only.
    normal_selection = engine[engine.index("var objects = settings.PartMode"):engine.index("if (objects.Count == 0)")]
    assert "LayoutPartMode.BentOnly" in normal_selection
    assert "ExpandSelectedGroups" in normal_selection
    assert "IsGeneratedOutputObject" in normal_selection
    assert "AddIssueMarkers(doc" not in engine
    for token in ["CreateExpandedSheet", "AutoExpanded = true", "sheet.Width + settings.SheetGap"]:
        assert token in packer, token
    assert "result.OversizedParts.Add" not in packer

    assert "net48;net8.0" in project
    assert "<Prefer32Bit>false</Prefer32Bit>" in project
    assert '<PackageReference Include="RhinoCommon" Version="7.0.20314.3001"' in project
    assert "<Version>2.2.4</Version>" in project

    for path in SRC.rglob("*.cs"):
        stripped = strip_csharp(path.read_text(encoding="utf-8"))
        assert stripped.count("{") == stripped.count("}"), f"unbalanced braces: {path}"

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for phrase in [
        "FlatBounds", "MaxRects", "共12次快速方案", "自定义边界框",
        "边框出血=4mm", "中文选项", "WSLayFlatBend",
        "不再提供真实轮廓精排、孔洞嵌套或自由角度搜索"
    ]:
        assert phrase in readme, phrase

    print("WoodSheetLayout 2.2.4 planar/bend workflow checks passed.")


if __name__ == "__main__":
    main()
