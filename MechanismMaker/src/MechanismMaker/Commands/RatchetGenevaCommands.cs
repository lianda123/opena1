using System;
using MechanismMaker.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;

namespace MechanismMaker.Commands
{
  public sealed class RatchetCommand : Command
  {
    public override string EnglishName => "MMRatchet";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = MechanismMakerPlugin.CurrentSettings;
      var teeth = 18;
      var rootRadius = 18.0;
      var toothHeight = 3.0;
      var bore = settings.RotatingHoleMm;
      var pawlWidth = 6.0;
      var pawlHole = settings.RotatingHoleMm;
      if (!CommandHelpers.AskInteger("棘轮齿数", ref teeth, 6)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("棘轮根圆半径（mm）", ref rootRadius, 2.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("棘齿高度（mm）", ref toothHeight, 0.5)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("棘轮中心活动孔直径（mm）", ref bore, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("棘爪宽度（mm）", ref pawlWidth, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("棘爪转轴孔直径（mm）", ref pawlHole, 0.1)) return Result.Cancel;

      Plane plane;
      if (!CommandHelpers.GetPlacementPlane(doc, "指定棘轮轴心", out plane)) return Result.Cancel;
      var assembly = GeometryFactory.CreateRatchet(teeth, rootRadius, toothHeight, bore, pawlWidth, pawlHole);
      var ids = OutputBuilder.AddAssembly(doc, assembly, plane, settings);
      RhinoApp.WriteLine("棘轮每齿分度角：{0:0.###}°。棘爪已作为独立组生成，可单独移动和卡关键帧。", 360.0 / teeth);
      return ids.Count > 0 ? Result.Success : Result.Failure;
    }
  }

  public sealed class GenevaCommand : Command
  {
    public override string EnglishName => "MMGeneva";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = MechanismMakerPlugin.CurrentSettings;
      var slots = 6;
      var centerDistance = 35.0;
      var pinDiameter = 2.0;
      if (!CommandHelpers.AskInteger("日内瓦槽数", ref slots, 3)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("主动轴与从动轴中心距（mm）", ref centerDistance, 5.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("驱动销直径（mm）", ref pinDiameter, 0.5)) return Result.Cancel;

      Plane plane;
      if (!CommandHelpers.GetPlacementPlane(doc, "指定日内瓦从动槽轮轴心", out plane)) return Result.Cancel;
      var slotWidth = pinDiameter + settings.SlotClearanceMm;
      var unitsPerMm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      var toleranceMm = Math.Max(0.001, doc.ModelAbsoluteTolerance / Math.Max(unitsPerMm, 1e-12));
      try
      {
        var assembly = GeometryFactory.CreateGeneva(
          slots,
          centerDistance,
          slotWidth,
          settings.RotatingHoleMm,
          settings.FixedHoleMm,
          toleranceMm);
        var ids = OutputBuilder.AddAssembly(doc, assembly, plane, settings);
        RhinoApp.WriteLine(
          "日内瓦机构每次分度 {0:0.###}°；槽宽 {1:0.###}mm（驱动销 {2:0.###} + 间隙 {3:0.###}）。",
          360.0 / slots, slotWidth, pinDiameter, settings.SlotClearanceMm);
        return ids.Count > 0 ? Result.Success : Result.Failure;
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("MechanismMaker 日内瓦机构生成失败：{0}", exception.Message);
        return Result.Failure;
      }
    }
  }
}
