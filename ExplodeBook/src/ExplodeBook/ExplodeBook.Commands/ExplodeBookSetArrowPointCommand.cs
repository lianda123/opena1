using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSetArrowPointCommand : Command
{
	public override string EnglishName => "EBSetArrowPoint";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		return CommandHelpers.SetRelativePointMetadata(doc, "选择需要调整安装箭头的零件组", "在原零件上点选箭头安装目标位置", "ExplodeBook.ArrowAnchor", "安装箭头位置");
	}
}
