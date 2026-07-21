using KSerialization;
using ONI_Together.DebugTools;
using ONI_Together.Menus;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport.Steamworks;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Threading;
using Shared.Profiling;

namespace ONI_Together.Networking
{
	public partial class ReadyManager
	{
		private static void CompleteSyncBarrier(ulong clientId)
		{
			bool completed = _syncBarrier.Complete(clientId);
			if (completed)
			{
				UI.ChatScreen.CancelBufferedHistory(clientId);
				ReliableSyncBacklog.Clear(clientId);
			}
			FinishSyncBarrierIfNeeded(completed, committed: true);
		}

		private static void PruneSyncBarrier()
		{
			var expiredClients = new List<ulong>();
			bool changed = _syncBarrier.Prune(id =>
					IsPendingClientStillExpected(id),
				TimeSpan.FromSeconds(LoadingLeaseSeconds),
				TimeSpan.FromSeconds(LoadingAbsoluteLeaseSeconds),
				System.DateTime.UtcNow,
				expiredClients);
			foreach (ulong clientId in expiredClients)
			{
				DebugConsole.LogWarning($"[ReadyManager] Snapshot lease expired for {clientId}; aborting transfer");
				UI.ChatScreen.CancelBufferedHistory(clientId);
				SaveFileTransferManager.CancelTransfers(clientId);
				if (NetworkConfig.TransportServer is ONI_Together.Networking.Transport.Lan.RiptideServer server)
					server.TcpTransfer?.CancelTransfers(clientId);
				if (MultiplayerSession.ConnectedPlayers.TryGetValue(clientId, out MultiplayerPlayer player))
					player.CompleteSaveTransfer();
				ReliableSyncBacklog.Clear(clientId);
				NetworkConfig.TransportServer?.KickClient(clientId);
			}
			ReliableSyncBacklog.Prune(id => _syncBarrier.Contains(id));
			FinishSyncBarrierIfNeeded(changed, committed: false);
		}

		internal static void Update()
		{
			PruneCompletedReadyProofs(System.DateTime.UtcNow);
			if (!_syncBarrier.IsActive || UnityEngine.Time.unscaledTime < _nextBarrierPruneAt)
				return;
			_nextBarrierPruneAt = UnityEngine.Time.unscaledTime + 1f;
			PruneSyncBarrier();
		}

		private static bool IsPendingClientStillExpected(ulong clientId)
		{
			if (_syncBarrier.IsLoading(clientId, TimeSpan.FromSeconds(LoadingLeaseSeconds)))
				return true;

			if (MultiplayerSession.ConnectedPlayers.TryGetValue(clientId, out var player))
				return player.Connection != null;

			return NetworkConfig.TransportServer is ONI_Together.Networking.Transport.Lan.RiptideServer server
				&& server.IsClientLoading(clientId);
		}

		internal static bool TransferSyncBarrierClient(
			ulong oldClientId,
			ulong newClientId,
			ulong reconnectToken)
		{
			if (!MultiplayerSession.ConnectedPlayers.TryGetValue(newClientId, out var player))
				return false;
			if (!_syncBarrier.IsLoading(
				    oldClientId, TimeSpan.FromSeconds(LoadingLeaseSeconds)))
				return false;
			if (!_syncBarrier.CanReplace(oldClientId, newClientId, reconnectToken)
			    || !ReliableSyncBacklog.CanTransfer(oldClientId, newClientId))
				return false;
			if (!_syncBarrier.Replace(oldClientId, newClientId, reconnectToken))
				return false;
			if (!ReliableSyncBacklog.Transfer(oldClientId, newClientId))
			{
				_syncBarrier.Replace(newClientId, oldClientId, reconnectToken);
				return false;
			}

			player.readyState = ClientReadyState.Loading;
			return true;
		}

		internal static bool HasReconnectProof(ulong clientId, ulong reconnectToken)
			=> _syncBarrier.IsLoading(clientId, TimeSpan.FromSeconds(LoadingLeaseSeconds))
			   && _syncBarrier.HasReconnectProof(clientId, reconnectToken);

		internal static bool IsClientInSyncBarrier(ulong clientId)
			=> _syncBarrier.Contains(clientId);

		internal static bool IsCurrentSnapshot(ulong clientId, long snapshotGeneration)
			=> _syncBarrier.MatchesGeneration(clientId, snapshotGeneration);

		internal static void RecordReliableReplayProgress(ulong clientId)
		{
			if (ReliableSyncBacklog.IsReplaying(clientId))
				_syncBarrier.RecordWorldBaselineProgress(clientId, System.DateTime.UtcNow);
		}

		internal static void RecordWorldBaselineProgress(ulong clientId)
			=> _syncBarrier.RecordWorldBaselineProgress(clientId, System.DateTime.UtcNow);

		internal static void AbortSyncBarrier(ulong clientId)
		{
			UI.ChatScreen.CancelBufferedHistory(clientId);
			WorldDataRequestPacket.CancelTransfer(clientId);
			RemoveActiveLanLoadingProof(clientId);
			bool completed = _syncBarrier.Complete(clientId);
			ReliableSyncBacklog.Clear(clientId);
			FinishSyncBarrierIfNeeded(completed, committed: false);
		}

		internal static void ResetSessionState()
		{
			GameClient.CancelWorldLoadPhase();
			bool restoreAutomaticPause = ShouldRestoreAutomaticPauseOnReset(
				_syncBarrier.IsActive,
					_syncBarrier.WasPausedBeforeStart,
					_ownsAutomaticPause);
			SpeedControlScreen speed = SpeedControlScreen.Instance;
			RestoreAutomaticPauseOnReset(
				restoreAutomaticPause,
				speed != null && speed.IsPaused
					? () => SetPauseWithoutLocalPatch(speed, paused: false)
					: null);
			_syncBarrier.Reset();
			ReliableSyncBacklog.ClearAll();
			Interlocked.Exchange(ref _nextSnapshotGeneration, 0);
			_reconnectToken = 0;
			_clientSnapshotGeneration = 0;
			_pendingLoadingToken = 0;
			_pendingWorldLoad = null;
			_completedReadyProofs.Clear();
			_nextBarrierPruneAt = 0f;
			_ownsAutomaticPause = false;
		}

		internal static bool ShouldRestoreAutomaticPauseOnReset(
			bool barrierActive,
			bool wasPausedBeforeStart,
			bool ownsAutomaticPause)
			=> barrierActive && !wasPausedBeforeStart && ownsAutomaticPause;

		internal static void MarkAutomaticPauseOwnership()
		{
			_ownsAutomaticPause = true;
		}

		internal static void ClearAutomaticPauseOwnership()
		{
			if (ShouldClearAutomaticPauseOwnership(_syncBarrier.IsActive))
				_ownsAutomaticPause = false;
		}

		internal static bool ShouldClearAutomaticPauseOwnership(bool barrierActive)
			=> !barrierActive;

		internal static bool ShouldReleaseAutomaticPause(
			bool barrierShouldUnpause,
			bool ownsAutomaticPause)
			=> barrierShouldUnpause && ownsAutomaticPause;

		private static void FinishSyncBarrierIfNeeded(bool changed, bool committed)
		{
			if (!changed || _syncBarrier.IsActive)
				return;

			SpeedControlScreen speed = SpeedControlScreen.Instance;
			if (ShouldReleaseAutomaticPause(
				    _syncBarrier.ShouldUnpauseAfterCompletion,
				    _ownsAutomaticPause))
			{
				if (speed != null)
					SetPauseWithoutLocalPatch(speed, paused: false);
			}
			_ownsAutomaticPause = false;
			if (speed != null)
			{
				SpeedChangePacket.SpeedState state = speed.IsPaused
					? SpeedChangePacket.SpeedState.Paused
					: (SpeedChangePacket.SpeedState)speed.GetSpeed();
				SpeedChangePacket.SubmitLocalChange(state);
			}
			if (committed)
				GameServerHardSync.OnSyncBarrierCompleted();
			else
				GameServerHardSync.OnSyncBarrierAborted();
		}

		private static void SetPauseWithoutLocalPatch(SpeedControlScreen speed, bool paused)
		{
			ONI_Together.Patches.SpeedControlScreen_SendSpeedPacketPatch.IsSyncing = true;
			try
			{
				if (paused)
					speed.Pause(false);
				else
					speed.Unpause(false);
			}
			finally
			{
				ONI_Together.Patches.SpeedControlScreen_SendSpeedPacketPatch.IsSyncing = false;
			}
		}

	}
}
