[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory = '',
    [switch]$CaptureSmoke,
    [switch]$GuiSmoke,
    [switch]$CodexSmoke
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this script with PowerShell on Windows.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot "publish\$Runtime" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$appProject = Join-Path $PSScriptRoot 'Simbi.Windows\Simbi.Windows.csproj'
$testProject = Join-Path $PSScriptRoot 'Simbi.Windows.Tests\Simbi.Windows.Tests.csproj'
Push-Location $PSScriptRoot
try {
& dotnet build $testProject -c $Configuration --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
$testExecutable = Join-Path $PSScriptRoot "Simbi.Windows.Tests\bin\$Configuration\net10.0-windows\Simbi.Windows.Tests.exe"
$testArguments = @()
if ($CaptureSmoke) { $testArguments += '--capture' }
if ($GuiSmoke) { $testArguments += '--ui' }
if ($CodexSmoke) { $testArguments += '--codex' }
& $testExecutable @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed.' }
& dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true --nologo `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -p:RestoreLockedMode=true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.txt')
# Carry the bundled runtime's own license and third-party notices with the artifact.
$packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
foreach ($runtimePackage in @("microsoft.netcore.app.runtime.$Runtime", "microsoft.windowsdesktop.app.runtime.$Runtime")) {
    $runtimeRoot = Join-Path $packageCache $runtimePackage
    $runtimeVersion = Get-ChildItem -LiteralPath $runtimeRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if ($runtimeVersion) {
        foreach ($licenseFile in @('LICENSE', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.TXT')) {
            $licensePath = Join-Path $runtimeVersion.FullName $licenseFile
            if (Test-Path -LiteralPath $licensePath) { Copy-Item -LiteralPath $licensePath -Destination (Join-Path $OutputDirectory "$runtimePackage-$licenseFile") }
        }
    }
}
Write-Output "Windows app: $(Join-Path $OutputDirectory 'Simbi.Windows.exe')"
}
finally { Pop-Location }
