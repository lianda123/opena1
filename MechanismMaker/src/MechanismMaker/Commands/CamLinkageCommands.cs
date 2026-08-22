using System;
using System.Linq;
using MechanismMaker.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace MechanismMaker.Commands
{
  public sealed class CamCommand : Command
  {
    public override string EnglishName => "MMCam";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var kindGetter = new GetOption();
      kindGetter.SetCommandPrompt("选择凸轮类型");
      var eccentric = kindGetter.AddOption("Eccentric");
      var pear = kindGetter.AddOption("Pear");
      var heart = kindGetter.AddOption("Heart");
      var snail = kindGetter.AddOption("Snail");
      kindGetter.Get();
      if (kindGetter.CommandResult() != Result.Success)
        return kindGetter.CommandResult();

      var kind = CamKind.Eccentric;
      if (kindGetter.OptionIndex() == pear) kind = CamKind.Pear;
      else if (kindGetter.OptionIndex() == heart) kind = CamKind.Heart;
      else if (kindGetter.OptionIndex() == snail) kind = CamKind.Snail;
      else if (kindGetter.OptionIndex() != eccentric) return Result.Cancel;

      var baseRadius = 10.0;
      var lift = 6.0;
      var bore = MechanismMakerPlugin.CurrentSettings.RotatingHoleMm;
      if (!CommandHelpers.AskNumber("凸轮基圆半径（mm）", ref baseRadius, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("从动件最大升程（mm）", ref lift, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("中心孔直径（mm）", ref bore, 0.1)) return Result.Cancel;

      Plane plane;
      if (!CommandHelpers.GetPlacementPlane(doc, "指定凸轮轴心", out plane)) return Result.Cancel;
      var part = GeometryFactory.CreateCam(kind, baseRadius, lift, bore);
      RhinoApp.WriteLine("已生成 {0} 凸轮，理论升程 {1:0.###} mm。", kind, lift);
      return CommandHelpers.AddPart(doc, part, plane);
    }
  }

  public sealed class CrankCommand : Command
  {
    public override string EnglishName => "MMCrank";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = MechanismMakerPlugin.CurrentSettings;
      var throwDistance = 15.0;
      var armWidth = 6.0;
      var shaftHole = settings.RotatingHoleMm;
      var pinHole = settings.FixedHoleMm;
      if (!CommandHelpers.AskNumber("曲柄半径/偏心距（mm）", ref throwDistance, 0.5)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("曲柄臂宽度（mm）", ref armWidth, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("中心活动孔直径（mm）", ref shaftHole, 0.1)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("连杆销固定孔直径（mm）", ref pinHole, 0.1)) return Result.Cancel;

      Plane plane;
      if (!CommandHelpers.GetPlacementPlane(doc, "指定曲柄轴心", out plane)) return Result.Cancel;
      var part = GeometryFactory.CreateCrank(throwDistance, armWidth, shaftHole, pinHole);
      RhinoApp.WriteLine("曲柄完整往复行程约为 {0:0.###} mm。", throwDistance * 2.0);
      return CommandHelpers.AddPart(doc, part, plane);
    }
  }

  public sealed class FourBarCommand : Command
  {
    public override string EnglishName => "MMFourBar";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var ground = 40.0;
      var input = 15.0;
      var coupler = 35.0;
      var rocker = 28.0;
      var inputAngle = 45.0;
      var width = 6.0;
      if (!CommandHelpers.AskNumber("机架两轴中心距（mm）", ref ground, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("主动曲柄长度（mm）", ref input, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("中间连杆长度（mm）", ref coupler, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("摇杆长度（mm）", ref rocker, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("初始主动曲柄角度（度）", ref inputAngle, -360.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("连杆板宽度（mm）", ref width, 1.0)) return Result.Cancel;

      Plane plane;
      if (!CommandHelpers.GetPlacementPlane(doc, "指定四连杆机架左轴心", out plane)) return Result.Cancel;
      try
      {
        var settings = MechanismMakerPlugin.CurrentSettings;
        var assembly = GeometryFactory.CreateFourBar(
          ground, input, coupler, rocker, inputAngle, width,
          settings.FixedHoleMm, settings.RotatingHoleMm);
        var ids = OutputBuilder.AddAssembly(doc, assembly, plane, settings);

        var lengths = new[] { ground, input, coupler, rocker }.OrderBy(value => value).ToArray();
        var grashof = lengths[0] + lengths[3] <= lengths[1] + lengths[2] + 1e-9;
        RhinoApp.WriteLine(grashof
          ? "四连杆满足 Grashof 条件，至少有一根杆可能连续整周转动；仍需用时间轴检查实际装配分支。"
          : "四连杆不满足 Grashof 条件，通常只能摇摆，不能让最短杆连续整周转动。");
        return ids.Count > 0 ? Result.Success : Result.Failure;
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("MechanismMaker 四连杆生成失败：{0}", exception.Message);
        return Result.Failure;
      }
    }
  }
}
