$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginId = "8AB71642-B4F7-496D-A0EA-6A1495ED3E20"
foreach ($major in @("7", "8")) {
  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  if (Test-Path $regPath) {
    Remove-Item -LiteralPath $regPath -Recurse -Force
  }
}

foreach ($version in @("2.0.0", "2.0.1", "2.0.2", "2.0.3", "2.0.4", "2.1.0", "2.1.1", "2.1.2", "2.1.3", "2.1.4", "2.1.5", "2.1.6", "2.1.7", "2.2.0", "2.2.1", "2.2.2", "2.2.3")) {
  $installRoot = Join-Path $env:LOCALAPPDATA "WoodSheetLayout\$version"
  if (Test-Path $installRoot) {
    Remove-Item -LiteralPath $installRoot -Recurse -Force
  }
}
Write-Host "Wood Sheet Layout 已卸载，请完全重启 Rhino。" -ForegroundColor Green
