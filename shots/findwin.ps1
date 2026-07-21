# Locates the DeskBox search popup window (by process) and prints each top-level
# window's bounds, so drag/resize tests can use exact physical coordinates.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinEnum {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    public struct RECT { public int L, T, R, B; }
}
'@
$deskbox = Get-Process DeskBox -ErrorAction SilentlyContinue
if (-not $deskbox) { 'DeskBox not running'; exit 1 }
$script:targetPid = $deskbox.Id
"pid=$script:targetPid"
$script:found = @()
$cb = {
    param($h, $l)
    [uint32]$windowPid = 0
    [WinEnum]::GetWindowThreadProcessId($h, [ref]$windowPid) | Out-Null
    if ($windowPid -eq $script:targetPid -and [WinEnum]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [WinEnum]::GetWindowText($h, $sb, 256) | Out-Null
        $r = New-Object WinEnum+RECT
        [WinEnum]::GetWindowRect($h, [ref]$r) | Out-Null
        $script:found += "hwnd=$h title='$($sb.ToString())' rect=$($r.L),$($r.T),$($r.R),$($r.B) size=$($r.R-$r.L)x$($r.B-$r.T)"
    }
    return $true
}
[WinEnum]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$script:found
