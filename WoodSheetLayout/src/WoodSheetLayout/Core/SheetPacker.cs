using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace WoodSheetLayout.Core
{
  internal static class SheetPacker
  {
    public static LayoutResult Pack(
      IEnumerable<BoardPart> sourceParts,
      LayoutSettings settings,
      Point2d outputOrigin)
    {
      var result = new LayoutResult();
      var buckets = BuildThicknessBuckets(sourceParts, settings.ThicknessToleranceMillimeters);
      var globalSheetIndex = 0;

      foreach (var bucket in buckets)
      {
        var validParts = new List<BoardPart>();
        foreach (var part in bucket.Parts)
        {
          if (FitsEmptySheet(part, settings))
            validParts.Add(part);
          else
            result.OversizedParts.Add(part);
        }
        if (validParts.Count == 0)
          continue;

        var attempt = FindBestAttempt(validParts, settings);
        for (var index = 0; index < attempt.Sheets.Count; index++)
        {
          var packed = new PackedSheet
          {
            GlobalIndex = ++globalSheetIndex,
            IndexWithinThickness = index + 1,
            ThicknessMillimeters = bucket.RepresentativeThickness
          };
          packed.Placements.AddRange(attempt.Sheets[index].Placements);
          result.Sheets.Add(packed);
        }
      }

      var cursorX = outputOrigin.X;
      foreach (var sheet in result.Sheets)
      {
        sheet.Origin = new Point2d(cursorX, outputOrigin.Y);
        cursorX += settings.SheetWidth + settings.SheetGap;
      }
      return result;
    }

    private static PackAttempt FindBestAttempt(IList<BoardPart> parts, LayoutSettings settings)
    {
      PackAttempt best = null;
      foreach (SortStrategy sort in Enum.GetValues(typeof(SortStrategy)))
      {
        foreach (PackingHeuristic heuristic in Enum.GetValues(typeof(PackingHeuristic)))
        {
          var attempt = RunAttempt(parts, settings, sort, heuristic);
          if (best == null || IsBetter(attempt, best))
            best = attempt;
        }
      }
      return best;
    }

    private static PackAttempt RunAttempt(
      IEnumerable<BoardPart> parts,
      LayoutSettings settings,
      SortStrategy sort,
      PackingHeuristic heuristic)
    {
      var ordered = SortParts(parts, sort).ToList();
      var attempt = new PackAttempt();
      foreach (var part in ordered)
      {
        MaxRectsSheet destination = null;
        PackCandidate selected = null;

        // 优先填充前面的板框，只有放不下才开新板框。
        foreach (var sheet in attempt.Sheets)
        {
          PackCandidate candidate;
          if (!sheet.TryFindBest(part, heuristic, out candidate))
            continue;
          destination = sheet;
          selected = candidate;
          break;
        }

        if (destination == null)
        {
          destination = new MaxRectsSheet(settings.SheetWidth, settings.SheetHeight, settings.Spacing);
          attempt.Sheets.Add(destination);
          if (!destination.TryFindBest(part, heuristic, out selected))
            throw new InvalidOperationException("板件在空白边界框中仍无法放置。");
        }
        destination.Place(selected);
      }
      return attempt;
    }

    private static bool IsBetter(PackAttempt candidate, PackAttempt current)
    {
      if (candidate.Sheets.Count != current.Sheets.Count)
        return candidate.Sheets.Count < current.Sheets.Count;

      var candidateLast = candidate.Sheets.Last().UsedArea;
      var currentLast = current.Sheets.Last().UsedArea;
      if (Math.Abs(candidateLast - currentLast) > 1e-8)
        return candidateLast < currentLast;

      return candidate.Sheets.Sum(sheet => sheet.OccupiedBoundingArea) <
             current.Sheets.Sum(sheet => sheet.OccupiedBoundingArea);
    }

    private static IEnumerable<BoardPart> SortParts(IEnumerable<BoardPart> parts, SortStrategy strategy)
    {
      switch (strategy)
      {
        case SortStrategy.Area:
          return parts.OrderByDescending(Area).ThenByDescending(MaxSide);
        case SortStrategy.Width:
          return parts.OrderByDescending(item => item.FlatBounds.Diagonal.X).ThenByDescending(Area);
        case SortStrategy.Height:
          return parts.OrderByDescending(item => item.FlatBounds.Diagonal.Y).ThenByDescending(Area);
        default:
          return parts.OrderByDescending(MaxSide).ThenByDescending(Area);
      }
    }

    private static bool FitsEmptySheet(BoardPart part, LayoutSettings settings)
    {
      var usableWidth = settings.SheetWidth - 2.0 * settings.Spacing;
      var usableHeight = settings.SheetHeight - 2.0 * settings.Spacing;
      var width = part.FlatBounds.Max.X - part.FlatBounds.Min.X;
      var height = part.FlatBounds.Max.Y - part.FlatBounds.Min.Y;
      return (width <= usableWidth && height <= usableHeight) ||
             (height <= usableWidth && width <= usableHeight);
    }

    private static double Area(BoardPart part)
    {
      return Math.Abs((part.FlatBounds.Max.X - part.FlatBounds.Min.X) *
                      (part.FlatBounds.Max.Y - part.FlatBounds.Min.Y));
    }

    private static double MaxSide(BoardPart part)
    {
      return Math.Max(
        Math.Abs(part.FlatBounds.Max.X - part.FlatBounds.Min.X),
        Math.Abs(part.FlatBounds.Max.Y - part.FlatBounds.Min.Y));
    }

    private static List<ThicknessBucket> BuildThicknessBuckets(
      IEnumerable<BoardPart> parts,
      double toleranceMillimeters)
    {
      var buckets = new List<ThicknessBucket>();
      foreach (var part in parts.OrderBy(item => item.ThicknessMillimeters))
      {
        var bucket = buckets.FirstOrDefault(item =>
          Math.Abs(item.RepresentativeThickness - part.ThicknessMillimeters) <= toleranceMillimeters);
        if (bucket == null)
        {
          bucket = new ThicknessBucket { RepresentativeThickness = part.ThicknessMillimeters };
          buckets.Add(bucket);
        }
        bucket.Parts.Add(part);
        bucket.RepresentativeThickness = bucket.Parts.Average(item => item.ThicknessMillimeters);
      }
      return buckets;
    }

    private static BoundingBox OrientedBounds(BoundingBox bounds, bool rotated)
    {
      if (!rotated)
        return bounds;
      var rotation = Transform.Rotation(Math.PI * 0.5, Vector3d.ZAxis, Point3d.Origin);
      var result = BoundingBox.Unset;
      foreach (var corner in bounds.GetCorners())
      {
        var point = corner;
        point.Transform(rotation);
        result = result.IsValid ? BoundingBox.Union(result, point) : new BoundingBox(point, point);
      }
      return result;
    }

    private enum SortStrategy
    {
      Area,
      MaxSide,
      Width,
      Height
    }

    private enum PackingHeuristic
    {
      BestShortSide,
      BestArea,
      BottomLeft
    }

    private sealed class ThicknessBucket
    {
      public double RepresentativeThickness { get; set; }
      public List<BoardPart> Parts { get; } = new List<BoardPart>();
    }

    private sealed class PackAttempt
    {
      public List<MaxRectsSheet> Sheets { get; } = new List<MaxRectsSheet>();
    }

    private sealed class PackCandidate
    {
      public BoardPart Part { get; set; }
      public bool Rotated { get; set; }
      public BoundingBox Bounds { get; set; }
      public RectD Rectangle { get; set; }
      public double ScoreA { get; set; }
      public double ScoreB { get; set; }
    }

    private sealed class MaxRectsSheet
    {
      private readonly double _gap;
      private readonly List<RectD> _free = new List<RectD>();

      public MaxRectsSheet(double width, double height, double gap)
      {
        _gap = gap;
        _free.Add(new RectD(gap, gap, width - 2.0 * gap, height - 2.0 * gap));
      }

      public List<PartPlacement> Placements { get; } = new List<PartPlacement>();
      public double UsedArea { get; private set; }

      public double OccupiedBoundingArea
      {
        get
        {
          if (Placements.Count == 0)
            return 0.0;
          var right = Placements.Max(item => item.LocalX + item.OrientedBounds.Max.X - item.OrientedBounds.Min.X);
          var top = Placements.Max(item => item.LocalY + item.OrientedBounds.Max.Y - item.OrientedBounds.Min.Y);
          return right * top;
        }
      }

      public bool TryFindBest(BoardPart part, PackingHeuristic heuristic, out PackCandidate best)
      {
        best = null;
        foreach (var free in _free)
        {
          foreach (var rotated in new[] { false, true })
          {
            var bounds = OrientedBounds(part.FlatBounds, rotated);
            var width = bounds.Max.X - bounds.Min.X;
            var height = bounds.Max.Y - bounds.Min.Y;
            if (width > free.Width + 1e-9 || height > free.Height + 1e-9)
              continue;

            var candidate = new PackCandidate
            {
              Part = part,
              Rotated = rotated,
              Bounds = bounds,
              Rectangle = new RectD(free.X, free.Y, width, height)
            };
            Score(candidate, free, heuristic);
            if (best == null || candidate.ScoreA < best.ScoreA - 1e-9 ||
                (Math.Abs(candidate.ScoreA - best.ScoreA) <= 1e-9 && candidate.ScoreB < best.ScoreB))
              best = candidate;

            if (Math.Abs(width - height) <= 1e-9)
              break;
          }
        }
        return best != null;
      }

      public void Place(PackCandidate candidate)
      {
        var occupied = candidate.Rectangle;
        SplitFreeRectangles(occupied);
        PruneFreeRectangles();
        Placements.Add(new PartPlacement
        {
          Part = candidate.Part,
          RotatedNinetyDegrees = candidate.Rotated,
          LocalX = occupied.X,
          LocalY = occupied.Y,
          OrientedBounds = candidate.Bounds
        });
        UsedArea += occupied.Width * occupied.Height;
      }

      private static void Score(PackCandidate candidate, RectD free, PackingHeuristic heuristic)
      {
        var horizontal = free.Width - candidate.Rectangle.Width;
        var vertical = free.Height - candidate.Rectangle.Height;
        switch (heuristic)
        {
          case PackingHeuristic.BestArea:
            candidate.ScoreA = free.Width * free.Height - candidate.Rectangle.Width * candidate.Rectangle.Height;
            candidate.ScoreB = Math.Min(horizontal, vertical);
            break;
          case PackingHeuristic.BottomLeft:
            candidate.ScoreA = candidate.Rectangle.Top;
            candidate.ScoreB = candidate.Rectangle.X;
            break;
          default:
            candidate.ScoreA = Math.Min(horizontal, vertical);
            candidate.ScoreB = Math.Max(horizontal, vertical);
            break;
        }
      }

      private void SplitFreeRectangles(RectD occupied)
      {
        var next = new List<RectD>();
        foreach (var free in _free)
        {
          if (!free.Intersects(occupied))
          {
            next.Add(free);
            continue;
          }

          var leftRight = occupied.X - _gap;
          if (leftRight > free.X)
            next.Add(RectD.FromEdges(free.X, free.Y, leftRight, free.Top));

          var rightLeft = occupied.Right + _gap;
          if (rightLeft < free.Right)
            next.Add(RectD.FromEdges(rightLeft, free.Y, free.Right, free.Top));

          var bottomTop = occupied.Y - _gap;
          if (bottomTop > free.Y)
            next.Add(RectD.FromEdges(free.X, free.Y, free.Right, bottomTop));

          var topBottom = occupied.Top + _gap;
          if (topBottom < free.Top)
            next.Add(RectD.FromEdges(free.X, topBottom, free.Right, free.Top));
        }
        _free.Clear();
        _free.AddRange(next.Where(item => item.Width > 1e-9 && item.Height > 1e-9));
      }

      private void PruneFreeRectangles()
      {
        for (var left = _free.Count - 1; left >= 0; left--)
        {
          for (var right = _free.Count - 1; right >= 0; right--)
          {
            if (left == right || !_free[right].Contains(_free[left]))
              continue;
            _free.RemoveAt(left);
            break;
          }
        }
      }
    }

    private struct RectD
    {
      public RectD(double x, double y, double width, double height)
      {
        X = x;
        Y = y;
        Width = width;
        Height = height;
      }

      public double X { get; }
      public double Y { get; }
      public double Width { get; }
      public double Height { get; }
      public double Right => X + Width;
      public double Top => Y + Height;

      public static RectD FromEdges(double left, double bottom, double right, double top)
      {
        return new RectD(left, bottom, right - left, top - bottom);
      }

      public bool Intersects(RectD other)
      {
        return X < other.Right && Right > other.X && Y < other.Top && Top > other.Y;
      }

      public bool Contains(RectD other)
      {
        return other.X >= X - 1e-9 && other.Y >= Y - 1e-9 &&
               other.Right <= Right + 1e-9 && other.Top <= Top + 1e-9;
      }
    }
  }
}
