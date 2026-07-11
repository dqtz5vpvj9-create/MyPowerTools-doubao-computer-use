[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$Project = Join-Path $Root "desktop\DoubaoComputerUse.Desktop.csproj"
if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) {
    throw "desktop project not found: $Project"
}

& dotnet run --project $Project --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "desktop app exited with code $LASTEXITCODE"
}

