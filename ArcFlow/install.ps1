[CmdletBinding()]
param(
  [ValidateSet("Auto", "7", "8", "Both")]
  [string]$RhinoVersion = "Auto"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pluginId = "D3AEB3FC-1A25-489C-B52A-1B01D4BA75AA"
$sourceRoot = if (Test-Path (Join-Path $PSScriptRoot "dist")) { Join-Path $PSScriptRoot "dist" } else { $PSScriptRoot }
if ($RhinoVersion -eq "Auto") {
  $hasRhino7 = Test-Path (Join-Path $sourceRoot "net48\ArcFlow.rhp")
  $hasRhino8 = Test-Path (Join-Path $sourceRoot "net8.0\ArcFlow.rhp")
  if ($hasRhino7 -and $hasRhino8) {
    $RhinoVersion = "Both"
  }
  elseif ($hasRhino7) {
    $RhinoVersion = "7"
  }
  elseif ($hasRhino8) {
    $RhinoVersion = "8"
  }
  else {
    throw "找不到 net8.0 或 net48 的 ArcFlow.rhp"
  }
}
$targets = if ($RhinoVersion -eq "Both") { @("7", "8") } else { @($RhinoVersion) }
foreach ($major in $targets) {
  $framework = if ($major -eq "7") { "net48" } else { "net8.0" }
  $source = Join-Path $sourceRoot "$framework\ArcFlow.rhp"
  if (-not (Test-Path $source)) {
    throw "找不到 $source"
  }

  $targetFolder = Join-Path $env:LOCALAPPDATA "ArcFlow\1.2.1\Rhino$major"
  $target = Join-Path $targetFolder "ArcFlow.rhp"
  New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
  Copy-Item -LiteralPath $source -Destination $target -Force
  Unblock-File -LiteralPath $target -ErrorAction SilentlyContinue

  $legacyRegistry = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\$pluginId"
  if (Test-Path $legacyRegistry) {
    Remove-Item -LiteralPath $legacyRegistry -Recurse -Force
  }
  $registry = "HKCU:\Software\McNeel\Rhinoceros\$major.0\Plug-ins\{$pluginId}"
  New-Item -Path $registry -Force | Out-Null
  New-ItemProperty -Path $registry -Name "Name" -Value "ArcFlow" -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name "FileName" -Value $target -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name "LoadMode" -Value 2 -PropertyType DWord -Force | Out-Null
  Write-Host "ArcFlow 已安装到 Rhino $major：$target" -ForegroundColor Green
}

Write-Host "请完全关闭并重启 Rhino，然后运行 ArcFlowSpiral。"
