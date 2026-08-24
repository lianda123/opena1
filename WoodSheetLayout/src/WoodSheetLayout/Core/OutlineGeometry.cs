using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace WoodSheetLayout.Core
{
  internal static class OutlineGeometry
  {
    public static PartOutline Create(IEnumerable<Curve> sourceCurves, double chordTolerance)
    {
      var curves = (sourceCurves ?? Enumerable.Empty<Curve>())
        .Where(item => item != null && item.IsValid)
        .ToArray();
      if (curves.Length == 0)
        return null;

      var joined = Curve.JoinCurves(curves, Math.Max(chordTolerance * 0.2, 1e-7));
      var loops = (joined == null ? curves : joined)
        .Where(item => item != null && item.IsClosed)
        .Select(item => SampleLoop(item, chordTolerance))
        .Where(item => item != null && item.Points.Count >= 4 && Math.Abs(item.SignedArea) > 1e-10)
        .OrderByDescending(item => Math.Abs(item.SignedArea))
        .ToList();
      if (loops.Count == 0)
        return null;

      var outline = new PartOutline { Outer = loops[0], Bounds = loops[0].Bounds };
      for (var index = 1; index < loops.Count; index++)
      {
        var probe = InteriorProbe(loops[index]);
        if (PointInLoop(loops[0], probe))
          outline.Holes.Add(loops[index]);
      }

      outline.NetArea = Math.Abs(outline.Outer.SignedArea) -
                        outline.Holes.Sum(item => Math.Abs(item.SignedArea));
      if (outline.NetArea <= 1e-10)
        outline.NetArea = Math.Abs(outline.Outer.SignedArea);
      return outline;
    }

    public static PartOutline CreateRectangle(BoundingBox bounds)
    {
      if (!bounds.IsValid)
        return null;
      var loop = new PolygonLoop2d();
      loop.Points.Add(new Point2d(bounds.Min.X, bounds.Min.Y));
      loop.Points.Add(new Point2d(bounds.Max.X, bounds.Min.Y));
      loop.Points.Add(new Point2d(bounds.Max.X, bounds.Max.Y));
      loop.Points.Add(new Point2d(bounds.Min.X, bounds.Max.Y));
      loop.Points.Add(loop.Points[0]);
      FinalizeLoop(loop);
      return new PartOutline
      {
        Outer = loop,
        NetArea = Math.Abs(loop.SignedArea),
        Bounds = loop.Bounds
      };
    }

    public static PositionedOutline Position(PartOutline outline, double angle, double translationX, double translationY)
    {
      if (outline == null || outline.Outer == null)
        return null;
      var positioned = new PositionedOutline
      {
        Outer = TransformLoop(outline.Outer, angle, translationX, translationY)
      };
      foreach (var hole in outline.Holes)
        positioned.Holes.Add(TransformLoop(hole, angle, translationX, translationY));
      positioned.Bounds = positioned.Outer.Bounds;
      return positioned;
    }

    public static BoundingBox RotatedBounds(PartOutline outline, double angle)
    {
      var positioned = Position(outline, angle, 0.0, 0.0);
      return positioned == null ? BoundingBox.Unset : positioned.Bounds;
    }

    public static bool FitsSheet(PositionedOutline outline, double width, double height, double margin)
    {
      if (outline == null || !outline.Bounds.IsValid)
        return false;
      return outline.Bounds.Min.X >= margin - 1e-8 &&
             outline.Bounds.Min.Y >= margin - 1e-8 &&
             outline.Bounds.Max.X <= width - margin + 1e-8 &&
             outline.Bounds.Max.Y <= height - margin + 1e-8;
    }

    public static bool Collides(PositionedOutline left, PositionedOutline right, double gap)
    {
      if (left == null || right == null)
        return true;
      if (left.Bounds.Max.X + gap <= right.Bounds.Min.X || right.Bounds.Max.X + gap <= left.Bounds.Min.X ||
          left.Bounds.Max.Y + gap <= right.Bounds.Min.Y || right.Bounds.Max.Y + gap <= left.Bounds.Min.Y)
        return false;

      var leftLoops = EnumerateLoops(left).ToArray();
      var rightLoops = EnumerateLoops(right).ToArray();
      var gapSquared = gap * gap;
      foreach (var leftLoop in leftLoops)
      {
        foreach (var rightLoop in rightLoops)
        {
          if (BoundsSeparated(leftLoop.Bounds, rightLoop.Bounds, gap))
            continue;
          if (BoundaryDistanceLessThan(leftLoop, rightLoop, gapSquared))
            return true;
        }
      }

      Point2d leftProbe;
      Point2d rightProbe;
      var hasLeftProbe = TryRegionInteriorProbe(left, out leftProbe);
      var hasRightProbe = TryRegionInteriorProbe(right, out rightProbe);
      return (hasRightProbe && PointInRegion(left, rightProbe)) ||
             (hasLeftProbe && PointInRegion(right, leftProbe));
    }

    public static bool IsNestedInsideHole(
      PositionedOutline candidate,
      PositionedOutline container,
      double gap)
    {
      if (candidate == null || container == null)
        return false;
      return container.Holes.Any(hole => LoopContainsLoop(hole, candidate.Outer, gap));
    }

    public static Point2d InteriorProbe(PolygonLoop2d loop)
    {
      if (loop == null || loop.Points.Count == 0)
        return Point2d.Origin;
      var centroid = PolygonCentroid(loop);
      if (PointInLoop(loop, centroid))
        return centroid;

      var average = new Point2d(
        loop.Points.Take(loop.Points.Count - 1).Average(item => item.X),
        loop.Points.Take(loop.Points.Count - 1).Average(item => item.Y));
      if (PointInLoop(loop, average))
        return average;

      var first = loop.Points[0];
      return new Point2d(first.X * 0.99 + average.X * 0.01, first.Y * 0.99 + average.Y * 0.01);
    }

    public static bool PointInLoop(PolygonLoop2d loop, Point2d point)
    {
      if (loop == null || loop.Points.Count < 4)
        return false;
      var inside = false;
      for (var index = 0; index < loop.Points.Count - 1; index++)
      {
        var a = loop.Points[index];
        var b = loop.Points[index + 1];
        var crosses = (a.Y > point.Y) != (b.Y > point.Y);
        if (!crosses)
          continue;
        var x = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
        if (point.X < x)
          inside = !inside;
      }
      return inside;
    }

    public static IEnumerable<Point2d> SampleAnchorPoints(PolygonLoop2d loop, int maximum)
    {
      if (loop == null || loop.Points.Count == 0)
        yield break;
      var count = Math.Max(1, loop.Points.Count - 1);
      var stride = Math.Max(1, count / Math.Max(1, maximum));
      for (var index = 0; index < count; index += stride)
        yield return loop.Points[index];
    }

    private static bool PointInRegion(PositionedOutline outline, Point2d point)
    {
      return PointInLoop(outline.Outer, point) && !outline.Holes.Any(hole => PointInLoop(hole, point));
    }

    private static bool TryRegionInteriorProbe(PositionedOutline outline, out Point2d point)
    {
      point = Point2d.Origin;
      if (outline == null || outline.Outer == null || !outline.Bounds.IsValid)
        return false;

      var first = InteriorProbe(outline.Outer);
      if (PointInRegion(outline, first))
      {
        point = first;
        return true;
      }

      for (var index = 0; index < outline.Outer.Points.Count - 1; index++)
      {
        var a = outline.Outer.Points[index];
        var b = outline.Outer.Points[index + 1];
        var midpoint = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
        foreach (var inset in new[] { 0.01, 0.025, 0.05, 0.1, 0.2 })
        {
          var sample = new Point2d(
            midpoint.X * (1.0 - inset) + first.X * inset,
            midpoint.Y * (1.0 - inset) + first.Y * inset);
          if (!PointInRegion(outline, sample))
            continue;
          point = sample;
          return true;
        }
      }

      const int divisions = 12;
      for (var y = 1; y < divisions; y++)
      {
        for (var x = 1; x < divisions; x++)
        {
          var sample = new Point2d(
            outline.Bounds.Min.X + outline.Bounds.Diagonal.X * x / divisions,
            outline.Bounds.Min.Y + outline.Bounds.Diagonal.Y * y / divisions);
          if (!PointInRegion(outline, sample))
            continue;
          point = sample;
          return true;
        }
      }
      return false;
    }

    private static bool LoopContainsLoop(
      PolygonLoop2d container,
      PolygonLoop2d candidate,
      double gap)
    {
      if (container == null || candidate == null ||
          !container.Bounds.IsValid || !candidate.Bounds.IsValid)
        return false;

      if (candidate.Bounds.Min.X < container.Bounds.Min.X + gap - 1e-8 ||
          candidate.Bounds.Min.Y < container.Bounds.Min.Y + gap - 1e-8 ||
          candidate.Bounds.Max.X > container.Bounds.Max.X - gap + 1e-8 ||
          candidate.Bounds.Max.Y > container.Bounds.Max.Y - gap + 1e-8)
        return false;

      var gapSquared = Math.Max(0.0, gap * gap);
      if (BoundaryDistanceLessThan(candidate, container, gapSquared))
        return false;

      for (var index = 0; index < candidate.Points.Count - 1; index++)
      {
        var point = candidate.Points[index];
        var next = candidate.Points[index + 1];
        if (!PointInLoop(container, point) ||
            !PointInLoop(container, new Point2d((point.X + next.X) * 0.5, (point.Y + next.Y) * 0.5)))
          return false;
      }
      return true;
    }

    private static IEnumerable<PolygonLoop2d> EnumerateLoops(PositionedOutline outline)
    {
      yield return outline.Outer;
      foreach (var hole in outline.Holes)
        yield return hole;
    }

    private static PolygonLoop2d SampleLoop(Curve curve, double chordTolerance)
    {
      var loop = new PolygonLoop2d();
      Polyline polyline;
      if (curve.TryGetPolyline(out polyline) && polyline.Count >= 3)
      {
        foreach (var point in polyline)
          AddDistinct(loop.Points, new Point2d(point.X, point.Y));
        SimplifyClosedPolyline(loop.Points, Math.Max(chordTolerance * 0.25, 1e-7));
      }
      else
      {
        var length = Math.Max(curve.GetLength(), chordTolerance);
        var count = Math.Max(24, Math.Min(720, (int)Math.Ceiling(length / Math.Max(chordTolerance, length / 360.0))));
        var parameters = curve.DivideByCount(count, true);
        if (parameters == null || parameters.Length < 3)
          return null;
        foreach (var parameter in parameters)
        {
          var point = curve.PointAt(parameter);
          AddDistinct(loop.Points, new Point2d(point.X, point.Y));
        }
      }

      if (loop.Points.Count < 3)
        return null;
      if (loop.Points[0].DistanceTo(loop.Points[loop.Points.Count - 1]) > 1e-8)
        loop.Points.Add(loop.Points[0]);
      FinalizeLoop(loop);
      return loop;
    }

    private static PolygonLoop2d TransformLoop(PolygonLoop2d source, double angle, double translationX, double translationY)
    {
      var result = new PolygonLoop2d();
      var cosine = Math.Cos(angle);
      var sine = Math.Sin(angle);
      foreach (var point in source.Points)
      {
        result.Points.Add(new Point2d(
          point.X * cosine - point.Y * sine + translationX,
          point.X * sine + point.Y * cosine + translationY));
      }
      FinalizeLoop(result);
      return result;
    }

    private static void SimplifyClosedPolyline(List<Point2d> points, double tolerance)
    {
      if (points == null || points.Count < 8)
        return;
      if (points[0].DistanceTo(points[points.Count - 1]) <= 1e-9)
        points.RemoveAt(points.Count - 1);
      if (points.Count < 7)
        return;

      var split = 1;
      var farthest = 0.0;
      for (var index = 1; index < points.Count; index++)
      {
        var distance = SquaredDistance(points[0], points[index]);
        if (distance <= farthest)
          continue;
        farthest = distance;
        split = index;
      }
      if (split <= 0 || split >= points.Count)
        return;

      var firstChain = points.Take(split + 1).ToList();
      var secondChain = points.Skip(split).ToList();
      secondChain.Add(points[0]);
      var firstSimplified = SimplifyOpenPolyline(firstChain, tolerance);
      var secondSimplified = SimplifyOpenPolyline(secondChain, tolerance);

      points.Clear();
      points.AddRange(firstSimplified);
      points.AddRange(secondSimplified.Skip(1));
    }

    private static List<Point2d> SimplifyOpenPolyline(IList<Point2d> points, double tolerance)
    {
      if (points.Count <= 2)
        return points.ToList();
      var keep = new bool[points.Count];
      keep[0] = true;
      keep[points.Count - 1] = true;
      var pending = new Stack<Tuple<int, int>>();
      pending.Push(Tuple.Create(0, points.Count - 1));
      var toleranceSquared = tolerance * tolerance;

      while (pending.Count > 0)
      {
        var range = pending.Pop();
        var bestIndex = -1;
        var bestDistance = toleranceSquared;
        for (var index = range.Item1 + 1; index < range.Item2; index++)
        {
          var distance = PointSegmentDistanceSquared(points[index], points[range.Item1], points[range.Item2]);
          if (distance <= bestDistance)
            continue;
          bestDistance = distance;
          bestIndex = index;
        }
        if (bestIndex < 0)
          continue;
        keep[bestIndex] = true;
        pending.Push(Tuple.Create(range.Item1, bestIndex));
        pending.Push(Tuple.Create(bestIndex, range.Item2));
      }

      return points.Where((point, index) => keep[index]).ToList();
    }

    private static void FinalizeLoop(PolygonLoop2d loop)
    {
      loop.SignedArea = SignedArea(loop.Points);
      if (loop.Points.Count == 0)
      {
        loop.Bounds = BoundingBox.Unset;
        return;
      }
      var minX = loop.Points.Min(item => item.X);
      var minY = loop.Points.Min(item => item.Y);
      var maxX = loop.Points.Max(item => item.X);
      var maxY = loop.Points.Max(item => item.Y);
      loop.Bounds = new BoundingBox(new Point3d(minX, minY, 0.0), new Point3d(maxX, maxY, 0.0));
    }

    private static double SignedArea(IList<Point2d> points)
    {
      var area = 0.0;
      for (var index = 0; index < points.Count - 1; index++)
        area += points[index].X * points[index + 1].Y - points[index + 1].X * points[index].Y;
      return area * 0.5;
    }

    private static Point2d PolygonCentroid(PolygonLoop2d loop)
    {
      var crossSum = 0.0;
      var x = 0.0;
      var y = 0.0;
      for (var index = 0; index < loop.Points.Count - 1; index++)
      {
        var a = loop.Points[index];
        var b = loop.Points[index + 1];
        var cross = a.X * b.Y - b.X * a.Y;
        crossSum += cross;
        x += (a.X + b.X) * cross;
        y += (a.Y + b.Y) * cross;
      }
      if (Math.Abs(crossSum) < 1e-12)
        return loop.Points[0];
      return new Point2d(x / (3.0 * crossSum), y / (3.0 * crossSum));
    }

    private static bool BoundaryDistanceLessThan(PolygonLoop2d left, PolygonLoop2d right, double gapSquared)
    {
      var gap = Math.Sqrt(Math.Max(0.0, gapSquared));
      for (var leftIndex = 0; leftIndex < left.Points.Count - 1; leftIndex++)
      {
        var a = left.Points[leftIndex];
        var b = left.Points[leftIndex + 1];
        for (var rightIndex = 0; rightIndex < right.Points.Count - 1; rightIndex++)
        {
          var c = right.Points[rightIndex];
          var d = right.Points[rightIndex + 1];
          if (SegmentsSeparated(a, b, c, d, gap))
            continue;
          if (SegmentsIntersect(a, b, c, d))
            return true;
          if (gapSquared > 0.0 && SegmentDistanceSquared(a, b, c, d) < gapSquared - 1e-10)
            return true;
        }
      }
      return false;
    }

    private static bool BoundsSeparated(BoundingBox left, BoundingBox right, double gap)
    {
      return left.Max.X + gap <= right.Min.X || right.Max.X + gap <= left.Min.X ||
             left.Max.Y + gap <= right.Min.Y || right.Max.Y + gap <= left.Min.Y;
    }

    private static bool SegmentsSeparated(Point2d a, Point2d b, Point2d c, Point2d d, double gap)
    {
      var leftMinX = Math.Min(a.X, b.X);
      var leftMaxX = Math.Max(a.X, b.X);
      var leftMinY = Math.Min(a.Y, b.Y);
      var leftMaxY = Math.Max(a.Y, b.Y);
      var rightMinX = Math.Min(c.X, d.X);
      var rightMaxX = Math.Max(c.X, d.X);
      var rightMinY = Math.Min(c.Y, d.Y);
      var rightMaxY = Math.Max(c.Y, d.Y);
      return leftMaxX + gap <= rightMinX || rightMaxX + gap <= leftMinX ||
             leftMaxY + gap <= rightMinY || rightMaxY + gap <= leftMinY;
    }

    private static bool SegmentsIntersect(Point2d a, Point2d b, Point2d c, Point2d d)
    {
      var abC = Cross(a, b, c);
      var abD = Cross(a, b, d);
      var cdA = Cross(c, d, a);
      var cdB = Cross(c, d, b);
      return abC * abD <= 1e-12 && cdA * cdB <= 1e-12 &&
             Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) <= Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) + 1e-10 &&
             Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) <= Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) + 1e-10;
    }

    private static double SegmentDistanceSquared(Point2d a, Point2d b, Point2d c, Point2d d)
    {
      return Math.Min(
        Math.Min(PointSegmentDistanceSquared(a, c, d), PointSegmentDistanceSquared(b, c, d)),
        Math.Min(PointSegmentDistanceSquared(c, a, b), PointSegmentDistanceSquared(d, a, b)));
    }

    private static double PointSegmentDistanceSquared(Point2d point, Point2d a, Point2d b)
    {
      var dx = b.X - a.X;
      var dy = b.Y - a.Y;
      var denominator = dx * dx + dy * dy;
      if (denominator <= 1e-20)
        return SquaredDistance(point, a);
      var parameter = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / denominator;
      parameter = Math.Max(0.0, Math.Min(1.0, parameter));
      return SquaredDistance(point, new Point2d(a.X + parameter * dx, a.Y + parameter * dy));
    }

    private static double SquaredDistance(Point2d left, Point2d right)
    {
      var dx = left.X - right.X;
      var dy = left.Y - right.Y;
      return dx * dx + dy * dy;
    }

    private static double Cross(Point2d a, Point2d b, Point2d c)
    {
      return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    private static void AddDistinct(ICollection<Point2d> points, Point2d point)
    {
      var list = points as IList<Point2d>;
      if (list != null && list.Count > 0 && list[list.Count - 1].DistanceTo(point) <= 1e-9)
        return;
      points.Add(point);
    }
  }
}
