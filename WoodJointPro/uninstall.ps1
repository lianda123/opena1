[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$pluginId = "86360262-92E9-4D3E-B819-D81B536C39BF"
foreach ($major in @("7", "8")) {
  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  if (Test-Path $regPath) {
    Remove-Item -LiteralPath $regPath -Recurse -Force
    Write-Host "已移除 Rhino $major 插件注册。"
  }
}
$installRoot = Join-Path $env:LOCALAPPDATA "WoodJointPro"
if (Test-Path $installRoot) {
  Remove-Item -LiteralPath $installRoot -Recurse -Force
  Write-Host "已移除插件文件：$installRoot"
}
Write-Host "卸载完成，请完全重启 Rhino。" -ForegroundColor Green
