using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetLabelPointCommand : Command
{
	public override string EnglishName => "EBSetLabelPoint";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		return CommandHelpers.SetRelativePointMetadata(doc, "选择需要调整编号圆标的零件组", "在原零件附近点选编号圆标的相对位置", "ExplodeBook.LabelOffset", "编号圆标位置");
	}
}
