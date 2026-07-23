#if DEBUG
using System.Globalization;
using System.Linq;
using ONI_Together.Misc;
using ONI_Together.Networking;

namespace ONI_Together.DebugTools
{
	internal static class ReconnectScenarioEvidence
	{
		private const string Scenario = "reconnect-world-state";
		private const string EntryId = "sync:e94ba2a3526636c98ae7e030";
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
			if (!TryCaptureState(
				peer.ConnectionGeneration, _snapshotGeneration, out ReconnectWorldStateState state,
				out reason))
				return false;

			_host = MultiplayerSession.IsHost;
			_armed = true;
			Log("final-state", _snapshotGeneration, state);
			reason = $"peer={_peerId};connectionGeneration={_connectionGeneration};" +
			         $"snapshotGeneration={_snapshotGeneration}";
			return true;
		}

		internal static bool CancelAutomationArm()
		{
			if (!_armed)
				return false;
			_armed = false;
			_peerId = 0;
			_peer = null;
			_connection = null;
			_connectionGeneration = 0;
			_snapshotGeneration = 0;
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
			if (staleConnectionAccepted || !TryCaptureState(
			    peer.ConnectionGeneration, snapshotGeneration,
			    out ReconnectWorldStateState state, out _))
				return;

			if (_host)
			{
				Log("host-submit", snapshotGeneration, state);
			}
			else
			{
				Log("client-apply", snapshotGeneration, state);
				Log("revision-accepted", snapshotGeneration, state);
			}
			Log("post-reconnect-state", snapshotGeneration, state);
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

		private static bool TryCaptureState(
			long connectionGeneration, long snapshotGeneration,
			out ReconnectWorldStateState state, out string reason)
		{
			state = null;
			if (!SoakStateHash.TryCaptureCurrent(out SoakStateHashes hashes, out reason))
				return false;
			state = new ReconnectWorldStateState
			{
				ConnectionGeneration = connectionGeneration,
				SnapshotGeneration = snapshotGeneration,
				Grid = Domain(hashes.GridRecords, hashes.Grid),
				Entity = Domain(hashes.EntityLifecycleRecords, hashes.EntityLifecycle),
				World = Domain(hashes.WorldMembershipRecords, hashes.WorldMembership),
				Storage = Domain(hashes.StorageMembershipRecords, hashes.StorageMembership),
				ClusterRocket = Domain(hashes.ClusterRocketRecords, hashes.ClusterRocket),
			};
			return true;
		}

		private static ReconnectDomainRecord Domain(int count, byte[] hash)
			=> new ReconnectDomainRecord
			{
				Count = count,
				Hash = "sha256:" + SoakStateHash.ToHex(hash).ToLowerInvariant(),
			};

		private static void Log(
			string phase, long revision, ReconnectWorldStateState state)
			=> IntegrationScenarioEvidenceCore.Log(TypedEvidenceRuntimeContext.Create(
				Scenario, phase, revision,
				new ReconnectWorldStateTarget
				{
					PeerId = _peerId.ToString(CultureInfo.InvariantCulture),
				},
				state, EntryId, connectionGeneration: state.ConnectionGeneration,
				snapshotGeneration: state.SnapshotGeneration));
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
