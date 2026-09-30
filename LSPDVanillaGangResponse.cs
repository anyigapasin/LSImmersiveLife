using GTA;
using GTA.Math;
using GTA.Native;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Identifies vanilla GTA gang actors for the Police-only local response
    /// layer. It does not change relationships or claim unrelated gang peds.
    /// </summary>
    internal sealed class LSPDVanillaGangResponse
    {
        private readonly LSImmersiveLog _log;
        private static readonly string[] VanillaGangModels =
        {
            "g_m_y_ballaeast_01", "g_m_y_ballaorig_01", "g_m_y_ballasout_01",
            "g_m_y_famca_01", "g_m_y_famdnf_01", "g_m_y_famfor_01",
            "g_m_y_mexgang_01", "g_m_y_mexgoon_01", "g_m_y_mexgoon_02",
            "g_m_y_lost_01", "g_m_y_lost_02", "g_m_y_lost_03",
            "g_m_y_vagos_01", "g_m_y_salvaboss_01", "g_m_y_salvagoon_01"
        };

        // These are the companion models used by Anyiii's Gang. They remain
        // friendly even when Gang & Turf data is available to Police Authority.
        private static readonly HashSet<int> AnyiiiCompanionHashes =
            new HashSet<int>
            {
                343272203, 1957851257, -314526266, 834197053,
                -1205420430, 1823612999, -514572009
            };

        private enum PoliceAwarenessMode
        {
            Stealth,
            Wary,
            AttackReady
        }

        private sealed class PoliceAwarenessState
        {
            internal PoliceAwarenessMode Mode;
            internal DateTime NextTaskAt;
            internal bool AttackReadyEscalationEligible;
            internal bool AttackReadyAimIssued;
            internal bool AttackReadyEscalationIssued;
            internal bool AttackReadyLogged;
            internal DateTime AttackReadyNearSince = DateTime.MinValue;
        }

        private readonly Dictionary<int, PoliceAwarenessState> _policeAwareness =
            new Dictionary<int, PoliceAwarenessState>();

        internal LSPDVanillaGangResponse(LSImmersiveLog log)
        {
            _log = log;
        }

        internal bool IsVanillaGangMember(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return false;
            try { return IsVanillaGangHash(ped.Model.Hash); }
            catch { return false; }
        }

        internal static bool IsFriendlyAnyiii(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return false;
            try { return AnyiiiCompanionHashes.Contains(ped.Model.Hash); }
            catch { return false; }
        }

        internal bool IsProtectedActor(
            Ped ped,
            LSPDGangDataIntegration gangData)
        {
            return IsFriendlyAnyiii(ped, gangData);
        }

        internal void ResetPoliceAwareness()
        {
            _policeAwareness.Clear();
        }

        /// <summary>
        /// Adds a small, local Police-mode awareness layer to recognized gang
        /// members without replacing their normal GTA behavior. It only runs
        /// during a quiet patrol approach, chooses a bounded variation per ped,
        /// and never turns an ambient gang member into a combatant by itself.
        /// </summary>
        internal void ProcessPoliceAwareness(
            Ped player,
            IEnumerable<Ped> nearby,
            LSPDGangDataIntegration gangData,
            DateTime now)
        {
            if (player == null || !player.Exists() || nearby == null)
                return;

            HashSet<int> observedHandles = new HashSet<int>();
            foreach (Ped ped in nearby)
            {
                if (!IsRecognizedGangActor(ped, gangData)
                    || ped.Handle == player.Handle)
                    continue;
                try
                {
                    if (ped.IsDead || ped.IsInVehicle() || ped.IsInCombat || ped.IsShooting)
                        continue;
                    float distance = ped.Position.DistanceTo(player.Position);
                    if (distance < 10f || distance > 55f)
                        continue;
                    observedHandles.Add(ped.Handle);

                    PoliceAwarenessState state;
                    if (!_policeAwareness.TryGetValue(ped.Handle, out state))
                    {
                        uint seed = unchecked((uint)(ped.Handle * 1103515245 + 12345));
                        int bucket = (int)(seed % 100u);
                        state = new PoliceAwarenessState
                        {
                            Mode = bucket < 55
                                ? PoliceAwarenessMode.Stealth
                                : bucket < 88
                                    ? PoliceAwarenessMode.Wary
                                    : PoliceAwarenessMode.AttackReady,
                            // AttackReady remains a minority state, and only
                            // a bounded subset of those armed peds may
                            // escalate after lingering near the officer.
                            AttackReadyEscalationEligible = bucket >= 88
                                && (seed % 3u == 0u),
                            NextTaskAt = DateTime.MinValue
                        };
                        _policeAwareness[ped.Handle] = state;
                    }
                    if (now < state.NextTaskAt)
                        continue;
                    state.NextTaskAt = now.AddSeconds(
                        state.Mode == PoliceAwarenessMode.AttackReady ? 2 : 10);

                    if (state.Mode == PoliceAwarenessMode.AttackReady)
                    {
                        if (state.AttackReadyEscalationEligible
                            && distance <= 35f)
                        {
                            if (state.AttackReadyNearSince == DateTime.MinValue)
                                state.AttackReadyNearSince = now;
                            if (!state.AttackReadyAimIssued)
                            {
                                Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY,
                                    ped, player, 2200, true);
                                state.AttackReadyAimIssued = true;
                                LogRuntime(
                                    "POLICE_GANG_AWARENESS_ESCALATION_AIMED",
                                    "Ped=" + ped.Handle
                                    + "; Distance=" + distance.ToString("0.0")
                                    + "; Behavior=LimitedAttackReadySubset");
                            }

                            if (!state.AttackReadyEscalationIssued
                                && now >= state.AttackReadyNearSince.AddSeconds(4)
                                && IsArmed(ped))
                            {
                                Function.Call(Hash.CLEAR_PED_TASKS, ped);
                                ped.Task.Combat(player);
                                state.AttackReadyEscalationIssued = true;
                                LogRuntime(
                                    "POLICE_GANG_AWARENESS_ESCALATED",
                                    "Ped=" + ped.Handle
                                    + "; Distance=" + distance.ToString("0.0")
                                    + "; Behavior=LimitedArmedResistance");
                            }
                        }
                        if (!state.AttackReadyLogged)
                        {
                            state.AttackReadyLogged = true;
                            LogRuntime(
                                "POLICE_GANG_AWARENESS_ATTACK_READY",
                                "Ped=" + ped.Handle
                                + "; Distance=" + distance.ToString("0.0")
                                + "; Behavior=AmbientAIMayEscalate");
                        }
                        continue;
                    }

                    if (state.Mode == PoliceAwarenessMode.Stealth)
                    {
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            ped, player, 1200);
                        Function.Call(Hash.TASK_WANDER_STANDARD,
                            ped, 5.0f, 10);
                        LogRuntime(
                            "POLICE_GANG_AWARENESS_STEALTH",
                            "Ped=" + ped.Handle
                            + "; Distance=" + distance.ToString("0.0")
                            + "; Behavior=TurnAwayAndContinue");
                        continue;
                    }

                    Vector3 away = ped.Position - player.Position;
                    float magnitude = (float)Math.Sqrt(
                        away.X * away.X + away.Y * away.Y);
                    if (magnitude < 0.1f)
                        away = new Vector3(0f, 1f, 0f);
                    else
                        away = new Vector3(away.X / magnitude, away.Y / magnitude, 0f);
                    Vector3 destination = ped.Position + away * 18f;
                    Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                        ped,
                        destination.X,
                        destination.Y,
                        destination.Z,
                        1.35f,
                        -1,
                        1.0f,
                        1,
                        0f);
                    LogRuntime(
                        "POLICE_GANG_AWARENESS_WARY",
                        "Ped=" + ped.Handle
                        + "; Distance=" + distance.ToString("0.0")
                        + "; Behavior=WalkAwayAndKeepDistance");
                }
                catch (Exception ex)
                {
                    LogException("POLICE_GANG_AWARENESS_TASK_FAILED", ex);
                }
            }

            foreach (int handle in _policeAwareness.Keys.ToArray())
                if (!observedHandles.Contains(handle))
                    _policeAwareness.Remove(handle);
        }

        internal bool TryFindAttacker(
            Ped player,
            IEnumerable<Ped> nearby,
            LSPDGangDataIntegration gangData,
            out Ped attacker,
            out string identity)
        {
            attacker = null;
            identity = string.Empty;
            if (player == null || !player.Exists() || nearby == null)
                return false;

            // Friendly Anyiii companions are valid protected targets. Keep the
            // list local to this scan so the response owner never stores a
            // handle that may stream out between ticks.
            var friendlyAllies = new List<Ped>();
            foreach (Ped candidate in nearby)
                if (candidate != null && candidate.Exists()
                    && candidate.Handle != player.Handle
                    && IsFriendlyAnyiii(candidate, gangData))
                    friendlyAllies.Add(candidate);

            float nearest = float.MaxValue;
            foreach (Ped ped in nearby)
            {
                if (ped == null || !ped.Exists() || ped.Handle == player.Handle)
                    continue;
                try
                {
                    if (ped.IsDead || IsFriendlyAnyiii(ped, gangData))
                        continue;

                    int hash = ped.Model.Hash;
                    LSPDGangDefinition gang = gangData == null
                        ? null : gangData.FindGangForModel(hash);
                    bool playerOwned = gang != null
                        && (gang.IsPlayerOwned || gang.CreatedByPlayer);
                    if (playerOwned)
                        continue;

                    bool external = gang != null && !playerOwned;
                    // A model assigned to a player-owned Gang & Turf profile is
                    // never reclassified as a vanilla hostile gang member.
                    bool vanilla = IsVanillaGangHash(hash) && gang == null;
                    if (!external && !vanilla
                        && (gangData == null || !gangData.IsMemberPoolModel(hash)))
                        continue;

                    Ped target = IsAggressive(ped, player) ? player : null;
                    if (target == null)
                        foreach (Ped ally in friendlyAllies)
                            if (IsAggressive(ped, ally))
                            {
                                target = ally;
                                break;
                            }
                    if (target == null)
                        continue;

                    float distance = ped.Position.DistanceTo(player.Position);
                    if (distance >= nearest)
                        continue;
                    nearest = distance;
                    attacker = ped;
                    string baseIdentity = gang == null
                        ? (vanilla ? "Vanilla GTA gang" : "Gang & Turf member pool")
                        : gang.Name;
                    identity = target.Handle == player.Handle
                        ? baseIdentity
                        : baseIdentity + " (attacking Anyiii ally)";
                }
                catch
                {
                    // A streamed-out or deleted ped is simply skipped. The
                    // response owner never keeps an invalid world handle.
                }
            }
            return attacker != null;
        }

        /// <summary>
        /// Returns the currently active hostile gang members in the same
        /// observation sample used to start the incident.  The old gang
        /// response deliberately selected only the nearest attacker, which
        /// was enough to raise an alert but left a responding Police unit with
        /// no authoritative group to fight.  This remains observation-only:
        /// it does not change Gang &amp; Turf ownership or claim the peds.
        /// </summary>
        internal List<Ped> FindActiveAttackers(
            Ped player,
            IEnumerable<Ped> nearby,
            LSPDGangDataIntegration gangData)
        {
            var attackers = new List<Ped>();
            if (player == null || !player.Exists() || nearby == null)
                return attackers;

            var friendlyAllies = new List<Ped>();
            foreach (Ped candidate in nearby)
            {
                if (candidate == null || !candidate.Exists()
                    || candidate.Handle == player.Handle)
                    continue;
                if (IsFriendlyAnyiii(candidate, gangData))
                    friendlyAllies.Add(candidate);
            }

            foreach (Ped ped in nearby)
            {
                if (ped == null || !ped.Exists() || ped.IsDead
                    || ped.Handle == player.Handle)
                    continue;
                try
                {
                    if (IsFriendlyAnyiii(ped, gangData))
                        continue;

                    int hash = ped.Model.Hash;
                    LSPDGangDefinition gang = gangData == null
                        ? null : gangData.FindGangForModel(hash);
                    bool playerOwned = gang != null
                        && (gang.IsPlayerOwned || gang.CreatedByPlayer);
                    if (playerOwned)
                        continue;

                    bool external = gang != null && !playerOwned;
                    bool vanilla = IsVanillaGangHash(hash) && gang == null;
                    if (!external && !vanilla
                        && (gangData == null || !gangData.IsMemberPoolModel(hash)))
                        continue;

                    bool hostile = IsAggressive(ped, player);
                    if (!hostile)
                    {
                        foreach (Ped ally in friendlyAllies)
                            if (IsAggressive(ped, ally))
                            {
                                hostile = true;
                                break;
                            }
                    }
                    if (hostile)
                        attackers.Add(ped);
                }
                catch
                {
                    // Streamed-out actors are observation misses, not a
                    // reason to break the active gang response.
                }
            }

            return attackers;
        }

        /// <summary>
        /// Expands an already-confirmed attack into the nearby members of the
        /// same faction. This is used only after an incident has started, so a
        /// nearby gang member is not treated as hostile during ordinary patrol
        /// observation.
        /// </summary>
        internal List<Ped> FindNearbyGangGroup(
            Ped anchor,
            IEnumerable<Ped> nearby,
            LSPDGangDataIntegration gangData)
        {
            var group = new List<Ped>();
            if (anchor == null || !anchor.Exists() || nearby == null)
                return group;

            LSPDGangDefinition anchorGang = null;
            int anchorHash = 0;
            try
            {
                anchorHash = anchor.Model.Hash;
                anchorGang = gangData == null
                    ? null : gangData.FindGangForModel(anchorHash);
            }
            catch
            {
                return group;
            }

            foreach (Ped candidate in nearby)
            {
                if (candidate == null || !candidate.Exists() || candidate.IsDead
                    || IsFriendlyAnyiii(candidate, gangData))
                    continue;
                try
                {
                    if (candidate.Position.DistanceTo(anchor.Position) > 85f)
                        continue;
                    LSPDGangDefinition candidateGang = gangData == null
                        ? null : gangData.FindGangForModel(candidate.Model.Hash);
                    bool candidatePlayerOwned = candidateGang != null
                        && (candidateGang.IsPlayerOwned || candidateGang.CreatedByPlayer);
                    if (candidatePlayerOwned)
                        continue;

                    bool sameExternalGang = anchorGang != null
                        && candidateGang != null
                        && string.Equals(anchorGang.Name, candidateGang.Name,
                            StringComparison.OrdinalIgnoreCase);
                    bool sameVanillaFaction = anchorGang == null
                        && candidateGang == null
                        && VanillaFaction(anchorHash) != 0
                        && VanillaFaction(anchorHash) == VanillaFaction(candidate.Model.Hash);
                    bool sameUnassignedMemberPool = anchorGang == null
                        && candidateGang == null
                        && gangData != null
                        && gangData.IsMemberPoolModel(anchorHash)
                        && gangData.IsMemberPoolModel(candidate.Model.Hash);
                    if (sameExternalGang || sameVanillaFaction || sameUnassignedMemberPool)
                        group.Add(candidate);
                }
                catch
                {
                }
            }

            if (!group.Any(ped => ped != null && ped.Exists()
                && ped.Handle == anchor.Handle))
                group.Add(anchor);
            return group;
        }

        private static bool IsFriendlyAnyiii(Ped ped, LSPDGangDataIntegration gangData)
        {
            if (IsFriendlyAnyiii(ped))
                return true;
            if (gangData == null || ped == null || !ped.Exists())
                return false;
            try
            {
                LSPDGangDefinition gang = gangData.FindGangForModel(ped.Model.Hash);
                return gang != null && (gang.IsPlayerOwned || gang.CreatedByPlayer);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRecognizedGangActor(
            Ped ped,
            LSPDGangDataIntegration gangData)
        {
            if (ped == null || !ped.Exists())
                return false;
            try
            {
                if (IsFriendlyAnyiii(ped, gangData))
                    return false;
                LSPDGangDefinition gang = gangData == null
                    ? null : gangData.FindGangForModel(ped.Model.Hash);
                bool playerOwned = gang != null
                    && (gang.IsPlayerOwned || gang.CreatedByPlayer);
                if (playerOwned)
                    return false;
                return (gang != null && !playerOwned)
                    || (IsVanillaGangHash(ped.Model.Hash) && gang == null)
                    || (gangData != null && gangData.IsMemberPoolModel(ped.Model.Hash));
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsVanillaGangHash(int hash)
        {
            foreach (string model in VanillaGangModels)
                if (unchecked((int)StringHash.AtStringHash(model, 0)) == hash)
                    return true;
            return false;
        }

        private static int VanillaFaction(int hash)
        {
            if (Matches(hash, "g_m_y_ballaeast_01")
                || Matches(hash, "g_m_y_ballaorig_01")
                || Matches(hash, "g_m_y_ballasout_01"))
                return 1;
            if (Matches(hash, "g_m_y_famca_01")
                || Matches(hash, "g_m_y_famdnf_01")
                || Matches(hash, "g_m_y_famfor_01"))
                return 2;
            if (Matches(hash, "g_m_y_mexgang_01")
                || Matches(hash, "g_m_y_mexgoon_01")
                || Matches(hash, "g_m_y_mexgoon_02"))
                return 3;
            if (Matches(hash, "g_m_y_lost_01")
                || Matches(hash, "g_m_y_lost_02")
                || Matches(hash, "g_m_y_lost_03"))
                return 4;
            if (Matches(hash, "g_m_y_vagos_01"))
                return 5;
            if (Matches(hash, "g_m_y_salvaboss_01")
                || Matches(hash, "g_m_y_salvagoon_01"))
                return 6;
            return 0;
        }

        private static bool Matches(int hash, string modelName)
        {
            return unchecked((int)StringHash.AtStringHash(modelName, 0)) == hash;
        }

        private static bool IsAggressive(Ped ped, Ped target)
        {
            if (ped == null || target == null || !ped.Exists() || !target.Exists())
                return false;
            try
            {
                return ped.IsInCombatAgainst(target)
                    || (ped.IsShooting
                        && ped.Position.DistanceTo(target.Position) <= 90f);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsArmed(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return false;
            try { return ped.Weapons.Current.Hash != WeaponHash.Unarmed; }
            catch { return false; }
        }

        private void LogRuntime(string category, string message)
        {
            // This awareness pass is intentionally event-based. It does not
            // write a per-frame trace while a gang member remains nearby.
            // The adapter's shared logger handles the actual runtime sink.
            // The method is kept local so the response remains an observer.
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
