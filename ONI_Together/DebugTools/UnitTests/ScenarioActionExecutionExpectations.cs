using System;
using System.Collections.Generic;

namespace ONI_Together.DebugTools.UnitTests
{
	internal sealed class ScenarioActionExecutionExpectation
	{
		internal string Scenario { get; }
		internal string DeterministicTargetRule { get; }
		internal IReadOnlyList<string> PacketNames { get; }
		internal Type TargetType { get; }
		internal Type StateType { get; }

		internal ScenarioActionExecutionExpectation(
			string scenario,
			string deterministicTargetRule,
			Type targetType,
			Type stateType,
			params string[] packetNames)
		{
			Scenario = scenario;
			DeterministicTargetRule = deterministicTargetRule;
			PacketNames = packetNames;
			TargetType = targetType;
			StateType = stateType;
		}
	}

	internal static class ScenarioActionExecutionExpectations
	{
		internal static ScenarioActionExecutionExpectation Animation { get; } = E(
			"animation", "cell:min-net-id", typeof(AnimationTarget), typeof(AnimationState),
			"PlayAnimPacket",
			"ONI_Together.Networking.Packets.DuplicantActions.DuplicantPresentationBatchPacket");
		internal static ScenarioActionExecutionExpectation BuildingConfig { get; } = E(
			"building-config", "net-id", typeof(BuildingConfigTarget), typeof(BuildingConfigState),
			"ONI_Together.Networking.Packets.World.BuildingConfigPacket");
		internal static ScenarioActionExecutionExpectation DlcRuntime { get; } = E(
			"dlc-runtime", "prefab+fixture-identity", typeof(DlcRuntimeTarget), typeof(DlcRuntimeState),
			"ONI_Together.Networking.Packets.DLC.SpacedOut.DlcRuntimeProfilePacket");
		internal static ScenarioActionExecutionExpectation Effect { get; } = E(
			"effect", "net-id", typeof(EffectTarget), typeof(EffectState),
			"ONI_Together.Networking.Packets.DuplicantActions.ToggleEffectPacket");
		internal static ScenarioActionExecutionExpectation EntityLifecycle { get; } = E(
			"entity-lifecycle", "net-id", typeof(EntityLifecycleTarget), typeof(EntityLifecycleState),
			"ONI_Together.Networking.Packets.World.SpawnPrefabPacket");
		internal static ScenarioActionExecutionExpectation Inventory { get; } = E(
			"inventory", "Sand+min-live-minion-net-id", typeof(InventoryTarget), typeof(InventoryState),
			"ONI_Together.Networking.Packets.World.ResourceCountPacket");
		internal static ScenarioActionExecutionExpectation Motion { get; } = E(
			"motion", "net-id", typeof(MotionTarget), typeof(MotionState),
			"ONI_Together.Networking.Packets.Core.EntityMotionBatchPacket");
		internal static ScenarioActionExecutionExpectation Pickup { get; } = E(
			"pickup", "item-net-id+min-live-minion-net-id+target-cell",
			typeof(PickupTarget), typeof(PickupState),
			"ONI_Together.Networking.Packets.World.GroundItemPickedUpPacket");
		internal static ScenarioActionExecutionExpectation Rocket { get; } = E(
			"rocket", "rocket-net-id+pad-net-id", typeof(RocketTarget), typeof(RocketState),
			"ONI_Together.Networking.Packets.DLC.SpacedOut.RocketSettingsStatePacket");
		internal static ScenarioActionExecutionExpectation Uproot { get; } = E(
			"uproot", "net-id", typeof(UprootTarget), typeof(UprootState),
			"ONI_Together.Networking.Packets.World.BuildingConfigPacket");

		private static ScenarioActionExecutionExpectation E(
			string scenario,
			string targetRule,
			Type targetType,
			Type stateType,
			params string[] packetNames)
			=> new(scenario, targetRule, targetType, stateType, packetNames);
	}
}
