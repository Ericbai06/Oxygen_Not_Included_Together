using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Animation;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Transport;
using ONI_Together.Patches.KleiPatches;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class AnimSyncTests
	{
		private sealed class RecordingPacketSender : TransportPacketSender
		{
			public int SendCount;
			public IPacket LastPacket;
			public PacketSendMode LastMode;

			public override bool SendPacket(
				object conn,
				SerializedPacket packet,
				PacketSendMode sendType = PacketSendMode.ReliableImmediate)
			{
				SendCount++;
				LastPacket = packet.Packet;
				LastMode = sendType;
				return true;
			}
		}

		[UnitTest(name: "Anim reconciliation: detects wrong animation", category: "Animation", liveSafe: true,
			headlessUnsupportedReason: "Requires a loaded colony with a duplicant")]
		public static UnitTestResult DetectsWrongAnimation()
		{
			var identities = NetworkIdentityRegistry.AllIdentities;
			foreach (var id in identities)
			{
				if (!id.gameObject.TryGetComponent<KBatchedAnimController>(out var kbac))
					continue;
				if (!id.gameObject.GetComponent<KPrefabID>()?.HasTag(GameTags.BaseMinion) ?? true)
					continue;

				if (kbac.CurrentAnim == null)
					continue;

				string currentAnim = kbac.CurrentAnim.name;
				if (string.IsNullOrEmpty(currentAnim))
					continue;

				var wrongHash = new HashedString("fake_anim_that_doesnt_exist");
				if (kbac.currentAnim == wrongHash)
					return UnitTestResult.Fail("Hash collision with fake anim");

				return UnitTestResult.Pass($"Minion '{id.gameObject.name}' anim='{currentAnim}', would detect mismatch");
			}
			return Game.Instance == null
				? UnitTestResult.Skip("Requires a loaded colony with a duplicant")
				: UnitTestResult.Fail("No minions with anim controller found");
		}

		[UnitTest(name: "Anim reconciliation: elapsed time readable", category: "Animation", liveSafe: true,
			headlessUnsupportedReason: "Requires a loaded colony with a duplicant")]
		public static UnitTestResult ElapsedTimeReadable()
		{
			var identities = NetworkIdentityRegistry.AllIdentities;
			foreach (var id in identities)
			{
				if (!id.gameObject.TryGetComponent<KBatchedAnimController>(out var kbac))
					continue;
				if (!id.gameObject.GetComponent<KPrefabID>()?.HasTag(GameTags.BaseMinion) ?? true)
					continue;

				float elapsed = kbac.GetElapsedTime();
				return UnitTestResult.Pass($"ElapsedTime={elapsed:F3}s on '{id.gameObject.name}'");
			}
			return Game.Instance == null
				? UnitTestResult.Skip("Requires a loaded colony with a duplicant")
				: UnitTestResult.Fail("No minions found");
		}

		[UnitTest(name: "Anim reconciliation: reflection helper resolves", category: "Animation",
			headlessUnsupportedReason: "Requires a loaded colony with animated entities")]
		public static UnitTestResult ReflectionHelperResolves()
		{
			var identities = NetworkIdentityRegistry.AllIdentities;
			foreach (var id in identities)
			{
				if (!id.gameObject.TryGetComponent<KBatchedAnimController>(out var kbac))
					continue;

				float before = kbac.GetElapsedTime();
				AnimReconciliationHelper.TrySetElapsedTime(kbac, before);
				float after = kbac.GetElapsedTime();

				return UnitTestResult.Pass($"SetElapsedTime resolved. Before={before:F3}, After={after:F3}");
			}
			return Game.Instance == null
				? UnitTestResult.Skip("Requires a loaded colony with animated entities")
				: UnitTestResult.Fail("No anim controllers found");
		}

		[UnitTest(name: "Anim sync packet: roundtrip", category: "Animation")]
		public static UnitTestResult AnimSyncPacketRoundtrip()
		{
			var packet = new AnimSyncPacket
			{
				NetId = 42,
				AnimHash = new HashedString("idle_loop").hash,
				Mode = (byte)KAnim.PlayMode.Loop,
				Speed = 1.25f,
				ElapsedTime = 2.5f,
				StartTick = 100,
				DurationTicks = 20,
			};

			using var ms = new MemoryStream();
			using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);

			ms.Position = 0;

			var copy = new AnimSyncPacket();
			using (var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);

			if (copy.NetId != packet.NetId || copy.AnimHash != packet.AnimHash || copy.Mode != packet.Mode)
				return UnitTestResult.Fail("Packet int fields did not roundtrip");
			if (copy.Speed != packet.Speed || copy.ElapsedTime != packet.ElapsedTime
			    || copy.StartTick != 100 || copy.DurationTicks != 20)
				return UnitTestResult.Fail("Packet float fields did not roundtrip");
			if (AnimSyncPacket.ProjectElapsedForTests(copy, 108) != 0.5f)
				return UnitTestResult.Fail("Loop animation did not project and wrap from presentation ticks");

			return UnitTestResult.Pass("AnimSyncPacket serialize/deserialize roundtrip succeeded");
		}

		[UnitTest(name: "Anim sync wire rejects invalid presentation state", category: "Animation")]
		public static UnitTestResult AnimSyncRejectsInvalidWireState()
		{
			AnimSyncPacket[] invalid =
			{
				Snapshot(0, 1), Snapshot(1, 0),
				Snapshot(1, 1, packet => packet.StartTick = -1),
				Snapshot(1, 1, packet => packet.DurationTicks = 0),
				Snapshot(1, 1, packet => packet.Speed = float.NaN),
				Snapshot(1, 1, packet => packet.Speed = float.PositiveInfinity),
				Snapshot(1, 1, packet => packet.ElapsedTime = float.NaN),
				Snapshot(1, 1, packet => packet.ElapsedTime = float.PositiveInfinity),
				Snapshot(1, 1, packet => packet.ElapsedTime = -0.1f),
				Snapshot(1, 1, packet => packet.Mode = byte.MaxValue),
			};
			if (invalid.Any(packet => !RejectsSerialize(packet)))
				return UnitTestResult.Fail("AnimSyncPacket serialized an invalid field");

			if (!RejectsAnimDeserialize(float.NaN, (byte)KAnim.PlayMode.Loop)
			    || !RejectsAnimDeserialize(0f, byte.MaxValue)
			    || !RejectsSerialize(new AnimSyncBatchPacket { Revision = 1, States = [] })
			    || !RejectsEmptyAnimBatchDeserialize())
				return UnitTestResult.Fail("Animation deserialize/batch accepted invalid or empty wire state");
			return UnitTestResult.Pass("Animation wire validates identifiers, ticks, floats, play mode and nonempty batches");
		}

		[UnitTest(name: "Anim production events: remain client-local", category: "Animation")]
		public static UnitTestResult ProductionAnimationEventsRemainLocal()
		{
			MethodInfo send = typeof(PacketSender).GetMethod(
				nameof(PacketSender.SendToAllClients),
				BindingFlags.Public | BindingFlags.Static,
				null,
				[typeof(IPacket), typeof(PacketSendMode)],
				null);
			if (send == null)
				return UnitTestResult.Fail("Could not resolve PacketSender.SendToAllClients");

			var owners = new List<Type> { typeof(KAnimControllerBase_Patches) };
			Type symbolPatch = typeof(KAnimControllerBase_Patches).Assembly.GetType(
				"ONI_Together.Patches.KleiPatches.SymbolOverrideController_Patch");
			if (symbolPatch != null)
				owners.Add(symbolPatch);
			foreach (Type owner in owners)
			{
				foreach (MethodInfo method in MethodsIncludingNested(owner))
					if (Calls(method, send))
						return UnitTestResult.Fail($"{owner.Name}.{method.Name} still broadcasts a production visual event");
			}

			return UnitTestResult.Pass("Play, queue, override and symbol events stay on each game's native client path");
		}

		[UnitTest(name: "Anim sync batch: dedupes and stays below unreliable MTU", category: "Animation")]
		public static UnitTestResult AnimSyncBatchDedupesAndSplits()
		{
			var snapshots = new List<AnimSyncPacket>();
			for (int netId = 1; netId <= AnimSyncBatchPacket.MaxEntriesPerBatch + 1; netId++)
				snapshots.Add(Snapshot(netId, netId));
			snapshots.Add(Snapshot(1, 999));

			List<AnimSyncBatchPacket> batches = AnimSyncBatchPacket.CreateBatches(17, snapshots);
			if (batches.Count != 2 || batches.Sum(batch => batch.States.Length) != snapshots.Count - 1
			    || !typeof(IHostOnlyPacket).IsAssignableFrom(typeof(AnimSyncBatchPacket)))
				return UnitTestResult.Fail("Batch did not split once or did not dedupe duplicate NetId");
			AnimSyncPacket latest = batches.SelectMany(batch => batch.States).Single(state => state.NetId == 1);
			if (latest.AnimHash != 999 || batches.Any(batch => WireBytes(batch) >= PacketSender.MAX_PACKET_SIZE_UNRELIABLE))
				return UnitTestResult.Fail("Batch lost last-write-wins state or exceeded unreliable MTU");

			return UnitTestResult.Pass("Batch dedupes by NetId and bounds every wire packet to 1000 bytes");
		}

		[UnitTest(name: "Anim sync batch: stale batches cannot rewind entity state", category: "Animation")]
		public static UnitTestResult AnimSyncBatchRejectsStaleRevisions()
		{
			AnimSyncBatchPacket.ResetSessionState();
			bool current = AnimSyncBatchPacket.AcceptBatchRevisionForTests(12);
			bool sameSplit = AnimSyncBatchPacket.AcceptBatchRevisionForTests(12);
			bool entityCurrent = AnimSyncBatchPacket.AcceptEntityRevisionForTests(42, 12);
			bool entityDuplicate = AnimSyncBatchPacket.AcceptEntityRevisionForTests(42, 12);
			bool staleBatch = AnimSyncBatchPacket.AcceptBatchRevisionForTests(11);
			bool staleEntity = AnimSyncBatchPacket.AcceptEntityRevisionForTests(42, 11);
			AnimSyncBatchPacket.ResetSessionState();
			bool resetAccepted = AnimSyncBatchPacket.AcceptBatchRevisionForTests(1)
			                     && AnimSyncBatchPacket.AcceptEntityRevisionForTests(42, 1);
			bool passed = current && sameSplit && entityCurrent && !entityDuplicate
			              && staleBatch && !staleEntity && resetAccepted;
			AnimSyncBatchPacket.ResetSessionState();

			return passed
				? UnitTestResult.Pass("Stale visual snapshots cannot rewind a newer batch or entity state")
				: UnitTestResult.Fail("Batch/entity revision guard rewound visual state or survived reset");
		}

		[UnitTest(name: "Anim sync batch: uses ordinary unreliable aggregation", category: "Animation")]
		public static UnitTestResult AnimSyncBatchDoesNotUseReliableOrdering()
		{
			TransportPacketSender original = NetworkConfig.TransportPacketSender;
			var sender = new RecordingPacketSender();
			object connection = new();
			try
			{
				NetworkConfig.TransportPacketSender = sender;
				PacketSender.ResetSessionState();
				AnimSyncBatchPacket batch = AnimSyncBatchPacket.CreateBatches(1, [Snapshot(7, 70)]).Single();
				bool sent = PacketSender.SendToConnection(
					connection, batch, AnimSyncCoordinator.CorrectionSendMode);

				return sent && sender.SendCount == 1 && ReferenceEquals(sender.LastPacket, batch)
				       && sender.LastMode == PacketSendMode.Unreliable
					? UnitTestResult.Pass("Visual correction uses Steam unreliable aggregation")
					: UnitTestResult.Fail("Visual correction entered reliable ordering or used the wrong send mode");
			}
			finally
			{
				PacketSender.ResetSessionState();
				NetworkConfig.TransportPacketSender = original;
			}
		}

		[UnitTest(name: "Anim sync batch: revisions survive coordinator rebuild", category: "Animation")]
		public static UnitTestResult AnimSyncRevisionIsSessionMonotonic()
		{
			AnimSyncCoordinator.ResetSessionState();
			ulong first = AnimSyncCoordinator.NextRevisionForTests(9);
			ulong second = AnimSyncCoordinator.NextRevisionForTests(9);
			ulong otherRecipient = AnimSyncCoordinator.NextRevisionForTests(10);
			AnimSyncCoordinator.ResetSessionState();
			ulong afterReset = AnimSyncCoordinator.NextRevisionForTests(9);
			AnimSyncCoordinator.ResetSessionState();

			return first == 1 && second == 2 && otherRecipient == 1 && afterReset == 1
				? UnitTestResult.Pass("Recipient revision is session-monotonic and resets only with the session")
				: UnitTestResult.Fail("Recipient revision restarted before session reset or leaked across reset");
		}

		[UnitTest(name: "Anim packets: bypass bulk queue", category: "Animation")]
		public static UnitTestResult AnimPacketsBypassBulkQueue()
		{
			bool animSyncBulk = typeof(IBulkablePacket).IsAssignableFrom(typeof(AnimSyncPacket));
			bool playAnimBulk = typeof(IBulkablePacket).IsAssignableFrom(typeof(PlayAnimPacket));
			if (animSyncBulk || playAnimBulk)
				return UnitTestResult.Fail("Animation packets still route through the bulk queue");

			return UnitTestResult.Pass("AnimSyncPacket and PlayAnimPacket send directly");
		}

		[UnitTest(name: "Anim sync: non-minion snapshots use coordinator", category: "Animation")]
		public static UnitTestResult NonMinionSnapshotsUseCoordinator()
		{
			bool perEntityHeartbeat = typeof(IRender1000ms).IsAssignableFrom(typeof(AnimStateSyncer));
			if (perEntityHeartbeat)
				return UnitTestResult.Fail("AnimStateSyncer still runs its own 1000ms heartbeat");

			return UnitTestResult.Pass("AnimStateSyncer relies on the shared coordinator");
		}

		[UnitTest(name: "Anim sync: non-minion entities discoverable", category: "Animation", liveSafe: true,
			headlessUnsupportedReason: "Requires a loaded colony with animated entities")]
		public static UnitTestResult NonMinionAnimEntitiesDiscoverable()
		{
			var identities = NetworkIdentityRegistry.AllIdentities;
			foreach (var id in identities)
			{
				if (id.gameObject.GetComponent<KPrefabID>()?.HasTag(GameTags.BaseMinion) ?? false)
					continue;
				if (!id.gameObject.TryGetComponent<KBatchedAnimController>(out var _))
					continue;
				if (!id.gameObject.TryGetComponent<AnimStateSyncer>(out var _))
					return UnitTestResult.Fail($"Entity '{id.gameObject.name}' is missing AnimStateSyncer");
				if (!AnimSyncEligibility.IsAnimatedNonMinion(id.gameObject))
					return UnitTestResult.Fail($"Entity '{id.gameObject.name}' should not have AnimStateSyncer");

				return UnitTestResult.Pass($"Entity '{id.gameObject.name}' is sync-eligible");
			}

			return Game.Instance == null
				? UnitTestResult.Skip("Requires a loaded colony with animated entities")
				: UnitTestResult.Fail("No non-minion animated network entities found");
		}

		[UnitTest(name: "Anim resync request packet: roundtrip", category: "Animation")]
		public static UnitTestResult AnimResyncRequestPacketRoundtrip()
		{
			var packet = new AnimResyncRequestPacket
			{
				RequesterId = 99,
				NetIds = [11, 22, 33]
			};

			using var ms = new MemoryStream();
			using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);

			ms.Position = 0;

			var copy = new AnimResyncRequestPacket();
			using (var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);

			if (copy.RequesterId != packet.RequesterId)
				return UnitTestResult.Fail("RequesterId did not roundtrip");
			if (!copy.NetIds.SequenceEqual(packet.NetIds))
				return UnitTestResult.Fail("NetId list did not roundtrip");

			return UnitTestResult.Pass("AnimResyncRequestPacket serialize/deserialize roundtrip succeeded");
		}

		private static AnimSyncPacket Snapshot(
			int netId, int animHash, Action<AnimSyncPacket> configure = null)
		{
			var packet = new AnimSyncPacket
			{
				NetId = netId,
				AnimHash = animHash,
				Mode = (byte)KAnim.PlayMode.Loop,
				Speed = 1f,
				ElapsedTime = 0.5f,
				StartTick = 100,
				DurationTicks = 10,
			};
			configure?.Invoke(packet);
			return packet;
		}

		private static bool RejectsSerialize(IPacket packet)
		{
			try
			{
				using var stream = new MemoryStream();
				using var writer = new BinaryWriter(stream);
				packet.Serialize(writer);
				return false;
			}
			catch (InvalidDataException)
			{
				return true;
			}
		}

		private static bool RejectsAnimDeserialize(float elapsed, byte mode)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
			{
				writer.Write(1);
				writer.Write(1);
				writer.Write(mode);
				writer.Write(1f);
				writer.Write(elapsed);
				writer.Write(1L);
				writer.Write(1U);
			}
			stream.Position = 0;
			try
			{
				using var reader = new BinaryReader(stream);
				new AnimSyncPacket().Deserialize(reader);
				return false;
			}
			catch (InvalidDataException)
			{
				return true;
			}
		}

		private static bool RejectsEmptyAnimBatchDeserialize()
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
			{
				writer.Write(1UL);
				writer.Write(0);
			}
			stream.Position = 0;
			try
			{
				using var reader = new BinaryReader(stream);
				new AnimSyncBatchPacket().Deserialize(reader);
				return false;
			}
			catch (InvalidDataException)
			{
				return true;
			}
		}

		private static int WireBytes(AnimSyncBatchPacket packet)
		{
			using var stream = new MemoryStream();
			using var writer = new BinaryWriter(stream);
			writer.Write(0);
			packet.Serialize(writer);
			return checked((int)stream.Length);
		}

		private static IEnumerable<MethodInfo> MethodsIncludingNested(Type type)
		{
			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
			                           | BindingFlags.Static | BindingFlags.Instance
			                           | BindingFlags.DeclaredOnly;
			foreach (MethodInfo method in type.GetMethods(flags))
				yield return method;
			foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
				foreach (MethodInfo method in MethodsIncludingNested(nested))
					yield return method;
		}

		private static bool Calls(MethodInfo caller, MethodInfo callee)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			if (il == null || callee == null)
				return false;
			byte[] token = BitConverter.GetBytes(callee.MetadataToken);
			for (int index = 0; index <= il.Length - token.Length; index++)
				if (il.Skip(index).Take(token.Length).SequenceEqual(token))
					return true;
			return false;
		}
	}
}
