using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookAutoOrderCommand : Command
{
	public override string EnglishName => "EBAutoOrder";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择要恢复自动排序的装配体", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		int num = AssemblyAnalyzer.ClearManualOrder(doc, objects);
		AutoUpdateController.Schedule(doc);
		RhinoApp.WriteLine("ExplodeBook：已清除 {0} 个对象的手动顺序。", num);
		return Result.Success;
	}
}
