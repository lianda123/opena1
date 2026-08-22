using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodCheck.Core
{
  internal static class WoodCheckEngine
  {
    private sealed class SolidCandidate
    {
      public RhinoObject Source { get; set; }
      public Brep Brep { get; set; }
      public BoundingBox Bounds { get; set; }
    }

    public static CheckReport Run(RhinoDoc doc, IEnumerable<RhinoObject> source, CheckSettings settings)
    {
      var report = new CheckReport();
      if (doc == null || source == null || settings == null)
        return report;

      var objects = source
        .Where(item => item != null && item.Geometry != null)
        .Where(item => item.Attributes.GetUserString(MarkerManager.MarkerKey) != MarkerManager.MarkerValue)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .ToList();

      var unitsPerMm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, unitsPerMm * 0.01);
      var boards = new List<BoardInfo>();
      var solids = new List<SolidCandidate>();

      foreach (var rhinoObject in objects)
      {
        if (!rhinoObject.Geometry.IsValid)
        {
          AddIssue(report, CheckKind.InvalidGeometry, CheckSeverity.Error, "无效几何",
            "对象几何无效，请先运行 Rhino 的 Check 或修复坏对象。",
            SafeCenter(rhinoObject.Geometry), rhinoObject.Id);
          continue;
        }

        BoardInfo board;
        if (GeometryTools.TryAnalyzeBoard(rhinoObject, tolerance, out board))
          boards.Add(board);

        var brep = GeometryTools.ToBrep(rhinoObject.Geometry);
        if (brep != null && brep.IsValid && brep.IsSolid)
        {
          solids.Add(new SolidCandidate
          {
            Source = rhinoObject,
            Brep = brep,
            Bounds = brep.GetBoundingBox(true)
          });
        }
      }

      CheckCollisions(report, solids, settings, unitsPerMm, tolerance);
      CheckSlots(report, boards, settings, unitsPerMm, tolerance);
      CheckWeakWalls(report, boards, settings, unitsPerMm, tolerance);
      CheckAxes(report, boards.SelectMany(item => item.Holes).ToList(), settings, unitsPerMm);
      CheckCurves(report, doc, objects, settings, unitsPerMm, tolerance);
      AssignCodes(report);
      return report;
    }

    private static void CheckCollisions(
      CheckReport report,
      IList<SolidCandidate> solids,
      CheckSettings settings,
      double unitsPerMm,
      double tolerance)
    {
      var minimumVolume = settings.CollisionVolumeMm3 * unitsPerMm * unitsPerMm * unitsPerMm;
      for (var leftIndex = 0; leftIndex < solids.Count; leftIndex++)
      {
        var left = solids[leftIndex];
        for (var rightIndex = leftIndex + 1; rightIndex < solids.Count; rightIndex++)
        {
          var right = solids[rightIndex];
          if (!GeometryTools.BoxesOverlap(left.Bounds, right.Bounds, tolerance))
            continue;

          Brep[] intersections;
          try
          {
            intersections = Brep.CreateBooleanIntersection(left.Brep, right.Brep, tolerance);
          }
          catch
          {
            intersections = null;
          }

          if (intersections == null || intersections.Length == 0)
            continue;

          var volume = 0.0;
          var overlapBounds = BoundingBox.Unset;
          foreach (var intersection in intersections)
          {
            if (intersection == null || !intersection.IsValid)
              continue;

            var properties = VolumeMassProperties.Compute(intersection);
            if (properties != null)
              volume += Math.Abs(properties.Volume);
            var bounds = intersection.GetBoundingBox(true);
            if (bounds.IsValid)
              overlapBounds = overlapBounds.IsValid ? BoundingBox.Union(overlapBounds, bounds) : bounds;
          }

          if (volume <= minimumVolume)
            continue;

          var volumeMm3 = volume / (unitsPerMm * unitsPerMm * unitsPerMm);
          var location = overlapBounds.IsValid
            ? overlapBounds.Center
            : Midpoint(left.Bounds.Center, right.Bounds.Center);
          AddIssue(report, CheckKind.Collision, CheckSeverity.Error, "实体穿模/碰撞",
            string.Format("两个零件发生实体重叠，重叠体积约 {0:0.###} mm³。", volumeMm3),
            location, left.Source.Id, right.Source.Id);
        }
      }
    }

    private static void CheckSlots(
      CheckReport report,
      IEnumerable<BoardInfo> boards,
      CheckSettings settings,
      double unitsPerMm,
      double tolerance)
    {
      var nominalThickness = settings.NominalBoardThicknessMm * unitsPerMm;
      var minimumDepth = settings.MinimumSlotDepthMm * unitsPerMm;
      foreach (var board in boards)
      {
        List<Point3d> points;
        if (!GeometryTools.TryGetProfilePoints(board, tolerance, out points))
          continue;

        var count = points.Count;
        var reported = new List<Point3d>();
        for (var index = 0; index < count; index++)
        {
          var p0 = points[index % count];
          var p1 = points[(index + 1) % count];
          var p2 = points[(index + 2) % count];
          var p3 = points[(index + 3) % count];
          var firstSide = p1 - p0;
          var bottom = p2 - p1;
          var secondSide = p3 - p2;
          var firstLength = firstSide.Length;
          var gap = bottom.Length;
          var secondLength = secondSide.Length;
          if (firstLength <= tolerance || gap <= tolerance || secondLength <= tolerance)
            continue;

          firstSide.Unitize();
          bottom.Unitize();
          secondSide.Unitize();
          var oppositeSides = Vector3d.Multiply(firstSide, secondSide) < -0.93;
          var squareBottom = Math.Abs(Vector3d.Multiply(firstSide, bottom)) < 0.20 &&
                             Math.Abs(Vector3d.Multiply(secondSide, bottom)) < 0.20;
          var slotWidthLikeBoard = gap >= nominalThickness * 0.55 && gap <= nominalThickness * 1.75;
          if (!oppositeSides || !squareBottom || !slotWidthLikeBoard)
            continue;

          var depth = Math.Min(firstLength, secondLength);
          if (depth + tolerance >= minimumDepth)
            continue;

          var local = Midpoint(p1, p2);
          var location = board.Plane.PointAt(local.X, local.Y);
          if (reported.Any(item => item.DistanceTo(location) <= nominalThickness))
            continue;
          reported.Add(location);

          AddIssue(report, CheckKind.ShallowSlot, CheckSeverity.Warning, "槽深不足",
            string.Format("检测到约 {0:0.###} mm 深的 U 形槽，小于当前下限 {1:0.###} mm。",
              depth / unitsPerMm, settings.MinimumSlotDepthMm),
            location, board.Source.Id);
        }
      }
    }

    private static void CheckWeakWalls(
      CheckReport report,
      IEnumerable<BoardInfo> boards,
      CheckSettings settings,
      double unitsPerMm,
      double tolerance)
    {
      var minimumWall = settings.MinimumWallMm * unitsPerMm;
      var minimumFeature = settings.MinimumFeatureMm * unitsPerMm;
      foreach (var board in boards)
      {
        foreach (var hole in board.Holes)
        {
          double parameter;
          if (!board.OuterCurve.ClosestPoint(hole.Center, out parameter))
            continue;
          var edgePoint = board.OuterCurve.PointAt(parameter);
          var wall = hole.Center.DistanceTo(edgePoint) - hole.Radius;
          if (wall < minimumWall - tolerance)
          {
            AddIssue(report, CheckKind.WeakWall, CheckSeverity.Warning, "孔边薄弱",
              string.Format("孔到外轮廓的剩余木料约 {0:0.###} mm，小于下限 {1:0.###} mm。",
                Math.Max(0.0, wall / unitsPerMm), settings.MinimumWallMm),
              Midpoint(hole.Center, edgePoint), board.Source.Id);
          }
        }

        for (var leftIndex = 0; leftIndex < board.Holes.Count; leftIndex++)
        {
          for (var rightIndex = leftIndex + 1; rightIndex < board.Holes.Count; rightIndex++)
          {
            var left = board.Holes[leftIndex];
            var right = board.Holes[rightIndex];
            var ligament = left.Center.DistanceTo(right.Center) - left.Radius - right.Radius;
            if (ligament >= minimumWall - tolerance)
              continue;
            AddIssue(report, CheckKind.WeakWall, CheckSeverity.Warning, "孔间薄弱",
              string.Format("两个孔之间的剩余木料约 {0:0.###} mm，小于下限 {1:0.###} mm。",
                Math.Max(0.0, ligament / unitsPerMm), settings.MinimumWallMm),
              Midpoint(left.Center, right.Center), board.Source.Id);
          }
        }

        List<Point3d> profile;
        if (!GeometryTools.TryGetProfilePoints(board, tolerance, out profile))
          continue;
        for (var index = 0; index < profile.Count; index++)
        {
          var next = (index + 1) % profile.Count;
          var length = profile[index].DistanceTo(profile[next]);
          if (length >= minimumFeature - tolerance)
            continue;
          var local = Midpoint(profile[index], profile[next]);
          AddIssue(report, CheckKind.WeakWall, CheckSeverity.Info, "过短加工特征",
            string.Format("轮廓中存在约 {0:0.###} mm 的短边，可能烧焦或脱落。", length / unitsPerMm),
            board.Plane.PointAt(local.X, local.Y), board.Source.Id);
        }
      }
    }

    private static void CheckAxes(
      CheckReport report,
      IList<HoleInfo> holes,
      CheckSettings settings,
      double unitsPerMm)
    {
      var shaftDiameter = settings.ShaftDiameterMm * unitsPerMm;
      var candidateTolerance = 0.60 * unitsPerMm;
      var axisTolerance = settings.AxisToleranceMm * unitsPerMm;
      var searchRadius = settings.AxisSearchRadiusMm * unitsPerMm;
      var maximumSpan = settings.MaximumAxisSpanMm * unitsPerMm;
      var candidates = holes
        .Where(item => Math.Abs(item.Radius * 2.0 - shaftDiameter) <= candidateTolerance)
        .ToList();

      for (var leftIndex = 0; leftIndex < candidates.Count; leftIndex++)
      {
        var left = candidates[leftIndex];
        for (var rightIndex = leftIndex + 1; rightIndex < candidates.Count; rightIndex++)
        {
          var right = candidates[rightIndex];
          if (left.Source.Id == right.Source.Id)
            continue;

          var directionDot = Math.Abs(Vector3d.Multiply(left.Axis, right.Axis));
          if (directionDot < 0.9986) // about 3 degrees
            continue;

          double axialSpan;
          var offset = GeometryTools.ParallelAxisOffset(left, right, out axialSpan);
          if (axialSpan > maximumSpan || offset <= axisTolerance || offset > searchRadius)
            continue;

          AddIssue(report, CheckKind.AxisMisalignment, CheckSeverity.Warning, "孔轴不同心",
            string.Format("两个 Ø{0:0.###} mm 附近的轴孔中心线偏移约 {1:0.###} mm。",
              settings.ShaftDiameterMm, offset / unitsPerMm),
            Midpoint(left.Center, right.Center), left.Source.Id, right.Source.Id);
        }
      }
    }

    private static void CheckCurves(
      CheckReport report,
      RhinoDoc doc,
      IList<RhinoObject> objects,
      CheckSettings settings,
      double unitsPerMm,
      double tolerance)
    {
      var curveObjects = objects
        .Where(item => item.Geometry is Curve)
        .ToList();
      var duplicateIds = new HashSet<Guid>();

      for (var leftIndex = 0; leftIndex < curveObjects.Count; leftIndex++)
      {
        var leftObject = curveObjects[leftIndex];
        var left = (Curve)leftObject.Geometry;
        for (var rightIndex = leftIndex + 1; rightIndex < curveObjects.Count; rightIndex++)
        {
          var rightObject = curveObjects[rightIndex];
          if (duplicateIds.Contains(rightObject.Id))
            continue;
          var right = (Curve)rightObject.Geometry;
          if (!GeometryTools.CurvesEquivalent(left, right, tolerance))
            continue;

          duplicateIds.Add(rightObject.Id);
          AddIssue(report, CheckKind.DuplicateCurve, CheckSeverity.Error, "重复曲线",
            "两条曲线在公差范围内完全重合，激光加工可能重复走刀。",
            SafeCenter(right), leftObject.Id, rightObject.Id);
        }
      }

      var closeGap = settings.OpenCurveGapMm * unitsPerMm;
      foreach (var curveObject in curveObjects)
      {
        var curve = (Curve)curveObject.Geometry;
        if (curve.IsClosed || IsEngravingLayer(doc, curveObject.Attributes.LayerIndex))
          continue;

        var gap = curve.PointAtStart.DistanceTo(curve.PointAtEnd);
        var location = Midpoint(curve.PointAtStart, curve.PointAtEnd);
        if (gap <= closeGap)
        {
          AddIssue(report, CheckKind.OpenCurve, CheckSeverity.Error, "轮廓未闭合",
            string.Format("曲线首尾仅相差 {0:0.###} mm，可尝试 Join/CloseCrv。", gap / unitsPerMm),
            location, curveObject.Id);
        }
        else
        {
          AddIssue(report, CheckKind.OpenCurve, CheckSeverity.Warning, "开放曲线",
            "该曲线没有闭合；如果它属于切割轮廓，导出 CAD 前需要修复。",
            SafeMidpoint(curve), curveObject.Id);
        }
      }
    }

    private static bool IsEngravingLayer(RhinoDoc doc, int layerIndex)
    {
      if (doc == null || layerIndex < 0 || layerIndex >= doc.Layers.Count)
        return false;
      var name = (doc.Layers[layerIndex].FullPath ?? string.Empty).ToLowerInvariant();
      return name.Contains("engrave") || name.Contains("etch") || name.Contains("mark") ||
             name.Contains("雕刻") || name.Contains("刻线") || name.Contains("标记");
    }

    private static void AddIssue(
      CheckReport report,
      CheckKind kind,
      CheckSeverity severity,
      string title,
      string message,
      Point3d location,
      params Guid[] sourceIds)
    {
      var issue = new CheckIssue
      {
        Kind = kind,
        Severity = severity,
        Title = title,
        Message = message,
        Location = location.IsValid ? location : Point3d.Origin
      };
      if (sourceIds != null)
        issue.SourceIds.AddRange(sourceIds.Where(item => item != Guid.Empty));
      report.Issues.Add(issue);
    }

    private static void AssignCodes(CheckReport report)
    {
      var error = 0;
      var warning = 0;
      var info = 0;
      foreach (var issue in report.Issues)
      {
        switch (issue.Severity)
        {
          case CheckSeverity.Error:
            issue.Code = "E" + (++error).ToString("000");
            break;
          case CheckSeverity.Warning:
            issue.Code = "W" + (++warning).ToString("000");
            break;
          default:
            issue.Code = "I" + (++info).ToString("000");
            break;
        }
      }
    }

    private static Point3d SafeCenter(GeometryBase geometry)
    {
      if (geometry == null)
        return Point3d.Origin;
      var bounds = geometry.GetBoundingBox(true);
      return bounds.IsValid ? bounds.Center : Point3d.Origin;
    }

    private static Point3d SafeMidpoint(Curve curve)
    {
      try
      {
        return curve.PointAtNormalizedLength(0.5);
      }
      catch
      {
        return curve.PointAt(curve.Domain.Mid);
      }
    }

    private static Point3d Midpoint(Point3d left, Point3d right)
    {
      return new Point3d(
        (left.X + right.X) * 0.5,
        (left.Y + right.Y) * 0.5,
        (left.Z + right.Z) * 0.5);
    }
  }
}
