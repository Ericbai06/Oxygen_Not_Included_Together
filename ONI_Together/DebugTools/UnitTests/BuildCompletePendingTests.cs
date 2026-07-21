#if DEBUG
using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildCompletePendingTests
	{
		[UnitTest(name: "v10 pending build completion binds once", category: "Sync")]
		public static UnitTestResult PendingCompletionBindsOnce()
		{
			BuildCompletePacket.ClearPending();
			try
			{
				BuildCompletePacket source = Packet(-910001, 17);
				BuildCompletePacket.StorePendingForTests(source, 100f);
				if (!BuildCompletePacket.TryTakePendingForTests(
					    source.NetId, source.LifecycleRevision, out BuildCompletePacket first))
					return UnitTestResult.Fail("Matching Constructable lifecycle did not take pending completion");
				bool reentrantTake = BuildCompletePacket.TryTakePendingForTests(
					source.NetId, source.LifecycleRevision, out _);
				if (reentrantTake || 0 != BuildCompletePacket.PendingCountForTests)
					return UnitTestResult.Fail("Pending completion remained visible during Build registration reentry");
				if (source.NetId != first.NetId || source.LifecycleRevision != first.LifecycleRevision)
					return UnitTestResult.Fail("Pending completion returned a different lifecycle identity");
				return UnitTestResult.Pass("Pending completion is removed before apply and cannot apply twice");
			}
			finally { BuildCompletePacket.ClearPending(); }
		}

		[UnitTest(name: "v10 pending build completion follows lifecycle ordering", category: "Sync")]
		public static UnitTestResult PendingCompletionLifecycleOrdering()
		{
			const int netId = -910002;
			BuildCompletePacket.ClearPending();
			try
			{
				BuildCompletePacket.StorePendingForTests(Packet(netId, 20), 100f);
				BuildCompletePacket.StorePendingForTests(Packet(netId, 19), 101f);
				if (!BuildCompletePacket.HasPendingForTests(netId, 20)
				    || BuildCompletePacket.HasPendingForTests(netId, 19))
					return UnitTestResult.Fail("Older lifecycle displaced pending build completion");
				BuildCompletePacket.StorePendingForTests(Packet(netId, 21), 102f);
				if (BuildCompletePacket.HasPendingForTests(netId, 20)
				    || !BuildCompletePacket.HasPendingForTests(netId, 21))
					return UnitTestResult.Fail("New lifecycle retained old pending completion");
				BuildCompletePacket.CancelPending(netId, 21);
				return 0 == BuildCompletePacket.PendingCountForTests
					? UnitTestResult.Pass("New lifecycle and tombstone retire old pending completion")
					: UnitTestResult.Fail("Lifecycle tombstone retained pending build completion");
			}
			finally { BuildCompletePacket.ClearPending(); }
		}

		[UnitTest(name: "v10 pending build completion is bounded and expiring", category: "Sync")]
		public static UnitTestResult PendingCompletionBounds()
		{
			BuildCompletePacket.ClearPending();
			try
			{
				for (int index = 0; index <= BuildCompletePacket.MaxPendingCompletions; index++)
					BuildCompletePacket.StorePendingForTests(
						Packet(-920000 - index, (ulong)index + 1), 100f + index * 0.001f);
				if (BuildCompletePacket.MaxPendingCompletions
				    != BuildCompletePacket.PendingCountForTests)
					return UnitTestResult.Fail("Pending build completion cache exceeded or undershot its bound");
				BuildCompletePacket.ClearPending();
				BuildCompletePacket.StorePendingForTests(Packet(-930001, 1), 100f);
				BuildCompletePacket.PrunePendingForTests(
					100f + BuildCompletePacket.PendingLifetimeSeconds - 0.001f);
				if (1 != BuildCompletePacket.PendingCountForTests)
					return UnitTestResult.Fail("Pending build completion expired before TTL");
				BuildCompletePacket.PrunePendingForTests(
					100f + BuildCompletePacket.PendingLifetimeSeconds);
				return 0 == BuildCompletePacket.PendingCountForTests
					? UnitTestResult.Pass("Pending build completion cache has exact capacity and TTL")
					: UnitTestResult.Fail("Expired pending build completion survived TTL");
			}
			finally { BuildCompletePacket.ClearPending(); }
		}

		[UnitTest(name: "v10 pending build completion resets with session", category: "Sync")]
		public static UnitTestResult PendingCompletionSessionReset()
		{
			BuildCompletePacket.ClearPending();
			BuildCompletePacket.StorePendingForTests(Packet(-940001, 3), 100f);
			SessionStateReset.Reset();
			return 0 == BuildCompletePacket.PendingCountForTests
				? UnitTestResult.Pass("Session reset clears pending build completions")
				: UnitTestResult.Fail("Pending build completion leaked across session reset");
		}

		private static BuildCompletePacket Packet(int netId, ulong lifecycle)
		{
			return new BuildCompletePacket
			{
				Cell = 1,
				PrefabID = "Wire",
				Orientation = Orientation.Neutral,
				MaterialTags = new List<string> { "Copper" },
				Temperature = 300f,
				FacadeID = "DEFAULT_FACADE",
				ObjectLayer = global::ObjectLayer.Wire,
				NetId = netId,
				LifecycleRevision = lifecycle
			};
		}
	}
}
#endif
