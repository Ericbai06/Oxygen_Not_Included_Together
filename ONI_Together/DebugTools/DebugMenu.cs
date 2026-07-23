using ONI_Together.Misc;
using ONI_Together.Networking;
using ONI_Together.Networking.States;
using ONI_Together.Patches.ToolPatches;
using System;
using Shared.Profiling;
using UnityEngine;
#if DEBUG
using ONI_Together.Tests;
#endif

namespace ONI_Together.DebugTools
{
#if DEBUG
	internal readonly struct DebugCommandOutcome
	{
		internal string Command { get; }
		internal bool Success { get; }
		internal string Reason { get; }
		private string Stage { get; }
		private DeferredDestroyResetEvidence CleanupEvidence { get; }
		private FaultInjectionReceipt FaultReceipt { get; }

		private DebugCommandOutcome(
			string command, bool success, string reason, string stage = null,
			DeferredDestroyResetEvidence cleanupEvidence = null,
			FaultInjectionReceipt faultReceipt = null)
		{
			Command = command;
			Success = success;
			Reason = reason;
			Stage = stage;
			CleanupEvidence = cleanupEvidence;
			FaultReceipt = faultReceipt;
		}

		internal static DebugCommandOutcome Ok(string command, string reason)
			=> new DebugCommandOutcome(command, true, reason);

		internal static DebugCommandOutcome Fail(string command, string reason)
			=> new DebugCommandOutcome(command, false, reason);

		internal static DebugCommandOutcome FromFaultReceipt(
			string command, FaultInjectionReceipt receipt)
			=> new DebugCommandOutcome(
				command, receipt.Succeeded, receipt.Detail, receipt.Stage,
				receipt.CleanupEvidence, receipt);

		internal string ToLogLine()
		{
			string prefix = $"[DebugCommand][{(Success ? "OK" : "FAIL")}] command={Command}";
			string reason = Reason.Replace('\r', ' ').Replace('\n', ' ');
			if (Stage == null) return prefix + " reason=" + reason;
			DeferredDestroyResetEvidence cleanup = CleanupEvidence;
			FaultInjectionReceipt receipt = FaultReceipt;
			return prefix
			       + " receiptId=" + receipt.ReceiptId
			       + " caseId=" + receipt.CaseId
			       + " targetId=" + receipt.TargetId
			       + " consumed=" + Lower(receipt.Consumed)
			       + " passed=" + Lower(Success)
			       + " stage=" + Stage
			       + " fixtureDisposeRequested=" + Lower(cleanup?.FixtureDisposeRequested ?? false)
			       + " fixtureDisposeRequestedFrame=" + (cleanup?.FixtureDisposeRequestedFrame ?? 0)
			       + " fixtureDisposeObservedFrame=" + (cleanup?.FixtureDisposeObservedFrame ?? 0)
			       + " fixtureAbsent=" + Lower(cleanup?.FixtureAbsent ?? false)
			       + " reason=" + reason;
		}

		private static string Lower(bool value) => value ? "true" : "false";
	}
#endif

	public partial class DebugMenu : MonoBehaviour
	{
		private static DebugMenu _instance;

#if DEBUG
		private bool showMenu = true;
		private const string AutomationCommandFileName = "oni_together_debug_command.txt";
		private const float AutomationCommandPollInterval = 0.25f;
		private string automationCommandPath;
		private string automationClaimPath;
		private float nextAutomationCommandPollAt;
#else
		private bool showMenu = false;
#endif
#if DEBUG
		// 避开主菜单按钮；Unity IMGUI 与 Klei UI 会同时接收重叠区域的鼠标事件。
		private Rect windowRect = new Rect(520, 10, 360, 520);
#else
		private Rect windowRect = new Rect(10, 10, 360, 520);
#endif
		private HierarchyViewer hierarchyViewer;
		private DebugConsole debugConsole;

		private Vector2 scrollPosition = Vector2.zero;

		// LAN
        private string lanHostIP = "127.0.0.1";
        private string lanHostPort = "7777";
        private string[] hostTransportOptions = new string[]
        {
            "Steam",
            "LAN"
        };
        private int selectedHostTransport = 0;

        private string lanJoinAddress = "127.0.0.1:7777";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		public static void Init()
		{
            using var _ = Profiler.Scope();

			if (_instance != null) return;

			GameObject go = new GameObject("ONI_Together_DebugMenu");
			DontDestroyOnLoad(go);
			_instance = go.AddComponent<DebugMenu>();

            _instance.lanHostIP = Configuration.Instance.Host.LanSettings.Ip;
            _instance.lanHostPort = Configuration.Instance.Host.LanSettings.Port.ToString();

            _instance.lanJoinAddress = $"{Configuration.Instance.Client.LanSettings.Ip}:{Configuration.Instance.Client.LanSettings.Port}";

        }

		private void Awake()
		{
            using var _ = Profiler.Scope();

			hierarchyViewer = gameObject.AddComponent<HierarchyViewer>();
			//debugConsole = gameObject.AddComponent<DebugConsole>();
#if DEBUG
			gameObject.AddComponent<SoakStateHashProbe>();
			automationCommandPath = System.IO.Path.Combine(
				Application.persistentDataPath, AutomationCommandFileName);
			automationClaimPath = automationCommandPath + ".processing";
			DebugConsole.Log($"[DebugCommand] path={automationCommandPath}");
#endif
		}

		private void Update()
		{
            using var _ = Profiler.Scope();

			bool shiftDown = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
			if (Input.GetKeyDown(KeyCode.F2) && shiftDown)
			{
				showMenu = !showMenu;
			}
#if DEBUG
			PollAutomationCommand();
			bool altDown = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
			bool automationChord = shiftDown && altDown;
			if ((Input.GetKeyDown(KeyCode.F3) && shiftDown)
			    || (Input.GetKeyDown(KeyCode.U) && automationChord))
				RunAllUnitTests();
			if ((Input.GetKeyDown(KeyCode.F4) && shiftDown)
			    || (Input.GetKeyDown(KeyCode.R) && automationChord))
				RunRiptideSmokeTest();
			if ((Input.GetKeyDown(KeyCode.F5) && shiftDown)
			    || (Input.GetKeyDown(KeyCode.S) && automationChord))
				SoakStateHashProbe.Toggle();
			if ((Input.GetKeyDown(KeyCode.F6) && shiftDown)
			    || (Input.GetKeyDown(KeyCode.H) && automationChord))
				StartConfiguredLanHost();
			if ((Input.GetKeyDown(KeyCode.F7) && shiftDown)
			    || (Input.GetKeyDown(KeyCode.J) && automationChord))
				StartConfiguredLanJoin();
#endif
		}

		private void OnGUI()
		{
            using var _ = Profiler.Scope();

			if (!showMenu) return;

			Matrix4x4 previousMatrix = GUI.matrix;
			try
			{
				GUI.matrix = ComposeUiScaleMatrix(previousMatrix, GetGameUiScale());
				GUIStyle windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(10, 10, 20, 20) };
				windowRect = GUI.ModalWindow(888, windowRect, DrawMenuContents, "DEBUG MENU", windowStyle);
			}
			finally
			{
				GUI.matrix = previousMatrix;
			}
		}

		internal static float GetGameUiScale()
		{
			var scaler = UnityEngine.Object.FindAnyObjectByType<KCanvasScaler>();
			return scaler != null ? scaler.GetCanvasScale() : 1f;
		}

		internal static Matrix4x4 ComposeUiScaleMatrix(
			Matrix4x4 currentMatrix, float uiScale)
			=> currentMatrix * Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));

        private void DrawMenuContents(int windowID)
        {
            using var _ = Profiler.Scope();

            scrollPosition = GUILayout.BeginScrollView(
                scrollPosition,
                false,
                true,
                GUILayout.Width(windowRect.width - 20),
                GUILayout.Height(windowRect.height - 40)
            );

            GUILayout.Label("Hosting", GUI.skin.box);
            GUILayout.Label("Transport:");
            selectedHostTransport = GUILayout.Toolbar(selectedHostTransport, hostTransportOptions);

            GUILayout.Space(5);

            GUILayout.Label("Host IP:");
            lanHostIP = GUILayout.TextField(lanHostIP);

            GUILayout.Label("Port:");
            lanHostPort = GUILayout.TextField(lanHostPort);

            if (GUILayout.Button("Start Hosting"))
            {
                if(selectedHostTransport == 0)
                {
                    NetworkConfig.NetworkTransport selected_transport = NetworkConfig.NetworkTransport.STEAMWORKS;
                    Configuration.Instance.Host.NetworkTransport = (int)selected_transport;
                    NetworkConfig.UpdateTransport(selected_transport);
                    Configuration.Instance.Save();

                    if (Utils.IsInMenu())
                    {
                        MultiplayerSession.ShouldHostAfterLoad = true;
                        showMenu = false;
                        string latestSave = SaveLoader.GetLatestSaveForCurrentDLC();
                        if (!string.IsNullOrEmpty(latestSave) && System.IO.File.Exists(latestSave))
                            MainMenu.Instance?.Button_ResumeGame.SignalClick(KKeyCode.Mouse0);
                        else
                            MainMenu.Instance?.NewGame();
                        return;
                    }

                    NetworkConfig.StartServer();
                    return;
                }

                if (int.TryParse(lanHostPort, out int port))
                {
                    DebugConsole.Log($"[LAN] Hosting on {lanHostIP}:{port}");

                    Configuration.Instance.Host.LanSettings.Ip = lanHostIP;
                    Configuration.Instance.Host.LanSettings.Port = port;

                    NetworkConfig.NetworkTransport selected_transport = NetworkConfig.NetworkTransport.RIPTIDE;
                    Configuration.Instance.Host.NetworkTransport = (int)selected_transport;
                    NetworkConfig.UpdateTransport(selected_transport);

                    Configuration.Instance.Save();

					NetworkConfig.StartServer();
                }
                else
                {
                    DebugConsole.LogError("Invalid port!");
                }
            }
            if (GUILayout.Button("Stop Hosting"))
            {
                NetworkConfig.Stop();
            }

#if DEBUG
            GUILayout.Space(10);
            GUILayout.Label("Unit Tests", GUI.skin.box);
            if (GUILayout.Button("Discover & Run All"))
				RunAllUnitTests();

            if (GUILayout.Button("Riptide Loopback Smoke Test"))
				RunRiptideSmokeTest();

			if (GUILayout.Button("Load Latest Save & Host Configured LAN"))
				StartConfiguredLanHost();

			if (GUILayout.Button("Join Configured LAN"))
				StartConfiguredLanJoin();

			if (GUILayout.Button("Toggle Soak State Hash"))
				SoakStateHashProbe.Toggle();

            int passed = 0;
            int failed = 0;
            foreach (UnitTest test in UnitTestRegistry.Tests)
            {
                if (test.IsPassed) passed++;
                if (test.IsFailed) failed++;
            }
            GUILayout.Label($"Tests: {passed} passed, {failed} failed, {UnitTestRegistry.Tests.Count - passed - failed} not run");
            foreach (UnitTest test in UnitTestRegistry.Tests)
            {
                if (test.IsFailed)
                    GUILayout.Label($"FAIL: {test.Name} — {test.Message}");
            }
#endif


            GUILayout.Space(10);

            GUILayout.Label("LAN Join", GUI.skin.box);

            GUILayout.Label("Server Address:");
            lanJoinAddress = GUILayout.TextField(lanJoinAddress);

            if (GUILayout.Button("Join Server"))
            {
                NetworkConfig.UpdateTransport(NetworkConfig.NetworkTransport.RIPTIDE); // Force into riptide (Testing)
                DebugConsole.Log($"[LAN] Joining {lanJoinAddress}");

                string[] address = lanJoinAddress.Split(':');
                if(address.Length != 2)
                {
                    DebugConsole.LogError("Invalid address format! Use IP:Port", false);
                    return;
                }

                if (int.TryParse(address[1], out int port))
                {
                    Configuration.Instance.Client.LanSettings.Ip = address[0];
                    Configuration.Instance.Client.LanSettings.Port = port;
                    Configuration.Instance.Save();

					Join(address[0], port);
                }
            }

            GUILayout.EndScrollView();

            GUI.DragWindow();
        }


		void Join(string ip, int port)
		{
			using var _ = Profiler.Scope();

			GameClient.ConnectToHost(ip: ip, port: port);
		}
	}
}
