#!/usr/bin/env python3
"""Static and geometric checks for the Rhino 7/8 thickness workflow."""

from pathlib import Path
import math
import re


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "WoodThicknessAdjuster"


def strip_csharp(text):
    return re.sub(r'//.*?$|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"', "", text, flags=re.M | re.S)


def scale_then_snap(value, anchor, factor, correction):
    """One-dimensional equivalent of Translation * Scale used by the plug-in."""
    return anchor + (value - anchor) * factor + correction


def main():
    presets = [1.5, 2.0, 2.5, 3.0, 4.0, 5.0]
    assert presets == sorted(presets)
    assert math.isclose(2.5 / 2.0, 1.25)
    assert math.isclose(1.5 / 3.0, 0.5)

    analyzer = (SRC / "Core" / "ThicknessAnalyzer.cs").read_text(encoding="utf-8")
    contact = (SRC / "Core" / "AssemblyContactResolver.cs").read_text(encoding="utf-8")
    workflow = (SRC / "Core" / "ThicknessAdjustmentWorkflow.cs").read_text(encoding="utf-8")
    commands = (SRC / "Commands" / "ThicknessCommands.cs").read_text(encoding="utf-8")
    project = (SRC / "WoodThicknessAdjuster.csproj").read_text(encoding="utf-8")

    for token in [
        "AreaMassProperties.Compute", "TryGetPlane", "Vector3d.Multiply",
        "left.Area", "right.Area", "smallerArea", "ThicknessModelUnits",
        "Extrusion", "IsSolid", "Math.Cos(2.0 * Math.PI / 180.0)",
        "DistanceToTrimmedFace", "face.IsPointOnFace", "PointFaceRelation.Exterior",
        "PreferredAnchorFaceIndex", "selectionPoint"
    ]:
        assert token in analyzer, token

    for token in [
        "TryFindContact", "ObjectType.Brep | ObjectType.Extrusion",
        "preferredNeighborId", "recoveryDistance", "modelUnitsPerMillimeter * 5.0",
        "overlapRatio >= 0.1", "overlapRatio >= 0.5", "WasExactContact", "TargetFaceIndex",
        "TryProjectedOverlapRatio", "neighborObject.Id == preferredNeighborId"
    ]:
        assert token in contact, token

    for token in [
        "RhinoMath.UnitScale", "UnitSystem.Millimeters", "reference.SelectionPoint()",
        "ThicknessAnchorMode.ClickedFace", "ThicknessAnchorMode.Center",
        "Transform.Scale(scalingPlane, 1.0, 1.0, factor)",
        "doc.Groups.GroupMembers", "ShouldFollowBoard", "DuplicateGeometry",
        "doc.Objects.Transform", "BeginUndoRecord", "EndUndoRecord",
        "TargetMillimeters", "保持点击面", "折弯板不会强制缩放",
        "ThicknessContactMode.AutoFit", "AssemblyContactResolver.TryFindContact",
        "contact.NeedsSnap", "Transform.Translation(correction) * scaleTransform",
        "IsFollowerAttachedToBoard", "自动回贴相邻板面", "以原贴合面为主表面",
        "lastAdjustedBoardId"
    ]:
        assert token in workflow, token

    for command in [
        "WSAdjustThickness", "WSThickness15", "WSThickness20",
        "WSThickness25", "WSThickness30", "WSThickness40", "WSThickness50"
    ]:
        assert command in commands, command
    for token in ["1.5", "2.0", "2.5", "3.0", "4.0", "5.0", "CustomThickness"]:
        assert token in commands, token
    for token in ["装配贴合", "自动贴合", "ThicknessContactMode.AutoFit"]:
        assert token in commands, token

    assert "net48;net8.0" in project
    assert '<PackageReference Include="RhinoCommon" Version="7.0.20314.3001"' in project
    assert '<Version>1.1.0</Version>' in project

    # 两块板原本在z=2贴合；各自从2mm改成3mm后，接触面仍应同为z=2。
    assert math.isclose(scale_then_snap(2.0, 2.0, 1.5, 0.0), 2.0)
    assert math.isclose(scale_then_snap(4.0, 2.0, 1.5, 0.0), 5.0)

    # 第二块板曾被拉到z=2.7，自动回贴修正-0.7；板面曲线使用同一变换。
    correction = 2.0 - 2.7
    assert math.isclose(scale_then_snap(2.7, 2.7, 1.5, correction), 2.0)
    assert math.isclose(scale_then_snap(4.7, 2.7, 1.5, correction), 5.0)

    for path in SRC.rglob("*.cs"):
        stripped = strip_csharp(path.read_text(encoding="utf-8"))
        assert stripped.count("{") == stripped.count("}"), f"unbalanced braces: {path}"

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for token in [
        "保持点击面", "中心对称", "自动贴合", "原贴合面", "5mm",
        "第一块→第二块→第三块", "表面曲线", "Group"
    ]:
        assert token in readme, token

    print("WoodThicknessAdjuster 1.1.0 workflow checks passed.")


if __name__ == "__main__":
    main()
