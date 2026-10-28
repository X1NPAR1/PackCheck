# Records frames of a PackCheck window (by AutomationId) for README GIFs.
# record.ps1 -Dir out -AutomationId Splash -Frames 40 -IntervalMs 60 [-Actions "NavMods,NavProxy"]
param([string]$Dir, [string]$AutomationId = "MainWindow", [int]$Frames = 40, [int]$IntervalMs = 60, [string]$Actions = "")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class R { [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
public struct RECT { public int L, T, Rt, B; } }
"@
[R]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force $Dir | Out-Null
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 200 -and $h -eq [IntPtr]::Zero; $i++) {
  foreach ($p in Get-Process PackCheck -ErrorAction SilentlyContinue) {
    if ($p.MainWindowHandle -ne 0) {
      $e = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
      if ($e.Current.AutomationId -eq $AutomationId) { $h = $p.MainWindowHandle }
    }
  }
  if ($h -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 25 }
}
if ($h -eq [IntPtr]::Zero) { "window not found"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$queue = if ($Actions) { $Actions.Split(",") } else { @() }
$every = if ($queue.Count -gt 0) { [Math]::Max(1, [int]($Frames / ($queue.Count + 1))) } else { 0 }
$a = 0
for ($f = 0; $f -lt $Frames; $f++) {
  if ($every -gt 0 -and $f -gt 0 -and ($f % $every) -eq 0 -and $a -lt $queue.Count) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $queue[$a])
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c); $pat = $null
    if ($el -and $el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() }
    $a++
  }
  $r = New-Object R+RECT; [R]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.Rt - $r.L; $ht = $r.B - $r.T
  if ($w -le 0) { break }
  $bmp = New-Object System.Drawing.Bitmap $w, $ht
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [R]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save((Join-Path $Dir ("{0:D3}.png" -f $f)), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Start-Sleep -Milliseconds $IntervalMs
}
"frames: $f"
