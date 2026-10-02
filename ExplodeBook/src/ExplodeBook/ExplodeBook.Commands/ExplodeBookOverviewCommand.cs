using System.Collections.Generic;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookOverviewCommand : Command
{
	public override string EnglishName => "EBExplode";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
		List<RhinoObject> objects;
		Result parts = CommandHelpers.GetParts("选择要制作关联爆炸图的装配体", out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		AssemblyAnalysis analysis;
		GeneratedBook generatedBook = ExplodeBookEngine.Execute(doc, objects, currentSettings, createOverview: true, createPages: false, linkSources: true, out analysis);
		CommandHelpers.ReportWarnings(analysis);
		RhinoApp.WriteLine("ExplodeBook：已生成并关联 {0} 个装配单元的爆炸总览。", generatedBook.PartCount);
		if (generatedBook.PartCount <= 0)
		{
			return Result.Failure;
		}
		return Result.Success;
	}
}
