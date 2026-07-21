using ONI_Together.Networking.Packets.Architecture;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
	public sealed class ResearchRequestPacket : IPacket
	{
		public ulong ClientRequestId;
		public long BaseResearchRevision;
		public string TechId = string.Empty;

		public void Serialize(BinaryWriter writer)
		{
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research request");
			writer.Write(ClientRequestId);
			writer.Write(BaseResearchRevision);
			ResearchSyncProtocol.WriteId(writer, TechId, ResearchSyncProtocol.MaxTechIdLength);
		}

		public void Deserialize(BinaryReader reader)
		{
			ClientRequestId = reader.ReadUInt64();
			BaseResearchRevision = reader.ReadInt64();
			TechId = ResearchSyncProtocol.ReadId(reader, ResearchSyncProtocol.MaxTechIdLength);
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research request");
		}

		public void OnDispatched()
		{
			ResearchSyncCoordinator.HandleRequest(this);
		}

		internal bool IsWireValid()
			=> ClientRequestId != 0 && BaseResearchRevision > 0
			   && !string.IsNullOrEmpty(TechId)
			   && TechId.Length <= ResearchSyncProtocol.MaxTechIdLength;
	}
}
