#!/usr/bin/env python3
"""Static and mathematical regression checks for ProductMotion Timeline 0.4.4."""

from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "ProductMotionTimeline"


def gear_angle(kind: str, driver_angle: float, driver_teeth: int, driven_teeth: int, phase: float = 0.0) -> float:
    sign = -1.0 if kind == "ExternalGear" else 1.0
    return phase + driver_angle * sign * driver_teeth / driven_teeth


def smooth_step(t: float) -> float:
    return t * t * (3.0 - 2.0 * t)


def gear_center_distance(kind: str, module: float, driver_teeth: int, driven_teeth: int) -> float:
    tooth_term = driver_teeth + driven_teeth if kind == "ExternalGear" else abs(driven_teeth - driver_teeth)
    return module * tooth_term * 0.5


def crank_slider_x(theta: float, radius: float, rod: float) -> float:
    import math
    return radius * math.cos(theta) + math.sqrt(rod * rod - radius * radius * math.sin(theta) ** 2)


def rack_travel(driver_angle: float, module: float, driver_teeth: int) -> float:
    import math
    return driver_angle / 360.0 * math.pi * module * driver_teeth


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
    assert smooth_step(0.25) == 0.15625
    assert smooth_step(0.25) != 0.25
    assert gear_center_distance("ExternalGear", 2.0, 10, 30) == 40.0
    assert gear_center_distance("InternalGear", 2.0, 10, 30) == 20.0
    assert crank_slider_x(0.0, 10.0, 30.0) == 40.0
    assert abs(rack_travel(360.0, 2.0, 20) - 40.0 * 3.141592653589793) < 1e-9

    # Parent moved +5; child own target remains +13, so inherited result is +18.
    parent_world = translation(15.0, 0.0, 0.0)
    parent_bind = translation(10.0, 0.0, 0.0)
    child_own = translation(13.0, 0.0, 0.0)
    child_world = multiply(multiply(parent_world, inverse_translation(parent_bind)), child_own)
    assert child_world[0][3] == 18.0

    data = (SRC / "Core" / "AnimationData.cs").read_text(encoding="utf-8")
    engine = (SRC / "Core" / "TimelineEngine.cs").read_text(encoding="utf-8")
    commands = "\n".join(path.read_text(encoding="utf-8") for path in (SRC / "Commands").glob("*.cs"))
    project = (SRC / "ProductMotionTimeline.csproj").read_text(encoding="utf-8")
    repository = (SRC / "Core" / "TimelineRepository.cs").read_text(encoding="utf-8")
    animation_math = (SRC / "Core" / "AnimationMath.cs").read_text(encoding="utf-8")
    panel = (SRC / "UI" / "TimelinePanel.cs").read_text(encoding="utf-8")
    canvas = (SRC / "UI" / "TimelineCanvas.cs").read_text(encoding="utf-8")
    axis_detector = (SRC / "Core" / "AxisDetector.cs").read_text(encoding="utf-8")
    templates = (SRC / "Core" / "MotionTemplateGenerator.cs").read_text(encoding="utf-8")
    conduit = (SRC / "UI" / "MechanicalConstraintConduit.cs").read_text(encoding="utf-8")
    gear_geometry = (SRC / "Core" / "GearGeometryGenerator.cs").read_text(encoding="utf-8")
    gear_metadata = (SRC / "Core" / "GearPartMetadata.cs").read_text(encoding="utf-8")

    required = [
        "ParentTrackId",
        "ParentBindTransform",
        "MechanicalConstraint",
        "ExternalGear",
        "InternalGear",
        "Belt",
        "HelicalGear", "BevelGear", "RackPinion", "TemplatePlacementMode",
        "WouldCreateParentCycle",
        "WouldCreateConstraintCycle",
    ]
    for token in required:
        assert token in data, token

    for token in [
        "EvaluateEffectivePose", "EvaluateWorldTarget", "AddMechanicalConstraint",
        "SetParent", "EffectiveMechanicalAngle", "UpdateCurrentKeyInterpolation",
        "TemplateStartFrame", "UpdateTemplatePlacement", "ReorderTrack"
    ]:
        assert token in engine, token

    for token in ["ExtractAxisRotationDegrees", "MechanicalAngleDegrees", "SmoothStep"]:
        assert token in animation_math, token

    command_names = re.findall(r'EnglishName\s*=>\s*"([^"]+)"', commands)
    assert len(command_names) == len(set(command_names)), "duplicate Rhino command names"
    for command in [
        "PMTAddGroupPart",
        "PMTSetParent",
        "PMTClearParent",
        "PMTBindMechanical",
        "PMTDeleteMechanical",
        "PMTExternalGear",
        "PMTInternalGear",
        "PMTBelt", "PMTAutoPivot", "PMTEditMechanical", "PMTValidateMechanical",
        "PMTReciprocate", "PMTRebound", "PMTCrankSlider", "PMTFourBar", "PMTBindMultiple",
        "PMTGearFactory", "PMTCreateSpurGear", "PMTCreateInternalGear",
        "PMTCreateHelicalGear", "PMTCreateBevelGear", "PMTCreateRack",
    ]:
        assert command in command_names, command

    for token in [
        "普通Gumball绕轴旋转", "automaticPhase", "QuickMechanicalBinding",
        "选择主动件", "选择从动件"
    ]:
        assert token in commands, token

    for token in [
        "平滑：缓入缓出", "线性：匀速", "阶梯：保持后跳变",
        "SelectedIndexChanged", "PMTExternalGear", "PMTInternalGear", "PMTBelt",
        "PMTBindMultiple", "PMTGearFactory", "接在全部动作末尾"
    ]:
        assert token in panel, token
    for token in ["_smoothSegmentPen", "_linearSegmentPen", "_constantSegmentPen"]:
        assert token in canvas, token
    for token in [
        "_rowDragTrackId", "DrawTrackDropIndicator", "UpdateTrackDrop",
        "轨道（上下拖动）", "_trackDropPen"
    ]:
        assert token in canvas, token
    assert "拖动左侧轨道名可上下排序" in panel

    for token in ["TryDetect", "TryGetCircle", "IsCoaxial", "MatchingCircularEdges"]:
        assert token in axis_detector, token
    for token in ["GenerateReciprocation", "GenerateRebound", "GenerateCrankSlider", "GenerateFourBar"]:
        assert token in templates, token
    for token in ["DrawLine", "DrawDot", "SignedRatio"]:
        assert token in conduit, token
    for token in ["Module", "PressureAngleDegrees", "ExpectedCenterDistance", "ValidateMechanicalConstraint"]:
        assert token in data + engine, token
    for token in [
        "CreateExternalOutline", "CreateInternalBoundary", "CreateHelicalSolid",
        "CreateBevelSolid", "CreateRackOutline", "Brep.CreateBooleanDifference",
        "CreateGearGeometry", "CleanClosedPolyline", "回退输出闭合齿形曲线",
        "CreatePitchReference", "LineCurve"
    ]:
        assert token in gear_geometry, token
    for token in ["GearPartType", "InferConstraintType", "HelixAngleDegrees", "RackLength", "OutputPitchReference"]:
        assert token in gear_metadata, token
    for token in [
        'LocalizeStringPair("Auto", "自动识别")',
        'LocalizeStringPair("ExternalGear", "外啮合齿轮")',
        'LocalizeStringPair("RackPinion", "齿轮齿条传动")',
        'LocalizeStringPair("Reverse", "反向")'
    ]:
        assert token in commands, token
    assert "已附带青色分度圆/分度线" in commands
    assert "分度圆或分度线（辅助）" in (SRC / "Core" / "TrackFactory.cs").read_text(encoding="utf-8")
    assert "driver?.Type == GearPartType.Internal || driven?.Type == GearPartType.Internal" in gear_metadata
    assert "当前版本只支持齿轮驱动齿条" in commands
    assert "内齿圈齿数必须大于配对的外齿轮" in engine
    assert "ConstraintsForDriver" in data
    assert "EvaluateRackDistance" in data

    assert "DataVersion = 5" in data
    assert "version < 2 || version > TimelineDocument.DataVersion" in repository
    assert "net48;net8.0" in project
    assert "<Version>0.4.4</Version>" in project

    for path in SRC.rglob("*.cs"):
        assert_balanced_csharp(path)

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for phrase in [
        "组内零件", "父子层级", "外啮合齿轮", "几何/运动学校验",
        "Gumball", "缓入缓出", "保持后跳变",
        "一主多从机构网络", "动作自动衔接", "齿轮生成器合并",
        "渐开线直齿", "斜齿", "锥齿", "齿条"
    ]:
        assert phrase in readme, phrase

    print("ProductMotion Timeline 0.4.4 static/mathematical checks passed.")


if __name__ == "__main__":
    main()
