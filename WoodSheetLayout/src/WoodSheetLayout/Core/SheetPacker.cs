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
      Point2d outputOrigin,
      LayoutProgress progress)
    {
      var result = new LayoutResult();
      var buckets = BuildThicknessBuckets(sourceParts, settings.ThicknessToleranceMillimeters);
      var globalSheetIndex = 0;
      if (progress != null)
      {
        var totalSteps = buckets.Sum(item => item.Parts.Count * StrategyCount(item.Parts, settings));
        progress.BeginPacking(totalSteps);
      }

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

        if (settings.Packing == PackingMode.Fast)
        {
          var fastAttempt = FindBestFastAttempt(validParts, settings, progress);
          for (var index = 0; index < fastAttempt.Sheets.Count; index++)
          {
            var packed = CreatePackedSheet(
              ++globalSheetIndex,
              index + 1,
              bucket.RepresentativeThickness,
              fastAttempt.Sheets[index].UsedPartArea,
              fastAttempt.Sheets[index].Placements);
            result.Sheets.Add(packed);
          }
        }
        else
        {
          var contourAttempt = FindBestContourAttempt(validParts, settings, progress);
          for (var index = 0; index < contourAttempt.Sheets.Count; index++)
          {
            var packed = CreatePackedSheet(
              ++globalSheetIndex,
              index + 1,
              bucket.RepresentativeThickness,
              contourAttempt.Sheets[index].UsedPartArea,
              contourAttempt.Sheets[index].Placements);
            result.Sheets.Add(packed);
          }
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

    private static PackedSheet CreatePackedSheet(
      int globalIndex,
      int indexWithinThickness,
      double thicknessMillimeters,
      double usedPartArea,
      IEnumerable<PartPlacement> placements)
    {
      var packed = new PackedSheet
      {
        GlobalIndex = globalIndex,
        IndexWithinThickness = indexWithinThickness,
        ThicknessMillimeters = thicknessMillimeters,
        UsedPartArea = usedPartArea
      };
      packed.Placements.AddRange(placements);
      return packed;
    }

    private static ContourPackAttempt FindBestContourAttempt(
      IList<BoardPart> parts,
      LayoutSettings settings,
      LayoutProgress progress)
    {
      ContourPackAttempt best = null;
      foreach (var sort in ContourStrategiesFor(parts, settings))
      {
        var attempt = RunContourAttempt(parts, settings, sort, progress);
        if (best == null || IsBetterContour(attempt, best))
          best = attempt;
      }
      return best;
    }

    private static ContourPackAttempt RunContourAttempt(
      IEnumerable<BoardPart> parts,
      LayoutSettings settings,
      SortStrategy sort,
      LayoutProgress progress)
    {
      var ordered = SortParts(parts, sort).ToList();
      var attempt = new ContourPackAttempt();
      foreach (var part in ordered)
      {
        ContourSheet destination = null;
        PlacementCandidate selected = null;

        // 优先继续填充已有板框；真实轮廓允许凹槽互补和放入大孔洞。
        foreach (var sheet in attempt.Sheets)
        {
          PlacementCandidate candidate;
          if (!sheet.TryFindBest(part, out candidate))
            continue;
          destination = sheet;
          selected = candidate;
          break;
        }

        if (destination == null)
        {
          destination = new ContourSheet(settings, progress);
          attempt.Sheets.Add(destination);
          if (!destination.TryFindBest(part, out selected))
            throw new InvalidOperationException("板件在空白边界框中仍无法放置。");
        }
        destination.Place(selected);
        if (progress != null && !progress.CompletePackingStep())
          throw new OperationCanceledException();
      }
      return attempt;
    }

    private static FastPackAttempt FindBestFastAttempt(
      IList<BoardPart> parts,
      LayoutSettings settings,
      LayoutProgress progress)
    {
      FastPackAttempt best = null;
      foreach (var sort in new[]
      {
        SortStrategy.NetArea,
        SortStrategy.MaxSide,
        SortStrategy.Width,
        SortStrategy.Height
      })
      {
        foreach (FastPackingHeuristic heuristic in Enum.GetValues(typeof(FastPackingHeuristic)))
        {
          var attempt = RunFastAttempt(parts, settings, sort, heuristic, progress);
          if (best == null || IsBetterFast(attempt, best))
            best = attempt;
        }
      }
      return best;
    }

    private static FastPackAttempt RunFastAttempt(
      IEnumerable<BoardPart> parts,
      LayoutSettings settings,
      SortStrategy sort,
      FastPackingHeuristic heuristic,
      LayoutProgress progress)
    {
      var ordered = SortFastParts(parts, sort).ToList();
      var attempt = new FastPackAttempt();
      foreach (var part in ordered)
      {
        FastMaxRectsSheet destination = null;
        FastPackCandidate selected = null;

        // 1.1 的工作流：优先填满已有板框，放不下才自动增加下一张。
        foreach (var sheet in attempt.Sheets)
        {
          FastPackCandidate candidate;
          if (!sheet.TryFindBest(part, heuristic, out candidate))
            continue;
          destination = sheet;
          selected = candidate;
          break;
        }

        if (destination == null)
        {
          destination = new FastMaxRectsSheet(settings);
          attempt.Sheets.Add(destination);
          if (!destination.TryFindBest(part, heuristic, out selected))
            throw new InvalidOperationException("板件在空白边界框中仍无法放置。");
        }

        destination.Place(selected);
        if (progress != null && !progress.CompletePackingStep())
          throw new OperationCanceledException();
      }
      return attempt;
    }

    private static bool IsBetterFast(FastPackAttempt candidate, FastPackAttempt current)
    {
      if (candidate.Sheets.Count != current.Sheets.Count)
        return candidate.Sheets.Count < current.Sheets.Count;

      var candidateLast = candidate.Sheets.Last().UsedBoundingArea;
      var currentLast = current.Sheets.Last().UsedBoundingArea;
      if (Math.Abs(candidateLast - currentLast) > 1e-8)
        return candidateLast < currentLast;

      return candidate.Sheets.Sum(sheet => sheet.OccupiedBoundingArea) <
             current.Sheets.Sum(sheet => sheet.OccupiedBoundingArea);
    }

    private static IEnumerable<BoardPart> SortFastParts(
      IEnumerable<BoardPart> parts,
      SortStrategy strategy)
    {
      switch (strategy)
      {
        case SortStrategy.NetArea:
          return parts.OrderByDescending(BoundingArea).ThenByDescending(FastMaxSide);
        case SortStrategy.Width:
          return parts.OrderByDescending(item => item.FlatBounds.Diagonal.X).ThenByDescending(BoundingArea);
        case SortStrategy.Height:
          return parts.OrderByDescending(item => item.FlatBounds.Diagonal.Y).ThenByDescending(BoundingArea);
        default:
          return parts.OrderByDescending(FastMaxSide).ThenByDescending(BoundingArea);
      }
    }

    private static IEnumerable<SortStrategy> ContourStrategiesFor(
      IList<BoardPart> parts,
      LayoutSettings settings)
    {
      if (settings.EnableHoleNesting && parts.Any(item => HoleArea(item) > 1e-8))
        yield return SortStrategy.HoleFirst;
      yield return SortStrategy.NetArea;
      yield return SortStrategy.MaxSide;
      if (parts.Count <= 24)
      {
        yield return SortStrategy.Width;
        yield return SortStrategy.Height;
      }
    }

    private static int StrategyCount(IList<BoardPart> parts, LayoutSettings settings)
    {
      if (settings.Packing == PackingMode.Fast)
        return 12;
      return (parts.Count <= 24 ? 4 : 2) +
             (settings.EnableHoleNesting && parts.Any(item => HoleArea(item) > 1e-8) ? 1 : 0);
    }

    private static bool IsBetterContour(ContourPackAttempt candidate, ContourPackAttempt current)
    {
      if (candidate.Sheets.Count != current.Sheets.Count)
        return candidate.Sheets.Count < current.Sheets.Count;

      var candidateNested = candidate.Sheets.Sum(item => item.Placements.Count(placement => placement.NestedInsideHole));
      var currentNested = current.Sheets.Sum(item => item.Placements.Count(placement => placement.NestedInsideHole));
      if (candidateNested != currentNested)
        return candidateNested > currentNested;

      var candidateOccupied = candidate.Sheets.Sum(item => item.OccupiedBoundingArea);
      var currentOccupied = current.Sheets.Sum(item => item.OccupiedBoundingArea);
      return candidateOccupied < currentOccupied - 1e-8;
    }

    private static IEnumerable<BoardPart> SortParts(IEnumerable<BoardPart> parts, SortStrategy strategy)
    {
      switch (strategy)
      {
        case SortStrategy.NetArea:
          return parts.OrderByDescending(Area).ThenByDescending(MaxSide);
        case SortStrategy.Width:
          return parts.OrderByDescending(item => item.Outline.Bounds.Diagonal.X).ThenByDescending(Area);
        case SortStrategy.Height:
          return parts.OrderByDescending(item => item.Outline.Bounds.Diagonal.Y).ThenByDescending(Area);
        case SortStrategy.HoleFirst:
          return parts.OrderByDescending(HoleArea).ThenByDescending(OuterArea).ThenByDescending(MaxSide);
        default:
          return parts.OrderByDescending(MaxSide).ThenByDescending(Area);
      }
    }

    private static bool FitsEmptySheet(BoardPart part, LayoutSettings settings)
    {
      if (part == null)
        return false;
      if (settings.Packing == PackingMode.Fast)
      {
        foreach (var angle in settings.RotationAnglesRadians())
        {
          var bounds = RotatedFlatBounds(part.FlatBounds, angle);
          if (bounds.IsValid &&
              bounds.Diagonal.X <= settings.SheetWidth - 2.0 * settings.FrameMargin + 1e-8 &&
              bounds.Diagonal.Y <= settings.SheetHeight - 2.0 * settings.FrameMargin + 1e-8)
            return true;
        }
        return false;
      }
      if (part.Outline == null)
        return false;
      foreach (var angle in settings.RotationAnglesRadians())
      {
        var bounds = OutlineGeometry.RotatedBounds(part.Outline, angle);
        if (!bounds.IsValid)
          continue;
        if (bounds.Diagonal.X <= settings.SheetWidth - 2.0 * settings.FrameMargin + 1e-8 &&
            bounds.Diagonal.Y <= settings.SheetHeight - 2.0 * settings.FrameMargin + 1e-8)
          return true;
      }
      return false;
    }

    private static double Area(BoardPart part)
    {
      return part.Outline == null ? 0.0 : Math.Abs(part.Outline.NetArea);
    }

    private static double BoundingArea(BoardPart part)
    {
      if (part == null || !part.FlatBounds.IsValid)
        return 0.0;
      return Math.Abs(part.FlatBounds.Diagonal.X * part.FlatBounds.Diagonal.Y);
    }

    private static double FastMaxSide(BoardPart part)
    {
      if (part == null || !part.FlatBounds.IsValid)
        return 0.0;
      return Math.Max(part.FlatBounds.Diagonal.X, part.FlatBounds.Diagonal.Y);
    }

    private static BoundingBox RotatedFlatBounds(BoundingBox bounds, double angle)
    {
      if (!bounds.IsValid || Math.Abs(angle) <= 1e-12)
        return bounds;
      var rotation = Transform.Rotation(angle, Vector3d.ZAxis, Point3d.Origin);
      var result = BoundingBox.Unset;
      foreach (var corner in bounds.GetCorners())
      {
        var point = corner;
        point.Transform(rotation);
        result = result.IsValid ? BoundingBox.Union(result, point) : new BoundingBox(point, point);
      }
      return result;
    }

    private static double MaxSide(BoardPart part)
    {
      if (part.Outline == null || !part.Outline.Bounds.IsValid)
        return 0.0;
      return Math.Max(part.Outline.Bounds.Diagonal.X, part.Outline.Bounds.Diagonal.Y);
    }

    private static double HoleArea(BoardPart part)
    {
      return part.Outline == null
        ? 0.0
        : part.Outline.Holes.Sum(item => Math.Abs(item.SignedArea));
    }

    private static double OuterArea(BoardPart part)
    {
      return part.Outline == null || part.Outline.Outer == null
        ? 0.0
        : Math.Abs(part.Outline.Outer.SignedArea);
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

    private enum SortStrategy
    {
      NetArea,
      MaxSide,
      Width,
      Height,
      HoleFirst
    }

    private enum FastPackingHeuristic
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

    private sealed class FastPackAttempt
    {
      public List<FastMaxRectsSheet> Sheets { get; } = new List<FastMaxRectsSheet>();
    }

    private sealed class FastPackCandidate
    {
      public BoardPart Part { get; set; }
      public double Angle { get; set; }
      public double TranslationX { get; set; }
      public double TranslationY { get; set; }
      public RectD Rectangle { get; set; }
      public PositionedOutline Outline { get; set; }
      public double ScoreA { get; set; }
      public double ScoreB { get; set; }
    }

    private sealed class FastMaxRectsSheet
    {
      private readonly LayoutSettings _settings;
      private readonly List<RectD> _free = new List<RectD>();

      public FastMaxRectsSheet(LayoutSettings settings)
      {
        _settings = settings;
        _free.Add(new RectD(
          settings.FrameMargin,
          settings.FrameMargin,
          settings.SheetWidth - 2.0 * settings.FrameMargin,
          settings.SheetHeight - 2.0 * settings.FrameMargin));
      }

      public List<PartPlacement> Placements { get; } = new List<PartPlacement>();
      public double UsedBoundingArea { get; private set; }
      public double UsedPartArea { get; private set; }

      public double OccupiedBoundingArea
      {
        get
        {
          if (Placements.Count == 0)
            return 0.0;
          var right = Placements.Max(item => item.PositionedOutline.Bounds.Max.X);
          var top = Placements.Max(item => item.PositionedOutline.Bounds.Max.Y);
          return right * top;
        }
      }

      public bool TryFindBest(
        BoardPart part,
        FastPackingHeuristic heuristic,
        out FastPackCandidate best)
      {
        best = null;
        foreach (var free in _free)
        {
          foreach (var angle in _settings.RotationAnglesRadians())
          {
            var bounds = RotatedFlatBounds(part.FlatBounds, angle);
            if (!bounds.IsValid)
              continue;
            var width = bounds.Diagonal.X;
            var height = bounds.Diagonal.Y;
            if (width > free.Width + 1e-9 || height > free.Height + 1e-9)
              continue;

            var translationX = free.X - bounds.Min.X;
            var translationY = free.Y - bounds.Min.Y;
            var positioned = OutlineGeometry.Position(part.Outline, angle, translationX, translationY);
            if (positioned == null)
              continue;

            var candidate = new FastPackCandidate
            {
              Part = part,
              Angle = angle,
              TranslationX = translationX,
              TranslationY = translationY,
              Rectangle = new RectD(free.X, free.Y, width, height),
              Outline = positioned
            };
            ScoreFast(candidate, free, heuristic);
            if (best == null || candidate.ScoreA < best.ScoreA - 1e-9 ||
                (Math.Abs(candidate.ScoreA - best.ScoreA) <= 1e-9 && candidate.ScoreB < best.ScoreB))
              best = candidate;

            if (_settings.GrainDirectionLocked || Math.Abs(width - height) <= 1e-9)
              break;
          }
        }
        return best != null;
      }

      public void Place(FastPackCandidate candidate)
      {
        var occupied = candidate.Rectangle;
        SplitFreeRectangles(occupied);
        PruneFreeRectangles();
        Placements.Add(new PartPlacement
        {
          Part = candidate.Part,
          RotationRadians = candidate.Angle,
          TranslationX = candidate.TranslationX,
          TranslationY = candidate.TranslationY,
          OrientedBounds = candidate.Outline.Bounds,
          PositionedOutline = candidate.Outline,
          NestedInsideHole = false
        });
        UsedBoundingArea += occupied.Width * occupied.Height;
        UsedPartArea += candidate.Part.Outline.NetArea;
      }

      private static void ScoreFast(
        FastPackCandidate candidate,
        RectD free,
        FastPackingHeuristic heuristic)
      {
        var horizontal = free.Width - candidate.Rectangle.Width;
        var vertical = free.Height - candidate.Rectangle.Height;
        switch (heuristic)
        {
          case FastPackingHeuristic.BestArea:
            candidate.ScoreA = free.Width * free.Height -
                               candidate.Rectangle.Width * candidate.Rectangle.Height;
            candidate.ScoreB = Math.Min(horizontal, vertical);
            break;
          case FastPackingHeuristic.BottomLeft:
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

          var leftRight = occupied.X - _settings.PartGap;
          if (leftRight > free.X)
            next.Add(RectD.FromEdges(free.X, free.Y, leftRight, free.Top));

          var rightLeft = occupied.Right + _settings.PartGap;
          if (rightLeft < free.Right)
            next.Add(RectD.FromEdges(rightLeft, free.Y, free.Right, free.Top));

          var bottomTop = occupied.Y - _settings.PartGap;
          if (bottomTop > free.Y)
            next.Add(RectD.FromEdges(free.X, free.Y, free.Right, bottomTop));

          var topBottom = occupied.Top + _settings.PartGap;
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

    private sealed class ContourPackAttempt
    {
      public List<ContourSheet> Sheets { get; } = new List<ContourSheet>();
    }

    private sealed class PlacementCandidate
    {
      public BoardPart Part { get; set; }
      public double Angle { get; set; }
      public double TranslationX { get; set; }
      public double TranslationY { get; set; }
      public PositionedOutline Outline { get; set; }
      public bool NestedInsideHole { get; set; }
      public double Score { get; set; }
    }

    private sealed class ContourSheet
    {
      private const int MaximumCandidateTranslationsPerAngle = 2200;
      private readonly LayoutSettings _settings;
      private readonly LayoutProgress _progress;

      public ContourSheet(LayoutSettings settings, LayoutProgress progress)
      {
        _settings = settings;
        _progress = progress;
      }

      public List<PartPlacement> Placements { get; } = new List<PartPlacement>();
      public double UsedPartArea { get; private set; }

      public double OccupiedBoundingArea
      {
        get
        {
          if (Placements.Count == 0)
            return 0.0;
          var right = Placements.Max(item => item.PositionedOutline.Bounds.Max.X);
          var top = Placements.Max(item => item.PositionedOutline.Bounds.Max.Y);
          return right * top;
        }
      }

      public bool TryFindBest(BoardPart part, out PlacementCandidate best)
      {
        best = null;
        foreach (var angle in _settings.RotationAnglesRadians())
        {
          var rotated = OutlineGeometry.Position(part.Outline, angle, 0.0, 0.0);
          if (rotated == null || !rotated.Bounds.IsValid)
            continue;

          var candidateIndex = 0;
          foreach (var translation in CandidateTranslations(rotated))
          {
            if ((candidateIndex++ & 63) == 0 && _progress != null && !_progress.Pulse())
              throw new OperationCanceledException();
            var positioned = OutlineGeometry.Position(part.Outline, angle, translation.X, translation.Y);
            if (!OutlineGeometry.FitsSheet(
              positioned,
              _settings.SheetWidth,
              _settings.SheetHeight,
              _settings.FrameMargin))
              continue;
            if (Placements.Any(existing =>
              OutlineGeometry.Collides(positioned, existing.PositionedOutline, _settings.PartGap)))
              continue;

            var nested = _settings.EnableHoleNesting && Placements.Any(existing =>
              OutlineGeometry.IsNestedInsideHole(
                positioned,
                existing.PositionedOutline,
                _settings.PartGap));
            var compactRight = Math.Max(positioned.Bounds.Max.X,
              Placements.Count == 0 ? _settings.FrameMargin : Placements.Max(item => item.PositionedOutline.Bounds.Max.X));
            var compactTop = Math.Max(positioned.Bounds.Max.Y,
              Placements.Count == 0 ? _settings.FrameMargin : Placements.Max(item => item.PositionedOutline.Bounds.Max.Y));
            var score = positioned.Bounds.Min.Y * _settings.SheetWidth + positioned.Bounds.Min.X +
                        compactRight * compactTop * 0.001;
            if (nested)
              score -= _settings.SheetWidth * _settings.SheetHeight;

            if (best == null || score < best.Score - 1e-8)
            {
              best = new PlacementCandidate
              {
                Part = part,
                Angle = angle,
                TranslationX = translation.X,
                TranslationY = translation.Y,
                Outline = positioned,
                NestedInsideHole = nested,
                Score = score
              };
            }
          }
        }
        return best != null;
      }

      public void Place(PlacementCandidate candidate)
      {
        Placements.Add(new PartPlacement
        {
          Part = candidate.Part,
          RotationRadians = candidate.Angle,
          TranslationX = candidate.TranslationX,
          TranslationY = candidate.TranslationY,
          OrientedBounds = candidate.Outline.Bounds,
          PositionedOutline = candidate.Outline,
          NestedInsideHole = candidate.NestedInsideHole
        });
        UsedPartArea += candidate.Part.Outline.NetArea;
      }

      private IEnumerable<Point2d> CandidateTranslations(PositionedOutline rotated)
      {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var holePriorityKeys = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<Point2d>();
        AddCandidate(
          candidates,
          unique,
          _settings.FrameMargin - rotated.Bounds.Min.X,
          _settings.FrameMargin - rotated.Bounds.Min.Y);

        var xTargets = new List<double> { _settings.FrameMargin };
        var yTargets = new List<double> { _settings.FrameMargin };
        foreach (var placement in Placements)
        {
          var bounds = placement.PositionedOutline.Bounds;
          xTargets.Add(bounds.Max.X + _settings.PartGap);
          xTargets.Add(bounds.Min.X - _settings.PartGap - rotated.Bounds.Diagonal.X);
          yTargets.Add(bounds.Max.Y + _settings.PartGap);
          yTargets.Add(bounds.Min.Y - _settings.PartGap - rotated.Bounds.Diagonal.Y);

          AddCandidate(candidates, unique,
            bounds.Max.X + _settings.PartGap - rotated.Bounds.Min.X,
            bounds.Min.Y - rotated.Bounds.Min.Y);
          AddCandidate(candidates, unique,
            bounds.Min.X - rotated.Bounds.Min.X,
            bounds.Max.Y + _settings.PartGap - rotated.Bounds.Min.Y);

          foreach (var hole in _settings.EnableHoleNesting
            ? placement.PositionedOutline.Holes
            : Enumerable.Empty<PolygonLoop2d>())
          {
            if (rotated.Bounds.Diagonal.X + 2.0 * _settings.PartGap > hole.Bounds.Diagonal.X + 1e-8 ||
                rotated.Bounds.Diagonal.Y + 2.0 * _settings.PartGap > hole.Bounds.Diagonal.Y + 1e-8)
              continue;

            var centerX = (hole.Bounds.Min.X + hole.Bounds.Max.X) * 0.5;
            var centerY = (hole.Bounds.Min.Y + hole.Bounds.Max.Y) * 0.5;
            var rotatedCenterX = (rotated.Bounds.Min.X + rotated.Bounds.Max.X) * 0.5;
            var rotatedCenterY = (rotated.Bounds.Min.Y + rotated.Bounds.Max.Y) * 0.5;
            AddPriorityCandidate(
              candidates,
              unique,
              holePriorityKeys,
              centerX - rotatedCenterX,
              centerY - rotatedCenterY);
            AddPriorityCandidate(candidates, unique, holePriorityKeys,
              hole.Bounds.Min.X + _settings.PartGap - rotated.Bounds.Min.X,
              hole.Bounds.Min.Y + _settings.PartGap - rotated.Bounds.Min.Y);
            AddPriorityCandidate(candidates, unique, holePriorityKeys,
              hole.Bounds.Max.X - _settings.PartGap - rotated.Bounds.Max.X,
              hole.Bounds.Min.Y + _settings.PartGap - rotated.Bounds.Min.Y);
            AddPriorityCandidate(candidates, unique, holePriorityKeys,
              hole.Bounds.Min.X + _settings.PartGap - rotated.Bounds.Min.X,
              hole.Bounds.Max.Y - _settings.PartGap - rotated.Bounds.Max.Y);
            AddPriorityCandidate(candidates, unique, holePriorityKeys,
              hole.Bounds.Max.X - _settings.PartGap - rotated.Bounds.Max.X,
              hole.Bounds.Max.Y - _settings.PartGap - rotated.Bounds.Max.Y);

            var holeProbe = OutlineGeometry.InteriorProbe(hole);
            var candidateProbe = OutlineGeometry.InteriorProbe(rotated.Outer);
            AddPriorityCandidate(
              candidates,
              unique,
              holePriorityKeys,
              holeProbe.X - candidateProbe.X,
              holeProbe.Y - candidateProbe.Y);

            foreach (var holePoint in OutlineGeometry.SampleAnchorPoints(hole, 8))
            {
              foreach (var candidatePoint in OutlineGeometry.SampleAnchorPoints(rotated.Outer, 8))
              {
                AddPriorityCandidate(candidates, unique, holePriorityKeys,
                  holePoint.X - candidatePoint.X + _settings.PartGap,
                  holePoint.Y - candidatePoint.Y);
                AddPriorityCandidate(candidates, unique, holePriorityKeys,
                  holePoint.X - candidatePoint.X - _settings.PartGap,
                  holePoint.Y - candidatePoint.Y);
                AddPriorityCandidate(candidates, unique, holePriorityKeys,
                  holePoint.X - candidatePoint.X,
                  holePoint.Y - candidatePoint.Y + _settings.PartGap);
                AddPriorityCandidate(candidates, unique, holePriorityKeys,
                  holePoint.X - candidatePoint.X,
                  holePoint.Y - candidatePoint.Y - _settings.PartGap);
              }
            }
          }

          var placedLoops = _settings.EnableHoleNesting
            ? new[] { placement.PositionedOutline.Outer }.Concat(placement.PositionedOutline.Holes)
            : new[] { placement.PositionedOutline.Outer };
          foreach (var placedLoop in placedLoops)
          {
            foreach (var placedPoint in OutlineGeometry.SampleAnchorPoints(placedLoop, 10))
            {
              foreach (var candidatePoint in OutlineGeometry.SampleAnchorPoints(rotated.Outer, 10))
              {
                AddCandidate(candidates, unique,
                  placedPoint.X - candidatePoint.X + _settings.PartGap,
                  placedPoint.Y - candidatePoint.Y);
                AddCandidate(candidates, unique,
                  placedPoint.X - candidatePoint.X - _settings.PartGap,
                  placedPoint.Y - candidatePoint.Y);
                AddCandidate(candidates, unique,
                  placedPoint.X - candidatePoint.X,
                  placedPoint.Y - candidatePoint.Y + _settings.PartGap);
                AddCandidate(candidates, unique,
                  placedPoint.X - candidatePoint.X,
                  placedPoint.Y - candidatePoint.Y - _settings.PartGap);
              }
            }
          }
        }

        foreach (var targetX in xTargets.Distinct())
        {
          foreach (var targetY in yTargets.Distinct())
            AddCandidate(candidates, unique, targetX - rotated.Bounds.Min.X, targetY - rotated.Bounds.Min.Y);
        }
        return candidates
          .Where(point => TranslationFitsSheetBounds(rotated.Bounds, point))
          .OrderBy(point => holePriorityKeys.Contains(CandidateKey(point)) ? 0 : 1)
          .ThenBy(point => point.Y * _settings.SheetWidth + point.X)
          .Take(MaximumCandidateTranslationsPerAngle)
          .ToArray();
      }

      private bool TranslationFitsSheetBounds(BoundingBox bounds, Point2d translation)
      {
        return bounds.Min.X + translation.X >= _settings.FrameMargin - 1e-8 &&
               bounds.Min.Y + translation.Y >= _settings.FrameMargin - 1e-8 &&
               bounds.Max.X + translation.X <= _settings.SheetWidth - _settings.FrameMargin + 1e-8 &&
               bounds.Max.Y + translation.Y <= _settings.SheetHeight - _settings.FrameMargin + 1e-8;
      }

      private static void AddCandidate(
        ICollection<Point2d> candidates,
        ISet<string> unique,
        double x,
        double y)
      {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
          return;
        var key = CandidateKey(new Point2d(x, y));
        if (unique.Add(key))
          candidates.Add(new Point2d(x, y));
      }

      private static void AddPriorityCandidate(
        ICollection<Point2d> candidates,
        ISet<string> unique,
        ISet<string> priorityKeys,
        double x,
        double y)
      {
        AddCandidate(candidates, unique, x, y);
        if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsInfinity(x) && !double.IsInfinity(y))
          priorityKeys.Add(CandidateKey(new Point2d(x, y)));
      }

      private static string CandidateKey(Point2d point)
      {
        return Math.Round(point.X, 5).ToString("R") + ":" + Math.Round(point.Y, 5).ToString("R");
      }
    }
  }
}
