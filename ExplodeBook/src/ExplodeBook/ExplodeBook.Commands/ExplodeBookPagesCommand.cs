using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookPagesCommand : Command
{
	public override string EnglishName => "EBPages";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
		if (!CommandHelpers.AskMode(currentSettings) || !CommandHelpers.AskPageKind(currentSettings))
		{
			return Result.Cancel;
		}
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择要生成关联说明书页面的装配体", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		AssemblyAnalysis analysis;
		GeneratedBook generatedBook = ExplodeBookEngine.Execute(doc, objects, currentSettings, createOverview: false, createPages: true, linkSources: true, out analysis);
		CommandHelpers.ReportWarnings(analysis);
		RhinoApp.WriteLine("ExplodeBook：已创建并关联 {0} 张 Rhino Layout 说明书页面。", generatedBook.LayoutNames.Count);
		if (generatedBook.LayoutNames.Count <= 0)
		{
			return Result.Failure;
		}
		return Result.Success;
	}
}
