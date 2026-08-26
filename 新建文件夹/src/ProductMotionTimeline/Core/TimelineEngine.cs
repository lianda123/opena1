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

    public static event Action Changed;

    public static TimelineDocument Model(RhinoDoc doc)
    {
      return TimelineRepository.Get(doc);
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

      model.Tracks.Add(track);
      model.SelectedTrackId = track.Id;
      SetTrackTag(doc, instance.Id, track.Id);
      SaveAndNotify(doc);
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
        doc.Objects.UnselectAll();
        var instance = ResolveInstance(doc, track);
        instance?.Select(true);
        doc.Views.Redraw();
        Notify();
      }
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

      track.UpsertKey(model.CurrentFrame, pose, interpolation);
      SaveAndNotify(doc);
      return true;
    }

    public static bool DeleteKey(RhinoDoc doc)
    {
      var model = Model(doc);
      var track = model.SelectedTrack;
      if (track == null || !track.DeleteKey(model.CurrentFrame))
        return false;
      TimelineRepository.Save(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
      Notify();
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
      track.UpsertKey(model.CurrentFrame, _keyClipboard.Pose, _keyClipboard.Interpolation);
      SaveAndNotify(doc);
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
          var freshStartPlaceholder = existing != null &&
                                      target.Keys.Count == 1 &&
                                      targetFrame == model.StartFrame &&
                                      IsIdentityPose(existing.Pose);
          if (existing != null && !freshStartPlaceholder)
          {
            result.SkippedExistingCount++;
            continue;
          }
          target.UpsertKey(targetFrame, sourceKey.Pose, sourceKey.Interpolation);
          result.PastedCount++;
        }
      }

      if (result.PastedCount > 0)
      {
        model.EndFrame = maxFrame;
        SaveAndNotify(doc);
        ApplyFrame(doc, model.CurrentFrame, false);
      }
      return result;
    }

    private static bool IsIdentityPose(Pose pose)
    {
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

    public static bool MoveKey(RhinoDoc doc, Guid trackId, int oldFrame, int newFrame)
    {
      var model = Model(doc);
      var track = model.Tracks.FirstOrDefault(item => item.Id == trackId);
      if (track == null || track.FindKey(oldFrame) == null)
        return false;
      newFrame = Math.Max(model.StartFrame, Math.Min(model.EndFrame, newFrame));
      track.MoveKey(oldFrame, newFrame);
      TimelineRepository.Save(doc);
      Notify();
      return true;
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
      double directionMultiplier = 1.0)
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

      model.Constraints.RemoveAll(item => item.DrivenTrackId == drivenTrackId);
      model.Constraints.Add(new MechanicalConstraint
      {
        DriverTrackId = driverTrackId,
        DrivenTrackId = drivenTrackId,
        Type = type,
        DriverTeeth = driverTeeth,
        DrivenTeeth = drivenTeeth,
        Module = Math.Max(0.0, module),
        PressureAngleDegrees = Math.Max(1.0, pressureAngleDegrees),
        PhaseOffsetDegrees = phaseOffsetDegrees,
        PhaseOffsetDistance = phaseOffsetDistance,
        DrivenLinearAxis = drivenLinearAxis,
        DirectionMultiplier = directionMultiplier < 0.0 ? -1.0 : 1.0,
        Enabled = true
      });
      SaveAndNotify(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
      RhinoApp.WriteLine(
        "ProductMotion：已建立“{0}”→“{1}”传动，角速度比 {2:0.###}。",
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
      return true;
    }

    public static bool DeleteMechanicalConstraint(RhinoDoc doc, Guid constraintId)
    {
      var model = Model(doc);
      var removed = model.Constraints.RemoveAll(item => item.Id == constraintId) > 0;
      if (!removed)
        return false;
      SaveAndNotify(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
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

    public static Point3d PivotOrigin(AnimationTrack track)
    {
      var origin = Point3d.Origin;
      origin.Transform(track.PivotTransform);
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
      var removed = model.Constraints.RemoveAll(item => item.DrivenTrackId == drivenTrackId) > 0;
      if (!removed)
        return false;
      SaveAndNotify(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
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
      track.RotationAxis = axis;
      key.Pose.AxisAngleDegrees = angleDegrees;
      TimelineRepository.Save(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
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

      track.RotationAxis = axis;
      key.Pose.Translation = translation;
      key.Pose.Scale = scale;
      if (model.ConstraintForDriven(track.Id) == null)
        key.Pose.AxisAngleDegrees = angleDegrees;
      TimelineRepository.Save(doc);
      ApplyFrame(doc, model.CurrentFrame, false);
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
      key.Interpolation = interpolation;
      TimelineRepository.Save(doc);
      Notify();
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
      var current = instance.InstanceXform;
      if (AnimationMath.AlmostEqual(current, target))
        return;

      Transform currentInverse;
      if (!current.TryGetInverse(out currentInverse))
        return;
      var delta = target * currentInverse;
      var wasSelected = instance.IsSelected(false) > 0;
      var newId = doc.Objects.Transform(instance.Id, delta, true);
      if (newId == Guid.Empty)
        return;
      track.ObjectId = newId;
      if (wasSelected)
      {
        var transformed = doc.Objects.FindId(newId);
        transformed?.Select(true);
      }
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
