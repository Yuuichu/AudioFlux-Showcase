# Licence and third-party notice

## AudioFlux

The full AudioFlux repository is private, and no licence file is included in this showcase.

**Therefore this showcase deliberately makes no licence claim.** If the middleware is MIT-licensed, the canonical `LICENSE` file should be added to the main repository first, and this notice updated to match. Do not treat the absence of a licence here as permission to reuse the code.

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