param([switch]$PackageOnly)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$localSdk = Join-Path $projectDirectory '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectDirectory '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $projectDirectory '.tools\nuget'
$outputDirectory = Join-Path $projectDirectory 'dist\parallexity-win-x64'
& $sdk publish (Join-Path $projectDirectory 'PhoneRemote.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $projectDirectory 'README.md'), (Join-Path $projectDirectory 'LICENSE'), (Join-Path $projectDirectory 'THIRD-PARTY-NOTICES.md') -Destination $outputDirectory
Copy-Item -LiteralPath (Join-Path $projectDirectory 'licenses') -Destination $outputDirectory -Recurse -Force

if ($PackageOnly) { Write-Host "Ready: $outputDirectory\parallexity.exe"; return }

# Publish away from the running app, then replace its launch path. Windows allows
# a running image to be renamed; its process continues using the previous image.
$installedDirectory = Join-Path $projectDirectory 'dist\PhoneRemote-win-x64'
New-Item -ItemType Directory -Path $installedDirectory -Force | Out-Null
$installedExe = Join-Path $installedDirectory 'parallexity.exe'
$temporaryExe = Join-Path $installedDirectory 'parallexity.new.exe'
Copy-Item -LiteralPath (Join-Path $outputDirectory 'parallexity.exe') -Destination $temporaryExe -Force
$previousExe = $null
try {
    if (Test-Path -LiteralPath $installedExe) {
        $previousExe = Join-Path $installedDirectory ('parallexity.running-' + [guid]::NewGuid().ToString('N') + '.exe')
        Move-Item -LiteralPath $installedExe -Destination $previousExe -ErrorAction Stop
    }
    Move-Item -LiteralPath $temporaryExe -Destination $installedExe -ErrorAction Stop
} catch {
    if ($previousExe -and (Test-Path -LiteralPath $previousExe) -and !(Test-Path -LiteralPath $installedExe)) {
        Move-Item -LiteralPath $previousExe -Destination $installedExe
    }
    throw
}
Copy-Item -LiteralPath (Join-Path $projectDirectory 'README.md'), (Join-Path $projectDirectory 'LICENSE'), (Join-Path $projectDirectory 'THIRD-PARTY-NOTICES.md') -Destination $installedDirectory -Force
Copy-Item -LiteralPath (Join-Path $projectDirectory 'licenses') -Destination $installedDirectory -Recurse -Force
# Remove only our obsolete binaries; locked files belong to the still-running app.
Get-ChildItem -LiteralPath $installedDirectory -Filter 'parallexity.running-*.exe' | ForEach-Object {
    try { Remove-Item -LiteralPath $_.FullName -ErrorAction Stop }
    catch [System.IO.IOException] { }
    catch [System.UnauthorizedAccessException] { }
}
Write-Host "Ready: $installedExe"
Write-Host 'Restart Phone Remote and refresh the phone page to use this build.'
