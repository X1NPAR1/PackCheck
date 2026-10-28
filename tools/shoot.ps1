# Drives the running app with UI Automation and saves screenshots: shoot.ps1 -Dir docs/media -Pages NavServers,NavValidate
param([string]$Dir, [string]$Pages, [string]$Prefix = "")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
public struct R { public int L, T, Rt, B; } }
"@
[W]::SetProcessDPIAware() | Out-Null
function Snap($h, $file) {
  [W]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 700
  $r = New-Object W+R; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [W]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
  $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); "saved $file"
}
$p = $null
for ($i = 0; $i -lt 60 -and -not $p; $i++) { $p = Get-Process PackCheck -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and ([System.Windows.Automation.AutomationElement]::FromHandle($_.MainWindowHandle)).Current.AutomationId -eq "MainWindow" } | Select-Object -First 1; Start-Sleep -Milliseconds 250 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
foreach ($page in $Pages.Split(",")) {
  if ($page.Contains('=')) {
    $kv = $page.Split('=', 2)
    $c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $kv[0])
    $tb = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c2)
    $vp = $null; if ($tb -and $tb.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue($kv[1]) } else { "not found: $($kv[0])" }
    continue
  }
  $parts = $page.Split(':'); $id = $parts[0]
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
  if ($el) {
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() }
    elseif ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke() }
  } else { "not found: $id" }
  Start-Sleep -Milliseconds 900
  $name = if ($parts.Length -gt 1) { $parts[1] } else { $id }
  if ($name -ne "-") { Snap $p.MainWindowHandle (Join-Path $Dir "$Prefix$name.png") }
}
