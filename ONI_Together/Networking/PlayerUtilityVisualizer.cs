using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Networking
{
	public class PlayerUtilityVisualizer
	{
		private Dictionary<int, GameObject> _visualizers = new Dictionary<int, GameObject>();
		private string _currentPrefabId = string.Empty;
		private BuildingDef _currentDef;

		private Color _color = Color.white;
		public Color Color
		{
			get => _color;
			set => _color = value;
		}

		public void UpdatePath(string prefabId, int[] cells, bool[] validity, Color color)
		{
			_color = color;

			if (string.IsNullOrEmpty(prefabId) || cells == null || cells.Length == 0)
			{
				ClearPath();
				return;
			}

			if (prefabId != _currentPrefabId)
			{
				ClearPath();
				_currentPrefabId = prefabId;
				_currentDef = Assets.GetBuildingDef(prefabId);
				if (_currentDef == null)
				{
					_currentPrefabId = string.Empty;
					return;
				}
			}

			HashSet<int> currentCells = new HashSet<int>(cells);

			List<int> toRemove = new List<int>();
			foreach (int cell in _visualizers.Keys)
			{
				if (!currentCells.Contains(cell))
					toRemove.Add(cell);
			}
			foreach (int cell in toRemove)
				DestroySingleVisualizer(cell);

			for (int i = 0; i < cells.Length; i++)
			{
				int cell = cells[i];
				bool valid = validity != null && i < validity.Length ? validity[i] : true;

				if (!_visualizers.ContainsKey(cell))
					CreateSingleVisualizer(cell);

				UpdateSingleVisualizer(cell, i, cells, valid);
			}
		}

		public void ClearPath()
		{
			foreach (var kvp in _visualizers)
				Util.KDestroyGameObject(kvp.Value);

			_visualizers.Clear();
			_currentPrefabId = string.Empty;
			_currentDef = null;
		}

		private void CreateSingleVisualizer(int cell)
		{
			if (_currentDef == null)
				return;

			Vector3 pos = Grid.CellToPosCBC(cell, Grid.SceneLayer.FXFront);

			GameObject go = new GameObject("PlayerUtilityPathVis");
			go.SetActive(false);

			KBatchedAnimController anim = go.AddComponent<KBatchedAnimController>();
			anim.isMovable = true;
			anim.sceneLayer = Grid.SceneLayer.FXFront;
			anim.AnimFiles = _currentDef.AnimFiles;
			anim.defaultAnim = "place";
			anim.visibilityType = KAnimControllerBase.VisibilityType.Always;
			anim.Offset = Vector3.zero;
			anim.SetLayer(LayerMask.NameToLayer("Place"));

			go.transform.SetPosition(pos);
			_visualizers[cell] = go;
		}

		private void UpdateSingleVisualizer(int cell, int index, int[] cells, bool valid)
		{
			if (!_visualizers.TryGetValue(cell, out GameObject go))
				return;

			UtilityConnections connections = (UtilityConnections)0;
			if (index > 0)
				connections |= UtilityConnectionsExtensions.DirectionFromToCell(cell, cells[index - 1]);
			if (index < cells.Length - 1)
				connections |= UtilityConnectionsExtensions.DirectionFromToCell(cell, cells[index + 1]);

			string connStr = GetConnectionsString(connections);
			string animName = connStr + "_place";

			KBatchedAnimController kbac = go.GetComponent<KBatchedAnimController>();
			if (kbac.HasAnimation(animName))
				kbac.Play(animName);
			else
				kbac.Play(connStr);

			Color tintColor = valid
				? Color.Lerp(_color, Color.white, 0.75f)
				: Color.Lerp(_color, Color.red, 0.75f);
			kbac.TintColour = tintColor;

			go.SetActive(true);
		}

		private void DestroySingleVisualizer(int cell)
		{
			if (_visualizers.TryGetValue(cell, out GameObject go))
			{
				Util.KDestroyGameObject(go);
				_visualizers.Remove(cell);
			}
		}

		private static string GetConnectionsString(UtilityConnections connections)
		{
			string text = "";
			if ((connections & UtilityConnections.Left) != (UtilityConnections)0) text += "L";
			if ((connections & UtilityConnections.Right) != (UtilityConnections)0) text += "R";
			if ((connections & UtilityConnections.Up) != (UtilityConnections)0) text += "U";
			if ((connections & UtilityConnections.Down) != (UtilityConnections)0) text += "D";
			if (text == "") text = "None";
			return text;
		}
	}
}
