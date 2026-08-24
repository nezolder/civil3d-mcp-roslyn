# Project Provenance

## Primary upstream

This project is currently a Civil 3D 2025-focused fork of [barbosaihan/civil3d-mcp](https://github.com/barbosaihan/civil3d-mcp), primarily based on its dynamic code-execution architecture. The repository name is intentionally not tied to one Civil 3D version so that separately verified compatibility can be added later. The original MIT license and copyright notice are retained in [LICENSE](LICENSE).

The fork preserves the central design: a small MCP tool surface delegates task-specific work to C# compiled with Roslyn and executed inside Civil 3D. Local development adds bounded, test-backed reliability, safety, measurement, discovery, and skill improvements without replacing that core.

## Comparative references

- [SantosSjba/mcp-to-c3d](https://github.com/SantosSjba/mcp-to-c3d) was evaluated as a related continuation of the same public lineage. Ideas are considered individually and, where used, are adapted in small changes with local tests. Its codebase has not been merged wholesale into this fork.
- [Sacred-G/Civil3D-mcp](https://github.com/Sacred-G/Civil3D-mcp) was reviewed only for architectural, packaging, and security ideas. No source code from that repository is intentionally copied into this fork.

These references do not imply feature parity or compatibility with their current releases.

## Licensing and proprietary dependencies

The source code in this repository is distributed under the [MIT License](LICENSE). Third-party dependencies remain subject to their own licenses.

Autodesk Civil 3D, its APIs, and its assemblies are proprietary software. Autodesk DLLs, drawings, and other proprietary project files are not distributed with this repository. Users must provide a properly licensed Civil 3D 2025 installation and local reference assemblies when building or running the plugin.

Autodesk and Civil 3D are trademarks of Autodesk, Inc. This independent project is not affiliated with or endorsed by Autodesk.

## Maintainer identity

Local fork maintainer: `nezolder`.
