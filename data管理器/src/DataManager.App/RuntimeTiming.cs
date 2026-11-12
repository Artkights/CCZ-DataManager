using System.Diagnostics;
namespace DataManager.App;

/// <summary>Optional diagnostic sink. Normal sessions do not allocate clocks or collect timings.</summary>
internal static class RuntimeTiming
{
    internal static Action<string, double>? Sink;
    internal static IDisposable? Measure(string name) => Sink == null ? null : new Measurement(name, Sink);
    private sealed class Measurement(string name, Action<string, double> sink) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => sink(name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
    }
}
