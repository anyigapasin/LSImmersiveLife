using System;

namespace LSImmersiveLife
{
    /// <summary>
    /// Tracks the officer''s explicit patrol shift and reports its radio state.
    /// PoliceCore owns the patrol vehicle, dispatch producer, response units,
    /// and other world systems that are allowed to run while this shift is on.
    /// </summary>
    internal sealed class LSPDPatrol
    {
        private readonly LSPDAudioDispatch _audio;
        private readonly LSPolicePatrolSettings _settings;
        private long _statusSequence;

        internal LSPDPatrol(LSPDAudioDispatch audio)
            : this(audio, null)
        {
        }

        internal LSPDPatrol(
            LSPDAudioDispatch audio,
            LSPolicePatrolSettings settings)
        {
            _audio = audio ?? throw new ArgumentNullException("audio");
            _settings = settings ?? LSPolicePatrolSettings.Default();
        }

        internal bool IsPatrolling { get; private set; }

        internal bool Start()
        {
            if (IsPatrolling)
                return false;
            if (!_settings.Enabled)
                return false;

            IsPatrolling = true;
            Report("lsimmersivelife.police.patrol.started", "started");
            Report("lsimmersivelife.police.patrol.available", "available");
            return true;
        }

        internal bool End()
        {
            if (!IsPatrolling)
                return false;

            IsPatrolling = false;
            Report("lsimmersivelife.police.patrol.ended", "ended");
            return true;
        }

        internal LSPDAudioResult ReportStatus(string stage, string occurrenceId)
        {
            string normalized = string.IsNullOrWhiteSpace(stage)
                ? "status"
                : stage.Trim().ToLowerInvariant();
            string eventId = normalized == "started"
                ? "lsimmersivelife.police.patrol.started"
                : normalized == "ended"
                    ? "lsimmersivelife.police.patrol.ended"
                    : normalized == "available"
                        ? "lsimmersivelife.police.patrol.available"
                        : normalized == "unavailable"
                            ? "lsimmersivelife.police.patrol.unavailable"
                            : normalized == "area_check"
                                ? "lsimmersivelife.police.patrol.area_check"
                                : "lsimmersivelife.police.patrol.status";
            string occurrence = string.IsNullOrWhiteSpace(occurrenceId)
                ? normalized + "-" + (++_statusSequence)
                : occurrenceId;
            // Patrol status is a confirmed fact owned by this class. The audio
            // service only plays the corresponding catalog entry.
            return _audio.Report(eventId, "police-patrol", occurrence, "patrol");
        }

        internal void Reset()
        {
            IsPatrolling = false;
        }

        private void Report(string eventId, string occurrence)
        {
            try { _audio.Report(eventId, "police-patrol", occurrence, "patrol"); } catch { }
        }
    }
}
