# Architecture

How one lift works, stage by stage.

---

## 1. Snapshot model

`Core/ZxSnapshot.cs` parses every supported container into one shape:

- the full register file, including the alternate set, `IX`/`IY`, `I`, `R`,
  `IFF1`/`IFF2` and the interrupt mode
- `Rom0` and `Rom1`, 16K each
- `RamBanks[0..7]`, 16K each
- the paging latches `Port7FFD` and `Port1FFD`

Memory is **bank-oriented, not flat**. That is the only representation that
survives 128K paging, because a game can remap `0xC000` while executing code in
the bank it is replacing.

The 48K slice numbering is the one ULA+ and most emulators use:

```
  0x0000-0x3FFF  Rom0 / Rom1        selected by port 0x7FFD bit 4
  0x4000-0x7FFF  RamBanks[5]        fixed screen bank
  0x8000-0xBFFF  RamBanks[2]        fixed, never paged
  0xC000-0xFFFF  RamBanks[bank]     bank = 0 on 48K, 0x7FFD & 7 on 128K
```

`GetFlat()` projects the current paging onto a 64K image for the disassembler.
It is a *view*, never the source of truth.

Details per format: [SNAPSHOTS.md](SNAPSHOTS.md).

## 2. ROM handling

Snapshots never carry the ROM, yet almost every game needs it: `RST` calls, the
IM 1 handler at `0x0038` and the character set all live at `0x0000-0x3FFF`.

`Core/RomProvider.cs` looks for `48.rom` / `128.rom` (and common aliases) next
to the snapshot, in the working directory, next to the executable and in a
`roms/` sub-folder of each. When nothing is found, `InstallMinimalRom()` writes
a stand-in:

```
  RST 00/08/10/18/20/28/30  →  RET
  0x0038 (IM 1)             →  PUSH AF; PUSH HL; LD HL,(5C78); INC HL;
                               LD (5C78),HL; POP HL; POP AF; RET
```

That is enough for the majority of games, which only need interrupts to advance
the system frame counter. Games that call into ROM proper will run but look
wrong; `analysis.txt` records which case applied.

## 3. Entry point

The order in `Host/Pipeline.cs` is load-bearing:

1. apply `--model`, so every heuristic sees the final value
2. load the ROM, because `HasRomCode` changes what is legal
3. apply the `SP` heuristic (a 48K stack at `0x0000` is inside ROM)
4. force the entry point **only when there is no ROM**
5. install the pseudo-ROM last, so it cannot influence step 4

Step 4 is the subtle one. Forcing `0x8000` whenever `PC < 0x4000` looks safe but
is wrong: a real snapshot may legitimately be paused inside a ROM call, and its
own `PC` plus a valid stack is then the correct place to resume. Manic Miner
(`PC=0x1F3E`) is the canonical example — forcing it sent execution into the ROM
editor loop.

## 4. Analysis

`Core/DisassemblerZ80.cs` runs two passes.

**Recursive descent** starts from the entry point, the eight RST/IM1 vectors
(when a ROM is present) and any user-forced addresses. It follows calls, jumps
and fall-throughs, and stops at `RET`, unconditional `JP` and `HALT`.

**Linear sweep** then covers everything the descent could not prove reachable,
skipping:

- the bitmap and attributes (`0x4000-0x5AFF`)
- bytes already covered by a decoded instruction
- runs of 16+ identical filler bytes (`0x00` or `0xFF`), which are data or
  padding, not code

The sweep matters because games driven by jump tables keep most of their code in
blocks the descent cannot see. Without it those blocks fall back to the
interpreter; with it they become first-class methods.

Anything genuinely computed (`JP (HL)`, `JP (IX)`, `RET` to a computed address)
is recorded in `model.IndirectJumps` and reported in `analysis.txt`.

## 5. Code generation

`Lifting/` is one emitter per concern, all writing through `EmitWriter` so the
output stays indented and readable:

| File | Emits |
|---|---|
| `LifterZ80.cs` | the file header and the namespace; drives the others |
| `BasicBlocks.cs` | groups instructions into methods (see below) |
| `ProgramEmitter.cs` | `Main`, argument parsing, headless mode, console attach |
| `RuntimeEmitter.*.cs` | registers, dispatch loop, instruction executor, helpers |
| `MemoryEmitter.cs` | the 64K decoder and the AY register file |
| `UlaEmitter.cs` | screen decoding, palette, RGBA frame buffer |
| `AudioEmitter.cs` | beeper + AY mixer over `waveOut` |
| `FormEmitter.cs` | the window, key matrix, overlay, ICO writer |

### Pruning first

The linear sweep is deliberately greedy: it decodes everything in RAM that
recursive descent did not reach, because jump-table-driven games keep real code
in blocks the descent cannot prove reachable.

The cost of that greed is measured, not guessed. On Arkanoid the sweep adds
46 000 instructions but only 131 basic blocks, and the game executes the exact
same 4 335 630 instructions over 600 frames either way — so the sweep
contributed nothing but 3 MB of C# and seven seconds of compilation.

`Core/Reachability.cs` therefore computes reachability as a proper fixed point
over *real* control flow:

* start from the roots — entry point, interrupt vectors, forced addresses;
* follow fall-through and jump/call targets of instructions that are **already
  known reachable**;
* repeat until nothing new is found.

The subtle part is that a target only counts when the instruction holding it is
itself reachable. Visiting the targets of *every* decoded instruction — the
obvious implementation — makes almost everything reachable, because swept
garbage contains jumps pointing all over RAM, and the filter then prunes
nothing.

The same trap applies to `AnalysisResultZ80.Functions`: it collects call and
jump targets found by the sweep too, so using it as the root set defeats pruning
entirely. The roots are built explicitly in `LifterContext.BuildRoots`.

Result on Arkanoid: 574 blocks from 2 290 instructions, 47 032 of 49 322 decoded
addresses pruned (95%), 0.57 MB of C# instead of 3.4 MB.

What the sweep is still good for: an address it decoded can be promoted by a
decoded jump from reachable code, and a genuinely computed target is covered by
the interpreter and reported through `dynamic_targets.log`, which feeds the next
lift.

### One method per basic block

One method per instruction was the first design and it was wrong. It produced
37 000 methods and 7 MB of C# for a 48K game, and the C# compiler became the
bottleneck: **0.3 s to analyse, 2 minutes to compile**. It was also pure
overhead at run time, because a straight run of twenty instructions became
twenty dispatch round-trips.

`Lifting/BasicBlocks.cs` groups instructions into maximal runs that are entered
only at the first instruction and left only at the last. A block starts when an
address is the entry point, the target of a jump/call/RST, or not the
fall-through of the previous instruction.

Two rules are easy to get wrong and are explicit in the code:

* **A `CALL` or `RST` ends its block.** They push a return address; if the next
  instruction were interior to the block, that return would land in the middle
  of it with no dispatch entry.
* **An unsupported instruction ends its block**, because it hands off to the
  interpreter, which returns a computed address.

The result for the same game: **3 100 methods, 3.2 MB, 9.6 s end to end** — a
14× speedup with identical behaviour.

```csharp
static ushort B8006()
{
    // 8006  INC A
    ExecBase(0x3C, 0x0000);
    // 8007  SRL A
    ExecCB(0x3F);
    // 8008  OUT (0xFE),A
    ExecBase(0xD3, 0x00FE);
    return 0x800A;
}
```

Dispatch is still three levels deep, but only block starts appear in it:

```csharp
static ushort StepStatic(ushort pc)
{
    switch (pc >> 8) { case 0x80: return StepPage80(pc); ... default: return StepDynamic(pc); }
}
```

The opcode geometry tables (`CpuZ80`) are serialised into the generated file as
decimal literals rather than recomputed there. A table bug then shows up as a
diff in the output instead of as mystery timing drift.

## 6. Packaging

`Compilation/GamePackager.cs` writes a throwaway project next to the game and
runs `dotnet publish`:

- **AppHost** (default) → `Game.exe` launcher + `Game.dll` + deps/runtimeconfig
- **AssemblyOnly** → `Game.dll` only
- **SelfContained** → one file, runtime bundled

Two details that are easy to get wrong and are handled explicitly:

* `dotnet publish -o <dir>` forwards the path to MSBuild as a property, and
  MSBuild splits property values on commas. A game called
  `Manic Miner, The` therefore fails with `MSB1006`. Publishing goes to a
  comma-free scratch directory and the results are moved afterwards.
* The application icon is only picked up by Explorer when it is embedded in the
  PE resource table, which the SDK does for the apphost. That is why the icon
  is passed as `ApplicationIcon` and not written after the fact.

## 7. Verification

`Host/SelfTest.cs` closes the loop. For every supported container it:

1. generates a synthetic snapshot with a known program
2. runs the full pipeline (load → analyse → emit → publish)
3. executes the result with `--headless` and a frame budget

The embedded program is deliberately boring and self-checking: it sets a stack,
enables interrupts, writes to the border port and halts in a loop. That proves
the stack works, the IM 1 vector returns, and port writes reach the ULA. A
snapshot that lifts but traps on `HALT` fails here.

Unit tests (`tests/`) cover the pieces: CLI parsing, name sanitising, all five
loaders, paging, opcode lengths, control-flow edges, mnemonics and code
generation.
