namespace ConstantProxy.Core.Connection;

/// <summary>Thread-safe bounded buffer of the most recent ssh output lines, used for diagnostics and classification.</summary>
public sealed class OutputTail
{
    private readonly object gate = new();
    private readonly Queue<SshOutputLine> lines = new();
    private readonly int capacity;

    public OutputTail(int capacity = 40)
    {
        this.capacity = capacity;
    }

    public void Add(SshOutputLine line)
    {
        lock (gate)
        {
            lines.Enqueue(line);
            while (lines.Count > capacity)
            {
                lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<SshOutputLine> Snapshot()
    {
        lock (gate)
        {
            return lines.ToArray();
        }
    }

    public string AsText()
    {
        lock (gate)
        {
            return string.Join(Environment.NewLine, lines.Select(l => l.Text));
        }
    }
}
