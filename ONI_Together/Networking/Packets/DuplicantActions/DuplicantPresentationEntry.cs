using System;
using System.IO;
using UnityEngine;

namespace ONI_Together.Networking.Packets.DuplicantActions
{
	internal sealed class DuplicantPresentationEntry
	{
		internal const int WireBytes = sizeof(int) + sizeof(ulong) + sizeof(long)
		                               + sizeof(uint) + sizeof(byte) + sizeof(int)
		                               + sizeof(byte) + sizeof(float) * 2
		                               + sizeof(bool) + sizeof(byte) + sizeof(int) * 2
		                               + sizeof(byte) * 2 + sizeof(bool) + sizeof(float) * 3;

		public int NetId;
		public ulong Revision;
		public long StartSimTick;
		public uint DurationTicks;
		public DuplicantActionState ActionState;
		public int AnimHash;
		public byte PlayMode;
		public float AnimSpeed;
		public float AnimElapsedAtStart;
		public bool IsWorking;
		public DuplicantWorkVisual WorkVisual;
		public int TargetCell;
		public int VisualTargetNetId;
		public DuplicantToolVisual ToolVisual;
		public DuplicantFacing Facing;
		public bool ShowProgress;
		public float ProgressPercent;
		public float WorkTimeRemaining;
		public float WorkTimeTotal;

		internal void Serialize(BinaryWriter writer)
		{
			if (!IsWireValid()) throw new InvalidDataException("Invalid duplicant presentation entry");
			writer.Write(NetId);
			writer.Write(Revision);
			writer.Write(StartSimTick);
			writer.Write(DurationTicks);
			writer.Write((byte)ActionState);
			writer.Write(AnimHash);
			writer.Write(PlayMode);
			writer.Write(AnimSpeed);
			writer.Write(AnimElapsedAtStart);
			writer.Write(IsWorking);
			writer.Write((byte)WorkVisual);
			writer.Write(TargetCell);
			writer.Write(VisualTargetNetId);
			writer.Write((byte)ToolVisual);
			writer.Write((byte)Facing);
			writer.Write(ShowProgress);
			writer.Write(ProgressPercent);
			writer.Write(WorkTimeRemaining);
			writer.Write(WorkTimeTotal);
		}

		internal void Deserialize(BinaryReader reader)
		{
			NetId = reader.ReadInt32();
			Revision = reader.ReadUInt64();
			StartSimTick = reader.ReadInt64();
			DurationTicks = reader.ReadUInt32();
			ActionState = (DuplicantActionState)reader.ReadByte();
			AnimHash = reader.ReadInt32();
			PlayMode = reader.ReadByte();
			AnimSpeed = reader.ReadSingle();
			AnimElapsedAtStart = reader.ReadSingle();
			IsWorking = reader.ReadBoolean();
			WorkVisual = (DuplicantWorkVisual)reader.ReadByte();
			TargetCell = reader.ReadInt32();
			VisualTargetNetId = reader.ReadInt32();
			ToolVisual = (DuplicantToolVisual)reader.ReadByte();
			Facing = (DuplicantFacing)reader.ReadByte();
			ShowProgress = reader.ReadBoolean();
			ProgressPercent = reader.ReadSingle();
			WorkTimeRemaining = reader.ReadSingle();
			WorkTimeTotal = reader.ReadSingle();
			if (!IsWireValid()) throw new InvalidDataException("Invalid duplicant presentation entry");
		}

		internal bool HasSameVisualState(DuplicantPresentationEntry other)
			=> other != null && HasSameAnimation(other)
			   && HasSameWorkVisual(other) && HasSameProgress(other);

		private bool HasSameAnimation(DuplicantPresentationEntry other)
			=> ActionState == other.ActionState && AnimHash == other.AnimHash
			   && PlayMode == other.PlayMode && AnimSpeed == other.AnimSpeed
			   && DurationTicks == other.DurationTicks;

		private bool HasSameWorkVisual(DuplicantPresentationEntry other)
			=> IsWorking == other.IsWorking && WorkVisual == other.WorkVisual
			   && TargetCell == other.TargetCell
			   && VisualTargetNetId == other.VisualTargetNetId
			   && ToolVisual == other.ToolVisual && Facing == other.Facing;

		private bool HasSameProgress(DuplicantPresentationEntry other)
			=> ShowProgress == other.ShowProgress
			   && ProgressPercent == other.ProgressPercent
			   && WorkTimeRemaining == other.WorkTimeRemaining
			   && WorkTimeTotal == other.WorkTimeTotal;

		private bool IsWireValid()
		{
			if (!HasValidIdentity() || !HasValidEnums() || !HasValidNumbers())
				return false;
			if (!ShowProgress)
				return ProgressPercent == 0f && WorkTimeRemaining == 0f && WorkTimeTotal == 0f;
			return HasValidProgress();
		}

		private bool HasValidIdentity()
			=> NetId != 0 && Revision != 0 && StartSimTick >= 0 && DurationTicks != 0;

		private bool HasValidEnums()
			=> Enum.IsDefined(typeof(DuplicantActionState), ActionState)
			   && Enum.IsDefined(typeof(KAnim.PlayMode), (int)PlayMode)
			   && Enum.IsDefined(typeof(DuplicantWorkVisual), WorkVisual)
			   && Enum.IsDefined(typeof(DuplicantToolVisual), ToolVisual)
			   && Enum.IsDefined(typeof(DuplicantFacing), Facing);

		private bool HasValidNumbers()
			=> IsFinite(AnimSpeed) && IsFinite(AnimElapsedAtStart)
			   && AnimElapsedAtStart >= 0f && IsFinite(ProgressPercent)
			   && IsFinite(WorkTimeRemaining) && IsFinite(WorkTimeTotal);

		private bool HasValidProgress()
			=> VisualTargetNetId != 0 && ProgressPercent is >= 0f and <= 1f
			       && WorkTimeRemaining >= 0f && WorkTimeTotal > 0f
			       && WorkTimeRemaining <= WorkTimeTotal;

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}

	public enum DuplicantActionState : byte
	{
		Idle, Walking, Working, Building, Digging, Eating, Sleeping, Using,
		Carrying, Climbing, Swimming, Falling, Disinfecting, Other
	}

	internal enum DuplicantWorkVisual : byte
	{
		None, Working, Building, Digging, Disinfecting
	}

	internal enum DuplicantToolVisual : byte
	{
		None, Build, Dig, Disinfect
	}

	internal enum DuplicantFacing : byte
	{
		Unspecified, Left, Right
	}
}
