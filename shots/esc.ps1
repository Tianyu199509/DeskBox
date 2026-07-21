Add-Type @"
using System;
using System.Runtime.InteropServices;
public class K {
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
}
"@
$fg = [K]::GetForegroundWindow()
$p = 0
[void][K]::GetWindowThreadProcessId($fg,[ref]$p)
Write-Host ("FOREGROUND hwnd={0} pid={1}" -f $fg,$p)
[K]::keybd_event(0x1B,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 50
[K]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 800
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class V {
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
"@
$hwnd = [IntPtr]35392658
$sb = New-Object Text.StringBuilder 256
[void][V]::GetWindowText($hwnd,$sb,256)
Write-Host ("POPUP hwnd=35392658 title='{0}' visible={1}" -f $sb.ToString(),([V]::IsWindowVisible($hwnd)))
