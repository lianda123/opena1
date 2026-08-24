using System.Linq;
using WoodSheetLayout.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace WoodSheetLayout.Commands
{
  public sealed class WoodSheetLayoutCommand : Command
  {
    public override string EnglishName => "WoodSheetLayout";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = new LayoutSettings();
      var configurationResult = Configure(settings);
      if (configurationResult != Result.Success)
        return configurationResult;
      return RunLayout(doc, settings);
    }

    private static Result Configure(LayoutSettings settings)
    {
      var sheetWidth = new OptionDouble(settings.CustomWidthMillimeters);
      var sheetHeight = new OptionDouble(settings.CustomHeightMillimeters);
      var landscape = new OptionToggle(settings.Landscape, "Portrait", "Landscape");
      var grainLock = new OptionToggle(settings.GrainDirectionLocked, "No", "Yes");
      var partGap = new OptionDouble(settings.PartGapMillimeters);
      var frameMargin = new OptionDouble(settings.FrameMarginMillimeters);
      var neutralFactor = new OptionDouble(settings.NeutralFactor);

      var getter = new GetOption();
      getter.SetCommandPrompt("设置板框与真实轮廓排版参数，回车开始选择零件");
      getter.AcceptNothing(true);
      var sheetOption = getter.AddOptionList("Sheet", new[] { "A3", "A4", "Custom" }, 0);
      var orientationOption = getter.AddOptionToggle("Orientation", ref landscape);
      var grainOption = getter.AddOptionToggle("GrainLock", ref grainLock);
      getter.AddOptionDouble("CustomWidth", ref sheetWidth);
      getter.AddOptionDouble("CustomHeight", ref sheetHeight);
      getter.AddOptionDouble("PartGap", ref partGap);
      getter.AddOptionDouble("FrameMargin", ref frameMargin);
      getter.AddOptionDouble("NeutralFactor", ref neutralFactor);

      while (true)
      {
        var result = getter.Get();
        if (result == GetResult.Cancel)
          return Result.Cancel;
        if (result == GetResult.Nothing)
          break;
        if (result != GetResult.Option)
          continue;

        if (getter.OptionIndex() == sheetOption)
          settings.Sheet = (SheetKind)getter.Option().CurrentListOptionIndex;
        else if (getter.OptionIndex() != orientationOption && getter.OptionIndex() != grainOption)
          continue;
      }

      settings.CustomWidthMillimeters = sheetWidth.CurrentValue;
      settings.CustomHeightMillimeters = sheetHeight.CurrentValue;
      settings.Landscape = landscape.CurrentValue;
      settings.GrainDirectionLocked = grainLock.CurrentValue;
      settings.PartGapMillimeters = partGap.CurrentValue;
      settings.FrameMarginMillimeters = frameMargin.CurrentValue;
      settings.NeutralFactor = neutralFactor.CurrentValue;

      if (settings.CustomWidthMillimeters <= 8.0 || settings.CustomHeightMillimeters <= 8.0)
      {
        RhinoApp.WriteLine("WoodSheetLayout：Custom 长宽必须大于8mm。");
        return Result.Failure;
      }
      if (settings.PartGapMillimeters < 0.0 || settings.FrameMarginMillimeters < 0.0)
      {
        RhinoApp.WriteLine("WoodSheetLayout：PartGap 和 FrameMargin 不能为负数。");
        return Result.Failure;
      }
      if (settings.NeutralFactor < 0.0 || settings.NeutralFactor > 1.0)
      {
        RhinoApp.WriteLine("WoodSheetLayout：NeutralFactor 必须在0到1之间，默认0.5代表木板厚度中间层。");
        return Result.Failure;
      }
      return Result.Success;
    }

    internal static Result RunLayout(RhinoDoc doc, LayoutSettings settings)
    {
      var getter = new GetObject();
      getter.SetCommandPrompt("选择木板及与木板打组的刀线/雕刻曲线/文字（可选择多组）");
      getter.GroupSelect = true;
      getter.SubObjectSelect = false;
      getter.GeometryFilter = Rhino.DocObjects.ObjectType.AnyObject;
      getter.EnablePreSelect(true, true);
      getter.GetMultiple(1, 0);
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      var objects = Enumerable.Range(0, getter.ObjectCount)
        .Select(index => getter.Object(index).Object())
        .Where(item => item != null)
        .ToList();
      return LayoutEngine.Execute(doc, objects, settings) ? Result.Success : Result.Failure;
    }

    internal static LayoutSettings FixedSettings(SheetKind sheet)
    {
      return new LayoutSettings
      {
        Sheet = sheet,
        PartGapMillimeters = 4.0,
        FrameMarginMillimeters = 4.0,
        ThicknessToleranceMillimeters = 0.15,
        Landscape = true,
        GrainDirectionLocked = false,
        NeutralFactor = 0.5
      };
    }
  }

  public sealed class WoodSheetLayoutA3Command : Command
  {
    public override string EnglishName => "WSLayFlatA3";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return WoodSheetLayoutCommand.RunLayout(doc, WoodSheetLayoutCommand.FixedSettings(SheetKind.A3));
    }
  }

  public sealed class WoodSheetLayoutA4Command : Command
  {
    public override string EnglishName => "WSLayFlatA4";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return WoodSheetLayoutCommand.RunLayout(doc, WoodSheetLayoutCommand.FixedSettings(SheetKind.A4));
    }
  }
}
