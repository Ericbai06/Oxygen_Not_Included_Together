#if DEBUG
using Klei.AI;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.DuplicantActions;
using ONI_Together.Patches.Duplicant;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking
{
	internal sealed class EffectActionMutation
	{
		internal EffectTarget Target;
		internal EffectProfileMutation Runtime;
		internal ToggleEffectPacket Packet;
	}

	internal sealed class EffectPreparedCommand { internal ScenarioActionCommand Command; }

	internal static class EffectActionFlow
	{
		private const string EntryId = "sync:f60e38b805c1052cff0fec0d";

		internal static EffectActionMutation ExecuteHost(ScenarioActionCommand command)
		{
			EffectPreparedCommand prepared = Prepare(command);
			EffectTarget target = Resolve(prepared);
			EffectActionMutation mutation = Mutate(target);
			EffectState state = CaptureState(mutation);
			ObserveHost(state);
			ToggleEffectPacket packet = CreatePacket(mutation);
			Send(packet);
			return mutation;
		}

		internal static EffectPreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static EffectTarget Resolve(EffectPreparedCommand prepared)
		{
			int netId = 0;
			prepared?.Command?.TryGetInt("netId", out netId);
			var target = new EffectTarget { MinionNetId = netId };
			if (NetworkIdentityRegistry.TryGet(netId, out NetworkIdentity identity))
				ScenarioActionFlowContext.Attach(target, identity.GetComponent<Effects>());
			return target;
		}

		internal static EffectActionMutation Mutate(EffectTarget target)
		{
			Effects effects = ScenarioActionFlowContext.Get<Effects>(target);
			EffectProfileMutation runtime = EffectProfileRuntime.Toggle(effects);
			if (runtime == null) return null;
			EffectInstance current = effects.Get(new HashedString(runtime.EffectId));
			NetworkIdentity identity = effects.GetComponent<NetworkIdentity>();
			ToggleEffectPacket packet = current != null
				? new ToggleEffectPacket(identity, current)
				: new ToggleEffectPacket(identity, new HashedString(runtime.EffectId));
			packet.ScenarioActionProfile = ScenarioActionReceiverGate.Mark("effect");
			return new EffectActionMutation { Target = target, Runtime = runtime, Packet = packet };
		}

		internal static EffectState CaptureState(EffectActionMutation mutation)
			=> State(mutation);

		internal static void ObserveHost(EffectState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);

		internal static ToggleEffectPacket CreatePacket(EffectActionMutation mutation)
			=> mutation?.Packet;

		internal static void Send(ToggleEffectPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static EffectState ExecuteClient(ToggleEffectPacket packet)
		{
			EffectState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static EffectState ApplyClient(ToggleEffectPacket packet)
		{
			if (packet == null || !packet.ApplyRuntimePacket()) return null;
			return Attach(new EffectState
			{
				EffectHash = packet.EffectHash.ToString(), Active = packet.IsAdding,
			}, new EffectTarget { MinionNetId = packet.MinionNetId }, (long)packet.Revision, packet);
		}

		internal static void ObserveClient(EffectState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static EffectState ExecuteCleanup(EffectActionMutation mutation)
		{
			EffectState state = Restore(mutation);
			ObserveCleanup(state);
			ToggleEffectPacket packet = CreateCleanupPacket(mutation);
			Send(packet);
			return state;
		}

		internal static EffectState Restore(EffectActionMutation mutation)
		{
			if (mutation?.Runtime == null || !EffectProfileRuntime.Restore(mutation.Runtime))
				return null;
			Effects effects = mutation.Runtime.Effects;
			NetworkIdentity identity = effects.GetComponent<NetworkIdentity>();
			EffectInstance current = effects.Get(new HashedString(mutation.Runtime.EffectId));
			mutation.Packet = current != null
				? new ToggleEffectPacket(identity, current)
				: new ToggleEffectPacket(identity, new HashedString(mutation.Runtime.EffectId));
			mutation.Packet.ScenarioActionProfile = ScenarioActionReceiverGate.Mark("effect");
			return State(mutation);
		}

		internal static void ObserveCleanup(EffectState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);

		internal static ToggleEffectPacket CreateCleanupPacket(EffectActionMutation mutation)
			=> mutation?.Packet;

		private static EffectState State(EffectActionMutation mutation)
			=> mutation == null ? null : Attach(new EffectState
			{
				EffectHash = mutation.Packet.EffectHash.ToString(),
				Active = mutation.Packet.IsAdding,
			}, mutation.Target, (long)mutation.Packet.Revision, mutation.Packet);

		private static EffectState Attach(
			EffectState state, EffectTarget target, long revision, ToggleEffectPacket packet)
			=> ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "effect", Revision = revision, Target = target, State = state,
				EntryId = EntryId, Packet = packet,
			});
	}
}
#endif
