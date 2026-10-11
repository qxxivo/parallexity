$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $localSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
    $sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
    & $sdk build -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $sdk '.\bin\Release\net8.0-windows10.0.19041.0\parallexity.dll' --self-test
    $result = Get-Content -LiteralPath 'test-results.json' -Raw | ConvertFrom-Json
    if (-not $result.passed) { throw $result.error }
    Write-Host "Passed $($result.checks.Count) checks. Details: test-results.json"
} finally { Pop-Location }
