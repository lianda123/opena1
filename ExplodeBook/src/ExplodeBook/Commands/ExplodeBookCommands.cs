using System.Collections.Generic;
using System.Linq;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ExplodeBook.Commands
{
  public sealed class ExplodeBookCommand : Command
  {
    public override string EnglishName => "ExplodeBook";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = ExplodeBookPlugin.CurrentSettings;
      if (!CommandHelpers.AskMode(settings) || !CommandHelpers.AskPageKind(settings))
        return Result.Cancel;
      List<RhinoObject> objects;
      var selectionResult = CommandHelpers.GetParts(
        "选择完整装配体：每个物理零件应分别打组", out objects);
      if (selectionResult != Result.Success)
        return selectionResult;

      AssemblyAnalysis analysis;
      var generated = ExplodeBookEngine.Execute(
        doc, objects, settings, true, true, out analysis);
      CommandHelpers.ReportWarnings(analysis);
      if (generated.PartCount == 0)
        return Result.Failure;
      RhinoApp.WriteLine(string.Format(
        "ExplodeBook：已生成 {0} 个零件的爆炸总览、{1} 个装配步骤和 {2} 张 Rhino Layout 页面；顺序来源：{3}。",
        generated.PartCount,
        generated.StepCount,
        generated.LayoutNames.Count,
        analysis.UsedManualOrder ? "EBSetOrder 手动顺序" : "自动接触/距离分析"));
      return Result.Success;
    }
  }

  public sealed class ExplodeBookOverviewCommand : Command
  {
    public override string EnglishName => "EBExplode";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = ExplodeBookPlugin.CurrentSettings;
      if (!CommandHelpers.AskMode(settings))
        return Result.Cancel;
      List<RhinoObject> objects;
      var selectionResult = CommandHelpers.GetParts("选择要制作爆炸图的装配体", out objects);
      if (selectionResult != Result.Success)
        return selectionResult;
      AssemblyAnalysis analysis;
      var generated = ExplodeBookEngine.Execute(
        doc, objects, settings, true, false, out analysis);
      CommandHelpers.ReportWarnings(analysis);
      RhinoApp.WriteLine("ExplodeBook：已生成 {0} 个装配单元的爆炸总览。", generated.PartCount);
      return generated.PartCount > 0 ? Result.Success : Result.Failure;
    }
  }

  public sealed class ExplodeBookPagesCommand : Command
  {
    public override string EnglishName => "EBPages";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = ExplodeBookPlugin.CurrentSettings;
      if (!CommandHelpers.AskMode(settings) || !CommandHelpers.AskPageKind(settings))
        return Result.Cancel;
      List<RhinoObject> objects;
      var selectionResult = CommandHelpers.GetParts("选择要生成说明书页面的装配体", out objects);
      if (selectionResult != Result.Success)
        return selectionResult;
      AssemblyAnalysis analysis;
      var generated = ExplodeBookEngine.Execute(
        doc, objects, settings, false, true, out analysis);
      CommandHelpers.ReportWarnings(analysis);
      RhinoApp.WriteLine("ExplodeBook：已创建 {0} 张 Rhino Layout 说明书页面。", generated.LayoutNames.Count);
      return generated.LayoutNames.Count > 0 ? Result.Success : Result.Failure;
    }
  }

  public sealed class ExplodeBookSetOrderCommand : Command
  {
    public override string EnglishName => "EBSetOrder";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      List<RhinoObject> objects;
      var selectionResult = CommandHelpers.GetParts(
        "按实际安装先后依次点选零件组：第一个应是底座/基准件", out objects);
      if (selectionResult != Result.Success)
        return selectionResult;
      var count = AssemblyAnalyzer.SetManualOrder(doc, objects);
      RhinoApp.WriteLine(
        "ExplodeBook：已记录 {0} 个装配单元的手动顺序；再次运行 ExplodeBook 即按此顺序生成页面。",
        count);
      return count > 0 ? Result.Success : Result.Failure;
    }
  }

  public sealed class ExplodeBookAutoOrderCommand : Command
  {
    public override string EnglishName => "EBAutoOrder";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      List<RhinoObject> objects;
      var selectionResult = CommandHelpers.GetParts("选择要恢复自动排序的装配体", out objects);
      if (selectionResult != Result.Success)
        return selectionResult;
      var count = AssemblyAnalyzer.ClearManualOrder(doc, objects);
      RhinoApp.WriteLine("ExplodeBook：已清除 {0} 个对象的手动顺序，下次将自动分析装配顺序。", count);
      return Result.Success;
    }
  }

  public sealed class ExplodeBookSettingsCommand : Command
  {
    public override string EnglishName => "EBSettings";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = ExplodeBookPlugin.CurrentSettings;
      var distance = settings.ExplodeDistanceMillimeters;
      var arrow = settings.ArrowHeadMillimeters;
      var pageGap = settings.PageGapMillimeters;
      var maximumPages = settings.MaximumStepPages;
      if (!CommandHelpers.AskNumber("基础爆炸距离（mm）", ref distance, 1.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("箭头头部尺寸（mm）", ref arrow, 0.5)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("模型空间说明页间距（mm）", ref pageGap, 0.0)) return Result.Cancel;
      if (!CommandHelpers.AskInteger("最多生成多少张装配步骤页", ref maximumPages, 1)) return Result.Cancel;
      settings.ExplodeDistanceMillimeters = distance;
      settings.ArrowHeadMillimeters = arrow;
      settings.PageGapMillimeters = pageGap;
      settings.MaximumStepPages = maximumPages;
      RhinoApp.WriteLine(string.Format(
        "ExplodeBook 参数：爆炸距离 {0:0.###}mm，箭头 {1:0.###}mm，页面间距 {2:0.###}mm，最多 {3} 个步骤。",
        distance, arrow, pageGap, maximumPages));
      return Result.Success;
    }
  }

  public sealed class ExplodeBookClearCommand : Command
  {
    public override string EnglishName => "EBClear";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var count = ExplodeBookEngine.ClearGenerated(doc);
      RhinoApp.WriteLine("ExplodeBook：已删除 {0} 个插件生成对象及 EB_ 开头的说明书布局，原装配体未删除。", count);
      return Result.Success;
    }
  }

  public sealed class ExplodeBookHelpCommand : Command
  {
    public override string EnglishName => "EBHelp";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      RhinoApp.WriteLine("ExplodeBook 1.0 命令：");
      RhinoApp.WriteLine("  ExplodeBook - 完整爆炸总览 + 装配步骤 + Rhino Layout 说明书");
      RhinoApp.WriteLine("  EBExplode - 只生成完整爆炸图");
      RhinoApp.WriteLine("  EBPages - 只生成 A4/A3 说明书布局页");
      RhinoApp.WriteLine("  EBSetOrder - 按点选先后记录手动装配顺序");
      RhinoApp.WriteLine("  EBAutoOrder - 清除手动顺序并恢复自动分析");
      RhinoApp.WriteLine("  EBSettings - 爆炸距离、箭头尺寸、页间距、最大页数");
      RhinoApp.WriteLine("  EBClear - 清理插件生成结果，不删除原模型");
      RhinoApp.WriteLine("每个物理零件应单独打组；已有 WoodExport 编号会自动沿用。布局页可直接用 Rhino 打印为 PDF。");
      return Result.Success;
    }
  }

  internal static class CommandHelpers
  {
    public static Result GetParts(string prompt, out List<RhinoObject> objects)
    {
      objects = new List<RhinoObject>();
      var getter = new GetObject();
      getter.SetCommandPrompt(prompt);
      getter.GroupSelect = true;
      getter.SubObjectSelect = false;
      getter.GeometryFilter = ObjectType.AnyObject;
      getter.EnablePreSelect(true, true);
      getter.GetMultiple(1, 0);
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      objects = Enumerable.Range(0, getter.ObjectCount)
        .Select(index => getter.Object(index).Object())
        .Where(item => item != null)
        .ToList();
      return objects.Count > 0 ? Result.Success : Result.Nothing;
    }

    public static bool AskMode(ExplodeSettings settings)
    {
      var getter = new GetOption();
      getter.SetCommandPrompt("选择爆炸方向（直接回车默认径向）");
      getter.AcceptNothing(true);
      var radial = getter.AddOption("Radial");
      var xAxis = getter.AddOption("X");
      var yAxis = getter.AddOption("Y");
      var zAxis = getter.AddOption("Z");
      var result = getter.Get();
      if (result == GetResult.Cancel)
        return false;
      if (result == GetResult.Nothing)
      {
        settings.Mode = ExplodeMode.Radial;
        return true;
      }
      if (getter.OptionIndex() == radial) settings.Mode = ExplodeMode.Radial;
      else if (getter.OptionIndex() == xAxis) settings.Mode = ExplodeMode.XAxis;
      else if (getter.OptionIndex() == yAxis) settings.Mode = ExplodeMode.YAxis;
      else if (getter.OptionIndex() == zAxis) settings.Mode = ExplodeMode.ZAxis;
      else return false;
      return true;
    }

    public static bool AskPageKind(ExplodeSettings settings)
    {
      var getter = new GetOption();
      getter.SetCommandPrompt("选择说明书页面（直接回车默认 A4 横向）");
      getter.AcceptNothing(true);
      var a4 = getter.AddOption("A4");
      var a3 = getter.AddOption("A3");
      var a4Portrait = getter.AddOption("A4Portrait");
      var a3Portrait = getter.AddOption("A3Portrait");
      var result = getter.Get();
      if (result == GetResult.Cancel)
        return false;
      if (result == GetResult.Nothing)
      {
        settings.PageKind = ManualPageKind.A4;
        settings.Landscape = true;
        return true;
      }
      if (getter.OptionIndex() == a4)
      {
        settings.PageKind = ManualPageKind.A4;
        settings.Landscape = true;
      }
      else if (getter.OptionIndex() == a3)
      {
        settings.PageKind = ManualPageKind.A3;
        settings.Landscape = true;
      }
      else if (getter.OptionIndex() == a4Portrait)
      {
        settings.PageKind = ManualPageKind.A4;
        settings.Landscape = false;
      }
      else if (getter.OptionIndex() == a3Portrait)
      {
        settings.PageKind = ManualPageKind.A3;
        settings.Landscape = false;
      }
      else return false;
      return true;
    }

    public static bool AskNumber(string prompt, ref double value, double minimum)
    {
      var getter = new GetNumber();
      getter.SetCommandPrompt(prompt);
      getter.SetDefaultNumber(value);
      getter.SetLowerLimit(minimum, false);
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return false;
      value = getter.Number();
      return true;
    }

    public static bool AskInteger(string prompt, ref int value, int minimum)
    {
      var getter = new GetInteger();
      getter.SetCommandPrompt(prompt);
      getter.SetDefaultInteger(value);
      getter.SetLowerLimit(minimum, false);
      getter.Get();
      if (getter.CommandResult() != Result.Success)
        return false;
      value = getter.Number();
      return true;
    }

    public static void ReportWarnings(AssemblyAnalysis analysis)
    {
      foreach (var warning in analysis.Warnings)
        RhinoApp.WriteLine("ExplodeBook：" + warning);
    }
  }
}
