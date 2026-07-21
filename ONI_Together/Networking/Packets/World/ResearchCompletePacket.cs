using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
	public sealed class ResearchCompletePacket : IPacket, IHostOnlyPacket
	{
		public long ResearchRevision;
		public string TechId = string.Empty;

		public void Serialize(BinaryWriter writer)
		{
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research completion packet");
			writer.Write(ResearchRevision);
			ResearchSyncProtocol.WriteId(writer, TechId, ResearchSyncProtocol.MaxTechIdLength);
		}

		public void Deserialize(BinaryReader reader)
		{
			ResearchRevision = reader.ReadInt64();
			TechId = ResearchSyncProtocol.ReadId(reader, ResearchSyncProtocol.MaxTechIdLength);
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research completion packet");
		}

		public void OnDispatched()
		{
			ResearchSyncCoordinator.ApplyCompletion(this);
		}

		internal bool IsWireValid()
			=> ResearchRevision > 0 && !string.IsNullOrEmpty(TechId)
			   && TechId.Length <= ResearchSyncProtocol.MaxTechIdLength;
	}
}
