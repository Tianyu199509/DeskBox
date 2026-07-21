param([string]$Dir="d:\project\wingezi\shots",[string]$Set="e2",[int]$N=10)
Add-Type -AssemblyName System.Drawing
for($f=0;$f -lt $N;$f++){
$p="$Dir\$Set$f.png"
if(-not (Test-Path $p)){continue}
$b=[System.Drawing.Image]::FromFile($p)
$sum=0;$cnt=0
for($y=66;$y -lt 430;$y+=8){for($x=110;$x -lt 650;$x+=20){$c=$b.GetPixel($x,$y);$sum+=($c.R+$c.G+$c.B)/3;$cnt++}}
$avg=[math]::Round($sum/$cnt,1)
$slots=@()
for($s=0;$s -lt 7;$s++){$sy=134+$s*44;$rsum=0;$rc=0;for($x=120;$x -lt 640;$x+=10){$c=$b.GetPixel($x,$sy);$rsum+=($c.R+$c.G+$c.B)/3;$rc++};$slots+=[math]::Round($rsum/$rc,0)}
$col=@()
for($y=34;$y -le 106;$y+=6){$c=$b.GetPixel(380,$y);$col+=([math]::Round(($c.R+$c.G+$c.B)/3,0))}
"$Set$f avg=$avg rows=" + ($slots -join ",") + " top=" + ($col -join ",")
$b.Dispose()
}
