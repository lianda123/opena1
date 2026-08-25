using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace WoodThicknessAdjuster.Core
{
  internal static class ThicknessAnalyzer
  {
    internal static bool TryAnalyze(
      GeometryBase geometry,
      double tolerance,
      Point3d selectionPoint,
      out ThicknessAnalysis analysis)
    {
      analysis = null;
      var brep = ToBrep(geometry);
      if (brep == null || !brep.IsValid || !brep.IsSolid)
        return false;

      var faces = new List<PlanarFaceData>();
      for (var index = 0; index < brep.Faces.Count; index++)
      {
        Plane plane;
        if (!brep.Faces[index].TryGetPlane(out plane, tolerance * 10.0))
          continue;
        var properties = AreaMassProperties.Compute(brep.Faces[index]);
        if (properties == null || properties.Area <= tolerance * tolerance)
          continue;
        faces.Add(new PlanarFaceData
        {
          FaceIndex = index,
          Plane = plane,
          Centroid = properties.Centroid,
          Area = properties.Area
        });
      }
      if (faces.Count < 2)
        return false;

      var diagonal = brep.GetBoundingBox(true).Diagonal.Length;
      var maximumArea = faces.Max(item => item.Area);
      var minimumParallel = Math.Cos(2.0 * Math.PI / 180.0);
      var candidates = new List<PairCandidate>();

      for (var leftIndex = 0; leftIndex < faces.Count - 1; leftIndex++)
      {
        var left = faces[leftIndex];
        for (var rightIndex = leftIndex + 1; rightIndex < faces.Count; rightIndex++)
        {
          var right = faces[rightIndex];
          var alignment = Math.Abs(Vector3d.Multiply(
            left.Plane.Normal,
            right.Plane.Normal));
          if (alignment < minimumParallel)
            continue;

          var distance = Math.Abs(left.Plane.DistanceTo(right.Centroid));
          if (distance <= tolerance * 2.0 || distance >= diagonal * 0.5)
            continue;

          var smallerArea = Math.Min(left.Area, right.Area);
          var largerArea = Math.Max(left.Area, right.Area);
          var areaBalance = smallerArea / Math.Max(largerArea, tolerance * tolerance);
          if (areaBalance < 0.25 || smallerArea < maximumArea * 0.08)
            continue;

          // 木板的两张主表面通常面积最大、间距最小。面积/间距评分能
          // 排除窄侧壁，同时允许布尔孔洞造成上下表面面积略有差异。
          var score = smallerArea * (0.5 + 0.5 * areaBalance) /
            Math.Max(distance, tolerance);
          var firstSelectionDistance = DistanceToTrimmedFace(
            brep.Faces[left.FaceIndex],
            left.Plane,
            selectionPoint);
          var secondSelectionDistance = DistanceToTrimmedFace(
            brep.Faces[right.FaceIndex],
            right.Plane,
            selectionPoint);
          var useFirstAsAnchor = firstSelectionDistance <= secondSelectionDistance;

          candidates.Add(new PairCandidate
          {
            Analysis = new ThicknessAnalysis
            {
              FirstFaceIndex = left.FaceIndex,
              SecondFaceIndex = right.FaceIndex,
              FirstPlane = left.Plane,
              SecondPlane = right.Plane,
              FirstCentroid = left.Centroid,
              SecondCentroid = right.Centroid,
              ThicknessModelUnits = distance,
              Score = score
            },
            SelectionDistance = Math.Min(
              firstSelectionDistance,
              secondSelectionDistance),
            PreferredAnchorFaceIndex = useFirstAsAnchor
              ? left.FaceIndex
              : right.FaceIndex,
            PreferredAnchorArea = useFirstAsAnchor ? left.Area : right.Area
          });
        }
      }

      if (candidates.Count == 0)
        return false;

      // 先使用真正包含点击点的修剪面，再在该面的平行候选中选主表面对。
      // 这样即使大板面被卡扣孔切碎，也不会退回到面积完整的侧面。
      var selectionTolerance = Math.Max(tolerance * 25.0, diagonal * 1e-7);
      var clickedCandidate = candidates
        .Where(item => item.SelectionDistance <= selectionTolerance)
        .OrderBy(item => item.SelectionDistance)
        .ThenByDescending(item => item.PreferredAnchorArea)
        .ThenByDescending(item => item.Analysis.Score)
        .FirstOrDefault();
      if (clickedCandidate != null)
      {
        analysis = clickedCandidate.Analysis;
        analysis.PreferredAnchorFaceIndex = clickedCandidate.PreferredAnchorFaceIndex;
        return true;
      }

      // 曲线、文字等组内附属对象的点击点不一定落在板面上，此时保留旧的
      // 主表面自动识别作为安全回退。
      analysis = candidates
        .OrderByDescending(item => item.Analysis.Score)
        .Select(item => item.Analysis)
        .First();
      return analysis != null;
    }

    private static double DistanceToTrimmedFace(
      BrepFace face,
      Plane plane,
      Point3d point)
    {
      if (face == null || !point.IsValid)
        return double.MaxValue;

      var projected = plane.ClosestPoint(point);
      double u;
      double v;
      if (!face.ClosestPoint(projected, out u, out v) ||
        face.IsPointOnFace(u, v) == PointFaceRelation.Exterior)
        return double.MaxValue;
      return point.DistanceTo(projected);
    }

    private static Brep ToBrep(GeometryBase geometry)
    {
      var brep = geometry as Brep;
      if (brep != null)
        return brep.DuplicateBrep();
      var extrusion = geometry as Extrusion;
      return extrusion == null ? null : extrusion.ToBrep();
    }

    private sealed class PlanarFaceData
    {
      public int FaceIndex { get; set; }
      public Plane Plane { get; set; }
      public Point3d Centroid { get; set; }
      public double Area { get; set; }
    }

    private sealed class PairCandidate
    {
      public ThicknessAnalysis Analysis { get; set; }
      public double SelectionDistance { get; set; }
      public int PreferredAnchorFaceIndex { get; set; }
      public double PreferredAnchorArea { get; set; }
    }
  }
}
