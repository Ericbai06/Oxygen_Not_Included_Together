using System;
using System.Collections.Generic;
using System.Globalization;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static partial class ScenarioNativeActions
	{
		internal static DebugCommandOutcome Research(ScenarioActionCommand command)
		{
			if (!ScenarioActionTargets.RequireHostWorld(command, out DebugCommandOutcome failure))
				return failure;
			Research research = global::Research.Instance;
			if (research == null || Db.Get()?.Techs == null)
				return ScenarioActionTargets.Fail(command, "research-runtime-unavailable");
			string techId = command.Selector["techId"];
			Tech target = Db.Get().Techs.TryGet(techId);
			TechInstance targetState = target == null ? null : research.Get(target);
			if (targetState == null || targetState.IsComplete())
				return ScenarioActionTargets.Fail(command, "incomplete-tech-required");
			Tech previous = research.GetActiveResearch()?.tech;
			if (previous == target)
				return ScenarioActionTargets.Fail(command, "different-inactive-tech-required");
			research.SetActiveResearch(target, true);
			return ScenarioActionTargets.Arm(command, cleanup =>
				RestoreResearch(cleanup, research, previous));
		}

		private static DebugCommandOutcome RestoreResearch(
			ScenarioActionCommand command,
			Research research,
			Tech previous)
		{
			if (research == null || global::Research.Instance != research)
				return ScenarioActionTargets.Fail(command, "research-runtime-changed");
			research.SetActiveResearch(previous, true);
			return ScenarioActionTargets.Restored(command);
		}

		internal static DebugCommandOutcome Schedule(ScenarioActionCommand command)
		{
			if (!ScenarioActionTargets.RequireHostWorld(command, out DebugCommandOutcome failure))
				return failure;
			if (!TryResolveSchedule(command.Selector["scheduleId"],
				    out Schedule target, out int index))
				return ScenarioActionTargets.Fail(command, "schedule-id-not-found");
			bool previous = target.alarmActivated;
			target.alarmActivated = !previous;
			ScheduleSyncCoordinator.PublishHostMutation();
			return ScenarioActionTargets.Arm(command, cleanup =>
				RestoreSchedule(cleanup, target, index, previous));
		}

		private static bool TryResolveSchedule(
			string scheduleId,
			out Schedule schedule,
			out int index)
		{
			schedule = null;
			index = -1;
			List<Schedule> schedules = ScheduleManager.Instance?.schedules;
			if (schedules == null)
				return false;
			if (scheduleId.StartsWith("schedule-", StringComparison.Ordinal)
			    && int.TryParse(scheduleId.Substring(9), NumberStyles.None,
				    CultureInfo.InvariantCulture, out index)
			    && index >= 0 && index < schedules.Count)
				schedule = schedules[index];
			else
				index = schedules.FindIndex(value => value?.name == scheduleId);
			if (schedule == null && index >= 0)
				schedule = schedules[index];
			return schedule != null;
		}

		private static DebugCommandOutcome RestoreSchedule(
			ScenarioActionCommand command,
			Schedule target,
			int index,
			bool previous)
		{
			List<Schedule> schedules = ScheduleManager.Instance?.schedules;
			if (schedules == null || index < 0 || index >= schedules.Count
			    || !ReferenceEquals(schedules[index], target))
				return ScenarioActionTargets.Fail(command, "schedule-runtime-changed");
			target.alarmActivated = previous;
			ScheduleSyncCoordinator.PublishHostMutation();
			return ScenarioActionTargets.Restored(command);
		}

		internal static DebugCommandOutcome Chat(ScenarioActionCommand command)
		{
			if (!ScenarioActionTargets.RequireHostWorld(command, out DebugCommandOutcome failure))
				return failure;
			if (!command.TryGetUInt64("sender", out ulong sender)
			    || sender != MultiplayerSession.LocalUserID)
				return ScenarioActionTargets.Fail(command, "local-host-sender-required");
			if (CursorManager.Instance == null)
				return ScenarioActionTargets.Fail(command, "cursor-manager-required-for-chat-color");
			if (!ScenarioActionReceiptStore.TryArm(command, cleanup =>
			    DebugCommandOutcome.Ok(cleanup.RawCommand, "chat-append-only-cleanup-complete")))
				return ScenarioActionTargets.Fail(command, "cleanup-receipt-already-armed");
			var packet = new ChatMessagePacket(
				"ONI Together integration scenario " + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			try
			{
				packet.PublishHostLocal();
				return ScenarioActionTargets.Applied(command);
			}
			catch
			{
				ScenarioActionReceiptStore.Discard(command);
				throw;
			}
		}

		internal static DebugCommandOutcome Cursor(ScenarioActionCommand command)
		{
			if (!ScenarioActionTargets.RequireHostWorld(command, out DebugCommandOutcome failure))
				return failure;
			if (!command.TryGetUInt64("playerId", out ulong playerId)
			    || playerId != MultiplayerSession.LocalUserID)
				return ScenarioActionTargets.Fail(command, "local-player-required");
			CursorManager target = CursorManager.Instance;
			if (target == null)
				return ScenarioActionTargets.Fail(command, "cursor-manager-unavailable");
			CursorState previous = target.cursorState;
			target.cursorState = previous == CursorState.DIG ? CursorState.SELECT : CursorState.DIG;
			return ScenarioActionTargets.Arm(command, cleanup =>
			{
				if (target == null || target.IsNullOrDestroyed())
					return ScenarioActionTargets.Fail(cleanup, "cursor-manager-destroyed");
				target.cursorState = previous;
				return ScenarioActionTargets.Restored(cleanup);
			});
		}
	}
}
#endif
