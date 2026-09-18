# Screenshots

This directory is intentionally empty in the draft. AudioFlux is a Unity runtime plus editor tooling, so its most convincing evidence is editor UI, and none of it could be captured automatically in this round (capturing requires opening the Unity project with its production assets).

## What should be captured

1. **`hero-profiler.png`** — `AudioProfilerWindow` during playback: log view plus voice monitor. This is the single most convincing image, because it shows the toolchain rather than the code.
2. **`inspector-event-library.png`** — an `AudioEventLibrary` asset in the inspector, showing the data-driven authoring model.
3. **`inspector-emitter.png`** — `AudioEmitterEditor` with spatial settings and the positioning strategy selector.
4. **`music-switch-tree.png`** — a music switch container with nested layers, showing the interactive music configuration.
5. **`pool-monitor.png`** — the source pool under load (capacity vs. active voices), which is the visible proof of the fixed-pool design.

## Constraints on what may be shown

Any capture must come from a scene that contains no third-party or client game content: no production art, no project names in window titles, no asset paths in the inspector that reveal a specific project. A minimal synthetic test scene is the right source.

Until these exist, the showcase makes no visual claims — the README describes the system and the selected sources show the implementation.