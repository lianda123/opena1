using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Geometry;

namespace ArcFlow.Core
{
  internal enum SpiralKind
  {
    Fibonacci,
    Golden,
    Archimedean,
    Logarithmic,
    Fermat
  }

  internal sealed class SpiralSettings
  {
    public SpiralKind Kind { get; set; }
    public double StartRadius { get; set; }
    public double Turns { get; set; }
    public double Growth { get; set; }
    public double Tolerance { get; set; }
    public bool Clockwise { get; set; }
  }

  internal static class SpiralFactory
  {
    private const double Phi = 1.6180339887498948482;

    public static List<Arc> Create(Plane plane, SpiralSettings settings)
    {
      if (settings.Kind == SpiralKind.Fibonacci)
        return CreateQuarterArcChain(plane, settings, true);
      if (settings.Kind == SpiralKind.Golden)
        return CreateQuarterArcChain(plane, settings, false);
      return CreateAnalyticBiarcChain(plane, settings);
    }

    private static List<Arc> CreateQuarterArcChain(Plane plane, SpiralSettings settings, bool fibonacci)
    {
      var arcs = new List<Arc>();
      var quarterCount = Math.Max(1, (int)Math.Ceiling(settings.Turns * 4.0));
      var sign = settings.Clockwise ? -1.0 : 1.0;
      var normal = sign * plane.ZAxis;
      var current = plane.Origin + plane.XAxis * settings.StartRadius;
      var tangent = sign * plane.YAxis;
      var fibA = 1.0;
      var fibB = 1.0;

      for (var i = 0; i < quarterCount; i++)
      {
        double scale;
        if (fibonacci)
        {
          scale = fibA;
          var next = fibA + fibB;
          fibA = fibB;
          fibB = next;
        }
        else
        {
          scale = Math.Pow(Phi, i);
        }

        var radius = settings.StartRadius * scale;
        var left = Vector3d.CrossProduct(normal, tangent);
        if (!left.Unitize())
          break;
        var center = current + left * radius;
        var radial = current - center;
        if (!radial.Unitize())
          break;

        var arcPlane = new Plane(center, radial, tangent);
        var remainingQuarter = settings.Turns * 4.0 - i;
        var sweep = Math.Min(1.0, remainingQuarter) * Math.PI * 0.5;
        var arc = new Arc(arcPlane, radius, sweep);
        if (!arc.IsValid)
          break;
        if (!ArcChainBuilder.TryAppendG1(arcs, arc, settings.Tolerance))
          break;
        current = arc.EndPoint;
        tangent = arc.TangentAt(arc.AngleDomain.T1);
        tangent.Unitize();
      }
      return arcs;
    }

    private static List<Arc> CreateAnalyticBiarcChain(Plane plane, SpiralSettings settings)
    {
      var points = new List<Point3d>();
      var tangents = new List<Vector3d>();
      var totalAngle = settings.Turns * Math.PI * 2.0;
      var count = EstimateSegmentCount(settings, totalAngle);
      var sign = settings.Clockwise ? -1.0 : 1.0;

      for (var i = 0; i <= count; i++)
      {
        var u = totalAngle * i / count;
        double radius;
        double radiusDerivative;
        EvaluateRadius(settings, u, out radius, out radiusDerivative);
        var theta = sign * u;
        var cosine = Math.Cos(theta);
        var sine = Math.Sin(theta);
        var x = radius * cosine;
        var y = radius * sine;
        var dx = radiusDerivative * cosine - radius * sine * sign;
        var dy = radiusDerivative * sine + radius * cosine * sign;
        var tangent = plane.XAxis * dx + plane.YAxis * dy;
        if (!tangent.Unitize())
          continue;
        points.Add(plane.PointAt(x, y));
        tangents.Add(tangent);
      }

      return ArcChainBuilder.FromSamples(points, tangents, Math.Max(settings.Tolerance * 0.1, RhinoMath.ZeroTolerance));
    }

    private static void EvaluateRadius(SpiralSettings settings, double u, out double radius, out double derivative)
    {
      if (settings.Kind == SpiralKind.Archimedean)
      {
        var slope = settings.Growth / (2.0 * Math.PI);
        radius = settings.StartRadius + slope * u;
        derivative = slope;
        return;
      }

      if (settings.Kind == SpiralKind.Logarithmic)
      {
        var multiplier = Math.Max(settings.Growth, 1e-6);
        var exponent = Math.Log(multiplier) / (2.0 * Math.PI);
        radius = settings.StartRadius * Math.Exp(exponent * u);
        derivative = exponent * radius;
        return;
      }

      var endRadius = Math.Max(settings.StartRadius + settings.Growth, settings.StartRadius * 1.01);
      var coefficient = (endRadius * endRadius - settings.StartRadius * settings.StartRadius) / (2.0 * Math.PI);
      radius = Math.Sqrt(Math.Max(settings.StartRadius * settings.StartRadius + coefficient * u, RhinoMath.ZeroTolerance));
      derivative = coefficient / (2.0 * radius);
    }

    private static int EstimateSegmentCount(SpiralSettings settings, double totalAngle)
    {
      double endRadius;
      double ignored;
      EvaluateRadius(settings, totalAngle, out endRadius, out ignored);
      var scale = Math.Max(settings.StartRadius, Math.Abs(endRadius));
      var tolerance = Math.Max(settings.Tolerance, 1e-6);
      var angularStep = Math.Sqrt(8.0 * tolerance / Math.Max(scale, tolerance));
      angularStep = Math.Max(Math.PI / 64.0, Math.Min(Math.PI / 6.0, angularStep));
      var count = (int)Math.Ceiling(totalAngle / angularStep);
      var minimum = Math.Max(4, (int)Math.Ceiling(settings.Turns * 8.0));
      return Math.Max(minimum, Math.Min(2048, count));
    }
  }
}
