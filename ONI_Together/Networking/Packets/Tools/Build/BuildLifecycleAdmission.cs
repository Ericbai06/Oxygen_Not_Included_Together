namespace ONI_Together.Networking.Packets.Tools.Build
{
	internal static class BuildLifecycleAdmission
	{
		internal static bool CanComplete(
			bool layerMatches, bool hasIdentity, bool hasConstructable,
			bool prefabMatches, bool cellMatches)
			=> layerMatches && hasIdentity && hasConstructable && prefabMatches && cellMatches;
	}
}
