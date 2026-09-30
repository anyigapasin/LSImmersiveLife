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
    /// Owns the player-requested Police support force. Units stage away from
    /// Anyi, drive to the actual Dispatch target, support a group or fleeing
    /// suspect interception, then stand down and clean up only their own peds
    /// and vehicles. It never owns Dispatch custody, Convoy transport, Gang
    /// actors, or ambient GTA Police.
    /// </summary>
    internal sealed class LSPDBackUp
    {
        private const int AssetPreparationTimeoutSeconds = 20;
        private const int TaskRefreshMilliseconds = 5000;
        private const int ArrivalSettleMilliseconds = 1400;
        private const int SupportTaskRefreshMilliseconds = 2500;
        private const int RouteStallTimeoutSeconds = 10;
        private const int MaximumRouteRecoveries = 5;
        private const int NpcEscortStallTimeoutSeconds = 10;
        private const int MaximumNpcEscortRecoveries = 4;
        private const float DefaultSafeCleanupDistance = 200f;
        private const int MaximumNpcRouteRetargets = 2;
        private const float MaximumRoadElevationDifference = 8f;
        // Gang support is allowed to stage beside a threat on a different
        // street, but not on a different road level.  The ordinary Backup
        // tolerance is intentionally broader for authored Dispatch/NPC
        // locations; it is too permissive for a gang response because GTA
        // can return a valid vehicle node on a drainage channel, ramp, or
        // embankment while the threat is on the street above it.
        private const float GangRoadElevationDifference = 3.5f;
        private const float GangVehicleMaximumPitchRoll = 12f;
        private const float GangMinimumStagingDistance = 55f;
        private const float GangMaximumStagingDistance = 145f;
        private const float GangDuplicateStagingDistance = 18f;
        private const float GangFootSupportArrivalDistance = 18f;
        private const float GangFootSupportTaskRefreshDistance = 14f;
        private const float GangCombatAssignmentDistance = 32f;
        private const float MinimumPursuitSpawnDistance = 120f;
        private const float DispatchVehicleCloseDistance = 32f;
        private const float DispatchVehicleStoppedSpeed = 1.25f;
        private const float DispatchOfficerContainmentDistance = 5.5f;
        private const float DispatchOfficerContainmentStartDistance = 14f;
        private const float NpcVehicleBlockMinimumForward = 8f;
        private const float NpcVehicleBlockMaximumForward = 65f;
        private const float NpcVehicleBlockLateralTolerance = 20f;
        private const float NpcVehicleBlockMaximumDistance = 75f;
        private const float NpcFootInterceptionVehicleDistance = 42f;
        private const int NpcVehicleStopSettleMilliseconds = 1200;
        private const int NpcVehicleBrakeTaskMilliseconds = 1800;
        private const int NpcVehicleInterceptionLogMilliseconds = 5000;
        private const int NpcBackgroundDepartureSeconds = 35;
        private const float NpcBackgroundDepartureMinimumDistance = 28f;

        private readonly LSPDAudioDispatch _audio;
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveLog _log;
        private readonly LSPoliceBackupSettings _settings;
        private readonly LSPoliceCleanupSettings _cleanupSettings;
        private readonly List<BackupUnit> _units = new List<BackupUnit>();

        private LSPDDispatchEvent _incident;
        // A Convoy support assignment is intentionally separate from a
        // Dispatch assignment. Convoy keeps custody and transport ownership;
        // Backup only follows its owned route threat/transport position and
        // supplies Police AI when the officer asks for help.
        private LSPDConvoy _convoy;
        // Crime Activity owns its scene and criminal actors. Backup only
        // receives this reference to stage, travel, and support that owner.
        private LSPDCrimeActivity _crimeActivity;
        // NPC Response owns the selected ambient citizen and its interaction
        // state. Backup owns only the responding officers, Police vehicle,
        // interception, pickup, and station journey.
        private LSPDNPCResponse _npcResponse;
        // Gang Adapter owns the hostile gang actors. Backup owns only the
        // staged Police force and its bounded combat support assignment.
        private LSPDGangAdapterResponse _gangResponse;
        private LSPDBackupAssignmentState _state = LSPDBackupAssignmentState.None;
        private DateTime _assetPreparationDeadline = DateTime.MinValue;
        // Model.Request must remain alive long enough for GTA to stream the
        // configured response vehicle/officer. Releasing the models in the
        // same tick cancels the request and leaves Backup permanently stuck in
        // PreparingAssets. Keep a small retry cadence instead of re-requesting
        // the same models every frame.
        private DateTime _nextAssetRequestAt = DateTime.MinValue;
        private DateTime _stateChangedAt = DateTime.MinValue;
        private DateTime _standDownStartedAt = DateTime.MinValue;
        private string _vehicleModelName = string.Empty;
        private string _officerModelName = string.Empty;
        private string _operationId = string.Empty;
        private bool _arrivalReported;
        private DateTime _lastSupportTaskAt = DateTime.MinValue;
        private BackupUnit _npcTransportUnit;
        private Ped _npcEscortOfficer;
        private DateTime _nextNpcOfficerTaskAt = DateTime.MinValue;
        private DateTime _npcEscortEntryStartedAt = DateTime.MinValue;
        private Vector3 _npcEscortLastPosition = Vector3.Zero;
        private DateTime _npcEscortLastProgressAt = DateTime.MinValue;
        private int _npcEscortRecoveryCount;
        private bool _npcInterventionStarted;
        private bool _npcTargetWasFleeing;
        private bool _npcOfficerExitRequested;
        private bool _npcTransportStarted;
        private bool _npcPlayerSessionReleased;
        private int _npcRouteRetargetCount;
        private Vector3 _npcRouteOverride = Vector3.Zero;
        private Vector3 _npcStationRouteDestination = Vector3.Zero;
        private DateTime _npcDeliveryCompletedAt = DateTime.MinValue;
        private DateTime _npcBackgroundDepartureDeadline = DateTime.MinValue;
        private Vector3 _npcBackgroundDepartureOrigin = Vector3.Zero;
        private Vector3 _npcBackgroundDepartureDestination = Vector3.Zero;
        private bool _npcBackgroundCleanupRequested;
        private DateTime _lastNpcVehicleInterceptionLogAt = DateTime.MinValue;
        private DateTime _lastNpcFootInterceptionLogAt = DateTime.MinValue;
        private DateTime _npcVehicleStoppedAt = DateTime.MinValue;
        private DateTime _nextNpcVehicleBrakeTaskAt = DateTime.MinValue;
        private bool _npcVehicleContainmentReported;
        private bool _npcVehiclePursuitStartedLogged;
        private bool _npcContainmentAimLogged;
        private DateTime _lastDispatchInterceptionTrackingAt = DateTime.MinValue;
        private DateTime _dispatchVehicleStoppedAt = DateTime.MinValue;
        private DateTime _nextDispatchVehicleExitTaskAt = DateTime.MinValue;
        private DateTime _nextDispatchInterceptionApproachTaskAt = DateTime.MinValue;
        private int _dispatchVehiclePursuitHandle;
        private Vehicle _dispatchVehiclePursuitTarget;
        private bool _dispatchVehicleExitRequested;
        private bool _dispatchContainmentStartedLogged;
        private bool _dispatchComplianceRequestedLogged;
        private bool _dispatchSuspectCompliantLogged;
        private bool _dispatchPlayerFinalArrestStartedLogged;
        private bool _dispatchPlayerFinalArrestCompletedLogged;
        private bool _gangCombatStartedLogged;
        private bool _gangCombatEndedLogged;
        private bool _gangPlayerProtectionLogged;
        private bool _gangAssignmentHeldForReengagement;
        private int _gangProtectionGroupHash;
        private int _gangPlayerRelationshipGroupHash;
        private readonly Dictionary<int, int> _gangOriginalRelationshipGroups =
            new Dictionary<int, int>();
        private int _gangLastThreatCount;

        private sealed class BackupUnit
        {
            internal Vehicle Vehicle;
            internal Ped Driver;
            internal Blip BackupBlip;
            internal List<Ped> Officers = new List<Ped>();
            internal Vector3 Destination;
            internal Vector3 ReturnDestination;
            internal float RouteHeading;
            internal DateTime LastDriveTaskAt;
            internal Vector3 LastProgressPosition;
            internal DateTime LastProgressAt;
            internal int RouteRecoveryCount;
            internal bool Arrived;
            internal bool DriverReleasedForGroupSupport;
            internal bool InterceptionExitRequested;
            internal DateTime NextInterceptionExitTaskAt;
            internal bool InterceptionAreaReported;
            internal bool NpcVehiclePursuitActive;
            internal bool VehiclePursuitLead;
            internal bool VehicleContainmentStopRequested;
            internal bool NpcCustodyCollisionProtectionApplied;
            // Gang response has a distinct lifecycle after the vehicle reaches
            // its road node. Once locked, the generic route loop must never
            // retarget or recover this vehicle while the officers support the
            // active firefight on foot.
            internal bool GangRouteLocked;
            internal bool GangVehicleArrivalReported;
            internal HashSet<int> GangExitRequestedOfficerHandles = new HashSet<int>();
            internal HashSet<int> GangExitedOfficerHandles = new HashSet<int>();
            internal HashSet<int> GangFootSupportStartedHandles = new HashSet<int>();
            internal HashSet<int> GangFootSupportReachedHandles = new HashSet<int>();
            internal HashSet<int> GangDeployedOfficerHandles = new HashSet<int>();
            internal HashSet<int> GangPlayerHostilityRepairHandles = new HashSet<int>();
            internal HashSet<int> GangProtectedActorRepairHandles = new HashSet<int>();
            internal HashSet<int> GangNoTargetHandles = new HashSet<int>();
            internal HashSet<int> GangCombatConfirmedHandles = new HashSet<int>();
            internal HashSet<int> GangDeadTargetHandles = new HashSet<int>();
            internal Dictionary<int, Ped> GangTargets = new Dictionary<int, Ped>();
            internal Dictionary<int, Vector3> GangLastSupportPositions = new Dictionary<int, Vector3>();
            internal bool GangSupportStartedLogged;
            internal bool GangDriverLossLogged;
            internal bool GangStandDownRouteStarted;
        }

        internal LSPDBackUp(
            LSPDAudioDispatch audio,
            LSPDProfile profile,
            LSImmersiveLog log,
            LSPoliceBackupSettings settings,
            LSPoliceCleanupSettings cleanupSettings = null)
        {
            _audio = audio ?? throw new ArgumentNullException("audio");
            _profile = profile;
            _log = log;
            _settings = settings ?? LSPoliceBackupSettings.Default();
            _cleanupSettings = cleanupSettings ?? LSPoliceCleanupSettings.Default();
        }

        internal LSPDBackupAssignmentState State { get { return _state; } }
        internal bool Active
        {
            get
            {
                return _state != LSPDBackupAssignmentState.None
                    && _state != LSPDBackupAssignmentState.Failed;
            }
        }

        /// <summary>
        /// A loaded NPC Backup transport continues to exist briefly so the
        /// departure is visible, but it no longer owns the player's Police
        /// activity slot. This is deliberately separate from Active because
        /// Process still has to finish the bounded background cleanup.
        /// </summary>
        internal bool BlocksPlayerActivities
        {
            get
            {
                return Active && !(_npcTransportStarted && _npcPlayerSessionReleased);
            }
        }
        internal int ActiveUnitCount { get { return _units.Count; } }
        /// <summary>
        /// Exposes Backup-owned actors only as a protection boundary. Police
        /// Authority and ambient NPC Response must not replace the route,
        /// escort, or combat tasks owned by this assignment.
        /// </summary>
        internal IEnumerable<Ped> ProtectedActors
        {
            get { return _units.SelectMany(SupportOfficers); }
        }
        /// <summary>
        /// Keeps an active response vehicle out of ambient traffic rerouting.
        /// </summary>
        internal IEnumerable<Vehicle> ProtectedVehicles
        {
            get
            {
                foreach (BackupUnit unit in _units)
                    if (unit != null && unit.Vehicle != null && unit.Vehicle.Exists())
                        yield return unit.Vehicle;
            }
        }
        internal string StatusText
        {
            get { return Active ? _state + " | Units=" + _units.Count : "No backup assignment active."; }
        }

        /// <summary>
        /// Starts one support assignment. Model loading always continues from
        /// Process, keeping an in-game menu callback free of Script.Wait.
        /// </summary>
        internal string Request(
            LSPDDispatch dispatch,
            Ped player,
            bool preferSavedFavorite)
        {
            if (!_settings.Enabled)
                return "Backup is disabled in LS Immersive settings.";
            if (dispatch == null || !dispatch.HasIncident || dispatch.Current == null)
                return "Backup requires an active Dispatch assignment.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (Active)
            {
                if (ReferenceEquals(_incident, dispatch.Current))
                    return "Backup is already assigned and " + _state.ToString().ToLowerInvariant() + ".";
                if (_state == LSPDBackupAssignmentState.StandingDown
                    && TryRetaskStandingDownUnits(dispatch, player))
                    return "Existing backup units are retasking to the active Dispatch assignment.";
                if (_state == LSPDBackupAssignmentState.StandingDown)
                    Reset();
                if (Active)
                    return "A previous backup assignment is still active.";
            }

            _incident = dispatch.Current;
            ResetDispatchInterceptionTracking();
            _convoy = null;
            _crimeActivity = null;
            _npcResponse = null;
            _vehicleModelName = ResolveVehicleModel();
            _officerModelName = ResolveOfficerModel(preferSavedFavorite);
            _operationId = Guid.NewGuid().ToString("N");
            _assetPreparationDeadline = DateTime.UtcNow.AddSeconds(AssetPreparationTimeoutSeconds);
            _nextAssetRequestAt = DateTime.MinValue;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            SetState(LSPDBackupAssignmentState.Requested);
            BeginAssetPreparation();
            if (_incident == null)
                return "Backup could not prepare the configured Police vehicle or officer model.";
            Report("lsimmersivelife.police.dispatch.assigned", "requested");
            if (_incident.State == LSPDDispatchState.SuspectFleeing)
            {
                LogRuntime("POLICE_BACKUP_INTERCEPTION_STARTED",
                    "Backup interception started; Incident=" + _incident.Id
                    + "; Target=" + (_incident.Suspect == null ? 0 : _incident.Suspect.Handle)
                    + "; Position=" + (_incident.Suspect == null
                        ? Vector3.Zero : _incident.Suspect.Position));
            }
            LogRuntime("POLICE_BACKUP_REQUESTED",
                "Incident=" + _incident.Id + "; Vehicle=" + _vehicleModelName
                + "; Officer=" + _officerModelName + "; Favorite=" + preferSavedFavorite);
            return "Backup requested. Police units are staging and will travel to the active situation.";
        }

        /// <summary>
        /// Requests Police AI support for an active custody operation. The
        /// support force still stages and drives normally; it does not create,
        /// move, or clean up the Convoy prisoner, vehicle, or road threat.
        /// </summary>
        internal string RequestConvoySupport(
            LSPDConvoy convoy,
            Ped player,
            bool preferSavedFavorite)
        {
            if (!_settings.Enabled)
                return "Backup is disabled in LS Immersive settings.";
            if (convoy == null || !convoy.Active)
                return "Backup requires an active Convoy operation.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (Active)
            {
                if (ReferenceEquals(_convoy, convoy))
                    return "Backup is already assigned and " + _state.ToString().ToLowerInvariant() + ".";
                if (_state == LSPDBackupAssignmentState.StandingDown
                    && TryRetaskStandingDownUnits(convoy, player))
                    return "Existing backup units are retasking to the Convoy operation.";
                if (_state == LSPDBackupAssignmentState.StandingDown)
                    Reset();
                if (Active)
                    return "A previous backup assignment is still active.";
            }

            _incident = null;
            _convoy = convoy;
            _crimeActivity = null;
            _npcResponse = null;
            _vehicleModelName = ResolveVehicleModel();
            _officerModelName = ResolveOfficerModel(preferSavedFavorite);
            _operationId = Guid.NewGuid().ToString("N");
            _assetPreparationDeadline = DateTime.UtcNow.AddSeconds(AssetPreparationTimeoutSeconds);
            _nextAssetRequestAt = DateTime.MinValue;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            SetState(LSPDBackupAssignmentState.Requested);
            BeginAssetPreparation();
            Report("lsimmersivelife.police.transport.requested", "convoy-backup-requested");
            LogRuntime("POLICE_BACKUP_CONVOY_REQUESTED",
                "ConvoyRequested=" + convoy.IsRequestedConvoyActivity
                + "; Vehicle=" + _vehicleModelName
                + "; Officer=" + _officerModelName
                + "; Favorite=" + preferSavedFavorite);
            return "Convoy backup requested. Police units are staging and will travel to the transport operation.";
        }

        /// <summary>
        /// Requests support for a player-led Crime Activity. The scene itself
        /// remains owned by LSPDCrimeActivity; this owner only creates the
        /// travelling Police force and assists after it has arrived.
        /// </summary>
        internal string RequestCrimeActivitySupport(
            LSPDCrimeActivity crimeActivity,
            Ped player,
            bool preferSavedFavorite)
        {
            if (!_settings.Enabled)
                return "Backup is disabled in LS Immersive settings.";
            if (crimeActivity == null || !crimeActivity.Active)
                return "Backup requires an active Crime Activity investigation.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (Active)
            {
                if (ReferenceEquals(_crimeActivity, crimeActivity))
                    return "Backup is already assigned and " + _state.ToString().ToLowerInvariant() + ".";
                if (_state == LSPDBackupAssignmentState.StandingDown
                    && TryRetaskStandingDownUnits(crimeActivity, player))
                    return "Existing backup units are retasking to the Crime Activity.";
                if (_state == LSPDBackupAssignmentState.StandingDown)
                    Reset();
                if (Active)
                    return "A previous backup assignment is still active.";
            }

            _incident = null;
            _convoy = null;
            _crimeActivity = crimeActivity;
            _npcResponse = null;
            _vehicleModelName = ResolveVehicleModel();
            _officerModelName = ResolveOfficerModel(preferSavedFavorite);
            _operationId = Guid.NewGuid().ToString("N");
            _assetPreparationDeadline = DateTime.UtcNow.AddSeconds(AssetPreparationTimeoutSeconds);
            _nextAssetRequestAt = DateTime.MinValue;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            SetState(LSPDBackupAssignmentState.Requested);
            BeginAssetPreparation();
            // Crime Activity is not a Dispatch incident. Use its own existing
            // audio context so the player does not hear an unrelated callout
            // assignment when requesting quiet support for an investigation.
            Report("lsimmersivelife.police.activity.support_requested", "crime-activity-requested");
            LogRuntime("POLICE_BACKUP_CRIME_ACTIVITY_REQUESTED",
                "Activity=" + (crimeActivity.CurrentIntel == null ? string.Empty : crimeActivity.CurrentIntel.Id)
                + "; Vehicle=" + _vehicleModelName
                + "; Officer=" + _officerModelName
                + "; Favorite=" + preferSavedFavorite);
            return "Backup requested. Police units are staging and will travel quietly to the Crime Activity.";
        }

        /// <summary>
        /// Starts one natural response to an NPC contact. The linked NPC owner
        /// retains the ambient citizen; this owner supplies one Police car and
        /// its officers, intercepts the tracked subject, and drives to station.
        /// </summary>
        internal string RequestNpcSupport(
            LSPDNPCResponse npcResponse,
            Ped player,
            bool preferSavedFavorite)
        {
            if (!_settings.Enabled)
                return "Backup is disabled in LS Immersive settings.";
            if (npcResponse == null || !npcResponse.CanRequestBackup)
                return "NPC Backup requires a detained or fleeing citizen contact.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (Active)
            {
                if (ReferenceEquals(_npcResponse, npcResponse))
                    return "Backup is already assigned and " + _state.ToString().ToLowerInvariant() + ".";
                if (_state == LSPDBackupAssignmentState.StandingDown)
                    Reset();
                if (Active)
                    return "A previous backup assignment is still active.";
            }

            _incident = null;
            _convoy = null;
            _crimeActivity = null;
            _npcResponse = npcResponse;
            _vehicleModelName = ResolveVehicleModel();
            _officerModelName = ResolveOfficerModel(preferSavedFavorite);
            _operationId = Guid.NewGuid().ToString("N");
            _assetPreparationDeadline = DateTime.UtcNow.AddSeconds(AssetPreparationTimeoutSeconds);
            _nextAssetRequestAt = DateTime.MinValue;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            _npcTransportUnit = null;
            _npcEscortOfficer = null;
            _nextNpcOfficerTaskAt = DateTime.MinValue;
            _npcEscortEntryStartedAt = DateTime.MinValue;
            _npcEscortLastPosition = Vector3.Zero;
            _npcEscortLastProgressAt = DateTime.MinValue;
            _npcEscortRecoveryCount = 0;
            _npcInterventionStarted = false;
            _npcTargetWasFleeing = npcResponse.IsFleeing
                || npcResponse.WasFleeingFootContact
                || npcResponse.WasFleeingTrafficContact;
            _npcOfficerExitRequested = false;
            _npcTransportStarted = false;
            _npcPlayerSessionReleased = false;
            _npcRouteRetargetCount = 0;
            _npcRouteOverride = Vector3.Zero;
            _npcStationRouteDestination = Vector3.Zero;
            _npcDeliveryCompletedAt = DateTime.MinValue;
            _npcBackgroundDepartureDeadline = DateTime.MinValue;
            _npcBackgroundDepartureOrigin = Vector3.Zero;
            _npcBackgroundDepartureDestination = Vector3.Zero;
            _npcBackgroundCleanupRequested = false;
            _lastNpcVehicleInterceptionLogAt = DateTime.MinValue;
            _lastNpcFootInterceptionLogAt = DateTime.MinValue;
            _npcVehicleStoppedAt = DateTime.MinValue;
            _nextNpcVehicleBrakeTaskAt = DateTime.MinValue;
            _npcVehicleContainmentReported = false;
            _npcVehiclePursuitStartedLogged = false;
            _npcContainmentAimLogged = false;
            SetState(LSPDBackupAssignmentState.Requested);
            BeginAssetPreparation();
            if (_state == LSPDBackupAssignmentState.Failed)
                return "Backup could not prepare the configured Police vehicle or officer model.";
            Report("lsimmersivelife.police.backup.requested", "npc-contact-requested");
            LogRuntime("POLICE_BACKUP_NPC_REQUESTED",
                "Fleeing=" + _npcTargetWasFleeing
                + "; Target=" + npcResponse.SupportPosition
                + "; Vehicle=" + _vehicleModelName
                + "; Officer=" + _officerModelName);
            return "Backup requested. One Police unit is staging for the citizen contact.";
        }

        /// <summary>
        /// Starts the player-requested Backup force for an active local gang
        /// incident. Gang Adapter remains the owner of the hostile peds; this
        /// class only stages Police vehicles/officers and assigns bounded
        /// combat tasks after the units arrive.
        /// </summary>
        internal string RequestGangSupport(
            LSPDGangAdapterResponse gangResponse,
            Ped player,
            bool preferSavedFavorite)
        {
            if (!_settings.Enabled)
                return "Backup is disabled in LS Immersive settings.";
            if (gangResponse == null || !gangResponse.HasActiveIncident)
                return "Gang Backup requires an active gang threat.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (Active)
            {
                if (ReferenceEquals(_gangResponse, gangResponse))
                    return "Gang Backup is already assigned and "
                        + _state.ToString().ToLowerInvariant() + ".";
                if (_state == LSPDBackupAssignmentState.StandingDown)
                    Reset();
                if (Active)
                    return "A previous backup assignment is still active.";
            }

            _incident = null;
            _convoy = null;
            _crimeActivity = null;
            _npcResponse = null;
            _gangResponse = gangResponse;
            _vehicleModelName = ResolveVehicleModel();
            _officerModelName = ResolveOfficerModel(preferSavedFavorite);
            _operationId = Guid.NewGuid().ToString("N");
            _assetPreparationDeadline = DateTime.UtcNow.AddSeconds(AssetPreparationTimeoutSeconds);
            _nextAssetRequestAt = DateTime.MinValue;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            _gangCombatStartedLogged = false;
            _gangCombatEndedLogged = false;
            _gangPlayerProtectionLogged = false;
            _gangAssignmentHeldForReengagement = false;
            _gangLastThreatCount = 0;
            SetState(LSPDBackupAssignmentState.Requested);
            BeginAssetPreparation();
            if (_state == LSPDBackupAssignmentState.Failed)
                return "Backup could not prepare the configured Police vehicle or officer model.";

            Report("lsimmersivelife.police.backup.requested", "gang-requested");
            LogRuntime(
                "POLICE_BACKUP_GANG_REQUESTED",
                "Identity=" + gangResponse.GangName
                + "; Turf=" + gangResponse.TerritoryName
                + "; Members=" + gangResponse.ThreatCount
                + "; Units=" + Math.Min(3, Math.Max(2, _settings.UnitsPerRequest))
                + "; OfficersPerUnit=" + Math.Max(2, _settings.OfficersPerUnit)
                + "; Vehicle=" + _vehicleModelName
                + "; Officer=" + _officerModelName);
            return "Gang Backup requested. Armed Police units are staging for the gang threat.";
        }

        internal void Process(LSPDDispatch dispatch, Ped player, bool paused)
        {
            Process(dispatch, null, null, null, player, paused);
        }

        internal void Process(LSPDDispatch dispatch, LSPDConvoy convoy, Ped player, bool paused)
        {
            Process(dispatch, convoy, null, null, player, paused);
        }

        internal void Process(
            LSPDDispatch dispatch,
            LSPDConvoy convoy,
            LSPDCrimeActivity crimeActivity,
            Ped player,
            bool paused)
        {
            Process(dispatch, convoy, crimeActivity, null, player, paused);
        }

        internal void Process(
            LSPDDispatch dispatch,
            LSPDConvoy convoy,
            LSPDCrimeActivity crimeActivity,
            LSPDNPCResponse npcResponse,
            Ped player,
            bool paused)
        {
            Process(dispatch, convoy, crimeActivity, npcResponse,
                null, player, paused);
        }

        internal void Process(
            LSPDDispatch dispatch,
            LSPDConvoy convoy,
            LSPDCrimeActivity crimeActivity,
            LSPDNPCResponse npcResponse,
            LSPDGangAdapterResponse gangResponse,
            Ped player,
            bool paused)
        {
            if (!Active || paused)
                return;

            DateTime now = DateTime.UtcNow;
            if (_state == LSPDBackupAssignmentState.StandingDown)
            {
                ProcessStandDown(player, now);
                return;
            }
            bool convoyAssignment = _convoy != null;
            bool crimeActivityAssignment = _crimeActivity != null;
            bool npcAssignment = _npcResponse != null;
            bool gangAssignment = _gangResponse != null;
            if (convoyAssignment)
            {
                if (convoy == null || !ReferenceEquals(convoy, _convoy) || !convoy.Active)
                {
                    BeginStandDown("The linked Convoy operation finished or changed.");
                    return;
                }
            }
            else if (crimeActivityAssignment)
            {
                if (crimeActivity == null || !ReferenceEquals(crimeActivity, _crimeActivity)
                    || !crimeActivity.Active)
                {
                    BeginStandDown("The linked Crime Activity finished or changed.");
                    return;
                }
            }
            else if (npcAssignment)
            {
                if (npcResponse == null || !ReferenceEquals(npcResponse, _npcResponse)
                    || !npcResponse.HasBackupAssignment)
                {
                    if (_npcDeliveryCompletedAt != DateTime.MinValue
                        && now < _npcDeliveryCompletedAt.AddSeconds(3))
                        return;
                    BeginStandDown("The linked NPC contact finished or changed.");
                    return;
                }
            }
            else if (gangAssignment)
            {
                if (gangResponse == null
                    || !ReferenceEquals(gangResponse, _gangResponse)
                    || !gangResponse.HasActiveIncident)
                {
                    BeginStandDown("The linked gang threat finished or changed.");
                    return;
                }
            }
            else if (dispatch == null || !dispatch.HasIncident || !ReferenceEquals(dispatch.Current, _incident))
            {
                BeginStandDown("The linked Dispatch assignment finished or changed.");
                return;
            }
            if (_state == LSPDBackupAssignmentState.Requested
                || _state == LSPDBackupAssignmentState.PreparingAssets)
            {
                ProcessAssetPreparation(player, now);
                return;
            }

            CleanupMissingUnits();
            if (_units.Count == 0)
            {
                if (npcAssignment && _npcTransportStarted && _npcPlayerSessionReleased)
                {
                    npcResponse.CompleteBackupBackgroundDeparture(
                        "Backup unit was no longer available after custody transfer.");
                    LogRuntime("POLICE_BACKUP_NPC_BACKGROUND_CLEANUP",
                        "Reason=BackupUnitUnavailableAfterCustodyTransfer");
                    Reset();
                    return;
                }
                Fail("No backup units remained available.");
                return;
            }

            // A loaded Backup prisoner is no longer a player-facing NPC
            // interaction. Do not send this unit back through the generic
            // assignment destination logic: that was the path that converted
            // a completed local handoff into a station-route/recovery loop.
            if (npcAssignment && _npcTransportStarted && _npcPlayerSessionReleased)
            {
                ProcessNpcBackgroundDeparture(npcResponse, player, now);
                return;
            }

            // A fleeing traffic contact is a live-entity pursuit, not a
            // stationary NPC route.  Once the unit has been staged, keep the
            // moving suspect vehicle owned by the NPC interception routine so
            // the generic destination loop cannot replace VehicleChase with a
            // stale road-coordinate task.  The same unit then transitions
            // directly into the existing physical custody handoff after the
            // vehicle has stopped.
            if (npcAssignment && _npcTargetWasFleeing && !_npcTransportStarted
                && MaintainNpcVehicleAssignment(npcResponse, player, now))
                return;

            bool pursuit = !convoyAssignment && !crimeActivityAssignment
                && !npcAssignment && !gangAssignment
                && dispatch.State == LSPDDispatchState.SuspectFleeing;
            Ped pursuitTarget = pursuit ? ResolveDispatchPursuitTarget(dispatch) : null;
            Vehicle pursuitVehicle = pursuit
                ? ResolvePursuitVehicle(_incident, pursuitTarget)
                : null;
            if (!npcAssignment && !gangAssignment)
                ObserveDispatchFinalArrest(dispatch.Current);

            Vector3 destination = convoyAssignment
                ? ResolveConvoyDestination(convoy, player)
                : crimeActivityAssignment
                    ? ResolveCrimeActivityDestination(crimeActivity, player)
                    : npcAssignment
                        ? ResolveNpcDestination(npcResponse, player)
                        : gangAssignment
                            ? ResolveGangDestination(gangResponse, player)
                        : ResolveDestination(dispatch, player, pursuit);

            if (pursuit && pursuitTarget != null)
            {
                Vehicle trackedPursuitVehicle = _dispatchVehicleExitRequested
                    && _dispatchVehiclePursuitTarget != null
                    && _dispatchVehiclePursuitTarget.Exists()
                    ? _dispatchVehiclePursuitTarget : pursuitVehicle;
                LogDispatchInterceptionTracking(pursuitTarget, trackedPursuitVehicle, now);
                if (trackedPursuitVehicle != null
                    && (pursuitTarget.IsInVehicle() || _dispatchVehicleExitRequested)
                    && MaintainDispatchVehicleInterception(
                        pursuitTarget, trackedPursuitVehicle, destination, now))
                    return;
            }

            bool allArrived = true;
            foreach (BackupUnit unit in _units)
            {
                // Gang units are locked independently as soon as they reach
                // their road node. A locked unit is now a foot-support owner;
                // never let the generic route loop retarget or recover it
                // while another Gang unit is still travelling.
                if (gangAssignment && unit != null && unit.GangRouteLocked)
                    continue;
                if (!IsUsable(unit))
                    continue;

                // A bounded local road waypoint is used when a Backup route
                // has to recover.  Consume that waypoint before evaluating
                // the next assignment destination; otherwise the generic
                // arrival logic can keep the old waypoint active or send the
                // unit back to the original citizen scene.
                if (npcAssignment && _npcRouteOverride != Vector3.Zero
                    && unit.Vehicle.Position.DistanceTo(_npcRouteOverride)
                        <= Math.Max(10f, _settings.ArrivalRadius))
                {
                    Vector3 reachedWaypoint = _npcRouteOverride;
                    _npcRouteOverride = Vector3.Zero;
                    unit.Arrived = false;
                    unit.RouteRecoveryCount = 0;
                    unit.LastProgressPosition = unit.Vehicle.Position;
                    unit.LastProgressAt = now;
                    unit.LastDriveTaskAt = DateTime.MinValue;
                    destination = ResolveNpcDestination(npcResponse, player);
                    if (destination != Vector3.Zero)
                        SendTo(unit, destination, true);
                    LogRuntime("POLICE_BACKUP_NPC_ROUTE_WAYPOINT_REACHED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; Waypoint=" + reachedWaypoint
                        + "; NextDestination=" + destination
                        + "; Transport=" + _npcTransportStarted);
                    allArrived = false;
                    continue;
                }
                if ((convoyAssignment || crimeActivityAssignment || npcAssignment || gangAssignment || pursuit) && unit.Arrived
                    && unit.Vehicle.Position.DistanceTo(destination)
                        > Math.Max(38f, _settings.ArrivalRadius * 2f)
                    && !unit.GangRouteLocked
                    && !(pursuit && unit.InterceptionExitRequested)
                    && !(npcAssignment && (_npcOfficerExitRequested
                        || unit.NpcVehiclePursuitActive)))
                {
                    // A later staged route threat or moving pursuit target has
                    // a different position. Reuse the same owned unit rather
                    // than smoke-spawning a second response beside the player.
                    unit.Arrived = false;
                }
                if (!unit.Arrived)
                {
                    allArrived = false;
                    if (unit.Vehicle.Position.DistanceTo(destination) <= _settings.ArrivalRadius)
                    {
                        unit.Arrived = true;
                        SetEmergencySignals(unit.Vehicle, false);
                        try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true); } catch { }
                        try { unit.Driver.Task.ClearAll(); } catch { }
                        // Keep the driver with the vehicle for its return trip.
                        // Support passengers leave and join the physical scene.
                        bool deferFleeingNpcExit = npcAssignment && _npcTargetWasFleeing;
                        foreach (Ped officer in pursuit || deferFleeingNpcExit
                            ? Enumerable.Empty<Ped>() : unit.Officers)
                        {
                            if (officer == null || !officer.Exists() || officer.Handle == unit.Driver.Handle)
                                continue;
                            try { Function.Call(Hash.TASK_LEAVE_VEHICLE, officer, unit.Vehicle, 0); }
                            catch (Exception ex) { LogException("POLICE_BACKUP_EXIT_TASK_FAILED", ex); }
                        }
                        if (gangAssignment)
                            LockGangSupportRoute(unit, gangResponse, destination);
                        if (pursuit && !unit.InterceptionAreaReported)
                        {
                            unit.InterceptionAreaReported = true;
                            LogRuntime("POLICE_BACKUP_INTERCEPTION_AREA_REACHED",
                                "Vehicle=" + unit.Vehicle.Handle
                                + "; Target=" + (pursuitTarget == null ? 0 : pursuitTarget.Handle)
                                + "; TargetPosition=" + (pursuitTarget == null ? Vector3.Zero : pursuitTarget.Position)
                                + "; RoadPosition=" + destination);
                        }
                        continue;
                    }
                    if (TryRecoverStalledRoute(unit, destination, now))
                        continue;
                    if (HasExhaustedRouteRecovery(unit, now))
                    {
                        if (npcAssignment && !_npcTargetWasFleeing
                            && (_npcTransportStarted
                                ? TryRetargetNpcTransportRoute(unit, destination, now)
                                : TryRetargetNpcCustodyRoute(unit, player, now)))
                        {
                            SetState(LSPDBackupAssignmentState.EnRoute);
                            continue;
                        }
                        string routeFailure = "Backup route remained blocked after "
                            + MaximumRouteRecoveries + " bounded recovery attempt(s)."
                            + " Vehicle=" + unit.Vehicle.Handle
                            + "; Destination=" + destination;
                        LogRuntime("POLICE_BACKUP_ROUTE_FAILED", routeFailure);
                        Fail(routeFailure);
                        return;
                    }
                    SendTo(unit, destination, false);
                }
            }

            if (!allArrived)
            {
                // A first Gang unit may support the firefight while a second
                // unit is still driving. Keep processing the unlocked route,
                // then process the already locked unit independently.
                if (gangAssignment && _units.Any(unit => unit != null
                    && unit.GangRouteLocked))
                    SupportGangThreat(gangResponse, player, now);
                else
                    SetState((pursuit || (npcAssignment && _npcTargetWasFleeing))
                        ? LSPDBackupAssignmentState.Intercepting
                        : LSPDBackupAssignmentState.EnRoute);
                return;
            }
            if (!gangAssignment
                && now < _stateChangedAt.AddMilliseconds(ArrivalSettleMilliseconds))
                return;

            if (!_arrivalReported)
            {
                _arrivalReported = true;
                Report(convoyAssignment
                    ? "lsimmersivelife.police.transport.requested"
                    : crimeActivityAssignment
                        ? "lsimmersivelife.police.activity.unit_arrived"
                        : npcAssignment
                            ? "lsimmersivelife.police.backup_reply.assigned"
                            : "lsimmersivelife.police.dispatch.arrived",
                    crimeActivityAssignment ? "crime-activity-arrived"
                        : npcAssignment ? "npc-contact-arrived" : "arrived");
                Notify(npcAssignment && _npcTargetWasFleeing
                    ? "~b~POLICE BACKUP~s~\nBackup blocked the tracked citizen. Unit is securing the contact."
                    : crimeActivityAssignment
                    ? "~b~POLICE BACKUP~s~\nSupport units arrived near the Crime Activity."
                    : "~b~POLICE BACKUP~s~\nSupport units arrived near the active situation.");
                LogRuntime("POLICE_BACKUP_ARRIVED",
                    (convoyAssignment
                        ? "Convoy=true"
                        : crimeActivityAssignment
                            ? "CrimeActivity=" + _crimeActivity.CurrentIntel.Id
                            : npcAssignment
                                ? "NpcContact=true; Target=" + npcResponse.SupportPosition
                                : gangAssignment
                                    ? "Gang=" + gangResponse.GangName
                                        + "; Turf=" + gangResponse.TerritoryName
                                        + "; Threats=" + gangResponse.ThreatCount
                                    : "Incident=" + _incident.Id)
                    + "; Units=" + _units.Count);
            }
            if (convoyAssignment)
                SupportConvoy(convoy);
            else if (crimeActivityAssignment)
                SupportCrimeActivity(crimeActivity, player);
            else if (npcAssignment)
                SupportNpcContact(npcResponse);
            else if (gangAssignment)
                SupportGangThreat(gangResponse, player, now);
            else if (pursuit)
                SupportInterception(dispatch, player);
            else
                SupportScene(dispatch, player);
        }

        internal void BeginStandDown(string reason)
        {
            if (!Active || _state == LSPDBackupAssignmentState.StandingDown)
                return;
            if (_gangResponse != null)
                LogGangCombatEnded(_gangResponse, reason ?? "GangBackupStandDown");
            ReleaseGangPlayerProtection();
            _standDownStartedAt = DateTime.UtcNow;
            foreach (BackupUnit unit in _units)
            {
                if (_gangResponse != null && unit != null && unit.GangRouteLocked
                    && unit.Vehicle != null && unit.Vehicle.Exists())
                {
                    ReleaseNpcCustodyCollisionProtection(unit);
                    SetEmergencySignals(unit.Vehicle, false);
                    unit.DriverReleasedForGroupSupport = true;
                    unit.GangStandDownRouteStarted = false;
                    foreach (Ped officer in SupportOfficers(unit))
                    {
                        if (officer.IsDead || officer.IsInVehicle())
                            continue;
                        try
                        {
                            Function.Call(Hash.CLEAR_PED_TASKS, officer);
                            VehicleSeat seat = unit.Driver != null
                                && officer.Handle == unit.Driver.Handle
                                ? VehicleSeat.Driver : VehicleSeat.RightFront;
                            Function.Call(Hash.TASK_ENTER_VEHICLE,
                                officer, unit.Vehicle, 12000,
                                (int)seat, 1.0f, 1, 0);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_GANG_STAND_DOWN_ENTRY_FAILED", ex);
                        }
                    }
                    continue;
                }
                if (IsUsable(unit))
                {
                    ReleaseNpcCustodyCollisionProtection(unit);
                    SetEmergencySignals(unit.Vehicle, false);
                    if (_gangResponse != null && unit.DriverReleasedForGroupSupport
                        && unit.Driver != null && unit.Driver.Exists()
                        && !unit.Driver.IsInVehicle())
                    {
                        try
                        {
                            Function.Call(Hash.TASK_ENTER_VEHICLE,
                                unit.Driver, unit.Vehicle, 12000, -1, 1.0f, 1, 0);
                            unit.LastDriveTaskAt = DateTime.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_GANG_DRIVER_REENTRY_FAILED", ex);
                        }
                    }
                    else
                        SendTo(unit, unit.ReturnDestination, true);
                }
            }
            SetState(LSPDBackupAssignmentState.StandingDown);
            LogRuntime("POLICE_BACKUP_STAND_DOWN", reason ?? string.Empty);
        }

        internal void Reset()
        {
            // If the assignment is cancelled while models are still streaming,
            // release those outstanding requests. Spawned entities are not
            // affected; this only returns the model assets to GTA's streamer.
            ReleasePreparedModelRequests();
            ReleaseGangPlayerProtection();
            foreach (BackupUnit unit in _units.ToArray())
                DeleteUnit(unit);
            _units.Clear();
            _incident = null;
            _convoy = null;
            _crimeActivity = null;
            _npcResponse = null;
            _gangResponse = null;
            _operationId = string.Empty;
            _assetPreparationDeadline = DateTime.MinValue;
            _nextAssetRequestAt = DateTime.MinValue;
            _stateChangedAt = DateTime.MinValue;
            _standDownStartedAt = DateTime.MinValue;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            _npcTransportUnit = null;
            _npcEscortOfficer = null;
            _nextNpcOfficerTaskAt = DateTime.MinValue;
            _npcEscortEntryStartedAt = DateTime.MinValue;
            _npcEscortLastPosition = Vector3.Zero;
            _npcEscortLastProgressAt = DateTime.MinValue;
            _npcEscortRecoveryCount = 0;
            _npcInterventionStarted = false;
            _npcTargetWasFleeing = false;
            _npcOfficerExitRequested = false;
            _npcTransportStarted = false;
            _npcPlayerSessionReleased = false;
            _npcRouteRetargetCount = 0;
            _npcRouteOverride = Vector3.Zero;
            _npcStationRouteDestination = Vector3.Zero;
            _npcDeliveryCompletedAt = DateTime.MinValue;
            _lastNpcVehicleInterceptionLogAt = DateTime.MinValue;
            _lastNpcFootInterceptionLogAt = DateTime.MinValue;
            _npcVehicleStoppedAt = DateTime.MinValue;
            _nextNpcVehicleBrakeTaskAt = DateTime.MinValue;
            _npcVehicleContainmentReported = false;
            _npcVehiclePursuitStartedLogged = false;
            _npcContainmentAimLogged = false;
            _gangCombatStartedLogged = false;
            _gangCombatEndedLogged = false;
            _gangPlayerProtectionLogged = false;
            _gangAssignmentHeldForReengagement = false;
            _gangLastThreatCount = 0;
            ResetDispatchInterceptionTracking();
            _state = LSPDBackupAssignmentState.None;
        }

        private void BeginAssetPreparation()
        {
            Model vehicle = new Model(_vehicleModelName);
            Model officer = new Model(_officerModelName);
            try
            {
                if (!IsUsableModel(vehicle, true) || !IsUsableModel(officer, false))
                {
                    ReleaseModel(vehicle);
                    ReleaseModel(officer);
                    Fail("The configured backup vehicle or officer model is unavailable.");
                    return;
                }

                // Do not MarkAsNoLongerNeeded here. That was the core runtime
                // failure: each request was cancelled immediately, so the
                // configured Police vehicle/officer never had a chance to
                // finish streaming before the 20-second timeout.
                if (!vehicle.IsLoaded) vehicle.Request();
                if (!officer.IsLoaded) officer.Request();
                _nextAssetRequestAt = DateTime.UtcNow.AddMilliseconds(750);
                SetState(LSPDBackupAssignmentState.PreparingAssets);
                LogRuntime("POLICE_BACKUP_ASSETS_REQUESTED",
                    "Vehicle=" + _vehicleModelName + "; Officer=" + _officerModelName);
            }
            catch (Exception ex)
            {
                ReleaseModel(vehicle);
                ReleaseModel(officer);
                LogException("POLICE_BACKUP_ASSET_REQUEST_FAILED", ex);
                Fail("Backup assets could not be prepared safely.");
            }
        }

        private void ProcessAssetPreparation(Ped player, DateTime now)
        {
            Model vehicle = new Model(_vehicleModelName);
            Model officer = new Model(_officerModelName);
            bool releaseAssets = false;
            try
            {
                if (!IsUsableModel(vehicle, true) || !IsUsableModel(officer, false))
                {
                    releaseAssets = true;
                    Fail("The configured backup vehicle or officer model is unavailable.");
                    return;
                }

                if (!vehicle.IsLoaded || !officer.IsLoaded)
                {
                    if (now >= _assetPreparationDeadline)
                    {
                        releaseAssets = true;
                        Fail("Backup assets did not finish loading before the response timeout.");
                        return;
                    }

                    // Model streaming is asynchronous. Request only at a small
                    // cadence and keep the request alive between Script ticks.
                    if (now >= _nextAssetRequestAt)
                    {
                        if (!vehicle.IsLoaded) vehicle.Request();
                        if (!officer.IsLoaded) officer.Request();
                        _nextAssetRequestAt = now.AddMilliseconds(750);
                    }
                    return;
                }

                releaseAssets = true;
                LogRuntime("POLICE_BACKUP_ASSETS_READY",
                    "Vehicle=" + _vehicleModelName + "; Officer=" + _officerModelName);
                if (!CreateUnits(vehicle, officer, player))
                {
                    Fail("Backup units could not be staged safely.");
                    return;
                }
                SetState(LSPDBackupAssignmentState.EnRoute);
                Notify("~b~POLICE BACKUP~s~\nSupport units are en route to the active situation.");
            }
            catch (Exception ex)
            {
                releaseAssets = true;
                LogException("POLICE_BACKUP_STAGE_FAILED", ex);
                Fail("Backup staging failed safely.");
            }
            finally
            {
                // Release only after the assets loaded and were consumed, or
                // after a terminal failure/timeout. Never cancel a live request.
                if (releaseAssets)
                {
                    ReleaseModel(vehicle);
                    ReleaseModel(officer);
                }
            }
        }

        private bool CreateUnits(Model vehicleModel, Model officerModel, Ped player)
        {
            if (player == null || !player.Exists())
                return false;
            bool dispatchPursuit = _incident != null
                && _incident.State == LSPDDispatchState.SuspectFleeing;
            bool npcPursuit = _npcResponse != null && _npcResponse.HasBackupAssignment
                && _npcTargetWasFleeing;
            bool movingPursuit = dispatchPursuit || npcPursuit;
            Ped movingTarget = dispatchPursuit
                ? ResolveDispatchPursuitTarget(null)
                : npcPursuit ? _npcResponse.ActiveSubject : null;
            Vehicle movingTargetVehicle = dispatchPursuit
                ? ResolvePursuitVehicle(_incident, movingTarget)
                : npcPursuit
                    ? ResolvePursuitVehicle(movingTarget, _npcResponse.ActiveSubjectVehicle)
                    : null;
            Vector3 destination = _convoy != null && _convoy.Active
                ? ResolveConvoyDestination(_convoy, player)
                : _crimeActivity != null && _crimeActivity.Active
                    ? ResolveCrimeActivityDestination(_crimeActivity, player)
                    : _npcResponse != null && _npcResponse.HasBackupAssignment
                        ? ResolveNpcDestination(_npcResponse, player)
                        : _gangResponse != null && _gangResponse.HasActiveIncident
                            ? ResolveGangDestination(_gangResponse, player)
                        : ResolveDestination(null, player, dispatchPursuit);
            if (destination == Vector3.Zero)
            {
                LogRuntime("POLICE_BACKUP_DRIVE_TARGET_REJECTED",
                    "Reason=NO_VALID_INITIAL_VEHICLE_PATH_NODE");
                return false;
            }
            int requestedUnits = _npcResponse == null ? _settings.UnitsPerRequest : 1;
            if (_gangResponse != null)
                requestedUnits = Math.Min(3, Math.Max(2, _settings.UnitsPerRequest));
            int officersPerUnit = _gangResponse == null
                ? _settings.OfficersPerUnit
                : Math.Max(2, _settings.OfficersPerUnit);
            for (int index = 0; index < requestedUnits; index++)
            {
                Vector3 spawn;
                if (movingPursuit)
                {
                    if (!TryFindPursuitStagingPosition(
                        player, index, movingTarget, movingTargetVehicle, out spawn))
                    {
                        LogRuntime("POLICE_BACKUP_INTERCEPTION_STAGING_REJECTED",
                            "UnitIndex=" + index
                            + "; Target=" + (movingTarget == null ? 0 : movingTarget.Handle)
                            + "; TargetPosition=" + (movingTarget == null
                                ? Vector3.Zero : movingTarget.Position));
                        continue;
                    }
                }
                else
                    spawn = _gangResponse != null
                        ? FindGangStagingPosition(player, index, destination)
                        : _npcResponse != null
                            ? FindNpcStagingPosition(player, destination)
                            : FindStagingPosition(player, index, destination);
                if (spawn == Vector3.Zero)
                {
                    LogRuntime(_gangResponse != null
                        ? "POLICE_BACKUP_GANG_STAGING_REJECTED"
                        : "POLICE_BACKUP_NPC_STAGING_REJECTED",
                        "UnitIndex=" + index
                        + "; Destination=" + destination
                        + "; Reason=NO_VALID_NEARBY_ROAD_POSITION");
                    continue;
                }

                // World.GetNextPositionOnStreet is not a sufficient vehicle
                // spawn guarantee: it can return a sidewalk, grass edge, or
                // pedestrian street position. Convert the staging point to a
                // real vehicle node and retain the node heading before the
                // vehicle is created. No live vehicle is moved during route
                // recovery; this is only spawn validation.
                Vector3 vehicleNode;
                float vehicleNodeHeading;
                if (!TryGetNearbyVehicleRoadNode(
                    spawn,
                    _gangResponse != null ? destination : spawn,
                    _gangResponse != null ? 18f : 24f,
                    out vehicleNode,
                    out vehicleNodeHeading))
                {
                    LogRuntime(_gangResponse != null
                        ? "POLICE_BACKUP_GANG_STAGING_NODE_REJECTED"
                        : "POLICE_BACKUP_STAGING_NODE_REJECTED",
                        "UnitIndex=" + index
                        + "; Candidate=" + spawn
                        + "; Reason=NO_VALID_VEHICLE_PATH_NODE");
                    continue;
                }
                if (_gangResponse != null
                    && !IsGangRoadNodeCompatible(vehicleNode, destination))
                {
                    LogRuntime("POLICE_BACKUP_GANG_STAGING_NODE_REJECTED",
                        "UnitIndex=" + index
                        + "; Candidate=" + vehicleNode
                        + "; Destination=" + destination
                        + "; ElevationDifference="
                        + Math.Abs(vehicleNode.Z - destination.Z).ToString("0.0")
                        + "; Reason=ROAD_NODE_ON_DIFFERENT_LEVEL");
                    continue;
                }
                spawn = vehicleNode;
                // GTA returns the lane heading with the vehicle node.  For
                // Gang support keep that heading instead of rotating the
                // cruiser toward the threat across the road; the latter can
                // place a long vehicle sideways on a slope and make the first
                // emergency task start already obstructed.
                float spawnHeading = _gangResponse != null
                    ? NormalizeHeading(vehicleNodeHeading)
                    : AlignRoadHeadingToTarget(
                        spawn,
                        destination,
                        vehicleNodeHeading,
                        player.Heading);
                Vehicle vehicle = null;
                try
                {
                    vehicle = World.CreateVehicle(vehicleModel, spawn, spawnHeading);
                    if (vehicle == null || !vehicle.Exists())
                        continue;
                    vehicle.IsPersistent = true;
                    try
                    {
                        Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY,
                            vehicle, true, true);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_VEHICLE_MISSION_OWNERSHIP_FAILED", ex);
                    }
                    vehicle.PlaceOnGround();
                    // The spawn point has already been validated against a
                    // vehicle path node above.  Do not run IS_POINT_ON_ROAD
                    // again against the vehicle's post-placement Z/handle:
                    // PlaceOnGround commonly changes the height enough for
                    // that native to reject a valid road node and delete the
                    // only usable Backup unit.  Keep the post-create check
                    // focused on the vehicle remaining near the validated
                    // node instead.
                    Vector3 actualSpawn = vehicle.Position;
                    float spawnDisplacement = actualSpawn.DistanceTo(spawn);
                    if (spawnDisplacement > 8f)
                    {
                        LogRuntime("POLICE_BACKUP_STAGING_PLACEMENT_REJECTED",
                            "UnitIndex=" + index
                            + "; Candidate=" + spawn
                            + "; Actual=" + actualSpawn
                            + "; Displacement=" + spawnDisplacement.ToString("0.0")
                            + "; Reason=CREATED_VEHICLE_MOVED_FROM_VALIDATED_NODE");
                        try { vehicle.Delete(); } catch { }
                        continue;
                    }
                    if (_gangResponse != null
                        && !IsGangVehiclePlacementValid(
                            vehicle, spawn, destination))
                    {
                        LogRuntime("POLICE_BACKUP_GANG_STAGING_SURFACE_REJECTED",
                            "UnitIndex=" + index
                            + "; Candidate=" + spawn
                            + "; Actual=" + actualSpawn
                            + "; Rotation=" + vehicle.Rotation
                            + "; Reason=CREATED_VEHICLE_HAS_EXCESSIVE_SLOPE_OR_HEIGHT");
                        try { vehicle.Delete(); } catch { }
                        continue;
                    }
                    SetEmergencySignals(vehicle, true);
                    Ped driver = vehicle.CreatePedOnSeat(VehicleSeat.Driver, officerModel);
                    if (driver == null || !driver.Exists())
                    {
                        vehicle.Delete();
                        continue;
                    }
                    if (_gangResponse != null)
                        PrepareGangOfficer(driver);
                    else
                        PrepareOfficer(driver);
                    var unit = new BackupUnit
                    {
                        Vehicle = vehicle,
                        Driver = driver,
                        Destination = destination,
                        ReturnDestination = spawn,
                        LastDriveTaskAt = DateTime.MinValue,
                        LastProgressPosition = spawn,
                        LastProgressAt = DateTime.UtcNow,
                        RouteRecoveryCount = 0
                    };
                    unit.Officers.Add(driver);
                    if (officersPerUnit > 1)
                    {
                        Ped partner = vehicle.CreatePedOnSeat(VehicleSeat.RightFront, officerModel);
                        if (partner != null && partner.Exists())
                        {
                            if (_gangResponse != null)
                                PrepareGangOfficer(partner);
                            else
                                PrepareOfficer(partner);
                            unit.Officers.Add(partner);
                        }
                    }
                    try
                    {
                        unit.BackupBlip = vehicle.AddBlip();
                        if (unit.BackupBlip != null && unit.BackupBlip.Exists())
                        {
                            unit.BackupBlip.Name = "Police Backup";
                            unit.BackupBlip.Sprite = BlipSprite.PoliceCar;
                            unit.BackupBlip.Color = BlipColor.Blue;
                            unit.BackupBlip.IsShortRange = false;
                        }
                    }
                    catch (Exception ex) { LogException("POLICE_BACKUP_BLIP_FAILED", ex); }
                    _units.Add(unit);
                    SendTo(unit, destination, true);
                    LogRuntime("POLICE_BACKUP_UNIT_STAGED",
                        "Vehicle=" + vehicle.Handle + "; Driver=" + driver.Handle
                        + "; Spawn=" + spawn + "; Destination=" + destination
                        + "; RoadHeading=" + spawnHeading.ToString("0.0")
                        + (movingPursuit && movingTarget != null
                            ? "; Suspect=" + movingTarget.Handle
                                + "; SpawnTargetDistance="
                                + spawn.DistanceTo(movingTarget.Position).ToString("0.0")
                            : string.Empty));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_BACKUP_UNIT_CREATE_FAILED", ex);
                    try { if (vehicle != null && vehicle.Exists()) vehicle.Delete(); } catch { }
                }
            }
            return _units.Count > 0;
        }

        private void SupportScene(LSPDDispatch dispatch, Ped player)
        {
            List<Ped> threats = dispatch.CurrentSuspects.Where(ped => !ped.IsDead).ToList();
            if (threats.Count == 0)
            {
                // Scene models can still be streaming while a naturally
                // arriving unit reaches the authored Dispatch location. Stay
                // available at the scene until Dispatch itself ends instead
                // of standing down before the group has been created.
                SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }
            if (dispatch.HasSuspectGroup && !ReleaseDriversForGroupSupport())
            {
                // Give the GTA exit task a moment to complete before aiming or
                // fighting. Otherwise a combat task can immediately replace
                // the visible vehicle-exit animation.
                SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }
            bool activeCombat = threats.Any(ped => ped.IsShooting
                || (player != null && player.Exists() && ped.IsInCombatAgainst(player)));
            if (activeCombat)
            {
                if (CanIssueSupportTasks())
                    foreach (BackupUnit unit in _units)
                        foreach (Ped officer in SupportOfficers(unit))
                            AssignCombat(officer, NearestThreat(officer, threats));
                SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }

            if (_settings.AutomaticGroupSupport && dispatch.HasSuspectGroup)
            {
                int ordered = dispatch.RequestBackupGroupCompliance(player);
                if (CanIssueSupportTasks())
                    foreach (BackupUnit unit in _units)
                        foreach (Ped officer in SupportOfficers(unit))
                            AimAtNearestThreat(officer, threats);
                SetState(ordered > 0 ? LSPDBackupAssignmentState.Securing : LSPDBackupAssignmentState.Supporting);
                if (ordered > 0)
                {
                    Notify("~b~POLICE BACKUP~s~\nGroup contained. Secure each compliant suspect to begin custody.");
                    LogRuntime("POLICE_BACKUP_GROUP_CONTAINED", "Ordered=" + ordered);
                }
                return;
            }

            if (CanIssueSupportTasks())
                foreach (BackupUnit unit in _units)
                    foreach (Ped officer in SupportOfficers(unit))
                        AimAtNearestThreat(officer, threats);
            SetState(LSPDBackupAssignmentState.Supporting);
        }

        private void SupportConvoy(LSPDConvoy convoy)
        {
            if (convoy == null || !convoy.Active)
            {
                BeginStandDown("The linked Convoy operation finished.");
                return;
            }

            List<Ped> threats = convoy.ActiveRouteThreats.ToList();
            if (threats.Count == 0)
            {
                // The route threat may not be staged until the transport has
                // actually left the station. Keep the arrived officers in a
                // quiet guard state instead of terminating and respawning
                // them later.
                SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }

            if (CanIssueSupportTasks())
                foreach (BackupUnit unit in _units)
                    foreach (Ped officer in SupportOfficers(unit))
                        AssignCombat(officer, threats.OrderBy(ped => ped.Position.DistanceTo(officer.Position)).First());
            SetState(LSPDBackupAssignmentState.Supporting);
        }

        private void SupportCrimeActivity(LSPDCrimeActivity crimeActivity, Ped player)
        {
            if (crimeActivity == null || !crimeActivity.Active)
            {
                BeginStandDown("The linked Crime Activity finished.");
                return;
            }

            // A meeting scene remains quiet while units travel. Once they have
            // physically arrived and left their cars, Crime Activity decides
            // how its owned criminals react; Backup simply supports that real
            // scene with Police behavior.
            if (!ReleaseDriversForGroupSupport())
            {
                SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }

            crimeActivity.BeginBackupIntervention(player);
            List<Ped> threats = crimeActivity.ActiveCriminals
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                .ToList();
            if (threats.Count == 0)
            {
                SetState(LSPDBackupAssignmentState.AssignmentComplete);
                return;
            }

            if (CanIssueSupportTasks())
            {
                foreach (BackupUnit unit in _units)
                {
                    foreach (Ped officer in SupportOfficers(unit))
                    {
                        Ped target = NearestThreat(officer, threats);
                        if (target != null)
                            AssignCombat(officer, target);
                    }
                }
            }
            SetState(LSPDBackupAssignmentState.Supporting);
        }

        private void LockGangSupportRoute(
            BackupUnit unit,
            LSPDGangAdapterResponse gangResponse,
            Vector3 destination)
        {
            if (unit == null || unit.Vehicle == null || !unit.Vehicle.Exists())
                return;
            if (unit.GangRouteLocked)
                return;

            unit.GangRouteLocked = true;
            unit.Arrived = true;
            SetEmergencySignals(unit.Vehicle, false);
            try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true); }
            catch { }
            try { if (unit.Driver != null && unit.Driver.Exists()) unit.Driver.Task.ClearAll(); }
            catch { }

            if (!unit.GangVehicleArrivalReported)
            {
                unit.GangVehicleArrivalReported = true;
                LogRuntime(
                    "POLICE_BACKUP_GANG_VEHICLE_ARRIVED",
                    "Vehicle=" + unit.Vehicle.Handle
                    + "; Driver=" + (unit.Driver == null ? 0 : unit.Driver.Handle)
                    + "; RoadPosition=" + unit.Vehicle.Position
                    + "; Destination=" + destination
                    + "; Distance=" + unit.Vehicle.Position.DistanceTo(destination).ToString("0.0"));
            }
            LogRuntime(
                "POLICE_BACKUP_GANG_ROUTE_LOCKED",
                "Identity=" + (gangResponse == null ? string.Empty : gangResponse.GangName)
                + "; Turf=" + (gangResponse == null ? string.Empty : gangResponse.TerritoryName)
                + "; Vehicle=" + unit.Vehicle.Handle
                + "; Units=1"
                + "; Destination=" + destination
                + "; VehicleRouting=Finished; FootSupport=Enabled");
        }

        private void SupportGangThreat(
            LSPDGangAdapterResponse gangResponse,
            Ped player,
            DateTime now)
        {
            if (gangResponse == null || !gangResponse.HasActiveIncident)
            {
                BeginStandDown("The linked gang threat finished.");
                return;
            }

            List<Ped> threats = gangResponse.ActiveThreats
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                .GroupBy(ped => ped.Handle)
                .Select(group => group.First())
                .ToList();
            int activeCombatThreatCount = gangResponse.ActiveCombatThreats.Count();
            if (threats.Count != _gangLastThreatCount)
            {
                _gangLastThreatCount = threats.Count;
                LogRuntime(
                    "POLICE_BACKUP_GANG_TARGETS_REFRESHED",
                    "Identity=" + gangResponse.GangName
                    + "; Turf=" + gangResponse.TerritoryName
                    + "; Members=" + threats.Count
                    + "; ActiveCombatMembers=" + gangResponse.ActiveCombatThreats.Count());
            }

            if (threats.Count == 0 || activeCombatThreatCount == 0)
            {
                // Gang Adapter owns a bounded re-engagement grace window.
                // Keep the existing assignment in the local support state
                // instead of sending officers after the player during a brief
                // empty observation sample.
                if (!_gangAssignmentHeldForReengagement)
                {
                    _gangAssignmentHeldForReengagement = true;
                    LogRuntime(
                        "POLICE_BACKUP_GANG_ASSIGNMENT_HELD_FOR_REENGAGEMENT",
                        "Identity=" + gangResponse.GangName
                        + "; Turf=" + gangResponse.TerritoryName
                        + "; ActiveCombatMembers=" + activeCombatThreatCount
                        + "; Reason=TemporaryZeroActiveCombatParticipants");
                }
                SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }

            if (_gangAssignmentHeldForReengagement)
            {
                _gangAssignmentHeldForReengagement = false;
                LogRuntime(
                    "POLICE_BACKUP_GANG_REENGAGED",
                    "Identity=" + gangResponse.GangName
                    + "; Turf=" + gangResponse.TerritoryName
                    + "; Members=" + threats.Count);
            }

            // Only route-locked units participate in the foot-support pass.
            // Another unit may still be driving to the scene independently.
            List<BackupUnit> supportUnits = _units
                .Where(unit => unit != null && unit.GangRouteLocked
                    && IsGangSupportUnitUsable(unit))
                .ToList();
            List<Ped> officers = supportUnits
                .SelectMany(SupportOfficers)
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead)
                .ToList();
            if (officers.Count == 0)
            {
                SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }

            ApplyGangPlayerProtection(player, officers);
            bool refreshTasks = CanIssueSupportTasks();
            if (refreshTasks)
                gangResponse.MaintainBackupEngagement(player);

            // Release only the driver of each route-locked Gang unit. An
            // en-route unit keeps its driver until its own road route ends.
            ReleaseGangDriversForLockedUnits();

            HashSet<int> backupOfficerHandles = new HashSet<int>(
                officers.Select(officer => officer.Handle));
            foreach (BackupUnit unit in supportUnits)
            {
                foreach (Ped officer in SupportOfficers(unit).ToList())
                {
                    if (officer.IsDead)
                        continue;
                    if (officer.IsInVehicle())
                    {
                        if (unit.GangExitRequestedOfficerHandles.Add(officer.Handle))
                        {
                            try
                            {
                                Function.Call(Hash.TASK_LEAVE_VEHICLE,
                                    officer, unit.Vehicle, 0);
                                LogRuntime(
                                    "POLICE_BACKUP_GANG_OFFICER_EXIT_REQUESTED",
                                    "Officer=" + officer.Handle
                                    + "; Vehicle=" + unit.Vehicle.Handle
                                    + "; OnFoot=false");
                            }
                            catch (Exception ex)
                            {
                                LogException("POLICE_BACKUP_GANG_OFFICER_EXIT_FAILED", ex);
                            }
                        }
                        continue;
                    }

                    if (unit.GangExitedOfficerHandles.Add(officer.Handle))
                    {
                        LogRuntime(
                            "POLICE_BACKUP_GANG_OFFICER_EXITED",
                            "Officer=" + officer.Handle
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; Position=" + officer.Position
                            + "; OnFoot=true");
                    }

                    if (unit.GangDeployedOfficerHandles.Add(officer.Handle))
                    {
                        LogRuntime(
                            "POLICE_BACKUP_GANG_OFFICER_DEPLOYED_INDEPENDENTLY",
                            "Officer=" + officer.Handle
                            + "; Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                            + "; OnFoot=true; OtherUnitsMayStillBeEnRoute=true");
                        if (!unit.GangSupportStartedLogged)
                        {
                            unit.GangSupportStartedLogged = true;
                            LogRuntime(
                                "POLICE_BACKUP_GANG_UNIT_SUPPORT_STARTED",
                                "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                                + "; Officer=" + officer.Handle
                                + "; IndependentDeployment=true");
                        }
                    }

                    RepairGangPlayerHostility(unit, officer, player);
                    RepairGangProtectedActorHostility(
                        unit, officer, player, gangResponse);
                    Ped movementTarget = ResolveGangCombatTarget(
                        unit,
                        officer,
                        player,
                        threats,
                        gangResponse,
                        backupOfficerHandles);
                    if (movementTarget == null)
                    {
                        if (unit.GangNoTargetHandles.Add(officer.Handle))
                            LogRuntime(
                                "POLICE_BACKUP_GANG_NO_TARGET_HOLD",
                                "Officer=" + officer.Handle
                                + "; Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                                + "; Position=" + officer.Position
                                + "; Behavior=HoldNearActiveGangScene");
                        continue;
                    }
                }
            }

            foreach (BackupUnit unit in supportUnits)
            {
                foreach (Ped officer in SupportOfficers(unit).ToList())
                {
                    if (officer.IsDead)
                        continue;
                    Ped movementTarget;
                    if (!unit.GangTargets.TryGetValue(officer.Handle, out movementTarget)
                        || movementTarget == null || !movementTarget.Exists()
                        || movementTarget.IsDead)
                        continue;
                    Vector3 supportPosition = movementTarget.Position;
                    if (IsOfficerEngagedWithTarget(officer, movementTarget))
                        continue;
                    float distance = officer.Position.DistanceTo(supportPosition);
                    if (distance > GangFootSupportArrivalDistance)
                    {
                        if (unit.GangFootSupportStartedHandles.Add(officer.Handle))
                        {
                            LogRuntime(
                                "POLICE_BACKUP_GANG_FOOT_SUPPORT_STARTED",
                                "Officer=" + officer.Handle
                                + "; Vehicle=" + unit.Vehicle.Handle
                                + "; SupportPosition=" + supportPosition
                                + "; Distance=" + distance.ToString("0.0")
                                + "; OnFoot=true");
                        }
                        // Reached is a sticky support-area transition. Do not
                        // start the same officer's navigation again merely
                        // because the player moved a few metres.
                        Vector3 previousSupportPosition;
                        bool targetMoved = !unit.GangLastSupportPositions.TryGetValue(
                            officer.Handle, out previousSupportPosition)
                            || previousSupportPosition.DistanceTo(supportPosition)
                                >= GangFootSupportTaskRefreshDistance;
                        if (refreshTasks && (targetMoved
                            || !unit.GangFootSupportReachedHandles.Contains(officer.Handle)))
                        {
                            try
                            {
                                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                    officer,
                                    supportPosition.X,
                                    supportPosition.Y,
                                    supportPosition.Z,
                                    1.65f,
                                    -1,
                                    1.5f,
                                    1,
                                    0f);
                                Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                                unit.GangLastSupportPositions[officer.Handle] = supportPosition;
                            }
                            catch (Exception ex)
                            {
                                LogException("POLICE_BACKUP_GANG_FOOT_SUPPORT_TASK_FAILED", ex);
                            }
                        }
                    }
                    else if (unit.GangFootSupportReachedHandles.Add(officer.Handle))
                    {
                        LogRuntime(
                            "POLICE_BACKUP_GANG_FOOT_SUPPORT_REACHED",
                            "Officer=" + officer.Handle
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; Position=" + officer.Position
                            + "; Distance=" + distance.ToString("0.0"));
                    }
                }
            }

            bool combatConfirmedThisPass = false;
            foreach (BackupUnit unit in supportUnits)
            {
                foreach (Ped officer in SupportOfficers(unit).ToList())
                {
                    if (officer.IsDead || officer.IsInVehicle())
                        continue;

                    RepairGangPlayerHostility(unit, officer, player);
                    RepairGangProtectedActorHostility(
                        unit, officer, player, gangResponse);
                    Ped target = ResolveGangCombatTarget(
                        unit,
                        officer,
                        player,
                        threats,
                        gangResponse,
                        backupOfficerHandles);
                    if (target == null)
                        continue;

                    float targetDistance = officer.Position.DistanceTo(target.Position);
                    if (targetDistance > GangCombatAssignmentDistance)
                        continue;

                    if (refreshTasks && !IsOfficerEngagedWithTarget(officer, target))
                        AssignGangCombat(
                            unit,
                            officer,
                            target,
                            player,
                            gangResponse,
                            threats,
                            backupOfficerHandles);

                    if (IsOfficerEngagedWithTarget(officer, target))
                    {
                        combatConfirmedThisPass = true;
                        if (unit.GangCombatConfirmedHandles.Add(officer.Handle))
                        {
                            LogRuntime(
                                "POLICE_BACKUP_GANG_COMBAT_CONFIRMED",
                                "Officer=" + officer.Handle
                                + "; Vehicle=" + unit.Vehicle.Handle
                                + "; Target=" + target.Handle
                                + "; TargetPosition=" + target.Position
                                + "; Distance=" + targetDistance.ToString("0.0")
                                + "; TargetAttackingPlayer=" + IsGangTargetAttackingPlayer(target, player)
                                + "; OfficerOnFoot=" + !officer.IsInVehicle());
                        }
                    }
                }
            }

            if (combatConfirmedThisPass && !_gangCombatStartedLogged)
            {
                _gangCombatStartedLogged = true;
                Notify("~b~POLICE BACKUP~s~\nGang-threat support is engaging the hostile group.");
                LogRuntime(
                    "POLICE_BACKUP_GANG_COMBAT_STARTED",
                    "Identity=" + gangResponse.GangName
                    + "; Turf=" + gangResponse.TerritoryName
                    + "; Members=" + threats.Count
                    + "; Units=" + _units.Count
                    + "; Officers=" + officers.Count
                    + "; PhysicalConfirmation=true");
            }

            SetState(LSPDBackupAssignmentState.Supporting);
        }

        private void ApplyGangPlayerProtection(Ped player, IEnumerable<Ped> officers)
        {
            if (player == null || !player.Exists() || officers == null)
                return;

            List<Ped> liveOfficers = officers
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead)
                .GroupBy(officer => officer.Handle)
                .Select(group => group.First())
                .ToList();
            if (!EnsureGangProtectionRelationshipGroup(player))
                return;
            foreach (Ped officer in liveOfficers)
            {
                EnsureGangOfficerFriendly(officer, player);
            }

            if (!_gangPlayerProtectionLogged)
            {
                _gangPlayerProtectionLogged = true;
                LogRuntime(
                    "POLICE_BACKUP_GANG_PLAYER_PROTECTION_APPLIED",
                    "Player=" + player.Handle
                    + "; Officers=" + liveOfficers.Count
                    + "; Scope=ActiveGangBackupAssignment"
                    + "; Verification=TemporaryRelationshipGroup"
                    + "; Group=" + _gangProtectionGroupHash);
            }
        }

        private bool EnsureGangProtectionRelationshipGroup(Ped player)
        {
            if (player == null || !player.Exists())
                return false;
            try
            {
                if (_gangProtectionGroupHash == 0)
                {
                    using (OutputArgument groupOutput = new OutputArgument())
                    {
                        Function.Call<bool>(
                            Hash.ADD_RELATIONSHIP_GROUP,
                            "LSIL_GANG_BACKUP",
                            groupOutput);
                        _gangProtectionGroupHash = groupOutput.GetResult<int>();
                    }
                    if (_gangProtectionGroupHash == 0)
                        _gangProtectionGroupHash = unchecked((int)StringHash.AtStringHash(
                            "LSIL_GANG_BACKUP", 0));
                }

                int playerGroup = Function.Call<int>(
                    Hash.GET_PED_RELATIONSHIP_GROUP_HASH, player);
                if (_gangPlayerRelationshipGroupHash != playerGroup)
                {
                    Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                        1, _gangProtectionGroupHash, playerGroup);
                    Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                        1, playerGroup, _gangProtectionGroupHash);
                    Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                        0, _gangProtectionGroupHash, _gangProtectionGroupHash);
                    _gangPlayerRelationshipGroupHash = playerGroup;
                }
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_GANG_PLAYER_PROTECTION_FAILED", ex);
                return false;
            }
        }

        private void EnsureGangOfficerFriendly(Ped officer, Ped player)
        {
            if (officer == null || player == null
                || !officer.Exists() || !player.Exists()
                || _gangProtectionGroupHash == 0)
                return;
            try
            {
                if (!_gangOriginalRelationshipGroups.ContainsKey(officer.Handle))
                {
                    _gangOriginalRelationshipGroups[officer.Handle] = Function.Call<int>(
                        Hash.GET_PED_RELATIONSHIP_GROUP_HASH, officer);
                    Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH,
                        officer, _gangProtectionGroupHash);
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_GANG_OFFICER_RELATIONSHIP_FAILED", ex);
            }
        }

        private void RepairGangPlayerHostility(
            BackupUnit unit,
            Ped officer,
            Ped player)
        {
            if (unit == null || officer == null || player == null
                || !officer.Exists() || !player.Exists())
                return;
            try
            {
                if (!officer.IsInCombatAgainst(player))
                    return;
                try { officer.Task.ClearAll(); } catch { }
                if (EnsureGangProtectionRelationshipGroup(player))
                    EnsureGangOfficerFriendly(officer, player);
                if (unit.GangPlayerHostilityRepairHandles.Add(officer.Handle))
                    LogRuntime(
                        "POLICE_BACKUP_GANG_FRIENDLY_FIRE_REPAIRED",
                        "Officer=" + officer.Handle
                        + "; Target=" + player.Handle
                        + "; Reason=PolicePlayerFriendlyFireRepair");
            }
            catch
            {
            }
        }

        private void RepairGangProtectedActorHostility(
            BackupUnit unit,
            Ped officer,
            Ped player,
            LSPDGangAdapterResponse gangResponse)
        {
            if (unit == null || officer == null || gangResponse == null
                || !officer.Exists() || officer.IsDead)
                return;

            foreach (Ped protectedActor in gangResponse.ProtectedGangActors)
            {
                if (protectedActor == null || !protectedActor.Exists()
                    || protectedActor.IsDead || protectedActor.Handle == officer.Handle
                    || (player != null && player.Exists()
                        && protectedActor.Handle == player.Handle))
                    continue;
                try
                {
                    if (!officer.IsInCombatAgainst(protectedActor))
                        continue;
                    officer.Task.ClearAll();
                    if (player != null && player.Exists()
                        && EnsureGangProtectionRelationshipGroup(player))
                        EnsureGangOfficerFriendly(officer, player);
                    if (unit.GangProtectedActorRepairHandles.Add(officer.Handle))
                        LogRuntime(
                            "POLICE_BACKUP_GANG_FRIENDLY_FIRE_REPAIRED",
                            "Officer=" + officer.Handle
                            + "; ProtectedActor=" + protectedActor.Handle
                            + "; Reason=ProtectedGangActorWasCombatTarget");
                    break;
                }
                catch
                {
                }
            }
        }

        private Ped ResolveGangCombatTarget(
            BackupUnit unit,
            Ped officer,
            Ped player,
            IEnumerable<Ped> threats,
            LSPDGangAdapterResponse gangResponse,
            HashSet<int> backupOfficerHandles)
        {
            if (unit == null || officer == null || player == null
                || threats == null || gangResponse == null)
                return null;

            bool hadPreviousTarget = false;
            int previousTargetHandle = 0;
            Ped current;
            if (unit.GangTargets.TryGetValue(officer.Handle, out current))
            {
                string currentReason;
                if (TryValidateGangCombatTarget(
                    current, player, threats, gangResponse,
                    backupOfficerHandles, out currentReason))
                    return current;

                hadPreviousTarget = true;
                previousTargetHandle = current == null ? 0 : current.Handle;
                if (current != null && current.Exists() && current.IsDead
                    && unit.GangDeadTargetHandles.Add(current.Handle))
                {
                    LogRuntime(
                        "POLICE_BACKUP_GANG_COMBAT_TARGET_DEAD",
                        "Officer=" + officer.Handle
                        + "; Target=" + current.Handle
                        + "; Reason=" + currentReason);
                }
                else
                {
                    LogRuntime(
                        "POLICE_BACKUP_GANG_TARGET_REJECTED",
                        "Officer=" + officer.Handle
                        + "; Target=" + (current == null ? 0 : current.Handle)
                        + "; Reason=" + currentReason);
                    if (currentReason == "TargetIsProtectedPlayerOwnedGangActor")
                        LogRuntime(
                            "POLICE_BACKUP_GANG_PROTECTED_ACTOR_REJECTED",
                            "Officer=" + officer.Handle
                            + "; Target=" + (current == null ? 0 : current.Handle)
                            + "; Reason=" + currentReason);
                }
                unit.GangNoTargetHandles.Remove(officer.Handle);
                unit.GangTargets.Remove(officer.Handle);
            }

            HashSet<int> activeCombatHandles = new HashSet<int>(
                gangResponse.ActiveCombatThreats
                    .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                    .Select(ped => ped.Handle));
            List<Ped> candidates = gangResponse.ActiveCombatThreats
                .Concat(threats)
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                .GroupBy(ped => ped.Handle)
                .Select(group => group.First())
                .Where(ped => activeCombatHandles.Contains(ped.Handle)
                    || IsGangTargetAttackingPlayer(ped, player)
                    || IsGangTargetAttackingBackup(ped)
                    || IsGangParticipantInCombat(ped))
                .OrderBy(ped => IsGangTargetAttackingPlayer(ped, player) ? 0
                    : IsGangTargetAttackingBackup(ped) ? 1
                    : activeCombatHandles.Contains(ped.Handle) ? 2 : 3)
                .ThenBy(ped => ped.Position.DistanceTo(officer.Position))
                .ToList();

            foreach (Ped candidate in candidates)
            {
                string reason;
                if (!TryValidateGangCombatTarget(
                    candidate, player, threats, gangResponse,
                    backupOfficerHandles, out reason))
                {
                    if (reason == "TargetIsProtectedPlayerOwnedGangActor")
                        LogRuntime(
                            "POLICE_BACKUP_GANG_PROTECTED_ACTOR_REJECTED",
                            "Officer=" + officer.Handle
                            + "; Target=" + candidate.Handle
                            + "; Reason=" + reason);
                    continue;
                }

                unit.GangTargets[officer.Handle] = candidate;
                unit.GangNoTargetHandles.Remove(officer.Handle);
                bool attackingPlayer = IsGangTargetAttackingPlayer(candidate, player);
                string targetReason = attackingPlayer
                    ? "AttackingPlayer"
                    : IsGangTargetAttackingBackup(candidate)
                        ? "AttackingBackupOfficer"
                        : activeCombatHandles.Contains(candidate.Handle)
                            ? "ActiveCombatParticipant"
                            : "NearestHostileParticipant";
                LogRuntime(
                    "POLICE_BACKUP_GANG_TARGET_SELECTED",
                    "Officer=" + officer.Handle
                    + "; Target=" + candidate.Handle
                    + "; Reason=" + targetReason
                    + "; Distance=" + officer.Position.DistanceTo(candidate.Position).ToString("0.0")
                    + "; TargetAttackingPlayer=" + attackingPlayer
                    + "; OfficerOnFoot=" + !officer.IsInVehicle());
                if (hadPreviousTarget)
                    LogRuntime(
                        "POLICE_BACKUP_GANG_TARGET_RESELECTED",
                        "Officer=" + officer.Handle
                        + "; PreviousTarget=" + previousTargetHandle
                        + "; Target=" + candidate.Handle
                        + "; Reason=" + targetReason);
                return candidate;
            }

            unit.GangTargets.Remove(officer.Handle);
            return null;
        }

        private static bool IsGangParticipantInCombat(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead)
                return false;
            try { return ped.IsInCombat || ped.IsShooting; }
            catch { return false; }
        }

        private bool TryValidateGangCombatTarget(
            Ped target,
            Ped player,
            IEnumerable<Ped> threats,
            LSPDGangAdapterResponse gangResponse,
            HashSet<int> backupOfficerHandles,
            out string reason)
        {
            reason = string.Empty;
            if (target == null || !target.Exists())
            {
                reason = "TargetUnavailable";
                return false;
            }
            if (target.IsDead)
            {
                reason = "TargetDead";
                return false;
            }
            if (player != null && player.Exists() && target.Handle == player.Handle)
            {
                reason = "TargetIsPolicePlayer";
                return false;
            }
            if (backupOfficerHandles != null && backupOfficerHandles.Contains(target.Handle))
            {
                reason = "TargetIsBackupOfficer";
                return false;
            }
            if (gangResponse.IsProtectedGangActor(target))
            {
                reason = "TargetIsProtectedPlayerOwnedGangActor";
                return false;
            }
            if (threats == null || !threats.Any(ped => ped != null
                && ped.Exists() && ped.Handle == target.Handle))
            {
                reason = "TargetIsNotActiveGangParticipant";
                return false;
            }
            return true;
        }

        private bool IsGangTargetAttackingBackup(Ped target)
        {
            if (target == null || !target.Exists() || target.IsDead)
                return false;
            foreach (BackupUnit unit in _units.Where(IsGangSupportUnitUsable))
                foreach (Ped officer in SupportOfficers(unit))
                    try
                    {
                        if (target.IsInCombatAgainst(officer))
                            return true;
                    }
                    catch
                    {
                    }
            return false;
        }

        private static bool IsGangTargetAttackingPlayer(Ped target, Ped player)
        {
            if (target == null || player == null
                || !target.Exists() || !player.Exists() || target.IsDead)
                return false;
            try
            {
                return target.IsInCombatAgainst(player)
                    || (target.IsShooting
                        && target.Position.DistanceTo(player.Position) <= 90f);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsOfficerEngagedWithTarget(Ped officer, Ped target)
        {
            if (officer == null || target == null
                || !officer.Exists() || !target.Exists()
                || officer.IsDead || target.IsDead)
                return false;
            try { return officer.IsInCombatAgainst(target); }
            catch { return false; }
        }

        private void AssignGangCombat(
            BackupUnit unit,
            Ped officer,
            Ped target,
            Ped player,
            LSPDGangAdapterResponse gangResponse,
            IEnumerable<Ped> threats,
            HashSet<int> backupOfficerHandles)
        {
            string reason;
            if (!TryValidateGangCombatTarget(
                target, player, threats, gangResponse,
                backupOfficerHandles, out reason))
            {
                LogRuntime(
                    "POLICE_BACKUP_GANG_TARGET_REJECTED",
                    "Officer=" + (officer == null ? 0 : officer.Handle)
                    + "; Target=" + (target == null ? 0 : target.Handle)
                    + "; Reason=" + reason);
                if (unit != null && officer != null)
                    unit.GangTargets.Remove(officer.Handle);
                return;
            }

            try
            {
                if (officer.IsInCombatAgainst(player))
                {
                    officer.Task.ClearAll();
                    if (EnsureGangProtectionRelationshipGroup(player))
                        EnsureGangOfficerFriendly(officer, player);
                    LogRuntime(
                        "POLICE_BACKUP_GANG_TARGET_REJECTED",
                        "Officer=" + officer.Handle
                        + "; Target=" + player.Handle
                        + "; Reason=PolicePlayerNeverCombatTarget");
                    return;
                }
                if (EnsureGangProtectionRelationshipGroup(player))
                    EnsureGangOfficerFriendly(officer, player);
                RepairGangProtectedActorHostility(
                    unit, officer, player, gangResponse);
                // Final target gate immediately before GTA receives the combat
                // task. A Gang actor may have changed ownership or streamed
                // state since target selection.
                if (!TryValidateGangCombatTarget(
                    target, player, threats, gangResponse,
                    backupOfficerHandles, out reason))
                {
                    if (reason == "TargetIsProtectedPlayerOwnedGangActor")
                        LogRuntime(
                            "POLICE_BACKUP_GANG_PROTECTED_ACTOR_REJECTED",
                            "Officer=" + officer.Handle
                            + "; Target=" + target.Handle
                            + "; Reason=" + reason);
                    if (unit != null)
                        unit.GangTargets.Remove(officer.Handle);
                    return;
                }
                Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                officer.Task.Combat(target);
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_GANG_COMBAT_TASK_FAILED", ex);
            }
        }

        private void LogGangCombatEnded(
            LSPDGangAdapterResponse gangResponse,
            string reason)
        {
            if (!_gangCombatStartedLogged || _gangCombatEndedLogged)
                return;
            _gangCombatEndedLogged = true;
            LogRuntime(
                "POLICE_BACKUP_GANG_COMBAT_ENDED",
                "Identity=" + (gangResponse == null ? string.Empty : gangResponse.GangName)
                + "; Turf=" + (gangResponse == null ? string.Empty : gangResponse.TerritoryName)
                + "; Reason=" + reason);
        }

        private void ReleaseGangPlayerProtection()
        {
            if (_gangOriginalRelationshipGroups.Count > 0)
            {
                foreach (BackupUnit unit in _units)
                    foreach (Ped officer in SupportOfficers(unit))
                    {
                        int originalGroup;
                        if (!officer.Exists()
                            || !_gangOriginalRelationshipGroups.TryGetValue(
                                officer.Handle, out originalGroup))
                            continue;
                        try
                        {
                            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH,
                                officer, originalGroup);
                        }
                        catch { }
                    }
            }

            if (_gangProtectionGroupHash != 0)
            {
                try
                {
                    Function.Call(Hash.REMOVE_RELATIONSHIP_GROUP,
                        _gangProtectionGroupHash);
                }
                catch { }
            }
            _gangOriginalRelationshipGroups.Clear();
            _gangProtectionGroupHash = 0;
            _gangPlayerRelationshipGroupHash = 0;
        }

        private bool MaintainNpcFootInterception(
            BackupUnit unit,
            Ped subject,
            LSPDNPCResponse npcResponse,
            DateTime now)
        {
            if (!IsUsable(unit) || subject == null || !subject.Exists()
                || subject.IsDead || npcResponse == null)
                return false;

            float vehicleDistance = unit.Vehicle.Position.DistanceTo(subject.Position);
            if (vehicleDistance <= NpcFootInterceptionVehicleDistance)
            {
                unit.Arrived = true;
                unit.NpcVehiclePursuitActive = false;
                SetEmergencySignals(unit.Vehicle, false);
                try
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                    unit.Vehicle.Speed = 0.0f;
                    unit.Driver.Task.ClearAll();
                }
                catch (Exception ex)
                {
                    LogException("POLICE_BACKUP_NPC_FOOT_INTERCEPTION_HOLD_FAILED", ex);
                }
                return false;
            }

            Vector3 interception = ResolveNpcDestination(
                npcResponse, Game.Player.Character);
            if (interception == Vector3.Zero)
                return false;

            // If an earlier arrival let the escort officer leave before the
            // target moved again, bring that same officer back to the owned
            // vehicle before the next road approach. No entity is teleported
            // and no replacement unit is created.
            if (_npcEscortOfficer != null && _npcEscortOfficer.Exists()
                && !_npcEscortOfficer.IsDead
                && !_npcEscortOfficer.IsInVehicle()
                && now >= _nextNpcOfficerTaskAt)
            {
                try
                {
                    _npcEscortOfficer.Task.EnterVehicle(
                        unit.Vehicle, VehicleSeat.RightFront, 12000, 1.0f);
                    _npcOfficerExitRequested = false;
                    _nextNpcOfficerTaskAt = now.AddSeconds(6);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_BACKUP_NPC_FOOT_OFFICER_REENTRY_FAILED", ex);
                }
            }

            unit.Arrived = false;
            unit.NpcVehiclePursuitActive = false;
            SetEmergencySignals(unit.Vehicle, true);
            SendTo(unit, interception, false);

            if (now >= _lastNpcFootInterceptionLogAt.AddMilliseconds(
                NpcVehicleInterceptionLogMilliseconds))
            {
                _lastNpcFootInterceptionLogAt = now;
                LogRuntime("POLICE_BACKUP_NPC_FOOT_INTERCEPTION_TRACKING",
                    "Ped=" + subject.Handle
                    + "; BackupVehicle=" + unit.Vehicle.Handle
                    + "; VehicleDistance=" + vehicleDistance.ToString("0.0")
                    + "; Interception=" + interception
                    + "; OfficerInVehicle="
                    + (_npcEscortOfficer == null || !_npcEscortOfficer.Exists()
                        ? false : _npcEscortOfficer.IsInVehicle()));
            }
            return true;
        }

        private bool MaintainNpcVehicleAssignment(
            LSPDNPCResponse npcResponse,
            Ped player,
            DateTime now)
        {
            if (npcResponse == null || !npcResponse.HasBackupAssignment
                || _npcTransportStarted)
                return false;

            Ped subject = npcResponse.ActiveSubject;
            if (subject == null || !subject.Exists() || subject.IsDead)
                return false;

            BackupUnit unit = _npcTransportUnit;
            if (!IsUsable(unit))
            {
                unit = _units.FirstOrDefault(IsUsable);
                _npcTransportUnit = unit;
            }
            if (!IsUsable(unit))
                return false;

            if (_npcInterventionStarted)
            {
                // Containment has already transferred ownership to the
                // physical NPC handoff.  Never restart vehicle pursuit while
                // the driver is exiting or the officer is securing the
                // subject.
                SupportNpcContact(npcResponse);
                return true;
            }

            Vehicle subjectVehicle = ResolvePursuitVehicle(
                subject, npcResponse.ActiveSubjectVehicle);
            if (subjectVehicle != null && subject.IsInVehicle()
                && subjectVehicle.Handle != unit.Vehicle.Handle)
            {
                bool contained = MaintainNpcVehicleInterception(
                    unit, subject, subjectVehicle, npcResponse, now);
                if (!contained)
                {
                    SetState(LSPDBackupAssignmentState.Intercepting);
                    return true;
                }

                unit.NpcVehiclePursuitActive = false;
                if (!_npcInterventionStarted)
                {
                    if (_npcEscortOfficer == null
                        || !_npcEscortOfficer.Exists()
                        || _npcEscortOfficer.IsDead)
                    {
                        _npcEscortOfficer = unit.Officers.FirstOrDefault(officer =>
                            officer != null && officer.Exists() && !officer.IsDead
                            && officer.Handle != unit.Driver.Handle) ?? unit.Driver;
                    }
                    if (_npcEscortOfficer == null || !_npcEscortOfficer.Exists())
                        return true;

                    LogRuntime("POLICE_BACKUP_NPC_PHYSICAL_CONTAINMENT_STARTED",
                        "Ped=" + subject.Handle
                        + "; SubjectVehicle=" + subjectVehicle.Handle
                        + "; Officer=" + _npcEscortOfficer.Handle
                        + "; BackupVehicle=" + unit.Vehicle.Handle
                        + "; Mode=TrafficVehicleInterception");
                    _npcInterventionStarted = npcResponse.BeginBackupIntervention(
                        _npcEscortOfficer, unit.Vehicle);
                    if (!_npcInterventionStarted)
                        return true;
                    LogRuntime("POLICE_BACKUP_NPC_BLOCKED",
                        "Ped=" + subject.Handle
                        + "; Officer=" + _npcEscortOfficer.Handle
                        + "; VehicleContainment=true");
                }

                // Continue directly into the existing NPC custody choreography
                // so the driver exits, the officer owns the handoff, and the
                // player can open the custody response.  Do not return to the
                // generic vehicle-route loop after containment.
                SupportNpcContact(npcResponse);
                return true;
            }

            // A fleeing driver may have left the vehicle after a collision or
            // after the interception stop.  Let the existing NPC support path
            // handle the foot-side containment without assigning a new road
            // destination or losing the same Backup assignment.
            SupportNpcContact(npcResponse);
            return true;
        }

        private bool MaintainNpcVehicleInterception(
            BackupUnit unit,
            Ped subject,
            Vehicle subjectVehicle,
            LSPDNPCResponse npcResponse,
            DateTime now)
        {
            if (!IsUsable(unit) || subject == null || !subject.Exists()
                || subjectVehicle == null || !subjectVehicle.Exists())
                return false;

            if (unit.NpcVehiclePursuitActive
                && unit.VehicleContainmentStopRequested
                && subjectVehicle.Handle != unit.Vehicle.Handle
                && subjectVehicle.Position.DistanceTo(unit.Vehicle.Position)
                    > NpcVehicleBlockMaximumDistance)
            {
                // The target moved away from a temporary containment attempt.
                // Release the local stop and let the live pursuit resume; do
                // not keep a handbrake/cleared-driver state on the response
                // vehicle while the target is moving again.
                unit.VehicleContainmentStopRequested = false;
                _npcVehicleStoppedAt = DateTime.MinValue;
                _nextNpcVehicleBrakeTaskAt = DateTime.MinValue;
                try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false); }
                catch { }
            }

            float directionX;
            float directionY;
            float targetSpeed;
            GetMovementDirection(subjectVehicle, out directionX, out directionY, out targetSpeed);
            Vector3 delta = unit.Vehicle.Position - subjectVehicle.Position;
            float distance = unit.Vehicle.Position.DistanceTo(subjectVehicle.Position);
            float forwardDistance = delta.X * directionX + delta.Y * directionY;
            float lateralDistance = Math.Abs(delta.X * -directionY + delta.Y * directionX);
            bool inBlockingPosition = forwardDistance >= NpcVehicleBlockMinimumForward
                && forwardDistance <= NpcVehicleBlockMaximumForward
                && lateralDistance <= NpcVehicleBlockLateralTolerance
                && distance <= NpcVehicleBlockMaximumDistance;

            if (!inBlockingPosition)
            {
                unit.NpcVehiclePursuitActive = true;
                if (unit.VehicleContainmentStopRequested)
                {
                    unit.VehicleContainmentStopRequested = false;
                    _npcVehicleStoppedAt = DateTime.MinValue;
                    _nextNpcVehicleBrakeTaskAt = DateTime.MinValue;
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false);
                    }
                    catch { }
                }
                SetEmergencySignals(unit.Vehicle, true);

                // This is deliberately the actual tracked vehicle, not a
                // changing world-coordinate interception point.  Dispatch
                // already uses this task successfully; sharing the same
                // throttled task ownership prevents the NPC route loop and
                // pursuit task from fighting over the Backup driver.
                if (now >= unit.LastDriveTaskAt.AddMilliseconds(TaskRefreshMilliseconds))
                {
                    try
                    {
                        unit.Driver.Task.VehicleChase(subject);
                        unit.LastDriveTaskAt = now;
                        if (!_npcVehiclePursuitStartedLogged)
                        {
                            _npcVehiclePursuitStartedLogged = true;
                            LogRuntime("POLICE_BACKUP_NPC_VEHICLE_PURSUIT_STARTED",
                                "Driver=" + subject.Handle
                                + "; SubjectVehicle=" + subjectVehicle.Handle
                                + "; BackupVehicle=" + unit.Vehicle.Handle
                                + "; Distance=" + distance.ToString("0.0")
                                + "; Mode=LiveVehicleChase");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Keep a bounded, road-validated fallback for a
                        // transient native failure.  It is never the normal
                        // pursuit path and is still handled by SendTo's road
                        // validation instead of a raw subject coordinate.
                        LogException("POLICE_BACKUP_NPC_VEHICLE_PURSUIT_FAILED", ex);
                        Vector3 fallback = ResolveNpcDestination(
                            npcResponse, Game.Player.Character);
                        if (fallback != Vector3.Zero)
                        {
                            SendTo(unit, fallback, false);
                            LogRuntime("POLICE_BACKUP_NPC_VEHICLE_PURSUIT_FALLBACK",
                                "SubjectVehicle=" + subjectVehicle.Handle
                                + "; BackupVehicle=" + unit.Vehicle.Handle
                                + "; RoadTarget=" + fallback);
                        }
                    }
                }
                if (now >= _lastNpcVehicleInterceptionLogAt.AddMilliseconds(
                    NpcVehicleInterceptionLogMilliseconds))
                {
                    _lastNpcVehicleInterceptionLogAt = now;
                    LogRuntime("POLICE_BACKUP_NPC_VEHICLE_INTERCEPTION_TRACKING",
                        "Driver=" + subject.Handle
                        + "; SubjectVehicle=" + subjectVehicle.Handle
                        + "; BackupVehicle=" + unit.Vehicle.Handle
                        + "; Distance=" + distance.ToString("0.0")
                        + "; ForwardDistance=" + forwardDistance.ToString("0.0")
                        + "; LateralDistance=" + lateralDistance.ToString("0.0")
                        + "; TargetSpeed=" + targetSpeed.ToString("0.0")
                        + "; Mode=LiveVehicleChase");
                }
                return false;
            }

            unit.NpcVehiclePursuitActive = true;
            SetEmergencySignals(unit.Vehicle, true);
            if (!unit.VehicleContainmentStopRequested)
            {
                unit.VehicleContainmentStopRequested = true;
                try
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                    unit.Vehicle.Speed = 0.0f;
                    unit.Driver.Task.ClearAll();
                }
                catch (Exception ex)
                {
                    LogException("POLICE_BACKUP_NPC_VEHICLE_BLOCK_FAILED", ex);
                }
                LogRuntime("POLICE_BACKUP_NPC_VEHICLE_BLOCKING_POSITION",
                    "Driver=" + subject.Handle
                    + "; SubjectVehicle=" + subjectVehicle.Handle
                    + "; BackupVehicle=" + unit.Vehicle.Handle
                    + "; Distance=" + distance.ToString("0.0")
                    + "; ForwardDistance=" + forwardDistance.ToString("0.0")
                    + "; LateralDistance=" + lateralDistance.ToString("0.0")
                    + "; Siren=true");
            }

            targetSpeed = Math.Abs(subjectVehicle.Speed);
            if (targetSpeed > DispatchVehicleStoppedSpeed)
            {
                _npcVehicleStoppedAt = DateTime.MinValue;
                if (now >= _nextNpcVehicleBrakeTaskAt)
                {
                    _nextNpcVehicleBrakeTaskAt = now.AddMilliseconds(NpcVehicleBrakeTaskMilliseconds);
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_BRAKE, subjectVehicle, true);
                        Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION,
                            subject, subjectVehicle, 27, NpcVehicleBrakeTaskMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_NPC_VEHICLE_BRAKE_FAILED", ex);
                    }
                    LogRuntime("POLICE_BACKUP_NPC_VEHICLE_BRAKING_REQUESTED",
                        "Driver=" + subject.Handle
                        + "; SubjectVehicle=" + subjectVehicle.Handle
                        + "; BackupVehicle=" + unit.Vehicle.Handle
                        + "; Speed=" + targetSpeed.ToString("0.0"));
                }
                return false;
            }

            if (_npcVehicleStoppedAt == DateTime.MinValue)
                _npcVehicleStoppedAt = now;
            if (now < _npcVehicleStoppedAt.AddMilliseconds(NpcVehicleStopSettleMilliseconds))
                return false;

            if (!_npcVehicleContainmentReported)
            {
                _npcVehicleContainmentReported = true;
                LogRuntime("POLICE_BACKUP_NPC_VEHICLE_CONTAINED",
                    "Driver=" + subject.Handle
                    + "; SubjectVehicle=" + subjectVehicle.Handle
                    + "; BackupVehicle=" + unit.Vehicle.Handle
                    + "; ForwardDistance=" + forwardDistance.ToString("0.0")
                    + "; LateralDistance=" + lateralDistance.ToString("0.0")
                    + "; TargetSpeed=" + targetSpeed.ToString("0.0"));
            }
            return true;
        }

        private void SupportNpcContact(LSPDNPCResponse npcResponse)
        {
            if (npcResponse == null || !npcResponse.HasBackupAssignment)
            {
                BeginStandDown("The linked NPC contact finished.");
                return;
            }

            BackupUnit unit = _npcTransportUnit;
            if (!IsUsable(unit))
            {
                unit = _units.FirstOrDefault(IsUsable);
                _npcTransportUnit = unit;
            }
            if (!IsUsable(unit))
            {
                npcResponse.CancelBackupHandoff("No usable Backup transport unit remained.");
                BeginStandDown("NPC transport unit was unavailable.");
                return;
            }

            EnsureNpcCustodyCollisionProtection(unit);

            if (_npcTransportStarted)
            {
                // This is retained as a safety path for an assignment that
                // entered transport before the player-release flag was set.
                // It intentionally performs only the local departure cleanup;
                // Backup NPC custody no longer simulates a station journey.
                if (!_npcPlayerSessionReleased)
                    _npcPlayerSessionReleased =
                        npcResponse.ReleasePlayerSessionForBackgroundTransport(
                            "BackupCustodyAlreadyLoaded");
                if (_npcPlayerSessionReleased)
                    ProcessNpcBackgroundDeparture(
                        npcResponse, Game.Player.Character, DateTime.UtcNow);
                return;
            }

            Ped subject = npcResponse.ActiveSubject;
            if (subject == null || !subject.Exists() || subject.IsDead)
            {
                npcResponse.CancelBackupHandoff("The citizen was unavailable when Backup arrived.");
                BeginStandDown("NPC contact subject was unavailable.");
                return;
            }

            if (_npcEscortOfficer == null || !_npcEscortOfficer.Exists()
                || _npcEscortOfficer.IsDead)
            {
                _npcEscortOfficer = unit.Officers.FirstOrDefault(officer =>
                    officer != null && officer.Exists() && !officer.IsDead
                    && officer.Handle != unit.Driver.Handle) ?? unit.Driver;
            }
            if (_npcEscortOfficer == null || !_npcEscortOfficer.Exists())
            {
                npcResponse.CancelBackupHandoff("No Backup officer was available for the physical handoff.");
                BeginStandDown("NPC escort officer was unavailable.");
                return;
            }
            if (_npcEscortLastProgressAt == DateTime.MinValue)
            {
                _npcEscortLastPosition = _npcEscortOfficer.Position;
                _npcEscortLastProgressAt = DateTime.UtcNow;
            }

            Vehicle subjectVehicle = ResolvePursuitVehicle(
                subject, npcResponse.ActiveSubjectVehicle);
            // A fleeing foot subject can move a long way while the Backup
            // officer is leaving the car. Keep the existing unit on a fresh,
            // validated road interception until it is close enough for the
            // officer to exit and contain the subject. This prevents the old
            // behavior where the officer left at a stale point and chased on
            // foot while the citizen continued to run away.
            if (_npcTargetWasFleeing && subjectVehicle == null
                && !_npcInterventionStarted
                && MaintainNpcFootInterception(
                    unit, subject, npcResponse, DateTime.UtcNow))
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }
            // A fleeing-foot subject remains marked as originally fleeing for
            // ownership and reporting, but once that subject is physically
            // inside Backup's own vehicle it is no longer a pursuit vehicle.
            // Let AdvanceBackupHandoff confirm the loaded custody state;
            // otherwise the pursuit branch re-intercepts the Backup car with
            // Distance=0 and prevents transport from ever starting.
            if (_npcTargetWasFleeing && subjectVehicle != null && subject.IsInVehicle()
                && !_npcInterventionStarted
                && subjectVehicle.Handle != unit.Vehicle.Handle)
            {
                if (!MaintainNpcVehicleInterception(
                    unit, subject, subjectVehicle, npcResponse, DateTime.UtcNow))
                {
                    SetState(LSPDBackupAssignmentState.Intercepting);
                    return;
                }

                unit.NpcVehiclePursuitActive = false;
                if (!_npcInterventionStarted)
                {
                    LogRuntime("POLICE_BACKUP_NPC_PHYSICAL_CONTAINMENT_STARTED",
                        "Ped=" + subject.Handle + "; SubjectVehicle=" + subjectVehicle.Handle
                        + "; Officer=" + _npcEscortOfficer.Handle
                        + "; BackupVehicle=" + unit.Vehicle.Handle);
                    _npcInterventionStarted = npcResponse.BeginBackupIntervention(
                        _npcEscortOfficer, unit.Vehicle);
                    if (!_npcInterventionStarted)
                        return;
                    LogRuntime("POLICE_BACKUP_NPC_BLOCKED",
                        "Ped=" + subject.Handle + "; Officer=" + _npcEscortOfficer.Handle
                        + "; VehicleContainment=true");
                }
            }
            else if (!_npcTargetWasFleeing && !_npcInterventionStarted)
            {
                _npcInterventionStarted = npcResponse.BeginBackupIntervention(
                    _npcEscortOfficer, unit.Vehicle);
                if (!_npcInterventionStarted)
                    return;
                LogRuntime("POLICE_BACKUP_NPC_BLOCKED",
                    "Ped=" + subject.Handle + "; Officer=" + _npcEscortOfficer.Handle);
            }

            // The transport vehicle is a stationary handoff point until the
            // citizen is confirmed inside. Do not let the driver roll away
            // while the escort officer is navigating the physical scene.
            if (!_npcTransportStarted)
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                    unit.Vehicle.Speed = 0.0f;
                }
                catch (Exception ex) { LogException("POLICE_BACKUP_NPC_VEHICLE_HOLD_FAILED", ex); }
            }

            if (_npcEscortOfficer.IsInVehicle())
            {
                if (!_npcOfficerExitRequested || DateTime.UtcNow >= _nextNpcOfficerTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            _npcEscortOfficer, unit.Vehicle, 0);
                        _npcOfficerExitRequested = true;
                        _nextNpcOfficerTaskAt = DateTime.UtcNow.AddSeconds(6);
                    }
                    catch (Exception ex) { LogException("POLICE_BACKUP_NPC_OFFICER_EXIT_FAILED", ex); }
                }
                SetState(LSPDBackupAssignmentState.Securing);
                return;
            }

            bool prisonerLoading = npcResponse.IsBackupPrisonerLoading;
            float officerDistance = _npcEscortOfficer.Position.DistanceTo(subject.Position);
            if (!prisonerLoading && officerDistance > 3.2f)
            {
                DateTime now = DateTime.UtcNow;
                if (TryRecoverStalledNpcEscort(_npcEscortOfficer, subject, now))
                {
                    SetState(LSPDBackupAssignmentState.Securing);
                    return;
                }
                if (HasExhaustedNpcEscortRecovery(now))
                {
                    string escortFailure = "Backup escort route remained blocked after "
                        + MaximumNpcEscortRecoveries + " bounded recovery attempt(s)."
                        + " Officer=" + _npcEscortOfficer.Handle
                        + "; Subject=" + subject.Handle;
                    LogRuntime("POLICE_BACKUP_NPC_ESCORT_ROUTE_FAILED", escortFailure);
                    npcResponse.CancelBackupHandoff(escortFailure);
                    BeginStandDown(escortFailure);
                    return;
                }
                if (DateTime.UtcNow >= _nextNpcOfficerTaskAt)
                {
                    try
                    {
                        if (_npcTargetWasFleeing)
                        {
                            // Track the actual fleeing entity instead of a
                            // stale coordinate. GTA can recalculate around
                            // moving obstacles while the officer closes in.
                            Function.Call(Hash.TASK_GO_TO_ENTITY,
                                _npcEscortOfficer, subject, -1, 2.2f,
                                3.2f, 1073741824, 0);
                        }
                        else
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                _npcEscortOfficer,
                                subject.Position.X, subject.Position.Y, subject.Position.Z,
                                1.2f, -1, 1.5f, 0, 0f);
                        }
                        _nextNpcOfficerTaskAt = DateTime.UtcNow.AddSeconds(6);
                    }
                    catch (Exception ex) { LogException("POLICE_BACKUP_NPC_OFFICER_APPROACH_FAILED", ex); }
                }
                SetState(LSPDBackupAssignmentState.Securing);
                return;
            }

            if (_npcTargetWasFleeing && !_npcInterventionStarted)
            {
                LogRuntime("POLICE_BACKUP_NPC_PHYSICAL_CONTAINMENT_STARTED",
                    "Ped=" + subject.Handle + "; Officer=" + _npcEscortOfficer.Handle
                    + "; Distance=" + officerDistance.ToString("0.0"));
                _npcInterventionStarted = npcResponse.BeginBackupIntervention(
                    _npcEscortOfficer, unit.Vehicle);
                if (!_npcInterventionStarted)
                    return;
                LogRuntime("POLICE_BACKUP_NPC_BLOCKED",
                    "Ped=" + subject.Handle + "; Officer=" + _npcEscortOfficer.Handle);
            }

            if (!npcResponse.AdvanceBackupHandoff(_npcEscortOfficer, unit.Vehicle))
            {
                // Keep the officer visually involved while NPC Response owns
                // the subject's cuff, walk, and vehicle-entry tasks.
                if (!npcResponse.IsBackupPrisonerLoading
                    && !npcResponse.IsBackupEscortMovementActive
                    && DateTime.UtcNow >= _nextNpcOfficerTaskAt)
                {
                    try
                    {
                        if (npcResponse.IsBackupContainmentAwaitingPlayer
                            && !subject.IsInVehicle())
                        {
                            AimAt(_npcEscortOfficer, subject);
                            if (!_npcContainmentAimLogged)
                            {
                                _npcContainmentAimLogged = true;
                                LogRuntime("POLICE_BACKUP_NPC_CONTAINMENT_AIMING",
                                    "Driver=" + subject.Handle
                                    + "; Officer=" + _npcEscortOfficer.Handle
                                    + "; PlayerHandoffRequired=true");
                            }
                        }
                        else
                            _npcEscortOfficer.Task.LookAt(subject, 4000);
                    }
                    catch (Exception ex) { LogException("POLICE_BACKUP_NPC_GUARD_FAILED", ex); }
                    _nextNpcOfficerTaskAt = DateTime.UtcNow.AddSeconds(6);
                }
                SetState(LSPDBackupAssignmentState.Securing);
                return;
            }

            if (unit.Driver.CurrentVehicle == null || !unit.Driver.CurrentVehicle.Exists()
                || unit.Driver.CurrentVehicle.Handle != unit.Vehicle.Handle)
            {
                if (DateTime.UtcNow >= _nextNpcOfficerTaskAt)
                {
                    try
                    {
                        unit.Driver.Task.EnterVehicle(
                            unit.Vehicle, VehicleSeat.Driver, 12000, 1.0f);
                        _nextNpcOfficerTaskAt = DateTime.UtcNow.AddSeconds(6);
                    }
                    catch (Exception ex) { LogException("POLICE_BACKUP_NPC_DRIVER_ENTRY_FAILED", ex); }
                }
                SetState(LSPDBackupAssignmentState.Securing);
                return;
            }

            // When the escort is the passenger officer, let that officer
            // physically re-enter before the driver leaves. If GTA cannot find
            // a seat within the bounded wait, the driver can still transport
            // the secured citizen and the assignment will not remain stuck.
            if (_npcEscortOfficer.Handle != unit.Driver.Handle
                && (_npcEscortOfficer.CurrentVehicle == null
                    || !_npcEscortOfficer.CurrentVehicle.Exists()
                    || _npcEscortOfficer.CurrentVehicle.Handle != unit.Vehicle.Handle)
                && (_npcEscortEntryStartedAt == DateTime.MinValue
                    || DateTime.UtcNow < _npcEscortEntryStartedAt.AddSeconds(12)))
            {
                if (_npcEscortEntryStartedAt == DateTime.MinValue)
                    _npcEscortEntryStartedAt = DateTime.UtcNow;
                if (DateTime.UtcNow >= _nextNpcOfficerTaskAt)
                {
                    try
                    {
                        _npcEscortOfficer.Task.EnterVehicle(
                            unit.Vehicle, VehicleSeat.RightFront, 10000, 1.0f);
                        _nextNpcOfficerTaskAt = DateTime.UtcNow.AddSeconds(6);
                    }
                    catch (Exception ex) { LogException("POLICE_BACKUP_NPC_ESCORT_ENTRY_FAILED", ex); }
                }
                SetState(LSPDBackupAssignmentState.Securing);
                return;
            }

            _npcRouteRetargetCount = 0;
            _npcRouteOverride = Vector3.Zero;
            _npcStationRouteDestination = Vector3.Zero;
            try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false); } catch { }
            npcResponse.MarkBackupTransportStarted();
            _npcTransportStarted = true;
            _npcBackgroundDepartureOrigin = unit.Vehicle.Position;
            _npcBackgroundDepartureDeadline = DateTime.UtcNow.AddSeconds(
                NpcBackgroundDepartureSeconds);
            _npcBackgroundDepartureDestination =
                ResolveNpcBackgroundDepartureDestination(unit, Game.Player.Character);
            _npcPlayerSessionReleased =
                npcResponse.ReleasePlayerSessionForBackgroundTransport(
                    "BackupCustodyLoaded");
            unit.Arrived = false;
            bool departureTaskIssued = _npcBackgroundDepartureDestination != Vector3.Zero
                && SendTo(unit, _npcBackgroundDepartureDestination, true);
            if (!departureTaskIssued)
                LogRuntime("POLICE_BACKUP_NPC_BACKGROUND_DEPARTURE_TASK_UNAVAILABLE",
                    "Vehicle=" + unit.Vehicle.Handle
                    + "; Destination=" + _npcBackgroundDepartureDestination
                    + "; Fallback=BoundedCleanup");
            SetState(LSPDBackupAssignmentState.Supporting);
            LogRuntime("POLICE_BACKUP_NPC_DEPARTING_PLAYER_SCENE",
                "Vehicle=" + unit.Vehicle.Handle
                + "; Destination=" + _npcBackgroundDepartureDestination
                + "; TaskIssued=" + departureTaskIssued
                + "; PlayerSessionReleased=" + _npcPlayerSessionReleased);
        }

        private void ProcessNpcBackgroundDeparture(
            LSPDNPCResponse npcResponse,
            Ped player,
            DateTime now)
        {
            BackupUnit unit = _npcTransportUnit;
            if (!IsUsable(unit))
            {
                npcResponse.CompleteBackupBackgroundDeparture(
                    "Backup unit was unavailable after the citizen left the scene.");
                BeginNpcBackgroundCleanup(
                    "Backup unit was unavailable after custody transfer.");
                return;
            }

            if (!npcResponse.IsLoadedInBackupVehicle)
            {
                npcResponse.CompleteBackupBackgroundDeparture(
                    "The citizen was no longer confirmed inside the departing Backup vehicle.");
                ReleaseNpcCustodyCollisionProtection(unit);
                BeginNpcBackgroundCleanup(
                    "Loaded Backup custody could not be confirmed during departure.");
                return;
            }

            float playerDistance = player == null || !player.Exists()
                ? float.MaxValue
                : unit.Vehicle.Position.DistanceTo(player.Position);
            bool playerIsFarAway = playerDistance >= DefaultSafeCleanupDistance;
            bool departureWindowExpired = _npcBackgroundDepartureDeadline != DateTime.MinValue
                && now >= _npcBackgroundDepartureDeadline;
            if (playerIsFarAway || departureWindowExpired)
            {
                string reason = playerIsFarAway
                    ? "Backup vehicle left the player's active scene."
                    : "Bounded Backup departure window completed.";
                npcResponse.CompleteBackupBackgroundDeparture(reason);
                ReleaseNpcCustodyCollisionProtection(unit);
                _npcDeliveryCompletedAt = now;
                BeginNpcBackgroundCleanup(reason);
                return;
            }

            // The departure task is intentionally not refreshed here. Once the
            // loaded unit has been sent away, GTA owns the short natural drive;
            // a new task is issued only by a future, explicit assignment.
            SetState(LSPDBackupAssignmentState.Supporting);
        }

        private void BeginNpcBackgroundCleanup(string reason)
        {
            if (!Active || _state == LSPDBackupAssignmentState.StandingDown)
                return;

            _npcBackgroundCleanupRequested = true;
            _standDownStartedAt = DateTime.UtcNow;
            foreach (BackupUnit unit in _units)
                if (IsUsable(unit))
                {
                    ReleaseNpcCustodyCollisionProtection(unit);
                    SetEmergencySignals(unit.Vehicle, false);
                }
            SetState(LSPDBackupAssignmentState.StandingDown);
            LogRuntime("POLICE_BACKUP_NPC_BACKGROUND_CLEANUP_STARTED",
                reason ?? string.Empty);
        }

        private Vector3 ResolveNpcBackgroundDepartureDestination(
            BackupUnit unit,
            Ped player)
        {
            if (!IsUsable(unit))
                return Vector3.Zero;

            Vector3 origin = unit.Vehicle.Position;
            Vector3 returnDestination = unit.ReturnDestination;
            bool returnPointIsUsable = returnDestination != Vector3.Zero
                && returnDestination.DistanceTo(origin)
                    >= NpcBackgroundDepartureMinimumDistance
                && (player == null || !player.Exists()
                    || returnDestination.DistanceTo(player.Position) >= 45f);
            if (returnPointIsUsable)
                return returnDestination;

            float directionX;
            float directionY;
            float unusedSpeed;
            GetMovementDirection(unit.Vehicle, out directionX, out directionY, out unusedSpeed);
            if (player != null && player.Exists())
            {
                Vector3 away = origin - player.Position;
                float awayLength = (float)Math.Sqrt(away.X * away.X + away.Y * away.Y);
                if (awayLength >= 0.5f)
                {
                    directionX = away.X / awayLength;
                    directionY = away.Y / awayLength;
                }
            }

            float[] distances = { 60f, 90f, 120f };
            foreach (float distance in distances)
            {
                Vector3 candidate = new Vector3(
                    origin.X + directionX * distance,
                    origin.Y + directionY * distance,
                    origin.Z);
                Vector3 road;
                float roadHeading;
                if (!TryGetNearbyVehicleRoadNode(
                    candidate,
                    origin,
                    75f,
                    out road,
                    out roadHeading))
                    continue;
                if (road.DistanceTo(origin) < NpcBackgroundDepartureMinimumDistance)
                    continue;
                if (player != null && player.Exists()
                    && road.DistanceTo(player.Position) < 35f)
                    continue;
                return road;
            }

            // The spawn node was already validated by the existing Backup
            // staging path. It is the only safe fallback when a short outward
            // departure node cannot be sampled.
            return returnDestination;
        }

        private void EnsureNpcCustodyCollisionProtection(BackupUnit unit)
        {
            if (_npcResponse == null || unit == null
                || unit.NpcCustodyCollisionProtectionApplied)
                return;
            try
            {
                SetNpcCollisionProof(unit.Vehicle, true);
                foreach (Ped officer in unit.Officers)
                    SetNpcCollisionProof(officer, true);
                unit.NpcCustodyCollisionProtectionApplied = true;
                LogRuntime("POLICE_BACKUP_NPC_HANDOFF_COLLISION_GUARD",
                    "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                    + "; Officers=" + unit.Officers.Count
                    + "; CollisionProof=true");
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_HANDOFF_COLLISION_GUARD_FAILED", ex);
            }
        }

        private void ReleaseNpcCustodyCollisionProtection(BackupUnit unit)
        {
            if (unit == null || !unit.NpcCustodyCollisionProtectionApplied)
                return;
            try
            {
                SetNpcCollisionProof(unit.Vehicle, false);
                foreach (Ped officer in unit.Officers)
                    SetNpcCollisionProof(officer, false);
                unit.NpcCustodyCollisionProtectionApplied = false;
                LogRuntime("POLICE_BACKUP_NPC_HANDOFF_COLLISION_GUARD_RELEASED",
                    "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle));
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_HANDOFF_COLLISION_GUARD_RELEASE_FAILED", ex);
            }
        }

        private static void SetNpcCollisionProof(Vehicle vehicle, bool enabled)
        {
            if (vehicle == null || !vehicle.Exists())
                return;
            Function.Call(Hash.SET_ENTITY_PROOFS,
                vehicle, false, false, false, enabled, false, false, false, false);
        }

        private static void SetNpcCollisionProof(Ped ped, bool enabled)
        {
            if (ped == null || !ped.Exists())
                return;
            Function.Call(Hash.SET_ENTITY_PROOFS,
                ped, false, false, false, enabled, false, false, false, false);
        }

        private bool ReleaseDriversForGroupSupport()
        {
            bool everyDriverReady = true;
            foreach (BackupUnit unit in _units)
            {
                if (!IsUsable(unit))
                    continue;
                Ped driver = unit.Driver;
                if (!unit.DriverReleasedForGroupSupport)
                {
                    unit.DriverReleasedForGroupSupport = true;
                    try { Function.Call(Hash.TASK_LEAVE_VEHICLE, driver, unit.Vehicle, 0); }
                    catch (Exception ex) { LogException("POLICE_BACKUP_GROUP_DRIVER_EXIT_FAILED", ex); }
                }
                if (driver.IsInVehicle())
                    everyDriverReady = false;
            }
            return everyDriverReady;
        }

        private void ReleaseGangDriversForLockedUnits()
        {
            foreach (BackupUnit unit in _units.Where(item => item != null
                && item.GangRouteLocked))
            {
                if (unit.Driver == null || !unit.Driver.Exists() || unit.Driver.IsDead
                    || unit.Vehicle == null || !unit.Vehicle.Exists())
                    continue;
                if (!unit.DriverReleasedForGroupSupport)
                {
                    unit.DriverReleasedForGroupSupport = true;
                    try
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            unit.Driver, unit.Vehicle, 0);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_GANG_DRIVER_EXIT_FAILED", ex);
                    }
                }
            }
        }

        private bool MaintainDispatchVehicleInterception(
            Ped suspect,
            Vehicle suspectVehicle,
            Vector3 roadDestination,
            DateTime now)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspectVehicle == null || !suspectVehicle.Exists())
                return false;

            if (_dispatchVehiclePursuitHandle != suspectVehicle.Handle)
            {
                _dispatchVehiclePursuitHandle = suspectVehicle.Handle;
                _dispatchVehiclePursuitTarget = suspectVehicle;
                _dispatchVehicleStoppedAt = DateTime.MinValue;
                _nextDispatchVehicleExitTaskAt = DateTime.MinValue;
                _dispatchVehicleExitRequested = false;
                foreach (BackupUnit unit in _units)
                    if (unit != null)
                    {
                        unit.VehiclePursuitLead = false;
                        unit.VehicleContainmentStopRequested = false;
                        unit.InterceptionExitRequested = false;
                        unit.NextInterceptionExitTaskAt = DateTime.MinValue;
                        unit.InterceptionAreaReported = false;
                    }
            }

            BackupUnit leadUnit = _units.FirstOrDefault(unit =>
                IsUsable(unit) && unit.VehiclePursuitLead);
            if (!IsUsable(leadUnit))
            {
                leadUnit = _units.Where(IsUsable)
                    .OrderBy(unit => unit.Vehicle.Position.DistanceTo(suspectVehicle.Position))
                    .FirstOrDefault();
                if (leadUnit != null)
                    leadUnit.VehiclePursuitLead = true;
            }
            if (!IsUsable(leadUnit))
                return false;

            if (_dispatchVehicleExitRequested)
            {
                if (suspect.IsInVehicle())
                {
                    if (now >= _nextDispatchVehicleExitTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_LEAVE_VEHICLE,
                                suspect, suspectVehicle, 0);
                            _nextDispatchVehicleExitTaskAt = now.AddSeconds(3);
                            LogRuntime("POLICE_BACKUP_SUSPECT_VEHICLE_EXIT_RETRY",
                                "Ped=" + suspect.Handle + "; Vehicle=" + suspectVehicle.Handle);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_SUSPECT_VEHICLE_EXIT_FAILED", ex);
                            _nextDispatchVehicleExitTaskAt = now.AddSeconds(3);
                        }
                    }
                    SetState(LSPDBackupAssignmentState.Intercepting);
                    return true;
                }

                leadUnit.InterceptionExitRequested = true;
                leadUnit.Arrived = true;
                leadUnit.Destination = leadUnit.Vehicle.Position;
                leadUnit.NextInterceptionExitTaskAt = DateTime.MinValue;
                SetEmergencySignals(leadUnit.Vehicle, false);
                _dispatchVehicleExitRequested = false;
                _dispatchVehicleStoppedAt = DateTime.MinValue;
                _dispatchVehiclePursuitHandle = 0;
                _dispatchVehiclePursuitTarget = null;
                LogRuntime("POLICE_BACKUP_SUSPECT_LEFT_VEHICLE",
                    "Ped=" + suspect.Handle + "; FormerVehicle=" + suspectVehicle.Handle
                    + "; BackupVehicle=" + leadUnit.Vehicle.Handle);
                return false;
            }

            float targetSpeed = Math.Abs(suspectVehicle.Speed);
            float leadDistance = leadUnit.Vehicle.Position.DistanceTo(suspectVehicle.Position);
            bool targetStopped = targetSpeed <= DispatchVehicleStoppedSpeed;
            if (targetStopped)
            {
                if (_dispatchVehicleStoppedAt == DateTime.MinValue)
                    _dispatchVehicleStoppedAt = now;
            }
            else
                _dispatchVehicleStoppedAt = DateTime.MinValue;

            if ((!targetStopped || leadDistance > DispatchVehicleCloseDistance)
                && leadUnit.VehicleContainmentStopRequested)
            {
                leadUnit.VehicleContainmentStopRequested = false;
                SetEmergencySignals(leadUnit.Vehicle, true);
                try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, leadUnit.Vehicle, false); } catch { }
            }

            if (targetStopped && leadDistance <= DispatchVehicleCloseDistance)
            {
                if (!leadUnit.VehicleContainmentStopRequested)
                {
                    leadUnit.VehicleContainmentStopRequested = true;
                    SetEmergencySignals(leadUnit.Vehicle, false);
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, leadUnit.Vehicle, true);
                        leadUnit.Driver.Task.ClearAll();
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_VEHICLE_CONTAINMENT_STOP_FAILED", ex);
                    }
                }
                if (now < _dispatchVehicleStoppedAt.AddMilliseconds(1200))
                {
                    SetState(LSPDBackupAssignmentState.Intercepting);
                    return true;
                }
                if (!leadUnit.InterceptionAreaReported)
                {
                    leadUnit.InterceptionAreaReported = true;
                    LogRuntime("POLICE_BACKUP_INTERCEPTION_AREA_REACHED",
                        "Vehicle=" + leadUnit.Vehicle.Handle
                        + "; Suspect=" + suspect.Handle
                        + "; SuspectVehicle=" + suspectVehicle.Handle
                        + "; SuspectSpeed=" + targetSpeed.ToString("0.0")
                        + "; Distance=" + leadDistance.ToString("0.0"));
                }
                if (!_dispatchContainmentStartedLogged)
                {
                    _dispatchContainmentStartedLogged = true;
                    LogRuntime("POLICE_BACKUP_PHYSICAL_CONTAINMENT_STARTED",
                        "Suspect=" + suspect.Handle
                        + "; SuspectVehicle=" + suspectVehicle.Handle
                        + "; BackupVehicle=" + leadUnit.Vehicle.Handle
                        + "; TargetStopped=true");
                }
                if (now >= _nextDispatchVehicleExitTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            suspect, suspectVehicle, 0);
                        _dispatchVehicleExitRequested = true;
                        _nextDispatchVehicleExitTaskAt = now.AddSeconds(3);
                        LogRuntime("POLICE_BACKUP_SUSPECT_EXIT_REQUESTED",
                            "Ped=" + suspect.Handle + "; Vehicle=" + suspectVehicle.Handle
                            + "; BackupOfficer=" + leadUnit.Driver.Handle);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_SUSPECT_VEHICLE_EXIT_FAILED", ex);
                        _nextDispatchVehicleExitTaskAt = now.AddSeconds(3);
                    }
                }
                SetState(LSPDBackupAssignmentState.Intercepting);
                return true;
            }

            foreach (BackupUnit unit in _units.Where(IsUsable))
            {
                SetEmergencySignals(unit.Vehicle, true);
                if (unit == leadUnit && leadDistance <= 160f)
                {
                    if (now >= unit.LastDriveTaskAt.AddMilliseconds(TaskRefreshMilliseconds))
                    {
                        try
                        {
                            unit.Driver.Task.VehicleChase(suspect);
                            unit.LastDriveTaskAt = now;
                            LogRuntime("POLICE_BACKUP_VEHICLE_PURSUIT",
                                "Suspect=" + suspect.Handle
                                + "; SuspectVehicle=" + suspectVehicle.Handle
                                + "; BackupVehicle=" + unit.Vehicle.Handle
                                + "; Distance=" + leadDistance.ToString("0.0"));
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_VEHICLE_PURSUIT_FAILED", ex);
                            SendTo(unit, roadDestination, false);
                        }
                    }
                }
                else
                    SendTo(unit, roadDestination, false);
            }

            SetState(LSPDBackupAssignmentState.Intercepting);
            return true;
        }

        private void LogDispatchInterceptionTracking(
            Ped target,
            Vehicle targetVehicle,
            DateTime now)
        {
            if (target == null || !target.Exists()
                || now < _lastDispatchInterceptionTrackingAt.AddSeconds(5))
                return;
            _lastDispatchInterceptionTrackingAt = now;
            LogRuntime("POLICE_BACKUP_INTERCEPTION_TRACKING",
                "Backup tracking fleeing suspect; Ped=" + target.Handle
                + "; Position=" + target.Position
                + "; Mode=" + (targetVehicle == null ? "Foot" : "Vehicle")
                + (targetVehicle == null ? string.Empty
                    : "; Vehicle=" + targetVehicle.Handle
                        + "; Speed=" + Math.Abs(targetVehicle.Speed).ToString("0.0")));
        }

        private void ObserveDispatchFinalArrest(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return;
            if (incident.PlayerHandcuffInProgress && !_dispatchPlayerFinalArrestStartedLogged)
            {
                _dispatchPlayerFinalArrestStartedLogged = true;
                LogRuntime("POLICE_BACKUP_PLAYER_FINAL_ARREST_STARTED",
                    "Ped=" + incident.PlayerHandcuffTargetHandle
                    + "; Owner=Player; BackupCustody=false");
            }
            if (incident.State == LSPDDispatchState.Arrested
                && _dispatchPlayerFinalArrestStartedLogged
                && !_dispatchPlayerFinalArrestCompletedLogged)
            {
                _dispatchPlayerFinalArrestCompletedLogged = true;
                LogRuntime("POLICE_BACKUP_PLAYER_FINAL_ARREST_COMPLETED",
                    "Incident=" + incident.Id + "; Owner=DispatchPlayerHandoff");
            }
        }

        private void ResetDispatchInterceptionTracking()
        {
            _lastDispatchInterceptionTrackingAt = DateTime.MinValue;
            _dispatchVehicleStoppedAt = DateTime.MinValue;
            _nextDispatchVehicleExitTaskAt = DateTime.MinValue;
            _nextDispatchInterceptionApproachTaskAt = DateTime.MinValue;
            _dispatchVehiclePursuitHandle = 0;
            _dispatchVehiclePursuitTarget = null;
            _dispatchVehicleExitRequested = false;
            _dispatchContainmentStartedLogged = false;
            _dispatchComplianceRequestedLogged = false;
            _dispatchSuspectCompliantLogged = false;
            _dispatchPlayerFinalArrestStartedLogged = false;
            _dispatchPlayerFinalArrestCompletedLogged = false;
        }

        private void SupportInterception(LSPDDispatch dispatch, Ped player)
        {
            Ped target = ResolveDispatchPursuitTarget(dispatch);
            if (target == null || !target.Exists() || target.IsDead)
            {
                BeginStandDown("The fleeing Dispatch suspect no longer exists.");
                return;
            }

            Vehicle targetVehicle = ResolvePursuitVehicle(_incident, target);
            if (targetVehicle != null && target.IsInVehicle())
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            if (!ReleaseInterceptionOfficers(DateTime.UtcNow))
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            List<Ped> officers = _units.SelectMany(SupportOfficers)
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead)
                .ToList();
            if (officers.Count == 0)
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            Ped interceptor = officers
                .OrderBy(officer => officer.Position.DistanceTo(target.Position))
                .FirstOrDefault();
            float officerDistance = interceptor == null
                ? float.MaxValue : interceptor.Position.DistanceTo(target.Position);
            if (!_dispatchContainmentStartedLogged
                && officerDistance <= DispatchOfficerContainmentStartDistance)
            {
                _dispatchContainmentStartedLogged = true;
                LogRuntime("POLICE_BACKUP_PHYSICAL_CONTAINMENT_STARTED",
                    "Suspect=" + target.Handle
                    + "; Interceptor=" + (interceptor == null ? 0 : interceptor.Handle)
                    + "; Distance=" + officerDistance.ToString("0.0"));
            }

            if (officerDistance > DispatchOfficerContainmentDistance)
            {
                DateTime now = DateTime.UtcNow;
                if (now >= _nextDispatchInterceptionApproachTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_GO_TO_ENTITY,
                            interceptor, target, -1, 1.5f, 3.2f, 1073741824, 0);
                        _nextDispatchInterceptionApproachTaskAt = now.AddSeconds(3);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_INTERCEPTOR_APPROACH_FAILED", ex);
                        _nextDispatchInterceptionApproachTaskAt = now.AddSeconds(3);
                    }
                }

                if (CanIssueSupportTasks())
                    foreach (Ped officer in officers.Where(value => value.Handle != interceptor.Handle))
                        if (officer.Position.DistanceTo(target.Position) <= 75f)
                            AimAt(officer, target);
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            if (!_dispatchComplianceRequestedLogged)
            {
                _dispatchComplianceRequestedLogged = true;
                LogRuntime("POLICE_BACKUP_SUSPECT_COMPLIANCE_REQUESTED",
                    "Suspect=" + target.Handle + "; Officer=" + interceptor.Handle
                    + "; OfficerDistance=" + officerDistance.ToString("0.0"));
            }
            if (dispatch.RequestBackupInterception(target, player))
            {
                if (!_dispatchSuspectCompliantLogged)
                {
                    _dispatchSuspectCompliantLogged = true;
                    LogRuntime("POLICE_BACKUP_SUSPECT_COMPLIANT",
                        "Suspect=" + target.Handle + "; Officer=" + interceptor.Handle
                        + "; DispatchOwnsSuspect=true; Arrested=false");
                    Notify("~b~POLICE BACKUP~s~\nSuspect is contained. Approach and use E to complete the arrest.");
                    Report("lsimmersivelife.police.compliance.confirmed", "intercepted");
                }
                SetState(LSPDBackupAssignmentState.Securing);
            }
            else
            {
                _dispatchComplianceRequestedLogged = false;
                _nextDispatchInterceptionApproachTaskAt = DateTime.UtcNow.AddSeconds(2);
                SetState(LSPDBackupAssignmentState.Supporting);
            }
        }

        private bool ReleaseInterceptionOfficers(DateTime now)
        {
            bool everyOfficerExited = true;
            foreach (BackupUnit unit in _units)
            {
                if (!IsUsable(unit))
                    continue;
                if (!unit.InterceptionExitRequested || now >= unit.NextInterceptionExitTaskAt)
                {
                    unit.InterceptionExitRequested = true;
                    unit.NextInterceptionExitTaskAt = now.AddSeconds(5);
                    SetEmergencySignals(unit.Vehicle, false);
                    try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true); }
                    catch (Exception ex) { LogException("POLICE_BACKUP_INTERCEPTION_VEHICLE_HOLD_FAILED", ex); }
                    foreach (Ped officer in SupportOfficers(unit))
                    {
                        if (officer.IsDead)
                            continue;
                        if (officer.CurrentVehicle == null
                            || !officer.CurrentVehicle.Exists()
                            || officer.CurrentVehicle.Handle != unit.Vehicle.Handle)
                            continue;
                        everyOfficerExited = false;
                        try
                        {
                            Function.Call(Hash.TASK_LEAVE_VEHICLE,
                                officer, unit.Vehicle, 0);
                            LogRuntime("POLICE_BACKUP_INTERCEPTION_OFFICER_EXIT",
                                "Officer=" + officer.Handle + "; Vehicle=" + unit.Vehicle.Handle);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_INTERCEPTION_OFFICER_EXIT_FAILED", ex);
                        }
                    }
                }
                foreach (Ped officer in SupportOfficers(unit))
                    if (officer.CurrentVehicle != null && officer.CurrentVehicle.Exists()
                        && officer.CurrentVehicle.Handle == unit.Vehicle.Handle)
                        everyOfficerExited = false;
            }
            return everyOfficerExited;
        }

        private bool TryRetaskStandingDownUnits(LSPDConvoy convoy, Ped player)
        {
            if (_gangResponse != null || convoy == null || !convoy.Active || _units.Count == 0)
                return false;
            if (_units.Any(unit => !IsUsable(unit)
                || unit.Driver.CurrentVehicle == null || !unit.Driver.CurrentVehicle.Exists()
                || unit.Driver.CurrentVehicle.Handle != unit.Vehicle.Handle))
                return false;

            _incident = null;
            _convoy = convoy;
            _crimeActivity = null;
            _npcResponse = null;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            Vector3 destination = ResolveConvoyDestination(convoy, player);
            foreach (BackupUnit unit in _units)
            {
                unit.Arrived = false;
                SendTo(unit, destination, true);
            }
            SetState(LSPDBackupAssignmentState.EnRoute);
            Report("lsimmersivelife.police.transport.requested", "convoy-backup-retasked");
            LogRuntime("POLICE_BACKUP_CONVOY_RETASKED", "Units=" + _units.Count + "; Destination=" + destination);
            return true;
        }

        private bool TryRetaskStandingDownUnits(
            LSPDCrimeActivity crimeActivity,
            Ped player)
        {
            if (_gangResponse != null || crimeActivity == null
                || !crimeActivity.Active || _units.Count == 0)
                return false;
            if (_units.Any(unit => !IsUsable(unit)
                || unit.Driver.CurrentVehicle == null || !unit.Driver.CurrentVehicle.Exists()
                || unit.Driver.CurrentVehicle.Handle != unit.Vehicle.Handle))
                return false;

            _incident = null;
            _convoy = null;
            _crimeActivity = crimeActivity;
            _npcResponse = null;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            Vector3 destination = ResolveCrimeActivityDestination(crimeActivity, player);
            foreach (BackupUnit unit in _units)
            {
                unit.Arrived = false;
                unit.DriverReleasedForGroupSupport = false;
                SendTo(unit, destination, true);
                SetEmergencySignals(unit.Vehicle, true);
            }
            SetState(LSPDBackupAssignmentState.EnRoute);
            Report("lsimmersivelife.police.activity.support_requested", "crime-activity-retasked");
            LogRuntime("POLICE_BACKUP_CRIME_ACTIVITY_RETASKED",
                "Activity=" + (crimeActivity.CurrentIntel == null ? string.Empty : crimeActivity.CurrentIntel.Id)
                + "; Units=" + _units.Count + "; Destination=" + destination);
            return true;
        }

        private bool TryRetaskStandingDownUnits(LSPDDispatch dispatch, Ped player)
        {
            if (_gangResponse != null || dispatch == null || !dispatch.HasIncident
                || dispatch.Current == null || _units.Count == 0)
                return false;
            if (_units.Any(unit => !IsUsable(unit)
                || unit.Driver.CurrentVehicle == null || !unit.Driver.CurrentVehicle.Exists()
                || unit.Driver.CurrentVehicle.Handle != unit.Vehicle.Handle))
                return false;

            _incident = dispatch.Current;
            _convoy = null;
            _crimeActivity = null;
            _npcResponse = null;
            ResetDispatchInterceptionTracking();
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            bool pursuit = dispatch.State == LSPDDispatchState.SuspectFleeing;
            Vector3 destination = ResolveDestination(dispatch, player, pursuit);
            foreach (BackupUnit unit in _units)
            {
                unit.Arrived = false;
                unit.DriverReleasedForGroupSupport = false;
                unit.InterceptionExitRequested = false;
                unit.NextInterceptionExitTaskAt = DateTime.MinValue;
                unit.InterceptionAreaReported = false;
                unit.NpcVehiclePursuitActive = false;
                unit.VehiclePursuitLead = false;
                unit.VehicleContainmentStopRequested = false;
                SendTo(unit, destination, true);
                SetEmergencySignals(unit.Vehicle, true);
            }
            if (pursuit)
            {
                LogRuntime("POLICE_BACKUP_INTERCEPTION_STARTED",
                    "Backup interception started; Incident=" + _incident.Id
                    + "; Target=" + (_incident.Suspect == null ? 0 : _incident.Suspect.Handle)
                    + "; Position=" + (_incident.Suspect == null
                        ? Vector3.Zero : _incident.Suspect.Position));
            }
            SetState(pursuit ? LSPDBackupAssignmentState.Intercepting : LSPDBackupAssignmentState.EnRoute);
            Report("lsimmersivelife.police.dispatch.assigned", "backup-retasked");
            LogRuntime("POLICE_BACKUP_DISPATCH_RETASKED", "Incident=" + _incident.Id + "; Units=" + _units.Count);
            return true;
        }

        private bool CanIssueSupportTasks()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _lastSupportTaskAt.AddMilliseconds(SupportTaskRefreshMilliseconds))
                return false;
            _lastSupportTaskAt = now;
            return true;
        }

        private float SafeCleanupDistance
        {
            get
            {
                return _cleanupSettings == null
                    ? DefaultSafeCleanupDistance
                    : _cleanupSettings.SafeCleanupDistance;
            }
        }

        private void ProcessStandDown(Ped player, DateTime now)
        {
            CleanupMissingUnits();
            bool pastGrace = now >= _standDownStartedAt.AddSeconds(_settings.StandDownSeconds);
            bool hardTimeout = now >= _standDownStartedAt.AddSeconds(_settings.StandDownSeconds * 2);
            if (!pastGrace)
                return;
            foreach (BackupUnit unit in _units.ToArray())
            {
                if (_gangResponse != null && unit != null && unit.GangRouteLocked)
                {
                    if (unit.GangStandDownRouteStarted)
                    {
                        if (hardTimeout)
                        {
                            ReleaseBrokenUnit(unit);
                            _units.Remove(unit);
                            LogRuntime(
                                "POLICE_BACKUP_GANG_UNIT_RELEASED",
                                "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                                + "; Reason=StandDownDepartureTimeout");
                            continue;
                        }
                        continue;
                    }

                    if (unit.Vehicle == null || !unit.Vehicle.Exists())
                    {
                        if (hardTimeout)
                        {
                            ReleaseBrokenUnit(unit);
                            _units.Remove(unit);
                            LogRuntime(
                                "POLICE_BACKUP_GANG_UNIT_RELEASED",
                                "Vehicle=0; Reason=VehicleUnavailableDuringStandDown");
                        }
                        continue;
                    }

                    if (!unit.DriverReleasedForGroupSupport)
                        unit.DriverReleasedForGroupSupport = true;
                    if (now >= unit.LastDriveTaskAt.AddSeconds(3))
                    {
                        foreach (Ped officer in SupportOfficers(unit))
                        {
                            if (officer.IsDead || officer.IsInVehicle())
                                continue;
                            try
                            {
                                Function.Call(Hash.CLEAR_PED_TASKS, officer);
                                VehicleSeat seat = unit.Driver != null
                                    && officer.Handle == unit.Driver.Handle
                                    ? VehicleSeat.Driver : VehicleSeat.RightFront;
                                Function.Call(Hash.TASK_ENTER_VEHICLE,
                                    officer, unit.Vehicle, 12000,
                                    (int)seat, 1.0f, 1, 0);
                            }
                            catch (Exception ex)
                            {
                                LogException("POLICE_BACKUP_GANG_DRIVER_REENTRY_RETRY_FAILED", ex);
                            }
                        }
                        unit.LastDriveTaskAt = now;
                    }

                    bool allLivingOfficersInside = SupportOfficers(unit)
                        .Where(officer => !officer.IsDead)
                        .All(officer => officer.CurrentVehicle != null
                            && officer.CurrentVehicle.Exists()
                            && officer.CurrentVehicle.Handle == unit.Vehicle.Handle);
                    if (!allLivingOfficersInside)
                    {
                        if (hardTimeout)
                        {
                            ReleaseBrokenUnit(unit);
                            _units.Remove(unit);
                            LogRuntime(
                                "POLICE_BACKUP_GANG_UNIT_RELEASED",
                                "Vehicle=" + unit.Vehicle.Handle
                                + "; Reason=DriverReentryTimeout");
                        }
                        continue;
                    }

                    unit.DriverReleasedForGroupSupport = false;
                    unit.GangStandDownRouteStarted = true;
                    unit.Arrived = false;
                    unit.LastDriveTaskAt = DateTime.MinValue;
                    SendTo(unit, unit.ReturnDestination, true);
                    continue;
                }
                float distance = player == null || !player.Exists()
                    ? float.MaxValue : unit.Vehicle.Position.DistanceTo(player.Position);
                // Never pop a Police unit out of existence while the player is
                // still close enough to observe or follow it. Hard timeout only
                // releases an already distant unit; reset/deactivation remains
                // the explicit emergency cleanup path.
                if (distance >= SafeCleanupDistance && (pastGrace || hardTimeout))
                {
                    DeleteUnit(unit);
                    _units.Remove(unit);
                }
                else if (_npcBackgroundCleanupRequested && hardTimeout)
                {
                    // The player has already been released from this custody
                    // operation. If the short departure became stuck nearby,
                    // relinquish ownership instead of deleting the vehicle in
                    // front of the player or keeping a permanent AI task.
                    ReleaseBrokenUnit(unit);
                    _units.Remove(unit);
                    LogRuntime("POLICE_BACKUP_NPC_BACKGROUND_UNIT_RELEASED",
                        "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                        + "; Distance=" + distance.ToString("0.0")
                        + "; Reason=BoundedDepartureCleanup");
                }
            }
            if (_units.Count == 0)
            {
                if (_gangResponse != null)
                    LogRuntime(
                        "POLICE_BACKUP_GANG_ASSIGNMENT_RELEASED",
                        "Identity=" + _gangResponse.GangName
                        + "; Turf=" + _gangResponse.TerritoryName
                        + "; Reason=AllGangBackupUnitsReleased");
                LogRuntime("POLICE_BACKUP_RELEASED", "Assignment=" + _operationId);
                _incident = null;
                _convoy = null;
                _crimeActivity = null;
                _npcResponse = null;
                _gangResponse = null;
                _operationId = string.Empty;
                _npcBackgroundCleanupRequested = false;
                _npcBackgroundDepartureDeadline = DateTime.MinValue;
                _npcBackgroundDepartureOrigin = Vector3.Zero;
                _npcBackgroundDepartureDestination = Vector3.Zero;
                _gangCombatStartedLogged = false;
                _gangLastThreatCount = 0;
                _state = LSPDBackupAssignmentState.None;
            }
        }

        private Vector3 ResolveConvoyDestination(LSPDConvoy convoy, Ped player)
        {
            if (convoy == null)
                return player == null || !player.Exists()
                    ? Vector3.Zero : ResolveRoadPosition(player.Position);
            Vector3 position = convoy.SupportPosition;
            if (position != Vector3.Zero)
                return ResolveRoadPosition(position);
            return player == null || !player.Exists()
                ? Vector3.Zero : ResolveRoadPosition(player.Position);
        }

        private Vector3 ResolveCrimeActivityDestination(
            LSPDCrimeActivity crimeActivity,
            Ped player)
        {
            if (crimeActivity == null)
                return player == null || !player.Exists()
                    ? Vector3.Zero : ResolveRoadPosition(player.Position);
            Vector3 position = crimeActivity.SupportPosition;
            if (position != Vector3.Zero)
                return ResolveRoadPosition(position);
            return player == null || !player.Exists()
                ? Vector3.Zero : ResolveRoadPosition(player.Position);
        }

        private Vector3 ResolveGangDestination(
            LSPDGangAdapterResponse gangResponse,
            Ped player)
        {
            if (gangResponse != null)
            {
                Vector3 threatPosition = gangResponse.ThreatPosition;
                if (threatPosition != Vector3.Zero)
                {
                    Vector3 road = ResolveRoadPosition(threatPosition);
                    if (road != Vector3.Zero)
                        return road;
                }
            }

            // A Gang Threat may be occurring inside a turf lot or another
            // non-vehicle area. The response still needs a validated road
            // destination; never send a Backup vehicle to the raw world
            // coordinate when GTA has not supplied a usable road node.
            return player == null || !player.Exists()
                ? Vector3.Zero : ResolveRoadPosition(player.Position);
        }

        private Vector3 ResolveNpcDestination(LSPDNPCResponse npcResponse, Ped player)
        {
            if (npcResponse == null)
                return player == null || !player.Exists()
                    ? Vector3.Zero : ResolveRoadPosition(player.Position);
            if (_npcRouteOverride != Vector3.Zero)
                return _npcRouteOverride;
            if (_npcTransportStarted && IsUsable(_npcTransportUnit))
            {
                return _npcBackgroundDepartureDestination != Vector3.Zero
                    ? _npcBackgroundDepartureDestination
                    : _npcTransportUnit.ReturnDestination;
            }

            Vector3 candidate = npcResponse.SupportPosition;
            if (candidate == Vector3.Zero)
                return player == null || !player.Exists()
                    ? Vector3.Zero : ResolveRoadPosition(player.Position);
            if (_npcTargetWasFleeing)
            {
                Ped subject = npcResponse.ActiveSubject;
                Vehicle vehicle = ResolvePursuitVehicle(
                    subject, npcResponse.ActiveSubjectVehicle);
                if (subject != null && subject.Exists())
                    return ResolveInterceptionRoadPosition(subject, vehicle);
            }
            return ResolveRoadPosition(candidate);
        }

        private Vector3 ResolveDestination(LSPDDispatch dispatch, Ped player, bool interception)
        {
            LSPDDispatchEvent incident = dispatch == null ? _incident : dispatch.Current;
            if (incident == null)
                return player == null || !player.Exists()
                    ? Vector3.Zero : ResolveRoadPosition(player.Position);
            Ped fleeing = interception ? incident.Suspect : null;
            if (fleeing != null && fleeing.Exists() && !fleeing.IsDead)
                return ResolveInterceptionRoadPosition(
                    fleeing, ResolvePursuitVehicle(incident, fleeing));
            if (incident.Suspect != null && incident.Suspect.Exists()
                && !incident.Suspect.IsDead)
                return ResolveRoadPosition(incident.Suspect.Position);
            Vector3 originRoad;
            return TryGetNearbyRoadPosition(
                incident.Origin,
                incident.Origin,
                55f,
                out originRoad) ? originRoad : Vector3.Zero;
        }

        private Ped ResolveDispatchPursuitTarget(LSPDDispatch dispatch)
        {
            LSPDDispatchEvent incident = dispatch == null ? _incident : dispatch.Current;
            if (incident == null)
                incident = _incident;
            if (incident == null)
                return null;

            Ped primary = incident.Suspect;
            if (primary != null && primary.Exists() && !primary.IsDead
                && (dispatch == null || dispatch.CurrentSuspects.Any(ped => ped != null
                    && ped.Exists() && ped.Handle == primary.Handle)))
                return primary;

            return dispatch == null
                ? null
                : dispatch.CurrentSuspects.FirstOrDefault(ped => ped != null
                    && ped.Exists() && !ped.IsDead);
        }

        private static Vehicle ResolvePursuitVehicle(
            LSPDDispatchEvent incident,
            Ped target)
        {
            return ResolvePursuitVehicle(
                target, incident == null ? null : incident.SuspectVehicle);
        }

        private static Vehicle ResolvePursuitVehicle(Ped target, Vehicle assignedVehicle)
        {
            if (target == null || !target.Exists() || !target.IsInVehicle())
                return null;
            try
            {
                Vehicle current = target.CurrentVehicle;
                if (current != null && current.Exists())
                    return current;
            }
            catch { }
            return assignedVehicle != null && assignedVehicle.Exists()
                ? assignedVehicle : null;
        }

        private Vector3 ResolveInterceptionRoadPosition(Ped target, Vehicle vehicle)
        {
            if (target == null || !target.Exists())
                return Vector3.Zero;
            Entity movingEntity = vehicle != null && vehicle.Exists()
                ? (Entity)vehicle : target;
            Vector3 origin = movingEntity.Position;
            float directionX;
            float directionY;
            float speed;
            GetMovementDirection(movingEntity, out directionX, out directionY, out speed);

            bool vehicleTarget = vehicle != null && vehicle.Exists();
            float configuredLead = Math.Max(0f, _settings.InterceptionLeadDistance);
            float maximumLead = vehicleTarget
                ? Math.Max(40f, Math.Min(120f, configuredLead))
                : Math.Max(15f, Math.Min(35f, configuredLead * 0.4f));
            float minimumLead = vehicleTarget ? 20f : 12f;
            float lead = Math.Min(maximumLead,
                Math.Max(minimumLead, speed * (vehicleTarget ? 3.5f : 3.0f)));
            Vector3 projected = new Vector3(
                origin.X + directionX * lead,
                origin.Y + directionY * lead,
                origin.Z);
            Vector3 road;
            if (TryGetNearbyRoadPosition(projected, origin, 45f, out road))
            {
                float forwardDistance = (road.X - origin.X) * directionX
                    + (road.Y - origin.Y) * directionY;
                if (forwardDistance >= -3f)
                    return road;
            }

            return TryGetNearbyRoadPosition(origin, origin, 35f, out road)
                ? road : Vector3.Zero;
        }

        private static void GetMovementDirection(
            Entity entity,
            out float directionX,
            out float directionY,
            out float speed)
        {
            directionX = 0f;
            directionY = 1f;
            speed = 0f;
            if (entity == null || !entity.Exists())
                return;

            try
            {
                Vector3 velocity = Function.Call<Vector3>(Hash.GET_ENTITY_VELOCITY, entity);
                float magnitude = (float)Math.Sqrt(
                    velocity.X * velocity.X + velocity.Y * velocity.Y);
                speed = magnitude;
                if (magnitude >= 0.75f)
                {
                    directionX = velocity.X / magnitude;
                    directionY = velocity.Y / magnitude;
                    return;
                }
            }
            catch { }

            try
            {
                Vector3 forward = entity.GetOffsetPosition(new Vector3(0f, 10f, 0f));
                float dx = forward.X - entity.Position.X;
                float dy = forward.Y - entity.Position.Y;
                float magnitude = (float)Math.Sqrt(dx * dx + dy * dy);
                if (magnitude > 0.1f)
                {
                    directionX = dx / magnitude;
                    directionY = dy / magnitude;
                }
            }
            catch { }
        }

        private static bool TryGetNearbyVehicleRoadNode(
            Vector3 candidate,
            Vector3 reference,
            float maximumDistance,
            out Vector3 roadPosition,
            out float roadHeading)
        {
            roadPosition = Vector3.Zero;
            roadHeading = 0f;
            if (candidate == Vector3.Zero)
                return false;
            try
            {
                using (OutputArgument nodeOutput = new OutputArgument())
                using (OutputArgument headingOutput = new OutputArgument())
                {
                    bool found = Function.Call<bool>(
                        Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        candidate.X,
                        candidate.Y,
                        candidate.Z,
                        nodeOutput,
                        headingOutput,
                        1,
                        3.0f,
                        0);
                    if (!found)
                        return false;

                    Vector3 node = nodeOutput.GetResult<Vector3>();
                    float heading = headingOutput.GetResult<float>();
                    if (node == Vector3.Zero
                        || float.IsNaN(heading)
                        || float.IsInfinity(heading)
                        || node.DistanceTo(candidate) > maximumDistance
                        || Math.Abs(node.Z - reference.Z) > MaximumRoadElevationDifference)
                        return false;

                    // A vehicle node is the routing source, but GTA can still
                    // return a node beside a road when the query starts in a
                    // lot, on a sidewalk, or near an obstruction. Require the
                    // final point to be on a vehicle-usable road surface before
                    // it can become a spawn point or a drive-task target.
                    if (!Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                        node.X, node.Y, node.Z, 0))
                        return false;

                    roadPosition = node;
                    roadHeading = heading;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetNearbyRoadPosition(
            Vector3 candidate,
            Vector3 reference,
            float maximumDistance,
            out Vector3 roadPosition)
        {
            roadPosition = Vector3.Zero;
            float roadHeading;
            if (TryGetNearbyVehicleRoadNode(
                candidate,
                reference,
                maximumDistance,
                out roadPosition,
                out roadHeading))
                return true;

            // Authored locations and moving subjects can be beside a road
            // rather than on its vehicle node. Probe a small, bounded ring for
            // another node, but never return the probe or the original world
            // coordinate itself. The final distance is measured from the
            // original candidate so a farther street is not accepted merely
            // because it is near one of the probes.
            float probeDistance = Math.Min(24f, Math.Max(8f, maximumDistance * 0.5f));
            Vector3[] probes =
            {
                candidate + new Vector3(probeDistance, 0f, 0f),
                candidate + new Vector3(-probeDistance, 0f, 0f),
                candidate + new Vector3(0f, probeDistance, 0f),
                candidate + new Vector3(0f, -probeDistance, 0f)
            };
            float bestDistance = float.MaxValue;
            Vector3 bestRoad = Vector3.Zero;
            foreach (Vector3 probe in probes)
            {
                Vector3 probeRoad;
                if (!TryGetNearbyVehicleRoadNode(
                    probe,
                    reference,
                    maximumDistance,
                    out probeRoad,
                    out roadHeading))
                    continue;
                float distance = probeRoad.DistanceTo(candidate);
                if (distance > maximumDistance || distance >= bestDistance)
                    continue;
                bestRoad = probeRoad;
                bestDistance = distance;
            }
            if (bestRoad != Vector3.Zero)
            {
                roadPosition = bestRoad;
                return true;
            }
            roadPosition = Vector3.Zero;
            return false;
        }

        private bool TryRetargetNpcCustodyRoute(
            BackupUnit unit,
            Ped player,
            DateTime now)
        {
            if (_npcResponse == null || _npcTargetWasFleeing
                || _npcRouteRetargetCount >= MaximumNpcRouteRetargets
                || !IsUsable(unit))
                return false;

            Vector3 target = _npcResponse.SupportPosition;
            if (target == Vector3.Zero)
                target = player == null || !player.Exists() ? Vector3.Zero : player.Position;
            if (target == Vector3.Zero)
                return false;

            Vector3 fromTarget = unit.Vehicle.Position - target;
            float length = (float)Math.Sqrt(
                fromTarget.X * fromTarget.X + fromTarget.Y * fromTarget.Y);
            if (length < 0.5f)
            {
                fromTarget = new Vector3(1f, 0f, 0f);
                length = 1f;
            }

            float unitX = fromTarget.X / length;
            float unitY = fromTarget.Y / length;
            Vector3 lateral = new Vector3(-unitY, unitX, 0f);
            float side = _npcRouteRetargetCount % 2 == 0 ? 1f : -1f;
            Vector3[] candidates =
            {
                target + lateral * (side * 14f),
                target - lateral * (side * 14f),
                target + new Vector3(unitX, unitY, 0f) * 12f,
                target - new Vector3(unitX, unitY, 0f) * 12f
            };

            foreach (Vector3 candidate in candidates)
            {
                Vector3 road;
                if (!TryGetNearbyRoadPosition(candidate, target, 35f, out road)
                    || road.DistanceTo(unit.Vehicle.Position) < 4f
                    || (unit.Destination != Vector3.Zero
                        && road.DistanceTo(unit.Destination) <= 6f))
                    continue;

                _npcRouteOverride = road;
                _npcRouteRetargetCount++;
                unit.RouteRecoveryCount = 0;
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.LastProgressAt = now;
                SendTo(unit, road, true);
                LogRuntime("POLICE_BACKUP_NPC_ROUTE_RETARGETED",
                    "Attempt=" + _npcRouteRetargetCount
                    + "; Vehicle=" + unit.Vehicle.Handle
                    + "; Destination=" + road
                    + "; Subject=" + (_npcResponse.ActiveSubject == null
                        ? 0 : _npcResponse.ActiveSubject.Handle));
                return true;
            }
            return false;
        }

        private bool TryRetargetNpcTransportRoute(
            BackupUnit unit,
            Vector3 stationDestination,
            DateTime now)
        {
            if (_npcResponse == null || !_npcTransportStarted
                || _npcRouteRetargetCount >= MaximumNpcRouteRetargets
                || !IsUsable(unit))
                return false;

            Vector3 origin = unit.Vehicle.Position;
            float probeDistance = 24f + (_npcRouteRetargetCount * 8f);
            Vector3[] probes =
            {
                unit.Vehicle.GetOffsetPosition(new Vector3(0f, probeDistance, 0f)),
                unit.Vehicle.GetOffsetPosition(new Vector3(0f, -probeDistance, 0f)),
                unit.Vehicle.GetOffsetPosition(new Vector3(probeDistance, 0f, 0f)),
                unit.Vehicle.GetOffsetPosition(new Vector3(-probeDistance, 0f, 0f)),
                origin + new Vector3(probeDistance, probeDistance, 0f),
                origin + new Vector3(-probeDistance, -probeDistance, 0f)
            };

            foreach (Vector3 probe in probes)
            {
                Vector3 road;
                if (!TryGetNearbyRoadPosition(probe, origin, 48f, out road)
                    || road.DistanceTo(origin) < 12f
                    || (stationDestination != Vector3.Zero
                        && road.DistanceTo(stationDestination) < 8f))
                    continue;

                // A recovery waypoint is only useful when it moves the
                // transport toward the station. The old local probe order
                // could select a valid street behind or beside the vehicle,
                // making the driver turn across lanes or repeatedly return to
                // the obstruction. Reject clearly backward waypoints and let
                // the bounded recovery fail safely when no forward road is
                // available.
                if (stationDestination != Vector3.Zero)
                {
                    Vector3 toStation = stationDestination - origin;
                    float stationLength = (float)Math.Sqrt(
                        toStation.X * toStation.X + toStation.Y * toStation.Y);
                    if (stationLength > 0.5f)
                    {
                        float progress = ((road.X - origin.X) * toStation.X
                            + (road.Y - origin.Y) * toStation.Y) / stationLength;
                        if (progress < -6f)
                            continue;
                    }
                }

                _npcRouteOverride = road;
                _npcRouteRetargetCount++;
                unit.Arrived = false;
                unit.RouteRecoveryCount = 0;
                unit.LastProgressPosition = origin;
                unit.LastProgressAt = now;
                SendTo(unit, road, true);
                LogRuntime("POLICE_BACKUP_NPC_TRANSPORT_ROUTE_RETARGETED",
                    "Attempt=" + _npcRouteRetargetCount
                    + "; Vehicle=" + unit.Vehicle.Handle
                    + "; Waypoint=" + road
                    + "; Station=" + stationDestination
                    + "; Subject=" + (_npcResponse.ActiveSubject == null
                        ? 0 : _npcResponse.ActiveSubject.Handle));
                return true;
            }

            return false;
        }

        private bool TryFindPursuitStagingPosition(
            Ped player,
            int index,
            Ped target,
            Vehicle targetVehicle,
            out Vector3 stagingPosition)
        {
            stagingPosition = Vector3.Zero;
            if (target == null || !target.Exists() || target.IsDead)
                return false;

            Entity movingEntity = targetVehicle != null && targetVehicle.Exists()
                ? (Entity)targetVehicle : target;
            Vector3 targetPosition = movingEntity.Position;
            float directionX;
            float directionY;
            float speed;
            GetMovementDirection(movingEntity, out directionX, out directionY, out speed);
            float minimumDistance = Math.Max(
                MinimumPursuitSpawnDistance,
                Math.Max(35f, _settings.MinimumStagingDistance));
            float perpendicularX = -directionY;
            float perpendicularY = directionX;
            float unitLateral = (index % 2 == 0 ? -1f : 1f)
                * (10f + (index / 2) * 12f);

            // Find an actual road behind the moving target. Reject fallback
            // coordinates and roads on another elevation (common at freeway
            // ramps) instead of creating a unit at a wall or below the map.
            for (int ring = 0; ring < 3; ring++)
            {
                float distance = minimumDistance + ring * 45f;
                for (int side = -1; side <= 1; side++)
                {
                    float lateral = unitLateral + side * 12f;
                    Vector3 candidate = new Vector3(
                        targetPosition.X - directionX * distance + perpendicularX * lateral,
                        targetPosition.Y - directionY * distance + perpendicularY * lateral,
                        targetPosition.Z);
                    Vector3 road;
                    if (TryGetNearbyRoadPosition(candidate, targetPosition, 45f, out road)
                        && road.DistanceTo(targetPosition) >= minimumDistance * 0.65f)
                    {
                        stagingPosition = road;
                        return true;
                    }
                }
            }

            LSPDPoliceStationDefinition station = SelectedStation();
            Vector3 stationPosition = station == null
                ? Vector3.Zero : StationVehiclePosition(station);
            Vector3 stationRoad;
            if (stationPosition != Vector3.Zero
                && TryGetNearbyRoadPosition(stationPosition, stationPosition, 50f, out stationRoad)
                && stationRoad.DistanceTo(targetPosition) >= minimumDistance * 0.65f)
            {
                stagingPosition = stationRoad;
                return true;
            }

            // The player position is only a last safe-road lookup, never the
            // pursuit destination. Avoid returning a raw offset if no road can
            // be validated.
            if (player != null && player.Exists()
                && TryGetNearbyRoadPosition(player.Position, player.Position, 45f, out stationRoad)
                && stationRoad.DistanceTo(targetPosition) >= minimumDistance * 0.65f)
            {
                stagingPosition = stationRoad;
                return true;
            }
            return false;
        }

        private Vector3 FindNpcStagingPosition(Ped player, Vector3 destination)
        {
            if (player == null || !player.Exists())
                return Vector3.Zero;

            Vector3 reference = destination == Vector3.Zero
                ? player.Position : destination;
            float configuredDistance = Math.Max(35f, _settings.MinimumStagingDistance);
            float minimumDistance = destination == Vector3.Zero
                ? configuredDistance
                : Math.Max(42f, configuredDistance * 0.65f);
            float lateral = 10f;
            Vector3[] candidates =
            {
                reference + new Vector3(-minimumDistance, lateral, 0f),
                reference + new Vector3(minimumDistance, -lateral, 0f),
                reference + new Vector3(lateral, minimumDistance, 0f),
                reference + new Vector3(-lateral, -minimumDistance, 0f),
                reference + new Vector3(-minimumDistance * 0.7f, -minimumDistance * 0.7f, 0f),
                reference + new Vector3(minimumDistance * 0.7f, minimumDistance * 0.7f, 0f),
                reference + new Vector3(minimumDistance * 0.7f, -minimumDistance * 0.7f, 0f),
                reference + new Vector3(-minimumDistance * 0.7f, minimumDistance * 0.7f, 0f)
            };

            foreach (Vector3 candidate in candidates)
            {
                Vector3 road;
                if (!TryGetNearbyRoadPosition(candidate, reference, 55f, out road))
                    continue;
                if (road.DistanceTo(player.Position)
                    < Math.Max(22f, minimumDistance * 0.5f))
                    continue;
                if (destination != Vector3.Zero
                    && road.DistanceTo(destination) < minimumDistance * 0.45f)
                    continue;
                return road;
            }

            // A local road probe around the player is a safe last attempt,
            // but never return an unchecked coordinate that may be inside a
            // building, on a sidewalk, or at an invalid world position.
            Vector3[] localCandidates =
            {
                player.GetOffsetPosition(new Vector3(-18f, 28f, 0f)),
                player.GetOffsetPosition(new Vector3(18f, 28f, 0f)),
                player.GetOffsetPosition(new Vector3(-18f, -28f, 0f)),
                player.GetOffsetPosition(new Vector3(18f, -28f, 0f))
            };
            foreach (Vector3 candidate in localCandidates)
            {
                Vector3 road;
                if (!TryGetNearbyRoadPosition(candidate, player.Position, 45f, out road))
                    continue;
                if (destination != Vector3.Zero
                    && road.DistanceTo(destination) < Math.Max(18f, minimumDistance * 0.4f))
                    continue;
                return road;
            }

            return Vector3.Zero;
        }

        private Vector3 FindGangStagingPosition(
            Ped player,
            int index,
            Vector3 destination)
        {
            if (player == null || !player.Exists()
                || destination == Vector3.Zero)
                return Vector3.Zero;

            float minimumDistance = Math.Min(
                GangMaximumStagingDistance - 20f,
                Math.Max(
                    GangMinimumStagingDistance,
                    Math.Max(45f, _settings.MinimumStagingDistance * 0.75f)));
            float[] distances =
            {
                minimumDistance,
                Math.Min(GangMaximumStagingDistance, minimumDistance + 24f),
                Math.Min(GangMaximumStagingDistance, minimumDistance + 52f),
                GangMaximumStagingDistance
            };

            // Start the search on the side of the threat that is nearest to
            // the player, then check a bounded ring.  This makes the response
            // arrive from a practical nearby road without treating the
            // threat's interior/turf coordinate as a vehicle spawn point.
            float towardPlayerX = player.Position.X - destination.X;
            float towardPlayerY = player.Position.Y - destination.Y;
            float playerDistance = (float)Math.Sqrt(
                towardPlayerX * towardPlayerX + towardPlayerY * towardPlayerY);
            float baseAngle = playerDistance > 0.1f
                ? (float)Math.Atan2(towardPlayerY, towardPlayerX)
                : 0f;
            float angleOffset = (index % 2 == 0 ? 0f : (float)Math.PI / 8f);

            foreach (float distance in distances)
            {
                for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                {
                    float angle = baseAngle + angleOffset
                        + directionIndex * ((float)Math.PI / 4f);
                    Vector3 candidate = new Vector3(
                        destination.X + (float)Math.Cos(angle) * distance,
                        destination.Y + (float)Math.Sin(angle) * distance,
                        destination.Z);
                    Vector3 road;
                    if (!TryGetNearbyVehicleRoadNode(
                        candidate,
                        destination,
                        34f,
                        out road,
                        out _))
                        continue;
                    if (!IsGangRoadNodeCompatible(road, destination)
                        || road.DistanceTo(destination) < minimumDistance * 0.80f
                        || road.DistanceTo(player.Position) < 24f
                        || IsGangStagingNodeDuplicated(road))
                        continue;
                    return road;
                }
            }

            // A gang scene can be close to a divided street or a steep
            // embankment.  Probe the player's nearby road only as a bounded
            // fallback, while still requiring the same level as the gang
            // destination.  Never return the raw player coordinate.
            Vector3[] localCandidates =
            {
                player.GetOffsetPosition(new Vector3(-24f, 42f, 0f)),
                player.GetOffsetPosition(new Vector3(24f, 42f, 0f)),
                player.GetOffsetPosition(new Vector3(-24f, -42f, 0f)),
                player.GetOffsetPosition(new Vector3(24f, -42f, 0f))
            };
            foreach (Vector3 candidate in localCandidates)
            {
                Vector3 road;
                if (!TryGetNearbyVehicleRoadNode(
                    candidate,
                    destination,
                    70f,
                    out road,
                    out _))
                    continue;
                if (!IsGangRoadNodeCompatible(road, destination)
                    || road.DistanceTo(destination) < minimumDistance * 0.80f
                    || IsGangStagingNodeDuplicated(road))
                    continue;
                return road;
            }

            return Vector3.Zero;
        }

        private bool IsGangStagingNodeDuplicated(Vector3 candidate)
        {
            return _units.Any(unit => IsUsable(unit)
                && unit.Vehicle.Position.DistanceTo(candidate)
                    < GangDuplicateStagingDistance);
        }

        private static bool IsGangRoadNodeCompatible(
            Vector3 road,
            Vector3 destination)
        {
            if (road == Vector3.Zero || destination == Vector3.Zero)
                return false;
            if (Math.Abs(road.Z - destination.Z)
                > GangRoadElevationDifference)
                return false;
            try
            {
                return Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                    road.X, road.Y, road.Z, 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsGangVehiclePlacementValid(
            Vehicle vehicle,
            Vector3 validatedNode,
            Vector3 destination)
        {
            if (vehicle == null || !vehicle.Exists()
                || validatedNode == Vector3.Zero
                || destination == Vector3.Zero)
                return false;

            Vector3 rotation = vehicle.Rotation;
            if (Math.Abs(rotation.X) > GangVehicleMaximumPitchRoll
                || Math.Abs(rotation.Y) > GangVehicleMaximumPitchRoll)
                return false;
            if (Math.Abs(vehicle.Position.Z - validatedNode.Z) > 3.0f)
                return false;
            return Math.Abs(validatedNode.Z - destination.Z)
                <= GangRoadElevationDifference;
        }

        private Vector3 FindStagingPosition(Ped player, int index, Vector3 destination)
        {
            LSPDPoliceStationDefinition station = SelectedStation();
            Vector3 stationPosition = station == null ? Vector3.Zero
                : new Vector3(station.VehicleX, station.VehicleY, station.VehicleZ);
            float configuredDistance = Math.Max(35f, _settings.MinimumStagingDistance);
            float minimumDistance = destination == Vector3.Zero
                ? configuredDistance
                : Math.Max(45f, configuredDistance * 0.70f);
            float lateral = (index % 2 == 0 ? -1f : 1f) * (8f + index * 5f);
            Vector3 candidate = Vector3.Zero;
            if (destination != Vector3.Zero)
            {
                // Stage beside the actual owned assignment even when a fleeing
                // subject has travelled far from Anyi. The previous distance
                // gate discarded this destination and left the fallback
                // candidate at world origin, so Backup could spawn far away
                // from the tracked citizen or Dispatch scene.
                candidate = destination + new Vector3(
                    lateral,
                    -minimumDistance - index * 8f,
                    0f);
            }
            else if (stationPosition == Vector3.Zero
                || stationPosition.DistanceTo(player.Position) < minimumDistance)
            {
                candidate = player.GetOffsetPosition(new Vector3(
                    lateral,
                    -minimumDistance - index * 8f,
                    0f));
            }
            else
            {
                // Multiple units must not all materialize at the exact same
                // station coordinate. Stagger each owned vehicle before asking
                // GTA for a nearby street position, which preserves a normal
                // response arrival without stacked Police cars.
                candidate += new Vector3(lateral, -6f - index * 7f, 0f);
            }
            Vector3 vehicleNode;
            if (TryGetNearbyRoadPosition(candidate, player.Position, 60f, out vehicleNode)
                && vehicleNode.DistanceTo(player.Position) >= minimumDistance * 0.75f)
                return vehicleNode;
            return Vector3.Zero;
        }

        private static float HeadingToward(Vector3 from, Vector3 target, float fallback)
        {
            float deltaX = target.X - from.X;
            float deltaY = target.Y - from.Y;
            if (Math.Abs(deltaX) < 0.01f && Math.Abs(deltaY) < 0.01f)
                return fallback;
            float heading = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI) - 90f;
            if (heading < 0f)
                heading += 360f;
            if (heading >= 360f)
                heading -= 360f;
            return heading;
        }

        private static float AlignRoadHeadingToTarget(
            Vector3 from,
            Vector3 target,
            float roadHeading,
            float fallback)
        {
            if (float.IsNaN(roadHeading) || float.IsInfinity(roadHeading))
                return HeadingToward(from, target, fallback);

            float deltaX = target.X - from.X;
            float deltaY = target.Y - from.Y;
            float distance = (float)Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (distance < 0.5f)
                return NormalizeHeading(roadHeading);

            float radians = roadHeading * (float)Math.PI / 180f;
            float roadDirectionX = (float)Math.Sin(radians);
            float roadDirectionY = (float)Math.Cos(radians);
            float targetDirectionX = deltaX / distance;
            float targetDirectionY = deltaY / distance;
            float alignment = roadDirectionX * targetDirectionX
                + roadDirectionY * targetDirectionY;
            if (alignment < 0f)
                roadHeading += 180f;
            return NormalizeHeading(roadHeading);
        }

        private static float NormalizeHeading(float heading)
        {
            while (heading < 0f)
                heading += 360f;
            while (heading >= 360f)
                heading -= 360f;
            return heading;
        }

        private bool TryRecoverStalledRoute(BackupUnit unit, Vector3 destination, DateTime now)
        {
            if (!IsUsable(unit) || destination == Vector3.Zero
                || (unit.GangRouteLocked
                    && _gangResponse != null
                    && _gangResponse.HasActiveIncident))
                return false;

            float moved = unit.Vehicle.Position.DistanceTo(unit.LastProgressPosition);
            if (moved >= 4f)
            {
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.LastProgressAt = now;
                return false;
            }
            if (now < unit.LastProgressAt.AddSeconds(RouteStallTimeoutSeconds)
                || unit.RouteRecoveryCount >= MaximumRouteRecoveries)
                return false;

            // Only query a replacement node when the bounded stall window is
            // actually due. This keeps route checks event-driven instead of
            // doing a path-node lookup every game tick while the vehicle is
            // still making progress.
            Vector3 routeDestination;
            float routeHeading;
            if (!TryGetNearbyVehicleRoadNode(
                destination,
                destination,
                75f,
                out routeDestination,
                out routeHeading))
            {
                LogRuntime("POLICE_BACKUP_DRIVE_TARGET_REJECTED",
                    "Vehicle=" + unit.Vehicle.Handle
                    + "; Candidate=" + destination
                    + "; Reason=NO_VALID_VEHICLE_PATH_NODE_DURING_RECOVERY");
                return false;
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false);
                SetEmergencySignals(unit.Vehicle, true);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    unit.Driver,
                    unit.Vehicle,
                    routeDestination.X,
                    routeDestination.Y,
                    routeDestination.Z,
                    _gangResponse != null && _gangResponse.HasActiveIncident
                        ? 25.0f
                        : _npcResponse != null && !_npcTargetWasFleeing
                            ? (_npcTransportStarted ? 24.0f : 22.0f) : 20.0f,
                    (int)EmergencyDrivingFlags,
                    _gangResponse != null && _gangResponse.HasActiveIncident
                        ? 8.0f
                        : _npcResponse != null && !_npcTargetWasFleeing
                            ? (_npcTransportStarted ? 10.0f : 6.0f) : 10.0f);
                unit.RouteRecoveryCount++;
                unit.RouteHeading = routeHeading;
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.LastProgressAt = now;
                unit.LastDriveTaskAt = now;
                LogRuntime("POLICE_BACKUP_ROUTE_RECOVERY",
                    "Recovery=" + unit.RouteRecoveryCount
                    + "; Destination=" + routeDestination
                    + "; RoadHeading=" + routeHeading.ToString("0.0"));
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_ROUTE_RECOVERY_FAILED", ex);
                unit.LastProgressAt = now;
                return false;
            }
        }

        private static bool HasExhaustedRouteRecovery(BackupUnit unit, DateTime now)
        {
            return unit != null
                && unit.RouteRecoveryCount >= MaximumRouteRecoveries
                && now >= unit.LastProgressAt.AddSeconds(RouteStallTimeoutSeconds);
        }

        private bool TryRecoverStalledNpcEscort(Ped officer, Ped subject, DateTime now)
        {
            if (officer == null || !officer.Exists() || subject == null || !subject.Exists())
                return false;
            float moved = officer.Position.DistanceTo(_npcEscortLastPosition);
            if (moved >= 1.0f)
            {
                _npcEscortLastPosition = officer.Position;
                _npcEscortLastProgressAt = now;
                return false;
            }
            if (now < _npcEscortLastProgressAt.AddSeconds(NpcEscortStallTimeoutSeconds)
                || _npcEscortRecoveryCount >= MaximumNpcEscortRecoveries)
                return false;

            try
            {
                // Once the subject is physically attached, the officer is no
                // longer approaching the subject. Reissuing TASK_GO_TO_ENTITY
                // against an attached ped can produce a zero-distance task
                // and leave the pair standing. Recover toward the assigned
                // rear door instead; the NPC owner continues to maintain the
                // measured escort relationship.
                if (_npcResponse != null
                    && _npcResponse.IsBackupPhysicalEscortActive
                    && IsUsable(_npcTransportUnit)
                    && _npcTransportUnit.Vehicle != null
                    && _npcTransportUnit.Vehicle.Exists())
                {
                    Vector3 entry = _npcTransportUnit.Vehicle.GetOffsetPosition(
                        new Vector3(2.0f, -1.0f, 0.0f));
                    Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                        officer,
                        entry.X, entry.Y, entry.Z,
                        1.15f, -1, 1.0f, 1, 0f);
                }
                else if (_npcTargetWasFleeing)
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY,
                        officer, subject, -1, 1.5f, 3.2f, 1073741824, 0);
                }
                else
                {
                    Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                        officer,
                        subject.Position.X, subject.Position.Y, subject.Position.Z,
                        1.35f, -1, 1.0f, 1, 0f);
                }
                _npcEscortRecoveryCount++;
                _npcEscortLastPosition = officer.Position;
                _npcEscortLastProgressAt = now;
                _nextNpcOfficerTaskAt = now.AddSeconds(5);
                LogRuntime("POLICE_BACKUP_NPC_ESCORT_ROUTE_RECOVERY",
                    "Recovery=" + _npcEscortRecoveryCount
                    + "; Officer=" + officer.Handle
                    + "; Subject=" + subject.Handle);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_ESCORT_ROUTE_RECOVERY_FAILED", ex);
                _npcEscortLastProgressAt = now;
                return false;
            }
        }

        private bool HasExhaustedNpcEscortRecovery(DateTime now)
        {
            return _npcEscortRecoveryCount >= MaximumNpcEscortRecoveries
                && now >= _npcEscortLastProgressAt.AddSeconds(NpcEscortStallTimeoutSeconds);
        }

        private bool SendTo(BackupUnit unit, Vector3 destination, bool force)
        {
            if (!IsUsable(unit))
                return false;
            DateTime now = DateTime.UtcNow;
            if (unit.Destination == Vector3.Zero
                || unit.Destination.DistanceTo(destination) > 8f)
            {
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.LastProgressAt = now;
                unit.RouteRecoveryCount = 0;
            }
            if (!force && now < unit.LastDriveTaskAt.AddMilliseconds(TaskRefreshMilliseconds))
                return true;

            Vector3 routeDestination;
            float routeHeading;
            if (!TryGetNearbyVehicleRoadNode(
                destination,
                destination,
                75f,
                out routeDestination,
                out routeHeading))
            {
                LogRuntime("POLICE_BACKUP_DRIVE_TARGET_REJECTED",
                    "Vehicle=" + unit.Vehicle.Handle
                    + "; Candidate=" + destination
                    + "; Reason=NO_VALID_VEHICLE_PATH_NODE");
                return false;
            }
            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false);
                SetEmergencySignals(unit.Vehicle, _state != LSPDBackupAssignmentState.StandingDown);
                float speed = 20f;
                VehicleDrivingFlags drivingFlags = EmergencyDrivingFlags;
                bool npcCustodyRoadRoute = _npcResponse != null
                    && !_npcTargetWasFleeing;
                bool gangRoadRoute = _gangResponse != null
                    && _gangResponse.HasActiveIncident;
                if (npcCustodyRoadRoute || gangRoadRoute)
                {
                    // The generic DriveTo wrapper can leave a response unit
                    // idling or turning into an obstacle during the NPC
                    // custody route. Keep both the approach and the loaded
                    // station trip on GTA's road-aware long-range task. The
                    // loaded trip gets a slightly stronger emergency speed.
                    // Gang support uses the same road-aware task so it does
                    // not try to cut across a turf lot or climb toward a raw
                    // world coordinate after staging.
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                        unit.Driver,
                        unit.Vehicle,
                        routeDestination.X,
                        routeDestination.Y,
                        routeDestination.Z,
                        gangRoadRoute
                            ? 25.0f
                            : (_npcTransportStarted ? 24.0f : 22.0f),
                        (int)drivingFlags,
                        gangRoadRoute
                            ? 8.0f
                            : (_npcTransportStarted ? 10.0f : 6.0f));
                }
                else
                    unit.Driver.Task.DriveTo(unit.Vehicle, routeDestination, speed,
                        drivingFlags, 18f);
                unit.Destination = routeDestination;
                unit.RouteHeading = routeHeading;
                unit.LastDriveTaskAt = now;
                if (gangRoadRoute && force
                    && _state != LSPDBackupAssignmentState.StandingDown)
                {
                    LogRuntime("POLICE_BACKUP_GANG_ROUTE_STARTED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; From=" + unit.Vehicle.Position
                        + "; Destination=" + routeDestination
                        + "; RoadHeading=" + routeHeading.ToString("0.0"));
                }
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DRIVE_TASK_FAILED", ex);
                return false;
            }
        }

        private static IEnumerable<Ped> SupportOfficers(BackupUnit unit)
        {
            return unit == null || unit.Officers == null
                ? Enumerable.Empty<Ped>()
                : unit.Officers.Where(ped => ped != null && ped.Exists());
        }

        private void PrepareOfficer(Ped officer)
        {
            if (officer == null || !officer.Exists())
                return;
            officer.IsPersistent = true;
            officer.BlockPermanentEvents = true;
            officer.MaxHealth = 250;
            officer.Health = 250;
            officer.Armor = 100;
            try
            {
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY,
                    officer, true, true);
                Function.Call(Hash.SET_PED_AS_COP, officer, true);
                Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                Function.Call(Hash.SET_PED_COMBAT_ABILITY, officer, 2);
                Function.Call(Hash.SET_PED_ACCURACY, officer, 60);
                Function.Call(Hash.SET_DRIVER_ABILITY, officer, 1.0f);
                // Keep the response assertive enough to reach an active call,
                // while the road-aware driving flags remain responsible for
                // avoiding civilian vehicles and fixed obstacles.
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, officer, 0.35f);

                string weaponName = ResolveBackupWeaponName();
                int weaponHash = unchecked((int)StringHash.AtStringHash(weaponName, 0));
                Function.Call(Hash.GIVE_WEAPON_TO_PED,
                    officer, weaponHash, ResolveBackupWeaponAmmo(), false, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, officer, weaponHash, true);
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_OFFICER_PREPARATION_FAILED", ex);
            }
        }

        private void PrepareGangOfficer(Ped officer)
        {
            PrepareOfficer(officer);
            if (officer == null || !officer.Exists())
                return;
            try
            {
                // Gang Threat support is a deliberate escalation of the
                // ordinary Backup loadout: the units must survive a larger
                // hostile group long enough to create real containment.
                officer.MaxHealth = 300;
                officer.Health = 300;
                officer.Armor = 100;
                Function.Call(Hash.SET_PED_COMBAT_ABILITY, officer, 2);
                Function.Call(Hash.SET_PED_ACCURACY, officer, 75);

                string weaponName = ResolveBackupWeaponName();
                int weaponHash = unchecked((int)StringHash.AtStringHash(weaponName, 0));
                int ammunition = Math.Max(180, ResolveBackupWeaponAmmo());
                Function.Call(Hash.GIVE_WEAPON_TO_PED,
                    officer, weaponHash, ammunition, false, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, officer, weaponHash, true);
                LogRuntime(
                    "POLICE_BACKUP_GANG_OFFICER_READY",
                    "Officer=" + officer.Handle
                    + "; Weapon=" + weaponName
                    + "; Ammo=" + ammunition
                    + "; Health=" + officer.Health
                    + "; Armor=" + officer.Armor
                    + "; CombatAbility=2; Accuracy=75");
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_GANG_OFFICER_PREPARATION_FAILED", ex);
            }
        }

        private string ResolveBackupWeaponName()
        {
            LSPDPoliceWeaponDefinition standard = _profile == null
                ? null
                : _profile.FindWeapon("service_pistol");
            return standard == null || string.IsNullOrWhiteSpace(standard.WeaponName)
                ? "WEAPON_PISTOL"
                : standard.WeaponName;
        }

        private int ResolveBackupWeaponAmmo()
        {
            LSPDPoliceWeaponDefinition standard = _profile == null
                ? null
                : _profile.FindWeapon("service_pistol");
            return standard == null ? 72 : Math.Max(1, standard.DefaultAmmo);
        }

        private void AimAtNearestThreat(Ped officer, IEnumerable<Ped> threats)
        {
            Ped target = NearestThreat(officer, threats);
            if (target != null) AimAt(officer, target);
        }

        private static Ped NearestThreat(Ped officer, IEnumerable<Ped> threats)
        {
            return officer == null || !officer.Exists() || threats == null ? null
                : threats.Where(ped => ped != null && ped.Exists())
                    .OrderBy(ped => ped.Position.DistanceTo(officer.Position)).FirstOrDefault();
        }

        private void AimAt(Ped officer, Ped target)
        {
            if (officer == null || !officer.Exists() || target == null || !target.Exists()) return;
            try { Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, officer, target, 6000, true); }
            catch (Exception ex) { LogException("POLICE_BACKUP_AIM_TASK_FAILED", ex); }
        }

        private void AssignCombat(Ped officer, Ped target)
        {
            if (officer == null || !officer.Exists() || target == null || !target.Exists()) return;
            try { officer.Task.Combat(target); }
            catch (Exception ex) { LogException("POLICE_BACKUP_COMBAT_TASK_FAILED", ex); }
        }

        private void CleanupMissingUnits()
        {
            foreach (BackupUnit unit in _units.ToArray())
            {
                // A Gang support unit has already finished its vehicle route
                // once GangRouteLocked is true. The driver is no longer a
                // prerequisite for the officers' foot engagement. Never
                // replace a living combat officer with a driver while the
                // incident is active.
                if (_gangResponse != null
                    && _state != LSPDBackupAssignmentState.StandingDown
                    && unit != null && unit.GangRouteLocked
                    && IsGangSupportUnitUsable(unit))
                {
                    if (unit.Driver == null || !unit.Driver.Exists()
                        || unit.Driver.IsDead)
                    {
                        if (!unit.GangDriverLossLogged)
                        {
                            unit.GangDriverLossLogged = true;
                            LogRuntime(
                                "POLICE_BACKUP_GANG_DRIVER_LOST_BUT_OFFICER_REMAINS",
                                DescribeUnavailableUnit(unit)
                                + "; VehicleRouting=Finished; FootSupport=Continues");
                        }
                    }
                    continue;
                }
                if (IsUsable(unit))
                    continue;

                string reason = DescribeUnavailableUnit(unit);
                if (TryRecoverUnitDriver(unit))
                {
                    LogRuntime(
                        "POLICE_BACKUP_UNIT_RECOVERED",
                        reason + "; ReplacementDriver=" + unit.Driver.Handle);
                    continue;
                }

                // A broken response unit should not pop every surviving officer
                // or vehicle out of the world beside the player. Release those
                // entities back to GTA and let normal distance cleanup handle the
                // visible aftermath; explicit Reset still owns emergency deletion.
                ReleaseBrokenUnit(unit);
                _units.Remove(unit);
                LogDebug("POLICE_BACKUP_UNIT_UNAVAILABLE", reason);
            }
        }

        private bool TryRecoverUnitDriver(BackupUnit unit)
        {
            if (unit == null || unit.Vehicle == null || !unit.Vehicle.Exists())
                return false;
            if (unit.Driver != null && unit.Driver.Exists() && !unit.Driver.IsDead)
                return true;

            Ped replacement = unit.Officers == null ? null : unit.Officers
                .FirstOrDefault(officer => officer != null && officer.Exists() && !officer.IsDead
                    && (_npcEscortOfficer == null || !_npcEscortOfficer.Exists()
                        || _npcTransportStarted
                        || officer.Handle != _npcEscortOfficer.Handle));
            if (replacement == null)
                return false;

            try
            {
                PrepareOfficer(replacement);
                unit.Driver = replacement;
                unit.DriverReleasedForGroupSupport = false;
                unit.Arrived = false;
                unit.LastDriveTaskAt = DateTime.MinValue;
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.LastProgressAt = DateTime.UtcNow;
                unit.RouteRecoveryCount = 0;
                if (replacement.CurrentVehicle == null
                    || !replacement.CurrentVehicle.Exists()
                    || replacement.CurrentVehicle.Handle != unit.Vehicle.Handle)
                    replacement.Task.EnterVehicle(
                        unit.Vehicle, VehicleSeat.Driver, 12000, 1.0f);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DRIVER_RECOVERY_FAILED", ex);
                return false;
            }
        }

        private static string DescribeUnavailableUnit(BackupUnit unit)
        {
            if (unit == null)
                return "Unit=null";
            string vehicle = unit.Vehicle == null ? "null"
                : !unit.Vehicle.Exists() ? "missing" : unit.Vehicle.Handle.ToString();
            string driver = unit.Driver == null ? "null"
                : !unit.Driver.Exists() ? "missing"
                    : unit.Driver.Handle + (unit.Driver.IsDead ? ":dead" : ":alive");
            int livingOfficers = unit.Officers == null ? 0 : unit.Officers.Count(
                officer => officer != null && officer.Exists() && !officer.IsDead);
            return "Vehicle=" + vehicle + "; Driver=" + driver
                + "; LivingOfficers=" + livingOfficers;
        }

        private static void ReleaseBrokenUnit(BackupUnit unit)
        {
            if (unit == null)
                return;
            try
            {
                if (unit.BackupBlip != null && unit.BackupBlip.Exists())
                    unit.BackupBlip.Delete();
            }
            catch { }
            foreach (Ped officer in unit.Officers == null
                ? Enumerable.Empty<Ped>() : unit.Officers.ToArray())
            {
                try
                {
                    if (officer == null || !officer.Exists())
                        continue;
                    SetNpcCollisionProof(officer, false);
                    officer.IsPersistent = false;
                    officer.MarkAsNoLongerNeeded();
                }
                catch { }
            }
            try
            {
                if (unit.Vehicle != null && unit.Vehicle.Exists())
                {
                    SetNpcCollisionProof(unit.Vehicle, false);
                    unit.Vehicle.IsPersistent = false;
                    unit.Vehicle.MarkAsNoLongerNeeded();
                }
            }
            catch { }
        }

        private static bool IsUsable(BackupUnit unit)
        {
            return unit != null && unit.Vehicle != null && unit.Vehicle.Exists()
                && unit.Driver != null && unit.Driver.Exists() && !unit.Driver.IsDead;
        }

        private static bool IsGangSupportUnitUsable(BackupUnit unit)
        {
            if (unit == null || !unit.GangRouteLocked)
                return false;
            return unit.Vehicle != null && unit.Vehicle.Exists()
                && SupportOfficers(unit).Any(officer =>
                officer != null && officer.Exists() && !officer.IsDead);
        }

        private static void DeleteUnit(BackupUnit unit)
        {
            if (unit == null) return;
            try { if (unit.BackupBlip != null && unit.BackupBlip.Exists()) unit.BackupBlip.Delete(); } catch { }
            foreach (Ped officer in unit.Officers == null ? Enumerable.Empty<Ped>() : unit.Officers.ToArray())
                try
                {
                    if (officer != null && officer.Exists())
                    {
                        SetNpcCollisionProof(officer, false);
                        officer.Delete();
                    }
                }
                catch { }
                try
                {
                    if (unit.Vehicle != null && unit.Vehicle.Exists())
                    {
                        SetNpcCollisionProof(unit.Vehicle, false);
                        unit.Vehicle.Delete();
                    }
                }
                catch { }
        }

        private static VehicleDrivingFlags EmergencyDrivingFlags
        {
            get
            {
                // Sirens provide the emergency-response behavior. Keep the
                // driver on the road, changing lanes around obstructions and
                // avoiding vehicles/objects instead of asking GTA to plough
                // through traffic with the reckless driving mode. The old
                // combination was the direct cause of sideways approaches,
                // repeated impacts, and vehicles trying to drive through
                // roadside geometry during Backup transport.
                return VehicleDrivingFlags.DrivingModeAvoidVehicles
                    | VehicleDrivingFlags.ChangeLanesAroundObstructions
                    | VehicleDrivingFlags.ForceJoinInRoadDirection
                    | VehicleDrivingFlags.PreferNavmeshRoute;
            }
        }

        private static void SetEmergencySignals(Vehicle vehicle, bool enabled)
        {
            if (vehicle == null || !vehicle.Exists())
                return;
            try
            {
                Function.Call(Hash.SET_VEHICLE_SIREN, vehicle, enabled);
                Function.Call(Hash.SET_VEHICLE_HAS_MUTED_SIRENS, vehicle, !enabled);
            }
            catch { }
        }

        private static Vector3 ResolveRoadPosition(Vector3 candidate)
        {
            Vector3 road;
            return TryGetNearbyRoadPosition(candidate, candidate, 55f, out road)
                ? road : Vector3.Zero;
        }

        private LSPDPoliceStationDefinition SelectedStation()
        {
            return _profile == null ? null : _profile.FindStation(_profile.Selection.StationId)
                ?? _profile.FindStation("mission_row") ?? _profile.Stations.FirstOrDefault();
        }

        private static Vector3 StationVehiclePosition(LSPDPoliceStationDefinition station)
        {
            if (station == null)
                return Vector3.Zero;
            Vector3 vehicle = new Vector3(station.VehicleX, station.VehicleY, station.VehicleZ);
            return vehicle == Vector3.Zero
                ? new Vector3(station.ExteriorX, station.ExteriorY, station.ExteriorZ)
                : vehicle;
        }

        private Vector3 ResolveNpcStationRouteDestination(Vector3 stationPosition)
        {
            if (stationPosition == Vector3.Zero)
                return stationPosition;
            if (_npcStationRouteDestination != Vector3.Zero)
                return _npcStationRouteDestination;

            float arrivalRadius = Math.Max(8f, _settings.ArrivalRadius);
            Vector3[] probes =
            {
                stationPosition,
                stationPosition + new Vector3(8f, 0f, 0f),
                stationPosition + new Vector3(-8f, 0f, 0f),
                stationPosition + new Vector3(0f, 8f, 0f),
                stationPosition + new Vector3(0f, -8f, 0f),
                stationPosition + new Vector3(14f, 0f, 0f),
                stationPosition + new Vector3(-14f, 0f, 0f),
                stationPosition + new Vector3(0f, 14f, 0f),
                stationPosition + new Vector3(0f, -14f, 0f)
            };

            Vector3 bestRoad = Vector3.Zero;
            float bestDistance = float.MaxValue;
            foreach (Vector3 probe in probes)
            {
                Vector3 road;
                if (!TryGetNearbyRoadPosition(
                    probe,
                    stationPosition,
                    arrivalRadius + 12f,
                    out road))
                    continue;
                float distance = road.DistanceTo(stationPosition);
                if (distance > arrivalRadius || distance >= bestDistance)
                    continue;
                bestRoad = road;
                bestDistance = distance;
            }

            if (bestRoad != Vector3.Zero)
            {
                _npcStationRouteDestination = bestRoad;
                LogRuntime("POLICE_BACKUP_NPC_STATION_ROAD_ROUTE",
                    "Station=" + stationPosition
                    + "; RoadDestination=" + bestRoad
                    + "; Distance=" + bestDistance.ToString("0.0"));
                return bestRoad;
            }

            // Do not fall back to the authored station/lot coordinate. It is
            // not a vehicle route target unless GTA supplied a usable path
            // node for it. The caller will perform a bounded, explicit failure
            // instead of sending the transport across a sidewalk or building.
            _npcStationRouteDestination = Vector3.Zero;
            LogRuntime("POLICE_BACKUP_NPC_STATION_ROAD_ROUTE_UNAVAILABLE",
                "Station=" + stationPosition
                + "; ArrivalRadius=" + arrivalRadius.ToString("0.0")
                + "; Fallback=None");
            return Vector3.Zero;
        }

        private string ResolveVehicleModel()
        {
            LSPDPoliceVehicleDefinition police = _profile == null ? null : _profile.FindVehicle("police");
            return police == null || string.IsNullOrWhiteSpace(police.ModelName) ? "police" : police.ModelName;
        }

        private string ResolveOfficerModel(bool preferFavorite)
        {
            if (_profile != null && preferFavorite && _settings.PreferSavedFavoritePed
                && !string.IsNullOrWhiteSpace(_profile.PreferredBackupPedModelName))
                return _profile.PreferredBackupPedModelName;
            LSPDPoliceModelDefinition patrol = _profile == null ? null : _profile.FindPed("lspd_male_patrol");
            if (patrol != null && !string.IsNullOrWhiteSpace(patrol.ModelName)) return patrol.ModelName;
            LSPDPoliceModelDefinition first = _profile == null ? null : _profile.PedModels.FirstOrDefault();
            return first == null || string.IsNullOrWhiteSpace(first.ModelName) ? "s_m_y_cop_01" : first.ModelName;
        }

        private static bool IsUsableModel(Model model, bool vehicle)
        {
            try { return model.IsValid && model.IsInCdImage && (vehicle ? model.IsVehicle : model.IsPed); }
            catch { return false; }
        }

        private static void ReleaseModel(Model model)
        {
            try { if (model.IsValid) model.MarkAsNoLongerNeeded(); } catch { }
        }

        private void ReleasePreparedModelRequests()
        {
            if (!string.IsNullOrWhiteSpace(_vehicleModelName))
                ReleaseModel(new Model(_vehicleModelName));
            if (!string.IsNullOrWhiteSpace(_officerModelName))
                ReleaseModel(new Model(_officerModelName));
        }

        private void Fail(string reason)
        {
            LSPDNPCResponse linkedNpcResponse = _npcResponse;
            LogDebug("POLICE_BACKUP_FAILED", reason ?? string.Empty);
            // A Backup failure is not a Dispatch cancellation. Using the
            // Dispatch-cancelled event made a failed NPC support request sound
            // as if the active case itself had been cancelled. Keep the audio
            // inside the Backup owner.
            Report("lsimmersivelife.police.backup_reply.unavailable", "failed");
            Notify("~r~POLICE BACKUP~s~\n" + reason);
            if (linkedNpcResponse != null)
                linkedNpcResponse.CancelBackupHandoff(reason);
            Reset();
            _state = LSPDBackupAssignmentState.Failed;
        }

        private void SetState(LSPDBackupAssignmentState state)
        {
            if (_state == state) return;

            BackupUnit leadUnit = _units.FirstOrDefault();
            Ped target = _incident != null && _incident.Suspect != null
                ? _incident.Suspect
                : (_npcResponse == null ? null : _npcResponse.ActiveSubject);
            LSDeveloperRuntime.StateTransition(
                _log == null ? string.Empty : _log.SessionId,
                "Backup",
                "POLICE_BACKUP_STATE",
                _state.ToString(),
                state.ToString(),
                leadUnit == null || leadUnit.Driver == null ? 0 : leadUnit.Driver.Handle,
                target == null ? 0 : target.Handle,
                leadUnit == null || leadUnit.Vehicle == null ? 0 : leadUnit.Vehicle.Handle,
                "Assignment=" + _operationId);

            _state = state;
            _stateChangedAt = DateTime.UtcNow;
            LogRuntime("POLICE_BACKUP_STATE", "State=" + state + "; Assignment=" + _operationId);
        }

        private void Report(string eventId, string stage)
        {
            try { _audio.Report(eventId, "backup-" + _operationId, stage, "backup"); } catch { }
        }
        private static void Notify(string message) { try { Notification.PostTicker(message, false, false); } catch { } }
        private void LogRuntime(string category, string message) { if (_log != null) _log.Runtime(category, message); }
        private void LogDebug(string category, string message) { if (_log != null) _log.Debug(category, message); }
        private void LogException(string category, Exception ex) { if (_log != null) _log.Exception(category, ex); }
    }

    /// <summary>Lifecycle of one owned player-requested support force.</summary>
    internal enum LSPDBackupAssignmentState
    {
        None,
        Requested,
        PreparingAssets,
        EnRoute,
        Arrived,
        Supporting,
        Intercepting,
        Securing,
        AssignmentComplete,
        StandingDown,
        Failed
    }
}
