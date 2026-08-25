using ProductMotionTimeline.Core;
using ProductMotionTimeline.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace ProductMotionTimeline.Commands
{
  public sealed class OpenTimelineCommand : Command
  {
    public override string EnglishName => "PMTimeline";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      Panels.OpenPanel(TimelinePanel.PanelId);
      return Result.Success;
    }
  }

  public sealed class AddAnimationPartCommand : Command
  {
    public override string EnglishName => "PMTAddPart";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var instance = TrackFactory.GetOrCreateAnimationPart(doc);
      if (instance == null)
        return Result.Cancel;
      TimelineEngine.AddTrack(doc, instance);
      Panels.OpenPanel(TimelinePanel.PanelId);
      return Result.Success;
    }
  }

  public sealed class AddGroupPartCommand : Command
  {
    public override string EnglishName => "PMTAddGroupPart";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var instance = TrackFactory.GetOrCreateGroupPart(doc);
      if (instance == null)
        return Result.Cancel;
      TimelineEngine.AddTrack(doc, instance);
      Panels.OpenPanel(TimelinePanel.PanelId);
      RhinoApp.WriteLine("ProductMotion：已把所选组内零件建立为独立动画轨道，可继续设置父级或关键帧。");
      return Result.Success;
    }
  }

  public sealed class InsertKeyCommand : Command
  {
    public override string EnglishName => "PMTKey";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return TimelineEngine.InsertOrUpdateKey(doc, InterpolationMode.Smooth)
        ? Result.Success
        : Result.Failure;
    }
  }

  public sealed class DeleteKeyCommand : Command
  {
    public override string EnglishName => "PMTDeleteKey";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return TimelineEngine.DeleteKey(doc) ? Result.Success : Result.Nothing;
    }
  }

  public sealed class SetPivotCommand : Command
  {
    public override string EnglishName => "PMTSetPivot";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      if (TimelineEngine.Model(doc).SelectedTrack == null)
      {
        RhinoApp.WriteLine("ProductMotion：请先添加并选择一条动画轨道。");
        return Result.Nothing;
      }

      var getter = new GetPoint();
      getter.SetCommandPrompt("指定旋转/缩放轴心（轴方向沿当前工作平面 XYZ）");
      getter.DynamicDraw += (sender, args) =>
      {
        args.Display.DrawPoint(args.CurrentPoint, Rhino.Display.PointStyle.RoundControlPoint, 7, System.Drawing.Color.FromArgb(255, 172, 57));
      };
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      return TimelineEngine.SetPivot(doc, getter.Point()) ? Result.Success : Result.Failure;
    }
  }

  public sealed class RebindTrackCommand : Command
  {
    public override string EnglishName => "PMTRebind";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      if (TimelineEngine.Model(doc).SelectedTrack == null)
      {
        RhinoApp.WriteLine("ProductMotion：请先选择要修复的轨道。");
        return Result.Nothing;
      }

      var getter = new GetObject();
      getter.SetCommandPrompt("选择用于重新绑定的块实例");
      getter.GeometryFilter = ObjectType.InstanceReference;
      getter.SubObjectSelect = false;
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      var instance = getter.Object(0).Object() as InstanceObject;
      return TimelineEngine.RebindSelectedTrack(doc, instance) ? Result.Success : Result.Failure;
    }
  }

  public sealed class SetParentTrackCommand : Command
  {
    public override string EnglishName => "PMTSetParent";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var child = TimelineEngine.Model(doc).SelectedTrack;
      if (child == null)
      {
        RhinoApp.WriteLine("ProductMotion：请先在时间轴中选中子级轨道。");
        return Result.Nothing;
      }

      var getter = new GetObject();
      getter.SetCommandPrompt("选择要作为父级的动画部件");
      getter.GeometryFilter = ObjectType.InstanceReference;
      getter.GroupSelect = false;
      getter.SubObjectSelect = false;
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      var parent = TimelineEngine.FindTrackForInstance(doc, getter.Object(0).Object() as InstanceObject);
      if (parent == null)
      {
        RhinoApp.WriteLine("ProductMotion：所选对象还没有动画轨道，请先添加部件。");
        return Result.Nothing;
      }
      return TimelineEngine.SetParent(doc, child.Id, parent.Id) ? Result.Success : Result.Failure;
    }
  }

  public sealed class ClearParentTrackCommand : Command
  {
    public override string EnglishName => "PMTClearParent";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var child = TimelineEngine.Model(doc).SelectedTrack;
      return child != null && TimelineEngine.ClearParent(doc, child.Id)
        ? Result.Success
        : Result.Nothing;
    }
  }

  public sealed class BindMechanicalConstraintCommand : Command
  {
    public override string EnglishName => "PMTBindMechanical";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var driver = TimelineEngine.Model(doc).SelectedTrack;
      if (driver == null)
      {
        RhinoApp.WriteLine("ProductMotion：请先在时间轴中选中主动件轨道。");
        return Result.Nothing;
      }

      var getter = new GetObject();
      getter.SetCommandPrompt("选择由主动件带动的从动动画部件");
      getter.GeometryFilter = ObjectType.InstanceReference;
      getter.GroupSelect = false;
      getter.SubObjectSelect = false;
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      var driven = TimelineEngine.FindTrackForInstance(doc, getter.Object(0).Object() as InstanceObject);
      if (driven == null)
      {
        RhinoApp.WriteLine("ProductMotion：所选从动件还没有动画轨道，请先添加部件。");
        return Result.Nothing;
      }

      var typeGetter = new GetOption();
      typeGetter.SetCommandPrompt("选择机械传动类型");
      var externalIndex = typeGetter.AddOption("ExternalGear");
      var internalIndex = typeGetter.AddOption("InternalGear");
      var beltIndex = typeGetter.AddOption("Belt");
      typeGetter.Get();
      if (typeGetter.CommandResult() != Result.Success)
        return typeGetter.CommandResult();
      var selectedType = MechanicalConstraintType.ExternalGear;
      if (typeGetter.OptionIndex() == internalIndex)
        selectedType = MechanicalConstraintType.InternalGear;
      else if (typeGetter.OptionIndex() == beltIndex)
        selectedType = MechanicalConstraintType.Belt;
      else if (typeGetter.OptionIndex() != externalIndex)
        return Result.Cancel;

      var driverCount = GetPositiveInteger("输入主动齿轮齿数/主动轮节数", 20);
      if (driverCount < 1)
        return Result.Cancel;
      var drivenCount = GetPositiveInteger("输入从动齿轮齿数/从动轮节数", 20);
      if (drivenCount < 1)
        return Result.Cancel;

      var driverAngle = TimelineEngine.EffectiveMechanicalAngle(
        doc,
        driver,
        TimelineEngine.Model(doc).CurrentFrame);
      var drivenAngle = TimelineEngine.EffectiveMechanicalAngle(
        doc,
        driven,
        TimelineEngine.Model(doc).CurrentFrame);
      var defaultConstraint = new MechanicalConstraint
      {
        Type = selectedType,
        DriverTeeth = driverCount,
        DrivenTeeth = drivenCount
      };
      var defaultPhase = drivenAngle - driverAngle * defaultConstraint.SignedRatio;
      var phaseGetter = new GetNumber();
      phaseGetter.SetCommandPrompt("输入从动件相位角（默认值保持当前啮合姿态）");
      phaseGetter.SetDefaultNumber(defaultPhase);
      phaseGetter.Get();
      if (phaseGetter.CommandResult() != Result.Success)
        return phaseGetter.CommandResult();

      return TimelineEngine.AddMechanicalConstraint(
        doc,
        driver.Id,
        driven.Id,
        selectedType,
        driverCount,
        drivenCount,
        phaseGetter.Number())
        ? Result.Success
        : Result.Failure;
    }

    private static int GetPositiveInteger(string prompt, int defaultValue)
    {
      var getter = new GetInteger();
      getter.SetCommandPrompt(prompt);
      getter.SetLowerLimit(1, false);
      getter.SetDefaultInteger(defaultValue);
      getter.Get();
      return getter.CommandResult() == Result.Success ? getter.Number() : -1;
    }
  }

  internal static class QuickMechanicalBinding
  {
    internal static Result Run(RhinoDoc doc, MechanicalConstraintType type)
    {
      var driverInstance = TrackFactory.GetOrCreateGroupPart(
        doc,
        "选择主动件（可在现有Rhino组内单独选择；多选后按回车）",
        false);
      if (driverInstance == null)
        return Result.Cancel;
      var driver = TimelineEngine.AddTrack(doc, driverInstance);
      if (driver == null)
        return Result.Failure;
      driverInstance.Select(false);

      var drivenInstance = TrackFactory.GetOrCreateGroupPart(
        doc,
        "选择从动件（可在现有Rhino组内单独选择；多选后按回车）",
        false);
      if (drivenInstance == null)
        return Result.Cancel;
      var driven = TimelineEngine.AddTrack(doc, drivenInstance);
      if (driven == null || driven.Id == driver.Id)
      {
        RhinoApp.WriteLine("ProductMotion：主动件和从动件必须是两个不同的动画部件。");
        return Result.Nothing;
      }

      var driverCount = GetPositiveInteger(
        type == MechanicalConstraintType.Belt
          ? "输入主动轮齿数/直径比例"
          : "输入主动齿轮齿数",
        20);
      if (driverCount < 1)
        return Result.Cancel;
      var drivenCount = GetPositiveInteger(
        type == MechanicalConstraintType.Belt
          ? "输入从动轮齿数/直径比例"
          : "输入从动齿轮齿数",
        20);
      if (drivenCount < 1)
        return Result.Cancel;

      var frame = TimelineEngine.Model(doc).CurrentFrame;
      var driverAngle = TimelineEngine.EffectiveMechanicalAngle(doc, driver, frame);
      var drivenAngle = TimelineEngine.EffectiveMechanicalAngle(doc, driven, frame);
      var template = new MechanicalConstraint
      {
        Type = type,
        DriverTeeth = driverCount,
        DrivenTeeth = drivenCount
      };
      var automaticPhase = drivenAngle - driverAngle * template.SignedRatio;

      // 完成后重新选中主动轨道，用户只需给主动件卡帧。
      TimelineEngine.SelectTrack(doc, driver.Id);
      if (!TimelineEngine.AddMechanicalConstraint(
        doc,
        driver.Id,
        driven.Id,
        type,
        driverCount,
        drivenCount,
        automaticPhase))
        return Result.Failure;

      Panels.OpenPanel(TimelinePanel.PanelId);
      RhinoApp.WriteLine(
        "ProductMotion：快速传动已完成。只需给主动件设置关键帧；普通Gumball绕轴旋转和连续转角都可驱动从动件。");
      return Result.Success;
    }

    private static int GetPositiveInteger(string prompt, int defaultValue)
    {
      var getter = new GetInteger();
      getter.SetCommandPrompt(prompt);
      getter.SetLowerLimit(1, false);
      getter.SetDefaultInteger(defaultValue);
      getter.Get();
      return getter.CommandResult() == Result.Success ? getter.Number() : -1;
    }
  }

  public sealed class QuickExternalGearCommand : Command
  {
    public override string EnglishName => "PMTExternalGear";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return QuickMechanicalBinding.Run(doc, MechanicalConstraintType.ExternalGear);
    }
  }

  public sealed class QuickInternalGearCommand : Command
  {
    public override string EnglishName => "PMTInternalGear";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return QuickMechanicalBinding.Run(doc, MechanicalConstraintType.InternalGear);
    }
  }

  public sealed class QuickBeltCommand : Command
  {
    public override string EnglishName => "PMTBelt";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return QuickMechanicalBinding.Run(doc, MechanicalConstraintType.Belt);
    }
  }

  public sealed class DeleteMechanicalConstraintCommand : Command
  {
    public override string EnglishName => "PMTDeleteMechanical";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var driven = TimelineEngine.Model(doc).SelectedTrack;
      return driven != null && TimelineEngine.DeleteConstraintForDriven(doc, driven.Id)
        ? Result.Success
        : Result.Nothing;
    }
  }

  public sealed class TogglePlaybackCommand : Command
  {
    public override string EnglishName => "PMTPlay";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      Panels.OpenPanel(TimelinePanel.PanelId);
      TimelinePanel.RequestTogglePlayback();
      return Result.Success;
    }
  }
}
