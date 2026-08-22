from pathlib import Path
import math


ROOT = Path(__file__).resolve().parents[1]
ENGINE = (ROOT / "src/WoodCheck/Core/WoodCheckEngine.cs").read_text(encoding="utf-8")
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


def is_u_slot(points, nominal=2.0, minimum_depth=4.0):
    a, b, c, d = points
    first = (b[0] - a[0], b[1] - a[1])
    bottom = (c[0] - b[0], c[1] - b[1])
    second = (d[0] - c[0], d[1] - c[1])

    def normalized(vector):
        length = math.hypot(*vector)
        return (vector[0] / length, vector[1] / length), length

    first_n, first_len = normalized(first)
    bottom_n, gap = normalized(bottom)
    second_n, second_len = normalized(second)
    dot_sides = sum(x * y for x, y in zip(first_n, second_n))
    square = abs(sum(x * y for x, y in zip(first_n, bottom_n))) < 0.20
    square &= abs(sum(x * y for x, y in zip(second_n, bottom_n))) < 0.20
    return (
        dot_sides < -0.93
        and square
        and nominal * 0.55 <= gap <= nominal * 1.75
        and min(first_len, second_len) < minimum_depth
    )


def test_contracts():
    required = [
        "Brep.CreateBooleanIntersection",
        "CheckSlots",
        "CheckWeakWalls",
        "CheckAxes",
        "CurvesEquivalent",
        "IsEngravingLayer",
    ]
    for token in required:
        assert token in ENGINE, token

    for command in ["WoodCheck", "WCCheckAll", "WCSettings", "WCLocate", "WCClearMarkers"]:
        assert f'"{command}"' in COMMANDS, command

    assert "net48;net8.0" in PROJECT
    assert "RhinoCommon\" Version=\"7.*" in PROJECT
    assert "RhinoCommon\" Version=\"8.*" in PROJECT
    assert "2.00 mm" in README and "Ø2.00 mm" in README


def test_axis_math():
    offset, span = axis_offset((0, 0, 0), (0, 0, 1), (0.12, 0, 20))
    assert abs(offset - 0.12) < 1e-9
    assert abs(span - 20.0) < 1e-9


def test_slot_rule():
    assert is_u_slot([(0, 0), (0, 3), (2.1, 3), (2.1, 0)])
    assert not is_u_slot([(0, 0), (0, 5), (2.1, 5), (2.1, 0)])


if __name__ == "__main__":
    test_contracts()
    test_axis_math()
    test_slot_rule()
    print("WoodCheck source and geometry rules verified.")
