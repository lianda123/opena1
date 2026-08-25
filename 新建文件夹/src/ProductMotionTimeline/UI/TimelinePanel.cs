using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using ProductMotionTimeline.Core;
using Rhino;

namespace ProductMotionTimeline.UI
{
  [Guid("2D2539E7-819F-4325-87E5-1C62784C7158")]
  public sealed class TimelinePanel : Panel
  {
    public static readonly Guid PanelId = new Guid("2D2539E7-819F-4325-87E5-1C62784C7158");
    private static TimelinePanel _instance;

    private readonly TimelineCanvas _canvas = new TimelineCanvas();
    private readonly NumericStepper _start = IntegerStepper(0, 100000, 0);
    private readonly NumericStepper _end = IntegerStepper(1, 100000, 250);
    private readonly NumericStepper _frame = IntegerStepper(0, 100000, 0);
    private readonly NumericStepper _fps = IntegerStepper(1, 120, 30);
    private readonly CheckBox _loop = new CheckBox { Text = "循环", Checked = true };
    private readonly DropDown _interpolation = new DropDown();
    private readonly DropDown _rotationAxis = new DropDown();
    private readonly NumericStepper _axisAngle = IntegerStepper(-100000, 100000, 0);
    private readonly TextBox _trackName = new TextBox { Width = 150 };
    private readonly Label _relationship = new Label { TextColor = Color.FromArgb(255, 190, 92) };
    private readonly ListBox _constraints = new ListBox { Height = 72 };
    private readonly List<MechanicalConstraint> _constraintItems = new List<MechanicalConstraint>();
    private readonly Label _status = new Label { TextColor = Color.FromArgb(180, 185, 194) };
    private readonly Button _play = new Button { Text = "▶ 播放" };
    private readonly UITimer _timer = new UITimer();
    private bool _suppress;

    public TimelinePanel()
    {
      _instance = this;
      _interpolation.DataStore = new[]
      {
        "平滑：缓入缓出",
        "线性：匀速",
        "阶梯：保持后跳变"
      };
      _interpolation.SelectedIndex = 0;
      _rotationAxis.DataStore = new[] { "X", "Y", "Z" };
      _rotationAxis.SelectedIndex = 2;
      BuildUi();
      WireEvents();
      RefreshFromModel();
    }

    public static void RequestTogglePlayback()
    {
      _instance?.TogglePlayback();
    }

    private void BuildUi()
    {
      var transport = Horizontal(
        Button("|◀", () => GoToBoundary(true)),
        Button("◀", () => Step(-1)),
        _play,
        Button("■", StopPlayback),
        Button("▶", () => Step(1)),
        Button("▶|", () => GoToBoundary(false)));

      var keyTools = Horizontal(
        Button("＋ 添加部件", () => RhinoApp.RunScript("_PMTAddPart", false)),
        Button("＋ 组内零件", () => RhinoApp.RunScript("_PMTAddGroupPart", false)),
        Button("◆ 插入/更新帧", InsertKey),
        Button("删除帧", DeleteKey),
        Button("复制", CopyKey),
        Button("粘贴", PasteKey));

      var settings = Horizontal(
        new Label { Text = "起始" }, _start,
        new Label { Text = "结束" }, _end,
        new Label { Text = "当前" }, _frame,
        new Label { Text = "FPS" }, _fps,
        _loop);

      var trackTools = Horizontal(
        new Label { Text = "轨道名" }, _trackName,
        Button("改名", RenameTrack),
        Button("设轴心", () => RhinoApp.RunScript("_PMTSetPivot", false)),
        Button("自动找轴孔", () => RhinoApp.RunScript("_PMTAutoPivot", false)),
        Button("重绑定", () => RhinoApp.RunScript("_PMTRebind", false)),
        new Label { Text = "插值" }, _interpolation,
        new Label { Text = "连续轴" }, _rotationAxis,
        new Label { Text = "转角°" }, _axisAngle,
        Button("删除轨道", DeleteTrack));

      var hierarchyTools = Horizontal(
        new Label { Text = "父子层级" },
        Button("设父级", () => RhinoApp.RunScript("_PMTSetParent", false)),
        Button("清除父级", () => RhinoApp.RunScript("_PMTClearParent", false)));

      var mechanicalTools = Horizontal(
        new Label { Text = "机械约束" },
        Button("外啮合齿轮", () => RhinoApp.RunScript("_PMTExternalGear", false)),
        Button("内啮合齿轮", () => RhinoApp.RunScript("_PMTInternalGear", false)),
        Button("皮带传动", () => RhinoApp.RunScript("_PMTBelt", false)),
        Button("编辑选中", () => RhinoApp.RunScript("_PMTEditMechanical", false)),
        Button("检查全部", () => RhinoApp.RunScript("_PMTValidateMechanical", false)),
        Button("删除选中", DeleteSelectedConstraint));

      var constraintSelectionTools = Horizontal(
        new Label { Text = "传动关系图" },
        Button("定位主动件", () => SelectConstraintTrack(true)),
        Button("定位从动件", () => SelectConstraintTrack(false)));

      var motionTemplates = Horizontal(
        new Label { Text = "动作模板" },
        Button("往复摆动", () => RhinoApp.RunScript("_PMTReciprocate", false)),
        Button("旋转回弹", () => RhinoApp.RunScript("_PMTRebound", false)),
        Button("曲柄滑块", () => RhinoApp.RunScript("_PMTCrankSlider", false)),
        Button("四连杆", () => RhinoApp.RunScript("_PMTFourBar", false)));

      var scroll = new Scrollable { Content = _canvas, Border = BorderType.None };
      var root = new TableLayout
      {
        Padding = new Padding(8),
        Spacing = new Size(6, 6)
      };
      root.Rows.Add(new TableRow(transport));
      root.Rows.Add(new TableRow(keyTools));
      root.Rows.Add(new TableRow(settings));
      root.Rows.Add(new TableRow(new TableCell(scroll, true)) { ScaleHeight = true });
      root.Rows.Add(new TableRow(trackTools));
      root.Rows.Add(new TableRow(hierarchyTools));
      root.Rows.Add(new TableRow(mechanicalTools));
      root.Rows.Add(new TableRow(constraintSelectionTools));
      root.Rows.Add(new TableRow(_constraints));
      root.Rows.Add(new TableRow(motionTemplates));
      root.Rows.Add(new TableRow(_relationship));
      root.Rows.Add(new TableRow(_status));
      Content = root;
    }

    private void WireEvents()
    {
      TimelineEngine.Changed += RefreshFromModel;
      RhinoDoc.ActiveDocumentChanged += OnActiveDocumentChanged;
      _play.Click += (sender, args) => TogglePlayback();
      _timer.Elapsed += (sender, args) => PlaybackTick();
      _frame.ValueChanged += (sender, args) =>
      {
        if (!_suppress)
          TimelineEngine.ApplyFrame(RhinoDoc.ActiveDoc, (int)_frame.Value, true);
      };
      _start.ValueChanged += (sender, args) => UpdateSettings();
      _end.ValueChanged += (sender, args) => UpdateSettings();
      _fps.ValueChanged += (sender, args) => UpdateSettings();
      _loop.CheckedChanged += (sender, args) => UpdateSettings();
      _interpolation.SelectedIndexChanged += (sender, args) => UpdateInterpolation();
      _rotationAxis.SelectedIndexChanged += (sender, args) => UpdateRotationChannel();
      _axisAngle.ValueChanged += (sender, args) => UpdateRotationChannel();
      _constraints.SelectedIndexChanged += (sender, args) => ConstraintSelectionChanged();
    }

    private void RefreshFromModel()
    {
      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      if (model == null)
        return;

      _suppress = true;
      _start.Value = model.StartFrame;
      _end.Value = model.EndFrame;
      _frame.MinValue = model.StartFrame;
      _frame.MaxValue = model.EndFrame;
      _frame.Value = model.CurrentFrame;
      _fps.Value = model.FramesPerSecond;
      _loop.Checked = model.LoopPlayback;

      var track = model.SelectedTrack;
      _trackName.Text = track?.Name ?? string.Empty;
      var key = track?.FindKey(model.CurrentFrame);
      var constraint = track == null ? null : model.ConstraintForDriven(track.Id);
      if (key != null)
      {
        _interpolation.SelectedIndex = ToDropDownIndex(key.Interpolation);
        _axisAngle.Value = constraint == null
          ? key.Pose.AxisAngleDegrees
          : TimelineEngine.EffectiveMechanicalAngle(doc, track, model.CurrentFrame);
      }
      else
      {
        _axisAngle.Value = 0.0;
      }
      _rotationAxis.SelectedIndex = track == null ? 2 : (int)track.RotationAxis;
      _axisAngle.Enabled = constraint == null;

      var parent = track == null ? null : model.FindTrack(track.ParentTrackId);
      var driver = constraint == null ? null : model.FindTrack(constraint.DriverTrackId);
      var parentText = parent == null ? "父级：无" : $"父级：{parent.Name}";
      var constraintText = constraint == null
        ? "传动：无"
        : $"传动：{driver?.Name ?? "未知"} → {track.Name}　比例 {constraint.SignedRatio:0.###}　相位 {constraint.PhaseOffsetDegrees:0.###}°";
      _relationship.Text = parentText + "　　" + constraintText;
      RefreshConstraintList(model, doc, constraint?.Id ?? Guid.Empty);

      _status.Text = track == null
        ? "先点“添加部件”；若对象已打组但只想动画其中一部分，请点“组内零件”。"
        : $"轨道：{track.Name}　帧：{model.CurrentFrame}　关键帧：{track.Keys.Count}　提示：父级继承运动，子级仍可单独卡帧。";
      _canvas.RefreshHeight();
      _suppress = false;
    }

    private void RefreshConstraintList(TimelineDocument model, RhinoDoc doc, Guid preferredId)
    {
      var previouslySelected = SelectedConstraint()?.Id ?? preferredId;
      _constraintItems.Clear();
      _constraintItems.AddRange(model.Constraints);
      var rows = new List<string>();
      foreach (var item in _constraintItems)
      {
        var driver = model.FindTrack(item.DriverTrackId);
        var driven = model.FindTrack(item.DrivenTrackId);
        var validation = TimelineEngine.ValidateMechanicalConstraint(doc, item);
        var mark = validation.Severity == ValidationSeverity.Ok ? "✓" : "⚠";
        rows.Add(string.Format(
          "{0}  {1} → {2}  {3}  {4}:{5}  比例 {6:0.###}  {7}",
          mark,
          driver?.Name ?? "?",
          driven?.Name ?? "?",
          MechanicalTypeName(item.Type),
          item.DriverTeeth,
          item.DrivenTeeth,
          item.SignedRatio,
          validation.Message));
      }
      _constraints.DataStore = rows;
      var index = _constraintItems.FindIndex(item => item.Id == previouslySelected);
      if (index >= 0)
        _constraints.SelectedIndex = index;
    }

    private MechanicalConstraint SelectedConstraint()
    {
      var index = _constraints.SelectedIndex;
      return index >= 0 && index < _constraintItems.Count ? _constraintItems[index] : null;
    }

    private void ConstraintSelectionChanged()
    {
      if (_suppress)
        return;
      var selected = SelectedConstraint();
      if (selected != null)
        TimelineEngine.SelectTrack(RhinoDoc.ActiveDoc, selected.DrivenTrackId);
    }

    private void SelectConstraintTrack(bool driver)
    {
      var doc = RhinoDoc.ActiveDoc;
      var selected = SelectedConstraint();
      if (doc == null || selected == null)
        return;
      var trackId = driver ? selected.DriverTrackId : selected.DrivenTrackId;
      TimelineEngine.SelectTrack(doc, trackId);
      var track = TimelineEngine.Model(doc).FindTrack(trackId);
      var instance = TimelineEngine.ResolveInstance(doc, track);
      doc.Objects.UnselectAll();
      instance?.Select(true);
      doc.Views.Redraw();
    }

    private void DeleteSelectedConstraint()
    {
      var selected = SelectedConstraint();
      if (selected != null)
        TimelineEngine.DeleteMechanicalConstraint(RhinoDoc.ActiveDoc, selected.Id);
    }

    private static string MechanicalTypeName(MechanicalConstraintType type)
    {
      switch (type)
      {
        case MechanicalConstraintType.InternalGear: return "内啮合";
        case MechanicalConstraintType.Belt: return "皮带";
        default: return "外啮合";
      }
    }

    private void UpdateSettings()
    {
      if (_suppress)
        return;
      TimelineEngine.UpdateSettings(
        RhinoDoc.ActiveDoc,
        (int)_start.Value,
        (int)_end.Value,
        (int)_fps.Value,
        _loop.Checked == true);
    }

    private void UpdateRotationChannel()
    {
      if (_suppress)
        return;
      var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
      if (model?.SelectedTrack != null && model.ConstraintForDriven(model.SelectedTrack.Id) != null)
      {
        _status.Text = "当前轨道由机械约束驱动；请修改主动件转角或先解除从动关系。";
        return;
      }
      var axis = (RotationAxis)Math.Max(0, Math.Min(2, _rotationAxis.SelectedIndex));
      if (!TimelineEngine.UpdateCurrentKeyRotationChannel(RhinoDoc.ActiveDoc, axis, _axisAngle.Value))
        _status.Text = "请先在当前帧插入关键帧，再设置连续轴转角。";
    }

    private void UpdateInterpolation()
    {
      if (_suppress)
        return;
      var mode = SelectedInterpolation();
      if (!TimelineEngine.UpdateCurrentKeyInterpolation(RhinoDoc.ActiveDoc, mode))
      {
        _status.Text = "当前帧没有关键帧；所选插值会在下一次插入关键帧时使用。";
        return;
      }
      _status.Text = InterpolationDescription(mode) + "，已应用到当前关键帧至下一关键帧。";
    }

    private void InsertKey()
    {
      StopPlayback();
      TimelineEngine.InsertOrUpdateKey(RhinoDoc.ActiveDoc, SelectedInterpolation());
    }

    private void DeleteKey()
    {
      StopPlayback();
      TimelineEngine.DeleteKey(RhinoDoc.ActiveDoc);
    }

    private void CopyKey()
    {
      if (!TimelineEngine.CopyKey(RhinoDoc.ActiveDoc))
        _status.Text = "当前帧没有可复制的关键帧。";
    }

    private void PasteKey()
    {
      StopPlayback();
      if (!TimelineEngine.PasteKey(RhinoDoc.ActiveDoc))
        _status.Text = "请先复制一个关键帧。";
    }

    private void RenameTrack()
    {
      var doc = RhinoDoc.ActiveDoc;
      var track = TimelineEngine.Model(doc).SelectedTrack;
      if (track == null || string.IsNullOrWhiteSpace(_trackName.Text))
        return;
      track.Name = _trackName.Text.Trim();
      TimelineEngine.Persist(doc);
      RefreshFromModel();
    }

    private void DeleteTrack()
    {
      StopPlayback();
      var track = TimelineEngine.Model(RhinoDoc.ActiveDoc).SelectedTrack;
      if (track == null)
        return;
      var result = MessageBox.Show(
        $"删除轨道“{track.Name}”？部件会恢复到创建轨道时的位置。",
        "ProductMotion Timeline",
        MessageBoxButtons.YesNo,
        MessageBoxType.Question);
      if (result == DialogResult.Yes)
        TimelineEngine.DeleteSelectedTrack(RhinoDoc.ActiveDoc);
    }

    private void TogglePlayback()
    {
      if (_timer.Started)
      {
        StopPlayback();
        return;
      }
      var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
      if (model.Tracks.Count == 0)
        return;
      _timer.Interval = 1.0 / Math.Max(1, model.FramesPerSecond);
      _timer.Start();
      _play.Text = "Ⅱ 暂停";
    }

    private void StopPlayback()
    {
      if (_timer.Started)
        _timer.Stop();
      _play.Text = "▶ 播放";
      TimelineEngine.Persist(RhinoDoc.ActiveDoc);
    }

    private void PlaybackTick()
    {
      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      var next = model.CurrentFrame + 1;
      if (next > model.EndFrame)
      {
        if (model.LoopPlayback)
          next = model.StartFrame;
        else
        {
          StopPlayback();
          return;
        }
      }
      TimelineEngine.ApplyFrame(doc, next, false);
    }

    private void Step(int delta)
    {
      StopPlayback();
      var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
      TimelineEngine.ApplyFrame(RhinoDoc.ActiveDoc, model.CurrentFrame + delta, true);
    }

    private void GoToBoundary(bool start)
    {
      StopPlayback();
      var model = TimelineEngine.Model(RhinoDoc.ActiveDoc);
      TimelineEngine.ApplyFrame(RhinoDoc.ActiveDoc, start ? model.StartFrame : model.EndFrame, true);
    }

    private void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
    {
      StopPlayback();
      RefreshFromModel();
    }

    private InterpolationMode SelectedInterpolation()
    {
      switch (_interpolation.SelectedIndex)
      {
        case 1: return InterpolationMode.Linear;
        case 2: return InterpolationMode.Constant;
        default: return InterpolationMode.Smooth;
      }
    }

    private static int ToDropDownIndex(InterpolationMode mode)
    {
      switch (mode)
      {
        case InterpolationMode.Linear: return 1;
        case InterpolationMode.Constant: return 2;
        default: return 0;
      }
    }

    private static string InterpolationDescription(InterpolationMode mode)
    {
      switch (mode)
      {
        case InterpolationMode.Linear:
          return "线性：全程匀速运动";
        case InterpolationMode.Constant:
          return "阶梯：保持当前姿态，到下一关键帧瞬间跳变";
        default:
          return "平滑：起步慢、中段快、到达前减速";
      }
    }

    private static NumericStepper IntegerStepper(double min, double max, double value)
    {
      return new NumericStepper
      {
        MinValue = min,
        MaxValue = max,
        Value = value,
        DecimalPlaces = 0,
        Increment = 1,
        Width = 62
      };
    }

    private static Button Button(string text, Action action)
    {
      var button = new Button { Text = text };
      button.Click += (sender, args) => action();
      return button;
    }

    private static StackLayout Horizontal(params Control[] controls)
    {
      var layout = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 5,
        VerticalContentAlignment = VerticalAlignment.Center
      };
      foreach (var control in controls)
        layout.Items.Add(control);
      return layout;
    }
  }
}
