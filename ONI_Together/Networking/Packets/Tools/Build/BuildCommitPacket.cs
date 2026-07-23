using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public sealed class BuildCommitPacket : IPacket, IHostOnlyPacket
	{
		public BuildOperationId OperationId;
		public string PrefabID = string.Empty;
		public byte GeometryKind;
		public int Cell;
		public Orientation Orientation;
		public List<int> Cells = [];
		public List<string> MaterialTags = [];
		public string FacadeID = BuildRequestValidator.DefaultFacade;
		public int PriorityClass;
		public int PriorityValue;
		public int ObjectLayer;
		public List<PlacementOutcome> Placements = [];
		public List<UtilityEdge> Connections = [];
		public ulong Revision;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			ValidateWire();
			BuildRequestPacket.WriteOperationId(writer, OperationId);
			writer.Write(PrefabID);
			writer.Write(GeometryKind);
			writer.Write(Cell);
			writer.Write((int)Orientation);
			writer.Write(Cells.Count);
			foreach (int pathCell in Cells)
				writer.Write(pathCell);
			BuildRequestPacket.WriteMaterials(writer, MaterialTags);
			writer.Write(FacadeID);
			writer.Write(PriorityClass);
			writer.Write(PriorityValue);
			writer.Write(ObjectLayer);
			writer.Write(Revision);
			writer.Write(Placements.Count);
			foreach (PlacementOutcome outcome in Placements)
			{
				writer.Write(outcome.Cell);
				writer.Write((byte)outcome.Kind);
				writer.Write(outcome.NetId);
				writer.Write(outcome.LifecycleRevision);
			}
			writer.Write(Connections.Count);
			foreach (UtilityEdge edge in Connections)
			{
				writer.Write(edge.FromCell);
				writer.Write(edge.ToCell);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			OperationId = BuildRequestPacket.ReadOperationId(reader);
			PrefabID = BuildWire.ReadBoundedString(reader, BuildRequestValidator.MaxIdLength);
			GeometryKind = reader.ReadByte();
			Cell = reader.ReadInt32();
			Orientation = (Orientation)reader.ReadInt32();
			int pathCount = reader.ReadInt32();
			if (pathCount < 0 || pathCount > BuildRequestValidator.MaxPathNodeCount)
				throw new InvalidDataException("Invalid build path count");
			Cells = new List<int>(pathCount);
			for (int i = 0; i < pathCount; i++)
				Cells.Add(reader.ReadInt32());
			MaterialTags = BuildWire.ReadMaterials(reader);
			FacadeID = BuildWire.ReadBoundedString(reader, BuildRequestValidator.MaxIdLength);
			PriorityClass = reader.ReadInt32();
			PriorityValue = reader.ReadInt32();
			ObjectLayer = reader.ReadInt32();
			Revision = reader.ReadUInt64();
			int placementCount = reader.ReadInt32();
			if (placementCount < 0 || placementCount > BuildRequestValidator.MaxPathNodeCount)
				throw new InvalidDataException("Invalid build placement count");
			Placements = new List<PlacementOutcome>(placementCount);
			for (int i = 0; i < placementCount; i++)
				Placements.Add(new PlacementOutcome(
					reader.ReadInt32(), (BuildPlacementKind)reader.ReadByte(),
					reader.ReadInt32(), reader.ReadUInt64()));
			int edgeCount = reader.ReadInt32();
			if (edgeCount < 0 || edgeCount > BuildRequestValidator.MaxPathNodeCount)
				throw new InvalidDataException("Invalid build edge count");
			Connections = new List<UtilityEdge>(edgeCount);
			for (int i = 0; i < edgeCount; i++)
				Connections.Add(new UtilityEdge(reader.ReadInt32(), reader.ReadInt32()));
			ValidateWire();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			if (!ShouldApply(MultiplayerSession.IsHost, PacketHandler.CurrentContext.SenderIsHost))
				return;
			ApplyResult result = BuildCommitApplier.Apply(ToDomain());
			if (!result.Applied)
				DebugConsole.LogWarning($"[BuildCommitPacket] apply rejected: {result.Error}");
		}

		internal static BuildCommitPacket FromDomain(BuildCommit commit)
		{
			var packet = new BuildCommitPacket
			{
				OperationId = commit.OperationId,
				Revision = commit.Revision.Value,
				Placements = [.. commit.Placements],
				Connections = [.. commit.Connections]
			};
			BuildRequestPacket requestPacket = new(commit.Request);
			packet.PrefabID = requestPacket.PrefabID;
			packet.GeometryKind = requestPacket.GeometryKind;
			packet.Cell = requestPacket.Cell;
			packet.Orientation = requestPacket.Orientation;
			packet.Cells = requestPacket.Cells;
			packet.MaterialTags = requestPacket.MaterialTags;
			packet.FacadeID = requestPacket.FacadeID;
			packet.PriorityClass = requestPacket.PriorityClass;
			packet.PriorityValue = requestPacket.PriorityValue;
			packet.ObjectLayer = requestPacket.ObjectLayer;
			return packet;
		}

		internal BuildCommit ToDomain()
		{
			var requestPacket = new BuildRequestPacket
			{
				OperationId = OperationId,
				PrefabID = PrefabID,
				GeometryKind = GeometryKind,
				Cell = Cell,
				Orientation = Orientation,
				Cells = [.. Cells],
				MaterialTags = [.. MaterialTags],
				FacadeID = FacadeID,
				PriorityClass = PriorityClass,
				PriorityValue = PriorityValue,
				ObjectLayer = ObjectLayer
			};
			return new BuildCommit(
				requestPacket.ToDomain(), OperationId, Placements, Connections,
				new BuildRevision(Revision));
		}

		internal static bool ShouldApply(bool localIsHost, bool senderIsHost)
			=> !localIsHost && senderIsHost;

		private void ValidateWire()
		{
			BuildRequest request = ToRequest();
			if (!BuildRequestValidator.TryValidate(request, out _)
				|| Revision == 0 || !BuildCommitValidator.TryValidate(
					new BuildCommit(request, OperationId, Placements, Connections,
						new BuildRevision(Revision)), out _))
				throw new InvalidDataException("Invalid build commit payload");
		}

		private BuildRequest ToRequest()
		{
			BuildGeometry geometry = GeometryKind == 1
				? new SinglePlacementGeometry(Cell, Orientation)
				: new UtilityPathGeometry(Cells);
			return new BuildRequest(OperationId, PrefabID, geometry, MaterialTags,
				FacadeID, PriorityClass, PriorityValue, ObjectLayer);
		}
	}
}
