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
  public sealed class ArcFlowDrawCommand : Command
  {
    public override string EnglishName => "ArcFlowDraw";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      Point3d start;
      var result = RhinoGet.GetPoint("圆弧链起点", false, out start);
      if (result != Result.Success)
        return result;

      var tangentGetter = new GetPoint();
      tangentGetter.SetCommandPrompt("指定起始切线方向");
      tangentGetter.SetBasePoint(start, true);
      tangentGetter.DrawLineFromPoint(start, true);
      var tangentResult = tangentGetter.Get();
      if (tangentResult != GetResult.Point)
        return Result.Cancel;

      var tangent = tangentGetter.Point() - start;
      if (!tangent.Unitize())
        return Result.Failure;

      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance * 10.0);
      var current = start;
      var arcs = new List<Arc>();

      while (true)
      {
        var getter = new GetPoint();
        getter.SetCommandPrompt("指定下一圆弧端点；按 Enter 完成");
        getter.SetBasePoint(current, true);
        getter.AcceptNothing(true);
        getter.DynamicDraw += (sender, args) =>
        {
          foreach (var existing in arcs)
            args.Display.DrawArc(existing, Color.FromArgb(80, 220, 255), 2);
          Arc preview;
          if (ArcChainBuilder.TryCreateTangentArc(current, tangent, args.CurrentPoint, tolerance, out preview))
            args.Display.DrawArc(preview, Color.Gold, 3);
        };

        var getResult = getter.Get();
        if (getResult == GetResult.Nothing)
          break;
        if (getResult != GetResult.Point)
          return Result.Cancel;

        Arc arc;
        if (!ArcChainBuilder.TryCreateTangentArc(current, tangent, getter.Point(), tolerance, out arc))
        {
          RhinoApp.WriteLine("该端点无法形成有效圆弧，请换一个位置。");
          continue;
        }
        if (!ArcChainBuilder.TryAppendG1(arcs, arc, tolerance))
        {
          RhinoApp.WriteLine("该圆弧与上一段未达到严格 G1/控制点共线条件，请换一个端点。");
          continue;
        }
        current = arc.EndPoint;
        tangent = arc.TangentAt(arc.AngleDomain.T1);
        tangent.Unitize();
      }

      if (arcs.Count == 0)
        return Result.Nothing;
      ArcChainBuilder.AddToDocument(doc, arcs, "ArcFlow_Draw_" + DateTime.Now.ToString("HHmmss"));
      RhinoApp.WriteLine("ArcFlow：已生成 {0} 段独立真圆弧，接点保持 G1 相切。", arcs.Count);
      return Result.Success;
    }
  }
}
