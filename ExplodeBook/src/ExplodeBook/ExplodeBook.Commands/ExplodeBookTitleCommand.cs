using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Input;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookTitleCommand : Command
{
	public override string EnglishName => "EBTitle";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
		string outputString = currentSettings.ProductTitle;
		if (RhinoGet.GetString("输入说明书产品名称", acceptNothing: false, ref outputString) != Result.Success)
		{
			return Result.Cancel;
		}
		currentSettings.ProductTitle = outputString.Trim();
		LinkedBookManager.SaveSettings(doc, currentSettings);
		AutoUpdateController.Schedule(doc);
		RhinoApp.WriteLine("ExplodeBook：说明书标题已更新为“{0}”。", currentSettings.ProductTitle);
		return Result.Success;
	}
}
