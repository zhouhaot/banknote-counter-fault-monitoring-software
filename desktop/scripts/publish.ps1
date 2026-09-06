[CmdletBinding()]
param([string]$InnoCompiler = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
$ErrorActionPreference = 'Stop'
$desktopRoot = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $desktopRoot ("../artifacts/publish/" + [Guid]::NewGuid().ToString('N') + '/win-x64')
$installerDir = Join-Path $desktopRoot '../artifacts/installer'
Push-Location $desktopRoot
try {
    dotnet publish src/MoneyCounter.Desktop/MoneyCounter.Desktop.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    if (!(Test-Path -LiteralPath $InnoCompiler)) { throw 'Inno Setup compiler not found; provide -InnoCompiler.' }
    & $InnoCompiler "/DPublishDir=$([IO.Path]::GetFullPath($publishDir))" "/DOutputDir=$([IO.Path]::GetFullPath($installerDir))" (Join-Path $desktopRoot 'packaging/setup.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    Get-ChildItem -LiteralPath $installerDir -Filter '*.exe' | Get-FileHash -Algorithm SHA256 | Select-Object Hash,Path
} finally { Pop-Location }
