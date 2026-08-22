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
      var getter = new GetObject();
      getter.SetCommandPrompt("选择要检查的木板、轴孔和切割曲线（支持多选及打组）");
      getter.GroupSelect = true;
      getter.SubObjectSelect = false;
      getter.GeometryFilter = ObjectType.AnyObject;
      getter.GetMultiple(1, 0);
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      var objects = Enumerable.Range(0, getter.ObjectCount)
        .Select(index => getter.Object(index).Object())
        .Where(item => item != null)
        .ToList();
      return WoodCheckRunner.Run(doc, objects);
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
      return WoodCheckRunner.Run(doc, objects);
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
      var result = RhinoGet.GetString("输入问题编号，例如 E001 或 W002", false, ref code);
      if (result != Result.Success)
        return result;

      var ids = MarkerManager.FindSourceIds(doc, code);
      if (ids.Count == 0)
      {
        RhinoApp.WriteLine("WoodCheck：未找到问题编号 {0}，请先运行 WoodCheck。", code);
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
      var boardThickness = current.NominalBoardThicknessMm;
      var shaftDiameter = current.ShaftDiameterMm;
      var slotDepth = current.MinimumSlotDepthMm;
      var axisTolerance = current.AxisToleranceMm;
      var minimumWall = current.MinimumWallMm;
      var minimumFeature = current.MinimumFeatureMm;
      var openGap = current.OpenCurveGapMm;

      if (!AskPositive("名义木板厚度（mm）", ref boardThickness)) return Result.Cancel;
      if (!AskPositive("钢轴直径（mm）", ref shaftDiameter)) return Result.Cancel;
      if (!AskPositive("最小槽深（mm）", ref slotDepth)) return Result.Cancel;
      if (!AskPositive("孔轴同心允许偏差（mm）", ref axisTolerance)) return Result.Cancel;
      if (!AskPositive("最小剩余壁厚（mm）", ref minimumWall)) return Result.Cancel;
      if (!AskPositive("最小加工特征（mm）", ref minimumFeature)) return Result.Cancel;
      if (!AskPositive("视为断口的首尾间隙（mm）", ref openGap)) return Result.Cancel;

      current.NominalBoardThicknessMm = boardThickness;
      current.ShaftDiameterMm = shaftDiameter;
      current.MinimumSlotDepthMm = slotDepth;
      current.AxisToleranceMm = axisTolerance;
      current.MinimumWallMm = minimumWall;
      current.MinimumFeatureMm = minimumFeature;
      current.OpenCurveGapMm = openGap;
      RhinoApp.WriteLine(
        "WoodCheck 参数已更新：板厚 {0:0.###} mm，轴径 Ø{1:0.###} mm，最小槽深 {2:0.###} mm。",
        boardThickness, shaftDiameter, slotDepth);
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

  internal static class WoodCheckRunner
  {
    public static Result Run(RhinoDoc doc, IList<RhinoObject> objects)
    {
      if (objects == null || objects.Count == 0)
      {
        RhinoApp.WriteLine("WoodCheck：没有可检查对象。");
        return Result.Nothing;
      }

      MarkerManager.Clear(doc);
      RhinoApp.WriteLine("WoodCheck：正在检查 {0} 个对象……", objects.Count);
      var report = WoodCheckEngine.Run(doc, objects, WoodCheckPlugin.CurrentSettings);
      if (WoodCheckPlugin.CurrentSettings.MarkIssues)
        MarkerManager.Render(doc, report);

      foreach (var issue in report.Issues
        .OrderByDescending(item => item.Severity)
        .ThenBy(item => item.Code))
      {
        RhinoApp.WriteLine("[{0}] {1}：{2}", issue.Code, issue.Title, issue.Message);
      }

      RhinoApp.WriteLine(
        "WoodCheck 完成：错误 {0}，警告 {1}，提示 {2}。输入 WCLocate 可按编号定位，WCClearMarkers 可清除标记。",
        report.ErrorCount, report.WarningCount, report.InfoCount);
      doc.Views.Redraw();
      return Result.Success;
    }
  }
}
