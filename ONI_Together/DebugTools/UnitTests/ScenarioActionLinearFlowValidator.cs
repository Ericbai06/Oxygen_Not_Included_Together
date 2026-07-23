using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class ScenarioActionLinearFlowValidator
	{
		internal static string Validate(
			ScenarioActionProductionBinding source,
			ScenarioActionExecutionExpectation expected,
			ExecutionContract basic)
		{
			if (!ScenarioActionLinearFlowBinding.TryRead(
				source, out ScenarioActionLinearFlowBinding flow, out string failure))
				return failure;
			var context = new ScenarioActionLinearFlowContext
			{
				Source = source, Expected = expected, Basic = basic, Flow = flow,
			};
			failure = ValidateSignatures(context) ?? ValidateEntrypoints(context);
			if (failure != null) return failure;
			LinearMethodFlowContract host = HostContract(context);
			failure = ScenarioActionConditionalSendValidator.Applies(context)
				? ScenarioActionConditionalSendValidator.ValidateHost(host)
				: LinearMethodFlowValidator.Validate(host);
			if (failure != null) return "host flow: " + failure;
			failure = ValidateClientFlows(flow.HostTransport)
			          ?? ValidateClientFlows(flow.CleanupTransport);
			if (failure != null) return failure;
			LinearMethodFlowContract cleanup = CleanupContract(context);
			failure = ScenarioActionConditionalSendValidator.Applies(context)
				? ScenarioActionConditionalSendValidator.ValidateCleanup(cleanup)
				: LinearMethodFlowValidator.Validate(cleanup);
			return failure == null ? null : "cleanup flow: " + failure;
		}

		private static string ValidateEntrypoints(ScenarioActionLinearFlowContext context)
		{
			var source = context.Source;
			var flow = context.Flow;
			if (!ReflectionExecutionGraph.Reaches(source.HandlerMethod, flow.HostExecution))
				return "handler does not execute the verified host flow";
			if (!ReflectionExecutionGraph.Reaches(source.HandlerMethod, flow.CleanupExecution))
				return "handler does not arm the verified cleanup flow";
			foreach (ScenarioActionTransportStepBinding step in
			         flow.HostTransport.Concat(flow.CleanupTransport))
			{
				if (!ReflectionExecutionGraph.ReachesPacketSender(step.NetworkSend))
					return step.PacketType.Name + " send does not execute PacketSender";
				if (step.ClientDispatch.DeclaringType != step.PacketType
				    || step.ClientDispatch.Name != "OnDispatched")
					return step.PacketType.Name + " dispatch is not OnDispatched";
			}
			return context.Expected.Scenario == "dlc-runtime"
				? ValidateDlcMetadata(source, context.Basic) : null;
		}

		private static LinearMethodFlowContract HostContract(
			ScenarioActionLinearFlowContext context)
		{
			ExecutionContract basic = context.Basic;
			var calls = new List<FlowCallContract>
			{
				C(basic.TargetPreparationMethod, "prepared", "command"),
				C(basic.TargetResolverMethod, "target", "prepared"),
			};
			if (context.Expected.Scenario == "dlc-runtime")
			{
				calls.Add(C(ReadMethod(context.Source, "FixtureAttachmentMethod"),
					"attached", "target"));
				calls.Add(C(basic.HostMutationMethod, "mutation", "attached",
					"literal:RobotIdleMonitor.idle", "literal:RobotIdleMonitor.working"));
			}
			else calls.Add(C(basic.HostMutationMethod, "mutation", "target"));
			calls.Add(C(context.Flow.HostState, "state", "mutation"));
			if (ScenarioActionConditionalSendValidator.Applies(context))
			{
				AddTransportCalls(calls, context.Flow.HostTransport, "mutation");
				calls.Add(C(context.Flow.HostOracle, null, "state"));
			}
			else
			{
				calls.Add(C(context.Flow.HostOracle, null, "state"));
				AddTransportCalls(calls, context.Flow.HostTransport, "mutation");
			}
			ScenarioActionConditionalSendValidator.BindHostOutcome(context, calls);
			return Contract(context.Flow.HostExecution, "mutation", calls);
		}

		private static LinearMethodFlowContract CleanupContract(
			ScenarioActionLinearFlowContext context)
		{
			var calls = new List<FlowCallContract>
			{
				C(context.Flow.CleanupMutation, "state", "mutation"),
			};
			if (ScenarioActionConditionalSendValidator.Applies(context))
			{
				AddTransportCalls(calls, context.Flow.CleanupTransport, "mutation");
				calls.Add(C(context.Flow.CleanupOracle, null, "state"));
			}
			else
			{
				calls.Add(C(context.Flow.CleanupOracle, null, "state"));
				AddTransportCalls(calls, context.Flow.CleanupTransport, "mutation");
			}
			ScenarioActionConditionalSendValidator.BindCleanupOutcome(context, calls);
			return Contract(context.Flow.CleanupExecution, "state", calls);
		}

		private static void AddTransportCalls(
			ICollection<FlowCallContract> calls,
			IReadOnlyList<ScenarioActionTransportStepBinding> steps,
			string sourceToken)
		{
			for (int index = 0; index < steps.Count; index++)
			{
				string packet = "packet:" + index;
				calls.Add(C(steps[index].Factory, packet, sourceToken));
				calls.Add(C(steps[index].NetworkSend, null, packet));
			}
		}

		private static string ValidateClientFlows(
			IReadOnlyList<ScenarioActionTransportStepBinding> steps)
		{
			foreach (ScenarioActionTransportStepBinding step in steps)
			{
				if (!ReflectionExecutionGraph.DirectlyPassesThisTo(
					step.ClientDispatch, step.ClientExecution))
					return step.PacketType.Name + " dispatch replaces the packet instance";
				var contract = Contract(step.ClientExecution, "state", new[]
				{
					C(step.ClientApply, "state", "packet"),
					C(step.ClientOracle, null, "state"),
				});
				string failure = LinearMethodFlowValidator.Validate(contract);
				if (failure != null) return step.PacketType.Name + " client flow: " + failure;
			}
			return null;
		}

		private static string ValidateSignatures(ScenarioActionLinearFlowContext context)
		{
			ExecutionContract basic = context.Basic;
			ScenarioActionLinearFlowBinding flow = context.Flow;
			if (!Signature(flow.HostExecution, flow.MutationType, 1)
			    || !Signature(flow.CleanupExecution, basic.StateType, 1))
				return "host/cleanup execution signatures are not normalized";
			if (!Unary(flow.HostState, flow.MutationType, basic.StateType)
			    || !Unary(flow.HostOracle, basic.StateType, typeof(void))
			    || !Unary(flow.CleanupMutation, flow.MutationType, basic.StateType)
			    || !Unary(flow.CleanupOracle, basic.StateType, typeof(void)))
				return "typed state/oracle or cleanup reversal signature is invalid";
			string failure = ValidateSequence(context);
			if (failure != null) return failure;
			Type sendResult = ScenarioActionConditionalSendValidator.Applies(context)
				? typeof(bool) : typeof(void);
			failure = ValidateTransportSignatures(
				flow.HostTransport, flow.MutationType, basic.StateType, sendResult)
			          ?? ValidateTransportSignatures(
				          flow.CleanupTransport, flow.MutationType, basic.StateType, sendResult);
			return failure ?? ValidateDomainSignatures(context);
		}

		private static string ValidateTransportSignatures(
			IReadOnlyList<ScenarioActionTransportStepBinding> steps,
			Type factoryInput,
			Type stateType,
			Type sendResult)
		{
			foreach (ScenarioActionTransportStepBinding step in steps)
				if (!Unary(step.Factory, factoryInput, step.PacketType)
				    || !Unary(step.NetworkSend, step.PacketType, sendResult)
				    || !Signature(step.ClientExecution, stateType, 1)
				    || !Unary(step.ClientApply, step.PacketType, stateType)
				    || !Unary(step.ClientOracle, stateType, typeof(void)))
					return step.PacketType.Name + " transport signatures are invalid";
			return null;
		}

		private static string ValidateSequence(ScenarioActionLinearFlowContext context)
		{
			string[] host = context.Flow.HostTransport
				.Select(step => step.PacketType.FullName).ToArray();
			string[] cleanup = context.Flow.CleanupTransport
				.Select(step => step.PacketType.FullName).ToArray();
			string[] expectedHost = ExpectedHost(context);
			string[] expectedCleanup = ExpectedCleanup(context);
			if (!host.SequenceEqual(expectedHost, StringComparer.Ordinal))
				return "host typed packet sequence is wrong";
			return cleanup.SequenceEqual(expectedCleanup, StringComparer.Ordinal)
				? null : "cleanup typed packet sequence is wrong";
		}

		private static string[] ExpectedHost(ScenarioActionLinearFlowContext context)
			=> context.Expected.Scenario == "pickup"
				? new[]
				{
					"ONI_Together.Networking.Packets.World.GroundItemPickedUpPacket",
					"ONI_Together.Networking.Packets.World.SpawnPrefabPacket",
				}
				: new[] { context.Basic.PacketType.FullName };

		private static string[] ExpectedCleanup(ScenarioActionLinearFlowContext context)
			=> context.Expected.Scenario == "pickup"
				? new[]
				{
					"ONI_Together.Networking.Packets.World.SpawnPrefabPacket",
					"ONI_Together.Networking.Packets.World.StorageItemPacket",
				}
				: new[] { context.Basic.PacketType.FullName };

		private static string ValidateDomainSignatures(ScenarioActionLinearFlowContext context)
		{
			ExecutionContract basic = context.Basic;
			Type command = context.Flow.HostExecution.GetParameters()[0].ParameterType;
			if (!Unary(basic.TargetPreparationMethod, command,
				basic.TargetPreparationMethod.ReturnType))
				return "target preparation signature is invalid";
			if (!Unary(basic.TargetResolverMethod,
				basic.TargetPreparationMethod.ReturnType, basic.TargetType))
				return "target resolver signature is invalid";
			if (context.Source.Scenario == "pickup")
			{
				string failure = ScenarioActionMutationShapeValidator.ValidatePickup(
					context.Flow.MutationType);
				if (failure != null) return failure;
			}
			if (context.Source.Scenario == "dlc-runtime")
				return Signature(basic.HostMutationMethod, context.Flow.MutationType, 3)
					? null : "DLC transition signature is invalid";
			return Unary(basic.HostMutationMethod, basic.TargetType, context.Flow.MutationType)
				? null : "host mutation signature is invalid";
		}

		private static string ValidateDlcMetadata(
			ScenarioActionProductionBinding source, ExecutionContract basic)
		{
			if (ReadText(source, "SafeFromState") != "RobotIdleMonitor.idle"
			    || ReadText(source, "SafeToState") != "RobotIdleMonitor.working")
				return "DLC safe From/To states are not explicit";
			MethodInfo attach = ReadMethod(source, "FixtureAttachmentMethod");
			Type fixture = source.GetType().Assembly.GetType(
				"ONI_Together.Networking.DlcRuntimeProfileFixture");
			return fixture != null && ReflectionExecutionGraph.ReachesType(attach, fixture)
			       && Unary(attach, basic.TargetType, basic.TargetType)
				? null : "DLC fixture path is not attached to the same typed target";
		}

		private static LinearMethodFlowContract Contract(
			MethodInfo method, string returned, IReadOnlyList<FlowCallContract> calls)
			=> new(method, new[] { method.GetParameters()[0].Name }, calls)
			{
				ReturnToken = returned,
			};

		private static FlowCallContract C(
			MethodInfo method, string output, params string[] inputs)
			=> new(method, output, inputs);

		private static MethodInfo ReadMethod(object source, string name)
			=> source.GetType().GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?.GetValue(source, null) as MethodInfo;

		private static string ReadText(object source, string name)
			=> source.GetType().GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?.GetValue(source, null) as string;

		private static bool Unary(MethodInfo method, Type input, Type output)
			=> Signature(method, output, 1)
			   && method.GetParameters()[0].ParameterType == input;

		private static bool Signature(MethodInfo method, Type output, int parameters)
			=> method != null && method.IsStatic && method.ReturnType == output
			   && method.GetParameters().Length == parameters;
	}
}
