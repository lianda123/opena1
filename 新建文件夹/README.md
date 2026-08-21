# ProductMotion Timeline（Rhino 7 / 8）

面向产品机构动态演示的 Rhino 关键帧时间轴插件原型。交互思路接近 Blender：在某一帧摆好部件姿态，插入关键帧，然后播放或拖动时间轴查看插值结果。

界面预览：[docs/UI_PREVIEW.svg](docs/UI_PREVIEW.svg)

## v0.1 已实现

- Rhino 7（`.NET Framework 4.8`）与 Rhino 8（`.NET 7` 插件目标，可在 Rhino 8 当前运行时下加载）共用一套源码、多目标编译。
- Rhino 停靠面板：播放、暂停、逐帧、首尾帧、拖动时间轴、循环、FPS、起止帧。
- 多部件轨道；选中多件普通几何体时，可自动合并成一个动画块。
- 插入/覆盖、删除、复制、粘贴、拖动关键帧。
- 平滑、线性、阶梯三种插值。
- 位移、旋转、缩放关键帧；四元数旋转插值避免欧拉角跳变。
- 自定义轴心；轴方向采用当前工作平面 XYZ，适合齿轮、拨盘、门、摆杆、旋转楼梯等机构。
- 连续轴转角通道；可直接输入 `360°`、`720°` 或负角度，完整表达多圈旋转。
- 动画数据写入当前 `.3dm` 文档，不需要外置工程文件。
- 轨道对象丢失后可重新绑定。

## 最快使用流程

1. 运行命令 `PMTimeline` 打开“产品动态时间轴”。
2. 点击“添加部件”，选择一个完整运动组件。可以多选，插件会把它合并为一个块实例。
3. 如为转动件，切换到合适的 Rhino 工作平面，点击“设轴心”，捕捉轴中心。
4. 在起始帧保持初始姿态，点击“插入/更新帧”。
5. 切到另一帧，用 Rhino Gumball 移动、旋转或缩放该动画块。
6. 再次点击“插入/更新帧”，然后播放或拖动时间轴。

如果是连续转动件，在轨道工具栏选择“连续轴”X/Y/Z，并在关键帧输入转角。例如 0 帧为 `0°`、60 帧为 `360°`；两圈旋转则输入 `720°`。

建议把一个真正同步运动的组件做成一条轨道。例如“拨盘”“棘爪”“铃铛”“柜门”分别建轨；一个组件内部不相对运动的零件可以放在同一个块内。

## 命令

| 命令 | 作用 |
| --- | --- |
| `PMTimeline` | 打开时间轴面板 |
| `PMTAddPart` | 从选择对象创建动画部件和轨道 |
| `PMTKey` | 在当前帧插入/覆盖平滑关键帧 |
| `PMTDeleteKey` | 删除当前帧关键帧 |
| `PMTSetPivot` | 设置选中轨道的局部轴心 |
| `PMTRebind` | 将丢失的轨道重新绑定到块实例 |
| `PMTPlay` | 播放/暂停 |

## 编译

Windows 环境需要：

- Visual Studio 2022 或 2026；安装“.NET 桌面开发”。
- .NET Framework 4.8 Developer Pack / Targeting Pack。
- .NET 7 SDK / Targeting Pack。
- Rhino 7 和/或 Rhino 8。

在 PowerShell 中进入本文件夹后运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于：

- `dist\net48\ProductMotionTimeline.rhp`：Rhino 7。
- `dist\net7.0\ProductMotionTimeline.rhp`：Rhino 8。
- `dist\ProductMotionTimeline-0.1.0-release.zip`：双版本发布包。

也可以在 Visual Studio 中打开 `ProductMotionTimeline.sln`，选择 `Release | Any CPU` 后生成。

如需制作 Rhino PackageManager 使用的 `.yak` 包，在安装 Rhino 8 的电脑上继续运行 `package-yak.ps1`。

## 安装

编译后运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

或者打开 Rhino，运行 `PlugInManager`，点击“安装”，选择对应版本的 `.rhp`，完全重启 Rhino。

## v0.1 使用边界

- 当前版本以“块实例”作为动画部件，以保证可以稳定读取完整变换；普通对象会在建轨时自动转为块。
- 播放和拖动时间轴会更新文档中的块实例位置；停止播放后再使用 Gumball 编辑姿态。
- v0.1 先实现物体变换轨；相机轨、显隐轨、材质轨、事件轨、齿轮驱动关系和视频导出已放入后续路线。
- 创建动画部件前建议另存一次模型；自动转块属于结构性操作，但支持 Rhino 撤销。

更完整的技术设计见 [docs/PLUGIN_DESIGN_CN.md](docs/PLUGIN_DESIGN_CN.md)。
