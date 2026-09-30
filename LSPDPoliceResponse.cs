using GTA;
using GTA.Math;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Owns the capped Police response unit used by active Dispatch and Gang
    /// incidents. The unit is persistent only for the current Police session,
    /// receives refreshed destinations, and is released when its assignment is
    /// finished.
    /// </summary>
    internal sealed class LSPDPoliceResponse
    {
        private const int TaskRefreshMilliseconds = 6000;
        private const float SceneArrivalRadius = 18f;

        private readonly LSPDAudioDispatch _audio;
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveLog _log;
        private readonly LSPoliceResponseSettings _settings;
        private readonly List<PoliceResponseUnit> _units =
            new List<PoliceResponseUnit>();
        private readonly Dictionary<int, DateTime> _authorityAcknowledgements =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _authorityDeescalations =
            new Dictionary<int, DateTime>();
        private DateTime _nextAuthoritySupportScan = DateTime.MinValue;
        private DateTime _nextResponseModelStatusLog = DateTime.MinValue;
        private int _nearbyAuthorityAllyCount;

        // This list is only a fallback for vanilla peds whose relationship
        // group is not exposed correctly by another mod. It deliberately does
        // not identify gangs, civilians, or external Gang & Turf members.
        private static readonly HashSet<int> PoliceAuthorityModelHashes =
            new HashSet<int>
            {
                ModelHash("s_m_y_cop_01"),
                ModelHash("s_f_y_cop_01"),
                ModelHash("s_m_y_sheriff_01"),
                ModelHash("s_f_y_sheriff_01"),
                ModelHash("s_m_y_hwaycop_01"),
                ModelHash("s_m_y_swat_01"),
                ModelHash("s_m_y_ranger_01"),
                ModelHash("s_m_m_fiboffice_01"),
                ModelHash("s_m_m_fiboffice_02")
            };

        private static readonly HashSet<int> MilitaryAuthorityModelHashes =
            new HashSet<int>
            {
                ModelHash("s_m_y_marine_01"),
                ModelHash("s_m_y_marine_02"),
                ModelHash("s_m_y_marine_03"),
                ModelHash("s_m_y_armymech_01"),
                ModelHash("s_m_y_armymech_02"),
                ModelHash("s_m_y_blackops_01"),
                ModelHash("s_m_y_blackops_02"),
                ModelHash("s_m_y_blackops_03")
            };

        private sealed class PoliceResponseUnit
        {
            internal Vehicle Vehicle;
            internal Ped Driver;
            internal Vector3 Destination;
            internal DateTime LastTaskAt;
            internal bool Responding;
            internal bool Owned;
        }

        internal LSPDPoliceResponse(LSPDAudioDispatch audio)
            : this(audio, null, null, null)
        {
        }

        internal LSPDPoliceResponse(
            LSPDAudioDispatch audio,
            LSPDProfile profile,
            LSImmersiveLog log)
            : this(audio, profile, log, null)
        {
        }

        internal LSPDPoliceResponse(
            LSPDAudioDispatch audio,
            LSPDProfile profile,
            LSImmersiveLog log,
            LSPoliceResponseSettings settings)
        {
            _audio = audio ?? throw new ArgumentNullException("audio");
            _profile = profile;
            _log = log;
            _settings = settings ?? LSPoliceResponseSettings.Default();
        }

        internal int ActiveUnitCount
        {
            get
            {
                CleanupDead();
                return _units.Count;
            }
        }

        /// <summary>
        /// Count from the last local Authority support scan. It is presentation
        /// data only: this class never takes ownership of these vanilla peds.
        /// </summary>
        internal int NearbyAuthorityAllyCount
        {
            get { return _nearbyAuthorityAllyCount; }
        }

        internal bool EnsureResponseUnit(Vector3 destination)
        {
            if (!_settings.Enabled || _settings.MaximumUnitsPerIncident <= 0)
                return false;
            CleanupDead();

            PoliceResponseUnit existing = _units.FirstOrDefault();
            if (existing != null)
            {
                existing.Destination = destination;
                existing.Responding = true;
                SendTo(existing, destination, true);
                return true;
            }

            if (_units.Count >= _settings.MaximumUnitsPerIncident)
                return false;

            LSPDPoliceStationDefinition station = SelectedStation();
            if (station == null)
                return false;

            string vehicleName = "police";
            if (_profile != null)
            {
                LSPDPoliceVehicleDefinition vehicle = _profile.Vehicles.FirstOrDefault(
                    item => string.Equals(item.Id, "police", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(item.ModelName, "police", StringComparison.OrdinalIgnoreCase));
                if (vehicle != null)
                    vehicleName = vehicle.ModelName;
            }

            string officerName = "s_m_y_cop_01";
            if (_profile != null)
            {
                LSPDPoliceModelDefinition officer = _profile.FindPed("lspd_male_patrol");
                if (officer != null)
                    officerName = officer.ModelName;
            }

            Model vehicleModel = new Model(vehicleName);
            Model officerModel = new Model(officerName);
            if (!RequestModel(vehicleModel, true) || !RequestModel(officerModel, false))
            {
                ReleaseModel(vehicleModel);
                ReleaseModel(officerModel);
                if (DateTime.UtcNow >= _nextResponseModelStatusLog)
                {
                    _nextResponseModelStatusLog = DateTime.UtcNow.AddSeconds(5);
                    LogDebug("POLICE_RESPONSE_MODEL_PENDING", vehicleName + " / " + officerName);
                }
                return false;
            }

            try
            {
                Vector3 spawn = new Vector3(station.VehicleX, station.VehicleY, station.VehicleZ);
                Vehicle vehicle = World.CreateVehicle(vehicleModel, spawn, station.VehicleHeading);
                if (vehicle == null || !vehicle.Exists())
                    return false;

                vehicle.IsPersistent = true;
                vehicle.PlaceOnGround();
                Ped driver = vehicle.CreatePedOnSeat(VehicleSeat.Driver, officerModel);
                if (driver == null || !driver.Exists())
                {
                    vehicle.Delete();
                    return false;
                }

                driver.IsPersistent = true;
                driver.BlockPermanentEvents = true;
                driver.MaxHealth = 250;
                driver.Health = 250;
                driver.Armor = 100;

                PoliceResponseUnit unit = new PoliceResponseUnit
                {
                    Vehicle = vehicle,
                    Driver = driver,
                    Destination = destination,
                    LastTaskAt = DateTime.MinValue,
                    Responding = true,
                    Owned = true
                };
                _units.Add(unit);
                SendTo(unit, destination, true);
                Report("lsimmersivelife.police.dispatch.assigned", "assigned");
                LogRuntime(
                    "POLICE_RESPONSE_UNIT_CREATED",
                    "Vehicle=" + vehicleName + "; Driver=" + officerName + "; Destination=" + destination);
                Notify("~b~POLICE RESPONSE~s~\nA response unit is heading to the scene.");
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_RESPONSE_SPAWN_FAILED", ex);
                return false;
            }
            finally
            {
                ReleaseModel(vehicleModel);
                ReleaseModel(officerModel);
            }
        }

        internal void Update(Vector3? destination)
        {
            CleanupDead();
            foreach (PoliceResponseUnit unit in _units.ToArray())
            {
                if (unit == null || !unit.Owned || unit.Vehicle == null
                    || !unit.Vehicle.Exists() || unit.Driver == null || !unit.Driver.Exists())
                    continue;

                Vector3 target = destination.HasValue ? destination.Value : unit.Destination;
                float distance = unit.Vehicle.Position.DistanceTo(target);
                unit.Destination = target;
                if (distance <= SceneArrivalRadius)
                {
                    if (unit.Responding)
                    {
                        unit.Responding = false;
                        try { unit.Driver.Task.ClearAll(); } catch { }
                        Report("lsimmersivelife.police.dispatch.arrived", "arrived");
                        LogRuntime("POLICE_RESPONSE_UNIT_ARRIVED", "Distance=" + distance);
                        Notify("~b~POLICE RESPONSE~s~\nResponse unit arrived near the active scene.");
                    }
                    continue;
                }

                // A fleeing or moving incident can leave the unit at the first
                // scene marker. Re-arm the same owned unit when its destination
                // is now outside the arrival radius; this preserves the one-unit
                // cap without spawning a duplicate response vehicle.
                if (!unit.Responding)
                    unit.Responding = true;
                SendTo(unit, target, false);
            }
        }

        /// <summary>
        /// Maintains local, non-owning recognition from nearby vanilla Police
        /// and optional military personnel toward the active Police player.
        /// It never changes relationship groups, never touches gang/civilian
        /// actors, and clears a task only when the recognised officer is
        /// actually in combat against the player.
        /// </summary>
        internal void MaintainAuthoritySupport(
            Ped player,
            LSPDAuthoritySettings authoritySettings,
            IEnumerable<Ped> protectedActors)
        {
            if (player == null || !player.Exists() || authoritySettings == null
                || !authoritySettings.EnableWorldBehaviorChanges
                || !_settings.AuthoritySupportEnabled
                || (!_settings.AuthorityGreetingEnabled
                    && !authoritySettings.SuppressAmbientPoliceHostility
                    && !authoritySettings.SuppressMilitaryHostility))
            {
                _nearbyAuthorityAllyCount = 0;
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now < _nextAuthoritySupportScan)
                return;
            _nextAuthoritySupportScan = now.AddMilliseconds(
                _settings.AuthoritySupportScanMilliseconds);
            TrimAuthoritySupportMemory(now);

            Ped[] nearby;
            try
            {
                nearby = World.GetNearbyPeds(player,
                    _settings.AuthoritySupportRadius);
            }
            catch (Exception ex)
            {
                _nearbyAuthorityAllyCount = 0;
                LogException("POLICE_AUTHORITY_SUPPORT_SCAN_FAILED", ex);
                return;
            }

            if (nearby == null)
            {
                _nearbyAuthorityAllyCount = 0;
                return;
            }

            var protectedHandles = new HashSet<int>();
            if (protectedActors != null)
            {
                foreach (Ped protectedActor in protectedActors)
                {
                    if (protectedActor != null && protectedActor.Exists())
                        protectedHandles.Add(protectedActor.Handle);
                }
            }

            var allies = new List<Ped>();
            foreach (Ped ped in nearby)
            {
                // A Convoy/Dispatch owner must keep its current task and
                // custody behavior. Authority recognition never owns those
                // peds, even when their model is a Police officer.
                if (ped == null || protectedHandles.Contains(ped.Handle))
                    continue;
                if (!IsAuthorityAlly(ped, player,
                    authoritySettings.SuppressMilitaryHostility))
                    continue;

                allies.Add(ped);
            }

            // A hostile officer always receives the available local support
            // budget before an idle officer receives a cosmetic acknowledgement.
            // This avoids the former failure mode where three greetings could
            // consume the scan budget while a fourth nearby officer kept aiming
            // at the player.
            int touched = 0;
            foreach (Ped ped in allies)
            {
                if (touched >= _settings.MaximumAuthoritySupportPeds
                    || !IsTargetingPlayer(ped, player))
                    continue;
                if (IsRemembered(_authorityDeescalations, ped.Handle, now))
                    continue;

                try
                {
                    // Clearing a confirmed player-targeting task is local and
                    // reversible: no persistent ped flags, models,
                    // relationships, or ambient entity ownership change.
                    ped.Task.ClearAll();
                    if (_settings.AuthorityGreetingEnabled && !ped.IsInVehicle())
                        ped.Task.LookAt(player, 900);
                    _authorityDeescalations[ped.Handle] = now.AddSeconds(3);
                    touched++;
                    LogRuntime(
                        "POLICE_AUTHORITY_OFFICER_DEESCALATED",
                        "Ped=" + ped.Handle + "; Military="
                        + IsMilitaryAlly(ped).ToString()
                        + "; Distance="
                        + ped.Position.DistanceTo(player.Position).ToString("0.0"));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_AUTHORITY_OFFICER_DEESCALATION_FAILED", ex);
                }
            }

            if (!_settings.AuthorityGreetingEnabled)
            {
                _nearbyAuthorityAllyCount = allies.Count;
                return;
            }

            foreach (Ped ped in allies)
            {
                if (touched >= _settings.MaximumAuthoritySupportPeds
                    || IsRemembered(_authorityDeescalations, ped.Handle, now)
                    || !CanAcknowledgeOfficer(ped, player)
                    || IsRemembered(_authorityAcknowledgements, ped.Handle, now))
                    continue;

                try
                {
                    // A short look-at acknowledgement is intentionally lighter
                    // than forcing a scripted animation on a working vanilla
                    // officer. It gives local recognition without interrupting
                    // a patrol, vehicle, scene, or external mod task.
                    ped.Task.LookAt(player, 900);
                    _authorityAcknowledgements[ped.Handle] = now.AddSeconds(20);
                    touched++;
                    LogRuntime(
                        "POLICE_AUTHORITY_OFFICER_ACKNOWLEDGED",
                        "Ped=" + ped.Handle + "; Distance="
                        + ped.Position.DistanceTo(player.Position).ToString("0.0"));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_AUTHORITY_OFFICER_ACKNOWLEDGEMENT_FAILED", ex);
                }
            }

            _nearbyAuthorityAllyCount = allies.Count;
        }

        internal void ReleaseAll()
        {
            foreach (PoliceResponseUnit unit in _units.ToArray())
                ReleaseUnit(unit);
            _units.Clear();
        }

        internal void Reset()
        {
            ReleaseAll();
            _authorityAcknowledgements.Clear();
            _authorityDeescalations.Clear();
            _nextAuthoritySupportScan = DateTime.MinValue;
            _nearbyAuthorityAllyCount = 0;
        }

        private static bool IsAuthorityAlly(
            Ped ped,
            Ped player,
            bool includeMilitary)
        {
            if (ped == null || !ped.Exists() || ped.IsDead || !ped.IsHuman
                || player == null || !player.Exists()
                || ped.Handle == player.Handle)
                return false;

            try
            {
                if (ped.IsInPoliceVehicle)
                    return true;

                int groupHash = ped.RelationshipGroup.Hash;
                if (groupHash == RelationshipGroupHashValue(RelationshipGroupHash.Cop))
                    return true;
                if (includeMilitary && groupHash == RelationshipGroupHashValue(RelationshipGroupHash.Army))
                    return true;

                int modelHash = ped.Model.Hash;
                return PoliceAuthorityModelHashes.Contains(modelHash)
                    || (includeMilitary
                        && MilitaryAuthorityModelHashes.Contains(modelHash));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsMilitaryAlly(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return false;
            try
            {
                return ped.RelationshipGroup.Hash == RelationshipGroupHashValue(RelationshipGroupHash.Army)
                    || MilitaryAuthorityModelHashes.Contains(ped.Model.Hash);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTargetingPlayer(Ped ped, Ped player)
        {
            try
            {
                return ped.IsInCombatAgainst(player);
            }
            catch
            {
                return false;
            }
        }

        private static bool CanAcknowledgeOfficer(Ped ped, Ped player)
        {
            try
            {
                if (ped.IsInVehicle() || ped.IsInCombat || ped.IsAiming
                    || ped.IsShooting || player.IsAiming || player.IsShooting)
                    return false;
                float distance = ped.Position.DistanceTo(player.Position);
                return distance >= 3.0f && distance <= 14.0f;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRemembered(
            Dictionary<int, DateTime> memory,
            int handle,
            DateTime now)
        {
            DateTime until;
            return memory.TryGetValue(handle, out until) && now < until;
        }

        private void TrimAuthoritySupportMemory(DateTime now)
        {
            TrimMemory(_authorityAcknowledgements, now);
            TrimMemory(_authorityDeescalations, now);
        }

        private static void TrimMemory(Dictionary<int, DateTime> memory, DateTime now)
        {
            if (memory.Count == 0)
                return;
            foreach (KeyValuePair<int, DateTime> pair in memory.ToArray())
                if (now >= pair.Value)
                    memory.Remove(pair.Key);
        }

        private static int ModelHash(string modelName)
        {
            return unchecked((int)StringHash.AtStringHash(modelName, 0));
        }

        private static int RelationshipGroupHashValue(RelationshipGroupHash group)
        {
            return unchecked((int)(uint)group);
        }

        private bool SendTo(PoliceResponseUnit unit, Vector3 destination, bool force)
        {
            if (unit == null || !unit.Owned || unit.Vehicle == null || !unit.Vehicle.Exists()
                || unit.Driver == null || !unit.Driver.Exists())
                return false;

            DateTime now = DateTime.UtcNow;
            if (!force && now < unit.LastTaskAt.AddMilliseconds(TaskRefreshMilliseconds))
                return true;

            try
            {
                unit.Destination = destination;
                unit.Driver.Task.DriveTo(
                    unit.Vehicle,
                    destination,
                    8f,
                    VehicleDrivingFlags.DrivingModeStopForVehicles,
                    18f);
                unit.Responding = true;
                unit.LastTaskAt = now;
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_RESPONSE_TASK_FAILED", ex);
                return false;
            }
        }

        private LSPDPoliceStationDefinition SelectedStation()
        {
            if (_profile == null)
                return null;
            return _profile.FindStation(_profile.Selection.StationId)
                ?? _profile.FindStation("mission_row")
                ?? _profile.Stations.FirstOrDefault();
        }

        private void CleanupDead()
        {
            foreach (PoliceResponseUnit unit in _units.ToArray())
            {
                if (unit == null || !unit.Owned)
                {
                    _units.Remove(unit);
                    continue;
                }
                bool vehicleMissing = unit.Vehicle == null || !unit.Vehicle.Exists();
                bool driverMissing = unit.Driver == null || !unit.Driver.Exists();
                if (vehicleMissing || driverMissing)
                {
                    if (!vehicleMissing)
                    {
                        try { unit.Vehicle.Delete(); } catch { }
                    }
                    if (!driverMissing)
                    {
                        try { unit.Driver.Delete(); } catch { }
                    }
                    _units.Remove(unit);
                }
            }
        }

        private static void ReleaseUnit(PoliceResponseUnit unit)
        {
            if (unit == null || !unit.Owned)
                return;
            try { if (unit.Driver != null && unit.Driver.Exists()) unit.Driver.Delete(); } catch { }
            try { if (unit.Vehicle != null && unit.Vehicle.Exists()) unit.Vehicle.Delete(); } catch { }
            unit.Owned = false;
        }

        private static bool RequestModel(Model model, bool vehicle)
        {
            try
            {
                if (!model.IsValid || !model.IsInCdImage || (vehicle ? !model.IsVehicle : !model.IsPed))
                    return false;
                if (!model.IsLoaded)
                    model.Request();
                return model.IsLoaded;
            }
            catch
            {
                return false;
            }
        }

        private static void ReleaseModel(Model model)
        {
            try { if (model.IsValid) model.MarkAsNoLongerNeeded(); } catch { }
        }

        private void Report(string eventId, string occurrence)
        {
            try { _audio.Report(eventId, "police-response", occurrence, "response"); } catch { }
        }

        private void Notify(string message)
        {
            try { GTA.UI.Notification.PostTicker(message, false, false); } catch { }
        }

        private void LogRuntime(string category, string message)
        {
            if (_log != null) _log.Runtime(category, message);
        }

        private void LogDebug(string category, string message)
        {
            if (_log != null) _log.Debug(category, message);
        }

        private void LogException(string category, Exception ex)
        {
            if (_log != null) _log.Exception(category, ex);
        }
    }
}
