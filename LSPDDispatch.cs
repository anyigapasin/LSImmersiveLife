using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace LSImmersiveLife
{
    // A controlled suspect's injury state follows the same Ped across
    // Dispatch/Convoy ownership. The current owner alone may process it.
    internal sealed class LSPDSuspectInjuryState
    {
        internal Ped Actor;
        internal int OriginalHealth;
        internal int OriginalMaximumHealth;
        internal int OriginalSelectedWeaponHash;
        internal int LastHealth;
        internal int DamageTaken;
        internal int RegisteredHits;
        internal int KnownDamageHits;
        internal int DownedHits;
        internal int KnownDownedHits;
        internal int MeleeHits;
        internal int ManagedMaximumHealth;
        internal int ArrestableWoundedHealthFloor;
        internal int HeadHits;
        internal int ChestHits;
        internal int StomachHits;
        internal int LastValidDamageBone;
        internal DateTime LastValidDamageBoneAt = DateTime.MinValue;
        internal DateTime LastConvoyHealthCheckAt = DateTime.MinValue;
        internal string OccurrenceId;
        internal string LastDamageKind;
        internal int LastDamageWeaponHash;
        internal int LastDamageAttackerHandle;
        internal int LastDamageGameTime;
        internal string RuntimeOwner = "Dispatch";
        internal bool OriginalCanSufferCriticalHits;
        internal bool OriginalCanSwitchWeapons;
        internal bool Wounded;
        internal bool Fatal;
        internal bool WeaponDisabledByInjury;
        internal bool WritheTaskStarted;
        internal bool MeleeReactionPending;
        internal bool ConvoyTerminalLogged;
        internal DateTime MeleeReactionUntil = DateTime.MinValue;
        internal DateTime NextMeleeRecoveryCheckAt = DateTime.MinValue;
    }

    /// <summary>
    /// Owns the sparse, XML-defined universal Police callout loop. It offers
    /// one incident at a time during an explicitly started patrol, creates only
    /// the scene actors it owns, and advances the incident through arrival,
    /// investigation, compliance, arrest, custody handoff, and terminal cleanup.
    /// Crime Activity and Gang response remain separate owners.
    /// </summary>
    internal sealed class LSPDDispatch
    {
        // This owner creates at most one sparse, XML-defined universal dispatch
        // assignment. It is active only while Police Authority and Patrol are
        // active, never reads Crime Activity as a callout source, and never
        // takes ownership of entities it did not create.
        // Investigation is a local scene action, not a marker-precision test.
        // Keep the player within the authored area while allowing a practical
        // approach radius before the officer reaches the exact blip position.
        private const int SceneArrivalRadius = 100;
        internal const int InitialPatrolOfferDelaySeconds = 30;
        private const int ArrestRadius = 5;
        private const int SurrenderSettleSeconds = 2;
        private const int HandcuffedPedConfigFlag = 120;
        // Keep GTA's authored arrest task alive through the visible handcuff
        // motion before Dispatch takes custody of the secured suspect.
        private const int PlayerHandcuffAnimationMilliseconds = 6500;
        private const int PlayerHandcuffMaximumAttempts = 2;
        private const int PlayerHandcuffAnimationLoadTimeoutSeconds = 6;
        private const float SynchronizedArrestMaximumDistance = 2.6f;
        private const int BackupArrestTaskTimeoutSeconds = 12;
        private const int CompliantTaskRefreshMilliseconds = 7000;
        private const int SecuredPoseRefreshMilliseconds = 2500;
        private const int DefaultDeferredCleanupGraceSeconds = 8;
        private const int DefaultDeferredCleanupMaximumSeconds = 60;
        private const float DefaultDeferredCleanupDistance = 200f;
        private const int MaintenanceMilliseconds = 750;
        private const int ScenePreparationTimeoutSeconds = 15;
        private const int SceneActorSetupRetryLimit = 2;
        private const int SceneActorSetupRetryDelayMilliseconds = 1000;
        private const int SceneResolutionPendingMaximumSeconds = 15;
        private const int SceneResolutionRetryDelayMilliseconds = 1000;
        private const int MaximumPlayerRadiusVehicleDefinitionCandidates = 2;
        private const int MaximumPlayerRadiusRoadFootDefinitionCandidates = 1;
        private const int MaximumPlayerRadiusGenericFootDefinitionCandidates = 1;
        private const float PlayerRadiusVehicleRoadSnapDistance = 45f;
        private const int PairedSceneAnimationLoadTimeoutSeconds = 10;
        private const float PairedSceneAnimationApproachDistance = 38f;
        private static readonly Vector3[] DispatchStoreParticipantOffsets =
        {
            new Vector3(3.6f, 0f, 0f),
            new Vector3(-3.6f, 0f, 0f),
            new Vector3(0f, 3.6f, 0f)
        };
        private const float PursuitWaypointMovementMeters = 35f;
        private const int PursuitWaypointRefreshSeconds = 4;
        private const int PursuitLocationRadioGapSeconds = 20;
        private const int MaxCriminalAssetSelectionAttempts = 12;
        // Location compatibility is metadata-only until AmbientWorld checks
        // the streamed ground/interior/occupancy state. Let a request fall
        // Validate alternate authored definitions only during an offer-time
        // request pass; this is never a per-frame catalog scan.
        private const int InteriorExitMaximumRecoveries = 3;
        private const int InteriorExitNavigationTimeoutSeconds = 60;
        private const int InteriorExitVehicleEntryTimeoutSeconds = 12;

        private readonly LSPDAudioDispatch _audio;
        private readonly LSImmersiveLog _log;
        private readonly string _catalogPath;
        private readonly string _locationCatalogPath;
        private readonly Random _random = new Random();
        private readonly LSPDControlBindings _controls;
        private readonly LSPoliceDispatchSettings _settings;
        private readonly LSPoliceCleanupSettings _cleanupSettings;
        private readonly LSUniversalUiSettings _uiSettings;
        private readonly List<LSPDDispatchEventDefinition> _definitions =
            new List<LSPDDispatchEventDefinition>();
        private readonly Dictionary<string, LSPDCriminalProfileDefinition> _criminalProfiles =
            new Dictionary<string, LSPDCriminalProfileDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly LSPDCriminalAssetCatalog _criminalAssets =
            new LSPDCriminalAssetCatalog();
        private readonly Queue<string> _recentCriminalAssetIds = new Queue<string>();
        private readonly HashSet<string> _recentCriminalAssetIdSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LSPDDispatchLocationDefinition> _locations =
            new Dictionary<string, LSPDDispatchLocationDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LSPDDispatchBranchDefinition> _branches =
            new Dictionary<string, LSPDDispatchBranchDefinition>(StringComparer.OrdinalIgnoreCase);
        private LSImmersiveLocationCatalog _locationCatalog;
        private LSWorldAmbientBehavior _ambientWorld;
        private readonly List<DeferredCleanup> _deferredCleanup =
            new List<DeferredCleanup>();
        private readonly Dictionary<string, DateTime> _definitionOfferedAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, DateTime> _lastCompliantTaskAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastSecuredPoseTaskAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, LSPDSuspectInjuryState> _suspectInjuries =
            new Dictionary<int, LSPDSuspectInjuryState>();
        private readonly Dictionary<int, BackupArrestOperation> _backupArrestOperations =
            new Dictionary<int, BackupArrestOperation>();
        private readonly HashSet<string> _pendingSceneResolutionDefinitionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private LSPDDispatchEvent _incident;
        private LSPDDispatchEvent _pendingOffer;
        private Blip _sceneBlip;
        private readonly Dictionary<int, Blip> _suspectBlips =
            new Dictionary<int, Blip>();
        private int _primarySuspectBlipHandle;
        private DateTime _nextSuspectBlipMaintenanceAt = DateTime.MinValue;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private DateTime _nextOfferAt = DateTime.MinValue;
        private DateTime _manualRequestRetryAt = DateTime.MinValue;
        private DateTime _nextMaintenance = DateTime.MinValue;
        private DateTime _nextInteriorPinMaintenanceAt = DateTime.MinValue;
        private DateTime _playerHandcuffHoldUntil = DateTime.MinValue;
        private int _playerHandcuffAttempts;
        private DateTime _playerHandcuffAnimationLoadUntil = DateTime.MinValue;
        private string _playerHandcuffWaitingDictionary;
        private bool _playerHandcuffWaitingForAnimation;
        private bool _playerHandcuffPairedAnimationStarted;
        private int _playerHandcuffSceneId = -1;
        private DateTime _playerHandcuffAnimationVerifyAt = DateTime.MinValue;
        private bool _playerHandcuffAnimationVerified;
        private bool _playerHandcuffPreserveTargetPosture;
        private string _playerHandcuffDictionary;
        private string _playerHandcuffOfficerClip;
        private string _playerHandcuffSuspectClip;
        private Vector3 _playerHandcuffSceneOrigin = Vector3.Zero;
        private float _playerHandcuffSceneHeading;
        private DateTime _lastPatrolObserved = DateTime.MinValue;
        private DateTime _lastOfferAudioAt = DateTime.MinValue;
        private bool _patrolSessionStarted;
        private bool _dispatchProducerEnabled;
        private bool _pendingOfferBypassesAutomaticQuietPeriod;
        private bool _pendingOfferWaitingLogged;
        private int _pendingOfferMinimumCooldownSeconds;
        private int _pendingSceneResolutionRetryCount;
        private DateTime _pendingSceneResolutionStartedAt = DateTime.MinValue;
        private bool _acceptKeyDown;
        private bool _rejectKeyDown;
        private bool _investigateKeyDown;
        private bool _secureKeyDown;
        private bool _hadPreviousWaypoint;
        private Vector3 _previousWaypoint = Vector3.Zero;
        private readonly string _criminalProfilePath;

        private int DeferredCleanupGraceSeconds
        {
            get
            {
                return _cleanupSettings == null
                    ? DefaultDeferredCleanupGraceSeconds
                    : _cleanupSettings.CompletedSceneGraceSeconds;
            }
        }

        private int DeferredCleanupMaximumSeconds
        {
            get
            {
                return _cleanupSettings == null
                    ? DefaultDeferredCleanupMaximumSeconds
                    : _cleanupSettings.HardCleanupSeconds;
            }
        }

        private float DeferredCleanupDistance
        {
            get
            {
                return _cleanupSettings == null
                    ? DefaultDeferredCleanupDistance
                    : _cleanupSettings.SafeCleanupDistance;
            }
        }

        private sealed class DeferredCleanup
        {
            internal Ped Ped;
            internal Vehicle Vehicle;
            internal string OccurrenceId;
            internal int LiveInteriorId;
            internal string Owner;
            internal string CleanupReason;
            internal DateTime Earliest;
            internal DateTime Expires;
            internal bool PreserveUntilPlayerLeavesArea;
        }

        private sealed class BackupArrestOperation
        {
            internal Ped Officer;
            internal Ped Suspect;
            internal int SceneId = -1;
            internal DateTime AnimationCompleteAt;
            internal DateTime TaskDeadline;
            internal bool PairedAnimationStarted;
            internal bool NativeTaskFallbackStarted;
        }

        private enum DispatchHitZone
        {
            Unknown,
            Limb,
            Head,
            Chest,
            Stomach
        }

        private enum ControlledDamageKind
        {
            Unknown,
            Weapon,
            Melee
        }

        private enum SceneOfferPreflightStatus
        {
            Failed,
            Pending,
            Ready
        }

        private LSPoliceSuspectInjurySettings _injurySettings =
            LSPoliceSuspectInjurySettings.Default();

        internal LSPDDispatch(LSPDAudioDispatch audio)
            : this(audio, null, null, null, null, null)
        {
        }

        internal LSPDDispatch(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log)
            : this(audio, paths, log, null, null, null)
        {
        }

        internal LSPDDispatch(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log,
            LSPDControlBindings controls)
            : this(audio, paths, log, controls, null, null)
        {
        }

        internal LSPDDispatch(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log,
            LSPDControlBindings controls,
            LSPoliceDispatchSettings settings,
            LSPoliceCleanupSettings cleanupSettings)
            : this(audio, paths, log, controls, settings, cleanupSettings, null)
        {
        }

        internal LSPDDispatch(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log,
            LSPDControlBindings controls,
            LSPoliceDispatchSettings settings,
            LSPoliceCleanupSettings cleanupSettings,
            LSUniversalUiSettings uiSettings)
        {
            _audio = audio ?? throw new ArgumentNullException("audio");
            _log = log;
            _controls = controls ?? LSPDControlBindings.Default();
            _settings = settings ?? LSPoliceDispatchSettings.Default();
            _cleanupSettings = cleanupSettings ?? LSPoliceCleanupSettings.Default();
            _uiSettings = uiSettings ?? LSUniversalUiSettings.Default();
            _catalogPath = paths == null ? null : paths.DispatchEventXmlPath;
            _locationCatalogPath = paths == null ? null : paths.LocationCatalogXmlPath;
            _criminalProfilePath = paths == null ? null : paths.CriminalProfileXmlPath;
            ReloadDefinitions();
        }

        internal LSImmersiveLocationCatalog LocationCatalog
        {
            get { return _locationCatalog; }
        }

        internal void AttachAmbientWorld(LSWorldAmbientBehavior ambientWorld)
        {
            _ambientWorld = ambientWorld;
            if (_ambientWorld != null)
                _ambientWorld.UpdateLocationCatalog(_locationCatalog);
        }

        internal void ConfigureSuspectInjurySettings(
            LSPoliceSuspectInjurySettings settings)
        {
            _injurySettings = settings ?? LSPoliceSuspectInjurySettings.Default();
            _injurySettings.Normalize();
        }

        internal bool HasOffer
        {
            get { return _incident != null && _incident.State == LSPDDispatchState.Offered; }
        }

        internal bool HasIncident
        {
            get { return _incident != null; }
        }

        internal LSPDDispatchState State
        {
            get { return _incident == null ? LSPDDispatchState.None : _incident.State; }
        }

        internal bool HasPendingVictimInvestigation
        {
            get
            {
                return _incident != null
                    && _incident.VictimStartsDead
                    && !_incident.VictimInvestigated;
            }
        }

        internal LSPDDispatchEvent Current
        {
            get { return _incident; }
        }

        internal Ped CurrentSuspect
        {
            get { return _incident == null ? null : _incident.Suspect; }
        }

        internal Ped CurrentPursuitTarget
        {
            get { return _incident == null ? null : _incident.CurrentPursuitTarget; }
        }

        /// <summary>
        /// Read-only runtime view used by Backup and Convoy coordination. Each
        /// ped in this collection is a real Dispatch-owned criminal, unlike
        /// witnesses or activity-only scene participants.
        /// </summary>
        internal IEnumerable<Ped> CurrentSuspects
        {
            get
            {
                return _incident == null || _incident.Suspects == null
                    ? Enumerable.Empty<Ped>()
                    : _incident.Suspects.Where(ped => ped != null && ped.Exists());
            }
        }

        internal IEnumerable<Vehicle> CurrentSuspectVehicles
        {
            get
            {
                return _incident == null || _incident.SuspectVehicles == null
                    ? Enumerable.Empty<Vehicle>()
                    : _incident.SuspectVehicles.Where(vehicle => vehicle != null && vehicle.Exists());
            }
        }

        internal IEnumerable<Ped> CurrentArrestedSuspects
        {
            get
            {
                return _incident == null || _incident.ArrestedSuspects == null
                    ? Enumerable.Empty<Ped>()
                    : _incident.ArrestedSuspects.Where(ped => ped != null && ped.Exists() && !ped.IsDead);
            }
        }

        internal bool CanReleaseCurrentSuspect
        {
            get
            {
                Ped suspect = CurrentSuspect;
                return _incident != null
                    && _incident.OwnedByDispatch
                    && !_incident.HasConvoyCustodyHandoff
                    && !_incident.PlayerHandcuffInProgress
                    && suspect != null && suspect.Exists() && !suspect.IsDead
                    && !suspect.IsInVehicle()
                    && IsArrested(_incident, suspect);
            }
        }

        internal int ActiveSuspectCount
        {
            get { return CurrentSuspects.Count(ped => !ped.IsDead); }
        }

        internal bool HasSuspectGroup
        {
            get { return ActiveSuspectCount > 1; }
        }

        internal string CurrentTitle
        {
            get { return _incident == null ? string.Empty : _incident.Title; }
        }

        internal string CurrentBriefing
        {
            get { return _incident == null ? string.Empty : _incident.Briefing; }
        }

        internal string CurrentStatus
        {
            get
            {
                if (_incident == null)
                    return "No active dispatch.";
                return _incident.Title + " | " + _incident.State;
            }
        }

        internal int EventDefinitionCount { get { return _definitions.Count; } }
        internal bool IsEnabled { get { return _settings.Enabled; } }
        internal int CriminalProfileCount { get { return _criminalProfiles.Count; } }
        internal LSPDCriminalAssetCatalog CriminalAssets { get { return _criminalAssets; } }
        internal int CriminalPedAssetCount { get { return _criminalAssets.PedCount; } }
        internal int CriminalVehicleAssetCount { get { return _criminalAssets.VehicleCount; } }
        internal int CriminalWeaponAssetCount { get { return _criminalAssets.WeaponCount; } }
        internal int AuthoredLocationCount { get { return _locations.Count; } }

        internal bool HasConvoyCustodyHandoff
        {
            get { return _incident != null && _incident.HasConvoyCustodyHandoff; }
        }

        /// <summary>
        /// Supplies profile data for the explicit Convoy Request activity. This
        /// is deliberately data-only: Convoy owns every spawned prisoner and
        /// route threat, while Dispatch remains free of a second active case.
        /// </summary>
        internal bool TryChooseConvoyRequestProfiles(
            out LSPDCriminalProfileDefinition prisonerProfile,
            out LSPDCriminalProfileDefinition routeThreatProfile)
        {
            prisonerProfile = null;
            routeThreatProfile = null;
            List<LSPDCriminalProfileDefinition> usable = _criminalProfiles.Values
                .Where(profile => profile != null && profile.IsEligibleFor("convoy")
                    && !string.IsNullOrWhiteSpace(profile.ModelName))
                .ToList();
            if (usable.Count == 0)
                return false;

            List<LSPDCriminalProfileDefinition> custodyCandidates = usable
                .Where(profile => !profile.Armed).ToList();
            if (custodyCandidates.Count == 0)
                custodyCandidates = usable;
            List<LSPDCriminalProfileDefinition> threatCandidates = usable
                .Where(profile => profile.Armed && !string.IsNullOrWhiteSpace(profile.WeaponName))
                .ToList();

            string prisonerFailure;
            if (!TryResolveUsableCriminalProfile(
                custodyCandidates,
                "convoy",
                false,
                out prisonerProfile,
                out prisonerFailure))
            {
                LogDebug(
                    "POLICE_CONVOY_CRIMINAL_ASSET_SELECTION_FAILED",
                    "Role=prisoner; Reason=" + prisonerFailure);
                return false;
            }

            string threatFailure;
            if (threatCandidates.Count > 0
                && !TryResolveUsableCriminalProfile(
                    threatCandidates,
                    "convoy",
                    true,
                    out routeThreatProfile,
                    out threatFailure))
            {
                // The prisoner remains a valid independent request. The
                // route threat is optional in the authored catalog, but its
                // failure must be visible rather than converted to a custody
                // failure or a fabricated fallback actor.
                routeThreatProfile = null;
                LogDebug(
                    "POLICE_CONVOY_ROUTE_THREAT_ASSET_SELECTION_FAILED",
                    "Reason=" + threatFailure);
            }
            RememberProfileAssets(prisonerProfile);
            RememberProfileAssets(routeThreatProfile);
            return true;
        }

        internal string AvailabilityStatus
        {
            get
            {
                if (_incident != null)
                    return CurrentStatus;
                if (!_settings.Enabled)
                    return "Dispatch is disabled in LS Immersive settings.";
                if (!_patrolSessionStarted)
                    return "Start Patrol to receive universal Dispatch calls.";
                if (DateTime.UtcNow < _nextOfferAt)
                    return "Quiet patrol. Dispatch remains available when the next call is due.";
                return "Quiet patrol. Dispatch is checking its authored callout catalog.";
            }
        }

        internal bool IsInTransportLifecycle
        {
            get
            {
                return _incident != null &&
                    (_incident.State == LSPDDispatchState.Arrested
                     || _incident.State == LSPDDispatchState.AwaitingTransport
                     || _incident.State == LSPDDispatchState.HoldingAtStation
                     || _incident.State == LSPDDispatchState.PrisonTransfer);
            }
        }

        /// <summary>
        /// Advances the universal callout producer and its active incident.
        /// Offers are sparse and patrol-gated; no callout exists before the
        /// officer explicitly starts Patrol.
        /// </summary>
        internal void Process(bool isPatrolling, bool npcInteractionActive, bool paused)
        {
            Process(isPatrolling, npcInteractionActive, paused, true, false);
        }

        internal void Process(
            bool isPatrolling,
            bool npcInteractionActive,
            bool paused,
            bool allowGameplayInput)
        {
            Process(isPatrolling, npcInteractionActive, paused, allowGameplayInput, false);
        }

        internal void Process(
            bool isPatrolling,
            bool npcInteractionActive,
            bool paused,
            bool allowGameplayInput,
            bool otherPoliceIncidentActive)
        {
            Process(
                isPatrolling,
                npcInteractionActive,
                paused,
                allowGameplayInput,
                otherPoliceIncidentActive,
                false);
        }

        internal void Process(
            bool isPatrolling,
            bool npcInteractionActive,
            bool paused,
            bool allowGameplayInput,
            bool otherPoliceIncidentActive,
            bool suppressEnterInput)
        {
            DateTime now = DateTime.UtcNow;
            ProcessDeferredCleanup(now);
            if (now >= _nextInteriorPinMaintenanceAt)
            {
                _nextInteriorPinMaintenanceAt = now.AddSeconds(1);
                ReleaseUnusedDispatchInteriorPins("InteriorPinMaintenance");
            }

            // Pause can happen while the player is holding a decision key. Keep
            // the edge tracker aligned with the real keyboard state before the
            // next unpaused tick, otherwise a held key could be interpreted as a
            // fresh dispatch decision after the game resumes.
            if (paused)
            {
                SynchronizeKeyboardInputStates();
                return;
            }

            if (_incident != null && _incident.PlayerHandcuffInProgress)
                LSPDPlayerControlSuppression.SuppressMeleeThisFrame();

            if (!allowGameplayInput || _incident == null)
                SynchronizeKeyboardInputStates();

            if (!isPatrolling)
            {
                CancelPendingOffer("Patrol ended before scene preparation completed.");
                ResetPendingSceneResolution();
                MaintainTrackedSuspectInjuries(now, Game.Player.Character);
                _patrolSessionStarted = false;
                _dispatchProducerEnabled = false;
                return;
            }

            if (!_patrolSessionStarted)
            {
                _patrolSessionStarted = true;
                _lastPatrolObserved = now;
                _dispatchProducerEnabled = _settings.Enabled;
                _nextOfferAt = _settings.Enabled
                    ? now.AddSeconds(InitialPatrolOfferDelaySeconds)
                    : DateTime.MaxValue;
                LogRuntime(
                    "POLICE_DISPATCH_PATROL_READY",
                    "Universal dispatch producer armed; initial automatic offer scheduled after "
                    + InitialPatrolOfferDelaySeconds + " seconds.");
            }

            if (_incident != null)
            {
                CancelPendingOffer("Another Dispatch assignment became active.");
                ResetPendingSceneResolution();
                if (allowGameplayInput)
                    ProcessControllerInput(suppressEnterInput);
                if (now < _nextMaintenance)
                {
                    MaintainTrackedSuspectInjuries(now, Game.Player.Character);
                    return;
                }
                _nextMaintenance = now.AddMilliseconds(MaintenanceMilliseconds);
                // Classify any fresh damage before scene maintenance can
                // reach its ordinary death/conclusion checks.
                MaintainTrackedSuspectInjuries(now, Game.Player.Character);
                MaintainSuspectBlips(now);
                MaintainIncident(now, Game.Player.Character);
                return;
            }

            if (!_settings.Enabled)
            {
                CancelPendingOffer("Dispatch was disabled before scene preparation completed.");
                ResetPendingSceneResolution();
                _dispatchProducerEnabled = false;
                return;
            }
            if (!_dispatchProducerEnabled)
            {
                _dispatchProducerEnabled = true;
                _nextOfferAt = now.AddSeconds(NextQuietPatrolSeconds());
                LogRuntime("POLICE_DISPATCH_ENABLED", "Dispatch producer re-armed after configuration enabled it.");
                return;
            }

            if (_pendingOffer != null)
            {
                if (npcInteractionActive || otherPoliceIncidentActive)
                {
                    CancelPendingOffer("Another Police activity took priority during scene preparation.");
                    return;
                }
                if (now >= _nextOfferAt)
                {
                    Ped pendingPlayer = Game.Player.Character;
                    if (pendingPlayer == null || !pendingPlayer.Exists())
                    {
                        CancelPendingOffer("The player character became unavailable during scene preparation.");
                        return;
                    }
                    TryAdvancePendingOffer(pendingPlayer, now);
                }
                return;
            }

            if (npcInteractionActive || otherPoliceIncidentActive || now < _cooldownUntil || now < _nextOfferAt)
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            TryCreateOffer(player, now, false);
        }

        internal string RequestOfferNow(Ped player)
        {
            DateTime now = DateTime.UtcNow;
            if (!_settings.Enabled)
                return "Dispatch is disabled in LS Immersive settings.";
            if (!_patrolSessionStarted)
            {
                // A menu action can arrive in the same frame as Patrol.Start,
                // before the normal Police Core tick has armed this producer.
                // The explicit request is already patrol-gated by Police Core,
                // so arm this existing session instead of rejecting the action.
                _patrolSessionStarted = true;
                _dispatchProducerEnabled = true;
            }
            if (_incident != null)
                return "A Dispatch assignment is already active.";
            if (_pendingOffer != null)
                return "Dispatch is preparing the scene. The call will appear in Police Radio when it is ready.";
            if (_pendingSceneResolutionDefinitionIds.Count > 0)
                return "Dispatch is waiting briefly for the selected scene's readiness probes.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (now < _manualRequestRetryAt)
                return "Dispatch just checked nearby locations. Wait a few seconds before requesting again.";

            if (!TryCreateOffer(player, now, true))
            {
                if (_pendingOffer != null)
                    return "Dispatch is preparing the scene. The call will appear in Police Radio when it is ready.";
                if (_pendingSceneResolutionDefinitionIds.Count > 0)
                    return "Dispatch is waiting briefly for the selected scene's readiness probes.";
                _manualRequestRetryAt = now.AddSeconds(5);
                return _definitions.Count == 0
                    ? "Dispatch has no usable callout definitions in its XML catalog."
                    : "Dispatch could not validate a complete compatible scene nearby.";
            }
            return "Dispatch request received. Review the new call in Police Radio.";
        }

        private bool TryCreateOffer(
            Ped player,
            DateTime now,
            bool bypassAutomaticQuietPeriod)
        {
            if (_pendingOffer != null)
                return TryAdvancePendingOffer(player, now);

            int playerLiveInteriorId = 0;
            bool playerLiveInteriorIdAvailable = _ambientWorld != null
                && _ambientWorld.TryGetDispatchPlayerInteriorId(
                    player, out playerLiveInteriorId);
            Vector3 playerRoadAnchor = Vector3.Zero;
            bool playerOutsideLiveInterior = playerLiveInteriorIdAvailable
                && playerLiveInteriorId == 0;
            List<LSPDDispatchEventDefinition> locationCandidates =
                _pendingSceneResolutionDefinitionIds.Count > 0
                    ? _definitions.Where(value => value.Enabled
                        && _pendingSceneResolutionDefinitionIds.Contains(value.Id)
                        && value.MinimumSuspects <= _settings.MaximumSuspects).ToList()
                    : ChooseLocationDefinitionCandidates(
                        player,
                        Math.Max(1, _definitions.Count),
                        playerLiveInteriorId,
                        playerLiveInteriorIdAvailable);
            if (locationCandidates.Count == 0)
            {
                ResetPendingSceneResolution();
                _nextOfferAt = _definitions.Count == 0
                    ? now.AddSeconds(30)
                    : NextDefinitionEligibleAt(now);
                if (_definitions.Count == 0)
                    LogDebug("POLICE_DISPATCH_NO_DEFINITIONS", "Dispatch XML contained no usable callout definitions.");
                return false;
            }

            LSPDDispatchEventDefinition definition = null;
            AmbientDispatchSceneResolution sceneResolution = null;
            string locationFailure = "AmbientWorld is not attached by PoliceCore.";
            var locationFailures = new List<string>();
            var preferredInteriorFailures = new List<string>();
            var pendingVehicleSceneFailures = new List<string>();
            foreach (LSPDDispatchEventDefinition candidateDefinition in locationCandidates)
            {
                List<LSPDDispatchLocationDefinition> eventAreas =
                    new List<LSPDDispatchLocationDefinition>();
                foreach (string locationId in candidateDefinition.LocationIds
                    ?? Enumerable.Empty<string>())
                {
                    LSPDDispatchLocationDefinition area;
                    if (_locations.TryGetValue(locationId, out area)
                        && candidateDefinition.CanUseAreaContext(area))
                        eventAreas.Add(area);
                }

                locationFailure = "AmbientWorld is not attached by PoliceCore.";
                // RequiresVehicle selects the suspect's criminal vehicle; the
                // separate staging flag requests an exterior Player Utility
                // position. Both need a staging coordinate, but only
                // RequiresVehicle may create or assign a suspect vehicle.
                bool requiresVehicleStaging = candidateDefinition.RequiresVehicle
                    || candidateDefinition.VehicleStagingRequired;
                if (_ambientWorld != null
                    && _ambientWorld.TryResolveDispatchScene(
                    eventAreas,
                    candidateDefinition.IncidentType,
                    player.Position,
                    requiresVehicleStaging,
                    out sceneResolution,
                    out locationFailure,
                    Vector3.Zero,
                    candidateDefinition.InteriorLocationId,
                    candidateDefinition.RequiredPhysicalType,
                    candidateDefinition.LocationContext,
                    candidateDefinition.RequiresMappedInterior,
                    playerLiveInteriorId,
                    playerLiveInteriorIdAvailable,
                    candidateDefinition.VehicleActivity,
                    false))
                {
                    definition = candidateDefinition;
                    break;
                }

                string candidateFailure = candidateDefinition.Id + "="
                    + (locationFailure ?? "Location validation failed.");
                locationFailures.Add(candidateFailure);
                if (candidateDefinition.RequiresMappedInterior
                    || !string.IsNullOrWhiteSpace(candidateDefinition.InteriorLocationId))
                    preferredInteriorFailures.Add(candidateFailure);
                sceneResolution = null;

                // One unresolved mapped site is enough to hold this offer.
                // Retrying every robbery/attack/murder definition against the
                // same doors would repeat the same native preparation work.
                // The next pass retries this authored event; a different
                // branch remains eligible after the bounded wait expires.
                bool mappedInteriorCandidate = candidateDefinition.RequiresMappedInterior
                    || !string.IsNullOrWhiteSpace(candidateDefinition.InteriorLocationId);
                if (mappedInteriorCandidate
                    && candidateFailure.IndexOf(
                        "=PENDING:", StringComparison.OrdinalIgnoreCase) >= 0)
                    break;
            }

            // An explicit request first tries the same randomized mapped/catalog
            // candidates as every other request. If none resolves, a Player
            // outside a live interior may receive a runtime-validated nearby
            // exterior scene. Road definitions are included only when a real
            // road node is available; generic outdoor definitions work away
            // from roads. Mapped-interior events never use this fallback.
            bool hasPendingInteriorResolution = preferredInteriorFailures.Any(failure =>
                failure.IndexOf("=PENDING:", StringComparison.OrdinalIgnoreCase) >= 0);
            if (definition == null
                && playerOutsideLiveInterior
                && !hasPendingInteriorResolution)
            {
                bool playerRadiusRoadSceneAvailable =
                    TryGetPlayerRadiusRoadVehicleAnchor(player, out playerRoadAnchor);
                LogRuntime("POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_REQUEST",
                    "Player=" + player.Handle
                    + "; Vehicle=" + (player.CurrentVehicle == null
                        ? 0 : player.CurrentVehicle.Handle)
                    + "; RoadAnchor=" + (playerRadiusRoadSceneAvailable
                        ? playerRoadAnchor.ToString() : "Unavailable")
                    + "; RequestMode=" + (bypassAutomaticQuietPeriod
                        ? "ExplicitRadio" : "ScheduledDispatch")
                    + "; Resolution=CatalogFirstThenPlayerRadiusOutdoorFallback");
                if (bypassAutomaticQuietPeriod && playerRadiusRoadSceneAvailable)
                {
                    LogRuntime("POLICE_DISPATCH_PLAYER_RADIUS_ROAD_REQUEST",
                        "Player=" + player.Handle
                        + "; Vehicle=" + (player.CurrentVehicle == null
                            ? 0 : player.CurrentVehicle.Handle)
                        + "; RoadAnchor=" + playerRoadAnchor
                        + "; SceneResolution=CatalogFirstThenPlayerRadiusOutdoorFallback");
                }
                IEnumerable<string> pendingDefinitionIds =
                    _pendingSceneResolutionDefinitionIds.Count > 0
                        ? _pendingSceneResolutionDefinitionIds : null;
                List<LSPDDispatchEventDefinition> outdoorFallbackCandidates =
                    ChoosePlayerRadiusOutdoorFallbackCandidates(
                        now, playerRadiusRoadSceneAvailable, pendingDefinitionIds);
                foreach (LSPDDispatchEventDefinition candidateDefinition in outdoorFallbackCandidates)
                {
                    List<LSPDDispatchLocationDefinition> eventAreas =
                        new List<LSPDDispatchLocationDefinition>();
                    foreach (string locationId in candidateDefinition.LocationIds
                        ?? Enumerable.Empty<string>())
                    {
                        LSPDDispatchLocationDefinition area;
                        if (_locations.TryGetValue(locationId, out area)
                            && candidateDefinition.CanUseAreaContext(area))
                            eventAreas.Add(area);
                    }

                    bool requiresVehicleStaging = candidateDefinition.RequiresVehicle
                        || candidateDefinition.VehicleStagingRequired;
                    locationFailure = "AmbientWorld is not attached by PoliceCore.";
                    LogRuntime(
                        "POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_ATTEMPT",
                        "Event=" + candidateDefinition.Id
                        + "; Context=" + candidateDefinition.LocationContext
                        + "; VehicleActivity=" + candidateDefinition.VehicleActivity
                        + "; CatalogCandidatesFailed=" + locationFailures.Count
                        + "; LiveInteriorFallback=Withheld; PendingSceneResolutionFallback="
                        + (_pendingSceneResolutionDefinitionIds.Count > 0
                            ? string.Join(",", _pendingSceneResolutionDefinitionIds)
                            : "None"));
                    if (_ambientWorld != null
                        && _ambientWorld.TryResolveDispatchScene(
                            eventAreas,
                            candidateDefinition.IncidentType,
                            player.Position,
                            requiresVehicleStaging,
                            out sceneResolution,
                            out locationFailure,
                            playerRoadAnchor,
                            candidateDefinition.InteriorLocationId,
                            candidateDefinition.RequiredPhysicalType,
                            candidateDefinition.LocationContext,
                            candidateDefinition.RequiresMappedInterior,
                            playerLiveInteriorId,
                            playerLiveInteriorIdAvailable,
                            candidateDefinition.VehicleActivity,
                            true))
                    {
                        definition = candidateDefinition;
                        LogRuntime(
                            "POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_SELECTED",
                            "Event=" + definition.Id
                            + "; LocationCategory=" + sceneResolution.LocationCategory
                            + "; ScenePosition=" + sceneResolution.SceneCenter
                            + "; VehicleActivity=" + definition.VehicleActivity);
                        break;
                    }

                    locationFailures.Add(candidateDefinition.Id
                        + "@PlayerRadiusOutdoorFallback="
                        + (locationFailure ?? "Location validation failed."));
                    LogRuntime(
                        "POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_REJECTED",
                        "Event=" + candidateDefinition.Id
                        + "; Context=" + candidateDefinition.LocationContext
                        + "; VehicleActivity=" + candidateDefinition.VehicleActivity
                        + "; Failure=" + (locationFailure ?? "Location validation failed."));
                    sceneResolution = null;
                    if (candidateDefinition.RequiresVehicle
                        && (locationFailure ?? string.Empty).IndexOf(
                            "CollisionPending", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pendingVehicleSceneFailures.Add(candidateDefinition.Id
                            + "=PENDING:" + locationFailure);
                        break;
                    }
                }
                if (definition == null
                    && pendingVehicleSceneFailures.Count == 0
                    && _pendingSceneResolutionDefinitionIds.Count > 0
                    && !playerRadiusRoadSceneAvailable)
                {
                    foreach (string pendingId in _pendingSceneResolutionDefinitionIds)
                        pendingVehicleSceneFailures.Add(pendingId
                            + "=PENDING:Stage=PlayerRadiusVehicleRoadReference; Result=Unavailable");
                }
            }

            // A mapped interior or roadside vehicle point that is still
            // streaming is not a failed event type. Hold the same candidate
            // inside Dispatch's bounded retry window before substituting an
            // unrelated exterior foot incident.
            bool hasResolvedExteriorScene = definition != null
                && sceneResolution != null
                && !sceneResolution.IsInteriorScene;
            if (hasResolvedExteriorScene
                && TryDeferPendingSceneResolution(
                    now,
                    preferredInteriorFailures,
                    definition.Id + "@" + sceneResolution.LocationId))
                return false;

            if (definition == null || sceneResolution == null)
            {
                if (TryDeferPendingSceneResolution(
                    now,
                    preferredInteriorFailures.Concat(pendingVehicleSceneFailures),
                    null))
                    return false;
                ResetPendingSceneResolution();
                _nextOfferAt = now.AddSeconds(30);
                LogDebug(
                    "POLICE_DISPATCH_AMBIENT_WORLD_LOCATION_FAILED",
                    "Attempts=" + locationFailures.Count
                    + "; MaximumAttempts=" + locationCandidates.Count
                    + "; EventFailures=" + string.Join(" | ", locationFailures));
                return false;
            }

            if (!sceneResolution.IsInteriorScene && preferredInteriorFailures.Count > 0)
            {
                LogDebug(
                    "POLICE_DISPATCH_INTERIOR_SCENE_REJECTED_BEFORE_EXTERIOR_OFFER",
                    "SelectedEvent=" + definition.Id
                    + "; SelectedLocation=" + sceneResolution.LocationId
                    + "; RejectedInteriorDefinitions=" + preferredInteriorFailures.Count
                    + "; FirstRejected=" + string.Join(" | ",
                        preferredInteriorFailures.Take(3)));
            }

            ResetPendingSceneResolution(sceneResolution);

            // Resolve profiles only after a compatible scene has passed
            // physical validation. Failed location searches must not select
            // or request actor assets that cannot be used by an occurrence.
            string assetFailureReason;
            LSPDCriminalProfileDefinition criminalProfile;
            if (!TryResolveDispatchProfile(
                definition,
                out criminalProfile,
                out assetFailureReason))
            {
                ReleaseUnusedDispatchInteriorPins("CriminalProfileSelectionFailed");
                _nextOfferAt = now.AddSeconds(30);
                LogDebug(
                    "POLICE_DISPATCH_CRIMINAL_ASSET_SELECTION_FAILED",
                    "Event=" + definition.Id + "; Reason=" + assetFailureReason);
                return false;
            }

            int suspectVehicleCount = definition.ChooseSuspectVehicleCount(_random, 2);
            if (suspectVehicleCount < 0)
            {
                ReleaseUnusedDispatchInteriorPins("SuspectVehicleCountUnavailable");
                _nextOfferAt = now.AddSeconds(30);
                LogDebug("POLICE_DISPATCH_VEHICLE_COUNT_UNAVAILABLE",
                    "Event=" + definition.Id + "; ConfiguredMaximum=2");
                return false;
            }

            LSPDDispatchEvent incident = LSPDDispatchEvent.FromDefinition(
                definition,
                sceneResolution.SceneCenter,
                criminalProfile,
                definition.ChooseSuspectCount(_random, _settings.MaximumSuspects),
                definition.ChooseVictimCount(_random),
                definition.ChooseSceneParticipantCount(_random),
                suspectVehicleCount);
            if (incident == null)
            {
                ReleaseUnusedDispatchInteriorPins("OccurrenceConstructionFailed");
                _nextOfferAt = now.AddSeconds(30);
                LogDebug(
                    "POLICE_DISPATCH_INCIDENT_NOT_CREATED",
                    "Event=" + definition.Id + "; Reason=AUTHORITATIVE_CRIMINAL_ASSET_SNAPSHOT_INVALID");
                return false;
            }
            incident.SceneResolution = sceneResolution;
            _pendingOffer = incident;
            _pendingOfferBypassesAutomaticQuietPeriod = bypassAutomaticQuietPeriod;
            _pendingOfferWaitingLogged = false;
            _pendingOfferMinimumCooldownSeconds = definition.MinimumCooldownSeconds;
            return TryAdvancePendingOffer(player, now);
        }

        private bool TryDeferPendingSceneResolution(
            DateTime now,
            IEnumerable<string> sceneFailures,
            string exteriorCandidateWithheld)
        {
            List<string> pendingFailures = (sceneFailures
                ?? Enumerable.Empty<string>())
                .Where(value => value.IndexOf(
                    "=PENDING:", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            List<string> pendingDefinitions = pendingFailures
                .Select(value => value.Substring(0, value.IndexOf('=')))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (pendingDefinitions.Count == 0)
                return false;

            string withheld = string.IsNullOrWhiteSpace(exteriorCandidateWithheld)
                ? string.Empty
                : "; ExteriorCandidateWithheld=" + exteriorCandidateWithheld;
            if (_pendingSceneResolutionStartedAt == DateTime.MinValue)
                _pendingSceneResolutionStartedAt = now;
            double pendingMilliseconds = Math.Max(
                0d, (now - _pendingSceneResolutionStartedAt).TotalMilliseconds);
            if (pendingMilliseconds >=
                TimeSpan.FromSeconds(SceneResolutionPendingMaximumSeconds).TotalMilliseconds)
            {
                LogDebug(
                    "POLICE_DISPATCH_SCENE_RESOLUTION_EXPIRED",
                    "Retries=" + _pendingSceneResolutionRetryCount
                    + "; ElapsedMilliseconds=" + pendingMilliseconds.ToString("0")
                    + "; MaximumPendingSeconds=" + SceneResolutionPendingMaximumSeconds
                    + withheld
                    + "; EventFailures=" + string.Join(" | ", pendingFailures));
                ResetPendingSceneResolution();
                _nextOfferAt = now.AddSeconds(30);
                return true;
            }

            _pendingSceneResolutionDefinitionIds.Clear();
            foreach (string pendingId in pendingDefinitions)
                _pendingSceneResolutionDefinitionIds.Add(pendingId);
            _pendingSceneResolutionRetryCount++;
            _nextOfferAt = now.AddMilliseconds(
                SceneResolutionRetryDelayMilliseconds);
            _manualRequestRetryAt = _nextOfferAt;
            LogDebug(
                "POLICE_DISPATCH_SCENE_RESOLUTION_PENDING",
                "Retry=" + _pendingSceneResolutionRetryCount
                + "; ElapsedMilliseconds=" + pendingMilliseconds.ToString("0")
                + "; MaximumPendingSeconds=" + SceneResolutionPendingMaximumSeconds
                + "; DelayMilliseconds=" + SceneResolutionRetryDelayMilliseconds
                + withheld
                + "; EventFailures=" + string.Join(" | ", pendingFailures));
            return true;
        }

        private bool TryAdvancePendingOffer(Ped player, DateTime now)
        {
            LSPDDispatchEvent incident = _pendingOffer;
            if (incident == null)
                return false;
            if (_incident != null)
            {
                CancelPendingOffer("Another Dispatch assignment became active.");
                return false;
            }

            string preparationFailure;
            SceneOfferPreflightStatus status = TryPrepareSceneBeforeOffer(
                player,
                incident,
                out preparationFailure);
            if (status == SceneOfferPreflightStatus.Pending)
            {
                if (!_pendingOfferWaitingLogged)
                {
                    _pendingOfferWaitingLogged = true;
                    LogDebug(
                        "POLICE_DISPATCH_SCENE_PREFLIGHT_WAITING",
                        "Event=" + incident.Id + "; Reason=" + preparationFailure);
                }
                DateTime sceneRetryAt = incident.ScenePreparationNextRetryAt;
                _nextOfferAt = sceneRetryAt > now
                    ? sceneRetryAt
                    : now.AddMilliseconds(250);
                return false;
            }

            bool bypassAutomaticQuietPeriod = _pendingOfferBypassesAutomaticQuietPeriod;
            int minimumCooldownSeconds = _pendingOfferMinimumCooldownSeconds;
            _pendingOffer = null;
            _pendingOfferBypassesAutomaticQuietPeriod = false;
            _pendingOfferWaitingLogged = false;
            _pendingOfferMinimumCooldownSeconds = 0;

            if (status == SceneOfferPreflightStatus.Failed)
            {
                CleanupPreparedOccurrence(incident, "ScenePreflightFailed");
                _nextOfferAt = now.AddSeconds(30);
                if (bypassAutomaticQuietPeriod)
                    _manualRequestRetryAt = now.AddSeconds(5);
                LogDebug(
                    "POLICE_DISPATCH_SCENE_PREFLIGHT_FAILED",
                    "Event=" + incident.Id + "; Reason=" + preparationFailure);
                return false;
            }

            if (Receive(incident, bypassAutomaticQuietPeriod))
            {
                _cooldownUntil = now.AddSeconds(Math.Max(
                    minimumCooldownSeconds,
                    _settings.MinimumQuietPatrolSeconds));
                _nextOfferAt = now.AddSeconds(NextQuietPatrolSeconds());
                if (bypassAutomaticQuietPeriod)
                    LogRuntime("POLICE_DISPATCH_MANUAL_REQUESTED", "A player-requested Dispatch offer was created.");
                return true;
            }

            CleanupPreparedOccurrence(incident, "OfferReceiveRejected");
            _nextOfferAt = now.AddSeconds(30);
            return false;
        }

        private void CancelPendingOffer(string reason)
        {
            LSPDDispatchEvent pending = _pendingOffer;
            if (pending == null)
                return;

            _pendingOffer = null;
            _pendingOfferBypassesAutomaticQuietPeriod = false;
            _pendingOfferWaitingLogged = false;
            _pendingOfferMinimumCooldownSeconds = 0;
            CleanupPreparedOccurrence(pending, "PendingOfferCancelled");
            LogDebug(
                "POLICE_DISPATCH_PENDING_SCENE_CANCELLED",
                "Event=" + pending.Id + "; Reason=" + (reason ?? string.Empty));
        }

        private SceneOfferPreflightStatus TryPrepareSceneBeforeOffer(
            Ped player,
            LSPDDispatchEvent incident,
            out string reason)
        {
            reason = string.Empty;
            if (player == null || !player.Exists())
            {
                reason = "The player character is unavailable for scene validation.";
                return SceneOfferPreflightStatus.Failed;
            }
            if (incident == null || _incident != null)
            {
                reason = "A Dispatch scene is already active or the candidate is missing.";
                return SceneOfferPreflightStatus.Failed;
            }
            if (DateTime.UtcNow < incident.ScenePreparationNextRetryAt)
            {
                reason = "Waiting for the bounded scene actor setup retry interval.";
                return SceneOfferPreflightStatus.Pending;
            }

            LSPDDispatchEvent previousIncident = _incident;
            _incident = incident;
            try
            {
                if (!EnsureScene(player, false, false))
                {
                    string stage = string.IsNullOrWhiteSpace(
                        incident.ScenePreparationFailureStage)
                        ? "UnclassifiedPreparation"
                        : incident.ScenePreparationFailureStage;
                    string stageReason = string.IsNullOrWhiteSpace(
                        incident.ScenePreparationFailureReason)
                        ? "The owning scene-preparation method returned false without a stage reason."
                        : incident.ScenePreparationFailureReason;
                    bool modelStage = stage.IndexOf("Model",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    bool modelStillStreaming = modelStage
                        && HasUnloadedRequiredSceneModels(incident);
                    bool boundedInteriorPending = IsInteriorScenePreparationPending(
                        incident, stageReason);
                    bool boundedVehicleGroundPending = string.Equals(
                            stage, "SuspectVehiclePlacement",
                            StringComparison.OrdinalIgnoreCase)
                        && stageReason.IndexOf(
                            "Result=CollisionPending", StringComparison.OrdinalIgnoreCase) >= 0;
                    boundedVehicleGroundPending = boundedVehicleGroundPending
                        || (string.Equals(stage, "SceneRevalidation",
                                StringComparison.OrdinalIgnoreCase)
                            && incident.SceneResolution != null
                            && string.Equals(
                                incident.SceneResolution.LocationCategory,
                                "PlayerRadiusRoadNode",
                                StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(
                                incident.SceneResolution.VehicleStagingMode,
                                "moving_road",
                                StringComparison.OrdinalIgnoreCase)
                            && stageReason.StartsWith(
                                "PENDING:Stage=PlayerRadiusParkedRevalidationGround;",
                                StringComparison.OrdinalIgnoreCase)
                            && stageReason.IndexOf(
                                "CollisionRequest=REQUEST_COLLISION_AT_COORD",
                                StringComparison.OrdinalIgnoreCase) >= 0);

                    if (modelStillStreaming
                        && DateTime.UtcNow < incident.ScenePreparationDeadline)
                    {
                        incident.ScenePreparationNextRetryAt = DateTime.UtcNow.AddMilliseconds(250);
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; Result=ModelStreamingPending";
                        return SceneOfferPreflightStatus.Pending;
                    }
                    if (boundedVehicleGroundPending
                        && DateTime.UtcNow < incident.ScenePreparationDeadline
                        && incident.ScenePreparationDeadline != DateTime.MinValue)
                    {
                        DateTime nextRetry = DateTime.UtcNow.AddMilliseconds(
                            SceneActorSetupRetryDelayMilliseconds);
                        incident.ScenePreparationNextRetryAt = nextRetry < incident.ScenePreparationDeadline
                            ? nextRetry : incident.ScenePreparationDeadline;
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; MaximumPendingSeconds=" + ScenePreparationTimeoutSeconds
                            + "; Result=WaitingForVehicleRoadCollision";
                        return SceneOfferPreflightStatus.Pending;
                    }
                    if (boundedVehicleGroundPending)
                    {
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; MaximumPendingSeconds=" + ScenePreparationTimeoutSeconds
                            + "; Result=VehicleRoadCollisionDeadlineExpired";
                        return SceneOfferPreflightStatus.Failed;
                    }
                    if (boundedInteriorPending
                        && DateTime.UtcNow < incident.ScenePreparationDeadline
                        && incident.ScenePreparationDeadline != DateTime.MinValue)
                    {
                        DateTime nextRetry = DateTime.UtcNow.AddMilliseconds(
                            SceneActorSetupRetryDelayMilliseconds);
                        incident.ScenePreparationNextRetryAt = nextRetry < incident.ScenePreparationDeadline
                            ? nextRetry : incident.ScenePreparationDeadline;
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; EnvironmentalRetry=true"
                            + "; MaximumPendingSeconds=" + ScenePreparationTimeoutSeconds
                            + "; Result=WaitingForInteriorReadinessOrCollision";
                        return SceneOfferPreflightStatus.Pending;
                    }
                    if (boundedInteriorPending)
                    {
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; MaximumPendingSeconds=" + ScenePreparationTimeoutSeconds
                            + "; Result=InteriorPreparationDeadlineExpired";
                        return SceneOfferPreflightStatus.Failed;
                    }
                    if (string.Equals(stage, "ParticipantPlacement",
                            StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(stage, "SceneRevalidation",
                                StringComparison.OrdinalIgnoreCase)
                            && !boundedInteriorPending))
                    {
                        reason = "Stage=" + stage + "; " + stageReason
                            + (modelStage && !modelStillStreaming
                                ? "; Result=ModelAvailableOrInvalid; ThisFailureIsNotModelStreaming"
                                : string.Empty);
                        return SceneOfferPreflightStatus.Failed;
                    }
                    if (modelStage && !modelStillStreaming)
                    {
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; Result=ModelUnavailableOrInvalidBeforePlacement";
                        return SceneOfferPreflightStatus.Failed;
                    }
                    if (DateTime.UtcNow < incident.ScenePreparationDeadline
                        && incident.ScenePreparationActorRetryCount < SceneActorSetupRetryLimit)
                    {
                        incident.ScenePreparationActorRetryCount++;
                        incident.ScenePreparationNextRetryAt = DateTime.UtcNow.AddMilliseconds(
                            SceneActorSetupRetryDelayMilliseconds);
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; AmbientWorld actor setup did not complete on attempt "
                            + incident.ScenePreparationActorRetryCount
                            + "; retrying after a bounded delay.";
                        return SceneOfferPreflightStatus.Pending;
                    }
                    reason = "Stage=" + stage + "; " + stageReason
                        + "; AmbientWorld could not finish scene preparation after "
                        + incident.ScenePreparationActorRetryCount
                        + " bounded retry attempt(s).";
                    return SceneOfferPreflightStatus.Failed;
                }

                int requiredSuspects = Math.Max(1, incident.SuspectCount);
                List<Ped> liveSuspects = TrackedSuspects(incident)
                    .Where(suspect => !suspect.IsDead && suspect.Health > 0)
                    .ToList();
                if (liveSuspects.Count != requiredSuspects)
                {
                    reason = "RequiredSuspects=" + requiredSuspects
                        + "; ReadySuspects=" + liveSuspects.Count + ".";
                    return SceneOfferPreflightStatus.Failed;
                }
                if (incident.RequiresVehicle
                    && CountLiveSuspectVehicles(incident)
                        < Math.Max(1, incident.SuspectVehicleCount))
                {
                    reason = "Required suspect vehicles were not physically staged and prepared.";
                    return SceneOfferPreflightStatus.Failed;
                }
                if (incident.VictimCount > 0
                    && (incident.Victim == null || !incident.Victim.Exists()
                        || !LSPDDispatchEventDefinition.IsVictimStateReady(
                            incident.VictimStartsDead,
                            incident.Victim.IsDead,
                            incident.Victim.Health)
                        || _ambientWorld == null
                        || !_ambientWorld.IsDispatchVictimModelInPool(
                            incident.VictimPoolId,
                            incident.Victim.Model.Hash)))
                {
                    reason = "The required Dispatch victim is missing, invalid, or outside its authored model pool.";
                    return SceneOfferPreflightStatus.Failed;
                }
                if (!AreAdditionalDispatchParticipantsReady(incident))
                {
                    reason = "One or more authored shop participants are missing, outside their NPC pool, or outside the exact selected live interior.";
                    return SceneOfferPreflightStatus.Failed;
                }

                incident.ScenePreparationCompleted = true;
                LogRuntime(
                    "POLICE_DISPATCH_SCENE_PREFLIGHTED",
                    "Event=" + incident.Id
                    + "; Suspects=" + liveSuspects.Count
                    + "; SceneParticipants=" + incident.AdditionalParticipants.Count(value =>
                        value != null && value.Exists())
                    + "; Vehicle=" + (incident.SuspectVehicle == null ? 0 : incident.SuspectVehicle.Handle)
                    + "; Location=" + (incident.SceneResolution == null
                        ? string.Empty : incident.SceneResolution.LocationId)
                    + "; ReadyBeforeOffer=true");
                return SceneOfferPreflightStatus.Ready;
            }
            finally
            {
                _incident = previousIncident;
            }
        }

        private static bool HasUnloadedRequiredSceneModels(LSPDDispatchEvent incident)
        {
            if (incident == null || !incident.ScenePreparationRequested)
                return false;
            try
            {
                Model suspectModel = new Model(incident.SuspectModel);
                if (IsUsableModel(suspectModel, false) && !suspectModel.IsLoaded)
                    return true;

                if (incident.RequiresVehicle)
                {
                    Model vehicleModel = new Model(incident.VehicleModel);
                    if (IsUsableModel(vehicleModel, true) && !vehicleModel.IsLoaded)
                        return true;
                }

                foreach (string modelName in RequiredAdditionalSuspectModels(incident))
                {
                    Model model = new Model(modelName);
                    if (IsUsableModel(model, false) && !model.IsLoaded)
                        return true;
                }

                if (incident.VictimCount > 0
                    && !string.IsNullOrWhiteSpace(incident.VictimModelName))
                {
                    Model victimModel = new Model(incident.VictimModelName);
                    if (IsUsableModel(victimModel, false) && !victimModel.IsLoaded)
                        return true;
                }

                foreach (string modelName in incident.SceneParticipantModelNames
                    ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(modelName))
                        continue;
                    Model participantModel = new Model(modelName);
                    if (IsUsableModel(participantModel, false)
                        && !participantModel.IsLoaded)
                        return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        /// <summary>
        /// A completed gang threat or civilian contact is contextual gameplay,
        /// not another Dispatch producer. Give its local aftermath a short
        /// quiet period before this owner can make a new universal offer.
        /// </summary>
        internal void DeferOffersForContextActivity(string context)
        {
            if (_incident != null || !_patrolSessionStarted)
                return;
            CancelPendingOffer("A contextual Police activity took priority.");
            DateTime now = DateTime.UtcNow;
            DateTime deferredUntil = now.AddSeconds(_settings.ContextClearQuietSeconds);
            if (_nextOfferAt < deferredUntil)
                _nextOfferAt = deferredUntil;
            if (_cooldownUntil < deferredUntil)
                _cooldownUntil = deferredUntil;
            LogRuntime(
                "POLICE_DISPATCH_CONTEXT_QUIET",
                "Source=" + (string.IsNullOrWhiteSpace(context) ? "contextual activity" : context)
                + "; Seconds=" + _settings.ContextClearQuietSeconds);
        }

        private void ProcessControllerInput(bool suppressEnterInput)
        {
            if (_incident == null)
                return;

            // Dispatch decisions use the authored Y/N keyboard pair. GTA maps
            // FrontendCancel to both controller Circle and keyboard Escape /
            // Backspace, so only read the frontend controls while the gamepad
            // is the active input method. This lets Circle accept and X/Cross
            // decline without closing a Police menu also declining the call.
            if (HasOffer)
            {
                bool acceptKeyPressed = WasKeyPressed(
                    _controls.AcceptKey,
                    ref _acceptKeyDown);
                bool rejectKeyPressed = WasKeyPressed(
                    _controls.RejectKey,
                    ref _rejectKeyDown);
                bool controllerInput = IsControllerInputActive();
                bool controllerAccept = controllerInput
                    && Game.IsControlJustPressed(GTA.Control.FrontendCancel);
                bool controllerReject = controllerInput
                    && Game.IsControlJustPressed(GTA.Control.FrontendAccept);

                if (controllerAccept || acceptKeyPressed)
                    Accept();
                else if (controllerReject || rejectKeyPressed)
                    Decline();
                return;
            }

            if ((!suppressEnterInput && Game.IsControlJustPressed(GTA.Control.Enter))
                || WasKeyPressed(_controls.InvestigateKey, ref _investigateKeyDown))
                Notify(Investigate(Game.Player.Character));
            else
            {
                // Reload is also the normal keyboard R action. Core consumes
                // the configured transport key before Dispatch is processed,
                // so do not let the same physical R press fall through as a
                // second secure command and undo Dispatch -> Convoy custody
                // ownership. Controller Reload remains available when no
                // configured transport key is physically held.
                bool configuredSecurePressed =
                    WasKeyPressed(_controls.SecureKey, ref _secureKeyDown);
                bool controllerReloadPressed =
                    Game.IsControlJustPressed(GTA.Control.Reload)
                    && !Game.IsKeyPressed(_controls.TransportKey);
                // Convoy owns the physical prisoner interaction after the
                // custody handoff. Let its E-controlled escort/loading path
                // consume that key instead of showing the old Dispatch
                // "Convoy already owns" response on every player interaction.
                if ((controllerReloadPressed || configuredSecurePressed)
                    && !_incident.HasConvoyCustodyHandoff)
                    Notify(SecureSuspect(Game.Player.Character));
            }
        }

        private static bool IsKeyDown(Keys key)
        {
            return Game.IsKeyPressed(key);
        }

        private static bool IsControllerInputActive()
        {
            try
            {
                return !Function.Call<bool>(Hash.IS_USING_KEYBOARD_AND_MOUSE, 2);
            }
            catch
            {
                // If input-method detection is unavailable, preserve the
                // unambiguous Y/N keyboard path and do not treat menu-back as
                // a Dispatch decision.
                return false;
            }
        }

        private static bool WasKeyPressed(Keys key, ref bool wasDown)
        {
            bool isDown = IsKeyDown(key);
            bool pressed = isDown && !wasDown;
            wasDown = isDown;
            return pressed;
        }

        private void SynchronizeKeyboardInputStates()
        {
            _acceptKeyDown = IsKeyDown(_controls.AcceptKey);
            _rejectKeyDown = IsKeyDown(_controls.RejectKey);
            _investigateKeyDown = IsKeyDown(_controls.InvestigateKey);
            _secureKeyDown = IsKeyDown(_controls.SecureKey);
        }

        /// <summary>
        /// Accepts a callout supplied by another Police producer. The local
        /// patrol producer uses this same boundary, so UI and external
        /// integrations share one ownership and state-transition path.
        /// </summary>
        internal bool Receive(LSPDDispatchEvent incident, bool bypassAutomaticQuietPeriod = false)
        {
            if (!_settings.Enabled || incident == null || _incident != null
                || (!bypassAutomaticQuietPeriod && DateTime.UtcNow < _cooldownUntil))
                return false;

            CancelPendingOffer("A separate Dispatch offer took priority.");

            incident.State = LSPDDispatchState.Offered;
            incident.StateChangedAt = DateTime.UtcNow;
            _incident = incident;
            _definitionOfferedAt[DefinitionId(incident)] = DateTime.UtcNow;
            if (CanPlayOfferAudio())
                ReportAudioStage("lsimmersivelife.police.dispatch.received", incident, "received");
            Notify(FormatOfferNotification(incident));
            LogRuntime(
                "POLICE_DISPATCH_OFFERED",
                incident.Title + " | Type=" + incident.IncidentType
                + " | Context=" + incident.LocationContext
                + " | Branches=" + string.Join(",", incident.BranchIds ?? new List<string>())
                + " | Origin=" + incident.Origin);
            return true;
        }

        private static string FormatOfferNotification(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return "~b~911 DISPATCH~s~\nNo call details are currently available.";

            string title = string.IsNullOrWhiteSpace(incident.Title)
                ? "Emergency call"
                : incident.Title;
            string briefing = string.IsNullOrWhiteSpace(incident.Briefing)
                ? "Details unavailable."
                : incident.Briefing;

            return "~b~911 DISPATCH~s~\n"
                + "~y~CALL:~s~ " + title + "\n"
                + "~c~REPORT:~s~ " + briefing + "\n"
                + "~c~RESPOND?~s~  ~g~Y / Circle ACCEPT~s~  ~r~N / X DECLINE~s~";
        }

        internal LSPDAudioResult Acknowledge()
        {
            if (!HasOffer)
                return LSPDAudioResult.Inactive;
            Notify("~b~POLICE DISPATCH~s~\nInformation received: " + _incident.Briefing);
            LogRuntime("POLICE_DISPATCH_ACKNOWLEDGED", _incident.Title);
            return ReportAudioStage(
                "lsimmersivelife.police.dispatch.acknowledged",
                _incident,
                "acknowledged");
        }

        internal LSPDAudioResult Accept()
        {
            if (!HasOffer)
                return LSPDAudioResult.Inactive;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return LSPDAudioResult.Unavailable;

            if (!_incident.ScenePreparationCompleted && !BeginScenePreparation(_incident))
            {
                Fail("The dispatch scene has an unavailable model definition.");
                return LSPDAudioResult.Unavailable;
            }

            SetState(LSPDDispatchState.Accepted);
            SetState(LSPDDispatchState.EnRoute);
            _incident.NextEnRouteStatusAt = DateTime.UtcNow.AddSeconds(30);
            CaptureAndSetWaypoint(_incident.Origin);
            SetSceneBlip(_incident.Origin, _incident.Title);
            if (_incident.Suspect != null && _incident.Suspect.Exists())
                SetSuspectBlip(_incident.Suspect);
            LSPDAudioResult accepted = ReportAudioStage(
                "lsimmersivelife.police.player.accept_dispatch",
                _incident,
                "accepted");
            ReportAudioStage(
                "lsimmersivelife.police.dispatch.en_route",
                _incident,
                "en-route-acknowledged",
                true);
            Notify("~b~POLICE DISPATCH~s~\nAssignment accepted. Proceed to the marked scene.");
            LogRuntime("POLICE_DISPATCH_ACCEPTED", _incident.Title + " | Scene preparation requested.");
            return accepted;
        }

        internal LSPDAudioResult Decline()
        {
            if (!HasOffer)
                return LSPDAudioResult.Inactive;
            LSPDDispatchEvent incident = _incident;
            LSPDAudioResult result = Terminate(
                incident,
                LSPDDispatchState.Cancelled,
                "Declined by officer.",
                "lsimmersivelife.police.player.decline_dispatch",
                "declined",
                true);
            Notify("~b~POLICE DISPATCH~s~\nCall declined. You remain available for patrol.");
            return result;
        }

        internal string Investigate(Ped player)
        {
            if (_incident == null)
                return "No active dispatch is waiting for investigation.";
            if (_incident.State == LSPDDispatchState.Offered)
                return "Accept the dispatch before investigating the scene.";
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
                return "Prisoner transport currently owns this dispatch custody.";
            if (player == null || !player.Exists())
                return "Player character unavailable.";

            if (_incident.Suspect == null || !_incident.Suspect.Exists())
            {
                if (!EnsureScene(player))
                {
                    if (_incident == null)
                        return "The dispatch scene could not be prepared and the assignment has ended.";
                    if (_incident.ScenePreparationRequested
                        && !_incident.ScenePreparationCompleted)
                        return "Dispatch is preparing the scene. Keep the assignment active for a moment.";
                    return "The active dispatch scene is temporarily unavailable.";
                }
            }

            bool pursuit = _incident.State == LSPDDispatchState.SuspectFleeing
                || _incident.State == LSPDDispatchState.SuspectResisting;
            Vector3 target = pursuit && _incident.Suspect != null && _incident.Suspect.Exists()
                ? _incident.Suspect.Position : _incident.Origin;
            if (player.Position.DistanceTo(target) > (pursuit ? ArrestRadius + 4f : SceneArrivalRadius))
                return pursuit
                    ? "Follow the active suspect marker before investigating."
                    : "Proceed to the marked scene before investigating.";

            if (IsMappedInteriorDispatchBehavior(_incident.SceneBehavior))
            {
                if (player.IsInVehicle())
                    return "Park your Police Utility outside the building, get out, and enter before investigating.";
                Vehicle parkedUtility = _incident.ParkedPlayerUtility;
                if (parkedUtility == null || !parkedUtility.Exists()
                    || _incident.SceneResolution == null
                    || !_incident.SceneResolution.HasVehicleStagingPosition
                    || Math.Abs(parkedUtility.Speed) > 0.5f
                    || parkedUtility.Position.DistanceTo(
                        _incident.SceneResolution.VehicleStagingPosition) > 35f)
                    return "Park your Police Utility on the ground near the entrance before entering.";

                int playerInteriorId;
                if (_ambientWorld == null
                    || !_ambientWorld.TryGetActorInteriorId(player, out playerInteriorId)
                    || playerInteriorId != _incident.SceneResolution.LiveInteriorId)
                    return "Enter through the mapped doorway before investigating.";
            }

            // A dead suspect is only a completed scene after the officer has
            // actually reached the scene or the confirmed pursuit position.
            // The old ordering closed a callout from anywhere in the city as
            // soon as the suspect died, which made Dispatch feel like a log
            // simulator and removed the visible aftermath.
            List<Ped> liveAtScene = LiveSuspects(_incident).ToList();
            if (liveAtScene.Count == 0)
            {
                if (HasPendingVictimInvestigation
                    && !TryRecordMurderVictimInvestigation(_incident, player))
                    return "The victim is still waiting for an on-scene investigation. Move close to the body and investigate before closing Dispatch.";
                if (HasUnfinishedArrestedCustody(_incident))
                    return FailUnfinishedArrestedCustody(_incident);
                string completion = CompletionMessage(_incident, true);
                Complete(completion);
                return completion;
            }

            if (_incident.State == LSPDDispatchState.EnRoute)
            {
                SetState(LSPDDispatchState.OnScene);
                ReportAudioStage("lsimmersivelife.police.dispatch.arrived", _incident, "arrived");
            }

            StartSceneBehavior(player);
            if (_incident == null || !_incident.OwnedByDispatch)
                return "The dispatch scene is no longer available for investigation.";

            if (_incident.State == LSPDDispatchState.OnScene)
                SetState(LSPDDispatchState.Investigating);

            if (!_incident.SceneArrivalReported)
            {
                _incident.SceneArrivalReported = true;
                ReportAudioStage(
                    "lsimmersivelife.police.scene.on_scene",
                    _incident,
                    "arrived",
                    true);
            }

            if (IsCompliant(_incident, _incident.Suspect)
                && !IsWoundedSuspect(_incident.Suspect)
                && !_incident.Suspect.IsInCombatAgainst(player)
                && !_incident.Suspect.IsShooting)
            {
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectCompliant);
                Notify("~g~POLICE COMPLIANCE~s~\nSuspect has stopped and is ready to secure.");
                return "Suspect is compliant. Secure the suspect to begin custody.";
            }

            if (_incident.Suspect.IsInCombatAgainst(player) || _incident.Suspect.IsShooting)
            {
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectResisting);
                Notify("~r~POLICE DISPATCH~s~\nSuspect is resisting. Use Secure & Comply when safe.");
                return "Suspect is resisting. Continue the response and secure the suspect when safe.";
            }

            if (_incident.Suspect.IsFleeing)
            {
                MarkSuspectFleeing(_incident, _incident.Suspect);
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectFleeing);
                return "Suspect is fleeing. Follow the active suspect marker.";
            }

            if (!_incident.InvestigationReported)
            {
                _incident.InvestigationReported = true;
                ReportAudioStage(
                    "lsimmersivelife.police.investigation.started",
                    _incident,
                    "investigation",
                    true);
            }

            if (HasPendingVictimInvestigation)
            {
                List<Ped> unsecured = liveAtScene
                    .Where(suspect => !IsArrested(_incident, suspect))
                    .ToList();
                if (unsecured.Count > 0)
                    return "Secure the remaining suspect(s), then use Investigate again to examine the murder victim.";
                if (!TryRecordMurderVictimInvestigation(_incident, player))
                    return "Move closer to the murder victim before investigating the body.";

                SetState(LSPDDispatchState.Arrested);
                Notify("~b~DISPATCH INVESTIGATION~s~\nVictim examination recorded. Request prisoner transport when ready.");
                return "Victim investigation recorded. Request prisoner transport when ready.";
            }

            Notify("~b~POLICE DISPATCH~s~\nScene reached. Suspect is present. Use Secure & Comply.");
            return "Scene reached. Suspect is present and ready for Police interaction.";
        }

        private bool TryRecordMurderVictimInvestigation(
            LSPDDispatchEvent incident,
            Ped player)
        {
            if (incident == null || !incident.VictimStartsDead
                || incident.VictimInvestigated)
                return true;
            Ped victim = incident.Victim;
            bool mappedInteriorMurder = string.Equals(
                incident.SceneBehavior, "murder_victim_interior",
                StringComparison.OrdinalIgnoreCase);
            int playerInteriorId = 0;
            bool playerInsideIncidentInterior = !mappedInteriorMurder
                || (player != null && player.Exists()
                    && _ambientWorld != null
                    && incident.SceneResolution != null
                    && incident.SceneResolution.IsInteriorScene
                    && (incident.SceneResolution.IsMappedInteriorDispatchScene
                        || incident.SceneResolution.IsInsideOnlyInteriorScene)
                    && _ambientWorld.TryGetDispatchPlayerInteriorId(player, out playerInteriorId)
                    && playerInteriorId == incident.SceneResolution.LiveInteriorId);
            if (victim == null || !victim.Exists()
                || !LSPDDispatchEventDefinition.IsVictimStateReady(
                    true, victim.IsDead, victim.Health)
                || player == null || !player.Exists()
                || player.Position.DistanceTo(victim.Position) > SceneArrivalRadius
                || !playerInsideIncidentInterior)
            {
                LogDebug("POLICE_DISPATCH_MURDER_VICTIM_INVESTIGATION_UNAVAILABLE",
                    "Incident=" + incident.Id
                    + "; Victim=" + (victim == null ? 0 : victim.Handle)
                    + "; Player=" + (player == null ? 0 : player.Handle)
                    + "; Reason=VictimOrOfficerNotAtSceneOrMappedInterior");
                return false;
            }

            incident.VictimInvestigated = true;
            LogRuntime("POLICE_DISPATCH_MURDER_VICTIM_INVESTIGATED",
                "Incident=" + incident.Id
                + "; Victim=" + victim.Handle
                + "; Player=" + player.Handle
                + "; VictimStartsDead=true; SceneContext="
                + (mappedInteriorMurder ? "mapped-interior" : "alley")
                + "; InteriorId=" + (mappedInteriorMurder
                    ? incident.SceneResolution.LiveInteriorId.ToString(CultureInfo.InvariantCulture)
                    : "none")
                + "; TerminalOutcome=false");
            return true;
        }

        private static string CompletionMessage(LSPDDispatchEvent incident, bool suspectNeutralized)
        {
            if (suspectNeutralized && incident != null && incident.Suspects != null)
            {
                int deceasedSuspects = incident.Suspects.Count(ped => ped != null
                    && ped.Exists() && (ped.IsDead || ped.Health <= 0));
                if (deceasedSuspects > 0)
                    return deceasedSuspects == 1
                        ? "A suspect died. The Dispatch assignment has ended."
                        : deceasedSuspects + " suspects died. The Dispatch assignment has ended.";
            }

            string prefix = suspectNeutralized ? "Suspect neutralized. " : string.Empty;
            switch ((incident == null ? string.Empty : incident.IncidentType ?? string.Empty).ToLowerInvariant())
            {
                case "carjacking":
                    return prefix + "Carjacking scene secured; victim and vehicle details recorded.";
                case "vehicle_theft":
                    return prefix + "Stolen vehicle scene secured and recovery recorded.";
                case "vehicle_pursuit":
                case "reckless_driver":
                    return prefix + "Vehicle pursuit scene secured and traffic hazard contained.";
                case "bank_robbery":
                    return prefix + "Bank robbery scene secured and evidence handoff recorded.";
                case "store_robbery":
                    return prefix + "Store robbery scene secured and witness area cleared.";
                case "kidnapping":
                    return prefix + "Kidnapping scene secured and victim recovery recorded.";
                case "shots_fired":
                    return prefix + "Shots-fired scene secured and weapon threat contained.";
                case "violent_disturbance":
                    return prefix + "Violent disturbance contained and scene secured.";
                default:
                    return prefix + "Scene investigation completed.";
            }
        }

        internal string SecureSuspect(Ped player)
        {
            if (_incident == null)
                return "There is no living suspect owned by the active dispatch.";
            if (player == null || !player.Exists())
                return "Player character unavailable.";
            if (_incident.State == LSPDDispatchState.Offered || _incident.State == LSPDDispatchState.EnRoute)
                return "Arrive at the dispatch scene first.";
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
                return "Convoy already owns this secured prisoner. Continue the active transport operation.";
            if (_incident.PlayerHandcuffInProgress)
                return "Physical handcuffing is already in progress. Keep close to the suspect.";

            List<Ped> living = LiveSuspects(_incident).ToList();
            if (living.Count == 0)
            {
                if (HasPendingVictimInvestigation)
                    return "Suspects are resolved. Investigate the murder victim at the scene before closing Dispatch.";
                if (HasUnfinishedArrestedCustody(_incident))
                    return FailUnfinishedArrestedCustody(_incident);
                string completed = CompletionMessage(_incident, true);
                Complete(completed);
                return completed;
            }

            Ped target = living
                .Where(ped => !IsArrested(_incident, ped)
                    && !_incident.BackupArrestInProgressSuspectHandles.Contains(
                        ped.Handle))
                .OrderBy(ped => ped.Position.DistanceTo(player.Position))
                .FirstOrDefault();
            if (target == null)
            {
                if (_incident.BackupArrestInProgressSuspectHandles.Count > 0)
                    return "Backup is completing a physical arrest. Secure the next suspect when the officer finishes.";
                SetState(LSPDDispatchState.Arrested);
                return "All living suspects are secured. Request prisoner transport when ready.";
            }
            float targetDistance = player.Position.DistanceTo(target.Position);
            if (targetDistance > ArrestRadius + 1f)
            {
                string response;
                bool fleeing = _incident.SuspectResponses.TryGetValue(
                    target.Handle, out response)
                    && string.Equals(response, "flee",
                        StringComparison.OrdinalIgnoreCase);
                if (fleeing && !target.IsInVehicle()
                    && targetDistance <= ArrestRadius + 4f
                    && !IsDownedOrInjured(target))
                    return OrderSuspectToComply(target, player, false);
                if (fleeing && target.IsInVehicle())
                    return "The fleeing driver is still in the vehicle. Stop the vehicle before issuing the surrender command.";
                return "Move closer to the next unsecured suspect before issuing the Police command.";
            }

            if (IsDispatchMeleeRecoveryPending(target, DateTime.UtcNow))
                return "Wait for the suspect's physical reaction to finish, then secure the same suspect.";

            bool downedAlive = IsDownedOrInjured(target);
            if (!IsCompliant(_incident, target) && !downedAlive)
                return OrderSuspectToComply(target, player, false);

            if (!downedAlive
                && DateTime.UtcNow < _incident.SurrenderRequestedAt.AddSeconds(SurrenderSettleSeconds))
                return "Surrender command issued. Wait for visible compliance, then secure again.";

            return BeginPlayerHandcuff(player, target);
        }

        internal bool RequestBackupSuspectArrest(Ped officer, Ped suspect)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || !incident.SceneArrivalReported
                || officer == null || !officer.Exists() || officer.IsDead
                || suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.Health <= 0 || suspect.IsInVehicle()
                || incident.Suspects == null
                || !incident.Suspects.Any(value => value != null && value.Exists()
                    && value.Handle == suspect.Handle)
                || IsArrested(incident, suspect)
                || (incident.PlayerHandcuffInProgress
                    && incident.PlayerHandcuffTargetHandle == suspect.Handle))
                return false;

            if (_backupArrestOperations.ContainsKey(suspect.Handle))
                return true;
            if (incident.BackupArrestInProgressOfficerHandles.Contains(officer.Handle))
                return false;
            if (incident.BackupArrestAttemptedSuspectHandles.Contains(suspect.Handle))
                return false;
            if (!IsCompliant(incident, suspect) && !IsWoundedSuspect(suspect))
                return false;
            if (officer.Position.DistanceTo(suspect.Position)
                > SynchronizedArrestMaximumDistance + 0.15f)
                return false;

            DateTime now = DateTime.UtcNow;
            var operation = new BackupArrestOperation
            {
                Officer = officer,
                Suspect = suspect,
                TaskDeadline = now.AddSeconds(BackupArrestTaskTimeoutSeconds)
            };
            try
            {
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    suspect, true, false, false, true, true, false, false, false);
                officer.BlockPermanentEvents = true;

                float animationDuration;
                bool grounded = IsGroundedForHandcuff(suspect);
                operation.PairedAnimationStarted = grounded
                    ? LSImmersiveDictionaryAnimation.TryPlaySynchronizedGroundArrest(
                        officer, suspect, out operation.SceneId, out animationDuration)
                    : LSImmersiveDictionaryAnimation.TryPlaySynchronizedArrest(
                        officer, suspect, out operation.SceneId, out animationDuration);
                if (operation.PairedAnimationStarted)
                    operation.AnimationCompleteAt = now.AddSeconds(
                        Math.Max(2f, animationDuration + 0.35f));
                else
                {
                    Function.Call(Hash.TASK_ARREST_PED, officer, suspect);
                    operation.NativeTaskFallbackStarted = true;
                }

                _backupArrestOperations[suspect.Handle] = operation;
                incident.BackupArrestInProgressSuspectHandles.Add(suspect.Handle);
                incident.BackupArrestInProgressOfficerHandles.Add(officer.Handle);
                incident.BackupArrestAttemptedSuspectHandles.Add(suspect.Handle);
                LogRuntime("POLICE_BACKUP_DISPATCH_ARREST_STARTED",
                    "Incident=" + incident.Id
                    + "; Officer=" + officer.Handle
                    + "; Suspect=" + suspect.Handle
                    + "; Method=" + (operation.PairedAnimationStarted
                        ? (grounded ? "SynchronizedGroundArrest" : "SynchronizedArrest")
                        : "TaskArrestPedFallback")
                    + "; Scene=" + operation.SceneId
                    + "; PhysicalCustodyPending=true");
                return true;
            }
            catch (Exception ex)
            {
                RestoreBackupArrestTarget(suspect, false);
                LogException("POLICE_BACKUP_DISPATCH_ARREST_START_FAILED", ex);
                return false;
            }
        }

        internal string ReleaseCurrentSuspect()
        {
            if (!CanReleaseCurrentSuspect)
                return "The active Dispatch suspect is not ready for release. Secure the suspect on foot first.";

            Ped suspect = CurrentSuspect;
            int handle = suspect.Handle;
            try
            {
                suspect.Task.ClearAll();
                Function.Call(Hash.SET_PED_CONFIG_FLAG,
                    suspect, HandcuffedPedConfigFlag, false);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, suspect, false);
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    suspect, false, false, false, false, false, false, false, false);
                suspect.CanSwitchWeapons = true;
                suspect.BlockPermanentEvents = false;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, suspect, false);
                Function.Call(Hash.SET_PED_KEEP_TASK, suspect, false);
                Function.Call(Hash.TASK_WANDER_STANDARD, suspect, 10.0f, 10);
                suspect.IsPersistent = false;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SUSPECT_RELEASE_FAILED", ex);
                return "The suspect could not be released safely. Dispatch custody remains active.";
            }

            return CompletePhysicalSuspectRelease(suspect, handle);
        }

        /// <summary>
        /// Convoy calls this only after its owned physical exit/release has
        /// completed. Dispatch updates the incident's exact suspect references
        /// but does not change the Ped's physical state a second time.
        /// </summary>
        internal string CompletePhysicalSuspectRelease(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.IsInVehicle())
                return "The suspect has not physically exited and cannot be released yet.";
            return CompletePhysicalSuspectRelease(suspect, suspect.Handle);
        }

        private string CompletePhysicalSuspectRelease(Ped suspect, int releasedHandle)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || suspect.Handle != releasedHandle
                || !IsArrested(incident, suspect))
                return "The released suspect no longer belongs to the active Dispatch custody.";

            incident.Suspects.RemoveAll(ped => ped == null || !ped.Exists()
                || ped.Handle == releasedHandle);
            incident.ArrestedSuspects.RemoveAll(ped => ped == null || !ped.Exists()
                || ped.Handle == releasedHandle);
            incident.CompliantSuspects.RemoveAll(ped => ped == null || !ped.Exists()
                || ped.Handle == releasedHandle);
            _lastCompliantTaskAt.Remove(releasedHandle);
            _lastSecuredPoseTaskAt.Remove(releasedHandle);
            // Release the injury snapshot only after Dispatch has removed the
            // Ped from its custody collections. This restores the same living
            // actor's original health/weapon controls without reviving it.
            RestoreSuspectInjuryState(suspect);
            LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_STATE_RELEASED",
                "Ped=" + releasedHandle + "; RuntimeOwner=Dispatch"
                + "; InjuryStateRemoved=true; SamePed=true"
                + "; Health=" + suspect.Health
                + "; CriticalHitsRestored=true");

            if (incident.Suspect != null && incident.Suspect.Handle == releasedHandle)
            {
                incident.Suspect = LiveSuspects(incident).FirstOrDefault();
                if (incident.Suspect != null
                    && (incident.State == LSPDDispatchState.SuspectFleeing
                        || incident.State == LSPDDispatchState.SuspectResisting))
                    SetSuspectBlip(incident.Suspect);
            }
            else
                MaintainSuspectBlips(DateTime.UtcNow, true);

            int remaining = LiveSuspects(incident).Count();
            LogRuntime("POLICE_DISPATCH_SUSPECT_RELEASED",
                "Ped=" + releasedHandle + "; Remaining=" + remaining
                + "; DispatchOwned=" + incident.OwnedByDispatch
                + "; ConvoyHandoff=" + incident.HasConvoyCustodyHandoff);

            if (remaining == 0)
            {
                if (HasPendingVictimInvestigation)
                {
                    SetState(LSPDDispatchState.Investigating);
                    Notify("~b~DISPATCH INVESTIGATION~s~\nInvestigate the murder victim before closing the assignment.");
                    return "The suspect was released. Investigate the murder victim before closing Dispatch.";
                }
                const string completed = "The secured suspect was released. Dispatch is closed.";
                Complete(completed);
                return completed;
            }

            const string continued = "The secured suspect was released. Other suspects remain in custody, so Dispatch stays active.";
            CloseActiveAudio(incident);
            Notify("~b~DISPATCH~s~\n" + continued);
            return continued;
        }

        private string BeginPlayerHandcuff(Ped player, Ped target)
        {
            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture =
                LSImmersiveDictionaryAnimation.ClassifyArrestTarget(target);
            if (_incident == null || player == null || !player.Exists()
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Dead
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Cuffed)
                return "The physical handcuff interaction is no longer available.";

            LSPDPlayerControlSuppression.SuppressMeleeThisFrame();

            DateTime now = DateTime.UtcNow;
            try
            {
                // Mark the target as controlled before assigning the native
                // arrest task. This closes the small window where a collision
                // or ambient task could make a compliant suspect walk away
                // while the visible player interaction is starting.
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    target, true, false, false, true, true, false, false, false);
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                bool targetDowned = IsGroundedForHandcuff(target);
                _incident.PlayerHandcuffInProgress = true;
                _incident.PlayerHandcuffTargetHandle = target.Handle;
                _incident.PlayerHandcuffStartedAt = now;

                if (targetDowned)
                {
                    // A living grounded suspect must be arrested in place.
                    // Requiring HandsUp/standing first left injured actors in
                    // an endless writhing loop and never reached physical cuffs.
                    target.Task.ClearAll();
                    _playerHandcuffAttempts = 0;
                    LogRuntime("POLICE_DISPATCH_PLAYER_GROUNDED_ARREST_STARTED",
                        "Ped=" + target.Handle + "; Player=" + player.Handle
                        + "; Ragdoll=" + IsLivingDowned(target)
                        + "; Writhe=" + IsLivingInWrithe(target)
                        + "; SamePed=true");
                }

                bool animationStarted = StartPlayerHandcuffAnimation(
                    player, target, now, true, targetDowned);
                if (!animationStarted && _playerHandcuffWaitingForAnimation)
                {
                    Notify("~b~POLICE CUSTODY~s~\nPreparing the paired handcuff animation. Stay close to the suspect.");
                    return "Preparing the handcuff animation...";
                }
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_STARTED",
                    "Ped=" + target.Handle + "; Player=" + player.Handle
                    + "; Posture=" + posture
                    + "; Method=" + (_playerHandcuffPairedAnimationStarted
                        ? "SynchronizedPairedAnimation" : "TaskArrestPed")
                    + "; SynchronizedScene=" + _playerHandcuffPairedAnimationStarted
                    + "; DownedOrInjured=" + targetDowned
                    + "; HoldMilliseconds=" + (int)Math.Max(0,
                        (_playerHandcuffHoldUntil - now).TotalMilliseconds));
                Notify(targetDowned
                    ? "~b~POLICE CUSTODY~s~\nArresting the wounded suspect from the ground. Stay close until the cuffs are confirmed."
                    : "~b~POLICE CUSTODY~s~\nPhysically handcuffing the compliant suspect. Keep close until secure.");
                return targetDowned
                    ? "Arresting the wounded suspect from the ground..."
                    : "Physically handcuffing the compliant suspect...";
            }
            catch (Exception ex)
            {
                ClearPlayerHandcuffState();
                LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_START_FAILED", ex);
                return "The physical handcuff interaction could not be started safely.";
            }
        }

        private bool StartPlayerHandcuffAnimation(
            Ped player,
            Ped target,
            DateTime now,
            bool allowSynchronizedAnimation,
            bool preserveGroundedArrestTask)
        {
            if (player == null || !player.Exists()
                || target == null || !target.Exists() || target.IsDead)
                return false;

            _playerHandcuffPairedAnimationStarted = false;
            _playerHandcuffWaitingForAnimation = false;
            _playerHandcuffAnimationLoadUntil = DateTime.MinValue;
            _playerHandcuffWaitingDictionary = null;
            _playerHandcuffSceneId = -1;
            _playerHandcuffAnimationVerifyAt = DateTime.MinValue;
            _playerHandcuffAnimationVerified = false;
            _playerHandcuffPreserveTargetPosture = false;
            _playerHandcuffDictionary = null;
            _playerHandcuffOfficerClip = null;
            _playerHandcuffSuspectClip = null;
            _playerHandcuffSceneOrigin = Vector3.Zero;
            _playerHandcuffSceneHeading = 0f;
            float clipDuration = 0f;
            int sceneId = -1;
            Vector3 sceneOrigin = Vector3.Zero;
            float sceneHeading = 0f;
            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture =
                LSImmersiveDictionaryAnimation.ClassifyArrestTarget(target);
            if (posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Dead
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Cuffed)
                return false;

            bool groundedArrestTask = preserveGroundedArrestTask
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded;
            bool kneelingArrestTask = !groundedArrestTask
                && posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling;
            _playerHandcuffPreserveTargetPosture = groundedArrestTask || kneelingArrestTask;
            string dictionary = groundedArrestTask ? "mp_arresting" : "mp_arrest_paired";
            string officerClip = groundedArrestTask
                ? "arrest_on_floor_front_right_a" : "cop_p2_back_right";
            string suspectClip = groundedArrestTask
                ? "arrest_on_floor_front_right_b" : "crook_p2_back_right";
            if (allowSynchronizedAnimation && groundedArrestTask)
            {
                const string groundDictionary = "mp_arresting";
                LSImmersiveDictionaryAnimation.RequestDictionary(groundDictionary);
                bool dictionaryLoaded = LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                    groundDictionary);
                float actorDistance = player.Position.DistanceTo(target.Position);
                if (!dictionaryLoaded || actorDistance > SynchronizedArrestMaximumDistance)
                {
                    _playerHandcuffWaitingForAnimation = true;
                    _playerHandcuffWaitingDictionary = groundDictionary;
                    _playerHandcuffAnimationLoadUntil = now.AddSeconds(
                        PlayerHandcuffAnimationLoadTimeoutSeconds);
                    _playerHandcuffHoldUntil = DateTime.MinValue;
                    _playerHandcuffAttempts = 0;
                    LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ANIMATION_WAITING",
                        "Ped=" + target.Handle + "; Player=" + player.Handle
                        + "; Dictionary=" + groundDictionary
                        + "; DictionaryLoaded=" + dictionaryLoaded
                        + "; Distance=" + actorDistance.ToString("0.00")
                        + "; RequiredDistance=" + SynchronizedArrestMaximumDistance.ToString("0.0")
                        + "; Grounded=true; TimeoutSeconds="
                        + PlayerHandcuffAnimationLoadTimeoutSeconds);
                    return false;
                }
                _playerHandcuffPairedAnimationStarted =
                    LSImmersiveDictionaryAnimation.TryPlaySynchronizedGroundArrest(
                        player, target, out sceneId, out clipDuration,
                        out sceneOrigin, out sceneHeading);
            }
            else if (allowSynchronizedAnimation && !kneelingArrestTask)
            {
                const string pairedDictionary = "mp_arrest_paired";
                LSImmersiveDictionaryAnimation.RequestDictionary(pairedDictionary);
                bool dictionaryLoaded = LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                    pairedDictionary);
                float actorDistance = player.Position.DistanceTo(target.Position);
                if (!dictionaryLoaded || actorDistance > SynchronizedArrestMaximumDistance)
                {
                    _playerHandcuffWaitingForAnimation = true;
                    _playerHandcuffWaitingDictionary = pairedDictionary;
                    _playerHandcuffAnimationLoadUntil = now.AddSeconds(
                        PlayerHandcuffAnimationLoadTimeoutSeconds);
                    _playerHandcuffHoldUntil = DateTime.MinValue;
                    _playerHandcuffAttempts = 0;
                    LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ANIMATION_WAITING",
                        "Ped=" + target.Handle + "; Player=" + player.Handle
                        + "; Dictionary=" + pairedDictionary
                        + "; DictionaryLoaded=" + dictionaryLoaded
                        + "; Distance=" + actorDistance.ToString("0.00")
                        + "; RequiredDistance=" + SynchronizedArrestMaximumDistance.ToString("0.0")
                        + "; Grounded=false; TimeoutSeconds="
                        + PlayerHandcuffAnimationLoadTimeoutSeconds);
                    return false;
                }
                _playerHandcuffPairedAnimationStarted =
                    LSImmersiveDictionaryAnimation.TryPlaySynchronizedArrest(
                        player, target, out sceneId, out clipDuration,
                        out sceneOrigin, out sceneHeading);
            }

            if (!_playerHandcuffPairedAnimationStarted)
            {
                if (!_playerHandcuffPreserveTargetPosture)
                {
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        player, target, 1000);
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        target, player, 1000);
                }
                Function.Call(Hash.TASK_ARREST_PED, player, target);
            }

            int handcuffDuration = _playerHandcuffPairedAnimationStarted
                ? Math.Max(PlayerHandcuffAnimationMilliseconds,
                    (int)Math.Ceiling(clipDuration * 1000.0f) + 500)
                : PlayerHandcuffAnimationMilliseconds;
            _playerHandcuffHoldUntil = now.AddMilliseconds(handcuffDuration);
            _playerHandcuffAttempts = Math.Max(1, _playerHandcuffAttempts);
            if (_playerHandcuffPairedAnimationStarted)
            {
                _playerHandcuffSceneId = sceneId;
                _playerHandcuffAnimationVerifyAt = now.AddMilliseconds(500);
                _playerHandcuffAnimationVerified = false;
                _playerHandcuffDictionary = dictionary;
                _playerHandcuffOfficerClip = officerClip;
                _playerHandcuffSuspectClip = suspectClip;
                _playerHandcuffSceneOrigin = sceneOrigin;
                _playerHandcuffSceneHeading = sceneHeading;
            }
            LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ANIMATION_STARTED",
                "Ped=" + target.Handle + "; Player=" + player.Handle
                + "; Method=" + (_playerHandcuffPairedAnimationStarted
                    ? groundedArrestTask
                        ? "SynchronizedGroundArrest" : "SynchronizedPairedAnimation"
                    : "TaskArrestPed")
                + "; Scene=" + (_playerHandcuffPairedAnimationStarted
                    ? sceneId.ToString() : "none")
                + "; ClipDuration=" + clipDuration.ToString("0.000")
                + "; Posture=" + posture
                + "; Dictionary=" + (_playerHandcuffPairedAnimationStarted
                    ? dictionary : "none")
                + "; OfficerClip=" + (_playerHandcuffPairedAnimationStarted
                    ? officerClip : "TASK_ARREST_PED")
                + "; SuspectClip=" + (_playerHandcuffPairedAnimationStarted
                    ? suspectClip : "TASK_ARREST_PED")
                + "; SceneOrigin=" + sceneOrigin.X.ToString("0.00") + ","
                    + sceneOrigin.Y.ToString("0.00") + ","
                    + sceneOrigin.Z.ToString("0.00")
                + "; SceneHeading=" + sceneHeading.ToString("0.0")
                + "; ScenePhase=" + GetScenePhaseText(sceneId)
                + "; Downed=" + groundedArrestTask
                + "; Kneeling=" + kneelingArrestTask
                + "; HoldMilliseconds=" + handcuffDuration);
            return true;
        }

        private static string GetScenePhaseText(int sceneId)
        {
            float phase;
            return LSImmersiveDictionaryAnimation.TryGetSynchronizedScenePhase(
                sceneId, out phase) ? phase.ToString("0.000") : "unavailable";
        }

        private void MaintainPlayerHandcuff(DateTime now, Ped player)
        {
            if (_incident == null || !_incident.PlayerHandcuffInProgress)
                return;

            Ped target = CurrentSuspects.FirstOrDefault(ped =>
                ped != null && ped.Exists()
                && ped.Handle == _incident.PlayerHandcuffTargetHandle);
            if (target == null || target.IsDead || target.Health <= 0)
            {
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ABORTED",
                    "Ped=" + _incident.PlayerHandcuffTargetHandle
                    + "; Reason=" + (target == null ? "PRISONER_REFERENCE_INVALID" : "PRISONER_DIED"));
                ClearPlayerHandcuffState();
                return;
            }

            // Keep the same handcuff operation alive while a punch reaction
            // temporarily owns the Ped's physical pose. Do not restart the
            // arrest task until that reaction has ended.
            if (IsDispatchMeleeRecoveryPending(target, now))
                return;

            try
            {
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    target, true, false, false, true, true, false, false, false);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_MAINTENANCE_FAILED", ex);
            }

            if (player == null || !player.Exists()
                || player.Position.DistanceTo(target.Position) > ArrestRadius + 2f)
            {
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ABORTED",
                    "Ped=" + target.Handle + "; Reason=PLAYER_LEFT_INTERACTION_RADIUS");
                ClearPlayerHandcuffState();
                Notify("The suspect remains compliant. Move close and press E again to finish handcuffing.");
                return;
            }

            if (_playerHandcuffSceneId >= 0
                && !_playerHandcuffAnimationVerified
                && now >= _playerHandcuffAnimationVerifyAt)
            {
                bool officerPlaying = LSImmersiveDictionaryAnimation.IsPlaying(
                    player, _playerHandcuffDictionary, _playerHandcuffOfficerClip);
                bool suspectPlaying = LSImmersiveDictionaryAnimation.IsPlaying(
                    target, _playerHandcuffDictionary, _playerHandcuffSuspectClip);
                float phase;
                bool phaseAvailable = LSImmersiveDictionaryAnimation.TryGetSynchronizedScenePhase(
                    _playerHandcuffSceneId, out phase);
                _playerHandcuffAnimationVerified = true;
                LogRuntime(officerPlaying && suspectPlaying
                        ? "POLICE_DISPATCH_ARREST_PAIR_PLAYBACK_CONFIRMED"
                        : "POLICE_DISPATCH_ARREST_PAIR_PLAYBACK_NOT_CONFIRMED",
                    "Incident=" + _incident.Id + "; Ped=" + target.Handle
                    + "; Player=" + player.Handle
                    + "; PosturePreserved=" + _playerHandcuffPreserveTargetPosture
                    + "; Dictionary=" + _playerHandcuffDictionary
                    + "; OfficerClip=" + _playerHandcuffOfficerClip
                    + "; OfficerPlaying=" + officerPlaying
                    + "; SuspectClip=" + _playerHandcuffSuspectClip
                    + "; SuspectPlaying=" + suspectPlaying
                    + "; Scene=" + _playerHandcuffSceneId
                    + "; ScenePhase=" + (phaseAvailable
                        ? phase.ToString("0.000") : "unavailable")
                    + "; SceneOrigin=" + _playerHandcuffSceneOrigin.X.ToString("0.00") + ","
                        + _playerHandcuffSceneOrigin.Y.ToString("0.00") + ","
                        + _playerHandcuffSceneOrigin.Z.ToString("0.00")
                    + "; SceneHeading=" + _playerHandcuffSceneHeading.ToString("0.0"));
                if (!officerPlaying || !suspectPlaying)
                {
                    _playerHandcuffPairedAnimationStarted = false;
                    _playerHandcuffSceneId = -1;
                    Function.Call(Hash.TASK_ARREST_PED, player, target);
                    _playerHandcuffHoldUntil = now.AddMilliseconds(
                        PlayerHandcuffAnimationMilliseconds);
                    LogRuntime("POLICE_DISPATCH_ARREST_PAIR_FALLBACK",
                        "Incident=" + _incident.Id + "; Ped=" + target.Handle
                        + "; Player=" + player.Handle
                        + "; Reason=PAIR_CLIP_NOT_PLAYING"
                        + "; Fallback=TASK_ARREST_PED"
                        + "; TargetPosture="
                        + LSImmersiveDictionaryAnimation.ClassifyArrestTarget(target));
                    return;
                }
            }

            if (_playerHandcuffWaitingForAnimation)
            {
                string waitingDictionary = string.IsNullOrWhiteSpace(
                    _playerHandcuffWaitingDictionary)
                    ? "mp_arrest_paired"
                    : _playerHandcuffWaitingDictionary;
                if (player.Position.DistanceTo(target.Position)
                    > SynchronizedArrestMaximumDistance)
                {
                    if (now < _playerHandcuffAnimationLoadUntil)
                        return;

                    ClearPlayerHandcuffState();
                    Notify("Move closer to the suspect before starting the paired handcuff animation, then press E again.");
                    return;
                }

                bool animationLoaded = LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                    waitingDictionary);
                if (animationLoaded)
                {
                    bool preserveGrounded = IsGroundedForHandcuff(target);
                    StartPlayerHandcuffAnimation(
                        player, target, now, true, preserveGrounded);
                    return;
                }

                if (now < _playerHandcuffAnimationLoadUntil)
                    return;

                _playerHandcuffWaitingForAnimation = false;
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ANIMATION_UNAVAILABLE",
                    "Ped=" + target.Handle + "; Player=" + player.Handle
                    + "; Dictionary=" + waitingDictionary
                    + "; Fallback=GroundedTaskArrestPed");
                StartPlayerHandcuffAnimation(player, target, now, false, false);
                Notify("The paired arrest animation did not load. Using GTA's physical arrest task as a fallback.");
                return;
            }

            if (now < _playerHandcuffHoldUntil)
                return;

            LogRuntime("POLICE_DISPATCH_ARREST_ANIMATION_COMMIT_POINT",
                "Incident=" + _incident.Id + "; Ped=" + target.Handle
                + "; Player=" + player.Handle
                + "; Scene=" + (_playerHandcuffSceneId < 0
                    ? "none" : _playerHandcuffSceneId.ToString())
                + "; ScenePhase=" + GetScenePhaseText(_playerHandcuffSceneId)
                + "; CuffedBeforeCommit=" + IsPedPhysicallyCuffed(target)
                + "; PhysicalHoldElapsed=true");

            // The captured Dispatch session repeated the arrest motion while
            // the engine still reported the Ped uncuffed. Commit the cuff state
            // only after the authored physical hold, then verify it before
            // custody begins.
            bool cuffed = IsPedPhysicallyCuffed(target);
            if (!cuffed)
            {
                cuffed = TryApplyPhysicalHandcuffs(target);
                if (cuffed)
                    LogRuntime("POLICE_DISPATCH_HANDCUFF_STATE_COMMITTED",
                        "Incident=" + _incident.Id + "; Ped=" + target.Handle
                        + "; RuntimeOwner=Dispatch"
                        + "; ConfigFlag=" + HandcuffedPedConfigFlag
                        + "; Cuffed=" + IsPedPhysicallyCuffed(target)
                        + "; PhysicalHoldElapsed=true; SamePed=true");
            }
            if (!cuffed)
            {
                if (_playerHandcuffAttempts < PlayerHandcuffMaximumAttempts)
                {
                    try
                    {
                        _playerHandcuffAttempts++;
                        bool retryStarted = StartPlayerHandcuffAnimation(
                            player, target, now, true, IsGroundedForHandcuff(target));
                        if (!retryStarted && _playerHandcuffWaitingForAnimation)
                            return;
                        LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_RETRIED",
                            "Ped=" + target.Handle + "; Player=" + player.Handle
                            + "; Attempt=" + _playerHandcuffAttempts
                            + "; MaximumAttempts=" + PlayerHandcuffMaximumAttempts
                            + "; DownedOrInjured=" + IsDownedOrInjured(target)
                            + "; Method=" + (_playerHandcuffPairedAnimationStarted
                                ? IsGroundedForHandcuff(target)
                                    ? "SynchronizedGroundArrest" : "SynchronizedPairedAnimation"
                                : "TaskArrestPed"));
                        Notify("Keep beside the suspect while the physical handcuffing finishes.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_RETRY_FAILED", ex);
                    }
                }

                ClearPlayerHandcuffState();
                if (!IsDownedOrInjured(target))
                {
                    try { target.Task.HandsUp(15000); } catch { }
                }
                Notify("The handcuff state was not confirmed. The suspect remains available; move close and press E to retry.");
                return;
            }

            try { player.Task.ClearAll(); } catch { }
            string result = CompletePlayerHandcuff(target);
            if (!string.IsNullOrWhiteSpace(result))
                Notify(result);
        }

        private string CompletePlayerHandcuff(Ped target)
        {
            if (_incident == null || target == null || !target.Exists()
                || target.IsDead || target.Health <= 0
                || !IsPedPhysicallyCuffed(target))
            {
                ClearPlayerHandcuffState();
                return "The suspect could not be retained in physical custody because the game did not confirm the handcuffs.";
            }

            try
            {
                if (!TryApplyPhysicalHandcuffs(target))
                {
                    ClearPlayerHandcuffState();
                    return "The game did not retain the physical handcuff state. The suspect remains available; try again.";
                }
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, target, true);
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    target, false, false, false, true, false, false, false, false);
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                AddArrestedSuspect(_incident, target);
                ClearPlayerHandcuffState();
                _lastSecuredPoseTaskAt.Remove(target.Handle);
                MaintainHandcuffedPose(target, DateTime.UtcNow, true);

                int remaining = LiveSuspects(_incident).Count(ped => !IsArrested(_incident, ped));
                if (remaining > 0)
                {
                    SetStateForUnresolvedSuspects(
                        LSPDDispatchState.SuspectCompliant);
                    Notify("~g~SUSPECT SECURED~s~\n" + remaining
                        + " suspect(s) still require physical custody.");
                    LogRuntime("POLICE_GROUP_SUSPECT_SECURED",
                        "Ped=" + target.Handle + "; Remaining=" + remaining
                        + "; Method=PlayerHandcuff");
                    return "Suspect physically handcuffed. " + remaining
                        + " living suspect(s) still require custody.";
                }

                _incident.ArrestSecured = true;
                SetState(LSPDDispatchState.Arrested);
                ReportAudioStage("lsimmersivelife.police.arrest.successful", _incident, "arrested");
                int custodyCount = CurrentArrestedSuspects.Count();
                bool victimInvestigationPending = HasPendingVictimInvestigation;
                Notify(victimInvestigationPending
                    ? "~g~ARREST SUCCESS~s~\n" + custodyCount
                        + " suspect(s) physically secured. Investigate the murder victim before requesting transport."
                    : "~g~ARREST SUCCESS~s~\n" + custodyCount
                        + " suspect(s) physically secured. Request prisoner transport when ready.");
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_COMPLETED",
                    "Ped=" + target.Handle + "; Prisoners=" + custodyCount
                    + "; VictimInvestigationPending=" + victimInvestigationPending);
                if (victimInvestigationPending)
                    return custodyCount == 1
                        ? "Suspect handcuffed and secured. Investigate the murder victim before requesting transport."
                        : custodyCount + " suspects handcuffed and secured. Investigate the murder victim before requesting transport.";
                return custodyCount == 1
                    ? "Suspect handcuffed and secured. Prisoner custody is ready for transport."
                    : custodyCount + " suspects handcuffed and secured. Group custody is ready for transport.";
            }
            catch (Exception ex)
            {
                ClearPlayerHandcuffState();
                LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_COMPLETE_FAILED", ex);
                return "The suspect could not be secured physically; keep the suspect compliant and try E again.";
            }
        }

        private void ClearPlayerHandcuffState()
        {
            if (_incident != null && _incident.PlayerHandcuffTargetHandle > 0
                && _incident.Suspects != null)
            {
                Ped handcuffTarget = _incident.Suspects.FirstOrDefault(ped =>
                    ped != null && ped.Exists()
                    && ped.Handle == _incident.PlayerHandcuffTargetHandle);
                if (handcuffTarget != null && !handcuffTarget.IsDead)
                    try
                    {
                        // Remove the short anti-late-shot guard on every abort
                        // or completion. Dispatch's normal injury/custody owner
                        // resumes damage handling as soon as the physical
                        // handcuff interaction ends.
                        Function.Call(Hash.SET_ENTITY_PROOFS,
                            handcuffTarget, false, false, false,
                            IsArrested(_incident, handcuffTarget), false, false, false, false);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_HANDCUFF_PROOF_RESTORE_FAILED", ex);
                    }
            }
            _playerHandcuffHoldUntil = DateTime.MinValue;
            _playerHandcuffAttempts = 0;
            _playerHandcuffAnimationLoadUntil = DateTime.MinValue;
            _playerHandcuffWaitingDictionary = null;
            _playerHandcuffWaitingForAnimation = false;
            _playerHandcuffPairedAnimationStarted = false;
            _playerHandcuffSceneId = -1;
            _playerHandcuffAnimationVerifyAt = DateTime.MinValue;
            _playerHandcuffAnimationVerified = false;
            _playerHandcuffPreserveTargetPosture = false;
            _playerHandcuffDictionary = null;
            _playerHandcuffOfficerClip = null;
            _playerHandcuffSuspectClip = null;
            _playerHandcuffSceneOrigin = Vector3.Zero;
            _playerHandcuffSceneHeading = 0f;
            if (_incident == null)
                return;
            _incident.PlayerHandcuffInProgress = false;
            _incident.PlayerHandcuffTargetHandle = 0;
            _incident.PlayerHandcuffStartedAt = DateTime.MinValue;
        }

        private void MaintainBackupArrests(DateTime now)
        {
            if (_backupArrestOperations.Count == 0)
                return;

            LSPDDispatchEvent incident = _incident;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff)
            {
                ClearBackupArrestOperations(incident);
                return;
            }

            foreach (KeyValuePair<int, BackupArrestOperation> entry
                in _backupArrestOperations.ToArray())
            {
                BackupArrestOperation operation = entry.Value;
                Ped suspect = operation == null ? null : operation.Suspect;
                Ped officer = operation == null ? null : operation.Officer;
                if (suspect == null || !suspect.Exists() || suspect.IsDead
                    || suspect.Health <= 0 || officer == null || !officer.Exists()
                    || officer.IsDead)
                {
                    EndBackupArrestOperation(incident, entry.Key, true);
                    LogRuntime("POLICE_BACKUP_DISPATCH_ARREST_ABORTED",
                        "Incident=" + incident.Id
                        + "; Suspect=" + entry.Key
                        + "; Reason=" + (suspect == null || !suspect.Exists()
                            ? "ACTOR_REFERENCE_INVALID"
                            : suspect.IsDead || suspect.Health <= 0
                                ? "SUSPECT_DIED" : "OFFICER_UNAVAILABLE"));
                    continue;
                }

                if (IsPedPhysicallyCuffed(suspect))
                {
                    CompleteBackupSuspectArrest(incident, operation, now);
                    continue;
                }

                if (operation.PairedAnimationStarted
                    && now >= operation.AnimationCompleteAt)
                {
                    if (TryApplyPhysicalHandcuffs(suspect))
                    {
                        CompleteBackupSuspectArrest(incident, operation, now);
                        continue;
                    }
                    if (!StartBackupArrestNativeFallback(operation, now))
                    {
                        FailBackupArrestOperation(incident, entry.Key,
                            "PAIRED_ANIMATION_DID_NOT_CONFIRM_CUFFS");
                        continue;
                    }
                }

                if (now >= operation.TaskDeadline)
                    FailBackupArrestOperation(incident, entry.Key,
                        "PHYSICAL_CUFF_STATE_NOT_CONFIRMED");
            }
        }

        private bool StartBackupArrestNativeFallback(
            BackupArrestOperation operation,
            DateTime now)
        {
            if (operation == null || operation.Officer == null
                || !operation.Officer.Exists() || operation.Officer.IsDead
                || operation.Suspect == null || !operation.Suspect.Exists()
                || operation.Suspect.IsDead)
                return false;
            try
            {
                operation.Officer.Task.ClearAll();
                operation.Suspect.Task.ClearAll();
                Function.Call(Hash.TASK_ARREST_PED,
                    operation.Officer, operation.Suspect);
                operation.PairedAnimationStarted = false;
                operation.NativeTaskFallbackStarted = true;
                operation.TaskDeadline = now.AddSeconds(
                    BackupArrestTaskTimeoutSeconds);
                LogRuntime("POLICE_BACKUP_DISPATCH_ARREST_FALLBACK_STARTED",
                    "Officer=" + operation.Officer.Handle
                    + "; Suspect=" + operation.Suspect.Handle
                    + "; Reason=PAIRED_ANIMATION_DID_NOT_CONFIRM_CUFFS");
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_ARREST_FALLBACK_FAILED", ex);
                return false;
            }
        }

        private void CompleteBackupSuspectArrest(
            LSPDDispatchEvent incident,
            BackupArrestOperation operation,
            DateTime now)
        {
            Ped suspect = operation == null ? null : operation.Suspect;
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || suspect.Health <= 0
                || !TryApplyPhysicalHandcuffs(suspect))
            {
                if (operation != null && operation.Suspect != null)
                    FailBackupArrestOperation(incident,
                        operation.Suspect.Handle,
                        "PHYSICAL_CUFF_STATE_NOT_CONFIRMED");
                return;
            }

            try
            {
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, suspect, true);
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    suspect, false, false, false, true, false, false, false, false);
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_DISPATCH_ARREST_CUSTODY_SETUP_FAILED", ex);
                FailBackupArrestOperation(incident, suspect.Handle,
                    "CUSTODY_STATE_SETUP_FAILED");
                return;
            }

            AddArrestedSuspect(incident, suspect);
            incident.PursuitContainedSuspectHandles.Add(suspect.Handle);
            incident.CompliantSuspects.RemoveAll(value => value == null
                || !value.Exists() || value.Handle == suspect.Handle);
            EndBackupArrestOperation(incident, suspect.Handle, false);
            _lastSecuredPoseTaskAt.Remove(suspect.Handle);
            MaintainHandcuffedPose(suspect, now, true);

            int remaining = LiveSuspects(incident)
                .Count(value => !IsArrested(incident, value));
            if (remaining > 0)
            {
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectCompliant);
                Notify("~g~BACKUP CUSTODY~s~\nBackup physically secured one suspect. "
                    + remaining + " suspect(s) remain unresolved.");
            }
            else
            {
                incident.ArrestSecured = true;
                SetState(LSPDDispatchState.Arrested);
                ReportAudioStage(
                    "lsimmersivelife.police.arrest.successful",
                    incident,
                    "backup-arrest-" + suspect.Handle);
                Notify("~g~ARREST SUCCESS~s~\nBackup physically secured the suspect group. Request prisoner transport when ready.");
            }

            LogRuntime("POLICE_BACKUP_DISPATCH_ARREST_COMPLETED",
                "Incident=" + incident.Id
                + "; Officer=" + (operation.Officer == null
                    ? 0 : operation.Officer.Handle)
                + "; Suspect=" + suspect.Handle
                + "; PhysicalCuffs=" + IsPedPhysicallyCuffed(suspect)
                + "; Remaining=" + remaining);
        }

        private void FailBackupArrestOperation(
            LSPDDispatchEvent incident,
            int suspectHandle,
            string reason)
        {
            EndBackupArrestOperation(incident, suspectHandle, true);
            LogRuntime("POLICE_BACKUP_DISPATCH_ARREST_FAILED",
                "Incident=" + (incident == null ? string.Empty : incident.Id)
                + "; Suspect=" + suspectHandle
                + "; Reason=" + (reason ?? "UNKNOWN")
                + "; DispatchCustodyUnchanged=true");
            Notify("~y~BACKUP CUSTODY~s~\nBackup could not confirm the physical arrest. Secure the suspect yourself.");
        }

        private void EndBackupArrestOperation(
            LSPDDispatchEvent incident,
            int suspectHandle,
            bool restoreSuspect)
        {
            BackupArrestOperation operation;
            if (!_backupArrestOperations.TryGetValue(suspectHandle, out operation))
                return;
            _backupArrestOperations.Remove(suspectHandle);
            if (incident != null
                && incident.BackupArrestInProgressSuspectHandles != null)
                incident.BackupArrestInProgressSuspectHandles.Remove(suspectHandle);
            if (incident != null && operation != null && operation.Officer != null)
                incident.BackupArrestInProgressOfficerHandles.Remove(
                    operation.Officer.Handle);
            try
            {
                if (operation.Officer != null && operation.Officer.Exists())
                    operation.Officer.Task.ClearAll();
            }
            catch { }
            if (restoreSuspect && operation.Suspect != null
                && operation.Suspect.Exists() && !operation.Suspect.IsDead
                && !(incident != null && incident.PlayerHandcuffInProgress
                    && incident.PlayerHandcuffTargetHandle == suspectHandle))
                RestoreBackupArrestTarget(operation.Suspect, false);
        }

        private void ClearBackupArrestOperations(LSPDDispatchEvent incident)
        {
            foreach (int suspectHandle in _backupArrestOperations.Keys.ToArray())
                EndBackupArrestOperation(incident, suspectHandle, true);
        }

        private static void RestoreBackupArrestTarget(Ped suspect, bool secured)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead)
                return;
            try
            {
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    suspect, false, false, false, secured, false, false, false, false);
                if (!secured)
                    suspect.Task.ClearAll();
            }
            catch { }
        }

        /// <summary>
        /// Called by the dedicated Backup owner only after its real support
        /// units arrive at the assignment. This never creates or teleports
        /// suspects; it turns a reachable, non-combat group into a visible
        /// hands-up state so the player can still perform each final secure.
        /// </summary>
        internal int RequestBackupGroupCompliance(Ped player)
        {
            if (_incident == null || player == null || !player.Exists())
                return 0;

            int ordered = 0;
            foreach (Ped suspect in LiveSuspects(_incident))
            {
                if (IsArrested(_incident, suspect)
                    || IsDownedOrInjured(suspect)
                    || suspect.IsInCombatAgainst(player) || suspect.IsShooting)
                    continue;
                if (suspect.Position.DistanceTo(_incident.Origin) > 75f)
                    continue;
                if (!IsCompliant(_incident, suspect))
                {
                    try
                    {
                        suspect.Task.ClearAll();
                        suspect.BlockPermanentEvents = true;
                        suspect.CanSwitchWeapons = false;
                        suspect.Task.HandsUp(12000);
                        AddCompliantSuspect(_incident, suspect);
                        ordered++;
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_GROUP_COMPLIANCE_FAILED", ex);
                    }
                }
            }
            if (ordered > 0)
            {
                _incident.SurrenderRequested = true;
                _incident.GroupComplianceRequested = true;
                _incident.SurrenderRequestedAt = DateTime.UtcNow;
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectCompliant);
                LogRuntime("POLICE_BACKUP_GROUP_COMPLIANCE",
                    "Ordered=" + ordered + "; Suspects=" + ActiveSuspectCount);
            }
            return ordered;
        }

        /// <summary>
        /// An interception unit can reach a fleeing target before the player.
        /// It pins the actual tracked suspect in a compliant state; the player
        /// still owns the final arrest/Convoy handoff.
        /// </summary>
        internal bool RequestBackupInterception(Ped suspect, Ped player)
        {
            if (_incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || !CurrentSuspects.Any(ped => ped.Handle == suspect.Handle))
                return false;
            if (IsArrested(_incident, suspect))
                return true;
            if (IsDownedOrInjured(suspect))
            {
                _incident.PursuitContainedSuspectHandles.Add(suspect.Handle);
                return true;
            }
            // Backup.Process keeps tracking an intercepted suspect while the
            // officer travels to the marked position. Do not clear and restart
            // the hands-up task every game tick once the real suspect is
            // already compliant; that interrupted the visible surrender pose.
            if (IsCompliant(_incident, suspect))
                return true;
            try
            {
                suspect.Task.ClearAll();
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                suspect.Task.HandsUp(15000);
                if (player != null && player.Exists())
                    suspect.Task.LookAt(player, 2000);
                AddCompliantSuspect(_incident, suspect);
                _incident.SurrenderRequested = true;
                _incident.SurrenderRequestedAt = DateTime.UtcNow;
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectCompliant);
                LogRuntime("POLICE_BACKUP_INTERCEPTION_CONFIRMED",
                    "Ped=" + suspect.Handle + "; Position=" + suspect.Position);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_INTERCEPTION_FAILED", ex);
                return false;
            }
        }

        private string OrderSuspectToComply(Ped suspect, Ped player, bool groupCommand)
        {
            try
            {
                suspect.Task.ClearAll();
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                suspect.Task.HandsUp(10000);
                suspect.Task.LookAt(player, 2500);
                AddCompliantSuspect(_incident, suspect);
                _incident.SurrenderRequested = true;
                _incident.SurrenderRequestedAt = DateTime.UtcNow;
                Notify("~b~POLICE COMMAND~s~\nSuspect ordered to show hands.\n~c~Wait, then secure again.");
                LogRuntime("POLICE_SUSPECT_SURRENDER_REQUESTED",
                    "Ped=" + suspect.Handle + "; Group=" + groupCommand);
                return "Surrender command issued. Wait for visible compliance, then secure again.";
            }
            catch (Exception ex)
            {
                LogException("POLICE_SUSPECT_SURRENDER_FAILED", ex);
                return "The surrender command could not be assigned safely.";
            }
        }

        private static bool IsCompliant(LSPDDispatchEvent incident, Ped suspect)
        {
            return incident != null && suspect != null && suspect.Exists()
                && !suspect.IsDead && suspect.Health > 0
                && incident.CompliantSuspects != null
                && incident.CompliantSuspects.Any(value => value != null && value.Exists()
                    && !value.IsDead && value.Health > 0
                    && value.Handle == suspect.Handle);
        }

        private static bool IsLivingDowned(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.Health <= 0)
                return false;
            try
            {
                return Function.Call<bool>(Hash.IS_PED_RAGDOLL, suspect);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLivingInWrithe(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.Health <= 0)
                return false;
            try { return Function.Call<bool>(Hash.IS_PED_IN_WRITHE, suspect); }
            catch { return false; }
        }

        private static bool IsGroundedForHandcuff(Ped suspect)
        {
            return IsLivingDowned(suspect) || IsLivingInWrithe(suspect);
        }

        private bool IsWoundedSuspect(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.Health <= 0)
                return false;
            LSPDSuspectInjuryState state;
            return _suspectInjuries.TryGetValue(suspect.Handle, out state)
                && state.Wounded && !state.Fatal;
        }

        private bool IsDownedOrInjured(Ped suspect)
        {
            return IsLivingDowned(suspect) || IsWoundedSuspect(suspect);
        }

        private static bool IsPedPhysicallyCuffed(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return false;
            try { return Function.Call<bool>(Hash.IS_PED_CUFFED, suspect); }
            catch { return false; }
        }

        private static bool TryApplyPhysicalHandcuffs(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead || suspect.Health <= 0)
                return false;
            try
            {
                Function.Call(Hash.SET_PED_CONFIG_FLAG,
                    suspect, HandcuffedPedConfigFlag, true);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, suspect, true);
                return IsPedPhysicallyCuffed(suspect);
            }
            catch
            {
                return false;
            }
        }

        private void MaintainTrackedSuspectInjuries(DateTime now, Ped player)
        {
            if (_incident == null)
            {
                RestoreAllSuspectInjuryStates();
                return;
            }
            if (!_incident.OwnedByDispatch || _incident.HasConvoyCustodyHandoff)
            {
                // Convoy is the sole physical owner after its explicit
                // handoff. Do not keep applying Dispatch injury tasks then.
                RestoreAllSuspectInjuryStates();
                return;
            }

            if (_incident.CompliantSuspects != null)
            {
                List<int> removed = _incident.CompliantSuspects
                    .Where(value => value == null || !value.Exists()
                        || value.IsDead || value.Health <= 0)
                    .Select(value => value == null || !value.Exists()
                        ? 0 : value.Handle)
                    .ToList();
                _incident.CompliantSuspects.RemoveAll(value => value == null
                    || !value.Exists() || value.IsDead || value.Health <= 0);
                foreach (int handle in removed)
                {
                    _lastCompliantTaskAt.Remove(handle);
                    _lastSecuredPoseTaskAt.Remove(handle);
                }
            }

            foreach (Ped suspect in TrackedSuspects(_incident))
            {
                if (suspect == null)
                    continue;
                CaptureDispatchSuspectInjuryState(suspect);
                MaintainDispatchSuspectInjury(suspect, now, player);
                LSPDSuspectInjuryState injuryState;
                if (_incident != null
                    && _suspectInjuries.TryGetValue(suspect.Handle, out injuryState)
                    && injuryState.Wounded && !injuryState.Fatal)
                    _incident.PursuitContainedSuspectHandles.Add(suspect.Handle);
            }
        }

        private void CaptureDispatchSuspectInjuryState(
            Ped suspect,
            LSPDDispatchEvent incident = null)
        {
            if (suspect == null || _suspectInjuries.ContainsKey(suspect.Handle)
                || !suspect.Exists() || suspect.IsDead || suspect.Health <= 0)
                return;

            int originalHealth = Math.Max(1, suspect.Health);
            int originalMaximumHealth = Math.Max(1, suspect.MaxHealth);
            LSPDSuspectInjuryState state = new LSPDSuspectInjuryState
            {
                Actor = suspect,
                OriginalHealth = originalHealth,
                OriginalMaximumHealth = originalMaximumHealth,
                LastHealth = originalHealth,
                ManagedMaximumHealth = Math.Max(
                    _injurySettings.ControlledMaximumHealth,
                    Math.Max(originalHealth, originalMaximumHealth)),
                ArrestableWoundedHealthFloor = _injurySettings.ArrestableWoundedHealthFloor,
                OccurrenceId = incident != null ? incident.Id
                    : (_incident == null ? string.Empty : _incident.Id),
                OriginalCanSufferCriticalHits = suspect.CanSufferCriticalHits,
                OriginalCanSwitchWeapons = suspect.CanSwitchWeapons
            };
            try
            {
                state.OriginalSelectedWeaponHash = Function.Call<int>(
                    Hash.GET_SELECTED_PED_WEAPON, suspect);
                suspect.CanSufferCriticalHits = false;
                suspect.MaxHealth = state.ManagedMaximumHealth;
                // One-time ownership setup for a living Ped. Damage handling
                // below never heals or revives a suspect after an injury.
                if (!suspect.IsDead && suspect.Health > 0
                    && suspect.Health < state.ManagedMaximumHealth)
                    suspect.Health = state.ManagedMaximumHealth;
                state.LastHealth = Math.Max(1, suspect.Health);
                try { Function.Call(Hash.CLEAR_PED_LAST_DAMAGE_BONE, suspect); }
                catch { }
                try { Function.Call(Hash.CLEAR_ENTITY_LAST_WEAPON_DAMAGE, suspect); }
                catch { }
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_CRITICAL_HIT_CONTROL_FAILED", ex);
            }
            _suspectInjuries[suspect.Handle] = state;
            LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_CONTROLLED",
                "Session=" + (_log == null ? string.Empty : _log.SessionId)
                + "; Incident=" + state.OccurrenceId
                + "; RuntimeOwner=Dispatch; Ped=" + suspect.Handle
                + "; HealthBefore=" + originalHealth
                + "; ManagedHealth=" + state.LastHealth
                + "; ManagedMaximum=" + state.ManagedMaximumHealth
                + "; CanSufferCriticalHits=false; ReusedPed=true");
        }

        private void MaintainDispatchSuspectInjury(
            Ped suspect,
            DateTime now,
            Ped player)
        {
            LSPDSuspectInjuryState state;
            if (suspect == null || !suspect.Exists()
                || !_suspectInjuries.TryGetValue(suspect.Handle, out state))
                return;
            if (state.Fatal)
                return;

            int currentHealth = Math.Max(0, suspect.Health);
            if (currentHealth < state.LastHealth)
            {
                int damage = state.LastHealth - currentHealth;
                // Capture the hit bone before any native clears the damage
                // record. The preceding work is only a health read and delta.
                int rawBone = ReadDispatchLastDamageBone(suspect);
                bool retainedBone = false;
                int bone = rawBone;
                if (rawBone != 0)
                {
                    state.LastValidDamageBone = rawBone;
                    state.LastValidDamageBoneAt = now;
                }
                else if (state.LastValidDamageBone != 0
                    && state.LastValidDamageBoneAt != DateTime.MinValue
                    && now - state.LastValidDamageBoneAt
                        <= TimeSpan.FromMilliseconds(
                            _injurySettings.LastDamageBoneRetentionMilliseconds))
                {
                    bone = state.LastValidDamageBone;
                    retainedBone = true;
                }
                int damageWeaponHash;
                int damageAttackerHandle;
                int damageGameTime;
                bool damageRecordAvailable = TryReadDispatchDamageRecord(
                    suspect, out damageWeaponHash, out damageAttackerHandle,
                    out damageGameTime);
                state.LastDamageWeaponHash = damageWeaponHash;
                state.LastDamageAttackerHandle = damageAttackerHandle;
                state.LastDamageGameTime = damageGameTime;
                ControlledDamageKind damageKind = ReadDispatchDamageKind(
                    suspect, damageWeaponHash, damageRecordAvailable);
                DispatchHitZone zone = ClassifyDispatchHitZone(bone);
                ClearDispatchLastDamageBone(suspect);
                try { Function.Call(Hash.CLEAR_ENTITY_LAST_WEAPON_DAMAGE, suspect); }
                catch { }

                state.DamageTaken += damage;
                state.RegisteredHits++;
                bool damageKindKnown = damageKind != ControlledDamageKind.Unknown;
                if (damageKindKnown)
                    state.KnownDamageHits++;
                if (damageKind == ControlledDamageKind.Melee)
                    state.MeleeHits++;
                bool wasDowned = state.Wounded
                    && (IsLivingDowned(suspect) || IsLivingInWrithe(suspect));
                string previousState = wasDowned
                    ? "DownedArrestable"
                    : (_incident == null ? "Active" : _incident.State.ToString());
                if (wasDowned)
                {
                    state.DownedHits++;
                    if (damageKindKnown)
                        state.KnownDownedHits++;
                }
                if (zone == DispatchHitZone.Head)
                    state.HeadHits++;
                else if (zone == DispatchHitZone.Chest)
                    state.ChestHits++;
                else if (zone == DispatchHitZone.Stomach)
                    state.StomachHits++;

                state.LastDamageKind = damageKind.ToString();
                // Damage thresholds must not synthesize a death while the game
                // still reports a living suspect. A short burst can produce
                // several tiny health deltas during one player action, and a
                // retained hit bone can be stale; neither is proof of death.
                // The runtime injury owner only records a fatal outcome after
                // GTA reports the Ped dead or its actual health reaches zero.
                bool engineTerminal = suspect.IsDead || currentHealth <= 0;
                state.Fatal = engineTerminal;

                LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_RECORDED",
                    "Session=" + (_log == null ? string.Empty : _log.SessionId)
                    + "; Incident=" + state.OccurrenceId
                    + "; RuntimeOwner=Dispatch"
                    + "; Ped=" + suspect.Handle
                    + "; PreviousState=" + previousState
                    + "; HealthBefore=" + state.LastHealth
                    + "; HealthAfter=" + currentHealth
                    + "; DamageDelta=" + damage
                    + "; RawBone=" + rawBone
                    + "; EffectiveBone=" + bone
                    + "; RetainedBone=" + retainedBone
                    + "; Zone=" + zone
                    + "; DamageKind=" + damageKind
                    + "; DamageRecordAvailable=" + damageRecordAvailable
                    + "; DamageWeaponHash=" + damageWeaponHash
                    + "; DamageAttackerHandle=" + damageAttackerHandle
                    + "; DamageGameTime=" + damageGameTime
                    + "; HeadHits=" + state.HeadHits
                    + "; ChestHits=" + state.ChestHits
                    + "; StomachHits=" + state.StomachHits
                    + "; RegisteredHits=" + state.RegisteredHits
                    + "; KnownDamageHits=" + state.KnownDamageHits
                    + "; DownedHits=" + state.DownedHits
                    + "; KnownDownedHits=" + state.KnownDownedHits
                    + "; CumulativeDamage=" + state.DamageTaken
                    + "; Wounded=" + state.Wounded
                    + "; Fatal=" + state.Fatal);

                if (state.Fatal)
                {
                    state.LastHealth = 0;
                    LogRuntime("POLICE_DISPATCH_SUSPECT_FATAL_INJURY",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + suspect.Handle
                        + "; DamageKind=" + damageKind
                        + "; Zone=" + zone
                        + "; Reason=EngineTerminal"
                        + "; RegisteredHits=" + state.RegisteredHits
                        + "; KnownDamageHits=" + state.KnownDamageHits
                        + "; DownedHits=" + state.DownedHits
                        + "; KnownDownedHits=" + state.KnownDownedHits
                        + "; CumulativeDamage=" + state.DamageTaken);
                    if (_incident != null
                        && string.Equals(_incident.Id, state.OccurrenceId,
                            StringComparison.Ordinal))
                    {
                        int otherLivingSuspects = LiveSuspects(_incident)
                            .Count(value => value.Handle != suspect.Handle);
                        if (otherLivingSuspects > 0)
                        {
                            string remaining = otherLivingSuspects == 1
                                ? "1 other living suspect remains."
                                : otherLivingSuspects + " other living suspects remain.";
                            Notify("~r~POLICE DISPATCH~s~\nSuspect deceased. Dispatch remains active. "
                                + remaining);
                        }
                    }
                    return;
                }

                try
                {
                    if (currentHealth <= 0)
                    {
                        state.Fatal = true;
                        state.LastHealth = 0;
                        LogRuntime("POLICE_DISPATCH_SUSPECT_ENGINE_TERMINAL_DAMAGE",
                            "Incident=" + state.OccurrenceId + "; Ped=" + suspect.Handle
                            + "; Reason=HealthReachedZeroBeforeSafeInterception");
                        return;
                    }

                    if (damageKind == ControlledDamageKind.Melee)
                    {
                        state.MeleeReactionPending = true;
                        state.MeleeReactionUntil = now.AddMilliseconds(
                            _injurySettings.MeleeReactionRecoveryMilliseconds);
                        state.NextMeleeRecoveryCheckAt = state.MeleeReactionUntil;
                    }
                    else
                    {
                        state.Wounded = true;
                        DisableWoundedSuspectWeapon(suspect, state);
                    }

                    if (currentHealth < _injurySettings.ArrestableWoundedHealthFloor)
                        suspect.Health = _injurySettings.ArrestableWoundedHealthFloor;
                    state.LastHealth = Math.Max(1, suspect.Health);

                    if (damageKind == ControlledDamageKind.Melee)
                    {
                        LogRuntime("POLICE_DISPATCH_SUSPECT_MELEE_REACTION",
                            "Incident=" + state.OccurrenceId
                            + "; Ped=" + suspect.Handle
                            + "; DamageKind=Melee; PreviousState=" + previousState
                            + "; MeleeHits=" + state.MeleeHits
                            + "; Downed=" + wasDowned
                            + "; ReactionUntil=" + state.MeleeReactionUntil.ToString("O", CultureInfo.InvariantCulture)
                            + "; Wounded=" + state.Wounded
                            + "; Arrestable=" + (state.Wounded || IsCompliant(_incident, suspect))
                            + "; TaskCleared=false; SceneReset=false");
                    }
                }
                catch (Exception ex)
                {
                    state.LastHealth = Math.Max(0, suspect.Health);
                    LogException("POLICE_DISPATCH_SUSPECT_WOUNDED_FLOOR_FAILED", ex);
                }
            }

            bool handcuffing = _incident != null
                && _incident.PlayerHandcuffInProgress
                && _incident.PlayerHandcuffTargetHandle == suspect.Handle;
            if (!state.Wounded || state.Fatal || handcuffing
                || IsArrested(_incident, suspect)
                || suspect.IsDead || suspect.Health <= 0
                || suspect.IsInVehicle()
                || IsDispatchMeleeRecoveryPending(suspect, now)
                || state.WritheTaskStarted)
                return;

            try
            {
                if (Function.Call<bool>(Hash.IS_PED_IN_WRITHE, suspect))
                {
                    state.WritheTaskStarted = true;
                    return;
                }
                state.WritheTaskStarted = true;
                Function.Call(Hash.TASK_WRITHE,
                    suspect,
                    player != null && player.Exists() ? player : suspect,
                    -1, 0, false, 0);
                LogRuntime("POLICE_DISPATCH_SUSPECT_WRITHE_TASK_ISSUED",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                    + "; Arrestable=true; Teleport=false; Attachment=false");
            }
            catch (Exception ex)
            {
                try
                {
                    Function.Call(Hash.SET_PED_TO_RAGDOLL,
                        suspect, 3500, 3500, 0, false, false, false);
                }
                catch (Exception ragdollException)
                {
                    LogException("POLICE_DISPATCH_SUSPECT_WRITHE_FALLBACK_FAILED",
                        ragdollException);
                }
                LogException("POLICE_DISPATCH_SUSPECT_WRITHE_TASK_FAILED", ex);
            }
        }

        private void DisableWoundedSuspectWeapon(
            Ped suspect,
            LSPDSuspectInjuryState state)
        {
            if (suspect == null || !suspect.Exists() || state == null
                || state.WeaponDisabledByInjury)
                return;
            try
            {
                suspect.CanSwitchWeapons = false;
                Function.Call(Hash.SET_CURRENT_PED_WEAPON,
                    suspect, WeaponHash.Unarmed, true);
                state.WeaponDisabledByInjury = true;
                LogRuntime("POLICE_DISPATCH_SUSPECT_WEAPON_DISABLED_AFTER_WOUND",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                    + "; CurrentWeapon=Unarmed; InventoryRemoved=false");
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SUSPECT_WEAPON_DISABLE_FAILED", ex);
            }
        }

        private static int ReadDispatchLastDamageBone(Ped suspect)
        {
            int bone = 0;
            try
            {
                using (OutputArgument output = new OutputArgument())
                {
                    if (Function.Call<bool>(
                        Hash.GET_PED_LAST_DAMAGE_BONE, suspect, output))
                        bone = output.GetResult<int>();
                }
            }
            catch { }
            return bone;
        }

        private static void ClearDispatchLastDamageBone(Ped suspect)
        {
            try { Function.Call(Hash.CLEAR_PED_LAST_DAMAGE_BONE, suspect); }
            catch { }
        }

        private static ControlledDamageKind ReadDispatchDamageKind(
            Ped suspect,
            int damageWeaponHash,
            bool damageRecordAvailable)
        {
            if (suspect == null || !suspect.Exists())
                return ControlledDamageKind.Unknown;
            try
            {
                if (damageRecordAvailable && damageWeaponHash != 0)
                {
                    WeaponGroup group = Function.Call<WeaponGroup>(
                        Hash.GET_WEAPONTYPE_GROUP, unchecked((uint)damageWeaponHash));
                    return group == WeaponGroup.Melee || group == WeaponGroup.Unarmed
                        ? ControlledDamageKind.Melee
                        : ControlledDamageKind.Weapon;
                }
                if (suspect.HasBeenDamagedByAnyMeleeWeapon())
                    return ControlledDamageKind.Melee;
                if (suspect.HasBeenDamagedByAnyWeapon())
                    return ControlledDamageKind.Weapon;
            }
            catch { }
            return ControlledDamageKind.Unknown;
        }

        private static bool TryReadDispatchDamageRecord(
            Ped suspect,
            out int weaponHash,
            out int attackerHandle,
            out int gameTime)
        {
            weaponHash = 0;
            attackerHandle = 0;
            gameTime = 0;
            if (suspect == null || !suspect.Exists())
                return false;
            try
            {
                EntityDamageRecord[] records = suspect.DamageRecords.GetAllDamageRecords();
                if (records == null || records.Length == 0)
                    return false;
                EntityDamageRecord latest = records[0];
                for (int i = 1; i < records.Length; i++)
                    if (records[i].GameTime >= latest.GameTime)
                        latest = records[i];
                weaponHash = (int)latest.WeaponHash;
                attackerHandle = latest.Attacker == null ? 0 : latest.Attacker.Handle;
                gameTime = latest.GameTime;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsDispatchMeleeRecoveryPending(Ped suspect, DateTime now)
        {
            LSPDSuspectInjuryState state;
            if (suspect == null || !suspect.Exists()
                || !_suspectInjuries.TryGetValue(suspect.Handle, out state)
                || !state.MeleeReactionPending)
                return false;
            if (now < state.MeleeReactionUntil
                || now < state.NextMeleeRecoveryCheckAt)
                return true;

            state.NextMeleeRecoveryCheckAt = now.AddMilliseconds(200);
            bool ragdoll = false;
            try { ragdoll = Function.Call<bool>(Hash.IS_PED_RAGDOLL, suspect); }
            catch { }
            if (ragdoll)
                return true;

            state.MeleeReactionPending = false;
            state.MeleeReactionUntil = DateTime.MinValue;
            LogRuntime("POLICE_DISPATCH_SUSPECT_MELEE_RECOVERED",
                "Incident=" + state.OccurrenceId
                + "; Ped=" + suspect.Handle
                + "; RestoredState=" + (_incident == null ? "Inactive" : _incident.State.ToString())
                + "; Wounded=" + state.Wounded
                + "; ArrestActionAvailable=" + (state.Wounded || IsCompliant(_incident, suspect))
                + "; SamePed=true; SceneReset=false");
            return false;
        }

        private static DispatchHitZone ClassifyDispatchHitZone(int bone)
        {
            switch (bone)
            {
                case 31086:
                case 39317:
                case 65068:
                    return DispatchHitZone.Head;
                case 64729:
                case 10706:
                case 24817:
                case 24818:
                    return DispatchHitZone.Chest;
                case 11816:
                case 57597:
                case 23553:
                case 24816:
                    return DispatchHitZone.Stomach;
                case 45509:
                case 40269:
                case 61163:
                case 43810:
                case 28252:
                case 18905:
                case 57005:
                case 58271:
                case 51826:
                case 63931:
                case 36864:
                case 14201:
                case 52301:
                case 2108:
                case 20781:
                    return DispatchHitZone.Limb;
                default:
                    return DispatchHitZone.Unknown;
            }
        }

        private void RestoreSuspectInjuryState(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return;
            LSPDSuspectInjuryState state;
            if (!_suspectInjuries.TryGetValue(suspect.Handle, out state))
                return;
            try
            {
                suspect.MaxHealth = Math.Max(1, state.OriginalMaximumHealth);
                bool custodyOwned = IsPedPhysicallyCuffed(suspect)
                    || IsArrested(_incident, suspect);
                if (!state.Fatal && !suspect.IsDead && suspect.Health > 0)
                {
                    int restoredHealth = Math.Max(1,
                        state.OriginalHealth - state.DamageTaken);
                    if (custodyOwned && state.Wounded)
                        restoredHealth = Math.Max(restoredHealth,
                            _injurySettings.ArrestableWoundedHealthFloor);
                    suspect.Health = Math.Min(state.OriginalMaximumHealth,
                        restoredHealth);
                }
                suspect.CanSufferCriticalHits = state.OriginalCanSufferCriticalHits;
                if (custodyOwned)
                    suspect.CanSwitchWeapons = false;
                else
                {
                    suspect.CanSwitchWeapons = state.OriginalCanSwitchWeapons;
                    if (state.WeaponDisabledByInjury && !suspect.IsDead
                        && suspect.Health > 0
                        && state.OriginalSelectedWeaponHash != 0)
                        Function.Call(Hash.SET_CURRENT_PED_WEAPON,
                            suspect, state.OriginalSelectedWeaponHash, true);
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SUSPECT_INJURY_RESTORE_FAILED", ex);
            }
            _suspectInjuries.Remove(suspect.Handle);
        }

        private void RestoreAllSuspectInjuryStates()
        {
            foreach (LSPDSuspectInjuryState state in _suspectInjuries.Values.ToArray())
                if (state != null)
                    RestoreSuspectInjuryState(state.Actor);
            _suspectInjuries.Clear();
        }

        private static bool IsArrested(LSPDDispatchEvent incident, Ped suspect)
        {
            return incident != null && suspect != null && incident.ArrestedSuspects != null
                && incident.ArrestedSuspects.Any(value => value != null && value.Exists()
                    && value.Handle == suspect.Handle);
        }

        private static bool HasUnfinishedArrestedCustody(LSPDDispatchEvent incident)
        {
            return incident != null
                && incident.ArrestSecured
                && !incident.HasConvoyCustodyHandoff
                && incident.ArrestedSuspects != null
                && incident.ArrestedSuspects.Count > 0;
        }

        private static bool HasDeadArrestedSuspect(LSPDDispatchEvent incident)
        {
            return incident != null
                && incident.ArrestedSuspects != null
                && incident.ArrestedSuspects.Any(ped => ped != null && ped.Exists()
                    && (ped.IsDead || ped.Health <= 0));
        }

        private string FailUnfinishedArrestedCustody(LSPDDispatchEvent incident)
        {
            if (HasDeadArrestedSuspect(incident))
            {
                const string deathReason =
                    "A suspect died while in Police custody. The Dispatch assignment has ended; no transport will be recorded.";
                Fail(deathReason, "lsimmersivelife.police.dispatch.closed");
                return "A suspect died in Police custody. Dispatch has ended.";
            }

            const string lostReason =
                "An arrested suspect was lost before prisoner transport completed.";
            Fail(lostReason);
            return "Prisoner custody failed because the arrested suspect was lost before transport completed.";
        }

        private static void AddCompliantSuspect(LSPDDispatchEvent incident, Ped suspect)
        {
            if (incident == null || suspect == null)
                return;
            incident.SuspectResponses[suspect.Handle] = "comply";
            incident.PursuitContainedSuspectHandles.Add(suspect.Handle);
            if (incident.CompliantSuspects == null
                || IsCompliant(incident, suspect))
                return;
            incident.CompliantSuspects.Add(suspect);
        }

        private static void MarkSuspectFleeing(LSPDDispatchEvent incident, Ped suspect)
        {
            if (incident == null || suspect == null || !suspect.Exists() || suspect.IsDead)
                return;
            incident.SuspectResponses[suspect.Handle] = "flee";
        }

        private static void AddArrestedSuspect(LSPDDispatchEvent incident, Ped suspect)
        {
            if (incident == null || suspect == null || incident.ArrestedSuspects == null
                || IsArrested(incident, suspect))
                return;
            incident.ArrestedSuspects.Add(suspect);
        }

        private void SetStateForUnresolvedSuspects(
            LSPDDispatchState fallback)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null)
            {
                SetState(fallback);
                return;
            }

            if (incident.CurrentPursuitTarget != null)
            {
                SetState(LSPDDispatchState.SuspectFleeing);
                return;
            }

            List<Ped> unresolved = LiveSuspects(incident)
                .Where(suspect => !IsArrested(incident, suspect))
                .ToList();
            if (unresolved.Any(suspect => IsSuspectResponse(
                incident, suspect, "resist")))
            {
                SetState(LSPDDispatchState.SuspectResisting);
                return;
            }
            if (unresolved.Any(suspect => IsCompliant(incident, suspect)))
            {
                SetState(LSPDDispatchState.SuspectCompliant);
                return;
            }
            SetState(fallback);
        }

        private static bool IsSuspectResponse(
            LSPDDispatchEvent incident,
            Ped suspect,
            string response)
        {
            if (incident == null || suspect == null
                || string.IsNullOrWhiteSpace(response))
                return false;
            string actual;
            return incident.SuspectResponses.TryGetValue(suspect.Handle, out actual)
                && string.Equals(actual, response, StringComparison.OrdinalIgnoreCase);
        }

        internal IList<LSPDSuspectInjuryState> CreateCustodyInjuryHandoff()
        {
            List<LSPDSuspectInjuryState> states = new List<LSPDSuspectInjuryState>();
            foreach (Ped prisoner in CurrentArrestedSuspects)
            {
                if (prisoner == null || !prisoner.Exists() || prisoner.IsDead
                    || prisoner.Health <= 0)
                    continue;
                CaptureDispatchSuspectInjuryState(prisoner, _incident);
                MaintainDispatchSuspectInjury(
                    prisoner, DateTime.UtcNow, Game.Player.Character);
                LSPDSuspectInjuryState state;
                if (!_suspectInjuries.TryGetValue(prisoner.Handle, out state)
                    || state == null || state.Fatal
                    || prisoner.IsDead || prisoner.Health <= 0)
                    return new List<LSPDSuspectInjuryState>();
                states.Add(state);
            }
            return states;
        }

        internal bool TransferCustodyOwnershipToConvoy(
            IList<LSPDSuspectInjuryState> adoptedInjuryStates)
        {
            List<Ped> prisoners = CurrentArrestedSuspects.ToList();
            if (_incident == null || prisoners.Count == 0)
                return false;
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
            {
                // Custody transfer is a one-way ownership boundary. Repeated
                // R/handbrake input or a second Core pass must not move the
                // Dispatch state back and forth or emit another handoff.
                LogDebug(
                    "POLICE_CUSTODY_HANDOFF_DUPLICATE_IGNORED",
                    "Dispatch already transferred custody; Prisoners="
                    + string.Join(",", prisoners.Select(ped => ped.Handle.ToString()).ToArray()));
                return true;
            }
            if (adoptedInjuryStates == null
                || prisoners.Any(prisoner => !adoptedInjuryStates.Any(state =>
                    state != null && state.Actor != null && state.Actor.Exists()
                    && state.Actor.Handle == prisoner.Handle)))
                return false;

            _incident.OwnedByDispatch = false;
            _incident.SurrenderRequested = false;
            _incident.HasConvoyCustodyHandoff = true;
            foreach (LSPDSuspectInjuryState state in adoptedInjuryStates)
            {
                if (state == null || state.Actor == null || !state.Actor.Exists())
                    continue;
                LSPDSuspectInjuryState tracked;
                if (_suspectInjuries.TryGetValue(state.Actor.Handle, out tracked)
                    && object.ReferenceEquals(tracked, state))
                {
                    state.RuntimeOwner = "Convoy";
                    _suspectInjuries.Remove(state.Actor.Handle);
                    LogRuntime("POLICE_DISPATCH_INJURY_OWNERSHIP_TRANSFERRED",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + state.Actor.Handle
                        + "; From=Dispatch; To=Convoy"
                        + "; Health=" + state.Actor.Health
                        + "; MaximumHealth=" + state.Actor.MaxHealth
                        + "; CumulativeDamage=" + state.DamageTaken
                        + "; LastDamageKind=" + (state.LastDamageKind ?? "Unknown")
                        + "; Cuffed=" + IsPedPhysicallyCuffed(state.Actor)
                        + "; DispatchRestoreSuppressed=true");
                }
            }
            // Convoy is now the sole owner of the prisoner, transport, and
            // custody markers. Leaving the Dispatch scene/suspect markers in
            // place created duplicate blue targets when the station handoff
            // immediately opened the prison-transfer phase.
            CleanupBlip(_sceneBlip);
            CleanupSuspectBlips();
            _sceneBlip = null;
            LogRuntime(
                "POLICE_DISPATCH_CUSTODY_MARKERS_RELEASED",
                "Dispatch markers released to Convoy ownership; Prisoners="
                + string.Join(",", prisoners.Select(ped => ped.Handle.ToString()).ToArray()));
            LogRuntime("POLICE_CUSTODY_HANDOFF", "Dispatch -> Convoy | Prisoners="
                + string.Join(",", prisoners.Select(ped => ped.Handle.ToString()).ToArray()));
            return true;
        }

        /// <summary>
        /// Convoy reports its own physical failure. When the arrested person is
        /// still valid, return ownership to Dispatch so the player can retry
        /// transport instead of losing the whole case or cancelling later calls.
        /// </summary>
        internal bool RecoverCustodyFromConvoy(Ped prisoner, string reason)
        {
            return RecoverCustodyFromConvoy(
                prisoner == null ? Enumerable.Empty<Ped>() : new[] { prisoner },
                reason);
        }

        internal bool RecoverCustodyFromConvoy(
            IEnumerable<Ped> prisoners,
            string reason,
            IEnumerable<LSPDSuspectInjuryState> convoyInjuryStates = null)
        {
            List<Ped> valid = prisoners == null
                ? new List<Ped>()
                : prisoners.Where(ped => ped != null && ped.Exists() && !ped.IsDead).ToList();
            if (_incident == null || valid.Count == 0)
                return false;
            try
            {
                Dictionary<int, LSPDSuspectInjuryState> returnedInjuryStates =
                    convoyInjuryStates == null
                        ? new Dictionary<int, LSPDSuspectInjuryState>()
                        : convoyInjuryStates.Where(state => state != null
                                && state.Actor != null && state.Actor.Exists())
                            .GroupBy(state => state.Actor.Handle)
                            .ToDictionary(group => group.Key, group => group.First());
                List<Ped> retainedDead = TrackedSuspects(_incident)
                    .Where(ped => ped != null && ped.Exists() && ped.IsDead)
                    .ToList();
                _incident.Suspect = valid[0];
                _incident.Suspects.Clear();
                _incident.Suspects.AddRange(valid);
                foreach (Ped dead in retainedDead)
                    if (!valid.Any(ped => ped.Handle == dead.Handle))
                        _incident.Suspects.Add(dead);
                _incident.ArrestedSuspects.Clear();
                _incident.ArrestedSuspects.AddRange(valid);
                _incident.CompliantSuspects.Clear();
                _incident.OwnedByDispatch = true;
                _incident.HasConvoyCustodyHandoff = false;
                _incident.ArrestSecured = true;
                foreach (Ped prisoner in valid)
                {
                    prisoner.IsPersistent = true;
                    prisoner.BlockPermanentEvents = true;
                    prisoner.CanSwitchWeapons = false;
                    Function.Call(Hash.SET_PED_CONFIG_FLAG,
                        prisoner, HandcuffedPedConfigFlag, true);
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, prisoner, true);
                    LSPDSuspectInjuryState injuryState;
                    if (returnedInjuryStates.TryGetValue(prisoner.Handle, out injuryState)
                        && !injuryState.Fatal && prisoner.Health > 0)
                    {
                        injuryState.Actor = prisoner;
                        injuryState.RuntimeOwner = "Dispatch";
                        injuryState.LastHealth = Math.Max(1, prisoner.Health);
                        injuryState.LastConvoyHealthCheckAt = DateTime.MinValue;
                        injuryState.ConvoyTerminalLogged = false;
                        prisoner.MaxHealth = Math.Max(
                            injuryState.ManagedMaximumHealth, prisoner.MaxHealth);
                        prisoner.CanSufferCriticalHits = false;
                        _suspectInjuries[prisoner.Handle] = injuryState;
                        if (injuryState.Wounded
                            && prisoner.Health < injuryState.ArrestableWoundedHealthFloor)
                            prisoner.Health = injuryState.ArrestableWoundedHealthFloor;
                        injuryState.LastHealth = Math.Max(1, prisoner.Health);
                        LogRuntime("POLICE_CUSTODY_INJURY_OWNERSHIP_RETURNED",
                            "Incident=" + injuryState.OccurrenceId
                            + "; Ped=" + prisoner.Handle
                            + "; From=Convoy; To=Dispatch"
                            + "; Health=" + prisoner.Health
                            + "; MaximumHealth=" + prisoner.MaxHealth
                            + "; Cuffed=" + IsPedPhysicallyCuffed(prisoner)
                            + "; CumulativeDamage=" + injuryState.DamageTaken
                            + "; LastDamageKind=" + (injuryState.LastDamageKind ?? "Unknown"));
                    }
                }
                SetState(LSPDDispatchState.Arrested);
                SetSuspectBlip(valid[0]);
                CaptureAndSetWaypoint(valid[0].Position);
                Notify("~y~PRISONER CUSTODY~s~\nTransport was interrupted. The arrested suspect group remains secured; request transport again when ready.");
                if (retainedDead.Count > 0)
                    LogRuntime(
                        "POLICE_CUSTODY_DEAD_PRISONER_RETAINED",
                        "Dead aftermath retained with Dispatch while living prisoners were recovered. Peds="
                        + string.Join(",", retainedDead.Select(ped => ped.Handle.ToString()).ToArray()));
                LogRuntime(
                    "POLICE_CUSTODY_RETURNED_TO_DISPATCH",
                    "Prisoners=" + string.Join(",", valid.Select(ped => ped.Handle.ToString()).ToArray())
                    + "; Reason=" + (reason ?? string.Empty));
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CUSTODY_RECOVERY_FAILED", ex);
                return false;
            }
        }

        internal void SetAwaitingTransport()
        {
            if (_incident != null)
                SetState(LSPDDispatchState.AwaitingTransport);
        }

        internal void SetConvoyState(LSPDDispatchState state)
        {
            if (_incident == null || state == LSPDDispatchState.None)
                return;
            SetState(state);
        }

        internal string CompleteTransport()
        {
            if (_incident == null)
                return "No active Police dispatch remains.";
            if (!_incident.HasConvoyCustodyHandoff)
                return "No active prisoner convoy is linked to this Dispatch case.";
            Complete("Prisoner delivered and booking completed.");
            return "Prisoner delivered and booking completed. Dispatch completed.";
        }

        internal string CompleteTransportCrewHandoff()
        {
            if (_incident == null)
                return "No active Police dispatch remains.";
            if (!_incident.HasConvoyCustodyHandoff)
                return "No active prisoner convoy is linked to this Dispatch case.";
            Complete("Station transport was declined. The loaded suspects remain in transport-officer custody as the crew departs.");
            return "Station transport declined. Transport crew has custody; Player Dispatch completed.";
        }

        internal string Cancel(string reason)
        {
            if (_incident == null)
                return "No active dispatch.";

            LSPDDispatchEvent incident = _incident;
            Terminate(
                incident,
                LSPDDispatchState.Cancelled,
                reason,
                "lsimmersivelife.police.dispatch.cancelled",
                "cancelled",
                true);
            LogRuntime("POLICE_DISPATCH_CANCELLED", incident.Title + " | Reason=" + reason);
            return "Dispatch cancelled and Police-owned scene state cleaned.";
        }

        internal void Reset()
        {
            ClearBackupArrestOperations(_incident);
            ClearPlayerHandcuffState();
            RestoreAllSuspectInjuryStates();
            CancelPendingOffer("Dispatch was reset.");
            ResetPendingSceneResolution();
            if (_incident != null)
            {
                CloseActiveAudio(_incident);
                CleanupIncident(_incident, false);
                _incident = null;
            }
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
                DeleteDeferred(item);
            _deferredCleanup.Clear();
            ReleaseUnusedDispatchInteriorPins("DispatchAuthorityReset");
            CleanupBlipsAndWaypoint();
            _cooldownUntil = DateTime.MinValue;
            _nextOfferAt = DateTime.MinValue;
            _manualRequestRetryAt = DateTime.MinValue;
            _nextMaintenance = DateTime.MinValue;
            _nextInteriorPinMaintenanceAt = DateTime.MinValue;
            _playerHandcuffHoldUntil = DateTime.MinValue;
            _playerHandcuffAttempts = 0;
            _patrolSessionStarted = false;
            _acceptKeyDown = false;
            _rejectKeyDown = false;
            _investigateKeyDown = false;
            _secureKeyDown = false;
            _lastCompliantTaskAt.Clear();
            _lastSecuredPoseTaskAt.Clear();
        }

        private void ResetPendingSceneResolution(
            AmbientDispatchSceneResolution preserveScene = null)
        {
            _pendingSceneResolutionDefinitionIds.Clear();
            _pendingSceneResolutionRetryCount = 0;
            _pendingSceneResolutionStartedAt = DateTime.MinValue;
            _manualRequestRetryAt = DateTime.MinValue;
            ReleaseUnusedDispatchInteriorPins("SceneResolutionReset", preserveScene);
        }

        private void ReleaseUnusedDispatchInteriorPins(
            string reason,
            AmbientDispatchSceneResolution preserveScene = null)
        {
            if (_ambientWorld == null)
                return;

            var retainedInteriorIds = new HashSet<int>();
            AddDispatchSceneInteriorId(retainedInteriorIds, preserveScene);
            if (_incident != null)
                AddDispatchSceneInteriorId(retainedInteriorIds, _incident.SceneResolution);
            if (_pendingOffer != null)
                AddDispatchSceneInteriorId(retainedInteriorIds, _pendingOffer.SceneResolution);
            foreach (DeferredCleanup item in _deferredCleanup)
                if (item != null && item.LiveInteriorId != 0)
                    retainedInteriorIds.Add(item.LiveInteriorId);

            _ambientWorld.ReleaseDispatchInteriorPinsExcept(retainedInteriorIds, reason);
        }

        private static void AddDispatchSceneInteriorId(
            ISet<int> retainedInteriorIds,
            AmbientDispatchSceneResolution scene)
        {
            if (retainedInteriorIds != null && scene != null
                && scene.IsInteriorScene && scene.LiveInteriorId != 0)
                retainedInteriorIds.Add(scene.LiveInteriorId);
        }

        private bool MaintainStoppedPursuitVehicle(
            LSPDDispatchEvent incident,
            Ped suspect,
            Ped player,
            DateTime now)
        {
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || suspect.Health <= 0)
                return false;
            Vehicle vehicle = incident.VehicleForSuspect(suspect);
            if (vehicle == null || !vehicle.Exists())
                return false;

            int pedHandle = suspect.Handle;
            DateTime stoppedAt;
            bool hasStoppedTimestamp = incident.PursuitVehicleStoppedAt.TryGetValue(
                pedHandle, out stoppedAt);
            bool exitRequested = incident.PursuitVehicleExitRequestedHandles.Contains(
                pedHandle);

            if (!suspect.IsInVehicle())
            {
                if (exitRequested)
                {
                    incident.PursuitVehicleExitRequestedHandles.Remove(pedHandle);
                    incident.PursuitVehicleExitTaskAt.Remove(pedHandle);
                    LogRuntime("POLICE_DISPATCH_PURSUIT_DRIVER_EXITED_STOPPED_VEHICLE",
                        "Incident=" + incident.Id + "; Ped=" + pedHandle
                        + "; Vehicle=" + vehicle.Handle
                        + "; PlayerDistance=" + (player == null || !player.Exists()
                            ? "unknown" : player.Position.DistanceTo(suspect.Position)
                                .ToString("0.0", CultureInfo.InvariantCulture)));
                    bool authoredVehicleStopResponse = IsVehicleEntryActivity(incident.VehicleActivity)
                        && incident.SuspectResponseBranches != null
                        && incident.SuspectResponseBranches.Count > 0;
                    if (!authoredVehicleStopResponse
                        && player != null && player.Exists()
                        && player.Position.DistanceTo(suspect.Position) <= ArrestRadius + 4f
                        && !IsDownedOrInjured(suspect)
                        && !IsCompliant(incident, suspect))
                    {
                        OrderSuspectToComply(suspect, player, false);
                        return true;
                    }
                }

                if (!incident.VictimVehicleRecovered
                    && incident.VictimVehicle != null
                    && incident.VictimVehicle.Exists()
                    && incident.VictimVehicle.Handle == vehicle.Handle
                    && hasStoppedTimestamp
                    && Math.Abs(vehicle.Speed) <= 1.25f
                    && !TrackedSuspects(incident).Any(member =>
                        member != null && member.Exists() && !member.IsDead
                        && member.IsInVehicle() && member.CurrentVehicle != null
                        && member.CurrentVehicle.Exists()
                        && member.CurrentVehicle.Handle == vehicle.Handle))
                {
                    incident.VictimVehicleRecovered = true;
                    LogRuntime("POLICE_DISPATCH_CARJACKED_VEHICLE_RECOVERED",
                        "Occurrence=" + incident.Id
                        + "; Victim=" + (incident.Victim == null ? 0 : incident.Victim.Handle)
                        + "; Vehicle=" + vehicle.Handle
                        + "; Speed=" + vehicle.Speed.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; SuspectOccupants=0; PhysicalStopConfirmed=true");
                }

                if (hasStoppedTimestamp && now < stoppedAt.AddSeconds(8))
                    return true;
                incident.PursuitVehicleStoppedAt.Remove(pedHandle);
                incident.PursuitVehicleExitTaskAt.Remove(pedHandle);
                return false;
            }

            Vehicle currentVehicle = suspect.CurrentVehicle;
            if (currentVehicle == null || !currentVehicle.Exists()
                || currentVehicle.Handle != vehicle.Handle)
                return false;

            float speed = Math.Abs(vehicle.Speed);
            if (!exitRequested && speed > 1.25f)
            {
                incident.PursuitVehicleStoppedAt.Remove(pedHandle);
                incident.PursuitVehicleExitTaskAt.Remove(pedHandle);
                return false;
            }

            if (!hasStoppedTimestamp)
            {
                stoppedAt = now;
                incident.PursuitVehicleStoppedAt[pedHandle] = stoppedAt;
                hasStoppedTimestamp = true;
            }
            if (now < stoppedAt.AddMilliseconds(1200))
                return true;

            if (!exitRequested)
            {
                int baselineHealth;
                if (!incident.PursuitVehicleHealthAtStart.TryGetValue(
                        vehicle.Handle, out baselineHealth))
                {
                    baselineHealth = vehicle.Health;
                    incident.PursuitVehicleHealthAtStart[vehicle.Handle] = baselineHealth;
                }
                bool collisionDamaged = baselineHealth - vehicle.Health >= 100;
                bool undriveable = !vehicle.IsDriveable;
                float playerDistance = player == null || !player.Exists()
                    ? float.MaxValue : player.Position.DistanceTo(vehicle.Position);
                bool playerVehicleBlock = false;
                try
                {
                    Vehicle playerVehicle = player == null || !player.Exists()
                        ? null : player.CurrentVehicle;
                    playerVehicleBlock = playerVehicle != null && playerVehicle.Exists()
                        && playerVehicle.Position.DistanceTo(vehicle.Position) <= 16f
                        && Math.Abs(playerVehicle.Speed) <= 5f;
                }
                catch { }
                bool playerFootBlock = player != null && player.Exists()
                    && !player.IsInVehicle()
                    && playerDistance <= ArrestRadius + 1f;
                if (!collisionDamaged && !undriveable
                    && !playerVehicleBlock && !playerFootBlock)
                {
                    incident.PursuitVehicleStoppedAt.Remove(pedHandle);
                    return false;
                }

                incident.PursuitVehicleExitRequestedHandles.Add(pedHandle);
                LogRuntime("POLICE_DISPATCH_PURSUIT_STOPPED_VEHICLE_EXIT_REQUESTED",
                    "Incident=" + incident.Id + "; Ped=" + pedHandle
                    + "; Vehicle=" + vehicle.Handle
                    + "; Speed=" + speed.ToString("0.0")
                    + "; HealthBaseline=" + baselineHealth
                    + "; HealthNow=" + vehicle.Health
                    + "; CollisionDamaged=" + collisionDamaged
                    + "; Undriveable=" + undriveable
                    + "; PlayerVehicleBlock=" + playerVehicleBlock
                    + "; PlayerFootBlock=" + playerFootBlock);
                exitRequested = true;
            }

            DateTime nextExitTaskAt;
            if (!incident.PursuitVehicleExitTaskAt.TryGetValue(
                    pedHandle, out nextExitTaskAt) || now >= nextExitTaskAt)
            {
                try
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, suspect, vehicle, 0);
                    incident.PursuitVehicleExitTaskAt[pedHandle] = now.AddSeconds(3);
                    LogRuntime("POLICE_DISPATCH_PURSUIT_STOPPED_VEHICLE_EXIT_TASK",
                        "Incident=" + incident.Id + "; Ped=" + pedHandle
                        + "; Vehicle=" + vehicle.Handle + "; RetrySeconds=3");
                }
                catch (Exception ex)
                {
                    incident.PursuitVehicleExitTaskAt[pedHandle] = now.AddSeconds(3);
                    LogException("POLICE_DISPATCH_PURSUIT_STOPPED_VEHICLE_EXIT_FAILED", ex);
                }
            }
            return true;
        }

        private void MaintainIncident(DateTime now, Ped player)
        {
            if (_incident == null)
                return;

            // Locally produced offers have already passed scene preflight, but
            // their behavior remains dormant until acceptance. Offers supplied
            // by another producer stay informational until that owner accepts.
            if (_incident.State == LSPDDispatchState.Offered)
                return;

            // GTA can clear the visible cuff/AI flags after an unrelated
            // collision or ambient task. Reassert them at the existing
            // maintenance cadence without issuing a new movement task; the
            // transport owner remains free to run the physical loading flow.
            MaintainPlayerHandcuff(now, player);
            MaintainBackupArrests(now);
            if (_incident == null)
                return;

            // Convoy owns both the prisoner tasks and the transport entity
            // after Dispatch has explicitly handed custody over. In particular,
            // Dispatch must not keep reissuing robbery or pursuit tasks against
            // a handcuffed prisoner while the player is driving to station.
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
                return;

            MaintainSecuredSuspects(now);
            MaintainCompliantSuspects(now);

            if (!_incident.ScenePreparationCompleted)
            {
                if (!EnsureScene(player))
                {
                    if (_incident.ScenePreparationRequested
                        && _incident.ScenePreparationDeadline != DateTime.MinValue
                        && now >= _incident.ScenePreparationDeadline)
                    {
                        Fail("The Dispatch scene could not finish loading safely.");
                    }
                    return;
                }
            }

            // Settle a physically observed terminal scene from the maintenance
            // loop as soon as its final living suspect is gone. Previously the
            // case could remain active until the Player reopened the menu and
            // issued another arrest/investigate command against a corpse.
            if (_incident.ScenePreparationCompleted
                && !LiveSuspects(_incident).Any()
                && IsPlayerAtResolvedDispatchScene(_incident, player))
            {
                if (HasPendingVictimInvestigation
                    && !TryRecordMurderVictimInvestigation(_incident, player))
                    return;
                if (HasUnfinishedArrestedCustody(_incident))
                {
                    FailUnfinishedArrestedCustody(_incident);
                    return;
                }

                string completion = CompletionMessage(_incident, true);
                LogRuntime("POLICE_DISPATCH_DEATH_SCENE_SETTLED",
                    "Incident=" + _incident.Id
                    + "; Suspects=" + TrackedSuspects(_incident).Count()
                    + "; Player=" + (player == null ? 0 : player.Handle)
                    + "; Reason=NO_LIVING_SUSPECTS_AT_OBSERVED_SCENE");
                Complete(completion);
                return;
            }

            // The accepted Dispatch scene is a live world activity during
            // travel. Previously EnsureScene was reached only through the
            // manual Investigate command, leaving the suspect frozen until
            // the officer reopened Police UI at the destination. Start the
            // authored behavior once, after the owned entities exist; the
            // state guard inside StartSceneBehavior prevents task spam.
            if (_incident.ScenePreparationCompleted
                && !_incident.SceneBehaviorInitialized
                && !TrackedSuspects(_incident).Any(value =>
                    IsDispatchMeleeRecoveryPending(value, now)
                    || IsWoundedSuspect(value))
                && (!_incident.PreArrivalSceneBehaviorInitialized
                    || IsPlayerNearDispatchOrigin(player)))
            {
                StartSceneBehavior(player);
                // StartSceneBehavior can fail and terminate an invalid scene
                // (for example, a paired event with fewer than two living
                // suspects). Terminate clears _incident, so stop this tick
                // before reading its suspect or state again.
                if (_incident == null || !_incident.OwnedByDispatch)
                    return;
            }

            ReportSceneApproach(player);
            MaintainEnRouteStatus(now);
            MaintainPursuitWaypoint(now, player);
            if (_incident != null && _incident.SceneBehaviorInitialized
                && _incident.SuspectResponseBranches != null
                && _incident.SuspectResponseBranches.Count > 0
                && (!_incident.PlayerEncounterResponseStarted
                    || IsVehicleEntryActivity(_incident.VehicleActivity))
                && !TrackedSuspects(_incident).Any(value =>
                    IsDispatchMeleeRecoveryPending(value, now))
                && (IsVehicleEntryActivity(_incident.VehicleActivity)
                    ? IsPlayerNearVehicleEncounter(_incident, player)
                    : IsPlayerNearDispatchOrigin(player)))
                StartPlayerEncounterResponse(player);
            MaintainInteriorStoreUtilityParking(player);

            Ped suspect = _incident.Suspect;
            if (suspect != null && suspect.Exists())
            {
                bool meleeReactionPending = IsDispatchMeleeRecoveryPending(suspect, now);
                bool wounded = IsWoundedSuspect(suspect);
                if (!meleeReactionPending && !wounded)
                {
                    TryStartInteriorStoreRobberyFlight(player);
                    MaintainStagedSceneBehavior(now);
                    MaintainVehicleSceneActivity(now, player);
                }
                ReportSuspectGunfire(player);
                List<Ped> unresolvedSuspects = LiveSuspects(_incident)
                    .Where(value => !IsArrested(_incident, value))
                    .ToList();
                bool visibleCompliance = unresolvedSuspects.Count > 0
                    && unresolvedSuspects.All(value => IsCompliant(_incident, value)
                        && !IsWoundedSuspect(value)
                        && !value.IsInCombatAgainst(player) && !value.IsShooting);
                if (_incident.SurrenderRequested && visibleCompliance &&
                    _incident.State != LSPDDispatchState.Arrested &&
                    _incident.State != LSPDDispatchState.AwaitingTransport &&
                    _incident.State != LSPDDispatchState.HoldingAtStation &&
                    _incident.State != LSPDDispatchState.PrisonTransfer &&
                    now >= _incident.SurrenderRequestedAt.AddSeconds(SurrenderSettleSeconds))
                {
                    SetStateForUnresolvedSuspects(
                        LSPDDispatchState.SuspectCompliant);
                    Notify("~g~POLICE COMPLIANCE~s~\nSuspect has put their hands up. Secure the suspect now.");
                    ReportAudioStage("lsimmersivelife.police.compliance.confirmed", _incident, "compliant");
                }

                bool interiorEscapeManaged = !meleeReactionPending && !wounded
                    && MaintainInteriorSuspectEscape(now, suspect, player);
                MaintainInteriorClerkSnatchVictimEgress(now, suspect, player);

                // Sample the selected flee branch, not just the legacy primary
                // suspect. This lets a group advance to its remaining runner
                // after another member complies or is wounded.
                bool interiorScene = _incident.SceneResolution != null
                    && _incident.SceneResolution.IsInteriorScene;
                Ped pursuitMovementTarget = interiorScene
                    ? suspect : _incident.CurrentPursuitTarget;
                if (pursuitMovementTarget != null
                    && IsSuspectResponse(_incident, pursuitMovementTarget, "flee")
                    && pursuitMovementTarget.Exists()
                    && !IsCompliant(_incident, pursuitMovementTarget)
                    && !IsWoundedSuspect(pursuitMovementTarget)
                    && !IsDispatchMeleeRecoveryPending(pursuitMovementTarget, now))
                {
                    if (now >= _incident.LastMovementSampleAt.AddSeconds(2))
                    {
                        bool targetChanged = _incident.LastMovementSampleTargetHandle
                            != pursuitMovementTarget.Handle;
                        if (targetChanged)
                        {
                            _incident.LastMovementSampleTargetHandle = pursuitMovementTarget.Handle;
                            _incident.LastSuspectPosition = pursuitMovementTarget.Position;
                            _incident.SuspectMovementConfirmed = false;
                        }
                        bool stoppedVehicleManaged = !interiorScene
                            && MaintainStoppedPursuitVehicle(
                                _incident, pursuitMovementTarget, player, now);
                        float moved = targetChanged ? 0f
                            : pursuitMovementTarget.Position.DistanceTo(
                                _incident.LastSuspectPosition);
                        if (!stoppedVehicleManaged && moved >= 3f)
                        {
                            if (!_incident.SuspectMovementConfirmed)
                            {
                                _incident.SuspectMovementConfirmed = true;
                                LogRuntime("POLICE_DISPATCH_PURSUIT_MOVEMENT_CONFIRMED",
                                    "Type=" + _incident.IncidentType
                                    + "; Ped=" + pursuitMovementTarget.Handle
                                    + "; Distance=" + moved.ToString("0.0", CultureInfo.InvariantCulture));
                            }
                        }
                        else if (!stoppedVehicleManaged && !targetChanged
                            && !_incident.SuspectMovementConfirmed
                            && !pursuitMovementTarget.IsDead
                            && !(pursuitMovementTarget.Handle == suspect.Handle
                                && interiorEscapeManaged))
                        {
                            try
                            {
                                Ped playerTarget = player != null && player.Exists()
                                    ? player : Game.Player.Character;
                                Vehicle assignedVehicle = _incident.VehicleForSuspect(
                                    pursuitMovementTarget);
                                if (assignedVehicle != null && assignedVehicle.Exists()
                                    && pursuitMovementTarget.IsInVehicle()
                                    && pursuitMovementTarget.CurrentVehicle != null
                                    && pursuitMovementTarget.CurrentVehicle.Exists()
                                    && pursuitMovementTarget.CurrentVehicle.Handle
                                        == assignedVehicle.Handle
                                    && playerTarget != null)
                                    pursuitMovementTarget.Task.VehicleChase(playerTarget);
                                else if (playerTarget != null)
                                    pursuitMovementTarget.Task.ReactAndFlee(playerTarget);
                            }
                            catch (Exception ex)
                            {
                                LogException("POLICE_DISPATCH_PURSUIT_REISSUE_FAILED", ex);
                            }
                        }
                        _incident.LastSuspectPosition = pursuitMovementTarget.Position;
                        _incident.LastMovementSampleAt = now;
                    }
                }
            }
        }

        private static bool IsMappedInteriorDispatchBehavior(string sceneBehavior)
        {
            return string.Equals(sceneBehavior, "interior_store_custody",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneBehavior, "interior_robbery_custody",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneBehavior, "interior_attack_attempt",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneBehavior, "interior_clerk_assault",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneBehavior, "interior_clerk_snatch",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneBehavior, "murder_victim_interior",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInteriorClerkSnatchBehavior(string sceneBehavior)
        {
            return string.Equals(sceneBehavior, "interior_clerk_snatch",
                StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPlayerInsideIncidentInterior(Ped player)
        {
            int playerInteriorId;
            return _incident != null
                && _incident.SceneResolution != null
                && _incident.SceneResolution.IsInteriorScene
                && (_incident.SceneResolution.IsMappedInteriorDispatchScene
                    || _incident.SceneResolution.IsInsideOnlyInteriorScene)
                && player != null && player.Exists() && !player.IsDead
                && _ambientWorld != null
                && _ambientWorld.TryGetDispatchPlayerInteriorId(player, out playerInteriorId)
                && playerInteriorId == _incident.SceneResolution.LiveInteriorId;
        }

        private void TryStartInteriorStoreRobberyFlight(Ped player)
        {
            if (_incident == null
                || _incident.State != LSPDDispatchState.Investigating
                || (!string.Equals(_incident.IncidentType, "store_robbery",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(_incident.IncidentType, "bank_robbery",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(_incident.SceneBehavior, "interior_attack_attempt",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(_incident.SceneBehavior, "interior_clerk_snatch",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(_incident.SceneBehavior, "murder_victim_interior",
                        StringComparison.OrdinalIgnoreCase))
                || _incident.SceneResolution == null
                || !_incident.SceneResolution.IsInteriorScene
                || player == null || !player.Exists() || player.IsDead || player.IsInVehicle())
                return;

            if (IsMappedInteriorDispatchBehavior(_incident.SceneBehavior))
            {
                TryStartInteriorStoreRobberyEscalation(player);
                return;
            }

            int playerInteriorId;
            if (_ambientWorld == null
                || !_ambientWorld.TryGetDispatchPlayerInteriorId(player, out playerInteriorId)
                || playerInteriorId != _incident.SceneResolution.LiveInteriorId)
                return;

            foreach (Ped suspect in LiveSuspects(_incident))
            {
                int suspectInteriorId;
                if (!_ambientWorld.TryGetActorInteriorId(suspect, out suspectInteriorId)
                    || suspectInteriorId != playerInteriorId
                    || player.Position.DistanceTo(suspect.Position) > SceneArrivalRadius)
                    continue;

                bool aimingAtSuspect;
                try
                {
                    aimingAtSuspect = Function.Call<bool>(
                        Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY,
                        Game.Player.Handle,
                        suspect);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_INTERIOR_AIM_CHECK_FAILED", ex);
                    return;
                }
                if (!aimingAtSuspect)
                    continue;

                MarkSuspectFleeing(_incident, suspect);
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectFleeing);
                OrderAdditionalSuspectsToFlee(player);
                Notify("~r~POLICE DISPATCH~s~\nThe suspect is fleeing through the mapped entrance.");
                ReportAudioStage(
                    "lsimmersivelife.police.pursuit.continuing",
                    _incident,
                    "interior-robbery-flight");
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_ROBBERY_FLIGHT_TRIGGERED",
                    "Type=" + _incident.IncidentType
                    + "; Player=" + player.Handle
                    + "; Suspect=" + suspect.Handle
                    + "; InteriorId=" + playerInteriorId
                    + "; Trigger=PlayerAimingAtSuspect");
                return;
            }
        }

        private void TryStartInteriorStoreRobberyEscalation(Ped player)
        {
            if (_incident == null
                || !_incident.OwnedByDispatch || _incident.HasConvoyCustodyHandoff
                || !_incident.SceneBehaviorInitialized
                || !IsMappedInteriorDispatchBehavior(_incident.SceneBehavior)
                || player == null || !player.Exists() || player.IsDead || player.IsInVehicle())
                return;

            int playerInteriorId;
            if (_ambientWorld == null
                || !_ambientWorld.TryGetDispatchPlayerInteriorId(player, out playerInteriorId)
                || _incident.SceneResolution == null
                || playerInteriorId != _incident.SceneResolution.LiveInteriorId)
                return;

            foreach (Ped suspect in LiveSuspects(_incident))
            {
                string selectedResponse;
                if (_incident.SuspectResponses.TryGetValue(suspect.Handle, out selectedResponse)
                    && string.Equals(selectedResponse, "resist", StringComparison.OrdinalIgnoreCase))
                    continue;
                int suspectInteriorId;
                if (!_ambientWorld.TryGetActorInteriorId(suspect, out suspectInteriorId)
                    || suspectInteriorId != playerInteriorId
                    || player.Position.DistanceTo(suspect.Position) > SceneArrivalRadius)
                    continue;

                bool aimingAtSuspect;
                try
                {
                    aimingAtSuspect = Function.Call<bool>(
                        Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY,
                        Game.Player.Handle,
                        suspect);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_INTERIOR_AIM_CHECK_FAILED", ex);
                    return;
                }
                if (!aimingAtSuspect)
                    continue;

                try
                {
                    suspect.Task.Combat(player);
                    _incident.SuspectEscalationTriggered = true;
                    _incident.SuspectResponses[suspect.Handle] = "resist";
                    SetStateForUnresolvedSuspects(
                        LSPDDispatchState.SuspectResisting);
                    ReportAudioStage(
                        "lsimmersivelife.police.resistance.armed",
                        _incident,
                        "interior-scene-escalation-" + suspect.Handle,
                        true);
                    Notify("~r~POLICE DISPATCH~s~\nThe armed suspect is engaging you inside the building.");
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_SUSPECT_ESCALATED",
                        "Incident=" + _incident.Id
                        + "; Player=" + player.Handle
                        + "; Suspect=" + suspect.Handle
                        + "; InteriorId=" + playerInteriorId
                        + "; Trigger=PlayerAimingAtSuspect");
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_INTERIOR_STORE_ESCALATION_FAILED", ex);
                }
                return;
            }
        }

        private void MaintainInteriorStoreUtilityParking(Ped player)
        {
            if (_incident == null
                || !IsMappedInteriorDispatchBehavior(_incident.SceneBehavior)
                || _incident.SceneResolution == null
                || !_incident.SceneResolution.HasVehicleStagingPosition
                || player == null || !player.Exists())
                return;

            if (!player.IsInVehicle())
                return;

            Vehicle vehicle = player.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists()
                || Math.Abs(vehicle.Speed) > 0.5f
                || vehicle.Position.DistanceTo(
                    _incident.SceneResolution.VehicleStagingPosition) > 35f)
                return;

            if (_incident.ParkedPlayerUtility != null
                && _incident.ParkedPlayerUtility.Exists()
                && _incident.ParkedPlayerUtility.Handle == vehicle.Handle)
                return;

            _incident.ParkedPlayerUtility = vehicle;
            LogRuntime(
                "POLICE_DISPATCH_INTERIOR_STORE_UTILITY_PARKED",
                "Incident=" + _incident.Id
                + "; Vehicle=" + vehicle.Handle
                + "; DistanceToApproach="
                + vehicle.Position.DistanceTo(
                    _incident.SceneResolution.VehicleStagingPosition).ToString("0.0", CultureInfo.InvariantCulture));
        }

        private void ReportSuspectGunfire(Ped player)
        {
            if (_incident == null || _incident.SuspectGunfireReported
                || !_incident.OwnedByDispatch || _incident.HasConvoyCustodyHandoff
                || player == null || !player.Exists())
                return;

            Ped shooter = LiveSuspects(_incident).FirstOrDefault(suspect => suspect.IsShooting);
            if (shooter == null)
                return;

            _incident.SuspectGunfireReported = true;
            if (_incident.State != LSPDDispatchState.Arrested
                && _incident.State != LSPDDispatchState.AwaitingTransport
                && _incident.State != LSPDDispatchState.HoldingAtStation
                && _incident.State != LSPDDispatchState.PrisonTransfer)
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectResisting);
            ReportAudioStage(
                "lsimmersivelife.police.emergency.shots_fired",
                _incident,
                "suspect-fired",
                true);
            Notify("~r~POLICE DISPATCH~s~\nShots fired. The suspect is still an active threat.");
            LogRuntime(
                "POLICE_DISPATCH_SUSPECT_GUNFIRE_REPORTED",
                "Incident=" + _incident.Id + "; Shooter=" + shooter.Handle
                + "; Player=" + player.Handle + "; ReportedOnce=true");
        }

        private bool MaintainInteriorSuspectEscape(DateTime now, Ped primarySuspect, Ped player)
        {
            if (_incident == null || _ambientWorld == null
                || _incident.SceneResolution == null
                || !_incident.SceneResolution.IsInteriorScene)
                return false;

            if (_incident.State != LSPDDispatchState.SuspectFleeing)
            {
                Ped pendingActor = TrackedSuspects(_incident).FirstOrDefault(value =>
                    value.Handle == _incident.InteriorExitActorHandle);
                if (_incident.InteriorExitActorHandle != 0)
                {
                    if (pendingActor != null && pendingActor.Exists())
                        _ambientWorld.CancelInteriorAccess(
                            pendingActor,
                            "DispatchPursuitStateChanged");
                    ResetInteriorExitAttempt(_incident);
                }
                return false;
            }

            Ped exitingSuspect = null;
            if (_incident.InteriorExitActorHandle != 0)
                exitingSuspect = LiveSuspects(_incident).FirstOrDefault(value =>
                    value.Handle == _incident.InteriorExitActorHandle);
            if (exitingSuspect == null)
            {
                ResetInteriorExitAttempt(_incident);
                foreach (Ped candidate in LiveSuspects(_incident))
                {
                    if (_incident.InteriorExitCompletedHandles.Contains(candidate.Handle)
                        || _incident.InteriorExitFailedHandles.Contains(candidate.Handle)
                        || IsWoundedSuspect(candidate))
                        continue;

                    int candidateInteriorId;
                    if (!_ambientWorld.TryGetActorInteriorId(candidate, out candidateInteriorId))
                        continue;
                    if (candidateInteriorId == 0)
                    {
                        _incident.InteriorExitCompletedHandles.Add(candidate.Handle);
                        LogRuntime(
                            "POLICE_DISPATCH_INTERIOR_EXIT_OBSERVED",
                            "Suspect=" + candidate.Handle
                            + "; InteriorId=0; Crossing=ObservedFromLiveEntityContext");
                        continue;
                    }
                    if (candidateInteriorId != _incident.SceneResolution.LiveInteriorId)
                        continue;

                    exitingSuspect = candidate;
                    _incident.InteriorExitActorHandle = candidate.Handle;
                    _incident.InteriorExitStartedAt = now;
                    _incident.InteriorExitRecoveryCount = 0;
                    break;
                }
            }

            if (exitingSuspect == null)
                return false;

            if (IsWoundedSuspect(exitingSuspect))
            {
                _ambientWorld.CancelInteriorAccess(exitingSuspect, "DispatchSuspectWounded");
                _incident.InteriorExitFailedHandles.Add(exitingSuspect.Handle);
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_EXIT_HELD_FOR_INJURY",
                    "Suspect=" + exitingSuspect.Handle
                    + "; State=InjuredOrDowned; InteriorId=" + _incident.SceneResolution.LiveInteriorId);
                ResetInteriorExitAttempt(_incident);
                return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
            }

            int liveInteriorId;
            if (!_ambientWorld.TryGetActorInteriorId(exitingSuspect, out liveInteriorId))
            {
                RetryInteriorSuspectExit(exitingSuspect, "GTA could not identify the suspect's live interior.");
                return true;
            }
            if (liveInteriorId == 0 && _incident.InteriorExitPhase < 3)
            {
                // The actor physically reached an exterior context before this
                // route session started (for example, through another live
                // doorway). Do not claim the mapped door was used.
                _incident.InteriorExitCompletedHandles.Add(exitingSuspect.Handle);
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_EXIT_OBSERVED",
                    "Suspect=" + exitingSuspect.Handle
                    + "; Crossing=ObservedOutsideWithoutMappedDoorSession");
                ResetInteriorExitAttempt(_incident);
                return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
            }

            if (_incident.InteriorExitStartedAt != DateTime.MinValue
                && now >= _incident.InteriorExitStartedAt.AddSeconds(InteriorExitNavigationTimeoutSeconds)
                && _incident.InteriorExitPhase < 4)
            {
                RetryInteriorSuspectExit(exitingSuspect,
                    "The suspect did not physically reach the mapped exterior threshold in time.");
                return true;
            }

            if (_incident.InteriorExitPhase == 0)
            {
                if (_incident.InteriorExitNextTaskAt != DateTime.MinValue
                    && now < _incident.InteriorExitNextTaskAt)
                    return true;

                AmbientInteriorAccessRoute route;
                string routeFailure;
                if (!_ambientWorld.TryResolveInteriorAccessRouteForInterior(
                    exitingSuspect,
                    _incident.SceneResolution.LiveInteriorId,
                    _incident.SceneResolution.LocationId,
                    _incident.SceneResolution.CatalogInteriorId,
                    false,
                    out route,
                    out routeFailure))
                {
                    RetryInteriorSuspectExit(exitingSuspect, routeFailure);
                    return true;
                }

                _incident.InteriorExitRoute = route;
                _incident.InteriorExitPhase = 1;
                _incident.InteriorExitStartedAt = now;
                _incident.InteriorExitNextTaskAt = DateTime.MinValue;
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_EXIT_ROUTE_RESOLVED",
                    "Suspect=" + exitingSuspect.Handle
                    + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord
                    + "; CatalogInteriorId=" + route.CatalogInteriorId
                    + "; LiveInteriorId=" + route.TargetLiveInteriorId
                    + "; Entry=" + route.EntryPosition
                    + "; Destination=" + route.DestinationPosition);
            }

            if (_incident.InteriorExitPhase == 1)
            {
                AmbientInteriorAccessRoute route = _incident.InteriorExitRoute;
                if (route == null)
                {
                    RetryInteriorSuspectExit(exitingSuspect, "The resolved interior exit route was lost.");
                    return true;
                }

                if (exitingSuspect.Position.DistanceTo(route.EntryPosition) > 1.35f)
                {
                    if (now >= _incident.InteriorExitNextTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                exitingSuspect,
                                route.EntryPosition.X,
                                route.EntryPosition.Y,
                                route.EntryPosition.Z,
                                2.2f,
                                -1,
                                0.75f,
                                1,
                                route.Pair.InsidePoint.HasHeading
                                    ? route.Pair.InsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, exitingSuspect, true);
                            _incident.InteriorExitNextTaskAt = now.AddSeconds(4);
                            LogRuntime(
                                "POLICE_DISPATCH_INTERIOR_EXIT_NAVIGATION_STARTED",
                                "Suspect=" + exitingSuspect.Handle
                                + "; Entry=" + route.EntryPosition
                                + "; Distance=" + exitingSuspect.Position.DistanceTo(route.EntryPosition).ToString("0.0", CultureInfo.InvariantCulture));
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_DISPATCH_INTERIOR_EXIT_NAVIGATION_FAILED", ex);
                            RetryInteriorSuspectExit(exitingSuspect, ex.Message);
                        }
                    }
                    return true;
                }

                string doorFailure;
                if (!_ambientWorld.TryBeginInteriorAccess(
                    exitingSuspect,
                    route,
                    out doorFailure))
                {
                    RetryInteriorSuspectExit(exitingSuspect, doorFailure);
                    return true;
                }
                _incident.InteriorExitPhase = 2;
                _incident.InteriorExitNextTaskAt = DateTime.MinValue;
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_DOOR_OPENING_REQUESTED",
                    "Suspect=" + exitingSuspect.Handle
                    + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord
                    + "; DoorHashPending=true");
            }

            if (_incident.InteriorExitPhase == 2 || _incident.InteriorExitPhase == 3)
            {
                string accessMessage;
                AmbientInteriorAccessStatus accessStatus = _ambientWorld.MaintainInteriorAccess(
                    exitingSuspect,
                    out accessMessage);
                if (accessStatus == AmbientInteriorAccessStatus.Failed)
                {
                    RetryInteriorSuspectExit(exitingSuspect, accessMessage);
                    return true;
                }
                if (accessStatus == AmbientInteriorAccessStatus.ReadyToCross)
                {
                    _incident.InteriorExitPhase = 3;
                    if (now >= _incident.InteriorExitNextTaskAt)
                    {
                        AmbientInteriorAccessRoute route = _incident.InteriorExitRoute;
                        if (route == null)
                        {
                            RetryInteriorSuspectExit(exitingSuspect, "The door opened but its mapped route was lost.");
                            return true;
                        }
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                exitingSuspect,
                                route.DestinationPosition.X,
                                route.DestinationPosition.Y,
                                route.DestinationPosition.Z,
                                2.2f,
                                -1,
                                0.35f,
                                1,
                                route.Pair.OutsidePoint.HasHeading
                                    ? route.Pair.OutsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, exitingSuspect, true);
                            _incident.InteriorExitNextTaskAt = now.AddSeconds(3);
                            LogRuntime(
                                "POLICE_DISPATCH_INTERIOR_DOOR_CROSSING_TASK_STARTED",
                                "Suspect=" + exitingSuspect.Handle
                                + "; Destination=" + route.DestinationPosition
                                + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_DISPATCH_INTERIOR_DOOR_CROSSING_TASK_FAILED", ex);
                            RetryInteriorSuspectExit(exitingSuspect, ex.Message);
                            return true;
                        }
                    }
                    return true;
                }
                if (accessStatus == AmbientInteriorAccessStatus.DoorOpening)
                    return true;
                if (accessStatus == AmbientInteriorAccessStatus.Completed)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_DOOR_CROSSING_CONFIRMED",
                        "Suspect=" + exitingSuspect.Handle
                        + "; InteriorId=" + _incident.SceneResolution.LiveInteriorId
                        + "; DoorPair=" + (_incident.InteriorExitRoute == null
                            ? string.Empty : _incident.InteriorExitRoute.Pair.InsidePoint.SourceRecord));
                    if (exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle)
                        && _incident.SuspectVehicle != null
                        && _incident.SuspectVehicle.Exists())
                    {
                        _incident.InteriorExitPhase = 4;
                        _incident.InteriorExitVehicleEntryStartedAt = now;
                        _incident.InteriorExitNextTaskAt = DateTime.MinValue;
                    }
                    else
                    {
                        CompleteInteriorSuspectExit(exitingSuspect, player);
                    }
                }
                else if (accessStatus == AmbientInteriorAccessStatus.None)
                {
                    RetryInteriorSuspectExit(exitingSuspect,
                        "The physical door session ended before the crossing was confirmed.");
                    return true;
                }
            }

            if (_incident.InteriorExitPhase == 4)
            {
                Vehicle escapeVehicle = _incident.SuspectVehicle;
                if (escapeVehicle == null || !escapeVehicle.Exists())
                {
                    CompleteInteriorSuspectExit(exitingSuspect, player);
                    return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
                }
                if (exitingSuspect.IsInVehicle()
                    && exitingSuspect.CurrentVehicle != null
                    && exitingSuspect.CurrentVehicle.Exists()
                    && exitingSuspect.CurrentVehicle.Handle == escapeVehicle.Handle)
                {
                    try
                    {
                        Ped target = player != null && player.Exists() ? player : Game.Player.Character;
                        if (target != null)
                            exitingSuspect.Task.VehicleChase(target);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_INTERIOR_GETAWAY_CHASE_FAILED", ex);
                    }
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_GETAWAY_VEHICLE_ENTERED",
                        "Suspect=" + exitingSuspect.Handle + "; Vehicle=" + escapeVehicle.Handle);
                    CompleteInteriorSuspectExit(exitingSuspect, player);
                    return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
                }

                if (now >= _incident.InteriorExitVehicleEntryStartedAt
                    .AddSeconds(InteriorExitVehicleEntryTimeoutSeconds))
                {
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_GETAWAY_ENTRY_TIMED_OUT",
                        "Suspect=" + exitingSuspect.Handle
                        + "; Vehicle=" + escapeVehicle.Handle
                        + "; Action=ContinueOnFoot");
                    CompleteInteriorSuspectExit(exitingSuspect, player);
                    return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
                }

                if (now >= _incident.InteriorExitNextTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_ENTER_VEHICLE,
                            exitingSuspect,
                            escapeVehicle,
                            InteriorExitVehicleEntryTimeoutSeconds * 1000,
                            -1,
                            2.0f,
                            1,
                            0);
                        Function.Call(Hash.SET_PED_KEEP_TASK, exitingSuspect, true);
                        _incident.InteriorExitNextTaskAt = now.AddSeconds(3);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_INTERIOR_GETAWAY_ENTRY_FAILED", ex);
                        _incident.InteriorExitVehicleEntryStartedAt = now
                            .AddSeconds(-InteriorExitVehicleEntryTimeoutSeconds);
                    }
                }
                return true;
            }

            return _incident.InteriorExitActorHandle != 0;
        }

        private void RetryInteriorSuspectExit(Ped suspect, string reason)
        {
            if (_incident == null)
                return;
            if (suspect != null && suspect.Exists())
                _ambientWorld?.CancelInteriorAccess(suspect, "DispatchInteriorExitRetry");
            _incident.InteriorExitRecoveryCount++;
            bool exhausted = _incident.InteriorExitRecoveryCount >= InteriorExitMaximumRecoveries;
            int suspectHandle = suspect == null || !suspect.Exists() ? 0 : suspect.Handle;
            int recoveryCount = _incident.InteriorExitRecoveryCount;
            if (exhausted && suspectHandle != 0)
                _incident.InteriorExitFailedHandles.Add(suspectHandle);
            LogDebug(
                exhausted
                    ? "POLICE_DISPATCH_INTERIOR_EXIT_FAILED"
                    : "POLICE_DISPATCH_INTERIOR_EXIT_RETRY",
                "Suspect=" + suspectHandle
                + "; Recovery=" + _incident.InteriorExitRecoveryCount
                + "; Reason=" + (reason ?? string.Empty));
            ResetInteriorExitAttempt(_incident);
            if (!exhausted)
            {
                _incident.InteriorExitActorHandle = suspectHandle;
                _incident.InteriorExitRecoveryCount = recoveryCount;
                _incident.InteriorExitStartedAt = DateTime.UtcNow;
                _incident.InteriorExitNextTaskAt = DateTime.UtcNow.AddSeconds(1.5);
            }
        }

        private void CompleteInteriorSuspectExit(Ped suspect, Ped player)
        {
            if (_incident == null || suspect == null || !suspect.Exists())
                return;
            _incident.InteriorExitCompletedHandles.Add(suspect.Handle);
            if (_incident.SuspectVehicle == null || !_incident.SuspectVehicle.Exists()
                || suspect.Handle != (_incident.Suspect == null ? 0 : _incident.Suspect.Handle)
                || !suspect.IsInVehicle())
            {
                try
                {
                    Ped target = player != null && player.Exists() ? player : Game.Player.Character;
                    if (target != null && !suspect.IsDead && !IsWoundedSuspect(suspect))
                        suspect.Task.ReactAndFlee(target);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_INTERIOR_FOOT_PURSUIT_STARTED_FAILED", ex);
                }
            }
            LogRuntime(
                "POLICE_DISPATCH_INTERIOR_SUSPECT_EXIT_COMPLETED",
                "Suspect=" + suspect.Handle
                + "; LiveInteriorId=0; Vehicle="
                + (_incident.SuspectVehicle == null ? 0 : _incident.SuspectVehicle.Handle));
            ResetInteriorExitAttempt(_incident);
        }

        private void MaintainInteriorClerkSnatchVictimEgress(
            DateTime now,
            Ped primarySuspect,
            Ped player)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null
                || !IsInteriorClerkSnatchBehavior(incident.SceneBehavior))
                return;

            bool victimResponseAllowsEgress =
                string.Equals(incident.VictimEncounterResponse, "attempt_escape",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(incident.VictimEncounterResponse, "remain_threatened",
                    StringComparison.OrdinalIgnoreCase);
            Ped victim = incident.Victim;
            if (incident.State != LSPDDispatchState.SuspectFleeing
                || !victimResponseAllowsEgress)
            {
                if (victim != null && victim.Exists()
                    && incident.InteriorVictimExitRoute != null)
                    _ambientWorld?.CancelInteriorAccess(
                        victim, "DispatchSnatchVictimResponseChanged");
                ResetInteriorClerkVictimExit(incident);
                return;
            }
            if (_ambientWorld == null || incident.SceneResolution == null
                || victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0
                || incident.InteriorVictimExitRecoveryCount >= InteriorExitMaximumRecoveries
                || incident.InteriorVictimExitCompleted)
                return;

            if (incident.InteriorVictimExitStartedAt == DateTime.MinValue)
                incident.InteriorVictimExitStartedAt = now;
            if (incident.InteriorVictimExitStartedAt != DateTime.MinValue
                && now >= incident.InteriorVictimExitStartedAt.AddSeconds(
                    InteriorExitNavigationTimeoutSeconds))
            {
                RetryInteriorClerkVictimExit(
                    incident, victim, "The shop victim did not reach the mapped exterior threshold before the navigation deadline.");
                return;
            }
            if (incident.InteriorVictimExitNextTaskAt != DateTime.MinValue
                && now < incident.InteriorVictimExitNextTaskAt)
            {
                if (incident.InteriorVictimExitRoute == null
                    && string.Equals(incident.VictimEncounterResponse,
                        "remain_threatened", StringComparison.OrdinalIgnoreCase)
                    && primarySuspect != null && primarySuspect.Exists()
                    && !primarySuspect.IsDead)
                    MaintainInteriorSnatchVictimFollow(
                        incident, victim, primarySuspect, now);
                return;
            }

            try
            {
                int victimLiveInteriorId;
                if (!_ambientWorld.TryGetActorInteriorId(victim, out victimLiveInteriorId))
                {
                    RetryInteriorClerkVictimExit(
                        incident, victim, "GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS did not identify the shop victim.");
                    return;
                }
                if (victimLiveInteriorId == 0
                    || victimLiveInteriorId != incident.SceneResolution.LiveInteriorId)
                {
                    incident.InteriorVictimExitCompleted = true;
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_EXIT_OBSERVED",
                        "Occurrence=" + incident.Id
                        + "; ShopVictim=" + victim.Handle
                        + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                        + "; ExpectedLiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                        + "; ActorLiveInteriorId=" + victimLiveInteriorId
                        + "; Crossing=ObservedFromLiveEntityContext; Vehicle=false");
                    return;
                }

                if (string.Equals(incident.VictimEncounterResponse, "remain_threatened",
                        StringComparison.OrdinalIgnoreCase))
                {
                    int suspectLiveInteriorId;
                    if (primarySuspect != null && primarySuspect.Exists()
                        && !primarySuspect.IsDead
                        && _ambientWorld.TryGetActorInteriorId(
                            primarySuspect, out suspectLiveInteriorId)
                        && suspectLiveInteriorId == incident.SceneResolution.LiveInteriorId)
                    {
                        MaintainInteriorSnatchVictimFollow(
                            incident, victim, primarySuspect, now);
                        return;
                    }
                }

                if (incident.InteriorVictimExitRoute == null)
                {
                    AmbientInteriorAccessRoute route;
                    string routeFailure;
                    if (!_ambientWorld.TryResolveInteriorAccessRouteForInterior(
                        victim,
                        incident.SceneResolution.LiveInteriorId,
                        incident.SceneResolution.LocationId,
                        incident.SceneResolution.CatalogInteriorId,
                        false,
                        out route,
                        out routeFailure))
                    {
                        if (routeFailure.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase))
                        {
                            incident.InteriorVictimExitNextTaskAt = now.AddMilliseconds(750);
                            if (incident.InteriorVictimExitRecoveryCount == 0)
                                LogDebug(
                                    "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_EXIT_PENDING",
                                    "Occurrence=" + incident.Id
                                    + "; ShopVictim=" + victim.Handle
                                    + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                                    + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                                    + "; Reason=" + routeFailure);
                            return;
                        }
                        RetryInteriorClerkVictimExit(incident, victim, routeFailure);
                        return;
                    }

                    incident.InteriorVictimExitRoute = route;
                    incident.InteriorVictimExitPhase = 1;
                    incident.InteriorVictimExitNextTaskAt = DateTime.MinValue;
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_EXIT_ROUTE_RESOLVED",
                        "Occurrence=" + incident.Id
                        + "; ShopVictim=" + victim.Handle
                        + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord
                        + "; CatalogInteriorId=" + route.CatalogInteriorId
                        + "; LiveInteriorId=" + route.TargetLiveInteriorId
                        + "; Entry=" + route.EntryPosition
                        + "; Destination=" + route.DestinationPosition
                        + "; Response=" + incident.VictimEncounterResponse);
                }

                AmbientInteriorAccessRoute activeRoute = incident.InteriorVictimExitRoute;
                if (activeRoute == null)
                {
                    RetryInteriorClerkVictimExit(
                        incident, victim, "The shop victim's mapped door route was lost during preparation.");
                    return;
                }
                if (incident.InteriorVictimExitPhase == 1)
                {
                    if (victim.Position.DistanceTo(activeRoute.EntryPosition) > 1.35f)
                    {
                        if (now >= incident.InteriorVictimExitNextTaskAt)
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                victim,
                                activeRoute.EntryPosition.X,
                                activeRoute.EntryPosition.Y,
                                activeRoute.EntryPosition.Z,
                                1.35f,
                                -1,
                                0.7f,
                                1,
                                activeRoute.Pair.InsidePoint.HasHeading
                                    ? activeRoute.Pair.InsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, victim, true);
                            incident.InteriorVictimExitNextTaskAt = now.AddSeconds(3);
                            LogRuntime(
                                "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_NAVIGATION_STARTED",
                                "Occurrence=" + incident.Id
                                + "; ShopVictim=" + victim.Handle
                                + "; Target=MappedInsideDoorway"
                                + "; Distance=" + victim.Position.DistanceTo(activeRoute.EntryPosition)
                                    .ToString("0.0", CultureInfo.InvariantCulture));
                        }
                        return;
                    }

                    string doorFailure;
                    if (!_ambientWorld.TryBeginInteriorAccess(
                        victim, activeRoute, out doorFailure))
                    {
                        RetryInteriorClerkVictimExit(incident, victim, doorFailure);
                        return;
                    }
                    incident.InteriorVictimExitPhase = 2;
                    incident.InteriorVictimExitNextTaskAt = DateTime.MinValue;
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_DOOR_OPENING_REQUESTED",
                        "Occurrence=" + incident.Id
                        + "; ShopVictim=" + victim.Handle
                        + "; DoorPair=" + activeRoute.Pair.InsidePoint.SourceRecord
                        + "; CatalogInteriorId=" + activeRoute.CatalogInteriorId
                        + "; LiveInteriorId=" + activeRoute.TargetLiveInteriorId);
                }

                if (incident.InteriorVictimExitPhase == 2
                    || incident.InteriorVictimExitPhase == 3)
                {
                    string accessMessage;
                    AmbientInteriorAccessStatus accessStatus =
                        _ambientWorld.MaintainInteriorAccess(victim, out accessMessage);
                    if (accessStatus == AmbientInteriorAccessStatus.Failed)
                    {
                        RetryInteriorClerkVictimExit(incident, victim, accessMessage);
                        return;
                    }
                    if (accessStatus == AmbientInteriorAccessStatus.ReadyToCross)
                    {
                        incident.InteriorVictimExitPhase = 3;
                        if (now >= incident.InteriorVictimExitNextTaskAt)
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                victim,
                                activeRoute.DestinationPosition.X,
                                activeRoute.DestinationPosition.Y,
                                activeRoute.DestinationPosition.Z,
                                1.35f,
                                -1,
                                0.35f,
                                1,
                                activeRoute.Pair.OutsidePoint.HasHeading
                                    ? activeRoute.Pair.OutsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, victim, true);
                            incident.InteriorVictimExitNextTaskAt = now.AddSeconds(3);
                            LogRuntime(
                                "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_DOOR_CROSSING_STARTED",
                                "Occurrence=" + incident.Id
                                + "; ShopVictim=" + victim.Handle
                                + "; Destination=" + activeRoute.DestinationPosition
                                + "; DoorPair=" + activeRoute.Pair.InsidePoint.SourceRecord);
                        }
                        return;
                    }
                    if (accessStatus == AmbientInteriorAccessStatus.DoorOpening)
                    {
                        // Door physics/readiness advances independently of the
                        // actor navigation cadence. Poll at a bounded interval
                        // instead of issuing native door checks every Dispatch tick.
                        incident.InteriorVictimExitNextTaskAt = now.AddMilliseconds(250);
                        return;
                    }
                    if (accessStatus == AmbientInteriorAccessStatus.Completed)
                    {
                        incident.InteriorVictimExitCompleted = true;
                        LogRuntime(
                            "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_DOOR_CROSSING_CONFIRMED",
                            "Occurrence=" + incident.Id
                            + "; ShopVictim=" + victim.Handle
                            + "; DoorPair=" + activeRoute.Pair.InsidePoint.SourceRecord
                            + "; CatalogInteriorId=" + activeRoute.CatalogInteriorId
                            + "; TargetLiveInteriorId=" + activeRoute.TargetLiveInteriorId
                            + "; ActorLiveInteriorId=" + victimLiveInteriorId
                            + "; NativeResult=MaintainInteriorAccess:Completed; Teleport=false");
                        return;
                    }
                    if (accessStatus == AmbientInteriorAccessStatus.None)
                        RetryInteriorClerkVictimExit(
                            incident, victim,
                            "The mapped door session ended before the shop-victim threshold crossing was confirmed.");
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_EGRESS_FAILED", ex);
                RetryInteriorClerkVictimExit(
                    incident, victim, "Exception=" + ex.GetType().Name);
            }
        }

        private void MaintainInteriorSnatchVictimFollow(
            LSPDDispatchEvent incident,
            Ped victim,
            Ped suspect,
            DateTime now)
        {
            if (incident == null || victim == null || !victim.Exists()
                || suspect == null || !suspect.Exists()
                || now < incident.InteriorVictimExitNextTaskAt)
                return;
            Function.Call(
                Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                victim,
                suspect,
                0f,
                -0.85f,
                0f,
                1.1f,
                -1,
                1.4f,
                true);
            Function.Call(Hash.SET_PED_KEEP_TASK, victim, true);
            incident.InteriorVictimExitNextTaskAt = now.AddSeconds(2);
        }

        private void RetryInteriorClerkVictimExit(
            LSPDDispatchEvent incident,
            Ped victim,
            string reason)
        {
            if (incident == null)
                return;
            if (victim != null && victim.Exists())
                _ambientWorld?.CancelInteriorAccess(
                    victim, "DispatchSnatchVictimExitRetry");
            incident.InteriorVictimExitRecoveryCount++;
            int recovery = incident.InteriorVictimExitRecoveryCount;
            bool exhausted = recovery >= InteriorExitMaximumRecoveries;
            incident.InteriorVictimExitRoute = null;
            incident.InteriorVictimExitPhase = 0;
            incident.InteriorVictimExitStartedAt = DateTime.UtcNow;
            incident.InteriorVictimExitNextTaskAt = exhausted
                ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(1.5);
            LogDebug(
                exhausted
                    ? "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_EXIT_FAILED"
                    : "POLICE_DISPATCH_INTERIOR_SNATCH_CLERK_EXIT_RETRY",
                "Occurrence=" + incident.Id
                + "; ShopVictim=" + (victim == null || !victim.Exists() ? 0 : victim.Handle)
                + "; Recovery=" + recovery
                + "; MaximumRecoveries=" + InteriorExitMaximumRecoveries
                + "; Reason=" + (reason ?? string.Empty)
                + "; Teleport=false");
        }

        private void ResetInteriorClerkVictimExit(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return;
            incident.InteriorVictimExitRoute = null;
            incident.InteriorVictimExitPhase = 0;
            incident.InteriorVictimExitRecoveryCount = 0;
            incident.InteriorVictimExitStartedAt = DateTime.MinValue;
            incident.InteriorVictimExitNextTaskAt = DateTime.MinValue;
            incident.InteriorVictimExitCompleted = false;
        }

        private static void ResetInteriorExitAttempt(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return;
            incident.InteriorExitRoute = null;
            incident.InteriorExitActorHandle = 0;
            incident.InteriorExitPhase = 0;
            incident.InteriorExitRecoveryCount = 0;
            incident.InteriorExitStartedAt = DateTime.MinValue;
            incident.InteriorExitNextTaskAt = DateTime.MinValue;
            incident.InteriorExitVehicleEntryStartedAt = DateTime.MinValue;
        }

        private void MaintainCompliantSuspects(DateTime now)
        {
            if (_incident == null || _incident.CompliantSuspects == null)
                return;

            foreach (Ped suspect in _incident.CompliantSuspects
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead
                    && !IsArrested(_incident, ped)
                    && !_incident.BackupArrestInProgressSuspectHandles.Contains(
                        ped.Handle)))
            {
                if (IsDispatchMeleeRecoveryPending(suspect, now))
                    continue;
                if (_incident.PlayerHandcuffInProgress
                    && suspect.Handle == _incident.PlayerHandcuffTargetHandle)
                    continue;
                if (IsWoundedSuspect(suspect))
                    continue;
                try
                {
                    suspect.BlockPermanentEvents = true;
                    suspect.CanSwitchWeapons = false;
                    DateTime lastTask;
                    if (!_lastCompliantTaskAt.TryGetValue(suspect.Handle, out lastTask)
                        || now >= lastTask.AddMilliseconds(CompliantTaskRefreshMilliseconds))
                    {
                        suspect.Task.HandsUp(10000);
                        _lastCompliantTaskAt[suspect.Handle] = now;
                    }
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_COMPLIANCE_MAINTENANCE_FAILED", ex);
                }
            }
        }

        private void MaintainSecuredSuspects(DateTime now)
        {
            if (_incident == null || _incident.ArrestedSuspects == null)
                return;
            foreach (Ped suspect in _incident.ArrestedSuspects
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead))
            {
                try
                {
                    suspect.BlockPermanentEvents = true;
                    suspect.CanSwitchWeapons = false;
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, suspect, true);
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        suspect, false, false, false, true, false, false, false, false);
                    if (!_incident.HasConvoyCustodyHandoff && !suspect.IsInVehicle()
                        && !IsDispatchMeleeRecoveryPending(suspect, now))
                        MaintainHandcuffedPose(suspect, now, false);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_CUSTODY_MAINTENANCE_FAILED", ex);
                }
            }
        }

        private void MaintainHandcuffedPose(Ped suspect, DateTime now, bool force)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead || suspect.IsInVehicle())
                return;
            DateTime lastTask;
            bool hasPreviousAttempt = _lastSecuredPoseTaskAt.TryGetValue(
                suspect.Handle, out lastTask);
            if (!force && hasPreviousAttempt
                && now < lastTask.AddMilliseconds(SecuredPoseRefreshMilliseconds))
                return;

            const string handcuffDictionary = "mp_arresting";
            try
            {
                if (LSImmersiveDictionaryAnimation.IsPlaying(
                    suspect, handcuffDictionary, "idle"))
                {
                    _lastSecuredPoseTaskAt[suspect.Handle] = now;
                    return;
                }

                if (!LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                    handcuffDictionary))
                {
                    _lastSecuredPoseTaskAt[suspect.Handle] =
                        now.AddMilliseconds(750);
                    return;
                }

                float clipDuration;
                if (!LSImmersiveDictionaryAnimation.TryPlay(
                    suspect,
                    handcuffDictionary,
                    "idle",
                    3.0f,
                    -2.0f,
                    -1,
                    1,
                    1.0f,
                    out clipDuration))
                {
                    _lastSecuredPoseTaskAt[suspect.Handle] = now.AddMilliseconds(750);
                    if (!hasPreviousAttempt)
                        LogRuntime("POLICE_DISPATCH_HANDCUFF_CLIP_UNAVAILABLE",
                        "Ped=" + suspect.Handle + "; Dictionary=" + handcuffDictionary
                        + "; Clip=idle; Action=PhysicalCustodyRetained");
                    return;
                }
                _lastSecuredPoseTaskAt[suspect.Handle] = now;
                if (!hasPreviousAttempt || force)
                    LogRuntime("POLICE_DISPATCH_HANDCUFF_CLIP_REQUESTED",
                    "Ped=" + suspect.Handle + "; Dictionary=" + handcuffDictionary
                    + "; Clip=idle; Duration=" + clipDuration.ToString("0.000")
                    + "; Flags=1; PlaybackRate=1.0; TaskDurationMs=-1");
            }
            catch (Exception ex)
            {
                _lastSecuredPoseTaskAt[suspect.Handle] = now.AddMilliseconds(1000);
                LogException("POLICE_DISPATCH_HANDCUFF_POSE_FAILED", ex);
            }
        }

        private void MaintainStagedSceneBehavior(DateTime now)
        {
            if (_incident == null || !_incident.SceneBehaviorInitialized
                || _incident.SurrenderRequested
                || _incident.State != LSPDDispatchState.Investigating
                || _incident.Suspect == null
                || !_incident.Suspect.Exists()
                || IsWoundedSuspect(_incident.Suspect))
                return;

            string type = (_incident.IncidentType ?? string.Empty).ToLowerInvariant();
            if (type != "bank_robbery" && type != "store_robbery")
                return;

            if (_incident.SceneBehaviorStage == 1
                && now >= _incident.SceneBehaviorNextStepAt)
            {
                IssueRobberyMovement(now);
            }
            else if (_incident.SceneBehaviorStage == 2
                && now >= _incident.SceneBehaviorNextStepAt)
            {
                IssueRobberyThreatPose();
            }
        }

        private void IssueRobberyMovement(DateTime now)
        {
            if (_incident == null || _incident.Suspect == null
                || !_incident.Suspect.Exists())
                return;
            try
            {
                int index = 0;
                foreach (Ped suspect in LiveSuspects(_incident))
                {
                    if (IsWoundedSuspect(suspect))
                        continue;
                    float lateral = index == 0 ? 0f : index % 2 == 0 ? 2.2f : -2.2f;
                    Function.Call(
                        Hash.TASK_GO_STRAIGHT_TO_COORD,
                        suspect,
                        _incident.Origin.X + 1.5f,
                        _incident.Origin.Y + lateral,
                        _incident.Origin.Z,
                        1.0f,
                        -1,
                        0f,
                        0f);
                    index++;
                }
                _incident.SceneBehaviorStage = 2;
                _incident.SceneBehaviorNextStepAt = now.AddMilliseconds(1800);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_ROBBERY_MOVEMENT_FAILED", ex);
            }
        }

        private void IssueRobberyThreatPose()
        {
            if (_incident == null || _incident.Suspect == null
                || !_incident.Suspect.Exists())
                return;
            try
            {
                foreach (Ped suspect in LiveSuspects(_incident))
                {
                    if (IsWoundedSuspect(suspect)
                        || _incident.Victim == null || !_incident.Victim.Exists())
                        continue;
                    Function.Call(
                        Hash.TASK_AIM_GUN_AT_ENTITY,
                        suspect,
                        _incident.Victim,
                        -1,
                        true);
                }
                _incident.SceneBehaviorStage = 3;
                _incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_ROBBERY_THREAT_POSE_FAILED", ex);
            }
        }

        private void StartInteriorClerkSnatchScene(Ped player)
        {
            if (_incident == null || _incident.SceneResolution == null
                || !_incident.SceneResolution.IsMappedInteriorDispatchScene
                || _incident.Victim == null || !_incident.Victim.Exists()
                || _incident.Victim.IsDead || _incident.Victim.Health <= 0)
            {
                Fail("The mapped shop snatch scene lost its validated interior or living victim.");
                return;
            }

            if (_incident.SceneAnimationDeadline == DateTime.MinValue)
                _incident.SceneAnimationDeadline = DateTime.UtcNow.AddSeconds(
                    PairedSceneAnimationLoadTimeoutSeconds);

            string animationReason = _ambientWorld == null
                ? "AmbientWorld is not attached by PoliceCore."
                : string.Empty;
            AmbientDispatchAnimationStartResult animation = _ambientWorld == null
                ? AmbientDispatchAnimationStartResult.Failed
                : _ambientWorld.TryStartDispatchPairAnimation(
                    _incident,
                    _incident.Suspect,
                    _incident.Victim,
                    out animationReason);
            if (animation == AmbientDispatchAnimationStartResult.Pending
                && DateTime.UtcNow < _incident.SceneAnimationDeadline)
                return;

            _incident.SceneBehaviorInitialized = true;
            _incident.PreArrivalSceneBehaviorInitialized = true;
            _incident.SceneBehaviorStage = animation == AmbientDispatchAnimationStartResult.Started
                ? 1 : 3;
            _incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
            _incident.Suspect.BlockPermanentEvents = true;
            _incident.Victim.BlockPermanentEvents = true;

            if (animation != AmbientDispatchAnimationStartResult.Started)
            {
                // Preserve the prepared call and its response branches if the
                // optional hostage animation cannot stream. The visible fallback
                // remains a shop-victim threat; the exact animation failure is logged.
                try
                {
                    _incident.Victim.Task.HandsUp(60000);
                    Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY,
                        _incident.Suspect, _incident.Victim, -1, true);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_INTERIOR_SNATCH_FALLBACK_FAILED", ex);
                }
                LogDebug(
                    "POLICE_DISPATCH_INTERIOR_CLERK_SNATCH_ANIMATION_FALLBACK",
                    "Event=" + _incident.Id
                    + "; Suspect=" + _incident.Suspect.Handle
                    + "; Victim=" + _incident.Victim.Handle
                    + "; Dictionary=" + (_incident.SceneAnimationDictionary ?? string.Empty)
                    + "; Result=" + animation
                    + "; Reason=" + animationReason
                    + "; Fallback=ShopVictimThreat; TerminalOutcome=false");
            }
            else
            {
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_CLERK_SNATCH_STARTED",
                    "Event=" + _incident.Id
                    + "; Suspect=" + _incident.Suspect.Handle
                    + "; Victim=" + _incident.Victim.Handle
                    + "; CatalogInteriorId=" + _incident.SceneResolution.CatalogInteriorId
                    + "; LiveInteriorId=" + _incident.SceneResolution.LiveInteriorId
                    + "; Dictionary=" + _incident.SceneAnimationDictionary
                    + "; Primary=" + _incident.SceneAnimationPrimaryClip
                    + "; Secondary=" + _incident.SceneAnimationSecondaryClip
                    + "; Vehicle=false; TerminalOutcome=false");
            }

            if (IsPlayerInsideIncidentInterior(player))
                SetState(LSPDDispatchState.Investigating);
            ReportAudioStage(
                "lsimmersivelife.police.scene.active",
                _incident,
                "interior-clerk-snatch-active",
                true);
        }

        private void ApplyAdditionalDispatchParticipantThreatPose(Ped player)
        {
            if (_incident == null || _incident.AdditionalParticipants == null
                || _incident.AdditionalParticipants.Count == 0)
                return;

            bool refreshForArrival = IsPlayerNearDispatchOrigin(player);
            foreach (Ped participant in _incident.AdditionalParticipants)
            {
                if (participant == null || !participant.Exists()
                    || participant.IsDead || participant.Health <= 0)
                    continue;
                if (_incident.SceneParticipantBehaviorInitializedHandles.Contains(
                        participant.Handle)
                    && !refreshForArrival)
                    continue;

                try
                {
                    participant.BlockPermanentEvents = true;
                    participant.Task.HandsUp(120000);
                    _incident.SceneParticipantBehaviorInitializedHandles.Add(
                        participant.Handle);
                    LogRuntime("POLICE_DISPATCH_SHOP_PARTICIPANT_THREATENED",
                        "Occurrence=" + _incident.Id
                        + "; Entity=Ped; Handle=" + participant.Handle
                        + "; Owner=" + (_incident.ExistingAmbientSceneParticipantHandles
                            .Contains(participant.Handle)
                                ? "AmbientWorldClaim" : "Dispatch")
                        + "; Pool=" + _incident.SceneParticipantPoolId
                        + "; Stage=" + (refreshForArrival ? "PlayerAtScene" : "PreArrival")
                        + "; Activity=HandsUp; TerminalOutcome=false");
                }
                catch (Exception ex)
                {
                    LogDebug("POLICE_DISPATCH_SHOP_PARTICIPANT_THREAT_POSE_FAILED",
                        "Occurrence=" + _incident.Id
                        + "; Handle=" + participant.Handle
                        + "; Reason=" + ex.GetType().Name);
                }
            }
        }

        private void StartSceneBehavior(Ped player)
        {
            if (_incident == null || _incident.HasConvoyCustodyHandoff
                || !_incident.OwnedByDispatch || _incident.Suspect == null ||
                !_incident.Suspect.Exists() || _incident.Suspect.IsDead ||
                _incident.SceneBehaviorInitialized)
                return;

            bool pairedScene = string.Equals(
                    _incident.SceneBehavior, "paired_meeting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    _incident.SceneBehavior, "paired_exchange", StringComparison.OrdinalIgnoreCase);
            ApplyAdditionalDispatchParticipantThreatPose(player);
            if (IsInteriorClerkSnatchBehavior(_incident.SceneBehavior))
            {
                StartInteriorClerkSnatchScene(player);
                return;
            }
            if (pairedScene)
            {
                bool responseAlreadyStarted = _incident.SurrenderRequested
                    || _incident.PlayerHandcuffInProgress
                    || (_incident.CompliantSuspects != null
                        && _incident.CompliantSuspects.Any())
                    || (_incident.ArrestedSuspects != null
                        && _incident.ArrestedSuspects.Any())
                    || LiveSuspects(_incident).Any(suspect =>
                        suspect.IsInCombatAgainst(player) || suspect.IsShooting || suspect.IsFleeing);
                if (responseAlreadyStarted)
                {
                    _incident.SceneBehaviorInitialized = true;
                    LogRuntime(
                        "POLICE_DISPATCH_SCENE_ANIMATION_SKIPPED",
                        "Event=" + _incident.Id + "; Reason=Player response already began.");
                    return;
                }

                if (_incident.SceneAnimationDeadline == DateTime.MinValue)
                    _incident.SceneAnimationDeadline = DateTime.UtcNow.AddSeconds(
                        PairedSceneAnimationLoadTimeoutSeconds);

                List<Ped> actors = LiveSuspects(_incident).Take(2).ToList();
                if (actors.Count < 2)
                {
                    bool memberWasLost = TrackedSuspects(_incident).Any(suspect =>
                        suspect == null || !suspect.Exists() || suspect.IsDead);
                    if (memberWasLost)
                    {
                        _incident.SceneBehaviorInitialized = true;
                        LogRuntime(
                            "POLICE_DISPATCH_SCENE_ANIMATION_SKIPPED",
                            "Event=" + _incident.Id + "; Reason=A group member was already lost.");
                        return;
                    }
                    Fail("The paired alley scene could not start because two living suspects were not available.");
                    return;
                }

                string animationReason = _ambientWorld == null
                    ? "AmbientWorld is not attached by PoliceCore."
                    : string.Empty;
                AmbientDispatchAnimationStartResult animation = _ambientWorld == null
                    ? AmbientDispatchAnimationStartResult.Failed
                    : _ambientWorld.TryStartDispatchPairAnimation(
                        _incident,
                        actors[0],
                        actors[1],
                        out animationReason);

                if (animation == AmbientDispatchAnimationStartResult.Pending
                    && DateTime.UtcNow < _incident.SceneAnimationDeadline)
                    return;
                if (animation != AmbientDispatchAnimationStartResult.Started)
                {
                    Fail("The paired alley scene could not start its authored animation. "
                        + animationReason);
                    return;
                }

                _incident.SceneBehaviorInitialized = true;
                _incident.PreArrivalSceneBehaviorInitialized = true;
                _incident.SceneBehaviorStage = 1;
                _incident.SceneBehaviorNextStepAt = DateTime.MinValue;
                List<Ped> liveGroup = LiveSuspects(_incident).ToList();
                foreach (Ped suspect in liveGroup)
                    suspect.BlockPermanentEvents = true;
                if (liveGroup.Count > 2)
                    PlayConfiguredPreArrivalActivity(liveGroup[2], "guard");
                if (IsPlayerNearDispatchOrigin(player))
                    SetState(LSPDDispatchState.Investigating);
                LogRuntime(
                    "POLICE_DISPATCH_SCENE_ANIMATION_STARTED",
                    "Event=" + _incident.Id
                    + "; Behavior=" + _incident.SceneBehavior
                    + "; Dictionary=" + _incident.SceneAnimationDictionary
                    + "; Actors=" + actors[0].Handle + "," + actors[1].Handle);
                return;
            }

            _incident.SceneBehaviorInitialized = true;
            _incident.SceneBehaviorStage = 0;
            _incident.SceneBehaviorNextStepAt = DateTime.MinValue;
            string type = (_incident.IncidentType ?? string.Empty).ToLowerInvariant();
            bool hasAuthoredSuspectResponses = _incident.SuspectResponseBranches != null
                && _incident.SuspectResponseBranches.Count > 0;
            try
            {
                bool robberyScene = type == "bank_robbery" || type == "store_robbery";
                bool mustWaitForPlayerApproach = !robberyScene
                    && !_incident.StartPursuitOnAccept
                    && (_incident.Mobile || _incident.Armed || _incident.Severity >= 4)
                    && !IsPlayerNearDispatchOrigin(player);
                if (mustWaitForPlayerApproach)
                {
                    if (!_incident.PreArrivalSceneBehaviorInitialized)
                    {
                        foreach (Ped suspect in LiveSuspects(_incident))
                        {
                            suspect.BlockPermanentEvents = true;
                            PlayConfiguredPreArrivalActivity(
                                suspect,
                                _incident.PreArrivalActivity);
                        }
                        _incident.PreArrivalSceneBehaviorInitialized = true;
                        LogRuntime(
                            "POLICE_DISPATCH_PREARRIVAL_SCENE_ACTIVE",
                            "Event=" + _incident.Id
                            + "; Suspects=" + LiveSuspects(_incident).Count()
                            + "; Activity=" + _incident.PreArrivalActivity
                            + "; TerminalOutcome=false");
                    }
                    _incident.SceneBehaviorInitialized = false;
                    return;
                }

                foreach (Ped suspect in LiveSuspects(_incident))
                    suspect.BlockPermanentEvents = true;

                if (string.Equals(_incident.SceneBehavior, "interior_clerk_assault",
                    StringComparison.OrdinalIgnoreCase))
                {
                    int victimInteriorId;
                    if (_incident.Victim == null || !_incident.Victim.Exists()
                        || _incident.Victim.IsDead || _incident.Victim.Health <= 0
                        || _incident.SceneResolution == null
                        || !_incident.SceneResolution.IsMappedInteriorDispatchScene
                        || !_ambientWorld.TryGetActorInteriorId(
                            _incident.Victim, out victimInteriorId)
                        || victimInteriorId != _incident.SceneResolution.LiveInteriorId)
                    {
                        Fail("The mapped shop assault lost its living victim or exact interior identity.");
                        return;
                    }

                    foreach (Ped suspect in LiveSuspects(_incident))
                        PlayConfiguredPreArrivalActivity(suspect, "victim_attack");
                    _incident.PreArrivalSceneBehaviorInitialized = true;
                    _incident.SceneBehaviorStage = 3;
                    _incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
                    if (IsPlayerInsideIncidentInterior(player))
                        SetState(LSPDDispatchState.Investigating);
                    ReportAudioStage(
                        "lsimmersivelife.police.scene.active",
                        _incident,
                        "interior-clerk-assault-active",
                        true);
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_CLERK_ASSAULT_STARTED",
                        "Event=" + _incident.Id
                        + "; Suspects=" + string.Join(",", LiveSuspects(_incident)
                            .Select(value => value.Handle.ToString(CultureInfo.InvariantCulture)))
                        + "; Victim=" + _incident.Victim.Handle
                        + "; CatalogInteriorId=" + _incident.SceneResolution.CatalogInteriorId
                        + "; LiveInteriorId=" + _incident.SceneResolution.LiveInteriorId
                        + "; WeaponRequirement=false; Vehicle=false; TerminalOutcome=false");
                    return;
                }

                if (IsMappedInteriorDispatchBehavior(_incident.SceneBehavior))
                {
                    if (string.Equals(
                        _incident.SceneBehavior, "murder_victim_interior",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (_incident.Victim == null || !_incident.Victim.Exists()
                            || !_incident.Victim.IsDead || _incident.Victim.Health > 0
                            || _incident.SceneResolution == null
                            || !_incident.SceneResolution.IsInteriorScene
                            || (!_incident.SceneResolution.IsMappedInteriorDispatchScene
                                && !_incident.SceneResolution.IsInsideOnlyInteriorScene))
                        {
                            Fail("The interior murder scene could not start because its authored dead victim or validated live interior was unavailable.");
                            return;
                        }

                        if (!_incident.PreArrivalSceneBehaviorInitialized)
                        {
                            foreach (Ped suspect in LiveSuspects(_incident))
                                PlayConfiguredPreArrivalActivity(suspect, "murder_scene");
                            _incident.PreArrivalSceneBehaviorInitialized = true;
                        }
                        _incident.SceneBehaviorStage = 3;
                        _incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
                        if (IsPlayerInsideIncidentInterior(player))
                            SetState(LSPDDispatchState.Investigating);
                        ReportAudioStage(
                            "lsimmersivelife.police.scene.active",
                            _incident,
                            "mapped-interior-murder-active",
                            true);
                        LogRuntime(
                            "POLICE_DISPATCH_MAPPED_INTERIOR_MURDER_SCENE_STARTED",
                            "Incident=" + _incident.Id
                            + "; Suspects=" + string.Join(",", LiveSuspects(_incident)
                                .Select(value => value.Handle.ToString(CultureInfo.InvariantCulture)))
                            + "; Victim=" + _incident.Victim.Handle
                            + "; VictimDead=true"
                            + "; InteriorId=" + _incident.SceneResolution.LiveInteriorId
                            + "; InteriorAccessMode=" + (_incident.SceneResolution.IsInsideOnlyInteriorScene
                                ? "InsideOnlyNoMappedDoorPair" : "MappedDoorPair")
                            + "; PlayerInside=" + IsPlayerInsideIncidentInterior(player)
                            + "; Threat=SuspectNearVictim; TerminalOutcome=false");
                        return;
                    }

                    if (_incident.Victim == null || !_incident.Victim.Exists()
                        || _incident.Victim.IsDead || _incident.Victim.Health <= 0)
                    {
                        Fail("The mapped interior incident could not start because its living victim was not safely placed inside the building.");
                        return;
                    }

                    _incident.Victim.BlockPermanentEvents = true;
                    _incident.Victim.Task.HandsUp(60000);
                    foreach (Ped suspect in LiveSuspects(_incident))
                    {
                        Function.Call(
                            Hash.TASK_AIM_GUN_AT_ENTITY,
                            suspect,
                            _incident.Victim,
                            -1,
                            true);
                    }
                    _incident.SceneBehaviorStage = 3;
                    _incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
                    if (IsPlayerInsideIncidentInterior(player))
                        SetState(LSPDDispatchState.Investigating);
                    ReportAudioStage(
                        "lsimmersivelife.police.scene.active",
                        _incident,
                        "mapped-interior-active",
                        true);
                    LogRuntime(
                        "POLICE_DISPATCH_MAPPED_INTERIOR_SCENE_STARTED",
                        "Incident=" + _incident.Id
                        + "; SceneBehavior=" + _incident.SceneBehavior
                        + "; Suspects=" + string.Join(",", LiveSuspects(_incident)
                            .Select(value => value.Handle.ToString(CultureInfo.InvariantCulture)))
                        + "; Victim=" + _incident.Victim.Handle
                        + "; InteriorId=" + _incident.SceneResolution.LiveInteriorId
                        + "; Threat=SuspectsAimingAtLivingVictim");
                }
                // Other robbery callouts are live scenes at the location. Let
                // the suspect finish leaving the escape vehicle before assigning
                // movement and the threat pose.
                else if (type == "bank_robbery" || type == "store_robbery")
                {
                    bool leavingVehicle = _incident.SuspectVehicle != null
                        && _incident.SuspectVehicle.Exists()
                        && _incident.Suspect.IsInVehicle();
                    if (leavingVehicle)
                    {
                        Function.Call(
                            Hash.TASK_LEAVE_VEHICLE,
                            _incident.Suspect,
                            _incident.SuspectVehicle,
                            0);
                        _incident.SceneBehaviorStage = 1;
                        _incident.SceneBehaviorNextStepAt =
                            DateTime.UtcNow.AddMilliseconds(1200);
                    }
                    else
                    {
                        IssueRobberyMovement(DateTime.UtcNow);
                    }
                    if (IsPlayerNearDispatchOrigin(player))
                        SetState(LSPDDispatchState.Investigating);
                    ReportAudioStage(
                        "lsimmersivelife.police.scene.active",
                        _incident,
                        "robbery-active",
                        true);
                }
                else if (IsVehicleEntryActivity(_incident.VehicleActivity))
                {
                    StartVehicleSceneActivity(player);
                }
                else if (type == "kidnapping" && !hasAuthoredSuspectResponses)
                {
                    bool interiorScene = _incident.SceneResolution != null
                        && _incident.SceneResolution.IsInteriorScene;
                    if (!interiorScene)
                    {
                        if (_incident.SuspectVehicle != null
                            && _incident.SuspectVehicle.Exists())
                            _incident.Suspect.Task.VehicleChase(player);
                        else
                            _incident.Suspect.Task.ReactAndFlee(player);
                    }
                    MarkSuspectFleeing(_incident, _incident.Suspect);
                    OrderAdditionalSuspectsToFlee(player);
                    SetStateForUnresolvedSuspects(
                        LSPDDispatchState.SuspectFleeing);
                    ReportAudioStage(
                        "lsimmersivelife.police.pursuit.continuing",
                        _incident,
                        "kidnapping-pursuit",
                        true);
                }
                else if (type == "carjacking" && !hasAuthoredSuspectResponses)
                {
                    bool interiorScene = _incident.SceneResolution != null
                        && _incident.SceneResolution.IsInteriorScene;
                    if (!interiorScene)
                    {
                        if (_incident.SuspectVehicle != null
                            && _incident.SuspectVehicle.Exists())
                            _incident.Suspect.Task.VehicleChase(player);
                        else
                            _incident.Suspect.Task.ReactAndFlee(player);
                    }
                    MarkSuspectFleeing(_incident, _incident.Suspect);
                    OrderAdditionalSuspectsToFlee(player);
                    SetStateForUnresolvedSuspects(
                        LSPDDispatchState.SuspectFleeing);
                    ReportAudioStage(
                        "lsimmersivelife.police.pursuit.continuing",
                        _incident,
                        "carjacking-pursuit",
                        true);
                }
                else if (_incident.StartPursuitOnAccept && _incident.RequiresVehicle)
                {
                    StartVehiclePursuitOnAccept(player);
                }
                else if (_incident.SuspectResponseBranches != null
                    && _incident.SuspectResponseBranches.Count > 0)
                {
                    if (!_incident.PreArrivalSceneBehaviorInitialized)
                    {
                        foreach (Ped suspect in LiveSuspects(_incident))
                            PlayConfiguredPreArrivalActivity(
                                suspect,
                                _incident.PreArrivalActivity);
                        _incident.PreArrivalSceneBehaviorInitialized = true;
                    }
                    if (IsPlayerNearDispatchOrigin(player))
                        SetState(LSPDDispatchState.Investigating);
                    LogRuntime(
                        "POLICE_DISPATCH_SCENE_RESPONSE_AWAITING_PLAYER",
                        "Event=" + _incident.Id
                        + "; Suspects=" + LiveSuspects(_incident).Count()
                        + "; Allowed=" + string.Join(",", _incident.SuspectResponseBranches)
                        + "; PreArrival=" + _incident.PreArrivalActivity);
                }
                else if (_incident.Mobile)
                {
                    bool interiorScene = _incident.SceneResolution != null
                        && _incident.SceneResolution.IsInteriorScene;
                    if (!interiorScene)
                    {
                        if (_incident.SuspectVehicle != null
                            && _incident.SuspectVehicle.Exists())
                            _incident.Suspect.Task.VehicleChase(player);
                        else
                            _incident.Suspect.Task.ReactAndFlee(player);
                    }
                    MarkSuspectFleeing(_incident, _incident.Suspect);
                    OrderAdditionalSuspectsToFlee(player);
                    SetStateForUnresolvedSuspects(
                        LSPDDispatchState.SuspectFleeing);
                    ReportAudioStage(
                        "lsimmersivelife.police.pursuit.continuing",
                        _incident,
                        "pursuit",
                        true);
                }
                else if (_incident.Armed || _incident.Severity >= 4)
                {
                    foreach (Ped suspect in LiveSuspects(_incident))
                    {
                        _incident.SuspectResponses[suspect.Handle] = "resist";
                        suspect.Task.Combat(player);
                    }
                    SetStateForUnresolvedSuspects(
                        LSPDDispatchState.SuspectResisting);
                    ReportAudioStage(
                        "lsimmersivelife.police.resistance.resisting",
                        _incident,
                        "resisting",
                        true);
                }
                else
                {
                    foreach (Ped suspect in LiveSuspects(_incident))
                    {
                        suspect.Task.LookAt(player, 2500);
                        PlaySceneActivity(suspect, type);
                    }
                    if (IsPlayerNearDispatchOrigin(player))
                        SetState(LSPDDispatchState.Investigating);
                }
                LogRuntime(
                    "POLICE_DISPATCH_SCENE_STARTED",
                    _incident.Title + " | State=" + _incident.State
                    + " | Behavior=" + type);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SCENE_BEHAVIOR_FAILED", ex);
            }
        }
        /// <summary>
        /// Begins model streaming without Script.Wait. This method is safe for
        /// a LemonUI action because it only asks GTA to load assets and lets the
        /// normal PoliceCore tick finish scene creation later.
        /// </summary>
        private bool BeginScenePreparation(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return false;
            Model pedModel = new Model(incident.SuspectModel);
            Model vehicleModel = new Model(incident.VehicleModel);
            if (!IsUsableModel(pedModel, false)
                || (incident.RequiresVehicle && !IsUsableModel(vehicleModel, true)))
                return false;
            try
            {
                if (!pedModel.IsLoaded)
                    pedModel.Request();
                if (incident.RequiresVehicle && !vehicleModel.IsLoaded)
                    vehicleModel.Request();
                foreach (string candidateName in RequiredAdditionalSuspectModels(incident))
                {
                    Model candidate = new Model(candidateName);
                    if (IsUsableModel(candidate, false) && !candidate.IsLoaded)
                    {
                        // Keep this asynchronous request alive until the full
                        // authored group can be created or the incident reaches
                        // terminal cleanup. Releasing it here reduced group
                        // Dispatch scenes to whichever primary model happened
                        // to be loaded on the next tick.
                        candidate.Request();
                    }
                }
                incident.ScenePreparationRequested = true;
                incident.ScenePreparationDeadline = DateTime.UtcNow.AddSeconds(
                    ScenePreparationTimeoutSeconds);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SCENE_PREPARATION_FAILED", ex);
                return false;
            }
        }

        private void OrderAdditionalSuspectsToFlee(Ped player)
        {
            if (_incident == null || player == null || !player.Exists())
                return;
            foreach (Ped suspect in LiveSuspects(_incident))
            {
                if (_incident.Suspect != null && suspect.Handle == _incident.Suspect.Handle)
                    continue;
                if (_incident.SceneResolution != null
                    && _incident.SceneResolution.IsInteriorScene
                    && _ambientWorld != null)
                {
                    int suspectInteriorId;
                    if (_ambientWorld.TryGetActorInteriorId(suspect, out suspectInteriorId)
                        && suspectInteriorId == _incident.SceneResolution.LiveInteriorId)
                    continue;
                }
                MarkSuspectFleeing(_incident, suspect);
                try { suspect.Task.ReactAndFlee(player); }
                catch (Exception ex) { LogException("POLICE_DISPATCH_GROUP_FLEE_TASK_FAILED", ex); }
            }
        }

        private bool IsPlayerNearDispatchOrigin(Ped player)
        {
            return _incident != null
                && player != null && player.Exists() && !player.IsDead
                && player.Position.DistanceTo(_incident.Origin)
                    <= PairedSceneAnimationApproachDistance;
        }

        private static bool IsPlayerAtResolvedDispatchScene(
            LSPDDispatchEvent incident,
            Ped player)
        {
            if (incident == null || player == null || !player.Exists() || player.IsDead)
                return false;
            if (player.Position.DistanceTo(incident.Origin) <= SceneArrivalRadius)
                return true;
            return TrackedSuspects(incident).Any(suspect => suspect != null
                && suspect.Exists()
                && player.Position.DistanceTo(suspect.Position) <= SceneArrivalRadius);
        }

        private void PlayConfiguredPreArrivalActivity(Ped suspect, string activity)
        {
            if (_incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead
                || (suspect.IsInVehicle()
                    && !string.Equals(activity, "victim_threat", StringComparison.OrdinalIgnoreCase))
                || string.IsNullOrWhiteSpace(activity))
                return;

            try
            {
                if (string.Equals(activity, "guard", StringComparison.OrdinalIgnoreCase))
                {
                    Function.Call(
                        Hash.TASK_START_SCENARIO_IN_PLACE,
                        suspect,
                        "WORLD_HUMAN_GUARD_STAND",
                        0,
                        true);
                    return;
                }

                if (string.Equals(activity, "murder_scene", StringComparison.OrdinalIgnoreCase))
                {
                    if (_incident.Victim == null || !_incident.Victim.Exists()
                        || !_incident.Victim.IsDead)
                        return;

                    Vector3 victimPosition = _incident.Victim.Position;
                    Function.Call(
                        Hash.TASK_WANDER_IN_AREA,
                        suspect,
                        victimPosition.X,
                        victimPosition.Y,
                        victimPosition.Z,
                        2.25f,
                        3,
                        6);
                    return;
                }

                if (string.Equals(activity, "victim_threat", StringComparison.OrdinalIgnoreCase))
                {
                    Ped victim = _incident.Victim;
                    if (victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0)
                        return;

                    if (!_incident.PreArrivalVictimThreatStarted)
                    {
                        victim.BlockPermanentEvents = true;
                        victim.Task.HandsUp(60000);
                        _incident.PreArrivalVictimThreatStarted = true;
                    }
                    suspect.BlockPermanentEvents = true;
                    Function.Call(
                        Hash.TASK_AIM_GUN_AT_ENTITY,
                        suspect,
                        victim,
                        -1,
                        true);
                    return;
                }

                if (string.Equals(activity, "victim_attack", StringComparison.OrdinalIgnoreCase))
                {
                    Ped victim = _incident.Victim;
                    if (victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0
                        || _incident.SceneResolution == null
                        || !_incident.SceneResolution.IsMappedInteriorDispatchScene)
                        return;

                    int suspectInteriorId;
                    int victimInteriorId;
                    if (_ambientWorld == null
                        || !_ambientWorld.TryGetActorInteriorId(suspect, out suspectInteriorId)
                        || !_ambientWorld.TryGetActorInteriorId(victim, out victimInteriorId)
                        || suspectInteriorId != _incident.SceneResolution.LiveInteriorId
                        || victimInteriorId != _incident.SceneResolution.LiveInteriorId)
                        return;

                    victim.BlockPermanentEvents = true;
                    suspect.BlockPermanentEvents = true;
                    suspect.Task.Combat(victim);
                    LogRuntime(
                        "POLICE_DISPATCH_PREARRIVAL_CLERK_ASSAULT_TASK_STARTED",
                        "Event=" + _incident.Id
                        + "; Suspect=" + suspect.Handle
                        + "; ShopVictim=" + victim.Handle
                        + "; CatalogInteriorId=" + _incident.SceneResolution.CatalogInteriorId
                        + "; LiveInteriorId=" + _incident.SceneResolution.LiveInteriorId
                        + "; Task=TASK_COMBAT_PED; Armed=false; TerminalOutcome=false");
                    return;
                }

                if (!string.Equals(activity, "disturbance", StringComparison.OrdinalIgnoreCase))
                    return;

                if (_incident.SceneResolution != null
                    && _incident.SceneResolution.IsInteriorScene)
                {
                    Function.Call(
                        Hash.TASK_START_SCENARIO_IN_PLACE,
                        suspect,
                        "WORLD_HUMAN_STAND_IMPATIENT",
                        0,
                        true);
                    return;
                }

                Vector3 center = suspect.Position;
                Function.Call(
                    Hash.TASK_WANDER_IN_AREA,
                    suspect,
                    center.X,
                    center.Y,
                    center.Z,
                    3.0f,
                    3,
                    6);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_PREARRIVAL_ACTIVITY_FAILED", ex);
            }
        }

        private void StartVehicleSceneActivity(Ped player)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || !IsVehicleEntryActivity(incident.VehicleActivity))
                return;

            Vehicle vehicle = incident.SuspectVehicle;
            if (vehicle == null || !vehicle.Exists())
            {
                Fail("The vehicle incident could not start because its prepared vehicle was unavailable.");
                return;
            }
            if (string.Equals(incident.VehicleActivity, "carjacking",
                    StringComparison.OrdinalIgnoreCase)
                && (incident.Victim == null || !incident.Victim.Exists()
                    || incident.Victim.IsDead || incident.Victim.Health <= 0
                    || incident.Victim.IsInVehicle()
                    || incident.VictimVehicle == null
                    || !incident.VictimVehicle.Exists()
                    || incident.VictimVehicle.Handle != vehicle.Handle))
            {
                Fail("The carjacking could not start because the prepared victim was not outside and linked to the occurrence vehicle.");
                return;
            }

            string seatFailure;
            if (!PrepareVehicleEntrySeats(incident, out seatFailure))
            {
                Fail("The vehicle incident could not start because its authored occupants no longer fit the prepared vehicle. "
                    + seatFailure);
                return;
            }

            DateTime now = DateTime.UtcNow;
            incident.VehicleActivityStartedAt = now;
            incident.VehicleActivityNextCheckAt = now;
            incident.VehicleActivityNextTaskAt = now;
            incident.VehicleActivityEntryDeadlineAt = DateTime.MinValue;
            incident.VehicleActivityEntryAttempts = 0;
            incident.SceneBehaviorInitialized = true;
            incident.PreArrivalSceneBehaviorInitialized = true;

            if (string.Equals(incident.VehicleActivity, "carjacking",
                StringComparison.OrdinalIgnoreCase))
            {
                incident.VehicleActivityStage = 1;
                Ped carjacker = incident.Suspect;
                if (carjacker == null || !carjacker.Exists())
                {
                    incident.VehicleActivityStage = 4;
                    LogRuntime("POLICE_DISPATCH_VEHICLE_ACTIVITY_FAILED",
                        "Occurrence=" + incident.Id + "; Stage=CarjackerIdentity"
                        + "; Suspect=Unavailable; Vehicle=" + vehicle.Handle);
                    Fail("The carjacking could not start because its assigned suspect was unavailable.");
                    return;
                }
                incident.VehicleActivityNextTaskAt = now;
                bool approachOrdered = TryOrderCarjackingVictimApproach(incident, carjacker);
                if (!approachOrdered)
                {
                    incident.VehicleActivityStage = 4;
                    LogRuntime("POLICE_DISPATCH_VEHICLE_ACTIVITY_FAILED",
                        "Occurrence=" + incident.Id
                        + "; Stage=PhysicalVictimApproach"
                        + "; Suspect=" + carjacker.Handle
                        + "; Victim=" + incident.Victim.Handle
                        + "; Vehicle=" + vehicle.Handle
                        + "; Native=TASK_GO_TO_ENTITY; Result=TaskNotIssued");
                    Fail("The carjacking could not start because the suspect approach task was unavailable.");
                    return;
                }
                LogRuntime("POLICE_DISPATCH_CARJACKING_APPROACH_STARTED",
                    "Occurrence=" + incident.Id
                    + "; Suspect=" + carjacker.Handle
                    + "; Victim=" + incident.Victim.Handle
                    + "; VictimVehicle=" + incident.VictimVehicle.Handle
                    + "; DriverVehicle=" + vehicle.Handle
                    + "; VictimDistance=" + carjacker.Position.DistanceTo(incident.Victim.Position)
                        .ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Task=TASK_GO_TO_ENTITY; PhysicalMovement=true; TerminalOutcome=false");
            }
            else
            {
                incident.VehicleActivityStage = 2;
                incident.VehicleActivityEntryDeadlineAt = now.AddSeconds(30);
                LogRuntime("POLICE_DISPATCH_PARKED_VEHICLE_THEFT_STARTED",
                    "Occurrence=" + incident.Id
                    + "; Suspects=" + string.Join(",", LiveSuspects(incident)
                        .Select(value => value.Handle.ToString(CultureInfo.InvariantCulture)))
                    + "; Vehicle=" + vehicle.Handle
                    + "; Activity=ApproachAndPhysicalEntry; TerminalOutcome=false");
            }
        }

        private void MaintainVehicleSceneActivity(DateTime now, Ped player)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || !IsVehicleEntryActivity(incident.VehicleActivity)
                || incident.VehicleActivityStage <= 0
                || incident.VehicleActivityStage >= 3
                || !incident.OwnedByDispatch || incident.HasConvoyCustodyHandoff
                || now < incident.VehicleActivityNextCheckAt)
                return;

            incident.VehicleActivityNextCheckAt = now.AddSeconds(1);
            Vehicle vehicle = incident.SuspectVehicle;
            if (vehicle == null || !vehicle.Exists())
            {
                incident.VehicleActivityStage = 4;
                LogRuntime("POLICE_DISPATCH_VEHICLE_ACTIVITY_FAILED",
                    "Occurrence=" + incident.Id + "; Stage=VehicleIdentity"
                    + "; Vehicle=Unavailable; CleanupReason=VehicleLostDuringEntry");
                Fail("The suspect vehicle disappeared before the suspect could enter it.");
                return;
            }

            if (incident.VehicleActivityStage == 1)
            {
                Ped victim = incident.Victim;
                Ped carjacker = incident.Suspect;
                if (victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0
                    || carjacker == null || !carjacker.Exists() || carjacker.IsDead)
                {
                    incident.VehicleActivityStage = 4;
                    LogRuntime("POLICE_DISPATCH_VEHICLE_ACTIVITY_FAILED",
                        "Occurrence=" + incident.Id + "; Stage=CarjackingApproach"
                        + "; Suspect=" + (carjacker == null ? 0 : carjacker.Handle)
                        + "; Victim=" + (victim == null ? 0 : victim.Handle)
                        + "; Result=RequiredActorUnavailable");
                    Fail("The carjacking approach could not continue because the suspect or victim was unavailable.");
                    return;
                }

                float victimDistance = carjacker.Position.DistanceTo(victim.Position);
                if (!incident.VehicleActivityThreatStarted && victimDistance <= 3.0f)
                {
                    PlayConfiguredPreArrivalActivity(carjacker, "victim_threat");
                    incident.VehicleActivityThreatStarted = true;
                    incident.VehicleActivityNextTaskAt = now.AddSeconds(2);
                    LogRuntime("POLICE_DISPATCH_CARJACKING_THREAT_STARTED",
                        "Occurrence=" + incident.Id
                        + "; Suspect=" + carjacker.Handle
                        + "; Victim=" + victim.Handle
                        + "; VictimVehicle=" + vehicle.Handle
                        + "; Distance=" + victimDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Task=TASK_AIM_GUN_AT_ENTITY; PhysicalThreat=true; TerminalOutcome=false");
                    return;
                }

                if (!incident.VehicleActivityThreatStarted)
                {
                    if (now >= incident.VehicleActivityStartedAt.AddSeconds(30))
                    {
                        incident.VehicleActivityStage = 4;
                        LogRuntime("POLICE_DISPATCH_CARJACKING_APPROACH_FAILED",
                            "Occurrence=" + incident.Id
                            + "; Suspect=" + carjacker.Handle
                            + "; Victim=" + victim.Handle
                            + "; Vehicle=" + vehicle.Handle
                            + "; SuspectPosition=" + carjacker.Position
                            + "; VictimPosition=" + victim.Position
                            + "; Distance=" + victimDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; FirstFailedStage=PhysicalVictimApproach"
                            + "; Native=TASK_GO_TO_ENTITY; Result=Timeout");
                        Fail("The suspect could not physically reach the victim before the carjacking scene timed out.");
                        return;
                    }
                    return;
                }

                if (now < incident.VehicleActivityNextTaskAt)
                    return;
                incident.VehicleActivityStage = 2;
                incident.VehicleActivityNextTaskAt = now;
                incident.VehicleActivityEntryDeadlineAt = now.AddSeconds(30);
                LogRuntime("POLICE_DISPATCH_CARJACKING_ENTRY_STAGE_STARTED",
                    "Occurrence=" + incident.Id
                    + "; Vehicle=" + vehicle.Handle
                    + "; Victim=" + victim.Handle
                    + "; ThreatDurationSeconds=2; Task=TASK_ENTER_VEHICLE");
            }

            List<Ped> suspects = LiveSuspects(incident).ToList();
            if (suspects.Count == 0)
            {
                incident.VehicleActivityStage = 4;
                return;
            }

            bool allSeated = true;
            bool entryTaskWindow = now >= incident.VehicleActivityNextTaskAt;
            bool entryTaskIssued = false;
            foreach (Ped suspect in suspects)
            {
                int seat;
                if (!incident.VehicleEntrySeatByPedHandle.TryGetValue(suspect.Handle, out seat))
                {
                    allSeated = false;
                    LogDebug("POLICE_DISPATCH_VEHICLE_ENTRY_SEAT_MISSING",
                        "Occurrence=" + incident.Id + "; Ped=" + suspect.Handle
                        + "; Vehicle=" + vehicle.Handle);
                    continue;
                }

                bool assignedSeatOccupied = false;
                try
                {
                    assignedSeatOccupied = suspect.IsInVehicle()
                        && suspect.CurrentVehicle != null
                        && suspect.CurrentVehicle.Exists()
                        && suspect.CurrentVehicle.Handle == vehicle.Handle
                        && Function.Call<int>(
                            Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, seat, false) == suspect.Handle;
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_VEHICLE_ENTRY_STATE_FAILED", ex);
                }
                if (assignedSeatOccupied)
                    continue;

                allSeated = false;
                if (!entryTaskWindow)
                    continue;
                try
                {
                    Function.Call(Hash.TASK_ENTER_VEHICLE,
                        suspect, vehicle, 15000, seat, 1.0f, 1, 0);
                    Function.Call(Hash.SET_PED_KEEP_TASK, suspect, true);
                    incident.VehicleActivityEntryAttempts++;
                    entryTaskIssued = true;
                    LogRuntime("POLICE_DISPATCH_VEHICLE_ENTRY_TASK_ISSUED",
                        "Occurrence=" + incident.Id
                        + "; Event=" + incident.Id.Split('-')[0]
                        + "; Ped=" + suspect.Handle
                        + "; Vehicle=" + vehicle.Handle
                        + "; Seat=" + seat
                        + "; Attempt=" + incident.VehicleActivityEntryAttempts
                        + "; Task=TASK_ENTER_VEHICLE; Teleport=false");
                }
                catch (Exception ex)
                {
                    entryTaskIssued = true;
                    LogException("POLICE_DISPATCH_VEHICLE_ENTRY_TASK_FAILED", ex);
                }
            }
            if (entryTaskIssued)
                incident.VehicleActivityNextTaskAt = now.AddSeconds(3);

            if (allSeated)
            {
                Ped driver = suspects.FirstOrDefault(suspect =>
                {
                    int seat;
                    return incident.VehicleEntrySeatByPedHandle.TryGetValue(suspect.Handle, out seat)
                        && seat == -1;
                });
                int driverSeatHandle = 0;
                try
                {
                    driverSeatHandle = Function.Call<int>(
                        Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, -1, false);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_VEHICLE_DRIVER_SEAT_PROBE_FAILED", ex);
                }
                if (driver == null || !driver.Exists()
                    || driverSeatHandle != driver.Handle)
                {
                    LogDebug("POLICE_DISPATCH_VEHICLE_DRIVER_NOT_READY",
                        "Occurrence=" + incident.Id + "; Vehicle=" + vehicle.Handle
                        + "; Driver=" + (driver == null ? 0 : driver.Handle)
                        + "; DriverSeatHandle=" + driverSeatHandle
                        + "; Native=GET_PED_IN_VEHICLE_SEAT(-1)");
                    return;
                }

                Ped pursuitTarget = player != null && player.Exists() && !player.IsDead
                    ? player : Game.Player.Character;
                if (pursuitTarget == null || !pursuitTarget.Exists() || pursuitTarget.IsDead)
                    return;
                try
                {
                    foreach (Ped suspect in suspects)
                        incident.SuspectResponses[suspect.Handle] = "flee";
                    driver.BlockPermanentEvents = true;
                    driver.CanSwitchWeapons = incident.Armed;
                    CapturePursuitVehicleHealthBaseline(incident, driver);
                    driver.Task.VehicleChase(pursuitTarget);
                    incident.VehicleActivityStage = 3;
                    incident.VehicleActivityNextCheckAt = DateTime.MaxValue;
                    SetStateForUnresolvedSuspects(LSPDDispatchState.SuspectFleeing);
                    ReportAudioStage(
                        "lsimmersivelife.police.pursuit.continuing",
                        incident,
                        "vehicle-activity-pursuit-started",
                        true);
                    LogRuntime("POLICE_DISPATCH_VEHICLE_ACTIVITY_PURSUIT_STARTED",
                        "Occurrence=" + incident.Id
                        + "; Event=" + incident.Id.Split('-')[0]
                        + "; Driver=" + driver.Handle
                        + "; Victim=" + (incident.Victim == null ? 0 : incident.Victim.Handle)
                        + "; VictimVehicle=" + (incident.VictimVehicle == null
                            ? 0 : incident.VictimVehicle.Handle)
                        + "; Vehicle=" + vehicle.Handle
                        + "; Occupants=" + string.Join(",", suspects
                            .Select(value => value.Handle.ToString(CultureInfo.InvariantCulture)))
                        + "; PursuitTask=VehicleChase; TerminalOutcome=false");
                }
                catch (Exception ex)
                {
                    incident.VehicleActivityStage = 4;
                    LogException("POLICE_DISPATCH_VEHICLE_ACTIVITY_PURSUIT_FAILED", ex);
                    Fail("The suspect entered the vehicle, but the Dispatch pursuit task could not start.");
                }
                return;
            }

            if (incident.VehicleActivityEntryDeadlineAt != DateTime.MinValue
                && now >= incident.VehicleActivityEntryDeadlineAt)
            {
                incident.VehicleActivityStage = 4;
                string occupants = string.Join(",", suspects.Select(suspect =>
                {
                    int seat;
                    bool hasSeat = incident.VehicleEntrySeatByPedHandle.TryGetValue(
                        suspect.Handle, out seat);
                    bool inAssignedVehicle = suspect.IsInVehicle()
                        && suspect.CurrentVehicle != null
                        && suspect.CurrentVehicle.Exists()
                        && suspect.CurrentVehicle.Handle == vehicle.Handle;
                    return suspect.Handle + ":seat=" + (hasSeat ? seat.ToString() : "missing")
                        + ":inVehicle=" + inAssignedVehicle;
                }).ToArray());
                LogRuntime("POLICE_DISPATCH_VEHICLE_ENTRY_TIMED_OUT",
                    "Occurrence=" + incident.Id
                    + "; Vehicle=" + vehicle.Handle
                    + "; Attempts=" + incident.VehicleActivityEntryAttempts
                    + "; Suspects=" + occupants
                    + "; FirstFailedStage=PhysicalVehicleEntry"
                    + "; Native=TASK_ENTER_VEHICLE/GET_PED_IN_VEHICLE_SEAT");
                Fail("The Dispatch scene could not complete physical suspect entry into the prepared vehicle.");
            }
        }

        private bool TryOrderCarjackingVictimApproach(
            LSPDDispatchEvent incident,
            Ped suspect)
        {
            Ped victim = incident == null ? null : incident.Victim;
            if (incident == null || victim == null || !victim.Exists()
                || suspect == null || !suspect.Exists())
                return false;
            try
            {
                Function.Call(Hash.TASK_GO_TO_ENTITY,
                    suspect, victim, -1, 1.6f, 1.0f, 0f, 0);
                Function.Call(Hash.SET_PED_KEEP_TASK, suspect, true);
                LogRuntime("POLICE_DISPATCH_CARJACKING_APPROACH_TASK_ISSUED",
                    "Occurrence=" + incident.Id
                    + "; Suspect=" + suspect.Handle
                    + "; Victim=" + victim.Handle
                    + "; Vehicle=" + (incident.VictimVehicle == null
                        ? 0 : incident.VictimVehicle.Handle)
                    + "; Native=TASK_GO_TO_ENTITY; StoppingRange=1.6"
                    + "; Teleport=false");
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_CARJACKING_APPROACH_TASK_FAILED", ex);
                return false;
            }
        }

        private void StartVehiclePursuitOnAccept(Ped player)
        {
            LSPDDispatchEvent incident = _incident;
            Ped pursuitTarget = player != null && player.Exists() && !player.IsDead
                ? player : Game.Player.Character;
            if (incident == null || pursuitTarget == null
                || !pursuitTarget.Exists() || pursuitTarget.IsDead)
            {
                Fail("The vehicle pursuit could not begin because the player target was unavailable.");
                return;
            }

            var started = new List<string>();
            foreach (Ped suspect in LiveSuspects(incident))
            {
                Vehicle assignedVehicle = incident.VehicleForSuspect(suspect);
                if (assignedVehicle == null || !assignedVehicle.Exists()
                    || !suspect.IsInVehicle() || suspect.CurrentVehicle == null
                    || !suspect.CurrentVehicle.Exists()
                    || suspect.CurrentVehicle.Handle != assignedVehicle.Handle)
                {
                    LogDebug("POLICE_DISPATCH_VEHICLE_PURSUIT_DRIVER_NOT_READY",
                        "Incident=" + incident.Id + "; Ped=" + suspect.Handle
                        + "; AssignedVehicle=" + (assignedVehicle == null
                            ? 0 : assignedVehicle.Handle));
                    continue;
                }

                try
                {
                    suspect.BlockPermanentEvents = true;
                    suspect.CanSwitchWeapons = incident.Armed;
                    incident.SuspectResponses[suspect.Handle] = "flee";
                    CapturePursuitVehicleHealthBaseline(incident, suspect);
                    suspect.Task.VehicleChase(pursuitTarget);
                    started.Add(suspect.Handle + ":" + assignedVehicle.Handle);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_GROUP_VEHICLE_PURSUIT_START_FAILED", ex);
                }
            }

            if (started.Count == 0)
            {
                Fail("The staged suspect vehicles had no valid drivers for the pursuit.");
                return;
            }

            incident.SceneBehaviorStage = 1;
            incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
            SetStateForUnresolvedSuspects(
                LSPDDispatchState.SuspectFleeing);
            ReportAudioStage(
                "lsimmersivelife.police.pursuit.continuing",
                incident,
                "multi-vehicle-pursuit-started",
                true);
            LogRuntime("POLICE_DISPATCH_GROUP_VEHICLE_PURSUIT_STARTED",
                "Incident=" + incident.Id
                + "; Drivers=" + string.Join(",", started)
                + "; RequestedVehicles=" + incident.SuspectVehicleCount
                + "; StartedVehicles=" + started.Count
                + "; Player=" + pursuitTarget.Handle
                + "; TerminalOutcome=false");
        }

        private static void CapturePursuitVehicleHealthBaseline(
            LSPDDispatchEvent incident,
            Ped suspect)
        {
            if (incident == null || suspect == null || !suspect.Exists())
                return;
            Vehicle vehicle = incident.VehicleForSuspect(suspect);
            if (vehicle == null || !vehicle.Exists()
                || incident.PursuitVehicleHealthAtStart.ContainsKey(vehicle.Handle))
                return;
            incident.PursuitVehicleHealthAtStart[vehicle.Handle] = vehicle.Health;
        }

        private bool IsPlayerNearVehicleEncounter(
            LSPDDispatchEvent incident,
            Ped player)
        {
            if (incident == null || !IsVehicleEntryActivity(incident.VehicleActivity)
                || incident.VehicleActivityStage < 3
                || player == null || !player.Exists() || player.IsDead)
                return false;

            foreach (Ped suspect in LiveSuspects(incident))
            {
                if (suspect.IsInVehicle())
                    continue;
                if (player.Position.DistanceTo(suspect.Position)
                    <= PairedSceneAnimationApproachDistance)
                    return true;
                Vehicle vehicle = incident.VehicleForSuspect(suspect);
                if (vehicle != null && vehicle.Exists()
                    && Math.Abs(vehicle.Speed) <= 1.25f
                    && player.Position.DistanceTo(vehicle.Position)
                        <= ArrestRadius + 8f)
                    return true;
            }
            return false;
        }

        private void StartPlayerEncounterResponse(Ped player)
        {
            LSPDDispatchEvent incident = _incident;
            bool vehicleEncounter = incident != null
                && IsVehicleEntryActivity(incident.VehicleActivity);
            if (incident == null
                || (incident.PlayerEncounterResponseStarted && !vehicleEncounter)
                || !incident.OwnedByDispatch || incident.HasConvoyCustodyHandoff
                || incident.SuspectResponseBranches == null
                || incident.SuspectResponseBranches.Count == 0
                || player == null || !player.Exists() || player.IsDead
                || (vehicleEncounter
                    ? !IsPlayerNearVehicleEncounter(incident, player)
                    : player.Position.DistanceTo(incident.Origin)
                        > PairedSceneAnimationApproachDistance))
                return;

            DateTime now = DateTime.UtcNow;
            if (incident.LastEncounterResponseCheckAt != DateTime.MinValue
                && now < incident.LastEncounterResponseCheckAt.AddSeconds(1))
                return;
            incident.LastEncounterResponseCheckAt = now;

            if (incident.SceneResolution != null
                && incident.SceneResolution.IsInteriorScene)
            {
                int playerInteriorId;
                if (_ambientWorld == null
                    || !_ambientWorld.TryGetActorInteriorId(player, out playerInteriorId)
                    || playerInteriorId != incident.SceneResolution.LiveInteriorId)
                    return;
            }

            List<Ped> suspects = LiveSuspects(incident)
                .Where(suspect => !IsArrested(incident, suspect))
                .ToList();
            if (vehicleEncounter)
            {
                if (incident.VehicleActivityStage < 3)
                    return;
                suspects = suspects.Where(suspect => !suspect.IsInVehicle()
                    && !incident.VehicleEncounterResponseHandles.Contains(suspect.Handle))
                    .ToList();
            }
            if (suspects.Count == 0)
            {
                if (!vehicleEncounter)
                    incident.PlayerEncounterResponseStarted = true;
                return;
            }

            List<string> allowed = incident.SuspectResponseBranches
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool mappedInteriorEscapeSupported = incident.SceneResolution != null
                && incident.SceneResolution.IsMappedInteriorDispatchScene
                && IsMappedInteriorDispatchBehavior(incident.SceneBehavior);
            if (incident.SceneResolution != null
                && incident.SceneResolution.IsInteriorScene
                && !mappedInteriorEscapeSupported)
                allowed.RemoveAll(value => string.Equals(
                    value, "flee", StringComparison.OrdinalIgnoreCase));
            if (allowed.Count == 0)
            {
                LogDebug(
                    "POLICE_DISPATCH_SCENE_RESPONSE_UNAVAILABLE",
                    "Event=" + incident.Id + "; Reason=No response is valid in the current interior context.");
                return;
            }

            if (!vehicleEncounter)
                incident.PlayerEncounterResponseStarted = true;
            List<string> selected = new List<string>();
            List<string> selectedResponses = new List<string>();
            for (int index = 0; index < suspects.Count; index++)
            {
                Ped suspect = suspects[index];
                List<string> candidates = new List<string>(allowed);
                string response = incident.ChooseSuspectResponse(_random, candidates);
                if (string.IsNullOrWhiteSpace(response))
                {
                    LogDebug("POLICE_DISPATCH_SUSPECT_RESPONSE_UNAVAILABLE",
                        "Event=" + incident.Id + "; Ped=" + suspect.Handle
                        + "; Allowed=" + string.Join(",", candidates));
                    continue;
                }
                incident.SuspectResponses[suspect.Handle] = response;
                selected.Add(suspect.Handle + ":" + response);
                selectedResponses.Add(response);
                if (vehicleEncounter)
                    incident.VehicleEncounterResponseHandles.Add(suspect.Handle);
                try
                {
                    suspect.BlockPermanentEvents = true;
                    if (string.Equals(response, "comply", StringComparison.OrdinalIgnoreCase))
                    {
                        suspect.Task.ClearAll();
                        suspect.CanSwitchWeapons = false;
                        suspect.Task.HandsUp(15000);
                        suspect.Task.LookAt(player, 2500);
                        AddCompliantSuspect(incident, suspect);
                        incident.SurrenderRequested = true;
                        incident.SurrenderRequestedAt = now;
                    }
                    else if (string.Equals(response, "resist", StringComparison.OrdinalIgnoreCase))
                    {
                        suspect.CanSwitchWeapons = true;
                        suspect.Task.Combat(player);
                    }
                    else if (string.Equals(response, "flee", StringComparison.OrdinalIgnoreCase))
                    {
                        Vehicle suspectVehicle = incident.VehicleForSuspect(suspect);
                        if (suspectVehicle != null && suspect.IsInVehicle()
                            && suspect.CurrentVehicle != null
                            && suspect.CurrentVehicle.Exists()
                            && suspect.CurrentVehicle.Handle == suspectVehicle.Handle)
                        {
                            CapturePursuitVehicleHealthBaseline(incident, suspect);
                            suspect.Task.VehicleChase(player);
                        }
                        else
                            suspect.Task.ReactAndFlee(player);
                    }
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_SCENE_RESPONSE_TASK_FAILED", ex);
                }
            }

            Ped fleeingFootSuspect = suspects.FirstOrDefault(suspect =>
            {
                string response;
                return incident.SuspectResponses.TryGetValue(suspect.Handle, out response)
                    && string.Equals(response, "flee", StringComparison.OrdinalIgnoreCase)
                    && incident.VehicleForSuspect(suspect) == null;
            });
            StartVictimEncounterResponse(incident, fleeingFootSuspect, player);

            IEnumerable<string> responsesForState = vehicleEncounter
                ? (IEnumerable<string>)selectedResponses : incident.SuspectResponses.Values;
            bool fleeing = responsesForState.Any(value =>
                string.Equals(value, "flee", StringComparison.OrdinalIgnoreCase));
            bool resisting = responsesForState.Any(value =>
                string.Equals(value, "resist", StringComparison.OrdinalIgnoreCase));
            bool complying = responsesForState.Any(value =>
                string.Equals(value, "comply", StringComparison.OrdinalIgnoreCase));

            if (vehicleEncounter)
            {
                incident.PlayerEncounterResponseStarted = !TrackedSuspects(incident)
                    .Where(suspect => suspect != null && suspect.Exists()
                        && !suspect.IsDead && !IsArrested(incident, suspect))
                    .Any(suspect => !incident.VehicleEncounterResponseHandles
                        .Contains(suspect.Handle));
            }

            if (fleeing)
            {
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectFleeing);
                ReportAudioStage(
                    "lsimmersivelife.police.pursuit.continuing",
                    incident,
                    "scene-pursuit-started",
                    true);
            }
            else if (resisting)
            {
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectResisting);
                ReportAudioStage(
                    "lsimmersivelife.police.resistance.resisting",
                    incident,
                    "scene-resistance-started",
                    true);
            }
            else if (complying)
            {
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.Investigating);
            }
            else
            {
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.Investigating);
            }

            LogRuntime(
                "POLICE_DISPATCH_SCENE_RESPONSES_SELECTED",
                "Event=" + incident.Id
                + "; Player=" + player.Handle
                + "; Responses=" + string.Join(",", selected)
                + "; TerminalOutcome=false");
        }

        private void StartVictimEncounterResponse(
            LSPDDispatchEvent incident,
            Ped fleeingFootSuspect,
            Ped player)
        {
            if (incident == null || incident.VictimEncounterResponseStarted
                || fleeingFootSuspect == null || !fleeingFootSuspect.Exists()
                || fleeingFootSuspect.IsDead || incident.VehicleForSuspect(fleeingFootSuspect) != null
                || (incident.SceneResolution != null
                    && incident.SceneResolution.IsInteriorScene
                    && !IsInteriorClerkSnatchBehavior(incident.SceneBehavior))
                || incident.VictimResponseBranches == null
                || incident.VictimResponseBranches.Count == 0)
                return;

            Ped victim = incident.Victim;
            if (victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0)
                return;

            incident.VictimEncounterResponseStarted = true;
            string response = incident.ChooseVictimResponse(
                _random,
                incident.VictimResponseBranches);
            if (string.IsNullOrWhiteSpace(response))
            {
                LogDebug(
                    "POLICE_DISPATCH_VICTIM_RESPONSE_UNAVAILABLE",
                    "Event=" + incident.Id
                    + "; Victim=" + victim.Handle
                    + "; Suspect=" + fleeingFootSuspect.Handle
                    + "; Reason=No positive authored victim response weight.");
                return;
            }

            try
            {
                victim.BlockPermanentEvents = true;
                if (string.Equals(response, "attempt_escape", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsInteriorClerkSnatchBehavior(incident.SceneBehavior))
                    {
                        // The active mapped-door routine assigns real nav-mesh
                        // and door tasks on the next Dispatch tick.
                        victim.Task.ClearAll();
                    }
                    else
                    {
                        Function.Call(
                            Hash.TASK_SMART_FLEE_PED,
                            victim,
                            fleeingFootSuspect,
                            45.0f,
                            -1,
                            false,
                            false);
                    }
                }
                else if (string.Equals(response, "remain_threatened", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsInteriorClerkSnatchBehavior(incident.SceneBehavior))
                    {
                        Function.Call(
                            Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                            victim,
                            fleeingFootSuspect,
                            0f,
                            -0.85f,
                            0f,
                            1.1f,
                            -1,
                            1.4f,
                            true);
                        Function.Call(Hash.SET_PED_KEEP_TASK, victim, true);
                    }
                    else
                    {
                        victim.Task.HandsUp(60000);
                        if (player != null && player.Exists() && !player.IsDead)
                            victim.Task.LookAt(player, 2500);
                    }
                }
                else
                {
                    return;
                }

                incident.VictimEncounterResponse = response;
                LogRuntime(
                    "POLICE_DISPATCH_VICTIM_RESPONSE_STARTED",
                    "Event=" + incident.Id
                    + "; Victim=" + victim.Handle
                    + "; Suspect=" + fleeingFootSuspect.Handle
                    + "; Response=" + response
                    + "; SceneBehavior=" + incident.SceneBehavior
                    + "; Action=" + (IsInteriorClerkSnatchBehavior(incident.SceneBehavior)
                        ? "MappedDoorEgress"
                        : "FootVictimReaction")
                    + "; Owner=Dispatch; TerminalOutcome=false");
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_VICTIM_RESPONSE_FAILED", ex);
            }
        }

        private static void SetScenePreparationFailure(
            LSPDDispatchEvent incident,
            string stage,
            string reason)
        {
            if (incident == null)
                return;
            incident.ScenePreparationFailureStage = stage ?? string.Empty;
            incident.ScenePreparationFailureReason = reason ?? string.Empty;
        }

        private static bool IsInteriorScenePreparationPending(
            LSPDDispatchEvent incident,
            string reason)
        {
            if (incident == null || incident.SceneResolution == null
                || !incident.SceneResolution.IsInteriorScene
                || string.IsNullOrWhiteSpace(reason)
                || !reason.StartsWith("PENDING:Stage=", StringComparison.OrdinalIgnoreCase))
                return false;

            string[] boundedPreparationStages =
            {
                "SceneRevalidationReadiness",
                "SceneRevalidationFloor",
                "InteriorReadiness",
                "InteriorFloorNavigation",
                "InteriorParticipantIdentity",
                "InteriorParticipantReadiness",
                "InteriorParticipantNavigation",
                "InteriorDoorwayReadiness"
            };
            foreach (string stage in boundedPreparationStages)
            {
                string prefix = "PENDING:Stage=" + stage;
                if (reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (reason.Length == prefix.Length || reason[prefix.Length] == ';'))
                    return true;
            }

            return false;
        }

        private bool EnsureScene(
            Ped player,
            bool startSceneBehavior = true,
            bool createSuspectBlip = true)
        {
            if (_incident == null)
                return false;
            SetScenePreparationFailure(_incident, string.Empty, string.Empty);
            if (_incident.ScenePreparationCompleted
                && _incident.Suspect != null && _incident.Suspect.Exists()
                && (!_incident.RequiresVehicle
                    || CountLiveSuspectVehicles(_incident)
                        >= Math.Max(1, _incident.SuspectVehicleCount))
                && AreAdditionalDispatchParticipantsReady(_incident))
                return true;
            if (_incident.ScenePreparationCompleted)
                _incident.ScenePreparationCompleted = false;
            if (!_incident.ScenePreparationRequested
                && !BeginScenePreparation(_incident))
            {
                SetScenePreparationFailure(
                    _incident, "CriminalProfileModel", "Profile-selected actor model failed validation or request.");
                return false;
            }

            AmbientDispatchSceneResolution refreshedScene;
            string sceneValidationFailure = "AmbientWorld is not attached by PoliceCore.";
            bool hasPreparedInteriorActor = _incident.Suspect != null
                && _incident.Suspect.Exists()
                && _incident.SceneResolution != null
                && _incident.SceneResolution.IsInteriorScene;
            bool sceneRevalidated = false;
            refreshedScene = _incident.SceneResolution;
            if (_ambientWorld != null && hasPreparedInteriorActor)
            {
                sceneRevalidated = _ambientWorld.TryRevalidatePreparedInteriorScene(
                    _incident.SceneResolution,
                    _incident.RequiresVehicle || _incident.VehicleStagingRequired,
                    _incident.RequiredPhysicalType,
                    _incident.LocationContext,
                    out sceneValidationFailure);
            }
            else if (_ambientWorld != null)
            {
                sceneRevalidated = _ambientWorld.TryRevalidateDispatchScene(
                    _incident.SceneResolution,
                    player == null || !player.Exists()
                        ? _incident.Origin : player.Position,
                    _incident.RequiresVehicle || _incident.VehicleStagingRequired,
                    out refreshedScene,
                    out sceneValidationFailure,
                    _incident.InteriorLocationId,
                    _incident.RequiredPhysicalType,
                    _incident.LocationContext,
                    _incident.SceneResolution != null
                        && string.Equals(
                            _incident.SceneResolution.LocationCategory,
                            "PlayerRadiusRoadsideFoot",
                            StringComparison.OrdinalIgnoreCase)
                        ? TrackedSuspects(_incident)
                            .Concat(_incident.Victim == null
                                ? Enumerable.Empty<Ped>()
                                : new[] { _incident.Victim })
                            .Concat(_incident.AdditionalParticipants
                                ?? new List<Ped>())
                            .Where(actor => actor != null && actor.Exists())
                            .Select(actor => actor.Handle)
                        : null);
            }
            if (!sceneRevalidated)
            {
                SetScenePreparationFailure(
                    _incident, "SceneRevalidation", sceneValidationFailure);
                LogDebug(
                    sceneValidationFailure != null
                        && sceneValidationFailure.StartsWith(
                            "PENDING:", StringComparison.OrdinalIgnoreCase)
                        ? "POLICE_DISPATCH_SCENE_REVALIDATION_PENDING"
                        : "POLICE_DISPATCH_SCENE_REVALIDATION_FAILED",
                    "Occurrence=" + _incident.Id
                    + "; CatalogInteriorId=" + (_incident.SceneResolution == null
                        ? string.Empty : _incident.SceneResolution.CatalogInteriorId)
                    + "; LiveInteriorId=" + (_incident.SceneResolution == null
                        ? 0 : _incident.SceneResolution.LiveInteriorId)
                    + "; Reason=" + sceneValidationFailure);
                return false;
            }
            _incident.SceneResolution = refreshedScene;
            _incident.Origin = refreshedScene.SceneCenter;
            if (_incident.SuspectVehicle != null && _incident.SuspectVehicle.Exists()
                && refreshedScene.HasVehicleStagingPosition
                && _incident.SuspectVehicle.Position.DistanceTo(
                    refreshedScene.VehicleStagingPosition) > 6f)
            {
                string vehicleIdentityFailure = "Stage=PreparedVehicleSceneIdentity; Vehicle="
                    + _incident.SuspectVehicle.Handle
                    + "; VehiclePosition=" + _incident.SuspectVehicle.Position
                    + "; RevalidatedStaging=" + refreshedScene.VehicleStagingPosition
                    + "; Result=PreparedVehicleNoLongerMatchesScene";
                SetScenePreparationFailure(
                    _incident, "PreparedVehicleSceneIdentity", vehicleIdentityFailure);
                LogDebug("POLICE_DISPATCH_PREPARED_VEHICLE_SCENE_REJECTED",
                    "Occurrence=" + _incident.Id + "; " + vehicleIdentityFailure);
                return false;
            }

            if (!AreAdditionalSuspectModelsLoaded(_incident))
            {
                SetScenePreparationFailure(
                    _incident, "AdditionalSuspectModel", "A profile-selected suspect model is still streaming.");
                return false;
            }
            if (_incident.Suspect != null && _incident.Suspect.Exists())
            {
                Vector3 existingScenePosition = refreshedScene.ActorPosition;
                if (refreshedScene.IsInteriorScene
                    && _ambientWorld.TryGetActorInteriorId(
                        _incident.Suspect, out int existingSuspectInteriorId)
                    && existingSuspectInteriorId != refreshedScene.LiveInteriorId)
                {
                    SetScenePreparationFailure(
                        _incident,
                        "SceneRevalidation",
                        "The partially prepared suspect is no longer inside the exact selected live interior.");
                    return false;
                }
                Vector3[] preparedSuspectPositions;
                string positionPreparationFailure;
                if (!TryResolveAdditionalSuspectPositions(
                    _incident,
                    existingScenePosition,
                    out preparedSuspectPositions,
                    out positionPreparationFailure))
                {
                    SetScenePreparationFailure(
                        _incident, "ParticipantPlacement", positionPreparationFailure);
                    LogDebug("POLICE_DISPATCH_GROUP_POSITION_REJECTED",
                        "Occurrence=" + _incident.Id
                        + "; Suspect=" + _incident.Suspect.Handle
                        + "; CatalogInteriorId=" + refreshedScene.CatalogInteriorId
                        + "; LiveInteriorId=" + refreshedScene.LiveInteriorId
                        + "; Reason=" + positionPreparationFailure);
                    return false;
                }
                if (!EnsureDispatchVictim(
                    _incident, existingScenePosition, preparedSuspectPositions))
                {
                    if (string.IsNullOrWhiteSpace(_incident.ScenePreparationFailureStage))
                        SetScenePreparationFailure(
                            _incident, "VictimPreparation", "Required victim preparation did not complete; see victim-stage diagnostic.");
                    return false;
                }
                if (!EnsureAdditionalDispatchParticipants(
                    _incident, existingScenePosition, preparedSuspectPositions))
                {
                    if (string.IsNullOrWhiteSpace(_incident.ScenePreparationFailureStage))
                        SetScenePreparationFailure(
                            _incident, "SceneParticipantPreparation", "Required shop participants did not complete; see participant-stage diagnostic.");
                    return false;
                }
                if (!EnsureAdditionalDispatchVehicles(_incident))
                {
                    SetScenePreparationFailure(
                        _incident, "SuspectVehiclePreparation", "Required suspect vehicle staging or creation failed.");
                    return false;
                }
                Model existingSuspectModel = new Model(_incident.SuspectModel);
                if (!RequestModel(existingSuspectModel, false))
                {
                    SetScenePreparationFailure(
                        _incident, "SuspectModel", "The profile-selected suspect model is still streaming or invalid.");
                    return false;
                }
                try
                {
                    string groupBuildFailure;
                    if (!CreateAdditionalSuspects(
                        _incident,
                        existingSuspectModel,
                        preparedSuspectPositions,
                        out groupBuildFailure))
                    {
                        SetScenePreparationFailure(
                            _incident, "AdditionalSuspectCreation", groupBuildFailure);
                        LogDebug("POLICE_DISPATCH_SCENE_ACTOR_SETUP_WAITING",
                            "Occurrence=" + _incident.Id
                            + "; Owner=Dispatch; Reason=" + groupBuildFailure);
                        return false;
                    }
                    string existingVehicleSeatFailure;
                    if (!PrepareVehicleEntrySeats(_incident, out existingVehicleSeatFailure))
                    {
                        SetScenePreparationFailure(
                            _incident, "VehicleCapacity", existingVehicleSeatFailure);
                        LogDebug("POLICE_DISPATCH_VEHICLE_CAPACITY_REJECTED",
                            "Occurrence=" + _incident.Id + "; " + existingVehicleSeatFailure);
                        return false;
                    }
                }
                finally
                {
                    existingSuspectModel.MarkAsNoLongerNeeded();
                }
                if (TrackedSuspects(_incident).Count() != Math.Max(1, _incident.SuspectCount))
                {
                    SetScenePreparationFailure(
                        _incident,
                        "SuspectGroupPreparation",
                        "Prepared suspect count does not match the authored group.");
                    return false;
                }
                _incident.ScenePreparationCompleted = true;
                SetScenePreparationFailure(_incident, "Complete", string.Empty);
                return true;
            }

            Model pedModel = new Model(_incident.SuspectModel);
            Model vehicleModel = new Model(_incident.VehicleModel);
            bool pedLoaded = false;
            bool vehicleLoaded = false;
            try
            {
                if (!RequestModel(pedModel, false))
                {
                    SetScenePreparationFailure(
                        _incident, "SuspectModel", "Model=" + _incident.SuspectModel
                            + "; Loaded=" + pedModel.IsLoaded + "; Usable=" + IsUsableModel(pedModel, false));
                    return false;
                }
                pedLoaded = true;
                if (_incident.RequiresVehicle && !RequestModel(vehicleModel, true))
                {
                    SetScenePreparationFailure(
                        _incident, "SuspectVehicleModel", "Model=" + _incident.VehicleModel
                            + "; Loaded=" + vehicleModel.IsLoaded + "; Usable=" + IsUsableModel(vehicleModel, true));
                    return false;
                }
                vehicleLoaded = _incident.RequiresVehicle;

                Vector3 spawn = refreshedScene.ActorPosition;
                Vector3[] preparedSuspectPositions;
                string positionPreparationFailure;
                if (!TryResolveAdditionalSuspectPositions(
                    _incident,
                    spawn,
                    out preparedSuspectPositions,
                    out positionPreparationFailure))
                {
                    SetScenePreparationFailure(
                        _incident, "ParticipantPlacement", positionPreparationFailure);
                    LogDebug("POLICE_DISPATCH_GROUP_POSITION_REJECTED",
                        "Occurrence=" + _incident.Id
                        + "; CatalogInteriorId=" + refreshedScene.CatalogInteriorId
                        + "; LiveInteriorId=" + refreshedScene.LiveInteriorId
                        + "; Reason=" + positionPreparationFailure);
                    return false;
                }

                if (_incident.RequiresVehicle)
                {
                    if (!refreshedScene.HasVehicleStagingPosition || _ambientWorld == null)
                    {
                        SetScenePreparationFailure(
                            _incident, "SuspectVehicleStaging",
                            "The incident requires a validated roadside vehicle position and the WorldAmbient placement owner.");
                        return false;
                    }
                    Vehicle vehicle;
                    string vehiclePlacementFailure;
                    if (!_ambientWorld.TryCreateDispatchSceneVehicle(
                        _incident.Id, vehicleModel, refreshedScene,
                        out vehicle, out vehiclePlacementFailure))
                    {
                        SetScenePreparationFailure(
                            _incident,
                            vehiclePlacementFailure.StartsWith("Stage=VehicleGroundProbe", StringComparison.OrdinalIgnoreCase)
                                || vehiclePlacementFailure.StartsWith("Stage=VehicleOccupancy", StringComparison.OrdinalIgnoreCase)
                                || vehiclePlacementFailure.StartsWith("Stage=VehiclePlacementRevalidation", StringComparison.OrdinalIgnoreCase)
                                ? "SuspectVehiclePlacement" : "SuspectVehicleCreation",
                            vehiclePlacementFailure);
                        LogDebug("POLICE_DISPATCH_VEHICLE_PREPARATION_REJECTED",
                            "Occurrence=" + _incident.Id
                            + "; Event=" + _incident.Id.Split('-')[0]
                            + "; Location=" + refreshedScene.LocationId
                            + "; Model=" + vehicleModel.Hash
                            + "; " + vehiclePlacementFailure);
                        return false;
                    }
                    _incident.SuspectVehicle = vehicle;
                    if (!_incident.SuspectVehicles.Any(value => value != null
                        && value.Exists() && value.Handle == vehicle.Handle))
                        _incident.SuspectVehicles.Add(vehicle);
                    _incident.OwnedByDispatch = true;
                    bool entersVehicleAfterOffer = IsVehicleEntryActivity(_incident.VehicleActivity);
                    Ped suspect = refreshedScene.IsInteriorScene || entersVehicleAfterOffer
                        ? World.CreatePed(pedModel, spawn)
                        : vehicle.CreatePedOnSeat(VehicleSeat.Driver, pedModel);
                    if (suspect == null || !suspect.Exists())
                    {
                        vehicle.Delete();
                        _incident.SuspectVehicle = null;
                        SetScenePreparationFailure(
                            _incident, "SuspectEntityCreation", "GTA did not create the suspect in the selected vehicle or interior.");
                        return false;
                    }
                    suspect.IsPersistent = true;
                    suspect.BlockPermanentEvents = true;
                    _incident.Suspect = suspect;
                    RegisterSuspect(_incident, suspect);
                    RegisterSuspectVehicle(_incident, suspect, vehicle);
                    ApplySceneWeapon(suspect);
                }
                else
                {
                    Ped suspect = World.CreatePed(pedModel, spawn);
                    if (suspect == null || !suspect.Exists())
                    {
                        SetScenePreparationFailure(
                            _incident, "SuspectEntityCreation", "GTA did not create the suspect at the validated scene position.");
                        return false;
                    }
                    suspect.IsPersistent = true;
                    suspect.BlockPermanentEvents = true;
                    _incident.Suspect = suspect;
                    RegisterSuspect(_incident, suspect);
                    _incident.OwnedByDispatch = true;
                    ApplySceneWeapon(suspect);
                }

                if (!EnsureAdditionalDispatchVehicles(_incident))
                {
                    SetScenePreparationFailure(
                        _incident, "SuspectVehiclePreparation", "Required suspect vehicle staging or creation failed.");
                    return false;
                }

                if (!EnsureDispatchVictim(_incident, spawn, preparedSuspectPositions))
                {
                    if (string.IsNullOrWhiteSpace(_incident.ScenePreparationFailureStage))
                        SetScenePreparationFailure(
                            _incident, "VictimPreparation", "Required victim preparation did not complete; see victim-stage diagnostic.");
                    return false;
                }

                if (!EnsureAdditionalDispatchParticipants(
                    _incident, spawn, preparedSuspectPositions))
                {
                    if (string.IsNullOrWhiteSpace(_incident.ScenePreparationFailureStage))
                        SetScenePreparationFailure(
                            _incident, "SceneParticipantPreparation", "Required shop participants did not complete; see participant-stage diagnostic.");
                    return false;
                }

                string groupBuildFailure;
                if (!CreateAdditionalSuspects(
                        _incident,
                        pedModel,
                        preparedSuspectPositions,
                        out groupBuildFailure))
                {
                    SetScenePreparationFailure(
                        _incident, "AdditionalSuspectCreation", groupBuildFailure);
                    LogDebug("POLICE_DISPATCH_SCENE_ACTOR_SETUP_WAITING",
                        "Occurrence=" + _incident.Id
                        + "; Owner=Dispatch; Reason=" + groupBuildFailure);
                    return false;
                }

                string vehicleSeatFailure;
                if (!PrepareVehicleEntrySeats(_incident, out vehicleSeatFailure))
                {
                    SetScenePreparationFailure(
                        _incident, "VehicleCapacity", vehicleSeatFailure);
                    LogDebug("POLICE_DISPATCH_VEHICLE_CAPACITY_REJECTED",
                        "Occurrence=" + _incident.Id + "; " + vehicleSeatFailure);
                    return false;
                }

                if (TrackedSuspects(_incident).Count() != Math.Max(1, _incident.SuspectCount))
                {
                    SetScenePreparationFailure(
                        _incident, "SuspectGroupPreparation", "Prepared suspect count does not match the authored group.");
                    return false;
                }

                if (createSuspectBlip)
                    SetSuspectBlip(_incident.Suspect);
                _incident.ScenePreparationCompleted = true;
                SetScenePreparationFailure(_incident, "Complete", string.Empty);
                if (startSceneBehavior)
                    StartSceneBehavior(player);
                if (_incident == null || !_incident.OwnedByDispatch)
                    return false;
                LogRuntime(
                    "POLICE_DISPATCH_SCENE_ENTITIES_CREATED",
                    "Ped=" + _incident.Suspect.Handle
                    + "; Suspects=" + _incident.Suspects.Count +
                    "; Vehicle=" + (_incident.SuspectVehicle == null ? 0 : _incident.SuspectVehicle.Handle)
                    + "; Interior=" + refreshedScene.IsInteriorScene
                    + "; InteriorId=" + refreshedScene.LiveInteriorId
                    + "; Location=" + refreshedScene.LocationId);
                return true;
            }
            catch (Exception ex)
            {
                SetScenePreparationFailure(
                    _incident, "SceneActorCreation", "Exception=" + ex.GetType().Name);
                LogException("POLICE_DISPATCH_SCENE_CREATE_FAILED", ex);
                CleanupIncident(_incident, false);
                return false;
            }
            finally
            {
                if (pedLoaded) pedModel.MarkAsNoLongerNeeded();
                if (vehicleLoaded) vehicleModel.MarkAsNoLongerNeeded();
            }
        }

        private static bool IsVehicleEntryActivity(string activity)
        {
            return string.Equals(activity, "carjacking", StringComparison.OrdinalIgnoreCase)
                || string.Equals(activity, "parked_theft", StringComparison.OrdinalIgnoreCase);
        }

        private bool PrepareVehicleEntrySeats(
            LSPDDispatchEvent incident,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || !IsVehicleEntryActivity(incident.VehicleActivity))
                return true;

            Vehicle vehicle = incident.SuspectVehicle;
            if (vehicle == null || !vehicle.Exists())
            {
                reason = "Stage=VehicleCapacity; VehicleForSuspects=Unavailable";
                return false;
            }

            List<Ped> suspects = TrackedSuspects(incident)
                .Where(value => value != null && value.Exists() && !value.IsDead)
                .ToList();
            if (suspects.Count == 0)
            {
                reason = "Stage=VehicleCapacity; Result=NoLiveSuspects";
                return false;
            }

            try
            {
                int passengerCapacity = Math.Max(0,
                    Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, vehicle));
                int maximumPassengerSeatsNeeded = Math.Min(
                    passengerCapacity, Math.Max(0, suspects.Count - 1));
                var freeSeats = new List<int>();
                if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, vehicle, -1, false))
                    freeSeats.Add(-1);
                for (int seat = 0; seat < maximumPassengerSeatsNeeded; seat++)
                {
                    if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, vehicle, seat, false))
                        freeSeats.Add(seat);
                }

                if (freeSeats.Count < suspects.Count)
                {
                    reason = "Stage=VehicleCapacity; Native=GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS/"
                        + "IS_VEHICLE_SEAT_FREE; Vehicle=" + vehicle.Handle
                        + "; PassengerCapacity=" + passengerCapacity
                        + "; LiveSuspects=" + suspects.Count
                        + "; FreeSeats=" + freeSeats.Count
                        + "; Result=InsufficientSeats";
                    return false;
                }

                incident.VehicleEntrySeatByPedHandle.Clear();
                for (int index = 0; index < suspects.Count; index++)
                {
                    Ped suspect = suspects[index];
                    int seat = freeSeats[index];
                    incident.VehicleEntrySeatByPedHandle[suspect.Handle] = seat;
                    RegisterSuspectVehicle(incident, suspect, vehicle);
                }

                LogRuntime("POLICE_DISPATCH_VEHICLE_ENTRY_SEATS_ASSIGNED",
                    "Occurrence=" + incident.Id
                    + "; Vehicle=" + vehicle.Handle
                    + "; Assignments=" + string.Join(",", incident.VehicleEntrySeatByPedHandle
                        .Select(value => value.Key + ":" + value.Value).ToArray())
                    + "; PassengerCapacity=" + passengerCapacity);
                return true;
            }
            catch (Exception ex)
            {
                reason = "Stage=VehicleCapacity; Native=GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS/"
                    + "IS_VEHICLE_SEAT_FREE; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        private bool EnsureAdditionalDispatchVehicles(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return false;
            if (!incident.RequiresVehicle)
                return incident.SuspectVehicleCount == 0;
            if (incident.SuspectVehicle == null || !incident.SuspectVehicle.Exists()
                || incident.SceneResolution == null
                || !incident.SceneResolution.HasVehicleStagingPosition
                || _ambientWorld == null)
                return false;

            // A failed first driver creation can leave its deleted Vehicle
            // wrapper in the occurrence list. Prune only this Dispatch-owned,
            // two-entry preparation list and keep the current primary at slot 0
            // so suspect index 1 always maps to the secondary vehicle.
            incident.SuspectVehicles.RemoveAll(value => value == null || !value.Exists());
            incident.SuspectVehicles.RemoveAll(value =>
                value.Handle == incident.SuspectVehicle.Handle);
            incident.SuspectVehicles.Insert(0, incident.SuspectVehicle);

            int required = Math.Max(1, Math.Min(2, incident.SuspectVehicleCount));
            while (CountLiveSuspectVehicles(incident) < required)
            {
                int index = CountLiveSuspectVehicles(incident);
                Vector3 staging;
                float heading;
                string stagingFailure = "A distinct vehicle staging point was not resolved.";
                if (index != 1
                    || !_ambientWorld.TryResolveAdditionalDispatchVehicleStaging(
                        incident.SceneResolution,
                        out staging,
                        out heading,
                        out stagingFailure))
                {
                    LogDebug("POLICE_DISPATCH_GROUP_VEHICLE_STAGING_FAILED",
                        "Incident=" + incident.Id
                        + "; VehicleIndex=" + index
                        + "; Reason=" + (index != 1
                            ? "The supported scene vehicle ceiling is two."
                            : stagingFailure));
                    return false;
                }

                string modelName = SuspectVehicleModelForIndex(incident, index);
                Model model = new Model(modelName);
                if (!RequestModel(model, true))
                {
                    try { model.MarkAsNoLongerNeeded(); } catch { }
                    return false;
                }
                try
                {
                    Vehicle vehicle = World.CreateVehicle(model, staging, heading);
                    if (vehicle == null || !vehicle.Exists())
                    {
                        LogDebug("POLICE_DISPATCH_GROUP_VEHICLE_CREATE_FAILED",
                            "Incident=" + incident.Id + "; VehicleIndex=" + index
                            + "; Model=" + modelName);
                        return false;
                    }
                    vehicle.IsPersistent = true;
                    vehicle.PlaceOnGround();
                    incident.SuspectVehicles.Add(vehicle);
                    LogRuntime("POLICE_DISPATCH_GROUP_VEHICLE_CREATED",
                        "Incident=" + incident.Id
                        + "; Vehicle=" + vehicle.Handle
                        + "; VehicleIndex=" + index
                        + "; Model=" + modelName
                        + "; Position=" + staging
                        + "; Required=" + required);
                }
                finally
                {
                    try { model.MarkAsNoLongerNeeded(); } catch { }
                }
            }
            return CountLiveSuspectVehicles(incident) >= required;
        }

        private static void RegisterSuspectVehicle(
            LSPDDispatchEvent incident,
            Ped suspect,
            Vehicle vehicle)
        {
            if (incident == null || suspect == null || !suspect.Exists()
                || vehicle == null || !vehicle.Exists())
                return;
            incident.SuspectVehicleByPedHandle[suspect.Handle] = vehicle.Handle;
        }

        private void ApplySceneWeapon(Ped suspect, int suspectIndex = 0)
        {
            if (suspect == null || !suspect.Exists() || !_incident.Armed)
                return;
            try
            {
                string weaponName = _incident.WeaponName;
                if (_incident.WeaponNameCandidates != null
                    && _incident.WeaponNameCandidates.Count > 0)
                    weaponName = _incident.WeaponNameCandidates[
                        suspectIndex % _incident.WeaponNameCandidates.Count];
                if (string.IsNullOrWhiteSpace(weaponName))
                    return;
                int hash = unchecked((int)StringHash.AtStringHash(weaponName, 0));
                Function.Call(Hash.GIVE_WEAPON_TO_PED, suspect, hash, 120, false, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, suspect, hash, true);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SCENE_WEAPON_FAILED", ex);
            }
        }

        private bool EnsureDispatchVictim(
            LSPDDispatchEvent incident,
            Vector3 spawn,
            IEnumerable<Vector3> preparedSuspectPositions)
        {
            if (incident == null)
                return false;
            if (incident.VictimCount <= 0)
                return true;
            if (string.IsNullOrWhiteSpace(incident.VictimPoolId) || _ambientWorld == null)
            {
                SetScenePreparationFailure(
                    incident, "VictimPool", "The cataloged victim pool or WorldAmbient owner is unavailable.");
                LogDebug("POLICE_DISPATCH_VICTIM_POOL_MISSING",
                    "Event=" + incident.Id + "; Pool=" + (incident.VictimPoolId ?? string.Empty));
                return false;
            }

            if (incident.Victim != null && incident.Victim.Exists()
                && LSPDDispatchEventDefinition.IsVictimStateReady(
                    incident.VictimStartsDead,
                    incident.Victim.IsDead,
                    incident.Victim.Health)
                && _ambientWorld.IsDispatchVictimModelInPool(
                    incident.VictimPoolId,
                    incident.Victim.Model.Hash))
            {
                if (string.Equals(incident.VehicleActivity, "carjacking",
                    StringComparison.OrdinalIgnoreCase))
                    incident.VictimVehicle = incident.SuspectVehicle;
                return true;
            }

            if (incident.Victim != null)
            {
                int previousVictimHandle = incident.Victim.Handle;
                if (incident.VictimWasExistingAmbient)
                    _ambientWorld.ReleaseDispatchCivilian(incident.Victim, incident.Id);
                else
                    try { if (incident.Victim.Exists()) incident.Victim.Delete(); } catch { }
                LogRuntime("POLICE_DISPATCH_VICTIM_REPLACED_DURING_PREPARATION",
                    "Occurrence=" + incident.Id
                    + "; Ped=" + previousVictimHandle
                    + "; Owner=" + (incident.VictimWasExistingAmbient ? "AmbientWorld" : "Dispatch")
                    + "; Reason=EntityMissingOrAuthoredStateNotReady");
                incident.Victim = null;
                incident.VictimWasExistingAmbient = false;
            }

            try
            {
                Vector3 preferredVictimPosition =
                    IsInteriorClerkSnatchBehavior(incident.SceneBehavior)
                        ? spawn + new Vector3(-1.6f, -0.5f, 0f)
                        : spawn + new Vector3(-2f, -1f, 0f);
                if (string.Equals(incident.VehicleActivity, "carjacking",
                        StringComparison.OrdinalIgnoreCase)
                    && incident.SuspectVehicle != null
                    && incident.SuspectVehicle.Exists())
                {
                    float radians = incident.SuspectVehicle.Heading * (float)Math.PI / 180f;
                    Vector3 side = new Vector3((float)Math.Cos(radians),
                        -(float)Math.Sin(radians), 0f);
                    preferredVictimPosition = incident.SuspectVehicle.Position + side * 3.5f;
                }
                Ped victim = null;
                bool reusedAmbientVictim = false;
                string reuseReason = "Nearby Ped reuse is not applicable to this scene.";
                if (!incident.VictimStartsDead
                    && !incident.VictimAmbientReuseEvaluated
                    && !string.Equals(incident.IncidentType, "kidnapping",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Bound the GTA ped query to one scan for this prepared
                    // occurrence; model-streaming retries reuse the result.
                    incident.VictimAmbientReuseEvaluated = true;
                    reusedAmbientVictim = _ambientWorld.TryClaimDispatchCivilian(
                        incident.Id,
                        incident.VictimPoolId,
                        preferredVictimPosition,
                        incident.SceneResolution,
                        TrackedSuspects(incident),
                        preparedSuspectPositions,
                        out victim,
                        out reuseReason);
                    if (reusedAmbientVictim
                        && string.Equals(incident.VehicleActivity, "carjacking",
                            StringComparison.OrdinalIgnoreCase)
                        && (incident.SuspectVehicle == null || !incident.SuspectVehicle.Exists()
                            || victim == null || !victim.Exists()
                            || victim.Position.DistanceTo(incident.SuspectVehicle.Position) > 8f))
                    {
                        if (victim != null)
                            _ambientWorld.ReleaseDispatchCivilian(victim, incident.Id);
                        victim = null;
                        reusedAmbientVictim = false;
                        reuseReason = "The nearby civilian is not physically beside the selected victim vehicle.";
                    }
                }

                if (!reusedAmbientVictim)
                {
                    LogDebug("POLICE_DISPATCH_VICTIM_GENERATED_FALLBACK",
                        "Type=" + incident.IncidentType
                        + "; Pool=" + incident.VictimPoolId
                    + "; Reason=" + reuseReason);
                    if (string.IsNullOrWhiteSpace(incident.VictimModelName))
                    {
                        string victimModelName;
                        if (!_ambientWorld.TryChooseDispatchVictimModel(
                            incident.VictimPoolId,
                            out victimModelName)
                            || string.IsNullOrWhiteSpace(victimModelName))
                        {
                            SetScenePreparationFailure(
                                incident, "VictimProfile", "The cataloged victim pool has no usable model.");
                            LogDebug("POLICE_DISPATCH_VICTIM_POOL_UNAVAILABLE",
                                "Event=" + incident.Id + "; Pool=" + incident.VictimPoolId);
                            return false;
                        }
                        incident.VictimModelName = victimModelName;
                    }

                    Model victimModel = new Model(incident.VictimModelName);
                    if (!RequestModel(victimModel, false))
                    {
                        SetScenePreparationFailure(
                            incident, "VictimModel", "Model=" + incident.VictimModelName
                                + "; Loaded=" + victimModel.IsLoaded
                                + "; Usable=" + IsUsableModel(victimModel, false));
                        return false;
                    }
                    try
                    {
                        string validationReason;
                        if (!_ambientWorld.TryCreateDispatchSceneVictim(
                            incident.Id,
                            incident.VictimPoolId,
                            victimModel,
                            preferredVictimPosition,
                            incident.SceneResolution,
                            incident.VictimStartsDead,
                            preparedSuspectPositions,
                            out victim,
                            out validationReason))
                        {
                            SetScenePreparationFailure(
                                incident,
                                validationReason.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase)
                                    ? "VictimPlacementPending" : "VictimPlacement",
                                validationReason);
                            LogDebug("POLICE_DISPATCH_VICTIM_POSITION_REJECTED",
                                "Occurrence=" + incident.Id
                                + "; Type=" + incident.IncidentType
                                + "; VictimOwner=WorldAmbient; CatalogInteriorId="
                                + incident.SceneResolution.CatalogInteriorId
                                + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                                + "; Reason=" + validationReason);
                            return false;
                        }
                        if (incident.VictimStartsDead)
                        {
                            incident.Victim = victim;
                            incident.VictimWasExistingAmbient = false;
                            LogRuntime("POLICE_DISPATCH_MURDER_VICTIM_SCENE_CREATED",
                                "Incident=" + incident.Id
                                + "; Ped=" + victim.Handle
                                + "; Pool=" + incident.VictimPoolId
                                + "; Model=" + victim.Model.Hash
                                + "; Health=" + victim.Health
                                + "; StartsDead=true; Cause=CatalogDefinedSceneState");
                        }
                    }
                    finally
                    {
                        victimModel.MarkAsNoLongerNeeded();
                    }
                }

                if (victim == null || !victim.Exists()
                    || !LSPDDispatchEventDefinition.IsVictimStateReady(
                        incident.VictimStartsDead, victim.IsDead, victim.Health))
                {
                    SetScenePreparationFailure(
                        incident, "VictimEntityCreation", "GTA did not create a valid victim in the authored state.");
                    if (reusedAmbientVictim && victim != null)
                        _ambientWorld.ReleaseDispatchCivilian(victim, incident.Id);
                    return false;
                }

                // Record ownership before assigning optional scene tasks so
                // failures still reach the correct Dispatch cleanup path.
                incident.Victim = victim;
                incident.VictimWasExistingAmbient = reusedAmbientVictim;
                if (string.Equals(incident.VehicleActivity, "carjacking",
                    StringComparison.OrdinalIgnoreCase))
                {
                    incident.VictimVehicle = incident.SuspectVehicle;
                    if (incident.VictimVehicle == null || !incident.VictimVehicle.Exists())
                    {
                        SetScenePreparationFailure(
                            incident, "VictimVehicleAssociation",
                            "The carjacking victim was prepared without the occurrence's validated vehicle.");
                        return false;
                    }
                }
                if (!incident.VictimStartsDead)
                    victim.Task.LookAt(Game.Player.Character, 3000);
                if (!incident.VictimStartsDead
                    && (incident.IncidentType == "carjacking"
                    || incident.IncidentType == "bank_robbery"
                    || incident.IncidentType == "store_robbery"
                    || incident.IncidentType == "kidnapping"))
                    victim.Task.HandsUp(8000);
                if (incident.IncidentType == "kidnapping"
                    && (incident.SceneResolution == null
                        || !incident.SceneResolution.IsInteriorScene)
                    && incident.SuspectVehicle != null
                    && incident.SuspectVehicle.Exists())
                {
                    victim.SetIntoVehicle(incident.SuspectVehicle, VehicleSeat.RightRear);
                    victim.BlockPermanentEvents = true;
                }
                LogRuntime(
                    reusedAmbientVictim
                        ? "POLICE_DISPATCH_SCENE_VICTIM_REUSED"
                        : "POLICE_DISPATCH_SCENE_VICTIM_CREATED",
                    "Type=" + incident.IncidentType
                    + "; Pool=" + incident.VictimPoolId
                    + "; Model=" + victim.Model.Hash
                    + "; Ped=" + victim.Handle
                    + "; Vehicle=" + (incident.VictimVehicle == null
                        ? 0 : incident.VictimVehicle.Handle)
                    + (reusedAmbientVictim ? "; Source=NearbyAmbientPed" : "; Source=GeneratedFallback"));
                return true;
            }
            catch (Exception ex)
            {
                SetScenePreparationFailure(
                    incident, "VictimPreparation", "Exception=" + ex.GetType().Name);
                LogException("POLICE_DISPATCH_VICTIM_CREATE_FAILED", ex);
                return false;
            }
        }

        private bool AreAdditionalDispatchParticipantsReady(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return false;
            int required = Math.Max(0, Math.Min(3, incident.SceneParticipantCount));
            if (required == 0)
                return true;
            if (_ambientWorld == null || incident.SceneResolution == null
                || !incident.SceneResolution.IsInteriorScene
                || incident.SceneResolution.LiveInteriorId == 0
                || string.IsNullOrWhiteSpace(incident.SceneParticipantPoolId)
                || incident.AdditionalParticipants == null
                || incident.AdditionalParticipants.Count < required)
                return false;

            var handles = new HashSet<int>();
            for (int index = 0; index < required; index++)
            {
                Ped participant = incident.AdditionalParticipants[index];
                int liveInteriorId;
                if (participant == null || !participant.Exists()
                    || participant.IsDead || participant.Health <= 0
                    || !handles.Add(participant.Handle)
                    || !_ambientWorld.IsDispatchVictimModelInPool(
                        incident.SceneParticipantPoolId, participant.Model.Hash)
                    || !_ambientWorld.TryGetActorInteriorId(participant, out liveInteriorId)
                    || liveInteriorId != incident.SceneResolution.LiveInteriorId)
                    return false;
            }
            return true;
        }

        private bool EnsureAdditionalDispatchParticipants(
            LSPDDispatchEvent incident,
            Vector3 spawn,
            IEnumerable<Vector3> preparedSuspectPositions)
        {
            if (incident == null)
                return false;
            int required = Math.Max(0, Math.Min(3, incident.SceneParticipantCount));
            if (required == 0)
                return true;
            if (_ambientWorld == null || incident.SceneResolution == null
                || !incident.SceneResolution.IsInteriorScene
                || incident.SceneResolution.LiveInteriorId == 0
                || string.IsNullOrWhiteSpace(incident.SceneParticipantPoolId))
            {
                SetScenePreparationFailure(
                    incident,
                    "SceneParticipantInteriorIdentity",
                    "A typed shop participant requires the selected interior scene and its exact nonzero live interior ID.");
                LogDebug("POLICE_DISPATCH_SCENE_PARTICIPANT_IDENTITY_REJECTED",
                    "Occurrence=" + incident.Id
                    + "; CatalogInteriorId=" + (incident.SceneResolution == null
                        ? string.Empty : incident.SceneResolution.CatalogInteriorId)
                    + "; LiveInteriorId=" + (incident.SceneResolution == null
                        ? 0 : incident.SceneResolution.LiveInteriorId)
                    + "; Pool=" + incident.SceneParticipantPoolId);
                return false;
            }

            while (incident.AdditionalParticipants.Count < required)
                incident.AdditionalParticipants.Add(null);
            while (incident.SceneParticipantModelNames.Count < required)
                incident.SceneParticipantModelNames.Add(string.Empty);

            var reservedPositions = new List<Vector3>(
                preparedSuspectPositions ?? Enumerable.Empty<Vector3>());
            if (incident.Victim != null && incident.Victim.Exists())
                reservedPositions.Add(incident.Victim.Position);

            for (int index = 0; index < required; index++)
            {
                Ped participant = incident.AdditionalParticipants[index];
                if (participant != null && participant.Exists())
                {
                    int actualInteriorId = 0;
                    bool ready = !participant.IsDead && participant.Health > 0
                        && _ambientWorld.IsDispatchVictimModelInPool(
                            incident.SceneParticipantPoolId, participant.Model.Hash)
                        && _ambientWorld.TryGetActorInteriorId(
                            participant, out actualInteriorId)
                        && actualInteriorId == incident.SceneResolution.LiveInteriorId;
                    if (ready)
                    {
                        reservedPositions.Add(participant.Position);
                        continue;
                    }

                    int staleHandle = participant.Handle;
                    bool borrowedAmbient = incident.ExistingAmbientSceneParticipantHandles
                        .Contains(staleHandle);
                    if (borrowedAmbient)
                        _ambientWorld.ReleaseDispatchCivilian(participant, incident.Id);
                    else
                        try { participant.Delete(); } catch { }
                    LogRuntime("POLICE_DISPATCH_SCENE_PARTICIPANT_REPLACED_DURING_PREPARATION",
                        "Occurrence=" + incident.Id
                        + "; Entity=Ped; Handle=" + staleHandle
                        + "; Owner=" + (borrowedAmbient ? "AmbientWorldClaim" : "Dispatch")
                        + "; Reason=MissingDeadWrongPoolOrWrongLiveInterior"
                        + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                        + "; ExpectedLiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                        + "; ActualLiveInteriorId=" + (actualInteriorId == 0
                            ? "Unavailable" : actualInteriorId.ToString(CultureInfo.InvariantCulture)));
                    incident.ExistingAmbientSceneParticipantHandles.Remove(staleHandle);
                    incident.SceneParticipantBehaviorInitializedHandles.Remove(staleHandle);
                    incident.AdditionalParticipants[index] = null;
                    participant = null;
                }
                else if (participant != null)
                {
                    int staleHandle = participant.Handle;
                    bool borrowedAmbient = incident.ExistingAmbientSceneParticipantHandles
                        .Contains(staleHandle);
                    if (borrowedAmbient)
                        _ambientWorld.ReleaseDispatchCivilian(participant, incident.Id);
                    LogRuntime("POLICE_DISPATCH_SCENE_PARTICIPANT_DISAPPEARED_DURING_PREPARATION",
                        "Occurrence=" + incident.Id
                        + "; Entity=Ped; Handle=" + staleHandle
                        + "; Owner=" + (borrowedAmbient ? "AmbientWorldClaim" : "Dispatch")
                        + "; Reason=EntityMissingBeforeSceneReady");
                    incident.ExistingAmbientSceneParticipantHandles.Remove(staleHandle);
                    incident.SceneParticipantBehaviorInitializedHandles.Remove(staleHandle);
                    incident.AdditionalParticipants[index] = null;
                    participant = null;
                }

                if (participant == null
                    && incident.SceneParticipantAmbientClaimAttemptedIndexes.Add(index))
                {
                    Vector3 ambientPreferred = spawn
                        + DispatchStoreParticipantOffsets[index % DispatchStoreParticipantOffsets.Length];
                    var protectedActors = TrackedSuspects(incident).ToList();
                    if (incident.Victim != null && incident.Victim.Exists())
                        protectedActors.Add(incident.Victim);
                    protectedActors.AddRange(incident.AdditionalParticipants
                        .Where(value => value != null && value.Exists()));
                    string reuseReason;
                    Ped ambientParticipant;
                    if (_ambientWorld.TryClaimDispatchCivilian(
                        incident.Id,
                        incident.SceneParticipantPoolId,
                        ambientPreferred,
                        incident.SceneResolution,
                        protectedActors,
                        reservedPositions,
                        out ambientParticipant,
                        out reuseReason))
                    {
                        int ambientInteriorId;
                        if (ambientParticipant != null && ambientParticipant.Exists()
                            && _ambientWorld.TryGetActorInteriorId(
                                ambientParticipant, out ambientInteriorId)
                            && ambientInteriorId == incident.SceneResolution.LiveInteriorId
                            && _ambientWorld.IsDispatchVictimModelInPool(
                                incident.SceneParticipantPoolId,
                                ambientParticipant.Model.Hash))
                        {
                            participant = ambientParticipant;
                            incident.AdditionalParticipants[index] = participant;
                            incident.ExistingAmbientSceneParticipantHandles.Add(
                                participant.Handle);
                            participant.BlockPermanentEvents = true;
                            reservedPositions.Add(participant.Position);
                            LogRuntime("POLICE_DISPATCH_SCENE_PARTICIPANT_REUSED",
                                "Occurrence=" + incident.Id
                                + "; Entity=Ped; Handle=" + participant.Handle
                                + "; Owner=AmbientWorldClaim; Pool=" + incident.SceneParticipantPoolId
                                + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                                + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                                + "; Slot=" + index + "; Source=EligibleExistingCitizen");
                        }
                        else
                        {
                            _ambientWorld.ReleaseDispatchCivilian(
                                ambientParticipant, incident.Id);
                            reuseReason = "Claimed participant did not retain the exact live interior or authored NPC pool.";
                        }
                    }
                    if (participant == null)
                        LogDebug("POLICE_DISPATCH_SCENE_PARTICIPANT_REUSE_UNAVAILABLE",
                            "Occurrence=" + incident.Id
                            + "; Pool=" + incident.SceneParticipantPoolId
                            + "; Slot=" + index
                            + "; Reason=" + reuseReason
                            + "; GeneratedFallback=NPCDatabase");
                }

                if (participant == null)
                {
                    string modelName = incident.SceneParticipantModelNames[index];
                    if (string.IsNullOrWhiteSpace(modelName))
                    {
                        if (!_ambientWorld.TryChooseDispatchVictimModel(
                                incident.SceneParticipantPoolId, out modelName)
                            || string.IsNullOrWhiteSpace(modelName))
                        {
                            SetScenePreparationFailure(
                                incident, "SceneParticipantProfile",
                                "The NPC Database has no usable model in participant pool "
                                    + incident.SceneParticipantPoolId + ".");
                            LogDebug("POLICE_DISPATCH_SCENE_PARTICIPANT_POOL_UNAVAILABLE",
                                "Occurrence=" + incident.Id
                                + "; Pool=" + incident.SceneParticipantPoolId
                                + "; Slot=" + index
                                + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                                + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId);
                            return false;
                        }
                        incident.SceneParticipantModelNames[index] = modelName;
                    }

                    Model model = new Model(modelName);
                    if (!RequestModel(model, false))
                    {
                        SetScenePreparationFailure(
                            incident, "SceneParticipantModel",
                            "Model=" + modelName
                                + "; Loaded=" + model.IsLoaded
                                + "; Usable=" + IsUsableModel(model, false)
                                + "; Pool=" + incident.SceneParticipantPoolId
                                + "; Slot=" + index);
                        LogDebug("POLICE_DISPATCH_SCENE_PARTICIPANT_MODEL_PENDING",
                            "Occurrence=" + incident.Id
                            + "; Model=" + modelName
                            + "; Hash=" + (model.IsValid ? model.Hash : 0)
                            + "; Loaded=" + model.IsLoaded
                            + "; Pool=" + incident.SceneParticipantPoolId
                            + "; Slot=" + index);
                        return false;
                    }

                    bool clerkParticipant = string.Equals(
                            incident.SceneParticipantPoolId, "shop_clerk",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(incident.SceneParticipantPoolId, "gun_store_clerk",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(incident.SceneParticipantPoolId, "tattoo_store_clerk",
                            StringComparison.OrdinalIgnoreCase);
                    string role = clerkParticipant ? "Clerk" : "Customer";
                    Vector3 preferredPosition = spawn
                        + DispatchStoreParticipantOffsets[index % DispatchStoreParticipantOffsets.Length];
                    try
                    {
                        string placementReason;
                        if (!_ambientWorld.TryCreateDispatchSceneParticipant(
                            incident.Id,
                            role,
                            incident.SceneParticipantPoolId,
                            model,
                            preferredPosition,
                            incident.SceneResolution,
                            false,
                            reservedPositions,
                            out participant,
                            out placementReason))
                        {
                            bool placementPending = placementReason != null
                                && placementReason.StartsWith(
                                    "PENDING:", StringComparison.OrdinalIgnoreCase);
                            bool placementFailure = placementPending
                                || (placementReason != null
                                    && placementReason.IndexOf(
                                        "Stage=ParticipantPlacement",
                                        StringComparison.OrdinalIgnoreCase) >= 0);
                            SetScenePreparationFailure(
                                incident,
                                placementPending ? "ParticipantPlacementPending"
                                    : placementFailure ? "ParticipantPlacement"
                                    : "SceneParticipantCreation",
                                placementReason ?? "AmbientWorld rejected the prepared shop participant without a reason.");
                            LogDebug(
                                placementFailure
                                    ? "POLICE_DISPATCH_SCENE_PARTICIPANT_PLACEMENT_REJECTED"
                                    : "POLICE_DISPATCH_SCENE_PARTICIPANT_CREATE_FAILED",
                                "Occurrence=" + incident.Id
                                + "; Role=" + role
                                + "; Pool=" + incident.SceneParticipantPoolId
                                + "; Slot=" + index
                                + "; Model=" + modelName
                                + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                                + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                                + "; Reason=" + placementReason);
                            return false;
                        }
                    }
                    finally
                    {
                        try { model.MarkAsNoLongerNeeded(); } catch { }
                    }

                    if (participant == null || !participant.Exists())
                    {
                        SetScenePreparationFailure(
                            incident, "SceneParticipantCreation",
                            "AmbientWorld returned no live participant after validated creation.");
                        return false;
                    }
                    incident.AdditionalParticipants[index] = participant;
                    reservedPositions.Add(participant.Position);
                    LogRuntime("POLICE_DISPATCH_SCENE_PARTICIPANT_CREATED",
                        "Occurrence=" + incident.Id
                        + "; Entity=Ped; Handle=" + participant.Handle
                        + "; Owner=Dispatch; Pool=" + incident.SceneParticipantPoolId
                        + "; Model=" + participant.Model.Hash
                        + "; Slot=" + index
                        + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                        + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                        + "; Source=NPCDatabaseGeneratedFallback");
                }

                try
                {
                    participant.BlockPermanentEvents = true;
                    participant.Task.HandsUp(60000);
                }
                catch (Exception ex)
                {
                    LogDebug("POLICE_DISPATCH_SCENE_PARTICIPANT_THREAT_POSE_FAILED",
                        "Occurrence=" + incident.Id
                        + "; Handle=" + participant.Handle
                        + "; Pool=" + incident.SceneParticipantPoolId
                        + "; Reason=" + ex.GetType().Name);
                }
            }

            return AreAdditionalDispatchParticipantsReady(incident);
        }

        private bool TryResolveAdditionalSuspectPositions(
            LSPDDispatchEvent incident,
            Vector3 spawn,
            out Vector3[] positions,
            out string failureReason)
        {
            positions = null;
            failureReason = string.Empty;
            if (incident == null)
            {
                failureReason = "The Dispatch occurrence is unavailable.";
                return false;
            }

            int required = Math.Max(1, Math.Min(15, incident.SuspectCount));
            positions = new Vector3[required];
            positions[0] = spawn;
            var reservedPositions = new List<Vector3> { spawn };
            for (int index = 1; index < required; index++)
            {
                // A configured suspect vehicle supplies the actor's physical
                // spawn point; only foot participants need a validated point.
                if (index < incident.SuspectVehicleCount)
                    continue;

                Vector3 preferredPosition = spawn
                    + DispatchSuspectFormationOffset(incident, index, required);
                Vector3 safePosition;
                string validationReason = "AmbientWorld is not attached by PoliceCore.";
                if (_ambientWorld == null
                    || !_ambientWorld.TryResolveDispatchParticipantPosition(
                        preferredPosition,
                        incident.SceneResolution,
                        reservedPositions,
                        out safePosition,
                        out validationReason))
                {
                    positions = null;
                    failureReason = "Stage=ParticipantPlacement; SuspectIndex=" + index
                        + "; RequestedPosition=" + preferredPosition
                        + "; Position=" + (_ambientWorld == null
                            ? "AmbientWorld is not attached by PoliceCore."
                            : validationReason);
                    return false;
                }
                positions[index] = safePosition;
                reservedPositions.Add(safePosition);
            }

            return true;
        }

        private static Vector3 DispatchSuspectFormationOffset(
            LSPDDispatchEvent incident,
            int index,
            int required)
        {
            bool pairedScene = string.Equals(
                    incident.SceneBehavior, "paired_meeting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    incident.SceneBehavior, "paired_exchange", StringComparison.OrdinalIgnoreCase);
            if (pairedScene && index == 1)
                return new Vector3(1.55f, 0f, 0f);
            if (pairedScene && index == 2)
                return new Vector3(0.4f, 2.4f, 0f);

            float angle = (float)(index * Math.PI * 2.0 / Math.Max(2, required));
            float formationRadius = incident.SceneResolution != null
                && incident.SceneResolution.IsInteriorScene
                    ? 2.25f
                    : 3.5f;
            return new Vector3(
                (float)Math.Cos(angle) * formationRadius,
                (float)Math.Sin(angle) * formationRadius,
                0f);
        }

        private bool CreateAdditionalSuspects(
            LSPDDispatchEvent incident,
            Model fallbackModel,
            Vector3[] preparedPositions,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (incident == null)
            {
                failureReason = "The Dispatch occurrence is unavailable.";
                return false;
            }
            if (fallbackModel == null || !fallbackModel.IsLoaded)
            {
                failureReason = "The selected suspect Ped model is not loaded.";
                return false;
            }
            int required = Math.Max(1, Math.Min(15, incident.SuspectCount));
            if (preparedPositions == null || preparedPositions.Length < required)
            {
                failureReason = "Required Dispatch suspect positions were not prepared.";
                return false;
            }
            for (int index = TrackedSuspects(incident).Count();
                index < required;
                index++)
            {
                Model suspectModel = null;
                try
                {
                    string modelName = SuspectModelForIndex(incident, index);
                    suspectModel = new Model(modelName);
                    if (!RequestModel(suspectModel, false))
                    {
                        if (string.IsNullOrWhiteSpace(failureReason))
                            failureReason = "Suspect model " + modelName + " is not ready for actor creation.";
                        break;
                    }
                    Vehicle assignedVehicle = index < incident.SuspectVehicleCount
                        && incident.SuspectVehicles != null
                        && index < incident.SuspectVehicles.Count
                            ? incident.SuspectVehicles[index] : null;
                    Vector3 suspectPosition = Vector3.Zero;
                    if (assignedVehicle == null || !assignedVehicle.Exists())
                    {
                        suspectPosition = preparedPositions[index];
                    }

                    Ped suspect = assignedVehicle != null && assignedVehicle.Exists()
                        ? assignedVehicle.CreatePedOnSeat(VehicleSeat.Driver, suspectModel)
                        : World.CreatePed(suspectModel, suspectPosition);
                    if (suspect == null || !suspect.Exists())
                    {
                        if (string.IsNullOrWhiteSpace(failureReason))
                            failureReason = "SuspectIndex=" + index
                                + "; GTA did not create the Ped at the validated scene position.";
                        break;
                    }
                    suspect.IsPersistent = true;
                    suspect.BlockPermanentEvents = true;
                    RegisterSuspect(incident, suspect);
                    if (assignedVehicle != null && assignedVehicle.Exists())
                        RegisterSuspectVehicle(incident, suspect, assignedVehicle);
                    ApplySceneWeapon(suspect, index);
                    LogRuntime(
                        assignedVehicle != null && assignedVehicle.Exists()
                            ? "POLICE_DISPATCH_GROUP_VEHICLE_DRIVER_CREATED"
                            : "POLICE_DISPATCH_GROUP_SUSPECT_CREATED",
                        "Type=" + incident.IncidentType + "; Ped=" + suspect.Handle
                        + "; Index=" + (index + 1) + "; Total=" + required
                        + "; Vehicle=" + (assignedVehicle == null || !assignedVehicle.Exists()
                            ? 0 : assignedVehicle.Handle));
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrWhiteSpace(failureReason))
                        failureReason = "SuspectIndex=" + index
                            + "; Creation exception=" + ex.GetType().Name;
                    LogException("POLICE_DISPATCH_GROUP_SUSPECT_FAILED", ex);
                    break;
                }
                finally
                {
                    if (suspectModel != null)
                    {
                        try { suspectModel.MarkAsNoLongerNeeded(); } catch { }
                    }
                }
            }

            int readySuspects = TrackedSuspects(incident).Count();
            if (readySuspects < required)
            {
                if (string.IsNullOrWhiteSpace(failureReason))
                    failureReason = "Only " + readySuspects + " of " + required
                        + " required suspect actors are valid.";
                LogDebug(
                    "POLICE_DISPATCH_GROUP_SUSPECT_BUILD_INCOMPLETE",
                    "Incident=" + incident.Id
                    + "; Required=" + required
                    + "; Ready=" + readySuspects
                    + "; Reason=" + failureReason);
                return false;
            }

            return true;
        }

        private static bool AreAdditionalSuspectModelsLoaded(
            LSPDDispatchEvent incident)
        {
            bool ready = true;
            foreach (string modelName in RequiredAdditionalSuspectModels(incident))
            {
                Model model = new Model(modelName);
                if (!IsUsableModel(model, false))
                    continue;
                if (!model.IsLoaded)
                {
                    model.Request();
                    ready = false;
                }
            }
            for (int index = 1;
                incident != null && index < incident.SuspectVehicleCount;
                index++)
            {
                Model model = new Model(SuspectVehicleModelForIndex(incident, index));
                if (!IsUsableModel(model, true))
                    continue;
                if (!model.IsLoaded)
                {
                    model.Request();
                    ready = false;
                }
            }
            return ready;
        }

        private static IEnumerable<string> RequiredAdditionalSuspectModels(
            LSPDDispatchEvent incident)
        {
            if (incident == null)
                yield break;
            int required = Math.Max(1, incident.SuspectCount);
            for (int index = 1; index < required; index++)
                yield return SuspectModelForIndex(incident, index);
        }

        private static string SuspectModelForIndex(
            LSPDDispatchEvent incident,
            int index)
        {
            string modelName = incident == null
                ? string.Empty : incident.SuspectModel;
            if (incident == null || incident.SuspectModelCandidates == null
                || incident.SuspectModelCandidates.Count == 0)
                return modelName;
            string candidateName = incident.SuspectModelCandidates[
                Math.Max(0, index) % incident.SuspectModelCandidates.Count];
            Model candidate = new Model(candidateName);
            return IsUsableModel(candidate, false) ? candidateName : modelName;
        }

        private static string SuspectVehicleModelForIndex(
            LSPDDispatchEvent incident,
            int index)
        {
            string modelName = incident == null
                ? string.Empty : incident.VehicleModel;
            if (incident == null || incident.VehicleModelCandidates == null
                || incident.VehicleModelCandidates.Count == 0)
                return modelName;
            string candidateName = incident.VehicleModelCandidates[
                Math.Max(0, index) % incident.VehicleModelCandidates.Count];
            Model candidate = new Model(candidateName);
            return IsUsableModel(candidate, true) ? candidateName : modelName;
        }

        private void RegisterSuspect(LSPDDispatchEvent incident, Ped suspect)
        {
            if (incident == null || suspect == null || !suspect.Exists())
                return;
            if (incident.Suspects == null)
                return;
            if (!incident.Suspects.Any(value => value != null && value.Exists()
                && value.Handle == suspect.Handle))
                incident.Suspects.Add(suspect);
            CaptureDispatchSuspectInjuryState(suspect, incident);
        }

        private static IEnumerable<Ped> LiveSuspects(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return Enumerable.Empty<Ped>();
            if (incident.Suspects != null && incident.Suspects.Count > 0)
                return incident.Suspects.Where(ped => ped != null && ped.Exists()
                    && !ped.IsDead && ped.Health > 0);
            return incident.Suspect != null && incident.Suspect.Exists()
                && !incident.Suspect.IsDead && incident.Suspect.Health > 0
                ? new[] { incident.Suspect }
                : Enumerable.Empty<Ped>();
        }

        private static IEnumerable<Ped> TrackedSuspects(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return Enumerable.Empty<Ped>();
            if (incident.Suspects != null && incident.Suspects.Count > 0)
                return incident.Suspects.Where(ped => ped != null && ped.Exists());
            return incident.Suspect != null && incident.Suspect.Exists()
                ? new[] { incident.Suspect }
                : Enumerable.Empty<Ped>();
        }

        private static int CountLiveSuspectVehicles(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return 0;
            var handles = new HashSet<int>();
            if (incident.SuspectVehicles != null)
                foreach (Vehicle vehicle in incident.SuspectVehicles)
                    if (vehicle != null && vehicle.Exists())
                        handles.Add(vehicle.Handle);
            if (incident.SuspectVehicle != null && incident.SuspectVehicle.Exists())
                handles.Add(incident.SuspectVehicle.Handle);
            return handles.Count;
        }
        private static void PlaySceneActivity(Ped suspect, string type)
        {
            if (suspect == null || !suspect.Exists())
                return;
            try
            {
                string scenario = string.Equals(type, "disturbance", StringComparison.OrdinalIgnoreCase)
                    ? "WORLD_HUMAN_STAND_IMPATIENT"
                    : string.Equals(type, "shots_fired", StringComparison.OrdinalIgnoreCase)
                        ? "WORLD_HUMAN_SMOKING"
                        : null;
                if (!string.IsNullOrWhiteSpace(scenario))
                    Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, suspect, scenario, 0, true);
            }
            catch { }
        }

        private static bool RequestModel(Model model, bool vehicle)
        {
            if (!IsUsableModel(model, vehicle))
                return false;
            if (!model.IsLoaded)
                model.Request();
            return model.IsLoaded;
        }

        private static bool IsUsableModel(Model model, bool vehicle)
        {
            return model != null && model.IsValid && model.IsInCdImage
                && (vehicle ? model.IsVehicle : model.IsPed);
        }

        private List<LSPDDispatchEventDefinition> ChoosePlayerRadiusOutdoorFallbackCandidates(
            DateTime now,
            bool includeRoadScenes,
            IEnumerable<string> preferredDefinitionIds = null)
        {
            DateTime repeatAfter = now.AddMinutes(-_settings.RepeatProtectionMinutes);
            HashSet<string> preferredIds = preferredDefinitionIds == null
                ? null
                : new HashSet<string>(preferredDefinitionIds,
                    StringComparer.OrdinalIgnoreCase);
            bool IsAvailable(LSPDDispatchEventDefinition definition)
            {
                if (definition == null || !definition.Enabled
                    || definition.MinimumSuspects > _settings.MaximumSuspects
                    || definition.RequiresMappedInterior
                    || !string.IsNullOrWhiteSpace(definition.InteriorLocationId)
                    || !string.IsNullOrWhiteSpace(definition.RequiredPhysicalType)
                    || (definition.BranchIds ?? new List<string>()).Contains(
                        "dispatch-road-vehicle-speed-chaos", StringComparer.OrdinalIgnoreCase))
                    return false;
                if (preferredIds != null && preferredIds.Count > 0
                    && !preferredIds.Contains(definition.Id))
                    return false;

                DateTime lastOffered;
                return !_definitionOfferedAt.TryGetValue(definition.Id, out lastOffered)
                    || lastOffered <= repeatAfter;
            }

            List<LSPDDispatchEventDefinition> eligibleVehicle = _definitions.Where(definition =>
                IsAvailable(definition)
                && definition.RequiresVehicle
                && !string.IsNullOrWhiteSpace(definition.VehicleActivity)
                && !string.Equals(definition.VehicleActivity, "none",
                    StringComparison.OrdinalIgnoreCase)
                && (string.Equals(definition.LocationContext, "any",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(definition.LocationContext, "road_vehicle",
                        StringComparison.OrdinalIgnoreCase))).ToList();
            List<LSPDDispatchEventDefinition> eligibleRoadFoot = _definitions.Where(definition =>
                IsAvailable(definition)
                && !definition.RequiresVehicle
                && !definition.VehicleStagingRequired
                && string.Equals(definition.VehicleActivity, "none",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(definition.LocationContext, "road_foot",
                    StringComparison.OrdinalIgnoreCase)).ToList();
            List<LSPDDispatchEventDefinition> eligibleGenericFoot = _definitions.Where(definition =>
                IsAvailable(definition)
                && !definition.RequiresVehicle
                && !definition.VehicleStagingRequired
                && (string.IsNullOrWhiteSpace(definition.VehicleActivity)
                    || string.Equals(definition.VehicleActivity, "none",
                        StringComparison.OrdinalIgnoreCase))
                && string.Equals(definition.LocationContext, "any",
                    StringComparison.OrdinalIgnoreCase)).ToList();

            List<LSPDDispatchEventDefinition> movingRoad = BuildWeightedDefinitionOrder(
                eligibleVehicle.Where(definition => string.Equals(
                    definition.VehicleActivity, "moving_road",
                    StringComparison.OrdinalIgnoreCase)));
            List<LSPDDispatchEventDefinition> parkedVehicle = BuildWeightedDefinitionOrder(
                eligibleVehicle.Where(definition => !string.Equals(
                    definition.VehicleActivity, "moving_road",
                    StringComparison.OrdinalIgnoreCase)));
            List<LSPDDispatchEventDefinition> vehicleCandidates = movingRoad
                .Take(1)
                .Concat(parkedVehicle.Take(1))
                .Take(MaximumPlayerRadiusVehicleDefinitionCandidates)
                .ToList();
            List<LSPDDispatchEventDefinition> roadFootCandidates = BuildWeightedDefinitionOrder(
                eligibleRoadFoot)
                .Take(MaximumPlayerRadiusRoadFootDefinitionCandidates)
                .ToList();
            List<LSPDDispatchEventDefinition> genericFootCandidates = BuildWeightedDefinitionOrder(
                eligibleGenericFoot)
                .Take(MaximumPlayerRadiusGenericFootDefinitionCandidates)
                .ToList();
            if (!includeRoadScenes)
            {
                vehicleCandidates.Clear();
                roadFootCandidates.Clear();
            }
            List<LSPDDispatchEventDefinition> candidates = BuildWeightedDefinitionOrder(
                vehicleCandidates.Concat(roadFootCandidates).Concat(genericFootCandidates))
                .ToList();
            LogRuntime("POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_CANDIDATES_READY",
                "EligibleVehicleEvents=" + eligibleVehicle.Count
                + "; MovingRoadEvents=" + movingRoad.Count
                + "; ParkedOrRoadsideEvents=" + parkedVehicle.Count
                + "; EligibleRoadFootEvents=" + eligibleRoadFoot.Count
                + "; EligibleGenericFootEvents=" + eligibleGenericFoot.Count
                + "; SelectedVehicle=" + string.Join(",", vehicleCandidates.Select(value => value.Id))
                + "; SelectedRoadFoot=" + string.Join(",", roadFootCandidates.Select(value => value.Id))
                + "; SelectedGenericFoot=" + string.Join(",", genericFootCandidates.Select(value => value.Id))
                + "; Selected=" + string.Join(",", candidates.Select(value => value.Id))
                + "; CandidateLimit=" + (MaximumPlayerRadiusVehicleDefinitionCandidates
                    + MaximumPlayerRadiusRoadFootDefinitionCandidates
                    + MaximumPlayerRadiusGenericFootDefinitionCandidates)
                + "; RoadScenesAvailable=" + includeRoadScenes
                + "; Selection=CatalogFirstThenPlayerRadiusOutdoorFallback"
                + "; CitizenTrafficBranchesExcluded=True");
            return candidates;
        }

        private static bool TryGetPlayerRadiusRoadVehicleAnchor(
            Ped player,
            out Vector3 roadAnchor)
        {
            roadAnchor = Vector3.Zero;
            if (player == null || !player.Exists())
                return false;
            Vehicle vehicle = player.CurrentVehicle;
            Vector3 playerPosition = vehicle != null && vehicle.Exists()
                ? vehicle.Position : player.Position;
            try
            {
                if (Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                    playerPosition.X, playerPosition.Y, playerPosition.Z) != 0)
                    return false;

                if (Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                    playerPosition.X, playerPosition.Y, playerPosition.Z, 0))
                {
                    roadAnchor = playerPosition;
                    return true;
                }

                using (OutputArgument nodeOutput = new OutputArgument())
                using (OutputArgument headingOutput = new OutputArgument())
                {
                    bool found = Function.Call<bool>(
                        Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        playerPosition.X,
                        playerPosition.Y,
                        playerPosition.Z,
                        nodeOutput,
                        headingOutput,
                        1,
                        3.0f,
                        0);
                    if (!found)
                        return false;

                    Vector3 nearestRoadNode = nodeOutput.GetResult<Vector3>();
                    float roadHeading = headingOutput.GetResult<float>();
                    float distance = nearestRoadNode == Vector3.Zero
                        ? float.MaxValue : HorizontalDistance(playerPosition, nearestRoadNode);
                    if (nearestRoadNode == Vector3.Zero
                        || float.IsNaN(roadHeading) || float.IsInfinity(roadHeading)
                        || distance > PlayerRadiusVehicleRoadSnapDistance
                        || Math.Abs(nearestRoadNode.Z - playerPosition.Z) > 8f)
                        return false;

                    roadAnchor = nearestRoadNode;
                    return true;
                }
            }
            catch
            {
                roadAnchor = Vector3.Zero;
                return false;
            }
        }

        private static float HorizontalDistance(Vector3 first, Vector3 second)
        {
            float x = first.X - second.X;
            float y = first.Y - second.Y;
            return (float)Math.Sqrt((x * x) + (y * y));
        }

        private List<LSPDDispatchEventDefinition> ChooseLocationDefinitionCandidates(
            Ped player,
            int maximumCount,
            int playerInteriorId,
            bool playerInteriorIdAvailable)
        {
            var selected = new List<LSPDDispatchEventDefinition>();
            if (_definitions.Count == 0 || player == null || !player.Exists())
                return selected;

            DateTime repeatAfter = DateTime.UtcNow.AddMinutes(
                -_settings.RepeatProtectionMinutes);
            var eligible = new List<LSPDDispatchEventDefinition>();
            var nearbyInteriorDefinitionIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var interiorEligibilityFailures = new List<string>();
            foreach (LSPDDispatchEventDefinition definition in _definitions)
            {
                if (!definition.Enabled
                    || definition.MinimumSuspects > _settings.MaximumSuspects)
                    continue;
                DateTime lastOffered;
                if (_definitionOfferedAt.TryGetValue(definition.Id, out lastOffered)
                    && lastOffered > repeatAfter)
                    continue;

                string eligibilityFailure;
                if (!HasEligibleAuthoredOrNearbyFallbackArea(
                    definition,
                    player.Position,
                    playerInteriorId,
                    playerInteriorIdAvailable,
                    out eligibilityFailure))
                {
                    if (definition.RequiresMappedInterior
                        || !string.IsNullOrWhiteSpace(definition.InteriorLocationId))
                        interiorEligibilityFailures.Add(definition.Id + "="
                            + (eligibilityFailure ?? "Stage=CatalogEligibility; FirstRejectedGate=Unknown"));
                    continue;
                }

                eligible.Add(definition);
                if (definition.RequiresMappedInterior
                    || !string.IsNullOrWhiteSpace(definition.InteriorLocationId))
                    nearbyInteriorDefinitionIds.Add(definition.Id);
            }

            // Try mapped-interior branches first when a compatible doorway is
            // actually nearby. Keep the other nearby catalog events and the
            // authored-area fallback behind them so a failed interior scene
            // cannot be silently rewritten, while valid exterior calls remain
            // available through their own resolver path.
            var nearbyCatalogEligible = new List<LSPDDispatchEventDefinition>();
            foreach (LSPDDispatchEventDefinition definition in eligible)
            {
                bool interiorDefinition = definition.RequiresMappedInterior
                    || !string.IsNullOrWhiteSpace(definition.InteriorLocationId);
                if (interiorDefinition)
                {
                    if (nearbyInteriorDefinitionIds.Contains(definition.Id))
                        nearbyCatalogEligible.Add(definition);
                    continue;
                }

                if (_ambientWorld != null
                    && _ambientWorld.HasNearbyCompatibleDispatchCatalogLocation(
                        definition,
                        player.Position,
                        playerInteriorId,
                        playerInteriorIdAvailable))
                    nearbyCatalogEligible.Add(definition);
            }
            List<LSPDDispatchEventDefinition> preferredInterior = nearbyCatalogEligible
                .Where(definition => definition.RequiresMappedInterior
                    || !string.IsNullOrWhiteSpace(definition.InteriorLocationId))
                .ToList();
            if (preferredInterior.Count == 0 && interiorEligibilityFailures.Count > 0)
            {
                LogDebug(
                    "POLICE_DISPATCH_INTERIOR_CATALOG_ELIGIBILITY_REJECTED",
                    "PlayerLiveInteriorId=" + playerInteriorId
                    + "; PlayerInteriorNativeAvailable=" + playerInteriorIdAvailable
                    + "; RejectedDefinitions=" + interiorEligibilityFailures.Count
                    + "; FirstRejected=" + string.Join(" | ",
                        interiorEligibilityFailures.Take(3)));
            }
            bool playerInsideLiveInterior = playerInteriorIdAvailable
                && playerInteriorId != 0;
            List<LSPDDispatchEventDefinition> nearbyConvenienceLiquorInteriors = preferredInterior
                .Where(definition => definition.RequiresMappedInterior
                    && (string.Equals(definition.RequiredPhysicalType, "ConvenienceStore",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(definition.RequiredPhysicalType, "LiquorStore",
                            StringComparison.OrdinalIgnoreCase)))
                .ToList();
            List<LSPDDispatchEventDefinition> nearbyTattooShopInteriors = preferredInterior
                .Where(definition => definition.RequiresMappedInterior
                    && string.Equals(definition.RequiredPhysicalType, "TattooShop",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<LSPDDispatchEventDefinition> nearbyClothingStoreInteriors = preferredInterior
                .Where(definition => definition.RequiresMappedInterior
                    && string.Equals(definition.RequiredPhysicalType, "ClothingStore",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool nearbyClothingStoreCatalogSite = nearbyClothingStoreInteriors.Count > 0;
            if (!nearbyClothingStoreCatalogSite && _ambientWorld != null)
            {
                // Cooldown can remove this family from the eligible set. Probe
                // one authored ClothingStore event so a mapped clothing-store
                // entrance never falls through to an unrelated exterior call.
                LSPDDispatchEventDefinition clothingStoreProbe = _definitions.FirstOrDefault(
                    definition => definition.RequiresMappedInterior
                        && string.Equals(definition.RequiredPhysicalType, "ClothingStore",
                            StringComparison.OrdinalIgnoreCase));
                nearbyClothingStoreCatalogSite = clothingStoreProbe != null
                    && _ambientWorld.HasNearbyCompatibleDispatchCatalogLocation(
                        clothingStoreProbe,
                        player.Position,
                        playerInteriorId,
                        playerInteriorIdAvailable);
            }
            List<LSPDDispatchEventDefinition> nearbyBankInteriors = preferredInterior
                .Where(definition => definition.RequiresMappedInterior
                    && string.Equals(definition.RequiredPhysicalType, "Bank",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool nearbyBankCatalogSite = nearbyBankInteriors.Count > 0;
            if (!nearbyBankCatalogSite && _ambientWorld != null)
            {
                // Keep bank calls inside the selected mapped branch even when
                // repeat protection temporarily removes its live candidates.
                LSPDDispatchEventDefinition bankProbe = _definitions.FirstOrDefault(
                    definition => definition.RequiresMappedInterior
                        && string.Equals(definition.RequiredPhysicalType, "Bank",
                            StringComparison.OrdinalIgnoreCase));
                nearbyBankCatalogSite = bankProbe != null
                    && _ambientWorld.HasNearbyCompatibleDispatchCatalogLocation(
                        bankProbe,
                        player.Position,
                        playerInteriorId,
                        playerInteriorIdAvailable);
            }
            bool nearbyTattooShopCatalogSite = nearbyTattooShopInteriors.Count > 0;
            if (!nearbyTattooShopCatalogSite && _ambientWorld != null)
            {
                // Cooldown or another request-level filter may empty the live
                // Tattoo event pool. Probe one authored definition so a known
                // Tattoo doorway never falls through to an unrelated exterior.
                LSPDDispatchEventDefinition tattooShopProbe = _definitions.FirstOrDefault(
                    definition => definition.RequiresMappedInterior
                        && string.Equals(definition.RequiredPhysicalType, "TattooShop",
                            StringComparison.OrdinalIgnoreCase));
                nearbyTattooShopCatalogSite = tattooShopProbe != null
                    && _ambientWorld.HasNearbyCompatibleDispatchCatalogLocation(
                        tattooShopProbe,
                        player.Position,
                        playerInteriorId,
                        playerInteriorIdAvailable);
            }
            List<LSPDDispatchEventDefinition> nearbyGunStoreInteriors = preferredInterior
                .Where(definition => definition.RequiresMappedInterior
                    && string.Equals(definition.RequiredPhysicalType, "GunStore",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool nearbyGunStoreCatalogSite = nearbyGunStoreInteriors.Count > 0;
            if (!nearbyGunStoreCatalogSite && !playerInsideLiveInterior && _ambientWorld != null)
            {
                // Repeat protection can temporarily remove every typed GunStore
                // event from the eligible list. Check one authored GunStore
                // definition so that condition cannot reopen the exterior pool.
                LSPDDispatchEventDefinition gunStoreProbe = _definitions.FirstOrDefault(
                    definition => definition.RequiresMappedInterior
                        && string.Equals(definition.RequiredPhysicalType, "GunStore",
                            StringComparison.OrdinalIgnoreCase));
                nearbyGunStoreCatalogSite = gunStoreProbe != null
                    && _ambientWorld.HasNearbyCompatibleDispatchCatalogLocation(
                        gunStoreProbe,
                        player.Position,
                        playerInteriorId,
                        playerInteriorIdAvailable);
            }
            if (playerInsideLiveInterior)
            {
                // When the Player is physically inside an interior, do not
                // convert a rejected interior scene into an unrelated
                // exterior call. Only definitions that already matched a
                // compatible mapped interior remain eligible; their normal
                // scene resolver still validates floor, participants, and
                // the physical doorway before Dispatch offers the call.
                if (nearbyTattooShopCatalogSite && nearbyTattooShopInteriors.Count == 0)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_TATTOO_SHOP_INTERIOR_NO_ELIGIBLE_EVENTS",
                        "PlayerLiveInteriorId=" + playerInteriorId
                        + "; CompatibleTattooShopCatalogSite=True"
                        + "; EligibleMappedEvents=0"
                        + "; ExteriorFallback=Withheld");
                    return selected;
                }
                if (nearbyClothingStoreCatalogSite && nearbyClothingStoreInteriors.Count == 0)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_CLOTHING_STORE_INTERIOR_NO_ELIGIBLE_EVENTS",
                        "PlayerLiveInteriorId=" + playerInteriorId
                        + "; CompatibleClothingStoreCatalogSite=True"
                        + "; EligibleMappedEvents=0"
                        + "; ExteriorFallback=Withheld");
                    return selected;
                }
                if (nearbyBankCatalogSite && nearbyBankInteriors.Count == 0)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_BANK_INTERIOR_NO_ELIGIBLE_EVENTS",
                        "PlayerLiveInteriorId=" + playerInteriorId
                        + "; CompatibleBankCatalogSite=True"
                        + "; EligibleMappedEvents=0"
                        + "; ExteriorFallback=Withheld");
                    return selected;
                }
                if (preferredInterior.Count == 0)
                {
                    LogDebug(
                        "POLICE_DISPATCH_NO_INTERIOR_CANDIDATE_WHILE_INSIDE",
                        "PlayerLiveInteriorId=" + playerInteriorId
                        + "; RejectedInteriorDefinitions=" + interiorEligibilityFailures.Count
                        + "; FirstRejected=" + string.Join(" | ",
                            interiorEligibilityFailures.Take(3))
                        + "; ExteriorFallback=Withheld");
                    return selected;
                }

                List<LSPDDispatchEventDefinition> insideCandidates = nearbyTattooShopInteriors.Count > 0
                    ? nearbyTattooShopInteriors
                    : nearbyGunStoreInteriors.Count > 0
                        ? nearbyGunStoreInteriors
                        : nearbyClothingStoreInteriors.Count > 0
                            ? nearbyClothingStoreInteriors
                            : nearbyBankInteriors.Count > 0
                                ? nearbyBankInteriors : preferredInterior;
                string interiorPhysicalType = nearbyTattooShopInteriors.Count > 0
                    ? "TattooShop"
                    : nearbyGunStoreInteriors.Count > 0
                        ? "GunStore"
                        : nearbyClothingStoreInteriors.Count > 0
                            ? "ClothingStore"
                            : nearbyBankInteriors.Count > 0 ? "Bank" : "MappedInterior";
                eligible = BuildWeightedDefinitionOrder(insideCandidates);
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_ONLY_CANDIDATES_READY",
                    "PlayerLiveInteriorId=" + playerInteriorId
                    + "; CompatibleInteriorEvents=" + insideCandidates.Count
                    + "; PhysicalType=" + interiorPhysicalType
                    + "; ExteriorFallback=Withheld");
            }
            else if (nearbyTattooShopCatalogSite)
            {
                // Tattoo rooms are small mapped interiors. Keep candidate
                // selection within their one-or-two-suspect authored family.
                if (nearbyTattooShopInteriors.Count == 0)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_TATTOO_SHOP_INTERIOR_NO_ELIGIBLE_EVENTS",
                        "CompatibleTattooShopCatalogSite=True"
                        + "; EligibleMappedEvents=0"
                        + "; ExteriorFallback=Withheld");
                    return selected;
                }
                eligible = BuildWeightedDefinitionOrder(nearbyTattooShopInteriors);
                LogRuntime(
                    "POLICE_DISPATCH_TATTOO_SHOP_INTERIOR_ONLY_CANDIDATES_READY",
                    "CompatibleInteriorEvents=" + nearbyTattooShopInteriors.Count
                    + "; PhysicalType=TattooShop"
                    + "; ExteriorFallback=Withheld");
            }
            else if (nearbyGunStoreCatalogSite)
            {
                // At an eligible mapped GunStore, keep the request within its
                // authored indoor incident family instead of substituting a
                // nearby mobile or roadside robbery.
                if (nearbyGunStoreInteriors.Count == 0)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_GUN_STORE_INTERIOR_NO_ELIGIBLE_EVENTS",
                        "CompatibleGunStoreCatalogSite=True"
                        + "; EligibleMappedEvents=0"
                        + "; ExteriorFallback=Withheld");
                    return selected;
                }
                eligible = BuildWeightedDefinitionOrder(nearbyGunStoreInteriors);
                LogRuntime(
                    "POLICE_DISPATCH_GUN_STORE_INTERIOR_ONLY_CANDIDATES_READY",
                    "CompatibleInteriorEvents=" + nearbyGunStoreInteriors.Count
                    + "; PhysicalType=GunStore"
                    + "; ExteriorFallback=Withheld");
            }
            else if (nearbyClothingStoreCatalogSite)
            {
                if (nearbyClothingStoreInteriors.Count == 0)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_CLOTHING_STORE_INTERIOR_NO_ELIGIBLE_EVENTS",
                        "CompatibleClothingStoreCatalogSite=True"
                        + "; EligibleMappedEvents=0"
                        + "; ExteriorFallback=Withheld");
                    return selected;
                }
                eligible = BuildWeightedDefinitionOrder(nearbyClothingStoreInteriors);
                LogRuntime(
                    "POLICE_DISPATCH_CLOTHING_STORE_INTERIOR_ONLY_CANDIDATES_READY",
                    "CompatibleInteriorEvents=" + nearbyClothingStoreInteriors.Count
                    + "; PhysicalType=ClothingStore"
                    + "; ExteriorFallback=Withheld");
            }
            else if (nearbyBankCatalogSite)
            {
                if (nearbyBankInteriors.Count == 0)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_BANK_INTERIOR_NO_ELIGIBLE_EVENTS",
                        "CompatibleBankCatalogSite=True"
                        + "; EligibleMappedEvents=0"
                        + "; ExteriorFallback=Withheld");
                    return selected;
                }
                eligible = BuildWeightedDefinitionOrder(nearbyBankInteriors);
                LogRuntime(
                    "POLICE_DISPATCH_BANK_INTERIOR_ONLY_CANDIDATES_READY",
                    "CompatibleInteriorEvents=" + nearbyBankInteriors.Count
                    + "; PhysicalType=Bank"
                    + "; ExteriorFallback=Withheld");
            }
            else if (nearbyConvenienceLiquorInteriors.Count > 0)
            {
                // At a confirmed convenience/liquor mapped doorway, keep the
                // request inside the authored store branch family even when the
                // Player is still outside. A failed interior floor/participant
                // preflight must not silently turn this call into an exterior
                // store or road incident.
                eligible = BuildWeightedDefinitionOrder(nearbyConvenienceLiquorInteriors);
                LogRuntime(
                    "POLICE_DISPATCH_CONVENIENCE_LIQUOR_INTERIOR_ONLY_CANDIDATES_READY",
                    "CompatibleInteriorEvents=" + nearbyConvenienceLiquorInteriors.Count
                    + "; PhysicalTypes=ConvenienceStore,LiquorStore"
                    + "; ExteriorFallback=Withheld");
            }
            else if (nearbyCatalogEligible.Count > 0)
            {
                var preferredIds = new HashSet<string>(preferredInterior
                    .Select(definition => definition.Id), StringComparer.OrdinalIgnoreCase);
                var nearbyIds = new HashSet<string>(nearbyCatalogEligible
                    .Select(definition => definition.Id), StringComparer.OrdinalIgnoreCase);
                List<LSPDDispatchEventDefinition> otherNearby = nearbyCatalogEligible
                    .Where(definition => !preferredIds.Contains(definition.Id))
                    .ToList();
                List<LSPDDispatchEventDefinition> authoredFallback = eligible
                    .Where(definition => !nearbyIds.Contains(definition.Id))
                    .ToList();
                eligible = BuildWeightedDefinitionOrder(preferredInterior)
                    .Concat(BuildWeightedDefinitionOrder(otherNearby))
                    .Concat(BuildWeightedDefinitionOrder(authoredFallback))
                    .ToList();
                LogRuntime(
                    "POLICE_DISPATCH_NEARBY_CATALOG_EVENTS_READY",
                    "CompatibleEvents=" + nearbyCatalogEligible.Count
                    + "; SelectionSource=SharedLocationCatalog");
            }
            else
                eligible = BuildWeightedDefinitionOrder(eligible);

            if (eligible.Count == 0)
            {
                LogRuntime(
                    "POLICE_DISPATCH_NO_LOCATION_ELIGIBLE",
                    "No Dispatch event matches the Player's authored area, nearby-catalog fallback, and location context.");
                return selected;
            }
            return eligible.Take(Math.Max(1, maximumCount)).ToList();
        }

        private List<LSPDDispatchEventDefinition> BuildWeightedDefinitionOrder(
            IEnumerable<LSPDDispatchEventDefinition> definitions)
        {
            var remaining = (definitions ?? Enumerable.Empty<LSPDDispatchEventDefinition>())
                .ToList();
            var ordered = new List<LSPDDispatchEventDefinition>(remaining.Count);
            while (remaining.Count > 0)
            {
                int totalWeight = remaining.Sum(
                    definition => Math.Max(1, definition.SelectionWeight));
                int roll = _random.Next(totalWeight);
                int selectedIndex = remaining.Count - 1;
                for (int index = 0; index < remaining.Count; index++)
                {
                    roll -= Math.Max(1, remaining[index].SelectionWeight);
                    if (roll < 0)
                    {
                        selectedIndex = index;
                        break;
                    }
                }
                ordered.Add(remaining[selectedIndex]);
                remaining.RemoveAt(selectedIndex);
            }
            return ordered;
        }

        private bool HasEligibleAuthoredOrNearbyFallbackArea(
            LSPDDispatchEventDefinition definition,
            Vector3 playerPosition,
            int playerInteriorId,
            bool playerInteriorIdAvailable,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (definition == null)
            {
                rejectionReason = "Stage=EventDefinition; FirstRejectedGate=DefinitionUnavailable";
                return false;
            }

            if (definition.RequiresMappedInterior
                || !string.IsNullOrWhiteSpace(definition.InteriorLocationId))
            {
                if (_ambientWorld == null)
                {
                    rejectionReason = "Stage=AmbientWorld; FirstRejectedGate=OwnerUnavailable";
                    return false;
                }
                return _ambientWorld.HasNearbyCompatibleDispatchCatalogLocation(
                        definition,
                        playerPosition,
                        playerInteriorId,
                        playerInteriorIdAvailable,
                        out rejectionReason);
            }

            List<LSPDDispatchLocationDefinition> matchingAreas = new List<LSPDDispatchLocationDefinition>();
            foreach (string locationId in definition.LocationIds)
            {
                LSPDDispatchLocationDefinition area;
                if (_locations.TryGetValue(locationId, out area)
                    && definition.CanUseAreaContext(area))
                    matchingAreas.Add(area);
            }

            bool hasAuthoredArea = matchingAreas.Any(area => area.IsEligibleFor(playerPosition))
                || (matchingAreas.Count > 0
                    && matchingAreas.All(area => !area.IsEligibleFor(playerPosition)))
                || (matchingAreas.Count == 0
                    && _ambientWorld != null
                    && _ambientWorld.HasNearbyCompatibleDispatchCatalogLocation(
                        definition,
                        playerPosition,
                        playerInteriorId,
                        playerInteriorIdAvailable));
            if (!hasAuthoredArea)
                rejectionReason = "Stage=AuthoredAreaEligibility; FirstRejectedGate=NoMatchingAreaOrNearbyCatalogPoint"
                    + "; IncidentType=" + (definition.IncidentType ?? string.Empty);
            return hasAuthoredArea;
        }

        private void SetState(LSPDDispatchState state)
        {
            if (_incident == null || _incident.State == state)
                return;

            LSDeveloperRuntime.StateTransition(
                _log == null ? string.Empty : _log.SessionId,
                "Dispatch",
                "POLICE_DISPATCH_STATE",
                _incident.State.ToString(),
                state.ToString(),
                _incident.Suspect == null ? 0 : _incident.Suspect.Handle,
                _incident.Suspect == null ? 0 : _incident.Suspect.Handle,
                _incident.SuspectVehicle == null ? 0 : _incident.SuspectVehicle.Handle,
                "Type=" + _incident.IncidentType + "; Title=" + _incident.Title);

            _incident.State = state;
            _incident.StateChangedAt = DateTime.UtcNow;
            LogRuntime(
                "POLICE_DISPATCH_STATE",
                "State=" + state + " | Type=" + _incident.IncidentType + " | Title=" + _incident.Title);
        }

        private void Complete(string reason)
        {
            if (_incident == null)
                return;
            LSPDDispatchEvent incident = _incident;
            Terminate(
                incident,
                LSPDDispatchState.Completed,
                reason,
                "lsimmersivelife.police.dispatch.closed",
                "completed",
                true);
            Notify("~g~POLICE DISPATCH~s~\n" + reason + "\n~c~Unit clear.");
            LogRuntime("POLICE_DISPATCH_COMPLETED", incident.Title + " | " + reason);
        }

        private void Fail(string reason)
        {
            Fail(reason, "lsimmersivelife.police.dispatch.cancelled");
        }

        private void Fail(string reason, string terminalAudioEvent)
        {
            if (_incident == null)
                return;
            LSPDDispatchEvent incident = _incident;
            string diagnostic = "Title=" + (incident.Title ?? string.Empty)
                + "; Id=" + (incident.Id ?? string.Empty)
                + "; Type=" + (incident.IncidentType ?? string.Empty)
                + "; State=" + incident.State
                + "; OwnedByDispatch=" + incident.OwnedByDispatch
                + "; Suspect=" + DescribePed(incident.Suspect)
                + "; Vehicle=" + DescribeVehicle(incident.SuspectVehicle)
                + "; Reason=" + (reason ?? string.Empty);
            Terminate(
                incident,
                LSPDDispatchState.Failed,
                reason,
                terminalAudioEvent,
                "failed",
                true);
            Notify("~r~POLICE DISPATCH~s~\n" + reason);
            LogStateFailure("POLICE_DISPATCH_FAILED", diagnostic);
        }

        /// <summary>
        /// Closes the live assignment exactly once. Terminal audio uses a fresh
        /// scope after stale progress speech has been cancelled, so an old
        /// "received" or "en route" line cannot play after the case ended.
        /// </summary>
        private LSPDAudioResult Terminate(
            LSPDDispatchEvent incident,
            LSPDDispatchState state,
            string reason,
            string terminalAudioEvent,
            string terminalStage,
            bool deferSceneCleanup)
        {
            if (incident == null || _incident != incident)
                return LSPDAudioResult.Inactive;

            CancelIncidentInteriorAccess(incident, "DispatchTerminated");
            ClearBackupArrestOperations(incident);
            ClearPlayerHandcuffState();
            RestoreAllSuspectInjuryStates();
            SetState(state);
            if (deferSceneCleanup)
                QueueCleanup(incident, "Terminal:" + state + "; " + (reason ?? string.Empty));
            else
                CleanupIncident(incident, false);
            CleanupBlipsAndWaypoint();
            _lastCompliantTaskAt.Clear();
            _lastSecuredPoseTaskAt.Clear();
            _incident = null;
            ReleaseUnusedDispatchInteriorPins("DispatchTerminal:" + state);
            ScheduleNextOffer(DateTime.UtcNow);
            LSPDAudioResult audio = ReportTerminalAudio(
                terminalAudioEvent,
                incident,
                terminalStage);
            LogRuntime(
                "POLICE_DISPATCH_TERMINAL",
                "State=" + state + "; Title=" + incident.Title
                + "; Reason=" + (reason ?? string.Empty));
            return audio;
        }

        private void CancelIncidentInteriorAccess(LSPDDispatchEvent incident, string reason)
        {
            if (incident == null)
                return;
            if (_ambientWorld != null)
            {
                foreach (Ped suspect in TrackedSuspects(incident))
                    if (suspect != null)
                        _ambientWorld.CancelInteriorAccess(suspect, reason);
                if (incident.Victim != null)
                    _ambientWorld.CancelInteriorAccess(incident.Victim, reason);
                if (incident.AdditionalParticipants != null)
                    foreach (Ped participant in incident.AdditionalParticipants)
                        if (participant != null)
                            _ambientWorld.CancelInteriorAccess(participant, reason);
            }
            ResetInteriorExitAttempt(incident);
            ResetInteriorClerkVictimExit(incident);
        }

        private void QueueCleanup(
            LSPDDispatchEvent incident,
            string cleanupReason = "OccurrenceFinished")
        {
            if (incident == null)
                return;
            ReleaseIncidentModelRequests(incident);
            DateTime now = DateTime.UtcNow;

            // The arrested suspect and escape vehicle move to Convoy ownership
            // during custody. Scene support actors remain Dispatch-owned and
            // receive the same grace/distance cleanup after the case closes.
            if (incident.OwnedByDispatch)
            {
                foreach (Ped suspect in TrackedSuspects(incident)
                    .Where(ped => ped != null && ped.Exists()))
                {
                    bool deadAftermath = suspect.IsDead;
                    _deferredCleanup.Add(new DeferredCleanup
                    {
                        Ped = suspect,
                        OccurrenceId = incident.Id,
                        LiveInteriorId = incident.SceneResolution != null
                            && incident.SceneResolution.IsInteriorScene
                                ? incident.SceneResolution.LiveInteriorId : 0,
                        Owner = "Dispatch",
                        CleanupReason = cleanupReason,
                        Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds),
                        PreserveUntilPlayerLeavesArea = deadAftermath
                    });
                    if (deadAftermath)
                        LogRuntime("POLICE_DISPATCH_DEAD_SCENE_RETAINED",
                            "Occurrence=" + incident.Id
                            + "; Owner=Dispatch"
                            + "; Ped=" + suspect.Handle
                            + "; Cleanup=AfterPlayerLeavesSafeRadius"
                            + "; Radius=" + DeferredCleanupDistance.ToString("0.0"));
                }
                foreach (Vehicle vehicle in (incident.SuspectVehicles ?? new List<Vehicle>())
                    .Where(value => value != null && value.Exists())
                    .GroupBy(value => value.Handle)
                    .Select(group => group.First()))
                    _deferredCleanup.Add(new DeferredCleanup
                    {
                        Vehicle = vehicle,
                        OccurrenceId = incident.Id,
                        LiveInteriorId = incident.SceneResolution != null
                            && incident.SceneResolution.IsInteriorScene
                                ? incident.SceneResolution.LiveInteriorId : 0,
                        Owner = "Dispatch",
                        CleanupReason = cleanupReason,
                        Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds)
                    });
                if (incident.SuspectVehicle != null && incident.SuspectVehicle.Exists()
                    && !(incident.SuspectVehicles ?? new List<Vehicle>()).Any(value =>
                        value != null && value.Exists()
                        && value.Handle == incident.SuspectVehicle.Handle))
                    _deferredCleanup.Add(new DeferredCleanup
                    {
                        Vehicle = incident.SuspectVehicle,
                        OccurrenceId = incident.Id,
                        LiveInteriorId = incident.SceneResolution != null
                            && incident.SceneResolution.IsInteriorScene
                                ? incident.SceneResolution.LiveInteriorId : 0,
                        Owner = "Dispatch",
                        CleanupReason = cleanupReason,
                        Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds)
                    });
            }
            if (incident.Victim != null && incident.Victim.Exists())
            {
                if (incident.VictimWasExistingAmbient)
                {
                    int ambientVictimHandle = incident.Victim.Handle;
                    if (_ambientWorld != null)
                        _ambientWorld.ReleaseDispatchCivilian(incident.Victim, incident.Id);
                    LogRuntime("POLICE_DISPATCH_ACTOR_CLEANUP",
                        "Occurrence=" + incident.Id
                        + "; Entity=Ped; Handle=" + ambientVictimHandle
                        + "; Owner=AmbientWorldClaim; Reason=" + (cleanupReason ?? string.Empty)
                        + "; Deleted=false; Released=true");
                    incident.Victim = null;
                    incident.VictimWasExistingAmbient = false;
                }
                else
                {
                    bool murderVictimAftermath = incident.VictimStartsDead
                    && (incident.Victim.IsDead || incident.Victim.Health <= 0);
                    _deferredCleanup.Add(new DeferredCleanup
                    {
                        Ped = incident.Victim,
                        OccurrenceId = incident.Id,
                        LiveInteriorId = incident.SceneResolution != null
                            && incident.SceneResolution.IsInteriorScene
                                ? incident.SceneResolution.LiveInteriorId : 0,
                        Owner = "Dispatch",
                        CleanupReason = cleanupReason,
                        Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds),
                        PreserveUntilPlayerLeavesArea = murderVictimAftermath
                    });
                    if (murderVictimAftermath)
                        LogRuntime("POLICE_DISPATCH_DEAD_VICTIM_RETAINED",
                            "Occurrence=" + incident.Id
                            + "; Ped=" + incident.Victim.Handle
                            + "; Owner=Dispatch; Cleanup=AfterPlayerLeavesSafeRadius"
                            + "; Radius=" + DeferredCleanupDistance.ToString("0.0"));
                }
            }
            for (int index = 0; index < incident.AdditionalParticipants.Count; index++)
            {
                Ped participant = incident.AdditionalParticipants[index];
                if (participant == null)
                    continue;
                if (incident.ExistingAmbientSceneParticipantHandles
                    .Contains(participant.Handle))
                {
                    int borrowedHandle = participant.Handle;
                    bool released = _ambientWorld != null
                        && _ambientWorld.ReleaseDispatchCivilian(participant, incident.Id);
                    LogRuntime("POLICE_DISPATCH_ACTOR_CLEANUP",
                        "Occurrence=" + incident.Id
                        + "; Entity=Ped; Handle=" + borrowedHandle
                        + "; Owner=AmbientWorldClaim; Reason=" + (cleanupReason ?? string.Empty)
                        + "; Deleted=false; Released=" + released);
                    incident.ExistingAmbientSceneParticipantHandles.Remove(borrowedHandle);
                    incident.AdditionalParticipants[index] = null;
                    continue;
                }
                if (participant.Exists())
                    _deferredCleanup.Add(new DeferredCleanup
                    {
                        Ped = participant,
                        OccurrenceId = incident.Id,
                        LiveInteriorId = incident.SceneResolution != null
                            && incident.SceneResolution.IsInteriorScene
                                ? incident.SceneResolution.LiveInteriorId : 0,
                        Owner = "Dispatch",
                        CleanupReason = cleanupReason,
                        Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds)
                    });
            }
        }
        private void ProcessDeferredCleanup(DateTime now)
        {
            Ped player = Game.Player.Character;
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
            {
                Entity entity = item.Ped != null && item.Ped.Exists()
                    ? (Entity)item.Ped : item.Vehicle;
                if (entity == null || !entity.Exists())
                {
                    LogRuntime("POLICE_DISPATCH_ACTOR_DISAPPEARED_BEFORE_CLEANUP",
                        "Occurrence=" + item.OccurrenceId
                        + "; Handle=" + (item.Ped != null ? item.Ped.Handle
                            : item.Vehicle == null ? 0 : item.Vehicle.Handle)
                        + "; Owner=" + item.Owner
                        + "; CleanupReason=" + item.CleanupReason
                        + "; Cause=EntityMissingBeforeScheduledCleanup");
                    _deferredCleanup.Remove(item);
                    ReleaseUnusedDispatchInteriorPins("DeferredActorAlreadyMissing");
                    continue;
                }
                float distance = player == null || !player.Exists()
                    ? float.MaxValue : entity.Position.DistanceTo(player.Position);
                bool playerLeftActiveArea = distance >= DeferredCleanupDistance;
                bool ordinaryCleanupExpired = !item.PreserveUntilPlayerLeavesArea
                    && now >= item.Expires;
                if (now >= item.Earliest && (playerLeftActiveArea || ordinaryCleanupExpired))
                {
                    if (item.PreserveUntilPlayerLeavesArea)
                        LogRuntime("POLICE_DISPATCH_DEAD_SCENE_CLEANUP",
                            "Ped=" + (item.Ped == null ? 0 : item.Ped.Handle)
                            + "; Distance=" + distance.ToString("0.0")
                            + "; Radius=" + DeferredCleanupDistance.ToString("0.0")
                            + "; Reason=PlayerLeftActiveArea");
                    DeleteDeferred(item);
                    _deferredCleanup.Remove(item);
                    ReleaseUnusedDispatchInteriorPins("DeferredOccurrenceCleanup");
                }
            }
        }

        private void DeleteDeferred(DeferredCleanup item)
        {
            int handle = item.Ped != null ? item.Ped.Handle
                : item.Vehicle == null ? 0 : item.Vehicle.Handle;
            bool deleted = false;
            try
            {
                if (item.Ped != null && item.Ped.Exists())
                {
                    item.Ped.Delete();
                    deleted = true;
                }
            }
            catch { }
            if (!deleted && item.Vehicle != null && item.Vehicle.Exists())
            {
                DeleteEmptyOwnedIncidentVehicle(
                    item.Vehicle, null, "DeferredOccurrenceCleanup:" + item.CleanupReason);
                deleted = !item.Vehicle.Exists();
            }
            LogRuntime("POLICE_DISPATCH_ACTOR_CLEANUP",
                "Occurrence=" + item.OccurrenceId
                + "; Handle=" + handle
                + "; Owner=" + item.Owner
                + "; Reason=" + item.CleanupReason
                + "; Deleted=" + deleted);
        }

        private void CleanupPreparedOccurrence(
            LSPDDispatchEvent incident,
            string reason)
        {
            CancelIncidentInteriorAccess(incident, "PreparedOccurrenceCleanup:" + (reason ?? string.Empty));
            CleanupIncident(incident, false, reason);
            // A prepared offer may already own actors and markers. Release or
            // clean those occurrence resources through Dispatch cleanup while
            // preserving the Player's existing waypoint.
            if (_incident == null || ReferenceEquals(_incident, incident))
            {
                CleanupBlip(_sceneBlip);
                CleanupSuspectBlips();
                _sceneBlip = null;
            }
            LogRuntime("POLICE_DISPATCH_PREPARED_OCCURRENCE_CLEANED",
                "Incident=" + (incident == null ? string.Empty : incident.Id)
                + "; Reason=" + (reason ?? string.Empty)
                + "; DispatchBlipsCleared=true; DispatchActorsDeletedOrAmbientReleased=true"
                + "; PlayerWaypointPreserved=true");
        }

        private void DeleteEmptyOwnedIncidentVehicle(
            Vehicle vehicle,
            LSPDDispatchEvent incident,
            string reason)
        {
            if (vehicle == null || !vehicle.Exists())
                return;

            Ped[] occupants;
            try { occupants = vehicle.Occupants ?? new Ped[0]; }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_OCCURRENCE_VEHICLE_OCCUPANT_CHECK_FAILED", ex);
                return;
            }
            List<Ped> liveOccupants = occupants
                .Where(ped => ped != null && ped.Exists())
                .ToList();
            if (liveOccupants.Count > 0)
            {
                // Dispatch no longer owns an occupied vehicle merely because
                // it staged the empty shell. Release persistence and leave its
                // current actors and physical vehicle in the world.
                try
                {
                    vehicle.IsPersistent = false;
                    vehicle.MarkAsNoLongerNeeded();
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_OCCURRENCE_VEHICLE_RELEASE_FAILED", ex);
                }
                LogRuntime("POLICE_DISPATCH_OCCURRENCE_OCCUPIED_VEHICLE_RELEASED",
                    "Incident=" + (incident == null ? string.Empty : incident.Id)
                    + "; Vehicle=" + vehicle.Handle
                    + "; Occupants=" + string.Join(",", liveOccupants
                        .Select(ped => ped.Handle.ToString()).ToArray())
                    + "; Reason=" + (reason ?? string.Empty)
                    + "; Deleted=false");
                return;
            }

            bool recoveredVictimVehicle = incident != null
                && incident.VictimVehicleRecovered
                && incident.VictimVehicle != null
                && incident.VictimVehicle.Exists()
                && incident.VictimVehicle.Handle == vehicle.Handle;
            if (recoveredVictimVehicle)
            {
                try
                {
                    vehicle.IsPersistent = false;
                    vehicle.MarkAsNoLongerNeeded();
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_RECOVERED_VEHICLE_RELEASE_FAILED", ex);
                }
                LogRuntime("POLICE_DISPATCH_RECOVERED_VEHICLE_RELEASED",
                    "Occurrence=" + incident.Id
                    + "; Victim=" + (incident.Victim == null ? 0 : incident.Victim.Handle)
                    + "; Vehicle=" + vehicle.Handle
                    + "; Reason=" + (reason ?? string.Empty)
                    + "; Deleted=false; DispatchPersistenceReleased=true");
                return;
            }

            int vehicleHandle = vehicle.Handle;
            try { vehicle.Delete(); }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_OCCURRENCE_EMPTY_VEHICLE_DELETE_FAILED", ex);
                return;
            }
            LogRuntime("POLICE_DISPATCH_OCCURRENCE_EMPTY_VEHICLE_CLEANED",
                "Incident=" + (incident == null ? string.Empty : incident.Id)
                + "; Vehicle=" + vehicleHandle
                + "; Reason=" + (reason ?? string.Empty)
                + "; Occupants=0; Deleted=true");
        }

        private void CleanupIncident(
            LSPDDispatchEvent incident,
            bool defer,
            string cleanupReason = "ImmediateOccurrenceCleanup")
        {
            if (incident == null || defer)
                return;

            ReleaseIncidentModelRequests(incident);

            // Convoy owns the arrested suspect and its transport after the
            // custody handoff. Dispatch still owns the temporary victim and
            // support actors, so those are always cleaned here.
            if (incident.OwnedByDispatch)
            {
                foreach (Ped suspect in TrackedSuspects(incident).ToArray())
                {
                    int handle = suspect == null ? 0 : suspect.Handle;
                    bool deleted = false;
                    try
                    {
                        if (suspect != null && suspect.Exists())
                        {
                            suspect.Delete();
                            deleted = true;
                        }
                    }
                    catch { }
                    LogRuntime("POLICE_DISPATCH_ACTOR_CLEANUP",
                        "Occurrence=" + incident.Id
                        + "; Entity=Ped; Handle=" + handle
                        + "; Owner=Dispatch; Reason=" + (cleanupReason ?? string.Empty)
                        + "; Deleted=" + deleted);
                }
                foreach (Vehicle vehicle in (incident.SuspectVehicles ?? new List<Vehicle>())
                    .Where(value => value != null && value.Exists())
                    .GroupBy(value => value.Handle)
                    .Select(group => group.First()))
                    DeleteEmptyOwnedIncidentVehicle(
                        vehicle, incident, "ImmediateOccurrenceCleanup");
                try
                {
                    if (incident.SuspectVehicle != null && incident.SuspectVehicle.Exists()
                        && !(incident.SuspectVehicles ?? new List<Vehicle>()).Any(value =>
                            value != null && value.Exists()
                            && value.Handle == incident.SuspectVehicle.Handle))
                        DeleteEmptyOwnedIncidentVehicle(
                            incident.SuspectVehicle,
                            incident,
                            "ImmediatePrimaryOccurrenceCleanup");
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_OCCURRENCE_VEHICLE_CLEANUP_FAILED", ex);
                }
                incident.Suspect = null;
                incident.SuspectVehicle = null;
                if (incident.SuspectVehicles != null) incident.SuspectVehicles.Clear();
                if (incident.SuspectVehicleByPedHandle != null)
                    incident.SuspectVehicleByPedHandle.Clear();
                if (incident.VehicleEntrySeatByPedHandle != null)
                    incident.VehicleEntrySeatByPedHandle.Clear();
                if (incident.VehicleEncounterResponseHandles != null)
                    incident.VehicleEncounterResponseHandles.Clear();
                if (incident.Suspects != null) incident.Suspects.Clear();
                if (incident.ArrestedSuspects != null) incident.ArrestedSuspects.Clear();
                if (incident.CompliantSuspects != null) incident.CompliantSuspects.Clear();
                if (incident.PursuitContainedSuspectHandles != null)
                    incident.PursuitContainedSuspectHandles.Clear();
                if (incident.PursuitVehicleHealthAtStart != null)
                    incident.PursuitVehicleHealthAtStart.Clear();
                if (incident.PursuitVehicleStoppedAt != null)
                    incident.PursuitVehicleStoppedAt.Clear();
                if (incident.PursuitVehicleExitTaskAt != null)
                    incident.PursuitVehicleExitTaskAt.Clear();
                if (incident.PursuitVehicleExitRequestedHandles != null)
                    incident.PursuitVehicleExitRequestedHandles.Clear();
            }
            if (incident.VictimWasExistingAmbient)
            {
                bool released = _ambientWorld != null
                    && _ambientWorld.ReleaseDispatchCivilian(incident.Victim, incident.Id);
                LogRuntime("POLICE_DISPATCH_ACTOR_CLEANUP",
                    "Occurrence=" + incident.Id
                    + "; Entity=Ped; Handle=" + (incident.Victim == null ? 0 : incident.Victim.Handle)
                    + "; Owner=AmbientWorldClaim; Reason=" + (cleanupReason ?? string.Empty)
                    + "; Deleted=false; Released=" + released);
            }
            else
            {
                int handle = incident.Victim == null ? 0 : incident.Victim.Handle;
                bool deleted = false;
                try
                {
                    if (incident.Victim != null && incident.Victim.Exists())
                    {
                        incident.Victim.Delete();
                        deleted = true;
                    }
                }
                catch { }
                if (incident.Victim != null)
                    LogRuntime("POLICE_DISPATCH_ACTOR_CLEANUP",
                        "Occurrence=" + incident.Id
                        + "; Entity=Ped; Handle=" + handle
                        + "; Owner=Dispatch; Reason=" + (cleanupReason ?? string.Empty)
                        + "; Deleted=" + deleted);
            }
            foreach (Ped participant in incident.AdditionalParticipants)
            {
                int handle = participant == null ? 0 : participant.Handle;
                bool deleted = false;
                bool released = false;
                bool borrowedAmbient = participant != null
                    && incident.ExistingAmbientSceneParticipantHandles.Contains(handle);
                try
                {
                    if (borrowedAmbient)
                    {
                        if (_ambientWorld != null)
                            released = _ambientWorld.ReleaseDispatchCivilian(
                                participant, incident.Id);
                    }
                    else if (participant != null && participant.Exists())
                    {
                        participant.Delete();
                        deleted = true;
                    }
                }
                catch { }
                if (participant != null)
                    LogRuntime("POLICE_DISPATCH_ACTOR_CLEANUP",
                        "Occurrence=" + incident.Id
                        + "; Entity=Ped; Handle=" + handle
                        + "; Owner=" + (borrowedAmbient ? "AmbientWorldClaim" : "Dispatch")
                        + "; Reason=" + (cleanupReason ?? string.Empty)
                        + "; Deleted=" + deleted
                        + (borrowedAmbient ? "; Released=" + released : string.Empty));
            }
            incident.Victim = null;
            incident.VictimWasExistingAmbient = false;
            incident.VictimVehicle = null;
            incident.AdditionalParticipants.Clear();
            incident.ExistingAmbientSceneParticipantHandles.Clear();
            incident.SceneParticipantAmbientClaimAttemptedIndexes.Clear();
            incident.SceneParticipantBehaviorInitializedHandles.Clear();
            incident.SceneParticipantModelNames.Clear();
            ReleaseUnusedDispatchInteriorPins("OccurrenceCleanup:" + (cleanupReason ?? string.Empty));
        }

        private static void ReleaseIncidentModelRequests(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return;
            var modelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(incident.SuspectModel))
                modelNames.Add(incident.SuspectModel);
            if (incident.SuspectModelCandidates != null)
                foreach (string candidate in incident.SuspectModelCandidates)
                    if (!string.IsNullOrWhiteSpace(candidate))
                        modelNames.Add(candidate);
            if (incident.RequiresVehicle
                && !string.IsNullOrWhiteSpace(incident.VehicleModel))
                modelNames.Add(incident.VehicleModel);
            if (!string.IsNullOrWhiteSpace(incident.VictimModelName))
                modelNames.Add(incident.VictimModelName);
            if (incident.SceneParticipantModelNames != null)
                foreach (string modelName in incident.SceneParticipantModelNames)
                    if (!string.IsNullOrWhiteSpace(modelName))
                        modelNames.Add(modelName);
            foreach (string modelName in modelNames)
            {
                try
                {
                    Model model = new Model(modelName);
                    if (model.IsValid)
                        model.MarkAsNoLongerNeeded();
                }
                catch { }
            }
        }
        private void SetSceneBlip(Vector3 position, string title)
        {
            CleanupBlip(_sceneBlip);
            try
            {
                _sceneBlip = World.CreateBlip(position);
                if (_sceneBlip != null && _sceneBlip.Exists())
                {
                    _sceneBlip.Name = title;
                    _sceneBlip.IsShortRange = false;
                }
            }
            catch (Exception ex) { LogException("POLICE_DISPATCH_BLIP_FAILED", ex); }
        }

        private void SetSuspectBlip(Ped suspect)
        {
            if (suspect != null && suspect.Exists())
                _primarySuspectBlipHandle = suspect.Handle;
            MaintainSuspectBlips(DateTime.UtcNow, true);
        }

        private void MaintainSuspectBlips(DateTime now, bool force = false)
        {
            if (!force && now < _nextSuspectBlipMaintenanceAt)
                return;
            _nextSuspectBlipMaintenanceAt = now.AddSeconds(1);

            LSPDDispatchEvent incident = _incident;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff)
            {
                CleanupSuspectBlips();
                return;
            }

            List<Ped> activeSuspects = TrackedSuspects(incident)
                .Where(ped => ped != null && ped.Exists()
                    && !ped.IsDead && ped.Health > 0
                    && !IsArrested(incident, ped))
                .ToList();
            HashSet<int> activeHandles = new HashSet<int>(
                activeSuspects.Select(ped => ped.Handle));

            foreach (int staleHandle in _suspectBlips.Keys
                .Where(handle => !activeHandles.Contains(handle)).ToList())
            {
                CleanupBlip(_suspectBlips[staleHandle]);
                _suspectBlips.Remove(staleHandle);
            }

            if (!activeHandles.Contains(_primarySuspectBlipHandle))
            {
                Ped primary = incident.CurrentPursuitTarget
                    ?? activeSuspects.FirstOrDefault();
                _primarySuspectBlipHandle = primary == null ? 0 : primary.Handle;
            }

            for (int index = 0; index < activeSuspects.Count; index++)
            {
                Ped suspect = activeSuspects[index];
                Blip existing;
                if (_suspectBlips.TryGetValue(suspect.Handle, out existing)
                    && existing != null && existing.Exists())
                    continue;

                try
                {
                    Blip blip = suspect.AddBlip();
                    if (blip == null || !blip.Exists())
                        continue;
                    int suspectIndex = incident.Suspects == null
                        ? index + 1
                        : incident.Suspects.FindIndex(value => value != null
                            && value.Handle == suspect.Handle) + 1;
                    blip.Name = "Dispatch Suspect " + Math.Max(1, suspectIndex);
                    blip.Color = BlipColor.Red;
                    blip.IsShortRange = false;
                    _suspectBlips[suspect.Handle] = blip;
                    LogRuntime("POLICE_DISPATCH_GROUP_SUSPECT_BLIP_CREATED",
                        "Incident=" + incident.Id + "; Ped=" + suspect.Handle
                        + "; SuspectIndex=" + Math.Max(1, suspectIndex)
                        + "; ActiveSuspects=" + activeSuspects.Count
                        + "; Primary=" + (suspect.Handle == _primarySuspectBlipHandle));
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_SUSPECT_BLIP_FAILED", ex);
                }
            }
        }

        private void CleanupSuspectBlips()
        {
            foreach (Blip blip in _suspectBlips.Values)
                CleanupBlip(blip);
            _suspectBlips.Clear();
            _primarySuspectBlipHandle = 0;
            _nextSuspectBlipMaintenanceAt = DateTime.MinValue;
        }

        private void CleanupBlipsAndWaypoint()
        {
            CleanupBlip(_sceneBlip);
            CleanupSuspectBlips();
            try
            {
                if (_hadPreviousWaypoint)
                    World.WaypointPosition = _previousWaypoint;
                else if (Game.IsWaypointActive)
                    World.RemoveWaypoint();
            }
            catch { }
            _hadPreviousWaypoint = false;
            _previousWaypoint = Vector3.Zero;
        }

        private static void CleanupBlip(Blip blip)
        {
            try { if (blip != null && blip.Exists()) blip.Delete(); } catch { }
        }

        private void CaptureAndSetWaypoint(Vector3 position)
        {
            if (!_uiSettings.ShowWaypoint)
            {
                LogRuntime("POLICE_DISPATCH_GPS_SUPPRESSED",
                    "Dispatch route guidance is disabled in LSImmersiveMainUI.xml.");
                return;
            }
            try
            {
                if (!_hadPreviousWaypoint && Game.IsWaypointActive)
                {
                    _previousWaypoint = World.WaypointPosition;
                    _hadPreviousWaypoint = true;
                }
                World.WaypointPosition = position;
            }
            catch (Exception ex) { LogException("POLICE_DISPATCH_GPS_FAILED", ex); }
        }

        /// <summary>
        /// Reports a single current Dispatch transmission. Replacing the prior
        /// stage prevents queued offer/route lines from being heard after the
        /// officer has already reached the scene or resolved the assignment.
        /// </summary>
        internal void ReportTransportAudio(string eventId, string stage)
        {
            if (_incident == null || string.IsNullOrWhiteSpace(eventId)
                || string.IsNullOrWhiteSpace(stage))
                return;
            ReportAudioStage(
                eventId,
                _incident,
                "transport-" + stage);
        }

        private void MaintainPursuitWaypoint(DateTime now, Ped player)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || incident.CurrentPursuitTarget == null
                || !IsVehiclePursuitWaypointEvent(incident)
                || now < incident.LastPursuitWaypointAt.AddSeconds(
                    PursuitWaypointRefreshSeconds))
                return;

            Ped suspect = incident.CurrentPursuitTarget;
            if (suspect == null || !suspect.Exists() || suspect.IsDead)
                return;

            Vehicle pursuitVehicle = incident.VehicleForSuspect(suspect);
            Vector3 position = pursuitVehicle != null
                && pursuitVehicle.Exists()
                    ? pursuitVehicle.Position
                    : suspect.Position;
            if (position.DistanceTo(incident.LastPursuitWaypointPosition)
                < PursuitWaypointMovementMeters)
                return;

            incident.LastPursuitWaypointAt = now;
            incident.LastPursuitWaypointPosition = position;
            if (_uiSettings.ShowWaypoint)
                CaptureAndSetWaypoint(position);

            if (incident.LastPursuitRadioUpdateAt == DateTime.MinValue
                || now >= incident.LastPursuitRadioUpdateAt.AddSeconds(
                    PursuitLocationRadioGapSeconds))
            {
                incident.LastPursuitRadioUpdateAt = now;
                incident.PursuitLocationUpdateCount++;
                ReportAudioStage(
                    "lsimmersivelife.police.detail.location_updated",
                    incident,
                    "pursuit-location-" + incident.PursuitLocationUpdateCount,
                    true);
                LogRuntime(
                    "POLICE_DISPATCH_PURSUIT_ROUTE_UPDATED",
                    "Event=" + incident.Id
                    + "; Target=" + suspect.Handle
                    + "; Vehicle=" + (pursuitVehicle == null ? 0 : pursuitVehicle.Handle)
                    + "; Position=" + position
                    + "; Player=" + (player == null || !player.Exists()
                        ? 0 : player.Handle));
            }
        }

        private static bool IsVehiclePursuitWaypointEvent(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return false;
            bool legacyRoadSpeedBranch = incident.BranchIds != null
                && incident.BranchIds.Contains(
                    "dispatch-road-vehicle-speed-chaos",
                    StringComparer.OrdinalIgnoreCase);
            bool boundedGroupPursuit = incident.StartPursuitOnAccept
                && incident.SuspectVehicleCount > 1;
            return legacyRoadSpeedBranch || boundedGroupPursuit;
        }

        private void ReportSceneApproach(Ped player)
        {
            if (_incident == null || _incident.SceneApproachReported
                || _incident.SceneArrivalReported || _incident.HasConvoyCustodyHandoff
                || !_incident.OwnedByDispatch || player == null || !player.Exists())
                return;

            switch (_incident.State)
            {
                case LSPDDispatchState.Accepted:
                case LSPDDispatchState.EnRoute:
                case LSPDDispatchState.OnScene:
                case LSPDDispatchState.Investigating:
                case LSPDDispatchState.SuspectFleeing:
                case LSPDDispatchState.SuspectResisting:
                    break;
                default:
                    return;
            }

            if (player.Position.DistanceTo(_incident.Origin) > 50f)
                return;

            _incident.SceneApproachReported = true;
            ReportAudioStage(
                "lsimmersivelife.police.scene.approaching",
                _incident,
                "approaching",
                true);
            ReportAudioStage(
                "lsimmersivelife.police.detail.safety_warning",
                _incident,
                "approach-caution",
                true);
        }

        private void MaintainEnRouteStatus(DateTime now)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || incident.SceneApproachReported || incident.SceneArrivalReported
                || !incident.ScenePreparationCompleted
                || (incident.State != LSPDDispatchState.EnRoute
                    && incident.State != LSPDDispatchState.SuspectFleeing
                    && incident.State != LSPDDispatchState.SuspectResisting)
                || now < incident.NextEnRouteStatusAt)
                return;

            incident.EnRouteStatusUpdateCount++;
            string stage = "en-route-scene-active-" + incident.EnRouteStatusUpdateCount;
            ReportAudioStage(
                "lsimmersivelife.police.dispatch.scene_active",
                incident,
                stage,
                true);
            incident.NextEnRouteStatusAt = now.AddSeconds(60);
        }

        private LSPDAudioResult ReportAudioStage(
            string eventId,
            LSPDDispatchEvent incident,
            string stage,
            bool retainCurrentScope = false)
        {
            if (incident == null || string.IsNullOrWhiteSpace(stage))
                return LSPDAudioResult.Inactive;
            if (string.Equals(incident.LastAudioStage, stage, StringComparison.Ordinal))
                return LSPDAudioResult.Duplicate;

            if (!retainCurrentScope || string.IsNullOrWhiteSpace(incident.ActiveAudioScope))
            {
                CloseActiveAudio(incident);
                incident.ActiveAudioScope = "police-dispatch-" + incident.Id + "-" + stage;
            }
            string scope = incident.ActiveAudioScope;
            incident.LastAudioStage = stage;
            return _audio.Report(
                eventId,
                scope,
                stage + "-" + incident.Id,
                incident.AudioIncidentType);
        }

        private LSPDAudioResult ReportTerminalAudio(
            string eventId,
            LSPDDispatchEvent incident,
            string stage)
        {
            if (incident == null)
                return LSPDAudioResult.Inactive;
            CloseActiveAudio(incident);
            string scope = "police-dispatch-terminal-" + incident.Id + "-" + stage;
            incident.ActiveAudioScope = scope;
            incident.LastAudioStage = stage;
            return _audio.Report(
                eventId,
                scope,
                stage + "-" + incident.Id,
                incident.AudioIncidentType);
        }

        private void CloseActiveAudio(LSPDDispatchEvent incident)
        {
            if (incident == null || string.IsNullOrWhiteSpace(incident.ActiveAudioScope))
                return;
            try { _audio.CancelScope(incident.ActiveAudioScope); }
            catch { }
            incident.ActiveAudioScope = string.Empty;
        }

        private int NextQuietPatrolSeconds()
        {
            int minimum = Math.Max(30, _settings.MinimumQuietPatrolSeconds);
            int maximum = Math.Max(minimum, _settings.MaximumQuietPatrolSeconds);
            return minimum == maximum
                ? minimum
                : _random.Next(minimum, maximum + 1);
        }

        private void ScheduleNextOffer(DateTime now)
        {
            _cooldownUntil = now.AddSeconds(_settings.MinimumQuietPatrolSeconds);
            _nextOfferAt = now.AddSeconds(NextQuietPatrolSeconds());
        }

        private DateTime NextDefinitionEligibleAt(DateTime now)
        {
            if (_settings.RepeatProtectionMinutes <= 0 || _definitionOfferedAt.Count == 0)
                return now.AddSeconds(30);

            DateTime earliest = DateTime.MaxValue;
            foreach (LSPDDispatchEventDefinition definition in _definitions)
            {
                DateTime lastOffered;
                if (!_definitionOfferedAt.TryGetValue(definition.Id, out lastOffered))
                    return now;
                DateTime eligibleAt = lastOffered.AddMinutes(_settings.RepeatProtectionMinutes);
                if (eligibleAt < earliest)
                    earliest = eligibleAt;
            }
            if (earliest == DateTime.MaxValue)
                return now.AddSeconds(30);
            return earliest <= now ? now.AddSeconds(1) : earliest;
        }

        private bool CanPlayOfferAudio()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _lastOfferAudioAt.AddSeconds(_settings.AudioMinimumGapSeconds))
                return false;
            _lastOfferAudioAt = now;
            return true;
        }

        private static string DefinitionId(LSPDDispatchEvent incident)
        {
            if (incident == null || string.IsNullOrWhiteSpace(incident.Id))
                return string.Empty;
            int separator = incident.Id.LastIndexOf('-');
            return separator > 0 ? incident.Id.Substring(0, separator) : incident.Id;
        }

        private LSPDCriminalProfileDefinition WeightedProfile(
            IList<LSPDCriminalProfileDefinition> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return null;
            int totalWeight = candidates.Sum(value => Math.Max(1, value.Weight));
            int roll = _random.Next(totalWeight);
            foreach (LSPDCriminalProfileDefinition candidate in candidates)
            {
                roll -= Math.Max(1, candidate.Weight);
                if (roll < 0)
                    return candidate;
            }
            return candidates[candidates.Count - 1];
        }

        private bool TryResolveDispatchProfile(
            LSPDDispatchEventDefinition definition,
            out LSPDCriminalProfileDefinition resolved,
            out string failureReason)
        {
            resolved = null;
            failureReason = string.Empty;
            if (definition == null || definition.CriminalProfileIds == null)
            {
                failureReason = "EVENT_HAS_NO_CRIMINAL_PROFILE_IDS";
                return false;
            }

            var candidates = new List<LSPDCriminalProfileDefinition>();
            foreach (string profileId in definition.CriminalProfileIds)
            {
                LSPDCriminalProfileDefinition profile;
                if (string.IsNullOrWhiteSpace(profileId)
                    || !_criminalProfiles.TryGetValue(profileId, out profile)
                    || profile == null
                    || !profile.IsEligibleFor("dispatch"))
                    continue;
                candidates.Add(profile);
            }
            if (candidates.Count == 0)
            {
                failureReason = "EVENT_HAS_NO_ELIGIBLE_XML_CRIMINAL_PROFILE";
                return false;
            }

            return TryResolveUsableCriminalProfile(
                candidates,
                "dispatch",
                definition.RequiresVehicle,
                definition.VehicleAssetTypes,
                out resolved,
                out failureReason);
        }

        private bool TryResolveUsableCriminalProfile(
            IList<LSPDCriminalProfileDefinition> candidates,
            string activity,
            bool requireVehicle,
            out LSPDCriminalProfileDefinition resolved,
            out string failureReason)
        {
            return TryResolveUsableCriminalProfile(
                candidates, activity, requireVehicle, null,
                out resolved, out failureReason);
        }

        private bool TryResolveUsableCriminalProfile(
            IList<LSPDCriminalProfileDefinition> candidates,
            string activity,
            bool requireVehicle,
            IEnumerable<string> requiredVehicleTypes,
            out LSPDCriminalProfileDefinition resolved,
            out string failureReason)
        {
            resolved = null;
            failureReason = string.Empty;
            if (candidates == null || candidates.Count == 0)
            {
                failureReason = "NO_XML_CRIMINAL_PROFILE_CANDIDATES";
                return false;
            }

            var attemptedSelections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string lastFailure = "XML_CRIMINAL_ASSET_SELECTION_EXHAUSTED";
            for (int attempt = 0; attempt < MaxCriminalAssetSelectionAttempts; attempt++)
            {
                LSPDCriminalProfileDefinition source = WeightedProfile(candidates);
                LSPDCriminalProfileDefinition selected;
                string catalogFailure;
                if (!_criminalAssets.TryResolveProfile(
                    source,
                    activity,
                    _random,
                    _recentCriminalAssetIdSet,
                    requiredVehicleTypes,
                    out selected,
                    out catalogFailure))
                {
                    lastFailure = catalogFailure;
                    LogDebug(
                        "POLICE_CRIMINAL_ASSET_CANDIDATE_REJECTED",
                        "Activity=" + activity + "; Profile="
                        + (source == null ? string.Empty : source.Id)
                        + "; Reason=" + catalogFailure);
                    continue;
                }

                string selectionKey = selected.Id + "|" + selected.PedAssetId
                    + "|" + selected.VehicleAssetId + "|" + selected.WeaponAssetId;
                if (!attemptedSelections.Add(selectionKey))
                    continue;

                string runtimeFailure;
                if (!ValidateCriminalProfileAssets(
                    selected,
                    requireVehicle,
                    out runtimeFailure))
                {
                    lastFailure = runtimeFailure;
                    LogDebug(
                        "POLICE_CRIMINAL_ASSET_CANDIDATE_REJECTED",
                        "Activity=" + activity + "; Profile=" + selected.Id
                        + "; PedAsset=" + selected.PedAssetId
                        + "; VehicleAsset=" + selected.VehicleAssetId
                        + "; WeaponAsset=" + selected.WeaponAssetId
                        + "; Reason=" + runtimeFailure);
                    continue;
                }

                resolved = selected;
                // Record every successful concrete XML selection, including
                // Dispatch selections.  Without this, the recent-use filter
                // was updated for Convoy only, so repeated Dispatch offers
                // could keep selecting the same profile assets.
                RememberProfileAssets(selected);
                LogRuntime(
                    "POLICE_CRIMINAL_ASSETS_SELECTED",
                    DescribeCriminalProfileAssets(activity, selected)
                    + "; Attempt=" + (attempt + 1));
                return true;
            }

            failureReason = lastFailure
                + "; Attempts=" + MaxCriminalAssetSelectionAttempts;
            return false;
        }

        private static bool ValidateCriminalProfileAssets(
            LSPDCriminalProfileDefinition profile,
            bool requireVehicle,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (profile == null)
            {
                failureReason = "PROFILE_REFERENCE_MISSING";
                return false;
            }
            if (string.IsNullOrWhiteSpace(profile.ModelName))
            {
                failureReason = "PED_MODEL_NAME_EMPTY";
                return false;
            }

            Model pedModel = new Model(profile.ModelName);
            if (!IsUsableModel(pedModel, false))
            {
                failureReason = "PED_MODEL_INVALID:" + profile.ModelName;
                return false;
            }

            if (requireVehicle)
            {
                if (string.IsNullOrWhiteSpace(profile.VehicleModelName))
                {
                    failureReason = "VEHICLE_MODEL_NAME_EMPTY";
                    return false;
                }
                Model vehicleModel = new Model(profile.VehicleModelName);
                if (!IsUsableModel(vehicleModel, true))
                {
                    failureReason = "VEHICLE_MODEL_INVALID:" + profile.VehicleModelName;
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(profile.WeaponName))
            {
                failureReason = "WEAPON_NAME_EMPTY";
                return false;
            }
            int weaponHash = ResolveWeaponHash(profile.WeaponName);
            var weaponAsset = new WeaponAsset(weaponHash);
            if (!weaponAsset.IsValid || !weaponAsset.IsValidAsWeaponHash)
            {
                failureReason = "WEAPON_ASSET_INVALID:" + profile.WeaponName;
                return false;
            }
            return true;
        }

        private static string DescribeCriminalProfileAssets(
            string activity,
            LSPDCriminalProfileDefinition profile)
        {
            return "Activity=" + activity
                + "; Profile=" + (profile == null ? string.Empty : profile.Id)
                + "; PedAsset=" + (profile == null ? string.Empty : profile.PedAssetId)
                + "/" + (profile == null ? string.Empty : profile.ModelName)
                + "; VehicleAsset=" + (profile == null ? string.Empty : profile.VehicleAssetId)
                + "/" + (profile == null ? string.Empty : profile.VehicleModelName)
                + "; WeaponAsset=" + (profile == null ? string.Empty : profile.WeaponAssetId)
                + "/" + (profile == null ? string.Empty : profile.WeaponName)
                + "; Pools=ped/" + (profile == null ? string.Empty : profile.PedPoolId)
                + ",vehicle/" + (profile == null ? string.Empty : profile.VehiclePoolId)
                + ",weapon/" + (profile == null ? string.Empty : profile.WeaponPoolId);
        }

        private static int ResolveWeaponHash(string name)
        {
            uint numeric;
            if (!string.IsNullOrWhiteSpace(name)
                && name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(name.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out numeric))
                return unchecked((int)numeric);
            if (!string.IsNullOrWhiteSpace(name)
                && uint.TryParse(name, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out numeric))
                return unchecked((int)numeric);
            return unchecked((int)StringHash.AtStringHash(name ?? string.Empty, 0));
        }

        private void RememberProfileAssets(LSPDCriminalProfileDefinition profile)
        {
            if (profile == null)
                return;
            RememberAsset(profile.PedAssetId);
            RememberAsset(profile.VehicleAssetId);
            RememberAsset(profile.WeaponAssetId);
        }

        private void RememberAsset(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
                return;
            if (_recentCriminalAssetIdSet.Contains(assetId))
                return;
            _recentCriminalAssetIdSet.Add(assetId);
            _recentCriminalAssetIds.Enqueue(assetId);
            while (_recentCriminalAssetIds.Count > 12)
                _recentCriminalAssetIdSet.Remove(_recentCriminalAssetIds.Dequeue());
        }

        private void ReloadDefinitions()
        {
            _definitions.Clear();
            _criminalProfiles.Clear();
            _locations.Clear();
            _branches.Clear();
            _locationCatalog = null;
            if (_ambientWorld != null)
                _ambientWorld.UpdateLocationCatalog(null);
            _recentCriminalAssetIds.Clear();
            _recentCriminalAssetIdSet.Clear();
            _criminalAssets.Clear();

            if (!string.IsNullOrWhiteSpace(_criminalProfilePath)
                && File.Exists(_criminalProfilePath))
            {
                try
                {
                    XDocument profileDocument = LoadXmlDocument(_criminalProfilePath);
                    XElement profiles = profileDocument.Root == null
                        ? null : profileDocument.Root.Element("Profiles");
                    if (profiles == null)
                        throw new InvalidDataException("Criminal profile XML does not contain Profiles.");
                    foreach (XElement node in profiles.Elements("Profile"))
                    {
                        LSPDCriminalProfileDefinition profile =
                            LSPDCriminalProfileDefinition.FromXml(node);
                        if (_criminalProfiles.ContainsKey(profile.Id))
                            throw new InvalidDataException("Duplicate criminal profile id: " + profile.Id);
                        _criminalProfiles.Add(profile.Id, profile);
                    }

                    XElement peds = profileDocument.Root.Element("Peds");
                    if (peds != null)
                        foreach (XElement node in peds.Elements("Ped"))
                            _criminalAssets.AddPed(LSPDCriminalAssetDefinition.FromXml(
                                node, "model", "Ped"));

                    XElement vehicles = profileDocument.Root.Element("Vehicles");
                    if (vehicles != null)
                        foreach (XElement node in vehicles.Elements("Vehicle"))
                            _criminalAssets.AddVehicle(LSPDCriminalAssetDefinition.FromXml(
                                node, "model", "Vehicle"));

                    XElement weapons = profileDocument.Root.Element("Weapons");
                    if (weapons != null)
                        foreach (XElement node in weapons.Elements("Weapon"))
                            _criminalAssets.AddWeapon(LSPDCriminalAssetDefinition.FromXml(
                                node, "weapon", "Weapon"));

                    XElement pools = profileDocument.Root.Element("AssetPools");
                    if (pools != null)
                        foreach (XElement node in pools.Elements("Pool"))
                            _criminalAssets.AddPool(
                                LSPDCriminalAssetPoolDefinition.FromXml(node));

                    XElement mappings = profileDocument.Root.Element("ActivityMappings");
                    if (mappings != null)
                        foreach (XElement node in mappings.Elements("Mapping"))
                            _criminalAssets.AddActivityMapping(
                                LSPDCriminalActivityMappingDefinition.FromXml(node));
                    _criminalAssets.Validate();
                    foreach (LSPDCriminalProfileDefinition profile in _criminalProfiles.Values)
                    {
                        if (!string.IsNullOrWhiteSpace(profile.PedPoolId)
                            && !_criminalAssets.HasPool(profile.PedPoolId, "ped"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid ped pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.VehiclePoolId)
                            && !_criminalAssets.HasPool(profile.VehiclePoolId, "vehicle"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid vehicle pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.WeaponPoolId)
                            && !_criminalAssets.HasPool(profile.WeaponPoolId, "weapon"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid weapon pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.ConvoyPedPoolId)
                            && !_criminalAssets.HasPool(profile.ConvoyPedPoolId, "ped"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid convoy ped pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.ConvoyVehiclePoolId)
                            && !_criminalAssets.HasPool(profile.ConvoyVehiclePoolId, "vehicle"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid convoy vehicle pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.ConvoyWeaponPoolId)
                            && !_criminalAssets.HasPool(profile.ConvoyWeaponPoolId, "weapon"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid convoy weapon pool: " + profile.Id);
                    }
                }
                catch (Exception ex)
                {
                    _criminalProfiles.Clear();
                    _criminalAssets.Clear();
                    LogException("POLICE_CRIMINAL_PROFILE_CATALOG_FAILED", ex);
                }
            }
            else if (!string.IsNullOrWhiteSpace(_criminalProfilePath))
            {
                LogDebug("POLICE_CRIMINAL_PROFILE_CATALOG_MISSING",
                    "LSPDCriminalProfile.xml was not found; Dispatch criminal asset selection is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(_catalogPath) || !File.Exists(_catalogPath))
                return;

            LSImmersiveLocationCatalog locationCatalog;
            try
            {
                locationCatalog = LSImmersiveLocationCatalog.Load(
                    _locationCatalogPath);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_LOCATION_CATALOG_FAILED", ex);
                return;
            }

            _locationCatalog = locationCatalog;
            if (_ambientWorld != null)
                _ambientWorld.UpdateLocationCatalog(locationCatalog);
            LogRuntime(
                "DISPATCH_LOCATION_CATALOG_LOADED",
                "LocationRecords=" + locationCatalog.LocationCount);

            try
            {
                XDocument document = LoadXmlDocument(_catalogPath);
                XElement branchCatalog = document.Root == null
                    ? null : document.Root.Element("Branches");
                if (branchCatalog != null)
                {
                    foreach (XElement node in branchCatalog.Elements("Branch"))
                    {
                        LSPDDispatchBranchDefinition branch =
                            LSPDDispatchBranchDefinition.FromXml(node);
                        if (_branches.ContainsKey(branch.Id))
                            throw new InvalidDataException(
                                "Duplicate Dispatch branch reference: " + branch.Id);
                        _branches.Add(branch.Id, branch);
                    }
                }
                XElement locationRefs = document.Root == null
                    ? null : document.Root.Element("LocationRefs");
                if (locationRefs != null)
                {
                    foreach (XElement node in locationRefs.Elements("LocationRef"))
                    {
                        LSPDDispatchLocationDefinition location =
                            LSPDDispatchLocationDefinition.FromXml(
                                node, locationCatalog);
                        if (_locations.ContainsKey(location.Id))
                            throw new InvalidDataException("Duplicate Dispatch location id: " + location.Id);
                        _locations.Add(location.Id, location);
                    }
                }
                XElement events = document.Root == null ? null : document.Root.Element("Events");
                if (events == null)
                    return;
                foreach (XElement node in events.Elements("Event"))
                {
                    try
                    {
                        LSPDDispatchEventDefinition definition =
                            LSPDDispatchEventDefinition.FromXml(node);
                        ValidateDefinitionReferences(definition);
                        _definitions.Add(definition);
                    }
                    catch (Exception ex) { LogException("POLICE_DISPATCH_DEFINITION_INVALID", ex); }
                }
                LogRuntime(
                    "POLICE_DISPATCH_CATALOG_LOADED",
                    "Definitions=" + _definitions.Count
                    + "; CriminalProfiles=" + _criminalProfiles.Count
                    + "; CriminalPeds=" + _criminalAssets.PedCount
                    + "; CriminalVehicles=" + _criminalAssets.VehicleCount
                    + "; CriminalWeapons=" + _criminalAssets.WeaponCount
                    + "; AuthoredLocations=" + _locations.Count
                    + "; Branches=" + _branches.Count);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_CATALOG_FAILED", ex);
            }
        }

        private void ValidateDefinitionReferences(LSPDDispatchEventDefinition definition)
        {
            if (definition == null)
                throw new InvalidDataException("Dispatch definition is null.");
            if (definition.BranchIds == null || definition.BranchIds.Count == 0)
                throw new InvalidDataException(
                    "Dispatch event has no documented response-branch reference: " + definition.Id);
            foreach (string branchId in definition.BranchIds)
            {
                LSPDDispatchBranchDefinition branch;
                if (!_branches.TryGetValue(branchId, out branch))
                    throw new InvalidDataException(
                        "Dispatch event references an unknown branch: "
                        + definition.Id + " -> " + branchId);
                if (string.Equals(branch.Phase, "custody_continuation",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "A custody continuation cannot be offered as a new Dispatch callout: "
                        + definition.Id + " -> " + branchId);
            }
            if (definition.CriminalProfileIds == null
                || definition.CriminalProfileIds.Count == 0)
                throw new InvalidDataException(
                    "Dispatch event has no authoritative criminal profile reference: "
                    + definition.Id);
            else
            {
                bool hasProfile = definition.CriminalProfileIds.Any(
                    id => _criminalProfiles.ContainsKey(id)
                        && _criminalProfiles[id].IsEligibleFor("dispatch"));
                if (!hasProfile)
                    throw new InvalidDataException(
                        "Dispatch event has no eligible criminal profile reference: " + definition.Id);
            }
            if (definition.LocationIds == null || definition.LocationIds.Count == 0
                || definition.LocationIds.Any(id => !_locations.ContainsKey(id)))
                throw new InvalidDataException(
                    "Dispatch event has a missing or invalid authored location reference: "
                    + definition.Id);
        }

        private static XDocument LoadXmlDocument(string path)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using (XmlReader reader = XmlReader.Create(path, settings))
                return XDocument.Load(reader, LoadOptions.None);
        }

        private void Notify(string message)
        {
            try { Notification.PostTicker(message, false, false); } catch { }
        }

        private void LogRuntime(string category, string message)
        {
            if (_log == null)
                return;
            bool recurringLocationSelection =
                string.Equals(category,
                    "POLICE_DISPATCH_NEARBY_CATALOG_EVENTS_READY",
                    StringComparison.Ordinal)
                || string.Equals(category,
                    "POLICE_DISPATCH_NO_LOCATION_ELIGIBLE",
                    StringComparison.Ordinal);
            if (recurringLocationSelection)
                _log.RuntimeThrottled(category, message,
                    TimeSpan.FromSeconds(30));
            else
                _log.Runtime(category, message);
        }

        private void LogStateFailure(string category, string message)
        {
            if (_log != null) _log.StateFailure(category, message);
        }

        private void LogDebug(string category, string message)
        {
            if (_log != null) _log.Debug(category, message);
        }

        private void LogException(string category, Exception ex)
        {
            if (_log != null) _log.Exception(category, ex);
        }

        private static string DescribePed(Ped ped)
        {
            if (ped == null)
                return "null";
            try
            {
                return "Handle=" + ped.Handle
                    + "; Exists=" + ped.Exists()
                    + "; Dead=" + (ped.Exists() && ped.IsDead)
                    + "; Position=" + (ped.Exists() ? ped.Position.ToString() : "unavailable");
            }
            catch
            {
                return "Handle unavailable";
            }
        }

        private static string DescribeVehicle(Vehicle vehicle)
        {
            if (vehicle == null)
                return "null";
            try
            {
                return "Handle=" + vehicle.Handle
                    + "; Exists=" + vehicle.Exists()
                    + "; Position=" + (vehicle.Exists() ? vehicle.Position.ToString() : "unavailable");
            }
            catch
            {
                return "Handle unavailable";
            }
        }
    }
}




