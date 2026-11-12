<#
.SYNOPSIS
Rebuilds WeatherRadarPlugin and creates a versioned distribution ZIP.
#>
param(
    [string]$VatSysDir = "C:\Program Files (x86)\vatSys",
    [switch]$OpenZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "WeatherRadarPlugin\WeatherRadarPlugin.csproj"
$buildDir = Join-Path $root "WeatherRadarPlugin\bin\Release"
$dllPath = Join-Path $buildDir "WeatherRadarPlugin.dll"
$distRoot = Join-Path $root "dist"
$distDir = Join-Path $distRoot "WeatherRadarPlugin"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio or Build Tools with MSBuild."
}

$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild not found via vswhere."
}

if (-not (Test-Path -LiteralPath (Join-Path $VatSysDir "bin\vatSys.exe"))) {
    throw "vatSys not found at '$VatSysDir'. Pass -VatSysDir with the vatSys install path."
}

Write-Host "Building WeatherRadarPlugin (Release)..." -ForegroundColor Cyan
& $msbuild $project /nologo /verbosity:minimal /t:Rebuild /p:Configuration=Release "/p:VatSysDir=$VatSysDir" "/p:OutputPath=$buildDir\"
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed."
}
if (-not (Test-Path -LiteralPath $dllPath)) {
    throw "Built plugin was not found at '$dllPath'."
}

$version = (Get-Item -LiteralPath $dllPath).VersionInfo.FileVersion
if (-not $version) {
    throw "Could not read the version from '$dllPath'."
}

Write-Host "Assembling distribution..." -ForegroundColor Cyan
if (Test-Path -LiteralPath $distDir) {
    Remove-Item -LiteralPath $distDir -Recurse -Force
}
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Copy-Item -LiteralPath $dllPath -Destination $distDir

@"
WeatherRadarPlugin v$version
================================

Copy the WeatherRadarPlugin folder into your active vatSys profile's
Plugins folder, for example:

  Documents\vatSys Files\Profiles\Australia\Plugins\WeatherRadarPlugin

Restart vatSys after installing or replacing the plugin.

The radar appears under Info > Weather Radar. By default it shows rain
only inside your vis range; tick "Show All Radars" to see every radar.

When vatSys downloads a new Australia profile version it can delete
plugin folders that are not part of the profile, including this one.
After a profile update, check the folder is still there and copy it
back in if it has gone.

The plugin downloads radar images from the Bureau of Meteorology's
anonymous FTP service for your own personal use. Do not stream or
otherwise republish the overlay. It is supplemental information only
and must not be used for real-world or operational decisions.
"@ | Set-Content -LiteralPath (Join-Path $distDir "INSTALL.txt") -Encoding UTF8

$zipPath = Join-Path $distRoot "WeatherRadarPlugin-v$version.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $distDir -DestinationPath $zipPath

Write-Host ""
Write-Host "Done. Version $version" -ForegroundColor Green
Write-Host "  Folder: $distDir"
Write-Host "  Zip:    $zipPath"

if ($OpenZip) {
    Invoke-Item -LiteralPath $zipPath
}
