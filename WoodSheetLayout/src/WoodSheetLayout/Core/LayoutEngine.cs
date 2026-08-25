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

      var selectedObjects = selection
        .Where(item => item != null && item.Geometry != null)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .ToList();
      // 普通排版严格恢复 1.1.0 的选择骨架：GetObject(GroupSelect=true)
      // 返回哪些对象，就按这些对象现有的 Rhino Group 组装零件，不再递归追踪
      // 交叉组。递归追踪会把本来独立的板件串成大组件，也是 2.1.6 与
      // 1.1.0 行为不一致的来源之一。折弯命令仍保留组成员补齐。
      var objects = settings.PartMode == LayoutPartMode.BentOnly
        ? BoardAnalyzer.ExpandSelectedGroups(doc, selectedObjects)
        : selectedObjects
          .Where(item => !BoardAnalyzer.IsGeneratedOutputObject(doc, item))
          .ToList();
      if (objects.Count == 0)
        return false;

      if (settings.PartMode == LayoutPartMode.BentOnly && objects.Count != selectedObjects.Count)
      {
        RhinoApp.WriteLine(
          "WSLayFlatBend：选中 {0} 个对象，补齐原始组后按 {1} 个对象分析；旧铺平副本和旧输出层已排除。",
          selectedObjects.Count,
          objects.Count);
      }

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

      var outputPartCount = 0;
      var outputObjectCount = 0;
      var outputFailureCount = 0;
      var undo = doc.BeginUndoRecord(settings.PartMode == LayoutPartMode.BentOnly
        ? "WoodSheetLayout 2.2.5 折弯件中性层展开排版"
        : "WoodSheetLayout 2.2.5（1.1原生通道）铺平排版");
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
          {
            int createdObjects;
            int failedObjects;
            if (AddPlacedPart(
              doc,
              sheet,
              placement,
              layers,
              sheetLayer,
              out createdObjects,
              out failedObjects))
            {
              outputPartCount++;
            }
            outputObjectCount += createdObjects;
            outputFailureCount += failedObjects;
          }
          progress.ReportOutput(++outputIndex, result.Sheets.Count);
        }
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }

      doc.Views.Redraw();
      ReportSummary(
        result,
        settings,
        components.Count,
        parts.Count,
        outputPartCount,
        outputObjectCount,
        outputFailureCount);
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

    private static bool AddPlacedPart(
      RhinoDoc doc,
      PackedSheet sheet,
      PartPlacement placement,
      OutputLayerManager layers,
      int sheetLayer,
      out int createdCount,
      out int failedCount)
    {
      // 普通平板必须走 1.1.0 的原生文档复制通道。这里把铺平、0/90°旋转
      // 与排版位移合并为一次 Transform，并由 Rhino 返回真实的新对象 GUID。
      // 折弯件没有单一刚体铺平变换，仍使用已展开的中性层几何输出。
      if (placement.Part.FlattenKind == FlattenKind.Planar)
      {
        return AddClassicPlanarPart(
          doc,
          sheet,
          placement,
          layers,
          sheetLayer,
          out createdCount,
          out failedCount);
      }

      createdCount = 0;
      failedCount = 0;
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
        {
          failedCount++;
          continue;
        }
        GeometryBase geometry;
        if (!TryCreatePlacedGeometry(doc, placement.Part, item, finalTransform, out geometry))
        {
          RhinoApp.WriteLine("WoodSheetLayout：对象“{0}”输出变换失败。", item.Name ?? placement.Part.Name);
          failedCount++;
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
        var newId = AddGeometryWithFallback(doc, geometry, attributes);
        if (newId == Guid.Empty)
        {
          RhinoApp.WriteLine("WoodSheetLayout：对象“{0}”复制输出失败。", attributes.Name);
          failedCount++;
        }
        else
        {
          createdIds.Add(newId);
          createdCount++;
        }
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
      if (createdCount == 0)
      {
        RhinoApp.WriteLine(
          "WoodSheetLayout：零件“{0}”没有生成任何副本，不能计为完成。",
          placement.Part.Name);
      }
      else if (failedCount > 0)
      {
        RhinoApp.WriteLine(
          "WoodSheetLayout：零件“{0}”生成 {1} 个对象，仍有 {2} 个对象失败。",
          placement.Part.Name,
          createdCount,
          failedCount);
      }
      return createdCount > 0 && failedCount == 0;
    }

    private static bool AddClassicPlanarPart(
      RhinoDoc doc,
      PackedSheet sheet,
      PartPlacement placement,
      OutputLayerManager layers,
      int sheetLayer,
      out int createdCount,
      out int failedCount)
    {
      createdCount = 0;
      failedCount = 0;
      var rotation = Transform.Rotation(placement.RotationRadians, Vector3d.ZAxis, Point3d.Origin);
      // 与1.1.0一致，把铺平后的最低Z放到世界XY；曲线所在正面保持朝上。
      var localTranslation = Transform.Translation(
        placement.TranslationX,
        placement.TranslationY,
        -placement.Part.FlatBounds.Min.Z);
      var sheetTranslation = Transform.Translation(sheet.Origin.X, sheet.Origin.Y, 0.0);
      var finalTransform = sheetTranslation * localTranslation * rotation * placement.Part.FlattenTransform;

      var groupName = string.Format(
        "WSL_PAIR_{0:0.00}mm_S{1:00}_{2}_{3}",
        sheet.ThicknessMillimeters,
        sheet.IndexWithinThickness,
        placement.Part.Name,
        Guid.NewGuid().ToString("N").Substring(0, 6));
      var groupIndex = doc.Groups.Add(groupName);
      var createdIds = new List<Guid>();

      foreach (var sourceReference in placement.Part.Objects.Where(item => item != null))
      {
        // 重新从 RhinoDoc 解析源对象，避免使用选择阶段的陈旧 RhinoObject 包装器。
        var source = doc.Objects.FindId(sourceReference.Id);
        if (source == null || source.Geometry == null)
        {
          failedCount++;
          continue;
        }

        // false = 保留原对象并创建变换后的副本。这是 1.1.0 的核心输出方式。
        var newId = doc.Objects.Transform(source.Id, finalTransform, false);
        if (newId == Guid.Empty)
        {
          RhinoApp.WriteLine(
            "WoodSheetLayout：对象“{0}”未能通过1.1原生复制通道铺平。",
            source.Attributes.Name ?? placement.Part.Name);
          failedCount++;
          continue;
        }

        var duplicate = doc.Objects.FindId(newId);
        if (duplicate == null)
        {
          failedCount++;
          continue;
        }

        var attributes = duplicate.Attributes.Duplicate();
        attributes.RemoveFromAllGroups();
        if (groupIndex >= 0)
          attributes.AddToGroup(groupIndex);
        attributes.SetUserString("WoodSheetLayoutRole", "FlatCopy");
        attributes.LayerIndex = layers.GetSourceLayer(sheetLayer, source.Attributes);
        attributes.Name = string.IsNullOrWhiteSpace(source.Attributes.Name)
          ? placement.Part.Name
          : source.Attributes.Name;
        if (!doc.Objects.ModifyAttributes(newId, attributes, true))
        {
          RhinoApp.WriteLine(
            "WoodSheetLayout：对象“{0}”已复制，但输出图层或分组属性设置失败。",
            attributes.Name);
          failedCount++;
          continue;
        }

        createdIds.Add(newId);
        createdCount++;
      }

      // 保留用户要求的“原件 + 铺平件”配对组；不删除原件已有的任何组。
      if (groupIndex >= 0 && createdIds.Count > 0)
      {
        foreach (var sourceReference in placement.Part.Objects.Where(item => item != null))
        {
          var source = doc.Objects.FindId(sourceReference.Id);
          if (source == null)
            continue;
          var sourceAttributes = source.Attributes.Duplicate();
          var existingGroups = sourceAttributes.GetGroupList() ?? new int[0];
          if (!existingGroups.Contains(groupIndex))
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

      if (createdCount == 0)
      {
        RhinoApp.WriteLine(
          "WoodSheetLayout：零件“{0}”没有生成任何副本，不能计为完成。",
          placement.Part.Name);
      }
      else if (failedCount > 0)
      {
        RhinoApp.WriteLine(
          "WoodSheetLayout：零件“{0}”通过1.1原生通道生成 {1} 个对象，仍有 {2} 个对象失败。",
          placement.Part.Name,
          createdCount,
          failedCount);
      }
      return createdCount > 0 && failedCount == 0;
    }

    private static bool TryCreatePlacedGeometry(
      RhinoDoc doc,
      BoardPart part,
      FlatGeometryItem item,
      Transform finalTransform,
      out GeometryBase geometry)
    {
      geometry = item.Geometry == null ? null : item.Geometry.Duplicate();
      if (geometry != null && geometry.Transform(finalTransform))
        return true;

      // 某些导入曲线或块实例连续执行两次变换会失败；退回源对象，
      // 把“铺平＋旋转＋排版平移”合并为一次变换再复制。
      var source = doc.Objects.FindId(item.SourceObjectId);
      geometry = source == null || source.Geometry == null ? null : source.Geometry.Duplicate();
      if (geometry == null)
        return false;
      var combined = finalTransform * part.FlattenTransform;
      return geometry.Transform(combined);
    }

    private static Guid AddGeometryWithFallback(
      RhinoDoc doc,
      GeometryBase geometry,
      ObjectAttributes attributes)
    {
      var result = doc.Objects.Add(geometry, attributes);
      if (result != Guid.Empty)
        return result;

      // 通用 Add 在 Rhino 7 对部分导入曲线或块实例可能返回 Guid.Empty。
      // 使用具体几何重载重试，避免排版统计有零件但文档里没有副本。
      var curve = geometry as Curve;
      if (curve != null)
        return doc.Objects.AddCurve(curve, attributes);
      var brep = geometry as Brep;
      if (brep != null)
        return doc.Objects.AddBrep(brep, attributes);
      var instance = geometry as InstanceReferenceGeometry;
      if (instance != null)
      {
        for (var index = 0; index < doc.InstanceDefinitions.Count; index++)
        {
          var definition = doc.InstanceDefinitions[index];
          if (definition != null && definition.Id == instance.ParentIdefId)
            return doc.Objects.AddInstanceObject(index, instance.Xform, attributes);
        }
      }
      return Guid.Empty;
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
      attributes.SetUserString("WoodSheetLayoutRole", "OutputGuide");
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
      labelAttributes.SetUserString("WoodSheetLayoutRole", "OutputGuide");
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

    private static void ReportSummary(
      LayoutResult result,
      LayoutSettings settings,
      int componentCount,
      int analyzedPartCount,
      int outputPartCount,
      int outputObjectCount,
      int outputFailureCount)
    {
      var packedPartCount = result.Sheets.Sum(sheet => sheet.Placements.Count);
      RhinoApp.WriteLine(string.Format(
        "WoodSheetLayout 2.2.5：选中组 {0} 件，识别 {1} 件，排入 {2} 件，实际生成 {3} 件/{4} 个对象，{5} 张 {6}；零件间距 {7:0.##} mm，边框出血 {8:0.##} mm。",
        componentCount,
        analyzedPartCount,
        packedPartCount,
        outputPartCount,
        outputObjectCount,
        result.Sheets.Count,
        settings.SheetDescription,
        settings.PartGapMillimeters,
        settings.FrameMarginMillimeters));
      RhinoApp.WriteLine(string.Format(
        "  工作方式：1.1选择/分组/FlatBounds矩形MaxRects＋ObjectTable.Transform原生复制；当前命令：{0}。",
        settings.PartMode == LayoutPartMode.BentOnly ? "折弯板" : "平板"));

      if (componentCount != analyzedPartCount || analyzedPartCount != packedPartCount ||
          packedPartCount != outputPartCount || outputFailureCount > 0)
      {
        RhinoApp.WriteLine(string.Format(
          "WoodSheetLayout：数量校验未通过（组{0}/识别{1}/排入{2}/生成{3}/对象失败{4}），本次不能判定为全部铺平。",
          componentCount,
          analyzedPartCount,
          packedPartCount,
          outputPartCount,
          outputFailureCount));
      }
      else
      {
        RhinoApp.WriteLine("WoodSheetLayout：数量校验通过，所有选中零件均已生成铺平副本。");
      }

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

      if (result.Issues.Count > 0 || outputFailureCount > 0 || outputPartCount != packedPartCount)
      {
        RhinoApp.WriteLine(
          "WoodSheetLayout：仍有 {0} 个分析问题、{1} 个输出对象失败；不创建文字标记。移动WSL_PAIR配对组可核对已铺平零件。",
          result.Issues.Count,
          outputFailureCount);
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
          "WoodSheetLayout_2.2.5_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
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
