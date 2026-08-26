using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodSheetLayout.Core
{
  internal static class BoardAnalyzer
  {
    private const string OutputRoleKey = "WoodSheetLayoutRole";

    public static List<RhinoObject> ExpandSelectedGroups(
      RhinoDoc doc,
      IEnumerable<RhinoObject> selectedObjects)
    {
      var result = new Dictionary<Guid, RhinoObject>();
      var pending = new Queue<RhinoObject>();
      var visitedGroups = new HashSet<int>();

      foreach (var rhinoObject in selectedObjects ?? Enumerable.Empty<RhinoObject>())
      {
        if (!IsBendInputObject(doc, rhinoObject) || result.ContainsKey(rhinoObject.Id))
          continue;
        result.Add(rhinoObject.Id, rhinoObject);
        pending.Enqueue(rhinoObject);
      }

      // Rhino 的框选或嵌套组选择不一定把交叉组的全部成员返回给 GetObject。
      // 从任意已选成员开始递归补齐原始组，避免一个真实零件被拆成许多曲线碎片。
      while (pending.Count > 0)
      {
        var current = pending.Dequeue();
        foreach (var groupIndex in current.Attributes.GetGroupList() ?? new int[0])
        {
          if (!visitedGroups.Add(groupIndex))
            continue;

          // 配对组同时包含原件和排版副本，不能跨过它把两套几何混成一个
          // 折弯组件。2.2.7旧副本没有WSL_PART内部组；当用户直接点击旧
          // FlatCopy时，只补齐同一配对组内的FlatCopy成员以兼容旧文件。
          if (IsOutputPairGroup(doc, groupIndex))
          {
            if (IsFlatCopyObject(current))
            {
              foreach (var oldFlatMember in doc.Groups.GroupMembers(groupIndex) ?? new RhinoObject[0])
              {
                if (!IsFlatCopyObject(oldFlatMember) || result.ContainsKey(oldFlatMember.Id))
                  continue;
                result.Add(oldFlatMember.Id, oldFlatMember);
                pending.Enqueue(oldFlatMember);
              }
            }
            continue;
          }

          foreach (var member in doc.Groups.GroupMembers(groupIndex) ?? new RhinoObject[0])
          {
            if (!IsBendInputObject(doc, member) || result.ContainsKey(member.Id))
              continue;
            result.Add(member.Id, member);
            pending.Enqueue(member);
          }
        }
      }

      return result.Values.ToList();
    }

    private static bool IsBendInputObject(RhinoDoc doc, RhinoObject rhinoObject)
    {
      if (rhinoObject == null || rhinoObject.Geometry == null)
        return false;
      var role = rhinoObject.Attributes.GetUserString(OutputRoleKey);
      // 折弯命令允许FlatCopy再次作为待展开零件，但板框、统计文字等辅助
      // 输出永远不能进入组件。原件上的Source标记也保持可用。
      if (string.Equals(role, "FlatCopy", StringComparison.Ordinal) ||
          string.Equals(role, "Source", StringComparison.Ordinal))
        return true;
      if (string.Equals(role, "OutputGuide", StringComparison.Ordinal))
        return false;
      if (doc == null || rhinoObject.Attributes.LayerIndex < 0 ||
          rhinoObject.Attributes.LayerIndex >= doc.Layers.Count)
        return true;
      var layer = doc.Layers[rhinoObject.Attributes.LayerIndex];
      return layer == null ||
             !layer.FullPath.StartsWith("WoodSheetLayout_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFlatCopyObject(RhinoObject rhinoObject)
    {
      return rhinoObject != null && rhinoObject.Geometry != null &&
             string.Equals(
               rhinoObject.Attributes.GetUserString(OutputRoleKey),
               "FlatCopy",
               StringComparison.Ordinal);
    }

    public static bool IsGeneratedOutputObject(RhinoDoc doc, RhinoObject rhinoObject)
    {
      if (rhinoObject == null || rhinoObject.Geometry == null)
        return true;
      var role = rhinoObject.Attributes.GetUserString(OutputRoleKey);
      if (string.Equals(role, "FlatCopy", StringComparison.Ordinal) ||
          string.Equals(role, "OutputGuide", StringComparison.Ordinal))
        return true;
      if (doc == null || rhinoObject.Attributes.LayerIndex < 0 ||
          rhinoObject.Attributes.LayerIndex >= doc.Layers.Count)
        return false;
      var layer = doc.Layers[rhinoObject.Attributes.LayerIndex];
      return layer != null && layer.FullPath.StartsWith("WoodSheetLayout_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOutputPairGroup(RhinoDoc doc, int groupIndex)
    {
      if (doc == null || groupIndex < 0 || groupIndex >= doc.Groups.Count)
        return false;
      var groupName = doc.Groups.GroupName(groupIndex);
      return !string.IsNullOrWhiteSpace(groupName) &&
             groupName.StartsWith("WSL_PAIR_", StringComparison.OrdinalIgnoreCase);
    }

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
          settings.PartMode == LayoutPartMode.BentOnly,
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

      // 普通命令先走1.1板件路径；失败后强制从所选对象自身方向铺平，
      // 不再因为“不是木板”或厚度识别失败而直接丢弃该组件。
      if (settings.PartMode == LayoutPartMode.PlanarOnly)
      {
        if (bestObject != null && bestThickness > tolerance &&
            TryCreatePlanarPart(
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
        {
          return true;
        }

        Plane forcedPlane;
        double forcedThickness;
        if (TryFindForcedPlane(doc, objects, settings, tolerance, out forcedPlane, out forcedThickness))
        {
          return TryCreatePlanarPart(
            doc,
            objects,
            sequence,
            settings,
            objects[0],
            null,
            forcedPlane,
            forcedThickness,
            out part,
            out warning);
        }

        warning = "选中组件没有可复制的有效几何，无法生成铺平副本。";
        return false;
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

    private static bool TryFindForcedPlane(
      RhinoDoc doc,
      IList<RhinoObject> objects,
      LayoutSettings settings,
      double tolerance,
      out Plane bestPlane,
      out double thickness)
    {
      bestPlane = Plane.Unset;
      thickness = 0.0;
      var candidates = new List<Plane>
      {
        Plane.WorldXY,
        new Plane(Point3d.Origin, Vector3d.YAxis, Vector3d.ZAxis),
        new Plane(Point3d.Origin, Vector3d.ZAxis, Vector3d.XAxis)
      };

      foreach (var rhinoObject in objects)
      {
        var geometry = rhinoObject.Geometry;
        var curve = geometry as Curve;
        if (curve != null)
        {
          Plane curvePlane;
          if (curve.TryGetPlane(out curvePlane, tolerance * 10.0))
            AddPlaneCandidate(candidates, curvePlane);
        }

        var text = geometry as TextEntity;
        if (text != null)
          AddPlaneCandidate(candidates, text.Plane);

        var instance = geometry as InstanceReferenceGeometry;
        if (instance != null)
        {
          var instancePlane = Plane.WorldXY;
          if (instancePlane.Transform(instance.Xform))
            AddPlaneCandidate(candidates, instancePlane);
        }

        var brep = geometry as Brep;
        var extrusion = geometry as Extrusion;
        if (brep == null && extrusion != null)
          brep = extrusion.ToBrep();
        var surface = geometry as Surface;
        if (brep == null && surface != null)
          brep = surface.ToBrep();
        if (brep != null)
          AddBrepPlaneCandidates(candidates, brep, tolerance);

        var mesh = geometry as Mesh;
        if (mesh != null)
          AddMeshPlaneCandidates(candidates, mesh);
      }

      var bestScore = double.MaxValue;
      var bestArea = 0.0;
      var bestDepth = 0.0;
      foreach (var candidate in candidates)
      {
        var bounds = BoundingBox.Unset;
        foreach (var rhinoObject in objects)
        {
          var itemBounds = rhinoObject.Geometry.GetBoundingBox(candidate);
          if (!itemBounds.IsValid)
            continue;
          bounds = bounds.IsValid ? BoundingBox.Union(bounds, itemBounds) : itemBounds;
        }
        if (!bounds.IsValid)
          continue;

        var width = Math.Abs(bounds.Max.X - bounds.Min.X);
        var height = Math.Abs(bounds.Max.Y - bounds.Min.Y);
        var depth = Math.Abs(bounds.Max.Z - bounds.Min.Z);
        var safeWidth = Math.Max(width, tolerance);
        var safeHeight = Math.Max(height, tolerance);
        var area = safeWidth * safeHeight;
        var score = depth / Math.Max(Math.Sqrt(area), tolerance);
        if (score < bestScore - 1e-9 ||
            (Math.Abs(score - bestScore) <= 1e-9 && area > bestArea))
        {
          bestPlane = candidate;
          bestScore = score;
          bestArea = area;
          bestDepth = depth;
        }
      }

      if (!bestPlane.IsValid)
        return false;

      double layerThicknessMillimeters;
      if (TryReadThicknessFromLayer(doc, objects, out layerThicknessMillimeters))
        thickness = layerThicknessMillimeters * settings.ModelUnitsPerMillimeter;
      else
        thickness = bestDepth;
      return true;
    }

    private static void AddBrepPlaneCandidates(List<Plane> candidates, Brep brep, double tolerance)
    {
      var relaxedTolerance = Math.Max(tolerance * 10.0, brep.GetBoundingBox(true).Diagonal.Length * 1e-7);
      foreach (var face in brep.Faces)
      {
        Plane plane;
        if (!face.TryGetPlane(out plane, relaxedTolerance))
        {
          var u = face.Domain(0).ParameterAt(0.5);
          var v = face.Domain(1).ParameterAt(0.5);
          if (!face.FrameAt(u, v, out plane))
            continue;
        }
        AddPlaneCandidate(candidates, plane);
      }
    }

    private static void AddMeshPlaneCandidates(List<Plane> candidates, Mesh mesh)
    {
      if (mesh.Faces.Count == 0)
        return;
      var step = Math.Max(1, mesh.Faces.Count / 64);
      for (var index = 0; index < mesh.Faces.Count; index += step)
      {
        var face = mesh.Faces[index];
        var a = (Point3d)mesh.Vertices[face.A];
        var b = (Point3d)mesh.Vertices[face.B];
        var c = (Point3d)mesh.Vertices[face.C];
        var xAxis = b - a;
        var normal = Vector3d.CrossProduct(xAxis, c - a);
        if (!xAxis.Unitize() || !normal.Unitize())
          continue;
        var yAxis = Vector3d.CrossProduct(normal, xAxis);
        if (!yAxis.Unitize())
          continue;
        AddPlaneCandidate(candidates, new Plane(a, xAxis, yAxis));
      }
    }

    private static void AddPlaneCandidate(List<Plane> candidates, Plane plane)
    {
      if (!plane.IsValid || candidates.Count >= 256)
        return;
      candidates.Add(plane);
    }

    private static bool TryReadThicknessFromLayer(
      RhinoDoc doc,
      IEnumerable<RhinoObject> objects,
      out double thicknessMillimeters)
    {
      thicknessMillimeters = 0.0;
      var expression = new Regex(
        @"(?<![0-9])([0-9]+(?:\.[0-9]+)?)\s*mm",
        RegexOptions.IgnoreCase);
      foreach (var rhinoObject in objects)
      {
        var layerIndex = rhinoObject.Attributes.LayerIndex;
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
          continue;
        var layer = doc.Layers[layerIndex];
        var match = expression.Match(layer == null ? string.Empty : layer.FullPath);
        double parsed;
        if (match.Success &&
            double.TryParse(
              match.Groups[1].Value,
              NumberStyles.Float,
              CultureInfo.InvariantCulture,
              out parsed) &&
            parsed > 0.0)
        {
          thicknessMillimeters = parsed;
          return true;
        }
      }
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

      if (!flatBounds.IsValid)
      {
        warning = "铺平后的零件边界无效。";
        return false;
      }

      // 线、点或单排文字也必须进入边界框；仅放大排版占位矩形，
      // 不缩放、不修改实际输出几何。
      var minimumSize = Math.Max(doc.ModelAbsoluteTolerance * 2.0, settings.ModelUnitsPerMillimeter * 0.1);
      var minimum = flatBounds.Min;
      var maximum = flatBounds.Max;
      if (maximum.X - minimum.X < minimumSize)
      {
        var centerX = (minimum.X + maximum.X) * 0.5;
        minimum.X = centerX - minimumSize * 0.5;
        maximum.X = centerX + minimumSize * 0.5;
      }
      if (maximum.Y - minimum.Y < minimumSize)
      {
        var centerY = (minimum.Y + maximum.Y) * 0.5;
        minimum.Y = centerY - minimumSize * 0.5;
        maximum.Y = centerY + minimumSize * 0.5;
      }
      flatBounds = new BoundingBox(minimum, maximum);

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
      bool allowApproximatePlane,
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
          allowApproximatePlane,
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
      bool allowApproximatePlane,
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
        // 保持1.1行为：用标注曲线采样点到候选平面的距离选木板正面。
        var annotationDistance = AverageAnnotationDistance(facePlane, annotationSamples);
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

      // 普通排版到这里即停止，保持 1.1.0 对真实平面的原始判定顺序。
      // 只有独立折弯命令允许执行较慢的近似平面回退。
      var exactScore = bestPlane.IsValid && bestFootprint > tolerance * tolerance
        ? bestThickness / Math.Max(Math.Sqrt(bestFootprint), tolerance)
        : double.MaxValue;
      if (allowApproximatePlane && (!bestPlane.IsValid || exactScore > 0.25))
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
