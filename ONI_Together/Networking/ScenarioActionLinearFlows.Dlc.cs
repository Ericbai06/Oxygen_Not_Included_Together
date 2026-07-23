#if DEBUG
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.DLC.SpacedOut;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking
{
	internal sealed class DlcActionMutation
	{
		internal DlcRuntimeTarget Target;
		internal DlcProfileMutation Runtime;
		internal DlcRuntimeProfilePacket Packet;
	}

	internal sealed class DlcPreparedCommand { internal ScenarioActionCommand Command; }

	internal static class DlcRuntimeActionFlow
	{
		private const string EntryId = "sync:dlc-runtime-profile-client-apply";

		internal static DlcActionMutation ExecuteHost(ScenarioActionCommand command)
		{
			DlcPreparedCommand prepared = Prepare(command);
			DlcRuntimeTarget target = Resolve(prepared);
			DlcRuntimeTarget attached = Attach(target);
			DlcActionMutation mutation = Mutate(attached,
				"RobotIdleMonitor.idle", "RobotIdleMonitor.working");
			DlcRuntimeState state = CaptureState(mutation);
			DlcRuntimeProfilePacket packet = CreatePacket(mutation);
			if (!Send(packet))
			{
				if (Restore(mutation) == null)
					Debug.LogError("[ONI_Together] DLC action rollback failed");
				return null;
			}
			ObserveHost(state);
			return mutation;
		}

		internal static DlcPreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static DlcRuntimeTarget Resolve(DlcPreparedCommand prepared)
			=> new()
			{
				DlcFamily = prepared?.Command?.Selector["dlcFamily"],
				Prefab = prepared?.Command?.Selector["prefab"],
				Identity = prepared?.Command?.Selector["identity"],
			};

		internal static DlcRuntimeTarget Attach(DlcRuntimeTarget target)
		{
			if (target != null && NetworkIdentityRegistry.TryPrepareProfileFixture(
				target.Prefab, target.Identity, out NetworkIdentity identity,
				out DlcRuntimeProfileFixture fixture, out bool created))
				ScenarioActionFlowContext.Attach(target,
					new DlcAttachedTarget { Identity = identity, Fixture = fixture, Created = created });
			return target;
		}

		internal static DlcActionMutation Mutate(
			DlcRuntimeTarget target, string fromState, string toState)
		{
			DlcAttachedTarget attached = ScenarioActionFlowContext.Get<DlcAttachedTarget>(target);
			if (attached == null || fromState != "RobotIdleMonitor.idle"
			    || toState != "RobotIdleMonitor.working") return null;
			DlcProfileMutation runtime = DlcRuntimeProfileRuntime.TransitionToNext(
				attached.Identity.gameObject, target.DlcFamily, attached.Fixture, attached.Created);
			if (runtime == null) return null;
			bool working = !runtime.PreviousWorking;
			return new DlcActionMutation
			{
				Target = target, Runtime = runtime,
				Packet = Packet(runtime.Identity.NetId, working),
			};
		}

		internal static DlcRuntimeState CaptureState(DlcActionMutation mutation) => State(mutation);
		internal static void ObserveHost(DlcRuntimeState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);
		internal static DlcRuntimeProfilePacket CreatePacket(DlcActionMutation mutation)
			=> mutation?.Packet;
		internal static bool Send(DlcRuntimeProfilePacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static DlcRuntimeState ExecuteClient(DlcRuntimeProfilePacket packet)
		{
			DlcRuntimeState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static DlcRuntimeState ApplyClient(DlcRuntimeProfilePacket packet)
			=> packet != null && packet.ApplyRuntimePacket()
				? AttachState(new DlcRuntimeTarget
				{
					DlcFamily = "SpacedOut", Prefab = ScoutRoverConfig.ID, Identity = "rover-7",
				}, packet) : null;
		internal static void ObserveClient(DlcRuntimeState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static DlcRuntimeState ExecuteCleanup(DlcActionMutation mutation)
		{
			DlcRuntimeState state = Restore(mutation);
			if (state == null) return null;
			DlcRuntimeProfilePacket packet = CreateCleanupPacket(mutation);
			if (!Send(packet)) return null;
			ObserveCleanup(state);
			return state;
		}

		internal static DlcRuntimeState Restore(DlcActionMutation mutation)
		{
			if (mutation?.Runtime == null || !DlcRuntimeProfileRuntime.Restore(mutation.Runtime))
				return null;
			mutation.Packet = Packet(mutation.Runtime.Identity.NetId, mutation.Runtime.PreviousWorking);
			return State(mutation);
		}

		internal static void ObserveCleanup(DlcRuntimeState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);
		internal static DlcRuntimeProfilePacket CreateCleanupPacket(DlcActionMutation mutation)
			=> mutation?.Packet;

		private static DlcRuntimeProfilePacket Packet(int netId, bool working)
			=> new()
			{
				NetId = netId,
				Revision = NetworkIdentityRegistry.NextAuthorityRevision(),
				Working = working,
				ScenarioActionProfile = ScenarioActionReceiverGate.Mark("dlc-runtime"),
			};
		private static DlcRuntimeState State(DlcActionMutation mutation)
			=> mutation == null ? null : AttachState(mutation.Target, mutation.Packet);
		private static DlcRuntimeState AttachState(DlcRuntimeTarget target, DlcRuntimeProfilePacket packet)
		{
			var state = new DlcRuntimeState
			{
				StateMachineState = packet.Working ? "RobotIdleMonitor.working" : "RobotIdleMonitor.idle",
				AdmissionGeneration = (long)packet.Revision,
			};
			return ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "dlc-runtime", Revision = (long)packet.Revision, Target = target,
				State = state, EntryId = EntryId, Packet = packet,
			});
		}

		private sealed class DlcAttachedTarget
		{
			internal NetworkIdentity Identity;
			internal DlcRuntimeProfileFixture Fixture;
			internal bool Created;
		}
	}
}
#endif
