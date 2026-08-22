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
      var option = new GetOption();
      option.SetCommandPrompt("选择激光排版边界框");
      option.AcceptNothing(true);
      option.SetDefaultString("A3");
      var a3 = option.AddOption("A3");
      var a4 = option.AddOption("A4");
      var getResult = option.Get();
      if (getResult == GetResult.Cancel)
        return Result.Cancel;

      var sheet = SheetKind.A3;
      if (getResult == GetResult.Option && option.OptionIndex() == a4)
        sheet = SheetKind.A4;
      else if (getResult == GetResult.Option && option.OptionIndex() != a3)
        return Result.Cancel;

      return RunLayout(doc, sheet);
    }

    internal static Result RunLayout(RhinoDoc doc, SheetKind sheet)
    {
      var getter = new GetObject();
      getter.SetCommandPrompt("选择木板及与木板打组的刀线/雕刻曲线（可选择多组）");
      getter.GroupSelect = true;
      getter.SubObjectSelect = false;
      getter.GeometryFilter = Rhino.DocObjects.ObjectType.AnyObject;
      getter.GetMultiple(1, 0);
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      var objects = Enumerable.Range(0, getter.ObjectCount)
        .Select(index => getter.Object(index).Object())
        .Where(item => item != null)
        .ToList();
      var settings = new LayoutSettings
      {
        Sheet = sheet,
        SpacingMillimeters = 4.0,
        ThicknessToleranceMillimeters = 0.15,
        Landscape = true
      };
      return LayoutEngine.Execute(doc, objects, settings) ? Result.Success : Result.Failure;
    }
  }

  public sealed class WoodSheetLayoutA3Command : Command
  {
    public override string EnglishName => "WSLayFlatA3";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return WoodSheetLayoutCommand.RunLayout(doc, SheetKind.A3);
    }
  }

  public sealed class WoodSheetLayoutA4Command : Command
  {
    public override string EnglishName => "WSLayFlatA4";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      return WoodSheetLayoutCommand.RunLayout(doc, SheetKind.A4);
    }
  }
}
