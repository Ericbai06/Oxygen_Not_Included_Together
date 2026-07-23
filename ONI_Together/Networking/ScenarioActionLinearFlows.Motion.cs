#if DEBUG
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking
{
	internal sealed class MotionActionMutation
	{
		internal MotionTarget Target;
		internal RemoteMotionPresenter Presenter;
		internal NetworkIdentity Identity;
		internal Vector3 Previous;
		internal EntityMotionState WireState;
		internal EntityMotionBatchPacket Packet;
	}

	internal sealed class MotionPreparedCommand { internal ScenarioActionCommand Command; }

	internal static class MotionActionFlow
	{
		private const string EntryId = "sync:55ff306bf78efab7f31ac7b3";

		internal static MotionActionMutation ExecuteHost(ScenarioActionCommand command)
		{
			MotionPreparedCommand prepared = Prepare(command);
			MotionTarget target = Resolve(prepared);
			MotionActionMutation mutation = Mutate(target);
			MotionState state = CaptureState(mutation);
			ObserveHost(state);
			EntityMotionBatchPacket packet = CreatePacket(mutation);
			Send(packet);
			return mutation;
		}

		internal static MotionPreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static MotionTarget Resolve(MotionPreparedCommand prepared)
		{
			int netId = 0;
			prepared?.Command?.TryGetInt("netId", out netId);
			var target = new MotionTarget { EntityNetId = netId };
			if (NetworkIdentityRegistry.TryGet(netId, out NetworkIdentity identity))
				ScenarioActionFlowContext.Attach(target, identity);
			return target;
		}

		internal static MotionActionMutation Mutate(MotionTarget target)
		{
			NetworkIdentity identity = ScenarioActionFlowContext.Get<NetworkIdentity>(target);
			RemoteMotionPresenter presenter = identity?.GetComponent<RemoteMotionPresenter>();
			if (presenter == null) return null;
			Vector3 previous = presenter.transform.position;
			EntityMotionState wire = Wire(identity, presenter, previous, previous + Vector3.right);
			presenter.ApplyAuthoritativeSnapshot(wire);
			return new MotionActionMutation
			{
				Target = target, Presenter = presenter, Identity = identity, Previous = previous,
				WireState = wire, Packet = new EntityMotionBatchPacket
				{
					States = [wire],
					ScenarioActionProfile = ScenarioActionReceiverGate.Mark("motion"),
				},
			};
		}

		internal static MotionState CaptureState(MotionActionMutation mutation)
			=> State(mutation?.Target, mutation?.WireState, mutation?.Packet);

		internal static void ObserveHost(MotionState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);

		internal static EntityMotionBatchPacket CreatePacket(MotionActionMutation mutation)
			=> mutation?.Packet;

		internal static void Send(EntityMotionBatchPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static MotionState ExecuteClient(EntityMotionBatchPacket packet)
		{
			MotionState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static MotionState ApplyClient(EntityMotionBatchPacket packet)
		{
			if (packet == null || !packet.ApplyRuntimePacket()) return null;
			EntityMotionState wire = packet.States[0];
			return State(new MotionTarget { EntityNetId = wire.NetId }, wire, packet);
		}

		internal static void ObserveClient(MotionState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static MotionState ExecuteCleanup(MotionActionMutation mutation)
		{
			MotionState state = Restore(mutation);
			ObserveCleanup(state);
			EntityMotionBatchPacket packet = CreateCleanupPacket(mutation);
			Send(packet);
			return state;
		}

		internal static MotionState Restore(MotionActionMutation mutation)
		{
			if (mutation?.Presenter == null || mutation.Presenter.IsNullOrDestroyed()) return null;
			Vector3 source = mutation.Presenter.transform.position;
			mutation.WireState = Wire(
				mutation.Identity, mutation.Presenter, source, mutation.Previous);
			mutation.Presenter.ApplyAuthoritativeSnapshot(mutation.WireState);
			mutation.Packet = new EntityMotionBatchPacket
			{
				States = [mutation.WireState],
				ScenarioActionProfile = ScenarioActionReceiverGate.Mark("motion"),
			};
			return State(mutation.Target, mutation.WireState, mutation.Packet);
		}

		internal static void ObserveCleanup(MotionState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);

		internal static EntityMotionBatchPacket CreateCleanupPacket(MotionActionMutation mutation)
			=> mutation?.Packet;

		private static EntityMotionState Wire(
			NetworkIdentity identity, RemoteMotionPresenter presenter,
			Vector3 source, Vector3 target)
			=> new()
			{
				NetId = identity.NetId,
				Revision = NetworkIdentityRegistry.NextAuthorityRevision(),
				Kind = EntityMotionKind.Transition,
				StartSimTick = PresentationTickClock.CurrentTick,
				Source = source, Target = target, DurationTicks = 1,
				StartNavType = presenter.AuthoritativeNavType,
				EndNavType = presenter.AuthoritativeNavType,
			};

		private static MotionState State(
			MotionTarget target, EntityMotionState wire, EntityMotionBatchPacket packet)
		{
			if (wire == null) return null;
			var state = new MotionState
			{
				Tick = wire.StartSimTick,
				StartPosition = [(double)wire.Source.x, wire.Source.y],
				EndPosition = [(double)wire.Target.x, wire.Target.y],
				NavigationState = wire.Kind + ":" + wire.StartNavType + "->" + wire.EndNavType,
				MotionRevision = (long)wire.Revision,
			};
			return ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "motion", Revision = (long)wire.Revision, Target = target,
				State = state, EntryId = EntryId, Packet = packet,
			});
		}
	}
}
#endif
