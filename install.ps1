#Requires -Version 7.0
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Installs Bifurcate: the background service and the tray app.

.DESCRIPTION
    Publishes both executables, copies them to Program Files, locks down the configuration
    directory, registers the service, and adds shortcuts.

    Inside a release download there is nothing to publish, only a publish folder of prebuilt
    executables next to this script, and that is detected and used as it stands.

    The configuration directory is deliberately admin-writable. The service acts on those values
    for the whole machine, so a standard user who could edit them could aim hardening at any
    adapter. The tray app asks for elevation only when saving settings.

.EXAMPLE
    .\install.ps1

.EXAMPLE
    .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = (Join-Path $env:ProgramFiles 'Bifurcate'),
    [switch] $SkipPublish,
    [switch] $NoStartMenuShortcut,
    [switch] $NoAutoStart,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$dataDirectory = Join-Path $env:ProgramData 'Bifurcate'
$serviceExe = Join-Path $InstallDirectory 'Bifurcate.Service.exe'
$trayExe = Join-Path $InstallDirectory 'Bifurcate.Tray.exe'
$startMenuShortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Bifurcate.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$palette = @{
    Title   = 'Green'
    Step    = 'Cyan'
    Detail  = 'DarkGray'
    Value   = 'Gray'
    Good    = 'Green'
    Warning = 'Yellow'
    Bad     = 'Red'
    Neutral = 'DarkGray'
}

# Every line is a single Write-Host on purpose. Splitting one with -NoNewline loses both the colour
# and the line break as soon as the output is piped or redirected to a log.
function Write-Banner {
    Write-Host ''
    Write-Host '  Bifurcate' -ForegroundColor $palette.Title
    Write-Host '  VPN Privacy and Splitting' -ForegroundColor $palette.Detail
}

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "== $Message" -ForegroundColor $palette.Step
}

# Takes a pipeline so the output of the executables can be dimmed and lined up with everything else.
function Write-Detail {
    param([Parameter(ValueFromPipeline = $true)] [string] $Text)
    process {
        if ($Text.Trim().Length -gt 0) {
            Write-Host ('  ' + $Text.TrimStart()) -ForegroundColor $palette.Detail
        }
    }
}

function Write-Field {
    param([string] $Label, [string] $Value, [string] $Color = $palette.Value)
    Write-Host ('  {0,-9}: {1}' -f $Label, $Value) -ForegroundColor $Color
}

<#
.SYNOPSIS
    Runs the service self-check, colouring its severities so problems stand out.
#>
function Show-Check {
    & $serviceExe --check 2>&1 | ForEach-Object {
        $line = [string] $_
        if ($line -match '^\s*\[(?<severity>\w+)\s*\]') {
            $color = $palette[$Matches.severity]
            if (-not $color) { $color = $palette.Value }
        }
        elseif ($line -match '^\S') {
            $color = $palette.Step
        }
        else {
            $color = $palette.Value
        }

        Write-Host $line -ForegroundColor $color
    }
}

function Stop-TrayApp {
    Get-Process -Name 'Bifurcate.Tray' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Detail "stopping the running tray app (pid $($_.Id))"
        Stop-Process -Id $_.Id -Force
    }
}

function Remove-Installation {
    Write-Step 'Removing Bifurcate'

    Stop-TrayApp

    if (Test-Path $serviceExe) {
        # The service removes its own firewall rules, so nothing is left enforcing anything.
        & $serviceExe --uninstall | Write-Detail
    }
    else {
        sc.exe stop Bifurcate | Out-Null
        sc.exe delete Bifurcate | Out-Null
    }

    Remove-Item $startMenuShortcut -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKey -Name 'Bifurcate' -ErrorAction SilentlyContinue

    if (Test-Path $InstallDirectory) {
        Remove-Item $InstallDirectory -Recurse -Force
        Write-Detail "deleted $InstallDirectory"
    }

    Write-Host ''
    Write-Host 'Removed.' -ForegroundColor $palette.Title
    Write-Detail "settings and logs are left in $dataDirectory, delete that folder to remove them too"
}

function Publish-Projects {
    Write-Step 'Publishing'

    $publishRoot = Join-Path $repoRoot 'publish'
    foreach ($project in 'Bifurcate.Service', 'Bifurcate.Tray') {
        $target = Join-Path $publishRoot $project
        Write-Detail "$project -> $target"

        # Trimming stays off on purpose: it breaks COM interop and the reflection CIM relies on.
        dotnet publish (Join-Path $repoRoot "src\$project") `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:PublishTrimmed=false `
            --output $target `
            --nologo `
            --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }

    return $publishRoot
}

function Copy-Payload {
    param([string] $PublishRoot)

    Write-Step "Installing to $InstallDirectory"

    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
    foreach ($project in 'Bifurcate.Service', 'Bifurcate.Tray') {
        $source = Join-Path $PublishRoot $project
        if (-not (Test-Path $source)) {
            throw "No executables at $source. Either the download is incomplete, or you are in " +
                  'the repository and should run without -SkipPublish so they get built.'
        }

        Copy-Item (Join-Path $source '*') -Destination $InstallDirectory -Recurse -Force
    }

    Get-ChildItem $InstallDirectory -Filter '*.exe' | ForEach-Object {
        Write-Detail ('{0} ({1:N0} MB)' -f $_.Name, ($_.Length / 1MB))
    }
}

function Set-DataDirectory {
    Write-Step "Preparing $dataDirectory"

    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null

    # Everyone can read it, only administrators and the service account can change it.
    $acl = Get-Acl $dataDirectory
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($entry in @(
            @('BUILTIN\Administrators', 'FullControl'),
            @('NT AUTHORITY\SYSTEM', 'FullControl'),
            @('BUILTIN\Users', 'ReadAndExecute'))) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                    $entry[0], $entry[1], 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -Path $dataDirectory -AclObject $acl
    Write-Detail 'administrators and SYSTEM can write, everyone else can read'

    $configPath = Join-Path $dataDirectory 'config.json'
    if (Test-Path $configPath) {
        Write-Detail "keeping the existing $configPath"
        return
    }

    $sample = Join-Path $repoRoot 'config.sample.json'
    if (Test-Path $sample) {
        Copy-Item $sample $configPath
        Write-Detail "seeded $configPath from the sample, the tray app will ask you to fill it in"
    }
}

function Install-Service {
    Write-Step 'Registering the service'
    & $serviceExe --install | Write-Detail
    if ($LASTEXITCODE -ne 0) { throw 'Registering the service failed.' }
}

function Add-Shortcuts {
    Write-Step 'Shortcuts'

    if (-not $NoStartMenuShortcut) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($startMenuShortcut)
        $shortcut.TargetPath = $trayExe
        $shortcut.WorkingDirectory = $InstallDirectory
        $shortcut.Description = 'VPN Privacy and Splitting'
        $shortcut.Save()
        Write-Detail "Start Menu: $startMenuShortcut"
    }

    if (-not $NoAutoStart) {
        # Sets it for the account running this installer. Anyone else can turn it on from the
        # tray menu, which needs no rights because it is their own Run key.
        Set-ItemProperty -Path $runKey -Name 'Bifurcate' -Value "`"$trayExe`" --minimized"
        Write-Detail "opens at sign-in for $env:USERNAME"
    }
}

function Start-TrayApp {
    # Launched through Explorer so it runs as the signed-in user rather than inheriting this
    # elevated prompt. The tray app is meant to be unelevated.
    Write-Step 'Starting the tray app'
    Start-Process explorer.exe -ArgumentList $trayExe
    Write-Detail 'look for the tray icon near the clock'
}

Write-Banner

if ($Uninstall) {
    Remove-Installation
    return
}

# A release download has the executables already built and no sources to build them from.
$prebuilt = $SkipPublish -or -not (Test-Path (Join-Path $repoRoot 'src'))
$publishRoot = if ($prebuilt) { Join-Path $repoRoot 'publish' } else { Publish-Projects }

Stop-TrayApp
if (Get-Service -Name 'Bifurcate' -ErrorAction SilentlyContinue) {
    Write-Step 'Stopping the previous service'

    # Prefer the installed executable, which also withdraws its firewall rules. Falling back to
    # sc.exe covers a service registered against an executable that is no longer there.
    if (Test-Path $serviceExe) {
        & $serviceExe --uninstall | Write-Detail
    }
    else {
        sc.exe stop Bifurcate | Out-Null
        sc.exe delete Bifurcate | Out-Null
    }
}

Copy-Payload -PublishRoot $publishRoot
Set-DataDirectory
Install-Service
Add-Shortcuts

Write-Step 'Checking'
Show-Check

Start-TrayApp

$service = Get-Service Bifurcate

Write-Host ''
Write-Host 'Installed.' -ForegroundColor $palette.Title
Write-Field 'settings' (Join-Path $dataDirectory 'config.json')
Write-Field 'logs' (Join-Path $dataDirectory 'logs')
Write-Field 'service' "Bifurcate ($($service.Status))" `
    ($(if ($service.Status -eq 'Running') { $palette.Good } else { $palette.Warning }))
