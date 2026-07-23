using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultUnityProductionBindingTests
	{
		private static readonly IReadOnlyDictionary<string, (string Callsite, string Mutation)> Expected =
			new Dictionary<string, (string, string)>(StringComparer.Ordinal)
			{
				["duplicant.personality-missing"] = ("ONI_Together.Networking.Packets.Social.ImmigrantOptionEntry.ToGameDeliverable", "personality=null"),
				["duplicant.set-minion-before-controller"] = ("ONI_Together.Patches.GamePatches.ImmigrantScreenPatch.ApplyOptionsToScreen", "order=SetMinion-before-SetController"),
				["duplicant.preview-flatulence"] = ("ONI_Together.Patches.Duplicant.Flatulence_Patch+Flatulence_Emit_Patch.Prefix", "preview=true"),
				["duplicant.destroyed-add-component"] = ("ONI_Together.Scripts.Duplicants.MinionMultiplayerInitializer.FinalizeInit", "gameObject=destroyed"),
				["work.workable-unregistered"] = ("ONI_Together.Networking.Packets.World.WorkableProgressPacket.TryApplyWorkableProgress", "workableRegistered=false"),
				["work.target-missing"] = ("ONI_Together.Networking.Packets.World.WorkableProgressPacket.TryApplyWorkableProgress", "target=null"),
				["work.original-dig-element-null"] = ("ONI_Together.Patches.Duplicant.DiggableStartWorkFaultPatch.Prefix", "originalDigElement=null"),
				["work.client-native-start"] = ("ONI_Together.Patches.World.WorkableStartWorkAuthorityPatch.Prefix", "clientNativeStart=true"),
				["building.selected-elements-null"] = ("ConstructablePatch.Capture", "selectedElementsTags=null"),
				["building.destroy-deferred"] = ("ONI_Together.Networking.Packets.Tools.Build.BuildAuthority.TryGetReplacement", "destroyDeferred=true"),
				["dlc.prefab-missing"] = ("ONI_Together.Networking.Packets.World.SpawnPrefabPacket.CreateAuthoritativeObject", "prefab=null"),
				["dlc.state-before-start-sm"] = ("ONI_Together.Patches.Bionics.BionicPatches+BionicOilMonitor_Instance_StartSM_Patch.Prefix", "stateBeforeStartSM=true"),
				["dlc.family-aquatic"] = ("ONI_Together.Patches.DLC.Aquatic.MinnowPoiPrefabIdentityPatch.Postfix", "dlcFamily=Aquatic"),
				["dlc.family-bionic"] = ("ONI_Together.Patches.DLC.Bionic.ElectrobankSim1000Patch.Prefix", "dlcFamily=Bionic"),
				["dlc.family-frosty"] = ("ONI_Together.Patches.DLC.Frosty.GeothermalControllerButtonPatch.Prefix", "dlcFamily=Frosty"),
				["dlc.family-prehistoric"] = ("ONI_Together.Patches.DLC.Prehistoric.LargeImpactorStartSmPatch.Postfix", "dlcFamily=Prehistoric"),
				["dlc.family-spaced-out"] = ("ONI_Together.Patches.DLC.SpacedOut.ArtifactPoiIdentityPatch.Postfix", "dlcFamily=SpacedOut"),
				["dlc.family-common"] = ("ONI_Together.Patches.DLC.Common.PoiTechSync+CompleteWorkPatch.Postfix", "dlcFamily=Common"),
			};

		[UnitTest(name: "Fault injection: Unity cases bind exact production callsites",
			category: "Integration")]
		public static UnitTestResult UnityCasesBindExactProductionCallsites()
		{
			IReadOnlyList<FaultUnityProductionBinding> bindings = FaultUnityBindingRegistry.Bindings;
			string[] expectedIds = Expected.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
			string[] actualIds = bindings.Select(binding => binding.CaseId)
				.OrderBy(id => id, StringComparer.Ordinal).ToArray();
			if (!expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal))
				return UnitTestResult.Fail("Unity production bindings do not exactly cover 18 cases");

			foreach (FaultUnityProductionBinding binding in bindings)
			{
				(string expectedCallsite, string expectedMutation) = Expected[binding.CaseId];
				string invalid = InvalidProductionMethod(binding.RuntimeCallsite);
				if (invalid != null) return UnitTestResult.Fail(binding.CaseId + ": " + invalid);
				string actualCallsite = ProductionSymbol(binding.RuntimeCallsite);
				if (expectedCallsite != actualCallsite)
					return UnitTestResult.Fail($"Expected callsite {expectedCallsite}, actual {actualCallsite}");
				if (expectedMutation != binding.InputMutation)
					return UnitTestResult.Fail($"Expected mutation {expectedMutation}, actual {binding.InputMutation}");
				invalid = InvalidProductionMethod(binding.GateMethod);
				if (invalid != null) return UnitTestResult.Fail(binding.CaseId + ": " + invalid);
				if (!typeof(IFaultInputMutation).IsAssignableFrom(binding.GateMethod.ReturnType))
					return UnitTestResult.Fail(binding.CaseId + ": gate does not return a typed input mutation");
				if (!binding.GateMethod.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
					return UnitTestResult.Fail(binding.CaseId + ": gate cannot alter the declared seam input");
				if (!ReferencesMethod(binding.RuntimeCallsite, binding.GateMethod))
					return UnitTestResult.Fail(binding.CaseId + ": production callsite bypasses typed fault gate");
			}
			return UnitTestResult.Pass("All Unity faults alter inputs through pinned runtime callsites");
		}

		[UnitTest(name: "Fault injection: Unity gates consume once and emit receipts",
			category: "Integration")]
		public static UnitTestResult UnityGatesConsumeAndEmitReceipts()
		{
			MethodInfo consume = typeof(FaultInjectionUnitySeams).GetMethod(
				"Consume", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo emit = typeof(FaultInjectionUnitySeams).GetMethod(
				"EmitReceipt", BindingFlags.Static | BindingFlags.NonPublic);
			if (consume == null || emit == null)
				return UnitTestResult.Fail("Typed consume and receipt seams are required");

			foreach (FaultUnityProductionBinding binding in FaultUnityBindingRegistry.Bindings)
			{
				if (!ReferencesMethod(binding.GateMethod, consume))
					return UnitTestResult.Fail(binding.CaseId + ": gate does not consume armed fault");
				if (!ReferencesMethod(binding.RuntimeCallsite, emit))
					return UnitTestResult.Fail(binding.CaseId + ": production callsite does not emit a fault receipt");
				if ("fault-receipt:" + binding.CaseId != binding.ReceiptId)
					return UnitTestResult.Fail(binding.CaseId + ": fault receipt ID is not fixed");
				if ("fault-clean-receipt:" + binding.CaseId != binding.CleanControlReceiptId)
					return UnitTestResult.Fail(binding.CaseId + ": clean-control receipt ID is not fixed");
			}
			return UnitTestResult.Pass("Each Unity gate consumes once and publishes paired receipts");
		}

		[UnitTest(name: "Fault injection: handlers execute bindings instead of arm-only",
			category: "Integration")]
		public static UnitTestResult HandlersExecuteBindingsInsteadOfArmOnly()
		{
			MethodInfo executeFault = typeof(FaultUnityBindingRegistry).GetMethod(
				"ExecuteFault", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo executeClean = typeof(FaultUnityBindingRegistry).GetMethod(
				"ExecuteCleanControl", BindingFlags.Static | BindingFlags.NonPublic);
			if (executeFault == null || executeClean == null)
				return UnitTestResult.Fail("Executable fault and clean-control dispatchers are required");

			foreach (string caseId in Expected.Keys)
			{
				if (!FaultInjectionHandlerRegistry.TryResolve(caseId, out FaultInjectionHandler fault)
				    || !ReferencesMethod(fault.Method, executeFault))
					return UnitTestResult.Fail(caseId + ": fault handler only arms or waits");
				if (!FaultInjectionHandlerRegistry.TryResolveCleanControl(caseId, out FaultInjectionHandler clean)
				    || !ReferencesMethod(clean.Method, executeClean))
					return UnitTestResult.Fail(caseId + ": clean-control handler is not executable");
			}
			return UnitTestResult.Pass("Handlers execute production-bound fault and clean-control paths");
		}

		[UnitTest(name: "Fault injection: driver commands are fixed and fail closed",
			category: "Integration")]
		public static UnitTestResult DriverCommandsAreFixedAndFailClosed()
		{
			foreach (string caseId in Expected.Keys)
			{
				string expectedFault = "fault-inject:" + caseId;
				string expectedClean = "fault-clean:" + caseId;
				if (expectedFault != FaultInjectionDriverRegistry.FaultCommands[caseId])
					return UnitTestResult.Fail(caseId + ": fault driver command mismatch");
				if (expectedClean != FaultInjectionDriverRegistry.CleanControlCommands[caseId])
					return UnitTestResult.Fail(caseId + ": clean-control driver command mismatch");
				if (!FaultInjectionCommand.TryParse(expectedFault, out FaultInjectionCommand fault)
				    || fault.CaseId != caseId || fault.IsCleanControl)
					return UnitTestResult.Fail(caseId + ": fault driver command does not parse exactly");
				if (!FaultInjectionCommand.TryParse(expectedClean, out FaultInjectionCommand clean)
				    || clean.CaseId != caseId || !clean.IsCleanControl)
					return UnitTestResult.Fail(caseId + ": clean-control command does not parse exactly");

				DebugCommandOutcome outcome = DebugMenu.ExecuteAutomationCommandForTests(expectedFault);
				if (outcome.Command != expectedFault || IsArmOnly(outcome.Reason))
					return UnitTestResult.Fail(caseId + ": driver did not reach executable injection");
			}
			return UnitTestResult.Pass("Fault and clean-control commands map to executable production bindings");
		}

		private static bool IsArmOnly(string reason)
			=> reason != null && (reason == "unknown-command" || reason == "invalid-fault-injection"
			   || reason.IndexOf("waiting-for-production-seam", StringComparison.Ordinal) >= 0
			   || reason.IndexOf("fault-armed", StringComparison.Ordinal) >= 0);

		private static string InvalidProductionMethod(MethodInfo method)
		{
			Type owner = method?.DeclaringType;
			if (owner == null) return "typed gate has no production owner";
			if (method.Module.Assembly != typeof(FaultInjectionController).Assembly)
				return "typed gate belongs to another assembly";
			if (owner.Namespace == "ONI_Together.DebugTools"
			    || owner.Name.IndexOf("Probe", StringComparison.Ordinal) >= 0)
				return "typed gate is a DebugTools replacement";
			return null;
		}

		private static bool ReferencesMethod(MethodInfo caller, MethodInfo callee)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			if (il == null || callee == null) return false;
			for (int offset = 0; offset <= il.Length - 5; offset++)
				if ((il[offset] == 0x28 || il[offset] == 0x6f)
				    && BitConverter.ToInt32(il, offset + 1) == callee.MetadataToken) return true;
			return false;
		}

		private static string ProductionSymbol(MethodInfo method)
			=> method.DeclaringType.FullName + "." + method.Name;
	}
}
