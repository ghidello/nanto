namespace Nanto.Hosting.Windows.IntegrationTestKit;

public sealed record Phase1DeploymentEvidence
{
    public required string Mode { get; init; }

    public required string RuntimeIdentifier { get; init; }

    public required string TargetFramework { get; init; }

    public required string SdkVersion { get; init; }

    public required string Architecture { get; init; }

    public required Phase1DeploymentFile[] DeploymentFiles { get; init; }

    public required long TotalDeployedBytes { get; init; }

    public required long DeclaredEmbeddedAssetBytes { get; init; }

    public required string PackageRelativePath { get; init; }

    public required long PackageLength { get; init; }

    public required string PackageSha256 { get; init; }

    public required Phase1DeploymentFile[] SymbolFiles { get; init; }

    public required long TotalSymbolBytes { get; init; }

    public required string LoaderForm { get; init; }

    public required bool? LoaderSignatureTrusted { get; init; }

    public required string? LoaderCompanyName { get; init; }

    public required string[] ExecutableImports { get; init; }

    public required Phase1DeploymentSmokeResult[] SmokeScenarios { get; init; }
}
