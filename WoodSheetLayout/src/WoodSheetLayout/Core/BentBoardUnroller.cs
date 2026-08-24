using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodSheetLayout.Core
{
  internal static class BentBoardUnroller
  {
    internal static bool HasPrimaryCurvedSurface(
      GeometryBase geometry,
      double tolerance,
      double modelUnitsPerMillimeter)
    {
      var brep = ToBrep(geometry);
      if (brep == null || !brep.IsValid || !brep.IsSolid)
        return false;

      var thickness = EstimateThickness(brep, tolerance, modelUnitsPerMillimeter);
      var diagonal = brep.GetBoundingBox(true).Diagonal.Length;
      if (thickness <= tolerance || diagonal <= tolerance || thickness >= diagonal * 0.25)
        return false;

      var faceAreas = Enumerable.Range(0, brep.Faces.Count)
        .Select(index => new
        {
          Face = brep.Faces[index],
          Area = ComputeArea(brep.Faces[index])
        })
        .ToList();
      var maximumArea = faceAreas.Select(item => item.Area).DefaultIfEmpty(0.0).Max();
      if (maximumArea <= tolerance * tolerance)
        return false;

      return faceAreas.Any(item =>
      {
        Plane ignored;
        return item.Area >= maximumArea * 0.08 && !item.Face.TryGetPlane(out ignored, tolerance);
      });
    }

    internal static bool HasBendBeyondThickness(
      GeometryBase geometry,
      double depthFromPlanarFace,
      double tolerance,
      double modelUnitsPerMillimeter)
    {
      var brep = ToBrep(geometry);
      if (brep == null || !brep.IsValid || !brep.IsSolid)
        return false;
      var thickness = EstimateThickness(brep, tolerance, modelUnitsPerMillimeter);
      if (thickness <= tolerance)
        return false;

      // 普通平板沿主平面法向的总深度≈板厚；混合折弯件会明显超过板厚。
      // 允许25%误差以兼容建模公差、倒角及板材实测厚度。
      var allowance = Math.Max(tolerance * 5.0, thickness * 0.25);
      return depthFromPlanarFace > thickness + allowance;
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
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, settings.ModelUnitsPerMillimeter * 0.001);
      var boardCandidates = objects
        .Select(item => new { Object = item, Brep = ToBrep(item.Geometry) })
        .Where(item => item.Brep != null && item.Brep.IsValid && item.Brep.IsSolid)
        .Select(item => new
        {
          item.Object,
          item.Brep,
          Thickness = EstimateThickness(item.Brep, tolerance, settings.ModelUnitsPerMillimeter)
        })
        .Where(item => item.Thickness > tolerance)
        .OrderBy(item => item.Thickness)
        .ToList();

      if (boardCandidates.Count == 0)
      {
        warning = "折弯件必须是厚度恒定的闭合 Brep/Extrusion 实体。";
        return false;
      }

      var failures = new List<string>();
      foreach (var candidate in boardCandidates)
      {
        var diagonal = candidate.Brep.GetBoundingBox(true).Diagonal.Length;
        if (candidate.Thickness >= diagonal * 0.25)
          continue;

        BoardPart created;
        string failure;
        if (TryUnrollCandidate(
          doc,
          objects,
          sequence,
          settings,
          candidate.Object,
          candidate.Brep,
          candidate.Thickness,
          out created,
          out failure))
        {
          part = created;
          return true;
        }
        if (!string.IsNullOrWhiteSpace(failure))
          failures.Add(failure);
      }

      warning = failures.FirstOrDefault() ?? "未找到可以无变形展开的圆柱、圆锥或连续可展曲面。";
      return false;
    }

    private static bool TryUnrollCandidate(
      RhinoDoc doc,
      IList<RhinoObject> objects,
      int sequence,
      LayoutSettings settings,
      RhinoObject boardObject,
      Brep boardBrep,
      double thickness,
      out BoardPart part,
      out string warning)
    {
      part = null;
      warning = null;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, settings.ModelUnitsPerMillimeter * 0.001);
      var annotations = BoardAnalyzer.SampleGroupedAnnotations(objects);
      var patches = BuildTangentPatches(boardBrep, tolerance, annotations);
      if (patches.Count == 0)
      {
        warning = "折弯实体没有可识别的连续主表面。";
        return false;
      }

      foreach (var patch in patches)
      {
        var seamTolerance = Math.Max(
          tolerance * 20.0,
          thickness * 0.025);
        Brep neutralBrep;
        Dictionary<int, Brep> offsetByFace;
        if (!TryCreateNeutralPatch(
          boardBrep,
          patch.FaceIndices,
          thickness * settings.NeutralFactor,
          tolerance,
          seamTolerance,
          out neutralBrep,
          out offsetByFace))
          continue;
        if (!IsConnectedFaceGraph(neutralBrep))
        {
          warning = "中性层公共接缝未能连接，已停止把各面分开铺平。";
          continue;
        }

        var following = BuildFollowingCurves(
          objects,
          boardObject,
          boardBrep,
          patch.FaceIndices,
          offsetByFace,
          tolerance);
        var unroller = new Unroller(neutralBrep)
        {
          AbsoluteTolerance = tolerance,
          RelativeTolerance = 0.01,
          ExplodeOutput = false
        };
        foreach (var item in following)
          unroller.AddFollowingGeometry(item.Curve);

        Curve[] flatCurves;
        Point3d[] flatPoints;
        TextDot[] flatDots;
        Brep[] flatBreps;
        try
        {
          flatBreps = unroller.PerformUnroll(out flatCurves, out flatPoints, out flatDots);
        }
        catch (Exception exception)
        {
          warning = "中性层展开运算失败：" + exception.Message;
          continue;
        }
        if (flatBreps == null || flatBreps.Length == 0)
          continue;

        Brep connectedFlatPatch;
        if (!TryGetConnectedFlatPatch(flatBreps, seamTolerance, out connectedFlatPatch))
        {
          warning = "直面与弯曲面的公共接缝在展开后断开，已停止输出不连续结果。";
          continue;
        }
        flatBreps = new[] { connectedFlatPatch };

        var sourceArea = ComputeArea(neutralBrep);
        var flatArea = flatBreps.Sum(ComputeArea);
        if (sourceArea > tolerance * tolerance &&
            Math.Abs(flatArea - sourceArea) / sourceArea > 0.02)
        {
          warning = "检测到双曲率或超过2%的展开面积变形，已停止强制摊平。";
          continue;
        }

        Plane flatPlane;
        var planarAnchorFace = FindLargestPlanarFaceIndex(neutralBrep, tolerance);
        if (!TryFindFlatPlane(flatBreps, planarAnchorFace, tolerance, out flatPlane))
        {
          warning = "展开结果没有有效平面。";
          continue;
        }
        var normalize = Transform.PlaneToPlane(flatPlane, Plane.WorldXY);
        foreach (var brep in flatBreps)
          brep.Transform(normalize);
        if (flatCurves != null)
        {
          foreach (var curve in flatCurves)
            curve.Transform(normalize);
        }

        var textNeedsMirror = TextFacesAgainstPatch(objects, boardBrep, patch.FaceIndices);
        if (textNeedsMirror)
        {
          var mirror = Transform.Mirror(new Plane(Point3d.Origin, Vector3d.YAxis));
          foreach (var brep in flatBreps)
            brep.Transform(mirror);
          if (flatCurves != null)
          {
            foreach (var curve in flatCurves)
              curve.Transform(mirror);
          }
        }

        var outlineCurves = flatBreps
          .SelectMany(item => item.DuplicateNakedEdgeCurves(true, true) ?? new Curve[0])
          .ToArray();
        var outline = OutlineGeometry.Create(outlineCurves, settings.OutlineChordTolerance);
        if (outline == null)
        {
          warning = "无法从中性层展开结果提取外轮廓。";
          continue;
        }

        var name = objects
          .Select(item => item.Attributes.Name)
          .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var created = new BoardPart
        {
          Sequence = sequence,
          Name = string.IsNullOrWhiteSpace(name) ? "折弯板_" + sequence.ToString("000") : name,
          BoardObject = boardObject,
          FlattenKind = FlattenKind.DevelopableMidSurface,
          Outline = outline,
          FlatBounds = outline.Bounds,
          SourceBounds = BoardAnalyzer.CombinedBounds(objects),
          ThicknessModelUnits = thickness,
          ThicknessMillimeters = thickness / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12),
          AnnotationSideCorrected = following.Count > 0,
          TextMirrorCorrected = textNeedsMirror
        };
        created.Objects.AddRange(objects);
        AddFlatBoardGeometry(created, boardObject, flatBreps, thickness, tolerance);
        AddFlatFollowingGeometry(created, following, flatCurves, unroller);
        created.Notes.Add(string.Format(
          "折弯件按中性层 K={0:0.###}（厚度×{0:0.###}）展开。",
          settings.NeutralFactor));
        var planarFaceCount = patch.FaceIndices.Count(index =>
        {
          Plane ignored;
          return boardBrep.Faces[index].TryGetPlane(out ignored, tolerance);
        });
        created.Notes.Add(string.Format(
          "识别连续折弯链：{0}个直面＋{1}个折弯面。",
          planarFaceCount,
          patch.FaceIndices.Count - planarFaceCount));
        if (neutralBrep.Faces.Count > 1)
          created.Notes.Add("直面与弯曲面以公共接缝整体展开，铺平后保持原有连接关系。");
        if (textNeedsMirror)
          created.Notes.Add("已自动修正折弯件文字镜像方向。");
        if (following.Any(item => item.FromText))
          created.Notes.Add("折弯面上的文字已转换为可展开的轮廓曲线。");
        part = created;
        return true;
      }

      warning = warning ?? "主表面无法偏移到厚度中间层，或属于不可精确展开的双曲率曲面。";
      return false;
    }

    private static void AddFlatBoardGeometry(
      BoardPart part,
      RhinoObject boardObject,
      IEnumerable<Brep> flatBreps,
      double thickness,
      double tolerance)
    {
      foreach (var flatBrep in flatBreps)
      {
        Brep planarSolid;
        GeometryBase output = TryCreatePlanarBoardSolid(
          flatBrep,
          thickness,
          tolerance,
          out planarSolid)
          ? planarSolid
          : flatBrep;
        part.FlatGeometry.Add(new FlatGeometryItem
        {
          Geometry = output,
          SourceAttributes = boardObject.Attributes.Duplicate(),
          SourceObjectId = boardObject.Id,
          Name = boardObject.Attributes.Name
        });
      }
    }

    private static bool TryCreatePlanarBoardSolid(
      Brep flatPatch,
      double thickness,
      double tolerance,
      out Brep solid)
    {
      solid = null;
      if (flatPatch == null || !flatPatch.IsValid)
        return false;

      // 多段折弯展开后可能仍由多个共面面片组成。先从整体裸边重建
      // 一个带孔洞和卡口的平面板，再从中性层向两侧各偏移半个厚度。
      var nakedEdges = flatPatch.DuplicateNakedEdgeCurves(true, true) ?? new Curve[0];
      var loopTolerance = Math.Max(tolerance * 20.0, 1e-7);
      var joinedLoops = Curve.JoinCurves(nakedEdges, loopTolerance);
      if (joinedLoops == null || joinedLoops.Length == 0)
        return false;

      Brep[] planarBreps;
      try
      {
        planarBreps = Brep.CreatePlanarBreps(joinedLoops, loopTolerance);
      }
      catch
      {
        planarBreps = null;
      }
      if (planarBreps == null || planarBreps.Length == 0)
        return false;

      // CreatePlanarBreps可能同时返回孔洞内部的小面，选择覆盖整块板的最大面。
      var planar = planarBreps
        .Where(item => item != null && item.IsValid && item.Faces.Count > 0)
        .OrderByDescending(ComputeArea)
        .FirstOrDefault();
      if (planar == null)
        return false;

      try
      {
        solid = Brep.CreateFromOffsetFace(
          planar.Faces[0],
          thickness * 0.5,
          tolerance,
          true,
          true);
      }
      catch
      {
        solid = null;
      }
      return solid != null && solid.IsValid && solid.IsSolid;
    }

    private static void AddFlatFollowingGeometry(
      BoardPart part,
      IList<FollowingCurve> following,
      IEnumerable<Curve> flatCurves,
      Unroller unroller)
    {
      if (flatCurves == null)
        return;
      var fallbackIndex = 0;
      foreach (var curve in flatCurves)
      {
        var sourceIndex = -1;
        try
        {
          sourceIndex = unroller.FollowingGeometryIndex(curve);
        }
        catch
        {
          sourceIndex = fallbackIndex;
        }
        fallbackIndex++;
        if (sourceIndex < 0 || sourceIndex >= following.Count)
          sourceIndex = Math.Min(following.Count - 1, Math.Max(0, fallbackIndex - 1));
        if (sourceIndex < 0)
          continue;
        var source = following[sourceIndex];
        part.FlatGeometry.Add(new FlatGeometryItem
        {
          Geometry = curve,
          SourceAttributes = source.Attributes.Duplicate(),
          SourceObjectId = source.SourceObjectId,
          Name = source.Name
        });
      }
    }

    private static List<FollowingCurve> BuildFollowingCurves(
      IEnumerable<RhinoObject> objects,
      RhinoObject boardObject,
      Brep sourceBrep,
      IList<int> faceIndices,
      IDictionary<int, Brep> offsetByFace,
      double tolerance)
    {
      var result = new List<FollowingCurve>();
      foreach (var rhinoObject in objects)
      {
        if (rhinoObject.Id == boardObject.Id)
          continue;
        var curves = new List<Curve>();
        var curve = rhinoObject.Geometry as Curve;
        if (curve != null)
          curves.Add(curve);
        var text = rhinoObject.Geometry as TextEntity;
        if (text != null)
        {
          try
          {
            var exploded = text.Explode();
            if (exploded != null)
              curves.AddRange(exploded.Where(item => item != null));
          }
          catch
          {
            // Rhino版本或字体无法转换时，仅跳过该文字，主板仍可展开。
          }
        }

        foreach (var sourceCurve in curves)
        {
          var faceIndex = FindClosestFace(sourceCurve.PointAtNormalizedLength(0.5), sourceBrep, faceIndices);
          Brep offsetFaceBrep;
          if (faceIndex < 0 || !offsetByFace.TryGetValue(faceIndex, out offsetFaceBrep) ||
              offsetFaceBrep.Faces.Count == 0)
            continue;
          var sourceFace = sourceBrep.Faces[faceIndex];
          Curve pullback;
          try
          {
            pullback = sourceFace.Pullback(sourceCurve, tolerance * 10.0);
          }
          catch
          {
            pullback = null;
          }
          if (pullback == null)
            continue;
          var mapped = offsetFaceBrep.Faces[0].Pushup(pullback, tolerance * 10.0);
          if (mapped == null)
            continue;
          result.Add(new FollowingCurve
          {
            Curve = mapped,
            Attributes = rhinoObject.Attributes.Duplicate(),
            SourceObjectId = rhinoObject.Id,
            Name = rhinoObject.Attributes.Name,
            FromText = text != null
          });
        }
      }
      return result;
    }

    private static bool TryCreateNeutralPatch(
      Brep sourceBrep,
      IList<int> faceIndices,
      double offsetDistance,
      double tolerance,
      double seamTolerance,
      out Brep neutralBrep,
      out Dictionary<int, Brep> offsetByFace)
    {
      neutralBrep = null;
      offsetByFace = new Dictionary<int, Brep>();
      var offsets = new List<Brep>();
      foreach (var faceIndex in faceIndices)
      {
        Brep offset;
        if (!TryOffsetTowardSolid(
          sourceBrep,
          sourceBrep.Faces[faceIndex],
          offsetDistance,
          tolerance,
          out offset))
          continue;
        offsets.Add(offset);
        offsetByFace[faceIndex] = offset;
      }

      // 先把原始直面/弯曲面组成连续皮肤，再整体偏移到中性层。
      // 逐面偏移仍保留给附属曲线的参数映射。
      Brep sourcePatch;
      if (TryBuildSourcePatch(sourceBrep, faceIndices, seamTolerance, out sourcePatch) &&
          TryOffsetPatchTowardSolid(
            sourceBrep,
            sourcePatch,
            offsetDistance,
            tolerance,
            seamTolerance,
            out neutralBrep))
      {
        return true;
      }

      // 整体偏移失败时才退回旧版逐面偏移；回退要求每个面都成功。
      if (offsets.Count != faceIndices.Count)
        return false;
      if (offsets.Count == 1)
      {
        neutralBrep = offsets[0].DuplicateBrep();
        return neutralBrep != null;
      }

      var joined = Brep.JoinBreps(offsets, seamTolerance);
      if (joined == null || joined.Length != 1)
        return false;
      neutralBrep = joined[0];
      return neutralBrep != null && neutralBrep.IsValid;
    }

    private static bool TryBuildSourcePatch(
      Brep sourceBrep,
      IEnumerable<int> faceIndices,
      double tolerance,
      out Brep patch)
    {
      patch = null;
      var faces = faceIndices
        .Distinct()
        .Select(index => sourceBrep.Faces[index].DuplicateFace(false))
        .Where(item => item != null && item.IsValid)
        .ToArray();
      if (faces.Length == 0)
        return false;
      if (faces.Length == 1)
      {
        patch = faces[0];
        return true;
      }

      var joined = Brep.JoinBreps(faces, tolerance);
      if (joined == null || joined.Length != 1)
        return false;
      patch = joined[0];
      return patch != null && patch.IsValid && IsConnectedFaceGraph(patch);
    }

    private static bool TryOffsetPatchTowardSolid(
      Brep sourceSolid,
      Brep sourcePatch,
      double distance,
      double tolerance,
      double seamTolerance,
      out Brep best)
    {
      best = null;
      var bestScore = -1;
      foreach (var extend in new[] { false, true })
      {
        foreach (var signedDistance in new[] { -Math.Abs(distance), Math.Abs(distance) })
        {
          Brep[] candidates;
          Brep[] blends;
          Brep[] walls;
          try
          {
            candidates = Brep.CreateOffsetBrep(
              sourcePatch,
              signedDistance,
              false,
              extend,
              tolerance,
              out blends,
              out walls);
          }
          catch
          {
            candidates = null;
            blends = null;
          }
          if (candidates == null || candidates.Length == 0)
            continue;

          var pieces = candidates
            .Concat(blends ?? new Brep[0])
            .Where(item => item != null && item.IsValid)
            .ToArray();
          if (pieces.Length == 0)
            continue;
          var joined = pieces.Length == 1
            ? pieces
            : Brep.JoinBreps(pieces, seamTolerance);
          if (joined == null || joined.Length != 1 || !IsConnectedFaceGraph(joined[0]))
            continue;

          var score = CountPatchSamplesInside(sourceSolid, joined[0], tolerance);
          if (score <= bestScore)
            continue;
          bestScore = score;
          best = joined[0];
        }
        if (best != null && bestScore > 0)
          break;
      }
      return best != null && best.IsValid && bestScore > 0;
    }

    private static int CountPatchSamplesInside(Brep solid, Brep patch, double tolerance)
    {
      return patch.Faces.Sum(face => CountSamplesInside(solid, face, tolerance));
    }

    private static bool TryOffsetTowardSolid(
      Brep sourceBrep,
      BrepFace face,
      double distance,
      double tolerance,
      out Brep best)
    {
      best = null;
      var bestScore = -1;
      foreach (var signedDistance in new[] { -Math.Abs(distance), Math.Abs(distance) })
      {
        Brep candidate;
        try
        {
          candidate = Brep.CreateFromOffsetFace(face, signedDistance, tolerance, false, false);
        }
        catch
        {
          candidate = null;
        }
        if (candidate == null || !candidate.IsValid || candidate.Faces.Count == 0)
          continue;
        var score = CountSamplesInside(sourceBrep, candidate.Faces[0], tolerance);
        if (score > bestScore)
        {
          bestScore = score;
          best = candidate;
        }
      }
      return best != null && bestScore > 0;
    }

    private static int CountSamplesInside(Brep solid, BrepFace face, double tolerance)
    {
      var score = 0;
      foreach (var uRatio in new[] { 0.25, 0.5, 0.75 })
      {
        foreach (var vRatio in new[] { 0.25, 0.5, 0.75 })
        {
          var point = face.PointAt(face.Domain(0).ParameterAt(uRatio), face.Domain(1).ParameterAt(vRatio));
          try
          {
            if (solid.IsPointInside(point, tolerance * 2.0, false))
              score++;
          }
          catch
          {
            return 1;
          }
        }
      }
      return score;
    }

    private static List<SurfacePatch> BuildTangentPatches(
      Brep brep,
      double tolerance,
      IList<Point3d> annotations)
    {
      var patches = new List<SurfacePatch>();
      var seenKeys = new HashSet<string>(StringComparer.Ordinal);
      var faceAreas = Enumerable.Range(0, brep.Faces.Count)
        .ToDictionary(index => index, index => ComputeArea(brep.Faces[index]));
      var maximumArea = faceAreas.Values.DefaultIfEmpty(0.0).Max();

      foreach (var seed in faceAreas.OrderByDescending(item => item.Value).Select(item => item.Key))
      {
        if (faceAreas[seed] < maximumArea * 0.015)
          continue;
        var patchFaces = CollectTangentFaces(brep, seed, tolerance, faceAreas, maximumArea);
        var key = string.Join(",", patchFaces.OrderBy(item => item));
        if (!seenKeys.Add(key))
          continue;
        patches.Add(new SurfacePatch
        {
          FaceIndices = patchFaces,
          Area = patchFaces.Sum(index => faceAreas[index]),
          AnnotationDistance = AverageDistanceToPatch(brep, patchFaces, annotations)
        });
      }

      if (annotations != null && annotations.Count > 0)
      {
        return patches
          .OrderBy(item => item.AnnotationDistance)
          .ThenByDescending(item => item.Area)
          .ToList();
      }
      return patches.OrderByDescending(item => item.Area).ToList();
    }

    private static List<int> CollectTangentFaces(
      Brep brep,
      int seed,
      double tolerance,
      IDictionary<int, double> faceAreas,
      double maximumArea)
    {
      var collected = new HashSet<int> { seed };
      var queue = new Queue<int>();
      queue.Enqueue(seed);
      while (queue.Count > 0)
      {
        var current = queue.Dequeue();
        foreach (var edgeIndex in brep.Faces[current].AdjacentEdges())
        {
          var edge = brep.Edges[edgeIndex];
          foreach (var adjacent in edge.AdjacentFaces())
          {
            if (adjacent == current || collected.Contains(adjacent) ||
                faceAreas[adjacent] < maximumArea * 0.015)
              continue;
            if (!FacesAreTangent(brep.Faces[current], brep.Faces[adjacent], edge, tolerance))
              continue;
            collected.Add(adjacent);
            queue.Enqueue(adjacent);
          }
        }
      }
      return collected.ToList();
    }

    private static bool FacesAreTangent(BrepFace left, BrepFace right, BrepEdge edge, double tolerance)
    {
      var point = edge.PointAtNormalizedLength(0.5);
      double leftU;
      double leftV;
      double rightU;
      double rightV;
      if (!left.ClosestPoint(point, out leftU, out leftV) ||
          !right.ClosestPoint(point, out rightU, out rightV))
        return false;
      var leftNormal = left.NormalAt(leftU, leftV);
      var rightNormal = right.NormalAt(rightU, rightV);
      if (!leftNormal.Unitize() || !rightNormal.Unitize())
        return false;
      // BrepFace底层参数方向可能相反；几何上切向连续的相邻面有时会
      // 返回相反法线。使用绝对点积，避免蓝色和绿色连续面链被拆开。
      return Math.Abs(Vector3d.Multiply(leftNormal, rightNormal)) >=
             Math.Cos(12.0 * Math.PI / 180.0);
    }

    private static double AverageDistanceToPatch(
      Brep brep,
      IList<int> faceIndices,
      IList<Point3d> samples)
    {
      if (samples == null || samples.Count == 0)
        return 0.0;
      return samples.Average(point => faceIndices.Min(index => DistanceToFace(brep.Faces[index], point)));
    }

    private static double DistanceToFace(BrepFace face, Point3d point)
    {
      double u;
      double v;
      if (!face.ClosestPoint(point, out u, out v))
        return double.MaxValue;
      return point.DistanceTo(face.PointAt(u, v));
    }

    private static int FindClosestFace(Point3d point, Brep brep, IEnumerable<int> faceIndices)
    {
      var best = -1;
      var bestDistance = double.MaxValue;
      foreach (var index in faceIndices)
      {
        var distance = DistanceToFace(brep.Faces[index], point);
        if (distance >= bestDistance)
          continue;
        best = index;
        bestDistance = distance;
      }
      return best;
    }

    private static bool TextFacesAgainstPatch(
      IEnumerable<RhinoObject> objects,
      Brep brep,
      IList<int> faceIndices)
    {
      foreach (var rhinoObject in objects)
      {
        var text = rhinoObject.Geometry as TextEntity;
        if (text == null)
          continue;
        var faceIndex = FindClosestFace(text.Plane.Origin, brep, faceIndices);
        if (faceIndex < 0)
          continue;
        double u;
        double v;
        if (!brep.Faces[faceIndex].ClosestPoint(text.Plane.Origin, out u, out v))
          continue;
        var normal = brep.Faces[faceIndex].NormalAt(u, v);
        if (Vector3d.Multiply(normal, text.Plane.ZAxis) < 0.0)
          return true;
      }
      return false;
    }

    private static bool TryGetConnectedFlatPatch(
      IEnumerable<Brep> flatBreps,
      double tolerance,
      out Brep connected)
    {
      connected = null;
      var pieces = flatBreps
        .Where(item => item != null && item.IsValid)
        .ToArray();
      if (pieces.Length == 0)
        return false;
      if (pieces.Length == 1)
        connected = pieces[0];
      else
      {
        var joined = Brep.JoinBreps(pieces, tolerance * 20.0);
        if (joined == null || joined.Length != 1)
          return false;
        connected = joined[0];
      }
      return connected != null && connected.IsValid && IsConnectedFaceGraph(connected);
    }

    private static bool IsConnectedFaceGraph(Brep brep)
    {
      if (brep == null || !brep.IsValid || brep.Faces.Count == 0)
        return false;
      if (brep.Faces.Count == 1)
        return true;

      var visited = new HashSet<int> { 0 };
      var queue = new Queue<int>();
      queue.Enqueue(0);
      while (queue.Count > 0)
      {
        var current = queue.Dequeue();
        foreach (var edgeIndex in brep.Faces[current].AdjacentEdges())
        {
          foreach (var adjacent in brep.Edges[edgeIndex].AdjacentFaces())
          {
            if (visited.Add(adjacent))
              queue.Enqueue(adjacent);
          }
        }
      }
      return visited.Count == brep.Faces.Count;
    }

    private static int FindLargestPlanarFaceIndex(Brep brep, double tolerance)
    {
      var bestIndex = -1;
      var bestArea = 0.0;
      for (var index = 0; index < brep.Faces.Count; index++)
      {
        Plane ignored;
        if (!brep.Faces[index].TryGetPlane(out ignored, tolerance))
          continue;
        var area = ComputeArea(brep.Faces[index]);
        if (area <= bestArea)
          continue;
        bestArea = area;
        bestIndex = index;
      }
      return bestIndex;
    }

    private static bool TryFindFlatPlane(
      IList<Brep> breps,
      int preferredFaceIndex,
      double tolerance,
      out Plane plane)
    {
      plane = Plane.Unset;
      if (breps != null && breps.Count == 1 && preferredFaceIndex >= 0 &&
          preferredFaceIndex < breps[0].Faces.Count &&
          breps[0].Faces[preferredFaceIndex].TryGetPlane(out plane, tolerance))
        return true;

      foreach (var brep in breps.OrderByDescending(ComputeArea))
      {
        foreach (var face in brep.Faces)
        {
          if (face.TryGetPlane(out plane, tolerance))
            return true;
        }
      }
      return false;
    }

    private static double EstimateThickness(Brep brep, double tolerance, double modelUnitsPerMillimeter)
    {
      var areaProperties = AreaMassProperties.Compute(brep);
      var volumeProperties = VolumeMassProperties.Compute(brep);
      var areaEstimate = areaProperties == null || volumeProperties == null || areaProperties.Area <= tolerance * tolerance
        ? 0.0
        : 2.0 * Math.Abs(volumeProperties.Volume) / areaProperties.Area;

      var diagonal = brep.GetBoundingBox(true).Diagonal.Length;
      var edgeLengths = brep.Edges
        .Select(edge => edge.GetLength())
        .Where(length => length > tolerance * 2.0 && length < diagonal * 0.2)
        .ToList();
      if (edgeLengths.Count == 0)
        return areaEstimate;

      var expected = areaEstimate > tolerance ? areaEstimate : edgeLengths.Min();
      var nearby = edgeLengths
        .Where(length => length >= expected * 0.55 && length <= expected * 1.8)
        .OrderBy(length => length)
        .ToList();
      if (nearby.Count == 0)
        return areaEstimate;

      var toleranceModel = Math.Max(0.2 * modelUnitsPerMillimeter, tolerance * 5.0);
      var clusters = new List<List<double>>();
      foreach (var length in nearby)
      {
        var cluster = clusters.FirstOrDefault(item => Math.Abs(item.Average() - length) <= toleranceModel);
        if (cluster == null)
        {
          cluster = new List<double>();
          clusters.Add(cluster);
        }
        cluster.Add(length);
      }
      var best = clusters.OrderByDescending(item => item.Count).ThenBy(item => Math.Abs(item.Average() - expected)).First();
      return best.Average();
    }

    private static Brep ToBrep(GeometryBase geometry)
    {
      var brep = geometry as Brep;
      if (brep != null)
        return brep.DuplicateBrep();
      var extrusion = geometry as Extrusion;
      if (extrusion != null)
        return extrusion.ToBrep();
      return null;
    }

    private static double ComputeArea(Brep brep)
    {
      var properties = brep == null ? null : AreaMassProperties.Compute(brep);
      return properties == null ? 0.0 : properties.Area;
    }

    private static double ComputeArea(BrepFace face)
    {
      var properties = face == null ? null : AreaMassProperties.Compute(face);
      return properties == null ? 0.0 : properties.Area;
    }

    private sealed class SurfacePatch
    {
      public List<int> FaceIndices { get; set; }
      public double Area { get; set; }
      public double AnnotationDistance { get; set; }
    }

    private sealed class FollowingCurve
    {
      public Curve Curve { get; set; }
      public ObjectAttributes Attributes { get; set; }
      public Guid SourceObjectId { get; set; }
      public string Name { get; set; }
      public bool FromText { get; set; }
    }
  }
}
