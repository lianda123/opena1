using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core
{
  internal static class DrawingBuilder
  {
    private const string ArrowLayerName = "ExplodeBook_箭头";
    private const string NumberLayerName = "ExplodeBook_编号";
    private const string PageLayerName = "ExplodeBook_页面";
    private const string CurrentPartLayerName = "ExplodeBook_当前步骤";

    public static List<Guid> CreateExplodedOverview(
      RhinoDoc doc,
      AssemblyAnalysis analysis,
      ExplodeSettings settings)
    {
      var ids = new List<Guid>();
      var arrowLayer = FindOrCreateLayer(doc, ArrowLayerName, Color.FromArgb(220, 60, 55));
      var numberLayer = FindOrCreateLayer(doc, NumberLayerName, Color.FromArgb(35, 35, 35));
      var gap = 50.0 * settings.ModelUnitsPerMillimeter;
      var baseShift = new Vector3d(
        analysis.Bounds.Max.X + gap - analysis.Bounds.Min.X,
        0.0,
        0.0);
      var groupIndex = doc.Groups.Add("EB_爆炸总览_" + Guid.NewGuid().ToString("N").Substring(0, 6));

      foreach (var part in analysis.Parts)
      {
        var offset = ExplosionOffset(part, analysis, settings);
        var transform = Transform.Translation(baseShift + offset);
        ids.AddRange(AddPartCopy(doc, part, transform, groupIndex, false));

        var installedCenter = part.Center + baseShift;
        var explodedCenter = installedCenter + offset;
        if (!part.IsBase)
          ids.AddRange(AddArrow(doc, explodedCenter, installedCenter, settings.ArrowHead, arrowLayer, groupIndex));
        ids.AddRange(AddNumberBubble(
          doc,
          part.PartNumber,
          LabelPoint(part, explodedCenter, offset, settings),
          settings,
          numberLayer,
          groupIndex));
      }

      var titlePoint = new Point3d(
        analysis.Bounds.Min.X + baseShift.X,
        analysis.Bounds.Max.Y + 12.0 * settings.ModelUnitsPerMillimeter,
        analysis.Bounds.Max.Z + 2.0 * settings.ModelUnitsPerMillimeter);
      var titleId = AddText(doc, "EXPLODED VIEW / 装配爆炸总览", titlePoint,
        7.0 * settings.ModelUnitsPerMillimeter, numberLayer, groupIndex,
        TextJustification.BottomLeft);
      if (titleId != Guid.Empty)
        ids.Add(titleId);
      return ids;
    }

    public static List<Guid> AddPartCopy(
      RhinoDoc doc,
      AssemblyPart part,
      Transform transform,
      int groupIndex,
      bool highlightCurrent)
    {
      var ids = new List<Guid>();
      var highlightLayer = highlightCurrent
        ? FindOrCreateLayer(doc, CurrentPartLayerName, Color.FromArgb(245, 178, 35))
        : -1;
      foreach (var source in part.Objects)
      {
        var id = doc.Objects.Transform(source.Id, transform, false);
        if (id == Guid.Empty)
          continue;
        var duplicate = doc.Objects.FindId(id);
        if (duplicate == null)
          continue;
        var attributes = duplicate.Attributes.Duplicate();
        attributes.RemoveFromAllGroups();
        if (groupIndex >= 0)
          attributes.AddToGroup(groupIndex);
        attributes.Name = "EB_" + part.PartNumber + "_" + (source.Attributes.Name ?? part.Name);
        attributes.SetUserString(AssemblyAnalyzer.GeneratedKey, AssemblyAnalyzer.GeneratedValue);
        attributes.SetUserString(AssemblyAnalyzer.PartNumberKey, part.PartNumber);
        attributes.SetUserString(AssemblyAnalyzer.OrderKey,
          part.AssemblyOrder.ToString(CultureInfo.InvariantCulture));
        if (highlightCurrent)
        {
          attributes.LayerIndex = highlightLayer;
          attributes.ObjectColor = Color.FromArgb(245, 178, 35);
          attributes.ColorSource = ObjectColorSource.ColorFromObject;
        }
        doc.Objects.ModifyAttributes(id, attributes, true);
        ids.Add(id);
      }
      return ids;
    }

    public static List<Guid> AddArrow(
      RhinoDoc doc,
      Point3d start,
      Point3d end,
      double headSize,
      int layerIndex,
      int groupIndex)
    {
      var ids = new List<Guid>();
      var direction = end - start;
      if (!direction.Unitize() || start.DistanceTo(end) <= headSize * 1.5)
        return ids;
      var normal = Vector3d.ZAxis;
      if (Math.Abs(direction * normal) > 0.95)
        normal = Vector3d.YAxis;
      var side = Vector3d.CrossProduct(direction, normal);
      if (!side.Unitize())
        side = Vector3d.XAxis;
      var headBase = end - direction * headSize;
      var left = headBase + side * headSize * 0.45;
      var right = headBase - side * headSize * 0.45;
      ids.Add(AddCurve(doc, new LineCurve(start, end), layerIndex, groupIndex, "安装箭头"));
      ids.Add(AddCurve(doc, new LineCurve(left, end), layerIndex, groupIndex, "安装箭头头部"));
      ids.Add(AddCurve(doc, new LineCurve(right, end), layerIndex, groupIndex, "安装箭头头部"));
      return ids.Where(item => item != Guid.Empty).ToList();
    }

    public static List<Guid> AddNumberBubble(
      RhinoDoc doc,
      string number,
      Point3d center,
      ExplodeSettings settings,
      int layerIndex,
      int groupIndex)
    {
      var ids = new List<Guid>();
      var radius = Math.Max(4.5, 0.65 * (number ?? string.Empty).Length) *
                   settings.ModelUnitsPerMillimeter;
      var circle = new Circle(new Plane(center, Vector3d.ZAxis), radius);
      ids.Add(AddCurve(doc, circle.ToNurbsCurve(), layerIndex, groupIndex, "零件编号框"));
      var textPoint = new Point3d(center.X, center.Y, center.Z);
      var textId = AddText(doc, number, textPoint,
        3.5 * settings.ModelUnitsPerMillimeter, layerIndex, groupIndex,
        TextJustification.MiddleCenter);
      if (textId != Guid.Empty)
        ids.Add(textId);
      return ids.Where(item => item != Guid.Empty).ToList();
    }

    public static Guid AddPageFrame(
      RhinoDoc doc,
      BoundingBox pageBounds,
      int groupIndex)
    {
      var layer = FindOrCreateLayer(doc, PageLayerName, Color.FromArgb(70, 70, 70));
      var z = pageBounds.Min.Z;
      var points = new[]
      {
        new Point3d(pageBounds.Min.X, pageBounds.Min.Y, z),
        new Point3d(pageBounds.Max.X, pageBounds.Min.Y, z),
        new Point3d(pageBounds.Max.X, pageBounds.Max.Y, z),
        new Point3d(pageBounds.Min.X, pageBounds.Max.Y, z),
        new Point3d(pageBounds.Min.X, pageBounds.Min.Y, z)
      };
      return AddCurve(doc, new PolylineCurve(points), layer, groupIndex, "说明书页框");
    }

    public static Guid AddPageText(
      RhinoDoc doc,
      string text,
      Point3d point,
      double height,
      int groupIndex,
      TextJustification justification)
    {
      var layer = FindOrCreateLayer(doc, PageLayerName, Color.FromArgb(35, 35, 35));
      return AddText(doc, text, point, height, layer, groupIndex, justification);
    }

    public static Vector3d ExplosionOffset(
      AssemblyPart part,
      AssemblyAnalysis analysis,
      ExplodeSettings settings)
    {
      if (part.IsBase)
        return Vector3d.Zero;
      Vector3d direction;
      switch (settings.Mode)
      {
        case ExplodeMode.XAxis:
          direction = Vector3d.XAxis;
          break;
        case ExplodeMode.YAxis:
          direction = Vector3d.YAxis;
          break;
        case ExplodeMode.ZAxis:
          direction = Vector3d.ZAxis;
          break;
        default:
          direction = part.Center - analysis.Bounds.Center;
          if (!direction.Unitize())
          {
            var fallback = (part.AssemblyOrder - 2) % 4;
            direction = fallback == 0 ? Vector3d.XAxis :
                        fallback == 1 ? Vector3d.YAxis :
                        fallback == 2 ? -Vector3d.XAxis : -Vector3d.YAxis;
          }
          break;
      }
      direction.Unitize();
      var diagonalAllowance = Math.Min(part.Bounds.Diagonal.Length * 0.30,
        settings.ExplodeDistance * 1.5);
      var distance = settings.ExplodeDistance *
                     (1.0 + 0.28 * Math.Max(0, part.AssemblyOrder - 2)) + diagonalAllowance;
      return direction * distance;
    }

    public static BoundingBox TransformedPartBounds(AssemblyPart part, Transform transform)
    {
      var result = BoundingBox.Unset;
      foreach (var cornerSource in part.Bounds.GetCorners())
      {
        var corner = cornerSource;
        corner.Transform(transform);
        result = result.IsValid ? BoundingBox.Union(result, corner) : new BoundingBox(corner, corner);
      }
      return result;
    }

    public static int FindOrCreateArrowLayer(RhinoDoc doc)
    {
      return FindOrCreateLayer(doc, ArrowLayerName, Color.FromArgb(220, 60, 55));
    }

    public static int FindOrCreateNumberLayer(RhinoDoc doc)
    {
      return FindOrCreateLayer(doc, NumberLayerName, Color.FromArgb(35, 35, 35));
    }

    private static Point3d LabelPoint(
      AssemblyPart part,
      Point3d explodedCenter,
      Vector3d offset,
      ExplodeSettings settings)
    {
      var direction = offset;
      if (!direction.Unitize())
        direction = Vector3d.XAxis;
      if (Math.Abs(direction * Vector3d.ZAxis) > 0.90)
        direction = Vector3d.XAxis;
      return explodedCenter + direction *
        (part.Bounds.Diagonal.Length * 0.35 + 8.0 * settings.ModelUnitsPerMillimeter);
    }

    private static Guid AddCurve(
      RhinoDoc doc,
      Curve curve,
      int layerIndex,
      int groupIndex,
      string name)
    {
      var attributes = GeneratedAttributes(layerIndex, groupIndex, name);
      return doc.Objects.AddCurve(curve, attributes);
    }

    private static Guid AddText(
      RhinoDoc doc,
      string text,
      Point3d point,
      double height,
      int layerIndex,
      int groupIndex,
      TextJustification justification)
    {
      var entity = new TextEntity
      {
        Plane = new Plane(point, Vector3d.ZAxis),
        PlainText = text ?? string.Empty,
        TextHeight = height,
        Justification = justification
      };
      return doc.Objects.AddText(entity, GeneratedAttributes(layerIndex, groupIndex, "说明文字"));
    }

    private static ObjectAttributes GeneratedAttributes(int layerIndex, int groupIndex, string name)
    {
      var attributes = new ObjectAttributes
      {
        LayerIndex = layerIndex,
        Name = name,
        ColorSource = ObjectColorSource.ColorFromLayer
      };
      if (groupIndex >= 0)
        attributes.AddToGroup(groupIndex);
      attributes.SetUserString(AssemblyAnalyzer.GeneratedKey, AssemblyAnalyzer.GeneratedValue);
      return attributes;
    }

    private static int FindOrCreateLayer(RhinoDoc doc, string name, Color color)
    {
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
  }
}
