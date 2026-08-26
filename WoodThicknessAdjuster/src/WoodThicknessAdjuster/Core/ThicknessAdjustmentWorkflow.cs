using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace WoodThicknessAdjuster.Core
{
  internal static class ThicknessAdjustmentWorkflow
  {
    internal static Result Run(
      RhinoDoc doc,
      double targetThicknessMillimeters,
      ThicknessAnchorMode anchorMode,
      ThicknessContactMode contactMode,
      ThicknessMoveMode moveMode)
    {
      if (doc == null || targetThicknessMillimeters <= 0.1 || targetThicknessMillimeters > 50.0)
      {
        RhinoApp.WriteLine("WoodThicknessAdjuster：目标板厚必须大于0.1mm且不超过50mm。");
        return Result.Failure;
      }

      var modelUnitsPerMillimeter = RhinoMath.UnitScale(
        UnitSystem.Millimeters,
        doc.ModelUnitSystem);
      if (!RhinoMath.IsValidDouble(modelUnitsPerMillimeter) || modelUnitsPerMillimeter <= 0.0)
      {
        RhinoApp.WriteLine("WoodThicknessAdjuster：无法换算当前文档单位，请先设置正确的Rhino模型单位。");
        return Result.Failure;
      }

      var targetModelUnits = targetThicknessMillimeters * modelUnitsPerMillimeter;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, modelUnitsPerMillimeter * 0.001);
      var adjustedCount = 0;
      var lastAdjustedBoardId = Guid.Empty;
      var history = new Stack<AdjustmentTransaction>();
      var conduit = new ThicknessContactConduit();
      try
      {
      while (true)
      {
        var getter = new GetObject();
        getter.SetCommandPrompt(adjustedCount == 0
          ? string.Format(
            CultureInfo.InvariantCulture,
            "点击木板零件（目标{0:0.###}mm）；{1}；回车结束",
            targetThicknessMillimeters,
            ContactPrompt(contactMode))
          : "继续点击下一个木板零件，或撤回上一步；回车结束");
        getter.GeometryFilter = ObjectType.AnyObject;
        getter.GroupSelect = false;
        getter.SubObjectSelect = false;
        getter.AcceptNothing(true);
        getter.EnablePreSelect(adjustedCount == 0, true);
        var undoLastOption = getter.AddOption(
          new LocalizeStringPair("UndoLast", "撤回上一步"));
        var getResult = getter.Get();

        if (getResult == GetResult.Nothing)
          break;
        if (getResult == GetResult.Cancel)
          return adjustedCount > 0 ? Result.Success : Result.Cancel;
        if (getResult == GetResult.Option && getter.OptionIndex() == undoLastOption)
        {
          if (history.Count == 0)
          {
            RhinoApp.WriteLine("WoodThicknessAdjuster：当前命令中没有可撤回的板厚调整。");
            continue;
          }
          var transaction = history.Peek();
          Guid restoredBoardId;
          Dictionary<Guid, Guid> restoredIds;
          if (!TryRollbackAdjustment(
            doc,
            transaction,
            out restoredBoardId,
            out restoredIds))
          {
            RhinoApp.WriteLine(
              "WoodThicknessAdjuster：撤回失败，场景中的相关对象可能已被其他操作修改。");
            continue;
          }
          history.Pop();
          RemapHistoryObjectIds(history, restoredIds);
          adjustedCount = Math.Max(0, adjustedCount - 1);
          lastAdjustedBoardId = history.Count > 0
            ? history.Peek().BoardObjectId
            : Guid.Empty;
          RhinoApp.WriteLine(
            "WoodThicknessAdjuster：已撤回上一块木板；当前保留{0}个调整。",
            adjustedCount);
          conduit.Clear();
          doc.Views.Redraw();
          continue;
        }
        if (getResult != GetResult.Object || getter.ObjectCount == 0)
        {
          var commandResult = getter.CommandResult();
          if (commandResult != Result.Success)
            return adjustedCount > 0 ? Result.Success : commandResult;
          continue;
        }

        var reference = getter.Object(0);
        var clickedObject = reference == null ? null : reference.Object();
        if (clickedObject == null)
          continue;

        Point3d selectionPoint;
        try
        {
          selectionPoint = reference.SelectionPoint();
        }
        catch
        {
          selectionPoint = Point3d.Unset;
        }

        PartTarget part;
        if (!TryResolvePart(
          doc,
          clickedObject,
          tolerance,
          selectionPoint,
          out part))
        {
          RhinoApp.WriteLine(
            "WoodThicknessAdjuster：未找到具有两张平行主表面的平直闭合Brep/Extrusion木板；折弯板不会强制缩放。");
          clickedObject.Select(false);
          continue;
        }

        var currentMillimeters = part.Analysis.ThicknessModelUnits /
          modelUnitsPerMillimeter;
        ThicknessContact contact = null;
        if (contactMode == ThicknessContactMode.AutoFit)
        {
          AssemblyContactResolver.TryFindContact(
            doc,
            part.BoardObject,
            part.Analysis,
            targetModelUnits,
            tolerance,
            modelUnitsPerMillimeter,
            lastAdjustedBoardId,
            out contact);
        }
        else if (contactMode == ThicknessContactMode.ExplicitFace)
        {
          Plane boardMarkerPlane;
          Point3d boardMarkerPoint;
          GetAnchorMarker(
            part.Analysis,
            selectionPoint,
            out boardMarkerPlane,
            out boardMarkerPoint);
          conduit.ShowBoard(
            boardMarkerPoint,
            boardMarkerPlane.Normal,
            modelUnitsPerMillimeter * 8.0);
          var explicitResult = TryPickExplicitContact(
            doc,
            part,
            tolerance,
            modelUnitsPerMillimeter,
            out contact);
          if (explicitResult == Result.Cancel)
            return adjustedCount > 0 ? Result.Success : Result.Cancel;
          if (explicitResult != Result.Success || contact == null)
          {
            clickedObject.Select(false);
            continue;
          }
        }

        if (contact != null)
        {
          conduit.ShowContact(
            contact.TargetCentroid,
            contact.TargetPlane.Normal,
            contact.NeighborPlane.ClosestPoint(contact.TargetCentroid),
            contact.NeighborPlane.Normal,
            modelUnitsPerMillimeter * 8.0);
        }
        else
        {
          conduit.Clear();
        }

        var thicknessNeedsAdjustment =
          Math.Abs(currentMillimeters - targetThicknessMillimeters) > 0.005;
        var contactNeedsSnap = contact != null && contact.NeedsSnap;
        if (!thicknessNeedsAdjustment && !contactNeedsSnap)
        {
          RhinoApp.WriteLine(
            "WoodThicknessAdjuster：该零件已经是{0:0.###}mm，贴合关系也无需调整。",
            targetThicknessMillimeters);
          clickedObject.Select(false);
          continue;
        }

        Transform transform;
        if (!TryCreateThicknessTransform(
          part.Analysis,
          selectionPoint,
          targetModelUnits,
          anchorMode,
          contact,
          moveMode,
          out transform))
        {
          RhinoApp.WriteLine(contact != null
            ? "WoodThicknessAdjuster：所选移动轴与目标面平行，无法沿该轴到达目标面；请改用物体厚度轴或其他世界坐标轴。"
            : "WoodThicknessAdjuster：无法建立有效的板厚变换，已跳过该零件。");
          clickedObject.Select(false);
          continue;
        }

        int transformedCount;
        int skippedFollowers;
        AdjustmentTransaction appliedTransaction;
        if (!TryApplyTransform(
          doc,
          part,
          transform,
          targetThicknessMillimeters,
          out transformedCount,
          out skippedFollowers,
          out appliedTransaction))
        {
          RhinoApp.WriteLine("WoodThicknessAdjuster：替换木板几何失败，原零件保持不变。");
          clickedObject.Select(false);
          continue;
        }

        RemapHistoryObjectIds(history, appliedTransaction);
        history.Push(appliedTransaction);
        adjustedCount++;
        lastAdjustedBoardId = appliedTransaction.BoardObjectId;
        var adjustmentDescription = contact != null
          ? (contactMode == ThicknessContactMode.ExplicitFace
            ? "贴合指定目标面"
            : (contactNeedsSnap ? "自动回贴相邻板面" : "以原贴合面为主表面"))
          : (anchorMode == ThicknessAnchorMode.ClickedFace
            ? "保持点击面"
            : "中心对称调整");
        RhinoApp.WriteLine(string.Format(
          CultureInfo.InvariantCulture,
          "WoodThicknessAdjuster：{0:0.###}mm → {1:0.###}mm；{2}；同步{3}个对象{4}。",
          currentMillimeters,
          targetThicknessMillimeters,
          adjustmentDescription,
          transformedCount,
          skippedFollowers > 0 ? "，另有不支持的组内对象已跳过" : string.Empty));
        if (contact != null)
        {
          var transformedContactPoint = contact.TargetCentroid;
          transformedContactPoint.Transform(transform);
          conduit.ShowContact(
            transformedContactPoint,
            contact.TargetPlane.Normal,
            contact.NeighborPlane.ClosestPoint(transformedContactPoint),
            contact.NeighborPlane.Normal,
            modelUnitsPerMillimeter * 8.0);
          ReportContactVerification(
            doc,
            appliedTransaction.BoardObjectId,
            contact,
            tolerance,
            modelUnitsPerMillimeter,
            conduit);
        }
        clickedObject.Select(false);
        doc.Views.Redraw();
      }
      }
      finally
      {
        conduit.Dispose();
      }

      if (adjustedCount == 0)
      {
        RhinoApp.WriteLine("WoodThicknessAdjuster：没有修改任何零件。");
        return Result.Nothing;
      }
      RhinoApp.WriteLine("WoodThicknessAdjuster：完成，共调整{0}个木板零件。", adjustedCount);
      return Result.Success;
    }

    private static string ContactPrompt(ThicknessContactMode contactMode)
    {
      if (contactMode == ThicknessContactMode.ExplicitFace)
        return "先点木板，再点需要贴合的目标面";
      return contactMode == ThicknessContactMode.AutoFit
        ? "相邻木板贴合面优先"
        : "点在需要保持不动的板面";
    }

    private static void GetAnchorMarker(
      ThicknessAnalysis analysis,
      Point3d selectionPoint,
      out Plane plane,
      out Point3d point)
    {
      var useFirst = analysis.PreferredAnchorFaceIndex == analysis.FirstFaceIndex;
      if (analysis.PreferredAnchorFaceIndex != analysis.FirstFaceIndex &&
        analysis.PreferredAnchorFaceIndex != analysis.SecondFaceIndex)
      {
        var firstDistance = selectionPoint.IsValid
          ? Math.Abs(analysis.FirstPlane.DistanceTo(selectionPoint))
          : 0.0;
        var secondDistance = selectionPoint.IsValid
          ? Math.Abs(analysis.SecondPlane.DistanceTo(selectionPoint))
          : double.MaxValue;
        useFirst = firstDistance <= secondDistance;
      }
      plane = useFirst ? analysis.FirstPlane : analysis.SecondPlane;
      point = useFirst ? analysis.FirstCentroid : analysis.SecondCentroid;
    }

    private static Result TryPickExplicitContact(
      RhinoDoc doc,
      PartTarget part,
      double tolerance,
      double modelUnitsPerMillimeter,
      out ThicknessContact contact)
    {
      contact = null;
      var getter = new GetObject();
      getter.SetCommandPrompt("点击另一零件上需要贴合的平面；Esc取消当前命令");
      getter.GeometryFilter = ObjectType.Surface;
      getter.GroupSelect = false;
      getter.SubObjectSelect = true;
      getter.EnablePreSelect(false, true);
      var getResult = getter.Get();
      if (getResult == GetResult.Cancel)
        return Result.Cancel;
      if (getResult != GetResult.Object || getter.ObjectCount == 0)
        return getter.CommandResult();

      var reference = getter.Object(0);
      var neighborObject = reference == null ? null : reference.Object();
      Plane neighborPlane;
      if (neighborObject == null ||
        !TryGetClickedPlanarFace(reference, tolerance, out neighborPlane))
      {
        RhinoApp.WriteLine("WoodThicknessAdjuster：目标面必须是平面，曲面不能作为贴合基准。");
        if (neighborObject != null)
          neighborObject.Select(false);
        return Result.Failure;
      }

      if (!AssemblyContactResolver.TryCreateExplicitContact(
        part.BoardObject,
        part.Analysis,
        neighborObject,
        neighborPlane,
        tolerance,
        modelUnitsPerMillimeter,
        out contact))
      {
        RhinoApp.WriteLine(
          "WoodThicknessAdjuster：目标面必须属于另一零件，并与木板两张主表面平行；请勿选择木板侧面。");
        neighborObject.Select(false);
        return Result.Failure;
      }
      neighborObject.Select(false);
      return Result.Success;
    }

    private static bool TryGetClickedPlanarFace(
      ObjRef reference,
      double tolerance,
      out Plane plane)
    {
      plane = Plane.Unset;
      if (reference == null)
        return false;
      var directFace = reference.Face();
      if (directFace != null &&
        directFace.TryGetPlane(out plane, tolerance * 10.0))
        return true;

      var rhinoObject = reference.Object();
      if (rhinoObject == null || rhinoObject.Geometry == null)
        return false;
      var brep = rhinoObject.Geometry as Brep;
      var extrusion = rhinoObject.Geometry as Extrusion;
      if (brep == null && extrusion != null)
        brep = extrusion.ToBrep();
      if (brep == null || !brep.IsValid)
        return false;

      Point3d selectionPoint;
      try
      {
        selectionPoint = reference.SelectionPoint();
      }
      catch
      {
        selectionPoint = Point3d.Unset;
      }
      var candidates = new List<PlanarTargetFace>();
      for (var index = 0; index < brep.Faces.Count; index++)
      {
        Plane candidatePlane;
        if (!brep.Faces[index].TryGetPlane(
          out candidatePlane,
          tolerance * 10.0))
          continue;
        var properties = AreaMassProperties.Compute(brep.Faces[index]);
        var area = properties == null ? 0.0 : properties.Area;
        var distance = selectionPoint.IsValid
          ? DistanceToTrimmedFace(
            brep.Faces[index],
            candidatePlane,
            selectionPoint)
          : 0.0;
        candidates.Add(new PlanarTargetFace
        {
          Plane = candidatePlane,
          Distance = distance,
          Area = area
        });
      }
      var best = candidates
        .OrderBy(item => item.Distance)
        .ThenByDescending(item => item.Area)
        .FirstOrDefault();
      if (best == null)
        return false;
      plane = best.Plane;
      return plane.IsValid;
    }

    private static double DistanceToTrimmedFace(
      BrepFace face,
      Plane plane,
      Point3d point)
    {
      if (face == null || !point.IsValid)
        return double.MaxValue;
      var projected = plane.ClosestPoint(point);
      double u;
      double v;
      if (!face.ClosestPoint(projected, out u, out v) ||
        face.IsPointOnFace(u, v) == PointFaceRelation.Exterior)
        return double.MaxValue;
      return point.DistanceTo(projected);
    }

    private static void ReportContactVerification(
      RhinoDoc doc,
      Guid boardObjectId,
      ThicknessContact contact,
      double tolerance,
      double modelUnitsPerMillimeter,
      ThicknessContactConduit conduit)
    {
      ContactVerification verification;
      if (!AssemblyContactResolver.TryVerifyContact(
        doc,
        boardObjectId,
        contact,
        tolerance,
        modelUnitsPerMillimeter,
        out verification))
      {
        RhinoApp.WriteLine("WoodThicknessAdjuster：贴合检查无法完成，请手动检查目标位置。");
        conduit.ShowVerification(false, "贴合检查失败");
        return;
      }

      var gapMillimeters = verification.GapModelUnits /
        modelUnitsPerMillimeter;
      var passed = verification.GapWithinTolerance &&
        verification.HasProjectedOverlap;
      var status = passed ? "贴合检查通过" : "贴合检查警告";
      RhinoApp.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "WoodThicknessAdjuster：{0}；面间距{1:0.###}mm；投影重合率{2:0.#}%。",
        status,
        gapMillimeters,
        verification.OverlapRatio * 100.0));
      conduit.ShowVerification(
        passed,
        string.Format(
          CultureInfo.InvariantCulture,
          "{0}  间距{1:0.###}mm / 重合{2:0.#}%",
          passed ? "通过" : "警告",
          gapMillimeters,
          verification.OverlapRatio * 100.0));
    }

    private static bool TryResolvePart(
      RhinoDoc doc,
      RhinoObject clickedObject,
      double tolerance,
      Point3d selectionPoint,
      out PartTarget target)
    {
      target = null;
      var directlyClickedBoard = AnalyzeObject(
        clickedObject,
        tolerance,
        selectionPoint);
      var candidateGroups = new List<PartTarget>();
      var groupIndices = clickedObject.Attributes.GetGroupList() ?? new int[0];
      foreach (var groupIndex in groupIndices)
      {
        RhinoObject[] members;
        try
        {
          members = doc.Groups.GroupMembers(groupIndex);
        }
        catch
        {
          members = null;
        }
        if (members == null || members.Length == 0)
          continue;

        var boards = members
          .Select(item => AnalyzeObject(item, tolerance, selectionPoint))
          .Where(item => item != null)
          .OrderByDescending(item => item.Analysis.Score)
          .ToList();
        if (boards.Count == 0)
          continue;

        AnalyzedObject board;
        if (directlyClickedBoard != null)
        {
          board = boards.FirstOrDefault(
            item => item.Object.Id == directlyClickedBoard.Object.Id);
        }
        else if (boards.Count == 1)
        {
          board = boards[0];
        }
        else if (selectionPoint.IsValid)
        {
          board = boards
            .OrderBy(item => DistanceToGeometryBounds(
              item.Object.Geometry,
              selectionPoint))
            .ThenByDescending(item => item.Analysis.Score)
            .FirstOrDefault();
        }
        else
        {
          board = null;
        }
        if (board == null)
          continue;

        var singleBoardGroup = boards.Count == 1;
        candidateGroups.Add(new PartTarget
        {
          BoardObject = board.Object,
          Analysis = board.Analysis,
          Objects = members
            .Where(item => item != null &&
              (item.Id == board.Object.Id ||
                (ShouldFollowBoard(item.Geometry) &&
                  (singleBoardGroup || IsFollowerAttachedToBoard(
                    item.Geometry,
                    board.Object.Geometry,
                    board.Analysis,
                    tolerance)))))
            .GroupBy(item => item.Id)
            .Select(item => item.First())
            .ToList()
        });
      }

      if (candidateGroups.Count > 0)
      {
        var best = candidateGroups
          .OrderByDescending(item => item.BoardObject.Id == clickedObject.Id)
          .ThenByDescending(item => item.Analysis.Score)
          .First();
        target = new PartTarget
        {
          BoardObject = best.BoardObject,
          Analysis = best.Analysis,
          Objects = candidateGroups
            .Where(item => item.BoardObject.Id == best.BoardObject.Id)
            .SelectMany(item => item.Objects)
            .GroupBy(item => item.Id)
            .Select(item => item.First())
            .ToList()
        };
        return true;
      }

      if (directlyClickedBoard == null)
        return false;
      target = new PartTarget
      {
        BoardObject = clickedObject,
        Analysis = directlyClickedBoard.Analysis,
        Objects = new List<RhinoObject> { clickedObject }
      };
      return true;
    }

    private static AnalyzedObject AnalyzeObject(
      RhinoObject rhinoObject,
      double tolerance,
      Point3d selectionPoint)
    {
      if (rhinoObject == null)
        return null;
      ThicknessAnalysis analysis;
      if (!ThicknessAnalyzer.TryAnalyze(
        rhinoObject.Geometry,
        tolerance,
        selectionPoint,
        out analysis))
        return null;
      return new AnalyzedObject
      {
        Object = rhinoObject,
        Analysis = analysis
      };
    }

    private static bool ShouldFollowBoard(GeometryBase geometry)
    {
      return geometry is Curve ||
        geometry is TextEntity ||
        geometry is TextDot ||
        geometry is Point;
    }

    private static bool IsFollowerAttachedToBoard(
      GeometryBase follower,
      GeometryBase board,
      ThicknessAnalysis analysis,
      double tolerance)
    {
      if (follower == null || board == null || analysis == null)
        return false;
      var followerBox = follower.GetBoundingBox(true);
      var boardBox = board.GetBoundingBox(true);
      if (!followerBox.IsValid || !boardBox.IsValid)
        return false;

      var proximity = Math.Max(
        tolerance * 25.0,
        boardBox.Diagonal.Length * 1e-7);
      if (followerBox.Max.X < boardBox.Min.X - proximity ||
        followerBox.Min.X > boardBox.Max.X + proximity ||
        followerBox.Max.Y < boardBox.Min.Y - proximity ||
        followerBox.Min.Y > boardBox.Max.Y + proximity ||
        followerBox.Max.Z < boardBox.Min.Z - proximity ||
        followerBox.Min.Z > boardBox.Max.Z + proximity)
        return false;

      var samplePoints = followerBox.GetCorners().ToList();
      samplePoints.Add(followerBox.Center);
      return samplePoints.Any(point =>
        Math.Abs(analysis.FirstPlane.DistanceTo(point)) <= proximity ||
        Math.Abs(analysis.SecondPlane.DistanceTo(point)) <= proximity);
    }

    private static double DistanceToGeometryBounds(
      GeometryBase geometry,
      Point3d point)
    {
      if (geometry == null || !point.IsValid)
        return double.MaxValue;
      var box = geometry.GetBoundingBox(true);
      if (!box.IsValid)
        return double.MaxValue;
      var closest = new Point3d(
        Math.Max(box.Min.X, Math.Min(box.Max.X, point.X)),
        Math.Max(box.Min.Y, Math.Min(box.Max.Y, point.Y)),
        Math.Max(box.Min.Z, Math.Min(box.Max.Z, point.Z)));
      return point.DistanceTo(closest);
    }

    private static bool TryCreateThicknessTransform(
      ThicknessAnalysis analysis,
      Point3d selectionPoint,
      double targetThickness,
      ThicknessAnchorMode anchorMode,
      ThicknessContact contact,
      ThicknessMoveMode moveMode,
      out Transform transform)
    {
      transform = Transform.Unset;
      if (analysis == null || analysis.ThicknessModelUnits <= 0.0 || targetThickness <= 0.0)
        return false;

      Plane scalingPlane;
      if (contact != null)
      {
        scalingPlane = new Plane(
          contact.TargetPlane.Origin,
          contact.TargetPlane.XAxis,
          contact.TargetPlane.YAxis);
      }
      else if (anchorMode == ThicknessAnchorMode.Center)
      {
        var signedDistance = analysis.FirstPlane.DistanceTo(analysis.SecondCentroid);
        var centerOrigin = analysis.FirstPlane.Origin +
          analysis.FirstPlane.Normal * (signedDistance * 0.5);
        scalingPlane = new Plane(
          centerOrigin,
          analysis.FirstPlane.XAxis,
          analysis.FirstPlane.YAxis);
      }
      else
      {
        var useFirst = analysis.PreferredAnchorFaceIndex == analysis.FirstFaceIndex;
        if (analysis.PreferredAnchorFaceIndex != analysis.FirstFaceIndex &&
          analysis.PreferredAnchorFaceIndex != analysis.SecondFaceIndex)
        {
          useFirst = true;
          if (selectionPoint.IsValid)
          {
            var firstDistance = Math.Abs(analysis.FirstPlane.DistanceTo(selectionPoint));
            var secondDistance = Math.Abs(analysis.SecondPlane.DistanceTo(selectionPoint));
            if (Math.Abs(firstDistance - secondDistance) <= 1e-9)
            {
              firstDistance = selectionPoint.DistanceTo(analysis.FirstCentroid);
              secondDistance = selectionPoint.DistanceTo(analysis.SecondCentroid);
            }
            useFirst = firstDistance <= secondDistance;
          }
        }
        var anchor = useFirst ? analysis.FirstPlane : analysis.SecondPlane;
        scalingPlane = new Plane(anchor.Origin, anchor.XAxis, anchor.YAxis);
      }

      var factor = targetThickness / analysis.ThicknessModelUnits;
      var scaleTransform = Transform.Scale(scalingPlane, 1.0, 1.0, factor);
      if (contact == null)
      {
        transform = scaleTransform;
        return transform.IsValid;
      }

      // 先以当前贴合面改变板厚，再整体平移到邻板表面。曲线、文字和点
      // 使用同一复合变换，因此不会留在旧表面或落入板材中间。
      Vector3d correction;
      if (!TryGetContactCorrection(contact, moveMode, out correction))
        return false;
      transform = Transform.Translation(correction) * scaleTransform;
      return transform.IsValid;
    }

    private static bool TryGetContactCorrection(
      ThicknessContact contact,
      ThicknessMoveMode moveMode,
      out Vector3d correction)
    {
      correction = Vector3d.Unset;
      if (contact == null)
        return false;
      Vector3d direction;
      if (moveMode == ThicknessMoveMode.WorldAuto)
      {
        direction = new[]
        {
          Vector3d.XAxis,
          Vector3d.YAxis,
          Vector3d.ZAxis
        }
          .OrderByDescending(axis => Math.Abs(Vector3d.Multiply(
            axis,
            contact.NeighborPlane.Normal)))
          .First();
      }
      else if (moveMode == ThicknessMoveMode.WorldX)
        direction = Vector3d.XAxis;
      else if (moveMode == ThicknessMoveMode.WorldY)
        direction = Vector3d.YAxis;
      else if (moveMode == ThicknessMoveMode.WorldZ)
        direction = Vector3d.ZAxis;
      else
        direction = contact.TargetPlane.Normal;
      if (!direction.Unitize())
        return false;

      var denominator = Vector3d.Multiply(
        contact.NeighborPlane.Normal,
        direction);
      if (Math.Abs(denominator) <= 1e-8)
        return false;
      var distance = contact.NeighborPlane.DistanceTo(contact.TargetCentroid);
      correction = direction * (-distance / denominator);
      return correction.IsValid;
    }

    private static bool TryApplyTransform(
      RhinoDoc doc,
      PartTarget target,
      Transform transform,
      double targetThicknessMillimeters,
      out int transformedCount,
      out int skippedFollowers,
      out AdjustmentTransaction transaction)
    {
      transformedCount = 0;
      skippedFollowers = 0;
      transaction = null;
      var prepared = new List<TransformTarget>();
      foreach (var rhinoObject in target.Objects
        .OrderByDescending(item => item.Id == target.BoardObject.Id))
      {
        GeometryBase geometry;
        try
        {
          geometry = rhinoObject.DuplicateGeometry();
        }
        catch
        {
          geometry = null;
        }
        if (geometry == null || !geometry.Transform(transform) || !geometry.IsValid)
        {
          if (rhinoObject.Id == target.BoardObject.Id)
            return false;
          skippedFollowers++;
          continue;
        }
        prepared.Add(new TransformTarget
        {
          ObjectId = rhinoObject.Id,
          IsBoard = rhinoObject.Id == target.BoardObject.Id,
          OriginalGeometry = rhinoObject.DuplicateGeometry(),
          OriginalAttributes = rhinoObject.Attributes.Duplicate()
        });
      }

      var boardTarget = prepared.FirstOrDefault(item => item.IsBoard);
      if (boardTarget == null)
        return false;
      var newBoardId = doc.Objects.Transform(boardTarget.ObjectId, transform, true);
      if (newBoardId == Guid.Empty)
        return false;
      var applied = new AdjustmentTransaction { BoardObjectId = newBoardId };
      boardTarget.CurrentObjectId = newBoardId;
      applied.Objects.Add(boardTarget);
      transformedCount++;

      foreach (var item in prepared.Where(item => !item.IsBoard))
      {
        var transformedId = doc.Objects.Transform(item.ObjectId, transform, true);
        if (transformedId != Guid.Empty)
        {
          item.CurrentObjectId = transformedId;
          applied.Objects.Add(item);
          transformedCount++;
        }
        else
          skippedFollowers++;
      }

      var board = doc.Objects.FindId(newBoardId);
      if (board != null)
      {
        var attributes = board.Attributes.Duplicate();
        attributes.SetUserString(
          "WoodThicknessAdjuster.TargetMillimeters",
          targetThicknessMillimeters.ToString("0.###", CultureInfo.InvariantCulture));
        doc.Objects.ModifyAttributes(board.Id, attributes, true);
      }
      transaction = applied;
      return true;
    }

    private static void RemapHistoryObjectIds(
      IEnumerable<AdjustmentTransaction> history,
      AdjustmentTransaction appliedTransaction)
    {
      foreach (var appliedObject in appliedTransaction.Objects)
      {
        foreach (var previous in history)
        {
          if (previous.BoardObjectId == appliedObject.ObjectId)
            previous.BoardObjectId = appliedObject.CurrentObjectId;
          foreach (var previousObject in previous.Objects.Where(item =>
            item.CurrentObjectId == appliedObject.ObjectId))
            previousObject.CurrentObjectId = appliedObject.CurrentObjectId;
        }
      }
    }

    private static void RemapHistoryObjectIds(
      IEnumerable<AdjustmentTransaction> history,
      IDictionary<Guid, Guid> restoredIds)
    {
      foreach (var previous in history)
      {
        Guid restoredBoardId;
        if (restoredIds.TryGetValue(previous.BoardObjectId, out restoredBoardId))
          previous.BoardObjectId = restoredBoardId;
        foreach (var previousObject in previous.Objects)
        {
          Guid restoredObjectId;
          if (restoredIds.TryGetValue(
            previousObject.CurrentObjectId,
            out restoredObjectId))
            previousObject.CurrentObjectId = restoredObjectId;
        }
      }
    }

    private static bool TryRollbackAdjustment(
      RhinoDoc doc,
      AdjustmentTransaction transaction,
      out Guid restoredBoardId,
      out Dictionary<Guid, Guid> restoredIds)
    {
      restoredBoardId = Guid.Empty;
      restoredIds = new Dictionary<Guid, Guid>();
      if (transaction == null || transaction.Objects.Count == 0)
        return false;
      if (transaction.Objects.Any(item =>
        item.CurrentObjectId == Guid.Empty ||
        item.OriginalGeometry == null ||
        item.OriginalAttributes == null ||
        doc.Objects.FindId(item.CurrentObjectId) == null))
        return false;

      var addedIds = new List<Guid>();
      foreach (var item in transaction.Objects)
      {
        var geometry = item.OriginalGeometry.Duplicate();
        var attributes = item.OriginalAttributes.Duplicate();
        var restoredId = geometry == null
          ? Guid.Empty
          : doc.Objects.Add(geometry, attributes);
        if (restoredId == Guid.Empty)
        {
          foreach (var addedId in addedIds)
            doc.Objects.Delete(addedId, true);
          return false;
        }
        addedIds.Add(restoredId);
        restoredIds[item.CurrentObjectId] = restoredId;
        if (item.IsBoard)
          restoredBoardId = restoredId;
      }

      foreach (var item in transaction.Objects)
      {
        if (!doc.Objects.Delete(item.CurrentObjectId, true))
        {
          RhinoApp.WriteLine(
            "WoodThicknessAdjuster：撤回时无法删除已调整对象，请立即使用Rhino常规撤销恢复。");
          return false;
        }
      }
      return restoredBoardId != Guid.Empty;
    }

    private sealed class AnalyzedObject
    {
      public RhinoObject Object { get; set; }
      public ThicknessAnalysis Analysis { get; set; }
    }

    private sealed class PartTarget
    {
      public RhinoObject BoardObject { get; set; }
      public ThicknessAnalysis Analysis { get; set; }
      public List<RhinoObject> Objects { get; set; }
    }

    private sealed class PlanarTargetFace
    {
      public Plane Plane { get; set; }
      public double Distance { get; set; }
      public double Area { get; set; }
    }

    private sealed class TransformTarget
    {
      public Guid ObjectId { get; set; }
      public Guid CurrentObjectId { get; set; }
      public bool IsBoard { get; set; }
      public GeometryBase OriginalGeometry { get; set; }
      public ObjectAttributes OriginalAttributes { get; set; }
    }

    private sealed class AdjustmentTransaction
    {
      public AdjustmentTransaction()
      {
        Objects = new List<TransformTarget>();
      }

      public Guid BoardObjectId { get; set; }
      public List<TransformTarget> Objects { get; private set; }
    }
  }
}
