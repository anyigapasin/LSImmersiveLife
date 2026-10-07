using GTA;
using GTA.Math;
using GTA.Native;
using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const float InteriorThresholdRadius = 1.35f;
        private const int InteriorTaskRefreshMilliseconds = 4500;
        private const int InteriorRouteTimeoutSeconds = 75;
        private const int MaximumInteriorRouteRecoveries = 3;

        private readonly LSPDAudioDispatch _audio;
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveLog _log;
        private readonly LSPoliceResponseSettings _settings;
        private LSWorldAmbientBehavior _ambientWorld;
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
            internal string IncidentId;
            internal string IncidentType;
            internal string ResponseMode;
            internal bool EmergencyUrgent;
            internal bool EmergencySignalsOn;
            // Interior follow is owned by this response unit, while AmbientWorld
            // owns the mapped threshold and registered physical door session.
            internal int InteriorFollowPhase;
            internal int InteriorTargetId;
            internal AmbientInteriorAccessRoute InteriorRoute;
            internal DateTime InteriorFollowStartedAt;
            internal DateTime InteriorFollowNextTaskAt;
            internal int InteriorFollowRecoveryCount;
            internal int InteriorThreatTargetHandle;
        }

        private const int InteriorPhaseVehicleResponse = 0;
        private const int InteriorPhaseLeaveVehicle = 1;
        private const int InteriorPhaseApproachEntry = 2;
        private const int InteriorPhaseCrossingEntry = 3;
        private const int InteriorPhaseFollowPlayer = 4;
        private const int InteriorPhaseApproachExit = 5;
        private const int InteriorPhaseCrossingExit = 6;
        private const int InteriorPhaseEnterVehicle = 7;
        private const int InteriorPhaseOutdoorResponse = 8;
        private const int InteriorPhaseFailed = 9;

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

        internal void AttachAmbientWorld(LSWorldAmbientBehavior ambientWorld)
        {
            _ambientWorld = ambientWorld;
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
            return EnsureResponseUnit(
                destination, string.Empty, string.Empty, "SceneSupport", false);
        }

        internal bool EnsureResponseUnit(
            Vector3 destination,
            string incidentId,
            string incidentType,
            string responseMode,
            bool urgent)
        {
            if (!_settings.Enabled || _settings.MaximumUnitsPerIncident <= 0)
                return false;
            CleanupDead();

            PoliceResponseUnit existing = _units.FirstOrDefault();
            if (existing != null)
            {
                existing.Destination = destination;
                existing.Responding = true;
                UpdateAssignment(
                    existing, incidentId, incidentType, responseMode, urgent, true);
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
                try
                {
                    int copGroup = unchecked((int)StringHash.AtStringHash("COP", 0));
                    Function.Call(Hash.SET_PED_AS_COP, driver, true);
                    if (copGroup != 0)
                        Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH,
                            driver, copGroup);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_RESPONSE_OFFICER_ALLY_SETUP_FAILED", ex);
                }

                PoliceResponseUnit unit = new PoliceResponseUnit
                {
                    Vehicle = vehicle,
                    Driver = driver,
                    Destination = destination,
                    LastTaskAt = DateTime.MinValue,
                    Responding = true,
                    Owned = true,
                    IncidentId = incidentId ?? string.Empty,
                    IncidentType = incidentType ?? string.Empty,
                    ResponseMode = responseMode ?? string.Empty
                };
                _units.Add(unit);
                SetEmergencySignals(unit, urgent);
                SendTo(unit, destination, true);
                Report("lsimmersivelife.police.dispatch.assigned", "assigned");
                LogRuntime(
                    "POLICE_RESPONSE_UNIT_CREATED",
                    "Incident=" + unit.IncidentId
                    + "; Type=" + unit.IncidentType
                    + "; Mode=" + unit.ResponseMode
                    + "; EmergencySignals=" + urgent
                    + "; Vehicle=" + vehicleName
                    + "; Driver=" + officerName
                    + "; Destination=" + destination);
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
            Update(destination, string.Empty, string.Empty, string.Empty, false);
        }

        internal void Update(
            Vector3? destination,
            string incidentId,
            string incidentType,
            string responseMode,
            bool urgent)
        {
            Update(destination, incidentId, incidentType, responseMode, urgent,
                null, Game.Player.Character);
        }

        internal void Update(
            Vector3? destination,
            string incidentId,
            string incidentType,
            string responseMode,
            bool urgent,
            LSPDDispatchEvent incident,
            Ped player)
        {
            CleanupDead();
            foreach (PoliceResponseUnit unit in _units.ToArray())
            {
                if (unit == null || !unit.Owned || unit.Vehicle == null
                    || !unit.Vehicle.Exists() || unit.Driver == null || !unit.Driver.Exists())
                    continue;

                Vector3 target = destination.HasValue ? destination.Value : unit.Destination;
                float distance = unit.Vehicle.Position.DistanceTo(target);
                bool incidentChanged = !string.Equals(
                    unit.IncidentId ?? string.Empty,
                    incidentId ?? string.Empty,
                    StringComparison.Ordinal);
                bool assignmentChanged = UpdateAssignment(
                    unit, incidentId, incidentType, responseMode, urgent, false);
                if (incidentChanged && unit.InteriorFollowPhase != InteriorPhaseVehicleResponse)
                    ResetInteriorFollow(unit, "ResponseAssignmentChanged");
                unit.Destination = target;
                SetEmergencySignals(unit, urgent && distance > SceneArrivalRadius);

                bool interiorAssignment = incident != null
                    && incident.OwnedByDispatch
                    && incident.SceneResolution != null
                    && incident.SceneResolution.IsInteriorScene
                    && incident.SceneResolution.InteriorId != 0
                    && string.Equals(unit.IncidentId, incident.Id, StringComparison.Ordinal);
                int playerInteriorId = 0;
                bool hasPlayerInterior = interiorAssignment
                    && player != null && player.Exists()
                    && _ambientWorld != null
                    && _ambientWorld.TryGetActorInteriorId(player, out playerInteriorId);
                bool playerInsideIncident = hasPlayerInterior
                    && playerInteriorId == incident.SceneResolution.InteriorId;
                bool responseAlreadyOnFoot = unit.InteriorFollowPhase >= InteriorPhaseLeaveVehicle
                    && unit.InteriorFollowPhase <= InteriorPhaseEnterVehicle;
                if (interiorAssignment
                    && unit.InteriorFollowPhase == InteriorPhaseOutdoorResponse
                    && playerInsideIncident
                    && distance <= SceneArrivalRadius)
                {
                    unit.InteriorFollowPhase = unit.Driver.IsInVehicle()
                        ? InteriorPhaseLeaveVehicle : InteriorPhaseApproachEntry;
                    unit.InteriorTargetId = incident.SceneResolution.InteriorId;
                    unit.InteriorFollowStartedAt = DateTime.UtcNow;
                    unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                }

                if (interiorAssignment && (responseAlreadyOnFoot
                    || (playerInsideIncident && distance <= SceneArrivalRadius)
                    || unit.InteriorFollowPhase == InteriorPhaseFailed))
                {
                    if (distance <= SceneArrivalRadius && unit.Responding)
                    {
                        unit.Responding = false;
                        SetEmergencySignals(unit, false);
                        Report("lsimmersivelife.police.dispatch.arrived", "arrived");
                        LogRuntime(
                            "POLICE_RESPONSE_UNIT_ARRIVED",
                            "Incident=" + unit.IncidentId
                            + "; Type=" + unit.IncidentType
                            + "; Mode=" + unit.ResponseMode
                            + "; Distance=" + distance);
                        Notify("~b~POLICE RESPONSE~s~\nResponse unit arrived near the active scene.");
                    }
                    MaintainInteriorIncidentResponse(unit, incident, player, playerInsideIncident);
                    continue;
                }

                if (distance <= SceneArrivalRadius)
                {
                    if (unit.Responding)
                    {
                        unit.Responding = false;
                        try { unit.Driver.Task.ClearAll(); } catch { }
                        SetEmergencySignals(unit, false);
                        Report("lsimmersivelife.police.dispatch.arrived", "arrived");
                        LogRuntime("POLICE_RESPONSE_UNIT_ARRIVED",
                            "Incident=" + unit.IncidentId
                            + "; Type=" + unit.IncidentType
                            + "; Mode=" + unit.ResponseMode
                            + "; Distance=" + distance);
                        Notify("~b~POLICE RESPONSE~s~\nResponse unit arrived near the active scene.");
                    }
                    if (interiorAssignment && playerInsideIncident
                        && unit.InteriorFollowPhase != InteriorPhaseOutdoorResponse)
                    {
                        unit.InteriorTargetId = incident.SceneResolution.InteriorId;
                        unit.InteriorFollowPhase = InteriorPhaseLeaveVehicle;
                        unit.InteriorFollowStartedAt = DateTime.UtcNow;
                        unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                        MaintainInteriorIncidentResponse(unit, incident, player, playerInsideIncident);
                    }
                    continue;
                }

                // A fleeing or moving incident can leave the unit at the first
                // scene marker. Re-arm the same owned unit when its destination
                // is now outside the arrival radius; this preserves the one-unit
                // cap without spawning a duplicate response vehicle.
                if (!unit.Responding)
                    unit.Responding = true;
                SendTo(unit, target, assignmentChanged);
            }
        }

        private void MaintainInteriorIncidentResponse(
            PoliceResponseUnit unit,
            LSPDDispatchEvent incident,
            Ped player,
            bool playerInsideIncident)
        {
            if (unit == null || !unit.Owned || unit.Driver == null || !unit.Driver.Exists()
                || _ambientWorld == null || incident == null
                || incident.SceneResolution == null || !incident.SceneResolution.IsInteriorScene)
                return;

            Ped officer = unit.Driver;
            int interiorId = incident.SceneResolution.InteriorId;
            if (interiorId == 0)
                return;
            unit.InteriorTargetId = interiorId;
            DateTime now = DateTime.UtcNow;

            if (unit.InteriorFollowPhase >= InteriorPhaseApproachEntry
                && unit.InteriorFollowPhase <= InteriorPhaseCrossingExit
                && unit.InteriorFollowStartedAt != DateTime.MinValue
                && now >= unit.InteriorFollowStartedAt.AddSeconds(InteriorRouteTimeoutSeconds))
            {
                bool entering = unit.InteriorFollowPhase <= InteriorPhaseCrossingEntry;
                RecoverInteriorFollow(unit,
                    entering,
                    "The response officer did not finish the physical doorway route before its timeout.");
                return;
            }

            int officerInteriorId;
            if (!_ambientWorld.TryGetActorInteriorId(officer, out officerInteriorId))
            {
                RecoverInteriorFollow(
                    unit,
                    unit.InteriorFollowPhase <= InteriorPhaseCrossingEntry,
                    "GTA could not identify the response officer's live interior context.");
                return;
            }

            if (unit.InteriorFollowPhase == InteriorPhaseVehicleResponse)
            {
                if (!playerInsideIncident)
                    return;
                unit.InteriorFollowPhase = officer.IsInVehicle()
                    ? InteriorPhaseLeaveVehicle : InteriorPhaseApproachEntry;
                unit.InteriorFollowStartedAt = now;
                unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                unit.InteriorFollowRecoveryCount = 0;
                LogRuntime(
                    "POLICE_RESPONSE_INTERIOR_FOLLOW_STARTED",
                    "Incident=" + incident.Id
                    + "; Officer=" + officer.Handle
                    + "; InteriorId=" + interiorId
                    + "; Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle));
            }

            if (unit.InteriorFollowPhase == InteriorPhaseLeaveVehicle)
            {
                if (officer.IsInVehicle())
                {
                    if (now >= unit.InteriorFollowNextTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_LEAVE_VEHICLE, officer, unit.Vehicle, 0);
                            Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                            unit.InteriorFollowNextTaskAt = now.AddSeconds(2);
                            LogRuntime(
                                "POLICE_RESPONSE_INTERIOR_VEHICLE_EXIT_REQUESTED",
                                "Incident=" + incident.Id
                                + "; Officer=" + officer.Handle
                                + "; Vehicle=" + unit.Vehicle.Handle);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_RESPONSE_INTERIOR_VEHICLE_EXIT_FAILED", ex);
                            RecoverInteriorFollow(unit, true, ex.Message);
                        }
                    }
                    return;
                }

                unit.InteriorFollowPhase = officerInteriorId == interiorId
                    ? InteriorPhaseFollowPlayer : InteriorPhaseApproachEntry;
                unit.InteriorFollowStartedAt = now;
                unit.InteriorFollowNextTaskAt = DateTime.MinValue;
            }

            if (unit.InteriorFollowPhase == InteriorPhaseApproachEntry)
            {
                if (officerInteriorId == interiorId)
                {
                    unit.InteriorFollowPhase = InteriorPhaseFollowPlayer;
                    unit.InteriorRoute = null;
                    unit.InteriorFollowRecoveryCount = 0;
                    LogRuntime(
                        "POLICE_RESPONSE_INTERIOR_CONTEXT_OBSERVED",
                        "Incident=" + incident.Id
                        + "; Officer=" + officer.Handle
                        + "; InteriorId=" + interiorId
                        + "; Crossing=ObservedBeforeResponseRoute");
                }
                else if (officerInteriorId != 0)
                {
                    FailInteriorFollow(unit,
                        "The response officer is in a different interior from the Dispatch scene.");
                    return;
                }
                else
                {
                    if (unit.InteriorRoute == null)
                    {
                        AmbientInteriorAccessRoute resolvedRoute;
                        string routeFailure;
                        if (!_ambientWorld.TryResolveInteriorAccessRouteForInterior(
                            officer, interiorId, true, out resolvedRoute, out routeFailure))
                        {
                            RecoverInteriorFollow(unit, true, routeFailure);
                            return;
                        }
                        unit.InteriorRoute = resolvedRoute;
                        LogRuntime(
                            "POLICE_RESPONSE_INTERIOR_ROUTE_RESOLVED",
                            "Incident=" + incident.Id
                            + "; Officer=" + officer.Handle
                            + "; InteriorId=" + interiorId
                            + "; Outside=" + resolvedRoute.Pair.OutsidePoint.SourceRecord
                            + "; Inside=" + resolvedRoute.Pair.InsidePoint.SourceRecord
                            + "; Entry=" + resolvedRoute.EntryPosition
                            + "; Destination=" + resolvedRoute.DestinationPosition);
                    }

                    AmbientInteriorAccessRoute route = unit.InteriorRoute;
                    float distanceToEntry = officer.Position.DistanceTo(route.EntryPosition);
                    if (distanceToEntry > InteriorThresholdRadius)
                    {
                        if (now >= unit.InteriorFollowNextTaskAt)
                        {
                            try
                            {
                                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                    officer,
                                    route.EntryPosition.X,
                                    route.EntryPosition.Y,
                                    route.EntryPosition.Z,
                                    1.65f,
                                    -1,
                                    0.9f,
                                    1,
                                    route.Pair.OutsidePoint.HasHeading
                                        ? route.Pair.OutsidePoint.Heading : 0f);
                                Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                                unit.InteriorFollowNextTaskAt = now.AddMilliseconds(
                                    InteriorTaskRefreshMilliseconds);
                                LogRuntime(
                                    "POLICE_RESPONSE_INTERIOR_ENTRY_APPROACH_STARTED",
                                    "Incident=" + incident.Id
                                    + "; Officer=" + officer.Handle
                                    + "; Distance=" + distanceToEntry.ToString("0.0", CultureInfo.InvariantCulture));
                            }
                            catch (Exception ex)
                            {
                                LogException("POLICE_RESPONSE_INTERIOR_ENTRY_APPROACH_FAILED", ex);
                                RecoverInteriorFollow(unit, true, ex.Message);
                            }
                        }
                        return;
                    }

                    string doorFailure;
                    if (!_ambientWorld.TryBeginInteriorAccess(officer, route, out doorFailure))
                    {
                        RecoverInteriorFollow(unit, true, doorFailure);
                        return;
                    }
                    unit.InteriorFollowPhase = InteriorPhaseCrossingEntry;
                    unit.InteriorFollowStartedAt = now;
                    unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                    LogRuntime(
                        "POLICE_RESPONSE_INTERIOR_DOOR_OPENING_REQUESTED",
                        "Incident=" + incident.Id
                        + "; Officer=" + officer.Handle
                        + "; InteriorId=" + interiorId
                        + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord);
                }
            }

            if (unit.InteriorFollowPhase == InteriorPhaseCrossingEntry
                || unit.InteriorFollowPhase == InteriorPhaseCrossingExit)
            {
                bool entering = unit.InteriorFollowPhase == InteriorPhaseCrossingEntry;
                AmbientInteriorAccessRoute route = unit.InteriorRoute;
                if (route == null)
                {
                    RecoverInteriorFollow(unit, entering,
                        "The response officer's physical door route was lost.");
                    return;
                }

                string accessMessage;
                AmbientInteriorAccessStatus accessStatus = _ambientWorld.MaintainInteriorAccess(
                    officer, out accessMessage);
                if (accessStatus == AmbientInteriorAccessStatus.Failed
                    || accessStatus == AmbientInteriorAccessStatus.None)
                {
                    RecoverInteriorFollow(unit, entering, accessMessage);
                    return;
                }
                if (accessStatus == AmbientInteriorAccessStatus.ReadyToCross
                    && now >= unit.InteriorFollowNextTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                            officer,
                            route.DestinationPosition.X,
                            route.DestinationPosition.Y,
                            route.DestinationPosition.Z,
                            1.45f,
                            -1,
                            0.35f,
                            1,
                            entering
                                ? (route.Pair.InsidePoint.HasHeading
                                    ? route.Pair.InsidePoint.Heading : 0f)
                                : (route.Pair.OutsidePoint.HasHeading
                                    ? route.Pair.OutsidePoint.Heading : 0f));
                        Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                        unit.InteriorFollowNextTaskAt = now.AddSeconds(3);
                        LogRuntime(
                            entering
                                ? "POLICE_RESPONSE_INTERIOR_CROSSING_TASK_STARTED"
                                : "POLICE_RESPONSE_EXTERIOR_CROSSING_TASK_STARTED",
                            "Incident=" + incident.Id
                            + "; Officer=" + officer.Handle
                            + "; Destination=" + route.DestinationPosition
                            + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_RESPONSE_DOOR_CROSSING_TASK_FAILED", ex);
                        RecoverInteriorFollow(unit, entering, ex.Message);
                        return;
                    }
                }
                if (accessStatus == AmbientInteriorAccessStatus.Completed)
                {
                    if (entering)
                    {
                        unit.InteriorFollowPhase = InteriorPhaseFollowPlayer;
                        unit.InteriorFollowRecoveryCount = 0;
                        unit.InteriorThreatTargetHandle = 0;
                        LogRuntime(
                            "POLICE_RESPONSE_INTERIOR_ENTRY_CONFIRMED",
                            "Incident=" + incident.Id
                            + "; Officer=" + officer.Handle
                            + "; InteriorId=" + interiorId
                            + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord
                            + "; PhysicalCrossing=true");
                    }
                    else
                    {
                        unit.InteriorFollowPhase = InteriorPhaseEnterVehicle;
                        LogRuntime(
                            "POLICE_RESPONSE_INTERIOR_EXIT_CONFIRMED",
                            "Incident=" + incident.Id
                            + "; Officer=" + officer.Handle
                            + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord
                            + "; PhysicalCrossing=true");
                    }
                    unit.InteriorRoute = null;
                    unit.InteriorFollowStartedAt = now;
                    unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                }
                return;
            }

            if (unit.InteriorFollowPhase == InteriorPhaseFollowPlayer)
            {
                if (officerInteriorId == 0)
                {
                    if (playerInsideIncident)
                    {
                        unit.InteriorFollowPhase = InteriorPhaseApproachEntry;
                        unit.InteriorFollowStartedAt = now;
                        unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                    }
                    else
                    {
                        unit.InteriorFollowPhase = InteriorPhaseEnterVehicle;
                    }
                    return;
                }
                if (officerInteriorId != interiorId)
                {
                    FailInteriorFollow(unit,
                        "The response officer left the mapped interior through an untracked route.");
                    return;
                }
                if (!playerInsideIncident)
                {
                    unit.InteriorFollowPhase = InteriorPhaseApproachExit;
                    unit.InteriorFollowStartedAt = now;
                    unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                    return;
                }

                if (player == null || !player.Exists() || player.IsDead || player.IsInVehicle())
                    return;
                float playerDistance = officer.Position.DistanceTo(player.Position);
                if (playerDistance > 6.0f && now >= unit.InteriorFollowNextTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                            officer,
                            player.Position.X,
                            player.Position.Y,
                            player.Position.Z,
                            1.25f,
                            -1,
                            4.0f,
                            1,
                            Function.Call<float>(Hash.GET_ENTITY_HEADING, player));
                        Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                        unit.InteriorFollowNextTaskAt = now.AddMilliseconds(
                            InteriorTaskRefreshMilliseconds);
                        LogRuntime(
                            "POLICE_RESPONSE_INTERIOR_PLAYER_FOLLOW_TASK",
                            "Incident=" + incident.Id
                            + "; Officer=" + officer.Handle
                            + "; Player=" + player.Handle
                            + "; Distance=" + playerDistance.ToString("0.0", CultureInfo.InvariantCulture));
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_RESPONSE_INTERIOR_PLAYER_FOLLOW_FAILED", ex);
                        RecoverInteriorFollow(unit, false, ex.Message);
                    }
                }
                return;
            }

            if (unit.InteriorFollowPhase == InteriorPhaseApproachExit)
            {
                if (playerInsideIncident)
                {
                    unit.InteriorFollowPhase = InteriorPhaseFollowPlayer;
                    unit.InteriorRoute = null;
                    unit.InteriorFollowStartedAt = now;
                    unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                    return;
                }
                if (officerInteriorId == 0)
                {
                    unit.InteriorFollowPhase = InteriorPhaseEnterVehicle;
                    return;
                }
                if (officerInteriorId != interiorId)
                {
                    FailInteriorFollow(unit,
                        "The response officer cannot resolve its mapped interior exit from the current interior.");
                    return;
                }
                if (unit.InteriorRoute == null)
                {
                    AmbientInteriorAccessRoute route;
                    string routeFailure;
                    if (!_ambientWorld.TryResolveInteriorAccessRouteForInterior(
                        officer, interiorId, false, out route, out routeFailure))
                    {
                        RecoverInteriorFollow(unit, false, routeFailure);
                        return;
                    }
                    unit.InteriorRoute = route;
                    LogRuntime(
                        "POLICE_RESPONSE_INTERIOR_EXIT_ROUTE_RESOLVED",
                        "Incident=" + incident.Id
                        + "; Officer=" + officer.Handle
                        + "; InteriorId=" + interiorId
                        + "; Inside=" + route.Pair.InsidePoint.SourceRecord
                        + "; Outside=" + route.Pair.OutsidePoint.SourceRecord);
                }

                AmbientInteriorAccessRoute exitRoute = unit.InteriorRoute;
                float exitEntryDistance = officer.Position.DistanceTo(exitRoute.EntryPosition);
                if (exitEntryDistance > InteriorThresholdRadius)
                {
                    if (now >= unit.InteriorFollowNextTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                officer,
                                exitRoute.EntryPosition.X,
                                exitRoute.EntryPosition.Y,
                                exitRoute.EntryPosition.Z,
                                1.4f,
                                -1,
                                0.9f,
                                1,
                                exitRoute.Pair.InsidePoint.HasHeading
                                    ? exitRoute.Pair.InsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                            unit.InteriorFollowNextTaskAt = now.AddMilliseconds(
                                InteriorTaskRefreshMilliseconds);
                            LogRuntime(
                                "POLICE_RESPONSE_INTERIOR_EXIT_APPROACH_STARTED",
                                "Incident=" + incident.Id
                                + "; Officer=" + officer.Handle
                                + "; Distance=" + exitEntryDistance.ToString("0.0", CultureInfo.InvariantCulture));
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_RESPONSE_INTERIOR_EXIT_APPROACH_FAILED", ex);
                            RecoverInteriorFollow(unit, false, ex.Message);
                        }
                    }
                    return;
                }

                string exitDoorFailure;
                if (!_ambientWorld.TryBeginInteriorAccess(officer, exitRoute, out exitDoorFailure))
                {
                    RecoverInteriorFollow(unit, false, exitDoorFailure);
                    return;
                }
                unit.InteriorFollowPhase = InteriorPhaseCrossingExit;
                unit.InteriorFollowStartedAt = now;
                unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                LogRuntime(
                    "POLICE_RESPONSE_EXTERIOR_DOOR_OPENING_REQUESTED",
                    "Incident=" + incident.Id
                    + "; Officer=" + officer.Handle
                    + "; InteriorId=" + interiorId
                    + "; DoorPair=" + exitRoute.Pair.InsidePoint.SourceRecord);
                return;
            }

            if (unit.InteriorFollowPhase == InteriorPhaseEnterVehicle)
            {
                if (officer.IsInVehicle())
                {
                    unit.InteriorFollowPhase = InteriorPhaseOutdoorResponse;
                    unit.InteriorRoute = null;
                    unit.InteriorFollowNextTaskAt = DateTime.MinValue;
                    LogRuntime(
                        "POLICE_RESPONSE_INTERIOR_FOLLOW_RETURNED_TO_VEHICLE",
                        "Incident=" + incident.Id
                        + "; Officer=" + officer.Handle
                        + "; Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle));
                    return;
                }
                if (unit.Vehicle == null || !unit.Vehicle.Exists())
                {
                    FailInteriorFollow(unit, "The response vehicle is no longer available outside.");
                    return;
                }
                float vehicleDistance = officer.Position.DistanceTo(unit.Vehicle.Position);
                if (vehicleDistance > 4.0f)
                {
                    if (now >= unit.InteriorFollowNextTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                officer,
                                unit.Vehicle.Position.X,
                                unit.Vehicle.Position.Y,
                                unit.Vehicle.Position.Z,
                                1.5f,
                                -1,
                                2.0f,
                                1,
                                Function.Call<float>(Hash.GET_ENTITY_HEADING, unit.Vehicle));
                            Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                            unit.InteriorFollowNextTaskAt = now.AddMilliseconds(
                                InteriorTaskRefreshMilliseconds);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_RESPONSE_RETURN_TO_VEHICLE_FAILED", ex);
                            FailInteriorFollow(unit, ex.Message);
                        }
                    }
                    return;
                }
                if (now >= unit.InteriorFollowNextTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_ENTER_VEHICLE,
                            officer, unit.Vehicle, 12000, -1, 1.0f, 1, 0);
                        Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                        unit.InteriorFollowNextTaskAt = now.AddSeconds(3);
                        LogRuntime(
                            "POLICE_RESPONSE_RETURN_TO_VEHICLE_REQUESTED",
                            "Incident=" + incident.Id
                            + "; Officer=" + officer.Handle
                            + "; Vehicle=" + unit.Vehicle.Handle);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_RESPONSE_RETURN_TO_VEHICLE_FAILED", ex);
                        FailInteriorFollow(unit, ex.Message);
                    }
                }
            }
        }

        private void RecoverInteriorFollow(
            PoliceResponseUnit unit,
            bool entering,
            string reason)
        {
            if (unit == null)
                return;
            if (_ambientWorld != null && unit.Driver != null)
                _ambientWorld.CancelInteriorAccess(unit.Driver, "PoliceResponseRouteRecovery");
            unit.InteriorFollowRecoveryCount++;
            unit.InteriorRoute = null;
            unit.InteriorFollowNextTaskAt = DateTime.UtcNow.AddSeconds(2);
            unit.InteriorFollowStartedAt = DateTime.UtcNow;
            if (unit.InteriorFollowRecoveryCount >= MaximumInteriorRouteRecoveries)
            {
                FailInteriorFollow(unit, reason);
                return;
            }
            unit.InteriorFollowPhase = entering
                ? InteriorPhaseApproachEntry : InteriorPhaseApproachExit;
            LogRuntime(
                "POLICE_RESPONSE_INTERIOR_ROUTE_RECOVERING",
                "Incident=" + unit.IncidentId
                + "; Officer=" + (unit.Driver == null ? 0 : unit.Driver.Handle)
                + "; Attempt=" + unit.InteriorFollowRecoveryCount
                + "; Direction=" + (entering ? "Enter" : "Exit")
                + "; Reason=" + (reason ?? string.Empty));
        }

        private void FailInteriorFollow(PoliceResponseUnit unit, string reason)
        {
            if (unit == null)
                return;
            if (_ambientWorld != null && unit.Driver != null)
                _ambientWorld.CancelInteriorAccess(unit.Driver, "PoliceResponseRouteFailed");
            unit.InteriorFollowPhase = InteriorPhaseFailed;
            unit.InteriorRoute = null;
            unit.InteriorFollowNextTaskAt = DateTime.MinValue;
            LogRuntime(
                "POLICE_RESPONSE_INTERIOR_ROUTE_FAILED",
                "Incident=" + unit.IncidentId
                + "; Officer=" + (unit.Driver == null ? 0 : unit.Driver.Handle)
                + "; InteriorId=" + unit.InteriorTargetId
                + "; Reason=" + (reason ?? string.Empty)
                + "; Action=NoTeleportAndHoldCurrentPosition");
            Notify("~b~POLICE RESPONSE~s~\nThe officer could not safely cross the mapped interior door.");
        }

        private void ResetInteriorFollow(PoliceResponseUnit unit, string reason)
        {
            if (unit == null)
                return;
            if (_ambientWorld != null && unit.Driver != null)
                _ambientWorld.CancelInteriorAccess(unit.Driver,
                    string.IsNullOrWhiteSpace(reason) ? "PoliceResponseReset" : reason);
            unit.InteriorFollowPhase = InteriorPhaseVehicleResponse;
            unit.InteriorTargetId = 0;
            unit.InteriorRoute = null;
            unit.InteriorFollowStartedAt = DateTime.MinValue;
            unit.InteriorFollowNextTaskAt = DateTime.MinValue;
            unit.InteriorFollowRecoveryCount = 0;
            unit.InteriorThreatTargetHandle = 0;
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

        private static bool UpdateAssignment(
            PoliceResponseUnit unit,
            string incidentId,
            string incidentType,
            string responseMode,
            bool urgent,
            bool force)
        {
            if (unit == null)
                return false;
            string nextIncidentId = incidentId ?? string.Empty;
            string nextIncidentType = incidentType ?? string.Empty;
            string nextResponseMode = responseMode ?? string.Empty;
            bool changed = force
                || !string.Equals(unit.IncidentId, nextIncidentId, StringComparison.Ordinal)
                || !string.Equals(unit.IncidentType, nextIncidentType, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(unit.ResponseMode, nextResponseMode, StringComparison.OrdinalIgnoreCase)
                || unit.EmergencyUrgent != urgent;
            unit.IncidentId = nextIncidentId;
            unit.IncidentType = nextIncidentType;
            unit.ResponseMode = nextResponseMode;
            unit.EmergencyUrgent = urgent;
            return changed;
        }

        private static void SetEmergencySignals(PoliceResponseUnit unit, bool enabled)
        {
            if (unit == null || !unit.Owned || unit.Vehicle == null
                || !unit.Vehicle.Exists() || unit.EmergencySignalsOn == enabled)
                return;
            try
            {
                Function.Call(Hash.SET_VEHICLE_SIREN, unit.Vehicle, enabled);
                Function.Call(Hash.SET_VEHICLE_HAS_MUTED_SIRENS, unit.Vehicle, !enabled);
                unit.EmergencySignalsOn = enabled;
            }
            catch
            {
                // Signal failure must not discard the physical response unit.
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
            SetEmergencySignals(unit, false);
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
