using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

using AudioFlux;
namespace AudioFlux.Components
{
    /// <summary>
    /// 3D 环境音发射器 - 循环播放环境音
    /// 支持距离优化、随机播放间隔、多位置模式等功能
    /// </summary>
    [AddComponentMenu("AudioFlux/Audio Emitter")]
    public class AudioEmitter : MonoBehaviour
    {
        [Header("音频事件")]
        [FormerlySerializedAs("_eventConfigs")]
        [FormerlySerializedAs("_eventNames")]
        [SerializeField, Tooltip("AudioEvent 列表（配置几个播几个）")]
        private AudioEvent[] _audioEvents;

        [Header("播放控制")]
        [SerializeField, Tooltip("启动时自动播放")]
        private bool _playOnStart = true;

        [SerializeField, Tooltip("是否循环播放")]
        private bool _loop = true;

        [SerializeField, Range(-80f, 12f), Tooltip("增益 (dB), 0=不改变事件库音量, -6≈减半, +6≈翻倍")]
        private float _gain = 0f;

        [Header("随机播放")]
        [SerializeField, Tooltip("多事件播放模式\nAll=全部同时播放\nRandomOne=加权随机选一个\nShuffle=洗牌顺序逐个")]
        private EmitterPlayMode _playMode = EmitterPlayMode.All;

        [SerializeField, Tooltip("是否随机化起始位置")]
        private bool _randomizeStart = false;

        [SerializeField, Tooltip("最小播放间隔（0 = 连续播放）")]
        private float _minInterval = 0f;

        [SerializeField, Tooltip("最大播放间隔")]
        private float _maxInterval = 0f;

        [Header("位置模式")]
        [SerializeField, Tooltip("发声位置模式")]
        private MultiPositionType _positionMode = MultiPositionType.Simple;

        [SerializeField, Tooltip("位置更新频率")]
        private UpdateFrequency _updateFrequency = UpdateFrequency.Medium;

        [SerializeField, Tooltip("多位置点列表")]
        private List<PositionPoint> _positionPoints = new List<PositionPoint>();

        [SerializeField, Tooltip("ClosestPoint 模式使用的 Collider")]
        private Collider _closestPointCollider;

        [Header("距离优化")]
        [SerializeField, Tooltip("超出距离时暂停（距离 = 事件衰减范围 EventEntry.MaxDistance）")]
        private bool _pauseWhenFar = true;

        [SerializeField, Tooltip("距离检测间隔")]
        private float _distanceCheckInterval = 0.5f;

        [SerializeField, Tooltip("视口外时暂停播放")]
        private bool _pauseWhenInvisible = false;

        // 注：空间音频参数（SpatialBlend、Spread、SpreadCurve）已迁移至 EventEntry，
        // 由 SFXManager.ConfigureSourceFromEntry 统一应用，Emitter 不再单独设置。

        [Header("调试")]
        [SerializeField]
        private bool _debug = false;

        // Simple 模式状态（多事件各一个 handle）
        private readonly List<AudioHandle> _handles = new List<AudioHandle>(4);

        // Large 模式状态
        private List<LargeModeContext> _largeModeContexts;

        // ClosestPoint 模式状态（多事件各一个 context）
        private List<ClosestPointContext> _closestPointContexts;

        // 通用状态
        private bool _isPlaying;
        private bool _isPaused;
        private bool _isDistanceMuted;  // 距离优化：超出范围时 Stop 释放资源，回到范围内 Recreate
        private float _lastDistanceCheck;
        private float _nextPlayTime;
        private int _frameCounter;

        // 缓存
        private float _cachedEventMaxDistance;  // 从 EventEntry.MaxDistance 缓存，用于距离优化 + Gizmo
        private Transform _listenerTransform;
        private float _lastListenerSearchTime = -10f;
        private Collider _cachedCollider;
        private Renderer _cachedRenderer;
        private const float LISTENER_SEARCH_COOLDOWN = 2f;

        // 逐事件操作用临时列表（实例字段，避免 static 帧内竞态）
        private readonly List<string> _tempAudioEvents = new List<string>(4);
        private readonly List<string> _activeAudioEvents = new List<string>(4);
        private readonly List<AudioEvent> _randomOneCandidates = new List<AudioEvent>(4);
        private bool _resolvedUseLoopPlayback = true;
        private bool _resolvedManagedByEventChain;

        // Emitter 级 Shuffle/Random 状态（用于 RandomOne/Shuffle 模式）
        private ShuffleBag _emitterShuffleBag;
        private StandardRandom _emitterStandardRandom;

        private void Start()
        {
            UpdateListenerReference();
            CacheComponents();

            if (_playOnStart)
            {
                Play();
            }
        }

        private void Update()
        {
            // 距离 + 可见性检测（仅在未被用户暂停时执行）
            if (_pauseWhenFar && _isPlaying && !_isPaused)
            {
                if (Time.time - _lastDistanceCheck > _distanceCheckInterval)
                {
                    CheckDistance();
                    _lastDistanceCheck = Time.time;
                }
            }

            // 距离静默时跳过所有播放逻辑
            if (_isDistanceMuted) return;

            // MultiPosition 模式：驱动 Manager Tick（内部有帧去重）
            if (_isPlaying && !_isPaused && _positionMode == MultiPositionType.MultiPosition)
            {
                MultiPositionManager.Tick();
                return; // MultiPosition 由 Manager 管理，不走下面的逻辑
            }

            // 间歇播放模式
            if (_isPlaying && !_isPaused && _resolvedUseLoopPlayback && !_resolvedManagedByEventChain && _positionMode != MultiPositionType.MultiPosition)
            {
                if (!IsAnyHandlePlaying())
                {
                    PlayLoop();
                }
            }

            if (_isPlaying && !_isPaused && !_resolvedUseLoopPlayback && _minInterval > 0f)
            {
                if (!IsAnyHandlePlaying() && Time.time >= _nextPlayTime)
                {
                    PlayOnce();
                    ScheduleNextPlay();
                }
            }

            // 位置更新（非 Simple 模式）
            if (_isPlaying && !_isPaused && _positionMode != MultiPositionType.Simple)
            {
                int freq = (int)_updateFrequency;
                // Never (999) 或未初始化 (0) 都不更新
                if (freq > 0 && freq < 999)
                {
                    _frameCounter++;
                    if (_frameCounter >= freq)
                    {
                        _frameCounter = 0;
                        UpdateEmitterPositions();
                    }
                }
            }
        }

        private void OnEnable()
        {
            if (_playOnStart && !_isPlaying)
            {
                Play();
            }
            else if (_isPaused)
            {
                Resume();
            }
        }

        private void OnDisable()
        {
            if (_isPlaying)
            {
                Pause();
            }
        }

        private void OnDestroy()
        {
            Stop(0f);
        }

        #region Public Properties

        public MultiPositionType PositionMode => _positionMode;
        public int PositionPointCount => _positionPoints?.Count ?? 0;
        public Collider ClosestPointCollider
        {
            get => _closestPointCollider;
            set => _closestPointCollider = value;
        }

        public AudioEvent[] AudioEvents => _audioEvents;
        public float MinInterval => _minInterval;
        public float MaxInterval => _maxInterval;
        public bool RandomizeStart => _randomizeStart;
        public float MaxDistance => _cachedEventMaxDistance > 0f ? _cachedEventMaxDistance : 50f;
        public bool Loop => _loop;

        /// <summary>
        /// 是否正在播放（距离静默时仍视为逻辑上在播放）
        /// </summary>
        public bool IsPlaying => _isPlaying && !_isPaused;

        /// <summary>
        /// 是否暂停中
        /// </summary>
        public bool IsPaused => _isPaused;

        /// <summary>
        /// 当前增益 (dB)
        /// </summary>
        public float Gain => _gain;

        #endregion

        #region Public Methods

        /// <summary>
        /// 开始播放
        /// </summary>
        public void Play()
        {
            if (_audioEvents == null || _audioEvents.Length == 0) return;
            if (_isPlaying) return;

            _isPlaying = true;
            _isPaused = false;

            CacheEventMaxDistance();
            UpdateListenerReference();

            if (_pauseWhenFar && !IsInRange())
            {
                _isDistanceMuted = true;

                AudioProfilerHelper.LogEmitter(
                    AudioAction.Mute, PrimaryEventName, gameObject, transform.position, _gain,
                    AudioTriggerSource.Distance, $"Play deferred (out of range) mode={_positionMode}");

                if (_debug)
                {
                    Debug.Log($"[AudioEmitter] {name}: Started distance-muted (out of range)");
                }
                return;
            }

            ResolveCurrentAudioEvents();
            if (_tempAudioEvents.Count == 0)
            {
                _activeAudioEvents.Clear();
                if (!_resolvedUseLoopPlayback && _minInterval > 0f)
                {
                    ScheduleNextPlay();
                }
                else
                {
                    _isPlaying = false;
                    _isPaused = false;
                }
                return;
            }

            PlayFromResolvedEvents();
        }

        /// <summary>
        /// 停止播放
        /// </summary>
        public void Stop(float fadeOut = 0.5f)
        {
            _isPlaying = false;
            _isPaused = false;
            _isDistanceMuted = false;

            // MultiPosition 模式：逐事件取消注册
            if (_positionMode == MultiPositionType.MultiPosition)
            {
                for (int i = 0; i < _activeAudioEvents.Count; i++)
                    MultiPositionManager.Unregister(_activeAudioEvents[i], this);
            }

            // 停止所有模式的 handle
            StopHandles(fadeOut);
            StopLargeModeHandles(fadeOut);
            StopClosestPointHandles(fadeOut);
            _activeAudioEvents.Clear();

            if (_debug)
            {
                Debug.Log($"[AudioEmitter] {name}: Stopped");
            }
        }

        /// <summary>
        /// 暂停播放
        /// </summary>
        public void Pause()
        {
            if (!_isPlaying || _isPaused) return;

            _isPaused = true;

            AudioProfilerHelper.LogEmitter(
                AudioAction.Pause, PrimaryEventName, gameObject, transform.position, _gain,
                extraInfo: _isDistanceMuted ? "Pause (already distance-muted)" : $"Pause mode={_positionMode}");

            // 如果已因距离静默（handle 已 Stop），无需再 Pause
            if (_isDistanceMuted)
            {
                if (_debug)
                    Debug.Log($"[AudioEmitter] {name}: Paused (already distance-muted)");
                return;
            }

            // MultiPosition 模式：逐事件从组中移除
            if (_positionMode == MultiPositionType.MultiPosition)
            {
                for (int i = 0; i < _activeAudioEvents.Count; i++)
                    MultiPositionManager.Unregister(_activeAudioEvents[i], this);
            }

            PauseHandles();
            PauseLargeModeHandles();
            PauseClosestPointHandles();

            if (_debug)
            {
                Debug.Log($"[AudioEmitter] {name}: Paused");
            }
        }

        /// <summary>
        /// 恢复播放
        /// </summary>
        public void Resume()
        {
            if (!_isPlaying || !_isPaused) return;

            _isPaused = false;

            AudioProfilerHelper.LogEmitter(
                AudioAction.Resume, PrimaryEventName, gameObject, transform.position, _gain,
                extraInfo: _isDistanceMuted ? "Resume (distance-muted, deferred)" : $"Resume mode={_positionMode}");

            // 如果因距离被静默，不立即恢复播放，等 CheckDistance 处理
            if (_isDistanceMuted) return;

            bool anyResumed = false;

            switch (_positionMode)
            {
                case MultiPositionType.Simple:
                    anyResumed = ResumeHandles();
                    break;

                case MultiPositionType.MultiPosition:
                    // 恢复当前已播放的事件；若无活动事件，再解析新一轮随机结果
                    if (_activeAudioEvents.Count == 0)
                        ResolveCurrentAudioEvents();

                    if (_activeAudioEvents.Count > 0)
                    {
                        for (int i = 0; i < _activeAudioEvents.Count; i++)
                        {
                            float vol = GetEventGainDb(_activeAudioEvents[i]);
                            MultiPositionManager.Register(_activeAudioEvents[i], this, vol, ResolveLoopPolicyForEvent(_activeAudioEvents[i]));
                        }
                        anyResumed = true;
                    }
                    else
                    {
                        anyResumed = ResumeHandles();
                    }
                    break;

                case MultiPositionType.Large:
                    anyResumed = ResumeLargeModeHandles();
                    break;

                case MultiPositionType.ClosestPoint:
                    anyResumed = ResumeClosestPointHandles();
                    break;
            }

            // handle 丢失时清理旧引用后重新播放
            if (!anyResumed)
            {
                StopHandles(0f);
                StopLargeModeHandles(0f);
                StopClosestPointHandles(0f);

                ResolveCurrentAudioEvents();
                if (_tempAudioEvents.Count == 0)
                {
                    _activeAudioEvents.Clear();
                    if (!_resolvedUseLoopPlayback && _minInterval > 0f)
                        ScheduleNextPlay();
                }
                else
                {
                    PlayFromResolvedEvents();
                }
            }

            if (_debug)
            {
                Debug.Log($"[AudioEmitter] {name}: Resumed");
            }
        }

        /// <summary>
        /// 设置增益 (dB)，运行时按比例调整所有活跃 handle
        /// </summary>
        public void SetGain(float gainDb)
        {
            float oldLinear = DbToLinear(_gain);
            _gain = Mathf.Clamp(gainDb, -80f, 12f);
            float newLinear = DbToLinear(_gain);

            float ratio = oldLinear > 0.0001f ? newLinear / oldLinear : newLinear;
            ApplyGainRatio(ratio);
        }

        #endregion

        #region Event Helpers

        private float GetEventGainDb(string eventName)
        {
            if (_audioEvents == null || string.IsNullOrEmpty(eventName))
                return _gain;

            for (int i = 0; i < _audioEvents.Length; i++)
            {
                if (_audioEvents[i] == null) continue;
                if (_audioEvents[i].EventName == eventName)
                    return _audioEvents[i].GetGainDb(_gain);
            }

            return _gain;
        }

        /// <summary>
        /// dB 转线性值 (0dB=1.0, -6dB≈0.5, -20dB=0.1)
        /// </summary>
        private static float DbToLinear(float db)
        {
            if (db <= -80f) return 0f;
            return Mathf.Pow(10f, db / 20f);
        }

        #endregion

        #region Playback Internal

        private AudioHandle PlayAtPosition(string eventName, Vector3 worldPos, float gainDb, bool loop)
        {
            var handle = Audio.Ambient(eventName, worldPos, gameObject);
            if (handle != AudioHandle.Empty)
            {
                // gain (dB) 叠加到事件库音量上（0dB = 不改变）
                if (handle.Source != null)
                {
                    handle.Source.volume *= DbToLinear(gainDb);

                    if (_randomizeStart && handle.Source.loop && handle.Source.clip != null)
                    {
                        handle.Source.time = Random.Range(0f, handle.Source.clip.length);
                    }
                }
            }
            return handle;
        }

        private void PlayLoop()
        {
            ResolveCurrentAudioEvents();
            if (_tempAudioEvents.Count == 0)
            {
                _activeAudioEvents.Clear();
                _isPlaying = false;
                _isPaused = false;
                return;
            }

            PlayAllEvents(_tempAudioEvents, true);
        }

        private void PlayOnce()
        {
            ResolveCurrentAudioEvents();
            if (_tempAudioEvents.Count == 0)
            {
                _activeAudioEvents.Clear();
                if (_minInterval > 0f)
                    ScheduleNextPlay();
                else
                {
                    _isPlaying = false;
                    _isPaused = false;
                }
                return;
            }

            PlayAllEvents(_tempAudioEvents, false);
        }

        /// <summary>
        /// 统一播放所有事件（所有位置模式共用）
        /// </summary>
        private void PlayAllEvents(List<string> eventNames, bool loop)
        {
            switch (_positionMode)
            {
                case MultiPositionType.Simple:
                    for (int i = 0; i < eventNames.Count; i++)
                    {
                        float vol = GetEventGainDb(eventNames[i]);
                        var handle = PlayAtPosition(eventNames[i], transform.position, vol, loop);
                        if (handle != AudioHandle.Empty)
                        {
                            // Simple 模式也需要跟随 emitter，避免运行时重定位后停在初始位置。
                            handle.On(transform);
                            _handles.Add(handle);
                        }
                    }
                    break;

                case MultiPositionType.Large:
                    if (_positionPoints == null || _positionPoints.Count == 0)
                    {
                        if (_debug)
                            Debug.LogWarning($"[AudioEmitter] {name}: Large mode has no position points, falling back to Simple");
                        for (int i = 0; i < eventNames.Count; i++)
                        {
                            float vol = GetEventGainDb(eventNames[i]);
                            var handle = PlayAtPosition(eventNames[i], transform.position, vol, loop);
                            if (handle != AudioHandle.Empty)
                            {
                                handle.On(transform);
                                _handles.Add(handle);
                            }
                        }
                        break;
                    }
                    if (_largeModeContexts == null)
                        _largeModeContexts = new List<LargeModeContext>();
                    else if (_largeModeContexts.Count > 0)
                        StopLargeModeHandles(0f);
                    for (int i = 0; i < eventNames.Count; i++)
                    {
                        float vol = GetEventGainDb(eventNames[i]);
                        PlayLargeMode(eventNames[i], vol);
                    }
                    break;

                case MultiPositionType.MultiPosition:
                    for (int i = 0; i < eventNames.Count; i++)
                    {
                        float vol = GetEventGainDb(eventNames[i]);
                        MultiPositionManager.Register(eventNames[i], this, vol, ResolveLoopPolicyForEvent(eventNames[i]));
                    }
                    break;

                case MultiPositionType.ClosestPoint:
                    if (GetEffectiveCollider() == null && _debug)
                        Debug.LogWarning($"[AudioEmitter] {name}: ClosestPoint mode requires a Collider, using transform.position");
                    var closestPos = GetClosestPointOnCollider();
                    var collider = GetEffectiveCollider();
                    if (_closestPointContexts == null)
                        _closestPointContexts = new List<ClosestPointContext>();
                    else if (_closestPointContexts.Count > 0)
                        StopClosestPointHandles(0f);
                    for (int i = 0; i < eventNames.Count; i++)
                    {
                        float vol = GetEventGainDb(eventNames[i]);
                        _closestPointContexts.Add(new ClosestPointContext
                        {
                            TargetCollider = collider,
                            Handle = PlayAtPosition(eventNames[i], closestPos, vol, loop),
                            LastClosestPoint = closestPos,
                            LastUpdateFrame = Time.frameCount
                        });
                    }
                    break;
            }
        }

        /// <summary>
        /// 为单个事件在所有 Large 位置点创建 handle（追加到 _largeModeContexts）
        /// 使用 GetBatchRandomClips 批量预分配 clip，避免多点共享 ShuffleBag 状态。
        /// 所有位置点使用 PlayScheduled(dspTime) 实现样本级同步起播（对齐 Wwise Multi-Position 行为）。
        /// </summary>
        private void PlayLargeMode(string eventName, float gainDb)
        {
            if (_positionPoints == null || _positionPoints.Count == 0) return;

            // 统计启用的位置点数
            int enabledCount = 0;
            for (int i = 0; i < _positionPoints.Count; i++)
                if (_positionPoints[i].Enabled) enabledCount++;

            if (enabledCount == 0) return;

            // Wwise Multi-Position 语义：单一 voice 空间化到多个位置
            // 所有位置点播放同一个 clip，在同一 DSP 时刻起播
            var entry = Audio.FindEntry(eventName);
            var clip = entry?.GetNextClip();

            if (clip == null)
            {
                AudioFluxRuntime.Logger.Warning($"[AudioEmitter] Large mode: no clip for '{eventName}'");
                return;
            }

            // Large 模式关键：所有位置点共享同一 pitch，否则不同播放速度导致越来越不同步
            float sharedPitch = entry.GetRandomizedPitch();
            // 稍微偏移到未来，确保所有 source 在同一 DSP buffer 边界精确起播
            double dspTime = UnityEngine.AudioSettings.dspTime + 0.05;

            for (int i = 0; i < _positionPoints.Count; i++)
            {
                var point = _positionPoints[i];
                if (!point.Enabled) continue;

                Vector3 worldPos = transform.TransformPoint(point.LocalPosition);
                float pointGainDb = gainDb + point.GainOffset;

                var handle = PlayAtPositionWithClip(eventName, clip, worldPos, pointGainDb, dspTime);

                // 覆盖 ConfigureSourceFromEntry 中的随机 pitch，保证所有点位同速播放
                if (handle != AudioHandle.Empty && handle.Source != null)
                    handle.Source.pitch = sharedPitch;

                _largeModeContexts.Add(new LargeModeContext
                {
                    Point = point,
                    Handle = handle,
                    WorldPosition = worldPos
                });
            }
        }

        /// <summary>
        /// 使用指定 AudioClip 在指定位置播放（Large 模式批量预分配用）
        /// </summary>
        /// <param name="scheduledDspTime">大于 0 时使用 PlayScheduled 实现多点同步起播</param>
        private AudioHandle PlayAtPositionWithClip(string eventName, AudioClip clip, Vector3 worldPos, float gainDb, double scheduledDspTime = 0)
        {
            var handle = Audio.AmbientWithClip(eventName, clip, worldPos, scheduledDspTime);
            if (handle != AudioHandle.Empty)
            {
                if (handle.Source != null)
                {
                    handle.Source.volume *= DbToLinear(gainDb);
                    // Large 模式不随机化起始位置：所有位置播同一 clip 同步起播
                }
            }
            return handle;
        }

        private void ScheduleNextPlay()
        {
            float interval = Random.Range(_minInterval, _maxInterval);
            _nextPlayTime = Time.time + interval;
        }


        #endregion

        #region Position Update

        private void UpdateEmitterPositions()
        {
            switch (_positionMode)
            {
                case MultiPositionType.Large:
                    if (_largeModeContexts == null) return;
                    for (int i = 0; i < _largeModeContexts.Count; i++)
                    {
                        var ctx = _largeModeContexts[i];
                        if (ctx.Handle == null || !ctx.Handle.IsPlaying) continue;
                        Vector3 newWorldPos = transform.TransformPoint(ctx.Point.LocalPosition);
                        ctx.WorldPosition = newWorldPos;
                        ctx.Handle.At(newWorldPos);
                    }
                    break;

                case MultiPositionType.MultiPosition:
                    // MultiPosition 由 MultiPositionManager 管理位置更新
                    break;

                case MultiPositionType.ClosestPoint:
                    if (_closestPointContexts != null)
                    {
                        Vector3 closestPos = GetClosestPointOnCollider();
                        for (int i = 0; i < _closestPointContexts.Count; i++)
                        {
                            var ctx = _closestPointContexts[i];
                            if (ctx?.Handle != null && ctx.Handle.IsPlaying)
                            {
                                ctx.LastClosestPoint = closestPos;
                                ctx.Handle.At(closestPos);
                                ctx.LastUpdateFrame = Time.frameCount;
                            }
                        }
                    }
                    break;
            }
        }

        #endregion

        #region Position Helpers

        private Vector3 GetClosestEnabledPointPosition()
        {
            if (_positionPoints == null || _positionPoints.Count == 0)
                return transform.position;

            Vector3 listenerPos = GetListenerPosition();
            Vector3 closest = transform.position;
            float minDistSq = float.MaxValue;

            for (int i = 0; i < _positionPoints.Count; i++)
            {
                if (!_positionPoints[i].Enabled) continue;

                Vector3 worldPos = transform.TransformPoint(_positionPoints[i].LocalPosition);
                float distSq = Vector3.SqrMagnitude(worldPos - listenerPos);
                if (distSq < minDistSq)
                {
                    minDistSq = distSq;
                    closest = worldPos;
                }
            }

            return closest;
        }

        private Vector3 GetClosestPointOnCollider()
        {
            var collider = GetEffectiveCollider();
            if (collider == null)
                return transform.position;

            return collider.ClosestPoint(GetListenerPosition());
        }

        private Collider GetEffectiveCollider()
        {
            if (_closestPointCollider != null)
                return _closestPointCollider;

            if (_cachedCollider == null)
                _cachedCollider = GetComponent<Collider>();

            return _cachedCollider;
        }

        private Vector3 GetListenerPosition()
        {
            if (_listenerTransform == null)
                UpdateListenerReference();

            return _listenerTransform != null
                ? _listenerTransform.position
                : Camera.main?.transform.position ?? Vector3.zero;
        }

        #endregion

        #region Stop / Pause / Resume Helpers

        private void StopHandles(float fadeOut)
        {
            for (int i = 0; i < _handles.Count; i++)
            {
                if (_handles[i] != null && _handles[i].IsPlaying)
                    _handles[i].Stop(fadeOut);
            }
            _handles.Clear();
        }

        private void StopLargeModeHandles(float fadeOut)
        {
            if (_largeModeContexts == null) return;

            for (int i = 0; i < _largeModeContexts.Count; i++)
            {
                // 使用 IsValid 而非 IsPlaying：PlayScheduled 调度后 50ms 内 isPlaying=false，
                // 但 source 已在队列中，必须 Stop() 取消调度，否则形成"幽灵播放"
                if (_largeModeContexts[i].Handle != null && _largeModeContexts[i].Handle.IsValid)
                    _largeModeContexts[i].Handle.Stop(fadeOut);
            }
            _largeModeContexts.Clear();
        }

        private void StopClosestPointHandles(float fadeOut)
        {
            if (_closestPointContexts == null) return;

            for (int i = 0; i < _closestPointContexts.Count; i++)
            {
                if (_closestPointContexts[i]?.Handle != null && _closestPointContexts[i].Handle.IsValid)
                    _closestPointContexts[i].Handle.Stop(fadeOut);
            }
            _closestPointContexts.Clear();
        }

        private void PauseHandles()
        {
            for (int i = 0; i < _handles.Count; i++)
            {
                if (_handles[i] != null && _handles[i].IsPlaying)
                    _handles[i].Pause();
            }
        }

        private void PauseLargeModeHandles()
        {
            if (_largeModeContexts == null) return;

            for (int i = 0; i < _largeModeContexts.Count; i++)
            {
                if (_largeModeContexts[i].Handle != null && _largeModeContexts[i].Handle.IsPlaying)
                    _largeModeContexts[i].Handle.Pause();
            }
        }

        private void PauseClosestPointHandles()
        {
            if (_closestPointContexts == null) return;

            for (int i = 0; i < _closestPointContexts.Count; i++)
            {
                if (_closestPointContexts[i]?.Handle != null && _closestPointContexts[i].Handle.IsPlaying)
                    _closestPointContexts[i].Handle.Pause();
            }
        }

        private bool ResumeHandles()
        {
            bool any = false;
            for (int i = 0; i < _handles.Count; i++)
            {
                if (_handles[i] != null && _handles[i].IsValid)
                {
                    _handles[i].Resume();
                    any = true;
                }
            }
            return any;
        }

        private bool ResumeLargeModeHandles()
        {
            if (_largeModeContexts == null || _largeModeContexts.Count == 0)
                return false;

            bool any = false;
            for (int i = 0; i < _largeModeContexts.Count; i++)
            {
                if (_largeModeContexts[i].Handle != null && _largeModeContexts[i].Handle.IsValid)
                {
                    _largeModeContexts[i].Handle.Resume();
                    any = true;
                }
            }
            return any;
        }

        private bool ResumeClosestPointHandles()
        {
            if (_closestPointContexts == null || _closestPointContexts.Count == 0)
                return false;

            bool any = false;
            for (int i = 0; i < _closestPointContexts.Count; i++)
            {
                if (_closestPointContexts[i]?.Handle != null && _closestPointContexts[i].Handle.IsValid)
                {
                    _closestPointContexts[i].Handle.Resume();
                    any = true;
                }
            }
            return any;
        }

        private void ApplyGainRatio(float ratio)
        {
            for (int i = 0; i < _handles.Count; i++)
            {
                if (_handles[i]?.Source != null && _handles[i].IsValid)
                    _handles[i].Source.volume *= ratio;
            }

            if (_largeModeContexts != null)
            {
                for (int i = 0; i < _largeModeContexts.Count; i++)
                {
                    if (_largeModeContexts[i].Handle?.Source != null)
                        _largeModeContexts[i].Handle.Source.volume *= ratio;
                }
            }

            if (_closestPointContexts != null)
            {
                for (int i = 0; i < _closestPointContexts.Count; i++)
                {
                    if (_closestPointContexts[i]?.Handle?.Source != null)
                        _closestPointContexts[i].Handle.Source.volume *= ratio;
                }
            }
        }

        private bool IsAnyHandlePlaying()
        {
            switch (_positionMode)
            {
                case MultiPositionType.Simple:
                    for (int i = 0; i < _handles.Count; i++)
                    {
                        if (_handles[i] != null && _handles[i].IsPlaying)
                            return true;
                    }
                    return false;

                case MultiPositionType.MultiPosition:
                    for (int i = 0; i < _activeAudioEvents.Count; i++)
                    {
                        if (MultiPositionManager.IsGroupPlaying(_activeAudioEvents[i]))
                            return true;
                    }
                    return false;

                case MultiPositionType.Large:
                    if (_largeModeContexts != null)
                    {
                        for (int i = 0; i < _largeModeContexts.Count; i++)
                        {
                            if (_largeModeContexts[i].IsPlaying)
                                return true;
                        }
                    }
                    return false;

                case MultiPositionType.ClosestPoint:
                    if (_closestPointContexts != null)
                    {
                        for (int i = 0; i < _closestPointContexts.Count; i++)
                        {
                            if (_closestPointContexts[i] != null && _closestPointContexts[i].IsPlaying)
                                return true;
                        }
                    }
                    return false;

                default:
                    return false;
            }
        }

        #endregion

        #region Distance & Visibility

        private void CheckDistance()
        {
            bool inRange = IsInRange();
            bool visible = !_pauseWhenInvisible || IsVisible();
            bool shouldPlay = inRange && visible;

            if (_debug && PrimaryEventName == "Amb_3D_Market_Crowd")
            {
                Vector3 listenerPos = _listenerTransform != null ? _listenerTransform.position : Vector3.zero;
                float maxDist = _cachedEventMaxDistance > 0f ? _cachedEventMaxDistance : 50f;
                Debug.Log(
                    $"[AudioFlow] phase=EmitterCheckDistance emitter={name} event={PrimaryEventName} shouldPlay={shouldPlay} inRange={inRange} visible={visible} isPlaying={_isPlaying} isPaused={_isPaused} distanceMuted={_isDistanceMuted} emitterPos={transform.position} listenerPos={listenerPos} maxDist={maxDist:F2}");
            }

            if (shouldPlay && _isDistanceMuted)
            {
                // 回到范围内：重新创建播放
                _isDistanceMuted = false;
                RestartPlayback();

                AudioProfilerHelper.LogEmitter(
                    AudioAction.Unmute, PrimaryEventName, gameObject, transform.position, _gain,
                    AudioTriggerSource.Distance, $"Back in range, restarting mode={_positionMode}");

                if (_debug)
                    Debug.Log($"[AudioEmitter] {name}: Back in range, restarting playback");
            }
            else if (!shouldPlay && !_isDistanceMuted)
            {
                // 超出范围：停止并释放 AudioSource 回池
                _isDistanceMuted = true;
                StopForDistance();

                AudioProfilerHelper.LogEmitter(
                    AudioAction.Mute, PrimaryEventName, gameObject, transform.position, _gain,
                    AudioTriggerSource.Distance, $"Out of range, stopped to free sources mode={_positionMode}");

                if (_debug)
                    Debug.Log($"[AudioEmitter] {name}: Out of range, stopped to free AudioSources");
            }
        }

        /// <summary>
        /// 距离优化：超出范围时停止所有 handle，释放 AudioSource 回池
        /// 不改变 _isPlaying 状态，仅设 _isDistanceMuted
        /// </summary>
        private void StopForDistance()
        {
            // MultiPosition 模式：逐事件从组中移除
            if (_positionMode == MultiPositionType.MultiPosition)
            {
                for (int i = 0; i < _activeAudioEvents.Count; i++)
                    MultiPositionManager.Unregister(_activeAudioEvents[i], this);
            }

            // 停止所有 handle（AudioSource 正确归还池）
            StopHandles(0f);
            StopLargeModeHandles(0f);
            StopClosestPointHandles(0f);
        }

        /// <summary>
        /// 距离优化：回到范围内时重新创建播放
        /// </summary>
        private void RestartPlayback()
        {
            if (_debug && PrimaryEventName == "Amb_3D_Market_Crowd")
            {
                Debug.Log(
                    $"[AudioFlow] phase=EmitterRestart emitter={name} event={PrimaryEventName} pos={transform.position}");
            }

            ResolveCurrentAudioEvents();
            if (_tempAudioEvents.Count == 0)
            {
                _activeAudioEvents.Clear();
                if (!_resolvedUseLoopPlayback && _minInterval > 0f)
                    ScheduleNextPlay();
                return;
            }

            PlayFromResolvedEvents();
        }

        private bool IsInRange()
        {
            if (_listenerTransform == null)
            {
                UpdateListenerReference();
                if (_listenerTransform == null) return true;
            }

            Vector3 listenerPos = _listenerTransform.position;
            float maxDist = _cachedEventMaxDistance > 0f ? _cachedEventMaxDistance : 50f;
            float maxDistSq = maxDist * maxDist;

            switch (_positionMode)
            {
                case MultiPositionType.Simple:
                    return Vector3.SqrMagnitude(transform.position - listenerPos) <= maxDistSq;

                case MultiPositionType.MultiPosition:
                    return Vector3.SqrMagnitude(transform.position - listenerPos) <= maxDistSq;

                case MultiPositionType.Large:
                    if (_positionPoints != null)
                    {
                        for (int i = 0; i < _positionPoints.Count; i++)
                        {
                            if (!_positionPoints[i].Enabled) continue;
                            Vector3 worldPos = transform.TransformPoint(_positionPoints[i].LocalPosition);
                            if (Vector3.SqrMagnitude(worldPos - listenerPos) <= maxDistSq)
                                return true;
                        }
                    }
                    return false;

                case MultiPositionType.ClosestPoint:
                    var collider = GetEffectiveCollider();
                    if (collider == null) return true;
                    Vector3 closestPt = collider.ClosestPoint(listenerPos);
                    return Vector3.SqrMagnitude(closestPt - listenerPos) <= maxDistSq;

                default:
                    return true;
            }
        }

        private bool IsVisible()
        {
            if (_cachedRenderer == null)
                return true;

            return _cachedRenderer.isVisible;
        }

        private void CacheComponents()
        {
            if (_closestPointCollider == null)
                _cachedCollider = GetComponent<Collider>();

            _cachedRenderer = GetComponent<Renderer>();
        }

        #endregion

        #region Listener

        private void UpdateListenerReference()
        {
            if (AudioListenerManager.IsInstanced)
            {
                _listenerTransform = AudioListenerManager.Instance.GetActiveListenerTransform();
            }

            if (_listenerTransform == null && Time.time - _lastListenerSearchTime > LISTENER_SEARCH_COOLDOWN)
            {
                _lastListenerSearchTime = Time.time;
                var listener = FindAnyObjectByType<AudioListener>();
                _listenerTransform = listener != null ? listener.transform : Camera.main?.transform;
            }
        }

        #endregion

        #region Event Name Helpers

        /// <summary>
        /// 获取所有有效的事件名列表（所有模式共用）
        /// </summary>
        /// <summary>
        /// 根据 EmitterPlayMode 获取要播放的事件列表
        /// All: 返回所有有效事件
        /// RandomOne: 加权随机 + Probability 过滤，选一个
        /// Shuffle: Wwise-style 洗牌袋，逐个遍历
        /// </summary>
        private void GetAllAudioEvents(List<string> result)
        {
            result.Clear();
            if (_audioEvents == null || _audioEvents.Length == 0) return;

            switch (_playMode)
            {
                case EmitterPlayMode.All:
                    for (int i = 0; i < _audioEvents.Length; i++)
                    {
                        if (_audioEvents[i] != null && _audioEvents[i].IsValid)
                            result.Add(_audioEvents[i].EventName);
                    }
                    break;

                case EmitterPlayMode.RandomOne:
                    SelectRandomOneEvent(result);
                    break;

                case EmitterPlayMode.Shuffle:
                    SelectShuffleEvent(result);
                    break;
            }
        }

        private void ResolveCurrentAudioEvents()
        {
            GetAllAudioEvents(_tempAudioEvents);
            _activeAudioEvents.Clear();
            for (int i = 0; i < _tempAudioEvents.Count; i++)
                _activeAudioEvents.Add(_tempAudioEvents[i]);
            _resolvedUseLoopPlayback = ResolveLoopPolicyFromEvents(_activeAudioEvents);
        }

        private bool ResolveLoopPolicyFromEvents(List<string> eventNames)
        {
            bool hasEntry = false;
            bool shouldLoop = false;
            _resolvedManagedByEventChain = false;
            for (int i = 0; i < eventNames.Count; i++)
            {
                var entry = Audio.FindEntry(eventNames[i]);
                if (entry == null) continue;

                hasEntry = true;
                var clips = entry.GetClips();
                if (entry.PlayMode == ContainerPlayMode.Continuous && clips != null && clips.Length > 1)
                    _resolvedManagedByEventChain = true;

                if (entry.PlayMode == ContainerPlayMode.Continuous || entry.EffectiveLoop || entry.LoopCount > 0)
                    shouldLoop = true;
            }

            return hasEntry ? shouldLoop : _loop;
        }

        private bool ResolveLoopPolicyForEvent(string eventName)
        {
            var entry = Audio.FindEntry(eventName);
            if (entry == null) return _loop;

            var clips = entry.GetClips();
            if (entry.PlayMode == ContainerPlayMode.Continuous && clips != null && clips.Length > 1)
                _resolvedManagedByEventChain = true;

            return entry.PlayMode == ContainerPlayMode.Continuous || entry.EffectiveLoop || entry.LoopCount > 0;
        }

        private void PlayFromResolvedEvents()
        {
            if (_resolvedUseLoopPlayback)
            {
                PlayAllEvents(_tempAudioEvents, true);
            }
            else
            {
                PlayAllEvents(_tempAudioEvents, false);
                if (_minInterval > 0f)
                    ScheduleNextPlay();
            }
        }

        private void SelectRandomOneEvent(List<string> result)
        {
            // 收集有效且通过 Probability 检查的候选，后续只在该集合内做加权随机
            _randomOneCandidates.Clear();
            float totalWeight = 0f;

            for (int i = 0; i < _audioEvents.Length; i++)
            {
                var evt = _audioEvents[i];
                if (evt == null || !evt.IsValid) continue;
                if (evt.Probability < 1f && Random.value > evt.Probability) continue;
                if (evt.Weight <= 0f) continue;

                _randomOneCandidates.Add(evt);
                totalWeight += evt.Weight;
            }

            if (_randomOneCandidates.Count == 0) return;

            // 加权随机选择
            float pick = Random.Range(0f, totalWeight);
            float cumulative = 0f;

            for (int i = 0; i < _randomOneCandidates.Count; i++)
            {
                var evt = _randomOneCandidates[i];
                cumulative += evt.Weight;
                if (pick <= cumulative)
                {
                    result.Add(evt.EventName);
                    return;
                }
            }

            // 浮点误差兜底：取最后一个候选
            result.Add(_randomOneCandidates[_randomOneCandidates.Count - 1].EventName);
        }

        private void SelectShuffleEvent(List<string> result)
        {
            // 收集有效事件索引
            int validCount = 0;
            for (int i = 0; i < _audioEvents.Length; i++)
            {
                if (_audioEvents[i] != null && _audioEvents[i].IsValid)
                    validCount++;
            }

            if (validCount == 0) return;

            if (_emitterShuffleBag == null || _emitterShuffleBag.NeedsRefill(validCount))
            {
                _emitterShuffleBag = new ShuffleBag();
                // 收集权重
                float[] weights = new float[validCount];
                int idx = 0;
                for (int i = 0; i < _audioEvents.Length; i++)
                {
                    if (_audioEvents[i] != null && _audioEvents[i].IsValid)
                        weights[idx++] = _audioEvents[i].Weight;
                }
                _emitterShuffleBag.Fill(validCount, weights);
            }

            int picked = _emitterShuffleBag.Next();

            // 将 shuffle 索引映射回实际事件索引
            int current = 0;
            for (int i = 0; i < _audioEvents.Length; i++)
            {
                if (_audioEvents[i] != null && _audioEvents[i].IsValid)
                {
                    if (current == picked)
                    {
                        result.Add(_audioEvents[i].EventName);
                        return;
                    }
                    current++;
                }
            }
        }

        /// <summary>
        /// 首个事件名（用于 Profiler 日志）
        /// </summary>
        private string PrimaryEventName =>
            _audioEvents != null && _audioEvents.Length > 0 && _audioEvents[0] != null ? _audioEvents[0].EventName : "";

        #endregion

        #region Editor Helpers

        private void OnDrawGizmosSelected()
        {
            // 无条件绘制中心标记（调试用，确认 Gizmo 回调被调用）
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(transform.position, 2f);

            float maxDist = GetEventMaxDistanceForGizmo();

            // Large 模式：始终绘制位置点标记（即使 maxDist 无效）
            if (_positionMode == MultiPositionType.Large && _positionPoints != null && _positionPoints.Count > 0)
            {
                for (int i = 0; i < _positionPoints.Count; i++)
                {
                    if (!_positionPoints[i].Enabled) continue;
                    Vector3 worldPos = transform.TransformPoint(_positionPoints[i].LocalPosition);

                    // 位置点小球 + 连线
                    Gizmos.color = new Color(0f, 0.8f, 1f, 0.8f);
                    Gizmos.DrawSphere(worldPos, 0.3f);
                    Gizmos.color = new Color(0f, 0.8f, 1f, 0.4f);
                    Gizmos.DrawLine(transform.position, worldPos);

                    // 衰减范围
                    if (maxDist > 0f)
                    {
                        Gizmos.color = new Color(0f, 1f, 0.5f, 0.06f);
                        Gizmos.DrawSphere(worldPos, maxDist);
                        Gizmos.color = new Color(0f, 1f, 0.5f, 0.4f);
                        Gizmos.DrawWireSphere(worldPos, maxDist);
                    }
                }
            }
            else
            {
                // Simple / MultiPosition / ClosestPoint：在 transform 位置绘制
                if (maxDist > 0f)
                {
                    Gizmos.color = new Color(0f, 1f, 0.5f, 0.06f);
                    Gizmos.DrawSphere(transform.position, maxDist);
                    Gizmos.color = new Color(0f, 1f, 0.5f, 0.4f);
                    Gizmos.DrawWireSphere(transform.position, maxDist);
                }
            }

            // ClosestPoint 额外画 Collider 范围
            if (_positionMode == MultiPositionType.ClosestPoint)
            {
                var col = GetEffectiveCollider();
                if (col != null)
                {
                    Gizmos.color = new Color(0f, 0.8f, 1f, 0.08f);
                    Gizmos.DrawCube(col.bounds.center, col.bounds.size);
                }
            }
        }

#if UNITY_EDITOR
        // 编辑模式 Gizmo 缓存（避免每帧 AssetDatabase 查询）
        [System.NonSerialized] private float _editorGizmoMaxDist = -1f;
        [System.NonSerialized] private string _editorGizmoEventKey;

        private void OnValidate()
        {
            // Inspector 值变化时清除 Gizmo 缓存
            _editorGizmoMaxDist = -1f;
            _editorGizmoEventKey = null;
        }
#endif

        /// <summary>
        /// 获取事件的 MaxDistance（运行时用缓存，编辑模式通过 AssetDatabase 查找并缓存）
        /// </summary>
        private float GetEventMaxDistanceForGizmo()
        {
            // 运行时：使用缓存值
            if (Application.isPlaying)
                return _cachedEventMaxDistance > 0f ? _cachedEventMaxDistance : 0f;

            // 编辑模式：从 EventEntry 查找（带缓存）
            if (_audioEvents == null || _audioEvents.Length == 0) return 0f;

#if UNITY_EDITOR
            // 生成事件名 key，用于缓存失效判断
            string key = "";
            for (int i = 0; i < _audioEvents.Length; i++)
            {
                if (!string.IsNullOrEmpty(_audioEvents[i].EventName))
                    key += _audioEvents[i].EventName + ";";
            }
            if (string.IsNullOrEmpty(key)) return 0f;

            // 缓存命中
            if (_editorGizmoMaxDist >= 0f && _editorGizmoEventKey == key)
                return _editorGizmoMaxDist;

            // 遍历所有事件，取最大 MaxDistance（与运行时 CacheEventMaxDistance 一致）
            float maxDist = 0f;
            var guids = UnityEditor.AssetDatabase.FindAssets("t:AudioEventLibrary");
            for (int i = 0; i < _audioEvents.Length; i++)
            {
                string eventName = _audioEvents[i].EventName;
                if (string.IsNullOrEmpty(eventName)) continue;

                for (int g = 0; g < guids.Length; g++)
                {
                    var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[g]);
                    var lib = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioEventLibrary>(path);
                    if (lib == null || lib.Events == null) continue;
                    for (int j = 0; j < lib.Events.Length; j++)
                    {
                        if (lib.Events[j].EventName == eventName && lib.Events[j].MaxDistance > maxDist)
                        {
                            maxDist = lib.Events[j].MaxDistance;
                            break;
                        }
                    }
                }
            }

            _editorGizmoMaxDist = maxDist;
            _editorGizmoEventKey = key;
            return maxDist;
#else
            return 0f;
#endif
        }

        /// <summary>
        /// 从 EventEntry 缓存 MaxDistance，用于运行时距离优化和 Gizmo
        /// </summary>
        private void CacheEventMaxDistance()
        {
            _cachedEventMaxDistance = 0f;
            if (_audioEvents == null || _audioEvents.Length == 0) return;

            for (int i = 0; i < _audioEvents.Length; i++)
            {
                if (string.IsNullOrEmpty(_audioEvents[i].EventName)) continue;
                var entry = Audio.FindEntry(_audioEvents[i].EventName);
                if (entry != null)
                {
                    // 取所有事件中最大的衰减距离；未命中时保留 0，由 MaxDistance 属性统一回退
                    if (entry.MaxDistance > _cachedEventMaxDistance)
                        _cachedEventMaxDistance = entry.MaxDistance;
                }
            }
        }

        #endregion
    }
}
