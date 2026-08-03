param(
    [switch]$Uninstall,
    [switch]$NoStartup,
    [switch]$NonInteractive,
    [ValidateSet('Codex', 'Cursor')]
    [string]$Platform
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'CodexStatusLight'
$installedExe = Join-Path $installDir 'CodexStatusBridge.exe'
$settingsPath = Join-Path $installDir 'settings.json'

# The ZIP puts the EXE beside this script. In the source tree it is under
# windows\publish, so support both layouts.
$sourceExe = Join-Path $PSScriptRoot 'CodexStatusBridge.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    $sourceExe = Join-Path (Split-Path -Parent $PSScriptRoot) 'windows\publish\CodexStatusBridge.exe'
}

function Invoke-BridgeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Bridge command failed with exit code $($process.ExitCode): $($Arguments -join ' ')"
    }
}

function Stop-InstalledBridge {
    $installedFullPath = [System.IO.Path]::GetFullPath($installedExe)
    foreach ($process in Get-Process -Name 'CodexStatusBridge' -ErrorAction SilentlyContinue) {
        $processPath = $null
        try { $processPath = $process.Path } catch { }
        if ($processPath -and
            [string]::Equals(
                [System.IO.Path]::GetFullPath($processPath),
                $installedFullPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        $stillRunning = $false
        foreach ($process in Get-Process -Name 'CodexStatusBridge' -ErrorAction SilentlyContinue) {
            $processPath = $null
            try { $processPath = $process.Path } catch { }
            if ($processPath -and
                [string]::Equals(
                    [System.IO.Path]::GetFullPath($processPath),
                    $installedFullPath,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $stillRunning = $true
                break
            }
        }
        if ($stillRunning) { Start-Sleep -Milliseconds 100 }
    } while ($stillRunning -and [DateTime]::UtcNow -lt $deadline)

    if ($stillRunning) {
        throw 'The installed bridge is still running and could not be replaced.'
    }
}

function Get-SelectedPlatform {
    if ($Platform) { return $Platform }
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $settings = Get-Content -Raw -Encoding UTF8 -LiteralPath $settingsPath |
                ConvertFrom-Json
            if ($settings.Platform -in @('Codex', 'Cursor')) {
                return [string]$settings.Platform
            }
        } catch {
            Write-Warning 'Existing platform setting could not be read; Codex will be used.'
        }
    }
    return 'Codex'
}

if ($Uninstall) {
    $managerExe = if (Test-Path -LiteralPath $installedExe) {
        $installedExe
    } elseif (Test-Path -LiteralPath $sourceExe) {
        $sourceExe
    } else {
        $null
    }

    if ($managerExe) {
        Invoke-BridgeCommand -Executable $managerExe -Arguments @('--remove-integrations')
        Invoke-BridgeCommand -Executable $managerExe -Arguments @('--uninstall-startup')
    } else {
        Write-Warning 'Bridge executable was not found, so hook and startup cleanup could not run.'
    }

    Stop-InstalledBridge
    if (Test-Path -LiteralPath $installedExe) {
        Remove-Item -LiteralPath $installedExe -Force
    }

    Write-Host 'AI Status Light was uninstalled.'
    Write-Host 'Its Codex and Cursor hooks were removed; unrelated hooks were preserved.'
    Write-Host 'Restart Codex or Cursor if it is currently open.'
    if (-not $NonInteractive) { Read-Host 'Press Enter to close' }
    exit 0
}

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Bridge executable was not found: $sourceExe"
}

$selectedPlatform = Get-SelectedPlatform
Stop-InstalledBridge
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
if (-not [string]::Equals(
    [System.IO.Path]::GetFullPath($sourceExe),
    [System.IO.Path]::GetFullPath($installedExe),
    [System.StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
}

Invoke-BridgeCommand -Executable $installedExe `
    -Arguments @('--configure-platform', $selectedPlatform)
$startupArgument = if ($NoStartup) { '--uninstall-startup' } else { '--install-startup' }
Invoke-BridgeCommand -Executable $installedExe `
    -Arguments @($startupArgument)

Start-Process -FilePath $installedExe
Write-Host "Installed: $installedExe"
Write-Host "Selected platform: $selectedPlatform"
Write-Host 'Open the control panel to switch between Codex and Cursor at any time.'
Write-Host "Restart $selectedPlatform completely to load the hooks."
if (-not $NonInteractive) { Read-Host 'Press Enter to close' }
