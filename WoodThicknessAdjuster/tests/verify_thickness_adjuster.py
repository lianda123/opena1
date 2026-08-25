#!/usr/bin/env python3
"""Static checks for the Rhino 7/8 thickness-adjustment workflow."""

from pathlib import Path
import math
import re


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "WoodThicknessAdjuster"


def strip_csharp(text):
    return re.sub(r'//.*?$|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"', "", text, flags=re.M | re.S)


def main():
    presets = [1.5, 2.0, 2.5, 3.0, 4.0, 5.0]
    assert presets == sorted(presets)
    assert math.isclose(2.5 / 2.0, 1.25)
    assert math.isclose(1.5 / 3.0, 0.5)

    analyzer = (SRC / "Core" / "ThicknessAnalyzer.cs").read_text(encoding="utf-8")
    workflow = (SRC / "Core" / "ThicknessAdjustmentWorkflow.cs").read_text(encoding="utf-8")
    commands = (SRC / "Commands" / "ThicknessCommands.cs").read_text(encoding="utf-8")
    project = (SRC / "WoodThicknessAdjuster.csproj").read_text(encoding="utf-8")

    for token in [
        "AreaMassProperties.Compute", "TryGetPlane", "Vector3d.Multiply",
        "left.Area", "right.Area", "smallerArea", "ThicknessModelUnits",
        "Extrusion", "IsSolid", "Math.Cos(2.0 * Math.PI / 180.0)"
    ]:
        assert token in analyzer, token

    for token in [
        "RhinoMath.UnitScale", "UnitSystem.Millimeters", "reference.SelectionPoint()",
        "ThicknessAnchorMode.ClickedFace", "ThicknessAnchorMode.Center",
        "Transform.Scale(scalingPlane, 1.0, 1.0, factor)",
        "doc.Groups.GroupMembers", "ShouldFollowBoard", "DuplicateGeometry",
        "doc.Objects.Transform", "BeginUndoRecord", "EndUndoRecord",
        "TargetMillimeters", "保持点击面", "折弯板不会强制缩放"
    ]:
        assert token in workflow, token

    for command in [
        "WSAdjustThickness", "WSThickness15", "WSThickness20",
        "WSThickness25", "WSThickness30", "WSThickness40", "WSThickness50"
    ]:
        assert command in commands, command
    for token in ["1.5", "2.0", "2.5", "3.0", "4.0", "5.0", "CustomThickness"]:
        assert token in commands, token

    assert "net48;net8.0" in project
    assert '<PackageReference Include="RhinoCommon" Version="7.0.20314.3001"' in project
    assert '<Version>1.0.0</Version>' in project

    for path in SRC.rglob("*.cs"):
        stripped = strip_csharp(path.read_text(encoding="utf-8"))
        assert stripped.count("{") == stripped.count("}"), f"unbalanced braces: {path}"

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for token in ["保持点击面", "中心对称", "1.5mm", "2.5mm", "5mm", "Group"]:
        assert token in readme, token

    print("WoodThicknessAdjuster 1.0.0 workflow checks passed.")


if __name__ == "__main__":
    main()
