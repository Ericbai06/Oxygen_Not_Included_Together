using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
	public sealed class ResearchProgressPacket : IPacket, IHostOnlyPacket
	{
		public long ResearchRevision;
		public ResearchProgressData Progress = new();

		public void Serialize(BinaryWriter writer)
		{
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research progress packet");
			writer.Write(ResearchRevision);
			Progress.Serialize(writer);
		}

		public void Deserialize(BinaryReader reader)
		{
			ResearchRevision = reader.ReadInt64();
			Progress = ResearchProgressData.Deserialize(reader);
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research progress packet");
		}

		public void OnDispatched()
		{
			ResearchSyncCoordinator.ApplyProgress(this);
		}

		internal bool IsWireValid()
			=> ResearchRevision > 0 && Progress != null && Progress.IsWireValid();
	}
}
