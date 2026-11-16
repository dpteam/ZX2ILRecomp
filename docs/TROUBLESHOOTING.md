# Troubleshooting

Symptom first, cause second, fix last.

---

## The lift itself

### "No ZX Spectrum snapshots found"

The input path exists but holds no `.z80`, `.sna`, `.szx`, `.tap` or `.tzx`
files. A library nested deeper than one level needs `-r`.

### "Input path not found"

The path is quoted incorrectly, or it is a `.zip`. Archives are not unpacked
automatically — extract first.

### "Compilation disabled (--no-compile): C# only"

Not an error. Remove `--no-compile` to package.

### "Packaging failed: dotnet publish failed"

Usually one of:

- **No SDK installed.** `dotnet --list-sdks` must show 10.x. The lifter itself
  needs only the runtime, but packaging needs the SDK.
- **A generated-code compile error.** The full compiler output is in the log and
  in `logs/`. That is a lifter bug: the offending line is in
  `src/Game.generated.cs`.
- **The output game is still running.** A locked `<Name>.dll` cannot be
  replaced. Close the game and re-run.

### "Already lifted, skipping (use --fresh to redo)"

Output already exists for that snapshot. `--fresh` re-lifts it.

## The generated game

### It opens, then immediately closes

`fatal.log` next to the exe has the exception. Most common: the game was built
for a .NET version that is not installed. Rebuild with `--full-exe` to bundle
the runtime.

### Black screen, no crash

The game is running but drawing nothing. Check `analysis.txt`:

- **`rom: none (pseudo-ROM installed)`** — the game calls into ROM for its
  drawing or printing routines, which the stub does not provide. Put a real
  `48.rom` next to the snapshot and lift again.
- **`indirect jumps` > 0** — the entry point or a hot path is reached through a
  computed jump. Run the game for a few seconds, then paste
  `dynamic_targets.log` into `dynamic_targets.txt` and lift again.

### Garbage on screen

Almost always the wrong model. A 128K game lifted as 48K shows the wrong bank at
`0xC000`; force the right one with `--model 128`. `analysis.txt` records what
was auto-detected and why.

### It hangs on the first frame

`cpu_trap.log` next to the exe says where. Typical causes:

- **`Unknown opcode`** — the disassembler produced an address in the middle of
  an instruction (a jump into data). Add the real entry point to
  `dynamic_targets.txt`.
- **`Dynamic execution failed`** — execution reached an address outside RAM
  (a corrupted stack, usually because the snapshot was taken mid-call with an
  invalid `SP`).
- **Infinite loop with no trap** — the game is polling for input or waiting for
  an interrupt that never arrives. Check that `IM` in `analysis.txt` matches
  what the game sets; if it is 0, the snapshot was taken before initialisation.

### No sound

`F2` toggles it. If it is on and still silent, the game may drive the AY through
the `0xBFFD`/`0xFFFD` pair only after it has selected a register — check that
`Ayu.Sel` is being written in the diagnostics overlay.

### Keyboard does not respond

Cursor keys and `QAOP` + `SPACE` both work. If nothing registers, the game may
read the keyboard through a port pattern the decoder does not special-case; the
overlay's `port`/`rd` fields show exactly what it polled and what it got back.

## Tooling

### `dotnet build` fails with SDK errors

`global.json` pins `10.0.100` with `rollForward: latestFeature`. A machine with
only 9.x will refuse to build. Install the .NET 10 SDK.

### `--selftest` fails on packaging but the lift is fine

The self-test runs the same packager as a normal lift, so this is the SDK
problem above, not a lifter problem.

### `--selftest` fails on execution

`--keep` preserves the artifacts under
`zx_lifted_output/_selftest/out/<name>/`. Run the generated assembly by hand:

```powershell
dotnet zx_lifted_output/_selftest/out/loop-48-sna/loop-48-sna.dll --headless 60
```

and read `cpu_trap.log` in that folder.

## Reporting a problem

Collect:

1. the snapshot format and model (`--version`, plus the `analysis.txt` header)
2. the full log from `logs/`
3. `cpu_trap.log` and `dynamic_targets.log` from the output folder
4. whether `--selftest` passes on the same machine

That is enough to tell a loader bug from a lifter bug from a runtime bug.
