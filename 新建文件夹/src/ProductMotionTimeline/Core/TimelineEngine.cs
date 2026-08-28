using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ProductMotionTimeline.Core
{
  internal static class TimelineEngine
  {
    public const string TrackUserStringKey = "ProductMotionTimeline.TrackId";
    private sealed class KeyClipboardGroup
    {
      public Guid SourceTrackId { get; set; }
      public List<Keyframe> Keys { get; } = new List<Keyframe>();
    }

    private static Keyframe _keyClipboard;
    private static readonly List<KeyClipboardGroup> _keyClipboardGroups = new List<KeyClipboardGroup>();
    private static bool _synchronizingRhinoSelection;

    public static event Action Changed;

    public static bool SynchronizingRhinoSelection => _synchronizingRhinoSelection;

    public static TimelineDocument Model(RhinoDoc doc)
    {
      return TimelineRepository.Get(doc);
    }

    public static IDisposable BeginUndoScope(RhinoDoc doc, string description)
    {
      return TimelineUndoManager.Begin(doc, description);
    }

    internal static void NotifyTimelineRestored(RhinoDoc doc)
    {
      Notify();
    }

    public static AnimationTrack AddTrack(RhinoDoc doc, InstanceObject instance)
    {
      if (doc == null || instance == null)
        return null;

      var model = Model(doc);
      var existingTrackText = instance.Attributes.GetUserString(TrackUserStringKey);
      var existingTrack = model.Tracks.FirstOrDefault(item =>
        item.ObjectId == instance.Id || item.Id.ToString("D") == existingTrackText);
      if (existingTrack != null)
      {
        model.SelectedTrackId = existingTrack.Id;
        RhinoApp.WriteLine("ProductMotion：该部件已有轨道，已切换到“{0}”。", existingTrack.Name);
        Notify();
        return existingTrack;
      }

      var track = new AnimationTrack
      {
        ObjectId = instance.Id,
        Name = string.IsNullOrWhiteSpace(instance.Attributes.Name)
          ? instance.InstanceDefinition.Name
          : instance.Attributes.Name,
        BaseTransform = instance.InstanceXform,
        PivotTransform = DefaultPivotTransform(doc, instance)
      };
      track.Keys.Add(new Keyframe
      {
        Frame = model.StartFrame,
        Pose = Pose.Identity,
        Interpolation = InterpolationMode.Smooth
      });

      using (TimelineUndoManager.Begin(doc, "添加 ProductMotion 动画轨道"))
      {
        model.Tracks.Add(track);
        model.SelectedTrackId = track.Id;
        SetTrackTag(doc, instance.Id, track.Id);
        SaveAndNotify(doc);
      }
      return track;
    }

    public static void SelectTrack(RhinoDoc doc, Guid trackId)
    {
      if (doc == null)
        return;
      var model = Model(doc);
      var track = model.FindTrack(trackId);
      if (track != null)
      {
        model.SelectedTrackId = trackId;
        _synchronizingRhinoSelection = true;
        try
        {
          doc.Objects.UnselectAll();
          var instance = ResolveInstance(doc, track);
          instance?.Select(true);
        }
        finally
        {
          _synchronizingRhinoSelection = false;
        }
        doc.Views.Redraw();
        Notify();
      }
    }

    public static bool SelectTrackFromRhinoObject(RhinoDoc doc, RhinoObject rhinoObject)
    {
      if (doc == null || rhinoObject == null || _synchronizingRhinoSelection)
        return false;
      var instance = rhinoObject as InstanceObject;
      var track = FindTrackForInstance(doc, instance);
      if (track == null)
        return false;
      var model = Model(doc);
      model.SelectedTrackId = track.Id;
      Notify();
      return true;
    }

    public static bool ReorderTrack(
      RhinoDoc doc,
      Guid trackId,
      Guid targetSiblingId,
      bool insertAfter)
    {
      var model = Model(doc);
      var track = model.FindTrack(trackId);
      var target = model.FindTrack(targetSiblingId);
      if (track == null || target == null || track.Id == target.Id ||
          track.ParentTrackId != target.ParentTrackId)
        return false;

      if (!model.Tracks.Remove(track))
        return false;
      var targetIndex = model.Tracks.IndexOf(target);
      if (targetIndex < 0)
      {
        model.Tracks.Add(track);
        return false;
      }
      model.Tracks.Insert(targetIndex + (insertAfter ? 1 : 0), track);
      model.SelectedTrackId = track.Id;
      SaveAndNotify(doc);
      RhinoApp.WriteLine(
        "ProductMotion：已调整轨道“{0}”的显示顺序；父子关系和关键帧保持不变。",
        track.Name);
      return true;
    }

    public static bool InsertOrUpdateKey(RhinoDoc doc, InterpolationMode interpolation)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      if (track == null)
        return false;

      var instance = ResolveInstance(doc, track);
      if (instance == null)
      {
        RhinoApp.WriteLine("ProductMotion：找不到该轨道绑定的动画部件，请使用 PMTRebind 重新绑定。");
        return false;
      }

      var poseCache = new Dictionary<Guid, Pose>();
      var evaluatedAngle = EvaluateEffectivePose(model, track, model.CurrentFrame, poseCache, new HashSet<Guid>()).AxisAngleDegrees;
      var independentTransform = instance.InstanceXform;
      if (track.ParentTrackId != Guid.Empty)
      {
        var parent = model.FindTrack(track.ParentTrackId);
        if (parent != null)
        {
          var worldCache = new Dictionary<Guid, Transform>();
          var parentWorld = EvaluateWorldTarget(model, parent, model.CurrentFrame, poseCache, worldCache, new HashSet<Guid>());
          Transform bindInverse;
          var parentDelta = Transform.Identity;
          if (track.ParentBindTransform.TryGetInverse(out bindInverse))
            parentDelta = parentWorld * bindInverse;
          Transform parentDeltaInverse;
          if (parentDelta.TryGetInverse(out parentDeltaInverse))
            independentTransform = parentDeltaInverse * independentTransform;
        }
      }

      Pose pose;
      if (!track.TryCapturePose(independentTransform, evaluatedAngle, out pose))
      {
        RhinoApp.WriteLine("ProductMotion：无法分解当前变换；请避免剪切或零缩放后再卡帧。");
        return false;
      }

      using (TimelineUndoManager.Begin(doc, "插入/更新 ProductMotion 关键帧"))
      {
        track.UpsertKey(model.CurrentFrame, pose, interpolation);
        SaveAndNotify(doc);
      }
      return true;
    }

    public static bool DeleteKey(RhinoDoc doc)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      if (track == null || track.FindKey(model.CurrentFrame) == null)
        return false;
      using (TimelineUndoManager.Begin(doc, "删除 ProductMotion 关键帧"))
      {
        track.DeleteKey(model.CurrentFrame);
        TimelineRepository.Save(doc);
        ApplyFrame(doc, model.CurrentFrame, false);
        Notify();
      }
      return true;
    }

    public static bool CopyKey(RhinoDoc doc)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      var key = track?.FindKey(model.CurrentFrame);
      if (key == null)
        return false;
      _keyClipboard = key.Clone();
      _keyClipboardGroups.Clear();
      var group = new KeyClipboardGroup { SourceTrackId = track.Id };
      group.Keys.Add(key.Clone());
      _keyClipboardGroups.Add(group);
      return true;
    }

    public static bool PasteKey(RhinoDoc doc)
    {
      if (_keyClipboard == null)
        return false;
      var model = Model(doc);
      var track = model.SelectedTrack;
      if (track == null)
        return false;
      using (TimelineUndoManager.Begin(doc, "粘贴 ProductMotion 关键帧"))
      {
        track.UpsertKey(model.CurrentFrame, _keyClipboard.Pose, _keyClipboard.Interpolation);
        SaveAndNotify(doc);
      }
      return true;
    }

    public static int CopyKeys(RhinoDoc doc, IEnumerable<KeySelection> selections)
    {
      var model = Model(doc);
      var valid = (selections ?? Enumerable.Empty<KeySelection>())
        .Where(selection => model.FindTrack(selection.TrackId)?.FindKey(selection.Frame) != null)
        .Distinct()
        .ToList();
      if (valid.Count == 0)
        return 0;

      _keyClipboardGroups.Clear();
      foreach (var source in valid.GroupBy(selection => selection.TrackId))
      {
        var track = model.FindTrack(source.Key);
        var group = new KeyClipboardGroup { SourceTrackId = source.Key };
        foreach (var selection in source.OrderBy(item => item.Frame))
          group.Keys.Add(track.FindKey(selection.Frame).Clone());
        _keyClipboardGroups.Add(group);
      }
      _keyClipboard = _keyClipboardGroups[0].Keys[0].Clone();
      return valid.Count;
    }

    public static List<Guid> SelectedRhinoTrackIds(RhinoDoc doc)
    {
      var result = new List<Guid>();
      if (doc == null)
        return result;
      var model = Model(doc);
      foreach (var track in model.OrderedTracks())
      {
        var instance = ResolveInstance(doc, track);
        if (instance != null && instance.IsSelected(false) > 0)
          result.Add(track.Id);
      }
      if (result.Count == 0 && model.SelectedTrack != null)
        result.Add(model.SelectedTrack.Id);
      return result;
    }

    public static KeyPasteResult PasteCopiedKeys(RhinoDoc doc, IEnumerable<Guid> targetTrackIds)
    {
      var result = new KeyPasteResult();
      if (doc == null || _keyClipboardGroups.Count == 0)
      {
        result.ErrorMessage = "请先复制关键帧。";
        return result;
      }

      var model = Model(doc);
      var targets = (targetTrackIds ?? Enumerable.Empty<Guid>())
        .Distinct()
        .Select(model.FindTrack)
        .Where(track => track != null)
        .ToList();
      if (targets.Count == 0)
      {
        result.ErrorMessage = "请先选择目标轨道或目标物体。";
        return result;
      }
      if (_keyClipboardGroups.Count > 1 && _keyClipboardGroups.Count != targets.Count)
      {
        result.ErrorMessage = "复制内容来自多条轨道；请选择相同数量的目标物体后再粘贴。";
        return result;
      }

      using (TimelineUndoManager.Begin(doc, "批量粘贴 ProductMotion 关键帧"))
      {
        var sourceStart = _keyClipboardGroups.SelectMany(group => group.Keys).Min(key => key.Frame);
        var maxFrame = model.EndFrame;
        for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
          var target = targets[targetIndex];
          var source = _keyClipboardGroups.Count == 1
            ? _keyClipboardGroups[0]
            : _keyClipboardGroups[targetIndex];
          foreach (var sourceKey in source.Keys)
          {
            var targetFrame = model.CurrentFrame + sourceKey.Frame - sourceStart;
            if (targetFrame < model.StartFrame)
              continue;
            maxFrame = Math.Max(maxFrame, targetFrame);
            var existing = target.FindKey(targetFrame);
            if (existing != null)
              result.OverwrittenCount++;
            target.UpsertKey(targetFrame, sourceKey.Pose, sourceKey.Interpolation);
            result.PastedCount++;
            result.PastedSelections.Add(new KeySelection
            {
              TrackId = target.Id,
              Frame = targetFrame
            });
          }
        }

        if (result.PastedCount > 0)
        {
          model.EndFrame = maxFrame;
          SaveAndNotify(doc);
          ApplyFrame(doc, model.CurrentFrame, false);
        }
      }
      return result;
    }

    public static bool MoveKey(RhinoDoc doc, Guid trackId, int oldFrame, int newFrame)
    {
      var result = MoveKeys(
        doc,
        new[] { new KeySelection { TrackId = trackId, Frame = oldFrame } },
        newFrame - oldFrame);
      return string.IsNullOrWhiteSpace(result.ErrorMessage);
    }

    public static KeyMoveResult MoveKeys(
      RhinoDoc doc,
      IEnumerable<KeySelection> selections,
      int requestedDelta)
    {
      var result = new KeyMoveResult();
      if (doc == null)
      {
        result.ErrorMessage = "没有活动Rhino文档。";
        return result;
      }

      var model = Model(doc);
      var valid = (selections ?? Enumerable.Empty<KeySelection>())
        .Where(selection => model.FindTrack(selection.TrackId)?.FindKey(selection.Frame) != null)
        .Distinct()
        .ToList();
      if (valid.Count == 0)
      {
        result.ErrorMessage = "没有可移动的关键帧。";
        return result;
      }

      var minimumFrame = valid.Min(selection => selection.Frame);
      var maximumFrame = valid.Max(selection => selection.Frame);
      var delta = Math.Max(
        model.StartFrame - minimumFrame,
        Math.Min(model.EndFrame - maximumFrame, requestedDelta));
      result.AppliedDelta = delta;

      using (TimelineUndoManager.Begin(doc, "整体移动 ProductMotion 关键帧"))
      {
        foreach (var trackGroup in valid.GroupBy(selection => selection.TrackId))
        {
          var track = model.FindTrack(trackGroup.Key);
          var moving = trackGroup
            .Select(selection => new
            {
              Selection = selection,
              Key = track.FindKey(selection.Frame)
            })
            .Where(item => item.Key != null)
            .ToList();
          var movingKeys = new HashSet<Keyframe>(moving.Select(item => item.Key));
          var targetFrames = new HashSet<int>(moving.Select(item => item.Selection.Frame + delta));
          var collisions = track.Keys
            .Where(key => targetFrames.Contains(key.Frame) && !movingKeys.Contains(key))
            .ToList();
          foreach (var collision in collisions)
            track.Keys.Remove(collision);
          result.OverwrittenCount += collisions.Count;

          foreach (var item in moving)
          {
            item.Key.Frame = item.Selection.Frame + delta;
            result.MovedSelections.Add(new KeySelection
            {
              TrackId = track.Id,
              Frame = item.Key.Frame
            });
            result.MovedCount++;
          }
          track.SortKeys();
        }

        TimelineRepository.Save(doc);
        Notify();
      }
      return result;
    }

    public static bool SetPivot(RhinoDoc doc, Point3d point)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      if (track == null)
        return false;

      var plane = Plane.WorldXY;
      var view = doc.Views.ActiveView;
      if (view != null)
      {
        plane = view.ActiveViewport.ConstructionPlane();
        plane.Origin = point;
      }
      else
      {
        plane.Origin = point;
      }

      return SetPivotPlane(doc, track, plane);
    }

    public static bool SetPivotPlane(RhinoDoc doc, AnimationTrack track, Plane plane)
    {
      if (doc == null || track == null || !plane.IsValid)
        return false;
      var pivot = Transform.PlaneToPlane(Plane.WorldXY, plane);
      if (!track.TryRebasePivot(pivot))
        return false;
      track.RotationAxis = RotationAxis.Z;
      SaveAndNotify(doc);
      return true;
    }

    public static bool TryAutoSetPivot(RhinoDoc doc, AnimationTrack track, out string description)
    {
      description = "未找到可靠的圆孔，已保留当前轴心";
      var instance = ResolveInstance(doc, track);
      AxisDetectionResult detection;
      if (instance == null || !AxisDetector.TryDetect(doc, instance, out detection))
        return false;
      if (!SetPivotPlane(doc, track, detection.Plane))
        return false;
      description = string.Format(
        "已识别轴孔：半径 {0:0.###}，共轴圆边 {1} 条",
        detection.Radius,
        detection.MatchingCircularEdges);
      return true;
    }

    public static bool RebindSelectedTrack(RhinoDoc doc, InstanceObject instance)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      if (track == null || instance == null)
        return false;

      track.ObjectId = instance.Id;
      track.BaseTransform = instance.InstanceXform;
      SetTrackTag(doc, instance.Id, track.Id);
      SaveAndNotify(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
      return true;
    }

    public static bool DeleteSelectedTrack(RhinoDoc doc)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      if (track == null)
        return false;

      ApplyAbsolute(doc, track, track.BaseTransform);
      var instance = ResolveInstance(doc, track);
      if (instance != null)
        SetTrackTag(doc, instance.Id, Guid.Empty);
      foreach (var child in model.Tracks.Where(item => item.ParentTrackId == track.Id))
      {
        child.ParentTrackId = Guid.Empty;
        child.ParentBindTransform = Transform.Identity;
      }
      model.Constraints.RemoveAll(constraint =>
        constraint.DriverTrackId == track.Id || constraint.DrivenTrackId == track.Id);
      model.Tracks.Remove(track);
      model.SelectedTrackId = model.Tracks.Count > 0 ? model.Tracks[0].Id : Guid.Empty;
      TimelineRepository.Save(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
      return true;
    }

    public static bool SetParent(RhinoDoc doc, Guid childTrackId, Guid parentTrackId)
    {
      var model = Model(doc);
      var child = model.FindTrack(childTrackId);
      var parent = model.FindTrack(parentTrackId);
      if (child == null || parent == null || model.WouldCreateParentCycle(childTrackId, parentTrackId))
      {
        RhinoApp.WriteLine("ProductMotion：不能建立该父子关系，请检查是否选择了自身或形成循环层级。");
        return false;
      }

      var poseCache = new Dictionary<Guid, Pose>();
      var worldCache = new Dictionary<Guid, Transform>();
      child.ParentTrackId = parent.Id;
      child.ParentBindTransform = EvaluateWorldTarget(
        model,
        parent,
        model.CurrentFrame,
        poseCache,
        worldCache,
        new HashSet<Guid>());
      SaveAndNotify(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
      RhinoApp.WriteLine("ProductMotion：已将“{0}”设为“{1}”的父级。", parent.Name, child.Name);
      return true;
    }

    public static bool ClearParent(RhinoDoc doc, Guid childTrackId)
    {
      var model = Model(doc);
      var child = model.FindTrack(childTrackId);
      if (child == null || child.ParentTrackId == Guid.Empty)
        return false;
      child.ParentTrackId = Guid.Empty;
      child.ParentBindTransform = Transform.Identity;
      SaveAndNotify(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
      return true;
    }

    public static bool AddMechanicalConstraint(
      RhinoDoc doc,
      Guid driverTrackId,
      Guid drivenTrackId,
      MechanicalConstraintType type,
      int driverTeeth,
      int drivenTeeth,
      double phaseOffsetDegrees)
    {
      return AddMechanicalConstraint(
        doc,
        driverTrackId,
        drivenTrackId,
        type,
        driverTeeth,
        drivenTeeth,
        phaseOffsetDegrees,
        0.0,
        20.0);
    }

    public static bool AddMechanicalConstraint(
      RhinoDoc doc,
      Guid driverTrackId,
      Guid drivenTrackId,
      MechanicalConstraintType type,
      int driverTeeth,
      int drivenTeeth,
      double phaseOffsetDegrees,
      double module,
      double pressureAngleDegrees,
      double phaseOffsetDistance = 0.0,
      RotationAxis drivenLinearAxis = RotationAxis.X,
      double directionMultiplier = 1.0,
      int referenceTeeth = 0)
    {
      var model = Model(doc);
      var driver = model.FindTrack(driverTrackId);
      var driven = model.FindTrack(drivenTrackId);
      if (driver == null || driven == null || driverTeeth < 1 || drivenTeeth < 1)
        return false;
      if (model.WouldCreateConstraintCycle(driverTrackId, drivenTrackId))
      {
        RhinoApp.WriteLine("ProductMotion：不能建立该传动关系，驱动链会形成循环。");
        return false;
      }

      using (TimelineUndoManager.Begin(doc, "建立 ProductMotion 机械传动"))
      {
        model.Constraints.RemoveAll(item => item.DrivenTrackId == drivenTrackId);
        model.Constraints.Add(new MechanicalConstraint
        {
          DriverTrackId = driverTrackId,
          DrivenTrackId = drivenTrackId,
          Type = type,
          DriverTeeth = driverTeeth,
          DrivenTeeth = drivenTeeth,
          ReferenceTeeth = Math.Max(0, referenceTeeth),
          Module = Math.Max(0.0, module),
          PressureAngleDegrees = Math.Max(1.0, pressureAngleDegrees),
          PhaseOffsetDegrees = phaseOffsetDegrees,
          PhaseOffsetDistance = phaseOffsetDistance,
          DrivenLinearAxis = drivenLinearAxis,
          DirectionMultiplier = directionMultiplier < 0.0 ? -1.0 : 1.0,
          Enabled = true
        });
        SaveAndNotify(doc);
        doc.Views.Redraw();
      }
      RhinoApp.WriteLine(
        "ProductMotion：已建立“{0}”→“{1}”传动，角速度比 {2:0.###}；绑定过程未移动零件。",
        driver.Name,
        driven.Name,
        model.ConstraintForDriven(drivenTrackId).SignedRatio);
      return true;
    }

    public static bool UpdateMechanicalConstraint(
      RhinoDoc doc,
      Guid constraintId,
      MechanicalConstraintType type,
      int driverTeeth,
      int drivenTeeth,
      double module,
      double pressureAngleDegrees,
      double phaseOffsetDegrees,
      double phaseOffsetDistance = 0.0,
      RotationAxis drivenLinearAxis = RotationAxis.X,
      double directionMultiplier = 1.0)
    {
      var model = Model(doc);
      var constraint = model.Constraints.FirstOrDefault(item => item.Id == constraintId);
      if (constraint == null || driverTeeth < 1 || drivenTeeth < 1 || module < 0.0)
        return false;
      using (TimelineUndoManager.Begin(doc, "编辑 ProductMotion 机械传动"))
      {
        constraint.Type = type;
        constraint.DriverTeeth = driverTeeth;
        constraint.DrivenTeeth = drivenTeeth;
        constraint.Module = module;
        constraint.PressureAngleDegrees = Math.Max(1.0, pressureAngleDegrees);
        constraint.PhaseOffsetDegrees = phaseOffsetDegrees;
        constraint.PhaseOffsetDistance = phaseOffsetDistance;
        constraint.DrivenLinearAxis = drivenLinearAxis;
        constraint.DirectionMultiplier = directionMultiplier < 0.0 ? -1.0 : 1.0;
        SaveAndNotify(doc);
        ApplyFrame(doc, model.CurrentFrame, false);
      }
      return true;
    }

    public static bool DeleteMechanicalConstraint(RhinoDoc doc, Guid constraintId)
    {
      var model = Model(doc);
      if (model.Constraints.All(item => item.Id != constraintId))
        return false;
      using (TimelineUndoManager.Begin(doc, "删除 ProductMotion 机械传动"))
      {
        model.Constraints.RemoveAll(item => item.Id == constraintId);
        SaveAndNotify(doc);
        ApplyFrame(doc, model.CurrentFrame, false);
      }
      return true;
    }

    public static MechanicalValidationResult ValidateMechanicalConstraint(
      RhinoDoc doc,
      MechanicalConstraint constraint)
    {
      var result = new MechanicalValidationResult();
      if (doc == null || constraint == null)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "传动数据不完整";
        return result;
      }

      var model = Model(doc);
      var driver = model.FindTrack(constraint.DriverTrackId);
      var driven = model.FindTrack(constraint.DrivenTrackId);
      if (driver == null || driven == null)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "找不到主动件或从动件";
        return result;
      }

      var driverOrigin = PivotOrigin(driver);
      var drivenOrigin = PivotOrigin(driven);
      var driverAxis = PivotAxis(driver);
      var drivenAxis = PivotAxis(driven);
      if (constraint.IsPlanetary)
        return ValidatePlanetaryConstraint(
          doc, model, constraint, driver, driven,
          driverOrigin, drivenOrigin, driverAxis, drivenAxis);
      if (constraint.Type == MechanicalConstraintType.SameShaft)
      {
        if (Math.Abs(driverAxis * drivenAxis) < Math.Cos(Math.PI / 90.0))
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = "同轴刚性失败：两转轴不平行（偏差超过 2°）";
          return result;
        }

        var sameShaftDelta = drivenOrigin - driverOrigin;
        result.ActualCenterDistance =
          (sameShaftDelta - driverAxis * (sameShaftDelta * driverAxis)).Length;
        var sameShaftTolerance = Math.Max(doc.ModelAbsoluteTolerance * 5.0, 1e-6);
        if (result.ActualCenterDistance > sameShaftTolerance)
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = string.Format(
            "同轴刚性失败：两轴线偏心 {0:0.###}，允许 {1:0.###}",
            result.ActualCenterDistance,
            sameShaftTolerance);
          return result;
        }

        result.Message = "同轴刚性有效：角速度 1:1，相位固定";
        return result;
      }
      if (constraint.Type == MechanicalConstraintType.RackPinion)
      {
        if (constraint.Module <= 0.0)
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = "齿轮-齿条传动必须设置模数";
          return result;
        }
        result.Message = string.Format(
          "齿轮-齿条有效：每转移动 {0:0.###}",
          Math.PI * constraint.Module * constraint.DriverTeeth);
        return result;
      }

      if (constraint.Type == MechanicalConstraintType.BevelGear)
      {
        var cross = Vector3d.CrossProduct(driverAxis, drivenAxis);
        if (!cross.Unitize())
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = "锥齿轮两轴不能平行";
          return result;
        }
        var axisDistance = Math.Abs((drivenOrigin - driverOrigin) * cross);
        var bevelTolerance = Math.Max(doc.ModelAbsoluteTolerance * 5.0, constraint.Module * 0.05);
        if (axisDistance > bevelTolerance)
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = string.Format("锥齿轮两轴不相交，轴线最短距离 {0:0.###}", axisDistance);
          return result;
        }
        result.Message = "锥齿轮轴线相交，传动比有效";
        return result;
      }

      if (Math.Abs(driverAxis * drivenAxis) < Math.Cos(Math.PI / 90.0))
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "两转轴不平行（偏差超过 2°）";
        return result;
      }

      var delta = drivenOrigin - driverOrigin;
      result.ActualCenterDistance = (delta - driverAxis * (delta * driverAxis)).Length;
      if (constraint.Type == MechanicalConstraintType.Belt)
      {
        result.Message = "转轴平行，皮带传动比有效";
        return result;
      }

      if (constraint.Type == MechanicalConstraintType.InternalGear)
      {
        GearParameters driverGear;
        GearParameters drivenGear;
        var driverIsGenerated = GearPartMetadata.TryRead(ResolveInstance(doc, driver), out driverGear);
        var drivenIsGenerated = GearPartMetadata.TryRead(ResolveInstance(doc, driven), out drivenGear);
        if (driverIsGenerated && drivenIsGenerated)
        {
          var driverIsInternal = driverGear.Type == GearPartType.Internal;
          var drivenIsInternal = drivenGear.Type == GearPartType.Internal;
          if (driverIsInternal == drivenIsInternal)
          {
            result.Severity = ValidationSeverity.Error;
            result.Message = "内啮合需要一个内齿圈和一个外齿轮";
            return result;
          }
          var internalTeeth = driverIsInternal ? constraint.DriverTeeth : constraint.DrivenTeeth;
          var externalTeeth = driverIsInternal ? constraint.DrivenTeeth : constraint.DriverTeeth;
          if (internalTeeth <= externalTeeth)
          {
            result.Severity = ValidationSeverity.Error;
            result.Message = "内齿圈齿数必须大于配对的外齿轮";
            return result;
          }
        }
        else if (constraint.DriverTeeth == constraint.DrivenTeeth)
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = "内啮合两齿轮齿数不能相同";
          return result;
        }
      }
      if (constraint.Module <= 0.0)
      {
        result.Severity = ValidationSeverity.Warning;
        result.Message = "未设置模数：已校验轴向，仅按齿数比动画";
        return result;
      }

      result.ExpectedCenterDistance = constraint.Type == MechanicalConstraintType.ExternalGear ||
                                      constraint.Type == MechanicalConstraintType.HelicalGear
        ? constraint.Module * (constraint.DriverTeeth + constraint.DrivenTeeth) * 0.5
        : constraint.Module * Math.Abs(constraint.DrivenTeeth - constraint.DriverTeeth) * 0.5;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance * 5.0, constraint.Module * 0.05);
      var error = Math.Abs(result.ActualCenterDistance - result.ExpectedCenterDistance);
      if (error > tolerance)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = string.Format(
          "中心距 {0:0.###}，按模数应为 {1:0.###}，差 {2:0.###}",
          result.ActualCenterDistance,
          result.ExpectedCenterDistance,
          error);
        return result;
      }

      if (constraint.Type == MechanicalConstraintType.HelicalGear)
      {
        GearParameters driverGear;
        GearParameters drivenGear;
        var driverInstance = ResolveInstance(doc, driver);
        var drivenInstance = ResolveInstance(doc, driven);
        if (GearPartMetadata.TryRead(driverInstance, out driverGear) &&
            GearPartMetadata.TryRead(drivenInstance, out drivenGear) &&
            Math.Sign(driverGear.HelixAngleDegrees) == Math.Sign(drivenGear.HelixAngleDegrees))
        {
          result.Severity = ValidationSeverity.Warning;
          result.Message = "中心距正确；平行轴外啮合斜齿轮建议使用相反旋向";
          return result;
        }
      }

      if (Math.Min(constraint.DriverTeeth, constraint.DrivenTeeth) < 17 &&
          constraint.PressureAngleDegrees >= 19.0 &&
          constraint.PressureAngleDegrees <= 21.0)
      {
        result.Severity = ValidationSeverity.Warning;
        result.Message = "中心距正确；小齿轮少于 17 齿，20°标准齿形可能根切";
        return result;
      }

      result.Message = string.Format(
        "真实啮合通过：中心距 {0:0.###}，模数 {1:0.###}",
        result.ActualCenterDistance,
        constraint.Module);
      return result;
    }

    private static MechanicalValidationResult ValidatePlanetaryConstraint(
      RhinoDoc doc,
      TimelineDocument model,
      MechanicalConstraint constraint,
      AnimationTrack driver,
      AnimationTrack driven,
      Point3d driverOrigin,
      Point3d drivenOrigin,
      Vector3d driverAxis,
      Vector3d drivenAxis)
    {
      var result = new MechanicalValidationResult();
      if (Math.Abs(driverAxis * drivenAxis) < Math.Cos(Math.PI / 90.0))
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "行星机构转轴不平行（偏差超过 2°）";
        return result;
      }
      if (constraint.DriverTeeth < 1 || constraint.DrivenTeeth < 1)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "行星机构齿数数据不完整";
        return result;
      }

      var delta = drivenOrigin - driverOrigin;
      result.ActualCenterDistance = (delta - driverAxis * (delta * driverAxis)).Length;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance * 5.0, constraint.Module * 0.05);
      if (constraint.Type == MechanicalConstraintType.PlanetaryCarrier)
      {
        if (result.ActualCenterDistance > Math.Max(tolerance, 1e-6))
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = string.Format(
            "行星架与输入件不同轴，偏心 {0:0.###}", result.ActualCenterDistance);
          return result;
        }
        result.Message = string.Format(
          "Willis 行星架关系有效：输出/输入 {0:0.####}", constraint.SignedRatio);
        return result;
      }

      if (constraint.Type == MechanicalConstraintType.PlanetaryRingFixedCarrier)
      {
        if (constraint.ReferenceTeeth < 1 ||
            constraint.DrivenTeeth != constraint.DriverTeeth + 2 * constraint.ReferenceTeeth)
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = "固定行星架模式的齿数不满足 Zr=Zs+2Zp";
          return result;
        }
        if (result.ActualCenterDistance > Math.Max(tolerance, 1e-6))
        {
          result.Severity = ValidationSeverity.Error;
          result.Message = string.Format(
            "固定行星架模式的太阳轮和内齿圈不同轴，偏心 {0:0.###}",
            result.ActualCenterDistance);
          return result;
        }
        result.Message = string.Format(
          "固定行星架关系有效：内齿圈/太阳轮 {0:0.####}（反向）",
          constraint.SignedRatio);
        return result;
      }

      if (constraint.ReferenceTeeth < 1)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "行星轮约束缺少固定件齿数";
        return result;
      }
      var toothRelationValid = constraint.Type == MechanicalConstraintType.PlanetaryPlanetExternalInput
        ? constraint.ReferenceTeeth == constraint.DriverTeeth + 2 * constraint.DrivenTeeth
        : constraint.DriverTeeth == constraint.ReferenceTeeth + 2 * constraint.DrivenTeeth;
      if (!toothRelationValid)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "行星轮齿数关系不满足 Zr=Zs+2Zp";
        return result;
      }
      if (constraint.Module <= 0.0)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "行星机构必须设置统一模数";
        return result;
      }

      result.ExpectedCenterDistance = constraint.Type == MechanicalConstraintType.PlanetaryPlanetExternalInput
        ? constraint.Module * (constraint.DriverTeeth + constraint.DrivenTeeth) * 0.5
        : constraint.Module * (constraint.DriverTeeth - constraint.DrivenTeeth) * 0.5;
      if (Math.Abs(result.ActualCenterDistance - result.ExpectedCenterDistance) > tolerance)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = string.Format(
          "行星轮中心距 {0:0.###}，应为 {1:0.###}",
          result.ActualCenterDistance,
          result.ExpectedCenterDistance);
        return result;
      }

      var carrier = model.FindTrack(driven.ParentTrackId);
      var carrierConstraint = carrier == null ? null : model.ConstraintForDriven(carrier.Id);
      if (carrier == null || carrierConstraint == null ||
          carrierConstraint.Type != MechanicalConstraintType.PlanetaryCarrier ||
          carrierConstraint.DriverTrackId != driver.Id)
      {
        result.Severity = ValidationSeverity.Error;
        result.Message = "行星轮缺少正确的行星架父级或 Willis 行星架约束";
        return result;
      }

      result.Message = string.Format(
        "Willis 行星轮关系有效：相对行星架比例 {0:0.####}，中心距 {1:0.###}",
        constraint.SignedRatio,
        result.ActualCenterDistance);
      return result;
    }

    public static Point3d PivotOrigin(AnimationTrack track)
    {
      var origin = Point3d.Origin;
      origin.Transform(track.PivotTransform);
      return origin;
    }

    public static Point3d DisplayedPivotOrigin(RhinoDoc doc, AnimationTrack track)
    {
      var origin = PivotOrigin(track);
      var instance = ResolveInstance(doc, track);
      Transform baseInverse;
      if (instance == null || !AnimationMath.SanitizeAffine(track.BaseTransform).TryGetInverse(out baseInverse))
        return origin;
      var delta = AnimationMath.SanitizeAffine(instance.InstanceXform) *
                  AnimationMath.SanitizeAffine(baseInverse);
      origin.Transform(AnimationMath.SanitizeAffine(delta));
      return origin;
    }

    public static Vector3d PivotAxis(AnimationTrack track)
    {
      var axis = Vector3d.ZAxis;
      axis.Transform(track.PivotTransform);
      if (!axis.Unitize())
        return Vector3d.ZAxis;
      return axis;
    }

    public static bool DeleteConstraintForDriven(RhinoDoc doc, Guid drivenTrackId)
    {
      var model = Model(doc);
      if (model.Constraints.All(item => item.DrivenTrackId != drivenTrackId))
        return false;
      using (TimelineUndoManager.Begin(doc, "删除 ProductMotion 机械传动"))
      {
        model.Constraints.RemoveAll(item => item.DrivenTrackId == drivenTrackId);
        SaveAndNotify(doc);
        ApplyFrame(doc, model.CurrentFrame, false);
      }
      return true;
    }

    public static void ApplyFrame(RhinoDoc doc, int frame, bool persist)
    {
      if (doc == null)
        return;
      var model = Model(doc);
      model.CurrentFrame = Math.Max(model.StartFrame, Math.Min(model.EndFrame, frame));

      var poseCache = new Dictionary<Guid, Pose>();
      var worldCache = new Dictionary<Guid, Transform>();
      foreach (var track in model.OrderedTracks().Where(item => item.Enabled))
      {
        var target = EvaluateWorldTarget(
          model,
          track,
          model.CurrentFrame,
          poseCache,
          worldCache,
          new HashSet<Guid>());
        ApplyAbsolute(doc, track, target);
      }

      doc.Views.Redraw();
      if (persist)
        TimelineRepository.Save(doc);
      Notify();
    }

    public static int RepairNonAffineTrackTransforms(RhinoDoc doc)
    {
      if (doc == null)
        return 0;
      var model = Model(doc);
      var needsRepair = model.OrderedTracks().Any(track =>
        !AnimationMath.HasExactAffineBottomRow(track.BaseTransform) ||
        !AnimationMath.HasExactAffineBottomRow(track.ParentBindTransform) ||
        !AnimationMath.HasExactAffineBottomRow(track.PivotTransform) ||
        (ResolveInstance(doc, track) != null &&
         !AnimationMath.HasExactAffineBottomRow(ResolveInstance(doc, track).InstanceXform)));
      if (!needsRepair)
        return 0;

      var repaired = 0;
      var metadataRepaired = 0;
      using (TimelineUndoManager.Begin(doc, "修复 ProductMotion 动画块变换"))
      {
        foreach (var track in model.OrderedTracks())
        {
          if (!AnimationMath.HasExactAffineBottomRow(track.BaseTransform))
          {
            track.BaseTransform = AnimationMath.SanitizeAffine(track.BaseTransform);
            metadataRepaired++;
          }
          if (!AnimationMath.HasExactAffineBottomRow(track.ParentBindTransform))
          {
            track.ParentBindTransform = AnimationMath.SanitizeAffine(track.ParentBindTransform);
            metadataRepaired++;
          }
          if (!AnimationMath.HasExactAffineBottomRow(track.PivotTransform))
          {
            track.PivotTransform = AnimationMath.SanitizeAffine(track.PivotTransform);
            metadataRepaired++;
          }
          var instance = ResolveInstance(doc, track);
          if (instance == null || AnimationMath.HasExactAffineBottomRow(instance.InstanceXform))
            continue;
          if (ReplaceInstanceTransform(
            doc,
            track,
            instance,
            AnimationMath.SanitizeAffine(instance.InstanceXform)))
            repaired++;
        }
        TimelineRepository.Save(doc);
        doc.Views.Redraw();
        Notify();
        RhinoApp.WriteLine(
          "ProductMotion：已修复 {0} 个动画块的非仿射变换残差，保持当前摆放位置不变。",
          repaired);
      }
      return repaired;
    }

    public static bool PrepareUnkeyedTrackPlacement(RhinoDoc doc, AnimationTrack track)
    {
      if (doc == null || track == null)
        return false;
      var model = Model(doc);
      if (track.ParentTrackId != Guid.Empty || model.ConstraintForDriven(track.Id) != null ||
          !HasOnlyInitialIdentityKey(model, track))
        return false;

      var instance = ResolveInstance(doc, track);
      if (instance == null)
        return false;
      var rawCurrent = instance.InstanceXform;
      var current = AnimationMath.SanitizeAffine(rawCurrent);
      var baseTransform = AnimationMath.SanitizeAffine(track.BaseTransform);
      if (AnimationMath.HasExactAffineBottomRow(rawCurrent) &&
          AnimationMath.HasExactAffineBottomRow(track.BaseTransform) &&
          AnimationMath.AlmostEqual(current, baseTransform))
        return false;
      if (!AnimationMath.HasExactAffineBottomRow(rawCurrent) &&
          !ReplaceInstanceTransform(doc, track, instance, current))
        return false;

      Transform baseInverse;
      if (!baseTransform.TryGetInverse(out baseInverse))
        return false;
      var placementDelta = current * baseInverse;
      track.BaseTransform = current;
      track.PivotTransform = AnimationMath.SanitizeAffine(
        placementDelta * AnimationMath.SanitizeAffine(track.PivotTransform));
      TimelineRepository.Save(doc);
      Notify();
      return true;
    }

    private static bool HasOnlyInitialIdentityKey(TimelineDocument model, AnimationTrack track)
    {
      if (model == null || track == null || track.Keys.Count != 1 ||
          track.Keys[0].Frame != model.StartFrame)
        return false;
      var pose = track.Keys[0].Pose;
      if (pose == null)
        return false;
      const double tolerance = 1e-9;
      return pose.Translation.Length <= tolerance &&
             Math.Abs(pose.Scale.X - 1.0) <= tolerance &&
             Math.Abs(pose.Scale.Y - 1.0) <= tolerance &&
             Math.Abs(pose.Scale.Z - 1.0) <= tolerance &&
             Math.Abs(pose.AxisAngleDegrees) <= tolerance &&
             Math.Abs(pose.Rotation.X) <= tolerance &&
             Math.Abs(pose.Rotation.Y) <= tolerance &&
             Math.Abs(pose.Rotation.Z) <= tolerance &&
             Math.Abs(Math.Abs(pose.Rotation.W) - 1.0) <= tolerance;
    }

    public static void Persist(RhinoDoc doc)
    {
      TimelineRepository.Save(doc);
    }

    public static void UpdateSettings(RhinoDoc doc, int start, int end, int fps, bool loop)
    {
      var model = Model(doc);
      model.StartFrame = start;
      model.EndFrame = end;
      model.FramesPerSecond = fps;
      model.LoopPlayback = loop;
      model.ClampSettings();
      SaveAndNotify(doc);
    }

    public static void UpdateTemplatePlacement(
      RhinoDoc doc,
      TemplatePlacementMode placement,
      int gapFrames)
    {
      var model = Model(doc);
      model.TemplatePlacement = placement;
      model.TemplateGapFrames = Math.Max(0, gapFrames);
      model.ClampSettings();
      SaveAndNotify(doc);
    }

    public static int TemplateStartFrame(RhinoDoc doc, params AnimationTrack[] tracks)
    {
      var model = Model(doc);
      if (model.TemplatePlacement == TemplatePlacementMode.CurrentFrame)
        return model.CurrentFrame;
      IEnumerable<AnimationTrack> candidates = model.TemplatePlacement == TemplatePlacementMode.AfterAllActions
        ? model.Tracks
        : tracks?.Where(track => track != null) ?? Enumerable.Empty<AnimationTrack>();
      var lastFrame = candidates
        .SelectMany(track => track.Keys)
        .Select(key => key.Frame)
        .DefaultIfEmpty(model.StartFrame)
        .Max();
      return lastFrame <= model.StartFrame
        ? model.StartFrame
        : lastFrame + model.TemplateGapFrames;
    }

    public static bool UpdateCurrentKeyRotationChannel(RhinoDoc doc, RotationAxis axis, double angleDegrees)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      var key = track?.FindKey(model.CurrentFrame);
      if (track == null || key == null)
        return false;
      using (TimelineUndoManager.Begin(doc, "编辑 ProductMotion 关键帧旋转"))
      {
        track.RotationAxis = axis;
        key.Pose.AxisAngleDegrees = angleDegrees;
        TimelineRepository.Save(doc);
        ApplyFrame(doc, model.CurrentFrame, false);
        Notify();
      }
      return true;
    }

    public static bool UpdateCurrentKeyPoseChannels(
      RhinoDoc doc,
      Vector3d translation,
      Vector3d scale,
      RotationAxis axis,
      double angleDegrees)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      var key = track?.FindKey(model.CurrentFrame);
      if (track == null || key == null)
        return false;
      if (Math.Abs(scale.X) < 1e-6 || Math.Abs(scale.Y) < 1e-6 || Math.Abs(scale.Z) < 1e-6)
        return false;

      using (TimelineUndoManager.Begin(doc, "编辑 ProductMotion 关键帧属性"))
      {
        track.RotationAxis = axis;
        key.Pose.Translation = translation;
        key.Pose.Scale = scale;
        if (model.ConstraintForDriven(track.Id) == null)
          key.Pose.AxisAngleDegrees = angleDegrees;
        TimelineRepository.Save(doc);
        ApplyFrame(doc, model.CurrentFrame, false);
        Notify();
      }
      return true;
    }

    public static bool UpdateCurrentKeyInterpolation(
      RhinoDoc doc,
      InterpolationMode interpolation)
    {
      var model = Model(doc);
      var key = model.SelectedTrack?.FindKey(model.CurrentFrame);
      if (key == null)
        return false;
      using (TimelineUndoManager.Begin(doc, "编辑 ProductMotion 关键帧插值"))
      {
        key.Interpolation = interpolation;
        TimelineRepository.Save(doc);
        Notify();
      }
      return true;
    }

    public static AnimationTrack FindTrackForInstance(RhinoDoc doc, InstanceObject instance)
    {
      if (doc == null || instance == null)
        return null;
      var model = Model(doc);
      var tag = instance.Attributes.GetUserString(TrackUserStringKey);
      return model.Tracks.FirstOrDefault(track =>
        track.ObjectId == instance.Id || track.Id.ToString("D") == tag);
    }

    public static Pose EffectivePose(RhinoDoc doc, AnimationTrack track, double frame)
    {
      var model = Model(doc);
      return EvaluateEffectivePose(model, track, frame, new Dictionary<Guid, Pose>(), new HashSet<Guid>());
    }

    public static double EffectiveMechanicalAngle(
      RhinoDoc doc,
      AnimationTrack track,
      double frame)
    {
      if (track == null)
        return 0.0;
      var pose = EffectivePose(doc, track, frame);
      return AnimationMath.MechanicalAngleDegrees(pose, track.RotationAxis);
    }

    public static double DisplayedMechanicalAngle(RhinoDoc doc, AnimationTrack track)
    {
      if (track == null)
        return 0.0;
      var instance = ResolveInstance(doc, track);
      Pose displayedPose;
      if (instance != null && track.TryCapturePose(
        AnimationMath.SanitizeAffine(instance.InstanceXform),
        0.0,
        out displayedPose))
        return AnimationMath.MechanicalAngleDegrees(displayedPose, track.RotationAxis);
      return EffectiveMechanicalAngle(doc, track, Model(doc).CurrentFrame);
    }

    public static InstanceObject ResolveInstance(RhinoDoc doc, AnimationTrack track)
    {
      if (doc == null || track == null)
        return null;

      var direct = doc.Objects.FindId(track.ObjectId) as InstanceObject;
      if (direct != null)
        return direct;

      var trackText = track.Id.ToString("D");
      var candidates = doc.Objects.GetObjectList(ObjectType.InstanceReference);
      foreach (var candidate in candidates)
      {
        if (candidate.Attributes.GetUserString(TrackUserStringKey) != trackText)
          continue;
        var instance = candidate as InstanceObject;
        if (instance == null)
          continue;
        track.ObjectId = instance.Id;
        return instance;
      }
      return null;
    }

    private static Pose EvaluateEffectivePose(
      TimelineDocument model,
      AnimationTrack track,
      double frame,
      Dictionary<Guid, Pose> cache,
      HashSet<Guid> visiting)
    {
      if (track == null)
        return Pose.Identity;
      Pose cached;
      if (cache.TryGetValue(track.Id, out cached))
        return cached.Clone();

      var pose = track.Evaluate(frame);
      if (!visiting.Add(track.Id))
        return pose;

      var constraint = model.ConstraintForDriven(track.Id);
      if (constraint != null)
      {
        var driver = model.FindTrack(constraint.DriverTrackId);
        if (driver != null)
        {
          var driverPose = EvaluateEffectivePose(model, driver, frame, cache, visiting);
          var driverAngle = AnimationMath.MechanicalAngleDegrees(
            driverPose,
            driver.RotationAxis);
          if (constraint.Type == MechanicalConstraintType.RackPinion)
          {
            var axis = AxisVector(constraint.DrivenLinearAxis);
            var targetDistance = constraint.EvaluateRackDistance(driverAngle);
            var currentDistance = pose.Translation * axis;
            pose.Translation += axis * (targetDistance - currentDistance);
          }
          else
          {
            var targetAngle = constraint.EvaluateDrivenAngle(driverAngle);
            var capturedDrivenTwist = AnimationMath.ExtractAxisRotationDegrees(
              pose.Rotation,
              track.RotationAxis);
            pose.AxisAngleDegrees = targetAngle - capturedDrivenTwist;
          }
        }
      }

      visiting.Remove(track.Id);
      cache[track.Id] = pose.Clone();
      return pose;
    }

    private static Transform EvaluateWorldTarget(
      TimelineDocument model,
      AnimationTrack track,
      double frame,
      Dictionary<Guid, Pose> poseCache,
      Dictionary<Guid, Transform> worldCache,
      HashSet<Guid> visiting)
    {
      Transform cached;
      if (worldCache.TryGetValue(track.Id, out cached))
        return cached;

      var ownTarget = track.TargetTransform(
        EvaluateEffectivePose(model, track, frame, poseCache, new HashSet<Guid>()));
      if (!visiting.Add(track.Id))
        return ownTarget;

      var target = ownTarget;
      var parent = model.FindTrack(track.ParentTrackId);
      if (parent != null)
      {
        Transform bindInverse;
        if (track.ParentBindTransform.TryGetInverse(out bindInverse))
        {
          var parentWorld = EvaluateWorldTarget(model, parent, frame, poseCache, worldCache, visiting);
          target = parentWorld * bindInverse * ownTarget;
        }
      }

      visiting.Remove(track.Id);
      worldCache[track.Id] = target;
      return target;
    }

    private static void ApplyAbsolute(RhinoDoc doc, AnimationTrack track, Transform target)
    {
      var instance = ResolveInstance(doc, track);
      if (instance == null)
        return;
      target = AnimationMath.SanitizeAffine(target);
      var rawCurrent = instance.InstanceXform;
      var requiresAffineRepair = !AnimationMath.HasExactAffineBottomRow(rawCurrent);
      var current = AnimationMath.SanitizeAffine(rawCurrent);
      if (!requiresAffineRepair && AnimationMath.AlmostEqual(current, target))
        return;
      ReplaceInstanceTransform(doc, track, instance, target);
    }

    private static bool ReplaceInstanceTransform(
      RhinoDoc doc,
      AnimationTrack track,
      InstanceObject instance,
      Transform target)
    {
      target = AnimationMath.SanitizeAffine(target);
      var wasSelected = instance.IsSelected(false) > 0;
      Transform currentInverse;
      var current = AnimationMath.SanitizeAffine(instance.InstanceXform);
      if (!current.TryGetInverse(out currentInverse))
        return false;
      var delta = AnimationMath.SanitizeAffine(target * currentInverse);
      var newId = doc.Objects.Transform(instance.Id, delta, true);
      if (newId == Guid.Empty)
        return false;
      track.ObjectId = newId;
      if (wasSelected)
      {
        var transformed = doc.Objects.FindId(newId);
        transformed?.Select(true);
      }
      return true;
    }

    private static Transform DefaultPivotTransform(RhinoDoc doc, InstanceObject instance)
    {
      var plane = Plane.WorldXY;
      var view = doc.Views.ActiveView;
      if (view != null)
        plane = view.ActiveViewport.ConstructionPlane();
      plane.Origin = instance.Geometry.GetBoundingBox(true).Center;
      return Transform.PlaneToPlane(Plane.WorldXY, plane);
    }

    private static Vector3d AxisVector(RotationAxis axis)
    {
      switch (axis)
      {
        case RotationAxis.X: return Vector3d.XAxis;
        case RotationAxis.Y: return Vector3d.YAxis;
        default: return Vector3d.ZAxis;
      }
    }

    private static void SetTrackTag(RhinoDoc doc, Guid objectId, Guid trackId)
    {
      var rhinoObject = doc.Objects.FindId(objectId);
      if (rhinoObject == null)
        return;
      var attributes = rhinoObject.Attributes.Duplicate();
      attributes.SetUserString(TrackUserStringKey, trackId == Guid.Empty ? string.Empty : trackId.ToString("D"));
      doc.Objects.ModifyAttributes(objectId, attributes, true);
    }

    private static void SaveAndNotify(RhinoDoc doc)
    {
      TimelineRepository.Save(doc);
      Notify();
    }

    private static void Notify()
    {
      Changed?.Invoke();
    }
  }
}
