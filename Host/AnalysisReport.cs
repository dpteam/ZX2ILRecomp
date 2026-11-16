using System.Globalization;
using System.Text;
using ZX2ILRecomp.Core;

namespace ZX2ILRecomp.Host;

/// <summary>
/// Writes <c>analysis.txt</c> next to the generated game.
///
/// A lift that "works" is not the same as a lift that is understood: this file
/// is the record of what the analyser actually saw, so a misbehaving game can
/// be diagnosed without a debugger. Everything a support question needs — which
/// ROM was used, how much code was proven reachable, which jumps are computed,
/// and what the loader had to guess — is on the first twenty lines.
/// </summary>
public static class AnalysisReport
{
    private const int MaxListingLines = 8000;

    public static void Write(
        string path,
        string snapshotPath,
        ZxSnapshot snapshot,
        AnalysisResultZ80 model,
        IReadOnlyList<ushort> forced,
        string? romPath)
    {
        StringBuilder sb = new();

        sb.AppendLine("ZX2ILRecomp analysis");
        sb.AppendLine("====================");
        sb.AppendLine();
        sb.AppendLine("snapshot      : " + snapshotPath);
        sb.AppendLine("format        : " + snapshot.Format);
        sb.AppendLine("model         : " + snapshot.Model + "K");
        sb.AppendLine("rom           : " + (romPath is null ? "none (pseudo-ROM installed)" : romPath));
        sb.AppendLine("entry         : 0x" + snapshot.Entry.ToString("X4", CultureInfo.InvariantCulture));
        sb.AppendLine("instructions  : " + model.Instructions.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("labels        : " + model.Labels.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("functions     : " + model.Functions.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("indirect jumps: " + model.IndirectJumps.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("unknown opcodes: " + model.UnknownOpcodes.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("linear sweep  : " + (model.Swept ? "on" : "off"));
        sb.AppendLine("forced targets: " + forced.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("registers");
        sb.AppendLine("  AF=0x" + ((snapshot.A << 8) | snapshot.F).ToString("X4", CultureInfo.InvariantCulture) +
                      " BC=0x" + ((snapshot.B << 8) | snapshot.C).ToString("X4", CultureInfo.InvariantCulture) +
                      " DE=0x" + ((snapshot.D << 8) | snapshot.E).ToString("X4", CultureInfo.InvariantCulture) +
                      " HL=0x" + ((snapshot.H << 8) | snapshot.L).ToString("X4", CultureInfo.InvariantCulture));
        sb.AppendLine("  IX=0x" + snapshot.IX.ToString("X4", CultureInfo.InvariantCulture) +
                      " IY=0x" + snapshot.IY.ToString("X4", CultureInfo.InvariantCulture) +
                      " SP=0x" + snapshot.SP.ToString("X4", CultureInfo.InvariantCulture) +
                      " PC=0x" + snapshot.PC.ToString("X4", CultureInfo.InvariantCulture));
        sb.AppendLine("  I=0x" + snapshot.I.ToString("X2", CultureInfo.InvariantCulture) +
                      " R=0x" + snapshot.R.ToString("X2", CultureInfo.InvariantCulture) +
                      " IM=" + snapshot.IM.ToString(CultureInfo.InvariantCulture) +
                      " IFF1=" + (snapshot.IFF1 ? "1" : "0") +
                      " border=" + snapshot.Border.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("  port7FFD=0x" + snapshot.Port7FFD.ToString("X2", CultureInfo.InvariantCulture) +
                      " port1FFD=0x" + snapshot.Port1FFD.ToString("X2", CultureInfo.InvariantCulture));
        sb.AppendLine();

        if (snapshot.Notes.Count > 0)
        {
            sb.AppendLine("loader notes");
            foreach (string note in snapshot.Notes)
            {
                sb.AppendLine("  - " + note);
            }

            sb.AppendLine();
        }

        if (model.IndirectJumps.Count > 0)
        {
            sb.AppendLine("computed jumps (the interpreter handles these; add the real");
            sb.AppendLine("targets to dynamic_targets.txt to inline them on the next lift)");

            foreach (ushort address in model.IndirectJumps)
            {
                sb.AppendLine("  0x" + address.ToString("X4", CultureInfo.InvariantCulture));
            }

            sb.AppendLine();
        }

        if (model.UnknownOpcodes.Count > 0)
        {
            sb.AppendLine("undecoded opcodes");

            foreach (byte op in model.UnknownOpcodes)
            {
                sb.AppendLine("  0x" + op.ToString("X2", CultureInfo.InvariantCulture));
            }

            sb.AppendLine();
        }

        sb.AppendLine("disassembly (first " + MaxListingLines + " instructions)");
        sb.AppendLine();

        int written = 0;

        foreach (KeyValuePair<ushort, InstructionZ80> pair in model.Instructions)
        {
            if (written++ >= MaxListingLines)
            {
                sb.AppendLine("... listing truncated ...");
                break;
            }

            InstructionZ80 inst = pair.Value;
            sb.AppendLine("  " + inst.Address.ToString("X4", CultureInfo.InvariantCulture) + "  " + inst.Text);
        }

        try
        {
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to write analysis report: " + ex.Message);
        }
    }
}
