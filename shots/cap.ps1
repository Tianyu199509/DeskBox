Add-Type -AssemblyName System.Drawing
$src = @'
using System; using System.Runtime.InteropServices;
public static class G {
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
[DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
[DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
[DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
[DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
[DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
Add-Type -TypeDefinition $src
$h = [IntPtr]22678852
$r = New-Object G+RECT
[G]::GetWindowRect($h, [ref]$r) | Out-Null
$x=$r.L; $y=$r.T; $w=$r.R-$r.L; $hh=$r.B-$r.T
Write-Output ("vis=" + [G]::IsWindowVisible($h) + " rect=$x,$y,${w}x$hh")
$sdc = [G]::GetDC([IntPtr]::Zero)
$mdc = [G]::CreateCompatibleDC($sdc)
$bmp = [G]::CreateCompatibleBitmap($sdc, $w, $hh)
$old = [G]::SelectObject($mdc, $bmp)
[G]::BitBlt($mdc,0,0,$w,$hh,$sdc,$x,$y,0x00CC0020) | Out-Null
$img = [System.Drawing.Image]::FromHbitmap($bmp)
$img.Save("d:\project\wingezi\shots\home.png", [System.Drawing.Imaging.ImageFormat]::Png)
$img.Dispose()
[G]::SelectObject($mdc, $old) | Out-Null; [G]::DeleteObject($bmp) | Out-Null; [G]::DeleteDC($mdc) | Out-Null; [G]::ReleaseDC([IntPtr]::Zero, $sdc) | Out-Null
Write-Output "saved"
