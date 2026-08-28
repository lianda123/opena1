from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
source = root / "src" / "WoodJointPro"

def read(path):
    return path.read_text(encoding="utf-8")

csproj = read(source / "WoodJointPro.csproj")
models = read(source / "Core" / "JointModels.cs")
builder = read(source / "Core" / "JointGeometryBuilder.cs")
updater = read(source / "Core" / "JointDocumentUpdater.cs")
commands = read(source / "Commands" / "JointCommands.cs")
aux = read(source / "Commands" / "AuxiliaryCommands.cs")
manifest = read(root / "manifest.yml")
workflow = read(root.parent / ".github" / "workflows" / "build-wood-joint-pro.yml")

assert "net48;net8.0" in csproj
assert "<Version>1.0.0</Version>" in csproj
assert "version: 1.0.0" in manifest
assert "woodjoint-pro-1.0.0" in workflow

for kind in ["CrossSlot", "TSlot", "TabSlot", "Snap", "Finger"]:
    assert kind in models and kind in builder

for command in [
    "WJPJoint", "WJPCrossSlot", "WJPTSlot", "WJPTabSlot", "WJPSnap",
    "WJPFingerJoint", "WJPAxisHole", "WJPCalibrationTest", "WJPSettings",
    "WJPLinkFlat", "WJPUpdateFlat"
]:
    assert command in commands + aux

assert "Math.Round(rawMillimeters / 0.5" in commands
assert "0.5mm吸附" in commands
assert "CreateBooleanDifference" in builder
assert "原件未修改" in builder
assert "BeginUndoRecord" in updater
assert "WoodSheetLayoutRole" in updater
assert "WSL_PAIR_" in updater
assert "WoodJointPro.SourceId" in updater
assert "doc.Objects.Replace" in updater
assert "Transform.PlaneToPlane" in updater
assert "CalibrationCoupon" in read(source / "Core" / "AuxiliaryGeometry.cs")

# Each public Rhino command needs a stable explicit GUID.
public_commands = len(re.findall(r"public sealed class .*Command", commands + aux))
command_guids = len(re.findall(r"\[Guid\(\"[0-9A-F-]{36}\"\)\]", commands + aux))
assert public_commands == command_guids

# Minimal brace-balance guard for accidental patch truncation.
for path in source.rglob("*.cs"):
    text = read(path)
    assert text.count("{") == text.count("}"), path

print("WoodJoint Pro 1.0.0 static checks passed.")
