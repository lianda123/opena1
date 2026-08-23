using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodExport.Core
{
  internal static class WoodExportEngine
  {
    private const string PartNumberKey = "WoodExport.PartNumber";
    private const string QuantityKey = "WoodExport.Quantity";
    private const string ThicknessKey = "WoodExport.ThicknessMm";
    private const string SourceBoardKey = "WoodExport.SourceBoardId";
    private const string EngravingLayerName = "WoodExport_刻字";

    public static ExportRunResult Analyze(
      RhinoDoc doc,
      IEnumerable<RhinoObject> selection,
      ExportSettings settings)
    {
      var result = new ExportRunResult();
      if (doc == null || selection == null || settings == null)
        return result;

      settings.ModelUnitsPerMillimeter = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      if (!IsFinitePositive(settings.ModelUnitsPerMillimeter))
      {
        settings.ModelUnitsPerMillimeter = 1.0;
        result.Warnings.Add("文档单位无效，暂按毫米计算。建议先把 Rhino 文档单位设为 mm。");
      }

      var objects = selection
        .Where(item => item != null && item.Geometry != null)
        .Where(item => item.Attributes.GetUserString(BoardExportAnalyzer.LabelMarkerKey) !=
                       BoardExportAnalyzer.LabelMarkerValue)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .ToList();
      var components = BoardExportAnalyzer.BuildGroupedComponents(objects);
      var sequence = 0;
      foreach (var component in components)
      {
        ExportPart part;
        string warning;
        sequence++;
        if (BoardExportAnalyzer.TryCreatePart(doc, component, sequence, settings, out part, out warning))
          result.Parts.Add(part);
        else
          result.Warnings.Add("第 " + sequence + " 组已跳过：" + (warning ?? "无法识别木板。"));
      }

      result.BomRows.AddRange(PartNumbering.Assign(result.Parts, settings));
      return result;
    }

    public static bool ApplyNumberingAndLabels(
      RhinoDoc doc,
      ExportRunResult result,
      ExportSettings settings)
    {
      if (doc == null || result == null || result.Parts.Count == 0)
        return false;
      var layerIndex = FindOrCreateEngravingLayer(doc);
      var quantityByNumber = result.BomRows.ToDictionary(item => item.PartNumber, item => item.Quantity);
      var undo = doc.BeginUndoRecord("WoodExport 自动编号与刻字");
      try
      {
        RemoveExistingLabels(doc, result.Parts.Select(item => item.BoardObject.Id));
        foreach (var part in result.Parts)
        {
          var quantity = quantityByNumber[part.PartNumber];
          ApplyMetadata(doc, part, quantity);
          AddSourceLabel(doc, part, settings, layerIndex);
        }
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }
      doc.Views.Redraw();
      return true;
    }

    public static bool WriteBom(ExportRunResult result, string path)
    {
      if (result == null || result.BomRows.Count == 0 || string.IsNullOrWhiteSpace(path))
        return false;
      BomWriter.Write(path, result.BomRows);
      result.BomFile = path;
      return File.Exists(path);
    }

    public static bool ExportCadByThickness(
      RhinoDoc doc,
      ExportRunResult result,
      ExportSettings settings,
      string directory,
      string baseName,
      ExportFormat format)
    {
      if (doc == null || result == null || result.Parts.Count == 0)
        return false;
      Directory.CreateDirectory(directory);
      var allSucceeded = true;
      var buckets = BuildThicknessBuckets(result.Parts, settings.ThicknessToleranceMillimeters);
      foreach (var bucket in buckets)
      {
        var temporaryIds = AddTemporarySheetGeometry(doc, bucket.Parts, settings);
        if (temporaryIds.Count == 0)
        {
          result.Warnings.Add("厚度 " + PartNumbering.FormatThickness(bucket.Thickness) +
                              " mm 没有生成可导出的曲线。");
          allSucceeded = false;
          continue;
        }

        try
        {
          if (format == ExportFormat.Dxf || format == ExportFormat.Both)
            allSucceeded &= ExportSelected(doc, temporaryIds, BuildCadPath(
              directory, baseName, bucket.Thickness, ".dxf"), result);
          if (format == ExportFormat.Dwg || format == ExportFormat.Both)
            allSucceeded &= ExportSelected(doc, temporaryIds, BuildCadPath(
              directory, baseName, bucket.Thickness, ".dwg"), result);
        }
        finally
        {
          foreach (var id in temporaryIds)
            doc.Objects.Delete(id, true);
        }
      }
      doc.Objects.UnselectAll();
      doc.Views.Redraw();
      return allSucceeded;
    }

    public static int ClearLabels(RhinoDoc doc)
    {
      if (doc == null)
        return 0;
      var labels = doc.Objects
        .GetObjectList(ObjectType.Curve)
        .Where(item => item.Attributes.GetUserString(BoardExportAnalyzer.LabelMarkerKey) ==
                       BoardExportAnalyzer.LabelMarkerValue)
        .Select(item => item.Id)
        .ToList();
      foreach (var id in labels)
        doc.Objects.Delete(id, true);
      doc.Views.Redraw();
      return labels.Count;
    }

    private static void ApplyMetadata(RhinoDoc doc, ExportPart part, int quantity)
    {
      foreach (var source in part.SourceObjects)
      {
        var attributes = source.Attributes.Duplicate();
        attributes.SetUserString(PartNumberKey, part.PartNumber);
        attributes.SetUserString(QuantityKey, quantity.ToString(CultureInfo.InvariantCulture));
        attributes.SetUserString(
          ThicknessKey,
          part.ThicknessMillimeters.ToString("0.###", CultureInfo.InvariantCulture));
        doc.Objects.ModifyAttributes(source.Id, attributes, true);
      }
    }

    private static void AddSourceLabel(
      RhinoDoc doc,
      ExportPart part,
      ExportSettings settings,
      int layerIndex)
    {
      var labelHeight = FitLabelHeight(part, settings);
      if (labelHeight <= settings.ModelUnitsPerMillimeter * 0.5)
        return;
      var inset = Math.Min(settings.LabelInset, labelHeight * 0.6);
      var lowerLeft = new Point3d(
        part.FlatBounds.Min.X + inset,
        part.FlatBounds.Max.Y - inset - labelHeight,
        0.0);
      Transform inverse;
      if (!part.FlattenTransform.TryGetInverse(out inverse))
        return;

      var attributes = new ObjectAttributes
      {
        LayerIndex = layerIndex,
        Name = "刻字_" + part.PartNumber,
        ColorSource = ObjectColorSource.ColorFromLayer
      };
      attributes.SetUserString(BoardExportAnalyzer.LabelMarkerKey, BoardExportAnalyzer.LabelMarkerValue);
      attributes.SetUserString(PartNumberKey, part.PartNumber);
      attributes.SetUserString(SourceBoardKey, part.BoardObject.Id.ToString("D"));
      foreach (var groupIndex in part.BoardObject.Attributes.GetGroupList() ?? new int[0])
        attributes.AddToGroup(groupIndex);

      foreach (var curve in StrokeFont.Create(part.PartNumber, labelHeight, lowerLeft))
      {
        if (curve.Transform(inverse))
          doc.Objects.AddCurve(curve, attributes);
      }
    }

    private static double FitLabelHeight(ExportPart part, ExportSettings settings)
    {
      var requested = settings.LabelHeight;
      var availableWidth = Math.Max(0.0, part.FlatBounds.Diagonal.X - 2.0 * settings.LabelInset);
      var availableHeight = Math.Max(0.0, part.FlatBounds.Diagonal.Y - 2.0 * settings.LabelInset);
      var unitWidth = StrokeFont.MeasureWidth(part.PartNumber, 1.0);
      if (unitWidth <= 1e-9)
        return 0.0;
      return Math.Min(requested, Math.Min(availableWidth / unitWidth, availableHeight * 0.35));
    }

    private static void RemoveExistingLabels(RhinoDoc doc, IEnumerable<Guid> sourceBoardIds)
    {
      var sourceSet = new HashSet<string>(
        sourceBoardIds.Select(item => item.ToString("D")),
        StringComparer.OrdinalIgnoreCase);
      var ids = doc.Objects
        .GetObjectList(ObjectType.Curve)
        .Where(item => item.Attributes.GetUserString(BoardExportAnalyzer.LabelMarkerKey) ==
                       BoardExportAnalyzer.LabelMarkerValue)
        .Where(item => sourceSet.Contains(item.Attributes.GetUserString(SourceBoardKey) ?? string.Empty))
        .Select(item => item.Id)
        .ToList();
      foreach (var id in ids)
        doc.Objects.Delete(id, true);
    }

    private static List<Guid> AddTemporarySheetGeometry(
      RhinoDoc doc,
      IList<ExportPart> parts,
      ExportSettings settings)
    {
      var ids = new List<Guid>();
      var placements = Arrange(parts, settings);
      var engravingLayer = FindOrCreateEngravingLayer(doc);
      foreach (var placement in placements)
      {
        foreach (var item in placement.Part.FlatCurves)
        {
          var curve = item.Geometry.DuplicateCurve();
          if (curve == null || !curve.Transform(placement.Transform))
            continue;
          var attributes = item.Attributes.Duplicate();
          attributes.RemoveFromAllGroups();
          attributes.Name = string.IsNullOrWhiteSpace(attributes.Name)
            ? placement.Part.PartNumber
            : attributes.Name;
          attributes.SetUserString(PartNumberKey, placement.Part.PartNumber);
          var id = doc.Objects.AddCurve(curve, attributes);
          if (id != Guid.Empty)
            ids.Add(id);
        }

        var labelHeight = FitLabelHeight(placement.Part, settings);
        var inset = Math.Min(settings.LabelInset, labelHeight * 0.6);
        var labelOrigin = new Point3d(
          placement.Part.FlatBounds.Min.X + inset,
          placement.Part.FlatBounds.Max.Y - inset - labelHeight,
          0.0);
        var labelAttributes = new ObjectAttributes
        {
          LayerIndex = engravingLayer,
          Name = "刻字_" + placement.Part.PartNumber,
          ColorSource = ObjectColorSource.ColorFromLayer
        };
        labelAttributes.SetUserString(PartNumberKey, placement.Part.PartNumber);
        foreach (var labelCurve in StrokeFont.Create(
          placement.Part.PartNumber,
          labelHeight,
          labelOrigin))
        {
          if (!labelCurve.Transform(placement.Transform))
            continue;
          var id = doc.Objects.AddCurve(labelCurve, labelAttributes);
          if (id != Guid.Empty)
            ids.Add(id);
        }
      }
      return ids;
    }

    private static List<Placement> Arrange(IList<ExportPart> parts, ExportSettings settings)
    {
      var result = new List<Placement>();
      var sheetWidth = 420.0 * settings.ModelUnitsPerMillimeter;
      var sheetHeight = 297.0 * settings.ModelUnitsPerMillimeter;
      var sheetGap = 20.0 * settings.ModelUnitsPerMillimeter;
      var margin = settings.Spacing;
      var cursorX = margin;
      var cursorY = margin;
      var rowHeight = 0.0;
      var sheetOriginX = 0.0;

      foreach (var part in parts
        .OrderByDescending(item => item.FlatBounds.Diagonal.X * item.FlatBounds.Diagonal.Y))
      {
        var normal = part.FlatBounds;
        var rotated = OrientedBounds(part.FlatBounds, true);
        var useRotated = false;
        var bounds = normal;
        if (normal.Diagonal.X > sheetWidth - 2.0 * margin &&
            rotated.Diagonal.X <= sheetWidth - 2.0 * margin)
        {
          useRotated = true;
          bounds = rotated;
        }
        else if (cursorX + normal.Diagonal.X + margin > sheetOriginX + sheetWidth &&
                 cursorX + rotated.Diagonal.X + margin <= sheetOriginX + sheetWidth)
        {
          useRotated = true;
          bounds = rotated;
        }

        if (cursorX + bounds.Diagonal.X + margin > sheetOriginX + sheetWidth)
        {
          cursorX = sheetOriginX + margin;
          cursorY += rowHeight + settings.Spacing;
          rowHeight = 0.0;
        }
        if (cursorY + bounds.Diagonal.Y + margin > sheetHeight)
        {
          sheetOriginX += sheetWidth + sheetGap;
          cursorX = sheetOriginX + margin;
          cursorY = margin;
          rowHeight = 0.0;
        }

        var rotation = useRotated
          ? Transform.Rotation(Math.PI * 0.5, Vector3d.ZAxis, Point3d.Origin)
          : Transform.Identity;
        var translation = Transform.Translation(
          cursorX - bounds.Min.X,
          cursorY - bounds.Min.Y,
          -bounds.Min.Z);
        result.Add(new Placement
        {
          Part = part,
          Transform = translation * rotation
        });
        cursorX += bounds.Diagonal.X + settings.Spacing;
        rowHeight = Math.Max(rowHeight, bounds.Diagonal.Y);
      }
      return result;
    }

    private static BoundingBox OrientedBounds(BoundingBox source, bool rotated)
    {
      if (!rotated)
        return source;
      var transform = Transform.Rotation(Math.PI * 0.5, Vector3d.ZAxis, Point3d.Origin);
      var result = BoundingBox.Unset;
      foreach (var sourceCorner in source.GetCorners())
      {
        var corner = sourceCorner;
        corner.Transform(transform);
        result = result.IsValid
          ? BoundingBox.Union(result, corner)
          : new BoundingBox(corner, corner);
      }
      return result;
    }

    private static bool ExportSelected(
      RhinoDoc doc,
      IEnumerable<Guid> ids,
      string path,
      ExportRunResult result)
    {
      doc.Objects.UnselectAll();
      foreach (var id in ids)
        doc.Objects.Select(id);
      doc.Views.Redraw();
      var success = doc.ExportSelected(path);
      if (success && File.Exists(path))
        result.CadFiles.Add(path);
      else
        result.Warnings.Add("CAD 导出失败：" + path);
      return success && File.Exists(path);
    }

    private static string BuildCadPath(
      string directory,
      string baseName,
      double thickness,
      string extension)
    {
      var thicknessText = PartNumbering.FormatThickness(thickness);
      return Path.Combine(directory, baseName + "_" + thicknessText + "mm" + extension);
    }

    private static List<ThicknessBucket> BuildThicknessBuckets(
      IEnumerable<ExportPart> parts,
      double tolerance)
    {
      var buckets = new List<ThicknessBucket>();
      foreach (var part in parts.OrderBy(item => item.ThicknessMillimeters))
      {
        var bucket = buckets.FirstOrDefault(item =>
          Math.Abs(item.Thickness - part.ThicknessMillimeters) <= tolerance);
        if (bucket == null)
        {
          bucket = new ThicknessBucket { Thickness = part.ThicknessMillimeters };
          buckets.Add(bucket);
        }
        bucket.Parts.Add(part);
        bucket.Thickness = bucket.Parts.Average(item => item.ThicknessMillimeters);
      }
      return buckets;
    }

    private static int FindOrCreateEngravingLayer(RhinoDoc doc)
    {
      foreach (var layer in doc.Layers)
      {
        if (string.Equals(layer.Name, EngravingLayerName, StringComparison.OrdinalIgnoreCase))
          return layer.Index;
      }
      var color = Color.FromArgb(240, 190, 35);
      return doc.Layers.Add(new Layer
      {
        Name = EngravingLayerName,
        Color = color,
        PlotColor = color
      });
    }

    private static bool IsFinitePositive(double value)
    {
      return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private sealed class Placement
    {
      public ExportPart Part { get; set; }
      public Transform Transform { get; set; }
    }

    private sealed class ThicknessBucket
    {
      public double Thickness { get; set; }
      public List<ExportPart> Parts { get; } = new List<ExportPart>();
    }
  }
}
