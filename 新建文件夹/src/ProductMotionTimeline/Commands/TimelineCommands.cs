using ProductMotionTimeline.Core;
using ProductMotionTimeline.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using System;
using System.Linq;

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

  public sealed class AutoPivotCommand : Command
  {
    public override string EnglishName => "PMTAutoPivot";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var track = TimelineEngine.Model(doc).SelectedTrack;
      if (track == null)
      {
        RhinoApp.WriteLine("ProductMotion：请先选中一条动画轨道。");
        return Result.Nothing;
      }
      string description;
      var found = TimelineEngine.TryAutoSetPivot(doc, track, out description);
      RhinoApp.WriteLine("ProductMotion：{0}。", description);
      return found ? Result.Success : Result.Nothing;
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

      var module = selectedType == MechanicalConstraintType.Belt
        ? 0.0
        : CommandInput.GetPositiveNumber("输入齿轮模数（0=仅动画比例）", 0.0, true);
      if (module < 0.0)
        return Result.Cancel;

      return TimelineEngine.AddMechanicalConstraint(
        doc,
        driver.Id,
        driven.Id,
        selectedType,
        driverCount,
        drivenCount,
        phaseGetter.Number(),
        module,
        20.0)
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

      string driverAxis;
      string drivenAxis;
      TimelineEngine.TryAutoSetPivot(doc, driver, out driverAxis);
      TimelineEngine.TryAutoSetPivot(doc, driven, out drivenAxis);
      RhinoApp.WriteLine("ProductMotion：主动件 {0}；从动件 {1}。", driverAxis, drivenAxis);

      GearParameters driverGear;
      GearParameters drivenGear;
      var hasDriverGear = GearPartMetadata.TryRead(driverInstance, out driverGear);
      var hasDrivenGear = GearPartMetadata.TryRead(drivenInstance, out drivenGear);
      var driverCount = hasDriverGear && driverGear.Type != GearPartType.Rack
        ? driverGear.Teeth
        : GetPositiveInteger(
          type == MechanicalConstraintType.Belt
            ? "输入主动轮齿数/直径比例"
            : "输入主动齿轮齿数",
          20);
      if (driverCount < 1)
        return Result.Cancel;
      var drivenCount = hasDrivenGear && drivenGear.Type != GearPartType.Rack
        ? drivenGear.Teeth
        : GetPositiveInteger(
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
      var module = 0.0;
      if (type != MechanicalConstraintType.Belt)
      {
        if (hasDriverGear && hasDrivenGear &&
            Math.Abs(driverGear.Module - drivenGear.Module) <= 1e-9)
        {
          module = driverGear.Module;
        }
        else
        {
        var divisor = type == MechanicalConstraintType.ExternalGear
          ? driverCount + drivenCount
          : Math.Abs(drivenCount - driverCount);
        var centerDistance = (TimelineEngine.PivotOrigin(driver) - TimelineEngine.PivotOrigin(driven)).Length;
        var estimatedModule = divisor > 0 ? centerDistance * 2.0 / divisor : 1.0;
        module = CommandInput.GetPositiveNumber(
          "输入齿轮模数（默认按当前中心距反算，0=只做动画）",
          Math.Max(0.001, estimatedModule),
          true);
        if (module < 0.0)
          return Result.Cancel;
        }
      }

      // 完成后重新选中主动轨道，用户只需给主动件卡帧。
      TimelineEngine.SelectTrack(doc, driver.Id);
      if (!TimelineEngine.AddMechanicalConstraint(
        doc,
        driver.Id,
        driven.Id,
        type,
        driverCount,
        drivenCount,
        automaticPhase,
        module,
        hasDriverGear ? driverGear.PressureAngleDegrees : 20.0))
        return Result.Failure;

      var added = TimelineEngine.Model(doc).ConstraintForDriven(driven.Id);
      var validation = TimelineEngine.ValidateMechanicalConstraint(doc, added);
      RhinoApp.WriteLine("ProductMotion：啮合检查：{0}。", validation.Message);

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

  public sealed class BindMultipleDrivenCommand : Command
  {
    public override string EnglishName => "PMTBindMultiple";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var model = TimelineEngine.Model(doc);
      var driver = model.SelectedTrack;
      if (driver == null)
      {
        RhinoApp.WriteLine("ProductMotion：请先在时间轴选中唯一主动件。");
        return Result.Nothing;
      }

      var typeGetter = new GetOption();
      typeGetter.SetCommandPrompt("选择分支传动类型（Auto 会读取插件生成的齿轮参数）");
      var auto = typeGetter.AddOption("Auto");
      var external = typeGetter.AddOption("ExternalGear");
      var internalGear = typeGetter.AddOption("InternalGear");
      var belt = typeGetter.AddOption("Belt");
      var helical = typeGetter.AddOption("HelicalGear");
      var bevel = typeGetter.AddOption("BevelGear");
      var rack = typeGetter.AddOption("RackPinion");
      var helical = typeGetter.AddOption("HelicalGear");
      var bevel = typeGetter.AddOption("BevelGear");
      var rack = typeGetter.AddOption("RackPinion");
      typeGetter.Get();
      if (typeGetter.CommandResult() != Result.Success)
        return typeGetter.CommandResult();
      MechanicalConstraintType? fixedType = null;
      if (typeGetter.OptionIndex() == external) fixedType = MechanicalConstraintType.ExternalGear;
      else if (typeGetter.OptionIndex() == internalGear) fixedType = MechanicalConstraintType.InternalGear;
      else if (typeGetter.OptionIndex() == belt) fixedType = MechanicalConstraintType.Belt;
      else if (typeGetter.OptionIndex() == helical) fixedType = MechanicalConstraintType.HelicalGear;
      else if (typeGetter.OptionIndex() == bevel) fixedType = MechanicalConstraintType.BevelGear;
      else if (typeGetter.OptionIndex() == rack) fixedType = MechanicalConstraintType.RackPinion;
      else if (typeGetter.OptionIndex() != auto) return Result.Cancel;

      var getter = new GetObject();
      getter.SetCommandPrompt("选择一个或多个从动动画部件，选完按回车");
      getter.GeometryFilter = ObjectType.InstanceReference;
      getter.GroupSelect = false;
      getter.SubObjectSelect = false;
      getter.EnablePreSelect(false, true);
      getter.GetMultiple(1, 0);
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      var driverInstance = TimelineEngine.ResolveInstance(doc, driver);
      GearParameters driverGear;
      var hasDriverGear = GearPartMetadata.TryRead(driverInstance, out driverGear);
      var manualDriverTeeth = 0;
      var successCount = 0;
      for (var index = 0; index < getter.ObjectCount; index++)
      {
        var drivenInstance = getter.Object(index).Object() as InstanceObject;
        if (drivenInstance == null)
          continue;
        var driven = TimelineEngine.AddTrack(doc, drivenInstance);
        if (driven == null || driven.Id == driver.Id)
          continue;
        string ignored;
        if (!hasDriverGear)
          TimelineEngine.TryAutoSetPivot(doc, driver, out ignored);
        GearParameters drivenGear;
        var hasDrivenGear = GearPartMetadata.TryRead(drivenInstance, out drivenGear);
        if (!hasDrivenGear)
          TimelineEngine.TryAutoSetPivot(doc, driven, out ignored);

        var type = fixedType ?? GearPartMetadata.InferConstraintType(driverGear, drivenGear);
        if (type == MechanicalConstraintType.RackPinion &&
            (!hasDrivenGear || drivenGear.Type != GearPartType.Rack))
        {
          RhinoApp.WriteLine("ProductMotion：跳过“{0}”：齿轮-齿条传动必须把齿条作为从动件。", driven.Name);
          continue;
        }
        var driverTeeth = hasDriverGear && driverGear.Type != GearPartType.Rack
          ? driverGear.Teeth
          : manualDriverTeeth;
        if (driverTeeth < 1)
        {
          manualDriverTeeth = CommandInput.GetPositiveInteger("输入主动件齿数/比例", 20);
          driverTeeth = manualDriverTeeth;
        }
        if (driverTeeth < 1)
          return Result.Cancel;
        var drivenTeeth = type == MechanicalConstraintType.RackPinion
          ? 1
          : hasDrivenGear && drivenGear.Type != GearPartType.Rack
            ? drivenGear.Teeth
            : CommandInput.GetPositiveInteger("输入“" + driven.Name + "”齿数/比例", 20);
        if (drivenTeeth < 1)
          return Result.Cancel;

        var module = type == MechanicalConstraintType.Belt
          ? 0.0
          : hasDriverGear
            ? driverGear.Module
            : hasDrivenGear
              ? drivenGear.Module
              : CommandInput.GetPositiveNumber("输入“" + driven.Name + "”传动模数", 1.0, false);
        if (module < 0.0)
          return Result.Cancel;
        var pressure = hasDriverGear ? driverGear.PressureAngleDegrees : 20.0;
        var frame = model.CurrentFrame;
        var driverAngle = TimelineEngine.EffectiveMechanicalAngle(doc, driver, frame);
        var phaseAngle = 0.0;
        var phaseDistance = 0.0;
        if (type == MechanicalConstraintType.RackPinion)
        {
          var pose = TimelineEngine.EffectivePose(doc, driven, frame);
          phaseDistance = AxisComponent(pose.Translation, RotationAxis.X) -
                          driverAngle / 360.0 * Math.PI * module * driverTeeth;
        }
        else
        {
          var drivenAngle = TimelineEngine.EffectiveMechanicalAngle(doc, driven, frame);
          var preview = new MechanicalConstraint
          {
            Type = type,
            DriverTeeth = driverTeeth,
            DrivenTeeth = drivenTeeth
          };
          phaseAngle = drivenAngle - driverAngle * preview.SignedRatio;
        }

        if (TimelineEngine.AddMechanicalConstraint(
          doc,
          driver.Id,
          driven.Id,
          type,
          driverTeeth,
          drivenTeeth,
          phaseAngle,
          module,
          pressure,
          phaseDistance,
          RotationAxis.X,
          1.0))
          successCount++;
      }

      TimelineEngine.SelectTrack(doc, driver.Id);
      Panels.OpenPanel(TimelinePanel.PanelId);
      RhinoApp.WriteLine(
        "ProductMotion：已从“{0}”建立 {1} 条分支传动；这些从动件仍可继续驱动下一级。",
        driver.Name,
        successCount);
      return successCount > 0 ? Result.Success : Result.Nothing;
    }

    private static double AxisComponent(Vector3d vector, RotationAxis axis)
    {
      switch (axis)
      {
        case RotationAxis.Y: return vector.Y;
        case RotationAxis.Z: return vector.Z;
        default: return vector.X;
      }
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

  public sealed class EditMechanicalConstraintCommand : Command
  {
    public override string EnglishName => "PMTEditMechanical";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var model = TimelineEngine.Model(doc);
      var selectedTrack = model.SelectedTrack;
      var constraint = selectedTrack == null ? null : model.ConstraintForDriven(selectedTrack.Id);
      if (constraint == null && selectedTrack != null)
        constraint = model.Constraints.FirstOrDefault(item => item.DriverTrackId == selectedTrack.Id);
      if (constraint == null)
      {
        RhinoApp.WriteLine("ProductMotion：请先在传动关系列表中选中一项。");
        return Result.Nothing;
      }

      var type = constraint.Type;
      var typeGetter = new GetOption();
      typeGetter.SetCommandPrompt("修改传动类型（按回车保留当前类型）");
      typeGetter.AcceptNothing(true);
      var external = typeGetter.AddOption("ExternalGear");
      var internalGear = typeGetter.AddOption("InternalGear");
      var belt = typeGetter.AddOption("Belt");
      var typeResult = typeGetter.Get();
      if (typeResult == GetResult.Cancel)
        return Result.Cancel;
      if (typeResult == GetResult.Option)
      {
        if (typeGetter.OptionIndex() == internalGear) type = MechanicalConstraintType.InternalGear;
        else if (typeGetter.OptionIndex() == belt) type = MechanicalConstraintType.Belt;
        else if (typeGetter.OptionIndex() == helical) type = MechanicalConstraintType.HelicalGear;
        else if (typeGetter.OptionIndex() == bevel) type = MechanicalConstraintType.BevelGear;
        else if (typeGetter.OptionIndex() == rack) type = MechanicalConstraintType.RackPinion;
        else if (typeGetter.OptionIndex() == external) type = MechanicalConstraintType.ExternalGear;
      }

      var driverCount = CommandInput.GetPositiveInteger("主动齿数/皮带轮比例", constraint.DriverTeeth);
      if (driverCount < 1) return Result.Cancel;
      var drivenCount = type == MechanicalConstraintType.RackPinion
        ? 1
        : CommandInput.GetPositiveInteger("从动齿数/皮带轮比例", constraint.DrivenTeeth);
      if (drivenCount < 1) return Result.Cancel;
      var module = type == MechanicalConstraintType.Belt
        ? 0.0
        : CommandInput.GetPositiveNumber("模数（0=跳过中心距检查）", constraint.Module, true);
      if (module < 0.0) return Result.Cancel;
      var pressureAngle = type == MechanicalConstraintType.Belt
        ? constraint.PressureAngleDegrees
        : CommandInput.GetPositiveNumber("压力角°", constraint.PressureAngleDegrees, false);
      if (pressureAngle < 0.0) return Result.Cancel;
      var phase = CommandInput.GetNumber("相位角°", constraint.PhaseOffsetDegrees);
      if (double.IsNaN(phase)) return Result.Cancel;
      var phaseDistance = type == MechanicalConstraintType.RackPinion
        ? CommandInput.GetNumber("齿条起始偏移", constraint.PhaseOffsetDistance)
        : constraint.PhaseOffsetDistance;
      if (double.IsNaN(phaseDistance)) return Result.Cancel;
      var linearAxis = type == MechanicalConstraintType.RackPinion
        ? CommandInput.GetAxis("齿条移动轴", constraint.DrivenLinearAxis)
        : constraint.DrivenLinearAxis;
      var direction = CommandInput.GetDirectionMultiplier(constraint.DirectionMultiplier);
      if (double.IsNaN(direction)) return Result.Cancel;

      if (!TimelineEngine.UpdateMechanicalConstraint(
        doc, constraint.Id, type, driverCount, drivenCount, module, pressureAngle, phase,
        phaseDistance, linearAxis, direction))
        return Result.Failure;
      var validation = TimelineEngine.ValidateMechanicalConstraint(doc, constraint);
      RhinoApp.WriteLine("ProductMotion：更新完成，{0}。", validation.Message);
      return Result.Success;
    }
  }

  public sealed class ValidateMechanicalConstraintsCommand : Command
  {
    public override string EnglishName => "PMTValidateMechanical";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var model = TimelineEngine.Model(doc);
      if (model.Constraints.Count == 0)
      {
        RhinoApp.WriteLine("ProductMotion：当前没有机械传动关系。");
        return Result.Nothing;
      }
      foreach (var constraint in model.Constraints)
      {
        var driver = model.FindTrack(constraint.DriverTrackId);
        var driven = model.FindTrack(constraint.DrivenTrackId);
        var validation = TimelineEngine.ValidateMechanicalConstraint(doc, constraint);
        RhinoApp.WriteLine(
          "ProductMotion：{0} → {1}：{2}",
          driver?.Name ?? "?",
          driven?.Name ?? "?",
          validation.Message);
      }
      return Result.Success;
    }
  }

  public sealed class ReciprocateTemplateCommand : Command
  {
    public override string EnglishName => "PMTReciprocate";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var track = TemplateCommandInput.SelectedIndependentTrack(doc);
      if (track == null) return Result.Nothing;
      var amplitude = CommandInput.GetPositiveNumber("单侧摆角°", 30.0, false);
      if (amplitude < 0.0) return Result.Cancel;
      var duration = CommandInput.GetPositiveInteger("总帧数", 120);
      if (duration < 1) return Result.Cancel;
      var cycles = CommandInput.GetPositiveInteger("往复次数", 1);
      if (cycles < 1) return Result.Cancel;
      MotionTemplateGenerator.GenerateReciprocation(
        doc, track, TimelineEngine.TemplateStartFrame(doc, track), duration, cycles, amplitude);
      RhinoApp.WriteLine("ProductMotion：已生成往复摆动关键帧，可继续拖动修改。");
      return Result.Success;
    }
  }

  public sealed class ReboundTemplateCommand : Command
  {
    public override string EnglishName => "PMTRebound";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var track = TemplateCommandInput.SelectedIndependentTrack(doc);
      if (track == null) return Result.Nothing;
      var angle = CommandInput.GetNumber("拨动角度°（可输入负数改变方向）", 90.0);
      if (double.IsNaN(angle)) return Result.Cancel;
      var duration = CommandInput.GetPositiveInteger("总帧数", 90);
      if (duration < 1) return Result.Cancel;
      MotionTemplateGenerator.GenerateRebound(
        doc, track, TimelineEngine.TemplateStartFrame(doc, track), duration, angle);
      RhinoApp.WriteLine("ProductMotion：已生成“快速拨动—短暂停留—平滑回弹”动作。");
      return Result.Success;
    }
  }

  public sealed class CrankSliderTemplateCommand : Command
  {
    public override string EnglishName => "PMTCrankSlider";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var crank = TemplateCommandInput.SelectPart(doc, "选择曲柄/偏心轮主动件");
      if (crank == null) return Result.Cancel;
      var crankObject = TimelineEngine.ResolveInstance(doc, crank);
      crankObject?.Select(false);
      var slider = TemplateCommandInput.SelectPart(doc, "选择直线往复的滑块");
      if (slider == null || slider.Id == crank.Id) return Result.Cancel;
      string ignored;
      TimelineEngine.TryAutoSetPivot(doc, crank, out ignored);
      var radius = CommandInput.GetPositiveNumber("曲柄半径", 10.0, false);
      if (radius <= 0.0) return Result.Cancel;
      var rod = CommandInput.GetPositiveNumber("连杆长度（必须大于等于曲柄半径）", Math.Max(30.0, radius), false);
      if (rod < radius) return Result.Cancel;
      var axis = CommandInput.GetAxis("选择滑块运动轴", RotationAxis.X);
      var duration = CommandInput.GetPositiveInteger("曲柄转一圈的帧数", 120);
      if (duration < 1) return Result.Cancel;
      MotionTemplateGenerator.GenerateCrankSlider(
        doc, crank, slider, TimelineEngine.TemplateStartFrame(doc, crank, slider), duration, radius, rod, axis);
      RhinoApp.WriteLine("ProductMotion：已按真实曲柄-连杆公式生成滑块位移。");
      return Result.Success;
    }
  }

  public sealed class FourBarTemplateCommand : Command
  {
    public override string EnglishName => "PMTFourBar";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var crank = TemplateCommandInput.SelectPart(doc, "选择四连杆的主动曲柄");
      if (crank == null) return Result.Cancel;
      TimelineEngine.ResolveInstance(doc, crank)?.Select(false);
      var rocker = TemplateCommandInput.SelectPart(doc, "选择四连杆的输出摇杆");
      if (rocker == null || rocker.Id == crank.Id) return Result.Cancel;
      string ignored;
      TimelineEngine.TryAutoSetPivot(doc, crank, out ignored);
      TimelineEngine.TryAutoSetPivot(doc, rocker, out ignored);
      var measuredGround = TimelineEngine.PivotOrigin(crank).DistanceTo(TimelineEngine.PivotOrigin(rocker));
      var ground = CommandInput.GetPositiveNumber("两固定轴中心距", Math.Max(1.0, measuredGround), false);
      if (ground <= 0.0) return Result.Cancel;
      var crankLength = CommandInput.GetPositiveNumber("主动曲柄长度", ground * 0.25, false);
      if (crankLength <= 0.0) return Result.Cancel;
      var couplerLength = CommandInput.GetPositiveNumber("中间连杆长度", ground * 0.75, false);
      if (couplerLength <= 0.0) return Result.Cancel;
      var rockerLength = CommandInput.GetPositiveNumber("输出摇杆长度", ground * 0.5, false);
      if (rockerLength <= 0.0) return Result.Cancel;
      var duration = CommandInput.GetPositiveInteger("主动曲柄转一圈的帧数", 120);
      if (duration < 1) return Result.Cancel;
      string error;
      if (!MotionTemplateGenerator.GenerateFourBar(
        doc, crank, rocker, TimelineEngine.TemplateStartFrame(doc, crank, rocker), duration,
        ground, crankLength, couplerLength, rockerLength, out error))
      {
        RhinoApp.WriteLine("ProductMotion：{0}", error);
        return Result.Nothing;
      }
      RhinoApp.WriteLine("ProductMotion：已按四杆闭环几何生成主动曲柄和输出摇杆关键帧。");
      return Result.Success;
    }
  }

  internal static class TemplateCommandInput
  {
    internal static AnimationTrack SelectedIndependentTrack(RhinoDoc doc)
    {
      var model = TimelineEngine.Model(doc);
      var track = model.SelectedTrack;
      if (track == null)
      {
        RhinoApp.WriteLine("ProductMotion：请先在时间轴中选中一条轨道。");
        return null;
      }
      if (model.ConstraintForDriven(track.Id) != null)
      {
        RhinoApp.WriteLine("ProductMotion：当前轨道是机械从动件，请对主动件使用动作模板。");
        return null;
      }
      return track;
    }

    internal static AnimationTrack SelectPart(RhinoDoc doc, string prompt)
    {
      var instance = TrackFactory.GetOrCreateGroupPart(doc, prompt, false);
      return instance == null ? null : TimelineEngine.AddTrack(doc, instance);
    }
  }

  internal static class CommandInput
  {
    internal static int GetPositiveInteger(string prompt, int defaultValue)
    {
      var getter = new GetInteger();
      getter.SetCommandPrompt(prompt);
      getter.SetLowerLimit(1, false);
      getter.SetDefaultInteger(Math.Max(1, defaultValue));
      getter.Get();
      return getter.CommandResult() == Result.Success ? getter.Number() : -1;
    }

    internal static double GetPositiveNumber(string prompt, double defaultValue, bool allowZero)
    {
      var getter = new GetNumber();
      getter.SetCommandPrompt(prompt);
      getter.SetLowerLimit(allowZero ? 0.0 : 1e-9, false);
      getter.SetDefaultNumber(Math.Max(allowZero ? 0.0 : 1e-9, defaultValue));
      getter.Get();
      return getter.CommandResult() == Result.Success ? getter.Number() : -1.0;
    }

    internal static double GetNumber(string prompt, double defaultValue)
    {
      var getter = new GetNumber();
      getter.SetCommandPrompt(prompt);
      getter.SetDefaultNumber(defaultValue);
      getter.Get();
      return getter.CommandResult() == Result.Success ? getter.Number() : double.NaN;
    }

    internal static RotationAxis GetAxis(string prompt, RotationAxis defaultAxis)
    {
      var getter = new GetOption();
      getter.SetCommandPrompt(prompt);
      getter.AcceptNothing(true);
      var x = getter.AddOption("X");
      var y = getter.AddOption("Y");
      var z = getter.AddOption("Z");
      var result = getter.Get();
      if (result != GetResult.Option)
        return defaultAxis;
      if (getter.OptionIndex() == y) return RotationAxis.Y;
      if (getter.OptionIndex() == z) return RotationAxis.Z;
      return getter.OptionIndex() == x ? RotationAxis.X : defaultAxis;
    }

    internal static double GetDirectionMultiplier(double current)
    {
      var getter = new GetOption();
      getter.SetCommandPrompt("传动方向（按回车保留）");
      getter.AcceptNothing(true);
      var normal = getter.AddOption("Normal");
      var reverse = getter.AddOption("Reverse");
      var result = getter.Get();
      if (result == GetResult.Cancel)
        return double.NaN;
      if (result != GetResult.Option)
        return current;
      return getter.OptionIndex() == reverse ? -1.0 : getter.OptionIndex() == normal ? 1.0 : current;
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
