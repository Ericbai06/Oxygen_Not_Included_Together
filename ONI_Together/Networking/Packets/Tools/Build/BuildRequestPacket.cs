using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Patches.ToolPatches.Build;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public sealed class BuildRequestPacket : IPacket, IClientRelayable
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

		public BuildRequestPacket() { }

		internal BuildRequestPacket(BuildRequest request)
		{
			OperationId = request.OperationId;
			PrefabID = request.PrefabId;
			MaterialTags = [.. request.MaterialTags];
			FacadeID = request.FacadeId;
			PriorityClass = request.PriorityClass;
			PriorityValue = request.PriorityValue;
			ObjectLayer = request.ObjectLayer;
			switch (request.Geometry)
			{
				case BuildGeometry.SinglePlacement single:
					GeometryKind = 1;
					Cell = single.Cell;
					Orientation = single.Orientation;
					break;
				case SinglePlacementGeometry singleGeometry:
					GeometryKind = 1;
					Cell = singleGeometry.Cell;
					Orientation = singleGeometry.Orientation;
					break;
				case BuildGeometry.UtilityPath utility:
					GeometryKind = 2;
					Cells = [.. utility.Cells];
					break;
				case UtilityPathGeometry utilityGeometry:
					GeometryKind = 2;
					Cells = [.. utilityGeometry.Cells];
					break;
			}
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			ValidateWire();
			WriteOperationId(writer, OperationId);
			writer.Write(PrefabID);
			writer.Write(GeometryKind);
			writer.Write(Cell);
			writer.Write((int)Orientation);
			writer.Write(Cells.Count);
			foreach (int cell in Cells)
				writer.Write(cell);
			WriteMaterials(writer, MaterialTags);
			writer.Write(FacadeID);
			writer.Write(PriorityClass);
			writer.Write(PriorityValue);
			writer.Write(ObjectLayer);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			OperationId = ReadOperationId(reader);
			PrefabID = BuildWire.ReadBoundedString(reader, BuildRequestValidator.MaxIdLength);
			GeometryKind = reader.ReadByte();
			Cell = reader.ReadInt32();
			Orientation = (Orientation)reader.ReadInt32();
			int count = reader.ReadInt32();
			if (count < 0 || count > BuildRequestValidator.MaxPathNodeCount)
				throw new InvalidDataException("Invalid build geometry count");
			Cells = new List<int>(count);
			for (int i = 0; i < count; i++)
				Cells.Add(reader.ReadInt32());
			MaterialTags = BuildWire.ReadMaterials(reader);
			FacadeID = BuildWire.ReadBoundedString(reader, BuildRequestValidator.MaxIdLength);
			PriorityClass = reader.ReadInt32();
			PriorityValue = reader.ReadInt32();
			ObjectLayer = reader.ReadInt32();
			ValidateWire();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			DispatchContext context = PacketHandler.CurrentContext;
			bool verified = MultiplayerSession.GetPlayer(context.SenderId)?.ProtocolVerified == true;
			if (!ShouldAccept(MultiplayerSession.IsHost, context, verified))
				return;
			if (OperationId.SenderId != context.SenderId)
			{
				BuildPublisher.Publish(new BuildRejected(
					OperationId, BuildRejectionReason.InvalidRequest,
					"operation sender does not match transport sender"));
				return;
			}
			AuthoritativeBuildExecutor.SetSessionEpoch(context.SessionEpoch);
			BuildRequest request = ToDomain();
			if (AuthoritativeBuildExecutor.Execute(request, HostBuildPolicyProvider.Current,
				out BuildCommit commit, out BuildRejected rejection))
				BuildPublisher.Publish(commit);
			else
				BuildPublisher.Publish(rejection);
		}

		internal static bool ShouldAccept(bool localIsHost, DispatchContext context, bool protocolVerified)
			=> localIsHost && !context.SenderIsHost && context.IsVerifiedHostBroadcast && protocolVerified;

		internal BuildRequest ToDomain()
		{
			BuildGeometry geometry = GeometryKind == 1
				? new SinglePlacementGeometry(Cell, Orientation)
				: new UtilityPathGeometry(Cells);
			return new BuildRequest(
				OperationId, PrefabID, geometry, MaterialTags, FacadeID,
				PriorityClass, PriorityValue, ObjectLayer);
		}

		private void ValidateWire()
		{
			if (!OperationId.IsValid || !BuildRequestValidator.IsBoundedId(PrefabID) ||
				(GeometryKind != 1 && GeometryKind != 2) ||
				!BuildRequestValidator.IsKnownOrientationValue(Orientation) ||
				!BuildRequestValidator.AreMaterialTagsWireValid(MaterialTags) ||
				!BuildRequestValidator.IsBoundedFacade(FacadeID) ||
				!BuildRequestValidator.IsPriorityAllowed(PriorityClass, PriorityValue) ||
				ObjectLayer < 0 || (GeometryKind == 1
					? !BuildRequestValidator.IsWireCell(Cell)
					: !BuildRequestValidator.IsPathShapeWireValid(Cells)))
				throw new InvalidDataException("Invalid build request payload");
		}

		internal static void WriteOperationId(BinaryWriter writer, BuildOperationId operationId)
		{
			writer.Write(operationId.SessionEpoch);
			writer.Write(operationId.SenderId);
			writer.Write(operationId.Sequence);
		}

		internal static BuildOperationId ReadOperationId(BinaryReader reader)
			=> new(reader.ReadInt64(), reader.ReadUInt64(), reader.ReadUInt64());

		internal static void WriteMaterials(BinaryWriter writer, IReadOnlyList<string> materials)
		{
			writer.Write(materials.Count);
			foreach (string material in materials)
				writer.Write(material);
		}
	}

	internal static class BuildWire
	{
		internal static string ReadBoundedString(BinaryReader reader, int maxLength)
		{
			string value = reader.ReadString();
			if (value.Length > maxLength)
				throw new InvalidDataException("Build string exceeds limit");
			return value;
		}

		internal static List<string> ReadMaterials(BinaryReader reader)
		{
			int count = reader.ReadInt32();
			if (count <= 0 || count > BuildRequestValidator.MaxMaterialTagCount)
				throw new InvalidDataException("Invalid build material count");
			var values = new List<string>(count);
			for (int i = 0; i < count; i++)
				values.Add(ReadBoundedString(reader, BuildRequestValidator.MaxMaterialTagLength));
			return values;
		}
	}
}
