# Builds demo recompilations of every snapshot in testdata/ into bin/recompiled/.
#
# The output is git-ignored on purpose: these are builds of copyrighted games
# and must never be committed. What *is* committed is this script, so anyone
# with their own snapshots can reproduce the same layout.
#
#   pwsh -File tools/demo.ps1                 # lift everything that is missing
#   pwsh -File tools/demo.ps1 -Force          # re-lift everything
#   pwsh -File tools/demo.ps1 -Frames 300     # longer headless run per game
#   pwsh -File tools/demo.ps1 -NoRun          # lift only, do not execute
#
# Each game ends up in bin/recompiled/<name>/ with the usual layout:
#
#   <name>.exe              double-click to play
#   <name>.dll              lifted assembly
#   src/Game.generated.cs   generated C#
#   analysis.txt            model, entry point, coverage, disassembly
#   frame.png               screenshot taken by the headless run
#   demo.txt                what the demo run reported

[CmdletBinding()]
param(
    [string] $InputDir = 'testdata',
    [string] $OutputDir = 'bin/recompiled',
    [int]    $Frames = 300,
    [int]    $ScreenshotFrame = 200,
    [switch] $Force,
    [switch] $NoRun,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$exe = Join-Path $repo "bin/$Configuration/net10.0-windows/ZX2ILRecomp.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Building $Configuration first..." -ForegroundColor Cyan
    dotnet build ZX2ILRecomp.csproj -c $Configuration -warnaserror | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}

if (-not (Test-Path $InputDir)) {
    throw "No snapshot folder at '$InputDir'. Put .z80/.sna/.szx/.tap/.tzx files there."
}

# A ROM next to the snapshots is what makes ROM-dependent games work. The
# script does not ship one; it only reports whether it found one.
$rom = Get-ChildItem -Path 'roms', $InputDir -Filter '*.rom' -ErrorAction SilentlyContinue | Select-Object -First 1

if ($rom) {
    Write-Host "ROM found: $($rom.FullName)" -ForegroundColor DarkGray
} else {
    Write-Host "No ROM found; ROM-dependent games will render partially." -ForegroundColor Yellow
}

$snapshots = Get-ChildItem -Path $InputDir -File -Recurse |
    Where-Object { $_.Extension -in '.z80', '.sna', '.szx', '.tap', '.tzx' } |
    Sort-Object Name

if ($snapshots.Count -eq 0) {
    throw "No snapshots in '$InputDir'."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host ""
Write-Host "Demo recompilation" -ForegroundColor Cyan
Write-Host "=================="
Write-Host "Snapshots: $($snapshots.Count)"
Write-Host "Output:    $OutputDir"
Write-Host ""

$results = @()
$clock = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($snapshot in $snapshots) {
    $name = $snapshot.BaseName
    $work = Join-Path $OutputDir $name

    if ((Test-Path (Join-Path $work "$name.dll")) -and -not $Force) {
        Write-Host "  skip   $name (already built, use -Force)" -ForegroundColor DarkGray
        $results += [pscustomobject]@{ Name = $name; Status = 'skipped'; Detail = '' }
        continue
    }

    Write-Host "  lift   $name ..." -ForegroundColor Cyan -NoNewline

    $liftClock = [System.Diagnostics.Stopwatch]::StartNew()

    & $exe $snapshot.FullName -o $OutputDir --fresh --no-tui *> $null
    $liftExit = $LASTEXITCODE
    $liftClock.Stop()

    if ($liftExit -ne 0) {
        Write-Host "`r  FAIL   $name (lift exit $liftExit)" -ForegroundColor Red
        $results += [pscustomobject]@{ Name = $name; Status = 'failed'; Detail = "lift exit $liftExit" }
        continue
    }

    $dll = Join-Path $work "$name.dll"

    if (-not (Test-Path $dll)) {
        Write-Host "`r  FAIL   $name (no assembly)" -ForegroundColor Red
        $results += [pscustomobject]@{ Name = $name; Status = 'failed'; Detail = 'no assembly' }
        continue
    }

    $analysis = Get-Content (Join-Path $work 'analysis.txt') -Raw
    $entry = if ($analysis -match 'entry\s+:\s+(\S+)') { $Matches[1] } else { '?' }
    $instructions = if ($analysis -match 'instructions\s+:\s+(\d+)') { [int]$Matches[1] } else { 0 }

    if ($NoRun) {
        Write-Host "`r  built  $name (entry $entry, $instructions instructions, $($liftClock.Elapsed.TotalSeconds.ToString('0.0'))s)" -ForegroundColor Green
        $results += [pscustomobject]@{ Name = $name; Status = 'built'; Detail = "$instructions instructions" }
        continue
    }

    # Run it headlessly and take a screenshot, which is both a smoke test and
    # the thing that shows the lift actually renders something.
    $frame = Join-Path $work 'frame.png'
    $runClock = [System.Diagnostics.Stopwatch]::StartNew()

    $output = & dotnet $dll --headless $ScreenshotFrame --screenshot $frame 2>&1
    $runExit = $LASTEXITCODE
    $runClock.Stop()

    $output | Set-Content (Join-Path $work 'demo.txt')
    $summary = ($output | Where-Object { $_ -like 'headless:*' } | Select-Object -First 1)

    if ($runExit -ne 0) {
        Write-Host "`r  WARN   $name (ran, exit $runExit)" -ForegroundColor Yellow
        $results += [pscustomobject]@{ Name = $name; Status = 'trap'; Detail = "$summary" }
        continue
    }

    $traps = if ($summary -match 'traps=(\d+)') { [int]$Matches[1] } else { -1 }

    if ($traps -gt 0) {
        Write-Host "`r  WARN   $name (trapped)" -ForegroundColor Yellow
        $results += [pscustomobject]@{ Name = $name; Status = 'trap'; Detail = "$summary" }
        continue
    }

    $total = $liftClock.Elapsed.TotalSeconds + $runClock.Elapsed.TotalSeconds
    Write-Host "`r  ok     $name (entry $entry, $instructions instructions, $($total.ToString('0.0'))s)" -ForegroundColor Green
    $results += [pscustomobject]@{ Name = $name; Status = 'ok'; Detail = "$instructions instructions, entry $entry" }
}

$clock.Stop()

Write-Host ""
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "-------"

$results | Format-Table -AutoSize -Property Name, Status, Detail | Out-Host

$ok = ($results | Where-Object { $_.Status -eq 'ok' }).Count
$built = ($results | Where-Object { $_.Status -in 'ok', 'built' }).Count
$failed = ($results | Where-Object { $_.Status -in 'failed', 'trap' }).Count

Write-Host ""
Write-Host ("{0} playable, {1} built, {2} failed, {3} total in {4:mm\:ss}" -f `
    $ok, $built, $failed, $results.Count, $clock.Elapsed) -ForegroundColor Cyan
Write-Host "Play: $OutputDir\<name>\<name>.exe" -ForegroundColor DarkGray

if ($failed -gt 0) {
    exit 1
}
