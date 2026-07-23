using System;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Tools.Build;
using ONI_Together.Patches.ToolPatches.Build;

namespace ONI_Together.DebugTools
{
#if DEBUG
	public partial class DebugMenu
	{
		private const string NetworkBuildCommandPrefix = "build:";
		private const string ReadyReplayLoadCommandPrefix = "replay-load:";

		internal static bool TryParseNetworkBuildCommand(
			string command, out string prefabId, out int cell, out string materialTag)
		{
			prefabId = string.Empty;
			materialTag = string.Empty;
			cell = -1;
			if (string.IsNullOrWhiteSpace(command)
			    || !command.StartsWith(NetworkBuildCommandPrefix, StringComparison.Ordinal))
				return false;
			string[] parts = command.Split(':');
			if (parts.Length != 4 || !IsAutomationIdentifier(parts[1])
			    || !int.TryParse(parts[2], out cell) || !BuildRequestValidator.IsWireCell(cell)
			    || !IsAutomationIdentifier(parts[3]))
				return false;
			prefabId = parts[1];
			materialTag = parts[3];
			return true;
		}

		internal static bool TryParseReadyReplayLoadCommand(
			string command, out int frameCount, out int payloadBytes)
		{
			frameCount = 0;
			payloadBytes = 0;
			if (string.IsNullOrWhiteSpace(command)
			    || !command.StartsWith(ReadyReplayLoadCommandPrefix, StringComparison.Ordinal))
				return false;
			string[] parts = command.Split(':');
			return parts.Length == 3
			       && int.TryParse(parts[1], out frameCount)
			       && int.TryParse(parts[2], out payloadBytes)
			       && ReadyReplayLoadPacket.IsValidShape(
				       runId: 1, index: 0, count: frameCount, payloadBytes: payloadBytes);
		}

		private static bool IsAutomationIdentifier(string value)
			=> !string.IsNullOrEmpty(value) && value.Length <= 128
			   && value.All(character => char.IsLetterOrDigit(character)
			      || character is '_' or '-');

		private static DebugCommandOutcome ExecuteNetworkBuild(
			string prefabId, int cell, string materialTag)
		{
			const string command = "build";
			if (!MultiplayerSession.IsHostInSession || !Grid.IsValidCell(cell))
				return DebugCommandOutcome.Fail(command, "active-host-and-valid-cell-required");
			if (!IsIntegrationMutationWindowOpen())
				return DebugCommandOutcome.Fail(command, "sync-checkpoint-active");
			if (HostBuildPolicyProvider.Current.InstantBuild)
				return DebugCommandOutcome.Fail(command, "non-instant-host-required");
			var request = new BuildRequest(
				BuildToolPatch.NextOperationId(), prefabId,
				new SinglePlacementGeometry(cell, Orientation.Neutral),
				new[] { materialTag }, BuildRequestValidator.DefaultFacade, 0, 5,
				(int)global::ObjectLayer.Building);
			AuthoritativeBuildExecutor.SetSessionEpoch(request.OperationId.SessionEpoch);
			if (!AuthoritativeBuildExecutor.Execute(request, new HostBuildPolicy(false),
				out BuildCommit publishedCommit, out BuildRejected rejection))
				return DebugCommandOutcome.Fail(command, rejection?.Message ?? "build-rejected");
			BuildPublisher.Publish(publishedCommit);
			PlacementOutcome publishedState = publishedCommit.Placements.FirstOrDefault();
			if (publishedState == null)
				return DebugCommandOutcome.Fail(command, "commit-empty");
			IntegrationScenarioEvidenceCore.Log(
				TypedEvidenceRuntimeContext.Create(
					scenario: "building-lifecycle", phase: "host-submit",
						revision: (long)publishedCommit.Revision.Value,
					target: new BuildingLifecycleTarget
					{
						Prefab = prefabId,
						Cell = publishedState.Cell,
						NetId = publishedState.NetId,
					},
					state: new BuildingLifecycleState
					{
						LifecycleRevision = (long)publishedCommit.Revision.Value,
						Queued = true,
						Completed = false,
					},
					entryId: "sync:c898f1c14a6f951b3ef66100"));
			return DebugCommandOutcome.Ok(
				command, $"prefab={prefabId};cell={cell};material={materialTag}");
		}

		private static DebugCommandOutcome StartProductionCheckpoint()
		{
			const string command = "checkpoint";
			if (!MultiplayerSession.IsHostInSession || GameClock.Instance == null)
				return DebugCommandOutcome.Fail(command, "active-host-world-required");
			if (!IsIntegrationMutationWindowOpen())
				return DebugCommandOutcome.Fail(command, "sync-checkpoint-active");
			int cycle = GameClock.Instance.GetCycle();
			return ProductionDesyncRecovery.TryBeginCycleProbe(cycle)
				? DebugCommandOutcome.Ok(command, $"cycle={cycle}")
				: DebugCommandOutcome.Fail(command, "checkpoint-rejected");
		}

		private static DebugCommandOutcome StartIntegrationHardSync()
		{
			const string command = "hard-sync";
			if (!MultiplayerSession.IsHostInSession || GameClock.Instance == null)
				return DebugCommandOutcome.Fail(command, "active-host-world-required");
			if (!IsIntegrationMutationWindowOpen())
				return DebugCommandOutcome.Fail(command, "sync-checkpoint-active");

			GameServerHardSync.PerformHardSync(consumeDailyUse: false);
			return GameServerHardSync.IsHardSyncInProgress
				? DebugCommandOutcome.Ok(command, "started")
				: DebugCommandOutcome.Fail(command, "no-ready-clients");
		}

		private static DebugCommandOutcome ExecuteReadyReplayLoad(
			int frameCount, int payloadBytes)
		{
			const string command = "replay-load";
			if (!MultiplayerSession.IsHostInSession
			    || !GameServerHardSync.IsHardSyncInProgress
			    || !ReadyManager.HasActiveSyncBarrier)
				return DebugCommandOutcome.Fail(command, "active-hard-sync-required");

			ulong[] clients = MultiplayerSession.GetConnectedRemotePlayerIds()
				.Where(ReadyManager.IsClientInSyncBarrier).ToArray();
			if (clients.Length != 1)
				return DebugCommandOutcome.Fail(command, "one-loading-client-required");
			if (!ReliableSyncBacklog.IsCollecting(clients[0]))
				return DebugCommandOutcome.Fail(command, "ready-replay-window-not-open");

			int before = ReliableSyncBacklog.CountForTests(clients[0]);
			int runId = ReadyReplayLoadPacket.NextRunId();
			for (int index = 0; index < frameCount; index++)
				PacketSender.SendToAllClients(
					new ReadyReplayLoadPacket(runId, index, frameCount, payloadBytes),
					PacketSendMode.ReliableImmediate);
			int buffered = ReliableSyncBacklog.CountForTests(clients[0]) - before;
			if (buffered != frameCount)
				return DebugCommandOutcome.Fail(
					command, $"buffered={buffered};expected={frameCount}");
			return DebugCommandOutcome.Ok(
				command, $"run={runId};frames={frameCount};payloadBytes={payloadBytes}");
		}
	}
#endif
}
