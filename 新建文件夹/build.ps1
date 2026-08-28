[CmdletBinding()]
param(
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = $PSScriptRoot
$solution = Join-Path $projectRoot "ProductMotionTimeline.sln"
$project = Join-Path $projectRoot "src\ProductMotionTimeline\ProductMotionTimeline.csproj"
$dist = Join-Path $projectRoot "dist"
$net48Out = Join-Path $projectRoot "src\ProductMotionTimeline\bin\$Configuration\net48"
$net8Out = Join-Path $projectRoot "src\ProductMotionTimeline\bin\$Configuration\net8.0"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "未找到 dotnet。请安装 .NET 8 SDK、.NET Framework 4.8 Developer Pack 与 .NET 桌面开发。"
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

$rhino7Plugin = Join-Path $net48Out "ProductMotionTimeline.rhp"
$rhino8Plugin = Join-Path $net8Out "ProductMotionTimeline.rhp"
if (-not (Test-Path $rhino7Plugin)) { throw "未生成 Rhino 7 插件：$rhino7Plugin" }
if (-not (Test-Path $rhino8Plugin)) { throw "未生成 Rhino 8 插件：$rhino8Plugin" }

Copy-Item -LiteralPath $rhino7Plugin -Destination (Join-Path $dist "net48\ProductMotionTimeline.rhp")
Copy-Item -LiteralPath $rhino8Plugin -Destination (Join-Path $dist "net8.0\ProductMotionTimeline.rhp")

foreach ($extension in @("pdb", "xml")) {
  $file7 = Join-Path $net48Out "ProductMotionTimeline.$extension"
  $file8 = Join-Path $net8Out "ProductMotionTimeline.$extension"
  if (Test-Path $file7) { Copy-Item -LiteralPath $file7 -Destination (Join-Path $dist "net48") }
  if (Test-Path $file8) { Copy-Item -LiteralPath $file8 -Destination (Join-Path $dist "net8.0") }
}

Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $projectRoot "install.ps1") -Destination $dist
Copy-Item -LiteralPath (Join-Path $projectRoot "uninstall.ps1") -Destination $dist
Copy-Item -LiteralPath (Join-Path $projectRoot "package-yak.ps1") -Destination $dist
Copy-Item -LiteralPath (Join-Path $projectRoot "manifest.yml") -Destination $dist

$commonContents = @(
  (Join-Path $dist "README.md"),
  (Join-Path $dist "install.ps1"),
  (Join-Path $dist "uninstall.ps1"),
  (Join-Path $dist "package-yak.ps1"),
  (Join-Path $dist "manifest.yml")
)

$rhino7Zip = Join-Path $dist "ProductMotionTimeline-0.4.8-rhino7.zip"
Compress-Archive -Path (@(
  (Join-Path $dist "net48")
) + $commonContents) -DestinationPath $rhino7Zip -Force

$rhino8Zip = Join-Path $dist "ProductMotionTimeline-0.4.8-rhino8.zip"
Compress-Archive -Path (@(
  (Join-Path $dist "net8.0")
) + $commonContents) -DestinationPath $rhino8Zip -Force

$combinedZip = Join-Path $dist "ProductMotionTimeline-0.4.8-rhino7-rhino8.zip"
$releaseContents = @(
  (Join-Path $dist "net48"),
  (Join-Path $dist "net8.0"),
  (Join-Path $dist "README.md"),
  (Join-Path $dist "install.ps1"),
  (Join-Path $dist "uninstall.ps1"),
  (Join-Path $dist "package-yak.ps1"),
  (Join-Path $dist "manifest.yml")
)
Compress-Archive -Path $releaseContents -DestinationPath $combinedZip -Force

Write-Host "编译完成：" -ForegroundColor Green
Write-Host "Rhino 7: $rhino7Plugin"
Write-Host "Rhino 8: $rhino8Plugin"
Write-Host "Rhino 7 安装包: $rhino7Zip"
Write-Host "Rhino 8 安装包: $rhino8Zip"
Write-Host "双版本发布包: $combinedZip"
