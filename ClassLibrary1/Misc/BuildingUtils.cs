using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static LogicGateVisualizer;
using static ONI_MP.Networking.Components.StructureStateSyncers.StorageStateSyncer;

namespace ONI_MP.Misc
{
    public static class BuildingUtils
    {
        public static ObjectLayer GetActualObjectLayer(Building building)
        {
            if (building == null || building.Def == null)
                return ObjectLayer.Building; // Default as building layer

            int baseCell = Grid.PosToCell(building.transform.position);

            // First check intended layer
            ObjectLayer defLayer = building.Def.ObjectLayer;

            if (Grid.Objects[baseCell, (int)defLayer] == building.gameObject)
                return defLayer;

            // Check all occupied cells
            if (building.Def.PlacementOffsets != null)
            {
                foreach (var offset in building.Def.PlacementOffsets)
                {
                    int cell = Grid.OffsetCell(baseCell, offset);

                    for (int i = 0; i < (int)ObjectLayer.NumLayers; i++)
                    {
                        if (Grid.Objects[cell, i] == building.gameObject)
                            return (ObjectLayer)i;
                    }
                }
            }

            // Fallback
            for (int i = 0; i < (int)ObjectLayer.NumLayers; i++)
            {
                if (Grid.Objects[baseCell, i] == building.gameObject)
                    return (ObjectLayer)i;
            }

            // We somehow found nothing
            return defLayer;
        }

        public static void GetLayerInfo(Building building, out ObjectLayer layer, out bool isReplacement)
        {
            layer = GetActualObjectLayer(building);
            isReplacement = IsReplacementLayer(layer);
            layer = NormalizeLayer(layer);
        }

        public static bool IsReplacementLayer(ObjectLayer layer)
        {
            switch (layer)
            {
                case ObjectLayer.ReplacementTile:
                case ObjectLayer.ReplacementGasConduit:
                case ObjectLayer.ReplacementLiquidConduit:
                case ObjectLayer.ReplacementSolidConduit:
                case ObjectLayer.ReplacementWire:
                case ObjectLayer.ReplacementLogicWire:
                case ObjectLayer.ReplacementTravelTube:
                case ObjectLayer.ReplacementLadder:
                case ObjectLayer.ReplacementBackwall:
                    return true;
                default:
                    return false;
            }
        }

        public static ObjectLayer NormalizeLayer(ObjectLayer layer)
        {
            switch (layer)
            {
                case ObjectLayer.ReplacementTile:
                    return ObjectLayer.FoundationTile;

                case ObjectLayer.ReplacementGasConduit:
                    return ObjectLayer.GasConduit;

                case ObjectLayer.ReplacementLiquidConduit:
                    return ObjectLayer.LiquidConduit;

                case ObjectLayer.ReplacementSolidConduit:
                    return ObjectLayer.SolidConduit;

                case ObjectLayer.ReplacementWire:
                    return ObjectLayer.Wire;

                case ObjectLayer.ReplacementLogicWire:
                    return ObjectLayer.LogicWire;

                case ObjectLayer.ReplacementTravelTube:
                    return ObjectLayer.TravelTube;

                case ObjectLayer.ReplacementLadder:
                    return ObjectLayer.LadderTile;

                case ObjectLayer.ReplacementBackwall:
                    return ObjectLayer.Backwall;

                default:
                    return layer;
            }
        }

        public static bool ValidCell(GameObject visualizer, BuildingDef def, int cell, Orientation orientation)
        {
            if (Grid.IsValidCell(cell)
                && Grid.IsVisible(cell))
            {
                bool IsValidPlaceLocation = def.IsValidPlaceLocation(visualizer, cell, orientation, out string failReason);
                bool IgnorableFailReason =
                    failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_WALL
                    || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER
                    || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER_FLOOR
                    || (failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_BACK_WALL_REQUIRED);

                bool validCell = (IsValidPlaceLocation || IgnorableFailReason);
                bool replacement = false;
                return (validCell || replacement);
            }

            return false;
        }

        public static void EncodeStorageContents(Storage storage, out Variant[] optionalValues)
        {
            var items = new List<StorageData>();
            for (int i = 0; i < storage.items.Count; i++)
            {
                if (storage.items[i] == null) continue;
                var pe = storage.items[i].GetComponent<PrimaryElement>();
                if (pe == null || pe.Mass <= 0f) continue;
                if (!storage.items[i].TryGetComponent<KPrefabID>(out var prefabID))
                    continue;

                items.Add(new StorageData
                {
                    PrefabTagHash = prefabID.PrefabTag.GetHashCode(),
                    Mass = pe.Mass,
                    Units = pe.Units,
                    Temperature = pe.Temperature,
                    DiseaseIdx = pe.DiseaseIdx,
                    DiseaseCount = pe.DiseaseCount
                });
            }

            optionalValues = new Variant[2 + items.Count * 6];
            optionalValues[0] = storage.capacityKg;
            optionalValues[1] = items.Count;
            int idx = 2;
            foreach (var item in items)
            {
                optionalValues[idx++] = item.PrefabTagHash;
                optionalValues[idx++] = item.Mass;
                optionalValues[idx++] = item.Units;
                optionalValues[idx++] = item.Temperature;
                optionalValues[idx++] = item.DiseaseIdx;
                optionalValues[idx++] = item.DiseaseCount;
            }
        }

        public static void RebuildStorageFromData(Storage storage, Variant[] data, string diseaseReason = "Multiplayer Sync")
        {
            if (storage == null || data.Length < 2) return;
            ClearStorage(storage);

            int count = data[1].Int;
            if (count == 0) return;

            for (int i = 0; i < count; i++)
            {
                int baseIdx = 2 + i * 6;
                if (baseIdx + 6 > data.Length) break;

                int hash = data[baseIdx].Int;
                float mass = data[baseIdx + 1].Float;
                float temperature = data[baseIdx + 3].Float;
                byte diseaseIdx = data[baseIdx + 4].Byte;
                int diseaseCount = data[baseIdx + 5].Int;
                if (mass <= 0f) continue;

                Tag tag = new Tag(hash);
                Element elementByHash = ElementLoader.GetElement(tag);
                if (elementByHash != null)
                {
                    storage.AddElement(elementByHash.id, mass, temperature, diseaseIdx, diseaseCount);
                }
                else
                {
                    var item = Assets.GetPrefab(tag);
                    if (item == null) continue;

                    var scrapObject = GameUtil.KInstantiate(item, storage.transform.position, Grid.SceneLayer.Ore);
                    if (scrapObject.TryGetComponent<PrimaryElement>(out var pe))
                    {
                        pe.Mass = mass;
                        pe.Temperature = temperature;
                        if (diseaseIdx != byte.MaxValue)
                            pe.AddDisease(diseaseIdx, diseaseCount, diseaseReason);
                    }
                    scrapObject.SetActive(true);
                    storage.Store(scrapObject, true, true);
                }
            }
        }

        private static void ClearStorage(Storage storage)
        {
            for (int i = storage.items.Count - 1; i >= 0; i--)
                storage.items[i].DeleteObject();
            storage.items.Clear();
        }
    }
}
