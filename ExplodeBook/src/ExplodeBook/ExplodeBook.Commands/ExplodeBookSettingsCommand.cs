using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookSettingsCommand : Command
{
	public override string EnglishName => "EBSettings";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		ExplodeSettings currentSettings = ExplodeBookPlugin.CurrentSettings;
		double value = currentSettings.ExplodeDistanceMillimeters;
		double value2 = currentSettings.ArrowHeadMillimeters;
		double value3 = currentSettings.PageGapMillimeters;
		int value4 = currentSettings.MaximumStepPages;
		int value5 = currentSettings.PdfDpi;
		if (!CommandHelpers.AskNumber("基础爆炸距离（mm）", ref value, 1.0))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskNumber("箭头头部尺寸（mm）", ref value2, 0.5))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskNumber("模型空间说明页间距（mm）", ref value3, 0.0))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskInteger("最多生成多少张装配步骤页", ref value4, 1))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskInteger("PDF输出分辨率（dpi）", ref value5, 72))
		{
			return Result.Cancel;
		}
		currentSettings.ExplodeDistanceMillimeters = value;
		currentSettings.ArrowHeadMillimeters = value2;
		currentSettings.PageGapMillimeters = value3;
		currentSettings.MaximumStepPages = value4;
		currentSettings.PdfDpi = value5;
		bool value6 = currentSettings.AutoUpdate;
		bool value7 = currentSettings.CreateVectorLinework;
		bool value8 = currentSettings.IncludeCover;
		bool value9 = currentSettings.IncludePartsList;
		bool value10 = currentSettings.IncludeCompletionPage;
		bool value11 = currentSettings.IncludeDetailInset;
		bool value12 = currentSettings.IncludeModuleOverview;
		bool value13 = currentSettings.IncludeModuleAssemblyPages;
		bool value14 = currentSettings.IncludeModulePartPages;
		if (!CommandHelpers.AskToggle("原模型改变后自动更新", ref value6))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskToggle("封面与总览使用隐藏线矢量线稿（装配步骤始终显示三维板件）", ref value7))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskToggle("生成封面", ref value8))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskToggle("生成零件清单", ref value9))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskToggle("生成装配完成页", ref value10))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskToggle("生成当前零件局部放大图", ref value11))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskToggle("生成模块总览", ref value12))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskToggle("生成模块之间的总装步骤", ref value13))
		{
			return Result.Cancel;
		}
		if (!CommandHelpers.AskToggle("生成每个模块内部零件步骤", ref value14))
		{
			return Result.Cancel;
		}
		currentSettings.AutoUpdate = value6;
		currentSettings.CreateVectorLinework = value7;
		currentSettings.IncludeCover = value8;
		currentSettings.IncludePartsList = value9;
		currentSettings.IncludeCompletionPage = value10;
		currentSettings.IncludeDetailInset = value11;
		currentSettings.IncludeModuleOverview = value12;
		currentSettings.IncludeModuleAssemblyPages = value13;
		currentSettings.IncludeModulePartPages = value14;
		LinkedBookManager.SaveSettings(doc, currentSettings);
		AutoUpdateController.Schedule(doc);
		RhinoApp.WriteLine(string.Format("ExplodeBook：参数已保存；爆炸距离 {0:0.###}mm，页面 {1:0.###}×{2:0.###}mm，自动更新 {3}。", value, currentSettings.PageWidthMillimeters, currentSettings.PageHeightMillimeters, currentSettings.AutoUpdate ? "开启" : "关闭"));
		return Result.Success;
	}
}
