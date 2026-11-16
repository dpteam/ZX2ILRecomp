# The generated runtime

What `Game.generated.cs` contains, why each part exists, and where the sharp
edges are. One file, no dependencies, no reflection.

---

## Structure

```
namespace Lifted.<Name>
  static class Program      entry point, argument parsing, headless mode
  static class Runtime      registers, dispatch, instruction executor, helpers
  static class Memory       64K decoder driven by the paging latches
  static class Ula          screen decoding, palette, RGBA frame buffer
  static class Ayu          AY-3-8912 register file
  static class Audio        beeper + AY mixer over waveOut
  sealed class UlaForm      the window, key matrix, overlay, ICO writer
```

## Execution model

```csharp
while (true)
{
    LastPC = pc;
    InsCount++;
    if (CheckInterrupt(pc)) pc = DispatchTarget;
    else pc = StepStatic(pc);
}
```

`StepStatic` dispatches on the high byte, then on the block start address, then
calls `Bxxxx()`. One method covers a whole basic block, so a straight run of
instructions executes inline:

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

Control flow is therefore plain C# returns, not a simulated program counter.
Calls push a literal return address; returns pop it. That is the whole point of
recompiling rather than interpreting.

`StepDynamic` is the fallback: it decodes from memory at run time and executes
one instruction through the interpreter. It exists for computed jumps, for the
`DD`/`FD` pages, and for the swept-but-unreachable decodes that
`Core/Reachability.cs` pruned.

## Registers

Individual bytes (`A`, `F`, `B`, …) with 16-bit properties on top (`BC`, `DE`,
`HL`, `AF`). Byte-level storage is deliberate: half-register access
(`IXH`/`IXL`) and `EX AF,AF'` are then direct field reads instead of shifts.

Flags are the real Z80 bit layout, so `Conditional(cc)` is a mask test:

| Bit | Flag | Bit | Flag |
|---|---|---|---|
| 7 | S | 3 | — |
| 6 | Z | 2 | P/V |
| 5 | — | 1 | N |
| 4 | H | 0 | C |

## Memory

`Read`/`Write` are pure functions of `Model` and `Port7FFD`. Nothing is cached,
because a game can flip the paging latch in the middle of a routine executing
from the bank it replaces.

```csharp
public static byte Read(ushort address)
{
    if (address < 0x4000)
    {
        if (Runtime.Model >= 128 && (Runtime.Port7FFD & 0x10) != 0) return Runtime.Rom1[address];
        return Runtime.Rom0[address];
    }
    if (address < 0x8000) return Runtime.RamBanks[5][address - 0x4000];
    if (address < 0xC000) return Runtime.RamBanks[2][address - 0x8000];
    int bank = (Runtime.Model >= 128) ? (Runtime.Port7FFD & 7) : 0;
    return Runtime.RamBanks[bank][address - 0xC000];
}
```

The power-on image is stored as base64 literals in `InitMemory()`; `Reset()`
clones them, so a restart is a real restart.

## Interrupts

Timing is counted in T-states, 69888 per 50 Hz frame.

```csharp
public static bool CheckInterrupt(ushort pc)
{
    if (TStates >= NextIntT) { NextIntT += 69888; InterruptPending = true; Frames++; }
    if ((InsCount & 4095) == 0) Throttle();
    if (!InterruptPending || !IFF1) return false;
    InterruptPending = false;
    Halted = false;
    Push16(pc);
    IFF1 = false; IFF2 = false;
    if (IM == 2) { ushort vector = (ushort)((I << 8) | 0xFF); DispatchTarget = Memory.Read16(vector); }
    else DispatchTarget = 0x0038;
    return true;
}
```

`HALT` is handled specially: it advances wall-clock time until the next
interrupt instead of spinning on a counter, so a halted game costs no CPU.

**Why `IFF1` is forced on at start:** a snapshot taken inside a `DI`'d routine
would otherwise never receive a frame interrupt again and would sit frozen at
its first `HALT`.

## ULA

The frame buffer is 320×240 RGBA with the 256×192 picture centred, so a
border-colour effect is visible rather than clipped.

Bitmap addressing follows the hardware's three interleaved 64-line thirds:

```
address = ((y & 0xC0) << 5) + ((y & 0x07) << 8) + ((y & 0x38) << 2) + (x >> 3)
```

Attributes at `0x1800 + (y >> 3) * 32 + (x >> 3)`: ink, paper, bright, flash.
Flash swaps ink and paper every 16 frames. On a 128K, bit 3 of `0x7FFD` selects
bank 7 as the visible screen.

Rendering happens once per window tick (20 ms) under `FrameLock`, then blits
2× with `InterpolationMode.NearestNeighbor` — pixel art must not be smoothed.

## Sound

`winmm` `waveOut`, 22050 Hz, unsigned 8-bit, two rotating 2048-byte buffers.
The mixer sums:

- the beeper bit (`BeeperLevel`, set by port `0xFE` bit 4) as a fixed offset
- three AY tone channels at `1773400 / (16 * period)`

No envelopes, no noise channel: games that rely on them sound thin but correct.
The audio thread is separate from the CPU thread and only reads state.

## Keyboard

The real 8×5 matrix, one byte per half-row, `0` = pressed:

```csharp
if ((port & 1) == 0)
{
    int high = port >> 8; int result = 0xFF;
    for (int i = 0; i < 8; i++)
        if ((high & (1 << i)) == 0) result &= KeyMatrix[i];
    return (byte)(result & 0xBF);
}
```

`ReadPort` is marked `NoInlining` on purpose. Games poll the keyboard in a tight
loop, and without a call barrier the JIT can hoist the `KeyMatrix` read out of
the loop — which freezes input while the game still runs.

Cursor keys duplicate `QAOP` + `SPACE`, so the common layout works without
remapping.

## Ports

| Port | Read | Write |
|---|---|---|
| `xxFE` (even) | keyboard matrix | border, beeper |
| `0x7FFD` | latch value | RAM bank, screen, ROM, lock |
| `0x1FFD` | latch value | stored verbatim |
| `0xFFFD` | AY register select | select register |
| `0xBFFD` | selected AY register | write register |
| `0x1F` (low) | Kempston | Kempston |

## Diagnostics

* `F1` — overlay: frame, instruction count, PC, traps, registers, CPU state.
* `F2` — toggle sound.
* `Esc` — quit.
* `cpu_trap.log` — written next to the exe on the first trap of each kind.
* `dynamic_targets.log` — every computed jump target the runtime actually took.

That last file is the feedback loop: paste its contents into
`dynamic_targets.txt`, lift again, and those addresses become first-class
methods instead of interpreter calls.

## Headless mode

```
Game.exe --headless 600
→ headless: frames=600 instructions=5 200 000 traps=0 tstates=41 900 000
```

Exit codes: `0` reached the budget, `3` trapped, `4` stopped early. This is what
`--selftest` and CI check.
