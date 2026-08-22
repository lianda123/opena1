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
        var thicknessSheetIndex = 0;
        ShelfState shelf = null;
        PackedSheet sheet = null;
        foreach (var part in bucket.Parts
          .OrderByDescending(item => Math.Max(item.FlatBounds.Diagonal.X, item.FlatBounds.Diagonal.Y))
          .ThenByDescending(item => item.FlatBounds.Diagonal.X * item.FlatBounds.Diagonal.Y))
        {
          PartPlacement placement;
          if (sheet == null || !shelf.TryPlace(part, out placement))
          {
            shelf = new ShelfState(settings.SheetWidth, settings.SheetHeight, settings.Spacing);
            if (!shelf.TryPlace(part, out placement))
            {
              result.OversizedParts.Add(part);
              continue;
            }

            sheet = new PackedSheet
            {
              GlobalIndex = ++globalSheetIndex,
              IndexWithinThickness = ++thicknessSheetIndex,
              ThicknessMillimeters = bucket.RepresentativeThickness
            };
            result.Sheets.Add(sheet);
          }
          sheet.Placements.Add(placement);
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

    private sealed class ThicknessBucket
    {
      public double RepresentativeThickness { get; set; }
      public List<BoardPart> Parts { get; } = new List<BoardPart>();
    }

    private sealed class ShelfState
    {
      private readonly double _sheetWidth;
      private readonly double _sheetHeight;
      private readonly double _gap;
      private double _cursorX;
      private double _cursorY;
      private double _rowHeight;

      public ShelfState(double sheetWidth, double sheetHeight, double gap)
      {
        _sheetWidth = sheetWidth;
        _sheetHeight = sheetHeight;
        _gap = gap;
        _cursorX = gap;
        _cursorY = gap;
      }

      public bool TryPlace(BoardPart part, out PartPlacement placement)
      {
        placement = ChoosePlacement(part, _cursorX, _cursorY, _rowHeight);
        if (placement != null)
        {
          Accept(placement);
          return true;
        }

        if (_cursorX <= _gap + 1e-9)
          return false;
        var nextRowY = _cursorY + _rowHeight + _gap;
        placement = ChoosePlacement(part, _gap, nextRowY, 0.0);
        if (placement == null)
          return false;
        _cursorX = _gap;
        _cursorY = nextRowY;
        _rowHeight = 0.0;
        Accept(placement);
        return true;
      }

      private PartPlacement ChoosePlacement(BoardPart part, double x, double y, double currentRowHeight)
      {
        var candidates = new List<PartPlacement>();
        foreach (var rotated in new[] { false, true })
        {
          var bounds = OrientedBounds(part.FlatBounds, rotated);
          var width = bounds.Max.X - bounds.Min.X;
          var height = bounds.Max.Y - bounds.Min.Y;
          if (x + width > _sheetWidth - _gap + 1e-9 ||
              y + height > _sheetHeight - _gap + 1e-9)
            continue;
          candidates.Add(new PartPlacement
          {
            Part = part,
            RotatedNinetyDegrees = rotated,
            LocalX = x,
            LocalY = y,
            OrientedBounds = bounds
          });
        }

        return candidates
          .OrderBy(item => Math.Max(currentRowHeight, Height(item)))
          .ThenBy(item => _sheetWidth - item.LocalX - Width(item))
          .FirstOrDefault();
      }

      private void Accept(PartPlacement placement)
      {
        _cursorX = placement.LocalX + Width(placement) + _gap;
        _rowHeight = Math.Max(_rowHeight, Height(placement));
      }

      private static double Width(PartPlacement placement)
      {
        return placement.OrientedBounds.Max.X - placement.OrientedBounds.Min.X;
      }

      private static double Height(PartPlacement placement)
      {
        return placement.OrientedBounds.Max.Y - placement.OrientedBounds.Min.Y;
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
          result = result.IsValid ? BoundingBox.Union(result, new BoundingBox(point, point)) : new BoundingBox(point, point);
        }
        return result;
      }
    }
  }
}
