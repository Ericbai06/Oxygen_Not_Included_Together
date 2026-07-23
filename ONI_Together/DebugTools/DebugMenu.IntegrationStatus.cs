using ONI_Together.Misc;
using ONI_Together.Misc.World;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport.Steam;
using ONI_Together.Networking.Transport.Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
			IntegrationPreflightFacts facts = CreatePreflightFacts(
				host, role, state, remotes, mutationWindow);
			if (!IntegrationPreflightStatus.TryCreate(facts, out var status, out string error))
				return DebugCommandOutcome.Fail(
					"status", "preflightError=" + IntegrationPreflightStatus.Escape(error));
			string reason = status.Format();
			return ready
				? DebugCommandOutcome.Ok("status", reason)
				: DebugCommandOutcome.Fail("status", reason);
		}

		private static IntegrationPreflightFacts CreatePreflightFacts(
			bool host, string role, string state, int remotes, bool mutationWindow)
		{
			bool steam = NetworkConfig.IsSteamConfig();
			string lobbyId = steam
				? SteamLobby.CurrentLobby.m_SteamID.ToString(CultureInfo.InvariantCulture)
				: string.Empty;
			var statusFields = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["host"] = MultiplayerSession.HostUserID.ToString(CultureInfo.InvariantCulture),
				["local"] = MultiplayerSession.LocalUserID.ToString(CultureInfo.InvariantCulture),
				["session"] = MultiplayerSession.InSession ? "1" : "0",
				["world"] = Utils.IsInGame() ? "1" : "0",
				["state"] = state,
				["remotes"] = remotes.ToString(CultureInfo.InvariantCulture),
				["dlc"] = ProtocolCompatibility.FormatDlcIds(ProtocolCompatibility.ActiveDlcIds),
				["mutationWindow"] = mutationWindow ? "1" : "0",
			};
			if (steam) statusFields["lobby"] = lobbyId;
			AddDelimitedFields(statusFields, RepairStatus(host));
			AddDelimitedFields(statusFields, NativeTransportStatus());

			return new IntegrationPreflightFacts
			{
				GameBuild = BuildWatermark.GetBuildText(),
				Transport = steam ? IntegrationTransportKind.Steam : IntegrationTransportKind.NonSteam,
				TransportReady = MultiplayerSession.IsTransportConnected,
				InSteamLobby = steam && SteamLobby.InLobby,
				SteamLobbyId = lobbyId,
				NonSteamSessionIdentity = steam ? string.Empty : GetNonSteamSessionIdentity(host),
				ActiveDlcIds = ProtocolCompatibility.ActiveDlcIds,
				Protocol = ProtocolCompatibility.CurrentProtocolVersion,
				Role = role,
				StatusFields = statusFields,
			};
		}

		private static string GetNonSteamSessionIdentity(bool host)
		{
			long generation = MultiplayerSession.ConnectedPlayers.Values
				.Select(player => player.ConnectionGeneration)
				.Where(value => value > 0)
				.DefaultIfEmpty(0)
				.Max();
			if (generation <= 0)
				return string.Empty;
			string address = host
				? Configuration.Instance.Host.LanSettings.Ip
				: MultiplayerSession.ServerIp;
			int port = host
				? Configuration.Instance.Host.LanSettings.Port
				: MultiplayerSession.ServerPort;
			return $"lan:{address}:{port}/session-{generation}";
		}

		private static void AddDelimitedFields(
			IDictionary<string, string> fields, string encodedFields)
		{
			foreach (string item in encodedFields.Split(';'))
			{
				int separator = item.IndexOf('=');
				if (separator <= 0)
					throw new InvalidOperationException("Integration status field is malformed.");
				fields.Add(item.Substring(0, separator), item.Substring(separator + 1));
			}
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
