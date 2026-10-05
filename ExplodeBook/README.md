# ExplodeBook 2.0.1 · 装配规划与说明书一体化测试版

面向木质拼装产品的 Rhino 插件。把装配路径检查、零件顺序、模块总装和说明书生成统一到一个规划核心；不需要先运行 WoodAssemblyGraph，也不把另一个插件作为依赖装入。

本包含 Windows Rhino 7 的 `net48/ExplodeBook.rhp`、Rhino 8 .NET Core 运行时的 `net8.0/ExplodeBook.rhp`、安装脚本、快速操作说明、测试报告和三个 `.3dm` 示例。已编译和完成独立核心测试，尚未完成真实 Rhino 界面及你的产品模型验收。

## 2.0.1 本次修正

- 删除全局报错时给前40件统一标“待修复”的兜底行为。只标明已定位的问题件；超时、取消、顺序冲突等全局问题在视口左上角和命令记录中说明。
- 区分“网格生成失败”“位置重叠，需核对”“路径受阻”“模块装入受阻”；一个零件出错不再掩盖其他网格转换失败的零件。
- 在局部坐标生成分析网格，Rhino8开启双精度顶点。回到装配坐标后才进行路径检测；原模型不会被平移。接触容差采用文档公差并限制在0.001mm以内，避免把单精度微小误差当成实体穿入；超出容差的真实重叠仍阻止出图。
- 原实体有效但分析网格开口时，开启匹配接缝和封闭对象后处理，并关闭简化平面、减小弦差再重算一次；不会无条件填孔，也不会把未闭合网格直接当成通过。
- 接触包围盒提前排除体积重叠；三角形碰撞前先排除不相交的运动包围盒，减少无效计算。
- 保留一个零件的全部阻挡对象，独显时不再只留下最后一个。附属曲线提示默认最多显示8条，装配失败原因在最后显示；`EBReport` 显示完整记录。
- `EBRestore` 同时恢复对象显示并清除本次诊断标记。

## 核心变化

- 每个可拆实体单独识别；普通外层群组不会把整台模型误合并成一个零件。曲线和文字只在所属板件唯一时附着。块默认视为一个刚性零件。
- 用封闭三角网格和空间树检查连续直线运动，不再根据包围盒距离猜顺序，也不通过离散抽样跳过薄阻挡板。包围盒仅用于提前排除不可能碰撞的组合。
- 从装配完成状态逐件寻找可拆路径，逆序形成安装步骤，再按安装方向复核。候选方向包括世界坐标方向和板面法线；手动方向、手动顺序仍须检查。
- 先检查模块内部，再检查整个模块的装入路径。所有模块先各自预装，再按总装步骤合并；需要交叉穿插安装的结构应作为一个模块分析。
- 缓存按源实体、变换、网格精度和运动参数区分。说明书复用同一份规划结果；重复检查复用碰撞结果，不复用已经改变的几何结果。
- 原三维板件通过共享块定义和累计装配阶段显示，减少每页重复复制实体，保留孔槽、厚度和颜色。默认关闭耗时的隐藏线运算；当前安装件黄色高亮。
- 自动更新在编辑停止后延迟约0.9秒合并处理。受阻、顺序冲突、无效实体、取消、超时或步骤上限不足时停止生成；失败更新保留上一版说明页，禁止直接导出与当前模型不一致的 PDF。
- 只更新本版记录的说明页；不按 `EB_` 前缀删除用户布局。保持文档原有纸张单位，PDF 导出前核对本次说明页完整性。

## 安装和升级

1. 保存当前模型并关闭 Rhino。完整解压 ZIP，不能直接从压缩包中加载。
2. Rhino 7：用 `PlugInManager` 安装 `net48/ExplodeBook.rhp`。Rhino 8 在 .NET 8 或更高的 Core 运行时下安装 `net8.0/ExplodeBook.rhp`。
3. 也可在解压目录运行 Windows PowerShell：`powershell -ExecutionPolicy Bypass -File .\install.ps1 -RhinoVersion Both`。不需要管理员权限。
4. 如 Rhino 8 仍用 .NET Framework 模式，先保持该运行时并使用 `net48` 文件，或运行安装脚本加 `-RhinoVersion 8 -Rhino8Runtime Framework`。Rhino 8 的运行时可通过 `SetDotNetRuntime` 查看、切换并重启。[McNeel 运行时说明](https://developer.rhino3d.com/guides/rhinocommon/moving-to-dotnet-core/)。本包没有修改全局运行时设置。
5. 新版沿用 ExplodeBook 插件 GUID，替换原 ExplodeBook；不需要同时加载不同版本的 ExplodeBook。WoodAssemblyGraph 可以保留，但本流程不调用其命令。
6. 旧版已经自动写入的 `Order/ModuleOrder/ModulePartOrder` 会被视为顺序约束。若希望重新自动推导，请对全部原件运行 `EBAutoOrder`。
7. 旧版未记录说明页所有权。升级前可用旧版 `EBClear` 清理旧结果；升级后遗留的旧版布局需要手动清理，新版不会根据名称猜测并删除它们。建议先在模型副本中升级。

Rhino 的不同版本、运行时、显示模式和模型复杂度仍需实际验证。本次编译 SDK：RhinoCommon 7.38.24338.17001 / 8.35.26251.13001；安装程序适用于 Windows。

## 推荐操作

1. 模型必须处于最终装配位置，每块可拆板件保持独立的封闭 Brep、Extrusion、SubD 或封闭 Mesh。不要把所有可拆板件做成一个块实例。附属曲线/文字可与其唯一板件打组。
2. 需要子装配时用 `EBDefineModule` 命名；仅在父子图层确实表达模块时使用 `EBAutoModules`。没有模块标记时整体作为“主体模块”。
3. 运行 `EBAnalyze`，选择全部原模型，查看检查结果和 `EBReport`。受阻时用 `EBFocus` 点选问题件独显，再用 `EBRestore` 恢复。
4. 分析通过后运行 `ExplodeBook`，选择纸张尺寸并选原模型。生成封面、清单、模块页、逐件步骤、总装步骤和完成页。
5. 修改原件后等待自动更新，或运行 `EBUpdate`。说明页生成失败时旧结果保留，但必须修正并成功更新后再导出。
6. 在 Rhino 中逐页核对箭头、编号、遮挡和实际可操作性，再运行 `EBExportPDF`。

安装箭头与“拆出方向”相反。`EBSetDirection` 和 `EBSetModuleDirection` 的两点指定的是从最终位置往外拆出的方向。错误的人工顺序不能绕过路径检查。

## 常用命令

| 命令 | 用途 |
|---|---|
| `EBAnalyze` / `EBReport` | 检查装配、查看最近的顺序与阻挡报告 |
| `EBFocus` / `EBRestore` | 点选问题原件，独显其阻挡件；恢复本插件临时隐藏的对象 |
| `ExplodeBook` | 检查并生成完整说明书 |
| `EBExplode` / `EBPages` | 只生成总览 / 只生成说明页；同样检查路径 |
| `EBUpdate` / `EBAutoUpdate` | 手动更新 / 切换自动更新 |
| `EBSetBase` | 指定固定基准件 |
| `EBSetOrder` / `EBSetPartOrder` / `EBSetModuleOrder` | 按点选顺序设置全局、模块内、模块间约束 |
| `EBAutoOrder` | 清除所选原件的人工顺序约束 |
| `EBDefineModule` / `EBClearSubassembly` | 定义模块 / 清除模块标记 |
| `EBSetDirection` / `EBSetModuleDirection` | 指定零件 / 模块的直线拆出方向 |
| `EBResetPosition` / `EBResetModuleDirection` | 清除零件位置和方向 / 清除模块方向 |
| `EBDefineRigid` / `EBClearRigid` | 合并永久固定的多实体 / 恢复独立板件 |
| `EBPathSettings` | 网格弦差和分析时间上限 |
| `EBPageSize` / `EBTitle` / `EBSettings` | 页面、标题、步骤上限、PDF 分辨率等 |
| `EBLockView` / `EBReferenceView` | 锁定当前平行视角 / 恢复参考视角 |
| `EBSetArrowPoint` / `EBSetLabelPoint` | 调整箭头和编号标注位置 |
| `EBExportPDF` / `EBClear` / `EBHelp` | 导出本次有效说明书 / 清除本版输出并解除关联 / 帮助 |

默认分析网格弦差0.02mm；时间预算120秒；步骤上限2000；PDF 300dpi。Rhino 原生网格计算期间不能保证即时响应取消；计算结束后会继续检查时间预算。

## 正确性的适用范围

本版验证的是指定网格精度下的刚性直线插入顺序。网格近似不能替代原实体精确布尔证明，也不验证旋转后插入、弯曲变形、弹性卡扣、摩擦、施力、重力稳定、工具空间或手指空间。需要这些动作的结构可能报告受阻，即使人工能够装入；不能把受阻强制解释为通过。

说明页显示起始位置可以是已验证直线路径上的一段，不能据此推断多个移动件可以同时无碰撞运动。紧配合和小于网格弦差的特征须减小容差并在真实模型中复核。实体重叠和无法分析的开口实体不会被自动忽略。

## 编译与测试

Windows 安装 .NET 8 SDK 与 .NET Framework 4.8 Developer Pack，运行：

```powershell
.\build.ps1 -Configuration Release
```

脚本先运行独立核心回归测试，失败则停止编译打包。单独运行核心测试：

```powershell
dotnet run --project tests/PlannerTests/PlannerTests.csproj -c Release
```

输出双版本和单版本 ZIP。当前测试细节及尚待 Rhino 验收的内容见 `Validation.md`。
