<#
.SYNOPSIS
    Captures the dashboard and the settings window to raw PNGs, ready for make-docs-shots.ps1.

.DESCRIPTION
    Draws each window with PrintWindow rather than reading the screen, so nothing that happens to be
    in front, or on another monitor, can end up in the image, and the window never has to be brought
    to the foreground.

    The tray app allows a single instance, so any running copy is stopped for the duration and
    started again at the end. The theme preference is set for the capture and put back the same way.

    What lands here still has the real VPN name and addresses in it. Run make-docs-shots.ps1 to
    replace those with placeholders before committing anything.

.EXAMPLE
    .\capture-shots.ps1

.EXAMPLE
    .\capture-shots.ps1 -Theme Dark -Exe .\src\Bifurcate.Tray\bin\Debug\net10.0-windows\Bifurcate.Tray.exe
#>

param(
    [string] $Exe = (Join-Path $env:ProgramFiles 'Bifurcate\Bifurcate.Tray.exe'),
    [string] $OutDir = (Join-Path $env:TEMP 'bifurcate-shots'),
    [ValidateSet('Light', 'Dark')] [string] $Theme = 'Light'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives,
    System.Private.Windows.GdiPlus, System.Private.Windows.Core -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public static class WindowShot
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT bounds);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    // PW_RENDERFULLCONTENT. Without it anything the desktop manager composites comes back blank.
    const uint FullContent = 2;

    public static void Save(IntPtr hWnd, string path)
    {
        RECT bounds;
        GetWindowRect(hWnd, out bounds);

        using (Bitmap bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            PrintWindow(hWnd, hdc, FullContent);
            graphics.ReleaseHdc(hdc);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
'@

if (-not (Test-Path $Exe)) {
    throw "No tray app at $Exe. Install it, build it, or pass -Exe."
}

# Captured at the display scale, which the polishing step then has to account for.
[WindowShot]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function Find-Element {
    param($Root, [string] $Name)

    $Root.FindFirst([System.Windows.Automation.TreeScope]::Subtree,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
}

$preferences = 'HKCU:\Software\Bifurcate'
$previousTheme = (Get-ItemProperty -Path $preferences -Name 'Theme' -ErrorAction SilentlyContinue).Theme

$running = @(Get-Process -Name 'Bifurcate.Tray' -ErrorAction SilentlyContinue)
$running | Stop-Process -Force
Start-Sleep -Seconds 1

New-Item -Path $preferences -Force | Out-Null
Set-ItemProperty -Path $preferences -Name 'Theme' -Value $Theme

$app = Start-Process -FilePath $Exe -PassThru
try {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }

    if ($app.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The dashboard never appeared. If this is a first run, set the app up by hand first.'
    }

    # The first status sweep probes the network, and the dashboard says "Checking..." until it lands.
    Start-Sleep -Seconds 4

    $dashboard = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $dashboardFile = Join-Path $OutDir "dashboard-$Theme.png"
    [WindowShot]::Save($app.MainWindowHandle, $dashboardFile)
    Write-Host "wrote $dashboardFile"

    (Find-Element -Root $dashboard -Name 'Settings').GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 5

    # Owned by the dashboard rather than a window of its own, so the search starts from there.
    $settings = Find-Element -Root $dashboard -Name 'Bifurcate Settings'
    if ($null -eq $settings) {
        throw 'The settings window did not open.'
    }

    $settingsFile = Join-Path $OutDir "settings-$Theme.png"
    [WindowShot]::Save([IntPtr] $settings.Current.NativeWindowHandle, $settingsFile)
    Write-Host "wrote $settingsFile"
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue

    if ($null -eq $previousTheme) {
        Remove-ItemProperty -Path $preferences -Name 'Theme' -ErrorAction SilentlyContinue
    }
    else {
        Set-ItemProperty -Path $preferences -Name 'Theme' -Value $previousTheme
    }

    if ($running.Count -gt 0) {
        Start-Sleep -Seconds 1
        Start-Process -FilePath $Exe | Out-Null
    }
}
