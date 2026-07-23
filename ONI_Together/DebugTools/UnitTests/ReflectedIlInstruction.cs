using System.Reflection.Emit;

namespace ONI_Together.DebugTools.UnitTests
{
	internal readonly struct ReflectedIlInstruction
	{
		internal ReflectedIlInstruction(int offset, OpCode code, object operand)
		{
			Offset = offset;
			Code = code;
			Operand = operand;
		}

		internal int Offset { get; }
		internal OpCode Code { get; }
		internal object Operand { get; }
	}
}
