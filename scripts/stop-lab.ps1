# Stops only the game copies launch-lab.ps1 started (private\lab-pids.txt), never a user-owned game process.
$modRoot = Split-Path $PSScriptRoot -Parent
$pids = Join-Path $modRoot 'private/lab-pids.txt'
if (-not (Test-Path $pids)) { return }
foreach ($id in Get-Content $pids) {
    if (-not $id) { continue }
    $p = Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
    if ($p -and $p.Path -and $p.Path -like (Join-Path $modRoot 'private\lab-*')) { Stop-Process -Id $p.Id -Force }
}
Remove-Item $pids -ErrorAction SilentlyContinue
Write-Output 'stopped lab processes'
