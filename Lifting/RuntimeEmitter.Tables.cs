using System.Globalization;
using ZX2ILRecomp.Core;

namespace ZX2ILRecomp.Lifting;

internal static partial class RuntimeEmitter
{
    /// <summary>
    /// Serialises the opcode geometry tables and the snapshot's memory image
    /// into the generated source.
    ///
    /// The tables are written as decimal literals rather than recomputed at
    /// runtime: a generated game then has no dependency on the lifter's own
    /// logic, and a table bug shows up as a diff in the output rather than as a
    /// mystery timing drift.
    /// </summary>
    private static void EmitTables(EmitWriter w, LifterContext ctx)
    {
        EmitTable(w, "BaseLen", CpuZ80.BaseLen);
        EmitTable(w, "EDLen", CpuZ80.EDLen);
        EmitTable(w, "UsesIXYDisp", CpuZ80.UsesIXYDisp);
        EmitTable(w, "BaseT", CpuZ80.BaseT);
        EmitTable(w, "CbT", CpuZ80.CbT);
        EmitTable(w, "EdT", CpuZ80.EdT);
        EmitTable(w, "PfT", CpuZ80.PfT);
        EmitTable(w, "DdCbT", CpuZ80.DdCbT);
        w.Blank();

        w.Open("public static void InitMemory()");
        EmitByteArray(w, "Rom0", ctx.Snapshot.Rom0);
        EmitByteArray(w, "Rom1", ctx.Snapshot.Rom1);

        for (int bank = 0; bank < ZxSnapshot.BankCount; bank++)
        {
            EmitByteArray(w, "InitialRam[" + bank.ToString(CultureInfo.InvariantCulture) + "]", ctx.Snapshot.RamBanks[bank]);
        }

        w.Line("for (int i = 0; i < 8; i++) RamBanks[i] = new byte[16384];");
        w.Close();
        w.Blank();
    }

    private static void EmitTable(EmitWriter w, string name, byte[] values)
    {
        w.Line("public static readonly byte[] " + name + " = new byte[256]");
        w.Line("{");

        for (int i = 0; i < 256; i += 16)
        {
            System.Text.StringBuilder line = new("    ");

            for (int j = 0; j < 16; j++)
            {
                line.Append(values[i + j].ToString(CultureInfo.InvariantCulture));

                // A trailing comma on every value but the last keeps the rows
                // uniform and is legal C#; a leading one is a syntax error.
                if (i + j < 255)
                {
                    line.Append(',');
                }
            }

            w.Line(line.ToString());
        }

        w.Line("};");
        w.Blank();
    }

    private static void EmitByteArray(EmitWriter w, string target, byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            w.Line(target + " = new byte[16384];");
            return;
        }

        // Base64 keeps the generated file ~33% larger than raw bytes but makes
        // it printable, diffable and immune to line-ending mangling.
        string base64 = Convert.ToBase64String(data);
        const int Chunk = 32768;

        w.Line(target + " = Convert.FromBase64String(");

        for (int i = 0; i < base64.Length; i += Chunk)
        {
            int length = Math.Min(Chunk, base64.Length - i);
            bool last = i + Chunk >= base64.Length;

            w.Line("    \"" + base64.Substring(i, length) + "\"" + (last ? ");" : " +"));
        }
    }
}
