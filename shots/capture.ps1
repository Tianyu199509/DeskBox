Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class C {
  public delegate bool EnumDelegate(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumDelegate f, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$pids = (Get-Process -Name DeskBox).Id
$found = [IntPtr]::Zero
$rect = New-Object C+RECT
$cb = [C+EnumDelegate]{
  param($h,$l)
  $p = 0
  [void][C]::GetWindowThreadProcessId($h,[ref]$p)
  if ($pids -contains $p -and [C]::IsWindowVisible($h)) {
    $r = New-Object C+RECT
    [void][C]::GetWindowRect($h,[ref]$r)
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    if ($w -eq 720 -and $ht -eq 540) { $script:found = $h; $script:rect = $r }
  }
  return $true
}
[void][C]::EnumWindows($cb,[IntPtr]::Zero)
if ($found -eq [IntPtr]::Zero) { Write-Host "POPUP_NOT_FOUND"; exit 1 }
$w = $rect.R - $rect.L; $h = $rect.B - $rect.T
Write-Host ("POPUP hwnd={0} rect={1},{2},{3},{4}" -f $found,$rect.L,$rect.T,$rect.R,$rect.B)
$bmp = New-Object System.Drawing.Bitmap $w,$h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [C]::PrintWindow($found,$hdc,2)
$g.ReleaseHdc($hdc)
$g.Dispose()
Write-Host ("PrintWindow ok={0}" -f $ok)
$bmp.Save("d:\project\wingezi\shots\home2.png",[System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$check = [System.Drawing.Image]::FromFile("d:\project\wingezi\shots\home2.png")
Write-Host ("SAVED size={0}x{1}" -f $check.Width,$check.Height)
$check.Dispose()
