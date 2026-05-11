using System;
using System.Collections.Generic;
using System.Text;
using ONI_MP.DebugTools;
using UnityEngine;

namespace ONI_MP.Networking
{
    public class PlayerBuildingVisualizer
    {
        public enum VisualizerType
        {
            BUILDING,
            UTILITY,
            TILE
        }

        private GameObject visualiser;
        private string lastPrefabId = string.Empty;
        private Color color = Color.white;
        private VisualizerType visualizerType = VisualizerType.BUILDING; // TODO Implement this

        private Color visualColor 
        { 
            get
            {
                return Color.Lerp(color, Color.white, 0.75f);
            }
        }
        private Color darkerColor
        {
            get
            {
                return Color.Lerp(color, Color.black, 0.75f);
            }
        }

        BuildingDef CurrentDef;

        private int _cell = Grid.InvalidCell;
        public int Cell
        {
            set
            {
                if (_cell == value)
                    return;

                if (visualiser != null && CurrentDef != null)
                {
                    visualiser.transform.position = Grid.CellToPosCBC(value, CurrentDef.SceneLayer);
                    if (visualiser.TryGetComponent<KBatchedAnimController>(out var kbac))
                    {
                        kbac.TintColour = visualColor; // Force all to white right now
                    }
                }

                OnCellChanged?.Invoke(value);
                _cell = value;
            }
            get
            {
                return _cell;
            }
        }

        public System.Action<int> OnCellChanged; // Leave this incase we want to do something with it later

        public void UpdateVisualizer(VisualizerType type, string buildingPrefabId, Vector3 position, Orientation orientation, Color visualColor)
        {
            this.color = visualColor;

            if (lastPrefabId.Equals(buildingPrefabId) && !visualiser.IsNullOrDestroyed())
            {
                UpdateCell(position); // Instead of updating the visualizer object update its position
                return;
            }

            // Destroy the visualiser if nothing is selected
            if (string.IsNullOrEmpty(buildingPrefabId) || !lastPrefabId.Equals(buildingPrefabId))
            {
                if (!visualiser.IsNullOrDestroyed())
                {
                    Util.KDestroyGameObject(visualiser); // Destroy the visualiser
                    visualiser = null;
                }
            }

            BuildingDef def = Assets.GetBuildingDef(buildingPrefabId);
            if (def == CurrentDef) // Same def somehow leaked through
                return;

            if (def != null)
            {
                CurrentDef = def;
                lastPrefabId = buildingPrefabId;

                int posCell = Grid.PosToCell(position);
                Vector3 pos = Grid.CellToPosCBC(posCell, def.SceneLayer);
                visualiser = GameUtil.KInstantiate(def.BuildingPreview, pos, Grid.SceneLayer.Front, "OtherPlayerBuildingVisualizer", LayerMask.NameToLayer("Place"));
                visualiser.transform.SetPosition(pos);
                visualiser.SetActive(true);

                if(visualiser.TryGetComponent<Rotatable>(out var rotatable))
                {
                    rotatable.SetOrientation(orientation);
                }

                if (visualiser.TryGetComponent<KBatchedAnimController>(out var kbac))
                {
                    kbac.visibilityType = KAnimControllerBase.VisibilityType.Always;
                    kbac.isMovable = true;
                    kbac.Offset = Vector3.zero;
                    kbac.TintColour = visualColor; // White by default

                    kbac.SetLayer(LayerMask.NameToLayer("Place"));
                    kbac.Play("place");
                } 
                else
                {
                    visualiser.SetLayerRecursively(LayerMask.NameToLayer("Place"));
                }
            }
        }

        public void UpdateCell(Vector3 position)
        {
            int cell = Grid.PosToCell(position);
            if (cell != Grid.InvalidCell)
            {
                Cell = cell;
            }
        }
    }
}
