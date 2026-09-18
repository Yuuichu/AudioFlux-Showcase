using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Audio;

namespace AudioFlux
{
    /// <summary>
    /// 音乐状态管理器
    /// 管理交互音乐的状态切换、层控制和节拍同步
    /// </summary>
    public class MusicStateManager : MonoBehaviour
    {
        public static MusicStateManager Instance { get; private set; }
        private const float HALF_PI = Mathf.PI * 0.5f;

        private AudioMixerGroup _mixerGroup;

        // EventLibrary 支持
        private AudioEventLibrary _eventLibrary;
        private AudioEventRegistry _eventRegistry;

        // 当前状态
        private MusicState _currentState;
        private string _currentStateName;

        // AudioSource
        private AudioSource _mainSource;
        private AudioSource _transitionSource;  // 用于交叉淡入淡出
        private AudioSource _stingerSource;

        // 子系统
        private MusicBeatTracker _beatTracker;
        private MusicLayerController _layerController;

        // 独立轨道
        private Dictionary<string, IndependentTrackInstance> _independentTracks = new Dictionary<string, IndependentTrackInstance>();

        // 过渡状态
        private Coroutine _transitionCoroutine;
        private bool _isTransitioning;

        // 待执行的过渡
        private PendingTransition? _pendingTransition;

        // Switch Container 解析器
        private MusicSwitchResolver _switchResolver;

        // 事件
        public static event Action<string> OnMusicStateChanged;
        public static event Action<int, int> OnBeat;
        public static event Action<int> OnBar;

        /// <summary>
        /// 当前音乐状态名称
        /// </summary>
        public static string CurrentStateName => Instance?._currentStateName;

        /// <summary>
        /// 当前节拍信息
        /// </summary>
        public static BeatInfo CurrentBeatInfo => Instance?._beatTracker?.CurrentBeatInfo ?? default;

        /// <summary>
        /// 是否正在过渡
        /// </summary>
        public static bool IsTransitioning => Instance?._isTransitioning ?? false;

        #region 初始化

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[MusicStateManager] Duplicate instance detected on '{gameObject.name}', destroying duplicate to preserve the active music runtime.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        /// <summary>
        /// 从 EventLibrary 初始化音乐系统
        /// </summary>
        public static void InitializeFromLibrary(AudioEventLibrary library)
        {
            if (Instance == null)
            {
                Debug.LogWarning("[MusicStateManager] Instance not created");
                return;
            }

            Instance._eventLibrary = library;
            Instance._eventRegistry = null;
            Instance._mixerGroup = library?.MusicMixerGroup;
            Instance.SetupAudioSources();
            Instance.SetupSubsystems();

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Load,
                AudioTriggerSource.Code,
                "MusicStateManager",
                extraInfo: "从 EventLibrary 初始化音乐系统完成"
            );
        }

        /// <summary>
        /// 从 EventRegistry 初始化音乐系统
        /// </summary>
        public static void InitializeFromRegistry(AudioEventRegistry registry)
        {
            if (Instance == null)
            {
                Debug.LogWarning("[MusicStateManager] Instance not created");
                return;
            }

            Instance._eventRegistry = registry;
            Instance._eventLibrary = null;

            // MixerGroup 获取优先级：
            // 1. 已加载的 Music 分类库
            // 2. DefaultLibrary
            // 3. AudioMixerController
            var musicLibrary = registry?.GetLoadedLibrary(EventLibraryCategory.Music);
            if (musicLibrary?.MusicMixerGroup != null)
            {
                Instance._mixerGroup = musicLibrary.MusicMixerGroup;
            }
            else if (registry?.DefaultLibrary?.MusicMixerGroup != null)
            {
                Instance._mixerGroup = registry.DefaultLibrary.MusicMixerGroup;
            }
            else
            {
                // 从 AudioMixerController 获取（AudioSettings 中配置）
                Instance._mixerGroup = AudioMixerController.GetMixerGroup(AudioGroup.Music);
            }

            Instance.SetupAudioSources();
            Instance.SetupSubsystems();

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Load,
                AudioTriggerSource.Code,
                "MusicStateManager",
                extraInfo: "从 EventRegistry 初始化音乐系统完成"
            );
        }

        /// <summary>
        /// 初始化 Music Switch Container 系统
        /// </summary>
        public static void InitializeSwitchContainers(MusicSwitchContainerConfig config)
        {
            if (Instance == null)
            {
                Debug.LogWarning("[MusicStateManager] Instance not created, cannot init SwitchContainers");
                return;
            }

            if (config == null)
                return;

            // 清理旧 resolver（显式置 null 防止订阅泄漏）
            if (Instance._switchResolver != null)
            {
                Instance._switchResolver.Shutdown();
                Instance._switchResolver = null;
            }

            var resolver = new MusicSwitchResolver();
            resolver.Initialize(
                config,
                onResolvedTransition: (eventName, transition, crossFade) =>
                {
                    SetStateInternal(eventName, transition, crossFade);
                },
                onPlayStinger: stingerName =>
                {
                    PlayStinger(stingerName);
                }
            );

            Instance._switchResolver = resolver;

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Load,
                AudioTriggerSource.Code,
                "MusicStateManager",
                extraInfo: "Switch Container 系统初始化完成"
            );
        }

        /// <summary>
        /// 获取当前激活的 Switch Container 名称
        /// </summary>
        public static string ActiveContainerName => Instance?._switchResolver?.ActiveContainerName;

        /// <summary>
        /// 获取当前 Music Switch 值
        /// </summary>
        public static string CurrentMusicSwitchValue => Instance?._switchResolver?.CurrentEventName;

        /// <summary>
        /// 检查名称是否是 Switch Container
        /// </summary>
        public static bool IsSwitchContainer(string name)
        {
            return Instance?._switchResolver?.IsContainer(name) ?? false;
        }

        private void SetupAudioSources()
        {
            // 主音源
            _mainSource = CreateAudioSource("MusicMain");

            // 过渡音源
            _transitionSource = CreateAudioSource("MusicTransition");

            // Stinger 音源
            _stingerSource = CreateAudioSource("MusicStinger");
        }

        private AudioSource CreateAudioSource(string name)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(transform);

            var source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.priority = 0;  // 最高优先级

            if (_mixerGroup != null)
            {
                source.outputAudioMixerGroup = _mixerGroup;
            }

            return source;
        }

        private void ResetTransitionSource()
        {
            if (_transitionSource == null)
            {
                return;
            }

            _transitionSource.Stop();
            _transitionSource.clip = null;
            _transitionSource.time = 0f;
            _transitionSource.volume = 0f;
            _transitionSource.loop = false;
        }

        private void SetupSubsystems()
        {
            _beatTracker = new MusicBeatTracker();
            _beatTracker.OnBeat += HandleBeat;
            _beatTracker.OnBar += HandleBar;

            _layerController = new MusicLayerController();
            _layerController.Initialize(transform, _mixerGroup, this);
        }

        #endregion

        #region 状态切换

        /// <summary>
        /// 切换音乐状态
        /// 如果 stateName 匹配 Switch Container，则委托给 Resolver 处理
        /// </summary>
        public static void SetState(string stateName, MusicTransitionType? transition = null)
        {
            if (Instance == null)
                return;

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Play,
                AudioTriggerSource.Code,
                stateName,
                null,
                null,
                default,
                1f,
                extraInfo: transition.HasValue
                    ? $"SetState transition={transition.Value}"
                    : "SetState"
            );

            // 检查是否是 Switch Container
            if (Instance._switchResolver != null && Instance._switchResolver.IsContainer(stateName))
            {
                Instance._switchResolver.TryActivateContainer(stateName);
                return;
            }

            // 容器已激活时，非容器名的 MusicState 调用为 no-op
            // 音乐由 Switch 树形评估驱动，无需直接播放
            if (Instance._switchResolver != null && Instance._switchResolver.HasActiveContainer)
            {
                return;
            }

            // 无容器：直接播放（传统模式）
            SetStateInternal(stateName, transition);
        }

        /// <summary>
        /// 内部音乐状态切换（跳过容器检查）
        /// 供 MusicSwitchResolver 回调使用
        /// </summary>
        internal static void SetStateInternal(string stateName, MusicTransitionType? transition = null, float crossFadeDuration = -1f)
        {
            if (Instance == null)
                return;

            // 防御性检查：Shutdown 后 resolver 回调可能仍触发
            if (Instance._eventRegistry == null && Instance._eventLibrary == null)
                return;

            // 从 EventLibrary/Registry 获取状态
            var state = Instance.GetMusicStateFromLibrary(stateName);

            if (state == null)
            {
                AudioProfilerHelper.LogWarning(AudioObjectType.SceneMusic, stateName, "音乐状态未找到");
                return;
            }

            var transitionType = transition ?? state.DefaultTransition;

            // 如果指定了自定义 crossFade 时长且过渡类型是 CrossFade，覆盖状态的 FadeInTime
            if (crossFadeDuration >= 0f && transitionType == MusicTransitionType.CrossFade)
            {
                // 临时修改 state 的 FadeInTime（MusicState 是 class，注意副作用）
                // 为避免修改原始数据，仅在 TransitionToState 内使用 crossFadeDuration
                Instance.TransitionToStateWithDuration(state, transitionType, crossFadeDuration);
            }
            else
            {
                Instance.TransitionToState(state, transitionType);
            }
        }

        /// <summary>
        /// 停止音乐
        /// </summary>
        public static void Stop(float fadeOut = 1f)
        {
            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Stop,
                AudioTriggerSource.Code,
                Instance?._currentStateName ?? "Music",
                null,
                null,
                default,
                1f,
                extraInfo: $"MusicStateManager.Stop fadeOut={fadeOut}s"
            );
            Instance?.StopMusic(fadeOut);
        }

        /// <summary>
        /// 暂停音乐
        /// </summary>
        public static void Pause()
        {
            if (Instance == null) return;
            Instance._mainSource?.Pause();
            Instance._layerController?.PauseAll();
            Instance._beatTracker?.Stop();

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Pause,
                AudioTriggerSource.Code,
                Instance._currentStateName ?? "Music",
                Instance._mainSource?.clip?.name,
                extraInfo: "MusicStateManager paused"
            );
        }

        /// <summary>
        /// 恢复音乐
        /// </summary>
        public static void Resume()
        {
            if (Instance == null) return;
            Instance._mainSource?.UnPause();
            Instance._layerController?.ResumeAll();
            Instance._beatTracker?.Start();

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Resume,
                AudioTriggerSource.Code,
                Instance._currentStateName ?? "Music",
                Instance._mainSource?.clip?.name,
                extraInfo: "MusicStateManager resumed"
            );
        }

        /// <summary>
        /// 带自定义 CrossFade 时长的过渡（供 SwitchResolver 使用）
        /// </summary>
        private void TransitionToStateWithDuration(MusicState newState, MusicTransitionType transition, float crossFadeDuration)
        {
            if (_currentStateName == newState.StateName)
                return;

            if (transition == MusicTransitionType.CrossFade)
            {
                ExecuteTransition(newState, crossFadeDuration);
            }
            else
            {
                TransitionToState(newState, transition);
            }
        }

        private void TransitionToState(MusicState newState, MusicTransitionType transition)
        {
            if (_currentStateName == newState.StateName)
                return;

            switch (transition)
            {
                case MusicTransitionType.Immediate:
                    ExecuteTransition(newState, 0f);
                    break;

                case MusicTransitionType.CrossFade:
                    ExecuteTransition(newState, newState.FadeInTime);
                    break;

                case MusicTransitionType.NextBeat:
                    ScheduleTransition(newState, _beatTracker?.GetTimeToNextBeat() ?? 0f);
                    break;

                case MusicTransitionType.NextBar:
                    ScheduleTransition(newState, _beatTracker?.GetTimeToNextBar() ?? 0f);
                    break;

                case MusicTransitionType.NextSyncPoint:
                    ScheduleTransitionAtSyncPoint(newState);
                    break;

                case MusicTransitionType.EndOfTrack:
                    ScheduleTransitionAtEnd(newState);
                    break;
            }
        }

        private void ExecuteTransition(MusicState newState, float crossFadeDuration)
        {
            if (_transitionCoroutine != null)
            {
                StopCoroutine(_transitionCoroutine);
                _transitionCoroutine = null;
                ResetTransitionSource();
            }

            _transitionCoroutine = StartCoroutine(TransitionCoroutine(newState, crossFadeDuration));
        }

        private IEnumerator TransitionCoroutine(MusicState newState, float crossFadeDuration)
        {
            _isTransitioning = true;

            // 交叉淡入淡出
            if (crossFadeDuration > 0 && _mainSource.isPlaying)
            {
                // 将当前音乐移到过渡音源
                _transitionSource.clip = _mainSource.clip;
                _transitionSource.time = _mainSource.time;
                _transitionSource.volume = _mainSource.volume;
                _transitionSource.loop = _mainSource.loop;
                _transitionSource.Play();

                // 开始播放新音乐（MainTrack 为 null 时视为静音状态，只淡出旧音乐）
                _mainSource.clip = newState.MainTrack;
                _mainSource.volume = 0f;
                _mainSource.loop = newState.Loop;
                if (newState.MainTrack != null)
                    _mainSource.Play();

                // 等功率交叉淡入淡出（sin/cos 曲线，避免线性 Lerp 的中段音量凹陷）
                float elapsed = 0f;
                float oldVolume = _transitionSource.volume;
                float newVolume = newState.Volume;

                while (elapsed < crossFadeDuration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    float t = Mathf.Clamp01(elapsed / crossFadeDuration);

                    _transitionSource.volume = oldVolume * Mathf.Cos(t * HALF_PI);
                    _mainSource.volume = newVolume * Mathf.Sin(t * HALF_PI);

                    yield return null;
                }

                // 确保最终音量精确
                _mainSource.volume = newVolume;
                ResetTransitionSource();
            }
            else
            {
                // 直接切换（MainTrack 为 null 时停止播放，实现静音）
                ResetTransitionSource();
                _mainSource.Stop();
                _mainSource.clip = newState.MainTrack;
                _mainSource.volume = newState.Volume;
                _mainSource.loop = newState.Loop;
                if (newState.MainTrack != null)
                    _mainSource.Play();
            }

            // 更新状态
            var oldState = _currentStateName;
            _currentState = newState;
            _currentStateName = newState.StateName;

            // 设置节拍追踪
            _beatTracker.Setup(_mainSource, newState.BPM, newState.BeatsPerBar);
            _beatTracker.Start();

            // 设置层
            _layerController.SetupLayers(newState, _mainSource);

            // 设置独立轨道
            SetupIndependentTracks(newState);

            _isTransitioning = false;

            // 触发事件
            OnMusicStateChanged?.Invoke(newState.StateName);

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Play,
                AudioTriggerSource.Code,
                newState.StateName,
                newState.MainTrack?.name,
                extraInfo: $"From={oldState}, Transition={crossFadeDuration}s"
            );
        }

        private void ScheduleTransition(MusicState newState, float delay)
        {
            _pendingTransition = new PendingTransition
            {
                State = newState,
                TriggerTime = Time.time + delay
            };
        }

        private void ScheduleTransitionAtSyncPoint(MusicState newState)
        {
            // 找到下一个同步点
            if (_currentState == null)
            {
                ExecuteTransition(newState, newState.FadeInTime);
                return;
            }

            var info = _beatTracker.GetBeatInfo();
            int nextBar = info.CurrentBar + 1;

            // 查找最近的同步点
            while (!_currentState.IsSyncBar(nextBar) && nextBar < info.CurrentBar + 100)
            {
                nextBar++;
            }

            float delay = _beatTracker.GetTimeToBar(nextBar);
            ScheduleTransition(newState, delay);
        }

        private void ScheduleTransitionAtEnd(MusicState newState)
        {
            if (_mainSource == null || _mainSource.clip == null)
            {
                ExecuteTransition(newState, newState.FadeInTime);
                return;
            }

            float remaining = _mainSource.clip.length - _mainSource.time;
            ScheduleTransition(newState, remaining);
        }

        private void StopMusic(float fadeOut)
        {
            string stateName = _currentStateName;
            string clipName = _mainSource?.clip?.name;

            if (_transitionCoroutine != null)
            {
                StopCoroutine(_transitionCoroutine);
                _transitionCoroutine = null;
            }

            _pendingTransition = null;
            ResetTransitionSource();
            _beatTracker.Stop();
            _layerController.StopAll(fadeOut);

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Stop,
                AudioTriggerSource.Code,
                stateName ?? "Music",
                clipName,
                extraInfo: fadeOut > 0 ? $"FadeOut={fadeOut}s" : "Immediate"
            );

            if (fadeOut > 0)
            {
                StartCoroutine(FadeOutAndStop(fadeOut));
            }
            else
            {
                ResetTransitionSource();
                _mainSource.Stop();
                _currentState = null;
                _currentStateName = null;
            }
        }

        private IEnumerator FadeOutAndStop(float duration)
        {
            float startVolume = _mainSource.volume;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                _mainSource.volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
                yield return null;
            }

            _mainSource.Stop();
            ResetTransitionSource();
            _mainSource.volume = startVolume;
            _currentState = null;
            _currentStateName = null;
        }

        #endregion

        #region 层控制

        /// <summary>
        /// 设置音乐层启用状态
        /// </summary>
        public static void SetLayer(string layerName, bool enabled, float? fadeTime = null)
        {
            Instance?._layerController?.SetLayerEnabled(layerName, enabled, fadeTime);
        }

        /// <summary>
        /// 设置音乐层音量
        /// </summary>
        public static void SetLayerVolume(string layerName, float volume)
        {
            Instance?._layerController?.SetLayerVolume(layerName, volume);
        }

        /// <summary>
        /// 批量设置层状态
        /// </summary>
        public static void SetLayers(Dictionary<string, bool> layerStates, float fadeTime = 0.5f)
        {
            Instance?._layerController?.SetLayers(layerStates, fadeTime);
        }

        /// <summary>
        /// 获取层是否启用
        /// </summary>
        public static bool IsLayerEnabled(string layerName)
        {
            return Instance?._layerController?.IsLayerEnabled(layerName) ?? false;
        }

        /// <summary>
        /// 获取所有层名称
        /// </summary>
        public static string[] GetLayerNames()
        {
            return Instance?._layerController?.GetLayerNames() ?? Array.Empty<string>();
        }

        #endregion

        #region Stinger

        /// <summary>
        /// 播放 Stinger
        /// </summary>
        public static void PlayStinger(string stingerName)
        {
            if (Instance == null)
                return;

            // 从 EventLibrary/Registry 获取 Stinger
            var stinger = Instance.GetStingerFromLibrary(stingerName);

            if (stinger == null)
            {
                AudioProfilerHelper.LogWarning(AudioObjectType.SceneMusic, stingerName, "Stinger 未找到");
                return;
            }

            Instance.PlayStingerInternal(stinger);
        }

        private void PlayStingerInternal(StingerEntry stinger)
        {
            _stingerSource.clip = stinger.Clip;
            _stingerSource.volume = stinger.Volume;
            _stingerSource.Play();

            if (stinger.DuckMainMusic)
            {
                StartCoroutine(DuckMusicCoroutine(stinger));
            }

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.SceneMusic,
                AudioAction.Play,
                AudioTriggerSource.Code,
                stinger.StingerName,
                stinger.Clip?.name,
                extraInfo: "Stinger"
            );
        }

        private IEnumerator DuckMusicCoroutine(StingerEntry stinger)
        {
            float originalVolume = _mainSource.volume;
            float targetVolume = originalVolume * stinger.DuckVolume;

            // 淡入 Duck
            float elapsed = 0f;
            while (elapsed < stinger.DuckFadeTime)
            {
                elapsed += Time.unscaledDeltaTime;
                _mainSource.volume = Mathf.Lerp(originalVolume, targetVolume, elapsed / stinger.DuckFadeTime);
                yield return null;
            }

            // 等待 Stinger 播放完成
            yield return new WaitForSecondsRealtime(stinger.Clip.length - stinger.DuckFadeTime);

            // 淡出 Duck
            elapsed = 0f;
            while (elapsed < stinger.DuckFadeTime)
            {
                elapsed += Time.unscaledDeltaTime;
                _mainSource.volume = Mathf.Lerp(targetVolume, originalVolume, elapsed / stinger.DuckFadeTime);
                yield return null;
            }

            _mainSource.volume = originalVolume;
        }

        #endregion

        #region 生命周期

        private void Update()
        {
            // 更新节拍追踪
            _beatTracker?.Update();

            // 检查待执行的过渡
            if (_pendingTransition.HasValue)
            {
                if (Time.time >= _pendingTransition.Value.TriggerTime)
                {
                    var pending = _pendingTransition.Value;
                    _pendingTransition = null;
                    ExecuteTransition(pending.State, pending.State.FadeInTime);
                }
            }
        }

        private void HandleBeat(int beat, int bar)
        {
            OnBeat?.Invoke(beat, bar);
        }

        private void HandleBar(int bar)
        {
            OnBar?.Invoke(bar);
        }

        #endregion

        #region EventLibrary 适配层

        /// <summary>
        /// 从 EventLibrary 获取 MusicState（适配层）
        /// </summary>
        private MusicState GetMusicStateFromLibrary(string stateName)
        {
            MusicEventEntry entry = null;

            if (_eventRegistry != null)
                entry = _eventRegistry.GetMusicEvent(stateName);
            else if (_eventLibrary != null)
                entry = _eventLibrary.GetMusicEvent(stateName);

            if (entry == null) return null;

            // 转换为 MusicState（兼容现有逻辑）
            return new MusicState
            {
                StateName = entry.EventName,
                Description = entry.Description,
                MainTrack = entry.MainTrack,
                Loop = entry.Loop,
                Layers = entry.Layers?.Select(l => new MusicLayer
                {
                    LayerName = l.LayerName,
                    Clip = l.Clip,
                    EnabledByDefault = l.EnabledByDefault,
                    Volume = l.Volume,
                    FadeInTime = l.FadeInTime,
                    FadeOutTime = l.FadeOutTime
                }).ToArray(),
                IndependentTracks = entry.IndependentTracks?.Select(t => new IndependentMusicTrack
                {
                    TrackName = t.TrackName,
                    Clip = t.Clip,
                    EnabledByDefault = t.EnabledByDefault,
                    Loop = t.Loop,
                    Volume = t.Volume,
                    FadeInTime = t.FadeInTime,
                    FadeOutTime = t.FadeOutTime,
                    Priority = t.Priority
                }).ToArray(),
                BPM = entry.BPM,
                BeatsPerBar = entry.BeatsPerBar,
                SyncBars = entry.SyncBars,
                FadeInTime = entry.FadeInTime,
                FadeOutTime = entry.FadeOutTime,
                DefaultTransition = entry.DefaultTransition,
                Volume = entry.Volume
            };
        }

        /// <summary>
        /// 从 EventLibrary 获取 StingerEntry（适配层）
        /// </summary>
        private StingerEntry GetStingerFromLibrary(string stingerName)
        {
            StingerEventEntry entry = null;

            if (_eventRegistry != null)
                entry = _eventRegistry.GetStinger(stingerName);
            else if (_eventLibrary != null)
                entry = _eventLibrary.GetStinger(stingerName);

            if (entry == null) return null;

            return new StingerEntry
            {
                StingerName = entry.EventName,
                Clip = entry.Clip,
                Volume = entry.Volume,
                DuckMainMusic = entry.DuckMainMusic,
                DuckVolume = entry.DuckVolume,
                DuckFadeTime = entry.DuckFadeTime
            };
        }

        #endregion

        private struct PendingTransition
        {
            public MusicState State;
            public float TriggerTime;
        }

        /// <summary>
        /// 独立轨道实例
        /// </summary>
        private class IndependentTrackInstance
        {
            public string TrackName;
            public AudioSource Source;
            public IndependentMusicTrack Config;
            public float TargetVolume;
            public bool IsEnabled;
            public Coroutine FadeCoroutine;
        }

        #region 独立轨道控制

        /// <summary>
        /// 设置独立音乐轨道启用状态
        /// </summary>
        /// <param name="trackName">轨道名称</param>
        /// <param name="enabled">是否启用</param>
        /// <param name="fadeTime">淡入淡出时间（可选，默认使用配置值）</param>
        public static void SetTrack(string trackName, bool enabled, float? fadeTime = null)
        {
            Instance?.SetTrackInternal(trackName, enabled, fadeTime);
        }

        /// <summary>
        /// 设置独立轨道音量
        /// </summary>
        /// <param name="trackName">轨道名称</param>
        /// <param name="volume">音量 0-1</param>
        public static void SetTrackVolume(string trackName, float volume)
        {
            Instance?.SetTrackVolumeInternal(trackName, volume);
        }

        /// <summary>
        /// 获取独立轨道是否启用
        /// </summary>
        public static bool IsTrackEnabled(string trackName)
        {
            if (Instance == null || !Instance._independentTracks.TryGetValue(trackName, out var track))
                return false;
            return track.IsEnabled;
        }

        /// <summary>
        /// 获取所有独立轨道名称
        /// </summary>
        public static string[] GetTrackNames()
        {
            if (Instance == null)
                return Array.Empty<string>();

            var names = new string[Instance._independentTracks.Count];
            int i = 0;
            foreach (var key in Instance._independentTracks.Keys)
            {
                names[i++] = key;
            }
            return names;
        }

        private void SetTrackInternal(string trackName, bool enabled, float? fadeTime)
        {
            if (!_independentTracks.TryGetValue(trackName, out var track))
            {
                AudioProfilerHelper.LogWarning(AudioObjectType.SceneMusic, trackName, "独立轨道未找到");
                return;
            }

            if (track.IsEnabled == enabled)
                return;

            track.IsEnabled = enabled;

            float fade = fadeTime ?? (enabled ? track.Config.FadeInTime : track.Config.FadeOutTime);

            if (track.FadeCoroutine != null)
            {
                StopCoroutine(track.FadeCoroutine);
                track.FadeCoroutine = null;
            }

            if (enabled)
            {
                // 启动播放
                if (!track.Source.isPlaying)
                {
                    track.Source.volume = 0f;
                    track.Source.Play();
                }

                track.FadeCoroutine = StartCoroutine(FadeTrackCoroutine(track, track.TargetVolume, fade));

                AudioProfilerHelper.Log(
                    AudioSeverity.Info,
                    AudioObjectType.SceneMusic,
                    AudioAction.Play,
                    AudioTriggerSource.Code,
                    trackName,
                    track.Config.Clip?.name,
                    extraInfo: $"IndependentTrack enabled, fade={fade}s"
                );
            }
            else
            {
                // 淡出停止
                track.FadeCoroutine = StartCoroutine(FadeTrackAndStopCoroutine(track, fade));

                AudioProfilerHelper.Log(
                    AudioSeverity.Info,
                    AudioObjectType.SceneMusic,
                    AudioAction.Stop,
                    AudioTriggerSource.Code,
                    trackName,
                    track.Config.Clip?.name,
                    extraInfo: $"IndependentTrack disabled, fade={fade}s"
                );
            }
        }

        private void SetTrackVolumeInternal(string trackName, float volume)
        {
            if (!_independentTracks.TryGetValue(trackName, out var track))
                return;

            track.TargetVolume = Mathf.Clamp01(volume);

            if (track.IsEnabled && track.Source != null)
            {
                track.Source.volume = track.TargetVolume;
            }
        }

        private void SetupIndependentTracks(MusicState state)
        {
            // 停止所有现有独立轨道
            StopAllIndependentTracks(0f);
            _independentTracks.Clear();

            if (state.IndependentTracks == null || state.IndependentTracks.Length == 0)
                return;

            foreach (var trackConfig in state.IndependentTracks)
            {
                if (string.IsNullOrEmpty(trackConfig.TrackName) || trackConfig.Clip == null)
                    continue;

                var source = CreateAudioSource($"IndependentTrack_{trackConfig.TrackName}");
                source.clip = trackConfig.Clip;
                source.loop = trackConfig.Loop;
                source.priority = trackConfig.Priority;
                source.volume = 0f;

                var instance = new IndependentTrackInstance
                {
                    TrackName = trackConfig.TrackName,
                    Source = source,
                    Config = trackConfig,
                    TargetVolume = trackConfig.Volume,
                    IsEnabled = false,
                    FadeCoroutine = null
                };

                _independentTracks[trackConfig.TrackName] = instance;

                // 如果默认启用，立即播放
                if (trackConfig.EnabledByDefault)
                {
                    SetTrackInternal(trackConfig.TrackName, true, trackConfig.FadeInTime);
                }
            }
        }

        private void StopAllIndependentTracks(float fadeOut)
        {
            foreach (var track in _independentTracks.Values)
            {
                if (track.FadeCoroutine != null)
                {
                    StopCoroutine(track.FadeCoroutine);
                    track.FadeCoroutine = null;
                }

                if (track.Source != null)
                {
                    if (fadeOut > 0 && track.Source.isPlaying)
                    {
                        // 启动淡出协程，完成后销毁 GameObject
                        StartCoroutine(FadeTrackAndDestroyCoroutine(track, fadeOut));
                    }
                    else
                    {
                        track.Source.Stop();
                        Destroy(track.Source.gameObject);
                    }
                }
            }
        }

        private IEnumerator FadeTrackCoroutine(IndependentTrackInstance track, float targetVolume, float duration)
        {
            if (track.Source == null)
                yield break;

            float startVolume = track.Source.volume;
            float elapsed = 0f;

            while (elapsed < duration && track.Source != null)
            {
                elapsed += Time.unscaledDeltaTime;
                track.Source.volume = Mathf.Lerp(startVolume, targetVolume, elapsed / duration);
                yield return null;
            }

            if (track.Source != null)
                track.Source.volume = targetVolume;

            track.FadeCoroutine = null;
        }

        private IEnumerator FadeTrackAndStopCoroutine(IndependentTrackInstance track, float duration)
        {
            if (track.Source == null)
                yield break;

            float startVolume = track.Source.volume;
            float elapsed = 0f;

            while (elapsed < duration && track.Source != null)
            {
                elapsed += Time.unscaledDeltaTime;
                track.Source.volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
                yield return null;
            }

            if (track.Source != null)
            {
                track.Source.Stop();
            }

            track.FadeCoroutine = null;
        }

        /// <summary>
        /// 淡出轨道并销毁 GameObject（用于 StopAllIndependentTracks）
        /// </summary>
        private IEnumerator FadeTrackAndDestroyCoroutine(IndependentTrackInstance track, float duration)
        {
            if (track.Source == null)
                yield break;

            float startVolume = track.Source.volume;
            float elapsed = 0f;

            while (elapsed < duration && track.Source != null)
            {
                elapsed += Time.unscaledDeltaTime;
                track.Source.volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
                yield return null;
            }

            // 淡出完成后销毁 GameObject，防止内存泄漏
            if (track.Source != null)
            {
                track.Source.Stop();
                Destroy(track.Source.gameObject);
            }

            track.FadeCoroutine = null;
        }

        #endregion

        private void OnDestroy()
        {
            if (Instance != this)
            {
                return;
            }

            Debug.Log($"[MusicStateManager] Active instance '{gameObject.name}' destroyed, shutting down music runtime.");

            // 停止过渡协程，防止 Shutdown 后仍回调
            if (_transitionCoroutine != null)
            {
                StopCoroutine(_transitionCoroutine);
                _transitionCoroutine = null;
            }
            _pendingTransition = null;
            _isTransitioning = false;

            // 关闭 SwitchResolver（取消事件订阅）
            _switchResolver?.Shutdown();
            _switchResolver = null;

            _beatTracker?.ClearEvents();
            _layerController?.ClearAllLayers();

            // 清理独立轨道
            StopAllIndependentTracks(0f);
            _independentTracks.Clear();

            if (_mainSource != null) _mainSource.Stop();
            ResetTransitionSource();
            if (_stingerSource != null) _stingerSource.Stop();

            // 清空数据源引用
            _eventRegistry = null;
            _eventLibrary = null;

            _currentState = null;
            _currentStateName = null;

            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
