# Contributing to ZX2ILRecomp

Thanks for looking. The rules here are short and mostly about not breaking the
two properties that make this project worth using: **no dependencies** and
**every lift is verifiable**.

---

## Ground rules

1. **No package references.** Not for tests, not for imaging, not for audio.
   If a feature seems to need one, the answer is to use the framework, a P/Invoke
   or a few dozen lines of code.
2. **No new top-level files.** Code belongs in `Core/`, `Lifting/`,
   `Compilation/`, `Host/` or `Common/`. One concern per file.
3. **Every behaviour change needs a test.** Either a unit test in `tests/` or a
   new case in `--selftest`.
4. **The generated C# must stay readable.** It is the product, not an
   intermediate. Annotated mnemonics, indentation, no wall of `goto`.

## Getting set up

```powershell
git clone https://github.com/dpteam/ZX2ILRecomp.git
cd ZX2ILRecomp
dotnet build
dotnet run -- --selftest
```

Requirements: Windows 10+, .NET 10 SDK.

## Before opening a pull request

All four must pass:

```powershell
dotnet build -warnaserror -c Debug
dotnet build -warnaserror -c Release
dotnet run --project tests/ZX2ILRecomp.Tests
dotnet run -- --selftest --frames 60
```

If you touched branding:

```powershell
dotnet run --project tools/Branding
```

and commit the regenerated files in `assets/`.

## What belongs where

| Directory | Contents |
|---|---|
| `Core/` | snapshot loaders, opcode tables, disassembler, ROM discovery |
| `Lifting/` | code generation, one emitter per runtime concern |
| `Compilation/` | packaging through `dotnet publish` |
| `Host/` | CLI parsing, pipeline, GUI, TUI, logging, self-test |
| `Common/` | helpers with no domain knowledge |
| `tests/` | unit tests and the assertion harness |
| `tools/` | developer utilities, never shipped |

A file in `Lifting/` may depend on `Core/` and `Common/`. It must never depend
on `Host/` — emitters describe the target machine, not the tool that runs them.

## Style

- 4 spaces, no tabs, braces on their own line (see `.editorconfig`).
- `var` is not used; explicit types are easier to read in a code generator.
- Comments explain *why*, not *what*. A comment that restates the code is
  deleted in review.
- Every non-obvious decision gets a comment naming the failure it prevents.
  The codebase is full of them on purpose — "why `SP=0xFF00` and not `0xFFFF`"
  is exactly the kind of thing that gets broken by a well-meaning refactor.

## Testing the hard parts

The interesting bugs are in specific games, so the useful contribution is
usually a **reproducing snapshot plus a failing self-test case**:

1. add the snapshot (or a minimal synthetic one) to the matrix in
   `Common/SyntheticSnapshot.cs`
2. write the assertion in `Host/SelfTest.cs` — what should the generated game do
   that it currently does not
3. then fix the lifter

A snapshot in the repository must be redistributable. If it is not, describe the
case in an issue instead and use a synthetic equivalent.

## Commit messages

Short imperative subject, then a body that says what was wrong:

```
Fix SZX RAMP page number

The page byte is at +0; +1 is the flags byte. Reading +1 put every
page one bank off, which looked like "loads fine, garbage screen".
```

## License

Contributions are accepted under GPL-3.0, the same license as the project.
