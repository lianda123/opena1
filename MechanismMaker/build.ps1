[CmdletBinding()]
param(
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = $PSScriptRoot
$solution = Join-Path $projectRoot "MechanismMaker.sln"
$project = Join-Path $projectRoot "src\MechanismMaker\MechanismMaker.csproj"
$dist = Join-Path $projectRoot "dist"
$net48Out = Join-Path $projectRoot "src\MechanismMaker\bin\$Configuration\net48"
$net8Out = Join-Path $projectRoot "src\MechanismMaker\bin\$Configuration\net8.0"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "未找到 dotnet。请安装 .NET 8 SDK 与 .NET Framework 4.8 Developer Pack。"
}

if (Test-Path $dist) {
  Remove-Item -LiteralPath $dist -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $dist "net48") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dist "net8.0") -Force | Out-Null

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "NuGet 还原失败。" }
dotnet build $project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "编译失败。" }

$rhino7Plugin = Join-Path $net48Out "MechanismMaker.rhp"
$rhino8Plugin = Join-Path $net8Out "MechanismMaker.rhp"
if (-not (Test-Path $rhino7Plugin)) { throw "未生成 Rhino 7 插件：$rhino7Plugin" }
if (-not (Test-Path $rhino8Plugin)) { throw "未生成 Rhino 8 插件：$rhino8Plugin" }

Copy-Item -LiteralPath $rhino7Plugin -Destination (Join-Path $dist "net48\MechanismMaker.rhp")
Copy-Item -LiteralPath $rhino8Plugin -Destination (Join-Path $dist "net8.0\MechanismMaker.rhp")

foreach ($extension in @("pdb", "xml")) {
  $file7 = Join-Path $net48Out "MechanismMaker.$extension"
  $file8 = Join-Path $net8Out "MechanismMaker.$extension"
  if (Test-Path $file7) { Copy-Item -LiteralPath $file7 -Destination (Join-Path $dist "net48") }
  if (Test-Path $file8) { Copy-Item -LiteralPath $file8 -Destination (Join-Path $dist "net8.0") }
}

foreach ($file in @("README.md", "install.ps1", "uninstall.ps1", "manifest.yml")) {
  Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $dist
}

$common = @(
  (Join-Path $dist "README.md"),
  (Join-Path $dist "install.ps1"),
  (Join-Path $dist "uninstall.ps1"),
  (Join-Path $dist "manifest.yml")
)

$rhino7Zip = Join-Path $dist "MechanismMaker-1.0.0-rhino7.zip"
Compress-Archive -Path (@((Join-Path $dist "net48")) + $common) -DestinationPath $rhino7Zip -Force

$rhino8Zip = Join-Path $dist "MechanismMaker-1.0.0-rhino8.zip"
Compress-Archive -Path (@((Join-Path $dist "net8.0")) + $common) -DestinationPath $rhino8Zip -Force

$combinedZip = Join-Path $dist "MechanismMaker-1.0.0-rhino7-rhino8.zip"
Compress-Archive -Path @(
  (Join-Path $dist "net48"),
  (Join-Path $dist "net8.0"),
  (Join-Path $dist "README.md"),
  (Join-Path $dist "install.ps1"),
  (Join-Path $dist "uninstall.ps1"),
  (Join-Path $dist "manifest.yml")
) -DestinationPath $combinedZip -Force

Write-Host "MechanismMaker 1.0.0 编译完成。" -ForegroundColor Green
Write-Host "Rhino 7: $rhino7Zip"
Write-Host "Rhino 8: $rhino8Zip"
Write-Host "双版本: $combinedZip"
