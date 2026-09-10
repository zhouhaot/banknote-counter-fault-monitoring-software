$ErrorActionPreference = 'Stop'
$desktopRoot = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path $desktopRoot '../artifacts/verification'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
Push-Location $desktopRoot
try {
    dotnet restore MoneyCounter.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    dotnet build MoneyCounter.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test tests/MoneyCounter.Tests/MoneyCounter.Tests.csproj -c Release --no-build --logger trx --results-directory $evidence
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    dotnet test tests/MoneyCounter.Desktop.Tests/MoneyCounter.Desktop.Tests.csproj -c Release --no-build --logger trx --results-directory $evidence
    if ($LASTEXITCODE -ne 0) { throw 'Desktop view-model tests failed' }
    $auditText = dotnet package list --project MoneyCounter.sln --include-transitive --vulnerable --format json --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Dependency audit failed' }
    $auditText | Set-Content (Join-Path $evidence 'dependency-audit.json') -Encoding utf8
    $audit = ($auditText -join "`n") | ConvertFrom-Json
    $findings = @($audit.projects.frameworks.topLevelPackages.vulnerabilities) + @($audit.projects.frameworks.transitivePackages.vulnerabilities)
    if (@($findings | Where-Object { $null -ne $_ }).Count -gt 0) { throw 'Dependency vulnerabilities found; inspect report.' }
} finally { Pop-Location }
