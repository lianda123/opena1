from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ENGINE = (ROOT / "src/WoodExport/Core/WoodExportEngine.cs").read_text(encoding="utf-8")
ANALYZER = (ROOT / "src/WoodExport/Core/BoardExportAnalyzer.cs").read_text(encoding="utf-8")
NUMBERING = (ROOT / "src/WoodExport/Core/PartNumbering.cs").read_text(encoding="utf-8")
FONT = (ROOT / "src/WoodExport/Core/StrokeFont.cs").read_text(encoding="utf-8")
MODELS = (ROOT / "src/WoodExport/Core/ExportModels.cs").read_text(encoding="utf-8")
COMMANDS = (ROOT / "src/WoodExport/Commands/WoodExportCommands.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "src/WoodExport/WoodExport.csproj").read_text(encoding="utf-8")
README = (ROOT / "README.md").read_text(encoding="utf-8")


def test_contracts():
    for token in [
        "BuildGroupedComponents",
        "AddBoardFaceCurves",
        "BuildShapeSignature",
        "ThicknessToleranceMillimeters",
        "ShapeToleranceMillimeters",
    ]:
        assert token in ANALYZER, token

    for token in [
        "ExportSelected",
        "WoodExport_刻字",
        "ApplyNumberingAndLabels",
        "ExportCadByThickness",
    ]:
        assert token in ENGINE, token

    assert "SpacingMillimeters" in MODELS

    for command in ["WoodExport", "WXNumber", "WXBOM", "WXSettings", "WXClearLabels", "WXHelp"]:
        assert f'"{command}"' in COMMANDS, command

    assert '"P" + FormatThickness' in NUMBERING
    assert "LineCurve" in FONT and "TextEntity" not in FONT
    assert "net48;net8.0" in PROJECT
    assert 'RhinoCommon" Version="7.*' in PROJECT
    assert 'RhinoCommon" Version="8.*' in PROJECT
    assert "4.00 mm" in README and "DXF" in README and "DWG" in README


def test_numbering_examples():
    def thickness_text(value):
        rounded = round(value)
        return str(int(rounded)) if abs(value - rounded) <= 0.05 else f"{value:.2f}".rstrip("0").rstrip(".")

    assert f"P{thickness_text(2.0)}-001" == "P2-001"
    assert f"P{thickness_text(2.5)}-003" == "P2.5-003"


if __name__ == "__main__":
    test_contracts()
    test_numbering_examples()
    print("WoodExport source contracts verified.")
