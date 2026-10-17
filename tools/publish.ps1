# Builds a self-contained Windows release (no .NET install needed) and zips it.
#   powershell -ExecutionPolicy Bypass -File tools/publish.ps1
# Mojang's textures are NOT included in the release zip (they may not be redistributed);
# the app offers to download them from Mojang's bedrock-samples on first launch.
param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$proj = Get-Content (Join-Path $root 'src\BMAP.App\BMAP.App.csproj')
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$out = Join-Path $root "artifacts\BMAP-$version-$Runtime"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $root 'src\BMAP.App\BMAP.App.csproj') -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:BundleTextures=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
dotnet publish (Join-Path $root 'src\BMAP.Cli\BMAP.Cli.csproj') -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -o (Join-Path $out 'cli')
if ($LASTEXITCODE -ne 0) { throw 'cli publish failed' }
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $out
Get-ChildItem $out -Filter *.pdb -Recurse | Remove-Item
Remove-Item (Join-Path $out 'BMAP.Cli.runtimeconfig.json') -ErrorAction SilentlyContinue
$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
"Release: $zip"