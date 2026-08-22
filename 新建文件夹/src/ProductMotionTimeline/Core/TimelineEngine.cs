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
    private static Keyframe _keyClipboard;

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
      var model = Model(doc);
      if (model.Tracks.Any(track => track.Id == trackId))
      {
        model.SelectedTrackId = trackId;
        Notify();
      }
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

      var pivot = Transform.PlaneToPlane(Plane.WorldXY, plane);
      if (!track.TryRebasePivot(pivot))
        return false;
      SaveAndNotify(doc);
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
        PhaseOffsetDegrees = phaseOffsetDegrees,
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
          pose.AxisAngleDegrees = constraint.EvaluateDrivenAngle(driverPose.AxisAngleDegrees);
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
