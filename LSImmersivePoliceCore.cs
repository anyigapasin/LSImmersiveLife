using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LSImmersiveLife
{
    /// <summary>
    /// Coordinates the Police Authority session, profile, patrol shift, and
    /// specialized Dispatch, response, Gang, Crime Activity, NPC, audio, and
    /// custody owners. Each owner keeps its own state machine and entity
    /// ownership; this class supplies ordering and lifecycle gates.
    /// </summary>
    // The original coordination note is retained above. The local NPC owner is
    // now connected below and remains explicitly gated by Authority + Patrol.
    /// <summary>
    /// Keeps the active Police owners in one deterministic runtime order while
    /// leaving Dispatch, Gang, Crime Activity, NPC, and Convoy state in their
    /// specialized classes.
    /// </summary>
    internal sealed class LSImmersivePoliceCore : IDisposable
    {
        // Construction stays passive. Runtime ownership begins only after
        // EnterPoliceAuthority and Process receives an active game tick.
        private readonly LSImmersiveLog _log;
        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveMainConfig _config;
        private readonly LSPDAuthority _authority;
        private readonly LSPDGangDataIntegration _gangData;
        private readonly LSNPCDatabase _npcDatabase;
        private readonly List<Blip> _stationMarkers = new List<Blip>();
        private readonly List<Ped> _npcProtectedActors = new List<Ped>(16);
        private readonly Dictionary<int, DispatchArrestInvincibilityState>
            _dispatchArrestInvincibilityStates =
                new Dictionary<int, DispatchArrestInvincibilityState>();
        private readonly List<Vehicle> _npcProtectedVehicles = new List<Vehicle>(12);
        private readonly List<Ped> _npcTrafficOperationActors = new List<Ped>(12);
        private readonly List<Vehicle> _npcTrafficOperationVehicles = new List<Vehicle>(8);
        private int _personalUtilityBrakeReleasePendingHandle;
        private DateTime _personalUtilityBrakeReleaseNextAttemptAt = DateTime.MinValue;
        private DateTime _nextNpcTrafficOperationRefresh = DateTime.MinValue;
        private PlayerStateSnapshot _normalPlayerState;
        private Vehicle _managedPoliceVehicle;
        private string _lastPreferredPoliceVehiclePlacementFailureSignature = string.Empty;
        private DateTime _preferredPoliceVehiclePlacementRetryAfter = DateTime.MinValue;
        private Vector3 _preferredPoliceVehiclePlacementRetryPlayerPosition;
        private string _preferredPoliceVehiclePlacementRetryModelName = string.Empty;
        private string _preferredPoliceVehiclePlacementRetryStationId = string.Empty;
        private string _preferredPoliceVehiclePlacementRetryCategory = string.Empty;
        private string _preferredPoliceVehiclePlacementRetryMessage = string.Empty;
        private bool _preferredPoliceVehiclePlacementRetryAllowsStationFallback;
        private bool _preferredPoliceVehiclePlacementRetryForceReplaceManagedVehicle;
        private string _lastPreferredPoliceVehicleSpawnSource = string.Empty;
        private Blip _preferredPoliceVehicleBlip;
        private int _preferredPoliceVehicleBlipTargetHandle;
        private Vehicle _fadingPoliceVehicle;
        private DateTime _preferredPoliceVehicleFadeStartedAt = DateTime.MinValue;
        private DateTime _policePedScreenFadeInAt = DateTime.MinValue;
        private bool _ownsPolicePedScreenFade;
        private const int PolicePedScreenFadeOutMilliseconds = 180;
        private const int PolicePedScreenFadeHoldMilliseconds = 80;
        private const int PolicePedScreenFadeInMilliseconds = 350;
        private readonly LSPDGangAdapterResponse _gangResponse;
        private DateTime _nextGangScan = DateTime.MinValue;
        private Ped[] _nearbyGangPeds = new Ped[0];
        private Ped[] _nearbyKnownGangPeds = new Ped[0];
        private bool _gangIncidentWasActive;
        private bool _crimeActivityWasActive;
        private bool _npcInteractionWasActive;
        private bool _patrolKeyDown;
        private bool _resetKeyDown;
        private bool _transportKeyDown;
        private bool _transportCustodyInputSuppression;
        private bool _transportCompleteKeyDown;
        private bool _convoyAcceptKeyDown;
        private bool _convoyRejectKeyDown;
        private bool _interiorAccessEnterKeyDown;
        private string _pendingDispatchTransportIncidentId = string.Empty;
        private string _dispatchGroupFinalHandoffIncidentId = string.Empty;
        private readonly HashSet<int> _dispatchGroupHandoffFailureLogged =
            new HashSet<int>();
        private Ped _pendingDispatchTransportPrisoner;
        private Vehicle _pendingDispatchTransportVehicle;
        private bool _configurationRefreshPending = true;
        private bool _lastAppliedAudioEnabled;
        private int _lastAppliedAudioVolume = -1;
        private PlayerStateSnapshot _pendingNormalPlayerRestore;
        private Model _pendingRestoreModel;
        private DateTime _pendingRestoreDeadline = DateTime.MinValue;
        private bool _pendingWeaponsRestored;
        private bool _stationSetupCompletionPending;
        // The selected Police profile may reference streamed addon assets. A
        // menu callback may request those assets but must not wait for them.
        // Keep the unfinished portions here and apply each one once from the
        // normal Script tick after GTA has loaded it.
        private bool _pendingProfilePedApply;
        private bool _pendingProfileVehicleApply;
        private bool _pendingProfileWeaponsApply;
        private string _pendingProfilePedSelectionModelName = string.Empty;
        private readonly HashSet<string> _profileRestoreAppliedWeaponIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _nextPendingProfileApply = DateTime.MinValue;
        private DateTime _pendingProfileApplyDeadline = DateTime.MinValue;
        private long _radioTest;
        private enum PersonalUtilityDeliveryState
        {
            None,
            PreparingDriver,
            EnRoute,
            AwaitingDriverExit
        }
        private enum PersonalUtilityRecoveryState
        {
            None,
            PreparingCollision,
            Settling,
            FadingIn
        }
        private PersonalUtilityDeliveryState _personalUtilityDeliveryState;
        private PersonalUtilityRecoveryState _personalUtilityRecoveryState;
        private Vehicle _personalUtilityRecoveryVehicle;
        private int _personalUtilityRecoveryVehicleHandle;
        private Vector3 _personalUtilityRecoveryOrigin;
        private float _personalUtilityRecoveryOriginHeading;
        private Vector3 _personalUtilityRecoveryDestination;
        private float _personalUtilityRecoveryDestinationHeading;
        private Vector3 _personalUtilityRecoveryPlayerPosition;
        private Vector3 _personalUtilityRecoveryLastPosition;
        private float _personalUtilityRecoveryLastHeading;
        private float _personalUtilityRecoveryLastGroundZ;
        private DateTime _personalUtilityRecoveryDeadline = DateTime.MinValue;
        private DateTime _personalUtilityRecoveryNextSampleAt = DateTime.MinValue;
        private DateTime _personalUtilityRecoveryRevealDeadline = DateTime.MinValue;
        private int _personalUtilityRecoveryGroundSamples;
        private int _personalUtilityRecoveryStableSamples;
        private bool _personalUtilityRecoveryMoved;
        private string _personalUtilityRecoverySource = string.Empty;
        private Ped _personalUtilityDeliveryOfficer;
        private int _personalUtilityDeliveryVehicleHandle;
        private Vector3 _personalUtilityDeliveryOrigin;
        private Vector3 _personalUtilityDeliveryDestination;
        private float _personalUtilityDeliveryDestinationHeading;
        private Vector3 _personalUtilityDeliveryLastPosition;
        private DateTime _personalUtilityDeliveryNextUpdateAt = DateTime.MinValue;
        private DateTime _personalUtilityDeliveryLastProgressAt = DateTime.MinValue;
        private DateTime _personalUtilityDeliveryStateDeadline = DateTime.MinValue;
        private int _personalUtilityDeliveryRecoveryAttempts;
        private DateTime _personalUtilityFetchRetryAfter = DateTime.MinValue;
        private Vector3 _personalUtilityFetchRetryPlayerPosition;
        private int _personalUtilityFetchRetryVehicleHandle;
        private string _personalUtilityFetchRetryModelName = string.Empty;
        private string _personalUtilityFetchRetryMessage = string.Empty;
        private const int PersonalUtilityDeliveryRetryMilliseconds = 1000;
        private const int PersonalUtilityDeliveryNoProgressMilliseconds = 20000;
        private const int PersonalUtilityDeliveryMaximumRecoveryAttempts = 1;
        private const int PersonalUtilityFetchRetryBackoffMilliseconds = 8000;
        private const float PersonalUtilityFetchRetryPositionTolerance = 25f;
        private const float PersonalUtilityRecoveryMaximumDistance = 48f;
        private const float PersonalUtilityRecoveryGroundProbeHeight = 10f;
        private const int PersonalUtilityRecoveryCollisionTimeoutSeconds = 6;
        private const int PersonalUtilityRecoverySettleTimeoutSeconds = 6;
        private const int PreferredPoliceVehiclePlacementRetryBackoffMilliseconds = 8000;
        private const float PreferredPoliceVehiclePlacementRetryPositionTolerance = 3f;
        private const float PersonalUtilityRecoveryDistance = 45f;
        private const float SelectedStationGarageAccessDistance = 75f;
        private const float SelectedStationParkingMatchDistance = 24f;
        private const int StationVehicleSettleSampleIntervalMilliseconds = 125;
        private const int StationVehicleMaximumSettleSamples = 8;
        private const int StationVehicleRequiredStableSamples = 2;
        private const float StationVehicleSettlePositionTolerance = 0.12f;

        private enum PreferredVehiclePlacementResult
        {
            ConfirmedSafe,
            ConfirmedSafeGroundInconclusive,
            ConfirmedUnsafe,
            Inconclusive
        }

        internal LSImmersivePoliceCore(
            LSImmersiveLog log,
            LSIMMERSIVEPATH paths,
            LSImmersiveMainConfig config,
            LSPDAudioDispatch audio = null)
        {
            _log = log ?? throw new ArgumentNullException("log");
            _paths = paths ?? throw new ArgumentNullException("paths");
            _config = config ?? throw new ArgumentNullException("config");
            _profile = new LSPDProfile(paths, log);
            // Authority behavior flags are owned by the one universal main
            // configuration document. The Police profile remains a personal
            // selection/favourites data document, not a second settings file.
            _authority = new LSPDAuthority(_profile, _config, log);

            // Construction is passive. Audio stays inactive and no model,
            // vehicle, weapon, wanted state, or world relationship is changed.
            Audio = audio ?? new LSPDAudioDispatch(paths, log);
            Audio.SetOptions(config.EnableAudio, config.AudioVolume);
            _lastAppliedAudioEnabled = config.EnableAudio;
            _lastAppliedAudioVolume = config.AudioVolume;
            Audio.SetActive(false);
            Patrol = new LSPDPatrol(Audio, _config.Police.Patrol);
            Dispatch = new LSPDDispatch(
                Audio,
                _paths,
                _log,
                _config.PoliceControls,
                _config.Police.Dispatch,
                _config.Police.Cleanup,
                _config.Ui);
            Dispatch.ConfigureSuspectInjurySettings(_config.Police.SuspectInjury);
            // AmbientWorld is the shared location/scene-preparation owner. It
            // receives the same parsed location catalog as Dispatch; no second
            // XML database or independent coordinate list is created.
            AmbientWorld = new LSWorldAmbientBehavior(
                Dispatch.LocationCatalog,
                _log,
                dispatchSettings: _config.Police.Dispatch);
            Dispatch.AttachAmbientWorld(AmbientWorld);
            Response = new LSPDPoliceResponse(
                Audio,
                _profile,
                _log,
                _config.Police.Response);
            Response.AttachAmbientWorld(AmbientWorld);
            Dispatch.AttachPoliceResponse(Response);
            Backup = new LSPDBackUp(
                Audio,
                _profile,
                _log,
                _config.Police.Backup);
            Backup.AttachAmbientWorld(AmbientWorld);
            Backup.AttachAuthority(_authority);
            Dispatch.AttachBackup(Backup);
            _gangData = new LSPDGangDataIntegration(_paths, _log);
            Dispatch.AttachGangData(_gangData);
            _npcDatabase = new LSNPCDatabase(_paths, _log);
            AmbientWorld.AttachNpcDatabase(_npcDatabase);
            _gangResponse = new LSPDGangAdapterResponse(Audio, _log, _gangData);
            NpcResponse = new LSPDNPCResponse(
                log,
                _gangData,
                _config.PoliceControls,
                _profile,
                _config.Police.NpcResponse,
                _config.Police.Traffic,
                _npcDatabase,
                Audio);
            NpcResponse.ConfigureSuspectInjurySettings(_config.Police.SuspectInjury);
            NpcResponse.SetBackupDamageEvidenceProvider(
                Backup.CaptureNpcDamageEvidence);
            CrimeActivity = new LSPDCrimeActivity(
                Audio,
                paths,
                log,
                _config.Police.CrimeActivity,
                _config.Police.Cleanup,
                Dispatch.CriminalAssets);
            Convoy = new LSPDConvoy(
                Audio,
                _profile,
                _log,
                _config.PoliceControls,
                _config.Police.Convoy,
                _config.Police.Cleanup,
                _config.Ui);
            _config.SettingsChanged += QueueConfigurationRefresh;
            LastOperationMessage = "Police Authority is inactive.";
        }

        internal LSPDAudioDispatch Audio { get; private set; }
        // One Police session shares one radio queue. These are the existing
        // module boundaries, not copies of the abandoned project's gameplay.
        // Owners report confirmed transitions; audio never creates those facts.
        internal LSPDPatrol Patrol { get; private set; }
        internal LSPDDispatch Dispatch { get; private set; }
        internal LSWorldAmbientBehavior AmbientWorld { get; private set; }
        internal LSPDPoliceResponse Response { get; private set; }
        internal LSPDBackUp Backup { get; private set; }
        internal LSPDGangDataIntegration GangData { get { return _gangData; } }
        internal LSPDGangAdapterResponse GangResponse { get { return _gangResponse; } }
        internal LSPDNPCResponse NpcResponse { get; private set; }
        internal LSNPCDatabase NpcDatabase { get { return _npcDatabase; } }
        internal LSPDCrimeActivity CrimeActivity { get; private set; }
        internal LSPDConvoy Convoy { get; private set; }
        internal LSPDProfile Profile { get { return _profile; } }
        internal LSPDAuthority Authority { get { return _authority; } }
        internal LSIMMERSIVEPATH Paths { get { return _paths; } }
        internal LSPDControlBindings PoliceControls { get { return _config.PoliceControls; } }
        internal bool IsPoliceAuthorityActive { get { return _authority.IsActive; } }
        internal string LastOperationMessage { get; private set; }
        internal bool HasPendingPreferredUtilityTransportChoice
        {
            get { return IsPendingPreferredUtilityTransportChoiceCurrent(); }
        }
        internal bool CanRecoverPersonalUtility
        {
            get
            {
                return IsPoliceAuthorityActive
                    && _profile.StationSetupComplete
                    && !Convoy.Active
                    && !CrimeActivity.BlocksOtherPoliceActivities
                    && !_gangResponse.HasActiveIncident
                    && !NpcResponse.HasActiveInteraction
                    && !HasPendingPreferredUtilityTransportChoice
                    && !string.IsNullOrWhiteSpace(_profile.SelectedVehicleModelName);
            }
        }
        internal int NearbyCrimeActivityCount { get { return CrimeActivity.NearbyIntel.Count; } }
        internal string CrimeActivityStatus
        {
            get
            {
                if (!CrimeActivity.IsLoaded) return "Crime intelligence is not loaded.";
                if (CrimeActivity.Active) return CrimeActivity.StatusText;
                if (CrimeActivity.HasAvailableActivity) return CrimeActivity.StatusText;
                if (CrimeActivity.NearbyIntel.Count == 0) return "No nearby intelligence observed.";
                return CrimeActivity.NearbyIntel.Count + " nearby intelligence tip(s) available.";
            }
        }

        internal void SetRoleActive(bool active)
        {
            // Selecting another Roleplay category releases Police Authority
            // through its existing UI/Core boundary. Police selection itself
            // enters through LSAuthorityRole and the existing Open path.
            if (!active)
                ExitPoliceAuthority();
        }

        internal bool EnterPoliceAuthority()
        {
            if (IsPoliceAuthorityActive)
            {
                LastOperationMessage = "Police Authority is already active.";
                return true;
            }

            CaptureNormalPlayerState();
            bool catalogsLoaded = _authority.Enter();
            Audio.SetActive(true);
            _gangData.Reload();
            if (!_npcDatabase.IsLoaded && !_npcDatabase.Reload())
            {
                _log.Debug("NPC_DATABASE_UNAVAILABLE",
                    _npcDatabase.LastLoadError ?? "NPC citizen database could not be loaded.");
            }
            if (!CrimeActivity.IsLoaded)
            {
                if (CrimeActivity.Reload())
                {
                    _log.Runtime("POLICE_CRIME_INTELLIGENCE_CATALOG_LOADED",
                        "Activities=" + CrimeActivity.ActivityCount
                        + "; Locations=" + CrimeActivity.LocationCount
                        + "; Groups=" + CrimeActivity.GroupCount);
                }
                else
                {
                    _log.Debug("POLICE_CRIME_INTELLIGENCE_CATALOG_LOAD_FAILED",
                        CrimeActivity.LastLoadError ?? "Crime Activity catalog load failed without a reported error.");
                }
            }

            _log.Runtime(
                _profile.LastProfileLoaded
                    ? "POLICE_PROFILE_LOADED"
                    : "POLICE_PROFILE_DEFAULTS_LOADED",
                _profile.LastProfileLoaded
                    ? "Saved Police profile and personal weapon collection loaded."
                    : "Police profile defaults loaded because no valid saved profile was available.");

            LSPDPoliceAgencyDefinition agency =
                _profile.FindAgency(_profile.Selection.AgencyId);
            LSPDPoliceStationDefinition station =
                _profile.FindStation(_profile.Selection.StationId);
            _log.Runtime(
                "POLICE_AUTHORITY_ACTIVATED",
                "Agency=" + (agency == null ? "Unselected" : agency.CallSign)
                + "; Station=" + (station == null ? "Unselected" : station.DisplayName)
                + "; SavedProfile=" + _profile.LastProfileLoaded.ToString(CultureInfo.InvariantCulture));

            if (catalogsLoaded)
                ActivateStationMarkers();
            if (!catalogsLoaded)
            {
                LastOperationMessage = "Police Authority activated, but Police XML data could not be loaded.";
                return false;
            }

            bool profileApplied = true;
            if (_profile.StationSetupComplete)
            {
                bool pedApplied;
                bool vehicleApplied;
                bool weaponsApplied;
                profileApplied = ApplySavedProfile(
                    false,
                    out pedApplied,
                    out vehicleApplied,
                    out weaponsApplied);
                if (!profileApplied)
                    QueuePendingSavedProfileApply(pedApplied, vehicleApplied, weaponsApplied);
            }
            else
            {
                ClearPendingSavedProfileApply();
                _stationSetupCompletionPending = false;
            }

            LastOperationMessage = BuildPoliceAuthorityEntryMessage(profileApplied);
            return true;
        }

        internal void ExitPoliceAuthority()
        {
            if (!IsPoliceAuthorityActive)
            {
                RestoreAllDispatchArrestInvincibility("PoliceAuthorityInactive");
                return;
            }

            RestoreAllDispatchArrestInvincibility("PoliceAuthorityDeactivated");
            RestorePolicePedScreenFade();
            CancelPersonalUtilityDelivery("PoliceAuthorityDeactivated");
            CancelPersonalUtilityRecovery("PoliceAuthorityDeactivated");
            AmbientWorld.CancelAllInteriorAccessSessions("PoliceAuthorityDeactivated");
            ClearPendingPreferredUtilityTransportChoice();
            Audio.SetActive(false);
            Patrol.Reset();
            Dispatch.Reset();
            Convoy.Reset();
            Response.Reset();
            Backup.Reset();
            _gangResponse.Reset();
            ResetStationMarkers();
            CrimeActivity.Reset();
            _gangData.Reset();
            NpcResponse.Reset();
            _authority.Exit();
            ReleaseManagedVehicle();
            QueueNormalPlayerRestore();
            LastOperationMessage = "Police Authority deactivated.";
            _patrolKeyDown = false;
            _resetKeyDown = false;
            _transportKeyDown = false;
            _transportCustodyInputSuppression = false;
            _transportCompleteKeyDown = false;
            _convoyAcceptKeyDown = false;
            _convoyRejectKeyDown = false;
            _nearbyGangPeds = new Ped[0];
            _nearbyKnownGangPeds = new Ped[0];
            _gangIncidentWasActive = false;
            _crimeActivityWasActive = false;
            _npcInteractionWasActive = false;
            _nextNpcTrafficOperationRefresh = DateTime.MinValue;
            _npcTrafficOperationActors.Clear();
            _npcTrafficOperationVehicles.Clear();
            ClearPendingSavedProfileApply();
            _stationSetupCompletionPending = false;
            _log.Runtime(
                "POLICE_AUTHORITY_DEACTIVATED",
                "Normal player session restored where possible.");
        }

        internal void Process(bool paused)
        {
            Process(paused, true);
        }

        internal void Process(bool paused, bool allowGameplayInput)
        {
            // Player restoration is queued by Authority exit and completed from
            // the normal Script tick. This avoids Model.Request waiting inside a
            // LemonUI callback, which caused the old Script.Wait crash.
            ProcessPendingNormalPlayerRestore();
            ProcessPolicePedScreenFade();
            ApplyPendingConfiguration();

            Ped player = Game.Player.Character;

            // Roleplay uses the same mapped physical threshold service as Police
            // actors. Keep this Player input path outside Police Authority; only
            // Police-specific duties and hotkeys remain behind that authority gate.
            bool convoyOwnsRearDoorInput =
                Convoy.OwnsPlayerRearDoorInteractionInput(player);
            if (convoyOwnsRearDoorInput)
                AmbientWorld.CancelInteriorAccess(
                    player,
                    "ConvoyRearDoorInteractionOwnsInput");
            bool allowInteriorAccessInput = allowGameplayInput && !paused
                && !Dispatch.HasOffer && !NpcResponse.HasActiveInteraction
                && !convoyOwnsRearDoorInput;
            bool keyboardEnterPressed = false;
            if (allowInteriorAccessInput)
            {
                keyboardEnterPressed = WasPoliceKey(
                    Keys.Enter, ref _interiorAccessEnterKeyDown);
            }
            else
            {
                _interiorAccessEnterKeyDown = Game.IsKeyPressed(Keys.Enter);
            }
            bool interiorEnterPressed = allowInteriorAccessInput
                && keyboardEnterPressed;
            string interiorAccessMessage;
            bool interiorAccessInputConsumed;
            AmbientInteriorAccessStatus interiorAccessStatus =
                AmbientWorld.ProcessPlayerInteriorAccess(
                    player,
                    allowInteriorAccessInput,
                    interiorEnterPressed,
                    out interiorAccessMessage,
                    out interiorAccessInputConsumed);
            if (!string.IsNullOrWhiteSpace(interiorAccessMessage))
            {
                LastOperationMessage = interiorAccessMessage;
                if (interiorAccessStatus != AmbientInteriorAccessStatus.DoorOpening)
                    Notify(interiorAccessMessage);
            }
            bool interiorDoorOwnsInteractInput =
                interiorAccessInputConsumed
                || interiorAccessStatus == AmbientInteriorAccessStatus.Started
                || interiorAccessStatus == AmbientInteriorAccessStatus.DoorOpening
                || interiorAccessStatus == AmbientInteriorAccessStatus.ReadyToCross;
            if (interiorDoorOwnsInteractInput || convoyOwnsRearDoorInput)
            {
                Game.DisableControlThisFrame(GTA.Control.Context);
                Game.DisableControlThisFrame(GTA.Control.Enter);
            }
            if (interiorAccessInputConsumed)
            {
                Game.DisableControlThisFrame(GTA.Control.Attack);
                Game.DisableControlThisFrame(GTA.Control.Attack2);
                Game.DisableControlThisFrame(GTA.Control.Aim);
                Game.DisableControlThisFrame(GTA.Control.FrontendAccept);
                LSPDPlayerControlSuppression.SuppressMeleeThisFrame();
                if (_log != null)
                    _log.Runtime(
                        "POLICE_CORE_INTERIOR_ACCESS_FRAME_OWNED",
                        "Player=" + (player == null ? 0 : player.Handle)
                        + "; InputOwner=AmbientWorld; InputKey=Enter; Consumed=true"
                        + "; DispatchInputSuppressed=true; GTAInteractionAndAttackSuppressed=true");
            }

            if (!IsPoliceAuthorityActive)
            {
                RestoreAllDispatchArrestInvincibility("PoliceAuthorityInactive");
                SynchronizePoliceKeyStates();
                return;
            }

            ProcessPreferredPoliceVehicleFade(paused);
            if (_personalUtilityDeliveryState != PersonalUtilityDeliveryState.None)
                CancelPersonalUtilityDelivery("ManagedUtilityRecoveryOwnsFetch");
            ProcessPersonalUtilityRecovery(paused);
            ProcessPersonalUtilityParkingBrakeRelease(paused);

            _authority.Process(
                paused,
                Dispatch.Current,
                Patrol.IsPatrolling,
                _nearbyKnownGangPeds);
            ProcessPendingSavedProfileApply(paused);
            bool profileAssetsPending = HasPendingSavedProfileAssets;
            if (allowGameplayInput && !paused && !interiorDoorOwnsInteractInput)
                ProcessPoliceHotkeys();

            // Authority owns player-scoped vanilla protection. PoliceResponse
            // owns this separate local officer recognition/de-escalation pass;
            // Core provides the active Authority context and the current
            // Dispatch occurrence so PoliceResponse can protect wounded,
            // compliant, or secured suspects while preserving active armed
            // threats. This remains active outside Dispatch for patrol support.
            if (!paused)
                Response.MaintainAuthoritySupport(
                    player,
                    _authority.Settings,
                    Convoy.ProtectedActors.Concat(Backup.ProtectedActors),
                    Dispatch.Current);

            if (Patrol.IsPatrolling && !paused)
                UpdateGangSample(player);

            // Gang activity is its own local layer. It gets first refusal over a
            // new dispatch call, while an already active Dispatch remains owned by
            // Dispatch until it reaches a terminal state.
            // A paused game must not start or resolve a gang incident from the
            // last cached sample. Keep an existing incident visible, but leave
            // its world tasks untouched until the game resumes.
            if (!paused)
                _gangResponse.Process(
                    Patrol.IsPatrolling,
                    Dispatch.HasIncident || Convoy.Active
                        || CrimeActivity.BlocksOtherPoliceActivities
                    || profileAssetsPending,
                    NpcResponse.HasActiveInteraction,
                    player,
                    _nearbyGangPeds,
                    Backup.Active && Backup.State != LSPDBackupAssignmentState.StandingDown);
            bool gangActive = _gangResponse.HasActiveIncident;
            if (_gangIncidentWasActive && !gangActive)
                Dispatch.DeferOffersForContextActivity("gang incident");
            _gangIncidentWasActive = gangActive;

            // Crime Activity is a quiet player-led owner. It can prepare an
            // intelligence lead only during a genuinely quiet patrol, while an
            // already active Crime scene continues to own its actors until it
            // resolves. It never produces a Dispatch offer.
            if (!paused)
            {
                bool mayDiscoverCrime = Patrol.IsPatrolling
                    && !profileAssetsPending
                    && !Dispatch.HasIncident
                    && !Convoy.Active
                    && !gangActive
                    && !NpcResponse.HasActiveInteraction
                    && !Backup.BlocksPlayerActivities;
                CrimeActivity.Process(
                    player,
                    Patrol.IsPatrolling,
                    mayDiscoverCrime,
                    _gangData,
                    false);
            }
            bool crimeActivityActive = CrimeActivity.BlocksOtherPoliceActivities;
            if (_crimeActivityWasActive && !crimeActivityActive)
                Dispatch.DeferOffersForContextActivity("Crime Activity");
            _crimeActivityWasActive = crimeActivityActive;

            // Establish and maintain a civilian/traffic contact before the
            // Dispatch producer evaluates a new offer. This closes the former
            // same-tick race where a G press could lose to a freshly-created
            // callout. Dispatch actor handles are supplied only as a protection
            // boundary; NPCResponse never owns or mutates those actors.
            // IMPORTANT: _nearbyGangPeds is the 75 m observation sample used
            // by GangAdapterResponse to *find* a real attacker. It contains all
            // nearby peds, not only gang actors. Passing that whole sample into
            // NPCResponse used to mark every civilian/driver around the player
            // as a protected scene actor, so the UI could see a person directly
            // in front of Anyi yet still report "No safe civilian...". Protect
            // only actors that a specialized owner actually owns.
            _npcProtectedActors.Clear();
            _npcProtectedActors.AddRange(CrimeActivity.ActiveCriminals);
            Ped activeGangThreat = _gangResponse.ActiveThreat;
            if (activeGangThreat != null && activeGangThreat.Exists())
                _npcProtectedActors.Add(activeGangThreat);
            _npcProtectedActors.AddRange(_gangResponse.ActiveThreats);
            _npcProtectedActors.AddRange(Convoy.ProtectedActors);
            _npcProtectedActors.AddRange(Backup.ProtectedActors);
            _npcProtectedActors.AddRange(Response.ProtectedActors);
            NpcResponse.SetProtectedSceneActors(Dispatch.Current, _npcProtectedActors);

            _npcProtectedVehicles.Clear();
            _npcProtectedVehicles.AddRange(Dispatch.CurrentSuspectVehicles);
            LSPDDispatchEvent currentDispatch = Dispatch.Current;
            if (currentDispatch != null)
            {
                if (currentDispatch.SuspectVehicle != null)
                    _npcProtectedVehicles.Add(currentDispatch.SuspectVehicle);
                if (currentDispatch.VictimVehicle != null)
                    _npcProtectedVehicles.Add(currentDispatch.VictimVehicle);
                if (currentDispatch.ParkedPlayerUtility != null)
                    _npcProtectedVehicles.Add(currentDispatch.ParkedPlayerUtility);
            }
            _npcProtectedVehicles.AddRange(Convoy.ProtectedVehicles);
            _npcProtectedVehicles.AddRange(Backup.ProtectedVehicles);
            _npcProtectedVehicles.AddRange(Response.ProtectedVehicles);
            if (_managedPoliceVehicle != null && _managedPoliceVehicle.Exists())
                _npcProtectedVehicles.Add(_managedPoliceVehicle);
            NpcResponse.SetProtectedSceneVehicles(_npcProtectedVehicles);
            bool dispatchBlocksNpcContact = Dispatch.HasIncident || gangActive
                || Convoy.Active || crimeActivityActive;
            // NPCResponse's traffic/crowd reactions are deliberately local to
            // the physical scene.  The old global activity flag made every
            // ordinary vehicle around the player behave as scene traffic while
            // a remote Dispatch, Convoy, or transport was active.  That was
            // the source of repeated departure tasks, slow traffic, and noisy
            // runtime output during otherwise unrelated travel.
            bool activeRoadScene = IsPlayerAtActiveRoadScene(
                player, gangActive, crimeActivityActive);
            DateTime now = DateTime.UtcNow;
            if (now >= _nextNpcTrafficOperationRefresh)
            {
                _nextNpcTrafficOperationRefresh = now.AddMilliseconds(500);
                _npcTrafficOperationActors.Clear();
                _npcTrafficOperationActors.AddRange(Convoy.ProtectedActors);
                _npcTrafficOperationActors.AddRange(Backup.ProtectedActors);
                _npcTrafficOperationActors.AddRange(Response.ProtectedActors);
                _npcTrafficOperationVehicles.Clear();
                // Prioritize the player's parked Utility so traffic sampling
                // remains centered on it even when the operation has several
                // assigned actors.
                if (currentDispatch != null && currentDispatch.ParkedPlayerUtility != null)
                    _npcTrafficOperationVehicles.Add(currentDispatch.ParkedPlayerUtility);
                _npcTrafficOperationVehicles.AddRange(Convoy.ProtectedVehicles);
                _npcTrafficOperationVehicles.AddRange(Backup.ProtectedVehicles);
                _npcTrafficOperationVehicles.AddRange(Response.ProtectedVehicles);
                if (_managedPoliceVehicle != null && _managedPoliceVehicle.Exists())
                    _npcTrafficOperationVehicles.Add(_managedPoliceVehicle);
                if (Dispatch.HasIncident && currentDispatch != null)
                {
                    if (currentDispatch.Suspect != null)
                        _npcTrafficOperationActors.Add(currentDispatch.Suspect);
                    if (currentDispatch.Suspects != null)
                        _npcTrafficOperationActors.AddRange(currentDispatch.Suspects);
                    if (currentDispatch.Victim != null)
                        _npcTrafficOperationActors.Add(currentDispatch.Victim);
                    if (currentDispatch.AdditionalParticipants != null)
                        _npcTrafficOperationActors.AddRange(currentDispatch.AdditionalParticipants);
                    _npcTrafficOperationVehicles.AddRange(Dispatch.CurrentSuspectVehicles);
                    if (currentDispatch.SuspectVehicle != null)
                        _npcTrafficOperationVehicles.Add(currentDispatch.SuspectVehicle);
                    if (currentDispatch.VictimVehicle != null)
                        _npcTrafficOperationVehicles.Add(currentDispatch.VictimVehicle);
                }
                NpcResponse.SetTrafficOperationEntities(
                    _npcTrafficOperationActors,
                    _npcTrafficOperationVehicles);
            }
            NpcResponse.Process(
                Patrol.IsPatrolling,
                dispatchBlocksNpcContact,
                activeRoadScene,
                paused,
                allowGameplayInput,
                _authority.IsActive && _authority.Settings.EnableCivilianReactionControl);

            bool npcInteractionActive = NpcResponse.HasActiveInteraction;
            if (_npcInteractionWasActive && !npcInteractionActive)
                Dispatch.DeferOffersForContextActivity("NPC interaction");
            _npcInteractionWasActive = npcInteractionActive;

            // A visible Police menu suppresses ordinary gameplay hotkeys, but
            // an already-delivered Dispatch offer still owns its dedicated
            // Y/N or Circle/X decision controls. Without this narrow exception
            // a player-requested offer could be visible in the menu while its
            // documented response keys were silently ignored.
            bool allowDispatchDecisionInput = allowGameplayInput || Dispatch.HasOffer;
            Dispatch.SetPlayerPatrolUtility(_managedPoliceVehicle);
            Dispatch.Process(
                Patrol.IsPatrolling,
                profileAssetsPending || npcInteractionActive || gangActive || Convoy.Active || crimeActivityActive,
                paused,
                allowDispatchDecisionInput,
                gangActive || Convoy.Active || crimeActivityActive,
                interiorDoorOwnsInteractInput);
            MaintainDispatchArrestInvincibility();

            if (!paused)
            {
                string pendingTransportMessage = ProcessPendingPreferredUtilityTransportChoice();
                if (!string.IsNullOrWhiteSpace(pendingTransportMessage))
                {
                    LastOperationMessage = pendingTransportMessage;
                    Notify(pendingTransportMessage);
                }
            }

            // Backup is a dedicated player-requested support owner. It follows
            // the same Dispatch case without sharing Response's restrained
            // one-unit ambient support lifecycle.
            Backup.Process(
                Dispatch,
                Convoy,
                CrimeActivity,
                NpcResponse,
                _gangResponse,
                player,
                paused);
            // Backup can begin a paired Dispatch arrest during its own Process
            // pass. Apply Core's physical-arrest safety in this same game tick.
            MaintainDispatchArrestInvincibility();

            // Do not issue response-unit tasks while GTA is paused. Keeping
            // the unit alive across the pause avoids a release/recreate cycle
            // and preserves the one-unit cap.
            if (!paused && _config.Police.Response.Enabled)
            {
                if (Dispatch.HasIncident && !Convoy.Active && !Backup.BlocksPlayerActivities)
                {
                    LSPDDispatchEvent incident = Dispatch.Current;
                    bool responseSceneActive = incident != null
                        && (incident.State == LSPDDispatchState.Accepted
                            || incident.State == LSPDDispatchState.EnRoute
                            || incident.State == LSPDDispatchState.OnScene
                            || incident.State == LSPDDispatchState.Investigating
                            || incident.State == LSPDDispatchState.SuspectFleeing
                            || incident.State == LSPDDispatchState.SuspectCompliant
                            || incident.State == LSPDDispatchState.SuspectResisting);
                    if (responseSceneActive)
                    {
                        Vector3 responseDestination;
                        string responseMode;
                        string routeFailure;
                        if (AmbientWorld.TryResolvePoliceResponseRoute(
                            incident,
                            out responseDestination,
                            out responseMode,
                            out routeFailure))
                        {
                            bool activePursuit = incident.State == LSPDDispatchState.SuspectFleeing
                                || incident.State == LSPDDispatchState.SuspectResisting;
                            bool allowAutomaticPursuit = !activePursuit
                                || _config.Police.Response.AutomaticPursuitSupport;
                            bool urgentResponse = incident.Armed
                                || incident.Severity >= 4
                                || activePursuit;
                            if (allowAutomaticPursuit)
                            {
                                if (Response.ActiveUnitCount == 0)
                                {
                                    Response.EnsureResponseUnit(
                                        responseDestination,
                                        incident.Id,
                                        incident.IncidentType,
                                        responseMode,
                                        urgentResponse);
                                }
                                Response.Update(
                                    responseDestination,
                                    incident.Id,
                                    incident.IncidentType,
                                    responseMode,
                                    urgentResponse,
                                    incident,
                                    player);
                            }
                            else
                            {
                                Response.ReleaseAll();
                            }
                        }
                        else
                        {
                            Response.ReleaseAll();
                            _log.Debug(
                                "POLICE_RESPONSE_ROUTE_UNAVAILABLE",
                                "Incident=" + incident.Id
                                + "; Type=" + incident.IncidentType
                                + "; Reason=" + routeFailure);
                        }
                    }
                    else
                    {
                        Response.ReleaseAll();
                    }
                }
                else if (gangActive && !Convoy.Active && !crimeActivityActive)
                {
                    // Gang Threat support is a player-requested LSPDBackUp
                    // assignment. Do not keep the old one-car ambient
                    // response alive beside it; that unit only drove to the
                    // scene and never received the hostile gang target list.
                    Response.ReleaseAll();
                }
                else
                {
                    Response.ReleaseAll();
                }
            }
            else if (!paused)
            {
                Response.ReleaseAll();
            }

            PrepareScatteredDispatchBackupCustodyEscorts();
            string convoyMessage = Convoy.Process(
                DateTime.UtcNow,
                allowGameplayInput && !paused);
            MaintainDispatchArrestInvincibility();
            if (!string.IsNullOrWhiteSpace(convoyMessage))
                LastOperationMessage = convoyMessage;

            ProcessScatteredDispatchCustodyHandoffs();

            Ped releasedDispatchSuspect = Convoy.ConsumeDispatchReleasedSuspect();
            if (releasedDispatchSuspect != null)
                LastOperationMessage = Dispatch.CompletePhysicalSuspectRelease(
                    releasedDispatchSuspect);

            if (Convoy.ConsumeDispatchCrewCustodyHandoffReady()
                && Dispatch.HasConvoyCustodyHandoff)
            {
                LastOperationMessage = Dispatch.CompleteTransportCrewHandoff();
                Response.ReleaseAll();
                Backup.BeginStandDown(
                    "Player declined the station drive; Convoy crew is departing with loaded Dispatch custody.");
            }

            // Convoy owns the physical custody trip. Mirror only its terminal
            // transport states into Dispatch so the two owners remain separate
            // while the player drives the prisoner to the chosen destination.
            if (Dispatch.HasConvoyCustodyHandoff
                && Convoy.State == LSPDDispatchState.HoldingAtStation
                && Dispatch.State == LSPDDispatchState.AwaitingTransport)
                Dispatch.SetConvoyState(LSPDDispatchState.HoldingAtStation);
            else if (Dispatch.HasConvoyCustodyHandoff
                && Convoy.State == LSPDDispatchState.PrisonTransfer
                && Dispatch.State == LSPDDispatchState.HoldingAtStation)
                Dispatch.SetConvoyState(LSPDDispatchState.PrisonTransfer);

            string prisonTransferDecision = ProcessStationPrisonTransferInput(
                allowGameplayInput,
                paused);
            if (!string.IsNullOrWhiteSpace(prisonTransferDecision))
                LastOperationMessage = prisonTransferDecision;

            ReconcileConvoyTerminalState();
            if (allowGameplayInput && !paused
                && Game.IsControlJustPressed(GTA.Control.VehicleHandbrake))
            {
                string transportMessage = Convoy.HoldingAtStation
                    ? RequestPrisonerTransport()
                    : Convoy.State == LSPDDispatchState.PrisonTransfer
                        ? CompletePrisonerTransport()
                        : string.Empty;
                if (!string.IsNullOrWhiteSpace(transportMessage))
                    LastOperationMessage = transportMessage;
            }

            ProcessInteriorDispatchWeaponAccess(paused);
            Audio.Process(paused);
        }

        /// <summary>
        /// Coordinates the active Dispatch scene with AmbientWorld's strict
        /// live-interior identity check, then gives LSPDAuthority the final
        /// Player-scoped weapon-control decision. No weapon input is owned here.
        /// </summary>
        private void ProcessInteriorDispatchWeaponAccess(bool paused)
        {
            LSPDDispatchEvent incident = Dispatch.Current;
            AmbientDispatchSceneResolution scene = incident == null
                ? null : incident.SceneResolution;
            Ped playerPed = Game.Player == null ? null : Game.Player.Character;

            bool activeAcceptedIncident = incident != null
                && Dispatch.HasIncident
                && IsInteriorDispatchWeaponAccessState(incident.State);
            bool validatedInteriorScene = scene != null
                && scene.IsInteriorScene
                && (scene.IsMappedInteriorDispatchScene
                    || scene.IsInsideOnlyInteriorScene)
                && scene.LiveInteriorId != 0
                && !scene.IsPendingSceneResolution
                && !string.IsNullOrWhiteSpace(scene.ValidationStatus)
                && scene.ValidationStatus.StartsWith(
                    "RuntimeValidatedInside", StringComparison.Ordinal);
            bool terminalCustodyOwner = Convoy.Active
                || Dispatch.HasConvoyCustodyHandoff;
            bool playerAlive = playerPed != null
                && playerPed.Exists()
                && !playerPed.IsDead;
            bool shouldCheckPlayerInterior = IsPoliceAuthorityActive
                && !paused
                && activeAcceptedIncident
                && validatedInteriorScene
                && !terminalCustodyOwner
                && playerAlive;

            int playerLiveInteriorId = 0;
            bool playerInteriorIdentityAvailable = false;
            if (shouldCheckPlayerInterior && AmbientWorld != null)
            {
                playerInteriorIdentityAvailable =
                    AmbientWorld.TryGetDispatchPlayerInteriorId(
                        playerPed, out playerLiveInteriorId);
            }

            bool exactInteriorMatch = playerInteriorIdentityAvailable
                && scene != null
                && playerLiveInteriorId == scene.LiveInteriorId;
            bool authorized = shouldCheckPlayerInterior && exactInteriorMatch;
            string decision = authorized
                ? "ValidatedAcceptedInteriorDispatchMatch"
                : !IsPoliceAuthorityActive
                    ? "PoliceAuthorityInactive"
                    : paused
                        ? "GameplayPaused"
                        : !activeAcceptedIncident
                            ? "NoAcceptedActiveDispatch"
                            : !validatedInteriorScene
                                ? "DispatchSceneNotValidatedInterior"
                                : terminalCustodyOwner
                                    ? "CustodyOwnedByTerminalTransport"
                                    : !playerAlive
                                        ? "PlayerUnavailableOrDead"
                                        : !playerInteriorIdentityAvailable
                                            ? "PlayerInteriorIdentityUnavailable"
                                            : "PlayerNotInsideExactDispatchInterior";

            _authority.ProcessInteriorDispatchWeaponAccess(
                authorized,
                playerPed,
                paused,
                incident == null ? string.Empty : incident.Id,
                incident == null ? LSPDDispatchState.None : incident.State,
                scene == null ? string.Empty : scene.LocationId,
                scene == null ? 0 : scene.LiveInteriorId,
                playerLiveInteriorId,
                playerInteriorIdentityAvailable,
                decision);
        }

        private static bool IsInteriorDispatchWeaponAccessState(
            LSPDDispatchState state)
        {
            return state == LSPDDispatchState.Accepted
                || state == LSPDDispatchState.EnRoute
                || state == LSPDDispatchState.OnScene
                || state == LSPDDispatchState.Investigating
                || state == LSPDDispatchState.SuspectFleeing
                || state == LSPDDispatchState.SuspectCompliant
                || state == LSPDDispatchState.SuspectResisting
                || state == LSPDDispatchState.Arrested
                || state == LSPDDispatchState.AwaitingTransport;
        }

        /// <summary>
        /// Convoy terminal states belong to one custody handoff only. The old
        /// condition cancelled whichever Dispatch happened to be active after a
        /// past transport failure, which is why fresh offers immediately died.
        /// Reconcile and clear each terminal state once. Scene-transport
        /// failures return valid custody to Dispatch; a prison-transfer
        /// failure keeps already-received station custody in Convoy so a
        /// retry cannot create another scene-transport leg.
        /// </summary>
        private void ReconcileConvoyTerminalState()
        {
            if (Convoy.CompletedAfterDispatchRelease)
            {
                Convoy.ConsumeCompletedState();
                Response.ReleaseAll();
                Backup.BeginStandDown("Dispatch custody ended after the suspect was released.");
                return;
            }

            if (Convoy.Failed)
            {
                if (Convoy.IsRequestedConvoyActivity)
                {
                    string requestReason = Convoy.LastFailureReason;
                    Backup.BeginStandDown("Requested Convoy activity ended: " + requestReason);
                    Convoy.Reset();
                    Response.ReleaseAll();
                    Dispatch.DeferOffersForContextActivity("Convoy Request");
                    LastOperationMessage = "Convoy Request closed: " + requestReason;
                    _log.Runtime("POLICE_CONVOY_REQUEST_CLOSED", "Reason=" + requestReason);
                    return;
                }

                bool partialScatteredGroupHandoff =
                    Convoy.DispatchGroupHasTransferredCustody;
                if (!Dispatch.HasConvoyCustodyHandoff
                    && !partialScatteredGroupHandoff
                    && Convoy.DispatchClosedAfterCrewCustodyHandoff)
                {
                    string stagingFailureReason = Convoy.LastFailureReason;
                    Convoy.ConsumeFailedCrewBackgroundState();
                    LastOperationMessage = "Police transport could not be staged after the Crew custody handoff."
                        + (string.IsNullOrWhiteSpace(stagingFailureReason)
                            ? string.Empty
                            : " Reason: " + stagingFailureReason);
                    _log.Runtime("POLICE_CONVOY_STALE_FAILURE_CLEARED",
                        "A completed or failed Crew Convoy state had no linked Dispatch custody handoff."
                        + "; CrewBackgroundStateConsumed=true"
                        + "; Reason=" + (stagingFailureReason ?? string.Empty));
                    return;
                }

                string reason = Convoy.LastFailureReason;
                // A prison-transfer route failure occurs after the prisoner
                // has already been physically received at the station. Keep
                // that custody phase in Convoy so a retry starts a new prison
                // leg instead of incorrectly creating another scene-transport
                // van back to the station.
                if (Convoy.FailedAfterStationHandoff
                    && Convoy.RestoreFailedPrisonTransferForRetry())
                {
                    Dispatch.SetConvoyState(LSPDDispatchState.HoldingAtStation);
                    LastOperationMessage =
                        "Prison transfer was interrupted. The prisoner remains secured at the station; approve prison transfer again when ready.";
                    Response.ReleaseAll();
                    Backup.BeginStandDown("Prison transfer interrupted; station custody retained.");
                    _log.Runtime(
                        "POLICE_CUSTODY_STATION_RETRY_READY",
                        "Living station custody retained after prison-transfer failure; Reason="
                        + (reason ?? string.Empty));
                    return;
                }

                IList<LSPDSuspectInjuryState> injuryStates;
                IList<Ped> prisoners = Convoy.ConsumeFailedPrisoners(out injuryStates);
                if (Dispatch.RecoverCustodyFromConvoy(prisoners, reason, injuryStates))
                {
                    Convoy.CompleteDispatchCustodyRecovery(prisoners, true);
                    _log.Runtime("POLICE_CONVOY_DISPATCH_CUSTODY_RECOVERED",
                        "Reason=" + (reason ?? string.Empty)
                        + "; LivingPrisoners=" + prisoners.Count
                        + "; DispatchReclaimed=true"
                        + "; DispatchCustodyRetained=true"
                        + "; BackupGuardRetained=" + partialScatteredGroupHandoff);
                    LastOperationMessage = partialScatteredGroupHandoff
                        ? "Transport was interrupted. The secured group remains in custody; Backup continues guarding remote suspects. Request transport again when ready."
                        : prisoners.Count > 1
                            ? "Transport was interrupted. The secured suspects remain in Dispatch custody and are available for another transport request."
                            : "Transport was interrupted. The prisoner remains in Dispatch custody and is available for another transport request.";
                    Response.ReleaseAll();
                    if (!partialScatteredGroupHandoff)
                        Backup.BeginStandDown("Convoy transport interrupted; scene support released.");
                    else
                        _log.Runtime("POLICE_CONVOY_SCATTERED_GROUP_RECOVERED",
                            "Reason=" + reason
                            + "; LivingPrisoners=" + prisoners.Count
                            + "; DispatchReclaimed=true; BackupGuardRetained=true");
                    return;
                }

                LastOperationMessage = Dispatch.Cancel(
                    "Prisoner custody failed and could not be recovered ("
                    + (reason ?? "unknown custody failure") + ").");
                Convoy.CompleteDispatchCustodyRecovery(prisoners, false);
                _log.Runtime(
                    "POLICE_CUSTODY_TERMINAL_FAILURE",
                    "Dispatch custody ended after Convoy failure; Reason="
                    + (reason ?? "unknown custody failure"));
                Response.ReleaseAll();
                Backup.BeginStandDown("Convoy failure closed the linked Dispatch case.");
                return;
            }

            if (!Convoy.Completed)
                return;

            if (Convoy.IsRequestedConvoyActivity)
            {
                Convoy.ConsumeCompletedState();
                Response.ReleaseAll();
                Backup.BeginStandDown("Requested Convoy activity completed.");
                Dispatch.DeferOffersForContextActivity("Convoy Request");
                LastOperationMessage = "Convoy Request completed. Return to patrol.";
                _log.Runtime("POLICE_CONVOY_REQUEST_COMPLETED", "Player-requested Convoy completed.");
                return;
            }

            if (Dispatch.HasConvoyCustodyHandoff)
                LastOperationMessage = Dispatch.CompleteTransport();
            else
                _log.Runtime("POLICE_CONVOY_STALE_COMPLETION_CLEARED",
                    "A completed Convoy state had no linked Dispatch custody handoff.");
            Convoy.ConsumeCompletedState();
            Response.ReleaseAll();
            Backup.BeginStandDown("Custody operation completed.");
        }

        private static bool IsActiveRoadScene(LSPDDispatchState state)
        {
            switch (state)
            {
                case LSPDDispatchState.OnScene:
                case LSPDDispatchState.Investigating:
                case LSPDDispatchState.SuspectFleeing:
                case LSPDDispatchState.SuspectCompliant:
                case LSPDDispatchState.SuspectResisting:
                case LSPDDispatchState.Arrested:
                case LSPDDispatchState.AwaitingTransport:
                    return true;
                default:
                    return false;
            }
        }

        private bool IsPlayerAtActiveRoadScene(
            Ped player,
            bool gangActive,
            bool crimeActivityActive)
        {
            if (player == null || !player.Exists())
                return false;

            const float sceneTrafficRadius = 35.0f;
            try
            {
                if (gangActive)
                {
                    Ped threat = _gangResponse.ActiveThreat;
                    if (threat != null && threat.Exists()
                        && threat.Position.DistanceTo(player.Position) <= sceneTrafficRadius)
                        return true;
                }

                if (crimeActivityActive
                    && CrimeActivity.ActiveCriminals.Any(ped =>
                        ped != null && ped.Exists()
                        && ped.Position.DistanceTo(player.Position) <= sceneTrafficRadius))
                    return true;

                // A Convoy is a transport owner, not a city-wide traffic
                // scene.  Only a physically nearby route threat can qualify
                // for the local scene reaction while the player handles it.
                if (Convoy.Active
                    && Convoy.ActiveRouteThreats.Any(ped =>
                        ped != null && ped.Exists()
                        && ped.Position.DistanceTo(player.Position) <= sceneTrafficRadius))
                    return true;

                // Convoy transport is a local physical scene even when there
                // is no Dispatch incident left at the current Core state.
                // This keeps the existing traffic response around the actual
                // transport/prisoner without turning the whole patrol into a
                // city-wide traffic slowdown.
                if (Convoy.Active
                    && Convoy.SupportPosition != Vector3.Zero
                    && Convoy.SupportPosition.DistanceTo(player.Position) <= sceneTrafficRadius)
                    return true;

                if (!IsActiveRoadScene(Dispatch.State) || Dispatch.Current == null)
                    return false;

                if (Dispatch.Current.Origin.DistanceTo(player.Position) <= sceneTrafficRadius)
                    return true;

                return Dispatch.CurrentSuspects.Any(ped =>
                    ped != null && ped.Exists()
                    && ped.Position.DistanceTo(player.Position) <= sceneTrafficRadius);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_ACTIVE_SCENE_SCOPE_CHECK_FAILED", ex);
                return false;
            }
        }

        private void UpdateGangSample(Ped player)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextGangScan)
                return;
            _nextGangScan = _config.Performance.ThrottleHeavyScans
                ? now.AddMilliseconds(_config.Performance.HeavyScanIntervalMilliseconds)
                : now;
            if (player == null || !player.Exists())
            {
                _nearbyGangPeds = new Ped[0];
                _nearbyKnownGangPeds = new Ped[0];
                return;
            }
            try
            {
                _nearbyGangPeds = World.GetNearbyPeds(player, 75f)
                    ?? new Ped[0];
                _nearbyKnownGangPeds = _nearbyGangPeds
                    .Where(_gangData.IsKnownGangMember)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _nearbyGangPeds = new Ped[0];
                _nearbyKnownGangPeds = new Ped[0];
                _log.Exception("POLICE_GANG_SCAN_FAILED", ex);
            }
        }

        private void ProcessPoliceHotkeys()
        {
            LSPDControlBindings controls = _config.PoliceControls;
            bool transportKeyHeld = Game.IsKeyPressed(controls.TransportKey);
            bool scatteredGroupTransportAvailable =
                CanRequestScatteredDispatchGroupTransport();
            if (!transportKeyHeld)
                _transportCustodyInputSuppression = false;
            bool activeCustodyInputOwner = Convoy.PlayerOwnsPhysicalCustodyInteraction
                || Convoy.HoldingAtStation
                || Dispatch.State == LSPDDispatchState.Arrested
                || scatteredGroupTransportAvailable;
            if (transportKeyHeld
                && (_transportCustodyInputSuppression || activeCustodyInputOwner))
                LSPDPlayerControlSuppression.SuppressMeleeThisFrame();
            if (WasPoliceKey(controls.PatrolKey, ref _patrolKeyDown))
            {
                bool wasPatrolling = Patrol.IsPatrolling;
                TogglePatrol();
                if (wasPatrolling && Patrol.IsPatrolling && !string.IsNullOrWhiteSpace(LastOperationMessage))
                    Notify(LastOperationMessage);
                return;
            }
            if (WasPoliceKey(controls.ResetKey, ref _resetKeyDown))
            {
                LastOperationMessage = ResetPoliceRuntime();
                Notify(LastOperationMessage);
            }
            if (WasPoliceKey(controls.TransportKey, ref _transportKeyDown))
            {
                string dispatchCustodyResponse;
                bool dispatchNearbyCustodyInputOwned =
                    Dispatch.OwnsNearbyCustodyTransportInput(
                        Game.Player.Character, out dispatchCustodyResponse);
                bool transportInputOwned = Convoy.PlayerOwnsPhysicalCustodyInteraction
                    || Convoy.HoldingAtStation
                    || Dispatch.State == LSPDDispatchState.Arrested
                    || scatteredGroupTransportAvailable
                    || dispatchNearbyCustodyInputOwned;
                _transportCustodyInputSuppression = transportKeyHeld
                    && transportInputOwned;
                if (transportInputOwned)
                    LSPDPlayerControlSuppression.SuppressMeleeThisFrame();

                // R is an approval action only when the prisoner is waiting at
                // the station or the arrest has just been secured. Do not emit
                // a misleading rejection while the officer is still driving.
                if (Convoy.HoldingAtStation || Dispatch.State == LSPDDispatchState.Arrested
                    || scatteredGroupTransportAvailable)
                {
                    LastOperationMessage = RequestPrisonerTransport();
                    Notify(LastOperationMessage);
                }
                else if (dispatchNearbyCustodyInputOwned)
                {
                    LastOperationMessage = dispatchCustodyResponse;
                    Notify(LastOperationMessage);
                }
            }
            if (WasPoliceKey(
                controls.TransportCompleteKey,
                ref _transportCompleteKeyDown))
            {
                if (Convoy.CanUseTerminalCompletion)
                {
                    LastOperationMessage = CompletePrisonerTransport();
                    Notify(LastOperationMessage);
                }
            }
        }

        private string ProcessStationPrisonTransferInput(bool allowGameplayInput, bool paused)
        {
            LSPDControlBindings controls = _config.PoliceControls;
            bool acceptKeyPressed = WasPoliceKey(
                controls.AcceptKey,
                ref _convoyAcceptKeyDown);
            bool rejectKeyPressed = WasPoliceKey(
                controls.RejectKey,
                ref _convoyRejectKeyDown);

            // Dispatch already owns the same Y/N pair while a callout offer is
            // active. This narrow gate handles the post-load station-transport
            // choice and the later station prison-transfer offer. Keep the
            // keyboard edge states synchronized while no choice exists so a
            // held key cannot approve a future transport stage accidentally.
            bool decisionInputAllowed = allowGameplayInput || Convoy.HoldingAtStation;
            bool controllerInput = decisionInputAllowed
                && !paused
                && IsControllerInputActive();
            bool controllerAccept = controllerInput
                && Game.IsControlJustPressed(GTA.Control.FrontendCancel);
            bool controllerReject = controllerInput
                && Game.IsControlJustPressed(GTA.Control.FrontendAccept);

            if (Convoy.DispatchStationTransportDecisionPending)
            {
                if (paused || !allowGameplayInput)
                    return string.Empty;
                if (controllerAccept || acceptKeyPressed)
                {
                    string result = Convoy.AcceptDispatchStationTransport();
                    Notify(result);
                    return result;
                }
                if (controllerReject || rejectKeyPressed)
                {
                    string result = Convoy.DeclineDispatchStationTransport();
                    Notify(result);
                    return result;
                }
                return string.Empty;
            }

            if (HasPendingPreferredUtilityTransportChoice)
            {
                if (paused || !allowGameplayInput)
                    return string.Empty;
                if (controllerAccept || acceptKeyPressed)
                {
                    string result = AcceptPreferredUtilityTransport();
                    Notify(result);
                    return result;
                }
                if (controllerReject || rejectKeyPressed)
                {
                    string result = RequestTransportVanForCurrentDispatch();
                    Notify(result);
                    return result;
                }
                return string.Empty;
            }

            if (paused || !Convoy.HoldingAtStation || !decisionInputAllowed)
                return string.Empty;

            if (controllerAccept || acceptKeyPressed)
            {
                string result = RequestPrisonerTransport();
                Notify(result);
                return result;
            }
            if (controllerReject || rejectKeyPressed)
            {
                string result = DeclinePrisonerTransport();
                Notify(result);
                return result;
            }
            return string.Empty;
        }

        private void QueueConfigurationRefresh()
        {
            _configurationRefreshPending = true;
        }

        /// <summary>
        /// Applies only settings that the existing owners support at runtime.
        /// Each owner keeps the typed configuration reference supplied at
        /// construction; this method handles the few settings that require a
        /// stateful refresh or resource release on the game tick.
        /// </summary>
        private void ApplyPendingConfiguration()
        {
            if (!_configurationRefreshPending)
                return;

            _configurationRefreshPending = false;
            _config.NormalizeSettings();
            Dispatch.ConfigureSuspectInjurySettings(_config.Police.SuspectInjury);
            NpcResponse.ConfigureSuspectInjurySettings(_config.Police.SuspectInjury);
            _log.Configure(
                _config.Logging.RuntimeEnabled,
                _config.Logging.DebugEnabled,
                _config.Logging.VerboseDiagnostics);

            if (_lastAppliedAudioEnabled != _config.EnableAudio
                || _lastAppliedAudioVolume != _config.AudioVolume)
            {
                Audio.SetOptions(_config.EnableAudio, _config.AudioVolume);
                _lastAppliedAudioEnabled = _config.EnableAudio;
                _lastAppliedAudioVolume = _config.AudioVolume;
            }

            if (IsPoliceAuthorityActive)
                _authority.RefreshSettings();
            if (!_config.Police.Response.Enabled)
                Response.ReleaseAll();
            if (!_config.Police.Backup.Enabled)
                Backup.Reset();
            if (!_config.Police.NpcResponse.Enabled)
                NpcResponse.Reset();

            _log.Runtime(
                "POLICE_RUNTIME_SETTINGS_APPLIED",
                "Central settings refresh applied to Authority, audio, Dispatch, NPC, response, backup, convoy, and patrol owners.");
        }

        private void SynchronizePoliceKeyStates()
        {
            if (_config == null || _config.PoliceControls == null)
                return;
            _patrolKeyDown = Game.IsKeyPressed(_config.PoliceControls.PatrolKey);
            _resetKeyDown = Game.IsKeyPressed(_config.PoliceControls.ResetKey);
            _transportKeyDown = Game.IsKeyPressed(_config.PoliceControls.TransportKey);
            _transportCompleteKeyDown = Game.IsKeyPressed(
                _config.PoliceControls.TransportCompleteKey);
            _convoyAcceptKeyDown = Game.IsKeyPressed(_config.PoliceControls.AcceptKey);
            _convoyRejectKeyDown = Game.IsKeyPressed(_config.PoliceControls.RejectKey);
        }

        private static bool WasPoliceKey(Keys key, ref bool wasDown)
        {
            bool isDown = Game.IsKeyPressed(key);
            bool pressed = isDown && !wasDown;
            wasDown = isDown;
            return pressed;
        }

        private static bool IsControllerInputActive()
        {
            try
            {
                return !Function.Call<bool>(Hash.IS_USING_KEYBOARD_AND_MOUSE, 2);
            }
            catch
            {
                // Keep the keyboard Y/N path unambiguous if input-method
                // detection is unavailable; Escape/Backspace must never become
                // a prison-transfer decision by accident.
                return false;
            }
        }

        private void Notify(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                try { Notification.PostTicker(message, false, false); } catch { }
            }
        }
        internal bool ReloadPoliceData()
        {
            if (!RequireActive())
                return false;

            bool loaded = _profile.ReloadAll();
            bool npcLoaded = _npcDatabase.Reload();
            _authority.RefreshSettings();
            if (!loaded || !npcLoaded)
                return Fail("POLICE_DATA_RELOAD_FAILED", "Police XML data could not be reloaded.");
            _gangData.Reload();
            ActivateStationMarkers();

            _log.Runtime(
                "POLICE_DATA_RELOADED",
                "Agencies=" + _profile.Agencies.Count()
                + "; Stations=" + _profile.Stations.Count()
                + "; Peds=" + _profile.PedModels.Count()
                + "; Vehicles=" + _profile.Vehicles.Count()
                + "; Weapons=" + _profile.Weapons.Count()
                + "; CitizenModels=" + _npcDatabase.ModelCount);
            LastOperationMessage = "Police profile, citizen database, and XML collections reloaded.";
            return true;
        }

        internal bool ApplySavedProfile()
        {
            bool pedApplied;
            bool vehicleApplied;
            bool weaponsApplied;
            bool applied = ApplySavedProfile(
                true,
                out pedApplied,
                out vehicleApplied,
                out weaponsApplied);
            if (!applied)
                QueuePendingSavedProfileApply(pedApplied, vehicleApplied, weaponsApplied);
            return applied;
        }

        internal bool TogglePatrol()
        {
            if (!RequireActive())
                return false;
            if (!_profile.StationSetupComplete)
                return Fail(
                    "POLICE_STATION_SETUP_REQUIRED",
                    "Finish the Police station setup before starting Patrol.");
            if (!_config.Police.Patrol.Enabled)
                return Fail(
                    "POLICE_PATROL_DISABLED",
                    "Police Patrol is disabled in LS Immersive settings.");
            if (Patrol.IsPatrolling)
            {
                if (Dispatch.HasIncident
                    || Convoy.Active
                    || CrimeActivity.BlocksOtherPoliceActivities
                    || _gangResponse.HasActiveIncident
                    || NpcResponse.HasActiveInteraction
                    || (Backup.BlocksPlayerActivities
                        && Backup.State != LSPDBackupAssignmentState.StandingDown))
                {
                    LastOperationMessage = "Finish or cancel the active Police assignment before ending Patrol.";
                    _log.Runtime(
                        "POLICE_PATROL_END_BLOCKED",
                        "Patrol end ignored because an active Police assignment still owns the session.");
                    return false;
                }

                Patrol.End();
                Dispatch.Reset();
                Convoy.PrepareForPatrolEnd();
                Response.Reset();
                Backup.PrepareForPatrolEnd();
                _gangResponse.Reset();
                NpcResponse.Reset();
                CrimeActivity.Reset();
                _crimeActivityWasActive = false;
            }
            else
            {
                bool vehicleReady = PreparePatrolVehicle();
                if (!vehicleReady)
                {
                    LastOperationMessage = GetPatrolUtilityPreparationMessage();
                    _log.Runtime(
                        "POLICE_PATROL_START_BLOCKED_UTILITY_UNAVAILABLE",
                        "Patrol remains inactive until the selected Police Utility is physically ready; Model="
                        + _profile.SelectedVehicleModelName);
                    return false;
                }

                Patrol.Start();
                string dispatchStatus = "Automatic Dispatch is waiting for the initial quiet-patrol interval.";
                _log.Runtime(
                    "POLICE_PATROL_INITIAL_ACTIVITY",
                    "DispatchReady=" + Dispatch.HasOffer
                    + "; InitialOfferDelaySeconds=" + LSPDDispatch.InitialPatrolOfferDelaySeconds
                    + "; Result=" + dispatchStatus);
                return Succeed("POLICE_PATROL_AVAILABILITY",
                    IsSelectedStationPoliceVehiclePlacement(_lastPreferredPoliceVehicleSpawnSource)
                        ? "Player’s Utility is ready at the selected Station. Dispatch is standing by."
                        : "Your Police Utility is ready. Dispatch is standing by.");
            }
            return Succeed("POLICE_PATROL_AVAILABILITY",
                "Patrol availability ended.");
        }

        internal bool StartPatrolTowardSelectedStation()
        {
            if (!RequireActive())
                return false;
            if (!_profile.StationSetupComplete)
                return Fail(
                    "POLICE_STATION_SETUP_REQUIRED",
                    "Finish the Police station setup before starting Patrol.");
            if (!_config.Police.Patrol.Enabled)
                return Fail(
                    "POLICE_PATROL_DISABLED",
                    "Police Patrol is disabled in LS Immersive settings.");
            if (Dispatch.HasIncident || Convoy.Active || CrimeActivity.BlocksOtherPoliceActivities)
                return Fail(
                    "POLICE_PATROL_ROUTE_BLOCKED",
                    "The active Police assignment already owns the current GPS route.");

            LSPDPoliceStationDefinition station =
                _profile.FindStation(_profile.Selection.StationId);
            if (station == null)
                return Fail(
                    "POLICE_PATROL_STATION_ROUTE_FAILED",
                    "The selected Police station is unavailable.");

            bool vehicleReady = PreparePatrolVehicle();
            if (!vehicleReady)
            {
                LastOperationMessage = GetPatrolUtilityPreparationMessage();
                _log.Runtime(
                    "POLICE_PATROL_START_BLOCKED_UTILITY_UNAVAILABLE",
                    "Station-route Patrol start remains inactive until the selected Police Utility is physically ready; Station="
                    + station.Id + "; Model=" + _profile.SelectedVehicleModelName);
                return false;
            }

            bool routeSet = _config.ShowWaypoint && TrySetStationWaypoint(station);
            if (!Patrol.IsPatrolling)
                Patrol.Start();

            return Succeed(
                "POLICE_PATROL_STATION_ROUTE",
                "Patrol active." + (routeSet
                    ? " GPS set to " + station.DisplayName + "."
                    : " Route guidance is disabled.")
                + (IsSelectedStationPoliceVehiclePlacement(_lastPreferredPoliceVehicleSpawnSource)
                    ? " Player’s Utility is ready at the selected Station."
                    : " Your Police Utility is ready."));
        }

        internal bool ReceiveDispatch(LSPDDispatchEvent incident)
        {
            // A real incident producer must supply the offer. This bridge will
            // not manufacture calls or interrupt a player who never started
            // patrol; accepted calls remain owned by LSPDDispatch.
            return IsPoliceAuthorityActive && Patrol.IsPatrolling
                && !NpcResponse.HasActiveInteraction
                && !_gangResponse.HasActiveIncident
                && !CrimeActivity.BlocksOtherPoliceActivities
                && incident != null && Dispatch.Receive(incident);
        }

        internal LSPDAudioResult DecideDispatch(bool? accept)
        {
            if (!IsPoliceAuthorityActive || !Patrol.IsPatrolling || !Dispatch.HasOffer)
                return LSPDAudioResult.Inactive;
            LSPDAudioResult result = !accept.HasValue ? Dispatch.Acknowledge()
                : accept.Value ? Dispatch.Accept() : Dispatch.Decline();
            LastOperationMessage = !accept.HasValue
                ? "Dispatch information acknowledged."
                : accept.Value
                    ? Dispatch.HasIncident
                        ? "Dispatch accepted. Proceed to the marked scene."
                        : "The Dispatch scene could not be created safely. The assignment was cancelled."
                    : "Dispatch declined. You remain available for patrol.";
            return result;
        }

        internal string RequestDispatch()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (Dispatch.HasIncident)
            {
                LastOperationMessage = "Finish or cancel the active Dispatch before requesting another one.";
                return LastOperationMessage;
            }
            if (Convoy.Active || CrimeActivity.BlocksOtherPoliceActivities
                || _gangResponse.HasActiveIncident || NpcResponse.HasActiveInteraction
                || (Backup.BlocksPlayerActivities
                    && Backup.State != LSPDBackupAssignmentState.StandingDown))
            {
                LastOperationMessage = "Finish the current Police activity before requesting a Dispatch.";
                return LastOperationMessage;
            }

            if (!Patrol.IsPatrolling)
            {
                if (!_profile.StationSetupComplete)
                {
                    LastOperationMessage = "Finish the Police station setup before requesting a Dispatch.";
                    return LastOperationMessage;
                }
                if (!_config.Police.Patrol.Enabled)
                {
                    LastOperationMessage = "Police Patrol is disabled in LS Immersive settings.";
                    return LastOperationMessage;
                }
                if (!PreparePatrolVehicle())
                {
                    LastOperationMessage = GetPatrolUtilityPreparationMessage();
                    _log.Runtime(
                        "POLICE_DISPATCH_REQUEST_BLOCKED_UTILITY_UNAVAILABLE",
                        "Explicit radio request did not start Patrol because the selected Police Utility was not physically ready; Model="
                        + _profile.SelectedVehicleModelName);
                    return LastOperationMessage;
                }

                Patrol.Start();
                _log.Runtime(
                    "POLICE_PATROL_INITIAL_ACTIVITY",
                    "DispatchReady=" + Dispatch.HasOffer
                    + "; InitialOfferDelaySeconds=" + LSPDDispatch.InitialPatrolOfferDelaySeconds
                    + "; Result=Patrol started by explicit radio request; automatic grace period bypassed for this request.");
            }

            LastOperationMessage = Dispatch.RequestOfferNow(Game.Player.Character);
            return LastOperationMessage;
        }

        internal string RecoverPersonalUtility()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (string.IsNullOrWhiteSpace(_profile.SelectedVehicleModelName))
            {
                LastOperationMessage = "Select a Police Utility in the Police Profile before using Fetch.";
                return LastOperationMessage;
            }
            if (Convoy.Active || CrimeActivity.BlocksOtherPoliceActivities
                || _gangResponse.HasActiveIncident || NpcResponse.HasActiveInteraction)
            {
                LastOperationMessage = "Finish the current custody or Police activity before recovering the Personal Utility.";
                return LastOperationMessage;
            }
            if (HasPendingPreferredUtilityTransportChoice)
            {
                LastOperationMessage = "Finish the current Dispatch transport choice before recovering the Utility assigned to that choice.";
                return LastOperationMessage;
            }
            if (_personalUtilityRecoveryState != PersonalUtilityRecoveryState.None)
            {
                LastOperationMessage = "Your same managed Police Utility is being safely recovered. Wait for it to settle and enter that vehicle.";
                return LastOperationMessage;
            }

            if (_personalUtilityDeliveryState != PersonalUtilityDeliveryState.None)
                CancelPersonalUtilityDelivery("PlayerSelectedGroundedManagedUtilityRecovery");

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                LastOperationMessage = "The Player is unavailable, so the Personal Utility cannot be recovered.";
                return LastOperationMessage;
            }

            Vehicle existing = _managedPoliceVehicle;
            if (existing != null && !existing.Exists())
            {
                _managedPoliceVehicle = null;
                existing = null;
            }

            if (existing != null)
            {
                float distance = existing.Position.DistanceTo(player.Position);
                LSPDPoliceStationDefinition station;
                bool atSelectedStation = IsManagedUtilityAtSelectedStation(
                    existing, out station);
                if (existing.Model.Hash
                    != new Model(_profile.SelectedVehicleModelName).Hash)
                {
                    LastOperationMessage = "The managed vehicle does not match the selected Police Utility. It has been left in place; review the Police Profile before fetching another vehicle.";
                    _log.Runtime(
                        "POLICE_PERSONAL_UTILITY_RECOVERY_REFUSED_MODEL_MISMATCH",
                        "Vehicle=" + existing.Handle
                        + "; ManagedModel=" + existing.Model.Hash
                        + "; SelectedModel=" + new Model(_profile.SelectedVehicleModelName).Hash
                        + "; ReplacementSpawned=false");
                    return LastOperationMessage;
                }
                if (player.CurrentVehicle != null && player.CurrentVehicle.Exists()
                    && player.CurrentVehicle.Handle == existing.Handle)
                {
                    LastOperationMessage = "You are in your managed Police Utility. Press "
                        + _config.PoliceControls.PatrolKey + " to start Patrol.";
                    return LastOperationMessage;
                }

                Ped[] occupants = existing.Occupants;
                if (occupants != null
                    && occupants.Any(occupant => occupant != null && occupant.Exists()))
                {
                    LastOperationMessage = "The managed Police Utility has an occupant and was left in place. Recover it after everyone has exited.";
                    return LastOperationMessage;
                }
                if (Math.Abs(existing.Speed) > 0.5f)
                {
                    LastOperationMessage = "The managed Police Utility is still moving. Wait until it is parked before recovering it.";
                    return LastOperationMessage;
                }

                if (distance <= PersonalUtilityRecoveryDistance)
                {
                    EnsurePreferredPoliceVehicleBlip(existing);
                    if (_config.ShowWaypoint && !Dispatch.HasIncident)
                    {
                        try { World.WaypointPosition = existing.Position; }
                        catch (Exception ex) { _log.Exception("POLICE_PERSONAL_UTILITY_ROUTE_FAILED", ex); }
                    }
                    LastOperationMessage = "Your same managed Police Utility is already nearby. Its map marker is active; enter that vehicle before pressing "
                        + _config.PoliceControls.PatrolKey + ".";
                    _log.Runtime(
                        "POLICE_PERSONAL_UTILITY_RETAINED",
                        "Vehicle=" + existing.Handle
                        + "; Distance=" + distance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; NearThreshold=" + PersonalUtilityRecoveryDistance.ToString("0", CultureInfo.InvariantCulture)
                        + "; ActiveDispatch=" + Dispatch.HasIncident);
                    return LastOperationMessage;
                }

                string cachedFailure;
                if (TryGetPersonalUtilityFetchRetryMessage(
                        existing, player, out cachedFailure))
                {
                    LastOperationMessage = cachedFailure;
                    return LastOperationMessage;
                }

                Vector3 destination;
                float destinationHeading;
                string placementSource;
                string placementFailure;
                if (!TryResolvePersonalUtilityRecoveryPose(
                    player, existing, out destination, out destinationHeading,
                    out placementSource, out placementFailure))
                {
                    LastOperationMessage = atSelectedStation && station != null
                        ? "Your Police Utility remains ready at " + station.DisplayName
                            + ". No nearby grounded parking pose was confirmed, so it stays at the selected Station. "
                            + placementFailure
                        : "Your same Police Utility remains at its current safe location. No nearby grounded parking pose was confirmed. "
                            + placementFailure;
                    EnsurePreferredPoliceVehicleBlip(existing);
                    RememberPersonalUtilityFetchRetry(
                        existing, player, LastOperationMessage);
                    _log.Runtime(
                        "POLICE_PERSONAL_UTILITY_RECOVERY_DEFERRED",
                        "Vehicle=" + existing.Handle
                        + "; PlayerDistance=" + distance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; ActiveDispatch=" + Dispatch.HasIncident
                        + "; Reason=" + placementFailure
                        + "; Result=SameManagedVehicleRetainedAtPreviousLocation");
                    return LastOperationMessage;
                }

                BeginPersonalUtilityRecovery(
                    existing, player, destination, destinationHeading, placementSource);
                ClearPersonalUtilityFetchRetry();
                return LastOperationMessage;
            }

            string previousPlacementFailure;
            if (TryGetPersonalUtilityFetchRetryMessage(
                    null, player, out previousPlacementFailure))
            {
                LastOperationMessage = previousPlacementFailure;
                return LastOperationMessage;
            }

            // Only create a Utility when Core has no valid managed instance.
            // Once created, later Fetch actions reuse this same handle.
            if (!SpawnPoliceVehicle(
                _profile.SelectedVehicleModelName,
                true,
                false,
                false))
            {
                string placementFailure = LastOperationMessage;
                if (IsPreferredPoliceVehiclePlacementFailure(placementFailure))
                {
                    LastOperationMessage = "The Police Utility could not be placed safely. Move to a clear roadside location and retry after repositioning. "
                        + placementFailure;
                    RememberPersonalUtilityFetchRetry(
                        null, player, LastOperationMessage);
                }
                return LastOperationMessage;
            }

            existing = _managedPoliceVehicle;
            if (existing == null || !existing.Exists())
            {
                LastOperationMessage = "The Police Utility was not created; no vehicle handle is available.";
                return LastOperationMessage;
            }

            LSPDPoliceStationDefinition preparedStation;
            bool preparedAtSelectedStation = IsManagedUtilityAtSelectedStation(
                existing, out preparedStation);
            if (preparedAtSelectedStation)
            {
                EnsureManagedStationUtilityGuidance();
                LastOperationMessage = BuildStationUtilityPickupMessage(
                    preparedStation, false);
                _log.Runtime(
                    "POLICE_PERSONAL_UTILITY_RETAINED_AT_STATION",
                    "Vehicle=" + existing.Handle
                    + "; Station=" + preparedStation.Id
                    + "; Result=SameManagedUtilityMarkedForPhysicalPickup");
                return LastOperationMessage;
            }

            ClearPersonalUtilityFetchRetry();
            LastOperationMessage = "Your managed Police Utility is ready. Enter that vehicle, then press "
                + _config.PoliceControls.PatrolKey + " to start Patrol.";
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_CREATED_FOR_FETCH",
                "PreviousVehicle=none; Vehicle=" + existing.Handle
                + "; RecoveryThreshold=" + PersonalUtilityRecoveryDistance.ToString("0", CultureInfo.InvariantCulture)
                + "; ActiveDispatch=" + Dispatch.HasIncident
                + "; Result=CreatedOnceBecauseNoManagedUtilityExisted; StationFallback=false");
            return LastOperationMessage;
        }

        private bool TryResolvePersonalUtilityRecoveryPose(
            Ped player,
            Vehicle managedUtility,
            out Vector3 destination,
            out float destinationHeading,
            out string source,
            out string failure)
        {
            destination = Vector3.Zero;
            destinationHeading = 0f;
            source = string.Empty;
            failure = string.Empty;
            if (player == null || !player.Exists()
                || managedUtility == null || !managedUtility.Exists())
            {
                failure = "The Player or managed Utility is unavailable.";
                return false;
            }

            List<Vector3> requestedPositions = new List<Vector3>();
            List<float> requestedHeadings = new List<float>();
            List<string> requestedSources = new List<string>();
            string nodeFailure = string.Empty;
            string roadsideFailure = string.Empty;
            string parkedGroundFailure = string.Empty;

            Vector3 requestedPosition;
            float requestedHeading;
            if (TryResolveNearbyUnoccupiedVehicleNodeParkingPosition(
                player, player.Position, PersonalUtilityRecoveryMaximumDistance,
                out requestedPosition, out requestedHeading, out nodeFailure))
                AddPersonalUtilityRecoveryCandidate(
                    requestedPositions, requestedHeadings, requestedSources,
                    requestedPosition, requestedHeading, "UnoccupiedVehicleNode");

            bool usedRoadNodeFallback;
            if (TryResolveNearbyRoadsidePosition(
                player, out requestedPosition, out requestedHeading,
                out roadsideFailure, out usedRoadNodeFallback))
                AddPersonalUtilityRecoveryCandidate(
                    requestedPositions, requestedHeadings, requestedSources,
                    requestedPosition, requestedHeading,
                    usedRoadNodeFallback ? "ValidatedRoadNodeRoadside" : "ValidatedRoadside");

            if (TryResolveNearbyParkedGroundPosition(
                player, PersonalUtilityRecoveryMaximumDistance,
                out requestedPosition, out requestedHeading, out parkedGroundFailure))
                AddPersonalUtilityRecoveryCandidate(
                    requestedPositions, requestedHeadings, requestedSources,
                    requestedPosition, requestedHeading, "ValidatedParkedGround");

            if (requestedPositions.Count == 0)
            {
                failure = "No roadside or parked-ground candidate was found within "
                    + PersonalUtilityRecoveryMaximumDistance.ToString("0", CultureInfo.InvariantCulture)
                    + " metres of the Player. "
                    + string.Join(" ", new[] { nodeFailure, roadsideFailure, parkedGroundFailure }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                return false;
            }

            string lastFailure = "The nearby candidates did not pass final footprint validation.";
            List<Vector3> rejectedPositions = new List<Vector3>();
            List<float> rejectedHeadings = new List<float>();
            for (int index = 0; index < requestedPositions.Count; index++)
            {
                Vector3 safePosition;
                float safeHeading;
                bool enhancedValidationConfirmed;
                string candidateFailure;
                if (!TryFindSafePreferredVehiclePose(
                    managedUtility.Model, player,
                    requestedPositions[index], requestedHeadings[index], true,
                    out safePosition, out safeHeading,
                    out enhancedValidationConfirmed, out candidateFailure,
                    rejectedPositions, rejectedHeadings))
                {
                    if (!string.IsNullOrWhiteSpace(candidateFailure))
                        lastFailure = candidateFailure;
                    rejectedPositions.Add(requestedPositions[index]);
                    rejectedHeadings.Add(requestedHeadings[index]);
                    continue;
                }

                bool groundOnlyProvisional = !enhancedValidationConfirmed
                    && !string.IsNullOrWhiteSpace(candidateFailure)
                    && candidateFailure.IndexOf(
                        "only center/corner ground support remained inconclusive",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                if ((!enhancedValidationConfirmed && !groundOnlyProvisional)
                    || Distance2D(safePosition, player.Position)
                        > PersonalUtilityRecoveryMaximumDistance)
                {
                    lastFailure = "Final vehicle-footprint evidence was incomplete or the candidate exceeded the nearby recovery bound.";
                    rejectedPositions.Add(safePosition);
                    rejectedHeadings.Add(safeHeading);
                    continue;
                }

                destination = safePosition;
                destinationHeading = safeHeading;
                source = requestedSources[index]
                    + (groundOnlyProvisional ? "_GroundProvisional" : string.Empty);
                failure = groundOnlyProvisional ? candidateFailure : string.Empty;
                return true;
            }

            failure = "Nearby candidates were rejected after ground, vehicle-footprint, access, and road validation. "
                + lastFailure
                + (string.IsNullOrWhiteSpace(nodeFailure) ? string.Empty : " Node search: " + nodeFailure)
                + (string.IsNullOrWhiteSpace(roadsideFailure) ? string.Empty : " Roadside search: " + roadsideFailure)
                + (string.IsNullOrWhiteSpace(parkedGroundFailure) ? string.Empty : " Parked-ground search: " + parkedGroundFailure);
            return false;
        }

        private static void AddPersonalUtilityRecoveryCandidate(
            IList<Vector3> positions,
            IList<float> headings,
            IList<string> sources,
            Vector3 position,
            float heading,
            string source)
        {
            if (positions == null || headings == null || sources == null
                || position == Vector3.Zero
                || float.IsNaN(heading) || float.IsInfinity(heading))
                return;
            if (positions.Any(existing => Distance2D(existing, position) < 3f))
                return;
            positions.Add(position);
            headings.Add(heading);
            sources.Add(source ?? string.Empty);
        }

        private void BeginPersonalUtilityRecovery(
            Vehicle vehicle,
            Ped player,
            Vector3 destination,
            float destinationHeading,
            string source)
        {
            if (vehicle == null || !vehicle.Exists()
                || player == null || !player.Exists())
                return;

            _personalUtilityRecoveryVehicle = vehicle;
            _personalUtilityRecoveryVehicleHandle = vehicle.Handle;
            _personalUtilityRecoveryOrigin = vehicle.Position;
            _personalUtilityRecoveryOriginHeading = vehicle.Heading;
            _personalUtilityRecoveryDestination = destination;
            _personalUtilityRecoveryDestinationHeading = destinationHeading;
            _personalUtilityRecoveryPlayerPosition = player.Position;
            _personalUtilityRecoveryLastPosition = Vector3.Zero;
            _personalUtilityRecoveryLastHeading = 0f;
            _personalUtilityRecoveryLastGroundZ = 0f;
            _personalUtilityRecoveryGroundSamples = 0;
            _personalUtilityRecoveryStableSamples = 0;
            _personalUtilityRecoveryMoved = false;
            _personalUtilityRecoverySource = source ?? string.Empty;
            _personalUtilityRecoveryState = PersonalUtilityRecoveryState.PreparingCollision;
            _personalUtilityRecoveryDeadline = DateTime.UtcNow.AddSeconds(
                PersonalUtilityRecoveryCollisionTimeoutSeconds);
            _personalUtilityRecoveryNextSampleAt = DateTime.MinValue;
            _personalUtilityRecoveryRevealDeadline = DateTime.MinValue;
            try
            {
                vehicle.IsPersistent = true;
                HoldManagedUtilityForPickup(vehicle);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PERSONAL_UTILITY_RECOVERY_PREPARE_FAILED", ex);
                FailPersonalUtilityRecovery("The managed Utility could not be secured before recovery.");
                return;
            }

            LastOperationMessage = "Recovering the same managed Police Utility to nearby supported ground. You and the current Dispatch remain in place.";
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_RECOVERY_STARTED",
                "Vehicle=" + vehicle.Handle
                + "; Source=" + _personalUtilityRecoverySource
                + "; Origin=" + FormatVehiclePosition(_personalUtilityRecoveryOrigin)
                + "; Destination=" + FormatVehiclePosition(destination)
                + "; Player=" + FormatVehiclePosition(player.Position)
                + "; ActiveDispatch=" + Dispatch.HasIncident
                + "; ReplacementSpawned=false; PlayerMoved=false");
            Notify(LastOperationMessage);
        }

        private static Vector3 CreatePersonalUtilityRecoveryGroundProbe(Vector3 position)
        {
            return new Vector3(
                position.X,
                position.Y,
                position.Z + PersonalUtilityRecoveryGroundProbeHeight);
        }

        private void ProcessPersonalUtilityRecovery(bool paused)
        {
            if (paused || _personalUtilityRecoveryState == PersonalUtilityRecoveryState.None)
                return;

            DateTime now = DateTime.UtcNow;
            Vehicle vehicle = _personalUtilityRecoveryVehicle;
            Ped player = Game.Player.Character;
            if (vehicle == null || !vehicle.Exists()
                || vehicle.Handle != _personalUtilityRecoveryVehicleHandle
                || _managedPoliceVehicle == null || !_managedPoliceVehicle.Exists()
                || _managedPoliceVehicle.Handle != _personalUtilityRecoveryVehicleHandle)
            {
                FailPersonalUtilityRecovery("The original managed Utility handle is no longer available; no replacement was created.");
                return;
            }
            if (HasPendingPreferredUtilityTransportChoice)
            {
                FailPersonalUtilityRecovery("A Dispatch custody choice now owns the Utility. The vehicle was returned to its previous location.");
                return;
            }
            if (player == null || !player.Exists())
            {
                FailPersonalUtilityRecovery("The Player became unavailable during Utility recovery.");
                return;
            }

            if (!_personalUtilityRecoveryMoved)
            {
                Ped[] occupants = vehicle.Occupants;
                if (occupants != null
                    && occupants.Any(occupant => occupant != null && occupant.Exists()))
                {
                    FailPersonalUtilityRecovery(
                        "Someone entered the managed Utility while its nearby position was being prepared; the vehicle was left in place.");
                    return;
                }
                if (Math.Abs(vehicle.Speed) > 0.5f)
                {
                    FailPersonalUtilityRecovery(
                        "The managed Utility began moving before its nearby position was ready; the vehicle was left in place.");
                    return;
                }
            }

            if (_personalUtilityRecoveryState == PersonalUtilityRecoveryState.PreparingCollision)
            {
                try
                {
                    Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                        _personalUtilityRecoveryDestination.X,
                        _personalUtilityRecoveryDestination.Y,
                        _personalUtilityRecoveryDestination.Z);
                    Vector3 groundProbe = CreatePersonalUtilityRecoveryGroundProbe(
                        _personalUtilityRecoveryDestination);
                    float groundZ;
                    Vector3 groundNormal;
                    if (World.GetGroundHeightAndNormal(
                        groundProbe, out groundZ, out groundNormal))
                    {
                        if (float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                            || float.IsNaN(groundNormal.Z) || float.IsInfinity(groundNormal.Z)
                            || groundNormal.Z < 0.80f
                            || Math.Abs(groundZ - _personalUtilityRecoveryDestination.Z) > 2.0f)
                        {
                            FailPersonalUtilityRecovery(
                                "The nearby ground sample confirmed unsupported elevation or slope under the Utility.");
                            return;
                        }

                        if (_personalUtilityRecoveryNextSampleAt == DateTime.MinValue
                            || now >= _personalUtilityRecoveryNextSampleAt)
                        {
                            if (_personalUtilityRecoveryGroundSamples == 0
                                || Math.Abs(groundZ - _personalUtilityRecoveryLastGroundZ) <= 0.35f)
                                _personalUtilityRecoveryGroundSamples++;
                            else
                                _personalUtilityRecoveryGroundSamples = 1;
                            _personalUtilityRecoveryLastGroundZ = groundZ;
                            _personalUtilityRecoveryNextSampleAt = now.AddMilliseconds(
                                StationVehicleSettleSampleIntervalMilliseconds);
                        }

                        if (_personalUtilityRecoveryGroundSamples
                            >= StationVehicleRequiredStableSamples)
                        {
                            _personalUtilityRecoveryDestination = new Vector3(
                                _personalUtilityRecoveryDestination.X,
                                _personalUtilityRecoveryDestination.Y,
                                groundZ);
                            Function.Call(Hash.SET_ENTITY_ALPHA, vehicle.Handle, 0, false);
                            vehicle.Position = _personalUtilityRecoveryDestination;
                            _personalUtilityRecoveryMoved = true;
                            vehicle.Heading = _personalUtilityRecoveryDestinationHeading;
                            vehicle.PlaceOnGround();
                            vehicle.IsPersistent = true;
                            HoldManagedUtilityForPickup(vehicle);
                            _personalUtilityRecoveryState = PersonalUtilityRecoveryState.Settling;
                            _personalUtilityRecoveryDeadline = now.AddSeconds(
                                PersonalUtilityRecoverySettleTimeoutSeconds);
                            _personalUtilityRecoveryNextSampleAt = now.AddMilliseconds(
                                StationVehicleSettleSampleIntervalMilliseconds);
                            _personalUtilityRecoveryLastPosition = vehicle.Position;
                            _personalUtilityRecoveryLastHeading = vehicle.Heading;
                            _personalUtilityRecoveryStableSamples = 0;
                            _log.Runtime(
                                "POLICE_PERSONAL_UTILITY_RECOVERY_VEHICLE_MOVED_HIDDEN",
                                "Vehicle=" + vehicle.Handle
                                + "; Destination=" + FormatVehiclePosition(vehicle.Position)
                                + "; GroundSamples=" + _personalUtilityRecoveryGroundSamples
                                + "; Source=" + _personalUtilityRecoverySource
                                + "; CollisionRequested=true; Alpha=0; PlayerMoved=false");
                        }
                    }
                }
                catch (Exception ex)
                {
                    FailPersonalUtilityRecovery(
                        "Collision or supported ground could not be prepared: " + ex.GetType().Name + ".");
                    return;
                }

                if (_personalUtilityRecoveryState == PersonalUtilityRecoveryState.PreparingCollision
                    && now >= _personalUtilityRecoveryDeadline)
                    FailPersonalUtilityRecovery(
                        "GTA did not confirm nearby ground after the bounded collision-preparation wait.");
                return;
            }

            if (_personalUtilityRecoveryState == PersonalUtilityRecoveryState.Settling)
            {
                if (now >= _personalUtilityRecoveryDeadline)
                {
                    FailPersonalUtilityRecovery(
                        "The same Utility did not settle upright on supported ground within the bounded recovery window.");
                    return;
                }
                if (now < _personalUtilityRecoveryNextSampleAt)
                    return;
                _personalUtilityRecoveryNextSampleAt = now.AddMilliseconds(
                    StationVehicleSettleSampleIntervalMilliseconds);

                try
                {
                    Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                        vehicle.Position.X, vehicle.Position.Y, vehicle.Position.Z);
                    Vector3 groundProbe = CreatePersonalUtilityRecoveryGroundProbe(
                        vehicle.Position);
                    float groundZ;
                    Vector3 groundNormal;
                    bool groundConfirmed = World.GetGroundHeightAndNormal(
                        groundProbe, out groundZ, out groundNormal);
                    bool allWheels = Function.Call<bool>(
                        Hash.IS_VEHICLE_ON_ALL_WHEELS, vehicle);
                    Vector3 currentPosition = vehicle.Position;
                    float currentHeading = vehicle.Heading;
                    float positionDrift = currentPosition.DistanceTo(
                        _personalUtilityRecoveryLastPosition);
                    float headingDrift = PreferredVehicleHeadingDifference(
                        currentHeading, _personalUtilityRecoveryLastHeading);
                    bool supported = groundConfirmed
                        && !float.IsNaN(groundZ) && !float.IsInfinity(groundZ)
                        && !float.IsNaN(groundNormal.Z) && !float.IsInfinity(groundNormal.Z)
                        && groundNormal.Z >= 0.80f
                        && Math.Abs(currentPosition.Z - groundZ) <= 2.0f;
                    bool stable = supported && allWheels
                        && positionDrift <= StationVehicleSettlePositionTolerance
                        && headingDrift <= 1.0f
                        && (_personalUtilityRecoveryStableSamples == 0
                            || Math.Abs(groundZ - _personalUtilityRecoveryLastGroundZ) <= 0.35f);
                    _personalUtilityRecoveryStableSamples = stable
                        ? _personalUtilityRecoveryStableSamples + 1 : 0;
                    _personalUtilityRecoveryLastPosition = currentPosition;
                    _personalUtilityRecoveryLastHeading = currentHeading;
                    if (supported)
                        _personalUtilityRecoveryLastGroundZ = groundZ;

                    if (_personalUtilityRecoveryStableSamples
                        < StationVehicleRequiredStableSamples)
                        return;

                    string finalPoseFailure;
                    PreferredVehiclePlacementResult finalPose =
                        TryValidateCreatedPreferredVehiclePose(
                            vehicle, player,
                            _personalUtilityRecoveryDestination,
                            _personalUtilityRecoveryDestinationHeading,
                            true, out finalPoseFailure);
                    bool finalPoseGroundOnlyInconclusive = finalPose
                        == PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive;
                    if (finalPose != PreferredVehiclePlacementResult.ConfirmedSafe
                        && !finalPoseGroundOnlyInconclusive)
                    {
                        FailPersonalUtilityRecovery(
                            "Final live vehicle validation did not confirm safe parking: "
                            + finalPoseFailure);
                        return;
                    }

                    BeginPreferredPoliceVehicleFade(vehicle);
                    _personalUtilityRecoveryState = PersonalUtilityRecoveryState.FadingIn;
                    _personalUtilityRecoveryRevealDeadline = now.AddMilliseconds(1500);
                    _log.Runtime(
                        "POLICE_PERSONAL_UTILITY_RECOVERY_POSE_CONFIRMED",
                        "Vehicle=" + vehicle.Handle
                        + "; Position=" + FormatVehiclePosition(vehicle.Position)
                        + "; Heading=" + vehicle.Heading.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; StableSamples=" + _personalUtilityRecoveryStableSamples
                        + "; WheelsSupported=true; FootprintAndDriverAccessConfirmed=true"
                        + "; CenterGroundSamplesConfirmed=true; FinalGroundQueryInconclusive="
                        + finalPoseGroundOnlyInconclusive
                        + "; PlayerMoved=false; ActiveDispatch=" + Dispatch.HasIncident);
                }
                catch (Exception ex)
                {
                    FailPersonalUtilityRecovery(
                        "GTA could not complete the final grounded Utility checks: "
                        + ex.GetType().Name + ".");
                }
                return;
            }

            if (_personalUtilityRecoveryState == PersonalUtilityRecoveryState.FadingIn)
            {
                bool fadeStillRunning = _fadingPoliceVehicle != null
                    && _fadingPoliceVehicle.Exists()
                    && _fadingPoliceVehicle.Handle == _personalUtilityRecoveryVehicleHandle;
                if (fadeStillRunning && now < _personalUtilityRecoveryRevealDeadline)
                    return;
                CompletePersonalUtilityRecovery(vehicle, player);
            }
        }

        private void CompletePersonalUtilityRecovery(Vehicle vehicle, Ped player)
        {
            if (vehicle == null || !vehicle.Exists()
                || vehicle.Handle != _personalUtilityRecoveryVehicleHandle)
            {
                FailPersonalUtilityRecovery("The managed Utility disappeared before recovery could finish.");
                return;
            }

            try { Function.Call(Hash.RESET_ENTITY_ALPHA, vehicle.Handle); } catch { }
            try { vehicle.IsPersistent = true; } catch { }
            try { HoldManagedUtilityForPickup(vehicle); } catch { }
            EnsurePreferredPoliceVehicleBlip(vehicle);
            if (_config.ShowWaypoint && !Dispatch.HasIncident)
            {
                try { World.WaypointPosition = vehicle.Position; }
                catch (Exception ex) { _log.Exception("POLICE_PERSONAL_UTILITY_ROUTE_FAILED", ex); }
            }

            LastOperationMessage = "Your same managed Police Utility is now parked safely near you. Enter that vehicle before pressing "
                + _config.PoliceControls.PatrolKey + " to start or continue Patrol.";
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_RECOVERY_COMPLETED",
                "Vehicle=" + vehicle.Handle
                + "; Position=" + FormatVehiclePosition(vehicle.Position)
                + "; Source=" + _personalUtilityRecoverySource
                + "; PlayerMovement=" + (player == null || !player.Exists()
                    ? "unavailable"
                    : Distance2D(player.Position, _personalUtilityRecoveryPlayerPosition)
                        .ToString("0.00", CultureInfo.InvariantCulture) + "m")
                + "; ActiveDispatch=" + Dispatch.HasIncident
                + "; SameHandle=true; ReplacementSpawned=false; ConfigPreserved=true");
            ClearPersonalUtilityRecoveryState();
            ClearPersonalUtilityFetchRetry();
            Notify(LastOperationMessage);
        }

        private void FailPersonalUtilityRecovery(string reason)
        {
            Vehicle vehicle = _personalUtilityRecoveryVehicle;
            bool moved = _personalUtilityRecoveryMoved;
            if (_fadingPoliceVehicle != null
                && _fadingPoliceVehicle.Exists()
                && _fadingPoliceVehicle.Handle == _personalUtilityRecoveryVehicleHandle)
                ResetPreferredPoliceVehicleFade();
            try
            {
                if (vehicle != null && vehicle.Exists()
                    && vehicle.Handle == _personalUtilityRecoveryVehicleHandle)
                {
                    if (moved)
                    {
                        Function.Call(Hash.SET_ENTITY_ALPHA, vehicle.Handle, 0, false);
                        Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                            _personalUtilityRecoveryOrigin.X,
                            _personalUtilityRecoveryOrigin.Y,
                            _personalUtilityRecoveryOrigin.Z);
                        vehicle.Position = _personalUtilityRecoveryOrigin;
                        vehicle.Heading = _personalUtilityRecoveryOriginHeading;
                        vehicle.PlaceOnGround();
                    }
                    Function.Call(Hash.RESET_ENTITY_ALPHA, vehicle.Handle);
                    vehicle.IsPersistent = true;
                    HoldManagedUtilityForPickup(vehicle);
                    EnsurePreferredPoliceVehicleBlip(vehicle);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PERSONAL_UTILITY_RECOVERY_ROLLBACK_FAILED", ex);
            }

            LSPDPoliceStationDefinition station = null;
            bool atStation = vehicle != null && vehicle.Exists()
                && IsManagedUtilityAtSelectedStation(vehicle, out station);
            LastOperationMessage = atStation && station != null
                ? "Your Police Utility remains ready at " + station.DisplayName
                    + ". Nearby recovery was not confirmed; it was returned to its Station position. " + reason
                : "Your same managed Police Utility remains at its previous safe location. Nearby recovery was not confirmed. " + reason;
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_RECOVERY_FAILED",
                "Vehicle=" + (vehicle == null || !vehicle.Exists() ? 0 : vehicle.Handle)
                + "; Reason=" + reason
                + "; RolledBack=" + moved
                + "; ActiveDispatch=" + Dispatch.HasIncident
                + "; ReplacementSpawned=false; CustodyChoicePreserved=true");
            if (vehicle != null && vehicle.Exists() && Game.Player.Character != null
                && Game.Player.Character.Exists())
                RememberPersonalUtilityFetchRetry(vehicle, Game.Player.Character, LastOperationMessage);
            ClearPersonalUtilityRecoveryState();
            Notify(LastOperationMessage);
        }

        private void CancelPersonalUtilityRecovery(string reason)
        {
            if (_personalUtilityRecoveryState == PersonalUtilityRecoveryState.None)
                return;

            Vehicle vehicle = _personalUtilityRecoveryVehicle;
            bool moved = _personalUtilityRecoveryMoved;
            if (_fadingPoliceVehicle != null
                && _fadingPoliceVehicle.Exists()
                && _fadingPoliceVehicle.Handle == _personalUtilityRecoveryVehicleHandle)
                ResetPreferredPoliceVehicleFade();
            try
            {
                if (vehicle != null && vehicle.Exists()
                    && vehicle.Handle == _personalUtilityRecoveryVehicleHandle)
                {
                    if (moved)
                    {
                        Function.Call(Hash.SET_ENTITY_ALPHA, vehicle.Handle, 0, false);
                        Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                            _personalUtilityRecoveryOrigin.X,
                            _personalUtilityRecoveryOrigin.Y,
                            _personalUtilityRecoveryOrigin.Z);
                        vehicle.Position = _personalUtilityRecoveryOrigin;
                        vehicle.Heading = _personalUtilityRecoveryOriginHeading;
                        vehicle.PlaceOnGround();
                    }
                    Function.Call(Hash.RESET_ENTITY_ALPHA, vehicle.Handle);
                    vehicle.IsPersistent = true;
                    HoldManagedUtilityForPickup(vehicle);
                    EnsurePreferredPoliceVehicleBlip(vehicle);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PERSONAL_UTILITY_RECOVERY_CANCEL_RESTORE_FAILED", ex);
            }
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_RECOVERY_CANCELLED",
                "Vehicle=" + _personalUtilityRecoveryVehicleHandle
                + "; Reason=" + reason
                + "; RolledBack=" + moved
                + "; ReplacementSpawned=false");
            ClearPersonalUtilityRecoveryState();
        }

        private void ClearPersonalUtilityRecoveryState()
        {
            _personalUtilityRecoveryState = PersonalUtilityRecoveryState.None;
            _personalUtilityRecoveryVehicle = null;
            _personalUtilityRecoveryVehicleHandle = 0;
            _personalUtilityRecoveryOrigin = Vector3.Zero;
            _personalUtilityRecoveryOriginHeading = 0f;
            _personalUtilityRecoveryDestination = Vector3.Zero;
            _personalUtilityRecoveryDestinationHeading = 0f;
            _personalUtilityRecoveryPlayerPosition = Vector3.Zero;
            _personalUtilityRecoveryLastPosition = Vector3.Zero;
            _personalUtilityRecoveryLastHeading = 0f;
            _personalUtilityRecoveryLastGroundZ = 0f;
            _personalUtilityRecoveryDeadline = DateTime.MinValue;
            _personalUtilityRecoveryNextSampleAt = DateTime.MinValue;
            _personalUtilityRecoveryRevealDeadline = DateTime.MinValue;
            _personalUtilityRecoveryGroundSamples = 0;
            _personalUtilityRecoveryStableSamples = 0;
            _personalUtilityRecoveryMoved = false;
            _personalUtilityRecoverySource = string.Empty;
        }

        private void HoldManagedUtilityForPickup(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
                return;

            Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, true);
            if (_managedPoliceVehicle == null || !_managedPoliceVehicle.Exists()
                || _managedPoliceVehicle.Handle != vehicle.Handle)
                return;

            _personalUtilityBrakeReleasePendingHandle = vehicle.Handle;
            _personalUtilityBrakeReleaseNextAttemptAt = DateTime.MinValue;
        }

        private void ProcessPersonalUtilityParkingBrakeRelease(bool paused)
        {
            int pendingHandle = _personalUtilityBrakeReleasePendingHandle;
            if (paused || pendingHandle == 0
                || _personalUtilityRecoveryState != PersonalUtilityRecoveryState.None
                || _personalUtilityDeliveryState != PersonalUtilityDeliveryState.None
                || Convoy.Active)
                return;

            Vehicle vehicle = _managedPoliceVehicle;
            if (vehicle == null || !vehicle.Exists()
                || vehicle.Handle != pendingHandle)
            {
                _personalUtilityBrakeReleasePendingHandle = 0;
                _personalUtilityBrakeReleaseNextAttemptAt = DateTime.MinValue;
                return;
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()
                || !player.IsInVehicle() || player.CurrentVehicle == null
                || !player.CurrentVehicle.Exists()
                || player.CurrentVehicle.Handle != pendingHandle
                || DateTime.UtcNow < _personalUtilityBrakeReleaseNextAttemptAt)
                return;

            try
            {
                int driverHandle = Function.Call<int>(
                    Hash.GET_PED_IN_VEHICLE_SEAT,
                    vehicle, (int)VehicleSeat.Driver, false);
                if (driverHandle != player.Handle)
                    return;

                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, false);
                _personalUtilityBrakeReleasePendingHandle = 0;
                _personalUtilityBrakeReleaseNextAttemptAt = DateTime.MinValue;
                _log.Runtime(
                    "POLICE_PERSONAL_UTILITY_PARKING_BRAKE_RELEASED",
                    "Vehicle=" + vehicle.Handle
                    + "; Driver=" + player.Handle
                    + "; Result=PlayerCanDriveManagedUtility");
            }
            catch (Exception ex)
            {
                _personalUtilityBrakeReleaseNextAttemptAt = DateTime.UtcNow.AddSeconds(2);
                _log.Exception("POLICE_PERSONAL_UTILITY_PARKING_BRAKE_RELEASE_FAILED", ex);
            }
        }

        private bool TryGetPersonalUtilityFetchRetryMessage(
            Vehicle vehicle,
            Ped player,
            out string message)
        {
            message = string.Empty;
            if (player == null || !player.Exists())
                return false;

            int vehicleHandle = vehicle == null || !vehicle.Exists()
                ? 0 : vehicle.Handle;
            DateTime now = DateTime.UtcNow;
            bool sameAttempt = now < _personalUtilityFetchRetryAfter
                && vehicleHandle == _personalUtilityFetchRetryVehicleHandle
                && string.Equals(
                    _profile.SelectedVehicleModelName,
                    _personalUtilityFetchRetryModelName,
                    StringComparison.OrdinalIgnoreCase)
                && Distance2D(player.Position, _personalUtilityFetchRetryPlayerPosition)
                    <= PersonalUtilityFetchRetryPositionTolerance;
            if (!sameAttempt)
            {
                ClearPersonalUtilityFetchRetry();
                return false;
            }

            int retrySeconds = Math.Max(1, (int)Math.Ceiling(
                (_personalUtilityFetchRetryAfter - now).TotalSeconds));
            message = _personalUtilityFetchRetryMessage
                + " The same Utility search is cooling down; wait "
                + retrySeconds.ToString(CultureInfo.InvariantCulture)
                + " seconds or move to a substantially different area before retrying.";
            return !string.IsNullOrWhiteSpace(message);
        }

        private void RememberPersonalUtilityFetchRetry(
            Vehicle vehicle,
            Ped player,
            string message)
        {
            if (player == null || !player.Exists()
                || string.IsNullOrWhiteSpace(message))
                return;

            _personalUtilityFetchRetryVehicleHandle = vehicle == null || !vehicle.Exists()
                ? 0 : vehicle.Handle;
            _personalUtilityFetchRetryModelName = _profile.SelectedVehicleModelName ?? string.Empty;
            _personalUtilityFetchRetryPlayerPosition = player.Position;
            _personalUtilityFetchRetryMessage = message;
            _personalUtilityFetchRetryAfter = DateTime.UtcNow.AddMilliseconds(
                PersonalUtilityFetchRetryBackoffMilliseconds);
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_FETCH_RETRY_DEFERRED",
                "Vehicle=" + _personalUtilityFetchRetryVehicleHandle
                + "; Model=" + _personalUtilityFetchRetryModelName
                + "; PlayerPosition=" + FormatVehiclePosition(_personalUtilityFetchRetryPlayerPosition)
                + "; RetryAfterMs=" + PersonalUtilityFetchRetryBackoffMilliseconds
                + "; PlayerMovementTolerance="
                + PersonalUtilityFetchRetryPositionTolerance.ToString("0", CultureInfo.InvariantCulture)
                + "m; Result=SamePlacementSearchSuppressedUntilCooldownOrMaterialPlayerMove");
        }

        private void ClearPersonalUtilityFetchRetry()
        {
            _personalUtilityFetchRetryAfter = DateTime.MinValue;
            _personalUtilityFetchRetryPlayerPosition = Vector3.Zero;
            _personalUtilityFetchRetryVehicleHandle = 0;
            _personalUtilityFetchRetryModelName = string.Empty;
            _personalUtilityFetchRetryMessage = string.Empty;
        }

        private bool TryBeginPersonalUtilityDelivery(
            Vehicle vehicle,
            Ped player,
            bool fromSelectedStation,
            LSPDPoliceStationDefinition station,
            out string failure)
        {
            failure = string.Empty;
            if (vehicle == null || !vehicle.Exists()
                || player == null || !player.Exists())
            {
                failure = "The managed Utility or Player is unavailable for delivery.";
                return false;
            }

            Vector3 destination;
            float destinationHeading;
            if (!TryResolveNearbyUnoccupiedVehicleNodeParkingPosition(
                    player, player.Position, 120f,
                    out destination, out destinationHeading, out failure))
            {
                failure = "No safe nearby parking approach was confirmed. " + failure;
                return false;
            }

            float routeDistance;
            string routeEvidence;
            if (!TryValidatePersonalUtilityDeliveryRoute(
                    vehicle.Position, destination, out routeDistance, out routeEvidence))
            {
                failure = "GTA could not confirm a connected road route to the safe parking approach. "
                    + routeEvidence;
                return false;
            }

            string officerModelName = _profile.SelectedPedModelName;
            Model officerModel = new Model(officerModelName);
            if (string.IsNullOrWhiteSpace(officerModelName)
                || !officerModel.IsValid || !officerModel.IsPed)
            {
                failure = "The selected Police officer model is unavailable for Utility delivery.";
                return false;
            }

            _personalUtilityDeliveryVehicleHandle = vehicle.Handle;
            _personalUtilityDeliveryOrigin = vehicle.Position;
            _personalUtilityDeliveryDestination = destination;
            _personalUtilityDeliveryDestinationHeading = destinationHeading;
            _personalUtilityDeliveryLastPosition = vehicle.Position;
            _personalUtilityDeliveryNextUpdateAt = DateTime.UtcNow;
            _personalUtilityDeliveryLastProgressAt = DateTime.UtcNow;
            _personalUtilityDeliveryStateDeadline = DateTime.UtcNow.AddSeconds(25);
            _personalUtilityDeliveryRecoveryAttempts = 0;
            _personalUtilityDeliveryOfficer = null;
            _personalUtilityDeliveryState = PersonalUtilityDeliveryState.PreparingDriver;
            EnsurePreferredPoliceVehicleBlip(vehicle);
            if (_config.ShowWaypoint)
            {
                try { World.WaypointPosition = destination; }
                catch (Exception ex) { _log.Exception("POLICE_PERSONAL_UTILITY_ROUTE_FAILED", ex); }
            }

            string stationEvidence = fromSelectedStation && station != null
                ? "; Station=" + station.Id
                : string.Empty;
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_DELIVERY_STARTED",
                "Vehicle=" + vehicle.Handle
                + "; PreviousVehicle=" + vehicle.Handle
                + stationEvidence
                + "; Destination=" + FormatVehiclePosition(destination)
                + "; DestinationHeading=" + destinationHeading.ToString("0.0", CultureInfo.InvariantCulture)
                + "; RoadTravelDistance=" + routeDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + "; Route=" + routeEvidence
                + "; DriverModel=" + officerModelName
                + "; ExistingManagedVehicleRetained=true");
            LastOperationMessage = "Your Police Utility is on its way to a safe parking spot near you. Enter it when it arrives, then press "
                + _config.PoliceControls.PatrolKey + " to start Patrol.";
            return true;
        }

        private bool TryValidatePersonalUtilityDeliveryRoute(
            Vector3 from,
            Vector3 destination,
            out float travelDistance,
            out string evidence)
        {
            travelDistance = 0f;
            evidence = string.Empty;
            Vector3 fromNode;
            Vector3 destinationNode;
            float fromHeading;
            float destinationHeading;
            if (!TryResolvePersonalUtilityRoadNode(from, out fromNode, out fromHeading)
                || !TryResolvePersonalUtilityRoadNode(
                    destination, out destinationNode, out destinationHeading))
            {
                evidence = "Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING/IS_POINT_ON_ROAD; Result=NoValidatedEndpoint";
                return false;
            }

            float straightDistance = Distance2D(fromNode, destinationNode);
            if (straightDistance <= 3f)
            {
                travelDistance = straightDistance;
                evidence = "Native=SameVehicleRoadNode; From=" + fromNode
                    + "; Destination=" + destinationNode;
                return true;
            }

            try
            {
                travelDistance = Function.Call<float>(
                    Hash.CALCULATE_TRAVEL_DISTANCE_BETWEEN_POINTS,
                    fromNode.X, fromNode.Y, fromNode.Z,
                    destinationNode.X, destinationNode.Y, destinationNode.Z);
                float maximumPlausible = Math.Max(250f, straightDistance * 8f + 100f);
                bool connected = !float.IsNaN(travelDistance)
                    && !float.IsInfinity(travelDistance)
                    && travelDistance > 0f && travelDistance < 99999f
                    && travelDistance <= maximumPlausible;
                evidence = "Native=CALCULATE_TRAVEL_DISTANCE_BETWEEN_POINTS"
                    + "; FromNode=" + fromNode
                    + "; DestinationNode=" + destinationNode
                    + "; StraightDistance=" + straightDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; TravelDistance=" + travelDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Connected=" + connected;
                return connected;
            }
            catch (Exception ex)
            {
                evidence = "Native=CALCULATE_TRAVEL_DISTANCE_BETWEEN_POINTS; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        private static bool TryResolvePersonalUtilityRoadNode(
            Vector3 query,
            out Vector3 roadNode,
            out float heading)
        {
            roadNode = Vector3.Zero;
            heading = 0f;
            try
            {
                using (OutputArgument nodeOutput = new OutputArgument())
                using (OutputArgument headingOutput = new OutputArgument())
                {
                    bool found = Function.Call<bool>(
                        Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        query.X, query.Y, query.Z,
                        nodeOutput, headingOutput, 1, 3.0f, 0);
                    if (!found)
                        return false;
                    roadNode = nodeOutput.GetResult<Vector3>();
                    heading = headingOutput.GetResult<float>();
                }
                return IsFinitePoliceVehiclePoint(roadNode)
                    && !float.IsNaN(heading) && !float.IsInfinity(heading)
                    && Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                        roadNode.X, roadNode.Y, roadNode.Z, 0);
            }
            catch
            {
                return false;
            }
        }

        private void ProcessPersonalUtilityDelivery(bool paused)
        {
            if (paused || _personalUtilityDeliveryState == PersonalUtilityDeliveryState.None)
                return;

            DateTime now = DateTime.UtcNow;
            Ped player = Game.Player.Character;
            Vehicle vehicle = _managedPoliceVehicle;
            if (vehicle == null || !vehicle.Exists()
                || vehicle.Handle != _personalUtilityDeliveryVehicleHandle)
            {
                _log.Runtime(
                    "POLICE_PERSONAL_UTILITY_DELIVERY_CANCELLED",
                    "Vehicle=" + _personalUtilityDeliveryVehicleHandle
                    + "; Reason=ManagedHandleUnavailableOrReplaced; Result=NoReplacementSpawned");
                CancelPersonalUtilityDelivery("ManagedHandleUnavailableOrReplaced");
                LastOperationMessage = "The managed Police Utility is no longer available. No replacement was spawned automatically.";
                Notify(LastOperationMessage);
                return;
            }

            if (now < _personalUtilityDeliveryNextUpdateAt)
                return;
            _personalUtilityDeliveryNextUpdateAt = now.AddMilliseconds(500);

            if (_personalUtilityDeliveryState == PersonalUtilityDeliveryState.PreparingDriver)
            {
                string officerModelName = _profile.SelectedPedModelName;
                Model officerModel = new Model(officerModelName);
                if (!officerModel.IsValid || !officerModel.IsPed)
                {
                    FailPersonalUtilityDelivery("SelectedPoliceOfficerModelInvalid");
                    return;
                }
                if (!officerModel.IsLoaded)
                {
                    if (now >= _personalUtilityDeliveryStateDeadline)
                    {
                        FailPersonalUtilityDelivery("SelectedPoliceOfficerModelTimedOut");
                        return;
                    }
                    officerModel.Request();
                    _personalUtilityDeliveryNextUpdateAt = now.AddMilliseconds(
                        PersonalUtilityDeliveryRetryMilliseconds);
                    return;
                }

                try
                {
                    Ped officer = vehicle.CreatePedOnSeat(VehicleSeat.Driver, officerModel);
                    if (officer == null || !officer.Exists())
                    {
                        FailPersonalUtilityDelivery("TemporaryDeliveryOfficerCreationFailed");
                        return;
                    }
                    officer.IsPersistent = true;
                    officer.BlockPermanentEvents = true;
                    officer.MaxHealth = Math.Max(officer.MaxHealth, 250);
                    officer.Health = Math.Max(officer.Health, 250);
                    try { Function.Call(Hash.SET_PED_AS_COP, officer, true); } catch { }
                    _personalUtilityDeliveryOfficer = officer;
                    _personalUtilityDeliveryState = PersonalUtilityDeliveryState.EnRoute;
                    _personalUtilityDeliveryLastPosition = vehicle.Position;
                    _personalUtilityDeliveryLastProgressAt = now;
                    _personalUtilityDeliveryStateDeadline = now.AddMinutes(4);
                    if (!IssuePersonalUtilityDeliveryDriveTask(vehicle, officer,
                            _personalUtilityDeliveryDestination))
                    {
                        FailPersonalUtilityDelivery("InitialVehicleDriveTaskFailed");
                        return;
                    }
                    _log.Runtime(
                        "POLICE_PERSONAL_UTILITY_DELIVERY_DRIVER_READY",
                        "Vehicle=" + vehicle.Handle + "; Officer=" + officer.Handle
                        + "; Destination=" + FormatVehiclePosition(_personalUtilityDeliveryDestination)
                        + "; Speed=18.0; TaskIssuedOnce=true");
                }
                catch (Exception ex)
                {
                    _log.Exception("POLICE_PERSONAL_UTILITY_DELIVERY_DRIVER_FAILED", ex);
                    FailPersonalUtilityDelivery("TemporaryDeliveryOfficerSetupException:" + ex.GetType().Name);
                }
                return;
            }

            Ped driver = _personalUtilityDeliveryOfficer;
            if (_personalUtilityDeliveryState == PersonalUtilityDeliveryState.EnRoute)
            {
                if (driver == null || !driver.Exists() || driver.IsDead
                    || !driver.IsInVehicle(vehicle))
                {
                    FailPersonalUtilityDelivery("DeliveryOfficerLostOrLeftVehicle");
                    return;
                }

                float distanceToDestination = Distance2D(
                    vehicle.Position, _personalUtilityDeliveryDestination);
                float speed = 0f;
                try { speed = Function.Call<float>(Hash.GET_ENTITY_SPEED, vehicle); } catch { }
                float moved = Distance2D(vehicle.Position, _personalUtilityDeliveryLastPosition);
                float reducedDistance = Distance2D(
                    _personalUtilityDeliveryLastPosition, _personalUtilityDeliveryDestination)
                    - distanceToDestination;
                if (moved >= 2.5f || reducedDistance >= 2.5f)
                {
                    _personalUtilityDeliveryLastProgressAt = now;
                    _personalUtilityDeliveryLastPosition = vehicle.Position;
                }

                if (distanceToDestination <= 3f && speed <= 1.5f)
                {
                    PreferredVehiclePlacementResult finalResult;
                    string finalFailure;
                    finalResult = TryValidateCreatedPreferredVehiclePose(
                        vehicle, player, _personalUtilityDeliveryDestination,
                        _personalUtilityDeliveryDestinationHeading, false, out finalFailure);
                    if (finalResult == PreferredVehiclePlacementResult.ConfirmedSafe)
                    {
                        _personalUtilityDeliveryState = PersonalUtilityDeliveryState.AwaitingDriverExit;
                        _personalUtilityDeliveryStateDeadline = now.AddSeconds(8);
                        try { Function.Call(Hash.TASK_LEAVE_VEHICLE, driver, vehicle, 0); }
                        catch (Exception ex)
                        {
                            _log.Exception("POLICE_PERSONAL_UTILITY_DELIVERY_DRIVER_EXIT_FAILED", ex);
                            FailPersonalUtilityDelivery("DriverCouldNotExitAtValidatedParking");
                            return;
                        }
                        _log.Runtime(
                            "POLICE_PERSONAL_UTILITY_DELIVERY_PARKED",
                            "Vehicle=" + vehicle.Handle + "; Officer=" + driver.Handle
                            + "; Position=" + FormatVehiclePosition(vehicle.Position)
                            + "; Heading=" + vehicle.Heading.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Speed=" + speed.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; FinalPose=ConfirmedSafe; DriverExit=Started");
                        return;
                    }
                    if (finalResult == PreferredVehiclePlacementResult.ConfirmedUnsafe)
                    {
                        FailPersonalUtilityDelivery("FinalParkingRejected:" + finalFailure);
                        return;
                    }
                }

                if (now >= _personalUtilityDeliveryStateDeadline)
                {
                    FailPersonalUtilityDelivery("DeliveryExceededBoundedFourMinuteWindow");
                    return;
                }

                if (now - _personalUtilityDeliveryLastProgressAt
                    >= TimeSpan.FromMilliseconds(PersonalUtilityDeliveryNoProgressMilliseconds))
                {
                    if (_personalUtilityDeliveryRecoveryAttempts
                        < PersonalUtilityDeliveryMaximumRecoveryAttempts
                        && player != null && player.Exists())
                    {
                        _personalUtilityDeliveryRecoveryAttempts++;
                        Vector3 alternate;
                        float alternateHeading;
                        string alternateFailure;
                        float alternateRouteDistance;
                        string alternateRouteEvidence;
                        if (TryResolveNearbyUnoccupiedVehicleNodeParkingPosition(
                                player, player.Position, 120f,
                                out alternate, out alternateHeading, out alternateFailure)
                            && Distance2D(alternate, _personalUtilityDeliveryDestination) > 15f
                            && TryValidatePersonalUtilityDeliveryRoute(
                                vehicle.Position, alternate,
                                out alternateRouteDistance, out alternateRouteEvidence))
                        {
                            _personalUtilityDeliveryDestination = alternate;
                            _personalUtilityDeliveryDestinationHeading = alternateHeading;
                            _personalUtilityDeliveryLastPosition = vehicle.Position;
                            _personalUtilityDeliveryLastProgressAt = now;
                            if (IssuePersonalUtilityDeliveryDriveTask(vehicle, driver, alternate))
                            {
                                _log.Runtime(
                                    "POLICE_PERSONAL_UTILITY_DELIVERY_ROUTE_RECOVERED",
                                    "Vehicle=" + vehicle.Handle + "; Officer=" + driver.Handle
                                    + "; Attempt=1; NewDestination=" + FormatVehiclePosition(alternate)
                                    + "; RoadTravelDistance=" + alternateRouteDistance.ToString("0.0", CultureInfo.InvariantCulture)
                                    + "; Route=" + alternateRouteEvidence
                                    + "; ChangedTarget=true");
                                return;
                            }
                        }
                        else
                        {
                            _log.Runtime(
                                "POLICE_PERSONAL_UTILITY_DELIVERY_ROUTE_RECOVERY_REJECTED",
                                "Vehicle=" + vehicle.Handle + "; Attempt=1"
                                + "; Reason=" + alternateFailure
                                + "; Result=NoDifferentConnectedSafeApproach");
                        }
                    }

                    FailPersonalUtilityDelivery("NoMeaningfulProgressAfterBoundedRouteRecovery");
                }
                return;
            }

            if (_personalUtilityDeliveryState == PersonalUtilityDeliveryState.AwaitingDriverExit)
            {
                if (driver == null || !driver.Exists() || !driver.IsInVehicle(vehicle))
                {
                    CompletePersonalUtilityDelivery(vehicle, driver);
                    return;
                }
                if (now >= _personalUtilityDeliveryStateDeadline)
                {
                    try { driver.Delete(); } catch { }
                    CompletePersonalUtilityDelivery(vehicle, null);
                }
            }
        }

        private bool IssuePersonalUtilityDeliveryDriveTask(
            Vehicle vehicle,
            Ped driver,
            Vector3 destination)
        {
            if (vehicle == null || !vehicle.Exists()
                || driver == null || !driver.Exists())
                return false;
            try
            {
                Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 0.25f);
                driver.Task.DriveTo(
                    vehicle, destination, 18f,
                    VehicleDrivingFlags.DrivingModeStopForVehicles, 2f);
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PERSONAL_UTILITY_DELIVERY_DRIVE_TASK_FAILED", ex);
                return false;
            }
        }

        private void FailPersonalUtilityDelivery(string reason)
        {
            Vehicle vehicle = _managedPoliceVehicle;
            LSPDPoliceStationDefinition station = null;
            bool remainsAtStation = vehicle != null && vehicle.Exists()
                && IsManagedUtilityAtSelectedStation(vehicle, out station);
            Ped deliveryOfficer = _personalUtilityDeliveryOfficer;
            if (deliveryOfficer != null && deliveryOfficer.Exists())
            {
                try { deliveryOfficer.Task.ClearAll(); } catch { }
                try { deliveryOfficer.Delete(); } catch { }
            }
            _personalUtilityDeliveryOfficer = null;
            if (vehicle != null && vehicle.Exists())
            {
                try { HoldManagedUtilityForPickup(vehicle); } catch { }
                try { vehicle.IsPersistent = true; } catch { }
            }
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_DELIVERY_FAILED",
                "Vehicle=" + (vehicle == null || !vehicle.Exists() ? 0 : vehicle.Handle)
                + "; Reason=" + reason
                + "; Result=ManagedVehiclePreserved; NoReplacementSpawned");
            ClearPersonalUtilityDeliveryState();
            LastOperationMessage = remainsAtStation
                ? "Your Police Utility remains at " + station.DisplayName
                    + ". GTA could not complete a safe road delivery; use the Station handoff."
                : "Your Police Utility remains available at its last safe position. GTA could not complete a safe road delivery.";
            Notify(LastOperationMessage);
        }

        private void CompletePersonalUtilityDelivery(Vehicle vehicle, Ped officer)
        {
            if (officer != null && officer.Exists())
            {
                try { officer.Task.ClearAll(); } catch { }
                try { officer.Delete(); } catch { }
            }
            _personalUtilityDeliveryOfficer = null;
            EnsurePreferredPoliceVehicleBlip(vehicle);
            LastOperationMessage = "Your Police Utility has arrived and is parked safely. Enter it, then press "
                + _config.PoliceControls.PatrolKey + " to start Patrol.";
            _log.Runtime(
                "POLICE_PERSONAL_UTILITY_DELIVERY_COMPLETED",
                "Vehicle=" + vehicle.Handle
                + "; Position=" + FormatVehiclePosition(vehicle.Position)
                + "; Player=" + (Game.Player.Character == null ? 0 : Game.Player.Character.Handle)
                + "; Result=SameManagedVehicleParkedAndDriverReleased");
            ClearPersonalUtilityDeliveryState();
            Notify(LastOperationMessage);
        }

        private void CancelPersonalUtilityDelivery(string reason)
        {
            Ped officer = _personalUtilityDeliveryOfficer;
            if (officer != null && officer.Exists())
            {
                try { officer.Task.ClearAll(); } catch { }
                try { officer.Delete(); } catch { }
            }
            if (_personalUtilityDeliveryState != PersonalUtilityDeliveryState.None
                && _log != null)
                _log.Runtime(
                    "POLICE_PERSONAL_UTILITY_DELIVERY_CANCELLED",
                    "Vehicle=" + _personalUtilityDeliveryVehicleHandle
                    + "; Reason=" + reason + "; Result=OwnedOfficerReleased; VehicleHandleRetainedUntilNormalCoreCleanup");
            ClearPersonalUtilityDeliveryState();
        }

        private void ClearPersonalUtilityDeliveryState()
        {
            _personalUtilityDeliveryState = PersonalUtilityDeliveryState.None;
            _personalUtilityDeliveryOfficer = null;
            _personalUtilityDeliveryVehicleHandle = 0;
            _personalUtilityDeliveryOrigin = Vector3.Zero;
            _personalUtilityDeliveryDestination = Vector3.Zero;
            _personalUtilityDeliveryDestinationHeading = 0f;
            _personalUtilityDeliveryLastPosition = Vector3.Zero;
            _personalUtilityDeliveryNextUpdateAt = DateTime.MinValue;
            _personalUtilityDeliveryLastProgressAt = DateTime.MinValue;
            _personalUtilityDeliveryStateDeadline = DateTime.MinValue;
            _personalUtilityDeliveryRecoveryAttempts = 0;
        }

        internal string InvestigateDispatch()
        {
            if (!RequireActive())
                return LastOperationMessage;
            string message = Dispatch.Investigate(Game.Player.Character);
            LastOperationMessage = message;
            return message;
        }

        internal string SecureDispatchSuspect()
        {
            if (!RequireActive())
                return LastOperationMessage;
            string message = Dispatch.SecureSuspect(Game.Player.Character);
            LastOperationMessage = message;
            return message;
        }

        internal string ArrestDispatchSuspect()
        {
            if (!RequireActive())
                return LastOperationMessage;
            string message = Dispatch.ArrestSuspect(Game.Player.Character);
            MaintainDispatchArrestInvincibility();
            LastOperationMessage = message;
            return message;
        }

        private void MaintainDispatchArrestInvincibility()
        {
            LSPDDispatchEvent incident = Dispatch == null ? null : Dispatch.Current;
            Dictionary<int, Ped> dispatchCustodyTargets =
                new Dictionary<int, Ped>();
            if (incident != null && incident.OwnedByDispatch)
            {
                if (incident.PlayerHandcuffInProgress
                    && incident.PlayerHandcuffTargetHandle > 0
                    && incident.Suspects != null)
                    AddDispatchArrestProtectionTarget(
                        dispatchCustodyTargets,
                        incident.Suspects.FirstOrDefault(ped => ped != null
                            && ped.Exists()
                            && ped.Handle == incident.PlayerHandcuffTargetHandle));

                if (incident.BackupArrestInProgressSuspectHandles != null
                    && incident.Suspects != null)
                    foreach (Ped suspect in incident.Suspects)
                        if (suspect != null && suspect.Exists()
                            && incident.BackupArrestInProgressSuspectHandles
                                .Contains(suspect.Handle))
                            AddDispatchArrestProtectionTarget(
                                dispatchCustodyTargets, suspect);

                foreach (Ped suspect in Dispatch.CurrentArrestedSuspects)
                    AddDispatchArrestProtectionTarget(
                        dispatchCustodyTargets, suspect);
            }

            HashSet<int> convoyCustodyHandles = new HashSet<int>(
                Convoy.ProtectedActors
                    .Where(ped => ped != null && ped.Exists())
                    .Select(ped => ped.Handle));
            foreach (DispatchArrestInvincibilityState state
                in _dispatchArrestInvincibilityStates.Values.ToArray())
            {
                bool dispatchStillOwns = dispatchCustodyTargets.ContainsKey(
                    state.Handle);
                bool convoyStillOwns = convoyCustodyHandles.Contains(
                    state.Handle);
                if (dispatchStillOwns || convoyStillOwns)
                {
                    MaintainDispatchArrestInvincibility(state.Actor);
                    continue;
                }

                RestoreDispatchArrestInvincibility(
                    state, "DispatchOrConvoyCustodyReleased");
                _dispatchArrestInvincibilityStates.Remove(state.Handle);
            }

            foreach (Ped suspect in dispatchCustodyTargets.Values)
                MaintainDispatchArrestInvincibility(suspect);
        }

        private static void AddDispatchArrestProtectionTarget(
            IDictionary<int, Ped> targets,
            Ped suspect)
        {
            if (targets != null && suspect != null && suspect.Exists()
                && !suspect.IsDead && suspect.Health > 0)
                targets[suspect.Handle] = suspect;
        }

        private void MaintainDispatchArrestInvincibility(Ped suspect)
        {
            if (suspect == null || !suspect.Exists()
                || suspect.IsDead || suspect.Health <= 0)
                return;

            DispatchArrestInvincibilityState state;
            if (!_dispatchArrestInvincibilityStates.TryGetValue(
                    suspect.Handle, out state))
            {
                state = new DispatchArrestInvincibilityState(
                    suspect, suspect.IsInvincible);
                _dispatchArrestInvincibilityStates[suspect.Handle] = state;
            }

            int maximumHealth = Math.Max(1, suspect.MaxHealth);
            int healthFloor = Math.Min(
                Math.Max(1, _config.Police.SuspectInjury.ArrestableWoundedHealthFloor),
                Math.Max(1, maximumHealth / 4));
            if (suspect.Health > 0 && suspect.Health < healthFloor)
            {
                int previousHealth = suspect.Health;
                suspect.Health = healthFloor;
                _log.Runtime(
                    "POLICE_CORE_DISPATCH_ARREST_HEALTH_FLOOR_APPLIED",
                    "Suspect=" + suspect.Handle
                    + "; Health=" + previousHealth + "->" + suspect.Health
                    + "; Floor=" + healthFloor
                    + "; DeadBeforeWrite=false; Owner=PoliceCoreArrestSafety");
            }

            if (!suspect.IsInvincible)
            {
                suspect.IsInvincible = true;
                _log.Runtime(
                    "POLICE_CORE_DISPATCH_ARREST_PROTECTION_APPLIED",
                    "Suspect=" + suspect.Handle
                    + "; State=PhysicalArrestOrActiveCustody"
                    + "; IsInvincible=true; Owner=PoliceCoreArrestSafety");
            }
        }

        private void RestoreAllDispatchArrestInvincibility(string reason)
        {
            foreach (DispatchArrestInvincibilityState state
                in _dispatchArrestInvincibilityStates.Values.ToArray())
                RestoreDispatchArrestInvincibility(state, reason);
            _dispatchArrestInvincibilityStates.Clear();
        }

        private void RestoreDispatchArrestInvincibility(
            DispatchArrestInvincibilityState state,
            string reason)
        {
            if (state == null || state.Actor == null || !state.Actor.Exists())
                return;
            try
            {
                state.Actor.IsInvincible = state.OriginalIsInvincible;
                _log.Runtime(
                    "POLICE_CORE_DISPATCH_ARREST_PROTECTION_RESTORED",
                    "Suspect=" + state.Handle
                    + "; IsInvincible=" + state.OriginalIsInvincible
                    + "; Reason=" + (string.IsNullOrWhiteSpace(reason)
                        ? "CustodyReleased" : reason));
            }
            catch (Exception ex)
            {
                _log.Exception(
                    "POLICE_CORE_DISPATCH_ARREST_PROTECTION_RESTORE_FAILED", ex);
            }
        }

        internal bool CanReleaseCurrentDispatchSuspect
        {
            get
            {
                Ped suspect = Dispatch.CurrentSuspect;
                return IsPoliceAuthorityActive && Patrol.IsPatrolling
                    && suspect != null && suspect.Exists() && !suspect.IsDead
                    && (Dispatch.CanReleaseCurrentSuspect
                        || Convoy.CanReleaseDispatchSuspect(suspect));
            }
        }

        internal string ReleaseDispatchSuspect()
        {
            if (!RequireActive())
                return LastOperationMessage;

            Ped suspect = Dispatch.CurrentSuspect;
            if (suspect != null && _pendingDispatchTransportPrisoner != null
                && suspect.Handle == _pendingDispatchTransportPrisoner.Handle)
                ClearPendingPreferredUtilityTransportChoice();
            string message;
            if (suspect != null && Convoy.CanReleaseDispatchSuspect(suspect))
            {
                message = Convoy.RequestDispatchSuspectRelease(suspect);
                Ped released = Convoy.ConsumeDispatchReleasedSuspect();
                if (released != null)
                    message = Dispatch.CompletePhysicalSuspectRelease(released);
            }
            else if (Dispatch.CanReleaseCurrentSuspect)
            {
                message = Dispatch.ReleaseCurrentSuspect();
            }
            else
            {
                message = "The active Dispatch suspect is not ready for release. Keep the suspect in custody and finish the physical handoff.";
            }

            LastOperationMessage = message;
            return message;
        }

        internal string RequestPrisonerTransport()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!_config.Police.Convoy.Enabled)
            {
                LastOperationMessage = "Prisoner transport is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }

            if (!string.IsNullOrWhiteSpace(_pendingDispatchTransportIncidentId)
                && !IsPendingPreferredUtilityTransportChoiceCurrent())
                ClearPendingPreferredUtilityTransportChoice();
            if (HasPendingPreferredUtilityTransportChoice)
            {
                LastOperationMessage = "Choose whether to use your Police Utility or request a transport van in Dispatch Activity.";
                return LastOperationMessage;
            }

            if (Dispatch.HasConvoyCustodyHandoff)
            {
                if (Convoy.HoldingAtStation)
                {
                    string transfer = Convoy.ContinueToPrison();
                    if (Convoy.State == LSPDDispatchState.PrisonTransfer)
                        Dispatch.SetConvoyState(LSPDDispatchState.PrisonTransfer);
                    LastOperationMessage = transfer;
                    return transfer;
                }

                LastOperationMessage = Convoy.Active
                    ? "Prisoner custody is already in transport. Continue following the active Convoy route."
                    : "Prisoner custody is already owned by Convoy and is being reconciled.";
                return LastOperationMessage;
            }

            if (Convoy.DispatchGroupPlayerCollectionActive)
            {
                LastOperationMessage = "Scattered-group transport is already active. Continue to the marked custody stop; Backup remains with any suspect not yet aboard.";
                return LastOperationMessage;
            }

            // Independent Convoy deliberately has no Dispatch incident. Once
            // its physical station handoff is complete, the same approval
            // control must continue that Convoy directly instead of routing
            // through Dispatch's Arrested-state requirement.
            if (Convoy.IsRequestedConvoyActivity && Convoy.HoldingAtStation)
            {
                LastOperationMessage = Convoy.ContinueToPrison();
                return LastOperationMessage;
            }

            bool scatteredGroupTransport =
                CanRequestScatteredDispatchGroupTransport();
            if (Dispatch.State != LSPDDispatchState.Arrested
                && !scatteredGroupTransport)
            {
                LastOperationMessage = "Secure and arrest the suspect before requesting transport.";
                return LastOperationMessage;
            }

            if (Dispatch.HasPendingVictimInvestigation)
            {
                LastOperationMessage = "Investigate the murder victim at the scene before requesting prisoner transport.";
                return LastOperationMessage;
            }

            List<Ped> arrestedSuspects = Dispatch.CurrentArrestedSuspects.ToList();
            Ped player = Game.Player.Character;
            Ped pickupPrisoner = player != null && player.Exists()
                ? arrestedSuspects.OrderBy(suspect =>
                    suspect.Position.DistanceTo(player.Position)).FirstOrDefault()
                : arrestedSuspects.FirstOrDefault();
            if (arrestedSuspects.Count == 0 || pickupPrisoner == null)
            {
                LastOperationMessage = "No living secured Dispatch suspect is ready for transport.";
                return LastOperationMessage;
            }
            Vector3 pickupPosition = pickupPrisoner != null && pickupPrisoner.Exists()
                ? pickupPrisoner.Position
                : Dispatch.Current == null
                    ? Game.Player.Character.Position
                    : Dispatch.Current.Origin;
            List<Ped> livingSuspects = Dispatch.CurrentSuspects
                .Where(suspect => suspect != null && suspect.Exists()
                    && !suspect.IsDead && suspect.Health > 0)
                .ToList();
            HashSet<int> arrestedHandles = new HashSet<int>(
                arrestedSuspects.Select(suspect => suspect.Handle));
            bool awaitingMoreScatteredSuspects = livingSuspects.Any(
                suspect => !arrestedHandles.Contains(suspect.Handle));
            if (scatteredGroupTransport)
            {
                return StartDispatchTransportVan(
                    arrestedSuspects,
                    pickupPosition,
                    true,
                    true,
                    "Dispatcher: A transport van is responding to the secured suspect. Backup will retain the remaining scattered suspects until each is collected.",
                    "ScatteredDispatchCustodyGroup",
                    true,
                    awaitingMoreScatteredSuspects,
                    livingSuspects.Count);
            }

            Vehicle playerVehicle = player == null ? null : player.CurrentVehicle;
            bool usingManagedPreferredUtilityOnFoot = false;
            if ((playerVehicle == null || !playerVehicle.Exists())
                && player != null && player.Exists() && !player.IsInVehicle()
                && _managedPoliceVehicle != null && _managedPoliceVehicle.Exists()
                && _profile != null
                && !string.IsNullOrWhiteSpace(_profile.SelectedVehicleModelName)
                && _managedPoliceVehicle.Model.Hash
                    == ResolveModelHash(_profile.SelectedVehicleModelName))
            {
                // The project already owns the selected parked Police Utility.
                // Reuse that exact entity when the Player is on foot; do not
                // search all streamed vehicles for a same-model car.
                playerVehicle = _managedPoliceVehicle;
                usingManagedPreferredUtilityOnFoot = true;
            }

            if (arrestedSuspects.Count == 1)
            {
                VehicleSeat custodySeat;
                string unavailableReason;
                if (Convoy.CanUseDispatchPreferredPlayerVehicleCustody(
                    player,
                    playerVehicle,
                    pickupPrisoner,
                    out custodySeat,
                    out unavailableReason))
                {
                    _pendingDispatchTransportIncidentId = Dispatch.Current == null
                        ? string.Empty : Dispatch.Current.Id;
                    _pendingDispatchTransportPrisoner = pickupPrisoner;
                    _pendingDispatchTransportVehicle = playerVehicle;
                    Dispatch.ReportTransportAudio(
                        "lsimmersivelife.police.transport.requested",
                        "personal-utility-choice-requested");
                    _log.Runtime(
                        "POLICE_DISPATCH_PERSONAL_UTILITY_CHOICE_OFFERED",
                        "Incident=" + _pendingDispatchTransportIncidentId
                        + "; Prisoner=" + pickupPrisoner.Handle
                        + "; Vehicle=" + playerVehicle.Handle
                        + "; Seat=" + custodySeat
                        + "; Source=" + (usingManagedPreferredUtilityOnFoot
                            ? "ManagedPreferredUtilityOnFoot" : "CurrentPoliceVehicle")
                        + "; DistanceFromPlayer="
                        + player.Position.DistanceTo(playerVehicle.Position).ToString("0.0", CultureInfo.InvariantCulture)
                        + "; DistanceFromSuspect="
                        + pickupPrisoner.Position.DistanceTo(playerVehicle.Position).ToString("0.0", CultureInfo.InvariantCulture));
                    LastOperationMessage = "Your saved Police Utility is within 150 metres and has a free rear seat. Press Y to walk the suspect to it or N to request a transport van, or choose either action in Dispatch Activity.";
                    return LastOperationMessage;
                }

                _log.Runtime(
                    "POLICE_DISPATCH_PREFERRED_UTILITY_UNAVAILABLE",
                    "Incident=" + (Dispatch.Current == null ? string.Empty : Dispatch.Current.Id)
                    + "; Prisoner=" + pickupPrisoner.Handle
                    + "; Reason=" + unavailableReason
                    + "; Fallback=TransportVan");
                return StartDispatchTransportVan(
                    arrestedSuspects,
                    pickupPosition,
                    true,
                    true,
                    "Dispatcher: Your saved Police Utility cannot be used here. A transport van is responding. The suspect remains in custody.",
                    "PreferredUtilityUnavailable:" + unavailableReason);
            }

            return StartDispatchTransportVan(
                arrestedSuspects,
                pickupPosition,
                true,
                true,
                "Dispatcher: A transport van is being sent for the secured group. All suspects remain in custody.",
                "DispatchCustodyGroup");
        }

        internal string ApproveDispatchTransportAction()
        {
            return HasPendingPreferredUtilityTransportChoice
                ? AcceptPreferredUtilityTransport()
                : RequestPrisonerTransport();
        }

        internal string DeclineDispatchTransportAction()
        {
            return HasPendingPreferredUtilityTransportChoice
                ? RequestTransportVanForCurrentDispatch()
                : DeclinePrisonerTransport();
        }

        private string AcceptPreferredUtilityTransport()
        {
            if (!HasPendingPreferredUtilityTransportChoice)
                return "There is no saved Police Utility choice waiting.";

            Ped prisoner = _pendingDispatchTransportPrisoner;
            Vehicle vehicle = _pendingDispatchTransportVehicle;
            string incidentId = _pendingDispatchTransportIncidentId;
            List<Ped> arrestedSuspects = Dispatch.CurrentArrestedSuspects.ToList();
            if (arrestedSuspects.Count != 1 || arrestedSuspects[0].Handle != prisoner.Handle)
            {
                ClearPendingPreferredUtilityTransportChoice();
                return "Dispatch custody changed. Request transport again for the secured suspect.";
            }

            VehicleSeat custodySeat;
            string unavailableReason;
            if (!Convoy.CanUseDispatchPreferredPlayerVehicleCustody(
                Game.Player.Character,
                vehicle,
                prisoner,
                out custodySeat,
                out unavailableReason))
            {
                _log.Runtime(
                    "POLICE_DISPATCH_PREFERRED_UTILITY_BECAME_UNAVAILABLE",
                    "Incident=" + incidentId + "; Prisoner=" + prisoner.Handle
                    + "; Reason=" + unavailableReason + "; Fallback=TransportVan");
                return StartDispatchTransportVan(
                    arrestedSuspects,
                    prisoner.Position,
                    false,
                    true,
                    "Dispatcher: Your Police Utility can no longer be used. A transport van is responding. The suspect remains in custody.",
                    "PreferredUtilityBecameUnavailable:" + unavailableReason);
            }

            string custody = Convoy.StartDispatchPreferredPlayerVehicleCustody(
                prisoner,
                prisoner.Position,
                vehicle);
            if (!Convoy.Active)
            {
                VehicleSeat retrySeat;
                string retryReason;
                if (!Convoy.CanUseDispatchPreferredPlayerVehicleCustody(
                    Game.Player.Character,
                    vehicle,
                    prisoner,
                    out retrySeat,
                    out retryReason))
                {
                    return StartDispatchTransportVan(
                        arrestedSuspects,
                        prisoner.Position,
                        false,
                        true,
                        "Dispatcher: Your Police Utility can no longer be used. A transport van is responding. The suspect remains in custody.",
                        "PreferredUtilityBecameUnavailable:" + retryReason);
                }
                LastOperationMessage = custody;
                return custody;
            }

            if (!TransferDispatchCustodyToConvoy())
            {
                Convoy.Reset();
                ClearPendingPreferredUtilityTransportChoice();
                LastOperationMessage = "Prisoner custody could not be handed to Convoy safely.";
                return LastOperationMessage;
            }

            Dispatch.SetAwaitingTransport();
            Dispatch.ReportTransportAudio(
                "lsimmersivelife.police.transport.approved",
                "personal-utility-approved");
            Backup.BeginStandDown("Dispatch suspect entered personal Utility Convoy custody.");
            ClearPendingPreferredUtilityTransportChoice();
            LastOperationMessage = custody;
            return custody;
        }

        private string RequestTransportVanForCurrentDispatch()
        {
            if (!HasPendingPreferredUtilityTransportChoice)
                return "There is no personal Utility choice waiting.";

            Ped prisoner = _pendingDispatchTransportPrisoner;
            List<Ped> arrestedSuspects = Dispatch.CurrentArrestedSuspects.ToList();
            if (arrestedSuspects.Count != 1 || prisoner == null
                || !prisoner.Exists() || arrestedSuspects[0].Handle != prisoner.Handle)
            {
                ClearPendingPreferredUtilityTransportChoice();
                return "Dispatch custody changed. Request transport again for the secured suspect.";
            }

            return StartDispatchTransportVan(
                arrestedSuspects,
                prisoner.Position,
                false,
                false,
                "Dispatcher: A transport van is responding. The suspect remains in custody.",
                "PlayerRequestedTransportVan");
        }

        private bool TransferDispatchCustodyToConvoy()
        {
            IList<LSPDSuspectInjuryState> injuryStates =
                Dispatch.CreateCustodyInjuryHandoff();
            if (injuryStates == null || injuryStates.Count == 0
                || !Convoy.AdoptDispatchCustodyInjuryStates(injuryStates))
                return false;

            if (!Dispatch.TransferCustodyOwnershipToConvoy(injuryStates))
            {
                Convoy.DiscardUncommittedDispatchInjuryHandoff();
                return false;
            }

            Convoy.ConfirmDispatchInjuryHandoff();
            return true;
        }

        private bool CanRequestScatteredDispatchGroupTransport()
        {
            if (!Dispatch.HasIncident || !Dispatch.HasSuspectGroup
                || Convoy.Active)
                return false;
            List<Ped> living = Dispatch.CurrentSuspects
                .Where(suspect => suspect != null && suspect.Exists()
                    && !suspect.IsDead && suspect.Health > 0)
                .ToList();
            if (living.Count < 2 || !Dispatch.CurrentArrestedSuspects.Any())
                return false;
            Vector3 incidentOrigin = Dispatch.Current == null
                ? Vector3.Zero : Dispatch.Current.Origin;
            return Convoy.HasScatteredDispatchGroup(living, incidentOrigin);
        }

        private void ProcessScatteredDispatchCustodyHandoffs()
        {
            if (!Convoy.DispatchGroupPlayerCollectionActive
                || !Dispatch.HasIncident || Dispatch.Current == null)
                return;

            List<Ped> livingSuspects = Dispatch.CurrentSuspects
                .Where(suspect => suspect != null && suspect.Exists()
                    && !suspect.IsDead && suspect.Health > 0)
                .ToList();
            List<Ped> securedSuspects = Dispatch.CurrentArrestedSuspects
                .Where(suspect => suspect != null && suspect.Exists()
                    && !suspect.IsDead && suspect.Health > 0)
                .ToList();
            HashSet<int> securedHandles = new HashSet<int>(
                securedSuspects.Select(suspect => suspect.Handle));
            bool everyLivingSuspectSecured = livingSuspects.All(
                suspect => securedHandles.Contains(suspect.Handle));

            Convoy.UpdateDispatchGroupPickupManifest(
                securedSuspects,
                everyLivingSuspectSecured,
                livingSuspects.Count);

            foreach (int handle in Convoy.PendingDispatchGroupLoadedHandles)
            {
                Ped prisoner = securedSuspects.FirstOrDefault(
                    suspect => suspect.Handle == handle);
                if (prisoner == null)
                {
                    if (_dispatchGroupHandoffFailureLogged.Add(handle))
                        _log.Runtime("POLICE_CONVOY_DISPATCH_GROUP_HANDOFF_PENDING",
                            "Suspect=" + handle
                            + "; Reason=LoadedSeatHasNoCurrentDispatchCustodyReference"
                            + "; CustodyTransfer=false; PhysicalSeatConfirmed=true");
                    continue;
                }

                IList<LSPDSuspectInjuryState> injuryStates =
                    Dispatch.CreateCustodyInjuryHandoff(prisoner);
                if (injuryStates == null || injuryStates.Count == 0
                    || !Convoy.AdoptDispatchCustodyInjuryStates(injuryStates))
                {
                    if (_dispatchGroupHandoffFailureLogged.Add(handle))
                        _log.Runtime("POLICE_CONVOY_DISPATCH_GROUP_HANDOFF_PENDING",
                            "Suspect=" + handle
                            + "; Reason=InjuryStateAdoptionFailed"
                            + "; CustodyTransfer=false; PhysicalSeatConfirmed=true");
                    continue;
                }

                if (!Dispatch.TransferCustodyOwnershipToConvoy(
                    prisoner, injuryStates))
                {
                    Convoy.DiscardUncommittedDispatchInjuryHandoff(
                        new[] { handle });
                    if (_dispatchGroupHandoffFailureLogged.Add(handle))
                        _log.Runtime("POLICE_CONVOY_DISPATCH_GROUP_HANDOFF_PENDING",
                            "Suspect=" + handle
                            + "; Reason=DispatchOwnershipTransferRejected"
                            + "; CustodyTransfer=false; PhysicalSeatConfirmed=true");
                    continue;
                }

                Convoy.ConfirmDispatchInjuryHandoff(new[] { handle });
                Convoy.ConfirmDispatchGroupLoadedOwnership(new[] { handle });
                Backup.CompleteDispatchCustodyEscortHandoff(prisoner);
                _dispatchGroupHandoffFailureLogged.Remove(handle);
                _log.Runtime("POLICE_CONVOY_DISPATCH_GROUP_CUSTODY_HANDOFF_CONFIRMED",
                    "Incident=" + Dispatch.Current.Id
                    + "; Suspect=" + handle
                    + "; PhysicalSeatConfirmed=true"
                    + "; BackupStandDown=false"
                    + "; DispatchOwned=" + !Dispatch.HasConvoyCustodyHandoff);
            }

            string incidentId = Dispatch.Current.Id ?? string.Empty;
            bool allLivingCustodyPhysicallyTransferred = livingSuspects.Count > 0
                && Convoy.HasTransferredDispatchGroupCustody(
                    livingSuspects.Select(suspect => suspect.Handle));
            if (allLivingCustodyPhysicallyTransferred
                && !string.Equals(_dispatchGroupFinalHandoffIncidentId,
                    incidentId, StringComparison.Ordinal))
            {
                _dispatchGroupFinalHandoffIncidentId = incidentId;
                Dispatch.SetAwaitingTransport();
                Dispatch.ReportTransportAudio(
                    "lsimmersivelife.police.transport.unit_assigned",
                    "scattered-group-transport-loaded");
                Backup.BeginStandDown(
                    "Every living Dispatch suspect is physically aboard the transport.");
                _log.Runtime("POLICE_CONVOY_DISPATCH_GROUP_ALL_CUSTODY_TRANSFERRED",
                    "Incident=" + incidentId
                    + "; LivingSuspects=" + livingSuspects.Count
                    + "; ConvoyTransferred=" + securedHandles.Count
                    + "; BackupStandDownAfterPhysicalSeatConfirmation=true");
            }
        }

        private void PrepareScatteredDispatchBackupCustodyEscorts()
        {
            if (!Convoy.DispatchGroupPlayerCollectionActive
                || !Dispatch.HasIncident)
            {
                if (Dispatch.HasIncident)
                    foreach (Ped suspect in Dispatch.CurrentSuspects)
                        Backup.ResumeDispatchCustodyEscort(suspect);
                return;
            }

            foreach (Ped suspect in Dispatch.CurrentArrestedSuspects)
            {
                if (suspect == null || !suspect.Exists() || suspect.IsDead
                    || !Convoy.IsRemoteDispatchGroupPickupFor(suspect.Handle))
                    continue;
                Ped officer = Backup.DelegateDispatchCustodyEscortToConvoy(suspect);
                if (officer != null)
                    Convoy.AssignDispatchGroupBackupEscort(suspect, officer);
            }
        }

        private string StartDispatchTransportVan(
            IList<Ped> arrestedSuspects,
            Vector3 pickupPosition,
            bool reportTransportRequest,
            bool clearChoiceBeforeStart,
            string dispatcherMessage,
            string reason,
            bool scatteredGroup = false,
            bool awaitingMoreGroupSuspects = false,
            int expectedLivingSuspectCount = 0)
        {
            if (clearChoiceBeforeStart)
                ClearPendingPreferredUtilityTransportChoice();

            string custody = Convoy.StartDispatchGroupTransport(
                arrestedSuspects,
                pickupPosition,
                Dispatch.Current == null ? Vector3.Zero : Dispatch.Current.Origin,
                reportTransportRequest,
                awaitingMoreGroupSuspects,
                expectedLivingSuspectCount,
                scatteredGroup);
            if (!Convoy.Active)
            {
                LastOperationMessage = custody;
                return custody;
            }

            bool playerDrivenGroupCollection =
                Convoy.DispatchGroupPlayerCollectionActive;
            if (!playerDrivenGroupCollection
                && !TransferDispatchCustodyToConvoy())
            {
                Convoy.Reset();
                ClearPendingPreferredUtilityTransportChoice();
                LastOperationMessage = "Prisoner custody could not be handed to Convoy safely.";
                return LastOperationMessage;
            }

            if (playerDrivenGroupCollection)
            {
                _dispatchGroupFinalHandoffIncidentId = string.Empty;
                _dispatchGroupHandoffFailureLogged.Clear();
            }
            else
            {
                Dispatch.SetAwaitingTransport();
            }
            Dispatch.ReportTransportAudio(
                "lsimmersivelife.police.transport.unit_assigned",
                "transport-van-assigned-" + reason);
            if (!playerDrivenGroupCollection)
                Backup.BeginStandDown("Dispatch suspects entered Convoy van custody.");
            ClearPendingPreferredUtilityTransportChoice();
            LastOperationMessage = string.IsNullOrWhiteSpace(dispatcherMessage)
                ? custody
                : dispatcherMessage + " " + custody;
            return LastOperationMessage;
        }

        private string ProcessPendingPreferredUtilityTransportChoice()
        {
            if (string.IsNullOrWhiteSpace(_pendingDispatchTransportIncidentId))
                return string.Empty;

            LSPDDispatchEvent incident = Dispatch.Current;
            if (incident == null
                || Dispatch.State != LSPDDispatchState.Arrested
                || !string.Equals(
                    incident.Id,
                    _pendingDispatchTransportIncidentId,
                    StringComparison.Ordinal)
                || Convoy.Active)
            {
                ClearPendingPreferredUtilityTransportChoice();
                return string.Empty;
            }

            if (!_config.Police.Convoy.Enabled)
            {
                ClearPendingPreferredUtilityTransportChoice();
                return "Prisoner transport was disabled in settings. The secured suspect remains under Dispatch custody.";
            }

            List<Ped> arrestedSuspects = Dispatch.CurrentArrestedSuspects.ToList();
            if (arrestedSuspects.Count > 1)
            {
                ClearPendingPreferredUtilityTransportChoice();
                return StartDispatchTransportVan(
                    arrestedSuspects,
                    arrestedSuspects[0].Position,
                    false,
                    true,
                    "Dispatcher: A transport van is being sent for the secured group. All suspects remain in custody.",
                    "DispatchCustodyGroup");
            }
            if (arrestedSuspects.Count != 1 || _pendingDispatchTransportPrisoner == null
                || !arrestedSuspects[0].Exists()
                || arrestedSuspects[0].Handle != _pendingDispatchTransportPrisoner.Handle)
            {
                ClearPendingPreferredUtilityTransportChoice();
                return string.Empty;
            }

            VehicleSeat custodySeat;
            string unavailableReason;
            if (Convoy.CanUseDispatchPreferredPlayerVehicleCustody(
                Game.Player.Character,
                _pendingDispatchTransportVehicle,
                _pendingDispatchTransportPrisoner,
                out custodySeat,
                out unavailableReason))
                return string.Empty;

            _log.Runtime(
                "POLICE_DISPATCH_PREFERRED_UTILITY_BECAME_UNAVAILABLE",
                "Incident=" + incident.Id
                + "; Prisoner=" + _pendingDispatchTransportPrisoner.Handle
                + "; Reason=" + unavailableReason
                + "; Fallback=TransportVan");
            return StartDispatchTransportVan(
                arrestedSuspects,
                _pendingDispatchTransportPrisoner.Position,
                false,
                true,
                "Dispatcher: Your Police Utility can no longer be used. A transport van is responding. The suspect remains in custody.",
                "PreferredUtilityBecameUnavailable:" + unavailableReason);
        }

        private bool IsPendingPreferredUtilityTransportChoiceCurrent()
        {
            if (string.IsNullOrWhiteSpace(_pendingDispatchTransportIncidentId)
                || _pendingDispatchTransportPrisoner == null
                || !_pendingDispatchTransportPrisoner.Exists()
                || _pendingDispatchTransportPrisoner.IsDead
                || Dispatch.Current == null
                || Dispatch.State != LSPDDispatchState.Arrested
                || !string.Equals(
                    Dispatch.Current.Id,
                    _pendingDispatchTransportIncidentId,
                    StringComparison.Ordinal)
                || Convoy.Active)
                return false;

            List<Ped> arrestedSuspects = Dispatch.CurrentArrestedSuspects.ToList();
            return arrestedSuspects.Count == 1
                && arrestedSuspects[0].Handle == _pendingDispatchTransportPrisoner.Handle;
        }

        private void ClearPendingPreferredUtilityTransportChoice()
        {
            _pendingDispatchTransportIncidentId = string.Empty;
            _pendingDispatchTransportPrisoner = null;
            _pendingDispatchTransportVehicle = null;
        }

        /// <summary>
        /// Starts the player-selected Convoy activity without fabricating a
        /// Dispatch incident. Dispatch only supplies an existing criminal
        /// profile; Convoy owns the staged prisoner, transport, road threat,
        /// prison handoff, and terminal cleanup from this point onward.
        /// </summary>
        internal string RequestConvoyActivity()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before requesting a Convoy activity.";
                return LastOperationMessage;
            }
            if (!_config.Police.Convoy.Enabled || !_config.Police.Convoy.RequestedConvoyEnabled)
            {
                LastOperationMessage = "Player Convoy Request is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            if (Dispatch.HasIncident)
            {
                LastOperationMessage = "Finish or cancel the active Dispatch assignment before requesting a Convoy.";
                return LastOperationMessage;
            }
            if (Convoy.Active || (Backup.BlocksPlayerActivities
                && Backup.State != LSPDBackupAssignmentState.StandingDown))
            {
                LastOperationMessage = "A Police custody or backup assignment is already active.";
                return LastOperationMessage;
            }
            if (_gangResponse.HasActiveIncident)
            {
                LastOperationMessage = "Resolve the active gang threat before requesting a Convoy.";
                return LastOperationMessage;
            }
            if (CrimeActivity.BlocksOtherPoliceActivities)
            {
                LastOperationMessage = "Resolve or close the active Crime Activity before requesting a Convoy.";
                return LastOperationMessage;
            }
            if (NpcResponse.HasActiveInteraction)
            {
                LastOperationMessage = "Finish the active NPC interaction before requesting a Convoy.";
                return LastOperationMessage;
            }

            LSPDCriminalProfileDefinition prisonerProfile;
            LSPDCriminalProfileDefinition routeThreatProfile;
            if (!Dispatch.TryChooseConvoyRequestProfiles(out prisonerProfile, out routeThreatProfile))
            {
                LastOperationMessage = "Convoy Request needs at least one usable criminal profile in LSPDCriminalProfile.xml.";
                return LastOperationMessage;
            }

            string result = Convoy.StartRequestedConvoy(prisonerProfile, routeThreatProfile);
            if (Convoy.Active)
            {
                Dispatch.DeferOffersForContextActivity("Convoy Request");
                _log.Runtime(
                    "POLICE_CONVOY_REQUESTED",
                    "PrisonerProfile=" + prisonerProfile.Id
                    + "; RouteThreatProfile=" + (routeThreatProfile == null ? "none" : routeThreatProfile.Id));
            }
            LastOperationMessage = result;
            return result;
        }

        internal string DeclinePrisonerTransport()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Convoy.HoldingAtStation)
            {
                LastOperationMessage = "No prisoner is waiting at the selected Police station.";
                return LastOperationMessage;
            }

            string result = Convoy.DeclineAtStation();
            if (Convoy.Completed && Dispatch.HasConvoyCustodyHandoff)
            {
                result = Dispatch.CompleteTransport();
                Response.ReleaseAll();
                Backup.BeginStandDown("Custody was completed at the station.");
            }
            if (Convoy.Completed)
                Convoy.ConsumeCompletedState();
            LastOperationMessage = result;
            return result;
        }

        internal string CompletePrisonerTransport()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!_config.Police.Convoy.TerminalCompletionEnabled)
            {
                LastOperationMessage = "Terminal transport completion is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            string result = Convoy.CompleteNow();
            if (Convoy.Completed && Dispatch.HasConvoyCustodyHandoff)
            {
                result = Dispatch.CompleteTransport();
                Response.ReleaseAll();
                Backup.BeginStandDown("Custody transport was completed by the recovery control.");
            }
            else if (Convoy.Completed && Convoy.IsRequestedConvoyActivity)
            {
                result = "Convoy Request completed by the officer recovery control.";
                Response.ReleaseAll();
                Backup.BeginStandDown("Requested Convoy completed by the recovery control.");
                Dispatch.DeferOffersForContextActivity("Convoy Request");
                _log.Runtime("POLICE_CONVOY_REQUEST_TERMINAL_COMPLETION", "Officer used terminal completion.");
            }
            if (Convoy.Completed)
                Convoy.ConsumeCompletedState();
            LastOperationMessage = result;
            return result;
        }

        internal string CallBackup()
        {
            return CallBackup(true);
        }

        internal string CallDefaultBackup()
        {
            return CallBackup(false);
        }

        internal string CallFavoriteBackup()
        {
            return CallBackup(true);
        }

        internal string RequestNpcBackupAssistance()
        {
            if (!RequireActive())
                return LastOperationMessage;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                LastOperationMessage = "The player character is unavailable.";
                return LastOperationMessage;
            }

            if (!NpcResponse.CanRequestBackup)
            {
                LastOperationMessage = NpcResponse.RequestBackupSupportCommand();
                return LastOperationMessage;
            }

            LastOperationMessage = StartNpcBackupRequest(player, false);
            return LastOperationMessage;
        }

        internal string RequestNpcBackupTransport()
        {
            if (!RequireActive())
                return LastOperationMessage;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                LastOperationMessage = "The player character is unavailable.";
                return LastOperationMessage;
            }

            if (NpcResponse.HasBackupAssignment)
            {
                if (NpcResponse.CanBeginBackupTransportHandoff)
                {
                    if (Backup.RequestNpcCustodyTransport(NpcResponse))
                    {
                        LastOperationMessage = "Backup custody transport accepted for this same handcuffed Citizen. Keep the Citizen at the scene until the assigned unit physically arrives.";
                        return LastOperationMessage;
                    }

                    LastOperationMessage = "The current Backup unit is not confirmed for this Citizen's custody transfer. The Citizen remains secured; request transport again when the assigned operation is available.";
                    return LastOperationMessage;
                }

                LastOperationMessage = NpcResponse.RequestBackupTransportCommand(player);
                return LastOperationMessage;
            }

            if (!NpcResponse.CanRequestBackupTransport)
            {
                LastOperationMessage = NpcResponse.RequestBackupTransportCommand(player);
                return LastOperationMessage;
            }

            LastOperationMessage = StartNpcBackupRequest(player, true);
            return LastOperationMessage;
        }

        internal string RequestNpcPersonalVehicleTransport()
        {
            if (!RequireActive())
                return LastOperationMessage;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                LastOperationMessage = "The player character is unavailable.";
                return LastOperationMessage;
            }

            if (!NpcResponse.CanBeginPlayerVehicleCustody)
            {
                LastOperationMessage = "Personal Police Utility transport is available after the same Citizen has been physically handcuffed. The current Citizen remains in its documented interaction branch.";
                return LastOperationMessage;
            }

            string custody = TryStartNpcPlayerVehicleCustody(player);
            LastOperationMessage = !string.IsNullOrWhiteSpace(custody)
                ? custody
                : "Personal Police Utility transport is unavailable or too far from the scene. The Citizen remains secured; choose Request a Back Up For Citizen Transport to continue custody.";
            return LastOperationMessage;
        }

        private string StartNpcBackupRequest(Ped player, bool custodyTransportRequest)
        {
            string backupResult = Backup.RequestNpcSupport(
                NpcResponse,
                player,
                true,
                custodyTransportRequest);
            if (!Backup.HasNpcAssignmentFor(NpcResponse, custodyTransportRequest))
                return backupResult;

            string handoff = NpcResponse.RequestBackupSupportCommand();
            return custodyTransportRequest
                ? "Citizen transport escort requested. " + handoff
                : handoff + " " + backupResult;
        }

        private string TryStartNpcPlayerVehicleCustody(Ped player)
        {
            if (!NpcResponse.CanBeginPlayerVehicleCustody)
                return null;

            Vehicle vehicle = FindNpcPlayerCustodyVehicle(player);
            if (vehicle == null)
                return null;

            Ped prisoner = NpcResponse.ActiveSubject;
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                return "The compliant citizen is no longer available for personal custody.";

            string custody = Convoy.StartPlayerVehicleCustodyFromNpc(
                prisoner,
                prisoner.Position,
                vehicle);
            if (!Convoy.Active)
                return custody;

            if (!NpcResponse.TransferArrestedSubjectToConvoy())
            {
                Convoy.Reset();
                _log.StateFailure(
                    "NPC_CUSTODY_TRANSFER_TO_CONVOY_FAILED",
                    "Convoy started, but the arrested NPC owner could not release custody safely. Ped="
                    + prisoner.Handle);
                return "The arrested citizen could not be transferred safely to Convoy custody.";
            }

            Backup.BeginStandDown("NPC citizen custody transferred to player Convoy.");
            _log.Runtime(
                "NPC_PLAYER_VEHICLE_CUSTODY_STARTED",
                "Ped=" + prisoner.Handle + "; Vehicle=" + vehicle.Handle
                + "; Model=" + vehicle.Model.Hash + "; Motorcycle=false");
            return custody;
        }

        private Vehicle FindNpcPlayerCustodyVehicle(Ped player)
        {
            if (player == null || !player.Exists())
                return null;
            if (player.IsInVehicle())
                return null;

            Vehicle current = player.CurrentVehicle;
            if (current != null && current.Exists()
                && IsKnownPoliceVehicle(current)
                && !current.Model.IsBike
                && current.Position.DistanceTo(player.Position) <= 75.0f)
            {
                if (Convoy.HasRearCustodySeat(current))
                    return current;
                _log.Runtime(
                    "NPC_PLAYER_CUSTODY_VEHICLE_REJECTED",
                    "Vehicle=" + current.Handle + "; Model=" + current.Model.Hash
                    + "; Reason=NO_REAR_CUSTODY_SEAT");
            }

            Vehicle managed = _managedPoliceVehicle;
            if (managed == null || !managed.Exists()
                || !IsKnownPoliceVehicle(managed)
                || managed.Model.IsBike)
                return null;

            if (!Convoy.HasRearCustodySeat(managed))
            {
                _log.Runtime(
                    "NPC_PLAYER_CUSTODY_VEHICLE_REJECTED",
                    "Vehicle=" + managed.Handle + "; Model=" + managed.Model.Hash
                    + "; Reason=NO_REAR_CUSTODY_SEAT");
                return null;
            }

            // The player has already left the patrol car to make the arrest.
            // Keep personal custody available only while that same managed
            // vehicle remains a practical walkable distance from the scene.
            return managed.Position.DistanceTo(player.Position) <= 75.0f
                ? managed : null;
        }

        private string CallBackup(bool preferSavedFavorite)
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Dispatch.HasIncident && !Convoy.Active
                && !CrimeActivity.BlocksOtherPoliceActivities
                && !_gangResponse.HasActiveIncident
                && !NpcResponse.CanRequestBackup
                && !NpcResponse.HasBackupAssignment)
            {
                LastOperationMessage = "Backup is available after a Dispatch assignment, Crime Activity, Convoy, gang threat, or detained/fleeing NPC contact is active.";
                return LastOperationMessage;
            }
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                LastOperationMessage = "The player character is unavailable.";
                return LastOperationMessage;
            }

            if (!_config.Police.Backup.Enabled)
            {
                LastOperationMessage = "Backup is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }

            if (Dispatch.HasIncident)
            {
                LastOperationMessage = Backup.Request(Dispatch, player, preferSavedFavorite);
                return LastOperationMessage;
            }

            if (Convoy.Active)
            {
                LastOperationMessage = Backup.RequestConvoySupport(Convoy, player, preferSavedFavorite);
                return LastOperationMessage;
            }

            if (CrimeActivity.BlocksOtherPoliceActivities)
            {
                // Keep the Crime Activity in its normal investigation state
                // until the separate Backup owner has actually accepted the
                // assignment. A failed request must not leave the scene
                // falsely marked as awaiting support.
                LastOperationMessage = Backup.RequestCrimeActivitySupport(
                    CrimeActivity, player, preferSavedFavorite);
                if (!LastOperationMessage.StartsWith("Backup requested", StringComparison.OrdinalIgnoreCase)
                    && !LastOperationMessage.StartsWith("Existing backup", StringComparison.OrdinalIgnoreCase)
                    && !LastOperationMessage.StartsWith("Backup is already", StringComparison.OrdinalIgnoreCase))
                    return LastOperationMessage;
                string supportState = CrimeActivity.RequestBackupSupport();
                if (!string.IsNullOrWhiteSpace(supportState))
                    LastOperationMessage = supportState + " " + LastOperationMessage;
                return LastOperationMessage;
            }

            if (NpcResponse.CanRequestBackup)
            {
                LastOperationMessage = StartNpcBackupRequest(player, false);
                return LastOperationMessage;
            }

            if (NpcResponse.HasBackupAssignment)
            {
                LastOperationMessage = "Backup is already assigned to the active NPC contact.";
                return LastOperationMessage;
            }

            // Gang Threats now use the same bounded, road-safe Backup owner as
            // the other player-requested support paths. The Gang Adapter still
            // owns the hostile actors; LSPDBackUp owns only its Police units
            // and their combat tasks.
            Response.ReleaseAll();
            LastOperationMessage = Backup.RequestGangSupport(
                _gangResponse,
                player,
                preferSavedFavorite);
            return LastOperationMessage;
        }

        internal string CancelActiveDispatch()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (CrimeActivity.BlocksOtherPoliceActivities
                && !Dispatch.HasIncident && !Convoy.Active)
            {
                Backup.BeginStandDown("Crime Activity cancelled by officer recovery control.");
                LastOperationMessage = CrimeActivity.IgnoreCurrentActivity(Game.Player.Character);
                Response.ReleaseAll();
                Dispatch.DeferOffersForContextActivity("Crime Activity cancellation");
                _log.Runtime("POLICE_CRIME_ACTIVITY_CANCELLED", LastOperationMessage);
                return LastOperationMessage;
            }
            if (Convoy.Active && Convoy.IsRequestedConvoyActivity && !Dispatch.HasIncident)
            {
                Backup.BeginStandDown("Requested Convoy cancelled by officer recovery control.");
                Convoy.Reset();
                _gangResponse.Reset();
                Response.ReleaseAll();
                Dispatch.DeferOffersForContextActivity("Convoy Request cancellation");
                LastOperationMessage = "Convoy Request cancelled. Owned Convoy state was cleared.";
                _log.Runtime("POLICE_CONVOY_REQUEST_CANCELLED", "Officer cancelled player-requested Convoy activity.");
                return LastOperationMessage;
            }
            Convoy.Reset();
            Backup.Reset();
            _gangResponse.Reset();
            ClearPendingPreferredUtilityTransportChoice();
            string result = Dispatch.Cancel("Cancelled by officer recovery control.");
            Response.ReleaseAll();
            LastOperationMessage = result;
            return result;
        }

        internal string ResetPoliceRuntime()
        {
            if (!RequireActive())
                return LastOperationMessage;
            AmbientWorld.CancelAllInteriorAccessSessions("PoliceRuntimeReset");
            ClearPendingPreferredUtilityTransportChoice();
            Convoy.Reset();
            Dispatch.Reset();
            Response.Reset();
            Backup.Reset();
            _gangResponse.Reset();
            NpcResponse.Reset();
            CrimeActivity.Reset();
            _gangIncidentWasActive = false;
            _crimeActivityWasActive = false;
            _npcInteractionWasActive = false;
            ClearPendingSavedProfileApply();
            LastOperationMessage = "Police runtime recovered. Authority remains active.";
            _log.Runtime("POLICE_RUNTIME_RECOVERED", "Dispatch, Convoy, Backup, Response, Gang, Crime Activity, and NPC-owned state reset.");
            return LastOperationMessage;
        }

        internal string BeginNpcInteraction()
        {
            if (!CanBeginNpcContact())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.BeginInteractionCommand();
            return LastOperationMessage;
        }

        internal string BeginNpcFootInteraction()
        {
            if (!CanBeginNpcContact())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.BeginFootInteractionCommand();
            return LastOperationMessage;
        }

        internal string BeginNpcTrafficInteraction()
        {
            if (!CanBeginNpcContact())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.BeginTrafficInteractionCommand();
            return LastOperationMessage;
        }

        private bool CanBeginNpcContact()
        {
            if (!RequireActive() || !Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before beginning an NPC interaction.";
                return false;
            }
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || CrimeActivity.BlocksOtherPoliceActivities)
            {
                LastOperationMessage = Dispatch.HasIncident
                    ? "NPC contact is paused while a Dispatch scene is active."
                    : Convoy.Active
                        ? "NPC contact is paused while a Convoy activity is active."
                        : _gangResponse.HasActiveIncident
                            ? "NPC contact is paused while a gang threat is active."
                            : "NPC contact is paused while a Crime Activity investigation is active.";
                return false;
            }
            return true;
        }

        internal string AcceptNpcInteraction()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.AcceptInteractionCommand();
            return LastOperationMessage;
        }

        internal string RejectNpcInteraction()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.RejectInteractionCommand();
            return LastOperationMessage;
        }

        internal string RequestNpcDocuments()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.RequestCitizenDocumentsCommand();
            return LastOperationMessage;
        }

        internal string RequestNpcValidation()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.RequestCitizenValidationCommand();
            return LastOperationMessage;
        }

        internal string ConfirmNpcDocumentReview()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.ConfirmCitizenRecordReviewCommand();
            return LastOperationMessage;
        }

        internal string ArrestNpcCitizen()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.ArrestCitizenCommand();
            return LastOperationMessage;
        }

        internal string ReleaseNpcCitizen()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.ReleaseCitizenCommand();
            return LastOperationMessage;
        }

        internal string ReviewNpcFramework()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.ReviewCitizenFrameworkCommand();
            return LastOperationMessage;
        }

        internal bool ObserveCrimeIntelligence()
        {
            if (!RequireActive())
                return false;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before reviewing nearby Crime Activity intelligence.";
                return false;
            }
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || NpcResponse.HasActiveInteraction || CrimeActivity.BlocksOtherPoliceActivities
                || (Backup.BlocksPlayerActivities
                    && Backup.State != LSPDBackupAssignmentState.StandingDown))
            {
                LastOperationMessage = "Nearby Crime Activity observation waits until the current Police activity is clear.";
                return false;
            }
            Ped player = Game.Player.Character;
            int count = CrimeActivity.Observe(player, _gangData);
            LastOperationMessage = count == 0
                ? "No authored criminal activity tip was observed nearby."
                : count + " criminal activity tip(s) are available for review.";
            _log.Runtime(
                "POLICE_CRIME_INTELLIGENCE_OBSERVED",
                "Count=" + count + "; DispatchActive=" + Dispatch.HasIncident
                + "; GangIncidentActive=" + _gangResponse.HasActiveIncident);
            return true;
        }

        internal string RequestCrimeActivity()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before requesting a Crime Activity.";
                return LastOperationMessage;
            }
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || NpcResponse.HasActiveInteraction || CrimeActivity.BlocksOtherPoliceActivities
                || (Backup.BlocksPlayerActivities
                    && Backup.State != LSPDBackupAssignmentState.StandingDown))
            {
                LastOperationMessage = "Crime Activity request waits until the current Police activity is clear.";
                return LastOperationMessage;
            }

            LastOperationMessage = CrimeActivity.RequestNearbyActivity(
                Game.Player.Character, _gangData);
            _log.Runtime(
                "POLICE_CRIME_ACTIVITY_REQUESTED",
                "Available=" + CrimeActivity.HasAvailableActivity
                + "; Message=" + LastOperationMessage);
            return LastOperationMessage;
        }

        internal string SetCrimeIntelWaypoint(string activityId)
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || NpcResponse.HasActiveInteraction || CrimeActivity.BlocksOtherPoliceActivities)
            {
                LastOperationMessage = "The current Police assignment already owns the active GPS route.";
                return LastOperationMessage;
            }
            LSPDActivityIntel intel;
            if (!CrimeActivity.TryGetIntel(activityId, out intel))
            {
                LastOperationMessage = "Observe nearby crime intelligence before setting a route.";
                return LastOperationMessage;
            }
            if (!_config.ShowWaypoint)
            {
                LastOperationMessage = "Route guidance is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            try
            {
                World.WaypointPosition = intel.Position;
                LastOperationMessage = "GPS set to " + intel.Location.Name + ".";
                _log.Runtime(
                    "POLICE_CRIME_INTELLIGENCE_GPS",
                    "Activity=" + intel.Id + "; Location=" + intel.Location.Id);
            }
            catch (Exception ex)
            {
                LastOperationMessage = "The intelligence location could not be placed on the GPS.";
                _log.Exception("POLICE_CRIME_INTELLIGENCE_GPS_FAILED", ex);
            }
            return LastOperationMessage;
        }

        internal string BeginCrimeActivity(string activityId)
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before investigating Criminal Activity.";
                return LastOperationMessage;
            }
            if (!_config.Police.CrimeActivity.Enabled)
            {
                LastOperationMessage = "Crime Activity is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || NpcResponse.HasActiveInteraction)
            {
                LastOperationMessage = Dispatch.HasIncident
                    ? "Finish or cancel the active Dispatch before investigating Crime Activity."
                    : Convoy.Active
                        ? "Finish or cancel the active Convoy before investigating Crime Activity."
                        : _gangResponse.HasActiveIncident
                            ? "Resolve the active gang threat before investigating Crime Activity."
                            : "Finish the active NPC interaction before investigating Crime Activity.";
                return LastOperationMessage;
            }
            if (Backup.BlocksPlayerActivities
                && Backup.State != LSPDBackupAssignmentState.StandingDown)
            {
                LastOperationMessage = "A previous Backup assignment must stand down before a Crime Activity scene starts.";
                return LastOperationMessage;
            }

            Ped player = Game.Player.Character;
            string result = CrimeActivity.BeginInvestigation(player, activityId);
            if (CrimeActivity.Active)
            {
                Dispatch.DeferOffersForContextActivity("Crime Activity investigation");
                _log.Runtime("POLICE_CRIME_ACTIVITY_PLAYER_STARTED",
                    "Activity=" + (CrimeActivity.CurrentIntel == null ? string.Empty : CrimeActivity.CurrentIntel.Id));
            }
            else if (result.StartsWith("Travel within", StringComparison.OrdinalIgnoreCase))
            {
                // A player-selected lead is not an active assignment yet. Set
                // GPS only as a convenience and leave other patrol layers free.
                string route = SetCrimeIntelWaypoint(activityId);
                if (route.StartsWith("GPS set", StringComparison.OrdinalIgnoreCase))
                    result += " " + route;
            }
            LastOperationMessage = result;
            return result;
        }

        internal string ConfrontCrimeActivity()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = CrimeActivity.Confront(Game.Player.Character);
            return LastOperationMessage;
        }

        internal string IgnoreCrimeActivity()
        {
            if (!RequireActive())
                return LastOperationMessage;
            bool wasActive = CrimeActivity.BlocksOtherPoliceActivities;
            LastOperationMessage = CrimeActivity.IgnoreCurrentActivity(Game.Player.Character);
            if (wasActive)
            {
                Backup.BeginStandDown("Crime Activity closed by the officer.");
                Response.ReleaseAll();
                Dispatch.DeferOffersForContextActivity("Crime Activity");
            }
            return LastOperationMessage;
        }

        internal bool ReloadCrimeIntelligence()
        {
            if (!RequireActive())
                return false;
            if (CrimeActivity.BlocksOtherPoliceActivities)
                return Fail("CRIME_INTELLIGENCE_RELOAD_BLOCKED",
                    "Finish or close the active Crime Activity before reloading its XML catalog.");
            if (!CrimeActivity.Reload())
                return Fail("CRIME_INTELLIGENCE_LOAD_FAILED", CrimeActivity.LastLoadError);
            CrimeActivity.ClearObservations();
            return Succeed("CRIME_INTELLIGENCE_LOADED",
                "Crime intelligence loaded: " + CrimeActivity.ActivityCount + " activities.");
        }

        internal bool ReloadGangIntelligence()
        {
            if (!RequireActive())
                return false;
            bool loaded = _gangData.Reload();
            if (!loaded && !string.IsNullOrWhiteSpace(_gangData.LastLoadError))
                return Fail("POLICE_GANG_DATA_LOAD_FAILED", _gangData.LastLoadError);
            LastOperationMessage = loaded
                ? "Read-only Gang & Turf intelligence loaded from the installed export or Plugin fallback."
                : "No optional Gang & Turf export was found in gangModData or the Police Plugin folder.";
            _log.Runtime(
                "POLICE_GANG_INTELLIGENCE_STATUS",
                "Loaded=" + loaded + "; Gangs=" + _gangData.Gangs.Count()
                + "; TurfZones=" + _gangData.TurfZones.Count()
                + "; MemberModels=" + _gangData.MemberModelCount
                + "; VehicleModels=" + _gangData.VehicleModelCount);
            return true;
        }

        internal bool SaveCurrentPersonalLoadout()
        {
            if (!RequireActive())
                return false;
            if (_profile.PersonalWeapons.Any())
                return Fail(
                    "PERSONAL_LOADOUT_REWRITE_REQUIRED",
                    "A personal weapon collection already exists. Use Rewrite the Weapon Loadout to replace it.");
            return CaptureCurrentPersonalLoadout();
        }

        internal bool RewritePersonalLoadout()
        {
            if (!RequireActive())
                return false;
            if (!_profile.PersonalWeapons.Any())
                return Fail(
                    "PERSONAL_LOADOUT_REWRITE_EMPTY",
                    "There is no saved collection to rewrite. Use Save the Current Loadout first.");
            return CaptureCurrentPersonalLoadout();
        }

        private bool CaptureCurrentPersonalLoadout()
        {
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return Fail("POLICE_PLAYER_UNAVAILABLE", "The player character is unavailable.");
                var weapons = new List<LSPDPoliceFavoriteWeapon>();
                string activeId = null;
                Weapon current = player.Weapons.Current;
                foreach (WeaponHash hash in player.Weapons.GetAllWeaponHashes())
                {
                    Weapon weapon = player.Weapons[hash];
                    if (hash == WeaponHash.Unarmed || weapon == null || !weapon.IsPresent)
                        continue;
                    LSPDPoliceWeaponDefinition library = _profile.Weapons.FirstOrDefault(
                        item => ResolveWeaponHash(item.WeaponName) == unchecked((int)hash));
                    string identifier = library == null
                        ? "0x" + unchecked((uint)hash).ToString("X8", CultureInfo.InvariantCulture)
                        : library.WeaponName;
                    var components = new List<string>();
                    foreach (WeaponComponent component in weapon.Components)
                        if (component.Active)
                            components.Add("0x" + unchecked((uint)component.ComponentHash).ToString("X8", CultureInfo.InvariantCulture));
                    var saved = LSPDPoliceFavoriteWeapon.FromCaptured(
                        library == null ? "personal-" + identifier : library.Id,
                        library == null ? hash.ToString() : library.DisplayName,
                        identifier, weapon.Ammo, (int)weapon.Tint,
                        components);
                    weapons.Add(saved);
                    if (current != null && current.Hash == hash)
                        activeId = saved.Id;
                }
                if (weapons.Count == 0)
                    return Fail("PERSONAL_LOADOUT_EMPTY", "No carried weapons were found; the saved collection was not changed.");
                if (!_profile.SaveCapturedLoadout(weapons, activeId))
                    return Fail("PERSONAL_LOADOUT_SAVE_FAILED", "The personal weapon collection could not be saved.");
                return Succeed("PERSONAL_LOADOUT_SAVED", "Personal weapon collection saved: " + weapons.Count + " weapons.");
            }
            catch (Exception ex)
            {
                return Fail("PERSONAL_LOADOUT_CAPTURE_FAILED", "The current weapon collection could not be read.", ex);
            }
        }

        private bool ApplySavedProfile(bool announce)
        {
            bool pedApplied;
            bool vehicleApplied;
            bool weaponsApplied;
            return ApplySavedProfile(
                announce,
                out pedApplied,
                out vehicleApplied,
                out weaponsApplied);
        }

        private bool ApplySavedProfile(
            bool announce,
            out bool pedApplied,
            out bool vehicleApplied,
            out bool weaponsApplied)
        {
            if (!RequireActive())
            {
                pedApplied = false;
                vehicleApplied = false;
                weaponsApplied = false;
                return false;
            }

            _profileRestoreAppliedWeaponIds.Clear();
            pedApplied = true;
            vehicleApplied = true;
            weaponsApplied = true;

            if (!string.IsNullOrWhiteSpace(_profile.SelectedPedModelName))
                pedApplied = ApplyPedModel(_profile.SelectedPedModelName, false, false);
            if (!string.IsNullOrWhiteSpace(_profile.SelectedVehicleModelName))
            {
                vehicleApplied = SpawnPoliceVehicle(_profile.SelectedVehicleModelName, false);
                if (vehicleApplied)
                    EnsureManagedStationUtilityGuidance();
            }
            // The preferred Profile's saved personal set is the Player's
            // preferred roulette inventory. Restore that set only after the
            // selected officer model is ready, so a later model change cannot
            // clear weapons that were just applied. Track successful entries
            // across streaming retries to avoid resetting their ammo each tick.
            weaponsApplied = pedApplied
                && ApplyPersonalLoadoutInternal(
                    false,
                    false,
                    _profileRestoreAppliedWeaponIds);

            bool success = pedApplied && vehicleApplied && weaponsApplied;
            if (announce)
            {
                LastOperationMessage = success
                    ? "Saved Police profile applied."
                    : "Saved Police profile was only partially applied.";
                _log.Runtime(
                    "POLICE_PROFILE_APPLIED",
                    "Ped=" + pedApplied + "; Vehicle=" + vehicleApplied + "; Weapons=" + weaponsApplied);
            }
            return success;
        }

        private void QueuePendingSavedProfileApply(
            bool pedApplied,
            bool vehicleApplied,
            bool weaponsApplied)
        {
            _pendingProfilePedApply = !pedApplied
                && !string.IsNullOrWhiteSpace(_profile.SelectedPedModelName);
            _pendingProfileVehicleApply = !vehicleApplied
                && !string.IsNullOrWhiteSpace(_profile.SelectedVehicleModelName);
            _pendingProfileWeaponsApply = !weaponsApplied;
            if (!_pendingProfilePedApply
                && !_pendingProfileVehicleApply
                && !_pendingProfileWeaponsApply)
                return;

            DateTime now = DateTime.UtcNow;
            _nextPendingProfileApply = now.AddMilliseconds(250);
            _pendingProfileApplyDeadline = now.AddSeconds(20);
            _log.Runtime(
                "POLICE_PROFILE_ASSETS_QUEUED",
                "Ped=" + _pendingProfilePedApply
                + "; Vehicle=" + _pendingProfileVehicleApply
                + "; Weapons=" + _pendingProfileWeaponsApply);
        }

        private bool HasPendingSavedProfileAssets
        {
            get
            {
                return _pendingProfilePedApply
                    || _pendingProfileVehicleApply
                    || _pendingProfileWeaponsApply;
            }
        }

        internal bool HasPendingProfilePedApply
        {
            get { return _pendingProfilePedApply; }
        }

        private bool HasActivePoliceGameplayOwner
        {
            get
            {
                return Dispatch.HasIncident
                    || Convoy.Active
                    || Backup.BlocksPlayerActivities
                    || _gangResponse.HasActiveIncident
                    || CrimeActivity.BlocksOtherPoliceActivities
                    || NpcResponse.HasActiveInteraction;
            }
        }

        private void ProcessPendingSavedProfileApply(bool paused)
        {
            if (paused || !IsPoliceAuthorityActive
                || (!_pendingProfilePedApply
                    && !_pendingProfileVehicleApply
                    && !_pendingProfileWeaponsApply))
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _nextPendingProfileApply)
                return;

            // Never change the player's model, force a saved vehicle, or
            // restore saved weapons while another Police owner is conducting
            // gameplay. Resume the same pending restore after the
            // scene/contact ends instead of allowing profile streaming to race
            // Dispatch, Convoy, Backup, Gang, Crime Activity, or NPC contact.
            if (HasActivePoliceGameplayOwner)
            {
                _nextPendingProfileApply = now.AddMilliseconds(750);
                _pendingProfileApplyDeadline = now.AddSeconds(20);
                return;
            }

            if (now >= _pendingProfileApplyDeadline)
            {
                bool stationSetupPending = _stationSetupCompletionPending;
                bool vehicleWasPending = _pendingProfileVehicleApply;
                bool profileAssetsWerePending = HasPendingSavedProfileAssets;
                string pendingPedSelection = _pendingProfilePedSelectionModelName;
                _log.Debug(
                    "POLICE_PROFILE_ASSETS_TIMEOUT",
                    "Saved Police assets did not finish streaming before the retry deadline. "
                    + "Ped=" + _pendingProfilePedApply
                    + "; Vehicle=" + _pendingProfileVehicleApply
                    + "; SelectedWeapon=" + (_profile.SelectedWeaponName ?? string.Empty)
                    + "; Weapons=" + _pendingProfileWeaponsApply
                    + "; PersonalWeapons=" + _profile.PersonalWeapons.Count() + ".");
                ClearPendingSavedProfileApply();
                if (stationSetupPending)
                {
                    _stationSetupCompletionPending = false;
                    LastOperationMessage = "Station setup could not load a selected Police resource. Return to the station marker to try again.";
                    Notification.PostTicker(LastOperationMessage, false, false);
                }
                else if (!string.IsNullOrWhiteSpace(pendingPedSelection))
                {
                    LastOperationMessage = "Character model did not finish loading: "
                        + pendingPedSelection
                        + ". The saved preference remains in Police Profile; select it again to retry.";
                    Notification.PostTicker(LastOperationMessage, false, false);
                }
                else if (profileAssetsWerePending)
                {
                    LastOperationMessage = vehicleWasPending
                        ? "The Police Utility did not finish loading. Press "
                            + _config.PoliceControls.DispatchMenuKey
                            + ", choose Spawn the Personal Utility to retry, then press "
                            + _config.PoliceControls.PatrolKey + " when it is ready."
                        : "Some saved Police assets did not finish loading. Reopen Police Authority to retry them; Patrol still requires a ready Utility.";
                    Notification.PostTicker(LastOperationMessage, false, false);
                }
                return;
            }

            // Individual flags prevent a vehicle that has already spawned
            // successfully from being created again while a separate weapon or
            // ped asset is still streaming.
            bool vehicleWasPendingThisAttempt = _pendingProfileVehicleApply;
            string previousMessage = LastOperationMessage;
            if (_pendingProfilePedApply)
                _pendingProfilePedApply = !ApplyPedModel(
                    _profile.SelectedPedModelName,
                    false,
                    false);
            if (_pendingProfileVehicleApply)
                _pendingProfileVehicleApply = !SpawnPoliceVehicle(
                    _profile.SelectedVehicleModelName,
                    false);
            bool vehicleAppliedThisAttempt = vehicleWasPendingThisAttempt
                && !_pendingProfileVehicleApply;
            if (vehicleAppliedThisAttempt)
                EnsureManagedStationUtilityGuidance();
            if (_pendingProfileWeaponsApply && !_pendingProfilePedApply)
                _pendingProfileWeaponsApply = !ApplyPersonalLoadoutInternal(
                    false,
                    false,
                    _profileRestoreAppliedWeaponIds);
            LastOperationMessage = previousMessage;

            if (!_pendingProfilePedApply
                && !_pendingProfileVehicleApply
                && !_pendingProfileWeaponsApply)
            {
                bool stationSetupPending = _stationSetupCompletionPending;
                string pendingPedSelection = _pendingProfilePedSelectionModelName;
                ClearPendingSavedProfileApply();
                _log.Runtime(
                    "POLICE_PROFILE_ASSETS_APPLIED",
                    "Saved Police profile completed after streamed assets became available.");
                if (stationSetupPending)
                    PersistStationSetupComplete(true);
                else if (!string.IsNullOrWhiteSpace(pendingPedSelection))
                {
                    LastOperationMessage = "Character model ready: "
                        + pendingPedSelection + ". Police Profile remains open.";
                    Notification.PostTicker(LastOperationMessage, false, false);
                }
                else
                {
                    LastOperationMessage = BuildPoliceAuthorityEntryMessage(true);
                    Notification.PostTicker(LastOperationMessage, false, false);
                }
                return;
            }

            if (vehicleAppliedThisAttempt && !_stationSetupCompletionPending)
            {
                LastOperationMessage = BuildPoliceAuthorityEntryMessage(false);
                Notification.PostTicker(LastOperationMessage, false, false);
            }

            _nextPendingProfileApply = now.AddMilliseconds(750);
        }

        private void ClearPendingSavedProfileApply()
        {
            _pendingProfilePedApply = false;
            _pendingProfileVehicleApply = false;
            _pendingProfileWeaponsApply = false;
            _pendingProfilePedSelectionModelName = string.Empty;
            _profileRestoreAppliedWeaponIds.Clear();
            _nextPendingProfileApply = DateTime.MinValue;
            _pendingProfileApplyDeadline = DateTime.MinValue;
        }

        internal bool SelectAgency(string agencyId)
        {
            if (!RequireActive())
                return false;
            if (!_profile.TrySelectAgency(agencyId))
                return Fail("POLICE_AGENCY_INVALID", "That Police department is unavailable.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The Police department selection could not be saved.");

            LSPDPoliceAgencyDefinition value = _profile.FindAgency(agencyId);
            return Succeed(
                "POLICE_AGENCY_SELECTED",
                "Police department selected: " + value.DisplayName + ".");
        }

        internal bool SelectStationSetupStation(string stationId)
        {
            if (!RequireStationSetupOpen())
                return false;
            LSPDPoliceStationDefinition station = _profile.FindStation(stationId);
            if (station == null || !_profile.TrySelectStation(station.Id))
                return Fail("POLICE_STATION_INVALID", "That Police station is unavailable.");
            LSPDPoliceAgencyDefinition agency = _profile.FindAgency(station.AgencyId);
            if (agency == null || !_profile.TrySelectAgency(agency.Id))
                return Fail("POLICE_STATION_AGENCY_MISSING", "That station's Police department is unavailable.");
            // The first Station Armory uses the system Police weapon catalog.
            // Personal loadout roulette remains saved as a separate Profile
            // preference and is available from Police Profile later.
            if (!string.IsNullOrWhiteSpace(_profile.Selection.WeaponId))
                _profile.TrySelectWeapon(_profile.Selection.WeaponId);
            LastOperationMessage = "Station selected: " + station.DisplayName + ".";
            return true;
        }

        internal bool SelectStationSetupAgency(string agencyId)
        {
            if (!RequireStationSetupOpen())
                return false;
            LSPDPoliceAgencyDefinition agency = _profile.FindAgency(agencyId);
            if (agency == null || !_profile.TrySelectAgency(agency.Id))
                return Fail("POLICE_AGENCY_INVALID", "That Police department is unavailable.");
            LastOperationMessage = "Police department selected: " + agency.DisplayName + ".";
            return true;
        }

        internal bool SelectStationSetupPed(string pedId)
        {
            if (!RequireStationSetupOpen())
                return false;
            LSPDPoliceModelDefinition ped = _profile.FindPed(pedId);
            if (ped == null || !_profile.TrySelectPed(ped.Id))
                return Fail("POLICE_PED_INVALID", "That Police character is unavailable.");
            LastOperationMessage = "Police character selected: " + ped.DisplayName + ".";
            return true;
        }

        internal bool SelectStationSetupVehicle(string vehicleId)
        {
            if (!RequireStationSetupOpen())
                return false;
            LSPDPoliceVehicleDefinition vehicle = _profile.FindVehicle(vehicleId);
            if (vehicle == null || !_profile.TrySelectVehicle(vehicle.Id))
                return Fail("POLICE_VEHICLE_INVALID", "That Police vehicle is unavailable.");
            LastOperationMessage = "Police vehicle selected: " + vehicle.DisplayName + ".";
            return true;
        }

        internal bool SelectStationSetupWeapon(string weaponId)
        {
            if (!RequireStationSetupOpen())
                return false;
            LSPDPoliceWeaponDefinition weapon = _profile.FindWeapon(weaponId);
            if (weapon == null || !_profile.TrySelectWeapon(weapon.Id))
                return Fail("POLICE_WEAPON_INVALID", "That Police weapon is unavailable.");
            LastOperationMessage = "Police armory selection: " + weapon.DisplayName + ".";
            return true;
        }

        internal bool CompleteInitialStationSetup()
        {
            if (!RequireActive())
                return false;
            if (_profile.StationSetupComplete)
            {
                LastOperationMessage = "Police station setup is already complete.";
                return true;
            }
            LSPDPoliceStationDefinition station = _profile.FindStation(
                _profile.Selection.StationId);
            if (station == null)
                return Fail("POLICE_STATION_SETUP_STATION_MISSING", "Choose a Police station before finishing setup.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "Police setup choices could not be saved.");

            // The Wardrobe and Garage are camera previews. When the Player
            // closes setup, return them to the selected Station entrance
            // before searching for the parked Police vehicle. In particular,
            // do not search for the vehicle from the interior preview room.
            if (!MovePlayerToStationExterior(station))
                return false;

            // Keep the initial Police vehicle close to the station doorway.
            // The pending flag is also used by the streaming retry path, so a
            // late-loaded vehicle observes the same 20-metre parking limit.
            _stationSetupCompletionPending = true;
            bool pedApplied;
            bool vehicleApplied;
            bool weaponsApplied;
            bool profileApplied = ApplySavedProfile(
                false,
                out pedApplied,
                out vehicleApplied,
                out weaponsApplied);
            if (profileApplied)
                return PersistStationSetupComplete(false);

            QueuePendingSavedProfileApply(pedApplied, vehicleApplied, weaponsApplied);
            _stationSetupCompletionPending = _pendingProfilePedApply
                || _pendingProfileVehicleApply
                || _pendingProfileWeaponsApply;
            LastOperationMessage = _stationSetupCompletionPending
                ? "Your choices are saved. Police resources are loading; stay near the station."
                : "Station setup could not prepare the selected Police resources. Return to the station marker to try again.";
            return false;
        }

        private bool MovePlayerToStationExterior(LSPDPoliceStationDefinition station)
        {
            if (station == null)
                return Fail("POLICE_STATION_EXIT_MISSING", "The selected Police station could not be found.");

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return Fail("POLICE_STATION_EXIT_PLAYER_MISSING", "The Player could not be returned to the Station entrance.");

            Vector3 exterior;
            float heading;
            string source;
            string failure;
            if (!TryResolveSafeStationExteriorPosition(
                player,
                station,
                out exterior,
                out heading,
                out source,
                out failure))
            {
                _log.Debug(
                    "POLICE_STATION_SETUP_PLAYER_SAFE_SPOT_UNAVAILABLE",
                    "Station=" + station.Id + "; " + failure);
                return Fail(
                    "POLICE_STATION_SETUP_SAFE_EXTERIOR_UNAVAILABLE",
                    "I couldn't find safe outdoor ground near your station. Move outside and try setup again.");
            }

            try
            {
                player.Position = exterior;
                player.Heading = heading;
                _log.Runtime(
                    "POLICE_STATION_SETUP_PLAYER_EXITED",
                    "Station=" + station.Id + "; Source=" + source
                    + "; Position=" + exterior.X.ToString("0.00", CultureInfo.InvariantCulture)
                    + "," + exterior.Y.ToString("0.00", CultureInfo.InvariantCulture)
                    + "," + exterior.Z.ToString("0.00", CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                return Fail(
                    "POLICE_STATION_EXIT_FAILED",
                    "The Player could not be moved outside the selected Police station.",
                    ex);
            }
        }

        private bool TryResolveSafeStationExteriorPosition(
            Ped player,
            LSPDPoliceStationDefinition station,
            out Vector3 position,
            out float heading,
            out string source,
            out string failure)
        {
            position = Vector3.Zero;
            heading = 0f;
            source = string.Empty;
            failure = string.Empty;
            if (player == null || !player.Exists())
            {
                failure = "The Player is not available to find safe outdoor ground.";
                return false;
            }
            if (station == null)
            {
                failure = "The selected Police station could not be found.";
                return false;
            }

            Vector3 stationPosition = new Vector3(
                station.ExteriorX,
                station.ExteriorY,
                station.ExteriorZ);
            if (stationPosition == Vector3.Zero
                || float.IsNaN(stationPosition.X) || float.IsInfinity(stationPosition.X)
                || float.IsNaN(stationPosition.Y) || float.IsInfinity(stationPosition.Y)
                || float.IsNaN(stationPosition.Z) || float.IsInfinity(stationPosition.Z))
            {
                failure = "The saved Police station coordinates are invalid.";
                return false;
            }

            LSPDPoliceLocationDefinition exteriorLocation =
                _profile.FindLocation(station.LocationId);
            if (exteriorLocation == null || !exteriorLocation.ExteriorSafe)
            {
                failure = "The selected Station has no authored safe outside entrance point.";
                return false;
            }

            Vector3 authoredEntrance = new Vector3(
                exteriorLocation.X,
                exteriorLocation.Y,
                exteriorLocation.Z);
            if (authoredEntrance == Vector3.Zero
                || float.IsNaN(authoredEntrance.X) || float.IsInfinity(authoredEntrance.X)
                || float.IsNaN(authoredEntrance.Y) || float.IsInfinity(authoredEntrance.Y)
                || float.IsNaN(authoredEntrance.Z) || float.IsInfinity(authoredEntrance.Z))
            {
                failure = "The selected Station entrance coordinates are invalid.";
                return false;
            }

            heading = float.IsNaN(station.ExteriorHeading)
                || float.IsInfinity(station.ExteriorHeading)
                ? 0f
                : station.ExteriorHeading;

            // Station setup can only open while the Player is on foot at the
            // authored doorway marker. Keep that exact position on completion
            // across every station. GTA can report a building interior for an
            // exterior doorway coordinate, so that native must not reject the
            // live position that already activated the station marker.
            string lastReason = string.Empty;
            try
            {
                if (!player.IsInVehicle()
                    && Distance2D(player.Position, authoredEntrance) <= 8f)
                {
                    position = player.Position;
                    source = "StationEntranceAlreadyReached";
                    return true;
                }
            }
            catch (Exception ex)
            {
                lastReason = "GTA could not check the Player's current entrance position: "
                    + ex.GetType().Name + ".";
            }

            float[] ringRadii = { 0f, 2f, 4f, 6f };
            for (int ring = 0; ring < ringRadii.Length; ring++)
            {
                float radius = ringRadii[ring];
                int probes = ring == 0 ? 1 : 8;
                for (int probe = 0; probe < probes; probe++)
                {
                    double radians = probe * (Math.PI / 4.0);
                    Vector3 candidate = authoredEntrance;
                    candidate.X += (float)Math.Cos(radians) * radius;
                    candidate.Y += (float)Math.Sin(radians) * radius;
                    if (Distance2D(candidate, authoredEntrance) > 6f)
                        continue;

                    try
                    {
                        if (Function.Call<int>(
                            Hash.GET_INTERIOR_AT_COORDS,
                            candidate.X,
                            candidate.Y,
                            candidate.Z) != 0)
                        {
                            lastReason = "The authored entrance candidate is inside a building.";
                            continue;
                        }

                        float groundZ;
                        Vector3 groundNormal;
                        if (!World.GetGroundHeightAndNormal(
                            candidate,
                            out groundZ,
                            out groundNormal)
                            || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                            || groundNormal.Z < 0.55f
                            || Math.Abs(candidate.Z - groundZ) > 4f
                            || Math.Abs(groundZ - station.ExteriorZ) > 8f)
                        {
                            lastReason = "GTA could not confirm level outdoor ground at the entrance.";
                            continue;
                        }

                        candidate.Z = groundZ;
                        position = candidate;
                        source = radius == 0f
                            ? "AuthoredStationEntrance"
                            : "NearbyStationEntrance";
                        return true;
                    }
                    catch (Exception ex)
                    {
                        lastReason = "GTA could not validate outdoor ground at the entrance: "
                            + ex.GetType().Name + ".";
                        continue;
                    }
                }
            }

            failure = "No clear outdoor ground was found at the Station entrance or within 6 metres."
                + (string.IsNullOrWhiteSpace(lastReason) ? string.Empty : " Last check: " + lastReason);
            return false;
        }

        private bool RequireStationSetupOpen()
        {
            if (!RequireActive())
                return false;
            if (_profile.StationSetupComplete)
            {
                LastOperationMessage = "The beginning Police station setup is already complete.";
                return false;
            }
            return true;
        }

        private bool PersistStationSetupComplete(bool notify)
        {
            _profile.SetStationSetupComplete(true);
            if (!_profile.Save())
            {
                _profile.SetStationSetupComplete(false);
                _stationSetupCompletionPending = false;
                LastOperationMessage = "Your Police choices were applied, but setup could not be saved. Return to the station marker to try again.";
                if (notify)
                    Notification.PostTicker(LastOperationMessage, false, false);
                _log.Debug("POLICE_STATION_SETUP_SAVE_FAILED", LastOperationMessage);
                return false;
            }

            _stationSetupCompletionPending = false;
            bool vehicleWaypointSet = SetStationSetupVehicleWaypoint();
            LastOperationMessage = vehicleWaypointSet
                ? "Police Roleplay is active. Follow the waypoint to your Police Utility. Get inside, then press P or choose Start Patrol in Police Gameplay."
                : "Police Roleplay is active. Go to your Police Utility, get inside, then press P or choose Start Patrol in Police Gameplay.";
            Vehicle setupVehicle = _managedPoliceVehicle;
            _log.Runtime(
                "POLICE_STATION_SETUP_COMPLETED",
                "Station=" + _profile.Selection.StationId
                + "; VehicleWaypointSet=" + vehicleWaypointSet
                + "; VehicleHandle=" + (setupVehicle != null && setupVehicle.Exists()
                    ? setupVehicle.Handle.ToString(CultureInfo.InvariantCulture)
                    : "none"));
            if (notify)
                Notification.PostTicker(LastOperationMessage, false, false);
            return true;
        }

        private bool SetStationSetupVehicleWaypoint()
        {
            Vehicle vehicle = _managedPoliceVehicle;
            if (vehicle == null || !vehicle.Exists())
                return false;

            try
            {
                World.WaypointPosition = vehicle.Position;
                _log.Runtime(
                    "POLICE_STATION_SETUP_UTILITY_WAYPOINT_SET",
                    "VehicleHandle=" + vehicle.Handle
                    + "; Position=" + vehicle.Position.X.ToString("0.00", CultureInfo.InvariantCulture)
                    + "," + vehicle.Position.Y.ToString("0.00", CultureInfo.InvariantCulture)
                    + "," + vehicle.Position.Z.ToString("0.00", CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_STATION_SETUP_UTILITY_WAYPOINT_FAILED", ex);
                return false;
            }
        }

        internal bool SelectStation(string stationId)
        {
            if (!RequireActive())
                return false;
            if (Dispatch.HasIncident || Convoy.Active)
                return Fail(
                    "POLICE_STATION_CHANGE_ASSIGNMENT_ACTIVE",
                    "Finish the active Dispatch or custody handoff before changing stations.");

            LSPDPoliceStationDefinition station = _profile.FindStation(stationId);
            LSPDPoliceAgencyDefinition agency = station == null
                ? null : _profile.FindAgency(station.AgencyId);
            if (station == null || agency == null)
                return Fail("POLICE_STATION_INVALID", "That Police station is unavailable.");

            string previousStationId = _profile.Selection.StationId;
            string previousAgencyId = _profile.Selection.AgencyId;
            bool previousSetupComplete = _profile.StationSetupComplete;
            bool stationChanged = !string.Equals(
                previousStationId,
                station.Id,
                StringComparison.OrdinalIgnoreCase);
            if (!_profile.TrySelectStation(station.Id)
                || !_profile.TrySelectAgency(agency.Id))
                return Fail("POLICE_STATION_INVALID", "That Police station is unavailable.");

            if (stationChanged)
                _profile.SetStationSetupComplete(false);
            if (!_profile.Save())
            {
                if (!string.IsNullOrWhiteSpace(previousStationId))
                    _profile.TrySelectStation(previousStationId);
                if (!string.IsNullOrWhiteSpace(previousAgencyId))
                    _profile.TrySelectAgency(previousAgencyId);
                _profile.SetStationSetupComplete(previousSetupComplete);
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The Police station selection could not be saved.");
            }

            string selectionMessage = "Police station selected: " + station.DisplayName
                + ". Department: " + agency.DisplayName + ".";
            if (stationChanged || !_profile.StationSetupComplete)
                selectionMessage += " Go there and use the station marker to set up your Police resources.";
            else
                selectionMessage += " Your Player and current car stay where they are.";

            return Succeed(
                "POLICE_STATION_SELECTED",
                selectionMessage);
        }

        internal string SetSelectedStationWaypoint()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (Dispatch.HasIncident || Convoy.Active)
            {
                LastOperationMessage = "The active Police assignment already owns the current GPS route.";
                return LastOperationMessage;
            }
            if (!_config.ShowWaypoint)
            {
                LastOperationMessage = "Route guidance is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            LSPDPoliceStationDefinition station =
                _profile.FindStation(_profile.Selection.StationId);
            if (station == null || !TrySetStationWaypoint(station))
            {
                LastOperationMessage = "The selected Police station could not be placed on the GPS.";
                return LastOperationMessage;
            }
            LastOperationMessage = "GPS set to " + station.DisplayName + ".";
            return LastOperationMessage;
        }

        internal bool SelectPolicePed(string idOrModel)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceModelDefinition value = _profile.FindPed(idOrModel);
            if (value == null)
                return Fail("POLICE_PED_INVALID", "That Police character is unavailable.");
            if (!ApplyPedModel(value.ModelName, true))
                return false;
            if (!_profile.TrySelectPed(value.Id))
                return Fail("POLICE_PED_SELECTION_FAILED", "The Police character could not be selected.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The Police character selection could not be saved.");

            return Succeed(
                "POLICE_PED_SELECTED",
                "Police character selected: " + value.DisplayName + ".");
        }

        internal bool SelectPoliceVehicle(string idOrModel)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceVehicleDefinition value = _profile.FindVehicle(idOrModel);
            if (value == null)
                return Fail("POLICE_VEHICLE_INVALID", "That Police vehicle is unavailable.");
            if (!SpawnPoliceVehicle(value.ModelName, true))
                return false;
            if (!_profile.TrySelectVehicle(value.Id))
                return Fail("POLICE_VEHICLE_SELECTION_FAILED", "The Police vehicle could not be selected.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The Police vehicle selection could not be saved.");

            return Succeed(
                "POLICE_VEHICLE_SELECTED",
                "Police vehicle selected: " + value.DisplayName + ".");
        }

        internal bool SelectPoliceWeapon(string idOrWeapon)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceWeaponDefinition value = _profile.FindWeapon(idOrWeapon);
            if (value == null)
                return Fail("POLICE_WEAPON_INVALID", "That Police weapon is unavailable.");
            if (!GiveWeapon(value.WeaponName, value.DefaultAmmo, true))
                return false;
            if (!_profile.TrySelectWeapon(value.Id))
                return Fail("POLICE_WEAPON_SELECTION_FAILED", "The Police weapon could not be selected.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The Police weapon selection could not be saved.");

            return Succeed(
                "POLICE_WEAPON_SELECTED",
                "Police weapon selected: " + value.DisplayName + ".");
        }

        internal bool SetSelectedPedAsFavourite()
        {
            if (!RequireActive())
                return false;
            string model = _profile.SelectedPedModelName;
            if (string.IsNullOrWhiteSpace(model))
                return Fail("PERSONAL_PED_EMPTY", "Select a Police character first.");
            return SaveFavouritePed(model);
        }

        internal bool SetCustomFavouritePed(string modelName)
        {
            if (!RequireActive())
                return false;
            string normalized = NormalizeModelName(modelName);
            if (string.IsNullOrEmpty(normalized))
                return Fail("PERSONAL_PED_EMPTY", "No addon ped model was entered.");

            Model model = new Model(normalized);
            try
            {
                if (!model.IsValid || !model.IsPed)
                    return Fail("POLICE_PED_MODEL_INVALID",
                        "Ped model is not available in this game build: " + normalized + ".");
                bool modelLoaded = model.IsLoaded;
                if (!modelLoaded)
                    model.Request();
                if (!_profile.SetFavouritePed(normalized))
                    return Fail("PERSONAL_PED_INVALID",
                        "The personal Police character was not accepted.");
                if (!_profile.Save())
                    return Fail("POLICE_PROFILE_SAVE_FAILED",
                        "The Police character preference could not be saved.");

                // Apply the player-model native on the next owner tick, after
                // the menu selection callback has returned. This prevents a
                // selection from tearing down the visible framework while
                // GTA replaces the Player's Ped handle.
                QueuePendingProfilePedSelection(normalized);
                return Succeed(
                    "PERSONAL_PED_SELECTION_QUEUED",
                    modelLoaded
                        ? "Character preference saved. Applying " + normalized
                            + " on the next game tick; Police Profile stays open."
                        : "Character preference saved. Loading " + normalized
                            + "; Police Profile stays open until the model is ready.");
            }
            catch (Exception ex)
            {
                return Fail("POLICE_PED_MODEL_ERROR",
                    "Ped model could not be prepared: " + normalized + ".", ex);
            }

        }

        private void QueuePendingProfilePedSelection(string modelName)
        {
            _pendingProfilePedApply = true;
            _pendingProfilePedSelectionModelName = modelName ?? string.Empty;
            DateTime now = DateTime.UtcNow;
            _nextPendingProfileApply = now.AddMilliseconds(250);
            _pendingProfileApplyDeadline = now.AddSeconds(20);
            _log.Runtime(
                "POLICE_PROFILE_PED_SELECTION_QUEUED",
                "Model=" + _pendingProfilePedSelectionModelName
                + "; SavedPreference=true; ProfileFrameworkRemainsOpen=true");
        }

        private bool SaveFavouritePed(string modelName)
        {
            if (!_profile.SetFavouritePed(modelName))
                return Fail("PERSONAL_PED_INVALID", "The personal Police character was not accepted.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The personal Police character could not be saved.");
            return Succeed(
                "PERSONAL_PED_SAVED",
                "Personal Police character saved: " + _profile.SelectedPedDisplayName + ".");
        }

        internal bool SetSelectedPedAsBackupFavourite()
        {
            if (!RequireActive())
                return false;
            string model = _profile.SelectedPedModelName;
            if (string.IsNullOrWhiteSpace(model))
                return Fail("BACKUP_PED_EMPTY", "Select a Police character before saving a Backup favorite.");
            return SaveFavouriteBackupPed(model);
        }

        internal bool SetSelectedPedAsCitizenBackupFavourite()
        {
            if (!RequireActive())
                return false;
            string model = _profile.SelectedPedModelName;
            if (string.IsNullOrWhiteSpace(model))
                return Fail("BACKUP_PED_EMPTY", "Select a Police character before choosing Citizen Backup.");
            return SaveFavouriteBackupPed(model, true);
        }

        internal bool SetSelectedPedAsDispatchBackupFavourite()
        {
            if (!RequireActive())
                return false;
            string model = _profile.SelectedPedModelName;
            if (string.IsNullOrWhiteSpace(model))
                return Fail("BACKUP_PED_EMPTY", "Select a Police character before choosing Dispatch Backup.");
            return SaveFavouriteBackupPed(model, false);
        }

        internal bool SetCustomBackupFavourite(string modelName)
        {
            if (!RequireActive())
                return false;
            string normalized = NormalizeModelName(modelName);
            if (string.IsNullOrEmpty(normalized))
                return Fail("BACKUP_PED_EMPTY", "No addon Backup officer model was entered.");
            if (!ValidateBackupPedModel(normalized))
                return false;
            return SaveFavouriteBackupPed(normalized);
        }

        internal bool SetCustomCitizenBackupFavourite(string modelName)
        {
            return SetCustomBackupFavourite(modelName, true);
        }

        internal bool SetCustomDispatchBackupFavourite(string modelName)
        {
            return SetCustomBackupFavourite(modelName, false);
        }

        internal bool SelectBackupFavourite(string idOrModel)
        {
            if (!RequireActive())
                return false;
            if (!_profile.SelectFavouriteBackupPed(idOrModel))
                return Fail("BACKUP_PED_SELECTION_FAILED", "That saved Backup officer is unavailable.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The selected Backup officer could not be saved.");
            LSPDPoliceFavoriteModel favorite = _profile.ActiveFavoriteBackupPed;
            return Succeed(
                "BACKUP_PED_SELECTED",
                "Preferred Backup officer: " + (favorite == null ? "None" : favorite.DisplayName) + ".");
        }

        internal bool SelectCitizenBackupFavourite(string idOrModel)
        {
            return SelectBackupFavourite(idOrModel, true);
        }

        internal bool SelectDispatchBackupFavourite(string idOrModel)
        {
            return SelectBackupFavourite(idOrModel, false);
        }

        private bool SelectBackupFavourite(string idOrModel, bool citizenInteraction)
        {
            if (!RequireActive())
                return false;
            bool selected = citizenInteraction
                ? _profile.SelectFavouriteCitizenBackupPed(idOrModel)
                : _profile.SelectFavouriteDispatchBackupPed(idOrModel);
            if (!selected)
                return Fail("BACKUP_PED_SELECTION_FAILED", "That saved Backup officer is unavailable.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The selected Backup officer could not be saved.");
            LSPDPoliceFavoriteModel favorite = citizenInteraction
                ? _profile.ActiveFavoriteCitizenBackupPed
                : _profile.ActiveFavoriteDispatchBackupPed;
            string kind = citizenInteraction ? "Citizen Interaction" : "Dispatch";
            return Succeed(
                "BACKUP_PED_SELECTED",
                "Preferred " + kind + " Backup officer: "
                + (favorite == null ? "None" : favorite.DisplayName) + ".");
        }

        private bool SetCustomBackupFavourite(string modelName, bool citizenInteraction)
        {
            if (!RequireActive())
                return false;
            string normalized = NormalizeModelName(modelName);
            if (string.IsNullOrEmpty(normalized))
                return Fail("BACKUP_PED_EMPTY", "No addon Backup officer model was entered.");
            if (!ValidateBackupPedModel(normalized))
                return false;
            return SaveFavouriteBackupPed(normalized, citizenInteraction);
        }

        private bool ValidateBackupPedModel(string modelName)
        {
            Model model = new Model(modelName);
            try
            {
                // Saving a support favorite must not change the player model.
                // Validate only the model type here; the Backup owner streams it
                // asynchronously when the player actually requests support.
                if (!model.IsValid || !model.IsPed)
                    return Fail("BACKUP_PED_INVALID", "Backup officer model is not available in this game build: " + modelName + ".");
            }
            catch (Exception ex)
            {
                return Fail("BACKUP_PED_INVALID", "Backup officer model could not be checked: " + modelName + ".", ex);
            }
            finally
            {
                try { model.MarkAsNoLongerNeeded(); } catch { }
            }
            return true;
        }

        private bool SaveFavouriteBackupPed(string modelName)
        {
            if (!_profile.SetFavouriteBackupPed(modelName))
                return Fail("BACKUP_PED_INVALID", "The Backup officer was not accepted.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The Backup officer could not be saved.");
            LSPDPoliceFavoriteModel favorite = _profile.ActiveFavoriteBackupPed;
            return Succeed(
                "BACKUP_PED_SAVED",
                "Backup officer saved: " + (favorite == null ? modelName : favorite.DisplayName) + ".");
        }

        private bool SaveFavouriteBackupPed(string modelName, bool citizenInteraction)
        {
            bool saved = citizenInteraction
                ? _profile.SetFavouriteCitizenBackupPed(modelName)
                : _profile.SetFavouriteDispatchBackupPed(modelName);
            if (!saved)
                return Fail("BACKUP_PED_INVALID", "The Backup officer was not accepted.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The Backup officer could not be saved.");
            LSPDPoliceFavoriteModel favorite = citizenInteraction
                ? _profile.ActiveFavoriteCitizenBackupPed
                : _profile.ActiveFavoriteDispatchBackupPed;
            string kind = citizenInteraction ? "Citizen Interaction" : "Dispatch";
            return Succeed(
                "BACKUP_PED_SAVED",
                kind + " Backup officer saved: " + (favorite == null ? modelName : favorite.DisplayName) + ".");
        }

        internal bool SetSelectedVehicleAsFavourite()
        {
            if (!RequireActive())
                return false;
            string model = _profile.SelectedVehicleModelName;
            if (string.IsNullOrWhiteSpace(model))
                return Fail("PERSONAL_VEHICLE_EMPTY", "Select a Police vehicle first.");
            return SaveFavouriteVehicle(model);
        }

        internal bool SetCustomFavouriteVehicle(string modelName)
        {
            if (!RequireActive())
                return false;
            string normalized = NormalizeModelName(modelName);
            if (string.IsNullOrEmpty(normalized))
                return Fail("PERSONAL_VEHICLE_EMPTY", "No addon vehicle model was entered.");
            if (!SpawnPoliceVehicle(normalized, true))
                return false;
            return SaveFavouriteVehicle(normalized);
        }

        private bool SaveFavouriteVehicle(string modelName)
        {
            if (!_profile.SetFavouriteVehicle(modelName))
                return Fail("PERSONAL_VEHICLE_INVALID", "The personal Police vehicle was not accepted.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The personal Police vehicle could not be saved.");
            return Succeed(
                "PERSONAL_VEHICLE_SAVED",
                "Personal Police vehicle saved: " + _profile.SelectedVehicleDisplayName + ".");
        }

        internal bool AddPersonalWeapon(string idOrWeapon)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceWeaponDefinition value = _profile.FindWeapon(idOrWeapon);
            if (value == null)
                return Fail("PERSONAL_WEAPON_INVALID", "Select a weapon from the Police weapon library.");
            if (!GiveWeapon(value.WeaponName, value.DefaultAmmo, true))
                return false;
            if (!_profile.AddPersonalWeapon(value.Id))
                return Fail("PERSONAL_WEAPON_ADD_FAILED", "The weapon could not be added to the personal loadout.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The personal weapon collection could not be saved.");

            return Succeed(
                "PERSONAL_WEAPON_ADDED",
                value.DisplayName + " added to Personal Police Weapons.");
        }

        internal bool SelectPersonalWeapon(string idOrWeapon)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceFavoriteWeapon value = _profile.FindPersonalWeapon(idOrWeapon);
            if (value == null)
                return Fail("PERSONAL_WEAPON_INVALID", "That personal Police weapon is unavailable.");
            if (!GiveWeapon(value.WeaponName, value.Ammo, true, value))
                return false;
            if (!_profile.SelectPersonalWeapon(value.Id))
                return Fail("PERSONAL_WEAPON_SELECTION_FAILED", "The personal Police weapon could not be selected.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The active Police weapon could not be saved.");
            return Succeed(
                "PERSONAL_WEAPON_SELECTED",
                "Active Police weapon: " + value.DisplayName + ".");
        }

        internal bool RemovePersonalWeapon(string idOrWeapon)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceFavoriteWeapon value = _profile.FindPersonalWeapon(idOrWeapon);
            if (value == null || !_profile.RemovePersonalWeapon(idOrWeapon))
                return Fail("PERSONAL_WEAPON_REMOVE_FAILED", "That personal Police weapon could not be removed.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The personal weapon collection could not be saved.");
            return Succeed(
                "PERSONAL_WEAPON_REMOVED",
                value.DisplayName + " removed from Personal Police Weapons.");
        }

        internal bool CyclePersonalWeapon()
        {
            if (!RequireActive())
                return false;
            List<LSPDPoliceFavoriteWeapon> values = _profile.PersonalWeapons.ToList();
            if (values.Count == 0)
                return Fail("PERSONAL_WEAPON_EMPTY", "Personal Police Weapons is empty.");

            int current = values.FindIndex(value =>
                string.Equals(
                    value.Id,
                    _profile.Selection.PersonalActiveWeaponId,
                    StringComparison.OrdinalIgnoreCase));
            LSPDPoliceFavoriteWeapon next = values[(current + 1 + values.Count) % values.Count];
            return SelectPersonalWeapon(next.Id);
        }

        internal bool ReloadPersonalWeapons()
        {
            if (!RequireActive())
                return false;
            if (!_profile.ReloadPersonalWeapons())
                return Fail(
                    "PERSONAL_WEAPON_RELOAD_FAILED",
                    "Personal Police Weapons XML could not be reloaded.");
            if (!ApplyPersonalLoadoutInternal(false))
                return false;
            return Succeed(
                "PERSONAL_WEAPON_RELOADED",
                "Personal Police Weapons reloaded: " + _profile.PersonalWeapons.Count() + ".");
        }

        internal bool ApplyPersonalLoadout()
        {
            if (!RequireActive())
                return false;
            if (!_profile.SelectPersonalLoadout())
                return Fail("PERSONAL_WEAPON_EMPTY", "Personal Police Weapons is empty.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The preferred personal weapon loadout could not be saved.");
            bool applied = ApplyPersonalLoadoutInternal(true);
            if (applied)
                Succeed("PERSONAL_LOADOUT_APPLIED", "Personal Police weapon collection applied.");
            return applied;
        }

        private bool ApplyPersonalLoadoutInternal(
            bool requirePersonalSet,
            bool reportPendingFailure = true,
            ISet<string> alreadyAppliedWeaponIds = null)
        {
            List<LSPDPoliceFavoriteWeapon> personal = _profile.PersonalWeapons.ToList();
            bool usePersonalLoadout = _profile.Selection.UsePersonalLoadout;
            if (!usePersonalLoadout)
            {
                // The Station Armory and the Police Profile's personal weapon
                // roulette are separate saved choices. When the system catalog
                // is selected, apply only that weapon; a large personal set
                // must not block first Station setup.
                string selected = _profile.SelectedWeaponName;
                if (string.IsNullOrWhiteSpace(selected))
                    return true;
                string selectedKey = "selected:" + selected;
                if (alreadyAppliedWeaponIds != null
                    && alreadyAppliedWeaponIds.Contains(selectedKey))
                    return true;
                bool selectedApplied = GiveWeapon(
                    selected,
                    _profile.SelectedWeaponAmmo,
                    true,
                    null,
                    false);
                if (selectedApplied && alreadyAppliedWeaponIds != null)
                    alreadyAppliedWeaponIds.Add(selectedKey);
                if (!selectedApplied && reportPendingFailure)
                    return Fail(
                        "POLICE_LOADOUT_ASSETS_PENDING",
                        "The selected Police weapon is still loading or unavailable. Retry after GTA finishes streaming it.");
                return selectedApplied;
            }

            if (personal.Count == 0)
            {
                if (requirePersonalSet)
                    return Fail("PERSONAL_WEAPON_EMPTY", "Personal Police Weapons is empty.");
                return true;
            }

            bool allApplied = true;
            bool activeWeaponFound = personal.Any(weapon => string.Equals(
                weapon.Id,
                _profile.Selection.PersonalActiveWeaponId,
                StringComparison.OrdinalIgnoreCase));
            foreach (LSPDPoliceFavoriteWeapon weapon in personal)
            {
                bool equip = string.Equals(
                    weapon.Id,
                    _profile.Selection.PersonalActiveWeaponId,
                    StringComparison.OrdinalIgnoreCase);
                equip = usePersonalLoadout && equip;
                if (alreadyAppliedWeaponIds != null
                    && alreadyAppliedWeaponIds.Contains(weapon.Id))
                    continue;
                if (!GiveWeapon(weapon.WeaponName, weapon.Ammo, equip, weapon, false))
                    allApplied = false;
                else if (alreadyAppliedWeaponIds != null)
                    alreadyAppliedWeaponIds.Add(weapon.Id);
            }

            // Preserve a valid personal set even when the active weapon id is
            // missing from it. Select its first saved weapon as the fallback.
            if (!activeWeaponFound && usePersonalLoadout && personal.Count > 0)
            {
                _profile.SelectPersonalWeapon(personal[0].Id);
                if (!GiveWeapon(personal[0].WeaponName, personal[0].Ammo, true, personal[0], false))
                    allApplied = false;
                else if (alreadyAppliedWeaponIds != null)
                    alreadyAppliedWeaponIds.Add(personal[0].Id);
            }
            if (!allApplied && reportPendingFailure)
                return Fail(
                    "POLICE_LOADOUT_ASSETS_PENDING",
                    "One or more saved Police weapons are still loading or unavailable. The collection remains saved and may be retried.");
            return allApplied;
        }

        internal bool SavePoliceProfile()
        {
            if (!RequireActive())
                return false;
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "Police profile could not be saved.");
            return Succeed("POLICE_PROFILE_SAVED", "Police profile saved.");
        }

        private bool ApplyPedModel(
            string modelName,
            bool reportFailure,
            bool restoreLoadout = true)
        {
            Model model = new Model(modelName);
            bool releaseModel = false;
            try
            {
                // Personal addon models are valid streamed assets even when
                // they are not part of the vanilla CD image. Model validity,
                // type, request, and load state are the meaningful checks here.
                if (!model.IsValid || !model.IsPed)
                {
                    if (reportFailure)
                        Fail("POLICE_PED_MODEL_INVALID", "Ped model is not available in this game build: " + modelName + ".");
                    else
                        _log.Debug("POLICE_PED_MODEL_INVALID", modelName);
                    return false;
                }
                if (!model.IsLoaded)
                {
                    // This method can be reached from a LemonUI profile action.
                    // Request only; the timeout overload calls Script.Wait and
                    // is not legal outside ScriptHook's normal game tick.
                    model.Request();
                    // Keep the request alive across normal Script ticks. The
                    // former finally block released it immediately, causing the
                    // same addon ped to remain permanently "pending".
                    if (reportFailure)
                        Fail("POLICE_PED_MODEL_LOAD_PENDING", "Ped model is loading. Select it again in a moment: " + modelName + ".");
                    return false;
                }

                releaseModel = true;
                Ped current = Game.Player.Character;
                if (current == null || !current.Exists())
                    return Fail("POLICE_PLAYER_UNAVAILABLE", "The player character is unavailable.");
                bool changedModel = current.Model.Hash != model.Hash;
                bool screenFadeStarted = changedModel
                    && BeginPolicePedScreenFade();
                if (changedModel && !Game.Player.ChangeModel(model))
                {
                    if (screenFadeStarted)
                        RestorePolicePedScreenFade();
                    return Fail("POLICE_PED_MODEL_APPLY_FAILED", "Ped model could not be applied: " + modelName + ".");
                }

                if (screenFadeStarted)
                    SchedulePolicePedScreenFadeIn();

                if (changedModel)
                    _profileRestoreAppliedWeaponIds.Clear();

                // GTA clears inventory when the officer model changes. Restore
                // the saved personal set and retry only unfinished weapon
                // assets from the normal Authority tick.
                if (restoreLoadout
                    && IsPoliceAuthorityActive
                    && !ApplyPersonalLoadoutInternal(
                        false,
                        false,
                        _profileRestoreAppliedWeaponIds))
                {
                    _pendingProfileWeaponsApply = true;
                    if (_pendingProfileApplyDeadline == DateTime.MinValue)
                    {
                        DateTime now = DateTime.UtcNow;
                        _nextPendingProfileApply = now.AddMilliseconds(250);
                        _pendingProfileApplyDeadline = now.AddSeconds(20);
                    }
                    _log.Debug(
                        "POLICE_LOADOUT_RESTORE_PARTIAL",
                        "The Police character changed, and unfinished saved weapons are queued for retry.");
                }
                return true;
            }
            catch (Exception ex)
            {
                RestorePolicePedScreenFade();
                return Fail("POLICE_PED_MODEL_ERROR", "Ped model could not be applied: " + modelName + ".", ex);
            }
            finally
            {
                // A pending streamed model must remain requested until a later
                // Process tick observes IsLoaded. Release only after it became
                // usable (or after a successful application).
                if (releaseModel)
                {
                    try { model.MarkAsNoLongerNeeded(); } catch { }
                }
            }
        }

        private bool PreparePatrolVehicle()
        {
            string modelName = _profile.SelectedVehicleModelName;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                _log.Debug("POLICE_PATROL_VEHICLE_UNSELECTED",
                    "Patrol started without a selected Police vehicle.");
                return false;
            }

            Ped player = Game.Player.Character;
            Vehicle occupiedPoliceVehicle = player == null || !player.Exists()
                ? null : player.CurrentVehicle;
            bool playerAlreadyHasPoliceVehicle = occupiedPoliceVehicle != null
                && occupiedPoliceVehicle.Exists()
                && IsKnownPoliceVehicle(occupiedPoliceVehicle);
            bool managedUtilityExists = _managedPoliceVehicle != null
                && _managedPoliceVehicle.Exists();
            bool playerAlreadyHasManagedUtility = managedUtilityExists
                && occupiedPoliceVehicle != null && occupiedPoliceVehicle.Exists()
                && occupiedPoliceVehicle.Handle == _managedPoliceVehicle.Handle;
            if (_personalUtilityRecoveryState != PersonalUtilityRecoveryState.None)
            {
                LastOperationMessage = "Your managed Police Utility is still being grounded and settled. Enter it after the recovery finishes, then press "
                    + _config.PoliceControls.PatrolKey + " to start Patrol.";
                _log.Runtime(
                    "POLICE_PATROL_START_BLOCKED_UTILITY_RECOVERY",
                    "ManagedVehicle=" + _personalUtilityRecoveryVehicleHandle
                    + "; RecoveryState=" + _personalUtilityRecoveryState
                    + "; Result=PatrolNotStartedUntilManagedUtilityEntered");
                return false;
            }
            if (_personalUtilityDeliveryState != PersonalUtilityDeliveryState.None)
            {
                LastOperationMessage = "Your Police Utility is still being delivered. Wait for it to park, enter it, then press "
                    + _config.PoliceControls.PatrolKey + " to start Patrol.";
                _log.Runtime(
                    "POLICE_PATROL_START_BLOCKED_UTILITY_DELIVERY",
                    "ManagedVehicle=" + _personalUtilityDeliveryVehicleHandle
                    + "; PlayerVehicle=" + (occupiedPoliceVehicle == null ? 0 : occupiedPoliceVehicle.Handle)
                    + "; DeliveryState=" + _personalUtilityDeliveryState
                    + "; Result=PatrolNotStarted");
                return false;
            }
            if (managedUtilityExists && !playerAlreadyHasManagedUtility)
            {
                LSPDPoliceStationDefinition stationPickup;
                LastOperationMessage = IsManagedUtilityAtSelectedStation(
                        _managedPoliceVehicle, out stationPickup)
                    ? BuildStationUtilityPickupMessage(stationPickup, false)
                    : "Enter your managed Police Utility before starting Patrol.";
                _log.Runtime(
                    "POLICE_PATROL_START_BLOCKED_UTILITY_NOT_ENTERED",
                    "ManagedVehicle=" + _managedPoliceVehicle.Handle
                    + "; PlayerVehicle=" + (occupiedPoliceVehicle == null ? 0 : occupiedPoliceVehicle.Handle)
                    + "; Result=PatrolNotStartedUntilManagedUtilityEntered");
                return false;
            }
            LSPDPoliceStationDefinition station;
            if (!playerAlreadyHasPoliceVehicle
                && IsManagedUtilityAwaitingStationPickup(player, out station))
            {
                LogStationUtilityPatrolStartBlocked(station);
                return false;
            }

            // reportFailure=false keeps a valid occupied Police vehicle and
            // refuses to replace it. A new owned vehicle is created only when
            // the player is on foot or in an unrelated vehicle.
            bool ready = SpawnPoliceVehicle(modelName, false);
            // SpawnPoliceVehicle may just have succeeded by using the
            // selected-Station fallback. Recheck after preparation so an
            // unreferenced or newly-created remote Station Utility cannot
            // accidentally start Patrol before the Player fetches it.
            if (ready && !playerAlreadyHasPoliceVehicle
                && IsManagedUtilityAwaitingStationPickup(player, out station))
            {
                LogStationUtilityPatrolStartBlocked(station);
                return false;
            }
            if (ready && (_managedPoliceVehicle == null || !_managedPoliceVehicle.Exists()
                || player == null || !player.Exists()
                || player.CurrentVehicle == null || !player.CurrentVehicle.Exists()
                || player.CurrentVehicle.Handle != _managedPoliceVehicle.Handle))
            {
                LastOperationMessage = "Enter your managed Police Utility before starting Patrol.";
                _log.Runtime(
                    "POLICE_PATROL_START_BLOCKED_UTILITY_NOT_ENTERED",
                    "ManagedVehicle=" + (_managedPoliceVehicle == null ? 0 : _managedPoliceVehicle.Handle)
                    + "; PlayerVehicle=" + (player == null || player.CurrentVehicle == null
                        ? 0 : player.CurrentVehicle.Handle)
                    + "; Result=PatrolNotStartedUntilManagedUtilityEntered");
                return false;
            }
            if (ready)
                _log.Runtime(
                    "POLICE_PATROL_VEHICLE_READY",
                    "Model=" + modelName + "; ReusedOrPrepared=true");
            return ready;
        }

        private bool IsManagedUtilityAwaitingStationPickup(
            Ped player,
            out LSPDPoliceStationDefinition station)
        {
            return IsManagedUtilityAtSelectedStation(_managedPoliceVehicle, out station)
                && !IsPlayerNearSelectedStationGarage(player, station);
        }

        private void LogStationUtilityPatrolStartBlocked(
            LSPDPoliceStationDefinition station)
        {
            _log.Runtime(
                "POLICE_PATROL_START_BLOCKED_STATION_UTILITY_PICKUP",
                "Station Utility remains parked at its selected garage; the Player must reach and enter that same vehicle before starting Patrol. Station="
                + station.Id
                + "; Utility=" + _managedPoliceVehicle.Handle
                + "; GarageAccessDistance=" + SelectedStationGarageAccessDistance.ToString("0", CultureInfo.InvariantCulture));
        }

        private string BuildPoliceAuthorityEntryMessage(bool profileApplied)
        {
            if (!_profile.StationSetupComplete)
                return "Go to a Preferred Station and Apply a Police Duty.";

            string assetStatus = profileApplied
                ? string.Empty
                : HasPendingSavedProfileAssets
                    ? "Some saved Police assets are still loading. "
                    : "Some saved Police assets could not be applied. ";

            LSPDPoliceStationDefinition station;
            if (IsManagedUtilityAtSelectedStation(_managedPoliceVehicle, out station))
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists()
                    || player.CurrentVehicle == null || !player.CurrentVehicle.Exists()
                    || player.CurrentVehicle.Handle != _managedPoliceVehicle.Handle)
                    return assetStatus + BuildStationUtilityPickupMessage(
                        station, !IsPlayerNearSelectedStationGarage(player, station));
            }

            if (!profileApplied)
            {
                string retry = HasPendingSavedProfileAssets
                    ? "Wait for the asset-ready message, then "
                    : string.Empty;
                return "Police Mode is active. " + assetStatus + retry
                    + "Press " + _config.PoliceControls.DispatchMenuKey
                    + " for Dispatch Framework and choose Spawn the Personal Utility; press "
                    + _config.PoliceControls.PatrolKey + " when the Utility is ready.";
            }

            return "Police Mode is active. Press "
                + _config.PoliceControls.PatrolKey + " to start Patrol.";
        }

        private string BuildStationUtilityPickupMessage(
            LSPDPoliceStationDefinition station,
            bool closePoliceMenu)
        {
            string stationName = station == null
                ? "the selected Station"
                : station.DisplayName;
            return "Your Police Utility is ready at the " + stationName + " garage. "
                + (closePoliceMenu ? "Close this menu and " : string.Empty)
                + "travel to its map marker, enter that same Utility, then press "
                + _config.PoliceControls.PatrolKey + " to start Patrol.";
        }

        private void EnsureManagedStationUtilityGuidance()
        {
            if (!_config.ShowWaypoint || Dispatch.HasIncident || Convoy.Active)
                return;

            Vehicle utility = _managedPoliceVehicle;
            LSPDPoliceStationDefinition station;
            if (!IsManagedUtilityAtSelectedStation(utility, out station))
                return;

            EnsurePreferredPoliceVehicleBlip(utility);
            try
            {
                World.WaypointPosition = utility.Position;
                _log.Runtime(
                    "POLICE_PERSONAL_UTILITY_STATION_GPS_SET",
                    "Station=" + station.Id
                    + "; Vehicle=" + utility.Handle
                    + "; Position=" + FormatVehiclePosition(utility.Position)
                    + "; Target=ManagedUtilityParking");
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PERSONAL_UTILITY_STATION_GPS_FAILED", ex);
            }
        }

        private string GetPatrolUtilityPreparationMessage()
        {
            LSPDPoliceStationDefinition station;
            if (IsManagedUtilityAtSelectedStation(_managedPoliceVehicle, out station))
                return BuildStationUtilityPickupMessage(station, false);

            return "Your Police Utility is not ready yet. Press "
                + _config.PoliceControls.DispatchMenuKey
                + " for Dispatch Framework, choose Spawn the Personal Utility, then press "
                + _config.PoliceControls.PatrolKey + " to start Patrol.";
        }

        private bool IsManagedUtilityAtSelectedStation(
            Vehicle vehicle,
            out LSPDPoliceStationDefinition station)
        {
            station = _profile.FindStation(_profile.Selection.StationId);
            if (vehicle == null || !vehicle.Exists()
                || station == null || !station.ExteriorSafe)
                return false;

            if (string.IsNullOrWhiteSpace(_profile.SelectedVehicleModelName)
                || vehicle.Model.Hash != new Model(_profile.SelectedVehicleModelName).Hash)
                return false;

            Vector3 authoredParking = new Vector3(
                station.VehicleX, station.VehicleY, station.VehicleZ);
            if (authoredParking == Vector3.Zero
                || float.IsNaN(authoredParking.X) || float.IsInfinity(authoredParking.X)
                || float.IsNaN(authoredParking.Y) || float.IsInfinity(authoredParking.Y)
                || float.IsNaN(authoredParking.Z) || float.IsInfinity(authoredParking.Z))
                return false;

            return Distance2D(vehicle.Position, authoredParking)
                <= SelectedStationParkingMatchDistance;
        }

        private bool IsPlayerNearSelectedStationGarage(
            Ped player,
            LSPDPoliceStationDefinition station)
        {
            if (player == null || !player.Exists()
                || station == null || !station.ExteriorSafe)
                return false;

            Vector3 authoredParking = new Vector3(
                station.VehicleX, station.VehicleY, station.VehicleZ);
            if (authoredParking == Vector3.Zero
                || float.IsNaN(authoredParking.X) || float.IsInfinity(authoredParking.X)
                || float.IsNaN(authoredParking.Y) || float.IsInfinity(authoredParking.Y)
                || float.IsNaN(authoredParking.Z) || float.IsInfinity(authoredParking.Z))
                return false;

            return Distance2D(player.Position, authoredParking)
                <= SelectedStationGarageAccessDistance;
        }

        private static bool IsPreferredPoliceVehiclePlacementFailure(string message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && (message.StartsWith(
                        "No safe parking place was found",
                        StringComparison.Ordinal)
                    || message.StartsWith(
                        "No confirmed safe parking pose was found",
                        StringComparison.Ordinal));
        }

        private static bool IsSelectedStationPoliceVehiclePlacement(string source)
        {
            return string.Equals(source, "StationParking", StringComparison.Ordinal)
                || string.Equals(source, "StationExteriorParking", StringComparison.Ordinal)
                || string.Equals(source, "SelectedStationParkingFallback", StringComparison.Ordinal);
        }

        private bool SpawnPoliceVehicle(
            string modelName,
            bool reportFailure,
            bool forceReplaceManagedVehicle = false,
            bool allowSelectedStationFallback = true)
        {
            _lastPreferredPoliceVehicleSpawnSource = string.Empty;
            Model model = new Model(modelName);
            bool releaseModel = false;
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return Fail("POLICE_PLAYER_UNAVAILABLE", "The player character is unavailable.");

                // Check the occupied vehicle before requesting a model. An addon
                // model may be valid only because it is already streamed in; a
                // failed request must never force a second patrol vehicle.
                Vehicle currentVehicle = player.CurrentVehicle;
                bool retainCurrentPatrolVehicle = !reportFailure
                    && (_config.Police.Patrol.ReuseCurrentPoliceVehicle
                        || _config.Police.Patrol.PreventDuplicatePersonalVehicle);
                if (retainCurrentPatrolVehicle
                    && currentVehicle != null && currentVehicle.Exists()
                    && IsKnownPoliceVehicle(currentVehicle))
                {
                    Vehicle previousManaged = _managedPoliceVehicle;
                    if (previousManaged != null && previousManaged.Exists()
                        && previousManaged.Handle != currentVehicle.Handle)
                        RetireOwnedVehicle(previousManaged, currentVehicle);
                    _managedPoliceVehicle = currentVehicle;
                    _lastPreferredPoliceVehicleSpawnSource = "ExistingPoliceVehicle";
                    ClearPreferredPoliceVehiclePlacementRetry();
                    EnsurePreferredPoliceVehicleBlip(currentVehicle);
                    _log.Runtime(
                        "POLICE_PATROL_VEHICLE_REUSED",
                        "Existing Police vehicle retained; Handle=" + currentVehicle.Handle);
                    return true;
                }

                if (!model.IsValid || !model.IsVehicle)
                {
                    if (reportFailure)
                        Fail("POLICE_VEHICLE_MODEL_INVALID", "Vehicle model is not available in this game build: " + modelName + ".");
                    else
                        _log.Debug("POLICE_VEHICLE_MODEL_INVALID", modelName);
                    return false;
                }

                if (currentVehicle != null && currentVehicle.Exists()
                    && currentVehicle.Model.Hash == model.Hash
                    && (reportFailure
                        || _config.Police.Patrol.ReuseCurrentPoliceVehicle
                        || _config.Police.Patrol.PreventDuplicatePersonalVehicle))
                {
                    if (_managedPoliceVehicle == null || !_managedPoliceVehicle.Exists()
                        || _managedPoliceVehicle.Handle == currentVehicle.Handle)
                        _managedPoliceVehicle = currentVehicle;
                    _lastPreferredPoliceVehicleSpawnSource = "ExistingPoliceVehicle";
                    ClearPreferredPoliceVehiclePlacementRetry();
                    EnsurePreferredPoliceVehicleBlip(
                        _managedPoliceVehicle != null && _managedPoliceVehicle.Exists()
                            ? _managedPoliceVehicle : currentVehicle);
                    return true;
                }

                if (_managedPoliceVehicle != null && _managedPoliceVehicle.Exists()
                    && _managedPoliceVehicle.Model.Hash == model.Hash
                    && !forceReplaceManagedVehicle
                    && (reportFailure
                        || _config.Police.Patrol.PreventDuplicatePersonalVehicle))
                {
                    _lastPreferredPoliceVehicleSpawnSource = "ExistingPoliceVehicle";
                    ClearPreferredPoliceVehiclePlacementRetry();
                    EnsurePreferredPoliceVehicleBlip(_managedPoliceVehicle);
                    return true;
                }

                if (!model.IsLoaded)
                {
                    // Patrol/menu actions must never block the ScriptHook UI
                    // thread while an addon model streams.
                    model.Request();
                    // Keep the streamed request alive for the pending-profile
                    // retry. Releasing it here made addon vehicles repeatedly
                    // fail to become ready for Patrol.
                    if (reportFailure)
                        Fail("POLICE_VEHICLE_MODEL_LOAD_PENDING", "Police vehicle is loading. Select it again in a moment: " + modelName + ".");
                    return false;
                }

                releaseModel = true;
                Vector3 spawn;
                float spawnHeading;
                string spawnSource;
                string placementFailure;
                string stationParkingFailure = string.Empty;
                LSPDPoliceStationDefinition station = _profile.FindStation(
                    _profile.Selection.StationId);
                string deferredPlacementCategory;
                string deferredPlacementMessage;
                if (TryGetPreferredPoliceVehiclePlacementRetry(
                        player, modelName, station, forceReplaceManagedVehicle,
                        allowSelectedStationFallback,
                        out deferredPlacementCategory, out deferredPlacementMessage))
                    return FailPreferredPoliceVehiclePlacement(
                        deferredPlacementCategory,
                        deferredPlacementMessage,
                        reportFailure);

                bool spawnResolved = TryResolvePreferredPoliceVehicleSpawn(
                    player,
                    station,
                    out spawn,
                    out spawnHeading,
                    out spawnSource,
                    out placementFailure);
                string nearbyPlacementFailure = placementFailure;
                if (!spawnResolved && allowSelectedStationFallback && station != null
                    && !_stationSetupCompletionPending)
                {
                    if (TryResolveStationParkingPosition(
                        player, station, true, out spawn, out spawnHeading,
                        out stationParkingFailure))
                    {
                        spawnSource = "SelectedStationParkingFallback";
                        spawnResolved = true;
                        _log.Runtime(
                            "POLICE_PREFERRED_VEHICLE_STATION_FALLBACK_SELECTED",
                            "Station=" + station.Id
                            + "; Model=" + modelName
                            + "; Reason=No nearby roadside or parked-ground candidate passed bounded validation.");
                    }
                    else if (!string.IsNullOrWhiteSpace(stationParkingFailure))
                    {
                        placementFailure = (string.IsNullOrWhiteSpace(placementFailure)
                            ? string.Empty
                            : "Nearby placement failed: " + placementFailure + " ")
                            + "Selected Station parking fallback failed: "
                            + stationParkingFailure;
                    }
                }
                if (!spawnResolved)
                {
                    string message = "No safe parking place was found for "
                        + _profile.SelectedVehicleDisplayName + ". " + placementFailure;
                    RememberPreferredPoliceVehiclePlacementRetry(
                        player, modelName, station, forceReplaceManagedVehicle,
                        allowSelectedStationFallback,
                        "POLICE_VEHICLE_SAFE_SPAWN_UNAVAILABLE", message);
                    return FailPreferredPoliceVehiclePlacement(
                        "POLICE_VEHICLE_SAFE_SPAWN_UNAVAILABLE",
                        message,
                        reportFailure);
                }

                Vector3 requestedSpawn = spawn;
                float requestedHeading = spawnHeading;
                List<Vector3> rejectedPositions = new List<Vector3>();
                List<float> rejectedHeadings = new List<float>();
                Vehicle created = null;
                string lastPlacementFailure = string.Empty;
                string preCreateValidation = string.Empty;
                string finalPoseFailure = string.Empty;
                bool enhancedValidationConfirmed = false;
                bool finalPoseInconclusive = false;
                bool headingAdjusted = false;
                bool stationParkingFallbackSelected =
                    string.Equals(spawnSource, "StationParking", StringComparison.Ordinal)
                    || string.Equals(spawnSource, "SelectedStationParkingFallback", StringComparison.Ordinal);
                const int maximumCreatedCandidates = 12;
                for (int attempt = 0; attempt < maximumCreatedCandidates; attempt++)
                {
                    if (!TryFindSafePreferredVehiclePose(
                        model, player, requestedSpawn, requestedHeading,
                        stationParkingFallbackSelected,
                        out spawn, out spawnHeading,
                        out enhancedValidationConfirmed,
                        out preCreateValidation,
                        rejectedPositions, rejectedHeadings))
                    {
                        string stationFallbackFailure = string.Empty;
                        if (allowSelectedStationFallback
                            && !stationParkingFallbackSelected
                            && station != null && !_stationSetupCompletionPending
                            && TryResolveStationParkingPosition(
                                player, station, true, out spawn, out spawnHeading,
                                out stationFallbackFailure))
                        {
                            if (string.IsNullOrWhiteSpace(nearbyPlacementFailure))
                                nearbyPlacementFailure = preCreateValidation;
                            requestedSpawn = spawn;
                            requestedHeading = spawnHeading;
                            spawnSource = "SelectedStationParkingFallback";
                            stationParkingFallbackSelected = true;
                            rejectedPositions.Clear();
                            rejectedHeadings.Clear();
                            _log.Runtime(
                                "POLICE_PREFERRED_VEHICLE_STATION_FALLBACK_SELECTED",
                                "Station=" + station.Id
                                + "; Model=" + modelName
                                + "; Reason=Nearby enhanced footprint validation was unsafe or inconclusive; selected authored Station parking.");
                            attempt--;
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(stationFallbackFailure))
                            stationParkingFailure = stationFallbackFailure;
                        else if (stationParkingFallbackSelected)
                            stationParkingFailure = preCreateValidation;
                        lastPlacementFailure = preCreateValidation;
                        if (!string.IsNullOrWhiteSpace(stationFallbackFailure))
                            lastPlacementFailure = (string.IsNullOrWhiteSpace(preCreateValidation)
                                ? string.Empty
                                : "Nearby placement failed: " + preCreateValidation + "; ")
                                + "Selected Station parking fallback failed: "
                                + stationFallbackFailure;
                        break;
                    }

                    Vehicle candidate = World.CreateVehicle(model, spawn, spawnHeading);
                    if (candidate == null || !candidate.Exists())
                        return Fail("POLICE_VEHICLE_SPAWN_FAILED",
                            "Police vehicle could not be created: " + modelName + ".");

                    candidate.IsPersistent = true;
                    candidate.PlaceOnGround();

                    float initialHeadingDrift = PreferredVehicleHeadingDifference(
                        candidate.Heading, spawnHeading);
                    if (initialHeadingDrift > 1.0f && initialHeadingDrift <= 10.0f)
                    {
                        try
                        {
                            candidate.Heading = spawnHeading;
                            headingAdjusted = PreferredVehicleHeadingDifference(
                                candidate.Heading, spawnHeading) <= 1.0f;
                        }
                        catch
                        {
                            // A usable grounded vehicle remains available even
                            // when GTA refuses a final heading correction.
                        }
                    }

                    PreferredVehiclePlacementResult finalResult;
                    string settleEvidence = string.Empty;
                    if (stationParkingFallbackSelected
                        && !TrySettleCreatedStationVehicle(
                            candidate, out settleEvidence))
                    {
                        finalResult = PreferredVehiclePlacementResult.ConfirmedUnsafe;
                        finalPoseFailure = settleEvidence;
                    }
                    else
                    {
                        finalResult = TryValidateCreatedPreferredVehiclePose(
                            candidate, player, spawn, spawnHeading,
                            stationParkingFallbackSelected,
                            out finalPoseFailure);
                        if (!string.IsNullOrWhiteSpace(settleEvidence))
                            finalPoseFailure += "; " + settleEvidence;
                    }
                    if (finalResult == PreferredVehiclePlacementResult.Inconclusive
                        && !stationParkingFallbackSelected
                        && allowSelectedStationFallback
                        && station != null && !_stationSetupCompletionPending)
                    {
                        string stationFallbackFailure;
                        if (TryResolveStationParkingPosition(
                            player, station, true, out spawn, out spawnHeading,
                            out stationFallbackFailure))
                        {
                            nearbyPlacementFailure = finalPoseFailure;
                            try { candidate.Delete(); } catch { }
                            requestedSpawn = spawn;
                            requestedHeading = spawnHeading;
                            spawnSource = "SelectedStationParkingFallback";
                            stationParkingFallbackSelected = true;
                            rejectedPositions.Clear();
                            rejectedHeadings.Clear();
                            _log.Runtime(
                                "POLICE_PREFERRED_VEHICLE_STATION_FALLBACK_SELECTED",
                                "Station=" + station.Id
                                + "; Model=" + modelName
                                + "; Reason=Created nearby pose remained inconclusive; retrying validated authored Station parking.");
                            attempt--;
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(stationFallbackFailure))
                            stationParkingFailure = stationFallbackFailure;
                        finalPoseFailure += "; Selected Station parking fallback failed: "
                            + stationFallbackFailure;
                    }

                    if (finalResult == PreferredVehiclePlacementResult.ConfirmedSafe
                        || (stationParkingFallbackSelected
                            && finalResult == PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive))
                    {
                        created = candidate;
                        finalPoseInconclusive = finalResult
                            == PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive;
                        break;
                    }

                    string evidence = "Model=" + modelName
                        + "; Source=" + spawnSource
                        + "; RequestedPosition=" + FormatVehiclePosition(requestedSpawn)
                        + "; RequestedHeading=" + requestedHeading.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; CandidatePosition=" + FormatVehiclePosition(spawn)
                        + "; CandidateHeading=" + spawnHeading.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; FinalPosition=" + FormatVehiclePosition(candidate.Position)
                        + "; FinalHeading=" + candidate.Heading.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Failure=" + finalPoseFailure
                        + "; NextCandidate=" + (attempt + 1 < maximumCreatedCandidates);
                    _log.Runtime("POLICE_PREFERRED_VEHICLE_PLACEMENT_REJECTED", evidence);
                    try { candidate.Delete(); } catch { }
                    rejectedPositions.Add(spawn);
                    rejectedHeadings.Add(spawnHeading);
                    lastPlacementFailure = finalPoseFailure;
                    if (stationParkingFallbackSelected)
                        stationParkingFailure = finalPoseFailure;
                }

                if (created == null)
                {
                    if (stationParkingFallbackSelected)
                    {
                        string selectedStationFailure = string.IsNullOrWhiteSpace(stationParkingFailure)
                            ? lastPlacementFailure
                            : stationParkingFailure;
                        lastPlacementFailure = (string.IsNullOrWhiteSpace(nearbyPlacementFailure)
                            ? string.Empty
                            : "Nearby placement failed: " + nearbyPlacementFailure + "; ")
                            + "Selected Station (" + (station == null ? "unknown" : station.Id)
                            + ") parking fallback failed: " + selectedStationFailure;
                    }
                    string message = "No confirmed safe parking pose was found for "
                        + modelName + ". " + lastPlacementFailure;
                    RememberPreferredPoliceVehiclePlacementRetry(
                        player, modelName, station, forceReplaceManagedVehicle,
                        allowSelectedStationFallback,
                        "POLICE_VEHICLE_FINAL_POSE_REJECTED", message);
                    return FailPreferredPoliceVehiclePlacement(
                        "POLICE_VEHICLE_FINAL_POSE_REJECTED",
                        message,
                        reportFailure);
                }
                Vehicle previous = _managedPoliceVehicle;
                _managedPoliceVehicle = created;
                _lastPreferredPoliceVehicleSpawnSource = spawnSource;
                ClearPreferredPoliceVehiclePlacementRetry();
                EnsurePreferredPoliceVehicleBlip(created);
                BeginPreferredPoliceVehicleFade(created);

                if (previous != null && previous.Exists() && previous.Handle != created.Handle)
                    RetireOwnedVehicle(previous, player.CurrentVehicle);

                string enhancedValidation = enhancedValidationConfirmed
                    && !finalPoseInconclusive
                    ? "Confirmed"
                    : "Inconclusive";
                string validationDetail = string.Join(" | ", new[]
                {
                    enhancedValidationConfirmed ? string.Empty : preCreateValidation,
                    finalPoseInconclusive ? finalPoseFailure : string.Empty
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                _lastPreferredPoliceVehiclePlacementFailureSignature = string.Empty;

                _log.Runtime(
                    "POLICE_PREFERRED_VEHICLE_PARKED",
                    "Model=" + modelName
                    + "; Source=" + spawnSource
                    + "; Station=" + (station == null ? string.Empty : station.Id)
                    + "; DistanceFromPlayer=" + player.Position.DistanceTo(created.Position).ToString("0.0", CultureInfo.InvariantCulture)
                    + "; RequestedPosition=" + FormatVehiclePosition(requestedSpawn)
                    + "; FinalPosition=" + FormatVehiclePosition(created.Position)
                    + "; RequestedHeading=" + requestedHeading.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; FinalHeading=" + created.Heading.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; PoseAdjusted=" + (Distance2D(requestedSpawn, spawn) > 0.25f
                        || PreferredVehicleHeadingDifference(requestedHeading, spawnHeading) > 1.0f)
                    + "; FinalPoseAdjusted=" + (Distance2D(spawn, created.Position) > 0.25f
                        || PreferredVehicleHeadingDifference(spawnHeading, created.Heading) > 1.0f
                        || headingAdjusted)
                    + "; EnhancedValidation=" + enhancedValidation
                    + "; HeadingAdjusted=" + headingAdjusted
                    + (string.IsNullOrWhiteSpace(validationDetail)
                        ? string.Empty
                        : "; ValidationDetail=" + validationDetail)
                    + "; Handle=" + created.Handle);
                return true;
            }
            catch (Exception ex)
            {
                return Fail("POLICE_VEHICLE_ERROR", "Police vehicle could not be created: " + modelName + ".", ex);
            }
            finally
            {
                try
                {
                    if (releaseModel && model.IsValid)
                        model.MarkAsNoLongerNeeded();
                }
                catch { }
            }
        }

        private bool TryFindSafePreferredVehiclePose(
            Model model,
            Ped player,
            Vector3 requestedPosition,
            float requestedHeading,
            bool allowInconclusiveCandidate,
            out Vector3 safePosition,
            out float safeHeading,
            out bool enhancedValidationConfirmed,
            out string failure,
            IList<Vector3> rejectedPositions,
            IList<float> rejectedHeadings)
        {
            safePosition = Vector3.Zero;
            safeHeading = 0.0f;
            enhancedValidationConfirmed = false;
            failure = string.Empty;
            if (player == null || !player.Exists()
                || !model.IsValid || !model.IsVehicle
                || float.IsNaN(requestedHeading) || float.IsInfinity(requestedHeading))
            {
                failure = "The Player, vehicle model, or requested heading is invalid.";
                return false;
            }

            float requestedDistance = Distance2D(requestedPosition, player.Position);
            float maximumDistanceFromPlayer = requestedDistance + 5.25f;
            float[] searchRadii = { 0.0f, 2.5f, 5.0f };
            string lastFailure = "No candidate footprint was checked.";
            bool hasGroundInconclusiveCandidate = false;
            Vector3 groundInconclusivePosition = Vector3.Zero;
            float groundInconclusiveHeading = 0.0f;
            string groundInconclusiveFailure = string.Empty;
            bool hasNearbyInconclusiveCandidate = false;
            string nearbyInconclusiveFailure = string.Empty;
            for (int ring = 0; ring < searchRadii.Length; ring++)
            {
                float radius = searchRadii[ring];
                int probes = radius <= 0.0f ? 1 : 8;
                for (int probe = 0; probe < probes; probe++)
                {
                    Vector3 candidate = requestedPosition;
                    if (radius > 0.0f)
                    {
                        double radians = probe * (Math.PI / 4.0);
                        candidate.X += (float)Math.Cos(radians) * radius;
                        candidate.Y += (float)Math.Sin(radians) * radius;
                    }
                    if (Distance2D(candidate, player.Position) > maximumDistanceFromPlayer)
                        continue;

                    Vector3 groundedCandidate;
                    string pointFailure;
                    bool isValidatedLegacyCandidate = ring == 0 && probe == 0;
                    if (isValidatedLegacyCandidate)
                    {
                        // TryResolvePreferredPoliceVehicleSpawn already
                        // validated the legacy candidate. Do not make that
                        // reliable result depend on a second ground query.
                        groundedCandidate = requestedPosition;
                    }
                    else
                    {
                        float candidateReferenceHeight = allowInconclusiveCandidate
                            ? requestedPosition.Z
                            : player.Position.Z;
                        if (!TryValidatePreferredVehicleSpawnPoint(
                            candidate, candidateReferenceHeight, player, true,
                            allowInconclusiveCandidate,
                            out groundedCandidate, out pointFailure))
                        {
                            if (!string.IsNullOrWhiteSpace(pointFailure))
                                lastFailure = pointFailure;
                            continue;
                        }
                    }

                    List<float> headings = new List<float>(3)
                    {
                        NormalizePreferredVehicleHeading(requestedHeading)
                    };
                    try
                    {
                        float roadHeading;
                        Vector3 roadNode = World.GetNextPositionOnStreetWithHeading(
                            groundedCandidate, out roadHeading);
                        float roadSeparation = roadNode == Vector3.Zero
                            ? float.MaxValue
                            : Distance2D(groundedCandidate, roadNode);
                        if (!float.IsNaN(roadHeading) && !float.IsInfinity(roadHeading)
                            && roadSeparation >= 1.5f && roadSeparation <= 18.0f)
                        {
                            AddPreferredVehicleHeading(headings, roadHeading);
                            AddPreferredVehicleHeading(headings, roadHeading + 180.0f);
                        }
                    }
                    catch
                    {
                        // The requested station or Player heading remains a
                        // candidate when GTA cannot provide a nearby road angle.
                    }

                    for (int headingIndex = 0; headingIndex < headings.Count; headingIndex++)
                    {
                        string footprintFailure;
                        float candidateHeading = headings[headingIndex];
                        if (IsRejectedPreferredVehiclePose(
                            groundedCandidate, candidateHeading,
                            rejectedPositions, rejectedHeadings))
                            continue;

                        PreferredVehiclePlacementResult validation =
                            TryValidatePreferredVehicleFootprint(
                                model, player, groundedCandidate, candidateHeading,
                                null, allowInconclusiveCandidate, out footprintFailure);
                        if (validation == PreferredVehiclePlacementResult.ConfirmedSafe)
                        {
                            safePosition = groundedCandidate;
                            safeHeading = candidateHeading;
                            enhancedValidationConfirmed = true;
                            failure = string.Empty;
                            return true;
                        }

                        if (validation == PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive
                            && allowInconclusiveCandidate)
                        {
                            if (!hasGroundInconclusiveCandidate)
                            {
                                hasGroundInconclusiveCandidate = true;
                                groundInconclusivePosition = groundedCandidate;
                                groundInconclusiveHeading = candidateHeading;
                                groundInconclusiveFailure = footprintFailure;
                            }
                        }
                        else if (validation == PreferredVehiclePlacementResult.Inconclusive
                            && !hasNearbyInconclusiveCandidate)
                        {
                            hasNearbyInconclusiveCandidate = true;
                            nearbyInconclusiveFailure = footprintFailure;
                        }
                        else if (!string.IsNullOrWhiteSpace(footprintFailure))
                            lastFailure = footprintFailure;
                    }
                }
            }

            if (hasGroundInconclusiveCandidate)
            {
                safePosition = groundInconclusivePosition;
                safeHeading = groundInconclusiveHeading;
                failure = "Vehicle footprint clearance was confirmed; only center/corner ground support remained inconclusive. "
                    + groundInconclusiveFailure;
                return true;
            }

            if (hasNearbyInconclusiveCandidate && !allowInconclusiveCandidate)
            {
                failure = "Enhanced footprint validation was inconclusive for nearby parking; selected Station parking fallback is required. "
                    + nearbyInconclusiveFailure;
                return false;
            }

            failure = "Checked the selected position and nearby alternatives within "
                + maximumDistanceFromPlayer.ToString("0.0", CultureInfo.InvariantCulture)
                + " metres of the Player. Confirmed rejection: " + lastFailure;
            return false;
        }

        private static bool IsRejectedPreferredVehiclePose(
            Vector3 candidate,
            float heading,
            IList<Vector3> rejectedPositions,
            IList<float> rejectedHeadings)
        {
            if (rejectedPositions == null || rejectedHeadings == null)
                return false;
            int count = Math.Min(rejectedPositions.Count, rejectedHeadings.Count);
            for (int index = 0; index < count; index++)
                if (Distance2D(candidate, rejectedPositions[index]) < 0.25f
                    && PreferredVehicleHeadingDifference(
                        heading, rejectedHeadings[index]) < 1.0f)
                    return true;
            return false;
        }

        private PreferredVehiclePlacementResult TryValidatePreferredVehicleFootprint(
            Model model,
            Ped player,
            Vector3 center,
            float heading,
            Entity ignoredVehicle,
            bool allowInconclusiveGroundSupport,
            out string failure)
        {
            failure = string.Empty;
            if (!model.IsValid || !model.IsVehicle
                || center == Vector3.Zero
                || float.IsNaN(heading) || float.IsInfinity(heading))
            {
                failure = "Vehicle model, position, or heading is invalid.";
                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
            }

            List<string> inconclusiveEvidence = new List<string>();
            List<string> groundSupportInconclusiveEvidence = new List<string>();
            try
            {
                Vector3 minimum;
                Vector3 maximum;
                try
                {
                    using (OutputArgument minimumOutput = new OutputArgument())
                    using (OutputArgument maximumOutput = new OutputArgument())
                    {
                        Function.Call(Hash.GET_MODEL_DIMENSIONS,
                            model.Hash, minimumOutput, maximumOutput);
                        minimum = minimumOutput.GetResult<Vector3>();
                        maximum = maximumOutput.GetResult<Vector3>();
                    }
                }
                catch (Exception ex)
                {
                    failure = "GTA model dimensions were unavailable: " + ex.GetType().Name + ".";
                    return PreferredVehiclePlacementResult.Inconclusive;
                }
                float halfWidth = Math.Abs(maximum.X - minimum.X) * 0.5f;
                float halfLength = Math.Abs(maximum.Y - minimum.Y) * 0.5f;
                if (halfWidth < 0.35f || halfLength < 0.75f
                    || float.IsNaN(halfWidth) || float.IsInfinity(halfWidth)
                    || float.IsNaN(halfLength) || float.IsInfinity(halfLength))
                {
                    failure = "GTA returned unusable vehicle body dimensions.";
                    return PreferredVehiclePlacementResult.Inconclusive;
                }

                float centerGroundZ = center.Z;
                Vector3 centerNormal = Vector3.Zero;
                bool centerGroundConfirmed = false;
                try
                {
                    centerGroundConfirmed = World.GetGroundHeightAndNormal(
                        center, out centerGroundZ, out centerNormal);
                }
                catch (Exception ex)
                {
                    (allowInconclusiveGroundSupport
                        ? groundSupportInconclusiveEvidence : inconclusiveEvidence).Add(
                            "Center ground query unavailable: " + ex.GetType().Name);
                }
                if (!centerGroundConfirmed
                    || float.IsNaN(centerGroundZ) || float.IsInfinity(centerGroundZ)
                    || float.IsNaN(centerNormal.Z) || float.IsInfinity(centerNormal.Z))
                    (allowInconclusiveGroundSupport
                        ? groundSupportInconclusiveEvidence : inconclusiveEvidence).Add(
                            "GTA could not confirm level ground under the vehicle center.");
                else if (centerNormal.Z < 0.5f
                    || Math.Abs(center.Z - centerGroundZ) > 2.0f)
                {
                    failure = "Confirmed unsupported ground under the vehicle center."
                        + "; NormalZ=" + centerNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; HeightDelta=" + Math.Abs(center.Z - centerGroundZ).ToString("0.00", CultureInfo.InvariantCulture);
                    return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                }

                float normalizedHeading = NormalizePreferredVehicleHeading(heading);
                double radians = normalizedHeading * (Math.PI / 180.0);
                Vector3 forward = new Vector3(
                    -(float)Math.Sin(radians), (float)Math.Cos(radians), 0.0f);
                Vector3 right = new Vector3(
                    (float)Math.Cos(radians), (float)Math.Sin(radians), 0.0f);
                float localCenterX = (minimum.X + maximum.X) * 0.5f;
                float localCenterY = (minimum.Y + maximum.Y) * 0.5f;
                float[] localX = { minimum.X, maximum.X };
                float[] localY = { minimum.Y, maximum.Y };
                Vector3[] corners = new Vector3[4];
                int cornerIndex = 0;
                for (int xIndex = 0; xIndex < localX.Length; xIndex++)
                {
                    for (int yIndex = 0; yIndex < localY.Length; yIndex++)
                    {
                        float x = localX[xIndex] - localCenterX;
                        float y = localY[yIndex] - localCenterY;
                        corners[cornerIndex++] = new Vector3(
                            center.X + right.X * x + forward.X * y,
                            center.Y + right.Y * x + forward.Y * y,
                            center.Z);
                    }
                }

                float[] clearanceHeights = { 0.65f, 1.25f, 1.9f };
                for (int index = 0; index < corners.Length; index++)
                {
                    float cornerGroundZ = corners[index].Z;
                    Vector3 cornerNormal = Vector3.Zero;
                    bool cornerGroundConfirmed = false;
                    try
                    {
                        cornerGroundConfirmed = World.GetGroundHeightAndNormal(
                            corners[index], out cornerGroundZ, out cornerNormal);
                    }
                    catch (Exception ex)
                    {
                        (allowInconclusiveGroundSupport
                            ? groundSupportInconclusiveEvidence : inconclusiveEvidence).Add(
                                "Corner ground query " + index.ToString(CultureInfo.InvariantCulture)
                                + " unavailable: " + ex.GetType().Name);
                    }
                    if (!cornerGroundConfirmed
                        || float.IsNaN(cornerGroundZ) || float.IsInfinity(cornerGroundZ)
                        || float.IsNaN(cornerNormal.Z) || float.IsInfinity(cornerNormal.Z))
                        (allowInconclusiveGroundSupport
                            ? groundSupportInconclusiveEvidence : inconclusiveEvidence).Add(
                                "Ground support at vehicle corner "
                                + index.ToString(CultureInfo.InvariantCulture) + " was inconclusive.");
                    else if (cornerNormal.Z < 0.5f
                        || (centerGroundConfirmed
                            && Math.Abs(cornerGroundZ - centerGroundZ) > 1.0f))
                    {
                        failure = "Confirmed unsupported ground under vehicle corner "
                            + index.ToString(CultureInfo.InvariantCulture) + ".";
                        return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                    }

                    for (int heightIndex = 0; heightIndex < clearanceHeights.Length; heightIndex++)
                    {
                        Vector3 from = center;
                        Vector3 to = corners[index];
                        from.Z += clearanceHeights[heightIndex];
                        to.Z += clearanceHeights[heightIndex];
                        try
                        {
                            RaycastResult hit = World.Raycast(
                                from, to,
                                IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Glass,
                                ignoredVehicle);
                            if (hit.DidHit)
                            {
                                failure = "Confirmed vehicle footprint obstruction by map geometry or an object.";
                                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                            }
                        }
                        catch (Exception ex)
                        {
                            inconclusiveEvidence.Add(
                                "Footprint obstruction raycast unavailable: " + ex.GetType().Name);
                        }
                    }
                }

                float footprintRadius = (float)Math.Sqrt(
                    halfWidth * halfWidth + halfLength * halfLength);
                try
                {
                    float roadHeading;
                    Vector3 roadNode = World.GetNextPositionOnStreetWithHeading(
                        center, out roadHeading);
                    if (roadNode == Vector3.Zero
                        || float.IsNaN(roadHeading) || float.IsInfinity(roadHeading))
                        inconclusiveEvidence.Add("A nearby road edge could not be confirmed.");
                    else if (Distance2D(center, roadNode)
                        < Math.Max(1.5f, halfWidth * 1.15f))
                    {
                        failure = "Confirmed vehicle footprint extends into the traffic lane.";
                        return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                    }
                }
                catch (Exception ex)
                {
                    inconclusiveEvidence.Add(
                        "Nearby road-edge query unavailable: " + ex.GetType().Name);
                }

                try
                {
                    Vehicle[] nearbyVehicles = World.GetNearbyVehicles(
                        center, footprintRadius + 4.0f);
                    if (nearbyVehicles == null)
                        inconclusiveEvidence.Add("Nearby vehicle-overlap query returned no evidence.");
                    else
                    {
                        foreach (Vehicle nearby in nearbyVehicles)
                        {
                            if (nearby == null || !nearby.Exists()
                                || (ignoredVehicle != null
                                    && nearby.Handle == ignoredVehicle.Handle))
                                continue;

                            Vector3 otherMinimum;
                            Vector3 otherMaximum;
                            try
                            {
                                using (OutputArgument minimumOutput = new OutputArgument())
                                using (OutputArgument maximumOutput = new OutputArgument())
                                {
                                    Function.Call(Hash.GET_MODEL_DIMENSIONS,
                                        nearby.Model.Hash, minimumOutput, maximumOutput);
                                    otherMinimum = minimumOutput.GetResult<Vector3>();
                                    otherMaximum = maximumOutput.GetResult<Vector3>();
                                }
                            }
                            catch (Exception ex)
                            {
                                inconclusiveEvidence.Add(
                                    "Nearby vehicle dimensions unavailable: " + ex.GetType().Name);
                                continue;
                            }
                            float otherHalfWidth = Math.Abs(
                                otherMaximum.X - otherMinimum.X) * 0.5f;
                            float otherHalfLength = Math.Abs(
                                otherMaximum.Y - otherMinimum.Y) * 0.5f;
                            if (otherHalfWidth < 0.25f || otherHalfLength < 0.5f
                                || float.IsNaN(otherHalfWidth) || float.IsInfinity(otherHalfWidth)
                                || float.IsNaN(otherHalfLength) || float.IsInfinity(otherHalfLength))
                            {
                                inconclusiveEvidence.Add(
                                    "Nearby vehicle dimensions were unusable.");
                                continue;
                            }
                            if (float.IsNaN(nearby.Heading) || float.IsInfinity(nearby.Heading))
                            {
                                inconclusiveEvidence.Add(
                                    "Nearby vehicle heading was unavailable.");
                                continue;
                            }

                            if (PreferredVehicleFootprintsOverlap(
                                center, normalizedHeading, halfWidth, halfLength,
                                nearby.Position, nearby.Heading,
                                otherHalfWidth, otherHalfLength, 0.2f))
                            {
                                failure = "Confirmed vehicle footprint overlap at handle "
                                    + nearby.Handle.ToString(CultureInfo.InvariantCulture) + ".";
                                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    inconclusiveEvidence.Add(
                        "Nearby vehicle-overlap query unavailable: " + ex.GetType().Name);
                }

                try
                {
                    Ped[] nearbyPeds = World.GetNearbyPeds(center, footprintRadius + 1.5f);
                    if (nearbyPeds == null)
                        inconclusiveEvidence.Add("Nearby pedestrian-overlap query returned no evidence.");
                    else
                    {
                        foreach (Ped nearby in nearbyPeds)
                        {
                            if (nearby == null || !nearby.Exists()
                                || (player != null && nearby.Handle == player.Handle)
                                || nearby.IsDead || nearby.IsInVehicle()
                                || Math.Abs(nearby.Position.Z - center.Z) > 2.5f)
                                continue;
                            if (PedOverlapsPreferredVehicleFootprint(
                                center, normalizedHeading, halfWidth, halfLength,
                                nearby.Position, 0.3f))
                            {
                                failure = "Confirmed pedestrian overlap in the vehicle footprint at handle "
                                    + nearby.Handle.ToString(CultureInfo.InvariantCulture) + ".";
                                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    inconclusiveEvidence.Add(
                        "Nearby pedestrian-overlap query unavailable: " + ex.GetType().Name);
                }

                if (inconclusiveEvidence.Count > 0)
                {
                    failure = string.Join("; ", inconclusiveEvidence.Distinct());
                    return PreferredVehiclePlacementResult.Inconclusive;
                }
                if (groundSupportInconclusiveEvidence.Count > 0)
                {
                    failure = "Vehicle footprint geometry and collision clearance were confirmed; ground support alone was inconclusive: "
                        + string.Join("; ", groundSupportInconclusiveEvidence.Distinct());
                    return PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive;
                }
                return PreferredVehiclePlacementResult.ConfirmedSafe;
            }
            catch (Exception ex)
            {
                failure = "Footprint validation failed: " + ex.GetType().Name + ".";
                return PreferredVehiclePlacementResult.Inconclusive;
            }
        }

        private static bool PreferredVehicleFootprintsOverlap(
            Vector3 firstCenter,
            float firstHeading,
            float firstHalfWidth,
            float firstHalfLength,
            Vector3 secondCenter,
            float secondHeading,
            float secondHalfWidth,
            float secondHalfLength,
            float clearance)
        {
            if (Math.Abs(firstCenter.Z - secondCenter.Z) > 2.0f)
                return false;

            double firstRadians = NormalizePreferredVehicleHeading(firstHeading)
                * (Math.PI / 180.0);
            double secondRadians = NormalizePreferredVehicleHeading(secondHeading)
                * (Math.PI / 180.0);
            Vector3 firstForward = new Vector3(
                -(float)Math.Sin(firstRadians), (float)Math.Cos(firstRadians), 0f);
            Vector3 firstRight = new Vector3(
                (float)Math.Cos(firstRadians), (float)Math.Sin(firstRadians), 0f);
            Vector3 secondForward = new Vector3(
                -(float)Math.Sin(secondRadians), (float)Math.Cos(secondRadians), 0f);
            Vector3 secondRight = new Vector3(
                (float)Math.Cos(secondRadians), (float)Math.Sin(secondRadians), 0f);
            Vector3 delta = secondCenter - firstCenter;
            Vector3[] axes = { firstRight, firstForward, secondRight, secondForward };
            for (int index = 0; index < axes.Length; index++)
            {
                Vector3 axis = axes[index];
                float centerDistance = Math.Abs(delta.X * axis.X + delta.Y * axis.Y);
                float firstRadius = firstHalfWidth * Math.Abs(
                        firstRight.X * axis.X + firstRight.Y * axis.Y)
                    + firstHalfLength * Math.Abs(
                        firstForward.X * axis.X + firstForward.Y * axis.Y);
                float secondRadius = secondHalfWidth * Math.Abs(
                        secondRight.X * axis.X + secondRight.Y * axis.Y)
                    + secondHalfLength * Math.Abs(
                        secondForward.X * axis.X + secondForward.Y * axis.Y);
                if (centerDistance > firstRadius + secondRadius + Math.Max(0f, clearance))
                    return false;
            }
            return true;
        }

        private static bool PedOverlapsPreferredVehicleFootprint(
            Vector3 vehicleCenter,
            float vehicleHeading,
            float halfWidth,
            float halfLength,
            Vector3 pedPosition,
            float clearance)
        {
            if (Math.Abs(vehicleCenter.Z - pedPosition.Z) > 2.0f)
                return false;

            double radians = NormalizePreferredVehicleHeading(vehicleHeading)
                * (Math.PI / 180.0);
            float forwardX = -(float)Math.Sin(radians);
            float forwardY = (float)Math.Cos(radians);
            float rightX = (float)Math.Cos(radians);
            float rightY = (float)Math.Sin(radians);
            float deltaX = pedPosition.X - vehicleCenter.X;
            float deltaY = pedPosition.Y - vehicleCenter.Y;
            float lateralDistance = Math.Abs(deltaX * rightX + deltaY * rightY);
            float longitudinalDistance = Math.Abs(
                deltaX * forwardX + deltaY * forwardY);
            return lateralDistance <= halfWidth + Math.Max(0f, clearance)
                && longitudinalDistance <= halfLength + Math.Max(0f, clearance);
        }

        private bool TrySettleCreatedStationVehicle(
            Vehicle vehicle,
            out string evidence)
        {
            evidence = string.Empty;
            if (vehicle == null || !vehicle.Exists())
            {
                evidence = "The selected Station Utility disappeared before settling.";
                return false;
            }

            Vector3 previousPosition = vehicle.Position;
            float previousHeading = vehicle.Heading;
            int stableSamples = 0;
            float lastPositionDrift = float.MaxValue;
            float lastHeadingDrift = float.MaxValue;
            bool lastAllWheels = false;
            for (int sample = 0; sample < StationVehicleMaximumSettleSamples; sample++)
            {
                try
                {
                    Function.Call(Hash.WAIT,
                        StationVehicleSettleSampleIntervalMilliseconds);
                }
                catch (Exception ex)
                {
                    evidence = "GTA could not yield while the selected Station Utility settled: "
                        + ex.GetType().Name + ".";
                    return false;
                }

                if (vehicle == null || !vehicle.Exists())
                {
                    evidence = "The selected Station Utility disappeared during its settling window.";
                    return false;
                }

                Vector3 currentPosition = vehicle.Position;
                float currentHeading = vehicle.Heading;
                lastPositionDrift = currentPosition.DistanceTo(previousPosition);
                lastHeadingDrift = PreferredVehicleHeadingDifference(
                    currentHeading, previousHeading);
                try
                {
                    lastAllWheels = Function.Call<bool>(
                        Hash.IS_VEHICLE_ON_ALL_WHEELS, vehicle);
                }
                catch
                {
                    lastAllWheels = false;
                }

                if (lastAllWheels
                    && lastPositionDrift <= StationVehicleSettlePositionTolerance
                    && lastHeadingDrift <= 1.0f)
                    stableSamples++;
                else
                    stableSamples = 0;

                previousPosition = currentPosition;
                previousHeading = currentHeading;
                if (stableSamples >= StationVehicleRequiredStableSamples)
                {
                    evidence = "SettleSamples=" + (sample + 1).ToString(CultureInfo.InvariantCulture)
                        + "; StableIntervals=" + stableSamples.ToString(CultureInfo.InvariantCulture)
                        + "; PositionDrift=" + lastPositionDrift.ToString("0.00", CultureInfo.InvariantCulture)
                        + "m; HeadingDrift=" + lastHeadingDrift.ToString("0.0", CultureInfo.InvariantCulture)
                        + "deg; AllWheels=true; SettleWindowMs="
                        + ((sample + 1) * StationVehicleSettleSampleIntervalMilliseconds)
                            .ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }

            evidence = "The selected Station Utility did not reach a stable upright ground-contact pose within the bounded settling period; PositionDrift="
                + lastPositionDrift.ToString("0.00", CultureInfo.InvariantCulture)
                + "m; HeadingDrift=" + lastHeadingDrift.ToString("0.0", CultureInfo.InvariantCulture)
                + "deg; AllWheels=" + lastAllWheels
                + "; SettleWindowMs="
                + (StationVehicleMaximumSettleSamples * StationVehicleSettleSampleIntervalMilliseconds)
                    .ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }

        private PreferredVehiclePlacementResult TryValidateCreatedPreferredVehiclePose(
            Vehicle vehicle,
            Ped player,
            Vector3 requestedPosition,
            float requestedHeading,
            bool allowInconclusiveGroundSupport,
            out string failure)
        {
            failure = string.Empty;
            if (vehicle == null || !vehicle.Exists())
            {
                failure = "The created vehicle no longer exists.";
                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
            }
            float positionDrift = Distance2D(vehicle.Position, requestedPosition);
            float headingDrift = PreferredVehicleHeadingDifference(
                vehicle.Heading, requestedHeading);
            if (positionDrift > 1.25f || headingDrift > 10.0f)
            {
                failure = "Confirmed unusable final pose drift; PositionDrift="
                    + positionDrift.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; HeadingDrift="
                    + headingDrift.ToString("0.0", CultureInfo.InvariantCulture) + ".";
                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
            }

            Vector3 rotation;
            try
            {
                rotation = vehicle.Rotation;
            }
            catch (Exception ex)
            {
                failure = "Final vehicle tilt could not be observed: " + ex.GetType().Name + ".";
                return PreferredVehiclePlacementResult.Inconclusive;
            }
            if (float.IsNaN(rotation.X) || float.IsInfinity(rotation.X)
                || float.IsNaN(rotation.Y) || float.IsInfinity(rotation.Y))
            {
                failure = "Final vehicle tilt data was unavailable.";
                return PreferredVehiclePlacementResult.Inconclusive;
            }
            float roll = Math.Abs(rotation.X % 360.0f);
            float pitch = Math.Abs(rotation.Y % 360.0f);
            roll = Math.Min(roll, 360.0f - roll);
            pitch = Math.Min(pitch, 360.0f - pitch);
            if (roll > 8.0f || pitch > 8.0f)
            {
                failure = "Confirmed excessive final vehicle tilt; RequiredMaximum=8deg; Roll="
                    + roll.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Pitch=" + pitch.ToString("0.0", CultureInfo.InvariantCulture) + ".";
                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
            }

            bool groundSupportInconclusive = false;
            PreferredVehiclePlacementResult footprintResult =
                TryValidatePreferredVehicleFootprint(
                    vehicle.Model, player, vehicle.Position, vehicle.Heading,
                    vehicle, allowInconclusiveGroundSupport, out failure);
            if (footprintResult == PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive)
                groundSupportInconclusive = true;
            else if (footprintResult != PreferredVehiclePlacementResult.ConfirmedSafe)
                return footprintResult;
            string footprintEvidence = failure;

            PreferredVehiclePlacementResult entryResult =
                TryValidatePreferredVehicleDriverEntrySpace(
                    vehicle, player, allowInconclusiveGroundSupport, out failure);
            if (entryResult == PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive)
                groundSupportInconclusive = true;
            else if (entryResult != PreferredVehiclePlacementResult.ConfirmedSafe)
                return entryResult;
            string entryEvidence = failure;

            float roadHeading;
            Vector3 roadNode;
            try
            {
                roadNode = World.GetNextPositionOnStreetWithHeading(
                    vehicle.Position, out roadHeading);
            }
            catch (Exception ex)
            {
                failure = "The final vehicle-road route probe was unavailable: "
                    + ex.GetType().Name + "."
                    + (string.IsNullOrWhiteSpace(footprintEvidence)
                        ? string.Empty : " Footprint=" + footprintEvidence);
                return PreferredVehiclePlacementResult.Inconclusive;
            }
            float roadDistance = roadNode == Vector3.Zero
                ? float.MaxValue : Distance2D(vehicle.Position, roadNode);
            if (roadNode == Vector3.Zero
                || float.IsNaN(roadHeading) || float.IsInfinity(roadHeading)
                || roadDistance < 1.5f || roadDistance > 40f)
            {
                failure = "The final vehicle pose has no clear, nearby vehicle-road route; RoadNodeDistance="
                    + (roadDistance == float.MaxValue ? "unavailable"
                        : roadDistance.ToString("0.0", CultureInfo.InvariantCulture))
                    + "; RequiredRange=1.5-40m."
                    + (string.IsNullOrWhiteSpace(footprintEvidence)
                        ? string.Empty : " Footprint=" + footprintEvidence);
                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
            }

            failure = "Final footprint was confirmed; Driver entry space was confirmed; "
                + "nearby vehicle-road route was observed; RoadNodeDistance="
                + roadDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + "m; RoadHeading=" + roadHeading.ToString("0.0", CultureInfo.InvariantCulture)
                + ". Footprint=" + footprintEvidence + " Entry=" + entryEvidence;
            return groundSupportInconclusive
                ? PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive
                : PreferredVehiclePlacementResult.ConfirmedSafe;
        }

        private PreferredVehiclePlacementResult TryValidatePreferredVehicleDriverEntrySpace(
            Vehicle vehicle,
            Ped player,
            bool allowInconclusiveGroundSupport,
            out string failure)
        {
            failure = string.Empty;
            if (vehicle == null || !vehicle.Exists())
            {
                failure = "The created Utility is unavailable during driver-entry validation.";
                return PreferredVehiclePlacementResult.ConfirmedUnsafe;
            }

            Vector3 minimum;
            Vector3 maximum;
            try
            {
                using (OutputArgument minimumOutput = new OutputArgument())
                using (OutputArgument maximumOutput = new OutputArgument())
                {
                    Function.Call(Hash.GET_MODEL_DIMENSIONS,
                        vehicle.Model.Hash, minimumOutput, maximumOutput);
                    minimum = minimumOutput.GetResult<Vector3>();
                    maximum = maximumOutput.GetResult<Vector3>();
                }
            }
            catch (Exception ex)
            {
                failure = "Driver-side vehicle dimensions were unavailable: "
                    + ex.GetType().Name + ".";
                return PreferredVehiclePlacementResult.Inconclusive;
            }

            float halfWidth = Math.Abs(maximum.X - minimum.X) * 0.5f;
            if (halfWidth < 0.35f || float.IsNaN(halfWidth) || float.IsInfinity(halfWidth))
            {
                failure = "GTA returned unusable vehicle width for driver-entry validation.";
                return PreferredVehiclePlacementResult.Inconclusive;
            }

            double radians = NormalizePreferredVehicleHeading(vehicle.Heading) * Math.PI / 180.0;
            Vector3 right = new Vector3((float)Math.Cos(radians), (float)Math.Sin(radians), 0f);
            Vector3 doorSide = vehicle.Position - right * (halfWidth + 0.2f);
            Vector3 firstEntryPoint = vehicle.Position - right * (halfWidth + 0.85f);
            Vector3 outerEntryPoint = vehicle.Position - right * (halfWidth + 1.6f);
            Vector3[] accessPoints = { firstEntryPoint, outerEntryPoint };
            List<string> groundSupportInconclusiveEvidence = new List<string>();
            List<string> coordinateInteriorInconclusiveEvidence = new List<string>();
            foreach (Vector3 accessPoint in accessPoints)
            {
                try
                {
                    if (Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                        accessPoint.X, accessPoint.Y, accessPoint.Z) != 0)
                    {
                        if (!allowInconclusiveGroundSupport)
                        {
                            failure = "The driver-side entry path enters building geometry.";
                            return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                        }
                        coordinateInteriorInconclusiveEvidence.Add(
                            "GTA reported a coordinate interior ID at a driver-entry sample; authored Station shell overlap is ambiguous until physical clearance checks complete.");
                    }
                }
                catch (Exception ex)
                {
                    if (!allowInconclusiveGroundSupport)
                    {
                        failure = "The driver-side entry interior query was unavailable: "
                            + ex.GetType().Name + ".";
                        return PreferredVehiclePlacementResult.Inconclusive;
                    }
                    coordinateInteriorInconclusiveEvidence.Add(
                        "The driver-entry coordinate interior query was unavailable: "
                        + ex.GetType().Name);
                }

                float groundZ = accessPoint.Z;
                Vector3 groundNormal = Vector3.Zero;
                bool groundConfirmed = false;
                try
                {
                    groundConfirmed = World.GetGroundHeightAndNormal(
                        accessPoint, out groundZ, out groundNormal);
                }
                catch (Exception ex)
                {
                    if (!allowInconclusiveGroundSupport)
                    {
                        failure = "Ground support beside the driver door was unavailable: "
                            + ex.GetType().Name + ".";
                        return PreferredVehiclePlacementResult.Inconclusive;
                    }
                    groundSupportInconclusiveEvidence.Add(
                        "Ground support beside the driver door was unavailable: "
                        + ex.GetType().Name);
                }
                if (!groundConfirmed || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || float.IsNaN(groundNormal.Z) || float.IsInfinity(groundNormal.Z))
                {
                    if (!allowInconclusiveGroundSupport)
                    {
                        failure = "GTA could not confirm ground support in the driver entry space.";
                        return PreferredVehiclePlacementResult.Inconclusive;
                    }
                    groundSupportInconclusiveEvidence.Add(
                        "GTA could not confirm ground support in the driver entry space.");
                    continue;
                }
                if (groundNormal.Z < 0.55f || Math.Abs(accessPoint.Z - groundZ) > 1.5f)
                {
                    failure = "Confirmed unusable ground beside the driver door; NormalZ="
                        + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; HeightDelta=" + Math.Abs(accessPoint.Z - groundZ)
                            .ToString("0.00", CultureInfo.InvariantCulture) + ".";
                    return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                }
            }

            try
            {
                Vector3 rayStart = doorSide;
                Vector3 rayEnd = outerEntryPoint;
                rayStart.Z += 0.8f;
                rayEnd.Z += 0.8f;
                RaycastResult obstruction = World.Raycast(
                    rayStart, rayEnd,
                    IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Glass,
                    vehicle);
                if (obstruction.DidHit)
                {
                    failure = "A barrier, object, or map surface blocks the driver-side entry path.";
                    return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                }

                foreach (Vehicle nearby in World.GetNearbyVehicles(outerEntryPoint, 1.5f))
                    if (nearby != null && nearby.Exists()
                        && nearby.Handle != vehicle.Handle
                        && Distance2D(nearby.Position, outerEntryPoint) < 1.35f)
                    {
                        failure = "Another vehicle occupies the driver-side entry space.";
                        return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                    }
                foreach (Ped nearby in World.GetNearbyPeds(outerEntryPoint, 1.5f))
                    if (nearby != null && nearby.Exists()
                        && (player == null || nearby.Handle != player.Handle)
                        && Distance2D(nearby.Position, outerEntryPoint) < 1.1f)
                    {
                        failure = "A pedestrian occupies the driver-side entry space.";
                        return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                    }

                int driverSeatOccupant = Function.Call<int>(
                    Hash.GET_PED_IN_VEHICLE_SEAT,
                    vehicle, (int)VehicleSeat.Driver, false);
                if (driverSeatOccupant != 0)
                {
                    failure = "The created Utility's driver seat is occupied.";
                    return PreferredVehiclePlacementResult.ConfirmedUnsafe;
                }
            }
            catch (Exception ex)
            {
                failure = "Driver-side entry clearance could not be confirmed: "
                    + ex.GetType().Name + ".";
                return PreferredVehiclePlacementResult.Inconclusive;
            }

            failure = "Clear driver-side access, unobstructed entry path, and an empty driver seat were confirmed."
                + (groundSupportInconclusiveEvidence.Count == 0
                    ? " Two exterior ground samples were confirmed."
                    : " Driver-entry ground support alone was inconclusive: "
                        + string.Join("; ", groundSupportInconclusiveEvidence.Distinct()))
                + (coordinateInteriorInconclusiveEvidence.Count == 0
                    ? string.Empty
                    : " Coordinate-interior evidence was treated as inconclusive; physical entry clearance passed: "
                        + string.Join("; ", coordinateInteriorInconclusiveEvidence.Distinct()));
            return groundSupportInconclusiveEvidence.Count > 0
                ? PreferredVehiclePlacementResult.ConfirmedSafeGroundInconclusive
                : PreferredVehiclePlacementResult.ConfirmedSafe;
        }

        private static void AddPreferredVehicleHeading(List<float> headings, float heading)
        {
            float normalized = NormalizePreferredVehicleHeading(heading);
            foreach (float existing in headings)
                if (PreferredVehicleHeadingDifference(existing, normalized) < 1.0f)
                    return;
            headings.Add(normalized);
        }

        private static float NormalizePreferredVehicleHeading(float heading)
        {
            float normalized = heading % 360.0f;
            if (normalized < 0.0f)
                normalized += 360.0f;
            return normalized;
        }

        private static float PreferredVehicleHeadingDifference(float first, float second)
        {
            float difference = Math.Abs(NormalizePreferredVehicleHeading(first)
                - NormalizePreferredVehicleHeading(second));
            return Math.Min(difference, 360.0f - difference);
        }

        private static string FormatVehiclePosition(Vector3 position)
        {
            return "(" + position.X.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + position.Y.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + position.Z.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        private bool IsKnownPoliceVehicle(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
                return false;
            int hash = vehicle.Model.Hash;
            foreach (LSPDPoliceVehicleDefinition definition in _profile.Vehicles)
                if (ResolveModelHash(definition.ModelName) == hash)
                    return true;
            foreach (LSPDPoliceFavoriteModel favorite in _profile.FavoriteVehicles)
                if (ResolveModelHash(favorite.ModelName) == hash)
                    return true;
            return false;
        }

        private static int ResolveModelHash(string modelName)
        {
            return unchecked((int)StringHash.AtStringHash(modelName, 0));
        }

        private static int ResolveWeaponHash(string name)
        {
            return LSPDProfile.ResolveWeaponHash(name);
        }

        private bool GiveWeapon(
            string weaponName,
            int ammo,
            bool equip,
            LSPDPoliceFavoriteWeapon saved = null,
            bool reportFailure = true)
        {
            int hash = ResolveWeaponHash(weaponName);
            var asset = new WeaponAsset(hash);
            bool releaseAsset = false;
            try
            {
                if (!asset.IsValid || !asset.IsValidAsWeaponHash)
                {
                    if (reportFailure)
                        return Fail("POLICE_WEAPON_ASSET_INVALID", "Weapon is not available in this game build: " + weaponName + ".");
                    return false;
                }
                if (!asset.IsLoaded)
                {
                    // Weapon selection may be called by the UI. Queue the
                    // asset without Script.Wait; a deliberate retry after GTA
                    // streams it is safer than freezing or aborting the script.
                    asset.Request();
                    // Background profile restoration is expected to encounter
                    // pending assets. Do not record hundreds of identical
                    // DEBUG failures while GTA is simply streaming them.
                    if (reportFailure)
                        return Fail("POLICE_WEAPON_ASSET_LOAD_PENDING", "Weapon is loading. Select it again in a moment: " + weaponName + ".");
                    return false;
                }

                releaseAsset = true;
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return Fail("POLICE_PLAYER_UNAVAILABLE", "The player character is unavailable.");
                Weapon given = player.Weapons.Give((WeaponHash)hash, Math.Max(0, ammo), equip, true);
                if (given == null || !given.IsPresent)
                    return Fail("POLICE_WEAPON_GIVE_FAILED", "The weapon could not be equipped.");
                if (saved != null)
                {
                    if (saved.Tint >= 0 && saved.Tint < given.TintCount)
                        given.Tint = (WeaponTint)saved.Tint;
                    var components = new HashSet<int>(saved.Components.Select(ResolveWeaponHash));
                    foreach (WeaponComponent component in given.Components)
                        component.Active = components.Contains(unchecked((int)component.ComponentHash));
                }
                return true;
            }
            catch (Exception ex)
            {
                return Fail("POLICE_WEAPON_ERROR", "Weapon could not be applied: " + weaponName + ".", ex);
            }
            finally
            {
                // Do not cancel a request that is still pending. A later
                // normal Script tick will see the loaded asset, apply it, and
                // then release the streaming reference here.
                if (releaseAsset)
                {
                    try { asset.MarkAsNoLongerNeeded(); } catch { }
                }
            }
        }

        private bool TryResolvePreferredPoliceVehicleSpawn(
            Ped player,
            LSPDPoliceStationDefinition station,
            out Vector3 position,
            out float heading,
            out string source,
            out string failure)
        {
            position = Vector3.Zero;
            heading = 0f;
            source = string.Empty;
            failure = string.Empty;
            if (player == null || !player.Exists())
            {
                failure = "The Player is not available to find a nearby safe spot.";
                return false;
            }

            if (_stationSetupCompletionPending)
            {
                string setupStationFailure = string.Empty;
                if (station != null
                    && Distance2D(
                        player.Position,
                        new Vector3(station.ExteriorX, station.ExteriorY, station.ExteriorZ))
                        <= 125f
                    && TryResolveStationParkingPosition(
                        player, station, true, out position, out heading,
                        out setupStationFailure))
                {
                    source = "StationParking";
                    return true;
                }

                string setupStationExteriorFailure = string.Empty;
                if (station != null
                    && Distance2D(
                        player.Position,
                        new Vector3(station.ExteriorX, station.ExteriorY, station.ExteriorZ))
                        <= 125f
                    && TryResolveStationExteriorParkingPosition(
                        player, station, out position, out heading,
                        out setupStationExteriorFailure))
                {
                    source = "StationExteriorParking";
                    return true;
                }

                string setupAmbientNodeFailure;
                if (TryResolveNearbyUnoccupiedVehicleNodeParkingPosition(
                    player, player.Position, 48f,
                    out position, out heading, out setupAmbientNodeFailure))
                {
                    source = "GtaUnoccupiedVehicleNodeParking";
                    return true;
                }

                string setupRoadsideFailure;
                bool usedSetupRoadNodeFallback;
                if (TryResolveNearbyRoadsidePosition(
                    player, out position, out heading,
                    out setupRoadsideFailure, out usedSetupRoadNodeFallback))
                {
                    source = usedSetupRoadNodeFallback
                        ? "NearbyRoadNodeRoadside"
                        : "NearbySafeRoadside";
                    return true;
                }

                string setupParkingFailure;
                if (TryResolveNearbyParkedGroundPosition(
                    player,
                    20f,
                    out position,
                    out heading,
                    out setupParkingFailure))
                {
                    source = "StationSetupNearbyParkedGround";
                    return true;
                }

                string setupFallbackFailure;
                if (TryResolveNearbyParkedGroundPosition(
                    player,
                    99f,
                    out position,
                    out heading,
                    out setupFallbackFailure))
                {
                    source = Distance2D(position, player.Position) <= 20f
                        ? "StationSetupNearbyParkedGround"
                        : "StationSetupNearbyParkedGroundUnder100m";
                    return true;
                }

                failure = "Station setup could not find safe parked ground within 20 metres or a safe fallback under 100 metres of the Station entrance. "
                    + (string.IsNullOrWhiteSpace(setupStationFailure)
                        ? string.Empty : setupStationFailure + " ")
                    + (string.IsNullOrWhiteSpace(setupStationExteriorFailure)
                        ? string.Empty : setupStationExteriorFailure + " ")
                    + (string.IsNullOrWhiteSpace(setupAmbientNodeFailure)
                        ? string.Empty : setupAmbientNodeFailure + " ")
                    + (string.IsNullOrWhiteSpace(setupRoadsideFailure)
                        ? string.Empty : setupRoadsideFailure + " ")
                    + setupParkingFailure
                    + (string.IsNullOrWhiteSpace(setupFallbackFailure)
                        ? string.Empty
                        : " " + setupFallbackFailure);
                return false;
            }

            const float stationSetupRadius = 125f;
            if (station != null
                && Distance2D(
                    player.Position,
                    new Vector3(station.ExteriorX, station.ExteriorY, station.ExteriorZ))
                    <= stationSetupRadius)
            {
                if (TryResolveStationParkingPosition(
                    player, station, true, out position, out heading, out failure))
                {
                    source = "StationParking";
                    return true;
                }

                string stationParkingFailure = failure;
                string stationExteriorFailure;
                if (TryResolveStationExteriorParkingPosition(
                    player, station, out position, out heading, out stationExteriorFailure))
                {
                    source = "StationExteriorParking";
                    failure = string.Empty;
                    return true;
                }

                string stationAmbientNodeFailure;
                if (TryResolveNearbyUnoccupiedVehicleNodeParkingPosition(
                    player, player.Position, 48f,
                    out position, out heading, out stationAmbientNodeFailure))
                {
                    source = "GtaUnoccupiedVehicleNodeParking";
                    failure = string.Empty;
                    return true;
                }

                string roadsideFailure;
                bool usedRoadNodeFallback;
                if (TryResolveNearbyRoadsidePosition(
                    player, out position, out heading,
                    out roadsideFailure, out usedRoadNodeFallback))
                {
                    source = usedRoadNodeFallback
                        ? "NearbyRoadNodeRoadside"
                        : "NearbySafeRoadside";
                    failure = string.Empty;
                    return true;
                }

                string parkedGroundFailure;
                if (TryResolveNearbyParkedGroundPosition(
                    player, out position, out heading, out parkedGroundFailure))
                {
                    source = "NearbyParkedGround";
                    failure = string.Empty;
                    return true;
                }

                failure = "No safe Station parking place was available. "
                    + stationParkingFailure
                    + (string.IsNullOrWhiteSpace(stationExteriorFailure)
                        ? string.Empty
                        : " " + stationExteriorFailure)
                    + (string.IsNullOrWhiteSpace(stationAmbientNodeFailure)
                        ? string.Empty
                        : " " + stationAmbientNodeFailure)
                    + (string.IsNullOrWhiteSpace(roadsideFailure)
                        ? string.Empty
                        : " " + roadsideFailure)
                    + (string.IsNullOrWhiteSpace(parkedGroundFailure)
                        ? string.Empty
                        : " " + parkedGroundFailure);
                return false;
            }

            string ambientVehicleNodeFailure;
            if (TryResolveNearbyUnoccupiedVehicleNodeParkingPosition(
                player, player.Position, 48f,
                out position, out heading, out ambientVehicleNodeFailure))
            {
                source = "GtaUnoccupiedVehicleNodeParking";
                failure = string.Empty;
                return true;
            }

            string nearbyRoadsideFailure;
            bool usedNearbyRoadNodeFallback;
            if (TryResolveNearbyRoadsidePosition(
                player, out position, out heading,
                out nearbyRoadsideFailure, out usedNearbyRoadNodeFallback))
            {
                source = usedNearbyRoadNodeFallback
                    ? "NearbyRoadNodeRoadside"
                    : "NearbySafeRoadside";
                failure = string.Empty;
                return true;
            }

            string nearbyParkedGroundFailure;
            if (TryResolveNearbyParkedGroundPosition(
                player, out position, out heading, out nearbyParkedGroundFailure))
            {
                source = "NearbyParkedGround";
                failure = string.Empty;
                return true;
            }

            if (station == null)
                failure = "The selected Station is missing, and no nearby safe roadside or parked-ground spot was found."
                    + (string.IsNullOrWhiteSpace(nearbyRoadsideFailure)
                        ? string.Empty
                        : " " + nearbyRoadsideFailure)
                    + (string.IsNullOrWhiteSpace(ambientVehicleNodeFailure)
                        ? string.Empty
                        : " " + ambientVehicleNodeFailure)
                    + (string.IsNullOrWhiteSpace(nearbyParkedGroundFailure)
                        ? string.Empty
                        : " " + nearbyParkedGroundFailure);
            else
                failure = "No nearby safe roadside or parked-ground spot was found."
                    + (string.IsNullOrWhiteSpace(nearbyRoadsideFailure)
                        ? string.Empty
                        : " " + nearbyRoadsideFailure)
                    + (string.IsNullOrWhiteSpace(ambientVehicleNodeFailure)
                        ? string.Empty
                        : " " + ambientVehicleNodeFailure)
                    + (string.IsNullOrWhiteSpace(nearbyParkedGroundFailure)
                        ? string.Empty
                        : " " + nearbyParkedGroundFailure);
            return false;
        }

        private bool TryResolveStationExteriorParkingPosition(
            Ped player,
            LSPDPoliceStationDefinition station,
            out Vector3 position,
            out float heading,
            out string failure)
        {
            position = Vector3.Zero;
            heading = 0f;
            failure = string.Empty;
            if (player == null || !player.Exists() || station == null || _profile == null)
            {
                failure = "The Player, selected Station, or Police catalog is unavailable.";
                return false;
            }

            LSPDPoliceLocationDefinition exterior = _profile.FindLocation(station.LocationId);
            if (exterior == null || !exterior.ExteriorSafe)
            {
                failure = "The selected Station has no authored safe outside Location.";
                return false;
            }

            Vector3 center = new Vector3(exterior.X, exterior.Y, exterior.Z);
            if (center == Vector3.Zero
                || float.IsNaN(center.X) || float.IsInfinity(center.X)
                || float.IsNaN(center.Y) || float.IsInfinity(center.Y)
                || float.IsNaN(center.Z) || float.IsInfinity(center.Z))
            {
                failure = "The selected Station outside Location is invalid.";
                return false;
            }

            const float minimumPlayerSeparation = 8f;
            const float maximumStationSearchDistance = 48f;
            float[] ringRadii = { 12f, 24f, 36f, 48f };
            string lastReason = string.Empty;
            for (int ring = 0; ring < ringRadii.Length; ring++)
            {
                float radius = ringRadii[ring];
                for (int probe = 0; probe < 8; probe++)
                {
                    double radians = probe * (Math.PI / 4.0);
                    Vector3 candidate = center;
                    candidate.X += (float)Math.Cos(radians) * radius;
                    candidate.Y += (float)Math.Sin(radians) * radius;
                    if (Distance2D(candidate, center) > maximumStationSearchDistance
                        || Distance2D(candidate, player.Position) < minimumPlayerSeparation)
                        continue;

                    Vector3 safe;
                    string reason;
                    if (!TryValidatePreferredVehicleSpawnPoint(
                        candidate, exterior.Z, player, false, out safe, out reason))
                    {
                        if (!string.IsNullOrWhiteSpace(reason))
                            lastReason = reason;
                        continue;
                    }

                    float roadHeading;
                    Vector3 roadNode;
                    try
                    {
                        roadNode = World.GetNextPositionOnStreetWithHeading(
                            safe, out roadHeading);
                    }
                    catch (Exception ex)
                    {
                        lastReason = "GTA could not check the nearby road direction: "
                            + ex.GetType().Name + ".";
                        continue;
                    }

                    float roadSeparation = roadNode == Vector3.Zero
                        ? float.MaxValue : Distance2D(safe, roadNode);
                    if (roadNode == Vector3.Zero
                        || float.IsNaN(roadHeading) || float.IsInfinity(roadHeading)
                        || roadSeparation < 1.5f || roadSeparation > 18f)
                    {
                        lastReason = "The candidate is not clear parked ground beside a usable road.";
                        continue;
                    }

                    position = safe;
                    heading = roadHeading;
                    return true;
                }
            }

            failure = "No clear parked ground was found within 48 metres of the Station outside Location and 8 metres from the Player."
                + (string.IsNullOrWhiteSpace(lastReason) ? string.Empty : " Last check: " + lastReason);
            return false;
        }

        private bool TryResolveStationParkingPosition(
            Ped player,
            LSPDPoliceStationDefinition station,
            bool allowInconclusiveGroundSupport,
            out Vector3 position,
            out float heading,
            out string failure)
        {
            position = Vector3.Zero;
            heading = 0f;
            failure = string.Empty;
            if (station == null || !station.ExteriorSafe)
            {
                failure = "The selected Station does not have a verified safe exterior parking area.";
                return false;
            }

            Vector3 authored = new Vector3(
                station.VehicleX, station.VehicleY, station.VehicleZ);
            if (authored == Vector3.Zero)
            {
                failure = "The selected Station has no authored vehicle parking coordinates.";
                return false;
            }

            // The station's authored parking point is preferred. A short,
            // bounded ring keeps an occupied parking place from putting a car
            // into a pedestrian or a parked vehicle elsewhere on the lot.
            for (int ring = 0; ring <= 3; ring++)
            {
                float radius = ring * 3f;
                int probes = ring == 0 ? 1 : 8;
                for (int probe = 0; probe < probes; probe++)
                {
                    double radians = probe * (Math.PI / 4.0);
                    Vector3 candidate = authored;
                    candidate.X += (float)Math.Cos(radians) * radius;
                    candidate.Y += (float)Math.Sin(radians) * radius;
                    Vector3 safe;
                    string reason;
                    if (!TryValidatePreferredVehicleSpawnPoint(
                        candidate,
                        authored.Z,
                        player,
                        true,
                        allowInconclusiveGroundSupport,
                        out safe,
                        out reason))
                    {
                        failure = reason;
                        continue;
                    }

                    float roadHeading;
                    Vector3 roadNode;
                    try
                    {
                        roadNode = World.GetNextPositionOnStreetWithHeading(
                            safe, out roadHeading);
                    }
                    catch (Exception ex)
                    {
                        failure = "The selected Station parking route could not be checked: "
                            + ex.GetType().Name + ".";
                        continue;
                    }
                    float roadDistance = roadNode == Vector3.Zero
                        ? float.MaxValue : Distance2D(safe, roadNode);
                    if (roadNode == Vector3.Zero
                        || float.IsNaN(roadHeading) || float.IsInfinity(roadHeading)
                        || roadDistance < 1.5f || roadDistance > 40f)
                    {
                        failure = "The selected Station parking point has no nearby vehicle-road route; RoadNodeDistance="
                            + (roadDistance == float.MaxValue ? "unavailable"
                                : roadDistance.ToString("0.0", CultureInfo.InvariantCulture))
                            + "; RequiredRange=1.5-40m.";
                        continue;
                    }

                    position = safe;
                    heading = roadDistance <= 18f
                        ? roadHeading : station.VehicleHeading;
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(failure))
                failure = "The Station parking area is blocked or could not be checked safely.";
            else
                failure = "The Station parking area has no clear checked spot. " + failure;
            return false;
        }

        private bool TryResolveNearbyRoadsidePosition(
            Ped player,
            out Vector3 position,
            out float heading,
            out string failure,
            out bool usedRoadNodeFallback)
        {
            position = Vector3.Zero;
            heading = 0f;
            failure = string.Empty;
            usedRoadNodeFallback = false;
            Vector3 playerPosition = player.Position;
            const float maximumDistanceFromPlayer = 48f;
            float[] ringRadii = { 0f, 14f, 28f, 42f };

            for (int ring = 0; ring < ringRadii.Length; ring++)
            {
                float radius = ringRadii[ring];
                int probes = ring == 0 ? 1 : 8;
                for (int probe = 0; probe < probes; probe++)
                {
                    double radians = probe * (Math.PI / 4.0);
                    Vector3 query = playerPosition;
                    query.X += (float)Math.Cos(radians) * radius;
                    query.Y += (float)Math.Sin(radians) * radius;

                    Vector3 roadside;
                    try
                    {
                        if (!World.GetPositionOnRoadside(query, Direction.Any, out roadside))
                            continue;
                    }
                    catch (Exception ex)
                    {
                        failure = "GTA could not find a roadside parking place: " + ex.GetType().Name + ".";
                        continue;
                    }

                    if (roadside == Vector3.Zero
                        || Distance2D(roadside, playerPosition) > maximumDistanceFromPlayer)
                        continue;

                    float roadHeading;
                    Vector3 roadNode;
                    try
                    {
                        roadNode = World.GetNextPositionOnStreetWithHeading(
                            roadside, out roadHeading);
                    }
                    catch
                    {
                        continue;
                    }

                    if (roadNode == Vector3.Zero
                        || float.IsNaN(roadHeading)
                        || float.IsInfinity(roadHeading))
                        continue;

                    Vector3 safe;
                    string reason;
                    if (!TryValidatePreferredVehicleSpawnPoint(
                        roadside,
                        query.Z,
                        player,
                        false,
                        out safe,
                        out reason))
                    {
                        failure = reason;
                        continue;
                    }

                    float roadSeparation = Distance2D(safe, roadNode);
                    if (roadSeparation < 1.5f || roadSeparation > 18f)
                    {
                        failure = "GTA returned a spot too close to the traffic lane or too far from its road.";
                        continue;
                    }

                    position = safe;
                    heading = roadHeading;
                    return true;
                }
            }

            string directRoadsideFailure = failure;
            string roadNodeFailure;
            if (TryResolveNearbyRoadNodeRoadsidePosition(
                player,
                playerPosition,
                maximumDistanceFromPlayer,
                out position,
                out heading,
                out roadNodeFailure))
            {
                usedRoadNodeFallback = true;
                failure = string.Empty;
                return true;
            }

            failure = "No nearby clear roadside or parked-ground spot was found."
                + (string.IsNullOrWhiteSpace(directRoadsideFailure)
                    ? string.Empty
                    : " " + directRoadsideFailure)
                + (string.IsNullOrWhiteSpace(roadNodeFailure)
                    ? string.Empty
                    : " Road-node roadside fallback: " + roadNodeFailure);
            return false;
        }

        private bool TryResolveNearbyUnoccupiedVehicleNodeParkingPosition(
            Ped player,
            Vector3 playerPosition,
            float maximumDistanceFromPlayer,
            out Vector3 position,
            out float heading,
            out string failure)
        {
            position = Vector3.Zero;
            heading = 0f;
            failure = string.Empty;
            if (player == null || !player.Exists()
                || !IsFinitePoliceVehiclePoint(playerPosition))
            {
                failure = "The Player position is unavailable for GTA's unoccupied vehicle-node search.";
                return false;
            }

            const float minimumPlayerSeparation = 8f;
            const float maximumElevationDifference = 8f;
            const int maximumDistinctNodes = 12;
            float[] ringRadii = { 0f, 12f, 24f, 36f, 48f };
            var checkedNodes = new List<Vector3>();
            var checkedParkingPositions = new List<Vector3>();
            string lastFailure = "GTA returned no unoccupied vehicle node.";

            for (int ring = 0;
                ring < ringRadii.Length && checkedNodes.Count < maximumDistinctNodes;
                ring++)
            {
                int probes = ring == 0 ? 1 : 8;
                for (int probe = 0;
                    probe < probes && checkedNodes.Count < maximumDistinctNodes;
                    probe++)
                {
                    double radians = probe * (Math.PI / 4.0)
                        + (ring % 2 == 1 ? Math.PI / 8.0 : 0.0);
                    Vector3 query = playerPosition;
                    query.X += (float)Math.Cos(radians) * ringRadii[ring];
                    query.Y += (float)Math.Sin(radians) * ringRadii[ring];

                    Vector3 availableNode;
                    try
                    {
                        // SHVDN resolves an actual vehicle road node and asks
                        // GTA for an unoccupied result. This is a road-node
                        // candidate, not a claim that a Rockstar parking
                        // generator or parking slot was queried.
                        availableNode = World.GetNextPositionOnStreet(query, true);
                    }
                    catch (Exception ex)
                    {
                        lastFailure = "GTA unoccupied vehicle-node query failed: "
                            + ex.GetType().Name + ".";
                        continue;
                    }

                    if (!IsFinitePoliceVehiclePoint(availableNode)
                        || Distance2D(availableNode, playerPosition)
                            < minimumPlayerSeparation
                        || Distance2D(availableNode, playerPosition)
                            > maximumDistanceFromPlayer
                        || Math.Abs(availableNode.Z - playerPosition.Z)
                            > maximumElevationDifference)
                    {
                        lastFailure = "The unoccupied vehicle node was invalid, too close, too far, or on another elevation.";
                        continue;
                    }

                    bool duplicateNode = checkedNodes.Any(previous =>
                        Distance2D(previous, availableNode) < 3f);
                    if (duplicateNode)
                        continue;
                    checkedNodes.Add(availableNode);

                    float nodeHeading;
                    Vector3 headingNode;
                    try
                    {
                        headingNode = World.GetNextPositionOnStreetWithHeading(
                            availableNode, out nodeHeading);
                    }
                    catch (Exception ex)
                    {
                        lastFailure = "GTA could not resolve the unoccupied node heading: "
                            + ex.GetType().Name + ".";
                        continue;
                    }
                    if (!IsFinitePoliceVehiclePoint(headingNode)
                        || float.IsNaN(nodeHeading) || float.IsInfinity(nodeHeading)
                        || Distance2D(availableNode, headingNode) > 5f)
                    {
                        lastFailure = "The unoccupied vehicle node had no nearby usable road heading.";
                        continue;
                    }

                    double headingRadians = nodeHeading * (Math.PI / 180.0);
                    Vector3 forward = new Vector3(
                        (float)Math.Sin(headingRadians),
                        (float)Math.Cos(headingRadians), 0f);
                    Vector3 side = new Vector3(
                        (float)Math.Cos(headingRadians),
                        (float)-Math.Sin(headingRadians), 0f);
                    Vector3[] parkingQueries =
                    {
                        availableNode + forward * 8f,
                        availableNode - forward * 8f,
                        availableNode + side * 4f,
                        availableNode - side * 4f,
                        availableNode + side * 6f + forward * 8f,
                        availableNode - side * 6f - forward * 8f
                    };

                    foreach (Vector3 parkingQuery in parkingQueries)
                    {
                        Vector3 roadside;
                        try
                        {
                            if (!World.GetPositionOnRoadside(
                                parkingQuery, Direction.Any, out roadside))
                            {
                                lastFailure = "No roadside parking offset was found beside the unoccupied vehicle node.";
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastFailure = "GTA could not resolve a parking offset: "
                                + ex.GetType().Name + ".";
                            continue;
                        }

                        if (!IsFinitePoliceVehiclePoint(roadside)
                            || Distance2D(roadside, playerPosition)
                                < minimumPlayerSeparation
                            || Distance2D(roadside, playerPosition)
                                > maximumDistanceFromPlayer
                            || checkedParkingPositions.Any(previous =>
                                Distance2D(previous, roadside) < 2f))
                        {
                            lastFailure = "The roadside offset was invalid, occupied by an earlier candidate, or outside the search radius.";
                            continue;
                        }
                        checkedParkingPositions.Add(roadside);

                        Vector3 safe;
                        string pointFailure;
                        if (!TryValidatePreferredVehicleSpawnPoint(
                            roadside, playerPosition.Z, player, false,
                            out safe, out pointFailure))
                        {
                            lastFailure = pointFailure;
                            continue;
                        }

                        Vector3 exitRoadNode;
                        float exitHeading;
                        try
                        {
                            exitRoadNode = World.GetNextPositionOnStreetWithHeading(
                                safe, out exitHeading);
                        }
                        catch (Exception ex)
                        {
                            lastFailure = "GTA could not validate the parking exit road: "
                                + ex.GetType().Name + ".";
                            continue;
                        }
                        float exitDistance = exitRoadNode == Vector3.Zero
                            ? float.MaxValue : Distance2D(safe, exitRoadNode);
                        if (!IsFinitePoliceVehiclePoint(exitRoadNode)
                            || float.IsNaN(exitHeading) || float.IsInfinity(exitHeading)
                            || exitDistance < 1.5f || exitDistance > 18f)
                        {
                            lastFailure = "The parking offset had no usable nearby road exit.";
                            continue;
                        }

                        position = safe;
                        heading = exitHeading;
                        return true;
                    }
                }
            }

            failure = "No safely validated roadside parking offset was found from GTA's unoccupied vehicle nodes. Nodes="
                + checkedNodes.Count.ToString(CultureInfo.InvariantCulture)
                + "; ParkingCandidates="
                + checkedParkingPositions.Count.ToString(CultureInfo.InvariantCulture)
                + "; LastCheck=" + lastFailure;
            return false;
        }

        private static bool IsFinitePoliceVehiclePoint(Vector3 point)
        {
            const float maximumCoordinateMagnitude = 10000f;
            return point != Vector3.Zero
                && !float.IsNaN(point.X) && !float.IsInfinity(point.X)
                && !float.IsNaN(point.Y) && !float.IsInfinity(point.Y)
                && !float.IsNaN(point.Z) && !float.IsInfinity(point.Z)
                && Math.Abs(point.X) <= maximumCoordinateMagnitude
                && Math.Abs(point.Y) <= maximumCoordinateMagnitude
                && Math.Abs(point.Z) <= maximumCoordinateMagnitude;
        }

        private bool TryResolveNearbyRoadNodeRoadsidePosition(
            Ped player,
            Vector3 playerPosition,
            float maximumDistanceFromPlayer,
            out Vector3 position,
            out float heading,
            out string failure)
        {
            position = Vector3.Zero;
            heading = 0f;
            failure = string.Empty;
            if (player == null || !player.Exists())
            {
                failure = "The Player is unavailable for a road-node search.";
                return false;
            }

            const float minimumPlayerSeparation = 8f;
            const float maximumRoadElevationDifference = 8f;
            const int maximumDistinctRoadNodes = 12;
            float[] ringRadii = { 0f, 12f, 24f, 36f, 48f };
            List<Vector3> checkedRoadNodes = new List<Vector3>();
            List<Vector3> checkedRoadsidePositions = new List<Vector3>();
            string lastFailure = "GTA did not return a nearby usable vehicle-road node.";

            for (int ring = 0;
                ring < ringRadii.Length && checkedRoadNodes.Count < maximumDistinctRoadNodes;
                ring++)
            {
                float radius = ringRadii[ring];
                int probes = ring == 0 ? 1 : 8;
                for (int probe = 0;
                    probe < probes && checkedRoadNodes.Count < maximumDistinctRoadNodes;
                    probe++)
                {
                    double radians = probe * (Math.PI / 4.0)
                        + (ring % 2 == 1 ? Math.PI / 8.0 : 0.0);
                    Vector3 query = playerPosition;
                    query.X += (float)Math.Cos(radians) * radius;
                    query.Y += (float)Math.Sin(radians) * radius;

                    Vector3 roadNode = Vector3.Zero;
                    float roadHeading = 0f;
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
                                lastFailure = "No vehicle-road node was found near a roadside query.";
                                continue;
                            }

                            roadNode = nodeOutput.GetResult<Vector3>();
                            roadHeading = headingOutput.GetResult<float>();
                        }

                        if (roadNode == Vector3.Zero
                            || float.IsNaN(roadNode.X) || float.IsInfinity(roadNode.X)
                            || float.IsNaN(roadNode.Y) || float.IsInfinity(roadNode.Y)
                            || float.IsNaN(roadNode.Z) || float.IsInfinity(roadNode.Z)
                            || float.IsNaN(roadHeading) || float.IsInfinity(roadHeading))
                        {
                            lastFailure = "GTA returned an invalid vehicle-road node or heading.";
                            continue;
                        }

                        if (Distance2D(roadNode, playerPosition) > maximumDistanceFromPlayer
                            || Math.Abs(roadNode.Z - playerPosition.Z)
                                > maximumRoadElevationDifference)
                        {
                            lastFailure = "The nearby vehicle-road node exceeded the Player distance or elevation limit.";
                            continue;
                        }

                        if (!Function.Call<bool>(
                            Hash.IS_POINT_ON_ROAD,
                            roadNode.X,
                            roadNode.Y,
                            roadNode.Z,
                            0))
                        {
                            lastFailure = "The candidate vehicle-road node is not on a vehicle road.";
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastFailure = "GTA could not check a nearby vehicle-road node: "
                            + ex.GetType().Name + ".";
                        continue;
                    }

                    bool duplicateRoadNode = false;
                    foreach (Vector3 checkedRoadNode in checkedRoadNodes)
                    {
                        if (Distance2D(roadNode, checkedRoadNode) < 3f)
                        {
                            duplicateRoadNode = true;
                            break;
                        }
                    }
                    if (duplicateRoadNode)
                        continue;
                    checkedRoadNodes.Add(roadNode);

                    double headingRadians = roadHeading * (Math.PI / 180.0);
                    Vector3 roadForward = new Vector3(
                        (float)Math.Sin(headingRadians),
                        (float)Math.Cos(headingRadians),
                        0f);
                    Vector3 roadSide = new Vector3(
                        (float)Math.Cos(headingRadians),
                        (float)-Math.Sin(headingRadians),
                        0f);
                    Vector3[] roadsideQueries =
                    {
                        roadNode,
                        roadNode + roadForward * 8f,
                        roadNode - roadForward * 8f,
                        roadNode + roadSide * 4f,
                        roadNode - roadSide * 4f
                    };

                    foreach (Vector3 roadsideQuery in roadsideQueries)
                    {
                        Vector3 roadside;
                        try
                        {
                            if (!World.GetPositionOnRoadside(
                                roadsideQuery, Direction.Any, out roadside))
                            {
                                lastFailure = "GTA found no roadside position beside a checked vehicle-road node.";
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastFailure = "GTA could not resolve roadside ground beside a vehicle-road node: "
                                + ex.GetType().Name + ".";
                            continue;
                        }

                        if (roadside == Vector3.Zero
                            || Distance2D(roadside, playerPosition) < minimumPlayerSeparation
                            || Distance2D(roadside, playerPosition) > maximumDistanceFromPlayer)
                        {
                            lastFailure = "The roadside position was too close to the Player or outside the 48 metre search.";
                            continue;
                        }

                        bool duplicateRoadsidePosition = false;
                        foreach (Vector3 checkedRoadsidePosition in checkedRoadsidePositions)
                        {
                            if (Distance2D(roadside, checkedRoadsidePosition) < 2f)
                            {
                                duplicateRoadsidePosition = true;
                                break;
                            }
                        }
                        if (duplicateRoadsidePosition)
                            continue;
                        checkedRoadsidePositions.Add(roadside);

                        Vector3 safe;
                        string pointFailure;
                        if (!TryValidatePreferredVehicleSpawnPoint(
                            roadside,
                            playerPosition.Z,
                            player,
                            false,
                            out safe,
                            out pointFailure))
                        {
                            lastFailure = pointFailure;
                            continue;
                        }

                        float safeRoadHeading;
                        Vector3 safeRoadNode;
                        try
                        {
                            safeRoadNode = World.GetNextPositionOnStreetWithHeading(
                                safe, out safeRoadHeading);
                        }
                        catch (Exception ex)
                        {
                            lastFailure = "GTA could not align the roadside candidate with its road: "
                                + ex.GetType().Name + ".";
                            continue;
                        }

                        float roadSeparation = safeRoadNode == Vector3.Zero
                            ? float.MaxValue
                            : Distance2D(safe, safeRoadNode);
                        if (safeRoadNode == Vector3.Zero
                            || float.IsNaN(safeRoadHeading)
                            || float.IsInfinity(safeRoadHeading)
                            || roadSeparation < 1.5f
                            || roadSeparation > 18f)
                        {
                            lastFailure = "The roadside candidate did not have a clear, nearby vehicle-road alignment.";
                            continue;
                        }

                        position = safe;
                        heading = safeRoadHeading;
                        return true;
                    }
                }
            }

            failure = "No safe road-adjacent position was found from nearby vehicle-road nodes within "
                + maximumDistanceFromPlayer.ToString("0", CultureInfo.InvariantCulture)
                + " metres. RoadNodes=" + checkedRoadNodes.Count.ToString(CultureInfo.InvariantCulture)
                + "; RoadsideCandidates=" + checkedRoadsidePositions.Count.ToString(CultureInfo.InvariantCulture)
                + "; LastCheck=" + lastFailure;
            return false;
        }

        private bool TryResolveNearbyParkedGroundPosition(
            Ped player,
            out Vector3 position,
            out float heading,
            out string failure)
        {
            return TryResolveNearbyParkedGroundPosition(
                player,
                48f,
                out position,
                out heading,
                out failure);
        }

        private bool TryResolveNearbyParkedGroundPosition(
            Ped player,
            float maximumDistanceFromPlayer,
            out Vector3 position,
            out float heading,
            out string failure)
        {
            position = Vector3.Zero;
            heading = 0f;
            failure = string.Empty;
            if (player == null || !player.Exists())
            {
                failure = "The Player is not available to find nearby parked ground.";
                return false;
            }

            Vector3 playerPosition = player.Position;
            float searchLimit = Math.Max(0f, Math.Min(99f, maximumDistanceFromPlayer));
            const float minimumPlayerSeparation = 8f;
            float[] ringRadii = searchLimit <= 20f
                ? new[] { 10f, 18f, 20f }
                : searchLimit <= 48f
                    ? new[] { 10f, 18f, 26f, 34f, 42f, 48f }
                    : new[] { 10f, 18f, 26f, 34f, 42f, 48f, 60f, 72f, 84f, 96f };
            string lastReason = string.Empty;

            for (int ring = 0; ring < ringRadii.Length; ring++)
            {
                float radius = ringRadii[ring];
                for (int probe = 0; probe < 8; probe++)
                {
                    double radians = probe * (Math.PI / 4.0);
                    Vector3 candidate = playerPosition;
                    candidate.X += (float)Math.Cos(radians) * radius;
                    candidate.Y += (float)Math.Sin(radians) * radius;
                    if (radius > searchLimit)
                        continue;
                    if (Distance2D(candidate, playerPosition) < minimumPlayerSeparation
                        || Distance2D(candidate, playerPosition) > searchLimit)
                        continue;

                    Vector3 safe;
                    string reason;
                    if (!TryValidatePreferredVehicleSpawnPoint(
                        candidate,
                        playerPosition.Z,
                        player,
                        true,
                        out safe,
                        out reason))
                    {
                        if (!string.IsNullOrWhiteSpace(reason))
                            lastReason = reason;
                        continue;
                    }

                    float candidateHeading = player.Heading;
                    try
                    {
                        float roadHeading;
                        Vector3 roadNode = World.GetNextPositionOnStreetWithHeading(
                            safe, out roadHeading);
                        float roadSeparation = roadNode == Vector3.Zero
                            ? float.MaxValue
                            : Distance2D(safe, roadNode);
                        if (!float.IsNaN(roadHeading)
                            && !float.IsInfinity(roadHeading)
                            && roadSeparation >= 1.5f
                            && roadSeparation <= 18f)
                            candidateHeading = roadHeading;
                    }
                    catch
                    {
                        // A clear parking area can still be used when GTA has
                        // no usable road heading nearby; face the vehicle with
                        // the Player's current direction in that case.
                    }

                    position = safe;
                    heading = candidateHeading;
                    return true;
                }
            }

            failure = "No clear parked ground was found within "
                + searchLimit.ToString("0", CultureInfo.InvariantCulture)
                + " metres of the Player and at least 8 metres away."
                + (string.IsNullOrWhiteSpace(lastReason)
                    ? string.Empty
                    : " Last check: " + lastReason);
            return false;
        }

        private bool TryValidatePreferredVehicleSpawnPoint(
            Vector3 candidate,
            float referenceHeight,
            Ped player,
            bool parkedGround,
            out Vector3 safe,
            out string failure)
        {
            return TryValidatePreferredVehicleSpawnPoint(
                candidate, referenceHeight, player, parkedGround, false,
                out safe, out failure);
        }

        private bool TryValidatePreferredVehicleSpawnPoint(
            Vector3 candidate,
            float referenceHeight,
            Ped player,
            bool parkedGround,
            bool allowInconclusiveGroundSupport,
            out Vector3 safe,
            out string failure)
        {
            safe = Vector3.Zero;
            failure = string.Empty;
            if (candidate == Vector3.Zero
                || float.IsNaN(candidate.X) || float.IsInfinity(candidate.X)
                || float.IsNaN(candidate.Y) || float.IsInfinity(candidate.Y)
                || float.IsNaN(candidate.Z) || float.IsInfinity(candidate.Z))
            {
                failure = "The parking coordinates are invalid.";
                return false;
            }

            try
            {
                int coordinateInteriorId = 0;
                bool coordinateInteriorQuerySucceeded = false;
                try
                {
                    coordinateInteriorId = Function.Call<int>(
                        Hash.GET_INTERIOR_AT_COORDS,
                        candidate.X, candidate.Y, candidate.Z);
                    coordinateInteriorQuerySucceeded = true;
                }
                catch
                {
                    // An authored exterior Station point may overlap GTA's
                    // coordinate-interior shell. The created vehicle still
                    // must pass physical footprint, wheel, tilt, clearance,
                    // driver-entry and road-access validation below.
                    if (!allowInconclusiveGroundSupport)
                        throw;
                }

                bool authoredStationInteriorShell =
                    coordinateInteriorQuerySucceeded
                    && coordinateInteriorId != 0;
                if (authoredStationInteriorShell
                    && !allowInconclusiveGroundSupport)
                {
                    failure = "The candidate is inside a building.";
                    return false;
                }

                if (float.IsNaN(referenceHeight) || float.IsInfinity(referenceHeight)
                    || Math.Abs(candidate.Z - referenceHeight) > 8f)
                {
                    failure = "The parking coordinates are inconsistent with their authored reference height.";
                    return false;
                }

                float groundZ = candidate.Z;
                Vector3 groundNormal = Vector3.Zero;
                bool groundConfirmed = false;
                try
                {
                    groundConfirmed = World.GetGroundHeightAndNormal(
                        candidate, out groundZ, out groundNormal);
                }
                catch (Exception ex)
                {
                    if (!allowInconclusiveGroundSupport)
                    {
                        failure = "GTA could not validate this "
                            + (parkedGround ? "parked-ground" : "roadside")
                            + " spot: " + ex.GetType().Name + ".";
                        return false;
                    }
                }
                bool groundEvidenceUsable = groundConfirmed
                    && !float.IsNaN(groundZ) && !float.IsInfinity(groundZ)
                    && !float.IsNaN(groundNormal.Z) && !float.IsInfinity(groundNormal.Z);
                if (!groundEvidenceUsable && !allowInconclusiveGroundSupport)
                {
                    failure = "GTA could not confirm level ground at the candidate.";
                    return false;
                }
                if (groundEvidenceUsable
                    && (groundNormal.Z < 0.55f
                        || Math.Abs(candidate.Z - groundZ) > 4f))
                {
                    failure = "Confirmed unsupported or steep ground at the candidate; NormalZ="
                        + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; HeightDelta=" + Math.Abs(candidate.Z - groundZ)
                            .ToString("0.00", CultureInfo.InvariantCulture) + ".";
                    return false;
                }

                Vector3 resolved = candidate;
                if (groundEvidenceUsable)
                    resolved.Z = groundZ;
                float roadHeading;
                Vector3 roadNode = World.GetNextPositionOnStreetWithHeading(
                    resolved, out roadHeading);
                if (roadNode != Vector3.Zero && Distance2D(resolved, roadNode) < 1.5f)
                {
                    failure = "The candidate is in the traffic lane.";
                    return false;
                }

                foreach (Vehicle nearby in World.GetNearbyVehicles(resolved, 5f))
                {
                    if (nearby != null && nearby.Exists()
                        && Distance2D(nearby.Position, resolved) < 4.5f)
                    {
                        failure = "Another vehicle occupies the candidate.";
                        return false;
                    }
                }

                foreach (Ped nearby in World.GetNearbyPeds(resolved, 3.5f))
                {
                    if (nearby == null || !nearby.Exists()
                        || (player != null && nearby.Handle == player.Handle))
                        continue;
                    if (Distance2D(nearby.Position, resolved) < 2.75f)
                    {
                        failure = "Another person occupies the candidate.";
                        return false;
                    }
                }

                safe = resolved;
                if (!groundEvidenceUsable)
                    failure = "Authored station ground query was inconclusive; retained its coordinates for grounded vehicle validation."
                        + (authoredStationInteriorShell
                            ? " Coordinate interior ID=" + coordinateInteriorId
                                .ToString(CultureInfo.InvariantCulture)
                                + " was treated as ambiguous for this authored exterior Station point; physical vehicle checks remain required."
                            : string.Empty);
                else if (authoredStationInteriorShell)
                    failure = "Coordinate interior ID="
                        + coordinateInteriorId.ToString(CultureInfo.InvariantCulture)
                        + " was treated as ambiguous for this authored exterior Station point; physical vehicle checks remain required.";
                return true;
            }
            catch (Exception ex)
            {
                failure = "GTA could not validate this "
                    + (parkedGround ? "parked-ground" : "roadside")
                    + " spot: " + ex.GetType().Name + ".";
                return false;
            }
        }

        private static float Distance2D(Vector3 first, Vector3 second)
        {
            float x = first.X - second.X;
            float y = first.Y - second.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        private void EnsurePreferredPoliceVehicleBlip(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
                return;
            if (_preferredPoliceVehicleBlip != null
                && _preferredPoliceVehicleBlip.Exists()
                && _preferredPoliceVehicleBlipTargetHandle == vehicle.Handle)
                return;

            ResetPreferredPoliceVehicleBlip();
            try
            {
                Blip blip = vehicle.AddBlip();
                if (blip == null || !blip.Exists())
                    return;
                blip.Name = "Preferred Police Vehicle";
                blip.Sprite = BlipSprite.PoliceCar;
                blip.Color = BlipColor.Blue;
                blip.IsShortRange = false;
                _preferredPoliceVehicleBlip = blip;
                _preferredPoliceVehicleBlipTargetHandle = vehicle.Handle;
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PREFERRED_VEHICLE_BLIP_FAILED", ex);
            }
        }

        private void ResetPreferredPoliceVehicleBlip()
        {
            try
            {
                if (_preferredPoliceVehicleBlip != null
                    && _preferredPoliceVehicleBlip.Exists())
                    _preferredPoliceVehicleBlip.Delete();
            }
            catch { }
            _preferredPoliceVehicleBlip = null;
            _preferredPoliceVehicleBlipTargetHandle = 0;
        }

        private void BeginPreferredPoliceVehicleFade(Vehicle vehicle)
        {
            ResetPreferredPoliceVehicleFade();
            if (vehicle == null || !vehicle.Exists())
                return;
            try
            {
                Function.Call(Hash.SET_ENTITY_ALPHA, vehicle.Handle, 0, false);
                _fadingPoliceVehicle = vehicle;
                _preferredPoliceVehicleFadeStartedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PREFERRED_VEHICLE_FADE_START_FAILED", ex);
            }
        }

        private void ProcessPreferredPoliceVehicleFade(bool paused)
        {
            if (paused || _fadingPoliceVehicle == null)
                return;
            if (!_fadingPoliceVehicle.Exists())
            {
                ResetPreferredPoliceVehicleFade();
                return;
            }

            const double fadeMilliseconds = 900.0;
            double elapsed = (DateTime.UtcNow - _preferredPoliceVehicleFadeStartedAt).TotalMilliseconds;
            int alpha = (int)Math.Max(0.0, Math.Min(255.0, elapsed * 255.0 / fadeMilliseconds));
            try
            {
                if (alpha >= 255)
                {
                    Function.Call(Hash.RESET_ENTITY_ALPHA, _fadingPoliceVehicle.Handle);
                    _fadingPoliceVehicle = null;
                    _preferredPoliceVehicleFadeStartedAt = DateTime.MinValue;
                }
                else
                {
                    Function.Call(Hash.SET_ENTITY_ALPHA, _fadingPoliceVehicle.Handle, alpha, false);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PREFERRED_VEHICLE_FADE_FAILED", ex);
                ResetPreferredPoliceVehicleFade();
            }
        }

        private void ResetPreferredPoliceVehicleFade()
        {
            try
            {
                if (_fadingPoliceVehicle != null && _fadingPoliceVehicle.Exists())
                    Function.Call(Hash.RESET_ENTITY_ALPHA, _fadingPoliceVehicle.Handle);
            }
            catch { }
            _fadingPoliceVehicle = null;
            _preferredPoliceVehicleFadeStartedAt = DateTime.MinValue;
        }

        private bool BeginPolicePedScreenFade()
        {
            try
            {
                // Leave any loading, pause, or externally-owned screen
                // transition alone. This fade belongs only to a real Player
                // officer-model change after its model has finished streaming.
                if (Function.Call<bool>(Hash.IS_SCREEN_FADED_OUT)
                    || Function.Call<bool>(Hash.IS_SCREEN_FADING_OUT)
                    || Function.Call<bool>(Hash.IS_SCREEN_FADING_IN))
                    return false;

                Function.Call(Hash.DO_SCREEN_FADE_OUT,
                    PolicePedScreenFadeOutMilliseconds);
                _ownsPolicePedScreenFade = true;
                _policePedScreenFadeInAt = DateTime.MinValue;
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PED_SCREEN_FADE_OUT_FAILED", ex);
                _ownsPolicePedScreenFade = false;
                _policePedScreenFadeInAt = DateTime.MinValue;
                return false;
            }
        }

        private void SchedulePolicePedScreenFadeIn()
        {
            if (!_ownsPolicePedScreenFade)
                return;

            _policePedScreenFadeInAt = DateTime.UtcNow.AddMilliseconds(
                PolicePedScreenFadeOutMilliseconds
                + PolicePedScreenFadeHoldMilliseconds);
        }

        private void ProcessPolicePedScreenFade()
        {
            if (!_ownsPolicePedScreenFade
                || _policePedScreenFadeInAt == DateTime.MinValue
                || DateTime.UtcNow < _policePedScreenFadeInAt)
                return;

            try
            {
                Function.Call(Hash.DO_SCREEN_FADE_IN,
                    PolicePedScreenFadeInMilliseconds);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PED_SCREEN_FADE_IN_FAILED", ex);
            }
            finally
            {
                _ownsPolicePedScreenFade = false;
                _policePedScreenFadeInAt = DateTime.MinValue;
            }
        }

        private void RestorePolicePedScreenFade()
        {
            if (!_ownsPolicePedScreenFade)
                return;

            try
            {
                Function.Call(Hash.DO_SCREEN_FADE_IN,
                    PolicePedScreenFadeInMilliseconds);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_PED_SCREEN_FADE_RESTORE_FAILED", ex);
            }
            finally
            {
                _ownsPolicePedScreenFade = false;
                _policePedScreenFadeInAt = DateTime.MinValue;
            }
        }

        private void CaptureNormalPlayerState()
        {
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return;

                var snapshot = new PlayerStateSnapshot { ModelHash = player.Model.Hash };
                Weapon current = player.Weapons.Current;
                snapshot.CurrentWeapon = current == null ? (WeaponHash?)null : current.Hash;
                foreach (WeaponHash hash in player.Weapons.GetAllWeaponHashes())
                {
                    Weapon weapon = player.Weapons[hash];
                    if (weapon != null && weapon.IsPresent)
                    {
                        var savedWeapon = new PlayerWeaponSnapshot(
                            hash,
                            weapon.Ammo,
                            weapon.Tint);
                        foreach (WeaponComponent component in weapon.Components)
                            if (component.Active)
                                savedWeapon.Components.Add(component.ComponentHash);
                        snapshot.Weapons.Add(savedWeapon);
                    }
                }
                _normalPlayerState = snapshot;
            }
            catch (Exception ex)
            {
                _normalPlayerState = null;
                _log.Exception("NORMAL_PLAYER_STATE_CAPTURE_FAILED", ex);
            }
        }

        private void QueueNormalPlayerRestore()
        {
            if (_normalPlayerState == null)
                return;
            _pendingNormalPlayerRestore = _normalPlayerState;
            _normalPlayerState = null;
            _pendingRestoreModel = new Model(_pendingNormalPlayerRestore.ModelHash);
            _pendingRestoreDeadline = DateTime.UtcNow.AddSeconds(30);
            _pendingWeaponsRestored = false;
            _log.Runtime(
                "NORMAL_PLAYER_STATE_RESTORE_QUEUED",
                "Player model and weapon collection will be restored from the next Script tick.");
        }

        private void ProcessPendingNormalPlayerRestore()
        {
            PlayerStateSnapshot snapshot = _pendingNormalPlayerRestore;
            if (snapshot == null)
                return;
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return;

                bool modelReady = player.Model.Hash == snapshot.ModelHash;
                if (!modelReady)
                {
                    Model model = _pendingRestoreModel;
                    if (model == null || !model.IsValid || !model.IsPed)
                    {
                        LogNormalRestoreFailure("The saved player model is unavailable.");
                        return;
                    }
                    if (!model.IsLoaded)
                    {
                        // The no-argument request never calls Script.Wait. The
                        // next game ticks will continue loading this model.
                        model.Request();
                        if (DateTime.UtcNow < _pendingRestoreDeadline)
                            return;
                    }
                    if (!model.IsLoaded)
                    {
                        LogNormalRestoreFailure("The saved player model did not load before timeout.");
                        return;
                    }
                    if (!Game.Player.ChangeModel(model))
                        return;
                    player = Game.Player.Character;
                    if (player == null || !player.Exists())
                        return;
                    modelReady = player.Model.Hash == snapshot.ModelHash;
                }

                if (!modelReady || _pendingWeaponsRestored)
                    return;
                player.Weapons.RemoveAll();
                foreach (PlayerWeaponSnapshot weapon in snapshot.Weapons)
                {
                    try
                    {
                        Weapon restored = player.Weapons.Give(
                            weapon.Hash, weapon.Ammo, false, true);
                        if (restored != null)
                        {
                            restored.Tint = weapon.Tint;
                            foreach (WeaponComponentHash component in weapon.Components)
                                restored.Components[component].Active = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Exception("NORMAL_PLAYER_WEAPON_RESTORE_FAILED", ex);
                    }
                }
                if (snapshot.CurrentWeapon.HasValue)
                    player.Weapons.Select(snapshot.CurrentWeapon.Value, true);
                _pendingWeaponsRestored = true;
                _log.Runtime("NORMAL_PLAYER_STATE_RESTORED", "Normal player model and weapon collection restored.");
                _pendingNormalPlayerRestore = null;
            }
            catch (Exception ex)
            {
                _log.Exception("NORMAL_PLAYER_STATE_RESTORE_FAILED", ex);
                if (DateTime.UtcNow >= _pendingRestoreDeadline)
                    _pendingNormalPlayerRestore = null;
            }
            finally
            {
                if (_pendingNormalPlayerRestore == null && _pendingRestoreModel != null)
                {
                    try { _pendingRestoreModel.MarkAsNoLongerNeeded(); } catch { }
                    _pendingRestoreModel = null;
                    _pendingRestoreDeadline = DateTime.MinValue;
                    _pendingWeaponsRestored = false;
                }
            }
        }

        private void LogNormalRestoreFailure(string message)
        {
            _log.Debug("NORMAL_PLAYER_STATE_RESTORE_SKIPPED", message);
            _pendingNormalPlayerRestore = null;
        }
        private void ReleaseManagedVehicle()
        {
            try
            {
                if (_managedPoliceVehicle == null || !_managedPoliceVehicle.Exists())
                    return;
                Ped player = Game.Player.Character;
                Vehicle current = player == null ? null : player.CurrentVehicle;
                RetireOwnedVehicle(_managedPoliceVehicle, current);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_VEHICLE_RELEASE_FAILED", ex);
            }
            finally
            {
                ResetPreferredPoliceVehicleBlip();
                ResetPreferredPoliceVehicleFade();
                _managedPoliceVehicle = null;
            }
        }

        private static void RetireOwnedVehicle(Vehicle owned, Vehicle playerVehicle)
        {
            bool playerInside = playerVehicle != null && playerVehicle.Exists()
                && playerVehicle.Handle == owned.Handle;
            Ped[] occupants = owned.Occupants;
            bool occupied = occupants != null
                && occupants.Any(value => value != null && value.Exists());
            if (!playerInside && !occupied)
            {
                owned.Delete();
                return;
            }

            // The vehicle is ours, but its current occupants may not be. Let GTA
            // manage it after the Police session instead of deleting those peds.
            owned.IsPersistent = false;
            owned.MarkAsNoLongerNeeded();
        }

        private bool RequireActive()
        {
            if (IsPoliceAuthorityActive)
                return true;
            LastOperationMessage = "Open Police Authority before changing the Police profile.";
            return false;
        }

        private bool Succeed(string category, string message)
        {
            LastOperationMessage = message;
            _log.Runtime(category, message);
            return true;
        }

        private bool TryGetPreferredPoliceVehiclePlacementRetry(
            Ped player,
            string modelName,
            LSPDPoliceStationDefinition station,
            bool forceReplaceManagedVehicle,
            bool allowSelectedStationFallback,
            out string category,
            out string message)
        {
            category = string.Empty;
            message = string.Empty;
            if (player == null || !player.Exists())
                return false;

            string stationId = station == null ? string.Empty : station.Id;
            bool sameAttempt = DateTime.UtcNow < _preferredPoliceVehiclePlacementRetryAfter
                && string.Equals(modelName, _preferredPoliceVehiclePlacementRetryModelName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(stationId, _preferredPoliceVehiclePlacementRetryStationId,
                    StringComparison.OrdinalIgnoreCase)
                && forceReplaceManagedVehicle == _preferredPoliceVehiclePlacementRetryForceReplaceManagedVehicle
                && allowSelectedStationFallback == _preferredPoliceVehiclePlacementRetryAllowsStationFallback
                && Distance2D(player.Position, _preferredPoliceVehiclePlacementRetryPlayerPosition)
                    <= PreferredPoliceVehiclePlacementRetryPositionTolerance;
            if (!sameAttempt)
            {
                ClearPreferredPoliceVehiclePlacementRetry();
                return false;
            }

            category = _preferredPoliceVehiclePlacementRetryCategory;
            message = _preferredPoliceVehiclePlacementRetryMessage;
            return !string.IsNullOrWhiteSpace(category)
                && !string.IsNullOrWhiteSpace(message);
        }

        private void RememberPreferredPoliceVehiclePlacementRetry(
            Ped player,
            string modelName,
            LSPDPoliceStationDefinition station,
            bool forceReplaceManagedVehicle,
            bool allowSelectedStationFallback,
            string category,
            string message)
        {
            if (player == null || !player.Exists()
                || string.IsNullOrWhiteSpace(modelName)
                || string.IsNullOrWhiteSpace(category)
                || string.IsNullOrWhiteSpace(message))
                return;

            _preferredPoliceVehiclePlacementRetryAfter = DateTime.UtcNow.AddMilliseconds(
                PreferredPoliceVehiclePlacementRetryBackoffMilliseconds);
            _preferredPoliceVehiclePlacementRetryPlayerPosition = player.Position;
            _preferredPoliceVehiclePlacementRetryModelName = modelName;
            _preferredPoliceVehiclePlacementRetryStationId = station == null
                ? string.Empty : station.Id;
            _preferredPoliceVehiclePlacementRetryCategory = category;
            _preferredPoliceVehiclePlacementRetryMessage = message;
            _preferredPoliceVehiclePlacementRetryAllowsStationFallback = allowSelectedStationFallback;
            _preferredPoliceVehiclePlacementRetryForceReplaceManagedVehicle = forceReplaceManagedVehicle;
            _log.Runtime(
                "POLICE_VEHICLE_PLACEMENT_RETRY_DEFERRED",
                "Model=" + modelName
                + "; Station=" + _preferredPoliceVehiclePlacementRetryStationId
                + "; PlayerPosition=" + FormatVehiclePosition(_preferredPoliceVehiclePlacementRetryPlayerPosition)
                + "; RetryAfterMs=" + PreferredPoliceVehiclePlacementRetryBackoffMilliseconds
                + "; Result=SamePlacementSearchSuppressedUntilPlayerMovesOrWaits");
        }

        private void ClearPreferredPoliceVehiclePlacementRetry()
        {
            _preferredPoliceVehiclePlacementRetryAfter = DateTime.MinValue;
            _preferredPoliceVehiclePlacementRetryPlayerPosition = Vector3.Zero;
            _preferredPoliceVehiclePlacementRetryModelName = string.Empty;
            _preferredPoliceVehiclePlacementRetryStationId = string.Empty;
            _preferredPoliceVehiclePlacementRetryCategory = string.Empty;
            _preferredPoliceVehiclePlacementRetryMessage = string.Empty;
            _preferredPoliceVehiclePlacementRetryAllowsStationFallback = false;
            _preferredPoliceVehiclePlacementRetryForceReplaceManagedVehicle = false;
        }

        private bool FailPreferredPoliceVehiclePlacement(
            string category,
            string message,
            bool reportFailure)
        {
            LastOperationMessage = message;
            string signature = category + "|" + message;
            if (string.Equals(
                signature,
                _lastPreferredPoliceVehiclePlacementFailureSignature,
                StringComparison.Ordinal))
                return false;

            _lastPreferredPoliceVehiclePlacementFailureSignature = signature;
            if (reportFailure)
                return Fail(category, message);
            _log.Debug(category, message);
            return false;
        }

        private bool Fail(string category, string message, Exception ex = null)
        {
            LastOperationMessage = message;
            if (ex == null)
                _log.Debug(category, message);
            else
                _log.Exception(category, ex);
            return false;
        }

        private static string NormalizeModelName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value.Trim().ToLowerInvariant();
        }

        internal LSPDAudioResult TestRadio()
        {
            return Audio.Report(
                "lsimmersivelife.police.radio.check",
                "radio-test",
                (++_radioTest).ToString(CultureInfo.InvariantCulture));
        }

        private void ActivateStationMarkers()
        {
            ResetStationMarkers();
            foreach (LSPDPoliceStationDefinition station in _profile.Stations)
            {
                try
                {
                    Blip marker = World.CreateBlip(new Vector3(
                        station.ExteriorX,
                        station.ExteriorY,
                        station.ExteriorZ));
                    if (marker == null || !marker.Exists())
                        continue;
                    marker.Name = "Police - " + station.DisplayName;
                    marker.Sprite = BlipSprite.PoliceStation;
                    marker.Color = BlipColor.Blue;
                    marker.IsShortRange = false;
                    _stationMarkers.Add(marker);
                }
                catch (Exception ex)
                {
                    _log.Exception("POLICE_STATION_MARKER_FAILED", ex);
                }
            }
            _log.Runtime("POLICE_STATION_MARKERS_READY", "Markers=" + _stationMarkers.Count);
        }

        private void ResetStationMarkers()
        {
            foreach (Blip marker in _stationMarkers.ToArray())
            {
                try { if (marker != null && marker.Exists()) marker.Delete(); } catch { }
            }
            _stationMarkers.Clear();
        }

        private bool TrySetStationWaypoint(LSPDPoliceStationDefinition station)
        {
            if (station == null)
                return false;
            try
            {
                World.WaypointPosition = new Vector3(
                    station.ExteriorX,
                    station.ExteriorY,
                    station.ExteriorZ);
                _log.Runtime("POLICE_STATION_GPS_SET", "Station=" + station.DisplayName);
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_STATION_GPS_FAILED", ex);
                return false;
            }
        }

        internal void PrepareAuthorityUi()
        {
            _log.Runtime("POLICE_AUTHORITY_UI_OPENED", "Police Authority menu opened.");
        }

        public void Dispose()
        {
            _config.SettingsChanged -= QueueConfigurationRefresh;
            ExitPoliceAuthority();
            AmbientWorld.CancelAllInteriorAccessSessions("PoliceCoreDisposed");
            ResetStationMarkers();
            _gangData.Reset();
            Audio.Dispose();
        }

        private sealed class PlayerStateSnapshot
        {
            internal int ModelHash { get; set; }
            internal WeaponHash? CurrentWeapon { get; set; }
            internal List<PlayerWeaponSnapshot> Weapons { get; private set; }

            internal PlayerStateSnapshot()
            {
                Weapons = new List<PlayerWeaponSnapshot>();
            }
        }

        private sealed class DispatchArrestInvincibilityState
        {
            internal readonly Ped Actor;
            internal readonly int Handle;
            internal readonly bool OriginalIsInvincible;

            internal DispatchArrestInvincibilityState(
                Ped actor,
                bool originalIsInvincible)
            {
                Actor = actor;
                Handle = actor == null ? 0 : actor.Handle;
                OriginalIsInvincible = originalIsInvincible;
            }
        }

        private sealed class PlayerWeaponSnapshot
        {
            internal PlayerWeaponSnapshot(WeaponHash hash, int ammo, WeaponTint tint)
            {
                Hash = hash;
                Ammo = ammo;
                Tint = tint;
                Components = new List<WeaponComponentHash>();
            }

            internal WeaponHash Hash { get; private set; }
            internal int Ammo { get; private set; }
            internal WeaponTint Tint { get; private set; }
            internal List<WeaponComponentHash> Components { get; private set; }
        }
    }
}
