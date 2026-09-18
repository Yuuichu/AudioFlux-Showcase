# Zero-GC runtime and editor tooling

> **English** | [简体中文](zero-gc-and-tooling.zh-CN.md)

Two constraints shaped almost every design decision in AudioFlux: the runtime hot path must not allocate, and the editor toolchain must be good enough that a designer never has to open a script to find out what audio is doing.

## Why zero-GC, concretely

Unity's audio callbacks run on the main thread, and garbage collected during gameplay shows up as frame-time spikes that are hard to attribute to audio. The goal was that playing, stopping, re-targeting and parameterising audio produce **no managed allocations at all** in steady state.

How it is achieved:

1. **Fixed-capacity source pool.** `AudioSourcePool` allocates its sources once, at construction, and reuses them round-robin. Playback never instantiates a component.
2. **Struct handles returned by value.** Gameplay receives an `AudioHandle` value rather than a class reference, so requesting playback does not create a heap object that the GC has to trace later.
3. **No closure-based callbacks on the hot path.** Continuations that would capture variables and allocate are avoided in the playback path.
4. **Internally mutable, externally immutable.** `AudioHandle` exposes voice state through getters while its mutable fields stay internal, so the handle can be copied cheaply without exposing mutable state to gameplay.

## Why the profiler is compiled out

Instrumentation is written with `[Conditional("UNITY_EDITOR")]`. The call sites remain in the source, so the code stays readable and the data is available in the editor, but the compiler removes the calls entirely from release builds. Shipping a player therefore cannot pay a cost — not even an empty method call — for the profiler.

## The editor toolchain

| Tool | Purpose |
|---|---|
| `AudioProfilerWindow` | Log view plus voice monitor: what is playing, through which event, at what volume, and why |
| `AudioEmitterEditor` | Authoring emitters and their spatial settings in the inspector, rather than by hand-editing components |
| `SegmentScenePrefabScanner` | Scans scene prefabs for audio-relevant objects, so a scene's audio surface can be inventoried (nested prefab instances are not recursed into twice) |
| `AudioEmitterBatchAttacher` | Applies emitter configuration in bulk instead of one object at a time |
| `AudioEventLibraryImporter` | Imports event libraries into the authoring assets |
| `Tools/` (Bash CLI) | `setup` / `scan` / `validate` / `health` helpers for working against a Unity project from the command line |

The design intent is the same as the runtime's: a designer should be able to answer "what is playing and why" without asking an engineer, and an engineer should be able to validate a project's audio configuration without opening the editor.

Note: the batch-scanning and import tools are project-shape aware. They are summarised here rather than included, because their configuration is tied to a specific production project.