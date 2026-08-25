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

        int sourceHoleLoopCount;
        int mappedHoleLoopCount;
        var boundaryFollowing = BuildBoundaryFollowingCurves(
          boardObject,
          boardBrep,
          patch.FaceIndices,
          offsetByFace,
          tolerance,
          out sourceHoleLoopCount,
          out mappedHoleLoopCount);
        if (mappedHoleLoopCount < sourceHoleLoopCount)
        {
          warning = string.Format(
            "卡扣孔/镂空提取不完整：原主表面有{0}个内环，仅{1}个完整映射到中性层；已停止输出缺孔零件。",
            sourceHoleLoopCount,
            mappedHoleLoopCount);
          return false;
        }
        var annotationFollowing = BuildFollowingCurves(
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
        // 主展开只携带用户的压痕、图案和文字。板材边界另做一次独立
        // 随动展开，避免边界碎片挤乱FollowingGeometryIndex；孔洞失败
        // 也不能再让算法跳到狭窄侧面并输出2.5mm细条。
        foreach (var item in annotationFollowing)
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

        var matchedFollowing = MatchFlatFollowingGeometry(
          annotationFollowing,
          flatCurves,
          unroller);
        Curve[] flatBoardBoundaries;
        if (!TryUnrollBoundaryCurves(
          neutralBrep,
          boundaryFollowing,
          tolerance,
          normalize,
          textNeedsMirror,
          out flatBoardBoundaries))
        {
          warning = "卡口/镂空边界无法随中性层完整展开，已保留原模型且不输出错误细条。";
          return false;
        }
        var expectedBoundaryLoopCount = JoinClosedLoops(
          boundaryFollowing.Select(item => item.Curve),
          seamTolerance).Count;
        var flatBoundaryLoopCount = JoinClosedLoops(
          flatBoardBoundaries,
          seamTolerance).Count;
        if (expectedBoundaryLoopCount > 0 &&
            flatBoundaryLoopCount < expectedBoundaryLoopCount)
        {
          warning = string.Format(
            "卡口/镂空边界展开不完整：原中性层{0}个闭合环，展开后仅{1}个，已停止输出缺口零件。",
            expectedBoundaryLoopCount,
            flatBoundaryLoopCount);
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
          SourceBounds = BoardAnalyzer.CombinedBounds(objects),
          ThicknessModelUnits = thickness,
          ThicknessMillimeters = thickness / Math.Max(settings.ModelUnitsPerMillimeter, 1e-12),
          AnnotationSideCorrected = annotationFollowing.Count > 0,
          TextMirrorCorrected = textNeedsMirror
        };
        created.Objects.AddRange(objects);
        int rebuiltHoleCount;
        if (!AddFlatBoardGeometry(
          created,
          boardObject,
          flatBreps,
          flatBoardBoundaries,
          sourceHoleLoopCount,
          thickness,
          tolerance,
          out rebuiltHoleCount))
        {
          warning = "主表面已成功铺平，但卡口/镂空无法重建为真实内孔；已保留原模型且不再尝试错误侧面。";
          return false;
        }
        AddFlatFollowingGeometry(created, matchedFollowing, thickness);
        var actualFlatBounds = CombinedFlatGeometryBounds(created.FlatGeometry);
        var outline = OutlineGeometry.CreateRectangle(actualFlatBounds);
        if (outline == null)
        {
          warning = "铺平实体已生成，但无法读取实际输出几何的完整占位范围。";
          return false;
        }
        created.FlatBounds = actualFlatBounds;
        created.Outline = outline;
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
        if (flatBoardBoundaries.Length > 0)
          created.Notes.Add(string.Format(
            "卡口、镂空及开槽已重建：提取{0}条边界，生成{1}个闭合轮廓环，其中{2}个实体内孔。",
            boundaryFollowing.Count,
            flatBoundaryLoopCount,
            rebuiltHoleCount));
        if (annotationFollowing.Count > 0)
          created.Notes.Add("附属曲线已从中性层抬升到铺平实体的上表面。");
        created.Notes.Add("排版占位由最终铺平实体和上表面曲线的完整包围盒反算，确保全部几何位于边界框内。");
        if (textNeedsMirror)
          created.Notes.Add("已自动修正折弯件文字镜像方向。");
        if (annotationFollowing.Any(item => item.FromText))
          created.Notes.Add("折弯面上的文字已转换为可展开的轮廓曲线。");
        part = created;
        return true;
      }

      warning = warning ?? "主表面无法偏移到厚度中间层，或属于不可精确展开的双曲率曲面。";
      return false;
    }

    private static bool TryUnrollBoundaryCurves(
      Brep neutralBrep,
      IList<FollowingCurve> boundaryFollowing,
      double tolerance,
      Transform normalize,
      bool mirrorOutput,
      out Curve[] flatBoundaryCurves)
    {
      flatBoundaryCurves = new Curve[0];
      if (boundaryFollowing == null || boundaryFollowing.Count == 0)
        return true;

      var unroller = new Unroller(neutralBrep)
      {
        AbsoluteTolerance = tolerance,
        RelativeTolerance = 0.01,
        ExplodeOutput = false
      };
      foreach (var item in boundaryFollowing)
        unroller.AddFollowingGeometry(item.Curve);

      Curve[] curves;
      Point3d[] points;
      TextDot[] dots;
      try
      {
        unroller.PerformUnroll(out curves, out points, out dots);
      }
      catch
      {
        return false;
      }
      if (curves == null || curves.Length == 0)
        return false;

      var mirror = Transform.Mirror(new Plane(Point3d.Origin, Vector3d.YAxis));
      foreach (var curve in curves.Where(item => item != null && item.IsValid))
      {
        curve.Transform(normalize);
        if (mirrorOutput)
          curve.Transform(mirror);
      }
      flatBoundaryCurves = curves
        .Where(item => item != null && item.IsValid)
        .ToArray();
      return flatBoundaryCurves.Length > 0;
    }

    private static bool AddFlatBoardGeometry(
      BoardPart part,
      RhinoObject boardObject,
      IEnumerable<Brep> flatBreps,
      IEnumerable<Curve> flatBoardBoundaries,
      int expectedHoleCount,
      double thickness,
      double tolerance,
      out int rebuiltHoleCount)
    {
      rebuiltHoleCount = 0;
      var added = false;
      foreach (var flatBrep in flatBreps)
      {
        Brep planarSolid;
        if (!TryCreatePlanarBoardSolid(
          flatBrep,
          flatBoardBoundaries,
          expectedHoleCount,
          thickness,
          tolerance,
          out planarSolid,
          out rebuiltHoleCount))
          return false;
        part.FlatGeometry.Add(new FlatGeometryItem
        {
          Geometry = planarSolid,
          SourceAttributes = boardObject.Attributes.Duplicate(),
          SourceObjectId = boardObject.Id,
          Name = boardObject.Attributes.Name
        });
        added = true;
      }
      return added;
    }

    private static bool TryCreatePlanarBoardSolid(
      Brep flatPatch,
      IEnumerable<Curve> flatBoardBoundaries,
      int expectedHoleCount,
      double thickness,
      double tolerance,
      out Brep solid,
      out int rebuiltHoleCount)
    {
      solid = null;
      rebuiltHoleCount = 0;
      if (flatPatch == null || !flatPatch.IsValid)
        return false;

      // 多段折弯展开后可能仍由多个共面面片组成。先从整体裸边重建
      // 一个带孔洞和卡口的平面板，再从中性层向两侧各偏移半个厚度。
      var loopTolerance = Math.Max(tolerance * 20.0, 1e-7);
      var patchLoops = JoinClosedLoops(
        flatPatch.DuplicateNakedEdgeCurves(true, true) ?? new Curve[0],
        loopTolerance);
      if (patchLoops.Count == 0)
        return false;

      // 2.2.1按逐边随动展开可以稳定得到完整板形；2.2.2把完整内环
      // 混进主Unroller后会跳到侧面。现在恢复逐边边界，并在独立Unroller
      // 中把碎边重新Join为“最大外环＋其内部的孔环”。
      var mappedLoops = JoinClosedLoops(flatBoardBoundaries, loopTolerance);
      var patchOuter = patchLoops
        .OrderByDescending(CurvePlanarArea)
        .ThenByDescending(CurveEnvelopeArea)
        .First();

      // 2.2.3错误地只要存在任意闭合随动环，就把其中面积最大的环
      // 当成整块板。zhewan4中真正的板外环未能Join，而一个约
      // 145.63×42.84mm的内部卡槽成功闭合，于是卡槽被拉成了整板。
      // 继续以2.2.1展开面片的最大裸边环作为尺寸基准；只有随动环与
      // 该基准四周范围一致时，才允许它替换外环并带回开口卡槽细节。
      var mappedOuter = mappedLoops
        .Where(item => CurvesShareExtents(item, patchOuter, loopTolerance))
        .OrderByDescending(CurvePlanarArea)
        .ThenByDescending(CurveEnvelopeArea)
        .FirstOrDefault();
      var outerLoop = mappedOuter ?? patchOuter;
      var holeLoops = mappedLoops
        .Where(item => !ReferenceEquals(item, mappedOuter))
        .Where(item => !CurvesShareExtents(item, outerLoop, loopTolerance))
        .Where(item => CurveIsInside(item, outerLoop, loopTolerance))
        .OrderByDescending(CurvePlanarArea)
        .ToList();
      if (holeLoops.Count < expectedHoleCount)
        return false;

      // 一次性建立带修剪环的平面Brep，并按“外轮廓范围匹配＋内环最多”
      // 选择结果，不能再像2.2.1那样只按最大面积选到填满孔洞的面。
      Brep[] planarBreps;
      try
      {
        planarBreps = Brep.CreatePlanarBreps(
          new[] { outerLoop }.Concat(holeLoops),
          loopTolerance);
      }
      catch
      {
        planarBreps = null;
      }
      var planar = (planarBreps ?? new Brep[0])
        .Where(item => item != null && item.IsValid && item.Faces.Count > 0)
        .Where(item => BrepSharesExtents(item, outerLoop, loopTolerance))
        .OrderByDescending(item => CountMaximumPlanarFaceInnerLoops(item, loopTolerance))
        .ThenByDescending(ComputeArea)
        .FirstOrDefault();
      if (planar != null &&
          CountMaximumPlanarFaceInnerLoops(planar, loopTolerance) >= Math.Max(holeLoops.Count, expectedHoleCount) &&
          TryCreateSymmetricSolid(planar, thickness, tolerance, out solid) &&
          BrepSharesExtents(solid, patchOuter, loopTolerance))
      {
        rebuiltHoleCount = CountMaximumPlanarFaceInnerLoops(solid, loopTolerance);
        if (rebuiltHoleCount >= Math.Max(holeLoops.Count, expectedHoleCount))
          return true;
      }

      // 某些Rhino 7修剪方向会让CreatePlanarBreps返回独立填充面。
      // 仅在这种情况下，以已经确认的最大外环建立整板，再逐孔布尔差。
      var rebuilt = TryBooleanRebuildBoard(
        outerLoop,
        holeLoops,
        thickness,
        loopTolerance,
        out solid,
        out rebuiltHoleCount);
      return rebuilt &&
             rebuiltHoleCount >= expectedHoleCount &&
             BrepSharesExtents(solid, patchOuter, loopTolerance);
    }

    private static bool TryCreateSymmetricSolid(
      Brep planar,
      double thickness,
      double tolerance,
      out Brep solid)
    {
      solid = null;
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

    private static bool TryBooleanRebuildBoard(
      Curve outerLoop,
      IList<Curve> holeLoops,
      double thickness,
      double tolerance,
      out Brep solid,
      out int rebuiltHoleCount)
    {
      solid = null;
      rebuiltHoleCount = 0;
      Brep[] outerPlanarBreps;
      try
      {
        outerPlanarBreps = Brep.CreatePlanarBreps(new[] { outerLoop }, tolerance);
      }
      catch
      {
        outerPlanarBreps = null;
      }
      var outerPlanar = (outerPlanarBreps ?? new Brep[0])
        .Where(item => item != null && item.IsValid && item.Faces.Count > 0)
        .OrderByDescending(ComputeArea)
        .FirstOrDefault();
      if (outerPlanar == null ||
          !TryCreateSymmetricSolid(outerPlanar, thickness, tolerance, out solid))
        return false;

      foreach (var holeLoop in holeLoops)
      {
        Brep cutter;
        if (!TryCreateThroughCutter(holeLoop, thickness, tolerance, out cutter))
          return false;
        Brep[] difference;
        try
        {
          difference = Brep.CreateBooleanDifference(solid, cutter, tolerance);
        }
        catch
        {
          difference = null;
        }
        var next = (difference ?? new Brep[0])
          .Where(item => item != null && item.IsValid && item.IsSolid)
          .OrderByDescending(ComputeVolume)
          .FirstOrDefault();
        if (next == null)
          return false;
        solid = next;
      }

      rebuiltHoleCount = CountMaximumPlanarFaceInnerLoops(solid, tolerance);
      return rebuiltHoleCount >= holeLoops.Count;
    }

    private static bool TryCreateThroughCutter(
      Curve holeLoop,
      double thickness,
      double tolerance,
      out Brep cutter)
    {
      cutter = null;
      Brep[] planarBreps;
      try
      {
        planarBreps = Brep.CreatePlanarBreps(new[] { holeLoop }, tolerance);
      }
      catch
      {
        planarBreps = null;
      }
      var planar = (planarBreps ?? new Brep[0])
        .Where(item => item != null && item.IsValid && item.Faces.Count > 0)
        .OrderByDescending(ComputeArea)
        .FirstOrDefault();
      if (planar == null)
        return false;
      try
      {
        cutter = Brep.CreateFromOffsetFace(
          planar.Faces[0],
          thickness,
          tolerance,
          true,
          true);
      }
      catch
      {
        cutter = null;
      }
      return cutter != null && cutter.IsValid && cutter.IsSolid;
    }

    private static int CountMaximumPlanarFaceInnerLoops(Brep brep, double tolerance)
    {
      if (brep == null || !brep.IsValid)
        return 0;
      var maximum = 0;
      foreach (var face in brep.Faces)
      {
        Plane ignored;
        if (!face.TryGetPlane(out ignored, tolerance))
          continue;
        maximum = Math.Max(maximum, Math.Max(0, face.Loops.Count - 1));
      }
      return maximum;
    }

    private static List<Curve> JoinClosedLoops(
      IEnumerable<Curve> curves,
      double tolerance)
    {
      var source = (curves ?? Enumerable.Empty<Curve>())
        .Where(item => item != null && item.IsValid)
        .Select(item => item.DuplicateCurve())
        .ToArray();
      if (source.Length == 0)
        return new List<Curve>();
      var joined = Curve.JoinCurves(source, tolerance) ?? new Curve[0];
      return joined
        .Where(item => item != null && item.IsValid &&
          (item.IsClosed || item.PointAtStart.DistanceTo(item.PointAtEnd) <= tolerance))
        .ToList();
    }

    private static double CurveEnvelopeArea(Curve curve)
    {
      if (curve == null)
        return 0.0;
      var bounds = curve.GetBoundingBox(true);
      return Math.Abs(bounds.Diagonal.X * bounds.Diagonal.Y);
    }

    private static double CurvePlanarArea(Curve curve)
    {
      if (curve == null || !curve.IsClosed)
        return 0.0;
      try
      {
        var properties = AreaMassProperties.Compute(curve);
        return properties == null ? 0.0 : Math.Abs(properties.Area);
      }
      catch
      {
        return CurveEnvelopeArea(curve);
      }
    }

    private static bool CurveIsInside(
      Curve candidate,
      Curve outer,
      double tolerance)
    {
      if (candidate == null || outer == null || !candidate.IsClosed || !outer.IsClosed)
        return false;
      var candidateBounds = candidate.GetBoundingBox(true);
      var outerBounds = outer.GetBoundingBox(true);
      var allowance = Math.Max(
        tolerance * 2.0,
        outerBounds.Diagonal.Length * 0.0025);
      if (candidateBounds.Min.X < outerBounds.Min.X - allowance ||
          candidateBounds.Min.Y < outerBounds.Min.Y - allowance ||
          candidateBounds.Max.X > outerBounds.Max.X + allowance ||
          candidateBounds.Max.Y > outerBounds.Max.Y + allowance)
        return false;
      Point3d sample;
      try
      {
        var properties = AreaMassProperties.Compute(candidate);
        sample = properties == null
          ? candidate.PointAtNormalizedLength(0.5)
          : properties.Centroid;
      }
      catch
      {
        sample = candidate.PointAtNormalizedLength(0.5);
      }
      try
      {
        var containment = outer.Contains(sample, Plane.WorldXY, tolerance);
        return containment == PointContainment.Inside ||
               containment == PointContainment.Coincident;
      }
      catch
      {
        return false;
      }
    }

    private static bool BrepSharesExtents(
      Brep candidate,
      Curve reference,
      double tolerance)
    {
      if (candidate == null || reference == null)
        return false;
      var candidateBounds = candidate.GetBoundingBox(true);
      var referenceBounds = reference.GetBoundingBox(true);
      var allowance = Math.Max(
        tolerance * 2.0,
        referenceBounds.Diagonal.Length * 0.01);
      return Math.Abs(candidateBounds.Min.X - referenceBounds.Min.X) <= allowance &&
             Math.Abs(candidateBounds.Min.Y - referenceBounds.Min.Y) <= allowance &&
             Math.Abs(candidateBounds.Max.X - referenceBounds.Max.X) <= allowance &&
             Math.Abs(candidateBounds.Max.Y - referenceBounds.Max.Y) <= allowance;
    }

    private static bool CurvesShareExtents(
      Curve candidate,
      Curve reference,
      double tolerance)
    {
      if (candidate == null || reference == null)
        return false;
      var candidateBounds = candidate.GetBoundingBox(true);
      var referenceBounds = reference.GetBoundingBox(true);
      var allowance = Math.Max(
        tolerance * 2.0,
        referenceBounds.Diagonal.Length * 0.01);
      return Math.Abs(candidateBounds.Min.X - referenceBounds.Min.X) <= allowance &&
             Math.Abs(candidateBounds.Min.Y - referenceBounds.Min.Y) <= allowance &&
             Math.Abs(candidateBounds.Max.X - referenceBounds.Max.X) <= allowance &&
             Math.Abs(candidateBounds.Max.Y - referenceBounds.Max.Y) <= allowance;
    }

    private static double ComputeVolume(Brep brep)
    {
      if (brep == null || !brep.IsValid)
        return 0.0;
      try
      {
        var properties = VolumeMassProperties.Compute(brep);
        return properties == null ? 0.0 : Math.Abs(properties.Volume);
      }
      catch
      {
        return 0.0;
      }
    }

    private static void AddFlatFollowingGeometry(
      BoardPart part,
      IEnumerable<FlatFollowingCurve> matchedFollowing,
      double thickness)
    {
      foreach (var item in matchedFollowing.Where(item => !item.Source.IsBoardBoundary))
      {
        var curve = item.Curve.DuplicateCurve();
        if (curve == null)
          continue;
        curve.Transform(Transform.Translation(0.0, 0.0, thickness * 0.5));
        var source = item.Source;
        part.FlatGeometry.Add(new FlatGeometryItem
        {
          Geometry = curve,
          SourceAttributes = source.Attributes.Duplicate(),
          SourceObjectId = source.SourceObjectId,
          Name = source.Name
        });
      }
    }

    private static BoundingBox CombinedFlatGeometryBounds(
      IEnumerable<FlatGeometryItem> flatGeometry)
    {
      var result = BoundingBox.Unset;
      foreach (var item in flatGeometry ?? Enumerable.Empty<FlatGeometryItem>())
      {
        if (item == null || item.Geometry == null || !item.Geometry.IsValid)
          continue;
        var bounds = item.Geometry.GetBoundingBox(true);
        if (!bounds.IsValid)
          continue;
        result = result.IsValid ? BoundingBox.Union(result, bounds) : bounds;
      }
      return result;
    }

    private static List<FlatFollowingCurve> MatchFlatFollowingGeometry(
      IList<FollowingCurve> following,
      IEnumerable<Curve> flatCurves,
      Unroller unroller)
    {
      var result = new List<FlatFollowingCurve>();
      if (flatCurves == null || following == null || following.Count == 0)
        return result;
      var fallbackIndex = 0;
      foreach (var curve in flatCurves.Where(item => item != null && item.IsValid))
      {
        var sourceIndex = -1;
        try
        {
          sourceIndex = unroller.FollowingGeometryIndex(curve);
        }
        catch
        {
          sourceIndex = -1;
        }
        if (sourceIndex < 0 || sourceIndex >= following.Count)
        {
          if (fallbackIndex >= following.Count)
          {
            fallbackIndex++;
            continue;
          }
          sourceIndex = fallbackIndex;
        }
        fallbackIndex++;
        if (sourceIndex < 0 || sourceIndex >= following.Count)
          continue;
        result.Add(new FlatFollowingCurve
        {
          Curve = curve,
          Source = following[sourceIndex]
        });
      }
      return result;
    }

    private static List<FollowingCurve> BuildBoundaryFollowingCurves(
      RhinoObject boardObject,
      Brep sourceBrep,
      IList<int> faceIndices,
      IDictionary<int, Brep> offsetByFace,
      double tolerance,
      out int sourceHoleLoopCount,
      out int mappedHoleLoopCount)
    {
      var result = new List<FollowingCurve>();
      var patchFaces = new HashSet<int>(faceIndices);
      var innerEdgeIndices = new HashSet<int>();
      sourceHoleLoopCount = 0;
      mappedHoleLoopCount = 0;
      var loopTolerance = Math.Max(tolerance * 20.0, 1e-7);

      // 封闭卡扣孔必须按原Brep内环完整映射。2.2.4逐边映射时，复杂孔
      // 只要有一小段Pullback失败，该孔就会静默消失。边界展开现已与
      // 压痕/图案隔离，因此可安全恢复完整内环；完整曲线映射失败时，
      // 才逐Trim回退，并且只有所有片段重新闭合后才计为成功。
      foreach (var faceIndex in faceIndices)
      {
        var sourceFace = sourceBrep.Faces[faceIndex];
        Brep offsetFaceBrep;
        var hasOffsetFace = offsetByFace.TryGetValue(faceIndex, out offsetFaceBrep) &&
          offsetFaceBrep != null && offsetFaceBrep.Faces.Count > 0;
        foreach (var loop in sourceFace.Loops.Where(item =>
          item.LoopType == BrepLoopType.Inner))
        {
          sourceHoleLoopCount++;
          foreach (var trim in loop.Trims)
          {
            if (trim.Edge != null)
              innerEdgeIndices.Add(trim.Edge.EdgeIndex);
          }
          if (!hasOffsetFace)
            continue;

          Curve sourceLoop;
          try
          {
            sourceLoop = loop.To3dCurve();
          }
          catch
          {
            sourceLoop = null;
          }
          Curve mappedLoop;
          if (TryMapCurveToOffsetFace(
            sourceLoop,
            sourceFace,
            offsetFaceBrep,
            tolerance,
            out mappedLoop) &&
            JoinClosedLoops(new[] { mappedLoop }, loopTolerance).Count == 1)
          {
            result.Add(CreateBoundaryFollowingCurve(boardObject, mappedLoop, true));
            mappedHoleLoopCount++;
            continue;
          }

          var mappedSegments = new List<Curve>();
          var complete = true;
          foreach (var trim in loop.Trims)
          {
            Curve mappedSegment;
            if (!TryMapTrimToOffsetFace(
                trim,
                offsetFaceBrep,
                tolerance,
                out mappedSegment) &&
              (trim.Edge == null ||
               !TryMapCurveToOffsetFace(
                 trim.Edge.DuplicateCurve(),
                 sourceFace,
                 offsetFaceBrep,
                 tolerance,
                 out mappedSegment)))
            {
              complete = false;
              break;
            }
            mappedSegments.Add(mappedSegment);
          }
          if (!complete ||
              mappedSegments.Count == 0 ||
              JoinClosedLoops(mappedSegments, loopTolerance).Count != 1)
            continue;
          foreach (var mappedSegment in mappedSegments)
            result.Add(CreateBoundaryFollowingCurve(boardObject, mappedSegment, true));
          mappedHoleLoopCount++;
        }
      }

      // 外轮廓及开口卡槽仍按边逐段提取；内环边已在上方完整处理，
      // 不能重复加入。直面与弯曲面之间的公共边仍只作为展开接缝。
      foreach (var edge in sourceBrep.Edges)
      {
        if (innerEdgeIndices.Contains(edge.EdgeIndex))
          continue;
        var selectedFaces = edge.AdjacentFaces()
          .Where(patchFaces.Contains)
          .Distinct()
          .ToArray();
        // 两个已选面的公共边是展开接缝，不是板材的卡口/孔洞边界。
        if (selectedFaces.Length != 1)
          continue;
        Brep offsetFaceBrep;
        if (!offsetByFace.TryGetValue(selectedFaces[0], out offsetFaceBrep) ||
            offsetFaceBrep == null || offsetFaceBrep.Faces.Count == 0)
          continue;
        var sourceCurve = edge.DuplicateCurve();
        Curve mapped;
        if (!TryMapCurveToOffsetFace(
          sourceCurve,
          sourceBrep.Faces[selectedFaces[0]],
          offsetFaceBrep,
          tolerance,
          out mapped))
          continue;
        result.Add(CreateBoundaryFollowingCurve(boardObject, mapped, false));
      }
      return result;
    }

    private static FollowingCurve CreateBoundaryFollowingCurve(
      RhinoObject boardObject,
      Curve curve,
      bool isHoleBoundary)
    {
      return new FollowingCurve
      {
        Curve = curve,
        Attributes = boardObject.Attributes.Duplicate(),
        SourceObjectId = boardObject.Id,
        Name = boardObject.Attributes.Name,
        IsBoardBoundary = true,
        IsHoleBoundary = isHoleBoundary
      };
    }

    private static bool TryMapTrimToOffsetFace(
      BrepTrim sourceTrim,
      Brep offsetFaceBrep,
      double tolerance,
      out Curve mapped)
    {
      mapped = null;
      if (sourceTrim == null ||
          offsetFaceBrep == null || offsetFaceBrep.Faces.Count == 0)
        return false;

      Curve trimCurve2d;
      try
      {
        // BrepTrim是位于所属曲面UV参数域中的二维CurveProxy。直接将这条
        // 原始修剪曲线Pushup到保持同一参数化的中性层偏移面，可绕过
        // 复杂闭合孔在3D Curve.Pullback阶段的失败。
        trimCurve2d = sourceTrim.DuplicateCurve();
      }
      catch
      {
        trimCurve2d = null;
      }
      if (trimCurve2d == null || !trimCurve2d.IsValid)
        return false;

      foreach (var targetFace in offsetFaceBrep.Faces)
      {
        try
        {
          mapped = targetFace.Pushup(trimCurve2d, tolerance * 10.0);
        }
        catch
        {
          mapped = null;
        }
        if (mapped != null && mapped.IsValid)
          return true;
      }
      mapped = null;
      return false;
    }

    private static bool TryMapCurveToOffsetFace(
      Curve sourceCurve,
      BrepFace sourceFace,
      Brep offsetFaceBrep,
      double tolerance,
      out Curve mapped)
    {
      mapped = null;
      if (sourceCurve == null || sourceFace == null ||
          offsetFaceBrep == null || offsetFaceBrep.Faces.Count == 0)
        return false;

      // 平面段无需做数值Pullback：把孔边界沿目标中性层平面的法向
      // 直接平移，能完整保留长圆孔、卡扣槽和大量布尔碎边。
      Plane sourcePlane;
      if (sourceFace.TryGetPlane(out sourcePlane, tolerance * 10.0))
      {
        foreach (var targetFace in offsetFaceBrep.Faces)
        {
          Plane targetPlane;
          if (!targetFace.TryGetPlane(out targetPlane, tolerance * 10.0))
            continue;
          var parallel = Math.Abs(Vector3d.Multiply(
            sourcePlane.Normal,
            targetPlane.Normal));
          if (parallel < 0.999999)
            continue;
          var sample = sourceCurve.PointAtNormalizedLength(0.5);
          var translation = targetPlane.ClosestPoint(sample) - sample;
          var translated = sourceCurve.DuplicateCurve();
          if (translated == null ||
              !translated.Transform(Transform.Translation(translation)))
            continue;
          mapped = translated;
          return mapped.IsValid;
        }
      }

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
        return false;
      try
      {
        mapped = offsetFaceBrep.Faces[0].Pushup(pullback, tolerance * 10.0);
      }
      catch
      {
        mapped = null;
      }
      return mapped != null && mapped.IsValid;
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
          Curve mapped;
          if (!TryMapCurveToOffsetFace(
            sourceCurve,
            sourceBrep.Faces[faceIndex],
            offsetFaceBrep,
            tolerance,
            out mapped))
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
                faceAreas[adjacent] <= tolerance * tolerance)
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
      double joinTolerance,
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
        var joined = Brep.JoinBreps(pieces, joinTolerance);
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
      public bool IsBoardBoundary { get; set; }
      public bool IsHoleBoundary { get; set; }
    }

    private sealed class FlatFollowingCurve
    {
      public Curve Curve { get; set; }
      public FollowingCurve Source { get; set; }
    }
  }
}
