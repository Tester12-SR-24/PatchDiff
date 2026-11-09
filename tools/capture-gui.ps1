# Captures real screenshots of the PatchDiff GUI for docs/testing.
# Usage: pwsh tools/capture-gui.ps1 -Exe <path to PatchDiff.exe> -OutDir <dir> -DataDir <isolated data dir>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$DataDir
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Cap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static Dictionary<long, string> WindowsOf(uint pid) {
        var list = new Dictionary<long, string>();
        EnumWindows((h, lp) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                if (sb.Length > 0) list[h.ToInt64()] = sb.ToString();
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

function Save-Shot([IntPtr]$hwnd, [string]$path) {
    [Win32Cap]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 600
    $r = New-Object 'Win32Cap+RECT'
    [Win32Cap]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "bad window rect" }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [Win32Cap]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "saved $path ($w x $h)"
}

$env:PATCHDIFF_DATA_DIR = $DataDir
$proc = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
Start-Sleep -Seconds 14
$proc.Refresh()
$main = $proc.MainWindowHandle
if ($main -eq [IntPtr]::Zero) { throw "no main window - app failed to start" }
Save-Shot $main (Join-Path $OutDir 'dashboard.png')

# --- Assembly Explorer via the Browse button ---
try {
    $ae = [System.Windows.Automation.AutomationElement]::FromHandle($main)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Browse')
    $btn = $ae.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($btn) {
        ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 10
        foreach ($kv in [Win32Cap]::WindowsOf($proc.Id).GetEnumerator()) {
            if ($kv.Value -match 'Explorer') { Save-Shot ([IntPtr]$kv.Key) (Join-Path $OutDir 'explorer.png') }
        }
    } else { Write-Host 'Browse button not found' }
} catch { Write-Host "explorer capture failed: $_" }

# --- Diff window via the Open diff button ---
try {
    $ae2 = [System.Windows.Automation.AutomationElement]::FromHandle($main)
    $cond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Open diff')
    $btn2 = $ae2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond2)
    if ($btn2) {
        ($btn2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 6
        foreach ($kv in [Win32Cap]::WindowsOf($proc.Id).GetEnumerator()) {
            if ($kv.Value -match '\u2192' -or $kv.Value -match 'modified|added|removed') {
                Save-Shot ([IntPtr]$kv.Key) (Join-Path $OutDir 'diff.png')
            }
        }
    } else { Write-Host 'Open diff button not found' }
} catch { Write-Host "diff capture failed: $_" }

try {
    $proc.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 3
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
} catch {}
Write-Host DONE
