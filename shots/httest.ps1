# Sends WM_NCHITTEST probes to the DeskBox search popup to verify the subclass
# hit-testing (HTCAPTION=2, HTCLIENT=1, HTLEFT=10..HTBOTTOMRIGHT=17).
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class HT {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
    public struct RECT { public int L, T, R, B; }
}
'@
$deskbox = Get-Process DeskBox -ErrorAction SilentlyContinue
if (-not $deskbox) { 'DeskBox not running'; exit 1 }
$script:targetPid = $deskbox.Id
$script:popup = [IntPtr]::Zero
$script:rect = New-Object HT+RECT
$cb = {
    param($h, $l)
    [uint32]$windowPid = 0
    [HT]::GetWindowThreadProcessId($h, [ref]$windowPid) | Out-Null
    if ($windowPid -eq $script:targetPid -and [HT]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [HT]::GetWindowText($h, $sb, 256) | Out-Null
        if ($sb.ToString() -eq 'WinUI Desktop') {
            $script:popup = $h
            $r = New-Object HT+RECT
            [HT]::GetWindowRect($h, [ref]$r) | Out-Null
            $script:rect = $r
        }
    }
    return $true
}
[HT]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($script:popup -eq [IntPtr]::Zero) { 'popup not visible'; exit 1 }
$r = $script:rect
"popup hwnd=$($script:popup) rect=$($r.L),$($r.T),$($r.R),$($r.B)"
# Probe points: (dx,dy) offsets from window top-left.
$probes = @(
    @{ n = 'left edge';      x = $r.L + 3;   y = $r.T + 200 },
    @{ n = 'right edge';     x = $r.R - 3;   y = $r.T + 200 },
    @{ n = 'bottom edge';    x = $r.L + 300; y = $r.B - 3 },
    @{ n = 'bottomright';    x = $r.R - 3;   y = $r.B - 3 },
    @{ n = 'title area';     x = $r.L + 30;  y = $r.T + 38 },
    @{ n = 'textbox';        x = $r.L + 300; y = $r.T + 38 },
    @{ n = 'content area';   x = $r.L + 300; y = $r.T + 300 }
)
foreach ($p in $probes) {
    $lp = [IntPtr](($p.y -band 0xFFFF) -shl 16 -bor ($p.x -band 0xFFFF))
    $hit = [HT]::SendMessage($script:popup, 0x0084, [IntPtr]::Zero, $lp)
    "{0,-14} ({1},{2}) -> {3}" -f $p.n, $p.x, $p.y, $hit
}
