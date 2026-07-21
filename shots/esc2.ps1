Add-Type @"
using System;
using System.Runtime.InteropServices;
public class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@
$hwnd = [IntPtr]35392658
[void][M]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 200
[void][M]::SetCursorPos(946,299)
Start-Sleep -Milliseconds 100
[M]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[M]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 400
[M]::keybd_event(0x1B,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 50
[M]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 800
Write-Host ("POPUP visible={0}" -f ([M]::IsWindowVisible($hwnd)))
