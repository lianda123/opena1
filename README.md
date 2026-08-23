# Rhino 木质拼装设计插件集

面向 Rhino 7 与 Rhino 8 的木质拼装产品设计、检查、排版、导出和装配说明插件。

## 一键下载安装包

| 插件 | 功能 | Rhino 7 + Rhino 8 安装包 |
|---|---|---|
| WoodCheck 1.1.0 | 实体碰撞体积、Ø2mm轴孔同心、重复激光曲线三项检查 | [下载 WoodCheck 双版本安装包](https://github.com/lianda123/opena1/raw/main/downloads/WoodCheck-1.1.0-rhino7-rhino8.zip) |
| MechanismMaker 1.0.0 | 齿轮、齿条、凸轮、曲柄、四连杆、棘轮和日内瓦机构 | [下载 MechanismMaker 双版本安装包](https://github.com/lianda123/opena1/raw/main/downloads/MechanismMaker-1.0.0-rhino7-rhino8.zip) |
| WoodExport 1.0.0 | 零件编号、刻字、BOM、按厚度输出 DXF/DWG | [下载 WoodExport 双版本安装包](https://github.com/lianda123/opena1/raw/main/downloads/WoodExport-1.0.0-rhino7-rhino8.zip) |
| ExplodeBook 1.0.0 | 爆炸图、装配顺序、箭头、编号和 A4/A3 说明书页面 | [下载 ExplodeBook 双版本安装包](https://github.com/lianda123/opena1/raw/main/downloads/ExplodeBook-1.0.0-rhino7-rhino8.zip) |

## 安装方法

1. 下载并完全解压 ZIP。
2. Rhino 7 使用压缩包内 `net48` 文件夹中的 `.rhp`。
3. Rhino 8 使用压缩包内 `net8.0` 文件夹中的 `.rhp`。
4. 在 Rhino 输入 `PlugInManager`，点击“安装”，选择对应 `.rhp`。
5. 完全重启 Rhino。

使用已编译的安装包不需要安装 Visual Studio 或 .NET SDK。

## 源码

- [WoodExport](./WoodExport)
- [ExplodeBook](./ExplodeBook)
- [WoodCheck](./WoodCheck)
- [MechanismMaker](./MechanismMaker)
- [WoodSheetLayout](./WoodSheetLayout)
