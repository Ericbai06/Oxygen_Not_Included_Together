using System;
using System.Threading;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	/// <summary>
	/// Centralized sync metrics for debug display.
	/// Each syncer updates its metric after performing a sync operation.
	/// </summary>
	public static class SyncStats
	{
		private enum PresentationTrafficKind
		{
			Other,
			Motion,
			Animation,
			Cursor,
		}

		public sealed class NativeTransportSnapshot
		{
			public long TxCalls { get; internal set; }
			public long TxBytes { get; internal set; }
			public long TxFailures { get; internal set; }
			public long MotionCalls { get; internal set; }
			public long MotionBytes { get; internal set; }
			public long AnimationCalls { get; internal set; }
			public long AnimationBytes { get; internal set; }
			public long CursorCalls { get; internal set; }
			public long CursorBytes { get; internal set; }
		}

		private static long _txCalls;
		private static long _txBytes;
		private static long _txFailures;
		private static long _motionCalls;
		private static long _motionBytes;
		private static long _animationCalls;
		private static long _animationBytes;
		private static long _cursorCalls;
		private static long _cursorBytes;

		public class SyncMetric
		{
			public string Name;
			public float Interval;
			public float LastSyncTime;
			public int LastItemCount;
			public int LastPacketBytes;
			public float LastDurationMs;

			public float TimeRemaining => Mathf.Max(0, Interval - (Time.unscaledTime - LastSyncTime));
		}

		// WorldStateSyncer metrics
		public static SyncMetric Gas = new SyncMetric { Name = "Gas/Liquid", Interval = 1.5f };
		public static SyncMetric Digging = new SyncMetric { Name = "Digging", Interval = 3f };
		public static SyncMetric Chores = new SyncMetric { Name = "Chores", Interval = 3f };
		public static SyncMetric Research = new SyncMetric { Name = "Research", Interval = 3f };
		// Priorities and Disinfect removed - synced via event-driven patches

		// Other syncer metrics
		public static SyncMetric Buildings = new SyncMetric { Name = "Buildings", Interval = 30f };
		public static SyncMetric Structures = new SyncMetric { Name = "Structures", Interval = 0.5f };
		public static SyncMetric VitalStats = new SyncMetric { Name = "VitalStats", Interval = 1f };
		public static SyncMetric Plants = new SyncMetric { Name = "Plants", Interval = 5f };
		// DragTool: bulk flush observability (count = cells batched in last flush, bytes = payload).
		public static SyncMetric DragTool = new SyncMetric { Name = "DragTool", Interval = 0.1f };
		// AnimSync: host-side per-entity visible-path sends (activity-triggered + interval).
		// LastItemCount = recipients in last send; LastPacketBytes = snapshot bytes.
		public static SyncMetric AnimSync = new SyncMetric { Name = "AnimSync", Interval = 5f };
		// AnimResyncRequest: client-side resync-request packets (count = NetIds requested,
		// bytes = packet size, durationMs = current retry interval in ms for easy log read).
		public static SyncMetric AnimResyncRequest = new SyncMetric { Name = "AnimResyncReq", Interval = 5f };

		/// <summary>
		/// Updates a metric after a sync operation.
		/// </summary>
		public static void RecordSync(SyncMetric metric, int itemCount, int packetBytes, float durationMs)
		{
			using var _ = Profiler.Scope();

			metric.LastSyncTime = Time.unscaledTime;
			metric.LastItemCount = itemCount;
			metric.LastPacketBytes = packetBytes;
			metric.LastDurationMs = durationMs;
		}

		public static void RecordNativeSend(string packetType, int bytes, bool success)
		{
			Interlocked.Increment(ref _txCalls);
			PresentationTrafficKind kind = ClassifyPresentationTraffic(packetType);
			IncrementCategoryCalls(kind);
			if (!success)
			{
				Interlocked.Increment(ref _txFailures);
				return;
			}

			int safeBytes = Math.Max(0, bytes);
			Interlocked.Add(ref _txBytes, safeBytes);
			IncrementCategoryBytes(kind, safeBytes);
		}

		public static NativeTransportSnapshot GetNativeTransportSnapshot()
			=> new NativeTransportSnapshot
			{
				TxCalls = Interlocked.Read(ref _txCalls),
				TxBytes = Interlocked.Read(ref _txBytes),
				TxFailures = Interlocked.Read(ref _txFailures),
				MotionCalls = Interlocked.Read(ref _motionCalls),
				MotionBytes = Interlocked.Read(ref _motionBytes),
				AnimationCalls = Interlocked.Read(ref _animationCalls),
				AnimationBytes = Interlocked.Read(ref _animationBytes),
				CursorCalls = Interlocked.Read(ref _cursorCalls),
				CursorBytes = Interlocked.Read(ref _cursorBytes),
			};

		internal static void ResetNativeTransportForTests()
			=> ResetNativeTransport();

		public static void ResetNativeTransport()
		{
			Interlocked.Exchange(ref _txCalls, 0);
			Interlocked.Exchange(ref _txBytes, 0);
			Interlocked.Exchange(ref _txFailures, 0);
			Interlocked.Exchange(ref _motionCalls, 0);
			Interlocked.Exchange(ref _motionBytes, 0);
			Interlocked.Exchange(ref _animationCalls, 0);
			Interlocked.Exchange(ref _animationBytes, 0);
			Interlocked.Exchange(ref _cursorCalls, 0);
			Interlocked.Exchange(ref _cursorBytes, 0);
		}

		private static PresentationTrafficKind ClassifyPresentationTraffic(string packetType)
		{
			if (string.IsNullOrEmpty(packetType))
				return PresentationTrafficKind.Other;
			if (packetType.IndexOf("Motion", StringComparison.Ordinal) >= 0)
				return PresentationTrafficKind.Motion;
			if (packetType.IndexOf("Cursor", StringComparison.Ordinal) >= 0)
				return PresentationTrafficKind.Cursor;
			return packetType.IndexOf("Anim", StringComparison.Ordinal) >= 0
			       || packetType.IndexOf("DuplicantPresentation", StringComparison.Ordinal) >= 0
				? PresentationTrafficKind.Animation
				: PresentationTrafficKind.Other;
		}

		private static void IncrementCategoryCalls(PresentationTrafficKind kind)
		{
			switch (kind)
			{
				case PresentationTrafficKind.Motion:
					Interlocked.Increment(ref _motionCalls);
					break;
				case PresentationTrafficKind.Animation:
					Interlocked.Increment(ref _animationCalls);
					break;
				case PresentationTrafficKind.Cursor:
					Interlocked.Increment(ref _cursorCalls);
					break;
			}
		}

		private static void IncrementCategoryBytes(PresentationTrafficKind kind, int bytes)
		{
			switch (kind)
			{
				case PresentationTrafficKind.Motion:
					Interlocked.Add(ref _motionBytes, bytes);
					break;
				case PresentationTrafficKind.Animation:
					Interlocked.Add(ref _animationBytes, bytes);
					break;
				case PresentationTrafficKind.Cursor:
					Interlocked.Add(ref _cursorBytes, bytes);
					break;
			}
		}

		/// <summary>
		/// All metrics for iteration in debug display.
		/// </summary>
		public static SyncMetric[] AllMetrics => new[]
		{
			Gas, Digging, Chores, Research,
			Buildings, Structures, VitalStats, Plants,
			DragTool,
			AnimSync, AnimResyncRequest
		};
	}
}
