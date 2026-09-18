# Licence and third-party notice

## AudioFlux

Released under the **MIT licence** (`Copyright (c) 2026 Yuuichu`). The full licence text is included in this showcase as `LICENSE`, and the same file lives in the private development repository.

## Third-party components

AudioFlux depends on exactly one third-party library:

- **UniTask** (`Cysharp.Threading.Tasks`) — the only non-Unity, non-`System` namespace used by the runtime. Distributed under the MIT licence by its authors; not bundled in this showcase.

Everything else in the included sources is original work.

## What is not included

- No game audio content (clips, music, voice) — the middleware is the artefact.
- No project-specific configuration assets or path configuration.
- No production project names, paths or URLs.
- No AI-assistant instruction files from the development repository.

## Included sources

Files under `selected-code/` are verbatim copies from the private repository — except that any project-specific identifiers found in them were removed. They are provided for review of the implementation approach, not as a drop-in library: they reference the rest of the runtime, which is not included.