using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core
{
  internal static class ManualPageBuilder
  {
    public static List<PageZone> CreatePages(
      RhinoDoc doc,
      AssemblyAnalysis analysis,
      ExplodeSettings settings,
      ICollection<Guid> generatedIds)
    {
      var pages = new List<PageZone>();
      var startX = analysis.Bounds.Max.X +
                   Math.Max(analysis.Bounds.Diagonal.X * 3.0, 150.0 * settings.ModelUnitsPerMillimeter);
      var startY = analysis.Bounds.Min.Y;
      var overview = CreateOverviewPage(doc, analysis, settings,
        new Point2d(startX, startY), 0, generatedIds);
      pages.Add(overview);

      var stepCount = Math.Min(analysis.Parts.Count, settings.MaximumStepPages);
      for (var step = 1; step <= stepCount; step++)
      {
        var origin = new Point2d(
          startX + step * (settings.PageWidth + settings.PageGap),
          startY);
        pages.Add(CreateStepPage(doc, analysis, settings, origin, step, generatedIds));
      }

      foreach (var page in pages)
        CreateLayout(doc, page, settings);
      return pages;
    }

    private static PageZone CreateOverviewPage(
      RhinoDoc doc,
      AssemblyAnalysis analysis,
      ExplodeSettings settings,
      Point2d origin,
      int pageIndex,
      ICollection<Guid> generatedIds)
    {
      var pageBounds = PageBounds(origin, settings);
      var groupIndex = doc.Groups.Add("EB_说明页_00_" + Guid.NewGuid().ToString("N").Substring(0, 6));
      AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, pageBounds, groupIndex));
      var orientation = IsometricOrientation();
      var contentBounds = BoundingBox.Unset;
      foreach (var part in analysis.Parts)
      {
        var transform = orientation * Transform.Translation(
          DrawingBuilder.ExplosionOffset(part, analysis, settings));
        contentBounds = Union(contentBounds, DrawingBuilder.TransformedPartBounds(part, transform));
      }
      var fit = FitToPage(contentBounds, pageBounds, settings, 34.0, 18.0);
      var arrowLayer = DrawingBuilder.FindOrCreateArrowLayer(doc);
      var numberLayer = DrawingBuilder.FindOrCreateNumberLayer(doc);
      foreach (var part in analysis.Parts)
      {
        var offset = DrawingBuilder.ExplosionOffset(part, analysis, settings);
        var explodedTransform = fit * orientation * Transform.Translation(offset);
        foreach (var id in DrawingBuilder.AddPartCopy(doc, part, explodedTransform, groupIndex, false))
          generatedIds.Add(id);
        var explodedCenter = TransformPoint(part.Center, explodedTransform);
        var installedCenter = TransformPoint(part.Center, fit * orientation);
        if (!part.IsBase)
        {
          foreach (var id in DrawingBuilder.AddArrow(
            doc, explodedCenter, installedCenter, settings.ArrowHead, arrowLayer, groupIndex))
            generatedIds.Add(id);
        }
        var labelPoint = explodedCenter + new Vector3d(
          8.0 * settings.ModelUnitsPerMillimeter,
          7.0 * settings.ModelUnitsPerMillimeter,
          0.0);
        foreach (var id in DrawingBuilder.AddNumberBubble(
          doc, part.PartNumber, labelPoint, settings, numberLayer, groupIndex))
          generatedIds.Add(id);
      }
      AddPageHeadings(doc, settings, pageBounds, groupIndex,
        "ASSEMBLY PREVIEW / 装配总览",
        "红色箭头表示安装方向；编号与 WoodExport 零件号保持一致。",
        generatedIds);
      return new PageZone
      {
        PageIndex = pageIndex,
        LayoutName = "EB_00_装配总览",
        Title = "装配总览",
        Bounds = pageBounds
      };
    }

    private static PageZone CreateStepPage(
      RhinoDoc doc,
      AssemblyAnalysis analysis,
      ExplodeSettings settings,
      Point2d origin,
      int step,
      ICollection<Guid> generatedIds)
    {
      var current = analysis.Parts.First(item => item.AssemblyOrder == step);
      var visibleParts = analysis.Parts.Where(item => item.AssemblyOrder <= step).ToList();
      var pageBounds = PageBounds(origin, settings);
      var groupIndex = doc.Groups.Add(string.Format(
        "EB_说明页_{0:00}_{1}", step, Guid.NewGuid().ToString("N").Substring(0, 6)));
      AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, pageBounds, groupIndex));

      var orientation = IsometricOrientation();
      var contentBounds = BoundingBox.Unset;
      foreach (var part in visibleParts)
      {
        var offset = part == current
          ? DrawingBuilder.ExplosionOffset(part, analysis, settings)
          : Vector3d.Zero;
        var transform = orientation * Transform.Translation(offset);
        contentBounds = Union(contentBounds, DrawingBuilder.TransformedPartBounds(part, transform));
      }
      var fit = FitToPage(contentBounds, pageBounds, settings, 38.0, 22.0);
      var installedTransform = fit * orientation;
      foreach (var part in visibleParts)
      {
        var transform = part == current
          ? fit * orientation * Transform.Translation(
              DrawingBuilder.ExplosionOffset(part, analysis, settings))
          : installedTransform;
        foreach (var id in DrawingBuilder.AddPartCopy(
          doc, part, transform, groupIndex, part == current))
          generatedIds.Add(id);
      }

      var currentCenter = TransformPoint(current.Center,
        fit * orientation * Transform.Translation(
          DrawingBuilder.ExplosionOffset(current, analysis, settings)));
      var targetCenter = TransformPoint(current.Center, installedTransform);
      if (!current.IsBase)
      {
        foreach (var id in DrawingBuilder.AddArrow(
          doc, currentCenter, targetCenter, settings.ArrowHead,
          DrawingBuilder.FindOrCreateArrowLayer(doc), groupIndex))
          generatedIds.Add(id);
      }
      foreach (var id in DrawingBuilder.AddNumberBubble(
        doc,
        current.PartNumber,
        currentCenter + new Vector3d(
          9.0 * settings.ModelUnitsPerMillimeter,
          8.0 * settings.ModelUnitsPerMillimeter,
          0.0),
        settings,
        DrawingBuilder.FindOrCreateNumberLayer(doc),
        groupIndex))
        generatedIds.Add(id);

      var instruction = current.IsBase
        ? "步骤 01：以 " + current.PartNumber + " 作为装配基准件。"
        : "步骤 " + step.ToString("00", CultureInfo.InvariantCulture) +
          "：将 " + current.PartNumber + "（" + current.Name + "）沿红色箭头安装。";
      AddPageHeadings(doc, settings, pageBounds, groupIndex,
        string.Format("STEP {0:00}/{1:00}  {2}", step, analysis.Parts.Count, current.PartNumber),
        instruction,
        generatedIds);
      return new PageZone
      {
        PageIndex = step,
        LayoutName = "EB_" + step.ToString("00", CultureInfo.InvariantCulture) + "_" + current.PartNumber,
        Title = instruction,
        Bounds = pageBounds
      };
    }

    private static void AddPageHeadings(
      RhinoDoc doc,
      ExplodeSettings settings,
      BoundingBox pageBounds,
      int groupIndex,
      string title,
      string instruction,
      ICollection<Guid> generatedIds)
    {
      var titlePoint = new Point3d(
        pageBounds.Min.X + 12.0 * settings.ModelUnitsPerMillimeter,
        pageBounds.Max.Y - 15.0 * settings.ModelUnitsPerMillimeter,
        0.0);
      var notePoint = new Point3d(
        pageBounds.Min.X + 12.0 * settings.ModelUnitsPerMillimeter,
        pageBounds.Min.Y + 10.0 * settings.ModelUnitsPerMillimeter,
        0.0);
      AddId(generatedIds, DrawingBuilder.AddPageText(
        doc, title, titlePoint, 6.0 * settings.ModelUnitsPerMillimeter,
        groupIndex, TextJustification.MiddleLeft));
      AddId(generatedIds, DrawingBuilder.AddPageText(
        doc, instruction, notePoint, 4.0 * settings.ModelUnitsPerMillimeter,
        groupIndex, TextJustification.MiddleLeft));
    }

    private static BoundingBox PageBounds(Point2d origin, ExplodeSettings settings)
    {
      return new BoundingBox(
        new Point3d(origin.X, origin.Y, 0.0),
        new Point3d(origin.X + settings.PageWidth, origin.Y + settings.PageHeight, 0.0));
    }

    private static Transform IsometricOrientation()
    {
      var turn = Transform.Rotation(-Math.PI * 0.25, Vector3d.ZAxis, Point3d.Origin);
      var tilt = Transform.Rotation(Math.Atan(1.0 / Math.Sqrt(2.0)), Vector3d.XAxis, Point3d.Origin);
      return tilt * turn;
    }

    private static Transform FitToPage(
      BoundingBox content,
      BoundingBox page,
      ExplodeSettings settings,
      double topMarginMillimeters,
      double bottomMarginMillimeters)
    {
      var sideMargin = 14.0 * settings.ModelUnitsPerMillimeter;
      var availableWidth = page.Diagonal.X - 2.0 * sideMargin;
      var availableHeight = page.Diagonal.Y -
        (topMarginMillimeters + bottomMarginMillimeters) * settings.ModelUnitsPerMillimeter;
      var width = Math.Max(content.Diagonal.X, settings.ModelUnitsPerMillimeter);
      var height = Math.Max(content.Diagonal.Y, settings.ModelUnitsPerMillimeter);
      var scaleFactor = Math.Min(availableWidth / width, availableHeight / height);
      scaleFactor = Math.Max(0.001, scaleFactor);
      var scale = Transform.Scale(Point3d.Origin, scaleFactor);
      var scaledCenter = content.Center;
      scaledCenter.Transform(scale);
      var targetCenter = new Point3d(
        (page.Min.X + page.Max.X) * 0.5,
        page.Min.Y + bottomMarginMillimeters * settings.ModelUnitsPerMillimeter + availableHeight * 0.5,
        0.0);
      return Transform.Translation(targetCenter - scaledCenter) * scale;
    }

    private static void CreateLayout(RhinoDoc doc, PageZone page, ExplodeSettings settings)
    {
      doc.PageUnitSystem = UnitSystem.Millimeters;
      var pageView = doc.Views.AddPageView(
        UniqueLayoutName(doc, page.LayoutName),
        settings.PageWidthMillimeters,
        settings.PageHeightMillimeters);
      if (pageView == null)
        return;
      var topLeft = new Point2d(5.0, settings.PageHeightMillimeters - 5.0);
      var bottomRight = new Point2d(settings.PageWidthMillimeters - 5.0, 5.0);
      var detail = pageView.AddDetailView(
        "装配说明",
        topLeft,
        bottomRight,
        DefinedViewportProjection.Top);
      if (detail == null)
        return;
      pageView.SetActiveDetail(detail.Id);
      detail.Viewport.ZoomBoundingBox(page.Bounds);
      detail.DetailGeometry.IsProjectionLocked = true;
      detail.CommitChanges();
    }

    private static string UniqueLayoutName(RhinoDoc doc, string desired)
    {
      var existing = new HashSet<string>(
        (doc.Views.GetPageViews() ?? new RhinoPageView[0]).Select(item => item.PageName),
        StringComparer.OrdinalIgnoreCase);
      if (!existing.Contains(desired))
        return desired;
      var suffix = 2;
      while (existing.Contains(desired + "_" + suffix))
        suffix++;
      return desired + "_" + suffix;
    }

    private static Point3d TransformPoint(Point3d source, Transform transform)
    {
      source.Transform(transform);
      return source;
    }

    private static BoundingBox Union(BoundingBox left, BoundingBox right)
    {
      if (!left.IsValid) return right;
      if (!right.IsValid) return left;
      return BoundingBox.Union(left, right);
    }

    private static void AddId(ICollection<Guid> ids, Guid id)
    {
      if (id != Guid.Empty)
        ids.Add(id);
    }
  }
}
