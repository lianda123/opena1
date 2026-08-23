using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using WoodExport.Core;

namespace WoodExport.Commands
{
  public sealed class WoodExportCommand : Command
  {
    public override string EnglishName => "WoodExport";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      ExportFormat format;
      if (!CommandHelpers.AskFormat(out format))
        return Result.Cancel;

      List<RhinoObject> objects;
      var selectionResult = CommandHelpers.GetParts(out objects);
      if (selectionResult != Result.Success)
        return selectionResult;

      string bomPath;
      if (!CommandHelpers.AskBomPath(out bomPath))
        return Result.Cancel;

      var settings = WoodExportPlugin.CurrentSettings;
      var result = WoodExportEngine.Analyze(doc, objects, settings);
      CommandHelpers.ReportWarnings(result);
      if (result.Parts.Count == 0)
      {
        RhinoApp.WriteLine("WoodExport：没有识别到板件。请把实体板和所属刀线/雕刻线打组后再选择。");
        return Result.Failure;
      }

      var labelsSucceeded = WoodExportEngine.ApplyNumberingAndLabels(doc, result, settings);
      var bomSucceeded = WoodExportEngine.WriteBom(result, bomPath);
      var cadSucceeded = true;
      if (format != ExportFormat.BomOnly)
      {
        var directory = Path.GetDirectoryName(bomPath) ?? System.Environment.CurrentDirectory;
        var baseName = CommandHelpers.ExportBaseName(bomPath);
        cadSucceeded = WoodExportEngine.ExportCadByThickness(
          doc, result, settings, directory, baseName, format);
      }

      CommandHelpers.ReportSummary(result, format);
      CommandHelpers.ReportWarnings(result);
      return labelsSucceeded && bomSucceeded && cadSucceeded ? Result.Success : Result.Failure;
    }
  }

  public sealed class WoodExportNumberCommand : Command
  {
    public override string EnglishName => "WXNumber";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      List<RhinoObject> objects;
      var selectionResult = CommandHelpers.GetParts(out objects);
      if (selectionResult != Result.Success)
        return selectionResult;
      var settings = WoodExportPlugin.CurrentSettings;
      var result = WoodExportEngine.Analyze(doc, objects, settings);
      CommandHelpers.ReportWarnings(result);
      if (!WoodExportEngine.ApplyNumberingAndLabels(doc, result, settings))
        return Result.Failure;
      RhinoApp.WriteLine(
        "WoodExport：已为 {0} 个实体板件生成 {1} 个唯一编号与单线雕刻曲线。",
        result.Parts.Count,
        result.BomRows.Count);
      return Result.Success;
    }
  }

  public sealed class WoodExportBomCommand : Command
  {
    public override string EnglishName => "WXBOM";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      List<RhinoObject> objects;
      var selectionResult = CommandHelpers.GetParts(out objects);
      if (selectionResult != Result.Success)
        return selectionResult;
      string path;
      if (!CommandHelpers.AskBomPath(out path))
        return Result.Cancel;
      var result = WoodExportEngine.Analyze(doc, objects, WoodExportPlugin.CurrentSettings);
      CommandHelpers.ReportWarnings(result);
      if (!WoodExportEngine.WriteBom(result, path))
        return Result.Failure;
      RhinoApp.WriteLine("WoodExport：BOM 已保存：{0}", path);
      return Result.Success;
    }
  }

  public sealed class WoodExportSettingsCommand : Command
  {
    public override string EnglishName => "WXSettings";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = WoodExportPlugin.CurrentSettings;
      var height = settings.LabelHeightMillimeters;
      var inset = settings.LabelInsetMillimeters;
      var spacing = settings.SpacingMillimeters;
      var thicknessTolerance = settings.ThicknessToleranceMillimeters;
      var shapeTolerance = settings.ShapeToleranceMillimeters;
      if (!CommandHelpers.AskNumber("编号刻字高度（mm）", ref height, 0.5)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("编号离板件边缘距离（mm）", ref inset, 0.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("导出排版零件间距（mm）", ref spacing, 0.0)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("同厚度归类公差（mm）", ref thicknessTolerance, 0.001)) return Result.Cancel;
      if (!CommandHelpers.AskNumber("同形零件判定公差（mm）", ref shapeTolerance, 0.001)) return Result.Cancel;
      settings.LabelHeightMillimeters = height;
      settings.LabelInsetMillimeters = inset;
      settings.SpacingMillimeters = spacing;
      settings.ThicknessToleranceMillimeters = thicknessTolerance;
      settings.ShapeToleranceMillimeters = shapeTolerance;
      RhinoApp.WriteLine(string.Format(
        "WoodExport 参数：字高 {0:0.###}mm，边距 {1:0.###}mm，排版间距 {2:0.###}mm，厚度公差 {3:0.###}mm，同形公差 {4:0.###}mm。",
        height, inset, spacing, thicknessTolerance, shapeTolerance));
      return Result.Success;
    }
  }

  public sealed class WoodExportClearLabelsCommand : Command
  {
    public override string EnglishName => "WXClearLabels";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var count = WoodExportEngine.ClearLabels(doc);
      RhinoApp.WriteLine("WoodExport：已删除 {0} 条由插件生成的编号刻字线，原模型未删除。", count);
      return Result.Success;
    }
  }

  public sealed class WoodExportHelpCommand : Command
  {
    public override string EnglishName => "WXHelp";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      RhinoApp.WriteLine("WoodExport 1.0 命令：");
      RhinoApp.WriteLine("  WoodExport - 编号、单线刻字、BOM、按厚度导出 DXF/DWG");
      RhinoApp.WriteLine("  WXNumber - 仅自动编号并生成刻字线");
      RhinoApp.WriteLine("  WXBOM - 仅生成 CSV 数量清单");
      RhinoApp.WriteLine("  WXSettings - 字高、边距、4mm间距和判重公差");
      RhinoApp.WriteLine("  WXClearLabels - 只删除插件生成的刻字线");
      RhinoApp.WriteLine("编号格式：P厚度-序号，例如 P2-001、P2.5-003；同形零件共用编号并合并数量。");
      return Result.Success;
    }
  }

  internal static class CommandHelpers
  {
    public static Result GetParts(out List<RhinoObject> objects)
    {
      objects = new List<RhinoObject>();
      var getter = new GetObject();
      getter.SetCommandPrompt("选择木板及与木板打组的刀线/雕刻曲线（可选择多组）");
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

    public static bool AskFormat(out ExportFormat format)
    {
      format = ExportFormat.Both;
      var getter = new GetOption();
      getter.SetCommandPrompt("选择按厚度导出的文件格式（直接回车默认 DXF+DWG）");
      getter.AcceptNothing(true);
      var dxf = getter.AddOption("DXF");
      var dwg = getter.AddOption("DWG");
      var both = getter.AddOption("DXFAndDWG");
      var bomOnly = getter.AddOption("BOMOnly");
      var getResult = getter.Get();
      if (getResult == GetResult.Cancel)
        return false;
      if (getResult == GetResult.Nothing)
        return true;
      if (getter.OptionIndex() == dxf) format = ExportFormat.Dxf;
      else if (getter.OptionIndex() == dwg) format = ExportFormat.Dwg;
      else if (getter.OptionIndex() == both) format = ExportFormat.Both;
      else if (getter.OptionIndex() == bomOnly) format = ExportFormat.BomOnly;
      else return false;
      return true;
    }

    public static bool AskBomPath(out string path)
    {
      path = null;
      var dialog = new SaveFileDialog
      {
        Title = "选择 WoodExport 输出位置和 BOM 文件名",
        Filter = "CSV 数量清单 (*.csv)|*.csv",
        DefaultExt = "csv",
        FileName = "WoodParts_BOM.csv"
      };
      if (!dialog.ShowSaveDialog())
        return false;
      path = dialog.FileName;
      return !string.IsNullOrWhiteSpace(path);
    }

    public static string ExportBaseName(string bomPath)
    {
      var name = Path.GetFileNameWithoutExtension(bomPath) ?? "WoodParts";
      if (name.EndsWith("_BOM", StringComparison.OrdinalIgnoreCase))
        name = name.Substring(0, name.Length - 4);
      foreach (var invalid in Path.GetInvalidFileNameChars())
        name = name.Replace(invalid, '_');
      return string.IsNullOrWhiteSpace(name) ? "WoodParts" : name;
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

    public static void ReportWarnings(ExportRunResult result)
    {
      foreach (var warning in result.Warnings)
        RhinoApp.WriteLine("WoodExport：" + warning);
      result.Warnings.Clear();
    }

    public static void ReportSummary(ExportRunResult result, ExportFormat format)
    {
      RhinoApp.WriteLine(string.Format(
        "WoodExport：完成 {0} 个板件、{1} 个唯一编号；BOM：{2}",
        result.Parts.Count,
        result.BomRows.Count,
        result.BomFile ?? "未生成"));
      if (format != ExportFormat.BomOnly)
      {
        foreach (var path in result.CadFiles)
          RhinoApp.WriteLine("  CAD：{0}", path);
      }
    }
  }
}
