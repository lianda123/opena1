using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using WoodThicknessAdjuster.Core;

namespace WoodThicknessAdjuster.Commands
{
  [Guid("6C8481DA-D564-4D05-8F92-3ADAEE4FC734")]
  public sealed class AdjustThicknessCommand : Command
  {
    private static readonly double[] Presets = { 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
    private static int _lastThicknessIndex = 2;
    private static int _lastAnchorIndex;
    private static int _lastContactIndex;
    private static int _lastMoveIndex;
    private static double _lastCustomThickness = 2.5;

    public override string EnglishName => "WSAdjustThickness";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      double thickness;
      ThicknessAnchorMode anchorMode;
      ThicknessContactMode contactMode;
      ThicknessMoveMode moveMode;
      var result = Configure(
        out thickness,
        out anchorMode,
        out contactMode,
        out moveMode);
      if (result != Result.Success)
        return result;
      return ThicknessAdjustmentWorkflow.Run(
        doc,
        thickness,
        anchorMode,
        contactMode,
        moveMode);
    }

    private static Result Configure(
      out double thicknessMillimeters,
      out ThicknessAnchorMode anchorMode,
      out ThicknessContactMode contactMode,
      out ThicknessMoveMode moveMode)
    {
      thicknessMillimeters = Presets[Math.Min(_lastThicknessIndex, Presets.Length - 1)];
      anchorMode = (ThicknessAnchorMode)Math.Min(_lastAnchorIndex, 1);
      contactMode = ContactModeFromIndex(_lastContactIndex);
      moveMode = MoveModeFromIndex(_lastMoveIndex);
      var customThickness = new OptionDouble(_lastCustomThickness);
      var getter = new GetOption();
      getter.SetCommandPrompt("设置目标板厚、保持方式与装配贴合，回车后连续点击木板");
      getter.AcceptNothing(true);
      var thicknessOption = getter.AddOptionList(
        L("Thickness", "目标板厚"),
        new[]
        {
          L("T1_5mm", "1点5毫米"),
          L("T2mm", "2毫米"),
          L("T2_5mm", "2点5毫米"),
          L("T3mm", "3毫米"),
          L("T4mm", "4毫米"),
          L("T5mm", "5毫米"),
          L("Custom", "自定义")
        },
        _lastThicknessIndex);
      var anchorOption = getter.AddOptionList(
        L("Anchor", "保持方式"),
        new[]
        {
          L("ClickedFace", "保持点击面"),
          L("Center", "中心对称")
        },
        _lastAnchorIndex);
      var contactOption = getter.AddOptionList(
        L("Contact", "装配贴合"),
        new[]
        {
          L("AutoFit", "自动贴合"),
          L("ExplicitFace", "指定目标面"),
          L("Off", "关闭")
        },
        _lastContactIndex);
      var moveOption = getter.AddOptionList(
        L("MoveCoordinate", "贴合移动坐标"),
        new[]
        {
          L("ObjectAxis", "物体厚度轴"),
          L("WorldAuto", "世界自动轴"),
          L("WorldX", "世界X轴"),
          L("WorldY", "世界Y轴"),
          L("WorldZ", "世界Z轴")
        },
        _lastMoveIndex);
      getter.AddOptionDouble(L("CustomThickness", "自定义板厚"), ref customThickness);

      while (true)
      {
        var getResult = getter.Get();
        if (getResult == GetResult.Cancel)
          return Result.Cancel;
        if (getResult == GetResult.Nothing)
          break;
        if (getResult != GetResult.Option)
          continue;
        if (getter.OptionIndex() == thicknessOption)
          _lastThicknessIndex = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == anchorOption)
          _lastAnchorIndex = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == contactOption)
          _lastContactIndex = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == moveOption)
          _lastMoveIndex = getter.Option().CurrentListOptionIndex;
      }

      _lastCustomThickness = customThickness.CurrentValue;
      thicknessMillimeters = _lastThicknessIndex < Presets.Length
        ? Presets[_lastThicknessIndex]
        : _lastCustomThickness;
      anchorMode = _lastAnchorIndex == 1
        ? ThicknessAnchorMode.Center
        : ThicknessAnchorMode.ClickedFace;
      contactMode = ContactModeFromIndex(_lastContactIndex);
      moveMode = MoveModeFromIndex(_lastMoveIndex);
      if (thicknessMillimeters <= 0.1 || thicknessMillimeters > 50.0)
      {
        RhinoApp.WriteLine("WoodThicknessAdjuster：自定义板厚必须大于0.1mm且不超过50mm。");
        return Result.Failure;
      }
      return Result.Success;
    }

    private static ThicknessContactMode ContactModeFromIndex(int index)
    {
      if (index == 1)
        return ThicknessContactMode.ExplicitFace;
      return index == 2
        ? ThicknessContactMode.Off
        : ThicknessContactMode.AutoFit;
    }

    private static ThicknessMoveMode MoveModeFromIndex(int index)
    {
      if (index == 1)
        return ThicknessMoveMode.WorldAuto;
      if (index == 2)
        return ThicknessMoveMode.WorldX;
      if (index == 3)
        return ThicknessMoveMode.WorldY;
      if (index == 4)
        return ThicknessMoveMode.WorldZ;
      return ThicknessMoveMode.ObjectAxis;
    }

    private static LocalizeStringPair L(string english, string chinese)
    {
      return new LocalizeStringPair(english, chinese);
    }
  }

  [Guid("D727B7F7-66B8-429F-8305-B461D862ADE8")]
  public sealed class AdjustThicknessFitCommand : Command
  {
    private static readonly double[] Presets = { 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
    private static int _lastThicknessIndex = 2;
    private static int _lastCoordinateIndex;
    private static int _lastAxisIndex;
    private static double _lastCustomThickness = 2.5;

    public override string EnglishName => "WSAdjustThicknessFit";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      double thickness;
      ThicknessMoveMode moveMode;
      var result = Configure(out thickness, out moveMode);
      if (result != Result.Success)
        return result;
      return ThicknessAdjustmentWorkflow.Run(
        doc,
        thickness,
        ThicknessAnchorMode.ClickedFace,
        ThicknessContactMode.ExplicitFace,
        moveMode);
    }

    private static Result Configure(
      out double thicknessMillimeters,
      out ThicknessMoveMode moveMode)
    {
      thicknessMillimeters = Presets[Math.Min(_lastThicknessIndex, Presets.Length - 1)];
      moveMode = ResolveMoveMode(_lastCoordinateIndex, _lastAxisIndex);
      var customThickness = new OptionDouble(_lastCustomThickness);
      var getter = new GetOption();
      getter.SetCommandPrompt("设置目标板厚与指定贴合的移动坐标，回车后按两次点击操作");
      getter.AcceptNothing(true);
      var thicknessOption = getter.AddOptionList(
        L("Thickness", "目标板厚"),
        new[]
        {
          L("T1_5mm", "1点5毫米"),
          L("T2mm", "2毫米"),
          L("T2_5mm", "2点5毫米"),
          L("T3mm", "3毫米"),
          L("T4mm", "4毫米"),
          L("T5mm", "5毫米"),
          L("Custom", "自定义")
        },
        _lastThicknessIndex);
      var coordinateOption = getter.AddOptionList(
        L("Coordinates", "移动坐标"),
        new[]
        {
          L("Object", "物体坐标"),
          L("World", "世界坐标")
        },
        _lastCoordinateIndex);
      var axisOption = getter.AddOptionList(
        L("Axis", "移动轴"),
        new[]
        {
          L("Auto", "自动轴"),
          L("X", "X轴"),
          L("Y", "Y轴"),
          L("Z", "Z轴")
        },
        _lastAxisIndex);
      getter.AddOptionDouble(L("CustomThickness", "自定义板厚"), ref customThickness);

      while (true)
      {
        var getResult = getter.Get();
        if (getResult == GetResult.Cancel)
          return Result.Cancel;
        if (getResult == GetResult.Nothing)
          break;
        if (getResult != GetResult.Option)
          continue;
        if (getter.OptionIndex() == thicknessOption)
          _lastThicknessIndex = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == coordinateOption)
          _lastCoordinateIndex = getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() == axisOption)
          _lastAxisIndex = getter.Option().CurrentListOptionIndex;
      }

      _lastCustomThickness = customThickness.CurrentValue;
      thicknessMillimeters = _lastThicknessIndex < Presets.Length
        ? Presets[_lastThicknessIndex]
        : _lastCustomThickness;
      moveMode = ResolveMoveMode(_lastCoordinateIndex, _lastAxisIndex);
      if (thicknessMillimeters <= 0.1 || thicknessMillimeters > 50.0)
      {
        RhinoApp.WriteLine("WoodThicknessAdjuster：自定义板厚必须大于0.1mm且不超过50mm。");
        return Result.Failure;
      }
      return Result.Success;
    }

    private static ThicknessMoveMode ResolveMoveMode(
      int coordinateIndex,
      int axisIndex)
    {
      if (coordinateIndex == 0)
        return ThicknessMoveMode.ObjectAxis;
      if (axisIndex == 1)
        return ThicknessMoveMode.WorldX;
      if (axisIndex == 2)
        return ThicknessMoveMode.WorldY;
      if (axisIndex == 3)
        return ThicknessMoveMode.WorldZ;
      return ThicknessMoveMode.WorldAuto;
    }

    private static LocalizeStringPair L(string english, string chinese)
    {
      return new LocalizeStringPair(english, chinese);
    }
  }

  internal static class FixedThicknessCommand
  {
    internal static Result Run(RhinoDoc doc, double thickness)
    {
      return ThicknessAdjustmentWorkflow.Run(
        doc,
        thickness,
        ThicknessAnchorMode.ClickedFace,
        ThicknessContactMode.AutoFit,
        ThicknessMoveMode.ObjectAxis);
    }
  }

  [Guid("1BFDBBFC-8480-435A-B38A-BC0FE0D88019")]
  public sealed class Thickness15Command : Command
  {
    public override string EnglishName => "WSThickness15";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      FixedThicknessCommand.Run(doc, 1.5);
  }

  [Guid("44F32526-39DE-44FE-BC57-3DD0DB74E8C9")]
  public sealed class Thickness20Command : Command
  {
    public override string EnglishName => "WSThickness20";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      FixedThicknessCommand.Run(doc, 2.0);
  }

  [Guid("95C481B2-E3A0-44BC-9B9E-7F3895CCADF5")]
  public sealed class Thickness25Command : Command
  {
    public override string EnglishName => "WSThickness25";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      FixedThicknessCommand.Run(doc, 2.5);
  }

  [Guid("CFCF8DA8-EC4B-4FC7-846E-1A27B6BC6861")]
  public sealed class Thickness30Command : Command
  {
    public override string EnglishName => "WSThickness30";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      FixedThicknessCommand.Run(doc, 3.0);
  }

  [Guid("8E03B039-96A4-441B-90BE-7206C82F15DB")]
  public sealed class Thickness40Command : Command
  {
    public override string EnglishName => "WSThickness40";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      FixedThicknessCommand.Run(doc, 4.0);
  }

  [Guid("7FE84B5C-033A-4E05-BCF9-DB46A9A259EF")]
  public sealed class Thickness50Command : Command
  {
    public override string EnglishName => "WSThickness50";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      FixedThicknessCommand.Run(doc, 5.0);
  }
}
