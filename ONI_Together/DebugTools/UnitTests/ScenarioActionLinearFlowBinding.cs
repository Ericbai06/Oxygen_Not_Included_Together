using System;
using System.Collections.Generic;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	internal sealed class ScenarioActionLinearFlowBinding
	{
		internal MethodInfo HostExecution { get; private set; }
		internal MethodInfo HostState { get; private set; }
		internal MethodInfo HostOracle { get; private set; }
		internal IReadOnlyList<ScenarioActionTransportStepBinding> HostTransport { get; private set; }
		internal MethodInfo CleanupExecution { get; private set; }
		internal MethodInfo CleanupMutation { get; private set; }
		internal MethodInfo CleanupOracle { get; private set; }
		internal IReadOnlyList<ScenarioActionTransportStepBinding> CleanupTransport { get; private set; }
		internal Type MutationType { get; private set; }

		internal static bool TryRead(
			object source, out ScenarioActionLinearFlowBinding value, out string failure)
		{
			value = new ScenarioActionLinearFlowBinding();
			var missing = new List<string>();
			value.HostExecution = Read<MethodInfo>(source, "HostExecutionMethod", missing);
			value.HostState = Read<MethodInfo>(source, "HostStateMethod", missing);
			value.HostOracle = Read<MethodInfo>(source, "HostOracleMethod", missing);
			value.CleanupExecution = Read<MethodInfo>(source, "CleanupExecutionMethod", missing);
			value.CleanupMutation = Read<MethodInfo>(source, "CleanupMutationMethod", missing);
			value.CleanupOracle = Read<MethodInfo>(source, "CleanupOracleMethod", missing);
			value.MutationType = Read<Type>(source, "MutationType", missing);
			value.HostTransport = ReadSteps(source, "HostTransportSteps", missing);
			value.CleanupTransport = ReadSteps(source, "CleanupTransportSteps", missing);
			failure = missing.Count == 0
				? null : "linear flow contract missing: " + string.Join(", ", missing);
			return failure == null;
		}

		private static T Read<T>(object source, string name, ICollection<string> missing)
			where T : class
		{
			T value = source.GetType().GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?.GetValue(source, null) as T;
			if (value == null) missing.Add(name);
			return value;
		}

		private static IReadOnlyList<ScenarioActionTransportStepBinding> ReadSteps(
			object source, string name, ICollection<string> missing)
		{
			if (source.GetType().GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?.GetValue(source, null) is not Array values || values.Length == 0)
			{
				missing.Add(name);
				return Array.Empty<ScenarioActionTransportStepBinding>();
			}
			var result = new List<ScenarioActionTransportStepBinding>();
			foreach (object item in values)
				if (!ScenarioActionTransportStepBinding.TryRead(item, out var step))
				{
					missing.Add(name + "[]");
					return Array.Empty<ScenarioActionTransportStepBinding>();
				}
				else result.Add(step);
			return result;
		}
	}

	internal sealed class ScenarioActionTransportStepBinding
	{
		internal Type PacketType { get; private set; }
		internal MethodInfo Factory { get; private set; }
		internal MethodInfo NetworkSend { get; private set; }
		internal MethodInfo ClientDispatch { get; private set; }
		internal MethodInfo ClientExecution { get; private set; }
		internal MethodInfo ClientApply { get; private set; }
		internal MethodInfo ClientOracle { get; private set; }

		internal static bool TryRead(object source, out ScenarioActionTransportStepBinding step)
		{
			step = new ScenarioActionTransportStepBinding
			{
				PacketType = Read<Type>(source, "PacketType"),
				Factory = Read<MethodInfo>(source, "FactoryMethod"),
				NetworkSend = Read<MethodInfo>(source, "NetworkSendMethod"),
				ClientDispatch = Read<MethodInfo>(source, "ClientDispatchMethod"),
				ClientExecution = Read<MethodInfo>(source, "ClientExecutionMethod"),
				ClientApply = Read<MethodInfo>(source, "ClientApplyMethod"),
				ClientOracle = Read<MethodInfo>(source, "ClientOracleMethod"),
			};
			return step.PacketType != null && step.Factory != null
			       && step.NetworkSend != null && step.ClientDispatch != null
			       && step.ClientExecution != null && step.ClientApply != null
			       && step.ClientOracle != null;
		}

		private static T Read<T>(object source, string name) where T : class
			=> source?.GetType().GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?.GetValue(source, null) as T;
	}

	internal sealed class ScenarioActionLinearFlowContext
	{
		internal ScenarioActionProductionBinding Source { get; set; }
		internal ScenarioActionExecutionExpectation Expected { get; set; }
		internal ExecutionContract Basic { get; set; }
		internal ScenarioActionLinearFlowBinding Flow { get; set; }
	}
}
