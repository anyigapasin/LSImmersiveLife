using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using NAudio.Wave;

namespace LSImmersiveLife
{
    internal enum LSPDAudioResult
    {
        Queued, Inactive, Muted, Paused, Duplicate, Cooldown, UnknownEvent,
        NoAsset, QueueFull, ClosedScope, Unavailable
    }

    internal enum LSPDAudioPriority { Routine, Dispatch, Urgent, Emergency, Critical }

    /// <summary>
    /// First-party Police speech resolver/player. Gameplay reports facts with
    /// stable occurrence IDs; audio never creates those facts or calls GTA.
    /// Persistent paths always come from the single LSIMMERSIVEPATH collection.
    /// </summary>
    internal sealed class LSPDAudioDispatch : IDisposable
    {
        private readonly object _sync = new object();
        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSImmersiveLog _log;
        private readonly Func<long> _clock;
        private readonly Func<string, float, ILSPDPlayback> _play;
        private readonly bool _backgroundPlaybackCreation;
        private readonly Random _random = new Random();
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _seenOrder = new Queue<string>();
        private readonly HashSet<string> _closed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _warnings = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _cooldowns = new Dictionary<string, long>();
        private readonly Dictionary<string, string> _lastClips = new Dictionary<string, string>();
        private Dictionary<string, AudioEvent> _events = new Dictionary<string, AudioEvent>(StringComparer.Ordinal);
        private ILSPDPlayback _player;
        private Pending _current;
        private AudioClip _currentClip;
        private Pending _openingPending;
        private AudioClip _openingClip;
        private bool _playbackOpening;
        private long _playbackGeneration;
        private bool _reloadInProgress;
        private bool _active, _enabled = true, _disposed, _paused, _attemptedLoad, _faulted;
        private float _volume = 0.55f;
        private int _capacity = 12;
        private string _language = "en-US";
        private long _sequence;

        // The test clock/player permit deterministic coverage without GTA or a
        // sound device. Production uses a monotonic clock and installed NAudio.
        internal LSPDAudioDispatch(LSIMMERSIVEPATH paths, LSImmersiveLog log,
            Func<long> clock = null, Func<string, float, ILSPDPlayback> play = null)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _log = log ?? throw new ArgumentNullException("log");
            Stopwatch timer = Stopwatch.StartNew();
            _clock = clock ?? (() => timer.ElapsedMilliseconds);
            _backgroundPlaybackCreation = play == null;
            _play = play ?? ((path, volume) => new LSPDWavePlayback(path, volume));
        }

        internal bool IsActive { get { lock (_sync) return _active && !_disposed; } }
        internal int EventCount { get { lock (_sync) return _events.Count; } }
        internal int ReadyClipCount { get { lock (_sync) return _events.Values.Sum(e => e.Clips.Count(c => c.Available)); } }
        internal int PendingCount { get { lock (_sync) return _pending.Count; } }
        internal bool IsPlaying { get { lock (_sync) return _playbackOpening || (_player != null && !_player.Finished); } }
        internal string LastPlayedEvent { get; private set; }
        internal string LastPlayedClip { get; private set; }

        internal void SetActive(bool active)
        {
            bool queueCatalogLoad = false;
            lock (_sync)
            {
                // Constructing a menu/core is not entering Police mode. Only
                // explicit authority selection enables this otherwise silent service.
                if (_disposed || _active == active) return;
                StopLocked();
                _seen.Clear(); _seenOrder.Clear(); _closed.Clear(); _cooldowns.Clear();
                _active = active;
                // A Police role may intentionally mute audio. Do not load or
                // touch the catalog while it is disabled; re-enabling audio
                // performs an explicit catalog load.
                if (active && !_attemptedLoad && _enabled && _volume > 0f)
                    queueCatalogLoad = true;
                _log.Runtime("POLICE_AUDIO_SESSION", active ? "Activated." : "Stopped.");
            }
            if (queueCatalogLoad)
                Reload(false);
        }

        internal void SetOptions(bool enabled, int volume)
        {
            bool loadCatalog = false;
            lock (_sync)
            {
                // Change this voice reader's samples, never the global mixer,
                // vehicle radio, sirens, or any other mod's output.
                _enabled = enabled;
                _volume = Math.Max(0, Math.Min(100, volume)) / 100f;
                if (!enabled || _volume == 0) StopLocked();
                else if (_player != null) _player.SetVolume(_volume);
                else if (_active && !_attemptedLoad) loadCatalog = true;
            }
            if (loadCatalog) Reload(false);
        }

        internal bool Reload()
        {
            return Reload(true);
        }

        private bool Reload(bool validateAudioContent)
        {
            lock (_sync)
            {
                // Do not let two reloads race the replacement cache.
                if (_disposed || _reloadInProgress)
                    return false;
                _reloadInProgress = true;
                _attemptedLoad = true;
            }

            Dictionary<string, AudioEvent> events = null;
            int capacity = 12;
            string language = "en-US";
            int unavailable = 0;
            List<string> problems = new List<string>();

            try
            {
                // Parse and validate into a replacement cache first. Structural
                // errors preserve the last good catalog; missing ready recordings
                // are isolated so an incomplete library still works.
                _paths.EnsureResourceDirectories();
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 4 * 1024 * 1024
                };
                XDocument document;
                string catalogPath = File.Exists(_paths.ResponseAudioXmlPath)
                    ? _paths.ResponseAudioXmlPath
                    : _paths.ResponseAudioXmlFallbackPath;
                if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
                    throw new FileNotFoundException("Police response-audio catalog was not found.", catalogPath);
                using (XmlReader reader = XmlReader.Create(catalogPath, settings))
                    document = XDocument.Load(reader);
                XElement root = document.Root;
                if (root == null || root.Name != "LSImmersiveLifeAudio" || (string)root.Attribute("version") != "1")
                    throw new InvalidDataException("Expected LSImmersiveLifeAudio version 1.");
                capacity = Number(root, "queueCapacity", 12, 1, 64);
                language = Required(root, "language");
                events = new Dictionary<string, AudioEvent>(StringComparer.Ordinal);
                var clipIds = new HashSet<string>(StringComparer.Ordinal);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement node in root.Elements("Group").Elements("Event"))
                {
                    string id = Required(node, "id");
                    if (!ValidId(id) || events.ContainsKey(id))
                        throw new InvalidDataException("Invalid or duplicate event: " + id);
                    LSPDAudioPriority priority;
                    if (!Enum.TryParse(Required(node, "priority"), false, out priority)
                        || !Enum.IsDefined(typeof(LSPDAudioPriority), priority))
                        throw new InvalidDataException("Invalid priority: " + id);
                    var entry = new AudioEvent
                    {
                        Id = id, Priority = priority, Speaker = Required(node, "speaker"),
                        Cooldown = Number(node, "cooldownMs", 1000, 0, 600000),
                        Expiry = Number(node, "expiresMs", 30000, 100, 600000)
                    };
                    if (entry.Speaker != "dispatch" && entry.Speaker != "player"
                        && entry.Speaker != "unit" && entry.Speaker != "civilian")
                        throw new InvalidDataException("Invalid speaker: " + id);
                    foreach (XElement item in node.Elements("Clip"))
                    {
                        string clipId = Required(item, "id");
                        if (!clipIds.Add(clipId)) throw new InvalidDataException("Duplicate clip: " + clipId);
                        string state = Required(item, "status");
                        if (state != "ready" && state != "planned" && state != "disabled")
                            throw new InvalidDataException("Invalid clip status: " + clipId);
                        string path = _paths.ResolveAudioFile(Required(item, "file"));
                        if (!paths.Add(path)) throw new InvalidDataException("Duplicate asset path: " + path);
                        string transcript = item.Value.Trim();
                        if (transcript.Length == 0 || transcript.Length > 2000)
                            throw new InvalidDataException("Invalid transcript: " + clipId);
                        var clip = new AudioClip
                        {
                            Id = clipId, Path = path, Kind = (string)item.Attribute("incidentType") ?? "",
                            Agency = (string)item.Attribute("agency") ?? "",
                            Language = Required(item, "language"),
                            Weight = Number(item, "weight", 1, 1, 100)
                        };
                        // Planned/disabled files are authored future assets,
                        // not reasons to spam missing-file errors each frame.
                        if (state == "ready")
                        {
                            try
                            {
                                // Authority activation only needs a fast,
                                // file-presence catalog so it cannot block the
                                // LemonUI callback on hundreds of WAV parses.
                                // Explicit Reload() retains full PCM validation.
                                if (validateAudioContent)
                                    ValidateWave(path);
                                else if (!File.Exists(path))
                                    throw new FileNotFoundException("Audio file was not found.", path);
                                clip.Available = true;
                            }
                            catch (Exception ex)
                            {
                                unavailable++;
                                if (problems.Count < 8) problems.Add(clipId + ": " + ex.Message);
                            }
                        }
                        entry.Clips.Add(clip);
                    }
                    events.Add(id, entry);
                }
                if (events.Count == 0 || events.Count > 2000 || clipIds.Count > 10000)
                    throw new InvalidDataException("Catalog size is outside supported limits.");

                lock (_sync)
                {
                    if (_disposed)
                        return false;
                    StopLocked();
                    _events = events; _capacity = capacity; _language = language;
                    _lastClips.Clear(); _warnings.Clear(); _faulted = false;
                    _log.Runtime("AUDIO_CATALOG_LOADED", events.Count + " events; " + ReadyClipCount
                        + " ready clips; " + unavailable + " unavailable ready clips.");
                    if (unavailable > 0) WarnOnce("asset-validation", string.Join(" | ", problems));
                }
                return true;
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    if (!_disposed)
                        WarnOnce("reload:" + ex.GetType().Name, "Catalog unchanged: " + ex.Message);
                }
                return false;
            }
            finally
            {
                lock (_sync)
                    _reloadInProgress = false;
            }
        }

        internal LSPDAudioResult Report(string eventId, string scopeId, string occurrenceId,
            string incidentType = null, string agency = null)
        {
            lock (_sync)
            {
                // The actual action owner provides occurrence IDs. Never create
                // fresh IDs here per observation/tick, which defeats deduplication.
                if (!_active || _disposed) return LSPDAudioResult.Inactive;
                if (!_enabled || _volume == 0) return LSPDAudioResult.Muted;
                if (_paused) return LSPDAudioResult.Paused;
                if (_faulted) return LSPDAudioResult.Unavailable;
                if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(occurrenceId)
                    || scopeId.Length > 256 || occurrenceId.Length > 256) return LSPDAudioResult.Unavailable;
                if (_closed.Contains(scopeId)) return LSPDAudioResult.ClosedScope;
                AudioEvent entry;
                if (eventId == null || !_events.TryGetValue(eventId, out entry))
                {
                    WarnOnce("event:" + eventId, "Unknown audio event: " + eventId);
                    return LSPDAudioResult.UnknownEvent;
                }
                string key = scopeId + "\n" + eventId + "\n" + occurrenceId;
                if (!_seen.Add(key)) return LSPDAudioResult.Duplicate;
                _seenOrder.Enqueue(key);
                if (_seenOrder.Count > 8192) _seen.Remove(_seenOrder.Dequeue());
                var message = new Pending
                {
                    Event = entry, Scope = scopeId, Kind = incidentType ?? "", Agency = agency ?? "",
                    Sequence = ++_sequence, Expires = _clock() + entry.Expiry
                };
                if (Candidates(message).Count == 0)
                {
                    WarnOnce("missing:" + eventId + ":" + message.Kind, "No matching ready speech: " + eventId + " / " + message.Kind);
                    return LSPDAudioResult.NoAsset;
                }
                string cooldownKey = scopeId + "\n" + eventId;
                long until;
                if (entry.Priority < LSPDAudioPriority.Emergency
                    && _cooldowns.TryGetValue(cooldownKey, out until) && _clock() < until)
                    return LSPDAudioResult.Cooldown;
                // Higher priority may replace a lower-priority pending message.
                // Equal priorities stay FIFO; the queue has a strict upper bound.
                RemoveExpired();
                if (_pending.Count >= _capacity)
                {
                    Pending lowest = _pending.OrderBy(p => p.Event.Priority).ThenBy(p => p.Sequence).First();
                    if (lowest.Event.Priority >= entry.Priority)
                    {
                        WarnOnce("queue-full", "Audio queue full; a transmission was dropped.");
                        return LSPDAudioResult.QueueFull;
                    }
                    _pending.Remove(lowest);
                }
                _pending.Add(message);
                _cooldowns[cooldownKey] = _clock() + entry.Cooldown;
                if (_cooldowns.Count > 8192)
                    foreach (string expired in _cooldowns.Where(p => p.Value <= _clock()).Select(p => p.Key).ToArray())
                        _cooldowns.Remove(expired);
                if (_current != null && entry.Priority >= LSPDAudioPriority.Emergency
                    && entry.Priority > _current.Event.Priority) StopCurrent();
                if (_playbackOpening && _openingPending != null
                    && entry.Priority >= LSPDAudioPriority.Emergency
                    && entry.Priority > _openingPending.Event.Priority)
                    CancelOpeningLocked();
                return LSPDAudioResult.Queued;
            }
        }

        internal void Process(bool paused = false)
        {
            Pending openingPending = null;
            AudioClip openingClip = null;
            string openingVariantKey = null;
            long openingGeneration = 0;
            float openingVolume = 0f;

            lock (_sync)
            {
                // The game tick selects work only. Production playback creation
                // runs on a worker because opening a WAV and initializing
                // WaveOutEvent can briefly block the SHVDN frame. Injected test
                // factories remain synchronous for deterministic tests.
                if (_disposed) return;
                if (_player != null && _player.Finished)
                {
                    Exception error = _player.Error;
                    _player.Dispose(); _player = null;
                    if (error != null)
                    {
                        _faulted = true; _pending.Clear();
                        WarnOnce("device-error", "Playback failed; reload to retry: " + error.Message);
                    }
                    else if (_current != null && !_current.Cancelled)
                    {
                        LastPlayedEvent = _current.Event.Id; LastPlayedClip = _currentClip.Id;
                        _log.Runtime("AUDIO_PLAYED", LastPlayedEvent + " | " + LastPlayedClip);
                    }
                    _current = null; _currentClip = null;
                }
                _paused = paused;
                if (paused) { StopLocked(); return; }
                if (!_active || !_enabled || _faulted || _player != null
                    || _playbackOpening || _volume == 0) return;
                RemoveExpired();
                if (_pending.Count == 0) return;
                Pending next = _pending.OrderByDescending(p => p.Event.Priority).ThenBy(p => p.Sequence).First();
                _pending.Remove(next);
                List<AudioClip> candidates = Candidates(next);
                if (candidates.Count == 0) return;
                string variantKey = next.Event.Id + "\n" + next.Kind + "\n" + next.Agency;
                string previous;
                if (candidates.Count > 1 && _lastClips.TryGetValue(variantKey, out previous))
                    candidates.RemoveAll(c => c.Id == previous);
                int choice = _random.Next(candidates.Sum(c => c.Weight));
                AudioClip selected = candidates[0];
                foreach (AudioClip clip in candidates)
                {
                    choice -= clip.Weight;
                    if (choice < 0) { selected = clip; break; }
                }

                if (_backgroundPlaybackCreation)
                {
                    _openingPending = next;
                    _openingClip = selected;
                    _playbackOpening = true;
                    openingGeneration = ++_playbackGeneration;
                    openingPending = next;
                    openingClip = selected;
                    openingVariantKey = variantKey;
                    openingVolume = _volume;
                }
                else
                {
                    try
                    {
                        _player = _play(selected.Path, _volume);
                        _current = next; _currentClip = selected;
                        _lastClips[variantKey] = selected.Id;
                        if (_lastClips.Count > 8192) _lastClips.Clear();
                    }
                    catch (Exception ex)
                    {
                        selected.Available = false;
                        _faulted = !(ex is IOException || ex is UnauthorizedAccessException);
                        if (_faulted) _pending.Clear();
                        WarnOnce("play:" + selected.Id, "Could not start " + selected.Id + ": " + ex.Message);
                    }
                }
            }

            if (openingPending == null || openingClip == null)
                return;

            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    CreatePlaybackInBackground(
                        openingPending,
                        openingClip,
                        openingGeneration,
                        openingVolume,
                        openingVariantKey);
                });
                if (!queued)
                    FailBackgroundPlayback(
                        openingPending,
                        openingClip,
                        openingGeneration,
                        new InvalidOperationException("The audio worker could not be queued."));
            }
            catch (Exception ex)
            {
                FailBackgroundPlayback(openingPending, openingClip, openingGeneration, ex);
            }
        }

        private void CreatePlaybackInBackground(
            Pending pending,
            AudioClip clip,
            long generation,
            float volume,
            string variantKey)
        {
            ILSPDPlayback playback = null;
            try
            {
                // LSPDWavePlayback uses only NAudio and file I/O here. It does
                // not call GTA natives or LemonUI, so preparation is safe away
                // from the SHVDN thread.
                playback = _play(clip.Path, volume);
            }
            catch (Exception ex)
            {
                FailBackgroundPlayback(pending, clip, generation, ex);
                return;
            }

            bool accepted = false;
            lock (_sync)
            {
                if (!_disposed && _playbackOpening && generation == _playbackGeneration
                    && ReferenceEquals(_openingPending, pending)
                    && ReferenceEquals(_openingClip, clip)
                    && _active && _enabled && !_paused && _volume > 0f)
                {
                    _player = playback;
                    _current = pending;
                    _currentClip = clip;
                    _lastClips[variantKey] = clip.Id;
                    if (_lastClips.Count > 8192) _lastClips.Clear();
                    _openingPending = null;
                    _openingClip = null;
                    _playbackOpening = false;
                    accepted = true;
                }
            }

            if (!accepted)
                DisposePlayback(playback);
        }

        private void FailBackgroundPlayback(
            Pending pending,
            AudioClip clip,
            long generation,
            Exception error)
        {
            lock (_sync)
            {
                if (!_playbackOpening || generation != _playbackGeneration
                    || !ReferenceEquals(_openingPending, pending)
                    || !ReferenceEquals(_openingClip, clip))
                    return;

                _openingPending = null;
                _openingClip = null;
                _playbackOpening = false;
                clip.Available = false;
                _faulted = !(error is IOException || error is UnauthorizedAccessException);
                if (_faulted) _pending.Clear();
                WarnOnce(
                    "play:" + clip.Id,
                    "Could not start " + clip.Id + ": "
                    + (error == null ? "Unknown playback error." : error.Message));
            }
        }

        private static void DisposePlayback(ILSPDPlayback playback)
        {
            if (playback == null) return;
            try { playback.Stop(); } catch { }
            try { playback.Dispose(); } catch { }
        }

        internal void CancelScope(string scopeId)
        {
            lock (_sync)
            {
                // Dispatch, custody and transport have distinct scopes. Ending
                // a dispatch call must not delete ongoing prison-transfer speech.
                if (string.IsNullOrWhiteSpace(scopeId)) return;
                _closed.Add(scopeId);
                _pending.RemoveAll(p => p.Scope == scopeId);
                if (_current != null && _current.Scope == scopeId) StopCurrent();
                if (_openingPending != null && _openingPending.Scope == scopeId)
                    CancelOpeningLocked();
            }
        }

        internal void Stop() { lock (_sync) StopLocked(); }

        public void Dispose()
        {
            lock (_sync)
            {
                // A stop callback retains ownership of audio resources until
                // safe release, even when script abortion means no further ticks.
                if (_disposed) return;
                StopLocked();
                if (_player != null) _player.Dispose();
                _active = false; _disposed = true;
            }
        }

        private List<AudioClip> Candidates(Pending message)
        {
            // Fallback stays inside exactly the same event/speaker meaning.
            // Generic context is allowed; unrelated outcomes are never substituted.
            var eligible = message.Event.Clips.Where(c => c.Available && c.Language == _language
                && (c.Kind.Length == 0 || c.Kind == message.Kind)
                && (c.Agency.Length == 0 || c.Agency == message.Agency)).ToList();
            if (eligible.Count == 0) return eligible;
            int score = eligible.Max(c => (c.Kind.Length > 0 ? 2 : 0) + (c.Agency.Length > 0 ? 1 : 0));
            return eligible.Where(c => (c.Kind.Length > 0 ? 2 : 0) + (c.Agency.Length > 0 ? 1 : 0) == score).ToList();
        }

        private void RemoveExpired() { _pending.RemoveAll(p => p.Expires <= _clock()); }
        private void StopLocked()
        {
            _pending.Clear();
            CancelOpeningLocked();
            StopCurrent();
        }

        private void CancelOpeningLocked()
        {
            if (!_playbackOpening && _openingPending == null && _openingClip == null)
                return;

            // A worker that is already opening a stream observes the generation
            // mismatch and disposes its result instead of attaching stale audio
            // to a new Police session.
            ++_playbackGeneration;
            _openingPending = null;
            _openingClip = null;
            _playbackOpening = false;
        }
        private void StopCurrent()
        {
            if (_current != null) _current.Cancelled = true;
            if (_player != null) _player.Stop();
        }

        private void WarnOnce(string key, string message)
        {
            // A bounded diagnostic set prevents missing libraries and bad
            // observations from flooding the two existing project logs.
            if (_warnings.Count < 256 && _warnings.Add(key)) _log.Debug("POLICE_AUDIO", message);
        }

        private static string Required(XElement node, string attribute)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
                throw new InvalidDataException("Missing/invalid " + attribute + " on " + node.Name);
            return value;
        }

        private static int Number(XElement node, string attribute, int fallback, int min, int max)
        {
            string text = (string)node.Attribute(attribute);
            if (text == null) return fallback;
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
                throw new InvalidDataException("Invalid " + attribute);
            return value;
        }

        private static bool ValidId(string value)
        {
            return value.StartsWith("lsimmersivelife.police.", StringComparison.Ordinal)
                && value.Length <= 160 && value.All(c => char.IsLower(c) || char.IsDigit(c) || c == '.' || c == '_');
        }

        private static void ValidateWave(string path)
        {
            // Inspect actual PCM data, not the extension alone. Supporting a
            // narrow first format prevents hidden codec and bit-depth assumptions.
            using (var reader = new WaveFileReader(path))
            {
                WaveFormat f = reader.WaveFormat;
                if (f.Encoding != WaveFormatEncoding.Pcm || f.BitsPerSample != 16
                    || f.Channels < 1 || f.Channels > 2 || f.SampleRate < 8000 || f.SampleRate > 48000
                    || reader.Length == 0 || reader.Length % f.BlockAlign != 0 || reader.TotalTime.TotalSeconds > 60)
                    throw new InvalidDataException("Expected nonempty 16-bit PCM WAV, 8-48 kHz, at most 60 seconds.");
            }
        }

        // Private cache/queue records contain audio data, never gameplay delegates.
        private sealed class AudioEvent
        {
            internal string Id, Speaker;
            internal LSPDAudioPriority Priority;
            internal int Cooldown, Expiry;
            internal readonly List<AudioClip> Clips = new List<AudioClip>();
        }
        private sealed class AudioClip
        {
            internal string Id, Path, Kind, Agency, Language;
            internal int Weight;
            internal bool Available;
        }
        private sealed class Pending
        {
            internal AudioEvent Event;
            internal string Scope, Kind, Agency;
            internal long Expires, Sequence;
            internal bool Cancelled;
        }
    }

    // Playback alone is replaceable in tests; this is not a second mod framework.
    internal interface ILSPDPlayback : IDisposable
    {
        bool Finished { get; }
        Exception Error { get; }
        void Stop();
        void SetVolume(float volume);
    }

    internal sealed class LSPDWavePlayback : ILSPDPlayback
    {
        private readonly object _sync = new object();
        private WaveOutEvent _output;
        private AudioFileReader _reader;
        private int _finished;
        private Exception _error;

        internal LSPDWavePlayback(string path, float volume)
        {
            // Callback execution must not depend on the game's/UI's synchronization
            // context. Callbacks release resources and signal completion only.
            SynchronizationContext context = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                _reader = new AudioFileReader(path) { Volume = volume };
                _output = new WaveOutEvent { DesiredLatency = 100 };
                _output.Init(_reader);
                _output.PlaybackStopped += OnStopped;
                _output.Play();
            }
            catch { Release(); throw; }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
        }

        public bool Finished { get { return Volatile.Read(ref _finished) != 0; } }
        public Exception Error { get { return _error; } }
        public void SetVolume(float volume)
        {
            lock (_sync) if (_reader != null) _reader.Volume = volume;
        }
        public void Stop()
        {
            // Stop requests completion; never dispose while background reading
            // is still active. NAudio releases in OnStopped before Finished is set.
            lock (_sync)
            {
                if (_output == null) return;
                try { _output.Stop(); }
                catch (Exception ex) { _error = ex; }
            }
        }
        private void OnStopped(object sender, StoppedEventArgs args)
        {
            _error = args.Exception ?? _error;
            Release();
        }
        private void Release()
        {
            lock (_sync)
            {
                try
                {
                    if (_output != null) { _output.PlaybackStopped -= OnStopped; _output.Dispose(); }
                }
                catch (Exception ex) { _error = ex; }
                finally
                {
                    _output = null;
                    try { if (_reader != null) _reader.Dispose(); }
                    catch (Exception ex) { _error = ex; }
                    finally
                    {
                        _reader = null;
                        Volatile.Write(ref _finished, 1);
                    }
                }
            }
        }
        public void Dispose() { Stop(); }
    }
}
