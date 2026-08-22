#!/usr/bin/env python3
"""Static and mathematical regression checks for ProductMotion Timeline 0.2."""

from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "ProductMotionTimeline"


def gear_angle(kind: str, driver_angle: float, driver_teeth: int, driven_teeth: int, phase: float = 0.0) -> float:
    sign = -1.0 if kind == "ExternalGear" else 1.0
    return phase + driver_angle * sign * driver_teeth / driven_teeth


def translation(x: float, y: float, z: float):
    return [
        [1.0, 0.0, 0.0, x],
        [0.0, 1.0, 0.0, y],
        [0.0, 0.0, 1.0, z],
        [0.0, 0.0, 0.0, 1.0],
    ]


def multiply(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def inverse_translation(m):
    return translation(-m[0][3], -m[1][3], -m[2][3])


def assert_balanced_csharp(path: Path):
    text = path.read_text(encoding="utf-8")
    stripped = re.sub(r'//.*?$|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"', "", text, flags=re.M | re.S)
    assert stripped.count("{") == stripped.count("}"), f"unbalanced braces: {path}"


def main():
    assert gear_angle("ExternalGear", 360.0, 20, 40) == -180.0
    assert gear_angle("InternalGear", 360.0, 20, 40) == 180.0
    assert gear_angle("Belt", -720.0, 30, 15, 10.0) == -1430.0

    # Parent moved +5; child own target remains +13, so inherited result is +18.
    parent_world = translation(15.0, 0.0, 0.0)
    parent_bind = translation(10.0, 0.0, 0.0)
    child_own = translation(13.0, 0.0, 0.0)
    child_world = multiply(multiply(parent_world, inverse_translation(parent_bind)), child_own)
    assert child_world[0][3] == 18.0

    data = (SRC / "Core" / "AnimationData.cs").read_text(encoding="utf-8")
    engine = (SRC / "Core" / "TimelineEngine.cs").read_text(encoding="utf-8")
    commands = (SRC / "Commands" / "TimelineCommands.cs").read_text(encoding="utf-8")
    project = (SRC / "ProductMotionTimeline.csproj").read_text(encoding="utf-8")
    repository = (SRC / "Core" / "TimelineRepository.cs").read_text(encoding="utf-8")

    required = [
        "ParentTrackId",
        "ParentBindTransform",
        "MechanicalConstraint",
        "ExternalGear",
        "InternalGear",
        "Belt",
        "WouldCreateParentCycle",
        "WouldCreateConstraintCycle",
    ]
    for token in required:
        assert token in data, token

    for token in ["EvaluateEffectivePose", "EvaluateWorldTarget", "AddMechanicalConstraint", "SetParent"]:
        assert token in engine, token

    command_names = re.findall(r'EnglishName\s*=>\s*"([^"]+)"', commands)
    assert len(command_names) == len(set(command_names)), "duplicate Rhino command names"
    for command in [
        "PMTAddGroupPart",
        "PMTSetParent",
        "PMTClearParent",
        "PMTBindMechanical",
        "PMTDeleteMechanical",
    ]:
        assert command in command_names, command

    assert "DataVersion = 3" in data
    assert "version < 2 || version > TimelineDocument.DataVersion" in repository
    assert "net48;net8.0" in project
    assert "<Version>0.2.0</Version>" in project

    for path in SRC.rglob("*.cs"):
        assert_balanced_csharp(path)

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for phrase in ["组内零件", "父子层级", "外啮合齿轮", "设计演示级运动学"]:
        assert phrase in readme, phrase

    print("ProductMotion Timeline 0.2 static/mathematical checks passed.")


if __name__ == "__main__":
    main()
