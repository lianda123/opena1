using System.Collections.Generic;
using System.Linq;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetBaseCommand : Command
{
	public override string EnglishName => "EBSetBase";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择一个底座/基准零件组", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		List<RhinoObject> list = AssemblyAnalyzer.BuildGroupedComponents(objects).FirstOrDefault();
		if (list == null)
		{
			return Result.Nothing;
		}
		int num = LinkedBookManager.SetMetadata(doc, list, "ExplodeBook.ForcedBase", "1", clearSameKeyOnOtherLinkedSources: true);
		RhinoApp.WriteLine("ExplodeBook：已设置手动基准件，并清除其他基准标记。");
		if (num <= 0)
		{
			return Result.Failure;
		}
		return Result.Success;
	}
}
