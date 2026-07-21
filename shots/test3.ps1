param([string]$Dir="d:\project\wingezi\shots")
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path "$Dir\w.cs"
$pidv=[uint32](Get-Process DeskBox|Select-Object -First 1).Id
function Shot($pf,$x,$y,$w,$h){$b=New-Object System.Drawing.Bitmap $w,$h;$g=[System.Drawing.Graphics]::FromImage($b);$g.CopyFromScreen($x,$y,0,0,(New-Object System.Drawing.Size($w,$h)));$g.Dispose();$b.Save("$Dir\$pf.png",[System.Drawing.Imaging.ImageFormat]::Png);$b.Dispose()}
function Burst($pf,$x,$y,$w,$h,$n,$ms){$sw=[Diagnostics.Stopwatch]::StartNew();for($i=0;$i -lt $n;$i++){$t=$i*$ms;$c=$sw.ElapsedMilliseconds;if($t -gt $c){Start-Sleep -Milliseconds ($t-$c)};Shot "$pf$i" $x $y $w $h;"$pf$i t=$($sw.ElapsedMilliseconds)ms"}}
$RX=580;$RY=200;$RW=760;$RH=580
[System.Windows.Forms.SendKeys]::SendWait("%d")
Burst "e3" $RX $RY $RW $RH 14 35
Start-Sleep -Milliseconds 1200
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
"done"
