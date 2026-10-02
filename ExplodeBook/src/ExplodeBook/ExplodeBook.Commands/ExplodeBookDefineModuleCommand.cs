using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookDefineModuleCommand : Command
{
	public override string EnglishName => "EBDefineModule";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择属于同一模块的零件组", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		string outputString = "模块01";
		if (RhinoGet.GetString("输入模块名称，例如 柜体、传动组或屋顶", acceptNothing: false, ref outputString) != Result.Success)
		{
			return Result.Cancel;
		}
		outputString = outputString.Trim();
		if (string.IsNullOrWhiteSpace(outputString))
		{
			return Result.Failure;
		}
		int num = LinkedBookManager.SetMetadata(doc, objects, "ExplodeBook.Subassembly", outputString, clearSameKeyOnOtherLinkedSources: false);
		LinkedBookManager.SetMetadata(doc, objects, "ExplodeBook.ModuleOrder", null, clearSameKeyOnOtherLinkedSources: false);
		LinkedBookManager.SetMetadata(doc, objects, "ExplodeBook.ModulePartOrder", null, clearSameKeyOnOtherLinkedSources: false);
		RhinoApp.WriteLine("ExplodeBook：已将 {0} 个对象归入模块“{1}”。", num, outputString);
		if (num <= 0)
		{
			return Result.Failure;
		}
		return Result.Success;
	}
}
