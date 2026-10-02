using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetSubassemblyCommand : Command
{
	public override string EnglishName => "EBSetSubassembly";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择属于同一子装配的零件组", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		string outputString = "子装配01";
		if (RhinoGet.GetString("输入子装配名称，例如 齿轮箱", acceptNothing: false, ref outputString) != Result.Success)
		{
			return Result.Cancel;
		}
		LinkedBookManager.SetMetadata(doc, objects, "ExplodeBook.Subassembly", outputString.Trim(), clearSameKeyOnOtherLinkedSources: false);
		RhinoApp.WriteLine("ExplodeBook：已将所选零件归入子装配“{0}”。", outputString.Trim());
		return Result.Success;
	}
}
