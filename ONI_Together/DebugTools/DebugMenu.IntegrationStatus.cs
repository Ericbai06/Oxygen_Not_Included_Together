using ONI_Together.Misc;
using ONI_Together.Misc.World;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport.Steam;

namespace ONI_Together.DebugTools
{
#if DEBUG
	public partial class DebugMenu
	{
		private static DebugCommandOutcome GetIntegrationStatus()
		{
			bool host = MultiplayerSession.IsHost;
			string role = host ? "host" : "client";
			string state = host ? GameServer.State.ToString() : GameClient.State.ToString();
			int remotes = MultiplayerSession.GetConnectedRemotePlayerIds().Count;
			bool mutationWindow = IsIntegrationMutationWindowOpen();
			bool ready = MultiplayerSession.InSession && Utils.IsInGame() && remotes > 0
			             && mutationWindow
			             && (host
				             ? GameServer.State == ServerState.Started
				             : GameClient.State == ClientState.InGame);
			string reason = $"role={role};host={MultiplayerSession.HostUserID};" +
			                $"local={MultiplayerSession.LocalUserID};session={(MultiplayerSession.InSession ? 1 : 0)};" +
			                $"world={(Utils.IsInGame() ? 1 : 0)};state={state};remotes={remotes};" +
			                $"protocol={ProtocolCompatibility.CurrentProtocolVersion};" +
			                $"dlc={ProtocolCompatibility.FormatDlcIds(ProtocolCompatibility.ActiveDlcIds)};" +
			                $"mutationWindow={(mutationWindow ? 1 : 0)};" +
			                RepairStatus(host) + ";" + NativeTransportStatus();
			return ready
				? DebugCommandOutcome.Ok("status", reason)
				: DebugCommandOutcome.Fail("status", reason);
		}

		private static bool IsIntegrationMutationWindowOpen()
			=> !GameServerHardSync.IsHardSyncInProgress
			   && !ProductionDesyncRecovery.IsActive
			   && !ReadyManager.HasActiveSyncBarrier;

		private static string RepairStatus(bool host)
			=> host
				? $"repairCut={WorldUpdatePacket.CurrentHostRepairDispatchSequence};" +
				  $"repairJournal={WorldUpdateBatcher.RepairJournalPendingCount}"
				: $"repairResolved={WorldUpdatePacket.ClientResolvedRepairSequence};" +
				  $"repairDeferred={WorldUpdatePacket.PendingRepairPacketCount};" +
				  $"repairObserving={WorldUpdateRepairObservability.PendingCount};" +
				  $"repairAckTarget={WorldUpdateRepairObservability.AckTarget};" +
				  $"repairAckSent={WorldUpdateRepairObservability.LastAckSent}";

		private static string NativeTransportStatus()
		{
			SyncStats.NativeTransportSnapshot stats = SyncStats.GetNativeTransportSnapshot();
			GetSteamQueueHealth(out long queueUsec, out int unackedBytes);
			return $"txCalls={stats.TxCalls};txBytes={stats.TxBytes};txFailures={stats.TxFailures};" +
			       $"motionCalls={stats.MotionCalls};motionBytes={stats.MotionBytes};" +
			       $"animationCalls={stats.AnimationCalls};animationBytes={stats.AnimationBytes};" +
			       $"cursorCalls={stats.CursorCalls};cursorBytes={stats.CursorBytes};" +
			       $"steamQueueUsec={queueUsec};steamUnackedReliableBytes={unackedBytes}";
		}

		private static void GetSteamQueueHealth(out long queueUsec, out int unackedBytes)
		{
			queueUsec = -1;
			unackedBytes = -1;
			if (!NetworkConfig.IsSteamConfig())
				return;
			if (MultiplayerSession.IsHost)
				SteamworksServer.TryGetMaxQueueHealth(out queueUsec, out unackedBytes);
			else
				SteamworksClient.TryGetCurrentQueueHealth(out queueUsec, out unackedBytes);
		}
	}
#endif
}
