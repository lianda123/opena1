$ErrorActionPreference = "Stop"
$pluginId = "C3D04CB5-79C2-42EE-9A81-43AAAEA16960"

foreach ($major in @("7", "8")) {
  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  if (Test-Path $regPath) {
    Remove-Item -LiteralPath $regPath -Recurse -Force
  }
}

$installRoot = Join-Path $env:LOCALAPPDATA "MechanismMaker"
if (Test-Path $installRoot) {
  Remove-Item -LiteralPath $installRoot -Recurse -Force
}

Write-Host "MechanismMaker 已卸载，请完全重启 Rhino。" -ForegroundColor Green
