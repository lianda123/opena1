using Rhino;
using Rhino.Commands;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookHelpCommand : Command
{
	public override string EnglishName => "EBHelp";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		RhinoApp.WriteLine("ExplodeBook 2.0.1 命令：");
        RhinoApp.WriteLine("  EBAnalyze / EBFocus / EBRestore / EBReport - 分析、独显阻挡、恢复、查看顺序报告");
        RhinoApp.WriteLine("  EBPathSettings / EBDefineRigid / EBClearRigid - 分析精度和刚性零件识别");
        RhinoApp.WriteLine("  EBSetModuleDirection / EBResetModuleDirection - 指定或恢复模块装入方向");
		RhinoApp.WriteLine("  ExplodeBook - 关联原模型并生成爆炸图、通过路径检查后生成原三维板件说明书");
		RhinoApp.WriteLine("  EBUpdate / EBAutoUpdate - 手动更新 / 开关自动更新");
		RhinoApp.WriteLine("  EBSetOrder / EBAutoOrder / EBSetBase - 装配顺序与基准件");
		RhinoApp.WriteLine("  EBSetDirection / EBSetPosition / EBResetPosition - 单件安装方向和爆炸位置");
		RhinoApp.WriteLine("  EBSetArrowPoint / EBSetLabelPoint - 调整箭头和编号位置");
		RhinoApp.WriteLine("  EBDefineModule / EBSetModuleOrder / EBSetPartOrder - 模块与两级装配顺序");
		RhinoApp.WriteLine("  EBAutoModules - 按父子图层自动识别模块");
		RhinoApp.WriteLine("  EBSetSubassembly / EBClearSubassembly - 旧版子装配命令（兼容保留）");
		RhinoApp.WriteLine("  EBLockView / EBReferenceView - 锁定当前平行视角 / 恢复参考视角");
		RhinoApp.WriteLine("  EBPageSize / EBTitle / EBSettings - 页面尺寸、标题与输出设置");
		RhinoApp.WriteLine("  EBExportPDF - 验证当前模型后导出本次说明页为300dpi PDF");
		RhinoApp.WriteLine("  EBClear - 清除生成结果并解除关联，不删除原模型");
		return Result.Success;
	}
}
