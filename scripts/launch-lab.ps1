# Starts isolated lab copies (private\lab-host, private\lab-client) as normal windowed apps, minimized without
# focus (SW_SHOWMINNOACTIVE). Restoring one shows it small (640x360) at the right edge of the primary monitor.
# The placement is set once at launch and never again; no keys or mouse input are sent. Lowest quality, 30 fps cap,
# BelowNormal priority. Test-owned PIDs go to private\lab-pids.txt; stop only those (never a user-owned game).
param(
    [string[]]$Names = @('host', 'client'), [int]$Seconds = 0, [switch]$NoPlugin,
    [string]$HostArgs = '', [string]$ClientArgs = '', [string]$Dll = ''
)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
if (-not $Dll) { $Dll = Join-Path $modRoot 'bin\Release\net6.0\DaveTheDiverMP.dll' }

if (-not ('DaveLab.Win1' -as [type])) { Add-Type -Namespace DaveLab -Name Win1 -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowText(System.IntPtr hWnd, string text);
[StructLayout(LayoutKind.Sequential)] public struct Placement { public int length, flags, showCmd, minX, minY, maxX, maxY, left, top, right, bottom; }
[DllImport("user32.dll")] public static extern bool SetWindowPlacement(System.IntPtr hWnd, ref Placement p);
public static void Park(System.IntPtr hWnd, int x, int y, int w, int h) {
    Placement p = new Placement(); p.length = Marshal.SizeOf(typeof(Placement));
    p.showCmd = 7;   // SW_SHOWMINNOACTIVE: minimized, focus stays where it is
    p.minX = p.minY = p.maxX = p.maxY = -1;
    p.left = x; p.top = y; p.right = x + w; p.bottom = y + h;   // where it comes back when the user restores it
    SetWindowPlacement(hWnd, ref p);
}
'@ }

$missing = $Names | Where-Object { -not (Test-Path (Join-Path $modRoot "private/lab-$_/DaveTheDiver.exe")) }
if ($missing) { & (Join-Path $PSScriptRoot 'make-lab.ps1') -Names $missing }

$logs = Join-Path $modRoot 'private/lab-logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$window = '-screen-fullscreen 0 -screen-width 640 -screen-height 360 -screen-quality Fastest'
Add-Type -AssemblyName System.Windows.Forms
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$slot = 0
$started = @()
foreach ($name in $Names) {
    $dir = Join-Path $modRoot "private/lab-$name"
    $plugin = Join-Path $dir 'BepInEx/plugins/DaveTheDiverMP'
    New-Item -ItemType Directory -Force $plugin | Out-Null
    if ($NoPlugin) { Remove-Item (Join-Path $plugin 'DaveTheDiverMP.dll') -ErrorAction SilentlyContinue }
    else { Copy-Item -Force $Dll $plugin }
    $log = Join-Path $logs "$name.log"
    if (Test-Path $log) { Clear-Content $log -ErrorAction SilentlyContinue }
    $role = if ($name -eq 'host') { $HostArgs } else { $ClientArgs }
    $p = Start-Process -FilePath (Join-Path $dir 'DaveTheDiver.exe') -WorkingDirectory $dir -PassThru -WindowStyle Minimized `
        -ArgumentList "$window -logFile `"$log`" -dtmp-lab $name $role"
    $started += $p
    try { $p.PriorityClass = 'BelowNormal' } catch { }
    # Once, as soon as Unity creates the window: minimized with its restore spot at the right edge, stacked.
    for ($i = 0; $i -lt 600; $i++) {
        $p.Refresh()
        if ($p.HasExited) { break }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
            $y = $area.Top + ($slot * 380) % [Math]::Max(380, $area.Height - 380)
            [DaveLab.Win1]::SetWindowText($p.MainWindowHandle, "Dave lab - $name") | Out-Null
            [DaveLab.Win1]::Park($p.MainWindowHandle, $area.Right - 660, $y, 656, 399)
            $slot++
            break
        }
        Start-Sleep -Milliseconds 100
    }
}
Set-Content (Join-Path $modRoot 'private/lab-pids.txt') (($started | ForEach-Object Id) -join "`n")
Write-Output ("started " + (($started | ForEach-Object { $_.Id }) -join ', '))
if ($Seconds -gt 0) {
    Start-Sleep $Seconds
    & (Join-Path $PSScriptRoot 'stop-lab.ps1')
}
