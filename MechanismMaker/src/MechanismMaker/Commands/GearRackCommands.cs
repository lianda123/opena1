using System;
using MechanismMaker.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;

namespace MechanismMaker.Commands
{
  public sealed class GearCommand : Command
  {
    public override string EnglishName => "MMGear";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = MechanismMakerPlugin.CurrentSettings;
      var module = settings.DefaultModuleMm;
      var teeth = 24;
      var backlash = settings.BacklashMm;
      var bore = settings.RotatingHoleMm;
      if (!CommandHelpers.AskNumber("齿轮模数（mm）", ref module, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskInteger("齿数", ref teeth, 6)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("齿侧隙（mm）", ref backlash, 0.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("中心孔直径（mm）", ref bore, 0.1)) return Result.Cancel;

      Plane plane;
      if (!CommandHelpers.GetPlacementPlane(doc, "指定齿轮轴心", out plane)) return Result.Cancel;
      try
      {
        var part = GeometryFactory.CreateGear(module, teeth, settings.PressureAngleDegrees, backlash, bore);
        RhinoApp.WriteLine("齿轮节圆直径：{0:0.###} mm；与同模数齿轮啮合时，中心距=(Z1+Z2)×m÷2。", module * teeth);
        return CommandHelpers.AddPart(doc, part, plane);
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("MechanismMaker 齿轮生成失败：{0}", exception.Message);
        return Result.Failure;
      }
    }
  }

  public sealed class RackCommand : Command
  {
    public override string EnglishName => "MMRack";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = MechanismMakerPlugin.CurrentSettings;
      var module = settings.DefaultModuleMm;
      var teeth = 20;
      var bodyHeight = 8.0;
      var backlash = settings.BacklashMm;
      if (!CommandHelpers.AskNumber("齿条模数（mm）", ref module, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskInteger("齿数", ref teeth, 2)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("齿条背板高度（mm）", ref bodyHeight, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("齿侧隙（mm）", ref backlash, 0.0)) return Result.Cancel;

      Plane plane;
      if (!CommandHelpers.GetPlacementPlane(doc, "指定齿条左下起点", out plane)) return Result.Cancel;
      var part = GeometryFactory.CreateRack(module, teeth, settings.PressureAngleDegrees, backlash, bodyHeight);
      RhinoApp.WriteLine("齿条节距：{0:0.###} mm；与模数 {1:0.###} 的齿轮配合。", Math.PI * module, module);
      return CommandHelpers.AddPart(doc, part, plane);
    }
  }
}
