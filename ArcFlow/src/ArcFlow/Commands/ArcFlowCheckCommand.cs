using System;
using System.Collections.Generic;
using ArcFlow.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace ArcFlow.Commands
{
  public sealed class ArcFlowCheckCommand : Command
  {
    public override string EnglishName => "ArcFlowCheck";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var getter = new GetObject();
      getter.SetCommandPrompt("按顺序选择要检查的圆弧链");
      getter.GeometryFilter = Rhino.DocObjects.ObjectType.Curve;
      getter.GroupSelect = true;
      getter.SubObjectSelect = false;
      getter.GetMultiple(1, 0);
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      var arcs = new List<Arc>();
      var nonArcCount = 0;
      for (var i = 0; i < getter.ObjectCount; i++)
      {
        var curve = getter.Object(i).Curve();
        Arc arc;
        if (curve != null && curve.TryGetArc(out arc, doc.ModelAbsoluteTolerance))
          arcs.Add(arc);
        else
          nonArcCount++;
      }

      if (nonArcCount > 0)
      {
        RhinoApp.WriteLine("ArcFlowCheck：发现 {0} 个非圆弧对象。", nonArcCount);
        return Result.Failure;
      }

      var gapTolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance * 10.0);
      var angleTolerance = Math.Max(doc.ModelAngleToleranceRadians, RhinoMath.ToRadians(0.05));
      var maxGap = 0.0;
      var maxAngle = 0.0;
      var maxControlPointAngle = 0.0;
      var maxControlPointLineDeviation = 0.0;
      var betweenFailures = 0;
      var failures = 0;

      for (var i = 0; i < arcs.Count - 1; i++)
      {
        var next = arcs[i + 1];
        var directGap = arcs[i].EndPoint.DistanceTo(next.StartPoint);
        var reverseGap = arcs[i].EndPoint.DistanceTo(next.EndPoint);
        if (reverseGap < directGap)
        {
          next.Reverse();
          arcs[i + 1] = next;
          directGap = reverseGap;
        }

        ArcJoinMetrics metrics;
        if (!ArcChainBuilder.TryMeasureJoin(arcs[i], arcs[i + 1], out metrics))
        {
          failures++;
          continue;
        }

        maxGap = Math.Max(maxGap, metrics.Gap);
        maxAngle = Math.Max(maxAngle, metrics.TangentAngleRadians);
        maxControlPointAngle = Math.Max(maxControlPointAngle, metrics.ControlPointAngleRadians);
        maxControlPointLineDeviation = Math.Max(maxControlPointLineDeviation, metrics.ControlPointLineDeviation);
        if (!metrics.JoinIsBetweenControlPoints)
          betweenFailures++;
        if (metrics.Gap > gapTolerance ||
          metrics.TangentAngleRadians > angleTolerance ||
          metrics.ControlPointAngleRadians > angleTolerance ||
          metrics.ControlPointLineDeviation > gapTolerance ||
          !metrics.JoinIsBetweenControlPoints)
          failures++;
      }

      RhinoApp.WriteLine("ArcFlowCheck：{0} 段真圆弧；最大接点误差 {1:G4}；最大切线夹角 {2:F8}°。",
        arcs.Count, maxGap, RhinoMath.ToDegrees(maxAngle));
      RhinoApp.WriteLine("控制点共线：最大离线误差 {0:G4}；最大方向夹角 {1:F8}°；接点不在两控制点之间 {2} 处。",
        maxControlPointLineDeviation, RhinoMath.ToDegrees(maxControlPointAngle), betweenFailures);
      if (failures == 0)
      {
        RhinoApp.WriteLine("检查通过：每个接点与两侧最近控制点三点共线，且全部接点达到 G1 相切。");
        return Result.Success;
      }
      RhinoApp.WriteLine("检查未通过：{0} 个接点超出文档公差。", failures);
      return Result.Failure;
    }
  }
}
