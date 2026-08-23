from pathlib import Path
import math


ROOT = Path(__file__).resolve().parents[1]
ANALYZER = (ROOT / "src/ExplodeBook/Core/AssemblyAnalyzer.cs").read_text(encoding="utf-8")
DRAWING = (ROOT / "src/ExplodeBook/Core/DrawingBuilder.cs").read_text(encoding="utf-8")
PAGES = (ROOT / "src/ExplodeBook/Core/ManualPageBuilder.cs").read_text(encoding="utf-8")
COMMANDS = (ROOT / "src/ExplodeBook/Commands/ExplodeBookCommands.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "src/ExplodeBook/ExplodeBook.csproj").read_text(encoding="utf-8")
README = (ROOT / "README.md").read_text(encoding="utf-8")


def axis_gap(a_min, a_max, b_min, b_max):
    if a_max < b_min:
        return b_min - a_max
    if b_max < a_min:
        return a_min - b_max
    return 0.0


def box_distance(left, right):
    gaps = [axis_gap(left[i], left[i + 3], right[i], right[i + 3]) for i in range(3)]
    return math.sqrt(sum(value * value for value in gaps))


def test_contracts():
    for token in [
        "BuildGroupedComponents",
        "BoxDistance",
        "WoodExport.PartNumber",
        "SetManualOrder",
        "ClearManualOrder",
    ]:
        assert token in ANALYZER, token

    for token in ["CreateExplodedOverview", "AddArrow", "AddNumberBubble", "ExplosionOffset"]:
        assert token in DRAWING, token

    for token in ["AddPageView", "AddDetailView", "ZoomBoundingBox", "IsProjectionLocked", "A4"]:
        assert token in PAGES or token in README, token

    for command in [
        "ExplodeBook", "EBExplode", "EBPages", "EBSetOrder",
        "EBAutoOrder", "EBSettings", "EBClear", "EBHelp",
    ]:
        assert f'"{command}"' in COMMANDS, command

    assert "net48;net8.0" in PROJECT
    assert 'RhinoCommon" Version="7.*' in PROJECT
    assert 'RhinoCommon" Version="8.*' in PROJECT
    assert "A4" in README and "A3" in README and "PDF" in README


def test_box_distance():
    left = (0, 0, 0, 10, 10, 2)
    touching = (10, 2, 0, 15, 8, 2)
    separated = (13, 14, 2, 20, 20, 4)
    assert box_distance(left, touching) == 0.0
    assert abs(box_distance(left, separated) - 5.0) < 1e-9


if __name__ == "__main__":
    test_contracts()
    test_box_distance()
    print("ExplodeBook source and ordering rules verified.")
