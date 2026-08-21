[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$dist = Join-Path $PSScriptRoot "dist"
$yak = "C:\Program Files\Rhino 8\System\Yak.exe"
if (-not (Test-Path $yak)) {
  throw "找不到 Rhino 8 Yak.exe：$yak"
}
if (-not (Test-Path (Join-Path $dist "net48\ProductMotionTimeline.rhp"))) {
  & (Join-Path $PSScriptRoot "build.ps1")
}

Push-Location $dist
try {
  & $yak build
  if ($LASTEXITCODE -ne 0) { throw "Yak 打包失败。" }
}
finally {
  Pop-Location
}
Write-Host "Yak 包已生成到：$dist" -ForegroundColor Green
