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

    public static CheckReport Run(
      RhinoDoc doc,
      IEnumerable<RhinoObject> source,
      CheckSettings settings,
      CheckScope scope)
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
          continue;

        if ((scope & CheckScope.Axis) != 0)
        {
          BoardInfo board;
          if (GeometryTools.TryAnalyzeBoard(rhinoObject, tolerance, out board))
            boards.Add(board);
        }

        if ((scope & CheckScope.Collision) != 0)
        {
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
      }

      if ((scope & CheckScope.Collision) != 0)
        CheckCollisions(report, solids, settings, unitsPerMm, tolerance);
      if ((scope & CheckScope.Axis) != 0)
        CheckAxes(report, boards.SelectMany(item => item.Holes).ToList(), settings, unitsPerMm);
      if ((scope & CheckScope.DuplicateCurve) != 0)
        CheckDuplicateCurves(report, objects, tolerance);
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

    private static void CheckDuplicateCurves(
      CheckReport report,
      IList<RhinoObject> objects,
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
          AddIssue(report, CheckKind.DuplicateCurve, CheckSeverity.Info, "重复曲线/重复走刀",
            "两条曲线在公差范围内完全重合，激光加工可能重复走刀。",
            SafeCenter(right), leftObject.Id, rightObject.Id);
        }
      }
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

    private static Point3d Midpoint(Point3d left, Point3d right)
    {
      return new Point3d(
        (left.X + right.X) * 0.5,
        (left.Y + right.Y) * 0.5,
        (left.Z + right.Z) * 0.5);
    }
  }
}
