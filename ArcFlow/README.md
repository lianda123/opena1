# ArcFlow 1.2.1（Rhino 7 / Rhino 8）

ArcFlow 用独立的真实圆弧段构建复杂曲线与参数化螺旋。相邻段保持 G1 切线连续，不把结果转换为 NURBS 样条曲线。

## 圆弧接点与控制点

对相邻圆弧 `A`、`B`，ArcFlow 1.2.1 会验证三件事：

1. `A` 的终点与 `B` 的起点重合；
2. `A` 靠近终点的最后一个 NURBS 控制点、公共接点、`B` 靠近起点的第一个 NURBS 控制点三点共线；
3. 公共接点位于两个相邻控制点之间，因此两段切线方向一致，而不只是落在同一直线上。

这三个点来自每个真实 `ArcCurve` 的有理二次 NURBS 表示，仅用于检查圆弧的端点切线；插件输出对象仍然是独立真圆弧。运行 `ArcFlowCheck` 会报告最大接点误差、切线夹角、控制点离线误差和控制点方向夹角。

## 已实现命令

| 命令 | 功能 |
| --- | --- |
| `ArcFlowDraw` | 连续点取端点，动态预览并绘制自动相切圆弧链 |
| `ArcFlowSpiral` | 在五类螺旋之间选择并输入参数 |
| `ArcFlowFibonacci` | 精确四分之一圆弧组成的斐波那契螺旋 |
| `ArcFlowGolden` | 半径按 φ 增长的黄金圆弧螺旋 |
| `ArcFlowArchimedean` | 阿基米德螺旋的 G1 双圆弧逼近 |
| `ArcFlowLogarithmic` | 对数螺旋的 G1 双圆弧逼近 |
| `ArcFlowFermat` | 费马螺旋的 G1 双圆弧逼近 |
| `ArcFlowConvert` | 把现有曲线近似转换为独立真圆弧链，保留原曲线 |
| `ArcFlowEdit` | 移动单段圆弧终点，同时保持起点切线与圆弧属性 |
| `ArcFlowCheck` | 检查真圆弧、接点 G1 连续性，以及两侧最近控制点与接点是否三点共线 |

螺旋在当前工作平面上生成；切换工作平面即可改变生成方向。黄金与斐波那契螺旋直接由精确四分之一圆弧构成；阿基米德、对数和费马螺旋用双圆弧插值，每个输出对象仍然是 `ArcCurve`。

## GitHub 编译

项目同时生成 Rhino 7 的 `net48` 与 Rhino 8 的 `net8.0` / AnyCPU 插件。Rhino 7 版本改用 Rhino 7 SR0 的 RhinoCommon 7.0 SDK 编译，因此也能在没有更新到 SR38 的 Rhino 7 中加载；Rhino 8 仍使用独立的 RhinoCommon 8 SDK。用户电脑无需安装开发环境。

## 1.2.1 Rhino 7 兼容性修正

- Rhino 7 目标仍为 `.NET Framework 4.8`，并明确关闭 `Prefer32Bit`。
- RhinoCommon 引用从 7.38 降到 Rhino 7 SR0 的 `7.0.20314.3000`，扩大旧版 Rhino 7 的兼容范围。
- 双版本包运行 `install.ps1` 时会同时安装 Rhino 7 与 Rhino 8，不再错误地优先只选择 Rhino 8。
- 安装脚本会解除 `.rhp` 的 Windows 下载锁定，使用带花括号的正确插件注册表路径，并设置自动加载模式。

`tests/verify_geometry.py` 使用独立的 openNURBS/rhino3dm 几何内核，把测试圆弧转换为有理二次 NURBS，再逐接点测量控制点共线误差。它覆盖五类螺旋与 500 组随机双圆弧。

独立仓库直接使用 `.github/workflows/build.yml`。若放入现有 `lianda123/opena1` 仓库的 `ArcFlow` 文件夹，把 `github-actions/build-arcflow-in-opena1.yml` 的内容保存为仓库根目录 `.github/workflows/build-arcflow.yml`。

## 安装

解压与 Rhino 版本对应的压缩包，然后任选一种方法：

1. Rhino 命令行运行 `_PlugInManager`，点击“安装”：Rhino 7 选择 `net48/ArcFlow.rhp`，Rhino 8 选择 `net8.0/ArcFlow.rhp`；或
2. 在 PowerShell 中运行：

```powershell
# 双版本包默认同时安装 Rhino 7 和 Rhino 8
powershell -ExecutionPolicy Bypass -File .\install.ps1

# Rhino 7
powershell -ExecutionPolicy Bypass -File .\install.ps1 -RhinoVersion 7
```

如果使用 Rhino `_PlugInManager` 手动安装，Rhino 7 必须选择 `net48/ArcFlow.rhp`，不要选择 `net8.0`。如果文件来自浏览器下载，先右键 `.rhp` → `属性` → 勾选“解除锁定”。

完全关闭并重启 Rhino，再输入 `ArcFlowSpiral`。生成后可运行 `ArcFlowCheck` 验证控制点共线和 G1 条件。
