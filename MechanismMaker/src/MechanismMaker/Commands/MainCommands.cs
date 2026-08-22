using System;
using MechanismMaker.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace MechanismMaker.Commands
{
  public sealed class MechanismMakerCommand : Command
  {
    public override string EnglishName => "MechanismMaker";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var getter = new GetOption();
      getter.SetCommandPrompt("选择要生成的木质机械机构");
      var gear = getter.AddOption("Gear");
      var rack = getter.AddOption("Rack");
      var cam = getter.AddOption("Cam");
      var crank = getter.AddOption("Crank");
      var fourBar = getter.AddOption("FourBar");
      var ratchet = getter.AddOption("Ratchet");
      var geneva = getter.AddOption("Geneva");
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      string command;
      if (getter.OptionIndex() == gear) command = "_MMGear";
      else if (getter.OptionIndex() == rack) command = "_MMRack";
      else if (getter.OptionIndex() == cam) command = "_MMCam";
      else if (getter.OptionIndex() == crank) command = "_MMCrank";
      else if (getter.OptionIndex() == fourBar) command = "_MMFourBar";
      else if (getter.OptionIndex() == ratchet) command = "_MMRatchet";
      else if (getter.OptionIndex() == geneva) command = "_MMGeneva";
      else return Result.Cancel;

      RhinoApp.RunScript(command, false);
      return Result.Success;
    }
  }

  public sealed class MechanismMakerSettingsCommand : Command
  {
    public override string EnglishName => "MMSettings";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var current = MechanismMakerPlugin.CurrentSettings;
      var board = current.BoardThicknessMm;
      var fixedHole = current.FixedHoleMm;
      var rotatingHole = current.RotatingHoleMm;
      var guideHole = current.GuideHoleMm;
      var module = current.DefaultModuleMm;
      var pressure = current.PressureAngleDegrees;
      var backlash = current.BacklashMm;
      var slotClearance = current.SlotClearanceMm;

      if (!CommandHelpers.AskNumber("木板厚度（mm）", ref board, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("2mm钢轴固定孔直径（mm）", ref fixedHole, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("2mm钢轴活动孔直径（mm）", ref rotatingHole, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("滑动导向孔/槽宽（mm）", ref guideHole, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("默认齿轮模数（mm）", ref module, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("压力角（度）", ref pressure, 5.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("齿轮侧隙（mm）", ref backlash, 0.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("日内瓦槽附加间隙（mm）", ref slotClearance, 0.0)) return Result.Cancel;

      current.BoardThicknessMm = board;
      current.FixedHoleMm = fixedHole;
      current.RotatingHoleMm = rotatingHole;
      current.GuideHoleMm = guideHole;
      current.DefaultModuleMm = module;
      current.PressureAngleDegrees = pressure;
      current.BacklashMm = backlash;
      current.SlotClearanceMm = slotClearance;
      RhinoApp.WriteLine(
        "MechanismMaker 参数：板厚 {0:0.###}mm，固定孔 Ø{1:0.###}，活动孔 Ø{2:0.###}，导向孔 Ø{3:0.###}。",
        board, fixedHole, rotatingHole, guideHole);
      return Result.Success;
    }
  }

  public sealed class MechanismMakerHelpCommand : Command
  {
    public override string EnglishName => "MMHelp";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      RhinoApp.WriteLine("MechanismMaker 1.0 命令：");
      RhinoApp.WriteLine("  MechanismMaker - 机构选择入口");
      RhinoApp.WriteLine("  MMGear / MMRack / MMCam / MMCrank");
      RhinoApp.WriteLine("  MMFourBar / MMRatchet / MMGeneva");
      RhinoApp.WriteLine("  MMSettings - 板厚、孔径、模数、侧隙参数");
      RhinoApp.WriteLine("生成结果为1:1封闭激光轮廓，每个零件单独打组并写入 MM.* 机械元数据。");
      return Result.Success;
    }
  }

  internal static class CommandHelpers
  {
    public static bool AskNumber(string prompt, ref double value, double minimum)
    {
      var getter = new GetNumber();
      getter.SetCommandPrompt(prompt);
      getter.SetDefaultNumber(value);
      getter.SetLowerLimit(minimum, false);
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return false;
      value = getter.Number();
      return true;
    }

    public static bool AskInteger(string prompt, ref int value, int minimum)
    {
      var getter = new GetInteger();
      getter.SetCommandPrompt(prompt);
      getter.SetDefaultInteger(value);
      getter.SetLowerLimit(minimum, false);
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return false;
      value = getter.Number();
      return true;
    }

    public static bool GetPlacementPlane(RhinoDoc doc, string prompt, out Plane plane)
    {
      plane = Plane.Unset;
      var pointGetter = new GetPoint();
      pointGetter.SetCommandPrompt(prompt);
      pointGetter.Get();
      if (pointGetter.CommandResult() != Result.Success)
        return false;

      var view = pointGetter.View() ?? doc.Views.ActiveView;
      if (view == null)
        return false;
      var constructionPlane = view.ActiveViewport.ConstructionPlane();
      constructionPlane.Origin = pointGetter.Point();
      plane = constructionPlane;
      return plane.IsValid;
    }

    public static Result AddPart(RhinoDoc doc, GeneratedPart part, Plane placement)
    {
      var assembly = new MechanismAssembly(part.Type);
      assembly.Parts.Add(part);
      var ids = OutputBuilder.AddAssembly(doc, assembly, placement, MechanismMakerPlugin.CurrentSettings);
      return ids.Count > 0 ? Result.Success : Result.Failure;
    }
  }
}
