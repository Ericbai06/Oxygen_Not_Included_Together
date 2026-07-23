namespace ONI_Together.Networking.Packets.Tools.Build
{
	internal static class BuildLifecycleAdmission
	{
		internal static bool IsAlreadyAppliedLifecycle(
			ulong current, ulong incoming, bool completedPrefabMatches, bool cellMatches)
			=> incoming != 0 && current == incoming && completedPrefabMatches && cellMatches;

		internal static bool CanComplete(
			bool layerMatches, bool hasIdentity, bool hasConstructable,
			bool prefabMatches, bool cellMatches)
			=> layerMatches && hasIdentity && hasConstructable && prefabMatches && cellMatches;
	}
}
