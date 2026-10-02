using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookPageSizeCommand : Command
{
	public override string EnglishName => "EBPageSize";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
		if (!CommandHelpers.AskPageKind(currentSettings))
		{
			return Result.Cancel;
		}
		LinkedBookManager.SaveSettings(doc, currentSettings);
		AutoUpdateController.Schedule(doc);
		RhinoApp.WriteLine("ExplodeBook：说明书尺寸已设为 {0:0.###} × {1:0.###} mm。", currentSettings.PageWidthMillimeters, currentSettings.PageHeightMillimeters);
		return Result.Success;
	}
}
