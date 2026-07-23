using System.Collections.Generic;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	internal enum FlowCallOutcomePolicy
	{
		None,
		ConditionalRollback,
	}

	internal sealed class FlowCallContract
	{
		internal FlowCallContract(MethodInfo method, string output, params string[] inputs)
		{
			Method = method;
			Inputs = inputs;
			Output = output;
		}

		internal MethodInfo Method { get; }
		internal IReadOnlyList<string> Inputs { get; }
		internal string Output { get; }
		internal FlowCallOutcomePolicy OutcomePolicy { get; set; }
		internal MethodInfo FailureMethod { get; set; }
	}

	internal sealed class LinearMethodFlowContract
	{
		internal LinearMethodFlowContract(
			MethodInfo method,
			IReadOnlyList<string> arguments,
			IReadOnlyList<FlowCallContract> calls)
		{
			Method = method;
			Arguments = arguments;
			Calls = calls;
		}

		internal MethodInfo Method { get; }
		internal IReadOnlyList<string> Arguments { get; }
		internal IReadOnlyList<FlowCallContract> Calls { get; }
		internal string ReturnToken { get; set; }
	}
}
