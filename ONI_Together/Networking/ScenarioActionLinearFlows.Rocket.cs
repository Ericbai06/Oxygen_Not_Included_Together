#if DEBUG
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.DLC.SpacedOut;
using ONI_Together.Patches.DLC.SpacedOut;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking
{
	internal sealed class RocketActionMutation
	{
		internal RocketTarget Target;
		internal RocketSettingsPacketData Previous;
		internal RocketSettingsStatePacket Packet;
	}

	internal sealed class RocketPreparedCommand { internal ScenarioActionCommand Command; }

	internal static class RocketActionFlow
	{
		private const string EntryId = "sync:b4c22ab1203aecc282cef612";

		internal static RocketActionMutation ExecuteHost(ScenarioActionCommand command)
		{
			RocketPreparedCommand prepared = Prepare(command);
			RocketTarget target = Resolve(prepared);
			RocketActionMutation mutation = Mutate(target);
			RocketState state = CaptureState(mutation);
			RocketSettingsStatePacket packet = CreatePacket(mutation);
			if (!Send(packet))
			{
				if (Restore(mutation) == null)
					Debug.LogError("[ONI_Together] Rocket action rollback failed");
				return null;
			}
			ObserveHost(state);
			return mutation;
		}

		internal static RocketPreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static RocketTarget Resolve(RocketPreparedCommand prepared)
		{
			int rocket = 0;
			int pad = 0;
			prepared?.Command?.TryGetInt("rocketNetId", out rocket);
			prepared?.Command?.TryGetInt("padNetId", out pad);
			return new RocketTarget { RocketNetId = rocket, PadNetId = pad };
		}

		internal static RocketActionMutation Mutate(RocketTarget target)
		{
			RocketSettingsPacketData previous = RocketProfileRuntime.ApplyNextBoarding(
				(int)target.RocketNetId, (int)target.PadNetId);
			if (previous == null) return null;
			if (!RocketSettingsSync.TryCaptureByTarget(
				    previous, out RocketSettingsPacketData applied))
			{
				RocketProfileRuntime.Restore(previous);
				return null;
			}
			RocketSettingsStatePacket packet =
				RocketSettingsStatePacket.CreateAuthoritative(applied);
			packet.ScenarioActionProfile = ScenarioActionReceiverGate.Mark("rocket");
			return new RocketActionMutation
			{
				Target = target, Previous = previous, Packet = packet,
			};
		}

		internal static RocketState CaptureState(RocketActionMutation mutation) => State(mutation);
		internal static void ObserveHost(RocketState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);
		internal static RocketSettingsStatePacket CreatePacket(RocketActionMutation mutation)
			=> mutation?.Packet;
		internal static bool Send(RocketSettingsStatePacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static RocketState ExecuteClient(RocketSettingsStatePacket packet)
		{
			RocketState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static RocketState ApplyClient(RocketSettingsStatePacket packet)
			=> packet != null && packet.ApplyRuntimePacket()
				? Attach(Target(packet.Data), packet) : null;
		internal static void ObserveClient(RocketState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static RocketState ExecuteCleanup(RocketActionMutation mutation)
		{
			RocketState state = Restore(mutation);
			if (state == null) return null;
			RocketSettingsStatePacket packet = CreateCleanupPacket(mutation);
			if (!Send(packet)) return null;
			ObserveCleanup(state);
			return state;
		}

		internal static RocketState Restore(RocketActionMutation mutation)
		{
			if (mutation?.Previous == null || !RocketProfileRuntime.Restore(mutation.Previous)
			    || !RocketSettingsSync.TryCaptureByTarget(
				    mutation.Previous, out RocketSettingsPacketData applied)) return null;
			mutation.Packet = RocketSettingsStatePacket.CreateAuthoritative(applied);
			mutation.Packet.ScenarioActionProfile = ScenarioActionReceiverGate.Mark("rocket");
			return State(mutation);
		}

		internal static void ObserveCleanup(RocketState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);
		internal static RocketSettingsStatePacket CreateCleanupPacket(RocketActionMutation mutation)
			=> mutation?.Packet;

		private static RocketState State(RocketActionMutation mutation)
			=> mutation == null ? null : Attach(mutation.Target, mutation.Packet);
		private static RocketTarget Target(RocketSettingsPacketData data)
			=> new()
			{
				RocketNetId = data.TargetNetId,
				PadNetId = data.HasPad ? data.PadNetId : data.CurrentPadNetId,
			};
		private static RocketState Attach(RocketTarget target, RocketSettingsStatePacket packet)
		{
			RocketSettingsPacketData data = packet.Data;
			var state = new RocketState
			{
				Destination = data.HasDestination ? data.DestinationQ + "," + data.DestinationR : "none",
				CraftPhase = data.CraftPhase.ToString(), SettingsRevision = (long)packet.Revision,
			};
			return ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "rocket", Revision = (long)packet.Revision, Target = target,
				State = state, EntryId = EntryId, Packet = packet,
			});
		}
	}
}
#endif
