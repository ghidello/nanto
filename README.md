# Nanto

> The native frame for your web application.

Nanto is an early-stage .NET framework for building small native applications with web frontends. Its Windows-first design combines a raw Win32 host, the system WebView2 runtime, strongly typed generated C# ↔ TypeScript APIs, capability-based security, and Native AOT by default.

## Status

Nanto starts with Phase 1: a clean production implementation of the Windows host and lifecycle kernel. This repository currently contains only the documentation and shared configuration baseline for review; production projects and tests have not been created yet.

Phase 0 feasibility work remains in the [Telaio repository](https://github.com/ghidello/telaio/tree/phase_1) at source commit [`0344107`](https://github.com/ghidello/telaio/commit/0344107c06ff36d2f89189bbb7660a46180193b1). That work proved the raw-Win32/WebView2, Native AOT, secure asset, messaging, recovery, and deterministic teardown approach. It is historical evidence, not a production dependency of Nanto.

## Documentation

- [Architecture and roadmap](docs/Nanto-architecture-and-roadmap.md)
- [Phase 1 implementation plan](docs/phase1-plan.md)
- [Contributor and coding-agent guidance](AGENTS.md)

## Requirements

The planned implementation targets Windows 10 22H2/build 19045 or newer on x64 and uses the .NET 10 SDK selected by [`global.json`](global.json).

## Development

There are intentionally no projects, solutions, tests, or build commands in this review baseline. Implementation begins only after the documentation and configuration have been approved.
