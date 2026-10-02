using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookUpdateCommand : Command
{
	public override string EnglishName => "EBUpdate";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		AssemblyAnalysis analysis;
		GeneratedBook generatedBook = ExplodeBookEngine.UpdateLinked(doc, out analysis);
		if (generatedBook.PartCount == 0)
		{
			RhinoApp.WriteLine("ExplodeBook：当前文档没有关联装配体，请先运行 ExplodeBook。");
			return Result.Nothing;
		}
		CommandHelpers.ReportWarnings(analysis);
		RhinoApp.WriteLine("ExplodeBook：已手动更新 {0} 个零件、{1} 个步骤和 {2} 张说明页。", generatedBook.PartCount, generatedBook.StepCount, generatedBook.LayoutNames.Count);
		return Result.Success;
	}
}
