param(
    [string]$Version = 'dev'
)

$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $PSScriptRoot
$sourceFile = Join-Path $PSScriptRoot 'CodexStatusBridge.cs'
$uiFile = Join-Path $PSScriptRoot 'StatusForm.cs'
$integrationFile = Join-Path $PSScriptRoot 'IntegrationManager.cs'
$iconFile = Join-Path $projectDir 'assets\app-icon.ico'
$publishDir = Join-Path $PSScriptRoot 'publish'
$outputFile = Join-Path $publishDir 'CodexStatusBridge.exe'
$oneClickFile = Join-Path $projectDir 'CodexStatusLight-OneClick.exe'
$portableFile = Join-Path $projectDir 'CodexStatusLight-portable.zip'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

$normalizedVersion = $Version.Trim()
if ($normalizedVersion.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}
if ($normalizedVersion -eq 'dev') {
    $assemblyVersion = '0.0.0.0'
}
elseif ($normalizedVersion -match '^\d+\.\d+\.\d+$') {
    $assemblyVersion = $normalizedVersion + '.0'
}
else {
    throw "Version must be 'dev' or use the X.Y.Z format: $Version"
}

if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}
if (-not (Test-Path -LiteralPath $iconFile)) {
    throw "Application icon was not found: $iconFile"
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$versionSourceFile = Join-Path $publishDir 'VersionInfo.g.cs'
@"
using System.Reflection;

[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$normalizedVersion")]
"@ | Set-Content -LiteralPath $versionSourceFile -Encoding UTF8

& $compiler /nologo /optimize+ /target:winexe /platform:anycpu `
    /win32icon:$iconFile `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /out:$outputFile `
    $sourceFile `
    $uiFile `
    $integrationFile `
    $versionSourceFile

if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}

$selfTestFile = Join-Path $publishDir 'self-test.txt'
Remove-Item -LiteralPath $selfTestFile -Force -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $outputFile -ArgumentList @('--self-test', ('"' + $selfTestFile + '"')) -Wait -PassThru
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $selfTestFile)) {
    throw 'Bridge self-test did not produce a result.'
}
$selfTest = Get-Content -Raw -LiteralPath $selfTestFile
if (-not $selfTest.StartsWith('PASS')) {
    throw "Bridge self-test failed: $selfTest"
}

$exitTestFile = Join-Path $publishDir 'exit-self-test.txt'
Remove-Item -LiteralPath $exitTestFile -Force -ErrorAction SilentlyContinue
$exitProcess = Start-Process -FilePath $outputFile -ArgumentList @('--exit-self-test', ('"' + $exitTestFile + '"')) -Wait -PassThru
if ($exitProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $exitTestFile)) {
    throw 'Bridge exit self-test did not produce a result.'
}
$exitSelfTest = Get-Content -Raw -LiteralPath $exitTestFile
if (-not $exitSelfTest.StartsWith('PASS')) {
    throw "Bridge exit self-test failed: $exitSelfTest"
}

Copy-Item -LiteralPath $outputFile -Destination $oneClickFile -Force

$packageStage = Join-Path $publishDir 'portable-package'
if (Test-Path -LiteralPath $packageStage) {
    Remove-Item -LiteralPath $packageStage -Recurse -Force
}
New-Item -ItemType Directory -Path $packageStage | Out-Null
Copy-Item -LiteralPath $outputFile -Destination (Join-Path $packageStage 'CodexStatusBridge.exe')
foreach ($file in Get-ChildItem -File -LiteralPath (Join-Path $projectDir 'portable')) {
    Copy-Item -LiteralPath $file.FullName -Destination $packageStage
}
Compress-Archive -Path (Join-Path $packageStage '*') -DestinationPath $portableFile -Force
Remove-Item -LiteralPath $packageStage -Recurse -Force

Write-Host "Build succeeded: $outputFile"
Write-Host "One-click package: $oneClickFile"
Write-Host "Portable package: $portableFile"
Write-Host "Self-test: $($selfTest.Trim())"
Write-Host "Exit self-test: $($exitSelfTest.Trim())"
Write-Host "Software version: $normalizedVersion"
