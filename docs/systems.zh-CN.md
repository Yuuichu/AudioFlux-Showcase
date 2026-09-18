> [English](systems.md) | **简体中文**

# 子系统

下面每个子系统在仓库中都作为独立的顶层文件夹存在。

## 空间音频

- **`Spatial/AudioEmitter.cs`** —— 发声器组件：把声音放置到世界中，并驱动所选的定位策略，包括多位置的每帧更新路径。
- 支持四种定位策略：`Simple`、`Large`、`MultiPosition` 和 `ClosestPoint`。
- **`AudioRoom`** 与 **`AudioReverbZone`** 提供房间声学与混响区域。
- **`AudioOcclusionManager`** 处理听者与音源之间的遮挡。
- **`MultiPositionManager`** 决定听者实际应该听到多个发声点中的哪一个——按听者距离排序，带远点剔除与逐点增益偏移。

## 交互式音乐

- **`MusicStateManager`** 持有当前音乐状态并执行内部状态过渡，包括交叉淡化处理。
- **`MusicSwitchResolver`** 在具备重入保护的前提下解析嵌套 Switch 树，因此一次 Switch 变更不会递归地再次触发自身。
- **`MusicTransitionType`** 定义了六种过渡：`Immediate`、`CrossFade`、`NextBeat`、`NextBar`、`NextSyncPoint`、`EndOfTrack`。
- **音乐容器**支持纵向分层（同时播放的层）与 Switch 树（互斥变体），并支持逐层淡入。
- **Stinger** 可以在当前音乐之上播放，并可以对音乐床做闪避（该系统的归档资料中提到 −12 dB 的闪避量），而不会打断音乐状态。

## 语音

- **`Voice/`** 负责叙事语音播放与语音闪避：对白可以闪避音乐与环境声，互相冲突的语音行会被仲裁，而不是简单叠加播放。

## 音效

- **`SFX/SFXManager.cs`** 覆盖单次音效、持续/循环音效，以及确定性地归还池中音源的持续音效停止路径。

## DSP

- **`DSP/AudioDSPChain.cs`** 是一条自研 DSP 链——施加在声部或总线上的滤波器序列（低通、高通、自定义级），而不是单个硬编码滤波器。
- DSP 预设以资源形式编写（`DSPPresetLibrary`），因此 DSP 链可以在多个事件之间复用。

## 性能分析

- **`Profiler/AudioProfilerHelper.cs`** 与 **`AudioProfilerMessage.cs`** 负责从运行时携带埋点数据。
- **`Profiler/Editor/AudioProfilerWindow.cs`** 是编辑器侧的消费方：一个日志视图加一个声部监视器，大致相当于 Unity 里的 Wwise 采集窗口。
- 这里的所有内容都标记为 `[Conditional("UNITY_EDITOR")]`，因此发行版播放器中不含任何相关代码。

## 声部池

- **`Pool/AudioSourcePool.cs`** 预分配固定数量的 `AudioSource` 实例并以轮转方式发放。池容量在构造时固定；获取与归还都不产生分配。

## 本地化

- **`Localization/`** 为当前语言选择正确的语音或片段变体，因此本地化的叙事语音不需要单独的播放路径。

## 组件

- **`Components/`** 包含玩法代码挂载的 MonoBehaviour 入口点（发声器、音频代理、场景级音频对象）。它们都很薄：只把 Unity 生命周期事件转译成中间件调用，而自身不持有播放状态。
