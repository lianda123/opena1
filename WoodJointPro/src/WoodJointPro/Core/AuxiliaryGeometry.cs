using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodJointPro.Core
{
  internal static class AuxiliaryGeometry
  {
    internal static bool TryCutAxisHole(
      BoardInfo board,
      Point3d point,
      double diameter,
      double tolerance,
      out Brep result)
    {
      result = null;
      if (board == null || diameter <= tolerance)
        return false;
      var normal = board.MidPlane.Normal;
      if (!normal.Unitize())
        return false;
      var projected = board.MidPlane.ClosestPoint(point);
      var extra = Math.Max(tolerance * 5.0, board.Thickness * 0.1);
      var plane = new Plane(
        projected - normal * (board.Thickness * 0.5 + extra),
        board.MidPlane.XAxis,
        board.MidPlane.YAxis);
      var circle = new Circle(plane, diameter * 0.5).ToNurbsCurve();
      var cutter = Extrusion.Create(circle, board.Thickness + extra * 2.0, true);
      if (cutter == null)
        return false;
      Brep[] difference;
      try
      {
        difference = Brep.CreateBooleanDifference(
          new[] { board.Brep },
          new[] { cutter.ToBrep() },
          tolerance);
      }
      catch
      {
        difference = null;
      }
      result = (difference ?? new Brep[0])
        .Where(item => item != null && item.IsValid && item.IsSolid)
        .OrderByDescending(Volume)
        .FirstOrDefault();
      return result != null;
    }

    internal static bool CreateCalibrationCoupon(
      RhinoDoc doc,
      Point3d origin,
      double thicknessMillimeters,
      out string description)
    {
      description = null;
      if (doc == null || thicknessMillimeters <= 0.1)
        return false;
      var scale = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      if (!RhinoMath.IsValidDouble(scale) || scale <= 0.0)
        return false;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, scale * 0.001);
      var width = 80.0 * scale;
      var height = 34.0 * scale;
      var thickness = thicknessMillimeters * scale;
      var platePlane = new Plane(origin, Vector3d.XAxis, Vector3d.YAxis);
      var plate = new Box(
        platePlane,
        new Interval(0.0, width),
        new Interval(0.0, height),
        new Interval(0.0, thickness)).ToBrep();
      var cutters = new List<Brep>();
      var slotOffsets = new[] { -0.15, -0.10, -0.05, 0.0, 0.05, 0.10, 0.15 };
      for (var index = 0; index < slotOffsets.Length; index++)
      {
        var slotWidth = Math.Max(tolerance * 3.0,
          (thicknessMillimeters + slotOffsets[index]) * scale);
        var xCenter = (6.0 + index * 8.0) * scale;
        cutters.Add(new Box(
          platePlane,
          new Interval(xCenter - slotWidth * 0.5, xCenter + slotWidth * 0.5),
          new Interval(height - 9.0 * scale, height + tolerance * 5.0),
          new Interval(-tolerance * 5.0, thickness + tolerance * 5.0)).ToBrep());
      }
      var holeDiameters = new[] { 1.90, 2.00, 2.10, 2.20, 2.30 };
      for (var index = 0; index < holeDiameters.Length; index++)
      {
        var center = platePlane.PointAt((12.0 + index * 13.0) * scale, 10.0 * scale);
        var holePlane = new Plane(center - Vector3d.ZAxis * tolerance * 5.0, Vector3d.ZAxis);
        var curve = new Circle(holePlane, holeDiameters[index] * scale * 0.5).ToNurbsCurve();
        var extrusion = Extrusion.Create(curve, thickness + tolerance * 10.0, true);
        if (extrusion != null)
          cutters.Add(extrusion.ToBrep());
      }
      Brep[] difference;
      try
      {
        difference = Brep.CreateBooleanDifference(new[] { plate }, cutters.ToArray(), tolerance);
      }
      catch
      {
        difference = null;
      }
      var coupon = (difference ?? new Brep[0])
        .Where(item => item != null && item.IsValid && item.IsSolid)
        .OrderByDescending(Volume)
        .FirstOrDefault();
      if (coupon == null)
        return false;

      var layerIndex = EnsureLayer(doc, "WoodJointPro_校准测试片", Color.FromArgb(230, 160, 55));
      var groupIndex = doc.Groups.Add("WJP_CALIBRATION_" + Guid.NewGuid().ToString("N").Substring(0, 6));
      var attributes = new ObjectAttributes
      {
        LayerIndex = layerIndex,
        Name = string.Format(CultureInfo.InvariantCulture, "WJP_{0:0.###}mm公差测试片", thicknessMillimeters)
      };
      if (groupIndex >= 0)
        attributes.AddToGroup(groupIndex);
      attributes.SetUserString("WoodJointPro.Role", "CalibrationCoupon");
      var couponId = doc.Objects.AddBrep(coupon, attributes);
      if (couponId == Guid.Empty)
        return false;

      for (var index = 0; index < slotOffsets.Length; index++)
      {
        var label = string.Format(CultureInfo.InvariantCulture, "槽{0:+0.00;-0.00;0.00}", slotOffsets[index]);
        AddDot(doc, platePlane.PointAt((6.0 + index * 8.0) * scale, 27.0 * scale, thickness), label, attributes);
      }
      for (var index = 0; index < holeDiameters.Length; index++)
      {
        var label = string.Format(CultureInfo.InvariantCulture, "Ø{0:0.00}", holeDiameters[index]);
        AddDot(doc, platePlane.PointAt((12.0 + index * 13.0) * scale, 4.0 * scale, thickness), label, attributes);
      }
      doc.Views.Redraw();
      description = string.Format(
        CultureInfo.InvariantCulture,
        "已生成{0:0.###}mm测试片：7档插槽（-0.15至+0.15mm）和5档2mm轴孔（1.90至2.30mm）",
        thicknessMillimeters);
      return true;
    }

    private static void AddDot(
      RhinoDoc doc,
      Point3d point,
      string text,
      ObjectAttributes sourceAttributes)
    {
      var attributes = sourceAttributes.Duplicate();
      attributes.Name = text;
      doc.Objects.AddTextDot(new TextDot(text, point), attributes);
    }

    private static int EnsureLayer(RhinoDoc doc, string name, Color color)
    {
      var existing = doc.Layers.Find(name, true);
      if (existing >= 0)
        return existing;
      var index = doc.Layers.Add(new Layer { Name = name, Color = color });
      return index >= 0 ? index : doc.Layers.CurrentLayerIndex;
    }

    private static double Volume(Brep brep)
    {
      var properties = brep == null ? null : VolumeMassProperties.Compute(brep);
      return properties == null ? 0.0 : properties.Volume;
    }
  }
}
