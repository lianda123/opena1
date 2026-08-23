using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using WoodCheck.Core;

namespace WoodCheck.Commands
{
  public sealed class WoodCheckCommand : Command
  {
    public override string EnglishName => "WoodCheck";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      List<RhinoObject> objects;
      var result = CommandHelpers.GetObjects(
        "选择要进行三项装配检查的实体、轴孔木板和激光曲线（支持打组）",
        out objects);
      return result == Result.Success
        ? WoodCheckRunner.Run(doc, objects, CheckScope.All, "三项综合检查")
        : result;
    }
  }

  public sealed class WoodCheckCollisionCommand : Command
  {
    public override string EnglishName => "WCC";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      List<RhinoObject> objects;
      var result = CommandHelpers.GetObjects(
        "选择要检查实体穿模和真实碰撞体积的零件（支持打组）",
        out objects);
      return result == Result.Success
        ? WoodCheckRunner.Run(doc, objects, CheckScope.Collision, "实体穿模/碰撞体积")
        : result;
    }
  }

  public sealed class WoodCheckAxisCommand : Command
  {
    public override string EnglishName => "WCA";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      List<RhinoObject> objects;
      var result = CommandHelpers.GetObjects(
        "选择含 Ø2mm 附近轴孔的木板实体（支持打组）",
        out objects);
      return result == Result.Success
        ? WoodCheckRunner.Run(doc, objects, CheckScope.Axis, "Ø2mm 轴孔同心")
        : result;
    }
  }

  public sealed class WoodCheckDuplicateCommand : Command
  {
    public override string EnglishName => "WCD";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      List<RhinoObject> objects;
      var result = CommandHelpers.GetObjects(
        "选择要检查重复曲线和激光重复走刀的曲线（支持打组）",
        out objects);
      return result == Result.Success
        ? WoodCheckRunner.Run(doc, objects, CheckScope.DuplicateCurve, "重复曲线/激光重复走刀")
        : result;
    }
  }

  public sealed class WoodCheckAllCommand : Command
  {
    public override string EnglishName => "WCCheckAll";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var objects = doc.Objects
        .GetObjectList(ObjectType.AnyObject)
        .Where(item => !item.IsHidden && !item.IsLocked)
        .Where(item => item.Attributes.GetUserString(MarkerManager.MarkerKey) != MarkerManager.MarkerValue)
        .ToList();
      return WoodCheckRunner.Run(doc, objects, CheckScope.All, "全文件三项检查");
    }
  }

  public sealed class WoodCheckClearCommand : Command
  {
    public override string EnglishName => "WCClearMarkers";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      MarkerManager.Clear(doc);
      doc.Views.Redraw();
      RhinoApp.WriteLine("WoodCheck：问题标记已清除，原模型没有被修改。");
      return Result.Success;
    }
  }

  public sealed class WoodCheckLocateCommand : Command
  {
    public override string EnglishName => "WCLocate";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var code = string.Empty;
      var result = RhinoGet.GetString("输入问题编号，例如 E001、W001 或 I001", false, ref code);
      if (result != Result.Success)
        return result;

      var ids = MarkerManager.FindSourceIds(doc, code);
      if (ids.Count == 0)
      {
        RhinoApp.WriteLine("WoodCheck：未找到问题编号 {0}，请先运行检查命令。", code);
        return Result.Nothing;
      }

      doc.Objects.UnselectAll();
      foreach (var id in ids)
        doc.Objects.Select(id);
      doc.Views.Redraw();
      RhinoApp.WriteLine("WoodCheck：已选中 {0} 对应的 {1} 个源对象。", code.ToUpperInvariant(), ids.Count);
      return Result.Success;
    }
  }

  public sealed class WoodCheckSettingsCommand : Command
  {
    public override string EnglishName => "WCSettings";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var current = WoodCheckPlugin.CurrentSettings;
      var collisionVolume = current.CollisionVolumeMm3;
      var shaftDiameter = current.ShaftDiameterMm;
      var axisTolerance = current.AxisToleranceMm;

      if (!AskPositive("最小真实碰撞体积（mm³）", ref collisionVolume)) return Result.Cancel;
      if (!AskPositive("钢轴孔目标直径（mm）", ref shaftDiameter)) return Result.Cancel;
      if (!AskPositive("孔轴同心允许偏差（mm）", ref axisTolerance)) return Result.Cancel;

      current.CollisionVolumeMm3 = collisionVolume;
      current.ShaftDiameterMm = shaftDiameter;
      current.AxisToleranceMm = axisTolerance;
      RhinoApp.WriteLine(string.Format(
        "WoodCheck 参数：最小碰撞体积 {0:0.###} mm³，轴孔 Ø{1:0.###} mm，同心偏差 {2:0.###} mm。",
        collisionVolume, shaftDiameter, axisTolerance));
      return Result.Success;
    }

    private static bool AskPositive(string prompt, ref double value)
    {
      var result = RhinoGet.GetNumber(prompt, true, ref value);
      if (result == Result.Cancel)
        return false;
      if (value <= 0.0 || double.IsNaN(value) || double.IsInfinity(value))
      {
        RhinoApp.WriteLine("数值必须大于 0。");
        return false;
      }
      return true;
    }
  }

  internal static class CommandHelpers
  {
    public static Result GetObjects(string prompt, out List<RhinoObject> objects)
    {
      objects = new List<RhinoObject>();
      var getter = new GetObject();
      getter.SetCommandPrompt(prompt);
      getter.GroupSelect = true;
      getter.SubObjectSelect = false;
      getter.GeometryFilter = ObjectType.AnyObject;
      getter.GetMultiple(1, 0);
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      objects = Enumerable.Range(0, getter.ObjectCount)
        .Select(index => getter.Object(index).Object())
        .Where(item => item != null)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .ToList();
      return objects.Count > 0 ? Result.Success : Result.Nothing;
    }
  }

  internal static class WoodCheckRunner
  {
    public static Result Run(
      RhinoDoc doc,
      IList<RhinoObject> objects,
      CheckScope scope,
      string label)
    {
      if (objects == null || objects.Count == 0)
      {
        RhinoApp.WriteLine("WoodCheck：没有可检查对象。");
        return Result.Nothing;
      }

      MarkerManager.Clear(doc);
      RhinoApp.WriteLine("WoodCheck：正在执行 {0}，共 {1} 个对象……", label, objects.Count);
      var report = WoodCheckEngine.Run(doc, objects, WoodCheckPlugin.CurrentSettings, scope);
      if (WoodCheckPlugin.CurrentSettings.MarkIssues)
        MarkerManager.Render(doc, report);

      foreach (var issue in report.Issues
        .OrderByDescending(item => item.Severity)
        .ThenBy(item => item.Code))
      {
        RhinoApp.WriteLine("[{0}] {1}：{2}", issue.Code, issue.Title, issue.Message);
      }

      RhinoApp.WriteLine(string.Format(
        "WoodCheck 完成：红色碰撞 {0}，橙色轴孔 {1}，黄色重复曲线 {2}。输入 WCLocate 可按编号定位。",
        report.ErrorCount, report.WarningCount, report.InfoCount));
      doc.Views.Redraw();
      return Result.Success;
    }
  }
}
