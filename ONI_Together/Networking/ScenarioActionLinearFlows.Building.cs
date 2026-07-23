#if DEBUG
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Packets.Architecture;
using Shared;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking
{
	internal sealed class BuildingConfigProfileMutation
	{
		internal BuildingConfigTarget Target;
		internal LogicSwitch RuntimeTarget;
		internal bool Previous;
		internal bool Current;
		internal BuildingConfigPacket Packet;
	}

	internal sealed class UprootProfileMutation
	{
		internal UprootTarget Target;
		internal Uprootable RuntimeTarget;
		internal bool Previous;
		internal bool Current;
		internal BuildingConfigPacket Packet;
	}

	internal sealed class ProfilePreparedCommand
	{
		internal ScenarioActionCommand Command;
	}

	internal static class BuildingConfigActionFlow
	{
		private const string EntryId = "sync:6eaef0b4077bfdc8e29a6aff";

		internal static BuildingConfigProfileMutation ExecuteHost(ScenarioActionCommand command)
		{
			ProfilePreparedCommand prepared = Prepare(command);
			BuildingConfigTarget target = Resolve(prepared);
			BuildingConfigProfileMutation mutation = Mutate(target);
			BuildingConfigState state = CaptureState(mutation);
			ObserveHost(state);
			BuildingConfigPacket packet = CreatePacket(mutation);
			Send(packet);
			return mutation;
		}

		internal static ProfilePreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static BuildingConfigTarget Resolve(ProfilePreparedCommand prepared)
		{
			int netId = 0;
			prepared?.Command?.TryGetInt("netId", out netId);
			var target = new BuildingConfigTarget { TargetNetId = netId };
			if (NetworkIdentityRegistry.TryGet(netId, out NetworkIdentity identity))
				ScenarioActionFlowContext.Attach(target, identity.GetComponent<LogicSwitch>());
			return target;
		}

		internal static BuildingConfigProfileMutation Mutate(BuildingConfigTarget target)
		{
			LogicSwitch runtime = ScenarioActionFlowContext.Get<LogicSwitch>(target);
			if (runtime == null) return null;
			bool previous = BuildingConfigProfileRuntime.ReadCheckbox(runtime);
			bool current = BuildingConfigProfileRuntime.ToggleCheckbox(runtime);
			if (current == previous) return null;
			var mutation = new BuildingConfigProfileMutation
			{
				Target = target, RuntimeTarget = runtime,
				Previous = previous, Current = current,
			};
			mutation.Packet = BuildPacket(mutation, current);
			return mutation;
		}

		internal static BuildingConfigState CaptureState(BuildingConfigProfileMutation mutation)
			=> CreateState(mutation, mutation?.Current ?? false);

		internal static void ObserveHost(BuildingConfigState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);

		internal static BuildingConfigPacket CreatePacket(BuildingConfigProfileMutation mutation)
			=> mutation?.Packet;

		internal static void Send(BuildingConfigPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static BuildingConfigState ExecuteClient(BuildingConfigPacket packet)
		{
			BuildingConfigState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static BuildingConfigState ApplyClient(BuildingConfigPacket packet)
			=> packet != null && packet.ApplyProfileClient()
				? CreateState(packet) : null;

		internal static void ObserveClient(BuildingConfigState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static BuildingConfigState ExecuteCleanup(BuildingConfigProfileMutation mutation)
		{
			BuildingConfigState state = Restore(mutation);
			ObserveCleanup(state);
			BuildingConfigPacket packet = CreateCleanupPacket(mutation);
			Send(packet);
			return state;
		}

		internal static BuildingConfigState Restore(BuildingConfigProfileMutation mutation)
		{
			if (mutation?.RuntimeTarget == null || mutation.RuntimeTarget.IsNullOrDestroyed())
				return null;
			if (BuildingConfigProfileRuntime.ReadCheckbox(mutation.RuntimeTarget) != mutation.Previous)
				BuildingConfigProfileRuntime.ToggleCheckbox(mutation.RuntimeTarget);
			if (BuildingConfigProfileRuntime.ReadCheckbox(mutation.RuntimeTarget) != mutation.Previous)
				return null;
			mutation.Current = mutation.Previous;
			mutation.Packet = BuildPacket(mutation, mutation.Previous);
			return CreateState(mutation, mutation.Previous);
		}

		internal static void ObserveCleanup(BuildingConfigState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);

		internal static BuildingConfigPacket CreateCleanupPacket(
			BuildingConfigProfileMutation mutation)
			=> mutation?.Packet;

		private static BuildingConfigPacket BuildPacket(
			BuildingConfigProfileMutation mutation, bool value)
		{
			var packet = new BuildingConfigPacket
			{
				NetId = (int)mutation.Target.TargetNetId,
				Cell = Grid.PosToCell(mutation.RuntimeTarget.gameObject),
				ConfigHash = NetworkingHash.ForConfigKey("LogicSwitchState"),
				Value = value ? 1f : 0f,
				ConfigType = BuildingConfigType.Boolean,
				ScenarioActionProfile = ScenarioActionReceiverGate.Mark("building-config"),
			};
			packet.PrepareProfileSend();
			return packet;
		}

		private static BuildingConfigState CreateState(
			BuildingConfigProfileMutation mutation, bool value)
			=> mutation == null ? null : AttachState(new BuildingConfigState
			{
				LifecycleRevision = (long)mutation.Packet.TargetLifecycleRevision,
				BaseRevision = 0,
				StateRevision = (long)mutation.Packet.StateRevision,
				ConfigKind = "LogicSwitchState",
				ConfigValue = value ? "true" : "false",
			}, mutation.Target, mutation.Packet);

		private static BuildingConfigState CreateState(BuildingConfigPacket packet)
			=> AttachState(new BuildingConfigState
			{
				LifecycleRevision = (long)packet.TargetLifecycleRevision,
				BaseRevision = (long)packet.BaseStateRevision,
				StateRevision = (long)packet.StateRevision,
				ConfigKind = "LogicSwitchState",
				ConfigValue = packet.Value > 0.5f ? "true" : "false",
			}, new BuildingConfigTarget { TargetNetId = packet.NetId }, packet);

		private static BuildingConfigState AttachState(
			BuildingConfigState state, BuildingConfigTarget target, BuildingConfigPacket packet)
			=> ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "building-config", Revision = state.StateRevision,
				Target = target, State = state, EntryId = EntryId, Packet = packet,
			});
	}

	internal static class UprootActionFlow
	{
		private const string EntryId = "sync:9dce9681c9df9ef0bf16b56e";

		internal static UprootProfileMutation ExecuteHost(ScenarioActionCommand command)
		{
			ProfilePreparedCommand prepared = Prepare(command);
			UprootTarget target = Resolve(prepared);
			UprootProfileMutation mutation = Mutate(target);
			UprootState state = CaptureState(mutation);
			ObserveHost(state);
			BuildingConfigPacket packet = CreatePacket(mutation);
			Send(packet);
			return mutation;
		}

		internal static ProfilePreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static UprootTarget Resolve(ProfilePreparedCommand prepared)
		{
			int netId = 0;
			prepared?.Command?.TryGetInt("netId", out netId);
			var target = new UprootTarget { TargetNetId = netId };
			if (NetworkIdentityRegistry.TryGet(netId, out NetworkIdentity identity))
				ScenarioActionFlowContext.Attach(target, identity.GetComponent<Uprootable>());
			return target;
		}

		internal static UprootProfileMutation Mutate(UprootTarget target)
		{
			Uprootable runtime = ScenarioActionFlowContext.Get<Uprootable>(target);
			if (runtime == null || !runtime.CanUproot() || runtime.IsMarkedForUproot)
				return null;
			if (!UprootProfileRuntime.Mark(runtime)) return null;
			var mutation = new UprootProfileMutation
			{
				Target = target, RuntimeTarget = runtime,
				Previous = false, Current = true,
			};
			mutation.Packet = BuildPacket(mutation, true);
			return mutation;
		}

		internal static UprootState CaptureState(UprootProfileMutation mutation)
			=> CreateState(mutation, mutation?.Current ?? false);

		internal static void ObserveHost(UprootState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);

		internal static BuildingConfigPacket CreatePacket(UprootProfileMutation mutation)
			=> mutation?.Packet;

		internal static void Send(BuildingConfigPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static UprootState ExecuteClient(BuildingConfigPacket packet)
		{
			UprootState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static UprootState ApplyClient(BuildingConfigPacket packet)
			=> packet != null && packet.ApplyProfileClient()
				? CreateState(packet) : null;

		internal static void ObserveClient(UprootState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static UprootState ExecuteCleanup(UprootProfileMutation mutation)
		{
			UprootState state = Restore(mutation);
			ObserveCleanup(state);
			BuildingConfigPacket packet = CreateCleanupPacket(mutation);
			Send(packet);
			return state;
		}

		internal static UprootState Restore(UprootProfileMutation mutation)
		{
			if (mutation?.RuntimeTarget == null || mutation.RuntimeTarget.IsNullOrDestroyed()
			    || !UprootProfileRuntime.Restore(mutation.RuntimeTarget, mutation.Previous))
				return null;
			mutation.Current = mutation.Previous;
			mutation.Packet = BuildPacket(mutation, mutation.Previous);
			return CreateState(mutation, mutation.Previous);
		}

		internal static void ObserveCleanup(UprootState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);

		internal static BuildingConfigPacket CreateCleanupPacket(UprootProfileMutation mutation)
			=> mutation?.Packet;

		private static BuildingConfigPacket BuildPacket(UprootProfileMutation mutation, bool value)
		{
			var packet = new BuildingConfigPacket
			{
				NetId = (int)mutation.Target.TargetNetId,
				Cell = Grid.PosToCell(mutation.RuntimeTarget.gameObject),
				ConfigHash = NetworkingHash.ForConfigKey("UprootPlant"),
				Value = value ? 1f : 0f,
				ConfigType = BuildingConfigType.Boolean,
				ScenarioActionProfile = ScenarioActionReceiverGate.Mark("uproot"),
			};
			packet.PrepareProfileSend();
			return packet;
		}

		private static UprootState CreateState(UprootProfileMutation mutation, bool value)
			=> mutation == null ? null : AttachState(new UprootState
			{
				LifecycleRevision = (long)mutation.Packet.TargetLifecycleRevision,
				StateRevision = (long)mutation.Packet.StateRevision,
				Uprooted = value,
			}, mutation.Target, mutation.Packet);

		private static UprootState CreateState(BuildingConfigPacket packet)
			=> AttachState(new UprootState
			{
				LifecycleRevision = (long)packet.TargetLifecycleRevision,
				StateRevision = (long)packet.StateRevision,
				Uprooted = packet.Value > 0.5f,
			}, new UprootTarget { TargetNetId = packet.NetId }, packet);

		private static UprootState AttachState(
			UprootState state, UprootTarget target, BuildingConfigPacket packet)
			=> ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "uproot", Revision = state.StateRevision,
				Target = target, State = state, EntryId = EntryId, Packet = packet,
			});
	}
}
#endif
