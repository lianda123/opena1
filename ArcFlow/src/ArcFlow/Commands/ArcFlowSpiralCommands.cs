using System;
using System.Collections.Generic;
using System.Drawing;
using ArcFlow.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ArcFlow.Commands
{
  internal static class SpiralCommandRunner
  {
    public static Result Run(RhinoDoc doc, SpiralKind kind)
    {
      var startRadius = 5.0;
      var turns = 2.0;
      var growth = DefaultGrowth(kind, startRadius);
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, 0.02);

      var result = RhinoGet.GetNumber("起始半径", false, ref startRadius);
      if (result != Result.Success)
        return result;
      if (startRadius <= tolerance)
      {
        RhinoApp.WriteLine("起始半径必须大于建模公差。");
        return Result.Failure;
      }

      result = RhinoGet.GetNumber("圈数", false, ref turns);
      if (result != Result.Success)
        return result;
      if (turns <= 0.0 || turns > 100.0)
      {
        RhinoApp.WriteLine("圈数应在 0 到 100 之间。");
        return Result.Failure;
      }

      if (kind == SpiralKind.Archimedean || kind == SpiralKind.Fermat)
      {
        result = RhinoGet.GetNumber("每圈半径增长量", false, ref growth);
        if (result != Result.Success)
          return result;
      }
      else if (kind == SpiralKind.Logarithmic)
      {
        result = RhinoGet.GetNumber("每圈半径倍率（例如 1.618）", false, ref growth);
        if (result != Result.Success)
          return result;
        if (growth <= 0.0)
          return Result.Failure;
      }

      if (kind == SpiralKind.Archimedean || kind == SpiralKind.Logarithmic || kind == SpiralKind.Fermat)
      {
        result = RhinoGet.GetNumber("圆弧拟合公差", false, ref tolerance);
        if (result != Result.Success)
          return result;
        if (tolerance <= 0.0)
          return Result.Failure;
      }

      var clockwise = new OptionToggle(false, "CounterClockwise", "Clockwise");
      var directionGetter = new GetOption();
      directionGetter.SetCommandPrompt("旋转方向；按 Enter 接受");
      directionGetter.AddOptionToggle("Direction", ref clockwise);
      directionGetter.AcceptNothing(true);
      while (true)
      {
        var getResult = directionGetter.Get();
        if (getResult == GetResult.Nothing)
          break;
        if (getResult != GetResult.Option)
          return Result.Cancel;
      }

      var settings = new SpiralSettings
      {
        Kind = kind,
        StartRadius = startRadius,
        Turns = turns,
        Growth = growth,
        Tolerance = tolerance,
        Clockwise = clockwise.CurrentValue
      };

      var cplane = doc.Views.ActiveView.ActiveViewport.ConstructionPlane();
      var previewArcs = SpiralFactory.Create(cplane, settings);
      var originGetter = new GetPoint();
      originGetter.SetCommandPrompt("指定螺旋中心；按 Enter 使用当前工作平面原点");
      originGetter.SetBasePoint(cplane.Origin, true);
      originGetter.AcceptNothing(true);
      originGetter.DynamicDraw += (sender, args) =>
      {
        var translation = Transform.Translation(args.CurrentPoint - cplane.Origin);
        foreach (var source in previewArcs)
        {
          var moved = source;
          moved.Transform(translation);
          args.Display.DrawArc(moved, Color.FromArgb(80, 220, 255), 2);
        }
      };

      var originResult = originGetter.Get();
      if (originResult != GetResult.Point && originResult != GetResult.Nothing)
        return Result.Cancel;
      var origin = originResult == GetResult.Point ? originGetter.Point() : cplane.Origin;
      var plane = new Plane(origin, cplane.XAxis, cplane.YAxis);
      var arcs = SpiralFactory.Create(plane, settings);
      if (arcs.Count == 0)
        return Result.Failure;

      var groupName = "ArcFlow_" + kind + "_" + DateTime.Now.ToString("HHmmss");
      ArcChainBuilder.AddToDocument(doc, arcs, groupName);
      RhinoApp.WriteLine("ArcFlow {0}：已生成 {1} 段独立真圆弧。", kind, arcs.Count);
      return Result.Success;
    }

    private static double DefaultGrowth(SpiralKind kind, double radius)
    {
      if (kind == SpiralKind.Logarithmic)
        return 1.618033988749895;
      return Math.Max(radius, 1.0);
    }
  }

  public sealed class ArcFlowSpiralCommand : Command
  {
    public override string EnglishName => "ArcFlowSpiral";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var choices = new List<string> { "Fibonacci", "Golden", "Archimedean", "Logarithmic", "Fermat" };
      var getter = new GetOption();
      getter.SetCommandPrompt("选择螺旋类型");
      getter.AddOptionList("Type", choices, 1);
      getter.AcceptNothing(true);
      var result = getter.Get();
      if (result == GetResult.Nothing)
        return SpiralCommandRunner.Run(doc, SpiralKind.Golden);
      if (result != GetResult.Option)
        return Result.Cancel;
      return SpiralCommandRunner.Run(doc, (SpiralKind)getter.Option().CurrentListOptionIndex);
    }
  }

  public sealed class ArcFlowFibonacciCommand : Command
  {
    public override string EnglishName => "ArcFlowFibonacci";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SpiralCommandRunner.Run(doc, SpiralKind.Fibonacci);
  }

  public sealed class ArcFlowGoldenCommand : Command
  {
    public override string EnglishName => "ArcFlowGolden";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SpiralCommandRunner.Run(doc, SpiralKind.Golden);
  }

  public sealed class ArcFlowArchimedeanCommand : Command
  {
    public override string EnglishName => "ArcFlowArchimedean";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SpiralCommandRunner.Run(doc, SpiralKind.Archimedean);
  }

  public sealed class ArcFlowLogarithmicCommand : Command
  {
    public override string EnglishName => "ArcFlowLogarithmic";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SpiralCommandRunner.Run(doc, SpiralKind.Logarithmic);
  }

  public sealed class ArcFlowFermatCommand : Command
  {
    public override string EnglishName => "ArcFlowFermat";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SpiralCommandRunner.Run(doc, SpiralKind.Fermat);
  }
}
