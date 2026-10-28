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
    /// Owns the physical custody continuation after Dispatch has arrested a
    /// suspect or a group. Convoy never fakes custody by creating a van at the
    /// player or inserting prisoners into it: a staged transport first drives
    /// to the location, prisoners walk and enter through GTA's task system,
    /// the Player or transport crew completes the station leg, and a second
    /// prison unit repeats that physical handoff before booking at Bolingbroke.
    /// </summary>
    internal sealed class LSPDConvoy
    {
        private const int HandcuffedPedConfigFlag = 120;
        private const int ModelPreparationTimeoutSeconds = 20;
        private const int DriverTaskRefreshMilliseconds = 5000;
        private const int DriverReleaseTimeoutSeconds = 12;
        private const int DispatchCrewDriverSeatCheckMilliseconds = 500;
        private const int DispatchCrewDriverEntryTaskRefreshMilliseconds = 5000;
        private const int DispatchCrewDriverEntryTimeoutSeconds = 30;
        private const int DispatchCrewRouteRecoveryCheckMilliseconds = 1000;
        private const int DispatchCrewArrivalCheckMilliseconds = 500;
        private const int DispatchReleaseTaskRefreshMilliseconds = 2500;
        private const int DispatchReleaseTimeoutSeconds = 15;
        private const float DispatchReleaseStoppedSpeed = 0.5f;
        private const int PrisonerTaskRefreshMilliseconds = 900;
        // Vehicle entry is a multi-step GTA animation. Reissuing TASK_ENTER_VEHICLE
        // while that animation is still active cancels the escort's first attempt
        // and leaves the prisoner circling/struggling at the open door. Keep one
        // task owned by the escort for its full bounded window, then permit a
        // controlled retry if the game genuinely failed to complete it.
        private const int PrisonerVehicleEntryRefreshMilliseconds = 15000;
        private const int DispatchCustodyDoorOpenTaskTimeoutMilliseconds = 12000;
        private const int DispatchCustodyDoorOpenRetryMilliseconds = 15000;
        private const int DispatchSceneDoorAnimationFallbackGraceMilliseconds = 4000;
        // Match the bounded physical-entry task window. Reissuing an exit
        // task at the general 900 ms prisoner-task cadence interrupts GTA's
        // door/seat exit animation and leaves the prisoner inside the van.
        private const int PrisonerVehicleExitRefreshMilliseconds = 15000;
        private const int PrisonerAssignedSeatCheckRefreshMilliseconds = 500;
        private const int PrisonerMaintenanceRefreshMilliseconds = 1000;
        // GTA can briefly invalidate a streamed Ped wrapper while the same
        // mission entity remains managed. Dispatch custody keeps ownership of
        // the handle and reconciles it before treating one sample as loss.
        private const int DispatchPrisonerMissingConfirmationMilliseconds = 1500;
        private const int DispatchPrisonerHandleRetryMilliseconds = 500;
        private const int TransportOfficerReleaseTimeoutSeconds = 12;
        private const int TransportOfficerTaskRefreshMilliseconds = 2500;
        private const int GroundedPrisonerFollowRefreshMilliseconds = 6000;
        private const int DispatchMovementClipsetRequestRefreshMilliseconds = 1000;
        private const int PrisonerEscortSettleMilliseconds = 1600;
        private const int PrisonerArrestTaskMilliseconds = 6500;
        private const int PrisonerEscortAlignmentMilliseconds = 650;
        private const int PrisonerDoorOpenSettleMilliseconds = 900;
        private const int PrisonerCuffPoseRefreshMilliseconds = 2500;
        private const int ActiveCustodyMaintenanceRefreshMilliseconds = 1000;
        // Player custody is a deliberate multi-step interaction. Give the
        // paired arrest animation time to finish and give the player enough
        // time to walk the subject to the marked rear door without allowing
        // the ordinary loading timeout to cancel a valid handoff.
        private const int PlayerHandcuffAnimationMilliseconds = 6500;
        private const int PlayerLoadingContactMilliseconds = 1200;
        private const int PlayerUnloadingInteractionTimeoutSeconds = 20;
        private const int PlayerEscortTimeoutSeconds = 150;
        private const int PlayerEscortReleaseGraceSeconds = 10;
        private const float PlayerEscortMaximumDistance = 35.0f;
        private const float MaximumDispatchPreferredUtilityDistance = 150.0f;
        private const float PlayerEscortFollowDistance = 2.0f;
        private const float DispatchEscortOfficerTrailingOffset = -1.15f;
        private const float PlayerCustodyInteractionRadius = 4.0f;
        private const float PlayerCustodyVehicleRadius = 3.2f;
        private const float PlayerCustodyLoadInteractionRadius = 8.0f;
        private const float TransportOfficerContactRadius = 1.35f;
        private const float PhysicalEscortContactRadius = 1.9f;
        // Give an officer who is already at the custody scene a short first
        // opportunity to perform the visible E handoff. If the player is not
        // at the scene, or does not take custody during this window, the
        // existing transport-officer fallback is allowed to continue.
        private const int PlayerCustodyOfferSeconds = 20;
        private const float PlayerCustodyOfferRadius = 14.0f;
        // A blocked road should not discard a live transport that is already
        // close enough for its custody officer to walk the remaining distance.
        // The limit is deliberately bounded so a genuinely distant unit still
        // follows the normal recoverable-failure path.
        private const float MinimumNearbyTransportCustodyRadius = 60.0f;
        private const float MaximumNearbyTransportCustodyRadius = 95.0f;
        private const float MaximumDispatchSceneVehicleDistance = 100.0f;
        // A transport is parked at the verified road access point and its
        // officers walk into the scene. Requiring the van to hit a four-metre
        // centre around that node made GTA circle kerbs and alley obstacles
        // even though the unit had already reached usable roadside ground.
        private const float DispatchSceneVehicleArrivalRadius = 10.0f;
        // A roadside pickup often starts from a sidewalk, store frontage, or
        // alley mouth. GTA's direct closest-node query can reject its one
        // closest result even when a normal road is nearby. Four fixed,
        // horizontal query offsets give the Dispatch-only resolver alternate
        // bounded samples without searching the map or changing Convoy routes.
        private static readonly Vector3[] DispatchSceneRoadNodeQueryOffsets =
        {
            new Vector3(24f, 0f, 0f),
            new Vector3(-24f, 0f, 0f),
            new Vector3(0f, 24f, 0f),
            new Vector3(0f, -24f, 0f)
        };
        private const int EscortStallTimeoutSeconds = 8;
        private const int EscortRecoveryCooldownSeconds = 8;
        private const float EscortProgressDistance = 0.75f;
        private const int TransportRouteStallTimeoutSeconds = 12;
        private const int MaximumTransportRouteRecoveries = 3;
        private const int MaximumDispatchTransportStagingCandidates = 8;
        private const float DispatchTransportRoadSnapRadius = 18.0f;
        private const float DispatchTransportTargetRoadSnapRadius = 45.0f;
        private const float DispatchTransportVehicleClearanceRadius = 9.0f;
        private const float DispatchTransportPedClearanceRadius = 4.0f;
        private const float DispatchTransportSpawnPlacementTolerance = 6.0f;
        private const float DispatchTransportGroundedHorizontalTolerance = 2.0f;
        private const float DispatchTransportVehicleOriginAboveRoadLimit = 2.0f;
        private const float DispatchTransportRoadNodeHeightTolerance = 1.5f;
        private const float DispatchTransportLaneHeadingTolerance = 55.0f;
        private const float MaximumDispatchTransportRouteDistance = 800.0f;
        private const int ArrivalRadius = 18;
        private const float StationHandoffArrivalRadius = 6.0f;
        private const float StationHandoffStoppedSpeed = 0.25f;
        private const float StationHandoffPreparationRadius = 100.0f;
        private const float StationHandoffMinimumRoadOffset = 5.0f;
        private const float StationHandoffMaximumRoadOffset = 18.0f;
        private const float StationHandoffMaximumMarkerOffset = 24.0f;
        private const float StationHandoffDoorAccessClearance = 1.15f;
        private const float StationHandoffPedClearance = 1.25f;
        private const float StationHandoffWalkRouteStep = 1.5f;
        private const float StationHandoffGroundProbeHeight = 10.0f;
        private const int DefaultDeferredCleanupGraceSeconds = 8;
        private const int DefaultDeferredCleanupMaximumSeconds = 60;
        private const int DeferredCleanupCheckMilliseconds = 1000;
        private const float DefaultCleanupDistance = 200f;
        private const float DispatchCrewDepartureConfirmationDistance = 25.0f;
        private const int DispatchCrewDepartureConfirmationMinimumSeconds = 2;
        private const int DispatchCrewDepartureConfirmationMaximumSeconds = 45;
        private const float MaximumRoadElevationDifference = 8f;
        private const float DispatchPickupClusterRadius = 35.0f;
        private const float DispatchPickupSceneClusterRadius = 75.0f;

        private enum CustodyPhase
        {
            None,
            PreparingRequestedPrisoner,
            PreparingSceneTransport,
            SceneTransportEnRoute,
            SceneLoading,
            DispatchPickupDriverBoarding,
            DispatchCrewDriveToPickup,
            DispatchGroupPickupWaiting,
            DispatchStationTransportDecision,
            DispatchCrewDriverBoarding,
            DispatchCrewDriveToStation,
            DriveToStation,
            StationUnloading,
            HoldingAtStation,
            PreparingPrisonTransport,
            PrisonTransportEnRoute,
            PrisonLoading,
            DriveToPrison,
            PrisonHandoff,
            Completed,
            Failed
        }

        private enum RouteThreatState
        {
            None,
            PreparingAssets,
            Staged,
            Resolved,
            Skipped
        }

        private enum PrisonerLoadingStage
        {
            EscortApproach,
            SecuringContact,
            Escorting,
            VehicleDoorApproach,
            VehicleDoorReached,
            DoorOpened,
            Loading,
            Loaded
        }

        private sealed class DeferredCleanup
        {
            internal Ped Ped;
            internal Vehicle Vehicle;
            internal IList<Ped> RelatedPeds;
            internal DateTime Earliest;
            internal DateTime Expires;
        }

        private sealed class DispatchPickupStop
        {
            internal Vector3 SceneTarget;
            internal Vector3 RoadTarget;
            internal bool Completed;
            internal readonly HashSet<int> PrisonerHandles = new HashSet<int>();
        }

        private readonly LSPDAudioDispatch _audio;
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveLog _log;
        private readonly LSPDControlBindings _controls;
        private readonly LSPoliceConvoySettings _settings;
        private readonly LSPoliceCleanupSettings _cleanupSettings;
        private readonly LSUniversalUiSettings _uiSettings;
        private readonly List<Ped> _prisoners = new List<Ped>();
        private readonly List<DispatchPickupStop> _dispatchPickupStops =
            new List<DispatchPickupStop>();
        private readonly HashSet<int> _dispatchCustodyTransferredHandles =
            new HashSet<int>();
        private readonly HashSet<int> _dispatchGroupLoadedPendingHandoff =
            new HashSet<int>();
        private readonly Dictionary<int, DateTime> _dispatchGroupLastRoadAttemptAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, Ped> _dispatchGroupBackupEscorts =
            new Dictionary<int, Ped>();
        private readonly HashSet<int> _dispatchGroupBackupLoaderWaitLogged =
            new HashSet<int>();
        private readonly List<Ped> _sceneEscortOfficers = new List<Ped>();
        private readonly List<Ped> _stationHandoffOfficers = new List<Ped>();
        private readonly List<Ped> _prisonHandoffOfficers = new List<Ped>();
        private readonly List<Ped> _routeThreatPeds = new List<Ped>();
        private readonly List<DeferredCleanup> _deferredCleanup = new List<DeferredCleanup>();
        // GTA animations and path tasks need time to advance. Reissuing a walk,
        // exit, or vehicle-entry task every frame was one source of stiff
        // prisoners and interrupted animations in the earlier implementation.
        private readonly Dictionary<int, DateTime> _lastPrisonerTaskAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastPrisonerVehicleEntryAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastPrisonerVehicleExitAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastAssignedSeatCheckAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, bool> _lastAssignedSeatCheckResult = new Dictionary<int, bool>();
        private readonly Dictionary<int, LSPDSuspectInjuryState> _prisonerInjuryStates =
            new Dictionary<int, LSPDSuspectInjuryState>();
        private readonly Dictionary<int, Ped> _prisonerEscortOfficers = new Dictionary<int, Ped>();
        private readonly Dictionary<int, DateTime> _prisonerEscortStartedAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastEscortOfficerTaskAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, Vector3> _lastPrisonerEscortPosition = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, Vector3> _lastEscortOfficerPosition = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, DateTime> _lastPrisonerEscortProgressAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastPrisonerEscortRecoveryAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, PrisonerLoadingStage> _prisonerLoadingStages =
            new Dictionary<int, PrisonerLoadingStage>();
        private readonly Dictionary<int, DateTime> _prisonerLoadingStageStartedAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _prisonerLoadingTimeoutStartedAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastGroundedEscortFollowAt =
            new Dictionary<int, DateTime>();
        private readonly HashSet<int> _prisonerEscortAlignmentStarted = new HashSet<int>();
        private readonly HashSet<int> _prisonerEscortApproachLogged = new HashSet<int>();
        private readonly Dictionary<int, Ped> _prisonerUnloadingOfficers = new Dictionary<int, Ped>();
        private readonly Dictionary<int, DateTime> _lastPrisonerCuffPoseAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _playerHandcuffAnimationUntil = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _playerLoadingContactStartedAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _playerEscortLostAt = new Dictionary<int, DateTime>();
        private readonly HashSet<int> _prisonerVehicleDoorsOpened = new HashSet<int>();
        private readonly HashSet<int> _prisonerDoorCloseTasksIssued = new HashSet<int>();
        private readonly Dictionary<int, DateTime> _prisonerDoorOpenTaskIssuedAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _dispatchPrisonerMissingSince =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _dispatchPrisonerLastHandleRetryAt =
            new Dictionary<int, DateTime>();
        private readonly HashSet<int> _dispatchSceneDoorAnimationFallbackIssued =
            new HashSet<int>(16);
        private readonly HashSet<int> _prisonerEntryTasksIssued = new HashSet<int>();
        private readonly HashSet<int> _physicalCuffReadFailuresLogged = new HashSet<int>();
        private readonly HashSet<int> _prisonerLoadedLogIssued = new HashSet<int>();
        // The player is not added to an owned officer collection because the
        // player must never be cleaned up by Convoy. These handles only mark
        // prisoners whose physical escort has explicitly been claimed with E.
        private readonly HashSet<int> _playerEscortPrisoners = new HashSet<int>();
        private readonly HashSet<int> _playerEscortFallbacks = new HashSet<int>();
        private readonly HashSet<int> _playerDoorInteractions = new HashSet<int>();
        private readonly HashSet<int> _playerEntryInteractions = new HashSet<int>();
        // Unloading has a separate choreography from scene loading. Keeping
        // its timestamps/door ownership separate prevents the station or
        // prison handoff from inheriting a stale scene-entry task.
        private readonly Dictionary<int, DateTime> _prisonerUnloadingEscortStartedAt =
            new Dictionary<int, DateTime>();
        private readonly HashSet<int> _prisonerUnloadingDoorsOpened = new HashSet<int>();
        private readonly Dictionary<int, DateTime> _playerUnloadingStartedAt =
            new Dictionary<int, DateTime>();
        private readonly HashSet<int> _playerUnloadingDoorInteractions = new HashSet<int>();
        private readonly HashSet<int> _playerUnloadingTasksIssued = new HashSet<int>();
        private readonly HashSet<int> _playerUnloadingFallbackHandles = new HashSet<int>();
        private readonly Dictionary<int, bool> _custodyInvincibilityStates = new Dictionary<int, bool>();
        private readonly HashSet<int> _dispatchCustodyMovementStyleApplied = new HashSet<int>();
        private readonly Dictionary<int, DateTime> _dispatchCustodyMovementStyleRequestedAt =
            new Dictionary<int, DateTime>();

        private Vehicle _transport;
        private Ped _transportDriver;
        private Ped _dispatchSceneTransportDriver;
        private Blip _transportBlip;
        private Blip _custodyOfficerBlip;
        private Blip _custodyPrisonerBlip;
        private Blip _prisonDestinationBlip;
        private int _custodyOfficerBlipHandle;
        private int _custodyPrisonerBlipHandle;
        private Vehicle _routeThreatVehicle;
        private LSPDDispatchState _state = LSPDDispatchState.None;
        private CustodyPhase _phase = CustodyPhase.None;
        private Vector3 _pickupTarget;
        private Vector3 _dispatchSceneVehicleAccessTarget;
        private float _dispatchSceneWalkInDistanceMeters;
        private Vector3 _stationDestination;
        private Vector3 _stationIntakeDestination;
        private Vector3 _stationHandoffMarkerPosition;
        private Vector3 _stationHandoffGroundPosition;
        private Vector3 _prisonDestination;
        private string _stationDisplayName = string.Empty;
        private string _prisonDisplayName = string.Empty;
        private string _pendingVehicleModelName = string.Empty;
        private string _pendingOfficerModelName = string.Empty;
        private string _pendingPrisonerModelName = string.Empty;
        private string _requestedPrisonerProfileId = string.Empty;
        private string _routeThreatModelName = string.Empty;
        private string _routeThreatVehicleModelName = string.Empty;
        private string _routeThreatWeaponName = string.Empty;
        private DateTime _preparationDeadline = DateTime.MinValue;
        private DateTime _phaseStartedAt = DateTime.MinValue;
        private DateTime _lastDriverTaskAt = DateTime.MinValue;
        private DateTime _lastTransportProgressAt = DateTime.MinValue;
        private Vector3 _lastTransportProgressPosition = Vector3.Zero;
        private Vector3 _lastTransportRoadTaskTarget = Vector3.Zero;
        private DateTime _nextTransportDriveReadinessLogAt = DateTime.MinValue;
        private DateTime _nextTransportDriverSeatTaskAt = DateTime.MinValue;
        private Vector3 _dispatchTransportRouteRoadTarget = Vector3.Zero;
        private Vector3 _dispatchTransportRouteDestination = Vector3.Zero;
        private string _lastDispatchTransportRouteTargetFailure = string.Empty;
        private int _transportRouteRecoveryCount;
        private DateTime _lastPrisonerMaintenanceAt = DateTime.MinValue;
        private DateTime _lastDeferredCleanupAt = DateTime.MinValue;
        private DateTime _driverReleaseRequestedAt = DateTime.MinValue;
        private bool _driverReleaseRequested;
        private DateTime _lastDispatchCrewDriverSeatCheckAt = DateTime.MinValue;
        private DateTime _lastDispatchCrewDriverEntryTaskAt = DateTime.MinValue;
        private DateTime _dispatchCrewDriverEntryStartedAt = DateTime.MinValue;
        private DateTime _lastDispatchCrewRouteRecoveryCheckAt = DateTime.MinValue;
        private DateTime _lastDispatchCrewArrivalCheckAt = DateTime.MinValue;
        private DateTime _lastDispatchCrewStationRouteProgressAt = DateTime.MinValue;
        private DateTime _dispatchCrewOfficerEntryTaskAt = DateTime.MinValue;
        private float _lastDispatchCrewStationGoalDistance = float.MaxValue;
        private Vector3 _dispatchCrewRouteRecoveryTarget = Vector3.Zero;
        private Vector3 _dispatchCrewDepartureOrigin = Vector3.Zero;
        private DateTime _dispatchCrewDepartureStartedAt = DateTime.MinValue;
        private int _dispatchCrewDriverSeatOccupantHandle;
        private int _dispatchCrewOfficerEntryTaskHandle;
        private int _activeDispatchPickupStopIndex = -1;
        private DateTime _lastDispatchPickupDriverEntryTaskAt = DateTime.MinValue;
        private DateTime _dispatchPickupDriverEntryStartedAt = DateTime.MinValue;
        private DateTime _lastDispatchPickupArrivalCheckAt = DateTime.MinValue;
        private bool _dispatchPickupDriverConflictLogged;
        private bool _dispatchCrewStationTransport;
        private bool _dispatchCrewCustodyHandoffReady;
        private bool _dispatchClosedAfterCrewCustodyHandoff;
        private bool _dispatchCrewExitStagingAttempted;
        private bool _dispatchCrewExitStagingPending;
        private bool _dispatchCrewExitStagingReached;
        private bool _dispatchCrewOfficerSeatConflictLogged;
        private bool _dispatchCrewDriverSeatConflictLogged;
        private bool _dispatchCrewPlayerRecoveryPending;
        private string _dispatchCrewPlayerRecoveryReason = string.Empty;
        private bool _dispatchGroupPlayerDrivenPickup;
        private bool _dispatchGroupCollectionAwaitingMore;
        private bool _dispatchGroupStationDeclineLogged;
        private bool _dispatchGroupPlayerRoutePromptLogged;
        private int _dispatchGroupExpectedPrisonerCount;
        private DateTime _transportOfficerReleaseRequestedAt = DateTime.MinValue;
        private DateTime _nextTransportOfficerReleaseTaskAt = DateTime.MinValue;
        private DateTime _lastActiveCustodyMaintenanceAt = DateTime.MinValue;
        private bool _transportOfficerReleaseRequested;
        private bool _transportOfficerReleaseRecoveryLogged;
        private bool _transportNearCustodyFallback;
        private bool _playerCustodyKeyDown;
        private bool _playerOwnedTransport;
        private VehicleSeat _playerVehicleCustodySeat = VehicleSeat.LeftRear;
        private int _pendingDispatchReleaseHandle;
        private DateTime _dispatchReleaseRequestedAt = DateTime.MinValue;
        private DateTime _nextDispatchReleaseTaskAt = DateTime.MinValue;
        private int _dispatchReleaseTaskAttempts;
        private Ped _dispatchReleasedPrisoner;
        private bool _completedAfterDispatchRelease;
        private bool _playerCustodyOfferNotified;
        private bool _playerUnloadingOfferNotified;
        private bool _stationHandoffSearchMessageShown;
        private bool _stationHandoffTargetMessageShown;
        private bool _stationHandoffStopMessageShown;
        private DateTime _lastStationHandoffSearchAt = DateTime.MinValue;
        private Vector3 _stationHandoffReceivingPosition = Vector3.Zero;
        private float _stationHandoffHeading;
        private string _stationHandoffProgressEvidence = string.Empty;
        private bool _stationHandoffStallMessageShown;
        private bool _stationHandoffAccessMessageShown;
        private bool _stationHandoffActualPoseValidated;
        private bool _stationHandoffAuthoredGroundFallbackUsed;
        private bool _stationHandoffUsedGarageFallback;
        private Vector3 _stationHandoffActualPosePosition = Vector3.Zero;
        private float _stationHandoffActualPoseHeading;
        private bool _stationHandoffArrivalOrdersIssued;
        private bool _prisonHandoffArrivalOrdersIssued;
        private bool _terminalKeyDown;
        private bool _hadPreviousWaypoint;
        private Vector3 _previousWaypoint = Vector3.Zero;
        private string _operationId = string.Empty;
        private string _activeAudioScope = string.Empty;
        private string _lastAudioStage = string.Empty;
        private string _lastFailureReason = string.Empty;
        private bool _failedIsRecoverable;
        private bool _failedAfterStationHandoff;
        private bool _isRequestedConvoyActivity;
        private bool _dispatchCustodyOperation;
        private RouteThreatState _routeThreatState = RouteThreatState.None;
        private DateTime _routeThreatPreparationDeadline = DateTime.MinValue;
        private DateTime _lastRouteThreatCombatTaskAt = DateTime.MinValue;

        internal LSPDConvoy(LSPDAudioDispatch audio)
            : this(audio, null, null, null, null, null, null)
        {
        }

        internal LSPDConvoy(
            LSPDAudioDispatch audio,
            LSPDProfile profile,
            LSImmersiveLog log)
            : this(audio, profile, log, null, null, null, null)
        {
        }

        internal LSPDConvoy(
            LSPDAudioDispatch audio,
            LSPDProfile profile,
            LSImmersiveLog log,
            LSPDControlBindings controls,
            LSPoliceConvoySettings settings,
            LSPoliceCleanupSettings cleanupSettings)
            : this(audio, profile, log, controls, settings, cleanupSettings, null)
        {
        }

        internal LSPDConvoy(
            LSPDAudioDispatch audio,
            LSPDProfile profile,
            LSImmersiveLog log,
            LSPDControlBindings controls,
            LSPoliceConvoySettings settings,
            LSPoliceCleanupSettings cleanupSettings,
            LSUniversalUiSettings uiSettings)
        {
            _audio = audio ?? throw new ArgumentNullException("audio");
            _profile = profile;
            _log = log;
            _controls = controls ?? LSPDControlBindings.Default();
            _settings = settings ?? LSPoliceConvoySettings.Default();
            _cleanupSettings = cleanupSettings ?? LSPoliceCleanupSettings.Default();
            _uiSettings = uiSettings ?? LSUniversalUiSettings.Default();
        }

        internal LSPDDispatchState State { get { return _state; } }
        internal bool IsPreparingTransport
        {
            get
            {
                return _phase == CustodyPhase.PreparingSceneTransport
                    || _phase == CustodyPhase.PreparingPrisonTransport
                    || _phase == CustodyPhase.PreparingRequestedPrisoner;
            }
        }
        internal bool Active
        {
            get
            {
                return _phase != CustodyPhase.None
                    && _phase != CustodyPhase.Completed
                    && _phase != CustodyPhase.Failed;
            }
        }
        internal bool HoldingAtStation
        {
            get { return Active && _phase == CustodyPhase.HoldingAtStation; }
        }
        internal bool DispatchStationTransportDecisionPending
        {
            get
            {
                return Active
                    && _dispatchCustodyOperation
                    && !_isRequestedConvoyActivity
                    && !_playerOwnedTransport
                    && _phase == CustodyPhase.DispatchStationTransportDecision;
            }
        }
        internal bool DispatchGroupPlayerCollectionActive
        {
            get
            {
                return Active && _dispatchCustodyOperation
                    && !_isRequestedConvoyActivity
                    && _dispatchGroupPlayerDrivenPickup;
            }
        }
        internal bool DispatchGroupHasTransferredCustody
        {
            get { return _dispatchGroupPlayerDrivenPickup
                && _dispatchCustodyTransferredHandles.Count > 0; }
        }
        internal bool HasTransferredDispatchGroupCustody(
            IEnumerable<int> handles)
        {
            if (!_dispatchGroupPlayerDrivenPickup || handles == null)
                return false;
            int[] required = handles.Where(handle => handle > 0)
                .Distinct().ToArray();
            return required.Length > 0
                && required.All(handle =>
                    _dispatchCustodyTransferredHandles.Contains(handle));
        }
        internal bool HasScatteredDispatchGroup(
            IEnumerable<Ped> suspects,
            Vector3 incidentOrigin)
        {
            List<Ped> living = suspects == null
                ? new List<Ped>()
                : suspects.Where(ped => ped != null && ped.Exists()
                    && !ped.IsDead && ped.Health > 0)
                    .GroupBy(ped => ped.Handle)
                    .Select(group => group.First())
                    .ToList();
            if (living.Count < 2)
                return false;

            Ped primary = incidentOrigin == Vector3.Zero
                ? living[0]
                : living.OrderBy(ped => ped.Position.DistanceTo(incidentOrigin))
                    .First();
            return BuildDispatchPickupStops(living, primary, incidentOrigin).Count > 1;
        }
        internal IEnumerable<int> PendingDispatchGroupLoadedHandles
        {
            get { return _dispatchGroupLoadedPendingHandoff.ToArray(); }
        }
        internal bool PlayerOwnsPhysicalCustodyInteraction
        {
            get
            {
                return Active
                    && (_playerEscortPrisoners.Count > 0
                        || _playerHandcuffAnimationUntil.Count > 0
                        || _playerDoorInteractions.Count > 0
                        || _playerEntryInteractions.Count > 0
                        || _playerUnloadingStartedAt.Count > 0
                        || _playerUnloadingDoorInteractions.Count > 0
                        || _playerUnloadingTasksIssued.Count > 0);
            }
        }
        internal bool OwnsPlayerRearDoorInteractionInput(Ped player)
        {
            if (!Active || player == null || !player.Exists() || player.IsDead
                || player.IsInVehicle()
                || (_phase != CustodyPhase.StationUnloading
                    && _phase != CustodyPhase.PrisonHandoff)
                || _transport == null || !_transport.Exists())
                return false;

            List<Ped> prisoners = ValidPrisoners().ToList();
            return prisoners.Any(prisoner => prisoner != null && prisoner.Exists()
                && !prisoner.IsDead && IsInTransport(prisoner)
                && (player.Position.DistanceTo(TransportDoorPosition(
                        PrisonerSeat(prisoner, prisoners)))
                    <= PlayerCustodyLoadInteractionRadius
                    || _playerUnloadingDoorInteractions.Contains(prisoner.Handle)
                        && player.Position.DistanceTo(_transport.Position)
                            <= PlayerCustodyLoadInteractionRadius));
        }
        internal bool Completed { get { return _phase == CustodyPhase.Completed; } }
        internal bool CompletedAfterDispatchRelease { get { return Completed && _completedAfterDispatchRelease; } }
        internal bool DispatchClosedAfterCrewCustodyHandoff
        {
            get { return _dispatchClosedAfterCrewCustodyHandoff; }
        }
        internal bool Failed { get { return _phase == CustodyPhase.Failed; } }
        internal bool FailedAfterStationHandoff
        {
            get { return Failed && _failedIsRecoverable && _failedAfterStationHandoff; }
        }
        internal bool CanUseTerminalCompletion
        {
            get
            {
                return Active && (_phase == CustodyPhase.DriveToPrison
                    || _phase == CustodyPhase.PrisonHandoff);
            }
        }
        internal string LastFailureReason { get { return _lastFailureReason; } }
        internal int PrisonerCount { get { return ValidPrisoners().Count(); } }
        internal bool IsRequestedConvoyActivity { get { return _isRequestedConvoyActivity; } }

        internal bool CanReleaseDispatchSuspect(Ped suspect)
        {
            if (!Active || _isRequestedConvoyActivity || !_playerOwnedTransport
                || suspect == null || !suspect.Exists() || suspect.IsDead
                || !_prisoners.Any(value => value != null && value.Exists()
                    && value.Handle == suspect.Handle))
                return false;
            if (_phase != CustodyPhase.SceneLoading
                && _phase != CustodyPhase.DriveToStation)
                return false;

            if (IsInTransport(suspect))
                return true;

            Ped player = Game.Player.Character;
            Ped escort;
            return player != null && player.Exists()
                && _playerEscortPrisoners.Contains(suspect.Handle)
                && _prisonerEscortOfficers.TryGetValue(suspect.Handle, out escort)
                && escort != null && escort.Exists()
                && escort.Handle == player.Handle;
        }

        internal string RequestDispatchSuspectRelease(Ped suspect)
        {
            if (!CanReleaseDispatchSuspect(suspect))
                return "The active suspect is not under your personal vehicle custody.";
            if (_pendingDispatchReleaseHandle != 0)
                return "The physical release is already in progress.";

            if (IsInTransport(suspect))
            {
                if (_transport == null || !_transport.Exists())
                    return "Your Police vehicle is unavailable. Custody remains active.";
                if (Math.Abs(_transport.Speed) > DispatchReleaseStoppedSpeed)
                    return "Stop the Police vehicle before asking the suspect to exit.";

                _pendingDispatchReleaseHandle = suspect.Handle;
                _dispatchReleaseRequestedAt = DateTime.UtcNow;
                _nextDispatchReleaseTaskAt = _dispatchReleaseRequestedAt
                    .AddMilliseconds(DispatchReleaseTaskRefreshMilliseconds);
                _dispatchReleaseTaskAttempts = 1;
                try
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, suspect, _transport, 0);
                }
                catch (Exception ex)
                {
                    ClearPendingDispatchRelease();
                    LogException("POLICE_CONVOY_DISPATCH_RELEASE_EXIT_TASK_FAILED", ex);
                    return "The suspect could not be asked to exit safely. Custody remains active.";
                }

                LogRuntime("POLICE_CONVOY_DISPATCH_RELEASE_EXIT_STARTED",
                    "Ped=" + suspect.Handle + "; Vehicle=" + _transport.Handle
                    + "; PhysicalExitRequired=true");
                return "The suspect is being asked to exit your stopped Police vehicle. Custody will end after the exit is confirmed.";
            }

            string result;
            ReleasePlayerCustodySuspect(suspect, out result);
            return result;
        }

        internal Ped ConsumeDispatchReleasedSuspect()
        {
            Ped released = _dispatchReleasedPrisoner;
            _dispatchReleasedPrisoner = null;
            return released;
        }
        internal string StatusText
        {
            get
            {
                string owner = _isRequestedConvoyActivity ? "Convoy Request" : "Dispatch Custody";
                switch (_phase)
                {
                    case CustodyPhase.PreparingRequestedPrisoner:
                        return owner + ": staging the secured prisoner at the station.";
                    case CustodyPhase.PreparingSceneTransport:
                    case CustodyPhase.PreparingPrisonTransport:
                        return owner + ": Police transport is staging on a safe road.";
                    case CustodyPhase.SceneTransportEnRoute:
                        return owner + ": transport unit is driving to the arrest scene.";
                    case CustodyPhase.PrisonTransportEnRoute:
                        return owner + ": prison convoy unit is driving to the station handoff.";
                    case CustodyPhase.SceneLoading:
                        return owner + ": "
                            + (_playerEscortPrisoners.Count > 0
                                ? PlayerLoadingStatusText()
                                : "transport officer is escorting the handcuffed prisoner to the rear door.");
                    case CustodyPhase.DispatchStationTransportDecision:
                        return _dispatchGroupPlayerDrivenPickup
                            ? owner + ": press Y to drive the loaded transport to the station or N to keep the van parked with custody active."
                            : owner + ": press Y to drive the loaded transport to the station or N to let the transport crew finish the station handoff.";
                    case CustodyPhase.DispatchCrewDriverBoarding:
                        return owner + ": transport crew is taking the driver seat for the station run.";
                    case CustodyPhase.DispatchCrewDriveToStation:
                        return owner + ": transport crew is driving secured suspects to " + _stationDisplayName + ".";
                    case CustodyPhase.PrisonLoading:
                        return owner + ": "
                            + (_playerEscortPrisoners.Count > 0
                                ? PlayerLoadingStatusText()
                                : "prison officer is escorting the handcuffed prisoner to the rear door.");
                    case CustodyPhase.DriveToStation:
                        return owner + ": drive the prisoner to " + _stationDisplayName + ".";
                    case CustodyPhase.StationUnloading:
                        return owner + ": exit the transport; at the rear door, E opens it and E again unloads. The station officer completes intake.";
                    case CustodyPhase.HoldingAtStation:
                        return owner + ": choose prison transfer or finish custody at the station.";
                    case CustodyPhase.DriveToPrison:
                        return owner + ": drive the convoy to " + _prisonDisplayName + ".";
                    case CustodyPhase.PrisonHandoff:
                        return owner + ": at the rear door, E opens it and E again unloads. Prison staff complete the handoff.";
                    case CustodyPhase.Completed:
                        return owner + ": completed.";
                    case CustodyPhase.Failed:
                        return owner + ": " + (string.IsNullOrWhiteSpace(_lastFailureReason)
                            ? "closed." : _lastFailureReason);
                    default:
                        return "No active Convoy operation.";
                }
            }
        }
        internal IEnumerable<Ped> ActiveRouteThreats
        {
            get { return _routeThreatPeds.Where(ped => ped != null && ped.Exists() && !ped.IsDead); }
        }
        /// <summary>
        /// Exposes the Convoy-owned peds only as a protection boundary for
        /// ambient NPC Response. Convoy remains the sole owner of their tasks,
        /// custody state, and cleanup.
        /// </summary>
        internal IEnumerable<Ped> ProtectedActors
        {
            get
            {
                return _prisoners
                    .Concat(_sceneEscortOfficers)
                    .Concat(_stationHandoffOfficers)
                    .Concat(_prisonHandoffOfficers)
                    .Concat(_routeThreatPeds)
                    .Where(ped => ped != null && ped.Exists());
            }
        }
        /// <summary>
        /// Exposes Convoy-owned vehicles only so ambient traffic logic cannot
        /// clear or redirect a transport that is already under Convoy control.
        /// </summary>
        internal IEnumerable<Vehicle> ProtectedVehicles
        {
            get
            {
                if (_transport != null && _transport.Exists())
                    yield return _transport;
                if (_routeThreatVehicle != null && _routeThreatVehicle.Exists())
                    yield return _routeThreatVehicle;
            }
        }
        internal Vector3 SupportPosition
        {
            get
            {
                Ped threat = ActiveRouteThreats.FirstOrDefault();
                if (threat != null)
                    return threat.Position;
                if (_transport != null && _transport.Exists())
                    return _transport.Position;
                // A requested Convoy stages its prisoner and receiving unit at
                // the station before it owns a vehicle. Backup must therefore
                // travel toward the station instead of incorrectly heading
                // straight to the prison on its first assignment.
                return _stationDestination != Vector3.Zero
                    ? _stationDestination : _prisonDestination;
            }
        }

        /// <summary>Compatibility overload for the former single-prisoner path.</summary>
        internal string Start(Ped prisoner, Vector3 pickupTarget)
        {
            return Start(prisoner == null ? Enumerable.Empty<Ped>() : new[] { prisoner }, pickupTarget);
        }

        /// <summary>
        /// Starts custody only after Dispatch has handed real arrested peds to
        /// Convoy. All input peds remain in-world; no actor is recreated.
        /// </summary>
        internal string Start(IEnumerable<Ped> prisoners, Vector3 pickupTarget)
        {
            return Start(prisoners, pickupTarget, true);
        }

        internal string Start(
            IEnumerable<Ped> prisoners,
            Vector3 pickupTarget,
            bool reportTransportRequest)
        {
            return Start(prisoners, pickupTarget, reportTransportRequest,
                true, false, false);
        }

        private string Start(
            IEnumerable<Ped> prisoners,
            Vector3 pickupTarget,
            bool reportTransportRequest,
            bool prepareExistingPrisoners,
            bool playerDrivenGroupCollection,
            bool awaitingMoreGroupSuspects)
        {
            if (!_settings.Enabled)
                return "Prisoner transport is disabled in LS Immersive settings.";
            if (Completed)
                ConsumeCompletedState();
            if (Failed)
                return "Recover or reset the failed prisoner transport before starting another one.";
            if (Active)
                return "A prisoner custody operation is already active.";

            List<Ped> valid = prisoners == null
                ? new List<Ped>()
                : prisoners.Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                    .GroupBy(ped => ped.Handle).Select(group => group.First()).ToList();
            if (valid.Count == 0)
                return "No living arrested suspect is available.";

            LSPDPoliceStationDefinition station = SelectedStation();
            LSPDPoliceStationDefinition prison = _profile == null ? null : _profile.FindStation("bolingbroke");
            if (station == null)
                return "No operational Police station is configured.";
            if (prison == null)
                return "No prison destination is configured.";
            Vector3 stationHandoff = StationHandoffPosition(station);
            if (stationHandoff == Vector3.Zero)
                return "No safe outside custody point is configured for " + station.DisplayName + ".";

            Vector3 dispatchVehicleAccessTarget;
            float roadWalkInDistance;
            float maximumRoadNodeDistance = MaximumDispatchSceneVehicleDistance;
            if (!TryGetDispatchSceneVehicleRoadNode(
                    pickupTarget, maximumRoadNodeDistance,
                    out dispatchVehicleAccessTarget, out roadWalkInDistance))
            {
                LogRuntime(
                    "POLICE_CONVOY_DISPATCH_ROADSIDE_PICKUP_UNAVAILABLE",
                    "ScenePickup=" + pickupTarget
                    + "; MaximumVehicleDistance=" + MaximumDispatchSceneVehicleDistance
                    + "; Reason=NoVerifiedVehicleRoadNodeWithinBound"
                    + "; CustodyOwnershipTransferred=false");
                return "No drivable road access was found within 100 metres of the scene. The suspect remains in Dispatch custody.";
            }

            ResetOperationFields();
            _operationId = Guid.NewGuid().ToString("N");
            _prisoners.AddRange(valid);
            _pickupTarget = pickupTarget;
            _dispatchSceneVehicleAccessTarget = dispatchVehicleAccessTarget;
            _dispatchSceneWalkInDistanceMeters = roadWalkInDistance;
            _stationDestination = stationHandoff;
            _stationIntakeDestination = StationIntakePosition(station);
            _prisonDestination = new Vector3(prison.ExteriorX, prison.ExteriorY, prison.ExteriorZ);
            _stationDisplayName = station.DisplayName;
            _prisonDisplayName = prison.DisplayName;
            _state = LSPDDispatchState.AwaitingTransport;
            _failedIsRecoverable = true;
            _dispatchCustodyOperation = true;
            _dispatchGroupPlayerDrivenPickup = playerDrivenGroupCollection;
            _dispatchGroupCollectionAwaitingMore = awaitingMoreGroupSuspects;
            if (prepareExistingPrisoners)
            foreach (Ped prisoner in _prisoners)
                // Dispatch has already completed the player's physical
                // handcuff interaction. Do not replace that secured pose with
                // a fresh hands-up/ambient task while the transport is being
                // prepared.
                PreparePrisoner(prisoner, false);

            if (reportTransportRequest)
                ReportStage("lsimmersivelife.police.transport.requested", "requested");
            BeginTransportPreparation(CustodyPhase.PreparingSceneTransport, VehicleName("policet", "fbi2"));
            LogRuntime("POLICE_CUSTODY_STARTED",
                "Prisoners=" + DescribePrisoners() + "; ScenePickup=" + _pickupTarget
                + "; VehicleRoadTarget=" + _dispatchSceneVehicleAccessTarget
                + "; RoadNodeDistance=" + roadWalkInDistance.ToString("0.0")
                + "; RequiredTransportEscorts=" + Math.Min(2, valid.Count)
                + "; Station=" + _stationDisplayName + "; Prison=" + _prisonDisplayName);
            return valid.Count == 1
                ? "Prisoner secured. A transport unit is staging and will drive to the scene."
                : valid.Count + " prisoners secured. A group transport unit is staging and will drive to the scene.";
        }

        internal string StartDispatchGroupTransport(
            IEnumerable<Ped> prisoners,
            Vector3 preferredPickup,
            Vector3 incidentOrigin,
            bool reportTransportRequest,
            bool awaitingMoreGroupSuspects = false,
            int expectedLivingSuspectCount = 0,
            bool scatteredGroup = false)
        {
            List<Ped> valid = prisoners == null
                ? new List<Ped>()
                : prisoners.Where(ped => ped != null && ped.Exists() && !ped.IsDead
                    && ped.Health > 0)
                    .GroupBy(ped => ped.Handle)
                    .Select(group => group.First())
                    .ToList();
            if (valid.Count < 2 && !awaitingMoreGroupSuspects)
                return Start(valid, preferredPickup, reportTransportRequest);
            if (valid.Count == 0)
                return "No living secured Dispatch suspect is available for transport.";

            Ped primaryPickup = valid
                .OrderBy(ped => preferredPickup == Vector3.Zero
                    ? 0f : ped.Position.DistanceTo(preferredPickup))
                .First();
            List<DispatchPickupStop> stops = BuildDispatchPickupStops(
                valid, primaryPickup, incidentOrigin);
            // The Player-driven pickup route exists only for a genuinely
            // scattered Dispatch group. A group secured at one place retains
            // the established single-stop transport flow.
            bool playerDrivenGroupCollection = scatteredGroup
                && (stops.Count > 1 || awaitingMoreGroupSuspects);
            if (!playerDrivenGroupCollection)
                return Start(valid,
                    stops.Count == 0 ? preferredPickup : stops[0].SceneTarget,
                    reportTransportRequest);

            for (int index = 0; index < stops.Count; index++)
            {
                DispatchPickupStop stop = stops[index];
                float distance;
                if (!TryGetDispatchSceneVehicleRoadNode(
                        stop.SceneTarget,
                        MaximumDispatchSceneVehicleDistance,
                        out stop.RoadTarget,
                        out distance))
                {
                    LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_UNAVAILABLE",
                        "Stop=" + (index + 1) + "; ScenePickup=" + stop.SceneTarget
                        + "; MaximumVehicleDistance=" + MaximumDispatchSceneVehicleDistance
                        + "; Reason=NoVerifiedVehicleRoadNodeWithinBound"
                        + "; CustodyOwnershipTransferred=false");
                    return "No drivable road access was found for pickup " + (index + 1)
                        + ". Dispatch custody is unchanged; move the secured group to an accessible roadside location and request transport again.";
                }
            }

            // Deliver the first van to the Player's current scene position
            // when GTA can confirm a nearby drivable road node. If the Player
            // is indoors or their position has no safe vehicle access, retain
            // the already validated first-suspect roadside destination.
            Vector3 initialDeliveryTarget = stops[0].SceneTarget;
            Vector3 initialDeliveryRoadTarget = stops[0].RoadTarget;
            Ped player = Game.Player.Character;
            Vector3 playerRoadTarget;
            float playerRoadDistance;
            if (player != null && player.Exists()
                && TryGetDispatchSceneVehicleRoadNode(
                    player.Position,
                    MaximumDispatchSceneVehicleDistance,
                    out playerRoadTarget,
                    out playerRoadDistance))
            {
                initialDeliveryTarget = player.Position;
                initialDeliveryRoadTarget = playerRoadTarget;
                LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PLAYER_DELIVERY_TARGET",
                    "PlayerPosition=" + player.Position
                    + "; RoadTarget=" + playerRoadTarget
                    + "; RoadDistance=" + playerRoadDistance.ToString(
                        "0.0", CultureInfo.InvariantCulture)
                    + "; DeliveryDriverRemainsOwner=true");
            }
            else
            {
                LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PLAYER_DELIVERY_FALLBACK",
                    "PlayerPosition=" + (player == null || !player.Exists()
                        ? "unavailable" : player.Position.ToString())
                    + "; RoadTarget=" + initialDeliveryRoadTarget
                    + "; Reason=NoVerifiedPlayerRoadNodeWithinBound"
                    + "; DeliveryDriverRemainsOwner=true");
            }

            // Start the existing Dispatch-owned Convoy operation only after
            // every physical pickup stop has a verified nearby road node.
            // Custody transfer remains the caller's existing atomic handoff.
            string result = Start(valid, initialDeliveryTarget,
                reportTransportRequest,
                false,
                true,
                awaitingMoreGroupSuspects);
            if (!Active)
                return result;

            _dispatchPickupStops.AddRange(stops);
            _activeDispatchPickupStopIndex = 0;
            _dispatchSceneVehicleAccessTarget = initialDeliveryRoadTarget;
            _dispatchGroupCollectionAwaitingMore = awaitingMoreGroupSuspects;
            // Provision for the whole live incident roster. A scattered runner
            // may be secured by Backup after the van is already en route, but
            // must still fit in this same transport.
            _dispatchGroupExpectedPrisonerCount = Math.Max(
                valid.Count, expectedLivingSuspectCount);
            LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_PLAN_READY",
                "Operation=" + _operationId
                + "; Stops=" + stops.Count
                + "; PrimaryPickup=" + stops[0].SceneTarget
                + "; IncidentOrigin=" + incidentOrigin
                + "; Plan=" + DescribeDispatchPickupStops()
                + "; AllRoadNodesValidated=true"
                + "; PlayerDrivenCollection=true"
                + "; AwaitingAdditionalSecuredSuspects=" + awaitingMoreGroupSuspects
                + "; InteriorSuspectCapacityPlannedFor=" + _dispatchGroupExpectedPrisonerCount
                + "; LivingSuspectsInIncident=" + expectedLivingSuspectCount
                + "; CustodyOwner=DispatchUntilPhysicalSeatConfirmation");
            return result + " The transport officer will bring the van to you for the original scene suspects. After they are physically loaded, take the driver's seat to collect any remaining Backup-secured suspect.";
        }

        internal void UpdateDispatchGroupPickupManifest(
            IEnumerable<Ped> livingSecuredSuspects,
            bool allLivingSuspectsResolved,
            int livingSuspectCount)
        {
            if (!DispatchGroupPlayerCollectionActive || livingSecuredSuspects == null)
                return;

            DateTime now = DateTime.UtcNow;
            List<Ped> secured = livingSecuredSuspects
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead
                    && ped.Health > 0)
                .GroupBy(ped => ped.Handle)
                .Select(group => group.First())
                .ToList();
            _dispatchGroupExpectedPrisonerCount = Math.Max(
                Math.Max(secured.Count, livingSuspectCount),
                _prisoners.Count(prisoner => prisoner != null
                    && prisoner.Exists() && !prisoner.IsDead));
            bool addedAny = false;
            foreach (Ped prisoner in secured)
            {
                if (_prisoners.Any(existing => existing != null && existing.Exists()
                    && existing.Handle == prisoner.Handle))
                    continue;

                if (_transport != null && _transport.Exists()
                    && !HasDispatchGroupSeatCapacity(
                        _dispatchGroupExpectedPrisonerCount))
                {
                    if (_dispatchGroupBackupLoaderWaitLogged.Add(prisoner.Handle))
                    {
                        LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_INTERIOR_CAPACITY_PENDING",
                            "Operation=" + _operationId
                            + "; Vehicle=" + _transport.Handle
                            + "; Suspect=" + prisoner.Handle
                            + "; ExpectedLivingSuspects=" + _dispatchGroupExpectedPrisonerCount
                            + "; InteriorSeats=" + DispatchGroupTransportSeatCount()
                            + "; RequiredInteriorSeats=" + (_dispatchGroupExpectedPrisonerCount + 1)
                            + "; BackupCustodyRetained=true; ExistingTransportRetained=true");
                        Notify("~b~POLICE CUSTODY~s~\nThe current van cannot confirm an inside seat for every living suspect. Backup is keeping this suspect secured while the existing transport remains active.");
                    }
                    continue;
                }
                _dispatchGroupBackupLoaderWaitLogged.Remove(prisoner.Handle);

                DateTime lastAttempt;
                if (_dispatchGroupLastRoadAttemptAt.TryGetValue(
                        prisoner.Handle, out lastAttempt)
                    && now < lastAttempt.AddSeconds(15))
                    continue;
                _dispatchGroupLastRoadAttemptAt[prisoner.Handle] = now;

                Vector3 roadTarget;
                float distance;
                if (!TryGetDispatchSceneVehicleRoadNode(
                        prisoner.Position,
                        MaximumDispatchSceneVehicleDistance,
                        out roadTarget,
                        out distance))
                {
                    LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_ROAD_PENDING",
                        "Operation=" + _operationId
                        + "; Suspect=" + prisoner.Handle
                        + "; SceneTarget=" + prisoner.Position
                        + "; MaximumVehicleDistance=" + MaximumDispatchSceneVehicleDistance
                        + "; CustodyOwner=DispatchOrBackup; BackupGuardRetained=true"
                        + "; RetrySeconds=15");
                    continue;
                }

                DispatchPickupStop stop = _dispatchPickupStops
                    .Where(candidate => candidate != null && !candidate.Completed
                        && candidate.SceneTarget.DistanceTo(prisoner.Position)
                            <= DispatchPickupClusterRadius)
                    .OrderBy(candidate => candidate.SceneTarget.DistanceTo(
                        prisoner.Position))
                    .FirstOrDefault();
                if (stop == null)
                {
                    stop = new DispatchPickupStop
                    {
                        SceneTarget = prisoner.Position,
                        RoadTarget = roadTarget
                    };
                    _dispatchPickupStops.Add(stop);
                }
                stop.PrisonerHandles.Add(prisoner.Handle);
                _prisoners.Add(prisoner);
                _dispatchGroupLastRoadAttemptAt.Remove(prisoner.Handle);
                addedAny = true;
                LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_ADDED",
                    "Operation=" + _operationId
                    + "; Suspect=" + prisoner.Handle
                    + "; Stop=" + (_dispatchPickupStops.IndexOf(stop) + 1)
                    + "; SceneTarget=" + stop.SceneTarget
                    + "; RoadTarget=" + stop.RoadTarget
                    + "; RoadDistance=" + distance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; CustodyOwner=DispatchOrBackupUntilPhysicalSeatConfirmation");
            }

            HashSet<int> represented = new HashSet<int>(_prisoners
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                .Select(ped => ped.Handle));
            bool everyLivingSecuredSuspectHasPickup = secured.All(
                ped => represented.Contains(ped.Handle));
            _dispatchGroupCollectionAwaitingMore = !allLivingSuspectsResolved
                || !everyLivingSecuredSuspectHasPickup;

            if (addedAny)
            {
                _dispatchGroupPlayerRoutePromptLogged = false;
                LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_PLAN_EXTENDED",
                    "Operation=" + _operationId
                    + "; Stops=" + _dispatchPickupStops.Count
                    + "; AwaitingAdditionalSuspects=" + _dispatchGroupCollectionAwaitingMore
                    + "; Plan=" + DescribeDispatchPickupStops());
            }
        }

        internal void ConfirmDispatchGroupLoadedOwnership(IEnumerable<int> handles)
        {
            if (!DispatchGroupPlayerCollectionActive || handles == null)
                return;
            foreach (int handle in handles.Distinct())
            {
                if (!_dispatchGroupLoadedPendingHandoff.Contains(handle))
                    continue;
                _dispatchGroupLoadedPendingHandoff.Remove(handle);
                _dispatchCustodyTransferredHandles.Add(handle);
                _dispatchGroupBackupEscorts.Remove(handle);
                _dispatchGroupBackupLoaderWaitLogged.Remove(handle);
                LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_CUSTODY_TRANSFERRED",
                    "Operation=" + _operationId
                    + "; Suspect=" + handle
                    + "; PhysicalSeatConfirmed=true"
                    + "; BackupReleaseAllowedForSuspect=true");
            }

            DispatchPickupStop activeStop = ActiveDispatchPickupStop();
            if (activeStop != null && activeStop.PrisonerHandles.Count > 0
                && activeStop.PrisonerHandles.All(handle =>
                    _dispatchCustodyTransferredHandles.Contains(handle)))
            {
                activeStop.Completed = true;
                if (_phase == CustodyPhase.SceneLoading)
                    SetPhase(CustodyPhase.DispatchGroupPickupWaiting);
            }
        }

        internal bool IsRemoteDispatchGroupPickupFor(int suspectHandle)
        {
            DispatchPickupStop activeStop = ActiveDispatchPickupStop();
            return IsRemoteDispatchGroupPickup()
                && activeStop != null
                && activeStop.PrisonerHandles.Contains(suspectHandle);
        }

        internal void AssignDispatchGroupBackupEscort(Ped suspect, Ped officer)
        {
            if (!DispatchGroupPlayerCollectionActive
                || suspect == null || !suspect.Exists()
                || officer == null || !officer.Exists() || officer.IsDead
                || !IsRemoteDispatchGroupPickupFor(suspect.Handle))
                return;

            _dispatchGroupBackupEscorts[suspect.Handle] = officer;
        }

        private List<DispatchPickupStop> BuildDispatchPickupStops(
            IList<Ped> prisoners,
            Ped primaryPickup,
            Vector3 incidentOrigin)
        {
            List<DispatchPickupStop> stops = new List<DispatchPickupStop>();
            Dictionary<int, Ped> remaining = prisoners
                .Where(ped => ped != null && ped.Exists())
                .GroupBy(ped => ped.Handle)
                .ToDictionary(group => group.Key, group => group.First());
            if (remaining.Count == 0 || primaryPickup == null
                || !primaryPickup.Exists())
                return stops;

            bool primaryAtIncident = incidentOrigin != Vector3.Zero
                && primaryPickup.Position.DistanceTo(incidentOrigin)
                    <= DispatchPickupSceneClusterRadius;
            if (primaryAtIncident)
            {
                List<Ped> incidentGroup = remaining.Values
                    .Where(ped => ped.Position.DistanceTo(incidentOrigin)
                        <= DispatchPickupSceneClusterRadius)
                    .ToList();
                AddDispatchPickupStop(stops, incidentOrigin, incidentGroup);
                foreach (Ped prisoner in incidentGroup)
                    remaining.Remove(prisoner.Handle);
            }
            else
            {
                List<Ped> primaryGroup = remaining.Values
                    .Where(ped => ped.Position.DistanceTo(primaryPickup.Position)
                        <= DispatchPickupClusterRadius)
                    .ToList();
                AddDispatchPickupStop(stops, primaryPickup.Position, primaryGroup);
                foreach (Ped prisoner in primaryGroup)
                    remaining.Remove(prisoner.Handle);

                if (incidentOrigin != Vector3.Zero)
                {
                    List<Ped> incidentGroup = remaining.Values
                        .Where(ped => ped.Position.DistanceTo(incidentOrigin)
                            <= DispatchPickupSceneClusterRadius)
                        .ToList();
                    AddDispatchPickupStop(stops, incidentOrigin, incidentGroup);
                    foreach (Ped prisoner in incidentGroup)
                        remaining.Remove(prisoner.Handle);
                }
            }

            while (remaining.Count > 0)
            {
                Ped representative = remaining.Values.First();
                List<Ped> nearby = remaining.Values
                    .Where(ped => ped.Position.DistanceTo(representative.Position)
                        <= DispatchPickupClusterRadius)
                    .ToList();
                AddDispatchPickupStop(stops, representative.Position, nearby);
                foreach (Ped prisoner in nearby)
                    remaining.Remove(prisoner.Handle);
            }
            return stops;
        }

        private DispatchPickupStop ActiveDispatchPickupStop()
        {
            return _activeDispatchPickupStopIndex >= 0
                && _activeDispatchPickupStopIndex < _dispatchPickupStops.Count
                ? _dispatchPickupStops[_activeDispatchPickupStopIndex]
                : null;
        }

        private static void AddDispatchPickupStop(
            IList<DispatchPickupStop> stops,
            Vector3 sceneTarget,
            IEnumerable<Ped> prisoners)
        {
            List<Ped> valid = prisoners == null
                ? new List<Ped>()
                : prisoners.Where(ped => ped != null && ped.Exists()).ToList();
            if (sceneTarget == Vector3.Zero || valid.Count == 0)
                return;

            DispatchPickupStop stop = new DispatchPickupStop
            {
                SceneTarget = sceneTarget
            };
            foreach (Ped prisoner in valid)
                stop.PrisonerHandles.Add(prisoner.Handle);
            stops.Add(stop);
        }

        private string DescribeDispatchPickupStops()
        {
            return string.Join(" | ", _dispatchPickupStops.Select((stop, index) =>
                (index + 1) + ":" + stop.SceneTarget + ":peds="
                + string.Join(",", stop.PrisonerHandles.Select(handle =>
                    handle.ToString()).ToArray())).ToArray());
        }

        /// <summary>
        /// Uses the player's already occupied Police car for one arrested
        /// prisoner. Motorcycles remain transport-only because they do not
        /// provide a rear custody seat. A two-seat Police model is also
        /// transport-only: the normal Convoy custody phases still own the
        /// physical handoff and station progression after this point.
        /// </summary>
        internal bool CanUsePlayerVehicleCustody(Ped player, Vehicle playerVehicle)
        {
            return player != null && player.Exists()
                && playerVehicle != null && playerVehicle.Exists()
                && player.CurrentVehicle != null && player.CurrentVehicle.Exists()
                && player.CurrentVehicle.Handle == playerVehicle.Handle
                && player.IsInPoliceVehicle && !playerVehicle.Model.IsBike
                && HasRearCustodySeat(playerVehicle);
        }

        /// <summary>
        /// Dispatch may offer the saved Preferred Utility only for a single
        /// suspect when it is the exact selected Police vehicle, stopped and
        /// driveable, within the bounded walk-to-vehicle distance, and has a
        /// real free rear custody seat. The Player may be on foot beside it.
        /// </summary>
        internal bool CanUseDispatchPreferredPlayerVehicleCustody(
            Ped player,
            Vehicle playerVehicle,
            Ped prisoner,
            out VehicleSeat custodySeat,
            out string unavailableReason)
        {
            custodySeat = VehicleSeat.Any;
            unavailableReason = string.Empty;
            if (player == null || !player.Exists()
                || playerVehicle == null || !playerVehicle.Exists()
                || prisoner == null || !prisoner.Exists() || prisoner.IsDead)
            {
                unavailableReason = "the Player, Utility, or secured suspect is no longer available";
                return false;
            }

            string preferredModel = _profile == null
                ? string.Empty : _profile.SelectedVehicleModelName;
            if (string.IsNullOrWhiteSpace(preferredModel)
                || ResolveModelHash(preferredModel) != playerVehicle.Model.Hash)
            {
                unavailableReason = "the saved Preferred Utility is not the Police vehicle at the scene";
                return false;
            }
            Vehicle currentVehicle = player.CurrentVehicle;
            bool playerInPreferredVehicle = currentVehicle != null
                && currentVehicle.Exists()
                && currentVehicle.Handle == playerVehicle.Handle;
            if ((player.IsInVehicle() && !playerInPreferredVehicle)
                || (playerInPreferredVehicle
                    && (!player.IsInPoliceVehicle || playerVehicle.Model.IsBike))
                || playerVehicle.Model.IsBike)
            {
                unavailableReason = "the Player must be on foot or in the saved Police Utility";
                return false;
            }

            try
            {
                Ped driver = Function.Call<Ped>(
                    Hash.GET_PED_IN_VEHICLE_SEAT,
                    playerVehicle,
                    (int)VehicleSeat.Driver,
                    false);
                if (driver != null && driver.Exists()
                    && driver.Handle != player.Handle)
                {
                    unavailableReason = "another Ped is occupying the saved Police Utility driver's seat";
                    return false;
                }
                if (playerInPreferredVehicle
                    && (driver == null || !driver.Exists() || driver.Handle != player.Handle))
                {
                    unavailableReason = "the Player is not driving the saved Police Utility";
                    return false;
                }
            }
            catch
            {
                unavailableReason = "the Utility driver seat could not be checked safely";
                return false;
            }

            if (!playerVehicle.IsDriveable || Math.Abs(playerVehicle.Speed) > 0.5f)
            {
                unavailableReason = "the saved Police Utility is damaged or still moving";
                return false;
            }
            if (!HasRearCustodySeat(playerVehicle))
            {
                unavailableReason = "the saved Police Utility has no supported rear custody seat";
                return false;
            }
            float distanceFromPlayer = playerVehicle.Position.DistanceTo(player.Position);
            float distanceFromPrisoner = playerVehicle.Position.DistanceTo(prisoner.Position);
            if (distanceFromPlayer > MaximumDispatchPreferredUtilityDistance
                || distanceFromPrisoner > MaximumDispatchPreferredUtilityDistance)
            {
                unavailableReason = "the saved Police Utility is more than 150 metres from the Player or suspect";
                return false;
            }
            if (!TryGetFreeRearCustodySeat(playerVehicle, out custodySeat))
            {
                unavailableReason = "the saved Police Utility has no free rear custody seat";
                return false;
            }
            return true;
        }

        internal string StartDispatchPreferredPlayerVehicleCustody(
            Ped prisoner,
            Vector3 pickupTarget,
            Vehicle playerVehicle)
        {
            VehicleSeat custodySeat;
            string unavailableReason;
            if (!CanUseDispatchPreferredPlayerVehicleCustody(
                Game.Player.Character,
                playerVehicle,
                prisoner,
                out custodySeat,
                out unavailableReason))
                return "The saved Police Utility can no longer be used: " + unavailableReason + ".";

            return StartPlayerVehicleCustodyInternal(
                prisoner,
                pickupTarget,
                playerVehicle,
                true,
                false,
                custodySeat,
                true);
        }

        private bool TryGetFreeRearCustodySeat(
            Vehicle vehicle,
            out VehicleSeat custodySeat)
        {
            custodySeat = VehicleSeat.Any;
            if (vehicle == null || !vehicle.Exists() || vehicle.Model.IsBike
                || !HasRearCustodySeat(vehicle))
                return false;

            VehicleSeat[] rearSeats = { VehicleSeat.LeftRear, VehicleSeat.RightRear };
            foreach (VehicleSeat seat in rearSeats)
            {
                try
                {
                    if (Function.Call<bool>(
                        Hash.IS_VEHICLE_SEAT_FREE,
                        vehicle,
                        (int)seat,
                        false))
                    {
                        custodySeat = seat;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private bool IsRearCustodySeatFree(Vehicle vehicle, VehicleSeat seat)
        {
            if (seat != VehicleSeat.LeftRear && seat != VehicleSeat.RightRear)
                return false;
            try
            {
                return vehicle != null && vehicle.Exists()
                    && Function.Call<bool>(
                        Hash.IS_VEHICLE_SEAT_FREE,
                        vehicle,
                        (int)seat,
                        false);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// NPC contact custody is requested after the officer has already left
        /// the patrol vehicle to arrest the citizen. Keep the same vehicle
        /// eligibility rule, but do not require the player to still occupy the
        /// vehicle at the moment the NPC owner transfers custody.
        /// </summary>
        internal bool CanUsePlayerVehicleCustodyForNpc(Ped player, Vehicle playerVehicle)
        {
            return player != null && player.Exists()
                && !player.IsInVehicle()
                && IsConfiguredPoliceCustodyVehicle(playerVehicle)
                && !playerVehicle.Model.IsBike
                && HasRearCustodySeat(playerVehicle);
        }

        /// <summary>
        /// A player-controlled custody transfer needs a real rear passenger
        /// seat. Do not infer that from the Police model name: addon Police
        /// cars such as two-seat pursuit models can be valid patrol vehicles
        /// while still being unable to carry a prisoner.
        /// </summary>
        internal bool HasRearCustodySeat(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists() || vehicle.Model.IsBike)
                return false;

            try
            {
                int seatCount = Function.Call<int>(
                    Hash.GET_VEHICLE_MODEL_NUMBER_OF_SEATS,
                    vehicle.Model.Hash);
                return seatCount >= 4;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CUSTODY_REAR_SEAT_QUERY_FAILED", ex);
                return false;
            }
        }

        private bool IsConfiguredPoliceCustodyVehicle(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists() || vehicle.Model.IsBike
                || _profile == null)
                return false;

            int hash = vehicle.Model.Hash;
            return _profile.Vehicles.Any(value => value != null
                    && !string.IsNullOrWhiteSpace(value.ModelName)
                    && ResolveModelHash(value.ModelName) == hash)
                || _profile.FavoriteVehicles.Any(value => value != null
                    && !string.IsNullOrWhiteSpace(value.ModelName)
                    && ResolveModelHash(value.ModelName) == hash);
        }

        private static int ResolveModelHash(string modelName)
        {
            return unchecked((int)StringHash.AtStringHash(modelName, 0));
        }

        internal string StartPlayerVehicleCustody(
            Ped prisoner,
            Vector3 pickupTarget,
            Vehicle playerVehicle)
        {
            return StartPlayerVehicleCustodyInternal(
                prisoner, pickupTarget, playerVehicle,
                false, false);
        }

        /// <summary>
        /// Transfers an already handcuffed NPC contact into the existing player
        /// Convoy path. The player remains the escort owner; no second arrest
        /// interaction is required before the physical rear-door steps.
        /// </summary>
        internal string StartPlayerVehicleCustodyFromNpc(
            Ped prisoner,
            Vector3 pickupTarget,
            Vehicle playerVehicle)
        {
            return StartPlayerVehicleCustodyInternal(
                prisoner, pickupTarget, playerVehicle,
                true, true);
        }

        private string StartPlayerVehicleCustodyInternal(
            Ped prisoner,
            Vector3 pickupTarget,
            Vehicle playerVehicle,
            bool allowPlayerOnFoot,
            bool prisonerAlreadySecuredByPlayer,
            VehicleSeat? requestedCustodySeat = null,
            bool dispatchCustodyOperation = false)
        {
            if (!_settings.Enabled)
                return "Prisoner transport is disabled in LS Immersive settings.";
            if (Completed)
                ConsumeCompletedState();
            if (Failed)
                return "Recover or reset the failed prisoner transport before starting another one.";
            if (Active)
                return "A prisoner custody operation is already active.";
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                return "No living arrested suspect is available for player custody.";

            Ped player = Game.Player.Character;
            VehicleSeat custodySeat;
            bool validPlayerVehicle;
            if (dispatchCustodyOperation)
            {
                string dispatchUnavailableReason;
                validPlayerVehicle = CanUseDispatchPreferredPlayerVehicleCustody(
                    player,
                    playerVehicle,
                    prisoner,
                    out custodySeat,
                    out dispatchUnavailableReason);
                if (!validPlayerVehicle)
                    LogRuntime(
                        "POLICE_CONVOY_DISPATCH_PREFERRED_UTILITY_RECHECK_FAILED",
                        "Prisoner=" + prisoner.Handle
                        + "; Vehicle=" + (playerVehicle == null ? "none" : playerVehicle.Handle.ToString())
                        + "; Reason=" + dispatchUnavailableReason);
            }
            else
                validPlayerVehicle = allowPlayerOnFoot
                    ? CanUsePlayerVehicleCustodyForNpc(player, playerVehicle)
                    : CanUsePlayerVehicleCustody(player, playerVehicle);
            if (!validPlayerVehicle)
                return "Player custody transport requires one living suspect and a Police car; Police motorcycles use a staged transport unit.";

            if (requestedCustodySeat.HasValue
                && IsRearCustodySeatFree(playerVehicle, requestedCustodySeat.Value))
                custodySeat = requestedCustodySeat.Value;
            else if (!TryGetFreeRearCustodySeat(playerVehicle, out custodySeat))
                return "The Police Utility has no free rear custody seat. Request a transport van instead.";

            LSPDPoliceStationDefinition station = SelectedStation();
            LSPDPoliceStationDefinition prison = _profile == null ? null : _profile.FindStation("bolingbroke");
            if (station == null)
                return "No operational Police station is configured.";
            if (prison == null)
                return "No prison destination is configured.";
            Vector3 stationHandoff = StationHandoffPosition(station);
            if (stationHandoff == Vector3.Zero)
                return "No safe outside custody point is configured for " + station.DisplayName + ".";

            ResetOperationFields();
            _operationId = Guid.NewGuid().ToString("N");
            _prisoners.Add(prisoner);
            _pickupTarget = pickupTarget;
            _stationDestination = stationHandoff;
            _stationIntakeDestination = StationIntakePosition(station);
            _prisonDestination = new Vector3(prison.ExteriorX, prison.ExteriorY, prison.ExteriorZ);
            _stationDisplayName = station.DisplayName;
            _prisonDisplayName = prison.DisplayName;
            _transport = playerVehicle;
            _playerOwnedTransport = true;
            _playerVehicleCustodySeat = custodySeat;
            _dispatchCustodyOperation = dispatchCustodyOperation;
            _state = LSPDDispatchState.AwaitingTransport;
            _failedIsRecoverable = true;
            PreparePrisoner(prisoner, false);
            SetPhase(CustodyPhase.SceneLoading);
            if (prisonerAlreadySecuredByPlayer)
            {
                // The NPC owner already completed the player's E arrest
                // interaction. Register that same player as the Convoy escort
                // so the shared physical attachment/loading path starts from
                // EscortApproach instead of asking for a second handcuff.
                _playerEscortPrisoners.Add(prisoner.Handle);
                _prisonerEscortOfficers[prisoner.Handle] = player;
                SetPrisonerLoadingStage(
                    prisoner,
                    player,
                    PrisonerLoadingStage.EscortApproach,
                    DateTime.UtcNow,
                    "NpcContactPlayerArrestAlreadyCompleted");
                LogRuntime(
                    "POLICE_CONVOY_PLAYER_ESCORT_PRESECURED",
                    "Prisoner=" + prisoner.Handle + "; Player=" + player.Handle
                    + "; Vehicle=" + playerVehicle.Handle
                    + "; Source=NpcContact");
            }
            CaptureAndSetWaypoint(pickupTarget);
            LogRuntime(
                "POLICE_CUSTODY_PLAYER_VEHICLE_SELECTED",
                "Prisoner=" + prisoner.Handle + "; Vehicle=" + playerVehicle.Handle
                + "; Model=" + playerVehicle.Model.Hash
                + "; Station=" + _stationDisplayName);
            Notify(prisonerAlreadySecuredByPlayer
                ? "~b~POLICE CUSTODY~s~\nWalk with the handcuffed citizen to the marked rear door. Press E to open it, then E again to load them."
                : "~b~POLICE CUSTODY~s~\nExit your Police car, press E beside the prisoner, and escort them to the rear door.");
            return prisonerAlreadySecuredByPlayer
                ? "Player Police vehicle selected. Escort the handcuffed citizen to the marked rear door, then use E to open and load them."
                : "Player Police vehicle selected. Exit the car, press E beside the prisoner, then escort and load them physically.";
        }

        /// <summary>
        /// Starts the explicit Police UI Convoy Request. It uses Dispatch's
        /// criminal-profile data but does not create a Dispatch case or a
        /// second incident. A secured prisoner is staged at the selected
        /// station first, then the normal physical prison-transfer flow owns
        /// every step from vehicle arrival to booking.
        /// </summary>
        internal string StartRequestedConvoy(
            LSPDCriminalProfileDefinition prisonerProfile,
            LSPDCriminalProfileDefinition routeThreatProfile)
        {
            if (!_settings.Enabled || !_settings.RequestedConvoyEnabled)
                return "Player Convoy Request is disabled in LS Immersive settings.";
            if (Completed)
                ConsumeCompletedState();
            if (Failed)
                return "Recover or reset the failed Convoy before starting another one.";
            if (Active)
                return "A prisoner custody operation is already active.";
            if (prisonerProfile == null
                || string.IsNullOrWhiteSpace(prisonerProfile.ModelName)
                || string.IsNullOrWhiteSpace(prisonerProfile.VehicleModelName)
                || string.IsNullOrWhiteSpace(prisonerProfile.WeaponName)
                || string.IsNullOrWhiteSpace(prisonerProfile.PedAssetId)
                || string.IsNullOrWhiteSpace(prisonerProfile.VehicleAssetId)
                || string.IsNullOrWhiteSpace(prisonerProfile.WeaponAssetId))
                return "Convoy Request has no complete validated XML criminal asset selection.";

            LSPDPoliceStationDefinition station = SelectedStation();
            LSPDPoliceStationDefinition prison = _profile == null ? null : _profile.FindStation("bolingbroke");
            if (station == null)
                return "No operational Police station is configured.";
            if (prison == null)
                return "No prison destination is configured.";

            ResetOperationFields();
            _operationId = Guid.NewGuid().ToString("N");
            _isRequestedConvoyActivity = true;
            _pendingPrisonerModelName = prisonerProfile.ModelName;
            _requestedPrisonerProfileId = prisonerProfile.Id ?? string.Empty;
            _routeThreatModelName = routeThreatProfile == null ? string.Empty : routeThreatProfile.ModelName;
            _routeThreatVehicleModelName = routeThreatProfile == null
                ? string.Empty : routeThreatProfile.VehicleModelName;
            _routeThreatWeaponName = routeThreatProfile == null ? string.Empty : routeThreatProfile.WeaponName;
            _stationDestination = StationHandoffPosition(station);
            _stationIntakeDestination = StationIntakePosition(station);
            _prisonDestination = new Vector3(prison.ExteriorX, prison.ExteriorY, prison.ExteriorZ);
            _stationDisplayName = station.DisplayName;
            _prisonDisplayName = prison.DisplayName;
            _state = LSPDDispatchState.PrisonTransfer;
            _failedIsRecoverable = false;
            _preparationDeadline = DateTime.UtcNow.AddSeconds(ModelPreparationTimeoutSeconds);
            SetPhase(CustodyPhase.PreparingRequestedPrisoner);
            CaptureAndSetWaypoint(_stationDestination);
            RequestRequestedPrisonerModel();
            ReportStage("lsimmersivelife.police.transport.requested", "requested-convoy");
            LogRuntime(
                "POLICE_CONVOY_REQUEST_STARTED",
                "PrisonerProfile=" + _requestedPrisonerProfileId
                + "; Station=" + _stationDisplayName
                + "; Prison=" + _prisonDisplayName
                + "; RouteThreat=" + (!string.IsNullOrWhiteSpace(_routeThreatModelName)));
            return "Convoy Request accepted. A secured prisoner is being staged at "
                + _stationDisplayName + "; Dispatch and Crime Activity will hold until transport ends.";
        }

        internal string ContinueToPrison()
        {
            if (!HoldingAtStation)
                return "No prisoner is waiting at the selected Police station.";
            if (!ValidPrisoners().Any())
                return Fail("No living prisoner remained at the station.", false);

            QueueCurrentTransportCleanup();
            _transport = null;
            _transportDriver = null;
            _state = LSPDDispatchState.PrisonTransfer;
            ReportStage("lsimmersivelife.police.transport.approved", "approved");
            BeginTransportPreparation(CustodyPhase.PreparingPrisonTransport, VehicleName("fbi2", "policet"));
            return "Prison transfer approved. A prison convoy unit is staging near the station.";
        }

        internal string DeclineAtStation()
        {
            if (!HoldingAtStation)
                return "No prisoner is waiting at the selected Police station.";
            ReportTerminal("lsimmersivelife.police.transport.declined", "declined");
            Complete("Custody completed at the station. Prison transfer declined.", true);
            return "Prison transfer declined. Custody completed at the station.";
        }

        internal string AcceptDispatchStationTransport()
        {
            if (!DispatchStationTransportDecisionPending)
                return "No Dispatch station transport choice is waiting.";
            if (!AllDispatchPrisonersPhysicallyLoaded())
                return "Every secured suspect must be confirmed in a rear custody seat before the station transport choice.";

            if (!ReleaseDriverForPlayer(DateTime.UtcNow))
                return "The transport officer is exiting the van. The station drive will continue when the driver's seat is clear.";

            _dispatchCrewStationTransport = false;
            CaptureAndSetWaypoint(_stationDestination);
            ReportStage("lsimmersivelife.police.transport.departing", "station-departing");
            SetPhase(CustodyPhase.DriveToStation);
            LogRuntime("POLICE_CONVOY_DISPATCH_STATION_TRANSPORT_ACCEPTED",
                "Operation=" + _operationId + "; Vehicle="
                + (_transport == null || !_transport.Exists() ? "none" : _transport.Handle.ToString())
                + "; Prisoners=" + DescribePrisoners()
                + "; Driver=Player; PhysicalSeatChecks=true");
            Notify("~b~POLICE CUSTODY~s~\nTake the transport and drive the secured suspects to " + _stationDisplayName + ".");
            return "Station transport accepted. Take the transport and drive to " + _stationDisplayName + ".";
        }

        internal string DeclineDispatchStationTransport()
        {
            if (!DispatchStationTransportDecisionPending)
                return "No Dispatch station transport choice is waiting.";
            if (!AllDispatchPrisonersPhysicallyLoaded())
                return "Every secured suspect must be confirmed in a rear custody seat before the station transport choice.";
            if (_transport == null || !_transport.Exists())
                return "The transport van is unavailable. Dispatch custody remains active.";

            if (_dispatchGroupPlayerDrivenPickup)
            {
                if (!_dispatchGroupStationDeclineLogged)
                {
                    _dispatchGroupStationDeclineLogged = true;
                    LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_STATION_DRIVE_DEFERRED",
                        "Operation=" + _operationId
                        + "; Vehicle=" + _transport.Handle
                        + "; Prisoners=" + DescribePrisoners()
                        + "; PlayerChoice=DeferredAtScene"
                        + "; Driver=Player; AIStationDrive=false"
                        + "; VanCleanup=false; CustodyRetained=true");
                    Notify("~b~POLICE CUSTODY~s~\nThe van and secured suspects will remain here. Press Y when you are ready to drive to the Police Station.");
                }
                return "Station transport deferred. The van remains parked with custody active; press Y when you are ready to continue.";
            }

            Ped crewDriver = _dispatchSceneTransportDriver;
            if (crewDriver == null || !crewDriver.Exists() || crewDriver.IsDead)
            {
                crewDriver = TryReassignDispatchCrewDriver("DriverUnavailableAtStationChoice");
                if (crewDriver == null)
                    return ContinueDispatchStationTransportWithPlayer(
                        "No live Convoy crew officer remained for the declined station drive.");
            }

            Ped currentDriver = null;
            try
            {
                currentDriver = Function.Call<Ped>(
                    Hash.GET_PED_IN_VEHICLE_SEAT,
                    _transport,
                    (int)VehicleSeat.Driver,
                    false);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_CREW_DRIVER_SEAT_CHECK_FAILED", ex);
                return "The transport driver seat could not be checked. Custody remains active; choose again when safe.";
            }
            if (currentDriver != null && currentDriver.Exists()
                && currentDriver.Handle != crewDriver.Handle)
                return "The transport driver's seat is occupied. Exit the seat or accept the station drive; custody remains active.";

            _transportDriver = crewDriver;
            _dispatchCrewStationTransport = true;
            _dispatchCrewDriverSeatOccupantHandle = 0;
            _lastDispatchCrewDriverSeatCheckAt = DateTime.MinValue;
            _lastDispatchCrewDriverEntryTaskAt = DateTime.MinValue;
            _dispatchCrewDriverEntryStartedAt = DateTime.UtcNow;
            _lastDispatchCrewRouteRecoveryCheckAt = DateTime.MinValue;
            _lastDispatchCrewArrivalCheckAt = DateTime.MinValue;
            _lastDispatchCrewStationRouteProgressAt = DateTime.MinValue;
            _lastDispatchCrewStationGoalDistance = float.MaxValue;
            _dispatchCrewRouteRecoveryTarget = Vector3.Zero;
            _dispatchCrewDepartureOrigin = _transport.Position;
            _dispatchCrewDepartureStartedAt = DateTime.MinValue;
            _transportRouteRecoveryCount = 0;
            _dispatchCrewDriverSeatConflictLogged = false;
            _dispatchCrewPlayerRecoveryPending = false;
            _dispatchCrewPlayerRecoveryReason = string.Empty;
            _dispatchCrewOfficerEntryTaskAt = DateTime.MinValue;
            _dispatchCrewOfficerEntryTaskHandle = 0;
            _dispatchCrewOfficerSeatConflictLogged = false;
            _dispatchCrewExitStagingAttempted = false;
            _dispatchCrewExitStagingPending = false;
            _dispatchCrewExitStagingReached = false;
            _dispatchTransportRouteRoadTarget = Vector3.Zero;
            _dispatchTransportRouteDestination = Vector3.Zero;
            CleanupWaypoint();
            SetPhase(CustodyPhase.DispatchCrewDriverBoarding);
            LogRuntime("POLICE_CONVOY_DISPATCH_STATION_TRANSPORT_DECLINED",
                "Operation=" + _operationId + "; Vehicle=" + _transport.Handle
                + "; Prisoners=" + DescribePrisoners()
                + "; Driver=" + crewDriver.Handle
                + "; PlayerChoice=DeclinedStationTransport"
                + "; CustodyRetained=true; PhysicalDriverEntryRequired=true"
                + "; RequiredCrewBoardingConfirmation=true"
                + "; PlayerDispatchEndsNow=false");
            LogRuntime("POLICE_CONVOY_DISPATCH_CREW_CUSTODY_HANDOFF_PENDING",
                "Operation=" + _operationId + "; Vehicle=" + _transport.Handle
                + "; Prisoners=" + DescribePrisoners()
                + "; AllAssignedRearSeatsConfirmed=true"
                + "; ConvoyOfficerAndVehicleOwnerConfirmed=true"
                + "; PlayerChoice=DeclinedStationTransport"
                + "; RequiredDriverSeatConfirmationPending=true"
                + "; PlayerDispatchEndsNow=false");
            Notify("~b~POLICE CUSTODY~s~\nThe suspects are secured in the transport. The assigned officer is boarding; Dispatch will close when the crew is physically aboard.");
            return "Station transport declined. The transport crew is boarding before Dispatch closes.";
        }

        internal bool ConsumeDispatchCrewCustodyHandoffReady()
        {
            if (!_dispatchCrewCustodyHandoffReady)
                return false;
            _dispatchCrewCustodyHandoffReady = false;
            return true;
        }

        internal void ConsumeFailedCrewBackgroundState()
        {
            if (Failed && _dispatchClosedAfterCrewCustodyHandoff)
                ClearTerminalState();
        }

        private bool AllDispatchPrisonersPhysicallyLoaded()
        {
            if (_transport == null || !_transport.Exists())
                return false;
            List<Ped> prisoners = ValidPrisoners().ToList();
            return prisoners.Count > 0
                && prisoners.All(prisoner => IsPrisonerInAssignedTransportSeat(
                    prisoner, PrisonerSeat(prisoner, prisoners)));
        }

        private bool EnsureRequiredDispatchCrewOfficersBoarded(
            DateTime now,
            Ped driver)
        {
            if (_transport == null || !_transport.Exists()
                || driver == null || !driver.Exists() || driver.IsDead
                || !AllDispatchPrisonersPhysicallyLoaded())
                return false;

            List<Ped> prisoners = ValidPrisoners().ToList();
            int requiredEscortCount = Math.Min(2, prisoners.Count);
            List<Ped> requiredOfficers = new List<Ped>();
            Ped player = Game.Player == null ? null : Game.Player.Character;
            foreach (Ped prisoner in prisoners)
            {
                Ped assigned;
                if (!_prisonerEscortOfficers.TryGetValue(prisoner.Handle, out assigned)
                    || assigned == null || !assigned.Exists() || assigned.IsDead
                    || player != null && player.Exists() && assigned.Handle == player.Handle)
                {
                    assigned = _sceneEscortOfficers
                        .Where(officer => officer != null && officer.Exists()
                            && !officer.IsDead
                            && (player == null || !player.Exists()
                                || officer.Handle != player.Handle)
                            && !requiredOfficers.Any(existing =>
                                existing.Handle == officer.Handle))
                        .OrderBy(officer => officer.Position.DistanceTo(prisoner.Position))
                        .FirstOrDefault();
                }

                if (assigned != null && assigned.Exists() && !assigned.IsDead
                    && !requiredOfficers.Any(existing => existing.Handle == assigned.Handle))
                    requiredOfficers.Add(assigned);
                if (requiredOfficers.Count >= requiredEscortCount)
                    break;
            }

            if (!requiredOfficers.Any(officer => officer.Handle == driver.Handle))
                requiredOfficers.Add(driver);
            if (requiredOfficers.Count < requiredEscortCount)
            {
                if (!_dispatchCrewOfficerSeatConflictLogged)
                {
                    _dispatchCrewOfficerSeatConflictLogged = true;
                    LogRuntime("POLICE_CONVOY_DISPATCH_CREW_REQUIRED_OFFICER_UNAVAILABLE",
                        "Operation=" + _operationId
                        + "; RequiredSceneEscorts=" + requiredEscortCount
                        + "; LiveAssignedOfficers=" + requiredOfficers.Count
                        + "; CustodyRetained=true; DispatchStillActive=true");
                }
                return false;
            }

            foreach (Ped officer in requiredOfficers)
            {
                if (officer.Handle == driver.Handle)
                {
                    if (DispatchCrewDriverSeatOccupant(now) != driver.Handle)
                        return false;
                    continue;
                }

                Ped passenger = null;
                try
                {
                    passenger = Function.Call<Ped>(
                        Hash.GET_PED_IN_VEHICLE_SEAT,
                        _transport,
                        (int)VehicleSeat.RightFront,
                        false);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_DISPATCH_CREW_PASSENGER_SEAT_CHECK_FAILED", ex);
                    return false;
                }

                if (passenger != null && passenger.Exists()
                    && passenger.Handle == officer.Handle)
                {
                    _dispatchCrewOfficerEntryTaskHandle = 0;
                    _dispatchCrewOfficerEntryTaskAt = DateTime.MinValue;
                    continue;
                }
                if (passenger != null && passenger.Exists())
                {
                    if (!_dispatchCrewOfficerSeatConflictLogged)
                    {
                        _dispatchCrewOfficerSeatConflictLogged = true;
                        LogRuntime("POLICE_CONVOY_DISPATCH_CREW_REQUIRED_SEAT_OCCUPIED",
                            "Operation=" + _operationId
                            + "; Vehicle=" + _transport.Handle
                            + "; RequiredOfficer=" + officer.Handle
                            + "; Occupant=" + passenger.Handle
                            + "; Seat=RightFront; CustodyRetained=true");
                    }
                    return false;
                }

                _dispatchCrewOfficerSeatConflictLogged = false;
                if (_dispatchCrewOfficerEntryTaskHandle != officer.Handle
                    || _dispatchCrewOfficerEntryTaskAt == DateTime.MinValue
                    || now >= _dispatchCrewOfficerEntryTaskAt.AddMilliseconds(
                        DispatchCrewDriverEntryTaskRefreshMilliseconds))
                {
                    try
                    {
                        officer.Task.EnterVehicle(
                            _transport, VehicleSeat.RightFront, 10000, 1.0f);
                        _dispatchCrewOfficerEntryTaskHandle = officer.Handle;
                        _dispatchCrewOfficerEntryTaskAt = now;
                        LogRuntime("POLICE_CONVOY_DISPATCH_CREW_REQUIRED_OFFICER_ENTRY_TASK",
                            "Operation=" + _operationId
                            + "; Officer=" + officer.Handle
                            + "; Vehicle=" + _transport.Handle
                            + "; Seat=RightFront; PhysicalEntryRequired=true");
                    }
                    catch (Exception ex)
                    {
                        _dispatchCrewOfficerEntryTaskAt = now;
                        LogException(
                            "POLICE_CONVOY_DISPATCH_CREW_REQUIRED_OFFICER_ENTRY_FAILED", ex);
                    }
                }
                return false;
            }

            return requiredOfficers.All(officer => officer != null
                && officer.Exists() && !officer.IsDead
                && IsOfficerInTransport(officer));
        }

        private Ped TryReassignDispatchCrewDriver(string reason, int excludedHandle = 0)
        {
            Ped previousDriver = _dispatchSceneTransportDriver;
            int previousHandle = previousDriver != null && previousDriver.Exists()
                ? previousDriver.Handle : 0;
            Ped replacement = _sceneEscortOfficers
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead
                    && officer.Handle != excludedHandle
                    && (previousHandle == 0 || officer.Handle != previousHandle))
                // Prefer an officer who is already outside, but retain the
                // surviving front passenger as a real crew fallback. The
                // boarding state releases that owned officer before assigning
                // the driver's seat; no new actor or forced seat insertion is
                // required when the original driver became unavailable.
                .OrderBy(officer => IsOfficerInTransport(officer) ? 1 : 0)
                .ThenBy(officer => _transport == null || !_transport.Exists()
                    ? 0f : officer.Position.DistanceTo(_transport.Position))
                .FirstOrDefault();
            if (replacement == null)
                return null;

            _dispatchSceneTransportDriver = replacement;
            _transportDriver = replacement;
            _dispatchCrewDriverSeatOccupantHandle = 0;
            _lastDispatchCrewDriverSeatCheckAt = DateTime.MinValue;
            _lastDispatchCrewDriverEntryTaskAt = DateTime.MinValue;
            _dispatchCrewDriverEntryStartedAt = DateTime.UtcNow;
            _dispatchCrewDriverSeatConflictLogged = false;
            LogRuntime("POLICE_CONVOY_DISPATCH_CREW_DRIVER_REASSIGNED",
                "Operation=" + _operationId
                + "; PreviousDriver=" + (previousHandle == 0 ? "unavailable" : previousHandle.ToString())
                + "; NewDriver=" + replacement.Handle
                + "; Vehicle=" + (_transport == null || !_transport.Exists()
                    ? "unavailable" : _transport.Handle.ToString())
                + "; Reason=" + reason
                + "; Owner=ConvoySceneEscort");
            return replacement;
        }

        private string ContinueDispatchStationTransportWithPlayer(string reason)
        {
            if (_transport == null || !_transport.Exists()
                || !AllDispatchPrisonersPhysicallyLoaded()
                || _stationDestination == Vector3.Zero)
                return "The transport crew is unavailable. Convoy custody remains active; the station drive cannot start until the loaded van and destination are verified.";

            _dispatchCrewStationTransport = false;
            _dispatchCrewPlayerRecoveryPending = false;
            _dispatchCrewPlayerRecoveryReason = string.Empty;
            _dispatchSceneTransportDriver = null;
            _transportDriver = null;
            _dispatchCrewRouteRecoveryTarget = Vector3.Zero;
            CaptureAndSetWaypoint(_stationDestination);
            ReportStage("lsimmersivelife.police.transport.departing", "station-departing");
            SetPhase(CustodyPhase.DriveToStation);
            LogRuntime("POLICE_CONVOY_DISPATCH_STATION_PLAYER_RECOVERY",
                "Operation=" + _operationId + "; Vehicle=" + _transport.Handle
                + "; Prisoners=" + DescribePrisoners()
                + "; Driver=Player; CustodyRetained=true; Reason=" + reason);
            Notify("~b~POLICE CUSTODY~s~\nThe transport crew could not continue safely. Take the loaded van and drive the secured suspects to " + _stationDisplayName + ".");
            return "Convoy custody is retained. The transport crew could not continue safely; take the loaded van and drive to " + _stationDisplayName + ".";
        }

        private void BeginDispatchCrewPlayerDriveRecovery(string reason)
        {
            if (_dispatchCrewPlayerRecoveryPending)
                return;

            _dispatchCrewPlayerRecoveryPending = true;
            _dispatchCrewPlayerRecoveryReason = reason;
            _driverReleaseRequested = false;
            _driverReleaseRequestedAt = DateTime.MinValue;
            try
            {
                SetEmergencySignals(_transport, false);
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, true);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_CREW_PLAYER_RECOVERY_BRAKE_FAILED", ex);
            }
            LogRuntime("POLICE_CONVOY_DISPATCH_CREW_PLAYER_RECOVERY_STARTED",
                "Operation=" + _operationId + "; Vehicle=" + _transport.Handle
                + "; Prisoners=" + DescribePrisoners()
                + "; CustodyRetained=true; Reason=" + reason
                + "; Action=StopVanAndReleaseDriverForPlayer");
            Notify("~b~POLICE CUSTODY~s~\nThe transport route is blocked. The van is stopping; take over and drive the secured suspects to the station.");
        }

        private string ProcessDispatchCrewPlayerDriveRecovery(DateTime now)
        {
            if (_transport == null || !_transport.Exists()
                || !AllDispatchPrisonersPhysicallyLoaded())
                return "Convoy custody remains active, but the van or loaded-prisoner check is unavailable. Recovery is waiting for a valid vehicle and confirmed seats.";

            if (Math.Abs(_transport.Speed) > DispatchReleaseStoppedSpeed)
            {
                try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, true); }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_DISPATCH_CREW_PLAYER_RECOVERY_BRAKE_FAILED", ex);
                }
                return string.Empty;
            }

            if (!ReleaseDriverForPlayer(now))
                return string.Empty;

            string reason = _dispatchCrewPlayerRecoveryReason;
            try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, false); }
            catch { }
            return ContinueDispatchStationTransportWithPlayer(reason);
        }

        private string RecoverOrRelinquishClosedCrewRun(string reason, DateTime now)
        {
            if (_dispatchClosedAfterCrewCustodyHandoff)
                return RelinquishDeclinedCrewRun(reason);

            BeginDispatchCrewPlayerDriveRecovery(reason);
            return ProcessDispatchCrewPlayerDriveRecovery(now);
        }

        private string FailOrRelinquishClosedCrewRun(string reason, bool recoverable)
        {
            if (_dispatchClosedAfterCrewCustodyHandoff)
                return RelinquishDeclinedCrewRun(reason);

            return Fail(reason, recoverable);
        }

        private string RelinquishDeclinedCrewRun(string reason)
        {
            int vehicleHandle = _transport != null && _transport.Exists()
                ? _transport.Handle : 0;
            int driverHandle = _dispatchSceneTransportDriver != null
                && _dispatchSceneTransportDriver.Exists()
                ? _dispatchSceneTransportDriver.Handle : 0;
            bool allPrisonersLoaded = AllDispatchPrisonersPhysicallyLoaded();
            DateTime now = DateTime.UtcNow;
            Vector3 departureTarget = Vector3.Zero;
            string departureTaskReason = "NotAttempted";
            bool finalDepartureTaskIssued = allPrisonersLoaded
                && TryIssueDeclinedCrewFinalDepartureTask(
                    now, out departureTarget, out departureTaskReason);
            if (!allPrisonersLoaded)
            {
                departureTarget = Vector3.Zero;
                departureTaskReason = "NotIssued;AllAssignedRearSeatsNotConfirmed";
            }
            int cleanupActorCount = QueueDeclinedCrewDepartureCleanup(now);
            LogRuntime("POLICE_CONVOY_DISPATCH_DECLINED_CREW_RUN_RELINQUISHED",
                "Operation=" + _operationId
                + "; Vehicle=" + vehicleHandle
                + "; Driver=" + driverHandle
                + "; AllAssignedRearSeatsConfirmed=" + allPrisonersLoaded
                + "; PlayerDispatchAlreadyEnded=true"
                + "; PlayerStationRouteStarted=false"
                + "; StationHandoffConfirmed=false"
                + "; FinalDepartureTask=" + (finalDepartureTaskIssued
                    ? "StationRoute" : "Unavailable")
                + "; FinalDepartureTaskReason=" + departureTaskReason
                + "; FinalDepartureTarget=" + departureTarget
                + "; CleanupActorCount=" + cleanupActorCount
                + "; Cleanup=PlayerSafeDistanceOrHardTimeout"
                + "; CleanupDistance=" + CleanupDistance.ToString(
                    "0.0", CultureInfo.InvariantCulture)
                + "; CleanupHardTimeoutSeconds=" + DeferredCleanupMaximumSeconds
                + "; Reason=" + (reason ?? string.Empty));

            if (!finalDepartureTaskIssued
                && _transport != null && _transport.Exists() && !_transport.IsDead)
            {
                try
                {
                    SetEmergencySignals(_transport, false);
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, false);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_DISPATCH_DECLINED_CREW_RELEASE_FAILED", ex);
                }
            }

            string message = allPrisonersLoaded
                ? finalDepartureTaskIssued
                    ? "Dispatch is clear. The transport crew received a final station-bound drive task; its owned crew and van will be cleaned up after you leave the area or the bounded timeout. Station arrival was not confirmed."
                    : "Dispatch is clear. The crew could not confirm a final station-bound drive task; its owned crew and van will remain until you leave the area or the bounded timeout. Station arrival was not confirmed."
                : "Dispatch is clear, but the transport crew could not confirm all assigned rear seats. Its owned scene actors will remain until you leave the area or the bounded timeout; station arrival was not confirmed.";
            Notify("~b~POLICE CUSTODY~s~\n" + message);
            // The Player ended Dispatch at the explicit crew-custody choice.
            // Preserve the background actors through a final departure attempt,
            // then remove the whole owned group together after the safe-distance
            // grace or bounded hard timeout. This path does not claim a Station
            // handoff when the route actually failed.
            ClearTerminalState();
            return message;
        }

        private bool TryIssueDeclinedCrewFinalDepartureTask(
            DateTime now,
            out Vector3 target,
            out string reason)
        {
            target = Vector3.Zero;
            reason = "TransportOrStationTargetUnavailable";
            if (_transport == null || !_transport.Exists() || _transport.IsDead
                || _stationDestination == Vector3.Zero)
                return false;

            Ped driver = _dispatchSceneTransportDriver;
            if (driver == null || !driver.Exists() || driver.IsDead)
            {
                reason = "NoLiveConvoyDriver";
                return false;
            }

            _lastDispatchCrewDriverSeatCheckAt = DateTime.MinValue;
            if (DispatchCrewDriverSeatOccupant(now) != driver.Handle)
            {
                reason = "DriverNotConfirmedInDriverSeat";
                return false;
            }

            _transportDriver = driver;
            Vector3 requestedTarget = _dispatchCrewRouteRecoveryTarget != Vector3.Zero
                ? _dispatchCrewRouteRecoveryTarget : _stationDestination;
            Vector3 roadTarget;
            float routeDistance;
            string routeFailure;
            float maximumDistance = IsDispatchSceneRoadPickup(false)
                ? DispatchSceneVehicleArrivalRadius : ArrivalRadius;
            if (!TryResolveDispatchTransportDestination(
                requestedTarget,
                _transport.Position,
                maximumDistance,
                out roadTarget,
                out routeDistance,
                out routeFailure))
            {
                reason = "FinalRoadTargetRejected;" + (routeFailure ?? "unknown");
                LogRuntime("POLICE_CONVOY_DISPATCH_CREW_FINAL_DEPARTURE_REJECTED",
                    "Operation=" + _operationId
                    + "; Vehicle=" + _transport.Handle
                    + "; Driver=" + driver.Handle
                    + "; RequestedTarget=" + requestedTarget
                    + "; Reason=" + reason
                    + "; TaskIssued=false; Teleport=false");
                return false;
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, false);
                SetEmergencySignals(_transport, true);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    driver,
                    _transport,
                    roadTarget.X,
                    roadTarget.Y,
                    roadTarget.Z,
                    22.0f,
                    (int)EmergencyDrivingFlags,
                    10.0f);
                _lastDriverTaskAt = now;
                target = roadTarget;
                reason = "FinalStationRoadTaskIssued";
                LogRuntime("POLICE_CONVOY_DISPATCH_CREW_FINAL_DEPARTURE_TASK",
                    "Operation=" + _operationId
                    + "; Vehicle=" + _transport.Handle
                    + "; Driver=" + driver.Handle
                    + "; RequestedTarget=" + requestedTarget
                    + "; ValidatedRoadTarget=" + roadTarget
                    + "; RouteDistance=" + routeDistance.ToString(
                        "0.0", CultureInfo.InvariantCulture)
                    + "; AssignedRearSeatsConfirmed=true"
                    + "; Task=TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE"
                    + "; Teleport=false");
                return true;
            }
            catch (Exception ex)
            {
                reason = "FinalStationRoadTaskFailed;Exception=" + ex.GetType().Name;
                LogException("POLICE_CONVOY_DISPATCH_CREW_FINAL_DEPARTURE_TASK_FAILED", ex);
                return false;
            }
        }

        private int QueueDeclinedCrewDepartureCleanup(DateTime now)
        {
            var relatedPeds = _prisoners
                .Concat(_sceneEscortOfficers)
                .Concat(_stationHandoffOfficers)
                .Where(ped => ped != null && ped.Exists())
                .GroupBy(ped => ped.Handle)
                .Select(group => group.First())
                .ToList();
            if (_dispatchSceneTransportDriver != null
                && _dispatchSceneTransportDriver.Exists()
                && relatedPeds.All(ped => ped.Handle != _dispatchSceneTransportDriver.Handle))
                relatedPeds.Add(_dispatchSceneTransportDriver);

            Vehicle vehicle = _transport != null && _transport.Exists()
                && !_playerOwnedTransport ? _transport : null;
            Ped primaryPed = vehicle == null ? relatedPeds.FirstOrDefault() : null;
            if (primaryPed == null && vehicle == null)
                return 0;

            _deferredCleanup.Add(new DeferredCleanup
            {
                Ped = primaryPed,
                Vehicle = vehicle,
                RelatedPeds = relatedPeds
                    .Where(ped => primaryPed == null || ped.Handle != primaryPed.Handle)
                    .ToList(),
                Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                Expires = now.AddSeconds(DeferredCleanupMaximumSeconds)
            });
            return relatedPeds.Count + (vehicle == null ? 0 : 1);
        }

        private bool TryRecoverDispatchCrewStationRoute(DateTime now)
        {
            if (_transport == null || !_transport.Exists()
                || _transportDriver == null || !_transportDriver.Exists()
                || _stationDestination == Vector3.Zero)
                return false;

            if (_dispatchCrewRouteRecoveryTarget != Vector3.Zero
                && _transport.Position.DistanceTo(_dispatchCrewRouteRecoveryTarget) <= 12f)
            {
                bool sceneExitReached = _dispatchCrewExitStagingPending;
                LogRuntime("POLICE_CONVOY_DISPATCH_CREW_ROUTE_WAYPOINT_REACHED",
                    "Operation=" + _operationId + "; Vehicle=" + _transport.Handle
                    + "; RoadTarget=" + _dispatchCrewRouteRecoveryTarget
                    + "; Stage=" + (sceneExitReached ? "SceneExit" : "StationRouteRecovery")
                    + "; NextTarget=Station");
                _dispatchCrewRouteRecoveryTarget = Vector3.Zero;
                if (sceneExitReached)
                {
                    _dispatchCrewExitStagingPending = false;
                    _dispatchCrewExitStagingReached = true;
                }
                _lastDispatchCrewStationRouteProgressAt = now;
                _lastDispatchCrewStationGoalDistance = _transport.Position.DistanceTo(_stationDestination);
                return false;
            }

            Vector3 goal = _dispatchCrewRouteRecoveryTarget == Vector3.Zero
                ? _stationDestination : _dispatchCrewRouteRecoveryTarget;
            float distance = _transport.Position.DistanceTo(goal);
            if (_dispatchCrewRouteRecoveryTarget == Vector3.Zero
                && distance <= _settings.StationArrivalRadius)
                return false;
            if (_lastDispatchCrewStationRouteProgressAt == DateTime.MinValue
                || _lastDispatchCrewStationGoalDistance == float.MaxValue)
            {
                _lastDispatchCrewStationRouteProgressAt = now;
                _lastDispatchCrewStationGoalDistance = distance;
                return false;
            }
            if (_lastDispatchCrewStationGoalDistance - distance >= 4f)
            {
                _lastDispatchCrewStationRouteProgressAt = now;
                _lastDispatchCrewStationGoalDistance = distance;
                return false;
            }
            if (now < _lastDispatchCrewStationRouteProgressAt.AddSeconds(TransportRouteStallTimeoutSeconds))
                return false;
            if (_transportRouteRecoveryCount >= MaximumTransportRouteRecoveries)
                return false;

            Vector3 recoveryAnchor = _dispatchCrewExitStagingReached
                ? _stationDestination : _transport.Position;
            Vector3 roadTarget;
            float routeDistance;
            string routeFailure;
            if (!TryResolveDispatchTransportRecoveryRoadTarget(
                recoveryAnchor,
                _transportRouteRecoveryCount + 1,
                out roadTarget,
                out routeDistance,
                out routeFailure))
            {
                _transportRouteRecoveryCount++;
                _lastDispatchCrewStationRouteProgressAt = now;
                LogRuntime("POLICE_CONVOY_DISPATCH_CREW_ROUTE_RECOVERY_REJECTED",
                    "Operation=" + _operationId
                    + "; Vehicle=" + _transport.Handle
                    + "; Recovery=" + _transportRouteRecoveryCount
                    + "; Stage=" + (_dispatchCrewExitStagingReached
                        ? "StationRoute" : "SceneExit")
                    + "; Anchor=" + recoveryAnchor
                    + "; Reason=" + (routeFailure ?? "NoReachableNearbyRoadTarget")
                    + "; TaskIssued=false; CustodyRetained=true");
                return false;
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, false);
                SetEmergencySignals(_transport, true);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    _transportDriver,
                    _transport,
                    roadTarget.X,
                    roadTarget.Y,
                    roadTarget.Z,
                    22.0f,
                    (int)EmergencyDrivingFlags,
                    10.0f);
                _transportRouteRecoveryCount++;
                _dispatchCrewRouteRecoveryTarget = roadTarget;
                _lastDispatchCrewStationRouteProgressAt = now;
                _lastDispatchCrewStationGoalDistance = _transport.Position.DistanceTo(roadTarget);
                _lastDriverTaskAt = now;
                LogRuntime("POLICE_CONVOY_DISPATCH_CREW_ROUTE_RECOVERY",
                    "Operation=" + _operationId + "; Vehicle=" + _transport.Handle
                    + "; Driver=" + _transportDriver.Handle
                    + "; Recovery=" + _transportRouteRecoveryCount
                    + "; Stage=" + (_dispatchCrewExitStagingReached
                        ? "StationRoute" : "SceneExit")
                    + "; Station=" + _stationDestination
                    + "; RoadTarget=" + roadTarget
                    + "; RouteDistance=" + routeDistance.ToString(
                        "0.0", CultureInfo.InvariantCulture)
                    + "; PreviousGoalDistance=" + distance.ToString("0.0")
                    + "; CustodyRetained=true; RouteValidated=true; Teleport=false");
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_CREW_ROUTE_RECOVERY_FAILED", ex);
                _lastDispatchCrewStationRouteProgressAt = now;
                return false;
            }
        }

        internal bool RestoreFailedPrisonTransferForRetry()
        {
            if (!FailedAfterStationHandoff || !ValidPrisoners().Any())
                return false;

            ClearDeferredCleanupBeforeRetry();
            // A group can lose one member while the others remain at the
            // station. Do not carry a dead/deleted reference back into the
            // retry phase, or the next availability check would fail forever
            // even though living prisoners are still recoverable.
            List<Ped> living = ValidPrisoners().ToList();
            _prisoners.Clear();
            _prisoners.AddRange(living);
            foreach (Ped prisoner in living)
                PreparePrisoner(prisoner, false);
            _failedIsRecoverable = true;
            _lastFailureReason = string.Empty;
            _state = LSPDDispatchState.HoldingAtStation;
            SetPhase(CustodyPhase.HoldingAtStation);
            LogRuntime(
                "POLICE_CONVOY_PRISON_TRANSFER_RETRY_READY",
                "Living prisoners remained at the station after a recoverable prison-transfer failure; "
                + "Prisoners=" + DescribePrisoners());
            return true;
        }

        internal string CompleteNow()
        {
            if (!_settings.TerminalCompletionEnabled)
                return "Terminal transport completion is disabled in LS Immersive settings.";
            if (!Active)
                return "No active prisoner transport is available.";
            if (!CanUseTerminalCompletion)
                return "Terminal completion is available only after prisoners are loaded for the prison transfer.";
            ReportTerminal("lsimmersivelife.police.transport.completed", "terminal-recovery");
            Complete("Transport marked complete by the officer recovery control.", true);
            return "Prisoner transport marked complete.";
        }

        internal string Process(DateTime now)
        {
            return Process(now, true);
        }

        internal string Process(DateTime now, bool allowGameplayInput)
        {
            ProcessDeferredCleanup(now);
            if (allowGameplayInput)
            {
                bool keyDown = Game.IsKeyPressed(_controls.TransportCompleteKey);
                bool pressed = keyDown && !_terminalKeyDown;
                _terminalKeyDown = keyDown;
                if (pressed && CanUseTerminalCompletion)
                    return CompleteNow();
            }
            else
                _terminalKeyDown = Game.IsKeyPressed(_controls.TransportCompleteKey);

            if (!Active)
                return string.Empty;
            string dispatchReleaseMessage;
            if (ProcessPendingDispatchSuspectRelease(now, out dispatchReleaseMessage))
                return dispatchReleaseMessage;
            // A player-requested Convoy deliberately enters this preparation
            // phase before its XML-selected prisoner exists. Begin validating
            // custody as soon as that prisoner is staged; applying the normal
            // loss guard one tick earlier made every independent request fail.
            bool prisonerReconciliationPending = false;
            if (_phase != CustodyPhase.PreparingRequestedPrisoner
                && !MaintainPrisonerAvailability(now, out prisonerReconciliationPending))
            {
                string lossReason = DescribePrisonerLoss();
                List<Ped> survivingPrisoners = ValidPrisoners().ToList();
                bool recoverable = survivingPrisoners.Count > 0;
                LogStateFailure(
                    "POLICE_CONVOY_PRISONER_OWNERSHIP_LOST",
                    "Phase=" + _phase + "; Reason=" + lossReason
                    + "; OwnedReferences=" + DescribePrisonerReferences());
                if (recoverable)
                    LogRuntime(
                        "POLICE_CONVOY_PARTIAL_PRISONER_LOSS",
                        "Reason=" + lossReason + "; LivingPrisoners="
                        + string.Join(",", survivingPrisoners.Select(ped => ped.Handle.ToString()).ToArray())
                        + "; TransportRecovery=enabled");
                return Fail(lossReason, recoverable);
            }
            if (_phase != CustodyPhase.PreparingRequestedPrisoner
                && prisonerReconciliationPending)
            {
                // Keep Convoy as the sole custody owner while the exact managed
                // handle is re-resolved. Do not advance escort, loading, route,
                // or handoff state from a transient missing-entity sample.
                return string.Empty;
            }
            MaintainActiveCustody(now);
            if (PlayerOwnsPhysicalCustodyInteraction)
                LSPDPlayerControlSuppression.SuppressMeleeThisFrame();
            string playerCustodyMessage = ProcessPlayerCustodyInput(now, allowGameplayInput);
            if (!string.IsNullOrWhiteSpace(playerCustodyMessage))
                return playerCustodyMessage;
            if (RequiresLiveTransport() && !HasTransportVehicle())
            {
                bool recoverable = _phase != CustodyPhase.PrisonHandoff;
                return Fail(
                    "The Police transport was destroyed or lost during custody ("
                    + DescribeTransportLoss() + ").", recoverable);
            }

            switch (_phase)
            {
                case CustodyPhase.PreparingRequestedPrisoner:
                    return ProcessRequestedPrisonerPreparation(now);
                case CustodyPhase.PreparingSceneTransport:
                case CustodyPhase.PreparingPrisonTransport:
                    return ProcessTransportPreparation(now);
                case CustodyPhase.SceneTransportEnRoute:
                    return ProcessTransportArrival(now, false);
                case CustodyPhase.PrisonTransportEnRoute:
                    return ProcessTransportArrival(now, true);
                case CustodyPhase.SceneLoading:
                    return ProcessLoading(now, false);
                case CustodyPhase.DispatchPickupDriverBoarding:
                case CustodyPhase.DispatchCrewDriveToPickup:
                    return ProcessDispatchPickupRoute(now);
                case CustodyPhase.DispatchGroupPickupWaiting:
                    return ProcessDispatchGroupPlayerRoute(now);
                case CustodyPhase.DispatchStationTransportDecision:
                    return string.Empty;
                case CustodyPhase.DispatchCrewDriverBoarding:
                case CustodyPhase.DispatchCrewDriveToStation:
                    return ProcessDispatchCrewStationTransport(now);
                case CustodyPhase.PrisonLoading:
                    return ProcessLoading(now, true);
                case CustodyPhase.DriveToStation:
                    return ProcessPlayerArrivalAtStation(now);
                case CustodyPhase.StationUnloading:
                    return ProcessStationUnloading(now);
                case CustodyPhase.DriveToPrison:
                    return ProcessPlayerArrivalAtPrison(now);
                case CustodyPhase.PrisonHandoff:
                    return ProcessPrisonHandoff(now);
                default:
                    return string.Empty;
            }
        }

        private bool ProcessPendingDispatchSuspectRelease(
            DateTime now,
            out string message)
        {
            message = string.Empty;
            if (_pendingDispatchReleaseHandle == 0)
                return false;

            Ped suspect = _prisoners.FirstOrDefault(value => value != null
                && value.Exists() && value.Handle == _pendingDispatchReleaseHandle);
            if (suspect == null || suspect.IsDead)
            {
                ClearPendingDispatchRelease();
                message = "The suspect is no longer available. Dispatch custody remains active.";
                return true;
            }

            if (!suspect.IsInVehicle())
            {
                ReleasePlayerCustodySuspect(suspect, out message);
                ClearPendingDispatchRelease();
                return true;
            }

            if (!IsInTransport(suspect))
            {
                ClearPendingDispatchRelease();
                message = "The suspect entered another vehicle. The release was stopped and custody remains active.";
                return true;
            }

            if (now >= _dispatchReleaseRequestedAt
                .AddSeconds(DispatchReleaseTimeoutSeconds))
            {
                try { suspect.Task.ClearAll(); } catch { }
                ClearPendingDispatchRelease();
                message = "The suspect did not exit the vehicle. Custody remains active; try again when the vehicle is stopped.";
                LogRuntime("POLICE_CONVOY_DISPATCH_RELEASE_EXIT_TIMED_OUT",
                    "Ped=" + suspect.Handle + "; Vehicle="
                    + (_transport == null ? 0 : _transport.Handle));
                return true;
            }

            if (_transport == null || !_transport.Exists()
                || Math.Abs(_transport.Speed) > DispatchReleaseStoppedSpeed)
                return true;

            if (now >= _nextDispatchReleaseTaskAt
                && _dispatchReleaseTaskAttempts < 4)
            {
                try
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, suspect, _transport, 0);
                    _dispatchReleaseTaskAttempts++;
                    _nextDispatchReleaseTaskAt = now
                        .AddMilliseconds(DispatchReleaseTaskRefreshMilliseconds);
                    LogRuntime("POLICE_CONVOY_DISPATCH_RELEASE_EXIT_RETRY",
                        "Ped=" + suspect.Handle + "; Attempt="
                        + _dispatchReleaseTaskAttempts);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_DISPATCH_RELEASE_EXIT_RETRY_FAILED", ex);
                }
            }
            return true;
        }

        private bool ReleasePlayerCustodySuspect(Ped suspect, out string message)
        {
            message = "The suspect could not be released safely. Custody remains active.";
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.IsInVehicle())
                return false;

            int handle = suspect.Handle;
            Ped escort;
            _prisonerEscortOfficers.TryGetValue(handle, out escort);
            if (escort != null && escort.Exists()
                && IsPrisonerAttachedToEscort(suspect, escort))
            {
                DetachPrisonerFromEscort(suspect, escort,
                    "DispatchReleaseAfterPhysicalExit");
                if (IsPrisonerAttachedToEscort(suspect, escort))
                {
                    LogRuntime("POLICE_CONVOY_DISPATCH_RELEASE_DETACH_PENDING",
                        "Ped=" + handle + "; Escort=" + escort.Handle);
                    return false;
                }
            }

            try
            {
                RestoreConvoyInjuryState(suspect, "DispatchReleaseAfterPhysicalExit");
                suspect.Task.ClearAll();
                RestoreDispatchCustodyMovementStyle(suspect);
                RestoreCustodyInvincibility(suspect);
                if (!_dispatchCustodyOperation)
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        suspect, false, false, false, false, false, false, false, false);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, suspect, false);
                suspect.CanSwitchWeapons = true;
                suspect.BlockPermanentEvents = false;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, suspect, false);
                Function.Call(Hash.SET_PED_KEEP_TASK, suspect, false);
                Function.Call(Hash.TASK_WANDER_STANDARD, suspect, 10.0f, 10);
                suspect.IsPersistent = false;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_RELEASE_FAILED", ex);
                return false;
            }

            RemoveReleasedPrisonerReferences(handle, escort);
            _dispatchReleasedPrisoner = suspect;
            LogRuntime("POLICE_CONVOY_DISPATCH_SUSPECT_RELEASED",
                "Ped=" + handle + "; Vehicle="
                + (_transport == null ? 0 : _transport.Handle)
                + "; PhysicalExitConfirmed=true; Remaining=" + ValidPrisoners().Count());
            message = "The suspect has exited your Police vehicle and was released.";

            if (!ValidPrisoners().Any())
                CompleteAfterDispatchRelease();
            return true;
        }

        private void RemoveReleasedPrisonerReferences(int handle, Ped escort)
        {
            _prisoners.RemoveAll(value => value == null || !value.Exists()
                || value.Handle == handle);
            _lastPrisonerTaskAt.Remove(handle);
            _lastPrisonerVehicleEntryAt.Remove(handle);
            _lastPrisonerVehicleExitAt.Remove(handle);
            _lastAssignedSeatCheckAt.Remove(handle);
            _lastAssignedSeatCheckResult.Remove(handle);
            _prisonerEscortOfficers.Remove(handle);
            _prisonerEscortStartedAt.Remove(handle);
            _physicalCuffReadFailuresLogged.Remove(handle);
            _lastPrisonerEscortPosition.Remove(handle);
            _lastPrisonerEscortProgressAt.Remove(handle);
            _lastPrisonerEscortRecoveryAt.Remove(handle);
            _prisonerLoadingStages.Remove(handle);
            _prisonerLoadingStageStartedAt.Remove(handle);
            _prisonerLoadingTimeoutStartedAt.Remove(handle);
            _lastGroundedEscortFollowAt.Remove(handle);
            _prisonerEscortAlignmentStarted.Remove(handle);
            _prisonerEscortApproachLogged.Remove(handle);
            _prisonerUnloadingOfficers.Remove(handle);
            _lastPrisonerCuffPoseAt.Remove(handle);
            _playerEscortLostAt.Remove(handle);
            _prisonerVehicleDoorsOpened.Remove(handle);
            _prisonerDoorCloseTasksIssued.Remove(handle);
            _prisonerDoorOpenTaskIssuedAt.Remove(handle);
            _dispatchPrisonerMissingSince.Remove(handle);
            _dispatchPrisonerLastHandleRetryAt.Remove(handle);
            _dispatchSceneDoorAnimationFallbackIssued.Remove(handle);
            _prisonerEntryTasksIssued.Remove(handle);
            _prisonerLoadedLogIssued.Remove(handle);
            _playerEscortPrisoners.Remove(handle);
            _playerEscortFallbacks.Remove(handle);
            _playerDoorInteractions.Remove(handle);
            _playerEntryInteractions.Remove(handle);
            _prisonerUnloadingEscortStartedAt.Remove(handle);
            _prisonerUnloadingDoorsOpened.Remove(handle);
            _playerUnloadingStartedAt.Remove(handle);
            _playerUnloadingDoorInteractions.Remove(handle);
            _playerUnloadingTasksIssued.Remove(handle);
            _prisonerInjuryStates.Remove(handle);

            if (escort != null && escort.Exists()
                && !_prisonerEscortOfficers.Values.Any(value => value != null
                    && value.Exists() && value.Handle == escort.Handle))
            {
                _lastEscortOfficerTaskAt.Remove(escort.Handle);
                _lastEscortOfficerPosition.Remove(escort.Handle);
                if (_sceneEscortOfficers.Any(value => value != null
                    && value.Exists() && value.Handle == escort.Handle))
                {
                    try { escort.Task.ClearAll(); } catch { }
                }
            }
        }

        private void ClearPendingDispatchRelease()
        {
            _pendingDispatchReleaseHandle = 0;
            _dispatchReleaseRequestedAt = DateTime.MinValue;
            _nextDispatchReleaseTaskAt = DateTime.MinValue;
            _dispatchReleaseTaskAttempts = 0;
        }

        private void CompleteAfterDispatchRelease()
        {
            CloseActiveAudio();
            RestoreAllCustodyInvincibility();
            QueueOperationCleanup();
            CleanupCustodyBlips();
            CleanupWaypoint();
            _state = LSPDDispatchState.Completed;
            SetPhase(CustodyPhase.Completed);
            _transport = null;
            _transportDriver = null;
            _sceneEscortOfficers.Clear();
            _stationHandoffOfficers.Clear();
            _prisonHandoffOfficers.Clear();
            _routeThreatPeds.Clear();
            _routeThreatVehicle = null;
            _completedAfterDispatchRelease = true;
            LogRuntime("POLICE_CUSTODY_ENDED_BY_DISPATCH_RELEASE",
                "Operation=" + _operationId + "; TransportCompleted=false");
        }

        private string ProcessPlayerCustodyInput(DateTime now, bool allowGameplayInput)
        {
            bool keyDown = Game.IsKeyPressed(_controls.SecureKey);
            bool pressed = allowGameplayInput && keyDown && !_playerCustodyKeyDown;
            _playerCustodyKeyDown = keyDown;
            if (!pressed)
                return string.Empty;
            LSPDPlayerControlSuppression.SuppressMeleeThisFrame();
            if (_phase == CustodyPhase.StationUnloading || _phase == CustodyPhase.PrisonHandoff)
                return ProcessPlayerPrisonerUnloadInput(now);
            if (_phase != CustodyPhase.SceneLoading && _phase != CustodyPhase.PrisonLoading)
                return string.Empty;
            // Remote stops in the scattered-group route are loaded by the
            // existing Backup officer assigned to that suspect. Do not replace
            // that physical handoff with a second Player escort interaction.
            if (IsRemoteDispatchGroupPickup())
                return string.Empty;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return "Player character unavailable for the physical custody interaction.";

            List<Ped> available = ValidPrisoners()
                .Where(prisoner => !IsInTransport(prisoner))
                .OrderBy(prisoner => prisoner.Position.DistanceTo(player.Position))
                .ToList();
            Ped target = available.FirstOrDefault(prisoner =>
                _playerEscortPrisoners.Contains(prisoner.Handle)
                && (prisoner.Position.DistanceTo(player.Position)
                        <= PlayerCustodyInteractionRadius + PlayerCustodyVehicleRadius
                    || (_playerDoorInteractions.Contains(prisoner.Handle)
                        && _transport != null && _transport.Exists()
                        && player.Position.DistanceTo(_transport.Position)
                            <= PlayerCustodyLoadInteractionRadius
                        && prisoner.Position.DistanceTo(_transport.Position)
                            <= PlayerCustodyLoadInteractionRadius)))
                ?? available.FirstOrDefault(prisoner =>
                    prisoner.Position.DistanceTo(player.Position) <= PlayerCustodyInteractionRadius);
            if (target == null)
            {
                LogRuntime("POLICE_CONVOY_PLAYER_CUSTODY_INPUT_REJECTED",
                    "Phase=" + _phase + "; Reason=NoPrisonerInInteractionRange; Player="
                    + player.Handle + "; Position=" + player.Position);
                return "Move within " + PlayerCustodyInteractionRadius.ToString("0.0")
                    + " metres of a secured prisoner before pressing E.";
            }

            bool anotherPlayerEscortActive = _playerEscortPrisoners.Any(handle =>
            {
                Ped assignedPrisoner = _prisoners.FirstOrDefault(value =>
                    value != null && value.Exists() && value.Handle == handle);
                return assignedPrisoner != null && !IsInTransport(assignedPrisoner)
                    && assignedPrisoner.Handle != target.Handle;
            });
            if (anotherPlayerEscortActive)
                return "Finish escorting the current prisoner into the transport before taking another prisoner.";

            if (player.IsInVehicle())
                return "Exit the Police vehicle and stand beside the secured prisoner before pressing E.";

            int prisonerHandle = target.Handle;
            if (_playerEscortFallbacks.Contains(prisonerHandle))
                return "The transport officer has taken over the physical prisoner handoff.";
            DateTime handcuffUntil;
            if (_playerHandcuffAnimationUntil.TryGetValue(prisonerHandle, out handcuffUntil)
                && now < handcuffUntil)
                return "The physical handcuff procedure is still in progress. Stay beside the prisoner.";
            if (!_playerEscortPrisoners.Contains(prisonerHandle))
            {
                bool alreadyPhysicallyCuffed;
                if (!TryGetPhysicalCuffState(target, out alreadyPhysicallyCuffed))
                    return "The suspect's physical handcuff state could not be verified. Try again when the suspect is stable.";

                Ped previousEscort;
                if (_prisonerEscortOfficers.TryGetValue(prisonerHandle, out previousEscort)
                    && previousEscort != null && previousEscort.Exists()
                    && previousEscort.Handle != player.Handle)
                {
                    DetachPrisonerFromEscort(target, previousEscort,
                        "PlayerTookOverPhysicalEscort");
                    try { previousEscort.Task.ClearAll(); } catch { }
                }

                // The previous transport officer may already have opened the
                // rear door or issued EnterVehicle. Player takeover is a new
                // owner boundary: stop that task and clear its timer so the
                // player's explicit E=open, E=load sequence can run once.
                _prisonerEntryTasksIssued.Remove(prisonerHandle);
                _lastPrisonerVehicleEntryAt.Remove(prisonerHandle);
                _lastPrisonerTaskAt.Remove(prisonerHandle);
                _playerDoorInteractions.Remove(prisonerHandle);
                _playerEntryInteractions.Remove(prisonerHandle);
                _playerLoadingContactStartedAt.Remove(prisonerHandle);
                _lastPrisonerEscortPosition.Remove(prisonerHandle);
                _lastPrisonerEscortProgressAt.Remove(prisonerHandle);
                _lastPrisonerEscortRecoveryAt.Remove(prisonerHandle);
                _prisonerEscortAlignmentStarted.Remove(prisonerHandle);
                _lastGroundedEscortFollowAt.Remove(prisonerHandle);
                if (previousEscort != null && previousEscort.Exists())
                {
                    _lastEscortOfficerTaskAt.Remove(previousEscort.Handle);
                    _lastEscortOfficerPosition.Remove(previousEscort.Handle);
                }

                _playerEscortPrisoners.Add(prisonerHandle);
                _prisonerEscortOfficers[prisonerHandle] = player;
                _prisonerEscortStartedAt[prisonerHandle] = now;
                _playerHandcuffAnimationUntil.Remove(prisonerHandle);
                SetPrisonerLoadingStage(target, player,
                    alreadyPhysicallyCuffed
                        ? PrisonerLoadingStage.EscortApproach
                        : PrisonerLoadingStage.SecuringContact,
                    now,
                    alreadyPhysicallyCuffed
                        ? "PlayerClaimedPhysicallyHandcuffedPrisoner"
                        : "PlayerStartedHandcuffInteraction");
                _playerEscortLostAt.Remove(prisonerHandle);
                if (alreadyPhysicallyCuffed)
                {
                    MaintainPrisonerCustodyState(target);
                    LogRuntime("POLICE_CONVOY_PLAYER_PRECUFFED_ESCORT_CLAIMED",
                        "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                        + "; PhysicalCuffed=true; ArrestTaskReplayed=false");
                    Notify("~b~POLICE CUSTODY~s~\nThe suspect is already handcuffed. Walk with them to the marked rear door and press E.");
                    return "Suspect already secured. Walk with the suspect to the marked rear door and press E.";
                }

                _playerHandcuffAnimationUntil[prisonerHandle] =
                    now.AddMilliseconds(PlayerHandcuffAnimationMilliseconds);
                LogRuntime(
                    "POLICE_CONVOY_ESCORT_STARTED",
                    "Prisoner=" + prisonerHandle + "; Escort=" + player.Handle
                    + "; Owner=Player; Stage=SecuringContact");
                try
                {
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, target, true);
                    target.BlockPermanentEvents = true;
                    target.CanSwitchWeapons = false;
                    target.Task.ClearAll();
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        player, target, 1000);
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        target, player, 1000);
                    Function.Call(Hash.TASK_ARREST_PED, player, target);
                    int handcuffDuration = PlayerHandcuffAnimationMilliseconds;
                    _playerHandcuffAnimationUntil[prisonerHandle] =
                        now.AddMilliseconds(handcuffDuration);
                    LogRuntime("POLICE_CONVOY_PLAYER_HANDCUFF_ANIMATION_STARTED",
                        "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                        + "; Method=GroundedTaskArrestPed"
                        + "; SynchronizedScene=false"
                        + "; HoldMilliseconds=" + handcuffDuration);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PLAYER_ESCORT_START_FAILED", ex);
                }
                LogRuntime(
                    "POLICE_CONVOY_PLAYER_ESCORT_STARTED",
                    "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                    + "; Phase=" + _phase);
                LogRuntime(
                    "POLICE_CONVOY_PRISONER_HANDOFF_STARTED",
                    "Prisoner=" + prisonerHandle + "; Escort=" + player.Handle
                    + "; Vehicle=" + (_transport == null ? "none" : _transport.Handle.ToString())
                    + "; Method=Player");
                Notify("~b~POLICE CUSTODY~s~\nHandcuffing in progress. Keep the suspect close, then walk to the marked rear door.");
                return "You started the physical handcuff procedure. Wait for it to finish, then escort the prisoner to the marked rear door.";
            }

            VehicleSeat seat = PrisonerSeat(target, ValidPrisoners().ToList());
            if (seat == VehicleSeat.None)
                return "No rear custody seat is available for this prisoner.";
            Vector3 entry = TransportDoorPosition(seat);
            int doorIndex = PrisonerDoorIndex(seat);
            bool playerDoorTaskStarted = _playerDoorInteractions.Contains(prisonerHandle);
            bool doorAlreadyOpenedByPlayer = playerDoorTaskStarted
                && IsPrisonerDoorPhysicallyOpen(doorIndex);
            if (doorAlreadyOpenedByPlayer)
            {
                _prisonerVehicleDoorsOpened.Add(prisonerHandle);
                _prisonerDoorOpenTaskIssuedAt.Remove(prisonerHandle);
                if (!_prisonerEntryTasksIssued.Contains(prisonerHandle))
                    SetPrisonerLoadingStage(target, player,
                        PrisonerLoadingStage.DoorOpened, now,
                        "PlayerRearDoorPhysicallyOpenConfirmed");
                LogRuntime("POLICE_CONVOY_PLAYER_PRISONER_DOOR_OPEN_CONFIRMED",
                    "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                    + "; Vehicle=" + _transport.Handle + "; Door=" + doorIndex
                    + "; AngleRatioAtLeast=0.75");
            }
            if (playerDoorTaskStarted && !doorAlreadyOpenedByPlayer)
                return "The rear door is still opening. Wait until it is fully open, then press E again to load the prisoner.";

            float playerDoorDistance = player.Position.DistanceTo(entry);
            float prisonerDoorDistance = target.Position.DistanceTo(entry);
            bool playerOwnsGroundedEscort = _playerEscortPrisoners.Contains(prisonerHandle)
                && IsPlayerEscort(player);
            bool bothActorsAtRearDoor =
                playerDoorDistance <= PlayerCustodyVehicleRadius
                && prisonerDoorDistance <= PlayerCustodyVehicleRadius;
            bool stillAtOpenedCustodyVehicle = doorAlreadyOpenedByPlayer
                && _transport != null && _transport.Exists()
                && player.Position.DistanceTo(_transport.Position)
                    <= PlayerCustodyLoadInteractionRadius
                && target.Position.DistanceTo(_transport.Position)
                    <= PlayerCustodyLoadInteractionRadius
                && player.Position.DistanceTo(target.Position)
                    <= PlayerCustodyLoadInteractionRadius;
            LogRuntime("POLICE_CONVOY_PLAYER_CUSTODY_INPUT",
                "Phase=" + _phase + "; Prisoner=" + prisonerHandle
                + "; DoorOpened=" + doorAlreadyOpenedByPlayer
                + "; PlayerDoorDistance=" + playerDoorDistance.ToString("0.0")
                + "; PrisonerDoorDistance=" + prisonerDoorDistance.ToString("0.0")
                + "; PlayerEscortFollowing=" + playerOwnsGroundedEscort
                + "; PlayerVehicleDistance=" + (_transport == null || !_transport.Exists()
                    ? "unavailable" : player.Position.DistanceTo(_transport.Position).ToString("0.0")));
            if (!bothActorsAtRearDoor && !stillAtOpenedCustodyVehicle)
            {
                // The player owns the escort after choosing it with E. The
                // prisoner follows on foot and must reach the actual rear-door
                // area before the physical load interaction can continue.
                if (_playerEscortPrisoners.Contains(prisonerHandle))
                {
                    string instruction = doorAlreadyOpenedByPlayer
                        ? "The rear door is open. Stay beside the transport and prisoner, then press E to load."
                        : "Walk with the physically controlled prisoner to the rear door; press E to open it, then E again to load.";
                    Notify("~b~POLICE CUSTODY~s~\n" + instruction);
                    return instruction;
                }
                return "Continue escorting the prisoner until both of you are beside the marked rear door.";
            }

            // A transport officer can have opened the physical door before
            // the player claims custody. The player still needs a registered
            // door interaction so the following E is accepted as the load
            // command rather than being blocked by an NPC-owned door flag.
            if (!_playerDoorInteractions.Contains(prisonerHandle))
            {
                bool alreadyPhysicallyOpen = IsPrisonerDoorPhysicallyOpen(doorIndex);
                try
                {
                    SetPrisonerLoadingStage(target, player,
                        PrisonerLoadingStage.VehicleDoorReached, now,
                        "PlayerAndPrisonerAtRearPassengerDoor");
                    _playerDoorInteractions.Add(prisonerHandle);
                    if (alreadyPhysicallyOpen)
                    {
                        _prisonerVehicleDoorsOpened.Add(prisonerHandle);
                        SetPrisonerLoadingStage(target, player,
                            PrisonerLoadingStage.DoorOpened, now,
                            "PlayerConfirmedRearDoorAlreadyOpen");
                        LogRuntime("POLICE_CONVOY_PLAYER_PRISONER_DOOR_OPEN_CONFIRMED",
                            "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                            + "; Vehicle=" + _transport.Handle + "; Door=" + doorIndex
                            + "; OpenedBeforePlayerInput=true");
                    }
                    else
                    {
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            player, target, 1000);
                        Function.Call(Hash.TASK_OPEN_VEHICLE_DOOR,
                            player, _transport, 4000, doorIndex, 1.0f);
                        _prisonerDoorOpenTaskIssuedAt[prisonerHandle] = now;
                        LogRuntime(
                            "POLICE_CONVOY_PLAYER_PRISONER_DOOR_TASK_ISSUED",
                            "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                            + "; Vehicle=" + _transport.Handle + "; Door=" + doorIndex
                            + "; PhysicalOpenForced=false");
                    }
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PLAYER_PRISONER_DOOR_OPEN_FAILED", ex);
                    return "The transport rear door could not be opened safely.";
                }
                string doorMessage = alreadyPhysicallyOpen
                    ? "The rear door is already open. Press E again to load the prisoner."
                    : "The officer is opening the rear door. Wait until it is fully open, then press E again to load the prisoner.";
                Notify("~b~POLICE CUSTODY~s~\n" + doorMessage);
                return doorMessage;
            }

            if (_playerEntryInteractions.Contains(prisonerHandle))
                return "The officer is loading the handcuffed prisoner into the transport.";

            _playerEntryInteractions.Add(prisonerHandle);
            _playerLoadingContactStartedAt[prisonerHandle] = now;
            try
            {
                bool physicallyCuffed;
                if (!TryGetPhysicalCuffState(target, out physicallyCuffed)
                    || !physicallyCuffed)
                {
                    _playerEntryInteractions.Remove(prisonerHandle);
                    _playerLoadingContactStartedAt.Remove(prisonerHandle);
                    LogRuntime("POLICE_CONVOY_PLAYER_LOADING_REJECTED",
                        "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                        + "; Reason=PhysicalCuffNotConfirmed; ArrestTaskReplayed=false");
                    return "Loading stopped because the prisoner's physical handcuffs are not confirmed.";
                }

                // A short second contact task gives the player a visible
                // officer-led loading moment before GTA performs the final
                // foot-to-seat movement. Do not replay TASK_ARREST_PED here:
                // the suspect is already physically handcuffed.
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    player, target, 800);
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    target, player, 800);
                MaintainPrisonerCustodyState(target);
            }
            catch (Exception ex)
            {
                _playerEntryInteractions.Remove(prisonerHandle);
                _playerLoadingContactStartedAt.Remove(prisonerHandle);
                LogException("POLICE_CONVOY_PLAYER_PRISONER_LOADING_CONTACT_FAILED", ex);
                return "The physical prisoner loading interaction could not be started.";
            }
            LogRuntime(
                "POLICE_CONVOY_PLAYER_PRISONER_LOAD_REQUESTED",
                "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                + "; Vehicle=" + _transport.Handle + "; Seat=" + seat);
            LogRuntime(
                "POLICE_CONVOY_PLAYER_PRISONER_LOADING_STARTED",
                "Prisoner=" + prisonerHandle + "; Officer=" + player.Handle
                + "; Vehicle=" + _transport.Handle + "; Seat=" + seat);
            Notify("~b~POLICE CUSTODY~s~\nYou are physically loading the handcuffed prisoner. The officer will secure the rear door afterward.");
            return "Physical loading started. The handcuffed prisoner is being guided into the transport.";
        }

        private string ProcessPlayerPrisonerUnloadInput(DateTime now)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return "Player character unavailable for the physical prisoner handoff.";
            if (player.IsInVehicle())
                return "Exit the transport and stand at the marked rear door before pressing E.";
            if (_transport == null || !_transport.Exists())
                return "The custody transport is unavailable for the physical unload.";
            if (_phase == CustodyPhase.StationUnloading
                && (_stationHandoffMarkerPosition == Vector3.Zero
                    || !_stationHandoffActualPoseValidated
                    || _transport.Position.DistanceTo(_stationHandoffMarkerPosition)
                        > StationHandoffMaximumMarkerOffset
                    || Math.Abs(_transport.Speed) > StationHandoffStoppedSpeed))
                return "Stop in the marked Station receiving area and wait for rear-door and officer-route clearance before unloading.";

            List<Ped> prisoners = ValidPrisoners().ToList();
            List<Ped> loaded = prisoners.Where(IsInTransport)
                .OrderBy(prisoner => player.Position.DistanceTo(
                    TransportDoorPosition(PrisonerSeat(prisoner, prisoners))))
                .ToList();
            Ped target = loaded.FirstOrDefault(prisoner =>
            {
                Vector3 rearDoor = TransportDoorPosition(
                    PrisonerSeat(prisoner, prisoners));
                float interactionRadius = _playerUnloadingDoorInteractions.Contains(prisoner.Handle)
                    ? PlayerCustodyLoadInteractionRadius
                    : PlayerCustodyInteractionRadius;
                return player.Position.DistanceTo(rearDoor) <= interactionRadius
                    || (_playerUnloadingDoorInteractions.Contains(prisoner.Handle)
                        && _transport != null && _transport.Exists()
                        && player.Position.DistanceTo(_transport.Position)
                            <= PlayerCustodyLoadInteractionRadius);
            });
            if (target == null)
            {
                if (loaded.Count == 0 && prisoners.Any(prisoner => !IsInTransport(prisoner)))
                    return "The prisoner is already outside. The standby officer is completing the physical handoff.";
                LogRuntime("POLICE_CONVOY_PLAYER_UNLOAD_INPUT_REJECTED",
                    "Stage=" + _phase + "; Reason=NoLoadedPrisonerAtRearDoor; Player="
                    + player.Handle + "; Position=" + player.Position);
                return "Move beside the transport's marked rear door, then press E to open it.";
            }

            int prisonerHandle = target.Handle;
            VehicleSeat seat = PrisonerSeat(target, prisoners);
            if (seat == VehicleSeat.None)
                return "No rear custody seat is available for this prisoner.";
            if (_playerUnloadingFallbackHandles.Contains(prisonerHandle))
            {
                LogRuntime("POLICE_CONVOY_PLAYER_UNLOAD_INPUT_RETAINED_WITH_STANDBY",
                    "Stage=" + _phase + "; Prisoner=" + prisonerHandle
                    + "; PlayerControlRestarted=false; SameCustodyOperation=true");
                return "The standby officer is completing the physical unload. Custody remains active; let the officer finish the same handoff.";
            }

            if (!_playerUnloadingDoorInteractions.Contains(prisonerHandle))
            {
                Ped previousOfficer;
                if (_prisonerUnloadingOfficers.TryGetValue(prisonerHandle, out previousOfficer)
                    && previousOfficer != null && previousOfficer.Exists())
                {
                    try { previousOfficer.Task.ClearAll(); } catch { }
                    _lastEscortOfficerTaskAt.Remove(previousOfficer.Handle);
                }
                _prisonerUnloadingOfficers.Remove(prisonerHandle);
                _prisonerUnloadingEscortStartedAt.Remove(prisonerHandle);
                _lastPrisonerTaskAt.Remove(prisonerHandle);

                int doorIndex = PrisonerDoorIndex(seat);
                bool alreadyOpenedByOfficer = _prisonerUnloadingDoorsOpened.Contains(prisonerHandle);
                bool doorAlreadyPhysicallyOpen = IsPrisonerDoorPhysicallyOpen(doorIndex);
                try
                {
                    if (!doorAlreadyPhysicallyOpen)
                    {
                        // TASK_OPEN_VEHICLE_DOOR is an entry-oriented task:
                        // assigning it to the Player can seat them in the
                        // custody vehicle instead of opening the rear door.
                        // Animate the actual vehicle door without taking
                        // control of the Player or changing their position.
                        Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                            _transport, doorIndex, false, false);
                        _prisonerDoorOpenTaskIssuedAt[prisonerHandle] = now;
                    }
                    else
                    {
                        _prisonerUnloadingDoorsOpened.Add(prisonerHandle);
                    }
                    _playerUnloadingDoorInteractions.Add(prisonerHandle);
                    _playerUnloadingStartedAt[prisonerHandle] = now;
                    _phaseStartedAt = now;
                    MaintainPrisonerCustodyState(target);
                }
                catch (Exception ex)
                {
                    _playerUnloadingDoorInteractions.Remove(prisonerHandle);
                    _playerUnloadingStartedAt.Remove(prisonerHandle);
                    LogException("POLICE_CONVOY_PLAYER_PRISONER_UNLOAD_DOOR_FAILED", ex);
                    return "The transport rear door could not be opened safely.";
                }

                LogRuntime("POLICE_CONVOY_PLAYER_PRISONER_UNLOAD_DOOR_OPEN_REQUESTED",
                    "Stage=" + _phase + "; Prisoner=" + prisonerHandle
                    + "; Player=" + player.Handle + "; Vehicle=" + _transport.Handle
                    + "; Door=" + doorIndex
                    + "; PreviouslyOpenedByOfficer=" + alreadyOpenedByOfficer
                    + "; AlreadyPhysicallyOpen=" + doorAlreadyPhysicallyOpen
                    + "; DoorOpenRequest=SET_VEHICLE_DOOR_OPEN"
                    + "; OpenInstantly=false; PlayerTaskIssued=false"
                    + "; PhysicalAngleConfirmationRequired=true");
                string doorMessage = doorAlreadyPhysicallyOpen
                    ? "The rear door is open. Press E again to unload the handcuffed prisoner."
                    : "The rear door is opening. Wait until it is open, then press E again to unload the handcuffed prisoner.";
                Notify("~b~POLICE CUSTODY~s~\n" + doorMessage + " The standby officer will escort them to intake.");
                return doorMessage;
            }

            if (_playerUnloadingTasksIssued.Contains(prisonerHandle))
                return "The prisoner is exiting the transport; the standby officer will take over the receiving escort.";

            int unloadDoorIndex = PrisonerDoorIndex(seat);
            if (!IsPrisonerDoorPhysicallyOpen(unloadDoorIndex))
                return "The rear door is still opening. Wait until it is fully open, then press E again to unload the prisoner.";
            if (_prisonerUnloadingDoorsOpened.Add(prisonerHandle))
            {
                _prisonerDoorOpenTaskIssuedAt.Remove(prisonerHandle);
                LogRuntime("POLICE_CONVOY_PLAYER_PRISONER_UNLOAD_DOOR_OPEN_CONFIRMED",
                    "Stage=" + _phase + "; Prisoner=" + prisonerHandle
                    + "; Player=" + player.Handle + "; Vehicle=" + _transport.Handle
                    + "; Door=" + unloadDoorIndex + "; AngleRatioAtLeast=0.75");
            }

            try
            {
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    player, target, 800);
                // This is an explicit player command after the rear door has
                // been opened. GTA performs the actual exit; Convoy does not
                // warp the prisoner, and the receiving officer resumes escort
                // as soon as the prisoner is physically outside.
                _lastPrisonerTaskAt.Remove(prisonerHandle);
                CommandLeaveTransport(target, "PlayerPhysicalUnload");
                _playerUnloadingTasksIssued.Add(prisonerHandle);
                _playerUnloadingStartedAt[prisonerHandle] = now;
                _phaseStartedAt = now;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PLAYER_PRISONER_UNLOAD_REQUEST_FAILED", ex);
                return "The physical unload could not be started; the standby officer will take over.";
            }

            LogRuntime("POLICE_CONVOY_PLAYER_PRISONER_UNLOAD_REQUESTED",
                "Stage=" + _phase + "; Prisoner=" + prisonerHandle
                + "; Player=" + player.Handle + "; Vehicle=" + _transport.Handle
                + "; Door=" + PrisonerDoorIndex(seat));
            Notify("~b~POLICE CUSTODY~s~\nYou are unloading the prisoner. The standby officer will escort them to the receiving point.");
            return "Physical unload started. The standby officer will complete the station/prison handoff.";
        }

        private bool CanPlayerTakeCustody(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead || IsInTransport(prisoner))
                return false;
            Ped player = Game.Player.Character;
            return player != null && player.Exists();
        }

        private string PlayerLoadingStatusText()
        {
            int prisonerHandle = _playerEscortPrisoners.FirstOrDefault();
            if (prisonerHandle != 0 && _playerEntryInteractions.Contains(prisonerHandle))
                return "physical prisoner loading is underway.";
            if (prisonerHandle != 0 && _playerDoorInteractions.Contains(prisonerHandle))
                return "rear door is open. E = load the handcuffed prisoner.";
            PrisonerLoadingStage stage;
            if (prisonerHandle != 0
                && _prisonerLoadingStages.TryGetValue(prisonerHandle, out stage)
                && stage == PrisonerLoadingStage.VehicleDoorReached)
                return "rear door reached. E = open the rear door, then E again = load.";
            return "walk with the handcuffed prisoner to the rear door. E = open, then E = load.";
        }

        internal string Update(DateTime now)
        {
            return Process(now);
        }

        /// <summary>
        /// Returns all living held prisoners after a recoverable physical
        /// transport failure. Dispatch can show them again as handcuffed rather
        /// than losing the original arrest to a missing vehicle.
        /// </summary>
        internal IList<Ped> ConsumeFailedPrisoners()
        {
            IList<LSPDSuspectInjuryState> ignored;
            return ConsumeFailedPrisoners(out ignored);
        }

        internal IList<Ped> ConsumeFailedPrisoners(
            out IList<LSPDSuspectInjuryState> injuryStates)
        {
            injuryStates = new List<LSPDSuspectInjuryState>();
            if (!Failed || !_failedIsRecoverable)
                return new List<Ped>();
            List<Ped> result = ValidPrisoners().ToList();
            HashSet<int> dispatchCustodyHandles = _dispatchCustodyOperation
                && !_isRequestedConvoyActivity && !_playerOwnedTransport
                && !_failedAfterStationHandoff
                    ? new HashSet<int>(result.Select(prisoner => prisoner.Handle))
                    : null;
            foreach (Ped prisoner in result)
            {
                ObserveConvoyInjuryState(prisoner, DateTime.UtcNow, true);
                LSPDSuspectInjuryState state;
                if (_prisonerInjuryStates.TryGetValue(prisoner.Handle, out state)
                    && state != null)
                    injuryStates.Add(state);
            }
            ClearDeferredCleanupBeforeRetry();
            _prisoners.Clear();
            ClearTerminalState(dispatchCustodyHandles);
            return result;
        }

        internal void CompleteDispatchCustodyRecovery(
            IEnumerable<Ped> prisoners,
            bool dispatchReclaimed)
        {
            if (prisoners == null)
                return;
            foreach (Ped prisoner in prisoners.Where(value => value != null && value.Exists()))
            {
                RestoreDispatchCustodyMovementStyle(prisoner);
                RestoreCustodyInvincibility(prisoner);
                LogRuntime("POLICE_CONVOY_DISPATCH_CUSTODY_MOVEMENT_RELEASED",
                    "Prisoner=" + prisoner.Handle
                    + "; NextOwner=" + (dispatchReclaimed ? "Dispatch" : "TerminalCleanup")
                    + "; DispatchReclaimed=" + dispatchReclaimed);
            }
        }

        internal Ped ConsumeFailedPrisoner()
        {
            return ConsumeFailedPrisoners().FirstOrDefault();
        }

        internal void ConsumeCompletedState()
        {
            if (Completed)
                ClearTerminalState();
        }

        internal void PrepareForPatrolEnd()
        {
            if (Active)
            {
                LogRuntime("POLICE_CONVOY_PATROL_END_DEFERRED",
                    "Reason=ActiveCustodyOperationStillOwnsPhysicalActors; Phase=" + _phase);
                return;
            }

            bool hasOwnedActors = _prisoners.Any(ped => ped != null && ped.Exists())
                || (_transport != null && _transport.Exists() && !_playerOwnedTransport)
                || (_transportDriver != null && _transportDriver.Exists())
                || _sceneEscortOfficers.Any(ped => ped != null && ped.Exists())
                || _stationHandoffOfficers.Any(ped => ped != null && ped.Exists())
                || _prisonHandoffOfficers.Any(ped => ped != null && ped.Exists())
                || _routeThreatPeds.Any(ped => ped != null && ped.Exists())
                || (_routeThreatVehicle != null && _routeThreatVehicle.Exists());
            if (hasOwnedActors)
                QueueOperationCleanup();

            if (_deferredCleanup.Count == 0)
            {
                Reset();
                return;
            }

            string operationId = _operationId;
            RestoreAllConvoyInjuryStates("PatrolEndedWithDeparturePending");
            ClearTerminalState();
            _prisoners.Clear();
            LogRuntime("POLICE_CONVOY_PATROL_END_DEPARTURE_RETAINED",
                "Operation=" + operationId
                + "; PendingEntities=" + _deferredCleanup.Count
                + "; Cleanup=SafeDistanceOrHardTimeout"
                + "; HardTimeoutSeconds=" + Math.Max(1, DeferredCleanupMaximumSeconds)
                + "; PlayerOwnedTransportDeleted=false");
        }

        internal void Reset()
        {
            CloseActiveAudio();
            ReleasePendingModelRequests();
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
                DeleteDeferred(item);
            _deferredCleanup.Clear();
            RestoreAllConvoyInjuryStates("ConvoyReset");
            RestoreDispatchCustodyMovementStyles();
            CleanupImmediate();
            CleanupWaypoint();
            _prisoners.Clear();
            _sceneEscortOfficers.Clear();
            _stationHandoffOfficers.Clear();
            _prisonHandoffOfficers.Clear();
            _routeThreatPeds.Clear();
            ResetOperationFields();
            _state = LSPDDispatchState.None;
            _phase = CustodyPhase.None;
        }

        internal LSPDAudioResult ReportConfirmed(
            string domain,
            string stage,
            string scopeId,
            string occurrenceId)
        {
            string safeDomain = string.IsNullOrWhiteSpace(domain) ? "transport" : domain.Trim();
            string safeStage = string.IsNullOrWhiteSpace(stage) ? "requested" : stage.Trim();
            string eventId = "lsimmersivelife.police." + safeDomain + "." + safeStage;
            if (string.Equals(safeStage, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _audio.CancelScope(scopeId);
                return _audio.Report(eventId, scopeId + "-terminal", occurrenceId, safeDomain);
            }
            return _audio.Report(eventId, scopeId, occurrenceId, safeDomain);
        }

        private void BeginTransportPreparation(CustodyPhase phase, string vehicleModelName)
        {
            _pendingVehicleModelName = vehicleModelName;
            _pendingOfficerModelName = OfficerModelName();
            _transportNearCustodyFallback = false;
            _preparationDeadline = DateTime.UtcNow.AddSeconds(ModelPreparationTimeoutSeconds);
            SetPhase(phase);
            RequestPendingModels();
            Notify("~b~POLICE CUSTODY~s~\nTransport unit is staging away from the scene.");
        }

        private string ProcessRequestedPrisonerPreparation(DateTime now)
        {
            Model prisonerModel = new Model(_pendingPrisonerModelName);
            bool releaseModel = false;
            try
            {
                if (!IsUsableModel(prisonerModel, false))
                {
                    releaseModel = true;
                    return Fail("The requested Convoy criminal profile has an unavailable ped model.", false);
                }
                if (!prisonerModel.IsLoaded)
                {
                    prisonerModel.Request();
                    if (now >= _preparationDeadline)
                    {
                        releaseModel = true;
                        return Fail("The requested Convoy prisoner did not finish loading safely.", false);
                    }
                    return string.Empty;
                }

                releaseModel = true;

                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return Fail("The player character is unavailable for Convoy staging.", false);
                Vector3 spawn = FindRequestedPrisonerPosition(player);
                Ped prisoner = World.CreatePed(prisonerModel, spawn);
                if (prisoner == null || !prisoner.Exists())
                    return Fail("The requested Convoy prisoner could not be staged on a safe street position.", false);

                // An independent Convoy prisoner is created as secured
                // custody. The receiving officer owns the later physical
                // escort; the prisoner must not regain ordinary pedestrian
                // autonomy while the unit is being staged.
                PreparePrisoner(prisoner, false);
                _prisoners.Add(prisoner);
                LogRuntime(
                    "POLICE_CONVOY_REQUEST_PRISONER_STAGED",
                    "Prisoner=" + prisoner.Handle + "; Profile=" + _requestedPrisonerProfileId
                    + "; Spawn=" + spawn);
                BeginTransportPreparation(CustodyPhase.PreparingPrisonTransport, VehicleName("fbi2", "policet"));
                return "Secured Convoy prisoner staged at the station. Prison transport unit is en route.";
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_REQUEST_PRISONER_STAGE_FAILED", ex);
                return Fail("The requested Convoy prisoner could not be staged safely.", false);
            }
            finally
            {
                // Keep an asynchronous request alive until it is consumed or
                // the operation reaches a terminal timeout.
                if (releaseModel)
                    ReleaseModel(prisonerModel);
            }
        }

        private void RequestRequestedPrisonerModel()
        {
            try
            {
                Model prisoner = new Model(_pendingPrisonerModelName);
                if (IsUsableModel(prisoner, false) && !prisoner.IsLoaded)
                    prisoner.Request();
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_REQUEST_PRISONER_MODEL_FAILED", ex);
            }
        }

        private Vector3 FindRequestedPrisonerPosition(Ped player)
        {
            Vector3 candidate = _stationDestination + new Vector3(7f, -5f, 0f);
            if (candidate.DistanceTo(player.Position) < 28f)
                candidate = player.GetOffsetPosition(new Vector3(
                    4f,
                    -Math.Max(35f, _settings.MinimumStagingDistance * 0.60f),
                    0f));
            try
            {
                Vector3 street = World.GetNextPositionOnStreet(candidate);
                if (street.DistanceTo(player.Position) >= 22f)
                    return street;
            }
            catch { }
            return candidate;
        }

        private string ProcessTransportPreparation(DateTime now)
        {
            Model vehicleModel = new Model(_pendingVehicleModelName);
            Model officerModel = new Model(_pendingOfficerModelName);
            bool releaseModels = false;
            try
            {
                if (!IsUsableModel(vehicleModel, true) || !IsUsableModel(officerModel, false))
                {
                    releaseModels = true;
                    return Fail("The configured Police transport model is unavailable.", true);
                }
                if (!vehicleModel.IsLoaded || !officerModel.IsLoaded)
                {
                    RequestPendingModels();
                    if (now >= _preparationDeadline)
                    {
                        releaseModels = true;
                        return Fail("Police transport did not finish loading before the custody timeout.", true);
                    }
                    return string.Empty;
                }

                releaseModels = true;
                bool prisonTrip = _phase == CustodyPhase.PreparingPrisonTransport;
                if (_dispatchGroupPlayerDrivenPickup)
                {
                    int securedCount = Math.Max(1,
                        _dispatchGroupExpectedPrisonerCount);
                    int requiredSeats = securedCount + 1;
                    int configuredSeats = DispatchGroupModelSeatCount(vehicleModel);
                    if (configuredSeats < requiredSeats)
                    {
                        Model fallbackModel = new Model("policet3");
                        int fallbackSeats = IsUsableModel(fallbackModel, true)
                            ? DispatchGroupModelSeatCount(fallbackModel) : 0;
                        if (fallbackSeats < requiredSeats)
                        {
                            releaseModels = true;
                            LogRuntime(
                                "POLICE_CONVOY_DISPATCH_GROUP_FALLBACK_UNAVAILABLE",
                                "Operation=" + _operationId
                                + "; ConfiguredModel=" + _pendingVehicleModelName
                                + "; ConfiguredSeats=" + configuredSeats
                                + "; CandidateModel=policet3"
                                + "; CandidateValid=" + IsUsableModel(fallbackModel, true)
                                + "; CandidateSeats=" + fallbackSeats
                                + "; RequiredSeats=" + requiredSeats
                                + "; CustodyOwnershipTransferred=false");
                            return Fail("The secured suspect group needs " + requiredSeats
                                + " seats, but the configured transport and the verified policet3 fallback do not provide them. Dispatch custody remains available for another transport choice.", true);
                        }

                        string configuredModelName = _pendingVehicleModelName;
                        _pendingVehicleModelName = "policet3";
                        vehicleModel = fallbackModel;
                        LogRuntime(
                            "POLICE_CONVOY_DISPATCH_GROUP_MODEL_FALLBACK",
                            "Operation=" + _operationId
                            + "; From=" + configuredModelName
                            + "; To=policet3"
                            + "; ConfiguredSeats=" + configuredSeats
                            + "; ConfirmedSeats=" + fallbackSeats
                            + "; RequiredSeats=" + requiredSeats
                            + "; ModelValid=" + IsUsableModel(fallbackModel, true)
                                + "; SeatQuery=GET_VEHICLE_MODEL_NUMBER_OF_SEATS"
                                + "; InteriorSeatLimit=4; ExteriorSeatsExcluded=true");
                        if (!vehicleModel.IsLoaded)
                        {
                            vehicleModel.Request();
                            RequestPendingModels();
                            return string.Empty;
                        }
                    }
                }
                if (!CreateStagedTransport(vehicleModel, officerModel, prisonTrip))
                    return Fail("Police transport could not be staged on a safe road position.", true);
                if (_dispatchGroupPlayerDrivenPickup
                    && !HasDispatchGroupSeatCapacity(_dispatchGroupExpectedPrisonerCount))
                {
                    int seats = DispatchGroupTransportSeatCount();
                    LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_SEAT_CAPACITY_REJECTED",
                        "Operation=" + _operationId
                        + "; Vehicle=" + (_transport == null ? "none" : _transport.Handle.ToString())
                        + "; Model=" + (_transport == null ? "none" : _transport.Model.Hash.ToString())
                        + "; Seats=" + seats
                        + "; InteriorSeatsOnly=true; Driver=Player; TransportOfficerPassenger=NotRequired"
                        + "; LivingSuspectsRequired=" + _dispatchGroupExpectedPrisonerCount
                        + "; CustodyOwnershipTransferred=false");
                    return Fail("The configured transport has too few confirmed inside seats for the living suspect group. Dispatch and Backup custody remain active.", true);
                }
                SetPhase(prisonTrip
                    ? CustodyPhase.PrisonTransportEnRoute
                    : CustodyPhase.SceneTransportEnRoute);
                return prisonTrip
                    ? "Prison convoy unit is en route to the station handoff."
                    : "Police transport unit is en route to the arrest scene.";
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_TRANSPORT_PREPARATION_FAILED", ex);
                return Fail("Police transport preparation failed safely.", true);
            }
            finally
            {
                // Do not cancel a live asynchronous request. Release only
                // after the models were consumed or preparation terminated.
                if (releaseModels)
                {
                    ReleaseModel(vehicleModel);
                    ReleaseModel(officerModel);
                }
            }
        }

        private bool HasDispatchGroupSeatCapacity(int livingSuspectCount)
        {
            // The transport driver delivers the van, then exits. The Player
            // drives after the pickup, so only interior vehicle positions for
            // the Player plus every suspect are reserved. The two exterior
            // side-hanging positions reported by policet3 are never custody seats.
            int required = Math.Max(1, livingSuspectCount) + 1;
            return DispatchGroupTransportSeatCount() >= required;
        }

        private int DispatchGroupTransportSeatCount()
        {
            if (_transport == null || !_transport.Exists())
                return 0;
            try
            {
                return DispatchGroupInteriorSeatCount(_transport.Model);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_GROUP_SEAT_QUERY_FAILED", ex);
                return 0;
            }
        }

        private int DispatchGroupModelSeatCount(Model model)
        {
            if (!IsUsableModel(model, true))
                return 0;
            try
            {
                return DispatchGroupInteriorSeatCount(model);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_GROUP_MODEL_SEAT_QUERY_FAILED", ex);
                return 0;
            }
        }

        private int DispatchGroupInteriorSeatCount(Model model)
        {
            if (!IsUsableModel(model, true))
                return 0;
            try
            {
                int reported = Function.Call<int>(
                    Hash.GET_VEHICLE_MODEL_NUMBER_OF_SEATS, model.Hash);
                // Scattered collection uses only the four mapped cabin
                // positions: driver, front passenger, and two rear seats.
                // Any additional metadata seats are intentionally excluded;
                // in policet3 those are the exterior side-hanging positions.
                return Math.Max(0, Math.Min(reported, 4));
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_GROUP_INTERIOR_SEAT_QUERY_FAILED", ex);
                return 0;
            }
        }

        private void RequestPendingModels()
        {
            try
            {
                Model vehicle = new Model(_pendingVehicleModelName);
                Model officer = new Model(_pendingOfficerModelName);
                if (IsUsableModel(vehicle, true) && !vehicle.IsLoaded) vehicle.Request();
                if (IsUsableModel(officer, false) && !officer.IsLoaded) officer.Request();
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_MODEL_REQUEST_FAILED", ex);
            }
        }

        private bool CreateStagedTransport(Model vehicleModel, Model officerModel, bool prisonTrip)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return false;
            Vector3 target = TransportTarget(prisonTrip);

            // A recovery tick can reach preparation again while the original
            // transport is still valid. Keep that vehicle and its driver as
            // the single owner of this assignment instead of creating another
            // van beside it.
            if (HasUsableTransport())
            {
                LogRuntime(
                    "POLICE_CONVOY_TRANSPORT_REUSED",
                    "Vehicle=" + _transport.Handle + "; Driver=" + _transportDriver.Handle
                    + "; Target=" + target + "; PrisonTrip=" + prisonTrip);
                DriveTransportTo(target, true);
                return true;
            }

            bool dispatchCustodyTransport = IsDispatchCustodyTransport();
            Vector3 spawn;
            Vector3 routeRoadTarget = Vector3.Zero;
            float spawnHeading = HeadingToward(
                player.Position, target, player.Heading);
            float routeDistance = -1f;
            int stagingCandidateIndex = -1;
            HashSet<int> rejectedGroundCandidates = new HashSet<int>();
            int groundedPlacementRejections = 0;
            string lastGroundPlacementFailure = string.Empty;
            Vehicle vehicle = null;
            int maximumPlacementAttempts = dispatchCustodyTransport
                ? MaximumDispatchTransportStagingCandidates : 1;
            for (int attempt = 0; attempt < maximumPlacementAttempts; attempt++)
            {
                if (dispatchCustodyTransport)
                {
                    string stagingFailure;
                    if (!TryFindDispatchTransportStagingPosition(
                        player, target, prisonTrip, out spawn, out spawnHeading,
                        out routeRoadTarget, out routeDistance,
                        out stagingCandidateIndex, out stagingFailure,
                        rejectedGroundCandidates))
                    {
                        LogRuntime("POLICE_CONVOY_TRANSPORT_STAGING_REJECTED",
                            "Operation=" + _operationId
                            + "; PrisonTrip=" + prisonTrip
                            + "; CandidateLimit=" + MaximumDispatchTransportStagingCandidates
                            + "; GroundPlacementRejected=" + groundedPlacementRejections
                            + "; LastGroundFailure=" + (lastGroundPlacementFailure.Length == 0
                                ? "none" : lastGroundPlacementFailure)
                            + "; Reason=" + stagingFailure
                            + "; Player=" + player.Position
                            + "; Target=" + target
                            + "; VanCreated=false");
                        return false;
                    }
                }
                else
                    spawn = FindSafeStagingPosition(player, target, prisonTrip);

                vehicle = World.CreateVehicle(vehicleModel, spawn, spawnHeading);
                if (vehicle == null || !vehicle.Exists())
                    return false;

                vehicle.IsPersistent = true;
                try
                {
                    // The transport remains Convoy-owned while the player is
                    // driving the prisoner away from the scene. IsPersistent alone
                    // does not always prevent Enhanced from streaming a distant
                    // vehicle out of the operation.
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, vehicle, true, true);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_TRANSPORT_MISSION_OWNERSHIP_FAILED", ex);
                }
                vehicle.PlaceOnGround();
                if (!dispatchCustodyTransport)
                    break;

                vehicle.Heading = spawnHeading;
                string placementFailure;
                if (IsDispatchTransportSpawnPlacementValid(
                    vehicle, spawn, routeRoadTarget, spawnHeading,
                    player, out placementFailure))
                {
                    if (groundedPlacementRejections > 0)
                        LogRuntime("POLICE_CONVOY_TRANSPORT_STAGING_ALTERNATE_SELECTED",
                            "Operation=" + _operationId
                            + "; Candidate=" + stagingCandidateIndex
                            + "; EarlierGroundCandidatesRejected=" + groundedPlacementRejections
                            + "; LastGroundFailure=" + lastGroundPlacementFailure
                            + "; Vehicle=" + vehicle.Handle);
                    break;
                }

                groundedPlacementRejections++;
                lastGroundPlacementFailure = placementFailure;
                rejectedGroundCandidates.Add(stagingCandidateIndex);
                try { vehicle.Delete(); } catch { }
                vehicle = null;
            }

            if (vehicle == null || !vehicle.Exists())
            {
                LogRuntime("POLICE_CONVOY_TRANSPORT_GROUND_CANDIDATES_EXHAUSTED",
                    "Operation=" + _operationId
                    + "; CandidateLimit=" + MaximumDispatchTransportStagingCandidates
                    + "; RejectedCandidates=" + string.Join(",",
                        rejectedGroundCandidates.OrderBy(index => index)
                            .Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray())
                    + "; LastReason=" + (lastGroundPlacementFailure.Length == 0
                        ? "NoVehicleCreated" : lastGroundPlacementFailure)
                    + "; RetryBound=" + maximumPlacementAttempts);
                return false;
            }
            NormalizeTransportRearDoors(vehicle);
            SetEmergencySignals(vehicle, true);
            Ped driver = vehicle.CreatePedOnSeat(VehicleSeat.Driver, officerModel);
            if (driver == null || !driver.Exists())
            {
                vehicle.Delete();
                return false;
            }
            PrepareOfficer(driver);
            _transport = vehicle;
            _transportDriver = driver;
            _sceneEscortOfficers.Add(driver);
            try
            {
                // Keep the transport driver available for the player's driving
                // handoff, but give the unit a second officer whose only job is
                // the physical prisoner approach and loading sequence. If a
                // vehicle has no usable front passenger seat, the released
                // driver remains the bounded escort fallback below.
                Ped escort = vehicle.CreatePedOnSeat(VehicleSeat.RightFront, officerModel);
                if (escort != null && escort.Exists())
                {
                    PrepareOfficer(escort);
                    _sceneEscortOfficers.Add(escort);
                    LogRuntime("POLICE_CONVOY_TRANSPORT_ESCORT_STAGED",
                        "Officer=" + escort.Handle + "; Vehicle=" + vehicle.Handle);
                }
                else
                    LogRuntime("POLICE_CONVOY_TRANSPORT_ESCORT_FALLBACK",
                        "Front passenger officer could not be created; released driver will handle custody escort.");
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_TRANSPORT_ESCORT_CREATE_FAILED", ex);
            }
            _lastDriverTaskAt = DateTime.MinValue;
            _lastTransportProgressPosition = vehicle.Position;
            _lastTransportProgressAt = DateTime.UtcNow;
            _lastTransportRoadTaskTarget = Vector3.Zero;
            _nextTransportDriveReadinessLogAt = DateTime.MinValue;
            _nextTransportDriverSeatTaskAt = DateTime.MinValue;
            _transportRouteRecoveryCount = 0;
            _dispatchTransportRouteRoadTarget = dispatchCustodyTransport
                ? routeRoadTarget : Vector3.Zero;
            _dispatchTransportRouteDestination = dispatchCustodyTransport
                ? target : Vector3.Zero;
            _lastDispatchTransportRouteTargetFailure = string.Empty;
            _driverReleaseRequested = false;
            _driverReleaseRequestedAt = DateTime.MinValue;
            _transportOfficerReleaseRequested = false;
            _transportOfficerReleaseRequestedAt = DateTime.MinValue;
            _transportOfficerReleaseRecoveryLogged = false;
            MaintainActiveCustody(DateTime.UtcNow, true);
            DriveTransportTo(target, true);
            LogRuntime("POLICE_CONVOY_TRANSPORT_STAGED",
                "Vehicle=" + vehicle.Handle + "; Driver=" + driver.Handle
                + "; Spawn=" + vehicle.Position + "; Target=" + target
                + "; PrisonTrip=" + prisonTrip
                + (dispatchCustodyTransport
                    ? "; Candidate=" + stagingCandidateIndex
                        + "; LaneHeading=" + spawnHeading.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; TargetBearing=" + HeadingToward(
                            vehicle.Position, routeRoadTarget, spawnHeading).ToString("0.0", CultureInfo.InvariantCulture)
                        + "; RouteDistance=" + routeDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; ValidatedRoadTarget=" + routeRoadTarget
                        + "; PlayerSeparation=" + vehicle.Position.DistanceTo(player.Position).ToString("0.0", CultureInfo.InvariantCulture)
                        + "; TargetSeparation=" + vehicle.Position.DistanceTo(target).ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Street=" + GetDispatchTransportStreetName(vehicle.Position)
                        + "; RoadValidated=true; SpawnRevalidated=true"
                    : string.Empty)
                + "; TransportOfficerCount=" + _sceneEscortOfficers.Count
                + "; RequiredSceneEscorts=" + Math.Min(2, ValidPrisoners().Count()));
            return true;
        }

        private bool IsNearbyTransportUsableForCustody(Vector3 target, bool prisonTrip)
        {
            if (!HasUsableTransport())
                return false;

            float verticalDifference = Math.Abs(_transport.Position.Z - target.Z);
            if (verticalDifference > MaximumRoadElevationDifference)
                return false;

            if (!prisonTrip && _dispatchCustodyOperation
                && !_isRequestedConvoyActivity && !_playerOwnedTransport
                && _transport.Position.DistanceTo(_pickupTarget)
                    > MaximumDispatchSceneVehicleDistance)
                return false;

            int configuredDistance = _settings == null
                ? 70 : Math.Max(35, _settings.MinimumStagingDistance);
            float allowedDistance = Math.Min(
                MaximumNearbyTransportCustodyRadius,
                Math.Max(MinimumNearbyTransportCustodyRadius, configuredDistance * 1.25f));
            return _transport.Position.DistanceTo(target) <= allowedDistance;
        }

        private string ProcessTransportArrival(DateTime now, bool prisonTrip)
        {
            if (!HasUsableTransport())
            {
                string reason = DescribeTransportLoss();
                LogRuntime("POLICE_CONVOY_TRANSPORT_UNAVAILABLE",
                    "Phase=" + _phase + "; Reason=" + reason
                    + "; Vehicle=" + (_transport == null ? "null" : _transport.Handle.ToString())
                    + "; Driver=" + (_transportDriver == null ? "null" : _transportDriver.Handle.ToString()));
                return Fail("The staged Police transport is no longer available (" + reason + ").", true);
            }
            Vector3 target = TransportTarget(prisonTrip);
            float arrivalRadius = IsDispatchSceneRoadPickup(prisonTrip)
                ? DispatchSceneVehicleArrivalRadius : ArrivalRadius;
            if (_transport.Position.DistanceTo(target) > arrivalRadius)
            {
                if (TryRecoverStalledTransport(target, now))
                    return string.Empty;
                if (_transportRouteRecoveryCount >= MaximumTransportRouteRecoveries
                    && now >= _lastTransportProgressAt.AddSeconds(TransportRouteStallTimeoutSeconds))
                {
                    // Keep the existing transport assignment alive when the
                    // vehicle is already on the correct road level and close
                    // enough for the custody officer to walk in. Failing here
                    // used to queue the live van for cleanup, return custody to
                    // Dispatch, and make the next request spawn a duplicate
                    // transport even though the first van was still usable.
                    if (IsNearbyTransportUsableForCustody(target, prisonTrip))
                    {
                        if (!_transportNearCustodyFallback)
                        {
                            _transportNearCustodyFallback = true;
                            LogRuntime(
                                "POLICE_CONVOY_TRANSPORT_NEAR_CUSTODY_REUSED",
                                "Vehicle=" + _transport.Handle + "; Driver=" + _transportDriver.Handle
                                + "; Distance=" + _transport.Position.DistanceTo(target).ToString("0.0")
                                + "; Target=" + target + "; Recoveries=" + _transportRouteRecoveryCount
                                + "; Mode=PhysicalOfficerWalkIn");
                        }
                        return BeginTransportLoading(now, prisonTrip);
                    }

                    LogStateFailure(
                        "POLICE_CONVOY_TRANSPORT_ROUTE_STALLED",
                        "Target=" + target + "; Recoveries=" + _transportRouteRecoveryCount
                        + "; VehicleDistance=" + _transport.Position.DistanceTo(target).ToString("0.0"));
                    return Fail(
                        "The Police transport route remained blocked after bounded recovery attempts.",
                        true);
                }
                DriveTransportTo(target, false);
                return string.Empty;
            }

            return BeginTransportLoading(now, prisonTrip);
        }

        private string BeginTransportLoading(DateTime now, bool prisonTrip)
        {
            // The driver exits before prisoners start loading. This creates a
            // visible officer at the scene and keeps the driver's seat free for
            // the player after the real entry tasks finish. This helper is also
            // used by the bounded nearby-transport recovery above, so that path
            // reuses the exact same physical loading state rather than creating
            // a second transport request.
            if (IsDispatchSceneRoadPickup(false)
                && _transport != null && _transport.Exists())
            {
                _dispatchSceneWalkInDistanceMeters = Math.Min(
                    MaximumDispatchSceneVehicleDistance,
                    _transport.Position.DistanceTo(_pickupTarget));
            }
            SetEmergencySignals(_transport, false);
            if (!ReleaseDriverForPlayer(now))
                return string.Empty;
            if (IsDispatchSceneRoadPickup(false)
                && _transport != null && _transport.Exists())
                LogRuntime(
                    "POLICE_CONVOY_DISPATCH_ROADSIDE_TRANSPORT_READY",
                    "ScenePickup=" + _pickupTarget
                    + "; RoadTarget=" + _dispatchSceneVehicleAccessTarget
                    + "; Vehicle=" + _transport.Handle
                    + "; WalkInDistance="
                    + _dispatchSceneWalkInDistanceMeters.ToString("0.0")
                    + "; OfficerWalkInAndPhysicalEscort=true");
            // The driver reference is intentionally cleared once the driver
            // has left. Move into the loading state before waiting for the
            // passenger escort; otherwise the next Script tick re-enters the
            // arrival method and incorrectly treats Driver=null as a lost
            // transport.
            SetPhase(prisonTrip ? CustodyPhase.PrisonLoading : CustodyPhase.SceneLoading);
            foreach (Ped prisoner in CurrentDispatchPickupPrisoners(
                ValidPrisoners().ToList()).Where(ped => !IsInTransport(ped)))
            {
                int prisonerHandle = prisoner.Handle;
                Ped previousEscort;
                if (_prisonerEscortOfficers.TryGetValue(prisonerHandle, out previousEscort))
                    DetachPrisonerFromEscort(prisoner, previousEscort,
                        "NewTransportLoadingLeg");
                _prisonerEscortOfficers.Remove(prisonerHandle);
                _prisonerEscortStartedAt.Remove(prisonerHandle);
                _lastPrisonerEscortPosition.Remove(prisonerHandle);
                _lastPrisonerEscortProgressAt.Remove(prisonerHandle);
                _lastPrisonerEscortRecoveryAt.Remove(prisonerHandle);
                _prisonerEscortAlignmentStarted.Remove(prisonerHandle);
                _prisonerEscortApproachLogged.Remove(prisonerHandle);
                _prisonerVehicleDoorsOpened.Remove(prisonerHandle);
                _prisonerDoorCloseTasksIssued.Remove(prisonerHandle);
                _prisonerDoorOpenTaskIssuedAt.Remove(prisonerHandle);
                _dispatchPrisonerMissingSince.Remove(prisonerHandle);
                _dispatchPrisonerLastHandleRetryAt.Remove(prisonerHandle);
                _prisonerEntryTasksIssued.Remove(prisonerHandle);
                _lastPrisonerVehicleEntryAt.Remove(prisonerHandle);
                _playerEscortPrisoners.Remove(prisonerHandle);
                _playerEscortFallbacks.Remove(prisonerHandle);
                _playerDoorInteractions.Remove(prisonerHandle);
                _playerEntryInteractions.Remove(prisonerHandle);
                _playerHandcuffAnimationUntil.Remove(prisonerHandle);
                _playerLoadingContactStartedAt.Remove(prisonerHandle);
                _playerEscortLostAt.Remove(prisonerHandle);
                _prisonerLoadedLogIssued.Remove(prisonerHandle);
                SetPrisonerLoadingStage(prisoner, null,
                    PrisonerLoadingStage.EscortApproach, now,
                    "TransportArrivedForNewPhysicalLoadingLeg");
            }
            if (!ReleaseTransportOfficersForLoading(now))
                return string.Empty;
            OrderSceneEscortGuard();
            if (_transportNearCustodyFallback)
            {
                Notify(prisonTrip
                    ? "~b~PRISON CONVOY~s~\nThe nearby unit is secured. Officers are walking in for the station handoff."
                    : "~b~POLICE CUSTODY~s~\nThe nearby transport is secured. The custody officer is walking in.");
                return prisonTrip
                    ? "Prison convoy unit is nearby. Officers are walking to the station custody point."
                    : "Police transport is nearby. The custody officer is walking to the arrest scene.";
            }
            Notify(prisonTrip
                ? "~b~PRISON CONVOY~s~\nOfficers are receiving prisoners at the station."
                : "~b~POLICE CUSTODY~s~\nTransport arrived. Prisoner escort and loading has begun.");
            return prisonTrip
                ? "Prison convoy arrived at the station. Prisoner transfer is in progress."
                : "Police transport arrived at the scene. Prisoner loading is in progress.";
        }

        private List<Ped> CurrentDispatchPickupPrisoners(IList<Ped> prisoners)
        {
            List<Ped> valid = prisoners == null
                ? new List<Ped>()
                : prisoners.Where(ped => ped != null && ped.Exists()).ToList();
            if (!_dispatchCustodyOperation || _dispatchPickupStops.Count == 0
                || _activeDispatchPickupStopIndex < 0
                || _activeDispatchPickupStopIndex >= _dispatchPickupStops.Count)
                return valid;

            HashSet<int> handles = _dispatchPickupStops[
                _activeDispatchPickupStopIndex].PrisonerHandles;
            return valid.Where(ped => handles.Contains(ped.Handle)).ToList();
        }

        private bool HasNextDispatchPickupStop()
        {
            return _dispatchCustodyOperation
                && _dispatchPickupStops.Count > 1
                && _activeDispatchPickupStopIndex >= 0
                && _activeDispatchPickupStopIndex + 1 < _dispatchPickupStops.Count;
        }

        private string BeginNextDispatchPickupStop(
            DateTime now,
            IList<Ped> completedStopPrisoners)
        {
            if (!HasNextDispatchPickupStop()
                || completedStopPrisoners == null
                || completedStopPrisoners.Count == 0
                || completedStopPrisoners.Any(prisoner =>
                    !IsPrisonerInAssignedTransportSeat(prisoner,
                        PrisonerSeat(prisoner, ValidPrisoners().ToList()))))
                return Fail("The previous pickup stop cannot be left until its suspects are physically confirmed in the van.", true);

            _activeDispatchPickupStopIndex++;
            DispatchPickupStop next = _dispatchPickupStops[
                _activeDispatchPickupStopIndex];
            if (next.SceneTarget == Vector3.Zero || next.RoadTarget == Vector3.Zero)
                return Fail("The next Dispatch pickup stop lost its validated road access. Convoy custody remains active.", true);

            _pickupTarget = next.SceneTarget;
            _dispatchSceneVehicleAccessTarget = next.RoadTarget;
            _dispatchSceneWalkInDistanceMeters = 0f;
            _transportRouteRecoveryCount = 0;
            _lastTransportProgressPosition = _transport.Position;
            _lastTransportProgressAt = now;
            _lastDispatchPickupArrivalCheckAt = DateTime.MinValue;
            _lastDispatchPickupDriverEntryTaskAt = DateTime.MinValue;
            _dispatchPickupDriverEntryStartedAt = now;
            _dispatchCrewDriverSeatOccupantHandle = 0;
            _lastDispatchCrewDriverSeatCheckAt = DateTime.MinValue;
            _dispatchPickupDriverConflictLogged = false;
            _driverReleaseRequested = false;
            _driverReleaseRequestedAt = DateTime.MinValue;
            _transportOfficerReleaseRequested = false;
            _transportOfficerReleaseRequestedAt = DateTime.MinValue;
            _nextTransportOfficerReleaseTaskAt = DateTime.MinValue;
            _transportOfficerReleaseRecoveryLogged = false;
            _transportNearCustodyFallback = false;

            Ped driver = _dispatchSceneTransportDriver;
            if (driver == null || !driver.Exists() || driver.IsDead)
                driver = TryReassignDispatchCrewDriver(
                    "ReturningForRemainingDispatchSuspects");
            if (driver == null || !driver.Exists() || driver.IsDead
                || _transport == null || !_transport.Exists()
                || driver.Position.DistanceTo(_transport.Position)
                    > MaximumDispatchSceneVehicleDistance)
                return Fail("The transport crew could not verify a nearby driver for the next pickup stop. Convoy custody remains active for recovery.", true);

            _dispatchSceneTransportDriver = driver;
            _transportDriver = driver;
            SetEmergencySignals(_transport, true);
            try { Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, false); }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_PICKUP_BRAKE_RELEASE_FAILED", ex);
            }
            SetPhase(CustodyPhase.DispatchPickupDriverBoarding);
            LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_RETURN_STARTED",
                "Operation=" + _operationId
                + "; Stop=" + (_activeDispatchPickupStopIndex + 1)
                + "; Stops=" + _dispatchPickupStops.Count
                + "; SceneTarget=" + _pickupTarget
                + "; RoadTarget=" + _dispatchSceneVehicleAccessTarget
                + "; RemainingSuspects=" + string.Join(",",
                    next.PrisonerHandles.Select(handle => handle.ToString()).ToArray())
                + "; Driver=" + driver.Handle
                + "; PreviousStopPhysicallyLoaded=true");
            Notify("~b~POLICE CUSTODY~s~\nThe transport has secured the suspect near you and is returning to the incident scene for the remaining suspects.");
            return "The first suspect is physically secured in the van. Transport is returning to the incident scene for the remaining suspects.";
        }

        private string ProcessDispatchPickupRoute(DateTime now)
        {
            if (_dispatchGroupPlayerDrivenPickup)
                return ProcessDispatchGroupPlayerRoute(now);

            if (!_dispatchCustodyOperation || _isRequestedConvoyActivity
                || _playerOwnedTransport || _dispatchPickupStops.Count <= 1
                || _activeDispatchPickupStopIndex <= 0
                || _activeDispatchPickupStopIndex >= _dispatchPickupStops.Count)
                return Fail("The Dispatch group-pickup route lost its custody operation. Convoy custody remains available for recovery.", true);
            if (_transport == null || !_transport.Exists() || _transport.IsDead)
                return Fail("The Dispatch transport became unavailable before reaching the remaining suspects.", true);

            DispatchPickupStop stop = _dispatchPickupStops[
                _activeDispatchPickupStopIndex];
            Ped driver = _dispatchSceneTransportDriver;
            if (driver == null || !driver.Exists() || driver.IsDead)
                driver = TryReassignDispatchCrewDriver(
                    "DriverUnavailableDuringGroupPickup");
            if (driver == null || !driver.Exists() || driver.IsDead
                || driver.Position.DistanceTo(_transport.Position)
                    > MaximumDispatchSceneVehicleDistance)
                return Fail("No nearby transport officer can drive to the remaining suspects. Convoy custody remains active for recovery.", true);

            _dispatchSceneTransportDriver = driver;
            _transportDriver = driver;
            int driverSeatOccupant = DispatchCrewDriverSeatOccupant(now);
            if (driverSeatOccupant == driver.Handle)
            {
                if (_phase == CustodyPhase.DispatchPickupDriverBoarding)
                {
                    _dispatchPickupDriverEntryStartedAt = DateTime.MinValue;
                    _lastDispatchPickupDriverEntryTaskAt = DateTime.MinValue;
                    _transportRouteRecoveryCount = 0;
                    _lastTransportProgressPosition = _transport.Position;
                    _lastTransportProgressAt = now;
                    _lastDispatchPickupArrivalCheckAt = DateTime.MinValue;
                    SetPhase(CustodyPhase.DispatchCrewDriveToPickup);
                    try
                    {
                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                            driver,
                            _transport,
                            stop.RoadTarget.X,
                            stop.RoadTarget.Y,
                            stop.RoadTarget.Z,
                            22.0f,
                            (int)EmergencyDrivingFlags,
                            10.0f);
                        _lastDriverTaskAt = now;
                        LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_ROUTE_TASK",
                            "Operation=" + _operationId
                            + "; Stop=" + (_activeDispatchPickupStopIndex + 1)
                            + "; Driver=" + driver.Handle
                            + "; Vehicle=" + _transport.Handle
                            + "; RoadTarget=" + stop.RoadTarget
                            + "; PhysicalDriverSeatConfirmed=true");
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_ROUTE_TASK_FAILED", ex);
                        return Fail("The transport officer could not start the route to the remaining suspects.", true);
                    }
                    return string.Empty;
                }

                if (now >= _lastDispatchPickupArrivalCheckAt.AddMilliseconds(
                    DispatchCrewArrivalCheckMilliseconds))
                {
                    _lastDispatchPickupArrivalCheckAt = now;
                    float distance = _transport.Position.DistanceTo(stop.RoadTarget);
                    if (distance <= DispatchSceneVehicleArrivalRadius
                        && Math.Abs(_transport.Speed) <= 2.5f)
                    {
                        SetEmergencySignals(_transport, false);
                        LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PICKUP_ARRIVED",
                            "Operation=" + _operationId
                            + "; Stop=" + (_activeDispatchPickupStopIndex + 1)
                            + "; SceneTarget=" + stop.SceneTarget
                            + "; RoadTarget=" + stop.RoadTarget
                            + "; Vehicle=" + _transport.Handle
                            + "; Distance=" + distance.ToString("0.0")
                            + "; Speed=" + Math.Abs(_transport.Speed).ToString("0.0")
                            + "; PhysicalArrivalConfirmed=true");
                        return BeginTransportLoading(now, false);
                    }
                    TryRecoverStalledTransport(stop.RoadTarget, now);
                    if (_transportRouteRecoveryCount >= MaximumTransportRouteRecoveries
                        && now >= _lastTransportProgressAt.AddSeconds(
                            TransportRouteStallTimeoutSeconds))
                        return Fail("The transport could not reach the validated incident road node after bounded route recovery. Convoy custody remains available for recovery.", true);
                }
                return string.Empty;
            }

            if (driverSeatOccupant > 0 && driverSeatOccupant != driver.Handle)
            {
                Ped seatedOfficer = _sceneEscortOfficers.FirstOrDefault(officer =>
                    officer != null && officer.Exists()
                    && officer.Handle == driverSeatOccupant);
                if (seatedOfficer != null && IsOfficerInTransport(seatedOfficer)
                    && now >= _lastDispatchPickupDriverEntryTaskAt.AddMilliseconds(
                        DispatchCrewDriverEntryTaskRefreshMilliseconds))
                {
                    try
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            seatedOfficer, _transport, 0);
                        _lastDispatchPickupDriverEntryTaskAt = now;
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_DISPATCH_PICKUP_PASSENGER_EXIT_FAILED", ex);
                    }
                }
                else if (!_dispatchPickupDriverConflictLogged)
                {
                    _dispatchPickupDriverConflictLogged = true;
                    LogRuntime("POLICE_CONVOY_DISPATCH_PICKUP_DRIVER_SEAT_OCCUPIED",
                        "Operation=" + _operationId
                        + "; Driver=" + driver.Handle
                        + "; Occupant=" + driverSeatOccupant
                        + "; CustodyRetained=true");
                }
                return string.Empty;
            }

            if (_dispatchPickupDriverEntryStartedAt == DateTime.MinValue)
                _dispatchPickupDriverEntryStartedAt = now;
            if (now >= _dispatchPickupDriverEntryStartedAt.AddSeconds(
                DispatchCrewDriverEntryTimeoutSeconds))
                return Fail("The transport officer could not physically enter the van for the return to the incident scene.", true);
            if (_lastDispatchPickupDriverEntryTaskAt == DateTime.MinValue
                || now >= _lastDispatchPickupDriverEntryTaskAt.AddMilliseconds(
                    DispatchCrewDriverEntryTaskRefreshMilliseconds))
            {
                try
                {
                    driver.Task.EnterVehicle(_transport, VehicleSeat.Driver,
                        10000, 1.0f);
                    _lastDispatchPickupDriverEntryTaskAt = now;
                    LogRuntime("POLICE_CONVOY_DISPATCH_PICKUP_DRIVER_ENTRY_TASK",
                        "Operation=" + _operationId
                        + "; Driver=" + driver.Handle
                        + "; Vehicle=" + _transport.Handle
                        + "; Seat=Driver; PhysicalEntryRequired=true");
                }
                catch (Exception ex)
                {
                    _lastDispatchPickupDriverEntryTaskAt = now;
                    LogException("POLICE_CONVOY_DISPATCH_PICKUP_DRIVER_ENTRY_FAILED", ex);
                }
            }
            return string.Empty;
        }

        private string ProcessDispatchGroupPlayerRoute(DateTime now)
        {
            if (!DispatchGroupPlayerCollectionActive
                || _transport == null || !_transport.Exists() || _transport.IsDead)
                return Fail("The scattered-suspect collection van is unavailable. Dispatch custody remains recoverable.", true);
            if (_dispatchGroupLoadedPendingHandoff.Count > 0)
                return string.Empty;

            DispatchPickupStop activeStop = ActiveDispatchPickupStop();
            if (activeStop != null && !activeStop.Completed)
                return string.Empty;

            int nextStopIndex = _dispatchPickupStops.FindIndex(stop =>
                stop != null && !stop.Completed
                && stop.PrisonerHandles.Any(handle =>
                    !_dispatchCustodyTransferredHandles.Contains(handle)));
            if (nextStopIndex < 0)
            {
                if (_dispatchGroupCollectionAwaitingMore)
                {
                    if (!_dispatchGroupPlayerRoutePromptLogged)
                    {
                        _dispatchGroupPlayerRoutePromptLogged = true;
                        LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_WAITING_FOR_BACKUP_CUSTODY",
                            "Operation=" + _operationId
                            + "; Van=" + _transport.Handle
                            + "; Loaded=" + string.Join(",",
                                _dispatchCustodyTransferredHandles.OrderBy(handle => handle)
                                    .Select(handle => handle.ToString()).ToArray())
                            + "; BackupGuardRetained=true; PlayerRouteTaskIssued=false");
                        Notify("~b~POLICE CUSTODY~s~\nThe van is waiting safely. Backup is still securing the remaining suspect; secured prisoners remain guarded until the van reaches them.");
                    }
                    return string.Empty;
                }

                List<Ped> living = ValidPrisoners().ToList();
                if (living.Count == 0 || living.Any(prisoner =>
                    !_dispatchCustodyTransferredHandles.Contains(prisoner.Handle)
                    || !IsPrisonerInAssignedTransportSeat(
                        prisoner, PrisonerSeat(prisoner, living))))
                    return string.Empty;

                _state = LSPDDispatchState.AwaitingTransport;
                SetPhase(CustodyPhase.DispatchStationTransportDecision);
                _dispatchGroupPlayerRoutePromptLogged = false;
                LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_ALL_PICKUPS_COMPLETE",
                    "Operation=" + _operationId
                    + "; Vehicle=" + _transport.Handle
                    + "; Prisoners=" + DescribePrisoners()
                    + "; PhysicalSeatsConfirmed=true; Driver=Player; PlayerChoice=Y/N");
                Notify("~b~POLICE CUSTODY~s~\nAll suspects are secured. Press Y to drive the van to the Police Station. Press N to keep the van parked and custody active here.");
                return "All suspects are secured. Choose whether to drive the transport to the Police Station.";
            }

            _activeDispatchPickupStopIndex = nextStopIndex;
            DispatchPickupStop next = _dispatchPickupStops[nextStopIndex];
            if (next.SceneTarget == Vector3.Zero || next.RoadTarget == Vector3.Zero)
                return Fail("The next suspect custody point has no validated road rendezvous. Dispatch custody remains recoverable.", true);

            if (!_dispatchGroupPlayerRoutePromptLogged)
            {
                CaptureAndSetWaypoint(next.RoadTarget);
                _dispatchGroupPlayerRoutePromptLogged = true;
                LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PLAYER_ROUTE_READY",
                    "Operation=" + _operationId
                    + "; Stop=" + (nextStopIndex + 1)
                    + "; Stops=" + _dispatchPickupStops.Count
                    + "; SceneTarget=" + next.SceneTarget
                    + "; RoadTarget=" + next.RoadTarget
                    + "; Driver=Player; AutonomousDrivingTaskIssued=false");
                if (!PlayerDrivesTransport())
                    Notify("~b~POLICE CUSTODY~s~\nEnter the transport driver's seat and drive to the marked Backup custody location. The assigned Backup officer will load the suspect and remain at the scene.");
            }

            if (!PlayerDrivesTransport())
                return string.Empty;

            float distance = _transport.Position.DistanceTo(next.RoadTarget);
            if (distance > DispatchSceneVehicleArrivalRadius
                || Math.Abs(_transport.Speed) > 2.5f)
                return string.Empty;

            _dispatchGroupPlayerRoutePromptLogged = false;
            SetEmergencySignals(_transport, false);
            _dispatchGroupPlayerRoutePromptLogged = false;
            SetPhase(CustodyPhase.SceneLoading);
            LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_PLAYER_ARRIVED",
                "Operation=" + _operationId
                + "; Stop=" + (nextStopIndex + 1)
                + "; SceneTarget=" + next.SceneTarget
                + "; RoadTarget=" + next.RoadTarget
                + "; Vehicle=" + _transport.Handle
                + "; PlayerDriverConfirmed=true"
                + "; Distance=" + distance.ToString("0.0", CultureInfo.InvariantCulture)
                + "; Stopped=true");
            return BeginTransportLoading(now, false);
        }

        private void ClearDispatchPickupPlan()
        {
            _dispatchPickupStops.Clear();
            _activeDispatchPickupStopIndex = -1;
            _lastDispatchPickupDriverEntryTaskAt = DateTime.MinValue;
            _dispatchPickupDriverEntryStartedAt = DateTime.MinValue;
            _lastDispatchPickupArrivalCheckAt = DateTime.MinValue;
            _dispatchPickupDriverConflictLogged = false;
        }

        private string ProcessLoading(DateTime now, bool prisonTrip)
        {
            if (!HasTransportVehicle())
                return Fail("The Police transport is no longer available during prisoner loading.", true);
            // A front-passenger escort may still be leaving the transport after
            // the driver has handed over the vehicle. Hold prisoner movement
            // until that officer is outside so the automatic custody escort,
            // rather than a casual direct entry task, owns the handoff.
            if (!ReleaseTransportOfficersForLoading(now))
                return string.Empty;
            List<Ped> prisoners = ValidPrisoners().ToList();
            List<Ped> pickupPrisoners = CurrentDispatchPickupPrisoners(prisoners);
            if (pickupPrisoners.Count == 0)
                return Fail("No secured suspect is assigned to this physical pickup stop.", true);
            bool remoteScatteredPickup = !prisonTrip
                && IsRemoteDispatchGroupPickup();
            if (remoteScatteredPickup && !PlayerIsTransportDriver())
            {
                if (!_dispatchGroupPlayerRoutePromptLogged)
                {
                    _dispatchGroupPlayerRoutePromptLogged = true;
                    LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_BACKUP_LOADING_WAITING_FOR_PLAYER_DRIVER",
                        "Operation=" + _operationId
                        + "; Vehicle=" + _transport.Handle
                        + "; Stop=" + (_activeDispatchPickupStopIndex + 1)
                        + "; PlayerDriverSeatConfirmed=false"
                        + "; BackupOfficerOwnsRemoteLoading=true");
                    Notify("~b~POLICE CUSTODY~s~\nRemain in the transport driver's seat while the assigned Backup officer loads the secured suspect.");
                }
                return string.Empty;
            }
            HashSet<int> activePrisonerHandles = new HashSet<int>(
                prisoners.Select(prisoner => prisoner.Handle));
            foreach (int staleHandle in _prisonerLoadingTimeoutStartedAt.Keys
                .Where(handle => !activePrisonerHandles.Contains(handle)).ToArray())
                _prisonerLoadingTimeoutStartedAt.Remove(staleHandle);
            foreach (int staleHandle in _lastAssignedSeatCheckAt.Keys
                .Where(handle => !activePrisonerHandles.Contains(handle)).ToArray())
            {
                _lastAssignedSeatCheckAt.Remove(staleHandle);
                _lastAssignedSeatCheckResult.Remove(staleHandle);
                _lastPrisonerVehicleExitAt.Remove(staleHandle);
            }
            foreach (Ped prisoner in pickupPrisoners)
            {
                if (_prisonerLoadedLogIssued.Contains(prisoner.Handle)
                    && IsInTransport(prisoner))
                {
                    if (_dispatchCustodyOperation)
                        ClosePrisonerDoor(prisoner);
                    continue;
                }

                VehicleSeat seat = PrisonerSeat(prisoner, prisoners);
                if (seat == VehicleSeat.None || seat == VehicleSeat.Any)
                    return Fail(
                        "No rear custody seat is available for every prisoner in this transport.",
                        true);

                if (IsPrisonerInAssignedTransportSeat(prisoner, seat))
                {
                    _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                    _lastPrisonerVehicleExitAt.Remove(prisoner.Handle);
                    Ped completedEscort;
                    _prisonerEscortOfficers.TryGetValue(
                        prisoner.Handle, out completedEscort);
                    DetachPrisonerFromEscort(prisoner, completedEscort,
                        "PhysicalVehicleEntryConfirmed");
                    SetPrisonerLoadingStage(prisoner, completedEscort,
                        PrisonerLoadingStage.Loaded, now,
                        "PrisonerConfirmedInsideTransport");
                    if (_dispatchGroupPlayerDrivenPickup)
                        _dispatchGroupLoadedPendingHandoff.Add(prisoner.Handle);
                    if (_prisonerLoadedLogIssued.Add(prisoner.Handle))
                    {
                        LogRuntime(
                            "POLICE_CONVOY_PRISONER_LOADED",
                            "Prisoner=" + prisoner.Handle + "; Vehicle=" + _transport.Handle
                            + "; Seat=" + seat
                            + "; Phase=" + _phase + "; PhysicalEntry=true"
                            + "; SeatOccupantConfirmed=true");
                    }
                    ClosePrisonerDoor(prisoner);
                    _playerEscortPrisoners.Remove(prisoner.Handle);
                    _playerEscortFallbacks.Remove(prisoner.Handle);
                    _playerHandcuffAnimationUntil.Remove(prisoner.Handle);
                    _playerLoadingContactStartedAt.Remove(prisoner.Handle);
                    _playerEscortLostAt.Remove(prisoner.Handle);
                    _playerDoorInteractions.Remove(prisoner.Handle);
                    _playerEntryInteractions.Remove(prisoner.Handle);
                    _lastPrisonerEscortPosition.Remove(prisoner.Handle);
                    _lastPrisonerEscortProgressAt.Remove(prisoner.Handle);
                    _lastPrisonerEscortRecoveryAt.Remove(prisoner.Handle);
                    continue;
                }
                if (IsInTransport(prisoner))
                {
                    // Being in the right vehicle is not proof that GTA seated
                    // the prisoner in the assigned custody seat. Keep the
                    // stage open and request a physical exit before retrying
                    // the documented rear-door loading sequence.
                    _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                    CommandLeaveTransport(prisoner, "WrongAssignedSeat:" + seat);
                    continue;
                }
                _lastPrisonerVehicleExitAt.Remove(prisoner.Handle);
                _lastAssignedSeatCheckAt.Remove(prisoner.Handle);
                _lastAssignedSeatCheckResult.Remove(prisoner.Handle);
                if (!_dispatchGroupPlayerDrivenPickup
                    || _dispatchCustodyTransferredHandles.Contains(prisoner.Handle))
                    MaintainPrisonerCustodyState(prisoner);

                // If the player is already at the active custody scene, keep
                // the prisoner waiting for the player's explicit E handoff
                // before assigning an NPC arrest task. This is intentionally a
                // bounded offer: a player who is away from the scene or does
                // not take custody still gets the existing transport-officer
                // fallback rather than an indefinite loading state.
                if (!remoteScatteredPickup
                    && ShouldOfferPlayerCustody(prisoner, now))
                {
                    // The Player's initial choice is outside each suspect's
                    // physical loading wait. Pause the clock if a fallback
                    // makes the choice available again.
                    _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                    if (!_playerCustodyOfferNotified)
                    {
                        _playerCustodyOfferNotified = true;
                        LogRuntime(
                            "POLICE_CONVOY_PLAYER_CUSTODY_OFFERED",
                            "Player is within the custody handoff radius; E is available before NPC escort takeover. Prisoners="
                            + DescribePrisoners());
                        Notify("~b~POLICE CUSTODY~s~\nYou are at the prisoner. Press E to handcuff and take the physical escort; the transport officer will take over if you move away.");
                    }
                    continue;
                }

                // The transport has one usable rear-door loading point. Let
                // one officer finish the grounded escort and entry at a time;
                // queue the next prisoner without spending that prisoner's
                // physical-loading timeout while the escort is occupied.
                if (HasOtherPrisonerLoadingEscort(prisoner.Handle))
                {
                    _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                    continue;
                }

                // Convoy normally has a released transport officer to own this
                // physical handoff. Never fall through to a normal autonomous
                // EnterVehicle task: the escort must own the custody handoff.
                Ped loadingEscort;
                if (remoteScatteredPickup)
                {
                    if (!_dispatchGroupBackupEscorts.TryGetValue(
                            prisoner.Handle, out loadingEscort)
                        || loadingEscort == null || !loadingEscort.Exists()
                        || loadingEscort.IsDead || IsOfficerInTransport(loadingEscort))
                    {
                        _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                        if (_dispatchGroupBackupLoaderWaitLogged.Add(prisoner.Handle))
                        {
                            LogRuntime("POLICE_CONVOY_DISPATCH_GROUP_BACKUP_LOADER_PENDING",
                                "Operation=" + _operationId
                                + "; Suspect=" + prisoner.Handle
                                + "; Stop=" + (_activeDispatchPickupStopIndex + 1)
                                + "; BackupOfficer=" + (loadingEscort == null
                                    || !loadingEscort.Exists() ? "unavailable" : loadingEscort.Handle.ToString())
                                + "; TransportEscortFallback=false"
                                + "; BackupCustodyRetained=true");
                            Notify("~b~POLICE CUSTODY~s~\nBackup is keeping the suspect secured. The assigned Backup officer will load them when available.");
                        }
                        continue;
                    }
                    _dispatchGroupBackupLoaderWaitLogged.Remove(prisoner.Handle);
                    _prisonerEscortOfficers[prisoner.Handle] = loadingEscort;
                }
                else
                    loadingEscort = FindLoadingEscort(prisoner);
                if (loadingEscort == null || IsOfficerInTransport(loadingEscort))
                {
                    if (remoteScatteredPickup)
                    {
                        _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                        continue;
                    }
                    if (HasOtherPrisonerLoadingEscort(prisoner.Handle))
                    {
                        // This suspect is queued behind an escort who is
                        // physically handling someone else. Start its clock
                        // when that escort becomes available.
                        _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                        continue;
                    }
                    StartPrisonerLoadingTimeout(prisoner, now);
                    if (HasPrisonerLoadingTimedOut(prisoner, now))
                        return Fail("No transport officer remained available for suspect "
                            + prisoner.Handle + " within the saved custody wait time.", true);
                    // A group transport can legitimately have fewer officers
                    // than prisoners. Keep the remaining secured subjects in
                    // Convoy ownership and expose the player handoff instead
                    // of failing the whole operation merely because the last
                    // subject has no free NPC escort yet.
                    if (CanPlayerTakeCustody(prisoner))
                        continue;
                    continue;
                }
                StartPrisonerLoadingTimeout(prisoner, now);
                bool escortIsWorking = MaintainPrisonerLoadingEscort(prisoner, seat, now);
                if (IsPrisonerInAssignedTransportSeat(prisoner, seat))
                {
                    _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                    continue;
                }
                if (IsInTransport(prisoner))
                {
                    _prisonerLoadingTimeoutStartedAt.Remove(prisoner.Handle);
                    CommandLeaveTransport(prisoner, "WrongAssignedSeat:" + seat);
                    continue;
                }
                if (HasPrisonerLoadingTimedOut(prisoner, now))
                    return Fail("The physical escort and loading for suspect "
                        + prisoner.Handle + " did not finish within that suspect's saved custody wait time.", true);
                if (escortIsWorking)
                    continue;
            }

            if (pickupPrisoners.All(prisoner =>
                IsPrisonerInAssignedTransportSeat(
                    prisoner, PrisonerSeat(prisoner, prisoners))
                && (!_dispatchCustodyOperation
                    || IsPrisonerDoorPhysicallyClosed(PrisonerDoorIndex(
                        PrisonerSeat(prisoner, prisoners))))))
            {
                if (!prisonTrip && _dispatchGroupPlayerDrivenPickup)
                {
                    SetPhase(CustodyPhase.DispatchGroupPickupWaiting);
                    return "The suspect is physically aboard. Convoy is confirming custody before the next pickup leg.";
                }
                if (!prisonTrip && HasNextDispatchPickupStop())
                    return BeginNextDispatchPickupStop(now, pickupPrisoners);

                if (!prisoners.All(prisoner => IsPrisonerInAssignedTransportSeat(
                    prisoner, PrisonerSeat(prisoner, prisoners))))
                    return Fail("A later pickup stop still has secured suspects waiting. Convoy custody remains active until every required suspect is physically loaded.", true);

                if (!prisonTrip)
                    ClearDispatchPickupPlan();

                bool crewOwnsDispatchTransport = !prisonTrip
                    && _dispatchCustodyOperation
                    && !_isRequestedConvoyActivity
                    && !_playerOwnedTransport
                    && !PlayerDrivesTransport();
                if (!crewOwnsDispatchTransport && !ReleaseDriverForPlayer(now))
                    return string.Empty;
                LogRuntime(
                    "POLICE_CONVOY_TRANSPORT_READY",
                    "Mode=" + (_isRequestedConvoyActivity ? "IndependentConvoy" : "DispatchCustody")
                    + "; PrisonTrip=" + prisonTrip
                    + "; Prisoners=" + DescribePrisoners()
                    + "; Vehicle=" + _transport.Handle
                    + "; PlayerDriverRequired="
                    + (crewOwnsDispatchTransport ? "false" : "true"));
                if (prisonTrip)
                {
                    // Prison staff are staged when the player reaches the
                    // prison. Creating them here, while the player is still
                    // at the station, allowed the receiving peds to be
                    // streamed away or left at an unrelated street node
                    // before the physical handoff began.
                    _state = LSPDDispatchState.PrisonTransfer;
                    CaptureAndSetWaypoint(_prisonDestination);
                    ReportStage("lsimmersivelife.police.transport.departing", "prison-departing");
                    SetPhase(CustodyPhase.DriveToPrison);
                    return "Prison convoy loaded. Take the vehicle and drive to Bolingbroke.";
                }
                if (crewOwnsDispatchTransport)
                {
                    _state = LSPDDispatchState.AwaitingTransport;
                    SetPhase(CustodyPhase.DispatchStationTransportDecision);
                    LogRuntime("POLICE_CONVOY_DISPATCH_STATION_TRANSPORT_CHOICE_READY",
                        "Operation=" + _operationId + "; Vehicle=" + _transport.Handle
                        + "; Prisoners=" + DescribePrisoners()
                        + "; AllAssignedRearSeatsConfirmed=true"
                        + "; ConvoyOfficerAndVehicleOwnerConfirmed=true"
                        + "; PlayerChoice=Y/N");
                    Notify("~b~POLICE CUSTODY~s~\nThe suspects are secured in the transport. Press Y to drive to the station, or N to decline. Dispatch closes after the assigned transport crew is physically aboard.");
                    return "Suspects secured in the transport. Press Y to continue to the station or N to let the transport officer take custody and end Dispatch.";
                }
                // Route to the authored station outside point. Receiving staff
                // are created only after the Player reaches safe handoff ground.
                CaptureAndSetWaypoint(_stationDestination);
                ReportStage("lsimmersivelife.police.transport.departing", "station-departing");
                SetPhase(CustodyPhase.DriveToStation);
                return "Prisoner loaded. Take the transport and drive to " + _stationDisplayName + ".";
            }

            return string.Empty;
        }

        private bool ShouldOfferPlayerCustody(Ped prisoner, DateTime now)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || IsInTransport(prisoner)
                || _playerEscortPrisoners.Contains(prisoner.Handle)
                || _playerEscortFallbacks.Contains(prisoner.Handle)
                || now >= _phaseStartedAt.AddSeconds(PlayerCustodyOfferSeconds))
                return false;

            Ped player = Game.Player.Character;
            return player != null && player.Exists()
                && player.Position.DistanceTo(prisoner.Position) <= PlayerCustodyOfferRadius;
        }

        private bool IsRemoteDispatchGroupPickup()
        {
            return _dispatchGroupPlayerDrivenPickup
                && _activeDispatchPickupStopIndex > 0
                && _activeDispatchPickupStopIndex < _dispatchPickupStops.Count;
        }

        private bool ShouldOfferPlayerUnloading(Ped prisoner, DateTime now)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || !IsInTransport(prisoner)
                || _playerUnloadingStartedAt.ContainsKey(prisoner.Handle)
                || now >= _phaseStartedAt.AddSeconds(PlayerCustodyOfferSeconds))
                return false;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return false;
            List<Ped> prisoners = ValidPrisoners().ToList();
            Vector3 rearDoor = TransportDoorPosition(PrisonerSeat(prisoner, prisoners));
            return player.Position.DistanceTo(rearDoor) <= PlayerCustodyOfferRadius;
        }

        private bool MaintainPlayerUnloading(Ped prisoner, DateTime now, string handoffStage)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                return false;

            int prisonerHandle = prisoner.Handle;
            DateTime startedAt;
            if (!_playerUnloadingStartedAt.TryGetValue(prisonerHandle, out startedAt))
                return false;

            if (!IsInTransport(prisoner))
            {
                _lastPrisonerVehicleExitAt.Remove(prisonerHandle);
                _playerUnloadingStartedAt.Remove(prisonerHandle);
                _playerUnloadingDoorInteractions.Remove(prisonerHandle);
                _playerUnloadingTasksIssued.Remove(prisonerHandle);
                _playerUnloadingFallbackHandles.Remove(prisonerHandle);
                LogRuntime("POLICE_CONVOY_PLAYER_PRISONER_UNLOADED",
                    "Stage=" + (handoffStage ?? string.Empty)
                    + "; Prisoner=" + prisonerHandle
                    + "; PhysicalExit=true; NextOwner=ReceivingOfficer");
                return false;
            }

            if (now < startedAt.AddSeconds(PlayerUnloadingInteractionTimeoutSeconds))
                return true;

            // Do not strand custody if the player opens the door but walks
            // away or GTA cannot finish the exit animation. Release only the
            // player's temporary claim; the existing standby officer resumes
            // the same physical unload and escort without spawning another unit.
            _playerUnloadingStartedAt.Remove(prisonerHandle);
            _playerUnloadingDoorInteractions.Remove(prisonerHandle);
            _playerUnloadingTasksIssued.Remove(prisonerHandle);
            _playerUnloadingFallbackHandles.Add(prisonerHandle);
            _prisonerUnloadingOfficers.Remove(prisonerHandle);
            _prisonerUnloadingEscortStartedAt.Remove(prisonerHandle);
            _lastPrisonerTaskAt.Remove(prisonerHandle);
            LogRuntime("POLICE_CONVOY_PLAYER_UNLOAD_FALLBACK",
                "Stage=" + (handoffStage ?? string.Empty)
                + "; Prisoner=" + prisonerHandle
                + "; Reason=PLAYER_UNLOAD_TIMEOUT; Fallback=ExistingReceivingOfficer");
            Notify("~b~POLICE CUSTODY~s~\nThe receiving officer is taking over the physical unload and handoff.");
            return false;
        }

        private VehicleSeat PrisonerSeat(Ped prisoner, IList<Ped> prisoners)
        {
            if (prisoner == null || prisoners == null)
                return VehicleSeat.Any;
            if (_playerOwnedTransport && prisoners.Count == 1
                && prisoners[0] != null && prisoners[0].Exists()
                && prisoners[0].Handle == prisoner.Handle)
                return _playerVehicleCustodySeat;
            int index = prisoners.IndexOf(prisoner);
            if (_dispatchGroupPlayerDrivenPickup)
            {
                // Keep the scattered group inside the cabin. ExtraSeat1 and
                // later may be side-hanging positions on the larger transport.
                // The front passenger seat is available because the delivery
                // escort exits before the Player takes over the van.
                switch (index)
                {
                    case 0:
                        return VehicleSeat.LeftRear;
                    case 1:
                        return VehicleSeat.RightRear;
                    case 2:
                        return VehicleSeat.RightFront;
                    default:
                        return VehicleSeat.None;
                }
            }
            switch (index)
            {
                case 0:
                    return VehicleSeat.LeftRear;
                case 1:
                    return VehicleSeat.RightRear;
                case 2:
                    return VehicleSeat.ExtraSeat1;
                case 3:
                    return VehicleSeat.ExtraSeat2;
                case 4:
                    return VehicleSeat.ExtraSeat3;
                case 5:
                    return VehicleSeat.ExtraSeat4;
                case 6:
                    return VehicleSeat.ExtraSeat5;
                case 7:
                    return VehicleSeat.ExtraSeat6;
                case 8:
                    return VehicleSeat.ExtraSeat7;
                case 9:
                    return VehicleSeat.ExtraSeat8;
                case 10:
                    return VehicleSeat.ExtraSeat9;
                case 11:
                    return VehicleSeat.ExtraSeat10;
                case 12:
                    return VehicleSeat.ExtraSeat11;
                case 13:
                    return VehicleSeat.ExtraSeat12;
                default:
                    // Never let GTA choose an arbitrary seat for custody;
                    // VehicleSeat.Any can select the front passenger seat.
                    return VehicleSeat.None;
            }
        }

        private bool ReleaseDriverForPlayer(DateTime now)
        {
            if (_transportDriver == null || !_transportDriver.Exists())
                return true;
            if (_dispatchCustodyOperation && !_isRequestedConvoyActivity
                && !_playerOwnedTransport && _dispatchSceneTransportDriver == null)
                _dispatchSceneTransportDriver = _transportDriver;
            if (!_driverReleaseRequested)
            {
                _driverReleaseRequested = true;
                _driverReleaseRequestedAt = now;
                try { Function.Call(Hash.TASK_LEAVE_VEHICLE, _transportDriver, _transport, 0); }
                catch (Exception ex) { LogException("POLICE_CONVOY_DRIVER_EXIT_TASK_FAILED", ex); }
                return false;
            }
            if (!_transportDriver.IsInVehicle())
            {
                try { _transportDriver.Task.LookAt(_transport, 1000); } catch { }
                _transportDriver = null;
                return true;
            }
            if (now >= _driverReleaseRequestedAt.AddSeconds(DriverReleaseTimeoutSeconds))
            {
                // This recovery fallback prevents a permanently occupied
                // driver's seat. Normal play uses the GTA exit task above.
                int releasedDriverHandle = _transportDriver.Handle;
                try { _transportDriver.Delete(); } catch { }
                _transportDriver = null;
                if (_dispatchSceneTransportDriver != null
                    && _dispatchSceneTransportDriver.Handle == releasedDriverHandle)
                    _dispatchSceneTransportDriver = null;
                _sceneEscortOfficers.RemoveAll(officer => officer == null
                    || !officer.Exists() || officer.Handle == releasedDriverHandle);
                LogRuntime("POLICE_CONVOY_DRIVER_RELEASE_RECOVERY",
                    "Driver exit task timed out; owned driver removed from retry candidates. Driver="
                    + releasedDriverHandle + "; Vehicle="
                    + (_transport == null || !_transport.Exists()
                        ? "unavailable" : _transport.Handle.ToString())
                    + "; SceneDriverReferenceCleared="
                    + (_dispatchSceneTransportDriver == null ? "true" : "false"));
                return true;
            }
            return false;
        }

        private bool ReleaseTransportOfficersForLoading(DateTime now)
        {
            if (_transport == null || !_transport.Exists())
                return true;

            bool officersStillInside = false;
            foreach (Ped officer in _sceneEscortOfficers
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead))
            {
                if (!IsOfficerInTransport(officer))
                    continue;

                officersStillInside = true;
                if (!_transportOfficerReleaseRequested)
                {
                    _transportOfficerReleaseRequested = true;
                    _transportOfficerReleaseRequestedAt = now;
                }
                if (now >= _nextTransportOfficerReleaseTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE, officer, _transport, 0);
                        _nextTransportOfficerReleaseTaskAt = now.AddSeconds(4);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_TRANSPORT_OFFICER_EXIT_FAILED", ex);
                    }
                }
            }

            if (!officersStillInside)
                return true;
            if (now >= _transportOfficerReleaseRequestedAt
                .AddSeconds(TransportOfficerReleaseTimeoutSeconds))
            {
                // A passenger that cannot leave must not block the custody
                // state forever. The released driver remains the bounded escort
                // fallback, while the retained passenger stays Convoy-owned for
                // normal cleanup rather than being teleported or discarded.
                if (!_transportOfficerReleaseRecoveryLogged)
                {
                    _transportOfficerReleaseRecoveryLogged = true;
                    LogRuntime("POLICE_CONVOY_TRANSPORT_OFFICER_EXIT_RECOVERY",
                        "A transport officer remained inside Vehicle=" + _transport.Handle
                        + "; loading will continue with an available outside officer.");
                }
                return true;
            }
            return false;
        }

        private string ProcessDispatchCrewStationTransport(DateTime now)
        {
            if (!_dispatchCrewStationTransport
                || !_dispatchCustodyOperation
                || _isRequestedConvoyActivity
                || _playerOwnedTransport)
                return FailOrRelinquishClosedCrewRun(
                    "The Dispatch transport crew lost ownership of its station run.", true);
            if (_transport == null || !_transport.Exists() || _transport.IsDead)
                return FailOrRelinquishClosedCrewRun(
                    "The transport van is unavailable during the crew station run.", true);

            if (_dispatchCrewPlayerRecoveryPending)
                return RecoverOrRelinquishClosedCrewRun(
                    _dispatchCrewPlayerRecoveryReason, now);

            Ped driver = _dispatchSceneTransportDriver;
            if (driver == null || !driver.Exists() || driver.IsDead)
            {
                driver = TryReassignDispatchCrewDriver(
                    "DriverUnavailableDuringStationRun",
                    driver != null ? driver.Handle : 0);
                if (driver == null)
                    return RecoverOrRelinquishClosedCrewRun(
                        "No live Convoy scene officer remained to drive the station route.", now);
                if (_phase != CustodyPhase.DispatchCrewDriverBoarding)
                    SetPhase(CustodyPhase.DispatchCrewDriverBoarding);
            }
            _transportDriver = driver;
            if (_stationDestination == Vector3.Zero)
                return FailOrRelinquishClosedCrewRun(
                    "The selected station has no outside custody point.", true);

            int driverSeatOccupant = DispatchCrewDriverSeatOccupant(now);
            if (driverSeatOccupant == driver.Handle)
            {
                _dispatchCrewDriverSeatConflictLogged = false;
                if (_phase == CustodyPhase.DispatchCrewDriverBoarding)
                {
                    SetPhase(CustodyPhase.DispatchCrewDriveToStation);
                    _lastDriverTaskAt = DateTime.MinValue;
                    _lastTransportProgressPosition = _transport.Position;
                    _lastTransportProgressAt = now;
                    _lastDispatchCrewRouteRecoveryCheckAt = now;
                    _lastDispatchCrewArrivalCheckAt = now;
                    if (_dispatchCrewDepartureStartedAt == DateTime.MinValue)
                        _dispatchCrewDepartureStartedAt = now;
                    if (_dispatchCrewDepartureOrigin == Vector3.Zero)
                        _dispatchCrewDepartureOrigin = _transport.Position;
                    if (_lastDispatchCrewStationRouteProgressAt == DateTime.MinValue)
                    {
                        _lastDispatchCrewStationRouteProgressAt = now;
                        _lastDispatchCrewStationGoalDistance =
                            _transport.Position.DistanceTo(_stationDestination);
                    }
                    LogRuntime("POLICE_CONVOY_DISPATCH_CREW_DRIVER_SEATED",
                        "Operation=" + _operationId + "; Driver=" + driver.Handle
                        + "; Vehicle=" + _transport.Handle
                        + "; Seat=Driver; PhysicalEntryConfirmed=true");
                }

                if (!EnsureRequiredDispatchCrewOfficersBoarded(now, driver))
                {
                    if (now >= _dispatchCrewDriverEntryStartedAt.AddSeconds(
                        DispatchCrewDriverEntryTimeoutSeconds))
                        return FailOrRelinquishClosedCrewRun(
                            "A required transport officer could not physically board the crew van after the Player declined the station drive.",
                            true);
                    return string.Empty;
                }

                if (!_dispatchClosedAfterCrewCustodyHandoff)
                {
                    _dispatchClosedAfterCrewCustodyHandoff = true;
                    _dispatchCrewCustodyHandoffReady = true;
                    LogRuntime("POLICE_CONVOY_DISPATCH_CREW_CUSTODY_HANDOFF_CONFIRMED",
                        "Operation=" + _operationId
                        + "; Vehicle=" + _transport.Handle
                        + "; Driver=" + driver.Handle
                        + "; Prisoners=" + DescribePrisoners()
                        + "; AllAssignedRearSeatsConfirmed=true"
                        + "; RequiredSceneCrewOfficersPhysicallySeated=true"
                        + "; DriverSeatConfirmed=true"
                        + "; ConvoyCustodyOwnerConfirmed=true"
                        + "; PlayerChoice=DeclinedStationTransport"
                        + "; PlayerDispatchEndsNow=true");
                    ReportStage("lsimmersivelife.police.transport.departing", "station-departing");
                    Notify("~b~POLICE CUSTODY~s~\nThe assigned transport crew is aboard with the secured suspects. Dispatch is clear; the crew will continue its departure independently.");
                }

                if (!_dispatchCrewExitStagingAttempted
                    && !_dispatchCrewExitStagingPending
                    && !_dispatchCrewExitStagingReached)
                {
                    _dispatchCrewExitStagingAttempted = true;
                    Vector3 exitRoadTarget;
                    float exitRouteDistance;
                    string exitRouteFailure;
                    if (TryResolveDispatchTransportRecoveryRoadTarget(
                        _transport.Position,
                        _transportRouteRecoveryCount + 1,
                        out exitRoadTarget,
                        out exitRouteDistance,
                        out exitRouteFailure))
                    {
                        _dispatchCrewRouteRecoveryTarget = exitRoadTarget;
                        _dispatchCrewExitStagingPending = true;
                        _dispatchTransportRouteRoadTarget = Vector3.Zero;
                        _dispatchTransportRouteDestination = Vector3.Zero;
                        _lastDispatchCrewStationRouteProgressAt = now;
                        _lastDispatchCrewStationGoalDistance =
                            _transport.Position.DistanceTo(exitRoadTarget);
                        LogRuntime("POLICE_CONVOY_DISPATCH_CREW_EXIT_STAGING_SELECTED",
                            "Operation=" + _operationId
                            + "; Vehicle=" + _transport.Handle
                            + "; Driver=" + driver.Handle
                            + "; SceneExitRoadTarget=" + exitRoadTarget
                            + "; RouteDistance=" + exitRouteDistance.ToString(
                                "0.0", CultureInfo.InvariantCulture)
                            + "; StationRouteFollows=true"
                            + "; RouteValidated=true; Teleport=false");
                    }
                    else
                    {
                        LogRuntime("POLICE_CONVOY_DISPATCH_CREW_EXIT_STAGING_UNAVAILABLE",
                            "Operation=" + _operationId
                            + "; Vehicle=" + _transport.Handle
                            + "; Position=" + _transport.Position
                            + "; Reason=" + (exitRouteFailure ?? "NoReachableNearbyRoadTarget")
                            + "; RecoveryWillRetry=true; CustodyRetained=true");
                    }
                }

                Vector3 stationRouteGoal = _dispatchCrewRouteRecoveryTarget != Vector3.Zero
                    ? _dispatchCrewRouteRecoveryTarget : _stationDestination;
                if (now >= _lastDriverTaskAt.AddMilliseconds(DriverTaskRefreshMilliseconds))
                    DriveTransportTo(stationRouteGoal, false);

                float departureDistance = _transport.Position.DistanceTo(
                    _dispatchCrewDepartureOrigin);
                if (_dispatchCrewDepartureStartedAt != DateTime.MinValue
                    && now >= _dispatchCrewDepartureStartedAt.AddSeconds(
                        DispatchCrewDepartureConfirmationMinimumSeconds)
                    && departureDistance >= DispatchCrewDepartureConfirmationDistance
                    && AllDispatchPrisonersPhysicallyLoaded())
                {
                    LogRuntime("POLICE_CONVOY_DISPATCH_CREW_DEPARTURE_CONFIRMED",
                        "Operation=" + _operationId
                        + "; Vehicle=" + _transport.Handle
                        + "; Driver=" + driver.Handle
                        + "; DepartureDistance=" + departureDistance.ToString("0.0")
                        + "; RequiredDistance="
                        + DispatchCrewDepartureConfirmationDistance.ToString("0.0")
                        + "; AssignedRearSeatsConfirmed=true"
                        + "; PlayerDispatchAlreadyEnded=true"
                        + "; BackgroundConvoyEndsAfterDeparture=true");
                    Complete(
                        "The transport crew departed with the secured suspects. The background custody run is clear.",
                        false);
                    return "Transport crew departure confirmed. Background custody movement ended.";
                }
                if (_dispatchCrewDepartureStartedAt != DateTime.MinValue
                    && now >= _dispatchCrewDepartureStartedAt.AddSeconds(
                        DispatchCrewDepartureConfirmationMaximumSeconds))
                    return RecoverOrRelinquishClosedCrewRun(
                        "The crew van did not complete a physical scene departure within the bounded confirmation time.", now);
                if (now >= _lastDispatchCrewRouteRecoveryCheckAt.AddMilliseconds(
                    DispatchCrewRouteRecoveryCheckMilliseconds))
                {
                    _lastDispatchCrewRouteRecoveryCheckAt = now;
                    TryRecoverDispatchCrewStationRoute(now);
                    if (_transportRouteRecoveryCount >= MaximumTransportRouteRecoveries
                        && now >= _lastDispatchCrewStationRouteProgressAt
                            .AddSeconds(TransportRouteStallTimeoutSeconds)
                        && _transport.Position.DistanceTo(_stationDestination)
                            > _settings.StationArrivalRadius)
                        return RecoverOrRelinquishClosedCrewRun(
                            "The crew van remained unable to make station-route progress after bounded recovery attempts.", now);
                }
                if (now >= _lastDispatchCrewArrivalCheckAt.AddMilliseconds(
                    DispatchCrewArrivalCheckMilliseconds))
                {
                    _lastDispatchCrewArrivalCheckAt = now;
                    if (_transport.Position.DistanceTo(_stationDestination)
                        <= _settings.StationArrivalRadius
                        && Math.Abs(_transport.Speed) <= StationHandoffStoppedSpeed)
                        return ProcessPlayerArrivalAtStation(now, true);
                }
                return string.Empty;
            }

            if (IsOfficerInTransport(driver))
            {
                // A surviving scene officer can still be in the front
                // passenger seat when the original driver disappears. Let
                // that owned officer leave physically before attempting the
                // driver's seat; issuing EnterVehicle while already seated
                // merely restarts GTA's task and used to strand loaded custody.
                if (now >= _dispatchCrewDriverEntryStartedAt
                    .AddSeconds(DispatchCrewDriverEntryTimeoutSeconds))
                    return RecoverOrRelinquishClosedCrewRun(
                        "The surviving transport officer could not physically leave the passenger seat to take over driving.", now);
                if (_lastDispatchCrewDriverEntryTaskAt == DateTime.MinValue
                    || now >= _lastDispatchCrewDriverEntryTaskAt.AddMilliseconds(
                        DispatchCrewDriverEntryTaskRefreshMilliseconds))
                {
                    try
                    {
                        Function.Call(Hash.TASK_LEAVE_VEHICLE, driver, _transport, 0);
                        _lastDispatchCrewDriverEntryTaskAt = now;
                        LogRuntime("POLICE_CONVOY_DISPATCH_REPLACEMENT_DRIVER_EXIT_TASK",
                            "Operation=" + _operationId + "; Driver=" + driver.Handle
                            + "; Vehicle=" + _transport.Handle
                            + "; Reason=ReleasePassengerForDriverSeat; PhysicalExitRequired=true");
                    }
                    catch (Exception ex)
                    {
                        _lastDispatchCrewDriverEntryTaskAt = now;
                        LogException("POLICE_CONVOY_DISPATCH_REPLACEMENT_DRIVER_EXIT_FAILED", ex);
                    }
                }
                return string.Empty;
            }

            if (driverSeatOccupant > 0)
            {
                if (!_dispatchCrewDriverSeatConflictLogged)
                {
                    _dispatchCrewDriverSeatConflictLogged = true;
                    LogRuntime("POLICE_CONVOY_DISPATCH_CREW_DRIVER_SEAT_OCCUPIED",
                        "Operation=" + _operationId + "; Vehicle=" + _transport.Handle
                        + "; Driver=" + driver.Handle
                        + "; Occupant=" + driverSeatOccupant
                        + "; CustodyRetained=true");
                    Notify("~b~POLICE CUSTODY~s~\nThe transport crew is waiting for the driver's seat to be clear. Custody remains active.");
                }
                if (now >= _dispatchCrewDriverEntryStartedAt
                    .AddSeconds(DispatchCrewDriverEntryTimeoutSeconds))
                    return FailOrRelinquishClosedCrewRun(
                        "The transport crew could not safely take the driver's seat.", true);
                return string.Empty;
            }

            if (driverSeatOccupant < 0)
            {
                if (now >= _dispatchCrewDriverEntryStartedAt
                    .AddSeconds(DispatchCrewDriverEntryTimeoutSeconds))
                    return FailOrRelinquishClosedCrewRun(
                        "The transport crew could not verify physical control of the driver's seat.", true);
                return string.Empty;
            }

            if (_phase == CustodyPhase.DispatchCrewDriveToStation)
            {
                _dispatchCrewDriverEntryStartedAt = now;
                _lastDispatchCrewDriverEntryTaskAt = DateTime.MinValue;
                _lastDispatchCrewRouteRecoveryCheckAt = DateTime.MinValue;
                _lastDispatchCrewArrivalCheckAt = DateTime.MinValue;
                SetPhase(CustodyPhase.DispatchCrewDriverBoarding);
                LogRuntime("POLICE_CONVOY_DISPATCH_CREW_DRIVER_REENTRY_REQUIRED",
                    "Operation=" + _operationId + "; Driver=" + driver.Handle
                    + "; Vehicle=" + _transport.Handle
                    + "; PhysicalEntryRequired=true");
            }
            if (now >= _dispatchCrewDriverEntryStartedAt
                .AddSeconds(DispatchCrewDriverEntryTimeoutSeconds))
                return FailOrRelinquishClosedCrewRun(
                    "The transport crew could not physically enter the van.", true);
            if (_lastDispatchCrewDriverEntryTaskAt == DateTime.MinValue
                || now >= _lastDispatchCrewDriverEntryTaskAt.AddMilliseconds(
                    DispatchCrewDriverEntryTaskRefreshMilliseconds))
            {
                try
                {
                    driver.Task.EnterVehicle(_transport, VehicleSeat.Driver, 10000, 1.0f);
                    _lastDispatchCrewDriverEntryTaskAt = now;
                    LogRuntime("POLICE_CONVOY_DISPATCH_CREW_DRIVER_ENTRY_TASK",
                        "Operation=" + _operationId + "; Driver=" + driver.Handle
                        + "; Vehicle=" + _transport.Handle
                        + "; Seat=Driver; AttemptIntervalMs="
                        + DispatchCrewDriverEntryTaskRefreshMilliseconds);
                }
                catch (Exception ex)
                {
                    _lastDispatchCrewDriverEntryTaskAt = now;
                    LogException("POLICE_CONVOY_DISPATCH_CREW_DRIVER_ENTRY_FAILED", ex);
                }
            }
            return string.Empty;
        }

        private int DispatchCrewDriverSeatOccupant(DateTime now)
        {
            if (now < _lastDispatchCrewDriverSeatCheckAt.AddMilliseconds(
                DispatchCrewDriverSeatCheckMilliseconds))
                return _dispatchCrewDriverSeatOccupantHandle;

            _lastDispatchCrewDriverSeatCheckAt = now;
            try
            {
                Ped occupant = Function.Call<Ped>(
                    Hash.GET_PED_IN_VEHICLE_SEAT,
                    _transport,
                    (int)VehicleSeat.Driver,
                    false);
                _dispatchCrewDriverSeatOccupantHandle = occupant != null && occupant.Exists()
                    ? occupant.Handle : 0;
            }
            catch (Exception ex)
            {
                _dispatchCrewDriverSeatOccupantHandle = -1;
                if (!_dispatchCrewDriverSeatConflictLogged)
                {
                    _dispatchCrewDriverSeatConflictLogged = true;
                    LogException("POLICE_CONVOY_DISPATCH_CREW_DRIVER_SEAT_CHECK_FAILED", ex);
                }
            }
            return _dispatchCrewDriverSeatOccupantHandle;
        }

        private string ProcessPlayerArrivalAtStation(DateTime now, bool transportCrewDriving = false)
        {
            if (transportCrewDriving)
            {
                if (!_dispatchCrewStationTransport
                    || _dispatchSceneTransportDriver == null
                    || DispatchCrewDriverSeatOccupant(now)
                        != _dispatchSceneTransportDriver.Handle)
                    return string.Empty;
            }
            else if (!PlayerDrivesTransport())
                return string.Empty;
            if (_stationDestination == Vector3.Zero)
                return Fail("The selected station has no outside custody point.", true);

            if (_stationHandoffMarkerPosition == Vector3.Zero)
            {
                Vector3 markerPosition;
                bool usedAuthoredGroundFallback;
                if (!TryResolveStationHandoffMarkerPosition(
                    out markerPosition, out usedAuthoredGroundFallback))
                    return Fail("The selected station has no usable receiving destination.", true);

                _stationHandoffMarkerPosition = markerPosition;
                CaptureAndSetWaypoint(_stationHandoffMarkerPosition);
                LogRuntime("POLICE_CONVOY_STATION_RECEIVING_MARKER_READY",
                    "Station=" + _stationDisplayName
                    + "; StationId=" + (_profile == null ? string.Empty : _profile.Selection.StationId)
                    + "; StationLocation=" + (SelectedStation() == null
                        ? string.Empty : SelectedStation().LocationId)
                    + "; VehicleReceivingDestination=" + _stationDestination
                    + "; PedestrianStationIntake=" + _stationIntakeDestination
                    + "; GroundedReceivingDestination=" + _stationHandoffMarkerPosition
                    + "; AuthoredElevationFallback=" + usedAuthoredGroundFallback
                    + "; VehicleParkingPoseValidated=false; CustodyRetained=true");
            }

            DrawStationHandoffMarker();
            float preparationRadius = Math.Max(StationHandoffPreparationRadius,
                _settings.StationArrivalRadius);
            bool nearStation = _transport.Position.DistanceTo(_stationDestination)
                <= preparationRadius;
            if (_stationHandoffGroundPosition == Vector3.Zero
                && nearStation
                && now >= _lastStationHandoffSearchAt.AddSeconds(2))
            {
                _lastStationHandoffSearchAt = now;
                Vector3 groundPosition;
                Vector3 receivingPosition;
                float handoffHeading;
                string reason;
                if (TryFindSafeStationHandoffGroundPosition(
                    out groundPosition, out handoffHeading,
                    out receivingPosition, out reason))
                {
                    _stationHandoffGroundPosition = groundPosition;
                    _stationHandoffReceivingPosition = receivingPosition;
                    _stationHandoffHeading = handoffHeading;
                    _stationHandoffSearchMessageShown = false;
                    _stationHandoffTargetMessageShown = false;
                    LogRuntime("POLICE_CONVOY_STATION_HANDOFF_GROUND_SELECTED",
                        "Station=" + _stationDisplayName
                        + "; ReceivingMarker=" + _stationHandoffMarkerPosition
                        + "; PreferredVehicleGround=" + _stationHandoffGroundPosition
                        + "; HandoffHeading=" + _stationHandoffHeading.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; ReceivingPoint=" + _stationHandoffReceivingPosition
                        + "; UsedSelectedStationGarageFallback="
                        + _stationHandoffUsedGarageFallback
                        + "; AuthoredGroundReferenceUsed="
                        + _stationHandoffAuthoredGroundFallbackUsed
                        + "; CustodyRetained=true");
                }
                else if (!_stationHandoffSearchMessageShown)
                {
                    _stationHandoffSearchMessageShown = true;
                    LogRuntime("POLICE_CONVOY_STATION_HANDOFF_GROUND_UNAVAILABLE",
                        "Station=" + _stationDisplayName
                        + "; ReceivingMarker=" + _stationHandoffMarkerPosition
                        + "; Reason=" + reason
                        + "; PhysicalVehiclePoseValidated=false; CustodyRetained=true");
                    Notify("~b~POLICE CUSTODY~s~\nThe selected Station vehicle receiving area is marked. No safe van pose is confirmed yet; reposition on clear, level ground beside the marker so the rear doors and officer route can be checked. Custody remains active.");
                }
            }

            if (nearStation)
            {
                EnsureStationHandoffOfficers();
                OrderStationHandoffGuards();
            }

            float handoffDistance = _transport.Position.DistanceTo(_stationHandoffMarkerPosition);
            if (handoffDistance > StationHandoffMaximumMarkerOffset
                || Math.Abs(_transport.Speed) > StationHandoffStoppedSpeed)
            {
                if (!_stationHandoffTargetMessageShown)
                {
                    _stationHandoffTargetMessageShown = true;
                    Notify("~b~POLICE CUSTODY~s~\nDrive to the marked Station vehicle receiving area, not the station doorway, and stop on clear ground. Rear-door access and the receiving-officer route must be confirmed before unloading.");
                }
                return string.Empty;
            }

            SetPhase(CustodyPhase.StationUnloading);
            _playerUnloadingOfferNotified = true;
            if (_dispatchCrewStationTransport)
            {
                LogRuntime("POLICE_CONVOY_DISPATCH_CREW_STATION_ARRIVED",
                    "Operation=" + _operationId + "; Driver="
                    + (_dispatchSceneTransportDriver == null
                        ? "none" : _dispatchSceneTransportDriver.Handle.ToString())
                    + "; Vehicle=" + _transport.Handle
                    + "; ReceivingMarker=" + _stationHandoffMarkerPosition);
                Notify("~b~POLICE CUSTODY~s~\nTransport crew reached the station. Standby officers are receiving the secured suspects.");
                return "Transport crew reached the station; standby officers are completing the physical custody handoff.";
            }
            LogRuntime("POLICE_CONVOY_PLAYER_UNLOAD_AVAILABLE",
                "Stage=STATION; E=OpenRearDoorThenUnload; StandbyOfficer=CompletesIntake");
            Notify("~b~POLICE CUSTODY~s~\nStation reached. Exit the transport; at the rear door press E to open it, then E again to unload. The station officer completes intake.");
            return "Prisoner transport arrived at " + _stationDisplayName + ". Station handoff is in progress.";
        }

        private string ProcessStationUnloading(DateTime now)
        {
            DrawStationHandoffMarker();
            if (_transport == null || !_transport.Exists())
                return Fail("The custody transport is unavailable at the station handoff.", true);
            if (_stationHandoffMarkerPosition == Vector3.Zero
                || _transport.Position.DistanceTo(_stationHandoffMarkerPosition)
                    > StationHandoffMaximumMarkerOffset
                || Math.Abs(_transport.Speed) > StationHandoffStoppedSpeed)
            {
                if (!_stationHandoffStopMessageShown)
                {
                    _stationHandoffStopMessageShown = true;
                        Notify("~b~POLICE CUSTODY~s~\nBring the transport back to the marked Station receiving area and stop before unloading can continue.");
                }
                return string.Empty;
            }
            _stationHandoffStopMessageShown = false;
            if (!_stationHandoffActualPoseValidated
                || _transport.Position.DistanceTo(_stationHandoffActualPosePosition) > 0.75f
                || HeadingDifference(_transport.Heading, _stationHandoffActualPoseHeading) > 18.0f)
            {
                Vector3 actualGroundPosition;
                Vector3 receivingPosition;
                string clearanceFailure;
                if (!TryValidateActualStationHandoffPose(
                    _transport.Position, _transport.Heading,
                    out actualGroundPosition, out receivingPosition,
                    out clearanceFailure))
                {
                    _stationHandoffActualPoseValidated = false;
                    if (!_stationHandoffAccessMessageShown)
                    {
                        _stationHandoffAccessMessageShown = true;
                        LogRuntime("POLICE_CONVOY_STATION_HANDOFF_ACCESS_REJECTED",
                            "Vehicle=" + _transport.Handle
                            + "; Position=" + _transport.Position
                            + "; Heading=" + _transport.Heading.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Reason=" + clearanceFailure
                            + "; CustodyRemainsActive=true");
                        Notify("~b~POLICE CUSTODY~s~\nThis parking pose blocks the custody door or receiving route. Reposition the Utility on the marked clear roadside spot; custody remains active.");
                    }
                    return string.Empty;
                }
                _stationHandoffGroundPosition = actualGroundPosition;
                _stationHandoffActualPoseValidated = true;
                _stationHandoffActualPosePosition = _transport.Position;
                _stationHandoffActualPoseHeading = _transport.Heading;
                _stationHandoffReceivingPosition = receivingPosition;
                _stationHandoffAccessMessageShown = false;
                LogRuntime("POLICE_CONVOY_STATION_HANDOFF_ACCESS_CONFIRMED",
                    "Vehicle=" + _transport.Handle
                    + "; Position=" + _transport.Position
                    + "; Heading=" + _transport.Heading.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; ReceivingPoint=" + _stationHandoffReceivingPosition
                    + "; RearDoorClearance=true; PlayerAccess=true; OfficerAccess=true; WalkRoute=true"
                    + "; AuthoredGroundReferenceUsed="
                    + _stationHandoffAuthoredGroundFallbackUsed);
            }
            if (!EnsureStationHandoffOfficers())
            {
                if (now >= _phaseStartedAt.AddSeconds(ModelPreparationTimeoutSeconds))
                {
                    if (!HasRecoverablePlayerStationCustody())
                        return Fail("Station handoff officers could not be prepared safely.", true);
                    if (!_stationHandoffStallMessageShown)
                    {
                        _stationHandoffStallMessageShown = true;
                        LogRuntime("POLICE_CONVOY_STATION_OFFICER_PREPARATION_STALLED",
                            "Vehicle=" + _transport.Handle
                            + "; LivePrisoners=" + ValidPrisoners().Count(prisoner => !prisoner.IsDead)
                            + "; CustodyRemainsActive=true; RetrySameOperation=true");
                        Notify("~b~POLICE CUSTODY~s~\nStation staff are still approaching. Your secured prisoner and Utility custody remain active while they retry.");
                    }
                    _phaseStartedAt = now;
                }
                return string.Empty;
            }
            _stationHandoffStallMessageShown = false;
            OrderStationHandoffGuards();
            if (!HasStationStandbyOfficerRouteToReceivingPoint())
            {
                if (!_stationHandoffAccessMessageShown)
                {
                    _stationHandoffAccessMessageShown = true;
                    LogRuntime("POLICE_CONVOY_STATION_STANDBY_ROUTE_UNAVAILABLE",
                        "Station=" + _stationDisplayName
                        + "; StationId=" + (_profile == null ? string.Empty : _profile.Selection.StationId)
                        + "; ReceivingPoint=" + _stationHandoffReceivingPosition
                        + "; StandbyOfficers=" + _stationHandoffOfficers.Count(ped =>
                            ped != null && ped.Exists() && !ped.IsDead)
                        + "; CustodyRetained=true");
                    Notify("~b~POLICE CUSTODY~s~\nThe standby officer is at the selected Station, but the walking route to this vehicle is blocked. Reposition on clear ground near the Station marker; custody remains active.");
                }
                return string.Empty;
            }
            _stationHandoffAccessMessageShown = false;
            foreach (Ped prisoner in ValidPrisoners())
            {
                if (!_dispatchCrewStationTransport
                    && ShouldOfferPlayerUnloading(prisoner, now))
                {
                    if (!_playerUnloadingOfferNotified)
                    {
                        _playerUnloadingOfferNotified = true;
                        LogRuntime("POLICE_CONVOY_PLAYER_UNLOAD_AVAILABLE",
                            "Stage=STATION; Prisoner=" + prisoner.Handle
                            + "; E=OpenRearDoorThenUnload; StandbyOfficer=CompletesIntake");
                        Notify("~b~POLICE CUSTODY~s~\nAt the transport's rear door press E to open it, then E again to unload. The station officer will finish intake if you do not take over.");
                    }
                    continue;
                }
                if (MaintainPlayerUnloading(prisoner, now, "STATION"))
                    continue;
                MaintainPrisonerUnloadingEscort(
                    prisoner,
                    _stationHandoffOfficers,
                    StationHoldingPosition(prisoner),
                    now,
                    "STATION");
            }
            RefreshStationHandoffProgress(now);
            List<Ped> stationPrisoners = ValidPrisoners().ToList();
            if (stationPrisoners.Count > 0 && stationPrisoners.All(prisoner =>
                !IsInTransport(prisoner)
                && _prisonerUnloadingEscortStartedAt.ContainsKey(prisoner.Handle)
                && prisoner.Position.DistanceTo(StationHoldingPosition(prisoner)) <= 4.0f
                && IsNearHandoffOfficer(prisoner, _stationHandoffOfficers)))
            {
                _state = LSPDDispatchState.HoldingAtStation;
                SetPhase(CustodyPhase.HoldingAtStation);
                ReportStage("lsimmersivelife.police.transport.arrived", "station-arrived");
                LogRuntime("POLICE_CONVOY_STATION_HANDOFF_CONFIRMED",
                    "Prisoners=" + string.Join(",", stationPrisoners.Select(ped => ped.Handle.ToString()).ToArray())
                    + "; Officers=" + string.Join(",", _stationHandoffOfficers
                        .Where(ped => ped != null && ped.Exists())
                        .Select(ped => ped.Handle.ToString()).ToArray()));
                if (_dispatchCrewStationTransport)
                {
                    LogRuntime("POLICE_CONVOY_DISPATCH_CREW_STATION_HANDOFF_COMPLETED",
                        "Operation=" + _operationId
                        + "; Prisoners=" + string.Join(",", stationPrisoners
                            .Select(ped => ped.Handle.ToString()).ToArray())
                        + "; PhysicalStationOfficerReceipt=true; DispatchEndsAfterHandoff=true");
                    Complete("The transport crew completed the station handoff. Dispatch custody is closed.", false);
                    return "Transport crew completed the station handoff. Dispatch activity ended.";
                }
                Notify("~g~POLICE CUSTODY~s~\nPrisoner(s) received at the station. Approve or decline prison transfer.");
                return "Prisoner(s) arrived at the selected Police station. Approve or decline prison transfer.";
            }
            if (now >= _phaseStartedAt.AddSeconds(PhysicalPhaseTimeoutSeconds))
            {
                if (!HasRecoverablePlayerStationCustody())
                    return Fail("Station handoff did not complete physically before the custody timeout.", true);

                LogRuntime("POLICE_CONVOY_STATION_HANDOFF_STALLED",
                    "Vehicle=" + _transport.Handle
                    + "; LivePrisoners=" + ValidPrisoners().Count(prisoner => !prisoner.IsDead)
                    + "; StandbyOfficers=" + _stationHandoffOfficers.Count(ped => ped != null && ped.Exists() && !ped.IsDead)
                    + "; PhysicalPhaseTimeout=" + PhysicalPhaseTimeoutSeconds
                    + "; CustodyRemainsActive=true; TerminalFailure=false; RetrySameOperation=true");
                if (!_stationHandoffStallMessageShown)
                {
                    _stationHandoffStallMessageShown = true;
                    Notify("~b~POLICE CUSTODY~s~\nThe station handoff is taking longer than expected. Your living prisoner remains secured; the standby officer continues the physical unload and receiving escort.");
                }
                _phaseStartedAt = now;
            }
            return string.Empty;
        }

        private bool HasRecoverablePlayerStationCustody()
        {
            if (!_playerOwnedTransport || _transport == null || !_transport.Exists()
                || _transport.IsDead)
                return false;
            List<Ped> prisoners = ValidPrisoners().ToList();
            return prisoners.Count > 0
                && prisoners.All(prisoner => prisoner != null
                    && prisoner.Exists() && !prisoner.IsDead && prisoner.Health > 0);
        }

        private void RefreshStationHandoffProgress(DateTime now)
        {
            List<string> evidence = new List<string>();
            if (_transport != null && _transport.Exists())
            {
                evidence.Add("V:" + _transport.Handle
                    + ":" + Math.Round(_transport.Position.X).ToString(CultureInfo.InvariantCulture)
                    + "," + Math.Round(_transport.Position.Y).ToString(CultureInfo.InvariantCulture)
                    + "," + Math.Round(_transport.Position.Z).ToString(CultureInfo.InvariantCulture)
                    + ":" + (int)Math.Round(_transport.Heading / 15.0f));
            }
            foreach (Ped prisoner in ValidPrisoners().OrderBy(ped => ped.Handle))
            {
                int door = PrisonerDoorIndex(PrisonerSeat(
                    prisoner, ValidPrisoners().ToList()));
                evidence.Add("P:" + prisoner.Handle
                    + ":" + IsInTransport(prisoner)
                    + ":" + IsPrisonerDoorPhysicallyOpen(door)
                    + ":" + _playerUnloadingTasksIssued.Contains(prisoner.Handle)
                    + ":" + _prisonerUnloadingEscortStartedAt.ContainsKey(prisoner.Handle)
                    + ":" + Math.Round(prisoner.Position.X).ToString(CultureInfo.InvariantCulture)
                    + "," + Math.Round(prisoner.Position.Y).ToString(CultureInfo.InvariantCulture)
                    + "," + Math.Round(prisoner.Position.Z).ToString(CultureInfo.InvariantCulture));
            }
            foreach (Ped officer in _stationHandoffOfficers
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                .OrderBy(ped => ped.Handle))
            {
                evidence.Add("O:" + officer.Handle
                    + ":" + Math.Round(officer.Position.X).ToString(CultureInfo.InvariantCulture)
                    + "," + Math.Round(officer.Position.Y).ToString(CultureInfo.InvariantCulture)
                    + "," + Math.Round(officer.Position.Z).ToString(CultureInfo.InvariantCulture));
            }

            string snapshot = string.Join(";", evidence.ToArray());
            if (string.Equals(snapshot, _stationHandoffProgressEvidence,
                StringComparison.Ordinal))
                return;

            bool hadPriorEvidence = !string.IsNullOrWhiteSpace(
                _stationHandoffProgressEvidence);
            _stationHandoffProgressEvidence = snapshot;
            _phaseStartedAt = now;
            _stationHandoffStallMessageShown = false;
            if (hadPriorEvidence)
                LogRuntime("POLICE_CONVOY_STATION_HANDOFF_PROGRESS",
                    "Vehicle=" + (_transport == null || !_transport.Exists()
                        ? 0 : _transport.Handle)
                    + "; Snapshot=" + snapshot
                    + "; ProgressRefresh=true; CustodyRemainsActive=true");
        }

        private string ProcessPlayerArrivalAtPrison(DateTime now)
        {
            if (!PlayerDrivesTransport())
                return string.Empty;
            string routeThreatMessage = ProcessRequestedRouteThreat(now);
            if (!string.IsNullOrWhiteSpace(routeThreatMessage))
                return routeThreatMessage;
            if (_transport.Position.DistanceTo(_prisonDestination) > _settings.PrisonArrivalRadius)
                return string.Empty;
            SetPhase(CustodyPhase.PrisonHandoff);
            _playerUnloadingOfferNotified = true;
            LogRuntime("POLICE_CONVOY_PLAYER_UNLOAD_AVAILABLE",
                "Stage=PRISON; E=OpenRearDoorThenUnload; StandbyOfficer=CompletesBooking");
            if (!EnsurePrisonHandoffOfficers())
            {
                Notify("~b~PRISON ARRIVAL~s~\nExit the transport; at the rear door press E to open it, then E again to unload. The receiving officer is preparing.");
                return "Prison reached. The receiving officer is preparing the physical handoff.";
            }
            OrderPrisonHandoffGuards();
            Notify("~b~PRISON ARRIVAL~s~\nExit the transport; at the rear door press E to open it, then E again to unload. Prison staff complete the handoff.");
            return "Prison reached. Prisoner handoff and booking is in progress.";
        }

        /// <summary>
        /// A Convoy Request can stage one intentional road threat after the
        /// transport has genuinely left the station. The entity is created a
        /// configurable distance ahead on a street, never on the player,
        /// transport, or prison entrance. It is an owned challenge, not a
        /// second Dispatch incident.
        /// </summary>
        private string ProcessRequestedRouteThreat(DateTime now)
        {
            if (!_isRequestedConvoyActivity || !_settings.RequestedConvoyRouteThreatEnabled
                || string.IsNullOrWhiteSpace(_routeThreatModelName))
            {
                if (_routeThreatState == RouteThreatState.None)
                    _routeThreatState = RouteThreatState.Skipped;
                return string.Empty;
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || !HasTransportVehicle())
                return string.Empty;
            if (player.Position.DistanceTo(_stationDestination)
                < Math.Max(35f, _settings.MinimumStagingDistance * 0.65f))
                return string.Empty;

            if (_routeThreatState == RouteThreatState.None)
            {
                _routeThreatState = RouteThreatState.PreparingAssets;
                _routeThreatPreparationDeadline = now.AddSeconds(ModelPreparationTimeoutSeconds);
                RequestRouteThreatModels();
                return string.Empty;
            }

            if (_routeThreatState == RouteThreatState.PreparingAssets)
            {
                Model vehicleModel = new Model(_routeThreatVehicleModelName);
                Model pedModel = new Model(_routeThreatModelName);
                bool releaseModels = false;
                try
                {
                    if (!IsUsableModel(vehicleModel, true) || !IsUsableModel(pedModel, false))
                    {
                        releaseModels = true;
                        _routeThreatState = RouteThreatState.Skipped;
                        LogStateFailure("POLICE_CONVOY_ROUTE_THREAT_SKIPPED", "The configured route threat model is unavailable.");
                        return string.Empty;
                    }
                    if (!vehicleModel.IsLoaded || !pedModel.IsLoaded)
                    {
                        if (!vehicleModel.IsLoaded) vehicleModel.Request();
                        if (!pedModel.IsLoaded) pedModel.Request();
                        if (now >= _routeThreatPreparationDeadline)
                        {
                            releaseModels = true;
                            _routeThreatState = RouteThreatState.Skipped;
                            LogStateFailure("POLICE_CONVOY_ROUTE_THREAT_SKIPPED", "Route-threat assets did not finish loading before the safe timeout.");
                        }
                        return string.Empty;
                    }

                    releaseModels = true;
                    if (!CreateRouteThreat(vehicleModel, pedModel, player))
                    {
                        _routeThreatState = RouteThreatState.Skipped;
                        LogStateFailure("POLICE_CONVOY_ROUTE_THREAT_SKIPPED", "No safe road staging point was available for the Convoy route threat.");
                        return string.Empty;
                    }
                    _routeThreatState = RouteThreatState.Staged;
                    _lastRouteThreatCombatTaskAt = DateTime.MinValue;
                    Notify("~r~CONVOY ROUTE THREAT~s~\nHostile road units are blocking the transport route ahead.");
                    LogRuntime("POLICE_CONVOY_ROUTE_THREAT_STAGED",
                        "Peds=" + string.Join(",", _routeThreatPeds.Select(ped => ped.Handle.ToString()).ToArray())
                        + "; Vehicle=" + (_routeThreatVehicle == null ? "none" : _routeThreatVehicle.Handle.ToString()));
                    return "Convoy route threat is staged ahead. Clear it or pass the roadblock to continue the prison transfer.";
                }
                catch (Exception ex)
                {
                    releaseModels = true;
                    _routeThreatState = RouteThreatState.Skipped;
                    LogException("POLICE_CONVOY_ROUTE_THREAT_STAGE_FAILED", ex);
                    return string.Empty;
                }
                finally
                {
                    if (releaseModels)
                    {
                        ReleaseModel(vehicleModel);
                        ReleaseModel(pedModel);
                    }
                }
            }

            if (_routeThreatState != RouteThreatState.Staged)
                return string.Empty;

            List<Ped> alive = ActiveRouteThreats.ToList();
            if (alive.Count == 0)
            {
                _routeThreatState = RouteThreatState.Resolved;
                Notify("~g~CONVOY ROUTE CLEAR~s~\nThe hostile road units no longer block the transport.");
                LogRuntime("POLICE_CONVOY_ROUTE_THREAT_RESOLVED", "All owned route-threat peds are gone.");
                return "Convoy route threat cleared. Continue to the prison.";
            }

            float closest = alive.Min(ped => ped.Position.DistanceTo(player.Position));
            if (closest <= Math.Max(45f, _settings.MinimumStagingDistance)
                && now >= _lastRouteThreatCombatTaskAt.AddSeconds(8))
            {
                foreach (Ped threat in alive)
                {
                    try { threat.Task.Combat(player); }
                    catch (Exception ex) { LogException("POLICE_CONVOY_ROUTE_THREAT_COMBAT_FAILED", ex); }
                }
                _lastRouteThreatCombatTaskAt = now;
            }
            return string.Empty;
        }

        private void RequestRouteThreatModels()
        {
            try
            {
                Model vehicle = new Model(_routeThreatVehicleModelName);
                Model ped = new Model(_routeThreatModelName);
                if (IsUsableModel(vehicle, true) && !vehicle.IsLoaded) vehicle.Request();
                if (IsUsableModel(ped, false) && !ped.IsLoaded) ped.Request();
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_ROUTE_THREAT_MODEL_REQUEST_FAILED", ex);
            }
        }

        private bool CreateRouteThreat(Model vehicleModel, Model pedModel, Ped player)
        {
            Vector3 staging = FindRouteThreatStagingPosition(player);
            if (staging.DistanceTo(player.Position) < Math.Max(55f, _settings.MinimumStagingDistance * 0.80f))
                return false;
            Vehicle vehicle = World.CreateVehicle(
                vehicleModel,
                staging,
                HeadingToward(staging, player.Position, player.Heading));
            if (vehicle == null || !vehicle.Exists())
                return false;
            vehicle.IsPersistent = true;
            try
            {
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, vehicle, true, true);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_ROUTE_THREAT_VEHICLE_MISSION_OWNERSHIP_FAILED", ex);
            }
            vehicle.PlaceOnGround();
            _routeThreatVehicle = vehicle;
            try
            {
                for (int index = 0; index < _settings.RequestedRouteThreatCount; index++)
                {
                    float lateral = index % 2 == 0 ? 2.5f + index : -2.5f - index;
                    Vector3 spawn = vehicle.GetOffsetPosition(new Vector3(lateral, 1.5f + index, 0f));
                    Ped threat = World.CreatePed(pedModel, spawn);
                    if (threat == null || !threat.Exists())
                        continue;
                    threat.IsPersistent = true;
                    threat.BlockPermanentEvents = true;
                    try
                    {
                        Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, threat, true, true);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_ROUTE_THREAT_PED_MISSION_OWNERSHIP_FAILED", ex);
                    }
                    GiveRouteThreatWeapon(threat);
                    _routeThreatPeds.Add(threat);
                }
                if (_routeThreatPeds.Count > 0)
                    return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_ROUTE_THREAT_CREATE_FAILED", ex);
            }

            foreach (Ped threat in _routeThreatPeds.ToArray())
                try { if (threat != null && threat.Exists()) threat.Delete(); } catch { }
            _routeThreatPeds.Clear();
            try { if (vehicle.Exists()) vehicle.Delete(); } catch { }
            _routeThreatVehicle = null;
            return false;
        }

        private Vector3 FindRouteThreatStagingPosition(Ped player)
        {
            Vector3 candidate = player.GetOffsetPosition(new Vector3(
                0f,
                _settings.RequestedRouteThreatDistance,
                0f));
            try
            {
                Vector3 street = World.GetNextPositionOnStreet(candidate);
                if (street.DistanceTo(player.Position) >= Math.Max(55f, _settings.MinimumStagingDistance * 0.80f))
                    return street;
            }
            catch { }
            return candidate;
        }

        private void GiveRouteThreatWeapon(Ped threat)
        {
            if (threat == null || !threat.Exists() || string.IsNullOrWhiteSpace(_routeThreatWeaponName))
                return;
            try
            {
                int hash = unchecked((int)StringHash.AtStringHash(_routeThreatWeaponName, 0));
                Function.Call(Hash.GIVE_WEAPON_TO_PED, threat, hash, 120, false, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, threat, hash, true);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_ROUTE_THREAT_WEAPON_FAILED", ex);
            }
        }

        private string ProcessPrisonHandoff(DateTime now)
        {
            if (!EnsurePrisonHandoffOfficers())
            {
                if (now >= _phaseStartedAt.AddSeconds(ModelPreparationTimeoutSeconds))
                    return Fail("Prison handoff officers could not be prepared safely while the prisoner remained alive.", true);
                return string.Empty;
            }
            OrderPrisonHandoffGuards();

            foreach (Ped prisoner in ValidPrisoners())
            {
                if (ShouldOfferPlayerUnloading(prisoner, now))
                {
                    if (!_playerUnloadingOfferNotified)
                    {
                        _playerUnloadingOfferNotified = true;
                        LogRuntime("POLICE_CONVOY_PLAYER_UNLOAD_AVAILABLE",
                            "Stage=PRISON; Prisoner=" + prisoner.Handle
                            + "; E=OpenRearDoorThenUnload; StandbyOfficer=CompletesBooking");
                        Notify("~b~PRISON ARRIVAL~s~\nAt the rear door press E to open it, then E again to unload. Prison staff will finish the handoff if you do not take over.");
                    }
                    continue;
                }
                if (MaintainPlayerUnloading(prisoner, now, "PRISON"))
                    continue;
                MaintainPrisonerUnloadingEscort(
                    prisoner,
                    _prisonHandoffOfficers,
                    PrisonHandoffPosition(prisoner),
                    now,
                    "PRISON");
            }
            List<Ped> prisonPrisoners = ValidPrisoners().ToList();
            if (prisonPrisoners.Count > 0 && prisonPrisoners.All(prisoner =>
                !IsInTransport(prisoner)
                && _prisonerUnloadingEscortStartedAt.ContainsKey(prisoner.Handle)
                && IsNearHandoffOfficer(prisoner, _prisonHandoffOfficers)
                && prisoner.Position.DistanceTo(PrisonHandoffPosition(prisoner)) <= 4.0f))
            {
                LogRuntime("POLICE_CONVOY_PRISON_HANDOFF_CONFIRMED",
                    "Prisoners=" + string.Join(",", prisonPrisoners.Select(ped => ped.Handle.ToString()).ToArray())
                    + "; Officers=" + string.Join(",", _prisonHandoffOfficers
                        .Where(ped => ped != null && ped.Exists())
                        .Select(ped => ped.Handle.ToString()).ToArray()));
                ReportTerminal("lsimmersivelife.police.transport.completed", "completed");
                Complete("Prison arrival confirmed. Prisoner handoff and booking completed.", true);
                return "Prisoner delivered and booking completed.";
            }
            if (now >= _phaseStartedAt.AddSeconds(PhysicalPhaseTimeoutSeconds))
                return Fail("Prison handoff did not complete physically before the custody timeout; living prisoner custody is being returned for another transport request.", true);
            return string.Empty;
        }

        private bool EnsurePrisonHandoffOfficers()
        {
            int required = Math.Min(2, Math.Max(1, PrisonerCount));
            _prisonHandoffOfficers.RemoveAll(ped =>
                ped == null || !ped.Exists() || ped.IsDead);
            if (_prisonHandoffOfficers.Count >= required)
                return true;

            string modelName = PrisonOfficerModelName();
            Model model = new Model(modelName);
            bool releaseModel = false;
            try
            {
                if (!IsUsableModel(model, false))
                {
                    releaseModel = true;
                    LogRuntime(
                        "POLICE_CONVOY_PRISON_HANDOFF_MODEL_REJECTED",
                        "Model=" + modelName + "; Required=" + required
                        + "; Destination=" + _prisonDestination);
                    return false;
                }
                if (!model.IsLoaded)
                {
                    model.Request();
                    return false;
                }
                releaseModel = true;
                int existing = _prisonHandoffOfficers.Count;
                for (int index = existing; index < required; index++)
                {
                    Vector3 spawn = FindPrisonHandoffOfficerPosition(index);
                    Ped officer = null;
                    try { officer = World.CreatePed(model, spawn); }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_PRISON_HANDOFF_OFFICER_CREATE_FAILED", ex);
                    }
                    if (officer == null || !officer.Exists())
                    {
                        LogRuntime(
                            "POLICE_CONVOY_PRISON_HANDOFF_OFFICER_UNAVAILABLE",
                            "Model=" + modelName + "; Index=" + index
                            + "; Spawn=" + spawn + "; Destination=" + _prisonDestination);
                        continue;
                    }
                    PrepareOfficer(officer);
                    try
                    {
                        officer.Heading = HeadingToward(
                            officer.Position,
                            _transport != null && _transport.Exists()
                                ? _transport.Position : _prisonDestination,
                            officer.Heading);
                        if (_transport != null && _transport.Exists())
                            officer.Task.LookAt(_transport, 4000);
                    }
                    catch { }
                    _prisonHandoffOfficers.Add(officer);
                    LogRuntime(
                        "POLICE_CONVOY_PRISON_HANDOFF_OFFICER_STAGED",
                        "Officer=" + officer.Handle + "; Model=" + modelName
                        + "; Spawn=" + spawn + "; Required=" + required
                        + "; Destination=" + _prisonDestination);
                }
                bool ready = _prisonHandoffOfficers.Count > 0;
                if (!ready)
                    LogRuntime(
                        "POLICE_CONVOY_PRISON_HANDOFF_OFFICER_UNAVAILABLE",
                        "Model=" + modelName + "; Required=" + required
                        + "; Created=" + _prisonHandoffOfficers.Count
                        + "; Destination=" + _prisonDestination);
                else if (_prisonHandoffOfficers.Count < required)
                    LogRuntime(
                        "POLICE_CONVOY_PRISON_HANDOFF_OFFICER_PARTIAL",
                        "Model=" + modelName + "; Required=" + required
                        + "; Created=" + _prisonHandoffOfficers.Count
                        + "; Handoff=serialized-per-prisoner");
                return ready;
            }
            catch (Exception ex)
            {
                releaseModel = true;
                LogException("POLICE_CONVOY_PRISON_HANDOFF_OFFICER_FAILED", ex);
                return false;
            }
            finally
            {
                if (releaseModel)
                    ReleaseModel(model);
            }
        }

        private Vector3 FindPrisonHandoffOfficerPosition(int index)
        {
            Vector3 candidate;
            if (_transport != null && _transport.Exists())
            {
                float side = index % 2 == 0 ? -3.4f : 3.4f;
                // Spawn beside the live transport at the actual arrival point.
                // A generic street snap at Bolingbroke can move a receiving
                // officer away from the prison gate or onto another road level.
                candidate = _transport.GetOffsetPosition(new Vector3(side, -0.5f, 0f));
            }
            else
            {
                candidate = _prisonDestination
                    + new Vector3(index % 2 == 0 ? -3.4f : 3.4f, 1.5f, 0f);
            }

            try
            {
                Vector3 street = World.GetNextPositionOnStreet(candidate);
                if (street != Vector3.Zero
                    && street.DistanceTo(candidate) <= 12f
                    && Math.Abs(street.Z - candidate.Z) <= MaximumRoadElevationDifference)
                    return street;
            }
            catch { }
            return candidate;
        }

        private bool EnsureStationHandoffOfficers()
        {
            _stationHandoffOfficers.RemoveAll(ped => ped == null || !ped.Exists());
            int requiredCount = Math.Min(2, Math.Max(1, PrisonerCount));
            if (_stationHandoffOfficers.Count >= requiredCount)
                return true;
            if (_stationHandoffMarkerPosition == Vector3.Zero
                && _stationHandoffGroundPosition == Vector3.Zero)
                return false;
            Model model = new Model(OfficerModelName());
            bool releaseModel = false;
            try
            {
                if (!IsUsableModel(model, false))
                {
                    releaseModel = true;
                    return false;
                }
                if (!model.IsLoaded)
                {
                    model.Request();
                    return false;
                }
                releaseModel = true;
                for (int index = _stationHandoffOfficers.Count; index < requiredCount; index++)
                {
                    Vector3 spawn;
                    if (!TryResolveStationHandoffOfficerPosition(index, out spawn))
                        continue;
                    Ped officer = World.CreatePed(model, spawn);
                    if (officer == null || !officer.Exists())
                        continue;
                    officer.Position = spawn;
                    PrepareOfficer(officer);
                    try { officer.Task.LookAt(_transport, 4000); } catch { }
                    _stationHandoffOfficers.Add(officer);
                }
                return _stationHandoffOfficers.Count >= requiredCount;
            }
            catch (Exception ex)
            {
                releaseModel = true;
                LogException("POLICE_CONVOY_STATION_HANDOFF_OFFICER_FAILED", ex);
                return false;
            }
            finally
            {
                if (releaseModel)
                    ReleaseModel(model);
            }
        }

        private void OrderStationHandoffGuards()
        {
            if (_stationHandoffArrivalOrdersIssued || _transport == null || !_transport.Exists())
                return;
            foreach (Ped officer in _stationHandoffOfficers.Where(ped => ped != null && ped.Exists()))
            {
                try { officer.Task.LookAt(_transport, 5000); }
                catch (Exception ex) { LogException("POLICE_CONVOY_STATION_GUARD_TASK_FAILED", ex); }
            }
            _stationHandoffArrivalOrdersIssued = _stationHandoffOfficers.Count > 0;
            if (_stationHandoffArrivalOrdersIssued)
                LogRuntime("POLICE_CONVOY_STATION_STANDBY_READY",
                    "Station=" + _stationDisplayName
                    + "; StationId=" + (_profile == null ? string.Empty : _profile.Selection.StationId)
                    + "; ReceivingMarker=" + _stationHandoffMarkerPosition
                    + "; Officers=" + string.Join(",", _stationHandoffOfficers
                        .Where(ped => ped != null && ped.Exists())
                        .Select(ped => ped.Handle.ToString()).ToArray())
                    + "; VehiclePoseValidated=" + _stationHandoffActualPoseValidated
                    + "; CustodyRetained=true");
        }

        private void OrderPrisonHandoffGuards()
        {
            if (_prisonHandoffArrivalOrdersIssued || _transport == null || !_transport.Exists())
                return;
            foreach (Ped officer in _prisonHandoffOfficers.Where(ped => ped != null && ped.Exists()))
            {
                try { officer.Task.LookAt(_transport, 5000); }
                catch (Exception ex) { LogException("POLICE_CONVOY_PRISON_GUARD_TASK_FAILED", ex); }
            }
            _prisonHandoffArrivalOrdersIssued = _prisonHandoffOfficers.Count > 0;
        }

        internal bool AdoptDispatchCustodyInjuryStates(
            IEnumerable<LSPDSuspectInjuryState> injuryStates)
        {
            if (!Active || !_dispatchCustodyOperation || injuryStates == null)
                return false;

            Dictionary<int, LSPDSuspectInjuryState> offered = injuryStates
                .Where(state => state != null && state.Actor != null
                    && state.Actor.Exists() && !state.Actor.IsDead
                    && state.Actor.Health > 0 && !state.Fatal)
                .GroupBy(state => state.Actor.Handle)
                .ToDictionary(group => group.Key, group => group.First());
            List<Ped> livingPrisoners = _prisoners.Where(prisoner => prisoner != null
                && prisoner.Exists() && !prisoner.IsDead && prisoner.Health > 0).ToList();
            if (livingPrisoners.Count == 0 || offered.Count == 0
                || offered.Keys.Any(handle => !livingPrisoners.Any(
                    prisoner => prisoner.Handle == handle)))
                return false;

            if (!_dispatchGroupPlayerDrivenPickup
                && (offered.Count != livingPrisoners.Count
                    || livingPrisoners.Any(prisoner => !offered.ContainsKey(
                        prisoner.Handle))))
                return false;

            if (!_dispatchGroupPlayerDrivenPickup)
                _prisonerInjuryStates.Clear();
            foreach (KeyValuePair<int, LSPDSuspectInjuryState> entry in offered)
            {
                if (_dispatchGroupPlayerDrivenPickup
                    && _prisonerInjuryStates.ContainsKey(entry.Key))
                    continue;
                _prisonerInjuryStates.Add(entry.Key, entry.Value);
            }
            return true;
        }

        internal void DiscardUncommittedDispatchInjuryHandoff()
        {
            _prisonerInjuryStates.Clear();
        }

        internal void ConfirmDispatchInjuryHandoff()
        {
            ConfirmDispatchInjuryHandoff(null);
        }

        internal void ConfirmDispatchInjuryHandoff(IEnumerable<int> handles)
        {
            HashSet<int> selectedHandles = handles == null
                ? null : new HashSet<int>(handles);
            foreach (Ped prisoner in _prisoners.ToArray())
            {
                if (prisoner == null || !prisoner.Exists())
                    continue;
                if (selectedHandles != null
                    && !selectedHandles.Contains(prisoner.Handle))
                    continue;
                LSPDSuspectInjuryState state;
                if (!_prisonerInjuryStates.TryGetValue(prisoner.Handle, out state)
                    || state == null)
                    continue;

                int healthBefore = prisoner.Health;
                int maximumBefore = prisoner.MaxHealth;
                try
                {
                    // Dispatch custody protection starts at physical arrest and
                    // follows this same injury state through the owner handoff.
                    // Independent Convoy prisoners retain their existing policy.
                    if (_dispatchCustodyOperation
                        && state.CustodyDamageProtectionActive)
                        LSPDDispatch.ApplyDispatchCustodyDamageProtection(
                            prisoner, state);
                    else
                        prisoner.CanSufferCriticalHits =
                            state.OriginalCanSufferCriticalHits;
                    if (_dispatchCustodyOperation)
                        Function.Call(Hash.SET_PED_CONFIG_FLAG,
                            prisoner, HandcuffedPedConfigFlag, true);
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, prisoner, true);
                    state.RuntimeOwner = "Convoy";
                    state.LastConvoyHealthCheckAt = DateTime.MinValue;
                    state.LastHealth = Math.Max(1, prisoner.Health);
                    LogRuntime("POLICE_CONVOY_INJURY_STATE_ADOPTED",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + prisoner.Handle
                        + "; From=Dispatch; To=Convoy"
                        + "; HealthBefore=" + healthBefore
                        + "; HealthAfter=" + prisoner.Health
                        + "; MaximumHealthBefore=" + maximumBefore
                        + "; MaximumHealthAfter=" + prisoner.MaxHealth
                        + "; Cuffed=" + Function.Call<bool>(Hash.IS_PED_CUFFED, prisoner)
                        + "; ConfigFlag120=true; DispatchCustodyProtection="
                        + (_dispatchCustodyOperation
                            && state.CustodyDamageProtectionActive)
                        + "; FullDamageProof=" + state.CustodyDamageProtectionActive
                        + "; CriticalHits=" + prisoner.CanSufferCriticalHits
                        + "; CumulativeDamage=" + state.DamageTaken
                        + "; LastDamageKind=" + (state.LastDamageKind ?? "Unknown"));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_INJURY_HANDOFF_MAINTENANCE_FAILED", ex);
                }
            }
        }

        internal void DiscardUncommittedDispatchInjuryHandoff(
            IEnumerable<int> handles)
        {
            if (handles == null)
                return;
            foreach (int handle in handles.Distinct())
            {
                LSPDSuspectInjuryState state;
                if (_prisonerInjuryStates.TryGetValue(handle, out state)
                    && state != null && string.Equals(state.RuntimeOwner,
                        "Dispatch", StringComparison.OrdinalIgnoreCase))
                    _prisonerInjuryStates.Remove(handle);
            }
        }

        private void ObserveConvoyInjuryState(Ped prisoner, DateTime now, bool force = false)
        {
            if (prisoner == null || !prisoner.Exists())
                return;
            LSPDSuspectInjuryState state;
            if (!_prisonerInjuryStates.TryGetValue(prisoner.Handle, out state)
                || state == null)
                return;
            if (!force && now < state.LastConvoyHealthCheckAt
                .AddMilliseconds(ActiveCustodyMaintenanceRefreshMilliseconds))
                return;
            state.LastConvoyHealthCheckAt = now;

            int currentHealth = Math.Max(0, prisoner.Health);
            if (currentHealth < state.LastHealth)
            {
                int previousHealth = state.LastHealth;
                int damage = previousHealth - currentHealth;
                state.DamageTaken += damage;
                state.LastDamageKind = "UnknownDuringConvoy";
                try
                {
                    using (OutputArgument output = new OutputArgument())
                    {
                        if (Function.Call<bool>(Hash.GET_PED_LAST_DAMAGE_BONE,
                            prisoner, output))
                        {
                            int bone = output.GetResult<int>();
                            if (bone != 0)
                            {
                                state.LastValidDamageBone = bone;
                                state.LastValidDamageBoneAt = now;
                            }
                        }
                    }
                }
                catch { }
                LogRuntime("POLICE_CONVOY_PRISONER_HEALTH_DELTA",
                    "Incident=" + state.OccurrenceId
                    + "; RuntimeOwner=Convoy; Ped=" + prisoner.Handle
                    + "; HealthBefore=" + previousHealth
                    + "; HealthAfter=" + currentHealth
                    + "; DamageDelta=" + damage
                    + "; LastValidBone=" + state.LastValidDamageBone
                    + "; DamageKind=UnknownDuringConvoy"
                    + "; Cuffed=" + Function.Call<bool>(Hash.IS_PED_CUFFED, prisoner)
                    + "; ConfirmedHitCountChanged=false"
                    + "; CumulativeDamage=" + state.DamageTaken);

                if (_dispatchCustodyOperation && state.Wounded
                    && !prisoner.IsDead)
                {
                    int protectedHealth = LSPDDispatch
                        .GetDispatchProtectedHealthAfterDamage(
                            previousHealth, currentHealth,
                            Math.Max(1, prisoner.MaxHealth),
                            state.DispatchWoundedHealthReserve,
                            false, false, false);
                    if (protectedHealth > currentHealth)
                    {
                        try
                        {
                            int observedHealthAfter = currentHealth;
                            prisoner.Health = protectedHealth;
                            currentHealth = Math.Max(1, prisoner.Health);
                            state.WoundedHealthCorrections++;
                            state.LastHealth = currentHealth;
                            state.LastMaximumHealth = Math.Max(1, prisoner.MaxHealth);
                            LogRuntime("POLICE_CONVOY_DISPATCH_WOUNDED_HEALTH_CORRECTED",
                                "Incident=" + state.OccurrenceId
                                + "; RuntimeOwner=Convoy; Ped=" + prisoner.Handle
                                + "; Health=" + previousHealth + "->" + observedHealthAfter
                                + "; RestoredTo=" + currentHealth
                                + "; MaximumHealth=" + prisoner.MaxHealth
                                + "; Cuffed=" + Function.Call<bool>(Hash.IS_PED_CUFFED, prisoner)
                                + "; ConfirmedHitCountChanged=false"
                                + "; Corrections=" + state.WoundedHealthCorrections);
                            return;
                        }
                        catch (Exception ex)
                        {
                            LogException(
                                "POLICE_CONVOY_DISPATCH_WOUNDED_HEALTH_CORRECTION_FAILED",
                                ex);
                        }
                    }
                }
            }
            state.LastHealth = currentHealth;

            if ((prisoner.IsDead || currentHealth <= 0) && !state.ConvoyTerminalLogged)
            {
                state.Fatal = true;
                state.ConvoyTerminalLogged = true;
                LogRuntime("POLICE_CONVOY_PRISONER_TERMINAL_INJURY",
                    "Incident=" + state.OccurrenceId
                    + "; RuntimeOwner=Convoy; Ped=" + prisoner.Handle
                    + "; Health=" + currentHealth
                    + "; MaximumHealth=" + prisoner.MaxHealth
                    + "; Cuffed=" + Function.Call<bool>(Hash.IS_PED_CUFFED, prisoner)
                    + "; ConfigFlag120=true; CollisionProof=true"
                    + "; BulletFireExplosionMeleeProof=false"
                    + "; LastDamageKind=" + (state.LastDamageKind ?? "Unknown")
                    + "; Cause=EngineTerminal; Revived=false");
            }
        }

        private void RestoreConvoyInjuryState(Ped prisoner, string reason)
        {
            if (prisoner == null || !prisoner.Exists())
                return;
            LSPDSuspectInjuryState state;
            if (!_prisonerInjuryStates.TryGetValue(prisoner.Handle, out state)
                || state == null)
                return;

            ObserveConvoyInjuryState(prisoner, DateTime.UtcNow, true);
            int healthBefore = Math.Max(0, prisoner.Health);
            int maximumBefore = Math.Max(1, prisoner.MaxHealth);
            if (!state.Fatal && !prisoner.IsDead && prisoner.Health > 0)
            {
                try
                {
                    // The custody owner observes injury; it does not heal a
                    // living prisoner or rewrite the Ped's normal health cap
                    // when ownership ends.
                    LSPDDispatch.RestoreDispatchCustodyDamageProtection(
                        prisoner, state);
                    prisoner.CanSufferCriticalHits = state.OriginalCanSufferCriticalHits;
                    state.RuntimeOwner = "Released";
                    LogRuntime("POLICE_CONVOY_INJURY_STATE_RESTORED",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + prisoner.Handle
                        + "; Reason=" + (reason ?? string.Empty)
                        + "; HealthBefore=" + healthBefore
                        + "; HealthAfter=" + prisoner.Health
                        + "; MaximumHealthBefore=" + maximumBefore
                        + "; MaximumHealthAfter=" + prisoner.MaxHealth
                        + "; CumulativeDamage=" + state.DamageTaken
                        + "; Cuffed=" + Function.Call<bool>(Hash.IS_PED_CUFFED, prisoner));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_INJURY_STATE_RESTORE_FAILED", ex);
                }
            }
            _prisonerInjuryStates.Remove(prisoner.Handle);
        }

        private void RestoreAllConvoyInjuryStates(string reason)
        {
            foreach (LSPDSuspectInjuryState state in _prisonerInjuryStates.Values.ToArray())
                if (state != null)
                    RestoreConvoyInjuryState(state.Actor, reason);
            _prisonerInjuryStates.Clear();
        }

        private void PreparePrisoner(Ped prisoner, bool assignVisibleSurrenderTask = true)
        {
            if (prisoner == null || !prisoner.Exists())
                return;
            try
            {
                prisoner.IsPersistent = true;
                prisoner.BlockPermanentEvents = true;
                prisoner.CanSwitchWeapons = false;
                // IsPersistent protects the ordinary SHVDN lifetime flag, but
                // custody also needs explicit mission ownership while the
                // player is away driving the transport or travelling to the
                // prison.  Without this boundary GTA could stream the staged
                // prisoner out and Convoy would later report zero prisoners.
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, prisoner, true, true);
                Function.Call(Hash.SET_ENTITY_VISIBLE, prisoner, true, false);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, prisoner, true);
                if (_dispatchCustodyOperation)
                    Function.Call(Hash.SET_PED_CONFIG_FLAG,
                        prisoner, HandcuffedPedConfigFlag, true);
                // Independent Convoy prisoners keep the established collision
                // guard. A Dispatch prisoner carries the stronger temporary
                // protection that began when physical arrest took ownership.
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    prisoner, false, false, false, true, false, false, false, false);
                MaintainDispatchCustodyDamageProtection(prisoner);
                LogRuntime("POLICE_CONVOY_PRISONER_COLLISION_GUARD",
                    "Prisoner=" + prisoner.Handle
                    + "; CollisionProof=true; DispatchCustodyDamageProtection="
                    + (_dispatchCustodyOperation
                        && HasDispatchCustodyDamageProtection(prisoner)));
                if (assignVisibleSurrenderTask && !prisoner.IsInVehicle())
                {
                    prisoner.Task.ClearAll();
                    prisoner.Task.HandsUp(2000);
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PRISONER_PREPARE_FAILED", ex);
            }
        }

        private bool MaintainPrisonerAvailability(
            DateTime now, out bool reconciliationPending)
        {
            reconciliationPending = false;
            bool refreshCustodyFlags = now >= _lastPrisonerMaintenanceAt
                .AddMilliseconds(PrisonerMaintenanceRefreshMilliseconds);
            if (refreshCustodyFlags)
                _lastPrisonerMaintenanceAt = now;
            for (int index = 0; index < _prisoners.Count; index++)
            {
                Ped prisoner = _prisoners[index];
                if (prisoner == null || !prisoner.Exists())
                {
                    // Only Dispatch-owned custody uses this recovery window.
                    // Standalone Convoy retains its existing loss behavior.
                    if (!_dispatchCustodyOperation || prisoner == null)
                        return false;

                    int handle = prisoner.Handle;
                    DateTime missingSince;
                    if (!_dispatchPrisonerMissingSince.TryGetValue(handle, out missingSince))
                    {
                        missingSince = now;
                        _dispatchPrisonerMissingSince[handle] = now;
                        LogRuntime(
                            "POLICE_CONVOY_DISPATCH_PRISONER_HANDLE_RECONCILIATION_STARTED",
                            "Prisoner=" + handle
                            + "; MissingSince=" + now.ToString("O", CultureInfo.InvariantCulture)
                            + "; ConfirmationMs="
                            + DispatchPrisonerMissingConfirmationMilliseconds
                            + "; OwnershipRetained=true");
                    }

                    DateTime lastRetryAt;
                    bool retryDue = !_dispatchPrisonerLastHandleRetryAt.TryGetValue(
                            handle, out lastRetryAt)
                        || now >= lastRetryAt.AddMilliseconds(
                            DispatchPrisonerHandleRetryMilliseconds);
                    if (retryDue)
                    {
                        _dispatchPrisonerLastHandleRetryAt[handle] = now;
                        try
                        {
                            Ped resolved = Entity.FromHandle(handle) as Ped;
                            if (resolved != null && resolved.Exists()
                                && resolved.Handle == handle)
                            {
                                _prisoners[index] = resolved;
                                prisoner = resolved;
                                _dispatchPrisonerMissingSince.Remove(handle);
                                _dispatchPrisonerLastHandleRetryAt.Remove(handle);
                                LogRuntime(
                                    "POLICE_CONVOY_DISPATCH_PRISONER_HANDLE_RECONCILED",
                                    "Prisoner=" + handle
                                    + "; ReacquiredSameHandle=true"
                                    + "; ElapsedMs=" + Math.Max(0,
                                        (now - missingSince).TotalMilliseconds).ToString("0")
                                    + "; OwnershipRetained=true");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogException(
                                "POLICE_CONVOY_DISPATCH_PRISONER_HANDLE_RECONCILIATION_FAILED",
                                ex);
                        }
                    }

                    if (prisoner == null || !prisoner.Exists())
                    {
                        if (now < missingSince.AddMilliseconds(
                            DispatchPrisonerMissingConfirmationMilliseconds))
                        {
                            reconciliationPending = true;
                            continue;
                        }

                        LogRuntime(
                            "POLICE_CONVOY_DISPATCH_PRISONER_HANDLE_RECONCILIATION_EXPIRED",
                            "Prisoner=" + handle
                            + "; ElapsedMs=" + Math.Max(0,
                                (now - missingSince).TotalMilliseconds).ToString("0")
                            + "; ConfirmationMs="
                            + DispatchPrisonerMissingConfirmationMilliseconds
                            + "; OwnershipWasRetainedUntilExpiry=true");
                        return false;
                    }
                }

                _dispatchPrisonerMissingSince.Remove(prisoner.Handle);
                _dispatchPrisonerLastHandleRetryAt.Remove(prisoner.Handle);
                if (prisoner.IsDead || prisoner.Health <= 0)
                {
                    ObserveConvoyInjuryState(prisoner, now, true);
                    return false;
                }
                if (!refreshCustodyFlags)
                    continue;
                try
                {
                    prisoner.IsPersistent = true;
                    prisoner.BlockPermanentEvents = true;
                    prisoner.CanSwitchWeapons = false;
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, prisoner, true, true);
                    Function.Call(Hash.SET_ENTITY_VISIBLE, prisoner, true, false);
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, prisoner, true);
                    if (_dispatchCustodyOperation)
                        Function.Call(Hash.SET_PED_CONFIG_FLAG,
                            prisoner, HandcuffedPedConfigFlag, true);
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        prisoner, false, false, false, true, false, false, false, false);
                    MaintainDispatchCustodyDamageProtection(prisoner);
                    ObserveConvoyInjuryState(prisoner, now);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_MAINTENANCE_FAILED", ex);
                }
            }
            return _prisoners.Count > 0;
        }

        private IEnumerable<Ped> ValidPrisoners()
        {
            return _prisoners.Where(ped => ped != null && ped.Exists() && !ped.IsDead);
        }

        private string DescribePrisonerLoss()
        {
            if (_prisoners.Count == 0)
                return "PRISONER_REFERENCE_INVALID: no prisoner was owned by this Convoy phase.";
            Ped dead = _prisoners.FirstOrDefault(ped => ped != null && ped.Exists() && ped.IsDead);
            if (dead != null)
                return "PRISONER_DIED: Ped=" + dead.Handle + ".";
            Ped deleted = _prisoners.FirstOrDefault(ped => ped == null || !ped.Exists());
            if (deleted != null)
                return "PRISONER_ENTITY_DELETED: the owned prisoner entity is no longer valid.";
            return "PRISONER_REFERENCE_INVALID: no living owned prisoner remained.";
        }

        private bool HasUsableTransport()
        {
            return _transport != null && _transport.Exists()
                && _transportDriver != null && _transportDriver.Exists() && !_transportDriver.IsDead;
        }

        private bool HasTransportVehicle()
        {
            return _transport != null && _transport.Exists() && !_transport.IsDead;
        }

        private string DescribeTransportLoss()
        {
            if (_transport == null)
                return "TRANSPORT_REFERENCE_INVALID";
            if (!_transport.Exists())
                return "TRANSPORT_ENTITY_DELETED";
            if (_transport.IsDead)
                return "TRANSPORT_DESTROYED";
            if (_transportDriver == null)
                return "TRANSPORT_DRIVER_REFERENCE_INVALID";
            if (!_transportDriver.Exists())
                return "TRANSPORT_DRIVER_ENTITY_DELETED";
            if (_transportDriver.IsDead)
                return "TRANSPORT_DRIVER_DIED";
            return "TRANSPORT_UNAVAILABLE";
        }

        private bool RequiresLiveTransport()
        {
            switch (_phase)
            {
                case CustodyPhase.DispatchStationTransportDecision:
                case CustodyPhase.DispatchGroupPickupWaiting:
                case CustodyPhase.DispatchPickupDriverBoarding:
                case CustodyPhase.DispatchCrewDriveToPickup:
                case CustodyPhase.DispatchCrewDriverBoarding:
                case CustodyPhase.DispatchCrewDriveToStation:
                case CustodyPhase.DriveToStation:
                case CustodyPhase.StationUnloading:
                case CustodyPhase.DriveToPrison:
                case CustodyPhase.PrisonHandoff:
                    return true;
                default:
                    return false;
            }
        }

        private int PhysicalPhaseTimeoutSeconds
        {
            get
            {
                return _settings == null ? 45 : _settings.PrisonerRecoveryTimeoutSeconds;
            }
        }

        private int LoadingTimeoutSeconds(Ped prisoner)
        {
            // Once the player has taken physical custody, that suspect needs
            // time for handcuff, escort, door, and load interactions. Keep the
            // saved unattended wait for other suspects in the same group.
            bool playerIsEscortingThisSuspect = prisoner != null && prisoner.Exists()
                && _playerEscortPrisoners.Contains(prisoner.Handle);
            Ped assignedOfficer;
            bool officerIsEscortingThisSuspect = prisoner != null && prisoner.Exists()
                && _prisonerEscortOfficers.TryGetValue(prisoner.Handle, out assignedOfficer)
                && assignedOfficer != null && assignedOfficer.Exists()
                && !assignedOfficer.IsDead && !IsOfficerInTransport(assignedOfficer);
            int timeout = playerIsEscortingThisSuspect || officerIsEscortingThisSuspect
                || _transportNearCustodyFallback
                ? Math.Max(PhysicalPhaseTimeoutSeconds, PlayerEscortTimeoutSeconds)
                : PhysicalPhaseTimeoutSeconds;
            if (_dispatchCustodyOperation && !_isRequestedConvoyActivity
                && !_playerOwnedTransport
                && _dispatchSceneWalkInDistanceMeters > 0f)
            {
                // A remote roadside stop adds a walk-in and return escort. At
                // the 100 m scene-access limit this adds at most 200 seconds
                // per active suspect; the existing one-at-a-time queue stays
                // responsible for group loading.
                timeout += (int)Math.Ceiling(
                    Math.Min(_dispatchSceneWalkInDistanceMeters,
                        MaximumDispatchSceneVehicleDistance) * 2.0f);
            }
            return timeout;
        }

        private void StartPrisonerLoadingTimeout(Ped prisoner, DateTime now)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || IsInTransport(prisoner)
                || _prisonerLoadingTimeoutStartedAt.ContainsKey(prisoner.Handle))
                return;

            _prisonerLoadingTimeoutStartedAt[prisoner.Handle] = now;
            LogRuntime("POLICE_CONVOY_PRISONER_LOADING_TIMEOUT_STARTED",
                "Prisoner=" + prisoner.Handle
                + "; TimeoutSeconds=" + LoadingTimeoutSeconds(prisoner)
                + "; Phase=" + _phase);
        }

        private bool HasPrisonerLoadingTimedOut(Ped prisoner, DateTime now)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || IsInTransport(prisoner))
                return false;

            DateTime startedAt;
            int timeoutSeconds = LoadingTimeoutSeconds(prisoner);
            if (!_prisonerLoadingTimeoutStartedAt.TryGetValue(
                    prisoner.Handle, out startedAt)
                || now < startedAt.AddSeconds(timeoutSeconds))
                return false;

            LogRuntime("POLICE_CONVOY_PRISONER_LOADING_TIMEOUT",
                "Prisoner=" + prisoner.Handle
                + "; TimeoutSeconds=" + timeoutSeconds
                + "; Phase=" + _phase);
            return true;
        }

        private bool HasOtherPrisonerLoadingEscort(int prisonerHandle)
        {
            foreach (KeyValuePair<int, Ped> assignment in _prisonerEscortOfficers)
            {
                if (assignment.Key == prisonerHandle
                    || assignment.Value == null || !assignment.Value.Exists()
                    || assignment.Value.IsDead || IsOfficerInTransport(assignment.Value))
                    continue;

                Ped otherPrisoner = _prisoners.FirstOrDefault(value =>
                    value != null && value.Exists()
                    && value.Handle == assignment.Key);
                if (otherPrisoner != null && !otherPrisoner.IsDead
                    && !IsInTransport(otherPrisoner))
                    return true;
            }
            return false;
        }

        private bool PlayerDrivesTransport()
        {
            Ped player = Game.Player.Character;
            return player != null && player.Exists() && _transport != null && _transport.Exists()
                && player.CurrentVehicle != null && player.CurrentVehicle.Exists()
                && player.CurrentVehicle.Handle == _transport.Handle;
        }

        private bool PlayerIsTransportDriver()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || _transport == null
                || !_transport.Exists() || !PlayerDrivesTransport())
                return false;
            try
            {
                Ped driver = Function.Call<Ped>(Hash.GET_PED_IN_VEHICLE_SEAT,
                    _transport, (int)VehicleSeat.Driver, false);
                return driver != null && driver.Exists()
                    && driver.Handle == player.Handle;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_GROUP_PLAYER_DRIVER_CHECK_FAILED", ex);
                return false;
            }
        }

        private bool IsInTransport(Ped prisoner)
        {
            return prisoner != null && prisoner.Exists() && _transport != null && _transport.Exists()
                && prisoner.CurrentVehicle != null && prisoner.CurrentVehicle.Exists()
                && prisoner.CurrentVehicle.Handle == _transport.Handle;
        }

        private bool IsPrisonerInAssignedTransportSeat(Ped prisoner, VehicleSeat seat)
        {
            if (!IsInTransport(prisoner) || seat == VehicleSeat.None
                || seat == VehicleSeat.Any)
                return false;
            int handle = prisoner.Handle;
            DateTime now = DateTime.UtcNow;
            DateTime lastCheck;
            bool lastResult;
            if (_lastAssignedSeatCheckAt.TryGetValue(handle, out lastCheck)
                && now < lastCheck.AddMilliseconds(
                    PrisonerAssignedSeatCheckRefreshMilliseconds)
                && _lastAssignedSeatCheckResult.TryGetValue(handle, out lastResult))
                return lastResult;

            _lastAssignedSeatCheckAt[handle] = now;
            try
            {
                Ped occupant = Function.Call<Ped>(
                    Hash.GET_PED_IN_VEHICLE_SEAT,
                    _transport,
                    (int)seat,
                    false);
                bool confirmed = occupant != null && occupant.Exists()
                    && occupant.Handle == prisoner.Handle;
                _lastAssignedSeatCheckResult[handle] = confirmed;
                return confirmed;
            }
            catch (Exception ex)
            {
                _lastAssignedSeatCheckResult[handle] = false;
                LogException("POLICE_CONVOY_PRISONER_ASSIGNED_SEAT_CHECK_FAILED", ex);
                return false;
            }
        }

        private bool IsOfficerInTransport(Ped officer)
        {
            return officer != null && officer.Exists() && _transport != null && _transport.Exists()
                && officer.CurrentVehicle != null && officer.CurrentVehicle.Exists()
                && officer.CurrentVehicle.Handle == _transport.Handle;
        }

        private void MaintainPrisonerCustodyState(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                return;
            try
            {
                // Reassert only the physical custody flags here. Movement and
                // entry tasks remain state-driven below so this cannot cancel an
                // escort animation every Script tick.
                prisoner.BlockPermanentEvents = true;
                prisoner.CanSwitchWeapons = false;
                Function.Call(Hash.SET_ENTITY_VISIBLE, prisoner, true, false);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, prisoner, true);
                if (_dispatchCustodyOperation)
                    Function.Call(Hash.SET_PED_CONFIG_FLAG,
                        prisoner, HandcuffedPedConfigFlag, true);
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    prisoner, false, false, false, true, false, false, false, false);
                MaintainDispatchCustodyDamageProtection(prisoner);
                ObserveConvoyInjuryState(prisoner, DateTime.UtcNow);
                MaintainDispatchCustodyMovementStyle(prisoner);
                if (ShouldMaintainVisibleCuffPose(prisoner))
                    MaintainVisibleCuffPose(prisoner, DateTime.UtcNow, false);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PRISONER_CUSTODY_STATE_FAILED", ex);
            }
        }

        private bool HasDispatchCustodyDamageProtection(Ped prisoner)
        {
            if (!_dispatchCustodyOperation || prisoner == null || !prisoner.Exists())
                return false;
            LSPDSuspectInjuryState state;
            return _prisonerInjuryStates.TryGetValue(prisoner.Handle, out state)
                && state != null && state.CustodyDamageProtectionActive;
        }

        private void MaintainDispatchCustodyDamageProtection(Ped prisoner)
        {
            if (!HasDispatchCustodyDamageProtection(prisoner))
                return;
            LSPDSuspectInjuryState state;
            if (_prisonerInjuryStates.TryGetValue(prisoner.Handle, out state))
                LSPDDispatch.ApplyDispatchCustodyDamageProtection(prisoner, state);
        }

        private void MaintainActiveCustody(DateTime now, bool force = false)
        {
            if (!force && now < _lastActiveCustodyMaintenanceAt
                .AddMilliseconds(ActiveCustodyMaintenanceRefreshMilliseconds))
                return;
            _lastActiveCustodyMaintenanceAt = now;

            try
            {
                if (_transport != null && _transport.Exists() && !_playerOwnedTransport)
                {
                    _transport.IsPersistent = true;
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _transport, true, true);
                    CaptureAndSetCustodyInvincibility(_transport);
                }
                if (_transportDriver != null && _transportDriver.Exists() && !_transportDriver.IsDead)
                {
                    _transportDriver.IsPersistent = true;
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _transportDriver, true, true);
                    CaptureAndSetCustodyInvincibility(_transportDriver);
                }
                foreach (Ped officer in _sceneEscortOfficers.Concat(_stationHandoffOfficers)
                    .Concat(_prisonHandoffOfficers)
                    .Where(ped => ped != null && ped.Exists() && !ped.IsDead))
                {
                    officer.IsPersistent = true;
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, officer, true, true);
                    CaptureAndSetCustodyInvincibility(officer);
                }
                foreach (Ped prisoner in ValidPrisoners())
                if (!_dispatchGroupPlayerDrivenPickup
                    || _dispatchCustodyTransferredHandles.Contains(prisoner.Handle))
                    MaintainPrisonerCustodyState(prisoner);
                MaintainCustodyBlips();
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_ACTIVE_CUSTODY_MAINTENANCE_FAILED", ex);
            }
        }

        private bool ShouldMaintainVisibleCuffPose(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead || IsInTransport(prisoner))
                return false;
            switch (_phase)
            {
                case CustodyPhase.SceneLoading:
                case CustodyPhase.PrisonLoading:
                    return !_prisonerEscortStartedAt.ContainsKey(prisoner.Handle)
                        && !_prisonerEntryTasksIssued.Contains(prisoner.Handle);
                case CustodyPhase.HoldingAtStation:
                    return true;
                default:
                    return false;
            }
        }

        private void MaintainVisibleCuffPose(Ped prisoner, DateTime now, bool force)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead || prisoner.IsInVehicle())
                return;
            DateTime lastTask;
            bool hasPreviousAttempt = _lastPrisonerCuffPoseAt.TryGetValue(
                prisoner.Handle, out lastTask);
            if (!force && hasPreviousAttempt
                && now < lastTask.AddMilliseconds(PrisonerCuffPoseRefreshMilliseconds))
                return;

            const string handcuffDictionary = "mp_arresting";
            try
            {
                if (LSImmersiveDictionaryAnimation.IsPlaying(
                    prisoner, handcuffDictionary, "idle"))
                {
                    _lastPrisonerCuffPoseAt[prisoner.Handle] = now;
                    return;
                }

                if (!LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                    handcuffDictionary))
                {
                    _lastPrisonerCuffPoseAt[prisoner.Handle] =
                        now.AddMilliseconds(750);
                    return;
                }

                float clipDuration;
                if (!LSImmersiveDictionaryAnimation.TryPlay(
                    prisoner,
                    handcuffDictionary,
                    "idle",
                    3.0f,
                    -2.0f,
                    -1,
                    1,
                    1.0f,
                    out clipDuration))
                {
                    _lastPrisonerCuffPoseAt[prisoner.Handle] = now.AddMilliseconds(750);
                    if (!hasPreviousAttempt)
                        LogRuntime("POLICE_CONVOY_HANDCUFF_CLIP_UNAVAILABLE",
                            "Prisoner=" + prisoner.Handle
                            + "; Dictionary=" + handcuffDictionary
                            + "; Clip=idle; CustodySequence=Retained");
                    return;
                }
                _lastPrisonerCuffPoseAt[prisoner.Handle] = now;
                if (!hasPreviousAttempt || force)
                    LogRuntime("POLICE_CONVOY_HANDCUFF_CLIP_REQUESTED",
                        "Prisoner=" + prisoner.Handle
                        + "; Dictionary=" + handcuffDictionary
                        + "; Clip=idle; Duration=" + clipDuration.ToString("0.000")
                        + "; Flags=1; PlaybackRate=1.0; TaskDurationMs=-1");
            }
            catch (Exception ex)
            {
                _lastPrisonerCuffPoseAt[prisoner.Handle] = now.AddMilliseconds(1000);
                LogException("POLICE_CONVOY_PRISONER_HANDCUFF_POSE_FAILED", ex);
            }
        }

        private void CaptureAndSetCustodyInvincibility(Entity entity)
        {
            if (entity == null || !entity.Exists())
                return;
            try
            {
                bool previous;
                if (!_custodyInvincibilityStates.TryGetValue(entity.Handle, out previous))
                    _custodyInvincibilityStates[entity.Handle] = entity.IsInvincible;
                if (!entity.IsInvincible)
                    entity.IsInvincible = true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_CUSTODY_PROTECTION_FAILED", ex);
            }
        }

        private void RestoreCustodyInvincibility(Entity entity)
        {
            if (entity == null)
                return;
            try
            {
                bool previous;
                if (_custodyInvincibilityStates.TryGetValue(entity.Handle, out previous)
                    && entity.Exists())
                    entity.IsInvincible = previous;
                _custodyInvincibilityStates.Remove(entity.Handle);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_CUSTODY_PROTECTION_RESTORE_FAILED", ex);
            }
        }

        private void RestoreAllCustodyInvincibility(
            ISet<int> preserveDispatchCustodyHandles = null)
        {
            RestoreCustodyInvincibility(_transport);
            RestoreCustodyInvincibility(_transportDriver);
            foreach (Ped prisoner in _prisoners.ToArray())
            {
                bool retainForDispatch = preserveDispatchCustodyHandles != null
                    && preserveDispatchCustodyHandles.Contains(prisoner.Handle);
                if (!retainForDispatch)
                {
                    RestoreDispatchCustodyMovementStyle(prisoner);
                    RestoreCustodyInvincibility(prisoner);
                }
            }
            foreach (Ped officer in _sceneEscortOfficers.Concat(_stationHandoffOfficers)
                .Concat(_prisonHandoffOfficers).ToArray())
                RestoreCustodyInvincibility(officer);
            if (preserveDispatchCustodyHandles == null)
                _custodyInvincibilityStates.Clear();
            else
                foreach (int handle in _custodyInvincibilityStates.Keys
                    .Where(handle => !preserveDispatchCustodyHandles.Contains(handle)).ToArray())
                    _custodyInvincibilityStates.Remove(handle);
        }

        private void MaintainDispatchCustodyMovementStyle(Ped prisoner)
        {
            if (!_dispatchCustodyOperation || _isRequestedConvoyActivity
                || prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || _dispatchCustodyMovementStyleApplied.Contains(prisoner.Handle))
                return;

            int handle = prisoner.Handle;
            DateTime lastRequest;
            if (_dispatchCustodyMovementStyleRequestedAt.TryGetValue(handle, out lastRequest)
                && DateTime.UtcNow < lastRequest.AddMilliseconds(
                    DispatchMovementClipsetRequestRefreshMilliseconds))
                return;

            const string movementClipset = "move_m@prisoner_cuffed";
            try
            {
                _dispatchCustodyMovementStyleRequestedAt[handle] = DateTime.UtcNow;
                if (!Function.Call<bool>(Hash.HAS_ANIM_SET_LOADED, movementClipset))
                {
                    Function.Call(Hash.REQUEST_ANIM_SET, movementClipset);
                    return;
                }

                Function.Call(Hash.SET_PED_MOVEMENT_CLIPSET,
                    prisoner, movementClipset, 0.25f);
                _dispatchCustodyMovementStyleApplied.Add(handle);
                _dispatchCustodyMovementStyleRequestedAt.Remove(handle);
                LogRuntime("POLICE_CONVOY_DISPATCH_RESTRAINED_MOVEMENT_APPLIED",
                    "Prisoner=" + handle + "; Clipset=" + movementClipset
                    + "; Attached=false; Owner=DispatchCustody");
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_RESTRAINED_MOVEMENT_FAILED", ex);
            }
        }

        private void RestoreDispatchCustodyMovementStyle(Ped prisoner)
        {
            if (prisoner == null)
                return;

            int handle = prisoner.Handle;
            bool wasApplied = _dispatchCustodyMovementStyleApplied.Remove(handle);
            _dispatchCustodyMovementStyleRequestedAt.Remove(handle);
            if (!wasApplied || !prisoner.Exists())
                return;

            try
            {
                Function.Call(Hash.RESET_PED_MOVEMENT_CLIPSET, prisoner, 0.25f);
                LogRuntime("POLICE_CONVOY_DISPATCH_RESTRAINED_MOVEMENT_RESTORED",
                    "Prisoner=" + handle + "; Owner=DispatchCustody");
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DISPATCH_RESTRAINED_MOVEMENT_RESTORE_FAILED", ex);
            }
        }

        private void RestoreDispatchCustodyMovementStyles()
        {
            foreach (Ped prisoner in _prisoners.ToArray())
                RestoreDispatchCustodyMovementStyle(prisoner);
            _dispatchCustodyMovementStyleApplied.Clear();
            _dispatchCustodyMovementStyleRequestedAt.Clear();
        }

        private bool MaintainPlayerHandcuffInteraction(
            Ped prisoner,
            Ped player,
            DateTime now)
        {
            DateTime handcuffUntil;
            if (!_playerHandcuffAnimationUntil.TryGetValue(prisoner.Handle, out handcuffUntil))
                return false;

            MaintainPrisonerCustodyState(prisoner);
            if (now < handcuffUntil)
                return true;

            _playerHandcuffAnimationUntil.Remove(prisoner.Handle);
            ClearPlayerCustodyTask(player);
            try { prisoner.Task.ClearAll(); } catch { }
            MaintainPrisonerCustodyState(prisoner);
            // Show the secured pose for the short transition into escorting.
            // The next movement task deliberately replaces it once the player
            // starts walking, so the subject does not become frozen in place.
            MaintainVisibleCuffPose(prisoner, now, true);
            LogRuntime(
                "POLICE_CONVOY_PLAYER_HANDCUFF_CONFIRMED",
                "Prisoner=" + prisoner.Handle + "; Player=" + player.Handle
                + "; Phase=" + _phase);
            Notify("~b~POLICE CUSTODY~s~\nHandcuffs secured. Walk with the prisoner to the marked rear door and press E there.");
            return false;
        }

        private bool TryGetPhysicalCuffState(Ped prisoner, out bool physicallyCuffed)
        {
            physicallyCuffed = false;
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                return false;

            int handle = prisoner.Handle;
            try
            {
                physicallyCuffed = Function.Call<bool>(Hash.IS_PED_CUFFED, prisoner);
                _physicalCuffReadFailuresLogged.Remove(handle);
                return true;
            }
            catch (Exception ex)
            {
                if (_physicalCuffReadFailuresLogged.Add(handle))
                    LogException("POLICE_CONVOY_PHYSICAL_CUFF_STATE_READ_FAILED", ex);
                return false;
            }
        }

        private bool MaintainPlayerLoadingContact(
            Ped prisoner,
            Ped player,
            DateTime now)
        {
            DateTime contactStartedAt;
            if (!_playerLoadingContactStartedAt.TryGetValue(prisoner.Handle, out contactStartedAt))
                return false;

            MaintainPrisonerCustodyState(prisoner);
            if (now < contactStartedAt.AddMilliseconds(PlayerLoadingContactMilliseconds))
                return true;

            _playerLoadingContactStartedAt.Remove(prisoner.Handle);
            ClearPlayerCustodyTask(player);
            try { prisoner.Task.ClearAll(); } catch { }
            MaintainPrisonerCustodyState(prisoner);
            LogRuntime(
                "POLICE_CONVOY_PLAYER_PRISONER_LOADING_CONTACT_CONFIRMED",
                "Prisoner=" + prisoner.Handle + "; Player=" + player.Handle
                + "; Vehicle=" + (_transport == null ? "none" : _transport.Handle.ToString()));
            return false;
        }

        private bool ReleaseAbandonedPlayerEscort(
            Ped prisoner,
            Ped player,
            DateTime now)
        {
            bool playerUnavailable = player == null || !player.Exists() || player.IsDead;
            bool playerInVehicle = !playerUnavailable && player.IsInVehicle();
            bool playerTooFar = !playerUnavailable
                && player.Position.DistanceTo(prisoner.Position) > PlayerEscortMaximumDistance;
            if (!playerUnavailable && !playerInVehicle && !playerTooFar)
            {
                _playerEscortLostAt.Remove(prisoner.Handle);
                return false;
            }

            DateTime lostAt;
            if (!_playerEscortLostAt.TryGetValue(prisoner.Handle, out lostAt))
            {
                _playerEscortLostAt[prisoner.Handle] = now;
                return false;
            }
            if (now < lostAt.AddSeconds(PlayerEscortReleaseGraceSeconds))
                return false;

            int prisonerHandle = prisoner.Handle;
            string reason = playerUnavailable
                ? "Player unavailable"
                : playerInVehicle ? "Player entered a vehicle" : "Player left the custody area";
            DetachPrisonerFromEscort(prisoner, player,
                "PlayerEscortReleased:" + reason);
            _playerEscortPrisoners.Remove(prisonerHandle);
            _playerHandcuffAnimationUntil.Remove(prisonerHandle);
            _playerLoadingContactStartedAt.Remove(prisonerHandle);
            _playerEscortLostAt.Remove(prisonerHandle);
            _playerDoorInteractions.Remove(prisonerHandle);
            _playerEntryInteractions.Remove(prisonerHandle);
            _lastPrisonerEscortPosition.Remove(prisonerHandle);
            _lastPrisonerEscortProgressAt.Remove(prisonerHandle);
            _lastPrisonerEscortRecoveryAt.Remove(prisonerHandle);
            _prisonerEscortOfficers.Remove(prisonerHandle);
            _prisonerEscortStartedAt.Remove(prisonerHandle);
            _prisonerEscortAlignmentStarted.Remove(prisonerHandle);
            _lastGroundedEscortFollowAt.Remove(prisonerHandle);
            SetPrisonerLoadingStage(prisoner, null,
                PrisonerLoadingStage.EscortApproach, now,
                "PlayerReleasedToTransportOfficer");
            _prisonerLoadingTimeoutStartedAt[prisonerHandle] = now;
            _phaseStartedAt = now;
            LogRuntime(
                "POLICE_CONVOY_PLAYER_ESCORT_RELEASED",
                "Prisoner=" + prisonerHandle + "; Reason=" + reason
                + "; Fallback=TransportOfficer");
            Notify("~b~POLICE CUSTODY~s~\nPlayer escort released. The transport officer is taking over the handoff.");
            return true;
        }

        private static void ClearPlayerCustodyTask(Ped player)
        {
            if (player == null || !player.Exists())
                return;
            try { player.Task.ClearAll(); } catch { }
        }

        private bool RecoverStalledPrisonerEscort(
            Ped prisoner,
            Ped escort,
            VehicleSeat seat,
            DateTime now)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || escort == null || !escort.Exists() || escort.IsDead
                || _prisonerEntryTasksIssued.Contains(prisoner.Handle))
                return false;

            bool playerEscort = IsPlayerEscort(escort);
            bool dispatchCustody = _dispatchCustodyOperation
                && !_isRequestedConvoyActivity;

            int prisonerHandle = prisoner.Handle;
            PrisonerLoadingStage stage;
            if (!_prisonerLoadingStages.TryGetValue(prisonerHandle, out stage))
                stage = PrisonerLoadingStage.EscortApproach;
            if (dispatchCustody
                && (stage == PrisonerLoadingStage.VehicleDoorReached
                    || stage == PrisonerLoadingStage.DoorOpened
                    || stage == PrisonerLoadingStage.Loading))
            {
                // The loading stage owns these tasks now. Clearing the
                // transport officer's door task here restarts the approach
                // loop while the same door is still being opened/entered.
                return false;
            }

            Vector3 previousPrisonerPosition;
            Vector3 previousEscortPosition;
            DateTime lastProgressAt;
            if (!_lastPrisonerEscortPosition.TryGetValue(
                    prisonerHandle, out previousPrisonerPosition)
                || !_lastEscortOfficerPosition.TryGetValue(
                    escort.Handle, out previousEscortPosition)
                || !_lastPrisonerEscortProgressAt.TryGetValue(
                    prisonerHandle, out lastProgressAt))
            {
                _lastPrisonerEscortPosition[prisonerHandle] = prisoner.Position;
                _lastEscortOfficerPosition[escort.Handle] = escort.Position;
                _lastPrisonerEscortProgressAt[prisonerHandle] = now;
                return false;
            }

            float prisonerMovement = prisoner.Position.DistanceTo(previousPrisonerPosition);
            float escortMovement = escort.Position.DistanceTo(previousEscortPosition);
            if (prisonerMovement >= EscortProgressDistance
                || (playerEscort && escortMovement >= EscortProgressDistance))
            {
                _lastPrisonerEscortPosition[prisonerHandle] = prisoner.Position;
                _lastEscortOfficerPosition[escort.Handle] = escort.Position;
                _lastPrisonerEscortProgressAt[prisonerHandle] = now;
                _lastPrisonerEscortRecoveryAt.Remove(prisonerHandle);
                return false;
            }
            if (now < lastProgressAt.AddSeconds(EscortStallTimeoutSeconds))
                return false;

            DateTime lastRecoveryAt;
            if (_lastPrisonerEscortRecoveryAt.TryGetValue(
                    prisonerHandle, out lastRecoveryAt)
                && now < lastRecoveryAt.AddSeconds(EscortRecoveryCooldownSeconds))
                return false;

            _lastPrisonerEscortRecoveryAt[prisonerHandle] = now;
            _lastPrisonerEscortPosition[prisonerHandle] = prisoner.Position;
            _lastEscortOfficerPosition[escort.Handle] = escort.Position;
            _lastPrisonerEscortProgressAt[prisonerHandle] = now;
            if (!playerEscort)
            {
                _lastEscortOfficerTaskAt.Remove(escort.Handle);
                try { escort.Task.ClearAll(); }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_ESCORT_RECOVERY_CLEAR_FAILED", ex);
                }
            }
            MaintainPrisonerCustodyState(prisoner);
            if (dispatchCustody
                || escort.Position.DistanceTo(prisoner.Position) > TransportOfficerContactRadius)
                IssuePrisonerGroundedEscortFollow(prisoner, escort, seat, now);
            if (!playerEscort && !dispatchCustody)
            {
                if (stage == PrisonerLoadingStage.Escorting
                    || stage == PrisonerLoadingStage.VehicleDoorApproach)
                    IssueTransportOfficerVehicleApproach(
                        escort, TransportDoorPosition(seat), now);
                else if (escort.Position.DistanceTo(prisoner.Position)
                    > TransportOfficerContactRadius)
                    IssueTransportOfficerApproach(escort, prisoner, now);
            }

            LogRuntime("POLICE_CONVOY_ESCORT_RECOVERY",
                "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                + "; Vehicle=" + (_transport == null ? "none" : _transport.Handle.ToString())
                + "; Phase=" + _phase + "; Action=ReissueGroundedFollowAndOfficerRoute"
                + "; PlayerOwnsEscort=" + playerEscort);
            return true;
        }

        private void SetPrisonerLoadingStage(
            Ped prisoner,
            Ped escort,
            PrisonerLoadingStage stage,
            DateTime now,
            string reason)
        {
            if (prisoner == null || !prisoner.Exists())
                return;

            int prisonerHandle = prisoner.Handle;
            PrisonerLoadingStage previous;
            bool hadPrevious = _prisonerLoadingStages.TryGetValue(
                prisonerHandle, out previous);
            if (hadPrevious && previous == stage)
                return;

            if (stage == PrisonerLoadingStage.EscortApproach
                || stage == PrisonerLoadingStage.Loaded)
                _dispatchSceneDoorAnimationFallbackIssued.Remove(prisonerHandle);
            _prisonerLoadingStages[prisonerHandle] = stage;
            _prisonerLoadingStageStartedAt[prisonerHandle] = now;
            LogRuntime(
                "POLICE_CONVOY_ESCORT_STAGE",
                "Prisoner=" + prisonerHandle
                + "; Previous=" + (hadPrevious ? previous.ToString() : "None")
                + "; Stage=" + stage
                + "; Escort=" + (escort == null || !escort.Exists()
                    ? "none" : escort.Handle.ToString())
                + "; Owner=" + LoadingEscortOwner(prisoner, escort)
                + "; Vehicle=" + (_transport == null || !_transport.Exists()
                    ? "none" : _transport.Handle.ToString())
                + "; Phase=" + _phase
                + "; Reason=" + (reason ?? string.Empty));

            if (stage == PrisonerLoadingStage.VehicleDoorReached
                && IsPlayerEscort(escort))
            {
                Notify("~b~POLICE CUSTODY~s~\nRear door reached. Press E to open it, then press E again to load the handcuffed citizen.");
                LogRuntime(
                    "POLICE_CONVOY_PLAYER_REAR_DOOR_READY",
                    "Prisoner=" + prisonerHandle + "; Player=" + escort.Handle
                    + "; Vehicle=" + (_transport == null || !_transport.Exists()
                        ? "none" : _transport.Handle.ToString())
                    + "; Input=E_OpenThenE_Load");
            }
        }

        private bool IssuePrisonerGroundedEscortFollow(
            Ped prisoner,
            Ped escort,
            VehicleSeat seat,
            DateTime now)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || escort == null || !escort.Exists() || escort.IsDead)
                return false;
            if (IsInTransport(prisoner))
                return true;

            int prisonerHandle = prisoner.Handle;
            DateTime lastFollow;
            if (_lastGroundedEscortFollowAt.TryGetValue(prisonerHandle, out lastFollow)
                && now < lastFollow.AddMilliseconds(GroundedPrisonerFollowRefreshMilliseconds))
                return false;

            try
            {
                // Clear only a stale attachment to this assigned escort before
                // issuing the grounded walking task. New escort stages never
                // attach the prisoner to the officer.
                DetachPrisonerFromEscort(
                    prisoner, escort, "GroundedEscortFollowStarts");
                MaintainPrisonerCustodyState(prisoner);
                bool dispatchCustody = _dispatchCustodyOperation
                    && !_isRequestedConvoyActivity;
                string movement;
                if (dispatchCustody && IsPlayerEscort(escort))
                {
                    // No verified officer/suspect paired walk clip is present
                    // in this project's animation catalog yet. Keep this
                    // fallback grounded and unattached; a bone attachment is
                    // not a substitute for the documented hand-held escort.
                    Vector3 entry = TransportDoorPosition(seat);
                    Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                        prisoner, entry.X, entry.Y, entry.Z,
                        0.85f, -1, 1.0f, 1, 0f);
                    movement = "PrisonerLeadsPlayerToTransport";
                }
                else if (dispatchCustody)
                {
                    // Interim grounded fallback while the documented paired
                    // escort walk is unresolved. Preserve physical movement;
                    // do not attach the prisoner to imitate hand contact.
                    Vector3 entry = TransportDoorPosition(seat);
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                        escort, prisoner,
                        0f, DispatchEscortOfficerTrailingOffset, 0f,
                        0.85f, -1, 0.3f, false);
                    Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                        prisoner, entry.X, entry.Y, entry.Z,
                        0.85f, -1, 1.0f, 1, 0f);
                    movement = "PrisonerLeadsTransportOfficerToDoor";
                }
                else
                {
                    // Keep the confirmed standalone Convoy movement path.
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                        prisoner, escort,
                        0f, -0.2f, 0f,
                        0.85f, -1, 0.3f, false);
                    movement = "RestrainedGroundedFollow";
                }
                _lastGroundedEscortFollowAt[prisonerHandle] = now;
                LogRuntime(
                    "POLICE_CONVOY_GROUNDED_ESCORT_FOLLOW_STARTED",
                    "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                    + "; Movement=" + movement
                    + "; Clipset=move_m@prisoner_cuffed"
                    + (dispatchCustody
                        ? "; Route=PrisonerNavigatesToPhysicalRearDoor; ActorOrder=PrisonerFrontEscortBehind"
                        : "; Offset=0,-0.2,0")
                    + (dispatchCustody && !IsPlayerEscort(escort)
                        ? "; OfficerTrailingOffsetY=" + DispatchEscortOfficerTrailingOffset
                        : string.Empty)
                    + "; Speed=0.85; StoppingRange=0.3"
                    + "; Attached=false");
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_GROUNDED_ESCORT_FOLLOW_FAILED", ex);
                return false;
            }
        }

        private bool IsPrisonerAttachedToEscort(Ped prisoner, Ped escort)
        {
            if (prisoner == null || !prisoner.Exists()
                || escort == null || !escort.Exists())
                return false;
            try
            {
                return Function.Call<bool>(
                    Hash.IS_ENTITY_ATTACHED_TO_ENTITY, prisoner, escort);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PHYSICAL_ESCORT_STATE_CHECK_FAILED", ex);
                return false;
            }
        }

        private void DetachPrisonerFromEscort(Ped prisoner, Ped escort, string reason)
        {
            if (prisoner == null || !prisoner.Exists())
                return;

            int prisonerHandle = prisoner.Handle;
            bool attached;
            try
            {
                // A Player can take custody from an officer after the old
                // officer had been assigned. Check for any attachment parent,
                // not only the newly selected escort.
                attached = Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, prisoner);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PHYSICAL_ESCORT_STATE_CHECK_FAILED", ex);
                attached = escort != null && escort.Exists()
                    && IsPrisonerAttachedToEscort(prisoner, escort);
            }
            if (attached)
            {
                try
                {
                    Function.Call(Hash.DETACH_ENTITY, prisoner, true, true);
                    LogRuntime(
                        "POLICE_CONVOY_PHYSICAL_ESCORT_DETACHED",
                        "Prisoner=" + prisonerHandle + "; Escort="
                        + (escort == null || !escort.Exists()
                            ? "unknown" : escort.Handle.ToString())
                        + "; Reason=" + (reason ?? string.Empty));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PHYSICAL_ESCORT_DETACH_FAILED", ex);
                }
            }

            _lastGroundedEscortFollowAt.Remove(prisonerHandle);
        }

        private bool MaintainPrisonerLoadingEscort(
            Ped prisoner,
            VehicleSeat seat,
            DateTime now)
        {
            if (prisoner == null || !prisoner.Exists() || _transport == null || !_transport.Exists())
                return false;

            Ped escort = FindLoadingEscort(prisoner);
            if (escort == null || IsOfficerInTransport(escort))
                return false;

            int prisonerHandle = prisoner.Handle;
            bool playerEscort = IsPlayerEscort(escort);
            bool dispatchCustody = _dispatchCustodyOperation
                && !_isRequestedConvoyActivity;
            if (playerEscort && ReleaseAbandonedPlayerEscort(prisoner, escort, now))
            {
                escort = FindLoadingEscort(prisoner);
                if (escort == null || IsOfficerInTransport(escort))
                    return false;
                playerEscort = IsPlayerEscort(escort);
            }
            if (playerEscort && MaintainPlayerHandcuffInteraction(prisoner, escort, now))
                return true;
            if (playerEscort && MaintainPlayerLoadingContact(prisoner, escort, now))
                return true;

            PrisonerLoadingStage stage;
            if (!_prisonerLoadingStages.TryGetValue(prisonerHandle, out stage))
            {
                stage = PrisonerLoadingStage.EscortApproach;
                SetPrisonerLoadingStage(prisoner, escort, stage, now,
                    "TransportOfficerAssigned");
            }
            if (stage == PrisonerLoadingStage.EscortApproach
                && _prisonerEscortApproachLogged.Add(prisonerHandle))
                LogRuntime(
                    "POLICE_CONVOY_ESCORT_STARTED",
                    "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                    + "; Owner=" + LoadingEscortOwner(prisoner, escort)
                    + "; Stage=EscortApproach");
            if (RecoverStalledPrisonerEscort(prisoner, escort, seat, now))
                return true;

            MaintainPrisonerCustodyState(prisoner);
            Vector3 entry = TransportDoorPosition(seat);
            if (stage == PrisonerLoadingStage.EscortApproach)
            {
                if (playerEscort)
                {
                    // Player movement is manual. Keep the suspect restrained
                    // and stationary until the player is close enough for a
                    // real handoff; never task the suspect to chase the player.
                    if (escort.Position.DistanceTo(prisoner.Position)
                        > PhysicalEscortContactRadius)
                        return true;
                    SetPrisonerLoadingStage(prisoner, escort,
                        PrisonerLoadingStage.SecuringContact, now,
                        "PlayerReachedCompliantPrisoner");
                    stage = PrisonerLoadingStage.SecuringContact;
                    _prisonerEscortStartedAt[prisonerHandle] = now;
                }
                else
                {
                    if (escort.Position.DistanceTo(prisoner.Position)
                        > TransportOfficerContactRadius)
                    {
                        IssueTransportOfficerApproach(escort, prisoner, now);
                        return true;
                    }

                    bool alreadyPhysicallyCuffed;
                    if (!TryGetPhysicalCuffState(prisoner, out alreadyPhysicallyCuffed))
                        return true;

                    try
                    {
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            escort, prisoner, 1000);
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            prisoner, escort, 1000);
                        if (!alreadyPhysicallyCuffed)
                            Function.Call(Hash.TASK_ARREST_PED, escort, prisoner);
                        _prisonerEscortStartedAt[prisonerHandle] = alreadyPhysicallyCuffed
                            ? now.AddMilliseconds(-PrisonerArrestTaskMilliseconds)
                            : now;
                        _prisonerEscortOfficers[prisonerHandle] = escort;
                        SetPrisonerLoadingStage(prisoner, escort,
                            PrisonerLoadingStage.SecuringContact, now,
                            "TransportOfficerReachedPrisoner");
                        LogRuntime("POLICE_CONVOY_PRISONER_HANDOFF_STARTED",
                            "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                            + "; Vehicle=" + _transport.Handle
                            + "; PhysicalCuffedBeforeHandoff=" + alreadyPhysicallyCuffed
                            + "; ArrestTaskReplayed=" + !alreadyPhysicallyCuffed
                            + "; SynchronizedScene=false");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_PRISONER_HANDOFF_TASK_FAILED", ex);
                        return false;
                    }
                }
            }

            if (stage == PrisonerLoadingStage.SecuringContact)
            {
                DateTime contactStartedAt;
                if (!_prisonerEscortStartedAt.TryGetValue(
                        prisonerHandle, out contactStartedAt))
                {
                    _prisonerEscortStartedAt[prisonerHandle] = now;
                    return true;
                }
                if (!playerEscort
                    && now < contactStartedAt.AddMilliseconds(PrisonerArrestTaskMilliseconds))
                    return true;
                if (playerEscort && _playerHandcuffAnimationUntil.ContainsKey(prisonerHandle))
                    return true;

                if (!playerEscort
                    && _prisonerEscortAlignmentStarted.Add(prisonerHandle))
                {
                    try
                    {
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_COORD,
                            escort, entry.X, entry.Y, entry.Z, 700);
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_COORD,
                            prisoner, entry.X, entry.Y, entry.Z, 700);
                        _prisonerEscortStartedAt[prisonerHandle] = now;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_ESCORT_ALIGNMENT_FAILED", ex);
                    }
                }
                if (!playerEscort && _prisonerEscortAlignmentStarted.Contains(prisonerHandle)
                    && _prisonerEscortStartedAt.TryGetValue(
                        prisonerHandle, out contactStartedAt)
                    && now < contactStartedAt.AddMilliseconds(
                        PrisonerEscortAlignmentMilliseconds))
                    return true;

                if (escort.Position.DistanceTo(prisoner.Position)
                    > PhysicalEscortContactRadius)
                {
                    if (!playerEscort)
                        IssueTransportOfficerApproach(escort, prisoner, now);
                    else
                        SetPrisonerLoadingStage(prisoner, escort,
                            PrisonerLoadingStage.EscortApproach, now,
                            "PlayerMustReapproachBeforePhysicalEscort");
                    return true;
                }

                if (!IssuePrisonerGroundedEscortFollow(prisoner, escort, seat, now))
                    return true;

                _prisonerEscortStartedAt[prisonerHandle] = now;
                SetPrisonerLoadingStage(prisoner, escort,
                    PrisonerLoadingStage.Escorting, now,
                    playerEscort ? "PlayerGroundedEscortFollowStarted"
                        : "OfficerGroundedEscortFollowStarted");
                _lastPrisonerEscortPosition[prisonerHandle] = prisoner.Position;
                _lastEscortOfficerPosition[escort.Handle] = escort.Position;
                _lastPrisonerEscortProgressAt[prisonerHandle] = now;
                LogRuntime("POLICE_CONVOY_ESCORT_MOVEMENT_STARTED",
                    "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                    + "; Owner=" + LoadingEscortOwner(prisoner, escort)
                    + "; Movement=" + (dispatchCustody
                        ? (playerEscort ? "PrisonerLeadsPlayerToTransport"
                            : "PrisonerLeadsTransportOfficerToDoor")
                        : "GroundedFollowToOffset")
                    + "; ActorOrder=" + (dispatchCustody
                        ? "PrisonerFrontEscortBehind" : "ExistingConvoyOrder")
                    + "; Attached=false");
                return true;
            }

            if (stage == PrisonerLoadingStage.Escorting
                || stage == PrisonerLoadingStage.VehicleDoorApproach)
            {
                Vector3 escortTarget = entry;
                float escortDoorRadius = playerEscort ? PlayerCustodyVehicleRadius : 2.6f;
                bool escortAtDoor = escort.Position.DistanceTo(escortTarget) <= escortDoorRadius;
                bool prisonerAtDoor = prisoner.Position.DistanceTo(entry) <= 2.8f;
                if (!escortAtDoor || !prisonerAtDoor)
                {
                    SetPrisonerLoadingStage(prisoner, escort,
                        PrisonerLoadingStage.VehicleDoorApproach, now,
                        "WalkingUnderGroundedEscortToRearDoor");
                    if (!playerEscort && !dispatchCustody)
                        IssueTransportOfficerVehicleApproach(escort, escortTarget, now);
                    if (prisoner.Position.DistanceTo(escort.Position) > 2.8f
                        && Math.Abs(prisoner.Speed) < 0.2f)
                        IssuePrisonerGroundedEscortFollow(prisoner, escort, seat, now);
                    return true;
                }

                SetPrisonerLoadingStage(prisoner, escort,
                    PrisonerLoadingStage.VehicleDoorReached, now,
                    "BothActorsReachedRearPassengerDoor");
                return true;
            }

            if (!_prisonerVehicleDoorsOpened.Contains(prisonerHandle))
            {
                // A player-owned escort must explicitly open the rear door.
                // The transport officer opens it automatically after the
                // officer and prisoner reach the correct side.
                if (playerEscort && !_playerDoorInteractions.Contains(prisonerHandle))
                    return true;
                int doorIndex = PrisonerDoorIndex(seat);

                DateTime doorTaskIssuedAt;
                bool doorTaskWasIssued = _prisonerDoorOpenTaskIssuedAt.TryGetValue(
                    prisonerHandle, out doorTaskIssuedAt);
                bool doorOpenConfirmed = IsPrisonerDoorPhysicallyOpen(doorIndex)
                    && (playerEscort ? _playerDoorInteractions.Contains(prisonerHandle)
                        : doorTaskWasIssued);
                if (doorOpenConfirmed)
                {
                    _prisonerVehicleDoorsOpened.Add(prisonerHandle);
                    _prisonerDoorOpenTaskIssuedAt.Remove(prisonerHandle);
                    _dispatchSceneDoorAnimationFallbackIssued.Remove(prisonerHandle);
                    SetPrisonerLoadingStage(prisoner, escort,
                        PrisonerLoadingStage.DoorOpened, now,
                        playerEscort ? "PlayerRearDoorPhysicallyOpenConfirmed"
                            : "OfficerRearDoorPhysicallyOpenConfirmed");
                    LogRuntime("POLICE_CONVOY_PRISONER_DOOR_OPEN_CONFIRMED",
                        "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                        + "; Door=" + doorIndex
                        + "; Owner=" + LoadingEscortOwner(prisoner, escort)
                        + "; AngleRatioAtLeast=0.75");
                    return true;
                }

                bool dispatchSceneCustody = dispatchCustody
                    && _phase == CustodyPhase.SceneLoading;
                bool playerDispatchCustody = playerEscort && dispatchCustody
                    && _playerDoorInteractions.Contains(prisonerHandle);
                bool mayUseDispatchDoorFallback = dispatchSceneCustody
                    || playerDispatchCustody;
                if (mayUseDispatchDoorFallback && doorTaskWasIssued
                    && now >= doorTaskIssuedAt.AddMilliseconds(
                        DispatchSceneDoorAnimationFallbackGraceMilliseconds)
                    && _dispatchSceneDoorAnimationFallbackIssued.Add(prisonerHandle))
                {
                    try
                    {
                        // Some stock Utility/Transport models leave the AI
                        // open-door task queued without moving the rear door.
                        // Keep the officer at the door and request the door's
                        // normal animation; physical angle confirmation and
                        // the prisoner's physical seat entry remain mandatory.
                        Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                            _transport, doorIndex, false, false);
                        LogRuntime(
                            playerEscort
                                ? "POLICE_CONVOY_PLAYER_DISPATCH_DOOR_ANIMATION_FALLBACK"
                                : "POLICE_CONVOY_DISPATCH_SCENE_DOOR_ANIMATION_FALLBACK",
                            "Prisoner=" + prisonerHandle
                            + "; Escort=" + escort.Handle
                            + "; Vehicle=" + _transport.Handle
                            + "; Door=" + doorIndex
                            + "; Owner=" + LoadingEscortOwner(prisoner, escort)
                            + "; TaskWaitMs="
                            + DispatchSceneDoorAnimationFallbackGraceMilliseconds
                            + "; OpenInstantly=false; PhysicalAngleConfirmationRequired=true"
                            + "; PhysicalEntryAndSeatConfirmationRequired=true");
                    }
                    catch (Exception ex)
                    {
                        LogException(
                            "POLICE_CONVOY_DISPATCH_SCENE_DOOR_ANIMATION_FALLBACK_FAILED",
                            ex);
                        return false;
                    }
                    return true;
                }
                // The Player's E interaction owns the opening task. After the
                // one bounded animation fallback, wait for GTA's physical door
                // angle and the next E instead of replacing the Player task.
                if (playerEscort)
                    return true;
                int doorTaskTimeoutMilliseconds = dispatchCustody
                    ? DispatchCustodyDoorOpenTaskTimeoutMilliseconds
                    : 4000;
                int doorTaskRetryMilliseconds = dispatchCustody
                    ? DispatchCustodyDoorOpenRetryMilliseconds
                    : 4000;
                if (doorTaskWasIssued
                    && now < doorTaskIssuedAt.AddMilliseconds(doorTaskRetryMilliseconds))
                    return true;
                try
                {
                    Function.Call(Hash.TASK_OPEN_VEHICLE_DOOR,
                        escort, _transport, doorTaskTimeoutMilliseconds, doorIndex, 1.0f);
                    _prisonerDoorOpenTaskIssuedAt[prisonerHandle] = now;
                    LogRuntime("POLICE_CONVOY_PRISONER_DOOR_TASK_ISSUED",
                        "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                        + "; Door=" + doorIndex
                        + "; Owner=" + LoadingEscortOwner(prisoner, escort)
                        + "; Retry=" + doorTaskWasIssued
                        + "; TaskTimeoutMs=" + doorTaskTimeoutMilliseconds
                        + "; RetryAfterMs=" + doorTaskRetryMilliseconds
                        + "; PhysicalOpenForced=false");
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_DOOR_OPEN_FAILED", ex);
                    return false;
                }
                return true;
            }

            DateTime entryTaskStartedAt;
            bool entryTaskInProgress = _prisonerEntryTasksIssued.Contains(prisonerHandle)
                && _lastPrisonerVehicleEntryAt.TryGetValue(
                    prisonerHandle, out entryTaskStartedAt)
                && now < entryTaskStartedAt.AddMilliseconds(
                    PrisonerVehicleEntryRefreshMilliseconds);
            if (entryTaskInProgress)
            {
                if (stage != PrisonerLoadingStage.Loading)
                    SetPrisonerLoadingStage(prisoner, escort,
                        PrisonerLoadingStage.Loading, now,
                        "PhysicalVehicleEntryTaskStillInProgress");
                return true;
            }

            if (stage != PrisonerLoadingStage.Loading)
                SetPrisonerLoadingStage(prisoner, escort,
                    PrisonerLoadingStage.DoorOpened, now,
                    "RearDoorReadyForPhysicalLoading");

            // The player deliberately uses E a second time for the loading
            // contact. An NPC transport officer gets the same short physical
            // handoff window after opening the rear door.
            if (playerEscort
                && (!_playerEntryInteractions.Contains(prisonerHandle)
                    || _playerLoadingContactStartedAt.ContainsKey(prisonerHandle)))
                return true;
            DateTime doorOpenedAt;
            if (_prisonerLoadingStageStartedAt.TryGetValue(prisonerHandle, out doorOpenedAt)
                && now < doorOpenedAt.AddMilliseconds(PrisonerDoorOpenSettleMilliseconds))
                return true;

            if (stage != PrisonerLoadingStage.Loading)
            {
                SetPrisonerLoadingStage(prisoner, escort,
                    PrisonerLoadingStage.Loading, now,
                    playerEscort ? "PlayerConfirmedPhysicalLoad" : "OfficerReadyToLoadPrisoner");
                DetachPrisonerFromEscort(prisoner, escort,
                    "BothActorsAtRearDoor;PhysicalVehicleEntryStarting");
            }

            if (CanIssuePrisonerVehicleEntry(prisoner))
            {
                try
                {
                    // The transport officer/player has completed contact and
                    // opened the assigned rear door. GTA's bounded physical
                    // entry task completes the step; the prisoner is never
                    // warped or given a free-standing civilian entry task.
                    prisoner.Task.EnterVehicle(_transport, seat, 12000, 1.0f);
                    bool firstEntryTask = _prisonerEntryTasksIssued.Add(prisonerHandle);
                    LogRuntime(
                        firstEntryTask
                            ? "POLICE_CONVOY_PRISONER_ENTRY_TASK_ISSUED"
                            : "POLICE_CONVOY_PRISONER_ENTRY_TASK_RETRY_ISSUED",
                        "Prisoner=" + prisonerHandle + "; Seat=" + seat
                        + "; Escort=" + escort.Handle + "; Door=" + PrisonerDoorIndex(seat)
                        + "; AttachmentReleasedAtRearDoor=true");
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_ENTRY_TASK_FAILED", ex);
                    return false;
                }
            }
            return true;
        }

        private bool MaintainPrisonerUnloadingEscort(
            Ped prisoner,
            IList<Ped> officers,
            Vector3 holdingPosition,
            DateTime now,
            string handoffStage)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || _transport == null || !_transport.Exists() || officers == null)
                return false;

            Ped officer = FindHandoffOfficer(prisoner, officers);
            if (officer == null || officer.IsDead)
                return false;

            int prisonerHandle = prisoner.Handle;
            DateTime escortStartedAt;
            if (!_prisonerUnloadingEscortStartedAt.TryGetValue(prisonerHandle, out escortStartedAt))
            {
                if (IsInTransport(prisoner))
                {
                    Vector3 entry = TransportDoorPosition(PrisonerSeat(
                        prisoner, ValidPrisoners().ToList()));
                    if (officer.Position.DistanceTo(entry) > 2.8f)
                    {
                        IssueTransportOfficerVehicleApproach(officer, entry, now);
                        return true;
                    }

                    int doorIndex = PrisonerDoorIndex(PrisonerSeat(
                        prisoner, ValidPrisoners().ToList()));
                    if (!_prisonerUnloadingDoorsOpened.Contains(prisonerHandle))
                    {
                        DateTime doorTaskIssuedAt;
                        bool doorTaskWasIssued = _prisonerDoorOpenTaskIssuedAt.TryGetValue(
                            prisonerHandle, out doorTaskIssuedAt);
                        if (!IsPrisonerDoorPhysicallyOpen(doorIndex))
                        {
                            if (!doorTaskWasIssued
                                || now >= doorTaskIssuedAt.AddSeconds(4))
                            {
                                try
                                {
                                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                        officer, _transport, 1000);
                                    // This task is used by GTA to open a door
                                    // as part of vehicle entry. Keep the
                                    // receiving officer on foot and animate
                                    // only the assigned custody door.
                                    Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                                        _transport, doorIndex, false, false);
                                    _prisonerDoorOpenTaskIssuedAt[prisonerHandle] = now;
                                    LogRuntime("POLICE_CONVOY_PRISONER_HANDOFF_DOOR_OPEN_REQUESTED",
                                        "Stage=" + (handoffStage ?? string.Empty)
                                        + "; Prisoner=" + prisonerHandle
                                        + "; Officer=" + officer.Handle
                                        + "; Door=" + doorIndex
                                        + "; Retry=" + doorTaskWasIssued
                                        + "; DoorOpenRequest=SET_VEHICLE_DOOR_OPEN"
                                        + "; OpenInstantly=false; OfficerTask=FaceTransportOnly"
                                        + "; PhysicalAngleConfirmationRequired=true");
                                }
                                catch (Exception ex)
                                {
                                    LogException("POLICE_CONVOY_PRISONER_HANDOFF_DOOR_FAILED", ex);
                                    return false;
                                }
                            }
                            return true;
                        }

                        _prisonerUnloadingDoorsOpened.Add(prisonerHandle);
                        _prisonerDoorOpenTaskIssuedAt.Remove(prisonerHandle);
                        LogRuntime("POLICE_CONVOY_PRISONER_HANDOFF_DOOR_OPEN_CONFIRMED",
                            "Stage=" + (handoffStage ?? string.Empty)
                            + "; Prisoner=" + prisonerHandle
                            + "; Officer=" + officer.Handle
                            + "; Door=" + doorIndex + "; AngleRatioAtLeast=0.75");
                    }

                    // Only the handoff officer opens the door and starts the
                    // exit. The prisoner never receives a free-standing leave
                    // task before an officer is physically beside the vehicle.
                    CommandLeaveTransport(prisoner, "ReceivingOfficerPhysicalUnload");
                    return true;
                }

                _lastPrisonerVehicleExitAt.Remove(prisonerHandle);

                if (_prisonerUnloadingDoorsOpened.Remove(prisonerHandle))
                {
                    try
                    {
                        int doorIndex = PrisonerDoorIndex(PrisonerSeat(
                            prisoner, ValidPrisoners().ToList()));
                        Function.Call(Hash.SET_VEHICLE_DOOR_SHUT,
                            _transport, doorIndex, false);
                        LogRuntime("POLICE_CONVOY_PRISONER_HANDOFF_DOOR_CLOSED",
                            "Stage=" + (handoffStage ?? string.Empty)
                            + "; Prisoner=" + prisonerHandle
                            + "; Vehicle=" + _transport.Handle);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_PRISONER_HANDOFF_DOOR_CLOSE_FAILED", ex);
                    }
                }

                if (officer.Position.DistanceTo(prisoner.Position) > 3.2f)
                {
                    IssueTransportOfficerApproach(officer, prisoner, now);
                    return true;
                }

                try
                {
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        officer, prisoner, 1500);
                    Function.Call(Hash.TASK_ARREST_PED, officer, prisoner);
                    MaintainPrisonerCustodyState(prisoner);
                    _prisonerUnloadingEscortStartedAt[prisonerHandle] = now;
                    LogRuntime("POLICE_CONVOY_PRISONER_UNLOADING_HANDOFF_STARTED",
                        "Stage=" + (handoffStage ?? string.Empty)
                        + "; Prisoner=" + prisonerHandle
                        + "; Officer=" + officer.Handle);
                    return true;
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_UNLOADING_HANDOFF_FAILED", ex);
                    return false;
                }
            }

            if (now < escortStartedAt.AddMilliseconds(PrisonerEscortSettleMilliseconds))
                return true;

            if (officer.Position.DistanceTo(holdingPosition) > 2.6f)
            {
                IssueTransportOfficerVehicleApproach(officer, holdingPosition, now);
                if (prisoner.Position.DistanceTo(officer.Position) > 4.0f
                    && CanIssuePrisonerTask(prisoner))
                {
                    try
                    {
                        Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                            prisoner, officer, 0f, -1.0f, 0f,
                            1.0f, -1, 1.1f, false);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_CONVOY_PRISONER_UNLOADING_ESCORT_FAILED", ex);
                    }
                }
                return true;
            }

            // Keep the officer and prisoner together for the state transition;
            // this prevents the prisoner from becoming an ordinary pedestrian
            // in the gap between physical arrival and station/prison booking.
            if (prisoner.Position.DistanceTo(officer.Position) > 3.0f
                && CanIssuePrisonerTask(prisoner))
            {
                try
                {
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                        prisoner, officer, 0f, -1.0f, 0f,
                        1.0f, -1, 1.1f, false);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_UNLOADING_FINAL_APPROACH_FAILED", ex);
                }
            }
            MaintainPrisonerCustodyState(prisoner);
            if (prisoner.Position.DistanceTo(officer.Position) <= 3.0f)
                _prisonerUnloadingOfficers.Remove(prisonerHandle);
            return true;
        }

        private Ped FindLoadingEscort(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists())
                return null;

            Ped assigned;
            if (_prisonerEscortOfficers.TryGetValue(prisoner.Handle, out assigned)
                && assigned != null && assigned.Exists() && !assigned.IsDead
                && !IsOfficerInTransport(assigned))
                return assigned;

            assigned = _sceneEscortOfficers
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead
                    && !IsOfficerInTransport(officer)
                    && (_dispatchPickupStops.Count == 0
                        || _transport == null || !_transport.Exists()
                        || officer.Position.DistanceTo(_transport.Position)
                            <= MaximumDispatchSceneVehicleDistance)
                    && !IsLoadingEscortBusy(officer, prisoner.Handle))
                .OrderBy(officer => officer.Position.DistanceTo(prisoner.Position))
                .FirstOrDefault();
            if (assigned != null)
                _prisonerEscortOfficers[prisoner.Handle] = assigned;
            return assigned;
        }

        private bool IsPlayerEscort(Ped escort)
        {
            Ped player = Game.Player.Character;
            return escort != null && escort.Exists()
                && player != null && player.Exists()
                && escort.Handle == player.Handle;
        }

        private string LoadingEscortOwner(Ped prisoner, Ped escort)
        {
            if (IsPlayerEscort(escort))
                return "Player";
            Ped backupEscort;
            if (prisoner != null && prisoner.Exists()
                && _dispatchGroupBackupEscorts.TryGetValue(
                    prisoner.Handle, out backupEscort)
                && backupEscort != null && backupEscort.Exists()
                && escort != null && escort.Exists()
                && backupEscort.Handle == escort.Handle)
                return "AssignedBackupOfficer";
            return "TransportOfficer";
        }

        private bool IsLoadingEscortBusy(Ped officer, int currentPrisonerHandle)
        {
            if (officer == null || !officer.Exists())
                return true;
            foreach (KeyValuePair<int, Ped> assignment in _prisonerEscortOfficers)
            {
                if (assignment.Key == currentPrisonerHandle
                    || assignment.Value == null
                    || !assignment.Value.Exists()
                    || assignment.Value.Handle != officer.Handle)
                    continue;
                Ped other = _prisoners.FirstOrDefault(prisoner =>
                    prisoner != null && prisoner.Exists()
                    && prisoner.Handle == assignment.Key);
                // One escort may be reused only after the earlier prisoner is
                // physically inside. Reusing it sooner overwrites the first
                // arrest/entry task and makes both subjects look autonomous.
                if (other != null && !other.IsDead && !IsInTransport(other))
                    return true;
            }
            return false;
        }

        private Ped FindHandoffOfficer(Ped prisoner, IList<Ped> officers)
        {
            if (prisoner == null || officers == null)
                return null;
            List<Ped> available = officers
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead)
                .ToList();
            if (available.Count == 0)
                return null;
            Ped assigned;
            if (_prisonerUnloadingOfficers.TryGetValue(prisoner.Handle, out assigned)
                && assigned != null && assigned.Exists() && !assigned.IsDead)
                return assigned;

            assigned = available.FirstOrDefault(officer =>
                !IsUnloadingOfficerBusy(officer, prisoner.Handle));
            if (assigned == null)
                return null;
            _prisonerUnloadingOfficers[prisoner.Handle] = assigned;
            return assigned;
        }

        private bool IsUnloadingOfficerBusy(Ped officer, int currentPrisonerHandle)
        {
            if (officer == null || !officer.Exists())
                return true;
            foreach (KeyValuePair<int, Ped> assignment in _prisonerUnloadingOfficers)
            {
                if (assignment.Key == currentPrisonerHandle
                    || assignment.Value == null
                    || !assignment.Value.Exists()
                    || assignment.Value.Handle != officer.Handle)
                    continue;
                Ped other = _prisoners.FirstOrDefault(prisoner =>
                    prisoner != null && prisoner.Exists()
                    && prisoner.Handle == assignment.Key);
                if (other == null || other.IsDead)
                    continue;
                // Keep a single receiving officer on one prisoner until the
                // subject has left the vehicle and reached the handoff area.
                // This prevents two leave/arrest tasks from cancelling each
                // other when a transport carries a group.
                if (IsInTransport(other)
                    || !_prisonerUnloadingEscortStartedAt.ContainsKey(other.Handle)
                    || other.Position.DistanceTo(officer.Position) > 3.0f)
                    return true;
            }
            return false;
        }

        private void IssueTransportOfficerApproach(Ped officer, Ped prisoner, DateTime now)
        {
            if (officer == null || !officer.Exists() || prisoner == null || !prisoner.Exists())
                return;
            DateTime last;
            if (_lastEscortOfficerTaskAt.TryGetValue(officer.Handle, out last)
                && now < last.AddMilliseconds(TransportOfficerTaskRefreshMilliseconds))
                return;
            try
            {
                Function.Call(Hash.TASK_GO_TO_ENTITY,
                    officer, prisoner, -1, 2.0f, 1.15f, 1073741824, 0);
                _lastEscortOfficerTaskAt[officer.Handle] = now;
                if (IsRemoteDispatchGroupPickup()
                    && _dispatchGroupBackupEscorts.Values.Any(value => value != null
                        && value.Exists() && value.Handle == officer.Handle))
                    LogRuntime("POLICE_CONVOY_BACKUP_CUSTODY_APPROACH_TASK",
                        "Suspect=" + prisoner.Handle
                        + "; Officer=" + officer.Handle
                        + "; PhysicalTask=ApproachSecuredSuspect");
            }
            catch (Exception ex)
            {
                LogException(LoadingEscortOwner(prisoner, officer)
                    == "AssignedBackupOfficer"
                    ? "POLICE_CONVOY_BACKUP_CUSTODY_APPROACH_FAILED"
                    : "POLICE_CONVOY_TRANSPORT_OFFICER_APPROACH_FAILED", ex);
            }
        }

        private void IssueTransportOfficerVehicleApproach(
            Ped officer,
            Vector3 entry,
            DateTime now)
        {
            if (officer == null || !officer.Exists())
                return;
            DateTime last;
            if (_lastEscortOfficerTaskAt.TryGetValue(officer.Handle, out last)
                && now < last.AddMilliseconds(TransportOfficerTaskRefreshMilliseconds))
                return;
            try
            {
                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                    officer, entry.X, entry.Y, entry.Z,
                    1.15f, -1, 1.0f, 1, 0f);
                _lastEscortOfficerTaskAt[officer.Handle] = now;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_TRANSPORT_OFFICER_VEHICLE_APPROACH_FAILED", ex);
            }
        }

        private void ClosePrisonerDoor(Ped prisoner)
        {
            if (prisoner == null || _transport == null || !_transport.Exists())
                return;

            int doorIndex = PrisonerDoorIndex(PrisonerSeat(
                prisoner, ValidPrisoners().ToList()));
            if (!_dispatchCustodyOperation)
            {
                if (!_prisonerVehicleDoorsOpened.Contains(prisoner.Handle))
                    return;
                try
                {
                    Function.Call(Hash.SET_VEHICLE_DOOR_SHUT,
                        _transport, doorIndex, false);
                    _prisonerVehicleDoorsOpened.Remove(prisoner.Handle);
                    _prisonerDoorOpenTaskIssuedAt.Remove(prisoner.Handle);
                    LogRuntime("POLICE_CONVOY_PRISONER_DOOR_CLOSED",
                        "Prisoner=" + prisoner.Handle + "; Vehicle=" + _transport.Handle);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_DOOR_CLOSE_FAILED", ex);
                }
                return;
            }

            if (IsPrisonerDoorPhysicallyClosed(doorIndex))
            {
                bool closeWasRequested = _prisonerDoorCloseTasksIssued.Remove(
                    prisoner.Handle);
                _prisonerVehicleDoorsOpened.Remove(prisoner.Handle);
                _prisonerDoorOpenTaskIssuedAt.Remove(prisoner.Handle);
                if (closeWasRequested)
                    LogRuntime("POLICE_CONVOY_PRISONER_DOOR_CLOSED_CONFIRMED",
                        "Prisoner=" + prisoner.Handle
                        + "; Vehicle=" + _transport.Handle
                        + "; Door=" + doorIndex
                        + "; AngleRatioAtMost=0.05");
                return;
            }

            if (!_prisonerDoorCloseTasksIssued.Add(prisoner.Handle))
                return;
            try
            {
                Function.Call(Hash.SET_VEHICLE_DOOR_SHUT,
                    _transport, doorIndex, false);
                LogRuntime("POLICE_CONVOY_PRISONER_DOOR_CLOSE_REQUESTED",
                    "Prisoner=" + prisoner.Handle
                    + "; Vehicle=" + _transport.Handle
                    + "; Door=" + doorIndex
                    + "; PhysicalCloseConfirmationRequired=true");
            }
            catch (Exception ex)
            {
                _prisonerDoorCloseTasksIssued.Remove(prisoner.Handle);
                LogException("POLICE_CONVOY_PRISONER_DOOR_CLOSE_FAILED", ex);
            }
        }

        private bool IsPrisonerDoorPhysicallyClosed(int doorIndex)
        {
            if (_transport == null || !_transport.Exists())
                return false;
            try
            {
                return Function.Call<float>(
                    Hash.GET_VEHICLE_DOOR_ANGLE_RATIO,
                    _transport,
                    doorIndex) <= 0.05f;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PRISONER_DOOR_CLOSE_STATE_CHECK_FAILED", ex);
                return false;
            }
        }

        private void NormalizeTransportRearDoors(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
                return;

            foreach (int doorIndex in new[] { 2, 3 })
            {
                try
                {
                    float angleRatio = Function.Call<float>(
                        Hash.GET_VEHICLE_DOOR_ANGLE_RATIO, vehicle, doorIndex);
                    if (angleRatio > 0.05f)
                    {
                        // Some add-on transport models spawn with a rear or
                        // side door already open. Start staging with doors
                        // closed; custody owners open them later with a Ped task.
                        Function.Call(Hash.SET_VEHICLE_DOOR_SHUT,
                            vehicle, doorIndex, true);
                        LogRuntime("POLICE_CONVOY_TRANSPORT_REAR_DOOR_NORMALIZED",
                            "Vehicle=" + vehicle.Handle + "; Door=" + doorIndex
                            + "; SpawnAngleRatio=" + angleRatio.ToString("0.00")
                            + "; ClosedBeforeArrival=true");
                    }
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_TRANSPORT_REAR_DOOR_NORMALIZE_FAILED", ex);
                }
            }
        }

        private bool IsPrisonerDoorPhysicallyOpen(int doorIndex)
        {
            if (_transport == null || !_transport.Exists())
                return false;
            try
            {
                return Function.Call<float>(
                    Hash.GET_VEHICLE_DOOR_ANGLE_RATIO,
                    _transport,
                    doorIndex) >= 0.75f;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PRISONER_DOOR_STATE_CHECK_FAILED", ex);
                return false;
            }
        }

        private static int PrisonerDoorIndex(VehicleSeat seat)
        {
            if (seat == VehicleSeat.RightFront)
                return 1;
            return seat == VehicleSeat.RightRear ? 3 : 2;
        }

        private Vector3 TransportDoorPosition(VehicleSeat seat)
        {
            if (_transport == null || !_transport.Exists())
                return Vector3.Zero;
            float side = seat == VehicleSeat.LeftRear ? -2.2f : 2.2f;
            float longitudinal = seat == VehicleSeat.RightFront ? 0.55f : -1.0f;
            return _transport.GetOffsetPosition(new Vector3(side, longitudinal, 0f));
        }

        private void CommandWalkToTransport(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists() || _transport == null || !_transport.Exists())
                return;
            Vector3 entry = TransportDoorPosition(PrisonerSeat(
                prisoner, ValidPrisoners().ToList()));
            CommandWalkTo(prisoner, entry);
        }

        private void CommandLeaveTransport(Ped prisoner, string reason = "PhysicalUnload")
        {
            if (prisoner == null || !prisoner.Exists() || !IsInTransport(prisoner))
                return;
            DateTime now = DateTime.UtcNow;
            DateTime lastIssuedAt;
            bool retry = _lastPrisonerVehicleExitAt.TryGetValue(
                prisoner.Handle, out lastIssuedAt);
            if (retry && now < lastIssuedAt.AddMilliseconds(
                    PrisonerVehicleExitRefreshMilliseconds))
                return;
            if (!CanIssuePrisonerTask(prisoner))
                return;
            try
            {
                Function.Call(Hash.TASK_LEAVE_VEHICLE, prisoner, _transport, 0);
                _lastPrisonerVehicleExitAt[prisoner.Handle] = now;
                LogRuntime(retry
                        ? "POLICE_CONVOY_PRISONER_EXIT_TASK_RETRY_ISSUED"
                        : "POLICE_CONVOY_PRISONER_EXIT_TASK_ISSUED",
                    "Prisoner=" + prisoner.Handle
                    + "; Vehicle=" + _transport.Handle
                    + "; AssignedSeat=" + PrisonerSeat(prisoner, _prisoners)
                    + "; Door=" + PrisonerDoorIndex(PrisonerSeat(prisoner, _prisoners))
                    + "; Reason=" + (reason ?? string.Empty)
                    + "; Retry=" + retry
                    + "; PhysicalExitConfirmationRequired=true"
                    + "; RetryAfterMs=" + PrisonerVehicleExitRefreshMilliseconds);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PRISONER_EXIT_TASK_FAILED", ex);
            }
        }

        private void CommandWalkTo(Ped prisoner, Vector3 target)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsInVehicle())
                return;
            if (!CanIssuePrisonerTask(prisoner))
                return;
            try
            {
                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                    prisoner, target.X, target.Y, target.Z,
                    1.0f, -1, 1.0f, 1, 0f);
            }
            catch (Exception ex) { LogException("POLICE_CONVOY_PRISONER_WALK_TASK_FAILED", ex); }
        }

        private bool CanIssuePrisonerTask(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists())
                return false;
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (_lastPrisonerTaskAt.TryGetValue(prisoner.Handle, out last)
                && now < last.AddMilliseconds(PrisonerTaskRefreshMilliseconds))
                return false;
            _lastPrisonerTaskAt[prisoner.Handle] = now;
            return true;
        }

        private bool CanIssuePrisonerVehicleEntry(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists())
                return false;
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (_lastPrisonerVehicleEntryAt.TryGetValue(prisoner.Handle, out last)
                && now < last.AddMilliseconds(PrisonerVehicleEntryRefreshMilliseconds))
                return false;
            _lastPrisonerVehicleEntryAt[prisoner.Handle] = now;
            return true;
        }

        private Vector3 StationHoldingPosition(Ped prisoner)
        {
            int index = Math.Max(0, _prisoners.IndexOf(prisoner));
            // Keep the receiving point tied to the accessible station intake,
            // not to a global offset from the transport's parking centre.
            Vector3 anchor = _stationHandoffReceivingPosition != Vector3.Zero
                ? _stationHandoffReceivingPosition
                : (_stationHandoffGroundPosition != Vector3.Zero
                    ? _stationHandoffGroundPosition
                    : (_stationHandoffMarkerPosition != Vector3.Zero
                        ? _stationHandoffMarkerPosition : _stationDestination));
            Vector3[] offsets =
            {
                Vector3.Zero, new Vector3(1.5f, 0f, 0f), new Vector3(-1.5f, 0f, 0f),
                new Vector3(0f, 1.5f, 0f), new Vector3(0f, -1.5f, 0f),
                new Vector3(2.0f, 1.5f, 0f), new Vector3(-2.0f, 1.5f, 0f),
                new Vector3(2.0f, -1.5f, 0f), new Vector3(-2.0f, -1.5f, 0f)
            };
            int start = (index * 2) % offsets.Length;
            for (int attempt = 0; attempt < offsets.Length; attempt++)
            {
                Vector3 holdingPosition;
                if (!TryResolveStationWalkPosition(
                    anchor + offsets[(start + attempt) % offsets.Length],
                    out holdingPosition))
                    continue;
                if (_transport != null && _transport.Exists()
                    && holdingPosition.DistanceTo(_transport.Position) < 4.0f)
                    continue;
                return holdingPosition;
            }
            return anchor;
        }

        private bool IsNearHandoffOfficer(Ped prisoner, IList<Ped> officers)
        {
            if (prisoner == null || !prisoner.Exists() || officers == null)
                return false;
            List<Ped> available = officers.Where(ped => ped != null && ped.Exists()).ToList();
            if (available.Count == 0)
                return false;
            int index = Math.Max(0, _prisoners.IndexOf(prisoner));
            Ped officer = available[index % available.Count];
            return officer.Position.DistanceTo(prisoner.Position) <= 4.0f;
        }

        private Vector3 PrisonHandoffPosition(Ped prisoner)
        {
            int index = Math.Max(0, _prisoners.IndexOf(prisoner));
            return ResolveRoadPosition(
                _prisonDestination + new Vector3(3.5f + index * 1.5f, 2.0f, 0f));
        }

        private void DriveTransportTo(Vector3 target, bool force)
        {
            if (_transportDriver == null || !_transportDriver.Exists()
                || _transport == null || !_transport.Exists())
                return;
            if (_playerOwnedTransport)
                return;
            // A scattered collection becomes Player-driven only after the
            // transport has physically delivered the van and the original
            // scene loading is complete. Keep the existing officer's route
            // task active for that first delivery leg.
            if (_dispatchGroupPlayerDrivenPickup
                && _phase != CustodyPhase.PreparingSceneTransport
                && _phase != CustodyPhase.SceneTransportEnRoute)
                return;
            DateTime now = DateTime.UtcNow;
            float moved = _transport.Position.DistanceTo(_lastTransportProgressPosition);
            if (moved >= 3f)
            {
                _lastTransportProgressPosition = _transport.Position;
                _lastTransportProgressAt = now;
            }
            if (!force && now < _lastDriverTaskAt.AddMilliseconds(DriverTaskRefreshMilliseconds))
                return;
            try
            {
                Vector3 roadTarget;
                if (IsDispatchCustodyTransport())
                {
                    if (_dispatchTransportRouteDestination == Vector3.Zero
                        || target.DistanceTo(_dispatchTransportRouteDestination) > 4f)
                    {
                        float routeDistance;
                        string routeFailure;
                        if (!TryResolveDispatchTransportDestination(
                            target, _transport.Position,
                            IsDispatchSceneRoadPickup(false)
                                ? DispatchSceneVehicleArrivalRadius : ArrivalRadius,
                            out roadTarget,
                            out routeDistance, out routeFailure))
                        {
                            _lastDriverTaskAt = now;
                            string failureKey = target + ";" + routeFailure;
                            if (!string.Equals(
                                failureKey, _lastDispatchTransportRouteTargetFailure,
                                StringComparison.Ordinal))
                            {
                                _lastDispatchTransportRouteTargetFailure = failureKey;
                                LogRuntime(
                                    "POLICE_CONVOY_TRANSPORT_ROUTE_TARGET_REJECTED",
                                    "Operation=" + _operationId
                                    + "; Target=" + target
                                    + "; Vehicle=" + _transport.Handle
                                    + "; Reason=" + routeFailure
                                    + "; TaskIssued=false; Teleport=false");
                            }
                            return;
                        }
                        _dispatchTransportRouteRoadTarget = roadTarget;
                        _dispatchTransportRouteDestination = target;
                        _lastDispatchTransportRouteTargetFailure = string.Empty;
                    }
                    roadTarget = _dispatchTransportRouteRoadTarget;
                }
                else
                    roadTarget = ResolveTransportRoadPosition(target, 0);
                bool dispatchCustodyTransport = IsDispatchCustodyTransport();
                if (dispatchCustodyTransport
                    && !PrepareTransportVehicleForDriving(
                        _transportDriver, now, "DispatchCustodyRoute"))
                {
                    _lastDriverTaskAt = now;
                    return;
                }

                if (dispatchCustodyTransport && !force
                    && _lastTransportRoadTaskTarget != Vector3.Zero
                    && _lastTransportRoadTaskTarget.DistanceTo(roadTarget) <= 8f)
                {
                    if (now >= _nextTransportDriveReadinessLogAt)
                    {
                        _nextTransportDriveReadinessLogAt = now.AddSeconds(10);
                        LogRuntime("POLICE_CONVOY_TRANSPORT_ROUTE_TASK_RETAINED",
                            "Operation=" + _operationId
                            + "; Vehicle=" + _transport.Handle
                            + "; Driver=" + _transportDriver.Handle
                            + "; RoadTarget=" + roadTarget
                            + "; Position=" + _transport.Position
                            + "; Speed=" + _transport.Speed.ToString("0.00", CultureInfo.InvariantCulture)
                            + "; ProgressMeters=" + _transport.Position.DistanceTo(
                                _lastTransportProgressPosition).ToString("0.0", CultureInfo.InvariantCulture)
                            + "; TaskRefreshSkipped=true; Reason=SameValidatedRoadTarget");
                    }
                    _lastDriverTaskAt = now;
                    return;
                }

                SetEmergencySignals(_transport, true);
                float stoppingDistance = IsDispatchSceneRoadPickup(false)
                    ? DispatchSceneVehicleArrivalRadius : 18f;
                if (dispatchCustodyTransport)
                {
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                        _transportDriver,
                        _transport,
                        roadTarget.X,
                        roadTarget.Y,
                        roadTarget.Z,
                        22.0f,
                        (int)EmergencyDrivingFlags,
                        stoppingDistance);
                    _lastTransportRoadTaskTarget = roadTarget;
                    LogRuntime("POLICE_CONVOY_TRANSPORT_ROUTE_TASK_ISSUED",
                        "Operation=" + _operationId
                        + "; Vehicle=" + _transport.Handle
                        + "; Driver=" + _transportDriver.Handle
                        + "; RoadTarget=" + roadTarget
                        + "; Position=" + _transport.Position
                        + "; Speed=" + _transport.Speed.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; PhysicalDriverSeatConfirmed=true"
                        + "; Task=TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE");
                }
                else
                {
                    _transportDriver.Task.DriveTo(_transport, roadTarget, 22f,
                        EmergencyDrivingFlags, stoppingDistance);
                    _lastTransportRoadTaskTarget = Vector3.Zero;
                }
                _lastDriverTaskAt = now;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DRIVER_TASK_FAILED", ex);
            }
        }

        private bool PrepareTransportVehicleForDriving(
            Ped driver,
            DateTime now,
            string routeKind)
        {
            if (driver == null || !driver.Exists() || driver.IsDead
                || _transport == null || !_transport.Exists())
                return false;

            int nativeDriverHandle;
            try
            {
                nativeDriverHandle = Function.Call<int>(
                    Hash.GET_PED_IN_VEHICLE_SEAT, _transport, -1, false);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DRIVER_SEAT_QUERY_FAILED", ex);
                return false;
            }

            if (nativeDriverHandle != driver.Handle)
            {
                string recovery = "NoEntryTaskIssued";
                if (nativeDriverHandle == 0
                    && now >= _nextTransportDriverSeatTaskAt)
                {
                    try
                    {
                        if (driver.IsInVehicle(_transport))
                        {
                            Function.Call(Hash.TASK_LEAVE_VEHICLE,
                                driver, _transport, 0);
                            recovery = "ExitPassengerSeatBeforeDriverEntry";
                        }
                        else if (!driver.IsInVehicle())
                        {
                            driver.Task.EnterVehicle(
                                _transport, VehicleSeat.Driver, 12000, 1.0f);
                            recovery = "PhysicalDriverSeatEntryTaskIssued";
                        }
                        else
                            recovery = "AssignedDriverInAnotherVehicle";
                        _nextTransportDriverSeatTaskAt = now.AddSeconds(3);
                    }
                    catch (Exception ex)
                    {
                        recovery = "DriverSeatTaskFailed";
                        _nextTransportDriverSeatTaskAt = now.AddSeconds(3);
                        LogException("POLICE_CONVOY_DRIVER_SEAT_TASK_FAILED", ex);
                    }
                }

                if (now >= _nextTransportDriveReadinessLogAt)
                {
                    _nextTransportDriveReadinessLogAt = now.AddSeconds(5);
                    LogRuntime("POLICE_CONVOY_TRANSPORT_DRIVE_BLOCKED",
                        "Operation=" + _operationId
                        + "; Vehicle=" + _transport.Handle
                        + "; Route=" + routeKind
                        + "; AssignedDriver=" + driver.Handle
                        + "; NativeDriverSeat=" + nativeDriverHandle
                        + "; DriverInTransport=" + driver.IsInVehicle(_transport)
                        + "; Position=" + _transport.Position
                        + "; Speed=" + _transport.Speed.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Recovery=" + recovery);
                }
                return false;
            }

            if (!_transport.IsDriveable)
            {
                if (now >= _nextTransportDriveReadinessLogAt)
                {
                    _nextTransportDriveReadinessLogAt = now.AddSeconds(5);
                    LogRuntime("POLICE_CONVOY_TRANSPORT_DRIVE_BLOCKED",
                        "Operation=" + _operationId
                        + "; Vehicle=" + _transport.Handle
                        + "; Route=" + routeKind
                        + "; Driver=" + driver.Handle
                        + "; NativeDriverSeat=" + nativeDriverHandle
                        + "; Driveable=false"
                        + "; Position=" + _transport.Position
                        + "; Speed=" + _transport.Speed.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Recovery=CustodyRetained");
                }
                return false;
            }

            try
            {
                Function.Call(Hash.FREEZE_ENTITY_POSITION, _transport, false);
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON,
                    _transport, true, true, false);
                Function.Call(Hash.SET_VEHICLE_BRAKE, _transport, false);
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, false);
                bool engineRunning = Function.Call<bool>(
                    Hash.GET_IS_VEHICLE_ENGINE_RUNNING, _transport);
                if (now >= _nextTransportDriveReadinessLogAt)
                {
                    _nextTransportDriveReadinessLogAt = now.AddSeconds(10);
                    LogRuntime("POLICE_CONVOY_TRANSPORT_DRIVE_READY",
                        "Operation=" + _operationId
                        + "; Vehicle=" + _transport.Handle
                        + "; Route=" + routeKind
                        + "; Driver=" + driver.Handle
                        + "; NativeDriverSeat=" + nativeDriverHandle
                        + "; DriverSeatConfirmed=true"
                        + "; Driveable=" + _transport.IsDriveable
                        + "; EngineRunning=" + engineRunning
                        + "; UnfreezeRequested=true"
                        + "; BrakeReleased=true; HandbrakeReleased=true"
                        + "; Position=" + _transport.Position
                        + "; Speed=" + _transport.Speed.ToString("0.00", CultureInfo.InvariantCulture));
                }
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_TRANSPORT_DRIVE_READINESS_FAILED", ex);
                return false;
            }
        }

        private bool TryRecoverStalledTransport(Vector3 target, DateTime now)
        {
            if (_transport == null || !_transport.Exists()
                || _transportDriver == null || !_transportDriver.Exists())
                return false;

            float moved = _transport.Position.DistanceTo(_lastTransportProgressPosition);
            if (moved >= 3f)
            {
                _lastTransportProgressPosition = _transport.Position;
                _lastTransportProgressAt = now;
                return false;
            }
            if (_lastTransportProgressAt == DateTime.MinValue)
            {
                _lastTransportProgressAt = now;
                _lastTransportProgressPosition = _transport.Position;
                return false;
            }
            if (now < _lastTransportProgressAt.AddSeconds(TransportRouteStallTimeoutSeconds))
                return false;
            if (_transportRouteRecoveryCount >= MaximumTransportRouteRecoveries)
                return false;

            double stalledSeconds = Math.Max(0.0,
                (now - _lastTransportProgressAt).TotalSeconds);
            bool dispatchCustodyTransport = IsDispatchCustodyTransport();

            try
            {
                if (dispatchCustodyTransport
                    && !PrepareTransportVehicleForDriving(
                        _transportDriver, now, "DispatchCustodyRecovery"))
                {
                    _transportRouteRecoveryCount++;
                    _lastTransportProgressPosition = _transport.Position;
                    _lastTransportProgressAt = now;
                    _lastDriverTaskAt = now;
                    LogRuntime("POLICE_CONVOY_TRANSPORT_RECOVERY_BLOCKED",
                        "Operation=" + _operationId
                        + "; Recovery=" + _transportRouteRecoveryCount
                        + "; Vehicle=" + _transport.Handle
                        + "; Driver=" + (_transportDriver == null
                            ? 0 : _transportDriver.Handle)
                        + "; Position=" + _transport.Position
                        + "; Speed=" + _transport.Speed.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Reason=DriverSeatOrVehicleReadinessUnconfirmed"
                        + "; CustodyRetained=true; Teleport=false");
                    return true;
                }

                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, false);
                SetEmergencySignals(_transport, true);
                // A station vehicle point can be adjacent to a blocked gate
                // or a different road level. Each bounded recovery therefore
                // tries one different nearby road target instead of repeating
                // the same impossible coordinate three times.
                Vector3 roadTarget;
                float routeDistance = -1f;
                string recoveryReason = string.Empty;
                if (dispatchCustodyTransport)
                {
                    if (!TryResolveDispatchTransportRecoveryRoadTarget(
                        target, _transportRouteRecoveryCount + 1,
                        out roadTarget, out routeDistance, out recoveryReason))
                    {
                        _transportRouteRecoveryCount++;
                        _lastTransportProgressPosition = _transport.Position;
                        _lastTransportProgressAt = now;
                        _lastDriverTaskAt = now;
                        LogRuntime(
                            "POLICE_CONVOY_TRANSPORT_ROUTE_RECOVERY_REJECTED",
                            "Operation=" + _operationId
                            + "; Recovery=" + _transportRouteRecoveryCount
                            + "; Target=" + target
                            + "; Position=" + _transport.Position
                            + "; VehicleHeading=" + _transport.Heading.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; DistanceToTarget=" + _transport.Position.DistanceTo(target).ToString("0.0", CultureInfo.InvariantCulture)
                        + "; StalledSeconds=" + stalledSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Reason=" + recoveryReason
                            + "; CustodyRetained=true; Teleport=false");
                        return true;
                    }
                }
                else
                    roadTarget = ResolveTransportRoadPosition(
                        target,
                        _transportRouteRecoveryCount + 1);

                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    _transportDriver,
                    _transport,
                    roadTarget.X,
                    roadTarget.Y,
                    roadTarget.Z,
                    22.0f,
                    (int)EmergencyDrivingFlags,
                    10.0f);
                _transportRouteRecoveryCount++;
                if (dispatchCustodyTransport)
                {
                    _dispatchTransportRouteRoadTarget = roadTarget;
                    _dispatchTransportRouteDestination = target;
                    _lastTransportRoadTaskTarget = roadTarget;
                }
                _lastTransportProgressPosition = _transport.Position;
                _lastTransportProgressAt = now;
                _lastDriverTaskAt = now;
                LogRuntime(
                    "POLICE_CONVOY_TRANSPORT_ROUTE_RECOVERY",
                    "Recovery=" + _transportRouteRecoveryCount + "; Target=" + target
                    + "; RoadTarget=" + roadTarget
                    + (dispatchCustodyTransport
                        ? "; Position=" + _transport.Position
                            + "; VehicleHeading=" + _transport.Heading.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; TargetBearing=" + HeadingToward(
                                _transport.Position, roadTarget, _transport.Heading).ToString("0.0", CultureInfo.InvariantCulture)
                            + "; HeadingDelta=" + HeadingDifference(
                                _transport.Heading, HeadingToward(
                                    _transport.Position, roadTarget, _transport.Heading)).ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Speed=" + _transport.Speed.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; DistanceToTarget=" + _transport.Position.DistanceTo(target).ToString("0.0", CultureInfo.InvariantCulture)
                            + "; ProgressMeters=" + moved.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; StalledSeconds=" + stalledSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; RouteDistance=" + routeDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; CustodyRetained=true; Teleport=false"
                        : string.Empty));
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_TRANSPORT_ROUTE_RECOVERY_FAILED", ex);
                _lastTransportProgressAt = now;
                return false;
            }
        }

        private void OrderSceneEscortGuard()
        {
            Ped target = CurrentDispatchPickupPrisoners(
                    ValidPrisoners().ToList())
                .OrderBy(prisoner => _transport == null || !_transport.Exists()
                    ? 0f : prisoner.Position.DistanceTo(_transport.Position))
                .FirstOrDefault();
            if (target == null)
                return;
            foreach (Ped officer in _sceneEscortOfficers.Where(ped =>
                ped != null && ped.Exists()
                && (_dispatchPickupStops.Count == 0
                    || _transport == null || !_transport.Exists()
                    || ped.Position.DistanceTo(_transport.Position)
                        <= MaximumDispatchSceneVehicleDistance)))
            {
                try
                {
                    Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, officer, target, 6000, true);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_ESCORT_GUARD_TASK_FAILED", ex);
                }
            }
        }

        private Vector3 TransportTarget(bool prisonTrip)
        {
            if (!prisonTrip)
            {
                if (IsDispatchSceneRoadPickup(false))
                    return _dispatchSceneVehicleAccessTarget;
                return _pickupTarget;
            }

            // Dispatch prison transfer starts from the physical outside spot
            // where the station accepted custody. The general station and
            // Police-car Garage coordinates are not custody handoff points.
            if (!_isRequestedConvoyActivity
                && _stationHandoffGroundPosition != Vector3.Zero)
                return _stationHandoffGroundPosition;

            return _stationDestination;
        }

        private bool IsDispatchSceneRoadPickup(bool prisonTrip)
        {
            return !prisonTrip
                && _dispatchCustodyOperation
                && !_isRequestedConvoyActivity
                && !_playerOwnedTransport
                && _dispatchSceneVehicleAccessTarget != Vector3.Zero
                && (_phase == CustodyPhase.PreparingSceneTransport
                    || _phase == CustodyPhase.SceneTransportEnRoute);
        }

        private Vector3 FindSafeStagingPosition(Ped player, Vector3 target, bool prisonTrip)
        {
            LSPDPoliceStationDefinition station = SelectedStation();
            bool preserveSeparateConvoyPlacement = prisonTrip && _isRequestedConvoyActivity;
            Vector3 stationSpawn = !preserveSeparateConvoyPlacement || station == null
                ? Vector3.Zero
                : new Vector3(station.VehicleX, station.VehicleY, station.VehicleZ);
            Vector3 candidate = prisonTrip ? stationSpawn : Vector3.Zero;
            float distance = prisonTrip
                ? Math.Max(55f, _settings.MinimumStagingDistance)
                : Math.Max(45f, _settings.MinimumStagingDistance * 0.60f);
            // Dispatch transport units stage behind the Player at a safe
            // distance and use the current scene or custody point as their
            // destination. This keeps custody vehicles out of the separate
            // Police-car Garage coordinate. The standalone Convoy request
            // retains its separate existing placement path.
            if (candidate == Vector3.Zero
                || candidate.DistanceTo(player.Position) < _settings.MinimumStagingDistance)
            {
                candidate = player.GetOffsetPosition(new Vector3(0f, -distance, 0f));
            }
            try
            {
                Vector3 street = World.GetNextPositionOnStreet(candidate);
                if (street.DistanceTo(player.Position) >= Math.Max(35f, distance * 0.75f)
                    && Math.Abs(street.Z - candidate.Z) <= MaximumRoadElevationDifference)
                    return street;
            }
            catch { }
            return candidate;
        }

        private bool IsDispatchCustodyTransport()
        {
            return _dispatchCustodyOperation
                && !_isRequestedConvoyActivity
                && !_playerOwnedTransport;
        }

        private bool TryFindDispatchTransportStagingPosition(
            Ped player,
            Vector3 target,
            bool prisonTrip,
            out Vector3 spawn,
            out float laneHeading,
            out Vector3 routeRoadTarget,
            out float routeDistance,
            out int candidateIndex,
            out string failure,
            ISet<int> rejectedGroundCandidates = null)
        {
            spawn = Vector3.Zero;
            laneHeading = 0f;
            routeRoadTarget = Vector3.Zero;
            routeDistance = -1f;
            candidateIndex = -1;
            failure = "NoValidatedStagingCandidate";
            if (player == null || !player.Exists() || target == Vector3.Zero)
            {
                failure = "PlayerOrCustodyTargetUnavailable";
                return false;
            }

            Vector3 destinationRoad;
            float destinationHeading;
            string destinationRoadEvidence;
            if (!TryGetValidatedDispatchTransportRoadNode(
                target,
                target.Z,
                DispatchTransportTargetRoadSnapRadius,
                out destinationRoad,
                out destinationHeading,
                out _,
                out destinationRoadEvidence))
            {
                failure = "NoValidatedCustodyRoadTarget"
                    + "; TargetRoadValidation=" + destinationRoadEvidence
                    + "; CruiserFallbackEligible=false";
                return false;
            }
            float maximumRoadTargetDistance = !prisonTrip
                && IsDispatchSceneRoadPickup(false)
                ? DispatchSceneVehicleArrivalRadius : ArrivalRadius;
            if (destinationRoad.DistanceTo(target) > maximumRoadTargetDistance)
            {
                failure = "CustodyRoadTargetOutsidePhysicalArrivalRadius"
                    + "; Distance=" + destinationRoad.DistanceTo(target).ToString(
                        "0.0", CultureInfo.InvariantCulture)
                    + "; Maximum=" + maximumRoadTargetDistance;
                return false;
            }

            float configuredMinimumDistance = _settings == null
                ? 70f : Math.Max(35f, _settings.MinimumStagingDistance);
            float stagingDistance = Math.Max(45f,
                configuredMinimumDistance * 0.60f);
            float minimumPlayerDistance = Math.Max(35f,
                stagingDistance * 0.75f);
            Vector3[] queries = BuildDispatchTransportStagingQueries(
                player.Position, player.Heading, stagingDistance);
            int roadRejected = 0;
            int alleyRejected = 0;
            int clearanceRejected = 0;
            int routeRejected = 0;
            int geometryRejected = 0;

            for (int i = 0; i < queries.Length; i++)
            {
                int candidateNumber = i + 1;
                if (rejectedGroundCandidates != null
                    && rejectedGroundCandidates.Contains(candidateNumber))
                    continue;
                Vector3 roadNode;
                float heading;
                float snapDistance;
                if (!TryGetValidatedDispatchTransportRoadNode(
                    queries[i],
                    player.Position.Z,
                    DispatchTransportRoadSnapRadius,
                    out roadNode,
                    out heading,
                    out snapDistance))
                {
                    roadRejected++;
                    continue;
                }

                string streetName = GetDispatchTransportStreetName(roadNode);
                // A labeled alley is still a road node in GTA. Do not choose
                // it as the transport's starting point when another bounded
                // candidate can use the public street network.
                if (!IsDispatchTransportRoadNameUsable(streetName))
                {
                    alleyRejected++;
                    continue;
                }

                float playerDistance = roadNode.DistanceTo(player.Position);
                float targetDistance = roadNode.DistanceTo(target);
                float elevationDifference = Math.Abs(
                    roadNode.Z - player.Position.Z);
                bool clear = HasDispatchTransportSpawnClearance(
                    roadNode, player, null);
                if (!clear)
                {
                    clearanceRejected++;
                    continue;
                }

                float directDistance = roadNode.DistanceTo(destinationRoad);
                float measuredRouteDistance;
                try
                {
                    measuredRouteDistance = World.CalculateTravelDistance(
                        roadNode, destinationRoad);
                }
                catch
                {
                    measuredRouteDistance = -1f;
                }

                if (!IsDispatchTransportStagingCandidateUsable(
                    roadNode, heading, playerDistance, targetDistance,
                    elevationDifference, measuredRouteDistance,
                    directDistance, minimumPlayerDistance,
                    MaximumNearbyTransportCustodyRadius, true, clear))
                {
                    if (!IsDispatchTransportRouteDistanceUsable(
                        directDistance, measuredRouteDistance))
                        routeRejected++;
                    else
                        geometryRejected++;
                    continue;
                }

                spawn = roadNode;
                laneHeading = heading;
                routeRoadTarget = destinationRoad;
                routeDistance = measuredRouteDistance;
                candidateIndex = candidateNumber;
                failure = string.Empty;
                return true;
            }

            failure = "NoCandidatePassedRoadClearanceAndRouteChecks"
                + "; RoadRejected=" + roadRejected
                + "; AlleyStreetRejected=" + alleyRejected
                + "; ClearanceRejected=" + clearanceRejected
                + "; RouteRejected=" + routeRejected
                + "; GeometryRejected=" + geometryRejected;
            return false;
        }

        private static bool TryGetValidatedDispatchTransportRoadNode(
            Vector3 query,
            float referenceElevation,
            float maximumSnapDistance,
            out Vector3 roadPosition,
            out float roadHeading,
            out float snapDistance,
            out string validationEvidence)
        {
            roadPosition = Vector3.Zero;
            roadHeading = 0f;
            snapDistance = float.MaxValue;
            validationEvidence = "Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING/IS_POINT_ON_ROAD";
            if (query == Vector3.Zero || maximumSnapDistance <= 0f)
            {
                validationEvidence += "; FailedGate=QueryUnavailable";
                return false;
            }

            try
            {
                using (OutputArgument nodeOutput = new OutputArgument())
                using (OutputArgument headingOutput = new OutputArgument())
                {
                    bool found = Function.Call<bool>(
                        Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        query.X,
                        query.Y,
                        query.Z,
                        nodeOutput,
                        headingOutput,
                        1,
                        3.0f,
                        0);
                    if (!found)
                    {
                        validationEvidence = "NativeNodeFound=false; FailedGate=RoadNodeUnavailable"
                            + "; Query=" + query;
                        string closestNodeFailure = validationEvidence;
                        return TryGetDispatchTransportStreetNodeFallback(
                            query, referenceElevation, maximumSnapDistance,
                            closestNodeFailure, out roadPosition, out roadHeading,
                            out snapDistance, out validationEvidence);
                    }
                    else
                    {
                        Vector3 node = nodeOutput.GetResult<Vector3>();
                        float heading = headingOutput.GetResult<float>();
                        float distance = node.DistanceTo(query);
                        float elevationDifference = Math.Abs(node.Z - referenceElevation);
                        bool nodePositionValid = IsFiniteDispatchTransportPosition(node);
                        bool headingValid = !float.IsNaN(heading)
                            && !float.IsInfinity(heading);
                        bool snapValid = !float.IsNaN(distance)
                            && !float.IsInfinity(distance)
                            && distance <= maximumSnapDistance;
                        bool elevationValid = !float.IsNaN(elevationDifference)
                            && !float.IsInfinity(elevationDifference)
                            && elevationDifference <= MaximumRoadElevationDifference;
                        bool onRoad = nodePositionValid
                            && Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                                node.X, node.Y, node.Z, 0);
                        List<string> failedGates = new List<string>();
                        if (!nodePositionValid) failedGates.Add("RoadNodePositionInvalid");
                        if (!headingValid) failedGates.Add("RoadHeadingInvalid");
                        if (!snapValid) failedGates.Add("RoadNodeSnapExceeded");
                        if (!elevationValid) failedGates.Add("RoadElevationExceeded");
                        if (!onRoad) failedGates.Add("RoadNodeNotOnRoad");
                        validationEvidence = "NativeNodeFound=true"
                            + "; Query=" + query
                            + "; Node=" + node
                            + "; Heading=" + heading.ToString(
                                "0.0", CultureInfo.InvariantCulture)
                            + "; SnapDistance=" + distance.ToString(
                                "0.00", CultureInfo.InvariantCulture)
                            + "; MaximumSnap=" + maximumSnapDistance.ToString(
                                "0.00", CultureInfo.InvariantCulture)
                            + "; ElevationDelta=" + elevationDifference.ToString(
                                "0.00", CultureInfo.InvariantCulture)
                            + "; MaximumElevationDelta=" + MaximumRoadElevationDifference.ToString(
                                "0.00", CultureInfo.InvariantCulture)
                            + "; IsPointOnRoad=" + onRoad
                            + "; FailedGates=" + (failedGates.Count == 0
                                ? "none" : string.Join(",", failedGates));
                        if (failedGates.Count == 0)
                        {
                            roadPosition = node;
                            roadHeading = heading;
                            snapDistance = distance;
                            return true;
                        }
                        string closestNodeFailure = validationEvidence;
                        return TryGetDispatchTransportStreetNodeFallback(
                            query, referenceElevation, maximumSnapDistance,
                            closestNodeFailure, out roadPosition, out roadHeading,
                            out snapDistance, out validationEvidence);
                    }
                }
            }
            catch (Exception ex)
            {
                validationEvidence = "FailedGate=RoadNodeProbeException; Exception="
                    + ex.GetType().Name + "; Query=" + query;
                string closestNodeFailure = validationEvidence;
                return TryGetDispatchTransportStreetNodeFallback(
                    query, referenceElevation, maximumSnapDistance,
                    closestNodeFailure, out roadPosition, out roadHeading,
                    out snapDistance, out validationEvidence);
            }
        }

        private static bool TryGetDispatchTransportStreetNodeFallback(
            Vector3 query,
            float referenceElevation,
            float maximumSnapDistance,
            string closestNodeEvidence,
            out Vector3 roadPosition,
            out float roadHeading,
            out float snapDistance,
            out string validationEvidence)
        {
            roadPosition = Vector3.Zero;
            roadHeading = 0f;
            snapDistance = float.MaxValue;

            // The scene-access resolver already accepts GTA's SHVDN street-node
            // result after applying these same snap/elevation limits. Repeat
            // that bounded fallback here so a later strict recheck does not
            // reject an access node solely because IS_POINT_ON_ROAD returned a
            // false negative for the same valid street node. Callers still
            // require route connectivity, clearance, and a fresh grounded
            // vehicle-position check before transport can proceed.
            Vector3 bestStreetNode = Vector3.Zero;
            float bestStreetHeading = 0f;
            float bestStreetDistance = float.MaxValue;
            int streetQueries = 0;
            int streetRejected = 0;
            string firstStreetFailure = string.Empty;
            foreach (Vector3 queryOffset in DispatchSceneRoadNodeQueryOffsets)
            {
                streetQueries++;
                try
                {
                    float heading;
                    Vector3 node = World.GetNextPositionOnStreetWithHeading(
                        query + queryOffset, out heading);
                    float distance = node.DistanceTo(query);
                    if (!IsDispatchTransportStreetNodeCandidateUsable(
                        query, node, heading, referenceElevation,
                        maximumSnapDistance))
                    {
                        streetRejected++;
                        if (firstStreetFailure.Length == 0)
                            firstStreetFailure = "OffsetIndex=" + streetQueries
                                + "; Node=" + node
                                + "; Heading=" + heading.ToString(
                                    "0.0", CultureInfo.InvariantCulture)
                                + "; SnapDistance=" + distance.ToString(
                                    "0.00", CultureInfo.InvariantCulture)
                                + "; ElevationDelta=" + Math.Abs(
                                    node.Z - referenceElevation).ToString(
                                        "0.00", CultureInfo.InvariantCulture);
                        continue;
                    }

                    if (distance < bestStreetDistance)
                    {
                        bestStreetNode = node;
                        bestStreetHeading = heading;
                        bestStreetDistance = distance;
                    }
                }
                catch (Exception ex)
                {
                    streetRejected++;
                    if (firstStreetFailure.Length == 0)
                        firstStreetFailure = "OffsetIndex=" + streetQueries
                            + "; Exception=" + ex.GetType().Name;
                }
            }

            if (bestStreetNode != Vector3.Zero)
            {
                roadPosition = bestStreetNode;
                roadHeading = bestStreetHeading;
                snapDistance = bestStreetDistance;
                validationEvidence = "RoadSource=World.GetNextPositionOnStreetWithHeading"
                    + "; NativeClosestRejected={" + closestNodeEvidence + "}"
                    + "; AlternateStreetQueries=" + streetQueries
                    + "; AlternateStreetRejected=" + streetRejected
                    + "; Query=" + query
                    + "; Node=" + bestStreetNode
                    + "; Heading=" + bestStreetHeading.ToString(
                        "0.0", CultureInfo.InvariantCulture)
                    + "; SnapDistance=" + bestStreetDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; MaximumSnap=" + maximumSnapDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; ElevationDelta=" + Math.Abs(
                        bestStreetNode.Z - referenceElevation).ToString(
                            "0.00", CultureInfo.InvariantCulture)
                    + "; MaximumElevationDelta=" + MaximumRoadElevationDifference.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; RouteClearanceAndGroundedRevalidationRequired=true";
                return true;
            }

            validationEvidence = closestNodeEvidence
                + "; AlternateStreetSource=World.GetNextPositionOnStreetWithHeading"
                + "; AlternateStreetQueries=" + streetQueries
                + "; AlternateStreetRejected=" + streetRejected
                + "; FirstAlternateFailure=" + (firstStreetFailure.Length == 0
                    ? "none" : firstStreetFailure)
                + "; FailedGate=NoValidatedRoadNode";
            return false;
        }

        internal static bool IsDispatchTransportStreetNodeCandidateUsable(
            Vector3 query,
            Vector3 candidate,
            float heading,
            float referenceElevation,
            float maximumSnapDistance)
        {
            if (!IsFiniteDispatchTransportPosition(query)
                || !IsFiniteDispatchTransportPosition(candidate)
                || maximumSnapDistance <= 0f
                || float.IsNaN(heading) || float.IsInfinity(heading)
                || float.IsNaN(referenceElevation) || float.IsInfinity(referenceElevation))
                return false;

            float distance = candidate.DistanceTo(query);
            float elevationDifference = Math.Abs(candidate.Z - referenceElevation);
            return !float.IsNaN(distance) && !float.IsInfinity(distance)
                && distance <= maximumSnapDistance
                && !float.IsNaN(elevationDifference)
                && !float.IsInfinity(elevationDifference)
                && elevationDifference <= MaximumRoadElevationDifference;
        }

        private static bool IsFiniteDispatchTransportPosition(Vector3 position)
        {
            return position != Vector3.Zero
                && !float.IsNaN(position.X) && !float.IsInfinity(position.X)
                && !float.IsNaN(position.Y) && !float.IsInfinity(position.Y)
                && !float.IsNaN(position.Z) && !float.IsInfinity(position.Z);
        }

        private static bool TryGetValidatedDispatchTransportRoadNode(
            Vector3 query,
            float referenceElevation,
            float maximumSnapDistance,
            out Vector3 roadPosition,
            out float roadHeading,
            out float snapDistance)
        {
            string validationEvidence;
            return TryGetValidatedDispatchTransportRoadNode(
                query, referenceElevation, maximumSnapDistance,
                out roadPosition, out roadHeading, out snapDistance,
                out validationEvidence);
        }

        private static bool HasDispatchTransportSpawnClearance(
            Vector3 position,
            Ped player,
            Vehicle ownedTransport)
        {
            foreach (Vehicle nearby in World.GetNearbyVehicles(
                position, DispatchTransportVehicleClearanceRadius))
            {
                if (nearby == null || !nearby.Exists()
                    || (ownedTransport != null
                        && nearby.Handle == ownedTransport.Handle))
                    continue;
                if (nearby.Position.DistanceTo(position)
                    < DispatchTransportVehicleClearanceRadius)
                    return false;
            }

            foreach (Ped nearby in World.GetNearbyPeds(
                position, DispatchTransportPedClearanceRadius))
            {
                if (nearby == null || !nearby.Exists()
                    || (player != null && nearby.Handle == player.Handle)
                    || nearby.IsInVehicle())
                    continue;
                if (nearby.Position.DistanceTo(position)
                    < DispatchTransportPedClearanceRadius)
                    return false;
            }
            return true;
        }

        private static string GetDispatchTransportStreetName(Vector3 position)
        {
            try { return World.GetStreetName(position) ?? string.Empty; }
            catch { return string.Empty; }
        }

        internal static bool IsDispatchTransportRoadNameUsable(string streetName)
        {
            return string.IsNullOrWhiteSpace(streetName)
                || streetName.IndexOf("alley", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsDispatchTransportSpawnPlacementValid(
            Vehicle vehicle,
            Vector3 expectedRoadNode,
            Vector3 routeRoadTarget,
            float laneHeading,
            Ped player,
            out string reason)
        {
            reason = "";
            if (vehicle == null || !vehicle.Exists())
            {
                reason = "VehicleMissingAfterGroundPlacement";
                return false;
            }

            Vector3 actual = vehicle.Position;
            Vector3 revalidatedRoadNode;
            float revalidatedLaneHeading;
            float roadNodeSnapDistance;
            if (!TryGetValidatedDispatchTransportRoadNode(
                actual, expectedRoadNode.Z,
                DispatchTransportVehicleOriginAboveRoadLimit + 0.5f,
                out revalidatedRoadNode, out revalidatedLaneHeading,
                out roadNodeSnapDistance))
            {
                reason = "GroundedOriginCouldNotReconfirmRoadNode";
                return false;
            }

            float directRouteDistance = revalidatedRoadNode.DistanceTo(routeRoadTarget);
            float measuredRouteDistance;
            try
            {
                measuredRouteDistance = World.CalculateTravelDistance(
                    revalidatedRoadNode, routeRoadTarget);
            }
            catch
            {
                measuredRouteDistance = -1f;
            }
            bool clearance = HasDispatchTransportSpawnClearance(
                actual, player, vehicle);
            bool routeUsable = IsDispatchTransportRouteDistanceUsable(
                directRouteDistance, measuredRouteDistance);
            if (IsDispatchTransportGroundPlacementEvidenceValid(
                expectedRoadNode, actual, revalidatedRoadNode,
                laneHeading, vehicle.Heading, revalidatedLaneHeading,
                clearance, routeUsable))
                return true;

            float horizontalDisplacement = HorizontalDistance(actual, expectedRoadNode);
            float originAboveRoad = actual.Z - revalidatedRoadNode.Z;
            float roadNodeHorizontalDrift = HorizontalDistance(
                expectedRoadNode, revalidatedRoadNode);
            float laneDifference = HeadingDifference(laneHeading, revalidatedLaneHeading);
            float vehicleHeadingDifference = HeadingDifference(
                vehicle.Heading, revalidatedLaneHeading);
            if (horizontalDisplacement > DispatchTransportGroundedHorizontalTolerance)
                reason = "GroundPlacementDisplacedFromValidatedRoadNode";
            else if (Math.Abs(originAboveRoad) > DispatchTransportVehicleOriginAboveRoadLimit)
                reason = "VehicleOriginNotNearRoadSurface";
            else if (roadNodeHorizontalDrift > DispatchTransportGroundedHorizontalTolerance
                || Math.Abs(revalidatedRoadNode.Z - expectedRoadNode.Z)
                    > DispatchTransportRoadNodeHeightTolerance)
                reason = "FreshRoadNodeDoesNotMatchStagedNode";
            else if (laneDifference > DispatchTransportLaneHeadingTolerance
                || vehicleHeadingDifference > DispatchTransportLaneHeadingTolerance)
                reason = "GroundPlacementLaneHeadingMismatch";
            else if (!routeUsable)
                reason = "GroundPlacementRouteNoLongerConnected";
            else if (!clearance)
                reason = "GroundPlacementClearanceLost";
            else
                reason = "GroundPlacementValidationFailed";
            return false;
        }

        internal static bool IsDispatchTransportGroundPlacementEvidenceValid(
            Vector3 expectedRoadNode,
            Vector3 actualVehicleOrigin,
            Vector3 revalidatedRoadNode,
            float requestedLaneHeading,
            float vehicleHeading,
            float revalidatedLaneHeading,
            bool clearance,
            bool routeUsable)
        {
            if (expectedRoadNode == Vector3.Zero
                || actualVehicleOrigin == Vector3.Zero
                || revalidatedRoadNode == Vector3.Zero
                || !IsFinite(requestedLaneHeading)
                || !IsFinite(vehicleHeading)
                || !IsFinite(revalidatedLaneHeading))
                return false;

            float originAboveRoad = actualVehicleOrigin.Z - revalidatedRoadNode.Z;
            return HorizontalDistance(actualVehicleOrigin, expectedRoadNode)
                    <= DispatchTransportGroundedHorizontalTolerance
                && originAboveRoad <= DispatchTransportVehicleOriginAboveRoadLimit
                && originAboveRoad >= -0.5f
                && HorizontalDistance(expectedRoadNode, revalidatedRoadNode)
                    <= DispatchTransportGroundedHorizontalTolerance
                && Math.Abs(revalidatedRoadNode.Z - expectedRoadNode.Z)
                    <= DispatchTransportRoadNodeHeightTolerance
                && HeadingDifference(requestedLaneHeading, revalidatedLaneHeading)
                    <= DispatchTransportLaneHeadingTolerance
                && HeadingDifference(vehicleHeading, revalidatedLaneHeading)
                    <= DispatchTransportLaneHeadingTolerance
                && clearance && routeUsable;
        }

        private static float HorizontalDistance(Vector3 first, Vector3 second)
        {
            float x = first.X - second.X;
            float y = first.Y - second.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static Vector3[] BuildDispatchTransportStagingQueries(
            Vector3 playerPosition,
            float playerHeading,
            float configuredMinimumDistance)
        {
            float distance = Math.Max(45f,
                configuredMinimumDistance * 0.60f);
            float diagonal = distance * 0.70710678f;
            Vector3[] localOffsets =
            {
                new Vector3(0f, -distance, 0f),
                new Vector3(-diagonal, -diagonal, 0f),
                new Vector3(diagonal, -diagonal, 0f),
                new Vector3(-distance, 0f, 0f),
                new Vector3(distance, 0f, 0f),
                new Vector3(-diagonal, diagonal, 0f),
                new Vector3(diagonal, diagonal, 0f),
                new Vector3(0f, distance, 0f)
            };
            double radians = (double)playerHeading * Math.PI / 180.0;
            float sin = (float)Math.Sin(radians);
            float cos = (float)Math.Cos(radians);
            Vector3[] queries = new Vector3[
                MaximumDispatchTransportStagingCandidates];
            for (int i = 0; i < queries.Length; i++)
            {
                Vector3 local = localOffsets[i];
                queries[i] = new Vector3(
                    playerPosition.X + local.X * cos + local.Y * sin,
                    playerPosition.Y - local.X * sin + local.Y * cos,
                    playerPosition.Z);
            }
            return queries;
        }

        internal static bool IsDispatchTransportRouteDistanceUsable(
            float directDistance,
            float routeDistance)
        {
            if (float.IsNaN(directDistance) || float.IsInfinity(directDistance)
                || directDistance < 0f || float.IsNaN(routeDistance)
                || float.IsInfinity(routeDistance) || routeDistance <= 0f)
                return false;
            float maximumRouteDistance = Math.Min(
                MaximumDispatchTransportRouteDistance,
                Math.Max(220f, directDistance * 6f + 120f));
            return routeDistance >= Math.Max(0f, directDistance - 15f)
                && routeDistance <= maximumRouteDistance;
        }

        internal static bool IsDispatchTransportStagingCandidateUsable(
            Vector3 roadPosition,
            float laneHeading,
            float playerDistance,
            float targetDistance,
            float elevationDifference,
            float routeDistance,
            float directDistance,
            float minimumPlayerDistance,
            float maximumTargetDistance,
            bool roadValidated,
            bool clearanceAvailable)
        {
            return roadValidated && clearanceAvailable
                && roadPosition != Vector3.Zero
                && !float.IsNaN(laneHeading) && !float.IsInfinity(laneHeading)
                && playerDistance >= minimumPlayerDistance
                && targetDistance <= maximumTargetDistance
                && elevationDifference <= MaximumRoadElevationDifference
                && IsDispatchTransportRouteDistanceUsable(
                    directDistance, routeDistance);
        }

        private static bool TryResolveDispatchTransportDestination(
            Vector3 target,
            Vector3 from,
            float maximumTargetDistance,
            out Vector3 roadTarget,
            out float routeDistance,
            out string reason)
        {
            roadTarget = Vector3.Zero;
            routeDistance = -1f;
            int roadNodeRejected = 0;
            int targetDistanceRejected = 0;
            int routeRejected = 0;
            Vector3[] queries =
            {
                target,
                target + new Vector3(24f, 0f, 0f),
                target + new Vector3(-24f, 0f, 0f),
                target + new Vector3(0f, 24f, 0f),
                target + new Vector3(0f, -24f, 0f)
            };
            float bestRouteDistance = float.MaxValue;

            foreach (Vector3 query in queries)
            {
                Vector3 candidate;
                float candidateHeading;
                float snapDistance;
                if (!TryGetValidatedDispatchTransportRoadNode(
                    query, target.Z, DispatchTransportTargetRoadSnapRadius,
                    out candidate, out candidateHeading, out snapDistance))
                {
                    roadNodeRejected++;
                    continue;
                }
                if (candidate.DistanceTo(target)
                    > maximumTargetDistance)
                {
                    targetDistanceRejected++;
                    continue;
                }

                float directDistance = from.DistanceTo(candidate);
                float measuredRouteDistance;
                try
                {
                    measuredRouteDistance = World.CalculateTravelDistance(
                        from, candidate);
                }
                catch
                {
                    measuredRouteDistance = -1f;
                }
                if (!IsDispatchTransportRouteDistanceUsable(
                    directDistance, measuredRouteDistance))
                {
                    routeRejected++;
                    continue;
                }
                if (measuredRouteDistance >= bestRouteDistance)
                    continue;

                bestRouteDistance = measuredRouteDistance;
                roadTarget = candidate;
                routeDistance = measuredRouteDistance;
            }

            if (roadTarget != Vector3.Zero)
            {
                reason = string.Empty;
                return true;
            }

            reason = "NoReachableDispatchRoadTarget"
                + "; RoadNodeRejected=" + roadNodeRejected
                + "; TargetDistanceRejected=" + targetDistanceRejected
                + "; RouteRejected=" + routeRejected;
            return false;
        }

        private bool TryResolveDispatchTransportRecoveryRoadTarget(
            Vector3 target,
            int recoveryIndex,
            out Vector3 roadTarget,
            out float routeDistance,
            out string reason)
        {
            roadTarget = Vector3.Zero;
            routeDistance = -1f;
            int roadNodeRejected = 0;
            int duplicateNodeRejected = 0;
            int routeRejected = 0;
            Vector3[] offsets =
            {
                new Vector3(38f, 0f, 0f),
                new Vector3(-38f, 0f, 0f),
                new Vector3(0f, 38f, 0f),
                new Vector3(0f, -38f, 0f)
            };
            int first = Math.Max(0, recoveryIndex - 1) % offsets.Length;
            float bestRouteDistance = float.MaxValue;

            for (int attempt = 0; attempt < offsets.Length; attempt++)
            {
                int index = (first + attempt) % offsets.Length;
                Vector3 query = target + offsets[index];
                Vector3 candidate;
                float candidateHeading;
                float snapDistance;
                if (!TryGetValidatedDispatchTransportRoadNode(
                    query, target.Z, DispatchTransportTargetRoadSnapRadius,
                    out candidate, out candidateHeading, out snapDistance)
                    || candidate.DistanceTo(target)
                        > MaximumNearbyTransportCustodyRadius)
                {
                    roadNodeRejected++;
                    continue;
                }
                if (!IsDispatchTransportRoadNameUsable(
                    GetDispatchTransportStreetName(candidate)))
                {
                    roadNodeRejected++;
                    continue;
                }

                if (_dispatchTransportRouteRoadTarget != Vector3.Zero
                    && candidate.DistanceTo(_dispatchTransportRouteRoadTarget) < 8f)
                {
                    duplicateNodeRejected++;
                    continue;
                }

                float directDistance = _transport.Position.DistanceTo(candidate);
                float measuredRouteDistance;
                try
                {
                    measuredRouteDistance = World.CalculateTravelDistance(
                        _transport.Position, candidate);
                }
                catch
                {
                    measuredRouteDistance = -1f;
                }
                if (!IsDispatchTransportRouteDistanceUsable(
                    directDistance, measuredRouteDistance))
                {
                    routeRejected++;
                    continue;
                }
                if (measuredRouteDistance >= bestRouteDistance)
                    continue;

                bestRouteDistance = measuredRouteDistance;
                roadTarget = candidate;
                routeDistance = measuredRouteDistance;
            }

            if (roadTarget != Vector3.Zero)
            {
                reason = string.Empty;
                return true;
            }

            reason = "NoDistinctNavigableRoadTarget"
                + "; RoadNodeRejected=" + roadNodeRejected
                + "; RepeatedNodeRejected=" + duplicateNodeRejected
                + "; RouteRejected=" + routeRejected;
            return false;
        }

        private static float HeadingDifference(float first, float second)
        {
            float difference = Math.Abs(first - second) % 360f;
            return difference > 180f ? 360f - difference : difference;
        }

        private LSPDPoliceStationDefinition SelectedStation()
        {
            if (_profile == null)
                return null;
            return _profile.FindStation(_profile.Selection.StationId);
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

        private string VehicleName(string preferred, string fallback)
        {
            if (_profile != null)
            {
                LSPDPoliceVehicleDefinition vehicle = _profile.FindVehicle(preferred);
                if (vehicle != null && !string.IsNullOrWhiteSpace(vehicle.ModelName))
                    return vehicle.ModelName;
                vehicle = _profile.FindVehicle(fallback);
                if (vehicle != null && !string.IsNullOrWhiteSpace(vehicle.ModelName))
                    return vehicle.ModelName;
            }
            return preferred;
        }

        private string OfficerModelName()
        {
            if (_profile != null)
            {
                LSPDPoliceModelDefinition officer = _profile.FindPed("lspd_male_patrol");
                if (officer != null && !string.IsNullOrWhiteSpace(officer.ModelName))
                    return officer.ModelName;
                officer = _profile.PedModels.FirstOrDefault();
                if (officer != null && !string.IsNullOrWhiteSpace(officer.ModelName))
                    return officer.ModelName;
            }
            return "s_m_y_cop_01";
        }

        private string PrisonOfficerModelName()
        {
            if (_profile != null)
            {
                LSPDPoliceModelDefinition officer = _profile.FindPed("prison_guard");
                if (officer != null && !string.IsNullOrWhiteSpace(officer.ModelName))
                    return officer.ModelName;
            }
            return OfficerModelName();
        }

        private static void PrepareOfficer(Ped officer)
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
                // Escort and receiving officers remain owned until physical
                // handoff completes; this is the same ownership boundary as
                // the prisoner and transport vehicle.
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, officer, true, true);
                Function.Call(Hash.SET_PED_AS_COP, officer, true);
                Function.Call(Hash.SET_PED_KEEP_TASK, officer, true);
                Function.Call(Hash.SET_DRIVER_ABILITY, officer, 1.0f);
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, officer, 0.15f);
            }
            catch { }
        }

        private void Complete(string reason, bool terminalAudioAlreadyReported)
        {
            if (!Active)
                return;
            if (!terminalAudioAlreadyReported)
                ReportTerminal("lsimmersivelife.police.transport.completed", "completed");
            RestoreAllConvoyInjuryStates("PhysicalCustodyCompleted");
            foreach (Ped prisoner in _prisoners.Where(ped => ped != null && ped.Exists()))
            {
                if (_dispatchCustodyOperation)
                    continue;
                try
                {
                    // Active custody protection belongs only to the Convoy
                    // owner. Once physical handoff is confirmed, restore the
                    // prior damage behavior before deferred cleanup.
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        prisoner, false, false, false, false, false, false, false, false);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_PROTECTION_RELEASE_FAILED", ex);
                }
            }
            RestoreAllCustodyInvincibility();
            QueueOperationCleanup();
            CleanupCustodyBlips();
            CleanupWaypoint();
            _state = LSPDDispatchState.Completed;
            SetPhase(CustodyPhase.Completed);
            _transport = null;
            _transportDriver = null;
            _prisoners.Clear();
            _sceneEscortOfficers.Clear();
            _stationHandoffOfficers.Clear();
            _prisonHandoffOfficers.Clear();
            _routeThreatPeds.Clear();
            _routeThreatVehicle = null;
            LogRuntime("POLICE_CUSTODY_COMPLETED", reason);
            Notify("~g~POLICE CUSTODY COMPLETE~s~\n" + reason);
        }

        private string Fail(string reason, bool recoverable)
        {
            if (!Active && _phase != CustodyPhase.Failed)
                return reason;
            if (_dispatchClosedAfterCrewCustodyHandoff)
                recoverable = false;
            _failedIsRecoverable = recoverable;
            _failedAfterStationHandoff = recoverable && IsPrisonTransferPhase(_phase);
            _lastFailureReason = reason ?? string.Empty;
            ReportTerminal("lsimmersivelife.police.transport.cancelled", recoverable ? "recoverable-failure" : "failed");
            HashSet<int> preserveDispatchCustodyHandles = recoverable
                && _dispatchCustodyOperation && !_isRequestedConvoyActivity
                && !_playerOwnedTransport && !_failedAfterStationHandoff
                    ? new HashSet<int>(ValidPrisoners().Select(prisoner => prisoner.Handle))
                    : null;
            DetachPrisonersForRecovery(preserveDispatchCustodyHandles);
            CleanupCustodyBlips();
            QueueRouteThreatCleanup();
            if (recoverable)
            {
                // Give an in-vehicle prisoner a real exit opportunity and keep
                // the owned transport around through the normal grace cleanup.
                // Deleting it immediately after TASK_LEAVE_VEHICLE cancels the
                // visible exit and caused the old "missing prisoner" bug.
                QueueCurrentTransportCleanup(preserveDispatchCustodyHandles);
                _transport = null;
                _transportDriver = null;
            }
            else
            {
                // Preserve the failed scene while the player is still in it.
                // The existing deferred queue removes owned Convoy entities
                // only after the player has left the configured cleanup radius.
                RestoreAllConvoyInjuryStates("NonRecoverableCustodyFailure");
                RestoreAllCustodyInvincibility();
                QueueOperationCleanup();
                _transport = null;
                _transportDriver = null;
            }
            CleanupWaypoint();
            _state = LSPDDispatchState.Failed;
            SetPhase(CustodyPhase.Failed);
            LogStateFailure("POLICE_CUSTODY_FAILED",
                "Reason=" + _lastFailureReason + "; Prisoners=" + DescribePrisoners());
            Notify("~r~POLICE CUSTODY~s~\n" + _lastFailureReason);
            return _lastFailureReason;
        }

        private void QueueOperationCleanup()
        {
            DateTime now = DateTime.UtcNow;
            foreach (Ped prisoner in _prisoners
                .Where(ped => ped != null && ped.Exists())
                .GroupBy(ped => ped.Handle)
                .Select(group => group.First()))
            {
                Ped escort;
                if (_prisonerEscortOfficers.TryGetValue(prisoner.Handle, out escort))
                    DetachPrisonerFromEscort(prisoner, escort,
                        "OperationCleanupQueued");
                QueueCleanup(prisoner, null, now);
            }

            IEnumerable<Ped> officers = _sceneEscortOfficers
                .Concat(_stationHandoffOfficers)
                .Concat(_prisonHandoffOfficers)
                .Concat(new[] { _transportDriver })
                .Where(ped => ped != null && ped.Exists())
                .GroupBy(ped => ped.Handle)
                .Select(group => group.First());
            foreach (Ped officer in officers)
                QueueCleanup(officer, null, now);
            if (_transport != null && _transport.Exists() && !_playerOwnedTransport)
                QueueCleanup(null, _transport, now);
            foreach (Ped threat in _routeThreatPeds.Where(ped => ped != null && ped.Exists()).Distinct())
                QueueCleanup(threat, null, now);
            if (_routeThreatVehicle != null && _routeThreatVehicle.Exists())
                QueueCleanup(null, _routeThreatVehicle, now);
        }

        private void QueueCurrentTransportCleanup(
            ISet<int> preserveDispatchCustodyHandles = null)
        {
            DateTime now = DateTime.UtcNow;
            CleanupBlip(_transportBlip);
            _transportBlip = null;
            CleanupBlip(_custodyOfficerBlip);
            _custodyOfficerBlip = null;
            _custodyOfficerBlipHandle = 0;
            // Preserve the exact Dispatch prisoner through the same-frame
            // recovery call. Core releases this temporary Convoy protection
            // only after Dispatch has re-adopted the same Ped.
            RestoreAllCustodyInvincibility(preserveDispatchCustodyHandles);
            if (_transport != null && _transport.Exists() && !_playerOwnedTransport)
                QueueCleanup(null, _transport, now);
            foreach (Ped officer in _sceneEscortOfficers.Where(ped => ped != null && ped.Exists()).ToArray())
                QueueCleanup(officer, null, now);
            _sceneEscortOfficers.Clear();
            _prisonerEscortOfficers.Clear();
            _prisonerEscortStartedAt.Clear();
            _lastEscortOfficerTaskAt.Clear();
            _prisonerVehicleDoorsOpened.Clear();
            _prisonerDoorCloseTasksIssued.Clear();
            _prisonerDoorOpenTaskIssuedAt.Clear();
            _dispatchPrisonerMissingSince.Clear();
            _dispatchPrisonerLastHandleRetryAt.Clear();
            _dispatchSceneDoorAnimationFallbackIssued.Clear();
            _prisonerEntryTasksIssued.Clear();
            _physicalCuffReadFailuresLogged.Clear();
            foreach (Ped officer in _stationHandoffOfficers.Where(ped => ped != null && ped.Exists()).ToArray())
                QueueCleanup(officer, null, now);
            _stationHandoffOfficers.Clear();
            foreach (Ped officer in _prisonHandoffOfficers.Where(ped => ped != null && ped.Exists()).ToArray())
                QueueCleanup(officer, null, now);
            _prisonHandoffOfficers.Clear();
        }

        private void QueueRouteThreatCleanup()
        {
            DateTime now = DateTime.UtcNow;
            foreach (Ped threat in _routeThreatPeds.Where(ped => ped != null && ped.Exists()).ToArray())
                QueueCleanup(threat, null, now);
            if (_routeThreatVehicle != null && _routeThreatVehicle.Exists())
                QueueCleanup(null, _routeThreatVehicle, now);
            _routeThreatPeds.Clear();
            _routeThreatVehicle = null;
        }

        private void QueueCleanup(Ped ped, Vehicle vehicle, DateTime now)
        {
            _deferredCleanup.Add(new DeferredCleanup
            {
                Ped = ped,
                Vehicle = vehicle,
                Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                Expires = now.AddSeconds(DeferredCleanupMaximumSeconds)
            });
        }

        private void ProcessDeferredCleanup(DateTime now)
        {
            if (now < _lastDeferredCleanupAt.AddMilliseconds(
                DeferredCleanupCheckMilliseconds))
                return;
            _lastDeferredCleanupAt = now;

            Ped player = Game.Player.Character;
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
            {
                Entity entity = item.Ped != null && item.Ped.Exists() ? (Entity)item.Ped : item.Vehicle;
                if (entity == null || !entity.Exists())
                {
                    _deferredCleanup.Remove(item);
                    continue;
                }
                bool playerOccupiesVehicle = item.Vehicle != null && item.Vehicle.Exists()
                    && player != null && player.Exists() && player.CurrentVehicle != null
                    && player.CurrentVehicle.Exists() && player.CurrentVehicle.Handle == item.Vehicle.Handle;
                float distance = player == null || !player.Exists()
                    ? float.MaxValue : entity.Position.DistanceTo(player.Position);
                if (now >= item.Earliest && !playerOccupiesVehicle
                    && distance >= CleanupDistance)
                {
                    DeleteDeferred(item);
                    _deferredCleanup.Remove(item);
                }
                else if (now >= item.Expires)
                {
                    if (!playerOccupiesVehicle)
                    {
                        LogRuntime("POLICE_CONVOY_DEFERRED_CLEANUP_HARD_TIMEOUT",
                            "Entity=" + entity.Handle
                            + "; Kind=" + (item.Ped != null ? "Ped" : "Vehicle")
                            + "; DistanceFromPlayer=" + distance.ToString("0.0")
                            + "; HardTimeoutSeconds=" + Math.Max(1, DeferredCleanupMaximumSeconds));
                        DeleteDeferred(item);
                    }
                    else
                    {
                        // Never delete a vehicle the player is currently driving.
                        LogRuntime("POLICE_CONVOY_DEFERRED_CLEANUP_RETAINED",
                            "Vehicle=" + item.Vehicle.Handle
                            + "; Reason=PlayerOccupiesVehicleAtHardTimeout");
                    }
                    _deferredCleanup.Remove(item);
                }
            }
        }

        /// <summary>
        /// A recoverable Convoy failure can leave its old transport, escort
        /// officers, or route-threat entities in the deferred-cleanup queue.
        /// Remove those Convoy-owned remnants before Dispatch retries custody
        /// or starts the next transport, otherwise the old van remains visible
        /// and the retry looks like a duplicate transport assignment. Prisoners
        /// are deliberately not queued by the recoverable failure path; keep
        /// this guard in place so this helper cannot delete an actively held
        /// prisoner if a future cleanup path changes.
        /// </summary>
        private void ClearDeferredCleanupBeforeRetry()
        {
            Ped player = Game.Player.Character;
            int removed = 0;
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
            {
                IEnumerable<Ped> queuedPeds = (item.RelatedPeds ?? new List<Ped>())
                    .Concat(item.Ped == null ? Enumerable.Empty<Ped>() : new[] { item.Ped });
                if (queuedPeds.Any(queued => queued != null && queued.Exists()
                    && _prisoners.Any(prisoner => prisoner != null
                        && prisoner.Exists() && prisoner.Handle == queued.Handle)))
                    continue;

                bool playerOccupiesVehicle = item.Vehicle != null && item.Vehicle.Exists()
                    && player != null && player.Exists()
                    && player.CurrentVehicle != null && player.CurrentVehicle.Exists()
                    && player.CurrentVehicle.Handle == item.Vehicle.Handle;
                if (playerOccupiesVehicle)
                {
                    LogRuntime(
                        "POLICE_CONVOY_FAILED_CLEANUP_RETAINED",
                        "Player is occupying a deferred Convoy vehicle; it was retained during custody retry. Vehicle="
                        + item.Vehicle.Handle);
                    continue;
                }

                DeleteDeferred(item);
                _deferredCleanup.Remove(item);
                removed++;
            }

            if (removed > 0)
                LogRuntime(
                    "POLICE_CONVOY_FAILED_CLEANUP_FLUSHED",
                    "Deferred Convoy transport entities removed before custody retry; Count=" + removed);
        }

        private void DetachPrisonersForRecovery(
            ISet<int> preserveDispatchCustodyHandles = null)
        {
            foreach (Ped prisoner in ValidPrisoners())
            {
                try
                {
                    bool wasInTransport = IsInTransport(prisoner);
                    if (wasInTransport)
                        Function.Call(Hash.TASK_LEAVE_VEHICLE, prisoner, _transport, 0);
                    if (preserveDispatchCustodyHandles == null
                        || !preserveDispatchCustodyHandles.Contains(prisoner.Handle))
                        Function.Call(Hash.SET_ENTITY_PROOFS,
                            prisoner, false, false, false, false, false, false, false, false);
                    bool dispatchRetainsCustody = preserveDispatchCustodyHandles != null
                        && preserveDispatchCustodyHandles.Contains(prisoner.Handle);
                    PreparePrisoner(
                        prisoner,
                        !dispatchRetainsCustody
                            && !wasInTransport && !_failedAfterStationHandoff);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_RECOVERY_FAILED", ex);
                }
            }
        }

        private void CleanupImmediate()
        {
            CleanupCustodyBlips();
            RestoreAllCustodyInvincibility();
            foreach (Ped prisoner in _prisoners.ToArray())
                try
                {
                    if (prisoner != null && prisoner.Exists())
                    {
                        Function.Call(Hash.SET_ENTITY_PROOFS,
                            prisoner, false, false, false, false, false, false, false, false);
                        prisoner.Delete();
                    }
                }
                catch { }
            foreach (Ped officer in _sceneEscortOfficers.Concat(_stationHandoffOfficers).Concat(_prisonHandoffOfficers).Distinct().ToArray())
                try { if (officer != null && officer.Exists()) officer.Delete(); } catch { }
            foreach (Ped threat in _routeThreatPeds.Distinct().ToArray())
                try { if (threat != null && threat.Exists()) threat.Delete(); } catch { }
            try { if (_routeThreatVehicle != null && _routeThreatVehicle.Exists()) _routeThreatVehicle.Delete(); } catch { }
            _routeThreatVehicle = null;
            CleanupTransportOnly();
        }

        private void CleanupTransportOnly()
        {
            CleanupCustodyBlips();
            RestoreAllCustodyInvincibility();
            try
            {
                if (_transport != null && _transport.Exists() && !_playerOwnedTransport)
                    SetEmergencySignals(_transport, false);
            }
            catch { }
            try
            {
                if (_transportDriver != null && _transportDriver.Exists())
                    _transportDriver.Delete();
            }
            catch { }
            try
            {
                Ped player = Game.Player.Character;
                bool playerOccupies = player != null && player.Exists() && player.CurrentVehicle != null
                    && player.CurrentVehicle.Exists() && _transport != null && _transport.Exists()
                    && player.CurrentVehicle.Handle == _transport.Handle;
                if (!_playerOwnedTransport && !playerOccupies
                    && _transport != null && _transport.Exists())
                    _transport.Delete();
            }
            catch { }
            _transportDriver = null;
            _transport = null;
        }

        private static VehicleDrivingFlags EmergencyDrivingFlags
        {
            get
            {
                // Keep emergency units moving through ordinary traffic while
                // retaining GTA's road/navmesh routing. The siren is enabled
                // separately so this remains explicit and reversible at the
                // scene/station handoff.
                return VehicleDrivingFlags.DrivingModePloughThrough
                    | VehicleDrivingFlags.DrivingModeAvoidVehiclesReckless
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

        private static Vector3 StationVehiclePosition(LSPDPoliceStationDefinition station)
        {
            if (station == null)
                return Vector3.Zero;
            Vector3 vehicle = new Vector3(station.VehicleX, station.VehicleY, station.VehicleZ);
            return vehicle == Vector3.Zero
                ? new Vector3(station.ExteriorX, station.ExteriorY, station.ExteriorZ)
                : vehicle;
        }

        private Vector3 StationHandoffPosition(LSPDPoliceStationDefinition station)
        {
            if (station == null)
                return Vector3.Zero;

            // Station vehicleX/Y/Z is the authored garage/drop-off reference.
            // Keep the pedestrian station coordinate out of the vehicle route.
            Vector3 garagePosition = new Vector3(
                station.VehicleX, station.VehicleY, station.VehicleZ);
            if (IsUsableStationHandoffReference(garagePosition))
                return garagePosition;

            Vector3 stationExterior = new Vector3(
                station.ExteriorX, station.ExteriorY, station.ExteriorZ);
            if (station.ExteriorSafe
                && IsUsableStationHandoffReference(stationExterior))
                return stationExterior;

            if (_profile != null)
            {
                LSPDPoliceLocationDefinition exterior =
                    _profile.FindLocation(station.LocationId);
                if (exterior != null && exterior.ExteriorSafe)
                {
                    Vector3 locationPosition = new Vector3(
                        exterior.X, exterior.Y, exterior.Z);
                    if (IsUsableStationHandoffReference(locationPosition))
                        return locationPosition;
                }
            }
            return Vector3.Zero;
        }

        private Vector3 StationIntakePosition(LSPDPoliceStationDefinition station)
        {
            if (station == null)
                return Vector3.Zero;
            Vector3 stationExterior = new Vector3(
                station.ExteriorX, station.ExteriorY, station.ExteriorZ);
            if (station.ExteriorSafe
                && IsUsableStationHandoffReference(stationExterior))
                return stationExterior;
            if (_profile != null)
            {
                LSPDPoliceLocationDefinition exterior =
                    _profile.FindLocation(station.LocationId);
                if (exterior != null && exterior.ExteriorSafe)
                {
                    Vector3 locationPosition = new Vector3(
                        exterior.X, exterior.Y, exterior.Z);
                    if (IsUsableStationHandoffReference(locationPosition))
                        return locationPosition;
                }
            }
            return StationVehiclePosition(station);
        }

        private static bool IsUsableStationHandoffReference(Vector3 position)
        {
            return position != Vector3.Zero
                && !float.IsNaN(position.X) && !float.IsInfinity(position.X)
                && !float.IsNaN(position.Y) && !float.IsInfinity(position.Y)
                && !float.IsNaN(position.Z) && !float.IsInfinity(position.Z);
        }

        private bool TryResolveStationHandoffMarkerPosition(
            out Vector3 position,
            out bool usedAuthoredGroundFallback)
        {
            position = Vector3.Zero;
            usedAuthoredGroundFallback = false;
            Vector3 reference = IsUsableStationHandoffReference(_stationDestination)
                ? _stationDestination : StationHandoffPosition(SelectedStation());
            if (!IsUsableStationHandoffReference(reference)
                || !IsAuthoredStationHandoffPoint(reference))
                return false;

            try
            {
                float groundZ;
                Vector3 groundNormal;
                if (TryGetStationHandoffGroundSample(
                    reference, out groundZ, out groundNormal)
                    && !float.IsNaN(groundZ) && !float.IsInfinity(groundZ)
                    && !float.IsNaN(groundNormal.Z) && !float.IsInfinity(groundNormal.Z)
                    && groundNormal.Z >= 0.80f
                    && Math.Abs(groundZ - reference.Z) <= 5f)
                {
                    position = new Vector3(reference.X, reference.Y, groundZ);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_STATION_MARKER_GROUND_QUERY_FAILED", ex);
            }

            // This is a visual/navigation destination, not an accepted vehicle
            // pose. Keep the authored elevation when the dynamic sample is
            // inconclusive; live parking validation remains a separate gate.
            position = reference;
            usedAuthoredGroundFallback = true;
            return true;
        }

        private bool TryFindSafeStationHandoffGroundPosition(
            out Vector3 position,
            out float heading,
            out Vector3 receivingPosition,
            out string reason)
        {
            position = Vector3.Zero;
            heading = 0f;
            receivingPosition = Vector3.Zero;
            reason = string.Empty;
            if (_stationDestination == Vector3.Zero)
            {
                reason = "The selected station has no authored outside marker.";
                return false;
            }

            _stationHandoffAuthoredGroundFallbackUsed = false;
            _stationHandoffUsedGarageFallback = false;
            Vector3 garageAnchor = StationVehiclePosition(SelectedStation());
            List<Vector3> anchors = new List<Vector3> { _stationDestination };
            if (IsUsableStationHandoffReference(garageAnchor)
                && garageAnchor.DistanceTo(_stationDestination) > 0.5f)
                anchors.Add(garageAnchor);

            // Try both authored station references before scanning nearby
            // roadside or parking candidates. The garage is the selected
            // Station's own saved vehicle point; no other Station is searched.
            List<KeyValuePair<Vector3, Vector3>> candidates =
                new List<KeyValuePair<Vector3, Vector3>>();
            foreach (Vector3 anchor in anchors)
                candidates.Add(new KeyValuePair<Vector3, Vector3>(anchor, anchor));
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
            {
                Vector3 anchor = anchors[anchorIndex];
                float[] ringRadii = anchorIndex == 0
                    ? new[] { 3f, 6f, 9f, 12f, 16f, 20f, 23f }
                    : new[] { 3f, 6f, 9f, 12f };
                int probes = anchorIndex == 0 ? 16 : 8;
                for (int ring = 0; ring < ringRadii.Length; ring++)
                {
                    float radius = ringRadii[ring];
                    for (int probe = 0; probe < probes; probe++)
                    {
                        double radians = probe * ((Math.PI * 2.0) / probes);
                        Vector3 candidate = anchor;
                        candidate.X += (float)Math.Cos(radians) * radius;
                        candidate.Y += (float)Math.Sin(radians) * radius;
                        candidates.Add(new KeyValuePair<Vector3, Vector3>(
                            anchor, candidate));
                    }
                }
            }

            List<Vehicle> nearbyVehicles = new List<Vehicle>();
            List<Ped> nearbyPeds = new List<Ped>();
            try
            {
                foreach (Vector3 anchor in anchors)
                {
                    if (_transport != null && _transport.Exists())
                        Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                            anchor.X, anchor.Y, anchor.Z);
                }
                foreach (Vehicle vehicle in World.GetAllVehicles())
                {
                    if (vehicle == null || !vehicle.Exists()
                        || _transport != null && _transport.Exists()
                            && vehicle.Handle == _transport.Handle)
                        continue;
                    if (anchors.Any(anchor =>
                        vehicle.Position.DistanceTo(anchor) <= 50f))
                        nearbyVehicles.Add(vehicle);
                }
                foreach (Ped ped in World.GetAllPeds())
                {
                    if (IsStationHandoffOwnedPed(ped))
                        continue;
                    if (anchors.Any(anchor => ped.Position.DistanceTo(anchor) <= 50f))
                        nearbyPeds.Add(ped);
                }
            }
            catch (Exception ex)
            {
                reason = "The station area is not ready to check for clear ground.";
                LogException("POLICE_CONVOY_STATION_HANDOFF_SCAN_FAILED", ex);
                return false;
            }

            string lastFailure = "No candidate had verified roadside vehicle clearance.";
            foreach (KeyValuePair<Vector3, Vector3> candidateEntry in candidates)
            {
                Vector3 anchor = candidateEntry.Key;
                Vector3 candidate = candidateEntry.Value;
                Vector3 grounded;
                if (!TryResolveStationHandoffGround(candidate, out grounded))
                    continue;
                if (grounded.DistanceTo(anchor) > StationHandoffMaximumMarkerOffset)
                    continue;

                Vector3 roadNode;
                float roadHeading;
                if (!TryGetStationHandoffRoadReference(
                    grounded, out roadNode, out roadHeading))
                {
                    lastFailure = "A nearby usable road node and heading could not be confirmed.";
                    continue;
                }
                float roadOffset = grounded.DistanceTo(roadNode);
                if (roadOffset < StationHandoffMinimumRoadOffset
                    || roadOffset > StationHandoffMaximumRoadOffset)
                {
                    lastFailure = "Candidate was not reachable roadside staging ground; RoadOffset="
                        + roadOffset.ToString("0.0", CultureInfo.InvariantCulture) + ".";
                    continue;
                }

                LSPDPoliceStationDefinition selectedStation = SelectedStation();
                float authoredHeading = StationHandoffHeading(selectedStation, anchor);
                bool authoredHeadingRoadAligned = IsUsableStationHandoffHeading(authoredHeading)
                    && Math.Min(HeadingDifference(authoredHeading, roadHeading),
                        HeadingDifference(authoredHeading, (roadHeading + 180f) % 360f))
                        <= 65.0f;
                float[] headings = authoredHeadingRoadAligned
                    && candidate.DistanceTo(anchor) <= 0.5f
                    ? new[] { authoredHeading, roadHeading, (roadHeading + 180f) % 360f }
                    : new[] { roadHeading, (roadHeading + 180f) % 360f };
                for (int headingIndex = 0; headingIndex < headings.Length; headingIndex++)
                {
                    Vector3 intake;
                    string candidateFailure;
                    if (!TryValidateStationHandoffCandidate(
                        grounded, headings[headingIndex], nearbyVehicles,
                        nearbyPeds, out intake, out candidateFailure))
                    {
                        lastFailure = candidateFailure;
                        continue;
                    }

                    position = grounded;
                    heading = headings[headingIndex];
                    receivingPosition = intake;
                    _stationHandoffUsedGarageFallback = anchors.Count > 1
                        && anchor.DistanceTo(garageAnchor) <= 0.5f;
                    return true;
                }
            }

            reason = "No station candidate passed road alignment, vehicle footprint, door access, dynamic clearance, and walking-route checks. "
                + lastFailure;
            return false;
        }

        private bool TryGetStationHandoffRoadReference(
            Vector3 query,
            out Vector3 roadNode,
            out float roadHeading)
        {
            roadNode = Vector3.Zero;
            roadHeading = 0f;
            if (query == Vector3.Zero)
                return false;

            try
            {
                using (OutputArgument nodeOutput = new OutputArgument())
                using (OutputArgument headingOutput = new OutputArgument())
                {
                    bool found = Function.Call<bool>(
                        Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        query.X, query.Y, query.Z,
                        nodeOutput, headingOutput, 1, 3.0f, 0);
                    if (found)
                    {
                        Vector3 node = nodeOutput.GetResult<Vector3>();
                        float nodeHeading = headingOutput.GetResult<float>();
                        if (node != Vector3.Zero
                            && !float.IsNaN(nodeHeading) && !float.IsInfinity(nodeHeading)
                            && node.DistanceTo(query) <= StationHandoffMaximumRoadOffset
                            && Math.Abs(node.Z - query.Z) <= MaximumRoadElevationDifference
                            && Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                                node.X, node.Y, node.Z, 0))
                        {
                            roadNode = node;
                            roadHeading = nodeHeading;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Use the existing SHVDN road-node fallback below.
            }

            try
            {
                float fallbackHeading;
                Vector3 fallback = World.GetNextPositionOnStreetWithHeading(
                    query, out fallbackHeading);
                if (fallback == Vector3.Zero
                    || float.IsNaN(fallbackHeading) || float.IsInfinity(fallbackHeading)
                    || fallback.DistanceTo(query) > StationHandoffMaximumRoadOffset
                    || Math.Abs(fallback.Z - query.Z) > MaximumRoadElevationDifference)
                    return false;
                roadNode = fallback;
                roadHeading = fallbackHeading;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryValidateActualStationHandoffPose(
            Vector3 position,
            float heading,
            out Vector3 groundedPosition,
            out Vector3 receivingPosition,
            out string reason)
        {
            groundedPosition = Vector3.Zero;
            receivingPosition = Vector3.Zero;
            reason = string.Empty;
            if (_transport == null || !_transport.Exists())
            {
                reason = "The transport is no longer available.";
                return false;
            }

            Vector3 grounded;
            Vector3 roadNode;
            float roadHeading;
            if (!TryResolveStationHandoffGround(
                position, out grounded, true))
            {
                reason = "The actual parked position has no confirmed or supported Station ground.";
                return false;
            }
            if (!TryGetStationHandoffRoadReference(
                grounded, out roadNode, out roadHeading))
            {
                reason = "The actual parked position has no usable nearby road reference.";
                return false;
            }
            groundedPosition = grounded;
            float roadOffset = grounded.DistanceTo(roadNode);
            Vector3 receivingArea = _stationHandoffMarkerPosition != Vector3.Zero
                ? _stationHandoffMarkerPosition : _stationDestination;
            if (roadOffset > StationHandoffMaximumRoadOffset
                || receivingArea == Vector3.Zero
                || grounded.DistanceTo(receivingArea)
                    > StationHandoffMaximumMarkerOffset)
            {
                reason = "The actual stopped vehicle is outside the selected Station receiving area.";
                return false;
            }
            float roadAlignment = Math.Min(
                HeadingDifference(heading, roadHeading),
                HeadingDifference(heading, (roadHeading + 180f) % 360f));
            if (roadAlignment > 65.0f)
            {
                reason = "The actual transport heading is not aligned with the nearby usable road.";
                return false;
            }

            List<Vehicle> nearbyVehicles = new List<Vehicle>();
            List<Ped> nearbyPeds = new List<Ped>();
            try
            {
                foreach (Vehicle vehicle in World.GetNearbyVehicles(
                    grounded, 55f) ?? new Vehicle[0])
                {
                    if (vehicle == null || !vehicle.Exists()
                        || vehicle.Handle == _transport.Handle)
                        continue;
                    nearbyVehicles.Add(vehicle);
                }
                foreach (Ped ped in World.GetAllPeds())
                {
                    if (IsStationHandoffOwnedPed(ped)
                        || ped.Position.DistanceTo(grounded) > 55f)
                        continue;
                    nearbyPeds.Add(ped);
                }
            }
            catch (Exception ex)
            {
                reason = "Live station access clearance could not be checked: " + ex.GetType().Name + ".";
                return false;
            }

            return TryValidateStationHandoffCandidate(
                grounded, heading, nearbyVehicles, nearbyPeds,
                out receivingPosition, out reason);
        }

        private bool TryValidateStationHandoffCandidate(
            Vector3 center,
            float heading,
            IList<Vehicle> nearbyVehicles,
            IList<Ped> nearbyPeds,
            out Vector3 receivingPosition,
            out string reason)
        {
            receivingPosition = Vector3.Zero;
            reason = string.Empty;
            if (_transport == null || !_transport.Exists()
                || center == Vector3.Zero
                || float.IsNaN(heading) || float.IsInfinity(heading))
            {
                reason = "Transport, candidate position, or heading is invalid.";
                return false;
            }

            Vector3 minimum;
            Vector3 maximum;
            try
            {
                using (OutputArgument minimumOutput = new OutputArgument())
                using (OutputArgument maximumOutput = new OutputArgument())
                {
                    Function.Call(Hash.GET_MODEL_DIMENSIONS,
                        _transport.Model.Hash, minimumOutput, maximumOutput);
                    minimum = minimumOutput.GetResult<Vector3>();
                    maximum = maximumOutput.GetResult<Vector3>();
                }
            }
            catch (Exception ex)
            {
                reason = "GTA vehicle footprint dimensions are unavailable: " + ex.GetType().Name + ".";
                return false;
            }
            float halfWidth = Math.Max(Math.Abs(minimum.X), Math.Abs(maximum.X));
            float halfLength = Math.Max(Math.Abs(minimum.Y), Math.Abs(maximum.Y));
            if (halfWidth < 0.6f || halfLength < 1.2f
                || float.IsNaN(halfWidth) || float.IsInfinity(halfWidth)
                || float.IsNaN(halfLength) || float.IsInfinity(halfLength))
            {
                reason = "GTA returned unusable transport footprint dimensions.";
                return false;
            }

            float centerGroundZ = center.Z;
            Vector3 centerNormal = Vector3.Zero;
            bool centerGroundConfirmed = false;
            try
            {
                centerGroundConfirmed = TryGetStationHandoffGroundSample(
                    center, out centerGroundZ, out centerNormal);
            }
            catch (Exception ex)
            {
                if (!IsTrustedStationHandoffGroundReference(center))
                {
                    reason = "Transport center ground query failed: "
                        + ex.GetType().Name + ".";
                    return false;
                }
            }
            if (!centerGroundConfirmed)
            {
                bool currentSupportedPose =
                    IsSupportedCurrentTransportAtStationReceivingPose(center);
                if (!currentSupportedPose
                    && !IsTrustedStationHandoffGroundReference(center))
                {
                    reason = "GTA could not confirm level ground under the transport center.";
                    return false;
                }
                centerGroundZ = currentSupportedPose
                    ? _stationHandoffMarkerPosition.Z : center.Z;
                _stationHandoffAuthoredGroundFallbackUsed = true;
            }
            else if (float.IsNaN(centerGroundZ) || float.IsInfinity(centerGroundZ)
                || float.IsNaN(centerNormal.Z) || float.IsInfinity(centerNormal.Z)
                || centerNormal.Z < 0.90f
                || Math.Abs(centerGroundZ - center.Z) > 1.25f)
            {
                reason = "GTA confirmed uneven or unsupported ground under the transport center.";
                return false;
            }

            bool allowOneInconclusiveStationGroundSample = false;
            if ((IsAuthoredStationHandoffPoint(center)
                    && _transport.Position.DistanceTo(center) <= 0.75f
                    && Math.Abs(_transport.Speed) <= StationHandoffStoppedSpeed)
                || IsSupportedCurrentTransportAtStationReceivingPose(center))
            {
                try
                {
                    allowOneInconclusiveStationGroundSample = Function.Call<bool>(
                        Hash.IS_VEHICLE_ON_ALL_WHEELS, _transport);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_STATION_WHEEL_SUPPORT_CHECK_FAILED", ex);
                }
            }

            double radians = heading * (Math.PI / 180.0);
            Vector3 forward = new Vector3(
                -(float)Math.Sin(radians), (float)Math.Cos(radians), 0f);
            Vector3 right = new Vector3(
                (float)Math.Cos(radians), (float)Math.Sin(radians), 0f);
            float[,] footprintSamples =
            {
                { -halfWidth, -halfLength }, { 0f, -halfLength }, { halfWidth, -halfLength },
                { -halfWidth, 0f }, { halfWidth, 0f },
                { -halfWidth, halfLength }, { 0f, halfLength }, { halfWidth, halfLength }
            };
            float footprintRadius = (float)Math.Sqrt(
                halfWidth * halfWidth + halfLength * halfLength);
            int inconclusiveFootprintGroundSamples = 0;
            for (int index = 0; index < footprintSamples.GetLength(0); index++)
            {
                Vector3 sample = StationHandoffPosePoint(
                    center, right, forward,
                    footprintSamples[index, 0], footprintSamples[index, 1]);
                float groundZ;
                Vector3 groundNormal;
                bool groundConfirmed = false;
                try
                {
                    groundConfirmed = TryGetStationHandoffGroundSample(
                        sample, out groundZ, out groundNormal);
                }
                catch
                {
                    groundZ = 0f;
                    groundNormal = Vector3.Zero;
                }
                if (!groundConfirmed || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || float.IsNaN(groundNormal.Z) || float.IsInfinity(groundNormal.Z))
                {
                    inconclusiveFootprintGroundSamples++;
                    if (!allowOneInconclusiveStationGroundSample
                        || inconclusiveFootprintGroundSamples > 1)
                    {
                        reason = "The transport footprint includes unconfirmed ground support.";
                        return false;
                    }
                    continue;
                }
                if (groundNormal.Z < 0.88f
                    || Math.Abs(groundZ - centerGroundZ) > 0.55f)
                {
                    reason = "GTA confirmed uneven or unsupported ground under the transport footprint.";
                    return false;
                }

                float[] clearanceHeights = { 0.55f, 1.1f, 1.65f };
                for (int heightIndex = 0; heightIndex < clearanceHeights.Length; heightIndex++)
                {
                    Vector3 from = center;
                    Vector3 to = sample;
                    from.Z = centerGroundZ + clearanceHeights[heightIndex];
                    to.Z = centerGroundZ + clearanceHeights[heightIndex];
                    try
                    {
                        RaycastResult obstruction = World.Raycast(
                            from, to,
                            IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Glass,
                            _transport);
                        if (obstruction.DidHit)
                        {
                            reason = "A wall, bench, prop, or static object intersects the transport footprint.";
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        reason = "Transport footprint obstruction query failed: " + ex.GetType().Name + ".";
                        return false;
                    }
                }
            }

            foreach (Vehicle nearby in nearbyVehicles ?? new Vehicle[0])
            {
                if (nearby == null || !nearby.Exists()
                    || nearby.Handle == _transport.Handle
                    || Math.Abs(nearby.Position.Z - center.Z) > 2.0f)
                    continue;
                float otherRadius = 3.2f;
                try
                {
                    Vector3 otherMinimum;
                    Vector3 otherMaximum;
                    using (OutputArgument minimumOutput = new OutputArgument())
                    using (OutputArgument maximumOutput = new OutputArgument())
                    {
                        Function.Call(Hash.GET_MODEL_DIMENSIONS,
                            nearby.Model.Hash, minimumOutput, maximumOutput);
                        otherMinimum = minimumOutput.GetResult<Vector3>();
                        otherMaximum = maximumOutput.GetResult<Vector3>();
                    }
                    float otherWidth = Math.Max(Math.Abs(otherMinimum.X), Math.Abs(otherMaximum.X));
                    float otherLength = Math.Max(Math.Abs(otherMinimum.Y), Math.Abs(otherMaximum.Y));
                    if (otherWidth >= 0.4f && otherLength >= 0.8f)
                        otherRadius = (float)Math.Sqrt(otherWidth * otherWidth + otherLength * otherLength);
                }
                catch { }
                if (nearby.Position.DistanceTo(center)
                    < footprintRadius + otherRadius + 0.75f)
                {
                    reason = "Another vehicle overlaps the complete transport footprint.";
                    return false;
                }
            }

            foreach (Ped ped in nearbyPeds ?? new Ped[0])
            {
                if (ped == null || !ped.Exists() || ped.IsDead)
                    continue;
                if (StationHandoffPointInsideFootprint(
                    center, right, forward, halfWidth + 0.8f, halfLength + 1.0f,
                    ped.Position))
                {
                    reason = "A pedestrian occupies the transport footprint.";
                    return false;
                }
            }

            List<Ped> assignedPrisoners = ValidPrisoners().ToList();
            List<Vector3> rearAccessPoints = new List<Vector3>();
            IEnumerable<Ped> loadedPrisoners = assignedPrisoners
                .Where(IsInTransport).ToList();
            if (!loadedPrisoners.Any())
                loadedPrisoners = new Ped[] { null };
            foreach (Ped prisoner in loadedPrisoners)
            {
                VehicleSeat seat = prisoner == null
                    ? VehicleSeat.LeftRear
                    : PrisonerSeat(prisoner, assignedPrisoners);
                float rearSide = PrisonerDoorIndex(seat) == 2 ? -1f : 1f;
                float custodyDoorLongitudinal = seat == VehicleSeat.RightFront
                    ? halfLength * 0.35f : -halfLength * 0.20f;
                Vector3 custodyDoor = StationHandoffPosePoint(
                    center, right, forward,
                    rearSide * (halfWidth + StationHandoffDoorAccessClearance),
                    custodyDoorLongitudinal);
                Vector3 rearAccess;
                if (!TryResolveStationWalkPosition(custodyDoor, out rearAccess)
                    || IsStationAccessOccupied(rearAccess, nearbyVehicles, nearbyPeds)
                    || !TryConfirmStationDoorSwing(
                        center, rearAccess, centerGroundZ))
                {
                    reason = "A required rear custody door has no confirmed opening and actor access space.";
                    return false;
                }
                rearAccessPoints.Add(rearAccess);
            }
            Vector3 driverExit = StationHandoffPosePoint(
                center, right, forward,
                -(halfWidth + StationHandoffDoorAccessClearance),
                halfLength * 0.30f);
            Vector3 driverAccess;
            if (!TryResolveStationWalkPosition(driverExit, out driverAccess))
            {
                reason = "Ground at the rear custody door or Player exit is unavailable.";
                return false;
            }
            if (IsStationAccessOccupied(driverAccess, nearbyVehicles, nearbyPeds)
                || !TryValidateStationWalkRoute(
                    driverAccess, rearAccessPoints[0], nearbyVehicles, nearbyPeds))
            {
                reason = "The Player exit or route from the Utility to its custody door is blocked.";
                return false;
            }

            Vector3[] receivingOffsets =
            {
                Vector3.Zero, new Vector3(1.5f, 0f, 0f), new Vector3(-1.5f, 0f, 0f),
                new Vector3(0f, 1.5f, 0f), new Vector3(0f, -1.5f, 0f),
                new Vector3(3.0f, 0f, 0f), new Vector3(-3.0f, 0f, 0f),
                new Vector3(0f, 3.0f, 0f), new Vector3(0f, -3.0f, 0f)
            };
            for (int intakeIndex = 0; intakeIndex < receivingOffsets.Length; intakeIndex++)
            {
                Vector3 intakeCandidate = _stationDestination + receivingOffsets[intakeIndex];
                Vector3 intake;
                if (!TryResolveStationWalkPosition(intakeCandidate, out intake)
                    || IsStationAccessOccupied(intake, nearbyVehicles, nearbyPeds))
                    continue;
                bool allRearRoutesClear = rearAccessPoints.All(rearAccess =>
                    TryValidateStationWalkRoute(
                        rearAccess, intake, nearbyVehicles, nearbyPeds));
                if (!allRearRoutesClear)
                    continue;
                Vector3 officerAccess;
                bool officerCanReachAllDoors = true;
                foreach (Vector3 rearAccess in rearAccessPoints)
                {
                    if (!TryFindStationOfficerAccessPoint(
                        intake, rearAccess, nearbyVehicles, nearbyPeds,
                        out officerAccess))
                    {
                        officerCanReachAllDoors = false;
                        break;
                    }
                }
                if (officerCanReachAllDoors)
                {
                    receivingPosition = intake;
                    return true;
                }
            }

            reason = "No clear rear-door approach, Player exit, Standby Officer approach, and walking route to station intake were available.";
            return false;
        }

        private static Vector3 StationHandoffPosePoint(
            Vector3 center,
            Vector3 right,
            Vector3 forward,
            float localX,
            float localY)
        {
            return center + right * localX + forward * localY;
        }

        private static bool StationHandoffPointInsideFootprint(
            Vector3 center,
            Vector3 right,
            Vector3 forward,
            float halfWidth,
            float halfLength,
            Vector3 point)
        {
            Vector3 delta = point - center;
            float lateral = Math.Abs(delta.X * right.X + delta.Y * right.Y);
            float longitudinal = Math.Abs(delta.X * forward.X + delta.Y * forward.Y);
            return lateral <= halfWidth && longitudinal <= halfLength;
        }

        private bool TryResolveStationWalkPosition(Vector3 candidate, out Vector3 position)
        {
            position = Vector3.Zero;
            if (candidate == Vector3.Zero)
                return false;
            try
            {
                float groundZ;
                Vector3 normal;
                bool groundConfirmed = TryGetStationHandoffGroundSample(
                    candidate, out groundZ, out normal);
                if (groundConfirmed)
                {
                    if (float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                        || float.IsNaN(normal.Z) || float.IsInfinity(normal.Z)
                        || normal.Z < 0.86f
                        || Math.Abs(groundZ - candidate.Z) > 5f)
                        return false;
                }
                else
                {
                    if (!IsSelectedStationExteriorHandoffPoint(candidate, 4.5f))
                        return false;
                    groundZ = candidate.Z;
                }
                int interior = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                    candidate.X, candidate.Y, groundZ);
                if (interior != 0
                    && !IsSelectedStationExteriorHandoffPoint(
                        new Vector3(candidate.X, candidate.Y, groundZ),
                        StationHandoffMaximumMarkerOffset + 2f))
                    return false;
                position = new Vector3(candidate.X, candidate.Y, groundZ);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool HasStationStandbyOfficerRouteToReceivingPoint()
        {
            if (_stationHandoffReceivingPosition == Vector3.Zero)
                return false;
            List<Ped> officers = _stationHandoffOfficers
                .Where(officer => officer != null && officer.Exists() && !officer.IsDead)
                .ToList();
            if (officers.Count == 0)
                return false;

            List<Ped> nearbyPeds = new List<Ped>();
            List<Vehicle> nearbyVehicles = new List<Vehicle>();
            try
            {
                foreach (Ped ped in World.GetAllPeds())
                    if (!IsStationHandoffOwnedPed(ped)
                        && ped.Position.DistanceTo(_stationHandoffReceivingPosition) <= 55f)
                        nearbyPeds.Add(ped);
                foreach (Vehicle vehicle in World.GetNearbyVehicles(
                    _stationHandoffReceivingPosition, 55f))
                    if (vehicle != null && vehicle.Exists()
                        && (_transport == null || !_transport.Exists()
                            || vehicle.Handle != _transport.Handle))
                        nearbyVehicles.Add(vehicle);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_STATION_STANDBY_ROUTE_SCAN_FAILED", ex);
                return false;
            }

            foreach (Ped officer in officers)
            {
                Vector3 officerPosition;
                if (TryResolveStationWalkPosition(officer.Position, out officerPosition)
                    && TryValidateStationWalkRoute(
                        officerPosition, _stationHandoffReceivingPosition,
                        nearbyVehicles, nearbyPeds))
                    return true;
            }
            return false;
        }

        private bool TryResolveStationStandbyPosition(
            Vector3 candidate,
            out Vector3 position)
        {
            position = Vector3.Zero;
            if (candidate == Vector3.Zero)
                return false;
            try
            {
                float groundZ;
                Vector3 normal;
                bool groundConfirmed = TryGetStationHandoffGroundSample(
                    candidate, out groundZ, out normal);
                if (groundConfirmed)
                {
                    if (float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                        || float.IsNaN(normal.Z) || float.IsInfinity(normal.Z)
                        || normal.Z < 0.86f
                        || Math.Abs(groundZ - candidate.Z) > 5f)
                        return false;
                }
                else
                {
                    if (!IsSelectedStationExteriorHandoffPoint(candidate, 4.5f))
                        return false;
                    groundZ = candidate.Z;
                }

                Vector3 grounded = new Vector3(candidate.X, candidate.Y, groundZ);
                int interior = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                    grounded.X, grounded.Y, grounded.Z);
                if (interior != 0
                    && !IsSelectedStationExteriorHandoffPoint(grounded,
                        StationHandoffMaximumMarkerOffset + 2f))
                    return false;
                position = grounded;
                return true;
            }
            catch
            {
                if (!IsSelectedStationExteriorHandoffPoint(candidate, 4.5f))
                    return false;
                position = candidate;
                return true;
            }
        }

        private bool IsStationAccessOccupied(
            Vector3 position,
            IList<Vehicle> nearbyVehicles,
            IList<Ped> nearbyPeds)
        {
            if (nearbyPeds != null && nearbyPeds.Any(ped => ped != null && ped.Exists()
                && !ped.IsDead && ped.Position.DistanceTo(position) < StationHandoffPedClearance))
                return true;
            return nearbyVehicles != null && nearbyVehicles.Any(vehicle =>
                vehicle != null && vehicle.Exists()
                && (_transport == null || !_transport.Exists()
                    || vehicle.Handle != _transport.Handle)
                && Math.Abs(vehicle.Position.Z - position.Z) < 2.0f
                && vehicle.Position.DistanceTo(position) < 3.0f);
        }

        private bool TryConfirmStationDoorSwing(
            Vector3 vehicleCenter,
            Vector3 rearAccess,
            float groundZ)
        {
            float[] heights = { 0.55f, 1.0f, 1.45f };
            for (int index = 0; index < heights.Length; index++)
            {
                Vector3 from = vehicleCenter;
                Vector3 to = rearAccess;
                from.Z = groundZ + heights[index];
                to.Z = groundZ + heights[index];
                try
                {
                    RaycastResult obstruction = World.Raycast(
                        from, to,
                        IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Glass,
                        _transport);
                    if (obstruction.DidHit)
                        return false;
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        private bool TryValidateStationWalkRoute(
            Vector3 start,
            Vector3 destination,
            IList<Vehicle> nearbyVehicles,
            IList<Ped> nearbyPeds)
        {
            float distance = start.DistanceTo(destination);
            if (distance < 0.5f)
                return true;
            if (distance > StationHandoffMaximumMarkerOffset + 12f)
                return false;

            int segments = Math.Max(1,
                (int)Math.Ceiling(distance / StationHandoffWalkRouteStep));
            Vector3 previous = start;
            for (int segment = 1; segment <= segments; segment++)
            {
                float t = (float)segment / segments;
                Vector3 sample = start + (destination - start) * t;
                Vector3 grounded;
                if (!TryResolveStationWalkPosition(sample, out grounded)
                    || Math.Abs(grounded.Z - previous.Z) > 0.75f
                    || IsStationAccessOccupied(grounded, nearbyVehicles, nearbyPeds))
                    return false;

                Vector3 from = previous;
                Vector3 to = grounded;
                from.Z += 0.9f;
                to.Z += 0.9f;
                try
                {
                    RaycastResult obstruction = World.Raycast(
                        from, to,
                        IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Glass,
                        _transport);
                    if (obstruction.DidHit)
                        return false;
                }
                catch
                {
                    return false;
                }
                previous = grounded;
            }
            return true;
        }

        private bool TryFindStationOfficerAccessPoint(
            Vector3 receivingPoint,
            Vector3 rearDoorAccess,
            IList<Vehicle> nearbyVehicles,
            IList<Ped> nearbyPeds,
            out Vector3 officerPosition)
        {
            officerPosition = Vector3.Zero;
            Vector3[] offsets =
            {
                new Vector3(2.0f, 0f, 0f), new Vector3(-2.0f, 0f, 0f),
                new Vector3(0f, 2.0f, 0f), new Vector3(0f, -2.0f, 0f),
                new Vector3(3.5f, 0f, 0f), new Vector3(-3.5f, 0f, 0f),
                new Vector3(0f, 3.5f, 0f), new Vector3(0f, -3.5f, 0f)
            };
            for (int index = 0; index < offsets.Length; index++)
            {
                Vector3 candidate;
                if (!TryResolveStationWalkPosition(
                    receivingPoint + offsets[index], out candidate)
                    || IsStationAccessOccupied(candidate, nearbyVehicles, nearbyPeds)
                    || !TryValidateStationWalkRoute(
                        candidate, rearDoorAccess, nearbyVehicles, nearbyPeds))
                    continue;
                officerPosition = candidate;
                return true;
            }
            return false;
        }

        private bool TryResolveStationHandoffGround(
            Vector3 candidate,
            out Vector3 position,
            bool allowCurrentSupportedStationPose = false)
        {
            position = Vector3.Zero;
            float groundZ = candidate.Z;
            Vector3 groundNormal = Vector3.Zero;
            bool groundConfirmed = false;
            try
            {
                groundConfirmed = TryGetStationHandoffGroundSample(
                    candidate, out groundZ, out groundNormal);
            }
            catch (Exception ex)
            {
                if (!IsTrustedStationHandoffGroundReference(candidate))
                {
                    LogException("POLICE_CONVOY_STATION_HANDOFF_GROUND_QUERY_FAILED", ex);
                    return false;
                }
            }

            if (!groundConfirmed)
            {
                bool currentSupportedPose = allowCurrentSupportedStationPose
                    && IsSupportedCurrentTransportAtStationReceivingPose(candidate);
                if (!currentSupportedPose
                    && !IsTrustedStationHandoffGroundReference(candidate))
                    return false;
                groundZ = currentSupportedPose
                    ? _stationHandoffMarkerPosition.Z : candidate.Z;
                _stationHandoffAuthoredGroundFallbackUsed = true;
            }
            else if (float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                || float.IsNaN(groundNormal.Z) || float.IsInfinity(groundNormal.Z)
                || groundNormal.Z < 0.94f
                || Math.Abs(groundZ - candidate.Z) > 5f)
                return false;

            Vector3 grounded = new Vector3(candidate.X, candidate.Y, groundZ);
            try
            {
                int interior = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                    grounded.X, grounded.Y, grounded.Z);
                if (interior != 0
                    && !IsSelectedStationExteriorHandoffPoint(
                        grounded, StationHandoffMaximumMarkerOffset + 2f))
                    return false;

                Vector3 nearestStreet = World.GetNextPositionOnStreet(grounded);
                float roadDistance = nearestStreet.DistanceTo(grounded);
                if (nearestStreet == Vector3.Zero
                    || (!allowCurrentSupportedStationPose && roadDistance < 4.0f)
                    || roadDistance > 30f
                    || Math.Abs(nearestStreet.Z - grounded.Z) > MaximumRoadElevationDifference)
                    return false;

                position = grounded;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsAuthoredStationHandoffPoint(Vector3 position)
        {
            if (!IsUsableStationHandoffReference(position))
                return false;
            if (_stationDestination != Vector3.Zero
                && StationHandoffHorizontalDistance(position, _stationDestination) <= 0.25f
                && Math.Abs(position.Z - _stationDestination.Z) <= 5f)
                return true;
            if (_stationIntakeDestination != Vector3.Zero
                && StationHandoffHorizontalDistance(position, _stationIntakeDestination) <= 0.25f
                && Math.Abs(position.Z - _stationIntakeDestination.Z) <= 5f)
                return true;
            Vector3 garage = StationVehiclePosition(SelectedStation());
            return IsUsableStationHandoffReference(garage)
                && StationHandoffHorizontalDistance(position, garage) <= 0.25f
                && Math.Abs(position.Z - garage.Z) <= 5f;
        }

        private bool IsSelectedStationExteriorHandoffPoint(
            Vector3 position,
            float maximumDistance)
        {
            if (_profile == null || !IsUsableStationHandoffReference(position))
                return false;
            LSPDPoliceStationDefinition station = SelectedStation();
            if (station == null)
                return false;
            LSPDPoliceLocationDefinition exterior =
                _profile.FindLocation(station.LocationId);
            if (!station.ExteriorSafe
                && (exterior == null || !exterior.ExteriorSafe))
                return false;

            Vector3 vehicleReference = IsUsableStationHandoffReference(_stationDestination)
                ? _stationDestination : StationHandoffPosition(station);
            Vector3 intakeReference = IsUsableStationHandoffReference(_stationIntakeDestination)
                ? _stationIntakeDestination : StationIntakePosition(station);
            return IsNearStationHandoffReference(position, vehicleReference, maximumDistance)
                || IsNearStationHandoffReference(position, intakeReference, maximumDistance);
        }

        private static bool IsNearStationHandoffReference(
            Vector3 position,
            Vector3 reference,
            float maximumDistance)
        {
            return IsUsableStationHandoffReference(position)
                && IsUsableStationHandoffReference(reference)
                && StationHandoffHorizontalDistance(position, reference) <= maximumDistance
                && Math.Abs(position.Z - reference.Z) <= 5f;
        }

        private static bool IsUsableStationHandoffHeading(float heading)
        {
            return !float.IsNaN(heading) && !float.IsInfinity(heading)
                && heading >= 0f && heading < 360f;
        }

        private static float StationHandoffHeading(
            LSPDPoliceStationDefinition station,
            Vector3 authoredReference)
        {
            if (station == null)
                return float.NaN;
            Vector3 garage = StationVehiclePosition(station);
            if (IsUsableStationHandoffReference(garage)
                && StationHandoffHorizontalDistance(authoredReference, garage) <= 0.5f
                && IsUsableStationHandoffHeading(station.VehicleHeading))
                return station.VehicleHeading;
            Vector3 exterior = new Vector3(
                station.ExteriorX, station.ExteriorY, station.ExteriorZ);
            return IsNearStationHandoffReference(authoredReference, exterior, 0.5f)
                && IsUsableStationHandoffHeading(station.ExteriorHeading)
                ? station.ExteriorHeading : float.NaN;
        }

        private static Vector3 CreateStationHandoffGroundProbe(Vector3 position)
        {
            return new Vector3(
                position.X, position.Y,
                position.Z + StationHandoffGroundProbeHeight);
        }

        private static bool TryGetStationHandoffGroundSample(
            Vector3 position,
            out float groundZ,
            out Vector3 groundNormal)
        {
            groundZ = 0f;
            groundNormal = Vector3.Zero;
            if (!IsUsableStationHandoffReference(position))
                return false;
            Vector3 probe = CreateStationHandoffGroundProbe(position);
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                probe.X, probe.Y, probe.Z);
            return World.GetGroundHeightAndNormal(
                probe, out groundZ, out groundNormal);
        }

        private static float StationHandoffHorizontalDistance(Vector3 first, Vector3 second)
        {
            float deltaX = first.X - second.X;
            float deltaY = first.Y - second.Y;
            return (float)Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        private bool IsTrustedStationHandoffGroundReference(Vector3 position)
        {
            return IsAuthoredStationHandoffPoint(position)
                || (_stationHandoffMarkerPosition != Vector3.Zero
                    && position.DistanceTo(_stationHandoffMarkerPosition) <= 0.25f)
                || (_stationHandoffGroundPosition != Vector3.Zero
                    && position.DistanceTo(_stationHandoffGroundPosition)
                        <= StationHandoffArrivalRadius);
        }

        private bool IsSupportedCurrentTransportAtStationReceivingPose(Vector3 position)
        {
            if (_transport == null || !_transport.Exists()
                || _stationHandoffMarkerPosition == Vector3.Zero
                || Math.Abs(_transport.Speed) > StationHandoffStoppedSpeed
                || StationHandoffHorizontalDistance(
                    _transport.Position, position) > 0.75f
                || Math.Abs(_transport.Position.Z - position.Z) > 2.0f
                || StationHandoffHorizontalDistance(
                    position, _stationHandoffMarkerPosition)
                    > StationHandoffMaximumMarkerOffset
                || Math.Abs(position.Z - _stationHandoffMarkerPosition.Z) > 2.0f
                || !IsSelectedStationExteriorHandoffPoint(
                    position, StationHandoffMaximumMarkerOffset))
                return false;
            try
            {
                return Function.Call<bool>(Hash.IS_VEHICLE_ON_ALL_WHEELS, _transport);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_STATION_WHEEL_SUPPORT_CHECK_FAILED", ex);
                return false;
            }
        }

        private bool IsStationHandoffOwnedPed(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return true;
            Ped player = Game.Player.Character;
            if (player != null && player.Exists() && ped.Handle == player.Handle)
                return true;
            if (_prisoners.Any(prisoner => prisoner != null && prisoner.Exists()
                && prisoner.Handle == ped.Handle)
                || _sceneEscortOfficers.Any(officer => officer != null && officer.Exists()
                    && officer.Handle == ped.Handle)
                || _stationHandoffOfficers.Any(officer => officer != null && officer.Exists()
                    && officer.Handle == ped.Handle))
                return true;
            try
            {
                return _transport != null && _transport.Exists()
                    && ped.IsInVehicle()
                    && ped.CurrentVehicle != null
                    && ped.CurrentVehicle.Exists()
                    && ped.CurrentVehicle.Handle == _transport.Handle;
            }
            catch
            {
                return false;
            }
        }

        private bool TryResolveStationHandoffOfficerPosition(int index, out Vector3 position)
        {
            position = Vector3.Zero;
            if (_stationHandoffMarkerPosition == Vector3.Zero
                && _stationHandoffGroundPosition == Vector3.Zero)
                return false;
            Vector3[] offsets =
            {
                new Vector3(1.5f, 0f, 0f), new Vector3(-1.5f, 0f, 0f),
                new Vector3(0f, 1.5f, 0f), new Vector3(0f, -1.5f, 0f),
                new Vector3(2.5f, 0f, 0f), new Vector3(-2.5f, 0f, 0f),
                new Vector3(0f, 2.5f, 0f), new Vector3(0f, -2.5f, 0f),
                new Vector3(3.5f, 0f, 0f), new Vector3(-3.5f, 0f, 0f),
                new Vector3(0f, 3.5f, 0f), new Vector3(0f, -3.5f, 0f)
            };
            Vector3 anchor = _stationHandoffReceivingPosition != Vector3.Zero
                ? _stationHandoffReceivingPosition
                : (_stationHandoffGroundPosition != Vector3.Zero
                    ? _stationHandoffGroundPosition : _stationHandoffMarkerPosition);
            List<Ped> nearbyPeds;
            List<Vehicle> nearbyVehicles;
            try
            {
                nearbyPeds = World.GetNearbyPeds(anchor, 12f)
                    .Where(ped => !IsStationHandoffOwnedPed(ped)).ToList();
                nearbyVehicles = World.GetNearbyVehicles(anchor, 12f)
                    .Where(vehicle => vehicle != null && vehicle.Exists()
                        && (_transport == null || !_transport.Exists()
                            || vehicle.Handle != _transport.Handle)).ToList();
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_STATION_STANDBY_CLEARANCE_FAILED", ex);
                return false;
            }
            int start = (Math.Max(0, index) * 2) % offsets.Length;
            for (int offsetIndex = 0; offsetIndex < offsets.Length; offsetIndex++)
            {
                Vector3 candidate = anchor
                    + offsets[(start + offsetIndex) % offsets.Length];
                Vector3 grounded;
                if (!TryResolveStationStandbyPosition(candidate, out grounded)
                    || IsStationAccessOccupied(grounded, nearbyVehicles, nearbyPeds)
                    || _transport != null && _transport.Exists()
                        && grounded.DistanceTo(_transport.Position) < 4.0f)
                    continue;
                if (_stationHandoffOfficers.Any(officer => officer != null
                    && officer.Exists() && officer.Position.DistanceTo(grounded) < 1.5f))
                    continue;
                position = grounded;
                return true;
            }

            Vector3 authoredStandby;
            if (TryResolveStationStandbyPosition(_stationHandoffMarkerPosition,
                    out authoredStandby)
                && !IsStationAccessOccupied(authoredStandby, nearbyVehicles, nearbyPeds)
                && (_transport == null || !_transport.Exists()
                    || authoredStandby.DistanceTo(_transport.Position) >= 4.0f)
                && !_stationHandoffOfficers.Any(officer => officer != null
                    && officer.Exists()
                    && officer.Position.DistanceTo(authoredStandby) < 1.5f))
            {
                position = authoredStandby;
                return true;
            }
            return false;
        }

        private void DrawStationHandoffMarker()
        {
            if (_stationHandoffMarkerPosition == Vector3.Zero)
                return;
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()
                || player.Position.DistanceTo(_stationHandoffMarkerPosition) > 100f)
                return;
            Vector3 ground = _stationHandoffMarkerPosition;
            World.DrawMarker(
                MarkerType.Cylinder,
                ground + new Vector3(0f, 0f, -0.35f),
                Vector3.Zero,
                Vector3.Zero,
                new Vector3(1.8f, 1.8f, 1.25f),
                System.Drawing.Color.FromArgb(105, 38, 158, 218),
                false,
                false,
                false,
                null,
                null,
                false);
            World.DrawMarker(
                MarkerType.Cone,
                ground + new Vector3(0f, 0f, 1.7f),
                Vector3.Zero,
                new Vector3(180f, 0f, 0f),
                new Vector3(0.38f, 0.38f, 0.55f),
                System.Drawing.Color.FromArgb(235, 235, 245, 255),
                false,
                false,
                false,
                null,
                null,
                false);
        }

        private static Vector3 ResolveRoadPosition(Vector3 candidate)
        {
            try
            {
                Vector3 street = World.GetNextPositionOnStreet(candidate);
                // Do not let a freeway custody target resolve to a lower road
                // simply because it is the nearest XY street coordinate.
                if (street.DistanceTo(candidate) <= 45f
                    && Math.Abs(street.Z - candidate.Z) <= MaximumRoadElevationDifference)
                    return street;
            }
            catch { }
            return candidate;
        }

        private static Vector3 ResolveTransportRoadPosition(Vector3 target, int recoveryIndex)
        {
            // Keep the authored destination as the first choice. Recovery
            // candidates are deliberately small and bounded so the unit stays
            // at the station/custody area while escaping a blocked gate or
            // invalid road-level snap.
            Vector3[] offsets = recoveryIndex <= 0
                ? new[] { Vector3.Zero }
                : new[]
                {
                    // The former 16-metre offsets repeatedly resolved to the
                    // same road node on freeway/bridge geometry. These larger
                    // bounded offsets give GTA navigation a real alternate
                    // approach while keeping the unit in the active area.
                    new Vector3(38f, 0f, 0f),
                    new Vector3(-38f, 0f, 0f),
                    new Vector3(0f, 38f, 0f),
                    new Vector3(0f, -38f, 0f)
                };
            int offsetIndex = Math.Min(Math.Max(0, recoveryIndex - 1), offsets.Length - 1);
            Vector3 candidate = target + offsets[offsetIndex];
            Vector3 road = ResolveRoadPosition(candidate);
            float maximumRecoveryDistance = recoveryIndex <= 0
                ? ArrivalRadius : MaximumNearbyTransportCustodyRadius;
            if (road.DistanceTo(target) <= maximumRecoveryDistance
                && Math.Abs(road.Z - target.Z) <= MaximumRoadElevationDifference)
                return road;
            return ResolveRoadPosition(target);
        }

        private static bool TryGetDispatchSceneVehicleRoadNode(
            Vector3 scenePosition,
            float maximumDistance,
            out Vector3 roadPosition,
            out float distance)
        {
            roadPosition = Vector3.Zero;
            distance = 0f;
            if (scenePosition == Vector3.Zero || maximumDistance <= 0f)
                return false;

            try
            {
                using (OutputArgument nodeOutput = new OutputArgument())
                using (OutputArgument headingOutput = new OutputArgument())
                {
                    bool found = Function.Call<bool>(
                        Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        scenePosition.X,
                        scenePosition.Y,
                        scenePosition.Z,
                        nodeOutput,
                        headingOutput,
                        1,
                        3.0f,
                        0);
                    if (found)
                    {
                        Vector3 node = nodeOutput.GetResult<Vector3>();
                        float heading = headingOutput.GetResult<float>();
                        float nodeDistance = node.DistanceTo(scenePosition);
                        if (node != Vector3.Zero
                            && !float.IsNaN(heading)
                            && !float.IsInfinity(heading)
                            && nodeDistance <= maximumDistance
                            && Math.Abs(node.Z - scenePosition.Z) <= MaximumRoadElevationDifference
                            && Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                                node.X, node.Y, node.Z, 0))
                        {
                            roadPosition = node;
                            distance = nodeDistance;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Try the existing SHVDN street-node query below. The native
                // query can fail for a scene coordinate on a sidewalk or lot.
            }

            Vector3 bestRoadPosition = Vector3.Zero;
            float bestDistance = float.MaxValue;
            foreach (Vector3 queryOffset in DispatchSceneRoadNodeQueryOffsets)
            {
                try
                {
                    Vector3 queryPosition = scenePosition + queryOffset;
                    float heading;
                    Vector3 node = World.GetNextPositionOnStreetWithHeading(
                        queryPosition, out heading);
                    float nodeDistance = node.DistanceTo(scenePosition);
                    if (node == Vector3.Zero
                        || float.IsNaN(heading)
                        || float.IsInfinity(heading)
                        || nodeDistance > maximumDistance
                        || Math.Abs(node.Z - scenePosition.Z) > MaximumRoadElevationDifference
                        || nodeDistance >= bestDistance)
                        continue;

                    // This SHVDN helper resolves a vehicle street node; retain
                    // the scene-distance and elevation bounds, but do not run
                    // IS_POINT_ON_ROAD again because that native can reject a
                    // valid node returned from a nearby road query.
                    bestRoadPosition = node;
                    bestDistance = nodeDistance;
                }
                catch
                {
                    // One unavailable sample must not suppress the remaining
                    // three fixed nearby queries.
                }
            }

            if (bestRoadPosition == Vector3.Zero)
                return false;

            roadPosition = bestRoadPosition;
            distance = bestDistance;
            return true;
        }

        private static bool IsPrisonTransferPhase(CustodyPhase phase)
        {
            switch (phase)
            {
                case CustodyPhase.PreparingPrisonTransport:
                case CustodyPhase.PrisonTransportEnRoute:
                case CustodyPhase.PrisonLoading:
                case CustodyPhase.DriveToPrison:
                    return true;
                default:
                    return false;
            }
        }

        private void ClearTerminalState(ISet<int> preserveDispatchCustodyHandles = null)
        {
            CleanupCustodyBlips();
            CleanupWaypoint();
            ReleasePendingModelRequests();
            RestoreAllCustodyInvincibility(preserveDispatchCustodyHandles);
            _state = LSPDDispatchState.None;
            _phase = CustodyPhase.None;
            _transport = null;
            _transportDriver = null;
            _sceneEscortOfficers.Clear();
            _stationHandoffOfficers.Clear();
            _prisonHandoffOfficers.Clear();
            _routeThreatPeds.Clear();
            _routeThreatVehicle = null;
            ResetOperationFields(preserveDispatchCustodyHandles);
        }

        private void ResetOperationFields(ISet<int> preserveDispatchCustodyHandles = null)
        {
            if (preserveDispatchCustodyHandles == null)
                RestoreDispatchCustodyMovementStyles();
            ClearDispatchPickupPlan();
            _prisoners.Clear();
            _dispatchCustodyTransferredHandles.Clear();
            _dispatchGroupLoadedPendingHandoff.Clear();
            _dispatchGroupLastRoadAttemptAt.Clear();
            _dispatchGroupBackupEscorts.Clear();
            _dispatchGroupBackupLoaderWaitLogged.Clear();
            _dispatchGroupPlayerDrivenPickup = false;
            _dispatchGroupCollectionAwaitingMore = false;
            _dispatchGroupStationDeclineLogged = false;
            _dispatchGroupPlayerRoutePromptLogged = false;
            _dispatchGroupExpectedPrisonerCount = 0;
            _pickupTarget = Vector3.Zero;
            _dispatchSceneVehicleAccessTarget = Vector3.Zero;
            _dispatchSceneWalkInDistanceMeters = 0f;
            _stationDestination = Vector3.Zero;
            _stationIntakeDestination = Vector3.Zero;
            _stationHandoffMarkerPosition = Vector3.Zero;
            _stationHandoffGroundPosition = Vector3.Zero;
            _prisonDestination = Vector3.Zero;
            _stationDisplayName = string.Empty;
            _prisonDisplayName = string.Empty;
            _pendingVehicleModelName = string.Empty;
            _pendingOfficerModelName = string.Empty;
            _pendingPrisonerModelName = string.Empty;
            _requestedPrisonerProfileId = string.Empty;
            _routeThreatModelName = string.Empty;
            _routeThreatVehicleModelName = string.Empty;
            _routeThreatWeaponName = string.Empty;
            _preparationDeadline = DateTime.MinValue;
            _phaseStartedAt = DateTime.MinValue;
            _lastDriverTaskAt = DateTime.MinValue;
            _lastTransportProgressAt = DateTime.MinValue;
            _lastTransportProgressPosition = Vector3.Zero;
            _lastTransportRoadTaskTarget = Vector3.Zero;
            _nextTransportDriveReadinessLogAt = DateTime.MinValue;
            _nextTransportDriverSeatTaskAt = DateTime.MinValue;
            _dispatchTransportRouteRoadTarget = Vector3.Zero;
            _dispatchTransportRouteDestination = Vector3.Zero;
            _lastDispatchTransportRouteTargetFailure = string.Empty;
            _transportRouteRecoveryCount = 0;
            _lastPrisonerMaintenanceAt = DateTime.MinValue;
            _driverReleaseRequestedAt = DateTime.MinValue;
            _driverReleaseRequested = false;
            _dispatchSceneTransportDriver = null;
            _lastDispatchCrewDriverSeatCheckAt = DateTime.MinValue;
            _lastDispatchCrewDriverEntryTaskAt = DateTime.MinValue;
            _dispatchCrewDriverEntryStartedAt = DateTime.MinValue;
            _lastDispatchCrewRouteRecoveryCheckAt = DateTime.MinValue;
            _lastDispatchCrewArrivalCheckAt = DateTime.MinValue;
            _lastDispatchCrewStationRouteProgressAt = DateTime.MinValue;
            _dispatchCrewOfficerEntryTaskAt = DateTime.MinValue;
            _lastDispatchCrewStationGoalDistance = float.MaxValue;
            _dispatchCrewRouteRecoveryTarget = Vector3.Zero;
            _dispatchCrewDepartureOrigin = Vector3.Zero;
            _dispatchCrewDepartureStartedAt = DateTime.MinValue;
            _dispatchCrewDriverSeatOccupantHandle = 0;
            _dispatchCrewOfficerEntryTaskHandle = 0;
            _dispatchCrewStationTransport = false;
            _dispatchCrewCustodyHandoffReady = false;
            _dispatchClosedAfterCrewCustodyHandoff = false;
            _dispatchCrewExitStagingAttempted = false;
            _dispatchCrewExitStagingPending = false;
            _dispatchCrewExitStagingReached = false;
            _dispatchCrewOfficerSeatConflictLogged = false;
            _dispatchCrewDriverSeatConflictLogged = false;
            _dispatchCrewPlayerRecoveryPending = false;
            _dispatchCrewPlayerRecoveryReason = string.Empty;
            _transportOfficerReleaseRequestedAt = DateTime.MinValue;
            _nextTransportOfficerReleaseTaskAt = DateTime.MinValue;
            _lastActiveCustodyMaintenanceAt = DateTime.MinValue;
            _transportOfficerReleaseRequested = false;
            _transportOfficerReleaseRecoveryLogged = false;
            _transportNearCustodyFallback = false;
            _playerCustodyKeyDown = false;
            _playerOwnedTransport = false;
            _playerVehicleCustodySeat = VehicleSeat.LeftRear;
            ClearPendingDispatchRelease();
            _dispatchReleasedPrisoner = null;
            _completedAfterDispatchRelease = false;
            _playerCustodyOfferNotified = false;
            _playerUnloadingOfferNotified = false;
            _stationHandoffSearchMessageShown = false;
            _stationHandoffTargetMessageShown = false;
            _stationHandoffStopMessageShown = false;
            _lastStationHandoffSearchAt = DateTime.MinValue;
            _stationHandoffReceivingPosition = Vector3.Zero;
            _stationHandoffHeading = 0f;
            _stationHandoffProgressEvidence = string.Empty;
            _stationHandoffStallMessageShown = false;
            _stationHandoffAccessMessageShown = false;
            _stationHandoffActualPoseValidated = false;
            _stationHandoffAuthoredGroundFallbackUsed = false;
            _stationHandoffUsedGarageFallback = false;
            _stationHandoffActualPosePosition = Vector3.Zero;
            _stationHandoffActualPoseHeading = 0f;
            _stationHandoffArrivalOrdersIssued = false;
            _prisonHandoffArrivalOrdersIssued = false;
            _terminalKeyDown = false;
            _operationId = string.Empty;
            _activeAudioScope = string.Empty;
            _lastAudioStage = string.Empty;
            _lastFailureReason = string.Empty;
            _failedIsRecoverable = false;
            _failedAfterStationHandoff = false;
            _isRequestedConvoyActivity = false;
            _dispatchCustodyOperation = false;
            _routeThreatState = RouteThreatState.None;
            _routeThreatPreparationDeadline = DateTime.MinValue;
            _lastRouteThreatCombatTaskAt = DateTime.MinValue;
            _lastPrisonerTaskAt.Clear();
            _lastPrisonerVehicleEntryAt.Clear();
            _lastPrisonerVehicleExitAt.Clear();
            _lastAssignedSeatCheckAt.Clear();
            _lastAssignedSeatCheckResult.Clear();
            _prisonerInjuryStates.Clear();
            _lastPrisonerCuffPoseAt.Clear();
            _lastPrisonerEscortPosition.Clear();
            _lastEscortOfficerPosition.Clear();
            _lastPrisonerEscortProgressAt.Clear();
            _lastPrisonerEscortRecoveryAt.Clear();
            _prisonerLoadingStages.Clear();
            _prisonerLoadingStageStartedAt.Clear();
            _prisonerLoadingTimeoutStartedAt.Clear();
            _lastGroundedEscortFollowAt.Clear();
            _prisonerEscortAlignmentStarted.Clear();
            _prisonerEscortApproachLogged.Clear();
            _playerHandcuffAnimationUntil.Clear();
            _playerLoadingContactStartedAt.Clear();
            _playerEscortLostAt.Clear();
            _prisonerEscortOfficers.Clear();
            _prisonerEscortStartedAt.Clear();
            _lastEscortOfficerTaskAt.Clear();
            _prisonerUnloadingOfficers.Clear();
            _prisonerVehicleDoorsOpened.Clear();
            _prisonerDoorCloseTasksIssued.Clear();
            _prisonerDoorOpenTaskIssuedAt.Clear();
            _dispatchPrisonerMissingSince.Clear();
            _dispatchPrisonerLastHandleRetryAt.Clear();
            _dispatchSceneDoorAnimationFallbackIssued.Clear();
            _prisonerEntryTasksIssued.Clear();
            _physicalCuffReadFailuresLogged.Clear();
            _prisonerLoadedLogIssued.Clear();
            _playerEscortPrisoners.Clear();
            _playerEscortFallbacks.Clear();
            _playerDoorInteractions.Clear();
            _playerEntryInteractions.Clear();
            _prisonerUnloadingEscortStartedAt.Clear();
            _prisonerUnloadingDoorsOpened.Clear();
            _playerUnloadingStartedAt.Clear();
            _playerUnloadingDoorInteractions.Clear();
            _playerUnloadingTasksIssued.Clear();
            _playerUnloadingFallbackHandles.Clear();
            if (preserveDispatchCustodyHandles == null)
            {
                _custodyInvincibilityStates.Clear();
                _dispatchCustodyMovementStyleApplied.Clear();
                _dispatchCustodyMovementStyleRequestedAt.Clear();
            }
            else
            {
                foreach (int handle in _custodyInvincibilityStates.Keys
                    .Where(handle => !preserveDispatchCustodyHandles.Contains(handle)).ToArray())
                    _custodyInvincibilityStates.Remove(handle);
                foreach (int handle in _dispatchCustodyMovementStyleApplied
                    .Where(handle => !preserveDispatchCustodyHandles.Contains(handle)).ToArray())
                    _dispatchCustodyMovementStyleApplied.Remove(handle);
                foreach (int handle in _dispatchCustodyMovementStyleRequestedAt.Keys
                    .Where(handle => !preserveDispatchCustodyHandles.Contains(handle)).ToArray())
                    _dispatchCustodyMovementStyleRequestedAt.Remove(handle);
            }
        }

        private int DeferredCleanupGraceSeconds
        {
            get { return _cleanupSettings == null ? DefaultDeferredCleanupGraceSeconds : _cleanupSettings.CompletedSceneGraceSeconds; }
        }
        private int DeferredCleanupMaximumSeconds
        {
            get { return _cleanupSettings == null ? DefaultDeferredCleanupMaximumSeconds : _cleanupSettings.HardCleanupSeconds; }
        }

        private float CleanupDistance
        {
            get { return _cleanupSettings == null ? DefaultCleanupDistance : _cleanupSettings.SafeCleanupDistance; }
        }

        private static bool IsUsableModel(Model model, bool vehicle)
        {
            try { return model.IsValid && model.IsInCdImage && (vehicle ? model.IsVehicle : model.IsPed); }
            catch { return false; }
        }
        private static void ReleaseModel(Model model) { try { if (model.IsValid) model.MarkAsNoLongerNeeded(); } catch { } }

        private void ReleasePendingModelRequests()
        {
            if (!string.IsNullOrWhiteSpace(_pendingVehicleModelName))
                ReleaseModel(new Model(_pendingVehicleModelName));
            if (!string.IsNullOrWhiteSpace(_pendingOfficerModelName))
                ReleaseModel(new Model(_pendingOfficerModelName));
            if (!string.IsNullOrWhiteSpace(_pendingPrisonerModelName))
                ReleaseModel(new Model(_pendingPrisonerModelName));
            if (!string.IsNullOrWhiteSpace(_routeThreatModelName))
                ReleaseModel(new Model(_routeThreatModelName));
        }

        private static void DeleteDeferred(DeferredCleanup item)
        {
            if (item == null)
                return;
            if (item.RelatedPeds != null)
            {
                foreach (Ped ped in item.RelatedPeds)
                {
                    try { if (ped != null && ped.Exists()) ped.Delete(); } catch { }
                }
            }
            try { if (item.Ped != null && item.Ped.Exists()) item.Ped.Delete(); } catch { }
            try { if (item.Vehicle != null && item.Vehicle.Exists()) item.Vehicle.Delete(); } catch { }
        }

        private void CleanupCustodyBlips()
        {
            CleanupBlip(_transportBlip);
            CleanupBlip(_custodyOfficerBlip);
            CleanupBlip(_custodyPrisonerBlip);
            CleanupBlip(_prisonDestinationBlip);
            _transportBlip = null;
            _custodyOfficerBlip = null;
            _custodyPrisonerBlip = null;
            _prisonDestinationBlip = null;
            _custodyOfficerBlipHandle = 0;
            _custodyPrisonerBlipHandle = 0;
        }

        private void MaintainCustodyBlips()
        {
            MaintainPrisonDestinationBlip();
            MaintainAttachedVehicleBlip();

            Ped prisoner = ValidPrisoners().FirstOrDefault();
            MaintainAttachedPedBlip(
                ref _custodyPrisonerBlip,
                ref _custodyPrisonerBlipHandle,
                prisoner,
                "Custody Prisoner");

            Ped officer = null;
            if (prisoner != null)
            {
                switch (_phase)
                {
                    case CustodyPhase.SceneLoading:
                    case CustodyPhase.PrisonLoading:
                        officer = FindLoadingEscort(prisoner);
                        break;
                    case CustodyPhase.StationUnloading:
                    case CustodyPhase.HoldingAtStation:
                        officer = FindHandoffOfficer(prisoner, _stationHandoffOfficers);
                        break;
                    case CustodyPhase.PrisonHandoff:
                        officer = FindHandoffOfficer(prisoner, _prisonHandoffOfficers);
                        break;
                    default:
                        officer = _sceneEscortOfficers
                            .Concat(_stationHandoffOfficers)
                            .Concat(_prisonHandoffOfficers)
                            .FirstOrDefault(ped => ped != null && ped.Exists() && !ped.IsDead);
                        break;
                }
            }
            MaintainAttachedPedBlip(
                ref _custodyOfficerBlip,
                ref _custodyOfficerBlipHandle,
                officer,
                "Custody Officer");
        }

        private void MaintainPrisonDestinationBlip()
        {
            bool needed = _phase == CustodyPhase.DriveToPrison
                || _phase == CustodyPhase.PrisonHandoff;
            if (!needed || _prisonDestination == Vector3.Zero)
            {
                CleanupBlip(_prisonDestinationBlip);
                _prisonDestinationBlip = null;
                return;
            }
            if (_prisonDestinationBlip != null && _prisonDestinationBlip.Exists())
                return;
            try
            {
                _prisonDestinationBlip = World.CreateBlip(_prisonDestination);
                if (_prisonDestinationBlip != null && _prisonDestinationBlip.Exists())
                {
                    _prisonDestinationBlip.Name = "Prison Receiving Point";
                    _prisonDestinationBlip.Sprite = BlipSprite.PoliceStation;
                    _prisonDestinationBlip.Color = BlipColor.Blue;
                    _prisonDestinationBlip.IsShortRange = false;
                    LogRuntime(
                        "POLICE_CONVOY_PRISON_DESTINATION_MARKER_READY",
                        "Destination=" + _prisonDestination);
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PRISON_DESTINATION_MARKER_FAILED", ex);
            }
        }

        private void MaintainAttachedVehicleBlip()
        {
            if (_transport == null || !_transport.Exists())
            {
                CleanupBlip(_transportBlip);
                _transportBlip = null;
                return;
            }
            if (_transportBlip != null && _transportBlip.Exists())
                return;
            try
            {
                _transportBlip = _transport.AddBlip();
                if (_transportBlip != null && _transportBlip.Exists())
                {
                    _transportBlip.Name = "Police Custody Transport";
                    _transportBlip.Sprite = BlipSprite.PoliceCar;
                    _transportBlip.Color = BlipColor.Blue;
                    _transportBlip.IsShortRange = false;
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_TRANSPORT_BLIP_FAILED", ex);
            }
        }

        private static void MaintainAttachedPedBlip(
            ref Blip blip,
            ref int entityHandle,
            Ped ped,
            string name)
        {
            if (ped == null || !ped.Exists() || ped.IsDead)
            {
                CleanupBlip(blip);
                blip = null;
                entityHandle = 0;
                return;
            }
            if (blip != null && blip.Exists() && entityHandle == ped.Handle)
                return;
            CleanupBlip(blip);
            blip = null;
            try
            {
                blip = ped.AddBlip();
                entityHandle = ped.Handle;
                if (blip != null && blip.Exists())
                {
                    blip.Name = name;
                    blip.Color = BlipColor.Blue;
                    blip.IsShortRange = false;
                }
            }
            catch
            {
                entityHandle = 0;
            }
        }

        private static void CleanupBlip(Blip blip)
        {
            try { if (blip != null && blip.Exists()) blip.Delete(); } catch { }
        }

        private void CaptureAndSetWaypoint(Vector3 position)
        {
            if (!_uiSettings.ShowWaypoint) return;
            try
            {
                if (!_hadPreviousWaypoint && Game.IsWaypointActive)
                {
                    _previousWaypoint = World.WaypointPosition;
                    _hadPreviousWaypoint = true;
                }
                World.WaypointPosition = position;
            }
            catch (Exception ex) { LogException("POLICE_TRANSPORT_GPS_FAILED", ex); }
        }

        private void CleanupWaypoint()
        {
            try
            {
                if (_hadPreviousWaypoint) World.WaypointPosition = _previousWaypoint;
                else if (Game.IsWaypointActive) World.RemoveWaypoint();
            }
            catch { }
            _hadPreviousWaypoint = false;
            _previousWaypoint = Vector3.Zero;
        }

        private void SetPhase(CustodyPhase phase)
        {
            Ped prisoner = _prisoners.FirstOrDefault();
            LSDeveloperRuntime.StateTransition(
                _log == null ? string.Empty : _log.SessionId,
                "Convoy",
                "POLICE_CONVOY_PHASE",
                _phase.ToString(),
                phase.ToString(),
                prisoner == null ? 0 : prisoner.Handle,
                0,
                _transport == null ? 0 : _transport.Handle,
                "Prisoners=" + PrisonerCount + "; PickupTarget=" + _pickupTarget);

            _phase = phase;
            _phaseStartedAt = DateTime.UtcNow;
            if (phase == CustodyPhase.SceneLoading
                || phase == CustodyPhase.PrisonLoading)
                _prisonerLoadingTimeoutStartedAt.Clear();
            _lastPrisonerTaskAt.Clear();
            _lastPrisonerVehicleEntryAt.Clear();
            _lastPrisonerVehicleExitAt.Clear();
            _lastAssignedSeatCheckAt.Clear();
            _lastAssignedSeatCheckResult.Clear();
            _lastPrisonerCuffPoseAt.Clear();
            _prisonerUnloadingOfficers.Clear();
            if (phase == CustodyPhase.StationUnloading
                || phase == CustodyPhase.PrisonHandoff)
            {
                _prisonerUnloadingEscortStartedAt.Clear();
                _prisonerUnloadingOfficers.Clear();
                _prisonerUnloadingDoorsOpened.Clear();
                _playerUnloadingOfferNotified = false;
                _playerUnloadingStartedAt.Clear();
                _playerUnloadingDoorInteractions.Clear();
                _playerUnloadingTasksIssued.Clear();
                _playerUnloadingFallbackHandles.Clear();
                _stationHandoffProgressEvidence = string.Empty;
                _stationHandoffStallMessageShown = false;
                _stationHandoffAccessMessageShown = false;
                _stationHandoffActualPoseValidated = false;
            }
            LogRuntime("POLICE_CONVOY_PHASE", "Phase=" + phase + "; Prisoners=" + PrisonerCount);
        }

        private void ReportStage(string eventId, string stage)
        {
            if (string.IsNullOrWhiteSpace(_operationId) || string.Equals(_lastAudioStage, stage, StringComparison.Ordinal)) return;
            CloseActiveAudio();
            _activeAudioScope = "police-transport-" + _operationId + "-" + stage;
            _lastAudioStage = stage;
            try { _audio.Report(eventId, _activeAudioScope, stage + "-" + _operationId, "transport"); } catch { }
        }

        private void ReportTerminal(string eventId, string stage)
        {
            if (string.IsNullOrWhiteSpace(_operationId)) return;
            CloseActiveAudio();
            _activeAudioScope = "police-transport-terminal-" + _operationId + "-" + stage;
            _lastAudioStage = stage;
            try { _audio.Report(eventId, _activeAudioScope, stage + "-" + _operationId, "transport"); } catch { }
        }
        private void CloseActiveAudio() { if (string.IsNullOrWhiteSpace(_activeAudioScope)) return; try { _audio.CancelScope(_activeAudioScope); } catch { } _activeAudioScope = string.Empty; }
        private static void Notify(string message) { try { Notification.PostTicker(message, false, false); } catch { } }
        private void LogRuntime(string category, string message) { if (_log != null) _log.Runtime(category, message); }
        private void LogStateFailure(string category, string message) { if (_log != null) _log.StateFailure(category, message); }
        private void LogException(string category, Exception ex) { if (_log != null) _log.Exception(category, ex); }
        private string DescribePrisoners() { return string.Join(",", ValidPrisoners().Select(ped => ped.Handle.ToString()).ToArray()); }
        private string DescribePrisonerReferences()
        {
            if (_prisoners.Count == 0)
                return "none";
            return string.Join(",", _prisoners.Select(ped =>
            {
                if (ped == null)
                    return "null";
                try
                {
                    return ped.Handle + ":" + (!ped.Exists()
                        ? "deleted" : ped.IsDead ? "dead" : "alive");
                }
                catch
                {
                    return ped.Handle + ":unreadable";
                }
            }).ToArray());
        }
    }
}
