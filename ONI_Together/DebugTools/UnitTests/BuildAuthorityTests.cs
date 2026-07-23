using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools.Build;
using ONI_Together.Patches.ToolPatches.Build;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildAuthorityTests
	{
		[UnitTest(name: "Build requests and commits enforce authority markers", category: "Sync")]
		public static UnitTestResult AuthorityMarkersAndGates()
		{
			var direct = new DispatchContext(41, false);
			var verified = direct.AsVerifiedHostBroadcast();
			if (new BuildRequestPacket() is not IClientRelayable
			    || new BuildCommitPacket() is not IHostOnlyPacket
			    || new BuildRejectedPacket() is not IHostOnlyPacket)
				return UnitTestResult.Fail("Build request/commit/rejection authority marker is missing");
			if (BuildRequestPacket.ShouldAccept(true, direct, true)
			    || BuildRequestPacket.ShouldAccept(true, verified, false)
			    || !BuildRequestPacket.ShouldAccept(true, verified, true)
			    || BuildRequestPacket.ShouldAccept(false, verified, true)
			    || !BuildCommitPacket.ShouldApply(false, true)
			    || BuildCommitPacket.ShouldApply(true, true)
			    || BuildCommitPacket.ShouldApply(false, false)
			    || PacketHandler.CanDispatchPacket(new BuildRejectedPacket(), direct, true)
			    || !PacketHandler.CanDispatchPacket(
				    new BuildRejectedPacket(), verified, false))
				return UnitTestResult.Fail("Build authority gate is incorrect");
			return UnitTestResult.Pass("Only verified client requests reach host; commit and rejection are host-only");
		}

		[UnitTest(name: "Build request and commit preserve host outcomes", category: "Sync")]
		public static UnitTestResult BuildRoundtrip()
		{
			BuildOperationId operation = new(8, 17, 41);
			BuildRequest request = Request(operation, new BuildGeometry.SinglePlacement(
				123, Orientation.FlipH), "ManualGenerator", ObjectLayer.Building, "Copper");
			BuildRequestPacket packet = Roundtrip(new BuildRequestPacket(request), new BuildRequestPacket());
			BuildRequest decoded = packet.ToDomain();
			if (decoded.OperationId != operation || decoded.PrefabId != "ManualGenerator"
			    || decoded.Geometry is not SinglePlacementGeometry single
			    || single.Cell != 123 || single.Orientation != Orientation.FlipH
			    || decoded.MaterialTags.Count != 1 || decoded.MaterialTags[0] != "Copper")
				return UnitTestResult.Fail("Build request did not preserve operation, geometry, or material");

			var commit = new BuildCommit(request, operation,
				new[] { new PlacementOutcome(123, BuildPlacementKind.Completed, 1234, 55) },
				Array.Empty<UtilityEdge>(), new BuildRevision(55));
			BuildCommitPacket commitCopy = Roundtrip(
				BuildCommitPacket.FromDomain(commit), new BuildCommitPacket());
			if (commitCopy.OperationId != operation || commitCopy.Revision != 55
			    || commitCopy.Placements.Count != 1 || commitCopy.Placements[0].NetId != 1234
			    || commitCopy.Placements[0].LifecycleRevision != 55)
				return UnitTestResult.Fail("Build commit did not preserve authoritative placement identity");
			return UnitTestResult.Pass("Client request excludes policy; host commit carries exact outcome identity");
		}

		[UnitTest(name: "Build policy validates geometry, materials, and priority", category: "Sync")]
		public static UnitTestResult BuildPolicyBounds()
		{
			if (!BuildRequestValidator.IsWireCell(123)
			    || BuildRequestValidator.IsWireCell(-1)
			    || BuildRequestValidator.IsWireCell(BuildRequestValidator.MaxWireCell)
			    || !BuildRequestValidator.IsOrientationAllowed(Orientation.Neutral, PermittedRotations.Unrotatable)
			    || BuildRequestValidator.IsOrientationAllowed(Orientation.R90, PermittedRotations.Unrotatable)
			    || !BuildRequestValidator.IsOrientationAllowed(Orientation.R270, PermittedRotations.R360)
			    || BuildRequestValidator.IsOrientationAllowed(Orientation.FlipH, PermittedRotations.R360)
			    || !BuildRequestValidator.IsPriorityAllowed(0, 1)
			    || !BuildRequestValidator.IsPriorityAllowed(2, 1)
			    || BuildRequestValidator.IsPriorityAllowed(2, 2)
			    || !BuildRequestValidator.AreMaterialTagsWireValid(new[] { "Copper" })
			    || BuildRequestValidator.AreMaterialTagsWireValid(Array.Empty<string>()))
				return UnitTestResult.Fail("Build request policy accepted an invalid geometry, material, or priority");
			return UnitTestResult.Pass("Build metadata is bounded before host execution");
		}

		[UnitTest(name: "Utility path planner preserves successful adjacency", category: "Sync")]
		public static UnitTestResult UtilityPathBounds()
		{
			if (!BuildRequestValidator.IsPathShapeWireValid(new[] { 101, 102, 103 })
			    || BuildRequestValidator.IsPathShapeWireValid(new[] { 5, 6, 5 })
			    || BuildRequestValidator.IsPathShapeWireValid(new[] { BuildRequestValidator.MaxWireCell })
			    || !UtilityPathPlanner.TryPlan(new[] { 5, 6, 7 }, out BuildGeometry utility)
			    || utility is not BuildGeometry.UtilityPath)
				return UnitTestResult.Fail("Utility path planner rejected a valid path or admitted invalid bounds");
			var edges = UtilityPathPlanner.BuildConnections(
				new[] { 5, 6, 7, 8 }, new[] { 5, 6, 8 });
			return edges.Count == 1 && edges[0] == new UtilityEdge(5, 6)
				? UnitTestResult.Pass("Utility connections only join adjacent successful cells")
				: UnitTestResult.Fail("Utility planner created an edge across a failed cell");
		}

		[UnitTest(name: "Build requests carry no instant-build client authority", category: "Sync")]
		public static UnitTestResult ClientToolGate()
		{
			BuildRequestPacket packet = new(new BuildRequest(
				new BuildOperationId(8, 17, 42), "Tile",
				new SinglePlacementGeometry(123, Orientation.Neutral),
				new[] { "SandStone" }, "DEFAULT_FACADE", 0, 5, (int)ObjectLayer.Building));
			return packet.GeometryKind == 1 && packet.OperationId.IsValid
				? UnitTestResult.Pass("Client packet contains intent only; host policy remains outside request")
				: UnitTestResult.Fail("Build request packet lost stable operation or geometry intent");
		}

		private static BuildRequest Request(
			BuildOperationId operation,
			BuildGeometry geometry,
			string prefab,
			ObjectLayer layer,
			string material)
			=> new(operation, prefab, geometry, new[] { material },
				"DEFAULT_FACADE", 0, 5, (int)layer);

		private static T Roundtrip<T>(T input, T output) where T : IPacket
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				input.Serialize(writer);
			stream.Position = 0;
			using var reader = new BinaryReader(stream);
			output.Deserialize(reader);
			if (stream.Position != stream.Length)
				throw new InvalidDataException("Build packet left unread bytes");
			return output;
		}
	}
}
