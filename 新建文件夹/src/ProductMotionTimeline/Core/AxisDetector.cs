using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ProductMotionTimeline.Core
{
  internal sealed class AxisDetectionResult
  {
    public Plane Plane { get; set; }
    public double Radius { get; set; }
    public int MatchingCircularEdges { get; set; }
  }

  internal static class AxisDetector
  {
    private sealed class Candidate
    {
      public Circle Circle;
      public int Matches;
      public double Score;
    }

    public static bool TryDetect(
      RhinoDoc doc,
      InstanceObject instance,
      out AxisDetectionResult result)
    {
      result = null;
      if (doc == null || instance?.InstanceDefinition == null)
        return false;

      var tolerance = Math.Max(doc.ModelAbsoluteTolerance * 5.0, 1e-5);
      var circles = new List<Circle>();
      foreach (var definitionObject in instance.InstanceDefinition.GetObjects())
        CollectCircles(definitionObject?.Geometry, instance.InstanceXform, tolerance, circles);
      if (circles.Count == 0)
        return false;

      var bounds = instance.Geometry.GetBoundingBox(true);
      var center = bounds.IsValid ? bounds.Center : circles[0].Center;
      var diagonal = bounds.IsValid ? bounds.Diagonal.Length : 1.0;
      var candidates = new List<Candidate>();
      foreach (var circle in circles)
      {
        var normal = circle.Normal;
        if (!normal.Unitize() || circle.Radius <= tolerance)
          continue;

        var matches = circles.Count(other => IsCoaxial(circle, other, tolerance));
        var offset = center - circle.Center;
        var radialOffset = offset - normal * (offset * normal);
        var normalizedOffset = radialOffset.Length / Math.Max(diagonal, tolerance);
        var radiusPenalty = circle.Radius / Math.Max(diagonal, tolerance) * 0.05;
        candidates.Add(new Candidate
        {
          Circle = circle,
          Matches = matches,
          Score = normalizedOffset + radiusPenalty - Math.Min(4, matches) * 0.1
        });
      }

      var best = candidates
        .OrderByDescending(item => item.Matches >= 2)
        .ThenBy(item => item.Score)
        .FirstOrDefault();
      if (best == null)
        return false;

      var axis = best.Circle.Normal;
      axis.Unitize();
      var view = doc.Views.ActiveView;
      var referenceNormal = view == null
        ? Vector3d.ZAxis
        : view.ActiveViewport.ConstructionPlane().Normal;
      if (axis * referenceNormal < 0.0)
        axis.Reverse();

      var toBoundsCenter = center - best.Circle.Center;
      var origin = best.Circle.Center + axis * (toBoundsCenter * axis);
      result = new AxisDetectionResult
      {
        Plane = new Plane(origin, axis),
        Radius = best.Circle.Radius,
        MatchingCircularEdges = best.Matches
      };
      return result.Plane.IsValid;
    }

    private static void CollectCircles(
      GeometryBase geometry,
      Transform instanceTransform,
      double tolerance,
      List<Circle> circles)
    {
      if (geometry == null)
        return;

      var curve = geometry as Curve;
      if (curve != null)
      {
        Circle circle;
        if (curve.TryGetCircle(out circle, tolerance) && circle.Transform(instanceTransform))
          circles.Add(circle);
        return;
      }

      var extrusion = geometry as Extrusion;
      var brep = extrusion == null ? geometry as Brep : extrusion.ToBrep();
      if (brep == null)
        return;
      foreach (var edge in brep.Edges)
      {
        Circle circle;
        if (edge.TryGetCircle(out circle, tolerance) && circle.Transform(instanceTransform))
          circles.Add(circle);
      }
    }

    private static bool IsCoaxial(Circle a, Circle b, double tolerance)
    {
      if (Math.Abs(a.Radius - b.Radius) > tolerance)
        return false;
      var na = a.Normal;
      var nb = b.Normal;
      if (!na.Unitize() || !nb.Unitize() || Math.Abs(na * nb) < Math.Cos(Math.PI / 90.0))
        return false;
      var delta = b.Center - a.Center;
      var radial = delta - na * (delta * na);
      return radial.Length <= tolerance;
    }
  }
}
