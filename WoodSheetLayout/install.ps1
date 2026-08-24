[CmdletBinding()]
param(
  [ValidateSet("7", "8", "Both")]
  [string]$RhinoVersion = "Both"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginId = "8AB71642-B4F7-496D-A0EA-6A1495ED3E20"
$version = "2.2.1"
$installRoot = Join-Path $env:LOCALAPPDATA "WoodSheetLayout\$version"
$targets = if ($RhinoVersion -eq "Both") { @("7", "8") } else { @($RhinoVersion) }

foreach ($major in $targets) {
  $framework = if ($major -eq "7") { "net48" } else { "net8.0" }
  $source = Join-Path $PSScriptRoot "$framework\WoodSheetLayout.rhp"
  if (-not (Test-Path $source)) {
    throw "安装包中缺少 $framework\WoodSheetLayout.rhp。请先完整解压 ZIP。"
  }

  $destination = Join-Path $installRoot "Rhino$major"
  New-Item -ItemType Directory -Path $destination -Force | Out-Null
  Copy-Item -LiteralPath $source -Destination (Join-Path $destination "WoodSheetLayout.rhp") -Force
  $pluginPath = Join-Path $destination "WoodSheetLayout.rhp"
  Unblock-File -LiteralPath $pluginPath -ErrorAction SilentlyContinue

  $regPath = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  New-Item -Path $regPath -Force | Out-Null
  New-ItemProperty -Path $regPath -Name "Name" -Value "Wood Sheet Layout" -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $regPath -Name "FileName" -Value $pluginPath -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $regPath -Name "LoadMode" -Value 2 -PropertyType DWord -Force | Out-Null
  Write-Host "已安装 Rhino $major：$pluginPath"
}

Write-Host "安装完成，请完全重启 Rhino。" -ForegroundColor Green
