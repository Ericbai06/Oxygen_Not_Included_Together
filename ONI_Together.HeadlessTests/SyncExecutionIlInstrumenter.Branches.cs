using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static partial class SyncExecutionIlInstrumenter
{
    private static void ExpandShortBranches(AssemblyDefinition assembly)
    {
        foreach (Instruction instruction in AllTypes(assembly.MainModule)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
            instruction.OpCode = LongBranch(instruction.OpCode);
    }

    private static OpCode LongBranch(OpCode opcode) =>
        opcode.Code switch
        {
            Code.Br_S => OpCodes.Br,
            Code.Brfalse_S => OpCodes.Brfalse,
            Code.Brtrue_S => OpCodes.Brtrue,
            Code.Beq_S => OpCodes.Beq,
            Code.Bge_S => OpCodes.Bge,
            Code.Bge_Un_S => OpCodes.Bge_Un,
            Code.Bgt_S => OpCodes.Bgt,
            Code.Bgt_Un_S => OpCodes.Bgt_Un,
            Code.Ble_S => OpCodes.Ble,
            Code.Ble_Un_S => OpCodes.Ble_Un,
            Code.Blt_S => OpCodes.Blt,
            Code.Blt_Un_S => OpCodes.Blt_Un,
            Code.Bne_Un_S => OpCodes.Bne_Un,
            Code.Leave_S => OpCodes.Leave,
            _ => opcode
        };
}
