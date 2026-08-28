using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodJointPro.Core
{
  internal static class JointDocumentUpdater
  {
    private const string FlatRoleKey = "WoodSheetLayoutRole";
    private const string FlatSourceKey = "WoodJointPro.SourceId";

    internal static bool ApplyJoint(
      RhinoDoc doc,
      JointBuildResult result,
      JointSettings settings,
      JointCalibration calibration,
      double tolerance,
      out int synchronizedFlatBoards,
      out string error)
    {
      synchronizedFlatBoards = 0;
      error = null;
      if (doc == null || result == null || result.First == null || result.Second == null)
      {
        error = "榫槽结果无效";
        return false;
      }
      var jointId = Guid.NewGuid().ToString("N");
      var edits = new[] { result.First, result.Second };
      var prepared = new List<PreparedReplacement>();
      foreach (var edit in edits)
      {
        if (!PrepareBoardAndFlatCopies(doc, edit, tolerance, prepared, out error))
          return false;
      }
      foreach (var item in prepared)
      {
        if (item.ObjectId == result.First.Board.Object.Id ||
            item.SourceObjectId == result.First.Board.Object.Id)
          item.CounterpartId = result.Second.Board.Object.Id;
        else if (item.ObjectId == result.Second.Board.Object.Id ||
                 item.SourceObjectId == result.Second.Board.Object.Id)
          item.CounterpartId = result.First.Board.Object.Id;
      }

      var duplicateTarget = prepared
        .GroupBy(item => item.ObjectId)
        .FirstOrDefault(group => group.Count() > 1);
      if (duplicateTarget != null)
      {
        error = "同一个铺平副本同时关联到两块源木板，请先检查WSL_PAIR分组";
        return false;
      }

      var undo = doc.BeginUndoRecord("WoodJoint Pro " + result.Description);
      var replaced = new List<PreparedReplacement>();
      try
      {
        foreach (var item in prepared)
        {
          if (!doc.Objects.Replace(item.ObjectId, item.NewGeometry))
          {
            error = "替换对象几何失败，已恢复本次操作";
            Rollback(doc, replaced);
            return false;
          }
          replaced.Add(item);
        }

        foreach (var item in prepared)
        {
          var rhinoObject = doc.Objects.FindId(item.ObjectId);
          if (rhinoObject == null)
            continue;
          var attributes = rhinoObject.Attributes.Duplicate();
          attributes.SetUserString("WoodJointPro.JointId", jointId);
          attributes.SetUserString("WoodJointPro.JointType", result.Description);
          attributes.SetUserString("WoodJointPro.RevisionUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
          attributes.SetUserString("WoodJointPro.Fit", settings.Fit.ToString());
          attributes.SetUserString("WoodJointPro.ClearanceMillimeters",
            settings.ClearanceMillimeters(calibration).ToString("0.###", CultureInfo.InvariantCulture));
          if (item.SourceObjectId != Guid.Empty)
            attributes.SetUserString(FlatSourceKey, item.SourceObjectId.ToString("D"));
          if (item.CounterpartId != Guid.Empty)
            attributes.SetUserString("WoodJointPro.Counterpart", item.CounterpartId.ToString("D"));
          doc.Objects.ModifyAttributes(item.ObjectId, attributes, true);
          if (item.IsFlatCopy)
            synchronizedFlatBoards++;
        }
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }
      doc.Views.Redraw();
      return true;
    }

    internal static bool ApplySingleBoardEdit(
      RhinoDoc doc,
      BoardInfo board,
      Brep geometry,
      string operationName,
      double tolerance,
      out int synchronizedFlatBoards,
      out string error)
    {
      var result = new JointBuildResult
      {
        First = new BoardEdit { Board = board, Geometry = geometry },
        Second = new BoardEdit { Board = board, Geometry = geometry },
        Description = operationName,
        Frame = new JointFrame()
      };
      synchronizedFlatBoards = 0;
      error = null;
      var prepared = new List<PreparedReplacement>();
      if (!PrepareBoardAndFlatCopies(doc, result.First, tolerance, prepared, out error))
        return false;
      var undo = doc.BeginUndoRecord("WoodJoint Pro " + operationName);
      var replaced = new List<PreparedReplacement>();
      try
      {
        foreach (var item in prepared)
        {
          if (!doc.Objects.Replace(item.ObjectId, item.NewGeometry))
          {
            error = "替换对象几何失败，已恢复本次操作";
            Rollback(doc, replaced);
            return false;
          }
          replaced.Add(item);
          var rhinoObject = doc.Objects.FindId(item.ObjectId);
          if (rhinoObject != null)
          {
            var attributes = rhinoObject.Attributes.Duplicate();
            attributes.SetUserString("WoodJointPro.Operation", operationName);
            attributes.SetUserString("WoodJointPro.RevisionUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            if (item.SourceObjectId != Guid.Empty)
              attributes.SetUserString(FlatSourceKey, item.SourceObjectId.ToString("D"));
            doc.Objects.ModifyAttributes(item.ObjectId, attributes, true);
          }
          if (item.IsFlatCopy)
            synchronizedFlatBoards++;
        }
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }
      doc.Views.Redraw();
      return true;
    }

    private static bool PrepareBoardAndFlatCopies(
      RhinoDoc doc,
      BoardEdit edit,
      double tolerance,
      ICollection<PreparedReplacement> prepared,
      out string error)
    {
      error = null;
      if (edit == null || edit.Board == null || edit.Board.Object == null ||
          edit.Geometry == null || !edit.Geometry.IsValid || !edit.Geometry.IsSolid)
      {
        error = "待替换的木板几何无效";
        return false;
      }
      var sourceObject = doc.Objects.FindId(edit.Board.Object.Id);
      var sourceBrep = sourceObject == null ? null : BoardAnalyzer.ToBrep(sourceObject.Geometry);
      if (sourceBrep == null)
      {
        error = "源木板已被删除或修改";
        return false;
      }
      prepared.Add(new PreparedReplacement
      {
        ObjectId = sourceObject.Id,
        OriginalGeometry = sourceBrep,
        NewGeometry = edit.Geometry.DuplicateBrep(),
        CounterpartId = Guid.Empty
      });

      foreach (var link in FindFlatLinks(doc, edit.Board, tolerance))
      {
        var flatObject = doc.Objects.FindId(link.ObjectId);
        var originalFlat = flatObject == null ? null : BoardAnalyzer.ToBrep(flatObject.Geometry);
        if (originalFlat == null)
          continue;
        var flatGeometry = edit.Geometry.DuplicateBrep();
        if (!flatGeometry.Transform(link.SourceToFlat) || !flatGeometry.IsValid || !flatGeometry.IsSolid)
        {
          error = "铺平副本同步变换失败，原件未修改";
          return false;
        }
        prepared.Add(new PreparedReplacement
        {
          ObjectId = flatObject.Id,
          OriginalGeometry = originalFlat,
          NewGeometry = flatGeometry,
          IsFlatCopy = true,
          SourceObjectId = edit.Board.Object.Id
        });
      }
      return true;
    }

    private static IEnumerable<FlatBoardLink> FindFlatLinks(
      RhinoDoc doc,
      BoardInfo source,
      double tolerance)
    {
      var candidates = new Dictionary<Guid, RhinoObject>();
      foreach (var rhinoObject in doc.Objects.GetObjectList(ObjectType.Brep | ObjectType.Extrusion))
      {
        var linkedSource = rhinoObject.Attributes.GetUserString(FlatSourceKey);
        Guid parsed;
        if (Guid.TryParse(linkedSource, out parsed) && parsed == source.Object.Id)
          candidates[rhinoObject.Id] = rhinoObject;
      }

      foreach (var groupIndex in source.Object.Attributes.GetGroupList() ?? new int[0])
      {
        var groupName = groupIndex >= 0 && groupIndex < doc.Groups.Count
          ? doc.Groups.GroupName(groupIndex)
          : null;
        if (string.IsNullOrWhiteSpace(groupName) ||
            !groupName.StartsWith("WSL_PAIR_", StringComparison.OrdinalIgnoreCase))
          continue;
        foreach (var member in doc.Groups.GroupMembers(groupIndex) ?? new RhinoObject[0])
        {
          if (member == null || member.Id == source.Object.Id)
            continue;
          if (string.Equals(member.Attributes.GetUserString(FlatRoleKey), "FlatCopy", StringComparison.Ordinal))
            candidates[member.Id] = member;
        }
      }

      foreach (var candidate in candidates.Values)
      {
        BoardInfo flat;
        if (!BoardAnalyzer.TryAnalyze(candidate, tolerance, out flat))
          continue;
        Plane flatFacePlane;
        if (!BoardAnalyzer.TryGetFacePlane(flat.Brep, source.FirstFaceIndex, tolerance, out flatFacePlane))
          flatFacePlane = flat.FirstPlane;
        var transform = Transform.PlaneToPlane(source.FirstPlane, flatFacePlane);
        var mappedCenter = source.Centroid;
        mappedCenter.Transform(transform);
        var allowance = Math.Max(flat.Bounds.Diagonal.Length * 0.15, tolerance * 50.0);
        if (mappedCenter.DistanceTo(flat.Centroid) > allowance)
        {
          transform = Transform.PlaneToPlane(source.FirstPlane, flat.SecondPlane);
          mappedCenter = source.Centroid;
          mappedCenter.Transform(transform);
          if (mappedCenter.DistanceTo(flat.Centroid) > allowance)
            continue;
        }
        yield return new FlatBoardLink
        {
          ObjectId = candidate.Id,
          SourceToFlat = transform
        };
      }
    }

    private static void Rollback(RhinoDoc doc, IEnumerable<PreparedReplacement> replacements)
    {
      foreach (var item in replacements.Reverse())
        doc.Objects.Replace(item.ObjectId, item.OriginalGeometry);
    }

    private sealed class PreparedReplacement
    {
      public Guid ObjectId { get; set; }
      public Guid SourceObjectId { get; set; }
      public Guid CounterpartId { get; set; }
      public Brep OriginalGeometry { get; set; }
      public Brep NewGeometry { get; set; }
      public bool IsFlatCopy { get; set; }
    }
  }
}
