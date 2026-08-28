using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;

namespace ProductMotionTimeline.Core
{
  internal enum PlanetaryFixedMember
  {
    Ring = 0,
    Sun = 1,
    Carrier = 2,
    GeometryOnly = 3
  }

  internal sealed class PlanetaryGearSetParameters
  {
    public int SunTeeth { get; set; } = 24;
    public int PlanetTeeth { get; set; } = 18;
    public int PlanetCount { get; set; } = 3;
    public double Module { get; set; } = 1.0;
    public double PressureAngleDegrees { get; set; } = 20.0;
    public double GearThickness { get; set; } = 3.0;
    public double BoreDiameter { get; set; } = 2.0;
    public double CarrierThickness { get; set; } = 2.0;
    public double PlanetShaftDiameter { get; set; } = 2.0;
    public bool OutputPitchReferences { get; set; } = true;
    public PlanetaryFixedMember FixedMember { get; set; } = PlanetaryFixedMember.Ring;

    public int RingTeeth => SunTeeth + 2 * PlanetTeeth;
    public double PlanetCenterDistance => Module * (SunTeeth + PlanetTeeth) * 0.5;
    public double PlanetOutsideDiameter => Module * (PlanetTeeth + 2.0);
  }

  internal sealed class PlanetaryValidationResult
  {
    public bool IsValid { get; set; }
    public string Message { get; set; }
    public List<int> CompatiblePlanetCounts { get; } = new List<int>();
  }

  internal static class PlanetaryGearSetGenerator
  {
    public static PlanetaryValidationResult Validate(PlanetaryGearSetParameters parameters)
    {
      var result = new PlanetaryValidationResult();
      if (parameters == null || parameters.SunTeeth < 4 || parameters.PlanetTeeth < 4)
      {
        result.Message = "太阳轮和行星轮齿数都必须大于等于 4";
        return result;
      }
      if (parameters.PlanetCount < 2 || parameters.PlanetCount > 12)
      {
        result.Message = "行星轮数量必须在 2–12 之间";
        return result;
      }
      if (parameters.Module <= 0.0 || parameters.GearThickness <= 0.0 ||
          parameters.CarrierThickness <= 0.0)
      {
        result.Message = "模数、齿轮厚度和行星架厚度必须大于 0";
        return result;
      }
      if (parameters.PressureAngleDegrees < 1.0 || parameters.PressureAngleDegrees > 44.0)
      {
        result.Message = "压力角必须在 1°–44° 之间";
        return result;
      }

      for (var count = 2; count <= 12; count++)
      {
        if ((parameters.SunTeeth + parameters.RingTeeth) % count == 0 &&
            !PlanetsOverlap(parameters, count))
          result.CompatiblePlanetCounts.Add(count);
      }

      if ((parameters.SunTeeth + parameters.RingTeeth) % parameters.PlanetCount != 0)
      {
        result.Message = string.Format(
          "不能均匀装配：({0}+{1})/{2} 不是整数；可用行星轮数量：{3}",
          parameters.SunTeeth,
          parameters.RingTeeth,
          parameters.PlanetCount,
          CompatibleText(result.CompatiblePlanetCounts));
        return result;
      }
      if (PlanetsOverlap(parameters, parameters.PlanetCount))
      {
        result.Message = string.Format(
          "行星轮会互相重叠：相邻中心距 {0:0.###}，齿顶圆直径 {1:0.###}；可用数量：{2}",
          AdjacentPlanetCenterDistance(parameters, parameters.PlanetCount),
          parameters.PlanetOutsideDiameter,
          CompatibleText(result.CompatiblePlanetCounts));
        return result;
      }

      result.IsValid = true;
      result.Message = string.Format(
        "齿数关系与均布检查通过：Zr=Zs+2Zp，{0}={1}+2×{2}，({1}+{0})/{3}={4}",
        parameters.RingTeeth,
        parameters.SunTeeth,
        parameters.PlanetTeeth,
        parameters.PlanetCount,
        (parameters.SunTeeth + parameters.RingTeeth) / parameters.PlanetCount);
      return result;
    }

    public static IEnumerable<Plane> PlanetPlanes(
      PlanetaryGearSetParameters parameters,
      Plane centerPlane)
    {
      for (var index = 0; index < parameters.PlanetCount; index++)
      {
        var angle = Math.PI * 2.0 * index / parameters.PlanetCount;
        var plane = centerPlane;
        plane.Origin = centerPlane.PointAt(
          parameters.PlanetCenterDistance * Math.Cos(angle),
          parameters.PlanetCenterDistance * Math.Sin(angle));
        var phase = angle + Math.PI -
                    (parameters.SunTeeth * angle + Math.PI) / parameters.PlanetTeeth;
        yield return RotatePlane(plane, phase);
      }
    }

    public static Plane RingPlane(
      PlanetaryGearSetParameters parameters,
      Plane centerPlane)
    {
      return RotatePlane(
        centerPlane,
        -Math.PI * parameters.PlanetTeeth / Math.Max(1, parameters.RingTeeth));
    }

    public static GeometryBase CreateCarrierGeometry(
      RhinoDoc doc,
      PlanetaryGearSetParameters parameters,
      Plane centerPlane,
      out string warning)
    {
      warning = string.Empty;
      if (doc == null || parameters == null || !centerPlane.IsValid)
        return null;

      var outerRadius = parameters.PlanetCenterDistance +
                        Math.Max(parameters.Module * 1.5, parameters.PlanetShaftDiameter);
      var localOuter = new Circle(Plane.WorldXY, outerRadius).ToNurbsCurve();
      var height = Math.Max(1e-6, parameters.CarrierThickness);
      var body = Extrusion.Create(localOuter, height, true)?.ToBrep();
      if (body == null)
      {
        warning = "行星架圆盘挤出失败";
        return null;
      }

      var cutters = new List<Brep>();
      AddCutter(cutters, Point3d.Origin, parameters.BoreDiameter * 0.5, height);
      foreach (var plane in PlanetPlanes(parameters, Plane.WorldXY))
      {
        AddCutter(
          cutters,
          plane.Origin,
          Math.Max(parameters.PlanetShaftDiameter * 0.5, parameters.Module * 0.35),
          height);
      }

      if (cutters.Count > 0)
      {
        var difference = Brep.CreateBooleanDifference(
          new[] { body }, cutters, doc.ModelAbsoluteTolerance);
        if (difference != null && difference.Length > 0)
          body = difference[0];
        else
          warning = "行星架轴孔布尔运算未完成，已保留无孔圆盘";
      }

      body.Transform(Transform.Translation(0.0, 0.0, -height));
      body.Transform(Transform.PlaneToPlane(Plane.WorldXY, centerPlane));
      return body;
    }

    public static string TransmissionDescription(PlanetaryGearSetParameters parameters)
    {
      var sun = Math.Max(1, parameters.SunTeeth);
      var ring = Math.Max(1, parameters.RingTeeth);
      switch (parameters.FixedMember)
      {
        case PlanetaryFixedMember.Sun:
          return string.Format(
            "太阳轮固定、内齿圈输入、行星架输出：ωc/ωr={0:0.####}，减速比 {1:0.####}:1",
            ring / (double)(sun + ring),
            (sun + ring) / (double)ring);
        case PlanetaryFixedMember.Carrier:
          return string.Format(
            "行星架固定、太阳轮输入、内齿圈输出：ωr/ωs=-{0:0.####}，反向减速比 {1:0.####}:1",
            sun / (double)ring,
            ring / (double)sun);
        case PlanetaryFixedMember.GeometryOnly:
          return "仅生成几何和轨道，不建立自动传动关系";
        default:
          return string.Format(
            "内齿圈固定、太阳轮输入、行星架输出：ωc/ωs={0:0.####}，减速比 {1:0.####}:1",
            sun / (double)(sun + ring),
            1.0 + ring / (double)sun);
      }
    }

    private static bool PlanetsOverlap(PlanetaryGearSetParameters parameters, int count)
    {
      return AdjacentPlanetCenterDistance(parameters, count) + parameters.Module * 0.05 <
             parameters.PlanetOutsideDiameter;
    }

    private static double AdjacentPlanetCenterDistance(
      PlanetaryGearSetParameters parameters,
      int count)
    {
      return 2.0 * parameters.PlanetCenterDistance * Math.Sin(Math.PI / Math.Max(2, count));
    }

    private static string CompatibleText(IEnumerable<int> counts)
    {
      var values = (counts ?? Enumerable.Empty<int>()).ToList();
      return values.Count == 0 ? "无（请调整齿数）" : string.Join("、", values);
    }

    private static void AddCutter(
      ICollection<Brep> cutters,
      Point3d center,
      double radius,
      double height)
    {
      if (cutters == null || radius <= 1e-9)
        return;
      var plane = Plane.WorldXY;
      plane.Origin = center;
      var curve = new Circle(plane, radius).ToNurbsCurve();
      var cutter = Extrusion.Create(curve, height, true)?.ToBrep();
      if (cutter != null)
        cutters.Add(cutter);
    }

    private static Plane RotatePlane(Plane plane, double angleRadians)
    {
      plane.Transform(Transform.Rotation(angleRadians, plane.ZAxis, plane.Origin));
      return plane;
    }
  }
}
