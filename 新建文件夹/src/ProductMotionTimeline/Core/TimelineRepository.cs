using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rhino;
using Rhino.Geometry;

namespace ProductMotionTimeline.Core
{
  internal static class TimelineRepository
  {
    private const string DocumentKey = "ProductMotionTimeline.Data.v1";
    private const string Magic = "PMTL";
    private static readonly Dictionary<uint, TimelineDocument> Documents = new Dictionary<uint, TimelineDocument>();

    public static void Initialize()
    {
      RhinoDoc.CloseDocument += OnCloseDocument;
      RhinoDoc.BeginSaveDocument += OnBeginSaveDocument;
    }

    public static void Shutdown()
    {
      RhinoDoc.CloseDocument -= OnCloseDocument;
      RhinoDoc.BeginSaveDocument -= OnBeginSaveDocument;
      Documents.Clear();
    }

    public static TimelineDocument Get(RhinoDoc doc)
    {
      if (doc == null)
        return null;

      TimelineDocument model;
      if (Documents.TryGetValue(doc.RuntimeSerialNumber, out model))
        return model;

      model = Load(doc) ?? new TimelineDocument();
      model.ClampSettings();
      Documents[doc.RuntimeSerialNumber] = model;
      return model;
    }

    public static void Save(RhinoDoc doc)
    {
      if (doc == null)
        return;

      try
      {
        doc.Strings.SetString(DocumentKey, Capture(doc));
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("ProductMotion：保存动画数据失败：{0}", exception.Message);
      }
    }

    internal static string Capture(RhinoDoc doc)
    {
      var model = Get(doc);
      if (model == null)
        return string.Empty;

      using (var stream = new MemoryStream())
      using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
      {
        writer.Write(Magic);
        writer.Write(TimelineDocument.DataVersion);
        writer.Write(model.StartFrame);
        writer.Write(model.EndFrame);
        writer.Write(model.CurrentFrame);
        writer.Write(model.FramesPerSecond);
        writer.Write(model.LoopPlayback);
        WriteGuid(writer, model.SelectedTrackId);
        writer.Write((int)model.TemplatePlacement);
        writer.Write(model.TemplateGapFrames);
        writer.Write(model.Tracks.Count);

        foreach (var track in model.Tracks)
        {
          WriteGuid(writer, track.Id);
          WriteGuid(writer, track.ObjectId);
          writer.Write(track.Name ?? string.Empty);
          writer.Write(track.Enabled);
          writer.Write((int)track.RotationAxis);
          WriteTransform(writer, track.BaseTransform);
          WriteTransform(writer, track.PivotTransform);
          WriteGuid(writer, track.ParentTrackId);
          WriteTransform(writer, track.ParentBindTransform);
          writer.Write(track.Keys.Count);
          foreach (var key in track.Keys)
          {
            writer.Write(key.Frame);
            writer.Write((int)key.Interpolation);
            WritePose(writer, key.Pose);
          }
        }

        writer.Write(model.Constraints.Count);
        foreach (var constraint in model.Constraints)
        {
          WriteGuid(writer, constraint.Id);
          WriteGuid(writer, constraint.DriverTrackId);
          WriteGuid(writer, constraint.DrivenTrackId);
          writer.Write((int)constraint.Type);
          writer.Write(constraint.DriverTeeth);
          writer.Write(constraint.DrivenTeeth);
          writer.Write(constraint.PhaseOffsetDegrees);
          writer.Write(constraint.Enabled);
          writer.Write(constraint.Module);
          writer.Write(constraint.PressureAngleDegrees);
          writer.Write(constraint.PhaseOffsetDistance);
          writer.Write((int)constraint.DrivenLinearAxis);
          writer.Write(constraint.DirectionMultiplier);
        }

        writer.Flush();
        return Convert.ToBase64String(stream.ToArray());
      }
    }

    internal static bool Restore(RhinoDoc doc, string encoded)
    {
      if (doc == null || string.IsNullOrWhiteSpace(encoded))
        return false;
      var model = Deserialize(encoded, false);
      if (model == null)
        return false;
      model.ClampSettings();
      Documents[doc.RuntimeSerialNumber] = model;
      return true;
    }

    private static TimelineDocument Load(RhinoDoc doc)
    {
      return Deserialize(doc.Strings.GetValue(DocumentKey), true);
    }

    private static TimelineDocument Deserialize(string encoded, bool reportFailure)
    {
      try
      {
        if (string.IsNullOrWhiteSpace(encoded))
          return null;

        using (var stream = new MemoryStream(Convert.FromBase64String(encoded)))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
          if (reader.ReadString() != Magic)
            return null;
          var version = reader.ReadInt32();
          if (version < 2 || version > TimelineDocument.DataVersion)
            return null;

          var model = new TimelineDocument
          {
            StartFrame = reader.ReadInt32(),
            EndFrame = reader.ReadInt32(),
            CurrentFrame = reader.ReadInt32(),
            FramesPerSecond = reader.ReadInt32(),
            LoopPlayback = reader.ReadBoolean(),
            SelectedTrackId = ReadGuid(reader)
          };
          if (version >= 5)
          {
            model.TemplatePlacement = (TemplatePlacementMode)reader.ReadInt32();
            model.TemplateGapFrames = reader.ReadInt32();
          }

          var trackCount = reader.ReadInt32();
          for (var i = 0; i < trackCount; i++)
          {
            var track = new AnimationTrack
            {
              Id = ReadGuid(reader),
              ObjectId = ReadGuid(reader),
              Name = reader.ReadString(),
              Enabled = reader.ReadBoolean(),
              RotationAxis = (RotationAxis)reader.ReadInt32(),
              BaseTransform = ReadTransform(reader),
              PivotTransform = ReadTransform(reader)
            };
            if (version >= 3)
            {
              track.ParentTrackId = ReadGuid(reader);
              track.ParentBindTransform = ReadTransform(reader);
            }
            var keyCount = reader.ReadInt32();
            for (var keyIndex = 0; keyIndex < keyCount; keyIndex++)
            {
              track.Keys.Add(new Keyframe
              {
                Frame = reader.ReadInt32(),
                Interpolation = (InterpolationMode)reader.ReadInt32(),
                Pose = ReadPose(reader)
              });
            }
            track.SortKeys();
            model.Tracks.Add(track);
          }

          if (version >= 3)
          {
            var constraintCount = reader.ReadInt32();
            for (var i = 0; i < constraintCount; i++)
            {
              var constraint = new MechanicalConstraint
              {
                Id = ReadGuid(reader),
                DriverTrackId = ReadGuid(reader),
                DrivenTrackId = ReadGuid(reader),
                Type = (MechanicalConstraintType)reader.ReadInt32(),
                DriverTeeth = reader.ReadInt32(),
                DrivenTeeth = reader.ReadInt32(),
                PhaseOffsetDegrees = reader.ReadDouble(),
                Enabled = reader.ReadBoolean()
              };
              if (version >= 4)
              {
                constraint.Module = reader.ReadDouble();
                constraint.PressureAngleDegrees = reader.ReadDouble();
              }
              if (version >= 5)
              {
                constraint.PhaseOffsetDistance = reader.ReadDouble();
                constraint.DrivenLinearAxis = (RotationAxis)reader.ReadInt32();
                constraint.DirectionMultiplier = reader.ReadDouble();
              }
              model.Constraints.Add(constraint);
            }
          }
          model.ClampSettings();
          return model;
        }
      }
      catch (Exception exception)
      {
        if (reportFailure)
          RhinoApp.WriteLine("ProductMotion：读取动画数据失败，将使用空时间轴：{0}", exception.Message);
        return null;
      }
    }

    private static void OnBeginSaveDocument(object sender, DocumentSaveEventArgs e)
    {
      if (e.Document != null && Documents.ContainsKey(e.Document.RuntimeSerialNumber))
        Save(e.Document);
    }

    private static void OnCloseDocument(object sender, DocumentEventArgs e)
    {
      if (e.Document != null)
        Documents.Remove(e.Document.RuntimeSerialNumber);
    }

    private static void WriteGuid(BinaryWriter writer, Guid value)
    {
      writer.Write(value.ToByteArray());
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
      return new Guid(reader.ReadBytes(16));
    }

    private static void WritePose(BinaryWriter writer, Pose pose)
    {
      writer.Write(pose.Translation.X);
      writer.Write(pose.Translation.Y);
      writer.Write(pose.Translation.Z);
      writer.Write(pose.Rotation.X);
      writer.Write(pose.Rotation.Y);
      writer.Write(pose.Rotation.Z);
      writer.Write(pose.Rotation.W);
      writer.Write(pose.Scale.X);
      writer.Write(pose.Scale.Y);
      writer.Write(pose.Scale.Z);
      writer.Write(pose.AxisAngleDegrees);
    }

    private static Pose ReadPose(BinaryReader reader)
    {
      return new Pose
      {
        Translation = new Vector3d(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
        Rotation = new QuaternionValue(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
        Scale = new Vector3d(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
        AxisAngleDegrees = reader.ReadDouble()
      };
    }

    private static void WriteTransform(BinaryWriter writer, Transform transform)
    {
      for (var row = 0; row < 4; row++)
      {
        for (var column = 0; column < 4; column++)
          writer.Write(transform[row, column]);
      }
    }

    private static Transform ReadTransform(BinaryReader reader)
    {
      var transform = Transform.Identity;
      for (var row = 0; row < 4; row++)
      {
        for (var column = 0; column < 4; column++)
          transform[row, column] = reader.ReadDouble();
      }
      return transform;
    }
  }
}
