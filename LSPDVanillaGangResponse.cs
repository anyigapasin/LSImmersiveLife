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
        private const int PlayerVehicleTaskProbeMilliseconds = 500;
        private const int MaximumPlayerVehicleTaskProbePeds = 64;
        private const int MaximumRememberedPlayerVehicleTaskPairs = 128;
        private const int PlayerVehiclePostExitObservationSeconds = 5;
        private const float PlayerVehicleTaskProbeRadius = 24f;
        private readonly LSImmersiveLog _log;
        private readonly HashSet<long> _reportedPlayerVehicleTaskPairs =
            new HashSet<long>();
        private readonly Queue<long> _reportedPlayerVehicleTaskOrder =
            new Queue<long>();
        private DateTime _nextPlayerVehicleTaskProbeAt = DateTime.MinValue;
        private Vehicle _lastObservedPlayerVehicle;
        private DateTime _lastObservedPlayerVehicleAt = DateTime.MinValue;
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

        internal void ResetObservationState()
        {
            _reportedPlayerVehicleTaskPairs.Clear();
            _reportedPlayerVehicleTaskOrder.Clear();
            _nextPlayerVehicleTaskProbeAt = DateTime.MinValue;
            _lastObservedPlayerVehicle = null;
            _lastObservedPlayerVehicleAt = DateTime.MinValue;
        }

        /// <summary>
        /// Captures a bounded one-time sample when a nearby pedestrian tries to
        /// enter or jack the Player's current vehicle, or attacks its occupant.
        /// This is evidence-only: it never issues a task or claims the Ped.
        /// </summary>
        internal void ObservePlayerVehicleTaskAttempts(
            Ped player,
            IEnumerable<Ped> nearby,
            LSPDGangDataIntegration gangData,
            DateTime now)
        {
            if (player == null || !player.Exists() || nearby == null
                || now < _nextPlayerVehicleTaskProbeAt)
                return;

            Vehicle vehicle;
            bool playerCurrentlyInVehicle;
            try
            {
                playerCurrentlyInVehicle = player.IsInVehicle();
                if (playerCurrentlyInVehicle)
                {
                    vehicle = player.CurrentVehicle;
                    if (vehicle == null || !vehicle.Exists())
                        return;
                    _lastObservedPlayerVehicle = vehicle;
                    _lastObservedPlayerVehicleAt = now;
                }
                else if (_lastObservedPlayerVehicle != null
                    && now < _lastObservedPlayerVehicleAt.AddSeconds(
                        PlayerVehiclePostExitObservationSeconds)
                    && _lastObservedPlayerVehicle.Exists())
                {
                    vehicle = _lastObservedPlayerVehicle;
                }
                else
                {
                    _lastObservedPlayerVehicle = null;
                    _lastObservedPlayerVehicleAt = DateTime.MinValue;
                    return;
                }
            }
            catch
            {
                return;
            }
            _nextPlayerVehicleTaskProbeAt = now.AddMilliseconds(
                PlayerVehicleTaskProbeMilliseconds);
            bool playerIsBeingJacked = false;
            try
            {
                playerIsBeingJacked = Function.Call<bool>(
                    Hash.IS_PED_BEING_JACKED, player);
            }
            catch { }

            int inspected = 0;
            foreach (Ped ped in nearby)
            {
                if (inspected >= MaximumPlayerVehicleTaskProbePeds)
                    break;
                inspected++;
                if (ped == null || !ped.Exists() || ped.Handle == player.Handle)
                    continue;
                try
                {
                    if (ped.IsDead
                        || ped.Position.DistanceTo(vehicle.Position)
                            > PlayerVehicleTaskProbeRadius)
                        continue;
                    int enteringVehicle = 0;
                    int enteringSeat = -2;
                    bool entryProbeAvailable = false;
                    try
                    {
                        enteringVehicle = Function.Call<int>(
                            Hash.GET_VEHICLE_PED_IS_TRYING_TO_ENTER, ped);
                        entryProbeAvailable = true;
                    }
                    catch { }
                    if (enteringVehicle != 0)
                    {
                        try
                        {
                            enteringSeat = Function.Call<int>(
                                Hash.GET_SEAT_PED_IS_TRYING_TO_ENTER, ped);
                        }
                        catch { }
                    }

                    bool isJacking = false;
                    try { isJacking = Function.Call<bool>(Hash.IS_PED_JACKING, ped); }
                    catch { }

                    bool combatAgainstPlayer = false;
                    try { combatAgainstPlayer = ped.IsInCombatAgainst(player); }
                    catch { }

                    bool tryingToEnterPlayerVehicle = entryProbeAvailable
                        && enteringVehicle == vehicle.Handle;
                    bool jackingPlayer = isJacking && playerIsBeingJacked;
                    if (!tryingToEnterPlayerVehicle && !jackingPlayer
                        && !combatAgainstPlayer)
                        continue;

                    long pairKey = ((long)ped.Handle << 32)
                        ^ (uint)vehicle.Handle;
                    if (!_reportedPlayerVehicleTaskPairs.Add(pairKey))
                        continue;
                    _reportedPlayerVehicleTaskOrder.Enqueue(pairKey);
                    while (_reportedPlayerVehicleTaskOrder.Count
                        > MaximumRememberedPlayerVehicleTaskPairs)
                    {
                        long expiredKey = _reportedPlayerVehicleTaskOrder.Dequeue();
                        _reportedPlayerVehicleTaskPairs.Remove(expiredKey);
                    }

                    int modelHash = 0;
                    int relationshipGroup = 0;
                    int weaponHash = 0;
                    try { modelHash = ped.Model.Hash; } catch { }
                    try
                    {
                        relationshipGroup = Function.Call<int>(
                            Hash.GET_PED_RELATIONSHIP_GROUP_HASH, ped);
                    }
                    catch { }
                    try { weaponHash = (int)ped.Weapons.Current.Hash; } catch { }

                    string gangIdentity = DescribeActorIdentity(modelHash, gangData);
                    string action = tryingToEnterPlayerVehicle
                        ? "TryingToEnterPlayerVehicle"
                        : jackingPlayer ? "JackingPlayerFromVehicle"
                            : "CombatAgainstPlayerInVehicle";
                    LogRuntime("POLICE_PLAYER_VEHICLE_TASK_OBSERVED",
                        "Ped=" + ped.Handle
                        + "; ModelHash=" + modelHash
                        + "; Identity=" + gangIdentity
                        + "; Action=" + action
                        + "; Player=" + player.Handle
                        + "; PlayerVehicle=" + vehicle.Handle
                        + "; PlayerInVehicleAtProbe=" + playerCurrentlyInVehicle
                        + "; EntryVehicle=" + enteringVehicle
                        + "; EntrySeat=" + enteringSeat
                        + "; EntryProbeAvailable=" + entryProbeAvailable
                        + "; IsJacking=" + isJacking
                        + "; PlayerBeingJacked=" + playerIsBeingJacked
                        + "; CombatAgainstPlayer=" + combatAgainstPlayer
                        + "; WeaponHash=" + weaponHash
                        + "; RelationshipGroup=" + relationshipGroup
                        + "; EnterVehicleTaskStatus=" + ReadTaskStatus(
                            ped, Hash.TASK_ENTER_VEHICLE)
                        + "; CombatTaskStatus=" + ReadTaskStatus(
                            ped, Hash.TASK_COMBAT_PED)
                        + "; Observer=LSPDVanillaGangResponse"
                        + "; GangResponseIssuedTask=false"
                        + "; TaskOwner=UNATTRIBUTED_BY_NATIVE_EVIDENCE");
                }
                catch (Exception ex)
                {
                    LogException("POLICE_PLAYER_VEHICLE_TASK_PROBE_FAILED", ex);
                }
            }
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
                    // A model in an unassigned Gang & Turf pool is only a
                    // catalog hint. It does not establish this Ped's live gang
                    // identity; ordinary hostile civilians must remain with
                    // Citizen Response unless a named external group or a
                    // recognized vanilla faction owns the actor.
                    if (!external && !vanilla)
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
                    if (!external && !vanilla)
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
                    if (sameExternalGang || sameVanillaFaction)
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
                return ped.IsInCombatAgainst(target);
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeActorIdentity(
            int modelHash,
            LSPDGangDataIntegration gangData)
        {
            try
            {
                LSPDGangDefinition gang = gangData == null
                    ? null : gangData.FindGangForModel(modelHash);
                if (gang != null)
                    return gang.Name;
                if (gangData != null && gangData.IsMemberPoolModel(modelHash))
                    return "Unassigned Gang & Turf model-pool hint (identity unconfirmed)";
                return IsVanillaGangHash(modelHash)
                    ? "Vanilla GTA gang" : "Unassigned civilian model";
            }
            catch
            {
                return "IdentityUnavailable";
            }
        }

        private static string ReadTaskStatus(Ped ped, Hash task)
        {
            try
            {
                return Function.Call<int>(Hash.GET_SCRIPT_TASK_STATUS, ped, task)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                return "Unavailable:" + ex.GetType().Name;
            }
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
