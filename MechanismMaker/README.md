# MechanismMaker 1.0.0

面向木质拼装、自动机与激光切割产品的 Rhino 参数化机构生成器，兼容 Rhino 7 和 Rhino 8。

## 支持的机构

| 命令 | 生成内容 |
|---|---|
| `MMGear` | 标准渐开线直齿轮；模数、齿数、压力角、侧隙、轴孔可调 |
| `MMRack` | 与同模数齿轮匹配的渐开线齿条 |
| `MMCam` | 偏心、梨形、心形、蜗形四类凸轮 |
| `MMCrank` | 曲柄板、中心活动孔与连杆销固定孔 |
| `MMFourBar` | 机架、主动曲柄、连杆、摇杆四个独立板件 |
| `MMRatchet` | 棘轮与独立棘爪，可分别移动和动画 |
| `MMGeneva` | 外啮合日内瓦从动槽轮、主动轮、驱动销孔 |

运行 `MechanismMaker` 可以从一个入口选择以上机构。

## 木质拼装默认参数

| 参数 | 默认值 |
|---|---:|
| 木板厚度 | 2.00 mm |
| Ø2mm钢轴固定孔 | Ø1.95 mm |
| Ø2mm钢轴活动孔 | Ø2.20 mm |
| 滑动导向孔/槽 | Ø2.30 mm |
| 默认齿轮模数 | 1.00 mm |
| 压力角 | 20° |
| 齿轮侧隙 | 0.15 mm |
| 日内瓦销槽间隙 | 0.30 mm |

运行 `MMSettings` 可修改参数。正式切割前仍应使用当前木板、激光焦点和机器制作公差测试片；活动孔偏紧时可在测试后放大至约 Ø2.25mm。

## 输出方式

- 在当前 Rhino 工作平面的指定点生成 **1:1、封闭、可激光切割的二维轮廓**。
- 每一个物理零件单独打组；四连杆会生成四组，棘轮和棘爪会生成两组，日内瓦主动/从动轮会生成两组。
- 自动建立带颜色的图层：齿轮齿条、凸轮、连杆、棘轮、日内瓦。
- 写入 `MM.Type`、`MM.Teeth`、`MM.Module`、`MM.MechanismId`、`MECHANISM_INFO` 等对象元数据。
- 原模型、原图层和原组不会被修改，所有生成过程支持 Rhino 撤销。

## 与现有插件配合

1. 使用 `MMGear`、`MMFourBar` 等命令生成机构轮廓。
2. 需要三维动态演示时，对封闭轮廓运行 Rhino `PlanarSrf`，再按木板厚度 `ExtrudeSrf`。
3. 使用 ProductMotion Timeline 的 `PMTAddPart` 或 `PMTAddGroupPart` 建立动画零件。
4. 齿轮传动使用 `PMTBindMechanical`，输入 MechanismMaker 已记录的齿数。
5. 使用 `WoodCheck` 检查碰撞、孔轴不同心和薄弱位置。
6. 使用 `WoodSheetLayout` 铺平并排入 A3/A4 板材。

## 机构说明

### 渐开线齿轮和齿条

- 节圆直径：`d = 模数 × 齿数`
- 两个外啮合齿轮中心距：`a = 模数 × (Z1 + Z2) ÷ 2`
- 齿条节距：`p = π × 模数`

相互啮合的齿轮和齿条必须使用相同模数与压力角。

### 四连杆

插件根据输入曲柄角求解两个圆的交点，无法闭合时停止生成；生成后会判断 Grashof 条件。该判断只描述杆长关系，仍需用 ProductMotion Timeline 和 WoodCheck 验证装配分支及实体干涉。

### 日内瓦机构

1.0版生成常用外啮合径向槽轮与驱动轮，槽宽按“驱动销直径＋间隙”计算。锁止盘、圆弧锁止面、弹簧和受力不在本版求解范围内，应根据实物样机补充。

## 安装

1. 完全解压下载的 ZIP。
2. Rhino 7 使用 `net48/MechanismMaker.rhp`。
3. Rhino 8 使用 `net8.0/MechanismMaker.rhp`。
4. 在 Rhino 运行 `PlugInManager`，点击“安装”，选择对应 `.rhp`。
5. 完全重启 Rhino，输入 `MechanismMaker` 或 `MMHelp`。

## 编译

Windows 需要 .NET 8 SDK 与 .NET Framework 4.8 Developer Pack。

```powershell
cd MechanismMaker
.\build.ps1 -Configuration Release
```

输出：

- `dist/MechanismMaker-1.0.0-rhino7.zip`
- `dist/MechanismMaker-1.0.0-rhino8.zip`
- `dist/MechanismMaker-1.0.0-rhino7-rhino8.zip`
