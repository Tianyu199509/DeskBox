param([string]$Dir="d:\project\wingezi\shots")
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path "$Dir\w.cs"
$pidv=[uint32](Get-Process DeskBox|Select-Object -First 1).Id
"deskbox pid=$pidv"
function Shot($pf,$x,$y,$w,$h){$b=New-Object System.Drawing.Bitmap $w,$h;$g=[System.Drawing.Graphics]::FromImage($b);$g.CopyFromScreen($x,$y,0,0,(New-Object System.Drawing.Size($w,$h)));$g.Dispose();$b.Save("$Dir\$pf.png",[System.Drawing.Imaging.ImageFormat]::Png);$b.Dispose()}
function Burst($pf,$x,$y,$w,$h,$n,$ms){$sw=[Diagnostics.Stopwatch]::StartNew();for($i=0;$i -lt $n;$i++){$t=$i*$ms;$c=$sw.ElapsedMilliseconds;if($t -gt $c){Start-Sleep -Milliseconds ($t-$c)};Shot "$pf$i" $x $y $w $h;"$pf$i t=$($sw.ElapsedMilliseconds)ms"}}
function PopupRect(){foreach($s in [W]::Scan($pidv)){if($s -match "^True\|(-?\d+),(-?\d+),(\d+)x(\d+)$" -and [int]$Matches[3] -lt 800 -and [int]$Matches[4] -lt 600){return $Matches[1..4]}};return $null}
$RX=580;$RY=200;$RW=760;$RH=580
[System.Windows.Forms.SendKeys]::SendWait("%d")
Start-Sleep -Milliseconds 1400
[W]::keybd_event(0,97,4,[UIntPtr]::Zero);[W]::keybd_event(0,97,6,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 600
Burst "r2" $RX $RY $RW $RH 16 80
Start-Sleep -Milliseconds 500
$r=PopupRect
if($r){"popup: x=$($r[0]) y=$($r[1]) w=$($r[2]) h=$($r[3])";Shot "stable2b" ([int]$r[0]-24) ([int]$r[1]-24) ([int]$r[2]+48) ([int]$r[3]+48)}else{"NO POPUP RECT";Shot "stable2b" $RX $RY $RW $RH}
"stable2b saved"
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Milliseconds 400
"after esc:";[W]::Scan($pidv)|ForEach-Object{"w: $_"}
