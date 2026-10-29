using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const int DispatchGroupUnitProvisioningRetrySeconds = 2;
        private const float DispatchRouteProgressDistance = 2f;
        private const int NpcEscortStallTimeoutSeconds = 10;
        private const int MaximumNpcEscortRecoveries = 4;
        private const float DefaultSafeCleanupDistance = 200f;
        private const int MaximumNpcRouteRetargets = 2;
        private const int MaximumDispatchStagingCandidates = 8;
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
        private const float NpcVehicleStoppedSpeed = 0.45f;
        private const float DispatchOfficerContainmentDistance = 5.5f;
        private const float DispatchOfficerContainmentStartDistance = 14f;
        private const float DispatchFootPursuitTaskSpeed = 4.0f;
        private const float DispatchFootLeadOffsetY = 5.0f;
        private const float DispatchFootLeadContainmentDistance = 8.0f;
        private const float DispatchFootRoadFallbackMaximumDistance = 90.0f;
        private const float DispatchFootRoadFallbackArrivalDistance = 12.0f;
        private const int NpcFootInterceptionRouteRefreshSeconds = 3;
        private const int NpcFootInterceptionRouteFreshSeconds = 4;
        private const float DispatchFootStoppedSpeedThreshold = 0.75f;
        private const int DispatchFootSpeedRefreshMilliseconds = 1000;
        private const int DispatchFootApproachProgressSampleMilliseconds = 3000;
        private const int DispatchFootApproachStallTimeoutSeconds = 10;
        private const int MaximumDispatchFootApproachRecoveries = 2;
        private const float DispatchFootApproachProgressDistance = 2f;
        private const int DispatchGroupExitRetrySeconds = 2;
        private const int MaximumDispatchGroupExitAttempts = 4;
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
        private const float NpcVehicleCloseContainmentRadius = 12f;
        private const float NpcVehicleBlockForwardDistance = 8f;
        private const float NpcVehicleBlockLateralDistance = 3f;
        private const float NpcVehicleBlockHeightDifference = 1.75f;
        private const int NpcVehicleTireOpportunityMilliseconds = 750;
        private const int NpcVehicleTireResultSampleMilliseconds = 250;
        private const int NpcVehicleTireShotDurationMilliseconds = 650;
        private const int NpcVehicleTireShotVerifyMilliseconds = 1500;
        private const float NpcVehicleTireShotMinimumRange = 6f;
        private const float NpcVehicleTireShotMaximumRange = 20f;
        private const float NpcVehicleTireShotMaximumSpeed = 4.5f;
        private const float NpcVehicleTireShooterSideOffset = 4.0f;
        private const float NpcVehicleTireShotLaneClearance = 2.5f;
        private const int NpcCustodySeatStableMilliseconds = 800;
        private const int NpcOfficerEvidenceSampleMilliseconds = 500;
        private const int NpcCustodySeatRetryMilliseconds = 12000;
        private const int NpcCustodySeatMaximumEntryAttempts = 4;
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
        private LSPDAuthority _authority;
        private readonly List<BackupUnit> _units = new List<BackupUnit>();
        private LSWorldAmbientBehavior _ambientWorld;

        private LSPDDispatchEvent _incident;
        private LSPDDispatchEvent _dispatchGroupPlanIncident;
        private readonly List<DispatchPursuitAssignment> _dispatchGroupScenePlan =
            new List<DispatchPursuitAssignment>();
        private readonly HashSet<string> _dispatchGroupRequestedOfficerModels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _nextDispatchGroupUnitRetryAt = DateTime.MinValue;
        private DateTime _nextDispatchGroupAssetRequestAt = DateTime.MinValue;
        private string _lastDispatchGroupProvisioningEvidence = string.Empty;
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
        private bool _dispatchUsesSavedFavoriteModels;
        private string _lastDispatchCrimeBackupOfficerModelName = string.Empty;
        private string _lastDispatchCrimeBackupPreferenceModelName = string.Empty;
        private string _lastCitizenBackupOfficerModelName = string.Empty;
        private string _lastCitizenBackupPreferenceModelName = string.Empty;
        private string _operationId = string.Empty;
        private NpcAssignmentIdentity _npcAssignmentIdentity;
        private readonly List<NpcDetachedCleanupOperation> _npcDetachedCleanupOperations =
            new List<NpcDetachedCleanupOperation>();
        private bool _arrivalReported;
        private bool _backupUnitCreatedForOperation;
        private DateTime _lastSupportTaskAt = DateTime.MinValue;
        private BackupUnit _npcTransportUnit;
        private Ped _npcEscortOfficer;
        private DateTime _nextNpcOfficerTaskAt = DateTime.MinValue;
        private DateTime _npcEscortEntryStartedAt = DateTime.MinValue;
        private DateTime _npcCustodySeatsStableSince = DateTime.MinValue;
        private DateTime _nextNpcOfficerEvidenceAt = DateTime.MinValue;
        private readonly Dictionary<int, string> _npcOfficerEvidenceSignatures =
            new Dictionary<int, string>();
        private bool _npcPursuitWeaponTasksStopped;
        private bool _npcCustodyWeaponTasksStopped;
        private bool _npcVehicleStopWeaponTasksCleared;
        private bool _npcCustodySeatRetryNotified;
        private bool _npcCustodySeatsConfirmedLogged;
        private bool _npcSeatNativeFailureLogged;
        private int _npcCustodySeatEntryAttempts;
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
        private DateTime _nextDispatchComplianceEligibilityLogAt = DateTime.MinValue;
        private DateTime _nextDispatchArrestCombatCheckAt = DateTime.MinValue;
        private bool _dispatchPlayerFinalArrestStartedLogged;
        private bool _dispatchPlayerFinalArrestCompletedLogged;
        private readonly Dictionary<int, int> _dispatchFootInterceptorBySuspect =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> _dispatchFootLeadBySuspect =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> _dispatchGroupSceneOfficerBySuspect =
            new Dictionary<int, int>();
        private readonly HashSet<int> _dispatchConvoyCustodyEscortDelegatedSuspects =
            new HashSet<int>();
        private readonly Dictionary<int, string> _dispatchGroupSceneTaskStateByOfficer =
            new Dictionary<int, string>();
        private readonly Dictionary<int, int> _dispatchVehiclePedFireCorrectionByOfficer =
            new Dictionary<int, int>();
        private readonly Dictionary<int, string> _dispatchVehicleFireAuthorizationByOfficer =
            new Dictionary<int, string>();
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
        private readonly HashSet<int> _dispatchFootCustodyArrestLogged =
            new HashSet<int>();
        private readonly HashSet<long> _dispatchArrestCombatReleasedPairs =
            new HashSet<long>();
        private readonly HashSet<int> _dispatchPlayerTargetingOfficerHandles =
            new HashSet<int>();
        private readonly HashSet<int> _dispatchUnfriendlyOfficerHandles =
            new HashSet<int>();
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
            internal int UnitIndex;
            internal string OfficerModelName;
            internal Vehicle Vehicle;
            internal Ped Driver;
            internal Blip BackupBlip;
            internal List<Ped> Officers = new List<Ped>();
            internal Vector3 Destination;
            internal Vector3 LastValidDriveDestination;
            internal Vector3 ReturnDestination;
            internal float RouteHeading;
            internal bool FinalStandDownRouteAttempted;
            internal DateTime FinalStandDownRouteDeadline = DateTime.MinValue;
            internal bool FinalStandDownGraceExpiredLogged;
            internal DateTime LastDriveTaskAt;
            internal DateTime NextDispatchDriveEvidenceLogAt = DateTime.MinValue;
            internal DateTime NextDispatchDriverSeatTaskAt = DateTime.MinValue;
            internal Vector3 LastProgressPosition;
            internal DateTime LastProgressAt;
            internal int RouteRecoveryCount;
            internal readonly List<Vector3> DispatchRouteRecoveryDestinations =
                new List<Vector3>();
            internal DateTime NextDispatchRouteRecoveryAt = DateTime.MinValue;
            internal bool DispatchRouteRecoveryExhaustionLogged;
            internal bool DispatchFootDeploymentIssued;
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
            // Shared per-unit wheel-intervention state for Citizen and Dispatch
            // pursuits. Keep one physical tire-shot owner per Backup unit.
            internal int NpcTireTargetVehicleHandle;
            internal Ped NpcTireShooter;
            internal Vector3 NpcTireShooterSafePosition;
            internal bool NpcTireShooterExitRequested;
            internal bool NpcTireShooterPositionTaskIssued;
            internal bool NpcTireShotIssued;
            internal bool NpcTireShotResolved;
            internal int NpcTireBurstBaselineMask;
            internal DateTime NpcTireShotAt = DateTime.MinValue;
            internal DateTime NextNpcTireActionAt = DateTime.MinValue;
            internal DateTime NextNpcTireResultSampleAt = DateTime.MinValue;
            internal DateTime NpcTireShooterExitAt = DateTime.MinValue;
            internal bool DispatchGroupSceneSupport;
            internal bool DispatchGroupSceneExitRequested;
            internal int DispatchGroupSceneSupportTargetHandle;
            internal DateTime NextDispatchGroupSceneExitTaskAt = DateTime.MinValue;
            internal string DispatchSceneDeploymentIncidentId = string.Empty;
            internal bool DispatchSceneOfficerRosterReported;
            internal readonly Dictionary<int, int> DispatchSceneExitAttempts =
                new Dictionary<int, int>();
            internal readonly Dictionary<int, DateTime> DispatchSceneNextExitTaskAt =
                new Dictionary<int, DateTime>();
            internal readonly HashSet<int> DispatchSceneExitConfirmedHandles =
                new HashSet<int>();
            internal readonly HashSet<int> DispatchSceneExitFailureLoggedHandles =
                new HashSet<int>();
            internal int DispatchFootPursuitSuspectHandle;
            internal DateTime NextDispatchFootPursuitRouteAt = DateTime.MinValue;
            internal bool DispatchFootPursuitRoadFallbackActive;
            internal Vector3 DispatchFootPursuitFallbackRoadNode = Vector3.Zero;
            internal DateTime NextDispatchFootPursuitFallbackTaskAt = DateTime.MinValue;
            internal DateTime NextRejectedDriveTargetRetryAt = DateTime.MinValue;
            internal DateTime NextRejectedDriveTargetLogAt = DateTime.MinValue;
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
            internal Ped NpcCitizenCustodyPed;
            internal bool NpcCitizenCustodyProtectionApplied;
            internal bool NpcCitizenCustodyOriginalInvincible;
            internal bool NpcCitizenCustodyAdoptionFailureLogged;
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
            internal string OfficerModelName;
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

        internal void AttachAuthority(LSPDAuthority authority)
        {
            _authority = authority;
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

        internal Ped DelegateDispatchCustodyEscortToConvoy(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead)
                return null;

            int officerHandle;
            bool hasAssignment = _dispatchFootInterceptorBySuspect.TryGetValue(
                suspect.Handle, out officerHandle);
            if (!hasAssignment)
                hasAssignment = _dispatchGroupSceneOfficerBySuspect.TryGetValue(
                    suspect.Handle, out officerHandle);
            if (!hasAssignment)
                return null;

            Ped officer = _units.Where(IsUsable)
                .SelectMany(SupportOfficers)
                .FirstOrDefault(value => value != null && value.Exists()
                    && !value.IsDead && value.Handle == officerHandle);
            if (officer == null)
                return null;

            if (_dispatchConvoyCustodyEscortDelegatedSuspects.Add(suspect.Handle))
                LogRuntime("POLICE_BACKUP_DISPATCH_CUSTODY_ESCORT_DELEGATED",
                    "Suspect=" + suspect.Handle
                    + "; Officer=" + officer.Handle
                    + "; Owner=ConvoyPhysicalLoading; BackupAssignmentRetained=true");
            return officer;
        }

        internal void ResumeDispatchCustodyEscort(Ped suspect)
        {
            if (suspect != null && suspect.Exists())
                _dispatchConvoyCustodyEscortDelegatedSuspects.Remove(suspect.Handle);
        }

        internal void CompleteDispatchCustodyEscortHandoff(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return;

            int suspectHandle = suspect.Handle;
            if (!_dispatchConvoyCustodyEscortDelegatedSuspects.Remove(
                suspectHandle))
                return;
            int officerHandle;
            bool hasOfficer = _dispatchFootInterceptorBySuspect.TryGetValue(
                suspectHandle, out officerHandle);
            if (!hasOfficer)
                hasOfficer = _dispatchGroupSceneOfficerBySuspect.TryGetValue(
                    suspectHandle, out officerHandle);
            _dispatchFootInterceptorBySuspect.Remove(suspectHandle);
            _dispatchFootLeadBySuspect.Remove(suspectHandle);
            _dispatchGroupSceneOfficerBySuspect.Remove(suspectHandle);

            if (hasOfficer)
            {
                bool officerStillAssigned = _dispatchFootInterceptorBySuspect
                        .Values.Contains(officerHandle)
                    || _dispatchGroupSceneOfficerBySuspect.Values
                        .Contains(officerHandle);
                if (!officerStillAssigned)
                {
                    Ped officer = _units.Where(IsUsable)
                        .SelectMany(SupportOfficers)
                        .FirstOrDefault(value => value != null && value.Exists()
                            && !value.IsDead && value.Handle == officerHandle);
                    if (officer != null)
                    {
                        try { officer.Task.ClearAll(); }
                        catch (Exception ex)
                        {
                            LogException("POLICE_BACKUP_DISPATCH_CUSTODY_ESCORT_RELEASE_FAILED", ex);
                        }
                    }
                }
            }
            LogRuntime("POLICE_BACKUP_DISPATCH_CUSTODY_ESCORT_TRANSFERRED",
                "Suspect=" + suspectHandle
                + "; Officer=" + (hasOfficer ? officerHandle.ToString() : "none")
                + "; PhysicalVehicleSeatConfirmed=true"
                + "; BackupAssignmentRetained=true");
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
            _officerModelName = ResolveContextBackupOfficerModel(
                preferSavedFavorite,
                _profile == null ? string.Empty : _profile.PreferredDispatchBackupPedModelName,
                false);
            _dispatchUsesSavedFavoriteModels = preferSavedFavorite
                && _settings.PreferSavedFavoritePed;
            _operationId = Guid.NewGuid().ToString("N");
            _assetPreparationDeadline = DateTime.UtcNow.AddSeconds(AssetPreparationTimeoutSeconds);
            _nextAssetRequestAt = DateTime.MinValue;
            _arrivalReported = false;
            _backupUnitCreatedForOperation = false;
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
                + "; Officer=" + _officerModelName + "; Favorite=" + preferSavedFavorite
                + "; FavoriteModelPolicy=PerContextRotation");
            return "Backup request accepted. Units are being prepared for the active Dispatch scene.";
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
            _officerModelName = ResolveContextBackupOfficerModel(
                preferSavedFavorite,
                _profile == null ? string.Empty : _profile.PreferredDispatchBackupPedModelName,
                false);
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
                + "; Favorite=" + preferSavedFavorite
                + "; FavoriteModelPolicy=PerContextRotation");
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
            _dispatchGroupPlanIncident = null;
            _dispatchGroupScenePlan.Clear();
            _nextDispatchGroupUnitRetryAt = DateTime.MinValue;
            _nextDispatchGroupAssetRequestAt = DateTime.MinValue;
            _lastDispatchGroupProvisioningEvidence = string.Empty;
            _dispatchUsesSavedFavoriteModels = false;
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
            _officerModelName = ResolveContextBackupOfficerModel(
                preferSavedFavorite,
                _profile == null ? string.Empty : _profile.PreferredCitizenBackupPedModelName,
                true);
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
            _npcCustodySeatsStableSince = DateTime.MinValue;
            _nextNpcOfficerEvidenceAt = DateTime.MinValue;
            _npcOfficerEvidenceSignatures.Clear();
            _npcPursuitWeaponTasksStopped = false;
            _npcCustodyWeaponTasksStopped = false;
            _npcVehicleStopWeaponTasksCleared = false;
            _npcCustodySeatRetryNotified = false;
            _npcCustodySeatsConfirmedLogged = false;
            _npcSeatNativeFailureLogged = false;
            _npcCustodySeatEntryAttempts = 0;
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
                + "; Favorite=" + preferSavedFavorite
                + "; FavoriteModelPolicy=PerContextRotation"
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
                    MaintainNpcCitizenCustodyProtection(
                        unit, "DetachedBackgroundTransport");
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
            ReleaseNpcCitizenCustodyProtection(
                unit, "CitizenBackupDetachedUnitReleased:" + (reason ?? "Unknown"));
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
            if (!convoyAssignment && !crimeActivityAssignment
                && !npcAssignment && !gangAssignment)
                RepairDispatchPlayerTargetingOfficers(
                    dispatch, player, _units.SelectMany(SupportOfficers)
                        .Where(officer => officer != null && officer.Exists()
                            && !officer.IsDead && !officer.IsInVehicle()));
            if (_state == LSPDBackupAssignmentState.Requested
                || _state == LSPDBackupAssignmentState.PreparingAssets)
            {
                ProcessAssetPreparation(player, now);
                return;
            }

            CleanupMissingUnits();
            if (HasPendingDispatchGroupProvisioning()
                && MaintainDispatchGroupUnitProvisioning(player, now))
            {
                // A documented multi-unit scene can remain in bounded
                // provisioning/recovery while its second or third validated
                // route becomes available. An empty first staging result is
                // not a terminal assignment failure and must not Reset() the
                // units or the retained authored plan.
                return;
            }
            if (_units.Count == 0)
            {
                if (npcAssignment && _npcTransportStarted && _npcPlayerSessionReleased)
                {
                    if (npcResponse.CitizenCustodyProtectionTransferRequired)
                    {
                        if (!_npcDepartureStallNotified)
                        {
                            _npcDepartureStallNotified = true;
                            LogRuntime("POLICE_BACKUP_NPC_CUSTODY_PROTECTION_ADOPTION_FAILED",
                                "Vehicle=Unavailable; CitizenReleaseBlocked=true");
                            Notify("Backup lost its transport before adopting the Citizen's custody protection. The Citizen handoff remains incomplete.");
                        }
                        return;
                    }
                    npcResponse.CompleteBackupBackgroundDeparture(
                        "Backup unit was no longer available after custody transfer.");
                    ReleaseNpcCitizenCustodyProtection(
                        _npcTransportUnit,
                        "BackupTransportUnitUnavailableAfterCustodyTransfer");
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
                        dispatch, vehiclePursuitTargets, player, now))
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

                bool dispatchRouteAssignment = IsDispatchRouteAssignment();
                Vector3 unitDestination = (pursuit || dispatchRouteAssignment)
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
                    if (dispatchRouteAssignment
                        && now < unit.NextDispatchRouteRecoveryAt)
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
                        if (dispatchRouteAssignment)
                        {
                            if (TryDeployDispatchUnitOnFoot(
                                unit, unitDestination, now))
                                continue;

                            unit.RouteRecoveryCount = 0;
                            unit.LastProgressAt = now;
                            unit.LastProgressPosition = unit.Vehicle.Position;
                            unit.NextDispatchRouteRecoveryAt = now.AddSeconds(15);
                            if (!unit.DispatchRouteRecoveryExhaustionLogged)
                            {
                                unit.DispatchRouteRecoveryExhaustionLogged = true;
                                LogRuntime(
                                    "POLICE_BACKUP_DISPATCH_ROUTE_RECOVERY_DELAYED",
                                    "Operation=" + _operationId
                                    + "; Incident=" + _incident.Id
                                    + "; Vehicle=" + unit.Vehicle.Handle
                                    + "; Destination=" + unitDestination
                                    + "; RecoveryAttempts=" + MaximumRouteRecoveries
                                    + "; UnitPreserved=true"
                                    + "; RetryAfterSeconds=15"
                                    + "; Reason=NoDistinctConnectedRouteOrSafeFootApproach");
                            }
                            SetState(LSPDBackupAssignmentState.Recovering);
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
                else if (IsDispatchRouteAssignment()
                    && _units.Any(unit => IsUsable(unit) && unit.Arrived))
                    SupportScene(dispatch, player, now);
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
                SupportScene(dispatch, player, now);
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

        internal void PrepareForPatrolEnd()
        {
            bool hasUnits = _units.Count > 0;
            bool preserveDeparture = hasUnits
                && (_state == LSPDBackupAssignmentState.StandingDown
                    || _npcBackgroundCleanupRequested
                    || (_npcTransportStarted && _npcPlayerSessionReleased));
            bool preserveDetachedCleanup = _npcDetachedCleanupOperations.Count > 0;
            if (!preserveDeparture && !preserveDetachedCleanup)
            {
                Reset();
                return;
            }

            ReleasePreparedModelRequests();
            ReleaseGangPlayerProtection();
            LogRuntime("POLICE_BACKUP_PATROL_END_DEPARTURE_RETAINED",
                "State=" + _state
                + "; Units=" + _units.Count
                + "; DetachedCleanupOperations=" + _npcDetachedCleanupOperations.Count
                + "; DepartureOwner=Backup"
                + "; Cleanup=ExistingBoundedLifecycle");
        }

        internal void Reset()
        {
            // If the assignment is cancelled while models are still streaming,
            // release those outstanding requests. Spawned entities are not
            // affected; this only returns the model assets to GTA's streamer.
            ReleasePreparedModelRequests();
            ReleaseGangPlayerProtection();
            foreach (BackupUnit unit in _units.ToArray())
            {
                DeleteUnit(unit);
                ReleaseNpcCitizenCustodyProtection(unit, "BackupAssignmentReset");
            }
            _units.Clear();
            _dispatchConvoyCustodyEscortDelegatedSuspects.Clear();
            _incident = null;
            _dispatchGroupPlanIncident = null;
            _dispatchGroupScenePlan.Clear();
            _nextDispatchGroupUnitRetryAt = DateTime.MinValue;
            _nextDispatchGroupAssetRequestAt = DateTime.MinValue;
            _lastDispatchGroupProvisioningEvidence = string.Empty;
            _dispatchUsesSavedFavoriteModels = false;
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
            _backupUnitCreatedForOperation = false;
            _lastSupportTaskAt = DateTime.MinValue;
            _npcTransportUnit = null;
            _npcEscortOfficer = null;
            _nextNpcOfficerTaskAt = DateTime.MinValue;
            _npcEscortEntryStartedAt = DateTime.MinValue;
            _npcCustodySeatsStableSince = DateTime.MinValue;
            _nextNpcOfficerEvidenceAt = DateTime.MinValue;
            _npcOfficerEvidenceSignatures.Clear();
            _npcPursuitWeaponTasksStopped = false;
            _npcCustodyWeaponTasksStopped = false;
            _npcVehicleStopWeaponTasksCleared = false;
            _npcCustodySeatRetryNotified = false;
            _npcCustodySeatsConfirmedLogged = false;
            _npcSeatNativeFailureLogged = false;
            _npcCustodySeatEntryAttempts = 0;
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
                string stagingFailureReason;
                if (!CreateUnits(vehicle, officer, player, out stagingFailureReason))
                {
                    if (HasPendingDispatchGroupProvisioning())
                    {
                        _nextDispatchGroupUnitRetryAt = now.AddSeconds(
                            DispatchGroupUnitProvisioningRetrySeconds);
                        SetState(LSPDBackupAssignmentState.Recovering);
                        RecordDispatchGroupProvisioningTransition(
                            now, "Delayed", stagingFailureReason);
                        return;
                    }
                    Fail(stagingFailureReason);
                    return;
                }
                if (HasPendingDispatchGroupProvisioning())
                {
                    _nextDispatchGroupUnitRetryAt = now.AddSeconds(
                        DispatchGroupUnitProvisioningRetrySeconds);
                    SetState(LSPDBackupAssignmentState.Recovering);
                    RecordDispatchGroupProvisioningTransition(
                        now, "Delayed", "A validated route or distinct approach was unavailable for every requested unit.");
                    return;
                }
                ReleaseDispatchGroupOfficerModelRequests();
                SetState(LSPDBackupAssignmentState.EnRoute);
                Notify("~b~POLICE BACKUP~s~\nSupport units are en route to the active situation.");
            }
            catch (Exception ex)
            {
                releaseAssets = true;
                LogException("POLICE_BACKUP_STAGE_FAILED", ex);
                if (HasPendingDispatchGroupProvisioning())
                {
                    _nextDispatchGroupUnitRetryAt = now.AddSeconds(
                        DispatchGroupUnitProvisioningRetrySeconds);
                    SetState(LSPDBackupAssignmentState.Recovering);
                    RecordDispatchGroupProvisioningTransition(
                        now, "Delayed", "Staging exception=" + ex.GetType().Name);
                }
                else
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

        private bool HasPendingDispatchGroupProvisioning()
        {
            if (_incident == null
                || !ReferenceEquals(_dispatchGroupPlanIncident, _incident)
                || _dispatchGroupScenePlan.Count < 2
                || GetActiveDispatchSuspects(_incident).Count == 0)
                return false;

            int usableGroupUnits = _units
                .Where(unit => IsUsable(unit) && unit.DispatchGroupSceneSupport)
                .Select(unit => unit.UnitIndex)
                .Distinct()
                .Count();
            return usableGroupUnits < _dispatchGroupScenePlan.Count;
        }

        private int CountUsableDispatchGroupUnits()
        {
            return _units
                .Where(unit => IsUsable(unit) && unit.DispatchGroupSceneSupport)
                .Select(unit => unit.UnitIndex)
                .Distinct()
                .Count();
        }

        private void RecordDispatchGroupProvisioningTransition(
            DateTime now,
            string outcome,
            string reason)
        {
            if (!HasPendingDispatchGroupProvisioning())
                return;

            int activeSuspects = GetActiveDispatchSuspects(_incident).Count;
            int usableUnits = CountUsableDispatchGroupUnits();
            string branch = GetAuthoredDispatchGroupSupportBranch(_incident);
            string evidence = "Outcome=" + (outcome ?? string.Empty)
                + "; Branch=" + branch
                + "; ActiveSuspects=" + activeSuspects
                + "; RequestedUnits=" + _dispatchGroupScenePlan.Count
                + "; CreatedUnits=" + usableUnits
                + "; Reason=" + (reason ?? string.Empty);
            if (string.Equals(_lastDispatchGroupProvisioningEvidence,
                evidence, StringComparison.Ordinal))
                return;

            _lastDispatchGroupProvisioningEvidence = evidence;
            LogRuntime("POLICE_BACKUP_DISPATCH_GROUP_PROVISIONING_" + outcome.ToUpperInvariant(),
                "Operation=" + _operationId
                + "; Incident=" + _incident.Id
                + "; " + evidence
                + "; RetryAt=" + _nextDispatchGroupUnitRetryAt.ToString("O")
                + "; AssignmentPreserved=true");
            if (string.Equals(outcome, "Delayed", StringComparison.Ordinal))
                Notify("~b~POLICE BACKUP~s~\nThe group route is blocked. Support staging will retry while the Dispatch incident remains active.");
        }

        private bool MaintainDispatchGroupUnitProvisioning(Ped player, DateTime now)
        {
            if (!HasPendingDispatchGroupProvisioning())
                return false;

            if (_nextDispatchGroupUnitRetryAt != DateTime.MinValue
                && now < _nextDispatchGroupUnitRetryAt)
                return _units.Count == 0;

            Model vehicle = new Model(_vehicleModelName);
            Model officer = new Model(_officerModelName);
            try
            {
                if (!IsUsableModel(vehicle, true) || !IsUsableModel(officer, false))
                {
                    _nextDispatchGroupUnitRetryAt = now.AddSeconds(
                        DispatchGroupUnitProvisioningRetrySeconds);
                    RecordDispatchGroupProvisioningTransition(
                        now, "Delayed", "Configured Backup assets are no longer valid.");
                    return _units.Count == 0;
                }

                if (!vehicle.IsLoaded || !officer.IsLoaded)
                {
                    if (now >= _nextDispatchGroupAssetRequestAt)
                    {
                        if (!vehicle.IsLoaded) vehicle.Request();
                        if (!officer.IsLoaded) officer.Request();
                        _nextDispatchGroupAssetRequestAt = now.AddMilliseconds(750);
                    }
                    if (_units.Count == 0)
                        _nextDispatchGroupUnitRetryAt = _nextDispatchGroupAssetRequestAt;
                    return _units.Count == 0;
                }

                int previousUnitCount = CountUsableDispatchGroupUnits();
                string stagingFailure;
                bool created = CreateUnits(vehicle, officer, player,
                    out stagingFailure);
                ReleaseModel(vehicle);
                ReleaseModel(officer);
                int currentUnitCount = CountUsableDispatchGroupUnits();
                if (currentUnitCount > previousUnitCount)
                {
                    LogRuntime("POLICE_BACKUP_DISPATCH_GROUP_UNIT_ADDED",
                        "Operation=" + _operationId
                        + "; Incident=" + _incident.Id
                        + "; ActiveSuspects=" + GetActiveDispatchSuspects(_incident).Count
                        + "; AuthoredBranch="
                        + GetAuthoredDispatchGroupSupportBranch(_incident)
                        + "; RequestedUnits=" + _dispatchGroupScenePlan.Count
                        + "; CreatedUnits=" + currentUnitCount
                        + "; UnitIndices=" + string.Join(",", _units
                            .Where(unit => IsUsable(unit)
                                && unit.DispatchGroupSceneSupport)
                            .Select(unit => unit.UnitIndex)
                            .Distinct().OrderBy(index => index))
                        + "; OfficerModels=" + string.Join(",", _units
                            .Where(unit => IsUsable(unit)
                                && unit.DispatchGroupSceneSupport)
                            .OrderBy(unit => unit.UnitIndex)
                            .Select(unit => unit.OfficerModelName)));
                }

                if (HasPendingDispatchGroupProvisioning())
                {
                    _nextDispatchGroupUnitRetryAt = now.AddSeconds(
                        DispatchGroupUnitProvisioningRetrySeconds);
                    SetState(LSPDBackupAssignmentState.Recovering);
                    RecordDispatchGroupProvisioningTransition(
                        now, "Delayed", string.IsNullOrWhiteSpace(stagingFailure)
                            ? "A distinct connected route for each authored unit is still pending."
                            : stagingFailure);
                    return _units.Count == 0;
                }

                _nextDispatchGroupUnitRetryAt = DateTime.MinValue;
                _nextDispatchGroupAssetRequestAt = DateTime.MinValue;
                _lastDispatchGroupProvisioningEvidence = string.Empty;
                ReleaseDispatchGroupOfficerModelRequests();
                LogRuntime("POLICE_BACKUP_DISPATCH_GROUP_UNIT_COUNT_READY",
                    "Operation=" + _operationId
                    + "; Incident=" + _incident.Id
                    + "; ActiveSuspects=" + GetActiveDispatchSuspects(_incident).Count
                    + "; AuthoredBranch="
                    + GetAuthoredDispatchGroupSupportBranch(_incident)
                    + "; RequestedUnits=" + _dispatchGroupScenePlan.Count
                    + "; CreatedUnits=" + currentUnitCount
                    + "; OfficerModels=" + string.Join(",", _units
                        .Where(unit => IsUsable(unit)
                            && unit.DispatchGroupSceneSupport)
                        .OrderBy(unit => unit.UnitIndex)
                        .Select(unit => unit.OfficerModelName))
                    + "; RouteConnectivity=PrevalidatedPerUnit");
                if (_state == LSPDBackupAssignmentState.Recovering)
                    SetState(LSPDBackupAssignmentState.EnRoute);
                return false;
            }
            catch (Exception ex)
            {
                _nextDispatchGroupUnitRetryAt = now.AddSeconds(
                    DispatchGroupUnitProvisioningRetrySeconds);
                RecordDispatchGroupProvisioningTransition(
                    now, "Delayed", "Retry exception=" + ex.GetType().Name);
                return _units.Count == 0;
            }
            finally
            {
                if (vehicle.IsLoaded) ReleaseModel(vehicle);
                if (officer.IsLoaded) ReleaseModel(officer);
            }
        }

        private bool CreateUnits(
            Model vehicleModel,
            Model officerModel,
            Ped player,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (player == null || !player.Exists())
            {
                failureReason = "No Backup unit was created because the Player is unavailable.";
                return false;
            }
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
            List<Ped> activeDispatchSuspects = _incident == null
                ? new List<Ped>() : GetActiveDispatchSuspects(_incident);
            bool retainedDispatchGroupPlan = _incident != null
                && ReferenceEquals(_dispatchGroupPlanIncident, _incident)
                && _dispatchGroupScenePlan.Count >= 2;
            bool authoredGroupResponseBranch = _incident != null
                && HasAuthoredDispatchGroupSupportBranch(_incident);
            bool authoredDispatchGroupResponse = !dispatchPursuit
                && _incident != null
                && activeDispatchSuspects.Count > 0
                && ((activeDispatchSuspects.Count >= 2
                        && authoredGroupResponseBranch)
                    || retainedDispatchGroupPlan);
            List<DispatchPursuitAssignment> dispatchPursuitPlan =
                new List<DispatchPursuitAssignment>();
            bool createdDispatchGroupPlan = false;
            if (authoredDispatchGroupResponse)
            {
                int groupUnitCount = retainedDispatchGroupPlan
                    ? _dispatchGroupScenePlan.Count
                    : Math.Min(3, Math.Max(2, activeDispatchSuspects.Count));
                if (!retainedDispatchGroupPlan)
                {
                    _dispatchGroupPlanIncident = _incident;
                    _dispatchGroupScenePlan.Clear();
                    createdDispatchGroupPlan = true;
                }

                // Rebind each planned unit to a currently living scene suspect
                // during a delayed staging retry. Preserve the original
                // authored unit count so a blocked second route does not
                // silently degrade a group request to one vehicle.
                for (int index = 0; index < groupUnitCount; index++)
                {
                    Ped plannedSuspect = activeDispatchSuspects.Count > 0
                        ? activeDispatchSuspects[index % activeDispatchSuspects.Count]
                        : null;
                    DispatchPursuitAssignment assignment =
                        new DispatchPursuitAssignment
                        {
                            Suspect = plannedSuspect,
                            SceneSupport = true,
                            SlotIndex = index,
                            OfficerModelName = ResolveDispatchGroupOfficerModel(
                                index, _officerModelName)
                        };
                    dispatchPursuitPlan.Add(assignment);
                }
                _dispatchGroupScenePlan.Clear();
                _dispatchGroupScenePlan.AddRange(dispatchPursuitPlan);
                if (createdDispatchGroupPlan)
                    LogRuntime("POLICE_BACKUP_DISPATCH_GROUP_SUPPORT_SELECTED",
                        "Operation=" + _operationId
                        + "; Incident=" + _incident.Id
                        + "; AuthoredBranches=" + string.Join(",",
                            _incident.BranchIds ?? new List<string>())
                        + "; ActiveSuspects=" + activeDispatchSuspects.Count
                        + "; AuthoredGroupBranch="
                        + GetAuthoredDispatchGroupSupportBranch(_incident)
                        + "; RequestedUnits=" + groupUnitCount
                        + "; UnitCountRule=TwoToThreeByActiveSuspectCount"
                        + "; OfficerModels=" + string.Join(",",
                            dispatchPursuitPlan.Select(assignment =>
                                assignment.OfficerModelName)));
            }
            Vector3 destination = _convoy != null && _convoy.Active
                ? ResolveConvoyDestination(_convoy, player)
                : _crimeActivity != null && _crimeActivity.Active
                    ? ResolveCrimeActivityDestination(_crimeActivity, player)
                    : _npcResponse != null && _npcResponse.HasBackupAssignment
                        ? ResolveNpcDestination(_npcResponse, player)
                        : _gangResponse != null && _gangResponse.HasActiveIncident
                            ? ResolveGangDestination(_gangResponse, player)
                        : ResolveDestination(null, player, dispatchPursuit);
            if (_incident != null && !dispatchPursuit
                && _convoy == null && _crimeActivity == null
                && _npcResponse == null && _gangResponse == null)
            {
                Vector3 exteriorApproach = ResolveDispatchVehicleApproach(
                    _incident, destination);
                if (exteriorApproach != Vector3.Zero)
                    destination = exteriorApproach;
            }
            if (destination == Vector3.Zero)
            {
                LogRuntime("POLICE_BACKUP_DRIVE_TARGET_REJECTED",
                    "Reason=NO_VALID_INITIAL_VEHICLE_PATH_NODE"
                    + (_incident == null || !authoredDispatchGroupResponse
                        ? string.Empty
                        : "; Incident=" + _incident.Id
                            + "; ActiveSuspects=" + activeDispatchSuspects.Count
                            + "; AuthoredBranch="
                            + GetAuthoredDispatchGroupSupportBranch(_incident)
                            + "; RequestedUnits=" + _dispatchGroupScenePlan.Count
                            + "; UnitPreserved=true; Retry=Delayed"));
                failureReason = "No Backup unit was created because the scene has no validated vehicle path node.";
                return false;
            }
            int requestedUnits = _npcResponse == null
                ? _settings.UnitsPerRequest
                : npcVehiclePursuit
                    ? NpcVehiclePursuitRequiredUnitCount
                    : 1;
            if (_gangResponse != null)
                requestedUnits = Math.Min(3, Math.Max(2, _settings.UnitsPerRequest));
            if (authoredDispatchGroupResponse)
            {
                requestedUnits = dispatchPursuitPlan.Count;
            }
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
            var dispatchApproachNodes = authoredDispatchGroupResponse
                ? _units
                    .Where(existing => existing != null
                        && existing.DispatchGroupSceneSupport
                        && existing.Destination != Vector3.Zero)
                    .Select(existing => existing.Destination)
                    .ToList()
                : new List<Vector3>();
            if (!dispatchPursuit && !authoredDispatchGroupResponse)
                dispatchPursuitPlan = new List<DispatchPursuitAssignment>();
            for (int index = 0; index < requestedUnits; index++)
            {
                if (authoredDispatchGroupResponse
                    && _units.Any(existing => existing.UnitIndex == index
                        && IsUsable(existing)))
                    continue;

                if (npcVehiclePursuit && index == 1
                    && !_units.Any(IsUsable))
                    break;

                Ped unitPursuitTarget = movingTarget;
                Vehicle unitPursuitVehicle = movingTargetVehicle;
                int pursuitSlotIndex = index;
                DispatchPursuitAssignment dispatchAssignment = dispatchPursuit
                    && index < dispatchPursuitPlan.Count
                    ? dispatchPursuitPlan[index]
                    : authoredDispatchGroupResponse
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
                int stagingCandidatesChecked = 0;
                if (dispatchGroupSceneSupport)
                {
                    int candidatesChecked;
                    if (!TryFindConnectedDispatchStagingPosition(
                        player, index, destination, out candidatesChecked,
                        out spawn))
                    {
                        LogRuntime("POLICE_BACKUP_GROUP_SCENE_STAGING_REJECTED",
                            "UnitIndex=" + index
                            + "; Target=" + (unitPursuitTarget == null
                                ? 0 : unitPursuitTarget.Handle)
                            + "; CandidatesChecked=" + candidatesChecked
                            + "; Reason=NO_CONNECTED_NEARBY_SCENE_ROAD_NODE");
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
                else if (_incident != null
                    && _gangResponse == null && _npcResponse == null)
                {
                    if (!TryFindConnectedDispatchStagingPosition(
                        player, index, destination,
                        out stagingCandidatesChecked, out spawn))
                        spawn = Vector3.Zero;
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
                        + "; Reason=NO_VALID_NEARBY_ROAD_POSITION"
                        + (stagingCandidatesChecked > 0
                            ? "; Search=BoundedValidatedRoadNodes"
                                + "; CandidatesChecked=" + stagingCandidatesChecked
                            : string.Empty));
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
                Vector3 unitDestination = destination;
                bool sharedSceneApproachFallback = false;
                string approachEvidence = string.Empty;
                if (_incident != null
                    && !TryFindDistinctDispatchApproachPosition(
                        index, vehicleNode, destination, dispatchApproachNodes,
                        out unitDestination, out _, out approachEvidence))
                {
                    // Multiple vehicles can use the same validated exterior
                    // scene approach when the road graph has no distinct
                    // approach node for each unit. Their vehicles still stage
                    // on separate, connected nodes and officers divide the
                    // suspects after dismounting.
                    unitDestination = destination;
                    sharedSceneApproachFallback = true;
                }
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
                int spawnInteriorId = 0;
                bool interiorProbeSucceeded = false;
                try
                {
                    spawnInteriorId = Function.Call<int>(
                        Hash.GET_INTERIOR_AT_COORDS,
                        vehicleNode.X, vehicleNode.Y, vehicleNode.Z);
                    interiorProbeSucceeded = true;
                }
                catch (Exception ex)
                {
                    if (_incident != null)
                    {
                        LogRuntime("POLICE_BACKUP_DISPATCH_STAGING_INTERIOR_PROBE_INCONCLUSIVE",
                            "Operation=" + _operationId
                            + "; UnitIndex=" + index
                            + "; RoadNode=" + vehicleNode
                            + "; Result=InteriorIdentityUnavailable"
                            + "; ValidatedVehicleRoadNode=true"
                            + "; Exception=" + ex.GetType().Name);
                    }
                }
                int authoredSceneInteriorId = _incident == null
                    || _incident.SceneResolution == null
                    || !_incident.SceneResolution.IsInteriorScene
                    ? 0 : _incident.SceneResolution.LiveInteriorId;
                bool roadNodeInsideAuthoredSceneInterior = _incident != null
                    && interiorProbeSucceeded
                    && authoredSceneInteriorId != 0
                    && spawnInteriorId == authoredSceneInteriorId;
                if (roadNodeInsideAuthoredSceneInterior)
                {
                    LogRuntime("POLICE_BACKUP_DISPATCH_STAGING_PREFLIGHT_REJECTED",
                        "Operation=" + _operationId
                        + "; UnitIndex=" + index
                        + "; RoadNode=" + vehicleNode
                        + "; InteriorId=" + spawnInteriorId
                        + "; AuthoredSceneInteriorId=" + authoredSceneInteriorId
                        + "; Result=RoadNodeInsideAuthoredInterior");
                    continue;
                }
                if (_incident != null && interiorProbeSucceeded
                    && spawnInteriorId != 0)
                    LogRuntime(
                        "POLICE_BACKUP_DISPATCH_STAGING_INTERIOR_PROBE_NONTERMINAL",
                        "Operation=" + _operationId
                        + "; Incident=" + _incident.Id
                        + "; UnitIndex=" + index
                        + "; RoadNode=" + vehicleNode
                        + "; InteriorId=" + spawnInteriorId
                        + "; AuthoredSceneInteriorId=" + authoredSceneInteriorId
                        + "; ValidatedVehicleRoadNode=true"
                        + "; Result=ContinueToRouteValidation");
                if (_incident != null)
                {
                    float connectedTravelDistance;
                    string routeConnectivityReason;
                    if (!TryValidateDispatchRouteConnectivity(
                        vehicleNode, unitDestination, out connectedTravelDistance,
                        out routeConnectivityReason))
                    {
                        LogRuntime("POLICE_BACKUP_DISPATCH_STAGING_PREFLIGHT_REJECTED",
                            "Operation=" + _operationId
                            + "; Incident=" + _incident.Id
                            + "; UnitIndex=" + index
                            + "; StagingNode=" + vehicleNode
                            + "; Approach=" + unitDestination
                            + "; RouteConnectivity=Rejected"
                            + "; Reason=" + routeConnectivityReason
                            + "; VehicleCreated=false");
                        continue;
                    }
                    if (sharedSceneApproachFallback)
                        LogRuntime(
                            "POLICE_BACKUP_DISPATCH_SHARED_SCENE_APPROACH_FALLBACK",
                            "Operation=" + _operationId
                            + "; Incident=" + _incident.Id
                            + "; UnitIndex=" + index
                            + "; StagingNode=" + vehicleNode
                            + "; SceneApproach=" + unitDestination
                            + "; RouteConnectivity=Connected"
                            + "; RouteTravelDistance="
                            + connectedTravelDistance.ToString("0.0")
                            + "; DistinctApproachUnavailable=true"
                            + "; DistinctApproachSearch=" + approachEvidence
                            + "; Reason=SharedApproachAllowsGroupProvisioning");
                    dispatchApproachNodes.Add(unitDestination);
                    Vector3 scenePosition = _incident.SceneResolution == null
                        ? destination : _incident.SceneResolution.SceneCenter;
                    LogRuntime("POLICE_BACKUP_DISPATCH_STAGING_PREFLIGHT",
                        "Operation=" + _operationId
                        + "; UnitIndex=" + index
                        + "; RoadNode=" + vehicleNode
                        + "; DistinctUnitIndex=true"
                        + "; RoadNodeValidated=true"
                        + "; Heading=" + spawnHeading.ToString("0.0")
                        + "; InteriorId=" + spawnInteriorId
                        + "; PlayerDistance=" + vehicleNode.DistanceTo(player.Position).ToString("0.0")
                        + "; ScenePosition=" + scenePosition
                        + "; SceneDistance=" + vehicleNode.DistanceTo(scenePosition).ToString("0.0")
                        + "; GroundEvidence=NativeVehicleRoadNode; PostPlacementDisplacementCheck=Pending"
                        + "; BuildingShellClearance=NotSeparatelyMeasured"
                        + "; RouteConnectivity=Connected"
                        + "; RouteNativeDistance=" + connectedTravelDistance.ToString("0.0")
                        + "; RouteEvidence=" + routeConnectivityReason
                        + "; VehicleCreated=false");
                }
                Vehicle vehicle = null;
                string unitOfficerModelName = dispatchAssignment == null
                    || string.IsNullOrWhiteSpace(dispatchAssignment.OfficerModelName)
                        ? _officerModelName
                        : dispatchAssignment.OfficerModelName;
                Model unitOfficerModel = officerModel;
                if (dispatchGroupSceneSupport
                    && !string.Equals(unitOfficerModelName, _officerModelName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Model assignedOfficerModel = new Model(unitOfficerModelName);
                    if (!IsUsableModel(assignedOfficerModel, false))
                    {
                        LogRuntime("POLICE_BACKUP_GROUP_OFFICER_MODEL_FALLBACK",
                            "UnitIndex=" + index
                            + "; RequestedModel=" + unitOfficerModelName
                            + "; FallbackModel=" + _officerModelName
                            + "; Reason=FAVORITE_MODEL_NOT_USABLE");
                        unitOfficerModelName = _officerModelName;
                    }
                    else if (!assignedOfficerModel.IsLoaded)
                    {
                        assignedOfficerModel.Request();
                        _dispatchGroupRequestedOfficerModels.Add(unitOfficerModelName);
                        LogRuntime("POLICE_BACKUP_GROUP_OFFICER_MODEL_REQUESTED",
                            "UnitIndex=" + index
                            + "; Model=" + unitOfficerModelName
                            + "; Incident=" + (_incident == null
                                ? string.Empty : _incident.Id)
                            + "; Retry=BoundedGroupProvisioning");
                        continue;
                    }
                    else
                    {
                        _dispatchGroupRequestedOfficerModels.Add(unitOfficerModelName);
                        unitOfficerModel = assignedOfficerModel;
                    }
                }
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
                    Ped driver = vehicle.CreatePedOnSeat(
                        VehicleSeat.Driver, unitOfficerModel);
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
                        UnitIndex = index,
                        OfficerModelName = unitOfficerModelName,
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
                        DispatchGroupSceneSupportTargetHandle = dispatchGroupSceneSupport
                            && unitPursuitTarget != null
                                ? unitPursuitTarget.Handle : 0,
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
                        Destination = destination,
                        ReturnDestination = spawn,
                        LastDriveTaskAt = DateTime.MinValue,
                        LastProgressPosition = spawn,
                        LastProgressAt = DateTime.UtcNow,
                        RouteRecoveryCount = 0
                    };
                    if (_incident != null)
                        unit.DispatchRouteRecoveryDestinations.Add(unitDestination);
                    unit.Officers.Add(driver);
                    if (officersPerUnit > 1)
                    {
                        Ped partner = vehicle.CreatePedOnSeat(
                            VehicleSeat.RightFront, unitOfficerModel);
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
                    _backupUnitCreatedForOperation = true;
                    if (dispatchPursuit && unitPursuitVehicle != null)
                        PrepareDispatchVehiclePursuitUnitForEnRoute(
                            unit, player, _incident == null ? string.Empty : _incident.Id);
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
                    SendTo(unit, unitDestination, true);
                    if (movingPursuit && unitPursuitTarget != null
                        && pursuitSlotIndex > 0)
                        unit.LastDriveTaskAt = DateTime.MinValue;
                    LogRuntime("POLICE_BACKUP_UNIT_STAGED",
                        "Vehicle=" + vehicle.Handle + "; Driver=" + driver.Handle
                        + "; OfficerModel=" + unitOfficerModelName
                        + "; Spawn=" + spawn + "; Destination=" + unitDestination
                        + "; RoadHeading=" + spawnHeading.ToString("0.0")
                        + (_npcResponse != null && _npcCustodyTransportRequested
                            ? "; Assignment=CitizenCustodyTransport; SceneDistance="
                                + spawn.DistanceTo(destination).ToString("0.0")
                            : string.Empty)
                        + (dispatchGroupSceneSupport
                            ? "; Assignment=DispatchGroupSceneSupport; Arrival=VehicleRouteRequired"
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
            if (_units.Count == 0)
            {
                failureReason = "No Backup unit was created. No validated unit could be staged for this operation.";
                return false;
            }
            return true;
        }

        private static bool HasAuthoredDispatchGroupSupportBranch(
            LSPDDispatchEvent incident)
        {
            return !string.IsNullOrWhiteSpace(
                    GetAuthoredDispatchGroupSupportBranch(incident))
                && !string.Equals(
                    GetAuthoredDispatchGroupSupportBranch(incident),
                    "None", StringComparison.Ordinal);
        }

        private static string GetAuthoredDispatchGroupSupportBranch(
            LSPDDispatchEvent incident)
        {
            if (incident == null || incident.BranchIds == null)
                return "None";

            string[] supportedBranches =
            {
                "dispatch-team-backup-group",
                "dispatch-store-robbery-group-gunfire"
            };
            return string.Join(",", supportedBranches.Where(branch =>
                incident.BranchIds.Contains(branch,
                    StringComparer.OrdinalIgnoreCase)));
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

        private void SupportScene(LSPDDispatch dispatch, Ped player, DateTime now)
        {
            LSPDDispatchEvent incident = dispatch == null ? null : dispatch.Current;
            if (!IsActiveDispatchSupportOwner(dispatch, incident))
                return;
            List<Ped> threats = dispatch.CurrentSuspects.Where(ped => ped != null
                && ped.Exists() && !ped.IsDead
                && !IsDispatchSuspectArrestProtected(incident, ped)).ToList();
            if (threats.Count == 0)
            {
                // Scene models can still be streaming while a naturally
                // arriving unit reaches the authored Dispatch location. Stay
                // available at the scene until Dispatch itself ends instead
                // of standing down before the group has been created.
                if (incident.Suspects != null && incident.Suspects.Count >= 2)
                {
                    if (now < _nextDispatchGroupSceneSupportAt)
                        return;
                    _nextDispatchGroupSceneSupportAt = now.AddSeconds(1);
                    List<Ped> deployed = PrepareDispatchGroupSceneOfficers(
                        incident, now);
                    RepairDispatchPlayerTargetingOfficers(
                        dispatch, player, deployed);
                    SetState(deployed.Count > 0
                        ? LSPDBackupAssignmentState.Supporting
                        : LSPDBackupAssignmentState.Intercepting);
                }
                else
                    SetState(LSPDBackupAssignmentState.Supporting);
                return;
            }
            if (incident.Suspects != null && incident.Suspects.Count >= 2)
            {
                if (now < _nextDispatchGroupSceneSupportAt)
                    return;
                _nextDispatchGroupSceneSupportAt = now.AddSeconds(1);

                List<Ped> groupOfficers = PrepareDispatchGroupSceneOfficers(
                    incident, now);
                RepairDispatchPlayerTargetingOfficers(
                    dispatch, player, groupOfficers);
                if (groupOfficers.Count == 0)
                {
                    SetState(LSPDBackupAssignmentState.Intercepting);
                    return;
                }

                HashSet<int> assigned = SupportDispatchGroupAtScene(
                    dispatch,
                    player,
                    groupOfficers,
                    new HashSet<int>(),
                    new HashSet<int>(),
                    now,
                    true);
                SetState(assigned.Count > 0
                    ? LSPDBackupAssignmentState.Supporting
                    : LSPDBackupAssignmentState.Intercepting);
                return;
            }
            List<Ped> officers = _units.Where(unit => IsUsable(unit) && unit.Arrived)
                .SelectMany(SupportOfficers)
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead
                    && !officer.IsInVehicle())
                .GroupBy(officer => officer.Handle)
                .Select(group => group.First())
                .ToList();
            RepairDispatchPlayerTargetingOfficers(dispatch, player, officers);
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
                            if (!AssignDispatchCombat(dispatch, player, officer, target))
                                continue;
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
                    {
                        if (officer == null || !officer.Exists() || officer.IsDead
                            || officer.IsInVehicle())
                            continue;
                        Ped target = NearestThreat(officer, threats);
                        if (target != null)
                            AimAtDispatchThreat(dispatch, player, officer, target);
                    }
            SetState(LSPDBackupAssignmentState.Supporting);
        }

        private List<Ped> PrepareDispatchGroupSceneOfficers(
            LSPDDispatchEvent incident,
            DateTime now)
        {
            var deployed = new List<Ped>();
            foreach (BackupUnit unit in _units.Where(value =>
                IsUsable(value) && value.Arrived))
            {
                if (!string.Equals(unit.DispatchSceneDeploymentIncidentId,
                        incident.Id, StringComparison.Ordinal))
                {
                    unit.DispatchSceneDeploymentIncidentId = incident.Id;
                    unit.DispatchSceneOfficerRosterReported = false;
                    unit.DispatchSceneExitAttempts.Clear();
                    unit.DispatchSceneNextExitTaskAt.Clear();
                    unit.DispatchSceneExitConfirmedHandles.Clear();
                    unit.DispatchSceneExitFailureLoggedHandles.Clear();
                }

                List<Ped> unitOfficers = SupportOfficers(unit)
                    .Where(officer => !officer.IsDead)
                    .ToList();
                if (!unit.DispatchSceneOfficerRosterReported)
                {
                    unit.DispatchSceneOfficerRosterReported = true;
                    LogRuntime("POLICE_BACKUP_DISPATCH_GROUP_OFFICER_ROSTER",
                        "Incident=" + incident.Id
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; Driver=" + (unit.Driver == null ? 0 : unit.Driver.Handle)
                        + "; Officers=" + string.Join(",",
                            unitOfficers.Select(officer => officer.Handle))
                        + "; OfficerCount=" + unitOfficers.Count);
                }

                foreach (Ped officer in unitOfficers)
                {
                    if (!officer.IsInVehicle())
                    {
                        if (unit.DispatchSceneExitConfirmedHandles.Add(
                                officer.Handle))
                            LogRuntime(
                                "POLICE_BACKUP_DISPATCH_GROUP_OFFICER_EXIT_CONFIRMED",
                                "Incident=" + incident.Id
                                + "; Vehicle=" + unit.Vehicle.Handle
                                + "; Officer=" + officer.Handle
                                + "; Role=" + (officer.Handle == unit.Driver.Handle
                                    ? "Driver" : "Partner")
                                + "; Position=" + officer.Position
                                + "; ExitTaskSource=ArrivalOrBoundedRetry");
                        deployed.Add(officer);
                        continue;
                    }

                    if (unit.DispatchSceneExitFailureLoggedHandles.Contains(
                            officer.Handle))
                        continue;
                    DateTime nextAttemptAt;
                    if (unit.DispatchSceneNextExitTaskAt.TryGetValue(
                            officer.Handle, out nextAttemptAt)
                        && now < nextAttemptAt)
                        continue;

                    Vehicle currentVehicle = null;
                    try { currentVehicle = officer.CurrentVehicle; }
                    catch { }
                    if (currentVehicle != null && currentVehicle.Exists()
                        && currentVehicle.Handle != unit.Vehicle.Handle)
                    {
                        unit.DispatchSceneExitFailureLoggedHandles.Add(
                            officer.Handle);
                        LogRuntime(
                            "POLICE_BACKUP_DISPATCH_GROUP_OFFICER_EXIT_FAILED",
                            "Incident=" + incident.Id
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; Officer=" + officer.Handle
                            + "; Reason=OfficerEnteredUnexpectedVehicle"
                            + "; CurrentVehicle=" + currentVehicle.Handle
                            + "; OtherOfficersContinue=true");
                        continue;
                    }

                    int attempts;
                    unit.DispatchSceneExitAttempts.TryGetValue(
                        officer.Handle, out attempts);
                    if (attempts >= MaximumDispatchGroupExitAttempts)
                    {
                        if (unit.DispatchSceneExitFailureLoggedHandles.Add(
                                officer.Handle))
                            LogRuntime(
                                "POLICE_BACKUP_DISPATCH_GROUP_OFFICER_EXIT_FAILED",
                                "Incident=" + incident.Id
                                + "; Vehicle=" + unit.Vehicle.Handle
                                + "; Officer=" + officer.Handle
                                + "; Role=" + (officer.Handle == unit.Driver.Handle
                                    ? "Driver" : "Partner")
                                + "; Attempts=" + attempts
                                + "; Maximum=" + MaximumDispatchGroupExitAttempts
                                + "; Reason=ExitTaskDidNotComplete"
                                + "; OtherOfficersContinue=true");
                        continue;
                    }

                    attempts++;
                    unit.DispatchSceneExitAttempts[officer.Handle] = attempts;
                    unit.DispatchSceneNextExitTaskAt[officer.Handle] =
                        now.AddSeconds(DispatchGroupExitRetrySeconds);
                    bool retry = attempts > 1;
                    bool taskIssued = false;
                    try
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            officer, unit.Vehicle, 0);
                        taskIssued = true;
                    }
                    catch (Exception ex)
                    {
                        LogException(
                            "POLICE_BACKUP_DISPATCH_GROUP_OFFICER_EXIT_TASK_FAILED",
                            ex);
                    }
                    LogRuntime(
                        retry
                            ? "POLICE_BACKUP_DISPATCH_GROUP_OFFICER_EXIT_RETRIED"
                            : "POLICE_BACKUP_DISPATCH_GROUP_OFFICER_EXIT_REQUESTED",
                        "Incident=" + incident.Id
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; Officer=" + officer.Handle
                        + "; Role=" + (officer.Handle == unit.Driver.Handle
                            ? "Driver" : "Partner")
                        + "; Attempt=" + attempts
                        + "; Maximum=" + MaximumDispatchGroupExitAttempts
                        + "; TaskIssued=" + taskIssued
                        + "; RetryAfterSeconds=" + DispatchGroupExitRetrySeconds
                        + "; OtherOfficersContinue=true");
                }
            }

            return deployed
                .GroupBy(officer => officer.Handle)
                .Select(group => group.First())
                .ToList();
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
            ObserveNpcBackupOfficerState(npcResponse, now);

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
                    npcResponse, unit, subject, subjectVehicle, now);
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
                    if (!_npcPursuitWeaponTasksStopped)
                    {
                        StopNpcOfficerWeaponTasks(
                            npcResponse, "VehicleStopContained", true);
                        _npcPursuitWeaponTasksStopped = true;
                    }
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

        private static string ResolveNpcVehicleStopEvidence(
            Vehicle subjectVehicle,
            Vehicle backupVehicle)
        {
            if (subjectVehicle == null || !subjectVehicle.Exists()
                || backupVehicle == null || !backupVehicle.Exists())
                return string.Empty;

            try
            {
                if (Function.Call<bool>(Hash.IS_ENTITY_TOUCHING_ENTITY,
                        subjectVehicle, backupVehicle))
                    return "BackupVehiclePhysicalContact";
            }
            catch { return string.Empty; }

            if (Math.Abs(backupVehicle.Speed) > 1.0f)
                return string.Empty;

            Vector3 offset = backupVehicle.Position - subjectVehicle.Position;
            if (Math.Abs(offset.Z) > NpcVehicleBlockHeightDifference)
                return string.Empty;
            float forwardDistance = Vector3.Dot(offset,
                subjectVehicle.ForwardVector);
            if (forwardDistance < 0.0f
                || forwardDistance > NpcVehicleBlockForwardDistance)
                return string.Empty;

            float lateralSquared = Math.Max(0.0f,
                offset.X * offset.X + offset.Y * offset.Y
                - forwardDistance * forwardDistance);
            return lateralSquared <= NpcVehicleBlockLateralDistance
                    * NpcVehicleBlockLateralDistance
                ? "BackupVehiclePhysicalBlockPosition" : string.Empty;
        }

        private bool MaintainNpcVehicleInterception(
            LSPDNPCResponse npcResponse,
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
            if (npcResponse != null)
            {
                foreach (BackupUnit pursuitUnit in pursuingUnits)
                {
                    string stopEvidence = ResolveNpcVehicleStopEvidence(
                        subjectVehicle, pursuitUnit.Vehicle);
                    if (!string.IsNullOrEmpty(stopEvidence))
                        npcResponse.ReportBackupTrafficVehicleIntervention(
                            subjectVehicle, pursuitUnit.Vehicle, stopEvidence, now);
                }
            }
            foreach (BackupUnit pursuitUnit in pursuingUnits)
            {
                bool targetChanged = pursuitUnit.NpcPursuitTargetHandle != subject.Handle
                    || pursuitUnit.NpcPursuitVehicleHandle != subjectVehicle.Handle;
                if (targetChanged)
                {
                    RecoverNpcTireShooter(pursuitUnit, "PursuitTargetChanged");
                    ResetNpcTireIntervention(pursuitUnit,
                        subjectVehicle.Handle);
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
            bool targetStopped = targetSpeed <= NpcVehicleStoppedSpeed;
            bool interventionConfirmed = npcResponse != null
                && npcResponse.HasRecentTrafficVehicleStopIntervention(
                    subjectVehicle, now);
            if (targetStopped && interventionConfirmed)
            {
                if (_npcVehicleStoppedAt == DateTime.MinValue)
                    _npcVehicleStoppedAt = now;
                if (!_npcVehicleStopWeaponTasksCleared)
                {
                    StopNpcOfficerWeaponTasks(
                        npcResponse, "CausalVehicleStopObserved", false);
                    _npcVehicleStopWeaponTasksCleared = true;
                    LogRuntime("POLICE_BACKUP_NPC_VEHICLE_STOP_TASKS_CLEARED",
                        "Ped=" + subject.Handle
                        + "; TargetVehicle=" + subjectVehicle.Handle
                        + "; Intervention="
                        + npcResponse.HasRecentTrafficVehicleStopIntervention(
                            subjectVehicle, now)
                        + "; SustainedStopPending=true");
                }
            }
            else
            {
                _npcVehicleStoppedAt = DateTime.MinValue;
                _npcVehicleContainmentReported = false;
                _npcVehicleStopWeaponTasksCleared = false;
                if (!targetStopped)
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

            MaintainBackupVehicleTireIntervention(
                npcResponse, null, blocker, subject, subjectVehicle, now);

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
            if (!sustainedStop || !interventionConfirmed
                || nearestUnitDistance > NpcVehicleCloseContainmentRadius)
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
                    + "; StopIntervention=" + interventionConfirmed
                    + "; TargetSpeed=" + targetSpeed.ToString("0.00")
                    + "; SustainedStopMilliseconds=" + NpcVehicleStopSettleMilliseconds
                    + "; SameCitizen=true");
            }
            SetState(LSPDBackupAssignmentState.Intercepting);
            return true;
        }

        private void MaintainBackupVehicleTireIntervention(
            LSPDNPCResponse npcResponse,
            LSPDDispatch dispatch,
            BackupUnit blocker,
            Ped subject,
            Vehicle subjectVehicle,
            DateTime now)
        {
            bool dispatchOwned = npcResponse == null
                && dispatch != null && dispatch.Current != null;
            if (npcResponse == null && !dispatchOwned)
                return;
            if (!IsUsable(blocker) || !blocker.VehiclePursuitBlocker
                || blocker.NpcBlockerFallbackToTrail
                || !blocker.PursuitBlockerHolding
                || Math.Abs(blocker.Vehicle.Speed) > 0.5f
                || subject == null || !subject.Exists() || subject.IsDead
                || subjectVehicle == null || !subjectVehicle.Exists())
                return;

            if (npcResponse != null
                && npcResponse.HasRecentTrafficVehicleStopIntervention(
                    subjectVehicle, now))
                return;

            if (blocker.NpcTireTargetVehicleHandle != subjectVehicle.Handle)
            {
                RecoverNpcTireShooter(blocker, "PursuitTargetChanged", dispatchOwned);
                ResetNpcTireIntervention(blocker, subjectVehicle.Handle);
            }
            if (blocker.NpcTireShotResolved
                || now < blocker.NextNpcTireActionAt)
                return;

            if (dispatchOwned && blocker.NpcTireShotIssued
                && !subject.IsInVehicle())
            {
                blocker.NpcTireShotResolved = true;
                LogRuntime("POLICE_BACKUP_DISPATCH_TIRE_SHOT_CANCELLED",
                    "Incident=" + dispatch.Current.Id
                    + "; Suspect=" + subject.Handle
                    + "; TargetVehicle=" + subjectVehicle.Handle
                    + "; Reason=DriverExitedBeforeTireResult"
                    + "; DriverBodyTargeted=false; PursuitContinues=true");
                RecoverNpcTireShooter(blocker, "DispatchDriverExited", true);
                return;
            }

            float targetSpeed = Math.Abs(subjectVehicle.Speed);
            float targetDistance = blocker.Vehicle.Position.DistanceTo(
                subjectVehicle.Position);
            float targetAhead = Vector3.Dot(
                subjectVehicle.Position - blocker.Vehicle.Position,
                blocker.Vehicle.ForwardVector);
            if (dispatchOwned)
            {
                LSPDDispatchEvent occurrence = dispatch.Current;
                Ped activeTarget = dispatch.CurrentPursuitTarget;
                Vehicle activeVehicle = occurrence == null || activeTarget == null
                    ? null : occurrence.VehicleForSuspect(activeTarget);
                bool exactOccurrenceTarget = occurrence != null
                    && IsEligibleDispatchPursuitTarget(occurrence, subject)
                    && activeTarget != null && activeTarget.Exists()
                    && activeTarget.Handle == subject.Handle
                    && activeVehicle != null && activeVehicle.Exists()
                    && activeVehicle.Handle == subjectVehicle.Handle;
                bool shooterAvailable = blocker.Officers != null
                    && blocker.Officers.Any(officer => officer != null
                        && officer.Exists() && !officer.IsDead
                        && blocker.Driver != null
                        && officer.Handle != blocker.Driver.Handle);
                bool driverThreateningPolice = IsDispatchVehicleSuspectThreateningActor(
                        subject, Game.Player.Character)
                    || _units.Where(unit => IsUsable(unit))
                        .SelectMany(unit => unit.Officers)
                        .Any(officer => officer != null
                            && officer.Exists() && !officer.IsDead
                            && IsDispatchVehicleSuspectThreateningActor(subject, officer));
                if (!blocker.NpcTireShotIssued
                    && !IsDispatchVehicleTireInterventionEligible(
                        exactOccurrenceTarget, subject.IsInVehicle(),
                        blocker.VehiclePursuitBlocker,
                        blocker.PursuitBlockerHolding,
                        blocker.NpcBlockerFallbackToTrail,
                        shooterAvailable, driverThreateningPolice,
                        Math.Abs(blocker.Vehicle.Speed), targetSpeed,
                        targetDistance, targetAhead))
                    return;
            }
            else if (!blocker.NpcTireShotIssued
                && (targetSpeed <= NpcVehicleStoppedSpeed
                || targetSpeed > NpcVehicleTireShotMaximumSpeed
                || targetDistance < NpcVehicleTireShotMinimumRange
                || targetDistance > NpcVehicleTireShotMaximumRange
                || targetAhead > -3.0f))
                return;

            blocker.NextNpcTireActionAt = now.AddMilliseconds(
                NpcVehicleTireOpportunityMilliseconds);

            Ped shooter = blocker.NpcTireShooter;
            if (shooter == null || !shooter.Exists() || shooter.IsDead)
            {
                shooter = blocker.Officers.FirstOrDefault(officer =>
                    officer != null && officer.Exists() && !officer.IsDead
                    && officer.Handle != blocker.Driver.Handle);
                blocker.NpcTireShooter = shooter;
            }
            if (shooter == null || !shooter.Exists() || shooter.IsDead)
            {
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "NoLivePassengerOfficer", now, dispatchOwned);
                return;
            }

            if (!blocker.NpcTireShooterExitRequested)
            {
                if (!shooter.IsInVehicle()
                    || shooter.CurrentVehicle == null
                    || !shooter.CurrentVehicle.Exists()
                    || shooter.CurrentVehicle.Handle != blocker.Vehicle.Handle)
                {
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "PassengerWasNotSeatedInInterceptor", now,
                        dispatchOwned);
                    return;
                }

                Vector3 preferred = blocker.Vehicle.Position
                    + blocker.Vehicle.RightVector * NpcVehicleTireShooterSideOffset;
                Vector3 safePosition;
                bool safePositionFound = false;
                try
                {
                    safePositionFound = World.GetSafePositionForPed(
                        preferred, out safePosition, (GetSafePositionFlags)0);
                }
                catch (Exception ex)
                {
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "SafeGroundQueryFailed", now, dispatchOwned);
                    LogException("POLICE_BACKUP_NPC_TIRE_SHOOTER_POSITION_FAILED", ex);
                    return;
                }

                if (!safePositionFound
                    || safePosition.DistanceTo(preferred) > 2.0f
                    || safePosition.DistanceTo(blocker.Vehicle.Position) < 2.5f
                    || safePosition.DistanceTo(blocker.Vehicle.Position) > 7.0f
                    || Math.Abs(safePosition.Z - blocker.Vehicle.Position.Z) > 1.25f)
                {
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "NoBoundedSafePassengerSidePosition", now,
                        dispatchOwned);
                    return;
                }

                try
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE,
                        shooter, blocker.Vehicle, 0);
                    blocker.NpcTireShooterSafePosition = safePosition;
                    blocker.NpcTireShooterExitRequested = true;
                    blocker.NpcTireShooterExitAt = now;
                    LogRuntime("POLICE_BACKUP_" + (dispatchOwned ? "DISPATCH" : "NPC")
                        + "_TIRE_SHOOTER_EXIT_REQUESTED",
                        "Operation=" + _operationId
                        + "; Ped=" + subject.Handle
                        + "; TargetVehicle=" + subjectVehicle.Handle
                        + "; BackupVehicle=" + blocker.Vehicle.Handle
                        + "; Officer=" + shooter.Handle
                        + "; TargetSpeed=" + targetSpeed.ToString("0.0")
                        + "; TargetDistance=" + targetDistance.ToString("0.0")
                        + "; SafePosition=" + safePosition);
                }
                catch (Exception ex)
                {
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "PassengerExitTaskFailed", now, dispatchOwned);
                    LogException("POLICE_BACKUP_NPC_TIRE_SHOOTER_EXIT_FAILED", ex);
                }
                return;
            }

            if (shooter.IsInVehicle())
            {
                if (now >= blocker.NpcTireShooterExitAt.AddSeconds(4))
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "PassengerExitNotConfirmed", now, dispatchOwned);
                return;
            }

            if (!blocker.NpcTireShooterPositionTaskIssued)
            {
                if (shooter.Position.DistanceTo(
                        blocker.NpcTireShooterSafePosition) > 1.5f)
                {
                    try
                    {
                        Vector3 safe = blocker.NpcTireShooterSafePosition;
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD,
                            shooter, safe.X, safe.Y, safe.Z,
                            1.0f, 3500, 0.0f, 0.25f);
                        blocker.NpcTireShooterPositionTaskIssued = true;
                    }
                    catch (Exception ex)
                    {
                        ResolveNpcTireInterventionUnavailable(blocker, subject,
                            subjectVehicle, "PassengerCoverMoveTaskFailed", now,
                            dispatchOwned);
                        LogException("POLICE_BACKUP_NPC_TIRE_SHOOTER_MOVE_FAILED", ex);
                    }
                    return;
                }
                blocker.NpcTireShooterPositionTaskIssued = true;
            }

            if (shooter.Position.DistanceTo(blocker.NpcTireShooterSafePosition) > 2.0f)
            {
                if (now >= blocker.NpcTireShooterExitAt.AddSeconds(4))
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "PassengerDidNotReachSafePosition", now,
                        dispatchOwned);
                return;
            }

            if (!blocker.NpcTireShotIssued)
            {
                Vector3 tirePosition;
                string tireBone;
                if (!TryResolveVisibleNpcTire(shooter, subjectVehicle,
                        out tirePosition, out tireBone)
                    || !IsNpcTireShotLaneClear(shooter, subjectVehicle,
                        tirePosition))
                {
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "NoVisibleSafeTireShotLane", now, dispatchOwned);
                    return;
                }

                int firearm = 0;
                bool armed = false;
                try
                {
                    armed = Function.Call<bool>(Hash.IS_PED_ARMED, shooter, 4);
                    firearm = Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, shooter);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_BACKUP_NPC_TIRE_SHOOTER_WEAPON_READ_FAILED", ex);
                }
                if (!armed || firearm == 0)
                {
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "PassengerHasNoSelectedFirearm", now,
                        dispatchOwned);
                    return;
                }

                blocker.NpcTireBurstBaselineMask = ReadNpcVehicleTireBurstMask(
                    subjectVehicle);
                blocker.NpcTireShotAt = now;
                blocker.NextNpcTireResultSampleAt = now;
                blocker.NpcTireShotIssued = true;
                int singleShotPattern = unchecked((int)StringHash.AtStringHash(
                    "FIRING_PATTERN_SINGLE_SHOT", 0));
                try
                {
                    Function.Call(Hash.TASK_SHOOT_AT_COORD,
                        shooter, tirePosition.X, tirePosition.Y, tirePosition.Z,
                        NpcVehicleTireShotDurationMilliseconds, singleShotPattern);
                    LogRuntime("POLICE_BACKUP_" + (dispatchOwned ? "DISPATCH" : "NPC")
                        + "_TIRE_SHOT_ISSUED",
                        "Operation=" + _operationId
                        + "; Ped=" + subject.Handle
                        + "; TargetVehicle=" + subjectVehicle.Handle
                        + "; BackupVehicle=" + blocker.Vehicle.Handle
                        + "; Officer=" + shooter.Handle
                        + "; Weapon=" + firearm
                        + "; TireBone=" + tireBone
                        + "; TirePosition=" + tirePosition
                        + "; TargetSpeed=" + targetSpeed.ToString("0.0")
                        + "; TargetDistance=" + targetDistance.ToString("0.0")
                        + "; LineOfSight=true; InVehicle=false; Pattern=SingleShot");
                }
                catch (Exception ex)
                {
                    ResolveNpcTireInterventionUnavailable(blocker, subject,
                        subjectVehicle, "TireShotTaskFailed", now, dispatchOwned);
                    LogException("POLICE_BACKUP_NPC_TIRE_SHOT_FAILED", ex);
                }
                return;
            }

            if (blocker.NpcTireShotResolved
                || now < blocker.NextNpcTireResultSampleAt)
                return;
            blocker.NextNpcTireResultSampleAt = now.AddMilliseconds(
                NpcVehicleTireResultSampleMilliseconds);
            bool newTireBurstObserved;
            bool confirmed = npcResponse != null
                ? npcResponse.ReportBackupTrafficVehicleTireIntervention(
                    subjectVehicle, shooter, blocker.NpcTireBurstBaselineMask,
                    blocker.NpcTireShotAt, now, out newTireBurstObserved)
                : ConfirmDispatchVehicleTireBurst(
                    dispatch, subject, subjectVehicle, shooter, blocker, now,
                    out newTireBurstObserved);
            if (newTireBurstObserved)
            {
                blocker.NpcTireShotResolved = true;
                if (!confirmed)
                {
                    LogRuntime("POLICE_BACKUP_" + (dispatchOwned ? "DISPATCH" : "NPC")
                        + "_TIRE_SHOT_UNATTRIBUTED",
                        "Operation=" + _operationId
                        + "; Ped=" + subject.Handle
                        + "; TargetVehicle=" + subjectVehicle.Handle
                        + "; Officer=" + shooter.Handle
                        + "; Reason=NewBurstWithoutCorroboratedShotAttribution");
                    RecoverNpcTireShooter(blocker, "TireBurstAttributionUnclear",
                        dispatchOwned);
                }
                else if (dispatchOwned)
                {
                    LogRuntime("POLICE_BACKUP_DISPATCH_TIRE_INTERVENTION_CONFIRMED",
                        "Incident=" + dispatch.Current.Id
                        + "; Suspect=" + subject.Handle
                        + "; TargetVehicle=" + subjectVehicle.Handle
                        + "; Officer=" + shooter.Handle
                        + "; TireBurstMaskBefore=" + blocker.NpcTireBurstBaselineMask
                        + "; TireBurstMaskAfter="
                        + ReadNpcVehicleTireBurstMask(subjectVehicle)
                        + "; Attribution=BackupSingleShotAndNewTireBurst"
                        + "; DriverBodyTargeted=false; PursuitContinues=true");
                    RecoverNpcTireShooter(blocker, "DispatchTireShotConfirmed", true);
                }
                return;
            }

            if (now > blocker.NpcTireShotAt.AddMilliseconds(
                    NpcVehicleTireShotVerifyMilliseconds))
            {
                blocker.NpcTireShotResolved = true;
                LogRuntime("POLICE_BACKUP_" + (dispatchOwned ? "DISPATCH" : "NPC")
                    + "_TIRE_SHOT_NO_BURST",
                    "Operation=" + _operationId
                    + "; Ped=" + subject.Handle
                    + "; TargetVehicle=" + subjectVehicle.Handle
                    + "; Officer=" + shooter.Handle
                    + "; Attempts=1; PursuitContinues=true");
                RecoverNpcTireShooter(blocker, "TireShotNoBurst", dispatchOwned);
            }
        }

        private static bool TryResolveVisibleNpcTire(
            Ped shooter,
            Vehicle vehicle,
            out Vector3 position,
            out string boneName)
        {
            position = Vector3.Zero;
            boneName = string.Empty;
            if (shooter == null || !shooter.Exists() || shooter.IsDead
                || vehicle == null || !vehicle.Exists())
                return false;

            string[] candidates =
            {
                "wheel_lf", "wheel_rf", "wheel_lr", "wheel_rr",
                "wheel_lm1", "wheel_rm1", "wheel_lm2", "wheel_rm2",
                "wheel_lm3", "wheel_rm3"
            };
            float nearest = float.MaxValue;
            foreach (string candidate in candidates)
            {
                try
                {
                    int boneIndex = Function.Call<int>(
                        Hash.GET_ENTITY_BONE_INDEX_BY_NAME, vehicle, candidate);
                    if (boneIndex < 0)
                        continue;
                    Vector3 tire = Function.Call<Vector3>(
                        Hash.GET_WORLD_POSITION_OF_ENTITY_BONE,
                        vehicle, boneIndex);
                    float distance = shooter.Position.DistanceTo(tire);
                    if (distance < NpcVehicleTireShotMinimumRange
                        || distance > NpcVehicleTireShotMaximumRange
                        || Math.Abs(tire.Z - shooter.Position.Z) > 2.0f
                        || distance >= nearest)
                        continue;
                    nearest = distance;
                    position = tire;
                    boneName = candidate;
                }
                catch
                {
                    // Optional wheel bones are model-dependent. Keep trying
                    // other documented wheel names; callers simply skip fire
                    // if no safe target point resolves.
                }
            }
            if (string.IsNullOrEmpty(boneName))
                return false;

            try
            {
                return Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                    shooter, vehicle, 17);
            }
            catch { return false; }
        }

        private bool IsNpcTireShotLaneClear(
            Ped shooter,
            Vehicle targetVehicle,
            Vector3 tirePosition)
        {
            Vector3 origin = shooter.Position;
            Ped player = Game.Player.Character;
            if (player != null && player.Exists() && !player.IsDead
                && DistanceToNpcShotSegment(player.Position, origin, tirePosition)
                    < NpcVehicleTireShotLaneClearance)
                return false;

            foreach (BackupUnit unit in _units)
            {
                if (!IsUsable(unit))
                    continue;
                foreach (Ped officer in unit.Officers
                    .Where(candidate => candidate != null && candidate.Exists()
                        && !candidate.IsDead && candidate.Handle != shooter.Handle))
                    if (DistanceToNpcShotSegment(officer.Position,
                            origin, tirePosition)
                        < NpcVehicleTireShotLaneClearance)
                        return false;
            }
            return targetVehicle != null && targetVehicle.Exists();
        }

        private static float DistanceToNpcShotSegment(
            Vector3 point,
            Vector3 start,
            Vector3 end)
        {
            Vector3 segment = end - start;
            float lengthSquared = segment.X * segment.X
                + segment.Y * segment.Y + segment.Z * segment.Z;
            if (lengthSquared <= 0.0001f)
                return point.DistanceTo(start);
            Vector3 fromStart = point - start;
            float amount = (fromStart.X * segment.X
                + fromStart.Y * segment.Y + fromStart.Z * segment.Z)
                / lengthSquared;
            amount = Math.Max(0.0f, Math.Min(1.0f, amount));
            return point.DistanceTo(start + segment * amount);
        }

        private static int ReadNpcVehicleTireBurstMask(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
                return 0;
            int mask = 0;
            for (int tire = 0; tire < 8; tire++)
            {
                try
                {
                    if (Function.Call<bool>(Hash.IS_VEHICLE_TYRE_BURST,
                        vehicle, tire, false))
                        mask |= 1 << tire;
                }
                catch { }
            }
            return mask;
        }

        private bool ConfirmDispatchVehicleTireBurst(
            LSPDDispatch dispatch,
            Ped suspect,
            Vehicle vehicle,
            Ped shooter,
            BackupUnit blocker,
            DateTime now,
            out bool newTireBurstObserved)
        {
            newTireBurstObserved = false;
            if (suspect == null || !suspect.Exists()
                || vehicle == null || !vehicle.Exists()
                || shooter == null || !shooter.Exists()
                || blocker == null || blocker.NpcTireShotAt == DateTime.MinValue
                || now < blocker.NpcTireShotAt
                || now > blocker.NpcTireShotAt.AddMilliseconds(
                    NpcVehicleTireShotVerifyMilliseconds))
                return false;

            int currentMask = ReadNpcVehicleTireBurstMask(vehicle);
            int newMask = currentMask & ~blocker.NpcTireBurstBaselineMask;
            if (newMask == 0)
                return false;
            newTireBurstObserved = true;

            bool shooterShooting = false;
            bool shooterDamageRecord = false;
            bool playerShooting = false;
            try
            {
                shooterShooting = shooter.IsShooting;
                shooterDamageRecord = Function.Call<bool>(
                    Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, vehicle, shooter);
                Ped player = Game.Player.Character;
                playerShooting = player != null && player.Exists()
                    && !player.IsDead && player.IsShooting;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_TIRE_ATTRIBUTION_READ_FAILED", ex);
                return false;
            }

            int tireIndex = 0;
            while (tireIndex < 8 && (newMask & (1 << tireIndex)) == 0)
                tireIndex++;
            if (!shooterShooting || !shooterDamageRecord || playerShooting)
            {
                LogRuntime("POLICE_BACKUP_DISPATCH_TIRE_BURST_UNVERIFIED",
                    "Incident=" + (dispatch == null || dispatch.Current == null
                        ? string.Empty : dispatch.Current.Id)
                    + "; Suspect=" + suspect.Handle
                    + "; TargetVehicle=" + vehicle.Handle
                    + "; Officer=" + shooter.Handle
                    + "; Tire=" + tireIndex
                    + "; OfficerShooting=" + shooterShooting
                    + "; OfficerDamageRecord=" + shooterDamageRecord
                    + "; PlayerShooting=" + playerShooting
                    + "; StopCauseAccepted=false");
                return false;
            }
            return true;
        }

        private void ResolveNpcTireInterventionUnavailable(
            BackupUnit unit,
            Ped subject,
            Vehicle subjectVehicle,
            string reason,
            DateTime now,
            bool dispatchOwned = false)
        {
            if (unit == null || unit.NpcTireShotResolved)
                return;
            unit.NpcTireShotResolved = true;
            LogRuntime("POLICE_BACKUP_" + (dispatchOwned ? "DISPATCH" : "NPC")
                + "_TIRE_SHOT_SKIPPED",
                "Operation=" + _operationId
                + "; Ped=" + (subject == null ? 0 : subject.Handle)
                + "; TargetVehicle=" + (subjectVehicle == null
                    ? 0 : subjectVehicle.Handle)
                + "; Unit=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                + "; Officer=" + (unit.NpcTireShooter == null
                    ? 0 : unit.NpcTireShooter.Handle)
                + "; Reason=" + reason
                + "; PursuitContinues=true");
            if (unit.NpcTireShooterExitRequested
                && unit.NpcTireShooter != null
                && unit.NpcTireShooter.Exists()
                && !unit.NpcTireShooter.IsDead
                && !unit.NpcTireShooter.IsInVehicle())
                RecoverNpcTireShooter(unit, "TireShotSkipped", dispatchOwned);
        }

        private void RecoverNpcTireShooter(
            BackupUnit unit,
            string reason,
            bool dispatchOwned = false)
        {
            if (unit == null || !IsUsable(unit) || unit.NpcTireShooter == null
                || !unit.NpcTireShooter.Exists() || unit.NpcTireShooter.IsDead
                || unit.NpcTireShooter.IsInVehicle())
                return;
            try
            {
                Function.Call(Hash.TASK_ENTER_VEHICLE,
                    unit.NpcTireShooter, unit.Vehicle, 5000, 0, 1.0f, 1, 0);
                LogRuntime("POLICE_BACKUP_" + (dispatchOwned ? "DISPATCH" : "NPC")
                    + "_TIRE_SHOOTER_RETURN_REQUESTED",
                    "Operation=" + _operationId
                    + "; Officer=" + unit.NpcTireShooter.Handle
                    + "; BackupVehicle=" + unit.Vehicle.Handle
                    + "; Seat=FrontPassenger; Reason=" + reason);
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_TIRE_SHOOTER_RETURN_FAILED", ex);
            }
        }

        private static void ResetNpcTireIntervention(
            BackupUnit unit,
            int targetVehicleHandle)
        {
            if (unit == null)
                return;
            unit.NpcTireTargetVehicleHandle = targetVehicleHandle;
            unit.NpcTireShooter = null;
            unit.NpcTireShooterSafePosition = Vector3.Zero;
            unit.NpcTireShooterExitRequested = false;
            unit.NpcTireShooterPositionTaskIssued = false;
            unit.NpcTireShotIssued = false;
            unit.NpcTireShotResolved = false;
            unit.NpcTireBurstBaselineMask = 0;
            unit.NpcTireShotAt = DateTime.MinValue;
            unit.NextNpcTireActionAt = DateTime.MinValue;
            unit.NextNpcTireResultSampleAt = DateTime.MinValue;
            unit.NpcTireShooterExitAt = DateTime.MinValue;
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

        private void ObserveNpcBackupOfficerState(
            LSPDNPCResponse npcResponse,
            DateTime now)
        {
            if (now < _nextNpcOfficerEvidenceAt)
                return;
            _nextNpcOfficerEvidenceAt = now.AddMilliseconds(
                NpcOfficerEvidenceSampleMilliseconds);

            foreach (BackupUnit unit in _units.Take(2))
            {
                if (!IsUsable(unit))
                    continue;
                foreach (Ped officer in unit.Officers
                    .Where(candidate => candidate != null && candidate.Exists()
                        && !candidate.IsDead)
                    .GroupBy(candidate => candidate.Handle)
                    .Select(group => group.First())
                    .Take(2))
                {
                    try
                    {
                        int weapon = Function.Call<int>(
                            Hash.GET_SELECTED_PED_WEAPON, officer);
                        int combatTarget = Function.Call<int>(
                            Hash.GET_PED_TARGET_FROM_COMBAT_PED, officer, true);
                        bool shooting = officer.IsShooting;
                        bool inCombat = officer.IsInCombat;
                        bool inVehicle = officer.IsInVehicle();
                        int officerVehicle = inVehicle && officer.CurrentVehicle != null
                            && officer.CurrentVehicle.Exists()
                            ? officer.CurrentVehicle.Handle : 0;
                        string role = officer.Handle == unit.Driver.Handle
                            ? "Driver"
                            : officer.Handle == (_npcEscortOfficer == null
                                ? 0 : _npcEscortOfficer.Handle)
                                ? "CustodyEscort" : "Cover";
                        string ownerTask = ResolveNpcOfficerOwnerTask(npcResponse);
                        string signature = weapon + "|" + shooting + "|" + inCombat
                            + "|" + combatTarget + "|" + inVehicle + "|"
                            + officerVehicle + "|" + role + "|" + ownerTask;
                        string prior;
                        if (_npcOfficerEvidenceSignatures.TryGetValue(
                                officer.Handle, out prior)
                            && string.Equals(prior, signature,
                                StringComparison.Ordinal))
                            continue;

                        _npcOfficerEvidenceSignatures[officer.Handle] = signature;
                        LogRuntime("POLICE_BACKUP_NPC_OFFICER_WEAPON_STATE",
                            "Operation=" + _operationId
                            + "; Ped=" + (npcResponse == null
                                || npcResponse.ActiveSubject == null
                                ? 0 : npcResponse.ActiveSubject.Handle)
                            + "; Unit=" + unit.Vehicle.Handle
                            + "; Officer=" + officer.Handle
                            + "; Role=" + role
                            + "; Weapon=" + weapon
                            + "; Shooting=" + shooting
                            + "; InCombat=" + inCombat
                            + "; CombatTarget=" + combatTarget
                            + "; InVehicle=" + inVehicle
                            + "; OfficerVehicle=" + officerVehicle
                            + "; OwnerTask=" + ownerTask);
                    }
                    catch (Exception ex)
                    {
                        string prior;
                        if (!_npcOfficerEvidenceSignatures.TryGetValue(
                                officer.Handle, out prior)
                            || prior != "EvidenceReadFailed")
                        {
                            _npcOfficerEvidenceSignatures[officer.Handle] =
                                "EvidenceReadFailed";
                            LogException(
                                "POLICE_BACKUP_NPC_OFFICER_WEAPON_STATE_FAILED", ex);
                        }
                    }
                }
            }
        }

        private string ResolveNpcOfficerOwnerTask(LSPDNPCResponse npcResponse)
        {
            if (_npcTransportStarted)
                return "BackupCustodyDeparture";
            if (npcResponse != null && npcResponse.IsBackupPrisonerLoading)
                return "BackupPrisonerLoading";
            if (npcResponse != null && npcResponse.IsBackupEscortMovementActive)
                return "BackupPhysicalEscort";
            if (npcResponse != null && (npcResponse.IsBackupCustodyActionActive
                || npcResponse.IsPlayerCustodyActionActive))
                return "CustodyAction";
            if (npcResponse != null && npcResponse.IsBackupContainmentAwaitingPlayer)
                return "CitizenContainmentHold";
            return _npcInterventionStarted
                ? "PhysicalContainment" : "CitizenPursuit";
        }

        private void StopNpcOfficerWeaponTasks(
            LSPDNPCResponse npcResponse,
            string reason,
            bool clearPursuitTasks)
        {
            Ped subject = npcResponse == null ? null : npcResponse.ActiveSubject;
            foreach (BackupUnit unit in _units.Take(2))
            {
                if (!IsUsable(unit))
                    continue;
                foreach (Ped officer in unit.Officers
                    .Where(candidate => candidate != null && candidate.Exists()
                        && !candidate.IsDead)
                    .GroupBy(candidate => candidate.Handle)
                    .Select(group => group.First())
                    .Take(2))
                {
                    try
                    {
                        int target = Function.Call<int>(
                            Hash.GET_PED_TARGET_FROM_COMBAT_PED, officer, true);
                        bool shooting = officer.IsShooting;
                        bool targetingSubject = subject != null && subject.Exists()
                            && (target == subject.Handle
                                || officer.IsInCombatAgainst(subject));
                        Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, officer);
                        if (clearPursuitTasks || shooting || targetingSubject)
                            officer.Task.ClearAll();
                        LogRuntime("POLICE_BACKUP_NPC_WEAPON_TASKS_STOPPED",
                            "Operation=" + _operationId
                            + "; Ped=" + (subject == null ? 0 : subject.Handle)
                            + "; Unit=" + unit.Vehicle.Handle
                            + "; Officer=" + officer.Handle
                            + "; ShootingBefore=" + shooting
                            + "; CombatTargetBefore=" + target
                            + "; ClearedPrimary="
                            + (clearPursuitTasks || shooting || targetingSubject)
                            + "; Reason=" + reason);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_NPC_WEAPON_TASK_CLEAR_FAILED", ex);
                    }
                }
            }
        }

        private bool IsNpcPedInVehicleSeat(
            Vehicle vehicle,
            Ped ped,
            int seatIndex)
        {
            if (vehicle == null || !vehicle.Exists()
                || ped == null || !ped.Exists()
                || ped.IsDead)
                return false;
            try
            {
                return Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT,
                    vehicle, seatIndex, false) == ped.Handle;
            }
            catch (Exception ex)
            {
                if (!_npcSeatNativeFailureLogged)
                {
                    _npcSeatNativeFailureLogged = true;
                    LogException("POLICE_BACKUP_NPC_CUSTODY_SEAT_CHECK_FAILED", ex);
                }
                return false;
            }
        }

        private bool MaintainNpcCustodySeatReadiness(
            LSPDNPCResponse npcResponse,
            BackupUnit unit,
            Ped subject,
            DateTime now)
        {
            if (!IsUsable(unit) || subject == null || !subject.Exists()
                || subject.IsDead
                || !npcResponse.IsLoadedInBackupVehicle)
            {
                _npcCustodySeatsStableSince = DateTime.MinValue;
                return false;
            }

            bool driverSeated = IsNpcPedInVehicleSeat(
                unit.Vehicle, unit.Driver, -1);
            bool citizenSeated = IsNpcPedInVehicleSeat(
                unit.Vehicle, subject, 1);
            bool passengerRequired = _npcEscortOfficer != null
                && _npcEscortOfficer.Exists() && !_npcEscortOfficer.IsDead
                && _npcEscortOfficer.Handle != unit.Driver.Handle;
            bool passengerSeated = !passengerRequired
                || IsNpcPedInVehicleSeat(unit.Vehicle, _npcEscortOfficer, 0);

            if (driverSeated && citizenSeated && passengerSeated)
            {
                if (_npcCustodySeatsStableSince == DateTime.MinValue)
                {
                    _npcCustodySeatsStableSince = now;
                    _npcCustodySeatEntryAttempts = 0;
                    _npcCustodySeatRetryNotified = false;
                    _log.Runtime("POLICE_BACKUP_NPC_CUSTODY_SEATS_STABILITY_STARTED",
                        "Operation=" + _operationId
                        + "; Citizen=" + subject.Handle
                        + "; Driver=" + unit.Driver.Handle
                        + "; Passenger=" + (passengerRequired
                            ? _npcEscortOfficer.Handle : 0)
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; RequiredStableMilliseconds="
                        + NpcCustodySeatStableMilliseconds);
                }

                if (now < _npcCustodySeatsStableSince.AddMilliseconds(
                        NpcCustodySeatStableMilliseconds))
                    return false;

                if (!_npcCustodySeatsConfirmedLogged)
                {
                    _npcCustodySeatsConfirmedLogged = true;
                    _log.Runtime("POLICE_BACKUP_NPC_CUSTODY_SEATS_STABLE",
                        "Operation=" + _operationId
                        + "; Citizen=" + subject.Handle
                        + "; CitizenSeat=LeftRear:1"
                        + "; Driver=" + unit.Driver.Handle
                        + "; DriverSeat=-1"
                        + "; Passenger=" + (passengerRequired
                            ? _npcEscortOfficer.Handle : 0)
                        + "; PassengerSeat=" + (passengerRequired ? 0 : -2)
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; StableMilliseconds="
                        + NpcCustodySeatStableMilliseconds);
                }
                return true;
            }

            _npcCustodySeatsStableSince = DateTime.MinValue;
            _npcCustodySeatsConfirmedLogged = false;
            if (now < _nextNpcOfficerTaskAt
                || _npcCustodySeatEntryAttempts >= NpcCustodySeatMaximumEntryAttempts)
            {
                if (_npcCustodySeatEntryAttempts >= NpcCustodySeatMaximumEntryAttempts
                    && !_npcCustodySeatRetryNotified)
                {
                    _npcCustodySeatRetryNotified = true;
                    _log.Runtime("POLICE_BACKUP_NPC_CUSTODY_SEATS_NOT_STABLE",
                        "Operation=" + _operationId
                        + "; Citizen=" + subject.Handle
                        + "; DriverSeated=" + driverSeated
                        + "; CitizenSeated=" + citizenSeated
                        + "; PassengerRequired=" + passengerRequired
                        + "; PassengerSeated=" + passengerSeated
                        + "; Attempts=" + _npcCustodySeatEntryAttempts
                        + "; DepartureStarted=false");
                    Notify("The Citizen is secure, but Backup will wait until the Citizen, driver, and escort officer are confirmed in their assigned seats.");
                }
                return false;
            }

            try
            {
                if (!driverSeated)
                {
                    bool driverInUnitVehicle = unit.Driver.IsInVehicle()
                        && unit.Driver.CurrentVehicle != null
                        && unit.Driver.CurrentVehicle.Exists()
                        && unit.Driver.CurrentVehicle.Handle == unit.Vehicle.Handle;
                    if (driverInUnitVehicle)
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            unit.Driver, unit.Vehicle, 0);
                    else
                        unit.Driver.Task.EnterVehicle(
                            unit.Vehicle, VehicleSeat.Driver, 12000, 1.0f);
                }
                else if (passengerRequired && !passengerSeated)
                {
                    bool passengerInUnitVehicle = _npcEscortOfficer.IsInVehicle()
                        && _npcEscortOfficer.CurrentVehicle != null
                        && _npcEscortOfficer.CurrentVehicle.Exists()
                        && _npcEscortOfficer.CurrentVehicle.Handle == unit.Vehicle.Handle;
                    if (passengerInUnitVehicle)
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            _npcEscortOfficer, unit.Vehicle, 0);
                    else
                        _npcEscortOfficer.Task.EnterVehicle(
                            unit.Vehicle, VehicleSeat.RightFront, 10000, 1.0f);
                }
                _npcCustodySeatEntryAttempts++;
                _nextNpcOfficerTaskAt = now.AddMilliseconds(
                    NpcCustodySeatRetryMilliseconds);
                _log.Runtime("POLICE_BACKUP_NPC_CUSTODY_SEAT_TASK_ISSUED",
                    "Operation=" + _operationId
                    + "; Citizen=" + subject.Handle
                    + "; Attempt=" + _npcCustodySeatEntryAttempts
                    + "; DriverSeated=" + driverSeated
                    + "; CitizenSeated=" + citizenSeated
                    + "; PassengerRequired=" + passengerRequired
                    + "; PassengerSeated=" + passengerSeated);
            }
            catch (Exception ex)
            {
                _nextNpcOfficerTaskAt = now.AddMilliseconds(
                    NpcCustodySeatRetryMilliseconds);
                LogException("POLICE_BACKUP_NPC_CUSTODY_SEAT_TASK_FAILED", ex);
            }
            return false;
        }

        private void SupportNpcContact(LSPDNPCResponse npcResponse)
        {
            if (npcResponse == null || !npcResponse.HasBackupAssignment)
            {
                BeginStandDown("The linked NPC contact finished.");
                return;
            }

            ObserveNpcBackupOfficerState(npcResponse, DateTime.UtcNow);

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
                if (!_npcCustodyWeaponTasksStopped)
                {
                    StopNpcOfficerWeaponTasks(
                        npcResponse, "CustodyTransportStarted", false);
                    _npcCustodyWeaponTasksStopped = true;
                }
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

            bool custodyMovementActive = npcResponse.IsBackupPrisonerLoading
                || npcResponse.IsBackupEscortMovementActive
                || npcResponse.IsBackupCustodyActionActive
                || npcResponse.IsPlayerCustodyActionActive;
            if (custodyMovementActive && !_npcCustodyWeaponTasksStopped)
            {
                StopNpcOfficerWeaponTasks(npcResponse,
                    "CustodyEscortOrLoadingStarted", false);
                _npcCustodyWeaponTasksStopped = true;
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
            if (_npcInterventionStarted && _npcTargetWasFleeing
                && subjectVehicle != null && subjectVehicle.Exists()
                && subject.IsInVehicle())
            {
                DateTime interventionSampleAt = DateTime.UtcNow;
                foreach (BackupUnit pursuitUnit in _units.Take(2))
                {
                    if (!IsUsable(pursuitUnit))
                        continue;
                    string stopEvidence = ResolveNpcVehicleStopEvidence(
                        subjectVehicle, pursuitUnit.Vehicle);
                    if (!string.IsNullOrEmpty(stopEvidence))
                        npcResponse.ReportBackupTrafficVehicleIntervention(
                            subjectVehicle, pursuitUnit.Vehicle,
                            stopEvidence, interventionSampleAt);
                }
            }
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
                    npcResponse, unit, subject, subjectVehicle, DateTime.UtcNow))
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
                    if (!_npcPursuitWeaponTasksStopped)
                    {
                        StopNpcOfficerWeaponTasks(
                            npcResponse, "VehicleStopContained", true);
                        _npcPursuitWeaponTasksStopped = true;
                    }
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
                if (_npcTargetWasFleeing && !_npcPursuitWeaponTasksStopped)
                {
                    StopNpcOfficerWeaponTasks(
                        npcResponse, "FleeingCitizenContained", true);
                    _npcPursuitWeaponTasksStopped = true;
                }
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
                if (_npcTargetWasFleeing && !_npcPursuitWeaponTasksStopped)
                {
                    StopNpcOfficerWeaponTasks(
                        npcResponse, "FleeingCitizenContained", true);
                    _npcPursuitWeaponTasksStopped = true;
                }
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
                        // BeginBackupIntervention has already issued the
                        // Citizen's surrender task. Keep the assigned officer
                        // visually attentive without maintaining an aim or
                        // combat task against a contained subject.
                        _npcEscortOfficer.Task.LookAt(subject, 4000);
                    }
                    catch (Exception ex) { LogException("POLICE_BACKUP_NPC_GUARD_FAILED", ex); }
                    _nextNpcOfficerTaskAt = DateTime.UtcNow.AddSeconds(6);
                }
                SetState(LSPDBackupAssignmentState.Securing);
                return;
            }

            // Departure belongs to the same three physical occupants for the
            // entire handoff. A vehicle handle alone is not proof that the
            // driver stayed in the driver seat or the escort officer stayed
            // in the front passenger seat.
            if (!MaintainNpcCustodySeatReadiness(
                    npcResponse, unit, subject, DateTime.UtcNow))
            {
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
                if (!EnsureNpcCitizenCustodyProtectionAdopted(unit, npcResponse))
                {
                    if (!unit.NpcCitizenCustodyAdoptionFailureLogged)
                    {
                        unit.NpcCitizenCustodyAdoptionFailureLogged = true;
                        LogRuntime("POLICE_BACKUP_NPC_CUSTODY_PROTECTION_ADOPTION_FAILED",
                            "Ped=" + (npcResponse.ActiveSubject == null
                                ? 0 : npcResponse.ActiveSubject.Handle)
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; DistanceFromScene=" + distanceFromScene.ToString("0.0")
                            + "; CitizenReleaseBlocked=true");
                    }
                    return;
                }

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

        private bool EnsureNpcCitizenCustodyProtectionAdopted(
            BackupUnit unit,
            LSPDNPCResponse npcResponse)
        {
            if (unit == null || npcResponse == null)
                return false;

            if (npcResponse.CitizenCustodyProtectionTransferred)
                return unit.NpcCitizenCustodyProtectionApplied
                    && unit.NpcCitizenCustodyPed != null
                    && npcResponse.ActiveSubject != null
                    && unit.NpcCitizenCustodyPed.Handle
                        == npcResponse.ActiveSubject.Handle;
            if (!npcResponse.CitizenCustodyProtectionTransferRequired)
                return true;

            Ped subject;
            bool originalInvincible;
            if (!npcResponse.TryPrepareBackupCustodyProtectionTransfer(
                unit.Vehicle, out subject, out originalInvincible)
                || subject == null || !subject.Exists())
                return false;

            if (unit.NpcCitizenCustodyProtectionApplied
                && unit.NpcCitizenCustodyPed != null
                && unit.NpcCitizenCustodyPed.Exists()
                && unit.NpcCitizenCustodyPed.Handle != subject.Handle)
                return false;

            try
            {
                subject.IsInvincible = true;
                unit.NpcCitizenCustodyPed = subject;
                unit.NpcCitizenCustodyOriginalInvincible = originalInvincible;
                unit.NpcCitizenCustodyProtectionApplied = true;
                if (!npcResponse.ConfirmBackupCustodyProtectionAdopted(subject))
                    return false;

                unit.NpcCitizenCustodyAdoptionFailureLogged = false;
                LogRuntime("POLICE_BACKUP_NPC_CUSTODY_PROTECTION_ADOPTED",
                    "Ped=" + subject.Handle
                    + "; Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                    + "; From=Citizen; To=Backup"
                    + "; AdoptedBeforeCitizenRelease=true; IsInvincible=true");
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_CUSTODY_PROTECTION_ADOPTION_FAILED", ex);
                return false;
            }
        }

        private void MaintainNpcCitizenCustodyProtection(
            BackupUnit unit,
            string phase)
        {
            if (unit == null || !unit.NpcCitizenCustodyProtectionApplied
                || unit.NpcCitizenCustodyPed == null
                || !unit.NpcCitizenCustodyPed.Exists()
                || unit.NpcCitizenCustodyPed.IsDead
                || unit.NpcCitizenCustodyPed.Health <= 0)
                return;
            try
            {
                if (!unit.NpcCitizenCustodyPed.IsInvincible)
                {
                    unit.NpcCitizenCustodyPed.IsInvincible = true;
                    LogRuntime("POLICE_BACKUP_NPC_CUSTODY_PROTECTION_REASSERTED",
                        "Ped=" + unit.NpcCitizenCustodyPed.Handle
                        + "; Phase=" + (phase ?? "Unknown")
                        + "; Reason=ExternalProtectionReset");
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_CUSTODY_PROTECTION_MAINTENANCE_FAILED", ex);
            }
        }

        private void ReleaseNpcCitizenCustodyProtection(
            BackupUnit unit,
            string reason)
        {
            if (unit == null || !unit.NpcCitizenCustodyProtectionApplied)
                return;
            int pedHandle = unit.NpcCitizenCustodyPed == null
                ? 0 : unit.NpcCitizenCustodyPed.Handle;
            bool originalInvincible = unit.NpcCitizenCustodyOriginalInvincible;
            try
            {
                if (unit.NpcCitizenCustodyPed != null
                    && unit.NpcCitizenCustodyPed.Exists())
                    unit.NpcCitizenCustodyPed.IsInvincible =
                        unit.NpcCitizenCustodyOriginalInvincible;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_NPC_CUSTODY_PROTECTION_RELEASE_FAILED", ex);
            }
            unit.NpcCitizenCustodyProtectionApplied = false;
            unit.NpcCitizenCustodyOriginalInvincible = false;
            unit.NpcCitizenCustodyPed = null;
            unit.NpcCitizenCustodyAdoptionFailureLogged = false;
            LogRuntime("POLICE_BACKUP_NPC_CUSTODY_PROTECTION_ENDED",
                "Ped=" + pedHandle
                + "; Reason=" + (reason ?? "Unknown")
                + "; RestoredBackupCapturedInvincible=" + originalInvincible);
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
            MaintainDispatchGroupSceneSupport(dispatch, player, now);
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
                // Keep the last validated staging/road point while the new
                // suspect route is resolved. An unresolved route is not a
                // valid (0,0,0) vehicle destination.
                unit.LastDriveTaskAt = DateTime.MinValue;
                unit.NextPursuitProgressCheckAt = now;
                unit.RouteRecoveryCount = 0;
                unit.PursuitRouteRecoveryExhausted = false;
                unit.PursuitRouteFailureLogged = false;
                unit.DispatchFootPursuitRoadFallbackActive = false;
                unit.DispatchFootPursuitFallbackRoadNode = Vector3.Zero;
                unit.NextDispatchFootPursuitFallbackTaskAt = DateTime.MinValue;
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
                if (target == null || !target.Exists() || target.IsDead)
                    continue;

                if (unit.DispatchFootPursuitRoadFallbackActive)
                {
                    MaintainDispatchFootPursuitRoadFallback(unit, target, now);
                    continue;
                }

                if (unit.PursuitRouteRecoveryExhausted
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
                            if (unit.RouteRecoveryCount < MaximumRouteRecoveries)
                                unit.RouteRecoveryCount++;
                            if (unit.RouteRecoveryCount >= MaximumRouteRecoveries)
                            {
                                if (TryBeginDispatchFootPursuitRoadFallback(
                                    unit, target, now))
                                    continue;

                                unit.PursuitRouteRecoveryExhausted = true;
                                if (!unit.PursuitRouteFailureLogged)
                                {
                                    unit.PursuitRouteFailureLogged = true;
                                    LogRuntime("POLICE_BACKUP_FOOT_ROUTE_RECOVERY_EXHAUSTED",
                                        "Suspect=" + target.Handle
                                        + "; BackupVehicle=" + unit.Vehicle.Handle
                                        + "; RecoveryAttempts=" + unit.RouteRecoveryCount
                                        + "; Reason=NO_VALID_TARGET_OR_NEARBY_ROAD_NODE"
                                        + "; SafeFootFallback=false"
                                        + "; LastValidDestination=" + unit.Destination);
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
                            if (TryBeginDispatchFootPursuitRoadFallback(
                                unit, target, now))
                                continue;

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

        private bool TryBeginDispatchFootPursuitRoadFallback(
            BackupUnit unit,
            Ped target,
            DateTime now)
        {
            if (!IsUsable(unit) || target == null || !target.Exists()
                || target.IsDead
                || unit.Vehicle.Position.DistanceTo(target.Position)
                    > DispatchFootRoadFallbackMaximumDistance)
                return false;

            Vector3 roadNode;
            float roadHeading;
            bool nearbyRoadFound = TryGetNearbyVehicleRoadNode(
                unit.Vehicle.Position,
                unit.Vehicle.Position,
                35f,
                out roadNode,
                out roadHeading);
            if (!nearbyRoadFound
                && IsValidBackupRoutePosition(unit.LastValidDriveDestination)
                && unit.LastValidDriveDestination.DistanceTo(unit.Vehicle.Position) <= 35f)
            {
                roadNode = unit.LastValidDriveDestination;
                roadHeading = unit.RouteHeading;
                nearbyRoadFound = true;
            }
            if (!nearbyRoadFound)
                return false;

            unit.DispatchFootPursuitRoadFallbackActive = true;
            unit.DispatchFootPursuitFallbackRoadNode = roadNode;
            unit.NextDispatchFootPursuitFallbackTaskAt = DateTime.MinValue;
            unit.PursuitRouteRecoveryExhausted = false;
            unit.Destination = roadNode;
            unit.RouteHeading = roadHeading;
            LogRuntime("POLICE_BACKUP_FOOT_NEAREST_ROAD_FALLBACK_SELECTED",
                "Suspect=" + target.Handle
                + "; BackupVehicle=" + unit.Vehicle.Handle
                + "; RoadNode=" + roadNode
                + "; VehicleDistance=" + unit.Vehicle.Position.DistanceTo(roadNode)
                    .ToString("0.0", CultureInfo.InvariantCulture)
                + "; SuspectDistance=" + unit.Vehicle.Position.DistanceTo(target.Position)
                    .ToString("0.0", CultureInfo.InvariantCulture)
                + "; RecoveryAttempts=" + unit.RouteRecoveryCount);
            MaintainDispatchFootPursuitRoadFallback(unit, target, now);
            return true;
        }

        private void MaintainDispatchFootPursuitRoadFallback(
            BackupUnit unit,
            Ped target,
            DateTime now)
        {
            if (!IsUsable(unit) || target == null || !target.Exists()
                || target.IsDead
                || unit.DispatchFootPursuitFallbackRoadNode == Vector3.Zero)
                return;

            if (unit.Arrived)
                return;

            float roadDistance = unit.Vehicle.Position.DistanceTo(
                unit.DispatchFootPursuitFallbackRoadNode);
            float arrivalDistance = Math.Max(
                DispatchFootRoadFallbackArrivalDistance,
                _settings.ArrivalRadius);
            if (roadDistance <= arrivalDistance)
            {
                unit.Arrived = true;
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
                    LogException("POLICE_BACKUP_FOOT_NEAREST_ROAD_HOLD_FAILED", ex);
                }
                LogRuntime("POLICE_BACKUP_FOOT_NEAREST_ROAD_REACHED",
                    "Suspect=" + target.Handle
                    + "; BackupVehicle=" + unit.Vehicle.Handle
                    + "; RoadNode=" + unit.DispatchFootPursuitFallbackRoadNode
                    + "; RoadDistance=" + roadDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; OfficersExit=true; FootInterceptionContinues=true");
                return;
            }

            if (now < unit.NextDispatchFootPursuitFallbackTaskAt)
                return;
            unit.NextDispatchFootPursuitFallbackTaskAt = now.AddSeconds(3);
            if (!SendTo(unit, unit.DispatchFootPursuitFallbackRoadNode, true))
                unit.NextDispatchFootPursuitFallbackTaskAt = now.AddSeconds(3);
        }

        private bool MaintainDispatchVehicleInterceptions(
            LSPDDispatch dispatch,
            IReadOnlyList<Ped> pursuitTargets,
            Ped player,
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
                dispatch, player, now);
            if (targets.Count == 0)
            {
                bool remainingFootSupport = _dispatchFootPursuitTargetCache.Count > 0
                    && MaintainDispatchFootPursuitApproach(
                        dispatch, player, now);
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
                    dispatch, player, target.Key, target.Value, now, group)
                    || hasAssignment;
            }

            bool footSupportActive = _dispatchFootPursuitTargetCache.Count > 0
                && MaintainDispatchFootPursuitApproach(
                    dispatch, player, now);
            return hasAssignment || sceneSupportActive || footSupportActive;
        }

        private void MaintainDispatchVehiclePursuitFireControl(
            LSPDDispatch dispatch,
            Ped player,
            Ped suspect,
            Vehicle suspectVehicle,
            IList<BackupUnit> assignedUnits)
        {
            if (suspect == null || !suspect.Exists() || assignedUnits == null)
                return;
            if (suspect.IsDead || !suspect.IsInVehicle()
                || suspectVehicle == null || !suspectVehicle.Exists())
            {
                CancelDispatchTireShotAfterVehiclePhase(
                    dispatch, suspect, suspectVehicle, assignedUnits,
                    suspect.IsDead ? "SuspectNoLongerLiving" : "DriverExitedVehicle");
                return;
            }

            bool suspectArmed = false;
            try { suspectArmed = Function.Call<bool>(Hash.IS_PED_ARMED, suspect, 7); }
            catch { }
            bool suspectFiring = suspect.IsShooting;
            bool suspectThreateningPlayer = suspectFiring
                && player != null && player.Exists()
                && suspect.IsInCombatAgainst(player);
            string incidentId = dispatch == null || dispatch.Current == null
                ? (_incident == null ? string.Empty : _incident.Id)
                : dispatch.Current.Id;

            foreach (BackupUnit unit in assignedUnits)
            {
                if (!IsUsable(unit))
                    continue;
                foreach (Ped officer in SupportOfficers(unit)
                    .Where(value => !value.IsDead)
                    .GroupBy(value => value.Handle)
                    .Select(group => group.First()))
                {
                    bool shooting = officer.IsShooting;
                    bool inCombat = officer.IsInCombat;
                    int combatTarget = 0;
                    if (shooting || inCombat)
                        try
                        {
                            combatTarget = Function.Call<int>(
                                Hash.GET_PED_TARGET_FROM_COMBAT_PED, officer, true);
                        }
                        catch { }

                    bool playerTarget = player != null && player.Exists()
                        && combatTarget == player.Handle;
                    bool suspectThreateningOfficer = suspectFiring
                        && IsDispatchVehicleSuspectThreateningActor(suspect, officer);
                    bool firingAuthorized = IsDispatchVehiclePursuitFireAuthorized(
                        unit.Arrived, suspect.IsInVehicle(), suspectArmed,
                        suspectFiring, suspectThreateningPlayer,
                        suspectThreateningOfficer,
                        combatTarget, suspect.Handle);
                    bool tireShooter = unit.NpcTireShooter != null
                        && unit.NpcTireShooter.Exists()
                        && unit.NpcTireShooter.Handle == officer.Handle
                        && unit.NpcTireTargetVehicleHandle == suspectVehicle.Handle;
                    long tireShotAgeMilliseconds = unit.NpcTireShotAt == DateTime.MinValue
                        ? -1L : (long)(DateTime.UtcNow
                            - unit.NpcTireShotAt).TotalMilliseconds;
                    bool authorizedTireTask = IsDispatchVehicleTireShotTaskAuthorized(
                        tireShooter, unit.NpcTireShotIssued,
                        unit.NpcTireShotResolved, tireShooter,
                        shooting, inCombat, combatTarget,
                        suspectThreateningPlayer || suspectThreateningOfficer,
                        tireShotAgeMilliseconds);
                    if (authorizedTireTask)
                    {
                        _dispatchVehiclePedFireCorrectionByOfficer.Remove(
                            officer.Handle);
                        _dispatchVehicleFireAuthorizationByOfficer.Remove(
                            officer.Handle);
                        continue;
                    }

                    if (firingAuthorized && tireShooter
                        && unit.NpcTireShotIssued && !unit.NpcTireShotResolved)
                        unit.NpcTireShotResolved = true;
                    bool unauthorizedPedCombat = (shooting || inCombat)
                        && (!firingAuthorized || playerTarget);
                    if (!unauthorizedPedCombat)
                    {
                        _dispatchVehiclePedFireCorrectionByOfficer.Remove(officer.Handle);
                        if (firingAuthorized && shooting)
                        {
                            string authorizationKey = suspect.Handle.ToString(
                                CultureInfo.InvariantCulture) + ":" + combatTarget.ToString(
                                CultureInfo.InvariantCulture);
                            string priorAuthorization;
                            if (!_dispatchVehicleFireAuthorizationByOfficer.TryGetValue(
                                    officer.Handle, out priorAuthorization)
                                || !string.Equals(priorAuthorization, authorizationKey,
                                    StringComparison.Ordinal))
                            {
                                _dispatchVehicleFireAuthorizationByOfficer[
                                    officer.Handle] = authorizationKey;
                                LogRuntime(
                                    "POLICE_BACKUP_DISPATCH_VEHICLE_FIRE_AUTHORIZED",
                                    "Incident=" + incidentId
                                    + "; Suspect=" + suspect.Handle
                                    + "; SuspectVehicle=" + suspectVehicle.Handle
                                    + "; Officer=" + officer.Handle
                                    + "; UnitVehicle=" + unit.Vehicle.Handle
                                    + "; UnitArrived=" + unit.Arrived
                                    + "; SuspectArmed=" + suspectArmed
                                    + "; SuspectShootingAtPlayer=" + suspectThreateningPlayer
                                    + "; SuspectShootingAtBackupOfficer="
                                    + suspectThreateningOfficer
                                    + "; TargetMode=Ped; Target=" + combatTarget
                                    + "; TireTarget=NotUsed; Authorization=ActiveLethalThreat");
                            }
                        }
                        else
                            _dispatchVehicleFireAuthorizationByOfficer.Remove(
                                officer.Handle);
                        continue;
                    }

                    _dispatchVehicleFireAuthorizationByOfficer.Remove(officer.Handle);

                    if (tireShooter && unit.NpcTireShotIssued
                        && !unit.NpcTireShotResolved)
                    {
                        unit.NpcTireShotResolved = true;
                        RecoverNpcTireShooter(unit,
                            "DispatchTireShotPreemptedByPedCombat", true);
                    }

                    int correctedSuspect;
                    if (_dispatchVehiclePedFireCorrectionByOfficer.TryGetValue(
                            officer.Handle, out correctedSuspect)
                        && correctedSuspect == suspect.Handle)
                        continue;

                    _dispatchVehiclePedFireCorrectionByOfficer[officer.Handle] =
                        suspect.Handle;
                    try { officer.Task.ClearAll(); }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_DISPATCH_VEHICLE_FIRE_TASK_CLEAR_FAILED", ex);
                    }
                    if (officer.Handle == unit.Driver.Handle
                        && !unit.VehicleContainmentStopRequested)
                    {
                        unit.DispatchVehiclePursuitChaseTaskIssued = false;
                        unit.LastDriveTaskAt = DateTime.MinValue;
                        unit.Arrived = false;
                    }
                    LogRuntime("POLICE_BACKUP_DISPATCH_VEHICLE_PED_FIRE_BLOCKED",
                        "Incident=" + incidentId
                        + "; Suspect=" + suspect.Handle
                        + "; SuspectVehicle=" + suspectVehicle.Handle
                        + "; SuspectArmed=" + suspectArmed
                        + "; SuspectShooting=" + suspectFiring
                        + "; SuspectShootingAtPlayer=" + suspectThreateningPlayer
                        + "; SuspectShootingAtBackupOfficer="
                        + suspectThreateningOfficer
                        + "; Officer=" + officer.Handle
                        + "; UnitVehicle=" + unit.Vehicle.Handle
                        + "; OfficerRole=" + (officer.Handle == unit.Driver.Handle
                            ? "Driver" : "Support")
                        + "; UnitArrived=" + unit.Arrived
                        + "; OfficerCombat=" + inCombat
                        + "; OfficerShooting=" + shooting
                        + "; ObservedCombatTarget=" + combatTarget
                        + "; PlayerTarget=" + playerTarget
                        + "; Action=ClearPedCombatAndResumeVehiclePursuit");
                }
            }
        }

        private void CancelDispatchTireShotAfterVehiclePhase(
            LSPDDispatch dispatch,
            Ped suspect,
            Vehicle suspectVehicle,
            IList<BackupUnit> assignedUnits,
            string reason)
        {
            if (suspect == null || assignedUnits == null)
                return;
            foreach (BackupUnit unit in assignedUnits)
            {
                if (unit == null || unit.DispatchVehiclePursuitSuspectHandle
                        != suspect.Handle
                    || !unit.NpcTireShotIssued || unit.NpcTireShotResolved
                    || unit.NpcTireShooter == null
                    || !unit.NpcTireShooter.Exists()
                    || unit.NpcTireShooter.IsDead)
                    continue;
                if (suspectVehicle != null && suspectVehicle.Exists()
                    && unit.NpcTireTargetVehicleHandle != suspectVehicle.Handle)
                    continue;

                unit.NpcTireShotResolved = true;
                try { unit.NpcTireShooter.Task.ClearAll(); }
                catch { }
                LogRuntime("POLICE_BACKUP_DISPATCH_TIRE_SHOT_CANCELLED",
                    "Incident=" + (dispatch == null || dispatch.Current == null
                        ? string.Empty : dispatch.Current.Id)
                    + "; Suspect=" + suspect.Handle
                    + "; TargetVehicle=" + unit.NpcTireTargetVehicleHandle
                    + "; Officer=" + unit.NpcTireShooter.Handle
                    + "; Reason=" + reason
                    + "; DriverBodyTargeted=false; PursuitContinues=true");
                RecoverNpcTireShooter(unit, "Dispatch" + reason, true);
            }
        }

        private void PrepareDispatchVehiclePursuitUnitForEnRoute(
            BackupUnit unit,
            Ped player,
            string incidentId)
        {
            if (unit == null || player == null || !player.Exists())
                return;

            foreach (Ped officer in SupportOfficers(unit)
                .Where(value => !value.IsDead)
                .GroupBy(value => value.Handle)
                .Select(group => group.First()))
            {
                bool friendly = _authority != null
                    && _authority.EnsurePoliceBackupFriendliness(player, officer);
                bool inCombat = officer.IsInCombat || officer.IsShooting;
                int target = 0;
                if (inCombat)
                    try
                    {
                        target = Function.Call<int>(
                            Hash.GET_PED_TARGET_FROM_COMBAT_PED, officer, true);
                    }
                    catch { }

                if (inCombat)
                    try
                    {
                        // Do not carry a spawn-time Ped combat task into the
                        // road-pursuit EnRoute phase. Later fire control admits
                        // only a currently corroborated threat.
                        officer.Task.ClearAll();
                    }
                    catch (Exception ex)
                    {
                        LogException(
                            "POLICE_BACKUP_DISPATCH_VEHICLE_ENROUTE_TASK_CLEAR_FAILED",
                            ex);
                    }

                LogRuntime(
                    "POLICE_BACKUP_DISPATCH_VEHICLE_ENROUTE_FIRE_CONTROL_READY",
                    "Incident=" + (incidentId ?? string.Empty)
                    + "; Officer=" + officer.Handle
                    + "; UnitVehicle=" + unit.Vehicle.Handle
                    + "; RelationshipVerified=" + friendly
                    + "; CombatBefore=" + inCombat
                    + "; CombatTargetBefore=" + target
                    + "; CombatCleared=" + inCombat
                    + "; PedCombatTaskAllowed=false");
            }
        }

        private static bool IsDispatchVehicleSuspectThreateningActor(
            Ped suspect,
            Ped policeActor)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || policeActor == null || !policeActor.Exists() || policeActor.IsDead)
                return false;
            try { return suspect.IsInCombatAgainst(policeActor); }
            catch { return false; }
        }

        private static bool IsDispatchTireShooterAwaitingVehicle(
            BackupUnit unit,
            int targetVehicleHandle)
        {
            if (unit == null || unit.Vehicle == null || !unit.Vehicle.Exists()
                || unit.NpcTireTargetVehicleHandle != targetVehicleHandle
                || !unit.NpcTireShooterExitRequested
                || unit.NpcTireShooter == null
                || !unit.NpcTireShooter.Exists() || unit.NpcTireShooter.IsDead)
                return false;

            Ped shooter = unit.NpcTireShooter;
            if (!shooter.IsInVehicle())
                return true;
            Vehicle currentVehicle = shooter.CurrentVehicle;
            return currentVehicle == null || !currentVehicle.Exists()
                || currentVehicle.Handle != unit.Vehicle.Handle;
        }

        internal static bool IsDispatchVehiclePursuitFireAuthorized(
            bool backupUnitArrived,
            bool suspectStillInVehicle,
            bool suspectArmed,
            bool suspectActivelyShooting,
            bool suspectActivelyThreateningPlayer,
            bool suspectActivelyThreateningBackupOfficer,
            int backupCombatTargetHandle,
            int suspectHandle)
        {
            return suspectStillInVehicle && suspectArmed && suspectActivelyShooting
                && (suspectActivelyThreateningBackupOfficer
                    || (backupUnitArrived && suspectActivelyThreateningPlayer))
                && suspectHandle > 0
                && backupCombatTargetHandle == suspectHandle;
        }

        internal static bool IsDispatchVehicleTireInterventionEligible(
            bool exactOccurrenceTarget,
            bool suspectStillInVehicle,
            bool blockerRole,
            bool blockerHolding,
            bool blockerFellBackToTrail,
            bool shooterAvailable,
            bool driverThreateningPolice,
            float blockerSpeed,
            float targetSpeed,
            float targetDistance,
            float targetAhead)
        {
            return exactOccurrenceTarget && suspectStillInVehicle
                && blockerRole && blockerHolding && !blockerFellBackToTrail
                && shooterAvailable && !driverThreateningPolice
                && Math.Abs(blockerSpeed) <= 0.5f
                && targetSpeed > NpcVehicleStoppedSpeed
                && targetSpeed <= NpcVehicleTireShotMaximumSpeed
                && targetDistance >= NpcVehicleTireShotMinimumRange
                && targetDistance <= NpcVehicleTireShotMaximumRange
                && targetAhead <= -3.0f;
        }

        internal static bool IsDispatchVehicleTireShotTaskAuthorized(
            bool targetVehicleMatches,
            bool shotIssued,
            bool shotResolved,
            bool exactShooter,
            bool officerShooting,
            bool officerInCombat,
            int combatTargetHandle,
            bool suspectThreateningPolice,
            long shotAgeMilliseconds)
        {
            return targetVehicleMatches && shotIssued && !shotResolved
                && exactShooter && officerShooting && !officerInCombat
                && combatTargetHandle == 0 && !suspectThreateningPolice
                && shotAgeMilliseconds >= 0
                && shotAgeMilliseconds <= Math.Max(
                    NpcVehicleTireShotDurationMilliseconds,
                    NpcVehicleTireShotVerifyMilliseconds);
        }

        private bool MaintainDispatchVehicleInterception(
            LSPDDispatch dispatch,
            Ped player,
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

            MaintainDispatchVehiclePursuitFireControl(
                dispatch, player, suspect, suspectVehicle, assignedUnits);

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

            BackupUnit tireBlocker = assignedUnits.FirstOrDefault(unit =>
                IsUsable(unit) && unit.VehiclePursuitBlocker);
            MaintainBackupVehicleTireIntervention(
                null, dispatch, tireBlocker, suspect, suspectVehicle, now);

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

                if (unit.VehiclePursuitBlocker
                    && IsDispatchTireShooterAwaitingVehicle(
                        unit, suspectVehicle.Handle))
                {
                    unit.PursuitBlockerHolding = true;
                    unit.Arrived = true;
                    unit.VehicleContainmentStopRequested = true;
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
                            "POLICE_BACKUP_DISPATCH_TIRE_BLOCKER_HOLD_FAILED", ex);
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
                    Vector3 pursuitRoad;
                    float pursuitRoadHeading;
                    if (!TryResolveDispatchVehiclePursuitRoadNode(
                        suspect, suspectVehicle, false,
                        out pursuitRoad, out pursuitRoadHeading))
                    {
                        unit.LastDriveTaskAt = now;
                        continue;
                    }

                    bool routeChanged = unit.Destination == Vector3.Zero
                        || unit.Destination.DistanceTo(pursuitRoad) >= 14f;
                    bool reachedPreviousGoal = unit.Destination != Vector3.Zero
                        && unit.Vehicle.Position.DistanceTo(unit.Destination) <= 12f;
                    if (routeChanged || reachedPreviousGoal)
                    {
                        unit.DispatchVehiclePursuitChaseTaskIssued = false;
                        if (!SendTo(unit, pursuitRoad, true))
                            unit.LastDriveTaskAt = now;
                        unit.Arrived = false;
                        LogRuntime("POLICE_BACKUP_VEHICLE_PURSUIT_ROUTE",
                            "Suspect=" + suspect.Handle
                            + "; SuspectVehicle=" + suspectVehicle.Handle
                            + "; BackupVehicle=" + unit.Vehicle.Handle
                            + "; Role=" + (unit == leadUnit ? "LeadPursuit" : "TrailingPursuit")
                            + "; RoadNode=" + pursuitRoad
                            + "; RoadHeading=" + pursuitRoadHeading.ToString("0.0")
                            + "; PedTargeting=false"
                            + "; Distance=" + unitDistance.ToString("0.0"));
                    }
                    else
                        unit.LastDriveTaskAt = now;
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
                    unit.DispatchFootPursuitRoadFallbackActive = false;
                    unit.DispatchFootPursuitFallbackRoadNode = Vector3.Zero;
                    unit.NextDispatchFootPursuitFallbackTaskAt = DateTime.MinValue;
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
            _dispatchConvoyCustodyEscortDelegatedSuspects.Clear();
            _dispatchGroupSceneTaskStateByOfficer.Clear();
            _dispatchVehiclePedFireCorrectionByOfficer.Clear();
            _dispatchVehicleFireAuthorizationByOfficer.Clear();
            _dispatchFootApproachTaskStates.Clear();
            _dispatchFootComplianceLogged.Clear();
            _dispatchFootCustodyArrestLogged.Clear();
            _dispatchPlayerTargetingOfficerHandles.Clear();
            _dispatchUnfriendlyOfficerHandles.Clear();
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
                if (targets.Count >= 5)
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
            Ped player,
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
                    if (unit.DispatchFootDeploymentIssued)
                    {
                        unit.DispatchGroupSceneSupportTargetHandle = assignedTarget.Handle;
                        unit.Arrived = true;
                        LogRuntime("POLICE_BACKUP_GROUP_SCENE_FOOT_ASSIGNMENT_RETAINED",
                            "Incident=" + incident.Id
                            + "; UnitIndex=" + unit.UnitIndex
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; Suspect=" + assignedTarget.Handle
                            + "; OfficersRemainOnFoot=true"
                            + "; VehicleRouteRestarted=false");
                    }
                    else
                    {
                    Vector3 sceneApproach = ResolveDispatchVehicleApproach(
                        incident, incident.Origin);
                    Vector3 currentRoadNode;
                    float currentRoadHeading;
                    var otherApproaches = _units
                        .Where(other => other != unit && IsUsable(other)
                            && other.Destination != Vector3.Zero)
                        .Select(other => other.Destination)
                        .ToList();
                    Vector3 roadNode;
                    float routeDistance;
                    string routeEvidence = "NoExteriorVehicleNode";
                    if (TryGetNearbyVehicleRoadNode(
                            unit.Vehicle.Position, sceneApproach, 45f,
                            out currentRoadNode, out currentRoadHeading)
                        && TryFindDistinctDispatchApproachPosition(
                            unit.UnitIndex, currentRoadNode, sceneApproach,
                            otherApproaches, out roadNode, out routeDistance,
                            out routeEvidence))
                    {
                        unit.DispatchGroupSceneSupportTargetHandle = assignedTarget.Handle;
                        unit.Destination = roadNode;
                        unit.Arrived = false;
                        unit.DispatchFootDeploymentIssued = false;
                        unit.LastProgressAt = now;
                        unit.LastProgressPosition = unit.Vehicle.Position;
                        unit.RouteRecoveryCount = 0;
                        unit.DispatchRouteRecoveryDestinations.Clear();
                        unit.DispatchRouteRecoveryDestinations.Add(roadNode);
                        unit.NextDispatchRouteRecoveryAt = DateTime.MinValue;
                        unit.LastDriveTaskAt = DateTime.MinValue;
                        if (!SendTo(unit, roadNode, true))
                            unit.LastDriveTaskAt = now;
                        LogRuntime("POLICE_BACKUP_GROUP_SCENE_ROUTE_ASSIGNED",
                            "Incident=" + incident.Id
                            + "; UnitIndex=" + unit.UnitIndex
                            + "; Suspect=" + assignedTarget.Handle
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; StagingNode=" + currentRoadNode
                            + "; ApproachNode=" + roadNode
                            + "; RouteConnectivity=Connected"
                            + "; TravelDistance=" + routeDistance.ToString("0.0")
                            + "; Evidence=" + routeEvidence);
                    }
                    else
                    {
                        unit.NextDispatchRouteRecoveryAt = now.AddSeconds(15);
                        LogRuntime("POLICE_BACKUP_GROUP_SCENE_ROUTE_ASSIGNMENT_DELAYED",
                            "Incident=" + incident.Id
                            + "; UnitIndex=" + unit.UnitIndex
                            + "; Suspect=" + assignedTarget.Handle
                            + "; Vehicle=" + unit.Vehicle.Handle
                            + "; SceneApproach=" + sceneApproach
                            + "; RouteConnectivity=RejectedOrUnavailable"
                            + "; Evidence=" + (routeEvidence ?? "NoExteriorVehicleNode")
                            + "; UnitPreserved=true");
                    }
                    }
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
                    else if (assignedTarget != null
                        && now >= unit.LastProgressAt.AddSeconds(RouteStallTimeoutSeconds)
                        && now >= unit.NextDispatchRouteRecoveryAt)
                    {
                        Vector3 sceneApproach = ResolveDispatchVehicleApproach(
                            incident, incident.Origin);
                        Vector3 recoveryRoad;
                        float recoveryHeading;
                        float recoveryDistance;
                        string recoveryEvidence = "MaximumRecoveryAttemptsReached";
                        if (unit.RouteRecoveryCount < MaximumRouteRecoveries
                            && TryFindConnectedDispatchRecoveryNode(
                                unit, sceneApproach, out recoveryRoad,
                                out recoveryHeading, out recoveryDistance,
                                out recoveryEvidence))
                        {
                            if (SendTo(unit, recoveryRoad, true))
                            {
                                unit.Destination = recoveryRoad;
                                unit.DispatchRouteRecoveryDestinations.Add(recoveryRoad);
                                unit.RouteRecoveryCount++;
                                unit.LastProgressAt = now;
                                unit.LastProgressPosition = position;
                                unit.NextDispatchRouteRecoveryAt = now.AddSeconds(
                                    RouteStallTimeoutSeconds);
                                LogRuntime("POLICE_BACKUP_GROUP_SCENE_ROUTE_RECOVERY",
                                    "Incident=" + incident.Id
                                    + "; UnitIndex=" + unit.UnitIndex
                                    + "; Vehicle=" + unit.Vehicle.Handle
                                    + "; Suspect=" + assignedTarget.Handle
                                    + "; Recovery=" + unit.RouteRecoveryCount
                                    + "; Destination=" + recoveryRoad
                                    + "; RouteConnectivity=Connected"
                                    + "; TravelDistance=" + recoveryDistance.ToString("0.0")
                                    + "; Evidence=" + recoveryEvidence);
                            }
                            else
                            {
                                unit.NextDispatchRouteRecoveryAt = now.AddSeconds(15);
                                unit.LastProgressAt = now;
                            }
                        }
                        else if (TryDeployDispatchUnitOnFoot(
                                unit, sceneApproach, now))
                        {
                            LogRuntime("POLICE_BACKUP_GROUP_SCENE_FOOT_DEPLOYMENT",
                                "Incident=" + incident.Id
                                + "; UnitIndex=" + unit.UnitIndex
                                + "; Suspect=" + assignedTarget.Handle
                                + "; Trigger=VehicleRouteBlocked"
                                + "; UnitPreserved=true");
                        }
                        else
                        {
                            unit.NextDispatchRouteRecoveryAt = now.AddSeconds(15);
                            unit.LastProgressAt = now;
                            if (!unit.DispatchRouteRecoveryExhaustionLogged)
                            {
                                unit.DispatchRouteRecoveryExhaustionLogged = true;
                                SetState(LSPDBackupAssignmentState.Recovering);
                                LogRuntime("POLICE_BACKUP_GROUP_SCENE_ROUTE_RECOVERY_DELAYED",
                                    "Incident=" + incident.Id
                                    + "; UnitIndex=" + unit.UnitIndex
                                    + "; Vehicle=" + unit.Vehicle.Handle
                                    + "; Suspect=" + assignedTarget.Handle
                                    + "; RouteRecoveryCount=" + unit.RouteRecoveryCount
                                    + "; Evidence=" + recoveryEvidence
                                    + "; NextRetrySeconds=15"
                                    + "; UnitPreserved=true");
                            }
                        }
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
                    player,
                    sceneOfficers,
                    new HashSet<int>(),
                    activePursuitHandles,
                    now,
                    false);
                if (assigned.Count > 0)
                    SetState(LSPDBackupAssignmentState.Supporting);
                else
                    SetState(LSPDBackupAssignmentState.Intercepting);
            }
            return true;
        }

        private HashSet<int> SupportDispatchGroupAtScene(
            LSPDDispatch dispatch,
            Ped player,
            List<Ped> officers,
            HashSet<int> alreadyAssignedOfficerHandles,
            HashSet<int> activePursuitHandles,
            DateTime now,
            bool stationaryGroupScene)
        {
            var assigned = new HashSet<int>();
            LSPDDispatchEvent incident = dispatch == null ? _incident : dispatch.Current;
            if (!IsActiveDispatchSupportOwner(dispatch, incident))
                return assigned;

            RepairDispatchPlayerTargetingOfficers(
                dispatch, player, officers);

            if (incident.BackupArrestInProgressOfficerHandles != null)
                assigned.UnionWith(
                    incident.BackupArrestInProgressOfficerHandles);
            if (incident.Suspects == null || incident.Suspects.Count < 2
                || officers == null || officers.Count == 0)
                return assigned;

            List<Ped> sceneSuspects = incident.Suspects
                    .Where(suspect => suspect != null && suspect.Exists()
                    && !suspect.IsDead && suspect.Health > 0
                        && (!stationaryGroupScene
                            || !IsDispatchSuspectArrestProtected(incident, suspect))
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
                .OrderByDescending(suspect => stationaryGroupScene
                    ? IsDispatchGroupSuspectThreateningPolice(
                        suspect, player, officers)
                    : suspect.IsShooting || suspect.IsInCombat)
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
                _dispatchGroupSceneTaskStateByOfficer.Clear();
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
                        + "; PlayerPursuingOtherSuspect="
                        + (activePursuitHandles != null
                            && activePursuitHandles.Any(handle =>
                                handle != suspect.Handle))
                        + "; SceneDistance=" + suspect.Position
                            .DistanceTo(incident.Origin).ToString("0.0"));

                bool isContained = incident.PursuitContainedSuspectHandles
                        .Contains(suspect.Handle)
                    || incident.CompliantSuspects.Any(value =>
                        value != null && value.Exists()
                        && value.Handle == suspect.Handle);
                bool activelyThreateningPolice = stationaryGroupScene
                    ? IsDispatchGroupSuspectThreateningPolice(
                        suspect, player, officers) && !isContained
                    : suspect.IsShooting || suspect.IsInCombat;
                string existingTaskState;
                bool combatAlreadyIssuedForTarget =
                    _dispatchGroupSceneTaskStateByOfficer.TryGetValue(
                        officer.Handle, out existingTaskState)
                    && string.Equals(existingTaskState,
                        suspect.Handle + "|Combat|Issued",
                        StringComparison.Ordinal);
                bool urgentThreatTask = stationaryGroupScene
                    && activelyThreateningPolice
                    && !combatAlreadyIssuedForTarget;

                if (officer.Position.DistanceTo(suspect.Position)
                        > DispatchOfficerContainmentStartDistance
                    && !(stationaryGroupScene && activelyThreateningPolice))
                {
                    if (stationaryGroupScene
                        && !CanPrepareDispatchSuspectTask(
                            dispatch, player, officer, suspect, true))
                    {
                        RecordDispatchGroupSceneTaskTransition(
                            incident, officer, suspect, "Approach", false,
                            "DispatchOwnershipOrPoliceSafetyGate");
                        continue;
                    }
                    MaintainDispatchFootApproachTask(
                        dispatch, player, officer, suspect, now, false);
                    continue;
                }

                if (dispatch != null
                    && incident.SceneArrivalReported
                    && CanPrepareDispatchSuspectTask(
                        dispatch, player, officer, suspect, true)
                    && dispatch.RequestBackupSuspectArrest(officer, suspect))
                {
                    if (stationaryGroupScene)
                        RecordDispatchGroupSceneTaskTransition(
                            incident, officer, suspect, "PhysicalArrest", true,
                            "DispatchAcceptedBackupArrest");
                    continue;
                }

                bool canIssueSuspectTask = canIssueSupportTask || urgentThreatTask;
                if (!canIssueSuspectTask)
                {
                    if (stationaryGroupScene)
                    {
                        RecordDispatchGroupSceneTaskTransition(
                            incident, officer, suspect,
                            activelyThreateningPolice ? "Combat" : "Aim",
                            false, "SupportTaskRefreshInterval");
                    }
                    continue;
                }

                if (activelyThreateningPolice)
                {
                    bool issued = AssignDispatchCombat(
                        dispatch, player, officer, suspect);
                    if (stationaryGroupScene)
                        RecordDispatchGroupSceneTaskTransition(
                            incident, officer, suspect, "Combat", issued,
                            issued ? "ConfirmedPoliceThreat" : "DispatchOwnershipOrPoliceSafetyGate");
                }
                else
                {
                    bool issued = AimAtDispatchThreat(
                        dispatch, player, officer, suspect);
                    if (stationaryGroupScene)
                        RecordDispatchGroupSceneTaskTransition(
                            incident, officer, suspect, "Aim", issued,
                            issued ? (isContained
                                ? "ContainedNoCombat" : "SuspectNotActivelyAttackingPolice")
                                : "DispatchOwnershipOrPoliceSafetyGate");
                }
            }

            foreach (int staleOfficer in _dispatchGroupSceneTaskStateByOfficer.Keys
                .Where(handle => !assigned.Contains(handle)).ToList())
                _dispatchGroupSceneTaskStateByOfficer.Remove(staleOfficer);

            return assigned;
        }

        private bool IsDispatchGroupSuspectThreateningPolice(
            Ped suspect,
            Ped player,
            IEnumerable<Ped> deployedOfficers)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead)
                return false;
            try
            {
                if (suspect.IsShooting)
                    return true;
                if (!suspect.IsInCombat)
                    return false;
                if (player != null && player.Exists()
                    && suspect.IsInCombatAgainst(player))
                    return true;
                if (deployedOfficers == null)
                    return false;
                foreach (Ped officer in deployedOfficers)
                    if (officer != null && officer.Exists() && !officer.IsDead
                        && suspect.IsInCombatAgainst(officer))
                        return true;
            }
            catch
            {
                return false;
            }
            return false;
        }

        private void RecordDispatchGroupSceneTaskTransition(
            LSPDDispatchEvent incident,
            Ped officer,
            Ped suspect,
            string task,
            bool issued,
            string reason)
        {
            if (incident == null || officer == null || suspect == null
                || !officer.Exists() || !suspect.Exists())
                return;
            string result = issued ? "Issued" : "Withheld";
            string state = suspect.Handle + "|" + task + "|" + result;
            string previous;
            if (_dispatchGroupSceneTaskStateByOfficer.TryGetValue(
                    officer.Handle, out previous))
            {
                if (string.Equals(previous, state, StringComparison.Ordinal))
                    return;
                if (!issued && string.Equals(previous,
                        suspect.Handle + "|" + task + "|Issued",
                        StringComparison.Ordinal))
                    return;
            }
            _dispatchGroupSceneTaskStateByOfficer[officer.Handle] = state;
            LogRuntime(issued
                    ? "POLICE_BACKUP_GROUP_SCENE_TASK_ISSUED"
                    : "POLICE_BACKUP_GROUP_SCENE_TASK_WITHHELD",
                "Incident=" + incident.Id
                + "; Officer=" + officer.Handle
                + "; Suspect=" + suspect.Handle
                + "; Task=" + task
                + "; Result=" + result
                + "; Reason=" + reason);
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
            HashSet<int> trackedSuspectHandles = new HashSet<int>(
                dispatch.CurrentSuspects.Where(value => value != null
                    && value.Exists() && !value.IsDead)
                    .Select(value => value.Handle));
            var assignedOfficerHandles = new HashSet<int>();
            foreach (int delegatedSuspectHandle
                in _dispatchConvoyCustodyEscortDelegatedSuspects)
            {
                int delegatedOfficerHandle;
                if (_dispatchFootInterceptorBySuspect.TryGetValue(
                        delegatedSuspectHandle, out delegatedOfficerHandle)
                    || _dispatchGroupSceneOfficerBySuspect.TryGetValue(
                        delegatedSuspectHandle, out delegatedOfficerHandle))
                    assignedOfficerHandles.Add(delegatedOfficerHandle);
            }
            HashSet<int> sceneOfficerHandles = SupportDispatchGroupAtScene(
                dispatch, player, officers, assignedOfficerHandles,
                activeTargetHandles, now, false);
            assignedOfficerHandles.UnionWith(sceneOfficerHandles);
            HashSet<int> activeSceneTargetHandles = new HashSet<int>(
                _dispatchGroupSceneOfficerBySuspect.Keys);
            foreach (int stale in _dispatchFootInterceptorBySuspect.Keys
                .Where(handle => !trackedSuspectHandles.Contains(handle)).ToList())
                _dispatchFootInterceptorBySuspect.Remove(stale);
            foreach (int staleOfficer in _dispatchFootApproachTaskStates
                .Where(pair => !activeTargetHandles.Contains(pair.Value.TargetHandle)
                    && !activeSceneTargetHandles.Contains(pair.Value.TargetHandle)
                    && !_dispatchFootInterceptorBySuspect.ContainsKey(
                        pair.Value.TargetHandle))
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
                    if (CanPrepareDispatchSuspectTask(
                            dispatch, player, interceptor, target, false)
                        && dispatch.RequestBackupInterception(target, player))
                    {
                        anyContained = true;
                        bool backupArrestStarted =
                            CanPrepareDispatchSuspectTask(
                                dispatch, player, interceptor, target, true)
                            && dispatch.RequestBackupSuspectArrest(
                                interceptor, target);
                        if (_dispatchFootComplianceLogged.Add(target.Handle))
                            LogRuntime("POLICE_BACKUP_GROUP_FOOT_SUSPECT_CONTAINED",
                                "Suspect=" + target.Handle + "; Officer=" + interceptor.Handle
                                + "; Distance=" + distance.ToString("0.0")
                                + "; BackupPhysicalArrestStarted=" + backupArrestStarted
                                + "; PlayerFinalArrest=" + !backupArrestStarted);
                        if (backupArrestStarted
                            && _dispatchFootCustodyArrestLogged.Add(target.Handle))
                            LogRuntime("POLICE_BACKUP_GROUP_FOOT_CUSTODY_ARREST_STARTED",
                                "Suspect=" + target.Handle
                                + "; Officer=" + interceptor.Handle
                                + "; Distance=" + distance.ToString("0.0")
                                + "; Method=BackupPhysicalArrest"
                                + "; PlayerFinalArrest=false");
                        if (!_dispatchSuspectCompliantLogged)
                        {
                            _dispatchSuspectCompliantLogged = true;
                            Notify(backupArrestStarted
                                ? "~b~POLICE BACKUP~s~\nBackup contained the fleeing suspect and is securing them."
                                : "~b~POLICE BACKUP~s~\nBackup contained the fleeing suspect. Approach and use E to complete the arrest.");
                            Report("lsimmersivelife.police.compliance.confirmed",
                                "group-foot-intercepted");
                        }
                    }
                    continue;
                }

                MaintainDispatchFootApproachTask(
                    dispatch, player, interceptor, target, now, false);
            }

            foreach (KeyValuePair<int, int> custodyAssignment
                in _dispatchFootInterceptorBySuspect.ToArray())
            {
                if (activeTargetHandles.Contains(custodyAssignment.Key))
                    continue;
                Ped suspect = dispatch.CurrentSuspects.FirstOrDefault(value =>
                    value != null && value.Exists() && !value.IsDead
                    && value.Handle == custodyAssignment.Key);
                if (suspect == null)
                    continue;
                Ped officer = officers.FirstOrDefault(value => value != null
                    && value.Exists() && !value.IsDead
                    && value.Handle == custodyAssignment.Value);
                if (officer == null)
                    officer = officers.Where(value => value != null
                            && value.Exists() && !value.IsDead
                            && !assignedOfficerHandles.Contains(value.Handle))
                        .OrderBy(value => value.Position.DistanceTo(suspect.Position))
                        .FirstOrDefault();
                if (officer == null)
                    continue;

                assignedOfficerHandles.Add(officer.Handle);
                _dispatchFootInterceptorBySuspect[suspect.Handle] = officer.Handle;
                bool physicallyArrested = dispatch.CurrentArrestedSuspects.Any(
                    value => value.Handle == suspect.Handle);
                float distance = officer.Position.DistanceTo(suspect.Position);
                if (physicallyArrested)
                {
                    anyContained = true;
                    if (_dispatchConvoyCustodyEscortDelegatedSuspects.Contains(
                        suspect.Handle))
                        continue;
                    if (distance > DispatchOfficerContainmentDistance)
                        MaintainDispatchFootApproachTask(
                            dispatch, player, officer, suspect, now, false);
                    continue;
                }

                if (distance <= DispatchOfficerContainmentDistance
                    && CanPrepareDispatchSuspectTask(
                        dispatch, player, officer, suspect, true)
                    && dispatch.RequestBackupSuspectArrest(officer, suspect))
                {
                    anyContained = true;
                    if (_dispatchFootCustodyArrestLogged.Add(suspect.Handle))
                        LogRuntime("POLICE_BACKUP_GROUP_FOOT_CUSTODY_ARREST_STARTED",
                            "Suspect=" + suspect.Handle
                            + "; Officer=" + officer.Handle
                            + "; Distance=" + distance.ToString("0.0")
                            + "; Method=ContainedSuspectFollowUp"
                            + "; PlayerFinalArrest=false");
                }
                else if (distance > DispatchOfficerContainmentDistance)
                {
                    MaintainDispatchFootApproachTask(
                        dispatch, player, officer, suspect, now, false);
                }
            }

            if (CanIssueSupportTasks())
                foreach (Ped officer in officers.Where(value =>
                    !assignedOfficerHandles.Contains(value.Handle)))
                {
                    Ped threat = targets.OrderBy(value =>
                        value.Position.DistanceTo(officer.Position)).FirstOrDefault();
                    if (threat != null && officer.Position.DistanceTo(threat.Position) <= 75f)
                        AimAtDispatchThreat(dispatch, player, officer, threat);
                }

            SetState(anyContained
                ? LSPDBackupAssignmentState.Securing
                : LSPDBackupAssignmentState.Intercepting);
        }

        private void SupportInterception(LSPDDispatch dispatch, Ped player)
        {
            DateTime now = DateTime.UtcNow;
            List<Ped> footTargets = ActiveFootPursuitTargets(dispatch);
            HashSet<int> currentSuspectHandles = new HashSet<int>(
                dispatch.CurrentSuspects.Where(suspect => suspect != null
                    && suspect.Exists() && !suspect.IsDead)
                    .Select(suspect => suspect.Handle));
            bool hasTrackedGroupFootCustody = dispatch.HasSuspectGroup
                && _dispatchFootInterceptorBySuspect.Keys.Any(handle =>
                    currentSuspectHandles.Contains(handle));
            if ((footTargets.Count > 1 || hasTrackedGroupFootCustody)
                && !footTargets.Any(IsDispatchActorInsideInterior))
            {
                SupportMultipleFootPursuits(dispatch, player, footTargets, now);
                return;
            }

            Ped target = ResolveDispatchPursuitTarget(dispatch);
            if (target == null || !target.Exists() || target.IsDead)
            {
                if (hasTrackedGroupFootCustody)
                {
                    SupportMultipleFootPursuits(
                        dispatch, player, new List<Ped>(), now);
                    return;
                }
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

            HashSet<int> sceneOfficerHandles = SupportDispatchGroupAtScene(
                dispatch,
                player,
                officers,
                new HashSet<int>(),
                new HashSet<int>(footTargets.Select(value => value.Handle)),
                now,
                false);
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
                    dispatch,
                    player,
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
                            AimAtDispatchThreat(dispatch, player, officer, target);
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
            bool leadClose = leadOfficer != null
                && (leadDistance <= DispatchFootLeadContainmentDistance
                    && (leadIsAhead
                        || (targetStopped
                            && leadDistance <= DispatchOfficerContainmentDistance)));
            // A nearby officer must be able to issue the surrender command even
            // when the runner has passed the second officer or the full
            // ahead/rear formation has not formed. Requiring both officers to
            // reach arrest distance left the runner fleeing past Backup while
            // the officers repeatedly tried to reposition.
            bool interceptionReady = officerDistance <= DispatchOfficerContainmentStartDistance
                || (targetStopped && leadOfficer != null && leadClose);

            if (!interceptionReady)
            {
                if (now >= _nextDispatchComplianceEligibilityLogAt)
                {
                    _nextDispatchComplianceEligibilityLogAt = now.AddSeconds(5);
                    LogRuntime("POLICE_BACKUP_SUSPECT_COMPLIANCE_DEFERRED_DISTANCE",
                        "Suspect=" + target.Handle
                        + "; RearOfficer=" + interceptor.Handle
                        + "; RearDistance=" + officerDistance.ToString("0.0")
                        + "; LeadOfficer=" + (leadOfficer == null
                            ? 0 : leadOfficer.Handle)
                        + "; LeadDistance=" + (leadOfficer == null
                            ? "Unavailable" : leadDistance.ToString("0.0"))
                        + "; TargetStopped=" + targetStopped
                        + "; RequiredRearDistance="
                        + DispatchOfficerContainmentStartDistance.ToString("0.0")
                        + "; Reason=NoBackupOfficerWithinPhysicalInterceptionRange");
                }
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
                        dispatch, player, interceptor, target, now, false);
                if (leadOfficer != null
                    && !leadOfficer.IsInCombat && !leadOfficer.IsShooting)
                    MaintainDispatchFootApproachTask(
                        dispatch, player, leadOfficer, target, now, true);
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
            if (CanPrepareDispatchSuspectTask(
                    dispatch, player, interceptor, target, false)
                && dispatch.RequestBackupInterception(target, player))
            {
                if (CanIssueSupportTasks() && now >= _nextDispatchComplianceAimAt)
                {
                    foreach (Ped officer in officers.Where(value =>
                        value.Position.DistanceTo(target.Position)
                            <= DispatchOfficerContainmentStartDistance))
                        AimAtDispatchThreat(dispatch, player, officer, target);
                    _nextDispatchComplianceAimAt = now.AddMilliseconds(
                        SupportTaskRefreshMilliseconds);
                }
                if (!_dispatchSuspectCompliantLogged)
                {
                    _dispatchSuspectCompliantLogged = true;
                    LogRuntime("POLICE_BACKUP_SUSPECT_COMPLIANT",
                        "Suspect=" + target.Handle + "; Officer=" + interceptor.Handle
                        + "; DispatchOwnsSuspect=true; Arrested=false");
                    Notify("~b~POLICE BACKUP~s~\nBack Up has secured the suspect. Approach and press E to physically arrest them; no second compliance command is needed.");
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
            LSPDDispatch dispatch,
            Ped player,
            Ped officer,
            Ped target,
            DateTime now,
            bool lead)
        {
            if (officer == null || !officer.Exists() || officer.IsDead
                || target == null || !target.Exists() || target.IsDead)
                return;
            if (!CanPrepareDispatchSuspectTask(
                dispatch, player, officer, target, true))
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
                    dispatch, player, officer, target, lead, 0,
                    "POLICE_BACKUP_FOOT_APPROACH_FAILED");
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
                dispatch, player, officer, target, lead, state.RecoveryCount,
                "POLICE_BACKUP_FOOT_APPROACH_RECOVERY_FAILED");
        }

        private void IssueDispatchFootApproachTask(
            LSPDDispatch dispatch,
            Ped player,
            Ped officer,
            Ped target,
            bool lead,
            int recoveryCount,
            string errorCategory)
        {
            try
            {
                if (!CanPrepareDispatchSuspectTask(
                    dispatch, player, officer, target, true))
                    return;
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

                int groupOfficerHandle;
                if (_dispatchGroupSceneOfficerBySuspect.TryGetValue(
                        target.Handle, out groupOfficerHandle)
                    && groupOfficerHandle == officer.Handle)
                    RecordDispatchGroupSceneTaskTransition(
                        dispatch == null ? null : dispatch.Current,
                        officer,
                        target,
                        recoveryCount > 0 ? "ApproachRecovery" : "Approach",
                        true,
                        recoveryCount > 0
                            ? "BoundedFootApproachRecovery"
                            : lead ? "FollowToContainmentPosition" : "ApproachFromRoad");
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
            LSPDDispatch dispatch,
            Ped player,
            Ped interceptor,
            Ped target,
            int targetInteriorId,
            DateTime now)
        {
            if (_ambientWorld == null || target == null || !target.Exists()
                || interceptor == null || !interceptor.Exists() || interceptor.IsDead)
                return true;
            if (!CanPrepareDispatchSuspectTask(
                dispatch, player, interceptor, target, false))
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

        private static bool IsBackupUnitOnScreen(BackupUnit unit)
        {
            try
            {
                if (unit == null)
                    return true;
                if (unit.Vehicle != null && unit.Vehicle.Exists()
                    && Function.Call<bool>(Hash.IS_ENTITY_ON_SCREEN, unit.Vehicle))
                    return true;
                foreach (Ped officer in unit.Officers == null
                    ? Enumerable.Empty<Ped>() : unit.Officers)
                    if (officer != null && officer.Exists()
                        && Function.Call<bool>(Hash.IS_ENTITY_ON_SCREEN, officer))
                        return true;
                return false;
            }
            catch
            {
                // A failed visibility probe is not permission to remove a
                // managed unit whose departure has not been observed.
                return true;
            }
        }

        private void ProcessStandDown(Ped player, DateTime now)
        {
            CleanupMissingUnits();
            foreach (BackupUnit unit in _units)
                MaintainNpcCitizenCustodyProtection(unit, "BackupStandDown");
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
                bool unitOnScreen = IsBackupUnitOnScreen(unit);
                bool safeToRemove = distance >= SafeCleanupDistance && !unitOnScreen;
                // Never remove or release a Police unit while it is close to,
                // or visible from, the Player. Distance alone is insufficient
                // when the unit remains on screen at a long camera range.
                if (_npcBackgroundCleanupRequested && hardTimeout && safeToRemove)
                {
                    ReleaseBrokenUnit(unit);
                    ReleaseNpcCitizenCustodyProtection(
                        unit, "BackupBackgroundDepartureSafeDistance");
                    _units.Remove(unit);
                    LogRuntime("POLICE_BACKUP_NPC_BACKGROUND_UNIT_RELEASED",
                        "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                        + "; Distance=" + distance.ToString("0.0")
                        + "; Visible=false; Reason=SafeDistance");
                }
                else if (safeToRemove && pastGrace)
                {
                    DeleteUnit(unit);
                    ReleaseNpcCitizenCustodyProtection(
                        unit, "BackupUnitSafelyDisposed");
                    _units.Remove(unit);
                    LogRuntime("POLICE_BACKUP_STAND_DOWN_CLEANUP_COMPLETED",
                        "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                        + "; DistanceFromPlayer=" + distance.ToString("0.0")
                        + "; Visible=false"
                        + "; Reason=SafeDistance");
                }
                else if (_npcBackgroundCleanupRequested && hardTimeout)
                {
                    // Preserve the existing Citizen background-departure
                    // ownership rule; Dispatch-owned requested units use the
                    // retained final-route path below.
                    ReleaseBrokenUnit(unit);
                    ReleaseNpcCitizenCustodyProtection(
                        unit, "BackupBackgroundDepartureBoundedCleanup");
                    _units.Remove(unit);
                    LogRuntime("POLICE_BACKUP_NPC_BACKGROUND_UNIT_RELEASED",
                        "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                        + "; Distance=" + distance.ToString("0.0")
                        + "; Reason=BoundedDepartureCleanup");
                }
                else if (hardTimeout && !unit.FinalStandDownRouteAttempted)
                {
                    unit.FinalStandDownRouteAttempted = true;
                    unit.FinalStandDownRouteDeadline = now.AddSeconds(25);
                    bool routeAccepted = SendTo(unit, unit.ReturnDestination, true);
                    LogRuntime("POLICE_BACKUP_STAND_DOWN_FINAL_ROUTE_RETRY",
                        "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                        + "; DistanceFromPlayer=" + distance.ToString("0.0")
                        + "; Destination=" + unit.ReturnDestination
                        + "; RouteAccepted=" + routeAccepted
                        + "; FinalGraceSeconds=25");
                    continue;
                }
                else if (hardTimeout && unit.FinalStandDownRouteAttempted)
                {
                    if (now >= unit.FinalStandDownRouteDeadline
                        && !unit.FinalStandDownGraceExpiredLogged)
                    {
                        unit.FinalStandDownGraceExpiredLogged = true;
                        LogRuntime(
                            "POLICE_BACKUP_STAND_DOWN_UNIT_RETAINED_NEAR_PLAYER",
                            "Vehicle=" + (unit.Vehicle == null ? 0 : unit.Vehicle.Handle)
                            + "; DistanceFromPlayer=" + distance.ToString("0.0")
                            + "; Visible=" + unitOnScreen
                            + "; SafeCleanupDistance="
                            + SafeCleanupDistance.ToString("0.0")
                            + "; FinalRouteAttempted=true"
                            + "; RouteTaskRetained=true"
                            + "; Reason=StillCloseOrVisible");
                    }
                    // Keep the managed vehicle and its departure task until it
                    // is both outside the safe radius and off screen.
                    continue;
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

        private static List<Ped> GetActiveDispatchSuspects(
            LSPDDispatchEvent incident)
        {
            if (incident == null || incident.Suspects == null)
                return new List<Ped>();

            return incident.Suspects.Where(suspect =>
                suspect != null && suspect.Exists() && !suspect.IsDead
                && suspect.Health > 0
                && !incident.BackupArrestInProgressSuspectHandles.Contains(
                    suspect.Handle)
                && !(incident.PlayerHandcuffInProgress
                    && incident.PlayerHandcuffTargetHandle == suspect.Handle)
                && (incident.ArrestedSuspects == null
                    || !incident.ArrestedSuspects.Any(arrested => arrested != null
                        && arrested.Exists() && arrested.Handle == suspect.Handle)))
                .ToList();
        }

        private static Vector3 ResolveDispatchVehicleApproach(
            LSPDDispatchEvent incident,
            Vector3 fallback)
        {
            if (incident == null)
                return fallback;

            AmbientDispatchSceneResolution scene = incident.SceneResolution;
            if (scene != null)
            {
                if (scene.HasVehicleStagingPosition
                    && IsValidBackupRoutePosition(scene.VehicleStagingPosition))
                    return scene.VehicleStagingPosition;
                if (scene.IsInteriorScene
                    && IsValidBackupRoutePosition(scene.ExteriorApproachPosition))
                    return scene.ExteriorApproachPosition;
            }

            return IsValidBackupRoutePosition(fallback)
                ? fallback
                : IsValidBackupRoutePosition(incident.Origin)
                    ? incident.Origin : Vector3.Zero;
        }

        private bool TryValidateDispatchRouteConnectivity(
            Vector3 stagingRoadNode,
            Vector3 approach,
            out float travelDistance,
            out string evidence)
        {
            travelDistance = 0f;
            evidence = string.Empty;
            if (!IsValidBackupRoutePosition(stagingRoadNode)
                || !IsValidBackupRoutePosition(approach))
            {
                evidence = "Reason=InvalidRoadNodeOrApproach";
                return false;
            }

            Vector3 approachRoadNode;
            float approachHeading;
            if (!TryGetNearbyVehicleRoadNode(
                    approach, approach, 35f,
                    out approachRoadNode, out approachHeading))
            {
                evidence = "Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING"
                    + "; Result=NoApproachRoadNode";
                return false;
            }

            float approachSnapDistance = approachRoadNode.DistanceTo(approach);
            float straightLineDistance = stagingRoadNode.DistanceTo(approachRoadNode);
            if (approachSnapDistance > 35f
                || Math.Abs(stagingRoadNode.Z - approachRoadNode.Z) > 18f)
            {
                evidence = "Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING"
                    + "; Result=ApproachNodeOutOfRangeOrDifferentLevel"
                    + "; ApproachSnap=" + approachSnapDistance.ToString("0.0")
                    + "; ElevationDelta="
                    + Math.Abs(stagingRoadNode.Z - approachRoadNode.Z).ToString("0.0");
                return false;
            }

            if (straightLineDistance <= 3f)
            {
                travelDistance = straightLineDistance;
                evidence = "Native=SameVehicleRoadNode"
                    + "; Staging=" + stagingRoadNode
                    + "; Approach=" + approachRoadNode;
                return true;
            }

            try
            {
                travelDistance = Function.Call<float>(
                    Hash.CALCULATE_TRAVEL_DISTANCE_BETWEEN_POINTS,
                    stagingRoadNode.X, stagingRoadNode.Y, stagingRoadNode.Z,
                    approachRoadNode.X, approachRoadNode.Y, approachRoadNode.Z);
                float maximumPlausibleRoute = Math.Max(
                    250f, straightLineDistance * 8f + 100f);
                bool connected = !float.IsNaN(travelDistance)
                    && !float.IsInfinity(travelDistance)
                    && travelDistance > 0f
                    && travelDistance < 99999f
                    && travelDistance <= maximumPlausibleRoute;
                evidence = "Native=CALCULATE_TRAVEL_DISTANCE_BETWEEN_POINTS"
                    + "; Staging=" + stagingRoadNode
                    + "; ApproachNode=" + approachRoadNode
                    + "; StraightLine=" + straightLineDistance.ToString("0.0")
                    + "; TravelDistance=" + travelDistance.ToString("0.0")
                    + "; FailureSentinel=100000"
                    + "; MaximumPlausible=" + maximumPlausibleRoute.ToString("0.0");
                return connected;
            }
            catch (Exception ex)
            {
                evidence = "Native=CALCULATE_TRAVEL_DISTANCE_BETWEEN_POINTS"
                    + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        private bool TryFindDistinctDispatchApproachPosition(
            int unitIndex,
            Vector3 stagingRoadNode,
            Vector3 sceneApproach,
            IList<Vector3> previouslyAssignedApproaches,
            out Vector3 selectedApproach,
            out float travelDistance,
            out string evidence)
        {
            selectedApproach = Vector3.Zero;
            travelDistance = 0f;
            evidence = string.Empty;
            if (!IsValidBackupRoutePosition(stagingRoadNode)
                || !IsValidBackupRoutePosition(sceneApproach))
            {
                evidence = "Reason=InvalidStagingOrSceneApproach";
                return false;
            }

            var candidates = new List<Vector3> { sceneApproach };
            Vector2[] directions =
            {
                new Vector2(1f, 0f), new Vector2(-1f, 0f),
                new Vector2(0f, 1f), new Vector2(0f, -1f),
                new Vector2(0.707f, 0.707f), new Vector2(-0.707f, 0.707f),
                new Vector2(0.707f, -0.707f), new Vector2(-0.707f, -0.707f)
            };
            float[] offsets = { 12f, 22f, 34f };
            int directionStart = Math.Max(0, unitIndex) % directions.Length;
            foreach (float offset in offsets)
                for (int directionIndex = 0;
                    directionIndex < directions.Length;
                    directionIndex++)
                {
                    Vector2 direction = directions[
                        (directionStart + directionIndex) % directions.Length];
                    candidates.Add(new Vector3(
                        sceneApproach.X + direction.X * offset,
                        sceneApproach.Y + direction.Y * offset,
                        sceneApproach.Z));
                }

            string lastFailure = "NoDistinctVehicleApproachNode";
            foreach (Vector3 query in candidates)
            {
                Vector3 approachNode;
                float approachHeading;
                if (!TryGetNearbyVehicleRoadNode(
                        query, sceneApproach, 22f,
                        out approachNode, out approachHeading))
                {
                    lastFailure = "NoVehicleRoadNodeForCandidate=" + query;
                    continue;
                }
                if (approachNode.DistanceTo(sceneApproach) > 42f
                    || Math.Abs(approachNode.Z - sceneApproach.Z) > 12f)
                {
                    lastFailure = "ApproachNodeOutOfSceneRange=" + approachNode;
                    continue;
                }
                if (previouslyAssignedApproaches != null
                    && previouslyAssignedApproaches.Any(assigned =>
                        assigned.DistanceTo(approachNode) < 9f))
                {
                    lastFailure = "ApproachNodeDuplicatesEarlierUnit=" + approachNode;
                    continue;
                }

                float stagingToApproachDistance;
                string stagingRouteEvidence;
                if (!TryValidateDispatchRouteConnectivity(
                        stagingRoadNode, approachNode,
                        out stagingToApproachDistance,
                        out stagingRouteEvidence))
                {
                    lastFailure = "StagingRouteRejected=" + stagingRouteEvidence;
                    continue;
                }
                float toSceneDistance;
                string sceneRouteEvidence;
                if (!TryValidateDispatchRouteConnectivity(
                        approachNode, sceneApproach,
                        out toSceneDistance, out sceneRouteEvidence))
                {
                    lastFailure = "SceneRouteRejected=" + sceneRouteEvidence;
                    continue;
                }

                selectedApproach = approachNode;
                travelDistance = stagingToApproachDistance;
                evidence = "UnitIndex=" + unitIndex
                    + "; ApproachNode=" + approachNode
                    + "; SceneApproach=" + sceneApproach
                    + "; DistinctFromEarlierUnits=true"
                    + "; StagingRoute=" + stagingRouteEvidence
                    + "; SceneRoute=" + sceneRouteEvidence;
                return true;
            }

            evidence = "Result=NoDistinctConnectedApproachNode"
                + "; UnitIndex=" + unitIndex
                + "; LastFailure=" + lastFailure;
            return false;
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

        private bool TryFindConnectedDispatchStagingPosition(
            Ped player,
            int index,
            Vector3 destination,
            out int candidatesChecked,
            out Vector3 selectedRoadNode)
        {
            candidatesChecked = 0;
            selectedRoadNode = Vector3.Zero;
            if (player == null || !player.Exists())
                return false;

            float configuredDistance = Math.Max(35f, _settings.MinimumStagingDistance);
            float minimumDistance = destination == Vector3.Zero
                ? configuredDistance
                : Math.Max(45f, configuredDistance * 0.70f);
            Vector3 reference = destination == Vector3.Zero
                ? player.Position : destination;
            Vector3[] candidates = BuildDispatchStagingCandidates(
                player.Position, index, destination, configuredDistance);
            int candidateLimit = Math.Min(
                MaximumDispatchStagingCandidates,
                candidates == null ? 0 : candidates.Length);
            for (int candidateIndex = 0;
                candidateIndex < candidateLimit;
                candidateIndex++)
            {
                candidatesChecked++;
                Vector3 roadNode;
                float roadHeading;
                if (!TryGetNearbyVehicleRoadNode(
                    candidates[candidateIndex], reference, 38f,
                    out roadNode, out roadHeading))
                    continue;

                float playerDistance = roadNode.DistanceTo(player.Position);
                float targetDistance = roadNode.DistanceTo(reference);
                float maximumTargetDistance =
                    candidates[candidateIndex].DistanceTo(reference) + 38f;
                if (playerDistance < minimumDistance * 0.75f
                    || targetDistance < minimumDistance * 0.55f
                    || targetDistance > maximumTargetDistance
                    || _units.Any(unit => IsUsable(unit)
                        && unit.Vehicle.Position.DistanceTo(roadNode) < 18f))
                    continue;

                float connectedTravelDistance = 0f;
                string routeEvidence = "NotApplicable";
                if (_incident != null
                    && !TryValidateDispatchRouteConnectivity(
                        roadNode, destination, out connectedTravelDistance,
                        out routeEvidence))
                {
                    LogRuntime("POLICE_BACKUP_DISPATCH_STAGING_ROUTE_REJECTED",
                        "Operation=" + _operationId
                        + "; Incident=" + _incident.Id
                        + "; UnitIndex=" + index
                        + "; RoadNode=" + roadNode
                        + "; Approach=" + destination
                        + "; Candidate=" + (candidateIndex + 1)
                        + "; RouteConnectivity=Rejected"
                        + "; Evidence=" + routeEvidence);
                    continue;
                }

                LogRuntime("POLICE_BACKUP_DISPATCH_STAGING_NODE_SELECTED",
                    "Operation=" + _operationId
                    + "; Incident=" + (_incident == null ? string.Empty : _incident.Id)
                    + "; UnitIndex=" + index
                    + "; Candidate=" + (candidateIndex + 1)
                    + "; CandidatesChecked=" + candidatesChecked
                    + "; CandidatePosition=" + candidates[candidateIndex]
                    + "; RoadNode=" + roadNode
                    + "; RoadHeading=" + roadHeading.ToString("0.0")
                    + "; PlayerDistance=" + playerDistance.ToString("0.0")
                    + "; SceneDistance=" + targetDistance.ToString("0.0")
                    + "; GroundRoadNodeValidated=true"
                    + "; RouteConnectivity=" + (_incident == null ? "NotApplicable" : "Connected")
                    + "; RouteTravelDistance=" + connectedTravelDistance.ToString("0.0")
                    + "; RouteEvidence=" + routeEvidence);
                selectedRoadNode = roadNode;
                return true;
            }

            return false;
        }

        internal static Vector3[] BuildDispatchStagingCandidates(
            Vector3 playerPosition,
            int unitIndex,
            Vector3 destination,
            float configuredMinimumDistance)
        {
            Vector3 reference = destination == Vector3.Zero
                ? playerPosition : destination;
            if (reference == Vector3.Zero)
                return new Vector3[0];

            int boundedUnitIndex = Math.Max(0, unitIndex);
            float configuredDistance = Math.Max(35f, configuredMinimumDistance);
            float minimumDistance = destination == Vector3.Zero
                ? configuredDistance
                : Math.Max(45f, configuredDistance * 0.70f);
            float lateral = (boundedUnitIndex % 2 == 0 ? -1f : 1f)
                * (8f + boundedUnitIndex * 5f);
            var candidates = new List<Vector3>(MaximumDispatchStagingCandidates)
            {
                reference + new Vector3(
                    lateral,
                    -minimumDistance - boundedUnitIndex * 8f,
                    0f)
            };

            float radius = minimumDistance + boundedUnitIndex * 8f;
            Vector2[] fallbackDirections =
            {
                new Vector2(0f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, -1f),
                new Vector2(-1f, 0f),
                new Vector2(0.707f, 0.707f),
                new Vector2(0.707f, -0.707f),
                new Vector2(-0.707f, 0.707f)
            };
            foreach (Vector2 direction in fallbackDirections)
            {
                if (candidates.Count >= MaximumDispatchStagingCandidates)
                    break;
                candidates.Add(reference + new Vector3(
                    direction.X * radius,
                    direction.Y * radius,
                    0f));
            }

            return candidates.ToArray();
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

            if (IsDispatchRouteAssignment())
                return TryRecoverStalledDispatchRoute(unit, destination, now);

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

        private bool IsDispatchRouteAssignment()
        {
            return _incident != null && _incident.OwnedByDispatch
                && !_incident.HasConvoyCustodyHandoff
                && _convoy == null && _crimeActivity == null
                && _npcResponse == null && _gangResponse == null;
        }

        private bool TryRecoverStalledDispatchRoute(
            BackupUnit unit,
            Vector3 destination,
            DateTime now)
        {
            if (!IsUsable(unit) || destination == Vector3.Zero)
                return false;

            float moved = unit.Vehicle.Position.DistanceTo(unit.LastProgressPosition);
            if (moved >= DispatchRouteProgressDistance)
            {
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.LastProgressAt = now;
                unit.RouteRecoveryCount = 0;
                unit.NextDispatchRouteRecoveryAt = DateTime.MinValue;
                unit.DispatchRouteRecoveryExhaustionLogged = false;
                return false;
            }
            if (now < unit.LastProgressAt.AddSeconds(RouteStallTimeoutSeconds))
                return false;
            if (now < unit.NextDispatchRouteRecoveryAt)
                return true;

            LogRuntime("POLICE_BACKUP_DISPATCH_ROUTE_STALLED",
                "Operation=" + _operationId
                + "; Incident=" + (_incident == null ? "None" : _incident.Id)
                + "; UnitIndex=" + unit.UnitIndex
                + "; Vehicle=" + unit.Vehicle.Handle
                + "; Destination=" + destination
                + "; ProgressMeters=" + moved.ToString("0.0")
                + "; RequestedTask=TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE"
                + "; " + DescribeDispatchVehicleMovementEvidence(unit));

            Vector3 approach = ResolveDispatchVehicleApproach(_incident, destination);
            float approachDistance = unit.Vehicle.Position.DistanceTo(approach);
            if ((unit.RouteRecoveryCount > 0
                    || approachDistance <= Math.Max(45f, _settings.ArrivalRadius + 22f))
                && TryDeployDispatchUnitOnFoot(unit, approach, now))
                return true;

            if (unit.RouteRecoveryCount >= MaximumRouteRecoveries)
            {
                // Keep recovery bounded, but give each distinct validated
                // approach a real progress window before ending the vehicle
                // route. A nearby unit has already had the safe foot option.
                unit.LastProgressAt = now;
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.NextDispatchRouteRecoveryAt = now.AddSeconds(15);
                if (!unit.DispatchRouteRecoveryExhaustionLogged)
                {
                    unit.DispatchRouteRecoveryExhaustionLogged = true;
                    SetState(LSPDBackupAssignmentState.Recovering);
                    LogRuntime("POLICE_BACKUP_DISPATCH_ROUTE_RECOVERY_DELAYED",
                        "Operation=" + _operationId
                        + "; Incident=" + _incident.Id
                        + "; UnitIndex=" + unit.UnitIndex
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; Destination=" + destination
                        + "; RecoveryAttempts=" + unit.RouteRecoveryCount
                        + "; UnitPreserved=true"
                        + "; RetryAfterSeconds=15"
                        + "; Reason=BoundedAlternatesExhaustedAndFootApproachUnavailable");
                }
                return true;
            }

            Vector3 recoveryNode;
            float recoveryHeading;
            float recoveryDistance;
            string recoveryEvidence;
            if (TryFindConnectedDispatchRecoveryNode(
                unit, approach, out recoveryNode, out recoveryHeading,
                out recoveryDistance, out recoveryEvidence))
            {
                int nextRecovery = unit.RouteRecoveryCount + 1;
                if (!SendTo(unit, recoveryNode, true))
                {
                    unit.RouteRecoveryCount = nextRecovery;
                    unit.LastProgressAt = now;
                    unit.LastProgressPosition = unit.Vehicle.Position;
                    unit.NextDispatchRouteRecoveryAt = now.AddSeconds(
                        RouteStallTimeoutSeconds);
                    LogRuntime("POLICE_BACKUP_DISPATCH_ROUTE_RECOVERY_REJECTED",
                        "Operation=" + _operationId
                        + "; Incident=" + _incident.Id
                        + "; UnitIndex=" + unit.UnitIndex
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; Destination=" + recoveryNode
                        + "; Attempt=" + nextRecovery
                        + "; RouteConnectivity=Connected"
                        + "; DriveTask=Rejected"
                        + "; Evidence=" + recoveryEvidence);
                    return true;
                }

                unit.RouteRecoveryCount = nextRecovery;
                unit.LastProgressPosition = unit.Vehicle.Position;
                unit.LastProgressAt = now;
                unit.NextDispatchRouteRecoveryAt = now.AddSeconds(
                    RouteStallTimeoutSeconds);
                unit.DispatchRouteRecoveryDestinations.Add(recoveryNode);
                unit.DispatchRouteRecoveryExhaustionLogged = false;
                SetState(LSPDBackupAssignmentState.EnRoute);
                LogRuntime("POLICE_BACKUP_DISPATCH_ROUTE_RECOVERY",
                    "Operation=" + _operationId
                    + "; Incident=" + _incident.Id
                    + "; UnitIndex=" + unit.UnitIndex
                    + "; Vehicle=" + unit.Vehicle.Handle
                    + "; PreviousDestination=" + destination
                    + "; Destination=" + recoveryNode
                    + "; Attempt=" + nextRecovery
                    + "; RouteConnectivity=Connected"
                    + "; TravelDistance=" + recoveryDistance.ToString("0.0")
                    + "; Evidence=" + recoveryEvidence);
                return true;
            }

            if (TryDeployDispatchUnitOnFoot(unit, approach, now))
                return true;

            unit.RouteRecoveryCount++;
            unit.LastProgressAt = now;
            unit.LastProgressPosition = unit.Vehicle.Position;
            unit.NextDispatchRouteRecoveryAt = now.AddSeconds(
                RouteStallTimeoutSeconds);
            if (now >= unit.NextRejectedDriveTargetLogAt)
            {
                unit.NextRejectedDriveTargetLogAt = now.AddSeconds(5);
                LogRuntime("POLICE_BACKUP_DISPATCH_ROUTE_RECOVERY_REJECTED",
                    "Operation=" + _operationId
                    + "; Incident=" + _incident.Id
                    + "; UnitIndex=" + unit.UnitIndex
                    + "; Vehicle=" + unit.Vehicle.Handle
                    + "; Destination=" + destination
                    + "; Attempt=" + unit.RouteRecoveryCount
                    + "; RouteConnectivity=NotFound"
                    + "; UnitPreserved=true"
                    + "; Evidence=" + recoveryEvidence);
            }
            return true;
        }

        private bool TryFindConnectedDispatchRecoveryNode(
            BackupUnit unit,
            Vector3 sceneApproach,
            out Vector3 selectedNode,
            out float selectedHeading,
            out float selectedTravelDistance,
            out string evidence)
        {
            selectedNode = Vector3.Zero;
            selectedHeading = 0f;
            selectedTravelDistance = 0f;
            evidence = string.Empty;
            if (!IsUsable(unit) || !IsValidBackupRoutePosition(sceneApproach))
            {
                evidence = "Reason=InvalidUnitOrSceneApproach";
                return false;
            }

            Vector3 currentRoadNode;
            float currentHeading;
            if (!TryGetNearbyVehicleRoadNode(
                    unit.Vehicle.Position, unit.Vehicle.Position, 30f,
                    out currentRoadNode, out currentHeading))
            {
                evidence = "Reason=CurrentVehicleHasNoNearbyRoadNode";
                return false;
            }

            Vector2[] directions =
            {
                new Vector2(1f, 0f), new Vector2(-1f, 0f),
                new Vector2(0f, 1f), new Vector2(0f, -1f),
                new Vector2(0.707f, 0.707f), new Vector2(-0.707f, 0.707f),
                new Vector2(0.707f, -0.707f), new Vector2(-0.707f, -0.707f)
            };
            float[] rings = { 16f, 28f, 42f, 58f };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string lastFailure = "NoDifferentVehicleNode";
            foreach (float ring in rings)
                foreach (Vector2 direction in directions)
                {
                    Vector3 query = sceneApproach + new Vector3(
                        direction.X * ring, direction.Y * ring, 0f);
                    Vector3 candidate;
                    float candidateHeading;
                    if (!TryGetNearbyVehicleRoadNode(
                            query, sceneApproach, 20f,
                            out candidate, out candidateHeading))
                    {
                        lastFailure = "NoVehicleNodeNear=" + query;
                        continue;
                    }
                    string identity = Math.Round(candidate.X, 1) + ","
                        + Math.Round(candidate.Y, 1) + ","
                        + Math.Round(candidate.Z, 1);
                    if (!seen.Add(identity)
                        || candidate.DistanceTo(unit.Destination) < 12f
                        || (unit.DispatchRouteRecoveryDestinations != null
                            && unit.DispatchRouteRecoveryDestinations.Any(previous =>
                                previous.DistanceTo(candidate) < 12f))
                        || _units.Any(other => other != unit && IsUsable(other)
                            && (other.Destination.DistanceTo(candidate) < 18f
                                || other.Vehicle.Position.DistanceTo(candidate) < 18f)))
                    {
                        lastFailure = "CandidateIsRepeatedOrStacks=" + candidate;
                        continue;
                    }
                    if (candidate.DistanceTo(sceneApproach) > 65f
                        || Math.Abs(candidate.Z - sceneApproach.Z) > 12f)
                    {
                        lastFailure = "CandidateOutsideSceneApproach=" + candidate;
                        continue;
                    }

                    float startDistance;
                    string startEvidence;
                    if (!TryValidateDispatchRouteConnectivity(
                            currentRoadNode, candidate,
                            out startDistance, out startEvidence))
                    {
                        lastFailure = "CurrentToCandidate=" + startEvidence;
                        continue;
                    }
                    float approachDistance;
                    string approachEvidence;
                    if (!TryValidateDispatchRouteConnectivity(
                            candidate, sceneApproach,
                            out approachDistance, out approachEvidence))
                    {
                        lastFailure = "CandidateToScene=" + approachEvidence;
                        continue;
                    }

                    selectedNode = candidate;
                    selectedHeading = candidateHeading;
                    selectedTravelDistance = startDistance;
                    evidence = "CurrentRoadNode=" + currentRoadNode
                        + "; Candidate=" + candidate
                        + "; SceneApproach=" + sceneApproach
                        + "; CurrentToCandidate=" + startEvidence
                        + "; CandidateToScene=" + approachEvidence;
                    return true;
                }

            evidence = "Result=NoDifferentConnectedVehicleNode"
                + "; UnitIndex=" + unit.UnitIndex
                + "; Failure=" + lastFailure;
            return false;
        }

        private bool TryDeployDispatchUnitOnFoot(
            BackupUnit unit,
            Vector3 sceneApproach,
            DateTime now)
        {
            if (!IsUsable(unit) || unit.DispatchFootDeploymentIssued
                || !IsValidBackupRoutePosition(sceneApproach))
                return false;

            float sceneDistance = unit.Vehicle.Position.DistanceTo(sceneApproach);
            if (sceneDistance > Math.Max(45f, _settings.ArrivalRadius + 22f))
                return false;

            Vector3 safePosition;
            try
            {
                if (!World.GetSafePositionForPed(
                    sceneApproach, out safePosition, (GetSafePositionFlags)0)
                    || safePosition.DistanceTo(sceneApproach) > 18f)
                    return false;
                int interiorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    safePosition.X, safePosition.Y, safePosition.Z);
                if (_incident.SceneResolution != null
                    && _incident.SceneResolution.IsInteriorScene
                    && interiorId != 0)
                    return false;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_FOOT_APPROACH_PREFLIGHT_FAILED", ex);
                return false;
            }

            SetEmergencySignals(unit.Vehicle, false);
            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, true);
                foreach (Ped officer in SupportOfficers(unit))
                {
                    if (officer == null || !officer.Exists() || officer.IsDead
                        || !officer.IsInVehicle())
                        continue;
                    officer.Task.ClearAll();
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, officer, unit.Vehicle, 0);
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_FOOT_DEPLOYMENT_FAILED", ex);
                return false;
            }

            unit.Arrived = true;
            unit.DispatchFootDeploymentIssued = true;
            unit.LastProgressAt = now;
            unit.LastProgressPosition = unit.Vehicle.Position;
            unit.RouteRecoveryCount = 0;
            unit.NextDispatchRouteRecoveryAt = DateTime.MinValue;
            LogRuntime("POLICE_BACKUP_DISPATCH_VEHICLE_TO_FOOT_FALLBACK",
                "Operation=" + _operationId
                + "; Incident=" + _incident.Id
                + "; UnitIndex=" + unit.UnitIndex
                + "; Vehicle=" + unit.Vehicle.Handle
                + "; OfficerCount=" + SupportOfficers(unit).Count()
                + "; VehiclePosition=" + unit.Vehicle.Position
                + "; SceneApproach=" + sceneApproach
                + "; SafeFootPosition=" + safePosition
                + "; Distance=" + sceneDistance.ToString("0.0")
                + "; OfficersExitTaskIssued=true"
                + "; VehiclePreserved=true"
                + "; DriverRetained=true");
            return true;
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
            if (!IsValidBackupRoutePosition(destination))
            {
                if (unit.LastValidDriveDestination != Vector3.Zero)
                    unit.Destination = unit.LastValidDriveDestination;
                if (now >= unit.NextRejectedDriveTargetLogAt)
                {
                    unit.NextRejectedDriveTargetLogAt = now.AddSeconds(5);
                    LogRuntime("POLICE_BACKUP_DRIVE_TARGET_REJECTED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; Candidate=" + destination
                        + "; Reason=INVALID_OR_EMPTY_ROUTE_TARGET"
                        + "; PreservedLastValidDestination="
                        + unit.LastValidDriveDestination);
                }
                return false;
            }
            if (now < unit.NextRejectedDriveTargetRetryAt)
                return false;

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
                unit.NextRejectedDriveTargetRetryAt = now.AddSeconds(3);
                if (unit.LastValidDriveDestination != Vector3.Zero)
                    unit.Destination = unit.LastValidDriveDestination;
                if (now >= unit.NextRejectedDriveTargetLogAt)
                {
                    unit.NextRejectedDriveTargetLogAt = now.AddSeconds(5);
                    LogRuntime("POLICE_BACKUP_DRIVE_TARGET_REJECTED",
                        "Vehicle=" + unit.Vehicle.Handle
                        + "; Candidate=" + destination
                        + "; Reason=NO_VALID_VEHICLE_PATH_NODE"
                        + "; PreservedLastValidDestination="
                        + unit.LastValidDriveDestination
                        + "; RetryAfterSeconds=3");
                }
                return false;
            }
            bool dispatchSceneRoadRoute = IsDispatchRouteAssignment()
                && _state != LSPDBackupAssignmentState.StandingDown;
            bool dispatchPursuitRoadRoute = _incident != null
                && _incident.CurrentPursuitTarget != null
                && _convoy == null && _npcResponse == null
                && _gangResponse == null;
            if ((dispatchSceneRoadRoute || dispatchPursuitRoadRoute)
                && !PrepareDispatchVehicleForDriving(unit, now,
                    dispatchSceneRoadRoute ? "DispatchScene" : "DispatchPursuit"))
                return false;
            if (!force && dispatchSceneRoadRoute
                && unit.LastValidDriveDestination != Vector3.Zero
                && unit.LastValidDriveDestination.DistanceTo(routeDestination) <= 8f
                && unit.Driver != null && unit.Driver.Exists()
                && !unit.Driver.IsDead && unit.Driver.IsInVehicle(unit.Vehicle))
            {
                // Keep the current GTA road task while it is progressing.
                // Reissuing an identical DriveTo every five seconds resets
                // vehicle navigation and produced the observed crawling route.
                return true;
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
                if (npcCustodyRoadRoute || gangRoadRoute || dispatchPursuitRoadRoute
                    || dispatchSceneRoadRoute)
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
                            : dispatchSceneRoadRoute ? 25.0f
                            : (_npcTransportStarted ? 24.0f : 22.0f),
                        (int)drivingFlags,
                        gangRoadRoute ? 8.0f
                            : dispatchPursuitRoadRoute ? 8.0f
                            : dispatchSceneRoadRoute ? 8.0f
                            : (_npcTransportStarted ? 10.0f : 6.0f));
                }
                else
                    unit.Driver.Task.DriveTo(unit.Vehicle, routeDestination, speed,
                        drivingFlags, 18f);
                unit.Destination = routeDestination;
                unit.LastValidDriveDestination = routeDestination;
                unit.RouteHeading = routeHeading;
                unit.LastDriveTaskAt = now;
                unit.NextRejectedDriveTargetRetryAt = DateTime.MinValue;
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

        private bool PrepareDispatchVehicleForDriving(
            BackupUnit unit,
            DateTime now,
            string routeKind)
        {
            if (!IsUsable(unit))
                return false;

            int nativeDriverHandle;
            try
            {
                nativeDriverHandle = Function.Call<int>(
                    Hash.GET_PED_IN_VEHICLE_SEAT, unit.Vehicle, -1, false);
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_DRIVER_SEAT_QUERY_FAILED", ex);
                return false;
            }

            if (nativeDriverHandle != unit.Driver.Handle)
            {
                string recovery = "NoEntryTaskIssued";
                if (nativeDriverHandle == 0 && !unit.Driver.IsInVehicle()
                    && now >= unit.NextDispatchDriverSeatTaskAt)
                {
                    try
                    {
                        unit.Driver.Task.EnterVehicle(
                            unit.Vehicle, VehicleSeat.Driver, 12000, 1.0f);
                        unit.NextDispatchDriverSeatTaskAt = now.AddSeconds(3);
                        unit.LastProgressAt = now;
                        unit.LastProgressPosition = unit.Vehicle.Position;
                        unit.NextDispatchRouteRecoveryAt = now.AddSeconds(3);
                        recovery = "PhysicalDriverSeatEntryTaskIssued";
                    }
                    catch (Exception ex)
                    {
                        recovery = "DriverSeatEntryTaskFailed";
                        LogException("POLICE_BACKUP_DISPATCH_DRIVER_SEAT_TASK_FAILED", ex);
                    }
                }
                if (now >= unit.NextDispatchDriveEvidenceLogAt)
                {
                    unit.NextDispatchDriveEvidenceLogAt = now.AddSeconds(5);
                    LogRuntime("POLICE_BACKUP_DISPATCH_DRIVE_BLOCKED",
                        "Operation=" + _operationId
                        + "; UnitIndex=" + unit.UnitIndex
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; Route=" + routeKind
                        + "; AssignedDriver=" + unit.Driver.Handle
                        + "; NativeDriverSeat=" + nativeDriverHandle
                        + "; DriverInVehicle=" + unit.Driver.IsInVehicle()
                        + "; VehiclePosition=" + unit.Vehicle.Position
                        + "; VehicleSpeed=" + unit.Vehicle.Speed.ToString("0.00")
                        + "; Recovery=" + recovery);
                }
                return false;
            }

            if (!unit.Vehicle.IsDriveable)
            {
                if (now >= unit.NextDispatchDriveEvidenceLogAt)
                {
                    unit.NextDispatchDriveEvidenceLogAt = now.AddSeconds(5);
                    LogRuntime("POLICE_BACKUP_DISPATCH_DRIVE_BLOCKED",
                        "Operation=" + _operationId
                        + "; UnitIndex=" + unit.UnitIndex
                        + "; Vehicle=" + unit.Vehicle.Handle
                        + "; Route=" + routeKind
                        + "; AssignedDriver=" + unit.Driver.Handle
                        + "; NativeDriverSeat=" + nativeDriverHandle
                        + "; Driveable=false"
                        + "; VehiclePosition=" + unit.Vehicle.Position
                        + "; VehicleSpeed=" + unit.Vehicle.Speed.ToString("0.00")
                        + "; Recovery=VehiclePreservedForFootFallback");
                }
                return false;
            }

            bool engineRunning = false;
            bool engineStateKnown = false;
            try
            {
                Function.Call(Hash.FREEZE_ENTITY_POSITION, unit.Vehicle, false);
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON,
                    unit.Vehicle, true, true, false);
                Function.Call(Hash.SET_VEHICLE_BRAKE, unit.Vehicle, false);
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, unit.Vehicle, false);
                engineRunning = Function.Call<bool>(
                    Hash.GET_IS_VEHICLE_ENGINE_RUNNING, unit.Vehicle);
                engineStateKnown = true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_DRIVE_READINESS_FAILED", ex);
                return false;
            }

            if (now >= unit.NextDispatchDriveEvidenceLogAt)
            {
                unit.NextDispatchDriveEvidenceLogAt = now.AddSeconds(5);
                LogRuntime("POLICE_BACKUP_DISPATCH_DRIVE_READY",
                    "Operation=" + _operationId
                    + "; UnitIndex=" + unit.UnitIndex
                    + "; Vehicle=" + unit.Vehicle.Handle
                    + "; Route=" + routeKind
                    + "; AssignedDriver=" + unit.Driver.Handle
                    + "; NativeDriverSeat=" + nativeDriverHandle
                    + "; DriverSeatConfirmed=true"
                    + "; Driveable=" + unit.Vehicle.IsDriveable
                    + "; EngineRunning=" + (engineStateKnown
                        ? engineRunning.ToString() : "Unknown")
                    + "; UnfreezeRequested=true"
                    + "; BrakeReleased=true"
                    + "; HandbrakeReleased=true"
                    + "; VehiclePosition=" + unit.Vehicle.Position
                    + "; VehicleSpeed=" + unit.Vehicle.Speed.ToString("0.00"));
            }
            return true;
        }

        private static string DescribeDispatchVehicleMovementEvidence(
            BackupUnit unit)
        {
            if (unit == null || unit.Vehicle == null || !unit.Vehicle.Exists())
                return "Vehicle=Unavailable";

            string driverSeat = "Unknown";
            try
            {
                driverSeat = Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT,
                    unit.Vehicle, -1, false).ToString();
            }
            catch { }

            string engineRunning = "Unknown";
            try
            {
                engineRunning = Function.Call<bool>(
                    Hash.GET_IS_VEHICLE_ENGINE_RUNNING, unit.Vehicle).ToString();
            }
            catch { }

            int seatHandle;
            int.TryParse(driverSeat, out seatHandle);
            return "AssignedDriver=" + (unit.Driver == null || !unit.Driver.Exists()
                    ? 0 : unit.Driver.Handle)
                + "; NativeDriverSeat=" + driverSeat
                + "; DriverSeatMatches=" + (unit.Driver != null
                    && unit.Driver.Exists() && seatHandle == unit.Driver.Handle)
                + "; DriverInUnitVehicle=" + (unit.Driver != null
                    && unit.Driver.Exists() && unit.Driver.IsInVehicle(unit.Vehicle))
                + "; Driveable=" + unit.Vehicle.IsDriveable
                + "; EngineRunning=" + engineRunning
                + "; VehiclePosition=" + unit.Vehicle.Position
                + "; VehicleSpeed=" + unit.Vehicle.Speed.ToString("0.00");
        }

        private static bool IsValidBackupRoutePosition(Vector3 position)
        {
            return position != Vector3.Zero
                && !float.IsNaN(position.X) && !float.IsInfinity(position.X)
                && !float.IsNaN(position.Y) && !float.IsInfinity(position.Y)
                && !float.IsNaN(position.Z) && !float.IsInfinity(position.Z);
        }

        private static IEnumerable<Ped> SupportOfficers(BackupUnit unit)
        {
            return unit == null || unit.Officers == null
                ? Enumerable.Empty<Ped>()
                : unit.Officers.Where(ped => ped != null && ped.Exists());
        }

        internal bool IsCurrentDispatchPhysicalIntervention(
            LSPDDispatchEvent incident,
            Ped suspect,
            out string evidence)
        {
            evidence = "Result=NoAssignedBackupIntervention";
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || !ReferenceEquals(_incident, incident)
                || _state == LSPDBackupAssignmentState.None
                || _state == LSPDBackupAssignmentState.StandingDown
                || _convoy != null || _crimeActivity != null
                || _npcResponse != null || _gangResponse != null
                || (incident.BackupArrestInProgressSuspectHandles != null
                    && incident.BackupArrestInProgressSuspectHandles.Contains(
                        suspect.Handle)))
                return false;

            foreach (BackupUnit unit in _units)
            {
                if (!IsUsable(unit) || !unit.Arrived)
                    continue;

                bool assignedToSuspect =
                    unit.DispatchFootPursuitSuspectHandle == suspect.Handle
                    || unit.DispatchVehiclePursuitSuspectHandle == suspect.Handle
                    || (unit.DispatchGroupSceneSupport
                        && unit.DispatchGroupSceneSupportTargetHandle
                            == suspect.Handle);
                foreach (Ped officer in SupportOfficers(unit))
                {
                    if (officer == null || !officer.Exists() || officer.IsDead
                        || officer.Handle == suspect.Handle || officer.IsInVehicle())
                        continue;

                    bool officerEngaging = false;
                    bool suspectEngaging = false;
                    try
                    {
                        officerEngaging = officer.IsInCombatAgainst(suspect);
                        suspectEngaging = suspect.IsInCombatAgainst(officer);
                    }
                    catch { }

                    float distance = float.MaxValue;
                    try { distance = officer.Position.DistanceTo(suspect.Position); }
                    catch { }
                    bool assignedPhysicalInterception = assignedToSuspect
                        && !float.IsNaN(distance) && !float.IsInfinity(distance)
                        && distance <= DispatchOfficerContainmentStartDistance;
                    if (!officerEngaging && !suspectEngaging
                        && !assignedPhysicalInterception)
                        continue;

                    evidence = "Result=PhysicalIntervention"
                        + "; Incident=" + incident.Id
                        + "; Unit=" + (unit.Vehicle == null
                            || !unit.Vehicle.Exists() ? 0 : unit.Vehicle.Handle)
                        + "; Officer=" + officer.Handle
                        + "; Suspect=" + suspect.Handle
                        + "; OfficerInCombatAgainstSuspect=" + officerEngaging
                        + "; SuspectInCombatAgainstOfficer=" + suspectEngaging
                        + "; AssignedInterception=" + assignedToSuspect
                        + "; OfficerDistance=" + distance.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; Owner=Backup";
                    return true;
                }
            }

            return false;
        }

        internal bool IsCurrentDispatchSupportAttacker(
            LSPDDispatchEvent incident,
            Entity damageAttacker,
            Ped damageController)
        {
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || !ReferenceEquals(_incident, incident)
                || _state == LSPDBackupAssignmentState.None
                || _state == LSPDBackupAssignmentState.StandingDown
                || damageAttacker == null || !damageAttacker.Exists())
                return false;

            foreach (BackupUnit unit in _units)
            {
                if (unit == null)
                    continue;
                if (unit.Vehicle != null && unit.Vehicle.Exists()
                    && unit.Vehicle.Handle == damageAttacker.Handle)
                    return true;
                if (damageController != null && damageController.Exists()
                    && SupportOfficers(unit).Any(officer =>
                        officer.Handle == damageController.Handle))
                    return true;
            }
            return false;
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

        private bool IsActiveDispatchSupportOwner(
            LSPDDispatch dispatch,
            LSPDDispatchEvent incident)
        {
            return Active && _state != LSPDBackupAssignmentState.StandingDown
                && _convoy == null && _crimeActivity == null
                && _npcResponse == null && _gangResponse == null
                && dispatch != null && dispatch.HasIncident
                && incident != null && incident.OwnedByDispatch
                && !incident.HasConvoyCustodyHandoff
                && ReferenceEquals(dispatch.Current, incident)
                && ReferenceEquals(_incident, incident);
        }

        private bool IsOwnedDispatchBackupOfficer(Ped officer)
        {
            return officer != null && officer.Exists() && _units.Any(unit =>
                SupportOfficers(unit).Any(value => value.Handle == officer.Handle));
        }

        private bool IsDispatchSupportSuspect(
            LSPDDispatchEvent incident,
            Ped suspect,
            bool allowContained)
        {
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || suspect.Health <= 0 || incident.Suspects == null
                || !incident.Suspects.Any(value => value != null && value.Exists()
                    && value.Handle == suspect.Handle))
                return false;

            int handle = suspect.Handle;
            if ((incident.ArrestedSuspects != null
                    && incident.ArrestedSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == handle))
                || (incident.BackupArrestInProgressSuspectHandles != null
                    && incident.BackupArrestInProgressSuspectHandles.Contains(handle))
                || (incident.PlayerHandcuffInProgress
                    && incident.PlayerHandcuffTargetHandle == handle))
                return false;

            bool contained = incident.PursuitContainedSuspectHandles != null
                    && incident.PursuitContainedSuspectHandles.Contains(handle)
                || (incident.CompliantSuspects != null
                    && incident.CompliantSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == handle));
            return allowContained || !contained;
        }

        private void RepairDispatchPlayerTargetingOfficers(
            LSPDDispatch dispatch,
            Ped player,
            IEnumerable<Ped> officers)
        {
            if (dispatch == null || player == null || !player.Exists()
                || officers == null || !IsActiveDispatchSupportOwner(
                    dispatch, dispatch.Current))
                return;

            foreach (Ped officer in officers)
                ClearDispatchPlayerCombatTask(dispatch, player, officer);
        }

        private bool ClearDispatchPlayerCombatTask(
            LSPDDispatch dispatch,
            Ped player,
            Ped officer)
        {
            if (player == null || !player.Exists()
                || !IsOwnedDispatchBackupOfficer(officer)
                || officer.IsDead
                || !IsActiveDispatchSupportOwner(dispatch,
                    dispatch == null ? null : dispatch.Current))
                return false;

            bool targetingPlayer = false;
            try
            {
                int combatTarget = Function.Call<int>(
                    Hash.GET_PED_TARGET_FROM_COMBAT_PED, officer, true);
                targetingPlayer = officer.IsInCombatAgainst(player)
                    || ((officer.IsInCombat || officer.IsShooting)
                        && combatTarget == player.Handle);
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_PLAYER_TARGET_CHECK_FAILED", ex);
                return false;
            }

            if (!targetingPlayer)
            {
                _dispatchPlayerTargetingOfficerHandles.Remove(officer.Handle);
                return false;
            }

            try
            {
                officer.Task.ClearAll();
                if (_dispatchPlayerTargetingOfficerHandles.Add(officer.Handle))
                    LogRuntime("POLICE_BACKUP_DISPATCH_FRIENDLY_TARGET_CORRECTED",
                        "Incident=" + dispatch.Current.Id
                        + "; Officer=" + officer.Handle
                        + "; Player=" + player.Handle
                        + "; CombatTargetBefore=" + player.Handle
                        + "; Action=ClearedBeforeDispatchRetask");
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_FRIENDLY_TARGET_CLEAR_FAILED", ex);
                return false;
            }
        }

        private bool CanPrepareDispatchSuspectTask(
            LSPDDispatch dispatch,
            Ped player,
            Ped officer,
            Ped suspect,
            bool allowContained)
        {
            ClearDispatchPlayerCombatTask(dispatch, player, officer);
            LSPDDispatchEvent incident = dispatch == null ? null : dispatch.Current;
            if (!IsActiveDispatchSupportOwner(dispatch, incident)
                || !IsOwnedDispatchBackupOfficer(officer)
                || officer.IsDead || officer.IsInVehicle()
                || !IsDispatchSupportSuspect(
                    incident, suspect, allowContained))
                return false;

            bool friendly = _authority != null
                && _authority.EnsurePoliceBackupFriendliness(player, officer);
            if (!friendly)
            {
                if (_dispatchUnfriendlyOfficerHandles.Add(officer.Handle))
                    LogRuntime("POLICE_BACKUP_DISPATCH_SUPPORT_TASK_WITHHELD",
                        "Incident=" + incident.Id
                        + "; Officer=" + officer.Handle
                        + "; Player=" + (player == null ? 0 : player.Handle)
                        + "; Suspect=" + suspect.Handle
                        + "; Reason=BidirectionalPoliceRelationshipNotVerified");
                return false;
            }
            _dispatchUnfriendlyOfficerHandles.Remove(officer.Handle);

            return !IsDispatchPlayerCombatTarget(officer, player);
        }

        private static bool IsDispatchPlayerCombatTarget(Ped officer, Ped player)
        {
            if (officer == null || player == null || !officer.Exists()
                || !player.Exists())
                return false;
            try
            {
                int combatTarget = Function.Call<int>(
                    Hash.GET_PED_TARGET_FROM_COMBAT_PED, officer, true);
                return officer.IsInCombatAgainst(player)
                    || ((officer.IsInCombat || officer.IsShooting)
                        && combatTarget == player.Handle);
            }
            catch
            {
                return false;
            }
        }

        private bool AimAtDispatchThreat(
            LSPDDispatch dispatch,
            Ped player,
            Ped officer,
            Ped suspect)
        {
            if (!CanPrepareDispatchSuspectTask(
                    dispatch, player, officer, suspect, true)
                || officer.IsInCombat || officer.IsShooting)
                return false;
            try
            {
                Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY,
                    officer, suspect, 6000, true);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_AIM_TASK_FAILED", ex);
                return false;
            }
        }

        private bool AssignDispatchCombat(
            LSPDDispatch dispatch,
            Ped player,
            Ped officer,
            Ped suspect)
        {
            if (!CanPrepareDispatchSuspectTask(
                    dispatch, player, officer, suspect, false))
                return false;
            try
            {
                officer.Task.Combat(suspect);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_COMBAT_TASK_FAILED", ex);
                return false;
            }
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
                if (!ReferenceEquals(unit, _npcTransportUnit))
                    ReleaseNpcCitizenCustodyProtection(
                        unit, "BackupUnitUnavailable");
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

        private string ResolveContextBackupOfficerModel(
            bool preferSavedFavorite,
            string preferredModelName,
            bool citizenInteraction)
        {
            string fallback = ResolveOfficerModel(preferSavedFavorite, preferredModelName);
            if (_profile == null || !preferSavedFavorite || !_settings.PreferSavedFavoritePed
                || string.IsNullOrWhiteSpace(preferredModelName))
                return fallback;

            List<string> favoriteModels = _profile.FavoriteBackupPeds
                .Where(favorite => favorite != null
                    && !string.IsNullOrWhiteSpace(favorite.ModelName))
                .Select(favorite => favorite.ModelName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (favoriteModels.Count == 0)
                return fallback;

            int preferredIndex = favoriteModels.FindIndex(modelName =>
                string.Equals(
                    modelName,
                    preferredModelName,
                    StringComparison.OrdinalIgnoreCase));
            if (preferredIndex < 0)
                return fallback;

            string lastOfficerModel = citizenInteraction
                ? _lastCitizenBackupOfficerModelName
                : _lastDispatchCrimeBackupOfficerModelName;
            string lastPreferenceModel = citizenInteraction
                ? _lastCitizenBackupPreferenceModelName
                : _lastDispatchCrimeBackupPreferenceModelName;
            int lastOfficerIndex = favoriteModels.FindIndex(modelName =>
                string.Equals(
                    modelName,
                    lastOfficerModel,
                    StringComparison.OrdinalIgnoreCase));

            // Honor a newly chosen context preference immediately. When the
            // preference stays the same, advance through its saved shared
            // favorite pool once per new assignment so separate incidents do
            // not always show the same Backup officer.
            int selectedIndex = preferredIndex;
            bool preferenceChanged = !string.Equals(
                preferredModelName,
                lastPreferenceModel,
                StringComparison.OrdinalIgnoreCase);
            if (!preferenceChanged && lastOfficerIndex >= 0)
                selectedIndex = (lastOfficerIndex + 1) % favoriteModels.Count;

            string selectedModel = favoriteModels[selectedIndex];
            if (citizenInteraction)
            {
                _lastCitizenBackupOfficerModelName = selectedModel;
                _lastCitizenBackupPreferenceModelName = preferredModelName;
            }
            else
            {
                _lastDispatchCrimeBackupOfficerModelName = selectedModel;
                _lastDispatchCrimeBackupPreferenceModelName = preferredModelName;
            }
            return selectedModel;
        }

        private string ResolveDispatchGroupOfficerModel(
            int unitIndex,
            string firstModel)
        {
            if (!_dispatchUsesSavedFavoriteModels || _profile == null
                || string.IsNullOrWhiteSpace(firstModel))
                return firstModel ?? string.Empty;

            List<string> favoriteModels = _profile.FavoriteBackupPeds
                .Where(favorite => favorite != null
                    && !string.IsNullOrWhiteSpace(favorite.ModelName))
                .Select(favorite => favorite.ModelName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (favoriteModels.Count < 2)
                return firstModel;

            int firstModelIndex = favoriteModels.FindIndex(modelName =>
                string.Equals(modelName, firstModel,
                    StringComparison.OrdinalIgnoreCase));
            if (firstModelIndex < 0)
                return firstModel;

            return favoriteModels[(firstModelIndex + Math.Max(0, unitIndex))
                % favoriteModels.Count];
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
            ReleaseDispatchGroupOfficerModelRequests();
        }

        private void ReleaseDispatchGroupOfficerModelRequests()
        {
            foreach (string modelName in _dispatchGroupRequestedOfficerModels.ToArray())
            {
                if (!string.IsNullOrWhiteSpace(modelName))
                    ReleaseModel(new Model(modelName));
            }
            _dispatchGroupRequestedOfficerModels.Clear();
        }

        private void Fail(string reason)
        {
            LSPDNPCResponse linkedNpcResponse = _npcResponse;
            string operationId = _operationId ?? string.Empty;
            string incidentId = _incident == null
                ? string.Empty : _incident.Id ?? string.Empty;
            int unitsCreated = _units.Count;
            bool backupUnitEverCreated = _backupUnitCreatedForOperation;
            SetState(LSPDBackupAssignmentState.Failed);
            LogRuntime("POLICE_BACKUP_OPERATION_FAILED",
                "Operation=" + operationId
                + "; Incident=" + incidentId
                + "; UnitsCreatedAtFailure=" + unitsCreated
                + "; BackupUnitEverCreated=" + backupUnitEverCreated
                + "; Reason=" + (reason ?? string.Empty));
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
        Recovering,
        Arrived,
        Supporting,
        Intercepting,
        Securing,
        AssignmentComplete,
        StandingDown,
        Failed
    }
}
