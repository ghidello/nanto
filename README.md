# Nanto

> The native frame for your web application.

Nanto is an early-stage .NET framework for building small native applications with web frontends. Its Windows-first design combines a raw Win32 host, the system WebView2 runtime, strongly typed generated C# ↔ TypeScript APIs, capability-based security, and Native AOT by default.

## Status

Nanto starts with Phase 1: a clean production implementation of the Windows host and lifecycle kernel. The first Milestone 1 increment now establishes the canonical solution and default project graph, portable contracts and validation, lifecycle state machines, repository-local test support, and fast dependency-boundary tests.

Nanto was renamed from Telaio after the feasibility work; commit [`90725e9`](https://github.com/ghidello/telaio/commit/90725e997fac3140ef4dd9f1a8ebd5d53db67642) records that historical boundary. Nanto has no source, build, report, or undocumented-decision dependency on the former repository. The architecture and Phase 1 plan state every adopted production rule directly.

## Documentation

- [Architecture and roadmap](docs/Nanto-architecture-and-roadmap.md)
- [Phase 1 implementation plan](docs/phase1-plan.md)
- [Contributor and coding-agent guidance](AGENTS.md)

## Requirements

The planned implementation targets Windows 10 22H2/build 19045 or newer on x64 and uses the .NET 10 SDK selected by [`global.json`](global.json).

## Development

The canonical default checks are:

```powershell
dotnet build
dotnet test
```

Test inventory is selected independently from the `Debug` or `Release` build configuration. Once the Phase 1 integration projects are introduced, the canonical commands will be:

```powershell
dotnet test                              # fast tests only
dotnet test -p:TestScope=All             # fast and unattended integration tests
dotnet test -p:TestScope=Integration     # unattended integration tests only
```

Every `*IntegrationTests` assembly receives its integration trait from repository build configuration; individual tests do not need to repeat it. Visible and long-running projects remain separately opted in and are never part of unattended scopes. The integration scopes are not phase gates until those test projects exist.

Once introduced, manual integration tests must be run by naming exactly one project and supplying both opt-ins:

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests/Nanto.Hosting.Windows.VisibleIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
dotnet test tests/Nanto.Hosting.Windows.LongRunningIntegrationTests/Nanto.Hosting.Windows.LongRunningIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

Visible tests interact with the desktop; long-running tests may occupy the machine for substantial time. Passing `RunManualTests=true` through `Nanto.slnx` is rejected so the two categories cannot start together accidentally. Automated agents must not run either project as routine verification. When a manual test is needed to validate relevant behavior, the agent must explain why and obtain explicit user approval before running that specific project.

The raw-Win32 host now has an external, versioned TestProtocol/TestApp foundation that exercises clean hidden startup and checkpoint-injected failure cleanup through the production host. Process containment, automated integration-test projects, WebView2 integration, and deployment modes have not been implemented yet.
