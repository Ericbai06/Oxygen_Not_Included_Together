using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ResearchRequestLifecycleTests
	{
		private sealed class RequestFixture : IDisposable
		{
			private readonly ulong _hostId = MultiplayerSession.HostUserID;
			private readonly DispatchContext _context = PacketHandler.CurrentContext;
			private readonly Dictionary<ulong, MultiplayerPlayer> _players =
				new(MultiplayerSession.ConnectedPlayers);

			internal RequestFixture()
			{
				ResearchSyncCoordinator.ResetSessionState();
				MultiplayerSession.ConnectedPlayers.Clear();
				MultiplayerSession.HostUserID = 1;
			}

			internal MultiplayerPlayer AddReadyPlayer(ulong senderId)
			{
				var player = new MultiplayerPlayer(senderId);
				player.BeginConnection(new object());
				MakeReady(player);
				MultiplayerSession.ConnectedPlayers.Add(senderId, player);
				return player;
			}

			internal static void MakeReady(MultiplayerPlayer player)
			{
				player.ProtocolVerified = true;
				player.readyState = ClientReadyState.Ready;
			}

			public void Dispose()
			{
				ResearchSyncCoordinator.ResetSessionState();
				SetContext(_context);
				MultiplayerSession.ConnectedPlayers.Clear();
				foreach (KeyValuePair<ulong, MultiplayerPlayer> pair in _players)
					MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
				MultiplayerSession.HostUserID = _hostId;
			}
		}

		[UnitTest(name: "Research request sender and generation are transport-bound", category: "Sync")]
		public static UnitTestResult SenderAndGenerationBounds()
		{
			using var fixture = new RequestFixture();
			MultiplayerPlayer player = fixture.AddReadyPlayer(11);
			var invalid = new[]
			{
				new DispatchContext(0, false, player.ConnectionGeneration),
				new DispatchContext(11, true, player.ConnectionGeneration),
				new DispatchContext(11, false, 0),
				new DispatchContext(11, false, player.ConnectionGeneration + 1),
				new DispatchContext(99, false, player.ConnectionGeneration)
			};
			foreach (DispatchContext context in invalid)
			{
				SetContext(context);
				if (AcceptRequest(1, out _))
					return UnitTestResult.Fail("Invalid outer sender or generation entered request stream");
			}
			SetContext(Context(player));
			return AcceptRequest(1, out ulong senderId) && senderId == 11
				? UnitTestResult.Pass("Only current verified Ready transport identity enters the stream")
				: UnitTestResult.Fail("Current transport sender and generation were rejected");
		}

		[UnitTest(name: "Research requests are once per sender connection generation", category: "Sync")]
		public static UnitTestResult AtMostOncePerGeneration()
		{
			using var fixture = new RequestFixture();
			MultiplayerPlayer first = fixture.AddReadyPlayer(11);
			SetContext(Context(first));
			if (!AcceptRequest(1, out _) || AcceptRequest(1, out _)
			    || !AcceptRequest(2, out _) || AcceptRequest(1, out _))
				return UnitTestResult.Fail("Duplicate or stale request ID crossed one connection stream");
			long nextGeneration = first.BeginConnection(new object());
			RequestFixture.MakeReady(first);
			SetContext(Context(first));
			if (nextGeneration <= 1 || !AcceptRequest(1, out _))
				return UnitTestResult.Fail("New connection generation did not reopen request ID 1");
			MultiplayerPlayer second = fixture.AddReadyPlayer(22);
			SetContext(Context(second));
			if (!AcceptRequest(1, out _))
				return UnitTestResult.Fail("One sender cursor contaminated another sender");
			ResearchSyncCoordinator.ResetSessionState();
			SetContext(Context(first));
			return AcceptRequest(1, out _)
				? UnitTestResult.Pass("Duplicate/stale IDs lose; sender, generation, and session cuts are independent")
				: UnitTestResult.Fail("Session reset retained an old request cursor");
		}

		[UnitTest(name: "Research baseline and session reset all client revision state", category: "Sync")]
		public static UnitTestResult BaselineAndSessionReset()
		{
			ResearchSyncCoordinator.ResetSessionState();
			try
			{
				SetCoordinatorField("_appliedRevision", 9L);
				SetCoordinatorField("_nextClientRequestId", 8UL);
				SetCoordinatorField("_applyingAuthoritativeState", true);
				ResearchSyncCoordinator.ResetClientForBaseline(77);
				if (ResearchSyncCoordinator.AppliedResearchRevision != 0
				    || GetCoordinatorField<ulong>("_nextClientRequestId") != 0
				    || GetCoordinatorField<long>("_trackedSnapshotGeneration") != 77
				    || GetCoordinatorField<bool>("_applyingAuthoritativeState"))
					return UnitTestResult.Fail("Baseline retained client revision, request ID, or apply guard");
				SetCoordinatorField("_appliedRevision", 4L);
				SetCoordinatorField("_nextClientRequestId", 3UL);
				ResearchSyncCoordinator.ResetSessionState();
				bool cleared = ResearchSyncCoordinator.AppliedResearchRevision == 0
				               && GetCoordinatorField<ulong>("_nextClientRequestId") == 0
				               && GetCoordinatorField<long>("_trackedSnapshotGeneration") == 0;
				return cleared
					? UnitTestResult.Pass("Baseline and session reset clear their complete research lifecycle state")
					: UnitTestResult.Fail("Session reset retained research lifecycle state");
			}
			finally { ResearchSyncCoordinator.ResetSessionState(); }
		}

		[UnitTest(name: "Research reset integration reaches coordinator lifecycle cuts", category: "Sync")]
		public static UnitTestResult ResetIntegration()
		{
			MethodInfo baseline = Method(typeof(SessionStateReset), "ResetPresentationForBaseline");
			MethodInfo session = Method(typeof(SessionStateReset), "ResetCore");
			bool wired = Calls(baseline, typeof(ResearchSyncCoordinator), "ResetClientForBaseline")
			             && Calls(session, typeof(ResearchSyncCoordinator), "ResetSessionState");
			return wired
				? UnitTestResult.Pass("World baseline and full session reset both reach research state")
				: UnitTestResult.Fail("Research lifecycle reset is detached from session reset orchestration");
		}

		[UnitTest(name: "Research stale base snapshots without host mutation", category: "Sync")]
		public static UnitTestResult StaleBaseSnapshotContract()
		{
			MethodInfo handle = Method(typeof(ResearchSyncCoordinator), "HandleRequest");
			int baseCheck = FindCall(handle, typeof(ResearchSyncProtocol), "IsCurrentBase");
			int snapshot = FindCall(handle, typeof(ResearchSyncCoordinator), "SendCurrentSnapshot");
			int mutation = FindCall(handle, typeof(Research), "SetActiveResearch");
			MethodInfo sendSnapshot = Method(typeof(ResearchSyncCoordinator), "SendCurrentSnapshot");
			bool isolated = Calls(sendSnapshot, typeof(PacketSender), nameof(PacketSender.SendToPlayer))
			                && !Calls(sendSnapshot, typeof(Research), "SetActiveResearch");
			if (ResearchSyncProtocol.IsCurrentBase(4, 5) || !ResearchSyncProtocol.IsCurrentBase(5, 5)
			    || baseCheck < 0 || snapshot <= baseCheck || mutation <= snapshot || !isolated)
				return UnitTestResult.Fail("Stale base can reach mutation or cannot emit its current snapshot");
			return UnitTestResult.Pass("Stale base exits through snapshot before the only host mutation site");
		}

		private static DispatchContext Context(MultiplayerPlayer player)
			=> new(player.PlayerId, false, player.ConnectionGeneration);

		private static bool AcceptRequest(ulong requestId, out ulong senderId)
		{
			MethodInfo method = Method(typeof(ResearchSyncCoordinator), "TryAcceptRequestStream");
			object[] arguments = { requestId, 0UL };
			bool accepted = (bool)method.Invoke(null, arguments);
			senderId = (ulong)arguments[1];
			return accepted;
		}

		private static void SetContext(DispatchContext context)
		{
			PropertyInfo property = typeof(PacketHandler).GetProperty(
				nameof(PacketHandler.CurrentContext), BindingFlags.Public | BindingFlags.Static);
			MethodInfo setter = property?.GetSetMethod(true);
			if (setter == null) throw new MissingMethodException("PacketHandler.CurrentContext setter");
			setter.Invoke(null, new object[] { context });
		}

		private static void SetCoordinatorField(string name, object value)
		{
			FieldInfo field = CoordinatorField(name);
			field.SetValue(null, value);
		}

		private static T GetCoordinatorField<T>(string name)
			=> (T)CoordinatorField(name).GetValue(null);

		private static FieldInfo CoordinatorField(string name)
		{
			FieldInfo field = typeof(ResearchSyncCoordinator).GetField(
				name, BindingFlags.Static | BindingFlags.NonPublic);
			return field ?? throw new MissingFieldException(typeof(ResearchSyncCoordinator).Name, name);
		}

		private static MethodInfo Method(Type type, string name)
			=> AccessTools.Method(type, name);

		private static bool Calls(MethodInfo method, Type declaringType, string name)
			=> FindCall(method, declaringType, name) >= 0;

		private static int FindCall(MethodInfo method, Type declaringType, string name)
		{
			byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
			if (il == null) return -1;
			for (int index = 0; index <= il.Length - sizeof(int); index++)
			{
				MethodBase called = null;
				try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, index)); }
				catch (ArgumentException) { }
				if (called?.DeclaringType == declaringType && called.Name == name) return index;
			}
			return -1;
		}
	}
}
