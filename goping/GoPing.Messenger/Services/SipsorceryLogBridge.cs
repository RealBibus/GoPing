using Microsoft.Extensions.Logging;
using System;
using GoPing.Services;

namespace GoPing.Services;

internal static class SipsorceryLogBridge
{
    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        SIPSorcery.LogFactory.Set(new CallLoggerFactory());
    }

    private sealed class CallLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new CallLoggerLogger(categoryName);
        public void Dispose() { }
    }

    private sealed class CallLoggerLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = $"[SIPSorcery/{category}] {formatter(state, exception)}";
            if (logLevel >= LogLevel.Error) CallLogger.Error(msg, exception);
            else CallLogger.Info(msg);
        }
    }
}
