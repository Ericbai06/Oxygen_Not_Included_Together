using ONI_Together.Misc;
using ONI_Together.Networking;
using ONI_Together.Networking.States;
using ONI_Together.Patches.ToolPatches;
using System;
using Shared.Profiling;
using UnityEngine;
#if DEBUG
using ONI_Together.Tests;
#endif

namespace ONI_Together.DebugTools
{
	public partial class DebugMenu : MonoBehaviour
	{
#if DEBUG
		internal static DebugCommandOutcome EnsurePausedForAutomation(
			bool isHost,
			Func<bool> isPaused,
			System.Action setPaused,
			System.Action publishPaused)
		{
			if (!isHost)
				return DebugCommandOutcome.Fail("pause", "host-session-required");

			bool alreadyPaused = isPaused();
			if (!alreadyPaused)
			{
				setPaused();
				if (!isPaused())
					return DebugCommandOutcome.Fail("pause", "pause-not-applied");
			}
			publishPaused();
			return DebugCommandOutcome.Ok(
				"pause", alreadyPaused ? "already-paused" : "paused");
		}

		private void PollAutomationCommand()
		{
			if (Time.unscaledTime < nextAutomationCommandPollAt)
				return;
			nextAutomationCommandPollAt = Time.unscaledTime + AutomationCommandPollInterval;

			string command = "read";
			try
			{
				if (!System.IO.File.Exists(automationClaimPath))
				{
					if (!System.IO.File.Exists(automationCommandPath))
						return;
					System.IO.File.Move(automationCommandPath, automationClaimPath);
				}

				command = System.IO.File.ReadAllText(automationClaimPath).Trim();
				LogCommandOutcome(ExecuteAutomationCommand(command));
				System.IO.File.Delete(automationClaimPath);
			}
			catch (Exception ex)
			{
				LogCommandOutcome(DebugCommandOutcome.Fail(
					command, $"{ex.GetType().Name}: {ex.Message}"));
			}
		}

		private static DebugCommandOutcome ExecuteAutomationCommand(string command)
		{
			try
			{
				if (TryParseNetworkBuildCommand(
					    command, out string prefabId, out int cell, out string materialTag))
					return ExecuteNetworkBuild(prefabId, cell, materialTag);
				if (TryParseReadyReplayLoadCommand(
					    command, out int frameCount, out int payloadBytes))
					return ExecuteReadyReplayLoad(frameCount, payloadBytes);
				if (TryParseSteamJoinCommand(command, out string lobbyCode))
					return StartConfiguredSteamJoin(lobbyCode);
				if (command?.StartsWith("steam-join", StringComparison.Ordinal) == true)
					return DebugCommandOutcome.Fail("steam-join", "valid-lobby-code-required");

				return command switch
				{
					"tests" => RunAllUnitTests(),
					"riptide" => RunRiptideSmokeTest(),
					"host" => StartConfiguredLanHost(),
					"join" => StartConfiguredLanJoin(),
					"steam-host" => StartConfiguredSteamHost(),
					"status" => GetIntegrationStatus(),
					"checkpoint" => StartProductionCheckpoint(),
					"hard-sync" => StartIntegrationHardSync(),
					"pause" => PauseConfiguredHost(),
					"soak" => SoakStateHashProbe.Start(),
					"reconnect-evidence" => ArmReconnectEvidence(),
					_ => DebugCommandOutcome.Fail(command, "unknown-command"),
				};
			}
			catch (Exception ex)
			{
				return DebugCommandOutcome.Fail(
					command, $"{ex.GetType().Name}: {ex.Message}");
			}
		}

		private static void LogCommandOutcome(DebugCommandOutcome outcome)
		{
			if (outcome.Success)
				DebugConsole.Log(outcome.ToLogLine());
			else
				DebugConsole.LogWarning(outcome.ToLogLine());
		}

		private static DebugCommandOutcome RunAllUnitTests()
		{
			if (!CanRunFullUnitTests(MultiplayerSession.InSession))
				return DebugCommandOutcome.Fail("tests", "active-multiplayer-session");
			bool discovered = UnitTestRegistry.DiscoverTests();
			bool passed = UnitTestRegistry.RunAll();
			return discovered && passed
				? DebugCommandOutcome.Ok("tests", "completed")
				: DebugCommandOutcome.Fail(
					"tests", discovered ? "test-failures" : "discovery-failed");
		}

		private static bool CanRunFullUnitTests(bool inMultiplayerSession)
			=> !inMultiplayerSession;

		internal static bool CanRunFullUnitTestsForTests(bool inMultiplayerSession)
			=> CanRunFullUnitTests(inMultiplayerSession);

		private static DebugCommandOutcome RunRiptideSmokeTest()
		{
			try
			{
				RiptideSmokeTest.Run("127.0.0.1", 27777);
				return DebugCommandOutcome.Ok("riptide", "completed");
			}
			catch (Exception ex)
			{
				return DebugCommandOutcome.Fail(
					"riptide", $"{ex.GetType().Name}: {ex.Message}");
			}
		}

		private static DebugCommandOutcome StartConfiguredLanHost()
		{
			if (Utils.IsInGame())
			{
				if (MultiplayerSession.InSession)
					return MultiplayerSession.IsHostInSession
						? DebugCommandOutcome.Ok("host", "already-hosting")
						: DebugCommandOutcome.Fail("host", "session-already-active");

				MultiplayerSession.ShouldHostAfterLoad = false;
				NetworkConfig.StartServer();
				return DebugCommandOutcome.Ok("host", "start-requested");
			}

			if (!Utils.IsInMenu())
				return DebugCommandOutcome.Fail("host", "main-menu-or-world-required");

			Configuration.Instance.Host.NetworkTransport =
				(int)NetworkConfig.NetworkTransport.RIPTIDE;
			NetworkConfig.UpdateTransport(NetworkConfig.NetworkTransport.RIPTIDE);
			Configuration.Instance.Save();

			string latestSave = SaveLoader.GetLatestSaveForCurrentDLC();
			if (string.IsNullOrEmpty(latestSave) || !System.IO.File.Exists(latestSave))
				return DebugCommandOutcome.Fail("host", "latest-save-not-found");

			MultiplayerSession.ShouldHostAfterLoad = true;
			KCrashReporter.MOST_RECENT_SAVEFILE = latestSave;
			SaveLoader.SetActiveSaveFilePath(latestSave);
			App.LoadScene("backend");
			return DebugCommandOutcome.Ok("host", "load-and-host-requested");
		}

		private static DebugCommandOutcome StartConfiguredLanJoin()
		{
			if (!Utils.IsInMenu())
				return DebugCommandOutcome.Fail("join", "main-menu-required");

			NetworkConfig.UpdateTransport(NetworkConfig.NetworkTransport.RIPTIDE);
			var settings = Configuration.Instance.Client.LanSettings;
			GameClient.ConnectToHost(ip: settings.Ip, port: settings.Port);
			return DebugCommandOutcome.Ok("join", "connect-requested");
		}

		private static DebugCommandOutcome PauseConfiguredHost()
		{
			if (!MultiplayerSession.IsHostInSession)
				return DebugCommandOutcome.Fail("pause", "host-session-required");
			SpeedControlScreen speed = SpeedControlScreen.Instance;
			if (speed == null)
				return DebugCommandOutcome.Fail("pause", "speed-controls-unavailable");
			return EnsurePausedForAutomation(
				isHost: true,
				isPaused: () => speed.IsPaused,
				setPaused: SoakTickBarrier.EnsureLocallyPaused,
				publishPaused: () => Networking.Packets.World.SpeedChangePacket.SubmitLocalChange(
					Networking.Packets.World.SpeedChangePacket.SpeedState.Paused));
		}

#endif
	}
}
