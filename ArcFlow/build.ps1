[CmdletBinding()]
param(
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$solution = Join-Path $root "ArcFlow.sln"
$project = Join-Path $root "src\ArcFlow\ArcFlow.csproj"
$outputRoot = Join-Path $root "src\ArcFlow\bin\$Configuration"
$dist = Join-Path $root "dist"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "未找到 dotnet SDK。"
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

foreach ($framework in @("net48", "net8.0")) {
  $output = Join-Path $outputRoot $framework
  $plugin = Join-Path $output "ArcFlow.rhp"
  if (-not (Test-Path $plugin)) {
    throw "没有生成 $framework 插件：$plugin"
  }

  Copy-Item -LiteralPath $plugin -Destination (Join-Path $dist "$framework\ArcFlow.rhp")
  foreach ($extension in @("pdb", "xml")) {
    $candidate = Join-Path $output "ArcFlow.$extension"
    if (Test-Path $candidate) {
      Copy-Item -LiteralPath $candidate -Destination (Join-Path $dist $framework)
    }
  }
}
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $root "install.ps1") -Destination $dist
Copy-Item -LiteralPath (Join-Path $root "manifest.yml") -Destination $dist

$rhino7Zip = Join-Path $dist "ArcFlow-1.2.1-rhino7.zip"
Compress-Archive -Path @(
  (Join-Path $dist "net48"),
  (Join-Path $dist "README.md"),
  (Join-Path $dist "install.ps1"),
  (Join-Path $dist "manifest.yml")
) -DestinationPath $rhino7Zip -Force

$rhino8Zip = Join-Path $dist "ArcFlow-1.2.1-rhino8.zip"
Compress-Archive -Path @(
  (Join-Path $dist "net8.0"),
  (Join-Path $dist "README.md"),
  (Join-Path $dist "install.ps1"),
  (Join-Path $dist "manifest.yml")
) -DestinationPath $rhino8Zip -Force

$combinedZip = Join-Path $dist "ArcFlow-1.2.1-rhino7-rhino8.zip"
Compress-Archive -Path @(
  (Join-Path $dist "net48"),
  (Join-Path $dist "net8.0"),
  (Join-Path $dist "README.md"),
  (Join-Path $dist "install.ps1"),
  (Join-Path $dist "manifest.yml")
) -DestinationPath $combinedZip -Force

Write-Host "ArcFlow Rhino 7/8 编译完成：$combinedZip" -ForegroundColor Green
