using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodCheck.Core
{
  internal static class GeometryTools
  {
    public static Brep ToBrep(GeometryBase geometry)
    {
      if (geometry == null)
        return null;

      var brep = geometry as Brep;
      if (brep != null)
        return brep.DuplicateBrep();

      var extrusion = geometry as Extrusion;
      if (extrusion != null)
        return extrusion.ToBrep();

      var surface = geometry as Surface;
      if (surface != null)
        return surface.ToBrep();

      var mesh = geometry as Mesh;
      if (mesh != null && mesh.IsValid)
        return Brep.CreateFromMesh(mesh, true);

      return null;
    }

    public static bool TryAnalyzeBoard(RhinoObject source, double tolerance, out BoardInfo board)
    {
      board = null;
      if (source == null || source.Geometry == null)
        return false;

      var brep = ToBrep(source.Geometry);
      if (brep == null || !brep.IsValid)
        return false;

      BrepFace mainFace = null;
      Plane mainPlane = Plane.Unset;
      var mainArea = 0.0;

      foreach (var face in brep.Faces)
      {
        Plane candidatePlane;
        if (!face.TryGetPlane(out candidatePlane, tolerance))
          continue;

        var faceBox = face.GetBoundingBox(candidatePlane);
        if (!faceBox.IsValid)
          continue;

        var area = Math.Abs(faceBox.Diagonal.X * faceBox.Diagonal.Y);
        if (area > mainArea)
        {
          mainArea = area;
          mainFace = face;
          mainPlane = candidatePlane;
        }
      }

      if (mainFace == null || !mainPlane.IsValid)
        return false;

      var alignedBounds = brep.GetBoundingBox(mainPlane);
      if (!alignedBounds.IsValid)
        return false;

      var width = Math.Abs(alignedBounds.Diagonal.X);
      var height = Math.Abs(alignedBounds.Diagonal.Y);
      var thickness = Math.Abs(alignedBounds.Diagonal.Z);
      if (width <= tolerance || height <= tolerance || thickness <= tolerance)
        return false;

      // A wooden board must have one dimension substantially smaller than its face dimensions.
      if (thickness > Math.Min(width, height) * 0.40)
        return false;

      var outer = mainFace.OuterLoop == null ? null : mainFace.OuterLoop.To3dCurve();
      if (outer == null || !outer.IsValid)
        return false;

      board = new BoardInfo
      {
        Source = source,
        Brep = brep,
        Plane = mainPlane,
        Thickness = thickness,
        OuterCurve = outer,
        Bounds = brep.GetBoundingBox(true)
      };

      foreach (var loop in mainFace.Loops)
      {
        if (loop.LoopType != BrepLoopType.Inner)
          continue;

        var curve = loop.To3dCurve();
        if (curve == null || !curve.IsValid)
          continue;

        Circle circle;
        if (!curve.TryGetCircle(out circle))
          continue;

        var axis = circle.Plane.Normal;
        if (!axis.Unitize())
          continue;

        board.Holes.Add(new HoleInfo
        {
          Source = source,
          Center = circle.Center,
          Axis = axis,
          Radius = circle.Radius,
          Boundary = curve
        });
      }

      return true;
    }

    public static bool TryGetProfilePoints(BoardInfo board, double tolerance, out List<Point3d> points)
    {
      points = new List<Point3d>();
      if (board == null || board.OuterCurve == null)
        return false;

      Polyline polyline;
      if (!board.OuterCurve.TryGetPolyline(out polyline) || polyline.Count < 4)
        return false;

      foreach (var point in polyline)
        points.Add(ToPlanePoint(board.Plane, point));

      if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) <= tolerance)
        points.RemoveAt(points.Count - 1);

      RemoveConsecutiveDuplicates(points, tolerance);
      return points.Count >= 4;
    }

    public static Point3d ToPlanePoint(Plane plane, Point3d point)
    {
      var delta = point - plane.Origin;
      return new Point3d(
        Vector3d.Multiply(delta, plane.XAxis),
        Vector3d.Multiply(delta, plane.YAxis),
        Vector3d.Multiply(delta, plane.ZAxis));
    }

    public static bool CurvesEquivalent(Curve left, Curve right, double tolerance)
    {
      if (left == null || right == null || !left.IsValid || !right.IsValid)
        return false;
      if (left.IsClosed != right.IsClosed)
        return false;

      var leftLength = left.GetLength();
      var rightLength = right.GetLength();
      if (Math.Abs(leftLength - rightLength) > Math.Max(tolerance * 2.0, leftLength * 1e-6))
        return false;

      var leftBox = left.GetBoundingBox(true);
      var rightBox = right.GetBoundingBox(true);
      if (!BoxesSimilar(leftBox, rightBox, tolerance * 2.0))
        return false;

      return SamplesLieOnCurve(left, right, tolerance) && SamplesLieOnCurve(right, left, tolerance);
    }

    public static bool BoxesOverlap(BoundingBox left, BoundingBox right, double tolerance)
    {
      if (!left.IsValid || !right.IsValid)
        return false;
      return left.Min.X <= right.Max.X + tolerance && left.Max.X >= right.Min.X - tolerance &&
             left.Min.Y <= right.Max.Y + tolerance && left.Max.Y >= right.Min.Y - tolerance &&
             left.Min.Z <= right.Max.Z + tolerance && left.Max.Z >= right.Min.Z - tolerance;
    }

    public static double ParallelAxisOffset(HoleInfo left, HoleInfo right, out double axialSpan)
    {
      var axis = left.Axis;
      if (!axis.Unitize())
      {
        axialSpan = double.MaxValue;
        return double.MaxValue;
      }

      var delta = right.Center - left.Center;
      var along = Vector3d.Multiply(delta, axis);
      axialSpan = Math.Abs(along);
      var perpendicular = delta - axis * along;
      return perpendicular.Length;
    }

    private static bool SamplesLieOnCurve(Curve source, Curve target, double tolerance)
    {
      const int sampleCount = 24;
      for (var index = 0; index <= sampleCount; index++)
      {
        var fraction = index / (double)sampleCount;
        Point3d point;
        try
        {
          point = source.PointAtNormalizedLength(fraction);
        }
        catch
        {
          point = source.PointAt(source.Domain.ParameterAt(fraction));
        }

        double parameter;
        if (!target.ClosestPoint(point, out parameter))
          return false;
        if (point.DistanceTo(target.PointAt(parameter)) > tolerance)
          return false;
      }
      return true;
    }

    private static bool BoxesSimilar(BoundingBox left, BoundingBox right, double tolerance)
    {
      if (!left.IsValid || !right.IsValid)
        return false;
      return left.Min.DistanceTo(right.Min) <= tolerance && left.Max.DistanceTo(right.Max) <= tolerance;
    }

    private static void RemoveConsecutiveDuplicates(IList<Point3d> points, double tolerance)
    {
      for (var index = points.Count - 1; index > 0; index--)
      {
        if (points[index].DistanceTo(points[index - 1]) <= tolerance)
          points.RemoveAt(index);
      }
    }
  }
}
