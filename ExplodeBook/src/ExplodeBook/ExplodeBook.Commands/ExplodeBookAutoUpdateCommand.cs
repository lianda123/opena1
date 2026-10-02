using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookAutoUpdateCommand : Command
{
	public override string EnglishName => "EBAutoUpdate";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
		bool value = currentSettings.AutoUpdate;
		if (!CommandHelpers.AskToggle("原模型改变后自动更新", ref value))
		{
			return Result.Cancel;
		}
		currentSettings.AutoUpdate = value;
		LinkedBookManager.SaveSettings(doc, currentSettings);
		RhinoApp.WriteLine("ExplodeBook：自动关联更新已{0}。", currentSettings.AutoUpdate ? "开启" : "关闭");
		return Result.Success;
	}
}
