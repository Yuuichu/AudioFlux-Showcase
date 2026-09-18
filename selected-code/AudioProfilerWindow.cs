using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

using AudioFlux;
namespace AudioFlux.Tools
{
    /// <summary>
    /// 音频 Profiler 窗口
    /// 参考成熟音频中间件的 Profiler 设计
    /// Log 视图：时序事件日志
    /// Voice Monitor 视图：实时活跃实例计数（类似 Wwise F6）
    /// </summary>
    public class AudioProfilerWindow : EditorWindow
    {
        #region Constants
        private const int MAX_MESSAGES = 500;
        private const int COLUMN_SEVERITY = 60;
        private const int COLUMN_TIME = 55;
        private const int COLUMN_TYPE = 85;
        private const int COLUMN_ACTION = 50;
        private const int COLUMN_NAME_MIN = 120;
        private const int COLUMN_NAME_MAX = 200;
        private const int COLUMN_CLIP_MIN = 100;
        private const int COLUMN_CLIP_MAX = 180;
        private const int COLUMN_TRIGGER = 80;
        private const int COLUMN_GO_MIN = 80;
        private const int COLUMN_GO_MAX = 150;
        private const int DIVIDER = 2;
        #endregion

        #region Colors
        private static readonly Color ColorInfo = new Color(0.8f, 0.8f, 0.8f);
        private static readonly Color ColorWarning = new Color(1f, 0.9f, 0.3f);
        private static readonly Color ColorError = new Color(1f, 0.4f, 0.4f);

        private static readonly Color ColorCharSFX = new Color(0.5f, 1f, 1f);      // Aqua
        private static readonly Color ColorItemSFX = new Color(1f, 0.7f, 0.3f);    // Orange
        private static readonly Color ColorPhysicsSFX = new Color(0.6f, 0.5f, 0.4f); // Brown-Gray
        private static readonly Color ColorMusic = new Color(0.5f, 1f, 0.5f);      // Light Green
        private static readonly Color ColorUI = new Color(1f, 0.8f, 0.5f);         // Gold
        private static readonly Color ColorAmbient = new Color(0.7f, 0.5f, 1f);    // Purple
        private static readonly Color ColorVoice = new Color(1f, 1f, 0.5f);        // Yellow
        private static readonly Color ColorSystem = new Color(0.5f, 0.7f, 1f);     // Blue
        private static readonly Color ColorEmitter = new Color(0.9f, 0.7f, 0.5f);  // Skin/Tan

        private static readonly Color ColorPlay = new Color(0.5f, 1f, 0.5f);
        private static readonly Color ColorStop = new Color(1f, 0.5f, 0.3f);
        private static readonly Color ColorPause = new Color(1f, 0.8f, 0f);        // Amber
        private static readonly Color ColorResume = new Color(0.5f, 1f, 0.8f);     // Cyan-Green
        private static readonly Color ColorSkip = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color ColorLoad = new Color(0.5f, 0.8f, 1f);
        private static readonly Color ColorMute = new Color(0.4f, 0.6f, 0.4f);     // Dark Green
        private static readonly Color ColorUnmute = new Color(0.6f, 1f, 0.6f);     // Light Green

        // Voice Monitor 专用
        private static readonly Color ColorBarBg = new Color(0.2f, 0.2f, 0.2f);
        private static readonly Color ColorBarFill = new Color(0.3f, 0.7f, 1f);
        private static readonly Color ColorBarPeak = new Color(1f, 0.5f, 0.3f, 0.6f);
        #endregion

        #region View Mode
        private enum ViewMode { Log, VoiceMonitor }
        private ViewMode _viewMode = ViewMode.Log;
        #endregion

        #region Log State
        private Queue<AudioProfilerMessage> _messages = new Queue<AudioProfilerMessage>();
        private Vector2 _scrollPosition;

        // Toolbar toggles
        private bool _showTextFilter = true;
        private bool _showToggleFilter = true;

        // Filters - Severity
        private bool _includeInfo = true;
        private bool _includeWarning = true;
        private bool _includeError = true;

        // Filters - Trigger
        private bool _includeCode = true;
        private bool _includeAnimEvent = true;
        private bool _includeAutoDetect = true;
        private bool _includeInspector = true;
        private bool _includeTrigger = true;
        private bool _includePhysics = true;
        private bool _includeDistance = true;

        // Filters - Type
        private bool _includeCharSFX = true;
        private bool _includeItemSFX = true;
        private bool _includePhysicsSFX = true;
        private bool _includeMusic = true;
        private bool _includeUI = true;
        private bool _includeAmbient = true;
        private bool _includeEmitter = true;
        private bool _includeVoice = true;
        private bool _includeSystem = true;

        // Control
        private bool _paused = false;
        private bool _autoScroll = true;
        private string _nameFilter = "";
        #endregion

        #region Voice Monitor State

        private struct VoiceEntry
        {
            public int ActiveCount;
            public int PeakCount;
            public int TotalPlays;
            public AudioObjectType ObjectType;
            public string LastClip;
            public float LastVolume;
            public float LastPlayTime;
            public GameObject LastGameObject;
            public string LastGameObjectName;
            public Vector3 LastPosition;
            public bool IsVirtualized;
        }

        private Dictionary<string, VoiceEntry> _voices = new Dictionary<string, VoiceEntry>();
        private Vector2 _voiceScrollPosition;
        private string _voiceFilter = "";

        private enum VoiceSortColumn { Name, Active, Peak, Total, Type }
        private VoiceSortColumn _voiceSortColumn = VoiceSortColumn.Active;
        private bool _voiceSortDescending = true;
        private bool _voiceShowZero = false;

        #endregion

        #region Menu
        [MenuItem("Window/AudioFlux/Audio Profiler")]
        public static void ShowWindow()
        {
            var window = GetWindow<AudioProfilerWindow>();
            window.titleContent = new GUIContent("Audio Profiler");
            window.Show();
        }
        #endregion

        #region Lifecycle
        private void OnEnable()
        {
            AudioProfilerHelper.ProfilerCallback += OnProfilerMessage;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            AudioProfilerHelper.ProfilerCallback -= OnProfilerMessage;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _voices.Clear();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                // 确保退出 PlayMode 后所有 GameObject 引用已清除
                _voices.Clear();
            }
        }

        private void OnProfilerMessage(AudioProfilerMessage message)
        {
            if (_paused) return;

            // Voice Monitor 始终追踪（不受 filter 影响）
            UpdateVoiceTracker(message);

            if (!FilterMessage(message)) return;

            _messages.Enqueue(message);
            while (_messages.Count > MAX_MESSAGES)
            {
                _messages.Dequeue();
            }
            Repaint();
        }

        private void UpdateVoiceTracker(AudioProfilerMessage msg)
        {
            string key = msg.AudioName;
            if (string.IsNullOrEmpty(key)) return;

            // 仅追踪 Play/Stop/Pause/Resume
            if (msg.Action != AudioAction.Play && msg.Action != AudioAction.Stop &&
                msg.Action != AudioAction.Pause && msg.Action != AudioAction.Resume)
                return;

            _voices.TryGetValue(key, out VoiceEntry entry);

            switch (msg.Action)
            {
                case AudioAction.Play:
                    entry.ActiveCount++;
                    entry.TotalPlays++;
                    if (entry.ActiveCount > entry.PeakCount)
                        entry.PeakCount = entry.ActiveCount;
                    entry.LastClip = msg.ClipName;
                    entry.LastVolume = msg.Volume;
                    entry.LastPlayTime = msg.Time;
                    entry.LastGameObject = msg.GameObject;
                    entry.LastGameObjectName = msg.GameObjectName;
                    entry.LastPosition = msg.Position;
                    entry.IsVirtualized = false;
                    entry.ObjectType = msg.ObjectType;
                    break;
                case AudioAction.Stop:
                    entry.ActiveCount = Mathf.Max(0, entry.ActiveCount - 1);
                    if (entry.ActiveCount == 0) entry.IsVirtualized = false;
                    break;
                case AudioAction.Pause:
                    if (msg.ExtraInfo == "Voice virtualized")
                        entry.IsVirtualized = true;
                    break;
                case AudioAction.Resume:
                    if (msg.ExtraInfo == "Voice devirtualized")
                        entry.IsVirtualized = false;
                    break;
            }

            _voices[key] = entry;
        }
        #endregion

        #region GUI
        private void OnGUI()
        {
            DrawViewModeToolbar();

            if (_viewMode == ViewMode.Log)
            {
                DrawToolbar();
                if (_showTextFilter) DrawTextFilter();
                if (_showToggleFilter) DrawToggleFilters();
                DrawControls();
                DrawHeader();
                DrawMessages();
            }
            else
            {
                DrawVoiceMonitorControls();
                DrawVoiceMonitorHeader();
                DrawVoiceMonitor();
            }
        }

        private void DrawViewModeToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            var logStyle = _viewMode == ViewMode.Log ? EditorStyles.toolbarButton : EditorStyles.toolbarButton;
            GUI.backgroundColor = _viewMode == ViewMode.Log ? new Color(0.6f, 0.8f, 1f) : Color.white;
            if (GUILayout.Toggle(_viewMode == ViewMode.Log, "Log", EditorStyles.toolbarButton, GUILayout.Width(60)))
                _viewMode = ViewMode.Log;

            GUI.backgroundColor = _viewMode == ViewMode.VoiceMonitor ? new Color(0.6f, 0.8f, 1f) : Color.white;
            if (GUILayout.Toggle(_viewMode == ViewMode.VoiceMonitor, "Voice Monitor", EditorStyles.toolbarButton, GUILayout.Width(100)))
                _viewMode = ViewMode.VoiceMonitor;

            GUI.backgroundColor = Color.white;

            GUILayout.FlexibleSpace();

            // 总活跃声音数
            int totalActive = 0;
            foreach (var v in _voices.Values) totalActive += v.ActiveCount;
            GUILayout.Label($"Active Voices: {totalActive}", EditorStyles.miniLabel);

            GUILayout.EndHorizontal();
        }

        #region Log View

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            _showTextFilter = GUILayout.Toggle(_showTextFilter, "Text Filter", EditorStyles.toolbarButton, GUILayout.Width(80));
            _showToggleFilter = GUILayout.Toggle(_showToggleFilter, "Toggle Filter", EditorStyles.toolbarButton, GUILayout.Width(90));
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Messages: {_messages.Count}/{MAX_MESSAGES}", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
        }

        private void DrawTextFilter()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", GUILayout.Width(45));
            _nameFilter = EditorGUILayout.TextField(_nameFilter, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                _nameFilter = "";
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawToggleFilters()
        {
            // Severity
            GUILayout.BeginHorizontal();
            GUILayout.Label("Severity:", GUILayout.Width(60));
            _includeInfo = GUILayout.Toggle(_includeInfo, "Info", GUILayout.Width(50));
            _includeWarning = GUILayout.Toggle(_includeWarning, "Warning", GUILayout.Width(65));
            _includeError = GUILayout.Toggle(_includeError, "Error", GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Trigger
            GUILayout.BeginHorizontal();
            GUILayout.Label("Trigger:", GUILayout.Width(60));
            _includeCode = GUILayout.Toggle(_includeCode, "Code", GUILayout.Width(50));
            _includeAnimEvent = GUILayout.Toggle(_includeAnimEvent, "AnimEvent", GUILayout.Width(80));
            _includeAutoDetect = GUILayout.Toggle(_includeAutoDetect, "AutoDetect", GUILayout.Width(85));
            _includeInspector = GUILayout.Toggle(_includeInspector, "Inspector", GUILayout.Width(70));
            _includeTrigger = GUILayout.Toggle(_includeTrigger, "Trigger", GUILayout.Width(60));
            _includePhysics = GUILayout.Toggle(_includePhysics, "Physics", GUILayout.Width(60));
            _includeDistance = GUILayout.Toggle(_includeDistance, "Distance", GUILayout.Width(65));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Type
            GUILayout.BeginHorizontal();
            GUILayout.Label("Type:", GUILayout.Width(60));
            _includeCharSFX = GUILayout.Toggle(_includeCharSFX, "CharSFX", GUILayout.Width(70));
            _includeItemSFX = GUILayout.Toggle(_includeItemSFX, "ItemSFX", GUILayout.Width(65));
            _includePhysicsSFX = GUILayout.Toggle(_includePhysicsSFX, "PhysSFX", GUILayout.Width(65));
            _includeMusic = GUILayout.Toggle(_includeMusic, "Music", GUILayout.Width(55));
            _includeUI = GUILayout.Toggle(_includeUI, "UI", GUILayout.Width(35));
            _includeAmbient = GUILayout.Toggle(_includeAmbient, "Ambient", GUILayout.Width(65));
            _includeEmitter = GUILayout.Toggle(_includeEmitter, "Emitter", GUILayout.Width(60));
            _includeVoice = GUILayout.Toggle(_includeVoice, "Voice", GUILayout.Width(50));
            _includeSystem = GUILayout.Toggle(_includeSystem, "System", GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Select All / Deselect All
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Select All", GUILayout.Width(80))) SetAllFilters(true);
            if (GUILayout.Button("Deselect All", GUILayout.Width(80))) SetAllFilters(false);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
        }

        private void DrawControls()
        {
            GUILayout.BeginHorizontal();
            GUI.backgroundColor = _paused ? Color.yellow : Color.white;
            if (GUILayout.Button(_paused ? "▶ Resume" : "⏸ Pause", GUILayout.Width(80)))
            {
                _paused = !_paused;
            }
            GUI.backgroundColor = Color.white;

            if (GUILayout.Button("Clear", GUILayout.Width(60)))
            {
                _messages.Clear();
            }

            GUILayout.FlexibleSpace();
            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto Scroll", GUILayout.Width(90));
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
        }

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("Severity", EditorStyles.miniLabel, GUILayout.Width(COLUMN_SEVERITY));
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("Time", EditorStyles.miniLabel, GUILayout.Width(COLUMN_TIME));
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("Type", EditorStyles.miniLabel, GUILayout.Width(COLUMN_TYPE));
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("Action", EditorStyles.miniLabel, GUILayout.Width(COLUMN_ACTION));
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("Audio Name", EditorStyles.miniLabel, GUILayout.MinWidth(COLUMN_NAME_MIN), GUILayout.MaxWidth(COLUMN_NAME_MAX));
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("Clip", EditorStyles.miniLabel, GUILayout.MinWidth(COLUMN_CLIP_MIN), GUILayout.MaxWidth(COLUMN_CLIP_MAX));
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("Trigger", EditorStyles.miniLabel, GUILayout.Width(COLUMN_TRIGGER));
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("GameObject", EditorStyles.miniLabel, GUILayout.MinWidth(COLUMN_GO_MIN), GUILayout.MaxWidth(COLUMN_GO_MAX));
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label("Info", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawMessages()
        {
            using (var scrollView = new EditorGUILayout.ScrollViewScope(_scrollPosition))
            {
                _scrollPosition = scrollView.scrollPosition;

                if (_autoScroll && Application.isPlaying && !EditorApplication.isPaused)
                {
                    _scrollPosition.y = float.MaxValue;
                }

                foreach (var msg in _messages)
                {
                    if (!FilterMessage(msg)) continue;
                    DrawMessageRow(msg);
                }
            }
        }

        private void DrawMessageRow(AudioProfilerMessage msg)
        {
            GUILayout.BeginHorizontal();

            // Severity
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUI.color = GetSeverityColor(msg.Severity);
            GUILayout.Label(msg.Severity.ToString(), GUILayout.Width(COLUMN_SEVERITY));

            // Time
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUI.color = Color.white;
            GUILayout.Label(msg.Time.ToString("F3"), GUILayout.Width(COLUMN_TIME));

            // Type
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUI.color = GetTypeColor(msg.ObjectType);
            GUILayout.Label(msg.ObjectType.ToString(), GUILayout.Width(COLUMN_TYPE));

            // Action
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUI.color = GetActionColor(msg.Action);
            GUILayout.Label(msg.Action.ToString(), GUILayout.Width(COLUMN_ACTION));

            // Audio Name
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUI.color = Color.white;
            GUILayout.Label(msg.AudioName, GUILayout.MinWidth(COLUMN_NAME_MIN), GUILayout.MaxWidth(COLUMN_NAME_MAX));

            // Clip Name
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label(msg.ClipName, GUILayout.MinWidth(COLUMN_CLIP_MIN), GUILayout.MaxWidth(COLUMN_CLIP_MAX));

            // Trigger
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label(msg.TriggerFrom.ToString(), GUILayout.Width(COLUMN_TRIGGER));

            // GameObject (clickable)
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            if (GUILayout.Button(msg.GameObjectName, GUI.skin.label, GUILayout.MinWidth(COLUMN_GO_MIN), GUILayout.MaxWidth(COLUMN_GO_MAX)))
            {
                if (msg.GameObject != null)
                {
                    Selection.activeGameObject = msg.GameObject;
                    EditorGUIUtility.PingObject(msg.GameObject);
                }
            }

            // Extra Info
            GUILayout.Label("", GUILayout.Width(DIVIDER));
            GUILayout.Label(msg.ExtraInfo, GUILayout.ExpandWidth(true));

            GUILayout.EndHorizontal();
        }

        #endregion

        #region Voice Monitor View

        private void DrawVoiceMonitorControls()
        {
            GUILayout.BeginHorizontal();

            // 搜索过滤
            GUILayout.Label("Filter:", GUILayout.Width(45));
            _voiceFilter = EditorGUILayout.TextField(_voiceFilter, GUILayout.Width(200));
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                _voiceFilter = "";
                GUI.FocusControl(null);
            }

            GUILayout.Space(10);
            _voiceShowZero = GUILayout.Toggle(_voiceShowZero, "Show Inactive", GUILayout.Width(100));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Reset Peak", GUILayout.Width(80)))
            {
                var keys = _voices.Keys.ToArray();
                foreach (var key in keys)
                {
                    var v = _voices[key];
                    v.PeakCount = v.ActiveCount;
                    _voices[key] = v;
                }
            }

            if (GUILayout.Button("Clear All", GUILayout.Width(70)))
            {
                _voices.Clear();
            }

            GUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private void DrawVoiceMonitorHeader()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            DrawSortableHeader("Event Name", VoiceSortColumn.Name, 200);
            DrawSortableHeader("Type", VoiceSortColumn.Type, 85);
            DrawSortableHeader("Active", VoiceSortColumn.Active, 55);
            DrawSortableHeader("Peak", VoiceSortColumn.Peak, 50);
            DrawSortableHeader("Total", VoiceSortColumn.Total, 50);
            GUILayout.Label("Volume", EditorStyles.miniLabel, GUILayout.Width(50));
            GUILayout.Label("Clip", EditorStyles.miniLabel, GUILayout.Width(150));
            GUILayout.Label("GameObject", EditorStyles.miniLabel, GUILayout.Width(120));
            GUILayout.Label("Position", EditorStyles.miniLabel, GUILayout.Width(130));
            GUILayout.Label("", GUILayout.ExpandWidth(true)); // bar area

            GUILayout.EndHorizontal();
        }

        private void DrawSortableHeader(string label, VoiceSortColumn column, int width)
        {
            string arrow = _voiceSortColumn == column ? (_voiceSortDescending ? " ▼" : " ▲") : "";
            if (GUILayout.Button(label + arrow, EditorStyles.miniLabel, GUILayout.Width(width)))
            {
                if (_voiceSortColumn == column)
                    _voiceSortDescending = !_voiceSortDescending;
                else
                {
                    _voiceSortColumn = column;
                    _voiceSortDescending = true;
                }
            }
        }

        private void DrawVoiceMonitor()
        {
            // 排序
            var sorted = _voices.ToList();

            // 过滤
            if (!string.IsNullOrEmpty(_voiceFilter))
                sorted = sorted.Where(kv =>
                    kv.Key.IndexOf(_voiceFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (!_voiceShowZero)
                sorted = sorted.Where(kv => kv.Value.ActiveCount > 0).ToList();

            // 排序
            switch (_voiceSortColumn)
            {
                case VoiceSortColumn.Name:
                    sorted.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
                    break;
                case VoiceSortColumn.Active:
                    sorted.Sort((a, b) => a.Value.ActiveCount.CompareTo(b.Value.ActiveCount));
                    break;
                case VoiceSortColumn.Peak:
                    sorted.Sort((a, b) => a.Value.PeakCount.CompareTo(b.Value.PeakCount));
                    break;
                case VoiceSortColumn.Total:
                    sorted.Sort((a, b) => a.Value.TotalPlays.CompareTo(b.Value.TotalPlays));
                    break;
                case VoiceSortColumn.Type:
                    sorted.Sort((a, b) => a.Value.ObjectType.CompareTo(b.Value.ObjectType));
                    break;
            }
            if (_voiceSortDescending) sorted.Reverse();

            // 计算 bar 最大值
            int maxActive = 1;
            foreach (var kv in sorted)
                if (kv.Value.PeakCount > maxActive) maxActive = kv.Value.PeakCount;

            using (var scrollView = new EditorGUILayout.ScrollViewScope(_voiceScrollPosition))
            {
                _voiceScrollPosition = scrollView.scrollPosition;

                foreach (var kv in sorted)
                {
                    DrawVoiceRow(kv.Key, kv.Value, maxActive);
                }

                if (sorted.Count == 0)
                {
                    GUILayout.Space(20);
                    GUILayout.Label("No active voices. Play audio to see voice counts here.",
                        EditorStyles.centeredGreyMiniLabel);
                }
            }
        }

        private void DrawVoiceRow(string eventName, VoiceEntry entry, int maxForBar)
        {
            GUILayout.BeginHorizontal();

            // Event Name
            GUI.color = GetTypeColor(entry.ObjectType);
            GUILayout.Label(eventName, GUILayout.Width(200));

            // Type
            GUILayout.Label(entry.ObjectType.ToString(), GUILayout.Width(85));
            GUI.color = Color.white;

            // Active count (highlight if > 0; yellow + [V] if virtualized)
            if (entry.IsVirtualized)
                GUI.color = ColorWarning;
            else if (entry.ActiveCount > 0)
                GUI.color = ColorPlay;
            string activeLabel = entry.IsVirtualized
                ? $"{entry.ActiveCount} [V]"
                : entry.ActiveCount.ToString();
            GUILayout.Label(activeLabel, GUILayout.Width(55));
            GUI.color = Color.white;

            // Peak
            GUI.color = entry.PeakCount > 1 ? ColorWarning : Color.white;
            GUILayout.Label(entry.PeakCount.ToString(), GUILayout.Width(50));
            GUI.color = Color.white;

            // Total
            GUILayout.Label(entry.TotalPlays.ToString(), GUILayout.Width(50));

            // Volume
            GUILayout.Label(entry.LastVolume.ToString("F2"), GUILayout.Width(50));

            // Last Clip
            GUILayout.Label(entry.LastClip ?? "", GUILayout.Width(150));

            // GameObject (clickable)
            GUI.color = Color.white;
            if (GUILayout.Button(entry.LastGameObjectName ?? "", GUI.skin.label, GUILayout.Width(120)))
            {
                if (entry.LastGameObject != null)
                {
                    Selection.activeGameObject = entry.LastGameObject;
                    EditorGUIUtility.PingObject(entry.LastGameObject);
                }
            }

            // Position
            var pos = entry.LastPosition;
            GUILayout.Label($"({pos.x:F1}, {pos.y:F1}, {pos.z:F1})", GUILayout.Width(130));

            // Visual bar
            Rect barRect = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                // Background
                EditorGUI.DrawRect(barRect, ColorBarBg);

                // Peak bar
                if (entry.PeakCount > 0)
                {
                    float peakWidth = barRect.width * ((float)entry.PeakCount / maxForBar);
                    EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, peakWidth, barRect.height), ColorBarPeak);
                }

                // Active bar
                if (entry.ActiveCount > 0)
                {
                    float activeWidth = barRect.width * ((float)entry.ActiveCount / maxForBar);
                    EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, activeWidth, barRect.height), ColorBarFill);
                }
            }

            GUILayout.EndHorizontal();
        }

        #endregion

        #endregion

        #region Helpers
        private bool FilterMessage(AudioProfilerMessage msg)
        {
            // Text filter
            if (!string.IsNullOrEmpty(_nameFilter))
            {
                bool match = msg.AudioName.IndexOf(_nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             msg.ClipName.IndexOf(_nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             msg.GameObjectName.IndexOf(_nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             msg.ExtraInfo.IndexOf(_nameFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match) return false;
            }

            // Severity filter
            switch (msg.Severity)
            {
                case AudioSeverity.Info: if (!_includeInfo) return false; break;
                case AudioSeverity.Warning: if (!_includeWarning) return false; break;
                case AudioSeverity.Error: if (!_includeError) return false; break;
            }

            // Trigger filter
            switch (msg.TriggerFrom)
            {
                case AudioTriggerSource.Code: if (!_includeCode) return false; break;
                case AudioTriggerSource.AnimationEvent: if (!_includeAnimEvent) return false; break;
                case AudioTriggerSource.AutoDetect: if (!_includeAutoDetect) return false; break;
                case AudioTriggerSource.Inspector: if (!_includeInspector) return false; break;
                case AudioTriggerSource.Trigger: if (!_includeTrigger) return false; break;
                case AudioTriggerSource.Physics: if (!_includePhysics) return false; break;
                case AudioTriggerSource.Distance: if (!_includeDistance) return false; break;
            }

            // Type filter
            switch (msg.ObjectType)
            {
                case AudioObjectType.CharacterSFX: if (!_includeCharSFX) return false; break;
                case AudioObjectType.ItemSFX: if (!_includeItemSFX) return false; break;
                case AudioObjectType.PhysicsSFX: if (!_includePhysicsSFX) return false; break;
                case AudioObjectType.SceneMusic: if (!_includeMusic) return false; break;
                case AudioObjectType.UI: if (!_includeUI) return false; break;
                case AudioObjectType.Ambient: if (!_includeAmbient) return false; break;
                case AudioObjectType.Emitter: if (!_includeEmitter) return false; break;
                case AudioObjectType.Voice: if (!_includeVoice) return false; break;
                case AudioObjectType.System: if (!_includeSystem) return false; break;
            }

            return true;
        }

        private void SetAllFilters(bool value)
        {
            _includeInfo = value;
            _includeWarning = value;
            _includeError = value;
            _includeCode = value;
            _includeAnimEvent = value;
            _includeAutoDetect = value;
            _includeInspector = value;
            _includeTrigger = value;
            _includePhysics = value;
            _includeDistance = value;
            _includeCharSFX = value;
            _includeItemSFX = value;
            _includePhysicsSFX = value;
            _includeMusic = value;
            _includeUI = value;
            _includeAmbient = value;
            _includeEmitter = value;
            _includeVoice = value;
            _includeSystem = value;
        }

        private Color GetSeverityColor(AudioSeverity severity)
        {
            switch (severity)
            {
                case AudioSeverity.Info: return ColorInfo;
                case AudioSeverity.Warning: return ColorWarning;
                case AudioSeverity.Error: return ColorError;
                default: return Color.white;
            }
        }

        private Color GetTypeColor(AudioObjectType type)
        {
            switch (type)
            {
                case AudioObjectType.CharacterSFX: return ColorCharSFX;
                case AudioObjectType.ItemSFX: return ColorItemSFX;
                case AudioObjectType.PhysicsSFX: return ColorPhysicsSFX;
                case AudioObjectType.SceneMusic: return ColorMusic;
                case AudioObjectType.UI: return ColorUI;
                case AudioObjectType.Ambient: return ColorAmbient;
                case AudioObjectType.Emitter: return ColorEmitter;
                case AudioObjectType.Voice: return ColorVoice;
                case AudioObjectType.System: return ColorSystem;
                default: return Color.white;
            }
        }

        private Color GetActionColor(AudioAction action)
        {
            switch (action)
            {
                case AudioAction.Play: return ColorPlay;
                case AudioAction.Stop: return ColorStop;
                case AudioAction.Pause: return ColorPause;
                case AudioAction.Resume: return ColorResume;
                case AudioAction.Skip: return ColorSkip;
                case AudioAction.Load:
                case AudioAction.Unload: return ColorLoad;
                case AudioAction.Mute: return ColorMute;
                case AudioAction.Unmute: return ColorUnmute;
                default: return Color.white;
            }
        }
        #endregion
    }
}
