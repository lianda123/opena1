using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace ExplodeBook.Commands;

public sealed class AnalyzeAssemblyCommand:Command
{
    public override string EnglishName=>"EBAnalyze";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)
    {
        PathDiagnostics.Restore(doc);var result=CommandHelpers.GetParts("选择原装配模型，分析路径、模块和拼装顺序",out var selection);if(result!=Result.Success)return result;
        var analysis=AssemblyAnalyzer.Analyze(doc,selection,ExplodeBookPlugin.CurrentSettings);PathDiagnostics.Draw(doc,analysis);CommandHelpers.ReportWarnings(analysis);
        if(analysis.PlanVerified)RhinoApp.WriteLine("通过网格直线路径检查：{0} 个零件，{1} 个模块。可运行 ExplodeBook 生成说明书。",analysis.Parts.Count,analysis.Modules.Count);
        return analysis.PlanVerified?Result.Success:Result.Failure;
    }
}
public sealed class FocusAssemblyCommand:Command
{
    public override string EnglishName=>"EBFocus";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)
    {
        var get=new GetObject();get.SetCommandPrompt("点选需要检查的原零件，独显该件和阻挡件");get.GroupSelect=false;get.SubObjectSelect=false;get.Get();
        if(get.CommandResult()!=Result.Success)return get.CommandResult();return PathDiagnostics.Focus(doc,get.Object(0).ObjectId)?Result.Success:Result.Nothing;
    }
}
public sealed class RestoreAssemblyCommand:Command
{public override string EnglishName=>"EBRestore";protected override Result RunCommand(RhinoDoc doc,RunMode mode){PathDiagnostics.Restore(doc);PathDiagnostics.Clear(doc);return Result.Success;}}
public sealed class AssemblyPathSettingsCommand:Command
{
    public override string EnglishName=>"EBPathSettings";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)
    {
        var settings=ExplodeBookPlugin.CurrentSettings;double tolerance=settings.MeshToleranceMillimeters;int timeout=settings.AnalysisTimeoutSeconds;
        if(!CommandHelpers.AskNumber("分析网格弦差（mm，默认0.02；更小更精细）",ref tolerance,.001)||!CommandHelpers.AskInteger("分析时间上限（秒）",ref timeout,5))return Result.Cancel;
        if(tolerance>1){RhinoApp.WriteLine("容差不得大于1mm。");return Result.Failure;}
        settings.MeshToleranceMillimeters=tolerance;settings.AnalysisTimeoutSeconds=timeout;LinkedBookManager.SaveSettings(doc,settings);return Result.Success;
    }
}
public sealed class DefineRigidPartCommand:Command
{
    public override string EnglishName=>"EBDefineRigid";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)
    {
        var result=CommandHelpers.GetParts("选择永远固定在一起的刚性零件；可拆板件应分开",out var objects);if(result!=Result.Success)return result;
        string rigidKey=Guid.NewGuid().ToString("N");
        using(AutoUpdateController.Suppress())foreach(var o in objects){var a=o.Attributes.Duplicate();a.SetUserString("EB2.RigidPart",rigidKey);doc.Objects.ModifyAttributes(o.Id,a,true);}
        AutoUpdateController.Schedule(doc);
        return Result.Success;
    }
}
public sealed class ClearRigidPartCommand:Command
{
    public override string EnglishName=>"EBClearRigid";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)
    {
        var result=CommandHelpers.GetParts("选择恢复为独立板件的对象",out var objects);if(result!=Result.Success)return result;
        using(AutoUpdateController.Suppress())foreach(var o in objects){var a=o.Attributes.Duplicate();a.DeleteUserString("EB2.RigidPart");doc.Objects.ModifyAttributes(o.Id,a,true);}
        AutoUpdateController.Schedule(doc);return Result.Success;
    }
}
public sealed class AssemblyReportCommand:Command
{
    public override string EnglishName=>"EBReport";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)
    {
        var analysis=IntegratedAssemblyPlanner.Session(doc).Last;if(analysis==null){RhinoApp.WriteLine("先运行 EBAnalyze 或 ExplodeBook。");return Result.Nothing;}
        CommandHelpers.ReportWarnings(analysis,true);
        foreach(var issue in analysis.PartIssues)RhinoApp.WriteLine("检查零件："+issue.Key.PartNumber+"；"+issue.Value+"；原对象 ID："+string.Join("、",issue.Key.Objects.Where(PartResolver.Physical).Select(o=>o.Id)));
        foreach(var m in analysis.Modules){RhinoApp.WriteLine("模块 {0}：{1}",m.Number,m.Name);foreach(var p in m.Parts)RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,"  {0:00} {1} {2}；拆出方向 ({3:F3}, {4:F3}, {5:F3})",p.ModulePartOrder,p.PartNumber,p.Name,p.AutoDirection.X,p.AutoDirection.Y,p.AutoDirection.Z));}
        foreach(var blocked in analysis.Blockers)RhinoApp.WriteLine("受阻："+blocked.Key.PartNumber+"；阻挡件："+string.Join("、",blocked.Value.Select(p=>p.PartNumber)));
        RhinoApp.WriteLine("报告为最近一次分析；修改模型后重新运行 EBAnalyze。");return Result.Success;
    }
}
public sealed class SetModuleDirectionCommand:Command
{
    public override string EnglishName=>"EBSetModuleDirection";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)
    {
        var analysis=IntegratedAssemblyPlanner.Session(doc).Last;
        if(analysis==null){RhinoApp.WriteLine("先运行 EBAnalyze，再指定模块方向。");return Result.Nothing;}
        var get=new GetObject();get.GroupSelect=false;get.SetCommandPrompt("点选模块中的任一原零件");get.Get();if(get.CommandResult()!=Result.Success)return get.CommandResult();
        var part=analysis.Parts.FirstOrDefault(p=>p.Objects.Any(o=>o.Id==get.Object(0).ObjectId));if(part==null)return Result.Nothing;
        if(!CommandHelpers.GetTwoPoints("指定模块拆出方向起点","指定模块拆出方向终点（安装箭头为反向）",out var a,out var b))return Result.Cancel;
        string name=string.IsNullOrWhiteSpace(part.Subassembly)?"主体模块":part.Subassembly;
        string value=AssemblyAnalyzer.VectorToString(b-a);
        using(AutoUpdateController.Suppress())foreach(var source in analysis.Parts.Where(p=>(string.IsNullOrWhiteSpace(p.Subassembly)?"主体模块":p.Subassembly)==name).SelectMany(p=>p.Objects))
        {var attributes=source.Attributes.Duplicate();attributes.SetUserString("EB2.ModuleDirection",value);doc.Objects.ModifyAttributes(source.Id,attributes,true);}
        AutoUpdateController.Schedule(doc);return Result.Success;
    }
}
public sealed class ResetModuleDirectionCommand:Command
{
    public override string EnglishName=>"EBResetModuleDirection";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)
    {
        var result=CommandHelpers.GetParts("选择模块的全部原零件，恢复自动模块方向",out var objects);if(result!=Result.Success)return result;
        using(AutoUpdateController.Suppress())foreach(var source in objects){var attributes=source.Attributes.Duplicate();attributes.DeleteUserString("EB2.ModuleDirection");doc.Objects.ModifyAttributes(source.Id,attributes,true);}
        AutoUpdateController.Schedule(doc);return Result.Success;
    }
}
