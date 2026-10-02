using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetDirectionCommand : Command
{
	public override string EnglishName => "EBSetDirection";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择需要单独指定安装方向的零件组", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		if (!CommandHelpers.GetTwoPoints("指定方向起点", "指定零件拆出方向终点", out var first, out var second))
		{
			return Result.Cancel;
		}
		Vector3d vector = second - first;
		if (!vector.Unitize())
		{
			return Result.Failure;
		}
		LinkedBookManager.SetMetadata(doc, objects, "ExplodeBook.Direction", AssemblyAnalyzer.VectorToString(vector), clearSameKeyOnOtherLinkedSources: false);
		RhinoApp.WriteLine("ExplodeBook：已为所选零件记录独立安装/拆出方向。");
		return Result.Success;
	}
}
