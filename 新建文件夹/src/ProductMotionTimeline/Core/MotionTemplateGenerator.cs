using System;
using Rhino;
using Rhino.Geometry;

namespace ProductMotionTimeline.Core
{
  internal static class MotionTemplateGenerator
  {
    public static void GenerateReciprocation(
      RhinoDoc doc,
      AnimationTrack track,
      int startFrame,
      int duration,
      int cycles,
      double amplitudeDegrees)
    {
      var basePose = track.Evaluate(startFrame);
      var endFrame = startFrame + duration;
      ClearRange(track, startFrame, endFrame);
      for (var cycle = 0; cycle < cycles; cycle++)
      {
        var cycleStart = startFrame + duration * cycle / cycles;
        var cycleEnd = startFrame + duration * (cycle + 1) / cycles;
        AddAngle(track, cycleStart, basePose, basePose.AxisAngleDegrees, InterpolationMode.Smooth);
        AddAngle(track, LerpFrame(cycleStart, cycleEnd, 0.25), basePose,
          basePose.AxisAngleDegrees + amplitudeDegrees, InterpolationMode.Smooth);
        AddAngle(track, LerpFrame(cycleStart, cycleEnd, 0.50), basePose,
          basePose.AxisAngleDegrees, InterpolationMode.Smooth);
        AddAngle(track, LerpFrame(cycleStart, cycleEnd, 0.75), basePose,
          basePose.AxisAngleDegrees - amplitudeDegrees, InterpolationMode.Smooth);
        AddAngle(track, cycleEnd, basePose, basePose.AxisAngleDegrees, InterpolationMode.Smooth);
      }
      Commit(doc, endFrame);
    }

    public static void GenerateRebound(
      RhinoDoc doc,
      AnimationTrack track,
      int startFrame,
      int duration,
      double turnDegrees)
    {
      var basePose = track.Evaluate(startFrame);
      var endFrame = startFrame + duration;
      ClearRange(track, startFrame, endFrame);
      AddAngle(track, startFrame, basePose, basePose.AxisAngleDegrees, InterpolationMode.Linear);
      AddAngle(track, LerpFrame(startFrame, endFrame, 0.35), basePose,
        basePose.AxisAngleDegrees + turnDegrees, InterpolationMode.Smooth);
      AddAngle(track, LerpFrame(startFrame, endFrame, 0.42), basePose,
        basePose.AxisAngleDegrees + turnDegrees, InterpolationMode.Smooth);
      AddAngle(track, endFrame, basePose, basePose.AxisAngleDegrees, InterpolationMode.Smooth);
      Commit(doc, endFrame);
    }

    public static void GenerateCrankSlider(
      RhinoDoc doc,
      AnimationTrack crank,
      AnimationTrack slider,
      int startFrame,
      int duration,
      double crankRadius,
      double rodLength,
      RotationAxis slideAxis)
    {
      var crankPose = crank.Evaluate(startFrame);
      var sliderPose = slider.Evaluate(startFrame);
      var endFrame = startFrame + duration;
      ClearRange(crank, startFrame, endFrame);
      ClearRange(slider, startFrame, endFrame);
      var samples = Math.Max(12, Math.Min(72, duration));
      var startPosition = crankRadius + rodLength;
      var direction = AxisVector(slideAxis);
      for (var i = 0; i <= samples; i++)
      {
        var theta = Math.PI * 2.0 * i / samples;
        var root = Math.Sqrt(Math.Max(0.0,
          rodLength * rodLength - crankRadius * crankRadius * Math.Sin(theta) * Math.Sin(theta)));
        var position = crankRadius * Math.Cos(theta) + root;
        var frame = LerpFrame(startFrame, endFrame, i / (double)samples);
        AddAngle(crank, frame, crankPose,
          crankPose.AxisAngleDegrees + 360.0 * i / samples, InterpolationMode.Linear);
        var pose = sliderPose.Clone();
        pose.Translation = sliderPose.Translation + direction * (position - startPosition);
        slider.UpsertKey(frame, pose, InterpolationMode.Linear);
      }
      Commit(doc, endFrame);
    }

    public static bool GenerateFourBar(
      RhinoDoc doc,
      AnimationTrack crank,
      AnimationTrack rocker,
      int startFrame,
      int duration,
      double groundLength,
      double crankLength,
      double couplerLength,
      double rockerLength,
      out string error)
    {
      error = string.Empty;
      var samples = Math.Max(24, Math.Min(96, duration));
      var crankAngles = new double[samples + 1];
      var rockerAngles = new double[samples + 1];
      var previous = 0.0;
      for (var i = 0; i <= samples; i++)
      {
        var theta = Math.PI * 2.0 * i / samples;
        var ax = crankLength * Math.Cos(theta);
        var ay = crankLength * Math.Sin(theta);
        var dx = ax - groundLength;
        var dy = ay;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= 1e-9 ||
            distance > couplerLength + rockerLength ||
            distance < Math.Abs(couplerLength - rockerLength))
        {
          error = string.Format("链杆在主动转角 {0:0.#}° 时无解，请调整四杆长度。", theta * 180.0 / Math.PI);
          return false;
        }

        var along = (rockerLength * rockerLength - couplerLength * couplerLength + distance * distance) /
                    (2.0 * distance);
        var heightSquared = rockerLength * rockerLength - along * along;
        if (heightSquared < -1e-8)
        {
          error = "四连杆圆交点计算无解，请检查杆长。";
          return false;
        }
        var height = Math.Sqrt(Math.Max(0.0, heightSquared));
        var ux = dx / distance;
        var uy = dy / distance;
        var bx = groundLength + along * ux - height * uy;
        var by = along * uy + height * ux;
        var phi = Math.Atan2(by, bx - groundLength);
        if (i > 0)
        {
          while (phi - previous > Math.PI) phi -= Math.PI * 2.0;
          while (phi - previous < -Math.PI) phi += Math.PI * 2.0;
        }
        previous = phi;
        crankAngles[i] = theta;
        rockerAngles[i] = phi;
      }

      var crankPose = crank.Evaluate(startFrame);
      var rockerPose = rocker.Evaluate(startFrame);
      var endFrame = startFrame + duration;
      ClearRange(crank, startFrame, endFrame);
      ClearRange(rocker, startFrame, endFrame);
      var initialRocker = rockerAngles[0];
      for (var i = 0; i <= samples; i++)
      {
        var frame = LerpFrame(startFrame, endFrame, i / (double)samples);
        AddAngle(crank, frame, crankPose,
          crankPose.AxisAngleDegrees + crankAngles[i] * 180.0 / Math.PI, InterpolationMode.Linear);
        AddAngle(rocker, frame, rockerPose,
          rockerPose.AxisAngleDegrees + (rockerAngles[i] - initialRocker) * 180.0 / Math.PI,
          InterpolationMode.Linear);
      }
      Commit(doc, endFrame);
      return true;
    }

    private static void AddAngle(
      AnimationTrack track,
      int frame,
      Pose basePose,
      double angleDegrees,
      InterpolationMode interpolation)
    {
      var pose = basePose.Clone();
      pose.AxisAngleDegrees = angleDegrees;
      track.UpsertKey(frame, pose, interpolation);
    }

    private static void ClearRange(AnimationTrack track, int startFrame, int endFrame)
    {
      track.Keys.RemoveAll(key => key.Frame >= startFrame && key.Frame <= endFrame);
    }

    private static int LerpFrame(int start, int end, double amount)
    {
      return (int)Math.Round(start + (end - start) * amount);
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

    private static void Commit(RhinoDoc doc, int endFrame)
    {
      var model = TimelineEngine.Model(doc);
      model.EndFrame = Math.Max(model.EndFrame, endFrame);
      model.ClampSettings();
      TimelineEngine.Persist(doc);
      TimelineEngine.ApplyFrame(doc, model.CurrentFrame, false);
    }
  }
}
