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
using System.Linq;

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
      var planetary = getter.AddOption(new LocalizeStringPair("Planetary", "行星齿轮组"));
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      if (getter.OptionIndex() == internalGear) return Run(doc, GearPartType.Internal);
      if (getter.OptionIndex() == helical) return Run(doc, GearPartType.Helical);
      if (getter.OptionIndex() == bevel) return Run(doc, GearPartType.Bevel);
      if (getter.OptionIndex() == rack) return Run(doc, GearPartType.Rack);
      if (getter.OptionIndex() == planetary) return PlanetaryGearSetCommandRunner.Run(doc);
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

  internal static class PlanetaryGearSetCommandRunner
  {
    internal static Result Run(RhinoDoc doc)
    {
      var centerGetter = new GetPoint();
      centerGetter.SetCommandPrompt("指定行星齿轮组中心（使用当前工作平面）");
      centerGetter.Get();
      if (centerGetter.CommandResult() != Result.Success)
        return centerGetter.CommandResult();

      var parameters = new PlanetaryGearSetParameters
      {
        SunTeeth = CommandInput.GetPositiveInteger("太阳轮齿数 Zs", 24)
      };
      if (parameters.SunTeeth < 1) return Result.Cancel;
      parameters.PlanetTeeth = CommandInput.GetPositiveInteger("行星轮齿数 Zp", 18);
      if (parameters.PlanetTeeth < 1) return Result.Cancel;
      parameters.PlanetCount = CommandInput.GetPositiveInteger("行星轮数量 N（2–12）", 3);
      if (parameters.PlanetCount < 1) return Result.Cancel;
      parameters.Module = CommandInput.GetPositiveNumber("统一模数 m", 1.0, false);
      if (parameters.Module <= 0.0) return Result.Cancel;
      parameters.PressureAngleDegrees = CommandInput.GetPositiveNumber("统一压力角°", 20.0, false);
      if (parameters.PressureAngleDegrees <= 0.0) return Result.Cancel;
      parameters.GearThickness = CommandInput.GetPositiveNumber("齿轮厚度", 3.0, false);
      if (parameters.GearThickness <= 0.0) return Result.Cancel;
      parameters.BoreDiameter = CommandInput.GetPositiveNumber("太阳轮/行星轮轴孔直径（0=无孔）", 2.0, true);
      if (parameters.BoreDiameter < 0.0) return Result.Cancel;
      parameters.CarrierThickness = CommandInput.GetPositiveNumber("行星架厚度", 2.0, false);
      if (parameters.CarrierThickness <= 0.0) return Result.Cancel;
      parameters.PlanetShaftDiameter = CommandInput.GetPositiveNumber("行星架行星轴孔直径", 2.0, false);
      if (parameters.PlanetShaftDiameter <= 0.0) return Result.Cancel;
      var outputReferences = GetBooleanOption("输出所有分度圆（回车=是）", true);
      if (!outputReferences.HasValue) return Result.Cancel;
      parameters.OutputPitchReferences = outputReferences.Value;
      var fixedMember = GetFixedMember();
      if (!fixedMember.HasValue) return Result.Cancel;
      parameters.FixedMember = fixedMember.Value;

      var validation = PlanetaryGearSetGenerator.Validate(parameters);
      if (!validation.IsValid)
      {
        RhinoApp.WriteLine("ProductMotion：行星齿轮参数无效：{0}。", validation.Message);
        return Result.Failure;
      }

      var centerPlane = doc.Views.ActiveView == null
        ? Plane.WorldXY
        : doc.Views.ActiveView.ActiveViewport.ConstructionPlane();
      centerPlane.Origin = centerGetter.Point();
      var sunParameters = GearParameters(parameters, GearPartType.Spur, parameters.SunTeeth);
      var planetParameters = GearParameters(parameters, GearPartType.Spur, parameters.PlanetTeeth);
      var ringParameters = GearParameters(parameters, GearPartType.Internal, parameters.RingTeeth);
      var warnings = new List<string>();
      var ringPlane = PlanetaryGearSetGenerator.RingPlane(parameters, centerPlane);

      string warning;
      var sunGeometry = GearGeometryGenerator.CreateGearGeometry(
        doc, sunParameters, centerPlane, out warning);
      AddWarning(warnings, "太阳轮", warning);
      var ringGeometry = GearGeometryGenerator.CreateGearGeometry(
        doc, ringParameters, ringPlane, out warning);
      AddWarning(warnings, "内齿圈", warning);
      var planetPlanes = new List<Plane>(PlanetaryGearSetGenerator.PlanetPlanes(parameters, centerPlane));
      var planetGeometries = new List<GeometryBase>();
      for (var index = 0; index < planetPlanes.Count; index++)
      {
        var geometry = GearGeometryGenerator.CreateGearGeometry(
          doc, planetParameters, planetPlanes[index], out warning);
        planetGeometries.Add(geometry);
        AddWarning(warnings, "行星轮" + (index + 1), warning);
      }
      var carrierGeometry = PlanetaryGearSetGenerator.CreateCarrierGeometry(
        doc, parameters, centerPlane, out warning);
      AddWarning(warnings, "行星架", warning);
      if (sunGeometry == null || ringGeometry == null || carrierGeometry == null ||
          planetGeometries.Any(geometry => geometry == null))
      {
        RhinoApp.WriteLine("ProductMotion：行星齿轮组几何生成失败；{0}。", string.Join("；", warnings));
        return Result.Failure;
      }

      using (TimelineEngine.BeginUndoScope(doc, "生成 ProductMotion 行星齿轮组"))
      {
        var sun = CreateTrack(
          doc, "行星太阳轮_" + parameters.SunTeeth + "T", sunGeometry,
          sunParameters, centerPlane);
        var carrier = CreateTrack(
          doc, "行星架_" + parameters.PlanetCount + "P", carrierGeometry,
          null, centerPlane);
        var planets = new List<AnimationTrack>();
        for (var index = 0; index < planetGeometries.Count; index++)
        {
          planets.Add(CreateTrack(
            doc,
            "行星轮_" + (index + 1) + "_" + parameters.PlanetTeeth + "T",
            planetGeometries[index],
            planetParameters,
            planetPlanes[index]));
        }
        var ring = CreateTrack(
          doc, "行星内齿圈_" + parameters.RingTeeth + "T", ringGeometry,
          ringParameters, ringPlane);
        if (sun == null || carrier == null || ring == null || planets.Any(track => track == null))
        {
          RhinoApp.WriteLine("ProductMotion：几何已生成，但行星齿轮时间轴轨道建立失败；请撤销本次生成后重试。");
          return Result.Failure;
        }

        if (parameters.FixedMember != PlanetaryFixedMember.GeometryOnly)
          ConfigureMotion(doc, parameters, sun, planets, ring, carrier);
        var input = parameters.FixedMember == PlanetaryFixedMember.Sun ? ring : sun;
        TimelineEngine.SelectTrack(doc, input.Id);
        TimelineEngine.Persist(doc);
        TimelineEngine.ApplyFrame(doc, TimelineEngine.Model(doc).CurrentFrame, false);
      }

      TimelineDocking.Open();
      RhinoApp.WriteLine(string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        "ProductMotion：已生成太阳轮 {0}T、{1} 个行星轮 {2}T、内齿圈 {3}T 和行星架。",
        parameters.SunTeeth, parameters.PlanetCount, parameters.PlanetTeeth, parameters.RingTeeth));
      RhinoApp.WriteLine("ProductMotion：{0}。", validation.Message);
      RhinoApp.WriteLine("ProductMotion：{0}。", PlanetaryGearSetGenerator.TransmissionDescription(parameters));
      if (Math.Min(parameters.SunTeeth, parameters.PlanetTeeth) < 17 &&
          parameters.PressureAngleDegrees >= 19.0 && parameters.PressureAngleDegrees <= 21.0)
        RhinoApp.WriteLine("ProductMotion：提示：20°标准齿形少于 17 齿时可能根切，请确认是否需要变位齿形。");
      if (warnings.Count > 0)
        RhinoApp.WriteLine("ProductMotion：生成提示：{0}。", string.Join("；", warnings));
      return Result.Success;
    }

    private static GearParameters GearParameters(
      PlanetaryGearSetParameters source,
      GearPartType type,
      int teeth)
    {
      return new GearParameters
      {
        Type = type,
        Teeth = teeth,
        Module = source.Module,
        PressureAngleDegrees = source.PressureAngleDegrees,
        Thickness = source.GearThickness,
        BoreDiameter = source.BoreDiameter,
        OutputPitchReference = source.OutputPitchReferences
      };
    }

    private static AnimationTrack CreateTrack(
      RhinoDoc doc,
      string name,
      GeometryBase geometry,
      GearParameters gearParameters,
      Plane plane)
    {
      var geometries = new List<GeometryBase> { geometry };
      if (gearParameters != null && gearParameters.OutputPitchReference)
      {
        var reference = GearGeometryGenerator.CreatePitchReference(gearParameters, plane);
        if (reference != null)
          geometries.Add(reference);
      }
      var instance = TrackFactory.CreateGeneratedPart(doc, geometries, name, gearParameters);
      var track = instance == null ? null : TimelineEngine.AddTrack(doc, instance);
      if (track != null)
        TimelineEngine.SetPivotPlane(doc, track, plane);
      return track;
    }

    private static void ConfigureMotion(
      RhinoDoc doc,
      PlanetaryGearSetParameters parameters,
      AnimationTrack sun,
      IEnumerable<AnimationTrack> planets,
      AnimationTrack ring,
      AnimationTrack carrier)
    {
      if (parameters.FixedMember == PlanetaryFixedMember.Carrier)
      {
        TimelineEngine.AddMechanicalConstraint(
          doc, sun.Id, ring.Id, MechanicalConstraintType.PlanetaryRingFixedCarrier,
          parameters.SunTeeth, parameters.RingTeeth, 0.0,
          parameters.Module, parameters.PressureAngleDegrees,
          0.0, RotationAxis.X, 1.0, parameters.PlanetTeeth);
        foreach (var planet in planets)
        {
          TimelineEngine.AddMechanicalConstraint(
            doc, sun.Id, planet.Id, MechanicalConstraintType.ExternalGear,
            parameters.SunTeeth, parameters.PlanetTeeth, 0.0,
            parameters.Module, parameters.PressureAngleDegrees);
        }
        return;
      }

      var input = parameters.FixedMember == PlanetaryFixedMember.Sun ? ring : sun;
      var inputTeeth = parameters.FixedMember == PlanetaryFixedMember.Sun
        ? parameters.RingTeeth
        : parameters.SunTeeth;
      var fixedTeeth = parameters.FixedMember == PlanetaryFixedMember.Sun
        ? parameters.SunTeeth
        : parameters.RingTeeth;
      var planetType = parameters.FixedMember == PlanetaryFixedMember.Sun
        ? MechanicalConstraintType.PlanetaryPlanetInternalInput
        : MechanicalConstraintType.PlanetaryPlanetExternalInput;
      TimelineEngine.AddMechanicalConstraint(
        doc, input.Id, carrier.Id, MechanicalConstraintType.PlanetaryCarrier,
        inputTeeth, fixedTeeth, 0.0,
        parameters.Module, parameters.PressureAngleDegrees);
      foreach (var planet in planets)
      {
        TimelineEngine.SetParent(doc, planet.Id, carrier.Id);
        TimelineEngine.AddMechanicalConstraint(
          doc, input.Id, planet.Id, planetType,
          inputTeeth, parameters.PlanetTeeth, 0.0,
          parameters.Module, parameters.PressureAngleDegrees,
          0.0, RotationAxis.X, 1.0, fixedTeeth);
      }
    }

    private static PlanetaryFixedMember? GetFixedMember()
    {
      var getter = new GetOption();
      getter.SetCommandPrompt("选择固定件/输入输出预设（回车=固定内齿圈）");
      getter.AcceptNothing(true);
      var ring = getter.AddOption(new LocalizeStringPair("RingFixed", "固定内齿圈_太阳轮输入_行星架输出"));
      var sun = getter.AddOption(new LocalizeStringPair("SunFixed", "固定太阳轮_内齿圈输入_行星架输出"));
      var carrier = getter.AddOption(new LocalizeStringPair("CarrierFixed", "固定行星架_太阳轮输入_内齿圈输出"));
      var geometry = getter.AddOption(new LocalizeStringPair("GeometryOnly", "只生成几何和轨道"));
      var result = getter.Get();
      if (result == GetResult.Cancel)
        return null;
      if (result != GetResult.Option)
        return PlanetaryFixedMember.Ring;
      if (getter.OptionIndex() == sun) return PlanetaryFixedMember.Sun;
      if (getter.OptionIndex() == carrier) return PlanetaryFixedMember.Carrier;
      if (getter.OptionIndex() == geometry) return PlanetaryFixedMember.GeometryOnly;
      return getter.OptionIndex() == ring ? PlanetaryFixedMember.Ring : PlanetaryFixedMember.Ring;
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

    private static void AddWarning(ICollection<string> warnings, string part, string warning)
    {
      if (warnings != null && !string.IsNullOrWhiteSpace(warning))
        warnings.Add(part + "：" + warning);
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

  public sealed class CreatePlanetaryGearSetCommand : Command
  {
    public override string EnglishName => "PMTCreatePlanetaryGearSet";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      PlanetaryGearSetCommandRunner.Run(doc);
  }
}
