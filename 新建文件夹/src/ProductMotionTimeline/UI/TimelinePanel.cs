using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly DropDown _templatePlacement = new DropDown();
    private readonly NumericStepper _templateGap = IntegerStepper(0, 100000, 0);
    private readonly NumericStepper _axisAngle = IntegerStepper(-100000, 100000, 0);
    private readonly Label _keyEditorTitle = new Label { Text = "双击关键帧可编辑数值" };
    private readonly NumericStepper _moveX = DecimalStepper(-1000000, 1000000, 0, 0.1);
    private readonly NumericStepper _moveY = DecimalStepper(-1000000, 1000000, 0, 0.1);
    private readonly NumericStepper _moveZ = DecimalStepper(-1000000, 1000000, 0, 0.1);
    private readonly NumericStepper _rotateX = DecimalStepper(-360000, 360000, 0, 1.0);
    private readonly NumericStepper _rotateY = DecimalStepper(-360000, 360000, 0, 1.0);
    private readonly NumericStepper _rotateZ = DecimalStepper(-360000, 360000, 0, 1.0);
    private readonly NumericStepper _scaleX = DecimalStepper(0.001, 1000, 1, 0.01);
    private readonly NumericStepper _scaleY = DecimalStepper(0.001, 1000, 1, 0.01);
    private readonly NumericStepper _scaleZ = DecimalStepper(0.001, 1000, 1, 0.01);
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
      _templatePlacement.DataStore = new[]
      {
        "当前帧（可覆盖）",
        "接在所选轨道末尾",
        "接在全部动作末尾"
      };
      _templatePlacement.SelectedIndex = 2;
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
        Button("全选本轨", SelectAllTrackKeys),
        Button("清除选择", () => _canvas.ClearKeySelection()),
        Button("复制所选", CopySelectedKeys),
        Button("粘贴到对象", PasteSelectedKeys));

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

      var keyEditor = Horizontal(
        _keyEditorTitle,
        new Label { Text = "位移 X" }, _moveX,
        new Label { Text = "Y" }, _moveY,
        new Label { Text = "Z" }, _moveZ,
        new Label { Text = "旋转° X" }, _rotateX,
        new Label { Text = "Y" }, _rotateY,
        new Label { Text = "Z" }, _rotateZ,
        new Label { Text = "缩放 X" }, _scaleX,
        new Label { Text = "Y" }, _scaleY,
        new Label { Text = "Z" }, _scaleZ,
        Button("应用到该帧", UpdateSelectedKeyPose));

      var hierarchyTools = Horizontal(
        new Label { Text = "父子层级" },
        Button("设父级", () => RhinoApp.RunScript("_PMTSetParent", false)),
        Button("清除父级", () => RhinoApp.RunScript("_PMTClearParent", false)));

      var mechanicalTools = Horizontal(
        new Label { Text = "机械约束" },
        Button("外啮合齿轮", () => RhinoApp.RunScript("_PMTExternalGear", false)),
        Button("内啮合齿轮", () => RhinoApp.RunScript("_PMTInternalGear", false)),
        Button("皮带传动", () => RhinoApp.RunScript("_PMTBelt", false)),
        Button("同轴复合齿轮", () => RhinoApp.RunScript("_PMTSameShaft", false)),
        Button("一主多从/串联", () => RhinoApp.RunScript("_PMTBindMultiple", false)),
        Button("编辑选中", () => RhinoApp.RunScript("_PMTEditMechanical", false)),
        Button("检查全部", () => RhinoApp.RunScript("_PMTValidateMechanical", false)),
        Button("删除选中", DeleteSelectedConstraint));

      var constraintSelectionTools = Horizontal(
        new Label { Text = "传动关系图" },
        Button("定位主动件", () => SelectConstraintTrack(true)),
        Button("定位从动件", () => SelectConstraintTrack(false)));

      var motionTemplates = Horizontal(
        new Label { Text = "动作模板" },
        new Label { Text = "放置" }, _templatePlacement,
        new Label { Text = "间隔帧" }, _templateGap,
        Button("往复摆动", () => RhinoApp.RunScript("_PMTReciprocate", false)),
        Button("旋转回弹", () => RhinoApp.RunScript("_PMTRebound", false)),
        Button("曲柄滑块", () => RhinoApp.RunScript("_PMTCrankSlider", false)),
        Button("四连杆", () => RhinoApp.RunScript("_PMTFourBar", false)));

      var gearTools = Horizontal(
        new Label { Text = "齿轮生成" },
        Button("综合生成器", () => RhinoApp.RunScript("_PMTGearFactory", false)),
        Button("渐开线直齿", () => RhinoApp.RunScript("_PMTCreateSpurGear", false)),
        Button("内齿轮", () => RhinoApp.RunScript("_PMTCreateInternalGear", false)),
        Button("斜齿轮", () => RhinoApp.RunScript("_PMTCreateHelicalGear", false)),
        Button("锥齿轮", () => RhinoApp.RunScript("_PMTCreateBevelGear", false)),
        Button("齿条", () => RhinoApp.RunScript("_PMTCreateRack", false)));

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
      root.Rows.Add(new TableRow(keyEditor));
      root.Rows.Add(new TableRow(trackTools));
      root.Rows.Add(new TableRow(hierarchyTools));
      root.Rows.Add(new TableRow(mechanicalTools));
      root.Rows.Add(new TableRow(constraintSelectionTools));
      root.Rows.Add(new TableRow(_constraints));
      root.Rows.Add(new TableRow(gearTools));
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
      _templatePlacement.SelectedIndexChanged += (sender, args) => UpdateTemplatePlacement();
      _templateGap.ValueChanged += (sender, args) => UpdateTemplatePlacement();
      _canvas.KeySelectionChanged += RefreshFromModel;
      _canvas.KeyActivated += RefreshFromModel;
      foreach (var editor in PoseEditors())
        editor.ValueChanged += (sender, args) => UpdateSelectedKeyPose();
    }

    private void RefreshFromModel()
    {
      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      if (model == null)
        return;

      _canvas.PruneSelection(model);

      _suppress = true;
      _start.Value = model.StartFrame;
      _end.Value = model.EndFrame;
      _frame.MinValue = model.StartFrame;
      _frame.MaxValue = model.EndFrame;
      _frame.Value = model.CurrentFrame;
      _fps.Value = model.FramesPerSecond;
      _loop.Checked = model.LoopPlayback;
      _templatePlacement.SelectedIndex = (int)model.TemplatePlacement;
      _templateGap.Value = model.TemplateGapFrames;

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
      RefreshKeyEditor(model);

      var parent = track == null ? null : model.FindTrack(track.ParentTrackId);
      var driver = constraint == null ? null : model.FindTrack(constraint.DriverTrackId);
      var parentText = parent == null ? "父级：无" : $"父级：{parent.Name}";
      var constraintText = constraint == null
        ? "传动：无"
        : $"传动：{driver?.Name ?? "未知"} → {track.Name}　比例 {constraint.SignedRatio:0.###}　相位 {constraint.PhaseOffsetDegrees:0.###}°";
      var branchCount = track == null ? 0 : model.ConstraintsForDriver(track.Id).Count;
      var branchText = branchCount > 0 ? $"　分支驱动：{branchCount} 个从动件" : string.Empty;
      _relationship.Text = parentText + "　　" + constraintText + branchText;
      RefreshConstraintList(model, doc, constraint?.Id ?? Guid.Empty);

      var selectedKeyCount = _canvas.SelectedKeys.Count;
      _status.Text = track == null
        ? "先点“添加部件”；若对象已打组但只想动画其中一部分，请点“组内零件”。"
        : $"轨道：{track.Name}　帧：{model.CurrentFrame}　关键帧：{track.Keys.Count}　已选：{selectedKeyCount}　右键框选，Shift 加选，Alt 减选；拖动左侧轨道名可上下排序。";
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
        case MechanicalConstraintType.HelicalGear: return "斜齿";
        case MechanicalConstraintType.BevelGear: return "锥齿";
        case MechanicalConstraintType.RackPinion: return "齿轮-齿条";
        case MechanicalConstraintType.SameShaft: return "同轴刚性";
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

    private void UpdateTemplatePlacement()
    {
      if (_suppress)
        return;
      var placement = (TemplatePlacementMode)Math.Max(0, Math.Min(2, _templatePlacement.SelectedIndex));
      TimelineEngine.UpdateTemplatePlacement(
        RhinoDoc.ActiveDoc,
        placement,
        (int)_templateGap.Value);
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

    private void SelectAllTrackKeys()
    {
      _canvas.SelectAllCurrentTrack(TimelineEngine.Model(RhinoDoc.ActiveDoc));
    }

    private void CopySelectedKeys()
    {
      var doc = RhinoDoc.ActiveDoc;
      var references = _canvas.SelectedKeys.ToList();
      if (references.Count == 0)
      {
        var model = TimelineEngine.Model(doc);
        if (model.SelectedTrack?.FindKey(model.CurrentFrame) != null)
          references.Add(new KeyframeReference(model.SelectedTrack.Id, model.CurrentFrame));
      }
      var copied = TimelineEngine.CopyKeys(doc, references);
      if (copied < 0)
        _status.Text = "一次请复制同一轨道的关键帧；可用右键框选或 Shift/Alt 调整选择。";
      else if (copied == 0)
        _status.Text = "请先选择至少一个关键帧。";
      else
        _status.Text = $"已复制 {copied} 个关键帧；选中其他轨道或 Rhino 对象后点“粘贴到对象”。";
    }

    private void PasteSelectedKeys()
    {
      StopPlayback();
      var doc = RhinoDoc.ActiveDoc;
      var targets = SelectedTargetTrackIds(doc);
      var result = TimelineEngine.PasteCopiedKeys(doc, targets, true);
      if (result.TargetTracks == 0)
        _status.Text = "请先点击目标轨道，或在 Rhino 中选中一个/多个已建轨道的对象。";
      else if (result.Added == 0 && result.Skipped == 0)
        _status.Text = "请先使用“复制所选”复制关键帧。";
      else
        _status.Text = $"已向 {result.TargetTracks} 条轨道添加 {result.Added} 帧；目标已有关键帧的 {result.Skipped} 处已自动跳过。";
    }

    private void RefreshKeyEditor(TimelineDocument model)
    {
      AnimationTrack keyTrack;
      Keyframe key;
      if (!TryEditableKey(model, out keyTrack, out key))
      {
        _keyEditorTitle.Text = "双击关键帧可编辑数值";
        foreach (var editor in PoseEditors())
          editor.Enabled = false;
        return;
      }

      var euler = AnimationMath.ToEulerDegrees(key.Pose.Rotation);
      _keyEditorTitle.Text = $"编辑：{keyTrack.Name} / 第 {key.Frame} 帧";
      _moveX.Value = key.Pose.Translation.X;
      _moveY.Value = key.Pose.Translation.Y;
      _moveZ.Value = key.Pose.Translation.Z;
      _rotateX.Value = euler.X;
      _rotateY.Value = euler.Y;
      _rotateZ.Value = euler.Z;
      _scaleX.Value = key.Pose.Scale.X;
      _scaleY.Value = key.Pose.Scale.Y;
      _scaleZ.Value = key.Pose.Scale.Z;
      foreach (var editor in PoseEditors())
        editor.Enabled = true;
    }

    private void UpdateSelectedKeyPose()
    {
      if (_suppress)
        return;
      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      AnimationTrack track;
      Keyframe key;
      if (!TryEditableKey(model, out track, out key))
      {
        _status.Text = "请双击一个关键帧后再修改位移、旋转或缩放。";
        return;
      }

      var pose = key.Pose.Clone();
      pose.Translation = new Rhino.Geometry.Vector3d(_moveX.Value, _moveY.Value, _moveZ.Value);
      pose.Rotation = AnimationMath.FromEulerDegrees(
        new Rhino.Geometry.Vector3d(_rotateX.Value, _rotateY.Value, _rotateZ.Value));
      pose.Scale = new Rhino.Geometry.Vector3d(_scaleX.Value, _scaleY.Value, _scaleZ.Value);
      if (!TimelineEngine.UpdateKeyPose(doc, track.Id, key.Frame, pose))
      {
        _status.Text = "无法更新该关键帧；缩放值不能为 0。";
        return;
      }
      _status.Text = $"已更新“{track.Name}”第 {key.Frame} 帧，Rhino 对象已同步到修改后的姿态。";
    }

    private bool TryEditableKey(TimelineDocument model, out AnimationTrack track, out Keyframe key)
    {
      track = null;
      key = null;
      if (model == null)
        return false;
      var primary = _canvas.PrimaryKey;
      if (primary.HasValue)
      {
        track = model.FindTrack(primary.Value.TrackId);
        key = track?.FindKey(primary.Value.Frame);
      }
      else
      {
        track = model.SelectedTrack;
        key = track?.FindKey(model.CurrentFrame);
      }
      return track != null && key != null;
    }

    private static List<Guid> SelectedTargetTrackIds(RhinoDoc doc)
    {
      var result = new List<Guid>();
      if (doc == null)
        return result;
      var model = TimelineEngine.Model(doc);
      var selectedObjectIds = new HashSet<Guid>(
        doc.Objects.GetSelectedObjects(false, false).Select(item => item.Id));
      foreach (var track in model.Tracks)
      {
        var instance = TimelineEngine.ResolveInstance(doc, track);
        if (instance != null && selectedObjectIds.Contains(instance.Id))
          result.Add(track.Id);
      }
      if (result.Count == 0 && model.SelectedTrack != null)
        result.Add(model.SelectedTrack.Id);
      return result;
    }

    private IEnumerable<NumericStepper> PoseEditors()
    {
      return new[]
      {
        _moveX, _moveY, _moveZ,
        _rotateX, _rotateY, _rotateZ,
        _scaleX, _scaleY, _scaleZ
      };
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

    private static NumericStepper DecimalStepper(
      double min,
      double max,
      double value,
      double increment)
    {
      return new NumericStepper
      {
        MinValue = min,
        MaxValue = max,
        Value = value,
        DecimalPlaces = 3,
        Increment = increment,
        Width = 68
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
