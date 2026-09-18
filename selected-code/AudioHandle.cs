using System;
using System.Collections;
using UnityEngine;

namespace AudioFlux
{
    /// <summary>
    /// 音频事件类型
    /// </summary>
    public enum AudioEventType
    {
        /// <summary>开始播放</summary>
        Start,
        /// <summary>停止播放</summary>
        Stop,
        /// <summary>暂停</summary>
        Pause,
        /// <summary>恢复</summary>
        Resume,
        /// <summary>播放完成</summary>
        Complete,
        /// <summary>循环完成一次</summary>
        LoopComplete,
        /// <summary>播放进度更新</summary>
        Progress
    }

    /// <summary>
    /// 音频播放句柄
    /// 提供链式 API 控制正在播放的音频
    /// </summary>
    public class AudioHandle
    {
        /// <summary>AudioSource.time 安全边距，防止 seek 越界</summary>
        private const float CLIP_TIME_SAFETY_MARGIN = 0.01f;
        private const string DEBUG_EVENT_NAME = "Amb_3D_Market_Crowd";

        private AudioSource _source;
        private string _eventName;
        private MonoBehaviour _host;
        private Coroutine _fadeCoroutine;
        private Coroutine _followCoroutine;
        private Coroutine _delayCoroutine;
        private Coroutine _progressCoroutine;
        private Coroutine _loopTrackCoroutine;

        // 回调
        private Action<AudioHandle> _onStart;
        private Action<AudioHandle> _onStop;
        private Action<AudioHandle> _onPause;
        private Action<AudioHandle> _onResume;
        private Action<AudioHandle> _onComplete;
        private Action<AudioHandle> _onLoopComplete;
        private Action<AudioHandle, float> _onProgress;

        private bool _isValid;
        private bool _startCallbackFired;
        private bool _isStopping;  // 防止重复调用 Stop
        private int _loopCount;
        private AudioClip _originalClip;  // 检测 source 是否被 pool 强制回收

        // --- Voice Virtualization ---
        internal int _voicePriority;
        internal AudioGroup _voiceGroup;
        internal float _voiceSpatialBlend;
        internal float _voiceMaxDistance;
        internal bool _voiceIsLooping;
        private VoiceState _voiceState = VoiceState.Physical;
        private float _virtualPlaybackTime;
        private float _virtualStartRealTime;
        private float _virtualClipLength;
        private float _volumeBeforeVirtualize;

        // --- DSP ---
        private AudioDSPChain _dspChain;

        /// <summary>
        /// 音频是否正在播放（包括虚拟化状态）
        /// </summary>
        public bool IsPlaying => _isValid && (_voiceState == VoiceState.Physical
            ? (_source != null && _source.isPlaying)
            : _voiceState == VoiceState.Virtual);

        /// <summary>
        /// 句柄是否有效（未被销毁或停止）
        /// </summary>
        public bool IsValid => _isValid;

        /// <summary>
        /// 事件名称
        /// </summary>
        public string EventName => _eventName;

        /// <summary>
        /// 关联的 AudioSource
        /// </summary>
        internal AudioSource Source => _source;

        /// <summary>
        /// 播放进度 (0-1)
        /// </summary>
        public float Progress
        {
            get
            {
                if (_source == null || _source.clip == null)
                    return 0f;
                return _source.time / _source.clip.length;
            }
        }

        /// <summary>
        /// 当前循环次数
        /// </summary>
        public int LoopCount => _loopCount;

        /// <summary>
        /// 当前播放时间（秒）
        /// </summary>
        public float CurrentTime => _source != null ? _source.time : 0f;

        /// <summary>
        /// 音频总时长（秒）
        /// </summary>
        public float Duration => _source != null && _source.clip != null ? _source.clip.length : 0f;

        /// <summary>
        /// Voice 虚拟化状态
        /// </summary>
        public VoiceState VoiceState => _voiceState;

        /// <summary>
        /// 是否处于虚拟化状态
        /// </summary>
        public bool IsVirtual => _voiceState == VoiceState.Virtual;

        // Voice 属性（VoiceManager 排序用）
        internal int VoicePriority => _voicePriority;
        internal AudioGroup VoiceGroup => _voiceGroup;
        internal float VoiceSpatialBlend => _voiceSpatialBlend;
        internal float VoiceMaxDistance => _voiceMaxDistance;
        internal bool VoiceIsLooping => _voiceIsLooping;

        /// <summary>
        /// 空句柄（播放失败时返回）
        /// </summary>
        public static AudioHandle Empty { get; } = new AudioHandle();

        private AudioHandle()
        {
            _isValid = false;
        }

        private AudioHandle(AudioSource source, string eventName, MonoBehaviour host,
            int priority = 50, AudioGroup group = AudioGroup.SFX,
            float spatialBlend = 0f, float maxDistance = 50f, bool isLooping = false)
        {
            _source = source;
            _eventName = eventName;
            _host = host;
            _isValid = true;
            _startCallbackFired = false;
            _isStopping = false;
            _loopCount = 0;

            // Voice info
            _voicePriority = priority;
            _voiceGroup = group;
            _voiceSpatialBlend = spatialBlend;
            _voiceMaxDistance = maxDistance;
            _voiceIsLooping = isLooping;
            _voiceState = VoiceState.Physical;
            _originalClip = source.clip;
        }

        /// <summary>
        /// 创建句柄（内部使用）
        /// </summary>
        internal static AudioHandle Create(AudioSource source, string eventName, MonoBehaviour host,
            int priority = 50, AudioGroup group = AudioGroup.SFX,
            float spatialBlend = 0f, float maxDistance = 50f, bool isLooping = false)
        {
            if (source == null || host == null)
                return Empty;

            var handle = new AudioHandle(source, eventName, host, priority, group, spatialBlend, maxDistance, isLooping);
            // 延迟触发 Start 回调（在下一帧，确保链式调用完成）
            host.StartCoroutine(handle.TriggerStartCallbackCoroutine());
            // 非循环音效始终监视自然结束，确保 VoiceManager 注销和 Profiler Stop 事件正确触发
            if (!isLooping)
                host.StartCoroutine(handle.NaturalCompletionCoroutine());
            return handle;
        }

        #region 链式方法

        /// <summary>
        /// 设置播放位置
        /// </summary>
        public AudioHandle At(Vector3 position)
        {
            if (!_isValid || _source == null)
                return this;

            _source.transform.position = position;
            return this;
        }

        /// <summary>
        /// 跟随目标对象
        /// </summary>
        public AudioHandle On(GameObject target)
        {
            if (!_isValid || _source == null || target == null || _host == null)
                return this;

            StopFollowCoroutine();
            _followCoroutine = _host.StartCoroutine(FollowTargetCoroutine(target.transform));
            return this;
        }

        /// <summary>
        /// 跟随目标 Transform
        /// </summary>
        public AudioHandle On(Transform target)
        {
            if (!_isValid || _source == null || target == null || _host == null)
                return this;

            StopFollowCoroutine();
            _followCoroutine = _host.StartCoroutine(FollowTargetCoroutine(target));
            return this;
        }

        /// <summary>
        /// 设置音量
        /// </summary>
        public AudioHandle Volume(float volume)
        {
            if (!_isValid || _source == null)
                return this;

            _source.volume = Mathf.Clamp01(volume);
            return this;
        }

        /// <summary>
        /// 设置音调
        /// </summary>
        public AudioHandle Pitch(float pitch)
        {
            if (!_isValid || _source == null)
                return this;

            _source.pitch = Mathf.Clamp(pitch, 0.1f, 3f);
            return this;
        }

        /// <summary>
        /// 设置循环
        /// </summary>
        public AudioHandle Loop(bool loop = true)
        {
            if (!_isValid || _source == null)
                return this;

            _source.loop = loop;
            return this;
        }

        /// <summary>
        /// 淡入播放
        /// </summary>
        public AudioHandle FadeIn(float duration = 0.5f)
        {
            if (!_isValid || _source == null || _host == null)
                return this;

            float targetVolume = _source.volume;
            _source.volume = 0f;

            StopFadeCoroutine();
            _fadeCoroutine = _host.StartCoroutine(FadeCoroutine(0f, targetVolume, duration));
            return this;
        }

        /// <summary>
        /// 延迟播放
        /// </summary>
        public AudioHandle Delay(float seconds)
        {
            if (!_isValid || _source == null || _host == null || seconds <= 0f)
                return this;

            // 暂停播放，延迟后恢复
            _source.Pause();
            StopDelayCoroutine();
            _delayCoroutine = _host.StartCoroutine(DelayPlayCoroutine(seconds));
            return this;
        }

        /// <summary>
        /// 设置空间混合度
        /// </summary>
        public AudioHandle SpatialBlend(float blend)
        {
            if (!_isValid || _source == null)
                return this;

            _source.spatialBlend = Mathf.Clamp01(blend);
            return this;
        }

        /// <summary>
        /// 设置 2D 音效
        /// </summary>
        public AudioHandle As2D()
        {
            return SpatialBlend(0f);
        }

        /// <summary>
        /// 设置 3D 音效
        /// </summary>
        public AudioHandle As3D()
        {
            return SpatialBlend(1f);
        }

        /// <summary>
        /// 设置最大距离
        /// </summary>
        public AudioHandle MaxDistance(float distance)
        {
            if (!_isValid || _source == null)
                return this;

            _source.maxDistance = Mathf.Max(1f, distance);
            return this;
        }

        /// <summary>
        /// 设置最小距离（小于此距离音量保持 100%）
        /// </summary>
        public AudioHandle MinDistance(float distance)
        {
            if (!_isValid || _source == null)
                return this;

            _source.minDistance = Mathf.Max(0.1f, distance);
            return this;
        }

        /// <summary>
        /// 设置 3D 声音扩展角度 (0=点声源, 360=全方位)
        /// </summary>
        public AudioHandle Spread(float degrees)
        {
            if (!_isValid || _source == null)
                return this;

            _source.spread = Mathf.Clamp(degrees, 0f, 360f);
            return this;
        }

        /// <summary>
        /// 设置距离-扩散曲线 (类似 Wwise Spread 曲线)
        /// X=距离(米), Y=扩散(0=0°, 1=360°)
        /// </summary>
        public AudioHandle SpreadCurve(AnimationCurve curve)
        {
            if (!_isValid || _source == null || curve == null)
                return this;

            _source.SetCustomCurve(AudioSourceCurveType.Spread, curve);
            return this;
        }

        /// <summary>
        /// 设置自定义音量衰减曲线（X=距离归一化0-1, Y=音量0-1）
        /// 同时将 rolloffMode 切换为 Custom
        /// </summary>
        public AudioHandle RolloffCurve(AnimationCurve curve)
        {
            if (!_isValid || _source == null || curve == null)
                return this;

            _source.rolloffMode = AudioRolloffMode.Custom;
            _source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
            return this;
        }

        /// <summary>
        /// 播放完成回调（兼容旧 API）
        /// </summary>
        public AudioHandle OnComplete(Action callback)
        {
            return OnComplete(_ => callback?.Invoke());
        }

        /// <summary>
        /// 开始播放回调
        /// </summary>
        /// <param name="callback">回调函数，参数为当前句柄</param>
        public AudioHandle OnStart(Action<AudioHandle> callback)
        {
            if (!_isValid)
                return this;

            _onStart = callback;

            // 如果已经触发过 Start，立即调用
            if (_startCallbackFired)
            {
                callback?.Invoke(this);
            }

            return this;
        }

        /// <summary>
        /// 停止播放回调
        /// </summary>
        /// <param name="callback">回调函数，参数为当前句柄</param>
        public AudioHandle OnStop(Action<AudioHandle> callback)
        {
            if (!_isValid)
                return this;

            _onStop = callback;
            return this;
        }

        /// <summary>
        /// 暂停回调
        /// </summary>
        /// <param name="callback">回调函数，参数为当前句柄</param>
        public AudioHandle OnPause(Action<AudioHandle> callback)
        {
            if (!_isValid)
                return this;

            _onPause = callback;
            return this;
        }

        /// <summary>
        /// 恢复播放回调
        /// </summary>
        /// <param name="callback">回调函数，参数为当前句柄</param>
        public AudioHandle OnResume(Action<AudioHandle> callback)
        {
            if (!_isValid)
                return this;

            _onResume = callback;
            return this;
        }

        /// <summary>
        /// 播放完成回调（带句柄参数）
        /// </summary>
        /// <param name="callback">回调函数，参数为当前句柄</param>
        public AudioHandle OnComplete(Action<AudioHandle> callback)
        {
            if (!_isValid || _host == null)
                return this;

            _onComplete = callback;
            // NaturalCompletionCoroutine 已在 Create() 中启动，无需再次启动
            return this;
        }

        /// <summary>
        /// 循环完成一次回调（每次循环结束时触发）
        /// </summary>
        /// <param name="callback">回调函数，参数为当前句柄</param>
        public AudioHandle OnLoopComplete(Action<AudioHandle> callback)
        {
            if (!_isValid || _host == null)
                return this;

            _onLoopComplete = callback;

            // 启动循环追踪协程
            if (_loopTrackCoroutine == null && _source != null && _source.loop)
            {
                _loopTrackCoroutine = _host.StartCoroutine(TrackLoopCoroutine());
            }

            return this;
        }

        /// <summary>
        /// 播放进度回调
        /// </summary>
        /// <param name="callback">回调函数，参数为当前句柄和进度(0-1)</param>
        /// <param name="updateInterval">更新间隔（秒），默认 0.1 秒</param>
        public AudioHandle OnProgress(Action<AudioHandle, float> callback, float updateInterval = 0.1f)
        {
            if (!_isValid || _host == null)
                return this;

            _onProgress = callback;

            // 停止旧的进度协程
            if (_progressCoroutine != null)
            {
                _host.StopCoroutine(_progressCoroutine);
            }

            _progressCoroutine = _host.StartCoroutine(ProgressCoroutine(updateInterval));
            return this;
        }

        #endregion

        #region 控制方法

        /// <summary>
        /// 停止播放
        /// </summary>
        public void Stop(float fadeOut = 0f)
        {
            // 防止重复调用
            if (_isStopping || !_isValid)
                return;

            DebugLifecycle("HandleStopRequest", $"fadeOut={fadeOut:0.00} voiceState={_voiceState}");
            _isStopping = true;

            // Profiler 日志
            LogStopEvent(fadeOut > 0 ? $"FadeOut={fadeOut}s" : null);

            // 停止除淡出以外的所有协程
            StopFollowCoroutine();
            StopDelayCoroutine();
            StopProgressCoroutine();
            StopLoopTrackCoroutine();

            // 如果已有淡出协程在运行，先停止它
            if (_fadeCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            if (fadeOut > 0f && _host != null && _source != null)
            {
                _fadeCoroutine = _host.StartCoroutine(FadeOutAndStopCoroutine(fadeOut));
            }
            else
            {
                // 立即停止
                StopImmediate();
            }
        }

        /// <summary>
        /// 立即停止（内部使用）
        /// </summary>
        private void StopImmediate()
        {
            // 清理 DSP 链
            _dspChain?.Dispose();
            _dspChain = null;

            if (_source != null)
            {
                _source.Stop();
                _source.clip = null; // 清理引用，帮助回收
            }

            _voiceState = VoiceState.Stopped;
            InvokeStopCallbackAndInvalidate();
        }

        /// <summary>
        /// 暂停播放
        /// </summary>
        public void Pause()
        {
            if (!_isValid || _source == null)
                return;

            _source.Pause();

            // Profiler 日志
            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.CharacterSFX,
                AudioAction.Pause,
                AudioTriggerSource.Code,
                _eventName,
                _source.clip?.name,
                _source.gameObject,
                _source.transform.position,
                _source.volume
            );

            _onPause?.Invoke(this);
        }

        /// <summary>
        /// 恢复播放
        /// </summary>
        public void Resume()
        {
            if (!_isValid || _source == null)
                return;

            _source.UnPause();

            // Profiler 日志
            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.CharacterSFX,
                AudioAction.Resume,
                AudioTriggerSource.Code,
                _eventName,
                _source.clip?.name,
                _source.gameObject,
                _source.transform.position,
                _source.volume
            );

            _onResume?.Invoke(this);
        }

        #endregion

        #region Voice 虚拟化

        /// <summary>
        /// 虚拟化（暂停 AudioSource，保存播放位置）
        /// </summary>
        internal void Virtualize()
        {
            if (_voiceState != VoiceState.Physical || _source == null) return;

            DebugLifecycle("HandleVirtualize", $"time={_source.time:0.00} volume={_source.volume:0.00}");
            _virtualPlaybackTime = _source.time;
            _virtualStartRealTime = Time.realtimeSinceStartup;
            _virtualClipLength = _source.clip != null ? _source.clip.length : 0f;
            _voiceIsLooping = _source.loop;
            _volumeBeforeVirtualize = _source.volume;

            _source.Pause();
            _voiceState = VoiceState.Virtual;
        }

        /// <summary>
        /// 恢复物理播放
        /// </summary>
        internal void Devirtualize()
        {
            if (_voiceState != VoiceState.Virtual || _source == null) return;

            DebugLifecycle("HandleDevirtualizeStart", $"virtualTime={_virtualPlaybackTime:0.00}");
            // clip 可能在虚拟化期间被换掉或卸载
            var clip = _source.clip;
            if (clip == null)
            {
                DebugLifecycle("HandleDevirtualizeStop", "reason=ClipMissing");
                _voiceState = VoiceState.Stopped;
                InvokeStopCallbackAndInvalidate();
                return;
            }

            float actualClipLength = clip.length;
            if (actualClipLength <= 0f)
            {
                DebugLifecycle("HandleDevirtualizeStop", "reason=ClipLengthInvalid");
                _voiceState = VoiceState.Stopped;
                InvokeStopCallbackAndInvalidate();
                return;
            }

            float safeMax = Mathf.Max(0f, actualClipLength - CLIP_TIME_SAFETY_MARGIN);
            float elapsed = Time.realtimeSinceStartup - _virtualStartRealTime;

            float targetTime = _voiceIsLooping
                ? (_virtualPlaybackTime + elapsed) % actualClipLength
                : _virtualPlaybackTime + elapsed;

            if (!_voiceIsLooping && targetTime >= actualClipLength)
            {
                // 非循环声音在虚拟期间自然结束
                DebugLifecycle("HandleDevirtualizeStop", $"reason=VirtualNaturalComplete targetTime={targetTime:0.00} clipLength={actualClipLength:0.00}");
                _voiceState = VoiceState.Stopped;
                _onComplete?.Invoke(this);
                InvokeStopCallbackAndInvalidate();
                return;
            }

            _source.time = Mathf.Clamp(targetTime, 0f, safeMax);
            _source.volume = _volumeBeforeVirtualize;
            _source.UnPause();
            _voiceState = VoiceState.Physical;
            DebugLifecycle("HandleDevirtualizeResume", $"time={_source.time:0.00} volume={_source.volume:0.00}");
        }

        #endregion

        #region DSP 滤波

        /// <summary>
        /// 获取或创建 DSP 链
        /// </summary>
        private AudioDSPChain EnsureDSPChain()
        {
            if (_dspChain == null && _source != null && _host != null)
                _dspChain = new AudioDSPChain(_source, _host);
            return _dspChain;
        }

        /// <summary>
        /// 设置低通滤波（截止频率 Hz）
        /// </summary>
        public AudioHandle LowPass(float cutoffHz, float resonance = 1f, float rampTime = 0.1f)
        {
            if (!_isValid) return this;
            EnsureDSPChain()?.SetLowPass(cutoffHz, resonance, rampTime);
            return this;
        }

        /// <summary>
        /// 设置高通滤波（截止频率 Hz）
        /// </summary>
        public AudioHandle HighPass(float cutoffHz, float resonance = 1f, float rampTime = 0.1f)
        {
            if (!_isValid) return this;
            EnsureDSPChain()?.SetHighPass(cutoffHz, resonance, rampTime);
            return this;
        }

        /// <summary>
        /// 设置混响效果
        /// </summary>
        public AudioHandle Reverb(AudioReverbPreset preset, float rampTime = 0.3f)
        {
            if (!_isValid) return this;
            EnsureDSPChain()?.SetReverb(preset, rampTime);
            return this;
        }

        /// <summary>
        /// 应用 DSP 预设
        /// </summary>
        public AudioHandle DSPPreset(string presetName)
        {
            if (!_isValid) return this;
            var preset = DSPPresetResolver.GetPreset(presetName);
            if (preset != null) EnsureDSPChain()?.ApplyPreset(preset);
            return this;
        }

        /// <summary>
        /// 清除所有 DSP 效果
        /// </summary>
        public AudioHandle ClearDSP(float rampTime = 0.1f)
        {
            if (!_isValid) return this;
            _dspChain?.ClearAll(rampTime);
            return this;
        }

        #endregion

        #region 协程

        private IEnumerator FollowTargetCoroutine(Transform target)
        {
            // 使用 Unity 的隐式 bool 转换检查 destroyed object
            // target != null 会正确处理 Unity 的 "fake null" 情况
            while (true)
            {
                // 检查协程宿主是否有效
                if (_host == null || !_host.gameObject.activeInHierarchy)
                    yield break;

                // 检查 source 是否有效
                if (_source == null || !_source.isPlaying)
                    yield break;

                // 检查 source 所在 GameObject 是否仍然活跃
                if (!_source.gameObject.activeInHierarchy)
                    yield break;

                // 检查 target 是否有效（Unity 的 "fake null" 检查）
                if (target == null)
                    yield break;

                // 额外检查目标对象是否仍然活跃
                if (!target.gameObject.activeInHierarchy)
                    yield break;

                // 安全地更新位置
                _source.transform.position = target.position;
                yield return null;
            }
        }

        private IEnumerator FadeCoroutine(float from, float to, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration && _host != null && _source != null)
            {
                elapsed += Time.unscaledDeltaTime;
                _source.volume = Mathf.Lerp(from, to, elapsed / duration);
                yield return null;
            }

            if (_source != null)
                _source.volume = to;
        }

        private IEnumerator FadeOutAndStopCoroutine(float duration)
        {
            if (_source == null || _host == null)
            {
                // 即使 source 为空也要触发回调和设置状态
                InvokeStopCallbackAndInvalidate();
                yield break;
            }

            float startVolume = _source.volume;
            float elapsed = 0f;

            while (elapsed < duration && _host != null && _source != null && _source.isPlaying)
            {
                elapsed += Time.unscaledDeltaTime;
                if (_source != null)
                {
                    _source.volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
                }
                yield return null;
            }

            // 协程正常完成
            if (_source != null)
            {
                _source.Stop();
                _source.volume = startVolume;
            }

            InvokeStopCallbackAndInvalidate();
        }

        /// <summary>
        /// 触发停止回调并使句柄无效
        /// </summary>
        private void InvokeStopCallbackAndInvalidate()
        {
            if (!_isValid) return;

            DebugLifecycle("HandleInvalidate", $"voiceState={_voiceState}");
            // 从 VoiceManager 注销
            VoiceManager.UnregisterVoice(this);

            _onStop?.Invoke(this);
            _isValid = false;
        }

        private IEnumerator DelayPlayCoroutine(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);

            if (_host == null || _source == null)
                yield break;

            _source.UnPause();
        }

        private IEnumerator NaturalCompletionCoroutine()
        {
            // 虚拟化期间继续等待；Stop() 调用后立即退出，避免淡出期间冗余轮询
            while (_isValid && !_isStopping)
            {
                if (_host == null) break;
                if (_voiceState == VoiceState.Stopped) break;
                // source 被 pool 强制回收时 clip 会变为 null 或新 clip
                if (_source == null || _source.clip != _originalClip) break;
                if (_voiceState == VoiceState.Physical && !_source.isPlaying) break;
                yield return null;
            }

            // 已被 Stop() 或 Devirtualize() 处理过，不重复处理
            if (!_isValid || _isStopping)
                yield break;

            _isStopping = true;
            _voiceState = VoiceState.Stopped;

            // source 被 pool 强制抢占时不清 clip（新声音已在用），仅注销
            bool sourceStolen = _source != null && _source.clip != _originalClip;
            DebugLifecycle("HandleNaturalCompletion", sourceStolen ? "reason=ForceRecycled" : "reason=NaturalComplete");
            LogStopEvent(sourceStolen ? "ForceRecycled" : "NaturalComplete");

            if (!sourceStolen && _source != null) _source.clip = null;
            _onComplete?.Invoke(this);
            InvokeStopCallbackAndInvalidate();
        }

        private void LogStopEvent(string extraInfo = null)
        {
            var sourceAlive = _source != null;
            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.CharacterSFX,
                AudioAction.Stop,
                AudioTriggerSource.Code,
                _eventName,
                sourceAlive ? _source.clip?.name : null,
                sourceAlive ? _source.gameObject : null,
                sourceAlive ? _source.transform.position : Vector3.zero,
                sourceAlive ? _source.volume : 0f,
                extraInfo
            );
        }

        private void DebugLifecycle(string phase, string extraInfo = null)
        {
            if (_eventName != DEBUG_EVENT_NAME)
                return;

            string sourceName = _source != null ? _source.name : "(null)";
            string clipName = _source != null && _source.clip != null ? _source.clip.name : "(null)";
            Vector3 pos = _source != null ? _source.transform.position : Vector3.zero;
            bool isPlaying = _source != null && _source.isPlaying;
            float volume = _source != null ? _source.volume : 0f;

            // Debug.Log(
            //     $"[AudioFlow] phase={phase} event={_eventName} source={sourceName} clip={clipName} " +
            //     $"isPlaying={isPlaying} voiceState={_voiceState} volume={volume:0.00} pos={pos} {extraInfo}");
        }

        private IEnumerator TriggerStartCallbackCoroutine()
        {
            // 等待一帧，确保链式调用完成
            yield return null;

            if (_host == null || !_isValid)
                yield break;

            if (!_startCallbackFired)
            {
                _startCallbackFired = true;
                _onStart?.Invoke(this);
            }
        }

        private IEnumerator ProgressCoroutine(float updateInterval)
        {
            var wait = new WaitForSecondsRealtime(updateInterval);

            while (_host != null && _source != null && _source.isPlaying)
            {
                _onProgress?.Invoke(this, Progress);
                yield return wait;
            }

            // 最后一次调用（进度 1.0）
            if (_source != null && _source.clip != null)
            {
                _onProgress?.Invoke(this, 1f);
            }
        }

        private IEnumerator TrackLoopCoroutine()
        {
            if (_source == null || _source.clip == null)
                yield break;

            float clipLength = _source.clip.length;
            // 使用音频长度的 10% 作为回退检测阈值，最小 0.05 秒，最大 0.5 秒
            float loopThreshold = Mathf.Clamp(clipLength * 0.1f, 0.05f, 0.5f);
            float lastTime = _source.time;

            while (_host != null && _source != null && _source.isPlaying && _source.loop)
            {
                float currentTime = _source.time;

                // 检测循环（时间回退超过阈值）
                if (currentTime < lastTime - loopThreshold)
                {
                    _loopCount++;
                    _onLoopComplete?.Invoke(this);
                }

                lastTime = currentTime;
                yield return null;
            }
        }

        private void StopFadeCoroutine()
        {
            if (_fadeCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }
        }

        private void StopFollowCoroutine()
        {
            if (_followCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_followCoroutine);
                _followCoroutine = null;
            }
        }

        private void StopDelayCoroutine()
        {
            if (_delayCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_delayCoroutine);
                _delayCoroutine = null;
            }
        }

        private void StopProgressCoroutine()
        {
            if (_progressCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_progressCoroutine);
                _progressCoroutine = null;
            }
        }

        private void StopLoopTrackCoroutine()
        {
            if (_loopTrackCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_loopTrackCoroutine);
                _loopTrackCoroutine = null;
            }
        }

        private void StopAllCoroutines()
        {
            StopFadeCoroutine();
            StopFollowCoroutine();
            StopDelayCoroutine();
            StopProgressCoroutine();
            StopLoopTrackCoroutine();
        }

        #endregion
    }
}
