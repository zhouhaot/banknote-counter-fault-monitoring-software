[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\MoneyCounter.Desktop\bin\Debug\net10.0-windows\MoneyCounter.Desktop.exe'),
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\artifacts\ui-smoke'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$desktopRoot = Split-Path $PSScriptRoot -Parent
$testProject = Join-Path $desktopRoot 'tests\MoneyCounter.UiTests\MoneyCounter.UiTests.csproj'
$Exe = [IO.Path]::GetFullPath($Exe)
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)

Push-Location $desktopRoot
try {
    if (!$NoBuild) {
        dotnet build $testProject --nologo
        if ($LASTEXITCODE -ne 0) { throw 'UI smoke harness build failed.' }
    }
    dotnet run --no-build --project $testProject -- --exe $Exe --evidence $EvidencePath
    if ($LASTEXITCODE -ne 0) { throw 'Native UI smoke test failed. See ui-smoke-report.json in the evidence directory.' }
} finally {
    Pop-Location
}
