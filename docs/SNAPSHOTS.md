# Snapshot formats

What each container actually stores, what the loader recovers, and the traps in
between. Everything here is implemented in `Core/ZxSnapshot.cs`.

---

## `.z80`

Three versions exist in the wild and they are told apart by the `PC` field at
offset 8.

### Version 1 (PC ≠ 0)

30-byte header, then a single 48K block.

| Offset | Size | Contents |
|---|---|---|
| 0-3 | 4 | `A`, `F`, `C`, `B` |
| 4-7 | 4 | `L`, `H`, `E`, `D` |
| 8 | 2 | `PC` — non-zero means v1 |
| 10 | 2 | `SP` |
| 12 | 1 | `I` |
| 13 | 1 | `R` |
| 14 | 1 | flags: bit 0 → `R` bit 7, bits 1-3 → border, bit 5 → compressed |
| 15-22 | 8 | `E'`, `D'`, `C'`, `B'`, `L'`, `H'`, `F'`, `A'` |
| 23 | 2 | `IY` |
| 25 | 2 | `IX` |
| 27 | 1 | `IFF1` |
| 28 | 1 | `IFF2` |
| 29 | 1 | interrupt mode |

**Trap:** offsets 4-7 are `L`, `H`, `E`, `D` — not `HL` then `DE` as a pair of
little-endian words in the order you would expect. Reading them as `HL`, `DE`
silently swaps the two registers, and a game that looks "almost right" is the
usual symptom.

**Compression:** `ED ED <count> <value>` repeats `<value>` `<count>` times. A
count of `0` terminates the stream.

### Versions 2 and 3 (PC = 0)

Offset 8 is zero; the real `PC` lives in an extended header at offset 30.

```
  30    2   header length (23 = v2, longer = v3)
  +0    2   PC
  +2    1   hardware id (3..5 = 128K)
  +3    1   port 0x7FFD
  +4    1   port 0x1FFD
```

Then a sequence of page blocks:

```
  <2 bytes length> <1 byte page> <length bytes data>
```

A length of `0` means 16384 (uncompressed). Page numbering:

| Page | Meaning |
|---|---|
| 0, 2 | ROM 0 / ROM 1 (on some 128K dumps) |
| 3, 6, 7, 9, 10 | RAM bank `page - 3` |
| 4 | RAM bank 2 (the fixed `0x8000` page) |
| 5 | RAM bank 5 (the fixed `0x4000` page) |
| 8 | RAM bank 0 (the paged `0xC000` page) |

The 4→2 and 5→5 renumbering is not a quirk of this loader; it is how `.z80`
files are written, because the numbering follows the *slot*, not the bank.

## `.sna`

27-byte header followed by exactly 49152 bytes of RAM.

| Offset | Contents |
|---|---|
| 0 | `I` |
| 1-8 | `L'`, `H'`, `E'`, `D'`, `C'`, `B'`, `F'`, `A'` |
| 9-14 | `L`, `H`, `E`, `D`, `C`, `B` |
| 15 | `IY` |
| 17 | `IX` |
| 19 | flags: bit 2 → `IFF1` |
| 20 | `R` |
| 21-22 | `F`, `A` |
| 23 | `SP` |
| 25 | interrupt mode |
| 26 | border |

**Trap:** a plain `.sna` has no `PC` field. The `PC` was *pushed onto the
stack* before saving, so the loader reads it from the saved image at `SP` and
then advances `SP` by two. Forgetting this gives a `PC` of `0x0000`, which
lands in ROM.

A 128K `.sna` appends a length-prefixed block after the 48K image containing
`PC`, port `0x7FFD` and the remaining banks. The loader detects it by length and
switches the model to 128K.

## `.szx`

Chunked container. Signature `ZXST`, then 8-byte chunk headers:

```
  <4 byte id> <4 byte length> <length bytes data>
```

Chunks the loader understands:

| Id | Contents |
|---|---|
| `Z80R` | registers: `I`, `F'`, `IFF1/IFF2`, `F`, `A`, ... `SP`, `IM`, border, `0x7FFD`, `0x1FFD`, `PC` at +15 |
| `RAMP` | 1 byte page number, 1 byte flags, then 16K of data |
| `ROMR` | 1 byte ROM number, 7 bytes flags, then 16K of data |

**Trap:** the `RAMP` page number is at `+0`, not `+1` — `+1` is the flags byte.
Reading `+1` puts every page one bank off, which looks like "the game loads but
the screen is garbage".

## `.tap`

A sequence of blocks:

```
  <2 byte length> <length bytes: flag + payload + checksum>
```

- flag `0x00` = header: type, 10-character name, length, parameter 1, parameter 2
- flag `0xFF` = data: the payload the previous header described

Type `3` (CODE) and type `0` (program) are the ones that matter. A program
always loads at `0x8000` behind a BASIC stub.

**A tape is not a snapshot.** It carries no registers, no ROM and no stack.

### Where the game actually starts

This is the single most important thing a tape loader has to get right, and the
load address is *not* the answer. A commercial tape looks like this:

```
  header "ManicMiner"  78 bytes   BASIC: 10 CLEAR 30000
                                        20 LOAD ""CODE
                                        30 RANDOMIZE USR 33792
  header "mmm"        256 bytes   CODE, load 0x8000
  header "mm1"      32768 bytes   CODE, load 0x8000
```

The CODE blocks load at `0x8000`, but the game starts at `0x8400` — the first
1K is data tables. Starting at the load address runs the data and then sits
there doing nothing.

So `Core/TapeLoader.cs` collects every block first and places them afterwards,
because two decisions need the whole tape:

1. **The entry point**, from `RANDOMIZE USR n` in the BASIC program block.
2. **Which header field is the load address** — see below.

Tokenised BASIC stores `USR` as `0xC0`, and a number as `0x0E` plus five
space-padded ASCII digits. Two encodings appear in real tapes:

```
  standard:  C0 0E "33792" 00
  variant:   C0 "33792" 0E 00      digits straight after USR
```

Both are handled. The variant is what several tape-to-TZX converters emit, and
missing it is the difference between a working lift and a blank screen.

### The two header conventions

A CODE header carries two addresses and real files disagree about their order:

```
  start address, load address      the ROM's own order
  load address, start address      what some converters emit
```

A single-block game hides the difference. A multi-stage loader does not — and
multi-stage loaders are how most commercial games ship.

Two real examples, which disagree with each other:

| Game | param 1 | param 2 | Loader calls | Block size |
|---|---|---|---|---|
| Atic Atac | `0x5B80` | `0x8000` | `USR 0x5B80` | 18 bytes |
| Jet Set Willy | `0x7FFE` | `0x80FB` | `USR 0x8400` | 32 769 bytes |

Atic Atac is only covered by `param 1`. Jet Set Willy is covered by **both**,
because its block is 32K long — so "which one contains the entry point" cannot
decide it alone.

The loader applies three tests, in order:

1. **Does the block fit?** A load address must let the block fit in the 64K
   address space. Jet Set Willy's 32K block fits at `0x7FFE` and overflows at
   `0x80FB`.
2. **Does it cover the entry point?** If only one convention puts the entry
   point inside a block, that is the answer. This settles Atic Atac.
3. **Which addresses look like load addresses?** A block starts on a round
   boundary (`0x8000`, `0x4000`, `0x5B00`); a start address is wherever the code
   happens to begin (`0x80FB`).

Whichever convention wins is recorded in `analysis.txt`, so a wrong guess is
visible rather than mysterious.

## `.tzx`

Block-structured tape image, signature `ZXTape!`. The loader walks the block
table and recovers memory from:

| Block | Contents |
|---|---|
| `0x10` | standard speed data (same layout as a TAP block) |
| `0x11` | turbo speed data |
| `0x14` | pure data |

Every other block (tones, pulses, direct recording, metadata, loops, jumps) is
skipped by length, which is what keeps the parser from derailing on the
decorative blocks real `.tzx` files are full of.

### 128K tapes are not recoverable

A 128K game loads its banks through its own loader, paging them in via port
`0x7FFD`. A tape image only records the *addresses* the blocks were saved at, so
everything lands in whichever bank is paged in at load time and the other seven
are lost.

The pipeline detects this and warns. Use a `.z80`, `.sna` or `.szx` snapshot of
a 128K game instead — those carry the full bank set.

### Tapes with unheadered data blocks

Some tapes carry **one** header followed by several raw data blocks. The game's
own routine loads the rest and decides the addresses at run time, so static
placement cannot be right.

Dizzy II is the example: one BASIC header, then five data blocks, and a loader
that does `POKE 24576, 96` before `RANDOMIZE USR 24576` — it writes the entry
point itself.

### Relocating loaders

A related case: the entry point is a valid address but lies **outside** every
loaded block. The loader computes it at run time and copies the payload there.

Batty uses Elite's *Uni-Loader*, whose BASIC program builds the address from
variables (`RANDOMIZE USR z`, where `z` is assembled from `PEEK`s) and then
moves code around before jumping. `param 1` names the entry point (`0xFCF1`)
while the block itself loads at `0x6400`, so nothing is at the entry until the
loader has run.

Both cases are detected and reported:

```
4 of 5 data blocks carry no header: this tape is loaded by the game's
own routine, so their addresses cannot be recovered statically.

Entry point 0xFCF1 is not inside any loaded block: this tape relocates
its code at run time, so the lift starts on uninitialised memory.
```

`analysis.txt` records the same, and `tools/demo.ps1` reports the game as
**partial** rather than playable. Recovering these needs the tape loader
emulated, which is [ROADMAP.md](../ROADMAP.md) Stage 6c.

## Bank mapping summary

For both `.z80` and `.sna`, the 48K image maps onto the fixed slots:

```
  RAM 0x4000-0x7FFF  →  RamBanks[5]
  RAM 0x8000-0xBFFF  →  RamBanks[2]
  RAM 0xC000-0xFFFF  →  RamBanks[0]
```

Banks 1, 3, 4, 6 and 7 only exist in a 128K dump. If a 48K snapshot is lifted as
128K (`--model 128`), those banks are zero-filled, which is exactly what the
hardware would show after a cold start.

## Paging

Port `0x7FFD` (128K):

| Bit | Meaning |
|---|---|
| 0-2 | RAM bank at `0xC000` |
| 3 | screen: 0 → bank 5, 1 → bank 7 |
| 4 | ROM: 0 → ROM 0, 1 → ROM 1 |
| 5 | paging lock: further writes to this port are ignored |

Port `0x1FFD` (+2A/+3): stored verbatim. The +3 disk interface is not emulated,
but games that only write the latch still see the value they wrote.
