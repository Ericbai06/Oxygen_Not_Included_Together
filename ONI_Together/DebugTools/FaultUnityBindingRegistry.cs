#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.Networking.Packets.Tools.Build;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Patches.Bionics;
using ONI_Together.Patches.DLC.Aquatic;
using ONI_Together.Patches.DLC.Bionic;
using ONI_Together.Patches.DLC.Common;
using ONI_Together.Patches.DLC.Frosty;
using ONI_Together.Patches.DLC.Prehistoric;
using ONI_Together.Patches.DLC.SpacedOut;
using ONI_Together.Patches.Duplicant;
using ONI_Together.Patches.GamePatches;
using ONI_Together.Patches.ToolPatches.Build;
using ONI_Together.Patches.World;
using ONI_Together.Scripts.Duplicants;

namespace ONI_Together.DebugTools
{
	public sealed class FaultUnityProductionBinding
	{
		internal FaultUnityProductionBinding(
			string caseId, MethodInfo callsite, MethodInfo gate, string mutation,
			Type concreteTargetType = null)
		{
			CaseId = caseId;
			RuntimeCallsite = callsite;
			GateMethod = gate;
			InputMutation = mutation;
			ConcreteRuntimeTargetType = concreteTargetType;
		}

		public string CaseId { get; }
		public MethodInfo RuntimeCallsite { get; }
		public MethodInfo GateMethod { get; }
		public string InputMutation { get; }
		public MethodInfo ExecutionMethod => Method(typeof(FaultUnityRuntimeDriver), "ExecuteLifecycle");
		public MethodInfo FaultExecutionMethod => Method(typeof(FaultUnityRuntimeDriver), "ExecuteLifecycle");
		public MethodInfo CleanExecutionMethod => Method(typeof(FaultUnityRuntimeDriver), "ExecuteCleanPhase");
		public MethodInfo SetupMethod => Method(typeof(FaultUnityRuntimeStages), "Setup");
		public MethodInfo TriggerMethod => Method(typeof(FaultUnityRuntimeStages), "Trigger");
		public MethodInfo ReceiptBarrierMethod => Method(typeof(FaultUnityRuntimeStages), "ReceiptBarrier");
		public MethodInfo SnapshotMethod => Method(typeof(FaultUnityRuntimeStages), "Snapshot");
		public MethodInfo OracleMethod => Method(typeof(FaultUnityRuntimeStages), "Oracle");
		public MethodInfo ValidationMethod => Method(typeof(FaultUnityRuntimeStages), "Validate");
		public MethodInfo ResetMethod => Method(typeof(FaultUnityRuntimeStages), "Reset");
		public MethodInfo CleanControlTriggerMethod => Method(typeof(FaultUnityRuntimeStages), "CleanControlTrigger");
		public MethodInfo DisposableFixtureCreateMethod =>
			Method(typeof(FaultDeferredDestroyRuntime), "CreateDisposableFixture");
		public MethodInfo DisposableFixtureIdentityMethod =>
			Method(typeof(FaultDeferredDestroyRuntime), "DisposableFixtureIdentity");
		public MethodInfo DeferredDestroyBarrierMethod =>
			Method(typeof(FaultDeferredDestroyRuntime), "DeferredDestroyBarrier");
		public MethodInfo ReplacementRecreateMethod =>
			Method(typeof(FaultDeferredDestroyRuntime), "ReplacementRecreate");
		public MethodInfo ReplacementRebindMethod =>
			Method(typeof(FaultDeferredDestroyRuntime), "ReplacementRebind");
		public MethodInfo BaselineRestoreMethod =>
			Method(typeof(FaultDeferredDestroyRuntime), "BaselineRestore");
		public MethodInfo ReplacementCleanControlMethod =>
			Method(typeof(FaultDeferredDestroyRuntime), "ReplacementCleanControl");
		public MethodInfo DeferredDestroyValidationMethod =>
			Method(typeof(FaultDeferredDestroyRuntime), "Validate");
		public Type ConcreteRuntimeTargetType { get; }
		public Type OracleTargetType => typeof(DlcRuntimeTarget);
		public Type OracleStateType => typeof(DlcRuntimeState);
		public string ReceiptId => "fault-receipt:" + CaseId;
		public string CleanControlReceiptId => "fault-clean-receipt:" + CaseId;

		private static MethodInfo Method(Type owner, string name)
			=> owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
	}

	public static class FaultUnityBindingRegistry
	{
		private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance
		                                         | BindingFlags.Public | BindingFlags.NonPublic;

		public static readonly IReadOnlyList<FaultUnityProductionBinding> Bindings = Build();

		internal static FaultInjectionReceipt ExecuteFault(string caseId)
		{
			Definition(caseId);
			return FaultUnityRuntimeDriver.ExecuteFault(Bindings.Single(item => item.CaseId == caseId));
		}

		internal static FaultInjectionReceipt ExecuteCleanControl(string caseId)
		{
			Definition(caseId);
			return FaultUnityRuntimeDriver.ExecuteCleanControl(Bindings.Single(item => item.CaseId == caseId));
		}

		private static IReadOnlyList<FaultUnityProductionBinding> Build()
		{
			return new[]
			{
				B("duplicant.personality-missing", typeof(ImmigrantOptionEntry), "ToGameDeliverable", "MissingPersonality", "personality=null"),
				B("duplicant.set-minion-before-controller", typeof(ImmigrantScreenPatch), "ApplyOptionsToScreen", "MinionBeforeController", "order=SetMinion-before-SetController"),
				B("duplicant.preview-flatulence", typeof(Flatulence_Patch.Flatulence_Emit_Patch), "Prefix", "PreviewFlatulence", "preview=true"),
				B("duplicant.destroyed-add-component", typeof(MinionMultiplayerInitializer), "FinalizeInit", "DestroyedMinionObject", "gameObject=destroyed"),
				B("work.workable-unregistered", typeof(WorkableProgressPacket), "TryApplyWorkableProgress", "UnregisteredWorkable", "workableRegistered=false"),
				B("work.target-missing", typeof(WorkableProgressPacket), "TryApplyWorkableProgress", "MissingWorkTarget", "target=null"),
				B("work.original-dig-element-null", typeof(DiggableStartWorkFaultPatch), "Prefix", "MissingOriginalDigElement", "originalDigElement=null"),
				B("work.client-native-start", typeof(WorkableStartWorkAuthorityPatch), "Prefix", "ClientNativeStart", "clientNativeStart=true"),
				B("building.selected-elements-null", typeof(global::ConstructablePatch), "Capture", "MissingSelectedElements", "selectedElementsTags=null"),
			B("building.destroy-deferred", typeof(BuildRuntimeAdapter), "TryGetReplacement", "DeferredReplacementDestroy", "destroyDeferred=true"),
				B("dlc.prefab-missing", typeof(SpawnPrefabPacket), "CreateAuthoritativeObject", "MissingDlcPrefab", "prefab=null"),
				B("dlc.state-before-start-sm", typeof(BionicPatches.BionicOilMonitor_Instance_StartSM_Patch), "Prefix", "StateBeforeStartSm", "stateBeforeStartSM=true"),
				B("dlc.family-aquatic", typeof(MinnowPoiPrefabIdentityPatch), "Postfix", "AquaticFamily", "dlcFamily=Aquatic", typeof(MinnowImperativePOIAConfig)),
				B("dlc.family-bionic", typeof(ElectrobankSim1000Patch), "Prefix", "BionicFamily", "dlcFamily=Bionic", typeof(Electrobank)),
				B("dlc.family-frosty", typeof(GeothermalControllerButtonPatch), "Prefix", "FrostyFamily", "dlcFamily=Frosty", typeof(GeothermalController.StatesInstance)),
				B("dlc.family-prehistoric", typeof(LargeImpactorStartSmPatch), "Postfix", "PrehistoricFamily", "dlcFamily=Prehistoric", typeof(LargeImpactorStatus.Instance)),
				B("dlc.family-spaced-out", typeof(ArtifactPoiIdentityPatch), "Postfix", "SpacedOutFamily", "dlcFamily=SpacedOut", typeof(ArtifactPOIConfig)),
				B("dlc.family-common", typeof(PoiTechSync.CompleteWorkPatch), "Postfix", "CommonFamily", "dlcFamily=Common", typeof(POITechItemUnlockWorkable)),
			};
		}

		private static FaultUnityProductionBinding B(
			string id, Type owner, string callsite, string gate, string mutation,
			Type concreteTargetType = null)
			=> new FaultUnityProductionBinding(
				id, UniqueMethod(owner, callsite),
				UniqueMethod(typeof(ProductionFaultInputGates), gate), mutation,
				concreteTargetType);

		private static MethodInfo UniqueMethod(Type owner, string name)
		{
			MethodInfo[] methods = owner.GetMethods(All)
				.Where(method => method.Name == name).ToArray();
			if (methods.Length != 1)
				throw new InvalidOperationException(
					$"Expected one {owner.FullName}.{name}, found {methods.Length}");
			return methods[0];
		}

		private static FaultInjectionCase Definition(string caseId)
		{
			FaultInjectionCase definition = FaultInjectionRegistry.Cases
				.SingleOrDefault(item => item.Id == caseId);
			if (definition == null || definition.ExecutionTier == "headless")
				throw new KeyNotFoundException("Unknown Unity fault case " + caseId);
			return definition;
		}
	}
}
#endif
