# ZX2ILRecomp roadmap

Ordered from easiest to hardest. Each stage ships on its own and leaves the tool
in a working state.

Legend: `[x]` done · `[~]` partial · `[ ]` planned

---

## Stage 0 — Foundation (done)

- [x] .NET 10 toolchain, `global.json`, `net10.0-windows`
- [x] Zero third-party dependencies: no NuGet packages at all
- [x] One class per concern (`Core`, `Lifting`, `Compilation`, `Host`, `Common`)
- [x] Deterministic `.editorconfig`, `.gitignore`, `.gitattributes`
- [x] Packaging through the SDK's own `dotnet publish` (no Roslyn dependency)
- [x] `--selftest`: a synthetic snapshot per container, lifted, packaged **and executed**
- [x] Unit tests without a test framework (`tests/ZX2ILRecomp.Tests`)
- [x] Headless mode (`--headless N`) so generated games run in CI
- [x] Branding: vector logo, multi-resolution `.ico`, social preview

## Stage 1 — Loaders and memory model (done)

- [x] `.z80` v1, v2 and v3, compressed and raw, 48K and 128K
- [x] `.sna` 48K and the 128K extension (including `PC` recovery from the stack)
- [x] `.szx` with `Z80R`, `RAMP` and `ROMR` chunks
- [x] `.tap` and `.tzx` memory recovery
- [x] **Entry point from the BASIC loader** (`RANDOMIZE USR n`), in both number
      encodings found in real tapes. Without this a commercial tape starts at
      its load address — which is data — and the game sits there doing nothing
- [x] Bank-oriented memory model, correct 5/2/0 48K slice numbering
- [x] Port `0x7FFD` paging including the lock bit and both screens
- [x] ROM discovery next to the snapshot, plus a minimal pseudo-ROM fallback
- [x] Two-page 128K ROM loading (`128-0.rom` + `128-1.rom`), not one cloned page
- [x] 128K tapes detected and reported as unrecoverable instead of silently
      producing a blank screen

## Stage 2 — Analysis (done)

- [x] Recursive descent from the entry point, RST vectors and IM 1
- [x] Linear sweep with bitmap and filler-run skipping
- [x] Control-flow edges: calls, jumps, conditional branches, `DJNZ`, `RST`
- [x] Computed jumps reported instead of silently mis-decoded
- [x] `dynamic_targets.txt` / `.log` feedback loop
- [x] `analysis.txt` report with registers, coverage and full disassembly

## Stage 3 — Code generation (done)

- [x] One method per address, literal next-address returns
- [x] Three-level dispatch (`pc >> 8` → page → address)
- [x] Full base, `CB` and `ED` pages inlined
- [x] Interpreter fallback for `DD`/`FD` and computed targets
- [x] Opcode geometry tables serialised into the output
- [x] Readable output: annotated mnemonics, indentation, no wall of `goto`

## Stage 4 — Runtime (done)

- [x] ULA with correct bitmap interleaving, bright, flash, border
- [x] 8×5 keyboard matrix with `NoInlining` read barrier
- [x] Beeper and three AY tone channels over `waveOut`
- [x] Interrupt modes 0/1/2, `HALT` that costs no CPU
- [x] Headless mode and exit codes
- [x] Icon self-capture and reuse on the next lift

## Stage 5 — Interface (done)

- [x] Native WinForms GUI, no web view
- [x] Terminal UI fallback
- [x] CLI with long and short forms, `=` and space syntax
- [x] Coloured console log and a file log per run

---

## Stage 6 — Next: correctness of the hard cases

The remaining bugs are not in the framework; they are in specific games.

### 6a. Disk images (highest value per hour)

Roughly 40% of a real Spectrum library is disk-based, and none of it can be
opened today.

- [ ] `.dsk` / `.edsk` — +3 DOS and CPCEMU formats
- [ ] `.trd` / `.scl` — TR-DOS (Beta Disk) images
- [ ] TR-DOS ROM hooks (`RST 8` command dispatch, `0x3D13`)
- [ ] `+3` FDC (`0x1FFD`, `0x2FFD`, `0x3FFD`) — enough to boot, not to write
- [ ] Auto-detect the loader and the entry point the same way tapes are handled

### 6b. Z80 and timing

- [ ] **Inline the `DD`/`FD` pages.** They are currently interpreted. Inlining
      them removes the last interpreter call from most inner loops.
- [ ] **SMC detection.** Self-modifying code (Elite's loader, several
      protections) must invalidate any cached inlining for the modified range.
- [ ] **Contended memory timing.** `0x4000-0x7FFF` on a 128K stalls the CPU at
      the wrong moment. Matters for raster effects and speed-loaders.

### 6c. Runtime fidelity

- [ ] **AY envelopes and noise.** Registers 6, 13 and the envelope shape are
      stored but not synthesised, so games relying on them sound thin.
- [ ] **`+2A`/`+3` special paging.** Port `0x1FFD` bits 1-2 remap the whole
      address space; only the plain 128K layout is handled.
- [ ] **128K tape recovery.** Needs the loader emulated, not parsed: a 128K game
      pages banks in itself, and a tape only records addresses.

### 6d. BASIC as a second front end

Not a "BASIC to C# converter" — a second input to the same recompiler, emitting
the same shape of code (one method per statement, `goto` for `GO TO`).

- [ ] Tier 1: integer games — `PRINT`, `PLOT`, `DRAW`, `INKEY$`, `BEEP`
- [ ] Tier 2: the 5-byte float format (8-bit exponent, 32-bit mantissa)
- [ ] Tier 3: ROM-dependent semantics (`VAL`, `STR$`, calculator stack)

Tier 1 is achievable and covers type-in games. Tier 2 is expensive but finite.
Tier 3 is effectively a ROM emulator and should only be attempted on demand.

## Stage 7 — Audio in: lifting from a tape recording

The most interesting input the project could accept, and the one that unlocks
material nothing else can: **recordings**. Old cassettes, YouTube rips,
someone's phone held next to a 1984 tape deck. There is no `.tzx` for these and
there never will be — but the data is all there, as sound.

This is not speculative. It is already how a good part of the scene's own
archive was built: the `__Conv` folder next to the reference collection holds
MP3s of tape loading and the `.TZX` files somebody converted them into, plus
`TZX2WAV.EXE` for the opposite direction. The pipeline is proven; it just is not
part of any recompiler.

```
   recording.mp3/.wav/.flac
        │
        ├─ ffmpeg ──────────────────► mono PCM WAV, 44.1/48 kHz
        │
        ├─ pulse extraction ────────► edge timings (samples → µs)
        │
        ├─ edge → bit ──────────────► CUTS / SpeedLock / custom loaders
        │
        ├─ bit → byte ──────────────► blocks, checksums
        │
        ├─ byte → memory ───────────► the same TapeLoader used today
        │
        └─ lift ────────────────────► Game.exe
```

### Stage 7a. Audio → pulses

- [ ] Accept `.wav`, `.flac`, `.mp3`, `.ogg` as input
- [ ] Decode with ffmpeg when present, fall back to a plain WAV reader so the
      tool still works without it
- [ ] Normalise: mono, DC offset removal, optional low-pass, gain to full scale
- [ ] Hysteresis threshold instead of a single level — cassette recordings have
      a wandering baseline and a fixed threshold loses whole blocks
- [ ] Emit a **pulse train** (list of durations in T-states), which is the
      format every decoder below wants

### Stage 7b. Pulses → bits

The Spectrum encodes bits as two pulses of differing length; which one is longer
is the bit. Two conventions exist and both must be tried:

| Standard | Short pulse | Long pulse | Bit |
|---|---|---|---|
| ROM loader | 855 T | 1710 T | 0 = two short, 1 = two long |
| Turbo | varies per block | varies per block | same idea, different timings |

- [ ] ROM-loader decoder (the 48K/128K ROM routine timings)
- [ ] Turbo decoder with per-block timing read from a pilot tone
- [ ] **Auto-detect the timing** by clustering pulse lengths — this is what makes
      a recording with no metadata work at all
- [ ] Tolerate jitter: real tape speed drifts, so compare *ratios*, not absolute
      durations
- [ ] Report signal quality (SNR, jitter spread) so a bad recording fails loudly
      instead of producing plausible garbage

### Stage 7c. Bits → blocks

- [ ] Pilot tone + sync pulse detection to find block boundaries
- [ ] Flag byte, 8 data bytes, two checksum bytes
- [ ] **Checksum verification** — a block that fails is a block that must not be
      lifted. Silently accepting it produces a game that crashes 30 seconds in
      and looks like a recompiler bug
- [ ] Block type dispatch: header vs data, program vs CODE vs array
- [ ] Retry with a different threshold when checksums fail; report how many
      blocks were recovered

### Stage 7d. Blocks → snapshot

- [ ] Reuse `TapeLoader` verbatim — a decoded recording produces exactly the
      blocks a `.tap` would
- [ ] Same entry-point logic (`RANDOMIZE USR n`), same 128K warning
- [ ] Write a `.tap` next to the output so the decode is reproducible and the
      user can inspect it in any emulator

### Stage 7e. Custom loaders

The hard part, and the reason this is a stage and not a weekend:

- [ ] **SpeedLock 1-7** — the most common protection; changes timing mid-block
- [ ] **Alcatraz**, **Bleepload**, **Microsphere**, **Digital Integration**
- [ ] **Lenslok** — not a loader but a challenge-response screen; needs the
      key-card lookup, not the audio
- [ ] Detection by timing signature, then a loader-specific decoder
- [ ] Unknown signature: emit the pulse train as a `.wav` plus a report, so the
      user can hand it to an existing tool instead of getting nothing

### Why this is worth it

- Cassette-only releases that were never archived as files
- Recordings of games whose `.tzx` is lost but whose YouTube upload survives
- Russian and Eastern European tapes whose loaders were never documented
- It makes the recompiler the *only* tool that goes from "a sound file" to
  "a playable .NET game", which is a genuinely new capability rather than a
  faster path to the same place

### Non-goals

- Perfect archival accuracy. The output is a playable game, not a
  preservation-grade `.tzx`.
- Real-time decoding. This is an offline batch step, seconds per file is fine.
- Analog artefacts. Hiss, dropouts and wow/flutter are handled by tolerance and
  retries, not by modelling the tape deck.

---

## Stage 8 — Other ZX machines

Each of these is a different CPU or a different memory map, not a flag.

### ZX81 (and Timex TS1000/1500)

- [ ] `Z80` at 3.25 MHz, 1K or 16K RAM with the `0x4000` mirroring trick
- [ ] `ZX81` display file: `D_FILE`, `DF_CC`, the collapsed `CHR$` character set
- [ ] `SLOW`/`FAST` mode — in `SLOW` the ULA steals cycles and the screen is
      generated by the CPU itself, so a naive port of the Spectrum ULA is wrong
- [ ] `.p`, `.o` and `.81` containers
- [ ] `Timex` hi-res/hi-colour modes (8×1 attribute cells)

### ZX80

- [ ] Same CPU, but 4K ROM and a display file generated entirely in software
- [ ] Integer-only BASIC ROM calls, different system variable addresses
- [ ] `.o` container

### Spectrum Next

- [ ] `Z80N` opcodes (`LDIX`, `MUL D,E`, `PUSH nn`, …)
- [ ] Layer 2, sprites, tilemap, DMA
- [ ] `.nex` container
- [ ] 2 MB banked memory model

### SAM Coupé

- [ ] `Z80` at 6 MHz with a different paging scheme
- [ ] `.sad` container
- [ ] Its own video modes (MODE 4 is Spectrum-like, the rest are not)

## Stage 9 — Beyond

- [ ] **`.scr`/`.tap` screenshots as visual regression tests.** Compare a lifted
      game's frame N against a reference capture, so correctness is measured and
      not eyeballed.
- [ ] **Coverage report.** Percentage of executed instructions that were
      statically compiled rather than interpreted, per game.
- [ ] **A second target runtime.** The generated C# is plain .NET; an SDL or
      headless-only backend would let lifted games run off Windows.
- [ ] **Batch queue with resume.** A library of hundreds of snapshots with
      per-file state, so a long run survives a reboot.
