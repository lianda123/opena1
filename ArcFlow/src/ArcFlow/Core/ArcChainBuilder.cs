using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Geometry;

namespace ArcFlow.Core
{
  internal struct ArcJoinMetrics
  {
    public double Gap { get; set; }
    public double TangentAngleRadians { get; set; }
    public double ControlPointAngleRadians { get; set; }
    public double ControlPointLineDeviation { get; set; }
    public bool JoinIsBetweenControlPoints { get; set; }
    public Point3d IncomingControlPoint { get; set; }
    public Point3d JoinPoint { get; set; }
    public Point3d OutgoingControlPoint { get; set; }
  }

  internal static class ArcChainBuilder
  {
    public static bool TryCreateTangentArc(
      Point3d start,
      Vector3d startTangent,
      Point3d end,
      double tolerance,
      out Arc arc)
    {
      arc = Arc.Unset;
      var tangent = startTangent;
      if (!tangent.Unitize() || start.DistanceTo(end) <= tolerance)
        return false;

      try
      {
        var candidate = new Arc(start, tangent, end);
        if (!candidate.IsValid || candidate.Radius <= tolerance || Math.Abs(candidate.Angle) <= RhinoMath.ZeroTolerance)
          return false;
        arc = candidate;
        return true;
      }
      catch
      {
        return false;
      }
    }

    public static bool TryCreateBiarc(
      Point3d start,
      Vector3d startTangent,
      Point3d end,
      Vector3d endTangent,
      double tolerance,
      out Arc first,
      out Arc second)
    {
      first = Arc.Unset;
      second = Arc.Unset;

      var t0 = startTangent;
      var t1 = endTangent;
      if (!t0.Unitize() || !t1.Unitize() || start.DistanceTo(end) <= tolerance)
        return false;

      var chord = end - start;
      var tangentSum = t0 + t1;
      var a = 2.0 * (1.0 - Vector3d.Multiply(t0, t1));
      var b = 2.0 * Vector3d.Multiply(chord, tangentSum);
      var c = -chord.SquareLength;
      double distance;

      if (Math.Abs(a) <= 1e-12)
      {
        if (Math.Abs(b) <= 1e-12)
          return false;
        distance = -c / b;
      }
      else
      {
        var discriminant = b * b - 4.0 * a * c;
        if (discriminant < 0.0)
          return false;
        var root = Math.Sqrt(discriminant);
        var d0 = (-b + root) / (2.0 * a);
        var d1 = (-b - root) / (2.0 * a);
        distance = ChoosePositive(d0, d1);
      }

      if (!RhinoMath.IsValidDouble(distance) || distance <= tolerance)
        return false;

      var join = new Point3d(
        0.5 * (start.X + end.X + distance * (t0.X - t1.X)),
        0.5 * (start.Y + end.Y + distance * (t0.Y - t1.Y)),
        0.5 * (start.Z + end.Z + distance * (t0.Z - t1.Z)));

      if (!TryCreateTangentArc(start, t0, join, tolerance, out first))
        return false;

      if (!TryCreateTangentArc(end, -t1, join, tolerance, out second))
        return false;

      second.Reverse();
      ArcJoinMetrics metrics;
      return first.IsValid && second.IsValid &&
        TryMeasureJoin(first, second, out metrics) &&
        metrics.Gap <= Math.Max(tolerance, RhinoMath.ZeroTolerance * 10.0) &&
        metrics.ControlPointAngleRadians <= 1e-7 &&
        metrics.ControlPointLineDeviation <= Math.Max(tolerance, RhinoMath.ZeroTolerance * 10.0) &&
        metrics.JoinIsBetweenControlPoints;
    }

    public static List<Arc> FromSamples(
      IList<Point3d> points,
      IList<Vector3d> tangents,
      double tolerance)
    {
      var result = new List<Arc>();
      if (points == null || tangents == null || points.Count != tangents.Count || points.Count < 2)
        return result;

      var currentTangent = tangents[0];
      if (!currentTangent.Unitize())
        return result;

      for (var i = 0; i < points.Count - 1; i++)
      {
        if (points[i].DistanceTo(points[i + 1]) <= tolerance)
          continue;

        Arc first;
        Arc second;
        if (TryCreateBiarc(points[i], currentTangent, points[i + 1], tangents[i + 1], tolerance, out first, out second))
        {
          if (!TryAppendG1(result, first, tolerance) || !TryAppendG1(result, second, tolerance))
            break;
          currentTangent = second.TangentAt(second.AngleDomain.T1);
          if (!currentTangent.Unitize())
            break;
          continue;
        }

        Arc fallback;
        if (!TryCreateTangentArc(points[i], currentTangent, points[i + 1], tolerance, out fallback) ||
          !TryAppendG1(result, fallback, tolerance))
          break;

        // The fallback arc may not finish on the analytic target tangent. Carry
        // its actual end tangent into the next interval so the produced chain
        // remains strictly G1 instead of reintroducing the theoretical tangent.
        currentTangent = fallback.TangentAt(fallback.AngleDomain.T1);
        if (!currentTangent.Unitize())
          break;
      }
      return result;
    }

    public static bool TryAppendG1(List<Arc> arcs, Arc candidate, double tolerance)
    {
      if (arcs == null || !candidate.IsValid)
        return false;
      if (arcs.Count == 0)
      {
        arcs.Add(candidate);
        return true;
      }

      ArcJoinMetrics metrics;
      if (!TryMeasureJoin(arcs[arcs.Count - 1], candidate, out metrics))
        return false;

      var lengthTolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance * 10.0);
      if (metrics.Gap > lengthTolerance ||
        metrics.TangentAngleRadians > 1e-7 ||
        metrics.ControlPointAngleRadians > 1e-7 ||
        metrics.ControlPointLineDeviation > lengthTolerance ||
        !metrics.JoinIsBetweenControlPoints)
        return false;

      arcs.Add(candidate);
      return true;
    }

    public static bool TryMeasureJoin(Arc first, Arc second, out ArcJoinMetrics metrics)
    {
      metrics = new ArcJoinMetrics();
      if (!first.IsValid || !second.IsValid)
        return false;

      using (var firstCurve = new ArcCurve(first))
      using (var secondCurve = new ArcCurve(second))
      using (var firstNurbs = firstCurve.ToNurbsCurve())
      using (var secondNurbs = secondCurve.ToNurbsCurve())
      {
        if (firstNurbs == null || secondNurbs == null ||
          firstNurbs.Points.Count < 2 || secondNurbs.Points.Count < 2)
          return false;

        var incomingControl = firstNurbs.Points[firstNurbs.Points.Count - 2].Location;
        var join = first.EndPoint;
        var outgoingControl = secondNurbs.Points[1].Location;
        var incomingDirection = join - incomingControl;
        var outgoingDirection = outgoingControl - join;
        var incomingLength = incomingDirection.Length;
        var outgoingLength = outgoingDirection.Length;
        if (incomingLength <= RhinoMath.ZeroTolerance || outgoingLength <= RhinoMath.ZeroTolerance)
          return false;

        var controlCross = Vector3d.CrossProduct(incomingDirection, outgoingDirection).Length;
        var controlDot = Vector3d.Multiply(incomingDirection, outgoingDirection);
        var controlAngle = Math.Atan2(controlCross, controlDot);

        metrics = new ArcJoinMetrics
        {
          Gap = first.EndPoint.DistanceTo(second.StartPoint),
          TangentAngleRadians = TangentAngle(first, second),
          ControlPointAngleRadians = controlAngle,
          ControlPointLineDeviation = controlCross / incomingLength,
          JoinIsBetweenControlPoints = controlDot >= 0.0,
          IncomingControlPoint = incomingControl,
          JoinPoint = join,
          OutgoingControlPoint = outgoingControl
        };
        return true;
      }
    }

    public static List<Guid> AddToDocument(RhinoDoc doc, IEnumerable<Arc> arcs, string groupName)
    {
      var ids = new List<Guid>();
      foreach (var arc in arcs)
      {
        if (!arc.IsValid)
          continue;
        var id = doc.Objects.AddArc(arc);
        if (id != Guid.Empty)
          ids.Add(id);
      }

      if (ids.Count > 0)
      {
        var groupIndex = doc.Groups.Add(groupName);
        if (groupIndex >= 0)
          doc.Groups.AddToGroup(groupIndex, ids);
        doc.Views.Redraw();
      }
      return ids;
    }

    public static double TangentAngle(Arc first, Arc second)
    {
      var a = first.TangentAt(first.AngleDomain.T1);
      var b = second.TangentAt(second.AngleDomain.T0);
      if (!a.Unitize() || !b.Unitize())
        return Math.PI;
      var cross = Vector3d.CrossProduct(a, b).Length;
      var dot = Math.Max(-1.0, Math.Min(1.0, Vector3d.Multiply(a, b)));
      return Math.Atan2(cross, dot);
    }

    private static double ChoosePositive(double first, double second)
    {
      var firstOk = RhinoMath.IsValidDouble(first) && first > 0.0;
      var secondOk = RhinoMath.IsValidDouble(second) && second > 0.0;
      if (firstOk && secondOk)
        return Math.Min(first, second);
      if (firstOk)
        return first;
      if (secondOk)
        return second;
      return double.NaN;
    }
  }
}
