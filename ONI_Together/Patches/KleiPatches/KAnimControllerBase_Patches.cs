using ONI_Together.DebugTools;
using Shared.Profiling;

namespace ONI_Together.Patches.KleiPatches
{
	internal static class KAnimControllerBase_Patches
	{
		private static int _overridePacketDepth;

		public static void AllowAnims() { }

		public static void ForbidAnims() { }

		internal static bool IsTogglingOverrideFromPacket => _overridePacketDepth > 0;

		internal static void RunWithOverridePacketGuard(System.Action action)
		{
			_overridePacketDepth++;
			try
			{
				action();
			}
			finally
			{
				_overridePacketDepth--;
			}
		}

		internal static void ResetOverridePacketGuardForTests() => _overridePacketDepth = 0;

		internal static void AddKanimOverride(KAnimControllerBase kbac, string kanim, float priority)
		{
			using var _ = Profiler.Scope();

			RunWithOverridePacketGuard(() =>
			{
				if (Assets.TryGetAnim(kanim, out var anim))
					kbac.AddAnimOverrides(anim, priority);
				else
					DebugConsole.LogWarning("could not find anim " + kanim);
			});
		}

		internal static void RemoveKanimOverride(KAnimControllerBase kbac, string kanim)
		{
			using var _ = Profiler.Scope();

			RunWithOverridePacketGuard(() =>
			{
				if (Assets.TryGetAnim(kanim, out var anim))
					kbac.RemoveAnimOverrides(anim);
				else
					DebugConsole.LogWarning("could not find anim " + kanim);
			});
		}
	}
}
