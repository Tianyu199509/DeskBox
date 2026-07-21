param([string]$Dir="d:\project\wingezi\shots",[string]$Set="e2",[int]$N=10)
Add-Type -AssemblyName System.Drawing
$zones=@(("SB",74,96),("R1",148,162),("R2",200,214),("R3",244,258),("R4",292,306),("R5",340,354))
for($f=0;$f -lt $N;$f++){
$p="$Dir\$Set$f.png";if(-not (Test-Path $p)){continue}
$b=[System.Drawing.Image]::FromFile($p)
$res=@()
foreach($z in $zones){$c=0;for($y=$z[1];$y -le $z[2];$y+=2){for($x=112;$x -le 648;$x+=4){$px=$b.GetPixel($x,$y);if(($px.R+$px.G+$px.B)/3 -gt 70){$c++}}};$res+=("$($z[0])=$c")}
"$Set$f " + ($res -join " ")
$b.Dispose()
}
