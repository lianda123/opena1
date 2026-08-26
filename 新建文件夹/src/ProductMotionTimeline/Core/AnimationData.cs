using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace ProductMotionTimeline.Core
{
  internal enum InterpolationMode
  {
    Linear = 0,
    Smooth = 1,
    Constant = 2
  }

  internal enum RotationAxis
  {
    X = 0,
    Y = 1,
    Z = 2
  }

  internal enum MechanicalConstraintType
  {
    ExternalGear = 0,
    InternalGear = 1,
    Belt = 2,
    HelicalGear = 3,
    BevelGear = 4,
    RackPinion = 5,
    SameShaft = 6
  }

  internal enum TemplatePlacementMode
  {
    CurrentFrame = 0,
    AfterSelectedTracks = 1,
    AfterAllActions = 2
  }

  internal enum ValidationSeverity
  {
    Ok = 0,
    Warning = 1,
    Error = 2
  }

  internal sealed class MechanicalValidationResult
  {
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.Ok;
    public string Message { get; set; } = "动画比例有效";
    public double ExpectedCenterDistance { get; set; }
    public double ActualCenterDistance { get; set; }
  }

  internal sealed class MechanicalConstraint
  {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DriverTrackId { get; set; }
    public Guid DrivenTrackId { get; set; }
    public MechanicalConstraintType Type { get; set; } = MechanicalConstraintType.ExternalGear;
    public int DriverTeeth { get; set; } = 20;
    public int DrivenTeeth { get; set; } = 20;
    public double Module { get; set; }
    public double PressureAngleDegrees { get; set; } = 20.0;
    public double PhaseOffsetDegrees { get; set; }
    public double PhaseOffsetDistance { get; set; }
    public RotationAxis DrivenLinearAxis { get; set; } = RotationAxis.X;
    public double DirectionMultiplier { get; set; } = 1.0;
    public bool Enabled { get; set; } = true;

    public double SignedRatio
    {
      get
      {
        if (Type == MechanicalConstraintType.SameShaft)
          return DirectionMultiplier < 0.0 ? -1.0 : 1.0;
        var ratio = Math.Max(1, DriverTeeth) / (double)Math.Max(1, DrivenTeeth);
        var direction = Type == MechanicalConstraintType.ExternalGear ||
               Type == MechanicalConstraintType.HelicalGear ||
               Type == MechanicalConstraintType.BevelGear
          ? -ratio
          : ratio;
        return direction * (DirectionMultiplier < 0.0 ? -1.0 : 1.0);
      }
    }

    public double EvaluateDrivenAngle(double driverAngleDegrees)
    {
      return PhaseOffsetDegrees + driverAngleDegrees * SignedRatio;
    }

    public double EvaluateRackDistance(double driverAngleDegrees)
    {
      var travelPerTurn = Math.PI * Math.Max(0.0, Module) * Math.Max(1, DriverTeeth);
      return PhaseOffsetDistance + driverAngleDegrees / 360.0 * travelPerTurn *
             (DirectionMultiplier < 0.0 ? -1.0 : 1.0);
    }
  }

  internal sealed class Pose
  {
    public Vector3d Translation { get; set; }
    public QuaternionValue Rotation { get; set; }
    public Vector3d Scale { get; set; }
    public double AxisAngleDegrees { get; set; }

    public static Pose Identity => new Pose
    {
      Translation = Vector3d.Zero,
      Rotation = QuaternionValue.Identity,
      Scale = new Vector3d(1.0, 1.0, 1.0),
      AxisAngleDegrees = 0.0
    };

    public Pose Clone()
    {
      return new Pose
      {
        Translation = Translation,
        Rotation = Rotation,
        Scale = Scale,
        AxisAngleDegrees = AxisAngleDegrees
      };
    }

    public static Pose Interpolate(Pose a, Pose b, double t)
    {
      return new Pose
      {
        Translation = a.Translation + (b.Translation - a.Translation) * t,
        Rotation = QuaternionValue.Slerp(a.Rotation, b.Rotation, t),
        Scale = a.Scale + (b.Scale - a.Scale) * t,
        AxisAngleDegrees = a.AxisAngleDegrees + (b.AxisAngleDegrees - a.AxisAngleDegrees) * t
      };
    }
  }

  internal sealed class Keyframe
  {
    public int Frame { get; set; }
    public Pose Pose { get; set; } = Pose.Identity;
    public InterpolationMode Interpolation { get; set; } = InterpolationMode.Smooth;

    public Keyframe Clone()
    {
      return new Keyframe
      {
        Frame = Frame,
        Pose = Pose.Clone(),
        Interpolation = Interpolation
      };
    }
  }

  internal struct KeyframeReference : IEquatable<KeyframeReference>
  {
    public KeyframeReference(Guid trackId, int frame)
    {
      TrackId = trackId;
      Frame = frame;
    }

    public Guid TrackId { get; }
    public int Frame { get; }

    public bool Equals(KeyframeReference other)
    {
      return TrackId == other.TrackId && Frame == other.Frame;
    }

    public override bool Equals(object obj)
    {
      return obj is KeyframeReference && Equals((KeyframeReference)obj);
    }

    public override int GetHashCode()
    {
      unchecked
      {
        return (TrackId.GetHashCode() * 397) ^ Frame;
      }
    }
  }

  internal sealed class KeyframePasteResult
  {
    public int Added { get; set; }
    public int Skipped { get; set; }
    public int TargetTracks { get; set; }
  }

  internal sealed class AnimationTrack
  {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ObjectId { get; set; }
    public string Name { get; set; } = "动画部件";
    public bool Enabled { get; set; } = true;
    public Guid ParentTrackId { get; set; } = Guid.Empty;
    public Transform ParentBindTransform { get; set; } = Transform.Identity;
    public RotationAxis RotationAxis { get; set; } = RotationAxis.Z;
    public Transform BaseTransform { get; set; } = Transform.Identity;
    public Transform PivotTransform { get; set; } = Transform.Identity;
    public List<Keyframe> Keys { get; } = new List<Keyframe>();

    public Keyframe FindKey(int frame)
    {
      return Keys.FirstOrDefault(key => key.Frame == frame);
    }

    public Keyframe UpsertKey(int frame, Pose pose, InterpolationMode interpolation)
    {
      var key = FindKey(frame);
      if (key == null)
      {
        key = new Keyframe { Frame = frame };
        Keys.Add(key);
      }
      key.Pose = pose.Clone();
      key.Interpolation = interpolation;
      SortKeys();
      return key;
    }

    public bool DeleteKey(int frame)
    {
      var key = FindKey(frame);
      return key != null && Keys.Remove(key);
    }

    public void MoveKey(int oldFrame, int newFrame)
    {
      var key = FindKey(oldFrame);
      if (key == null)
        return;
      var collision = FindKey(newFrame);
      if (collision != null && collision != key)
        Keys.Remove(collision);
      key.Frame = newFrame;
      SortKeys();
    }

    public Pose Evaluate(double frame)
    {
      if (Keys.Count == 0)
        return Pose.Identity;
      var exact = Keys.FirstOrDefault(key => Math.Abs(frame - key.Frame) < 1e-9);
      if (exact != null)
        return exact.Pose.Clone();
      if (frame <= Keys[0].Frame)
        return Keys[0].Pose.Clone();
      if (frame >= Keys[Keys.Count - 1].Frame)
        return Keys[Keys.Count - 1].Pose.Clone();

      for (var i = 0; i < Keys.Count - 1; i++)
      {
        var left = Keys[i];
        var right = Keys[i + 1];
        if (frame < left.Frame || frame > right.Frame)
          continue;

        if (left.Interpolation == InterpolationMode.Constant)
          return left.Pose.Clone();

        var t = (frame - left.Frame) / (right.Frame - left.Frame);
        if (left.Interpolation == InterpolationMode.Smooth)
          t = AnimationMath.SmoothStep(t);
        return Pose.Interpolate(left.Pose, right.Pose, t);
      }

      return Keys[Keys.Count - 1].Pose.Clone();
    }

    public Transform TargetTransform(double frame)
    {
      return TargetTransform(Evaluate(frame));
    }

    public Transform TargetTransform(Pose pose)
    {
      var pivotInverse = Transform.Identity;
      if (!PivotTransform.TryGetInverse(out pivotInverse))
        return BaseTransform;
      return PivotTransform * AnimationMath.Compose(pose, RotationAxis) * pivotInverse * BaseTransform;
    }

    public bool TryCapturePose(Transform currentObjectTransform, double axisAngleDegrees, out Pose pose)
    {
      pose = Pose.Identity;
      Transform pivotInverse;
      Transform baseInverse;
      if (!PivotTransform.TryGetInverse(out pivotInverse) || !BaseTransform.TryGetInverse(out baseInverse))
        return false;
      var poseTransform = pivotInverse * currentObjectTransform * baseInverse * PivotTransform;
      if (!AnimationMath.TryDecompose(poseTransform, out pose))
        return false;
      var inverseAxisRotation = QuaternionValue.FromAxisAngle(RotationAxis, -axisAngleDegrees);
      pose.Rotation = QuaternionValue.Multiply(inverseAxisRotation, pose.Rotation);
      pose.AxisAngleDegrees = axisAngleDegrees;
      return true;
    }

    public bool TryRebasePivot(Transform newPivot)
    {
      Transform oldPivotInverse;
      Transform newPivotInverse;
      Transform baseInverse;
      if (!PivotTransform.TryGetInverse(out oldPivotInverse) ||
          !newPivot.TryGetInverse(out newPivotInverse) ||
          !BaseTransform.TryGetInverse(out baseInverse))
        return false;

      var converted = new List<Pose>();
      foreach (var key in Keys)
      {
        var worldTarget = PivotTransform * AnimationMath.Compose(key.Pose, RotationAxis) * oldPivotInverse * BaseTransform;
        var newPoseTransform = newPivotInverse * worldTarget * baseInverse * newPivot;
        Pose newPose;
        if (!AnimationMath.TryDecompose(newPoseTransform, out newPose))
          return false;
        var inverseAxisRotation = QuaternionValue.FromAxisAngle(RotationAxis, -key.Pose.AxisAngleDegrees);
        newPose.Rotation = QuaternionValue.Multiply(inverseAxisRotation, newPose.Rotation);
        newPose.AxisAngleDegrees = key.Pose.AxisAngleDegrees;
        converted.Add(newPose);
      }

      PivotTransform = newPivot;
      for (var i = 0; i < Keys.Count; i++)
        Keys[i].Pose = converted[i];
      return true;
    }

    public void SortKeys()
    {
      Keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
    }
  }

  internal sealed class TimelineDocument
  {
    public const int DataVersion = 5;

    public int StartFrame { get; set; } = 0;
    public int EndFrame { get; set; } = 250;
    public int CurrentFrame { get; set; } = 0;
    public int FramesPerSecond { get; set; } = 30;
    public bool LoopPlayback { get; set; } = true;
    public Guid SelectedTrackId { get; set; } = Guid.Empty;
    public TemplatePlacementMode TemplatePlacement { get; set; } = TemplatePlacementMode.AfterAllActions;
    public int TemplateGapFrames { get; set; }
    public List<AnimationTrack> Tracks { get; } = new List<AnimationTrack>();
    public List<MechanicalConstraint> Constraints { get; } = new List<MechanicalConstraint>();

    public AnimationTrack SelectedTrack
    {
      get
      {
        var selected = Tracks.FirstOrDefault(track => track.Id == SelectedTrackId);
        return selected ?? Tracks.FirstOrDefault();
      }
    }

    public void ClampSettings()
    {
      StartFrame = Math.Max(0, StartFrame);
      EndFrame = Math.Max(StartFrame + 1, EndFrame);
      FramesPerSecond = Math.Max(1, Math.Min(120, FramesPerSecond));
      TemplateGapFrames = Math.Max(0, Math.Min(100000, TemplateGapFrames));
      if (!Enum.IsDefined(typeof(TemplatePlacementMode), TemplatePlacement))
        TemplatePlacement = TemplatePlacementMode.AfterAllActions;
      CurrentFrame = Math.Max(StartFrame, Math.Min(EndFrame, CurrentFrame));
      if (SelectedTrack == null)
        SelectedTrackId = Guid.Empty;

      var trackIds = new HashSet<Guid>(Tracks.Select(track => track.Id));
      foreach (var track in Tracks)
      {
        if (track.ParentTrackId == track.Id || !trackIds.Contains(track.ParentTrackId))
        {
          track.ParentTrackId = Guid.Empty;
          track.ParentBindTransform = Transform.Identity;
        }
      }
      Constraints.RemoveAll(constraint =>
        constraint.DriverTrackId == constraint.DrivenTrackId ||
        !trackIds.Contains(constraint.DriverTrackId) ||
        !trackIds.Contains(constraint.DrivenTrackId));
    }

    public AnimationTrack FindTrack(Guid id)
    {
      return Tracks.FirstOrDefault(track => track.Id == id);
    }

    public MechanicalConstraint ConstraintForDriven(Guid trackId)
    {
      return Constraints.FirstOrDefault(constraint => constraint.Enabled && constraint.DrivenTrackId == trackId);
    }

    public List<MechanicalConstraint> ConstraintsForDriver(Guid trackId)
    {
      return Constraints
        .Where(constraint => constraint.Enabled && constraint.DriverTrackId == trackId)
        .ToList();
    }

    public bool WouldCreateParentCycle(Guid childTrackId, Guid parentTrackId)
    {
      if (childTrackId == Guid.Empty || parentTrackId == Guid.Empty)
        return false;
      if (childTrackId == parentTrackId)
        return true;

      var visited = new HashSet<Guid>();
      var current = FindTrack(parentTrackId);
      while (current != null && current.ParentTrackId != Guid.Empty && visited.Add(current.Id))
      {
        if (current.ParentTrackId == childTrackId)
          return true;
        current = FindTrack(current.ParentTrackId);
      }
      return false;
    }

    public bool WouldCreateConstraintCycle(Guid driverTrackId, Guid drivenTrackId)
    {
      if (driverTrackId == Guid.Empty || drivenTrackId == Guid.Empty)
        return false;
      if (driverTrackId == drivenTrackId)
        return true;

      var visited = new HashSet<Guid>();
      var current = driverTrackId;
      while (current != Guid.Empty && visited.Add(current))
      {
        if (current == drivenTrackId)
          return true;
        var upstream = ConstraintForDriven(current);
        current = upstream?.DriverTrackId ?? Guid.Empty;
      }
      return false;
    }

    public List<AnimationTrack> OrderedTracks()
    {
      var result = new List<AnimationTrack>();
      var visited = new HashSet<Guid>();
      foreach (var root in Tracks.Where(track => track.ParentTrackId == Guid.Empty))
        AppendTrackTree(root, result, visited);
      foreach (var track in Tracks)
        AppendTrackTree(track, result, visited);
      return result;
    }

    public int TrackDepth(AnimationTrack track)
    {
      if (track == null)
        return 0;
      var depth = 0;
      var visited = new HashSet<Guid>();
      var current = track;
      while (current.ParentTrackId != Guid.Empty && visited.Add(current.Id))
      {
        current = FindTrack(current.ParentTrackId);
        if (current == null)
          break;
        depth++;
      }
      return depth;
    }

    private void AppendTrackTree(AnimationTrack track, List<AnimationTrack> result, HashSet<Guid> visited)
    {
      if (track == null || !visited.Add(track.Id))
        return;
      result.Add(track);
      foreach (var child in Tracks.Where(item => item.ParentTrackId == track.Id))
        AppendTrackTree(child, result, visited);
    }
  }
}
