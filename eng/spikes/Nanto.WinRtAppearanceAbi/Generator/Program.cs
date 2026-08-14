namespace Nanto.WinRtAppearanceAbiGen;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            GeneratorArguments arguments = GeneratorArguments.Parse(args);
            Generator.Generate(arguments);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

internal sealed record GeneratorArguments(string PackageRoot, string SpecPath, string OutputDirectory, string? VerifyDirectory)
{
    public static GeneratorArguments Parse(string[] args)
    {
        if (args.Length is not 6 and not 8)
        {
            throw new ArgumentException("Expected --package-root <path> --spec <path> --output <path> [--verify <path>].");
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"Invalid or duplicate generator argument '{args[index]}'.");
            }
        }

        return new GeneratorArguments(
            GetRequiredPath(values, "--package-root"),
            GetRequiredPath(values, "--spec"),
            GetRequiredPath(values, "--output"),
            values.TryGetValue("--verify", out string? verifyDirectory) ? Path.GetFullPath(verifyDirectory) : null);
    }

    private static string GetRequiredPath(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"Missing generator argument '{name}'.");
}