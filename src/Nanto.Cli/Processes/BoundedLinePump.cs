using System.Text;

namespace Nanto.Cli.Processes;

internal sealed class BoundedLinePump
{
    private const int BufferSize = 1024;
    private const int MaximumLineLength = 4096;
    private const int RetainedLineCount = 32;

    private readonly Queue<string> _retained = new(RetainedLineCount);
    private readonly string _resource;
    private readonly TextWriter _output;

    internal BoundedLinePump(string resource, TextWriter output)
    {
        _resource = resource;
        _output = output;
    }

    internal async Task RunAsync(TextReader reader)
    {
        char[] buffer = new char[BufferSize];
        var line = new StringBuilder();
        bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            for (int index = 0; index < read; index++)
            {
                char character = buffer[index];
                if (character == '\n')
                {
                    await EmitAsync(line, truncated);
                    line.Clear();
                    truncated = false;
                    continue;
                }

                if (character == '\r')
                {
                    continue;
                }

                if (line.Length < MaximumLineLength)
                {
                    line.Append(character);
                }
                else
                {
                    truncated = true;
                }
            }
        }

        if (line.Length > 0 || truncated)
        {
            await EmitAsync(line, truncated);
        }
    }

    internal string[] Snapshot()
    {
        lock (_retained)
        {
            return [.. _retained];
        }
    }

    private async Task EmitAsync(StringBuilder line, bool truncated)
    {
        string value = truncated ? line + "…[truncated]" : line.ToString();
        lock (_retained)
        {
            if (_retained.Count == RetainedLineCount)
            {
                _retained.Dequeue();
            }

            _retained.Enqueue(value);
        }

        await _output.WriteLineAsync($"[{_resource}] {value}");
    }
}
