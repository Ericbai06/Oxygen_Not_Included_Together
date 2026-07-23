using ONI_Together.Networking.Packets.Tools.Build;

namespace ONI_Together.Patches.ToolPatches.Build
{
	internal static class HostBuildPolicyProvider
	{
		internal static HostBuildPolicy Current
		{
			get
			{
				Game game = Game.Instance;
				return new HostBuildPolicy(game != null && game.DebugOnlyBuildingsAllowed);
			}
		}
	}
}
