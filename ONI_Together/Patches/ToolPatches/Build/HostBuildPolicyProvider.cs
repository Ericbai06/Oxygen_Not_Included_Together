using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Tools.Build;

namespace ONI_Together.Patches.ToolPatches.Build
{
	internal static class HostBuildPolicyProvider
	{
		internal static HostBuildPolicy Current
		{
			get
			{
				bool sandboxActive = Game.Instance != null && Game.Instance.SandboxModeActive;
				bool sandboxInstant = SandboxToolParameterMenu.instance?.settings?.InstantBuild == true;
				return new HostBuildPolicy(
					DebugHandler.InstantBuildMode || sandboxActive && sandboxInstant);
			}
		}
	}
}
