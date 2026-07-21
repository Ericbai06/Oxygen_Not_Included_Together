using HarmonyLib;
using System.Collections;
using System.Collections.Generic;

namespace ONI_Together.Networking.Packets.World
{
	internal static partial class ResearchSyncCoordinator
	{
		private static bool TryCaptureState(long revision, out ResearchStatePacket packet)
		{
			packet = null;
			Research research = Research.Instance;
			if (research == null || Db.Get()?.Techs?.resources == null)
				return false;
			var captured = new ResearchStatePacket
			{
				ResearchRevision = revision,
				ActiveTechId = research.GetActiveResearch()?.tech?.Id ?? string.Empty
			};
			if (!TryCaptureQueue(research, captured.QueuedTechIds))
				return false;
			foreach (Tech tech in Db.Get().Techs.resources)
			{
				TechInstance instance = research.Get(tech);
				if (instance == null)
					continue;
				if (instance.IsComplete())
					captured.UnlockedTechIds.Add(tech.Id);
				else
				{
					if (!TryCaptureProgress(instance, out ResearchProgressData progress))
						return false;
					captured.ProgressEntries.Add(progress);
				}
			}
			if (!captured.IsWireValid())
				return false;
			packet = captured;
			return true;
		}

		private static bool TryCaptureQueue(Research research, List<string> queuedIds)
		{
			var field = AccessTools.Field(typeof(Research), "queuedTech");
			if (field?.GetValue(research) is not IList queue)
				return false;
			foreach (object item in queue)
			{
				if (item is not TechInstance instance || instance.tech == null)
					return false;
				queuedIds.Add(instance.tech.Id);
			}
			return true;
		}

		private static bool TryCaptureProgress(
			TechInstance instance, out ResearchProgressData progress)
		{
			progress = null;
			if (instance?.tech == null || instance.progressInventory?.PointsByTypeID == null)
				return false;
			var captured = new ResearchProgressData { TechId = instance.tech.Id };
			foreach (string typeId in instance.tech.costsByResearchTypeID.Keys)
			{
				if (!instance.progressInventory.PointsByTypeID.TryGetValue(typeId, out float points)
				    || points <= 0f)
					continue;
				if (!ResearchSyncProtocol.IsFinitePoint(points))
					return false;
				captured.Points.Add(new ResearchPointData
				{
					ResearchTypeId = typeId,
					Points = points
				});
			}
			progress = captured;
			return true;
		}

		private static bool TryGetActiveProgress(
			ResearchStatePacket state, out ResearchProgressData progress)
		{
			progress = null;
			if (string.IsNullOrEmpty(state.ActiveTechId))
				return false;
			foreach (ResearchProgressData entry in state.ProgressEntries)
			{
				if (entry.TechId == state.ActiveTechId)
				{
					progress = entry;
					return true;
				}
			}
			progress = new ResearchProgressData { TechId = state.ActiveTechId };
			return true;
		}

		private static bool ContentEquals(ResearchStatePacket left, ResearchStatePacket right)
			=> MetadataEquals(left, right) && ProgressEquals(left.ProgressEntries, right.ProgressEntries);

		private static bool MetadataEquals(ResearchStatePacket left, ResearchStatePacket right)
		{
			if (left == null || right == null || left.ActiveTechId != right.ActiveTechId)
				return false;
			return StringListsEqual(left.UnlockedTechIds, right.UnlockedTechIds)
			       && StringListsEqual(left.QueuedTechIds, right.QueuedTechIds);
		}

		private static bool StringListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
		{
			if (left.Count != right.Count)
				return false;
			for (int i = 0; i < left.Count; i++)
				if (left[i] != right[i])
					return false;
			return true;
		}

		private static bool ProgressEquals(
			IReadOnlyList<ResearchProgressData> left,
			IReadOnlyList<ResearchProgressData> right)
		{
			if (left.Count != right.Count)
				return false;
			for (int i = 0; i < left.Count; i++)
				if (!ProgressEntryEquals(left[i], right[i]))
					return false;
			return true;
		}

		private static bool ProgressEntryEquals(ResearchProgressData left, ResearchProgressData right)
		{
			if (left.TechId != right.TechId || left.Points.Count != right.Points.Count)
				return false;
			for (int i = 0; i < left.Points.Count; i++)
			{
				ResearchPointData a = left.Points[i];
				ResearchPointData b = right.Points[i];
				if (a.ResearchTypeId != b.ResearchTypeId || a.Points != b.Points)
					return false;
			}
			return true;
		}

		private static bool OnlyActiveProgressChanged(
			ResearchStatePacket left, ResearchStatePacket right, string activeTechId)
		{
			if (left == null || right == null || string.IsNullOrEmpty(activeTechId))
				return false;
			var oldEntries = WithoutTech(left.ProgressEntries, activeTechId);
			var newEntries = WithoutTech(right.ProgressEntries, activeTechId);
			return ProgressEquals(oldEntries, newEntries);
		}

		private static List<ResearchProgressData> WithoutTech(
			IEnumerable<ResearchProgressData> source, string excludedTechId)
		{
			var result = new List<ResearchProgressData>();
			foreach (ResearchProgressData entry in source)
				if (entry.TechId != excludedTechId)
					result.Add(entry);
			return result;
		}
	}
}
