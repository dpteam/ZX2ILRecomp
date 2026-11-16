using System.Text;

namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Indentation-aware writer for generated C#.
///
/// Every emitter appends through this instead of raw <c>StringBuilder</c>, so
/// the generated <c>Game.generated.cs</c> stays readable enough to debug: the
/// whole point of a recompiler is that a human can follow the output.
/// </summary>
public sealed class EmitWriter
{
    private readonly StringBuilder _sb;
    private int _indent;
    private int _blankRun;

    public EmitWriter(StringBuilder builder)
    {
        _sb = builder;
    }

    /// <summary>Current nesting depth in 4-space units.</summary>
    public int Indent => _indent;

    public void Push() => _indent++;

    public void Pop()
    {
        if (_indent > 0)
        {
            _indent--;
        }
    }

    /// <summary>Writes a blank line, collapsing runs of them into one.</summary>
    public void Blank()
    {
        if (_blankRun > 0)
        {
            return;
        }

        _sb.AppendLine();
        _blankRun++;
    }

    /// <summary>Writes one line at the current indentation.</summary>
    public void Line(string text)
    {
        _blankRun = 0;

        if (text.Length == 0)
        {
            _sb.AppendLine();
            return;
        }

        _sb.Append(' ', _indent * 4);
        _sb.AppendLine(text);
    }

    /// <summary>Writes several lines, each at the current indentation.</summary>
    public void Lines(params string[] lines)
    {
        foreach (string line in lines)
        {
            Line(line);
        }
    }

    /// <summary>Opens a brace block and indents one level.</summary>
    public void Open(string header)
    {
        Line(header);
        Line("{");
        Push();
    }

    /// <summary>Closes a brace block and unindents one level.</summary>
    public void Close()
    {
        Pop();
        Line("}");
    }

    /// <summary>Closes a block and writes a trailing comment on the brace.</summary>
    public void Close(string comment)
    {
        Pop();
        Line("} // " + comment);
    }

    public override string ToString() => _sb.ToString();

    /// <summary>
    /// Escapes text for inclusion in a C# string literal. Snapshot names come
    /// from the file system, so quotes and backslashes are entirely possible.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        System.Text.StringBuilder sb = new(text.Length + 8);

        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
