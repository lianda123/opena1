using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetModuleOrderCommand : Command
{
	public override string EnglishName => "EBSetModuleOrder";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("按模块总装先后点选各模块中的任一零件，第一个模块作为总装基准", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		List<string> orderedModules;
		int num = AssemblyAnalyzer.SetModuleOrder(doc, objects, out orderedModules);
		RhinoApp.WriteLine("ExplodeBook：已记录 {0} 个模块的总装顺序：{1}。", orderedModules.Count, string.Join(" → ", orderedModules));
		if (num <= 0)
		{
			return Result.Failure;
		}
		return Result.Success;
	}
}
