#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Tools.Prioritize;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using ONI_Together.Patches.World;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class PriorityV10Tests
	{
		private readonly struct RevisionSeed
		{
			internal readonly int NetId;
			internal readonly ulong Lifecycle;
			internal readonly ulong State;

			internal RevisionSeed(int netId, ulong lifecycle, ulong state)
			{
				NetId = netId;
				Lifecycle = lifecycle;
				State = state;
			}
		}

		private sealed class RequestFixture : IDisposable
		{
			private readonly bool originalHost = MultiplayerSession.IsHost;
			private readonly ulong originalHostId = MultiplayerSession.HostUserID;
			private readonly Dictionary<ulong, MultiplayerPlayer> originalPlayers =
				new(MultiplayerSession.ConnectedPlayers);

			internal readonly MultiplayerPlayer Player;

			internal RequestFixture()
			{
				MultiplayerSession.ConnectedPlayers.Clear();
				MultiplayerSession.IsHost = true;
				MultiplayerSession.HostUserID = 1;
				Player = new MultiplayerPlayer(9);
				Player.BeginConnection(new object());
				Player.ProtocolVerified = true;
				Player.readyState = ClientReadyState.Ready;
				MultiplayerSession.ConnectedPlayers.Add(Player.PlayerId, Player);
			}

			internal DispatchContext Context(long generationOffset = 0)
				=> new DispatchContext(
					Player.PlayerId, false, Player.ConnectionGeneration + generationOffset)
					.AsVerifiedHostBroadcast();

			public void Dispose()
			{
				MultiplayerSession.ConnectedPlayers.Clear();
				foreach (KeyValuePair<ulong, MultiplayerPlayer> pair in originalPlayers)
					MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
				MultiplayerSession.IsHost = originalHost;
				MultiplayerSession.HostUserID = originalHostId;
			}
		}

		[UnitTest(name: "Priority v10 request wire carries authority revisions", category: "Sync")]
		public static UnitTestResult RequestWireAndBounds()
		{
			PrioritizeTargetRequestPacket copy = Roundtrip(Request());
			if (copy.SenderId != 9 || copy.ClientRequestId != 4 || copy.NetId != -31
			    || copy.TargetLifecycleRevision != 7 || copy.BasePriorityRevision != 3
			    || copy.PriorityClass != (int)PriorityScreen.PriorityClass.high
			    || copy.PriorityValue != 8)
				return UnitTestResult.Fail("Priority request lost sender, request, lifecycle, or base revision");
			if (!RejectRequest(packet => packet.SenderId = 0)
			    || !RejectRequest(packet => packet.ClientRequestId = 0)
			    || !RejectRequest(packet => packet.ClientRequestId = (ulong)long.MaxValue + 1)
			    || !RejectRequest(packet => packet.NetId = 0)
			    || !RejectRequest(packet => packet.TargetLifecycleRevision = 0)
			    || !RejectRequest(packet => packet.TargetLifecycleRevision = (ulong)long.MaxValue + 1)
			    || !RejectRequest(packet => packet.BasePriorityRevision = (ulong)long.MaxValue + 1))
				return UnitTestResult.Fail("Priority request accepted a zero or unsupported authority field");
			return UnitTestResult.Pass("Priority request wire is v10-only and bounds every authority field");
		}

		[UnitTest(name: "Priority requests use MustExecute host-authoritative relay", category: "Sync")]
		public static UnitTestResult MustExecuteAuthorityMarkers()
		{
			PrioritizeTargetRequestPacket request = Request();
			if (request is not IClientRelayable || request is not ISenderBoundRelay
			    || request is not IHostAuthoritativeRelay
			    || ((ISenderBoundRelay)request).RelaySenderId != request.SenderId)
				return UnitTestResult.Fail("Priority request is not sender-bound host-authoritative relay input");
			MethodInfo domainMethod = Method(typeof(HostBroadcastPacket), "GetRelayDomain");
			object domain = domainMethod?.Invoke(null, new object[] { request });
			HostBroadcastPacket.ResetClientRequestSequences();
			HostBroadcastPacket first = PacketSender.CreateHostRelayForClient(request, request.SenderId);
			HostBroadcastPacket second = PacketSender.CreateHostRelayForClient(request, request.SenderId);
			bool valid = domain?.ToString() == "MustExecute"
			             && first.RequestId == 1 && second.RequestId == 2;
			HostBroadcastPacket.ResetClientRequestSequences();
			return valid
				? UnitTestResult.Pass("Priority requests execute in one reliable ordered command domain")
				: UnitTestResult.Fail("Priority request used latest-state relay or a non-monotonic command sequence");
		}

		[UnitTest(name: "Priority request gate binds sender generation and verification", category: "Sync")]
		public static UnitTestResult RequestTransportBinding()
		{
			using var fixture = new RequestFixture();
			PrioritizeTargetRequestPacket valid = Request();
			PrioritizeTargetRequestPacket wrongSender = Request();
			wrongSender.SenderId = 8;
			bool accepted = Accept(valid, fixture.Context(), protocolVerified: true);
			bool rejected = !Accept(wrongSender, fixture.Context(), protocolVerified: true)
			                && !Accept(valid, fixture.Context(1), protocolVerified: true)
			                && !Accept(valid, fixture.Context(), protocolVerified: false)
			                && !Accept(valid, new DispatchContext(9, false), protocolVerified: true);
			return accepted && rejected
				? UnitTestResult.Pass("Only the current verified sender connection enters priority authority")
				: UnitTestResult.Fail("Priority request admitted wrong sender, stale generation, or unverified relay");
		}

		[UnitTest(name: "Priority lifecycle and stale base correct before mutation", category: "Sync")]
		public static UnitTestResult LifecycleBaseAndCorrectionOrder()
		{
			MethodInfo dispatch = Method(typeof(PrioritizeTargetRequestPacket), nameof(IPacket.OnDispatched));
			int currentIdentity = FindCall(dispatch, typeof(PrioritizeStatePacket), "IsCurrentHostIdentity");
			int hostRevision = FindCall(dispatch, typeof(PrioritizeStatePacket), "GetHostRevision");
			int correction = FindCall(dispatch, typeof(PrioritizeStatePacket), "SendHostCorrection");
			int mutation = FindCall(dispatch, typeof(PrioritizeStatePacket), "ApplyHostRequest");
			bool readsLifecycle = ReadsField(dispatch, nameof(PrioritizeTargetRequestPacket.TargetLifecycleRevision));
			bool readsBase = ReadsField(dispatch, nameof(PrioritizeTargetRequestPacket.BasePriorityRevision));
			MethodInfo correct = Method(typeof(PrioritizeStatePacket), "SendHostCorrection");
			bool correctionOnly = Calls(correct, typeof(PrioritizeStatePacket), "TryCaptureHostSnapshot")
			                      && Calls(correct, typeof(PacketSender), nameof(PacketSender.SendToPlayer))
			                      && !Calls(correct, typeof(PrioritizeStatePacket), "ApplyHostRequest");
			bool ordered = currentIdentity >= 0 && hostRevision > currentIdentity
			               && correction > hostRevision && mutation > correction;
			return readsLifecycle && readsBase && correctionOnly && ordered
				? UnitTestResult.Pass("Exact live lifecycle and base revision gate mutation; stale state gets correction")
				: UnitTestResult.Fail("Priority lifecycle/base correction no longer precedes host mutation");
		}

		[UnitTest(name: "Priority state is lifecycle-scoped latest-only", category: "Sync")]
		public static UnitTestResult LatestOnlyAndReset()
		{
			PrioritizeStatePacket.ResetSessionState();
			try
			{
				SeedCursor("ClientRevisions", new RevisionSeed(-41, 7, 2));
				bool rejectsOne = !AcceptClient(new RevisionSeed(-41, 7, 1));
				bool rejectsTwo = !AcceptClient(new RevisionSeed(-41, 7, 2));
				bool acceptsThree = AcceptClient(new RevisionSeed(-41, 7, 3));
				bool acceptsNewLifecycle = AcceptClient(new RevisionSeed(-41, 8, 1));
				PrioritizeStatePacket.ResetClientRevisionState();
				bool resetAcceptsOne = AcceptClient(new RevisionSeed(-41, 7, 1));
				return rejectsOne && rejectsTwo && acceptsThree
				       && acceptsNewLifecycle && resetAcceptsOne
					? UnitTestResult.Pass("Priority 2 rejects 1/2; new lifecycle and reset accept revision 1")
					: UnitTestResult.Fail("Priority latest-only cursor crossed lifecycle or reset boundary");
			}
			finally { PrioritizeStatePacket.ResetSessionState(); }
		}

		[UnitTest(name: "Priority state wire rejects bad identity and revisions", category: "Sync")]
		public static UnitTestResult StateWireBounds()
		{
			PrioritizeStatePacket.PriorityData badClass = State(-40, 1, 1);
			badClass.PriorityClass = 99;
			bool rejected = RejectState(State(0, 1, 1))
			                && RejectState(State(-40, 0, 1))
			                && RejectState(State(-40, 1, 0))
			                && RejectState(State(-40, (ulong)long.MaxValue + 1, 1))
			                && RejectState(State(-40, 1, (ulong)long.MaxValue + 1))
			                && RejectState(badClass);
			return rejected
				? UnitTestResult.Pass("Priority state rejects zero identity/revision and invalid enum bounds")
				: UnitTestResult.Fail("Priority state accepted bad identity, revision, or enum");
		}

		[UnitTest(name: "Priority host revisions are per identity lifecycle", category: "Sync")]
		public static UnitTestResult HostCountersAreLifecycleScoped()
		{
			PrioritizeStatePacket.ResetSessionState();
			try
			{
				ulong first = NextHost(new RevisionSeed(-51, 10, 0), advance: true);
				ulong second = NextHost(new RevisionSeed(-51, 10, 0), advance: true);
				ulong newLifecycle = NextHost(new RevisionSeed(-51, 11, 0), advance: true);
				ulong other = NextHost(new RevisionSeed(-52, 10, 0), advance: true);
				return first == 1 && second == 2 && newLifecycle == 1 && other == 1
					? UnitTestResult.Pass("Host priority counters restart independently per identity lifecycle")
					: UnitTestResult.Fail("Host priority counter leaked across identity or lifecycle");
			}
			finally { PrioritizeStatePacket.ResetSessionState(); }
		}

		[UnitTest(name: "Priority periodic snapshots are viewport-bounded bulk state", category: "Sync")]
		public static UnitTestResult PeriodicSnapshotContract()
		{
			var packet = new PrioritizeStatePacket();
			for (int index = 0; index < PrioritizeStatePacket.MaxPriorityCount; index++)
				packet.Priorities.Add(State(-1000 - index, 1, 1));
			bool bounded = packet is IBulkablePacket && packet.MaxPackSize == 64
			               && packet.IntervalMs == 50 && !SerializeRejects(packet);
			packet.Priorities.Add(State(-2000, 1, 1));
			MethodInfo periodic = Method(typeof(PrioritizeStatePacket), "SendPeriodicSnapshot");
			MethodInfo viewing = Method(typeof(PrioritizeStatePacket), "SendToViewingClients");
			MethodInfo sweep = Method(typeof(WorldStateSyncer), "SyncPriorities");
			bool routed = Calls(periodic, typeof(PrioritizeStatePacket), "TryCaptureHostSnapshot")
			              && Calls(periodic, typeof(PrioritizeStatePacket), "SendToViewingClients")
			              && Calls(viewing, typeof(WorldStateSyncer), "GetClientsViewingCell")
			              && Calls(viewing, typeof(PacketSender), nameof(PacketSender.SendToPlayer))
			              && !Calls(viewing, typeof(PacketSender), nameof(PacketSender.SendToAllClients))
			              && Calls(sweep, typeof(PrioritizeStatePacket), "SendPeriodicSnapshot");
			return bounded && SerializeRejects(packet) && routed
				? UnitTestResult.Pass("Periodic priority repair is 64-entry bulk state sent only to viewers")
				: UnitTestResult.Fail("Priority repair exceeded bounds or escaped viewport targeting");
		}

		[UnitTest(name: "Priority clients never originate authoritative state", category: "Sync")]
		public static UnitTestResult ClientHasNoAuthoritativeSource()
		{
			MethodInfo client = Method(typeof(UserMenuPriorityPatch), nameof(UserMenuPriorityPatch.Prefix));
			MethodInfo host = Method(typeof(PrioritizablePatch), nameof(PrioritizablePatch.Postfix));
			bool requestOnly = Calls(client, typeof(PrioritizeTargetRequestPacket), "TryCreateClientRequest")
			                   && Calls(client, typeof(PacketSender), nameof(PacketSender.SendToAllOtherPeers))
			                   && !Calls(client, typeof(Prioritizable), "SetMasterPriority");
			bool hostSource = Calls(host, typeof(PrioritizeStatePacket), "PublishHostMutation");
			bool stateGate = PrioritizeStatePacket.ShouldApply(false, true)
			                 && !PrioritizeStatePacket.ShouldApply(true, true)
			                 && !PrioritizeStatePacket.ShouldApply(false, false);
			return requestOnly && hostSource && stateGate
				? UnitTestResult.Pass("Clients emit requests; only host mutation and host state are authoritative")
				: UnitTestResult.Fail("A client path can originate or apply non-host priority authority");
		}

		[UnitTest(name: "Priority lifecycle cuts reset revision trackers", category: "Sync")]
		public static UnitTestResult ResetHooks()
		{
			MethodInfo baseline = Method(typeof(WorldDataPacket), "TryAcceptFinalLifecycleBaseline");
			MethodInfo session = Method(typeof(SessionStateReset), "ResetCore");
			bool wired = Calls(baseline, typeof(PrioritizeStatePacket), "ResetClientRevisionState")
			             && Calls(session, typeof(PrioritizeStatePacket), "ResetSessionState");
			PrioritizeStatePacket.ResetSessionState();
			ulong first = PrioritizeStatePacket.BeginClientRequest(-61);
			ulong second = PrioritizeStatePacket.BeginClientRequest(-62);
			PrioritizeStatePacket.ResetSessionState();
			ulong reset = PrioritizeStatePacket.BeginClientRequest(-61);
			PrioritizeStatePacket.ResetSessionState();
			return wired && first == 1 && second == 2 && reset == 1
				? UnitTestResult.Pass("Baseline and session cuts reset priority revisions and request sequence")
				: UnitTestResult.Fail("Priority reset hook or client request sequence survived a lifecycle cut");
		}

		private static PrioritizeTargetRequestPacket Request()
			=> new()
			{
				SenderId = 9, ClientRequestId = 4, NetId = -31,
				TargetLifecycleRevision = 7, BasePriorityRevision = 3,
				PriorityClass = (int)PriorityScreen.PriorityClass.high, PriorityValue = 8
			};

		private static PrioritizeStatePacket.PriorityData State(
			int netId, ulong lifecycle, ulong revision)
			=> new()
			{
				NetId = netId, LifecycleRevision = lifecycle, StateRevision = revision,
				PriorityClass = (int)PriorityScreen.PriorityClass.basic, PriorityValue = 5
			};

		private static bool RejectRequest(Action<PrioritizeTargetRequestPacket> mutate)
		{
			PrioritizeTargetRequestPacket packet = Request();
			mutate(packet);
			return SerializeRejects(packet);
		}

		private static bool RejectState(PrioritizeStatePacket.PriorityData data)
		{
			var packet = new PrioritizeStatePacket();
			packet.Priorities.Add(data);
			return SerializeRejects(packet);
		}

		private static bool Accept(
			PrioritizeTargetRequestPacket packet,
			DispatchContext context,
			bool protocolVerified)
		{
			MethodInfo method = Method(typeof(PrioritizeTargetRequestPacket), "ShouldAccept");
			return (bool)method.Invoke(packet, new object[] { true, context, protocolVerified });
		}

		private static bool AcceptClient(RevisionSeed seed)
		{
			MethodInfo method = Method(typeof(PrioritizeStatePacket), "ShouldAcceptClientRevision");
			return (bool)method.Invoke(null, new object[] { seed.NetId, seed.Lifecycle, seed.State });
		}

		private static ulong NextHost(RevisionSeed seed, bool advance)
		{
			MethodInfo method = Method(typeof(PrioritizeStatePacket), "NextHostRevision");
			return (ulong)method.Invoke(null, new object[] { seed.NetId, seed.Lifecycle, advance });
		}

		private static void SeedCursor(string dictionaryName, RevisionSeed seed)
		{
			Type cursorType = typeof(PrioritizeStatePacket).GetNestedType(
				"RevisionCursor", BindingFlags.NonPublic);
			object cursor = Activator.CreateInstance(cursorType);
			cursorType.GetField("LifecycleRevision", BindingFlags.Instance | BindingFlags.NonPublic)
				?.SetValue(cursor, seed.Lifecycle);
			cursorType.GetField("StateRevision", BindingFlags.Instance | BindingFlags.NonPublic)
				?.SetValue(cursor, seed.State);
			FieldInfo field = typeof(PrioritizeStatePacket).GetField(
				dictionaryName, BindingFlags.Static | BindingFlags.NonPublic);
			((IDictionary)field?.GetValue(null))[seed.NetId] = cursor;
		}

		private static T Roundtrip<T>(T input) where T : IPacket, new()
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				input.Serialize(writer);
			stream.Position = 0;
			var output = new T();
			using var reader = new BinaryReader(stream);
			output.Deserialize(reader);
			return output;
		}

		private static bool SerializeRejects(IPacket packet)
		{
			try
			{
				using var stream = new MemoryStream();
				using var writer = new BinaryWriter(stream);
				packet.Serialize(writer);
				return false;
			}
			catch (InvalidDataException) { return true; }
		}

		private static MethodInfo Method(Type type, string name)
			=> type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
			                              | BindingFlags.Static | BindingFlags.Instance);

		private static bool Calls(MethodInfo method, Type targetType, string targetName)
			=> FindCall(method, targetType, targetName) >= 0;

		private static int FindCall(MethodInfo method, Type targetType, string targetName)
		{
			byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
			if (il == null) return -1;
			for (int index = 0; index <= il.Length - sizeof(int); index++)
			{
				MethodBase called = null;
				try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, index)); }
				catch (ArgumentException) { }
				if (called?.DeclaringType == targetType && called.Name == targetName) return index;
			}
			return -1;
		}

		private static bool ReadsField(MethodInfo method, string fieldName)
		{
			byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
			if (il == null) return false;
			for (int index = 0; index <= il.Length - sizeof(int); index++)
			{
				FieldInfo field = null;
				try { field = method.Module.ResolveField(BitConverter.ToInt32(il, index)); }
				catch (ArgumentException) { }
				if (field?.DeclaringType == typeof(PrioritizeTargetRequestPacket)
				    && field.Name == fieldName) return true;
			}
			return false;
		}
	}
}
#endif
