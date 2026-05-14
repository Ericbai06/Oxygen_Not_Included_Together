using HarmonyLib;
using ONI_MP.DebugTools;
using ONI_MP.Networking;
using ONI_MP.Networking.Components;
using ONI_MP.Networking.Components.StructureStateSyncers;
using Shared.Profiling;

namespace ONI_MP.Patches.World
{
    [HarmonyPatch(typeof(Battery), "OnSpawn")]
    public static class BatterySpawnPatch
    {
        public static void Postfix(Battery __instance)
        {
            using var _ = Profiler.Scope();

            BatteryStateSyncer syncer = __instance.gameObject.AddOrGet<BatteryStateSyncer>();
        }
    }

    [HarmonyPatch(typeof(Generator), "OnSpawn")]
    public static class GeneratorSpawnPatch
    {
        public static void Postfix(Generator __instance)
        {
            using var _ = Profiler.Scope();

            if (__instance.gameObject.TryGetComponent<EnergyGenerator>(out var egen))
            {
                EnergyGeneratorSyncer egenSyncer = __instance.gameObject.AddOrGet<EnergyGeneratorSyncer>();
                return;
            }

            GenericGeneratorSyncer syncer = __instance.gameObject.AddOrGet<GenericGeneratorSyncer>();
        }
    }

    public static class StorageBuildingPatches
    {
        [HarmonyPatch(typeof(StorageLocker), nameof(StorageLocker.OnSpawn))]
        public static class StorageLocker_OnSpawn_Patch
        {
            public static void Postfix(StorageLocker __instance)
            {
                using var _ = Profiler.Scope();
                StorageStateSyncer syncer = __instance.gameObject.AddOrGet<StorageStateSyncer>();
            }
        }
    }

    /* Not scalable, patch buildings that we want storage syncing on
    [HarmonyPatch(typeof(Storage), nameof(Storage.OnSpawn))]
    public static class StorageLocker_OnSpawn_Patch
    {
        public static void Postfix(Storage __instance)
        {
            using var _ = Profiler.Scope();
            StorageStateSyncer syncer = __instance.gameObject.AddOrGet<StorageStateSyncer>();
        }
    }

    }
    */
}
