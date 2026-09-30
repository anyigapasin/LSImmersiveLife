using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LSImmersiveLife
{
    /// <summary>
    /// Owns the two persistent logs used by every LS Immersive Life system.
    /// Runtime records confirmed gameplay and framework actions. Debug records
    /// errors, invalid state, exceptions, and other troubleshooting evidence.
    /// </summary>
    internal sealed class LSImmersiveLog
    {
        private const int MaximumRecordLength = 16384;
        private const int MaximumThrottleCategories = 128;

        private readonly object _sync = new object();
        private bool _backgroundWrites;
        private readonly string _runtimePath;
        private readonly string _debugPath;
        private readonly string _sessionId;
        private readonly DateTime _sessionStartedUtc;
        private readonly Dictionary<string, ThrottleState> _throttleStates =
            new Dictionary<string, ThrottleState>(StringComparer.Ordinal);

        private bool _sessionStarted;
        private bool _sessionEnded;
        private bool _runtimeEnabled = true;
        private bool _debugEnabled = true;
        private bool _verboseDiagnostics;
        private int _runtimeEntryCount;
        private int _debugEntryCount;
        private int _failureEntryCount;
        private int _suppressedEntryCount;
        private readonly Queue<LogRecord> _queuedRecords = new Queue<LogRecord>();
        private AutoResetEvent _logSignal;
        private Thread _logWorker;
        private bool _logWorkerStopping;

        /// <summary>
        /// The fixed Runtime log path. This remains LSRuntime.log for every
        /// play session so users have one predictable place to inspect it.
        /// </summary>
        internal string RuntimePath
        {
            get { return _runtimePath; }
        }

        /// <summary>
        /// The fixed diagnostic log path. This remains LSDebug.log for every
        /// play session so failures stay separate from normal gameplay flow.
        /// </summary>
        internal string DebugPath
        {
            get { return _debugPath; }
        }

        /// <summary>
        /// Stable identifier written on every record created by this script
        /// instance. It makes appended log files readable one play session at
        /// a time without creating additional log files.
        /// </summary>
        internal string SessionId
        {
            get { return _sessionId; }
        }

        internal LSImmersiveLog(string lsImmersiveDirectory, bool backgroundWrites = false)
        {
            _backgroundWrites = backgroundWrites;
            // The caller supplies the already-derived
            // <GTA V Enhanced>\\scripts\\LSImmersiveLife directory. The logger
            // adds exactly one Log child and never derives a relative path or
            // creates a second user-storage location.
            string logDirectory = Path.Combine(
                lsImmersiveDirectory,
                "Log");

            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch
            {
                // The individual write methods remain guarded as well. A bad
                // log path must never stop ScriptHook from loading the script.
            }

            _runtimePath = Path.Combine(
                logDirectory,
                "LSRuntime.log");

            _debugPath = Path.Combine(
                logDirectory,
                "LSDebug.log");

            _sessionStartedUtc = DateTime.UtcNow;
            _sessionId = _sessionStartedUtc.ToString(
                "yyyyMMdd-HHmmssfff",
                CultureInfo.InvariantCulture)
                + "-"
                + Guid.NewGuid().ToString("N").Substring(0, 8);

            // A constructor is the one reliable point reached on every
            // script load, including early startup failures. The Main UI may
            // supply a richer context through BeginSession later; it is safe
            // and does not create a second session marker.
            StartLogWorkerIfNeeded();
            BeginSession("Logger constructed.");
        }

        /// <summary>
        /// Adds the start marker for this play session. The call is idempotent
        /// because construction already begins the session; later callers add
        /// useful startup context without producing a second session.
        /// </summary>
        internal void BeginSession(string context)
        {
            lock (_sync)
            {
                if (_sessionEnded)
                    return;

                if (!_sessionStarted)
                {
                    _sessionStarted = true;
                    WriteRuntimeLocked(
                        "SESSION_STARTED",
                        "SessionId=" + _sessionId
                        + "; StartedLocal=" + DateTime.Now.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff",
                            CultureInfo.InvariantCulture)
                        + "; Context=" + NormalizeMessage(context));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(context))
                {
                    WriteRuntimeLocked(
                        "SESSION_CONTEXT",
                        "SessionId=" + _sessionId
                        + "; " + NormalizeMessage(context));
                }
            }
        }

        /// <summary>
        /// Applies the user-facing logging controls loaded from the single
        /// LSImmersiveMainUI.xml configuration. The logger has safe defaults
        /// until this method is called, which keeps early configuration-load
        /// failures diagnosable. Verbose diagnostics are opt-in and available
        /// through Verbose; Debug, Anomaly, StateFailure, and Exception remain
        /// reserved for actual troubleshooting evidence.
        /// </summary>
        internal void Configure(
            bool runtimeEnabled,
            bool debugEnabled,
            bool verboseDiagnostics)
        {
            lock (_sync)
            {
                _runtimeEnabled = runtimeEnabled;
                _debugEnabled = debugEnabled;
                _verboseDiagnostics = verboseDiagnostics;

                WriteRuntimeLocked(
                    "LOGGING_CONFIGURATION",
                    "RuntimeEnabled=" + runtimeEnabled.ToString()
                    + "; DebugEnabled=" + debugEnabled.ToString()
                    + "; VerboseDiagnostics=" + verboseDiagnostics.ToString());
            }
        }

        /// <summary>
        /// Adds the end marker and a compact session summary. It is safe to
        /// call from Script.Aborted even when another shutdown path has already
        /// recorded the end of the session.
        /// </summary>
        internal void EndSession(string context)
        {
            Thread worker = null;
            lock (_sync)
            {
                if (_sessionEnded)
                    return;

                if (!_sessionStarted)
                {
                    _sessionStarted = true;
                    WriteRuntimeLocked(
                        "SESSION_STARTED",
                        "SessionId=" + _sessionId
                        + "; Started during shutdown; Context="
                        + NormalizeMessage(context));
                }

                FlushThrottleSummariesLocked();

                TimeSpan duration = DateTime.UtcNow - _sessionStartedUtc;
                WriteRuntimeLocked(
                    "SESSION_SUMMARY",
                    "SessionId=" + _sessionId
                    + "; DurationSeconds="
                    + Math.Max(0, (int)duration.TotalSeconds).ToString(
                        CultureInfo.InvariantCulture)
                    + "; RuntimeEntries=" + _runtimeEntryCount.ToString(
                        CultureInfo.InvariantCulture)
                    + "; DebugEntries=" + _debugEntryCount.ToString(
                        CultureInfo.InvariantCulture)
                    + "; FailureEntries=" + _failureEntryCount.ToString(
                        CultureInfo.InvariantCulture)
                    + "; SuppressedRepeatedEntries=" + _suppressedEntryCount.ToString(
                        CultureInfo.InvariantCulture));

                WriteRuntimeLocked(
                    "SESSION_ENDED",
                    "SessionId=" + _sessionId
                    + "; Context=" + NormalizeMessage(context));

                _sessionEnded = true;
                if (_backgroundWrites && _logWorker != null)
                {
                    _logWorkerStopping = true;
                    worker = _logWorker;
                }
            }

            if (worker != null)
            {
                try { _logSignal.Set(); } catch { }
                if (!ReferenceEquals(Thread.CurrentThread, worker))
                {
                    try { worker.Join(2000); } catch { }
                }
            }
        }

        /// <summary>
        /// Records an actual gameplay or framework action in LSRuntime.log.
        /// Categories that plainly describe failure are mirrored into LSDebug.log
        /// so Runtime still shows the attempted action while Debug records the
        /// troubleshooting evidence the player needs after a bad outcome.
        /// </summary>
        internal void Runtime(string category, string message)
        {
            lock (_sync)
            {
                EnsureSessionStartedLocked();
                WriteRuntimeLocked(category, message);
            }
        }

        /// <summary>
        /// Records a repeated action without performing synchronous disk I/O
        /// for every tick. The first action is retained, later entries within
        /// the requested interval are counted, and the next retained action or
        /// session end writes a clear repeat summary. Use this only for scan or
        /// guard loops; one-off gameplay actions should use Runtime instead.
        /// </summary>
        internal void RuntimeThrottled(
            string category,
            string message,
            TimeSpan minimumInterval)
        {
            if (minimumInterval <= TimeSpan.Zero)
            {
                Runtime(category, message);
                return;
            }

            lock (_sync)
            {
                EnsureSessionStartedLocked();

                string normalizedCategory = NormalizeCategory(category);
                DateTime now = DateTime.UtcNow;
                ThrottleState state;
                if (!_throttleStates.TryGetValue(normalizedCategory, out state))
                {
                    if (_throttleStates.Count >= MaximumThrottleCategories)
                        _throttleStates.Clear();

                    state = new ThrottleState();
                    _throttleStates[normalizedCategory] = state;
                }

                if (state.HasWritten
                    && now - state.LastWrittenUtc < minimumInterval)
                {
                    state.SuppressedCount++;
                    _suppressedEntryCount++;
                    return;
                }

                WriteThrottleSummaryLocked(normalizedCategory, state);
                WriteRuntimeLocked(normalizedCategory, message);
                state.LastWrittenUtc = now;
                state.HasWritten = true;
            }
        }

        /// <summary>
        /// Records configuration fallbacks, invalid state, and other non-
        /// exception troubleshooting evidence in LSDebug.log only.
        /// </summary>
        internal void Debug(string category, string message)
        {
            lock (_sync)
            {
                EnsureSessionStartedLocked();
                WriteDebugLocked("DEBUG_" + NormalizeCategory(category), message);
            }
        }

        /// <summary>
        /// Records optional developer-level diagnostic detail only when the
        /// player has enabled VerboseDiagnostics in the main configuration.
        /// This keeps Debug focused on errors and misbehavior during ordinary
        /// gameplay testing.
        /// </summary>
        internal void Verbose(string category, string message)
        {
            lock (_sync)
            {
                if (!_verboseDiagnostics)
                    return;

                EnsureSessionStartedLocked();
                WriteDebugLocked("VERBOSE_" + NormalizeCategory(category), message);
            }
        }

        /// <summary>
        /// Records a known anomalous condition in LSDebug.log. It is useful
        /// when no managed exception exists but the gameplay result is wrong.
        /// </summary>
        internal void Anomaly(string category, string message)
        {
            lock (_sync)
            {
                EnsureSessionStartedLocked();
                _failureEntryCount++;
                WriteDebugLocked("ANOMALY_" + NormalizeCategory(category), message);
            }
        }

        /// <summary>
        /// Records a gameplay state failure in both logs. Runtime preserves the
        /// player-visible action history, while Debug preserves the diagnostic
        /// classification even when there was no managed exception.
        /// </summary>
        internal void StateFailure(string category, string message)
        {
            lock (_sync)
            {
                EnsureSessionStartedLocked();
                string normalizedCategory = NormalizeCategory(category);
                WriteRuntimeLocked(normalizedCategory, message);

                // Runtime already mirrors clearly named failure categories.
                // This additional entry is only needed for neutral category
                // names supplied by a caller that explicitly identified a
                // bad state.
                if (!LooksLikeFailure(normalizedCategory))
                {
                    _failureEntryCount++;
                    WriteDebugLocked(
                        "STATE_FAILURE_" + normalizedCategory,
                        message);
                }
            }
        }

        /// <summary>
        /// Records full exception evidence in LSDebug.log. Exceptions never
        /// pollute the ordinary gameplay Runtime log.
        /// </summary>
        internal void Exception(string category, Exception error)
        {
            lock (_sync)
            {
                EnsureSessionStartedLocked();
                _failureEntryCount++;
                WriteDebugLocked(
                    "EXCEPTION_" + NormalizeCategory(category),
                    error == null
                        ? "Unknown exception."
                        : error.GetType().FullName + ": " + error);
            }
        }

        private void EnsureSessionStartedLocked()
        {
            if (_sessionStarted || _sessionEnded)
                return;

            _sessionStarted = true;
            WriteRuntimeLocked(
                "SESSION_STARTED",
                "SessionId=" + _sessionId
                + "; Started automatically before first log entry.");
        }

        private void WriteRuntimeLocked(string category, string message)
        {
            string normalizedCategory = NormalizeCategory(category);
            string normalizedMessage = NormalizeMessage(message);
            if (_runtimeEnabled)
            {
                WriteRecordLocked(
                    _runtimePath,
                    "RUNTIME",
                    normalizedCategory,
                    normalizedMessage);
                _runtimeEntryCount++;
            }

            // Existing gameplay owners often describe a failed state with a
            // Runtime event so the normal history remains complete. Mirror
            // that evidence to Debug automatically rather than requiring each
            // owner to remember a second, easy-to-miss write call.
            if (LooksLikeFailure(normalizedCategory))
            {
                _failureEntryCount++;
                WriteDebugLocked(
                    "STATE_FAILURE_" + normalizedCategory,
                    "Runtime action reported a failed or invalid state. "
                    + normalizedMessage);
            }
        }

        private void WriteDebugLocked(string category, string message)
        {
            if (!_debugEnabled)
                return;

            WriteRecordLocked(
                _debugPath,
                "DEBUG",
                NormalizeCategory(category),
                NormalizeMessage(message));
            _debugEntryCount++;
        }

        private void WriteThrottleSummaryLocked(
            string category,
            ThrottleState state)
        {
            if (state == null || state.SuppressedCount <= 0)
                return;

            WriteRuntimeLocked(
                "LOG_REPEAT_SUMMARY",
                "Category=" + category
                + "; SuppressedRepeatedEntries="
                + state.SuppressedCount.ToString(CultureInfo.InvariantCulture));
            state.SuppressedCount = 0;
        }

        private void FlushThrottleSummariesLocked()
        {
            foreach (KeyValuePair<string, ThrottleState> pair in _throttleStates)
                WriteThrottleSummaryLocked(pair.Key, pair.Value);
        }

        private void WriteRecordLocked(
            string path,
            string level,
            string category,
            string message)
        {
            // The live GTA instance queues records for a small background batch
            // writer. Formatting and enqueueing are cheap; opening a file,
            // allocating a writer, and flushing it are not frame-safe work.
            // Isolated regression fixtures retain the original synchronous
            // writer by using the default constructor option.
            try
            {
                string record = DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture)
                    + " | Session=" + _sessionId
                    + " | " + level
                    + " | " + category
                    + " | " + message;

                // Developer Mode receives the already-authored Runtime/Debug
                // event in memory at this single boundary. It never scans the
                // world and it never writes a second line for every tick.
                LSDeveloperRuntime.ObserveLog(
                    _sessionId,
                    level,
                    category,
                    message,
                    DateTime.UtcNow);

                if (_backgroundWrites && !_sessionEnded && !_logWorkerStopping
                    && _logSignal != null)
                {
                    if (_queuedRecords.Count < 8192)
                    {
                        _queuedRecords.Enqueue(new LogRecord(path, record));
                        _logSignal.Set();
                        return;
                    }
                }

                // A full queue is an abnormal diagnostic flood. Preserve the
                // line rather than silently dropping evidence; this path is
                // bounded and is not used during normal gameplay.
                AppendRecord(path, record);
            }
            catch
            {
                // Diagnostics must never stop the GTA script. There is no
                // safe fallback channel that should be allowed to throw here.
            }
        }

        private void StartLogWorkerIfNeeded()
        {
            if (!_backgroundWrites)
                return;

            try
            {
                _logSignal = new AutoResetEvent(false);
                _logWorker = new Thread(LogWorkerLoop)
                {
                    IsBackground = true,
                    Name = "LSImmersiveLife.LogWriter"
                };
                _logWorker.Start();
            }
            catch
            {
                _backgroundWrites = false;
                try { if (_logSignal != null) _logSignal.Dispose(); } catch { }
                _logSignal = null;
                _logWorker = null;
            }
        }

        private void LogWorkerLoop()
        {
            while (true)
            {
                List<LogRecord> batch = null;
                bool stopping;
                lock (_sync)
                {
                    if (_queuedRecords.Count > 0)
                    {
                        batch = new List<LogRecord>(_queuedRecords.Count);
                        while (_queuedRecords.Count > 0)
                            batch.Add(_queuedRecords.Dequeue());
                    }
                    stopping = _logWorkerStopping;
                }

                if (batch != null && batch.Count > 0)
                {
                    WriteBatch(batch);
                    continue;
                }

                if (stopping)
                    return;

                try { _logSignal.WaitOne(250); }
                catch { return; }
            }
        }

        private void WriteBatch(List<LogRecord> batch)
        {
            var runtime = new List<string>();
            var debug = new List<string>();
            foreach (LogRecord record in batch)
            {
                if (string.Equals(record.Path, _debugPath, StringComparison.OrdinalIgnoreCase))
                    debug.Add(record.Line);
                else
                    runtime.Add(record.Line);
            }

            AppendRecords(_runtimePath, runtime);
            AppendRecords(_debugPath, debug);
        }

        private static void AppendRecords(string path, List<string> records)
        {
            if (records == null || records.Count == 0)
                return;

            try
            {
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                using (StreamWriter writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(false)))
                {
                    foreach (string record in records)
                        writer.WriteLine(record);
                }
            }
            catch
            {
                // A diagnostics failure must never reach the game thread.
            }
        }

        private static void AppendRecord(string path, string record)
        {
            AppendRecords(path, new List<string> { record });
        }

        private static bool LooksLikeFailure(string category)
        {
            if (string.IsNullOrEmpty(category))
                return false;

            return category.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("INVALID", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("TIMEOUT", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("ABORTED", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("NOT_CONFIRMED", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return "GENERAL";

            return NormalizeRecordText(category.Trim(), "GENERAL");
        }

        private static string NormalizeMessage(string message)
        {
            return NormalizeRecordText(message, string.Empty);
        }

        private static string NormalizeRecordText(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value))
                return fallback;

            string normalized = value
                .Replace("\r\n", "\\n")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");

            if (normalized.Length <= MaximumRecordLength)
                return normalized;

            return normalized.Substring(0, MaximumRecordLength)
                + " [truncated]";
        }

        private sealed class LogRecord
        {
            internal readonly string Path;
            internal readonly string Line;

            internal LogRecord(string path, string line)
            {
                Path = path;
                Line = line;
            }
        }

        private sealed class ThrottleState
        {
            internal bool HasWritten;
            internal DateTime LastWrittenUtc;
            internal int SuppressedCount;
        }
    }
}
