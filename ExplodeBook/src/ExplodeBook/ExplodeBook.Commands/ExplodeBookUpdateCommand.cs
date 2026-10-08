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
            if (analysis.ValidationErrors.Count > 0 || analysis.Parts.Count > 0)
            {
                CommandHelpers.ReportWarnings(analysis);
                RhinoApp.WriteLine("ExplodeBook：更新未完成。已保留上一版说明页；运行 EBReport 查看原因，EBFocus 独显问题件。");
                return Result.Failure;
            }
            RhinoApp.WriteLine("ExplodeBook：还没有成功生成过关联说明书。请运行 ExplodeBook；EBUpdate 只更新已有说明书。");
            return Result.Nothing;
		}
		CommandHelpers.ReportWarnings(analysis);
		RhinoApp.WriteLine("ExplodeBook：已手动更新 {0} 个零件、{1} 个步骤和 {2} 张说明页。", generatedBook.PartCount, generatedBook.StepCount, generatedBook.LayoutNames.Count);
		return Result.Success;
	}
}
