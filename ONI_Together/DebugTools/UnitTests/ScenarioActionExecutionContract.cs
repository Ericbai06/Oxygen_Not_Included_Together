using System;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	internal sealed class ExecutionContract
	{
		internal ExecutionContract(
			string rule, Type packet, Type target, Type state, MethodInfo[] methods)
		{
			DeterministicTargetRule = rule;
			PacketType = packet;
			TargetType = target;
			StateType = state;
			TargetPreparationMethod = methods[0];
			TargetResolverMethod = methods[1];
			HostMutationMethod = methods[2];
			NetworkEmitterMethod = methods[3];
			ClientDispatchMethod = methods[4];
			ClientApplyMethod = methods[5];
			TypedOracleMethod = methods[6];
			CleanupMethod = methods[7];
			ProductionMethods = methods.Skip(2).ToArray();
		}

		internal string DeterministicTargetRule { get; }
		internal Type PacketType { get; }
		internal Type TargetType { get; }
		internal Type StateType { get; }
		internal MethodInfo TargetPreparationMethod { get; }
		internal MethodInfo TargetResolverMethod { get; }
		internal MethodInfo HostMutationMethod { get; }
		internal MethodInfo NetworkEmitterMethod { get; }
		internal MethodInfo ClientDispatchMethod { get; }
		internal MethodInfo ClientApplyMethod { get; }
		internal MethodInfo TypedOracleMethod { get; }
		internal MethodInfo CleanupMethod { get; }
		internal MethodInfo[] ProductionMethods { get; }
	}
}
