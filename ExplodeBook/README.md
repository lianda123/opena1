# ExplodeBook 1.0.0

面向木质拼装产品的 Rhino 爆炸图与装配说明书插件，兼容 Rhino 7 和 Rhino 8。

## 功能

- 把每个打组零件识别为独立装配单元，原模型不会被移动或删除。
- 自动选择最大、最接近装配中心的零件作为基准件。
- 根据零件包围盒接触距离和中心距离推导由内到外的装配顺序。
- 支持径向、X、Y、Z 四种爆炸方向。
- 自动生成完整爆炸图、红色安装箭头、零件编号圆标和标题。
- 自动创建 Rhino `Layout`：第 0 页为装配总览，之后每页显示已安装部分、黄色当前零件和安装箭头。
- 支持 A4/A3 横向、A4/A3 竖向；布局页可直接用 Rhino 打印或另存为 PDF。
- 优先沿用 `WoodExport.PartNumber`，例如 `P2-001`；没有 WoodExport 编号时自动使用 `B-01`、`B-02`。
- 复制的非高亮零件保留源图层和颜色；插件生成对象均带标记，可一键清理。

## 命令

| 命令 | 作用 |
|---|---|
| `ExplodeBook` | 一键生成完整爆炸图、装配顺序和说明书 Layout |
| `EBExplode` | 只生成爆炸总览 |
| `EBPages` | 只生成说明书页面 |
| `EBSetOrder` | 按点选先后记录手动装配顺序，第一个零件为底座/基准件 |
| `EBAutoOrder` | 清除手动顺序，下次恢复自动分析 |
| `EBSettings` | 修改爆炸距离、箭头尺寸、页面间距和最大步骤数 |
| `EBClear` | 删除插件生成的爆炸图与 `EB_` 说明页，不删除原装配体 |
| `EBHelp` | 显示命令帮助 |

## 推荐工作流

1. 每个物理零件分别打组；一块板和它的装饰/结构曲线可以放在同一组。
2. 如已运行 `WoodExport`，ExplodeBook 会直接使用相同零件编号。
3. 输入 `ExplodeBook`，选择爆炸方向与 A4/A3 页面。
4. 选择完整装配模型并回车。
5. 切换到 Rhino 底部的 `EB_00_装配总览`、`EB_01_...` 等布局页检查结果。
6. 顺序不正确时运行 `EBSetOrder`，按真实安装先后逐个点选零件组，然后再次运行 `ExplodeBook`。
7. 在布局页运行 Rhino `Print`，选择 PDF 打印机即可输出说明书 PDF。

## 图层

- `ExplodeBook_箭头`：红色安装方向箭头。
- `ExplodeBook_编号`：零件编号圆标和爆炸图标题。
- `ExplodeBook_页面`：A4/A3 页面边框、标题和步骤说明。
- `ExplodeBook_当前步骤`：说明页中正在安装的黄色高亮零件。

## 默认参数

| 参数 | 默认值 |
|---|---:|
| 基础爆炸距离 | 25 mm |
| 箭头头部尺寸 | 4 mm |
| 模型空间页面间距 | 25 mm |
| 最大步骤数 | 40 |
| 页面 | A4 横向 |

## 安装

1. 完全解压 ZIP。
2. Rhino 7 使用 `net48/ExplodeBook.rhp`；Rhino 8 使用 `net8.0/ExplodeBook.rhp`。
3. Rhino 输入 `PlugInManager`，点击“安装”，选择对应 `.rhp`；也可运行 `install.ps1`。
4. 完全重启 Rhino，输入 `ExplodeBook`。

## 编译

Windows 需要 .NET 8 SDK 与 .NET Framework 4.8 Developer Pack：

```powershell
cd ExplodeBook
.\build.ps1 -Configuration Release
```

输出：

- `dist/ExplodeBook-1.0.0-rhino7.zip`
- `dist/ExplodeBook-1.0.0-rhino8.zip`
- `dist/ExplodeBook-1.0.0-rhino7-rhino8.zip`
