# WoodExport 1.0.0

面向激光切割木制拼装产品的 Rhino 编号与导出插件，兼容 Rhino 7 和 Rhino 8。它可以接在 `WoodSheetLayout` 铺平排版之后使用，也可以直接处理“木板实体 + 同组刀线/雕刻线”的三维装配模型。

## 一键输出内容

- **自动编号**：按“厚度 + 外形 + 孔槽几何”识别同形零件；同形零件共用编号并自动累计数量。
- **矢量刻字**：编号由真正的单线曲线构成，不依赖系统字体，可直接放到激光软件的雕刻层。
- **BOM 清单**：生成带 UTF-8 BOM 的 CSV，Excel 可直接打开中文列名。
- **按厚度拆分**：1.5 / 2 / 2.5 / 3 mm 等厚度自动分别输出 DXF、DWG 或两者。
- **保留加工信息**：组内曲线随板件铺平；临时导出曲线沿用源图层、对象颜色和图层颜色。
- **4 mm 间距**：每个厚度文件内按 A3 横向区域逐行排放，板件之间默认留 4 mm；多张区域沿 X 方向排列。

编号格式为 `P厚度-序号`，例如 `P2-001`、`P2.5-003`。再次运行编号命令时会替换该板件旧的插件刻字，不会重复叠加。

## 命令

| 命令 | 作用 |
|---|---|
| `WoodExport` | 一键编号、生成刻字、CSV BOM，并按厚度导出 DXF/DWG |
| `WXNumber` | 只自动编号并生成单线刻字 |
| `WXBOM` | 只输出 CSV 数量清单 |
| `WXSettings` | 修改字高、刻字边距、排版间距、厚度和同形判定公差 |
| `WXClearLabels` | 只删除插件生成的刻字曲线，不删除原模型 |
| `WXHelp` | 在 Rhino 命令历史中显示帮助 |

## 推荐工作流

1. 每块木板实体与属于它的孔线、槽线、雕刻线分别打组；不要把多个物理零件打进同一个组。
2. 需要精细满版时，先运行 `WoodSheetLayout`；然后选择铺平后的板件组运行 `WoodExport`。
3. 在格式步骤选择 `DXF`、`DWG`、`DXFAndDWG` 或 `BOMOnly`；直接回车默认同时输出 DXF 和 DWG。
4. 选择 BOM 文件位置。CAD 文件保存在同一文件夹，例如 `WoodParts_2mm.dxf`、`WoodParts_2.5mm.dwg`。
5. 在激光软件中把 `WoodExport_刻字` 图层设为雕刻，其余轮廓图层按原设计设置切割参数。

## BOM 列

`零件编号、名称、数量、厚度(mm)、宽(mm)、高(mm)、外接面积(mm²)、源图层`

## 默认参数

| 参数 | 默认值 |
|---|---:|
| 单线编号字高 | 4.00 mm |
| 编号边距 | 2.00 mm |
| 导出排版间距 | 4.00 mm |
| 同厚度归类公差 | 0.15 mm |
| 同形判定公差 | 0.10 mm |

## CAD 导出说明

插件调用 Rhino 自带的 `ExportSelected` 文件导出器，因此会沿用当前 Rhino 的 DXF/DWG 默认导出方案。正式下料前请在 CAD 或激光软件中抽查单位为毫米、曲线闭合且没有重复线。插件只把临时铺平曲线送入导出器，导出完成后会删除临时对象；原始板件不会被移动或删除。

## 安装

1. 完全解压对应 ZIP。
2. Rhino 7 使用 `net48/WoodExport.rhp`；Rhino 8 使用 `net8.0/WoodExport.rhp`。
3. Rhino 输入 `PlugInManager`，点击“安装”，选择对应 `.rhp`；也可右键运行 `install.ps1`。
4. 完全重启 Rhino，输入 `WoodExport`。

如果 Windows 阻止插件，右键 `.rhp` → 属性 → 解除锁定，然后重新安装并重启 Rhino。

## 编译

Windows 需要 .NET 8 SDK 与 .NET Framework 4.8 Developer Pack：

```powershell
cd WoodExport
.\build.ps1 -Configuration Release
```

输出：

- `dist/WoodExport-1.0.0-rhino7.zip`
- `dist/WoodExport-1.0.0-rhino8.zip`
- `dist/WoodExport-1.0.0-rhino7-rhino8.zip`
