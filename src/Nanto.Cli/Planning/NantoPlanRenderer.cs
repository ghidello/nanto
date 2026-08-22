using System.Text;
using System.Text.Json;

namespace Nanto.Cli.Planning;

internal static class NantoPlanRenderer
{
    internal static string RenderHuman(NantoPlan plan)
    {
        var builder = new StringBuilder();
        builder.Append("Nanto ").Append(plan.Command).AppendLine(" plan (schema 1)");
        builder.Append("Runtime: ").Append(plan.Runtime).Append(" — ").AppendLine(plan.RuntimeReason);
        builder.AppendLine("Paths:");
        foreach (NantoPlanPath path in plan.Paths)
        {
            builder.Append("  ").Append(path.Label).Append(" = ").AppendLine(path.RelativePath);
        }

        builder.AppendLine("Steps:");
        foreach (NantoPlanStep step in plan.Steps)
        {
            builder.Append("  ").Append(step.Id).Append(" [").Append(step.Mutation).Append(']');
            if (step.File is not null)
            {
                builder.Append(": ").Append(step.File);
                foreach (string argument in step.Arguments)
                {
                    builder.Append(' ').Append(Quote(argument));
                }
            }
            else if (step.ReadinessUrl is not null)
            {
                builder.Append(": ").Append(step.ReadinessUrl);
            }

            builder.AppendLine();
        }

        if (plan.ShutdownOrder.Length > 0)
        {
            builder.Append("Shutdown: ").AppendLine(string.Join(" -> ", plan.ShutdownOrder));
        }

        return builder.ToString();
    }

    internal static string RenderJson(NantoPlan plan) => JsonSerializer.Serialize(plan, NantoPlanJsonContext.Default.NantoPlan);

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"' : value;
}
