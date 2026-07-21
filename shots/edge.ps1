param([string]$File,[int]$M=24)
Add-Type -AssemblyName System.Drawing
$b=[System.Drawing.Image]::FromFile($File)
function Px($x,$y){$c=$b.GetPixel($x,$y);"{0},{1},{2}" -f $c.R,$c.G,$c.B}
"img $($b.Width)x$($b.Height) M=$M"
$xs=0.25,0.5,0.75
"bg_top: "+(($xs|ForEach-Object{Px ([int]($M+$_*($b.Width-2*$M))) ($M-10)}) -join " | ")
foreach($d in 1,2,3,5,8,12,18,26){"top_d${d}: "+(($xs|ForEach-Object{Px ([int]($M+$_*($b.Width-2*$M))) ($M+$d)}) -join " | ")}
foreach($d in 1,2,3,5,8,12,18,26){"left_d${d}: "+(($xs|ForEach-Object{Px ($M+$d) ([int]($M+$_*($b.Height-2*$M)))}) -join " | ")}
"corner_tl: "+((0,2,4,6,8,10,14,20|ForEach-Object{Px ($M+$_) ($M+$_)}) -join " | ")
$b.Dispose()
