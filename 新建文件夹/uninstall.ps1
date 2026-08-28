[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginId = "F9A7EFD6-7BBE-4E9D-A7C6-4BBE9B7DE101"
$version = "0.4.13"
$installRoot = Join-Path $env:LOCALAPPDATA "ProductMotionTimeline\$version"

foreach ($major in @("7", "8")) {
  $registry = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\$pluginId"
  if (Test-Path $registry) {
    Remove-Item -LiteralPath $registry -Recurse -Force
    Write-Host "已移除 Rhino $major 注册信息。"
  }
}

if (Test-Path $installRoot) {
  Remove-Item -LiteralPath $installRoot -Recurse -Force
  Write-Host "已移除 ProductMotion Timeline 0.4.13 插件文件。"
}
Write-Host "卸载完成，请完全重启 Rhino。" -ForegroundColor Green
