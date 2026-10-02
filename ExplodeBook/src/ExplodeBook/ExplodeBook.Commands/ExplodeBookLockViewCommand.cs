using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookLockViewCommand : Command
{
	public override string EnglishName => "EBLockView";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
		if (!ViewProjectionBuilder.CaptureActiveParallelView(doc, currentSettings))
		{
			RhinoApp.WriteLine("ExplodeBook：当前视图无法建立有效的平行相机坐标。");
			return Result.Failure;
		}
		LinkedBookManager.SaveSettings(doc, currentSettings);
		AutoUpdateController.Schedule(doc);
		RhinoApp.WriteLine("ExplodeBook：已把当前视图改为平行投影并锁定为全部说明页视角。{0}", ViewProjectionBuilder.Describe(currentSettings));
		return Result.Success;
	}
}
