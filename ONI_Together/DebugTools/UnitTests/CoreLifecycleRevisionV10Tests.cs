#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
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
		[UnitTest(name: "v10 build completion carries one lifecycle identity", category: "Sync")]
		public static UnitTestResult BuildCompletionWireRoundtrip()
		{
			var input = new BuildCompletePacket
			{
				Cell = 123,
				PrefabID = "WireBridge",
				Orientation = Orientation.R90,
				MaterialTags = new List<string> { "Copper", "Ceramic" },
				Temperature = 315f,
				FacadeID = "DEFAULT_FACADE",
				UtilityConnectionFlags = (UtilityConnections)5,
				ObjectLayer = global::ObjectLayer.Wire
			};
			if (!Set(input, "NetId", 7101) || !Set(input, "LifecycleRevision", 29UL))
				return UnitTestResult.Fail("BuildCompletePacket has no NetId + LifecycleRevision wire identity");
			BuildCompletePacket output = Roundtrip(input, new BuildCompletePacket());
			if (7101 != Get<int>(output, "NetId") || 29UL != Get<ulong>(output, "LifecycleRevision")
			    || 123 != output.Cell || 2 != output.MaterialTags.Count
			    || "Copper" != output.MaterialTags[0] || "Ceramic" != output.MaterialTags[1]
			    || (UtilityConnections)5 != output.UtilityConnectionFlags
			    || global::ObjectLayer.Wire != output.ObjectLayer)
				return UnitTestResult.Fail("Build completion lost identity, materials, layer, or utility topology");
			return UnitTestResult.Pass("Build completion wire carries exact lifecycle and native material inputs");
		}

		[UnitTest(name: "v10 build completion rejects zero lifecycle identity", category: "Sync")]
		public static UnitTestResult BuildCompletionRejectsZeroIdentity()
		{
			var packet = new BuildCompletePacket
			{
				Cell = 1, PrefabID = "Wire", MaterialTags = new List<string> { "Copper" },
				FacadeID = "DEFAULT_FACADE"
			};
			if (!Set(packet, "NetId", 0) || !Set(packet, "LifecycleRevision", 0UL))
				return UnitTestResult.Fail("Build completion lifecycle identity is absent");
			return RejectsSerialization(packet)
				? UnitTestResult.Pass("Zero build lifecycle identity is rejected before dispatch")
				: UnitTestResult.Fail("Zero build lifecycle identity entered the wire");
		}

		[UnitTest(name: "v10 build completion lifecycle gate is idempotent", category: "Sync")]
		public static UnitTestResult BuildCompletionLifecycleGate()
		{
			if (BuildCompletePacket.ShouldApplyLifecycle(10, 0, false)
			    || BuildCompletePacket.ShouldApplyLifecycle(10, 9, false)
			    || BuildCompletePacket.ShouldApplyLifecycle(10, 10, true)
			    || !BuildCompletePacket.ShouldApplyLifecycle(10, 10, false)
			    || !BuildCompletePacket.ShouldApplyLifecycle(10, 11, true))
				return UnitTestResult.Fail("Build completion rejected its live transition or admitted stale/tombstoned state");
			return UnitTestResult.Pass("Same live lifecycle can transition Constructable; tombstones require a higher revision");
		}

		[UnitTest(name: "v10 deconstruct completion carries a tombstone", category: "Sync")]
		public static UnitTestResult DeconstructTombstoneWireRoundtrip()
		{
			var input = new DeconstructCompletePacket { NetId = 7201, Revision = 31 };
			DeconstructCompletePacket output = Roundtrip(input, new DeconstructCompletePacket());
			if (7201 != output.NetId || 31UL != output.Revision)
				return UnitTestResult.Fail("Deconstruct tombstone did not roundtrip exactly");
			return UnitTestResult.Pass("Deconstruct completion carries its exact lifecycle tombstone");
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
			if (!RejectsSerialization(zero))
				return UnitTestResult.Fail("Zero deconstruct tombstone entered the wire");
			return UnitTestResult.Pass("Deconstruct rejects zero/stale/duplicate tombstones before target lookup");
		}

		[UnitTest(name: "v10 lifecycle journal rejects zero stale duplicate and tombstone replay", category: "Sync")]
		public static UnitTestResult LifecycleJournalOrdering()
		{
			const int netId = -1_700_101;
			var previous = NetworkIdentityRegistry.GetLifecycleRevisionSnapshot();
			ulong authority = NetworkIdentityRegistry.AuthorityRevisionForTests;
			try
			{
				var baseline = new[]
				{
					new NetworkIdentityRegistry.LifecycleRevisionSnapshotEntry(netId, 50, true)
				};
				if (!NetworkIdentityRegistry.TryReplaceLifecycleRevisionBaseline(baseline))
					return UnitTestResult.Fail("Lifecycle test baseline was rejected");
				bool zero = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 0, false);
				bool old = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 49, false);
				bool duplicate = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 50, false);
				bool newer = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 51, false);
				if (zero || old || duplicate || !newer || NetworkIdentityRegistry.IsLifecycleTombstoned(netId))
					return UnitTestResult.Fail("Lifecycle journal admitted zero/stale/duplicate state or rejected replacement");
				return UnitTestResult.Pass("Only a higher lifecycle revision can replace a tombstone");
			}
			finally
			{
				NetworkIdentityRegistry.RestoreLifecycleRevisionStateForTests(previous, authority);
			}
		}

		[UnitTest(name: "v10 reconnect rejects old build deconstruct ground and world state", category: "Sync")]
		public static UnitTestResult ReconnectCutsRejectOldLifecycleState()
		{
			const ulong lifecycleBaseline = 80;
			const long worldBaseline = 80;
			bool oldBuild = BuildCompletePacket.ShouldApplyLifecycle(lifecycleBaseline, 79, false);
			bool oldDeconstruct = DeconstructCompletePacket.ShouldApply(true, lifecycleBaseline, 79);
			bool oldGround = NetworkIdentityRegistry.IsNewerRevision(lifecycleBaseline, 79);
			bool oldWorld = WorldUpdatePacket.ShouldApply(
				localIsHost: false, senderIsHost: true,
				revision: 79, supersededRevision: worldBaseline);
			bool newBuild = BuildCompletePacket.ShouldApplyLifecycle(lifecycleBaseline, 81, true);
			bool newDeconstruct = DeconstructCompletePacket.ShouldApply(true, lifecycleBaseline, 81);
			bool newGround = NetworkIdentityRegistry.IsNewerRevision(lifecycleBaseline, 81);
			bool newWorld = WorldUpdatePacket.ShouldApply(false, true, 81, worldBaseline);
			if (oldBuild || oldDeconstruct || oldGround || oldWorld
			    || !newBuild || !newDeconstruct || !newGround || !newWorld)
				return UnitTestResult.Fail("Reconnect baseline admitted old state or rejected a newer revision");
			return UnitTestResult.Pass("Reconnect cuts prevent old lifecycle and foreground rollback");
		}

		[UnitTest(name: "v10 build and deconstruct dispatch by NetId journal", category: "Sync")]
		public static UnitTestResult CompletionPacketsUseIdentityJournal()
		{
			MethodInfo build = Method(typeof(BuildCompletePacket), nameof(BuildCompletePacket.OnDispatched));
			MethodInfo deconstruct = Method(typeof(DeconstructCompletePacket), nameof(DeconstructCompletePacket.OnDispatched));
			bool buildJournal = CallsReachable(build, typeof(NetworkIdentityRegistry),
				nameof(NetworkIdentityRegistry.TryBindAuthoritativeLifecycle));
			bool buildLookup = CallsReachable(build, typeof(NetworkIdentityRegistry),
				nameof(NetworkIdentityRegistry.TryGet));
			bool deconstructJournal = CallsReachable(deconstruct, typeof(NetworkIdentityRegistry),
				nameof(NetworkIdentityRegistry.TryAcceptLifecycleRevision));
			bool deconstructLookup = CallsReachable(deconstruct, typeof(NetworkIdentityRegistry),
				nameof(NetworkIdentityRegistry.TryGetComponent));
			if (!buildJournal || !buildLookup || !deconstructJournal || !deconstructLookup)
				return UnitTestResult.Fail("Completion packet can mutate by cell without exact NetId/revision admission");
			return UnitTestResult.Pass("Old cell events cannot mutate a replacement with a different NetId");
		}

		[UnitTest(name: "v10 build uses one native completion event", category: "Sync")]
		public static UnitTestResult BuildPublishesOnceAndUsesNativeBuild()
		{
			MethodInfo postfix = Method(typeof(global::ConstructablePatch), "Postfix");
			MethodInfo dispatch = Method(typeof(BuildCompletePacket), nameof(BuildCompletePacket.OnDispatched));
			int sends = CountDirectCalls(postfix, typeof(PacketSender), nameof(PacketSender.SendToAllClients));
			if (1 != sends)
				return UnitTestResult.Fail($"Host completion published {sends} terminal events instead of one");
			if (!CallsReachable(dispatch, typeof(BuildingDef), nameof(BuildingDef.Build))
			    || CallsReachable(dispatch, typeof(Util), nameof(Util.KInstantiate)))
				return UnitTestResult.Fail("Build completion bypassed native Build materialization");
			return UnitTestResult.Pass("Tiles, bridges, utilities and multi-cell buildings use one native completion event");
		}

		[UnitTest(name: "v10 managed construction preserves lifecycle through cleanup", category: "Sync")]
		public static UnitTestResult ManagedConstructionPreservesIdentity()
		{
			MethodInfo prefix = Method(typeof(global::ConstructablePatch), "Prefix");
			MethodInfo postfix = Method(typeof(global::ConstructablePatch), "Postfix");
			MethodInfo finalizer = Method(typeof(global::ConstructablePatch), "Finalizer");
			MethodInfo finalize = Method(typeof(global::ConstructablePatch), "TryFinalizeIdentity");
			MethodInfo cleanup = Method(typeof(NetworkIdentity), nameof(NetworkIdentity.OnCleanUp));
			if (!CallsReachable(prefix, typeof(NetworkIdentity), "BeginManagedSpawn")
			    || !CallsReachable(postfix, typeof(NetworkIdentity), "EndManagedSpawn")
			    || !CallsReachable(finalizer, typeof(NetworkIdentity), "EndManagedSpawn"))
				return UnitTestResult.Fail("Construction transition does not balance managed-spawn suppression");
			if (!CallsReachable(cleanup, typeof(NetworkIdentity), "get_IsManagedSpawnSuppressed"))
				return UnitTestResult.Fail("Old Constructable cleanup can publish Despawn or end its lifecycle");
			if (!CallsReachable(finalize, typeof(NetworkIdentity), nameof(NetworkIdentity.RegisterIdentity)))
				return UnitTestResult.Fail("Completed building does not reclaim the managed construction identity");
			return UnitTestResult.Pass("Constructable cleanup is silent and completed building retains its NetId/revision");
		}

		[UnitTest(name: "v10 identity binding drains pending build completion", category: "Sync")]
		public static UnitTestResult IdentityBindingDrainsPendingCompletion()
		{
			MethodInfo bind = Method(
				typeof(NetworkIdentityRegistry),
				nameof(NetworkIdentityRegistry.TryBindAuthoritativeLifecycle));
			MethodInfo apply = Method(typeof(BuildCompletePacket), "TryApplyPending");
			if (!CallsReachable(bind, typeof(BuildCompletePacket), "TryApplyPending"))
				return UnitTestResult.Fail("Constructable identity binding does not drain pending completion");
			if (!CallsReachable(apply, typeof(BuildCompletePacket), "TryTakePending")
			    || !CallsReachable(apply, typeof(BuildCompletePacket), "TryApplyCompletion"))
				return UnitTestResult.Fail("Pending completion is not taken before re-entering build dispatch");
			return UnitTestResult.Pass("Matching NetId/revision binding takes pending completion before apply");
		}

		[UnitTest(name: "v10 deconstruct is scoped; general cleanup stays Despawn", category: "Sync")]
		public static UnitTestResult DeconstructAndGeneralCleanupAreDistinct()
		{
			MethodInfo cleanup = Method(typeof(NetworkIdentity), "SendAuthoritativeCleanup");
			if (!CallsReachable(cleanup, typeof(PacketSender), nameof(PacketSender.SendToAllClients))
			    || !ReferencesTypeReachable(cleanup, typeof(DeconstructCompletePacket)))
				return UnitTestResult.Fail("Deconstruct completion is not published by the deconstruct path");
			if (!ReferencesTypeReachable(cleanup, typeof(DespawnEntityPacket)))
				return UnitTestResult.Fail("General NetworkIdentity cleanup no longer emits DespawnEntityPacket");
			return UnitTestResult.Pass("Deconstruct has a scoped tombstone while general cleanup remains Despawn");
		}

		[UnitTest(name: "v10 removes DigComplete and PickupItem terminal packets", category: "Sync")]
		public static UnitTestResult LegacyTerminalPacketTypesAreDeleted()
		{
			Assembly assembly = typeof(PacketRegistry).Assembly;
			Type digComplete = assembly.GetType(
				"ONI_Together.Networking.Packets.Tools.Dig.DigCompletePacket", throwOnError: false);
			Type pickupItem = assembly.GetType(
				"ONI_Together.Networking.Packets.World.PickupItemPacket", throwOnError: false);
			if (digComplete != null || pickupItem != null)
				return UnitTestResult.Fail("DigCompletePacket or PickupItemPacket still exists");
			return UnitTestResult.Pass("Legacy dig/pickup terminal packet types are absent from PacketRegistry assembly");
		}

		[UnitTest(name: "v10 dig uses foreground world causality and TakeUnit is non-terminal", category: "Sync")]
		public static UnitTestResult DigAndTakeUnitDoNotPublishTerminalState()
		{
			bool oldRejected = !WorldUpdatePacket.ShouldApply(
				localIsHost: false, senderIsHost: true, revision: 40, supersededRevision: 40);
			bool newAccepted = WorldUpdatePacket.ShouldApply(
				localIsHost: false, senderIsHost: true, revision: 41, supersededRevision: 40);
			Type takeUnit = typeof(PacketRegistry).Assembly.GetType(
				"ONI_Together.Patches.World.PickupablePatches+PickupableTakeUnitPatch", false);
			MethodInfo postfix = Method(takeUnit, "Postfix");
			if (!oldRejected || !newAccepted)
				return UnitTestResult.Fail("Dig foreground mutations are not protected by the WorldUpdate cut");
			if (postfix != null && CallsReachable(
				    postfix, typeof(PacketSender), nameof(PacketSender.SendToAllClients)))
				return UnitTestResult.Fail("Pickupable.TakeUnit still publishes terminal network state");
			if (new DiggablePacket() is not IClientRelayable)
				return UnitTestResult.Fail("Dig tool intent is no longer a client relay request");
			return UnitTestResult.Pass("Dig completion is foreground WorldUpdate state; TakeUnit is local quantity change");
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

		private static bool Set<T>(object target, string name, T value)
		{
			FieldInfo field = target.GetType().GetField(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field == null || field.FieldType != typeof(T)) return false;
			field.SetValue(target, value);
			return true;
		}

		private static T Get<T>(object target, string name)
		{
			FieldInfo field = target.GetType().GetField(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			return field != null && field.FieldType == typeof(T)
				? (T)field.GetValue(target)
				: default;
		}

		private static MethodInfo Method(Type type, string name)
			=> type?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
			                         BindingFlags.Static | BindingFlags.Instance);

		private static bool CallsReachable(MethodInfo root, Type declaringType, string name)
			=> CallsReachable(root, declaringType, name, new HashSet<MethodInfo>(), 3);

		private static bool CallsReachable(
			MethodInfo method, Type declaringType, string name,
			ISet<MethodInfo> visited, int depth)
		{
			if (method == null || depth < 0 || !visited.Add(method)) return false;
			foreach (MethodBase called in CalledMethods(method))
			{
				if (called.DeclaringType == declaringType && called.Name == name) return true;
				if (called is MethodInfo child && called.DeclaringType == method.DeclaringType
				    && CallsReachable(child, declaringType, name, visited, depth - 1)) return true;
			}
			return false;
		}

		private static int CountDirectCalls(MethodInfo method, Type declaringType, string name)
		{
			int count = 0;
			foreach (MethodBase called in CalledMethods(method))
				if (called.DeclaringType == declaringType && called.Name == name) count++;
			return count;
		}

		private static bool ReferencesTypeReachable(MethodInfo method, Type type)
		{
			return ReferencesTypeReachable(method, type, new HashSet<MethodInfo>(), 3);
		}

		private static bool ReferencesTypeReachable(
			MethodInfo method, Type type, ISet<MethodInfo> visited, int depth)
		{
			if (method == null || depth < 0 || !visited.Add(method)) return false;
			foreach (MethodBase called in CalledMethods(method))
			{
				if (called.DeclaringType == type) return true;
				if (called is MethodInfo child && called.DeclaringType == method.DeclaringType
				    && ReferencesTypeReachable(child, type, visited, depth - 1)) return true;
			}
			return false;
		}

		private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
		{
			byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
			if (il == null) yield break;
			for (int index = 0; index <= il.Length - sizeof(int); index++)
			{
				MethodBase called = null;
				try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, index)); }
				catch (ArgumentException) { }
				if (called != null) yield return called;
			}
		}
	}
}
#endif
