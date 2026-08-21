using System;
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

      Pose pose;
      var evaluatedAngle = track.Evaluate(model.CurrentFrame).AxisAngleDegrees;
      if (!track.TryCapturePose(instance.InstanceXform, evaluatedAngle, out pose))
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
      model.Tracks.Remove(track);
      model.SelectedTrackId = model.Tracks.Count > 0 ? model.Tracks[0].Id : Guid.Empty;
      SaveAndNotify(doc);
      return true;
    }

    public static void ApplyFrame(RhinoDoc doc, int frame, bool persist)
    {
      if (doc == null)
        return;
      var model = Model(doc);
      model.CurrentFrame = Math.Max(model.StartFrame, Math.Min(model.EndFrame, frame));

      foreach (var track in model.Tracks.Where(item => item.Enabled))
        ApplyAbsolute(doc, track, track.TargetTransform(model.CurrentFrame));

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
