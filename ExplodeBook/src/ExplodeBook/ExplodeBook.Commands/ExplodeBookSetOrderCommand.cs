using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetOrderCommand : Command
{
	public override string EnglishName => "EBSetOrder";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("按实际安装先后依次点选零件组：第一个应是底座/基准件", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		int num = AssemblyAnalyzer.SetManualOrder(doc, objects);
		AutoUpdateController.Schedule(doc);
		RhinoApp.WriteLine("ExplodeBook：已记录 {0} 个装配单元的手动顺序。", num);
		if (num <= 0)
		{
			return Result.Failure;
		}
		return Result.Success;
	}
}
