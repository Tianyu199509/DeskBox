param([string]$Dir="d:\project\wingezi\shots",[string]$Set="s2",[int]$N=10)
Add-Type -AssemblyName System.Drawing
for($f=0;$f -lt $N;$f++){
$p="$Dir\$Set$f.png";if(-not (Test-Path $p)){continue}
$b=[System.Drawing.Image]::FromFile($p)
$res=@()
for($band=0;$band -lt 13;$band++){$y0=122+$band*24;$c=0;for($y=$y0;$y -le $y0+16;$y+=2){for($x=112;$x -le 648;$x+=4){$px=$b.GetPixel($x,$y);if(($px.R+$px.G+$px.B)/3 -gt 70){$c++}}};$res+=$c}
"$Set$f " + ($res -join ",")
$b.Dispose()
}
