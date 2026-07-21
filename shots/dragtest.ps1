param(
    [int]$StartX = -560,
    [int]$StartY = 152,
    [int]$StepX = 6,
    [int]$StepY = 5,
    [int]$Steps = 20
)
# Simulates a native left-button drag from the search popup's title area to verify
# the WM_NCHITTEST HTCAPTION drag-move path.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class M {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
'@
[M]::SetCursorPos($StartX, $StartY)
Start-Sleep -Milliseconds 200
[M]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)   # left down
Start-Sleep -Milliseconds 150
for ($i = 1; $i -le $Steps; $i++) {
    [M]::SetCursorPos($StartX + $i * $StepX, $StartY + $i * $StepY)
    Start-Sleep -Milliseconds 20
}
Start-Sleep -Milliseconds 150
[M]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)   # left up
Start-Sleep -Milliseconds 500
'drag done'
