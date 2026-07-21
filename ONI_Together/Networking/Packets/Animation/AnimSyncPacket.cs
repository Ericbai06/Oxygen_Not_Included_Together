using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System;
using System.IO;

namespace ONI_Together.Networking.Packets.Animation
{
	internal class AnimSyncPacket : IPacket, Shared.Interfaces.Networking.IHostOnlyPacket
	{
		public int NetId;
		public int AnimHash;
		public byte Mode;
		public float Speed;
		public float ElapsedTime;
		public long StartTick;
		public uint DurationTicks;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			if (!IsWireValid()) throw new InvalidDataException("Invalid animation state");
			writer.Write(NetId);
			writer.Write(AnimHash);
			writer.Write(Mode);
			writer.Write(Speed);
			writer.Write(ElapsedTime);
			writer.Write(StartTick);
			writer.Write(DurationTicks);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			AnimHash = reader.ReadInt32();
			Mode = reader.ReadByte();
			Speed = reader.ReadSingle();
			ElapsedTime = reader.ReadSingle();
			StartTick = reader.ReadInt64();
			DurationTicks = reader.ReadUInt32();
			if (!IsWireValid()) throw new InvalidDataException("Invalid animation state");
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;
			ApplySnapshot();
		}

		internal void ApplySnapshot()
		{
			using var _ = Profiler.Scope();

			if (AnimHash == 0)
				return;

			if (!NetworkIdentityRegistry.TryGetComponent<KBatchedAnimController>(NetId, out var kbac))
				return;

			AnimReconciliationHelper.Reconcile(
				kbac,
				new HashedString(AnimHash),
				(KAnim.PlayMode)Mode,
				Speed,
				ProjectElapsed(this, PresentationTickClock.CurrentTick),
				nameof(AnimSyncPacket));

			if (kbac.TryGetComponent<AnimStateSyncer>(out var syncer))
				syncer.MarkSnapshotReceived();
		}

		internal static float ProjectElapsedForTests(AnimSyncPacket state, long currentTick)
			=> ProjectElapsed(state, currentTick);

		internal bool IsWireValid()
			=> NetId != 0 && AnimHash != 0 && StartTick >= 0 && DurationTicks > 0
			   && IsFinite(Speed) && IsFinite(ElapsedTime) && ElapsedTime >= 0f
			   && Enum.IsDefined(typeof(KAnim.PlayMode), (KAnim.PlayMode)Mode);

		private static float ProjectElapsed(AnimSyncPacket state, long currentTick)
		{
			long delta = System.Math.Max(0, currentTick - state.StartTick);
			float elapsed = state.ElapsedTime
			                + delta / (float)PresentationTickClock.TicksPerSecond * state.Speed;
			if (state.DurationTicks == 0)
				return elapsed;
			float duration = state.DurationTicks / (float)PresentationTickClock.TicksPerSecond;
			if ((KAnim.PlayMode)state.Mode == KAnim.PlayMode.Loop)
				return elapsed % duration;
			return System.Math.Min(elapsed, duration);
		}

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}
}
