using HarmonyLib;
using ONI_MP.Networking;
using ONI_MP.Networking.Components;
using Shared.Profiling;

namespace ONI_MP.Patches.World
{
	[HarmonyPatch(typeof(Battery), "OnSpawn")]
	public static class BatterySpawnPatch
	{
		public static void Postfix(Battery __instance)
		{
			using var _ = Profiler.Scope();

			StructureStateSyncer syncer = __instance.gameObject.AddOrGet<StructureStateSyncer>();
			syncer.InitalizeAsStructure(StructureStateSyncer.StructureType.BATTERY);
		}
	}

	[HarmonyPatch(typeof(Generator), "OnSpawn")]
	public static class GeneratorSpawnPatch
	{
		public static void Postfix(Generator __instance)
		{
			using var _ = Profiler.Scope();

            StructureStateSyncer syncer = __instance.gameObject.AddOrGet<StructureStateSyncer>();
            syncer.InitalizeAsStructure(StructureStateSyncer.StructureType.GENERATOR);
        }
    }

    [HarmonyPatch(typeof(Storage), nameof(Storage.OnSpawn))]
    public static class StorageLocker_OnSpawn_Patch
    {
        public static void Postfix(Storage __instance)
        {
            using var _ = Profiler.Scope();
            bool shouldIgnore = __instance.GetComponent<Generator>() || __instance.GetComponent<Battery>();
            if (shouldIgnore)
                return; // Already initalized as something else

            StructureStateSyncer syncer = __instance.gameObject.AddOrGet<StructureStateSyncer>();
            syncer.InitalizeAsStructure(StructureStateSyncer.StructureType.STORAGE_CONTAINER);
        }
    }
}
