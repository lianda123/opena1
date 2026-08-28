using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using WoodJointPro.Core;

namespace WoodJointPro.Commands
{
  [Guid("0916A753-5BA6-4706-935B-A3BF4562AD08")]
  public sealed class AxisHoleCommand : Command
  {
    private static int _lastKind = 1;
    private static int _lastDiameterMode;
    private static double _lastCustomDiameter = 2.10;

    public override string EnglishName => "WJPAxisHole";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var scale = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      if (!RhinoMath.IsValidDouble(scale) || scale <= 0.0)
        return Result.Failure;
      var calibration = JointCalibrationStore.Load(doc);
      var custom = new OptionDouble(_lastCustomDiameter);
      var getter = new GetOption();
      getter.SetCommandPrompt("设置2mm钢轴孔类型；回车后选择木板和孔中心");
      getter.AcceptNothing(true);
      var kindOption = getter.AddOptionList(
        JointCommandRunner.L("HoleType", "轴孔类型"),
        new[]
        {
          JointCommandRunner.L("Fixed", "固定孔"),
          JointCommandRunner.L("Rotating", "转动孔"),
          JointCommandRunner.L("Guide", "导向孔")
        },
        _lastKind);
      var diameterOption = getter.AddOptionList(
        JointCommandRunner.L("DiameterSource", "孔径来源"),
        new[]
        {
          JointCommandRunner.L("Calibration", "测试片标定值"),
          JointCommandRunner.L("Custom", "本次自定义")
        },
        _lastDiameterMode);
      getter.AddOptionDouble(JointCommandRunner.L("CustomDiameter", "自定义孔径"), ref custom);
      while (true)
      {
        var getResult = getter.Get();
        if (getResult == GetResult.Cancel)
          return Result.Cancel;
        if (getResult == GetResult.Nothing)
          break;
        if (getResult != GetResult.Option)
          continue;
        if (getter.OptionIndex() == kindOption)
          _lastKind = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == diameterOption)
          _lastDiameterMode = getter.Option().CurrentListOptionIndex;
      }
      _lastCustomDiameter = custom.CurrentValue;
      if (_lastCustomDiameter <= 0.1 || _lastCustomDiameter > 50.0)
      {
        RhinoApp.WriteLine("WoodJoint Pro：自定义孔径必须大于0.1mm且不超过50mm。");
        return Result.Failure;
      }
      var kind = (AxisHoleKind)Math.Max(0, Math.Min(2, _lastKind));
      var diameterMillimeters = _lastDiameterMode == 1
        ? _lastCustomDiameter
        : calibration.HoleDiameter(kind);

      RhinoObject rhinoObject;
      var pickResult = PickBoard("选择需要开2mm钢轴孔的木板", out rhinoObject);
      if (pickResult != Result.Success)
        return pickResult;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, scale * 0.001);
      BoardInfo board;
      if (!BoardAnalyzer.TryAnalyze(rhinoObject, tolerance, out board))
      {
        RhinoApp.WriteLine("WoodJoint Pro：所选对象不是可测厚的平直闭合木板。");
        return Result.Failure;
      }

      var pointGetter = new GetPoint();
      pointGetter.SetCommandPrompt(string.Format(
        CultureInfo.InvariantCulture,
        "指定轴孔中心（当前直径Ø{0:0.###}mm）",
        diameterMillimeters));
      pointGetter.DynamicDraw += (sender, args) =>
      {
        var point = board.MidPlane.ClosestPoint(args.CurrentPoint);
        args.Display.DrawPoint(point, Rhino.Display.PointStyle.RoundControlPoint, 9,
          System.Drawing.Color.FromArgb(255, 190, 55));
        args.Display.DrawDot(point,
          string.Format(CultureInfo.InvariantCulture, "Ø{0:0.###} mm", diameterMillimeters),
          System.Drawing.Color.FromArgb(255, 225, 90), System.Drawing.Color.Black);
      };
      pointGetter.Get();
      if (pointGetter.CommandResult() != Result.Success)
        return pointGetter.CommandResult();

      Brep geometry;
      if (!AuxiliaryGeometry.TryCutAxisHole(
        board,
        board.MidPlane.ClosestPoint(pointGetter.Point()),
        diameterMillimeters * scale,
        tolerance,
        out geometry))
      {
        RhinoApp.WriteLine("WoodJoint Pro：轴孔布尔失败，木板保持不变。");
        return Result.Failure;
      }
      int flatCount;
      string error;
      if (!JointDocumentUpdater.ApplySingleBoardEdit(
        doc,
        board,
        geometry,
        "2mm钢轴孔",
        tolerance,
        out flatCount,
        out error))
      {
        RhinoApp.WriteLine("WoodJoint Pro：" + error + "。");
        return Result.Failure;
      }
      RhinoApp.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "WoodJoint Pro：已生成{0}Ø{1:0.###}mm，同步{2}个铺平副本；Ctrl+Z可撤回。",
        HoleName(kind),
        diameterMillimeters,
        flatCount));
      return Result.Success;
    }

    private static string HoleName(AxisHoleKind kind)
    {
      if (kind == AxisHoleKind.Fixed)
        return "固定孔";
      return kind == AxisHoleKind.Guide ? "导向孔" : "转动孔";
    }

    internal static Result PickBoard(string prompt, out RhinoObject rhinoObject)
    {
      rhinoObject = null;
      var getter = new GetObject();
      getter.SetCommandPrompt(prompt);
      getter.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion;
      getter.GroupSelect = false;
      getter.SubObjectSelect = false;
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      rhinoObject = getter.ObjectCount == 0 ? null : getter.Object(0).Object();
      return rhinoObject == null ? Result.Nothing : Result.Success;
    }
  }

  [Guid("E5D3054E-E02E-4E50-BC6F-E111AFB97B66")]
  public sealed class CalibrationCouponCommand : Command
  {
    private static int _lastThicknessIndex = 2;
    private static double _lastCustomThickness = 2.0;
    private static readonly double[] Presets = { 1.5, 2.0, 2.5, 3.0, 4.0 };

    public override string EnglishName => "WJPCalibrationTest";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var custom = new OptionDouble(_lastCustomThickness);
      var getter = new GetOption();
      getter.SetCommandPrompt("选择公差测试片板厚；回车后指定放置点");
      getter.AcceptNothing(true);
      var thicknessOption = getter.AddOptionList(
        JointCommandRunner.L("Thickness", "板厚"),
        new[]
        {
          JointCommandRunner.L("T1_5", "1点5毫米"),
          JointCommandRunner.L("T2", "2毫米"),
          JointCommandRunner.L("T2_5", "2点5毫米"),
          JointCommandRunner.L("T3", "3毫米"),
          JointCommandRunner.L("T4", "4毫米"),
          JointCommandRunner.L("Custom", "自定义")
        },
        _lastThicknessIndex);
      getter.AddOptionDouble(JointCommandRunner.L("CustomThickness", "自定义板厚"), ref custom);
      while (true)
      {
        var result = getter.Get();
        if (result == GetResult.Cancel)
          return Result.Cancel;
        if (result == GetResult.Nothing)
          break;
        if (result == GetResult.Option && getter.OptionIndex() == thicknessOption)
          _lastThicknessIndex = getter.Option().CurrentListOptionIndex;
      }
      _lastCustomThickness = custom.CurrentValue;
      if (_lastCustomThickness <= 0.1 || _lastCustomThickness > 50.0)
        return Result.Failure;
      var thickness = _lastThicknessIndex < Presets.Length
        ? Presets[_lastThicknessIndex]
        : _lastCustomThickness;
      var point = new GetPoint();
      point.SetCommandPrompt("指定测试片左下角放置点");
      point.Get();
      if (point.CommandResult() != Result.Success)
        return point.CommandResult();
      var undo = doc.BeginUndoRecord("生成 WoodJoint Pro 公差测试片");
      try
      {
        string description;
        if (!AuxiliaryGeometry.CreateCalibrationCoupon(doc, point.Point(), thickness, out description))
        {
          RhinoApp.WriteLine("WoodJoint Pro：测试片生成失败。");
          return Result.Failure;
        }
        RhinoApp.WriteLine("WoodJoint Pro：" + description + "；测试后运行WJPSettings填写实测结果。");
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }
      return Result.Success;
    }
  }

  [Guid("2812C819-998C-4746-8641-05F5C2C0C3D2")]
  public sealed class SettingsCommand : Command
  {
    public override string EnglishName => "WJPSettings";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var calibration = JointCalibrationStore.Load(doc);
      var tight = new OptionDouble(calibration.TightClearanceMillimeters);
      var normal = new OptionDouble(calibration.NormalClearanceMillimeters);
      var loose = new OptionDouble(calibration.LooseClearanceMillimeters);
      var fixedHole = new OptionDouble(calibration.FixedHoleMillimeters);
      var rotatingHole = new OptionDouble(calibration.RotatingHoleMillimeters);
      var guideHole = new OptionDouble(calibration.GuideHoleMillimeters);
      var getter = new GetOption();
      getter.SetCommandPrompt("输入测试片实测标定值；回车保存到当前3dm文档");
      getter.AcceptNothing(true);
      getter.AddOptionDouble(JointCommandRunner.L("TightClearance", "紧配公差"), ref tight);
      getter.AddOptionDouble(JointCommandRunner.L("NormalClearance", "普通公差"), ref normal);
      getter.AddOptionDouble(JointCommandRunner.L("LooseClearance", "松配公差"), ref loose);
      getter.AddOptionDouble(JointCommandRunner.L("FixedHole", "固定孔径"), ref fixedHole);
      getter.AddOptionDouble(JointCommandRunner.L("RotatingHole", "转动孔径"), ref rotatingHole);
      getter.AddOptionDouble(JointCommandRunner.L("GuideHole", "导向孔径"), ref guideHole);
      while (true)
      {
        var result = getter.Get();
        if (result == GetResult.Cancel)
          return Result.Cancel;
        if (result == GetResult.Nothing)
          break;
      }
      calibration.TightClearanceMillimeters = tight.CurrentValue;
      calibration.NormalClearanceMillimeters = normal.CurrentValue;
      calibration.LooseClearanceMillimeters = loose.CurrentValue;
      calibration.FixedHoleMillimeters = fixedHole.CurrentValue;
      calibration.RotatingHoleMillimeters = rotatingHole.CurrentValue;
      calibration.GuideHoleMillimeters = guideHole.CurrentValue;
      if (calibration.TightClearanceMillimeters < -1.0 ||
          calibration.LooseClearanceMillimeters > 2.0 ||
          calibration.FixedHoleMillimeters <= 0.1 ||
          calibration.RotatingHoleMillimeters <= 0.1 ||
          calibration.GuideHoleMillimeters <= 0.1)
      {
        RhinoApp.WriteLine("WoodJoint Pro：标定值超出允许范围，未保存。");
        return Result.Failure;
      }
      JointCalibrationStore.Save(doc, calibration);
      RhinoApp.WriteLine("WoodJoint Pro：当前3dm的配合公差与2mm钢轴孔径标定值已保存。");
      return Result.Success;
    }
  }

  [Guid("18685B76-F871-4241-87C0-2E37B6761E36")]
  public sealed class LinkFlatCommand : Command
  {
    public override string EnglishName => "WJPLinkFlat";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      RhinoObject source;
      var first = AxisHoleCommand.PickBoard("选择3D源木板", out source);
      if (first != Result.Success)
        return first;
      RhinoObject flat;
      var second = AxisHoleCommand.PickBoard("选择该木板的铺平副本", out flat);
      if (second != Result.Success)
        return second;
      if (source.Id == flat.Id)
        return Result.Failure;
      var scale = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, scale * 0.001);
      BoardInfo sourceInfo;
      BoardInfo flatInfo;
      if (!BoardAnalyzer.TryAnalyze(source, tolerance, out sourceInfo) ||
          !BoardAnalyzer.TryAnalyze(flat, tolerance, out flatInfo))
        return Result.Failure;
      if (Math.Abs(sourceInfo.Thickness - flatInfo.Thickness) > Math.Max(scale * 0.2, tolerance * 10.0))
      {
        RhinoApp.WriteLine("WoodJoint Pro：源木板与铺平副本厚度不一致，未建立关联。");
        return Result.Failure;
      }
      var undo = doc.BeginUndoRecord("关联 WoodJoint Pro 铺平副本");
      try
      {
        var flatAttributes = flat.Attributes.Duplicate();
        flatAttributes.SetUserString("WoodJointPro.SourceId", source.Id.ToString("D"));
        flatAttributes.SetUserString("WoodJointPro.Role", "FlatCopy");
        if (!doc.Objects.ModifyAttributes(flat.Id, flatAttributes, true))
          return Result.Failure;
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }
      RhinoApp.WriteLine("WoodJoint Pro：3D源木板与铺平副本已关联，后续榫槽和轴孔会自动同步。");
      return Result.Success;
    }
  }

  [Guid("016CF470-D632-459F-BAEE-B3515091A4F7")]
  public sealed class UpdateFlatCommand : Command
  {
    public override string EnglishName => "WJPUpdateFlat";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      RhinoObject source;
      var pick = AxisHoleCommand.PickBoard("选择需要重新同步铺平副本的3D源木板", out source);
      if (pick != Result.Success)
        return pick;
      var scale = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, scale * 0.001);
      BoardInfo board;
      if (!BoardAnalyzer.TryAnalyze(source, tolerance, out board))
        return Result.Failure;
      int count;
      string error;
      if (!JointDocumentUpdater.ApplySingleBoardEdit(
        doc,
        board,
        board.Brep,
        "重新同步铺平副本",
        tolerance,
        out count,
        out error))
      {
        RhinoApp.WriteLine("WoodJoint Pro：" + error + "。");
        return Result.Failure;
      }
      RhinoApp.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "WoodJoint Pro：已重新同步{0}个铺平副本。",
        count));
      return count > 0 ? Result.Success : Result.Nothing;
    }
  }
}
