> [English](README.md) | **简体中文**

# AudioFlux

一个自建的 Unity 音频中间件，用 Event / Switch / State / RTPC 数据驱动模型取代散落各处的 `AudioSource` 调用；它围绕两条硬性约束构建：零分配的运行时，以及一等公民级别的编辑器工具链。

## 为什么做这个

Unity 里的游戏音频往往会退化成散落在各个玩法脚本中的 `AudioSource` 实例：谁持有这个 source、谁负责停止它、切场景时会发生什么、对象销毁后为什么还有声音在播——这些问题的答案都是隐式的，而且前后不一致。商业中间件能解决这些问题，但它同时也会规定你的管线、资源格式和许可方式。

我想要 Wwise 的心智模型——事件、Switch、State、参数、总线——但不要被厂商锁定；同时我也想弄清楚，在 Unity 混音器之上做一个零 GC 音频运行时，真实代价到底是多少。AudioFlux 就是这个问题的答案：一套运行时加编辑器工具链，由我从头到尾设计和编写。

## 功能概览

- **事件驱动播放。** 玩法代码请求一个具名事件；中间件通过数据驱动的注册表把它解析为音频片段（或片段集合）、路由与播放规则，并返回一个句柄而非 `AudioSource`。
- **Parameter、Switch 与 State 三层。** 连续参数（RTPC 风格）、互斥的 Switch 变体以及全局 State 共同驱动混音器快照、音高、音量与音乐行为，玩法代码无需接触混音器。
- **交互式音乐。** 分层且同步的音乐，具备六种过渡类型与嵌套 Switch 树，还有可以对音乐床做闪避的 stinger。
- **空间音频。** 多种定位策略、房间声学、混响区域、遮挡，以及多位置发声器。
- **编辑器工具链。** 性能分析窗口、发声器编辑与批量挂载、场景预制体扫描、事件库导入，以及用于 setup / scan / validate / health 检查的命令行助手。

## 工作流

```text
Gameplay code
      |
      v
Event / State / Switch / Parameter          <- authored as ScriptableObject assets
      |
      v
AudioFlux runtime (registry, handles, controllers)
      |
      v
AudioSource pool -> Mixer / Snapshot -> DSP chain -> Spatial -> output
```

设计师以资源形式编写事件库、混音器快照与发声器预设；玩法代码只调用事件名并设置参数。热路径上没有任何分配。

## 技术要点

- **83 个 C# 源文件 / 24,867 行**，覆盖空间音频、交互式音乐、叙事语音、DSP 与性能分析。整个运行时唯一使用的第三方命名空间是 `Cysharp.Threading.Tasks`（UniTask）。
- **定位策略：** `Simple`、`Large`、`MultiPosition` 与 `ClosestPoint`，另有 `AudioRoom`、`AudioReverbZone` 和 `AudioOcclusionManager` 负责房间声学与遮挡。
- **交互式音乐：** `MusicTransitionType` 恰好实现六种过渡——`Immediate`、`CrossFade`、`NextBeat`、`NextBar`、`NextSyncPoint`、`EndOfTrack`——并支持嵌套 Switch 树、逐层淡入与 stinger 闪避。
- **零 GC 热路径：** 一个预分配的固定容量 `AudioSource` 池，采用轮转方式取用；句柄以结构体按值返回，而不是分配类实例。
- **在发行版中零成本的分析能力：** 分析器埋点由 `[Conditional("UNITY_EDITOR")]` 保护，因此发行构建会把调用完全编译剔除。
- **编辑器工具：** `AudioProfilerWindow`（日志视图与声部监视）、`AudioEmitterEditor`、`SegmentScenePrefabScanner`、`AudioEmitterBatchAttacher`、`AudioEventLibraryImporter`。

## 架构

```text
Gameplay
   |
   v
Event / State / Switch / Parameter
   |
   v
AudioFlux Runtime
   |
   v
AudioSource Pool / Mixer / DSP / Spatial
   |
   v
Output
```

分层说明见 `docs/architecture.zh-CN.md`，各子系统见 `docs/systems.zh-CN.md`。

## 我的角色

独立设计与开发者：音频架构、运行时、数据模型、编辑器工具链以及命令行助手。未使用任何第三方音频中间件。

## 局限与边界

- **是 Wwise 风格，而非 Wwise 兼容。** 没有 SoundBank 格式，没有 WAAPI 桥接，事件与资源的编写都放在 Unity 的 ScriptableObject 资源里，而不是外部的编写工具中。
- **仅在单一项目上验证。** 这套中间件由一个接近生产形态的 Unity 项目驱动，而不是多个已上市项目；多项目可移植性是设计目标，但尚未在实战中验证。
- **分析器是编辑器内工具**，而非具备远程采集或长时段历史记录的运行时遥测管线。
- **不包含任何音频内容**，这是刻意的：这个中间件本身就是交付物，而不是声音设计作品。

## 仓库范围

这是一个作品集展示仓库。完整开发仓库保持私有。

包含：`selected-code/` 中一组精选的运行时与编辑器源码（播放门面、句柄、事件注册表、音乐状态管理器、音源池、空间发声器、编辑器分析窗口），以及架构文档。不包含：项目特定的配置资源、内部路径配置、与特定生产项目绑定的项目/工具脚本、AI 助手指令文件，以及全部游戏音频内容。

## 技术栈

`C#` · `Unity` · `ScriptableObject` 驱动的数据 · `UniTask` · `AudioMixer` / 快照 · 自研 DSP 链 · 对象池与零 GC 设计 · 编辑器扩展
