param(
    [switch]$Package
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\MusicVisualizer.csproj"
$pluginOut = Join-Path $root "Plugin"

Write-Host "Building MusicVisualizer (Release)..." -ForegroundColor Cyan
dotnet build $proj -c Release -o $pluginOut

if ($Package) {
    $distDir = Join-Path $root "dist"
    $stageDir = Join-Path $distDir "com.musicvisualizer.loopback.visualizer.sdPlugin"
    $zipPath = Join-Path $distDir "MusicVisualizerLoopback.streamDeckPlugin"

    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    Copy-Item (Join-Path $root "manifest.json") $stageDir
    Copy-Item (Join-Path $root "Imgs") (Join-Path $stageDir "Imgs") -Recurse
    Copy-Item (Join-Path $root "PropertyInspector") (Join-Path $stageDir "PropertyInspector") -Recurse
    Copy-Item (Join-Path $root "Plugin") (Join-Path $stageDir "Plugin") -Recurse
    Get-ChildItem (Join-Path $stageDir "Plugin") -Filter "*.log" -ErrorAction SilentlyContinue | Remove-Item -Force

    Compress-Archive -Path $stageDir -DestinationPath $zipPath -Force
    Write-Host "Packaged plugin: $zipPath" -ForegroundColor Green
}
