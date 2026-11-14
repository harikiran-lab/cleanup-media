<#
.SYNOPSIS
    Builds the service and the configuration app into the "publish" folder.
.DESCRIPTION
    The output is self-contained: the target PC does not need .NET installed.
    Copy the whole folder to its final place (for example C:\Program Files\MediaCleanup)
    and run MediaCleanup.Config.exe from there.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\publish"),
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

foreach ($project in "src\MediaCleanup.Service", "src\MediaCleanup.ConfigApp") {
    dotnet publish (Join-Path $root $project) -c Release -r $Runtime --self-contained true -o $Output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

Write-Host ""
Write-Host "Done. Files are in: $((Resolve-Path $Output).Path)" -ForegroundColor Green
