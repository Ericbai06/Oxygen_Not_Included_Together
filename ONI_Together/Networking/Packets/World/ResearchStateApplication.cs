using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections;
using System.Collections.Generic;

namespace ONI_Together.Networking.Packets.World
{
	internal static partial class ResearchSyncCoordinator
	{
		private sealed class ResolvedState
		{
			internal readonly List<TechInstance> Unlocked = new();
			internal readonly List<TechInstance> Queued = new();
			internal readonly List<ResolvedProgress> Progress = new();
			internal Tech Active;
			internal IList Queue;
		}

		private sealed class ResolvedProgress
		{
			internal TechInstance Instance;
			internal ResearchProgressData Data;
		}

		internal static void ApplyState(ResearchStatePacket packet)
		{
			if (!CanApply(packet.ResearchRevision)
			    || !TryResolveState(packet, out ResolvedState resolved))
				return;
			_applyingAuthoritativeState = true;
			try
			{
				ApplyUnlocked(resolved.Unlocked);
				foreach (ResolvedProgress progress in resolved.Progress)
					ApplyProgressData(progress.Instance, progress.Data);
				ApplySelection(resolved);
				CommitApplied(packet.ResearchRevision);
				RefreshResearchScreen();
#if DEBUG
				LogAppliedResearchEvidence(packet.ResearchRevision, packet);
#endif
			}
			catch (Exception exception)
			{
				DebugConsole.LogError($"[ResearchState] Failed authoritative apply: {exception}");
			}
			finally
			{
				_applyingAuthoritativeState = false;
			}
		}

		internal static void ApplyProgress(ResearchProgressPacket packet)
		{
			if (!CanApply(packet.ResearchRevision)
			    || !TryResolveProgress(packet.Progress, out ResolvedProgress resolved)
			    || resolved.Instance.IsComplete())
				return;
			_applyingAuthoritativeState = true;
			try
			{
				ApplyProgressData(resolved.Instance, resolved.Data);
				CommitApplied(packet.ResearchRevision);
				RefreshResearchScreen();
#if DEBUG
				LogAppliedResearchEvidence(packet.ResearchRevision, packet);
#endif
			}
			finally
			{
				_applyingAuthoritativeState = false;
			}
		}

		internal static void ApplyCompletion(ResearchCompletePacket packet)
		{
			if (!CanApply(packet.ResearchRevision))
				return;
			Tech tech = Db.Get().Techs.TryGet(packet.TechId);
			TechInstance instance = tech == null ? null : Research.Instance.Get(tech);
			if (instance == null)
				return;
			_applyingAuthoritativeState = true;
			try
			{
				if (!instance.IsComplete())
					instance.Purchased();
				if (Research.Instance.GetActiveResearch()?.tech?.Id == packet.TechId)
					Research.Instance.SetActiveResearch(null, true);
				CommitApplied(packet.ResearchRevision);
				RefreshResearchScreen();
#if DEBUG
				LogAppliedResearchEvidence(packet.ResearchRevision, packet);
#endif
			}
			finally
			{
				_applyingAuthoritativeState = false;
			}
		}

		private static bool CanApply(long revision)
		{
			DispatchContext context = PacketHandler.CurrentContext;
			return MultiplayerSession.IsClient && context.SenderIsHost
			       && PacketHandler.IsCurrentDispatchContext(context)
			       && TrackCurrentResearch()
			       && ResearchSyncProtocol.ShouldApply(revision, _appliedRevision);
		}

		private static void CommitApplied(long revision)
		{
			_appliedRevision = revision;
		}

#if DEBUG
		private static void LogAppliedResearchEvidence(long revision, IPacket packet)
		{
			LogResearchEvidence("revision-accepted", revision, true, packet);
			LogResearchEvidence("client-apply", revision, true, packet);
			LogResearchEvidence("final-state", revision, true, packet);
			if (!ResearchSyncProtocol.ShouldApply(revision, revision))
				LogResearchEvidence("revision-duplicate", revision, false, packet);
			long older = revision - 1;
			if (!ResearchSyncProtocol.ShouldApply(older, revision))
				LogResearchEvidence("revision-out-of-order", older, false, packet);
		}
#endif

		private static bool TryResolveState(
			ResearchStatePacket packet, out ResolvedState resolved)
		{
			resolved = new ResolvedState();
			if (!HasCompleteTechCoverage(packet)
			    || !TryResolveTechList(packet.UnlockedTechIds, resolved.Unlocked)
			    || !TryResolveTechList(packet.QueuedTechIds, resolved.Queued))
				return false;
			foreach (TechInstance queued in resolved.Queued)
				if (queued.IsComplete())
					return false;
			if (!string.IsNullOrEmpty(packet.ActiveTechId))
			{
				resolved.Active = Db.Get().Techs.TryGet(packet.ActiveTechId);
				TechInstance active = resolved.Active == null
					? null
					: Research.Instance.Get(resolved.Active);
				if (active == null || active.IsComplete())
					return false;
			}
			foreach (ResearchProgressData entry in packet.ProgressEntries)
			{
				if (!TryResolveProgress(entry, out ResolvedProgress progress)
				    || progress.Instance.IsComplete())
					return false;
				resolved.Progress.Add(progress);
			}
			var field = AccessTools.Field(typeof(Research), "queuedTech");
			resolved.Queue = field?.GetValue(Research.Instance) as IList;
			return resolved.Queue != null;
		}

		private static bool HasCompleteTechCoverage(ResearchStatePacket packet)
		{
			var covered = new HashSet<string>(packet.UnlockedTechIds, StringComparer.Ordinal);
			foreach (ResearchProgressData progress in packet.ProgressEntries)
				covered.Add(progress.TechId);
			int expected = 0;
			foreach (Tech tech in Db.Get().Techs.resources)
			{
				if (Research.Instance.Get(tech) == null)
					continue;
				expected++;
				if (!covered.Contains(tech.Id))
					return false;
			}
			return covered.Count == expected;
		}

		private static bool TryResolveTechList(
			IEnumerable<string> ids, ICollection<TechInstance> destination)
		{
			foreach (string id in ids)
			{
				Tech tech = Db.Get().Techs.TryGet(id);
				TechInstance instance = tech == null ? null : Research.Instance.Get(tech);
				if (instance == null)
					return false;
				destination.Add(instance);
			}
			return true;
		}

		private static bool TryResolveProgress(
			ResearchProgressData data, out ResolvedProgress resolved)
		{
			resolved = null;
			Tech tech = Db.Get().Techs.TryGet(data.TechId);
			TechInstance instance = tech == null ? null : Research.Instance.Get(tech);
			if (instance?.progressInventory?.PointsByTypeID == null)
				return false;
			foreach (ResearchPointData point in data.Points)
				if (!tech.costsByResearchTypeID.ContainsKey(point.ResearchTypeId))
					return false;
			resolved = new ResolvedProgress { Instance = instance, Data = data };
			return true;
		}

		private static void ApplyUnlocked(IEnumerable<TechInstance> unlocked)
		{
			foreach (TechInstance instance in unlocked)
				if (!instance.IsComplete())
					instance.Purchased();
		}

		private static void ApplyProgressData(
			TechInstance instance, ResearchProgressData data)
		{
			foreach (string typeId in instance.tech.costsByResearchTypeID.Keys)
				instance.progressInventory.PointsByTypeID[typeId] = 0f;
			foreach (ResearchPointData point in data.Points)
				instance.progressInventory.PointsByTypeID[point.ResearchTypeId] = point.Points;
		}

		private static void ApplySelection(ResolvedState resolved)
		{
			Research.Instance.SetActiveResearch(resolved.Active, true);
			resolved.Queue.Clear();
			foreach (TechInstance instance in resolved.Queued)
				resolved.Queue.Add(instance);
		}

		private static void RefreshResearchScreen()
		{
			object screen = ManagementMenu.Instance?.researchScreen;
			if (screen == null)
				return;
			Traverse.Create(screen).Method("OnActiveResearchChanged", new[] { typeof(object) })
				.GetValue(null);
		}
	}
}
