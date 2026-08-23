$ErrorActionPreference = "Stop"
$pluginId = "759C8B55-4FEC-4325-9694-EF58ED80BE18"

foreach ($major in @("7", "8")) {
  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  if (Test-Path $regPath) {
    Remove-Item -LiteralPath $regPath -Recurse -Force
  }
}

$installRoot = Join-Path $env:LOCALAPPDATA "WoodExport"
if (Test-Path $installRoot) {
  Remove-Item -LiteralPath $installRoot -Recurse -Force
}

Write-Host "WoodExport 已卸载，请完全重启 Rhino。" -ForegroundColor Green
