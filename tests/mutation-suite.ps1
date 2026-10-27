# Runs every lab mutant (one sync rule turned off) and checks that the check it targets FAILS while the run's
# earlier checks still pass. Results go to private\results\mutant-<name>.txt; prints one line per mutant.
param([string[]]$Only = @())
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root 'private\results'
New-Item -ItemType Directory -Force $results | Out-Null
# mutant(s), step to stop after, check that must fail
$runs = @(
    # needs a slot 7 the game has saved into since the mod wrote it (any earlier full run leaves one)
    @{ Mutant = 'slot-reclaim'; StopAfter = 'boat'; Target = 'save'; Timeout = 420 },
    @{ Mutant = 'connect'; StopAfter = 'boat'; Target = 'connect'; Timeout = 420 },
    @{ Mutant = 'save'; StopAfter = 'boat'; Target = 'save'; Timeout = 420 },
    @{ Mutant = 'puppet,day,layout,fish-request'; StopAfter = 'fish'; Target = 'puppet,day,sea-layout,fish-client-catch'; Timeout = 1500 },
    @{ Mutant = 'dive-follow'; StopAfter = 'dive'; Target = 'dive'; Timeout = 900 },
    @{ Mutant = 'dive-exit'; StopAfter = 'dive-exit'; Target = 'dive-exit'; Timeout = 900 },
    @{ Mutant = 'dialogue-spectator'; StopAfter = 'story'; Target = 'dialogue'; Timeout = 1200 },
    @{ Mutant = 'party-wipe,story'; StopAfter = 'death'; Target = 'death,story'; Timeout = 1500 },
    @{ Mutant = 'travel'; StopAfter = 'sushi-travel'; Target = 'sushi-travel'; Timeout = 1800 },
    @{ Mutant = 'sushi'; StopAfter = 'sushi'; Target = 'mainsushi'; Timeout = 1800 },
    @{ Mutant = 'short-timeout'; StopAfter = 'boat-after'; Target = 'stall'; Timeout = 1800 }
)
foreach ($run in $runs) {
    if ($Only.Count -gt 0 -and $Only -notcontains $run.Mutant) { continue }
    $out = & (Join-Path $PSScriptRoot 'lab-suite.ps1') -Mutant $run.Mutant -StopAfter $run.StopAfter -TimeoutSeconds $run.Timeout 2>&1 | Out-String -Width 300
    $name = $run.Mutant -replace ',', '+'
    Set-Content (Join-Path $results "mutant-$name.txt") $out
    $caught = @()
    $missed = @()
    foreach ($target in $run.Target -split ',') {
        if ($out -match "(?m)^FAIL $([regex]::Escape($target)):") { $caught += $target } else { $missed += $target }
    }
    $status = if ($missed.Count -eq 0) { 'CAUGHT' } else { 'MISSED' }
    Write-Output ("{0} mutant {1}: failed as required [{2}]{3}" -f $status, $run.Mutant, ($caught -join ','), $(if ($missed) { "; still passing [$($missed -join ',')]" } else { '' }))
}
