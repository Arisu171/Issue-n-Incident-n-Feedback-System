using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>Một dòng log đã ghi, kèm các trường có cấu trúc.</summary>
public sealed record LogEntry(
    LogLevel Level,
    string Category,
    string Message,
    IReadOnlyDictionary<string, object?> State,
    Exception? Exception = null);

/// <summary>
/// Bắt log của API để kiểm chứng FR-011 và NFR-AUD-01 (<i>"sự kiện quản trị có correlation,
/// actor, target, action, result"</i>) cũng như NFR-SEC-02 (<i>"password và signing key không
/// xuất hiện trong log"</i>).
///
/// Không có thứ này thì hai NFR trên chỉ được kiểm chứng bằng cách đọc log bằng mắt.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public IEnumerable<LogEntry> AuditEntries =>
        Entries.Where(e => e.Message.StartsWith("Audit ", StringComparison.Ordinal));

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<LogEntry> _sink;

        public CapturingLogger(string category, ConcurrentQueue<LogEntry> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    fields[key] = value;
                }
            }

            _sink.Enqueue(new LogEntry(logLevel, _category, formatter(state, exception), fields, exception));
        }
    }
}
