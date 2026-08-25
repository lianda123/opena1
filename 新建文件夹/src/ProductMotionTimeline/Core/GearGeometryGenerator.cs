using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;

namespace ProductMotionTimeline.Core
{
  internal static class GearGeometryGenerator
  {
    private const double Clearance = 0.167;

    public static GeometryBase CreateGearGeometry(
      RhinoDoc doc,
      GearParameters parameters,
      Plane plane,
      out string warning)
    {
      var solid = CreateGearSolid(doc, parameters, plane, out warning);
      if (solid != null)
        return solid;

      var profile = CreateFallbackProfile(parameters);
      if (profile == null)
        return null;
      profile.Transform(Transform.PlaneToPlane(Plane.WorldXY, plane));
      warning = (string.IsNullOrWhiteSpace(warning)
        ? "实体生成未完成"
        : warning) + "；已按 RhinoGears 的曲线工作流回退输出闭合齿形曲线";
      return profile;
    }

    public static Brep CreateGearSolid(
      RhinoDoc doc,
      GearParameters parameters,
      Plane plane,
      out string warning)
    {
      warning = string.Empty;
      if (doc == null || parameters == null || !plane.IsValid)
        return null;
      Brep local;
      switch (parameters.Type)
      {
        case GearPartType.Internal:
          local = CreateInternalSolid(doc, parameters, out warning);
          break;
        case GearPartType.Helical:
          local = CreateHelicalSolid(doc, parameters, out warning);
          break;
        case GearPartType.Bevel:
          local = CreateBevelSolid(doc, parameters, out warning);
          break;
        case GearPartType.Rack:
          local = CreateRackSolid(parameters);
          break;
        default:
          local = CreateSpurSolid(doc, parameters, out warning);
          break;
      }
      if (local == null)
      {
        if (string.IsNullOrWhiteSpace(warning))
          warning = parameters.DisplayName + "实体生成失败";
        return null;
      }
      local.Transform(Transform.PlaneToPlane(Plane.WorldXY, plane));
      return local;
    }

    public static Curve CreateExternalOutline(int teeth, double module, double pressureAngleDegrees)
    {
      teeth = Math.Max(4, teeth);
      module = Math.Max(1e-6, module);
      var pressure = RhinoMath.ToRadians(Math.Max(1.0, Math.Min(44.0, pressureAngleDegrees)));
      var pitchRadius = module * teeth * 0.5;
      var baseRadius = pitchRadius * Math.Cos(pressure);
      var rootRadius = Math.Max(module * 0.2, pitchRadius - (1.0 + Clearance) * module);
      var outsideRadius = pitchRadius + module;
      var tStart = rootRadius > baseRadius
        ? Math.Sqrt(Math.Max(0.0, rootRadius * rootRadius / (baseRadius * baseRadius) - 1.0))
        : 0.0;
      var tEnd = Math.Sqrt(Math.Max(0.0, outsideRadius * outsideRadius / (baseRadius * baseRadius) - 1.0));
      var halfAtPitch = Math.PI / (2.0 * teeth);
      var involuteAtPressure = Math.Tan(pressure) - pressure;
      var pitch = Math.PI * 2.0 / teeth;
      var points = new List<Point3d>();
      const int flankSamples = 7;
      const int tipSamples = 4;

      for (var tooth = 0; tooth < teeth; tooth++)
      {
        var center = tooth * pitch;
        points.Add(Polar(rootRadius, center - pitch * 0.5));
        var rootAngle = halfAtPitch + involuteAtPressure - (tStart - Math.Atan(tStart));
        points.Add(Polar(rootRadius, center - rootAngle));
        for (var sample = 0; sample <= flankSamples; sample++)
        {
          var t = tStart + (tEnd - tStart) * sample / flankSamples;
          var radius = baseRadius * Math.Sqrt(1.0 + t * t);
          var halfAngle = halfAtPitch + involuteAtPressure - (t - Math.Atan(t));
          points.Add(Polar(radius, center - halfAngle));
        }
        var outerHalf = halfAtPitch + involuteAtPressure - (tEnd - Math.Atan(tEnd));
        for (var sample = 1; sample <= tipSamples; sample++)
        {
          var angle = center - outerHalf + outerHalf * 2.0 * sample / tipSamples;
          points.Add(Polar(outsideRadius, angle));
        }
        for (var sample = flankSamples; sample >= 0; sample--)
        {
          var t = tStart + (tEnd - tStart) * sample / flankSamples;
          var radius = baseRadius * Math.Sqrt(1.0 + t * t);
          var halfAngle = halfAtPitch + involuteAtPressure - (t - Math.Atan(t));
          points.Add(Polar(radius, center + halfAngle));
        }
        points.Add(Polar(rootRadius, center + rootAngle));
        points.Add(Polar(rootRadius, center + pitch * 0.5));
      }
      return CleanClosedPolyline(points);
    }

    public static Curve CreateInternalBoundary(int teeth, double module, double pressureAngleDegrees)
    {
      teeth = Math.Max(8, teeth);
      module = Math.Max(1e-6, module);
      var pitchRadius = module * teeth * 0.5;
      var pressure = RhinoMath.ToRadians(Math.Max(1.0, Math.Min(44.0, pressureAngleDegrees)));
      var baseRadius = pitchRadius * Math.Cos(pressure);
      var tipRadius = Math.Max(module, pitchRadius - module);
      var rootRadius = pitchRadius + (1.0 + Clearance) * module;
      var tTip = tipRadius > baseRadius
        ? Math.Sqrt(Math.Max(0.0, tipRadius * tipRadius / (baseRadius * baseRadius) - 1.0))
        : 0.0;
      var tRoot = Math.Sqrt(Math.Max(0.0, rootRadius * rootRadius / (baseRadius * baseRadius) - 1.0));
      var pitch = Math.PI * 2.0 / teeth;
      var halfAtPitch = Math.PI / (2.0 * teeth);
      var involuteAtPressure = Math.Tan(pressure) - pressure;
      var tipHalf = halfAtPitch - involuteAtPressure + (tTip - Math.Atan(tTip));
      var rootHalf = halfAtPitch - involuteAtPressure + (tRoot - Math.Atan(tRoot));
      var points = new List<Point3d>();
      const int flankSamples = 7;
      const int tipSamples = 4;
      for (var tooth = 0; tooth < teeth; tooth++)
      {
        var center = tooth * pitch;
        points.Add(Polar(rootRadius, center - pitch * 0.5));
        points.Add(Polar(rootRadius, center - rootHalf));
        for (var sample = flankSamples; sample >= 0; sample--)
        {
          var t = tTip + (tRoot - tTip) * sample / flankSamples;
          var radius = baseRadius * Math.Sqrt(1.0 + t * t);
          var halfAngle = halfAtPitch - involuteAtPressure + (t - Math.Atan(t));
          points.Add(Polar(radius, center - halfAngle));
        }
        if (tipRadius < baseRadius)
          points.Add(Polar(tipRadius, center - tipHalf));
        for (var sample = 1; sample <= tipSamples; sample++)
          points.Add(Polar(tipRadius, center - tipHalf + tipHalf * 2.0 * sample / tipSamples));
        if (tipRadius < baseRadius)
          points.Add(Polar(baseRadius, center + tipHalf));
        for (var sample = 0; sample <= flankSamples; sample++)
        {
          var t = tTip + (tRoot - tTip) * sample / flankSamples;
          var radius = baseRadius * Math.Sqrt(1.0 + t * t);
          var halfAngle = halfAtPitch - involuteAtPressure + (t - Math.Atan(t));
          points.Add(Polar(radius, center + halfAngle));
        }
        points.Add(Polar(rootRadius, center + rootHalf));
        points.Add(Polar(rootRadius, center + pitch * 0.5));
      }
      return CleanClosedPolyline(points);
    }

    public static Curve CreateRackOutline(double length, double module, double pressureAngleDegrees)
    {
      length = Math.Max(module * Math.PI, length);
      module = Math.Max(1e-6, module);
      var pressure = RhinoMath.ToRadians(Math.Max(1.0, Math.Min(44.0, pressureAngleDegrees)));
      var circularPitch = module * Math.PI;
      var addendum = module;
      var dedendum = (1.0 + Clearance) * module;
      var bodyHeight = Math.Max(module * 2.0, dedendum + module);
      var tipHalf = Math.Max(circularPitch * 0.04, circularPitch * 0.25 - addendum * Math.Tan(pressure));
      var rootHalf = Math.Min(circularPitch * 0.48, circularPitch * 0.25 + dedendum * Math.Tan(pressure));
      var start = -length * 0.5;
      var end = length * 0.5;
      var points = new List<Point3d>
      {
        new Point3d(start, -dedendum - bodyHeight, 0),
        new Point3d(end, -dedendum - bodyHeight, 0),
        new Point3d(end, -dedendum, 0)
      };
      var firstIndex = (int)Math.Floor(start / circularPitch) - 1;
      var lastIndex = (int)Math.Ceiling(end / circularPitch) + 1;
      for (var index = lastIndex; index >= firstIndex; index--)
      {
        var center = index * circularPitch;
        AddClipped(points, center + rootHalf, -dedendum, start, end);
        AddClipped(points, center + tipHalf, addendum, start, end);
        AddClipped(points, center - tipHalf, addendum, start, end);
        AddClipped(points, center - rootHalf, -dedendum, start, end);
      }
      points.Add(new Point3d(start, -dedendum, 0));
      return CleanClosedPolyline(points);
    }

    private static Brep CreateSpurSolid(RhinoDoc doc, GearParameters parameters, out string warning)
    {
      warning = string.Empty;
      var outline = CreateExternalOutline(parameters.Teeth, parameters.Module, parameters.PressureAngleDegrees);
      if (outline == null || !outline.IsValid || !outline.IsClosed)
      {
        warning = "渐开线轮廓无效";
        return null;
      }
      var extrusion = Extrusion.Create(outline, Math.Max(1e-6, parameters.Thickness), true);
      var body = extrusion?.ToBrep();
      if (body == null)
      {
        warning = "渐开线轮廓挤出失败";
        return null;
      }
      return ApplyBore(doc, body, parameters.BoreDiameter, parameters.Thickness, out warning);
    }

    private static Brep CreateInternalSolid(RhinoDoc doc, GearParameters parameters, out string warning)
    {
      warning = string.Empty;
      var inner = CreateInternalBoundary(
        parameters.Teeth,
        parameters.Module,
        parameters.PressureAngleDegrees);
      var pitchRadius = parameters.Module * parameters.Teeth * 0.5;
      var outerRadius = pitchRadius + parameters.Module * 3.0;
      var outer = new Circle(Plane.WorldXY, outerRadius).ToNurbsCurve();
      var height = Math.Max(1e-6, parameters.Thickness);
      if (inner == null || !inner.IsValid || !inner.IsClosed)
      {
        warning = "内齿渐开线轮廓无效";
        return null;
      }
      var outerSolid = Extrusion.Create(outer, height, true)?.ToBrep();
      var cutter = Extrusion.Create(inner, height, true)?.ToBrep();
      if (outerSolid == null || cutter == null)
      {
        warning = "内齿轮外环或内齿轮廓挤出失败";
        return null;
      }
      var result = Brep.CreateBooleanDifference(
        new[] { outerSolid }, new[] { cutter }, doc.ModelAbsoluteTolerance);
      if (result != null && result.Length > 0)
        return result[0];
      warning = "内齿布尔运算未完成，已输出外环实体";
      return outerSolid;
    }

    private static Brep CreateHelicalSolid(RhinoDoc doc, GearParameters parameters, out string warning)
    {
      warning = string.Empty;
      var outline = CreateExternalOutline(parameters.Teeth, parameters.Module, parameters.PressureAngleDegrees);
      if (outline == null || !outline.IsValid || !outline.IsClosed)
      {
        warning = "斜齿轮渐开线轮廓无效";
        return null;
      }
      var height = Math.Max(1e-6, parameters.Thickness);
      var pitchDiameter = parameters.Module * parameters.Teeth;
      var helix = RhinoMath.ToRadians(parameters.HelixAngleDegrees);
      var twistRadians = Math.Abs(Math.Tan(helix)) < 1e-9
        ? 0.0
        : height * Math.Tan(helix) / Math.Max(1e-6, pitchDiameter * 0.5);
      var sectionCount = Math.Max(3, Math.Min(17, 3 + (int)Math.Ceiling(Math.Abs(twistRadians) / (Math.PI / 18.0))));
      var sections = new List<Curve>();
      for (var i = 0; i < sectionCount; i++)
      {
        var amount = i / (double)(sectionCount - 1);
        var section = outline.DuplicateCurve();
        section.Transform(Transform.Rotation(twistRadians * amount, Vector3d.ZAxis, Point3d.Origin));
        section.Transform(Transform.Translation(0, 0, height * amount));
        sections.Add(section);
      }
      var lofts = Brep.CreateFromLoft(
        sections, Point3d.Unset, Point3d.Unset, LoftType.Normal, false);
      var body = lofts == null || lofts.Length == 0
        ? null
        : lofts[0].CapPlanarHoles(doc.ModelAbsoluteTolerance);
      if (body == null)
      {
        warning = "斜齿轮放样或封口失败";
        return null;
      }
      return ApplyBore(doc, body, parameters.BoreDiameter, height, out warning);
    }

    private static Brep CreateBevelSolid(RhinoDoc doc, GearParameters parameters, out string warning)
    {
      warning = string.Empty;
      var outline = CreateExternalOutline(parameters.Teeth, parameters.Module, parameters.PressureAngleDegrees);
      if (outline == null || !outline.IsValid || !outline.IsClosed)
      {
        warning = "锥齿轮渐开线轮廓无效";
        return null;
      }
      var pitchRadius = parameters.Module * parameters.Teeth * 0.5;
      var coneHalf = RhinoMath.ToRadians(Math.Max(1.0, Math.Min(179.0, parameters.ConeAngleDegrees)) * 0.5);
      var apexHeight = pitchRadius / Math.Max(1e-6, Math.Tan(coneHalf));
      var height = Math.Min(Math.Max(1e-6, parameters.Thickness), apexHeight * 0.9);
      var sections = new List<Curve>();
      const int sectionCount = 5;
      for (var i = 0; i < sectionCount; i++)
      {
        var amount = i / (double)(sectionCount - 1);
        var scale = Math.Max(0.05, (apexHeight - height * amount) / apexHeight);
        var section = outline.DuplicateCurve();
        section.Transform(Transform.Scale(Point3d.Origin, scale));
        section.Transform(Transform.Translation(0, 0, height * amount));
        sections.Add(section);
      }
      var lofts = Brep.CreateFromLoft(
        sections, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
      var body = lofts == null || lofts.Length == 0
        ? null
        : lofts[0].CapPlanarHoles(doc.ModelAbsoluteTolerance);
      if (body == null)
      {
        warning = "锥齿轮放样或封口失败";
        return null;
      }
      return ApplyBore(doc, body, parameters.BoreDiameter, height, out warning);
    }

    private static Brep CreateRackSolid(GearParameters parameters)
    {
      var outline = CreateRackOutline(
        parameters.RackLength,
        parameters.Module,
        parameters.PressureAngleDegrees);
      if (outline == null || !outline.IsValid || !outline.IsClosed)
        return null;
      return Extrusion.Create(outline, Math.Max(1e-6, parameters.Thickness), true)?.ToBrep();
    }

    private static Curve CreateFallbackProfile(GearParameters parameters)
    {
      if (parameters == null)
        return null;
      switch (parameters.Type)
      {
        case GearPartType.Internal:
          return CreateInternalBoundary(
            parameters.Teeth,
            parameters.Module,
            parameters.PressureAngleDegrees);
        case GearPartType.Rack:
          return CreateRackOutline(
            parameters.RackLength,
            parameters.Module,
            parameters.PressureAngleDegrees);
        default:
          return CreateExternalOutline(
            parameters.Teeth,
            parameters.Module,
            parameters.PressureAngleDegrees);
      }
    }

    private static Curve CleanClosedPolyline(IEnumerable<Point3d> source)
    {
      if (source == null)
        return null;
      var input = source.Where(point => point.IsValid).ToList();
      if (input.Count < 3)
        return null;
      var scale = input.Max(point => Math.Max(Math.Abs(point.X), Math.Abs(point.Y)));
      var tolerance = Math.Max(1e-10, scale * 1e-12);
      var cleaned = new List<Point3d>();
      foreach (var point in input)
      {
        if (cleaned.Count == 0 || cleaned[cleaned.Count - 1].DistanceTo(point) > tolerance)
          cleaned.Add(point);
      }
      if (cleaned.Count < 3)
        return null;
      if (cleaned[0].DistanceTo(cleaned[cleaned.Count - 1]) <= tolerance)
        cleaned[cleaned.Count - 1] = cleaned[0];
      else
        cleaned.Add(cleaned[0]);
      if (cleaned.Count < 4)
        return null;
      var curve = new PolylineCurve(cleaned);
      return curve.IsValid && curve.IsClosed ? curve : null;
    }

    private static Brep ApplyBore(
      RhinoDoc doc,
      Brep body,
      double boreDiameter,
      double height,
      out string warning)
    {
      warning = string.Empty;
      if (body == null || boreDiameter <= doc.ModelAbsoluteTolerance)
        return body;
      var bore = new Circle(Plane.WorldXY, boreDiameter * 0.5).ToNurbsCurve();
      var cutter = Extrusion.Create(bore, Math.Max(1e-6, height), true)?.ToBrep();
      if (cutter == null)
        return body;
      var result = Brep.CreateBooleanDifference(
        new[] { body }, new[] { cutter }, doc.ModelAbsoluteTolerance);
      if (result != null && result.Length > 0)
        return result[0];
      warning = "轴孔布尔运算未完成，已保留无孔齿轮实体";
      return body;
    }

    private static Point3d Polar(double radius, double angle)
    {
      return new Point3d(radius * Math.Cos(angle), radius * Math.Sin(angle), 0.0);
    }

    private static void AddArcPoints(
      List<Point3d> points,
      double radius,
      double start,
      double end,
      int samples,
      bool includeStart)
    {
      var first = includeStart ? 0 : 1;
      for (var sample = first; sample <= samples; sample++)
        points.Add(Polar(radius, start + (end - start) * sample / samples));
    }

    private static void AddClipped(
      List<Point3d> points,
      double x,
      double y,
      double minimum,
      double maximum)
    {
      if (x >= minimum && x <= maximum)
        points.Add(new Point3d(x, y, 0));
    }
  }
}
