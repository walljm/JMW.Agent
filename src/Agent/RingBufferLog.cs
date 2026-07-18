using System.Text;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent;

/// <summary>
/// A bounded, in-memory ring of the agent's most recent formatted log lines. This is the
/// fallback capture source for the on-demand log viewer on every deployment target that isn't a
/// native systemd install (Docker, dev runs, macOS, Windows) — exactly the content that would
/// appear in <c>docker logs</c>, captured from inside the process instead of needing
/// docker.sock access (docs/plans/agent-log-viewer.md §4.1). Bounded by BOTH a line count and a
/// total byte budget, whichever hits first. Every appended line gets a monotonically increasing
/// <c>Seq</c>, which doubles as the opaque paging token for "load older".
/// </summary>
internal sealed class LogRingBuffer
{
    private readonly int _maxLines;
    private readonly long _maxBytes;
    private readonly LinkedList<Entry> _entries = new();
    private readonly object _lock = new();
    private long _totalBytes;
    private long _nextSeq;

    public LogRingBuffer(int maxLines = 2000, long maxBytes = 256 * 1024)
    {
        _maxLines = maxLines;
        _maxBytes = maxBytes;
    }

    public void Append(string line)
    {
        int bytes = Encoding.UTF8.GetByteCount(line);
        lock (_lock)
        {
            _entries.AddLast(new Entry(_nextSeq++, line, bytes));
            _totalBytes += bytes;

            // Evict oldest until back within BOTH bounds. Keep at least one line so a single
            // pathological over-budget line doesn't leave the buffer empty.
            while (_entries.Count > 1 && (_entries.Count > _maxLines || _totalBytes > _maxBytes))
            {
                Entry oldest = _entries.First!.Value;
                _entries.RemoveFirst();
                _totalBytes -= oldest.Bytes;
            }
        }
    }

    /// <summary>
    /// Returns up to <paramref name="max"/> lines, newest-first, whose Seq is strictly less than
    /// <paramref name="before"/> (or the newest lines when <paramref name="before"/> is null).
    /// The caller over-fetches by one to decide whether older lines remain.
    /// </summary>
    public IReadOnlyList<(long Seq, string Line)> Snapshot(long? before, int max)
    {
        List<(long Seq, string Line)> result = new(Math.Min(max, 64));
        lock (_lock)
        {
            for (LinkedListNode<Entry>? node = _entries.Last; node is not null; node = node.Previous)
            {
                if (before is { } b && node.Value.Seq >= b)
                {
                    continue;
                }

                result.Add((node.Value.Seq, node.Value.Line));
                if (result.Count >= max)
                {
                    break;
                }
            }
        }

        return result;
    }

    private readonly record struct Entry(long Seq, string Line, int Bytes);
}

/// <summary>
/// <see cref="ILoggerProvider"/> that appends every formatted log line into a shared
/// <see cref="LogRingBuffer"/>. Registered alongside the console sink in Program.cs; the
/// framework's configured minimum level gates what reaches it. Negligible overhead — a lock and
/// a string append per line.
/// </summary>
internal sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly LogRingBuffer _buffer;

    public RingBufferLoggerProvider(LogRingBuffer buffer) => _buffer = buffer;

    public ILogger CreateLogger(string categoryName) => new RingLogger(categoryName, _buffer);

    public void Dispose()
    {
    }

    private sealed class RingLogger : ILogger
    {
        private readonly string _category;
        private readonly LogRingBuffer _buffer;

        public RingLogger(string category, LogRingBuffer buffer)
        {
            _category = category;
            _buffer = buffer;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            StringBuilder sb = new();
            sb.Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            sb.Append(' ');
            sb.Append(LevelLabel(logLevel));
            sb.Append(' ');
            sb.Append(_category);
            sb.Append(": ");
            sb.Append(message);
            if (exception is not null)
            {
                sb.Append(" | ");
                sb.Append(exception.GetType().Name);
                sb.Append(": ");
                sb.Append(exception.Message);
            }

            _buffer.Append(sb.ToString());
        }

        private static string LevelLabel(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => "NONE",
        };
    }
}