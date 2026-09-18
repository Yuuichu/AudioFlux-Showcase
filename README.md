# AudioFlux

> **English** | [简体中文](README.zh-CN.md)

A self-built Unity audio middleware that replaces scattered `AudioSource` calls with an Event / Switch / State / RTPC data-driven model, built around two hard constraints: a zero-allocation runtime and a first-class editor toolchain.

## Why I Built This

Game audio in Unity usually degrades into `AudioSource` instances scattered across gameplay scripts: who owns the source, who stops it, what happens on scene change, and why a sound is still playing after the object is destroyed are all answered implicitly and inconsistently. Commercial middleware solves this, but it also dictates the pipeline, the asset format and the licence.

I wanted the Wwise mental model — events, switches, states, parameters, buses — without the vendor lock-in, and I wanted to find out what a zero-GC audio runtime on top of Unity's mixer actually costs to build. AudioFlux is the result: a runtime plus editor toolchain that I designed and wrote end to end.

## What It Does

- **Event-driven playback.** Gameplay asks for a named event; the middleware resolves it to a clip (or clip set), routing and playback rules through a data-driven registry, and returns a handle instead of an `AudioSource`.
- **Parameter, Switch and State layers.** Continuous parameters (RTPC-style), mutually exclusive switch variants, and global states drive mixer snapshots, pitch, volume and music behaviour without gameplay code touching the mixer.
- **Interactive music.** Layered, synchronised music with six transition types and nested switch trees, plus stingers that can duck the music bed.
- **Spatial audio.** Multiple positioning strategies, room acoustics, reverb zones, occlusion and multi-position emitters.
- **Editor toolchain.** Profiler window, emitter authoring and batch attachment, scene prefab scanning, event-library import, and a command-line helper for setup / scan / validate / health checks.

## Workflow

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

A designer authors event libraries, mixer snapshots and emitter presets as assets; gameplay only calls event names and sets parameters. Nothing in the hot path allocates.

## Technical Highlights

- **83 C# source files / 24,867 lines** covering spatial audio, interactive music, narrative voice, DSP and profiling. The only third-party namespace used across the runtime is `Cysharp.Threading.Tasks` (UniTask).
- **Positioning strategies:** `Simple`, `Large`, `MultiPosition` and `ClosestPoint`, plus `AudioRoom`, `AudioReverbZone` and `AudioOcclusionManager` for room acoustics and occlusion.
- **Interactive music:** `MusicTransitionType` implements exactly six transitions — `Immediate`, `CrossFade`, `NextBeat`, `NextBar`, `NextSyncPoint`, `EndOfTrack` — with nested switch trees, per-layer fade-in and stinger ducking.
- **Zero-GC hot path:** a pre-allocated fixed-capacity `AudioSource` pool with round-robin acquisition, and struct handles returned by value instead of class allocations.
- **Profiling that costs nothing in release:** profiler instrumentation is guarded by `[Conditional("UNITY_EDITOR")]`, so release builds compile the calls out entirely.
- **Editor tooling:** `AudioProfilerWindow` (log view and voice monitor), `AudioEmitterEditor`, `SegmentScenePrefabScanner`, `AudioEmitterBatchAttacher`, `AudioEventLibraryImporter`.

## Architecture

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

See `docs/architecture.md` for the layer breakdown and `docs/systems.md` for each subsystem.

## My Role

Sole designer and developer: audio architecture, runtime, data model, editor tooling and the command-line helper. No third-party audio middleware is used.

## Limitations

- **Wwise-style, not Wwise-compatible.** There is no SoundBank format, no WAAPI bridge, and event/asset authoring lives in Unity ScriptableObject assets rather than an external authoring tool.
- **Single-project validation.** The middleware has been driven by one production-style Unity project, not by multiple shipped titles; multi-project portability is designed for but not proven in the field.
- **The profiler is an in-editor tool**, not a runtime telemetry pipeline with remote capture or long-session history.
- **No audio content is included**, by design: the middleware is the artefact, not the sound design.

## Repository Scope

This is a portfolio showcase repository. The full development repository remains private.

Included: a curated set of runtime and editor sources in `selected-code/` (playback facade, handle, event registry, music state manager, source pool, spatial emitter, editor profiler window), plus architecture documentation. Excluded: project-specific configuration assets, internal path configuration, project/tooling scripts tied to a specific production project, AI-assistant instruction files, and all game audio content.

## Tech Stack

`C#` · `Unity` · `ScriptableObject`-driven data · `UniTask` · `AudioMixer` / snapshots · custom DSP chain · object pooling and zero-GC design · Editor extensions
