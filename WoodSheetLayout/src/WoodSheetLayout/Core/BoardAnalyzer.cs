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
      LayoutSettings settings,
      out BoardPart part,
      out string warning)
    {
      part = null;
      warning = null;
      if (doc == null || objects == null || objects.Count == 0)
        return false;

      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, settings.ModelUnitsPerMillimeter * 0.001);
      var annotationSamples = SampleGroupedAnnotations(objects);
      RhinoObject bestObject = null;
      BrepFace bestFace = null;
      var bestPlane = Plane.Unset;
      var bestThickness = double.MaxValue;
      var bestFootprint = 0.0;

      foreach (var rhinoObject in objects)
      {
        Plane candidatePlane;
        BrepFace candidateFace;
        double candidateThickness;
        double candidateFootprint;
        if (!TryFindBoardPlane(
          rhinoObject.Geometry,
          tolerance,
          annotationSamples,
          out candidatePlane,
          out candidateFace,
          out candidateThickness,
          out candidateFootprint))
          continue;

        var candidateScore = candidateThickness / Math.Max(Math.Sqrt(candidateFootprint), tolerance);
        var bestScore = bestThickness / Math.Max(Math.Sqrt(bestFootprint), tolerance);
        if (bestObject == null || candidateScore < bestScore ||
            (Math.Abs(candidateScore - bestScore) < 1e-9 && candidateFootprint > bestFootprint))
        {
          bestObject = rhinoObject;
          bestFace = candidateFace;
          bestPlane = candidatePlane;
          bestThickness = candidateThickness;
          bestFootprint = candidateFootprint;
        }
      }

      // 只有整个实体确实像一张薄平板时才使用刚体放平；弯曲件交给中性层展开器。
      // 对“直面 + 弯曲面”的混合板件，平面段可能很大，但实体沿该平面法向的
      // 总深度会明显大于真实板厚。此时必须优先整体展开，否则会把弯曲段当作
      // 普通平板附属几何一起刚体旋转，公共接缝也就无法保持连续。
      var planarSlenderness = bestObject == null
        ? double.MaxValue
        : bestThickness / Math.Max(Math.Sqrt(bestFootprint), tolerance);
      var preferContinuousUnroll = bestObject != null &&
        BentBoardUnroller.HasBendBeyondThickness(
          bestObject.Geometry,
          bestThickness,
          tolerance,
          settings.ModelUnitsPerMillimeter);

      if (preferContinuousUnroll &&
          BentBoardUnroller.TryCreatePart(doc, objects, sequence, settings, out part, out warning))
        return true;

      if (bestObject != null && bestThickness > tolerance && planarSlenderness <= 0.12 &&
          !preferContinuousUnroll)
      {
        if (TryCreatePlanarPart(
          doc,
          objects,
          sequence,
          settings,
          bestObject,
          bestFace,
          bestPlane,
          bestThickness,
          out part,
          out warning))
          return true;
      }

      if (!preferContinuousUnroll &&
          BentBoardUnroller.TryCreatePart(doc, objects, sequence, settings, out part, out warning))
        return true;

      if (bestObject == null || bestThickness <= tolerance)
        warning = warning ?? "未找到具有可测厚度的平板或可展开折弯板。";
      else
        warning = warning ?? "实体不是薄平板，且中性层展开失败。";
      return false;
    }

    private static bool TryCreatePlanarPart(
      RhinoDoc doc,
      IList<RhinoObject> objects,
      int sequence,
      LayoutSettings settings,
      RhinoObject boardObject,
      BrepFace boardFace,
      Plane sourcePlane,
      double thickness,
      out BoardPart part,
      out string warning)
    {
      part = null;
      warning = null;
      var flatten = Transform.PlaneToPlane(sourcePlane, Plane.WorldXY);
      var textNeedsMirror = TextWouldFaceDown(objects, flatten);
      if (textNeedsMirror)
      {
        var mirror = Transform.Mirror(new Plane(Point3d.Origin, Vector3d.YAxis));
        flatten = mirror * flatten;
      }

      var flatBounds = BoundingBox.Unset;
      var flatItems = new List<FlatGeometryItem>();
      foreach (var rhinoObject in objects)
      {
        var duplicate = rhinoObject.Geometry.Duplicate();
        if (duplicate == null || !duplicate.Transform(flatten))
          continue;
        var bounds = duplicate.GetBoundingBox(true);
        if (bounds.IsValid)
          flatBounds = flatBounds.IsValid ? BoundingBox.Union(flatBounds, bounds) : bounds;
        flatItems.Add(new FlatGeometryItem
        {
          Geometry = duplicate,
          SourceAttributes = rhinoObject.Attributes.Duplicate(),
          SourceObjectId = rhinoObject.Id,
          Name = rhinoObject.Attributes.Name
        });
      }

      if (!flatBounds.IsValid || flatBounds.Diagonal.X <= doc.ModelAbsoluteTolerance ||
          flatBounds.Diagonal.Y <= doc.ModelAbsoluteTolerance)
      {
        warning = "铺平后的零件边界无效。";
        return false;
      }

      PartOutline outline = null;
      if (boardFace != null)
      {
        var faceCopy = boardFace.DuplicateFace(false);
        if (faceCopy != null)
        {
          var edgeCurves = faceCopy.DuplicateNakedEdgeCurves(true, true) ?? new Curve[0];
          foreach (var curve in edgeCurves)
            curve.Transform(flatten);
          outline = OutlineGeometry.Create(edgeCurves, settings.OutlineChordTolerance);
        }
      }
      if (outline == null)
        outline = OutlineGeometry.CreateRectangle(ProjectBoundsToXY(flatBounds));
      if (outline == null)
      {
        warning = "无法提取木板真实外轮廓。";
        return false;
      }

      var name = objects
        .Select(item => item.Attributes.Name)
        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
      part = new BoardPart
      {
        Sequence = sequence,
        Name = string.IsNullOrWhiteSpace(name) ? "木板_" + sequence.ToString("000") : name,
        BoardObject = boardObject,
        SourcePlane = sourcePlane,
        FlattenTransform = flatten,
        FlattenKind = FlattenKind.Planar,
        Outline = outline,
        FlatBounds = outline.Bounds,
        SourceBounds = CombinedBounds(objects),
        ThicknessModelUnits = thickness,
        ThicknessMillimeters = thickness / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12),
        AnnotationSideCorrected = SampleGroupedAnnotations(objects).Count > 0,
        TextMirrorCorrected = textNeedsMirror
      };
      part.Objects.AddRange(objects);
      part.FlatGeometry.AddRange(flatItems);
      if (textNeedsMirror)
        part.Notes.Add("已自动修正文字朝下/镜像方向。");
      return true;
    }

    private static bool TryFindBoardPlane(
      GeometryBase geometry,
      double tolerance,
      IList<Point3d> annotationSamples,
      out Plane plane,
      out BrepFace boardFace,
      out double thickness,
      out double footprint)
    {
      plane = Plane.Unset;
      boardFace = null;
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
        return TryFindBrepPlane(
          brep,
          tolerance,
          annotationSamples,
          out plane,
          out boardFace,
          out thickness,
          out footprint);

      var mesh = geometry as Mesh;
      if (mesh != null)
        return TryFindMeshPlane(mesh, tolerance, annotationSamples, out plane, out thickness, out footprint);
      return false;
    }

    private static bool TryFindBrepPlane(
      Brep brep,
      double tolerance,
      IList<Point3d> annotationSamples,
      out Plane bestPlane,
      out BrepFace bestFace,
      out double bestThickness,
      out double bestFootprint)
    {
      bestPlane = Plane.Unset;
      bestFace = null;
      bestThickness = double.MaxValue;
      bestFootprint = 0.0;
      var bestAnnotationDistance = double.MaxValue;
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
        var annotationDistance = AverageAnnotationDistance(face, annotationSamples);
        if (width <= tolerance || height <= tolerance || depth <= tolerance)
          continue;
        if (depth < bestThickness - tolerance ||
            (Math.Abs(depth - bestThickness) <= tolerance && annotationDistance < bestAnnotationDistance - tolerance) ||
            (Math.Abs(depth - bestThickness) <= tolerance &&
             Math.Abs(annotationDistance - bestAnnotationDistance) <= tolerance && area > bestFootprint))
        {
          bestPlane = facePlane;
          bestFace = face;
          bestThickness = depth;
          bestFootprint = area;
          bestAnnotationDistance = annotationDistance;
        }
      }
      if (bestPlane.IsValid)
        bestPlane = OrientBoardBehindPlane(bestPlane, brep.GetBoundingBox(bestPlane));
      return bestPlane.IsValid && bestThickness < double.MaxValue;
    }

    private static bool TryFindMeshPlane(
      Mesh mesh,
      double tolerance,
      IList<Point3d> annotationSamples,
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
      var bestAnnotationDistance = double.MaxValue;
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
        var annotationDistance = AverageAnnotationDistance(candidatePlane, annotationSamples);
        if (candidateDepth <= tolerance || candidateFootprint <= tolerance * tolerance)
          continue;
        if (candidateDepth < bestDepth - tolerance ||
            (Math.Abs(candidateDepth - bestDepth) <= tolerance && annotationDistance < bestAnnotationDistance - tolerance) ||
            (Math.Abs(candidateDepth - bestDepth) <= tolerance &&
             Math.Abs(annotationDistance - bestAnnotationDistance) <= tolerance && area > bestArea))
        {
          bestDepth = candidateDepth;
          bestAnnotationDistance = annotationDistance;
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

    internal static List<Point3d> SampleGroupedAnnotations(IEnumerable<RhinoObject> objects)
    {
      var points = new List<Point3d>();
      foreach (var rhinoObject in objects)
      {
        var curve = rhinoObject.Geometry as Curve;
        if (curve != null && curve.IsValid)
        {
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

        var text = rhinoObject.Geometry as TextEntity;
        if (text != null)
          points.Add(text.Plane.Origin);
      }
      return points;
    }

    internal static BoundingBox CombinedBounds(IEnumerable<RhinoObject> objects)
    {
      var result = BoundingBox.Unset;
      foreach (var rhinoObject in objects)
      {
        var bounds = rhinoObject.Geometry.GetBoundingBox(true);
        if (!bounds.IsValid)
          continue;
        result = result.IsValid ? BoundingBox.Union(result, bounds) : bounds;
      }
      return result;
    }

    private static bool TextWouldFaceDown(IEnumerable<RhinoObject> objects, Transform flatten)
    {
      foreach (var rhinoObject in objects)
      {
        var text = rhinoObject.Geometry as TextEntity;
        if (text == null)
          continue;
        var duplicate = text.Duplicate() as TextEntity;
        if (duplicate != null && duplicate.Transform(flatten) && duplicate.Plane.ZAxis.Z < -1e-6)
          return true;
      }
      return false;
    }

    private static double AverageAnnotationDistance(BrepFace face, IList<Point3d> samples)
    {
      if (samples == null || samples.Count == 0)
        return 0.0;
      var distances = new List<double>();
      foreach (var point in samples)
      {
        double u;
        double v;
        if (!face.ClosestPoint(point, out u, out v))
          continue;
        distances.Add(point.DistanceTo(face.PointAt(u, v)));
      }
      return distances.Count == 0 ? double.MaxValue : distances.Average();
    }

    private static double AverageAnnotationDistance(Plane plane, IList<Point3d> samples)
    {
      if (samples == null || samples.Count == 0)
        return 0.0;
      return samples.Average(point => Math.Abs(plane.DistanceTo(point)));
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

    private static BoundingBox ProjectBoundsToXY(BoundingBox bounds)
    {
      return new BoundingBox(
        new Point3d(bounds.Min.X, bounds.Min.Y, 0.0),
        new Point3d(bounds.Max.X, bounds.Max.Y, 0.0));
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
