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

namespace WoodThicknessAdjuster.Core
{
  internal static class ThicknessAdjustmentWorkflow
  {
    internal static Result Run(
      RhinoDoc doc,
      double targetThicknessMillimeters,
      ThicknessAnchorMode anchorMode,
      ThicknessContactMode contactMode)
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
      var undo = doc.BeginUndoRecord("Wood Thickness Adjuster");
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
              contactMode == ThicknessContactMode.AutoFit
                ? "相邻木板贴合面优先"
                : "点在需要保持不动的板面")
            : "继续点击下一个木板零件，回车结束");
          getter.GeometryFilter = ObjectType.AnyObject;
          getter.GroupSelect = false;
          getter.SubObjectSelect = false;
          getter.AcceptNothing(true);
          getter.EnablePreSelect(adjustedCount == 0, true);
          var getResult = getter.Get();

          if (getResult == GetResult.Nothing)
            break;
          if (getResult == GetResult.Cancel)
            return adjustedCount > 0 ? Result.Success : Result.Cancel;
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
            out transform))
          {
            RhinoApp.WriteLine("WoodThicknessAdjuster：无法建立有效的板厚变换，已跳过该零件。");
            clickedObject.Select(false);
            continue;
          }

          int transformedCount;
          int skippedFollowers;
          Guid newBoardId;
          if (!TryApplyTransform(
            doc,
            part,
            transform,
            targetThicknessMillimeters,
            out transformedCount,
            out skippedFollowers,
            out newBoardId))
          {
            RhinoApp.WriteLine("WoodThicknessAdjuster：替换木板几何失败，原零件保持不变。");
            clickedObject.Select(false);
            continue;
          }

          adjustedCount++;
          lastAdjustedBoardId = newBoardId;
          var adjustmentDescription = contact != null
            ? (contactNeedsSnap ? "自动回贴相邻板面" : "以原贴合面为主表面")
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
          clickedObject.Select(false);
          doc.Views.Redraw();
        }
      }
      finally
      {
        doc.EndUndoRecord(undo);
      }

      if (adjustedCount == 0)
      {
        RhinoApp.WriteLine("WoodThicknessAdjuster：没有修改任何零件。");
        return Result.Nothing;
      }
      RhinoApp.WriteLine("WoodThicknessAdjuster：完成，共调整{0}个木板零件。", adjustedCount);
      return Result.Success;
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
      var snappedPoint = contact.NeighborPlane.ClosestPoint(contact.TargetCentroid);
      var correction = snappedPoint - contact.TargetCentroid;
      transform = Transform.Translation(correction) * scaleTransform;
      return transform.IsValid;
    }

    private static bool TryApplyTransform(
      RhinoDoc doc,
      PartTarget target,
      Transform transform,
      double targetThicknessMillimeters,
      out int transformedCount,
      out int skippedFollowers,
      out Guid newBoardId)
    {
      transformedCount = 0;
      skippedFollowers = 0;
      newBoardId = Guid.Empty;
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
          IsBoard = rhinoObject.Id == target.BoardObject.Id
        });
      }

      var boardTarget = prepared.FirstOrDefault(item => item.IsBoard);
      if (boardTarget == null)
        return false;
      newBoardId = doc.Objects.Transform(boardTarget.ObjectId, transform, true);
      if (newBoardId == Guid.Empty)
        return false;
      transformedCount++;

      foreach (var item in prepared.Where(item => !item.IsBoard))
      {
        if (doc.Objects.Transform(item.ObjectId, transform, true) != Guid.Empty)
          transformedCount++;
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
      return true;
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

    private sealed class TransformTarget
    {
      public Guid ObjectId { get; set; }
      public bool IsBoard { get; set; }
    }
  }
}
