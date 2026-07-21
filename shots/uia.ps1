Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hwnd = [IntPtr]35392658
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
Write-Host ("ROOT: '{0}' rect={1}" -f $root.Current.Name, $root.Current.BoundingRectangle)
$targets = @("新建待办","新建随记","打开设置","最近搜索","最近使用","操作")
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$stack = New-Object System.Collections.Stack
$stack.Push($root)
$seen = 0
while ($stack.Count -gt 0 -and $seen -lt 5000) {
  $el = $stack.Pop()
  $seen++
  $cur = $el.Current
  $name = $cur.Name
  if ($name -and $targets -contains $name) {
    Write-Host ("FOUND name='{0}' ctrl={1} rect={2}" -f $name, $cur.ControlType.ProgrammaticName, $cur.BoundingRectangle)
  }
  $child = $walker.GetFirstChild($el)
  while ($child -ne $null) {
    $stack.Push($child)
    $child = $walker.GetNextSibling($child)
  }
}
Write-Host "DONE"
