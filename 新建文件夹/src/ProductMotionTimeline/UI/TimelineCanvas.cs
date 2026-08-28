using System;
using System.Collections.Generic;
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
    private readonly SolidBrush _smoothKeyBrush = new SolidBrush(Color.FromArgb(255, 177, 58));
    private readonly SolidBrush _linearKeyBrush = new SolidBrush(Color.FromArgb(76, 184, 255));
    private readonly SolidBrush _constantKeyBrush = new SolidBrush(Color.FromArgb(205, 126, 255));
    private readonly SolidBrush _selectedKeyBrush = new SolidBrush(Color.FromArgb(255, 226, 124));
    private readonly Pen _smoothSegmentPen = new Pen(Color.FromArgb(207, 140, 47), 2f);
    private readonly Pen _linearSegmentPen = new Pen(Color.FromArgb(58, 145, 204), 2f);
    private readonly Pen _constantSegmentPen = new Pen(Color.FromArgb(151, 87, 194), 2f);
    private readonly Pen _gridPen = new Pen(Color.FromArgb(67, 70, 77), 1f);
    private readonly Pen _minorGridPen = new Pen(Color.FromArgb(49, 52, 58), 1f);
    private readonly Pen _playheadPen = new Pen(Color.FromArgb(255, 86, 77), 2f);
    private readonly Pen _trackDropPen = new Pen(Color.FromArgb(0, 190, 255), 3f);
    private readonly Pen _selectedTrackPen = new Pen(Color.FromArgb(70, 190, 255), 4f);
    private readonly Pen _marqueePen = new Pen(Color.FromArgb(93, 203, 255), 1f);
    private readonly SolidBrush _marqueeBrush = new SolidBrush(Color.FromArgb(55, 74, 177, 235));
    private readonly SolidBrush _playheadBrush = new SolidBrush(Color.FromArgb(255, 86, 77));
    private readonly HashSet<KeySelection> _selectedKeys = new HashSet<KeySelection>();
    private readonly HashSet<KeySelection> _dragKeys = new HashSet<KeySelection>();

    private Guid _dragTrackId = Guid.Empty;
    private int _dragOriginalFrame = -1;
    private int _dragPreviewFrame = -1;
    private bool _scrubbing;
    private Guid _rowDragTrackId = Guid.Empty;
    private Guid _rowDropSiblingId = Guid.Empty;
    private float _rowDragStartY;
    private bool _rowDropAfter;
    private bool _rowDragging;
    private bool _marqueeSelecting;
    private PointF _marqueeStart;
    private PointF _marqueeCurrent;
    private int _marqueeMode;

    public event Action KeySelectionChanged;
    public event Action KeyActivated;
    public event Action<string> OperationCompleted;

    public IEnumerable<KeySelection> SelectedKeys
    {
      get
      {
        var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
        if (model != null)
          _selectedKeys.RemoveWhere(item => model.FindTrack(item.TrackId)?.FindKey(item.Frame) == null);
        return _selectedKeys.Select(item => new KeySelection
        {
          TrackId = item.TrackId,
          Frame = item.Frame
        }).ToList();
      }
    }

    public TimelineCanvas()
    {
      Size = new Size(520, 190);
      Paint += OnPaint;
      MouseDown += OnMouseDown;
      MouseMove += OnMouseMove;
      MouseUp += OnMouseUp;
      MouseDoubleClick += OnMouseDoubleClick;
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
      DrawTrackDropIndicator(graphics, model, width);
      DrawPlayhead(graphics, model, width, height);
      DrawMarquee(graphics);
    }

    private void DrawRuler(Graphics graphics, TimelineDocument model, float width, float height)
    {
      graphics.DrawText(_font, _text, new PointF(10, 8), "轨道（上下拖动） / 关键帧");
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
        if (selected)
          graphics.DrawLine(_selectedTrackPen, 2f, y + 1f, 2f, y + RowHeight - 2f);
        var depth = model.TrackDepth(track);
        var driven = model.ConstraintForDriven(track.Id) != null;
        var prefix = (selected ? "● " : string.Empty) +
                     (depth > 0 ? "↳ " : string.Empty) +
                     (driven ? "[传] " : string.Empty);
        graphics.DrawText(
          _font,
          track.Enabled ? _text : _mutedText,
          new PointF(10 + depth * 13, y + 6),
          prefix + TrimName(track.Name, depth, driven));

        for (var keyIndex = 0; keyIndex < track.Keys.Count - 1; keyIndex++)
        {
          var left = track.Keys[keyIndex];
          var right = track.Keys[keyIndex + 1];
          graphics.DrawLine(
            SegmentPen(left.Interpolation),
            FrameToX(left.Frame, model, width),
            y + RowHeight / 2f,
            FrameToX(right.Frame, model, width),
            y + RowHeight / 2f);
        }

        foreach (var key in track.Keys)
        {
          var keySelection = new KeySelection
          {
            TrackId = track.Id,
            Frame = key.Frame
          };
          var frame = _dragKeys.Contains(keySelection)
            ? key.Frame + _dragPreviewFrame - _dragOriginalFrame
            : key.Frame;
          var x = FrameToX(frame, model, width);
          var keySelected = _selectedKeys.Contains(keySelection);
          DrawDiamond(
            graphics,
            x,
            y + RowHeight / 2f,
            keySelected ? _selectedKeyBrush : KeyBrush(key.Interpolation));
        }
      }
    }

    private void DrawMarquee(Graphics graphics)
    {
      if (!_marqueeSelecting)
        return;
      var rectangle = NormalizedRectangle(_marqueeStart, _marqueeCurrent);
      graphics.FillRectangle(_marqueeBrush, rectangle);
      graphics.DrawRectangle(_marqueePen, rectangle);
    }

    private Brush KeyBrush(InterpolationMode mode)
    {
      switch (mode)
      {
        case InterpolationMode.Linear:
          return _linearKeyBrush;
        case InterpolationMode.Constant:
          return _constantKeyBrush;
        default:
          return _smoothKeyBrush;
      }
    }

    private Pen SegmentPen(InterpolationMode mode)
    {
      switch (mode)
      {
        case InterpolationMode.Linear:
          return _linearSegmentPen;
        case InterpolationMode.Constant:
          return _constantSegmentPen;
        default:
          return _smoothSegmentPen;
      }
    }

    private void DrawPlayhead(Graphics graphics, TimelineDocument model, float width, float height)
    {
      var x = FrameToX(model.CurrentFrame, model, width);
      graphics.DrawLine(_playheadPen, x, 0, x, height);
      graphics.FillRectangle(_playheadBrush, new RectangleF(x - 4, 0, 8, 6));
    }

    private void DrawTrackDropIndicator(Graphics graphics, TimelineDocument model, float width)
    {
      if (!_rowDragging || _rowDropSiblingId == Guid.Empty)
        return;
      var tracks = model.OrderedTracks();
      var targetIndex = tracks.FindIndex(track => track.Id == _rowDropSiblingId);
      if (targetIndex < 0)
        return;
      var lineIndex = targetIndex;
      if (_rowDropAfter)
      {
        var targetDepth = model.TrackDepth(tracks[targetIndex]);
        lineIndex++;
        while (lineIndex < tracks.Count && model.TrackDepth(tracks[lineIndex]) > targetDepth)
          lineIndex++;
      }
      var y = RulerHeight + lineIndex * RowHeight;
      graphics.DrawLine(_trackDropPen, 0f, y, width, y);
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
      if ((e.Buttons & MouseButtons.Alternate) != 0 && e.Location.X >= HeaderWidth)
      {
        _marqueeSelecting = true;
        _marqueeStart = e.Location;
        _marqueeCurrent = e.Location;
        _marqueeMode = HasModifier(e, Keys.Alt) ? -1 : HasModifier(e, Keys.Shift) ? 1 : 0;
        e.Handled = true;
        Invalidate();
        return;
      }
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
        {
          _rowDragTrackId = track.Id;
          _rowDropSiblingId = track.Id;
          _rowDragStartY = e.Location.Y;
          _rowDropAfter = false;
          _rowDragging = false;
          return;
        }

        var hit = HitKey(track, model, ClientSize.Width, e.Location.X);
        if (hit != null)
        {
          var selection = new KeySelection { TrackId = track.Id, Frame = hit.Frame };
          if (HasModifier(e, Keys.Alt))
          {
            _selectedKeys.Remove(selection);
            KeySelectionChanged?.Invoke();
            TimelineEngine.ApplyFrame(doc, hit.Frame, false);
            Invalidate();
            return;
          }
          else if (HasModifier(e, Keys.Shift))
            _selectedKeys.Add(selection);
          else if (!_selectedKeys.Contains(selection))
          {
            _selectedKeys.Clear();
            _selectedKeys.Add(selection);
          }
          KeySelectionChanged?.Invoke();
          _dragKeys.Clear();
          _dragKeys.UnionWith(_selectedKeys);
          _dragTrackId = track.Id;
          _dragOriginalFrame = hit.Frame;
          _dragPreviewFrame = hit.Frame;
          TimelineEngine.ApplyFrame(doc, hit.Frame, false);
          Invalidate();
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
      if (_marqueeSelecting)
      {
        _marqueeCurrent = e.Location;
        e.Handled = true;
        Invalidate();
        return;
      }
      if ((e.Buttons & MouseButtons.Primary) == 0)
        return;
      if (_rowDragTrackId != Guid.Empty)
      {
        var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
        if (model == null)
          return;
        if (!_rowDragging && Math.Abs(e.Location.Y - _rowDragStartY) < 4f)
          return;
        _rowDragging = true;
        UpdateTrackDrop(model, e.Location.Y);
        Invalidate();
      }
      else if (_dragTrackId != Guid.Empty)
      {
        var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
        var requested = XToFrame(e.Location.X, model, ClientSize.Width) - _dragOriginalFrame;
        _dragPreviewFrame = _dragOriginalFrame + ClampDragDelta(model, requested);
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
      if (_marqueeSelecting)
      {
        _marqueeCurrent = e.Location;
        ApplyMarqueeSelection();
        _marqueeSelecting = false;
        e.Handled = true;
        KeySelectionChanged?.Invoke();
        Invalidate();
        return;
      }
      if (_rowDragTrackId != Guid.Empty)
      {
        if (_rowDragging && _rowDropSiblingId != Guid.Empty)
          TimelineEngine.ReorderTrack(
            doc,
            _rowDragTrackId,
            _rowDropSiblingId,
            _rowDropAfter);
      }
      else if (_dragTrackId != Guid.Empty)
      {
        var requestedDelta = _dragPreviewFrame - _dragOriginalFrame;
        using (TimelineEngine.BeginUndoScope(doc, "整体移动 ProductMotion 关键帧"))
        {
          var result = TimelineEngine.MoveKeys(doc, _dragKeys, requestedDelta);
          if (string.IsNullOrWhiteSpace(result.ErrorMessage))
          {
            _selectedKeys.Clear();
            _selectedKeys.UnionWith(result.MovedSelections);
            TimelineEngine.ApplyFrame(doc, _dragOriginalFrame + result.AppliedDelta, true);
            OperationCompleted?.Invoke(
              $"已整体移动 {result.MovedCount} 个关键帧，保持原间距" +
              (result.OverwrittenCount > 0 ? $"；覆盖 {result.OverwrittenCount} 个目标关键帧。" : "。"));
          }
          else
          {
            OperationCompleted?.Invoke(result.ErrorMessage);
          }
        }
        KeySelectionChanged?.Invoke();
      }
      else if (_scrubbing)
      {
        ScrubTo(e.Location.X, true);
      }

      _dragTrackId = Guid.Empty;
      _dragKeys.Clear();
      _dragOriginalFrame = -1;
      _dragPreviewFrame = -1;
      _scrubbing = false;
      _rowDragTrackId = Guid.Empty;
      _rowDropSiblingId = Guid.Empty;
      _rowDragStartY = 0f;
      _rowDropAfter = false;
      _rowDragging = false;
      Invalidate();
    }

    private int ClampDragDelta(TimelineDocument model, int requestedDelta)
    {
      if (model == null || _dragKeys.Count == 0)
        return 0;
      var minimumFrame = _dragKeys.Min(selection => selection.Frame);
      var maximumFrame = _dragKeys.Max(selection => selection.Frame);
      return Math.Max(
        model.StartFrame - minimumFrame,
        Math.Min(model.EndFrame - maximumFrame, requestedDelta));
    }

    private void OnMouseDoubleClick(object sender, MouseEventArgs e)
    {
      if ((e.Buttons & MouseButtons.Primary) == 0 || e.Location.X < HeaderWidth)
        return;
      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      var tracks = model?.OrderedTracks();
      var row = RowFromY(e.Location.Y);
      if (tracks == null || row < 0 || row >= tracks.Count)
        return;
      var track = tracks[row];
      var key = HitKey(track, model, ClientSize.Width, e.Location.X);
      if (key == null)
        return;
      _selectedKeys.Clear();
      _selectedKeys.Add(new KeySelection { TrackId = track.Id, Frame = key.Frame });
      TimelineEngine.SelectTrack(doc, track.Id);
      TimelineEngine.ApplyFrame(doc, key.Frame, false);
      KeySelectionChanged?.Invoke();
      KeyActivated?.Invoke();
      e.Handled = true;
      Invalidate();
    }

    public int SelectAllKeys()
    {
      _selectedKeys.Clear();
      var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
      var track = model?.SelectedTrack;
      if (track != null)
      {
        foreach (var key in track.Keys)
          _selectedKeys.Add(new KeySelection { TrackId = track.Id, Frame = key.Frame });
      }
      KeySelectionChanged?.Invoke();
      Invalidate();
      return _selectedKeys.Count;
    }

    public void ClearKeySelection()
    {
      _selectedKeys.Clear();
      KeySelectionChanged?.Invoke();
      Invalidate();
    }

    public void ReplaceKeySelection(IEnumerable<KeySelection> selections)
    {
      _selectedKeys.Clear();
      _selectedKeys.UnionWith(selections ?? Enumerable.Empty<KeySelection>());
      KeySelectionChanged?.Invoke();
      Invalidate();
    }

    private void ApplyMarqueeSelection()
    {
      var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
      if (model == null)
        return;
      var rectangle = NormalizedRectangle(_marqueeStart, _marqueeCurrent);
      var hits = new HashSet<KeySelection>();
      var tracks = model.OrderedTracks();
      for (var row = 0; row < tracks.Count; row++)
      {
        var track = tracks[row];
        var y = RulerHeight + row * RowHeight + RowHeight / 2f;
        foreach (var key in track.Keys)
        {
          var point = new PointF(FrameToX(key.Frame, model, ClientSize.Width), y);
          if (rectangle.Contains(point))
            hits.Add(new KeySelection { TrackId = track.Id, Frame = key.Frame });
        }
      }
      if (_marqueeMode == 0)
        _selectedKeys.Clear();
      if (_marqueeMode < 0)
        _selectedKeys.ExceptWith(hits);
      else
        _selectedKeys.UnionWith(hits);
    }

    private static bool HasModifier(MouseEventArgs e, Keys modifier)
    {
      return (e.Modifiers & modifier) == modifier;
    }

    private static RectangleF NormalizedRectangle(PointF first, PointF second)
    {
      var left = Math.Min(first.X, second.X);
      var top = Math.Min(first.Y, second.Y);
      return new RectangleF(left, top, Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));
    }

    private void UpdateTrackDrop(TimelineDocument model, float y)
    {
      var source = model.FindTrack(_rowDragTrackId);
      var tracks = model.OrderedTracks();
      if (source == null || tracks.Count == 0)
        return;
      var row = Math.Max(0, Math.Min(tracks.Count - 1, RowFromY(y)));
      var target = tracks[row];
      while (target != null && target.ParentTrackId != source.ParentTrackId)
        target = model.FindTrack(target.ParentTrackId);

      if (target == null)
      {
        var siblings = tracks.Where(track => track.ParentTrackId == source.ParentTrackId).ToList();
        if (siblings.Count == 0)
          return;
        var firstRow = tracks.FindIndex(track => track.Id == siblings[0].Id);
        target = row <= firstRow ? siblings[0] : siblings[siblings.Count - 1];
        _rowDropAfter = row > firstRow;
      }
      else
      {
        var targetRow = tracks.FindIndex(track => track.Id == target.Id);
        _rowDropAfter = row > targetRow ||
                        (row == targetRow && y >= RulerHeight + (targetRow + 0.5f) * RowHeight);
      }
      _rowDropSiblingId = target.Id;
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
