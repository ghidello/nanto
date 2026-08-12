namespace Nanto.WebView2InteropGen;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var arguments = GeneratorArguments.Parse(args);
            Generator.Generate(arguments.PackageRoot, arguments.SpecPath, arguments.OutputDirectory);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed record GeneratorArguments(string PackageRoot, string SpecPath, string OutputDirectory)
    {
        public static GeneratorArguments Parse(string[] args)
        {
            if (args.Length != 6)
            {
                throw new ArgumentException("Expected --package-root <path> --spec <path> --output <path>.");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException($"Invalid or duplicate generator argument '{args[index]}'.");
                }
            }

            return new GeneratorArguments(
                GetRequired(values, "--package-root"),
                GetRequired(values, "--spec"),
                GetRequired(values, "--output"));
        }

        private static string GetRequired(Dictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? Path.GetFullPath(value)
                : throw new ArgumentException($"Missing generator argument '{name}'.");
    }
}
