namespace Nanto.WebView2InteropGen;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var arguments = GeneratorArguments.Parse(args);
            Generator.Generate(
                arguments.PackageRoot,
                arguments.SpecPath,
                arguments.OutputDirectory,
                arguments.ManifestSpecPath,
                arguments.ManifestOutputPath);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed record GeneratorArguments(
        string PackageRoot,
        string SpecPath,
        string OutputDirectory,
        string ManifestSpecPath,
        string ManifestOutputPath)
    {
        public static GeneratorArguments Parse(string[] args)
        {
            if (args.Length is not 6 and not 10)
            {
                throw new ArgumentException(
                    "Expected --package-root <path> --spec <path> --output <path> "
                    + "[--manifest-spec-path <path> --manifest-output-path <path>].");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException($"Invalid or duplicate generator argument '{args[index]}'.");
                }
            }

            string[] allowedArguments =
            [
                "--package-root",
                "--spec",
                "--output",
                "--manifest-spec-path",
                "--manifest-output-path",
            ];
            var unknownArgument = values.Keys.FirstOrDefault(key => !allowedArguments.Contains(key, StringComparer.Ordinal));
            if (unknownArgument is not null)
            {
                throw new ArgumentException($"Unknown generator argument '{unknownArgument}'.");
            }

            if (values.ContainsKey("--manifest-spec-path") != values.ContainsKey("--manifest-output-path"))
            {
                throw new ArgumentException("Manifest spec and output paths must be supplied together.");
            }

            return new GeneratorArguments(
                GetRequired(values, "--package-root"),
                GetRequired(values, "--spec"),
                GetRequired(values, "--output"),
                GetOptional(values, "--manifest-spec-path", "eng/Nanto.WebView2InteropGen/webview2-interop-spec.json"),
                GetOptional(values, "--manifest-output-path", "src/Nanto.Hosting.Windows/Interop/Generated/WebView2Interop.g.cs"));
        }

        private static string GetRequired(Dictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? Path.GetFullPath(value)
                : throw new ArgumentException($"Missing generator argument '{name}'.");

        private static string GetOptional(Dictionary<string, string> values, string name, string defaultValue) =>
            values.TryGetValue(name, out var value)
                ? !string.IsNullOrWhiteSpace(value)
                    ? value.Replace('\\', '/')
                    : throw new ArgumentException($"Generator argument '{name}' is empty.")
                : defaultValue;
    }
}