using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using UnityEngine;

namespace QueryRigSdk
{
    /// <summary>Configuration for <see cref="QueryRig.Init"/>. Every field is optional;
    /// unset fields fall back to config files / env vars / defaults.</summary>
    public sealed class QueryRigSettings
    {
        public string ApiKey;
        public string Endpoint;
        /// <summary>true | false | "remote" (default). See the SDK spec §2.3.</summary>
        public object Enabled;
        public double? FlushIntervalS;
        public int? FlushMaxEvents;
        public long? DiskQueueMaxBytes;
        public double? SampleRate;
        public double? GatePollIntervalS;
    }

    /// <summary>
    /// QueryRig analytics SDK for Unity — implements the QueryRig engine SDK
    /// spec (docs/sdk-spec.md in the queryrig repo).
    ///
    /// Design contract, in order:
    ///  1. Telemetry behavior is a runtime decision — config file, env var, or
    ///     the remote gate. Never a rebuild.
    ///  2. Accepted events are never lost: memory queue -> disk spill -> replay,
    ///     with client-generated event_ids so retries are dedupe-safe.
    ///  3. Track() never costs a frame: it stamps and enqueues; serialization,
    ///     disk, and HTTP run on a worker thread.
    ///
    /// Usage: QueryRig.Track("level_complete", new Dictionary&lt;string, object&gt; {
    ///     { "level", 3 }, { "duration_s", 82.5 } });
    /// </summary>
    public static class QueryRig
    {
        private static QueryRigClient _client;

        /// <summary>Resolve config and start the SDK. Called automatically on
        /// startup; call directly with overrides for tests or manual setup.
        /// Safe to re-call.</summary>
        public static void Init(QueryRigSettings overrides = null)
        {
            _client?.Shutdown();
            _client = new QueryRigClient(overrides);
        }

        /// <summary>Queue an event under a name. Safe from any thread; never blocks on I/O.
        /// The name is sent as the <c>event_name</c> field, which the dashboard's query
        /// templates look for by default — a convention, not a requirement.</summary>
        public static void Track(string eventName, Dictionary<string, object> properties = null)
            => _client?.Track(eventName, properties);

        /// <summary>Queue an event with no name — the fields are entirely yours.
        /// Nothing is reserved server-side: an event is any set of fields, and the only
        /// rule is that it carries at least one. Use this when the game already has its
        /// own vocabulary, and point the query templates at that field instead.</summary>
        public static void TrackFields(Dictionary<string, object> fields)
            => _client?.TrackFields(fields);

        /// <summary>Override the default (device-derived) player id.</summary>
        public static void SetPlayerId(string playerId) => _client?.SetPlayerId(playerId);

        /// <summary>Force an immediate flush (asynchronous — returns at once).</summary>
        public static void Flush() => _client?.Flush();

        /// <summary>Re-check the remote gate now instead of waiting for the next poll.</summary>
        public static void RefreshGate() => _client?.RefreshGate();

        /// <summary>Effective telemetry state, for game-side debug UI.</summary>
        public static bool IsEnabled => _client != null && _client.IsEnabled;

        /// <summary>Final flush with a short deadline, then stop the worker.</summary>
        public static void Shutdown()
        {
            _client?.Shutdown();
            _client = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void AutoInit()
        {
            if (_client == null)
            {
                Init();
            }
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
        }
    }

    internal sealed class QueryRigClient
    {
        private const string SdkVersion = "0.1.0";

        // Spec limits (server-enforced; batches are built to stay inside them).
        private const int MaxBatchEvents = 500;
        private const int FlushMaxBytes = 200_000; // headroom under the 256 KB payload cap
        private const int MemoryQueueMax = 10_000;
        private const long SessionGapMs = 30 * 60 * 1000;
        private const long BackoffBaseMs = 5_000;
        private const long BackoffCapMs = 5 * 60 * 1000;
        private const long RejectedFileMaxBytes = 262_144;

        private enum EnabledMode { HardOff, HardOn, Remote }
        private enum SendOutcome { Delivered, Rejected, Retryable, Dormant }

        // Resolved config (immutable after construction).
        private readonly string _apiKey = "";
        private readonly string _endpoint = "";
        private readonly EnabledMode _mode = EnabledMode.Remote;
        private readonly double _flushIntervalS = 5;
        private readonly int _flushMaxEvents = 100;
        private readonly long _diskQueueMaxBytes = 8 * 1024 * 1024;
        private readonly double _sampleRate = 1.0;
        private readonly double _gatePollIntervalS = 300;

        private readonly bool _inert = true; // hard-off or no api_key: zero network
        private volatile bool _dormant;      // 401 or gate-off: drop new events, keep disk queue

        // Captured on the main thread at Init — Unity APIs are main-thread-only.
        private readonly string _dataDir;
        private readonly string _queueDir;
        private readonly string _platform;
        private readonly string _buildVersion;
        private string _playerId;

        // Session state.
        private string _sessionId = "";
        private long _sessionStartMs;
        private long _lastEventMs;
        private bool _sampledIn = true;

        // Memory queue (any-thread Track).
        private readonly object _queueLock = new object();
        private List<Dictionary<string, object>> _queue = new List<Dictionary<string, object>>();
        private int _droppedOverflow;

        // Worker thread + signaling.
        private readonly object _signal = new object();
        private readonly Thread _thread;
        private bool _flushRequested;
        private bool _gateRequested;
        private bool _quitting;
        private bool _shutdownDone;

        // Delivery state (worker thread only).
        private int _consecutiveFailures;
        private long _nextDrainMs;
        private readonly HttpClient _http;
        private volatile int _httpTimeoutMs = 5_000;

        public QueryRigClient(QueryRigSettings overrides)
        {
            // ---- Config resolution (spec §2.1): defaults < StreamingAssets
            // < next-to-executable < persistentDataPath < env < cmdline < overrides.
            var cfg = new Dictionary<string, object>();
            ApplyJsonFile(cfg, Path.Combine(Application.streamingAssetsPath ?? "", "queryrig.json"));
            string exeDir = Directory.GetParent(Application.dataPath ?? ".")?.FullName;
            if (exeDir != null)
            {
                ApplyJsonFile(cfg, Path.Combine(exeDir, "queryrig.json"));
            }
            ApplyJsonFile(cfg, Path.Combine(Application.persistentDataPath, "queryrig.json"));
            ApplyEnv(cfg, "QUERYRIG_API_KEY", "api_key");
            ApplyEnv(cfg, "QUERYRIG_ENDPOINT", "endpoint");
            ApplyEnv(cfg, "QUERYRIG_ENABLED", "enabled");
            ApplyCommandLine(cfg);
            if (overrides != null)
            {
                if (overrides.ApiKey != null) cfg["api_key"] = overrides.ApiKey;
                if (overrides.Endpoint != null) cfg["endpoint"] = overrides.Endpoint;
                if (overrides.Enabled != null) cfg["enabled"] = overrides.Enabled;
                if (overrides.FlushIntervalS.HasValue) cfg["flush_interval_s"] = overrides.FlushIntervalS.Value;
                if (overrides.FlushMaxEvents.HasValue) cfg["flush_max_events"] = (double)overrides.FlushMaxEvents.Value;
                if (overrides.DiskQueueMaxBytes.HasValue) cfg["disk_queue_max_bytes"] = (double)overrides.DiskQueueMaxBytes.Value;
                if (overrides.SampleRate.HasValue) cfg["sample_rate"] = overrides.SampleRate.Value;
                if (overrides.GatePollIntervalS.HasValue) cfg["gate_poll_interval_s"] = overrides.GatePollIntervalS.Value;
            }

            _apiKey = GetString(cfg, "api_key", "");
            _endpoint = GetString(cfg, "endpoint", "");
            _mode = ParseEnabled(cfg.TryGetValue("enabled", out object en) ? en : "remote");
            _flushIntervalS = GetNumber(cfg, "flush_interval_s", 5);
            _flushMaxEvents = (int)GetNumber(cfg, "flush_max_events", 100);
            _diskQueueMaxBytes = (long)GetNumber(cfg, "disk_queue_max_bytes", 8 * 1024 * 1024);
            _sampleRate = GetNumber(cfg, "sample_rate", 1.0);
            _gatePollIntervalS = GetNumber(cfg, "gate_poll_interval_s", 300);

            _dataDir = Path.Combine(Application.persistentDataPath, "queryrig");
            _queueDir = Path.Combine(_dataDir, "queue");
            _platform = Application.platform.ToString().ToLowerInvariant();
            _buildVersion = Application.version ?? "";

            if (_mode == EnabledMode.HardOff || string.IsNullOrEmpty(_apiKey))
            {
                // Hard-off (or unconfigured): zero network calls, verifiable
                // with a packet capture. Track() drops immediately.
                _inert = true;
                return;
            }
            _inert = false;

            Directory.CreateDirectory(_queueDir);
            _playerId = DefaultPlayerId();
            bool gate = _mode != EnabledMode.Remote || LoadCachedGate();
            _dormant = !gate;

            _http = new HttpClient();
            _http.Timeout = Timeout.InfiniteTimeSpan; // per-request CTS enforces the real timeout

            _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "QueryRig" };
            _thread.Start();

            if (_mode == EnabledMode.Remote)
            {
                RefreshGate();
            }
            Application.focusChanged -= OnFocusChanged;
            Application.focusChanged += OnFocusChanged;

            StartSession();
        }

        private void OnFocusChanged(bool focused)
        {
            if (!focused)
            {
                Flush(); // lifecycle flush (spec §3.4)
            }
        }

        // ---------------------------------------------------------- public API

        public void Track(string eventName, Dictionary<string, object> properties)
        {
            var fields = properties != null
                ? new Dictionary<string, object>(properties)
                : new Dictionary<string, object>();
            fields["event_name"] = eventName;
            TrackFields(fields);
        }

        public void TrackFields(Dictionary<string, object> fields)
        {
            if (_inert || !IsCollecting() || !_sampledIn)
            {
                return;
            }
            long now = NowMs();
            if (_lastEventMs > 0 && now - _lastEventMs > SessionGapMs)
            {
                StartSession(); // 30-minute idle rollover (spec §3.5)
            }
            _lastEventMs = now;
            Enqueue(Stamp(fields));
        }

        public void SetPlayerId(string playerId) => _playerId = playerId;

        public void Flush()
        {
            if (_inert)
            {
                return;
            }
            lock (_signal)
            {
                _flushRequested = true;
                Monitor.Pulse(_signal);
            }
        }

        public void RefreshGate()
        {
            if (_inert || _mode != EnabledMode.Remote)
            {
                return;
            }
            lock (_signal)
            {
                _gateRequested = true;
                Monitor.Pulse(_signal);
            }
        }

        public bool IsEnabled => !_inert && !_dormant;

        public void Shutdown()
        {
            if (_inert || _shutdownDone)
            {
                return;
            }
            _shutdownDone = true;
            Application.focusChanged -= OnFocusChanged;
            EndSession();
            _httpTimeoutMs = 2_000; // shutdown deadline (spec §3.9)
            lock (_signal)
            {
                _quitting = true;
                Monitor.Pulse(_signal);
            }
            _thread.Join(5_000);
        }

        // ---------------------------------------------------------- config

        private static void ApplyJsonFile(Dictionary<string, object> cfg, string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }
                if (QueryRigJson.Parse(File.ReadAllText(path)) is Dictionary<string, object> parsed)
                {
                    foreach (var pair in parsed)
                    {
                        cfg[pair.Key] = pair.Value; // unknown keys carried, ignored below
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"QueryRig: ignoring unparseable config at {path}: {e.Message}");
            }
        }

        private static void ApplyEnv(Dictionary<string, object> cfg, string env, string key)
        {
            string value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(value))
            {
                cfg[key] = value;
            }
        }

        private static void ApplyCommandLine(Dictionary<string, object> cfg)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                TryArg(cfg, arg, "-queryrig-api-key=", "api_key");
                TryArg(cfg, arg, "-queryrig-endpoint=", "endpoint");
                TryArg(cfg, arg, "-queryrig-enabled=", "enabled");
            }
        }

        private static void TryArg(Dictionary<string, object> cfg, string arg, string prefix, string key)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cfg[key] = arg.Substring(prefix.Length);
            }
        }

        private static EnabledMode ParseEnabled(object value)
        {
            if (value is bool b)
            {
                return b ? EnabledMode.HardOn : EnabledMode.HardOff;
            }
            switch (Convert.ToString(value, CultureInfo.InvariantCulture)?.ToLowerInvariant())
            {
                case "true": case "1": case "on": case "yes": return EnabledMode.HardOn;
                case "false": case "0": case "off": case "no": return EnabledMode.HardOff;
                default: return EnabledMode.Remote;
            }
        }

        private static string GetString(Dictionary<string, object> cfg, string key, string fallback)
            => cfg.TryGetValue(key, out object v) && v is string s ? s : fallback;

        private static double GetNumber(Dictionary<string, object> cfg, string key, double fallback)
            => cfg.TryGetValue(key, out object v) && v is double d ? d : fallback;

        // ---------------------------------------------------------- sessions

        private void StartSession()
        {
            _sessionId = NewUuid();
            _sessionStartMs = NowMs();
            _lastEventMs = _sessionStartMs;
            // Per-session sampling (spec §3.8): decide once so sampled-in
            // sessions are complete and funnels stay coherent.
            double rate = Math.Max(0.0, Math.Min(1.0, _sampleRate));
            _sampledIn = (Fnv1a(_sessionId) & 0x7fffffff) % 10_000 < (int)(rate * 10_000);
            if (_sampledIn && IsCollecting())
            {
                Enqueue(Stamp(new Dictionary<string, object> { { "event_name", "session_start" } }));
            }
        }

        private void EndSession()
        {
            if (string.IsNullOrEmpty(_sessionId) || !_sampledIn || !IsCollecting())
            {
                return;
            }
            Enqueue(Stamp(new Dictionary<string, object>
            {
                { "event_name", "session_end" },
                { "duration_s", (NowMs() - _sessionStartMs) / 1000.0 },
            }));
        }

        // ---------------------------------------------------------- queueing

        // Stamp everything at enqueue time (spec §3.3) so retries can't change
        // the event. Explicit values in the properties win over SDK defaults.
        //
        // Values go out as their natural JSON types; the server coerces every
        // scalar to a string so each field has one predictable type to query.
        // Never pre-stringify here — that would lose the distinction between
        // "not sent" and "sent empty".
        private Dictionary<string, object> Stamp(Dictionary<string, object> properties)
        {
            var ev = properties != null
                ? new Dictionary<string, object>(properties)
                : new Dictionary<string, object>();
            if (!ev.ContainsKey("event_id")) ev["event_id"] = NewUuid();
            if (!ev.ContainsKey("timestamp")) ev["timestamp"] = NowMs();
            if (!ev.ContainsKey("session_id")) ev["session_id"] = _sessionId;
            if (!ev.ContainsKey("player_id")) ev["player_id"] = _playerId;
            if (!ev.ContainsKey("platform")) ev["platform"] = _platform;
            if (!ev.ContainsKey("build_version")) ev["build_version"] = _buildVersion;
            return ev;
        }

        private void Enqueue(Dictionary<string, object> ev)
        {
            bool requestFlush;
            lock (_queueLock)
            {
                _queue.Add(ev);
                while (_queue.Count > MemoryQueueMax) // drop OLDEST on overflow
                {
                    _queue.RemoveAt(0);
                    _droppedOverflow++;
                }
                requestFlush = _queue.Count >= _flushMaxEvents;
            }
            if (requestFlush)
            {
                Flush();
            }
        }

        private bool IsCollecting()
        {
            switch (_mode)
            {
                case EnabledMode.HardOn: return true;
                case EnabledMode.HardOff: return false;
                default: return !_dormant; // remote: the gate decides
            }
        }

        // ---------------------------------------------------------- worker

        private void WorkerLoop()
        {
            long nextFlushAt = NowMs() + (long)(_flushIntervalS * 1000);
            long nextGateAt = long.MaxValue;
            if (_mode == EnabledMode.Remote)
            {
                nextGateAt = NowMs() + (long)(_gatePollIntervalS * 1000);
            }

            while (true)
            {
                bool doFlush, doGate, doQuit;
                lock (_signal)
                {
                    if (!_quitting && !_flushRequested && !_gateRequested)
                    {
                        Monitor.Wait(_signal, 250);
                    }
                    doQuit = _quitting;
                    doFlush = _flushRequested;
                    doGate = _gateRequested;
                    _flushRequested = _gateRequested = false;
                }

                long now = NowMs();
                if (doGate || now >= nextGateAt)
                {
                    nextGateAt = now + (long)(_gatePollIntervalS * 1000);
                    DoGateCheck();
                }
                if (doFlush || doQuit || now >= nextFlushAt)
                {
                    nextFlushAt = now + (long)(_flushIntervalS * 1000);
                    DoFlush();
                }
                if (doQuit)
                {
                    SpillQueueToDisk(); // whatever the final flush couldn't deliver
                    return;
                }
            }
        }

        private void DoFlush()
        {
            if (_dormant)
            {
                return;
            }
            List<Dictionary<string, object>> events;
            int dropped;
            lock (_queueLock)
            {
                events = _queue;
                _queue = new List<Dictionary<string, object>>();
                dropped = _droppedOverflow;
                _droppedOverflow = 0;
            }
            if (dropped > 0)
            {
                Debug.LogWarning($"QueryRig: memory queue overflowed; dropped {dropped} oldest events");
            }
            if (events.Count == 0)
            {
                DrainDiskOne();
                return;
            }

            var lines = events.Select(e => QueryRigJson.Serialize(e)).ToList();
            bool allOk = true;
            foreach (var batch in SplitBatches(lines))
            {
                switch (SendBatch(batch))
                {
                    case SendOutcome.Delivered:
                        break;
                    case SendOutcome.Rejected:
                        AppendRejected(batch);
                        break;
                    default: // Retryable or Dormant: spill; the drain loop owns retries
                        SpillToDisk(batch);
                        allOk = false;
                        break;
                }
            }
            if (allOk)
            {
                DrainDiskOne();
            }
        }

        private static IEnumerable<List<string>> SplitBatches(List<string> lines)
        {
            var current = new List<string>();
            int bytes = 0;
            foreach (string line in lines)
            {
                int n = Encoding.UTF8.GetByteCount(line);
                if (current.Count >= MaxBatchEvents || (bytes + n > FlushMaxBytes && current.Count > 0))
                {
                    yield return current;
                    current = new List<string>();
                    bytes = 0;
                }
                current.Add(line);
                bytes += n;
            }
            if (current.Count > 0)
            {
                yield return current;
            }
        }

        // One batch -> one POST. Pure delivery attempt — the CALLER decides
        // what to do with the lines, so a disk-drained batch is never
        // re-spilled into a duplicate file.
        private SendOutcome SendBatch(List<string> lines)
        {
            string body = "[" + string.Join(",", lines) + "]";
            int code;
            string responseBody = "";
            try
            {
                using (var cts = new CancellationTokenSource(_httpTimeoutMs))
                using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                    request.Headers.TryAddWithoutValidation("User-Agent", "queryrig-unity/" + SdkVersion);
                    using (var response = _http.SendAsync(request, cts.Token).GetAwaiter().GetResult())
                    {
                        code = (int)response.StatusCode;
                        responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
            }
            catch
            {
                NoteFailure();
                return SendOutcome.Retryable; // network error or timeout
            }

            if (code == 200)
            {
                NoteSuccess();
                try
                {
                    if (QueryRigJson.Parse(responseBody) is Dictionary<string, object> parsed
                        && parsed.TryGetValue("rejected", out object rejected)
                        && rejected is double r && r > 0)
                    {
                        Debug.LogWarning($"QueryRig: server rejected {(int)r} event(s) — integration bug, see response results");
                    }
                }
                catch { /* response diagnostics only */ }
                return SendOutcome.Delivered;
            }
            if (code == 401)
            {
                // Key unknown/disabled: go dormant, keep events on disk, let
                // the gate poll decide when to wake up (spec §1 responses).
                _dormant = true;
                return SendOutcome.Dormant;
            }
            if (code == 400 || code == 413)
            {
                Debug.LogWarning($"QueryRig: batch rejected with HTTP {code} — dropped (see {_dataDir}/rejected.ndjson)");
                return SendOutcome.Rejected;
            }
            NoteFailure();
            return SendOutcome.Retryable; // 5xx etc.
        }

        private void NoteSuccess()
        {
            _consecutiveFailures = 0;
            _nextDrainMs = 0;
        }

        private void NoteFailure()
        {
            _consecutiveFailures++;
            long backoff = Math.Min(BackoffBaseMs << Math.Min(_consecutiveFailures - 1, 10), BackoffCapMs);
            backoff += (long)(backoff * ((_random.NextDouble() * 0.4) - 0.2)); // jitter
            _nextDrainMs = NowMs() + backoff;
        }

        private readonly System.Random _random = new System.Random();

        // ---------------------------------------------------------- disk queue

        private void SpillQueueToDisk()
        {
            List<Dictionary<string, object>> remaining;
            lock (_queueLock)
            {
                remaining = _queue;
                _queue = new List<Dictionary<string, object>>();
            }
            if (remaining.Count == 0)
            {
                return;
            }
            var lines = remaining.Select(e => QueryRigJson.Serialize(e)).ToList();
            foreach (var batch in SplitBatches(lines))
            {
                SpillToDisk(batch);
            }
        }

        private void SpillToDisk(List<string> lines)
        {
            try
            {
                string path = Path.Combine(_queueDir, $"{NowMs()}_{_random.Next(1_000_000):D6}.ndjson");
                File.WriteAllText(path, string.Join("\n", lines));
                EnforceDiskCap();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"QueryRig: cannot write disk queue; {lines.Count} events lost: {e.Message}");
            }
        }

        private void EnforceDiskCap()
        {
            var files = QueueFiles();
            long total = files.Sum(f => new FileInfo(f).Length);
            foreach (string file in files) // sorted oldest first — drop OLDEST
            {
                if (total <= _diskQueueMaxBytes)
                {
                    break;
                }
                total -= new FileInfo(file).Length;
                File.Delete(file);
            }
        }

        private string[] QueueFiles()
        {
            if (!Directory.Exists(_queueDir))
            {
                return Array.Empty<string>();
            }
            string[] files = Directory.GetFiles(_queueDir, "*.ndjson");
            Array.Sort(files); // timestamp-named => oldest first
            return files;
        }

        // Send at most one disk batch (spec §3.6: a trickle, not a burst),
        // honoring the failure backoff. Original event_ids/timestamps ride
        // along unchanged.
        private void DrainDiskOne()
        {
            if (_dormant || NowMs() < _nextDrainMs)
            {
                return;
            }
            var files = QueueFiles();
            if (files.Length == 0)
            {
                return;
            }
            string path = files[0];
            List<string> lines;
            try
            {
                lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            }
            catch
            {
                return;
            }
            if (lines.Count == 0)
            {
                File.Delete(path);
                return;
            }
            switch (SendBatch(lines))
            {
                case SendOutcome.Delivered:
                    // Delete only after success — a crash in between resends,
                    // and the server dedupes on event_id (same contract as the
                    // server's own R2 replay).
                    File.Delete(path);
                    break;
                case SendOutcome.Rejected:
                    AppendRejected(lines);
                    File.Delete(path);
                    break;
                default: // Retryable (backoff set) or Dormant: leave the file
                    break;
            }
        }

        private void AppendRejected(List<string> lines)
        {
            try
            {
                string path = Path.Combine(_dataDir, "rejected.ndjson");
                if (File.Exists(path) && new FileInfo(path).Length > RejectedFileMaxBytes)
                {
                    return; // capped, best-effort
                }
                File.AppendAllText(path, string.Join("\n", lines) + "\n");
            }
            catch { /* best-effort */ }
        }

        // ---------------------------------------------------------- remote gate

        private void DoGateCheck()
        {
            int code;
            string body;
            try
            {
                using (var cts = new CancellationTokenSource(_httpTimeoutMs))
                using (var request = new HttpRequestMessage(HttpMethod.Get, Origin(_endpoint) + "/v1/config"))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                    request.Headers.TryAddWithoutValidation("User-Agent", "queryrig-unity/" + SdkVersion);
                    using (var response = _http.SendAsync(request, cts.Token).GetAwaiter().GetResult())
                    {
                        code = (int)response.StatusCode;
                        body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
            }
            catch
            {
                // Fetch failure: keep the cached answer (spec §3.2) — losing
                // data over a flaky config fetch is worse than sending.
                return;
            }

            bool enabled;
            if (code == 200)
            {
                try
                {
                    if (!(QueryRigJson.Parse(body) is Dictionary<string, object> parsed)
                        || !(parsed.TryGetValue("enabled", out object v) && v is bool e))
                    {
                        return;
                    }
                    enabled = e;
                }
                catch
                {
                    return;
                }
            }
            else if (code == 401)
            {
                enabled = false;
            }
            else
            {
                return;
            }
            SaveCachedGate(enabled);
            _dormant = !enabled;
        }

        private bool LoadCachedGate()
        {
            try
            {
                string path = Path.Combine(_dataDir, "gate.json");
                if (File.Exists(path)
                    && QueryRigJson.Parse(File.ReadAllText(path)) is Dictionary<string, object> parsed
                    && parsed.TryGetValue("enabled", out object v) && v is bool enabled)
                {
                    return enabled;
                }
            }
            catch { /* fall through */ }
            return true; // first run: default enabled
        }

        private void SaveCachedGate(bool enabled)
        {
            try
            {
                File.WriteAllText(Path.Combine(_dataDir, "gate.json"),
                    "{\"enabled\":" + (enabled ? "true" : "false") + "}");
            }
            catch { /* best-effort */ }
        }

        private static string Origin(string url)
        {
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                return url;
            }
            int pathStart = url.IndexOf('/', schemeEnd + 3);
            return pathStart < 0 ? url : url.Substring(0, pathStart);
        }

        // ---------------------------------------------------------- utils

        private string DefaultPlayerId()
        {
            string id = SystemInfo.deviceUniqueIdentifier;
            if (!string.IsNullOrEmpty(id) && id != SystemInfo.unsupportedIdentifier)
            {
                return id;
            }
            // Some platforms don't support device ids: persist a random one.
            try
            {
                string path = Path.Combine(_dataDir, "device_id");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path).Trim();
                }
                id = NewUuid();
                Directory.CreateDirectory(_dataDir);
                File.WriteAllText(path, id);
                return id;
            }
            catch
            {
                return NewUuid();
            }
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string NewUuid() => Guid.NewGuid().ToString("D");

        private static int Fnv1a(string s)
        {
            unchecked
            {
                int hash = (int)2166136261;
                foreach (char c in s)
                {
                    hash = (hash ^ c) * 16777619;
                }
                return hash;
            }
        }
    }
}
