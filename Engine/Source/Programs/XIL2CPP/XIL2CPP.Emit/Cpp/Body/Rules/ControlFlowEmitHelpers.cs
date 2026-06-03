// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Small internal helpers shared by the WU-D2 control-flow lowering rules
/// (<see cref="IfStatementLoweringRule"/>, <see cref="ReturnStatementLoweringRule"/>,
/// <see cref="BlockLoweringRule"/>, <see cref="ForLoopLoweringRule"/>). These
/// rules build header / statement lines in pieces with
/// <see cref="CppWriter.Append(string)"/> (the raw, non-indenting escape hatch)
/// so a recursed expression can be composed inline; the writer exposes no
/// indent-only primitive, so the current indentation is replicated here.
/// </summary>
internal static class ControlFlowEmitHelpers
{
    /// <summary>
    /// Append the current indentation (<see cref="CppWriter.IndentUnit"/>
    /// repeated <see cref="CppWriter.Depth"/> times) WITHOUT any trailing
    /// content or newline, so a caller can then build the rest of an indented
    /// line piecewise via <see cref="CppWriter.Append(string)"/>. Byte-identical
    /// to the writer's own internal indentation, so a line composed this way is
    /// indistinguishable from one written through
    /// <see cref="CppWriter.AppendLine(string)"/>.
    /// </summary>
    /// <param name="writer">The writer to append the indentation into. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public static void AppendIndent(CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        for (int i = 0; i < writer.Depth; i++)
        {
            writer.Append(CppWriter.IndentUnit);
        }
    }
}
