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
        // 再次运行命令时，配对Group可能把上一次铺平副本也选中；副本不重复排版。
        .Where(item => !string.Equals(
          item.Attributes.GetUserString("WoodSheetLayoutRole"),
          "FlatCopy",
          StringComparison.Ordinal))
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
        ? "WoodSheetLayout 2.1.5 折弯件中性层展开排版"
        : "WoodSheetLayout 2.1.5 选择对象全部铺平排版");
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
        "WSL_PAIR_{0:0.00}mm_S{1:00}_{2}_{3}",
        sheet.ThicknessMillimeters,
        sheet.IndexWithinThickness,
        placement.Part.Name,
        Guid.NewGuid().ToString("N").Substring(0, 6));
      var groupIndex = doc.Groups.Add(groupName);
      var createdIds = new List<Guid>();

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
        attributes.SetUserString("WoodSheetLayoutRole", "FlatCopy");
        attributes.LayerIndex = layers.GetSourceLayer(sheetLayer, item.SourceAttributes);
        attributes.Name = string.IsNullOrWhiteSpace(item.Name) ? placement.Part.Name : item.Name;
        var newId = doc.Objects.Add(geometry, attributes);
        if (newId == Guid.Empty)
          RhinoApp.WriteLine("WoodSheetLayout：对象“{0}”复制输出失败。", attributes.Name);
        else
          createdIds.Add(newId);
      }

      // 原件和铺平副本进入同一个额外配对Group，同时保留原件已有的Group。
      // 移动任意一边都会带动另一边，可直接检查哪些原件已经生成铺平副本。
      if (groupIndex >= 0 && createdIds.Count > 0)
      {
        foreach (var source in placement.Part.Objects.Where(item => item != null))
        {
          var sourceAttributes = source.Attributes.Duplicate();
          var existingGroups = sourceAttributes.GetGroupList() ?? new int[0];
          if (existingGroups.Contains(groupIndex))
            continue;
          sourceAttributes.AddToGroup(groupIndex);
          sourceAttributes.SetUserString("WoodSheetLayoutRole", "Source");
          if (!doc.Objects.ModifyAttributes(source.Id, sourceAttributes, true))
          {
            RhinoApp.WriteLine(
              "WoodSheetLayout：原件“{0}”未能加入配对组。",
              source.Attributes.Name ?? placement.Part.Name);
          }
        }
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
      var sheetWidth = sheet.Width > 0.0 ? sheet.Width : settings.SheetWidth;
      var sheetHeight = sheet.Height > 0.0 ? sheet.Height : settings.SheetHeight;
      var points = new[]
      {
        new Point3d(x, y, 0.0),
        new Point3d(x + sheetWidth, y, 0.0),
        new Point3d(x + sheetWidth, y + sheetHeight, 0.0),
        new Point3d(x, y + sheetHeight, 0.0),
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
        (sheetWidth - 2.0 * settings.FrameMargin) *
        (sheetHeight - 2.0 * settings.FrameMargin));
      var utilization = sheet.UsedPartArea / usableArea * 100.0;
      var labelHeight = 6.0 * settings.ModelUnitsPerMillimeter;
      var labelOffset = 2.0 * settings.ModelUnitsPerMillimeter;
      var labelPlane = new Plane(
        new Point3d(x, y + sheetHeight + labelOffset, 0.0),
        Vector3d.ZAxis);
      var label = new TextEntity
      {
        Plane = labelPlane,
        PlainText = string.Format(
          "{0}｜第{1:00}张｜{2}件｜利用率{3:0.0}%{4}",
          FormatThickness(sheet.ThicknessMillimeters),
          sheet.IndexWithinThickness,
          sheet.Placements.Count,
          utilization,
          sheet.AutoExpanded ? "｜自动加大边界框" : string.Empty),
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
      if (thicknessMillimeters <= 0.001)
        return "未识别厚度";
      var roundedInteger = Math.Round(thicknessMillimeters);
      return Math.Abs(thicknessMillimeters - roundedInteger) <= 0.05
        ? roundedInteger.ToString("0") + "mm"
        : thicknessMillimeters.ToString("0.##") + "mm";
    }

    private static void ReportSummary(LayoutResult result, LayoutSettings settings)
    {
      var partCount = result.Sheets.Sum(sheet => sheet.Placements.Count);
      RhinoApp.WriteLine(string.Format(
        "WoodSheetLayout 2.1.5：完成 {0} 块{1}、{2} 张 {3}；1.1主路径＋失败强制铺平＋原件/副本配对组＋矩形MaxRects；零件间距 {4:0.##} mm，边框出血 {5:0.##} mm。",
        partCount,
        settings.PartMode == LayoutPartMode.BentOnly ? "折弯板" : "平板",
        result.Sheets.Count,
        settings.SheetDescription,
        settings.PartGapMillimeters,
        settings.FrameMarginMillimeters));

      foreach (var sheet in result.Sheets)
      {
        var sheetWidth = sheet.Width > 0.0 ? sheet.Width : settings.SheetWidth;
        var sheetHeight = sheet.Height > 0.0 ? sheet.Height : settings.SheetHeight;
        var usableArea = Math.Max(1e-12,
          (sheetWidth - 2.0 * settings.FrameMargin) *
          (sheetHeight - 2.0 * settings.FrameMargin));
        RhinoApp.WriteLine(string.Format(
          "  {0} 第{1:00}张：{2}件，矩形占位利用率 {3:0.0}%{4}",
          FormatThickness(sheet.ThicknessMillimeters),
          sheet.IndexWithinThickness,
          sheet.Placements.Count,
          sheet.UsedPartArea / usableArea * 100.0,
          sheet.AutoExpanded
            ? "（自动加大边界框）"
            : sheet.Placements.Any(item => item.NestedInsideHole) ? "（包含孔洞嵌套）" : string.Empty));
      }

      foreach (var part in result.Sheets.SelectMany(sheet => sheet.Placements).Select(item => item.Part).Distinct())
      {
        foreach (var note in part.Notes)
          RhinoApp.WriteLine("  {0}：{1}", part.Name, note);
      }

      if (result.Issues.Count > 0)
      {
        RhinoApp.WriteLine(
          "WoodSheetLayout：有 {0} 组对象未生成铺平副本；不创建文字标记。移动WSL_PAIR配对组可核对已铺平零件。",
          result.Issues.Count);
        foreach (var issue in result.Issues)
          RhinoApp.WriteLine("  {0}：{1}", issue.PartName, issue.Message);
      }
      else
      {
        RhinoApp.WriteLine("WoodSheetLayout：全部识别零件均已铺平，并建立原件/副本配对组。 ");
      }

      ReportSkippedParts(result);
    }

    private static void ReportSkippedParts(LayoutResult result)
    {
      if (result.SkippedParts.Count == 0)
        return;
      RhinoApp.WriteLine("WoodSheetLayout：按当前命令模式跳过 {0} 组对象（不生成文字标记）：", result.SkippedParts.Count);
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
          "WoodSheetLayout_2.1.5_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
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
