param([string]$Keys="",[string]$Text="",[string]$Prefix="d:\project\wingezi\shots\s",[int]$Count=1,[int]$IntervalMs=100,[int]$PreMs=0,[int]$X=-1,[int]$Y=-1,[int]$W=-1,[int]$H=-1)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
if(-not ("Kbd" -as [type])){Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class Kbd{[DllImport("user32.dll")]public static extern void keybd_event(byte bVk,byte bScan,uint dwFlags,UIntPtr dwExtraInfo);}'}
if($Keys -ne ""){[System.Windows.Forms.SendKeys]::SendWait($Keys)}
if($PreMs -gt 0){Start-Sleep -Milliseconds $PreMs}
if($Text -ne ""){foreach($ch in $Text.ToCharArray()){[Kbd]::keybd_event(0,[byte][int]$ch,4,[UIntPtr]::Zero);[Kbd]::keybd_event(0,[byte][int]$ch,6,[UIntPtr]::Zero)}}
$area=[System.Windows.Forms.SystemInformation]::VirtualScreen
if($X -lt 0){$X=$area.X;$Y=$area.Y;$W=$area.Width;$H=$area.Height}
"region=${X},${Y},${W},${H} screen=$($area.Width)x$($area.Height)"
$sw=[System.Diagnostics.Stopwatch]::StartNew()
for($i=0;$i -lt $Count;$i++){
$t=$i*$IntervalMs;$c=$sw.ElapsedMilliseconds;if($t -gt $c){Start-Sleep -Milliseconds ($t-$c)}
$b=New-Object System.Drawing.Bitmap $W,$H
$g=[System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen($X,$Y,0,0,(New-Object System.Drawing.Size($W,$H)))
$g.Dispose()
$p="{0}{1}.png" -f $Prefix,$i
$b.Save($p,[System.Drawing.Imaging.ImageFormat]::Png)
$b.Dispose()
"saved $p t=$($sw.ElapsedMilliseconds)ms"
}
