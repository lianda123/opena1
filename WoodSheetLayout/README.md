# WoodSheetLayout 2.1.0（Rhino 7 / Rhino 8）

面向木制拼装产品、激光切割刀模与 CAD 出图的一键铺平排版插件。2.1.0 重新采用 1.1.0 的快速规整 MaxRects 排版作为默认核心，同时把真实外轮廓与孔洞嵌套保留为可选精排模式。

插件采用非破坏方式：**不移动、删除或修改原模型，也不改变原对象的组、图层和颜色。** 每次结果输出至新的 `WoodSheetLayout_2.1.0_日期时间` 图层树。

## 2.1.0 改进重点

- 默认 `Mode=Fast`：恢复 1.1.0 的 MaxRects 快速排版，零件排列整齐，优先填满已有板框，放不下才自动增加下一张。
- `Mode=Contour`：需要异形件互补时才启用真实外轮廓碰撞计算。
- `HoleNesting=Yes`：只在 Contour 模式下启用零件放入大孔洞；默认关闭，避免普通任务浪费时间。
- 普通命令只处理平板；折弯件继续使用独立命令 `WSLayFlatBend`。
- 固定只尝试 0°/90°；`GrainLock=Yes` 时只使用 0°。
- 保留 A3、A4、Custom 自定义尺寸、横竖方向、4 mm零件间距与4 mm边框留量。
- 保留曲线朝上、文字镜像检查、厚度分类、多张板分页、厚度标注、利用率、独立图层和原属性复制。
- Rhino状态栏显示0%～100%进度，可按 `Esc` 取消。
- Rhino 7 使用 `net48` 与 Rhino 7 SR0 RhinoCommon 基线编译；Rhino 8 使用独立 `net8.0` 版本。

## 两种排版模式

| 模式 | 特点 | 建议用途 |
| --- | --- | --- |
| `Fast` | 以真实外轮廓的矩形范围运行 MaxRects，多排序策略、速度快、排列规整 | 日常木板零件、批量生产，默认推荐 |
| `Contour` | 按真实外轮廓检查碰撞，可选孔洞嵌套 | 少量异形件、需要提高材料利用率时 |

Fast 模式只把包围范围用于排版占位；铺平输出仍是零件真实几何、孔线、刻线和文字，不会把零件改成矩形。

## 边界框和间距

| 类型 | 完整尺寸 |
| --- | --- |
| A3 | 420 × 297 mm |
| A4 | 297 × 210 mm |
| Custom | 自主输入长度、宽度 |

- `PartGap=4`：零件之间默认4 mm。
- `FrameMargin=4`：零件到板框四周默认4 mm。
- 两个参数独立设置，并自动换算到Rhino文档的毫米、厘米或英寸单位。
- 同厚度零件排在同一批板框；一张排满后自动生成下一张。

## 命令

| 命令 | 功能 | 推荐别名 |
| --- | --- | --- |
| `WoodSheetLayout` | 普通平板排版；默认 Fast，可切换 Contour | `WSL` |
| `WSLayFlatA3` | 一键 A3 横向、Fast、4 mm间距与边距 | `A3P` |
| `WSLayFlatA4` | 一键 A4 横向、Fast、4 mm间距与边距 | `A4P` |
| `WSLayTight` | 真实轮廓精排；默认开启孔洞嵌套 | `WST` |
| `WSLayFlatBend` | 单独铺平折弯件并排版 | `BFP` |

## `WoodSheetLayout` 选项

| 选项 | 作用 | 默认值 |
| --- | --- | --- |
| `Sheet` | A3 / A4 / Custom | A3 |
| `Mode` | Fast / Contour | Fast |
| `Orientation` | Landscape / Portrait | Landscape |
| `CustomWidth` | 自定义完整宽度（mm） | 420 |
| `CustomHeight` | 自定义完整高度（mm） | 297 |
| `GrainLock` | 锁定木纹方向 | No |
| `HoleNesting` | Contour模式下允许放入大孔洞 | No |
| `PartGap` | 零件间距（mm） | 4 |
| `FrameMargin` | 边框留量（mm） | 4 |

## 折弯件中性层展开

`WSLayFlatBend` 独立处理厚度恒定、可展开的折弯木板：

1. 识别内外主表面与厚度。
2. 按 `NeutralFactor=0.5` 取得木板厚度中间层。
3. 将中性层与同组曲线、孔线、刻线和文字连续展开到世界XY。
4. 对“直面＋弯曲面”或“直—弯—直”按公共接缝组成连续面链，展开后保持原来的相接关系。
5. 球面、马鞍面等双曲率对象不强制展开，使用黄色编号报告。

默认公式：

```text
中性层半径 = 内半径 + 厚度 × NeutralFactor
NeutralFactor = 0.5
```

## 建模准备

1. 每块木板使用有真实厚度的闭合 Brep、Extrusion 或 Mesh。
2. 一块木板与属于它的切割线、雕刻线、孔位线或文字放进同一个 Rhino Group。
3. 不要把整套产品的所有木板放在一个 Group 中。
4. 普通平板运行 `WoodSheetLayout`；折弯板另行运行 `WSLayFlatBend`。

## 输出

- 有曲线的一面统一朝上；文字和图案避免镜像。
- 同厚度自动分类，每张板左上角显示厚度、板号、零件数和利用率。
- 复制结果保留源图层颜色、对象颜色、线型、打印属性和零件组关系。
- 未排入或无法铺平的零件用 `WSL-001` 形式的黄色编号标记源对象。
- 整个操作支持一次 Rhino 撤销。

## 编译输出

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

- `dist\WoodSheetLayout-2.1.0-rhino7.zip`
- `dist\WoodSheetLayout-2.1.0-rhino8.zip`
- `dist\WoodSheetLayout-2.1.0-rhino7-rhino8.zip`

用户安装已编译的 `.rhp` 不需要安装 .NET SDK。

## 安装

完整解压ZIP后运行 `install.ps1`；或在 Rhino 输入 `_PlugInManager` → “安装”：Rhino 7选择 `net48/WoodSheetLayout.rhp`，Rhino 8选择 `net8.0/WoodSheetLayout.rhp`。安装后完全重启Rhino。

如果Windows阻止加载：右键 `.rhp` → “属性” → 勾选“解除锁定”。

## 生产提醒

插件不会自动修改插槽公差与激光切缝。正式下料前仍建议切割板厚、插槽及折弯长度测试片。
