using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookReferenceViewCommand : Command
{
	public override string EnglishName => "EBReferenceView";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
		ViewProjectionBuilder.UseReferenceView(currentSettings);
		LinkedBookManager.SaveSettings(doc, currentSettings);
		AutoUpdateController.Schedule(doc);
		RhinoApp.WriteLine("ExplodeBook：已恢复为参考说明书的固定平行视角。");
		return Result.Success;
	}
}
