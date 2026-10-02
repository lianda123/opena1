using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookClearSubassemblyCommand : Command
{
	public override string EnglishName => "EBClearSubassembly";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择要移出子装配的零件组", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		LinkedBookManager.SetMetadata(doc, objects, "ExplodeBook.Subassembly", null, clearSameKeyOnOtherLinkedSources: false);
		RhinoApp.WriteLine("ExplodeBook：已清除所选零件的子装配标记。");
		return Result.Success;
	}
}
