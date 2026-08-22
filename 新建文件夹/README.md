# ProductMotion Timeline 0.2（Rhino 7 / 8）

面向产品机构动态演示的 Rhino 关键帧时间轴插件。交互方式接近 Blender：在某一帧摆好部件姿态、插入关键帧，再播放或拖动时间轴查看结果。0.2 新增父子层级、Rhino 组内零件独立动画，以及齿轮/皮带机械传动约束。

界面预览：[docs/UI_PREVIEW.svg](docs/UI_PREVIEW.svg)

## 0.2 主要能力

- Rhino 7：`.NET Framework 4.8`；Rhino 8：`.NET 8.0`；同一套源码多目标编译。
- Rhino 停靠面板：播放、暂停、逐帧、首尾帧、拖动时间轴、循环、FPS、起止帧。
- 位移、旋转、缩放关键帧；平滑、线性、阶梯插值；四元数旋转避免欧拉角跳变。
- 自定义轴心和连续轴转角；可输入 `360°`、`720°` 或负角度。
- **组内零件**：对象已经使用 Rhino `Group` 打组时，仍可只选其中一部分建立独立动画轨道。
- **父子层级**：父级运动会传递给所有子级；子级仍可叠加自己的关键帧，可继续建立多级子级。
- **机械约束**：外啮合齿轮、内啮合齿轮和皮带传动；根据主动/从动齿数自动计算转速比和方向；支持串联传动并阻止循环驱动。
- 动画数据写入当前 `.3dm`；可读取 0.1 数据，保存时自动升级为 0.2 数据格式。

## 最快使用流程

### 普通关键帧

1. 运行 `PMTimeline`。
2. 点击“添加部件”，选择一个完整刚性组件。
3. 转动件先点击“设轴心”，捕捉真实轴中心，并选择连续轴 X/Y/Z。
4. 在起始帧插入关键帧。
5. 切到另一帧，用 Rhino Gumball 移动、旋转或缩放部件，再插入关键帧。
6. 连续旋转可在关键帧中输入 `360°`、`720°` 等角度，然后播放。

### 已打组物体中只动画一部分

1. 点击“组内零件”或运行 `PMTAddGroupPart`。
2. 只选择需要运动的板件；该选择不会自动扩大为整个 Rhino 组。
3. 可多选若干同步运动的板件，插件会把它们转换成一个独立动画块并保留共同的组关系。
4. 继续卡关键帧；同组其他物体不会被带入这条轨道。

### 建立父子级

1. 父件和子件分别建立轨道，建议先回到起始帧并摆到装配初始位置。
2. 在时间轴中选中**子级轨道**，点击“设父级”。
3. 在 Rhino 视口选择父级动画部件。
4. 父级移动/旋转时子级整体跟随；子级自己的关键帧仍会在继承运动后叠加。
5. 需要解除时选中子级，点击“清除父级”。

### 齿轮自动传动

1. 主动齿轮和从动齿轮分别建立轨道，分别设置真实轴心和连续旋转轴。
2. 只给主动齿轮设置连续轴转角关键帧，例如 0 帧 `0°`、60 帧 `360°`。
3. 在时间轴中选中**主动件轨道**，点击“绑定传动”。
4. 在视口选择从动件，选择 `ExternalGear`、`InternalGear` 或 `Belt`。
5. 输入主动/从动齿数（皮带可输入对应节数或等比例整数）。默认相位会保持绑定瞬间的当前姿态。

外啮合齿轮使用：

`从动角 = 相位角 - 主动角 × 主动齿数 / 从动齿数`

内啮合与皮带使用同向比例。可继续把第一从动齿轮设为下一组传动的主动件，形成齿轮链。

## 命令

| 命令 | 作用 |
| --- | --- |
| `PMTimeline` | 打开时间轴面板 |
| `PMTAddPart` | 从选择对象创建完整动画部件和轨道 |
| `PMTAddGroupPart` | 从 Rhino 组内单独选择部分零件建立轨道 |
| `PMTKey` | 在当前帧插入/覆盖平滑关键帧 |
| `PMTDeleteKey` | 删除当前帧关键帧 |
| `PMTSetPivot` | 设置选中轨道的局部轴心 |
| `PMTSetParent` | 给当前选中轨道设置父级 |
| `PMTClearParent` | 清除当前轨道的父级关系 |
| `PMTBindMechanical` | 建立齿轮/皮带主动—从动约束 |
| `PMTDeleteMechanical` | 解除当前轨道的从动约束 |
| `PMTRebind` | 将丢失的轨道重新绑定到块实例 |
| `PMTPlay` | 播放/暂停 |

## 编译

Windows 环境需要：

- Visual Studio 2022 或 2026；安装“.NET 桌面开发”。
- .NET Framework 4.8 Developer Pack / Targeting Pack。
- .NET 8 SDK / Targeting Pack。
- Rhino 7 和/或 Rhino 8。

在 PowerShell 中进入本文件夹后运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于：

- `dist\net48\ProductMotionTimeline.rhp`：Rhino 7。
- `dist\net8.0\ProductMotionTimeline.rhp`：Rhino 8。
- `dist\ProductMotionTimeline-0.2.0-rhino7.zip`：Rhino 7 安装包。
- `dist\ProductMotionTimeline-0.2.0-rhino8.zip`：Rhino 8 安装包。
- `dist\ProductMotionTimeline-0.2.0-rhino7-rhino8.zip`：双版本发布包。

## 安装

编译后运行 `install.ps1`；也可以在 Rhino 中运行 `PlugInManager`，点击“安装”，选择对应版本的 `.rhp`，然后完全重启 Rhino。

## 当前边界

- 轨道仍使用 Block Instance 保持稳定的对象变换；“组内零件”会把所选板件转换为独立块，但不会把同组其他物体一起转换。
- 机械约束是设计演示级运动学关系，不进行实体碰撞、齿形接触、受力、间隙或动力学求解；齿轮中心、模数和实际啮合位置仍由设计师在 Rhino 中确定。
- 从动件的连续轴转角由约束计算；其位移、缩放或其他剩余旋转仍可卡关键帧。
- 解除父级会停止后续继承，不会自动烘焙原父级的全部历史运动。
- 创建动画部件前建议另存模型；自动转块属于结构性操作，但支持 Rhino 撤销。

更完整的技术说明见 [docs/PLUGIN_DESIGN_CN.md](docs/PLUGIN_DESIGN_CN.md)。
