using ONI_Together.Networking;
using ONI_Together.Networking.Packets.DuplicantActions;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Animation;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using UnityEngine;
using System.Collections.Generic;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class PresentationDataPlaneTests
	{
		[UnitTest(name: "Presentation clock extrapolates and resets without rewind", category: "Sync")]
		public static UnitTestResult PresentationClockIsSessionMonotonic()
		{
			PresentationTickClock.ResetSessionState();
			PresentationTickClock.AdvanceLocalTickForTests();
			PresentationTickClock.AdvanceLocalTickForTests();
			bool corrected = PresentationTickClock.ObserveWorldClockForTests(100, 5);
			long synchronized = PresentationTickClock.EstimatedHostTickForTests();
			PresentationTickClock.AdvanceLocalTickForTests();
			long extrapolated = PresentationTickClock.EstimatedHostTickForTests();
			bool stale = PresentationTickClock.ObserveWorldClockForTests(200, 4);
			bool newerBehind = PresentationTickClock.ObserveWorldClockForTests(90, 6);
			long afterCorrection = PresentationTickClock.EstimatedHostTickForTests();
			PresentationTickClock.ResetSessionState();

			return corrected && synchronized == 100 && extrapolated == 101
			       && !stale && newerBehind && afterCorrection == 101
			       && PresentationTickClock.CurrentTick == 0
				? UnitTestResult.Pass("Sim-sub-ticks extrapolate from revisioned host offset without rewind")
				: UnitTestResult.Fail("Presentation sim tick rewound, accepted stale correction, or leaked across reset");
		}

		[UnitTest(name: "Presentation clock accepts newer baseline generation", category: "Sync")]
		public static UnitTestResult PresentationClockAcceptsBaselineGeneration()
		{
			PresentationTickClock.ResetSessionState();
			bool first = PresentationTickClock.ObserveBaselineClockForTests(400, 8);
			bool stale = PresentationTickClock.ObserveBaselineClockForTests(900, 7);
			return first && !stale && PresentationTickClock.EstimatedHostTickForTests() == 400
				? UnitTestResult.Pass("Completed snapshot generation provides a latest-only host sim tick")
				: UnitTestResult.Fail("Baseline clock accepted a stale generation");
		}

		[UnitTest(name: "Accepted baseline resets presentation revisions without rewinding tick", category: "Sync")]
		public static UnitTestResult BaselineResetsPresentationDataPlane()
		{
			PresentationTickClock.ResetSessionState();
			AnimSyncBatchPacket.ResetSessionState();
			DuplicantPresentationBatchPacket.ResetSessionState();
			RemoteMotionPresenter.ResetSessionState();
			PlayerCursorPacket.ResetSessionState();
			WorldCyclePacket.ClearState();
			PresentationTickClock.AdvanceLocalTickForTests();
			PresentationTickClock.AdvanceLocalTickForTests();
			bool initialTick = PresentationTickClock.ObserveBaselineClockForTests(400, 2);
			bool initialRevisions = Every(
				AnimSyncBatchPacket.AcceptEntityRevisionForTests(1, 2),
				DuplicantPresentationBatchPacket.AcceptEntityRevisionForTests(1, 2),
				EntityMotionBatchPacket.AcceptEntityRevisionForTests(1, 2),
				PlayerCursorPacket.AcceptRevisionForTests(9, 3, 2),
				WorldCyclePacket.AcceptRevisionForTests(2));

			bool reset = SessionStateReset.ResetPresentationForBaseline(100, 1);
			long afterBaseline = PresentationTickClock.EstimatedHostTickForTests();
			bool restartedRevisions = Every(
				AnimSyncBatchPacket.AcceptEntityRevisionForTests(1, 1),
				DuplicantPresentationBatchPacket.AcceptEntityRevisionForTests(1, 1),
				EntityMotionBatchPacket.AcceptEntityRevisionForTests(1, 1),
				PlayerCursorPacket.AcceptRevisionForTests(9, 3, 1),
				WorldCyclePacket.AcceptRevisionForTests(1),
				PresentationTickClock.ObserveWorldClockForTests(101, 1));
			PresentationTickClock.AdvanceLocalTickForTests();
			long afterAdvance = PresentationTickClock.EstimatedHostTickForTests();

			return Every(initialTick, initialRevisions, reset, restartedRevisions,
				       afterBaseline == 400, afterAdvance == 401)
				? UnitTestResult.Pass("Baseline restarts latest-only revisions while presentation tick remains monotonic")
				: UnitTestResult.Fail("Baseline retained a presentation revision or rewound the host tick estimate");
		}

		[UnitTest(name: "Presentation batches flush once per simulation sub-tick", category: "Sync")]
		public static UnitTestResult PresentationFlushUsesSimulationTickWindow()
		{
			NetworkingComponent.ResetPresentationFlushGateForTests(10);
			bool paused = !NetworkingComponent.ShouldFlushPresentationForTests(10)
			              && !NetworkingComponent.ShouldFlushPresentationForTests(10);
			bool nextTick = NetworkingComponent.ShouldFlushPresentationForTests(11)
			                && !NetworkingComponent.ShouldFlushPresentationForTests(11)
			                && NetworkingComponent.ShouldFlushPresentationForTests(12);
			return paused && nextTick
				? UnitTestResult.Pass("Queued presentation state waits for and flushes once in each 200ms simulation batch")
				: UnitTestResult.Fail("Presentation state flushed per frame or more than once in a simulation tick");
		}

		[UnitTest(name: "Latest motion transition replaces pending stop and transition", category: "Sync")]
		public static UnitTestResult LatestMotionTransitionWinsPendingWindow()
		{
			EntityMotionState stop = MotionState(7, 1f);
			stop.Kind = EntityMotionKind.Stop;
			stop.Revision = 2;
			EntityMotionState transition = MotionState(7, 2f);
			transition.Revision = 3;
			EntityMotionState latest = MotionState(7, 3f);
			latest.Revision = 4;

			return ReferenceEquals(RemoteMotionPresenter.SelectPendingForTests(stop, transition), transition)
			       && ReferenceEquals(RemoteMotionPresenter.SelectPendingForTests(transition, latest), latest)
			       && ReferenceEquals(RemoteMotionPresenter.SelectPendingForTests(latest, transition), latest)
				? UnitTestResult.Pass("Newest transition survives the pending 200ms window")
				: UnitTestResult.Fail("Pending stop/transition blocked or rewound a newer transition");
		}

		[UnitTest(name: "Duplicant presentation batches are bounded latest-only snapshots", category: "Sync")]
		public static UnitTestResult DuplicantPresentationBatchIsLatestOnly()
		{
			DuplicantPresentationBatchPacket.ResetSessionState();
			DuplicantPresentationEntry first = DuplicantState(7, 9, (100, 11));
			DuplicantPresentationEntry latest = DuplicantState(7, 10, (120, 22));
			DuplicantPresentationEntry other = DuplicantState(8, 11, (120, 33));
			DuplicantPresentationBatchPacket batch = DuplicantPresentationBatchPacket
				.CreateBatches([first, latest, other]).Single();
			DuplicantPresentationBatchPacket copy = RoundTrip(batch);

			bool revisions = Every(
				DuplicantPresentationBatchPacket.AcceptEntityRevisionForTests(7, 10),
				!DuplicantPresentationBatchPacket.AcceptEntityRevisionForTests(7, 10),
				!DuplicantPresentationBatchPacket.AcceptEntityRevisionForTests(7, 9));
			DuplicantPresentationBatchPacket.ResetSessionState();
			bool reset = DuplicantPresentationBatchPacket.AcceptEntityRevisionForTests(7, 1);

			DuplicantPresentationEntry copied = copy.Entries.Single(state => state.NetId == 7);
			return Every(copy.Entries.Length == 2, copied.Revision == 10,
				       copied.AnimHash == 22, copied.StartSimTick == 120,
				       copied.DurationTicks == 20,
				       WireBytes(batch) <= PacketSender.MAX_PACKET_SIZE_UNRELIABLE,
				       revisions, reset)
				? UnitTestResult.Pass("Duplicant presentation batches dedupe, bound wire size and reject stale state")
				: UnitTestResult.Fail("Duplicant presentation batch rewound or exceeded its datagram bound");
		}

		[UnitTest(name: "Duplicant presentation never invokes remote StandardWorker lifecycle", category: "Sync")]
		public static UnitTestResult DuplicantPresentationDoesNotStartWork()
		{
			Type legacyPacket = typeof(DuplicantPresentationBatchPacket).Assembly.GetType(
				"ONI_Together.Networking.Packets.Animation.StandardWorker_WorkingState_Packet");
			MethodInfo apply = typeof(RemoteDuplicantPresenter).GetMethod(
				"ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
			MethodInfo startWork = typeof(StandardWorker).GetMethod(
				nameof(StandardWorker.StartWork), BindingFlags.Instance | BindingFlags.Public);
			return legacyPacket == null && !Calls(apply, startWork)
				? UnitTestResult.Pass("Remote duplicants consume presentation only; StandardWorker lifecycle stays native")
				: UnitTestResult.Fail("Duplicant presentation can still invoke remote StartWork");
		}

		[UnitTest(name: "Cursor snapshots scope revisions to sender connections", category: "Sync")]
		public static UnitTestResult CursorPresentationIsConnectionScoped()
		{
			PlayerCursorPacket.ResetSessionState();
			bool revisions = PlayerCursorPacket.AcceptRevisionForTests(7, 11, 5)
			                 && !PlayerCursorPacket.AcceptRevisionForTests(7, 11, 5)
			                 && !PlayerCursorPacket.AcceptRevisionForTests(7, 11, 4)
			                 && PlayerCursorPacket.AcceptRevisionForTests(7, 12, 1)
			                 && !PlayerCursorPacket.AcceptRevisionForTests(7, 11, 6);
			var packet = new PlayerCursorPacket
			{
				PlayerID = 7,
				SenderConnectionGeneration = 999,
				Revision = 1,
				BuildingPrefabId = string.Empty
			};
			HostBroadcastPacket.BindSenderConnectionGeneration(packet, 12);
			PlayerCursorPacket copy = RoundTrip(packet);

			return revisions && packet.Revision == 1
			       && copy.SenderConnectionGeneration == 12 && copy.Revision == 1
				? UnitTestResult.Pass("Reconnect generations restart cursor revisions without accepting old-connection state")
				: UnitTestResult.Fail("Cursor revision gate crossed generations or the relay rewrote its revision");
		}

		[UnitTest(name: "Cursor utility path starts at cursor and stays viewport-contiguous", category: "Sync")]
		public static UnitTestResult CursorUtilityPathIsViewportLocal()
		{
			const int width = 10;
			var path = new List<BaseUtilityBuildTool.PathNode>
			{
				new() { cell = 11 }, new() { cell = 12 }, new() { cell = 13 },
				new() { cell = 14 }, new() { cell = 15 }, new() { cell = 16 },
			};
			var viewport = new CursorManager.CursorViewport
			{
				CursorCell = 16,
				Width = width,
				MinX = 3,
				MinY = 1,
				MaxX = 6,
				MaxY = 1,
			};
			List<BaseUtilityBuildTool.PathNode> selected = CursorManager.SelectViewportPath(
				path, viewport);
			var brokenPath = new List<BaseUtilityBuildTool.PathNode>
			{
				new() { cell = 13 }, new() { cell = 24 }, new() { cell = 15 }, new() { cell = 16 },
			};
			viewport.MaxY = 2;
			List<BaseUtilityBuildTool.PathNode> continuous = CursorManager.SelectViewportPath(
				brokenPath, viewport);

			return selected.Select(node => node.cell).SequenceEqual(new[] { 16, 15, 14, 13 })
			       && continuous.Select(node => node.cell).SequenceEqual(new[] { 16, 15 })
				? UnitTestResult.Pass("Cursor utility preview keeps the continuous visible suffix from the cursor")
				: UnitTestResult.Fail("Cursor utility preview included off-viewport cells or did not start at the cursor");
		}

		[UnitTest(name: "Cursor packet preserves visualizer command state", category: "Sync")]
		public static UnitTestResult CursorVisualizerCommandsPreserveState()
		{
			var packet = new PlayerCursorPacket
			{
				BuildingPrefabId = "WireBridge",
				BuildingOrientation = Orientation.R180,
				BuildingAllowed = true,
				Color = Color.cyan,
				AreaDownPos = new Vector3(2f, 3f),
				Position = new Vector3(5f, 7f),
				Dragging = true,
				DragMode = DragTool.Mode.Line,
				LengthLimit = new Vector2(2f, 2f),
			};
			Vector3 interpolated = new(4f, 6f);
			PlayerBuildingVisualizer.VisualState building =
				packet.CreateBuildingVisualState(interpolated);
			PlayerAreaVisualizer.VisualState area = packet.CreateAreaVisualState();

			return building.BuildingPrefabId == "WireBridge"
			       && building.Position == interpolated
			       && building.Orientation == Orientation.R180
			       && building.Color == Color.cyan && building.AllowedToPlace
			       && area.DownPosition == packet.AreaDownPos && area.CursorPosition == packet.Position
			       && area.Dragging && area.DragMode == DragTool.Mode.Line
			       && area.LengthLimit == packet.LengthLimit
				? UnitTestResult.Pass("Cursor packet maps every visual field into bounded command objects")
				: UnitTestResult.Fail("Cursor visualizer command mapping lost packet state");
		}

		[UnitTest(name: "Motion batches interpolate transitions and reject stale corrections", category: "Sync")]
		public static UnitTestResult MotionBatchIsBoundedAndLatestOnly()
		{
			var states = new List<EntityMotionState>();
			for (int netId = 1; netId <= EntityMotionBatchPacket.MaxEntriesPerBatch + 1; netId++)
				states.Add(MotionState(netId, netId));
			states.Add(MotionState(1, 999));
			List<EntityMotionBatchPacket> batches = EntityMotionBatchPacket.CreateBatches(states);
			EntityMotionState latest = batches.SelectMany(batch => batch.States)
				.Single(state => state.NetId == 1);

			EntityMotionBatchPacket.ResetSessionState();
			bool revisions = Every(
				EntityMotionBatchPacket.AcceptEntityRevisionForTests(1, latest.Revision),
				!EntityMotionBatchPacket.AcceptEntityRevisionForTests(1, latest.Revision),
				!EntityMotionBatchPacket.AcceptEntityRevisionForTests(1, latest.Revision - 1));
			Vector3 midpoint = RemoteMotionPresenter.EvaluatePosition(latest, 1_500);
			bool snap = RemoteMotionPresenter.ShouldSnapCorrection(
				Vector3.zero, new Vector3(1.6f, 0f, 0f));
			bool smooth = !RemoteMotionPresenter.ShouldSnapCorrection(
				Vector3.zero, new Vector3(1.4f, 0f, 0f));
			bool stopHeartbeat = RemoteMotionPresenter.HeartbeatKindForTests(
				hasNavigator: true, isMoving: false) == EntityMotionKind.Stop;
			MethodInfo applyOrientation = typeof(RemoteMotionPresenter).GetMethod(
				"ApplyOrientation", BindingFlags.Instance | BindingFlags.NonPublic);
			MethodInfo setNavType = typeof(Navigator).GetMethod(
				nameof(Navigator.SetCurrentNavType), BindingFlags.Instance | BindingFlags.Public);
			bool presentationOnly = !Calls(applyOrientation, setNavType);
			EntityMotionBatchPacket.ResetSessionState();

			return Every(batches.Count == 2, latest.Target == new Vector3(999f, 0f, 0f),
				       midpoint == new Vector3(499.5f, 0f, 0f),
				       batches.All(batch => MotionWireBytes(batch) <= PacketSender.MAX_PACKET_SIZE_UNRELIABLE),
				       revisions, snap, smooth, stopHeartbeat, presentationOnly,
				       EntityMotionBatchPacket.AcceptEntityRevisionForTests(1, 1))
				? UnitTestResult.Pass("Motion transitions interpolate from ticks and corrections are bounded latest-only state")
				: UnitTestResult.Fail("Motion state rewound, interpolated incorrectly, or exceeded its datagram bound");
		}

		private static DuplicantPresentationEntry DuplicantState(
			int netId, ulong revision, (long Tick, int AnimHash) animation) => new()
		{
			NetId = netId,
			Revision = revision,
			StartSimTick = animation.Tick,
			DurationTicks = 20,
			ActionState = DuplicantActionState.Working,
			AnimHash = animation.AnimHash,
			PlayMode = (byte)KAnim.PlayMode.Loop,
			AnimSpeed = 1f,
			AnimElapsedAtStart = 0.5f,
			IsWorking = true,
			WorkVisual = DuplicantWorkVisual.Working,
			TargetCell = 88,
			VisualTargetNetId = 99,
			ToolVisual = DuplicantToolVisual.None,
			Facing = DuplicantFacing.Right,
			ShowProgress = true,
			ProgressPercent = 0.5f,
			WorkTimeRemaining = 1f,
			WorkTimeTotal = 2f,
		};

		private static bool Every(params bool[] conditions)
			=> conditions.All(value => value);

		private static DuplicantPresentationBatchPacket RoundTrip(
			DuplicantPresentationBatchPacket packet)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);
			stream.Position = 0;
			var copy = new DuplicantPresentationBatchPacket();
			using var reader = new BinaryReader(stream);
			copy.Deserialize(reader);
			return copy;
		}

		private static PlayerCursorPacket RoundTrip(PlayerCursorPacket packet)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);
			stream.Position = 0;
			var copy = new PlayerCursorPacket();
			using var reader = new BinaryReader(stream);
			copy.Deserialize(reader);
			return copy;
		}

		private static int WireBytes(DuplicantPresentationBatchPacket packet)
		{
			using var stream = new MemoryStream();
			using var writer = new BinaryWriter(stream);
			writer.Write(0);
			packet.Serialize(writer);
			return checked((int)stream.Length);
		}

		private static EntityMotionState MotionState(int netId, float targetX) => new()
		{
			NetId = netId,
			Revision = (ulong)(1000 + netId) + (ulong)targetX,
			Kind = EntityMotionKind.Transition,
			StartSimTick = 1_000,
			Source = Vector3.zero,
			Target = new Vector3(targetX, 0f, 0f),
			DurationTicks = 1_000,
			StartNavType = NavType.Floor,
			EndNavType = NavType.Floor,
			Flags = EntityMotionFlags.FlipX,
		};

		private static int MotionWireBytes(EntityMotionBatchPacket packet)
		{
			using var stream = new MemoryStream();
			using var writer = new BinaryWriter(stream);
			writer.Write(0);
			packet.Serialize(writer);
			return checked((int)stream.Length);
		}

		private static bool Calls(MethodInfo caller, MethodInfo callee)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			if (il == null || callee == null) return false;
			byte[] token = System.BitConverter.GetBytes(callee.MetadataToken);
			for (int index = 0; index <= il.Length - token.Length; index++)
				if (il.Skip(index).Take(token.Length).SequenceEqual(token)) return true;
			return false;
		}
	}
}
