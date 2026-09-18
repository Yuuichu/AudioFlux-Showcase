# Subsystems

Each subsystem below exists as its own top-level folder in the repository.

## Spatial audio

- **`Spatial/AudioEmitter.cs`** — the emitter component: places a sound in the world and drives the chosen positioning strategy, including the multi-position tick path.
- Four positioning strategies are supported: `Simple`, `Large`, `MultiPosition` and `ClosestPoint`.
- **`AudioRoom`** and **`AudioReverbZone`** provide room acoustics and reverb zones.
- **`AudioOcclusionManager`** handles occlusion between listener and source.
- **`MultiPositionManager`** resolves which of several emitter points the listener should actually hear — sorted by listener distance with far-point culling and per-point gain offsets.

## Interactive music

- **`MusicStateManager`** owns the active music state and performs internal state transitions, including crossfade handling.
- **`MusicSwitchResolver`** resolves the nested switch tree with re-entrancy protection, so a switch change cannot recursively re-trigger itself.
- **`MusicTransitionType`** defines six transitions: `Immediate`, `CrossFade`, `NextBeat`, `NextBar`, `NextSyncPoint`, `EndOfTrack`.
- **Music containers** support vertical layering (simultaneous layers) and switch trees (mutually exclusive variants), with per-layer fade-in.
- **Stingers** can be played over the current music and can duck the bed (the archive of this system references a −12 dB duck) without interrupting the music state.

## Voice

- **`Voice/`** handles narrative voice playback and voice ducking: dialogue can duck music and ambience, and competing voice lines are arbitrated rather than played on top of each other.

## SFX

- **`SFX/SFXManager.cs`** covers one-shot effects, continuous/looping effects, and the continuous-stop path that releases the pooled source deterministically.

## DSP

- **`DSP/AudioDSPChain.cs`** is a custom DSP chain — a sequence of filters applied to a voice or bus (low-pass, high-pass, custom stages) rather than a single hardcoded filter.
- DSP presets are authored as assets (`DSPPresetLibrary`) so chains can be reused across events.

## Profiling

- **`Profiler/AudioProfilerHelper.cs`** and **`AudioProfilerMessage.cs`** carry instrumentation data from the runtime.
- **`Profiler/Editor/AudioProfilerWindow.cs`** is the editor-side consumer: a log view plus a voice monitor, roughly the Unity equivalent of the Wwise capture window.
- Everything here is marked `[Conditional("UNITY_EDITOR")]`, so the release player contains none of it.

## Voice pooling

- **`Pool/AudioSourcePool.cs`** pre-allocates a fixed number of `AudioSource` instances and hands them out round-robin. Pool capacity is fixed at construction; acquisition and release never allocate.

## Localization

- **`Localization/`** selects the correct voice or clip variant for the active language, so localized narrative voice does not require a separate playback path.

## Components

- **`Components/`** contains the MonoBehaviour entry points gameplay attaches (emitters, audio proxies, scene-level audio objects). These are thin: they translate Unity lifecycle events into middleware calls rather than owning playback state.