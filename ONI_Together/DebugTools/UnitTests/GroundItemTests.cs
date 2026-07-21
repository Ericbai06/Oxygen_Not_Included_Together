using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class GroundItemTests
	{
		[UnitTest(name: "GroundItemPickedUpPacket: serialization roundtrip", category: "GroundItems")]
		public static UnitTestResult PacketRoundtrip()
		{
			var original = new GroundItemPickedUpPacket { NetId = 999888777, Revision = 77 };
			using var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);
			original.Serialize(writer);
			ms.Position = 0;
			using var reader = new BinaryReader(ms);
			var copy = new GroundItemPickedUpPacket();
			copy.Deserialize(reader);
			if (999888777 != copy.NetId || 77UL != copy.Revision || ms.Position != ms.Length)
				return UnitTestResult.Fail("Ground pickup lost NetId/revision or left unread bytes");
			return UnitTestResult.Pass("GroundItemPickedUpPacket roundtrip OK");
		}

		[UnitTest(name: "GroundItemPickedUpPacket: sends immediately", category: "GroundItems")]
		public static UnitTestResult SendsImmediately()
		{
			if (typeof(IBulkablePacket).IsAssignableFrom(typeof(GroundItemPickedUpPacket)))
				return UnitTestResult.Fail("GroundItemPickedUpPacket still depends on bulk flushing");

			return UnitTestResult.Pass("GroundItemPickedUpPacket dispatches immediately and stays independent of bulk flush timing");
		}

		[UnitTest(name: "Ground pickup resolved target requires a newer tombstone", category: "GroundItems")]
		public static UnitTestResult ResolvedRemovalRequiresNewerRevision()
		{
			if (GroundItemPickedUpPacket.ShouldRemoveResolved(0, 11)
			    || GroundItemPickedUpPacket.ShouldRemoveResolved(10, 9)
			    || GroundItemPickedUpPacket.ShouldRemoveResolved(10, 10)
			    || !GroundItemPickedUpPacket.ShouldRemoveResolved(10, 11))
				return UnitTestResult.Fail("Ground pickup removed a zero/stale/same lifecycle or rejected a newer tombstone");
			return UnitTestResult.Pass("Resolved ground items are removed only by a newer tombstone");
		}

		[UnitTest(name: "GroundItems: NetworkIdentityRegistry accessible", category: "GroundItems")]
		public static UnitTestResult RegistryAccessible()
		{
			// TryGetComponent with a non-existent NetId should return false (not throw)
			bool found = NetworkIdentityRegistry.TryGetComponent<Pickupable>(-1, out _);
			if (found)
				return UnitTestResult.Fail("NetId -1 should not exist in registry");
			return UnitTestResult.Pass("NetworkIdentityRegistry.TryGetComponent accessible and returns false for unknown NetId");
		}

		[UnitTest(name: "ClearTool.Instance accessible (sweep relay)", category: "GroundItems", liveSafe: true)]
		public static UnitTestResult ClearToolAccessible()
		{
			if (ClearTool.Instance == null)
				return Game.Instance == null
					? UnitTestResult.Skip("Requires a loaded colony")
					: UnitTestResult.Fail("ClearTool.Instance is null");
			return UnitTestResult.Pass("ClearTool.Instance accessible");
		}

		[UnitTest(name: "Ground pickup pending rejects old and same spawn", category: "GroundItems")]
		public static UnitTestResult PendingRevisionPolicy()
		{
			const int netId = -424242;
			float now = Time.realtimeSinceStartup;
			try
			{
				GroundItemPickedUpPacket.ClearPending();
				GroundItemPickedUpPacket.StorePendingForTests(netId, 10, now);
				if (!GroundItemPickedUpPacket.TryConsumePending(netId, 9)
				    || 1 != GroundItemPickedUpPacket.PendingCountForTests)
					return UnitTestResult.Fail("Old spawn escaped its pending tombstone");
				if (!GroundItemPickedUpPacket.TryConsumePending(netId, 10)
				    || 0 != GroundItemPickedUpPacket.PendingCountForTests)
					return UnitTestResult.Fail("Same-revision spawn escaped or retained its tombstone");
				GroundItemPickedUpPacket.StorePendingForTests(netId, 10, now);
				if (GroundItemPickedUpPacket.TryConsumePending(netId, 11)
				    || 0 != GroundItemPickedUpPacket.PendingCountForTests)
					return UnitTestResult.Fail("Higher lifecycle spawn was removed by an old tombstone");
				return UnitTestResult.Pass("Old/same spawn is removed; higher revision survives and clears pending state");
			}
			finally
			{
				GroundItemPickedUpPacket.ClearPending();
			}
		}

		[UnitTest(name: "Ground pickup pending capacity is bounded", category: "GroundItems")]
		public static UnitTestResult PendingCapacity()
		{
			float now = Time.realtimeSinceStartup;
			try
			{
				GroundItemPickedUpPacket.ClearPending();
				for (int index = 0; index <= GroundItemPickedUpPacket.MaxPendingPickups; index++)
					GroundItemPickedUpPacket.StorePendingForTests(
						-500000 - index, (ulong)index + 1, now + index * 0.001f);
				if (GroundItemPickedUpPacket.MaxPendingPickups
				    != GroundItemPickedUpPacket.PendingCountForTests)
					return UnitTestResult.Fail("Pending ground pickup cache exceeded or undershot its exact bound");
				return UnitTestResult.Pass("Pending ground pickup cache evicts at its exact capacity");
			}
			finally { GroundItemPickedUpPacket.ClearPending(); }
		}

		[UnitTest(name: "Ground pickup pending TTL expires deterministically", category: "GroundItems")]
		public static UnitTestResult PendingTimeout()
		{
			const float storedAt = 100f;
			try
			{
				GroundItemPickedUpPacket.ClearPending();
				GroundItemPickedUpPacket.StorePendingForTests(-700001, 3, storedAt);
				GroundItemPickedUpPacket.PrunePendingForTests(
					storedAt + GroundItemPickedUpPacket.PendingPickupLifetimeSeconds - 0.001f);
				if (1 != GroundItemPickedUpPacket.PendingCountForTests)
					return UnitTestResult.Fail("Pending ground pickup expired before its TTL");
				GroundItemPickedUpPacket.PrunePendingForTests(
					storedAt + GroundItemPickedUpPacket.PendingPickupLifetimeSeconds);
				return 0 == GroundItemPickedUpPacket.PendingCountForTests
					? UnitTestResult.Pass("Pending ground pickup expires exactly at TTL")
					: UnitTestResult.Fail("Expired ground pickup survived its TTL");
			}
			finally { GroundItemPickedUpPacket.ClearPending(); }
		}

		[UnitTest(name: "Ground pickup pending state resets with session", category: "GroundItems")]
		public static UnitTestResult PendingSessionReset()
		{
			GroundItemPickedUpPacket.ClearPending();
			GroundItemPickedUpPacket.StorePendingForTests(
				-800001, 4, Time.realtimeSinceStartup);
			SessionStateReset.Reset();
			return 0 == GroundItemPickedUpPacket.PendingCountForTests
				? UnitTestResult.Pass("Session reset clears pending ground pickup tombstones")
				: UnitTestResult.Fail("Pending ground pickup tombstone leaked across sessions");
		}
	}
}
