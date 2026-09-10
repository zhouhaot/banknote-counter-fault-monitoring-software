[CmdletBinding()]
param([string]$Installer = (Join-Path $PSScriptRoot '../../artifacts/installer/MoneyCounterMonitor-0.6.0-win-x64-setup.exe'))
$ErrorActionPreference = 'Stop'
foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {
    $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try {
        $existing = $hive.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{BC7D9E66-230B-4F78-B9F3-CF362A53B248}_is1')
        if ($null -ne $existing) { $existing.Dispose(); throw 'An existing installation is registered. Use an isolated Windows user or VM for the installer test.' }
    } finally { $hive.Dispose() }
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence = Join-Path $root ('artifacts/install-' + [Guid]::NewGuid().ToString('N'))
$target = Join-Path $evidence '中文安装路径'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$setup = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="'+$target+'"'),('/LOG="'+(Join-Path $evidence 'install.log')+'"')) -WindowStyle Hidden -Wait -PassThru
if ($setup.ExitCode -ne 0) { throw "Installer failed: $($setup.ExitCode)" }
& (Join-Path $PSScriptRoot 'test-ui.ps1') -Exe (Join-Path $target 'MoneyCounter.Desktop.exe') -EvidencePath (Join-Path $evidence 'ui')
$report = Get-Content -LiteralPath (Join-Path $evidence 'ui/ui-smoke-report.json') -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Installed application UI test did not pass.' }
$database = Join-Path $report.dataDirectory 'app.sqlite3'
$before = (Get-FileHash -LiteralPath $database).Hash
$uninstaller = Join-Path $target 'unins000.exe'
if (![IO.Path]::GetFullPath($uninstaller).StartsWith($evidence + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstall target escapes test directory.' }
$uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
if ($uninstall.ExitCode -ne 0) { throw "Uninstall failed: $($uninstall.ExitCode)" }
if ((Get-FileHash -LiteralPath $database).Hash -ne $before) { throw 'Data changed during uninstall.' }
if (Test-Path -LiteralPath (Join-Path $target 'MoneyCounter.Desktop.exe')) { throw 'Executable remains after uninstall.' }
@{ passed=$true; installerHash=(Get-FileHash -LiteralPath $Installer).Hash; dataRetained=$true; cleanMachine=$false; note='Local host; isolated data override; shortcut checks and clean-machine validation pending.' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'installer-report.json') -Encoding utf8
Write-Output "PASS: installer, installed UI, uninstall and isolated data retention. Evidence: $evidence"
