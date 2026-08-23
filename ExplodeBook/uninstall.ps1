$ErrorActionPreference = "Stop"
$pluginId = "0BA544B2-8E36-4A71-8E3E-A33D722B04AC"
foreach ($major in @("7", "8")) {
  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  if (Test-Path $regPath) {
    Remove-Item -LiteralPath $regPath -Recurse -Force
  }
}
$installRoot = Join-Path $env:LOCALAPPDATA "ExplodeBook"
if (Test-Path $installRoot) {
  Remove-Item -LiteralPath $installRoot -Recurse -Force
}
Write-Host "ExplodeBook 已卸载，请完全重启 Rhino。" -ForegroundColor Green
