# Finds the Dave the Diver install the scripts build against and test with, in this order:
#   1. $env:DTMP_GAME
#   2. steamapps\common\Dave the Diver in every Steam library (libraryfolders.vdf)
# Dot-source it: . (Join-Path $PSScriptRoot 'game-dir.ps1'); $game = Get-DaveDir
function Get-DaveDir {
    $exe = 'DaveTheDiver.exe'
    if ($env:DTMP_GAME) {
        if (Test-Path (Join-Path $env:DTMP_GAME $exe)) { return (Resolve-Path $env:DTMP_GAME).Path }
        throw "DTMP_GAME is $env:DTMP_GAME, but there's no $exe there."
    }
    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $p = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($p -and $p.SteamPath) { $steam = $p.SteamPath; break }
        if ($p -and $p.InstallPath) { $steam = $p.InstallPath; break }
    }
    $libraries = @()
    if ($steam) {
        $libraries += $steam
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libraries += $m.Groups[1].Value -replace '\\\\', '\' }
        }
    }
    $libraries += "${env:ProgramFiles(x86)}\Steam"
    foreach ($lib in $libraries) {
        $dir = Join-Path $lib 'steamapps\common\Dave the Diver'
        if (Test-Path (Join-Path $dir $exe)) { return (Resolve-Path $dir).Path }
    }
    throw "Dave the Diver not found. Set DTMP_GAME to the folder that holds $exe."
}
