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
            ThicknessMillimeters = bucket.RepresentativeThickness,
            UsedPartArea = attempt.Sheets[index].UsedPartArea
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
        var attempt = RunAttempt(parts, settings, sort);
        if (best == null || IsBetter(attempt, best))
          best = attempt;
      }
      return best;
    }

    private static PackAttempt RunAttempt(
      IEnumerable<BoardPart> parts,
      LayoutSettings settings,
      SortStrategy sort)
    {
      var ordered = SortParts(parts, sort).ToList();
      var attempt = new PackAttempt();
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
          destination = new ContourSheet(settings);
          attempt.Sheets.Add(destination);
          if (!destination.TryFindBest(part, out selected))
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

      var candidateOccupied = candidate.Sheets.Sum(item => item.OccupiedBoundingArea);
      var currentOccupied = current.Sheets.Sum(item => item.OccupiedBoundingArea);
      if (Math.Abs(candidateOccupied - currentOccupied) > 1e-8)
        return candidateOccupied < currentOccupied;

      var candidateNested = candidate.Sheets.Sum(item => item.Placements.Count(placement => placement.NestedInsideHole));
      var currentNested = current.Sheets.Sum(item => item.Placements.Count(placement => placement.NestedInsideHole));
      return candidateNested > currentNested;
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
        default:
          return parts.OrderByDescending(MaxSide).ThenByDescending(Area);
      }
    }

    private static bool FitsEmptySheet(BoardPart part, LayoutSettings settings)
    {
      if (part == null || part.Outline == null)
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

    private static double MaxSide(BoardPart part)
    {
      if (part.Outline == null || !part.Outline.Bounds.IsValid)
        return 0.0;
      return Math.Max(part.Outline.Bounds.Diagonal.X, part.Outline.Bounds.Diagonal.Y);
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
      Height
    }

    private sealed class ThicknessBucket
    {
      public double RepresentativeThickness { get; set; }
      public List<BoardPart> Parts { get; } = new List<BoardPart>();
    }

    private sealed class PackAttempt
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
      private readonly LayoutSettings _settings;

      public ContourSheet(LayoutSettings settings)
      {
        _settings = settings;
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

          foreach (var translation in CandidateTranslations(rotated))
          {
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

            var nested = Placements.Any(existing =>
              OutlineGeometry.IsNestedInsideHole(positioned, existing.PositionedOutline));
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

          foreach (var hole in placement.PositionedOutline.Holes)
          {
            var centerX = (hole.Bounds.Min.X + hole.Bounds.Max.X) * 0.5;
            var centerY = (hole.Bounds.Min.Y + hole.Bounds.Max.Y) * 0.5;
            var rotatedCenterX = (rotated.Bounds.Min.X + rotated.Bounds.Max.X) * 0.5;
            var rotatedCenterY = (rotated.Bounds.Min.Y + rotated.Bounds.Max.Y) * 0.5;
            AddCandidate(candidates, unique, centerX - rotatedCenterX, centerY - rotatedCenterY);
            AddCandidate(candidates, unique,
              hole.Bounds.Min.X + _settings.PartGap - rotated.Bounds.Min.X,
              hole.Bounds.Min.Y + _settings.PartGap - rotated.Bounds.Min.Y);
          }

          var placedLoops = new[] { placement.PositionedOutline.Outer }
            .Concat(placement.PositionedOutline.Holes);
          foreach (var placedLoop in placedLoops)
          {
            foreach (var placedPoint in OutlineGeometry.SampleAnchorPoints(placedLoop, 18))
            {
              foreach (var candidatePoint in OutlineGeometry.SampleAnchorPoints(rotated.Outer, 18))
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
        return candidates;
      }

      private static void AddCandidate(
        ICollection<Point2d> candidates,
        ISet<string> unique,
        double x,
        double y)
      {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
          return;
        var key = Math.Round(x, 5).ToString("R") + ":" + Math.Round(y, 5).ToString("R");
        if (unique.Add(key))
          candidates.Add(new Point2d(x, y));
      }
    }
  }
}
