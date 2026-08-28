[CmdletBinding()]
param(
  [switch]$Rhino7,
  [switch]$Rhino8
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginId = "F9A7EFD6-7BBE-4E9D-A7C6-4BBE9B7DE101"
$pluginName = "ProductMotion Timeline"
$version = "0.4.10"
$sourceRoot = if (Test-Path (Join-Path $PSScriptRoot "dist")) { Join-Path $PSScriptRoot "dist" } else { $PSScriptRoot }
$installRoot = Join-Path $env:LOCALAPPDATA "ProductMotionTimeline\$version"

if (-not $Rhino7 -and -not $Rhino8) {
  $Rhino7 = Test-Path "HKLM:\SOFTWARE\McNeel\Rhinoceros\7.0\Install"
  $Rhino8 = Test-Path "HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install"
}
if (-not $Rhino7 -and -not $Rhino8) {
  throw "未检测到 Rhino 7 或 Rhino 8。也可以使用 -Rhino7 或 -Rhino8 参数指定版本。"
}

function Install-ProductMotionPlugin {
  param(
    [string]$MajorVersion,
    [string]$FrameworkFolder
  )

  $source = Join-Path $sourceRoot "$FrameworkFolder\ProductMotionTimeline.rhp"
  if (-not (Test-Path $source)) {
    throw "找不到 $source，请先运行 build.ps1。"
  }

  $targetFolder = Join-Path $installRoot "Rhino$MajorVersion"
  New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
  $target = Join-Path $targetFolder "ProductMotionTimeline.rhp"
  Copy-Item -LiteralPath $source -Destination $target -Force

  $registry = "HKCU:\Software\McNeel\Rhinoceros\$MajorVersion.0\Plug-ins\$pluginId"
  New-Item -Path $registry -Force | Out-Null
  New-ItemProperty -Path $registry -Name "Name" -Value $pluginName -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name "FileName" -Value $target -PropertyType String -Force | Out-Null
  Write-Host "已安装到 Rhino $MajorVersion：$target" -ForegroundColor Green
}

if ($Rhino7) { Install-ProductMotionPlugin -MajorVersion "7" -FrameworkFolder "net48" }
if ($Rhino8) { Install-ProductMotionPlugin -MajorVersion "8" -FrameworkFolder "net8.0" }
Write-Host "请完全关闭并重新启动 Rhino，然后运行 PMTimeline。"
