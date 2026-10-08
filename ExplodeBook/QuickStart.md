# 快速操作 · ExplodeBook 2.0.2

1. 关闭所有 Rhino 窗口，完整解压包。Rhino 7 安装 `net48/ExplodeBook.rhp`；Rhino 8 Core 安装 `net8.0/ExplodeBook.rhp`。使用 `PlugInManager` 核对插件版本为 2.0.2；重新启动 Rhino 后再运行命令。
2. 在原模型副本中运行 `EBRestore`，然后 `EBAnalyze` 选择全部原板件。`EBReport` 查看明确的失败原因；`EBFocus` 点选问题原件，只显示该件和关联阻挡件。
3. 修正具体干涉位置后，重新运行 `EBAnalyze`。全部按干涉处理，不能通过增加公差或合并刚性零件掩盖可拆木板的重叠。
4. 模块流程：`EBDefineModule` 分模块 → `EBSetPartOrder` 按点选顺序设模块内部顺序 → `EBSetModuleOrder` 点各模块代表件。首次说明书生成前即可设置。`EBSetBase` 可重新指定唯一基准件。
5. 调整平行视角后 `EBLockView`。运行 `ExplodeBook` 生成原三维板件步骤与模块总装页。切换 Layout 核对，最后 `EBExportPDF`。
6. `EBUpdate` 用于已成功生成的说明书。首次生成失败后继续用 `ExplodeBook`；更新失败将显示真实原因并保留旧说明页。

附件 `002_干涉核对.3dm` 是原模型诊断副本，保留61个实体和全部曲线，增加编号和候选重叠位置标记，没有修改孔槽或实体。它源于缓存网格复核，须在 Rhino 内重新 `EBAnalyze`。标记图层可隐藏。

本环境没有 Rhino 主程序，未完成原生网格生成、Layout 和 PDF 实机验收。所有正式步骤仍须通过直线装配路径检查，不会强行生成“通过”的说明书。
