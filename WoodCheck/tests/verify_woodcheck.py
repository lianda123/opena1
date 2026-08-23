from pathlib import Path
import math


ROOT = Path(__file__).resolve().parents[1]
ENGINE = (ROOT / "src/WoodCheck/Core/WoodCheckEngine.cs").read_text(encoding="utf-8")
MODELS = (ROOT / "src/WoodCheck/Core/CheckModels.cs").read_text(encoding="utf-8")
MARKERS = (ROOT / "src/WoodCheck/Core/MarkerManager.cs").read_text(encoding="utf-8")
COMMANDS = (ROOT / "src/WoodCheck/Commands/WoodCheckCommands.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "src/WoodCheck/WoodCheck.csproj").read_text(encoding="utf-8")
README = (ROOT / "README.md").read_text(encoding="utf-8")


def axis_offset(left_center, axis, right_center):
    length = math.sqrt(sum(value * value for value in axis))
    unit = tuple(value / length for value in axis)
    delta = tuple(r - l for l, r in zip(left_center, right_center))
    along = sum(d * a for d, a in zip(delta, unit))
    perpendicular = tuple(d - a * along for d, a in zip(delta, unit))
    return math.sqrt(sum(value * value for value in perpendicular)), abs(along)


def test_contracts():
    for token in [
        "Brep.CreateBooleanIntersection",
        "VolumeMassProperties.Compute",
        "CheckCollisions",
        "CheckAxes",
        "CheckDuplicateCurves",
        "CurvesEquivalent",
    ]:
        assert token in ENGINE, token

    for removed in ["CheckSlots", "CheckWeakWalls", "CheckOpenCurves", "OpenCurve"]:
        assert removed not in ENGINE, removed

    for scope in ["Collision = 1", "Axis = 2", "DuplicateCurve = 4"]:
        assert scope in MODELS, scope

    for command in [
        "WoodCheck", "WCC", "WCA", "WCD", "WCCheckAll",
        "WCSettings", "WCLocate", "WCClearMarkers",
    ]:
        assert f'"{command}"' in COMMANDS, command

    for marker in ["WoodCheck_错误", "WoodCheck_警告", "WoodCheck_提示"]:
        assert marker in MARKERS, marker

    assert "net48;net8.0" in PROJECT
    assert 'RhinoCommon" Version="7.*' in PROJECT
    assert 'RhinoCommon" Version="8.*' in PROJECT
    assert "1.1.0" in PROJECT
    assert "不修改原有打组、图层、对象颜色和图层颜色" in README


def test_axis_math():
    offset, span = axis_offset((0, 0, 0), (0, 0, 1), (0.12, 0, 20))
    assert abs(offset - 0.12) < 1e-9
    assert abs(span - 20.0) < 1e-9


if __name__ == "__main__":
    test_contracts()
    test_axis_math()
    print("WoodCheck 1.1 simplified checks verified.")
