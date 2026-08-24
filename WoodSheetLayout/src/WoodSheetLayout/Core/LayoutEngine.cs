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

      var progress = new LayoutProgress();
      progress.Start();
      try
      {

      settings.ModelUnitsPerMillimeter = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      if (!IsFinitePositive(settings.ModelUnitsPerMillimeter))
      {
        settings.ModelUnitsPerMillimeter = 1.0;
        RhinoApp.WriteLine("WoodSheetLayout：文档没有有效单位，将按毫米处理板框、间距与中性层厚度。");
      }

      var components = BoardAnalyzer.BuildGroupedComponents(objects);
      var parts = new List<BoardPart>();
      var analysisIssues = new List<LayoutIssue>();
      var skippedParts = new List<string>();
      var sequence = 0;
      foreach (var component in components)
      {
        BoardPart part;
        string warning;
        bool skippedByMode;
        sequence++;
        if (BoardAnalyzer.TryCreatePart(
          doc,
          component,
          sequence,
          settings,
          out part,
          out warning,
          out skippedByMode))
        {
          parts.Add(part);
        }
        else if (skippedByMode)
        {
          skippedParts.Add(string.Format(
            "{0}：{1}",
            ComponentName(component, sequence),
            warning ?? "因当前命令模式而跳过。"));
        }
        else
        {
          analysisIssues.Add(new LayoutIssue
          {
            PartSequence = sequence,
            PartName = ComponentName(component, sequence),
            Message = warning ?? "无法识别或铺平木板。",
            Severity = IssueSeverity.Warning,
            SourceBounds = BoardAnalyzer.CombinedBounds(component)
          });
        }
        if (!progress.ReportAnalysis(sequence, components.Count))
          throw new OperationCanceledException();
      }

      var selectionBounds = BoardAnalyzer.CombinedBounds(objects);
      var origin = selectionBounds.IsValid
        ? new Point2d(selectionBounds.Max.X + settings.SheetGap, selectionBounds.Min.Y)
        : Point2d.Origin;
      var result = parts.Count == 0
        ? new LayoutResult()
        : SheetPacker.Pack(parts, settings, origin, progress);
      result.SkippedParts.AddRange(skippedParts);
      result.Issues.AddRange(analysisIssues);
      foreach (var oversized in result.OversizedParts)
      {
        result.Issues.Add(new LayoutIssue
        {
          PartSequence = oversized.Sequence,
          PartName = oversized.Name,
          Message = "零件超过当前边界框可用范围，未排入。",
          Severity = IssueSeverity.Warning,
          SourceBounds = oversized.SourceBounds
        });
      }
      for (var index = 0; index < result.Issues.Count; index++)
        result.Issues[index].Number = index + 1;

      if (result.Sheets.Count == 0 && result.Issues.Count == 0)
      {
        if (result.SkippedParts.Count > 0)
        {
          RhinoApp.WriteLine(
            settings.PartMode == LayoutPartMode.PlanarOnly
              ? "WoodSheetLayout：本次选择只有折弯件；普通排版未处理，请运行 WSLayFlatBend。"
              : "WSLayFlatBend：本次选择没有可展开的折弯件；普通平板已跳过。");
          ReportSkippedParts(result);
          return true;
        }
        RhinoApp.WriteLine("WoodSheetLayout：没有识别到可铺平的板件实体。");
        return false;
      }

      if (progress.IsCancelled)
        throw new OperationCanceledException();

      var undo = doc.BeginUndoRecord(settings.PartMode == LayoutPartMode.BentOnly
        ? "WoodSheetLayout 2.1.1 折弯件中性层展开排版"
        : "WoodSheetLayout 2.1.1 经典MaxRects规整排版");
      try
      {
        var layers = new OutputLayerManager(doc);
        var outputIndex = 0;
        foreach (var sheet in result.Sheets)
        {
          var color = BoundaryColors[(sheet.GlobalIndex - 1) % BoundaryColors.Length];
          var sheetLayer = layers.CreateSheetLayer(sheet, color);
          var boundaryLayer = layers.CreateChildLayer(sheetLayer, "边界框与统计", color, color);
          AddBoundary(doc, sheet, settings, boundaryLayer);
          foreach (var placement in sheet.Placements)
            AddPlacedPart(doc, sheet, placement, layers, sheetLayer);
          progress.ReportOutput(++outputIndex, result.Sheets.Count);
        }
        AddIssueMarkers(doc, result.Issues, layers);
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }

      doc.Views.Redraw();
      ReportSummary(result, settings);
      return result.Sheets.Count > 0 || result.SkippedParts.Count > 0;
      }
      catch (OperationCanceledException)
      {
        RhinoApp.WriteLine("WoodSheetLayout：用户已取消，原模型未修改。");
        return false;
      }
      finally
      {
        progress.Dispose();
      }
    }

    private static void AddPlacedPart(
      RhinoDoc doc,
      PackedSheet sheet,
      PartPlacement placement,
      OutputLayerManager layers,
      int sheetLayer)
    {
      var rotation = Transform.Rotation(placement.RotationRadians, Vector3d.ZAxis, Point3d.Origin);
      var localTranslation = Transform.Translation(placement.TranslationX, placement.TranslationY, 0.0);
      var sheetTranslation = Transform.Translation(sheet.Origin.X, sheet.Origin.Y, 0.0);
      var finalTransform = sheetTranslation * localTranslation * rotation;

      var groupName = string.Format(
        "WSL2_{0:0.00}mm_S{1:00}_{2}_{3}",
        sheet.ThicknessMillimeters,
        sheet.IndexWithinThickness,
        placement.Part.Name,
        Guid.NewGuid().ToString("N").Substring(0, 6));
      var groupIndex = doc.Groups.Add(groupName);

      foreach (var item in placement.Part.FlatGeometry)
      {
        if (item.Geometry == null)
          continue;
        var geometry = item.Geometry.Duplicate();
        if (geometry == null || !geometry.Transform(finalTransform))
        {
          RhinoApp.WriteLine("WoodSheetLayout：对象“{0}”输出变换失败。", item.Name ?? placement.Part.Name);
          continue;
        }

        var attributes = item.SourceAttributes == null
          ? new ObjectAttributes()
          : item.SourceAttributes.Duplicate();
        attributes.RemoveFromAllGroups();
        if (groupIndex >= 0)
          attributes.AddToGroup(groupIndex);
        attributes.LayerIndex = layers.GetSourceLayer(sheetLayer, item.SourceAttributes);
        attributes.Name = string.IsNullOrWhiteSpace(item.Name) ? placement.Part.Name : item.Name;
        var newId = doc.Objects.Add(geometry, attributes);
        if (newId == Guid.Empty)
          RhinoApp.WriteLine("WoodSheetLayout：对象“{0}”复制输出失败。", attributes.Name);
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
          settings.SheetDescription,
          sheet.ThicknessMillimeters,
          sheet.IndexWithinThickness)
      };
      doc.Objects.AddCurve(new PolylineCurve(points), attributes);

      var usableArea = Math.Max(1e-12,
        (settings.SheetWidth - 2.0 * settings.FrameMargin) *
        (settings.SheetHeight - 2.0 * settings.FrameMargin));
      var utilization = sheet.UsedPartArea / usableArea * 100.0;
      var labelHeight = 6.0 * settings.ModelUnitsPerMillimeter;
      var labelOffset = 2.0 * settings.ModelUnitsPerMillimeter;
      var labelPlane = new Plane(
        new Point3d(x, y + settings.SheetHeight + labelOffset, 0.0),
        Vector3d.ZAxis);
      var label = new TextEntity
      {
        Plane = labelPlane,
        PlainText = string.Format(
          "{0}｜第{1:00}张｜{2}件｜利用率{3:0.0}%",
          FormatThickness(sheet.ThicknessMillimeters),
          sheet.IndexWithinThickness,
          sheet.Placements.Count,
          utilization),
        TextHeight = labelHeight,
        Justification = TextJustification.BottomLeft
      };
      var labelAttributes = new ObjectAttributes
      {
        LayerIndex = layerIndex,
        Name = "板材统计_" + FormatThickness(sheet.ThicknessMillimeters)
      };
      doc.Objects.AddText(label, labelAttributes);
    }

    private static void AddIssueMarkers(
      RhinoDoc doc,
      IEnumerable<LayoutIssue> issues,
      OutputLayerManager layers)
    {
      var issueList = issues.ToList();
      if (issueList.Count == 0)
        return;
      var layer = layers.CreateIssueLayer();
      foreach (var issue in issueList)
      {
        var point = issue.SourceBounds.IsValid ? issue.SourceBounds.Center : Point3d.Origin;
        var text = string.Format("WSL-{0:000} {1}：{2}", issue.Number, issue.PartName, issue.Message);
        var attributes = new ObjectAttributes
        {
          LayerIndex = layer,
          Name = "WSL问题_" + issue.Number.ToString("000"),
          ObjectColor = Color.Gold,
          ColorSource = ObjectColorSource.ColorFromObject
        };
        doc.Objects.AddTextDot(new TextDot(text, point), attributes);
      }
    }

    private static string FormatThickness(double thicknessMillimeters)
    {
      var roundedInteger = Math.Round(thicknessMillimeters);
      return Math.Abs(thicknessMillimeters - roundedInteger) <= 0.05
        ? roundedInteger.ToString("0") + "mm"
        : thicknessMillimeters.ToString("0.##") + "mm";
    }

    private static void ReportSummary(LayoutResult result, LayoutSettings settings)
    {
      var partCount = result.Sheets.Sum(sheet => sheet.Placements.Count);
      RhinoApp.WriteLine(string.Format(
        "WoodSheetLayout 2.1.1：完成 {0} 块{1}、{2} 张 {3}；矩形包围盒MaxRects；零件间距 {4:0.##} mm，边框出血 {5:0.##} mm。",
        partCount,
        settings.PartMode == LayoutPartMode.BentOnly ? "折弯板" : "平板",
        result.Sheets.Count,
        settings.SheetDescription,
        settings.PartGapMillimeters,
        settings.FrameMarginMillimeters));

      foreach (var sheet in result.Sheets)
      {
        var usableArea = Math.Max(1e-12,
          (settings.SheetWidth - 2.0 * settings.FrameMargin) *
          (settings.SheetHeight - 2.0 * settings.FrameMargin));
        RhinoApp.WriteLine(string.Format(
          "  {0} 第{1:00}张：{2}件，矩形占位利用率 {3:0.0}%{4}",
          FormatThickness(sheet.ThicknessMillimeters),
          sheet.IndexWithinThickness,
          sheet.Placements.Count,
          sheet.UsedPartArea / usableArea * 100.0,
          sheet.Placements.Any(item => item.NestedInsideHole) ? "（包含孔洞嵌套）" : string.Empty));
      }

      foreach (var part in result.Sheets.SelectMany(sheet => sheet.Placements).Select(item => item.Part).Distinct())
      {
        foreach (var note in part.Notes)
          RhinoApp.WriteLine("  {0}：{1}", part.Name, note);
      }

      if (result.Issues.Count > 0)
      {
        RhinoApp.WriteLine("WoodSheetLayout：{0} 个问题/未排入对象已用黄色 WSL 编号标记：", result.Issues.Count);
        foreach (var issue in result.Issues)
          RhinoApp.WriteLine("  WSL-{0:000} {1}：{2}", issue.Number, issue.PartName, issue.Message);
      }
      else
      {
        RhinoApp.WriteLine("WoodSheetLayout：未排入零件 0 个。原模型未移动、未删除、未改图层。 ");
      }

      ReportSkippedParts(result);
    }

    private static void ReportSkippedParts(LayoutResult result)
    {
      if (result.SkippedParts.Count == 0)
        return;
      RhinoApp.WriteLine("WoodSheetLayout：按当前命令模式跳过 {0} 组对象（未生成黄色问题标记）：", result.SkippedParts.Count);
      foreach (var message in result.SkippedParts)
        RhinoApp.WriteLine("  " + message);
    }

    private static string ComponentName(IEnumerable<RhinoObject> component, int sequence)
    {
      return component
        .Select(item => item.Attributes.Name)
        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? "木板_" + sequence.ToString("000");
    }

    private static bool IsFinitePositive(double value)
    {
      return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private sealed class OutputLayerManager
    {
      private readonly RhinoDoc _doc;
      private readonly int _rootLayer;
      private readonly Dictionary<string, int> _sourceLayerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

      public OutputLayerManager(RhinoDoc doc)
      {
        _doc = doc;
        _rootLayer = CreateLayer(
          "WoodSheetLayout_2.1.1_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
          Color.White,
          Color.White,
          Guid.Empty,
          -1);
      }

      public int CreateSheetLayer(PackedSheet sheet, Color color)
      {
        return CreateLayer(
          string.Format("{0:0.00}mm_第{1:00}张", sheet.ThicknessMillimeters, sheet.IndexWithinThickness),
          color,
          color,
          _doc.Layers[_rootLayer].Id,
          -1);
      }

      public int CreateChildLayer(int parent, string name, Color color, Color plotColor)
      {
        return CreateLayer(name, color, plotColor, _doc.Layers[parent].Id, -1);
      }

      public int GetSourceLayer(int sheetLayer, ObjectAttributes sourceAttributes)
      {
        var sourceLayer = sourceAttributes == null || sourceAttributes.LayerIndex < 0 ||
                          sourceAttributes.LayerIndex >= _doc.Layers.Count
          ? null
          : _doc.Layers[sourceAttributes.LayerIndex];
        var sourceKey = sourceLayer == null ? "默认图层" : sourceLayer.FullPath;
        var key = sheetLayer.ToString() + "|" + sourceKey;
        int layerIndex;
        if (_sourceLayerMap.TryGetValue(key, out layerIndex))
          return layerIndex;

        var name = "原图层_" + SanitizeLayerName(sourceKey);
        var color = sourceLayer == null ? Color.White : sourceLayer.Color;
        var plotColor = sourceLayer == null ? color : sourceLayer.PlotColor;
        var lineType = sourceLayer == null ? -1 : sourceLayer.LinetypeIndex;
        layerIndex = CreateLayer(name, color, plotColor, _doc.Layers[sheetLayer].Id, lineType);
        _sourceLayerMap[key] = layerIndex;
        return layerIndex;
      }

      public int CreateIssueLayer()
      {
        return CreateLayer("问题标记_黄色", Color.Gold, Color.Gold, _doc.Layers[_rootLayer].Id, -1);
      }

      private int CreateLayer(
        string name,
        Color color,
        Color plotColor,
        Guid parentId,
        int lineTypeIndex)
      {
        var layer = new Layer
        {
          Name = name,
          Color = color,
          PlotColor = plotColor,
          ParentLayerId = parentId
        };
        if (lineTypeIndex >= 0)
          layer.LinetypeIndex = lineTypeIndex;
        return _doc.Layers.Add(layer);
      }

      private static string SanitizeLayerName(string value)
      {
        var result = (value ?? "默认图层").Replace("::", "_").Replace("/", "_").Replace("\\", "_");
        return result.Length <= 80 ? result : result.Substring(result.Length - 80);
      }
    }
  }
}
