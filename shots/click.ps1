$src = @'
using System; using System.Runtime.InteropServices;
public static class M {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint dw, IntPtr ex);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, IntPtr ex);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h); }
'@
Add-Type -TypeDefinition $src
$h=[IntPtr]22678852
[M]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 300
[M]::SetCursorPos(946, 299)
[M]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); [M]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 400
[M]::keybd_event(0x1B, 0x01, 0, [IntPtr]::Zero); [M]::keybd_event(0x1B, 0x01, 2, [IntPtr]::Zero)
Start-Sleep -Milliseconds 800; Write-Output ("visAfter=" + [M]::IsWindowVisible($h))
