$ErrorActionPreference = "Stop"
$pluginId = "A4B064E4-E870-461D-8889-C808924C5153"

foreach ($major in @("7", "8")) {
  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  if (Test-Path $regPath) {
    Remove-Item -LiteralPath $regPath -Recurse -Force
  }
}

$installRoot = Join-Path $env:LOCALAPPDATA "WoodCheck"
if (Test-Path $installRoot) {
  Remove-Item -LiteralPath $installRoot -Recurse -Force
}

Write-Host "WoodCheck 已卸载，请完全重启 Rhino。" -ForegroundColor Green
