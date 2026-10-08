using System.Collections.Generic;
using System.Linq;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ExplodeBook.Commands;

internal static class CommandHelpers
{
	public static Result GetParts(string prompt, out List<RhinoObject> objects)
	{
		objects = new List<RhinoObject>();
		GetObject getter = new GetObject();
		getter.SetCommandPrompt(prompt);
        bool ordered=prompt.Contains("先后")||prompt.Contains("顺序点选");
        bool singlePart=prompt.Contains("选择一个")||prompt.Contains("单独指定")||prompt.Contains("需要调整");
        getter.GroupSelect = !ordered&&!singlePart;
		getter.OneByOnePostSelect = ordered;
		getter.SubObjectSelect = false;
		getter.GeometryFilter = ObjectType.AnyObject;
        getter.EnablePreSelect(enable: !ordered, ignoreUnacceptablePreselectedObjects: true);
		getter.GetMultiple(1, 0);
		if (getter.CommandResult() != Result.Success)
		{
			return getter.CommandResult();
		}
		objects = (from index in Enumerable.Range(0, getter.ObjectCount)
			select getter.Object(index).Object() into item
			where item != null
			select item).ToList();
		if (objects.Count <= 0)
		{
			return Result.Nothing;
		}
		return Result.Success;
	}

	public static bool AskMode(ExplodeSettings settings)
	{
		GetOption getOption = new GetOption();
		getOption.SetCommandPrompt("选择爆炸方向（直接回车使用当前设置）");
		getOption.AcceptNothing(enable: true);
		int num = getOption.AddOption("Radial");
		int num2 = getOption.AddOption("X");
		int num3 = getOption.AddOption("Y");
		int num4 = getOption.AddOption("Z");
		switch (getOption.Get())
		{
		case GetResult.Cancel:
			return false;
		case GetResult.Nothing:
			return true;
		default:
			if (getOption.OptionIndex() == num)
			{
				settings.Mode = ExplodeMode.Radial;
			}
			else if (getOption.OptionIndex() == num2)
			{
				settings.Mode = ExplodeMode.XAxis;
			}
			else if (getOption.OptionIndex() == num3)
			{
				settings.Mode = ExplodeMode.YAxis;
			}
			else
			{
				if (getOption.OptionIndex() != num4)
				{
					return false;
				}
				settings.Mode = ExplodeMode.ZAxis;
			}
			return true;
		}
	}

	public static bool AskPageKind(ExplodeSettings settings)
	{
		GetOption getOption = new GetOption();
		getOption.SetCommandPrompt("选择说明书尺寸（直接回车使用当前设置）");
		getOption.AcceptNothing(enable: true);
		int num = getOption.AddOption("A4Landscape");
		int num2 = getOption.AddOption("A3Landscape");
		int num3 = getOption.AddOption("A4Portrait");
		int num4 = getOption.AddOption("A3Portrait");
		int num5 = getOption.AddOption("Custom");
		switch (getOption.Get())
		{
		case GetResult.Cancel:
			return false;
		case GetResult.Nothing:
			return true;
		default:
			if (getOption.OptionIndex() == num)
			{
				settings.PageKind = ManualPageKind.A4;
				settings.Landscape = true;
			}
			else if (getOption.OptionIndex() == num2)
			{
				settings.PageKind = ManualPageKind.A3;
				settings.Landscape = true;
			}
			else if (getOption.OptionIndex() == num3)
			{
				settings.PageKind = ManualPageKind.A4;
				settings.Landscape = false;
			}
			else if (getOption.OptionIndex() == num4)
			{
				settings.PageKind = ManualPageKind.A3;
				settings.Landscape = false;
			}
			else
			{
				if (getOption.OptionIndex() != num5)
				{
					return false;
				}
				double value = settings.CustomPageWidthMillimeters;
				double value2 = settings.CustomPageHeightMillimeters;
				if (!AskNumber("自定义页面宽度（mm）", ref value, 50.0) || !AskNumber("自定义页面高度（mm）", ref value2, 50.0))
				{
					return false;
				}
				settings.PageKind = ManualPageKind.Custom;
				settings.CustomPageWidthMillimeters = value;
				settings.CustomPageHeightMillimeters = value2;
			}
			return true;
		}
	}

	public static bool AskNumber(string prompt, ref double value, double minimum)
	{
		GetNumber getNumber = new GetNumber();
		getNumber.SetCommandPrompt(prompt);
		getNumber.SetDefaultNumber(value);
		getNumber.SetLowerLimit(minimum, strictlyGreaterThan: false);
		getNumber.Get();
		if (getNumber.CommandResult() != Result.Success)
		{
			return false;
		}
		value = getNumber.Number();
		return true;
	}

	public static bool AskInteger(string prompt, ref int value, int minimum)
	{
		GetInteger getInteger = new GetInteger();
		getInteger.SetCommandPrompt(prompt);
		getInteger.SetDefaultInteger(value);
		getInteger.SetLowerLimit(minimum, strictlyGreaterThan: false);
		getInteger.Get();
		if (getInteger.CommandResult() != Result.Success)
		{
			return false;
		}
		value = getInteger.Number();
		return true;
	}

	public static bool AskToggle(string prompt, ref bool value)
	{
		GetOption getOption = new GetOption();
		getOption.SetCommandPrompt(prompt + "（当前：" + (value ? "开" : "关") + "）");
		getOption.AcceptNothing(enable: true);
		int num = getOption.AddOption("On");
		int num2 = getOption.AddOption("Off");
		switch (getOption.Get())
		{
		case GetResult.Cancel:
			return false;
		case GetResult.Nothing:
			return true;
		default:
			if (getOption.OptionIndex() == num)
			{
				value = true;
			}
			else if (getOption.OptionIndex() == num2)
			{
				value = false;
			}
			return true;
		}
	}

	public static bool GetTwoPoints(string firstPrompt, string secondPrompt, out Point3d first, out Point3d second)
	{
		first = Point3d.Unset;
		second = Point3d.Unset;
		GetPoint getPoint = new GetPoint();
		getPoint.SetCommandPrompt(firstPrompt);
		getPoint.Get();
		if (getPoint.CommandResult() != Result.Success)
		{
			return false;
		}
		first = getPoint.Point();
		GetPoint getPoint2 = new GetPoint();
		getPoint2.SetCommandPrompt(secondPrompt);
		getPoint2.SetBasePoint(first, showDistanceInStatusBar: true);
		getPoint2.DrawLineFromPoint(first, showDistanceInStatusBar: true);
		getPoint2.Get();
		if (getPoint2.CommandResult() != Result.Success)
		{
			return false;
		}
		second = getPoint2.Point();
		return first.DistanceTo(second) > 2.3283064365386963E-10;
	}

	public static bool GetPoint(string prompt, Point3d basePoint, out Point3d point)
	{
		point = Point3d.Unset;
		GetPoint getPoint = new GetPoint();
		getPoint.SetCommandPrompt(prompt);
		getPoint.SetBasePoint(basePoint, showDistanceInStatusBar: true);
		getPoint.DrawLineFromPoint(basePoint, showDistanceInStatusBar: true);
		getPoint.Get();
		if (getPoint.CommandResult() != Result.Success)
		{
			return false;
		}
		point = getPoint.Point();
		return point.IsValid;
	}

	public static BoundingBox ComponentBounds(IEnumerable<RhinoObject> objects)
	{
		BoundingBox boundingBox = BoundingBox.Unset;
		foreach (RhinoObject item in objects.Where((RhinoObject item) => item != null && item.Geometry != null))
		{
			BoundingBox boundingBox2 = item.Geometry.GetBoundingBox(accurate: true);
			if (boundingBox2.IsValid)
			{
				boundingBox = (boundingBox.IsValid ? BoundingBox.Union(boundingBox, boundingBox2) : boundingBox2);
			}
		}
		return boundingBox;
	}

	public static Result SetRelativePointMetadata(RhinoDoc doc, string selectionPrompt, string pointPrompt, string key, string label)
	{
		List<RhinoObject> objects;
		Result parts = GetParts(selectionPrompt, out objects);
		if (parts != Result.Success)
		{
			return parts;
		}
		List<RhinoObject> list = AssemblyAnalyzer.BuildGroupedComponents(objects).FirstOrDefault();
		if (list == null)
		{
			return Result.Nothing;
		}
		Point3d center = ComponentBounds(list).Center;
		if (!GetPoint(pointPrompt, center, out var point))
		{
			return Result.Cancel;
		}
		LinkedBookManager.SetMetadata(doc, list, key, AssemblyAnalyzer.VectorToString(point - center), clearSameKeyOnOtherLinkedSources: false);
		RhinoApp.WriteLine("ExplodeBook：已记录所选零件的{0}。", label);
		return Result.Success;
	}

    public static void ReportWarnings(AssemblyAnalysis analysis,bool detailed=false)
    {
        RhinoApp.WriteLine(string.Format("装配分析：{0} ms；新建网格 {1}，复用网格 {2}，路径对检查 {3}，复用检查 {4}。", analysis.AnalysisMilliseconds, analysis.MeshBuilds, analysis.MeshHits, analysis.PairTests, analysis.CacheHits));
        foreach (string warning in detailed?analysis.Warnings:analysis.Warnings.Take(8))
        {
            RhinoApp.WriteLine("ExplodeBook：" + warning);
        }
        if(!detailed&&analysis.Warnings.Count>8)RhinoApp.WriteLine("其余 {0} 条提示可运行 EBReport 查看。",analysis.Warnings.Count-8);
        if(analysis.ValidationErrors.Count>0&&!string.IsNullOrEmpty(analysis.ProgressSummary))RhinoApp.WriteLine("停止阶段："+analysis.ProgressSummary);
        // Keep actionable failures last, so accessory warnings cannot bury them.
        foreach (string error in analysis.ValidationErrors) RhinoApp.WriteLine("装配验证未通过：" + error);
	}
}
