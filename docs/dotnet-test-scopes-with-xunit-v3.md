# Fast, integration, and manual test scopes with xUnit v3

Large .NET solutions often reach a point where `dotnet test` is no longer a fast feedback command. Unit tests may take seconds, while integration, deployment, UI, or soak tests can take minutes and may require special machine conditions.

This guide shows how to keep the default test command fast without hiding the slower tests or maintaining separate solution files. It uses:

- xUnit v3;
- Microsoft.Testing.Platform (MTP);
- MSBuild project-name conventions;
- assembly-level traits generated at build time; and
- an explicit `TestScope` property.

The resulting commands are:

```powershell
dotnet test                          # Fast tests only
dotnet test -p:TestScope=All         # Fast and unattended integration tests
dotnet test -p:TestScope=Integration # Unattended integration tests only
```

An optional second guard keeps visible UI and long-running tests disabled unless one specific project is explicitly selected:

```powershell
dotnet test tests/MyApp.VisibleIntegrationTests/MyApp.VisibleIntegrationTests.csproj `
  -p:TestScope=All `
  -p:RunManualTests=true
```

## Why filter by test project?

Fast and slow tests can be separated using a trait on every test method. That works, but it is easy to forget a trait and it mixes tests with very different dependencies and execution requirements in the same assembly.

This approach instead uses project names as the classification:

| Project suffix | Classification | Runs by default |
| --- | --- | --- |
| `.Tests` | Fast | Yes |
| `.IntegrationTests` | Integration | No |
| `.VisibleIntegrationTests` | Integration and manual | No |
| `.LongRunningIntegrationTests` | Integration and manual | No |

MSBuild converts those conventions into assembly-level xUnit traits. Every test in an integration-test assembly therefore has `integration=true`, without annotations in source code.

The important design rule is that `TestScope` controls only test inventory. It should not silently change the build configuration, deployment mode, runtime, compiler settings, or application behavior.

## Prerequisites

This example uses an SDK that supports selecting Microsoft.Testing.Platform in `global.json`. Pin an SDK version appropriate for the target repository:

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

Each test project needs the MTP-enabled xUnit v3 package. With Central Package Management, put the version in the repository-level `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <PackageVersion Include="xunit.v3.mtp-v2" Version="3.2.2" />
  </ItemGroup>
</Project>
```

If the repository does not use Central Package Management, specify the version on the `PackageReference` instead. Check for a newer compatible package version when copying this guide into a new project.

## The reusable configuration

Place the following file at `tests/Directory.Build.props`. It applies automatically to projects below the `tests` directory.

```xml
<Project>
  <!--
    A nested Directory.Build.props stops MSBuild's automatic upward search.
    Import the repository-level defaults explicitly if the repository has them.
  -->
  <Import Project="../Directory.Build.props" />

  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <TestScope Condition="'$(TestScope)' == ''">Fast</TestScope>
  </PropertyGroup>

  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Tests'))">
    <OutputType>Exe</OutputType>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <!-- Classify the complete assembly from its project name. -->
  <ItemGroup Condition="$(MSBuildProjectName.EndsWith('IntegrationTests'))">
    <AssemblyAttribute Include="Xunit.TraitAttribute">
      <_Parameter1>integration</_Parameter1>
      <_Parameter2>true</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>

  <!-- Default: make integration assemblies select no tests. -->
  <PropertyGroup Condition="'$(TestScope)' == 'Fast' and $(MSBuildProjectName.EndsWith('IntegrationTests'))">
    <TestAssemblyExcluded>true</TestAssemblyExcluded>
    <TestingPlatformCommandLineArguments>$(TestingPlatformCommandLineArguments) --filter-not-trait "integration=true"</TestingPlatformCommandLineArguments>
  </PropertyGroup>

  <!-- Integration-only: make ordinary fast-test assemblies select no tests. -->
  <PropertyGroup Condition="'$(TestScope)' == 'Integration' and $(MSBuildProjectName.EndsWith('Tests')) and !$(MSBuildProjectName.EndsWith('IntegrationTests'))">
    <TestAssemblyExcluded>true</TestAssemblyExcluded>
    <TestingPlatformCommandLineArguments>$(TestingPlatformCommandLineArguments) --filter-trait "integration=true"</TestingPlatformCommandLineArguments>
  </PropertyGroup>

  <!--
    MTP returns exit code 8 when a filter selects no tests. That is intentional
    for an excluded assembly, so ignore that code only when this file excluded it.
  -->
  <PropertyGroup Condition="'$(TestAssemblyExcluded)' == 'true'">
    <TestingPlatformCommandLineArguments>$(TestingPlatformCommandLineArguments) --ignore-exit-code 8</TestingPlatformCommandLineArguments>
  </PropertyGroup>

  <ItemGroup Condition="$(MSBuildProjectName.EndsWith('Tests'))">
    <PackageReference Include="xunit.v3.mtp-v2" />
    <Using Include="Xunit" />
  </ItemGroup>

  <Target Name="ValidateTestScope"
          BeforeTargets="PrepareForBuild"
          Condition="'$(IsTestProject)' == 'true' and '$(TestScope)' != 'Fast' and '$(TestScope)' != 'All' and '$(TestScope)' != 'Integration'">
    <Error Text="Unknown TestScope '$(TestScope)'. Expected Fast, All, or Integration." />
  </Target>
</Project>
```

If there is no repository-level `Directory.Build.props`, remove the explicit `Import`. If the test defaults already live in the repository-level file, the entire configuration can be placed there instead.

Test project files can now remain small:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/MyApp/MyApp.csproj" />
  </ItemGroup>
</Project>
```

## How the filtering works

For `MyApp.IntegrationTests`, MSBuild emits an assembly attribute equivalent to:

```csharp
[assembly: Xunit.Trait("integration", "true")]
```

The scope then adds an MTP command-line filter:

- `Fast` adds `--filter-not-trait "integration=true"` to integration assemblies;
- `Integration` adds `--filter-trait "integration=true"` to ordinary test assemblies;
- `All` adds neither filter.

The solution still builds every project. Excluded test executables start, apply their filters, and discover zero selected tests. MTP normally returns exit code 8 for "no tests," so the configuration appends `--ignore-exit-code 8` only to assemblies that it deliberately excluded.

Do not add `--ignore-exit-code 8` globally. A genuinely empty or misconfigured test project should remain visible rather than being silently accepted.

## Optional guard for manual tests

Some tests should require more than `TestScope=All`. Examples include tests that steal foreground focus, resize windows, need attached hardware, incur external cost, or occupy the machine for a long time.

Add the following blocks to `tests/Directory.Build.props`. Place the manual exclusion `PropertyGroup` before the shared `TestAssemblyExcluded`/`--ignore-exit-code 8` block from the main configuration; MSBuild evaluates these properties in file order.

```xml
<!-- Give manual assemblies both integration=true and manual=true. -->
<ItemGroup Condition="$(MSBuildProjectName.EndsWith('VisibleIntegrationTests')) or $(MSBuildProjectName.EndsWith('LongRunningIntegrationTests'))">
  <AssemblyAttribute Include="Xunit.TraitAttribute">
    <_Parameter1>manual</_Parameter1>
    <_Parameter2>true</_Parameter2>
  </AssemblyAttribute>
</ItemGroup>

<!-- Exclude manual assemblies unless the caller explicitly opts in. -->
<PropertyGroup Condition="'$(RunManualTests)' != 'true' and ($(MSBuildProjectName.EndsWith('VisibleIntegrationTests')) or $(MSBuildProjectName.EndsWith('LongRunningIntegrationTests')))">
  <TestAssemblyExcluded>true</TestAssemblyExcluded>
  <TestingPlatformCommandLineArguments>$(TestingPlatformCommandLineArguments) --filter-not-trait "manual=true"</TestingPlatformCommandLineArguments>
</PropertyGroup>

<!-- Reject enabling all manual projects through a solution-level command. -->
<Target Name="ValidateManualTestInvocation"
        BeforeTargets="PrepareForBuild;VSTest;Test"
        Condition="'$(RunManualTests)' == 'true' and '$(BuildingSolutionFile)' == 'true' and ($(MSBuildProjectName.EndsWith('VisibleIntegrationTests')) or $(MSBuildProjectName.EndsWith('LongRunningIntegrationTests')))">
  <Error Text="Manual tests must be run by naming one manual integration-test project directly; they cannot be enabled through the solution." />
</Target>
```

The intentional opt-in is therefore both scope-specific and project-specific:

```powershell
dotnet test tests/MyApp.LongRunningIntegrationTests/MyApp.LongRunningIntegrationTests.csproj `
  -p:TestScope=All `
  -p:RunManualTests=true
```

Notice that all exclusion blocks set the same `TestAssemblyExcluded` property. Keeping the shared `--ignore-exit-code 8` block after all filters appends the exception once, regardless of which exclusion applied.

## When fast and slow tests share an assembly

If project separation is not practical, put traits directly on slow tests:

```csharp
[Trait("integration", "true")]
public sealed class DatabaseIntegrationTests
{
    [Fact]
    public async Task Persists_an_order()
    {
        // ...
    }
}
```

Then apply the filter to that test project for the relevant scope. This is more granular, but classification can be forgotten on newly added tests. Assembly-level classification is preferable when integration tests already have their own dependencies, fixtures, environment, or lifecycle.

## CI examples

A pull-request job can keep feedback fast:

```powershell
dotnet test -c Release
```

A separate job can run all unattended coverage:

```powershell
dotnet test -c Release -p:TestScope=All
```

Or split fast and integration jobs explicitly:

```powershell
dotnet test -c Release -p:TestScope=Fast
dotnet test -c Release -p:TestScope=Integration
```

Manual tests should normally be scheduled or launched deliberately, with their prerequisites and machine impact documented alongside the command.

## Common mistakes

### Forgetting the parent import

MSBuild normally finds the nearest `Directory.Build.props` and stops. Adding `tests/Directory.Build.props` therefore hides the repository-level file unless the nested file imports it explicitly.

### Treating "no tests" as an unconditional success

Ignoring MTP exit code 8 everywhere can conceal broken discovery. Scope it to assemblies intentionally excluded by the filter.

### Mixing scope with runtime behavior

Do not make `TestScope=All` switch the application to a different runtime, deployment model, database, compiler mode, or build configuration. Use separate, explicitly named properties or dedicated projects for those dimensions.

### Assuming `All` should include disruptive tests

"All unattended tests" is a safer meaning for solution-level automation. Visible, destructive, costly, hardware-dependent, and long-running tests deserve a separate opt-in.

### Relying on names without validation

The convention works only if integration projects consistently end in `IntegrationTests`. Enforce the convention in review or add a repository-specific validation target if project naming is likely to drift.

## Verification checklist

Before adopting the setup, verify all of these cases:

1. `dotnet test` executes fast tests and reports success while skipping integration tests.
2. `dotnet test -p:TestScope=All` executes fast and unattended integration tests.
3. `dotnet test -p:TestScope=Integration` executes only integration tests.
4. An invalid value such as `-p:TestScope=Slow` fails during the build.
5. A genuinely misconfigured test assembly is not hidden by a global exit-code exception.
6. If manual guards are enabled, a solution-level `RunManualTests=true` invocation fails.
7. A directly named manual project runs only when both `TestScope=All` and `RunManualTests=true` are supplied.

This gives developers a fast default, keeps slower coverage easy to discover, and makes exceptional test costs explicit at the command line.
