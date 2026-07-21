using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ONI_Together.Networking.Packets.World
{
	internal static class ResearchSyncProtocol
	{
		internal const int MaxTechCount = 4096;
		internal const int MaxTechIdLength = 256;
		internal const int MaxProgressTechCount = 4096;
		internal const int MaxResearchTypesPerTech = 32;
		internal const int MaxTotalResearchPoints = 16384;
		internal const int MaxResearchTypeIdLength = 128;
		internal const float MaxResearchPoints = 1000000f;
		private static readonly UTF8Encoding StrictUtf8 = new(false, true);

		internal static bool ShouldApply(long incomingRevision, long appliedRevision)
			=> incomingRevision > 0 && incomingRevision > appliedRevision;

		internal static bool IsCurrentBase(long baseRevision, long hostRevision)
			=> baseRevision > 0 && baseRevision == hostRevision;

		internal static bool IsStrictNextRequest(ulong requestId, ulong previousRequestId)
			=> requestId != 0 && previousRequestId != ulong.MaxValue
			   && requestId == previousRequestId + 1;

		internal static bool IsFinitePoint(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value)
			   && value >= 0f && value <= MaxResearchPoints;

		internal static void WriteId(
			BinaryWriter writer, string value, int maximum, bool allowEmpty = false)
		{
			value ??= string.Empty;
			if ((!allowEmpty && value.Length == 0) || value.Length > maximum)
				throw new InvalidDataException("Invalid research identifier");
			byte[] bytes = StrictUtf8.GetBytes(value);
			if (bytes.Length > maximum * 4)
				throw new InvalidDataException("Research identifier exceeds byte limit");
			writer.Write(bytes.Length);
			writer.Write(bytes);
		}

		internal static string ReadId(
			BinaryReader reader, int maximum, bool allowEmpty = false)
		{
			int byteCount = reader.ReadInt32();
			if (byteCount < 0 || byteCount > maximum * 4)
				throw new InvalidDataException("Invalid research identifier byte length");
			byte[] bytes = reader.ReadBytes(byteCount);
			if (bytes.Length != byteCount)
				throw new EndOfStreamException("Truncated research identifier");
			string value = StrictUtf8.GetString(bytes);
			if ((!allowEmpty && value.Length == 0) || value.Length > maximum)
				throw new InvalidDataException("Invalid research identifier");
			return value;
		}

		internal static int ReadCount(BinaryReader reader, int maximum, string field)
		{
			int count = reader.ReadInt32();
			if (count < 0 || count > maximum)
				throw new InvalidDataException($"Invalid research {field} count: {count}");
			return count;
		}

		internal static bool HasUniqueIds(IEnumerable<string> ids)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (string id in ids)
				if (string.IsNullOrEmpty(id) || id.Length > MaxTechIdLength || !seen.Add(id))
					return false;
			return true;
		}
	}

	public sealed class ResearchPointData
	{
		public string ResearchTypeId = string.Empty;
		public float Points;

		internal void Serialize(BinaryWriter writer)
		{
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research point entry");
			ResearchSyncProtocol.WriteId(
				writer, ResearchTypeId, ResearchSyncProtocol.MaxResearchTypeIdLength);
			writer.Write(Points);
		}

		internal static ResearchPointData Deserialize(BinaryReader reader)
		{
			var entry = new ResearchPointData
			{
				ResearchTypeId = ResearchSyncProtocol.ReadId(
					reader, ResearchSyncProtocol.MaxResearchTypeIdLength),
				Points = reader.ReadSingle()
			};
			if (!entry.IsWireValid())
				throw new InvalidDataException("Invalid research point entry");
			return entry;
		}

		internal bool IsWireValid()
			=> !string.IsNullOrEmpty(ResearchTypeId)
			   && ResearchTypeId.Length <= ResearchSyncProtocol.MaxResearchTypeIdLength
			   && ResearchSyncProtocol.IsFinitePoint(Points);
	}

	public sealed class ResearchProgressData
	{
		public string TechId = string.Empty;
		public List<ResearchPointData> Points = new();

		internal void Serialize(BinaryWriter writer)
		{
			if (!IsWireValid())
				throw new InvalidDataException("Invalid research progress entry");
			ResearchSyncProtocol.WriteId(writer, TechId, ResearchSyncProtocol.MaxTechIdLength);
			writer.Write(Points.Count);
			foreach (ResearchPointData point in Points)
				point.Serialize(writer);
		}

		internal static ResearchProgressData Deserialize(BinaryReader reader)
		{
			var entry = new ResearchProgressData
			{
				TechId = ResearchSyncProtocol.ReadId(reader, ResearchSyncProtocol.MaxTechIdLength)
			};
			int count = ResearchSyncProtocol.ReadCount(
				reader, ResearchSyncProtocol.MaxResearchTypesPerTech, "point");
			entry.Points = new List<ResearchPointData>(count);
			for (int i = 0; i < count; i++)
				entry.Points.Add(ResearchPointData.Deserialize(reader));
			if (!entry.IsWireValid())
				throw new InvalidDataException("Invalid research progress entry");
			return entry;
		}

		internal bool IsWireValid()
		{
			if (string.IsNullOrEmpty(TechId) || TechId.Length > ResearchSyncProtocol.MaxTechIdLength
			    || Points == null || Points.Count > ResearchSyncProtocol.MaxResearchTypesPerTech)
				return false;
			var types = new HashSet<string>(StringComparer.Ordinal);
			foreach (ResearchPointData point in Points)
				if (point == null || !point.IsWireValid() || !types.Add(point.ResearchTypeId))
					return false;
			return true;
		}
	}
}
