# Contributing

## Development tests against Civil 3D

A development test of this repository (a new recipe, a plugin build, a server change) must never
modify a real work project:

- Test on a throwaway copy of a drawing, made for the test. Never open a work-project drawing
  for writing during a test, and never change its sources: referenced drawings (xrefs), data
  shortcut files and folders, and GIS or survey source files (for example shapefiles).
- The copy needs the owner's explicit agreement when it is taken from a work project.
- Keep an unchanged, time-stamped copy of the test drawing before the first write, and follow
  the working order in [docs/USAGE.md](docs/USAGE.md): identify the drawing, write with
  `expectedDrawing`, `requireClean`, `backup` and `saveDrawing`, then read the result back
  independently.
- Do not close or restart Civil 3D sessions that someone else is working in.
- Record what was proven, and on which commit, in `docs/validation/`.
- The live smoke test ([docs/LIVE_SMOKE.md](docs/LIVE_SMOKE.md)) builds its own test drawing and
  needs no copy of a work drawing; run it after installing a new plugin and server.

## Checks before a pull request

- `node --test tests/errors/*.test.mjs tests/skills/*.test.mjs tests/transport/*.test.mjs tests/plugin/*.test.mjs tests/benchmark/benchmark.test.mjs`
- `npm run test:skills` compiles every recipe against the Civil 3D 2025 reference assemblies
  (see [C_References/README.md](C_References/README.md)).
- `npm run test:mcp`
- `npm run test:smoke` (the live smoke runner against a simulated Civil 3D)
- `npm run test:packaging` (the connector plugin in `packaging/claude-plugin/` and `packaging/codex-plugin/`)
