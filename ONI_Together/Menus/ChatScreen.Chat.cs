using System;
using System.Collections.Generic;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Social;
using Shared.Profiling;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Utils = ONI_Together.Misc.Utils;

namespace ONI_Together.UI
{
	public partial class ChatScreen
	{
		public struct PendingMessage
		{
			public ulong sequence;
			public long timestamp;
			public string message;
		}

		public static ChatScreen Instance;
		private static readonly List<PendingMessage> PendingMessages = [];
		private static readonly List<PendingMessage> ChatHistory = [];
		private static readonly HashSet<ulong> PendingHistoryRecipients = [];
		private static ulong nextChatSequence;
		private static ulong lastHistoryCut;
		private static bool historyCutApplied;

		private readonly List<TextMeshProUGUI> messages = [];
		private readonly HashSet<ulong> renderedSequences = [];
		private RectTransform messageContainer;

		public static void Show()
		{
			using var _ = Profiler.Scope();
			if (Instance != null || GameScreenManager.Instance == null)
				return;
			var gameObject = new GameObject("ChatScreen", typeof(RectTransform));
			Instance = gameObject.AddComponent<ChatScreen>();
			gameObject.transform.SetParent(
				GameScreenManager.Instance.ssOverlayCanvas.transform, false);
			RectTransform transform = gameObject.GetComponent<RectTransform>();
			transform.anchorMin = new Vector2(0.5f, 0.5f);
			transform.anchorMax = new Vector2(0.5f, 0.5f);
			transform.pivot = new Vector2(0.5f, 0.5f);
			transform.sizeDelta = Vector2.zero;
			transform.anchoredPosition = Vector2.zero;
			Instance.SetupUI();
		}

		internal static void BufferHistoryForPlayer(ulong playerId)
		{
			if (!MultiplayerSession.IsHost || playerId == 0
			    || playerId == MultiplayerSession.HostUserID)
				return;
			PendingHistoryRecipients.Add(playerId);
			FlushBufferedHistory(playerId);
		}

		internal static void FlushBufferedHistory(ulong playerId)
		{
			if (!PendingHistoryRecipients.Contains(playerId)
			    || !ReliableSyncBacklog.IsBuffering(playerId))
				return;
			PendingHistoryRecipients.Remove(playerId);
			var packet = new ChatHistorySyncPacket(ChatHistory, nextChatSequence);
			if (!PacketSender.SendToPlayer(
				    playerId, packet, PacketSendMode.ReliableImmediate))
				DebugConsole.LogWarning($"[Chatbox] Failed to buffer history for {playerId}");
		}

		internal static void CancelBufferedHistory(ulong playerId)
			=> PendingHistoryRecipients.Remove(playerId);

		internal static ulong NextHostSequence()
		{
			if (!MultiplayerSession.IsHost || nextChatSequence == long.MaxValue)
				throw new InvalidOperationException("Chat sequence authority is unavailable");
			return ++nextChatSequence;
		}

		public static void QueueMessage(PendingMessage message)
		{
			using var _ = Profiler.Scope();
			if (string.IsNullOrEmpty(message.message))
				return;
			if (message.sequence != 0)
			{
				ApplyAuthoritativeLive(message);
				return;
			}
			if (Instance != null)
				Instance.AddMessage(message);
			else
				PendingMessages.Add(message);
		}

		internal static bool ApplyAuthoritativeLive(PendingMessage message)
		{
			if (message.sequence == 0
			    || historyCutApplied && message.sequence <= lastHistoryCut
			    || ChatHistory.Count > 0 && message.sequence <= ChatHistory[^1].sequence)
				return false;
			ChatHistorySyncPacket.AppendBounded(ChatHistory, message);
			ChatHistory.Sort((left, right) => left.sequence.CompareTo(right.sequence));
			Instance?.AddMessage(message);
			return true;
		}

		internal static bool ApplyHistory(
			ulong cutSequence, IReadOnlyList<PendingMessage> snapshot)
		{
			if (!ShouldApplyHistoryCut(historyCutApplied, lastHistoryCut, cutSequence))
				return false;
			List<PendingMessage> merged = snapshot.ToList();
			merged.AddRange(ChatHistory.Where(message => message.sequence > cutSequence));
			merged.Sort((left, right) => left.sequence.CompareTo(right.sequence));
			ChatHistory.Clear();
			foreach (PendingMessage message in merged)
				if (!ChatHistory.Any(existing => existing.sequence == message.sequence))
					ChatHistorySyncPacket.AppendBounded(ChatHistory, message);
			lastHistoryCut = cutSequence;
			historyCutApplied = true;
			Instance?.RenderAuthoritativeHistory();
			return true;
		}

		internal static bool ShouldApplyHistoryCut(
			bool hasCurrentCut, ulong currentCut, ulong incomingCut)
			=> !hasCurrentCut || incomingCut > currentCut;

		public static bool IsFocused()
			=> Instance != null && Instance.inputField != null && Instance.inputField.isFocused;

		private void Update()
		{
			header.SetActive(MultiplayerSession.InSession);
			chatbox.SetActive(MultiplayerSession.InSession && expanded);
			resizeHandles.SetActive(MultiplayerSession.InSession && expanded);
			if (!MultiplayerSession.InSession)
				return;
			if (!inputField.isFocused && Input.GetKeyDown(KeyCode.Return))
				inputField.ActivateInputField();
			else if (inputField.isFocused && Input.GetKeyDown(KeyCode.Escape))
				inputField.DeactivateInputField();
			if (!inputField.isFocused && Input.GetKeyDown(KeyCode.R)
			    && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
				ResetChatWindowState();
		}

		public void ProcessMessageQueue()
		{
			foreach (PendingMessage pending in PendingMessages)
				AddMessage(pending);
			PendingMessages.Clear();
			foreach (PendingMessage message in ChatHistory)
				AddMessage(message);
		}

		public void ClearMessages()
		{
			foreach (TextMeshProUGUI message in messages)
				Destroy(message.gameObject);
			messages.Clear();
			renderedSequences.Clear();
		}

		public void AddMessage(long timestamp, string text)
			=> AddMessage(new PendingMessage { timestamp = timestamp, message = text });

		private void AddMessage(PendingMessage message)
		{
			if (message.sequence != 0 && !renderedSequences.Add(message.sequence))
				return;
			if (messageContainer == null)
			{
				DebugConsole.LogWarning("[Chatbox] messageContainer was null");
				return;
			}
			GameObject gameObject = new("Message", typeof(RectTransform), typeof(TextMeshProUGUI));
			gameObject.transform.SetParent(messageContainer, false);
			RectTransform transform = gameObject.GetComponent<RectTransform>();
			transform.anchorMin = new Vector2(0, 1);
			transform.anchorMax = new Vector2(1, 1);
			transform.pivot = new Vector2(0, 1);
			transform.offsetMin = Vector2.zero;
			transform.offsetMax = Vector2.zero;
			TextMeshProUGUI text = gameObject.GetComponent<TextMeshProUGUI>();
			text.text = message.message;
			text.font = Utils.GetDefaultTMPFont();
			text.fontSize = 18;
			text.textWrappingMode = TextWrappingModes.Normal;
			text.richText = true;
			text.alignment = TextAlignmentOptions.TopLeft;
			text.color = new Color(0.9f, 0.9f, 0.9f, 1f);
			text.margin = new Vector4(6, 2, 6, 2);
			ContentSizeFitter fitter = gameObject.AddComponent<ContentSizeFitter>();
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
			messages.Add(text);
			LayoutRebuilder.ForceRebuildLayoutImmediate(messageContainer);
			ScrollRect scroll = messageContainer.GetComponentInParent<ScrollRect>();
			if (scroll != null)
				scroll.verticalNormalizedPosition = 0f;
		}

		private void RenderAuthoritativeHistory()
		{
			ClearMessages();
			AddMessage(GeneratePendingMessage(STRINGS.UI.MP_CHATWINDOW.CHAT_INITIALIZED));
			foreach (PendingMessage message in ChatHistory)
				AddMessage(message);
		}

		public static PendingMessage GeneratePendingMessage(string message)
			=> new()
			{
				timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				message = message
			};

		public static void ResetSessionState()
		{
			ChatScreen screen = Instance;
			Instance = null;
			if (screen != null)
			{
				screen.ClearMessages();
				Destroy(screen.gameObject);
			}
			PendingMessages.Clear();
			ChatHistory.Clear();
			PendingHistoryRecipients.Clear();
			nextChatSequence = 0;
			lastHistoryCut = 0;
			historyCutApplied = false;
		}

		private new void OnDestroy()
		{
			SaveChatWindowState();
			if (ReferenceEquals(Instance, this))
				Instance = null;
		}

		internal static int HistoryCountForTests => ChatHistory.Count;
		internal static IReadOnlyList<PendingMessage> HistorySnapshotForTests
			=> ChatHistory.ToArray();
		internal static int PendingMessageCountForTests => PendingMessages.Count;
		internal static int PendingHistoryRecipientCountForTests
			=> PendingHistoryRecipients.Count;
		internal static ulong NextChatSequenceForTests => nextChatSequence;
		internal static ulong LastHistoryCutForTests => lastHistoryCut;
		internal static bool HistoryCutAppliedForTests => historyCutApplied;
	}
}
