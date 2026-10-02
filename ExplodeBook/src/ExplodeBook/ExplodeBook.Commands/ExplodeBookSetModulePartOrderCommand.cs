using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetModulePartOrderCommand : Command
{
	public override string EnglishName => "EBSetPartOrder";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("按安装先后点选同一模块内的零件，第一个零件作为模块基准", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		if (AssemblyAnalyzer.SetModulePartOrder(doc, objects, out var moduleName) == 0)
		{
			RhinoApp.WriteLine("ExplodeBook：所选零件必须全部属于同一模块；请先运行 EBDefineModule。");
			return Result.Failure;
		}
		RhinoApp.WriteLine("ExplodeBook：已记录模块“{0}”内部的零件安装顺序。", moduleName);
		return Result.Success;
	}
}
