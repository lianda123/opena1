using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace WoodJointPro.Core
{
  internal sealed class JointPreviewConduit : DisplayConduit, IDisposable
  {
    private JointBuildResult _result;

    internal void Show(JointBuildResult result)
    {
      _result = result;
      Enabled = result != null;
      Redraw();
    }

    internal void Clear()
    {
      Enabled = false;
      _result = null;
      Redraw();
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      if (_result == null || _result.Frame == null)
        return;
      var frame = _result.Frame;
      var along = frame.Along;
      if (!along.Unitize())
        along = Vector3d.XAxis;
      var start = frame.Center - along * (frame.Length * 0.5);
      var end = frame.Center + along * (frame.Length * 0.5);
      e.Display.DrawLine(new Line(start, end), Color.FromArgb(255, 190, 55), 4);
      e.Display.DrawPoint(start, PointStyle.RoundSimple, 7, Color.FromArgb(255, 225, 90));
      e.Display.DrawPoint(end, PointStyle.RoundSimple, 7, Color.FromArgb(255, 225, 90));
      e.Display.DrawDot(frame.Center, _result.Description + "（回车确认）",
        Color.FromArgb(255, 190, 55), Color.Black);
      DrawCutters(e, _result.First == null ? null : _result.First.Cutters, Color.FromArgb(235, 85, 75));
      DrawCutters(e, _result.Second == null ? null : _result.Second.Cutters, Color.FromArgb(70, 175, 255));
    }

    public void Dispose()
    {
      Clear();
    }

    private static void DrawCutters(DrawEventArgs e, IEnumerable<Brep> cutters, Color color)
    {
      if (cutters == null)
        return;
      foreach (var cutter in cutters)
      {
        if (cutter != null && cutter.IsValid)
          e.Display.DrawBrepWires(cutter, color, 2);
      }
    }

    private static void Redraw()
    {
      var doc = Rhino.RhinoDoc.ActiveDoc;
      if (doc != null)
        doc.Views.Redraw();
    }
  }
}
