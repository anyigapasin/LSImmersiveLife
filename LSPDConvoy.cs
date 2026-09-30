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
    /// Owns the physical custody continuation after Dispatch has arrested a
    /// suspect or a group. Convoy never fakes custody by creating a van at the
    /// player or inserting prisoners into it: a staged transport first drives
    /// to the location, prisoners walk and enter through GTA's task system,
    /// the player drives to station, and a second prison unit repeats that
    /// physical handoff before booking at Bolingbroke.
    /// </summary>
    internal sealed class LSPDConvoy
    {
        private const int ModelPreparationTimeoutSeconds = 20;
        private const int DriverTaskRefreshMilliseconds = 5000;
        private const int DriverReleaseTimeoutSeconds = 12;
        private const int PrisonerTaskRefreshMilliseconds = 900;
        // Vehicle entry is a multi-step GTA animation. Reissuing TASK_ENTER_VEHICLE
        // while that animation is still active cancels the escort's first attempt
        // and leaves the prisoner circling/struggling at the open door. Keep one
        // task owned by the escort for its full bounded window, then permit a
        // controlled retry if the game genuinely failed to complete it.
        private const int PrisonerVehicleEntryRefreshMilliseconds = 15000;
        private const int PrisonerMaintenanceRefreshMilliseconds = 1000;
        private const int TransportOfficerReleaseTimeoutSeconds = 12;
        private const int TransportOfficerTaskRefreshMilliseconds = 2500;
        private const int PrisonerEscortSettleMilliseconds = 1600;
        private const int PrisonerEscortAlignmentMilliseconds = 650;
        private const int PrisonerDoorOpenSettleMilliseconds = 900;
        private const int PhysicalEscortAttachRetryMilliseconds = 900;
        private const int PhysicalEscortMaximumAttachAttempts = 3;
        private const int PrisonerCuffPoseRefreshMilliseconds = 2500;
        private const int ActiveCustodyMaintenanceRefreshMilliseconds = 1000;
        // Player custody is a deliberate multi-step interaction. Give the
        // paired arrest animation time to finish and give the player enough
        // time to walk the subject to the marked rear door without allowing
        // the ordinary loading timeout to cancel a valid handoff.
        private const int PlayerHandcuffAnimationMilliseconds = 2600;
        private const int PlayerLoadingContactMilliseconds = 1200;
        private const int PlayerUnloadingInteractionTimeoutSeconds = 20;
        private const int PlayerEscortTimeoutSeconds = 150;
        private const int PlayerEscortReleaseGraceSeconds = 10;
        private const float PlayerEscortMaximumDistance = 35.0f;
        private const float PlayerEscortFollowDistance = 2.0f;
        private const float PlayerCustodyInteractionRadius = 4.0f;
        private const float PlayerCustodyVehicleRadius = 3.2f;
        private const float PlayerCustodyLoadInteractionRadius = 8.0f;
        private const float TransportOfficerContactRadius = 1.35f;
        private const float PhysicalEscortAttachRadius = 1.9f;
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
        private const int EscortStallTimeoutSeconds = 8;
        private const int EscortRecoveryCooldownSeconds = 8;
        private const float EscortProgressDistance = 0.75f;
        private const int TransportRouteStallTimeoutSeconds = 12;
        private const int MaximumTransportRouteRecoveries = 3;
        private const int ArrivalRadius = 18;
        private const int DefaultDeferredCleanupGraceSeconds = 8;
        private const int DefaultDeferredCleanupMaximumSeconds = 60;
        private const int DeferredCleanupCheckMilliseconds = 1000;
        private const float DefaultCleanupDistance = 200f;
        private const float MaximumRoadElevationDifference = 8f;

        private enum CustodyPhase
        {
            None,
            PreparingRequestedPrisoner,
            PreparingSceneTransport,
            SceneTransportEnRoute,
            SceneLoading,
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
            internal DateTime Earliest;
            internal DateTime Expires;
        }

        private readonly LSPDAudioDispatch _audio;
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveLog _log;
        private readonly LSPDControlBindings _controls;
        private readonly LSPoliceConvoySettings _settings;
        private readonly LSPoliceCleanupSettings _cleanupSettings;
        private readonly LSUniversalUiSettings _uiSettings;
        private readonly List<Ped> _prisoners = new List<Ped>();
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
        private readonly Dictionary<int, DateTime> _lastPhysicalEscortAttachAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, Vector3> _prisonerEscortRootOffsets =
            new Dictionary<int, Vector3>();
        private readonly Dictionary<int, DateTime> _lastPrisonerEscortAnimationAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _physicalEscortAttachAttempts =
            new Dictionary<int, int>();
        private readonly Dictionary<int, string> _prisonerEscortAnimationClips =
            new Dictionary<int, string>();
        private readonly HashSet<int> _prisonerEscortAlignmentStarted = new HashSet<int>();
        private readonly HashSet<int> _prisonerEscortApproachLogged = new HashSet<int>();
        private readonly Dictionary<int, Ped> _prisonerUnloadingOfficers = new Dictionary<int, Ped>();
        private readonly Dictionary<int, DateTime> _lastPrisonerCuffPoseAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _playerHandcuffAnimationUntil = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _playerLoadingContactStartedAt = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _playerEscortLostAt = new Dictionary<int, DateTime>();
        private readonly HashSet<int> _prisonerVehicleDoorsOpened = new HashSet<int>();
        private readonly HashSet<int> _prisonerEntryTasksIssued = new HashSet<int>();
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
        private readonly Dictionary<int, bool> _custodyInvincibilityStates = new Dictionary<int, bool>();

        private Vehicle _transport;
        private Ped _transportDriver;
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
        private Vector3 _stationDestination;
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
        private int _transportRouteRecoveryCount;
        private DateTime _lastPrisonerMaintenanceAt = DateTime.MinValue;
        private DateTime _lastDeferredCleanupAt = DateTime.MinValue;
        private DateTime _driverReleaseRequestedAt = DateTime.MinValue;
        private bool _driverReleaseRequested;
        private DateTime _transportOfficerReleaseRequestedAt = DateTime.MinValue;
        private DateTime _nextTransportOfficerReleaseTaskAt = DateTime.MinValue;
        private DateTime _lastActiveCustodyMaintenanceAt = DateTime.MinValue;
        private bool _transportOfficerReleaseRequested;
        private bool _transportOfficerReleaseRecoveryLogged;
        private bool _transportNearCustodyFallback;
        private bool _playerCustodyKeyDown;
        private bool _playerOwnedTransport;
        private bool _playerCustodyOfferNotified;
        private bool _playerUnloadingOfferNotified;
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
        internal bool Completed { get { return _phase == CustodyPhase.Completed; } }
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

            ResetOperationFields();
            _operationId = Guid.NewGuid().ToString("N");
            _prisoners.AddRange(valid);
            _pickupTarget = pickupTarget;
            _stationDestination = StationVehiclePosition(station);
            _prisonDestination = new Vector3(prison.ExteriorX, prison.ExteriorY, prison.ExteriorZ);
            _stationDisplayName = station.DisplayName;
            _prisonDisplayName = prison.DisplayName;
            _state = LSPDDispatchState.AwaitingTransport;
            _failedIsRecoverable = true;
            foreach (Ped prisoner in _prisoners)
                // Dispatch has already completed the player's physical
                // handcuff interaction. Do not replace that secured pose with
                // a fresh hands-up/ambient task while the transport is being
                // prepared.
                PreparePrisoner(prisoner, false);

            ReportStage("lsimmersivelife.police.transport.requested", "requested");
            BeginTransportPreparation(CustodyPhase.PreparingSceneTransport, VehicleName("policet", "fbi2"));
            LogRuntime("POLICE_CUSTODY_STARTED",
                "Prisoners=" + DescribePrisoners() + "; Pickup=" + _pickupTarget
                + "; Station=" + _stationDisplayName + "; Prison=" + _prisonDisplayName);
            return valid.Count == 1
                ? "Prisoner secured. A transport unit is staging and will drive to the scene."
                : valid.Count + " prisoners secured. A group transport unit is staging and will drive to the scene.";
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
            bool prisonerAlreadySecuredByPlayer)
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
            bool validPlayerVehicle = allowPlayerOnFoot
                ? CanUsePlayerVehicleCustodyForNpc(player, playerVehicle)
                : CanUsePlayerVehicleCustody(player, playerVehicle);
            if (!validPlayerVehicle)
                return "Player custody transport requires one living suspect and a Police car; Police motorcycles use a staged transport unit.";

            LSPDPoliceStationDefinition station = SelectedStation();
            LSPDPoliceStationDefinition prison = _profile == null ? null : _profile.FindStation("bolingbroke");
            if (station == null)
                return "No operational Police station is configured.";
            if (prison == null)
                return "No prison destination is configured.";

            ResetOperationFields();
            _operationId = Guid.NewGuid().ToString("N");
            _prisoners.Add(prisoner);
            _pickupTarget = pickupTarget;
            _stationDestination = StationVehiclePosition(station);
            _prisonDestination = new Vector3(prison.ExteriorX, prison.ExteriorY, prison.ExteriorZ);
            _stationDisplayName = station.DisplayName;
            _prisonDisplayName = prison.DisplayName;
            _transport = playerVehicle;
            _playerOwnedTransport = true;
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
            _stationDestination = StationVehiclePosition(station);
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
            // A player-requested Convoy deliberately enters this preparation
            // phase before its XML-selected prisoner exists. Begin validating
            // custody as soon as that prisoner is staged; applying the normal
            // loss guard one tick earlier made every independent request fail.
            if (_phase != CustodyPhase.PreparingRequestedPrisoner
                && !MaintainPrisonerAvailability())
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
            MaintainActiveCustody(now);
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

        private string ProcessPlayerCustodyInput(DateTime now, bool allowGameplayInput)
        {
            bool keyDown = Game.IsKeyPressed(_controls.SecureKey);
            bool pressed = allowGameplayInput && keyDown && !_playerCustodyKeyDown;
            _playerCustodyKeyDown = keyDown;
            if (!pressed)
                return string.Empty;
            if (_phase == CustodyPhase.StationUnloading || _phase == CustodyPhase.PrisonHandoff)
                return ProcessPlayerPrisonerUnloadInput(now);
            if (_phase != CustodyPhase.SceneLoading && _phase != CustodyPhase.PrisonLoading)
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
                _physicalEscortAttachAttempts.Remove(prisonerHandle);
                _lastPhysicalEscortAttachAt.Remove(prisonerHandle);
                if (previousEscort != null && previousEscort.Exists())
                {
                    _lastEscortOfficerTaskAt.Remove(previousEscort.Handle);
                    _lastEscortOfficerPosition.Remove(previousEscort.Handle);
                }

                _playerEscortPrisoners.Add(prisonerHandle);
                _prisonerEscortOfficers[prisonerHandle] = player;
                _prisonerEscortStartedAt[prisonerHandle] = now;
                SetPrisonerLoadingStage(target, player,
                    PrisonerLoadingStage.SecuringContact, now,
                    "PlayerStartedHandcuffInteraction");
                _playerHandcuffAnimationUntil[prisonerHandle] =
                    now.AddMilliseconds(PlayerHandcuffAnimationMilliseconds);
                _playerEscortLostAt.Remove(prisonerHandle);
                LogRuntime(
                    "POLICE_CONVOY_ESCORT_STARTED",
                    "Prisoner=" + prisonerHandle + "; Escort=" + player.Handle
                    + "; Owner=Player; Stage=SecuringContact");
                try
                {
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, target, true);
                    target.BlockPermanentEvents = true;
                    target.CanSwitchWeapons = false;
                    // Clear the suspect's previous surrender/ambient task
                    // once, before the paired arrest task takes ownership.
                    // The custody flags are reasserted by the maintenance
                    // path and are never released during this interaction.
                    target.Task.ClearAll();
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        player, target, 1000);
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        target, player, 1000);
                    Function.Call(Hash.TASK_ARREST_PED, player, target);
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
            float playerDoorDistance = player.Position.DistanceTo(entry);
            float prisonerDoorDistance = target.Position.DistanceTo(entry);
            bool doorAlreadyOpenedByPlayer = _playerDoorInteractions.Contains(prisonerHandle);
            bool playerOwnsAttachedEscort = _playerEscortPrisoners.Contains(prisonerHandle)
                && IsPlayerEscort(player)
                && IsPrisonerAttachedToEscort(target, player);
            bool playerOwnedEscortAtRearDoor = playerOwnsAttachedEscort
                && playerDoorDistance <= PlayerCustodyVehicleRadius
                && _transport != null && _transport.Exists()
                && player.Position.DistanceTo(_transport.Position)
                    <= PlayerCustodyLoadInteractionRadius
                && target.Position.DistanceTo(_transport.Position)
                    <= PlayerCustodyLoadInteractionRadius;
            // The measured attachment keeps the prisoner beside the player,
            // but the two entity roots do not necessarily land on the exact
            // hard-coded door-offset point.  On addon Police cruisers this
            // can leave the officer and prisoner visibly beside the correct
            // rear quarter while the strict door-point test still rejects E.
            // Treat the attached pair as being at the custody vehicle when
            // both actors are inside the existing load-interaction radius.
            // This does not move either actor or choose an arbitrary seat; it
            // only makes the already-reached physical handoff usable.
            bool playerOwnedPairAtCustodyVehicle = playerOwnsAttachedEscort
                && _transport != null && _transport.Exists()
                && player.Position.DistanceTo(_transport.Position)
                    <= PlayerCustodyLoadInteractionRadius
                && target.Position.DistanceTo(_transport.Position)
                    <= PlayerCustodyLoadInteractionRadius
                && player.Position.DistanceTo(target.Position)
                    <= PlayerCustodyLoadInteractionRadius;
            bool bothActorsAtRearDoor =
                (playerDoorDistance <= PlayerCustodyVehicleRadius
                    && prisonerDoorDistance <= PlayerCustodyVehicleRadius)
                || playerOwnedEscortAtRearDoor
                || playerOwnedPairAtCustodyVehicle;
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
                + "; PlayerEscortAttached=" + playerOwnsAttachedEscort
                + "; PlayerEscortAtDoor=" + playerOwnedEscortAtRearDoor
                + "; PlayerEscortAtVehicle=" + playerOwnedPairAtCustodyVehicle
                + "; PlayerVehicleDistance=" + (_transport == null || !_transport.Exists()
                    ? "unavailable" : player.Position.DistanceTo(_transport.Position).ToString("0.0")));
            if (!bothActorsAtRearDoor && !stillAtOpenedCustodyVehicle)
            {
                // The player owns the escort after choosing it with E. Keep
                // the cuffed pedestrian physically linked to the player; a
                // second E must not issue a civilian follow task or repeatedly
                // overwrite either actor's movement animation.
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

            int doorIndex = PrisonerDoorIndex(seat);
            // A transport officer can have opened the physical door before
            // the player claims custody. The player still needs a registered
            // door interaction so the following E is accepted as the load
            // command rather than being blocked by an NPC-owned door flag.
            if (!_playerDoorInteractions.Contains(prisonerHandle))
            {
                bool alreadyOpenedByOfficer = _prisonerVehicleDoorsOpened.Contains(prisonerHandle);
                try
                {
                    SetPrisonerLoadingStage(target, player,
                        PrisonerLoadingStage.VehicleDoorReached, now,
                        "PlayerAndPrisonerAtRearPassengerDoor");
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        player, target, 1000);
                    Function.Call(Hash.TASK_OPEN_VEHICLE_DOOR,
                        player, _transport, 4000, doorIndex, 1.0f);
                    Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                        _transport, doorIndex, false, false);
                    _prisonerVehicleDoorsOpened.Add(prisonerHandle);
                    _playerDoorInteractions.Add(prisonerHandle);
                    SetPrisonerLoadingStage(target, player,
                        PrisonerLoadingStage.DoorOpened, now,
                        "PlayerOpenedRearDoor");
                    LogRuntime(
                        "POLICE_CONVOY_PLAYER_PRISONER_DOOR_OPENED",
                        "Prisoner=" + prisonerHandle + "; Player=" + player.Handle
                        + "; Vehicle=" + _transport.Handle + "; Door=" + doorIndex
                        + "; PreviouslyOpenedByOfficer=" + alreadyOpenedByOfficer);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PLAYER_PRISONER_DOOR_OPEN_FAILED", ex);
                    return "The transport rear door could not be opened safely.";
                }
                Notify("~b~POLICE CUSTODY~s~\nRear door open. Press E again to load the handcuffed prisoner.");
                return "Rear transport door opened. Press E again to load the prisoner.";
            }

            if (_playerEntryInteractions.Contains(prisonerHandle))
                return "The officer is loading the handcuffed prisoner into the transport.";

            _playerEntryInteractions.Add(prisonerHandle);
            _playerLoadingContactStartedAt[prisonerHandle] = now;
            try
            {
                // A short second contact task gives the player a visible
                // officer-led loading moment before GTA performs the final
                // foot-to-seat movement. The door is already open and the
                // prisoner remains handcuffed, so this cannot become a normal
                // passenger entry.
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    player, target, 800);
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    target, player, 800);
                Function.Call(Hash.TASK_ARREST_PED, player, target);
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
                try
                {
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        player, target, 800);
                    Function.Call(Hash.TASK_OPEN_VEHICLE_DOOR,
                        player, _transport, 4000, doorIndex, 1.0f);
                    Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                        _transport, doorIndex, false, false);
                    _prisonerUnloadingDoorsOpened.Add(prisonerHandle);
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

                LogRuntime("POLICE_CONVOY_PLAYER_PRISONER_UNLOAD_DOOR_OPENED",
                    "Stage=" + _phase + "; Prisoner=" + prisonerHandle
                    + "; Player=" + player.Handle + "; Vehicle=" + _transport.Handle
                    + "; Door=" + doorIndex
                    + "; PreviouslyOpenedByOfficer=" + alreadyOpenedByOfficer);
                Notify("~b~POLICE CUSTODY~s~\nRear door open. Press E again to bring the handcuffed prisoner out; the standby officer will escort them to intake.");
                return "Rear door opened. Press E again to unload the handcuffed prisoner.";
            }

            if (_playerUnloadingTasksIssued.Contains(prisonerHandle))
                return "The prisoner is exiting the transport; the standby officer will take over the receiving escort.";

            try
            {
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    player, target, 800);
                // This is an explicit player command after the rear door has
                // been opened. GTA performs the actual exit; Convoy does not
                // warp the prisoner, and the receiving officer resumes escort
                // as soon as the prisoner is physically outside.
                _lastPrisonerTaskAt.Remove(prisonerHandle);
                CommandLeaveTransport(target);
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
            if (!Failed || !_failedIsRecoverable)
                return new List<Ped>();
            List<Ped> result = ValidPrisoners().ToList();
            ClearDeferredCleanupBeforeRetry();
            _prisoners.Clear();
            ClearTerminalState();
            return result;
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

        internal void Reset()
        {
            CloseActiveAudio();
            ReleasePendingModelRequests();
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
                DeleteDeferred(item);
            _deferredCleanup.Clear();
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
                if (!CreateStagedTransport(vehicleModel, officerModel, prisonTrip))
                    return Fail("Police transport could not be staged on a safe road position.", true);
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
            Vector3 target = prisonTrip ? _stationDestination : _pickupTarget;

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

            Vector3 spawn = FindSafeStagingPosition(player, target, prisonTrip);
            Vehicle vehicle = World.CreateVehicle(
                vehicleModel,
                spawn,
                HeadingToward(spawn, target, player.Heading));
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
            _lastTransportProgressPosition = spawn;
            _lastTransportProgressAt = DateTime.UtcNow;
            _transportRouteRecoveryCount = 0;
            _driverReleaseRequested = false;
            _driverReleaseRequestedAt = DateTime.MinValue;
            _transportOfficerReleaseRequested = false;
            _transportOfficerReleaseRequestedAt = DateTime.MinValue;
            _transportOfficerReleaseRecoveryLogged = false;
            MaintainActiveCustody(DateTime.UtcNow, true);
            DriveTransportTo(target, true);
            LogRuntime("POLICE_CONVOY_TRANSPORT_STAGED",
                "Vehicle=" + vehicle.Handle + "; Driver=" + driver.Handle
                + "; Spawn=" + spawn + "; Target=" + target + "; PrisonTrip=" + prisonTrip);
            return true;
        }

        private bool IsNearbyTransportUsableForCustody(Vector3 target)
        {
            if (!HasUsableTransport())
                return false;

            float verticalDifference = Math.Abs(_transport.Position.Z - target.Z);
            if (verticalDifference > MaximumRoadElevationDifference)
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
            Vector3 target = prisonTrip ? _stationDestination : _pickupTarget;
            if (_transport.Position.DistanceTo(target) > ArrivalRadius)
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
                    if (IsNearbyTransportUsableForCustody(target))
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
            SetEmergencySignals(_transport, false);
            if (!ReleaseDriverForPlayer(now))
                return string.Empty;
            // The driver reference is intentionally cleared once the driver
            // has left. Move into the loading state before waiting for the
            // passenger escort; otherwise the next Script tick re-enters the
            // arrival method and incorrectly treats Driver=null as a lost
            // transport.
            SetPhase(prisonTrip ? CustodyPhase.PrisonLoading : CustodyPhase.SceneLoading);
            foreach (Ped prisoner in ValidPrisoners().Where(ped => !IsInTransport(ped)))
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
            foreach (Ped prisoner in prisoners)
            {
                if (IsInTransport(prisoner))
                {
                    Ped completedEscort;
                    _prisonerEscortOfficers.TryGetValue(
                        prisoner.Handle, out completedEscort);
                    DetachPrisonerFromEscort(prisoner, completedEscort,
                        "PhysicalVehicleEntryConfirmed");
                    SetPrisonerLoadingStage(prisoner, completedEscort,
                        PrisonerLoadingStage.Loaded, now,
                        "PrisonerConfirmedInsideTransport");
                    if (_prisonerLoadedLogIssued.Add(prisoner.Handle))
                    {
                        LogRuntime(
                            "POLICE_CONVOY_PRISONER_LOADED",
                            "Prisoner=" + prisoner.Handle + "; Vehicle=" + _transport.Handle
                            + "; Phase=" + _phase + "; PhysicalEntry=true");
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
                MaintainPrisonerCustodyState(prisoner);

                VehicleSeat seat = PrisonerSeat(prisoner, prisoners);
                if (seat == VehicleSeat.None)
                    return Fail(
                        "No rear custody seat is available for every prisoner in this transport.",
                        true);

                // If the player is already at the active custody scene, keep
                // the prisoner waiting for the player's explicit E handoff
                // before assigning an NPC arrest task. This is intentionally a
                // bounded offer: a player who is away from the scene or does
                // not take custody still gets the existing transport-officer
                // fallback rather than an indefinite loading state.
                if (ShouldOfferPlayerCustody(prisoner, now))
                {
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

                // Convoy normally has a released transport officer to own this
                // physical handoff. Never fall through to a normal autonomous
                // EnterVehicle task: the escort must own the custody handoff.
                Ped loadingEscort = FindLoadingEscort(prisoner);
                if (loadingEscort == null || IsOfficerInTransport(loadingEscort))
                {
                    // A group transport can legitimately have fewer officers
                    // than prisoners. Keep the remaining secured subjects in
                    // Convoy ownership and expose the player handoff instead
                    // of failing the whole operation merely because the last
                    // subject has no free NPC escort yet.
                    if (CanPlayerTakeCustody(prisoner))
                        continue;
                    if (now >= _phaseStartedAt.AddSeconds(LoadingTimeoutSeconds))
                        return Fail("No transport officer remained available for the physical prisoner handoff.", true);
                    continue;
                }
                if (MaintainPrisonerLoadingEscort(prisoner, seat, now))
                    continue;
                if (now >= _phaseStartedAt.AddSeconds(LoadingTimeoutSeconds))
                    return Fail("The transport officer could not complete the physical prisoner handoff.", true);
            }

            if (ValidPrisoners().All(IsInTransport))
            {
                if (!ReleaseDriverForPlayer(now))
                    return string.Empty;
                LogRuntime(
                    "POLICE_CONVOY_TRANSPORT_READY",
                    "Mode=" + (_isRequestedConvoyActivity ? "IndependentConvoy" : "DispatchCustody")
                    + "; PrisonTrip=" + prisonTrip
                    + "; Prisoners=" + DescribePrisoners()
                    + "; Vehicle=" + _transport.Handle
                    + "; PlayerDriverRequired=true");
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
                // Likewise, stage station staff while the transport is still
                // at the arrest scene. The later arrival phase only gives
                // existing officers their visible receiving task.
                EnsureStationHandoffOfficers();
                CaptureAndSetWaypoint(_stationDestination);
                ReportStage("lsimmersivelife.police.transport.departing", "station-departing");
                SetPhase(CustodyPhase.DriveToStation);
                return "Prisoner loaded. Take the transport and drive to " + _stationDisplayName + ".";
            }

            if (now >= _phaseStartedAt.AddSeconds(LoadingTimeoutSeconds))
                return Fail("Prisoner loading did not complete physically before the custody timeout.", true);
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
                _playerUnloadingStartedAt.Remove(prisonerHandle);
                _playerUnloadingDoorInteractions.Remove(prisonerHandle);
                _playerUnloadingTasksIssued.Remove(prisonerHandle);
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
            int index = prisoners.IndexOf(prisoner);
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
                try { _transportDriver.Delete(); } catch { }
                _transportDriver = null;
                LogRuntime("POLICE_CONVOY_DRIVER_RELEASE_RECOVERY", "Driver exit task timed out.");
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

        private string ProcessPlayerArrivalAtStation(DateTime now)
        {
            if (!PlayerDrivesTransport())
                return string.Empty;
            if (_transport.Position.DistanceTo(_stationDestination) > _settings.StationArrivalRadius)
                return string.Empty;
            SetPhase(CustodyPhase.StationUnloading);
            EnsureStationHandoffOfficers();
            OrderStationHandoffGuards();
            _playerUnloadingOfferNotified = true;
            LogRuntime("POLICE_CONVOY_PLAYER_UNLOAD_AVAILABLE",
                "Stage=STATION; E=OpenRearDoorThenUnload; StandbyOfficer=CompletesIntake");
            Notify("~b~POLICE CUSTODY~s~\nStation reached. Exit the transport; at the rear door press E to open it, then E again to unload. The station officer completes intake.");
            return "Prisoner transport arrived at " + _stationDisplayName + ". Station handoff is in progress.";
        }

        private string ProcessStationUnloading(DateTime now)
        {
            if (!EnsureStationHandoffOfficers())
            {
                if (now >= _phaseStartedAt.AddSeconds(ModelPreparationTimeoutSeconds))
                    return Fail("Station handoff officers could not be prepared safely.", true);
                return string.Empty;
            }
            OrderStationHandoffGuards();
            foreach (Ped prisoner in ValidPrisoners())
            {
                if (ShouldOfferPlayerUnloading(prisoner, now))
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
            List<Ped> stationPrisoners = ValidPrisoners().ToList();
            if (stationPrisoners.Count > 0 && stationPrisoners.All(prisoner =>
                !IsInTransport(prisoner)
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
                Notify("~g~POLICE CUSTODY~s~\nPrisoner(s) received at the station. Approve or decline prison transfer.");
                return "Prisoner(s) arrived at the selected Police station. Approve or decline prison transfer.";
            }
            if (now >= _phaseStartedAt.AddSeconds(PhysicalPhaseTimeoutSeconds))
                return Fail("Station handoff did not complete physically before the custody timeout.", true);
            return string.Empty;
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
            if (_stationHandoffOfficers.Any(ped => ped != null && ped.Exists()))
                return true;
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
                int count = Math.Min(2, Math.Max(1, PrisonerCount));
                for (int index = 0; index < count; index++)
                {
                    Vector3 spawn = _stationDestination + new Vector3(2.5f + index * 2f, -1.5f, 0f);
                    try { spawn = World.GetNextPositionOnStreet(spawn); } catch { }
                    Ped officer = World.CreatePed(model, spawn);
                    if (officer == null || !officer.Exists())
                        continue;
                    PrepareOfficer(officer);
                    try { officer.Task.LookAt(_transport, 4000); } catch { }
                    _stationHandoffOfficers.Add(officer);
                }
                return _stationHandoffOfficers.Count > 0;
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
                // Convoy owns the prisoner's lifetime and tasks, not the
                // prisoner's damage outcome. Clear stale proofs from an older
                // owner so the prisoner remains visible and can still be
                // killed; the availability guard reports that as custody
                // failure instead of treating the prisoner as lost silently.
                // Preserve collision proof during active custody to prevent
                // unrelated traffic impacts; bullets, fire, explosions, and
                // melee remain lethal and use the existing custody-loss path.
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    prisoner, false, false, false, true, false, false, false, false);
                LogRuntime("POLICE_CONVOY_PRISONER_COLLISION_GUARD",
                    "Prisoner=" + prisoner.Handle
                    + "; CollisionProof=true; BulletFireExplosionMeleeProof=false");
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

        private bool MaintainPrisonerAvailability()
        {
            DateTime now = DateTime.UtcNow;
            bool refreshCustodyFlags = now >= _lastPrisonerMaintenanceAt
                .AddMilliseconds(PrisonerMaintenanceRefreshMilliseconds);
            if (refreshCustodyFlags)
                _lastPrisonerMaintenanceAt = now;
            foreach (Ped prisoner in _prisoners.ToArray())
            {
                if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                    return false;
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
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        prisoner, false, false, false, true, false, false, false, false);
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

        private int LoadingTimeoutSeconds
        {
            get
            {
                // Once the player has taken physical custody, the loading
                // phase is no longer an unattended NPC handoff. The player
                // needs time to complete handcuff, escort, door, and load
                // interactions without the normal NPC timeout cancelling the
                // still-valid operation.
                return _playerEscortPrisoners.Count > 0 || _transportNearCustodyFallback
                    ? Math.Max(PhysicalPhaseTimeoutSeconds, PlayerEscortTimeoutSeconds)
                    : PhysicalPhaseTimeoutSeconds;
            }
        }

        private bool PlayerDrivesTransport()
        {
            Ped player = Game.Player.Character;
            return player != null && player.Exists() && _transport != null && _transport.Exists()
                && player.CurrentVehicle != null && player.CurrentVehicle.Exists()
                && player.CurrentVehicle.Handle == _transport.Handle;
        }

        private bool IsInTransport(Ped prisoner)
        {
            return prisoner != null && prisoner.Exists() && _transport != null && _transport.Exists()
                && prisoner.CurrentVehicle != null && prisoner.CurrentVehicle.Exists()
                && prisoner.CurrentVehicle.Handle == _transport.Handle;
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
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    prisoner, false, false, false, true, false, false, false, false);
                if (ShouldMaintainVisibleCuffPose(prisoner))
                    MaintainVisibleCuffPose(prisoner, DateTime.UtcNow, false);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PRISONER_CUSTODY_STATE_FAILED", ex);
            }
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
            if (!force && _lastPrisonerCuffPoseAt.TryGetValue(prisoner.Handle, out lastTask)
                && now < lastTask.AddMilliseconds(PrisonerCuffPoseRefreshMilliseconds))
                return;

            const string handcuffDictionary = "mp_arresting";
            try
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, handcuffDictionary);
                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, handcuffDictionary))
                {
                    _lastPrisonerCuffPoseAt[prisoner.Handle] = now.AddMilliseconds(500);
                    return;
                }
                Function.Call(Hash.TASK_PLAY_ANIM, prisoner, handcuffDictionary, "idle",
                    3.0f, -2.0f, -1, 49, 0f, false, false, false);
                _lastPrisonerCuffPoseAt[prisoner.Handle] = now;
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

        private void RestoreAllCustodyInvincibility()
        {
            RestoreCustodyInvincibility(_transport);
            RestoreCustodyInvincibility(_transportDriver);
            foreach (Ped prisoner in _prisoners.ToArray())
                RestoreCustodyInvincibility(prisoner);
            foreach (Ped officer in _sceneEscortOfficers.Concat(_stationHandoffOfficers)
                .Concat(_prisonHandoffOfficers).ToArray())
                RestoreCustodyInvincibility(officer);
            _custodyInvincibilityStates.Clear();
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
            _physicalEscortAttachAttempts.Remove(prisonerHandle);
            SetPrisonerLoadingStage(prisoner, null,
                PrisonerLoadingStage.EscortApproach, now,
                "PlayerReleasedToTransportOfficer");
            _phaseStartedAt = now;
            LogRuntime(
                "POLICE_CONVOY_PLAYER_ESCORT_RELEASED",
                "Prisoner=" + prisonerHandle + "; Reason=" + reason
                + "; Fallback=TransportOfficer");
            Notify("~b~POLICE CUSTODY~s~\nPlayer escort released. The transport officer is taking over the handoff.");
            return true;
        }

        private void ReleasePlayerEscortForOfficerFallback(
            Ped prisoner,
            Ped player,
            DateTime now)
        {
            if (prisoner == null || !prisoner.Exists())
                return;

            int prisonerHandle = prisoner.Handle;
            DetachPrisonerFromEscort(prisoner, player,
                "PlayerPhysicalEscortAttachRetriesExhausted");
            _playerEscortPrisoners.Remove(prisonerHandle);
            _playerEscortFallbacks.Add(prisonerHandle);
            _playerHandcuffAnimationUntil.Remove(prisonerHandle);
            _playerLoadingContactStartedAt.Remove(prisonerHandle);
            _playerEscortLostAt.Remove(prisonerHandle);
            _playerDoorInteractions.Remove(prisonerHandle);
            _playerEntryInteractions.Remove(prisonerHandle);
            _prisonerEscortOfficers.Remove(prisonerHandle);
            _prisonerEscortStartedAt.Remove(prisonerHandle);
            _lastPrisonerEscortPosition.Remove(prisonerHandle);
            _lastPrisonerEscortProgressAt.Remove(prisonerHandle);
            _lastPrisonerEscortRecoveryAt.Remove(prisonerHandle);
            _prisonerEscortAlignmentStarted.Remove(prisonerHandle);
            SetPrisonerLoadingStage(prisoner, null,
                PrisonerLoadingStage.EscortApproach, now,
                "PlayerAttachmentFailed;TransportOfficerFallback");

            LogRuntime(
                "POLICE_CONVOY_PLAYER_ESCORT_FALLBACK",
                "Prisoner=" + prisonerHandle
                + "; Player=" + (player == null || !player.Exists()
                    ? "unavailable" : player.Handle.ToString())
                + "; Reason=PhysicalAttachmentRetriesExhausted"
                + "; Fallback=ExistingTransportOfficer");
            Notify("~b~POLICE CUSTODY~s~\nPhysical escort could not be secured. The transport officer is taking over; the prisoner remains cuffed.");
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
            // A player-owned escort is manual by design. Standing still while
            // the officer decides what to do is not a navigation failure, and
            // the prisoner must not be given a follow task as a recovery.
            if (playerEscort)
                return false;

            int prisonerHandle = prisoner.Handle;
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
                || escortMovement >= EscortProgressDistance)
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
            _lastEscortOfficerTaskAt.Remove(escort.Handle);

            try
            {
                // Keep the prisoner task/attachment intact. Only the owning
                // transport officer's stalled route is refreshed; this keeps
                // the transport assignment singular and the cuffed actor out
                // of GTA's independent civilian pathfinding.
                escort.Task.ClearAll();
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_ESCORT_RECOVERY_CLEAR_FAILED", ex);
            }

            MaintainPrisonerCustodyState(prisoner);
            if (!IsPrisonerAttachedToEscort(prisoner, escort)
                && escort.Position.DistanceTo(prisoner.Position) > TransportOfficerContactRadius)
                IssueTransportOfficerApproach(escort, prisoner, now);
            else if (IsPrisonerAttachedToEscort(prisoner, escort))
            {
                Vector3 entry = TransportDoorPosition(seat);
                IssueTransportOfficerVehicleApproach(
                    escort, EscortOfficerDoorTarget(prisoner, escort, entry), now);
            }

            LogRuntime("POLICE_CONVOY_ESCORT_RECOVERY",
                "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                + "; Vehicle=" + (_transport == null ? "none" : _transport.Handle.ToString())
                + "; Phase=" + _phase + "; Action=ReissueOfficerApproach"
                + "; Attached=" + IsPrisonerAttachedToEscort(prisoner, escort));
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

            _prisonerLoadingStages[prisonerHandle] = stage;
            _prisonerLoadingStageStartedAt[prisonerHandle] = now;
            LogRuntime(
                "POLICE_CONVOY_ESCORT_STAGE",
                "Prisoner=" + prisonerHandle
                + "; Previous=" + (hadPrevious ? previous.ToString() : "None")
                + "; Stage=" + stage
                + "; Escort=" + (escort == null || !escort.Exists()
                    ? "none" : escort.Handle.ToString())
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

        private bool TryAttachPrisonerToEscort(Ped prisoner, Ped escort, DateTime now)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || escort == null || !escort.Exists() || escort.IsDead)
                return false;
            if (IsPrisonerAttachedToEscort(prisoner, escort))
                return true;

            int prisonerHandle = prisoner.Handle;
            DateTime lastAttempt;
            if (_lastPhysicalEscortAttachAt.TryGetValue(prisonerHandle, out lastAttempt)
                && now < lastAttempt.AddMilliseconds(PhysicalEscortAttachRetryMilliseconds))
                return false;

            int attempts;
            _physicalEscortAttachAttempts.TryGetValue(prisonerHandle, out attempts);
            if (attempts >= PhysicalEscortMaximumAttachAttempts)
                return false;

            attempts++;
            _physicalEscortAttachAttempts[prisonerHandle] = attempts;
            _lastPhysicalEscortAttachAt[prisonerHandle] = now;
            try
            {
                // Preserve the actors' current world spacing rather than
                // applying a guessed wrist/bone offset. Attaching at the
                // escort's root keeps the cuffed prisoner beside the officer
                // while both share one physical movement owner.
                Vector3 relativeOffset = Function.Call<Vector3>(
                    Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS,
                    escort,
                    prisoner.Position.X,
                    prisoner.Position.Y,
                    prisoner.Position.Z);
                _prisonerEscortRootOffsets[prisonerHandle] = relativeOffset;
                int rootBone = Function.Call<int>(Hash.GET_PED_BONE_INDEX, escort, 0);
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                    prisoner,
                    escort,
                    rootBone,
                    relativeOffset.X,
                    relativeOffset.Y,
                    relativeOffset.Z,
                    0f,
                    0f,
                    0f,
                    false,
                    false,
                    false,
                    true,
                    0,
                    false,
                    false);

                if (!IsPrisonerAttachedToEscort(prisoner, escort))
                {
                    LogRuntime(
                        "POLICE_CONVOY_PHYSICAL_ESCORT_ATTACH_REJECTED",
                        "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                        + "; Attempt=" + attempts + "; Bone=SKEL_ROOT"
                        + "; Offset=" + relativeOffset);
                    return false;
                }

                LogRuntime(
                    "POLICE_CONVOY_PHYSICAL_ESCORT_ATTACHED",
                    "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                    + "; Attempt=" + attempts + "; Bone=SKEL_ROOT"
                    + "; Offset=" + relativeOffset
                    + "; CollisionWithEscort=false");
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PHYSICAL_ESCORT_ATTACH_FAILED", ex);
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
            bool attached = escort != null && escort.Exists()
                && IsPrisonerAttachedToEscort(prisoner, escort);
            if (attached)
            {
                try
                {
                    Function.Call(Hash.DETACH_ENTITY, prisoner, true, true);
                    LogRuntime(
                        "POLICE_CONVOY_PHYSICAL_ESCORT_DETACHED",
                        "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                        + "; Reason=" + (reason ?? string.Empty));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PHYSICAL_ESCORT_DETACH_FAILED", ex);
                }
            }

            _prisonerEscortRootOffsets.Remove(prisonerHandle);
            _lastPhysicalEscortAttachAt.Remove(prisonerHandle);
            _lastPrisonerEscortAnimationAt.Remove(prisonerHandle);
            _prisonerEscortAnimationClips.Remove(prisonerHandle);
            _physicalEscortAttachAttempts.Remove(prisonerHandle);
        }

        private void MaintainPrisonerEscortAnimation(
            Ped prisoner,
            Ped escort,
            DateTime now,
            bool walking = true)
        {
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                || escort == null || !escort.Exists()
                || !IsPrisonerAttachedToEscort(prisoner, escort))
                return;

            const string cuffedMovementDictionary = "anim@move_m@prisoner_cuffed";
            string clip = walking ? "walk" : "idle";
            int prisonerHandle = prisoner.Handle;
            string previousClip;
            bool clipChanged = !_prisonerEscortAnimationClips.TryGetValue(
                prisonerHandle, out previousClip)
                || !string.Equals(previousClip, clip, StringComparison.Ordinal);
            DateTime lastAnimation;
            if (!clipChanged
                && _lastPrisonerEscortAnimationAt.TryGetValue(prisonerHandle, out lastAnimation)
                && now < lastAnimation.AddMilliseconds(PrisonerCuffPoseRefreshMilliseconds))
                return;

            try
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, cuffedMovementDictionary);
                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, cuffedMovementDictionary))
                {
                    _lastPrisonerEscortAnimationAt[prisonerHandle] = now;
                    return;
                }

                if (!clipChanged && Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM,
                    prisoner, cuffedMovementDictionary, clip, 3))
                {
                    _lastPrisonerEscortAnimationAt[prisonerHandle] = now;
                    return;
                }

                int flags = walking ? 1 : 49;
                Function.Call(Hash.TASK_PLAY_ANIM,
                    prisoner,
                    cuffedMovementDictionary,
                    clip,
                    3.0f,
                    -2.0f,
                    -1,
                    flags,
                    1.0f,
                    false,
                    false,
                    false);
                _prisonerEscortAnimationClips[prisonerHandle] = clip;
                _lastPrisonerEscortAnimationAt[prisonerHandle] = now;
                LogRuntime(
                    "POLICE_CONVOY_PRISONER_ESCORT_ANIMATION",
                    "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                    + "; Dictionary=" + cuffedMovementDictionary + "; Clip=" + clip);
            }
            catch (Exception ex)
            {
                _lastPrisonerEscortAnimationAt[prisonerHandle] = now;
                LogException("POLICE_CONVOY_PRISONER_ESCORT_ANIMATION_FAILED", ex);
            }
        }

        private Vector3 EscortOfficerDoorTarget(Ped prisoner, Ped escort, Vector3 doorPosition)
        {
            if (prisoner == null || !prisoner.Exists()
                || escort == null || !escort.Exists())
                return doorPosition;

            Vector3 prisonerOffset;
            if (!_prisonerEscortRootOffsets.TryGetValue(prisoner.Handle, out prisonerOffset))
                return doorPosition;

            try
            {
                Vector3 doorOffset = Function.Call<Vector3>(
                    Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS,
                    escort,
                    doorPosition.X,
                    doorPosition.Y,
                    doorPosition.Z);
                Vector3 officerOffset = new Vector3(
                    doorOffset.X - prisonerOffset.X,
                    doorOffset.Y - prisonerOffset.Y,
                    doorOffset.Z - prisonerOffset.Z);
                return escort.GetOffsetPosition(officerOffset);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_ESCORT_DOOR_TARGET_FAILED", ex);
                return doorPosition;
            }
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
                    + "; Owner=" + (playerEscort ? "Player" : "TransportOfficer")
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
                        > PhysicalEscortAttachRadius)
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

                    try
                    {
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            escort, prisoner, 1000);
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            prisoner, escort, 1000);
                        // GTA owns the visible contact animation here. The
                        // prisoner remains under Convoy custody flags before
                        // and after this task; it is issued once, not refreshed
                        // while the contact animation is running.
                        Function.Call(Hash.TASK_ARREST_PED, escort, prisoner);
                        _prisonerEscortStartedAt[prisonerHandle] = now;
                        _prisonerEscortOfficers[prisonerHandle] = escort;
                        SetPrisonerLoadingStage(prisoner, escort,
                            PrisonerLoadingStage.SecuringContact, now,
                            "TransportOfficerReachedPrisoner");
                        LogRuntime("POLICE_CONVOY_PRISONER_HANDOFF_STARTED",
                            "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                            + "; Vehicle=" + _transport.Handle);
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
                    && now < contactStartedAt.AddMilliseconds(PrisonerEscortSettleMilliseconds))
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
                    > PhysicalEscortAttachRadius)
                {
                    if (!playerEscort)
                        IssueTransportOfficerApproach(escort, prisoner, now);
                    else
                        SetPrisonerLoadingStage(prisoner, escort,
                            PrisonerLoadingStage.EscortApproach, now,
                            "PlayerMustReapproachBeforePhysicalEscort");
                    return true;
                }

                if (!TryAttachPrisonerToEscort(prisoner, escort, now))
                {
                    int attachAttempts;
                    _physicalEscortAttachAttempts.TryGetValue(
                        prisonerHandle, out attachAttempts);
                    if (playerEscort
                        && attachAttempts >= PhysicalEscortMaximumAttachAttempts)
                        ReleasePlayerEscortForOfficerFallback(prisoner, escort, now);
                    return true;
                }

                _prisonerEscortStartedAt[prisonerHandle] = now;
                SetPrisonerLoadingStage(prisoner, escort,
                    PrisonerLoadingStage.Escorting, now,
                    playerEscort ? "PlayerPhysicalEscortAttached" : "OfficerPhysicalEscortAttached");
                _lastPrisonerEscortPosition[prisonerHandle] = prisoner.Position;
                _lastEscortOfficerPosition[escort.Handle] = escort.Position;
                _lastPrisonerEscortProgressAt[prisonerHandle] = now;
                MaintainPrisonerEscortAnimation(prisoner, escort, now);
                LogRuntime("POLICE_CONVOY_ESCORT_MOVEMENT_STARTED",
                    "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                    + "; Owner=" + (playerEscort ? "Player" : "TransportOfficer")
                    + "; Attachment=MeasuredEscortRoot");
                return true;
            }

            if (stage == PrisonerLoadingStage.Escorting
                || stage == PrisonerLoadingStage.VehicleDoorApproach)
            {
                if (!IsPrisonerAttachedToEscort(prisoner, escort))
                {
                    SetPrisonerLoadingStage(prisoner, escort,
                        PrisonerLoadingStage.EscortApproach, now,
                        "PhysicalEscortAttachmentWasInterrupted");
                    _prisonerEscortStartedAt.Remove(prisonerHandle);
                    _prisonerEscortAlignmentStarted.Remove(prisonerHandle);
                    return true;
                }

                Vector3 escortTarget = EscortOfficerDoorTarget(prisoner, escort, entry);
                float escortDoorRadius = playerEscort ? PlayerCustodyVehicleRadius : 2.6f;
                bool playerOwnedEscortAtRearDoor = playerEscort
                    && _playerEscortPrisoners.Contains(prisonerHandle)
                    && IsPrisonerAttachedToEscort(prisoner, escort)
                    && escort.Position.DistanceTo(entry) <= PlayerCustodyVehicleRadius
                    && _transport != null && _transport.Exists()
                    && prisoner.Position.DistanceTo(_transport.Position)
                        <= PlayerCustodyLoadInteractionRadius;
                bool escortAtDoor = playerEscort
                    ? escort.Position.DistanceTo(entry) <= escortDoorRadius
                    : escort.Position.DistanceTo(escortTarget) <= escortDoorRadius;
                // The player's handcuffed prisoner is physically attached at
                // the measured escort offset, so the ped's root may remain
                // several metres behind the rear-door point even while the
                // player has reached it. Treat the attached pair as one
                // controlled unit once the player is beside the door and the
                // prisoner is still within the transport interaction radius.
                bool prisonerAtDoor = prisoner.Position.DistanceTo(entry) <= 2.8f
                    || playerOwnedEscortAtRearDoor;
                if (!escortAtDoor || !prisonerAtDoor)
                {
                    SetPrisonerLoadingStage(prisoner, escort,
                        PrisonerLoadingStage.VehicleDoorApproach, now,
                        "WalkingUnderPhysicalEscortToRearDoor");
                    if (!playerEscort)
                        IssueTransportOfficerVehicleApproach(escort, escortTarget, now);
                    MaintainPrisonerEscortAnimation(prisoner, escort, now);
                    return true;
                }

                SetPrisonerLoadingStage(prisoner, escort,
                    PrisonerLoadingStage.VehicleDoorReached, now,
                    "BothActorsReachedRearPassengerDoor");
                MaintainPrisonerEscortAnimation(prisoner, escort, now, false);
                return true;
            }

            if (!_prisonerVehicleDoorsOpened.Contains(prisonerHandle))
            {
                // A player-owned escort must explicitly open the rear door.
                // The transport officer opens it automatically after the
                // officer and attached prisoner reach the correct side.
                if (playerEscort && !_playerDoorInteractions.Contains(prisonerHandle))
                    return true;
                int doorIndex = PrisonerDoorIndex(seat);
                try
                {
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        escort, prisoner, 800);
                    Function.Call(Hash.TASK_OPEN_VEHICLE_DOOR,
                        escort, _transport, 4000, doorIndex, 1.0f);
                    // Keep the visible door state deterministic even when the
                    // GTA task is interrupted by traffic or another nearby ped.
                    Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                        _transport, doorIndex, false, false);
                    _prisonerVehicleDoorsOpened.Add(prisonerHandle);
                    SetPrisonerLoadingStage(prisoner, escort,
                        PrisonerLoadingStage.DoorOpened, now,
                        playerEscort ? "PlayerOpenedRearDoor" : "OfficerOpenedRearDoor");
                    LogRuntime("POLICE_CONVOY_PRISONER_DOOR_OPENED",
                        "Prisoner=" + prisonerHandle + "; Escort=" + escort.Handle
                        + "; Door=" + doorIndex);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_CONVOY_PRISONER_DOOR_OPEN_FAILED", ex);
                    return false;
                }
                return true;
            }

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

            SetPrisonerLoadingStage(prisoner, escort,
                PrisonerLoadingStage.Loading, now,
                playerEscort ? "PlayerConfirmedPhysicalLoad" : "OfficerReadyToLoadPrisoner");
            DetachPrisonerFromEscort(prisoner, escort,
                "BothActorsAtRearDoor;PhysicalVehicleEntryStarting");

            DateTime entryTaskStartedAt;
            bool entryTaskInProgress = _prisonerEntryTasksIssued.Contains(prisonerHandle)
                && _lastPrisonerVehicleEntryAt.TryGetValue(
                    prisonerHandle, out entryTaskStartedAt)
                && now < entryTaskStartedAt.AddMilliseconds(
                    PrisonerVehicleEntryRefreshMilliseconds);
            if (entryTaskInProgress)
                return true;

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
                        try
                        {
                            Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                officer, prisoner, 1000);
                            Function.Call(Hash.TASK_OPEN_VEHICLE_DOOR,
                                officer, _transport, 4000, doorIndex, 1.0f);
                            Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                                _transport, doorIndex, false, false);
                            _prisonerUnloadingDoorsOpened.Add(prisonerHandle);
                            LogRuntime("POLICE_CONVOY_PRISONER_HANDOFF_DOOR_OPENED",
                                "Stage=" + (handoffStage ?? string.Empty)
                                + "; Prisoner=" + prisonerHandle
                                + "; Officer=" + officer.Handle
                                + "; Door=" + doorIndex);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_CONVOY_PRISONER_HANDOFF_DOOR_FAILED", ex);
                            return false;
                        }
                    }

                    // Only the handoff officer opens the door and starts the
                    // exit. The prisoner never receives a free-standing leave
                    // task before an officer is physically beside the vehicle.
                    CommandLeaveTransport(prisoner);
                    return true;
                }

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
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_TRANSPORT_OFFICER_APPROACH_FAILED", ex);
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
            if (prisoner == null || _transport == null || !_transport.Exists()
                || !_prisonerVehicleDoorsOpened.Contains(prisoner.Handle))
                return;
            try
            {
                Function.Call(Hash.SET_VEHICLE_DOOR_SHUT,
                    _transport, PrisonerDoorIndex(PrisonerSeat(
                        prisoner, ValidPrisoners().ToList())), false);
                _prisonerVehicleDoorsOpened.Remove(prisoner.Handle);
                LogRuntime("POLICE_CONVOY_PRISONER_DOOR_CLOSED",
                    "Prisoner=" + prisoner.Handle + "; Vehicle=" + _transport.Handle);
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_PRISONER_DOOR_CLOSE_FAILED", ex);
            }
        }

        private static int PrisonerDoorIndex(VehicleSeat seat)
        {
            return seat == VehicleSeat.RightRear ? 3 : 2;
        }

        private Vector3 TransportDoorPosition(VehicleSeat seat)
        {
            if (_transport == null || !_transport.Exists())
                return Vector3.Zero;
            float side = seat == VehicleSeat.LeftRear ? -2.2f : 2.2f;
            return _transport.GetOffsetPosition(new Vector3(side, -1.0f, 0f));
        }

        private void CommandWalkToTransport(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists() || _transport == null || !_transport.Exists())
                return;
            Vector3 entry = TransportDoorPosition(PrisonerSeat(
                prisoner, ValidPrisoners().ToList()));
            CommandWalkTo(prisoner, entry);
        }

        private void CommandLeaveTransport(Ped prisoner)
        {
            if (prisoner == null || !prisoner.Exists() || !IsInTransport(prisoner))
                return;
            if (!CanIssuePrisonerTask(prisoner))
                return;
            try { Function.Call(Hash.TASK_LEAVE_VEHICLE, prisoner, _transport, 0); }
            catch (Exception ex) { LogException("POLICE_CONVOY_PRISONER_EXIT_TASK_FAILED", ex); }
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
            // This is a fixed receiving point, not the officer's current
            // position. Using the moving officer position made the first
            // contact at the transport look like a completed station handoff.
            return ResolveRoadPosition(
                _stationDestination + new Vector3(3.5f + index * 1.5f, -2.0f, 0f));
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
                Vector3 roadTarget = ResolveTransportRoadPosition(target, 0);
                SetEmergencySignals(_transport, true);
                _transportDriver.Task.DriveTo(_transport, roadTarget, 22f,
                    EmergencyDrivingFlags, 18f);
                _lastDriverTaskAt = now;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CONVOY_DRIVER_TASK_FAILED", ex);
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

            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _transport, false);
                SetEmergencySignals(_transport, true);
                // A station vehicle point can be adjacent to a blocked gate
                // or a different road level. Each bounded recovery therefore
                // tries one different nearby road target instead of repeating
                // the same impossible coordinate three times.
                Vector3 roadTarget = ResolveTransportRoadPosition(
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
                _lastTransportProgressPosition = _transport.Position;
                _lastTransportProgressAt = now;
                _lastDriverTaskAt = now;
                LogRuntime(
                    "POLICE_CONVOY_TRANSPORT_ROUTE_RECOVERY",
                    "Recovery=" + _transportRouteRecoveryCount + "; Target=" + target
                    + "; RoadTarget=" + roadTarget);
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
            Ped target = ValidPrisoners()
                .OrderBy(prisoner => _transport == null || !_transport.Exists()
                    ? 0f : prisoner.Position.DistanceTo(_transport.Position))
                .FirstOrDefault();
            if (target == null)
                return;
            foreach (Ped officer in _sceneEscortOfficers.Where(ped => ped != null && ped.Exists()))
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

        private Vector3 FindSafeStagingPosition(Ped player, Vector3 target, bool prisonTrip)
        {
            LSPDPoliceStationDefinition station = SelectedStation();
            Vector3 stationSpawn = station == null
                ? Vector3.Zero
                : new Vector3(station.VehicleX, station.VehicleY, station.VehicleZ);
            Vector3 candidate = prisonTrip ? stationSpawn : Vector3.Zero;
            float distance = prisonTrip
                ? Math.Max(55f, _settings.MinimumStagingDistance)
                : Math.Max(45f, _settings.MinimumStagingDistance * 0.60f);
            // Scene transport belongs near the arrest, not back at the selected
            // station. Stage it behind the officer at the configured distance
            // and snap it to a road so a fleeing suspect's actual custody point
            // receives the unit promptly. A prison-transfer unit still prefers
            // the authored station vehicle position.
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

        private LSPDPoliceStationDefinition SelectedStation()
        {
            if (_profile == null)
                return null;
            return _profile.FindStation(_profile.Selection.StationId)
                ?? _profile.FindStation("mission_row")
                ?? _profile.Stations.FirstOrDefault();
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
            foreach (Ped prisoner in _prisoners.Where(ped => ped != null && ped.Exists()))
            {
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
            _failedIsRecoverable = recoverable;
            _failedAfterStationHandoff = recoverable && IsPrisonTransferPhase(_phase);
            _lastFailureReason = reason ?? string.Empty;
            ReportTerminal("lsimmersivelife.police.transport.cancelled", recoverable ? "recoverable-failure" : "failed");
            DetachPrisonersForRecovery();
            CleanupCustodyBlips();
            QueueRouteThreatCleanup();
            if (recoverable)
            {
                // Give an in-vehicle prisoner a real exit opportunity and keep
                // the owned transport around through the normal grace cleanup.
                // Deleting it immediately after TASK_LEAVE_VEHICLE cancels the
                // visible exit and caused the old "missing prisoner" bug.
                QueueCurrentTransportCleanup();
                _transport = null;
                _transportDriver = null;
            }
            else
            {
                // Preserve the failed scene while the player is still in it.
                // The existing deferred queue removes owned Convoy entities
                // only after the player has left the configured cleanup radius.
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

        private void QueueCurrentTransportCleanup()
        {
            DateTime now = DateTime.UtcNow;
            CleanupBlip(_transportBlip);
            _transportBlip = null;
            CleanupBlip(_custodyOfficerBlip);
            _custodyOfficerBlip = null;
            _custodyOfficerBlipHandle = 0;
            // Release Convoy's temporary damage protection before the current
            // transport is handed to deferred cleanup or a recoverable
            // prisoner is returned to Dispatch custody.
            RestoreAllCustodyInvincibility();
            if (_transport != null && _transport.Exists() && !_playerOwnedTransport)
                QueueCleanup(null, _transport, now);
            foreach (Ped officer in _sceneEscortOfficers.Where(ped => ped != null && ped.Exists()).ToArray())
                QueueCleanup(officer, null, now);
            _sceneEscortOfficers.Clear();
            _prisonerEscortOfficers.Clear();
            _prisonerEscortStartedAt.Clear();
            _lastEscortOfficerTaskAt.Clear();
            _prisonerVehicleDoorsOpened.Clear();
            _prisonerEntryTasksIssued.Clear();
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
                else if (now >= item.Expires && playerOccupiesVehicle)
                {
                    // Never delete a vehicle the player is currently driving.
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
                if (item.Ped != null && item.Ped.Exists()
                    && _prisoners.Any(prisoner => prisoner != null
                        && prisoner.Exists()
                        && prisoner.Handle == item.Ped.Handle))
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

        private void DetachPrisonersForRecovery()
        {
            foreach (Ped prisoner in ValidPrisoners())
            {
                try
                {
                    bool wasInTransport = IsInTransport(prisoner);
                    if (wasInTransport)
                        Function.Call(Hash.TASK_LEAVE_VEHICLE, prisoner, _transport, 0);
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        prisoner, false, false, false, false, false, false, false, false);
                    PreparePrisoner(
                        prisoner,
                        !wasInTransport && !_failedAfterStationHandoff);
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

        private void ClearTerminalState()
        {
            CleanupCustodyBlips();
            CleanupWaypoint();
            ReleasePendingModelRequests();
            RestoreAllCustodyInvincibility();
            _state = LSPDDispatchState.None;
            _phase = CustodyPhase.None;
            _transport = null;
            _transportDriver = null;
            _sceneEscortOfficers.Clear();
            _stationHandoffOfficers.Clear();
            _prisonHandoffOfficers.Clear();
            _routeThreatPeds.Clear();
            _routeThreatVehicle = null;
            ResetOperationFields();
        }

        private void ResetOperationFields()
        {
            _pickupTarget = Vector3.Zero;
            _stationDestination = Vector3.Zero;
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
            _transportRouteRecoveryCount = 0;
            _lastPrisonerMaintenanceAt = DateTime.MinValue;
            _driverReleaseRequestedAt = DateTime.MinValue;
            _driverReleaseRequested = false;
            _transportOfficerReleaseRequestedAt = DateTime.MinValue;
            _nextTransportOfficerReleaseTaskAt = DateTime.MinValue;
            _lastActiveCustodyMaintenanceAt = DateTime.MinValue;
            _transportOfficerReleaseRequested = false;
            _transportOfficerReleaseRecoveryLogged = false;
            _transportNearCustodyFallback = false;
            _playerCustodyKeyDown = false;
            _playerOwnedTransport = false;
            _playerCustodyOfferNotified = false;
            _playerUnloadingOfferNotified = false;
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
            _routeThreatState = RouteThreatState.None;
            _routeThreatPreparationDeadline = DateTime.MinValue;
            _lastRouteThreatCombatTaskAt = DateTime.MinValue;
            _lastPrisonerTaskAt.Clear();
            _lastPrisonerVehicleEntryAt.Clear();
            _lastPrisonerCuffPoseAt.Clear();
            _lastPrisonerEscortPosition.Clear();
            _lastEscortOfficerPosition.Clear();
            _lastPrisonerEscortProgressAt.Clear();
            _lastPrisonerEscortRecoveryAt.Clear();
            _prisonerLoadingStages.Clear();
            _prisonerLoadingStageStartedAt.Clear();
            _lastPhysicalEscortAttachAt.Clear();
            _prisonerEscortRootOffsets.Clear();
            _lastPrisonerEscortAnimationAt.Clear();
            _physicalEscortAttachAttempts.Clear();
            _prisonerEscortAnimationClips.Clear();
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
            _prisonerEntryTasksIssued.Clear();
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
            _custodyInvincibilityStates.Clear();
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
            _lastPrisonerTaskAt.Clear();
            _lastPrisonerVehicleEntryAt.Clear();
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
