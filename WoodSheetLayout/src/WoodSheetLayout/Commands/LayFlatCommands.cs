using System.Linq;
using WoodSheetLayout.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace WoodSheetLayout.Commands
{
  public sealed class WoodSheetLayoutCommand : Command
  {
    public override string EnglishName => "WoodSheetLayout";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = new LayoutSettings
      {
        PartMode = LayoutPartMode.PlanarOnly,
        Packing = PackingMode.Fast,
        EnableHoleNesting = false
      };
      var configurationResult = Configure(settings, false);
      if (configurationResult != Result.Success)
        return configurationResult;
      return RunLayout(doc, settings);
    }

    internal static Result Configure(LayoutSettings settings, bool includeNeutralFactor)
    {
      var sheetWidth = new OptionDouble(settings.CustomWidthMillimeters);
      var sheetHeight = new OptionDouble(settings.CustomHeightMillimeters);
      var landscape = new OptionToggle(
        settings.Landscape,
        L("Portrait", "纵向"),
        L("Landscape", "横向"));
      var grainLock = new OptionToggle(
        settings.GrainDirectionLocked,
        L("No", "否"),
        L("Yes", "是"));
      var partGap = new OptionDouble(settings.PartGapMillimeters);
      var frameMargin = new OptionDouble(settings.FrameMarginMillimeters);
      var neutralFactor = new OptionDouble(settings.NeutralFactor);

      var getter = new GetOption();
      getter.SetCommandPrompt(includeNeutralFactor
        ? "设置折弯件铺平参数，回车开始选择折弯件"
        : "设置木板铺平排版参数，回车开始选择零件");
      getter.AcceptNothing(true);
      var sheetOption = getter.AddOptionList(
        L("Sheet", "边界框"),
        new[] { L("A3", "A3"), L("A4", "A4"), L("Custom", "自定义") },
        (int)settings.Sheet);
      var orientationOption = getter.AddOptionToggle(L("Orientation", "方向"), ref landscape);
      var grainOption = getter.AddOptionToggle(L("GrainLock", "木纹锁定"), ref grainLock);
      getter.AddOptionDouble(L("CustomWidth", "自定义宽度"), ref sheetWidth);
      getter.AddOptionDouble(L("CustomHeight", "自定义高度"), ref sheetHeight);
      getter.AddOptionDouble(L("PartGap", "零件间距"), ref partGap);
      getter.AddOptionDouble(L("FrameMargin", "边框出血"), ref frameMargin);
      if (includeNeutralFactor)
        getter.AddOptionDouble(L("NeutralFactor", "中性层系数"), ref neutralFactor);

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
      settings.Packing = PackingMode.Fast;
      settings.EnableHoleNesting = false;
      settings.PartGapMillimeters = partGap.CurrentValue;
      settings.FrameMarginMillimeters = frameMargin.CurrentValue;
      settings.NeutralFactor = neutralFactor.CurrentValue;

      if (settings.CustomWidthMillimeters <= 8.0 || settings.CustomHeightMillimeters <= 8.0)
      {
        RhinoApp.WriteLine("WoodSheetLayout：自定义边界框的宽度和高度必须大于8mm。");
        return Result.Failure;
      }
      if (settings.PartGapMillimeters < 0.0 || settings.FrameMarginMillimeters < 0.0)
      {
        RhinoApp.WriteLine("WoodSheetLayout：零件间距和边框出血不能为负数。");
        return Result.Failure;
      }
      if (includeNeutralFactor && (settings.NeutralFactor < 0.0 || settings.NeutralFactor > 1.0))
      {
        RhinoApp.WriteLine("WoodSheetLayout：中性层系数必须在0到1之间，默认0.5代表木板厚度中间层。");
        return Result.Failure;
      }
      return Result.Success;
    }

    internal static Result RunLayout(RhinoDoc doc, LayoutSettings settings)
    {
      var getter = new GetObject();
      getter.SetCommandPrompt(settings.PartMode == LayoutPartMode.BentOnly
        ? "选择折弯木板及其同组刀线、雕刻线或文字"
        : "选择普通木板及其同组刀线、雕刻线或文字（每块木板使用独立组）");
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
        PartMode = LayoutPartMode.PlanarOnly,
        Packing = PackingMode.Fast,
        Sheet = sheet,
        PartGapMillimeters = 4.0,
        FrameMarginMillimeters = 4.0,
        ThicknessToleranceMillimeters = 0.15,
        Landscape = true,
        GrainDirectionLocked = false,
        EnableHoleNesting = false,
        NeutralFactor = 0.5
      };
    }

    private static LocalizeStringPair L(string english, string chinese)
    {
      return new LocalizeStringPair(english, chinese);
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

  public sealed class WoodSheetLayoutBendCommand : Command
  {
    public override string EnglishName => "WSLayFlatBend";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var settings = new LayoutSettings
      {
        PartMode = LayoutPartMode.BentOnly,
        Packing = PackingMode.Fast,
        EnableHoleNesting = false,
        NeutralFactor = 0.5
      };
      var configurationResult = WoodSheetLayoutCommand.Configure(settings, true);
      if (configurationResult != Result.Success)
        return configurationResult;
      return WoodSheetLayoutCommand.RunLayout(doc, settings);
    }
  }
}
