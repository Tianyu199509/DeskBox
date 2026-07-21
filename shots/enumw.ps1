Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W {
  public delegate bool EnumDelegate(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumDelegate enumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$target = (Get-Process -Name DeskBox -ErrorAction SilentlyContinue).Id
Write-Host "DeskBox PID(s): $target"
$cb = [W+EnumDelegate]{
  param($h,$l)
  $pid2 = 0
  [void][W]::GetWindowThreadProcessId($h,[ref]$pid2)
  if ($target -contains $pid2) {
    $sb = New-Object Text.StringBuilder 256
    [void][W]::GetWindowText($h,$sb,256)
    $r = New-Object W+RECT
    [void][W]::GetWindowRect($h,[ref]$r)
    Write-Host ("hWnd={0} vis={1} title='{2}' rect={3},{4},{5},{6}" -f $h,([W]::IsWindowVisible($h)),$sb.ToString(),$r.L,$r.T,$r.R,$r.B)
  }
  return $true
}
[void][W]::EnumWindows($cb,[IntPtr]::Zero)
