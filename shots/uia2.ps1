[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hwnd = [IntPtr]35392658
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$out = New-Object System.Collections.ArrayList
$null = $out.Add(("ROOT '{0}' {1}" -f $root.Current.Name, $root.Current.BoundingRectangle))
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$stack = New-Object System.Collections.Stack
$stack.Push($root)
$seen = 0
while ($stack.Count -gt 0 -and $seen -lt 8000) {
  $el = $stack.Pop()
  $seen++
  $cur = $el.Current
  $r = $cur.BoundingRectangle
  if ($r.Width -gt 0 -and $r.Height -gt 0) {
    $null = $out.Add(("{0}|{1}|{2}|{3},{4},{5},{6}" -f $cur.ControlType.ProgrammaticName, $cur.ClassName, $cur.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height))
  }
  $child = $walker.GetFirstChild($el)
  while ($child -ne $null) {
    $stack.Push($child)
    $child = $walker.GetNextSibling($child)
  }
}
$out | Set-Content -Path "d:\project\wingezi\shots\uia_dump.txt" -Encoding UTF8
Write-Host ("elements={0}" -f $out.Count)
