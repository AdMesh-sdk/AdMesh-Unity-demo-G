using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdMesh.Core
{
    public static class AdMeshLogger
    {
        public enum LogLevel
        {
            Debug,
            Info,
            Warning,
            Error
        }

        public sealed class LogEntry
        {
            public LogLevel Level { get; set; }
            public string Message { get; set; }
            public string Context { get; set; }
            public DateTime Timestamp { get; set; }
            public Exception Exception { get; set; }
        }

        private static readonly Queue<LogEntry> LogHistory = new Queue<LogEntry>(100);
        private static LogLevel _minimumLevel = LogLevel.Info;
        private const int MaxLogHistory = 100;

        public static event Action<LogLevel, string, Exception> OnLog;

        public static void SetLogLevel(LogLevel level) => _minimumLevel = level;
        public static void Debug(string message, string context = null) => Log(LogLevel.Debug, message, context);
        public static void Info(string message, string context = null) => Log(LogLevel.Info, message, context);
        public static void Warning(string message, string context = null, Exception exception = null) => Log(LogLevel.Warning, message, context, exception);
        public static void Error(string message, string context = null, Exception exception = null) => Log(LogLevel.Error, message, context, exception);

        public static LogEntry[] GetRecentLogs() => LogHistory.ToArray();

        private static void Log(LogLevel level, string message, string context = null, Exception exception = null)
        {
            if (level < _minimumLevel)
            {
                return;
            }

            var entry = new LogEntry
            {
                Level = level,
                Message = message,
                Context = context,
                Timestamp = DateTime.UtcNow,
                Exception = exception
            };

            LogHistory.Enqueue(entry);
            if (LogHistory.Count > MaxLogHistory)
            {
                LogHistory.Dequeue();
            }

            var formatted = $"[AdMesh] [{entry.Timestamp:HH:mm:ss.fff}] {entry.Message}";
            if (!string.IsNullOrWhiteSpace(entry.Context))
            {
                formatted += $" ({entry.Context})";
            }

            switch (level)
            {
                case LogLevel.Warning:
                    UnityEngine.Debug.LogWarning(formatted);
                    break;
                case LogLevel.Error:
                    UnityEngine.Debug.LogError(formatted);
                    break;
                default:
                    UnityEngine.Debug.Log(formatted);
                    break;
            }

            if (exception != null)
            {
                UnityEngine.Debug.LogException(exception);
            }

            OnLog?.Invoke(level, formatted, exception);
        }
    }
}
