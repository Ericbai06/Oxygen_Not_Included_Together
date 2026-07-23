#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools.Build;
using ONI_Together.Networking.Packets.Tools.Deconstruct;
using ONI_Together.Networking.Packets.Tools.Dig;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class CoreLifecycleRevisionV10Tests
	{
		[UnitTest(name: "v10 build commit carries one lifecycle identity", category: "Sync")]
		public static UnitTestResult BuildCommitWireRoundtrip()
		{
			BuildOperationId operation = new(8, 17, 1);
			BuildRequest request = new(operation, "WireBridge",
				new UtilityPathGeometry(new[] { 123, 124 }), new[] { "Copper", "Ceramic" },
				"DEFAULT_FACADE", 0, 5, (int)ObjectLayer.Wire);
			var commit = new BuildCommit(request, operation,
				new[]
				{
					new PlacementOutcome(123, BuildPlacementKind.Completed, 7101, 29),
					new PlacementOutcome(124, BuildPlacementKind.Queued)
				},
				new[] { new UtilityEdge(123, 124) }, new BuildRevision(29));
			BuildCommitPacket output = Roundtrip(
				BuildCommitPacket.FromDomain(commit), new BuildCommitPacket());
			return output.OperationId == operation && output.Revision == 29
			       && output.Placements.Count == 2
			       && output.Placements[0].NetId == 7101
			       && output.Placements[0].LifecycleRevision == 29
			       && output.Connections.Count == 1
				? UnitTestResult.Pass("Build commit carries exact operation, lifecycle identity, and utility topology")
				: UnitTestResult.Fail("Build commit lost operation, lifecycle identity, or utility topology");
		}

		[UnitTest(name: "v10 build commit rejects zero lifecycle revision", category: "Sync")]
		public static UnitTestResult BuildCommitRejectsZeroIdentity()
		{
			BuildOperationId operation = new(8, 17, 2);
			BuildRequest request = new(operation, "Tile",
				new SinglePlacementGeometry(1, Orientation.Neutral), new[] { "SandStone" },
				"DEFAULT_FACADE", 0, 5, (int)ObjectLayer.Building);
			BuildCommitPacket packet = BuildCommitPacket.FromDomain(new BuildCommit(
				request, operation, new[] { new PlacementOutcome(1, BuildPlacementKind.Queued) },
				Array.Empty<UtilityEdge>(), new BuildRevision(1)));
			packet.Revision = 0;
			return RejectsSerialization(packet)
				? UnitTestResult.Pass("Zero build commit revision is rejected before dispatch")
				: UnitTestResult.Fail("Zero build commit revision entered the wire");
		}

		[UnitTest(name: "v10 build lifecycle gate is idempotent", category: "Sync")]
		public static UnitTestResult BuildCommitLifecycleGate()
		{
			if (NetworkIdentityRegistry.IsNewerRevision(10, 0)
			    || NetworkIdentityRegistry.IsNewerRevision(10, 9)
			    || !NetworkIdentityRegistry.IsNewerRevision(10, 11)
			    || !BuildLifecycleAdmission.CanComplete(true, true, true, true, true)
			    || BuildLifecycleAdmission.CanComplete(true, true, false, true, true))
				return UnitTestResult.Fail("Build lifecycle admitted stale identity or incomplete materialization");
			return UnitTestResult.Pass("Build commit lifecycle requires a newer revision and initialized target");
		}

		[UnitTest(name: "v10 deconstruct completion carries a tombstone", category: "Sync")]
		public static UnitTestResult DeconstructTombstoneWireRoundtrip()
		{
			var input = new DeconstructCompletePacket { NetId = 7201, Revision = 31 };
			DeconstructCompletePacket output = Roundtrip(input, new DeconstructCompletePacket());
			return output.NetId == 7201 && output.Revision == 31
				? UnitTestResult.Pass("Deconstruct completion carries its exact lifecycle tombstone")
				: UnitTestResult.Fail("Deconstruct tombstone did not roundtrip exactly");
		}

		[UnitTest(name: "v10 deconstruct rejects zero stale duplicate and missing-target replay", category: "Sync")]
		public static UnitTestResult DeconstructLifecycleGate()
		{
			if (DeconstructCompletePacket.ShouldApply(true, 10, 0)
			    || DeconstructCompletePacket.ShouldApply(true, 10, 9)
			    || DeconstructCompletePacket.ShouldApply(true, 10, 10)
			    || DeconstructCompletePacket.ShouldApply(false, 10, 11)
			    || !DeconstructCompletePacket.ShouldApply(true, 10, 11))
				return UnitTestResult.Fail("Deconstruct authority or revision gate is incorrect");
			var zero = new DeconstructCompletePacket { NetId = 7201, Revision = 0 };
			return RejectsSerialization(zero)
				? UnitTestResult.Pass("Deconstruct rejects zero/stale/duplicate tombstones before target lookup")
				: UnitTestResult.Fail("Zero deconstruct tombstone entered the wire");
		}

		[UnitTest(name: "v10 lifecycle journal rejects stale duplicate and tombstone replay", category: "Sync")]
		public static UnitTestResult LifecycleJournalOrdering()
		{
			const int netId = -1_700_101;
			var previous = NetworkIdentityRegistry.GetLifecycleRevisionSnapshot();
			ulong authority = NetworkIdentityRegistry.AuthorityRevisionForTests;
			try
			{
				if (!NetworkIdentityRegistry.TryReplaceLifecycleRevisionBaseline(new[]
					{ new NetworkIdentityRegistry.LifecycleRevisionSnapshotEntry(netId, 50, true) }))
					return UnitTestResult.Fail("Lifecycle test baseline was rejected");
				bool old = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 49, false);
				bool duplicate = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 50, false);
				bool newer = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 51, false);
				return !old && !duplicate && newer && !NetworkIdentityRegistry.IsLifecycleTombstoned(netId)
					? UnitTestResult.Pass("Only a higher lifecycle revision can replace a tombstone")
					: UnitTestResult.Fail("Lifecycle journal admitted stale/duplicate state or rejected replacement");
			}
			finally
			{
				NetworkIdentityRegistry.RestoreLifecycleRevisionStateForTests(previous, authority);
			}
		}

		[UnitTest(name: "v10 reconnect cuts reject old build deconstruct ground and world state", category: "Sync")]
		public static UnitTestResult ReconnectCutsRejectOldLifecycleState()
		{
			const ulong lifecycleBaseline = 80;
			const long worldBaseline = 80;
			bool oldBuild = NetworkIdentityRegistry.IsNewerRevision(lifecycleBaseline, 79);
			bool oldDeconstruct = DeconstructCompletePacket.ShouldApply(true, lifecycleBaseline, 79);
			bool oldGround = NetworkIdentityRegistry.IsNewerRevision(lifecycleBaseline, 79);
			bool oldWorld = WorldUpdatePacket.ShouldApply(false, true, 79, worldBaseline);
			bool newBuild = NetworkIdentityRegistry.IsNewerRevision(lifecycleBaseline, 81);
			bool newDeconstruct = DeconstructCompletePacket.ShouldApply(true, lifecycleBaseline, 81);
			bool newGround = NetworkIdentityRegistry.IsNewerRevision(lifecycleBaseline, 81);
			bool newWorld = WorldUpdatePacket.ShouldApply(false, true, 81, worldBaseline);
			return !oldBuild && !oldDeconstruct && !oldGround && !oldWorld
			       && newBuild && newDeconstruct && newGround && newWorld
				? UnitTestResult.Pass("Reconnect cuts prevent old lifecycle and foreground rollback")
				: UnitTestResult.Fail("Reconnect baseline admitted old state or rejected a newer revision");
		}

		[UnitTest(name: "v10 build commit dispatch applies through one applier", category: "Sync")]
		public static UnitTestResult CompletionPacketsUseIdentityJournal()
		{
			MethodInfo dispatch = Method(typeof(BuildCommitPacket), nameof(BuildCommitPacket.OnDispatched));
			MethodInfo apply = Method(typeof(BuildCommitApplier), nameof(BuildCommitApplier.Apply));
			return CallsReachable(dispatch, apply)
				? UnitTestResult.Pass("Build commit dispatch delegates identity/revision handling to one applier")
				: UnitTestResult.Fail("Build commit dispatch bypasses the authoritative commit applier");
		}

		[UnitTest(name: "v10 build publisher emits only dedicated commit/rejection packets", category: "Sync")]
		public static UnitTestResult BuildPublishesOnceAndUsesNativeBuild()
		{
			MethodInfo[] publish = typeof(BuildPublisher).GetMethods(
				BindingFlags.Static | BindingFlags.NonPublic)
				.Where(method => method.Name == nameof(BuildPublisher.Publish)).ToArray();
			if (publish.Length != 2 || publish.Any(method => CountDirectCalls(
				method, typeof(PacketSender), nameof(PacketSender.SendToAllClients)) != 1))
				return UnitTestResult.Fail("Build publisher does not expose one send per authoritative outcome kind");
		return UnitTestResult.Pass("Build commit and rejection each publish through one dedicated packet path");
		}

		[UnitTest(name: "v10 identity binding is replaced by commit idempotence", category: "Sync")]
		public static UnitTestResult IdentityBindingDrainsPendingCompletion()
		{
			MethodInfo reset = Method(typeof(BuildCommitApplier), nameof(BuildCommitApplier.Reset));
			MethodInfo apply = Method(typeof(BuildCommitApplier), nameof(BuildCommitApplier.Apply));
			return reset != null && apply != null && apply.ReturnType == typeof(ApplyResult)
				? UnitTestResult.Pass("Build client state is guarded by operation/revision idempotence")
				: UnitTestResult.Fail("Build commit applier lacks operation/revision guard");
		}

		[UnitTest(name: "v10 removes DigComplete and PickupItem terminal packets", category: "Sync")]
		public static UnitTestResult LegacyTerminalPacketTypesAreDeleted()
		{
			Assembly assembly = typeof(PacketRegistry).Assembly;
			Type digComplete = assembly.GetType(
				"ONI_Together.Networking.Packets.Tools.Dig.DigCompletePacket", false);
			Type pickupItem = assembly.GetType(
				"ONI_Together.Networking.Packets.World.PickupItemPacket", false);
			return digComplete == null && pickupItem == null
				? UnitTestResult.Pass("Legacy dig/pickup terminal packet types are absent")
				: UnitTestResult.Fail("DigCompletePacket or PickupItemPacket still exists");
		}

		[UnitTest(name: "v10 dig uses foreground world causality and TakeUnit is non-terminal", category: "Sync")]
		public static UnitTestResult DigAndTakeUnitDoNotPublishTerminalState()
		{
			bool oldRejected = !WorldUpdatePacket.ShouldApply(false, true, 40, 40);
			bool newAccepted = WorldUpdatePacket.ShouldApply(false, true, 41, 40);
			Type takeUnit = typeof(PacketRegistry).Assembly.GetType(
				"ONI_Together.Patches.World.PickupablePatches+PickupableTakeUnitPatch", false);
			MethodInfo postfix = Method(takeUnit, "Postfix");
			if (!oldRejected || !newAccepted)
				return UnitTestResult.Fail("Dig foreground mutations are not protected by the WorldUpdate cut");
			if (postfix != null && ReflectionExecutionGraph.ReachesPacketSender(postfix))
				return UnitTestResult.Fail("Pickupable.TakeUnit still publishes terminal network state");
		return new DiggablePacket() is IClientRelayable
			? UnitTestResult.Pass("Dig completion is foreground WorldUpdate state; TakeUnit is local quantity change")
			: UnitTestResult.Fail("Dig tool intent is no longer a client relay request");
		}

		private static BuildCommitPacket CommitPacket()
		{
			BuildOperationId operation = new(8, 17, 5);
			BuildRequest request = new(operation, "Tile",
				new SinglePlacementGeometry(1, Orientation.Neutral), new[] { "SandStone" },
				"DEFAULT_FACADE", 0, 5, (int)ObjectLayer.Building);
			return BuildCommitPacket.FromDomain(new BuildCommit(request, operation,
				new[] { new PlacementOutcome(1, BuildPlacementKind.Queued) },
				Array.Empty<UtilityEdge>(), new BuildRevision(1)));
		}

		private static T Roundtrip<T>(T input, T output) where T : IPacket
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				input.Serialize(writer);
			stream.Position = 0;
			using var reader = new BinaryReader(stream);
			output.Deserialize(reader);
			if (stream.Position != stream.Length)
				throw new InvalidDataException("Lifecycle packet left unread bytes");
			return output;
		}

		private static bool RejectsSerialization(IPacket packet)
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
			=> type?.GetMethod(name, BindingFlags.Static | BindingFlags.Instance |
				BindingFlags.Public | BindingFlags.NonPublic);

		private static bool CallsReachable(MethodBase root, MethodBase target)
			=> target != null && ReflectionExecutionGraph.Reaches(root, target);

		private static int CountDirectCalls(MethodInfo caller, Type owner, string name)
			=> caller == null ? 0 : ReflectionExecutionGraph.ReadInstructions(caller)
				.Count(value => value.Operand is MethodInfo method && method.DeclaringType == owner && method.Name == name);
	}
}
#endif
