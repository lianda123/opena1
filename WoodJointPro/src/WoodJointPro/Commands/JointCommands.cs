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
  internal static class JointCommandRunner
  {
    private static readonly double[] ThicknessPresets = { 1.5, 2.0, 2.5, 3.0, 4.0 };
    private static int _lastKind;
    private static int _lastFit = 1;
    private static int _lastThickness;
    private static int _lastClearanceMode;
    private static double _lastCustomThickness = 2.0;
    private static double _lastCustomClearance = 0.10;
    private static double _lastLength = 10.0;
    private static double _lastFingerWidth = 4.0;
    private static double _lastRelief = 0.8;

    internal static Result Run(RhinoDoc doc, JointKind? forcedKind)
    {
      if (doc == null)
        return Result.Failure;
      var scale = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      if (!RhinoMath.IsValidDouble(scale) || scale <= 0.0)
      {
        RhinoApp.WriteLine("WoodJoint Pro：无法换算当前文档单位，请先设置正确的模型单位。");
        return Result.Failure;
      }

      JointSettings settings;
      var configured = Configure(scale, forcedKind, out settings);
      if (configured != Result.Success)
        return configured;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, scale * 0.001);

      RhinoObject firstObject;
      Point3d firstPick;
      var firstResult = PickBoard(
        doc,
        "选择第一块木板（接收板/主板）",
        Guid.Empty,
        out firstObject,
        out firstPick);
      if (firstResult != Result.Success)
        return firstResult;

      RhinoObject secondObject;
      Point3d secondPick;
      var secondResult = PickBoard(
        doc,
        "选择第二块木板（插入板/配合板）",
        firstObject.Id,
        out secondObject,
        out secondPick);
      if (secondResult != Result.Success)
        return secondResult;

      BoardInfo first;
      BoardInfo second;
      if (!BoardAnalyzer.TryAnalyze(firstObject, tolerance, out first) ||
          !BoardAnalyzer.TryAnalyze(secondObject, tolerance, out second))
      {
        RhinoApp.WriteLine("WoodJoint Pro：所选对象必须是具有两张平行主表面的平直闭合Brep/Extrusion木板。");
        return Result.Failure;
      }

      JointFrame frame;
      string frameError;
      if (NeedsPickedLength(settings.Kind))
      {
        JointFrame pickedFrame;
        var pickLengthResult = PickSnappedLength(doc, first, scale, out pickedFrame);
        if (pickLengthResult != Result.Success)
          return pickLengthResult;
        if (!JointGeometryBuilder.TryAlignPickedFrame(
          first,
          second,
          pickedFrame,
          scale,
          out frame,
          out frameError))
        {
          RhinoApp.WriteLine("WoodJoint Pro：" + frameError + "。");
          return Result.Failure;
        }
      }
      else if (!JointGeometryBuilder.TryCreateAutomaticFrame(
        first,
        second,
        firstPick,
        secondPick,
        settings,
        tolerance,
        out frame,
        out frameError))
      {
        RhinoApp.WriteLine("WoodJoint Pro：" + frameError + "。");
        return Result.Failure;
      }

      JointBuildResult build;
      string buildError;
      var calibration = JointCalibrationStore.Load(doc);
      if (!JointGeometryBuilder.TryBuild(
        first,
        second,
        frame,
        settings,
        calibration,
        tolerance,
        out build,
        out buildError))
      {
        RhinoApp.WriteLine("WoodJoint Pro：" + buildError + "。");
        return Result.Failure;
      }

      using (var preview = new JointPreviewConduit())
      {
        preview.Show(build);
        var confirm = new GetOption();
        confirm.SetCommandPrompt("检查橙色长度线和红/蓝切割体；回车确认生成，Esc取消");
        confirm.AcceptNothing(true);
        var previewResult = confirm.Get();
        if (previewResult == GetResult.Cancel)
          return Result.Cancel;
        if (previewResult != GetResult.Nothing)
          return confirm.CommandResult();
        preview.Clear();
      }

      int flatCount;
      string updateError;
      if (!JointDocumentUpdater.ApplyJoint(
        doc,
        build,
        settings,
        calibration,
        tolerance,
        out flatCount,
        out updateError))
      {
        RhinoApp.WriteLine("WoodJoint Pro：" + updateError + "。");
        return Result.Failure;
      }

      firstObject.Select(false);
      secondObject.Select(false);
      var clearance = settings.ClearanceMillimeters(calibration);
      RhinoApp.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "WoodJoint Pro：{0}生成完成；第一块板{1:0.###}mm，第二块板{2:0.###}mm，{3}公差{4:+0.###;-0.###;0}mm；同步{5}个铺平副本。",
        build.Description,
        first.Thickness / scale,
        second.Thickness / scale,
        FitName(settings.Fit),
        clearance,
        flatCount));
      foreach (var warning in build.Warnings)
        RhinoApp.WriteLine("WoodJoint Pro：提示：" + warning + "。");
      RhinoApp.WriteLine("WoodJoint Pro：本次两块木板和铺平副本属于一个撤销步骤，可直接按Ctrl+Z撤回。");
      return Result.Success;
    }

    private static Result Configure(
      double scale,
      JointKind? forcedKind,
      out JointSettings settings)
    {
      settings = null;
      var customThickness = new OptionDouble(_lastCustomThickness);
      var customClearance = new OptionDouble(_lastCustomClearance);
      var length = new OptionDouble(_lastLength);
      var fingerWidth = new OptionDouble(_lastFingerWidth);
      var relief = new OptionDouble(_lastRelief);
      var getter = new GetOption();
      getter.SetCommandPrompt("设置榫槽类型、板厚和配合公差；回车开始选择两块木板");
      getter.AcceptNothing(true);
      var kindOption = forcedKind.HasValue ? -1 : getter.AddOptionList(
        L("JointType", "榫槽类型"),
        new[]
        {
          L("CrossSlot", "十字插槽"),
          L("TSlot", "T形槽"),
          L("TabSlot", "插片榫"),
          L("Snap", "简单卡扣"),
          L("Finger", "指接榫")
        },
        _lastKind);
      var fitOption = getter.AddOptionList(
        L("Fit", "配合"),
        new[]
        {
          L("Tight", "紧配"),
          L("Normal", "普通插接"),
          L("Loose", "松配")
        },
        _lastFit);
      var thicknessOption = getter.AddOptionList(
        L("MaterialThickness", "材料板厚"),
        new[]
        {
          L("Measured", "自动实测"),
          L("T1_5", "1点5毫米"),
          L("T2", "2毫米"),
          L("T2_5", "2点5毫米"),
          L("T3", "3毫米"),
          L("T4", "4毫米"),
          L("Custom", "自定义")
        },
        _lastThickness);
      var clearanceModeOption = getter.AddOptionList(
        L("ClearanceSource", "公差来源"),
        new[]
        {
          L("Calibration", "测试片标定值"),
          L("Custom", "本次自定义")
        },
        _lastClearanceMode);
      getter.AddOptionDouble(L("CustomThickness", "自定义板厚"), ref customThickness);
      getter.AddOptionDouble(L("CustomClearance", "自定义公差"), ref customClearance);
      getter.AddOptionDouble(L("Length", "默认榫槽长度"), ref length);
      getter.AddOptionDouble(L("FingerWidth", "指宽"), ref fingerWidth);
      getter.AddOptionDouble(L("SnapRelief", "卡扣释放缝"), ref relief);

      while (true)
      {
        var result = getter.Get();
        if (result == GetResult.Cancel)
          return Result.Cancel;
        if (result == GetResult.Nothing)
          break;
        if (result != GetResult.Option)
          continue;
        if (getter.OptionIndex() == kindOption)
          _lastKind = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == fitOption)
          _lastFit = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == thicknessOption)
          _lastThickness = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == clearanceModeOption)
          _lastClearanceMode = getter.Option().CurrentListOptionIndex;
      }

      _lastCustomThickness = customThickness.CurrentValue;
      _lastCustomClearance = customClearance.CurrentValue;
      _lastLength = length.CurrentValue;
      _lastFingerWidth = fingerWidth.CurrentValue;
      _lastRelief = relief.CurrentValue;
      var materialThickness = 0.0;
      if (_lastThickness >= 1 && _lastThickness <= ThicknessPresets.Length)
        materialThickness = ThicknessPresets[_lastThickness - 1];
      else if (_lastThickness == ThicknessPresets.Length + 1)
        materialThickness = _lastCustomThickness;
      settings = new JointSettings
      {
        Kind = forcedKind ?? (JointKind)Math.Max(0, Math.Min(4, _lastKind)),
        Fit = (FitClass)Math.Max(0, Math.Min(2, _lastFit)),
        MaterialThicknessMillimeters = materialThickness,
        CustomClearanceMillimeters = _lastClearanceMode == 1 ? _lastCustomClearance : double.NaN,
        JointLengthMillimeters = _lastLength,
        FingerWidthMillimeters = _lastFingerWidth,
        SnapReliefMillimeters = _lastRelief,
        ModelUnitsPerMillimeter = scale
      };
      if (_lastCustomThickness <= 0.1 || _lastCustomThickness > 50.0 ||
          _lastCustomClearance < -1.0 || _lastCustomClearance > 2.0 ||
          _lastLength < 0.5 || _lastLength > 500.0 ||
          _lastFingerWidth < 1.0 || _lastFingerWidth > 100.0 ||
          _lastRelief < 0.2 || _lastRelief > 5.0)
      {
        RhinoApp.WriteLine("WoodJoint Pro：自定义板厚、公差、长度、指宽或释放缝超出允许范围。");
        return Result.Failure;
      }
      return Result.Success;
    }

    private static Result PickBoard(
      RhinoDoc doc,
      string prompt,
      Guid excluded,
      out RhinoObject rhinoObject,
      out Point3d selectionPoint)
    {
      rhinoObject = null;
      selectionPoint = Point3d.Unset;
      while (true)
      {
        var getter = new GetObject();
        getter.SetCommandPrompt(prompt);
        getter.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion;
        getter.GroupSelect = false;
        getter.SubObjectSelect = false;
        getter.EnablePreSelect(true, true);
        getter.Get();
        if (getter.CommandResult() != Result.Success)
          return getter.CommandResult();
        if (getter.ObjectCount == 0)
          return Result.Nothing;
        var reference = getter.Object(0);
        rhinoObject = reference == null ? null : reference.Object();
        if (rhinoObject == null)
          continue;
        if (rhinoObject.Id == excluded)
        {
          RhinoApp.WriteLine("WoodJoint Pro：第二块木板不能与第一块相同，请重新选择。");
          rhinoObject.Select(false);
          continue;
        }
        try
        {
          selectionPoint = reference.SelectionPoint();
        }
        catch
        {
          selectionPoint = Point3d.Unset;
        }
        return Result.Success;
      }
    }

    private static Result PickSnappedLength(
      RhinoDoc doc,
      BoardInfo board,
      double scale,
      out JointFrame frame)
    {
      frame = null;
      var firstGetter = new GetPoint();
      firstGetter.SetCommandPrompt("在第一块木板上指定卡口/榫槽长度的第一点");
      firstGetter.Get();
      if (firstGetter.CommandResult() != Result.Success)
        return firstGetter.CommandResult();
      var start = board.MidPlane.ClosestPoint(firstGetter.Point());

      var snappedEnd = start;
      var snappedLength = 0.5 * scale;
      var secondGetter = new GetPoint();
      secondGetter.SetCommandPrompt("移动鼠标指定第二点；长度按0.5mm吸附，单击确认");
      secondGetter.SetBasePoint(start, true);
      secondGetter.DynamicDraw += (sender, args) =>
      {
        Vector3d direction;
        ResolveSnappedPoint(board.MidPlane, start, args.CurrentPoint, scale,
          out snappedEnd, out snappedLength, out direction);
        args.Display.DrawLine(new Line(start, snappedEnd), System.Drawing.Color.FromArgb(255, 190, 55), 4);
        args.Display.DrawPoint(snappedEnd, Rhino.Display.PointStyle.RoundControlPoint, 8,
          System.Drawing.Color.FromArgb(255, 225, 90));
        args.Display.DrawDot(
          snappedEnd,
          string.Format(CultureInfo.InvariantCulture, "{0:0.0} mm", snappedLength / scale),
          System.Drawing.Color.FromArgb(255, 225, 90),
          System.Drawing.Color.Black);
      };
      secondGetter.Get();
      if (secondGetter.CommandResult() != Result.Success)
        return secondGetter.CommandResult();
      Vector3d finalDirection;
      ResolveSnappedPoint(board.MidPlane, start, secondGetter.Point(), scale,
        out snappedEnd, out snappedLength, out finalDirection);
      if (!finalDirection.Unitize())
        return Result.Failure;
      frame = new JointFrame
      {
        Center = (start + snappedEnd) * 0.5,
        Along = finalDirection,
        Length = snappedLength
      };
      return Result.Success;
    }

    private static void ResolveSnappedPoint(
      Plane plane,
      Point3d start,
      Point3d current,
      double scale,
      out Point3d end,
      out double length,
      out Vector3d direction)
    {
      var projected = plane.ClosestPoint(current);
      direction = projected - start;
      var raw = direction.Length;
      if (!direction.Unitize())
      {
        direction = plane.XAxis;
        direction.Unitize();
        raw = scale * 0.5;
      }
      var rawMillimeters = raw / scale;
      var snappedMillimeters = Math.Max(0.5,
        Math.Round(rawMillimeters / 0.5, MidpointRounding.AwayFromZero) * 0.5);
      length = snappedMillimeters * scale;
      end = start + direction * length;
    }

    private static bool NeedsPickedLength(JointKind kind)
    {
      return kind == JointKind.TabSlot || kind == JointKind.Snap || kind == JointKind.Finger;
    }

    private static string FitName(FitClass fit)
    {
      if (fit == FitClass.Tight)
        return "紧配";
      return fit == FitClass.Loose ? "松配" : "普通插接";
    }

    internal static LocalizeStringPair L(string english, string chinese)
    {
      return new LocalizeStringPair(english, chinese);
    }
  }

  [Guid("298F508B-0B20-44A6-B3F2-05CF19FD1D28")]
  public sealed class CreateJointCommand : Command
  {
    public override string EnglishName => "WJPJoint";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => JointCommandRunner.Run(doc, null);
  }

  [Guid("0AA04A48-57E7-477B-97BD-3DD6BBF93214")]
  public sealed class CrossSlotCommand : Command
  {
    public override string EnglishName => "WJPCrossSlot";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => JointCommandRunner.Run(doc, JointKind.CrossSlot);
  }

  [Guid("2B976C95-3101-4139-BD32-173EC80E3CA1")]
  public sealed class TSlotCommand : Command
  {
    public override string EnglishName => "WJPTSlot";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => JointCommandRunner.Run(doc, JointKind.TSlot);
  }

  [Guid("14153DA9-623D-43C4-B0C4-3CF404EBDB6C")]
  public sealed class TabSlotCommand : Command
  {
    public override string EnglishName => "WJPTabSlot";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => JointCommandRunner.Run(doc, JointKind.TabSlot);
  }

  [Guid("79289143-E8C4-4505-8A11-8D5370E518EF")]
  public sealed class SnapCommand : Command
  {
    public override string EnglishName => "WJPSnap";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => JointCommandRunner.Run(doc, JointKind.Snap);
  }

  [Guid("D1A0241C-E0A0-4DE1-83EE-27573E429A8E")]
  public sealed class FingerJointCommand : Command
  {
    public override string EnglishName => "WJPFingerJoint";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => JointCommandRunner.Run(doc, JointKind.Finger);
  }
}
