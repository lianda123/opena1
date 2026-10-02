using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookAutoModulesCommand : Command
{
	public override string EnglishName => "EBAutoModules";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择装配体；插件会优先按父子图层自动识别模块", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		int moduleCount;
		int num = AssemblyAnalyzer.AutoAssignModules(doc, objects, out moduleCount);
		RhinoApp.WriteLine("ExplodeBook：已处理 {0} 个对象，识别 {1} 个模块。", num, moduleCount);
		if (num <= 0)
		{
			return Result.Failure;
		}
		return Result.Success;
	}
}
