using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AudioFlux
{
    /// <summary>
    /// 事件库类别（类似 Wwise SoundBank）
    /// </summary>
    public enum EventLibraryCategory
    {
        /// <summary>角色动作音效</summary>
        Act,
        /// <summary>物品音效</summary>
        Item,
        /// <summary>UI 音效</summary>
        UI,
        /// <summary>环境音效</summary>
        Ambient,
        /// <summary>音乐</summary>
        Music,
        /// <summary>语音</summary>
        Voice
    }

    /// <summary>
    /// 事件库引用配置
    /// </summary>
    [Serializable]
    public class EventLibraryReference
    {
        [Tooltip("事件库类别")]
        public EventLibraryCategory Category;

        [Tooltip("直接引用（Editor 可直接拖拽，优先于 AssetPath）")]
        public AudioEventLibrary DirectReference;

        [Tooltip("事件库资源路径（如 Audio/AudioEventLibrary_Music.asset，DirectReference 为空时走 LoaderManager）")]
        public string AssetPath;

        [Tooltip("是否在启动时预加载")]
        public bool PreloadOnStart = true;

        [Tooltip("已加载的事件库（运行时）")]
        [NonSerialized]
        public AudioEventLibrary LoadedLibrary;
    }

    /// <summary>
    /// 音频事件注册中心
    /// 管理多个 EventLibrary，支持按需加载（类似 Wwise SoundBank）
    /// </summary>
    [CreateAssetMenu(fileName = "AudioEventRegistry", menuName = "AudioFlux/Event Registry")]
    public class AudioEventRegistry : ScriptableObject
    {
        [Header("事件库配置")]
        [SerializeField]
        [Tooltip("事件库引用列表")]
        private EventLibraryReference[] _libraries;

        [Header("默认配置")]
        [SerializeField]
        [Tooltip("默认事件库（兼容旧系统，可选）")]
        private AudioEventLibrary _defaultLibrary;

        /// <summary>
        /// 事件库引用列表
        /// </summary>
        public EventLibraryReference[] Libraries => _libraries;

        /// <summary>
        /// 默认事件库
        /// </summary>
        public AudioEventLibrary DefaultLibrary => _defaultLibrary;

        // 运行时缓存
        private Dictionary<EventLibraryCategory, AudioEventLibrary> _loadedLibraries;
        private Dictionary<string, EventEntry> _eventCache;
        private readonly object _cacheLock = new object();

        // 初始化状态：0=未初始化, 1=初始化中, 2=已完成
        private int _initState;
        private const int INIT_STATE_NONE = 0;
        private const int INIT_STATE_INITIALIZING = 1;
        private const int INIT_STATE_COMPLETED = 2;

        // 兼容性属性（使用 Volatile.Read 保证跨线程可见性）
        private bool _isInitialized => Volatile.Read(ref _initState) == INIT_STATE_COMPLETED;
        private bool _isInitializing => Volatile.Read(ref _initState) == INIT_STATE_INITIALIZING;

        // 初始化超时时间（毫秒）
        private const int INIT_TIMEOUT_MS = 10000;

        /// <summary>
        /// 初始化注册中心（预加载标记的事件库）
        /// </summary>
        public async UniTask InitializeAsync()
        {
            // 原子检查并设置初始化状态
            int previousState = Interlocked.CompareExchange(ref _initState, INIT_STATE_INITIALIZING, INIT_STATE_NONE);

            if (previousState == INIT_STATE_COMPLETED)
            {
                // 已完成初始化
                return;
            }

            if (previousState == INIT_STATE_INITIALIZING)
            {
                // 其他任务正在初始化，带超时等待完成
                var timeoutTask = UniTask.Delay(INIT_TIMEOUT_MS);
                var waitTask = UniTask.WaitUntil(() => _initState == INIT_STATE_COMPLETED);
                var result = await UniTask.WhenAny(waitTask, timeoutTask);

                if (result == 1)
                {
                    // 超时
                    AudioFluxRuntime.Logger.Warning($"InitializeAsync 等待超时（{INIT_TIMEOUT_MS}ms），可能存在初始化死锁");
                }
                return;
            }

            try
            {
                _loadedLibraries = new Dictionary<EventLibraryCategory, AudioEventLibrary>();
                _eventCache = new Dictionary<string, EventEntry>();

                // 加载默认库
                if (_defaultLibrary != null)
                {
                    RegisterLibrary(_defaultLibrary);
                }

                // 预加载标记的事件库
                if (_libraries != null)
                {
                    foreach (var libRef in _libraries)
                    {
                        // 优先使用直接引用（无需 LoaderManager）
                        if (libRef.DirectReference != null)
                        {
                            libRef.LoadedLibrary = libRef.DirectReference;
                            // 跳过已作为 defaultLibrary 注册过的库，避免重复注册
                            if (libRef.DirectReference != _defaultLibrary)
                                RegisterLibrary(libRef.DirectReference, libRef.Category);
                        }
                        else if (libRef.PreloadOnStart && !string.IsNullOrEmpty(libRef.AssetPath))
                        {
                            await LoadLibraryAsync(libRef.Category);
                        }
                    }
                }

                // 标记完成
                Interlocked.Exchange(ref _initState, INIT_STATE_COMPLETED);
            }
            catch (Exception)
            {
                // 初始化失败，重置状态以允许重试
                Interlocked.Exchange(ref _initState, INIT_STATE_NONE);
                throw;
            }
        }

        /// <summary>
        /// 同步初始化（使用已配置的直接引用）
        /// </summary>
        public void Initialize()
        {
            // 原子检查并设置初始化状态
            int previousState = Interlocked.CompareExchange(ref _initState, INIT_STATE_INITIALIZING, INIT_STATE_NONE);

            if (previousState != INIT_STATE_NONE)
            {
                // 已在初始化中或已完成
                return;
            }

            try
            {
                _loadedLibraries = new Dictionary<EventLibraryCategory, AudioEventLibrary>();
                _eventCache = new Dictionary<string, EventEntry>();

                // 加载默认库
                if (_defaultLibrary != null)
                {
                    RegisterLibrary(_defaultLibrary);
                }

                // 注册直接引用的事件库
                if (_libraries != null)
                {
                    foreach (var libRef in _libraries)
                    {
                        if (libRef.DirectReference != null)
                        {
                            libRef.LoadedLibrary = libRef.DirectReference;
                            RegisterLibrary(libRef.DirectReference, libRef.Category);
                        }
                    }
                }

                Interlocked.Exchange(ref _initState, INIT_STATE_COMPLETED);
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref _initState, INIT_STATE_NONE);
                throw;
            }
        }

        /// <summary>
        /// 按需加载事件库
        /// </summary>
        public async UniTask<AudioEventLibrary> LoadLibraryAsync(EventLibraryCategory category)
        {
            // 快速路径：已加载则直接返回
            lock (_cacheLock)
            {
                if (_loadedLibraries != null && _loadedLibraries.TryGetValue(category, out var loaded))
                {
                    return loaded;
                }
            }

            // 查找配置
            var libRef = FindLibraryReference(category);
            if (libRef == null || string.IsNullOrEmpty(libRef.AssetPath))
            {
                AudioFluxRuntime.Logger.Warning($"未找到类别 {category} 的事件库配置");
                return null;
            }

            // 异步加载
            var library = await AudioFluxRuntime.AssetLoader.LoadAssetAsync<AudioEventLibrary>(libRef.AssetPath);
            if (library != null)
            {
                // 二次检查 + 原子注册（lock 可重入，RegisterLibrary 内部 lock 安全）
                lock (_cacheLock)
                {
                    if (_loadedLibraries != null && _loadedLibraries.TryGetValue(category, out var existing))
                    {
                        return existing;
                    }

                    libRef.LoadedLibrary = library;
                    RegisterLibrary(library, category);
                }
                AudioFluxRuntime.Logger.Info($"已加载事件库: {category} ({libRef.AssetPath})");
            }

            return library;
        }

        /// <summary>
        /// 卸载事件库
        /// </summary>
        public void UnloadLibrary(EventLibraryCategory category)
        {
            AudioEventLibrary library = null;

            lock (_cacheLock)
            {
                if (_loadedLibraries == null || !_loadedLibraries.TryGetValue(category, out library))
                    return;

                // 从缓存中移除该库的事件
                if (library.Events != null)
                {
                    foreach (var entry in library.Events)
                    {
                        if (!string.IsNullOrEmpty(entry.EventName))
                        {
                            _eventCache.Remove(entry.EventName);
                        }
                    }
                }

                _loadedLibraries.Remove(category);
            }

            // 在 lock 外释放资源（避免 AssetLoader 回调持锁）
            var libRef = FindLibraryReference(category);
            if (libRef != null)
            {
                if (!string.IsNullOrEmpty(libRef.AssetPath))
                {
                    AudioFluxRuntime.AssetLoader.UnloadAsset<AudioEventLibrary>(libRef.AssetPath);
                }
                libRef.LoadedLibrary = null;
            }

            AudioFluxRuntime.Logger.Info($"已卸载事件库: {category}");
        }

        /// <summary>
        /// 获取事件配置
        /// </summary>
        public EventEntry GetEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
                return null;

            // 检查是否正在初始化
            if (_isInitializing)
            {
                AudioFluxRuntime.Logger.Warning($"系统正在初始化中，无法获取事件: {eventName}");
                return null;
            }

            EnsureInitialized();

            lock (_cacheLock)
            {
                _eventCache.TryGetValue(eventName, out var entry);
                return entry;
            }
        }

        /// <summary>
        /// 尝试获取事件配置
        /// </summary>
        public bool TryGetEvent(string eventName, out EventEntry entry)
        {
            entry = GetEvent(eventName);
            return entry != null;
        }

        /// <summary>
        /// 检查事件库是否已加载
        /// </summary>
        public bool IsLibraryLoaded(EventLibraryCategory category)
        {
            lock (_cacheLock)
            {
                return _loadedLibraries != null && _loadedLibraries.ContainsKey(category);
            }
        }

        /// <summary>
        /// 获取音乐事件配置
        /// </summary>
        public MusicEventEntry GetMusicEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
                return null;

            EnsureInitialized();

            // 缓存 _defaultLibrary 引用，避免 TOCTOU
            var defaultLib = _defaultLibrary;

            lock (_cacheLock)
            {
                // 优先从 Music 分类库获取
                if (_loadedLibraries != null &&
                    _loadedLibraries.TryGetValue(EventLibraryCategory.Music, out var musicLibrary) &&
                    musicLibrary != null)
                {
                    var entry = musicLibrary.GetMusicEvent(eventName);
                    if (entry != null) return entry;
                }
            }

            // 回退到默认库（使用局部缓存引用）
            return defaultLib?.GetMusicEvent(eventName);
        }

        /// <summary>
        /// 获取 Stinger 配置
        /// </summary>
        public StingerEventEntry GetStinger(string stingerName)
        {
            if (string.IsNullOrEmpty(stingerName))
                return null;

            EnsureInitialized();

            // 缓存 _defaultLibrary 引用，避免 TOCTOU
            var defaultLib = _defaultLibrary;

            lock (_cacheLock)
            {
                // 优先从 Music 分类库获取
                if (_loadedLibraries != null &&
                    _loadedLibraries.TryGetValue(EventLibraryCategory.Music, out var musicLibrary) &&
                    musicLibrary != null)
                {
                    var entry = musicLibrary.GetStinger(stingerName);
                    if (entry != null) return entry;
                }
            }

            // 回退到默认库（使用局部缓存引用）
            return defaultLib?.GetStinger(stingerName);
        }

        /// <summary>
        /// 获取已加载的事件库
        /// </summary>
        public AudioEventLibrary GetLoadedLibrary(EventLibraryCategory category)
        {
            lock (_cacheLock)
            {
                if (_loadedLibraries != null && _loadedLibraries.TryGetValue(category, out var library))
                {
                    return library;
                }
                return null;
            }
        }

        /// <summary>
        /// 注册事件库（将事件添加到缓存）
        /// </summary>
        private void RegisterLibrary(AudioEventLibrary library, EventLibraryCategory? category = null)
        {
            if (library == null || library.Events == null)
                return;

            lock (_cacheLock)
            {
                foreach (var entry in library.Events)
                {
                    if (!string.IsNullOrEmpty(entry.EventName))
                    {
                        if (_eventCache.ContainsKey(entry.EventName))
                        {
                            AudioFluxRuntime.Logger.Warning($"事件名称重复: {entry.EventName}");
                        }
                        _eventCache[entry.EventName] = entry;
                    }
                }

                if (category.HasValue)
                {
                    _loadedLibraries[category.Value] = library;
                }
            }
        }

        /// <summary>
        /// 查找事件库引用
        /// </summary>
        private EventLibraryReference FindLibraryReference(EventLibraryCategory category)
        {
            if (_libraries == null)
                return null;

            foreach (var libRef in _libraries)
            {
                if (libRef.Category == category)
                    return libRef;
            }
            return null;
        }

        /// <summary>
        /// 确保已初始化
        /// </summary>
        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                Initialize();
            }
        }

        /// <summary>
        /// 清除缓存
        /// </summary>
        public void ClearCache()
        {
            // 先标记未初始化，阻止新的访问
            Interlocked.Exchange(ref _initState, INIT_STATE_NONE);

            lock (_cacheLock)
            {
                _eventCache?.Clear();
                _loadedLibraries?.Clear();
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            ClearCache();
        }
#endif
    }
}
