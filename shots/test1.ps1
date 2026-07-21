param([string]$Dir="d:\project\wingezi\shots",[string]$Mode="full")
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path "$Dir\w.cs"
$pidv=[uint32](Get-Process DeskBox|Select-Object -First 1).Id
"deskbox pid=$pidv"
function Shot($pf,$x,$y,$w,$h){$b=New-Object System.Drawing.Bitmap $w,$h;$g=[System.Drawing.Graphics]::FromImage($b);$g.CopyFromScreen($x,$y,0,0,(New-Object System.Drawing.Size($w,$h)));$g.Dispose();$b.Save("$Dir\$pf.png",[System.Drawing.Imaging.ImageFormat]::Png);$b.Dispose()}
function Burst($pf,$x,$y,$w,$h,$n,$ms){$sw=[Diagnostics.Stopwatch]::StartNew();for($i=0;$i -lt $n;$i++){$t=$i*$ms;$c=$sw.ElapsedMilliseconds;if($t -gt $c){Start-Sleep -Milliseconds ($t-$c)};Shot "$pf$i" $x $y $w $h;"$pf$i t=$($sw.ElapsedMilliseconds)ms"}}
function PopupRect(){foreach($s in [W]::Scan($pidv)){if($s -match "^True\|(-?\d+),(-?\d+),(\d+)x(\d+)$" -and [int]$Matches[3] -lt 800 -and [int]$Matches[4] -lt 600){return $Matches[1..4]}};return $null}
$RX=580;$RY=200;$RW=760;$RH=580
if($Mode -eq "full"){
[System.Windows.Forms.SendKeys]::SendWait("%d")
Burst "e2" $RX $RY $RW $RH 10 60
Start-Sleep -Milliseconds 850
$r=PopupRect
if($r){"popup: x=$($r[0]) y=$($r[1]) w=$($r[2]) h=$($r[3])";Shot "stable1" ([int]$r[0]-24) ([int]$r[1]-24) ([int]$r[2]+48) ([int]$r[3]+48)}else{"NO POPUP RECT";[W]::Scan($pidv)|ForEach-Object{"w: $_"};Shot "stable1" $RX $RY $RW $RH}
"stable1 saved"
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Burst "x2" $RX $RY $RW $RH 8 50
Start-Sleep -Milliseconds 300
"after esc:";[W]::Scan($pidv)|ForEach-Object{"w: $_"}
}
if($Mode -eq "search"){
[System.Windows.Forms.SendKeys]::SendWait("%d")
Start-Sleep -Milliseconds 1400
[W]::keybd_event(0,97,4,[UIntPtr]::Zero);[W]::keybd_event(0,97,6,[UIntPtr]::Zero)
Burst "s2" $RX $RY $RW $RH 10 70
Start-Sleep -Milliseconds 800
$r=PopupRect
if($r){"popup: x=$($r[0]) y=$($r[1]) w=$($r[2]) h=$($r[3])";Shot "stable2" ([int]$r[0]-24) ([int]$r[1]-24) ([int]$r[2]+48) ([int]$r[3]+48)}else{"NO POPUP RECT";[W]::Scan($pidv)|ForEach-Object{"w: $_"};Shot "stable2" $RX $RY $RW $RH}
"stable2 saved"
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Burst "x3" $RX $RY $RW $RH 5 60
"after esc:";[W]::Scan($pidv)|ForEach-Object{"w: $_"}
}
