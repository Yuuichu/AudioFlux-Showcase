using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace AudioFlux
{
    /// <summary>
    /// AudioSource 对象池
    /// 通用的 AudioSource 池化管理，支持循环缓冲和抢占策略
    /// </summary>
    public class AudioSourcePool
    {
        private readonly List<AudioSource> _pool;
        private readonly Transform _parent;
        private readonly AudioMixerGroup _defaultMixerGroup;
        private readonly string _namePrefix;
        private int _poolIndex;
        private int _poolSize;

        /// <summary>
        /// 当前活跃（正在播放）的音源数量
        /// </summary>
        public int ActiveCount
        {
            get
            {
                int count = 0;
                foreach (var source in _pool)
                {
                    if (source != null && source.isPlaying)
                        count++;
                }
                return count;
            }
        }

        /// <summary>
        /// 池中总音源数量
        /// </summary>
        public int TotalCount => _pool.Count;

        /// <summary>
        /// 池容量
        /// </summary>
        public int Capacity => _poolSize;

        /// <summary>
        /// 创建 AudioSource 对象池
        /// </summary>
        /// <param name="size">池大小</param>
        /// <param name="parent">父 Transform（AudioSource 的 GameObject 会挂载在这里）</param>
        /// <param name="defaultMixerGroup">默认的 AudioMixerGroup（可为 null）</param>
        /// <param name="namePrefix">AudioSource 名称前缀</param>
        public AudioSourcePool(int size, Transform parent, AudioMixerGroup defaultMixerGroup = null, string namePrefix = "PooledAudioSource")
        {
            _poolSize = Mathf.Max(1, size);
            _parent = parent;
            _defaultMixerGroup = defaultMixerGroup;
            _namePrefix = namePrefix;
            _pool = new List<AudioSource>(_poolSize);
            _poolIndex = 0;

            InitializePool();
        }

        /// <summary>
        /// 获取可用的 AudioSource
        /// </summary>
        /// <param name="recycleIfFull">如果池满，是否抢占最旧的音源</param>
        /// <returns>可用的 AudioSource，如果不抢占且池满则返回 null</returns>
        public AudioSource GetAvailable(bool recycleIfFull = true)
        {
            // 查找未在播放的 AudioSource
            for (int i = 0; i < _poolSize; i++)
            {
                int index = (_poolIndex + i) % _poolSize;
                if (index < _pool.Count && _pool[index] != null && !_pool[index].isPlaying)
                {
                    _poolIndex = (index + 1) % _poolSize;
                    ResetSource(_pool[index]);
                    return _pool[index];
                }
            }

            if (!recycleIfFull)
                return null;

            // 所有 AudioSource 都在播放，停止最旧的并复用
            if (_poolIndex < _pool.Count && _pool[_poolIndex] != null)
            {
                var source = _pool[_poolIndex];
                source.Stop();
                ResetSource(source);
                _poolIndex = (_poolIndex + 1) % _poolSize;
                return source;
            }

            return null;
        }

        /// <summary>
        /// 归还 AudioSource 到池（停止播放）
        /// </summary>
        /// <param name="source">要归还的 AudioSource</param>
        public void ReturnToPool(AudioSource source)
        {
            if (source == null)
                return;

            if (!_pool.Contains(source))
                return;

            source.Stop();
            ResetSource(source);
        }

        /// <summary>
        /// 清空池（停止所有播放并销毁 GameObject）
        /// </summary>
        public void Clear()
        {
            foreach (var source in _pool)
            {
                if (source != null)
                {
                    source.Stop();
                    if (source.gameObject != null)
                    {
                        Object.Destroy(source.gameObject);
                    }
                }
            }
            _pool.Clear();
            _poolIndex = 0;
        }

        /// <summary>
        /// 停止所有正在播放的音源
        /// </summary>
        public void StopAll()
        {
            foreach (var source in _pool)
            {
                if (source != null && source.isPlaying)
                {
                    source.Stop();
                }
            }
        }

        /// <summary>
        /// 暂停所有正在播放的音源
        /// </summary>
        public void PauseAll()
        {
            foreach (var source in _pool)
            {
                if (source != null && source.isPlaying)
                {
                    source.Pause();
                }
            }
        }

        /// <summary>
        /// 恢复所有暂停的音源
        /// </summary>
        public void UnPauseAll()
        {
            foreach (var source in _pool)
            {
                if (source != null)
                {
                    source.UnPause();
                }
            }
        }

        /// <summary>
        /// 设置所有音源的 MixerGroup
        /// </summary>
        /// <param name="mixerGroup">要设置的 MixerGroup</param>
        public void SetMixerGroup(AudioMixerGroup mixerGroup)
        {
            foreach (var source in _pool)
            {
                if (source != null)
                {
                    source.outputAudioMixerGroup = mixerGroup;
                }
            }
        }

        /// <summary>
        /// 扩展池容量
        /// </summary>
        /// <param name="additionalCount">要增加的数量</param>
        public void Expand(int additionalCount)
        {
            if (additionalCount <= 0)
                return;

            int oldSize = _poolSize;
            _poolSize += additionalCount;

            for (int i = oldSize; i < _poolSize; i++)
            {
                var source = CreateAudioSource(i);
                _pool.Add(source);
            }
        }

        /// <summary>
        /// 获取池中所有 AudioSource（只读）
        /// </summary>
        public IReadOnlyList<AudioSource> GetAllSources()
        {
            return _pool;
        }

        private void InitializePool()
        {
            for (int i = 0; i < _poolSize; i++)
            {
                var source = CreateAudioSource(i);
                _pool.Add(source);
            }
        }

        private AudioSource CreateAudioSource(int index)
        {
            var sourceObj = new GameObject($"{_namePrefix}_{index}");
            sourceObj.transform.SetParent(_parent);
            sourceObj.transform.localPosition = Vector3.zero;

            var source = sourceObj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;

            if (_defaultMixerGroup != null)
            {
                source.outputAudioMixerGroup = _defaultMixerGroup;
            }

            return source;
        }

        private void ResetSource(AudioSource source)
        {
            if (source == null)
                return;

            source.clip = null;
            source.volume = 1f;
            source.pitch = 1f;
            source.loop = false;
            source.spatialBlend = 0f;
            source.panStereo = 0f;
            source.priority = 128;
            source.mute = false;

            // 清理 DSP Filter 组件
            AudioDSPChain.CleanupFilters(source.gameObject);

            if (_defaultMixerGroup != null)
            {
                source.outputAudioMixerGroup = _defaultMixerGroup;
            }
        }
    }
}
