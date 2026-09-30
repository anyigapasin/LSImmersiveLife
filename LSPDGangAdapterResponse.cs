using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Owns the small local Gang & Turf/vanilla-gang incident layer. Gang
    /// activity is deliberately separate from Universal Dispatch: it reports
    /// a nearby threat, holds the world state while it is active, and resolves
    /// after the threat has actually ended. It never edits external gang data,
    /// creates a Dispatch callout, or deletes entities it did not create.
    /// </summary>
    internal sealed class LSPDGangAdapterResponse
    {
        private const float DetectionRadius = 75f;
        private const int ResolveHoldSeconds = 5;
        private const int CooldownSeconds = 45;

        private readonly LSPDAudioDispatch _audio;
        private readonly LSImmersiveLog _log;
        private readonly LSPDGangDataIntegration _gangData;
        private readonly LSPDVanillaGangResponse _vanilla;

        private Ped _threat;
        private readonly List<Ped> _activeThreats = new List<Ped>();
        private readonly List<Ped> _activeCombatThreats = new List<Ped>();
        private readonly List<Ped> _protectedGangActors = new List<Ped>();
        private readonly HashSet<int> _backupHeldThreatHandles = new HashSet<int>();
        private Blip _incidentBlip;
        private DateTime _lastAggressiveAt = DateTime.MinValue;
        private DateTime _backupZeroParticipantsAt = DateTime.MinValue;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private string _gangName = string.Empty;
        private string _territoryName = string.Empty;
        private string _status = "No active gang incident.";

        internal LSPDGangAdapterResponse(
            LSPDAudioDispatch audio,
            LSImmersiveLog log,
            LSPDGangDataIntegration gangData)
        {
            _audio = audio ?? throw new ArgumentNullException("audio");
            _log = log;
            _gangData = gangData;
            _vanilla = new LSPDVanillaGangResponse(log);
        }

        internal bool HasActiveIncident
        {
            get { return _threat != null; }
        }

        internal string GangName { get { return _gangName; } }
        internal string TerritoryName { get { return _territoryName; } }
        internal string StatusText { get { return _status; } }

        /// <summary>
        /// Returns only the actor currently owned by the active gang incident.
        /// The wider nearby scan sample is observation input, not gameplay
        /// ownership, and must never be treated as protected scene actors.
        /// </summary>
        internal Ped ActiveThreat
        {
            get
            {
                try
                {
                    return _threat != null && _threat.Exists() ? _threat : null;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// The currently observed hostile members of the active gang scene.
        /// This is a read-only observation boundary for Police Backup; Gang &amp;
        /// Turf remains the owner of the underlying actors.
        /// </summary>
        internal IEnumerable<Ped> ActiveThreats
        {
            get
            {
                var result = new List<Ped>();
                foreach (Ped ped in _activeThreats)
                    try
                    {
                        if (ped != null && ped.Exists() && !ped.IsDead)
                            result.Add(ped);
                    }
                    catch
                    {
                    }
                return result;
            }
        }

        internal int ThreatCount
        {
            get { return ActiveThreats.Count(); }
        }

        /// <summary>
        /// The subset of tracked gang actors that were actually involved in
        /// the confrontation. Nearby same-faction observation members remain
        /// in ActiveThreats for scene context, but Police Backup should prefer
        /// this smaller set for tactical target selection.
        /// </summary>
        internal IEnumerable<Ped> ActiveCombatThreats
        {
            get
            {
                var result = new List<Ped>();
                foreach (Ped ped in _activeCombatThreats)
                    try
                    {
                        if (ped != null && ped.Exists() && !ped.IsDead)
                            result.Add(ped);
                    }
                    catch
                    {
                    }
                return result;
            }
        }

        /// <summary>
        /// Gang actors observed in the same local sample that are classified
        /// as player-owned/allied by the existing Gang &amp; Turf integration.
        /// This is a narrow protection boundary used to repair an already
        /// misdirected GTA combat task; it does not claim or control them.
        /// </summary>
        internal IEnumerable<Ped> ProtectedGangActors
        {
            get
            {
                var result = new List<Ped>();
                foreach (Ped ped in _protectedGangActors)
                    try
                    {
                        if (ped != null && ped.Exists() && !ped.IsDead)
                            result.Add(ped);
                    }
                    catch
                    {
                    }
                return result;
            }
        }

        internal bool IsProtectedGangActor(Ped ped)
        {
            return _vanilla.IsProtectedActor(ped, _gangData);
        }

        internal Vector3 ThreatPosition
        {
            get
            {
                try
                {
                    return _threat != null && _threat.Exists()
                        ? _threat.Position : Vector3.Zero;
                }
                catch { return Vector3.Zero; }
            }
        }

        internal void Process(
            bool isPatrolling,
            bool dispatchActive,
            bool npcInteractionActive,
            Ped player,
            Ped[] nearby)
        {
            Process(
                isPatrolling,
                dispatchActive,
                npcInteractionActive,
                player,
                nearby,
                false);
        }

        internal void Process(
            bool isPatrolling,
            bool dispatchActive,
            bool npcInteractionActive,
            Ped player,
            Ped[] nearby,
            bool gangBackupActive)
        {
            DateTime now = DateTime.UtcNow;
            if (!isPatrolling || player == null || !player.Exists())
            {
                if (!isPatrolling)
                    Reset();
                return;
            }

            Ped found;
            string identity;
            bool aggressive = _vanilla.TryFindAttacker(
                player,
                nearby,
                _gangData,
                out found,
                out identity);

            if (_threat == null)
            {
                // A universal callout or a civilian contact owns the player's
                // attention. Gang activity remains observable and can be
                // reported on the next patrol scan, but never overlaps it.
                if (!dispatchActive && !npcInteractionActive
                    && !gangBackupActive && !aggressive)
                    _vanilla.ProcessPoliceAwareness(
                        player, nearby, _gangData, now);
                if (dispatchActive || npcInteractionActive || !aggressive)
                    return;
                Start(found, identity, player, now);
                RefreshActiveThreats(player, nearby, now, gangBackupActive);
                return;
            }

            if (aggressive && found != null)
            {
                _threat = found;
                _lastAggressiveAt = now;
            }

            RefreshActiveThreats(player, nearby, now, gangBackupActive);
            UpdateBlip();
            _status = "Gang threat active: " + _gangName
                + (_territoryName.Length == 0 ? string.Empty
                    : " | Turf: " + _territoryName);

            // Active maintenance is intentionally frequent. A resolved gang
            // threat is not delayed by the new-incident scan cooldown.
            if (gangBackupActive)
            {
                // ActiveThreats also contains nearby same-faction context.
                // Only confirmed combat participants keep the Backup
                // engagement alive; passive bystanders must not hold Police
                // Backup forever.
                if (_activeCombatThreats.Count > 0)
                {
                    _backupZeroParticipantsAt = DateTime.MinValue;
                    return;
                }
                if (_backupZeroParticipantsAt == DateTime.MinValue)
                    _backupZeroParticipantsAt = now;
                if (now < _backupZeroParticipantsAt.AddSeconds(15))
                    return;
            }
            else
                _backupZeroParticipantsAt = DateTime.MinValue;

            if (now < _lastAggressiveAt.AddSeconds(ResolveHoldSeconds))
                return;
            Resolve(now);
        }

        internal void Reset()
        {
            CleanupBlip();
            _threat = null;
            _activeThreats.Clear();
            ReleaseBackupEngagementTasks();
            _activeCombatThreats.Clear();
            _protectedGangActors.Clear();
            _backupHeldThreatHandles.Clear();
            _vanilla.ResetPoliceAwareness();
            _gangName = string.Empty;
            _territoryName = string.Empty;
            _status = "No active gang incident.";
            _lastAggressiveAt = DateTime.MinValue;
            _backupZeroParticipantsAt = DateTime.MinValue;
            _cooldownUntil = DateTime.MinValue;
        }

        private void Start(
            Ped threat,
            string identity,
            Ped player,
            DateTime now)
        {
            if (threat == null || now < _cooldownUntil)
                return;
            _threat = threat;
            _gangName = string.IsNullOrWhiteSpace(identity)
                ? "Unidentified gang"
                : identity;
            LSPDTurfZoneDefinition turf = _gangData == null
                ? null : _gangData.FindNearbyTurf(player.Position);
            _territoryName = turf == null
                ? string.Empty : turf.Name + " / " + turf.OwnerGangName;
            _lastAggressiveAt = now;
            _status = "Gang threat active: " + _gangName
                + (_territoryName.Length == 0 ? string.Empty
                    : " | Turf: " + _territoryName);
            try
            {
                _incidentBlip = World.CreateBlip(threat.Position);
                if (_incidentBlip != null && _incidentBlip.Exists())
                {
                    _incidentBlip.Name = "Gang Incident";
                    _incidentBlip.Sprite = BlipSprite.GangPolice;
                    _incidentBlip.Color = BlipColor.Red;
                    _incidentBlip.IsShortRange = false;
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_GANG_INCIDENT_BLIP_FAILED", ex);
            }

            string scope = "gang-runtime";
            string occurrence = now.Ticks.ToString();
            _audio.Report(
                "lsimmersivelife.police.gang.attack_on_police",
                scope,
                occurrence,
                "gang");
            Notify(
                "~r~POLICE GANG ALERT~s~\n" + _gangName
                + "\n~c~Local threat detected. Resolve the situation; it is not a prison dispatch.");
            LogRuntime(
                "POLICE_GANG_INCIDENT_STARTED",
                "Identity=" + _gangName + "; Turf=" + _territoryName
                + "; Ped=" + threat.Handle);
        }

        private void RefreshActiveThreats(
            Ped player,
            Ped[] nearby,
            DateTime now,
            bool gangBackupActive)
        {
            int previousCount = _activeThreats.Count;
            List<Ped> observedAttackers = _vanilla.FindActiveAttackers(
                player,
                nearby,
                _gangData);
            HashSet<int> observedAttackerHandles = new HashSet<int>(
                observedAttackers
                    .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                    .Select(ped => ped.Handle));
            foreach (Ped attacker in observedAttackers)
            {
                if (attacker == null || !attacker.Exists() || attacker.IsDead
                    || _activeCombatThreats.Any(existing => existing != null
                        && existing.Exists() && existing.Handle == attacker.Handle))
                    continue;
                _activeCombatThreats.Add(attacker);
            }

            List<Ped> observed = new List<Ped>(observedAttackers);
            foreach (Ped ped in nearby ?? new Ped[0])
            {
                if (ped == null || !ped.Exists() || ped.IsDead
                    || !IsProtectedGangActor(ped))
                    continue;
                if (!_protectedGangActors.Any(existing => existing != null
                    && existing.Exists() && existing.Handle == ped.Handle))
                    _protectedGangActors.Add(ped);
            }
            foreach (Ped ped in _protectedGangActors.ToArray())
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead
                        || ped.Position.DistanceTo(player.Position) > DetectionRadius + 25f)
                        _protectedGangActors.Remove(ped);
                }
                catch
                {
                    _protectedGangActors.Remove(ped);
                }
            if (_threat != null && _threat.Exists())
                observed.AddRange(_vanilla.FindNearbyGangGroup(
                    _threat,
                    nearby,
                    _gangData));
            foreach (Ped ped in observed)
            {
                if (ped == null || !ped.Exists()
                    || _activeThreats.Any(existing => existing != null
                        && existing.Exists() && existing.Handle == ped.Handle))
                    continue;
                _activeThreats.Add(ped);
            }

            bool gangCombatStillActive = false;
            foreach (Ped ped in _activeThreats.ToArray())
            {
                bool keep;
                try
                {
                    keep = ped != null && ped.Exists() && !ped.IsDead;
                    if (keep && (ped.IsInCombat || ped.IsShooting))
                        gangCombatStillActive = true;
                }
                catch
                {
                    keep = false;
                }
                if (!keep)
                    _activeThreats.Remove(ped);
            }

            foreach (Ped ped in _activeCombatThreats.ToArray())
            {
                bool keep;
                try
                {
                    keep = ped != null && ped.Exists() && !ped.IsDead;
                    if (keep && !gangBackupActive
                        && !observedAttackerHandles.Contains(ped.Handle)
                        && !ped.IsInCombat && !ped.IsShooting)
                        keep = false;
                }
                catch
                {
                    keep = false;
                }
                if (!keep)
                {
                    _activeCombatThreats.Remove(ped);
                    _backupHeldThreatHandles.Remove(ped == null ? 0 : ped.Handle);
                }
            }

            if (gangCombatStillActive)
                _lastAggressiveAt = now;

            Ped liveThreat = ActiveThreats.FirstOrDefault();
            if (liveThreat != null)
                _threat = liveThreat;

            if (previousCount != _activeThreats.Count)
            {
                LogRuntime(
                    "POLICE_GANG_THREATS_TRACKED",
                    "Identity=" + _gangName
                    + "; Members=" + _activeThreats.Count
                    + "; CombatActive=" + gangCombatStillActive
                    + "; ActiveCombatMembers=" + _activeCombatThreats.Count);
            }
        }

        /// <summary>
        /// Keeps only confirmed combat participants in the active firefight
        /// after the player has explicitly requested Gang Backup. This is a
        /// temporary engagement hold: it never claims or deletes external gang
        /// actors and is released when the incident resolves.
        /// </summary>
        internal void MaintainBackupEngagement(Ped player)
        {
            if (player == null || !player.Exists())
                return;
            foreach (Ped ped in _activeCombatThreats.ToArray())
            {
                if (ped == null || !ped.Exists() || ped.IsDead)
                    continue;
                try
                {
                    if (_backupHeldThreatHandles.Add(ped.Handle))
                        Function.Call(Hash.SET_PED_KEEP_TASK, ped, true);

                    // Vanilla police arrival can replace a gang member's
                    // combat task with flee/hide. Only the actors that were
                    // already confirmed as attackers are brought back into the
                    // active confrontation, never the wider nearby group.
                    if (Function.Call<bool>(Hash.IS_PED_FLEEING, ped)
                        && !ped.IsInCombatAgainst(player))
                    {
                        Function.Call(Hash.CLEAR_PED_TASKS, ped);
                        ped.Task.Combat(player);
                    }
                }
                catch (Exception ex)
                {
                    LogException("POLICE_GANG_BACKUP_ENGAGEMENT_HOLD_FAILED", ex);
                }
            }
        }

        private void ReleaseBackupEngagementTasks()
        {
            foreach (Ped ped in _activeCombatThreats.ToArray())
            {
                if (ped == null || !ped.Exists())
                    continue;
                try { Function.Call(Hash.SET_PED_KEEP_TASK, ped, false); }
                catch { }
            }
        }

        private void Resolve(DateTime now)
        {
            string identity = _gangName;
            CleanupBlip();
            ReleaseBackupEngagementTasks();
            _threat = null;
            _activeThreats.Clear();
            _activeCombatThreats.Clear();
            _protectedGangActors.Clear();
            _backupHeldThreatHandles.Clear();
            _cooldownUntil = now.AddSeconds(CooldownSeconds);
            _lastAggressiveAt = DateTime.MinValue;
            _backupZeroParticipantsAt = DateTime.MinValue;
            _status = "Gang incident contained. Patrol remains available.";
            _audio.Report(
                "lsimmersivelife.police.gang.resolved",
                "gang-runtime",
                "resolved-" + now.Ticks,
                "gang");
            Notify("~g~POLICE GANG INCIDENT~s~\nThreat contained: " + identity + ".");
            LogRuntime("POLICE_GANG_INCIDENT_RESOLVED", "Identity=" + identity);
        }

        private void UpdateBlip()
        {
            try
            {
                if (_incidentBlip != null && _incidentBlip.Exists()
                    && _threat != null && _threat.Exists())
                    _incidentBlip.Position = _threat.Position;
            }
            catch
            {
            }
        }

        private void CleanupBlip()
        {
            try
            {
                if (_incidentBlip != null && _incidentBlip.Exists())
                    _incidentBlip.Delete();
            }
            catch
            {
            }
            _incidentBlip = null;
        }

        private static void Notify(string message)
        {
            try { Notification.PostTicker(message, false, false); } catch { }
        }

        private void LogRuntime(string category, string message)
        {
            if (_log != null)
                _log.Runtime(category, message);
        }

        private void LogException(string category, Exception ex)
        {
            if (_log != null)
                _log.Exception(category, ex);
        }
    }
}
