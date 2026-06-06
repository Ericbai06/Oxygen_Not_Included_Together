using ONI_Together.Networking.Packets.Chores;
using Shared.Profiling;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public class ClientReceiver_ChoreErrands : KMonoBehaviour
	{
		[MyCmpGet] private NetworkIdentity identity;

		public ErrandEntry? Current { get; private set; }
		public List<ErrandEntry> Upcoming { get; private set; } = new();

		public float LastApplyTime { get; private set; }
		public float SecondsUntilRefresh => Mathf.Max(0, 0.2f - (Time.time - LastApplyTime)); // 0.2 because they broadcast every 200ms
		
		public void Apply(List<ErrandEntry> entries)
		{
			using var _ = Profiler.Scope();
			LastApplyTime = Time.time;
			
			Current = null;
			if (entries == null)
			{
				Upcoming = new List<ErrandEntry>();
				return;
			}
			var upcoming = new List<ErrandEntry>(entries.Count);
			for (int i = 0; i < entries.Count; i++)
			{
				var entry = entries[i];
				if (entry.IsCurrent && !Current.HasValue)
					Current = entry;
				else
					upcoming.Add(entry);
			}
			Upcoming = upcoming;
		}
	}
}
