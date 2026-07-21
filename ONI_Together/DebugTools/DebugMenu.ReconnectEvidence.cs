#if DEBUG
using System.Linq;
using ONI_Together.Misc;
using ONI_Together.Networking;

namespace ONI_Together.DebugTools
{
	internal static class ReconnectScenarioEvidence
	{
		private const string Scenario = "reconnect-world-state";
		private static bool _armed;
		private static bool _host;
		private static ulong _peerId;
		private static MultiplayerPlayer _peer;
		private static object _connection;
		private static long _connectionGeneration;
		private static long _snapshotGeneration;

		internal static bool TryArm(MultiplayerPlayer peer, out string reason)
		{
			reason = string.Empty;
			if (peer?.Connection == null || peer.ConnectionGeneration <= 0
			    || _snapshotGeneration <= 0 || !ReferenceEquals(peer, _peer)
			    || !ReferenceEquals(peer.Connection, _connection)
			    || peer.ConnectionGeneration != _connectionGeneration)
			{
				reason = "completed-current-connection-generation-required";
				return false;
			}
			if (!TryCaptureState(out string state, out reason))
				return false;

			_host = MultiplayerSession.IsHost;
			_armed = true;
			IntegrationScenarioEvidenceCore.Log(
				Scenario, "final-state", _snapshotGeneration, true, state);
			reason = $"peer={_peerId};connectionGeneration={_connectionGeneration};" +
			         $"snapshotGeneration={_snapshotGeneration}";
			return true;
		}

		internal static void ObserveReadyConnection(
			MultiplayerPlayer peer, object connection, long snapshotGeneration)
		{
			if (peer == null || connection == null || snapshotGeneration <= 0
			    || !ReferenceEquals(connection, peer.Connection)
			    || peer.ConnectionGeneration <= 0)
				return;
			if (!_armed)
			{
				RememberConnection(peer, connection, snapshotGeneration);
				return;
			}

			if (_host != MultiplayerSession.IsHost || peer.PlayerId != _peerId
			    || !ReferenceEquals(connection, peer.Connection)
			    || snapshotGeneration <= _snapshotGeneration)
				return;
			bool staleConnectionAccepted =
				peer.IsCurrentConnection(_connection, _connectionGeneration);
			if (staleConnectionAccepted || !TryCaptureState(out string state, out _))
				return;

			if (_host)
			{
				IntegrationScenarioEvidenceCore.Log(
					Scenario, "host-submit", snapshotGeneration, true, state);
			}
			else
			{
				IntegrationScenarioEvidenceCore.Log(
					Scenario, "client-apply", snapshotGeneration, true, state);
				IntegrationScenarioEvidenceCore.Log(
					Scenario, "revision-accepted", snapshotGeneration, true, state);
				IntegrationScenarioEvidenceCore.Log(
					Scenario, "revision-duplicate", snapshotGeneration,
					GameClient.ShouldCompleteReadyAcceptance(
						GameClient.State, 0, snapshotGeneration), state);
				IntegrationScenarioEvidenceCore.Log(
					Scenario, "revision-out-of-order", _snapshotGeneration,
					GameClient.ShouldCompleteReadyAcceptance(
						GameClient.State, 0, _snapshotGeneration), state);
				IntegrationScenarioEvidenceCore.Log(
					Scenario, "client-original-blocked", _snapshotGeneration,
					staleConnectionAccepted, state);
			}
			IntegrationScenarioEvidenceCore.Log(
				Scenario, "post-reconnect-state", snapshotGeneration, true, state);
			_armed = false;
			RememberConnection(peer, connection, snapshotGeneration);
		}

		private static void RememberConnection(
			MultiplayerPlayer peer, object connection, long snapshotGeneration)
		{
			_peerId = peer.PlayerId;
			_peer = peer;
			_connection = connection;
			_connectionGeneration = peer.ConnectionGeneration;
			_snapshotGeneration = snapshotGeneration;
		}

		private static bool TryCaptureState(out string state, out string reason)
		{
			state = string.Empty;
			if (!SoakStateHash.TryCaptureCurrent(out SoakStateHashes hashes, out reason))
				return false;
			state = $"grid:{hashes.GridRecords}:{SoakStateHash.ToHex(hashes.Grid)}," +
			        $"entity:{hashes.EntityLifecycleRecords}:{SoakStateHash.ToHex(hashes.EntityLifecycle)}," +
			        $"world:{hashes.WorldMembershipRecords}:{SoakStateHash.ToHex(hashes.WorldMembership)}," +
			        $"storage:{hashes.StorageMembershipRecords}:{SoakStateHash.ToHex(hashes.StorageMembership)}," +
			        $"clusterRocket:{hashes.ClusterRocketRecords}:{SoakStateHash.ToHex(hashes.ClusterRocket)}";
			return true;
		}
	}

	public partial class DebugMenu
	{
		private static DebugCommandOutcome ArmReconnectEvidence()
		{
			const string command = "reconnect-evidence";
			if (!MultiplayerSession.InSession || !Utils.IsInGame()
			    || SpeedControlScreen.Instance?.IsPaused != true)
				return DebugCommandOutcome.Fail(command, "paused-multiplayer-world-required");
			if (!IsIntegrationMutationWindowOpen())
				return DebugCommandOutcome.Fail(command, "sync-checkpoint-active");
			var peers = MultiplayerSession.GetConnectedRemotePlayerIds();
			if (peers.Count != 1
			    || !MultiplayerSession.ConnectedPlayers.TryGetValue(
				    peers.Single(), out MultiplayerPlayer peer))
				return DebugCommandOutcome.Fail(command, "one-connected-peer-required");

			return ReconnectScenarioEvidence.TryArm(peer, out string reason)
				? DebugCommandOutcome.Ok(command, reason)
				: DebugCommandOutcome.Fail(command, reason);
		}
	}
}
#endif
