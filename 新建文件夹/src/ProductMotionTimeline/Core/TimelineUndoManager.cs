using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;

namespace ProductMotionTimeline.Core
{
  internal static class TimelineUndoManager
  {
    private sealed class UndoSnapshot
    {
      public string Description { get; set; }
      public string EncodedModel { get; set; }
    }

    private sealed class UndoScope : IDisposable
    {
      private RhinoDoc _document;
      private readonly uint _undoSerial;

      public UndoScope(RhinoDoc document, uint undoSerial)
      {
        _document = document;
        _undoSerial = undoSerial;
      }

      public void Dispose()
      {
        var document = _document;
        if (document == null)
          return;
        _document = null;
        End(document, _undoSerial);
      }
    }

    private sealed class EmptyScope : IDisposable
    {
      public void Dispose()
      {
      }
    }

    private static readonly Dictionary<uint, int> DepthByDocument =
      new Dictionary<uint, int>();

    public static IDisposable Begin(RhinoDoc doc, string description)
    {
      if (doc == null)
        return new EmptyScope();

      var serial = doc.RuntimeSerialNumber;
      int depth;
      DepthByDocument.TryGetValue(serial, out depth);
      DepthByDocument[serial] = depth + 1;
      if (depth > 0)
        return new UndoScope(doc, 0);

      var action = string.IsNullOrWhiteSpace(description)
        ? "ProductMotion 时间轴操作"
        : description;
      var snapshot = new UndoSnapshot
      {
        Description = action,
        EncodedModel = TimelineRepository.Capture(doc)
      };
      var undoSerial = doc.BeginUndoRecord(action);
      doc.AddCustomUndoEvent(action, OnCustomUndo, snapshot);
      return new UndoScope(doc, undoSerial);
    }

    private static void End(RhinoDoc doc, uint undoSerial)
    {
      var serial = doc.RuntimeSerialNumber;
      int depth;
      if (!DepthByDocument.TryGetValue(serial, out depth))
        return;
      depth--;
      if (depth > 0)
      {
        DepthByDocument[serial] = depth;
        return;
      }

      DepthByDocument.Remove(serial);
      if (undoSerial > 0)
        doc.EndUndoRecord(undoSerial);
    }

    private static void OnCustomUndo(object sender, CustomUndoEventArgs e)
    {
      var snapshot = e.Tag as UndoSnapshot;
      if (snapshot == null || e.Document == null)
        return;

      var current = new UndoSnapshot
      {
        Description = snapshot.Description,
        EncodedModel = TimelineRepository.Capture(e.Document)
      };
      e.Document.AddCustomUndoEvent(
        snapshot.Description,
        OnCustomUndo,
        current);

      if (TimelineRepository.Restore(e.Document, snapshot.EncodedModel))
        TimelineEngine.NotifyTimelineRestored(e.Document);
    }
  }
}
