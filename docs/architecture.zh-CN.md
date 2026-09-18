> [English](architecture.md) | **简体中文**

# 架构

AudioFlux 把**该播什么**（编写好的数据）与**怎么播**（运行时）分离，并让玩法代码远离 `AudioSource` 与混音器内部实现。

## 分层

### 1. 编写层（ScriptableObject 资源）

设计师编写的是资源，而不是代码：

| 资源 | 职责 |
|---|---|
| `AudioSettings` | 全局运行时配置与资源引用 |
| `AudioEventLibrary` | 具名事件到音频片段、分组与播放规则的映射 |
| `AudioEventRegistry` | 由事件库构建出的、已解析的运行时索引 |
| `AudioSceneConfig` | 逐场景的音频配置 |
| `MusicSwitchContainerConfig` / Switch 配置 | 音乐容器、音乐层与 Switch 树 |
| `DSPPresetLibrary`、`VoiceConfig` | DSP 链预设与声部上限 |
| 发声器预设 | 空间发声器的默认值 |

### 2. 运行时层

`Audio.cs` 是游戏调用的静态门面：用配置初始化，播放或停止事件，设置参数、Switch 与 State。`AudioHandle` 是返回给玩法代码的值——一个轻量句柄，同时暴露底层声部状态。

`AudioEventRegistry` 按解析规则把请求的事件名解析为一个条目；`AudioEventRegistry` 与 `AudioEventLibrary` 保持分离，因此重新导入事件库数据不会扰动已解析的运行时索引。

各种控制器持有连续状态：

- `AudioParameterController` —— RTPC 风格的连续参数
- `AudioSwitchController` —— 互斥的 Switch 变体
- `AudioStateController` —— 全局 State
- `AudioMixerController` / `AudioAuxBusController` —— 混音器分组、快照与辅助总线
- `SceneAudioTransitionController`（以及 `SceneAudioTransitionProfile`）—— 切场景时正在播放的音频如何处理
- `AudioListenerManager`、`AudioProxyManager` —— 听者跟踪，以及为远距离发声器提供的代理对象

### 3. 播放层

- `SFXManager` —— 单次与持续音效，包括持续音效的停止路径
- `Music/` —— `MusicStateManager`、`MusicSwitchResolver` 与交互式音乐容器
- `Voice/` —— 叙事语音播放与闪避
- `Spatial/` —— 发声器、房间、混响、遮挡与多位置布置

### 4. 基础设施层

- `Pool/AudioSourcePool.cs` —— 预分配的固定容量池。池在构造时确定大小并以轮转方式复用条目，因此播放过程中永远不会分配 `AudioSource`。
- `DSP/` —— 自研 DSP 链（`AudioDSPChain`），包含低通、高通等滤波器
- `Profiler/` —— 埋点（`AudioProfilerHelper`、`AudioProfilerMessage`）以及编辑器窗口
- `Localization/` —— 本地化的语音/片段选择
- `Components/` —— 玩法代码挂载的 MonoBehaviour 入口点

### 5. 工具链层

- `Editor/` —— `AudioProfilerWindow`、`AudioEmitterEditor`、`SegmentScenePrefabScanner`、`AudioEmitterBatchAttacher`、`AudioEventLibraryImporter`
- `Tools/` —— 针对 Unity 项目执行 setup / scan / validate / health 检查的 Bash 命令行助手

## 设计规则

1. **玩法代码永不接触混音器或裸 `AudioSource`。** 它只请求事件、设置参数。
2. **编写数据对版本控制友好。** 配置存放在由 Unity 校验引用的资源里，而不是自由格式的 JSON 中。
3. **发行构建不为埋点付出任何成本。** 分析器调用由 `[Conditional("UNITY_EDITOR")]` 编译剔除。
4. **热路径不分配。** 句柄是按值返回的结构体，音源来自固定容量的池。

## 配置链

一次事件请求会经过一条简短的链路完成解析：请求的事件名 -> 注册表条目 -> 片段与分组选择 -> 句柄 -> 池中音源。由于解析是针对注册表而不是针对场景对象进行的，因此无论在场景切换之前、之中还是之后请求事件，行为都保持一致。

说明：本展示仓库有意省略了项目特定的路径常量；在完整仓库中，它们集中在一个路径配置类里，因此重新指向另一个项目时无需改动运行时。
