namespace ZX2ILRecomp.Core;

/// <summary>How a decoded instruction transfers control.</summary>
public enum OpControlZ80
{
    /// <summary>Falls through to the next instruction.</summary>
    Normal,

    /// <summary>Relative jump (JR / DJNZ).</summary>
    Branch,

    /// <summary>Absolute jump, unconditional or conditional.</summary>
    Jmp,

    /// <summary><c>JP (HL)</c> / <c>JP (IX)</c> / <c>JP (IY)</c> — target is computed.</summary>
    JmpInd,

    /// <summary>Call, unconditional or conditional.</summary>
    Call,

    /// <summary>Return, unconditional or conditional.</summary>
    Ret,

    /// <summary>One of the eight <c>RST</c> vectors.</summary>
    Rst,

    /// <summary><c>HALT</c>.</summary>
    Halt,

    /// <summary>Not a valid instruction in this context.</summary>
    Invalid
}

/// <summary>One decoded Z80 instruction plus its control-flow edges.</summary>
public sealed class InstructionZ80
{
    /// <summary>Address the instruction starts at.</summary>
    public ushort Address;

    /// <summary>0 for unprefixed, otherwise 0xCB / 0xED / 0xDD / 0xFD / 0xDDCB / 0xFDCB.</summary>
    public int Prefix;

    /// <summary>Opcode byte, prefix stripped.</summary>
    public byte Opcode;

    /// <summary>Total length in bytes, prefixes included.</summary>
    public int Length;

    /// <summary>Immediate 8- or 16-bit operand, when the form has one.</summary>
    public ushort Operand;

    /// <summary><c>(IX+d)</c> displacement, when the form has one.</summary>
    public sbyte Displacement;

    /// <summary>Mnemonic-ish text used in generated comments and reports.</summary>
    public string Text = string.Empty;

    public OpControlZ80 Control;

    /// <summary>Static branch/call target, valid when <see cref="HasTarget"/>.</summary>
    public ushort Target;

    public bool HasTarget;

    /// <summary>Address control falls to when the instruction does not jump away.</summary>
    public ushort Fallthrough;

    public bool HasFallthrough;

    /// <summary>False when the lifter must fall back to the interpreter for this instruction.</summary>
    public bool Supported;

    /// <summary>Condition code (0-7) for conditional jumps/calls/returns, -1 when unconditional.</summary>
    public int Condition = -1;

    /// <summary>True for <c>DJNZ</c>, which has its own B-decrement semantics.</summary>
    public bool IsDJNZ;
}

/// <summary>Everything the lifter needs to know about one snapshot.</summary>
public sealed class AnalysisResultZ80
{
    /// <summary>Every decoded instruction, keyed by address.</summary>
    public SortedDictionary<ushort, InstructionZ80> Instructions { get; } = new();

    /// <summary>Every instruction address, ascending — one generated method each.</summary>
    public List<ushort> Labels { get; set; } = new();

    /// <summary>Addresses that are provably entered by a call or jump.</summary>
    public List<ushort> Functions { get; } = new();

    /// <summary>Opcodes the decoder could not classify.</summary>
    public List<byte> UnknownOpcodes { get; } = new();

    /// <summary>Addresses of computed jumps (indirect dispatch points).</summary>
    public List<ushort> IndirectJumps { get; } = new();

    /// <summary>Addresses the user forced into the analysis via <c>dynamic_targets.txt</c>.</summary>
    public List<ushort> DynamicTargets { get; } = new();

    /// <summary>Where execution starts.</summary>
    public ushort Entry;

    /// <summary>True when the linear sweep (not just recursive descent) ran.</summary>
    public bool Swept;
}
