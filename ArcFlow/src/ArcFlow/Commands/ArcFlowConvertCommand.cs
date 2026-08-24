using System;
using System.Collections.Generic;
using ArcFlow.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ArcFlow.Commands
{
  public sealed class ArcFlowConvertCommand : Command
  {
    public override string EnglishName => "ArcFlowConvert";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var getter = new GetObject();
      getter.SetCommandPrompt("选择要转换为真圆弧链的曲线");
      getter.GeometryFilter = Rhino.DocObjects.ObjectType.Curve;
      getter.SubObjectSelect = false;
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      var curve = getter.Object(0).Curve();
      if (curve == null)
        return Result.Failure;

      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, 0.02);
      var result = RhinoGet.GetNumber("拟合公差", false, ref tolerance);
      if (result != Result.Success)
        return result;
      if (tolerance <= 0.0)
        return Result.Failure;

      var length = curve.GetLength();
      var characteristic = Math.Max(tolerance * 4.0, Math.Sqrt(Math.Max(tolerance * length, tolerance * tolerance)));
      var segmentCount = Math.Max(4, Math.Min(512, (int)Math.Ceiling(length / characteristic)));
      var parameters = curve.DivideByCount(segmentCount, true);
      if (parameters == null || parameters.Length < 2)
        return Result.Failure;

      var points = new List<Point3d>();
      var tangents = new List<Vector3d>();
      foreach (var parameter in parameters)
      {
        var tangent = curve.TangentAt(parameter);
        if (!tangent.Unitize())
          continue;
        points.Add(curve.PointAt(parameter));
        tangents.Add(tangent);
      }

      var arcs = ArcChainBuilder.FromSamples(points, tangents, Math.Max(tolerance * 0.1, RhinoMath.ZeroTolerance));
      if (arcs.Count == 0)
        return Result.Failure;

      ArcChainBuilder.AddToDocument(doc, arcs, "ArcFlow_Convert_" + DateTime.Now.ToString("HHmmss"));
      RhinoApp.WriteLine("ArcFlowConvert：原曲线已保留，另生成 {0} 段独立真圆弧。", arcs.Count);
      return Result.Success;
    }
  }
}
