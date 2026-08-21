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
    }

    public static void Shutdown()
    {
      RhinoDoc.CloseDocument -= OnCloseDocument;
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
      var model = Get(doc);
      if (model == null)
        return;

      try
      {
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
            writer.Write(track.Keys.Count);
            foreach (var key in track.Keys)
            {
              writer.Write(key.Frame);
              writer.Write((int)key.Interpolation);
              WritePose(writer, key.Pose);
            }
          }

          writer.Flush();
          doc.Strings.SetString(DocumentKey, Convert.ToBase64String(stream.ToArray()));
        }
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("ProductMotion：保存动画数据失败：{0}", exception.Message);
      }
    }

    private static TimelineDocument Load(RhinoDoc doc)
    {
      try
      {
        var encoded = doc.Strings.GetValue(DocumentKey);
        if (string.IsNullOrWhiteSpace(encoded))
          return null;

        using (var stream = new MemoryStream(Convert.FromBase64String(encoded)))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
          if (reader.ReadString() != Magic)
            return null;
          var version = reader.ReadInt32();
          if (version != TimelineDocument.DataVersion)
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
          return model;
        }
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("ProductMotion：读取动画数据失败，将使用空时间轴：{0}", exception.Message);
        return null;
      }
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
