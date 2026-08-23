using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodExport.Core
{
  internal static class BoardExportAnalyzer
  {
    public const string LabelMarkerKey = "WoodExport.Label";
    public const string LabelMarkerValue = "1";

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
        foreach (var groupIndex in objects[index].Attributes.GetGroupList() ?? new int[0])
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
      ExportSettings settings,
      out ExportPart part,
      out string warning)
    {
      part = null;
      warning = null;
      if (doc == null || objects == null || objects.Count == 0)
        return false;

      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, settings.ModelUnitsPerMillimeter * 0.001);
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
            (Math.Abs(candidateScore - bestScore) <= 1e-9 && candidateFootprint > bestFootprint))
        {
          bestObject = rhinoObject;
          bestPlane = candidatePlane;
          bestThickness = candidateThickness;
          bestFootprint = candidateFootprint;
        }
      }

      if (bestObject == null || bestThickness <= tolerance)
      {
        warning = "未找到可测量厚度的板状 Brep、Extrusion、Surface 或 Mesh。";
        return false;
      }

      var flatten = Transform.PlaneToPlane(bestPlane, Plane.WorldXY);
      var flatCurves = new List<ExportCurve>();
      AddBoardFaceCurves(bestObject, bestPlane, flatten, tolerance, flatCurves);
      AddGroupedCurves(objects, flatten, flatCurves);
      flatCurves = RemoveDuplicateCurves(flatCurves, tolerance).ToList();
      if (flatCurves.Count == 0)
      {
        warning = "木板没有可导出的平面轮廓或组内曲线。";
        return false;
      }

      var bounds = CombinedCurveBounds(flatCurves.Select(item => item.Geometry));
      if (!bounds.IsValid || bounds.Diagonal.X <= tolerance || bounds.Diagonal.Y <= tolerance)
      {
        warning = "铺平后的轮廓边界无效。";
        return false;
      }

      var name = objects.Select(item => item.Attributes.Name)
        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
      var layers = objects
        .Select(item => item.Attributes.LayerIndex)
        .Distinct()
        .Where(index => index >= 0 && index < doc.Layers.Count)
        .Select(index => doc.Layers[index].FullPath)
        .Where(item => !string.IsNullOrWhiteSpace(item));

      part = new ExportPart
      {
        Sequence = sequence,
        Name = string.IsNullOrWhiteSpace(name) ? "木板_" + sequence.ToString("000") : name,
        BoardObject = bestObject,
        SourcePlane = bestPlane,
        FlattenTransform = flatten,
        FlatBounds = bounds,
        ThicknessModelUnits = bestThickness,
        ThicknessMillimeters = bestThickness / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12),
        WidthMillimeters = Math.Abs(bounds.Diagonal.X) / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12),
        HeightMillimeters = Math.Abs(bounds.Diagonal.Y) / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12),
        SourceLayers = string.Join(";", layers)
      };
      part.SourceObjects.AddRange(objects);
      part.FlatCurves.AddRange(flatCurves);
      part.ShapeSignature = BuildShapeSignature(part, settings);
      return true;
    }

    public static string BuildShapeSignature(ExportPart part, ExportSettings settings)
    {
      var tolerance = Math.Max(settings.ShapeToleranceMillimeters, 0.001);
      var thickness = Quantize(part.ThicknessMillimeters, settings.ThicknessToleranceMillimeters);
      var width = Math.Min(part.WidthMillimeters, part.HeightMillimeters);
      var height = Math.Max(part.WidthMillimeters, part.HeightMillimeters);
      var features = new List<string>();
      foreach (var item in part.FlatCurves)
      {
        var curve = item.Geometry;
        var length = curve.GetLength() / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12);
        var box = curve.GetBoundingBox(true);
        var curveWidth = Math.Abs(box.Diagonal.X) / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12);
        var curveHeight = Math.Abs(box.Diagonal.Y) / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12);
        var area = 0.0;
        if (curve.IsClosed && curve.IsPlanar())
        {
          try
          {
            using (var properties = AreaMassProperties.Compute(curve))
            {
              if (properties != null)
                area = Math.Abs(properties.Area) /
                       Math.Pow(Math.Max(settings.ModelUnitsPerMillimeter, 1e-12), 2.0);
            }
          }
          catch
          {
            area = 0.0;
          }
        }
        features.Add(string.Format(
          CultureInfo.InvariantCulture,
          "{0}:{1}:{2}:{3}:{4}",
          curve.IsClosed ? 1 : 0,
          Quantize(length, tolerance),
          Quantize(Math.Min(curveWidth, curveHeight), tolerance),
          Quantize(Math.Max(curveWidth, curveHeight), tolerance),
          Quantize(area, tolerance * tolerance)));
      }
      features.Sort(StringComparer.Ordinal);
      return string.Format(
        CultureInfo.InvariantCulture,
        "T{0}|B{1}x{2}|N{3}|{4}",
        thickness,
        Quantize(width, tolerance),
        Quantize(height, tolerance),
        features.Count,
        string.Join(";", features));
    }

    private static void AddBoardFaceCurves(
      RhinoObject boardObject,
      Plane sourcePlane,
      Transform flatten,
      double tolerance,
      ICollection<ExportCurve> destination)
    {
      var brep = boardObject.Geometry as Brep;
      var extrusion = boardObject.Geometry as Extrusion;
      if (brep == null && extrusion != null)
        brep = extrusion.ToBrep();
      var surface = boardObject.Geometry as Surface;
      if (brep == null && surface != null)
        brep = surface.ToBrep();

      if (brep != null)
      {
        BrepFace bestFace = null;
        var bestDistance = double.MaxValue;
        foreach (var face in brep.Faces)
        {
          Plane facePlane;
          if (!face.TryGetPlane(out facePlane, tolerance))
            continue;
          var parallel = Math.Abs(facePlane.Normal * sourcePlane.Normal);
          if (parallel < 0.999)
            continue;
          var distance = Math.Abs(sourcePlane.DistanceTo(facePlane.Origin));
          if (distance < bestDistance)
          {
            bestDistance = distance;
            bestFace = face;
          }
        }

        if (bestFace != null)
        {
          foreach (var loop in bestFace.Loops)
          {
            var curve = loop.To3dCurve();
            if (curve == null || !curve.Transform(flatten))
              continue;
            destination.Add(new ExportCurve
            {
              Geometry = curve,
              Attributes = boardObject.Attributes.Duplicate(),
              IsOutline = true
            });
          }
          return;
        }
      }

      var mesh = boardObject.Geometry as Mesh;
      if (mesh == null)
        return;
      foreach (var polyline in mesh.GetNakedEdges() ?? new Polyline[0])
      {
        var curve = new PolylineCurve(polyline);
        if (!curve.Transform(flatten))
          continue;
        destination.Add(new ExportCurve
        {
          Geometry = curve,
          Attributes = boardObject.Attributes.Duplicate(),
          IsOutline = true
        });
      }
    }

    private static void AddGroupedCurves(
      IEnumerable<RhinoObject> objects,
      Transform flatten,
      ICollection<ExportCurve> destination)
    {
      foreach (var rhinoObject in objects)
      {
        if (rhinoObject.Attributes.GetUserString(LabelMarkerKey) == LabelMarkerValue)
          continue;
        var curve = rhinoObject.Geometry as Curve;
        if (curve == null || !curve.IsValid)
          continue;
        var duplicate = curve.DuplicateCurve();
        if (duplicate == null || !duplicate.Transform(flatten))
          continue;
        destination.Add(new ExportCurve
        {
          Geometry = duplicate,
          Attributes = rhinoObject.Attributes.Duplicate(),
          IsOutline = false
        });
      }
    }

    private static IEnumerable<ExportCurve> RemoveDuplicateCurves(
      IEnumerable<ExportCurve> source,
      double tolerance)
    {
      var seen = new HashSet<string>(StringComparer.Ordinal);
      foreach (var item in source)
      {
        var curve = item.Geometry;
        var box = curve.GetBoundingBox(true);
        var center = box.Center;
        var key = string.Format(
          CultureInfo.InvariantCulture,
          "{0}|{1}|{2}|{3}|{4}|{5}",
          curve.IsClosed ? 1 : 0,
          Quantize(curve.GetLength(), tolerance),
          Quantize(box.Diagonal.X, tolerance),
          Quantize(box.Diagonal.Y, tolerance),
          Quantize(center.X, tolerance),
          Quantize(center.Y, tolerance));
        if (seen.Add(key))
          yield return item;
      }
    }

    private static BoundingBox CombinedCurveBounds(IEnumerable<Curve> curves)
    {
      var bounds = BoundingBox.Unset;
      foreach (var curve in curves)
      {
        var curveBounds = curve.GetBoundingBox(true);
        if (!curveBounds.IsValid)
          continue;
        bounds = bounds.IsValid ? BoundingBox.Union(bounds, curveBounds) : curveBounds;
      }
      return bounds;
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
      return mesh != null &&
             TryFindMeshPlane(mesh, tolerance, curveSamples, out plane, out thickness, out footprint);
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
        var width = Math.Abs(box.Diagonal.X);
        var height = Math.Abs(box.Diagonal.Y);
        var depth = Math.Abs(box.Diagonal.Z);
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
        var faceArea = 0.5 * normal.Length;
        if (!normal.Unitize())
          continue;
        var candidatePlane = new Plane(a, normal);
        var box = mesh.GetBoundingBox(candidatePlane);
        var depth = Math.Abs(box.Diagonal.Z);
        var candidateFootprint = Math.Abs(box.Diagonal.X * box.Diagonal.Y);
        var curveDistance = AverageCurveDistance(candidatePlane, curveSamples);
        if (depth <= tolerance || candidateFootprint <= tolerance * tolerance)
          continue;
        if (depth < bestDepth - tolerance ||
            (Math.Abs(depth - bestDepth) <= tolerance && curveDistance < bestCurveDistance - tolerance) ||
            (Math.Abs(depth - bestDepth) <= tolerance &&
             Math.Abs(curveDistance - bestCurveDistance) <= tolerance && faceArea > bestArea))
        {
          bestDepth = depth;
          bestCurveDistance = curveDistance;
          bestArea = faceArea;
          plane = candidatePlane;
          thickness = depth;
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
        if (rhinoObject.Attributes.GetUserString(LabelMarkerKey) == LabelMarkerValue)
          continue;
        var curve = rhinoObject.Geometry as Curve;
        if (curve == null || !curve.IsValid)
          continue;
        foreach (var parameter in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
          try { points.Add(curve.PointAtNormalizedLength(parameter)); }
          catch { points.Add(curve.PointAt(curve.Domain.ParameterAt(parameter))); }
        }
      }
      return points;
    }

    private static double AverageCurveDistance(Plane plane, IList<Point3d> samples)
    {
      return samples == null || samples.Count == 0
        ? 0.0
        : samples.Average(point => Math.Abs(plane.DistanceTo(point)));
    }

    private static Plane OrientBoardBehindPlane(Plane plane, BoundingBox planeAlignedBounds)
    {
      if (!planeAlignedBounds.IsValid)
        return plane;
      var centerZ = (planeAlignedBounds.Min.Z + planeAlignedBounds.Max.Z) * 0.5;
      return centerZ <= 0.0 ? plane : new Plane(plane.Origin, plane.XAxis, -plane.YAxis);
    }

    private static long Quantize(double value, double tolerance)
    {
      return (long)Math.Round(value / Math.Max(tolerance, 1e-9));
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
