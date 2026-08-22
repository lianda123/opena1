$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginId = "8AB71642-B4F7-496D-A0EA-6A1495ED3E20"
foreach ($major in @("7", "8")) {
  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  if (Test-Path $regPath) {
    Remove-Item -LiteralPath $regPath -Recurse -Force
  }
}

$installRoot = Join-Path $env:LOCALAPPDATA "WoodSheetLayout\1.0.0"
if (Test-Path $installRoot) {
  Remove-Item -LiteralPath $installRoot -Recurse -Force
}
Write-Host "Wood Sheet Layout 已卸载，请完全重启 Rhino。" -ForegroundColor Green
