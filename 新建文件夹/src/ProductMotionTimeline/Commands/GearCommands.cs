using System;
using ProductMotionTimeline.Core;
using ProductMotionTimeline.UI;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using System.Collections.Generic;

namespace ProductMotionTimeline.Commands
{
  internal static class GearCommandRunner
  {
    internal static Result Run(RhinoDoc doc, GearPartType type)
    {
      var pointGetter = new GetPoint();
      pointGetter.SetCommandPrompt("指定齿轮/齿条中心（使用当前工作平面）");
      pointGetter.Get();
      if (pointGetter.CommandResult() != Result.Success)
        return pointGetter.CommandResult();

      var parameters = new GearParameters { Type = type };
      if (type != GearPartType.Rack)
      {
        parameters.Teeth = GetInteger("齿数", type == GearPartType.Internal ? 48 : 20, type == GearPartType.Internal ? 8 : 4);
        if (parameters.Teeth < 1) return Result.Cancel;
      }
      parameters.Module = GetNumber("模数", 1.0, 1e-6, 100000.0);
      if (double.IsNaN(parameters.Module) || parameters.Module <= 0.0) return Result.Cancel;
      parameters.PressureAngleDegrees = GetNumber("压力角°", 20.0, 1.0, 44.0);
      if (double.IsNaN(parameters.PressureAngleDegrees) || parameters.PressureAngleDegrees <= 0.0) return Result.Cancel;
      parameters.Thickness = GetNumber("厚度", 3.0, 1e-6, 100000.0);
      if (double.IsNaN(parameters.Thickness) || parameters.Thickness <= 0.0) return Result.Cancel;

      if (type == GearPartType.Rack)
      {
        parameters.RackLength = GetNumber("齿条长度", parameters.Module * Math.PI * 12.0, parameters.Module * Math.PI, 1000000.0);
        if (double.IsNaN(parameters.RackLength) || parameters.RackLength <= 0.0) return Result.Cancel;
      }
      else
      {
        parameters.BoreDiameter = GetNumber("轴孔直径（0=无孔）", 2.0, 0.0, 100000.0);
        if (double.IsNaN(parameters.BoreDiameter) || parameters.BoreDiameter < 0.0) return Result.Cancel;
      }
      if (type == GearPartType.Helical)
      {
        parameters.HelixAngleDegrees = GetNumber("螺旋角°（负数=左旋）", 15.0, -45.0, 45.0);
        if (double.IsNaN(parameters.HelixAngleDegrees)) return Result.Cancel;
      }
      if (type == GearPartType.Bevel)
      {
        parameters.ConeAngleDegrees = GetNumber("节锥角°", 90.0, 1.0, 179.0);
        if (double.IsNaN(parameters.ConeAngleDegrees) || parameters.ConeAngleDegrees <= 0.0) return Result.Cancel;
      }
      var pitchReference = GetBooleanOption(
        "是否输出分度圆/分度线（回车=是）",
        true);
      if (!pitchReference.HasValue)
        return Result.Cancel;
      parameters.OutputPitchReference = pitchReference.Value;

      var plane = doc.Views.ActiveView == null
        ? Plane.WorldXY
        : doc.Views.ActiveView.ActiveViewport.ConstructionPlane();
      plane.Origin = pointGetter.Point();
      string warning;
      var geometry = GearGeometryGenerator.CreateGearGeometry(doc, parameters, plane, out warning);
      if (geometry == null)
      {
        RhinoApp.WriteLine(
          "ProductMotion：齿轮几何生成失败：{0}。请检查齿数、模数、压力角和厚度。",
          string.IsNullOrWhiteSpace(warning) ? "没有得到有效轮廓" : warning);
        return Result.Failure;
      }
      var name = BuildName(parameters);
      var geometries = new List<GeometryBase> { geometry };
      if (parameters.OutputPitchReference)
      {
        var reference = GearGeometryGenerator.CreatePitchReference(parameters, plane);
        if (reference != null)
          geometries.Add(reference);
      }
      var instance = TrackFactory.CreateGeneratedPart(doc, geometries, name, parameters);
      if (instance == null)
      {
        RhinoApp.WriteLine("ProductMotion：齿轮几何已经生成，但创建动画块失败。");
        return Result.Failure;
      }
      var track = TimelineEngine.AddTrack(doc, instance);
      if (track == null)
        return Result.Failure;
      TimelineEngine.SetPivotPlane(doc, track, plane);
      TimelineDocking.Open();
      RhinoApp.WriteLine(
        "ProductMotion：已生成 {0}，并自动建立时间轴轨道。{1}{2}",
        name,
        parameters.OutputPitchReference ? "已附带青色分度圆/分度线。" : string.Empty,
        string.IsNullOrWhiteSpace(warning) ? string.Empty : "提示：" + warning);
      return Result.Success;
    }

    internal static Result ChooseAndRun(RhinoDoc doc)
    {
      var getter = new GetOption();
      getter.SetCommandPrompt("选择要生成的齿轮类型");
      var spur = getter.AddOption(new LocalizeStringPair("Spur", "渐开线直齿轮"));
      var internalGear = getter.AddOption(new LocalizeStringPair("Internal", "内齿轮"));
      var helical = getter.AddOption(new LocalizeStringPair("Helical", "斜齿轮"));
      var bevel = getter.AddOption(new LocalizeStringPair("Bevel", "锥齿轮"));
      var rack = getter.AddOption(new LocalizeStringPair("Rack", "齿条"));
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      if (getter.OptionIndex() == internalGear) return Run(doc, GearPartType.Internal);
      if (getter.OptionIndex() == helical) return Run(doc, GearPartType.Helical);
      if (getter.OptionIndex() == bevel) return Run(doc, GearPartType.Bevel);
      if (getter.OptionIndex() == rack) return Run(doc, GearPartType.Rack);
      return getter.OptionIndex() == spur ? Run(doc, GearPartType.Spur) : Result.Cancel;
    }

    private static string BuildName(GearParameters parameters)
    {
      if (parameters.Type == GearPartType.Rack)
        return string.Format("齿条_L{0:0.###}_m{1:0.###}", parameters.RackLength, parameters.Module);
      return string.Format("{0}_{1}T_m{2:0.###}", parameters.DisplayName, parameters.Teeth, parameters.Module);
    }

    private static int GetInteger(string prompt, int defaultValue, int minimum)
    {
      var getter = new GetInteger();
      getter.SetCommandPrompt(prompt);
      getter.SetDefaultInteger(defaultValue);
      getter.SetLowerLimit(minimum, false);
      getter.Get();
      return getter.CommandResult() == Result.Success ? getter.Number() : -1;
    }

    private static double GetNumber(
      string prompt,
      double defaultValue,
      double minimum,
      double maximum)
    {
      var getter = new GetNumber();
      getter.SetCommandPrompt(prompt);
      getter.SetDefaultNumber(defaultValue);
      getter.SetLowerLimit(minimum, false);
      getter.SetUpperLimit(maximum, false);
      getter.Get();
      return getter.CommandResult() == Result.Success ? getter.Number() : double.NaN;
    }

    private static bool? GetBooleanOption(string prompt, bool defaultValue)
    {
      var getter = new GetOption();
      getter.SetCommandPrompt(prompt);
      getter.AcceptNothing(true);
      var yes = getter.AddOption(new LocalizeStringPair("Yes", "是"));
      var no = getter.AddOption(new LocalizeStringPair("No", "否"));
      var result = getter.Get();
      if (result == GetResult.Cancel)
        return null;
      if (result == GetResult.Option)
      {
        if (getter.OptionIndex() == yes) return true;
        if (getter.OptionIndex() == no) return false;
      }
      return defaultValue;
    }
  }

  public sealed class GearFactoryCommand : Command
  {
    public override string EnglishName => "PMTGearFactory";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => GearCommandRunner.ChooseAndRun(doc);
  }

  public sealed class CreateSpurGearCommand : Command
  {
    public override string EnglishName => "PMTCreateSpurGear";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => GearCommandRunner.Run(doc, GearPartType.Spur);
  }

  public sealed class CreateInternalGearCommand : Command
  {
    public override string EnglishName => "PMTCreateInternalGear";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => GearCommandRunner.Run(doc, GearPartType.Internal);
  }

  public sealed class CreateHelicalGearCommand : Command
  {
    public override string EnglishName => "PMTCreateHelicalGear";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => GearCommandRunner.Run(doc, GearPartType.Helical);
  }

  public sealed class CreateBevelGearCommand : Command
  {
    public override string EnglishName => "PMTCreateBevelGear";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => GearCommandRunner.Run(doc, GearPartType.Bevel);
  }

  public sealed class CreateRackCommand : Command
  {
    public override string EnglishName => "PMTCreateRack";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => GearCommandRunner.Run(doc, GearPartType.Rack);
  }
}
