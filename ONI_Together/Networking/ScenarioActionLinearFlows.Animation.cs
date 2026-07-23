#if DEBUG
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking
{
	internal sealed class AnimationActionMutation
	{
		internal AnimationTarget Target;
		internal AnimationProfileMutation Runtime;
		internal PlayAnimPacket Packet;
	}

	internal sealed class AnimationPreparedCommand { internal ScenarioActionCommand Command; }

	internal static class AnimationActionFlow
	{
		private const string EntryId = "sync:f60e38b805c1052cff0fec0d";

		internal static AnimationActionMutation ExecuteHost(ScenarioActionCommand command)
		{
			AnimationPreparedCommand prepared = Prepare(command);
			AnimationTarget target = Resolve(prepared);
			AnimationActionMutation mutation = Mutate(target);
			AnimationState state = CaptureState(mutation);
			ObserveHost(state);
			PlayAnimPacket packet = CreatePacket(mutation);
			Send(packet);
			return mutation;
		}

		internal static AnimationPreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static AnimationTarget Resolve(AnimationPreparedCommand prepared)
		{
			int cell = 0;
			prepared?.Command?.TryGetInt("cell", out cell);
			KBatchedAnimController controller = global::Components.LiveMinionIdentities?.Items
				.Where(value => value != null && !value.IsNullOrDestroyed())
				.Select(value => value.GetComponent<KBatchedAnimController>())
				.FirstOrDefault(value => value != null && Grid.PosToCell(value.gameObject) == cell);
			NetworkIdentity identity = controller?.GetComponent<NetworkIdentity>();
			var target = new AnimationTarget
			{
				MinionNetId = identity?.NetId ?? 0,
				TargetNetId = 0,
				TargetCell = cell,
			};
			return ScenarioActionFlowContext.Attach(target, controller);
		}

		internal static AnimationActionMutation Mutate(AnimationTarget target)
		{
			KBatchedAnimController controller =
				ScenarioActionFlowContext.Get<KBatchedAnimController>(target);
			AnimationProfileMutation runtime = AnimationProfileRuntime.PlayWorkingLoop(controller);
			if (runtime == null) return null;
			var packet = new PlayAnimPacket(runtime.Identity.NetId,
				[new HashedString("working_loop")], false,
				KAnim.PlayMode.Loop, 1f, 0f)
			{
				ScenarioActionProfile = ScenarioActionReceiverGate.Mark("animation"),
			};
			return new AnimationActionMutation
			{
				Target = target,
				Runtime = runtime,
				Packet = packet,
			};
		}

		internal static AnimationState CaptureState(AnimationActionMutation mutation)
			=> State(mutation, "working_loop");

		internal static void ObserveHost(AnimationState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);

		internal static PlayAnimPacket CreatePacket(AnimationActionMutation mutation)
			=> mutation?.Packet;

		internal static void Send(PlayAnimPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static AnimationState ExecuteClient(PlayAnimPacket packet)
		{
			AnimationState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static AnimationState ApplyClient(PlayAnimPacket packet)
		{
			if (packet == null || !packet.ApplyRuntimePacket()) return null;
			return Attach(new AnimationState
			{
				Action = "Working", Animation = packet.AnimHashes[0].ToString(),
				Tool = "None", Progress = 0d,
			}, new AnimationTarget { MinionNetId = packet.NetId }, packet.TimeStamp, packet);
		}

		internal static void ObserveClient(AnimationState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static AnimationState ExecuteCleanup(AnimationActionMutation mutation)
		{
			AnimationState state = Restore(mutation);
			ObserveCleanup(state);
			PlayAnimPacket packet = CreateCleanupPacket(mutation);
			Send(packet);
			return state;
		}

		internal static AnimationState Restore(AnimationActionMutation mutation)
		{
			if (mutation?.Runtime == null || !AnimationProfileRuntime.Restore(mutation.Runtime))
				return null;
			mutation.Packet = new PlayAnimPacket(mutation.Runtime.Identity.NetId,
				[mutation.Runtime.PreviousAnim], false, mutation.Runtime.PreviousMode,
				mutation.Runtime.PreviousSpeed, mutation.Runtime.PreviousElapsed)
			{
				ScenarioActionProfile = ScenarioActionReceiverGate.Mark("animation"),
			};
			return State(mutation, mutation.Runtime.PreviousAnim.ToString());
		}

		internal static void ObserveCleanup(AnimationState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);

		internal static PlayAnimPacket CreateCleanupPacket(AnimationActionMutation mutation)
			=> mutation?.Packet;

		private static AnimationState State(AnimationActionMutation mutation, string animation)
			=> mutation == null ? null : Attach(new AnimationState
			{
				Action = "Working", Animation = animation, Tool = "None", Progress = 0d,
			}, mutation.Target, mutation.Packet.TimeStamp, mutation.Packet);

		private static AnimationState Attach(
			AnimationState state, AnimationTarget target, long revision, PlayAnimPacket packet)
			=> ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "animation", Revision = revision > 0 ? revision : 1,
				Target = target, State = state, EntryId = EntryId, Packet = packet,
			});
	}
}
#endif
