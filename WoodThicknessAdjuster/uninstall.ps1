[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginId = "7E0039F7-EBDE-4708-8481-3970035E1FA7"
foreach ($major in @("7", "8")) {
  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  if (Test-Path $regPath) {
    Remove-Item -LiteralPath $regPath -Recurse -Force
    Write-Host "已移除 Rhino $major 插件注册。"
  }
}

$installRoot = Join-Path $env:LOCALAPPDATA "WoodThicknessAdjuster"
if (Test-Path $installRoot) {
  Remove-Item -LiteralPath $installRoot -Recurse -Force
  Write-Host "已移除插件文件：$installRoot"
}

Write-Host "卸载完成，请完全重启 Rhino。" -ForegroundColor Green
