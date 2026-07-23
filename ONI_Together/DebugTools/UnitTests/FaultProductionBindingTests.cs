using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultProductionBindingTests
	{
		private static readonly IReadOnlyDictionary<string, string> ExpectedGateSymbols =
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["work.revision-stale"] = "ONI_Together.Networking.NetworkIdentityRegistry.TryAcceptStateRevision",
				["building.complete-before-queued"] = "ONI_Together.Networking.Packets.Tools.Build.BuildLifecycleAdmission.CanComplete",
				["building.finish-duplicate"] = "ONI_Together.Networking.NetworkIdentityRegistry.ShouldAcceptLifecycleRevision",
				["building.net-id-collision"] = "ONI_Together.Networking.NetworkIdentityRegistry.CanRegisterExisting",
				["inventory.storage-missing"] = "ONI_Together.Networking.Packets.World.StorageItemPacket.CanApplyResolvedTransfer",
				["inventory.item-missing"] = "ONI_Together.Networking.Packets.World.StorageItemPacket.CanApplyResolvedTransfer",
				["inventory.membership-wrong"] = "ONI_Together.Networking.Packets.World.StorageItemPacket.CanApplyResolvedTransfer",
				["inventory.mass-zero"] = "ONI_Together.Networking.Packets.World.StorageItemPacket.CanApplyResolvedTransfer",
				["inventory.delta-duplicate"] = "ONI_Together.Networking.Packets.World.StorageItemPacket.ShouldApplyRevision",
				["inventory.delta-out-of-order"] = "ONI_Together.Networking.Packets.World.StorageItemPacket.ShouldApplyRevision",
				["entity.state-before-identity"] = "ONI_Together.Networking.NetworkIdentityRegistry.CanApplyDomainState",
				["entity.despawn-before-spawn"] = "ONI_Together.Networking.NetworkIdentityRegistry.TryAcceptLifecycleRevision",
				["entity.spawn-after-tombstone"] = "ONI_Together.Networking.NetworkIdentityRegistry.ShouldAcceptLifecycleRevision",
				["entity.prefab-null"] = "ONI_Together.Networking.NetworkIdentityRegistry.CanAdmitPrefab",
				["dlc.fingerprint-mismatch"] = "ONI_Together.Networking.ProtocolCompatibility.MatchesValues",
				["reconnect.session-stale"] = "ONI_Together.Networking.ReadyReplayAssembly.AcceptBatch",
				["reconnect.connection-stale"] = "ONI_Together.Networking.ReadyReplayAssembly.AcceptBatch",
				["reconnect.snapshot-stale"] = "ONI_Together.Networking.ReadyReplayAssembly.AcceptCommit",
				["reconnect.batch-missing"] = "ONI_Together.Networking.ReadyReplayAssembly.TryBeginApply",
				["reconnect.batch-duplicate"] = "ONI_Together.Networking.ReadyReplayAssembly.AcceptBatch",
				["reconnect.ack-lost"] = "ONI_Together.Networking.ReadyManager.ShouldRetryReadyAcceptance",
				["reconnect.disconnect-mid-apply"] = "ONI_Together.Networking.ReadyReplayAssembly.ShouldRollbackApply",
			};

		[UnitTest(name: "Fault injection: every headless case binds a production gate",
			category: "Integration")]
		public static UnitTestResult EveryHeadlessCaseBindsProductionGate()
		{
			string[] expected = HeadlessCaseIds();
			IReadOnlyList<FaultProductionBinding> bindings = FaultProductionBindingRegistry.Bindings;
			string[] actual = bindings.Select(binding => binding.CaseId)
				.OrderBy(id => id, StringComparer.Ordinal).ToArray();
			if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
				return UnitTestResult.Fail("Production gate bindings do not exactly cover headless faults");

			foreach (FaultProductionBinding binding in bindings)
			{
				string expectedSymbol = ExpectedGateSymbols[binding.CaseId];
				string actualSymbol = ProductionSymbol(binding.GateMethod);
				if (expectedSymbol != actualSymbol)
					return UnitTestResult.Fail($"Expected production gate {expectedSymbol}, actual {actualSymbol}");
				string invalid = InvalidRuntimeSymbol(binding.GateMethod, "gate");
				if (invalid != null) return UnitTestResult.Fail(binding.CaseId + ": " + invalid);
				invalid = InvalidRuntimeSymbol(binding.RuntimeCallsite, "runtime callsite");
				if (invalid != null) return UnitTestResult.Fail(binding.CaseId + ": " + invalid);
			}

			return UnitTestResult.Pass("Every headless fault binds a non-DebugTools production gate");
		}

		[UnitTest(name: "Fault injection: runtime callsites use bound production gates",
			category: "Integration")]
		public static UnitTestResult RuntimeCallsitesDirectlyUseBoundGates()
		{
			foreach (FaultProductionBinding binding in FaultProductionBindingRegistry.Bindings)
			{
				if (binding.RuntimeCallsite == binding.GateMethod)
					return UnitTestResult.Fail(binding.CaseId + ": gate cannot cite itself as runtime use");
				if (!ReferencesMethod(binding.RuntimeCallsite, binding.GateMethod))
					return UnitTestResult.Fail(binding.CaseId + ": runtime callsite does not call bound gate");
			}

			return UnitTestResult.Pass("Every bound gate is directly consumed by actual runtime code");
		}

		[UnitTest(name: "Fault injection: headless controller dispatches through production bindings",
			category: "Integration")]
		public static UnitTestResult ControllerDispatchesThroughProductionBindings()
		{
			MethodInfo dispatch = typeof(FaultInjectionController).GetMethod(
				"Dispatch", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo productionExecute = typeof(FaultProductionBindingRegistry).GetMethod(
				"ExecuteHeadless", BindingFlags.Static | BindingFlags.NonPublic);
			if (dispatch == null || productionExecute == null)
				return UnitTestResult.Fail("Expected controller dispatch and production execution seams");
			if (!ReferencesMethod(dispatch, productionExecute))
				return UnitTestResult.Fail("Fault controller still bypasses production gate bindings");

			foreach (string caseId in HeadlessCaseIds())
			{
				FaultProductionBinding binding = FaultProductionBindingRegistry.Resolve(caseId);
				FaultInjectionExecution execution = FaultInjectionController.ExecuteHeadless(caseId);
				string expected = ProductionSymbol(binding.GateMethod);
				if (expected != execution.ProductionGateSymbol)
					return UnitTestResult.Fail($"Expected production gate {expected}, actual {execution.ProductionGateSymbol}");
			}

			return UnitTestResult.Pass("Headless execution receipts identify the production gate executed");
		}

		[UnitTest(name: "Fault injection: probes contain no shadow state machines",
			category: "Integration")]
		public static UnitTestResult ProbesContainNoShadowStateMachines()
		{
			Type[] shadows = typeof(FaultInjectionController).Assembly.GetTypes()
				.Where(type => type.Namespace == "ONI_Together.DebugTools"
				               && type.Name.IndexOf("Fault", StringComparison.Ordinal) >= 0
				               && type.Name.EndsWith("Probe", StringComparison.Ordinal))
				.SelectMany(type => type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
				.Where(type => !type.IsEnum && !typeof(Delegate).IsAssignableFrom(type))
				.ToArray();
			return shadows.Length == 0
				? UnitTestResult.Pass("Fault probes contain no local replacement domain models")
				: UnitTestResult.Fail("Shadow state types found: " + string.Join(", ",
					shadows.Select(type => type.FullName)));
		}

		private static string[] HeadlessCaseIds()
			=> FaultInjectionMatrixFixture.Cases
				.Where(item => item.ExecutionTier == "headless")
				.Select(item => item.Id)
				.OrderBy(id => id, StringComparer.Ordinal).ToArray();

		private static string InvalidRuntimeSymbol(MethodInfo method, string label)
		{
			if (method == null) return label + " is missing";
			Type owner = method.DeclaringType;
			if (owner == null) return label + " has no declaring type";
			if (method.Module.Assembly != typeof(FaultInjectionController).Assembly)
				return label + " belongs to another assembly";
			if (owner.Namespace == null || !owner.Namespace.StartsWith(
				    "ONI_Together.Networking", StringComparison.Ordinal))
				return label + " is outside the networking runtime";
			if (owner.FullName.IndexOf("DebugTools", StringComparison.Ordinal) >= 0
			    || owner.Name.IndexOf("Fault", StringComparison.Ordinal) >= 0
			    || owner.Name.IndexOf("Probe", StringComparison.Ordinal) >= 0)
				return label + " is a debug-only replacement";
			return null;
		}

		private static bool ReferencesMethod(MethodInfo caller, MethodInfo callee)
		{
			byte[] il = caller.GetMethodBody()?.GetILAsByteArray();
			if (il == null) return false;
			for (int offset = 0; offset <= il.Length - 5; offset++)
			{
				bool methodCall = il[offset] == 0x28 || il[offset] == 0x6f;
				if (methodCall && BitConverter.ToInt32(il, offset + 1) == callee.MetadataToken)
					return true;
			}
			return false;
		}

		private static string ProductionSymbol(MethodInfo method)
			=> method.DeclaringType.FullName + "." + method.Name;
	}
}
