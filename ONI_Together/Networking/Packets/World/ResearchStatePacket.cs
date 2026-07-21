using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using System.Collections.Generic;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
	public sealed class ResearchStatePacket : IPacket, IHostOnlyPacket
	{
		internal const int MaxTechCount = ResearchSyncProtocol.MaxTechCount;
		public long ResearchRevision;
		public List<string> UnlockedTechIds = new();
		public List<string> QueuedTechIds = new();
		public string ActiveTechId = string.Empty;
		public List<ResearchProgressData> ProgressEntries = new();

		public void Serialize(BinaryWriter writer)
		{
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research state snapshot");
			writer.Write(ResearchRevision);
			WriteTechIds(writer, UnlockedTechIds);
			WriteTechIds(writer, QueuedTechIds);
			ResearchSyncProtocol.WriteId(
				writer, ActiveTechId, ResearchSyncProtocol.MaxTechIdLength, allowEmpty: true);
			writer.Write(ProgressEntries.Count);
			foreach (ResearchProgressData entry in ProgressEntries)
				entry.Serialize(writer);
		}

		public void Deserialize(BinaryReader reader)
		{
			ResearchRevision = reader.ReadInt64();
			UnlockedTechIds = ReadTechIds(reader, "unlocked tech");
			QueuedTechIds = ReadTechIds(reader, "queued tech");
			ActiveTechId = ResearchSyncProtocol.ReadId(
				reader, ResearchSyncProtocol.MaxTechIdLength, allowEmpty: true);
			int count = ResearchSyncProtocol.ReadCount(
				reader, ResearchSyncProtocol.MaxProgressTechCount, "progress tech");
			ProgressEntries = new List<ResearchProgressData>(count);
			for (int i = 0; i < count; i++)
				ProgressEntries.Add(ResearchProgressData.Deserialize(reader));
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research state snapshot");
		}

		public void OnDispatched()
		{
			ResearchSyncCoordinator.ApplyState(this);
		}

		internal bool IsWireValid()
		{
			if (ResearchRevision <= 0 || UnlockedTechIds == null || QueuedTechIds == null
			    || ActiveTechId == null || ActiveTechId.Length > ResearchSyncProtocol.MaxTechIdLength
			    || ProgressEntries == null
			    || UnlockedTechIds.Count > MaxTechCount || QueuedTechIds.Count > MaxTechCount
			    || ProgressEntries.Count > ResearchSyncProtocol.MaxProgressTechCount
			    || !ResearchSyncProtocol.HasUniqueIds(UnlockedTechIds)
			    || !ResearchSyncProtocol.HasUniqueIds(QueuedTechIds))
				return false;
			return HasValidProgressEntries() && HasConsistentTechRoles();
		}

		private bool HasValidProgressEntries()
		{
			var techIds = new HashSet<string>(System.StringComparer.Ordinal);
			int totalPoints = 0;
			foreach (ResearchProgressData entry in ProgressEntries)
			{
				if (entry == null || !entry.IsWireValid() || !techIds.Add(entry.TechId))
					return false;
				totalPoints += entry.Points.Count;
				if (totalPoints > ResearchSyncProtocol.MaxTotalResearchPoints)
					return false;
			}
			return true;
		}

		private bool HasConsistentTechRoles()
		{
			var unlocked = new HashSet<string>(UnlockedTechIds, System.StringComparer.Ordinal);
			if (!string.IsNullOrEmpty(ActiveTechId) && unlocked.Contains(ActiveTechId))
				return false;
			foreach (string techId in QueuedTechIds)
				if (unlocked.Contains(techId))
					return false;
			foreach (ResearchProgressData progress in ProgressEntries)
				if (unlocked.Contains(progress.TechId))
					return false;
			return true;
		}

		private static void WriteTechIds(BinaryWriter writer, IReadOnlyCollection<string> ids)
		{
			writer.Write(ids.Count);
			foreach (string id in ids)
				ResearchSyncProtocol.WriteId(writer, id, ResearchSyncProtocol.MaxTechIdLength);
		}

		private static List<string> ReadTechIds(BinaryReader reader, string field)
		{
			int count = ResearchSyncProtocol.ReadCount(reader, MaxTechCount, field);
			var ids = new List<string>(count);
			for (int i = 0; i < count; i++)
				ids.Add(ResearchSyncProtocol.ReadId(reader, ResearchSyncProtocol.MaxTechIdLength));
			return ids;
		}
	}
}
