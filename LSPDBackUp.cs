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
        // Pursuit support should stage close enough to join the evolving stop,
        // while remaining far enough away to avoid spawning on top of actors.
        // Every point still has to resolve to a live vehicle road node.
        private const float MinimumPursuitSpawnDistance = 42f;
        private const float MaximumPursuitSpawnDistance = 110f;
        private const float DispatchVehicleCloseDistance = 32f;
        private const float DispatchVehicleStoppedSpeed = 1.25f;
        private const float DispatchOfficerContainmentDistance = 5.5f;
        private const float DispatchOfficerContainmentStartDistance = 14f;
        private const float DispatchFootPursuitTaskSpeed = 4.0f;
        private const float DispatchFootLeadOffsetY = 5.0f;
        private const float DispatchFootLeadContainmentDistance = 8.0f;
        private const int NpcFootInterceptionRouteRefreshSeconds = 3;
        private const int NpcFootInterceptionRouteFreshSeconds = 4;
        private const float DispatchFootStoppedSpeedThreshold = 0.75f;
        private const int DispatchFootSpeedRefreshMilliseconds = 1000;
        private const int DispatchFootApproachProgressSampleMilliseconds = 3000;
        private const int DispatchFootApproachStallTimeoutSeconds = 10;
        private const int MaximumDispatchFootApproachRecoveries = 2;
        private const float DispatchFootApproachProgressDistance = 2f;
        private const int DispatchArrestCombatCheckMilliseconds = 150;
        private const int DispatchInteriorAccessTimeoutSeconds = 75;
        private const int DispatchInteriorAccessMaximumDoorRetries = 3;
        private const float NpcFootInterceptionVehicleDistance = 45f;
        private const float NpcCustodyTransportStagingDistance = 16f;
        private const float NpcCustodyTransportMaximumSceneDistance = 28f;
        private const float NpcCustodyTransportMinimumSceneDistance = 7f;
        private const int NpcVehicleStopSettleMilliseconds = 1200;
        private const int NpcVehicleInterceptionLogMilliseconds = 5000;
        private const int NpcVehicleInterceptionRouteRefreshMilliseconds = 3000;
        private const int NpcVehicleInterceptionRouteFailureLimit = 3;
        private const int NpcVehiclePursuitRequiredUnitCount = 2;
        private const int NpcVehicleLeadProgressSampleMilliseconds = 3000;
        private const float NpcVehicleContainmentRadius = 32f;
        private const float NpcVehicleLeadResumeChaseDistance = 70f;
        private const float NpcVehicleLeadProgressDistance = 5f;
        private const float NpcVehicleLeadRouteProgressDistance = 4f;
        private const int NpcBackgroundDepartureSeconds = RouteStallTimeoutSeconds;
        private const float NpcBackgroundDepartureMinimumDistance = 28f;
        private const int NpcDetachedCleanupSampleMilliseconds = 1000;
        private const int MaximumNpcDetachedCleanupOperations = 4;

        private readonly LSPDAudioDispatch _audio;
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveLog _log;
        private readonly LSPoliceBackupSettings _settings;
        private readonly LSPoliceCleanupSettings _cleanupSettings;
        private readonly List<BackupUnit> _units = new List<BackupUnit>();
        private LSWorldAmbientBehavior _ambientWorld;

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
        private NpcAssignmentIdentity _npcAssignmentIdentity;
        private readonly List<NpcDetachedCleanupOperation> _npcDetachedCleanupOperations =
            new List<NpcDetachedCleanupOperation>();
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
        private bool _npcCustodyTransportRequested;
        private bool _npcTransportStarted;
        private bool _npcPlayerSessionReleased;
        private bool _npcDepartureStallNotified;
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
        private DateTime _nextNpcFootInterceptionRouteAt = DateTime.MinValue;
        private DateTime _lastNpcFootInterceptionRouteAt = DateTime.MinValue;
        private bool _npcFootInterceptionCommitted;
        private DateTime _npcVehicleStoppedAt = DateTime.MinValue;
        private bool _npcVehicleContainmentReported;
        private bool _npcVehiclePursuitStartedLogged;
        private bool _npcContainmentAimLogged;
        private DateTime _lastDispatchInterceptionTrackingAt = DateTime.MinValue;
        private LSPDDispatchEvent _dispatchPursuitTargetCacheIncident;
        private DateTime _dispatchPursuitTargetCacheRefreshAt = DateTime.MinValue;
        private List<Ped> _dispatchVehiclePursuitTargetCache = new List<Ped>();
        private List<Ped> _dispatchFootPursuitTargetCache = new List<Ped>();
        private DateTime _nextDispatchFootSupportAt = DateTime.MinValue;
        private DateTime _nextDispatchGroupSceneSupportAt = DateTime.MinValue;
        private AmbientInteriorAccessRoute _dispatchInteriorRoute;
        private int _dispatchInteriorOfficerHandle;
        private int _dispatchInteriorTargetHandle;
        private int _dispatchInteriorPhase;
        private int _dispatchInteriorRecoveryCount;
        private DateTime _dispatchInteriorStartedAt = DateTime.MinValue;
        private DateTime _dispatchInteriorNextTaskAt = DateTime.MinValue;
        private bool _dispatchContainmentStartedLogged;
        private bool _dispatchComplianceRequestedLogged;
        private bool _dispatchSuspectCompliantLogged;
        private bool _dispatchVehicleRouteFailureNotified;
        private DateTime _nextDispatchComplianceAimAt = DateTime.MinValue;
        private DateTime _nextDispatchArrestCombatCheckAt = DateTime.MinValue;
        private bool _dispatchPlayerFinalArrestStartedLogged;
        private bool _dispatchPlayerFinalArrestCompletedLogged;
        private readonly Dictionary<int, int> _dispatchFootInterceptorBySuspect =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> _dispatchFootLeadBySuspect =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> _dispatchGroupSceneOfficerBySuspect =
            new Dictionary<int, int>();
        private readonly Dictionary<int, DispatchFootApproachTaskState>
            _dispatchFootApproachTaskStates =
                new Dictionary<int, DispatchFootApproachTaskState>();
        private int _dispatchFootSpeedTargetHandle;
        private float _dispatchFootTargetSpeed = float.MaxValue;
        private DateTime _dispatchFootSpeedSampleAt = DateTime.MinValue;
        private bool _dispatchFootSpeedReadFailedLogged;
        private int _dispatchFootFormationTargetHandle;
        private DateTime _dispatchFootFormationSampleAt = DateTime.MinValue;
        private float _dispatchFootRearDistance = float.MaxValue;
        private float _dispatchFootLeadDistance = float.MaxValue;
        private bool _dispatchFootLeadAhead;
        private readonly HashSet<int> _dispatchFootComplianceLogged =
            new HashSet<int>();
        private readonly HashSet<long> _dispatchArrestCombatReleasedPairs =
            new HashSet<long>();
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
            internal bool NpcVehicleChaseTaskIssued;
            internal bool NpcLeadRoadRecoveryActive;
            internal DateTime NextNpcVehicleProgressCheckAt = DateTime.MinValue;
            internal int NpcVehicleChaseRecoveryAttempts;
            internal bool NpcVehicleChaseRecoveryDegradedLogged;
            internal bool VehiclePursuitLead;
            internal bool VehiclePursuitBlocker;
            internal bool NpcPursuitRolesInitialized;
            internal int NpcPursuitTargetHandle;
            internal int NpcPursuitVehicleHandle;
            internal DateTime NextNpcBlockerRouteAt = DateTime.MinValue;
            internal int NpcBlockerRouteFailures;
            internal bool NpcBlockerFallbackToTrail;
            internal bool NpcBlockerRouteAttempted;
            internal Vector3 NpcBlockerLastProgressPosition = Vector3.Zero;
            internal bool NpcPursuitDriverTaskCleared;
            internal bool VehicleContainmentStopRequested;
            internal bool DispatchGroupSceneSupport;
            internal bool DispatchGroupSceneExitRequested;
            internal int DispatchGroupSceneSupportTargetHandle;
            internal DateTime NextDispatchGroupSceneExitTaskAt = DateTime.MinValue;
            internal int DispatchFootPursuitSuspectHandle;
            internal DateTime NextDispatchFootPursuitRouteAt = DateTime.MinValue;
            internal int DispatchVehiclePursuitSuspectHandle;
            internal int DispatchVehiclePursuitVehicleHandle;
            internal bool DispatchVehiclePursuitChaseTaskIssued;
            internal DateTime DispatchVehicleStoppedAt = DateTime.MinValue;
            internal DateTime NextDispatchVehicleExitTaskAt = DateTime.MinValue;
            internal DateTime NextPursuitProgressCheckAt = DateTime.MinValue;
            internal float LastPursuitTargetDistance = float.MaxValue;
            internal float LastPursuitGoalDistance = float.MaxValue;
            internal bool DispatchVehicleExitRequested;
            internal bool PursuitRouteRecoveryExhausted;
            internal bool PursuitRouteFailureLogged;
            internal bool PursuitBlockerHolding;
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

        private sealed class NpcAssignmentIdentity
        {
            internal readonly string InteractionId;
            internal readonly int CitizenHandle;
            internal readonly int VehicleHandle;
            internal readonly string EscalationType;
            internal readonly string OperationId;
            internal readonly Ped Citizen;
            internal readonly DateTime CreatedAt;
            internal LSPDBackupAssignmentState LifecycleState;

            internal NpcAssignmentIdentity(
                LSPDNPCResponse response,
                string escalationType,
                string operationId,
                DateTime createdAt)
            {
                InteractionId = response == null ? string.Empty : response.InteractionId;
                Citizen = response == null ? null : response.ActiveSubject;
                CitizenHandle = Citizen == null ? 0 : Citizen.Handle;
                VehicleHandle = response == null
                    ? 0 : response.OriginalContactVehicleHandle;
                EscalationType = escalationType ?? string.Empty;
                OperationId = operationId ?? string.Empty;
                CreatedAt = createdAt;
                LifecycleState = LSPDBackupAssignmentState.None;
            }

            internal bool Matches(LSPDNPCResponse response, string escalationType)
            {
                return MatchesContact(response)
                    && string.Equals(EscalationType, escalationType,
                        StringComparison.Ordinal);
            }

            internal bool MatchesContact(LSPDNPCResponse response)
            {
                if (response == null)
                    return false;
                Ped citizen = response.ActiveSubject;
                return !string.IsNullOrWhiteSpace(InteractionId)
                    && string.Equals(InteractionId, response.InteractionId,
                        StringComparison.Ordinal)
                    && CitizenHandle != 0 && citizen != null
                    && CitizenHandle == citizen.Handle
                    && VehicleHandle == response.OriginalContactVehicleHandle;
            }
        }

        private sealed class NpcDetachedCleanupOperation
        {
            internal NpcAssignmentIdentity Identity;
            internal List<BackupUnit> Units = new List<BackupUnit>();
            internal Vector3 DepartureOrigin;
            internal DateTime RetainedAt;
            internal DateTime NextSampleAt;
            internal DateTime CleanupStartedAt = DateTime.MinValue;
            internal bool DepartureConfirmed;
            internal bool CleanupLogged;
            internal bool SampleFailureLogged;
        }

        private sealed class DispatchFootApproachTaskState
        {
            internal int TargetHandle;
            internal bool LeadRole;
            internal DateTime NextProgressSampleAt;
            internal DateTime LastProgressAt;
            internal float LastTargetDistance = float.MaxValue;
            internal int RecoveryCount;
            internal bool RecoveryExhausted;
        }

        private sealed class DispatchPursuitAssignment
        {
            internal Ped Suspect;
            internal Vehicle Vehicle;
            internal bool SceneSupport;
            internal int SlotIndex;
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

        internal void AttachAmbientWorld(LSWorldAmbientBehavior ambientWorld)
        {
            _ambientWorld = ambientWorld;
        }
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
            _officerModelName = ResolveOfficerModel(
                preferSavedFavorite,
                _profile == null ? string.Empty : _profile.PreferredDispatchBackupPedModelName);
            _operationId = Guid.NewGuid().ToString("N");
            _assetPreparationDeadline = DateTime.UtcNow.AddSeconds(AssetPreparationTimeoutSeconds);
            _nextAssetRequestAt = DateTime.MinValue;
            _arrivalReported = false;
            _lastSupportTaskAt = DateTime.MinValue;
            SetState(LSPDBackupAssignmentState.Requested);
            BeginAssetPreparation();
            if (_incident == null)
                return "Backup could not prepare the configured Police vehicle or officer model.";
            Report("lsimmersivelife.police.backup_reply.acknowledged", "requested");
            if (_incident.CurrentPursuitTarget != null)
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
            bool preferSavedFavorite,
            bool custodyTransportRequest = false)
        {
            if (!_settings.Enabled)
                return "Backup is disabled in LS Immersive settings.";
            if (npcResponse == null || !npcResponse.CanRequestBackup)
                return "NPC Backup requires a detained or fleeing citizen contact.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            string escalationType = custodyTransportRequest
                ? "CustodyTransport" : npcResponse.CurrentStageName;
            string requestedOperationId = Guid.NewGuid().ToString("N");
            if (Active)
            {
                if (_npcAssignmentIdentity != null
                    && _npcAssignmentIdentity.OperationId == _operationId
                    && _npcAssignmentIdentity.Matches(npcResponse, escalationType))
                {
                    LogRuntime("POLICE_BACKUP_NPC_ASSIGNMENT_IDENTITY_REUSED",
                        "Interaction=" + _npcAssignmentIdentity.InteractionId
                        + "; Operation=" + _operationId
                        + "; Citizen=" + _npcAssignmentIdentity.CitizenHandle
                        + "; Vehicle=" + _npcAssignmentIdentity.VehicleHandle
                        + "; Escalation=" + escalationType
                        + "; Lifecycle=" + _state);
                    return "Backup is already assigned to this exact Citizen escalation.";
                }

                if (_npcAssignmentIdentity != null)
                {
                    string detachReason;
                    if (!TryDetachNpcTransportForNewEscalation(
                        npcResponse, escalationType, requestedOperationId,
                        out detachReason))
                    {
                        return "The previous Citizen Backup operation is still active; the new request was not accepted. "
                            + detachReason;
                    }
                }
                else if (_state == LSPDBackupAssignmentState.StandingDown)
                    Reset();
                else if (Active)
                    return "A previous backup assignment is still active.";
            }

            _incident = null;
            _convoy = null;
            _crimeActivity = null;
            _npcResponse = npcResponse;
            _vehicleModelName = ResolveVehicleModel();
            _officerModelName = ResolveOfficerModel(
                preferSavedFavorite,
                _profile == null ? string.Empty : _profile.PreferredCitizenBackupPedModelName);
            _operationId = requestedOperationId;
            _npcAssignmentIdentity = new NpcAssignmentIdentity(
                npcResponse, escalationType, _operationId, DateTime.UtcNow);
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
            _npcCustodyTransportRequested = custodyTransportRequest;
            _npcTargetWasFleeing = npcResponse.IsFleeing
                || npcResponse.WasFleeingFootContact
                || npcResponse.WasFleeingTrafficContact;
            _npcOfficerExitRequested = false;
            _npcTransportStarted = false;
            _npcPlayerSessionReleased = false;
            _npcDepartureStallNotified = false;
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
            _nextNpcFootInterceptionRouteAt = DateTime.MinValue;
            _lastNpcFootInterceptionRouteAt = DateTime.MinValue;
            _npcFootInterceptionCommitted = false;
            _npcVehicleStoppedAt = DateTime.MinValue;
            _npcVehicleContainmentReported = false;
            _npcVehiclePursuitStartedLogged = false;
            _npcContainmentAimLogged = false;
            SetState(LSPDBackupAssignmentState.Requested);
            BeginAssetPreparation();
            if (_state == LSPDBackupAssignmentState.Failed)
                return "Backup could not prepare the configured Police vehicle or officer model.";
            Report("lsimmersivelife.police.backup.requested", "npc-contact-requested");
            LogRuntime("POLICE_BACKUP_NPC_REQUESTED",
                "Operation=" + _operationId
                + "; Interaction=" + _npcAssignmentIdentity.InteractionId
                + "; Citizen=" + _npcAssignmentIdentity.CitizenHandle
                + "; Vehicle=" + _npcAssignmentIdentity.VehicleHandle
                + "; Escalation=" + _npcAssignmentIdentity.EscalationType
                + "; Lifecycle=" + _state
                + "; Fleeing=" + _npcTargetWasFleeing
                + "; Target=" + npcResponse.SupportPosition
                + "; Vehicle=" + _vehicleModelName
                + "; Officer=" + _officerModelName
                + "; CitizenTransportRequest=" + _npcCustodyTransportRequested);
            return custodyTransportRequest
                ? "Backup transport requested. One Police unit is staging near the citizen scene."
                : "Backup requested. One Police unit is staging for the citizen contact.";
        }

        internal bool HasNpcAssignmentFor(LSPDNPCResponse npcResponse,
            bool custodyTransportRequest = false)
        {
            string escalationType = custodyTransportRequest
                ? "CustodyTransport"
                : npcResponse == null ? string.Empty : npcResponse.CurrentStageName;
            return Active && _npcResponse != null
                && ReferenceEquals(_npcResponse, npcResponse)
                && _npcAssignmentIdentity != null
                && _npcAssignmentIdentity.OperationId == _operationId
                && _npcAssignmentIdentity.Matches(npcResponse, escalationType);
        }

        internal bool HasNpcAssignmentForContact(LSPDNPCResponse npcResponse)
        {
            return Active && _state != LSPDBackupAssignmentState.StandingDown
                && _npcResponse != null
                && ReferenceEquals(_npcResponse, npcResponse)
                && _npcAssignmentIdentity != null
                && _npcAssignmentIdentity.OperationId == _operationId
                && _npcAssignmentIdentity.MatchesContact(npcResponse);
        }

        internal bool RequestNpcCustodyTransport(LSPDNPCResponse npcResponse)
        {
            if (!HasNpcAssignmentForContact(npcResponse)
                || !npcResponse.CanBeginBackupTransportHandoff
                || _npcTransportStarted)
                return false;

            if (!_npcCustodyTransportRequested)
            {
                _npcCustodyTransportRequested = true;
                LogRuntime("POLICE_BACKUP_NPC_CUSTODY_TRANSPORT_ACCEPTED",
                    "Operation=" + _operationId
                    + "; Interaction=" + _npcAssignmentIdentity.InteractionId
                    + "; Citizen=" + _npcAssignmentIdentity.CitizenHandle
                    + "; OriginalVehicle=" + _npcAssignmentIdentity.VehicleHandle
                    + "; OriginalEscalation=" + _npcAssignmentIdentity.EscalationType
                    + "; Lifecycle=" + _state
                    + "; SameOperation=true; PhysicalCuffsConfirmed=true");
            }
            return true;
        }

        private bool TryDetachNpcTransportForNewEscalation(
            LSPDNPCResponse newResponse,
            string newEscalationType,
            string newOperationId,
            out string reason)
        {
            reason = string.Empty;
            NpcAssignmentIdentity previous = _npcAssignmentIdentity;
            BackupUnit unit = _npcTransportUnit;
            Ped newCitizen = newResponse == null ? null : newResponse.ActiveSubject;
            float previousDistance = float.NaN;
            try
            {
                if (IsUsable(unit) && IsAvailablePed(newCitizen))
                    previousDistance = unit.Vehicle.Position.DistanceTo(
                        newCitizen.Position);
            }
            catch (Exception ex)
            {
                reason = "The prior Backup/Citizen distance could not be read: "
                    + ex.GetType().Name;
            }
            int previousCitizenVehicleHandle = 0;
            bool previousCitizenStateReadable = previous != null
                && TryReadPedCurrentVehicleHandle(previous.Citizen,
                    out previousCitizenVehicleHandle);
            bool previousCitizenLoaded = previousCitizenStateReadable
                && IsUsable(unit)
                && previousCitizenVehicleHandle == unit.Vehicle.Handle;

            if (previous == null || !_npcTransportStarted
                || !previousCitizenLoaded)
            {
                if (string.IsNullOrWhiteSpace(reason))
                    reason = previousCitizenStateReadable
                        ? "The prior Citizen is not confirmed loaded in its assigned Backup vehicle."
                        : "The prior Citizen's current vehicle state could not be read safely.";
                LogRuntime("POLICE_BACKUP_NPC_ASSIGNMENT_REPLACEMENT_REJECTED",
                    "PreviousOperation=" + (previous == null ? string.Empty : previous.OperationId)
                    + "; PreviousInteraction=" + (previous == null ? string.Empty : previous.InteractionId)
                    + "; NewInteraction=" + (newResponse == null ? string.Empty : newResponse.InteractionId)
                    + "; PreviousCitizen=" + (previous == null ? 0 : previous.CitizenHandle)
                    + "; NewCitizen=" + (newCitizen == null ? 0 : newCitizen.Handle)
                    + "; PreviousVehicle=" + (previous == null ? 0 : previous.VehicleHandle)
                    + "; PreviousEscalation=" + (previous == null ? string.Empty : previous.EscalationType)
                    + "; Escalation=" + (newEscalationType ?? string.Empty)
                    + "; Distance=" + (float.IsNaN(previousDistance)
                        ? "Unavailable" : previousDistance.ToString("0.0"))
                    + "; Lifecycle=" + _state
                    + "; Decision=Reject; Reason=PriorCustodyNotPhysicallyConfirmed"
                    + "; NewOperation=" + (newOperationId ?? string.Empty));
                return false;
            }

            if (_npcDetachedCleanupOperations.Count
                >= MaximumNpcDetachedCleanupOperations)
            {
                reason = "Bounded Backup background-cleanup capacity is full.";
                LogRuntime("POLICE_BACKUP_NPC_ASSIGNMENT_REPLACEMENT_REJECTED",
                    "PreviousOperation=" + previous.OperationId
                    + "; PreviousInteraction=" + previous.InteractionId
                    + "; NewInteraction=" + newResponse.InteractionId
                    + "; PreviousCitizen=" + previous.CitizenHandle
                    + "; NewCitizen=" + newCitizen.Handle
                    + "; PreviousVehicle=" + previous.VehicleHandle
                    + "; PreviousEscalation=" + previous.EscalationType
                    + "; NewEscalation=" + (newEscalationType ?? string.Empty)
                    + "; Distance=" + (float.IsNaN(previousDistance)
                        ? "Unavailable" : previousDistance.ToString("0.0"))
                    + "; Lifecycle=" + _state
                    + "; Decision=Reject; Reason=DetachedCleanupCapacityReached"
                    + "; NewOperation=" + (newOperationId ?? string.Empty));
                return false;
            }

            if (_units.Count > 2)
            {
                reason = "The prior Citizen assignment exceeded its two-unit ownership bound.";
                LogRuntime("POLICE_BACKUP_NPC_ASSIGNMENT_REPLACEMENT_REJECTED",
                    "PreviousOperation=" + previous.OperationId
                    + "; PreviousInteraction=" + previous.InteractionId
                    + "; NewInteraction=" + newResponse.InteractionId
                    + "; PreviousCitizen=" + previous.CitizenHandle
                    + "; NewCitizen=" + newCitizen.Handle
                    + "; PreviousVehicle=" + previous.VehicleHandle
                    + "; PreviousEscalation=" + previous.EscalationType
                    + "; NewEscalation=" + (newEscalationType ?? string.Empty)
                    + "; Distance=" + (float.IsNaN(previousDistance)
                        ? "Unavailable" : previousDistance.ToString("0.0"))
                    + "; Lifecycle=" + _state
                    + "; UnitCount=" + _units.Count
                    + "; Decision=Reject; Reason=PriorUnitOwnershipBoundExceeded"
                    + "; NewOperation=" + (newOperationId ?? string.Empty));
                return false;
            }

            var background = new NpcDetachedCleanupOperation
            {
                Identity = previous,
                Units = _units.ToList(),
                DepartureOrigin = _npcBackgroundDepartureOrigin,
                RetainedAt = DateTime.UtcNow,
                NextSampleAt = DateTime.MinValue,
                DepartureConfirmed = _npcPlayerSessionReleased,
                CleanupStartedAt = _npcBackgroundCleanupRequested
                    || _state == LSPDBackupAssignmentState.StandingDown
                        ? (_standDownStartedAt == DateTime.MinValue
                            ? DateTime.UtcNow : _standDownStartedAt)
                        : DateTime.MinValue
            };
            if (background.DepartureOrigin == Vector3.Zero)
                background.DepartureOrigin = unit.Vehicle.Position;

            if (background.Units.Count == 0
                || !background.Units.Contains(unit))
            {
                reason = "No usable Backup transport unit remained for the prior Citizen.";
                LogRuntime("POLICE_BACKUP_NPC_ASSIGNMENT_REPLACEMENT_REJECTED",
                    "PreviousOperation=" + previous.OperationId
                    + "; NewInteraction=" + newResponse.InteractionId
                    + "; PreviousCitizen=" + previous.CitizenHandle
                    + "; NewCitizen=" + newCitizen.Handle
                    + "; Distance=" + (float.IsNaN(previousDistance)
                        ? "Unavailable" : previousDistance.ToString("0.0"))
                    + "; Lifecycle=" + _state
                    + "; Decision=Reject; Reason=TransportUnitUnavailable"
                    + "; NewOperation=" + (newOperationId ?? string.Empty));
                return false;
            }

            _npcDetachedCleanupOperations.Add(background);
            LogRuntime("POLICE_BACKUP_NPC_ASSIGNMENT_RETIRED_FOR_NEW_ESCALATION",
                "PreviousOperation=" + previous.OperationId
                + "; PreviousInteraction=" + previous.InteractionId
                + "; NewInteraction=" + newResponse.InteractionId
                + "; PreviousCitizen=" + previous.CitizenHandle
                + "; NewCitizen=" + newCitizen.Handle
                + "; PreviousVehicle=" + previous.VehicleHandle
                + "; NewVehicle=" + (newResponse.ActivePursuitVehicle
                    ?? newResponse.OriginalContactVehicle)?.Handle
                + "; PreviousEscalation=" + previous.EscalationType
                + "; NewEscalation=" + (newEscalationType ?? string.Empty)
                + "; Distance=" + (float.IsNaN(previousDistance)
                    ? "Unavailable" : previousDistance.ToString("0.0"))
                + "; Lifecycle=" + _state
                + "; Decision=DetachedToBoundedBackgroundCleanup"
                + "; ResultOperation=" + (newOperationId ?? string.Empty)
                + "; Units=" + background.Units.Count
                + "; PriorCitizenLoaded=true");

            // Reset only this completed transport assignment. The old physical
            // units now belong to their bounded detached cleanup record and
            // cannot be deleted or retasked by the new Citizen operation.
            _units.Clear();
            Reset();
            return true;
        }

        private void ProcessNpcDetachedBackgroundCleanup(Ped player, DateTime now)
        {
            for (int operationIndex = _npcDetachedCleanupOperations.Count - 1;
                operationIndex >= 0; operationIndex--)
            {
                NpcDetachedCleanupOperation operation =
                    _npcDetachedCleanupOperations[operationIndex];
                if (operation == null || now < operation.NextSampleAt)
                    continue;
                operation.NextSampleAt = now.AddMilliseconds(
                    NpcDetachedCleanupSampleMilliseconds);

                for (int unitIndex = operation.Units.Count - 1;
                    unitIndex >= 0; unitIndex--)
                {
                    BackupUnit unit = operation.Units[unitIndex];
                    if (unit == null || !IsAvailableVehicle(unit.Vehicle))
                    {
                        ReleaseNpcDetachedUnit(operation, unitIndex,
                            "VehicleUnavailable", float.NaN);
                        continue;
                    }

                    Ped citizen = operation.Identity == null
                        ? null : operation.Identity.Citizen;
                    int citizenVehicleHandle;
                    bool citizenStateReadable = TryReadPedCurrentVehicleHandle(
                        citizen, out citizenVehicleHandle);
                    int driverVehicleHandle;
                    bool driverStateReadable = TryReadPedCurrentVehicleHandle(
                        unit.Driver, out driverVehicleHandle);
                    if (!citizenStateReadable || !driverStateReadable)
                    {
                        if (!operation.SampleFailureLogged)
                        {
                            operation.SampleFailureLogged = true;
                            LogRuntime("POLICE_BACKUP_NPC_DETACHED_SAMPLE_UNAVAILABLE",
                                "Operation=" + (operation.Identity == null
                                    ? string.Empty : operation.Identity.OperationId)
                                + "; Citizen=" + (operation.Identity == null
                                    ? 0 : operation.Identity.CitizenHandle)
                                + "; Vehicle=" + unit.Vehicle.Handle
                                + "; Reason=PedVehicleStateQueryFailed; Retry=Throttled");
                        }
                        DateTime stateReadDeadline = operation.RetainedAt.AddSeconds(
                            Math.Max(1, _settings.StandDownSeconds) * 2);
                        if (now >= stateReadDeadline)
                            ReleaseNpcDetachedUnit(operation, unitIndex,
                                "BoundedReleaseAfterPedStateReadFailure", float.NaN);
                        continue;
                    }
                    bool citizenInVehicle = citizenVehicleHandle == unit.Vehicle.Handle;
                    bool driverInVehicle = driverVehicleHandle == unit.Vehicle.Handle;
                    float distanceFromScene;
                    try
                    {
                        distanceFromScene = operation.DepartureOrigin == Vector3.Zero
                            ? 0f : unit.Vehicle.Position.DistanceTo(
                                operation.DepartureOrigin);
                    }
                    catch (Exception ex)
                    {
                        if (!operation.SampleFailureLogged)
                        {
                            operation.SampleFailureLogged = true;
                            LogRuntime("POLICE_BACKUP_NPC_DETACHED_SAMPLE_UNAVAILABLE",
                                "Operation=" + (operation.Identity == null
                                    ? string.Empty : operation.Identity.OperationId)
                                + "; Citizen=" + (operation.Identity == null
                                    ? 0 : operation.Identity.CitizenHandle)
                                + "; Vehicle=" + unit.Vehicle.Handle
                                + "; Reason=VehiclePositionQueryFailed; Exception="
                                + ex.GetType().Name + "; Retry=Throttled");
                        }
                        DateTime positionReadDeadline = operation.RetainedAt.AddSeconds(
                            Math.Max(1, _settings.StandDownSeconds) * 2);
                        if (now >= positionReadDeadline)
                            ReleaseNpcDetachedUnit(operation, unitIndex,
                                "BoundedReleaseAfterVehiclePositionFailure", float.NaN);
                        continue;
                    }

                    float distanceFromPlayer = 0f;
                    try
                    {
                        if (player != null && player.Exists())
                            distanceFromPlayer = unit.Vehicle.Position.DistanceTo(
                                player.Position);
                    }
                    catch (Exception ex)
                    {
                        if (!operation.SampleFailureLogged)
                        {
                            operation.SampleFailureLogged = true;
                            LogRuntime("POLICE_BACKUP_NPC_DETACHED_SAMPLE_UNAVAILABLE",
                                "Operation=" + (operation.Identity == null
                                    ? string.Empty : operation.Identity.OperationId)
                                + "; Citizen=" + (operation.Identity == null
                                    ? 0 : operation.Identity.CitizenHandle)
                                + "; Vehicle=" + unit.Vehicle.Handle
                                + "; Reason=PlayerDistanceQueryFailed; Exception="
                                + ex.GetType().Name + "; Retry=Throttled");
                        }
                    }

                    if (!operation.DepartureConfirmed
                        && driverInVehicle && citizenInVehicle
                        && distanceFromScene >= NpcBackgroundDepartureMinimumDistance)
                    {
                        operation.DepartureConfirmed = true;
                        if (operation.CleanupStartedAt == DateTime.MinValue)
                            operation.CleanupStartedAt = now;
                        LogRuntime("POLICE_BACKUP_NPC_DETACHED_DEPARTURE_CONFIRMED",
                            "Operation=" + (operation.Identity == null
                                ? string.Empty : operation.Identity.OperationId)
                            + "; Citizen=" + citizen.Handle
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; DistanceFromScene=" + distanceFromScene.ToString("0.0")
                            + "; SameCitizenLoaded=true; DriverInVehicle=true");
                    }

                    if (!operation.DepartureConfirmed)
                    {
                        DateTime cleanupLimit = operation.RetainedAt.AddSeconds(
                            Math.Max(1, _settings.StandDownSeconds) * 2);
                        if (now >= cleanupLimit)
                            ReleaseNpcDetachedUnit(operation, unitIndex,
                                citizenInVehicle
                                    ? "DepartureNotConfirmedWithinBoundedWindow"
                                    : "CitizenNoLongerLoadedWithinBoundedWindow",
                                distanceFromPlayer);
                        continue;
                    }

                    if (operation.CleanupStartedAt == DateTime.MinValue)
                        operation.CleanupStartedAt = now;
                    DateTime distantCleanupAt = operation.CleanupStartedAt.AddSeconds(
                        Math.Max(1, _settings.StandDownSeconds));
                    DateTime releaseDeadline = operation.CleanupStartedAt.AddSeconds(
                        Math.Max(1, _settings.StandDownSeconds) * 2);
                    if (distanceFromPlayer >= SafeCleanupDistance
                        && now >= distantCleanupAt)
                    {
                        ReleaseNpcDetachedUnit(operation, unitIndex,
                            "DepartedAndBeyondSafeCleanupRange", distanceFromPlayer);
                    }
                    else if (now >= releaseDeadline)
                    {
                        // Relinquish only the old Backup unit. Never delete its
                        // vehicle, the loaded Citizen, or the new contact.
                        ReleaseNpcDetachedUnit(operation, unitIndex,
                            "BoundedBackgroundCleanupDeadline", distanceFromPlayer);
                    }
                }

                if (operation.Units.Count == 0)
                {
                    if (!operation.CleanupLogged)
                    {
                        operation.CleanupLogged = true;
                        LogRuntime("POLICE_BACKUP_NPC_DETACHED_CLEANUP_COMPLETED",
                            "Operation=" + (operation.Identity == null
                                ? string.Empty : operation.Identity.OperationId)
                            + "; Interaction=" + (operation.Identity == null
                                ? string.Empty : operation.Identity.InteractionId)
                            + "; Citizen=" + (operation.Identity == null
                                ? 0 : operation.Identity.CitizenHandle)
                            + "; Lifecycle=" + (operation.Identity == null
                                ? LSPDBackupAssignmentState.None
                                : operation.Identity.LifecycleState));
                    }
                    _npcDetachedCleanupOperations.RemoveAt(operationIndex);
                }
            }
        }

        private void ReleaseNpcDetachedUnit(
            NpcDetachedCleanupOperation operation,
            int unitIndex,
            string reason,
            float distanceFromPlayer)
        {
            if (operation == null || unitIndex < 0
                || unitIndex >= operation.Units.Count)
                return;

            BackupUnit unit = operation.Units[unitIndex];
            int vehicleHandle = 0;
            try
            {
                if (unit != null && unit.Vehicle != null)
                    vehicleHandle = unit.Vehicle.Handle;
            }
            catch { }
            ReleaseBrokenUnit(unit);
            operation.Units.RemoveAt(unitIndex);
            LogRuntime("POLICE_BACKUP_NPC_DETACHED_UNIT_RELEASED",
                "Operation=" + (operation.Identity == null
                    ? string.Empty : operation.Identity.OperationId)
                + "; Interaction=" + (operation.Identity == null
                    ? string.Empty : operation.Identity.InteractionId)
                + "; Citizen=" + (operation.Identity == null
                    ? 0 : operation.Identity.CitizenHandle)
                + "; Vehicle=" + vehicleHandle
                + "; Distance=" + (float.IsNaN(distanceFromPlayer)
                    ? "Unavailable" : distanceFromPlayer.ToString("0.0"))
                + "; Reason=" + (reason ?? "Unknown"));
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
            if (paused)
                return;

            DateTime now = DateTime.UtcNow;
            ProcessNpcDetachedBackgroundCleanup(player, now);
            if (!Active)
                return;
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
            if (npcAssignment && _npcTransportStarted)
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
                && !_npcCustodyTransportRequested
                && MaintainNpcVehicleAssignment(npcResponse, player, now))
                return;

            bool pursuitRequested = !convoyAssignment && !crimeActivityAssignment
                && !npcAssignment && !gangAssignment
                && dispatch.Current != null
                && dispatch.Current.CurrentPursuitTarget != null;
            Ped pursuitTarget = pursuitRequested
                ? ResolveDispatchPursuitTarget(dispatch) : null;
            bool pursuit = pursuitRequested && pursuitTarget != null;
            Vehicle pursuitVehicle = pursuit
                ? ResolvePursuitVehicle(_incident, pursuitTarget)
                : null;
            if (!npcAssignment && !gangAssignment)
            {
                ObserveDispatchFinalArrest(dispatch.Current);
                ReleaseDispatchArrestCombat(dispatch.Current);
            }

            // Resolve the road node once when the pursuit assignment is
            // created. Keep each Backup vehicle on that physical approach
            // point until its officers dismount; following the runner's road
            // node every tick makes the destination outrun the vehicles and
            // prevents the foot-interception phase from starting.
            Vector3 cachedPursuitDestination = Vector3.Zero;
            if (pursuit)
                foreach (BackupUnit unit in _units)
                    if (unit != null && unit.Destination != Vector3.Zero)
                    {
                        cachedPursuitDestination = unit.Destination;
                        break;
                    }

            Vector3 destination = convoyAssignment
                ? ResolveConvoyDestination(convoy, player)
                : crimeActivityAssignment
                    ? ResolveCrimeActivityDestination(crimeActivity, player)
                    : npcAssignment
                        ? ResolveNpcDestination(npcResponse, player)
                        : gangAssignment
                            ? ResolveGangDestination(gangResponse, player)
                        : cachedPursuitDestination != Vector3.Zero
                            ? cachedPursuitDestination
                            : ResolveDestination(dispatch, player, pursuit);

            if (pursuit && _incident != null)
            {
                RefreshDispatchPursuitTargetCache(dispatch, now);
                IReadOnlyList<Ped> vehiclePursuitTargets =
                    _dispatchVehiclePursuitTargetCache;
                if (vehiclePursuitTargets != null
                    && vehiclePursuitTargets.Count > 0
                    && MaintainDispatchVehicleInterceptions(
                        dispatch, vehiclePursuitTargets, now))
                    return;
                if (pursuitVehicle == null
                    && MaintainDispatchFootPursuitApproach(
                        dispatch, player, now))
                    return;
                LogDispatchInterceptionTracking(pursuitTarget, pursuitVehicle, now);
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

                Vector3 unitDestination = pursuit
                    && unit.Destination != Vector3.Zero
                        ? unit.Destination : destination;

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
                    && unit.Vehicle.Position.DistanceTo(unitDestination)
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
                    if (unit.Vehicle.Position.DistanceTo(unitDestination) <= _settings.ArrivalRadius)
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
                    if (TryRecoverStalledRoute(unit, unitDestination, now))
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
                            + "; Destination=" + unitDestination;
                        LogRuntime("POLICE_BACKUP_ROUTE_FAILED", routeFailure);
                        Fail(routeFailure);
                        return;
                    }
                    SendTo(unit, unitDestination, false);
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
                else if (pursuit && _units.Any(unit => IsUsable(unit)
                    && unit.Arrived))
                    SupportInterception(dispatch, player);
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
                        : "lsimmersivelife.police.unit.on_scene",
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
            _npcAssignmentIdentity = null;
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
            _npcCustodyTransportRequested = false;
            _npcTransportStarted = false;
            _npcPlayerSessionReleased = false;
            _npcDepartureStallNotified = false;
            _npcRouteRetargetCount = 0;
            _npcRouteOverride = Vector3.Zero;
            _npcStationRouteDestination = Vector3.Zero;
            _npcDeliveryCompletedAt = DateTime.MinValue;
            _lastNpcVehicleInterceptionLogAt = DateTime.MinValue;
            _lastNpcFootInterceptionLogAt = DateTime.MinValue;
            _nextNpcFootInterceptionRouteAt = DateTime.MinValue;
            _lastNpcFootInterceptionRouteAt = DateTime.MinValue;
            _npcFootInterceptionCommitted = false;
            _npcVehicleStoppedAt = DateTime.MinValue;
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
            Ped currentDispatchPursuitTarget = _incident == null
                ? null : _incident.CurrentPursuitTarget;
            bool dispatchPursuit = currentDispatchPursuitTarget != null;
            List<Ped> dispatchVehicleTargets = dispatchPursuit
                ? _incident.CurrentVehiclePursuitTargets.ToList()
                : new List<Ped>();
            bool npcPursuit = _npcResponse != null && _npcResponse.HasBackupAssignment
                && _npcTargetWasFleeing && !_npcCustodyTransportRequested;
            bool movingPursuit = dispatchPursuit || npcPursuit;
            Ped movingTarget = dispatchPursuit
                ? currentDispatchPursuitTarget
                : npcPursuit ? _npcResponse.ActiveSubject : null;
            Vehicle movingTargetVehicle = dispatchPursuit
                ? ResolvePursuitVehicle(_incident, movingTarget)
                : npcPursuit
                    ? ResolvePursuitVehicle(movingTarget, _npcResponse.ActivePursuitVehicle)
                    : null;
            bool npcVehiclePursuit = npcPursuit && movingTargetVehicle != null;
            List<Ped> dispatchFootTargets = dispatchPursuit
                ? ActiveFootPursuitTargets(null)
                : new List<Ped>();
            List<Ped> dispatchSceneSupportTargets = dispatchPursuit
                ? DispatchGroupSceneTargets(_incident)
                : new List<Ped>();
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
            int requestedUnits = _npcResponse == null
                ? _settings.UnitsPerRequest
                : npcVehiclePursuit
                    ? NpcVehiclePursuitRequiredUnitCount
                    : 1;
            if (_gangResponse != null)
                requestedUnits = Math.Min(3, Math.Max(2, _settings.UnitsPerRequest));
            List<DispatchPursuitAssignment> dispatchPursuitPlan =
                new List<DispatchPursuitAssignment>();
            if (dispatchPursuit)
            {
                int fleeingCount = CountActiveDispatchFleeingSuspects(_incident);
                int vehicleTeamCount = Math.Min(2, dispatchVehicleTargets.Count);
                int branchUnits = vehicleTeamCount > 1
                    ? 4
                    : vehicleTeamCount == 1
                    ? 3
                    : Math.Min(3, Math.Max(2, fleeingCount));
                bool reserveSceneUnit = dispatchSceneSupportTargets.Count > 0;
                requestedUnits = Math.Min(4,
                    Math.Max(requestedUnits, branchUnits + (reserveSceneUnit ? 1 : 0)));
                dispatchPursuitPlan = BuildDispatchPursuitPlan(
                    requestedUnits,
                    _incident,
                    dispatchVehicleTargets,
                    dispatchFootTargets,
                    dispatchSceneSupportTargets);
                requestedUnits = dispatchPursuitPlan.Count;
                LogRuntime("POLICE_BACKUP_DISPATCH_PURSUIT_SUPPORT_SELECTED",
                    "Incident=" + _incident.Id
                    + "; SuspectsFleeing=" + fleeingCount
                    + "; Target=" + movingTarget.Handle
                    + "; Mode=" + (movingTargetVehicle == null ? "Foot" : "Vehicle")
                    + "; Units=" + requestedUnits
                    + "; VehicleTargets=" + vehicleTeamCount
                    + "; FootTargets=" + dispatchFootTargets.Count
                    + "; SceneCover=" + reserveSceneUnit
                    + "; BranchPlan=" + (vehicleTeamCount > 1
                        ? "TwoVehicleTeamsOneAheadOnePursuitEach"
                        : movingTargetVehicle == null
                            ? "FootPerRunnerUpToThree" : "OneAheadTwoBehind")
                    + (reserveSceneUnit ? "; OriginalSceneUnitReserved=true" : string.Empty));
            }
            int officersPerUnit = _gangResponse == null
                ? _settings.OfficersPerUnit
                : Math.Max(2, _settings.OfficersPerUnit);
            if (!dispatchPursuit)
                dispatchPursuitPlan = new List<DispatchPursuitAssignment>();
            for (int index = 0; index < requestedUnits; index++)
            {
                if (npcVehiclePursuit && index == 1
                    && !_units.Any(IsUsable))
                    break;

                Ped unitPursuitTarget = movingTarget;
                Vehicle unitPursuitVehicle = movingTargetVehicle;
                int pursuitSlotIndex = index;
                DispatchPursuitAssignment dispatchAssignment = dispatchPursuit
                    && index < dispatchPursuitPlan.Count
                    ? dispatchPursuitPlan[index] : null;
                bool dispatchGroupSceneSupport = dispatchAssignment != null
                    && dispatchAssignment.SceneSupport;
                if (dispatchAssignment != null)
                {
                    unitPursuitTarget = dispatchAssignment.Suspect;
                    unitPursuitVehicle = dispatchAssignment.Vehicle;
                    pursuitSlotIndex = dispatchAssignment.SlotIndex;
                }
                Vector3 spawn;
                if (dispatchGroupSceneSupport)
                {
                    if (!TryFindDispatchGroupSceneSupportStagingPosition(
                        unitPursuitTarget, player, out spawn))
                    {
                        LogRuntime("POLICE_BACKUP_GROUP_SCENE_STAGING_REJECTED",
                            "UnitIndex=" + index
                            + "; Target=" + (unitPursuitTarget == null
                                ? 0 : unitPursuitTarget.Handle)
                            + "; Reason=NO_VALID_NEARBY_SCENE_ROAD_NODE");
                        continue;
                    }
                }
                else if (movingPursuit)
                {
                    bool pursuitStaged = TryFindPursuitStagingPosition(
                        player, pursuitSlotIndex, unitPursuitTarget,
                        unitPursuitVehicle, out spawn);
                    if (!pursuitStaged
                        && (dispatchPursuit || npcVehiclePursuit))
                    {
                        // The NPC vehicle-flight rear/front roles are opposite
                        // the Dispatch slot numbering. Keep the fallback road
                        // search bounded, but map its slot so unit 0 remains
                        // behind and unit 1 remains ahead.
                        int fallbackSlot = npcVehiclePursuit
                            ? NpcVehiclePursuitRequiredUnitCount - 1
                                - pursuitSlotIndex
                            : pursuitSlotIndex;
                        pursuitStaged = TryFindPursuitBackupRoadStagingPosition(
                            fallbackSlot, unitPursuitTarget,
                            unitPursuitVehicle, out spawn);
                        if (pursuitStaged && npcVehiclePursuit)
                            LogRuntime(
                                "POLICE_BACKUP_NPC_VEHICLE_STAGING_FALLBACK_USED",
                                "Target=" + unitPursuitTarget.Handle
                                + "; UnitIndex=" + index
                                + "; Role=" + (index == 0 ? "RearPursuit" : "ForwardInterceptor")
                                + "; Position=" + spawn
                                + "; Search=BoundedAlternateVehicleRoadNodes");
                    }
                    if (!pursuitStaged)
                    {
                        LogRuntime("POLICE_BACKUP_INTERCEPTION_STAGING_REJECTED",
                            "UnitIndex=" + index
                            + "; Target=" + (unitPursuitTarget == null
                                ? 0 : unitPursuitTarget.Handle)
                            + "; TargetPosition=" + (unitPursuitTarget == null
                                ? Vector3.Zero : unitPursuitTarget.Position));
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
                    string stagingFailureCategory = movingPursuit
                        ? "POLICE_BACKUP_INTERCEPTION_STAGING_REJECTED"
                        : _gangResponse != null
                            ? "POLICE_BACKUP_GANG_STAGING_REJECTED"
                            : _npcResponse != null
                                ? "POLICE_BACKUP_NPC_STAGING_REJECTED"
                                : "POLICE_BACKUP_STAGING_REJECTED";
                    LogRuntime(stagingFailureCategory,
                        "UnitIndex=" + index
                        + "; Mode=" + (movingPursuit
                            ? (movingTargetVehicle == null ? "Foot" : "Vehicle")
                            : "Stationary")
                        + "; Target=" + (movingTarget == null ? 0 : movingTarget.Handle)
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
                        dispatchGroupSceneSupport && unitPursuitTarget != null
                            ? unitPursuitTarget.Position : destination,
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
                        VehiclePursuitBlocker = unitPursuitVehicle != null
                            && (dispatchAssignment != null
                                ? pursuitSlotIndex == 0
                                : npcVehiclePursuit && index == 1),
                        VehiclePursuitLead = unitPursuitVehicle != null
                            && (dispatchAssignment != null
                                ? pursuitSlotIndex == 1
                                : npcVehiclePursuit && index == 0),
                        NpcPursuitRolesInitialized = npcVehiclePursuit,
                        NpcPursuitTargetHandle = npcVehiclePursuit
                            && unitPursuitTarget != null ? unitPursuitTarget.Handle : 0,
                        NpcPursuitVehicleHandle = npcVehiclePursuit
                            ? unitPursuitVehicle.Handle : 0,
                        NextNpcBlockerRouteAt = DateTime.MinValue,
                        NpcBlockerRouteFailures = 0,
                        NpcBlockerFallbackToTrail = false,
                        DispatchGroupSceneSupport = dispatchGroupSceneSupport,
                        DispatchGroupSceneSupportTargetHandle = 0,
                        DispatchFootPursuitSuspectHandle = dispatchPursuit
                            && !dispatchGroupSceneSupport
                            && unitPursuitVehicle == null && unitPursuitTarget != null
                                ? unitPursuitTarget.Handle : 0,
                        DispatchVehiclePursuitSuspectHandle = unitPursuitVehicle != null
                            && unitPursuitTarget != null ? unitPursuitTarget.Handle : 0,
                        DispatchVehiclePursuitVehicleHandle = unitPursuitVehicle == null
                            ? 0 : unitPursuitVehicle.Handle,
                        LastPursuitTargetDistance = unitPursuitTarget == null
                            ? float.MaxValue
                            : spawn.DistanceTo(unitPursuitVehicle != null
                                ? unitPursuitVehicle.Position : unitPursuitTarget.Position),
                        Destination = dispatchGroupSceneSupport ? spawn : destination,
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
                    Vector3 unitDestination = dispatchGroupSceneSupport
                        ? spawn : destination;
                    if (movingPursuit && !dispatchGroupSceneSupport
                        && unitPursuitTarget != null)
                    {
                        if (npcVehiclePursuit)
                        {
                            Vector3 pursuitRoad;
                            float pursuitHeading;
                            if (TryResolveDispatchVehiclePursuitRoadNode(
                                unitPursuitTarget,
                                unitPursuitVehicle,
                                unit.VehiclePursuitBlocker,
                                out pursuitRoad,
                                out pursuitHeading))
                                unitDestination = pursuitRoad;
                            else
                                // The safe spawn staging point is already on a
                                // validated vehicle node. Keep that finite
                                // target while the live-pursuit owner retries
                                // ahead-node resolution at its bounded cadence.
                                unitDestination = spawn;
                        }
                        else
                        {
                            Vector3 targetRoad = ResolveInterceptionRoadPosition(
                                unitPursuitTarget, unitPursuitVehicle);
                            if (targetRoad != Vector3.Zero)
                                unitDestination = targetRoad;
                            else if (pursuitSlotIndex > 0)
                            {
                                Vector3 chaseRoad;
                                float chaseHeading;
                                if (TryGetNearbyVehicleRoadNode(
                                    unitPursuitTarget.Position,
                                    unitPursuitTarget.Position,
                                    45f,
                                    out chaseRoad,
                                    out chaseHeading))
                                    unitDestination = chaseRoad;
                            }
                        }
                    }
                    unit.Destination = unitDestination;
                    if (npcVehiclePursuit && unit.VehiclePursuitBlocker)
                    {
                        unit.NpcBlockerRouteAttempted = unitDestination != Vector3.Zero;
                        unit.NpcBlockerLastProgressPosition = vehicle.Position;
                    }
                    if (unitPursuitTarget != null)
                    {
                        unit.LastPursuitTargetDistance = vehicle.Position.DistanceTo(
                            unitPursuitTarget.Position);
                        unit.LastPursuitGoalDistance = vehicle.Position.DistanceTo(
                            unitDestination);
                    }
                    if (dispatchGroupSceneSupport)
                    {
                        unit.Arrived = true;
                        unit.LastProgressAt = DateTime.UtcNow;
                        SetEmergencySignals(unit.Vehicle, false);
                        try
                        {
                            Function.Call(Hash.SET_VEHICLE_HANDBRAKE,
                                unit.Vehicle, true);
                            unit.Driver.Task.ClearAll();
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_GROUP_SCENE_STAGE_HOLD_FAILED", ex);
                        }
                    }
                    else
                        SendTo(unit, unitDestination, true);
                    if (movingPursuit && unitPursuitTarget != null
                        && pursuitSlotIndex > 0)
                        unit.LastDriveTaskAt = DateTime.MinValue;
                    LogRuntime("POLICE_BACKUP_UNIT_STAGED",
                        "Vehicle=" + vehicle.Handle + "; Driver=" + driver.Handle
                        + "; Spawn=" + spawn + "; Destination=" + unitDestination
                        + "; RoadHeading=" + spawnHeading.ToString("0.0")
                        + (_npcResponse != null && _npcCustodyTransportRequested
                            ? "; Assignment=CitizenCustodyTransport; SceneDistance="
                                + spawn.DistanceTo(destination).ToString("0.0")
                            : string.Empty)
                        + (dispatchGroupSceneSupport
                            ? "; Assignment=OriginalSceneGroupCover"
                            : string.Empty)
                        + (movingPursuit && unitPursuitTarget != null
                            ? "; Suspect=" + unitPursuitTarget.Handle
                                + "; SpawnTargetDistance="
                                + spawn.DistanceTo(unitPursuitTarget.Position).ToString("0.0")
                            : string.Empty));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_BACKUP_UNIT_CREATE_FAILED", ex);
                    try { if (vehicle != null && vehicle.Exists()) vehicle.Delete(); } catch { }
                }
            }
            if (npcVehiclePursuit)
            {
                List<BackupUnit> citizenPursuitUnits = _units
                    .Where(IsUsable).Take(2).ToList();
                for (int index = 0; index < citizenPursuitUnits.Count; index++)
                {
                    BackupUnit pursuitUnit = citizenPursuitUnits[index];
                    pursuitUnit.NpcPursuitRolesInitialized = true;
                    pursuitUnit.VehiclePursuitLead = index == 0;
                    pursuitUnit.VehiclePursuitBlocker = index == 1;
                }
                LogRuntime("POLICE_BACKUP_NPC_VEHICLE_PURSUIT_ROLES_ASSIGNED",
                    "Ped=" + (movingTarget == null ? 0 : movingTarget.Handle)
                    + "; Lead=" + (citizenPursuitUnits.Count > 0
                        ? citizenPursuitUnits[0].Vehicle.Handle : 0)
                    + "; Interceptor=" + (citizenPursuitUnits.Count > 1
                        ? citizenPursuitUnits[1].Vehicle.Handle : 0)
                    + "; Units=" + citizenPursuitUnits.Count
                    + "; Requested=" + NpcVehiclePursuitRequiredUnitCount
                    + "; AssignmentStatus=" + (citizenPursuitUnits.Count
                        >= NpcVehiclePursuitRequiredUnitCount
                            ? "TwoUnitTeam" : "DegradedSingleOrNoInterceptor")
                    + "; Maximum=" + NpcVehiclePursuitRequiredUnitCount);
                if (citizenPursuitUnits.Count
                    < NpcVehiclePursuitRequiredUnitCount)
                {
                    LogRuntime("POLICE_BACKUP_NPC_VEHICLE_PURSUIT_DEGRADED",
                        "Ped=" + (movingTarget == null ? 0 : movingTarget.Handle)
                        + "; Requested=" + NpcVehiclePursuitRequiredUnitCount
                        + "; Created=" + citizenPursuitUnits.Count
                        + "; Lead=" + (citizenPursuitUnits.Count > 0
                            ? citizenPursuitUnits[0].Vehicle.Handle : 0)
                        + "; Interceptor=0"
                        + "; Reason=BOUNDED_STAGING_OR_UNIT_CREATION_FAILED"
                        + "; PlayerMayContinuePursuit=true");
                    Notify("~b~POLICE BACKUP~s~\nOnly one vehicle pursuit unit could be staged after bounded road-node retries. The Citizen contact remains active.");
                }
            }
            return _units.Count > 0;
        }

        private static List<Ped> DispatchGroupSceneTargets(LSPDDispatchEvent incident)
        {
            if (incident == null || incident.Suspects == null
                || incident.Suspects.Count < 2)
                return new List<Ped>();

            return incident.Suspects
                .Where(suspect => suspect != null && suspect.Exists()
                    && !suspect.IsDead && suspect.Health > 0
                    && !incident.BackupArrestInProgressSuspectHandles.Contains(
                        suspect.Handle)
                    && !incident.ArrestedSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == suspect.Handle)
                    && suspect.Position.DistanceTo(incident.Origin) <= 100f)
                .Where(suspect =>
                {
                    bool contained = incident.PursuitContainedSuspectHandles
                            .Contains(suspect.Handle)
                        || incident.CompliantSuspects.Any(value => value != null
                            && value.Exists() && value.Handle == suspect.Handle);
                    string response;
                    bool fleeing = incident.SuspectResponses.TryGetValue(
                        suspect.Handle, out response)
                        && string.Equals(response, "flee",
                            StringComparison.OrdinalIgnoreCase);
                    return contained || !fleeing;
                })
                .OrderByDescending(suspect => suspect.IsShooting || suspect.IsInCombat)
                .ThenBy(suspect => suspect.Position.DistanceTo(incident.Origin))
                .ToList();
        }

        private static List<DispatchPursuitAssignment> BuildDispatchPursuitPlan(
            int unitLimit,
            LSPDDispatchEvent incident,
            IList<Ped> vehicleTargets,
            IList<Ped> footTargets,
            IList<Ped> sceneTargets)
        {
            var plan = new List<DispatchPursuitAssignment>();
            int maximum = Math.Min(4, Math.Max(0, unitLimit));
            if (maximum == 0)
                return plan;

            if (sceneTargets != null && sceneTargets.Count > 0)
                plan.Add(new DispatchPursuitAssignment
                {
                    Suspect = sceneTargets[0],
                    SceneSupport = true,
                    SlotIndex = 0
                });

            var uniqueVehicles = new List<Ped>();
            var seenVehicles = new HashSet<int>();
            if (vehicleTargets != null)
                foreach (Ped suspect in vehicleTargets)
                {
                    Vehicle vehicle = ResolvePursuitVehicle(incident, suspect);
                    if (suspect != null && suspect.Exists() && !suspect.IsDead
                        && vehicle != null && vehicle.Exists()
                        && seenVehicles.Add(vehicle.Handle))
                        uniqueVehicles.Add(suspect);
                }

            var uniqueFoot = new List<Ped>();
            var seenSuspects = new HashSet<int>(
                uniqueVehicles.Select(suspect => suspect.Handle));
            if (footTargets != null)
                foreach (Ped suspect in footTargets)
                    if (suspect != null && suspect.Exists() && !suspect.IsDead
                        && seenSuspects.Add(suspect.Handle))
                        uniqueFoot.Add(suspect);

            foreach (Ped suspect in uniqueVehicles)
            {
                if (plan.Count >= maximum)
                    break;
                Vehicle vehicle = ResolvePursuitVehicle(incident, suspect);
                if (vehicle == null || !vehicle.Exists())
                    continue;
                plan.Add(new DispatchPursuitAssignment
                {
                    Suspect = suspect,
                    Vehicle = vehicle,
                    SlotIndex = 0
                });
            }

            foreach (Ped suspect in uniqueFoot)
            {
                if (plan.Count >= maximum)
                    break;
                plan.Add(new DispatchPursuitAssignment
                {
                    Suspect = suspect,
                    SlotIndex = 0
                });
            }

            while (plan.Count < maximum)
            {
                Ped nextVehicleTarget = uniqueVehicles
                    .Select(suspect => new
                    {
                        Suspect = suspect,
                        Count = plan.Count(assignment => !assignment.SceneSupport
                            && assignment.Vehicle != null
                            && assignment.Suspect.Handle == suspect.Handle)
                    })
                    .Where(value => value.Count < 3)
                    .OrderBy(value => value.Count)
                    .Select(value => value.Suspect)
                    .FirstOrDefault();
                if (nextVehicleTarget != null)
                {
                    Vehicle vehicle = ResolvePursuitVehicle(incident, nextVehicleTarget);
                    if (vehicle == null || !vehicle.Exists())
                        break;
                    int slot = plan.Count(assignment => !assignment.SceneSupport
                        && assignment.Vehicle != null
                        && assignment.Suspect.Handle == nextVehicleTarget.Handle);
                    plan.Add(new DispatchPursuitAssignment
                    {
                        Suspect = nextVehicleTarget,
                        Vehicle = vehicle,
                        SlotIndex = slot
                    });
                    continue;
                }

                Ped nextFootTarget = uniqueFoot
                    .Select(suspect => new
                    {
                        Suspect = suspect,
                        Count = plan.Count(assignment => !assignment.SceneSupport
                            && assignment.Vehicle == null
                            && assignment.Suspect.Handle == suspect.Handle)
                    })
                    .OrderBy(value => value.Count)
                    .Select(value => value.Suspect)
                    .FirstOrDefault();
                if (nextFootTarget == null)
                    break;
                int footSlot = plan.Count(assignment => !assignment.SceneSupport
                    && assignment.Vehicle == null
                    && assignment.Suspect.Handle == nextFootTarget.Handle);
                plan.Add(new DispatchPursuitAssignment
                {
                    Suspect = nextFootTarget,
                    SlotIndex = footSlot
                });
            }

            return plan;
        }

        private void SupportScene(LSPDDispatch dispatch, Ped player)
        {
            LSPDDispatchEvent incident = dispatch == null ? null : dispatch.Current;
            List<Ped> threats = dispatch.CurrentSuspects.Where(ped => ped != null
                && ped.Exists() && !ped.IsDead
                && !IsDispatchSuspectArrestProtected(incident, ped)).ToList();
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
            bool refreshSupportTasks = CanIssueSupportTasks();
            Ped officerAttacker = refreshSupportTasks
                ? FindThreatAttackingBackupOfficer(threats) : null;
            bool activeCombat = threats.Any(ped => ped.IsShooting
                || (player != null && player.Exists() && ped.IsInCombatAgainst(player)))
                || officerAttacker != null;
            if (activeCombat)
            {
                if (refreshSupportTasks)
                    foreach (BackupUnit unit in _units)
                        foreach (Ped officer in SupportOfficers(unit))
                        {
                            if (officer == null || !officer.Exists() || officer.IsDead
                                || officer.IsInVehicle()
                                || officer.IsInCombat || officer.IsShooting)
                                continue;
                            Ped target = officerAttacker
                                ?? NearestThreat(officer, threats);
                            if (target == null || !target.Exists() || target.IsDead)
                                continue;
                            AssignCombat(officer, target);
                            LogRuntime(
                                "POLICE_BACKUP_DISPATCH_DEFENSE_COMBAT_ASSIGNED",
                                "Incident=" + (dispatch.Current == null
                                    ? string.Empty : dispatch.Current.Id)
                                + "; Officer=" + officer.Handle
                                + "; Target=" + target.Handle
                                + "; AttackingOfficer=" + (officerAttacker != null)
                                + "; Task=Combat; CombatTaskIssuedForIdleOfficer=true"
                                + "; ExistingCombatTasksOverwritten=false");
                        }
                SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }

            if (refreshSupportTasks)
                foreach (BackupUnit unit in _units)
                    foreach (Ped officer in SupportOfficers(unit))
                        AimAtNearestThreat(officer, threats);
            SetState(LSPDBackupAssignmentState.Supporting);
        }

        private Ped FindThreatAttackingBackupOfficer(IEnumerable<Ped> threats)
        {
            if (threats == null)
                return null;
            foreach (Ped threat in threats)
            {
                if (threat == null || !threat.Exists() || threat.IsDead
                    || !threat.IsInCombat)
                    continue;
                foreach (BackupUnit unit in _units)
                    foreach (Ped officer in SupportOfficers(unit))
                    {
                        if (officer == null || !officer.Exists() || officer.IsDead
                            || officer.Handle == threat.Handle)
                            continue;
                        try
                        {
                            if (threat.IsInCombatAgainst(officer))
                                return threat;
                        }
                        catch
                        {
                        }
                    }
            }
            return null;
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
            if (_npcFootInterceptionCommitted)
                return false;

            float vehicleDistance = unit.Vehicle.Position.DistanceTo(subject.Position);
            bool routeRefreshAttempted = false;
            bool routeAvailable = false;
            if (now >= _nextNpcFootInterceptionRouteAt)
            {
                routeRefreshAttempted = true;
                _nextNpcFootInterceptionRouteAt = now.AddSeconds(
                    NpcFootInterceptionRouteRefreshSeconds);
                Vector3 interception = ResolveNpcDestination(
                    npcResponse, Game.Player.Character);
                Vector3 roadNode = Vector3.Zero;
                float roadHeading;
                bool hasRoadNode = interception != Vector3.Zero
                    && TryGetNearbyVehicleRoadNode(
                        interception, subject.Position, 22f,
                        out roadNode, out roadHeading);
                if (!hasRoadNode)
                    hasRoadNode = TryGetNearbyVehicleRoadNode(
                        subject.Position, subject.Position, 35f,
                        out roadNode, out roadHeading);
                if (hasRoadNode && SendTo(unit, roadNode, true))
                {
                    _lastNpcFootInterceptionRouteAt = now;
                    routeAvailable = true;
                }
            }

            bool recentRoute = _lastNpcFootInterceptionRouteAt != DateTime.MinValue
                && now < _lastNpcFootInterceptionRouteAt.AddSeconds(
                    NpcFootInterceptionRouteFreshSeconds);
            float roadDistance = unit.Destination == Vector3.Zero
                ? float.MaxValue
                : unit.Vehicle.Position.DistanceTo(unit.Destination);
            bool reachedInterception = vehicleDistance
                    <= NpcFootInterceptionVehicleDistance
                || (recentRoute && roadDistance
                    <= Math.Max(8f, _settings.ArrivalRadius));
            if (reachedInterception)
            {
                _npcFootInterceptionCommitted = true;
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
                LogRuntime("POLICE_BACKUP_NPC_FOOT_APPROACH_REACHED",
                    "Ped=" + subject.Handle
                    + "; BackupVehicle=" + unit.Vehicle.Handle
                    + "; VehicleDistance=" + vehicleDistance.ToString("0.0")
                    + "; RoadDistance=" + roadDistance.ToString("0.0")
                    + "; Arrival=" + (vehicleDistance
                        <= NpcFootInterceptionVehicleDistance
                            ? "SubjectDistance" : "ValidatedRoadNode"));
                return false;
            }

            // If an earlier arrival let the escort officer leave before the
            // target moved again, bring that same officer back to the owned
            // vehicle before the next road approach. Reuse the officer's
            // assigned seat so a one-officer unit does not ask its driver to
            // re-enter the occupied passenger seat.
            if (_npcEscortOfficer != null && _npcEscortOfficer.Exists()
                && !_npcEscortOfficer.IsDead
                && !_npcEscortOfficer.IsInVehicle()
                && routeAvailable
                && now >= _nextNpcOfficerTaskAt)
            {
                try
                {
                    _npcEscortOfficer.Task.EnterVehicle(
                        unit.Vehicle,
                        _npcEscortOfficer.Handle == unit.Driver.Handle
                            ? VehicleSeat.Driver : VehicleSeat.RightFront,
                        12000, 1.0f);
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

            if (now >= _lastNpcFootInterceptionLogAt.AddMilliseconds(
                NpcVehicleInterceptionLogMilliseconds))
            {
                _lastNpcFootInterceptionLogAt = now;
                LogRuntime("POLICE_BACKUP_NPC_FOOT_INTERCEPTION_TRACKING",
                    "Ped=" + subject.Handle
                    + "; BackupVehicle=" + unit.Vehicle.Handle
                    + "; VehicleDistance=" + vehicleDistance.ToString("0.0")
                    + "; RoadDistance=" + roadDistance.ToString("0.0")
                    + "; RouteRefreshAttempted=" + routeRefreshAttempted
                    + "; RouteAvailable=" + (routeAvailable || recentRoute)
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

            bool subjectIsOnFoot = !subject.IsInVehicle();
            foreach (BackupUnit pursuitUnit in _units)
            {
                if (!IsUsable(pursuitUnit) || pursuitUnit == unit)
                    continue;
                if (subjectIsOnFoot
                    && (pursuitUnit.NpcVehiclePursuitActive
                        || pursuitUnit.VehicleContainmentStopRequested))
                {
                    pursuitUnit.NpcVehiclePursuitActive = false;
                    HoldNpcPursuitVehicle(pursuitUnit);
                }
            }
            if (subjectIsOnFoot)
                unit.NpcVehiclePursuitActive = false;

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
                subject, npcResponse.ActivePursuitVehicle);
            if (subjectVehicle != null && subject.IsInVehicle())
            {
                _nextNpcFootInterceptionRouteAt = DateTime.MinValue;
                _lastNpcFootInterceptionRouteAt = DateTime.MinValue;
                _npcFootInterceptionCommitted = false;
            }
            if (subjectVehicle != null && subject.IsInVehicle()
                && subjectVehicle.Handle != unit.Vehicle.Handle)
            {
                bool contained = MaintainNpcVehicleInterception(
                    unit, subject, subjectVehicle, now);
                if (!contained)
                {
                    SetState(LSPDBackupAssignmentState.Intercepting);
                    return true;
                }

                BackupUnit pursuitLead;
                BackupUnit pursuitBlocker;
                ResolveNpcVehiclePursuitUnits(
                    unit, out pursuitLead, out pursuitBlocker);
                foreach (BackupUnit pursuitUnit in _units)
                    if (IsUsable(pursuitUnit))
                        pursuitUnit.NpcVehiclePursuitActive = false;
                if (!_npcInterventionStarted)
                {
                    BackupUnit containmentUnit = ResolveNpcContainmentUnit(
                        pursuitLead, pursuitBlocker, subject);
                    if (!IsUsable(containmentUnit))
                        return true;
                    if (_npcEscortOfficer == null
                        || !_npcEscortOfficer.Exists()
                        || _npcEscortOfficer.IsDead
                        || !containmentUnit.Officers.Any(officer => officer != null
                            && officer.Exists() && !officer.IsDead
                            && officer.Handle == _npcEscortOfficer.Handle))
                    {
                        _npcEscortOfficer = containmentUnit.Officers.FirstOrDefault(officer =>
                            officer != null && officer.Exists() && !officer.IsDead
                            && officer.Handle != containmentUnit.Driver.Handle)
                            ?? containmentUnit.Driver;
                    }
                    if (_npcEscortOfficer == null || !_npcEscortOfficer.Exists())
                        return true;

                    LogRuntime("POLICE_BACKUP_NPC_PHYSICAL_CONTAINMENT_STARTED",
                        "Ped=" + subject.Handle
                        + "; SubjectVehicle=" + subjectVehicle.Handle
                        + "; Officer=" + _npcEscortOfficer.Handle
                        + "; BackupVehicle=" + unit.Vehicle.Handle
                        + "; LeadUnit=" + (pursuitLead == null ? 0 : pursuitLead.Vehicle.Handle)
                        + "; BlockerUnit=" + (pursuitBlocker == null ? 0 : pursuitBlocker.Vehicle.Handle)
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
            DateTime now)
        {
            if (!IsUsable(unit) || subject == null || !subject.Exists()
                || subject.IsDead || subjectVehicle == null || !subjectVehicle.Exists())
                return false;

            BackupUnit lead;
            BackupUnit blocker;
            ResolveNpcVehiclePursuitUnits(unit, out lead, out blocker);
            if (!IsUsable(lead))
                return false;

            var pursuingUnits = _units.Where(candidate => IsUsable(candidate)
                && (candidate == lead || candidate == blocker
                    || candidate.NpcBlockerFallbackToTrail)).ToList();
            foreach (BackupUnit pursuitUnit in pursuingUnits)
            {
                bool targetChanged = pursuitUnit.NpcPursuitTargetHandle != subject.Handle
                    || pursuitUnit.NpcPursuitVehicleHandle != subjectVehicle.Handle;
                if (targetChanged)
                {
                    pursuitUnit.NpcPursuitTargetHandle = subject.Handle;
                    pursuitUnit.NpcPursuitVehicleHandle = subjectVehicle.Handle;
                    pursuitUnit.NpcBlockerRouteFailures = 0;
                    pursuitUnit.NextNpcBlockerRouteAt = now;
                    pursuitUnit.PursuitBlockerHolding = false;
                    pursuitUnit.VehicleContainmentStopRequested = false;
                    pursuitUnit.NpcBlockerRouteAttempted = false;
                    pursuitUnit.NpcBlockerLastProgressPosition = pursuitUnit.Vehicle.Position;
                    pursuitUnit.NpcPursuitDriverTaskCleared = false;
                    pursuitUnit.NpcVehicleChaseTaskIssued = false;
                    pursuitUnit.NpcLeadRoadRecoveryActive = false;
                    pursuitUnit.NpcVehicleChaseRecoveryAttempts = 0;
                    pursuitUnit.NpcVehicleChaseRecoveryDegradedLogged = false;
                    pursuitUnit.NextNpcVehicleProgressCheckAt = now;
                    pursuitUnit.LastDriveTaskAt = DateTime.MinValue;
                    pursuitUnit.LastProgressAt = now;
                    pursuitUnit.LastPursuitTargetDistance =
                        pursuitUnit.Vehicle.Position.DistanceTo(
                            subjectVehicle.Position);
                    pursuitUnit.LastPursuitGoalDistance = float.MaxValue;
                    pursuitUnit.Destination = Vector3.Zero;
                }
                pursuitUnit.NpcVehiclePursuitActive = true;
            }

            float targetSpeed = Math.Abs(subjectVehicle.Speed);
            float leadDistance = lead.Vehicle.Position.DistanceTo(subjectVehicle.Position);
            float nearestUnitDistance = leadDistance;
            foreach (BackupUnit pursuitUnit in pursuingUnits)
            {
                float unitDistance = pursuitUnit.Vehicle.Position.DistanceTo(
                    subjectVehicle.Position);
                if (unitDistance < nearestUnitDistance)
                    nearestUnitDistance = unitDistance;
            }
            bool targetStopped = targetSpeed <= DispatchVehicleStoppedSpeed;
            if (targetStopped)
            {
                if (_npcVehicleStoppedAt == DateTime.MinValue)
                    _npcVehicleStoppedAt = now;
            }
            else
            {
                _npcVehicleStoppedAt = DateTime.MinValue;
                _npcVehicleContainmentReported = false;
                foreach (BackupUnit pursuitUnit in pursuingUnits)
                    ReleaseNpcPursuitVehicleHold(pursuitUnit);
            }

            // Unit 1 keeps live pursuit pressure. A second unit can take the
            // cached road-ahead role; if no safe node or route progress appears
            // after three throttled attempts it becomes another trail unit.
            foreach (BackupUnit pursuitUnit in pursuingUnits)
            {
                bool aheadInterceptor = pursuitUnit == blocker
                    && !pursuitUnit.NpcBlockerFallbackToTrail;
                if (!aheadInterceptor)
                {
                    MaintainNpcVehicleLeadPursuit(
                        pursuitUnit, subject, subjectVehicle, now);
                    continue;
                }

                SetEmergencySignals(pursuitUnit.Vehicle,
                    !pursuitUnit.PursuitBlockerHolding);
                if (now < pursuitUnit.NextNpcBlockerRouteAt)
                    continue;
                pursuitUnit.NextNpcBlockerRouteAt = now.AddMilliseconds(
                    NpcVehicleInterceptionRouteRefreshMilliseconds);

                bool movingTowardPreviousRoute = pursuitUnit.NpcBlockerRouteAttempted
                    && pursuitUnit.Destination != Vector3.Zero
                    && pursuitUnit.Vehicle.Position.DistanceTo(pursuitUnit.Destination) > 12f
                    && pursuitUnit.Vehicle.Position.DistanceTo(
                        pursuitUnit.NpcBlockerLastProgressPosition) >= 2f;
                if (movingTowardPreviousRoute)
                    pursuitUnit.NpcBlockerRouteFailures = 0;
                bool previousRouteStalled = pursuitUnit.NpcBlockerRouteAttempted
                    && pursuitUnit.Destination != Vector3.Zero
                    && pursuitUnit.Vehicle.Position.DistanceTo(pursuitUnit.Destination) > 12f
                    && !movingTowardPreviousRoute;
                if (previousRouteStalled)
                    pursuitUnit.NpcBlockerRouteFailures++;
                pursuitUnit.NpcBlockerLastProgressPosition = pursuitUnit.Vehicle.Position;

                Vector3 blockerDestination;
                bool hasSafeAheadNode = TryResolveDispatchVehiclePursuitRoadNode(
                    subject, subjectVehicle, true,
                    out blockerDestination, out _);
                bool currentRouteFailed = !hasSafeAheadNode;
                if (!hasSafeAheadNode)
                {
                    if (!previousRouteStalled)
                        pursuitUnit.NpcBlockerRouteFailures++;
                }
                else
                {
                    Vector3 priorDestination = pursuitUnit.Destination;
                    float blockerDistance = pursuitUnit.Vehicle.Position.DistanceTo(
                        blockerDestination);
                    if (blockerDistance <= 12f)
                    {
                        pursuitUnit.PursuitBlockerHolding = true;
                        pursuitUnit.Arrived = true;
                        pursuitUnit.Destination = blockerDestination;
                        bool firstBlockHold = !pursuitUnit.VehicleContainmentStopRequested;
                        HoldNpcPursuitVehicle(pursuitUnit);
                        if (firstBlockHold)
                        {
                            LogRuntime("POLICE_BACKUP_NPC_INTERCEPTOR_POSITIONED",
                                "Ped=" + subject.Handle
                                + "; TargetVehicle=" + subjectVehicle.Handle
                                + "; Interceptor=" + pursuitUnit.Vehicle.Handle
                                + "; AheadDistance=" + (blockerDestination.DistanceTo(
                                    subjectVehicle.Position)).ToString("0.0"));
                        }
                    }
                    else
                    {
                        if (pursuitUnit.PursuitBlockerHolding)
                            ReleaseNpcPursuitVehicleHold(pursuitUnit);
                        bool routeChanged = priorDestination == Vector3.Zero
                            || priorDestination.DistanceTo(blockerDestination) >= 14f;
                        bool reachedPreviousGoal = priorDestination != Vector3.Zero
                            && pursuitUnit.Vehicle.Position.DistanceTo(priorDestination) <= 12f;
                        if (routeChanged || reachedPreviousGoal)
                        {
                            if (!SendTo(pursuitUnit, blockerDestination, true))
                                currentRouteFailed = true;
                            pursuitUnit.NpcBlockerRouteAttempted = true;
                        }
                    }
                    pursuitUnit.NpcBlockerRouteAttempted = true;
                }

                if (currentRouteFailed && !previousRouteStalled)
                    pursuitUnit.NpcBlockerRouteFailures++;

                if (pursuitUnit.NpcBlockerRouteFailures
                    >= NpcVehicleInterceptionRouteFailureLimit)
                {
                    if (pursuitUnit.VehicleContainmentStopRequested)
                        ReleaseNpcPursuitVehicleHold(pursuitUnit);
                    pursuitUnit.NpcBlockerFallbackToTrail = true;
                    pursuitUnit.VehiclePursuitBlocker = false;
                    pursuitUnit.VehiclePursuitLead = true;
                    pursuitUnit.NpcBlockerRouteFailures = 0;
                    pursuitUnit.PursuitBlockerHolding = false;
                    pursuitUnit.LastDriveTaskAt = DateTime.MinValue;
                    pursuitUnit.NextNpcBlockerRouteAt = DateTime.MinValue;
                    SetEmergencySignals(pursuitUnit.Vehicle, true);
                    LogRuntime("POLICE_BACKUP_NPC_INTERCEPTOR_FALLBACK",
                        "Ped=" + subject.Handle
                        + "; TargetVehicle=" + subjectVehicle.Handle
                        + "; Unit=" + pursuitUnit.Vehicle.Handle
                        + "; FailedAttempts=" + NpcVehicleInterceptionRouteFailureLimit
                        + "; Fallback=TrailingVehicleChase");
                }
            }

            if (now >= _lastNpcVehicleInterceptionLogAt.AddMilliseconds(
                    NpcVehicleInterceptionLogMilliseconds))
            {
                _lastNpcVehicleInterceptionLogAt = now;
                LogRuntime("POLICE_BACKUP_NPC_VEHICLE_INTERCEPTION_TRACKING",
                    "Ped=" + subject.Handle
                    + "; TargetVehicle=" + subjectVehicle.Handle
                    + "; Lead=" + lead.Vehicle.Handle
                    + "; Interceptor=" + (blocker == null ? 0 : blocker.Vehicle.Handle)
                    + "; LeadDistance=" + leadDistance.ToString("0.0")
                    + "; LeadSpeed=" + Math.Abs(lead.Vehicle.Speed).ToString("0.0")
                    + "; LeadChaseIssued=" + lead.NpcVehicleChaseTaskIssued
                    + "; LeadRoadRecovery=" + lead.NpcLeadRoadRecoveryActive
                    + "; LeadRecoveryAttempts=" + lead.NpcVehicleChaseRecoveryAttempts
                    + "; Speed=" + targetSpeed.ToString("0.0")
                    + "; SustainedStop=" + (_npcVehicleStoppedAt != DateTime.MinValue
                        && now >= _npcVehicleStoppedAt.AddMilliseconds(
                            NpcVehicleStopSettleMilliseconds)));
            }

            bool sustainedStop = _npcVehicleStoppedAt != DateTime.MinValue
                && now >= _npcVehicleStoppedAt.AddMilliseconds(
                    NpcVehicleStopSettleMilliseconds);
            if (!sustainedStop
                || nearestUnitDistance > NpcVehicleContainmentRadius)
                return false;

            foreach (BackupUnit pursuitUnit in pursuingUnits)
                if (pursuitUnit.Vehicle.Position.DistanceTo(subjectVehicle.Position)
                    <= NpcVehicleContainmentRadius + 12f)
                    HoldNpcPursuitVehicle(pursuitUnit);

            if (!_npcVehicleContainmentReported)
            {
                _npcVehicleContainmentReported = true;
                LogRuntime("POLICE_BACKUP_NPC_VEHICLE_CONTAINED",
                    "Ped=" + subject.Handle
                    + "; TargetVehicle=" + subjectVehicle.Handle
                    + "; Lead=" + lead.Vehicle.Handle
                    + "; Interceptor=" + (blocker == null ? 0 : blocker.Vehicle.Handle)
                    + "; NearestContainmentDistance="
                    + nearestUnitDistance.ToString("0.0")
                    + "; TargetSpeed=" + targetSpeed.ToString("0.00")
                    + "; SustainedStopMilliseconds=" + NpcVehicleStopSettleMilliseconds
                    + "; SameCitizen=true");
            }
            SetState(LSPDBackupAssignmentState.Intercepting);
            return true;
        }

        private void MaintainNpcVehicleLeadPursuit(
            BackupUnit unit,
            Ped subject,
            Vehicle subjectVehicle,
            DateTime now)
        {
            if (!IsUsable(unit) || subject == null || !subject.Exists()
                || subjectVehicle == null || !subjectVehicle.Exists())
                return;

            SetEmergencySignals(unit.Vehicle, true);
            if (now >= unit.NextNpcVehicleProgressCheckAt)
            {
                unit.NextNpcVehicleProgressCheckAt = now.AddMilliseconds(
                    NpcVehicleLeadProgressSampleMilliseconds);
                float targetDistance = unit.Vehicle.Position.DistanceTo(
                    subjectVehicle.Position);
                if (unit.NpcLeadRoadRecoveryActive)
                {
                    float goalDistance = unit.Destination == Vector3.Zero
                        ? float.MaxValue
                        : unit.Vehicle.Position.DistanceTo(unit.Destination);
                    bool routeProgress = unit.LastPursuitGoalDistance < float.MaxValue
                        && goalDistance <= unit.LastPursuitGoalDistance
                            - NpcVehicleLeadRouteProgressDistance;
                    if (routeProgress)
                        unit.LastProgressAt = now;
                    unit.LastPursuitGoalDistance = goalDistance;

                    if (goalDistance <= 12f
                        || targetDistance <= NpcVehicleLeadResumeChaseDistance)
                    {
                        unit.NpcLeadRoadRecoveryActive = false;
                        unit.NpcVehicleChaseTaskIssued = false;
                        unit.Destination = Vector3.Zero;
                        unit.LastDriveTaskAt = DateTime.MinValue;
                        unit.LastProgressAt = now;
                        unit.LastPursuitTargetDistance = targetDistance;
                        LogRuntime("POLICE_BACKUP_NPC_VEHICLE_LEAD_RECOVERY_COMPLETED",
                            "Ped=" + subject.Handle
                            + "; TargetVehicle=" + subjectVehicle.Handle
                            + "; Lead=" + unit.Vehicle.Handle
                            + "; GoalDistance=" + goalDistance.ToString("0.0")
                            + "; TargetDistance=" + targetDistance.ToString("0.0")
                            + "; Next=LiveVehicleChase");
                    }
                    else if (now >= unit.LastProgressAt.AddSeconds(
                        RouteStallTimeoutSeconds))
                    {
                        StartNpcVehicleLeadRoadRecovery(
                            unit, subject, subjectVehicle, now);
                    }
                }
                else
                {
                    bool chaseProgress = unit.LastPursuitTargetDistance
                            < float.MaxValue
                        && targetDistance <= unit.LastPursuitTargetDistance
                            - NpcVehicleLeadProgressDistance;
                    if (chaseProgress)
                        unit.LastProgressAt = now;
                    else if (now >= unit.LastProgressAt.AddSeconds(
                        RouteStallTimeoutSeconds))
                        StartNpcVehicleLeadRoadRecovery(
                            unit, subject, subjectVehicle, now);
                }

                unit.LastPursuitTargetDistance = targetDistance;
            }

            if (unit.NpcLeadRoadRecoveryActive
                || unit.NpcVehicleChaseTaskIssued)
                return;
            if (now < unit.LastDriveTaskAt.AddMilliseconds(
                    TaskRefreshMilliseconds))
                return;

            try
            {
                // Match Dispatch's live chase ownership: issue once for the
                // same Citizen/vehicle, then use bounded progress recovery
                // rather than replacing VehicleChase on a fixed five-second timer.
                unit.Driver.Task.VehicleChase(subject);
                unit.NpcVehicleChaseTaskIssued = true;
                unit.LastDriveTaskAt = now;
                if (!_npcVehiclePursuitStartedLogged)
                {
                    _npcVehiclePursuitStartedLogged = true;
                    LogRuntime("POLICE_BACKUP_NPC_VEHICLE_PURSUIT_STARTED",
                        "Ped=" + subject.Handle
                        + "; Vehicle=" + subjectVehicle.Handle
                        + "; Lead=" + unit.Vehicle.Handle
                        + "; Mode=LiveVehicleChase; TaskIssuedOnceForTarget=true");
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_VEHICLE_PURSUIT_FAILED", ex);
                unit.NpcVehicleChaseTaskIssued = false;
                unit.LastDriveTaskAt = now;
            }
        }

        private void StartNpcVehicleLeadRoadRecovery(
            BackupUnit unit,
            Ped subject,
            Vehicle subjectVehicle,
            DateTime now)
        {
            if (!IsUsable(unit) || subject == null || !subject.Exists()
                || subjectVehicle == null || !subjectVehicle.Exists())
                return;

            if (unit.NpcVehicleChaseRecoveryAttempts
                >= NpcVehicleInterceptionRouteFailureLimit)
            {
                DegradeNpcVehicleLeadRecovery(
                    unit, subject, subjectVehicle, now,
                    "BOUNDED_RECOVERY_LIMIT_REACHED");
                return;
            }

            Vector3 recoveryRoad;
            float recoveryHeading;
            bool nodeResolved = TryResolveDispatchVehiclePursuitRoadNode(
                subject, subjectVehicle, false,
                out recoveryRoad, out recoveryHeading);
            bool sameRoute = nodeResolved && unit.Destination != Vector3.Zero
                && unit.Destination.DistanceTo(recoveryRoad) < 18f;
            if (!nodeResolved || sameRoute
                || !SendTo(unit, recoveryRoad, true))
            {
                unit.NpcVehicleChaseRecoveryAttempts++;
                unit.LastProgressAt = now;
                LogRuntime("POLICE_BACKUP_NPC_VEHICLE_LEAD_RECOVERY_REJECTED",
                    "Ped=" + subject.Handle
                    + "; TargetVehicle=" + subjectVehicle.Handle
                    + "; Lead=" + unit.Vehicle.Handle
                    + "; Attempt=" + unit.NpcVehicleChaseRecoveryAttempts
                    + "; Candidate=" + recoveryRoad
                    + "; Reason=" + (!nodeResolved ? "NO_SAFE_CURRENT_ROAD_NODE"
                        : sameRoute ? "NO_NEW_ROUTE_PROGRESS"
                        : "DRIVE_TASK_REJECTED"));
                if (unit.NpcVehicleChaseRecoveryAttempts
                    >= NpcVehicleInterceptionRouteFailureLimit)
                    DegradeNpcVehicleLeadRecovery(
                        unit, subject, subjectVehicle, now,
                        "ALL_BOUNDED_LEAD_ROUTE_RECOVERIES_FAILED");
                return;
            }

            unit.NpcVehicleChaseRecoveryAttempts++;
            unit.NpcLeadRoadRecoveryActive = true;
            unit.NpcVehicleChaseTaskIssued = false;
            unit.LastProgressAt = now;
            unit.LastPursuitGoalDistance = unit.Vehicle.Position.DistanceTo(
                unit.Destination);
            LogRuntime("POLICE_BACKUP_NPC_VEHICLE_LEAD_RECOVERY_STARTED",
                "Ped=" + subject.Handle
                + "; TargetVehicle=" + subjectVehicle.Handle
                + "; Lead=" + unit.Vehicle.Handle
                + "; Attempt=" + unit.NpcVehicleChaseRecoveryAttempts
                + "; Destination=" + unit.Destination
                + "; RoadHeading=" + recoveryHeading.ToString("0.0")
                + "; SpeedPolicy=ExistingSafeBackupRoadRoute");
        }

        private void DegradeNpcVehicleLeadRecovery(
            BackupUnit unit,
            Ped subject,
            Vehicle subjectVehicle,
            DateTime now,
            string reason)
        {
            if (!unit.NpcVehicleChaseRecoveryDegradedLogged)
            {
                unit.NpcVehicleChaseRecoveryDegradedLogged = true;
                LogRuntime("POLICE_BACKUP_NPC_VEHICLE_LEAD_RECOVERY_DEGRADED",
                    "Ped=" + subject.Handle
                    + "; TargetVehicle=" + subjectVehicle.Handle
                    + "; Lead=" + unit.Vehicle.Handle
                    + "; Attempts=" + unit.NpcVehicleChaseRecoveryAttempts
                    + "; Reason=" + reason
                    + "; Fallback=LiveVehicleChase; InterceptorRemainsAssigned=true");
            }
            unit.NpcLeadRoadRecoveryActive = false;
            unit.NpcVehicleChaseTaskIssued = false;
            unit.Destination = Vector3.Zero;
            unit.LastDriveTaskAt = DateTime.MinValue;
            unit.LastProgressAt = now;
        }

        // Citizen injury samples use this read-only, bounded view to distinguish
        // an assigned Backup Ped from Player/ambient damage. It is called only
        // after the selected Citizen's health decreases; it never issues tasks
        // or searches the world.
        internal Tuple<bool, string> CaptureNpcDamageEvidence(
            Ped citizen,
            int allegedAttackerHandle)
        {
            if (citizen == null || !citizen.Exists())
                return Tuple.Create(false,
                    "BackupSnapshot=CitizenUnavailable; BackupState=" + _state);

            const int maximumUnits = 4;
            const int maximumSupportOfficersPerUnit = 4;
            var units = new List<string>(maximumUnits);
            int inspectedUnits = 0;
            bool allegedAttackerIsBackup = false;
            for (int index = 0; index < _units.Count && inspectedUnits < maximumUnits; index++)
            {
                BackupUnit unit = _units[index];
                if (unit == null)
                    continue;
                inspectedUnits++;

                try
                {
                    int vehicleHandle = unit.Vehicle != null && unit.Vehicle.Exists()
                        ? unit.Vehicle.Handle : 0;
                    string vehicleDistance = vehicleHandle == 0
                        ? "Unavailable"
                        : unit.Vehicle.Position.DistanceTo(citizen.Position)
                            .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    string role = !unit.NpcVehiclePursuitActive
                        ? "NotCitizenVehiclePursuit"
                        : unit.VehiclePursuitLead ? "CitizenVehicleLead"
                        : unit.VehiclePursuitBlocker
                            ? unit.NpcBlockerFallbackToTrail
                                ? "CitizenVehicleTrailFallback"
                                : "CitizenVehicleInterceptor"
                            : "CitizenVehiclePursuitRoleUnknown";
                    var actors = new List<string>(maximumSupportOfficersPerUnit + 1);
                    if (unit.Driver != null)
                    {
                        actors.Add(FormatNpcDamageActorEvidence(unit.Driver, citizen));
                        if (unit.Driver.Handle == allegedAttackerHandle)
                            allegedAttackerIsBackup = true;
                    }

                    int supportCount = 0;
                    if (unit.Officers != null)
                    {
                        for (int officerIndex = 0;
                            officerIndex < unit.Officers.Count
                                && supportCount < maximumSupportOfficersPerUnit;
                            officerIndex++)
                        {
                            Ped officer = unit.Officers[officerIndex];
                            if (officer == null)
                                continue;
                            supportCount++;
                            actors.Add(FormatNpcDamageActorEvidence(officer, citizen));
                            if (officer.Handle == allegedAttackerHandle)
                                allegedAttackerIsBackup = true;
                        }
                    }

                    units.Add("Unit=" + inspectedUnits
                        + ",Vehicle=" + vehicleHandle
                        + ",VehicleDistance=" + vehicleDistance
                        + "m,Lead=" + unit.VehiclePursuitLead
                        + ",Blocker=" + unit.VehiclePursuitBlocker
                        + ",BlockerTrailFallback=" + unit.NpcBlockerFallbackToTrail
                        + ",NpcVehiclePursuitActive=" + unit.NpcVehiclePursuitActive
                        + ",Target=" + unit.NpcPursuitTargetHandle
                        + ",PursuitVehicle=" + unit.NpcPursuitVehicleHandle
                        + ",OwnerTask=" + role
                        + ",Actors=[" + string.Join("|", actors.ToArray()) + "]");
                }
                catch (Exception ex)
                {
                    units.Add("Unit=" + inspectedUnits + ",SnapshotUnavailable="
                        + ex.GetType().Name);
                }
            }

            string snapshot = "BackupState=" + _state
                + "; BackupUnitsTotal=" + _units.Count
                + "; BackupUnitsInspected=" + inspectedUnits
                + "; BackupSnapshotUnitLimit=" + maximumUnits
                + "; AllegedAttackerIsAssignedBackup=" + allegedAttackerIsBackup
                + "; BackupUnits=[" + string.Join(";", units.ToArray()) + "]";
            return Tuple.Create(allegedAttackerIsBackup, snapshot);
        }

        private static string FormatNpcDamageActorEvidence(Ped actor, Ped citizen)
        {
            try
            {
                if (!actor.Exists())
                    return "Ped=" + actor.Handle + ",Exists=false";

                Vehicle currentVehicle = actor.IsInVehicle() ? actor.CurrentVehicle : null;
                int vehicleHandle = currentVehicle != null && currentVehicle.Exists()
                    ? currentVehicle.Handle : 0;
                string distance = actor.Position.DistanceTo(citizen.Position)
                    .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                return "Ped=" + actor.Handle
                    + ",Exists=true,Distance=" + distance + "m"
                    + ",InVehicle=" + (vehicleHandle != 0)
                    + ",CurrentVehicle=" + vehicleHandle;
            }
            catch (Exception ex)
            {
                return "Ped=" + (actor == null ? 0 : actor.Handle)
                    + ",SnapshotUnavailable=" + ex.GetType().Name;
            }
        }

        private void ResolveNpcVehiclePursuitUnits(
            BackupUnit transportUnit,
            out BackupUnit lead,
            out BackupUnit blocker)
        {
            lead = _units.FirstOrDefault(candidate => IsUsable(candidate)
                && candidate.VehiclePursuitLead);
            if (!IsUsable(lead))
                lead = IsUsable(transportUnit) ? transportUnit
                    : _units.FirstOrDefault(IsUsable);

            BackupUnit selectedLead = lead;
            blocker = _units.FirstOrDefault(candidate => IsUsable(candidate)
                && candidate != selectedLead && candidate.VehiclePursuitBlocker
                && !candidate.NpcBlockerFallbackToTrail);
            if (!IsUsable(lead))
            {
                blocker = null;
                return;
            }

            if (!lead.NpcPursuitRolesInitialized)
            {
                lead.NpcPursuitRolesInitialized = true;
                lead.VehiclePursuitLead = true;
                lead.VehiclePursuitBlocker = false;
            }
            if (IsUsable(blocker))
            {
                blocker.NpcPursuitRolesInitialized = true;
                blocker.VehiclePursuitBlocker = true;
                blocker.VehiclePursuitLead = false;
            }
        }

        private static BackupUnit ResolveNpcContainmentUnit(
            BackupUnit lead,
            BackupUnit blocker,
            Ped subject)
        {
            if (!IsUsable(lead))
                return IsUsable(blocker) ? blocker : null;
            if (!IsUsable(blocker) || subject == null || !subject.Exists())
                return lead;
            return blocker.Vehicle.Position.DistanceTo(subject.Position)
                < lead.Vehicle.Position.DistanceTo(subject.Position)
                ? blocker : lead;
        }

        private void HoldNpcPursuitVehicle(BackupUnit unit)
        {
            if (!IsUsable(unit))
                return;
            bool firstHold = !unit.VehicleContainmentStopRequested;
            unit.VehicleContainmentStopRequested = true;
            if (firstHold)
            {
                SetEmergencySignals(unit.Vehicle, false);
                try { Function.Call(Hash.SET_VEHICLE_BRAKE, unit.Vehicle, true); }
                catch (Exception ex)
                {
                    LogException("POLICE_BACKUP_NPC_CONTAINMENT_BRAKE_FAILED", ex);
                }
            }
            if (unit.NpcPursuitDriverTaskCleared
                || Math.Abs(unit.Vehicle.Speed) > 0.65f)
                return;
            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                unit.Driver.Task.ClearAll();
                unit.NpcPursuitDriverTaskCleared = true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_CONTAINMENT_VEHICLE_HOLD_FAILED", ex);
            }
        }

        private void ReleaseNpcPursuitVehicleHold(BackupUnit unit)
        {
            if (!IsUsable(unit) || !unit.VehicleContainmentStopRequested)
                return;
            unit.VehicleContainmentStopRequested = false;
            unit.Arrived = false;
            unit.PursuitBlockerHolding = false;
            unit.NpcPursuitDriverTaskCleared = false;
            if (unit.NpcVehiclePursuitActive)
            {
                // HoldNpcPursuitVehicle clears the driver task. Invalidate the
                // cached chase/recovery ownership so a moving target resumes
                // the same bounded pursuit instead of leaving this unit
                // stopped with a stale "task issued" flag.
                unit.NpcVehicleChaseTaskIssued = false;
                unit.NpcLeadRoadRecoveryActive = false;
                unit.Destination = Vector3.Zero;
                unit.LastDriveTaskAt = DateTime.MinValue;
                unit.LastProgressAt = DateTime.UtcNow;
            }
            SetEmergencySignals(unit.Vehicle, true);
            try
            {
                Function.Call(Hash.SET_VEHICLE_BRAKE, unit.Vehicle, false);
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false);
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_CONTAINMENT_VEHICLE_RELEASE_FAILED", ex);
            }
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
                // Keep PoliceCore's Citizen/Backup slot until the physical
                // custody vehicle has actually left the scene.
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
            if (npcResponse.IsPlayerCustodyActionActive)
            {
                // Citizen Interaction owns the same subject's physical arrest.
                // Hold the assigned Backup state and leave the officer's
                // existing cover task untouched until the cuff is confirmed.
                SetState(LSPDBackupAssignmentState.Securing);
                return;
            }
            if (_npcEscortLastProgressAt == DateTime.MinValue)
            {
                _npcEscortLastPosition = _npcEscortOfficer.Position;
                _npcEscortLastProgressAt = DateTime.UtcNow;
            }

            Vehicle subjectVehicle = ResolvePursuitVehicle(
                subject, npcResponse.ActivePursuitVehicle);
            // A fleeing foot subject can move a long way while the Backup
            // officer is leaving the car. Keep the existing unit on a fresh,
            // validated road interception until it is close enough for the
            // officer to exit and contain the subject. This prevents the old
            // behavior where the officer left at a stale point and chased on
            // foot while the citizen continued to run away.
            if (_npcTargetWasFleeing && !_npcCustodyTransportRequested
                && subjectVehicle == null
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
                    unit, subject, subjectVehicle, DateTime.UtcNow))
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
                        // Dispatch's foot pursuit tracks the live Ped at its
                        // established 4 m/s task speed. Use that same target
                        // task for resistance and flight so a moving subject
                        // cannot outrun a stale 1.2 m/s navigation point.
                        Function.Call(Hash.TASK_GO_TO_ENTITY,
                            _npcEscortOfficer, subject, -1, 1.5f,
                            DispatchFootPursuitTaskSpeed, 1073741824, 0);
                        _nextNpcOfficerTaskAt = DateTime.UtcNow.AddSeconds(3);
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
                    && !npcResponse.IsBackupCustodyActionActive
                    && !npcResponse.IsPlayerCustodyActionActive
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
                            unit.Vehicle,
                            _npcEscortOfficer.Handle == unit.Driver.Handle
                                ? VehicleSeat.Driver : VehicleSeat.RightFront,
                            10000, 1.0f);
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
            _npcPlayerSessionReleased = false;
            _npcDepartureStallNotified = false;
            unit.Arrived = false;
            unit.RouteRecoveryCount = 0;
            unit.LastProgressPosition = unit.Vehicle.Position;
            unit.LastProgressAt = DateTime.UtcNow;
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
                + "; PlayerSessionReleased=false"
                + "; MinimumDepartureDistance="
                + NpcBackgroundDepartureMinimumDistance.ToString("0.0")
                + "; CitizenLoaded=" + npcResponse.IsLoadedInBackupVehicle);
        }

        private void ProcessNpcBackgroundDeparture(
            LSPDNPCResponse npcResponse,
            Ped player,
            DateTime now)
        {
            BackupUnit unit = _npcTransportUnit;
            if (!IsUsable(unit))
            {
                if (!_npcDepartureStallNotified)
                {
                    _npcDepartureStallNotified = true;
                    LogRuntime("POLICE_BACKUP_NPC_DEPARTURE_NOT_CONFIRMED",
                        "Reason=TransportUnitUnavailable; PlayerSessionReleased=false");
                    Notify("Backup's transport vehicle is unavailable. The Citizen handoff is not complete, so patrol remains paused.");
                }
                return;
            }

            if (!npcResponse.IsLoadedInBackupVehicle)
            {
                if (!_npcDepartureStallNotified)
                {
                    _npcDepartureStallNotified = true;
                    try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true); }
                    catch { }
                    LogRuntime("POLICE_BACKUP_NPC_DEPARTURE_NOT_CONFIRMED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; Reason=CitizenSeatNotConfirmed; PlayerSessionReleased=false");
                    Notify("The Citizen is no longer confirmed in Backup's vehicle. Patrol remains paused until the custody handoff is restored.");
                }
                return;
            }

            bool driverInVehicle = unit.Driver != null
                && unit.Driver.Exists() && !unit.Driver.IsDead
                && unit.Driver.CurrentVehicle != null
                && unit.Driver.CurrentVehicle.Exists()
                && unit.Driver.CurrentVehicle.Handle == unit.Vehicle.Handle;
            Vector3 vehiclePosition = unit.Vehicle.Position;
            float distanceFromScene = _npcBackgroundDepartureOrigin == Vector3.Zero
                ? 0f : unit.Vehicle.Position.DistanceTo(_npcBackgroundDepartureOrigin);
            if (driverInVehicle
                && distanceFromScene >= NpcBackgroundDepartureMinimumDistance)
            {
                _npcPlayerSessionReleased =
                    npcResponse.ReleasePlayerSessionForBackgroundTransport(
                        "BackupVehiclePhysicallyDepartedScene");
                if (!_npcPlayerSessionReleased)
                {
                    LogRuntime("POLICE_BACKUP_NPC_DEPARTURE_NOT_CONFIRMED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; CitizenLoaded=true; DriverInVehicle=true"
                        + "; DistanceFromScene=" + distanceFromScene.ToString("0.0")
                        + "; Reason=PlayerSessionReleaseRejected");
                    return;
                }

                string reason = "Backup vehicle physically departed with the same Citizen loaded. DistanceFromScene="
                    + distanceFromScene.ToString("0.0") + "m.";
                LogRuntime("POLICE_BACKUP_NPC_PHYSICAL_DEPARTURE_CONFIRMED",
                    "Ped=" + npcResponse.ActiveSubject.Handle
                    + "; Vehicle=" + unit.Vehicle.Handle
                    + "; Driver=" + unit.Driver.Handle
                    + "; CitizenLoaded=true; DriverInVehicle=true"
                    + "; DistanceFromScene=" + distanceFromScene.ToString("0.0")
                    + "; MinimumDistance="
                    + NpcBackgroundDepartureMinimumDistance.ToString("0.0")
                    + "; PlayerSessionReleased=true");
                npcResponse.CompleteBackupBackgroundDeparture(reason);
                ReleaseNpcCustodyCollisionProtection(unit);
                _npcDeliveryCompletedAt = now;
                BeginNpcBackgroundCleanup(reason);
                return;
            }

            float movementSinceProgress = vehiclePosition.DistanceTo(unit.LastProgressPosition);
            if (movementSinceProgress >= 4f)
            {
                unit.LastProgressPosition = vehiclePosition;
                unit.LastProgressAt = now;
                unit.RouteRecoveryCount = 0;
                _npcBackgroundDepartureDeadline = now.AddSeconds(
                    NpcBackgroundDepartureSeconds);
            }

            if (_npcBackgroundDepartureDeadline != DateTime.MinValue
                && now >= _npcBackgroundDepartureDeadline
                && driverInVehicle && !_npcDepartureStallNotified)
            {
                _npcBackgroundDepartureDeadline = now.AddSeconds(
                    NpcBackgroundDepartureSeconds);
                int retargetAttempt = _npcRouteRetargetCount + 1;
                bool retargetIssued = TryRetargetNpcTransportRoute(
                    unit, _npcBackgroundDepartureDestination, now);
                if (retargetIssued)
                {
                    LogRuntime("POLICE_BACKUP_NPC_DEPARTURE_ROUTE_RECOVERED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; CitizenLoaded=true; DriverInVehicle=true"
                        + "; DistanceFromScene=" + distanceFromScene.ToString("0.0")
                        + "; Attempt=" + _npcRouteRetargetCount
                        + "; Waypoint=" + unit.Destination
                        + "; PlayerSessionReleased=false");
                    Notify("Backup's transport route was blocked. The unit is trying another road path; patrol remains paused until the loaded Citizen leaves.");
                }
                else if (_npcRouteRetargetCount < retargetAttempt
                    && _npcRouteRetargetCount < MaximumNpcRouteRetargets)
                {
                    // Count a failed candidate search as a bounded attempt too;
                    // otherwise a missing road node would be queried forever.
                    _npcRouteRetargetCount++;
                    LogRuntime("POLICE_BACKUP_NPC_DEPARTURE_ROUTE_RECOVERY_FAILED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; CitizenLoaded=true; DriverInVehicle=true"
                        + "; DistanceFromScene=" + distanceFromScene.ToString("0.0")
                        + "; Attempt=" + _npcRouteRetargetCount
                        + "; PlayerSessionReleased=false");
                }
                else
                {
                    _npcDepartureStallNotified = true;
                    LogRuntime("POLICE_BACKUP_NPC_DEPARTURE_STALLED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; CitizenLoaded=true; DriverInVehicle=true"
                        + "; DistanceFromScene=" + distanceFromScene.ToString("0.0")
                        + "; RouteRetargetAttempts=" + _npcRouteRetargetCount
                        + "; PlayerSessionReleased=false");
                    Notify("Backup still has the Citizen but its vehicle has not physically left the scene. Patrol remains paused.");
                }
            }

            // Keep the player activity occupied until position evidence shows
            // the loaded Backup vehicle has actually departed the scene.
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

        private bool MaintainDispatchFootPursuitApproach(
            LSPDDispatch dispatch,
            Ped player,
            DateTime now)
        {
            MaintainDispatchGroupSceneSupport(dispatch, now);
            List<Ped> targets = _dispatchFootPursuitTargetCache;
            if (targets.Count == 0)
                return false;

            List<BackupUnit> usableUnits = _units
                .Where(unit => IsUsable(unit) && !unit.DispatchGroupSceneSupport
                    && (_dispatchVehiclePursuitTargetCache.Count == 0
                        || unit.DispatchVehiclePursuitVehicleHandle == 0))
                .ToList();
            if (usableUnits.Count == 0)
                return false;

            var targetCounts = targets.ToDictionary(target => target.Handle, target => 0);
            foreach (BackupUnit unit in usableUnits)
            {
                if (unit.Arrived && unit.InterceptionExitRequested)
                    continue;
                if (targetCounts.ContainsKey(unit.DispatchFootPursuitSuspectHandle))
                    targetCounts[unit.DispatchFootPursuitSuspectHandle]++;
            }

            foreach (BackupUnit unit in usableUnits)
            {
                if (unit.Arrived && unit.InterceptionExitRequested)
                    continue;
                if (targetCounts.ContainsKey(unit.DispatchFootPursuitSuspectHandle))
                    continue;

                Ped assignedTarget = targets
                    .OrderBy(target => targetCounts[target.Handle])
                    .ThenBy(target => unit.Vehicle.Position.DistanceTo(target.Position))
                    .FirstOrDefault();
                if (assignedTarget == null)
                    continue;

                unit.DispatchFootPursuitSuspectHandle = assignedTarget.Handle;
                targetCounts[assignedTarget.Handle]++;
                unit.NextDispatchFootPursuitRouteAt = DateTime.MinValue;
                unit.Destination = Vector3.Zero;
                unit.LastDriveTaskAt = DateTime.MinValue;
                unit.NextPursuitProgressCheckAt = now;
                unit.RouteRecoveryCount = 0;
                unit.PursuitRouteRecoveryExhausted = false;
                unit.PursuitRouteFailureLogged = false;
                unit.LastProgressAt = now;
                unit.LastPursuitTargetDistance = unit.Vehicle.Position.DistanceTo(
                    assignedTarget.Position);
                unit.LastPursuitGoalDistance = unit.Destination == Vector3.Zero
                    ? float.MaxValue
                    : unit.Vehicle.Position.DistanceTo(unit.Destination);
                unit.Arrived = false;
                unit.InterceptionAreaReported = false;
                LogRuntime("POLICE_BACKUP_FOOT_PURSUIT_TARGET_ASSIGNED",
                    "Suspect=" + assignedTarget.Handle
                    + "; BackupVehicle=" + unit.Vehicle.Handle
                    + "; ActiveRunners=" + targets.Count
                    + "; Owner=Dispatch");
            }

            foreach (BackupUnit unit in usableUnits)
            {
                if (unit.Arrived && unit.InterceptionExitRequested)
                    continue;
                Ped target = targets.FirstOrDefault(value => value.Handle
                    == unit.DispatchFootPursuitSuspectHandle);
                if (target == null || !target.Exists() || target.IsDead
                    || unit.PursuitRouteRecoveryExhausted
                    || now < unit.NextDispatchFootPursuitRouteAt)
                    continue;

                unit.NextDispatchFootPursuitRouteAt = now.AddSeconds(3);
                Vector3 approach = ResolveInterceptionRoadPosition(target, null);
                Vector3 roadNode;
                float roadHeading;
                if (approach == Vector3.Zero
                    || !TryGetNearbyVehicleRoadNode(
                        approach, target.Position, 22f,
                        out roadNode, out roadHeading))
                {
                    if (!TryGetNearbyVehicleRoadNode(
                        target.Position, target.Position, 35f,
                        out roadNode, out roadHeading))
                    {
                        if (now >= unit.LastProgressAt.AddSeconds(RouteStallTimeoutSeconds))
                        {
                            unit.LastProgressAt = now;
                            unit.RouteRecoveryCount++;
                            if (unit.RouteRecoveryCount >= MaximumRouteRecoveries)
                            {
                                unit.PursuitRouteRecoveryExhausted = true;
                                if (!unit.PursuitRouteFailureLogged)
                                {
                                    unit.PursuitRouteFailureLogged = true;
                                    LogRuntime("POLICE_BACKUP_FOOT_ROUTE_RECOVERY_EXHAUSTED",
                                        "Suspect=" + target.Handle
                                        + "; BackupVehicle=" + unit.Vehicle.Handle
                                        + "; RecoveryAttempts=" + unit.RouteRecoveryCount
                                        + "; Reason=NO_VALID_NEARBY_VEHICLE_ROAD_NODE");
                                }
                            }
                        }
                        continue;
                    }
                }

                float targetDistance = unit.Vehicle.Position.DistanceTo(target.Position);
                float roadDistance = unit.Vehicle.Position.DistanceTo(roadNode);
                if (unit.Arrived && !unit.InterceptionExitRequested
                    && targetDistance > 45f)
                {
                    unit.Arrived = false;
                    SetEmergencySignals(unit.Vehicle, true);
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_FOOT_APPROACH_RELEASE_FAILED", ex);
                    }
                }
                if (targetDistance <= 45f
                    || roadDistance <= Math.Max(8f, _settings.ArrivalRadius))
                {
                    if (!unit.Arrived)
                    {
                        unit.Arrived = true;
                        unit.Destination = roadNode;
                        unit.InterceptionExitRequested = false;
                        unit.NextInterceptionExitTaskAt = DateTime.MinValue;
                        unit.InterceptionAreaReported = true;
                        SetEmergencySignals(unit.Vehicle, false);
                        try
                        {
                            Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                            unit.Driver.Task.ClearAll();
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_FOOT_APPROACH_HOLD_FAILED", ex);
                        }
                        LogRuntime("POLICE_BACKUP_FOOT_APPROACH_REACHED",
                            "Suspect=" + target.Handle
                            + "; BackupVehicle=" + unit.Vehicle.Handle
                            + "; TargetDistance=" + targetDistance.ToString("0.0")
                            + "; RoadNode=" + roadNode
                            + "; OfficersExit=true");
                    }
                    continue;
                }

                if (now >= unit.NextPursuitProgressCheckAt)
                {
                    unit.NextPursuitProgressCheckAt = now.AddSeconds(3);
                    float goalDistance = unit.Vehicle.Position.DistanceTo(roadNode);
                    bool meaningfulProgress =
                        (unit.LastPursuitGoalDistance < float.MaxValue
                            && goalDistance <= unit.LastPursuitGoalDistance - 4f)
                        || (unit.LastPursuitTargetDistance < float.MaxValue
                            && targetDistance <= unit.LastPursuitTargetDistance - 5f);
                    unit.LastPursuitTargetDistance = targetDistance;
                    unit.LastPursuitGoalDistance = goalDistance;
                    if (meaningfulProgress)
                    {
                        unit.LastProgressAt = now;
                        unit.RouteRecoveryCount = 0;
                        unit.PursuitRouteRecoveryExhausted = false;
                        unit.PursuitRouteFailureLogged = false;
                    }
                    else if (now >= unit.LastProgressAt.AddSeconds(
                        RouteStallTimeoutSeconds))
                    {
                        if (unit.RouteRecoveryCount >= MaximumRouteRecoveries)
                        {
                            unit.PursuitRouteRecoveryExhausted = true;
                            if (!unit.PursuitRouteFailureLogged)
                            {
                                unit.PursuitRouteFailureLogged = true;
                                LogRuntime("POLICE_BACKUP_FOOT_ROUTE_RECOVERY_EXHAUSTED",
                                    "Suspect=" + target.Handle
                                    + "; BackupVehicle=" + unit.Vehicle.Handle
                                    + "; RecoveryAttempts=" + unit.RouteRecoveryCount
                                    + "; RemainingFootUnitsContinue="
                                    + usableUnits.Any(other => other != unit
                                        && IsUsable(other)
                                        && !other.PursuitRouteRecoveryExhausted));
                                SetEmergencySignals(unit.Vehicle, false);
                                try
                                {
                                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE,
                                        unit.Vehicle, true);
                                    unit.Driver.Task.ClearAll();
                                }
                                catch (Exception ex)
                                {
                                    LogException(
                                        "POLICE_BACKUP_FOOT_ROUTE_HOLD_FAILED", ex);
                                }
                            }
                            continue;
                        }

                        unit.RouteRecoveryCount++;
                        unit.LastProgressAt = now;
                        unit.LastDriveTaskAt = DateTime.MinValue;
                        unit.Destination = roadNode;
                        if (!SendTo(unit, roadNode, true))
                            unit.LastDriveTaskAt = now;
                        LogRuntime("POLICE_BACKUP_FOOT_ROUTE_RECOVERY",
                            "Recovery=" + unit.RouteRecoveryCount
                            + "; Suspect=" + target.Handle
                            + "; BackupVehicle=" + unit.Vehicle.Handle
                            + "; RoadNode=" + roadNode
                            + "; RemainingFootUnitsContinue="
                            + usableUnits.Any(other => other != unit
                                && IsUsable(other)
                                && !other.PursuitRouteRecoveryExhausted));
                        continue;
                    }
                }

                bool routeChanged = unit.Destination == Vector3.Zero
                    || unit.Destination.DistanceTo(roadNode) >= 18f;
                bool reachedPreviousGoal = unit.Destination != Vector3.Zero
                    && unit.Vehicle.Position.DistanceTo(unit.Destination) <= 12f;
                if (routeChanged || reachedPreviousGoal)
                {
                    unit.Destination = roadNode;
                    if (!SendTo(unit, roadNode, true))
                        unit.LastDriveTaskAt = now;
                }
            }

            bool hasArrivedUnit = usableUnits.Any(unit => IsUsable(unit) && unit.Arrived);
            if (hasArrivedUnit && now >= _nextDispatchFootSupportAt)
            {
                _nextDispatchFootSupportAt = now.AddSeconds(1);
                SupportInterception(dispatch, player);
            }
            else if (!hasArrivedUnit)
                SetState(LSPDBackupAssignmentState.Intercepting);
            return true;
        }

        private bool MaintainDispatchVehicleInterceptions(
            LSPDDispatch dispatch,
            IReadOnlyList<Ped> pursuitTargets,
            DateTime now)
        {
            if (pursuitTargets == null || pursuitTargets.Count == 0)
                return false;

            var targets = new List<KeyValuePair<Ped, Vehicle>>();
            var seenVehicleHandles = new HashSet<int>();
            foreach (Ped suspect in pursuitTargets)
            {
                Vehicle suspectVehicle = ResolvePursuitVehicle(_incident, suspect);
                if (suspect == null || !suspect.Exists() || suspect.IsDead
                    || suspectVehicle == null || !suspectVehicle.Exists()
                    || !seenVehicleHandles.Add(suspectVehicle.Handle))
                    continue;
                targets.Add(new KeyValuePair<Ped, Vehicle>(suspect, suspectVehicle));
                LogDispatchInterceptionTracking(suspect, suspectVehicle, now);
            }
            bool sceneSupportActive = MaintainDispatchGroupSceneSupport(
                dispatch, now);
            if (targets.Count == 0)
            {
                bool remainingFootSupport = _dispatchFootPursuitTargetCache.Count > 0
                    && MaintainDispatchFootPursuitApproach(
                        dispatch, Game.Player.Character, now);
                return sceneSupportActive || remainingFootSupport;
            }

            HashSet<int> activeFootTargetHandles = new HashSet<int>(
                _dispatchFootPursuitTargetCache
                    .Where(ped => ped != null && ped.Exists())
                    .Select(ped => ped.Handle));
            foreach (BackupUnit unit in _units.Where(value => value != null
                && value.DispatchFootPursuitSuspectHandle > 0
                && !activeFootTargetHandles.Contains(
                    value.DispatchFootPursuitSuspectHandle)))
                unit.DispatchFootPursuitSuspectHandle = 0;

            List<BackupUnit> usableUnits = _units
                .Where(unit => IsUsable(unit) && !unit.DispatchGroupSceneSupport
                    && unit.DispatchFootPursuitSuspectHandle == 0)
                .ToList();
            if (usableUnits.Count == 0)
                return sceneSupportActive;

            var groups = targets.ToDictionary(
                target => target.Key.Handle,
                target => new List<BackupUnit>());
            var unassigned = new List<BackupUnit>();
            foreach (BackupUnit unit in usableUnits)
            {
                List<BackupUnit> existingGroup;
                if (groups.TryGetValue(
                    unit.DispatchVehiclePursuitSuspectHandle, out existingGroup)
                    && targets.Any(target => target.Key.Handle
                        == unit.DispatchVehiclePursuitSuspectHandle
                        && target.Value.Handle
                            == unit.DispatchVehiclePursuitVehicleHandle))
                    existingGroup.Add(unit);
                else
                    unassigned.Add(unit);
            }

            if (targets.Count == 1)
                groups[targets[0].Key.Handle].AddRange(unassigned);
            else
            {
                foreach (BackupUnit unit in unassigned)
                {
                    List<BackupUnit> smallestGroup = groups.Values
                        .OrderBy(group => group.Count)
                        .First();
                    smallestGroup.Add(unit);
                }

                foreach (KeyValuePair<Ped, Vehicle> target in targets)
                {
                    List<BackupUnit> group = groups[target.Key.Handle];
                    if (group.Count > 0)
                        continue;
                    List<BackupUnit> donor = groups.Values
                        .Where(candidate => candidate.Count > 1)
                        .OrderByDescending(candidate => candidate.Count)
                        .FirstOrDefault();
                    if (donor != null)
                    {
                        BackupUnit reassigned = donor[donor.Count - 1];
                        donor.RemoveAt(donor.Count - 1);
                        group.Add(reassigned);
                    }
                }
            }

            bool hasAssignment = false;
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                KeyValuePair<Ped, Vehicle> target = targets[targetIndex];
                List<BackupUnit> group = groups[target.Key.Handle];
                for (int unitIndex = 0; unitIndex < group.Count; unitIndex++)
                {
                    BackupUnit unit = group[unitIndex];
                    bool targetChanged = unit.DispatchVehiclePursuitSuspectHandle
                            != target.Key.Handle
                        || unit.DispatchVehiclePursuitVehicleHandle
                            != target.Value.Handle;
                    bool blockerRole = group.Count > 1 && unitIndex == 0;
                    bool leadRole = group.Count == 1 || unitIndex == 1;
                    bool roleChanged = unit.VehiclePursuitBlocker != blockerRole
                        || unit.VehiclePursuitLead != leadRole;
                    if (targetChanged)
                    {
                        unit.DispatchVehiclePursuitSuspectHandle = target.Key.Handle;
                        unit.DispatchVehiclePursuitVehicleHandle = target.Value.Handle;
                        unit.DispatchVehiclePursuitChaseTaskIssued = false;
                        unit.DispatchVehicleStoppedAt = DateTime.MinValue;
                        unit.NextDispatchVehicleExitTaskAt = DateTime.MinValue;
                        unit.NextPursuitProgressCheckAt = now;
                        unit.DispatchVehicleExitRequested = false;
                        unit.PursuitRouteRecoveryExhausted = false;
                        unit.PursuitRouteFailureLogged = false;
                        unit.PursuitBlockerHolding = false;
                        unit.RouteRecoveryCount = 0;
                        unit.InterceptionExitRequested = false;
                        unit.NextInterceptionExitTaskAt = DateTime.MinValue;
                        unit.InterceptionAreaReported = false;
                        unit.VehicleContainmentStopRequested = false;
                        unit.Arrived = false;
                        unit.LastProgressAt = now;
                        unit.LastPursuitTargetDistance = unit.Vehicle.Position
                            .DistanceTo(target.Value.Position);
                        unit.Destination = Vector3.Zero;
                        unit.LastDriveTaskAt = DateTime.MinValue;
                    }
                    unit.VehiclePursuitBlocker = blockerRole;
                    unit.VehiclePursuitLead = leadRole;
                    if (targetChanged || roleChanged)
                    {
                        Vector3 targetRoad;
                        float targetRoadHeading;
                        if (TryResolveDispatchVehiclePursuitRoadNode(
                            target.Key,
                            target.Value,
                            unit.VehiclePursuitBlocker,
                            out targetRoad,
                            out targetRoadHeading))
                        {
                            unit.Destination = targetRoad;
                            SendTo(unit, targetRoad, true);
                        }
                        else
                            unit.LastDriveTaskAt = now;
                        unit.LastPursuitGoalDistance = unit.Destination == Vector3.Zero
                            ? float.MaxValue
                            : unit.Vehicle.Position.DistanceTo(unit.Destination);
                    }
                }

                if (group.Count == 0)
                    continue;
                hasAssignment = MaintainDispatchVehicleInterception(
                    target.Key, target.Value, now, group)
                    || hasAssignment;
            }

            bool footSupportActive = _dispatchFootPursuitTargetCache.Count > 0
                && MaintainDispatchFootPursuitApproach(
                    dispatch, Game.Player.Character, now);
            return hasAssignment || sceneSupportActive || footSupportActive;
        }

        private bool MaintainDispatchVehicleInterception(
            Ped suspect,
            Vehicle suspectVehicle,
            DateTime now,
            IList<BackupUnit> assignedUnits)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspectVehicle == null || !suspectVehicle.Exists())
                return false;

            bool hasUsableUnit = false;
            foreach (BackupUnit candidate in assignedUnits)
                if (IsUsable(candidate))
                {
                    hasUsableUnit = true;
                    break;
                }
            if (!hasUsableUnit)
                return false;

            foreach (BackupUnit unit in assignedUnits)
            {
                if (!IsUsable(unit))
                    continue;
                if (unit.DispatchVehiclePursuitSuspectHandle == suspect.Handle
                    && unit.DispatchVehiclePursuitVehicleHandle == suspectVehicle.Handle)
                    continue;
                unit.DispatchVehiclePursuitSuspectHandle = suspect.Handle;
                unit.DispatchVehiclePursuitVehicleHandle = suspectVehicle.Handle;
                unit.DispatchVehiclePursuitChaseTaskIssued = false;
                unit.DispatchVehicleStoppedAt = DateTime.MinValue;
                unit.NextDispatchVehicleExitTaskAt = DateTime.MinValue;
                unit.NextPursuitProgressCheckAt = now;
                unit.DispatchVehicleExitRequested = false;
                unit.PursuitRouteRecoveryExhausted = false;
                unit.PursuitRouteFailureLogged = false;
                unit.PursuitBlockerHolding = false;
                unit.RouteRecoveryCount = 0;
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.LastProgressAt = now;
                unit.InterceptionExitRequested = false;
                unit.NextInterceptionExitTaskAt = DateTime.MinValue;
                unit.InterceptionAreaReported = false;
                unit.VehicleContainmentStopRequested = false;
                unit.Destination = Vector3.Zero;
                unit.LastDriveTaskAt = DateTime.MinValue;
            }

            BackupUnit leadUnit = null;
            BackupUnit firstUsableUnit = null;
            BackupUnit firstPursuitUnit = null;
            int usableUnitCount = 0;
            bool hasForwardBlocker = false;
            foreach (BackupUnit unit in assignedUnits)
            {
                if (!IsUsable(unit))
                    continue;
                usableUnitCount++;
                if (firstUsableUnit == null)
                    firstUsableUnit = unit;
                if (!unit.VehiclePursuitBlocker && firstPursuitUnit == null)
                    firstPursuitUnit = unit;
                if (unit.VehiclePursuitBlocker)
                    hasForwardBlocker = true;
                if (unit.VehiclePursuitLead && !unit.VehiclePursuitBlocker)
                {
                    leadUnit = unit;
                }
            }
            if (!hasForwardBlocker && usableUnitCount > 1
                && firstUsableUnit != null)
            {
                firstUsableUnit.VehiclePursuitBlocker = true;
                firstPursuitUnit = null;
                foreach (BackupUnit unit in assignedUnits)
                    if (IsUsable(unit) && !unit.VehiclePursuitBlocker)
                    {
                        firstPursuitUnit = unit;
                        break;
                    }
                leadUnit = firstPursuitUnit;
            }
            if (!IsUsable(leadUnit))
                leadUnit = firstPursuitUnit;
            if (!IsUsable(leadUnit))
                foreach (BackupUnit unit in assignedUnits)
                    if (IsUsable(unit))
                    {
                        leadUnit = unit;
                        break;
                    }
            if (!IsUsable(leadUnit))
                return false;
            foreach (BackupUnit unit in assignedUnits)
                if (IsUsable(unit))
                    unit.VehiclePursuitLead = unit == leadUnit;

            float targetSpeed = Math.Abs(suspectVehicle.Speed);
            float leadDistance = leadUnit.Vehicle.Position.DistanceTo(suspectVehicle.Position);
            bool targetStopped = targetSpeed <= DispatchVehicleStoppedSpeed;
            if (leadUnit.DispatchVehicleExitRequested && suspect.IsInVehicle()
                && (!targetStopped || leadDistance > DispatchVehicleCloseDistance))
            {
                // A suspect who drives away during the exit command has resumed
                // the pursuit branch. Release the temporary vehicle containment
                // and issue fresh pursuit tasks; do not keep ordering the suspect
                // out while the car is moving.
                leadUnit.DispatchVehicleExitRequested = false;
                leadUnit.DispatchVehicleStoppedAt = DateTime.MinValue;
                leadUnit.NextDispatchVehicleExitTaskAt = DateTime.MinValue;
                foreach (BackupUnit unit in assignedUnits)
                {
                    if (!IsUsable(unit))
                        continue;
                    unit.DispatchVehicleExitRequested = false;
                    unit.DispatchVehiclePursuitChaseTaskIssued = false;
                    unit.DispatchVehicleStoppedAt = DateTime.MinValue;
                    unit.NextDispatchVehicleExitTaskAt = DateTime.MinValue;
                    if (!unit.VehicleContainmentStopRequested)
                        continue;
                    unit.VehicleContainmentStopRequested = false;
                    unit.Arrived = false;
                    SetEmergencySignals(unit.Vehicle, true);
                    try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false); }
                    catch { }
                }
            }

            if (leadUnit.DispatchVehicleExitRequested || !suspect.IsInVehicle())
            {
                if (suspect.IsInVehicle())
                {
                    if (now >= leadUnit.NextDispatchVehicleExitTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_LEAVE_VEHICLE,
                                suspect, suspectVehicle, 0);
                            leadUnit.NextDispatchVehicleExitTaskAt = now.AddSeconds(3);
                            LogRuntime("POLICE_BACKUP_SUSPECT_VEHICLE_EXIT_RETRY",
                                "Ped=" + suspect.Handle + "; Vehicle=" + suspectVehicle.Handle);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_SUSPECT_VEHICLE_EXIT_FAILED", ex);
                            leadUnit.NextDispatchVehicleExitTaskAt = now.AddSeconds(3);
                        }
                    }
                    foreach (BackupUnit unit in assignedUnits)
                    {
                        if (!IsUsable(unit)
                            || unit.Vehicle.Position.DistanceTo(suspectVehicle.Position)
                                > DispatchVehicleCloseDistance)
                            continue;
                        unit.Arrived = true;
                        unit.VehicleContainmentStopRequested = true;
                        SetEmergencySignals(unit.Vehicle, false);
                        try
                        {
                            Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                            unit.Driver.Task.ClearAll();
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_VEHICLE_CONTAINMENT_STOP_FAILED", ex);
                        }
                    }
                    SetState(LSPDBackupAssignmentState.Intercepting);
                    return true;
                }

                foreach (BackupUnit unit in assignedUnits)
                {
                    if (!IsUsable(unit))
                        continue;
                    unit.InterceptionExitRequested = unit == leadUnit;
                    unit.Arrived = true;
                    unit.Destination = unit.Vehicle.Position;
                    unit.NextInterceptionExitTaskAt = DateTime.MinValue;
                    unit.DispatchVehicleExitRequested = false;
                    unit.DispatchVehiclePursuitChaseTaskIssued = false;
                    unit.DispatchVehicleStoppedAt = DateTime.MinValue;
                    SetEmergencySignals(unit.Vehicle, false);
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                        unit.Driver.Task.ClearAll();
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_INTERCEPTION_VEHICLE_HOLD_FAILED", ex);
                    }
                }
                LogRuntime("POLICE_BACKUP_SUSPECT_LEFT_VEHICLE",
                    "Ped=" + suspect.Handle + "; FormerVehicle=" + suspectVehicle.Handle
                    + "; BackupVehicle=" + leadUnit.Vehicle.Handle);
                return false;
            }

            if (targetStopped)
            {
                if (leadUnit.DispatchVehicleStoppedAt == DateTime.MinValue)
                    leadUnit.DispatchVehicleStoppedAt = now;
            }
            else
                leadUnit.DispatchVehicleStoppedAt = DateTime.MinValue;

            if ((!targetStopped || leadDistance > DispatchVehicleCloseDistance)
                && leadUnit.VehicleContainmentStopRequested)
            {
                leadUnit.VehicleContainmentStopRequested = false;
                SetEmergencySignals(leadUnit.Vehicle, true);
                try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, leadUnit.Vehicle, false); } catch { }
            }

            if (targetStopped && leadDistance <= DispatchVehicleCloseDistance)
            {
                foreach (BackupUnit unit in assignedUnits)
                {
                    if (!IsUsable(unit)
                        || unit.Vehicle.Position.DistanceTo(suspectVehicle.Position)
                            > DispatchVehicleCloseDistance)
                        continue;
                    unit.Arrived = true;
                    unit.VehicleContainmentStopRequested = true;
                    unit.DispatchVehiclePursuitChaseTaskIssued = false;
                    SetEmergencySignals(unit.Vehicle, false);
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                        unit.Driver.Task.ClearAll();
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_VEHICLE_CONTAINMENT_STOP_FAILED", ex);
                    }
                }
                if (now < leadUnit.DispatchVehicleStoppedAt.AddMilliseconds(1200))
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
                if (now >= leadUnit.NextDispatchVehicleExitTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            suspect, suspectVehicle, 0);
                        leadUnit.DispatchVehicleExitRequested = true;
                        leadUnit.NextDispatchVehicleExitTaskAt = now.AddSeconds(3);
                        LogRuntime("POLICE_BACKUP_SUSPECT_EXIT_REQUESTED",
                            "Ped=" + suspect.Handle + "; Vehicle=" + suspectVehicle.Handle
                            + "; BackupOfficer=" + leadUnit.Driver.Handle);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_SUSPECT_VEHICLE_EXIT_FAILED", ex);
                        leadUnit.NextDispatchVehicleExitTaskAt = now.AddSeconds(3);
                    }
                }
                SetState(LSPDBackupAssignmentState.Intercepting);
                return true;
            }

            foreach (BackupUnit unit in assignedUnits)
            {
                if (!IsUsable(unit))
                    continue;
                SetEmergencySignals(unit.Vehicle, !unit.PursuitBlockerHolding);
                if (targetStopped
                    && unit.Vehicle.Position.DistanceTo(suspectVehicle.Position)
                        <= DispatchVehicleCloseDistance)
                {
                    unit.Arrived = true;
                    unit.VehicleContainmentStopRequested = true;
                    SetEmergencySignals(unit.Vehicle, false);
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                        unit.Driver.Task.ClearAll();
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_VEHICLE_CONTAINMENT_STOP_FAILED", ex);
                    }
                    continue;
                }

                if (unit.Arrived && unit.VehicleContainmentStopRequested)
                {
                    unit.Arrived = false;
                    unit.VehicleContainmentStopRequested = false;
                    SetEmergencySignals(unit.Vehicle, true);
                    try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false); }
                    catch { }
                }

                if (TryRecoverDispatchVehiclePursuitRoute(
                    unit, suspect, suspectVehicle, now,
                    assignedUnits))
                    continue;
                if (unit.PursuitRouteRecoveryExhausted)
                    continue;

                if (unit.VehiclePursuitBlocker)
                {
                    if (now >= unit.LastDriveTaskAt.AddMilliseconds(TaskRefreshMilliseconds))
                    {
                        Vector3 blockerDestination;
                        float blockerHeading;
                        if (!TryResolveDispatchVehiclePursuitRoadNode(
                            suspect, suspectVehicle, true,
                            out blockerDestination, out blockerHeading))
                        {
                            unit.LastDriveTaskAt = now;
                            continue;
                        }
                        if (unit.Vehicle.Position.DistanceTo(blockerDestination) <= 10f)
                        {
                            unit.PursuitBlockerHolding = true;
                            unit.Arrived = true;
                            unit.Destination = blockerDestination;
                            unit.LastProgressPosition = unit.Vehicle.Position;
                            unit.LastProgressAt = now;
                            unit.LastDriveTaskAt = now;
                            SetEmergencySignals(unit.Vehicle, false);
                            try
                            {
                                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                                unit.Driver.Task.ClearAll();
                            }
                            catch (Exception ex)
                            {
                                LogException("POLICE_BACKUP_FORWARD_BLOCKER_HOLD_FAILED", ex);
                            }
                        }
                        else
                        {
                            if (unit.PursuitBlockerHolding)
                            {
                                unit.PursuitBlockerHolding = false;
                                unit.Arrived = false;
                                SetEmergencySignals(unit.Vehicle, true);
                                try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false); }
                                catch { }
                            }
                            bool routeChanged = unit.Destination == Vector3.Zero
                                || unit.Destination.DistanceTo(blockerDestination) >= 14f;
                            bool reachedPreviousGoal = unit.Destination != Vector3.Zero
                                && unit.Vehicle.Position.DistanceTo(unit.Destination) <= 12f;
                            if (routeChanged || reachedPreviousGoal)
                            {
                                if (!SendTo(unit, blockerDestination, true))
                                    unit.LastDriveTaskAt = now;
                            }
                            else
                                unit.LastDriveTaskAt = now;
                        }
                    }
                    continue;
                }

                if (unit.DispatchVehiclePursuitChaseTaskIssued
                    && unit.DispatchVehiclePursuitSuspectHandle == suspect.Handle
                    && unit.DispatchVehiclePursuitVehicleHandle == suspectVehicle.Handle)
                    continue;
                if (now < unit.LastDriveTaskAt.AddMilliseconds(TaskRefreshMilliseconds))
                    continue;
                float unitDistance = unit.Vehicle.Position.DistanceTo(suspectVehicle.Position);
                if (unitDistance <= 180f)
                {
                    try
                    {
                        unit.Driver.Task.VehicleChase(suspect);
                        unit.DispatchVehiclePursuitChaseTaskIssued = true;
                        unit.LastDriveTaskAt = now;
                        unit.Arrived = false;
                        LogRuntime("POLICE_BACKUP_VEHICLE_PURSUIT",
                            "Suspect=" + suspect.Handle
                            + "; SuspectVehicle=" + suspectVehicle.Handle
                            + "; BackupVehicle=" + unit.Vehicle.Handle
                            + "; Role=" + (unit == leadUnit ? "LeadPursuit" : "TrailingPursuit")
                            + "; Distance=" + unitDistance.ToString("0.0"));
                    }
                    catch (Exception ex)
                    {
                        unit.DispatchVehiclePursuitChaseTaskIssued = false;
                        LogException("POLICE_BACKUP_VEHICLE_PURSUIT_FAILED", ex);
                        Vector3 recoveryRoad;
                        float recoveryHeading;
                        if (TryResolveDispatchVehiclePursuitRoadNode(
                            suspect, suspectVehicle, false,
                            out recoveryRoad, out recoveryHeading))
                        {
                            if (!SendTo(unit, recoveryRoad, true))
                                unit.LastDriveTaskAt = now;
                        }
                        else
                            unit.LastDriveTaskAt = now;
                    }
                }
                else
                {
                    Vector3 catchUpRoad;
                    float catchUpHeading;
                    if (TryResolveDispatchVehiclePursuitRoadNode(
                        suspect, suspectVehicle, false,
                        out catchUpRoad, out catchUpHeading))
                    {
                        bool routeChanged = unit.Destination == Vector3.Zero
                            || unit.Destination.DistanceTo(catchUpRoad) >= 18f;
                        bool reachedPreviousGoal = unit.Destination != Vector3.Zero
                            && unit.Vehicle.Position.DistanceTo(unit.Destination) <= 12f;
                        if (routeChanged || reachedPreviousGoal)
                        {
                            unit.DispatchVehiclePursuitChaseTaskIssued = false;
                            if (!SendTo(unit, catchUpRoad, true))
                                unit.LastDriveTaskAt = now;
                        }
                        else
                            unit.LastDriveTaskAt = now;
                    }
                    else
                        unit.LastDriveTaskAt = now;
                }
            }

            SetState(LSPDBackupAssignmentState.Intercepting);
            return true;
        }

        private bool TryRecoverDispatchVehiclePursuitRoute(
            BackupUnit unit,
            Ped suspect,
            Vehicle suspectVehicle,
            DateTime now,
            IList<BackupUnit> assignedUnits)
        {
            if (!IsUsable(unit) || suspect == null || !suspect.Exists()
                || suspectVehicle == null || !suspectVehicle.Exists()
                || now < unit.NextPursuitProgressCheckAt)
                return false;

            unit.NextPursuitProgressCheckAt = now.AddSeconds(3);
            Vector3 position = unit.Vehicle.Position;
            if (unit.VehiclePursuitBlocker && unit.PursuitBlockerHolding)
            {
                unit.LastProgressPosition = position;
                unit.LastPursuitTargetDistance = position.DistanceTo(
                    suspectVehicle.Position);
                unit.LastPursuitGoalDistance = unit.Destination == Vector3.Zero
                    ? float.MaxValue : position.DistanceTo(unit.Destination);
                unit.LastProgressAt = now;
                return false;
            }

            float targetDistance = position.DistanceTo(suspectVehicle.Position);
            float goalDistance = unit.Destination == Vector3.Zero
                ? float.MaxValue : position.DistanceTo(unit.Destination);
            bool meaningfulProgress = unit.VehiclePursuitBlocker
                ? unit.LastPursuitGoalDistance < float.MaxValue
                    && goalDistance <= unit.LastPursuitGoalDistance - 4f
                : unit.LastPursuitTargetDistance < float.MaxValue
                    && targetDistance <= unit.LastPursuitTargetDistance - 5f;
            unit.LastProgressPosition = position;
            unit.LastPursuitTargetDistance = targetDistance;
            unit.LastPursuitGoalDistance = goalDistance;
            if (meaningfulProgress)
            {
                unit.LastProgressAt = now;
                unit.RouteRecoveryCount = 0;
                unit.PursuitRouteRecoveryExhausted = false;
                unit.PursuitRouteFailureLogged = false;
                return false;
            }
            if (now < unit.LastProgressAt.AddSeconds(RouteStallTimeoutSeconds))
                return false;

            if (unit.RouteRecoveryCount >= MaximumRouteRecoveries)
            {
                unit.PursuitRouteRecoveryExhausted = true;
                if (!unit.PursuitRouteFailureLogged)
                {
                    unit.PursuitRouteFailureLogged = true;
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                        unit.Driver.Task.ClearAll();
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_PURSUIT_ROUTE_HOLD_FAILED", ex);
                    }
                    bool remainingUnitsContinue = false;
                    foreach (BackupUnit other in assignedUnits)
                        if (other != unit && IsUsable(other)
                            && !other.PursuitRouteRecoveryExhausted)
                        {
                            remainingUnitsContinue = true;
                            break;
                        }
                    LogRuntime("POLICE_BACKUP_PURSUIT_ROUTE_RECOVERY_EXHAUSTED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; Suspect=" + suspect.Handle
                        + "; RecoveryAttempts=" + unit.RouteRecoveryCount
                        + "; RemainingUnitsContinue=" + remainingUnitsContinue);
                    if (remainingUnitsContinue && !_dispatchVehicleRouteFailureNotified)
                    {
                        _dispatchVehicleRouteFailureNotified = true;
                        Notify("~b~POLICE BACKUP~s~\nOne unit could not reach the pursuit route. Remaining units continue.");
                    }
                    SetEmergencySignals(unit.Vehicle, false);
                }
                unit.LastProgressAt = now;
                return true;
            }

            Vector3 recoveryDestination = Vector3.Zero;
            float recoveryHeading;
            TryResolveDispatchVehiclePursuitRoadNode(
                suspect,
                suspectVehicle,
                unit.VehiclePursuitBlocker,
                out recoveryDestination,
                out recoveryHeading);

            unit.LastProgressAt = now;
            if (recoveryDestination != Vector3.Zero
                && SendTo(unit, recoveryDestination, true))
            {
                unit.DispatchVehiclePursuitChaseTaskIssued = false;
                unit.LastPursuitGoalDistance = unit.Vehicle.Position.DistanceTo(
                    unit.Destination);
                unit.RouteRecoveryCount++;
                unit.Arrived = false;
                LogRuntime("POLICE_BACKUP_DISPATCH_PURSUIT_ROUTE_RECOVERY",
                    "Recovery=" + unit.RouteRecoveryCount
                    + "; Vehicle=" + unit.Vehicle.Handle
                    + "; Suspect=" + suspect.Handle
                    + "; Destination=" + unit.Destination
                    + "; Role=" + (unit.VehiclePursuitBlocker
                        ? "ForwardBlocker" : "Pursuit"));
            }
            else
            {
                unit.RouteRecoveryCount++;
                unit.LastDriveTaskAt = now;
                LogRuntime("POLICE_BACKUP_DISPATCH_PURSUIT_ROUTE_RECOVERY_REJECTED",
                    "Recovery=" + unit.RouteRecoveryCount
                    + "; Vehicle=" + unit.Vehicle.Handle
                    + "; Suspect=" + suspect.Handle
                    + "; Candidate=" + recoveryDestination
                    + "; Reason=NO_VALID_VEHICLE_PATH_NODE");
            }
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

        private void RefreshDispatchPursuitTargetCache(
            LSPDDispatch dispatch,
            DateTime now)
        {
            LSPDDispatchEvent incident = dispatch == null
                ? _incident : dispatch.Current;
            if (ReferenceEquals(incident, _dispatchPursuitTargetCacheIncident)
                && now < _dispatchPursuitTargetCacheRefreshAt)
                return;

            _dispatchPursuitTargetCacheIncident = incident;
            _dispatchPursuitTargetCacheRefreshAt = now.AddSeconds(1);
            _dispatchVehiclePursuitTargetCache = incident == null
                ? new List<Ped>()
                : incident.CurrentVehiclePursuitTargets.ToList();
            _dispatchFootPursuitTargetCache = incident == null
                ? new List<Ped>() : ActiveFootPursuitTargets(dispatch);
        }

        private void ResetDispatchInterceptionTracking()
        {
            ResetDispatchInteriorAccess("DispatchInterceptionTrackingReset");
            _lastDispatchInterceptionTrackingAt = DateTime.MinValue;
            _dispatchPursuitTargetCacheIncident = null;
            _dispatchPursuitTargetCacheRefreshAt = DateTime.MinValue;
            _dispatchVehiclePursuitTargetCache.Clear();
            _dispatchFootPursuitTargetCache.Clear();
            _nextDispatchFootSupportAt = DateTime.MinValue;
            _nextDispatchGroupSceneSupportAt = DateTime.MinValue;
            _dispatchVehicleRouteFailureNotified = false;
            foreach (BackupUnit unit in _units)
                if (unit != null)
                {
                    unit.DispatchVehiclePursuitSuspectHandle = 0;
                    unit.DispatchVehiclePursuitVehicleHandle = 0;
                    unit.DispatchVehiclePursuitChaseTaskIssued = false;
                    unit.DispatchFootPursuitSuspectHandle = 0;
                    unit.NextDispatchFootPursuitRouteAt = DateTime.MinValue;
                    unit.LastPursuitTargetDistance = float.MaxValue;
                    unit.LastPursuitGoalDistance = float.MaxValue;
                    unit.DispatchVehicleStoppedAt = DateTime.MinValue;
                    unit.NextDispatchVehicleExitTaskAt = DateTime.MinValue;
                    unit.NextPursuitProgressCheckAt = DateTime.MinValue;
                    unit.DispatchVehicleExitRequested = false;
                    unit.PursuitRouteRecoveryExhausted = false;
                    unit.PursuitRouteFailureLogged = false;
                    unit.PursuitBlockerHolding = false;
                    unit.VehiclePursuitLead = false;
                    unit.VehiclePursuitBlocker = false;
                    unit.VehicleContainmentStopRequested = false;
                    unit.DispatchGroupSceneSupport = false;
                    unit.DispatchGroupSceneExitRequested = false;
                    unit.DispatchGroupSceneSupportTargetHandle = 0;
                    unit.NextDispatchGroupSceneExitTaskAt = DateTime.MinValue;
                }
            _dispatchContainmentStartedLogged = false;
            _dispatchComplianceRequestedLogged = false;
            _dispatchSuspectCompliantLogged = false;
            _nextDispatchComplianceAimAt = DateTime.MinValue;
            _nextDispatchArrestCombatCheckAt = DateTime.MinValue;
            _dispatchPlayerFinalArrestStartedLogged = false;
            _dispatchPlayerFinalArrestCompletedLogged = false;
            _dispatchFootInterceptorBySuspect.Clear();
            _dispatchFootLeadBySuspect.Clear();
            _dispatchGroupSceneOfficerBySuspect.Clear();
            _dispatchFootApproachTaskStates.Clear();
            _dispatchFootComplianceLogged.Clear();
            _dispatchFootSpeedTargetHandle = 0;
            _dispatchFootTargetSpeed = float.MaxValue;
            _dispatchFootSpeedSampleAt = DateTime.MinValue;
            _dispatchFootSpeedReadFailedLogged = false;
            _dispatchFootFormationTargetHandle = 0;
            _dispatchFootFormationSampleAt = DateTime.MinValue;
            _dispatchFootRearDistance = float.MaxValue;
            _dispatchFootLeadDistance = float.MaxValue;
            _dispatchFootLeadAhead = false;
            _dispatchArrestCombatReleasedPairs.Clear();
        }

        private void ReleaseDispatchArrestCombat(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return;
            if (!incident.PlayerHandcuffInProgress
                || incident.PlayerHandcuffTargetHandle <= 0)
            {
                _dispatchArrestCombatReleasedPairs.Clear();
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now < _nextDispatchArrestCombatCheckAt)
                return;
            _nextDispatchArrestCombatCheckAt = now.AddMilliseconds(
                DispatchArrestCombatCheckMilliseconds);

            Ped suspect = incident.Suspects == null ? null
                : incident.Suspects.FirstOrDefault(ped => ped != null
                    && ped.Exists()
                    && ped.Handle == incident.PlayerHandcuffTargetHandle);
            if (suspect == null || suspect.IsDead)
                return;

            foreach (Ped officer in _units.Where(IsUsable)
                .SelectMany(SupportOfficers)
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                .GroupBy(ped => ped.Handle)
                .Select(group => group.First()))
            {
                long pairKey = ((long)officer.Handle << 32)
                    | (uint)suspect.Handle;
                int sceneOfficerHandle;
                bool assignedToSuspect = _dispatchGroupSceneOfficerBySuspect.TryGetValue(
                        suspect.Handle, out sceneOfficerHandle)
                    && sceneOfficerHandle == officer.Handle;
                if (!assignedToSuspect)
                    assignedToSuspect = _units.Any(unit => unit != null
                        && unit.Officers != null
                        && unit.Officers.Any(value => value != null
                            && value.Exists() && value.Handle == officer.Handle)
                        && (unit.DispatchFootPursuitSuspectHandle == suspect.Handle
                            || unit.DispatchVehiclePursuitSuspectHandle == suspect.Handle));
                if (_dispatchArrestCombatReleasedPairs.Contains(pairKey)
                    || (!assignedToSuspect
                        && !IsOfficerEngagedWithTarget(officer, suspect)))
                    continue;

                try
                {
                    // Backup owns only its officer task. Dispatch keeps the
                    // suspect and the physical handcuff animation remains the
                    // arrest owner while the Player completes it. Clear the
                    // assigned officer even if GTA has already dropped the
                    // combat flag while a fired round is still in flight.
                    officer.Task.ClearAll();
                    _dispatchArrestCombatReleasedPairs.Add(pairKey);
                    LogRuntime("POLICE_BACKUP_DISPATCH_ARREST_COMBAT_RELEASED",
                        "Incident=" + incident.Id
                        + "; Officer=" + officer.Handle
                        + "; Suspect=" + suspect.Handle
                        + "; Reason=PlayerHandcuffInProgress"
                        + "; AssignedToTarget=" + assignedToSuspect
                        + "; Owner=BackupOfficerTask");
                }
                catch (Exception ex)
                {
                    LogException("POLICE_BACKUP_DISPATCH_ARREST_COMBAT_RELEASE_FAILED", ex);
                }
            }
        }

        private static bool IsDispatchSuspectArrestProtected(
            LSPDDispatchEvent incident,
            Ped suspect)
        {
            return incident != null && suspect != null
                && ((incident.PlayerHandcuffInProgress
                        && incident.PlayerHandcuffTargetHandle == suspect.Handle)
                    || (incident.PursuitContainedSuspectHandles != null
                        && incident.PursuitContainedSuspectHandles.Contains(suspect.Handle)));
        }

        private static int CountActiveDispatchFleeingSuspects(
            LSPDDispatchEvent incident)
        {
            if (incident == null || incident.Suspects == null)
                return 0;
            int count = 0;
            foreach (Ped suspect in incident.Suspects)
            {
                if (suspect == null || !suspect.Exists() || suspect.IsDead
                    || suspect.Health <= 0
                    || incident.PursuitContainedSuspectHandles.Contains(suspect.Handle)
                    || incident.ArrestedSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == suspect.Handle)
                    || incident.CompliantSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == suspect.Handle))
                    continue;
                string response;
                if (incident.SuspectResponses.TryGetValue(suspect.Handle, out response)
                    && string.Equals(response, "flee", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        private List<Ped> ActiveFootPursuitTargets(LSPDDispatch dispatch)
        {
            LSPDDispatchEvent incident = dispatch == null ? _incident : dispatch.Current;
            if (incident == null || incident.Suspects == null)
                return new List<Ped>();
            var targets = new List<Ped>();
            foreach (Ped suspect in incident.Suspects)
            {
                if (suspect == null || !suspect.Exists() || suspect.IsDead
                    || suspect.Health <= 0 || suspect.IsInVehicle()
                    || incident.PursuitContainedSuspectHandles.Contains(suspect.Handle)
                    || incident.ArrestedSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == suspect.Handle)
                    || incident.CompliantSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == suspect.Handle))
                    continue;
                string response;
                if (!incident.SuspectResponses.TryGetValue(suspect.Handle, out response)
                    || !string.Equals(response, "flee", StringComparison.OrdinalIgnoreCase))
                    continue;
                targets.Add(suspect);
                if (targets.Count >= 3)
                    break;
            }
            return targets;
        }

        private bool IsDispatchActorInsideInterior(Ped actor)
        {
            int interiorId;
            return actor != null && actor.Exists() && _ambientWorld != null
                && _ambientWorld.TryGetActorInteriorId(actor, out interiorId)
                && interiorId != 0;
        }

        private bool MaintainDispatchGroupSceneSupport(
            LSPDDispatch dispatch,
            DateTime now)
        {
            List<BackupUnit> sceneUnits = _units
                .Where(unit => IsUsable(unit) && unit.DispatchGroupSceneSupport)
                .ToList();
            if (sceneUnits.Count == 0)
                return false;
            if (now < _nextDispatchGroupSceneSupportAt)
                return true;
            _nextDispatchGroupSceneSupportAt = now.AddSeconds(1);

            LSPDDispatchEvent incident = dispatch == null ? _incident : dispatch.Current;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff)
            {
                foreach (BackupUnit unit in sceneUnits)
                {
                    unit.DispatchGroupSceneSupport = false;
                    unit.DispatchGroupSceneExitRequested = false;
                    unit.DispatchGroupSceneSupportTargetHandle = 0;
                    unit.NextDispatchGroupSceneExitTaskAt = DateTime.MinValue;
                }
                _dispatchGroupSceneOfficerBySuspect.Clear();
                return false;
            }

            List<Ped> sceneSuspects = DispatchGroupSceneTargets(incident);
            var activePursuitHandles = new HashSet<int>(
                _dispatchVehiclePursuitTargetCache
                    .Concat(_dispatchFootPursuitTargetCache)
                    .Where(ped => ped != null && ped.Exists())
                    .Select(ped => ped.Handle));
            var sceneOfficers = new List<Ped>();
            foreach (BackupUnit unit in sceneUnits)
            {
                Ped assignedTarget = sceneSuspects.FirstOrDefault(suspect =>
                    suspect.Handle == unit.DispatchGroupSceneSupportTargetHandle)
                    ?? sceneSuspects.FirstOrDefault();
                if (assignedTarget != null
                    && unit.DispatchGroupSceneSupportTargetHandle != assignedTarget.Handle)
                {
                    unit.DispatchGroupSceneSupportTargetHandle = assignedTarget.Handle;
                    Vector3 roadNode;
                    float roadHeading;
                    if (TryGetNearbyVehicleRoadNode(
                        assignedTarget.Position, incident.Origin, 75f,
                        out roadNode, out roadHeading)
                        && (unit.Destination == Vector3.Zero
                            || unit.Destination.DistanceTo(roadNode) >= 16f))
                    {
                        unit.Destination = roadNode;
                        unit.Arrived = false;
                        unit.LastProgressAt = now;
                        unit.LastProgressPosition = unit.Vehicle.Position;
                        unit.RouteRecoveryCount = 0;
                        unit.LastDriveTaskAt = DateTime.MinValue;
                        if (!SendTo(unit, roadNode, true))
                            unit.LastDriveTaskAt = now;
                    }
                    LogRuntime("POLICE_BACKUP_GROUP_SCENE_ROUTE_ASSIGNED",
                        "Incident=" + incident.Id
                        + "; Suspect=" + assignedTarget.Handle
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; RoadNode=" + unit.Destination
                        + "; PhysicalRoadNodeValidated=true");
                }

                if (!unit.Arrived)
                {
                    Vector3 position = unit.Vehicle.Position;
                    if (position.DistanceTo(unit.LastProgressPosition) >= 5f)
                    {
                        unit.LastProgressPosition = position;
                        unit.LastProgressAt = now;
                        unit.RouteRecoveryCount = 0;
                    }
                    else if (now >= unit.LastProgressAt.AddSeconds(RouteStallTimeoutSeconds)
                        && unit.RouteRecoveryCount < MaximumRouteRecoveries
                        && assignedTarget != null)
                    {
                        Vector3 recoveryRoad;
                        float recoveryHeading;
                        if (TryGetNearbyVehicleRoadNode(
                            assignedTarget.Position, incident.Origin, 75f,
                            out recoveryRoad, out recoveryHeading)
                            && (unit.Destination == Vector3.Zero
                                || unit.Destination.DistanceTo(recoveryRoad) >= 12f))
                        {
                            unit.Destination = recoveryRoad;
                            unit.RouteRecoveryCount++;
                            unit.LastProgressAt = now;
                            unit.LastProgressPosition = position;
                            unit.LastDriveTaskAt = DateTime.MinValue;
                            if (!SendTo(unit, recoveryRoad, true))
                                unit.LastDriveTaskAt = now;
                            LogRuntime("POLICE_BACKUP_GROUP_SCENE_ROUTE_RECOVERY",
                                "Incident=" + incident.Id
                                + "; Vehicle=" + unit.Vehicle.Handle
                                + "; Suspect=" + assignedTarget.Handle
                                + "; Recovery=" + unit.RouteRecoveryCount
                                + "; RoadNode=" + recoveryRoad);
                        }
                        else
                            unit.LastProgressAt = now;
                    }

                    float arrivalRadius = Math.Max(14f, _settings.ArrivalRadius);
                    if (unit.Destination != Vector3.Zero
                        && position.DistanceTo(unit.Destination) <= arrivalRadius)
                    {
                        unit.Arrived = true;
                        SetEmergencySignals(unit.Vehicle, false);
                        try
                        {
                            Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                            unit.Driver.Task.ClearAll();
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_GROUP_SCENE_HOLD_FAILED", ex);
                        }
                        LogRuntime("POLICE_BACKUP_GROUP_SCENE_UNIT_ARRIVED",
                            "Incident=" + incident.Id
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; Suspect=" + unit.DispatchGroupSceneSupportTargetHandle
                            + "; RoadPosition=" + unit.Vehicle.Position
                            + "; OfficersDeploy=true");
                    }
                    if (!unit.Arrived)
                        continue;
                }

                if (unit.NextDispatchGroupSceneExitTaskAt == DateTime.MinValue
                    || now >= unit.NextDispatchGroupSceneExitTaskAt)
                {
                    foreach (Ped officer in SupportOfficers(unit))
                    {
                        if (officer == null || !officer.Exists() || officer.IsDead
                            || !officer.IsInVehicle())
                            continue;
                        try { Function.Call(Hash.TASK_LEAVE_VEHICLE, officer, unit.Vehicle, 0); }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_GROUP_SCENE_EXIT_FAILED", ex);
                        }
                    }
                    unit.DispatchGroupSceneExitRequested = true;
                    unit.NextDispatchGroupSceneExitTaskAt = now.AddSeconds(2);
                }

                sceneOfficers.AddRange(SupportOfficers(unit)
                    .Where(officer => officer != null && officer.Exists()
                        && !officer.IsDead && !officer.IsInVehicle()));
            }

            if (sceneOfficers.Count > 0 && sceneSuspects.Count > 0)
            {
                HashSet<int> assigned = SupportDispatchGroupAtScene(
                    dispatch,
                    sceneOfficers,
                    new HashSet<int>(),
                    activePursuitHandles,
                    now);
                if (assigned.Count > 0)
                    SetState(LSPDBackupAssignmentState.Supporting);
                else
                    SetState(LSPDBackupAssignmentState.Intercepting);
            }
            return true;
        }

        private HashSet<int> SupportDispatchGroupAtScene(
            LSPDDispatch dispatch,
            List<Ped> officers,
            HashSet<int> alreadyAssignedOfficerHandles,
            HashSet<int> activePursuitHandles,
            DateTime now)
        {
            var assigned = new HashSet<int>();
            LSPDDispatchEvent incident = dispatch == null ? _incident : dispatch.Current;
            if (incident == null)
                return assigned;

            if (incident.BackupArrestInProgressOfficerHandles != null)
                assigned.UnionWith(
                    incident.BackupArrestInProgressOfficerHandles);
            if (incident.Suspects == null || incident.Suspects.Count < 2
                || officers == null || officers.Count == 0)
                return assigned;

            List<Ped> sceneSuspects = incident.Suspects
                .Where(suspect => suspect != null && suspect.Exists()
                    && !suspect.IsDead && suspect.Health > 0
                    && !incident.BackupArrestInProgressSuspectHandles.Contains(
                        suspect.Handle)
                    && !incident.ArrestedSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == suspect.Handle)
                    && suspect.Position.DistanceTo(incident.Origin) <= 100f)
                .Where(suspect =>
                {
                    bool alreadyContained = incident.PursuitContainedSuspectHandles
                            .Contains(suspect.Handle)
                        || incident.CompliantSuspects.Any(value => value != null
                            && value.Exists() && value.Handle == suspect.Handle);
                    string response;
                    bool fleeing = incident.SuspectResponses.TryGetValue(
                        suspect.Handle, out response)
                        && string.Equals(response, "flee",
                            StringComparison.OrdinalIgnoreCase);
                    return alreadyContained || (!fleeing
                        && (activePursuitHandles == null
                            || !activePursuitHandles.Contains(suspect.Handle)));
                })
                .OrderByDescending(suspect => suspect.IsShooting
                    || suspect.IsInCombat)
                .ThenByDescending(suspect =>
                {
                    string response;
                    return incident.SuspectResponses.TryGetValue(
                            suspect.Handle, out response)
                        && string.Equals(response, "resist",
                            StringComparison.OrdinalIgnoreCase);
                })
                .ThenBy(suspect => suspect.Position.DistanceTo(incident.Origin))
                .ToList();
            if (sceneSuspects.Count == 0)
            {
                _dispatchGroupSceneOfficerBySuspect.Clear();
                return assigned;
            }

            HashSet<int> sceneSuspectHandles = new HashSet<int>(
                sceneSuspects.Select(suspect => suspect.Handle));
            foreach (int stale in _dispatchGroupSceneOfficerBySuspect.Keys
                .Where(handle => !sceneSuspectHandles.Contains(handle)).ToList())
                _dispatchGroupSceneOfficerBySuspect.Remove(stale);

            bool canIssueSupportTask = CanIssueSupportTasks();
            foreach (Ped suspect in sceneSuspects)
            {
                int existingOfficerHandle;
                Ped officer = _dispatchGroupSceneOfficerBySuspect.TryGetValue(
                        suspect.Handle, out existingOfficerHandle)
                    ? officers.FirstOrDefault(value => value != null
                        && value.Exists() && !value.IsDead
                        && value.Handle == existingOfficerHandle
                        && (alreadyAssignedOfficerHandles == null
                            || !alreadyAssignedOfficerHandles.Contains(value.Handle))
                        && !assigned.Contains(value.Handle))
                    : null;
                if (officer == null)
                {
                    officer = officers.Where(value => value != null
                            && value.Exists() && !value.IsDead
                            && (alreadyAssignedOfficerHandles == null
                                || !alreadyAssignedOfficerHandles.Contains(value.Handle))
                            && !assigned.Contains(value.Handle))
                        .OrderBy(value => value.Position.DistanceTo(suspect.Position))
                        .FirstOrDefault();
                }
                if (officer == null)
                    break;

                assigned.Add(officer.Handle);
                int previousOfficerHandle;
                bool assignmentChanged = !_dispatchGroupSceneOfficerBySuspect.TryGetValue(
                    suspect.Handle, out previousOfficerHandle)
                    || previousOfficerHandle != officer.Handle;
                _dispatchGroupSceneOfficerBySuspect[suspect.Handle] = officer.Handle;
                if (assignmentChanged)
                    LogRuntime("POLICE_BACKUP_GROUP_SCENE_OFFICER_ASSIGNED",
                        "Incident=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Officer=" + officer.Handle
                        + "; PlayerPursuingOtherSuspect=true"
                        + "; SceneDistance=" + suspect.Position
                            .DistanceTo(incident.Origin).ToString("0.0"));

                if (officer.Position.DistanceTo(suspect.Position)
                    > DispatchOfficerContainmentStartDistance)
                {
                    MaintainDispatchFootApproachTask(officer, suspect, now, false);
                    continue;
                }

                if (dispatch != null
                    && incident.SceneArrivalReported
                    && dispatch.RequestBackupSuspectArrest(officer, suspect))
                    continue;

                if (!canIssueSupportTask)
                    continue;
                if (suspect.IsShooting || suspect.IsInCombat)
                    AssignCombat(officer, suspect);
                else
                    AimAt(officer, suspect);
            }

            return assigned;
        }

        private void SupportMultipleFootPursuits(
            LSPDDispatch dispatch,
            Ped player,
            List<Ped> targets,
            DateTime now)
        {
            if (!ReleaseInterceptionOfficers(now))
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            List<Ped> officers = _units.Where(unit => IsUsable(unit) && unit.Arrived)
                .SelectMany(SupportOfficers)
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead)
                .ToList();
            if (officers.Count == 0)
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            HashSet<int> activeTargetHandles = new HashSet<int>(
                targets.Select(value => value.Handle));
            var assignedOfficerHandles = new HashSet<int>();
            HashSet<int> sceneOfficerHandles = SupportDispatchGroupAtScene(
                dispatch, officers, assignedOfficerHandles,
                activeTargetHandles, now);
            assignedOfficerHandles.UnionWith(sceneOfficerHandles);
            HashSet<int> activeSceneTargetHandles = new HashSet<int>(
                _dispatchGroupSceneOfficerBySuspect.Keys);
            foreach (int stale in _dispatchFootInterceptorBySuspect.Keys
                .Where(handle => !activeTargetHandles.Contains(handle)).ToList())
                _dispatchFootInterceptorBySuspect.Remove(stale);
            foreach (int staleOfficer in _dispatchFootApproachTaskStates
                .Where(pair => !activeTargetHandles.Contains(pair.Value.TargetHandle)
                    && !activeSceneTargetHandles.Contains(pair.Value.TargetHandle))
                .Select(pair => pair.Key).ToList())
                _dispatchFootApproachTaskStates.Remove(staleOfficer);
            foreach (int stale in _dispatchFootLeadBySuspect.Keys
                .Where(handle => !activeTargetHandles.Contains(handle)).ToList())
                _dispatchFootLeadBySuspect.Remove(stale);

            bool anyContained = false;
            foreach (Ped target in targets)
            {
                int officerHandle;
                Ped interceptor = _dispatchFootInterceptorBySuspect.TryGetValue(
                        target.Handle, out officerHandle)
                    ? officers.FirstOrDefault(value => value.Handle == officerHandle
                        && !assignedOfficerHandles.Contains(value.Handle))
                    : null;
                Ped nearestAvailable = officers.Where(value =>
                        !assignedOfficerHandles.Contains(value.Handle))
                    .OrderBy(value => value.Position.DistanceTo(target.Position))
                    .FirstOrDefault();
                if (interceptor == null || (nearestAvailable != null
                    && nearestAvailable.Position.DistanceTo(target.Position) + 8f
                        < interceptor.Position.DistanceTo(target.Position)))
                    interceptor = nearestAvailable;
                if (interceptor == null)
                    continue;

                assignedOfficerHandles.Add(interceptor.Handle);
                int previousOfficer;
                if (!_dispatchFootInterceptorBySuspect.TryGetValue(
                        target.Handle, out previousOfficer)
                    || previousOfficer != interceptor.Handle)
                {
                    _dispatchFootInterceptorBySuspect[target.Handle] = interceptor.Handle;
                    LogRuntime("POLICE_BACKUP_GROUP_FOOT_INTERCEPTOR_ASSIGNED",
                        "Suspect=" + target.Handle + "; Officer=" + interceptor.Handle
                        + "; ActiveFootRunners=" + targets.Count
                        + "; Owner=Dispatch");
                }

                float distance = interceptor.Position.DistanceTo(target.Position);
                if (distance <= DispatchOfficerContainmentDistance)
                {
                    if (dispatch.RequestBackupInterception(target, player))
                    {
                        anyContained = true;
                        if (_dispatchFootComplianceLogged.Add(target.Handle))
                            LogRuntime("POLICE_BACKUP_GROUP_FOOT_SUSPECT_CONTAINED",
                                "Suspect=" + target.Handle + "; Officer=" + interceptor.Handle
                                + "; Distance=" + distance.ToString("0.0")
                                + "; PlayerFinalArrest=true");
                        if (!_dispatchSuspectCompliantLogged)
                        {
                            _dispatchSuspectCompliantLogged = true;
                            Notify("~b~POLICE BACKUP~s~\nBackup contained a fleeing suspect. Approach and use E to complete the arrest.");
                            Report("lsimmersivelife.police.compliance.confirmed",
                                "group-foot-intercepted");
                        }
                    }
                    continue;
                }

                MaintainDispatchFootApproachTask(
                    interceptor, target, now, false);
            }

            if (CanIssueSupportTasks())
                foreach (Ped officer in officers.Where(value =>
                    !assignedOfficerHandles.Contains(value.Handle)))
                {
                    Ped threat = targets.OrderBy(value =>
                        value.Position.DistanceTo(officer.Position)).FirstOrDefault();
                    if (threat != null && officer.Position.DistanceTo(threat.Position) <= 75f)
                        AimAt(officer, threat);
                }

            SetState(anyContained
                ? LSPDBackupAssignmentState.Securing
                : LSPDBackupAssignmentState.Intercepting);
        }

        private void SupportInterception(LSPDDispatch dispatch, Ped player)
        {
            Ped target = ResolveDispatchPursuitTarget(dispatch);
            if (target == null || !target.Exists() || target.IsDead)
            {
                BeginStandDown("The fleeing Dispatch suspect no longer exists.");
                return;
            }

            int targetInteriorId = 0;
            bool targetInteriorKnown = _ambientWorld != null
                && _ambientWorld.TryGetActorInteriorId(target, out targetInteriorId);
            if (_dispatchInteriorTargetHandle == target.Handle
                && (!targetInteriorKnown || targetInteriorId == 0))
                ResetDispatchInteriorAccess("DispatchSuspectPhysicallyOutside");

            Vehicle targetVehicle = ResolvePursuitVehicle(_incident, target);
            if (targetVehicle != null && target.IsInVehicle())
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            List<Ped> footTargets = ActiveFootPursuitTargets(dispatch);
            if (footTargets.Count > 1
                && !footTargets.Any(IsDispatchActorInsideInterior))
            {
                SupportMultipleFootPursuits(
                    dispatch, player, footTargets, DateTime.UtcNow);
                return;
            }

            if (!ReleaseInterceptionOfficers(DateTime.UtcNow))
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            List<Ped> officers = _units.Where(unit => IsUsable(unit) && unit.Arrived)
                .SelectMany(SupportOfficers)
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead)
                .ToList();
            if (officers.Count == 0)
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            DateTime now = DateTime.UtcNow;
            HashSet<int> sceneOfficerHandles = SupportDispatchGroupAtScene(
                dispatch,
                officers,
                new HashSet<int>(),
                new HashSet<int>(footTargets.Select(value => value.Handle)),
                now);
            if (sceneOfficerHandles.Count > 0)
                officers = officers.Where(officer =>
                    !sceneOfficerHandles.Contains(officer.Handle)).ToList();
            if (officers.Count == 0)
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            int previousRearHandle;
            Ped interceptor = _dispatchFootInterceptorBySuspect.TryGetValue(
                    target.Handle, out previousRearHandle)
                ? officers.FirstOrDefault(officer =>
                    officer.Handle == previousRearHandle)
                : null;
            Ped nearestInterceptor = officers
                .OrderBy(officer => officer.Position.DistanceTo(target.Position))
                .FirstOrDefault();
            if (interceptor == null || (nearestInterceptor != null
                && nearestInterceptor.Position.DistanceTo(target.Position) + 8f
                    < interceptor.Position.DistanceTo(target.Position)))
                interceptor = nearestInterceptor;

            int previousLeadHandle;
            bool hasLeadAssignment = _dispatchFootLeadBySuspect.TryGetValue(
                target.Handle, out previousLeadHandle);
            Ped leadOfficer = hasLeadAssignment
                ? officers.FirstOrDefault(officer =>
                    officer.Handle == previousLeadHandle
                    && officer.Handle != (interceptor == null ? 0 : interceptor.Handle))
                : null;
            if (interceptor != null && (!hasLeadAssignment
                || (previousLeadHandle != 0 && leadOfficer == null)))
                leadOfficer = officers
                    .Where(officer => officer.Handle != interceptor.Handle)
                    .OrderBy(officer => officer.Position.DistanceTo(target.Position))
                    .FirstOrDefault();

            if (interceptor == null)
            {
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }

            _dispatchFootInterceptorBySuspect[target.Handle] = interceptor.Handle;
            _dispatchFootLeadBySuspect[target.Handle] =
                leadOfficer == null ? 0 : leadOfficer.Handle;
            if (previousRearHandle != interceptor.Handle
                || previousLeadHandle != (leadOfficer == null ? 0 : leadOfficer.Handle))
            {
                _dispatchFootFormationSampleAt = DateTime.MinValue;
                LogRuntime("POLICE_BACKUP_SINGLE_FOOT_FORMATION_ASSIGNED",
                    "Suspect=" + target.Handle
                    + "; RearOfficer=" + interceptor.Handle
                    + "; LeadOfficer=" + (leadOfficer == null ? 0 : leadOfficer.Handle)
                    + "; Formation=" + (leadOfficer == null
                        ? "RearPursuitOnly" : "RearPursuitAndForwardContainment")
                    + "; Owner=Dispatch");
            }

            if (targetInteriorKnown && targetInteriorId != 0
                && MaintainInteriorDispatchInterception(
                    interceptor,
                    target,
                    targetInteriorId,
                    DateTime.UtcNow))
            {
                if (CanIssueSupportTasks())
                    foreach (Ped officer in officers)
                    {
                        if (officer == null || !officer.Exists() || officer.IsDead)
                            continue;
                        int officerInteriorId;
                        if (_ambientWorld.TryGetActorInteriorId(officer, out officerInteriorId)
                            && officerInteriorId == targetInteriorId
                            && officer.Position.DistanceTo(target.Position) <= 75f)
                            AimAt(officer, target);
                    }
                SetState(LSPDBackupAssignmentState.Intercepting);
                return;
            }
            RefreshDispatchFootFormationSample(
                target, interceptor, leadOfficer, now);
            float officerDistance = _dispatchFootRearDistance;
            if (!_dispatchContainmentStartedLogged
                && officerDistance <= DispatchOfficerContainmentStartDistance)
            {
                _dispatchContainmentStartedLogged = true;
                LogRuntime("POLICE_BACKUP_PHYSICAL_CONTAINMENT_STARTED",
                    "Suspect=" + target.Handle
                    + "; Interceptor=" + (interceptor == null ? 0 : interceptor.Handle)
                    + "; Distance=" + officerDistance.ToString("0.0"));
            }

            float leadDistance = _dispatchFootLeadDistance;
            bool leadIsAhead = _dispatchFootLeadAhead;
            float targetSpeed = SampleDispatchFootPursuitSpeed(target, now);
            bool targetStopped = targetSpeed <= DispatchFootStoppedSpeedThreshold;
            bool leadClose = leadOfficer == null
                || (leadDistance <= DispatchFootLeadContainmentDistance
                    && (leadIsAhead
                        || (targetStopped
                            && leadDistance <= DispatchOfficerContainmentDistance)));
            // A nearby officer must be able to issue the surrender command even
            // when the runner has passed the second officer or the full
            // ahead/rear formation has not formed. Requiring both officers to
            // reach arrest distance left the runner fleeing past Backup while
            // the officers repeatedly tried to reposition.
            bool interceptionReady = officerDistance <= DispatchOfficerContainmentStartDistance
                || (targetStopped && leadClose);

            if (!interceptionReady)
            {
                bool interceptorBusy = interceptor.IsInCombat
                    || interceptor.IsShooting;
                bool leadBusy = leadOfficer != null
                    && (leadOfficer.IsInCombat || leadOfficer.IsShooting);
                if (interceptorBusy && !leadBusy)
                {
                    interceptor = leadOfficer;
                    leadOfficer = null;
                    _dispatchFootInterceptorBySuspect[target.Handle] =
                        interceptor.Handle;
                    _dispatchFootLeadBySuspect[target.Handle] = 0;
                    _dispatchFootFormationSampleAt = DateTime.MinValue;
                    LogRuntime("POLICE_BACKUP_SINGLE_FOOT_FORMATION_ASSIGNED",
                        "Suspect=" + target.Handle
                        + "; RearOfficer=" + interceptor.Handle
                        + "; LeadOfficer=0; Formation=RearPursuitOnly"
                        + "; Reason=PreviousRearOfficerDefending");
                }
                else if (leadBusy)
                {
                    leadOfficer = null;
                    _dispatchFootLeadBySuspect[target.Handle] = 0;
                    _dispatchFootFormationSampleAt = DateTime.MinValue;
                }

                if (!interceptor.IsInCombat && !interceptor.IsShooting)
                    MaintainDispatchFootApproachTask(
                        interceptor, target, now, false);
                if (leadOfficer != null
                    && !leadOfficer.IsInCombat && !leadOfficer.IsShooting)
                    MaintainDispatchFootApproachTask(
                        leadOfficer, target, now, true);
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
                if (CanIssueSupportTasks() && now >= _nextDispatchComplianceAimAt)
                {
                    foreach (Ped officer in officers.Where(value =>
                        value.Position.DistanceTo(target.Position)
                            <= DispatchOfficerContainmentStartDistance))
                        AimAt(officer, target);
                    _nextDispatchComplianceAimAt = now.AddMilliseconds(
                        SupportTaskRefreshMilliseconds);
                }
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
                SetState(LSPDBackupAssignmentState.Supporting);
            }
        }

        private void MaintainDispatchFootApproachTask(
            Ped officer,
            Ped target,
            DateTime now,
            bool lead)
        {
            if (officer == null || !officer.Exists() || officer.IsDead
                || target == null || !target.Exists() || target.IsDead)
                return;

            DispatchFootApproachTaskState state;
            if (!_dispatchFootApproachTaskStates.TryGetValue(
                    officer.Handle, out state)
                || state.TargetHandle != target.Handle
                || state.LeadRole != lead)
            {
                float initialDistance = officer.Position.DistanceTo(target.Position);
                state = new DispatchFootApproachTaskState
                {
                    TargetHandle = target.Handle,
                    LeadRole = lead,
                    NextProgressSampleAt = now.AddMilliseconds(
                        DispatchFootApproachProgressSampleMilliseconds),
                    LastProgressAt = now,
                    LastTargetDistance = initialDistance
                };
                _dispatchFootApproachTaskStates[officer.Handle] = state;
                IssueDispatchFootApproachTask(
                    officer, target, lead, 0, "POLICE_BACKUP_FOOT_APPROACH_FAILED");
                return;
            }

            if (state.RecoveryExhausted || now < state.NextProgressSampleAt)
                return;

            state.NextProgressSampleAt = now.AddMilliseconds(
                DispatchFootApproachProgressSampleMilliseconds);
            float distance = officer.Position.DistanceTo(target.Position);
            if (distance <= state.LastTargetDistance
                - DispatchFootApproachProgressDistance)
            {
                state.LastTargetDistance = distance;
                state.LastProgressAt = now;
                state.RecoveryCount = 0;
                return;
            }

            state.LastTargetDistance = distance;
            if (now < state.LastProgressAt.AddSeconds(
                DispatchFootApproachStallTimeoutSeconds))
                return;

            if (state.RecoveryCount >= MaximumDispatchFootApproachRecoveries)
            {
                state.RecoveryExhausted = true;
                LogRuntime("POLICE_BACKUP_FOOT_APPROACH_RECOVERY_EXHAUSTED",
                    "Officer=" + officer.Handle
                    + "; Suspect=" + target.Handle
                    + "; Recoveries=" + state.RecoveryCount
                    + "; OtherBackupAndPlayerContinue=true");
                return;
            }

            state.RecoveryCount++;
            state.LastProgressAt = now;
            LogRuntime("POLICE_BACKUP_FOOT_APPROACH_REPOSITIONED",
                "Officer=" + officer.Handle
                + "; Suspect=" + target.Handle
                + "; Attempt=" + state.RecoveryCount
                + "; Role=" + (lead ? "ForwardContainment" : "RearPursuit"));
            IssueDispatchFootApproachTask(
                officer, target, lead, state.RecoveryCount,
                "POLICE_BACKUP_FOOT_APPROACH_RECOVERY_FAILED");
        }

        private void IssueDispatchFootApproachTask(
            Ped officer,
            Ped target,
            bool lead,
            int recoveryCount,
            string errorCategory)
        {
            try
            {
                if (recoveryCount > 0)
                {
                    float lateral = recoveryCount % 2 == 1 ? 2.5f : -2.5f;
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                        officer, target, lateral,
                        lead ? DispatchFootLeadOffsetY : -1.5f,
                        0f, DispatchFootPursuitTaskSpeed, -1, 0.3f, false);
                }
                else if (lead)
                {
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                        officer, target, 0f, DispatchFootLeadOffsetY, 0f,
                        DispatchFootPursuitTaskSpeed, -1, 0.3f, false);
                }
                else
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY,
                        officer, target, -1, 1.5f,
                        DispatchFootPursuitTaskSpeed, 1073741824, 0);
                }
            }
            catch (Exception ex)
            {
                LogException(errorCategory, ex);
            }
        }

        private void RefreshDispatchFootFormationSample(
            Ped target,
            Ped rearOfficer,
            Ped leadOfficer,
            DateTime now)
        {
            if (target == null || !target.Exists() || rearOfficer == null
                || !rearOfficer.Exists())
                return;
            if (_dispatchFootFormationTargetHandle == target.Handle
                && now < _dispatchFootFormationSampleAt.AddMilliseconds(
                    DispatchFootSpeedRefreshMilliseconds))
                return;

            _dispatchFootFormationTargetHandle = target.Handle;
            _dispatchFootFormationSampleAt = now;
            Vector3 targetPosition = target.Position;
            Vector3 rearOffset = rearOfficer.Position - targetPosition;
            _dispatchFootRearDistance = (float)Math.Sqrt(
                rearOffset.X * rearOffset.X
                + rearOffset.Y * rearOffset.Y
                + rearOffset.Z * rearOffset.Z);
            _dispatchFootLeadDistance = float.MaxValue;
            _dispatchFootLeadAhead = false;
            if (leadOfficer != null && leadOfficer.Exists() && !leadOfficer.IsDead)
            {
                Vector3 leadOffset = leadOfficer.Position - targetPosition;
                _dispatchFootLeadDistance = (float)Math.Sqrt(
                    leadOffset.X * leadOffset.X
                    + leadOffset.Y * leadOffset.Y
                    + leadOffset.Z * leadOffset.Z);
                Vector3 forward = target.ForwardVector;
                float forwardLength = (float)Math.Sqrt(
                    forward.X * forward.X + forward.Y * forward.Y);
                if (forwardLength > 0.001f)
                {
                    float forwardSeparation =
                        (leadOffset.X * forward.X + leadOffset.Y * forward.Y)
                        / forwardLength;
                    _dispatchFootLeadAhead = forwardSeparation >= 1.25f;
                }
            }

            SampleDispatchFootPursuitSpeed(target, now);
        }

        private float SampleDispatchFootPursuitSpeed(Ped target, DateTime now)
        {
            if (target == null || !target.Exists() || target.IsDead)
                return float.MaxValue;
            if (_dispatchFootSpeedTargetHandle != target.Handle
                || now >= _dispatchFootSpeedSampleAt.AddMilliseconds(
                    DispatchFootSpeedRefreshMilliseconds))
            {
                _dispatchFootSpeedTargetHandle = target.Handle;
                _dispatchFootSpeedSampleAt = now;
                try
                {
                    _dispatchFootTargetSpeed = Function.Call<float>(
                        Hash.GET_ENTITY_SPEED, target);
                    _dispatchFootSpeedReadFailedLogged = false;
                }
                catch (Exception ex)
                {
                    _dispatchFootTargetSpeed = float.MaxValue;
                    if (!_dispatchFootSpeedReadFailedLogged)
                    {
                        LogException("POLICE_BACKUP_FOOT_SPEED_SAMPLE_FAILED", ex);
                        _dispatchFootSpeedReadFailedLogged = true;
                    }
                }
            }
            return _dispatchFootTargetSpeed;
        }

        private bool MaintainInteriorDispatchInterception(
            Ped interceptor,
            Ped target,
            int targetInteriorId,
            DateTime now)
        {
            if (_ambientWorld == null || target == null || !target.Exists()
                || interceptor == null || !interceptor.Exists() || interceptor.IsDead)
                return true;

            if (_dispatchInteriorTargetHandle != target.Handle
                || _dispatchInteriorOfficerHandle != interceptor.Handle)
            {
                ResetDispatchInteriorAccess("DispatchInteriorTargetChanged");
                _dispatchInteriorTargetHandle = target.Handle;
                _dispatchInteriorOfficerHandle = interceptor.Handle;
            }

            int officerInteriorId;
            if (!_ambientWorld.TryGetActorInteriorId(interceptor, out officerInteriorId))
                return true;
            if (officerInteriorId == targetInteriorId)
            {
                ResetDispatchInteriorAccess("BackupOfficerReachedSuspectInterior");
                return false;
            }
            if (_dispatchInteriorPhase == 4)
                return true;
            if (officerInteriorId != 0)
            {
                FailDispatchInteriorAccess(interceptor, target,
                    "The Backup officer is in a different interior context.");
                return true;
            }
            if (_dispatchInteriorStartedAt != DateTime.MinValue
                && now >= _dispatchInteriorStartedAt.AddSeconds(DispatchInteriorAccessTimeoutSeconds))
            {
                FailDispatchInteriorAccess(interceptor, target,
                    "Backup could not physically reach the mapped door within the bounded route time.");
                return true;
            }

            if (_dispatchInteriorPhase == 0)
            {
                if (_incident == null
                    || _incident.SceneResolution == null
                    || !_incident.SceneResolution.IsInteriorScene
                    || _incident.SceneResolution.LiveInteriorId != targetInteriorId)
                {
                    FailDispatchInteriorAccess(interceptor, target,
                        "Backup target no longer matches the active Dispatch scene's live interior identity.");
                    return true;
                }

                AmbientInteriorAccessRoute route;
                string routeFailure;
                if (!_ambientWorld.TryResolveInteriorAccessRouteForInterior(
                    interceptor,
                    targetInteriorId,
                    _incident.SceneResolution.LocationId,
                    _incident.SceneResolution.CatalogInteriorId,
                    true,
                    out route,
                    out routeFailure))
                {
                    FailDispatchInteriorAccess(interceptor, target, routeFailure);
                    return true;
                }
                _dispatchInteriorRoute = route;
                _dispatchInteriorPhase = 1;
                _dispatchInteriorStartedAt = now;
                _dispatchInteriorNextTaskAt = DateTime.MinValue;
                LogRuntime(
                    "POLICE_BACKUP_INTERIOR_ROUTE_RESOLVED",
                    "Officer=" + interceptor.Handle
                    + "; Suspect=" + target.Handle
                    + "; Location=" + route.LocationId
                    + "; CatalogInteriorId=" + route.CatalogInteriorId
                    + "; LiveInteriorId=" + route.TargetLiveInteriorId
                    + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord
                    + "; Entry=" + route.EntryPosition
                    + "; Destination=" + route.DestinationPosition);
            }

            AmbientInteriorAccessRoute activeRoute = _dispatchInteriorRoute;
            if (activeRoute == null)
            {
                FailDispatchInteriorAccess(interceptor, target,
                    "The resolved Backup doorway route was lost.");
                return true;
            }

            if (_dispatchInteriorPhase == 1)
            {
                if (interceptor.Position.DistanceTo(activeRoute.EntryPosition) > 1.35f)
                {
                    if (now >= _dispatchInteriorNextTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                interceptor,
                                activeRoute.EntryPosition.X,
                                activeRoute.EntryPosition.Y,
                                activeRoute.EntryPosition.Z,
                                1.7f,
                                -1,
                                0.8f,
                                1,
                                activeRoute.Pair.OutsidePoint.HasHeading
                                    ? activeRoute.Pair.OutsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, interceptor, true);
                            _dispatchInteriorNextTaskAt = now.AddSeconds(4);
                            LogRuntime(
                                "POLICE_BACKUP_INTERIOR_THRESHOLD_APPROACH_STARTED",
                                "Officer=" + interceptor.Handle
                                + "; Suspect=" + target.Handle
                                + "; Entry=" + activeRoute.EntryPosition);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_INTERIOR_THRESHOLD_APPROACH_FAILED", ex);
                            FailDispatchInteriorAccess(interceptor, target, ex.Message);
                        }
                    }
                    return true;
                }

                if (_dispatchInteriorNextTaskAt != DateTime.MinValue
                    && now < _dispatchInteriorNextTaskAt)
                    return true;

                string doorFailure;
                if (!_ambientWorld.TryBeginInteriorAccess(
                    interceptor,
                    activeRoute,
                    out doorFailure))
                {
                    _dispatchInteriorRecoveryCount++;
                    LogDebug(
                        _dispatchInteriorRecoveryCount >= DispatchInteriorAccessMaximumDoorRetries
                            ? "POLICE_BACKUP_INTERIOR_DOOR_ACCESS_FAILED"
                            : "POLICE_BACKUP_INTERIOR_DOOR_ACCESS_RETRY",
                        "Officer=" + interceptor.Handle
                        + "; Suspect=" + target.Handle
                        + "; Recovery=" + _dispatchInteriorRecoveryCount
                        + "; Reason=" + doorFailure);
                    if (_dispatchInteriorRecoveryCount >= DispatchInteriorAccessMaximumDoorRetries)
                        FailDispatchInteriorAccess(interceptor, target, doorFailure);
                    else
                        _dispatchInteriorNextTaskAt = now.AddSeconds(1.5);
                    return true;
                }
                _dispatchInteriorPhase = 2;
                _dispatchInteriorNextTaskAt = DateTime.MinValue;
                LogRuntime(
                    "POLICE_BACKUP_INTERIOR_DOOR_OPENING_REQUESTED",
                    "Officer=" + interceptor.Handle
                    + "; Suspect=" + target.Handle
                    + "; DoorPair=" + activeRoute.Pair.InsidePoint.SourceRecord);
            }

            if (_dispatchInteriorPhase == 2 || _dispatchInteriorPhase == 3)
            {
                string accessMessage;
                AmbientInteriorAccessStatus accessStatus = _ambientWorld.MaintainInteriorAccess(
                    interceptor,
                    out accessMessage);
                if (accessStatus == AmbientInteriorAccessStatus.Failed)
                {
                    FailDispatchInteriorAccess(interceptor, target, accessMessage);
                    return true;
                }
                if (accessStatus == AmbientInteriorAccessStatus.DoorOpening)
                    return true;
                if (accessStatus == AmbientInteriorAccessStatus.ReadyToCross)
                {
                    if (now >= _dispatchInteriorNextTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                interceptor,
                                activeRoute.DestinationPosition.X,
                                activeRoute.DestinationPosition.Y,
                                activeRoute.DestinationPosition.Z,
                                1.6f,
                                -1,
                                0.35f,
                                1,
                                activeRoute.Pair.InsidePoint.HasHeading
                                    ? activeRoute.Pair.InsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, interceptor, true);
                            _dispatchInteriorPhase = 3;
                            _dispatchInteriorNextTaskAt = now.AddSeconds(3);
                            LogRuntime(
                                "POLICE_BACKUP_INTERIOR_DOOR_CROSSING_TASK_STARTED",
                                "Officer=" + interceptor.Handle
                                + "; Suspect=" + target.Handle
                                + "; Destination=" + activeRoute.DestinationPosition);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_INTERIOR_DOOR_CROSSING_TASK_FAILED", ex);
                            FailDispatchInteriorAccess(interceptor, target, ex.Message);
                        }
                    }
                    return true;
                }
                if (accessStatus == AmbientInteriorAccessStatus.Completed)
                {
                    LogRuntime(
                        "POLICE_BACKUP_INTERIOR_CROSSING_CONFIRMED",
                        "Officer=" + interceptor.Handle
                        + "; Suspect=" + target.Handle
                        + "; Location=" + activeRoute.LocationId
                        + "; CatalogInteriorId=" + activeRoute.CatalogInteriorId
                        + "; LiveInteriorId=" + activeRoute.TargetLiveInteriorId
                        + "; DoorPair=" + activeRoute.Pair.InsidePoint.SourceRecord);
                    ResetDispatchInteriorAccess("BackupOfficerCrossedDoor");
                    return false;
                }
                if (accessStatus == AmbientInteriorAccessStatus.None)
                {
                    FailDispatchInteriorAccess(interceptor, target,
                        "The mapped Backup door session ended before the officer's crossing was confirmed.");
                    return true;
                }
            }

            return true;
        }

        private void FailDispatchInteriorAccess(Ped interceptor, Ped target, string reason)
        {
            if (_ambientWorld != null && interceptor != null)
                _ambientWorld.CancelInteriorAccess(interceptor, "BackupInteriorAccessFailed");
            _dispatchInteriorPhase = 4;
            LogDebug(
                "POLICE_BACKUP_INTERIOR_ACCESS_FAILED",
                "Officer=" + (interceptor == null || !interceptor.Exists() ? 0 : interceptor.Handle)
                + "; Suspect=" + (target == null || !target.Exists() ? 0 : target.Handle)
                + "; Reason=" + (reason ?? string.Empty));
        }

        private void ResetDispatchInteriorAccess(string reason)
        {
            if (_ambientWorld != null && _dispatchInteriorOfficerHandle != 0)
            {
                Ped officer = _units.SelectMany(SupportOfficers)
                    .FirstOrDefault(value => value != null
                        && value.Exists()
                        && value.Handle == _dispatchInteriorOfficerHandle);
                if (officer != null)
                    _ambientWorld.CancelInteriorAccess(officer, reason);
            }
            _dispatchInteriorRoute = null;
            _dispatchInteriorOfficerHandle = 0;
            _dispatchInteriorTargetHandle = 0;
            _dispatchInteriorPhase = 0;
            _dispatchInteriorRecoveryCount = 0;
            _dispatchInteriorStartedAt = DateTime.MinValue;
            _dispatchInteriorNextTaskAt = DateTime.MinValue;
        }

        private bool ReleaseInterceptionOfficers(DateTime now)
        {
            bool everyOfficerExited = true;
            foreach (BackupUnit unit in _units)
            {
                if (!IsUsable(unit) || !unit.Arrived)
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
            bool pursuit = dispatch.Current != null
                && dispatch.Current.CurrentPursuitTarget != null;
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
            Report("lsimmersivelife.police.backup_reply.acknowledged", "backup-retasked");
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
                _npcAssignmentIdentity = null;
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
                    subject, npcResponse.ActivePursuitVehicle);
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
            Ped fleeing = interception ? incident.CurrentPursuitTarget : null;
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

            Ped selected = dispatch == null
                ? incident.CurrentPursuitTarget
                : dispatch.CurrentPursuitTarget;
            if (IsEligibleDispatchPursuitTarget(incident, selected)
                && (dispatch == null || dispatch.CurrentSuspects.Any(ped => ped != null
                    && ped.Exists() && ped.Handle == selected.Handle)))
                return selected;

            if (incident.Suspects == null)
                return null;
            return incident.Suspects.FirstOrDefault(suspect =>
                IsEligibleDispatchPursuitTarget(incident, suspect)
                && (dispatch == null || dispatch.CurrentSuspects.Any(ped => ped != null
                    && ped.Exists() && ped.Handle == suspect.Handle)));
        }

        private static bool IsEligibleDispatchPursuitTarget(
            LSPDDispatchEvent incident,
            Ped suspect)
        {
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || suspect.Health <= 0
                || IsDispatchSuspectArrestProtected(incident, suspect)
                || (incident.ArrestedSuspects != null
                    && incident.ArrestedSuspects.Any(ped => ped != null
                        && ped.Exists() && ped.Handle == suspect.Handle))
                || (incident.CompliantSuspects != null
                    && incident.CompliantSuspects.Any(ped => ped != null
                        && ped.Exists() && ped.Handle == suspect.Handle)))
                return false;

            string response;
            return incident.SuspectResponses != null
                && incident.SuspectResponses.TryGetValue(suspect.Handle, out response)
                && string.Equals(response, "flee", StringComparison.OrdinalIgnoreCase);
        }

        private static Vehicle ResolvePursuitVehicle(
            LSPDDispatchEvent incident,
            Ped target)
        {
            Vehicle assignedVehicle = incident == null
                ? null : incident.VehicleForSuspect(target);
            if (assignedVehicle == null && incident != null)
                assignedVehicle = incident.SuspectVehicle;
            return ResolvePursuitVehicle(target, assignedVehicle);
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

        private Vector3 ResolveDispatchVehicleForwardBlockPosition(
            Ped suspect,
            Vehicle suspectVehicle)
        {
            if (suspect == null || !suspect.Exists()
                || suspectVehicle == null || !suspectVehicle.Exists())
                return Vector3.Zero;

            Vector3 origin = suspectVehicle.Position;
            float directionX;
            float directionY;
            float speed;
            GetMovementDirection(suspectVehicle, out directionX, out directionY, out speed);
            float lead = Math.Max(28f, Math.Min(65f, speed * 2.5f));
            Vector3 projected = new Vector3(
                origin.X + directionX * lead,
                origin.Y + directionY * lead,
                origin.Z);
            Vector3 road;
            if (!TryGetNearbyRoadPosition(projected, origin, 30f, out road))
                return Vector3.Zero;

            float forwardDistance = (road.X - origin.X) * directionX
                + (road.Y - origin.Y) * directionY;
            return forwardDistance >= 12f && forwardDistance <= 90f
                ? road : Vector3.Zero;
        }

        private bool TryResolveDispatchVehiclePursuitRoadNode(
            Ped suspect,
            Vehicle suspectVehicle,
            bool forwardBlocker,
            out Vector3 roadPosition,
            out float roadHeading)
        {
            roadPosition = Vector3.Zero;
            roadHeading = 0f;
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspectVehicle == null || !suspectVehicle.Exists())
                return false;

            Vector3 candidate = forwardBlocker
                ? ResolveDispatchVehicleForwardBlockPosition(suspect, suspectVehicle)
                : ResolveInterceptionRoadPosition(suspect, suspectVehicle);
            if (candidate != Vector3.Zero
                && TryGetNearbyVehicleRoadNode(
                    candidate, suspectVehicle.Position, 45f,
                    out roadPosition, out roadHeading))
            {
                if (!forwardBlocker)
                    return true;

                float directionX;
                float directionY;
                float speed;
                GetMovementDirection(suspectVehicle,
                    out directionX, out directionY, out speed);
                float alongRoute = (roadPosition.X - suspectVehicle.Position.X)
                    * directionX
                    + (roadPosition.Y - suspectVehicle.Position.Y) * directionY;
                if (alongRoute >= 10f && alongRoute <= 100f)
                    return true;
            }

            roadPosition = Vector3.Zero;
            roadHeading = 0f;
            if (forwardBlocker)
                return false;

            // A trailing unit can still route to the suspect's current road
            // when the projected interception point is invalid. The fallback
            // remains tied to this assigned suspect vehicle; it never borrows
            // the primary suspect's cached destination.
            return TryGetNearbyVehicleRoadNode(
                suspectVehicle.Position,
                suspectVehicle.Position,
                45f,
                out roadPosition,
                out roadHeading);
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

        private bool TryFindPursuitBackupRoadStagingPosition(
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
            Vector3 origin = movingEntity.Position;
            float directionX;
            float directionY;
            float speed;
            GetMovementDirection(movingEntity, out directionX, out directionY, out speed);
            float perpendicularX = -directionY;
            float perpendicularY = directionX;
            bool vehicleTarget = targetVehicle != null && targetVehicle.Exists();
            float minimumDistance = MinimumPursuitSpawnDistance * 0.55f;
            float maximumDistance = MaximumPursuitSpawnDistance;
            int firstDirection = index == 0 ? 1 : -1;
            int directionCount = vehicleTarget ? 1 : 2;

            // The primary formation search can reject a mapped approach near
            // a tight street or slope. Try a small, deterministic ring of real
            // vehicle nodes around the runner before dropping this Backup car.
            for (int directionIndex = 0; directionIndex < directionCount; directionIndex++)
            {
                float longitudinalDirection = directionIndex == 0
                    ? firstDirection : -firstDirection;
                for (int ring = 0; ring < 3; ring++)
                {
                    float distance = MinimumPursuitSpawnDistance + ring * 25f;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float lateral = side * (10f + ring * 4f);
                        Vector3 candidate = new Vector3(
                            origin.X + directionX * distance * longitudinalDirection
                                + perpendicularX * lateral,
                            origin.Y + directionY * distance * longitudinalDirection
                                + perpendicularY * lateral,
                            origin.Z);
                        Vector3 road;
                        float heading;
                        if (!TryGetNearbyVehicleRoadNode(
                            candidate, origin, 30f, out road, out heading))
                            continue;

                        float roadDistance = road.DistanceTo(origin);
                        if (roadDistance < minimumDistance
                            || roadDistance > maximumDistance
                            || _units.Any(unit => IsUsable(unit)
                                && unit.Vehicle.Position.DistanceTo(road) < 18f))
                            continue;

                        float alongRoute = (road.X - origin.X) * directionX
                            + (road.Y - origin.Y) * directionY;
                        if (vehicleTarget && index == 0
                            && alongRoute < MinimumPursuitSpawnDistance * 0.25f)
                            continue;
                        if (vehicleTarget && index > 0
                            && alongRoute > -MinimumPursuitSpawnDistance * 0.25f)
                            continue;

                        stagingPosition = road;
                        return true;
                    }
                }
            }

            // Formation probes can miss road lanes near tight blocks or mapped
            // foot paths. Try a small local cardinal ring around the assigned
            // target before dropping this unit. Every accepted point must still
            // resolve to a live, same-level vehicle road node and preserve the
            // target-specific ahead/behind role and spacing from other units.
            return TryFindPursuitNearRoadStagingPosition(
                index,
                origin,
                directionX,
                directionY,
                vehicleTarget,
                minimumDistance,
                maximumDistance,
                out stagingPosition);
        }

        private bool TryFindDispatchGroupSceneSupportStagingPosition(
            Ped target,
            Ped player,
            out Vector3 stagingPosition)
        {
            stagingPosition = Vector3.Zero;
            if (_incident == null || target == null || !target.Exists()
                || target.IsDead || target.Health <= 0)
                return false;

            Vector3 center = target.Position;
            Vector2[] directions =
            {
                new Vector2(1f, 0f),
                new Vector2(-1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(0f, -1f),
                new Vector2(0.707f, 0.707f),
                new Vector2(-0.707f, 0.707f),
                new Vector2(0.707f, -0.707f),
                new Vector2(-0.707f, -0.707f)
            };
            float[] radii = { 28f, 42f, 58f, 72f };
            foreach (float radius in radii)
                foreach (Vector2 direction in directions)
                {
                    Vector3 candidate = new Vector3(
                        center.X + direction.X * radius,
                        center.Y + direction.Y * radius,
                        center.Z);
                    Vector3 roadNode;
                    float roadHeading;
                    if (!TryGetNearbyVehicleRoadNode(
                        candidate, center, 24f, out roadNode, out roadHeading))
                        continue;
                    float sceneDistance = roadNode.DistanceTo(_incident.Origin);
                    if (roadNode.DistanceTo(center) < 16f
                        || sceneDistance > 90f
                        || Math.Abs(roadNode.Z - center.Z)
                            > MaximumRoadElevationDifference
                        || (player != null && player.Exists()
                            && roadNode.DistanceTo(player.Position) < 14f)
                        || _units.Any(unit => IsUsable(unit)
                            && unit.Vehicle.Position.DistanceTo(roadNode) < 18f))
                        continue;

                    stagingPosition = roadNode;
                    return true;
                }
            return false;
        }

        private bool TryFindPursuitNearRoadStagingPosition(
            int index,
            Vector3 targetPosition,
            float directionX,
            float directionY,
            bool vehicleTarget,
            float minimumDistance,
            float maximumDistance,
            out Vector3 stagingPosition)
        {
            stagingPosition = Vector3.Zero;
            Vector2[] offsets =
            {
                new Vector2(1f, 0f),
                new Vector2(-1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(0f, -1f)
            };
            for (int ring = 0; ring < 3; ring++)
            {
                float distance = minimumDistance + 18f + ring * 24f;
                foreach (Vector2 offset in offsets)
                {
                    Vector3 candidate = new Vector3(
                        targetPosition.X + offset.X * distance,
                        targetPosition.Y + offset.Y * distance,
                        targetPosition.Z);
                    Vector3 road;
                    float heading;
                    if (!TryGetNearbyVehicleRoadNode(
                        candidate, targetPosition, 20f, out road, out heading))
                        continue;

                    float roadDistance = road.DistanceTo(targetPosition);
                    if (roadDistance < minimumDistance
                        || roadDistance > maximumDistance
                        || _units.Any(unit => IsUsable(unit)
                            && unit.Vehicle.Position.DistanceTo(road) < 18f))
                        continue;

                    float alongRoute = (road.X - targetPosition.X) * directionX
                        + (road.Y - targetPosition.Y) * directionY;
                    float roleClearance = minimumDistance * 0.25f;
                    bool stageAhead = index == 0;
                    if ((stageAhead && alongRoute < roleClearance)
                        || (!stageAhead && alongRoute > -roleClearance))
                        continue;
                    if (vehicleTarget && Math.Abs(road.Z - targetPosition.Z)
                        > MaximumRoadElevationDifference)
                        continue;

                    stagingPosition = road;
                    return true;
                }
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
                Math.Min(75f, Math.Max(35f, _settings.MinimumStagingDistance)));
            float maximumDistance = MaximumPursuitSpawnDistance;
            float perpendicularX = -directionY;
            float perpendicularY = directionX;
            bool stageAhead = (_incident != null && index == 0)
                || (_npcResponse != null && _npcTargetWasFleeing && index == 1);
            float unitLateral = (index % 2 == 0 ? -1f : 1f)
                * (10f + (index / 2) * 12f);

            // Dispatch foot and vehicle pursuits both use a forward lead unit
            // and trailing support units. Every result still requires a real,
            // same-level road node; after arrival, officers switch to the live
            // target's physical on-foot interception path.
            for (int ring = 0; ring < 3; ring++)
            {
                float distance = minimumDistance + ring * 30f;
                for (int side = -1; side <= 1; side++)
                {
                    float lateral = unitLateral + side * 12f;
                    float longitudinalDirection = stageAhead ? 1f : -1f;
                    Vector3 candidate = new Vector3(
                        targetPosition.X + directionX * distance * longitudinalDirection
                            + perpendicularX * lateral,
                        targetPosition.Y + directionY * distance * longitudinalDirection
                            + perpendicularY * lateral,
                        targetPosition.Z);
                    Vector3 road;
                    if (TryGetNearbyRoadPosition(candidate, targetPosition, 35f, out road)
                        && road.DistanceTo(targetPosition) >= minimumDistance * 0.55f
                        && road.DistanceTo(targetPosition) <= maximumDistance
                        && !_units.Any(unit => IsUsable(unit)
                            && unit.Vehicle.Position.DistanceTo(road) < 18f))
                    {
                        float alongRoute = (road.X - targetPosition.X) * directionX
                            + (road.Y - targetPosition.Y) * directionY;
                        if (stageAhead && alongRoute < minimumDistance * 0.45f)
                            continue;
                        if (!stageAhead && alongRoute > -minimumDistance * 0.45f)
                            continue;
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
                && stationRoad.DistanceTo(targetPosition) >= minimumDistance * 0.55f
                && stationRoad.DistanceTo(targetPosition) <= maximumDistance
                && !_units.Any(unit => IsUsable(unit)
                    && unit.Vehicle.Position.DistanceTo(stationRoad) < 18f))
            {
                float alongRoute = (stationRoad.X - targetPosition.X) * directionX
                    + (stationRoad.Y - targetPosition.Y) * directionY;
                if (!stageAhead || alongRoute >= minimumDistance * 0.45f)
                {
                    stagingPosition = stationRoad;
                    return true;
                }
            }

            // The player position is only a last safe-road lookup, never the
            // pursuit destination. Avoid returning a raw offset if no road can
            // be validated.
            if (player != null && player.Exists()
                && TryGetNearbyRoadPosition(player.Position, player.Position, 45f, out stationRoad)
                && stationRoad.DistanceTo(targetPosition) >= minimumDistance * 0.55f
                && stationRoad.DistanceTo(targetPosition) <= maximumDistance
                && !_units.Any(unit => IsUsable(unit)
                    && unit.Vehicle.Position.DistanceTo(stationRoad) < 18f))
            {
                float alongRoute = (stationRoad.X - targetPosition.X) * directionX
                    + (stationRoad.Y - targetPosition.Y) * directionY;
                if (!stageAhead || alongRoute >= minimumDistance * 0.45f)
                {
                    stagingPosition = stationRoad;
                    return true;
                }
            }
            return false;
        }

        private Vector3 FindNpcStagingPosition(Ped player, Vector3 destination)
        {
            if (player == null || !player.Exists())
                return Vector3.Zero;

            Vector3 reference = destination == Vector3.Zero
                ? player.Position : destination;
            bool closeCustodyTransport = _npcCustodyTransportRequested;
            float configuredDistance = Math.Max(35f, _settings.MinimumStagingDistance);
            float minimumDistance = closeCustodyTransport
                ? NpcCustodyTransportStagingDistance
                : destination == Vector3.Zero
                    ? configuredDistance
                    : Math.Max(42f, configuredDistance * 0.65f);
            float lateral = closeCustodyTransport ? 6f : 10f;
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
                if (!TryGetNearbyRoadPosition(
                    candidate,
                    reference,
                    closeCustodyTransport ? 24f : 55f,
                    out road))
                    continue;
                if (closeCustodyTransport)
                {
                    float targetDistance = road.DistanceTo(reference);
                    if (targetDistance < NpcCustodyTransportMinimumSceneDistance
                        || targetDistance > NpcCustodyTransportMaximumSceneDistance
                        || road.DistanceTo(player.Position) > 34f)
                        continue;
                }
                else
                {
                    if (road.DistanceTo(player.Position)
                        < Math.Max(22f, minimumDistance * 0.5f))
                        continue;
                    if (destination != Vector3.Zero
                        && road.DistanceTo(destination) < minimumDistance * 0.45f)
                        continue;
                }
                return road;
            }

            // A local road probe around the player is a safe last attempt,
            // but never return an unchecked coordinate that may be inside a
            // building, on a sidewalk, or at an invalid world position.
            Vector3[] localCandidates = closeCustodyTransport
                ? new[]
                {
                    player.GetOffsetPosition(new Vector3(-12f, 18f, 0f)),
                    player.GetOffsetPosition(new Vector3(12f, 18f, 0f)),
                    player.GetOffsetPosition(new Vector3(-12f, -18f, 0f)),
                    player.GetOffsetPosition(new Vector3(12f, -18f, 0f))
                }
                : new[]
                {
                    player.GetOffsetPosition(new Vector3(-18f, 28f, 0f)),
                    player.GetOffsetPosition(new Vector3(18f, 28f, 0f)),
                    player.GetOffsetPosition(new Vector3(-18f, -28f, 0f)),
                    player.GetOffsetPosition(new Vector3(18f, -28f, 0f))
                };
            foreach (Vector3 candidate in localCandidates)
            {
                Vector3 road;
                if (!TryGetNearbyRoadPosition(
                    candidate,
                    player.Position,
                    closeCustodyTransport ? 28f : 45f,
                    out road))
                    continue;
                if (closeCustodyTransport
                    && (road.DistanceTo(reference) > NpcCustodyTransportMaximumSceneDistance
                        || road.DistanceTo(player.Position) > 34f))
                    continue;
                if (destination != Vector3.Zero
                    && !closeCustodyTransport
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
                // During Citizen Backup escort, the NPC owner gives the
                // Officer and subject separate natural navigation tasks.
                // Recover the Officer toward the assigned rear door without
                // replacing the subject's follow task or attaching either Ped.
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
                        officer, subject, -1, 1.5f,
                        DispatchFootPursuitTaskSpeed, 1073741824, 0);
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
                bool dispatchPursuitRoadRoute = _incident != null
                    && _incident.CurrentPursuitTarget != null
                    && _convoy == null && _npcResponse == null
                    && _gangResponse == null;
                if (npcCustodyRoadRoute || gangRoadRoute || dispatchPursuitRoadRoute)
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
                        gangRoadRoute ? 25.0f
                            : dispatchPursuitRoadRoute ? 25.0f
                            : (_npcTransportStarted ? 24.0f : 22.0f),
                        (int)drivingFlags,
                        gangRoadRoute ? 8.0f
                            : dispatchPursuitRoadRoute ? 8.0f
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
                int copGroup = unchecked((int)StringHash.AtStringHash("COP", 0));
                if (copGroup != 0)
                    Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH,
                        officer, copGroup);
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
            if (officer == null || !officer.Exists() || officer.IsDead
                || officer.IsInCombat || officer.IsShooting
                || target == null || !target.Exists() || target.IsDead) return;
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

        private static bool IsAvailablePed(Ped ped)
        {
            try
            {
                return ped != null && ped.Exists() && !ped.IsDead;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAvailableVehicle(Vehicle vehicle)
        {
            try
            {
                return vehicle != null && vehicle.Exists();
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadPedCurrentVehicleHandle(
            Ped ped,
            out int vehicleHandle)
        {
            vehicleHandle = 0;
            try
            {
                if (ped == null || !ped.Exists() || ped.IsDead)
                    return false;
                if (!ped.IsInVehicle())
                    return true;

                Vehicle currentVehicle = ped.CurrentVehicle;
                if (currentVehicle == null || !currentVehicle.Exists())
                    return false;
                vehicleHandle = currentVehicle.Handle;
                return true;
            }
            catch
            {
                return false;
            }
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
            return ResolveOfficerModel(
                preferFavorite,
                _profile == null ? string.Empty : _profile.PreferredBackupPedModelName);
        }

        private string ResolveOfficerModel(bool preferFavorite, string preferredModelName)
        {
            if (_profile != null && preferFavorite && _settings.PreferSavedFavoritePed
                && !string.IsNullOrWhiteSpace(preferredModelName))
                return preferredModelName;
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
            if (_npcAssignmentIdentity != null)
                _npcAssignmentIdentity.LifecycleState = state;
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
