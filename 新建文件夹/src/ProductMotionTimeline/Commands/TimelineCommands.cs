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
