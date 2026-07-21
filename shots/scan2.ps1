param([string]$F="d:\project\wingezi\shots\stable1.png")
Add-Type -AssemblyName System.Drawing
$b=[System.Drawing.Image]::FromFile($F)
("size {0}x{1}" -f $b.Width,$b.Height)
$out=@()
for($y=26;$y -lt $b.Height-24;$y+=2){$c=$b.GetPixel(120,$y);if(($c.R+$c.G+$c.B)/3 -gt 75){$out+=$y}}
("bright at x=120: {0}" -f ($out -join ","))
$o2=@()
for($y=26;$y -lt $b.Height-24;$y+=2){$c=$b.GetPixel(60,$y);if(($c.R+$c.G+$c.B)/3 -gt 60){$o2+=$y}}
("bright at x=60: {0}" -f ($o2 -join ","))
$b.Dispose()
