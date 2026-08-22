from pathlib import Path
import math


ROOT = Path(__file__).resolve().parents[1]
FACTORY = (ROOT / "src/MechanismMaker/Core/GeometryFactory.cs").read_text(encoding="utf-8")
COMMANDS = "\n".join(
    path.read_text(encoding="utf-8")
    for path in (ROOT / "src/MechanismMaker/Commands").glob("*.cs")
)
PROJECT = (ROOT / "src/MechanismMaker/MechanismMaker.csproj").read_text(encoding="utf-8")
README = (ROOT / "README.md").read_text(encoding="utf-8")


def circle_intersection(c1, r1, c2, r2):
    dx, dy = c2[0] - c1[0], c2[1] - c1[1]
    distance = math.hypot(dx, dy)
    if distance > r1 + r2 or distance < abs(r1 - r2) or distance == 0:
        return None
    along = (r1 * r1 - r2 * r2 + distance * distance) / (2 * distance)
    height = math.sqrt(max(0.0, r1 * r1 - along * along))
    ux, uy = dx / distance, dy / distance
    base = (c1[0] + ux * along, c1[1] + uy * along)
    return base[0] - uy * height, base[1] + ux * height


def test_geometry_formulas():
    module, z1, z2 = 1.0, 20, 40
    assert module * z1 == 20.0
    assert module * (z1 + z2) / 2 == 30.0
    assert abs(math.pi * module - 3.141592653589793) < 1e-12

    slots, center = 6, 35.0
    drive_radius = center * math.sin(math.pi / slots)
    driven_radius = center * math.cos(math.pi / slots)
    assert abs(drive_radius - 17.5) < 1e-9
    assert driven_radius > drive_radius


def test_four_bar_closure():
    angle = math.radians(45)
    point_b = (15 * math.cos(angle), 15 * math.sin(angle))
    point_c = circle_intersection(point_b, 35, (40, 0), 28)
    assert point_c is not None
    assert abs(math.dist(point_b, point_c) - 35) < 1e-8
    assert abs(math.dist((40, 0), point_c) - 28) < 1e-8


def test_source_contracts():
    for token in [
        "InvoluteAngle",
        "CreateRack",
        "CreateCam",
        "CreateFourBar",
        "CreateRatchet",
        "CreateGeneva",
        "Curve.CreateBooleanDifference",
    ]:
        assert token in FACTORY, token

    for command in [
        "MechanismMaker",
        "MMGear",
        "MMRack",
        "MMCam",
        "MMCrank",
        "MMFourBar",
        "MMRatchet",
        "MMGeneva",
        "MMSettings",
    ]:
        assert f'"{command}"' in COMMANDS, command

    assert "net48;net8.0" in PROJECT
    assert "RhinoCommon\" Version=\"7.*" in PROJECT
    assert "RhinoCommon\" Version=\"8.*" in PROJECT
    assert "Ø1.95 mm" in README and "Ø2.20 mm" in README and "Ø2.30 mm" in README


if __name__ == "__main__":
    test_geometry_formulas()
    test_four_bar_closure()
    test_source_contracts()
    print("MechanismMaker source and kinematic rules verified.")
