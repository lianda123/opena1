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
      out string warning,
      out bool skippedByMode)
    {
      part = null;
      warning = null;
      skippedByMode = false;
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

      // 普通命令严格走1.1快速路径：找到可测厚度的大平面后立即刚体铺平。
      // 不扫描曲面、不估算折弯、不提取真实外轮廓；这些重计算只属于
      // WSLayFlatBend。这样带圆孔、圆角或局部圆柱孔壁的普通板不会被误判跳过。
      if (settings.PartMode == LayoutPartMode.PlanarOnly)
      {
        if (bestObject == null || bestThickness <= tolerance)
        {
          warning = "未找到具有可测厚度的大平面实体；请确认木板为有真实厚度的 Brep、Extrusion 或 Mesh。";
          return false;
        }

        return TryCreatePlanarPart(
          doc,
          objects,
          sequence,
          settings,
          bestObject,
          bestFace,
          bestPlane,
          bestThickness,
          out part,
          out warning);
      }

      // 只有独立折弯命令才执行下面的曲面分析和中性层展开判定。
      var preferContinuousUnroll = bestObject != null &&
        BentBoardUnroller.HasBendBeyondThickness(
          bestObject.Geometry,
          bestThickness,
          tolerance,
          settings.ModelUnitsPerMillimeter);
      var hasPrimaryCurvedSurface = objects.Any(item =>
        BentBoardUnroller.HasPrimaryCurvedSurface(
          item.Geometry,
          tolerance,
          settings.ModelUnitsPerMillimeter));
      var isBentBoard = preferContinuousUnroll || hasPrimaryCurvedSurface;

      if (settings.PartMode == LayoutPartMode.BentOnly)
      {
        if (!isBentBoard)
        {
          skippedByMode = true;
          warning = "检测到普通平板，折弯件命令已跳过。";
          return false;
        }

        return BentBoardUnroller.TryCreatePart(
          doc,
          objects,
          sequence,
          settings,
          out part,
          out warning);
      }

      warning = warning ?? "未找到可以由折弯件命令展开的恒厚板件。";
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

      // 普通平板严格恢复1.1的矩形包围盒骨架，不再计算真实轮廓候选。
      // 包围盒包含木板以及同组刀线、雕刻线和文字，因此排版规整且不会互相覆盖。
      var outline = OutlineGeometry.CreateRectangle(flatBounds);

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
        // 普通排版严格沿用1.1：板件与同组曲线的整体矩形包围盒参与MaxRects。
        FlatBounds = flatBounds,
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

      // STEP/IGES 导入、连续布尔或圆角后的大板面有时视觉上是平面，
      // 但底层NURBS会在文档公差内带有极小起伏，TryGetPlane因此全部失败。
      // 仅当精确候选不存在或明显不像薄板时，才执行较慢的近似平面回退。
      var exactScore = bestPlane.IsValid && bestFootprint > tolerance * tolerance
        ? bestThickness / Math.Max(Math.Sqrt(bestFootprint), tolerance)
        : double.MaxValue;
      if (!bestPlane.IsValid || exactScore > 0.25)
      {
        Plane approximatePlane;
        BrepFace approximateFace;
        double approximateThickness;
        double approximateFootprint;
        if (TryFindApproximateBrepPlane(
          brep,
          tolerance,
          annotationSamples,
          out approximatePlane,
          out approximateFace,
          out approximateThickness,
          out approximateFootprint))
        {
          var approximateScore = approximateThickness /
            Math.Max(Math.Sqrt(approximateFootprint), tolerance);
          if (!bestPlane.IsValid || approximateScore < exactScore)
          {
            bestPlane = approximatePlane;
            bestFace = approximateFace;
            bestThickness = approximateThickness;
            bestFootprint = approximateFootprint;
          }
        }
      }

      if (bestPlane.IsValid)
        bestPlane = OrientBoardBehindPlane(bestPlane, brep.GetBoundingBox(bestPlane));
      return bestPlane.IsValid && bestThickness < double.MaxValue;
    }

    private static bool TryFindApproximateBrepPlane(
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
      bestThickness = 0.0;
      bestFootprint = 0.0;
      var bestScore = double.MaxValue;
      var bestAnnotationDistance = double.MaxValue;
      var diagonal = brep.GetBoundingBox(true).Diagonal.Length;
      var relaxedTolerance = Math.Max(tolerance * 10.0, diagonal * 1e-7);

      foreach (var face in brep.Faces)
      {
        Plane candidatePlane;
        if (!face.TryGetPlane(out candidatePlane, relaxedTolerance))
        {
          var u = face.Domain(0).ParameterAt(0.5);
          var v = face.Domain(1).ParameterAt(0.5);
          if (!face.FrameAt(u, v, out candidatePlane) || !candidatePlane.IsValid)
            continue;
        }

        var box = brep.GetBoundingBox(candidatePlane);
        if (!box.IsValid)
          continue;
        var width = Math.Abs(box.Max.X - box.Min.X);
        var height = Math.Abs(box.Max.Y - box.Min.Y);
        var depth = Math.Abs(box.Max.Z - box.Min.Z);
        var footprint = width * height;
        if (width <= tolerance || height <= tolerance || depth <= tolerance ||
            footprint <= tolerance * tolerance)
          continue;

        var score = depth / Math.Max(Math.Sqrt(footprint), tolerance);
        var annotationDistance = AverageAnnotationDistance(face, annotationSamples);
        if (score < bestScore - 1e-9 ||
            (Math.Abs(score - bestScore) <= 1e-9 &&
             annotationDistance < bestAnnotationDistance - tolerance))
        {
          bestPlane = candidatePlane;
          bestFace = face;
          bestThickness = depth;
          bestFootprint = footprint;
          bestScore = score;
          bestAnnotationDistance = annotationDistance;
        }
      }

      return bestPlane.IsValid && bestThickness > tolerance;
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
