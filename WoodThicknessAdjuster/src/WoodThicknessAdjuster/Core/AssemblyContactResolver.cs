using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodThicknessAdjuster.Core
{
  internal static class AssemblyContactResolver
  {
    internal static bool TryFindContact(
      RhinoDoc doc,
      RhinoObject boardObject,
      ThicknessAnalysis analysis,
      double targetThicknessModelUnits,
      double tolerance,
      double modelUnitsPerMillimeter,
      Guid preferredNeighborId,
      out ThicknessContact contact)
    {
      contact = null;
      if (doc == null || boardObject == null || analysis == null)
        return false;

      var boardGeometry = boardObject.Geometry;
      if (boardGeometry == null)
        return false;

      var contactTolerance = Math.Max(
        tolerance * 20.0,
        modelUnitsPerMillimeter * 0.02);
      var recoveryDistance = Math.Max(
        modelUnitsPerMillimeter * 0.5,
        Math.Min(
          modelUnitsPerMillimeter * 5.0,
          Math.Max(targetThicknessModelUnits, analysis.ThicknessModelUnits)));
      var candidates = new List<ContactCandidate>();
      var objects = doc.Objects.GetObjectList(ObjectType.Brep | ObjectType.Extrusion);
      foreach (var neighborObject in objects)
      {
        if (neighborObject == null || neighborObject.Id == boardObject.Id ||
          neighborObject.Geometry == null)
          continue;

        ThicknessAnalysis neighborAnalysis;
        if (!ThicknessAnalyzer.TryAnalyze(
          neighborObject.Geometry,
          tolerance,
          Point3d.Unset,
          out neighborAnalysis))
          continue;

        var preferred = preferredNeighborId != Guid.Empty &&
          neighborObject.Id == preferredNeighborId;
        foreach (var targetFace in GetMainFaces(analysis))
        {
          foreach (var neighborFace in GetMainFaces(neighborAnalysis))
          {
            var alignment = Math.Abs(Vector3d.Multiply(
              targetFace.Plane.Normal,
              neighborFace.Plane.Normal));
            if (alignment < Math.Cos(2.0 * Math.PI / 180.0))
              continue;

            double overlapRatio;
            if (!TryProjectedOverlapRatio(
              boardGeometry,
              neighborObject.Geometry,
              targetFace.Plane,
              tolerance,
              out overlapRatio))
              continue;

            var separation = Math.Abs(
              neighborFace.Plane.DistanceTo(targetFace.Centroid));
            var exact = separation <= contactTolerance;
            var recoverable = separation <= recoveryDistance &&
              ((preferred && overlapRatio >= 0.1) || overlapRatio >= 0.5);
            if (!exact && !recoverable)
              continue;

            // 原本真正贴合的面优先；若前一块木板已经调整，则允许当前木板
            // 跨越小间隙找回该表面。宽面积重叠用于排除邻近但无装配关系的零件。
            var score = (preferred ? 1000.0 : 0.0) +
              (exact ? 100.0 : 0.0) +
              overlapRatio * 10.0 -
              separation / Math.Max(recoveryDistance, tolerance);
            candidates.Add(new ContactCandidate
            {
              Score = score,
              Contact = new ThicknessContact
              {
                NeighborObjectId = neighborObject.Id,
                TargetFaceIndex = targetFace.FaceIndex,
                TargetPlane = targetFace.Plane,
                NeighborPlane = neighborFace.Plane,
                TargetCentroid = targetFace.Centroid,
                SeparationModelUnits = separation,
                ContactToleranceModelUnits = contactTolerance,
                OverlapRatio = overlapRatio,
                IsPreferredNeighbor = preferred,
                WasExactContact = exact
              }
            });
          }
        }
      }

      var best = candidates
        .OrderByDescending(item => item.Score)
        .ThenBy(item => item.Contact.SeparationModelUnits)
        .FirstOrDefault();
      if (best == null)
        return false;
      contact = best.Contact;
      return true;
    }

    private static IEnumerable<MainFace> GetMainFaces(ThicknessAnalysis analysis)
    {
      yield return new MainFace
      {
        FaceIndex = analysis.FirstFaceIndex,
        Plane = analysis.FirstPlane,
        Centroid = analysis.FirstCentroid
      };
      yield return new MainFace
      {
        FaceIndex = analysis.SecondFaceIndex,
        Plane = analysis.SecondPlane,
        Centroid = analysis.SecondCentroid
      };
    }

    private static bool TryProjectedOverlapRatio(
      GeometryBase first,
      GeometryBase second,
      Plane plane,
      double tolerance,
      out double ratio)
    {
      ratio = 0.0;
      ProjectionBounds firstBounds;
      ProjectionBounds secondBounds;
      if (!TryGetProjectionBounds(first, plane, out firstBounds) ||
        !TryGetProjectionBounds(second, plane, out secondBounds))
        return false;

      var overlapX = Math.Max(
        0.0,
        Math.Min(firstBounds.MaxX, secondBounds.MaxX) -
        Math.Max(firstBounds.MinX, secondBounds.MinX));
      var overlapY = Math.Max(
        0.0,
        Math.Min(firstBounds.MaxY, secondBounds.MaxY) -
        Math.Max(firstBounds.MinY, secondBounds.MinY));
      if (overlapX <= tolerance * 5.0 || overlapY <= tolerance * 5.0)
        return false;

      var firstArea = firstBounds.Width * firstBounds.Height;
      var secondArea = secondBounds.Width * secondBounds.Height;
      var smallerArea = Math.Min(firstArea, secondArea);
      if (smallerArea <= tolerance * tolerance)
        return false;
      ratio = Math.Min(1.0, overlapX * overlapY / smallerArea);
      return ratio >= 0.02;
    }

    private static bool TryGetProjectionBounds(
      GeometryBase geometry,
      Plane plane,
      out ProjectionBounds bounds)
    {
      bounds = null;
      if (geometry == null)
        return false;
      var box = geometry.GetBoundingBox(true);
      if (!box.IsValid)
        return false;

      var result = new ProjectionBounds();
      foreach (var corner in box.GetCorners())
      {
        var offset = corner - plane.Origin;
        var x = Vector3d.Multiply(offset, plane.XAxis);
        var y = Vector3d.Multiply(offset, plane.YAxis);
        result.Include(x, y);
      }
      if (!result.IsValid)
        return false;
      bounds = result;
      return true;
    }

    private sealed class MainFace
    {
      public int FaceIndex { get; set; }
      public Plane Plane { get; set; }
      public Point3d Centroid { get; set; }
    }

    private sealed class ContactCandidate
    {
      public double Score { get; set; }
      public ThicknessContact Contact { get; set; }
    }

    private sealed class ProjectionBounds
    {
      public double MinX { get; private set; } = double.PositiveInfinity;
      public double MinY { get; private set; } = double.PositiveInfinity;
      public double MaxX { get; private set; } = double.NegativeInfinity;
      public double MaxY { get; private set; } = double.NegativeInfinity;

      public double Width => MaxX - MinX;
      public double Height => MaxY - MinY;
      public bool IsValid =>
        !double.IsInfinity(MinX) && !double.IsInfinity(MinY) &&
        Width > 0.0 && Height > 0.0;

      public void Include(double x, double y)
      {
        MinX = Math.Min(MinX, x);
        MinY = Math.Min(MinY, y);
        MaxX = Math.Max(MaxX, x);
        MaxY = Math.Max(MaxY, y);
      }
    }
  }
}
