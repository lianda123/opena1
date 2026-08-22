using System;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using ProductMotionTimeline.Core;
using Rhino;

namespace ProductMotionTimeline.UI
{
  internal sealed class TimelineCanvas : Drawable
  {
    private const float HeaderWidth = 190f;
    private const float RulerHeight = 30f;
    private const float RowHeight = 25f;
    private readonly Font _font = new Font(SystemFont.Default, 9f);
    private readonly SolidBrush _background = new SolidBrush(Color.FromArgb(31, 33, 37));
    private readonly SolidBrush _header = new SolidBrush(Color.FromArgb(42, 44, 49));
    private readonly SolidBrush _row = new SolidBrush(Color.FromArgb(37, 39, 44));
    private readonly SolidBrush _selectedRow = new SolidBrush(Color.FromArgb(52, 58, 68));
    private readonly SolidBrush _text = new SolidBrush(Color.FromArgb(220, 223, 228));
    private readonly SolidBrush _mutedText = new SolidBrush(Color.FromArgb(145, 150, 158));
    private readonly SolidBrush _keyBrush = new SolidBrush(Color.FromArgb(255, 177, 58));
    private readonly SolidBrush _selectedKeyBrush = new SolidBrush(Color.FromArgb(255, 226, 124));
    private readonly Pen _gridPen = new Pen(Color.FromArgb(67, 70, 77), 1f);
    private readonly Pen _minorGridPen = new Pen(Color.FromArgb(49, 52, 58), 1f);
    private readonly Pen _playheadPen = new Pen(Color.FromArgb(255, 86, 77), 2f);
    private readonly SolidBrush _playheadBrush = new SolidBrush(Color.FromArgb(255, 86, 77));

    private Guid _dragTrackId = Guid.Empty;
    private int _dragOriginalFrame = -1;
    private int _dragPreviewFrame = -1;
    private bool _scrubbing;

    public TimelineCanvas()
    {
      Size = new Size(520, 190);
      Paint += OnPaint;
      MouseDown += OnMouseDown;
      MouseMove += OnMouseMove;
      MouseUp += OnMouseUp;
    }

    public void RefreshHeight()
    {
      var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
      var rows = Math.Max(4, model?.Tracks.Count ?? 0);
      Size = new Size(Size.Width, (int)(RulerHeight + rows * RowHeight + 4));
      Invalidate();
    }

    private void OnPaint(object sender, PaintEventArgs e)
    {
      var graphics = e.Graphics;
      var width = ClientSize.Width;
      var height = ClientSize.Height;
      graphics.FillRectangle(_background, new RectangleF(0, 0, width, height));
      graphics.FillRectangle(_header, new RectangleF(0, 0, HeaderWidth, height));

      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      if (model == null)
        return;

      DrawRuler(graphics, model, width, height);
      DrawTracks(graphics, model, width);
      DrawPlayhead(graphics, model, width, height);
    }

    private void DrawRuler(Graphics graphics, TimelineDocument model, float width, float height)
    {
      graphics.DrawText(_font, _text, new PointF(10, 8), "轨道 / 关键帧");
      var pixelsPerFrame = TimelineWidth(width) / Math.Max(1, model.EndFrame - model.StartFrame);
      var majorStep = ChooseMajorStep(pixelsPerFrame);
      var minorStep = Math.Max(1, majorStep / 5);

      for (var frame = model.StartFrame; frame <= model.EndFrame; frame += minorStep)
      {
        var x = FrameToX(frame, model, width);
        var major = (frame - model.StartFrame) % majorStep == 0;
        graphics.DrawLine(major ? _gridPen : _minorGridPen, x, RulerHeight - (major ? 10 : 5), x, height);
        if (major)
          graphics.DrawText(_font, _mutedText, new PointF(x + 3, 5), frame.ToString());
      }
    }

    private void DrawTracks(Graphics graphics, TimelineDocument model, float width)
    {
      var tracks = model.OrderedTracks();
      for (var index = 0; index < tracks.Count; index++)
      {
        var track = tracks[index];
        var y = RulerHeight + index * RowHeight;
        var selected = model.SelectedTrack?.Id == track.Id;
        graphics.FillRectangle(selected ? _selectedRow : _row, new RectangleF(0, y, width, RowHeight - 1));
        var depth = model.TrackDepth(track);
        var driven = model.ConstraintForDriven(track.Id) != null;
        var prefix = (depth > 0 ? "↳ " : string.Empty) + (driven ? "[传] " : string.Empty);
        graphics.DrawText(
          _font,
          track.Enabled ? _text : _mutedText,
          new PointF(10 + depth * 13, y + 6),
          prefix + TrimName(track.Name, depth, driven));

        foreach (var key in track.Keys)
        {
          var frame = track.Id == _dragTrackId && key.Frame == _dragOriginalFrame
            ? _dragPreviewFrame
            : key.Frame;
          var x = FrameToX(frame, model, width);
          var keySelected = selected && frame == model.CurrentFrame;
          DrawDiamond(graphics, x, y + RowHeight / 2f, keySelected ? _selectedKeyBrush : _keyBrush);
        }
      }
    }

    private void DrawPlayhead(Graphics graphics, TimelineDocument model, float width, float height)
    {
      var x = FrameToX(model.CurrentFrame, model, width);
      graphics.DrawLine(_playheadPen, x, 0, x, height);
      graphics.FillRectangle(_playheadBrush, new RectangleF(x - 4, 0, 8, 6));
    }

    private static void DrawDiamond(Graphics graphics, float x, float y, Brush brush)
    {
      using (var path = new GraphicsPath())
      {
        path.MoveTo(x, y - 5);
        path.LineTo(x + 5, y);
        path.LineTo(x, y + 5);
        path.LineTo(x - 5, y);
        path.CloseFigure();
        graphics.FillPath(brush, path);
      }
    }

    private void OnMouseDown(object sender, MouseEventArgs e)
    {
      if ((e.Buttons & MouseButtons.Primary) == 0)
        return;
      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      if (model == null)
        return;

      var row = RowFromY(e.Location.Y);
      var tracks = model.OrderedTracks();
      if (row >= 0 && row < tracks.Count)
      {
        var track = tracks[row];
        TimelineEngine.SelectTrack(doc, track.Id);
        if (e.Location.X < HeaderWidth)
          return;

        var hit = HitKey(track, model, ClientSize.Width, e.Location.X);
        if (hit != null)
        {
          _dragTrackId = track.Id;
          _dragOriginalFrame = hit.Frame;
          _dragPreviewFrame = hit.Frame;
          TimelineEngine.ApplyFrame(doc, hit.Frame, false);
          return;
        }
      }

      if (e.Location.X >= HeaderWidth)
      {
        _scrubbing = true;
        ScrubTo(e.Location.X, false);
      }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
      if ((e.Buttons & MouseButtons.Primary) == 0)
        return;
      if (_dragTrackId != Guid.Empty)
      {
        var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
        _dragPreviewFrame = XToFrame(e.Location.X, model, ClientSize.Width);
        Invalidate();
      }
      else if (_scrubbing)
      {
        ScrubTo(e.Location.X, false);
      }
    }

    private void OnMouseUp(object sender, MouseEventArgs e)
    {
      var doc = RhinoDoc.ActiveDoc;
      if (_dragTrackId != Guid.Empty)
      {
        var newFrame = _dragPreviewFrame;
        TimelineEngine.MoveKey(doc, _dragTrackId, _dragOriginalFrame, newFrame);
        TimelineEngine.ApplyFrame(doc, newFrame, true);
      }
      else if (_scrubbing)
      {
        ScrubTo(e.Location.X, true);
      }

      _dragTrackId = Guid.Empty;
      _dragOriginalFrame = -1;
      _dragPreviewFrame = -1;
      _scrubbing = false;
      Invalidate();
    }

    private void ScrubTo(float x, bool persist)
    {
      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      if (model == null)
        return;
      TimelineEngine.ApplyFrame(doc, XToFrame(x, model, ClientSize.Width), persist);
    }

    private static Keyframe HitKey(AnimationTrack track, TimelineDocument model, float width, float x)
    {
      return track.Keys.FirstOrDefault(key => Math.Abs(FrameToX(key.Frame, model, width) - x) <= 7f);
    }

    private static int RowFromY(float y)
    {
      if (y < RulerHeight)
        return -1;
      return (int)((y - RulerHeight) / RowHeight);
    }

    private static float TimelineWidth(float width)
    {
      return Math.Max(1f, width - HeaderWidth - 8f);
    }

    private static float FrameToX(int frame, TimelineDocument model, float width)
    {
      var t = (frame - model.StartFrame) / (double)Math.Max(1, model.EndFrame - model.StartFrame);
      return HeaderWidth + (float)(t * TimelineWidth(width));
    }

    private static int XToFrame(float x, TimelineDocument model, float width)
    {
      var t = (x - HeaderWidth) / TimelineWidth(width);
      t = Math.Max(0f, Math.Min(1f, t));
      return (int)Math.Round(model.StartFrame + t * (model.EndFrame - model.StartFrame));
    }

    private static int ChooseMajorStep(float pixelsPerFrame)
    {
      var candidates = new[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 500 };
      return candidates.First(step => step * pixelsPerFrame >= 55f || step == 500);
    }

    private static string TrimName(string value, int depth, bool driven)
    {
      if (string.IsNullOrWhiteSpace(value))
        return "动画部件";
      var limit = Math.Max(7, 18 - depth * 2 - (driven ? 3 : 0));
      return value.Length <= limit ? value : value.Substring(0, limit - 1) + "…";
    }
  }
}
