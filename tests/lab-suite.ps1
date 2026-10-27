# Runs the two-copy lab tour (host + client, real game clients on this PC) and checks what each copy logged.
# Prints one line per check and "RESULT: PASS" or "RESULT: FAIL". -Mutant <name> turns one sync rule off in both
# copies; a mutant run must FAIL the check it targets (scripts\launch-lab.ps1 keeps windows minimized, no input).
#   tests\lab-suite.ps1                      LAN, all checks
#   tests\lab-suite.ps1 -Steam               Steam path through SteamMock (lobby, owner lookup, session accept)
#   tests\lab-suite.ps1 -Mutant party-wipe   must fail "death"
#   tests\lab-suite.ps1 -Chaos               120 ms lag + 5% loss between the copies (tests\chaos-proxy.py)
param([switch]$Steam, [string]$Mutant = '', [string]$StopAfter = '', [string]$HostExtra = '', [int]$TimeoutSeconds = 900, [switch]$Chaos, [switch]$KeepRunning)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scripts = Join-Path $root 'scripts'
$logs = @{ host = Join-Path $root 'private\lab-host\BepInEx\LogOutput.log'; client = Join-Path $root 'private\lab-client\BepInEx\LogOutput.log' }
& (Join-Path $scripts 'stop-lab.ps1') | Out-Null
foreach ($log in $logs.Values) { Remove-Item $log -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $root 'private\steam-mock') -Recurse -Force -ErrorAction SilentlyContinue

$extra = if ($Mutant) { " -dtmp-mutant $Mutant" } else { '' }
if ($StopAfter) { $extra += " -dtmp-stop-after $StopAfter" }
$hostExtra = if ($HostExtra) { " $HostExtra" } else { '' }
$port = 27877
$chaosProc = $null
if ($Chaos) {
    # The client talks to the proxy on 27878; the proxy forwards to the host on 27877 with lag and loss both ways.
    $chaosProc = Start-Process python -ArgumentList "-I `"$PSScriptRoot\chaos-proxy.py`" 27878 127.0.0.1 $port --lag-ms 120 --loss 0.05" -PassThru -WindowStyle Hidden
    $joinPort = 27878
} else { $joinPort = $port }
if ($Steam) {
    $hostArgs = "-dtmp-host steam -dtmp-steam-mock 1 -dtmp-tour features$extra$hostExtra"
    $clientArgs = "-dtmp-join steam -dtmp-steam-mock 2 -dtmp-tour features$extra"
} else {
    $hostArgs = "-dtmp-host lan -dtmp-port $port -dtmp-tour features$extra$hostExtra"
    $clientArgs = "-dtmp-join 127.0.0.1 -dtmp-port $joinPort -dtmp-tour features$extra"
}
& (Join-Path $scripts 'launch-lab.ps1') -Names host, client -HostArgs $hostArgs -ClientArgs $clientArgs | Out-Null
$pids = Get-Content (Join-Path $root 'private\lab-pids.txt')

function Lines($role) { if (Test-Path $logs[$role]) { Get-Content $logs[$role] -ErrorAction SilentlyContinue } else { @() } }
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$crashed = ''
while ((Get-Date) -lt $deadline) {
    Start-Sleep 5
    $doneHost = (Lines 'host') -match 'LAB host tour-done'
    $doneClient = (Lines 'client') -match 'LAB client tour-done'
    if ($doneHost -and $doneClient) { break }
    foreach ($id in $pids) { if (-not (Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue)) { $crashed = "game process $id exited" } }
    if ($crashed) { break }
}
if (-not $KeepRunning) { & (Join-Path $scripts 'stop-lab.ps1') | Out-Null }
if ($chaosProc) { Stop-Process -Id $chaosProc.Id -Force -ErrorAction SilentlyContinue }

$h = Lines 'host'; $c = Lines 'client'
function Fact($lines, $role, $key) { $lines | Where-Object { $_ -match "LAB $role FACT $key\b" } }
function Value($line, $name) { if ($line -match "\b$name=([^ ]*)") { $Matches[1] } else { $null } }
$results = [ordered]@{}
function Check($name, $ok, $evidence) { $results[$name] = @($ok, $evidence) }

# connect + save
$hc = $h -match 'LAB host connected'; $cc = $c -match 'LAB client connected'
Check 'connect' ([bool]($hc -and $cc)) "host:$([bool]$hc) client:$([bool]$cc) steam=$([bool]($c -match 'steam=True'))"
function LastMatch($lines, $pattern) { $m = $lines | Select-String $pattern | Select-Object -Last 1; if ($m) { $m.Matches[0].Groups[1].Value } else { '' } }
$hfp = LastMatch $h 'MP save sync: prepared .* fp=(\w+)'
$cfp = LastMatch $c 'MP save sync: client .*loaded=True; fp=(\w+)'
$reject = LastMatch $c 'LAB client save-status (.*(mismatch|invalid|refus).*)'
Check 'save' ([bool]($hfp -and $hfp -eq $cfp)) "host fp=$hfp client fp=$cfp$(if ($reject) { " (client: $reject)" })"

# puppet: the client's puppet ends within 0.3 of where the host moved
$moved = Fact $h 'host' 'host-moved' | Select-Object -First 1
$last = Fact $c 'client' 'puppet' | Select-Object -Last 1
if ($moved -and $last) {
    $dx = [double](Value $moved 'x') - [double](Value $last 'x'); $dy = [double](Value $moved 'y') - [double](Value $last 'y')
    $dist = [Math]::Sqrt($dx * $dx + $dy * $dy)
    Check 'puppet' ($dist -lt 0.3) ("host moved to {0},{1}; client puppet at {2},{3} (d={4:F2})" -f (Value $moved 'x'), (Value $moved 'y'), (Value $last 'x'), (Value $last 'y'), $dist)
} else { Check 'puppet' $false "host-moved=$([bool]$moved) client puppet=$([bool]$last)" }

# dive: both reach the same dive scene
$hd = Value (Fact $h 'host' 'dive-scene' | Select-Object -First 1) 'dive-scene'
$hds = ((Fact $h 'host' 'dive-scene' | Select-Object -First 1) -replace '.*dive-scene ', '')
$cds = ((Fact $c 'client' 'dive-scene' | Select-Object -First 1) -replace '.*dive-scene ', '')
Check 'dive' ([bool]($hds -and $hds -eq $cds -and ($c -match 'client starts native dive'))) "host=$hds client=$cds"

# leaving a dive (2/2 exit consent) and a second dive into the normal shallows
$he = Fact $h 'host' 'exit-boat' | Select-Object -First 1; $ce = Fact $c 'client' 'exit-boat' | Select-Object -First 1
Check 'dive-exit' ([bool]($he -and $ce)) "host back on boat:$([bool]$he) client:$([bool]$ce)"
$h2 = (Fact $h 'host' 'dive2-scene' | Select-Object -First 1) -replace '.*dive2-scene ', ''
$c2 = (Fact $c 'client' 'dive2-scene' | Select-Object -First 1) -replace '.*dive2-scene ', ''
Check 'dive-again' ([bool]($h2 -and $h2 -eq $c2)) "host=$h2 client=$c2"

# sea world: both built the dive from the same sub-scenes
$hl = Value (Fact $h 'host' 'sea-layout' | Select-Object -Last 1) 'scenes'
$cl = Value (Fact $c 'client' 'sea-layout' | Select-Object -Last 1) 'scenes'
Check 'sea-layout' ([bool]($hl -and $hl -eq $cl)) "host=$hl client=$cl"

# fish: same id set; host kill and client hit both dead on both sides
$hf = Value (Fact $h 'host' 'fish-ids' | Select-Object -Last 1) 'ids'
$cf = Value (Fact $c 'client' 'fish-ids' | Select-Object -Last 1) 'ids'
$hact = Value (Fact $h 'host' 'fish-active' | Select-Object -Last 1) 'ids'
$hset = @($hf -split ',' | Where-Object { $_ }); $cset = @($cf -split ',' | Where-Object { $_ }); $aset = @($hact -split ',' | Where-Object { $_ })
$subset = $cset.Count -gt 0 -and -not ($cset | Where-Object { $hset -notcontains $_ })
$nearBound = $aset.Count -gt 0 -and -not ($aset | Where-Object { $cset -notcontains $_ })
Check 'fish-world' ([bool]($subset -and $nearBound)) "host tracks $($hset.Count), client bound $($cset.Count) (all host ids: $subset); host fish active near divers [$hact] bound on client: $nearBound"
$hk = Value (Fact $h 'host' 'fish-host-kill' | Select-Object -Last 1) 'id'
$ck = Value (Fact $c 'client' 'fish-client-hit' | Select-Object -Last 1) 'id'
$hdead = (Fact $h 'host' 'fish-dead' | Select-Object -Last 1); $cdead = (Fact $c 'client' 'fish-dead' | Select-Object -Last 1)
function DeadOn($line, $id) { if (-not $line -or -not $id) { return $false }; $ids = ((Value $line 'ids') -split ',') + ((Value $line 'gone') -split ','); return $ids -contains $id }
Check 'fish-host-kill' ((DeadOn $hdead $hk) -and (DeadOn $cdead $hk)) "id=$hk dead on host=$(DeadOn $hdead $hk) client=$(DeadOn $cdead $hk)"
Check 'fish-client-catch' ((DeadOn $hdead $ck) -and (DeadOn $cdead $ck)) "id=$ck dead on host=$(DeadOn $hdead $ck) client=$(DeadOn $cdead $ck)"

# death: both died, both back on the boat
$ha = Fact $h 'host' 'after-death' | Select-Object -First 1; $ca = Fact $c 'client' 'after-death' | Select-Object -First 1
Check 'death' ([bool]($ha -and $ca -and ($h -match 'Dive: client died'))) "host:$([bool]$ha) client:$([bool]$ca) host saw client death:$([bool]($h -match 'Dive: client died'))"

# a 12 s client stall must not end the session
$st = Fact $c 'client' 'stall-end' | Select-Object -First 1
$drops = @($h + $c | Where-Object { $_ -match 'LAB (host|client) disconnected' })
if ($st) { Check 'stall' ([bool]($drops.Count -eq 0)) "client froze 12 s; disconnects logged: $($drops.Count)" }

# story and day: the client applied the host's last story/day event (manager lanes are ordered by revision)
$hs = (Fact $h 'host' 'sushi-scene' | Select-Object -First 1) -replace '.*sushi-scene ', ''
$cs = (Fact $c 'client' 'sushi-scene' | Select-Object -First 1) -replace '.*sushi-scene ', ''
Check 'sushi-travel' ([bool]($hs -and $hs -eq $cs)) "host=$hs client=$cs"
foreach ($lane in 'Story', 'Day', 'MainSushi', 'SushiMenu', 'SushiTable') {
    $hp = $h | Select-String "Manager trace: stage=publish; lane=$lane; event=([^;]+); revision=(\d+);.*value=(-?\d+)" | Select-Object -Last 1
    $ca = $c | Select-String "Manager trace: stage=apply-ok; lane=$lane; event=([^;]+); revision=(\d+);.*value=(-?\d+)" | Select-Object -Last 1
    if (-not $hp) { if ($lane -in 'Story', 'Day') { Check $lane.ToLower() $false "host published no $lane events" }; continue }
    $hr = $hp.Matches[0].Groups; $cr = if ($ca) { $ca.Matches[0].Groups } else { $null }
    $ok = $cr -and $cr[2].Value -eq $hr[2].Value
    Check $lane.ToLower() ([bool]$ok) ("host last {0} r{1} value={2}; client applied {3}" -f $hr[1].Value, $hr[2].Value, $hr[3].Value, $(if ($cr) { "$($cr[1].Value) r$($cr[2].Value) value=$($cr[3].Value)" } else { 'nothing' }))
}
$hv = Fact $h 'host' 'dialogue-quiet' | Select-Object -First 1; $cv = Fact $c 'client' 'dialogue-quiet' | Select-Object -First 1
if ($hv -or $cv) { Check 'dialogue' ([bool]($hv -and $cv -and ($h -match 'Dialogue vote cast'))) "host: $($hv -replace '.*FACT ', '') | client: $($cv -replace '.*FACT ', '')" }

# sushi bar open on both
$hb = Fact $h 'host' 'sushi-state' | Select-Object -Last 1; $cb = Fact $c 'client' 'sushi-state' | Select-Object -Last 1
if ($hb -or $cb) { Check 'sushi-bar' ([bool]($hb -match 'bar=open' -and $cb -match 'bar=open')) "host: $($hb -replace '.*FACT ', '') | client: $($cb -replace '.*FACT ', '')" }

$errors = @($h + $c | Where-Object { $_ -match 'Update failed|STEP-ERROR' })
$fail = $false
foreach ($k in $results.Keys) { $ok, $ev = $results[$k]; if (-not $ok) { $fail = $true }; Write-Output ("{0} {1}: {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $k, $ev) }
if ($crashed) { Write-Output "FAIL crash: $crashed"; $fail = $true }
Write-Output "errors logged: $($errors.Count)"
Write-Output ("RESULT: " + $(if ($fail) { 'FAIL' } else { 'PASS' }) + $(if ($Mutant) { " (mutant $Mutant)" } else { '' }))
