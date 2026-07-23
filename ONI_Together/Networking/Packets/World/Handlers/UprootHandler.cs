using UnityEngine;
using ONI_Together.DebugTools;
using Shared;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World.Handlers
{
	public class UprootHandler : IBuildingConfigHandler
	{
		private static readonly int[] _hashes = new int[]
		{
			NetworkingHash.ForConfigKey("UprootPlant"),
		};

		public int[] SupportedConfigHashes => _hashes;

		public bool TryApplyConfig(GameObject go, BuildingConfigPacket packet)
		{
			using var _ = Profiler.Scope();

			if (packet.ConfigHash == NetworkingHash.ForConfigKey("UprootPlant")
			    && packet.ConfigType == BuildingConfigType.Boolean
			    && (packet.Value == 0f || packet.Value == 1f)
			    && go.TryGetComponent<Uprootable>(out Uprootable uprootable))
			{
				if (packet.Value == 1f)
					uprootable.MarkForUproot();
				else
					uprootable.ForceCancelUproot(null);
				return true;
			}

			return false;
		}
	}
}
