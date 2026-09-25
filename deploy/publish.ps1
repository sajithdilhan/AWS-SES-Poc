<#
.SYNOPSIS
    Publishes SesPoc.Api as a framework-dependent build ready to copy to an IIS site on EC2.
.EXAMPLE
    .\deploy\publish.ps1
    .\deploy\publish.ps1 -Output C:\temp\sespoc -Zip
#>
param(
    [string]$Configuration = "Release",
    [string]$Output = (Join-Path $PSScriptRoot "..\publish"),
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\SesPoc.Api\SesPoc.Api.csproj"

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

dotnet publish $project -c $Configuration -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

if ($Zip) {
    $zipPath = "$Output.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $zipPath
    Write-Host "Created $zipPath"
}

Write-Host "Published to $((Resolve-Path $Output).Path)"
Write-Host "Copy the contents to the IIS site folder on the EC2 instance (e.g. C:\inetpub\SesPoc)."
