# Builds demo recompilations of every snapshot in testdata/ into bin/recompiled/.
#
# The output is git-ignored on purpose: these are builds of copyrighted games
# and must never be committed. What *is* committed is this script, so anyone
# with their own snapshots can reproduce the same layout.
#
#   powershell -File tools/demo.ps1                 # lift everything that is missing
#   powershell -File tools/demo.ps1 -Force          # re-lift everything
#   powershell -File tools/demo.ps1 -Frames 300     # longer headless run per game
#   powershell -File tools/demo.ps1 -NoRun          # lift only, do not execute
#
# Each game ends up in bin/recompiled/<name>/ with the usual layout:
#
#   <name>.exe              double-click to play
#   <name>.dll              lifted assembly
#   src/Game.generated.cs   generated C#
#   analysis.txt            model, entry point, coverage, disassembly
#   frame.png               screenshot taken by the headless run
#   demo.txt                what the demo run reported
#   lift.log                the lifter's own output

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

Add-Type -AssemblyName System.Drawing

<#
    Counts distinct colours in a rendered frame, sampling every fourth pixel.

    This is the only honest test of "did the lift work": a game can run for
    hundreds of frames without trapping and still draw nothing, and a loader
    heuristic can be wrong in both directions. The picture cannot.
#>
function Get-FrameColourCount {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return 0
    }

    $image = [System.Drawing.Bitmap]::FromFile($Path)

    try {
        $colours = @{}

        for ($y = 0; $y -lt $image.Height; $y += 4) {
            for ($x = 0; $x -lt $image.Width; $x += 4) {
                $colours[$image.GetPixel($x, $y).ToArgb()] = $true
            }
        }

        return $colours.Count
    }
    finally {
        $image.Dispose()
    }
}

$exe = Join-Path $repo "bin/$Configuration/net10.0-windows/ZX2ILRecomp.exe"

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "Building $Configuration first..." -ForegroundColor Cyan
    dotnet build ZX2ILRecomp.csproj -c $Configuration -warnaserror | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}

# Run from a private copy of the whole output folder. Rebuilding the project
# while this script is using the binary is otherwise fatal mid-batch, and it is
# an easy thing to do by accident in another window. Copying only the .exe is
# not enough: it is an apphost that loads ZX2ILRecomp.dll next to it.
$runDir = Join-Path $env:TEMP ("zx2il-demo-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$runExe = Join-Path $runDir 'ZX2ILRecomp.exe'

New-Item -ItemType Directory -Force -Path $runDir | Out-Null
Copy-Item -Path (Join-Path (Split-Path $exe -Parent) '*') -Destination $runDir -Recurse -Force

$exe = $runExe

if (-not (Test-Path -LiteralPath $InputDir)) {
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

# Snapshot names contain '[', ']' and sometimes '+' — PowerShell treats those as
# wildcard metacharacters in -Path/-Filter, so every file operation here uses
# -LiteralPath. Sorting is by name only, to keep the batch order stable.
$snapshots = Get-ChildItem -LiteralPath $InputDir -File -Recurse |
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

    if ((Test-Path -LiteralPath (Join-Path $work "$name.dll")) -and -not $Force) {
        Write-Host "  skip   $name (already built, use -Force)" -ForegroundColor DarkGray
        $results += [pscustomobject]@{ Name = $name; Status = 'skipped'; Detail = '' }
        continue
    }

    Write-Host "  lift   $name ..." -ForegroundColor Cyan -NoNewline

    $liftClock = [System.Diagnostics.Stopwatch]::StartNew()
    $liftLog = Join-Path $work 'lift.log'

    New-Item -ItemType Directory -Force -Path $work | Out-Null

    # Capture the lifter's own output instead of discarding it: when a lift
    # fails, the reason is in there and nowhere else.
    #
    # Out-File with -LiteralPath, not the `*>` redirection operator: snapshot
    # names contain '[', ']' and '+', and the redirection operator binds to
    # -Path, which treats those as wildcards and throws.
    & $exe $snapshot.FullName -o $OutputDir --fresh --no-tui 2>&1 |
        Out-File -LiteralPath $liftLog -Encoding utf8

    $liftExit = $LASTEXITCODE
    $liftClock.Stop()

    if ($liftExit -ne 0) {
        $why = ''

        if ($liftExit -eq -1) {
            $why = 'could not start the lifter (was it rebuilt while running?)'
        } elseif (Test-Path -LiteralPath $liftLog) {
            $why = (Get-Content -LiteralPath $liftLog |
                Where-Object { $_ -match '\[(ERROR|WARN)\]' } |
                Select-Object -Last 1)
        }

        Write-Host "`r  FAIL   $name (exit $liftExit)" -ForegroundColor Red

        if ($why) {
            Write-Host "         $why" -ForegroundColor DarkGray
        }

        $results += [pscustomobject]@{ Name = $name; Status = 'failed'; Detail = "exit $liftExit" }
        continue
    }

    $dll = Join-Path $work "$name.dll"

    if (-not (Test-Path -LiteralPath $dll)) {
        Write-Host "`r  FAIL   $name (no assembly)" -ForegroundColor Red
        $results += [pscustomobject]@{ Name = $name; Status = 'failed'; Detail = 'no assembly' }
        continue
    }

    $analysis = Get-Content -LiteralPath (Join-Path $work 'analysis.txt') -Raw
    $entry = if ($analysis -match 'entry\s+:\s+(\S+)') { $Matches[1] } else { '?' }
    $instructions = if ($analysis -match 'instructions\s+:\s+(\d+)') { [int]$Matches[1] } else { 0 }

    # Why the tape may not be fully recoverable, if the loader said so. This is
    # only used for the detail text: the playable/partial verdict comes from the
    # screenshot, not from a guess about the loader.
    $caveat = ''

    if ($analysis -match 'unheadered data block') {
        $caveat = 'unheadered blocks'
    } elseif ($analysis -match 'outside every loaded block') {
        $caveat = 'relocating loader'
    }

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

    $output | Out-File -LiteralPath (Join-Path $work 'demo.txt') -Encoding utf8
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

    # Ground truth: does the machine actually draw anything? A lift that runs
    # 200 frames without trapping and still renders a blank screen is not
    # playable, whatever the loader thought of the tape.
    $colours = Get-FrameColourCount $frame

    if ($colours -lt 3) {
        $why = if ($caveat) { $caveat } else { 'renders blank' }
        Write-Host "`r  PART   $name ($why, $colours colours)" -ForegroundColor DarkYellow
        $results += [pscustomobject]@{ Name = $name; Status = 'partial'; Detail = "$why, $colours colours" }
        continue
    }

    $total = $liftClock.Elapsed.TotalSeconds + $runClock.Elapsed.TotalSeconds
    Write-Host "`r  ok     $name (entry $entry, $colours colours, $($total.ToString('0.0'))s)" -ForegroundColor Green
    $results += [pscustomobject]@{ Name = $name; Status = 'ok'; Detail = "entry $entry, $colours colours" }
}

$clock.Stop()

Write-Host ""
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "-------"

$results | Format-Table -AutoSize -Property Name, Status, Detail | Out-Host

$ok = ($results | Where-Object { $_.Status -eq 'ok' }).Count
$built = ($results | Where-Object { $_.Status -in 'ok', 'built' }).Count
$partialCount = ($results | Where-Object { $_.Status -eq 'partial' }).Count
$failed = ($results | Where-Object { $_.Status -in 'failed', 'trap' }).Count

Write-Host ""
Write-Host ("{0} playable, {1} built, {2} partial, {3} failed, {4} total in {5:mm\:ss}" -f `
    $ok, $built, $partialCount, $failed, $results.Count, $clock.Elapsed) -ForegroundColor Cyan
Write-Host "Play: $OutputDir\<name>\<name>.exe" -ForegroundColor DarkGray

Remove-Item -LiteralPath $runDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failed -gt 0) {
    exit 1
}
