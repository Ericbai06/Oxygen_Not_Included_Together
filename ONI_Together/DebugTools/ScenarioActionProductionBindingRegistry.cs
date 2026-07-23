using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.DuplicantActions;
using ONI_Together.Networking.Packets.DLC.SpacedOut;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Synchronization;
using ONI_Together.Patches.Duplicant;
using ONI_Together.Patches.DLC.SpacedOut;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal sealed class ScenarioActionProductionBinding
	{
		internal string Scenario { get; }
		internal MethodInfo HandlerMethod { get; }
		internal MethodInfo RuntimeCallsite { get; }
		internal MethodInfo RuntimeMethod { get; }
		internal MethodInfo TargetPreparationMethod { get; private set; }
		internal MethodInfo TargetResolverMethod { get; private set; }
		internal MethodInfo HostMutationMethod { get; private set; }
		internal MethodInfo NetworkEmitterMethod { get; private set; }
		internal MethodInfo ClientDispatchMethod { get; private set; }
		internal MethodInfo ClientApplyMethod { get; private set; }
		internal MethodInfo TypedOracleMethod { get; private set; }
		internal MethodInfo CleanupMethod { get; private set; }
		internal string DeterministicTargetRule { get; private set; }
		internal Type PacketType { get; private set; }
		internal Type TargetType { get; private set; }
		internal Type StateType { get; private set; }
		internal MethodInfo FixtureAttachmentMethod { get; private set; }
		internal string SafeFromState { get; private set; }
		internal string SafeToState { get; private set; }
		internal MethodInfo HostExecutionMethod { get; private set; }
		internal MethodInfo HostStateMethod { get; private set; }
		internal MethodInfo HostOracleMethod { get; private set; }
		internal MethodInfo CleanupExecutionMethod { get; private set; }
		internal MethodInfo CleanupMutationMethod { get; private set; }
		internal MethodInfo CleanupOracleMethod { get; private set; }
		internal Type MutationType { get; private set; }
		internal ScenarioActionTransportBinding[] HostTransportSteps { get; private set; }
		internal ScenarioActionTransportBinding[] CleanupTransportSteps { get; private set; }

		internal ScenarioActionProductionBinding(
			string scenario,
			MethodInfo handlerMethod,
			MethodInfo runtimeMethod)
		{
			Scenario = scenario;
			HandlerMethod = handlerMethod;
			RuntimeCallsite = handlerMethod;
			RuntimeMethod = runtimeMethod;
		}

		internal ScenarioActionProductionBinding WithExecution(
			string rule, Type packet, Type target, Type state,
			MethodInfo prepare, MethodInfo resolve, MethodInfo mutate,
			MethodInfo emit, MethodInfo dispatch, MethodInfo apply,
			MethodInfo oracle, MethodInfo cleanup)
		{
			DeterministicTargetRule = rule;
			PacketType = packet;
			TargetType = target;
			StateType = state;
			TargetPreparationMethod = prepare;
			TargetResolverMethod = resolve;
			HostMutationMethod = mutate;
			NetworkEmitterMethod = emit;
			ClientDispatchMethod = dispatch;
			ClientApplyMethod = apply;
			TypedOracleMethod = oracle;
			CleanupMethod = cleanup;
			return this;
		}

		internal ScenarioActionProductionBinding WithDlcFixture(MethodInfo attachment)
		{
			FixtureAttachmentMethod = attachment;
			SafeFromState = "RobotIdleMonitor.idle";
			SafeToState = "RobotIdleMonitor.working";
			return this;
		}

		internal ScenarioActionProductionBinding WithLinearFlow(
			Type mutationType, MethodInfo hostExecution, MethodInfo hostState,
			MethodInfo hostOracle, MethodInfo cleanupExecution,
			MethodInfo cleanupMutation, MethodInfo cleanupOracle,
			ScenarioActionTransportBinding[] hostTransport,
			ScenarioActionTransportBinding[] cleanupTransport)
		{
			MutationType = mutationType;
			HostExecutionMethod = hostExecution;
			HostStateMethod = hostState;
			HostOracleMethod = hostOracle;
			CleanupExecutionMethod = cleanupExecution;
			CleanupMutationMethod = cleanupMutation;
			CleanupOracleMethod = cleanupOracle;
			HostTransportSteps = hostTransport;
			CleanupTransportSteps = cleanupTransport;
			return this;
		}
	}

	internal sealed class ScenarioActionTransportBinding
	{
		internal Type PacketType { get; }
		internal MethodInfo FactoryMethod { get; }
		internal MethodInfo NetworkSendMethod { get; }
		internal MethodInfo ClientDispatchMethod { get; }
		internal MethodInfo ClientExecutionMethod { get; }
		internal MethodInfo ClientApplyMethod { get; }
		internal MethodInfo ClientOracleMethod { get; }

		internal ScenarioActionTransportBinding(
			Type packetType, MethodInfo factory, MethodInfo send,
			MethodInfo dispatch, MethodInfo executeClient,
			MethodInfo applyClient, MethodInfo clientOracle)
		{
			PacketType = packetType;
			FactoryMethod = factory;
			NetworkSendMethod = send;
			ClientDispatchMethod = dispatch;
			ClientExecutionMethod = executeClient;
			ClientApplyMethod = applyClient;
			ClientOracleMethod = clientOracle;
		}
	}

	internal static class ScenarioActionProductionBindingRegistry
	{
		private const BindingFlags StaticMethods =
			BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

		internal static IReadOnlyList<ScenarioActionProductionBinding> Bindings { get; } =
			new[]
			{
				Animation(),
				BuildingConfig(),
				Dlc(), Effect(), EntityLifecycle(), Inventory(), Motion(), Pickup(), Rocket(), Uproot(),
			};

		private static ScenarioActionProductionBinding Animation()
		{
			ScenarioActionTransportBinding transport = T(typeof(PlayAnimPacket),
				M(typeof(AnimationActionFlow), "CreatePacket"), M(typeof(AnimationActionFlow), "Send"),
				M(typeof(PlayAnimPacket), "OnDispatched"), M(typeof(AnimationActionFlow), "ExecuteClient"),
				M(typeof(AnimationActionFlow), "ApplyClient"), M(typeof(AnimationActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(PlayAnimPacket),
				M(typeof(AnimationActionFlow), "CreateCleanupPacket"), M(typeof(AnimationActionFlow), "Send"),
				M(typeof(PlayAnimPacket), "OnDispatched"), M(typeof(AnimationActionFlow), "ExecuteClient"),
				M(typeof(AnimationActionFlow), "ApplyClient"), M(typeof(AnimationActionFlow), "ObserveClient"));
			return Bind("animation", nameof(ScenarioNativeActions.Animation),
					typeof(AnimationActionFlow), nameof(AnimationActionFlow.ExecuteHost))
				.WithExecution("cell:min-net-id", typeof(PlayAnimPacket),
					typeof(AnimationTarget), typeof(AnimationState),
					M(typeof(AnimationActionFlow), "Prepare"), M(typeof(AnimationActionFlow), "Resolve"),
					M(typeof(AnimationActionFlow), "Mutate"), M(typeof(AnimationActionFlow), "Send"),
					M(typeof(AnimationActionFlow), "ExecuteClient"), M(typeof(AnimationActionFlow), "ApplyClient"),
					M(typeof(AnimationActionFlow), "ObserveClient"), M(typeof(AnimationActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(AnimationActionMutation),
					M(typeof(AnimationActionFlow), "ExecuteHost"), M(typeof(AnimationActionFlow), "CaptureState"),
					M(typeof(AnimationActionFlow), "ObserveHost"), M(typeof(AnimationActionFlow), "ExecuteCleanup"),
					M(typeof(AnimationActionFlow), "Restore"), M(typeof(AnimationActionFlow), "ObserveCleanup"),
					[transport], [cleanup]);
		}

		private static ScenarioActionProductionBinding BuildingConfig()
		{
			ScenarioActionTransportBinding host = T(typeof(BuildingConfigPacket),
				M(typeof(BuildingConfigActionFlow), "CreatePacket"),
				M(typeof(BuildingConfigActionFlow), "Send"),
				M(typeof(BuildingConfigPacket), "OnDispatched"),
				M(typeof(BuildingConfigActionFlow), "ExecuteClient"),
				M(typeof(BuildingConfigActionFlow), "ApplyClient"),
				M(typeof(BuildingConfigActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(BuildingConfigPacket),
				M(typeof(BuildingConfigActionFlow), "CreateCleanupPacket"),
				M(typeof(BuildingConfigActionFlow), "Send"),
				M(typeof(BuildingConfigPacket), "OnDispatched"),
				M(typeof(BuildingConfigActionFlow), "ExecuteClient"),
				M(typeof(BuildingConfigActionFlow), "ApplyClient"),
				M(typeof(BuildingConfigActionFlow), "ObserveClient"));
			return Bind("building-config", nameof(ScenarioNativeActions.BuildingConfig),
					typeof(BuildingConfigActionFlow), nameof(BuildingConfigActionFlow.ExecuteHost))
				.WithExecution("net-id", typeof(BuildingConfigPacket),
					typeof(BuildingConfigTarget), typeof(BuildingConfigState),
					M(typeof(BuildingConfigActionFlow), "Prepare"), M(typeof(BuildingConfigActionFlow), "Resolve"),
					M(typeof(BuildingConfigActionFlow), "Mutate"), M(typeof(BuildingConfigActionFlow), "Send"),
					M(typeof(BuildingConfigPacket), "OnDispatched"), M(typeof(BuildingConfigActionFlow), "ApplyClient"),
					M(typeof(BuildingConfigActionFlow), "ObserveClient"), M(typeof(BuildingConfigActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(BuildingConfigProfileMutation),
					M(typeof(BuildingConfigActionFlow), "ExecuteHost"), M(typeof(BuildingConfigActionFlow), "CaptureState"),
					M(typeof(BuildingConfigActionFlow), "ObserveHost"), M(typeof(BuildingConfigActionFlow), "ExecuteCleanup"),
					M(typeof(BuildingConfigActionFlow), "Restore"), M(typeof(BuildingConfigActionFlow), "ObserveCleanup"),
					[host], [cleanup]);
		}

		private static ScenarioActionProductionBinding Dlc()
		{
			MethodInfo fixture = M(typeof(DlcRuntimeActionFlow), "Attach");
			ScenarioActionTransportBinding transport = T(typeof(DlcRuntimeProfilePacket),
				M(typeof(DlcRuntimeActionFlow), "CreatePacket"), M(typeof(DlcRuntimeActionFlow), "Send"),
				M(typeof(DlcRuntimeProfilePacket), "OnDispatched"), M(typeof(DlcRuntimeActionFlow), "ExecuteClient"),
				M(typeof(DlcRuntimeActionFlow), "ApplyClient"), M(typeof(DlcRuntimeActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(DlcRuntimeProfilePacket),
				M(typeof(DlcRuntimeActionFlow), "CreateCleanupPacket"), M(typeof(DlcRuntimeActionFlow), "Send"),
				M(typeof(DlcRuntimeProfilePacket), "OnDispatched"), M(typeof(DlcRuntimeActionFlow), "ExecuteClient"),
				M(typeof(DlcRuntimeActionFlow), "ApplyClient"), M(typeof(DlcRuntimeActionFlow), "ObserveClient"));
			return Bind("dlc-runtime", nameof(ScenarioNativeActions.DlcRuntime),
					typeof(DlcRuntimeActionFlow), nameof(DlcRuntimeActionFlow.ExecuteHost))
				.WithExecution("prefab+fixture-identity", typeof(DlcRuntimeProfilePacket),
					typeof(DlcRuntimeTarget), typeof(DlcRuntimeState),
					M(typeof(DlcRuntimeActionFlow), "Prepare"), M(typeof(DlcRuntimeActionFlow), "Resolve"),
					M(typeof(DlcRuntimeActionFlow), "Mutate"), M(typeof(DlcRuntimeActionFlow), "Send"),
					M(typeof(DlcRuntimeProfilePacket), "OnDispatched"), M(typeof(DlcRuntimeActionFlow), "ApplyClient"),
					M(typeof(DlcRuntimeActionFlow), "ObserveClient"), M(typeof(DlcRuntimeActionFlow), "ExecuteCleanup"))
				.WithDlcFixture(fixture)
				.WithLinearFlow(typeof(DlcActionMutation),
					M(typeof(DlcRuntimeActionFlow), "ExecuteHost"), M(typeof(DlcRuntimeActionFlow), "CaptureState"),
					M(typeof(DlcRuntimeActionFlow), "ObserveHost"), M(typeof(DlcRuntimeActionFlow), "ExecuteCleanup"),
					M(typeof(DlcRuntimeActionFlow), "Restore"), M(typeof(DlcRuntimeActionFlow), "ObserveCleanup"),
					[transport], [cleanup]);
		}

		private static ScenarioActionProductionBinding Effect()
		{
			ScenarioActionTransportBinding transport = T(typeof(ToggleEffectPacket),
				M(typeof(EffectActionFlow), "CreatePacket"), M(typeof(EffectActionFlow), "Send"),
				M(typeof(ToggleEffectPacket), "OnDispatched"), M(typeof(EffectActionFlow), "ExecuteClient"),
				M(typeof(EffectActionFlow), "ApplyClient"), M(typeof(EffectActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(ToggleEffectPacket),
				M(typeof(EffectActionFlow), "CreateCleanupPacket"), M(typeof(EffectActionFlow), "Send"),
				M(typeof(ToggleEffectPacket), "OnDispatched"), M(typeof(EffectActionFlow), "ExecuteClient"),
				M(typeof(EffectActionFlow), "ApplyClient"), M(typeof(EffectActionFlow), "ObserveClient"));
			return Bind("effect", nameof(ScenarioNativeActions.Effect),
					typeof(EffectActionFlow), nameof(EffectActionFlow.ExecuteHost))
				.WithExecution("net-id", typeof(ToggleEffectPacket),
					typeof(EffectTarget), typeof(EffectState),
					M(typeof(EffectActionFlow), "Prepare"), M(typeof(EffectActionFlow), "Resolve"),
					M(typeof(EffectActionFlow), "Mutate"), M(typeof(EffectActionFlow), "Send"),
					M(typeof(ToggleEffectPacket), "OnDispatched"), M(typeof(EffectActionFlow), "ApplyClient"),
					M(typeof(EffectActionFlow), "ObserveClient"), M(typeof(EffectActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(EffectActionMutation),
					M(typeof(EffectActionFlow), "ExecuteHost"), M(typeof(EffectActionFlow), "CaptureState"),
					M(typeof(EffectActionFlow), "ObserveHost"), M(typeof(EffectActionFlow), "ExecuteCleanup"),
					M(typeof(EffectActionFlow), "Restore"), M(typeof(EffectActionFlow), "ObserveCleanup"),
					[transport], [cleanup]);
		}

		private static ScenarioActionProductionBinding EntityLifecycle()
		{
			ScenarioActionTransportBinding transport = T(typeof(SpawnPrefabPacket),
				M(typeof(EntityLifecycleActionFlow), "CreatePacket"), M(typeof(EntityLifecycleActionFlow), "Send"),
				M(typeof(SpawnPrefabPacket), "OnDispatched"), M(typeof(EntityLifecycleActionFlow), "ExecuteClient"),
				M(typeof(EntityLifecycleActionFlow), "ApplyClient"), M(typeof(EntityLifecycleActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(SpawnPrefabPacket),
				M(typeof(EntityLifecycleActionFlow), "CreateCleanupPacket"), M(typeof(EntityLifecycleActionFlow), "Send"),
				M(typeof(SpawnPrefabPacket), "OnDispatched"), M(typeof(EntityLifecycleActionFlow), "ExecuteClient"),
				M(typeof(EntityLifecycleActionFlow), "ApplyClient"), M(typeof(EntityLifecycleActionFlow), "ObserveClient"));
			return Bind("entity-lifecycle", nameof(ScenarioNativeActions.EntityLifecycle),
					typeof(EntityLifecycleActionFlow), nameof(EntityLifecycleActionFlow.ExecuteHost))
				.WithExecution("net-id", typeof(SpawnPrefabPacket),
					typeof(EntityLifecycleTarget), typeof(EntityLifecycleState),
					M(typeof(EntityLifecycleActionFlow), "Prepare"), M(typeof(EntityLifecycleActionFlow), "Resolve"),
					M(typeof(EntityLifecycleActionFlow), "Mutate"), M(typeof(EntityLifecycleActionFlow), "Send"),
					M(typeof(SpawnPrefabPacket), "OnDispatched"), M(typeof(EntityLifecycleActionFlow), "ApplyClient"),
					M(typeof(EntityLifecycleActionFlow), "ObserveClient"), M(typeof(EntityLifecycleActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(EntityLifecycleActionMutation),
					M(typeof(EntityLifecycleActionFlow), "ExecuteHost"), M(typeof(EntityLifecycleActionFlow), "CaptureState"),
					M(typeof(EntityLifecycleActionFlow), "ObserveHost"), M(typeof(EntityLifecycleActionFlow), "ExecuteCleanup"),
					M(typeof(EntityLifecycleActionFlow), "Restore"), M(typeof(EntityLifecycleActionFlow), "ObserveCleanup"),
					[transport], [cleanup]);
		}

		private static ScenarioActionProductionBinding Inventory()
		{
			ScenarioActionTransportBinding transport = T(typeof(ResourceCountPacket),
				M(typeof(InventoryActionFlow), "CreatePacket"), M(typeof(InventoryActionFlow), "Send"),
				M(typeof(ResourceCountPacket), "OnDispatched"), M(typeof(InventoryActionFlow), "ExecuteClient"),
				M(typeof(InventoryActionFlow), "ApplyClient"), M(typeof(InventoryActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(ResourceCountPacket),
				M(typeof(InventoryActionFlow), "CreateCleanupPacket"), M(typeof(InventoryActionFlow), "Send"),
				M(typeof(ResourceCountPacket), "OnDispatched"), M(typeof(InventoryActionFlow), "ExecuteClient"),
				M(typeof(InventoryActionFlow), "ApplyClient"), M(typeof(InventoryActionFlow), "ObserveClient"));
			return Bind("inventory", nameof(ScenarioNativeActions.Inventory),
					typeof(InventoryActionFlow), nameof(InventoryActionFlow.ExecuteHost))
				.WithExecution("Sand+min-live-minion-net-id", typeof(ResourceCountPacket),
					typeof(InventoryTarget), typeof(InventoryState),
					M(typeof(InventoryActionFlow), "Prepare"), M(typeof(InventoryActionFlow), "Resolve"),
					M(typeof(InventoryActionFlow), "Mutate"), M(typeof(InventoryActionFlow), "Send"),
					M(typeof(ResourceCountPacket), "OnDispatched"), M(typeof(InventoryActionFlow), "ApplyClient"),
					M(typeof(InventoryActionFlow), "ObserveClient"), M(typeof(InventoryActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(InventoryActionMutation),
					M(typeof(InventoryActionFlow), "ExecuteHost"), M(typeof(InventoryActionFlow), "CaptureState"),
					M(typeof(InventoryActionFlow), "ObserveHost"), M(typeof(InventoryActionFlow), "ExecuteCleanup"),
					M(typeof(InventoryActionFlow), "Restore"), M(typeof(InventoryActionFlow), "ObserveCleanup"),
					[transport], [cleanup]);
		}

		private static ScenarioActionProductionBinding Motion()
		{
			ScenarioActionTransportBinding transport = T(typeof(EntityMotionBatchPacket),
				M(typeof(MotionActionFlow), "CreatePacket"), M(typeof(MotionActionFlow), "Send"),
				M(typeof(EntityMotionBatchPacket), "OnDispatched"), M(typeof(MotionActionFlow), "ExecuteClient"),
				M(typeof(MotionActionFlow), "ApplyClient"), M(typeof(MotionActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(EntityMotionBatchPacket),
				M(typeof(MotionActionFlow), "CreateCleanupPacket"), M(typeof(MotionActionFlow), "Send"),
				M(typeof(EntityMotionBatchPacket), "OnDispatched"), M(typeof(MotionActionFlow), "ExecuteClient"),
				M(typeof(MotionActionFlow), "ApplyClient"), M(typeof(MotionActionFlow), "ObserveClient"));
			return Bind("motion", nameof(ScenarioNativeActions.Motion),
					typeof(MotionActionFlow), nameof(MotionActionFlow.ExecuteHost))
				.WithExecution("net-id", typeof(EntityMotionBatchPacket),
					typeof(MotionTarget), typeof(MotionState),
					M(typeof(MotionActionFlow), "Prepare"), M(typeof(MotionActionFlow), "Resolve"),
					M(typeof(MotionActionFlow), "Mutate"), M(typeof(MotionActionFlow), "Send"),
					M(typeof(EntityMotionBatchPacket), "OnDispatched"), M(typeof(MotionActionFlow), "ApplyClient"),
					M(typeof(MotionActionFlow), "ObserveClient"), M(typeof(MotionActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(MotionActionMutation),
					M(typeof(MotionActionFlow), "ExecuteHost"), M(typeof(MotionActionFlow), "CaptureState"),
					M(typeof(MotionActionFlow), "ObserveHost"), M(typeof(MotionActionFlow), "ExecuteCleanup"),
					M(typeof(MotionActionFlow), "Restore"), M(typeof(MotionActionFlow), "ObserveCleanup"),
					[transport], [cleanup]);
		}

		private static ScenarioActionProductionBinding Pickup()
		{
			ScenarioActionTransportBinding pickup = T(typeof(GroundItemPickedUpPacket),
				M(typeof(PickupActionFlow), "CreatePickupPacket"), M(typeof(PickupActionFlow), "SendPickup"),
				M(typeof(GroundItemPickedUpPacket), "OnDispatched"), M(typeof(PickupActionFlow), "ExecutePickupClient"),
				M(typeof(PickupActionFlow), "ApplyPickupClient"), M(typeof(PickupActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding spawn = T(typeof(SpawnPrefabPacket),
				M(typeof(PickupActionFlow), "CreateSpawnPacket"), M(typeof(PickupActionFlow), "SendSpawn"),
				M(typeof(SpawnPrefabPacket), "OnDispatched"), M(typeof(PickupActionFlow), "ExecuteSpawnClient"),
				M(typeof(PickupActionFlow), "ApplySpawnClient"), M(typeof(PickupActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanupSpawn = T(typeof(SpawnPrefabPacket),
				M(typeof(PickupActionFlow), "CreateCleanupSpawnPacket"), M(typeof(PickupActionFlow), "SendSpawn"),
				M(typeof(SpawnPrefabPacket), "OnDispatched"), M(typeof(PickupActionFlow), "ExecuteSpawnClient"),
				M(typeof(PickupActionFlow), "ApplySpawnClient"), M(typeof(PickupActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding storage = T(typeof(StorageItemPacket),
				M(typeof(PickupActionFlow), "CreateCleanupStoragePacket"), M(typeof(PickupActionFlow), "SendStorage"),
				M(typeof(StorageItemPacket), "OnDispatched"), M(typeof(PickupActionFlow), "ExecuteStorageClient"),
				M(typeof(PickupActionFlow), "ApplyStorageClient"), M(typeof(PickupActionFlow), "ObserveClient"));
			return Bind("pickup", nameof(ScenarioNativeActions.Pickup),
					typeof(PickupActionFlow), nameof(PickupActionFlow.ExecuteHost))
				.WithExecution("item-net-id+min-live-minion-net-id+target-cell",
					typeof(GroundItemPickedUpPacket), typeof(PickupTarget), typeof(PickupState),
					M(typeof(PickupActionFlow), "Prepare"), M(typeof(PickupActionFlow), "Resolve"),
					M(typeof(PickupActionFlow), "Mutate"), M(typeof(PickupActionFlow), "SendPickup"),
					M(typeof(GroundItemPickedUpPacket), "OnDispatched"), M(typeof(PickupActionFlow), "ApplyPickupClient"),
					M(typeof(PickupActionFlow), "ObserveClient"), M(typeof(PickupActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(PickupActionMutation),
					M(typeof(PickupActionFlow), "ExecuteHost"), M(typeof(PickupActionFlow), "CaptureState"),
					M(typeof(PickupActionFlow), "ObserveHost"), M(typeof(PickupActionFlow), "ExecuteCleanup"),
					M(typeof(PickupActionFlow), "Restore"), M(typeof(PickupActionFlow), "ObserveCleanup"),
					[pickup, spawn], [cleanupSpawn, storage]);
		}

		private static ScenarioActionProductionBinding Rocket()
		{
			ScenarioActionTransportBinding transport = T(typeof(RocketSettingsStatePacket),
				M(typeof(RocketActionFlow), "CreatePacket"), M(typeof(RocketActionFlow), "Send"),
				M(typeof(RocketSettingsStatePacket), "OnDispatched"), M(typeof(RocketActionFlow), "ExecuteClient"),
				M(typeof(RocketActionFlow), "ApplyClient"), M(typeof(RocketActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(RocketSettingsStatePacket),
				M(typeof(RocketActionFlow), "CreateCleanupPacket"), M(typeof(RocketActionFlow), "Send"),
				M(typeof(RocketSettingsStatePacket), "OnDispatched"), M(typeof(RocketActionFlow), "ExecuteClient"),
				M(typeof(RocketActionFlow), "ApplyClient"), M(typeof(RocketActionFlow), "ObserveClient"));
			return Bind("rocket", nameof(ScenarioNativeActions.Rocket),
					typeof(RocketActionFlow), nameof(RocketActionFlow.ExecuteHost))
				.WithExecution("rocket-net-id+pad-net-id", typeof(RocketSettingsStatePacket),
					typeof(RocketTarget), typeof(RocketState),
					M(typeof(RocketActionFlow), "Prepare"), M(typeof(RocketActionFlow), "Resolve"),
					M(typeof(RocketActionFlow), "Mutate"), M(typeof(RocketActionFlow), "Send"),
					M(typeof(RocketSettingsStatePacket), "OnDispatched"), M(typeof(RocketActionFlow), "ApplyClient"),
					M(typeof(RocketActionFlow), "ObserveClient"), M(typeof(RocketActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(RocketActionMutation),
					M(typeof(RocketActionFlow), "ExecuteHost"), M(typeof(RocketActionFlow), "CaptureState"),
					M(typeof(RocketActionFlow), "ObserveHost"), M(typeof(RocketActionFlow), "ExecuteCleanup"),
					M(typeof(RocketActionFlow), "Restore"), M(typeof(RocketActionFlow), "ObserveCleanup"),
					[transport], [cleanup]);
		}

		private static ScenarioActionProductionBinding Uproot()
		{
			ScenarioActionTransportBinding host = T(typeof(BuildingConfigPacket),
				M(typeof(UprootActionFlow), "CreatePacket"), M(typeof(UprootActionFlow), "Send"),
				M(typeof(BuildingConfigPacket), "OnDispatched"), M(typeof(UprootActionFlow), "ExecuteClient"),
				M(typeof(UprootActionFlow), "ApplyClient"), M(typeof(UprootActionFlow), "ObserveClient"));
			ScenarioActionTransportBinding cleanup = T(typeof(BuildingConfigPacket),
				M(typeof(UprootActionFlow), "CreateCleanupPacket"), M(typeof(UprootActionFlow), "Send"),
				M(typeof(BuildingConfigPacket), "OnDispatched"), M(typeof(UprootActionFlow), "ExecuteClient"),
				M(typeof(UprootActionFlow), "ApplyClient"), M(typeof(UprootActionFlow), "ObserveClient"));
			return Bind("uproot", nameof(ScenarioNativeActions.Uproot),
					typeof(UprootActionFlow), nameof(UprootActionFlow.ExecuteHost))
				.WithExecution("net-id", typeof(BuildingConfigPacket),
					typeof(UprootTarget), typeof(UprootState),
					M(typeof(UprootActionFlow), "Prepare"), M(typeof(UprootActionFlow), "Resolve"),
					M(typeof(UprootActionFlow), "Mutate"), M(typeof(UprootActionFlow), "Send"),
					M(typeof(BuildingConfigPacket), "OnDispatched"), M(typeof(UprootActionFlow), "ApplyClient"),
					M(typeof(UprootActionFlow), "ObserveClient"), M(typeof(UprootActionFlow), "ExecuteCleanup"))
				.WithLinearFlow(typeof(UprootProfileMutation),
					M(typeof(UprootActionFlow), "ExecuteHost"), M(typeof(UprootActionFlow), "CaptureState"),
					M(typeof(UprootActionFlow), "ObserveHost"), M(typeof(UprootActionFlow), "ExecuteCleanup"),
					M(typeof(UprootActionFlow), "Restore"), M(typeof(UprootActionFlow), "ObserveCleanup"),
					[host], [cleanup]);
		}

		private static ScenarioActionProductionBinding Bind(
			string scenario,
			string handlerName,
			Type runtimeType,
			string runtimeName)
		{
			MethodInfo handler = typeof(ScenarioNativeActions).GetMethod(
				handlerName, StaticMethods);
			MethodInfo runtime = runtimeType.GetMethod(runtimeName, StaticMethods);
			return new ScenarioActionProductionBinding(scenario, handler, runtime);
		}

		private static MethodInfo M(Type type, string name, int? parameterCount = null)
		{
			IEnumerable<MethodInfo> methods = type.GetMethods(
				BindingFlags.Static | BindingFlags.Instance |
				BindingFlags.Public | BindingFlags.NonPublic)
				.Where(method => method.Name == name);
			if (parameterCount.HasValue)
				methods = methods.Where(method =>
					method.GetParameters().Length == parameterCount.Value);
			return methods.Single();
		}

		private static ScenarioActionTransportBinding T(
			Type packet, MethodInfo factory, MethodInfo send, MethodInfo dispatch,
			MethodInfo executeClient, MethodInfo applyClient, MethodInfo clientOracle)
			=> new(packet, factory, send, dispatch, executeClient, applyClient, clientOracle);
	}
}
#endif
