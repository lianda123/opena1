using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookResetPositionCommand : Command
{
	public override string EnglishName => "EBResetPosition";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择要恢复自动爆炸位置的零件组", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		LinkedBookManager.SetMetadata(doc, objects, "ExplodeBook.ExactOffset", null, clearSameKeyOnOtherLinkedSources: false);
		LinkedBookManager.SetMetadata(doc, objects, "ExplodeBook.Direction", null, clearSameKeyOnOtherLinkedSources: false);
		RhinoApp.WriteLine("ExplodeBook：所选零件已恢复自动爆炸方向和距离。");
		return Result.Success;
	}
}
