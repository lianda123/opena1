using System.Collections.Generic;
using System.Linq;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetPositionCommand : Command
{
	public override string EnglishName => "EBSetPosition";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择一个需要调整爆炸位置的零件组", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		List<RhinoObject> list = AssemblyAnalyzer.BuildGroupedComponents(objects).FirstOrDefault();
		if (list == null)
		{
			return Result.Nothing;
		}
		Point3d center = CommandHelpers.ComponentBounds(list).Center;
		if (!CommandHelpers.GetPoint("点选该零件希望到达的爆炸中心位置", center, out var point))
		{
			return Result.Cancel;
		}
		Vector3d vector = point - center;
		if (vector.IsTiny())
		{
			return Result.Failure;
		}
		LinkedBookManager.SetMetadata(doc, list, "ExplodeBook.ExactOffset", AssemblyAnalyzer.VectorToString(vector), clearSameKeyOnOtherLinkedSources: false);
		RhinoApp.WriteLine("ExplodeBook：已记录所选零件的鼠标指定爆炸位置。");
		return Result.Success;
	}
}
