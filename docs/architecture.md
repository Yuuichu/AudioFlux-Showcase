# Architecture

> **English** | [简体中文](architecture.zh-CN.md)

AudioFlux separates **what should play** (authored data) from **how it plays** (runtime), and keeps gameplay code away from `AudioSource` and mixer internals.

## Layers

### 1. Authoring layer (ScriptableObject assets)

Designers author assets rather than code:

| Asset | Responsibility |
|---|---|
| `AudioSettings` | Global runtime configuration and asset references |
| `AudioEventLibrary` | Named events mapped to clips, groups and playback rules |
| `AudioEventRegistry` | The resolved runtime index built from libraries |
| `AudioSceneConfig` | Per-scene audio configuration |
| `MusicSwitchContainerConfig` / switch configs | Music containers, layers and switch trees |
| `DSPPresetLibrary`, `VoiceConfig` | DSP chain presets and voice limits |
| Emitter presets | Spatial emitter defaults |

### 2. Runtime layer

`Audio.cs` is the static facade the game talks to: initialize with settings, play or stop events, set parameters, switches and states. `AudioHandle` is the value returned to gameplay — a lightweight handle that also exposes the underlying voice state.

`AudioEventRegistry` resolves a requested event name to an entry, applying the resolution rules; `AudioEventRegistry` and `AudioEventLibrary` are kept separate so that library data can be re-imported without disturbing the resolved runtime index.

Controllers own the continuous state:

- `AudioParameterController` — RTPC-style continuous parameters
- `AudioSwitchController` — mutually exclusive switch variants
- `AudioStateController` — global states
- `AudioMixerController` / `AudioAuxBusController` — mixer groups, snapshots and aux buses
- `SceneAudioTransitionController` (+ `SceneAudioTransitionProfile`) — what happens to active audio across scene changes
- `AudioListenerManager`, `AudioProxyManager` — listener tracking and proxy objects for distant emitters

### 3. Playback layer

- `SFXManager` — one-shot and continuous effects, including the continuous-stop path
- `Music/` — `MusicStateManager`, `MusicSwitchResolver` and the interactive music container
- `Voice/` — narrative voice playback and ducking
- `Spatial/` — emitters, rooms, reverb, occlusion and multi-position placement

### 4. Infrastructure layer

- `Pool/AudioSourcePool.cs` — a pre-allocated, fixed-capacity pool. The pool is sized at construction and reuses entries round-robin, so playback never allocates an `AudioSource`.
- `DSP/` — a custom DSP chain (`AudioDSPChain`) with filters such as low-pass and high-pass
- `Profiler/` — instrumentation (`AudioProfilerHelper`, `AudioProfilerMessage`) plus the editor window
- `Localization/` — localized voice/clip selection
- `Components/` — the MonoBehaviour entry points that gameplay attaches

### 5. Toolchain layer

- `Editor/` — `AudioProfilerWindow`, `AudioEmitterEditor`, `SegmentScenePrefabScanner`, `AudioEmitterBatchAttacher`, `AudioEventLibraryImporter`
- `Tools/` — a Bash command-line helper for setup / scan / validate / health checks against a Unity project

## Design rules

1. **Gameplay never touches the mixer or a raw `AudioSource`.** It requests events and sets parameters.
2. **Authoring data is version-control friendly.** Configuration lives in assets whose references Unity validates, rather than in free-form JSON.
3. **Release builds pay nothing for instrumentation.** Profiler calls are compiled out with `[Conditional("UNITY_EDITOR")]`.
4. **The hot path does not allocate.** Handles are structs returned by value and sources come from a fixed pool.

## Configuration chain

An event request resolves through a small chain: requested event name -> registry entry -> clip and group selection -> handle -> pooled source. Because resolution happens against the registry rather than against scene objects, an event can be requested before, during or after a scene transition with consistent behaviour.

Note: project-specific path constants are intentionally omitted from this showcase; in the full repository they are centralised in a single path-configuration class so a project can be re-targeted without touching the runtime.