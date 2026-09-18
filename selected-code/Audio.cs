using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Audio;

namespace AudioFlux
{
    /// <summary>
    /// 音频系统静态门面
    /// 提供简洁的链式 API 进行音频播放控制
    /// </summary>
    /// <example>
    /// Audio.Play("Footstep_Rock").At(position).Volume(0.8f);
    /// Audio.Music("BGM_Battle").FadeIn(2f).Loop();
    /// Audio.SetVolume(AudioGroup.SFX, 0.5f);
    /// </example>
    public static class Audio
    {
        private static AudioEventLibrary _library;
        private static AudioEventRegistry _registry;
        private static AudioSettings _settings;
        private static AudioMixer _mixer;
        private static bool _initialized;
        private static bool _isFullyReady;

        // 活动音频句柄追踪
        private static Dictionary<string, List<AudioHandle>> _activeHandles = new Dictionary<string, List<AudioHandle>>();
        private static Dictionary<AudioGroup, List<AudioHandle>> _handlesByGroup = new Dictionary<AudioGroup, List<AudioHandle>>();

        // 句柄字典的线程安全锁
        private static readonly object _handleLock = new object();

        // 冷却追踪
        private static Dictionary<string, float> _lastPlayedTime = new Dictionary<string, float>();

        // 每帧播放计数
        private static int _playsThisFrame;
        private static int _lastFrameCount;

        #region 初始化

        /// <summary>
        /// 初始化音频系统
        /// </summary>
        public static void Initialize(AudioSettings settings)
        {
            if (settings == null)
            {
                AudioProfilerHelper.LogError(AudioObjectType.System, "Initialize", "AudioSettings is null");
                return;
            }

            _settings = settings;
            _mixer = settings.MainMixer;

            // 优先使用 EventRegistry（分包模式），否则使用 EventLibrary（单文件模式）
            if (settings.EventRegistry != null)
            {
                _registry = settings.EventRegistry;
                _registry.Initialize();
            }
            else
            {
                _library = settings.EventLibrary;
            }

            // 初始化混音器控制器
            AudioMixerController.Initialize(_mixer, settings);

            // 初始化分组句柄字典
            foreach (AudioGroup group in System.Enum.GetValues(typeof(AudioGroup)))
            {
                _handlesByGroup[group] = new List<AudioHandle>();
            }

            _initialized = true;

            string mode = _registry != null ? "Registry 分包模式" : "Library 单文件模式";
            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.System,
                AudioAction.Load,
                AudioTriggerSource.Code,
                "AudioSystem",
                extraInfo: $"音频系统初始化完成（{mode}）"
            );
        }

        /// <summary>
        /// 使用单独的库和混音器初始化
        /// </summary>
        public static void Initialize(AudioEventLibrary library, AudioMixer mixer)
        {
            _library = library;
            _mixer = mixer;

            AudioMixerController.Initialize(mixer, null);

            foreach (AudioGroup group in System.Enum.GetValues(typeof(AudioGroup)))
            {
                _handlesByGroup[group] = new List<AudioHandle>();
            }

            _initialized = true;
        }

        /// <summary>
        /// 使用事件注册中心初始化（支持分包加载）
        /// </summary>
        public static void Initialize(AudioEventRegistry registry, AudioMixer mixer)
        {
            _registry = registry;
            _mixer = mixer;

            // 同步初始化注册中心
            registry.Initialize();

            AudioMixerController.Initialize(mixer, null);

            foreach (AudioGroup group in System.Enum.GetValues(typeof(AudioGroup)))
            {
                _handlesByGroup[group] = new List<AudioHandle>();
            }

            _initialized = true;

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.System,
                AudioAction.Load,
                AudioTriggerSource.Code,
                "AudioSystem",
                extraInfo: "音频系统初始化完成（Registry 模式）"
            );
        }

        /// <summary>
        /// 使用事件注册中心异步初始化（预加载事件库）
        /// </summary>
        public static async UniTask InitializeAsync(AudioEventRegistry registry, AudioMixer mixer)
        {
            _registry = registry;
            _mixer = mixer;

            // 异步初始化注册中心（预加载标记的事件库）
            await registry.InitializeAsync();

            AudioMixerController.Initialize(mixer, null);

            foreach (AudioGroup group in System.Enum.GetValues(typeof(AudioGroup)))
            {
                _handlesByGroup[group] = new List<AudioHandle>();
            }

            _initialized = true;

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.System,
                AudioAction.Load,
                AudioTriggerSource.Code,
                "AudioSystem",
                extraInfo: "音频系统异步初始化完成（Registry 模式）"
            );
        }

        /// <summary>
        /// 核心系统是否已初始化（可播放 SFX）
        /// </summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// 全部子系统是否就绪（包括 Music/Switch/State）
        /// </summary>
        public static bool IsReady => _isFullyReady;

        /// <summary>
        /// 标记全部子系统初始化完成（由 AudioSystemInitializer 调用）
        /// </summary>
        public static void MarkReady()
        {
            if (!_initialized)
            {
                AudioProfilerHelper.LogError(AudioObjectType.System, "MarkReady", "核心系统未初始化，无法标记就绪");
                return;
            }
            if (_isFullyReady) return;
            _isFullyReady = true;
            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.System,
                AudioAction.Load,
                AudioTriggerSource.Code,
                "AudioSystem",
                extraInfo: "全部子系统就绪"
            );
        }

        /// <summary>
        /// 关闭音频系统，重置所有状态
        /// </summary>
        public static void Shutdown()
        {
            if (_initialized)
            {
                StopAll(0.1f);
                CleanupDeadHandles();
            }

            // 关闭子系统
            VoiceManager.Shutdown();
            GlobalDSPController.Shutdown();

            // 先关闭核心门控（阻断 PlayInternal），再关闭子系统门控
            _initialized = false;
            _isFullyReady = false;
            _library = null;
            _registry = null;
            _settings = null;
            _mixer = null;

            lock (_handleLock)
            {
                _activeHandles.Clear();
                _handlesByGroup.Clear();
            }
            _lastPlayedTime.Clear();

            AudioProfilerHelper.Log(
                AudioSeverity.Info,
                AudioObjectType.System,
                AudioAction.Unload,
                AudioTriggerSource.Code,
                "AudioSystem",
                extraInfo: "音频系统已关闭"
            );
        }

        /// <summary>
        /// 按需加载事件库（仅 Registry 模式）
        /// </summary>
        public static async UniTask LoadLibraryAsync(EventLibraryCategory category)
        {
            if (_registry != null)
            {
                await _registry.LoadLibraryAsync(category);
            }
        }

        /// <summary>
        /// 卸载事件库（仅 Registry 模式）
        /// </summary>
        public static void UnloadLibrary(EventLibraryCategory category)
        {
            _registry?.UnloadLibrary(category);
        }

        /// <summary>
        /// 检查事件库是否已加载
        /// </summary>
        public static bool IsLibraryLoaded(EventLibraryCategory category)
        {
            return _registry?.IsLibraryLoaded(category) ?? false;
        }

        #endregion

        #region 播放方法

        /// <summary>
        /// 播放音频事件（自动路由到配置的分组）
        /// </summary>
        public static AudioHandle Play(string eventName)
        {
            return PlayInternal(eventName, Vector3.zero, null, null);
        }

        /// <summary>
        /// 在指定位置播放音频事件
        /// </summary>
        public static AudioHandle Play(string eventName, Vector3 position)
        {
            return PlayInternal(eventName, position, null, null);
        }

        /// <summary>
        /// 在目标对象上播放音频事件（自动使用目标的 Switch 值）
        /// </summary>
        public static AudioHandle Play(string eventName, GameObject target)
        {
            if (target == null)
                return AudioHandle.Empty;

            var handle = PlayInternal(eventName, target.transform.position, null, target);
            return handle.On(target);
        }

        /// <summary>
        /// 在指定位置播放音频事件，并使用目标对象的 Switch 值
        /// 用于位置与 Switch 对象分离的场景（如角色脚步声）
        /// 注意：不会跟随 switchTarget 移动，只使用其 Switch 值
        /// </summary>
        public static AudioHandle Play(string eventName, Vector3 position, GameObject switchTarget)
        {
            // 只传入 switchTarget 用于 Switch 查询，不调用 On() 避免跟随错误位置
            return PlayInternal(eventName, position, null, switchTarget);
        }

        /// <summary>
        /// 播放音乐
        /// </summary>
        public static AudioHandle Music(string eventName)
        {
            return PlayWithGroup(eventName, AudioGroup.Music, Vector3.zero);
        }

        /// <summary>
        /// 播放语音
        /// </summary>
        public static AudioHandle Voice(string eventName)
        {
            return PlayWithGroup(eventName, AudioGroup.Voice, Vector3.zero);
        }

        /// <summary>
        /// 播放环境音
        /// </summary>
        public static AudioHandle Ambient(string eventName)
        {
            return PlayWithGroup(eventName, AudioGroup.Ambient, Vector3.zero);
        }

        /// <summary>
        /// 在指定世界坐标播放环境音。
        /// </summary>
        public static AudioHandle Ambient(string eventName, Vector3 position, GameObject switchTarget = null)
        {
            return PlayInternal(eventName, position, AudioGroup.Ambient, switchTarget);
        }

        /// <summary>
        /// 播放 UI 音效
        /// </summary>
        public static AudioHandle UI(string eventName)
        {
            var handle = PlayWithGroup(eventName, AudioGroup.UI, Vector3.zero);
            return handle.As2D();
        }

        /// <summary>
        /// 内部播放实现
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="position">播放位置</param>
        /// <param name="groupOverride">分组覆盖</param>
        /// <param name="target">目标对象（用于 Switch 查询）</param>
        private static AudioHandle PlayInternal(string eventName, Vector3 position, AudioGroup? groupOverride, GameObject target)
        {
            if (!_initialized)
            {
                AudioProfilerHelper.LogError(AudioObjectType.System, eventName, "音频系统未初始化");
                return AudioHandle.Empty;
            }

            if (_library == null && _registry == null)
            {
                AudioProfilerHelper.LogError(AudioObjectType.System, eventName, "AudioEventLibrary/Registry 未加载");
                return AudioHandle.Empty;
            }

            // 检查每帧播放限制
            if (!CheckFrameLimit())
            {
                AudioProfilerHelper.LogWarning(AudioObjectType.CharacterSFX, eventName, "每帧播放数达到上限");
                return AudioHandle.Empty;
            }

            // 获取事件配置（优先从 Registry 获取）
            var entry = _registry != null
                ? _registry.GetEvent(eventName)
                : _library?.GetEvent(eventName);
            if (entry == null)
            {
                AudioProfilerHelper.LogWarning(AudioObjectType.CharacterSFX, eventName, "未找到音频事件配置");
                return AudioHandle.Empty;
            }

            // 检查冷却
            if (!CheckCooldown(eventName, entry.Cooldown))
            {
                return AudioHandle.Empty;
            }

            // 确定分组（支持 UI_ 前缀自动路由）
            var group = groupOverride ?? entry.Group;
            if (groupOverride == null && eventName.StartsWith("UI_", System.StringComparison.OrdinalIgnoreCase))
            {
                group = AudioGroup.UI;
            }

            // 检查实例限制
            if (!CheckInstanceLimit(eventName, entry))
            {
                return AudioHandle.Empty;
            }

            // 通过 SFXManager 播放（传递 target 以支持 Switch）
            var handle = SFXManager.PlayEvent(entry, position, null, group, target);

            if (handle != AudioHandle.Empty)
            {
                // UI 音效强制 2D
                if (group == AudioGroup.UI)
                {
                    handle.As2D();
                }

                // 注册到 VoiceManager
                VoiceManager.RegisterVoice(handle);

                // 记录活动句柄
                TrackHandle(eventName, group, handle);

                // 更新冷却
                _lastPlayedTime[eventName] = Time.unscaledTime;
            }

            return handle;
        }

        /// <summary>
        /// 以指定分组播放
        /// </summary>
        private static AudioHandle PlayWithGroup(string eventName, AudioGroup group, Vector3 position)
        {
            return PlayInternal(eventName, position, group, null);
        }

        /// <summary>
        /// 查找事件配置（供 AudioEmitter 等内部组件使用）
        /// </summary>
        internal static EventEntry FindEntry(string eventName)
        {
            if (!_initialized) return null;
            return _registry != null
                ? _registry.GetEvent(eventName)
                : _library?.GetEvent(eventName);
        }

        /// <summary>
        /// 使用指定 AudioClip 播放环境音（用于 Large 模式批量预分配 clip，绕过 GetRandomClip）
        /// </summary>
        /// <param name="scheduledDspTime">大于 0 时使用 PlayScheduled 实现多点同步起播</param>
        internal static AudioHandle AmbientWithClip(string eventName, AudioClip clip, double scheduledDspTime = 0)
        {
            return AmbientWithClip(eventName, clip, Vector3.zero, scheduledDspTime);
        }

        internal static AudioHandle AmbientWithClip(string eventName, AudioClip clip, Vector3 position, double scheduledDspTime = 0)
        {
            if (!_initialized || clip == null) return AudioHandle.Empty;

            var entry = _registry != null
                ? _registry.GetEvent(eventName)
                : _library?.GetEvent(eventName);
            if (entry == null) return AudioHandle.Empty;

            var handle = SFXManager.PlayEventWithClip(entry, clip, position, null, AudioGroup.Ambient, null, scheduledDspTime);

            if (handle != AudioHandle.Empty)
            {
                VoiceManager.RegisterVoice(handle);
                TrackHandle(eventName, AudioGroup.Ambient, handle);
            }

            return handle;
        }

        #endregion

        #region 停止方法

        /// <summary>
        /// 停止指定事件的所有实例
        /// </summary>
        public static void Stop(string eventName, float fadeOut = 0.2f)
        {
            AudioHandle[] handlesToStop;

            lock (_handleLock)
            {
                if (!_activeHandles.TryGetValue(eventName, out var handles))
                    return;

                handlesToStop = handles.ToArray();
                handles.Clear();
            }

            foreach (var handle in handlesToStop)
            {
                handle.Stop(fadeOut);
            }
        }

        /// <summary>
        /// 停止目标对象上的所有音频
        /// </summary>
        public static void StopAll(GameObject target, float fadeOut = 0.2f)
        {
            if (target == null)
                return;

            List<AudioHandle> handlesToStop = new List<AudioHandle>();

            lock (_handleLock)
            {
                foreach (var kvp in _activeHandles)
                {
                    foreach (var handle in kvp.Value)
                    {
                        if (handle.Source != null && handle.Source.transform.IsChildOf(target.transform))
                        {
                            handlesToStop.Add(handle);
                        }
                    }
                }
            }

            foreach (var handle in handlesToStop)
            {
                handle.Stop(fadeOut);
            }
        }

        /// <summary>
        /// 停止所有音频
        /// </summary>
        public static void StopAll(float fadeOut = 0.5f)
        {
            List<AudioHandle> handlesToStop = new List<AudioHandle>();

            lock (_handleLock)
            {
                foreach (var kvp in _activeHandles)
                {
                    handlesToStop.AddRange(kvp.Value);
                    kvp.Value.Clear();
                }
            }

            foreach (var handle in handlesToStop)
            {
                handle.Stop(fadeOut);
            }
        }

        /// <summary>
        /// 停止指定分组的所有音频
        /// </summary>
        public static void StopGroup(AudioGroup group, float fadeOut = 0.5f)
        {
            AudioHandle[] handlesToStop;

            lock (_handleLock)
            {
                if (!_handlesByGroup.TryGetValue(group, out var handles))
                    return;

                handlesToStop = handles.ToArray();
                handles.Clear();
            }

            foreach (var handle in handlesToStop)
            {
                handle.Stop(fadeOut);
            }
        }

        #endregion

        #region 混音器控制

        /// <summary>
        /// 设置分组音量
        /// </summary>
        public static void SetVolume(AudioGroup group, float volume)
        {
            AudioMixerController.SetVolume(group, volume);
        }

        /// <summary>
        /// 获取分组音量
        /// </summary>
        public static float GetVolume(AudioGroup group)
        {
            return AudioMixerController.GetVolume(group);
        }

        /// <summary>
        /// 设置分组静音
        /// </summary>
        public static void SetMute(AudioGroup group, bool mute)
        {
            AudioMixerController.SetMute(group, mute);
        }

        /// <summary>
        /// 获取分组静音状态
        /// </summary>
        public static bool IsMuted(AudioGroup group)
        {
            return AudioMixerController.IsMuted(group);
        }

        /// <summary>
        /// 切换分组静音状态
        /// </summary>
        public static void ToggleMute(AudioGroup group)
        {
            AudioMixerController.ToggleMute(group);
        }

        /// <summary>
        /// 切换到混音器快照
        /// </summary>
        public static void TransitionToSnapshot(string snapshotName, float time = 1f)
        {
            AudioMixerController.TransitionToSnapshot(snapshotName, time);
        }

        /// <summary>
        /// 设置辅助发送电平（通过混音器参数路径）
        /// </summary>
        /// <param name="sendPath">混音器参数路径，如 "SFX_ReverbSend"</param>
        /// <param name="level">电平值 (0-1)</param>
        /// <example>
        /// Audio.SetAuxSendLevel("SFX_ReverbSend", 0.5f);
        /// </example>
        public static void SetAuxSendLevel(string sendPath, float level)
        {
            AudioAuxBusController.SetAuxSendLevel(sendPath, level);
        }

        /// <summary>
        /// 设置分组到辅助总线的发送电平
        /// </summary>
        /// <param name="group">音频分组</param>
        /// <param name="auxBusName">辅助总线名称（Reverb, Delay 等）</param>
        /// <param name="level">电平值 (0-1)</param>
        /// <example>
        /// Audio.SetAuxSendLevel(AudioGroup.SFX, "Reverb", 0.8f);
        /// Audio.SetAuxSendLevel(AudioGroup.Voice, "Delay", 0.3f);
        /// </example>
        public static void SetAuxSendLevel(AudioGroup group, string auxBusName, float level)
        {
            AudioAuxBusController.SetGroupAuxSendLevel(group, auxBusName, level);
        }

        /// <summary>
        /// 获取辅助发送电平
        /// </summary>
        /// <param name="sendPath">混音器参数路径</param>
        /// <returns>电平值 (0-1)</returns>
        public static float GetAuxSendLevel(string sendPath)
        {
            return AudioAuxBusController.GetAuxSendLevel(sendPath);
        }

        /// <summary>
        /// 获取分组到辅助总线的发送电平
        /// </summary>
        /// <param name="group">音频分组</param>
        /// <param name="auxBusName">辅助总线名称</param>
        /// <returns>电平值 (0-1)</returns>
        public static float GetAuxSendLevel(AudioGroup group, string auxBusName)
        {
            return AudioAuxBusController.GetGroupAuxSendLevel(group, auxBusName);
        }

        #endregion

        #region State 系统 (Phase 2)

        /// <summary>
        /// 设置全局音频状态
        /// </summary>
        /// <param name="groupName">状态组名称，如 "Environment", "GameState"</param>
        /// <param name="stateName">状态名称，如 "Underwater", "Combat"</param>
        /// <example>
        /// Audio.SetState("Environment", "Underwater");  // 切换到水下效果
        /// Audio.SetState("GameState", "Combat");        // 切换到战斗混音
        /// </example>
        public static void SetState(string groupName, string stateName)
        {
            AudioStateController.SetState(groupName, stateName);
        }

        /// <summary>
        /// 获取当前状态
        /// </summary>
        public static string GetState(string groupName)
        {
            return AudioStateController.GetState(groupName);
        }

        /// <summary>
        /// 重置状态到默认
        /// </summary>
        public static void ResetState(string groupName)
        {
            AudioStateController.ResetState(groupName);
        }

        /// <summary>
        /// 检查是否处于指定状态
        /// </summary>
        public static bool IsInState(string groupName, string stateName)
        {
            return AudioStateController.IsInState(groupName, stateName);
        }

        #endregion

        #region Switch 系统 (Phase 2)

        /// <summary>
        /// 设置全局 Switch 值
        /// </summary>
        /// <param name="groupName">Switch 组名称，如 "FootstepMaterial"</param>
        /// <param name="variantName">变体名称，如 "Metal", "Wood"</param>
        /// <example>
        /// Audio.SetSwitch("FootstepMaterial", "Metal");
        /// </example>
        public static void SetSwitch(string groupName, string variantName)
        {
            AudioSwitchController.SetSwitch(groupName, variantName);
        }

        /// <summary>
        /// 设置对象级 Switch 值
        /// </summary>
        /// <example>
        /// Audio.SetSwitch(player, "FootstepMaterial", "Metal");
        /// Audio.Play("Footstep").On(player);  // 自动播放金属脚步声
        /// </example>
        public static void SetSwitch(GameObject target, string groupName, string variantName)
        {
            AudioSwitchController.SetSwitch(target, groupName, variantName);
        }

        /// <summary>
        /// 设置对象级 Switch 值（Component 版本）
        /// </summary>
        public static void SetSwitch(Component target, string groupName, string variantName)
        {
            AudioSwitchController.SetSwitch(target, groupName, variantName);
        }

        /// <summary>
        /// 获取对象的 Switch 值
        /// </summary>
        public static string GetSwitch(GameObject target, string groupName)
        {
            return AudioSwitchController.GetSwitch(target, groupName);
        }

        /// <summary>
        /// 清除对象的所有 Switch 值
        /// </summary>
        public static void ClearSwitches(GameObject target)
        {
            AudioSwitchController.ClearSwitches(target);
        }

        #endregion

        #region Parameter 参数系统 (Phase 2)

        /// <summary>
        /// 设置全局参数
        /// </summary>
        /// <param name="paramName">参数名称，如 "Health", "Speed"</param>
        /// <param name="value">参数值</param>
        /// <example>
        /// Audio.SetParameter("Health", 0.3f);  // 低血量效果
        /// Audio.SetParameter("Speed", 0.8f);   // 速度相关音效
        /// </example>
        public static void SetParameter(string paramName, float value)
        {
            AudioParameterController.SetGlobal(paramName, value);
        }

        /// <summary>
        /// 设置对象级参数
        /// </summary>
        /// <example>
        /// Audio.SetParameter("Speed", 0.8f, player);
        /// </example>
        public static void SetParameter(string paramName, float value, GameObject target)
        {
            AudioParameterController.SetOnObject(paramName, value, target);
        }

        /// <summary>
        /// 设置对象级参数（Component 版本）
        /// </summary>
        public static void SetParameter(string paramName, float value, Component target)
        {
            AudioParameterController.SetOnObject(paramName, value, target);
        }

        /// <summary>
        /// 获取全局参数值
        /// </summary>
        public static float GetParameter(string paramName)
        {
            return AudioParameterController.GetGlobal(paramName);
        }

        /// <summary>
        /// 获取对象参数值
        /// </summary>
        public static float GetParameter(string paramName, GameObject target)
        {
            return AudioParameterController.GetOnObject(paramName, target);
        }

        /// <summary>
        /// 重置全局参数到默认值
        /// </summary>
        public static void ResetParameter(string paramName)
        {
            AudioParameterController.ResetGlobal(paramName);
        }

        /// <summary>
        /// 设置全局参数（带斜坡时间）
        /// </summary>
        /// <param name="paramName">参数名称</param>
        /// <param name="targetValue">目标值</param>
        /// <param name="rampTime">斜坡时间（秒）</param>
        /// <param name="curve">插值曲线类型</param>
        /// <example>
        /// Audio.SetParameterWithRamp("Health", 0.3f, 2f);  // 2 秒内渐变到 0.3
        /// Audio.SetParameterWithRamp("Intensity", 1f, 0.5f, ParameterRampCurve.EaseIn);
        /// </example>
        public static void SetParameterWithRamp(string paramName, float targetValue, float rampTime, ParameterRampCurve curve = ParameterRampCurve.Linear)
        {
            AudioParameterController.SetGlobalWithRamp(paramName, targetValue, rampTime, curve);
        }

        /// <summary>
        /// 停止参数斜坡
        /// </summary>
        public static void StopParameterRamp(string paramName, GameObject target = null)
        {
            AudioParameterController.StopRamp(paramName, target);
        }

        #endregion

        #region Localization 本地化 (Phase 2)

        /// <summary>
        /// 设置当前语言
        /// </summary>
        /// <param name="languageCode">语言代码，如 "zh-CN", "en-US", "ja-JP"</param>
        /// <example>
        /// Audio.SetLanguage("zh-CN");
        /// Audio.Voice("VO_NPC_Greeting");  // 自动播放中文语音
        /// </example>
        public static void SetLanguage(string languageCode)
        {
            AudioLocalizationController.SetLanguage(languageCode);
        }

        /// <summary>
        /// 获取当前语言
        /// </summary>
        public static string GetLanguage()
        {
            return AudioLocalizationController.CurrentLanguage;
        }

        /// <summary>
        /// 获取所有支持的语言
        /// </summary>
        public static string[] GetSupportedLanguages()
        {
            return AudioLocalizationController.GetSupportedLanguages();
        }

        #endregion

        #region 扩展初始化 (Phase 2)

        /// <summary>
        /// 初始化 State 系统
        /// </summary>
        public static void InitializeStates(AudioStateConfig config)
        {
            AudioStateController.Initialize(config);
        }

        /// <summary>
        /// 初始化 Switch 系统
        /// </summary>
        public static void InitializeSwitches(AudioSwitchConfig config)
        {
            AudioSwitchController.Initialize(config);
        }

        /// <summary>
        /// 初始化 Parameter 系统
        /// </summary>
        public static void InitializeParameters(AudioParameterConfig config)
        {
            AudioParameterController.Initialize(config);
        }

        /// <summary>
        /// 初始化本地化系统
        /// </summary>
        public static void InitializeLocalization(LocalizedAudioConfig config)
        {
            AudioLocalizationController.Initialize(config);
        }

        /// <summary>
        /// 初始化交互音乐系统（从 EventLibrary 获取配置）
        /// </summary>
        public static void InitializeMusicFromLibrary()
        {
            if (_registry != null)
                MusicStateManager.InitializeFromRegistry(_registry);
            else if (_library != null)
                MusicStateManager.InitializeFromLibrary(_library);
            else
                AudioProfilerHelper.LogWarning(AudioObjectType.SceneMusic, "InitializeMusicFromLibrary", "no library or registry configured");
        }

        #endregion

        #region 交互音乐 (Phase 3)

        /// <summary>
        /// 切换音乐状态
        /// </summary>
        /// <param name="stateName">状态名称</param>
        /// <example>
        /// Audio.MusicState("Exploration");
        /// Audio.MusicState("Combat");
        /// </example>
        public static void MusicState(string stateName)
        {
            if (!_isFullyReady)
            {
                AudioProfilerHelper.LogWarning(AudioObjectType.SceneMusic, stateName, "音频子系统尚未就绪，MusicState 调用被忽略");
                return;
            }
            MusicStateManager.SetState(stateName);
        }

        /// <summary>
        /// 切换音乐状态（指定过渡方式）
        /// </summary>
        /// <param name="stateName">状态名称</param>
        /// <param name="transition">过渡类型</param>
        /// <example>
        /// Audio.MusicState("Combat", MusicTransitionType.NextBar);
        /// Audio.MusicState("Victory", MusicTransitionType.NextBeat);
        /// </example>
        public static void MusicState(string stateName, MusicTransitionType transition)
        {
            if (!_isFullyReady)
            {
                AudioProfilerHelper.LogWarning(AudioObjectType.SceneMusic, stateName, "音频子系统尚未就绪，MusicState 调用被忽略");
                return;
            }
            MusicStateManager.SetState(stateName, transition);
        }

        /// <summary>
        /// 停止音乐
        /// </summary>
        public static void StopMusic(float fadeOut = 1f)
        {
            if (!_isFullyReady) return;
            MusicStateManager.Stop(fadeOut);
        }

        /// <summary>
        /// 暂停音乐
        /// </summary>
        public static void PauseMusic()
        {
            if (!_isFullyReady) return;
            MusicStateManager.Pause();
        }

        /// <summary>
        /// 恢复音乐
        /// </summary>
        public static void ResumeMusic()
        {
            if (!_isFullyReady) return;
            MusicStateManager.Resume();
        }

        /// <summary>
        /// 设置音乐层启用状态
        /// </summary>
        /// <param name="layerName">层名称</param>
        /// <param name="enabled">是否启用</param>
        /// <example>
        /// Audio.SetMusicLayer("Drums", true);
        /// Audio.SetMusicLayer("Strings", false);
        /// </example>
        public static void SetMusicLayer(string layerName, bool enabled)
        {
            if (!_isFullyReady) return;
            MusicStateManager.SetLayer(layerName, enabled);
        }

        /// <summary>
        /// 设置音乐层启用状态（指定淡入淡出时间）
        /// </summary>
        public static void SetMusicLayer(string layerName, bool enabled, float fadeTime)
        {
            if (!_isFullyReady) return;
            MusicStateManager.SetLayer(layerName, enabled, fadeTime);
        }

        /// <summary>
        /// 设置音乐层音量
        /// </summary>
        /// <param name="layerName">层名称</param>
        /// <param name="volume">音量 0-1</param>
        public static void SetMusicLayerVolume(string layerName, float volume)
        {
            if (!_isFullyReady) return;
            MusicStateManager.SetLayerVolume(layerName, volume);
        }

        /// <summary>
        /// 批量设置音乐层状态
        /// </summary>
        /// <example>
        /// Audio.SetMusicLayers(new Dictionary<string, bool> {
        ///     {"Drums", true},
        ///     {"Strings", false},
        ///     {"Bass", true}
        /// });
        /// </example>
        public static void SetMusicLayers(System.Collections.Generic.Dictionary<string, bool> layerStates, float fadeTime = 0.5f)
        {
            if (!_isFullyReady) return;
            MusicStateManager.SetLayers(layerStates, fadeTime);
        }

        /// <summary>
        /// 获取音乐层是否启用
        /// </summary>
        public static bool IsMusicLayerEnabled(string layerName)
        {
            return MusicStateManager.IsLayerEnabled(layerName);
        }

        /// <summary>
        /// 获取所有音乐层名称
        /// </summary>
        public static string[] GetMusicLayerNames()
        {
            return MusicStateManager.GetLayerNames();
        }

        /// <summary>
        /// 设置独立音乐轨道启用状态
        /// </summary>
        /// <param name="trackName">轨道名称</param>
        /// <param name="enabled">是否启用</param>
        /// <param name="fadeTime">淡入淡出时间（可选）</param>
        /// <example>
        /// Audio.SetMusicTrack("Percussion", true, fadeTime: 1f);
        /// Audio.SetMusicTrack("Bass", false);
        /// </example>
        public static void SetMusicTrack(string trackName, bool enabled, float? fadeTime = null)
        {
            if (!_isFullyReady) return;
            MusicStateManager.SetTrack(trackName, enabled, fadeTime);
        }

        /// <summary>
        /// 设置独立轨道音量
        /// </summary>
        /// <param name="trackName">轨道名称</param>
        /// <param name="volume">音量 0-1</param>
        public static void SetMusicTrackVolume(string trackName, float volume)
        {
            if (!_isFullyReady) return;
            MusicStateManager.SetTrackVolume(trackName, volume);
        }

        /// <summary>
        /// 获取独立轨道是否启用
        /// </summary>
        public static bool IsMusicTrackEnabled(string trackName)
        {
            return MusicStateManager.IsTrackEnabled(trackName);
        }

        /// <summary>
        /// 获取所有独立轨道名称
        /// </summary>
        public static string[] GetMusicTrackNames()
        {
            return MusicStateManager.GetTrackNames();
        }

        /// <summary>
        /// 播放 Stinger（一次性音乐覆盖）
        /// </summary>
        /// <param name="stingerName">Stinger 名称</param>
        /// <example>
        /// Audio.PlayStinger("Stinger_Victory");
        /// Audio.PlayStinger("Stinger_LevelUp");
        /// </example>
        public static void PlayStinger(string stingerName)
        {
            if (!_isFullyReady) return;
            MusicStateManager.PlayStinger(stingerName);
        }

        /// <summary>
        /// 获取当前音乐状态名称
        /// </summary>
        public static string GetCurrentMusicState()
        {
            return MusicStateManager.CurrentStateName;
        }

        /// <summary>
        /// 获取当前节拍信息
        /// </summary>
        public static BeatInfo GetMusicBeatInfo()
        {
            return MusicStateManager.CurrentBeatInfo;
        }

        /// <summary>
        /// 音乐是否正在过渡
        /// </summary>
        public static bool IsMusicTransitioning()
        {
            return MusicStateManager.IsTransitioning;
        }

        /// <summary>
        /// 节拍事件
        /// </summary>
        public static event System.Action<int, int> OnMusicBeat
        {
            add => MusicStateManager.OnBeat += value;
            remove => MusicStateManager.OnBeat -= value;
        }

        /// <summary>
        /// 小节事件
        /// </summary>
        public static event System.Action<int> OnMusicBar
        {
            add => MusicStateManager.OnBar += value;
            remove => MusicStateManager.OnBar -= value;
        }

        #endregion

        #region Music Switch Container

        /// <summary>
        /// 设置音乐 Switch 值（触发容器内音乐切换）
        /// </summary>
        /// <param name="groupName">Switch 组名称，如 "GameState"</param>
        /// <param name="value">Switch 值，如 "Combat", "Exploration"</param>
        /// <example>
        /// Audio.SetMusicSwitch("GameState", "Combat");  // 下一小节切换到战斗音乐
        /// Audio.SetMusicSwitch("GameState", "Exploration");  // 切回探索音乐
        /// </example>
        public static void SetMusicSwitch(string groupName, string value)
        {
            if (!_isFullyReady) return;
            AudioSwitchController.SetSwitch(groupName, value);
        }

        /// <summary>
        /// 获取当前音乐 Switch 值
        /// </summary>
        public static string GetMusicSwitch(string groupName)
        {
            return AudioSwitchController.GetSwitch(null, groupName);
        }

        /// <summary>
        /// 获取当前激活的 Music Switch Container 名称
        /// </summary>
        public static string GetActiveMusicContainer()
        {
            return MusicStateManager.ActiveContainerName;
        }

        /// <summary>
        /// 检查名称是否是 Music Switch Container
        /// </summary>
        public static bool IsMusicSwitchContainer(string name)
        {
            return MusicStateManager.IsSwitchContainer(name);
        }

        /// <summary>
        /// 初始化 Music Switch Container 系统
        /// </summary>
        public static void InitializeMusicSwitchContainers(MusicSwitchContainerConfig config)
        {
            MusicStateManager.InitializeSwitchContainers(config);
        }

        #endregion

        #region 内部方法

        private static bool CheckFrameLimit()
        {
            if (_settings == null)
                return true;

            int currentFrame = Time.frameCount;
            if (currentFrame != _lastFrameCount)
            {
                _lastFrameCount = currentFrame;
                _playsThisFrame = 0;
            }

            if (_playsThisFrame >= _settings.MaxSoundsPerFrame)
                return false;

            _playsThisFrame++;
            return true;
        }

        private static bool CheckCooldown(string eventName, float cooldown)
        {
            if (_lastPlayedTime.TryGetValue(eventName, out float lastTime))
            {
                if (Time.unscaledTime - lastTime < cooldown)
                    return false;
            }
            return true;
        }

        private static bool CheckInstanceLimit(string eventName, EventEntry entry)
        {
            if (entry.MaxInstances <= 0)
                return true;

            AudioHandle handleToStop = null;

            lock (_handleLock)
            {
                if (!_activeHandles.TryGetValue(eventName, out var handles))
                    return true;

                // 清理无效句柄
                handles.RemoveAll(h => !h.IsPlaying);

                if (handles.Count < entry.MaxInstances)
                    return true;

                // 根据抢占模式处理
                switch (entry.StealMode)
                {
                    case StealMode.None:
                        return false;

                    case StealMode.Oldest:
                        if (handles.Count > 0)
                        {
                            handleToStop = handles[0];
                            handles.RemoveAt(0);
                        }
                        break;

                    case StealMode.Quietest:
                        float minVolume = float.MaxValue;
                        foreach (var h in handles)
                        {
                            if (h.Source != null && h.Source.volume < minVolume)
                            {
                                minVolume = h.Source.volume;
                                handleToStop = h;
                            }
                        }
                        if (handleToStop != null)
                        {
                            handles.Remove(handleToStop);
                        }
                        break;

                    case StealMode.Farthest:
                        float maxDist = 0f;
                        var listener = AudioListenerManager.IsInstanced
                            ? AudioListenerManager.Instance.GetActiveListenerPosition()
                            : (Camera.main?.transform.position ?? Vector3.zero);
                        foreach (var h in handles)
                        {
                            if (h.Source != null)
                            {
                                float dist = Vector3.Distance(h.Source.transform.position, listener);
                                if (dist > maxDist)
                                {
                                    maxDist = dist;
                                    handleToStop = h;
                                }
                            }
                        }
                        if (handleToStop != null)
                        {
                            handles.Remove(handleToStop);
                        }
                        break;

                    default:
                        return false;
                }
            }

            // 在锁外执行 Stop，避免回调时的死锁
            if (handleToStop != null)
                handleToStop.Stop(0.05f);
            return true;
        }

        private static void TrackHandle(string eventName, AudioGroup group, AudioHandle handle)
        {
            lock (_handleLock)
            {
                if (!_activeHandles.ContainsKey(eventName))
                    _activeHandles[eventName] = new List<AudioHandle>();

                _activeHandles[eventName].Add(handle);

                if (_handlesByGroup.ContainsKey(group))
                    _handlesByGroup[group].Add(handle);

                // 注册句柄完成/停止时的自动清理回调（必须在锁内注册，确保追踪一致性）
                handle.OnComplete(_ => UntrackHandle(eventName, group, handle));
                handle.OnStop(_ => UntrackHandle(eventName, group, handle));
            }
        }

        /// <summary>
        /// 移除句柄追踪（内部使用）
        /// </summary>
        private static void UntrackHandle(string eventName, AudioGroup group, AudioHandle handle)
        {
            lock (_handleLock)
            {
                if (_activeHandles.TryGetValue(eventName, out var handles))
                    handles.Remove(handle);

                if (_handlesByGroup.TryGetValue(group, out var groupHandles))
                    groupHandles.Remove(handle);
            }
        }

        /// <summary>
        /// 清理所有无效（已停止）的句柄
        /// 建议定期调用（如场景切换时）
        /// </summary>
        public static void CleanupDeadHandles()
        {
            lock (_handleLock)
            {
                foreach (var kvp in _activeHandles)
                    kvp.Value.RemoveAll(h => !h.IsPlaying && !h.IsValid);

                foreach (var kvp in _handlesByGroup)
                    kvp.Value.RemoveAll(h => !h.IsPlaying && !h.IsValid);
            }
        }

        private static AudioObjectType AudioObjectTypeFromGroup(AudioGroup group)
        {
            return group switch
            {
                AudioGroup.Music => AudioObjectType.SceneMusic,
                AudioGroup.Voice => AudioObjectType.Voice,
                AudioGroup.Ambient => AudioObjectType.Ambient,
                AudioGroup.UI => AudioObjectType.UI,
                _ => AudioObjectType.CharacterSFX
            };
        }

        #endregion

        #region 全局 DSP 滤波

        /// <summary>
        /// 全局 DSP 控制器入口
        /// </summary>
        public static class GlobalDSP
        {
            public static void LowPass(float cutoff, float resonance = 1f, float rampTime = 0.3f)
                => GlobalDSPController.SetLowPass(cutoff, resonance, rampTime);

            public static void HighPass(float cutoff, float resonance = 1f, float rampTime = 0.3f)
                => GlobalDSPController.SetHighPass(cutoff, resonance, rampTime);

            public static void Reverb(AudioReverbPreset preset, float rampTime = 0.5f)
                => GlobalDSPController.SetReverb(preset, rampTime);

            public static void ApplyPreset(string presetName, float? transitionTime = null)
                => GlobalDSPController.ApplyPreset(presetName, transitionTime);

            public static void ClearAll(float rampTime = 0.3f)
                => GlobalDSPController.ClearPreset(rampTime);

            public static void RemoveLowPass(float rampTime = 0.3f)
                => GlobalDSPController.RemoveLowPass(rampTime);

            public static void RemoveHighPass(float rampTime = 0.3f)
                => GlobalDSPController.RemoveHighPass(rampTime);

            public static void RemoveReverb(float rampTime = 0.5f)
                => GlobalDSPController.RemoveReverb(rampTime);

            public static string CurrentPreset => GlobalDSPController.CurrentPreset;
        }

        #endregion

        #region Voice 虚拟化

        /// <summary>当前物理 Voice 数量</summary>
        public static int PhysicalVoiceCount => VoiceManager.PhysicalVoiceCount;

        /// <summary>当前虚拟 Voice 数量</summary>
        public static int VirtualVoiceCount => VoiceManager.VirtualVoiceCount;

        /// <summary>总 Voice 数量</summary>
        public static int TotalVoiceCount => VoiceManager.TotalVoiceCount;

        #endregion
    }
}
