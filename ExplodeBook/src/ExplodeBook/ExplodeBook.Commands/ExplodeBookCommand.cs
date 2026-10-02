using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookCommand : Command
{
	public override string EnglishName => "ExplodeBook";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
        if (!CommandHelpers.AskPageKind(currentSettings))
		{
			return Result.Cancel;
		}
		List<RhinoObject> objects;
        Result parts = CommandHelpers.GetParts("选择完整装配体：每个可拆板件保持独立实体", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		AssemblyAnalysis analysis;
		GeneratedBook generatedBook = ExplodeBookEngine.Execute(doc, objects, currentSettings, createOverview: true, createPages: true, linkSources: true, out analysis);
		CommandHelpers.ReportWarnings(analysis);
		if (generatedBook.PartCount == 0)
		{
			return Result.Failure;
		}
		RhinoApp.WriteLine($"ExplodeBook：已关联 {generatedBook.PartCount} 个零件、识别 {analysis.Modules.Count} 个模块，生成 {generatedBook.StepCount} 个模块内步骤和 {generatedBook.LayoutNames.Count} 张说明页；后续修改原模型会自动更新。视角：{ViewProjectionBuilder.Describe(currentSettings)}。");
		return Result.Success;
	}
}
