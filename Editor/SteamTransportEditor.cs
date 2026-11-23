// Matches Steamworks.NET's own switch, so the Steam half compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
using System;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Nucleus.Integrations.Unity.Transports;
#if NUCLEUS_STEAMWORKS_ENABLED
using Nucleus.Transports;
using Steamworks;
#endif

namespace Nucleus.Integrations.Unity.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="SteamTransport"/>. A status box heads it, tinted by state: whether the transport is compiled
    /// in, the Steamworks.NET version, and while playing the role and App ID. While the transport is compiled out the box lists which check
    /// failed, and an install button follows when Git is on the editor's path. Then the running server's address with a button to copy it,
    /// a box recommending Heathen's Toolkit for Steamworks until a Heathen package is installed, how peers find each other, who a client
    /// connects to, and how the server listens and admits players. All presentation lives here per project convention.
    /// </summary>
    [CustomEditor(typeof(SteamTransport))]
    [CanEditMultipleObjects]
    public class SteamTransportEditor : UnityEditor.Editor
    {
#if !NUCLEUS_STEAMWORKS
        /// <summary>
        /// The Package Manager's install of Steamworks.NET, or <see langword="null"/> before the button is pressed.
        /// </summary>
        private AddRequest _addRequest;
#endif
        /// <summary>
        /// The installed Steamworks.NET version, such as "Steamworks.NET 2025.164.1", or empty when it is not installed.
        /// </summary>
        private string _steamworksVersionText = string.Empty;
        /// <summary>
        /// The status dot while the transport is running.
        /// </summary>
        private static readonly Color RunningColor = new(0.35f, 0.75f, 0.4f);
        /// <summary>
        /// The status dot while the transport is compiled in and idle.
        /// </summary>
        private static readonly Color IdleColor = new(0.55f, 0.55f, 0.55f);
        /// <summary>
        /// The status dot while the transport is compiled out, or Steam stopped under it.
        /// </summary>
        private static readonly Color WarningColor = new(0.9f, 0.65f, 0.2f);
        /// <summary>
        /// The tint of the recommended tools box.
        /// </summary>
        private static readonly Color RecommendationColor = new(0.35f, 0.6f, 0.95f);
        /// <summary>
        /// The fill of the version pill: a dark wash that reads on any tint.
        /// </summary>
        private static readonly Color StatusPillColor = new(0f, 0f, 0f, 0.25f);
        /// <summary>
        /// The git URL Steamworks.NET installs from.
        /// </summary>
        private const string SteamworksPackageUrl = "https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net";
        /// <summary>
        /// The Package Manager name of Steamworks.NET.
        /// </summary>
        private const string SteamworksPackageName = "com.rlabrecque.steamworks.net";
        /// <summary>
        /// Where Steamworks.NET is published, for a reader who wants to install it another way.
        /// </summary>
        private const string SteamworksHomeUrl = "https://github.com/rlabrecque/Steamworks.NET";
        /// <summary>
        /// Heathen's Toolkit for Steamworks, recommended for prebuilt Steam features beyond networking, such as lobbies, achievements,
        /// leaderboards and inventory.
        /// </summary>
        private const string HeathenToolkitUrl = "https://heathen.group/kb/steam-welcome/";
        /// <summary>
        /// The page Git is downloaded from.
        /// </summary>
        private const string GitDownloadUrl = "https://git-scm.com/downloads";
        /// <summary>
        /// The session key recording that Git was looked for this editor session.
        /// </summary>
        private const string GitCheckedSessionKey = "Nucleus.Steamworks.GitChecked";
        /// <summary>
        /// The session key recording whether Git was found.
        /// </summary>
        private const string GitFoundSessionKey = "Nucleus.Steamworks.GitFound";
        /// <summary>
        /// How long the Git check waits for <c>git --version</c> to answer, in milliseconds.
        /// </summary>
        private const int GitCheckTimeoutMilliseconds = 3000;
        /// <summary>
        /// The width of the button that copies the server's address.
        /// </summary>
        private const float CopyButtonWidth = 48f;
        /// <summary>
        /// The height of the status box's top row.
        /// </summary>
        private const float StatusRowHeight = 26f;
        /// <summary>
        /// The height of the version pill inside the status box.
        /// </summary>
        private const float StatusPillHeight = 16f;
        /// <summary>
        /// The diameter of the status dot.
        /// </summary>
        private const float StatusDotSize = 8f;
        /// <summary>
        /// The corner radius of the status and recommended tools boxes.
        /// </summary>
        private const float BoxCornerRadius = 4f;
        /// <summary>
        /// The space inside the status and recommended tools boxes around what they hold.
        /// </summary>
        private const float BoxPadding = 8f;
        /// <summary>
        /// How strongly a box's colour tints it.
        /// </summary>
        private const float BoxFillAlpha = 0.12f;
        /// <summary>
        /// How strongly a box's colour draws its border.
        /// </summary>
        private const float BoxBorderAlpha = 0.35f;
        /// <summary>
        /// The width of a compile check's OK or Blocked column.
        /// </summary>
        private const float CheckResultWidth = 64f;
        /// <summary>
        /// What every addressing needs from the game, said once under the addressing's own help.
        /// </summary>
        private const string SteamOwnershipText = "Steam must already be initialised, and its callbacks run every frame, by something else in the game, such as Heathen's Foundation for Steamworks or a SteamManager.";

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            NucleusInspectorGUI.DrawScriptField(serializedObject);

            DrawSteamworksStatus();

            serializedObject.Update();

            SerializedProperty addressingProperty = serializedObject.FindProperty("_addressing");
            // A mixed selection shows every field, so nothing a selected transport uses is hidden.
            bool isMixed = addressingProperty.hasMultipleDifferentValues;
            bool isSteamIdShown = isMixed || addressingProperty.enumValueIndex == (int)SteamAddressing.SteamId;
            bool isIpAddressShown = isMixed || addressingProperty.enumValueIndex == (int)SteamAddressing.IpAddress;
            bool isFakeIpShown = isMixed || addressingProperty.enumValueIndex == (int)SteamAddressing.FakeIp;

            EditorGUILayout.HelpBox($"{GetAddressingText(isSteamIdShown, isIpAddressShown, isFakeIpShown)}\n\n{SteamOwnershipText}", MessageType.Info);

            EditorGUILayout.PropertyField(addressingProperty);

            using (new EditorGUI.IndentLevelScope())
            {
                if (isSteamIdShown)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_remoteSteamId"));

                if (isIpAddressShown || isFakeIpShown)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_remoteAddress"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_expectedServerSteamId"));
                }

                if (isSteamIdShown || isFakeIpShown)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_isRelayOnlyEnabled"));

                if (isIpAddressShown)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_port"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_bindAddress"));
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_isGameServer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumConnections"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumConnectionsPerSteamId"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumTransmissionUnit"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_sendRateKilobytesPerSecond"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Players", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_ticketAuthenticationEnabled"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_shareSteamIdsEnabled"));

            serializedObject.ApplyModifiedProperties();
        }

        /// <inheritdoc/>
        /// <remarks>Repaints every frame while playing, since the role and the fake IP address change with no event this inspector hears.</remarks>
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        /// <summary>
        /// Reads the Steamworks.NET version for the status box, or, while it is missing, looks for Git once per editor session so the
        /// install button is drawn only where the Package Manager can use it.
        /// </summary>
        private void OnEnable()
        {
#if NUCLEUS_STEAMWORKS
            // Found by name, not by a Steamworks.NET type, since DISABLESTEAMWORKS compiles those types out while the package stays.
            UnityEditor.PackageManager.PackageInfo packageInfo = UnityEditor.PackageManager.PackageInfo.FindForPackageName(SteamworksPackageName);

            if (packageInfo is not null)
                _steamworksVersionText = $"Steamworks.NET {packageInfo.version}";
#else
            if (SessionState.GetBool(GitCheckedSessionKey, defaultValue: false))
                return;

            SessionState.SetBool(GitFoundSessionKey, IsGitFound());
            SessionState.SetBool(GitCheckedSessionKey, true);
#endif
        }

        /// <summary>
        /// Draws the status box and, under it, the install button while Steamworks.NET is missing, or, while this transport's server
        /// runs, where clients reach it, with a button to copy that. The recommended Steamworks tools follow.
        /// </summary>
        private void DrawSteamworksStatus()
        {
            DrawStatusBox();
#if NUCLEUS_STEAMWORKS_ENABLED
            if (targets.Length == 1)
                DrawServerAddressRow();
#elif !NUCLEUS_STEAMWORKS
            DrawSteamworksInstall();
#endif
#if NUCLEUS_STEAMWORKS && !NUCLEUS_HEATHEN_STEAMWORKS
            DrawRecommendedTools();
#endif
            EditorGUILayout.Space();
        }

        /// <summary>
        /// Draws the status box: a rounded box tinted by state, holding a dot, the state, and the Steamworks.NET version in a pill at the
        /// right. While the transport is compiled out the box grows to hold the checklist under a divider, so the state and its causes read
        /// as one block.
        /// </summary>
        private void DrawStatusBox()
        {
            GetStatus(out string statusText, out Color statusColor);

            EditorGUILayout.Space(2f);

            Rect boxRect = EditorGUILayout.BeginVertical();

            DrawTintedBox(boxRect, statusColor);
            DrawStatusRow(EditorGUILayout.GetControlRect(hasLabel: false, StatusRowHeight), statusText, statusColor);
#if !NUCLEUS_STEAMWORKS_ENABLED

            Rect dividerRect = EditorGUILayout.GetControlRect(hasLabel: false, height: 1f);
            Color borderColor = new(statusColor.r, statusColor.g, statusColor.b, BoxBorderAlpha);

            EditorGUI.DrawRect(new(dividerRect.x + BoxPadding, dividerRect.y, dividerRect.width - BoxPadding * 2f, 1f), borderColor);
            DrawCompiledOutChecks();
            EditorGUILayout.Space(BoxPadding / 2f);
#endif

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draws the status box's top row: the dot, the state, and the version pill.
        /// </summary>
        /// <param name="rowRect">The row, spanning the box's width.</param>
        /// <param name="statusText">The state.</param>
        /// <param name="statusColor">The dot's colour.</param>
        private void DrawStatusRow(Rect rowRect, string statusText, Color statusColor)
        {
            Rect dotRect = new(rowRect.x + BoxPadding, rowRect.y + (rowRect.height - StatusDotSize) / 2f, StatusDotSize, StatusDotSize);

            GUI.DrawTexture(dotRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, alphaBlend: true, imageAspect: 0f, statusColor, borderWidth: 0f, borderRadius: StatusDotSize / 2f);

            float pillWidth = 0f;

            if (_steamworksVersionText.Length > 0)
            {
                GUIContent versionGuiContent = new(_steamworksVersionText);
                Vector2 versionSize = EditorStyles.miniLabel.CalcSize(versionGuiContent);
                pillWidth = versionSize.x + BoxPadding * 2f;
                Rect pillRect = new(rowRect.xMax - pillWidth - BoxPadding / 2f, rowRect.y + (rowRect.height - StatusPillHeight) / 2f, pillWidth, StatusPillHeight);

                GUI.DrawTexture(pillRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, alphaBlend: true, imageAspect: 0f, StatusPillColor, borderWidth: 0f, borderRadius: StatusPillHeight / 2f);
                GUI.Label(new(pillRect.x + BoxPadding, pillRect.y, versionSize.x, pillRect.height), versionGuiContent, EditorStyles.miniLabel);
            }

            Rect textRect = new(dotRect.xMax + BoxPadding, rowRect.y, rowRect.width - (dotRect.xMax - rowRect.x) - BoxPadding * 2f - pillWidth, rowRect.height);

            EditorGUI.LabelField(textRect, statusText);
        }

        /// <summary>
        /// Gets the status line's text and colour from whether the transport is compiled in and, while playing, what it runs.
        /// </summary>
        /// <param name="statusText">The state, such as "Steam running · App 480 · hosting".</param>
        /// <param name="statusColor">The dot's colour.</param>
        private void GetStatus(out string statusText, out Color statusColor)
        {
#if NUCLEUS_STEAMWORKS_ENABLED
            statusColor = IdleColor;

            if (!Application.isPlaying)
            {
                statusText = "Ready to start";

                return;
            }

            SteamNetworkingTransport steamNetworkingTransport = ((SteamTransport)target).SteamNetworkingTransport;

            if (steamNetworkingTransport is null)
            {
                statusText = "Not added to a running CoreManager";

                return;
            }

            bool isServerStarted = steamNetworkingTransport.ServerSocket is { State: LocalConnectionState.Connected };
            bool isClientStarted = steamNetworkingTransport.ClientSocket is { State: LocalConnectionState.Connected };

            if (!isServerStarted && !isClientStarted)
            {
                statusText = "Steam transport idle";

                return;
            }

            // A dedicated server signs in only as a game server, so its App ID is read there; every other peer has the player's.
            bool isGameServerOnly = isServerStarted && !isClientStarted && steamNetworkingTransport.IsGameServer;

            if (!TryGetAppId(isGameServerOnly, out uint appId))
            {
                statusText = "Steam stopped while the transport runs";
                statusColor = WarningColor;

                return;
            }

            string roleText = "client";

            if (isServerStarted)
                roleText = isClientStarted ? "hosting" : "server";

            statusText = $"Steam running · App {appId} · {roleText}";
            statusColor = RunningColor;
#else
            statusText = "Transport compiled out";
            statusColor = WarningColor;
#endif
        }
#if !NUCLEUS_STEAMWORKS_ENABLED

        /// <summary>
        /// Draws why the transport is compiled out, inside the status box, as one line per check saying what blocks it and how to fix that.
        /// </summary>
        /// <remarks>The checklist stands in for a warning box: the amber status box above it already says the transport is compiled out.</remarks>
        private void DrawCompiledOutChecks()
        {
#if NUCLEUS_STEAMWORKS
            DrawCheck(isPassed: true, _steamworksVersionText.Length > 0 ? $"{_steamworksVersionText} is installed" : "Steamworks.NET is installed");
#else
            bool isGitFound = SessionState.GetBool(GitFoundSessionKey, defaultValue: false);

            DrawCheck(isPassed: false, "Steamworks.NET is not installed: install it below");
            DrawCheck(isGitFound, isGitFound ? "Git is on the editor's path, for the install" : "Git is not on the editor's path: install it and restart Unity, or import Steamworks.NET's .unitypackage");
#endif
#if DISABLESTEAMWORKS
            DrawCheck(isPassed: false, "DISABLESTEAMWORKS is defined: remove it from Scripting Define Symbols");
#else
            DrawCheck(isPassed: true, "DISABLESTEAMWORKS is not defined");
#endif
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX
            DrawCheck(isPassed: true, $"Build target {EditorUserBuildSettings.activeBuildTarget} is supported");
#else
            DrawCheck(isPassed: false, $"Build target {EditorUserBuildSettings.activeBuildTarget} is not supported: switch to Windows, macOS, Linux or Android");
#endif
        }
#endif
#if !NUCLEUS_STEAMWORKS

        /// <summary>
        /// Draws, under the status box while Steamworks.NET is missing, the install button, or a Git download link when the Package Manager
        /// has no Git to install with, and the project link.
        /// </summary>
        private void DrawSteamworksInstall()
        {
            bool isInstalling = _addRequest is { IsCompleted: false };

            if (_addRequest is { IsCompleted: true, Status: StatusCode.Failure })
                EditorGUILayout.HelpBox($"Steamworks.NET could not be installed: {_addRequest.Error?.message}", MessageType.Error);

            if (SessionState.GetBool(GitFoundSessionKey, defaultValue: false))
            {
                using (new EditorGUI.DisabledScope(isInstalling))
                {
                    if (GUILayout.Button(isInstalling ? "Installing Steamworks.NET..." : "Install Steamworks.NET"))
                        _addRequest = Client.Add(SteamworksPackageUrl);
                }
            }
            else if (EditorGUILayout.LinkButton("Download Git"))
            {
                Application.OpenURL(GitDownloadUrl);
            }

            if (EditorGUILayout.LinkButton("Steamworks.NET on GitHub"))
                Application.OpenURL(SteamworksHomeUrl);

            // The request finishes on its own; repainting is what shows it finishing.
            if (isInstalling)
                Repaint();
        }
#endif
#if NUCLEUS_STEAMWORKS_ENABLED

        /// <summary>
        /// Draws where clients reach this transport's running server, with a button to copy it: its Steam ID, its fake IP address once Steam
        /// grants one, or its port.
        /// </summary>
        private void DrawServerAddressRow()
        {
            SteamNetworkingTransport steamNetworkingTransport = ((SteamTransport)target).SteamNetworkingTransport;

            if (!Application.isPlaying || steamNetworkingTransport is not { ServerSocket: { State: LocalConnectionState.Connected } })
                return;

            string label;
            string copyText;

            switch (steamNetworkingTransport.Addressing)
            {
                case SteamAddressing.IpAddress:
                    label = "Server port";
                    copyText = steamNetworkingTransport.Configuration.Port.ToString();

                    break;
                case SteamAddressing.FakeIp:
                    label = "Server fake IP";
                    copyText = steamNetworkingTransport.ServerFakeAddress;

                    break;
                default:
                    label = "Server Steam ID";
                    copyText = steamNetworkingTransport.ServerSteamId.ToString();

                    break;
            }

            Rect rowRect = EditorGUILayout.GetControlRect();
            Rect valueRect = new(rowRect.x, rowRect.y, rowRect.width - CopyButtonWidth - EditorGUIUtility.standardVerticalSpacing, rowRect.height);

            EditorGUI.LabelField(valueRect, label, copyText ?? "Waiting for Steam to grant it...");

            using (new EditorGUI.DisabledScope(copyText is null))
            {
                if (GUI.Button(new(rowRect.xMax - CopyButtonWidth, rowRect.y, CopyButtonWidth, rowRect.height), "Copy", EditorStyles.miniButton))
                    EditorGUIUtility.systemCopyBuffer = copyText;
            }
        }
#endif
#if NUCLEUS_STEAMWORKS && !NUCLEUS_HEATHEN_STEAMWORKS

        /// <summary>
        /// Draws, in a blue box, a pointer to Heathen's Toolkit for Steamworks for the prebuilt Steam features this transport does not
        /// cover, with a link to it.
        /// </summary>
        /// <remarks>Drawn only once Steamworks.NET is installed, and never once Heathen's Foundation or Toolkit for Steamworks is.</remarks>
        private static void DrawRecommendedTools()
        {
            EditorGUILayout.Space(2f);

            Rect boxRect = EditorGUILayout.BeginVertical();

            DrawTintedBox(boxRect, RecommendationColor);
            EditorGUILayout.Space(BoxPadding / 2f);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(BoxPadding);

                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label("Looking for prebuilt Steam features? We recommend Heathen's Toolkit for Steamworks for lobbies, achievements, leaderboards, inventory, and more.", EditorStyles.wordWrappedLabel);

                    if (EditorGUILayout.LinkButton("heathen.group/kb/steam-welcome"))
                        Application.OpenURL(HeathenToolkitUrl);
                }

                GUILayout.Space(BoxPadding);
            }

            EditorGUILayout.Space(BoxPadding / 2f);
            EditorGUILayout.EndVertical();
        }
#endif

        /// <summary>
        /// Paints a rounded box tinted by a colour and bordered in it, behind the rows drawn into it.
        /// </summary>
        /// <param name="boxRect">The box, as the layout group the rows are drawn into returned it.</param>
        /// <param name="color">The colour.</param>
        /// <remarks>The group's rect is only known once layout has run, so the box is painted on Repaint alone.</remarks>
        private static void DrawTintedBox(Rect boxRect, Color color)
        {
            if (Event.current.type is not EventType.Repaint)
                return;

            Color fillColor = new(color.r, color.g, color.b, BoxFillAlpha);
            Color borderColor = new(color.r, color.g, color.b, BoxBorderAlpha);

            GUI.DrawTexture(boxRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, alphaBlend: true, imageAspect: 0f, fillColor, borderWidth: 0f, borderRadius: BoxCornerRadius);
            GUI.DrawTexture(boxRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, alphaBlend: true, imageAspect: 0f, borderColor, borderWidth: 1f, borderRadius: BoxCornerRadius);
        }

        /// <summary>
        /// Returns the help for the addressing shown, or for all of them when a mixed selection shows more than one.
        /// </summary>
        /// <param name="isSteamIdShown">Whether Steam ID addressing is shown.</param>
        /// <param name="isIpAddressShown">Whether IP addressing is shown.</param>
        /// <param name="isFakeIpShown">Whether fake IP addressing is shown.</param>
        /// <returns>The help text.</returns>
        private static string GetAddressingText(bool isSteamIdShown, bool isIpAddressShown, bool isFakeIpShown)
        {
            if (isFakeIpShown && !isSteamIdShown && !isIpAddressShown)
                return "Peers connect by a fake IP address Steam grants the server, carried over Valve's relay network, so a dedicated server's real address is never shown and no port is opened. The server logs its address once Steam grants it; a client sets it as Remote Address. A game server asks Steam for one between GameServer.Init and logging on.";

            if (isIpAddressShown && !isSteamIdShown && !isFakeIpShown)
                return "Peers connect by IP address and port, the way a dedicated server is usually reached. The server listens on its port, which must be open to the players joining it.";

            return "Peers reach each other by Steam ID through Valve's relay network, so there is no port to forward and no address to share. A host hands its Steam ID to the players joining it.";
        }
#if NUCLEUS_STEAMWORKS_ENABLED

        /// <summary>
        /// Reads the App ID the chosen Steam interface signed in with.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="appId">The App ID, or zero when the interface is down.</param>
        /// <returns>True when the interface is up and the App ID was read.</returns>
        /// <remarks>The game owns Steam and can shut it down while a socket is still open, and Steamworks.NET then throws on every call.</remarks>
        private static bool TryGetAppId(bool isGameServer, out uint appId)
        {
            try
            {
                appId = (isGameServer ? SteamGameServerUtils.GetAppID() : SteamUtils.GetAppID()).m_AppId;

                return true;
            }
            catch (InvalidOperationException)
            {
                appId = 0U;

                return false;
            }
        }
#else

        /// <summary>
        /// Draws one compile check as OK or Blocked beside what it found.
        /// </summary>
        /// <param name="isPassed">Whether the check passed.</param>
        /// <param name="description">What it found, and how to fix it when it failed.</param>
        private static void DrawCheck(bool isPassed, string description)
        {
            Rect rowRect = EditorGUILayout.GetControlRect();

            // Inset to line up with the status text above, inside the box's border.
            rowRect.xMin += BoxPadding;
            rowRect.xMax -= BoxPadding;

            Rect resultRect = new(rowRect.x, rowRect.y, CheckResultWidth, rowRect.height);
            Color previousColor = GUI.contentColor;

            GUI.contentColor = isPassed ? RunningColor : WarningColor;
            EditorGUI.LabelField(resultRect, isPassed ? "OK" : "Blocked", EditorStyles.boldLabel);
            GUI.contentColor = previousColor;

            EditorGUI.LabelField(new(resultRect.xMax, rowRect.y, rowRect.width - resultRect.width, rowRect.height), description);
        }
#endif
#if !NUCLEUS_STEAMWORKS

        /// <summary>
        /// Checks whether <c>git</c> runs from this editor's path, which is the Git the Package Manager installs with.
        /// </summary>
        /// <returns>True when <c>git --version</c> ran and succeeded.</returns>
        private static bool IsGitFound()
        {
            try
            {
                using Process gitProcess = Process.Start(new ProcessStartInfo("git", "--version")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                return gitProcess is not null && gitProcess.WaitForExit(GitCheckTimeoutMilliseconds) && gitProcess.ExitCode == 0;
            }
            catch (Exception)
            {
                // Process.Start throws, rather than returning null, when git is not on the path.
                return false;
            }
        }
#endif
    }
}
