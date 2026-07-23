using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.Networking.Packets.World
{
	public partial class SpawnPrefabPacket
	{
		private static readonly System.Func<SpawnPrefabPacket, bool>
			ScenarioProfileMaterializer = packet => packet.TryApplyScenarioSnapshot();

		internal bool ApplyScenarioProfile()
		{
			DispatchContext context = PacketHandler.CurrentContext;
			ulong lastRevision = NetworkIdentityRegistry.GetLastLifecycleRevision(NetId);
			bool entityExists = NetworkIdentityRegistry.Exists(NetId);
			if (!PacketHandler.IsCurrentDispatchContext(context)
			    || !ShouldApply(MultiplayerSession.IsHost, context.SenderIsHost,
				    entityExists, lastRevision, Revision,
				    NetworkIdentityRegistry.IsLifecycleTombstoned(NetId)))
				return false;
			return ScenarioProfileMaterializer(this);
		}

		private bool TryApplyScenarioSnapshot()
		{
			GroundItemPickedUpPacket.ReleaseForNewLifecycle(NetId, Revision);
			return TryApplySnapshot();
		}
	}
}
