using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodSheetLayout.Core
{
  internal static class BoardAnalyzer
  {
    public static List<List<RhinoObject>> BuildGroupedComponents(IEnumerable<RhinoObject> sourceObjects)
    {
      var objects = sourceObjects
        .Where(item => item != null && item.Geometry != null)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .ToList();
      var parent = Enumerable.Range(0, objects.Count).ToArray();
      var firstByGroup = new Dictionary<int, int>();

      for (var index = 0; index < objects.Count; index++)
      {
        var groups = objects[index].Attributes.GetGroupList() ?? new int[0];
        foreach (var groupIndex in groups)
        {
          int first;
          if (firstByGroup.TryGetValue(groupIndex, out first))
            Union(parent, index, first);
          else
            firstByGroup[groupIndex] = index;
        }
      }

      return objects
        .Select((item, index) => new { Item = item, Root = Find(parent, index) })
        .GroupBy(item => item.Root)
        .Select(group => group.Select(item => item.Item).ToList())
        .ToList();
    }

    public static bool TryCreatePart(
      RhinoDoc doc,
      IList<RhinoObject> objects,
      int sequence,
      double modelUnitsPerMillimeter,
      out BoardPart part,
      out string warning)
    {
      part = null;
      warning = null;
      if (doc == null || objects == null || objects.Count == 0)
        return false;

      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, modelUnitsPerMillimeter * 0.001);
      var curveSamples = SampleGroupedCurves(objects);
      RhinoObject bestObject = null;
      var bestPlane = Plane.Unset;
      var bestThickness = double.MaxValue;
      var bestFootprint = 0.0;

      foreach (var rhinoObject in objects)
      {
        Plane candidatePlane;
        double candidateThickness;
        double candidateFootprint;
        if (!TryFindBoardPlane(
          rhinoObject.Geometry,
          tolerance,
          curveSamples,
          out candidatePlane,
          out candidateThickness,
          out candidateFootprint))
          continue;

        var candidateScore = candidateThickness / Math.Max(Math.Sqrt(candidateFootprint), tolerance);
        var bestScore = bestThickness / Math.Max(Math.Sqrt(bestFootprint), tolerance);
        if (bestObject == null || candidateScore < bestScore ||
            (Math.Abs(candidateScore - bestScore) < 1e-9 && candidateFootprint > bestFootprint))
        {
          bestObject = rhinoObject;
          bestPlane = candidatePlane;
          bestThickness = candidateThickness;
          bestFootprint = candidateFootprint;
        }
      }

      if (bestObject == null || bestThickness <= tolerance)
      {
        warning = "未找到具有可测厚度的大平面实体；请确保每块板至少包含一个 Brep、Extrusion 或 Mesh 实体。";
        return false;
      }

      var flatten = Transform.PlaneToPlane(bestPlane, Plane.WorldXY);
      var flatBounds = BoundingBox.Unset;
      foreach (var rhinoObject in objects)
      {
        var duplicate = rhinoObject.Geometry.Duplicate();
        if (duplicate == null || !duplicate.Transform(flatten))
          continue;
        var bounds = duplicate.GetBoundingBox(true);
        if (!bounds.IsValid)
          continue;
        flatBounds = flatBounds.IsValid ? BoundingBox.Union(flatBounds, bounds) : bounds;
      }

      if (!flatBounds.IsValid || flatBounds.Diagonal.X <= tolerance || flatBounds.Diagonal.Y <= tolerance)
      {
        warning = "铺平后的零件边界无效。";
        return false;
      }

      var name = objects
        .Select(item => item.Attributes.Name)
        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
      part = new BoardPart
      {
        Sequence = sequence,
        Name = string.IsNullOrWhiteSpace(name) ? "木板_" + sequence.ToString("000") : name,
        BoardObject = bestObject,
        SourcePlane = bestPlane,
        FlattenTransform = flatten,
        FlatBounds = flatBounds,
        ThicknessModelUnits = bestThickness,
        ThicknessMillimeters = bestThickness / Math.Max(modelUnitsPerMillimeter, 1e-12)
      };
      part.Objects.AddRange(objects);
      return true;
    }

    private static bool TryFindBoardPlane(
      GeometryBase geometry,
      double tolerance,
      IList<Point3d> curveSamples,
      out Plane plane,
      out double thickness,
      out double footprint)
    {
      plane = Plane.Unset;
      thickness = 0.0;
      footprint = 0.0;

      var brep = geometry as Brep;
      var extrusion = geometry as Extrusion;
      if (brep == null && extrusion != null)
        brep = extrusion.ToBrep();
      var surface = geometry as Surface;
      if (brep == null && surface != null)
        brep = surface.ToBrep();
      if (brep != null)
        return TryFindBrepPlane(brep, tolerance, curveSamples, out plane, out thickness, out footprint);

      var mesh = geometry as Mesh;
      if (mesh != null)
        return TryFindMeshPlane(mesh, tolerance, curveSamples, out plane, out thickness, out footprint);
      return false;
    }

    private static bool TryFindBrepPlane(
      Brep brep,
      double tolerance,
      IList<Point3d> curveSamples,
      out Plane bestPlane,
      out double bestThickness,
      out double bestFootprint)
    {
      bestPlane = Plane.Unset;
      bestThickness = double.MaxValue;
      bestFootprint = 0.0;
      var bestCurveDistance = double.MaxValue;
      foreach (var face in brep.Faces)
      {
        Plane facePlane;
        if (!face.TryGetPlane(out facePlane, tolerance))
          continue;
        var box = brep.GetBoundingBox(facePlane);
        if (!box.IsValid)
          continue;
        var width = Math.Abs(box.Max.X - box.Min.X);
        var height = Math.Abs(box.Max.Y - box.Min.Y);
        var depth = Math.Abs(box.Max.Z - box.Min.Z);
        var area = width * height;
        var curveDistance = AverageCurveDistance(facePlane, curveSamples);
        if (width <= tolerance || height <= tolerance || depth <= tolerance)
          continue;
        if (depth < bestThickness - tolerance ||
            (Math.Abs(depth - bestThickness) <= tolerance && curveDistance < bestCurveDistance - tolerance) ||
            (Math.Abs(depth - bestThickness) <= tolerance &&
             Math.Abs(curveDistance - bestCurveDistance) <= tolerance && area > bestFootprint))
        {
          bestPlane = facePlane;
          bestThickness = depth;
          bestFootprint = area;
          bestCurveDistance = curveDistance;
        }
      }
      if (bestPlane.IsValid)
        bestPlane = OrientBoardBehindPlane(bestPlane, brep.GetBoundingBox(bestPlane));
      return bestPlane.IsValid && bestThickness < double.MaxValue;
    }

    private static bool TryFindMeshPlane(
      Mesh mesh,
      double tolerance,
      IList<Point3d> curveSamples,
      out Plane plane,
      out double thickness,
      out double footprint)
    {
      plane = Plane.Unset;
      thickness = 0.0;
      footprint = 0.0;
      if (mesh.Faces.Count == 0)
        return false;

      var bestDepth = double.MaxValue;
      var bestCurveDistance = double.MaxValue;
      var bestArea = 0.0;
      for (var index = 0; index < mesh.Faces.Count; index++)
      {
        var face = mesh.Faces[index];
        var a = (Point3d)mesh.Vertices[face.A];
        var b = (Point3d)mesh.Vertices[face.B];
        var c = (Point3d)mesh.Vertices[face.C];
        var normal = Vector3d.CrossProduct(b - a, c - a);
        var area = 0.5 * normal.Length;
        if (face.IsQuad)
        {
          var d = (Point3d)mesh.Vertices[face.D];
          area += 0.5 * Vector3d.CrossProduct(c - a, d - a).Length;
        }
        if (!normal.Unitize())
          continue;
        var candidatePlane = new Plane(a, normal);
        var candidateBox = mesh.GetBoundingBox(candidatePlane);
        var candidateDepth = Math.Abs(candidateBox.Max.Z - candidateBox.Min.Z);
        var candidateFootprint = Math.Abs(candidateBox.Max.X - candidateBox.Min.X) *
                                 Math.Abs(candidateBox.Max.Y - candidateBox.Min.Y);
        var curveDistance = AverageCurveDistance(candidatePlane, curveSamples);
        if (candidateDepth <= tolerance || candidateFootprint <= tolerance * tolerance)
          continue;
        if (candidateDepth < bestDepth - tolerance ||
            (Math.Abs(candidateDepth - bestDepth) <= tolerance && curveDistance < bestCurveDistance - tolerance) ||
            (Math.Abs(candidateDepth - bestDepth) <= tolerance &&
             Math.Abs(curveDistance - bestCurveDistance) <= tolerance && area > bestArea))
        {
          bestDepth = candidateDepth;
          bestCurveDistance = curveDistance;
          bestArea = area;
          plane = candidatePlane;
          thickness = candidateDepth;
          footprint = candidateFootprint;
        }
      }

      if (!plane.IsValid)
        return false;
      plane = OrientBoardBehindPlane(plane, mesh.GetBoundingBox(plane));
      return thickness > tolerance && footprint > tolerance * tolerance;
    }

    private static List<Point3d> SampleGroupedCurves(IEnumerable<RhinoObject> objects)
    {
      var points = new List<Point3d>();
      foreach (var rhinoObject in objects)
      {
        var curve = rhinoObject.Geometry as Curve;
        if (curve == null || !curve.IsValid)
          continue;
        foreach (var normalizedLength in new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 })
        {
          try
          {
            points.Add(curve.PointAtNormalizedLength(normalizedLength));
          }
          catch
          {
            points.Add(curve.PointAt(curve.Domain.ParameterAt(normalizedLength)));
          }
        }
      }
      return points;
    }

    private static double AverageCurveDistance(Plane plane, IList<Point3d> curveSamples)
    {
      if (curveSamples == null || curveSamples.Count == 0)
        return 0.0;
      return curveSamples.Average(point => Math.Abs(plane.DistanceTo(point)));
    }

    private static Plane OrientBoardBehindPlane(Plane plane, BoundingBox planeAlignedBounds)
    {
      if (!planeAlignedBounds.IsValid)
        return plane;
      var centerZ = (planeAlignedBounds.Min.Z + planeAlignedBounds.Max.Z) * 0.5;
      return centerZ <= 0.0
        ? plane
        : new Plane(plane.Origin, plane.XAxis, -plane.YAxis);
    }

    private static int Find(int[] parent, int index)
    {
      while (parent[index] != index)
      {
        parent[index] = parent[parent[index]];
        index = parent[index];
      }
      return index;
    }

    private static void Union(int[] parent, int left, int right)
    {
      var leftRoot = Find(parent, left);
      var rightRoot = Find(parent, right);
      if (leftRoot != rightRoot)
        parent[rightRoot] = leftRoot;
    }
  }
}
