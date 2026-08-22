using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodSheetLayout.Core
{
  internal static class LayoutEngine
  {
    private static readonly Color[] BoundaryColors =
    {
      Color.FromArgb(232, 75, 75),
      Color.FromArgb(58, 145, 220),
      Color.FromArgb(65, 170, 105),
      Color.FromArgb(230, 152, 45),
      Color.FromArgb(152, 99, 210),
      Color.FromArgb(38, 168, 168)
    };

    public static bool Execute(
      RhinoDoc doc,
      IEnumerable<RhinoObject> selection,
      LayoutSettings settings)
    {
      if (doc == null || selection == null || settings == null)
        return false;

      var objects = selection
        .Where(item => item != null && item.Geometry != null)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .ToList();
      if (objects.Count == 0)
        return false;

      settings.ModelUnitsPerMillimeter = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      if (!IsFinitePositive(settings.ModelUnitsPerMillimeter))
      {
        settings.ModelUnitsPerMillimeter = 1.0;
        RhinoApp.WriteLine("WoodSheetLayout：文档没有有效单位，将按毫米处理 A3/A4 和 4 mm 间距。");
      }

      var components = BoardAnalyzer.BuildGroupedComponents(objects);
      var parts = new List<BoardPart>();
      var sequence = 0;
      foreach (var component in components)
      {
        BoardPart part;
        string warning;
        if (BoardAnalyzer.TryCreatePart(
          doc,
          component,
          ++sequence,
          settings.ModelUnitsPerMillimeter,
          out part,
          out warning))
        {
          parts.Add(part);
        }
        else
        {
          RhinoApp.WriteLine(
            "WoodSheetLayout：第 {0} 组已跳过：{1}",
            sequence,
            warning ?? "无法识别木板。");
        }
      }

      if (parts.Count == 0)
      {
        RhinoApp.WriteLine("WoodSheetLayout：没有识别到可铺平的板件实体。");
        return false;
      }

      var selectionBounds = CombinedBounds(objects);
      var origin = selectionBounds.IsValid
        ? new Point2d(selectionBounds.Max.X + settings.SheetGap, selectionBounds.Min.Y)
        : Point2d.Origin;
      var result = SheetPacker.Pack(parts, settings, origin);
      if (result.Sheets.Count == 0)
      {
        ReportOversized(result, settings);
        return false;
      }

      var undo = doc.BeginUndoRecord("WoodSheetLayout 一键铺平排版");
      try
      {
        var boundaryLayerByThickness = new Dictionary<string, int>();
        foreach (var sheet in result.Sheets)
        {
          var thicknessKey = sheet.ThicknessMillimeters.ToString("0.00");
          int boundaryLayer;
          if (!boundaryLayerByThickness.TryGetValue(thicknessKey, out boundaryLayer))
          {
            boundaryLayer = FindOrCreateBoundaryLayer(
              doc,
              sheet.ThicknessMillimeters,
              BoundaryColors[boundaryLayerByThickness.Count % BoundaryColors.Length]);
            boundaryLayerByThickness[thicknessKey] = boundaryLayer;
          }

          AddBoundary(doc, sheet, settings, boundaryLayer);
          foreach (var placement in sheet.Placements)
            AddPlacedPart(doc, sheet, placement, settings);
        }
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }

      doc.Views.Redraw();
      ReportSummary(result, settings);
      return true;
    }

    private static void AddPlacedPart(
      RhinoDoc doc,
      PackedSheet sheet,
      PartPlacement placement,
      LayoutSettings settings)
    {
      var rotation = placement.RotatedNinetyDegrees
        ? Transform.Rotation(Math.PI * 0.5, Vector3d.ZAxis, Point3d.Origin)
        : Transform.Identity;
      var targetX = sheet.Origin.X + placement.LocalX;
      var targetY = sheet.Origin.Y + placement.LocalY;
      var translation = Transform.Translation(
        targetX - placement.OrientedBounds.Min.X,
        targetY - placement.OrientedBounds.Min.Y,
        -placement.OrientedBounds.Min.Z);
      var finalTransform = translation * rotation * placement.Part.FlattenTransform;

      var groupName = string.Format(
        "WSL_{0:0.00}mm_S{1:00}_{2}_{3}",
        sheet.ThicknessMillimeters,
        sheet.IndexWithinThickness,
        placement.Part.Name,
        Guid.NewGuid().ToString("N").Substring(0, 6));
      var groupIndex = doc.Groups.Add(groupName);

      foreach (var source in placement.Part.Objects)
      {
        var newId = doc.Objects.Transform(source.Id, finalTransform, false);
        if (newId == Guid.Empty)
        {
          RhinoApp.WriteLine("WoodSheetLayout：对象“{0}”复制铺平失败。", source.Attributes.Name);
          continue;
        }

        var duplicate = doc.Objects.FindId(newId);
        if (duplicate == null)
          continue;
        var attributes = duplicate.Attributes.Duplicate();
        attributes.RemoveFromAllGroups();
        if (groupIndex >= 0)
          attributes.AddToGroup(groupIndex);
        attributes.Name = string.IsNullOrWhiteSpace(source.Attributes.Name)
          ? placement.Part.Name
          : source.Attributes.Name;
        doc.Objects.ModifyAttributes(newId, attributes, true);
      }
    }

    private static void AddBoundary(
      RhinoDoc doc,
      PackedSheet sheet,
      LayoutSettings settings,
      int layerIndex)
    {
      var x = sheet.Origin.X;
      var y = sheet.Origin.Y;
      var points = new[]
      {
        new Point3d(x, y, 0.0),
        new Point3d(x + settings.SheetWidth, y, 0.0),
        new Point3d(x + settings.SheetWidth, y + settings.SheetHeight, 0.0),
        new Point3d(x, y + settings.SheetHeight, 0.0),
        new Point3d(x, y, 0.0)
      };
      var attributes = new ObjectAttributes
      {
        LayerIndex = layerIndex,
        Name = string.Format(
          "{0}_{1:0.00}mm_第{2:00}张",
          settings.Sheet,
          sheet.ThicknessMillimeters,
          sheet.IndexWithinThickness)
      };
      doc.Objects.AddCurve(new PolylineCurve(points), attributes);
    }

    private static int FindOrCreateBoundaryLayer(RhinoDoc doc, double thicknessMillimeters, Color color)
    {
      var name = "WSL_边界框_" + thicknessMillimeters.ToString("0.00") + "mm";
      foreach (var layer in doc.Layers)
      {
        if (string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase))
          return layer.Index;
      }

      return doc.Layers.Add(new Layer
      {
        Name = name,
        Color = color,
        PlotColor = color
      });
    }

    private static BoundingBox CombinedBounds(IEnumerable<RhinoObject> objects)
    {
      var result = BoundingBox.Unset;
      foreach (var rhinoObject in objects)
      {
        var bounds = rhinoObject.Geometry.GetBoundingBox(true);
        if (!bounds.IsValid)
          continue;
        result = result.IsValid ? BoundingBox.Union(result, bounds) : bounds;
      }
      return result;
    }

    private static void ReportSummary(LayoutResult result, LayoutSettings settings)
    {
      var partCount = result.Sheets.Sum(sheet => sheet.Placements.Count);
      RhinoApp.WriteLine(string.Format(
        "WoodSheetLayout：完成 {0} 块板件、{1} 张 {2} 边界框；板间距与边界留量均为 {3:0.##} mm。",
        partCount,
        result.Sheets.Count,
        settings.Sheet,
        settings.SpacingMillimeters));

      foreach (var group in result.Sheets.GroupBy(sheet => sheet.ThicknessMillimeters))
      {
        RhinoApp.WriteLine(
          "  厚度 {0:0.00} mm：{1} 块，{2} 张。",
          group.Key,
          group.Sum(sheet => sheet.Placements.Count),
          group.Count());
      }
      ReportOversized(result, settings);
    }

    private static void ReportOversized(LayoutResult result, LayoutSettings settings)
    {
      foreach (var part in result.OversizedParts)
      {
        RhinoApp.WriteLine(
          "WoodSheetLayout：板件“{0}”超过 {1} 可用范围，未排入边界框。",
          part.Name,
          settings.Sheet);
      }
    }

    private static bool IsFinitePositive(double value)
    {
      return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }
  }
}
