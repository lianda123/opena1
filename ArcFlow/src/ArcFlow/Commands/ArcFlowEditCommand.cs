using System;
using System.Drawing;
using ArcFlow.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ArcFlow.Commands
{
  public sealed class ArcFlowEditCommand : Command
  {
    public override string EnglishName => "ArcFlowEdit";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var objectGetter = new GetObject();
      objectGetter.SetCommandPrompt("选择要调整端点的单段圆弧");
      objectGetter.GeometryFilter = Rhino.DocObjects.ObjectType.Curve;
      objectGetter.SubObjectSelect = false;
      objectGetter.Get();
      if (objectGetter.CommandResult() != Result.Success)
        return objectGetter.CommandResult();

      var curve = objectGetter.Object(0).Curve();
      Arc source;
      if (curve == null || !curve.TryGetArc(out source, doc.ModelAbsoluteTolerance))
      {
        RhinoApp.WriteLine("所选对象不是圆弧。");
        return Result.Failure;
      }

      var start = source.StartPoint;
      var tangent = source.TangentAt(source.AngleDomain.T0);
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance * 10.0);
      var pointGetter = new GetPoint();
      pointGetter.SetCommandPrompt("指定新的圆弧端点");
      pointGetter.SetBasePoint(start, true);
      pointGetter.DynamicDraw += (sender, args) =>
      {
        Arc preview;
        if (ArcChainBuilder.TryCreateTangentArc(start, tangent, args.CurrentPoint, tolerance, out preview))
          args.Display.DrawArc(preview, Color.Gold, 3);
      };
      if (pointGetter.Get() != GetResult.Point)
        return Result.Cancel;

      Arc replacement;
      if (!ArcChainBuilder.TryCreateTangentArc(start, tangent, pointGetter.Point(), tolerance, out replacement))
        return Result.Failure;
      if (!doc.Objects.Replace(objectGetter.Object(0).ObjectId, new ArcCurve(replacement), false))
        return Result.Failure;
      doc.Views.Redraw();
      RhinoApp.WriteLine("ArcFlowEdit：已移动端点，并保持圆弧起点切线不变。");
      return Result.Success;
    }
  }
}
