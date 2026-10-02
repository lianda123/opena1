using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookClearCommand : Command
{
	public override string EnglishName => "EBClear";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		int num;
		int num2;
		using (AutoUpdateController.Suppress())
		{
			num = ExplodeBookEngine.ClearGenerated(doc);
			num2 = LinkedBookManager.UnlinkAll(doc);
		}
		RhinoApp.WriteLine("ExplodeBook：已删除 {0} 个生成对象和 EB_ 说明页，并解除 {1} 个原零件的自动关联；原装配体未删除。", num, num2);
		return Result.Success;
	}
}
