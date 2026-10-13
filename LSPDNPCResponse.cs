using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LSImmersiveLife
{
    /// <summary>
    /// Local Police civilian-contact and ambient-reaction owner. Construction is
    /// passive; PoliceCore supplies Authority context and owned-scene boundaries.
    /// </summary>
    // The original boundary note is retained above. The owner below now runs
    // only through the explicit Authority and Patrol gates described here.
    /// <summary>
    /// Owns bounded Police-only civilian contact and ambient reaction loops.
    ///
    /// Contact selection remains Patrol-only. Presence and traffic reactions
    /// follow active Authority scenes and custody using small local samples. This
    /// owner never scans the city, owns Gang data, or creates Dispatch incidents.
    /// </summary>
    internal sealed class LSPDNPCResponse
    {
        private const int HandcuffedPedConfigFlag = 120;
        private const float DefaultFootContactRadius = 6.5f;
        private const float DefaultTrafficContactRadius = 28.0f;
        private const float FootTrafficContactSpeed = 7.5f;
        private const float CitizenDocumentHandoffDistance = 1.65f;
        private const float CitizenDocumentHandoffHeightDifference = 0.65f;
        private const float TrafficAimSurrenderMaximumDistance = 35.0f;
        private const float DefaultLocalTrafficCautionRadius = 12.0f;
        private const float DefaultActiveSceneTrafficCautionRadius = 18.0f;
        // Eight metres is useful as a caution area, but was far too large for
        // a frame-refreshed no-collision shield. Keep a small emergency bubble
        // only for vehicles that are actually closing on the officer.
        private const float ImmediateCollisionGuardRadius = 3.25f;
        private const float StoppedTrafficSpeed = 6.0f;
        private const float LocalTrafficCautionSpeed = 4.0f;
        private const float ActiveSceneTrafficCautionSpeed = 7.0f;
        private const float PredictedTrafficCautionSpeed = 2.0f;
        private const float TrafficWindowContactRadius = 5.5f;
        private const float TrafficWindowGroundContactRadius = 2.75f;
        private const float TrafficWindowGroundElevationTolerance = 1.25f;
        private const float TrafficPullOverContactAreaRadius = 16.0f;
        private const float TrafficPullOverContactAreaElevation = 4.5f;
        private const float TrafficStopPositionDriftRadius = 4.0f;
        private const float TrafficDriverFallbackElevationTolerance = 3.5f;
        private const float TrafficWindowGeometryMoveInvalidation = 0.75f;
        private const float TrafficWindowGeometryHeadingInvalidation = 8.0f;
        private const int TrafficWindowGeometryRetryMilliseconds = 1500;
        private const int TrafficSubjectLostRecoveryMilliseconds = 6000;
        private const int TrafficVehicleTransitionRecoveryMilliseconds = 6000;
        private const int TrafficBackupPursuitSampleMilliseconds = 500;
        private const int TrafficPursuitExitRetryMilliseconds = 4000;
        private const int TrafficPursuitExitMaximumAttempts = 2;
        private const float InteractionReleaseDistance = 32.0f;
        private const float MaximumPullOverTargetDistance = 12.0f;
        private const float MaximumPullOverElevationDifference = 8.0f;
        private const float StationArrivalRadius = 24.0f;
        private const float FollowReleaseDistance = 350.0f;
        private const float DefaultDeadSubjectReleaseDistance = 80.0f;
        private const float BackupContainmentInteractionRadius = 12.0f;
        private const float FleeMovementSpeed = 3.0f;
        private const float FleeMovementDistance = 2.5f;
        private const int ContactPreparationMilliseconds = 700;
        private const int SubjectOrientationMilliseconds = 1800;
        private const int MaximumFootGreetingAttempts = 2;
        private const int StationFollowTimeoutMilliseconds = 180000;
        private const int SubjectHoldMilliseconds = 3000;
        private const int TrafficCautionMilliseconds = 500;
        private const int TrafficApproachBrakeMilliseconds = 1200;
        private const int TrafficApproachBrakeCooldownMilliseconds = 1500;
        private const int MaximumTrafficOperationAnchors = 4;
        private const int FleeMemoryMilliseconds = 30000;
        private const int SceneReactionScanMilliseconds = 1000;
        private const int CalmCivilianPauseMilliseconds = 1500;
        private const int CalmCivilianNoticeMilliseconds = 1100;
        private const int CalmCivilianStepBackMilliseconds = 2200;
        private const int CivilianRetreatMilliseconds = 7000;
        private const float CivilianInterferenceRadius = 2.15f;
        private const float CivilianPresenceNoticeRadius = 6.0f;
        private const float CivilianStepBackDistance = 2.5f;
        private const float MaximumTrafficVerticalSeparation = 2.5f;
        private const float MaximumTrafficImpactLookAheadSeconds = 3.0f;
        private const float ImminentTrafficImpactWindowSeconds = 1.5f;
        private const int DeescalationScanMilliseconds = 1250;
        private const int ApproachScanMilliseconds = 1000;
        private const int ApproachCandidateMilliseconds = 4500;
        private const int DefaultCollisionGuardMilliseconds = 900;
        private const int TrafficDrivingStyle = 786603;
        // Let GTA's physical arrest task finish, then confirm the actual cuff
        // state before Citizen transfers the subject to Backup custody.
        private const int FootPlayerArrestMilliseconds = 6500;
        private const int FootPlayerArrestMaximumAttempts = 2;
        private const int BackupArrestMaximumAttempts = 3;
        private const float FootPlayerArrestInteractionRadius = 3.5f;
        private const int FootResistanceTaskRecoveryMilliseconds = 1800;
        private const int SuspiciousApproachTaskRecoveryMilliseconds = 1400;
        private const int DeadSubjectMinimumHoldMilliseconds = 15000;
        // Backup's physical handoff is deliberately bounded, but it needs a
        // separate window from the officer's initial approach.  The previous
        // 45-second shared window expired while the subject was still being
        // navigated to the rear door.
        private const int BackupPhysicalEscortTimeoutSeconds = 90;
        private const float BackupEscortDoorRadius = 2.8f;
        private const int BackupVehicleDoorAnimationFallbackGraceMilliseconds = 4000;
        private const int BackupMovementClipsetCheckMilliseconds = 1500;
        private const int BackupMovementClipsetTimeoutSeconds = 8;
        private const int BackupEscortTaskRecoverySeconds = 12;
        private const string BackupPrisonerMovementClipset = "move_m@prisoner_cuffed";

        private struct TrafficDriverWindowGeometry
        {
            internal int VehicleHandle;
            internal Vector3 VehiclePosition;
            internal float VehicleHeading;
            internal Vector3 WindowPosition;
            internal Vector3 OfficerGroundPosition;
            internal float MaximumWindowSeparation;
            internal DateTime LastAttemptAt;
            internal bool Resolved;
        }

        private enum TrafficPursuitStage
        {
            None,
            VehicleFlight,
            VehicleStoppedAwaitingExit,
            OnFootFlight,
            Contained
        }

        private enum InteractionStage
        {
            None,
            FootPreparing,
            FootGreeting,
            FootDocuments,
            FootRefused,
            FootStationFollow,
            FootFleeing,
            FootResisting,
            TrafficPullingOver,
            TrafficPreparing,
            TrafficDocuments,
            TrafficRefused,
            TrafficStationFollow,
            TrafficFleeing,
            TrafficResisting,
            FootAwaitingBackup,
            FootBackupContained,
            TrafficAwaitingBackup,
            TrafficBackupContained,
            FootAwaitingPlayerArrest,
            FootPlayerArresting,
            BackupSecuring,
            BackupEscort,
            BackupTransport
        }

        private enum PendingAnimation
        {
            None,
            Documents,
            HandcuffedIdle,
            CompliantKneel,
            PairedArrest
        }

        private sealed class CollisionGuard
        {
            internal Vehicle Vehicle;
            internal Entity Target;
            internal DateTime ExpiresAt;
        }

        private enum SuspectHitZone
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

        private sealed class SubjectInjuryState
        {
            internal int OriginalHealth;
            internal int OriginalMaximumHealth;
            internal int ManagedMaximumHealth;
            internal int ManagedHealthAtProtectionStart;
            internal int OriginalSelectedWeaponHash;
            internal int LastHealth;
            internal int DamageTaken;
            internal int RegisteredHits;
            internal int WeaponHits;
            internal int DownedHits;
            internal int FatalDownedHits;
            internal int MeleeHits;
            internal int HeadHits;
            internal int ChestHits;
            internal int StomachHits;
            internal int LastValidDamageBone;
            internal DateTime LastValidDamageBoneAt = DateTime.MinValue;
            internal string InteractionId;
            internal string LastDamageKind;
            internal int LastDamageWeaponHash;
            internal int LastDamageAttackerHandle;
            internal int LastDamageGameTime;
            internal bool HasLastProcessedDamageRecord;
            internal int LastProcessedDamageWeaponHash;
            internal int LastProcessedDamageAttackerHandle;
            internal int LastProcessedDamageGameTime;
            internal int LastProcessedDamageBone;
            internal bool OriginalCanSufferCriticalHits;
            internal bool OriginalCanSwitchWeapons;
            internal bool ControlledInjuryActive;
            internal bool HealthReconciliationLogged;
            internal bool Wounded;
            internal bool Fatal;
            internal bool WeaponDisabledByInjury;
            internal bool WritheTaskStarted;
            internal bool WritheSuppressedForCitizenContact;
            internal bool MeleeReactionPending;
            internal DateTime MeleeReactionUntil = DateTime.MinValue;
            internal DateTime NextMeleeRecoveryCheckAt = DateTime.MinValue;
        }

        private readonly LSImmersiveLog _log;
        private readonly LSPDAudioDispatch _audio;
        private readonly LSPDGangDataIntegration _gangData;
        private readonly LSPDProfile _profile;
        private readonly LSNPCDatabase _database;
        private string _interactionAudioScope = string.Empty;
        private string _interactionAudioOccurrence = string.Empty;
        private int _documentAudioSequence;
        private int _trafficRefusalAudioSequence;
        private bool _footAggressionAudioReported;
        private readonly Dictionary<int, DateTime> _recentlyReleasedVehicles =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _recentSceneReactions =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _recentDeescalations =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _recentApproachCandidates =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _recentTrafficApproachBrakes =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, float> _trafficVehicleSafetyRadii =
            new Dictionary<int, float>();
        private readonly List<Ped> _trafficOperationActors = new List<Ped>(MaximumTrafficOperationAnchors);
        private readonly List<Vehicle> _trafficOperationVehicles = new List<Vehicle>(MaximumTrafficOperationAnchors);
        private readonly HashSet<int> _trafficDriversSeen = new HashSet<int>();
        private readonly HashSet<int> _sceneReactionPedsSeen = new HashSet<int>();
        private readonly Dictionary<int, CollisionGuard> _collisionGuards =
            new Dictionary<int, CollisionGuard>();
        private readonly HashSet<int> _protectedSceneActorHandles =
            new HashSet<int>();
        private readonly HashSet<int> _protectedSceneVehicleHandles =
            new HashSet<int>();
        private readonly Dictionary<int, Ped> _protectedSceneActors =
            new Dictionary<int, Ped>();
        private readonly Dictionary<int, Vehicle> _protectedSceneVehicles =
            new Dictionary<int, Vehicle>();
        private readonly Dictionary<int, SubjectInjuryState> _subjectInjuries =
            new Dictionary<int, SubjectInjuryState>();
        private readonly Random _random = new Random();
        private readonly LSPDControlBindings _controls;
        private readonly LSPoliceNpcResponseSettings _settings;
        private readonly LSPoliceTrafficSettings _trafficSettings;
        private LSPoliceSuspectInjurySettings _injurySettings =
            LSPoliceSuspectInjurySettings.Default();
        private InteractionStage _stageValue;
        private InteractionStage _stage
        {
            get { return _stageValue; }
            set
            {
                if (_stageValue == value)
                    return;

                LSDeveloperRuntime.StateTransition(
                    _log == null ? string.Empty : _log.SessionId,
                    "NPCResponse",
                    "NPC_INTERACTION_STATE",
                    _stageValue.ToString(),
                    value.ToString(),
                    _subject == null ? 0 : _subject.Handle,
                    _backupOfficer == null ? 0 : _backupOfficer.Handle,
                    _subjectVehicle == null
                        ? (_backupVehicle == null ? 0 : _backupVehicle.Handle)
                        : _subjectVehicle.Handle,
                    "BackupRequested=" + _backupRequested);
                _stageValue = value;
            }
        }
        private Ped _subject;
        // _subjectVehicle remains the original Vehicle Citizen contact record.
        // Flight may move the same selected Citizen into a different vehicle;
        // pursuit tasks use _trafficPursuitVehicle instead.
        private Vehicle _subjectVehicle;
        private Vehicle _trafficPursuitVehicle;
        private TrafficPursuitStage _trafficPursuitStage;
        // GTA ambient cleanup may remove an ordinary driver as soon as its
        // original traffic task is replaced.  A contact temporarily owns one
        // civilian and, where applicable, one vehicle, so preserve their
        // prior persistence values and restore them on every terminal path.
        private bool _subjectPersistenceCaptured;
        private bool _subjectWasPersistent;
        private bool _subjectInvincibilityCaptured;
        private bool _subjectWasInvincible;
        private bool _subjectVehiclePersistenceCaptured;
        private bool _subjectVehicleWasPersistent;
        private Ped _approachCandidate;
        private Ped _backupOfficer;
        private Vehicle _backupVehicle;
        private Blip _subjectBlip;
        private DateTime _readyAt = DateTime.MinValue;
        private DateTime _expiresAt = DateTime.MinValue;
        private DateTime _nextSubjectHold = DateTime.MinValue;
        private DateTime _nextTrafficCaution = DateTime.MinValue;
        private DateTime _nextTrafficApproachBrakeTrim = DateTime.MinValue;
        private int _trafficOperationAnchorCursor;
        private DateTime _nextSceneReactionScan = DateTime.MinValue;
        private DateTime _nextDeescalationScan = DateTime.MinValue;
        private DateTime _nextApproachScan = DateTime.MinValue;
        private DateTime _approachCandidateUntil = DateTime.MinValue;
        private DateTime _animationHoldUntil = DateTime.MinValue;
        private DateTime _fleeStartedAt = DateTime.MinValue;
        private DateTime _lastFleeSampleAt = DateTime.MinValue;
        private DateTime _nextFleeTaskAt = DateTime.MinValue;
        private DateTime _nextResistanceTaskAt = DateTime.MinValue;
        private DateTime _nextSuspiciousApproachActionAt = DateTime.MinValue;
        private DateTime _fleeStoppedAt = DateTime.MinValue;
        private DateTime _nextStationTaskAt = DateTime.MinValue;
        private DateTime _nextPullOverTaskAt = DateTime.MinValue;
        private DateTime _pullOverDeadline = DateTime.MinValue;
        private DateTime _trafficStoppedSince = DateTime.MinValue;
        private DateTime _nextTrafficBrakeTaskAt = DateTime.MinValue;
        private DateTime _nextTrafficHoldAt = DateTime.MinValue;
        private DateTime _nextTrafficDriverExitCheckAt = DateTime.MinValue;
        private DateTime _nextTrafficDriverExitAttemptAt = DateTime.MinValue;
        private DateTime _trafficDriverExitDeadline = DateTime.MinValue;
        private DateTime _trafficVehicleTransitionStartedAt = DateTime.MinValue;
        private DateTime _trafficSubjectLostSince = DateTime.MinValue;
        private DateTime _nextTrafficBackupPursuitSampleAt = DateTime.MinValue;
        private DateTime _nextTrafficPursuitExitAt = DateTime.MinValue;
        private DateTime _trafficPlayerAimStartedAt = DateTime.MinValue;
        private int _trafficPursuitExitAttempts;
        private int _trafficPlayerAimSubjectHandle;
        private bool _trafficPursuitExitFailureNotified;
        private bool _trafficPlayerAimCheckFailureLogged;
        private DateTime _nextAnimationRequest = DateTime.MinValue;
        private DateTime _animationLoadDeadline = DateTime.MinValue;
        private DateTime _animationVerificationAt = DateTime.MinValue;
        private DateTime _animationVerificationDeadline = DateTime.MinValue;
        private DateTime _backupPhaseDeadline = DateTime.MinValue;
        private DateTime _nextBackupSubjectTask = DateTime.MinValue;
        private DateTime _deferredDeadSubjectCreatedAt = DateTime.MinValue;
        private DateTime _nextDeferredDeadSubjectScanAt = DateTime.MinValue;
        private Vector3 _fleeLastPosition = Vector3.Zero;
        private Vector3 _trafficPullOverPosition = Vector3.Zero;
        private Vector3 _trafficContactVehiclePosition = Vector3.Zero;
        private Vector3 _trafficContactPlayerPosition = Vector3.Zero;
        private Vector3 _trafficStopConfirmedPosition = Vector3.Zero;
        private TrafficDriverWindowGeometry _trafficDriverWindowGeometry;
        private bool _trafficDirectFallbackLogged;
        private Vector3 _interactionAnchor = Vector3.Zero;
        private Vector3 _footStationDestination = Vector3.Zero;
        private Vector3 _trafficStationDestination = Vector3.Zero;
        private string _footStationName = string.Empty;
        private string _trafficStationName = string.Empty;
        private PendingAnimation _animationVerificationAction = PendingAnimation.None;
        private bool _animationVerificationPassed;
        private bool _footGreetingConfirmed;
        private int _footGreetingAttempts;
        private int _animationVerificationSceneId = -1;
        private string _animationVerificationDictionary = string.Empty;
        private string _animationVerificationSubjectClip = string.Empty;
        private string _animationVerificationPlayerClip = string.Empty;
        private Ped _deferredDeadSubject;
        private Ped _animationVerificationSubject;
        private Ped _animationVerificationPlayer;
        private bool _fleeMovementConfirmed;
        private bool _footContactFled;
        private bool _trafficContactFled;
        private bool _fleeRecoveryIssued;
        private bool _suspiciousFootContact;
        private bool _approachCandidateSuspicious;
        private bool _approachCandidateTraffic;
        private bool _approachCandidateTrafficTaskIssued;
        private bool _suspiciousTrafficContact;
        private bool _deferredDeadSubjectPersistenceCaptured;
        private bool _pullOverRecoveryIssued;
        private bool _trafficPullOverFallbackTarget;
        private bool _trafficStopConfirmedAtTarget;
        private bool _trafficStopConfirmed;
        private bool _trafficComplianceExitRequested;
        private bool _trafficDriverExitConfirmed;
        private int _trafficDriverExitAttempts;
        private bool _approachCandidateStopped;
        private bool _backupRequested;
        private bool _documentsProvided;
        private bool _citizenDocumentDecisionResolved;
        private bool _backupBackgroundTransport;
        private bool _backupEscortTaskIssued;
        private bool _backupEntryTaskIssued;
        private bool _backupVehicleDoorOpened;
        private bool _backupVehicleDoorTaskIssued;
        private bool _backupVehicleDoorFallbackIssued;
        private DateTime _backupVehicleDoorTaskIssuedAt = DateTime.MinValue;
        private bool _backupArrestTaskIssued;
        private bool _backupArrestPlaybackConfirmed;
        private int _backupArrestAttempts;
        private DateTime _backupArrestAnimationHoldUntil = DateTime.MinValue;
        private DateTime _nextBackupMovementClipsetCheckAt = DateTime.MinValue;
        private DateTime _backupMovementClipsetDeadline = DateTime.MinValue;
        private DateTime _nextBackupCustodyRefreshAt = DateTime.MinValue;
        private bool _backupMovementClipsetApplied;
        private bool _backupMovementClipsetUnavailableLogged;
        private bool _deferredDeadSubjectWasPersistent;
        private bool _backupCustodyOfferPresented;
        private bool _backupInterceptionInvestigationRequired;
        private bool _footPlayerArrested;
        private bool _footPlayerArrestPlaybackConfirmed;
        private int _footPlayerArrestAttempts;
        private DateTime _footPlayerArrestDeadline = DateTime.MinValue;
        private bool _secureKeyDown;
        private PendingAnimation _pendingAnimation;
        private int _pendingAnimationDuration;
        private LSPDNPCRecord _currentRecord;
        private bool _dispatcherValidationReceived;
        private bool _citizenRecordReviewed;
        private bool _acceptKeyDown;
        private bool _rejectKeyDown;

        private float FootContactRadius
        {
            get
            {
                return _settings == null
                    ? DefaultFootContactRadius
                    : _settings.InteractionRadius;
            }
        }

        private float TrafficContactRadius
        {
            get
            {
                return _settings == null
                    ? DefaultTrafficContactRadius
                    : _settings.TrafficAwarenessRadius;
            }
        }

        private int DocumentPresentationMilliseconds
        {
            get { return Math.Max(1000, _settings.DocumentPresentationSeconds * 1000); }
        }

        private int InteractionTimeoutMilliseconds
        {
            get { return Math.Max(20000, _settings.InteractionTimeoutSeconds * 1000); }
        }

        private float ActiveSceneCivilianRadius
        {
            get { return Math.Max(5.0f, _settings.ScenePedReactionRadius); }
        }

        private int SceneReactionCooldownMilliseconds
        {
            get { return Math.Max(5000, _settings.SceneReactionCooldownSeconds * 1000); }
        }

        private float LocalTrafficCautionRadius
        {
            get
            {
                if (_settings == null || _trafficSettings == null)
                    return DefaultLocalTrafficCautionRadius;
                return Math.Min(
                    _settings.TrafficAwarenessRadius,
                    _trafficSettings.SceneControlRadius);
            }
        }

        private float ActiveSceneTrafficCautionRadius
        {
            get
            {
                if (_settings == null || _trafficSettings == null)
                    return DefaultActiveSceneTrafficCautionRadius;
                return Math.Min(
                    _settings.TrafficAwarenessRadius,
                    _trafficSettings.SceneControlRadius);
            }
        }

        private int CollisionGuardMilliseconds
        {
            get
            {
                return _settings == null
                    ? DefaultCollisionGuardMilliseconds
                    : Math.Max(
                        DefaultCollisionGuardMilliseconds,
                        _settings.CollisionGuardCooldownMilliseconds);
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        internal LSPDNPCResponse(LSImmersiveLog log)
            : this(log, null, null, null, null, null, null, null)
        {
        }

        internal LSPDNPCResponse(
            LSImmersiveLog log,
            LSPDGangDataIntegration gangData)
            : this(log, gangData, null, null, null, null, null)
        {
        }

        internal LSPDNPCResponse(
            LSImmersiveLog log,
            LSPDGangDataIntegration gangData,
            LSPDControlBindings controls)
            : this(log, gangData, controls, null, null, null, null, null)
        {
        }

        internal LSPDNPCResponse(
            LSImmersiveLog log,
            LSPDGangDataIntegration gangData,
            LSPDControlBindings controls,
            LSPDProfile profile)
            : this(log, gangData, controls, profile, null, null, null, null)
        {
        }

        internal LSPDNPCResponse(
            LSImmersiveLog log,
            LSPDGangDataIntegration gangData,
            LSPDControlBindings controls,
            LSPDProfile profile,
            LSPoliceNpcResponseSettings settings,
            LSPoliceTrafficSettings trafficSettings,
            LSNPCDatabase database)
            : this(log, gangData, controls, profile, settings, trafficSettings, database, null)
        {
        }

        internal LSPDNPCResponse(
            LSImmersiveLog log,
            LSPDGangDataIntegration gangData,
            LSPDControlBindings controls,
            LSPDProfile profile,
            LSPoliceNpcResponseSettings settings,
            LSPoliceTrafficSettings trafficSettings,
            LSNPCDatabase database,
            LSPDAudioDispatch audio)
        {
            _log = log ?? throw new ArgumentNullException("log");
            _audio = audio;
            _gangData = gangData;
            _controls = controls ?? LSPDControlBindings.Default();
            _profile = profile;
            _settings = settings ?? LSPoliceNpcResponseSettings.Default();
            _trafficSettings = trafficSettings ?? LSPoliceTrafficSettings.Default();
            _database = database;
        }

        private void BeginInteractionAudioOccurrence(Ped subject)
        {
            if (!IsUsable(subject))
                return;
            _interactionAudioScope = "citizen-" + _log.SessionId;
            _interactionAudioOccurrence = "contact-" + subject.Handle
                + "-" + DateTime.UtcNow.Ticks;
            _documentAudioSequence = 0;
            _trafficRefusalAudioSequence = 0;
            _footAggressionAudioReported = false;
        }

        private void ReportInteractionAudio(string eventId, string transition)
        {
            if (_audio == null || string.IsNullOrWhiteSpace(_interactionAudioScope)
                || string.IsNullOrWhiteSpace(_interactionAudioOccurrence)
                || string.IsNullOrWhiteSpace(transition))
                return;
            try
            {
                _audio.Report(
                    eventId,
                    _interactionAudioScope,
                    _interactionAudioOccurrence + ":" + transition,
                    _subjectVehicle == null ? "civilian" : "traffic");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_INTERACTION_AUDIO_REPORT_FAILED", ex);
            }
        }

        private void ReportCitizenDocumentRequestAudio()
        {
            _documentAudioSequence++;
            ReportInteractionAudio(
                _subjectVehicle == null
                    ? "lsimmersivelife.police.civilian.identification_requested"
                    : "lsimmersivelife.police.traffic.documents_requested",
                "documents-requested-" + _documentAudioSequence);
        }

        internal void ConfigureSuspectInjurySettings(
            LSPoliceSuspectInjurySettings settings)
        {
            _injurySettings = settings ?? LSPoliceSuspectInjurySettings.Default();
            _injurySettings.Normalize();
        }

        internal bool HasActiveInteraction
        {
            get { return _stage != InteractionStage.None && !_backupBackgroundTransport; }
        }

        internal bool CanRequestBackup
        {
            get
            {
                return HasActiveInteraction && !_backupRequested
                    && (_stage == InteractionStage.FootAwaitingBackup
                        || _stage == InteractionStage.TrafficAwaitingBackup
                        || _stage == InteractionStage.FootResisting
                        || _stage == InteractionStage.FootFleeing
                        || _stage == InteractionStage.TrafficFleeing
                        || _stage == InteractionStage.TrafficResisting
                        || _stage == InteractionStage.FootRefused
                        || _stage == InteractionStage.TrafficRefused);
            }
        }

        internal bool CanRequestBackupTransport
        {
            get
            {
                return HasActiveInteraction && !_backupRequested
                    && _footPlayerArrested
                    && (_stage == InteractionStage.FootAwaitingBackup
                        || _stage == InteractionStage.TrafficAwaitingBackup)
                    && IsUsable(_subject) && !_subject.IsDead
                    && IsPedPhysicallyCuffed(_subject);
            }
        }

        internal bool HasBackupAssignment
        {
            get
            {
                // Backup remains linked to this owner while a loaded citizen
                // is travelling in the background after the player's local
                // interaction session has been released.
                return _backupRequested
                    && (_stage != InteractionStage.None || _backupBackgroundTransport);
            }
        }

        internal bool IsFleeing
        {
            get
            {
                return _stage == InteractionStage.FootFleeing
                    || _stage == InteractionStage.TrafficFleeing;
            }
        }

        internal bool IsTrafficPursuitContained
        {
            get
            {
                return HasActiveInteraction && _trafficContactFled
                    && _trafficPursuitStage == TrafficPursuitStage.Contained;
            }
        }

        internal bool WasFleeingFootContact
        {
            get
            {
                return HasActiveInteraction && _subjectVehicle == null && _footContactFled;
            }
        }

        internal bool WasFleeingTrafficContact
        {
            get
            {
                return HasActiveInteraction && _subjectVehicle != null && _trafficContactFled;
            }
        }

        internal Vector3 SupportPosition
        {
            get
            {
                Vehicle pursuitVehicle = ActivePursuitVehicle;
                if (IsUsable(pursuitVehicle)
                    && (_stage == InteractionStage.TrafficFleeing
                        || _stage == InteractionStage.TrafficAwaitingBackup
                        || _stage == InteractionStage.TrafficBackupContained))
                    return pursuitVehicle.Position;
                return IsUsable(_subject) ? _subject.Position : Vector3.Zero;
            }
        }

        internal Ped ActiveSubject { get { return IsUsable(_subject) ? _subject : null; } }
        internal Ped CurrentSubject { get { return IsUsable(_subject) ? _subject : null; } }
        internal Vehicle OriginalContactVehicle { get { return IsUsable(_subjectVehicle) ? _subjectVehicle : null; } }
        internal Vehicle ActiveSubjectVehicle { get { return IsUsable(_subjectVehicle) ? _subjectVehicle : null; } }
        internal Vehicle ActivePursuitVehicle
        {
            get
            {
                if (_trafficPursuitStage == TrafficPursuitStage.None)
                    return IsUsable(_subjectVehicle) ? _subjectVehicle : null;
                if (!IsUsable(_subject) || !_subject.IsInVehicle())
                    return null;
                try
                {
                    Vehicle current = _subject.CurrentVehicle;
                    return IsUsable(current) ? current : null;
                }
                catch { return null; }
            }
        }

        internal bool CanRequestCitizenDocuments
        {
            get
            {
                bool backupInterceptionInvestigation = IsBackupInterceptionInvestigationActive;
                bool trafficInterceptionInvestigation = IsTrafficOnFootInterceptionInvestigationActive;
                if (!HasActiveInteraction || (_backupRequested && !backupInterceptionInvestigation)
                    || !IsUsable(_subject)
                    || _pendingAnimation != PendingAnimation.None)
                    return false;

                DateTime now = DateTime.UtcNow;
                Ped player = backupInterceptionInvestigation || trafficInterceptionInvestigation
                    ? Game.Player.Character : null;
                bool greetingReady = _subjectVehicle == null
                    && _stage == InteractionStage.FootGreeting
                    && _footGreetingConfirmed
                    && now >= _animationHoldUntil;
                bool trafficWindowReady = IsUsable(_subjectVehicle)
                    && _stage == InteractionStage.TrafficPreparing
                    && IsTrafficStopAtSelectedRoadside()
                    && IsDriver(_subject, _subjectVehicle)
                    && IsSelectedTrafficDriverStillSeated()
                    && now >= _readyAt
                    && !Game.Player.Character.IsInVehicle()
                    && IsAtTrafficDriverWindow(Game.Player.Character);
                bool canRetryFootHandoff = _subjectVehicle == null
                    && _stage == InteractionStage.FootDocuments
                    && !_documentsProvided
                    && now >= _animationHoldUntil;
                bool canRetryTrafficHandoff = IsUsable(_subjectVehicle)
                    && _stage == InteractionStage.TrafficDocuments
                    && IsTrafficStopAtSelectedRoadside()
                    && IsDriver(_subject, _subjectVehicle)
                    && IsSelectedTrafficDriverStillSeated()
                    && !_documentsProvided
                    && now >= _animationHoldUntil
                    && !Game.Player.Character.IsInVehicle()
                    && IsAtTrafficDriverWindow(Game.Player.Character);
                bool backupFootInvestigationReady = backupInterceptionInvestigation
                    && _stage == InteractionStage.FootBackupContained
                    && _subjectVehicle == null
                    && !_documentsProvided
                    && !IsLivingDowned(_subject)
                    && !IsLivingInWrithe(_subject)
                    && IsUsable(player)
                    && !player.IsInVehicle()
                    && _subject.Position.DistanceTo(player.Position) <= BackupContainmentInteractionRadius
                    && now >= _animationHoldUntil;
                bool backupTrafficInvestigationReady = backupInterceptionInvestigation
                    && _stage == InteractionStage.TrafficBackupContained
                    && IsUsable(_subjectVehicle)
                    && !_subject.IsInVehicle()
                    && !_documentsProvided
                    && !IsLivingDowned(_subject)
                    && !IsLivingInWrithe(_subject)
                    && IsUsable(player)
                    && !player.IsInVehicle()
                    && _subject.Position.DistanceTo(player.Position) <= BackupContainmentInteractionRadius
                    && now >= _animationHoldUntil;
                bool trafficFootInvestigationReady = trafficInterceptionInvestigation
                    && !_documentsProvided
                    && !IsLivingDowned(_subject)
                    && !IsLivingInWrithe(_subject)
                    && IsUsable(player)
                    && !player.IsInVehicle()
                    && IsCitizenDocumentHandoffReady(player, _subject)
                    && now >= _animationHoldUntil;
                return greetingReady || trafficWindowReady
                    || canRetryFootHandoff || canRetryTrafficHandoff
                    || backupFootInvestigationReady || backupTrafficInvestigationReady
                    || trafficFootInvestigationReady;
            }
        }

        internal bool CanRequestCitizenValidation
        {
            get
            {
                return HasActiveInteraction
                    && (!_backupRequested || IsBackupInterceptionInvestigationActive)
                    && !CitizenRecordVisible
                    && !_dispatcherValidationReceived
                    && _documentsProvided
                    && IsTrafficDocumentReviewReady();
            }
        }

        internal bool CanDecideCitizenRecord
        {
            get
            {
                if (!HasActiveInteraction)
                    return false;
                if (_stage == InteractionStage.FootBackupContained
                    || _stage == InteractionStage.TrafficBackupContained)
                    return _backupCustodyOfferPresented;
                if (_backupCustodyOfferPresented)
                    return true;

                // A completed document disagreement cannot be rerolled by a
                // repeated key, controller edge, or menu command. A stopped
                // fleeing subject retains its explicit same-branch clear/retry
                // choice without drawing a new random outcome.
                bool sameBranchFollowUp = _citizenDocumentDecisionResolved
                    && (_stage == InteractionStage.FootRefused
                        || _stage == InteractionStage.TrafficRefused);
                bool documentReviewComplete = _currentRecord != null
                    && _dispatcherValidationReceived
                    && _citizenRecordReviewed
                    && !CitizenRecordVisible
                    && IsTrafficDocumentReviewReady();
                return documentReviewComplete
                    && (!_citizenDocumentDecisionResolved || sameBranchFollowUp);
            }
        }

        internal bool CanArrestCitizen
        {
            get
            {
                if (!HasActiveInteraction || _backupRequested
                    || !IsUsable(_subject) || _subject.IsDead || _subject.Health <= 0
                    || IsSubjectMeleeReactionPending(_subject))
                    return false;

                bool reviewedDecision = _currentRecord != null
                    && _dispatcherValidationReceived
                    && _citizenRecordReviewed
                    && !CitizenRecordVisible;
                if (!reviewedDecision)
                    return false;

                bool trafficFlightContainedOnFoot = _subjectVehicle != null
                    && _trafficContactFled
                    && _trafficPursuitStage == TrafficPursuitStage.Contained
                    && !_subject.IsInVehicle();

                if (_stage == InteractionStage.FootAwaitingPlayerArrest)
                    return _subjectVehicle == null;

                if (_subjectVehicle != null)
                {
                    if (!trafficFlightContainedOnFoot
                        && !IsSelectedTrafficDriverOutside())
                        return false;
                    if (_stage == InteractionStage.TrafficAwaitingBackup
                        && IsTrafficStopAtSelectedRoadside()
                        && _trafficDriverExitConfirmed)
                        return true;
                    if (_stage != InteractionStage.TrafficRefused
                        && _stage != InteractionStage.TrafficResisting
                        && _stage != InteractionStage.TrafficFleeing)
                        return false;
                }
                else if (_stage != InteractionStage.FootRefused
                    && _stage != InteractionStage.FootResisting
                    && _stage != InteractionStage.FootFleeing)
                    return false;

                // A rejected document decision authorizes the response branch,
                // not an arrest by itself. A pursuing/resisting subject becomes
                // arrestable only after the same living Ped is down or contained.
                return IsLivingDowned(_subject) || IsLivingInWrithe(_subject)
                    || trafficFlightContainedOnFoot;
            }
        }

        internal bool CanReleaseCitizen
        {
            get
            {
                return HasActiveInteraction
                    && !_backupRequested
                    && _currentRecord != null
                    && _dispatcherValidationReceived
                    && _citizenRecordReviewed
                    && !CitizenRecordVisible
                    && _stage != InteractionStage.FootFleeing
                    && _stage != InteractionStage.FootStationFollow
                    && _stage != InteractionStage.FootResisting
                    && _stage != InteractionStage.TrafficFleeing
                    && _stage != InteractionStage.TrafficStationFollow
                    && _stage != InteractionStage.TrafficResisting
                    && _stage != InteractionStage.FootBackupContained
                    && _stage != InteractionStage.TrafficBackupContained;
            }
        }

        internal bool CanReviewBackupCustodyOffer
        {
            get
            {
                return (_stage == InteractionStage.FootBackupContained
                        || _stage == InteractionStage.TrafficBackupContained)
                    && HasBackupAssignment;
            }
        }

        private bool IsBackupInterceptionInvestigationActive
        {
            get
            {
                return _backupInterceptionInvestigationRequired
                    && _backupRequested
                    && HasBackupAssignment
                    && (_stage == InteractionStage.FootBackupContained
                        || _stage == InteractionStage.TrafficBackupContained);
            }
        }

        private bool IsTrafficOnFootInterceptionInvestigationActive
        {
            get
            {
                return !_backupRequested && HasActiveInteraction
                    && _trafficContactFled
                    && _trafficPursuitStage == TrafficPursuitStage.Contained
                    && _stage == InteractionStage.TrafficRefused
                    && IsUsable(_subject) && !_subject.IsInVehicle();
            }
        }

        private bool IsTrafficDocumentReviewReady()
        {
            if (_subjectVehicle == null)
                return true;
            if (IsTrafficOnFootInterceptionInvestigationActive)
            {
                Ped player = Game.Player.Character;
                return IsUsable(player) && !player.IsInVehicle()
                    && IsCitizenDocumentHandoffReady(player, _subject);
            }
            if (IsBackupInterceptionInvestigationActive
                && _stage == InteractionStage.TrafficBackupContained)
            {
                Ped player = Game.Player.Character;
                return IsUsable(_subject)
                    && IsUsable(_subjectVehicle)
                    && !_subject.IsInVehicle()
                    && IsUsable(player)
                    && !player.IsInVehicle()
                    && _subject.Position.DistanceTo(player.Position)
                        <= BackupContainmentInteractionRadius;
            }
            return _stage == InteractionStage.TrafficDocuments
                && IsTrafficStopAtSelectedRoadside()
                && IsDriver(_subject, _subjectVehicle)
                && IsSelectedTrafficDriverStillSeated();
        }

        internal bool CanBeginPlayerVehicleCustody
        {
            get
            {
                bool subjectReadyForCustody = IsUsable(_subject)
                    && !_subject.IsDead
                    && IsPedPhysicallyCuffed(_subject)
                    && (_subjectVehicle == null
                        || (_trafficStopConfirmed
                            && _trafficDriverExitConfirmed
                            && IsSelectedTrafficDriverOutside()));
                return HasActiveInteraction
                    && _footPlayerArrested
                    && !_backupRequested
                    && _stage == InteractionStage.FootAwaitingBackup
                    && subjectReadyForCustody;
            }
        }

        internal bool IsLoadedInBackupVehicle
        {
            get
            {
                return _stage == InteractionStage.BackupTransport
                    && IsUsable(_subject) && IsUsable(_backupVehicle)
                    && _subject.CurrentVehicle != null
                    && _subject.CurrentVehicle.Exists()
                    && _subject.CurrentVehicle.Handle == _backupVehicle.Handle;
            }
        }

        internal bool IsBackupPhysicalEscortActive
        {
            get
            {
                return _stage == InteractionStage.BackupEscort
                    && !_backupVehicleDoorOpened
                    && IsUsable(_subject)
                    && IsUsable(_backupOfficer)
                    && IsUsable(_backupVehicle);
            }
        }

        /// <summary>
        /// True while the Citizen and Backup officer use their paired natural
        /// navigation tasks. Backup must not replace either task with a
        /// secondary LookAt/AimAt task during this window.
        /// </summary>
        internal bool IsBackupEscortMovementActive
        {
            get
            {
                return _stage == InteractionStage.BackupEscort
                    && !_backupVehicleDoorOpened
                    && IsUsable(_subject)
                    && IsUsable(_backupOfficer)
                    && IsUsable(_backupVehicle);
            }
        }

        internal bool IsBackupPrisonerLoading
        {
            get
            {
                // The officer deliberately detaches the subject at the rear
                // door. From this point until vehicle entry, the unit is in a
                // loading phase and must not restart the officer approach.
                return _stage == InteractionStage.BackupEscort
                    && _backupVehicleDoorOpened
                    && IsUsable(_subject)
                    && IsUsable(_backupVehicle);
            }
        }

        /// <summary>
        /// True while the assigned Backup officer's current arrest task is
        /// waiting for physical cuff confirmation. Backup must not replace it
        /// with a secondary LookAt task during that interval.
        /// </summary>
        internal bool IsBackupCustodyActionActive
        {
            get
            {
                return _stage == InteractionStage.BackupSecuring
                    && _backupArrestTaskIssued
                    && IsUsable(_subject)
                    && IsUsable(_backupOfficer);
            }
        }

        internal bool IsBackupContainmentAwaitingPlayer
        {
            get
            {
                return _stage == InteractionStage.FootBackupContained
                    || _stage == InteractionStage.TrafficBackupContained;
            }
        }

        /// <summary>
        /// Releases the NPC interaction owner after the player has completed
        /// the arrest. The entity is deliberately left untouched: Convoy has
        /// already prepared the same ped as its living, handcuffed prisoner
        /// and now owns persistence, tasks, and terminal cleanup.
        /// </summary>
        internal bool TransferArrestedSubjectToConvoy()
        {
            if (!CanBeginPlayerVehicleCustody)
                return false;

            int handle = _subject.Handle;
            int vehicleHandle = IsUsable(_subjectVehicle) ? _subjectVehicle.Handle : 0;
            if (IsUsable(_subjectVehicle))
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_BRAKE, _subjectVehicle, false);
                }
                catch (Exception ex) { _log.Exception("NPC_TRAFFIC_VEHICLE_RELEASE_FOR_CONVOY_FAILED", ex); }
            }
            try
            {
                if (_subjectVehiclePersistenceCaptured && IsUsable(_subjectVehicle))
                    _subjectVehicle.IsPersistent = _subjectVehicleWasPersistent;
            }
            catch (Exception ex) { _log.Exception("NPC_TRAFFIC_PERSISTENCE_TRANSFER_FAILED", ex); }
            _subjectVehiclePersistenceCaptured = false;
            _subjectVehicleWasPersistent = false;
            // Convoy now owns the physically handcuffed Ped; the original
            // traffic vehicle remains under the ambient world owner.
            _subjectPersistenceCaptured = false;
            ClearInteractionState();
            _log.Runtime(
                "NPC_CUSTODY_TRANSFERRED_TO_CONVOY",
                "Ped=" + handle + "; Vehicle=" + vehicleHandle
                + "; Owner=Convoy; ArrestAlreadyCompleted=true; SamePed=true");
            return true;
        }

        internal string RequestBackupSupportCommand()
        {
            if (!CanRequestBackup)
                return HasBackupAssignment
                    ? "Backup is already assigned to this NPC contact."
                    : "Finish the document decision or begin a pursuit before requesting NPC Backup.";

            _backupRequested = true;
            _expiresAt = DateTime.UtcNow.AddMinutes(6);
            if (_stage == InteractionStage.FootRefused)
                _stage = InteractionStage.FootAwaitingBackup;
            else if (_stage == InteractionStage.TrafficRefused)
                _stage = InteractionStage.TrafficAwaitingBackup;
            EnsureSubjectBlip("NPC Contact");
            bool fleeing = IsFleeing || WasFleeingFootContact || WasFleeingTrafficContact;
            _log.Runtime("NPC_BACKUP_HANDOFF_REQUESTED",
                "Ped=" + (_subject == null ? 0 : _subject.Handle)
                + "; Stage=" + _stage + "; Fleeing=" + fleeing);
            return fleeing
                ? "Backup received the fleeing citizen's tracked position and will attempt an interception."
                : _footPlayerArrested
                    ? "Backup transport requested for the physically handcuffed citizen. Keep the subject at the contact until the unit arrives."
                    : "Backup requested for the compliant citizen. Keep the subject at the contact until the unit arrives.";
        }

        internal string RequestBackupTransportCommand(Ped player)
        {
            if (_backupBackgroundTransport)
                return "Backup has already departed with the secured citizen.";

            if (_stage == InteractionStage.FootBackupContained
                || _stage == InteractionStage.TrafficBackupContained)
            {
                PresentBackupCustodyOffer(player);
                return _backupCustodyOfferPresented
                    ? "Backup escort is ready. Select Accept to authorize physical arrest, handcuffing, loading, and transport."
                    : "Complete the contained citizen's required document review before accepting Backup escort.";
            }

            if (HasBackupAssignment)
            {
                if ((_stage == InteractionStage.FootAwaitingBackup
                        || _stage == InteractionStage.TrafficAwaitingBackup)
                    && _footPlayerArrested && IsUsable(_subject)
                    && IsPedPhysicallyCuffed(_subject))
                    return "Backup transport is en route for the secured citizen. Keep the same citizen at the scene until the unit arrives.";
                if (_stage == InteractionStage.BackupSecuring)
                    return "Backup is physically securing the same citizen. Wait for confirmed handcuffing and loading.";
                if (_stage == InteractionStage.BackupEscort)
                    return "Backup is escorting the same citizen to its utility for loading.";
                if (_stage == InteractionStage.BackupTransport)
                    return "The same citizen is loaded. Backup will end the interaction after the utility departs.";
                return "Backup is already assigned to this citizen contact. Wait for containment, then accept the custody offer.";
            }

            if (CanRequestBackupTransport)
                return "A Backup transport escort can be requested for the physically handcuffed citizen.";

            if (CanRequestBackup)
                return "Use Back Up Response and select Request a Back Up NPC Ped Interaction for contact assistance. Request transport after the citizen is physically secured.";

            return "Request Backup transport after the selected citizen has been physically handcuffed, or after accepting a contained Backup custody offer.";
        }

        /// <summary>
        /// Backup calls this only after an owned unit physically arrives. NPC
        /// Reaction stops the selected ambient subject and preserves its own
        /// state; Backup continues to own the officer and transport vehicle.
        /// </summary>
        internal bool BeginBackupIntervention(Ped officer, Vehicle vehicle)
        {
            if (!HasBackupAssignment || !IsUsable(_subject)
                || !IsUsable(officer) || !IsUsable(vehicle))
                return false;
            if (_stage == InteractionStage.FootBackupContained
                || _stage == InteractionStage.TrafficBackupContained)
            {
                _backupOfficer = officer;
                _backupVehicle = vehicle;
                return true;
            }
            if (_stage == InteractionStage.BackupSecuring
                || _stage == InteractionStage.BackupEscort
                || _stage == InteractionStage.BackupTransport)
                return true;

            DateTime now = DateTime.UtcNow;
            bool subjectWasFleeing = IsFleeing
                || WasFleeingFootContact
                || _trafficContactFled;
            bool custodyRiskBranch = subjectWasFleeing
                || _suspiciousFootContact
                || _stage == InteractionStage.FootAwaitingPlayerArrest
                || _stage == InteractionStage.TrafficAwaitingBackup;
            if (custodyRiskBranch)
                EnableControlledCitizenInjury(_subject, "BackupContainment");
            RestoreBackupCustodyMovementStyle();
            _backupOfficer = officer;
            _backupVehicle = vehicle;
            _stage = subjectWasFleeing
                ? (_subjectVehicle == null
                    ? InteractionStage.FootBackupContained
                    : InteractionStage.TrafficBackupContained)
                : InteractionStage.BackupSecuring;
            _backupInterceptionInvestigationRequired = subjectWasFleeing
                && (_subjectVehicle != null || _suspiciousFootContact);
            if (_backupInterceptionInvestigationRequired)
            {
                _documentsProvided = false;
                _dispatcherValidationReceived = false;
                _citizenRecordReviewed = false;
                _backupCustodyOfferPresented = false;
                CitizenRecordVisible = false;
                _log.Runtime("NPC_BACKUP_INTERCEPTION_INVESTIGATION_REQUIRED",
                    "Ped=" + _subject.Handle
                    + "; SubjectType=" + (_subjectVehicle == null ? "SuspiciousFoot" : "Traffic")
                    + "; IdentityPreserved=" + (_currentRecord != null));
            }
            _backupPhaseDeadline = now.AddSeconds(BackupPhysicalEscortTimeoutSeconds);
            _nextBackupSubjectTask = now;
            _backupArrestTaskIssued = false;
            _backupArrestAttempts = 0;
            _backupArrestAnimationHoldUntil = DateTime.MinValue;
            _backupArrestPlaybackConfirmed = false;
            _backupEscortTaskIssued = false;
            _backupEntryTaskIssued = false;
            _backupVehicleDoorOpened = false;
            _backupVehicleDoorTaskIssued = false;
            _backupVehicleDoorFallbackIssued = false;
            _backupVehicleDoorTaskIssuedAt = DateTime.MinValue;
            _nextBackupMovementClipsetCheckAt = DateTime.MinValue;
            _backupMovementClipsetDeadline = DateTime.MinValue;
            _nextBackupCustodyRefreshAt = DateTime.MinValue;
            _backupMovementClipsetUnavailableLogged = false;
            SetSubjectPoliceAware(_subject);
            try
            {
                Vehicle occupiedVehicle = _subject.CurrentVehicle;
                if (IsUsable(occupiedVehicle)
                    && occupiedVehicle.Handle != vehicle.Handle)
                {
                    bool stopped = Math.Abs(occupiedVehicle.Speed) <= 0.65f;
                    if (stopped)
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, occupiedVehicle, true);
                        Function.Call(Hash.TASK_LEAVE_VEHICLE,
                            _subject, occupiedVehicle, 0);
                    }
                    else
                    {
                        Function.Call(Hash.SET_VEHICLE_BRAKE, occupiedVehicle, true);
                        _log.Runtime("NPC_BACKUP_CONTAINMENT_WAITING_FOR_SUBJECT_VEHICLE_STOP",
                            "Ped=" + _subject.Handle + "; Vehicle=" + occupiedVehicle.Handle
                            + "; Speed=" + Math.Abs(occupiedVehicle.Speed).ToString("0.0")
                            + "; CitizenVehicleNotFrozen=true");
                    }
                }
                else if (!_subject.IsInVehicle() && !_footPlayerArrested
                    && !IsLivingDowned(_subject)
                    && !IsLivingInjuredSubject(_subject))
                {
                    // A fleeing citizen remains mobile only while Backup is
                    // travelling. This method is called after the owned unit
                    // has physically arrived, so the flee task must now be
                    // replaced with a real compliance task or the officer
                    // will chase a subject that never enters BackupSecuring.
                    _subject.Task.ClearAll();
                    _subject.Task.HandsUp(30000);
                    if (subjectWasFleeing)
                    {
                        _log.Runtime("NPC_BACKUP_FLEEING_SUBJECT_STOPPED",
                            "Ped=" + _subject.Handle + "; Officer=" + officer.Handle);
                        if (_subjectVehicle == null)
                        {
                            _log.Runtime("NPC_BACKUP_FLEEING_CONTAINMENT_CONFIRMED",
                                "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                + "; Vehicle=" + vehicle.Handle);
                            Notify(_backupInterceptionInvestigationRequired
                                ? "Backup contained the fleeing citizen. Approach, open Citizen Ped Response with "
                                    + _controls.InteractionKey + ", and recheck the citizen's documents before custody."
                                : "Backup has contained the fleeing citizen. Approach and press "
                                    + _controls.InteractionKey + " to review the custody response.");
                        }
                        else
                        {
                            _log.Runtime("NPC_BACKUP_TRAFFIC_FLEEING_CONTAINMENT_CONFIRMED",
                                "Driver=" + _subject.Handle + "; SubjectVehicle="
                                + _subjectVehicle.Handle + "; Officer=" + officer.Handle
                                + "; BackupVehicle=" + vehicle.Handle);
                            Notify(_backupInterceptionInvestigationRequired
                                ? "Backup stopped the fleeing driver. Wait for the driver to exit, then open Citizen Ped Response with "
                                    + _controls.InteractionKey + " and recheck the driver's documents before custody."
                                : "Backup stopped the fleeing driver. Approach and press "
                                    + _controls.InteractionKey + " to review the custody response.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_BACKUP_INTERVENTION_START_FAILED", ex);
                return false;
            }

            _log.Runtime("NPC_BACKUP_INTERVENTION_STARTED",
                "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                + "; Vehicle=" + vehicle.Handle
                + "; FleeingFootContainment=" + (_stage == InteractionStage.FootBackupContained)
                + "; FleeingTrafficContainment=" + (_stage == InteractionStage.TrafficBackupContained));
            if (_stage != InteractionStage.FootBackupContained
                && _stage != InteractionStage.TrafficBackupContained)
                Notify("~b~POLICE BACKUP~s~\nBackup reached the citizen and is beginning the physical handoff.");
            return true;
        }

        /// <summary>
        /// Advances only the ambient subject's side of the handoff. Returns true
        /// after GTA confirms that the subject physically entered Backup's car.
        /// </summary>
        internal bool AdvanceBackupHandoff(Ped officer, Vehicle vehicle)
        {
            if (!HasBackupAssignment || !IsUsable(_subject)
                || !IsUsable(officer) || !IsUsable(vehicle))
                return false;
            _backupOfficer = officer;
            _backupVehicle = vehicle;
            DateTime now = DateTime.UtcNow;

            if ((_stage == InteractionStage.BackupSecuring
                    || _stage == InteractionStage.BackupEscort)
                && now >= _backupPhaseDeadline)
            {
                CancelBackupHandoff(
                    "The physical citizen handoff timed out at stage " + _stage
                    + ". The subject remains available for another Backup request.");
                return false;
            }

            // A fleeing foot subject is a containment-only stage until the
            // player explicitly accepts the Backup custody response. Do not
            // let the normal compliant-citizen handoff arrest the subject
            // automatically on the same tick that containment is confirmed.
            if (_stage == InteractionStage.FootBackupContained)
                return false;
            if (_stage == InteractionStage.TrafficBackupContained)
            {
                Vehicle currentVehicle = null;
                try { currentVehicle = _subject.CurrentVehicle; }
                catch { }
                if (IsUsable(currentVehicle) && currentVehicle.Handle != vehicle.Handle
                    && now >= _nextBackupSubjectTask)
                {
                    bool stopped = Math.Abs(currentVehicle.Speed) <= 0.65f;
                    _nextBackupSubjectTask = now.AddSeconds(stopped ? 5 : 1);
                    try
                    {
                        if (stopped)
                        {
                            HoldTrafficSubject(currentVehicle);
                            Function.Call(Hash.TASK_LEAVE_VEHICLE,
                                _subject, currentVehicle, 0);
                        }
                        else
                            Function.Call(Hash.SET_VEHICLE_BRAKE,
                                currentVehicle, true);
                    }
                    catch (Exception ex) { _log.Exception("NPC_BACKUP_TRAFFIC_SUBJECT_EXIT_FAILED", ex); }
                }
                return false;
            }

            if (_subject.CurrentVehicle != null && _subject.CurrentVehicle.Exists())
            {
                if (_subject.CurrentVehicle.Handle == vehicle.Handle)
                {
                    DetachBackupSubjectFromOfficer("SubjectConfirmedInsideBackupVehicle");
                    if (_backupVehicleDoorOpened)
                    {
                        try { Function.Call(Hash.SET_VEHICLE_DOOR_SHUT, vehicle, 2, false); }
                        catch (Exception ex) { _log.Exception("NPC_BACKUP_PRISONER_DOOR_CLOSE_FAILED", ex); }
                        _backupVehicleDoorOpened = false;
                    }
                    _stage = InteractionStage.BackupTransport;
                    CleanupSubjectBlip();
                    return true;
                }
                if (now >= _nextBackupSubjectTask)
                {
                    _nextBackupSubjectTask = now.AddSeconds(5);
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subject.CurrentVehicle, true);
                        Function.Call(Hash.TASK_LEAVE_VEHICLE, _subject, _subject.CurrentVehicle, 0);
                    }
                    catch (Exception ex) { _log.Exception("NPC_BACKUP_SUBJECT_EXIT_FAILED", ex); }
                }
                return false;
            }

            if (_stage == InteractionStage.BackupSecuring)
            {
                if (_subject.Position.DistanceTo(officer.Position) > 3.2f)
                    return false;
                bool playerAlreadySecured = _subjectVehicle == null
                    && _footPlayerArrested && IsPedPhysicallyCuffed(_subject);
                try
                {
                    _subject.CanSwitchWeapons = false;
                    CaptureBackupTransportProtection(_subject);
                    if (playerAlreadySecured)
                    {
                        // The player already completed the physical arrest.
                        // Backup takes transport custody without aiming at or
                        // re-arresting an already handcuffed citizen.
                        MaintainFootArrestedSubject();
                        _log.Runtime("NPC_BACKUP_PLAYER_CUSTODY_CONFIRMED",
                            "Ped=" + _subject.Handle
                            + "; PlayerArrestAlreadyCompleted=true"
                            + "; Officer=" + officer.Handle);
                    }
                    else if (!IsPedPhysicallyCuffed(_subject))
                    {
                        if (now < _backupArrestAnimationHoldUntil)
                            return false;

                        bool cuffed = _backupArrestPlaybackConfirmed
                            && TryApplyPhysicalHandcuffs(_subject);
                        if (cuffed)
                        {
                            _log.Runtime("NPC_BACKUP_HANDCUFF_STATE_COMMITTED",
                                "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                + "; ConfigFlag=" + HandcuffedPedConfigFlag
                                + "; Cuffed=" + IsPedPhysicallyCuffed(_subject)
                                + "; PairedPlaybackConfirmed=" + _backupArrestPlaybackConfirmed
                                + "; PhysicalHoldElapsed=true");
                        }
                        if (!IsPedPhysicallyCuffed(_subject)
                            && _backupArrestAttempts < BackupArrestMaximumAttempts
                            && (!_backupArrestTaskIssued || now >= _nextBackupSubjectTask))
                        {
                            bool downedOrInjured = IsLivingDowned(_subject)
                                || IsLivingInjuredSubject(_subject);
                            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture =
                                LSImmersiveDictionaryAnimation.ClassifyArrestTarget(_subject);
                            bool canUsePairedAnimation = posture !=
                                    LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                                && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Dead
                                && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Cuffed
                                && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling
                                && !_subject.IsInVehicle();
                            string requestedDictionary = posture ==
                                    LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded
                                ? "mp_arresting" : "mp_arrest_paired";
                            if (canUsePairedAnimation)
                            {
                                LSImmersiveDictionaryAnimation.RequestDictionary(
                                    requestedDictionary);
                                if (!LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                                    requestedDictionary))
                                {
                                    _nextBackupSubjectTask = now.AddMilliseconds(250);
                                    return false;
                                }
                            }
                            bool groundArrestAnimation = false;
                            Vector3 arrestSceneOrigin = Vector3.Zero;
                            float arrestSceneHeading = 0f;
                            _backupArrestPlaybackConfirmed = false;
                            bool pairedArrest = TryStartPairedCustodyArrest(
                                officer, _subject, out int arrestScene, out float arrestDuration,
                                out groundArrestAnimation,
                                out arrestSceneOrigin, out arrestSceneHeading);

                            if (pairedArrest)
                            {
                                string dictionary = groundArrestAnimation
                                    ? "mp_arresting" : "mp_arrest_paired";
                                string officerClip = groundArrestAnimation
                                    ? "arrest_on_floor_front_right_a" : "cop_p2_back_right";
                                string subjectClip = groundArrestAnimation
                                    ? "arrest_on_floor_front_right_b" : "crook_p2_back_right";
                                SchedulePendingAnimationVerification(
                                    now, PendingAnimation.PairedArrest, dictionary,
                                    subjectClip, _subject, officerClip, officer, arrestScene);
                                _backupArrestAnimationHoldUntil = now.AddMilliseconds(
                                    Math.Max(FootPlayerArrestMilliseconds,
                                        (int)Math.Ceiling(arrestDuration * 1000.0f) + 500));
                                _nextBackupSubjectTask = _backupArrestAnimationHoldUntil;
                            }
                            else
                            {
                                if (!downedOrInjured
                                    && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling)
                                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                        officer, _subject, 1200);
                                Function.Call(Hash.TASK_ARREST_PED, officer, _subject);
                                _backupArrestAnimationHoldUntil = now.AddMilliseconds(
                                    FootPlayerArrestMilliseconds);
                                _nextBackupSubjectTask = _backupArrestAnimationHoldUntil;
                            }

                            ObserveDeveloperTask(
                                pairedArrest ? "TASK_SYNCHRONIZED_ARREST" : "TASK_ARREST_PED",
                                officer.Handle,
                                _subject.Handle,
                                _subjectVehicle == null ? 0 : _subjectVehicle.Handle,
                                "BackupSecuring");
                            _backupArrestTaskIssued = true;
                            _backupArrestAttempts++;
                            _log.Runtime("NPC_BACKUP_PHYSICAL_ARREST_ATTEMPT",
                                "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                + "; Attempt=" + _backupArrestAttempts
                                + "; MaximumAttempts=" + BackupArrestMaximumAttempts
                                + "; DownedOrInjured=" + downedOrInjured
                                + "; Posture=" + posture
                                + "; Method=" + (pairedArrest
                                    ? groundArrestAnimation
                                        ? "SynchronizedGroundArrest"
                                        : "SynchronizedPairedAnimation"
                                    : downedOrInjured
                                        ? "GroundedTaskArrestPed"
                                        : "TaskArrestPedFallback")
                                + "; Scene=" + (pairedArrest
                                    ? arrestScene.ToString() : "none")
                                + "; Dictionary=" + (pairedArrest
                                    ? groundArrestAnimation ? "mp_arresting" : "mp_arrest_paired"
                                    : "none")
                                + "; OfficerClip=" + (pairedArrest
                                    ? groundArrestAnimation
                                        ? "arrest_on_floor_front_right_a" : "cop_p2_back_right"
                                    : "TASK_ARREST_PED")
                                + "; SubjectClip=" + (pairedArrest
                                    ? groundArrestAnimation
                                        ? "arrest_on_floor_front_right_b" : "crook_p2_back_right"
                                    : "TASK_ARREST_PED")
                                + "; SceneOrigin=" + arrestSceneOrigin.X.ToString("0.00") + ","
                                    + arrestSceneOrigin.Y.ToString("0.00") + ","
                                    + arrestSceneOrigin.Z.ToString("0.00")
                                + "; SceneHeading=" + arrestSceneHeading.ToString("0.0")
                                + "; ScenePhase=" + GetScenePhaseText(arrestScene)
                                + "; ClipDuration=" + arrestDuration.ToString("0.000")
                                + "; Cuffed=" + IsPedPhysicallyCuffed(_subject));
                            return false;
                        }
                        else if (!IsPedPhysicallyCuffed(_subject))
                        {
                            return false;
                        }
                    }

                    if (!playerAlreadySecured
                        && now < _backupArrestAnimationHoldUntil)
                        return false;

                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, true);
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_BACKUP_HANDCUFF_FLAG_FAILED", ex);
                    return false;
                }
                _pendingAnimation = PendingAnimation.None;
                ClearAnimationVerification();
                try
                {
                    // TASK_ARREST_PED can remain the officer's active task
                    // after cuffs are confirmed. Clear only that officer task
                    // before assigning the explicit escort route.
                    officer.Task.ClearAll();
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_BACKUP_ARREST_TASK_CLEAR_FAILED", ex);
                }
                _animationHoldUntil = now.AddMilliseconds(1800);
                _readyAt = _animationHoldUntil;
                _stage = InteractionStage.BackupEscort;
                // Securing can take most of the approach window when the
                // officer has to navigate around the scene. Give the actual
                // escort/vehicle-entry phase its own bounded handoff window.
                _backupPhaseDeadline = now.AddSeconds(BackupPhysicalEscortTimeoutSeconds);
                _nextBackupSubjectTask = _readyAt;
                _backupEscortTaskIssued = false;
                _backupEntryTaskIssued = false;
                _backupArrestTaskIssued = false;
                _backupArrestAttempts = 0;
                _backupArrestAnimationHoldUntil = DateTime.MinValue;
                _backupArrestPlaybackConfirmed = false;
                _backupVehicleDoorOpened = false;
                _backupVehicleDoorTaskIssued = false;
                _backupVehicleDoorFallbackIssued = false;
                _backupVehicleDoorTaskIssuedAt = DateTime.MinValue;
                _nextBackupMovementClipsetCheckAt = now;
                _backupMovementClipsetDeadline = now.AddSeconds(
                    BackupMovementClipsetTimeoutSeconds);
                _nextBackupCustodyRefreshAt = now;
                _backupMovementClipsetUnavailableLogged = false;
                _log.Runtime("NPC_BACKUP_SUBJECT_SECURED",
                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle);
                Notify("Citizen secured. Backup is escorting the subject to the Police vehicle.");
                return false;
            }

            if (_stage == InteractionStage.BackupEscort)
            {
                if (now < _animationHoldUntil)
                    return false;

                MaintainBackupEscortCustody(now);

                if (_backupVehicleDoorOpened)
                {
                    // Door opening is the boundary between escort movement
                    // and loading. Do not recalculate the officer's approach
                    // target after this point; issue the bounded rear-seat
                    // entry task directly and retry only on its timer.
                    MaintainBackupLoadingState();
                    if (!_backupEntryTaskIssued || now >= _nextBackupSubjectTask)
                    {
                        try
                        {
                            _subject.Task.EnterVehicle(
                                vehicle, VehicleSeat.LeftRear, 12000, 1.0f);
                            ObserveDeveloperTask(
                                "TASK_ENTER_VEHICLE",
                                _subject.Handle,
                                officer.Handle,
                                vehicle == null ? 0 : vehicle.Handle,
                                "BackupEscortEntry");
                            bool firstEntryTask = !_backupEntryTaskIssued;
                            _backupEntryTaskIssued = true;
                            _nextBackupSubjectTask = now.AddSeconds(12);
                            _log.Runtime(
                                firstEntryTask
                                    ? "NPC_BACKUP_PRISONER_ENTRY_TASK_ISSUED"
                                    : "NPC_BACKUP_PRISONER_ENTRY_TASK_RETRY_ISSUED",
                                "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                + "; Vehicle=" + vehicle.Handle + "; Seat=LeftRear");
                        }
                        catch (Exception ex) { _log.Exception("NPC_BACKUP_SUBJECT_ENTRY_FAILED", ex); }
                    }
                    return false;
                }

                // LeftRear is the physical custody seat used by Convoy.
                // Keep the escort and door task on that same side of the
                // vehicle instead of walking to the right rear door.
                Vector3 entry = vehicle.GetOffsetPosition(new Vector3(-2.2f, -1.0f, 0.0f));
                Vector3 escortTarget = entry;
                bool escortAtDoor = officer.Position.DistanceTo(escortTarget)
                    <= BackupEscortDoorRadius;
                bool subjectAtDoor = _subject.Position.DistanceTo(entry)
                    <= BackupEscortDoorRadius + 0.75f;

                if (!escortAtDoor || !subjectAtDoor)
                {
                    if (!_backupEscortTaskIssued || now >= _nextBackupSubjectTask)
                    {
                        try
                        {
                            if (!_backupEscortTaskIssued)
                                _subject.Task.ClearAll();
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                _subject, escortTarget.X, escortTarget.Y, escortTarget.Z,
                                0.85f, -1, 1.0f, 1, 0f);
                            Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                                officer, _subject, 0.0f, -0.75f, 0.0f,
                                0.85f, -1, 0.4f, false);
                            bool firstEscortTask = !_backupEscortTaskIssued;
                            _backupEscortTaskIssued = true;
                            _nextBackupSubjectTask = now.AddSeconds(
                                BackupEscortTaskRecoverySeconds);
                            if (firstEscortTask)
                                _log.Runtime("NPC_BACKUP_ESCORT_TO_VEHICLE_STARTED",
                                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                    + "; Vehicle=" + vehicle.Handle + "; Door=2"
                                    + "; Movement=RestrainedSuspectLeadsOfficer"
                                    + "; Clipset=" + BackupPrisonerMovementClipset
                                    + "; StopRange=" + BackupEscortDoorRadius.ToString("0.0")
                                    + "; SubjectAttached=false; SamePed=true");
                            else
                                _log.Runtime("NPC_BACKUP_ESCORT_ROUTE_RECOVERED",
                                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                    + "; Vehicle=" + vehicle.Handle
                                    + "; Movement=RestrainedSuspectLeadsOfficer"
                                    + "; RecoverySeconds=" + BackupEscortTaskRecoverySeconds);
                        }
                        catch (Exception ex) { _log.Exception("NPC_BACKUP_ESCORT_TO_VEHICLE_FAILED", ex); }
                    }
                    return false;
                }

                if (!_backupVehicleDoorOpened)
                {
                    try
                    {
                        float doorAngle = Function.Call<float>(
                            Hash.GET_VEHICLE_DOOR_ANGLE_RATIO, vehicle, 2);
                        if (doorAngle < 0.75f)
                        {
                            if (!_backupVehicleDoorTaskIssued)
                            {
                                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                    officer, vehicle, 1000);
                                Function.Call(Hash.TASK_OPEN_VEHICLE_DOOR,
                                    officer, vehicle, 4000, 2, 1.0f);
                                _backupVehicleDoorTaskIssued = true;
                                _backupVehicleDoorTaskIssuedAt = now;
                                _nextBackupSubjectTask = now.AddMilliseconds(
                                    BackupVehicleDoorAnimationFallbackGraceMilliseconds);
                                _log.Runtime("NPC_BACKUP_PRISONER_DOOR_OPEN_TASK",
                                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                    + "; Vehicle=" + vehicle.Handle + "; Door=2"
                                    + "; AngleRatio=" + doorAngle.ToString("0.00"));
                            }
                            else if (!_backupVehicleDoorFallbackIssued
                                && _backupVehicleDoorTaskIssuedAt != DateTime.MinValue
                                && now >= _backupVehicleDoorTaskIssuedAt.AddMilliseconds(
                                    BackupVehicleDoorAnimationFallbackGraceMilliseconds))
                            {
                                // Match Convoy's stock-vehicle fallback after
                                // the officer's physical door task has had time
                                // to animate. Seat entry still waits for the
                                // real rear-door angle and occupied seat.
                                _backupVehicleDoorFallbackIssued = true;
                                Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                                    vehicle, 2, false, false);
                                _log.Runtime(
                                    "NPC_BACKUP_PRISONER_DOOR_ANIMATION_FALLBACK",
                                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                    + "; Vehicle=" + vehicle.Handle + "; Door=2"
                                    + "; TaskWaitMs="
                                    + BackupVehicleDoorAnimationFallbackGraceMilliseconds
                                    + "; PhysicalAngleConfirmationRequired=true"
                                    + "; PhysicalEntryAndSeatConfirmationRequired=true");
                            }
                            return false;
                        }

                        _backupVehicleDoorOpened = true;
                        _backupVehicleDoorTaskIssued = false;
                        _backupVehicleDoorFallbackIssued = false;
                        _backupVehicleDoorTaskIssuedAt = DateTime.MinValue;
                        _nextBackupSubjectTask = now.AddMilliseconds(500);
                        _log.Runtime("NPC_BACKUP_ESCORT_REACHED_VEHICLE",
                            "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                            + "; Vehicle=" + vehicle.Handle + "; Door=2"
                            + "; SubjectAtDoor=true");
                        _log.Runtime("NPC_BACKUP_PRISONER_DOOR_OPENED",
                            "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                            + "; Vehicle=" + vehicle.Handle + "; Door=2"
                            + "; PhysicalAngleRatio=" + doorAngle.ToString("0.00"));
                    }
                    catch (Exception ex)
                    {
                        _nextBackupSubjectTask = now.AddSeconds(2);
                        _log.Exception("NPC_BACKUP_PRISONER_DOOR_OPEN_FAILED", ex);
                    }
                    DetachBackupSubjectFromOfficer("BothActorsAtRearDoor");
                    return false;
                }

            }

            if (now >= _backupPhaseDeadline)
            {
                CancelBackupHandoff(
                    "The physical citizen handoff timed out at stage " + _stage
                    + ". The subject remains available for another Backup request.");
            }
            return false;
        }

        private bool IsBackupSubjectAttachedToOfficer(Ped subject, Ped officer)
        {
            if (!IsUsable(subject) || !IsUsable(officer))
                return false;
            try
            {
                return Function.Call<bool>(
                    Hash.IS_ENTITY_ATTACHED_TO_ENTITY, subject, officer);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_BACKUP_PHYSICAL_ESCORT_STATE_CHECK_FAILED", ex);
                return false;
            }
        }

        private void DetachBackupSubjectFromOfficer(string reason)
        {
            if (!IsUsable(_subject))
                return;

            bool attached = IsUsable(_backupOfficer)
                && IsBackupSubjectAttachedToOfficer(_subject, _backupOfficer);
            if (attached)
            {
                try
                {
                    Function.Call(Hash.DETACH_ENTITY, _subject, true, true);
                    _log.Runtime("NPC_BACKUP_PHYSICAL_ESCORT_DETACHED",
                        "Ped=" + _subject.Handle + "; Officer=" + _backupOfficer.Handle
                        + "; Reason=" + (reason ?? string.Empty));
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_BACKUP_PHYSICAL_ESCORT_DETACH_FAILED", ex);
                }
            }

        }

        private void MaintainBackupLoadingState()
        {
            if (!IsUsable(_subject) || _subject.IsDead || _subject.Health <= 0)
                return;
            try
            {
                // Loading owns the subject after the Officer and subject
                // reach the rear door. Keep custody active while GTA performs the real
                // rear-seat entry task, without issuing a competing movement
                // or ambient task that could interrupt vehicle entry.
                SetSubjectPoliceAware(_subject);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, true);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_BACKUP_LOADING_STATE_MAINTENANCE_FAILED", ex);
            }
        }

        internal void MarkBackupTransportStarted()
        {
            if (!IsLoadedInBackupVehicle)
                return;
            _stage = InteractionStage.BackupTransport;
            _expiresAt = DateTime.UtcNow.AddMinutes(6);
            CleanupSubjectBlip();
            _log.Runtime("NPC_BACKUP_TRANSPORT_STARTED",
                "Ped=" + _subject.Handle + "; Vehicle=" + _backupVehicle.Handle);
            Notify("Citizen is loaded into the Backup utility. Wait for the vehicle to leave the scene before resuming patrol.");
        }

        internal bool ReleasePlayerSessionForBackgroundTransport()
        {
            return ReleasePlayerSessionForBackgroundTransport(
                "BackupTransportBeyondPlayerReleaseDistance");
        }

        internal bool ReleasePlayerSessionForBackgroundTransport(string reason)
        {
            if (!HasBackupAssignment || _stage != InteractionStage.BackupTransport
                || _backupBackgroundTransport)
                return false;

            _backupBackgroundTransport = true;
            CleanupSubjectBlip();
            _log.Runtime("NPC_BACKUP_PLAYER_SESSION_RELEASED",
                "Ped=" + (_subject == null ? 0 : _subject.Handle)
                + "; Vehicle=" + (_backupVehicle == null ? 0 : _backupVehicle.Handle)
                + "; Reason=" + (reason ?? string.Empty));
            Notify("Backup vehicle has physically left the scene. You may resume patrol.");
            return true;
        }

        internal void CompleteBackupBackgroundDeparture(string reason)
        {
            if (!_backupRequested && !_backupBackgroundTransport)
                return;

            int handle = IsUsable(_subject) ? _subject.Handle : 0;
            try
            {
                if (IsUsable(_subject))
                {
                    // The Backup vehicle is already leaving the player's
                    // scene. Preserve cuff state until station completion while
                    // returning only the departed actor to ambient ownership.
                    ReleaseSubjectPoliceAwareness(_subject, true);
                }
                if (IsUsable(_subjectVehicle))
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, false);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_BACKUP_BACKGROUND_DEPARTURE_RELEASE_FAILED", ex);
            }

            RestoreBackupTransportProtection();
            RestoreInteractionPersistence();
            CleanupSubjectBlip();
            ClearInteractionState();
            _log.Runtime("NPC_BACKUP_BACKGROUND_DEPARTURE_COMPLETED",
                "Ped=" + handle + "; Reason=" + (reason ?? string.Empty));
        }

        internal void CompleteBackupTransport(string stationName)
        {
            if (!HasBackupAssignment)
                return;
            int handle = IsUsable(_subject) ? _subject.Handle : 0;
            try
            {
                if (IsUsable(_subject) && IsUsable(_backupVehicle)
                    && _subject.CurrentVehicle != null && _subject.CurrentVehicle.Exists()
                    && _subject.CurrentVehicle.Handle == _backupVehicle.Handle)
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, _subject, _backupVehicle, 0);
                if (IsUsable(_subject))
                {
                    Function.Call(Hash.SET_PED_CONFIG_FLAG,
                        _subject, HandcuffedPedConfigFlag, false);
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, false);
                    _subject.CanSwitchWeapons = true;
                    ReleaseSubjectPoliceAwareness(_subject, true);
                }
                if (IsUsable(_subjectVehicle))
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, false);
            }
            catch (Exception ex) { _log.Exception("NPC_BACKUP_TRANSPORT_RELEASE_FAILED", ex); }
            RestoreBackupTransportProtection();
            RestoreInteractionPersistence();
            CleanupSubjectBlip();
            ClearInteractionState();
            _log.Runtime("NPC_BACKUP_TRANSPORT_COMPLETED",
                "Ped=" + handle + "; Station=" + (stationName ?? string.Empty));
            Notify("~b~POLICE BACKUP~s~\nCitizen delivered to "
                + (string.IsNullOrWhiteSpace(stationName) ? "the Police station" : stationName)
                + ". NPC contact complete; Anyi may continue patrol.");
        }

        internal void CancelBackupHandoff(string reason)
        {
            if (!_backupRequested)
                return;
            DetachBackupSubjectFromOfficer("BackupHandoffCancelled");
            _backupRequested = false;
            _expiresAt = DateTime.UtcNow.AddMinutes(6);
            _backupBackgroundTransport = false;
            _backupOfficer = null;
            _backupVehicle = null;
            _backupEscortTaskIssued = false;
            _backupEntryTaskIssued = false;
            _backupVehicleDoorOpened = false;
            _backupVehicleDoorTaskIssued = false;
            _backupVehicleDoorFallbackIssued = false;
            _backupVehicleDoorTaskIssuedAt = DateTime.MinValue;
            _backupArrestTaskIssued = false;
            _backupArrestAttempts = 0;
            _backupArrestAnimationHoldUntil = DateTime.MinValue;
            _backupArrestPlaybackConfirmed = false;
            RestoreBackupCustodyMovementStyle();
            _backupCustodyOfferPresented = false;
            RestoreBackupTransportProtection();
            if (IsUsable(_subject))
            {
                SetSubjectPoliceAware(_subject);
                if (_subjectVehicle == null && _footPlayerArrested)
                {
                    _stage = InteractionStage.FootAwaitingBackup;
                    MaintainFootArrestedSubject();
                }
                else
                {
                    if (!IsLivingDowned(_subject)
                        && !IsLivingInjuredSubject(_subject))
                        _subject.Task.HandsUp(15000);
                    _stage = _subjectVehicle == null
                        ? InteractionStage.FootAwaitingBackup
                        : InteractionStage.TrafficAwaitingBackup;
                }
            }
            CleanupSubjectBlip();
            _log.StateFailure("NPC_BACKUP_HANDOFF_CANCELLED", reason ?? string.Empty);
        }

        /// <summary>
        /// Keeps ambient civilian behavior from ever claiming a Dispatch actor.
        /// The NPC owner stores only transient handles; Dispatch continues to own
        /// the actor state and cleanup.
        /// </summary>
        internal void SetProtectedSceneActors(LSPDDispatchEvent incident)
        {
            SetProtectedSceneActors(incident, null);
        }

        internal void SetProtectedSceneActors(
            LSPDDispatchEvent incident,
            IEnumerable<Ped> additionalActors)
        {
            _protectedSceneActorHandles.Clear();
            _protectedSceneActors.Clear();
            if (incident != null)
            {
                AddProtectedSceneActor(incident.Suspect);
                if (incident.Suspects != null)
                    foreach (Ped suspect in incident.Suspects)
                        AddProtectedSceneActor(suspect);
                AddProtectedSceneActor(incident.Victim);
                if (incident.AdditionalParticipants != null)
                    foreach (Ped participant in incident.AdditionalParticipants)
                        AddProtectedSceneActor(participant);
            }
            if (additionalActors != null)
                foreach (Ped actor in additionalActors)
                    AddProtectedSceneActor(actor);
        }

        /// <summary>
        /// Keeps ambient traffic behavior from clearing or redirecting a
        /// vehicle owned by Dispatch custody/Convoy. Civilian traffic remains
        /// eligible for the normal local move-away response.
        /// </summary>
        internal void SetProtectedSceneVehicles(IEnumerable<Vehicle> additionalVehicles)
        {
            _protectedSceneVehicleHandles.Clear();
            _protectedSceneVehicles.Clear();
            if (additionalVehicles == null)
                return;
            foreach (Vehicle vehicle in additionalVehicles)
                AddProtectedSceneVehicle(vehicle);
        }

        /// <summary>
        /// Stores the live positions of already-owned police/custody actors and
        /// vehicles. NPCResponse uses one rotating local sample to warn ordinary
        /// traffic near the active operation; it never tasks these anchors.
        /// </summary>
        internal void SetTrafficOperationEntities(
            IEnumerable<Ped> actors,
            IEnumerable<Vehicle> vehicles)
        {
            _trafficOperationActors.Clear();
            _trafficOperationVehicles.Clear();
            if (actors != null)
            {
                foreach (Ped actor in actors)
                {
                    if (!IsUsable(actor)
                        || _trafficOperationActors.Any(value => value.Handle == actor.Handle))
                        continue;
                    if (_trafficOperationActors.Count + _trafficOperationVehicles.Count
                        >= MaximumTrafficOperationAnchors)
                        break;
                    _trafficOperationActors.Add(actor);
                }
            }
            if (vehicles != null
                && _trafficOperationActors.Count + _trafficOperationVehicles.Count
                    < MaximumTrafficOperationAnchors)
            {
                foreach (Vehicle vehicle in vehicles)
                {
                    if (!IsUsable(vehicle)
                        || _trafficOperationVehicles.Any(value => value.Handle == vehicle.Handle))
                        continue;
                    if (_trafficOperationActors.Count + _trafficOperationVehicles.Count
                        >= MaximumTrafficOperationAnchors)
                        break;
                    _trafficOperationVehicles.Add(vehicle);
                }
            }
        }

        internal string StatusText
        {
            get
            {
                switch (_stage)
                {
                    case InteractionStage.FootPreparing:
                        return "Citizen noticed the officer and is stopping for contact.";
                    case InteractionStage.FootGreeting:
                        return _footGreetingConfirmed
                            ? "Citizen is facing the officer. Request identification."
                            : "Citizen is turning to face the officer. Press "
                                + _controls.SecureKey + " to retry if needed.";
                    case InteractionStage.FootDocuments:
                        return CitizenDocumentStatusText("Citizen contact");
                    case InteractionStage.FootRefused:
                        return "Citizen did not comply. Y = release, N = pursue.";
                    case InteractionStage.FootStationFollow:
                        return "Citizen complied and is walking to the selected Police station.";
                    case InteractionStage.FootFleeing:
                        return _fleeMovementConfirmed
                            ? "Citizen fled. Pursue on foot until the person stops or is down."
                            : "Citizen is attempting to leave. Waiting for real movement.";
                    case InteractionStage.FootResisting:
                        return IsLivingDowned(_subject)
                            ? "Citizen is down but alive. Approach and press "
                                + _controls.SecureKey + " to begin the physical arrest."
                            : "Citizen is resisting the Police contact. Use appropriate force to end the threat or request Backup.";
                    case InteractionStage.TrafficPullingOver:
                        return "Driver is pulling over for Police contact.";
                    case InteractionStage.TrafficPreparing:
                        return "Driver is pulled over. Approach the window, open Citizen Ped Response with "
                            + _controls.InteractionKey + ", and request the driver's documents.";
                    case InteractionStage.TrafficDocuments:
                        return CitizenDocumentStatusText("Traffic stop");
                    case InteractionStage.TrafficRefused:
                        return "Driver remained stopped after refusing. Y = clear, N = pursue.";
                    case InteractionStage.TrafficStationFollow:
                        return "Driver is complying with a station direction to " + _trafficStationName + ".";
                    case InteractionStage.TrafficFleeing:
                        return _fleeMovementConfirmed
                            ? "Driver fled. Follow the vehicle until it stops, crashes, or escapes."
                            : "Driver is attempting to leave. Waiting for real vehicle movement.";
                    case InteractionStage.TrafficResisting:
                        return "Driver is resisting the Police contact. Use appropriate force to end the threat or request Backup.";
                    case InteractionStage.FootAwaitingBackup:
                        return _footPlayerArrested
                            ? "Citizen is handcuffed and waiting for Police transport. Press "
                                + _controls.EmergencyKey + " to open Back Up Response and choose Citizen assistance."
                            : "Subject is compliant and waiting for Police Backup transport.";
                    case InteractionStage.FootAwaitingPlayerArrest:
                        return "Citizen complied and is waiting for your physical arrest. Press "
                            + _controls.SecureKey + " while standing beside the citizen.";
                    case InteractionStage.FootPlayerArresting:
                        return "Physical arrest is in progress. Stay beside the citizen.";
                    case InteractionStage.FootBackupContained:
                        return _backupInterceptionInvestigationRequired
                            ? "Backup has contained the fleeing citizen. "
                                + CitizenDocumentStatusText("Citizen contact")
                            : "Backup has contained the fleeing citizen. Open Citizen Ped Response with "
                                + _controls.InteractionKey + " to review custody.";
                    case InteractionStage.TrafficAwaitingBackup:
                        return "Subject is compliant and waiting for Police Backup transport.";
                    case InteractionStage.TrafficBackupContained:
                        return _backupInterceptionInvestigationRequired
                            ? "Backup stopped the fleeing driver. After the driver exits, "
                                + CitizenDocumentStatusText("Driver")
                            : "Backup has stopped the fleeing driver. Approach and open Citizen Ped Response with "
                                + _controls.InteractionKey + " to review custody.";
                    case InteractionStage.BackupSecuring:
                        return "Backup is physically securing the citizen.";
                    case InteractionStage.BackupEscort:
                        return "Backup is escorting the citizen to the Police vehicle.";
                    case InteractionStage.BackupTransport:
                        return "Backup is transporting the citizen to the selected Police station.";
                    default:
                        return "No active NPC interaction.";
                }
            }
        }

        internal LSPDNPCRecord CurrentRecord
        {
            get { return _currentRecord; }
        }

        internal bool CitizenRecordVisible { get; private set; }

        /// <summary>
        /// Runs while PoliceCore has an explicitly active Authority session.
        /// Patrol gates Citizen contact selection; local ambient reactions and
        /// traffic caution also follow active scenes and custody. UI owns G as
        /// the Citizen framework key; E/Y/N retain their existing contact roles.
        /// </summary>
        internal void Process(bool isPatrolling, bool dispatchActive, bool paused)
        {
            Process(isPatrolling, dispatchActive, dispatchActive, paused, true);
        }

        internal void Process(
            bool isPatrolling,
            bool dispatchActive,
            bool paused,
            bool allowGameplayInput)
        {
            Process(isPatrolling, dispatchActive, dispatchActive, paused, allowGameplayInput);
        }

        /// <summary>
        /// Keeps two separate facts separate: a Dispatch offer/route blocks a
        /// new civilian contact, while only a physically active roadside scene
        /// receives local crowd and traffic safety reactions.
        /// </summary>
        internal void Process(
            bool isPatrolling,
            bool contactBlocked,
            bool activeRoadScene,
            bool paused,
            bool allowGameplayInput)
        {
            Process(
                isPatrolling,
                contactBlocked,
                activeRoadScene,
                paused,
                allowGameplayInput,
                true);
        }

        internal void Process(
            bool isPatrolling,
            bool contactBlocked,
            bool activeRoadScene,
            bool paused,
            bool allowGameplayInput,
            bool civilianReactionControlEnabled)
        {
            if (paused)
            {
                // A pause freezes GTA tasks. Preserve an active contact and its
                // subject so resuming the game does not silently release the
                // citizen or driver; align keyboard edges to prevent a held
                // key from becoming a fresh decision after the pause.
                SynchronizeKeyStates();
                return;
            }
            if (!_settings.Enabled)
            {
                Reset();
                ResetKeyStates();
                return;
            }

            Ped player = Game.Player.Character;
            if (!IsUsable(player))
            {
                Reset();
                ResetKeyStates();
                return;
            }

            DateTime now = DateTime.UtcNow;

            SuppressTrafficContactVehicleControls(player);

            // Keep frame-scoped melee suppression ahead of the active Citizen
            // injury sample. A hit from the interaction frame must not be
            // observed before this owner masks the conflicting input.
            bool deferInjurySampleUntilCitizenInputMask = HasActiveInteraction;
            if (!deferInjurySampleUntilCitizenInputMask)
                MaintainSubjectInjury(_subject, now, player);
            MaintainDeferredDeadSubject(player, now);

            // Once Backup owns a loaded prisoner, the player-facing contact
            // is released, but the subject reference must remain alive until
            // Backup confirms station delivery. Do not let the next Patrol
            // scan reclaim that subject as a new ambient contact.
            if (_backupBackgroundTransport)
            {
                if (deferInjurySampleUntilCitizenInputMask)
                    MaintainSubjectInjury(_subject, now, player);
                ApplyLocalTrafficCaution(player, now, false, false);
                MaintainCollisionGuards(player, now);
                ResetKeyStates();
                return;
            }

            // Police Authority is already active when Core calls this owner.
            // Contact selection remains Patrol-only, but an on-duty officer
            // still receives the small collision-risk bubble and civilian
            // de-escalation before Patrol or between active incidents.
            if (!isPatrolling)
            {
                if (deferInjurySampleUntilCitizenInputMask)
                    MaintainSubjectInjury(_subject, now, player);
                if (HasActiveInteraction)
                {
                    ReleaseSubject(true);
                    ClearInteractionState();
                }
                ReleaseApproachCandidate();
                ApplyLocalTrafficCaution(player, now, false, false);
                if (civilianReactionControlEnabled)
                {
                    ApplyPolicePresenceReaction(player, now, activeRoadScene);
                    if (now >= _nextDeescalationScan)
                    {
                        _nextDeescalationScan = now.AddMilliseconds(DeescalationScanMilliseconds);
                        ApplyPoliceDeescalation(player, now);
                    }
                }
                MaintainCollisionGuards(player, now);
                TrimReleasedVehicleMemory(now);
                TrimSceneReactionMemory(now);
                TrimDeescalationMemory(now);
                TrimApproachMemory(now);
                ResetKeyStates();
                return;
            }

            // The current Dispatch owner remains independent. While it has an
            // offer or active scene, no new civilian/traffic contact is started.
            // PoliceCore also refuses a new dispatch handoff while this owner has
            // a contact. A quiet foot patrol may keep one short-lived approach
            // candidate so a civilian can notice the officer before E begins a
            // real interaction. G is routed by Police UI to the framework.
            if (_settings.InteractionsEnabled)
                MaintainApproachCandidate(player, now, contactBlocked);
            else
                ReleaseApproachCandidate();

            bool secureKeyPressed = _settings.InteractionsEnabled
                && allowGameplayInput
                && WasPressed(_controls.SecureKey, ref _secureKeyDown);
            if (_settings.InteractionsEnabled && allowGameplayInput
                && HasActiveInteraction && secureKeyPressed
                && _stage == InteractionStage.FootGreeting)
                TryRetryFootGreeting(player, now);
            bool controllerInteraction = _settings.InteractionsEnabled
                && allowGameplayInput
                && Game.IsControlJustPressed(GTA.Control.Attack)
                && !player.IsAiming
                && !player.IsShooting;
            bool beganContactThisFrame = false;
            bool ownsPlayerCustodyAction = _stage == InteractionStage.FootPlayerArresting
                || _stage == InteractionStage.BackupSecuring
                || _stage == InteractionStage.BackupEscort
                || _stage == InteractionStage.BackupTransport;
            Vehicle currentVehicle = player.CurrentVehicle;
            bool trafficSignal = _settings.InteractionsEnabled
                && allowGameplayInput
                && IsUsable(currentVehicle)
                && IsDriver(player, currentVehicle)
                // This observes the player's existing horn/siren control. It
                // never calls a siren or light native and works with any active
                // Patrol vehicle, including addon models.
                && Game.IsControlJustPressed(GTA.Control.VehicleHorn);
            if (_settings.InteractionsEnabled && allowGameplayInput
                && secureKeyPressed && !HasActiveInteraction && !contactBlocked)
            {
                BeginNearestInteraction(player);
                beganContactThisFrame = HasActiveInteraction;
            }

            if (_settings.InteractionsEnabled && allowGameplayInput
                && (controllerInteraction || trafficSignal))
            {
                bool hadActiveInteraction = HasActiveInteraction;
                if (HasActiveInteraction
                    && (_stage == InteractionStage.FootBackupContained
                        || _stage == InteractionStage.TrafficBackupContained)
                    && controllerInteraction)
                    PresentBackupCustodyOffer(player);
                else if (contactBlocked && !HasActiveInteraction)
                    Notify("Dispatch is active. NPC contact is paused until the Police scene is clear.");
                else
                    BeginNearestInteraction(player, trafficSignal);
                if (!hadActiveInteraction && HasActiveInteraction)
                    beganContactThisFrame = true;
            }
            bool citizenInputConsumed = beganContactThisFrame
                || (secureKeyPressed && HasActiveInteraction)
                || (controllerInteraction && HasActiveInteraction);
            if (ownsPlayerCustodyAction)
                LSPDPlayerControlSuppression.SuppressMeleeThisFrame();
            else if (HasActiveInteraction)
                LSPDPlayerControlSuppression.SuppressMeleeThisFrame(
                    player.IsAiming || player.IsShooting);
            else if (citizenInputConsumed)
                LSPDPlayerControlSuppression.SuppressMeleeThisFrame();

            if (HasActiveInteraction)
                MaintainSubjectInjury(_subject, now, player);

            if (_settings.InteractionsEnabled && allowGameplayInput && HasActiveInteraction
                && secureKeyPressed && !beganContactThisFrame && CanArrestCitizen)
                BeginFootPlayerArrest(player, now);
            bool controllerAccept = _settings.InteractionsEnabled && allowGameplayInput
                && Game.IsControlJustPressed(GTA.Control.FrontendAccept);
            bool controllerReject = _settings.InteractionsEnabled && allowGameplayInput
                && Game.IsControlJustPressed(GTA.Control.FrontendCancel);
            if (_settings.InteractionsEnabled && allowGameplayInput && HasActiveInteraction
                && CanDecideCitizenRecord
                && (WasPressed(_controls.AcceptKey, ref _acceptKeyDown) || controllerAccept))
                AcceptInteraction();
            if (_settings.InteractionsEnabled && allowGameplayInput && HasActiveInteraction
                && CanDecideCitizenRecord
                && (WasPressed(_controls.RejectKey, ref _rejectKeyDown) || controllerReject))
                RejectInteraction(player);

            MaintainInteraction(player, now);
            if (civilianReactionControlEnabled)
                ApplyPolicePresenceReaction(player, now, activeRoadScene);
            ApplyLocalTrafficCaution(
                player,
                now,
                activeRoadScene && _trafficSettings.StopTrafficAtActiveScenes,
                HasActiveInteraction);
            if (civilianReactionControlEnabled && now >= _nextDeescalationScan)
            {
                _nextDeescalationScan = now.AddMilliseconds(DeescalationScanMilliseconds);
                ApplyPoliceDeescalation(player, now);
            }
            MaintainCollisionGuards(player, now);
            TrimReleasedVehicleMemory(now);
            TrimSceneReactionMemory(now);
            TrimDeescalationMemory(now);
            TrimApproachMemory(now);
        }

        internal string BeginInteractionCommand()
        {
            if (!_settings.Enabled || !_settings.InteractionsEnabled)
                return "NPC interactions are disabled in LS Immersive settings.";
            Ped player = Game.Player.Character;
            if (!IsUsable(player))
                return "The player character is unavailable.";
            if (HasActiveInteraction
                && TryRetryFootGreeting(player, DateTime.UtcNow))
                return StatusText;
            BeginNearestInteraction(player);
            return HasActiveInteraction
                ? StatusText
                : "No safe civilian or stopped driver is nearby for Police contact.";
        }

        internal string BeginFootInteractionCommand()
        {
            if (!_settings.Enabled || !_settings.InteractionsEnabled)
                return "NPC interactions are disabled in LS Immersive settings.";
            Ped player = Game.Player.Character;
            if (!IsUsable(player))
                return "The player character is unavailable.";
            if (HasActiveInteraction)
            {
                if (TryBeginTrafficWindowInvestigation(player))
                    return StatusText;
                return StatusText;
            }

            Ped footPed = FindFootPedestrian(player);
            LogContactSearch(player, "Foot", footPed, null);
            if (footPed == null)
                return "No safe pedestrian is close enough for Police contact.";
            BeginFootInteraction(player, footPed);
            return StatusText;
        }

        internal string BeginTrafficInteractionCommand()
        {
            if (!_settings.Enabled || !_settings.InteractionsEnabled)
                return "NPC interactions are disabled in LS Immersive settings.";
            Ped player = Game.Player.Character;
            if (!IsUsable(player))
                return "The player character is unavailable.";
            if (HasActiveInteraction)
            {
                if (TryBeginTrafficWindowInvestigation(player))
                    return StatusText;
                return StatusText;
            }

            Vehicle playerVehicle = player.CurrentVehicle;
            Ped trafficDriver = FindTrafficDriver(
                player,
                playerVehicle != null && playerVehicle.Exists());
            LogContactSearch(player, "Traffic", null, trafficDriver);
            if (trafficDriver == null)
                return "No safe driver is close enough for a Police traffic contact.";
            BeginTrafficInteraction(player, trafficDriver, trafficDriver.CurrentVehicle);
            return StatusText;
        }

        internal string AcceptInteractionCommand()
        {
            if (!HasActiveInteraction)
                return "No NPC interaction is awaiting a decision.";
            if (!CanDecideCitizenRecord)
                return CitizenDecisionGateMessage("accept");
            AcceptInteraction();
            return HasActiveInteraction
                ? StatusText
                : "NPC interaction completed and the subject was released.";
        }

        internal string RejectInteractionCommand()
        {
            if (!HasActiveInteraction)
                return "No NPC interaction is awaiting a decision.";
            if (!CanDecideCitizenRecord)
                return CitizenDecisionGateMessage("reject");
            RejectInteraction(Game.Player.Character);
            return HasActiveInteraction
                ? StatusText
                : "NPC interaction ended and the subject was released or fled.";
        }

        internal string RequestCitizenDocumentsCommand()
        {
            if (!HasActiveInteraction || !IsUsable(_subject))
                return "Greet a citizen or stop a driver before requesting identification.";
            if (!CanRequestCitizenDocuments)
                return StatusText;

            bool footHandoff = _subjectVehicle == null
                || (IsBackupInterceptionInvestigationActive
                    && !_subject.IsInVehicle())
                || IsTrafficOnFootInterceptionInvestigationActive;
            Ped handoffPlayer = Game.Player.Character;
            if (footHandoff && !IsCitizenDocumentHandoffReady(
                    handoffPlayer, _subject))
            {
                Notify("Move within arm's reach of the selected citizen before requesting the document handoff.");
                return "Move within arm's reach before requesting the citizen document handoff.";
            }

            bool backupInvestigation = IsBackupInterceptionInvestigationActive;
            bool trafficInvestigation = IsTrafficOnFootInterceptionInvestigationActive;
            if (backupInvestigation || trafficInvestigation)
            {
                DateTime investigationNow = DateTime.UtcNow;
                _documentsProvided = false;
                _dispatcherValidationReceived = false;
                _citizenRecordReviewed = false;
                CitizenRecordVisible = false;
                _readyAt = DateTime.MaxValue;
                _nextSubjectHold = DateTime.MinValue;
                ReportCitizenDocumentRequestAudio();
                BeginPendingAnimation(PendingAnimation.Documents,
                    DocumentPresentationMilliseconds, investigationNow);
                _log.Runtime(backupInvestigation
                        ? "NPC_BACKUP_INTERCEPTION_DOCUMENT_REQUESTED"
                        : "NPC_TRAFFIC_INTERCEPTION_DOCUMENT_REQUESTED",
                    "Ped=" + _subject.Handle
                    + "; SubjectType=" + (_subjectVehicle == null ? "SuspiciousFoot" : "Traffic")
                    + "; Vehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                    + "; IdentityPreserved=" + (_currentRecord != null)
                    + "; BackupAssigned=" + _backupRequested);
                Notify(backupInvestigation
                    ? (_subjectVehicle == null
                        ? "Rechecking the contained citizen's documents. Wait for the physical handoff before requesting Dispatcher validation."
                        : "Rechecking the stopped driver's documents. Wait for the physical handoff before requesting Dispatcher validation.")
                    : "Rechecking the same fleeing Citizen's documents after containment. Wait for the physical handoff before requesting Dispatcher validation.");
                return backupInvestigation
                    ? "Citizen document handoff started after Backup interception."
                    : "Citizen document handoff started after flight containment.";
            }

            if (IsUsable(_subjectVehicle))
            {
                if (TryBeginTrafficWindowInvestigation(Game.Player.Character))
                    return "Driver document handoff started. Wait for the driver to present the document.";
                return "Approach the selected driver's window before requesting identification.";
            }

            DateTime now = DateTime.UtcNow;
            _stage = InteractionStage.FootDocuments;
            _documentsProvided = false;
            _readyAt = DateTime.MaxValue;
            _nextSubjectHold = DateTime.MinValue;
            CitizenRecordVisible = false;
            ReportCitizenDocumentRequestAudio();
            BeginPendingAnimation(PendingAnimation.Documents, DocumentPresentationMilliseconds, now);
            _log.Runtime("NPC_FOOT_DOCUMENT_REQUESTED", "Ped=" + _subject.Handle);
            Notify("Citizen is presenting identification. The Dispatcher check is a separate selection.");
            return "Citizen document handoff started.";
        }

        internal string RequestCitizenValidationCommand()
        {
            if (!HasActiveInteraction)
                return "Start a Citizen contact before requesting a Dispatcher validation.";
            if (!CanRequestCitizenValidation)
                return _dispatcherValidationReceived
                    ? (CitizenRecordVisible
                        ? "Dispatcher validation is already displayed. Review the record and select Confirm Review."
                        : "Dispatcher validation has been reviewed. Choose Accept or Reject.")
                    : "Complete the citizen's document handoff first, then request the Dispatcher validation.";

            PresentCitizenRecord();
            return _currentRecord == null
                ? "The citizen database could not return a validation record. You may request the check again."
                : "Dispatcher validation received for " + _currentRecord.FullName + ".";
        }

        internal string ConfirmCitizenRecordReviewCommand()
        {
            if (!HasActiveInteraction)
                return "Start a Citizen contact before reviewing a Dispatcher validation.";
            if (!_documentsProvided || !_dispatcherValidationReceived || _currentRecord == null)
                return "Request the citizen's document and Dispatcher validation before confirming review.";
            if (!IsTrafficDocumentReviewReady())
                return "Keep the same driver at the confirmed roadside stop, or approach the physically contained driver, before confirming review.";
            if (_backupCustodyOfferPresented)
                return "Review the Backup custody offer with its Accept or Reject decision.";
            if (!CitizenRecordVisible)
                return _citizenRecordReviewed
                    ? "You have already confirmed review of this Citizen record. Choose Accept or Reject."
                    : "The Citizen validation record is not currently visible.";

            _citizenRecordReviewed = true;
            CitizenRecordVisible = false;
            _log.Runtime("NPC_DOCUMENT_REVIEW_CONFIRMED",
                "Ped=" + (_subject == null ? 0 : _subject.Handle)
                + "; " + RecordStatus("Dispatcher validation reviewed"));
            if (IsBackupInterceptionInvestigationActive)
            {
                Ped player = Game.Player.Character;
                if (IsUsable(player)
                    && _subject.Position.DistanceTo(player.Position) <= BackupContainmentInteractionRadius)
                    PresentBackupCustodyOffer(player);
                else
                    Notify("You confirmed review of " + _currentRecord.FullName
                        + "'s validation. Approach the contained citizen and reopen Citizen Ped Response for the Backup custody decision.");
            }
            else
                Notify("You confirmed review of " + _currentRecord.FullName
                    + "'s validation documents. Choose Accept or Reject.");
            return "Citizen document review confirmed.";
        }

        internal string ArrestCitizenCommand()
        {
            if (IsSubjectMeleeReactionPending(_subject))
                return "Wait for the citizen's physical reaction to finish, then secure the same subject.";
            if (!CanArrestCitizen)
                return "Arrest is available after the citizen complies and waits for physical arrest.";
            BeginFootPlayerArrest(Game.Player.Character, DateTime.UtcNow);
            return StatusText;
        }

        internal string ReleaseCitizenCommand()
        {
            if (!CanReleaseCitizen)
                return "Release is available after Confirm Review closes the Dispatcher record and the contact is safe to clear.";
            return AcceptInteractionCommand();
        }

        internal string ReviewCitizenFrameworkCommand()
        {
            if (CanReviewBackupCustodyOffer)
                PresentBackupCustodyOffer(Game.Player.Character);
            return StatusText;
        }

        internal void Reset()
        {
            ReleaseSubject(true);
            ReleaseApproachCandidate();
            ReleaseDeferredDeadSubject(true, "PoliceSessionReset");
            MaintainCollisionGuards(Game.Player.Character, DateTime.MaxValue);
            ClearInteractionState();
            _recentlyReleasedVehicles.Clear();
            _recentSceneReactions.Clear();
            _recentDeescalations.Clear();
            _recentApproachCandidates.Clear();
            _recentTrafficApproachBrakes.Clear();
            _collisionGuards.Clear();
            _nextDeescalationScan = DateTime.MinValue;
            _nextSceneReactionScan = DateTime.MinValue;
            _nextTrafficCaution = DateTime.MinValue;
            _nextTrafficApproachBrakeTrim = DateTime.MinValue;
            _trafficOperationAnchorCursor = 0;
            _trafficOperationActors.Clear();
            _trafficOperationVehicles.Clear();
            _trafficDriversSeen.Clear();
            _sceneReactionPedsSeen.Clear();
            _protectedSceneActorHandles.Clear();
            _protectedSceneVehicleHandles.Clear();
            _protectedSceneActors.Clear();
            _protectedSceneVehicles.Clear();
            if (_database != null)
                _database.ResetSession();
        }

        private void BeginNearestInteraction(Ped player)
        {
            BeginNearestInteraction(player, false);
        }

        private void BeginNearestInteraction(Ped player, bool preferTraffic)
        {
            if (HasActiveInteraction)
            {
                if (TryBeginTrafficWindowInvestigation(player))
                    return;
                Notify(StatusText);
                return;
            }

            Vehicle playerVehicle = player.CurrentVehicle;
            // A generic interaction edge while riding must not select any
            // nearby driver from the awareness scan. Vehicle contact is an
            // explicit traffic-signal action; the menu still has its direct
            // Vehicle Interaction command for a deliberate selection.
            if (playerVehicle != null && playerVehicle.Exists() && !preferTraffic)
                return;

            Ped trafficDriver = FindTrafficDriver(player, playerVehicle != null && playerVehicle.Exists());
            Ped footPed = FindFootPedestrian(player);
            LogContactSearch(player, preferTraffic ? "TrafficSignal" : "Auto", footPed, trafficDriver);

            // A driver is the intended first target while the Officer is in a
            // vehicle. On foot, a close pedestrian remains the natural choice;
            // a driver may be contacted only after their vehicle is already slow.
            if (trafficDriver != null && (preferTraffic
                || (playerVehicle != null && playerVehicle.Exists())
                || footPed == null))
            {
                BeginTrafficInteraction(player, trafficDriver, trafficDriver.CurrentVehicle);
                return;
            }
            if (footPed != null)
            {
                BeginFootInteraction(player, footPed);
                return;
            }
            if (trafficDriver != null)
            {
                BeginTrafficInteraction(player, trafficDriver, trafficDriver.CurrentVehicle);
                return;
            }

            Notify("No safe civilian or stopped driver is nearby for Police contact.");
        }

        private bool TryRetryFootGreeting(Ped player, DateTime now)
        {
            if (_stage != InteractionStage.FootGreeting
                || _subjectVehicle != null
                || _footGreetingConfirmed
                || _pendingAnimation != PendingAnimation.None
                || _animationVerificationAt != DateTime.MinValue
                || now < _readyAt)
                return false;

            if (!IsUsable(_subject) || !IsUsable(player))
                return false;

            float subjectDistance = _subject.Position.DistanceTo(player.Position);
            if (player.IsInVehicle() || subjectDistance > FootContactRadius
                || Math.Abs(_subject.Position.Z - player.Position.Z) > 1.25f)
            {
                Notify("Move within contact range of the selected citizen before retrying orientation.");
                return true;
            }

            if (_footGreetingAttempts >= MaximumFootGreetingAttempts)
            {
                Notify("The selected citizen's greeting still could not be confirmed. Release this contact and begin a new interaction to try again.");
                return true;
            }

            _footGreetingAttempts++;
            OrientSubjectToOfficer(_subject, player, SubjectOrientationMilliseconds);
            _readyAt = now.AddMilliseconds(SubjectOrientationMilliseconds);
            _log.Runtime("NPC_FOOT_GREETING_RETRY_STARTED",
                "Ped=" + _subject.Handle + "; Attempt=" + _footGreetingAttempts
                + "; Mode=OfficerFacing");
            Notify("Asking the selected citizen to face you again.");
            return true;
        }

        /// <summary>
        /// The stop signal chooses one driver; it does not silently finish the
        /// roadside investigation. The officer must leave the patrol vehicle,
        /// walk to the driver's window, and deliberately press Interact before
        /// the identification gesture begins.
        /// </summary>
        private bool TryBeginTrafficWindowInvestigation(Ped player)
        {
            bool retryingDocumentHandoff = _stage == InteractionStage.TrafficDocuments
                && !_documentsProvided
                && _pendingAnimation == PendingAnimation.None;
            if (_stage != InteractionStage.TrafficPreparing && !retryingDocumentHandoff)
                return false;
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle)
                || !IsTrafficStopAtSelectedRoadside()
                || !IsDriver(_subject, _subjectVehicle)
                || !IsSelectedTrafficDriverStillSeated())
            {
                Notify("The selected driver and vehicle have not completed a physical roadside stop.");
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (!retryingDocumentHandoff && now < _readyAt)
            {
                Notify("Wait for the driver to finish pulling over.");
                return true;
            }
            if (player.IsInVehicle())
            {
                Notify("Exit the patrol vehicle and walk to the driver's window to investigate.");
                return true;
            }
            if (!IsAtTrafficDriverWindow(player))
            {
                Notify("Move closer to the driver's window, then open Citizen Ped Response and request the driver's documents.");
                return true;
            }

            _stage = InteractionStage.TrafficDocuments;
            _readyAt = DateTime.MaxValue;
            _documentsProvided = false;
            _nextSubjectHold = DateTime.MinValue;
            HoldTrafficSubject();
            try { _subject.Task.LookAt(player, 3500); }
            catch (Exception ex) { _log.Exception("NPC_TRAFFIC_DRIVER_LOOK_FAILED", ex); }
            ReportCitizenDocumentRequestAudio();
            BeginPendingAnimation(PendingAnimation.Documents, DocumentPresentationMilliseconds, now);
            _log.Runtime("NPC_TRAFFIC_DOCUMENTS_READY",
                "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle);
            Notify("Driver remains seated and is providing identification.");
            return true;
        }

        private Ped FindFootPedestrian(Ped player)
        {
            if (IsUsable(_approachCandidate)
                && _approachCandidateUntil > DateTime.UtcNow
                && _approachCandidate.Position.DistanceTo(player.Position) <= FootContactRadius
                && CanUseAsCivilian(_approachCandidate, player)
                && !_approachCandidate.IsInVehicle())
                return _approachCandidate;

            Ped result = null;
            float closest = float.MaxValue;
            Ped[] nearby = World.GetNearbyPeds(player, FootContactRadius);
            if (nearby == null)
                return null;

            foreach (Ped ped in nearby)
            {
                if (!CanUseAsCivilian(ped, player) || ped.IsInVehicle()
                    || (ped.IsInCombat && !ped.IsInCombatAgainst(player)))
                    continue;
                float distance = ped.Position.DistanceTo(player.Position);
                if (distance < closest)
                {
                    closest = distance;
                    result = ped;
                }
            }
            return result;
        }

        private Ped FindTrafficDriver(Ped player, bool playerInVehicle)
        {
            Ped result = null;
            float bestScore = float.MaxValue;
            Ped[] nearby = World.GetNearbyPeds(player, TrafficContactRadius);
            if (nearby == null)
                return null;

            foreach (Ped ped in nearby)
            {
                if (!CanUseAsCivilian(ped, player) || !ped.IsInVehicle()
                    || (ped.IsInCombat && !ped.IsInCombatAgainst(player)))
                    continue;

                Vehicle vehicle = ped.CurrentVehicle;
                if (!IsUsable(vehicle) || !IsDriver(ped, vehicle) || WasRecentlyReleased(vehicle))
                    continue;

                // On foot, the Officer contacts a vehicle that has already
                // pulled over. In a Police vehicle, G may initiate the one
                // selected driver stop without touching unrelated traffic.
                if (!playerInVehicle && Math.Abs(vehicle.Speed) > FootTrafficContactSpeed)
                    continue;

                float distance = vehicle.Position.DistanceTo(player.Position);
                float score = distance + Math.Min(8.0f, Math.Abs(vehicle.Speed) * 0.15f);
                if (playerInVehicle)
                    score += TrafficDirectionPenalty(player.CurrentVehicle, vehicle);
                if (score < bestScore)
                {
                    bestScore = score;
                    result = ped;
                }
            }
            return result;
        }

        private void BeginFootInteraction(Ped player, Ped ped)
        {
            _subject = ped;
            CaptureSubjectInjuryState(ped);
            _subjectVehicle = null;
            BeginInteractionAudioOccurrence(ped);
            _currentRecord = null;
            _dispatcherValidationReceived = false;
            _citizenRecordReviewed = false;
            CitizenRecordVisible = false;
            _backupRequested = false;
            _backupInterceptionInvestigationRequired = false;
            _documentsProvided = false;
            _citizenDocumentDecisionResolved = false;
            _suspiciousFootContact = IsUsable(_approachCandidate)
                && _approachCandidate.Handle == ped.Handle
                && _approachCandidateSuspicious;
            CaptureInteractionPersistence();
            _stage = InteractionStage.FootPreparing;
            DateTime now = DateTime.UtcNow;
            _readyAt = now.AddMilliseconds(Math.Max(
                ContactPreparationMilliseconds, SubjectOrientationMilliseconds));
            _expiresAt = now.AddMilliseconds(InteractionTimeoutMilliseconds);
            _nextSubjectHold = DateTime.MinValue;
            SetSubjectPoliceAware(ped);
            _animationHoldUntil = DateTime.MinValue;
            _interactionAnchor = ped.Position;
            ped.Task.StandStill(6000);
            OrientSubjectToOfficer(ped, player, SubjectOrientationMilliseconds);
            HoldFootSubject(player, now);
            _log.Runtime("NPC_FOOT_CONTACT_STARTED", "Ped=" + ped.Handle);

            if (_suspiciousFootContact)
            {
                LSPDNPCRecord adverseRecord;
                if (_database != null && _database.TryGetAdverseRecord(ped, false, out adverseRecord))
                {
                    _currentRecord = adverseRecord;
                    _log.Runtime("NPC_FOOT_SUSPICIOUS_RECORD_READY",
                        "Ped=" + ped.Handle + "; " + RecordStatus("Database adverse record selected"));
                }
                else
                {
                    _log.Runtime("NPC_FOOT_SUSPICIOUS_RECORD_UNAVAILABLE",
                        "Ped=" + ped.Handle + "; Reason=NPC database could not provide an adverse record");
                }

                _log.Runtime("NPC_FOOT_SUSPICIOUS_BEHAVIOR_STARTED",
                    "Ped=" + ped.Handle + "; Behavior=FastWalkLookBackThenFlee");
                Notify("The citizen noticed Police Anyi and is acting suspiciously. The citizen may run when approached.");
                StartFootFlee(player);
                return;
            }

            ReportInteractionAudio(
                "lsimmersivelife.police.civilian.conversation", "foot-contact");
            Notify("Citizen noticed Police Anyi and stopped. Please wait for a greeting and identification.");
        }

        private void OrientSubjectToOfficer(Ped subject, Ped officer, int duration)
        {
            if (!IsUsable(subject) || !IsUsable(officer))
                return;
            try
            {
                int turnDuration = Math.Max(500, duration);
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    subject, officer, turnDuration);
                subject.Task.LookAt(officer, turnDuration + 1200);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_SUBJECT_ORIENTATION_FAILED", ex);
            }
        }

        private static bool IsCitizenDocumentHandoffReady(Ped player, Ped citizen)
        {
            if (!IsUsable(player) || !IsUsable(citizen)
                || player.IsDead || citizen.IsDead
                || player.IsInVehicle() || citizen.IsInVehicle())
                return false;

            Vector3 playerPosition = player.Position;
            Vector3 citizenPosition = citizen.Position;
            return playerPosition.DistanceTo(citizenPosition)
                    <= CitizenDocumentHandoffDistance
                && Math.Abs(playerPosition.Z - citizenPosition.Z)
                    <= CitizenDocumentHandoffHeightDifference;
        }

        private static bool IsCitizenFacingOfficer(Ped citizen, Ped officer, float toleranceDegrees)
        {
            if (!IsUsable(citizen) || !IsUsable(officer))
                return false;

            Vector3 citizenPosition = citizen.Position;
            Vector3 officerPosition = officer.Position;
            float deltaX = officerPosition.X - citizenPosition.X;
            float deltaY = officerPosition.Y - citizenPosition.Y;
            if (Math.Abs(deltaX) < 0.01f && Math.Abs(deltaY) < 0.01f)
                return false;

            // GTA heading increases counter-clockwise from north: east is 270,
            // so the world-space X delta must be negated for the bearing.
            float desiredHeading = (float)(Math.Atan2(-deltaX, deltaY)
                * (180.0 / Math.PI));
            if (desiredHeading < 0f)
                desiredHeading += 360f;
            float difference = Math.Abs((citizen.Heading - desiredHeading + 540f)
                % 360f - 180f);
            return difference <= Math.Max(0f, toleranceDegrees);
        }

        private static bool OrientActorsForCitizenDocumentHandoff(Ped player, Ped citizen)
        {
            if (!IsCitizenDocumentHandoffReady(player, citizen))
                return false;

            Vector3 playerPosition = player.Position;
            Vector3 citizenPosition = citizen.Position;
            float deltaX = citizenPosition.X - playerPosition.X;
            float deltaY = citizenPosition.Y - playerPosition.Y;
            if (Math.Abs(deltaX) < 0.01f && Math.Abs(deltaY) < 0.01f)
                return false;

            float playerHeading = (float)(Math.Atan2(-deltaX, deltaY)
                * (180.0 / Math.PI));
            if (playerHeading < 0f)
                playerHeading += 360f;
            float citizenHeading = (playerHeading + 180f) % 360f;
            try
            {
                Function.Call(Hash.SET_ENTITY_HEADING, player, playerHeading);
                Function.Call(Hash.SET_ENTITY_HEADING, citizen, citizenHeading);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void BeginTrafficInteraction(Ped player, Ped driver, Vehicle vehicle)
        {
            if (!IsUsable(player) || !IsUsable(driver)
                || !IsUsable(vehicle) || !IsDriver(driver, vehicle))
            {
                Notify("The selected driver is no longer in a usable vehicle.");
                return;
            }

            _subject = driver;
            CaptureSubjectInjuryState(driver);
            _subjectVehicle = vehicle;
            _trafficContactVehiclePosition = vehicle.Position;
            _trafficContactPlayerPosition = player.Position;
            _trafficStopConfirmedPosition = Vector3.Zero;
            _trafficPullOverFallbackTarget = false;
            _trafficStopConfirmedAtTarget = false;
            _trafficPursuitVehicle = null;
            _trafficPursuitStage = TrafficPursuitStage.None;
            _trafficVehicleTransitionStartedAt = DateTime.MinValue;
            _trafficSubjectLostSince = DateTime.MinValue;
            _nextTrafficBackupPursuitSampleAt = DateTime.MinValue;
            _nextTrafficPursuitExitAt = DateTime.MinValue;
            _trafficPursuitExitAttempts = 0;
            _trafficPursuitExitFailureNotified = false;
            _trafficPlayerAimStartedAt = DateTime.MinValue;
            _trafficPlayerAimSubjectHandle = 0;
            _trafficPlayerAimCheckFailureLogged = false;
            _trafficDriverWindowGeometry = default(TrafficDriverWindowGeometry);
            _trafficDirectFallbackLogged = false;
            BeginInteractionAudioOccurrence(driver);
            _currentRecord = null;
            _dispatcherValidationReceived = false;
            _citizenRecordReviewed = false;
            CitizenRecordVisible = false;
            _backupRequested = false;
            _backupInterceptionInvestigationRequired = false;
            _documentsProvided = false;
            _citizenDocumentDecisionResolved = false;
            _suspiciousTrafficContact = IsUsable(_approachCandidate)
                && _approachCandidateTraffic
                && _approachCandidateSuspicious
                && _approachCandidate.IsInVehicle();
            if (_suspiciousTrafficContact)
                EnableControlledCitizenInjury(driver, "SuspiciousTrafficContact");
            CaptureInteractionPersistence();
            DateTime now = DateTime.UtcNow;
            _readyAt = now.AddMilliseconds(ContactPreparationMilliseconds);
            _expiresAt = now.AddMilliseconds(InteractionTimeoutMilliseconds);
            _nextSubjectHold = DateTime.MinValue;
            _animationHoldUntil = DateTime.MinValue;
            _trafficStoppedSince = DateTime.MinValue;
            _trafficStopConfirmed = false;
            _trafficComplianceExitRequested = false;
            _trafficDriverExitConfirmed = false;
            _trafficDriverExitAttempts = 0;
            _nextTrafficHoldAt = DateTime.MinValue;
            _nextTrafficDriverExitCheckAt = DateTime.MinValue;
            _nextTrafficDriverExitAttemptAt = DateTime.MinValue;
            _trafficDriverExitDeadline = DateTime.MinValue;
            SetSubjectPoliceAware(driver);
            if (!BeginTrafficPullOver(now))
            {
                _log.Runtime("NPC_TRAFFIC_PULL_OVER_UNAVAILABLE",
                    "Driver=" + driver.Handle + "; Vehicle=" + vehicle.Handle
                    + "; ContactStarted=false; Reason=NoValidatedRoadsidePosition");
                ReleaseSubject(false);
                ClearInteractionState();
                Notify("No safe roadside stop was available. The driver was not stopped for document contact.");
                return;
            }
            if (_suspiciousTrafficContact)
            {
                LSPDNPCRecord adverseRecord;
                if (_currentRecord == null && _database != null
                    && _database.TryGetAdverseRecord(driver, true, out adverseRecord))
                {
                    _currentRecord = adverseRecord;
                    _log.Runtime("NPC_TRAFFIC_SUSPICIOUS_RECORD_READY",
                        "Driver=" + driver.Handle + "; " + RecordStatus("Database adverse record selected"));
                }
                else
                {
                    _log.Runtime("NPC_TRAFFIC_SUSPICIOUS_RECORD_UNAVAILABLE",
                        "Driver=" + driver.Handle + "; Reason=NPC database could not provide an adverse record");
                }
                _log.Runtime("NPC_TRAFFIC_SUSPICIOUS_CONTACT_STARTED",
                    "Driver=" + driver.Handle + "; Vehicle=" + vehicle.Handle
                    + "; Behavior=SlowDepartureLookBack");
            }
            _log.Runtime("NPC_TRAFFIC_CONTACT_STARTED", "Driver=" + driver.Handle + "; Vehicle=" + vehicle.Handle);
            ReportInteractionAudio(
                "lsimmersivelife.police.traffic.initiated", "traffic-stop-initiated");
            Notify(_stage == InteractionStage.TrafficPullingOver
                ? "One driver selected. The driver is pulling over and remains seated for contact."
                : "One driver selected. The driver remains seated for Police contact.");
        }

        /// <summary>
        /// Gives a selected driver one bounded, local pull-over request before
        /// the contact lock is applied. Parking uses the SHVDN task wrapper so
        /// the interaction never needs to manipulate emergency-light or siren
        /// natives. The Enhanced runtime can return an invalid result from the
        /// roadside helper, so the target resolver uses nearby street probes
        /// and lets the existing stopped-contact path remain the final fallback.
        /// </summary>
        private bool BeginTrafficPullOver(DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle)
                || !IsDriver(_subject, _subjectVehicle))
                return false;

            _stage = InteractionStage.TrafficPullingOver;
            _pullOverDeadline = now.AddSeconds(_settings.PullOverTimeoutSeconds);
            _pullOverRecoveryIssued = false;

            // A real stop beside the officer/contact area is already the
            // requested pull-over. Do not make that driver move to a generated
            // road node merely to satisfy an invisible coordinate gate.
            if (Math.Abs(_subjectVehicle.Speed) <= 0.45f
                && IsTrafficVehicleNearContactArea(_subjectVehicle))
            {
                _trafficPullOverPosition = _subjectVehicle.Position;
                _trafficPullOverFallbackTarget = true;
                _trafficStoppedSince = now;
                _log.Runtime("NPC_TRAFFIC_PULL_OVER_EXISTING_STOP",
                    "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                    + "; StopBasis=ExistingContactArea; TaskIssued=false");
                return true;
            }

            Vector3 roadside;
            try
            {
                if (TryGetValidatedPullOverPosition(out roadside))
                    _trafficPullOverPosition = roadside;
                else
                {
                    // Street-node lookup is a parking guide, not a Citizen
                    // eligibility test. Keep the same contact and make one
                    // bounded stop request at the selected vehicle's current
                    // position when no local node can be resolved.
                    roadside = _subjectVehicle.Position;
                    if (!HasFiniteComponents(roadside))
                        return false;
                    _trafficPullOverPosition = roadside;
                    _trafficPullOverFallbackTarget = true;
                    _log.Runtime("NPC_TRAFFIC_PULL_OVER_TARGET_FALLBACK",
                        "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                        + "; Target=SelectedVehiclePosition; ContactPreserved=true");
                }
                IssueTrafficPullOverTask(now);
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_PULL_OVER_START_FAILED", ex);
                return false;
            }
        }

        private bool TryGetValidatedPullOverPosition(out Vector3 roadside)
        {
            roadside = Vector3.Zero;
            if (!IsUsable(_subjectVehicle))
                return false;

            Vector3 origin = _subjectVehicle.Position;
            Vector3[] probes;
            try
            {
                // Probe only the selected vehicle's immediate road space. Some
                // Enhanced builds return a farther node before a nearby one, so
                // check this bounded set and choose its nearest usable result.
                probes = new[]
                {
                    origin,
                    _subjectVehicle.GetOffsetPosition(new Vector3(0f, 8f, 0f)),
                    _subjectVehicle.GetOffsetPosition(new Vector3(0f, -8f, 0f)),
                    _subjectVehicle.GetOffsetPosition(new Vector3(-4f, 0f, 0f)),
                    _subjectVehicle.GetOffsetPosition(new Vector3(4f, 0f, 0f))
                };
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_PULL_OVER_PROBE_FAILED", ex);
                probes = new[] { origin };
            }

            // SHVDN exposes both occupied and unoccupied street-node queries;
            // Enhanced builds have returned zero for one of the variants in
            // otherwise valid road locations. Try both, but accept only a local
            // candidate on the same elevation. A long drive to a distant node
            // is not the documented roadside contact stop.
            Vector3 nearestRoadside = Vector3.Zero;
            float nearestDistance = float.MaxValue;
            float nearestElevation = float.MaxValue;
            int nearestProbe = -1;
            bool nearestUnoccupied = false;
            for (int probeIndex = 0; probeIndex < probes.Length; probeIndex++)
            {
                for (int queryIndex = 0; queryIndex < 2; queryIndex++)
                {
                    bool unoccupied = queryIndex == 0;
                    Vector3 candidate;
                    try
                    {
                        candidate = World.GetNextPositionOnStreet(probes[probeIndex], unoccupied);
                    }
                    catch
                    {
                        continue;
                    }

                    float distance;
                    float elevation;
                    if (!TryAcceptPullOverCandidate(origin, candidate, out distance, out elevation))
                        continue;
                    if (distance < nearestDistance)
                    {
                        nearestRoadside = candidate;
                        nearestDistance = distance;
                        nearestElevation = elevation;
                        nearestProbe = probeIndex;
                        nearestUnoccupied = unoccupied;
                    }
                }
            }

            if (nearestProbe >= 0)
            {
                roadside = nearestRoadside;
                _log.Runtime("NPC_TRAFFIC_PULL_OVER_TARGET_STREET",
                    "Driver=" + (_subject == null ? 0 : _subject.Handle)
                    + "; Vehicle=" + _subjectVehicle.Handle
                    + "; Probe=" + nearestProbe
                    + "; Unoccupied=" + nearestUnoccupied
                    + "; Offset=" + nearestDistance.ToString("0.0")
                    + "; Elevation=" + nearestElevation.ToString("0.0")
                    + "; Selected=NearestLocalCandidate");
                return true;
            }

            _log.Runtime("NPC_TRAFFIC_PULL_OVER_TARGET_UNAVAILABLE",
                "Driver=" + (_subject == null ? 0 : _subject.Handle)
                + "; Vehicle=" + _subjectVehicle.Handle
                + "; Reason=NoNearbyStreetCandidate"
                + "; MaximumTargetDistance=" + MaximumPullOverTargetDistance.ToString("0.0"));
            return false;
        }

        private bool IsTrafficVehicleNearContactArea(Vehicle vehicle)
        {
            if (!IsUsable(vehicle))
                return false;

            Vector3 vehiclePosition;
            try { vehiclePosition = vehicle.Position; }
            catch { return false; }
            if (!HasFiniteComponents(vehiclePosition))
                return false;

            if (IsNearTrafficContactAnchor(vehiclePosition, _trafficContactVehiclePosition)
                || IsNearTrafficContactAnchor(vehiclePosition, _trafficContactPlayerPosition))
                return true;

            Ped player = Game.Player == null ? null : Game.Player.Character;
            return IsUsable(player)
                && IsNearTrafficContactAnchor(vehiclePosition, player.Position);
        }

        private static bool IsNearTrafficContactAnchor(Vector3 position, Vector3 anchor)
        {
            return HasFiniteComponents(position) && HasFiniteComponents(anchor)
                && HorizontalDistance(position, anchor) <= TrafficPullOverContactAreaRadius
                && Math.Abs(position.Z - anchor.Z) <= TrafficPullOverContactAreaElevation;
        }

        private static bool TryAcceptPullOverCandidate(
            Vector3 origin,
            Vector3 candidate,
            out float distance,
            out float elevation)
        {
            distance = float.MaxValue;
            elevation = float.MaxValue;
            if (!IsFiniteWorldPosition(candidate))
                return false;

            distance = origin.DistanceTo(candidate);
            elevation = Math.Abs(origin.Z - candidate.Z);
            return distance <= MaximumPullOverTargetDistance
                && elevation <= MaximumPullOverElevationDifference;
        }

        private bool IsAtTrafficDriverWindow(Ped player)
        {
            if (!IsUsable(player) || !IsUsable(_subjectVehicle) || !IsUsable(_subject))
                return false;

            DateTime now = DateTime.UtcNow;
            if (player.IsInVehicle() || !IsSelectedTrafficDriverStillSeated())
                return false;

            if (EnsureTrafficDriverWindowGeometry(now))
            {
                TrafficDriverWindowGeometry geometry = _trafficDriverWindowGeometry;
                Vector3 playerPosition = player.Position;
                float horizontalToDoor = HorizontalDistance(
                    playerPosition, geometry.WindowPosition);
                float horizontalToGroundAnchor = HorizontalDistance(
                    playerPosition, geometry.OfficerGroundPosition);
                float playerGroundDifference = Math.Abs(
                    playerPosition.Z - geometry.OfficerGroundPosition.Z);
                float playerWindowDifference = Math.Abs(
                    playerPosition.Z - geometry.WindowPosition.Z);

                // Ground and safe-position probes improve the anchor, but the
                // selected driver/window and direct driver-side relationship
                // remain authoritative when either optional probe is absent.
                if (horizontalToDoor <= TrafficWindowContactRadius
                    && horizontalToGroundAnchor <= TrafficWindowGroundContactRadius + 1.0f
                    && playerGroundDifference <= TrafficWindowGroundElevationTolerance + 1.0f
                    && playerWindowDifference <= geometry.MaximumWindowSeparation)
                    return true;
            }

            // Last-resort contact uses only the already selected seated Driver,
            // vehicle transform, and Player position. It performs no model,
            // bone, ground, or safe-position lookup and cannot accept the
            // passenger side or a Player across the road.
            bool directFallback = IsPlayerAtDirectTrafficDriverSide(
                player, _subjectVehicle, _subject, TrafficWindowContactRadius, true);
            if (directFallback && !_trafficDirectFallbackLogged)
            {
                _trafficDirectFallbackLogged = true;
                if (_log != null)
                    _log.Runtime("NPC_TRAFFIC_DRIVER_WINDOW_DIRECT_FALLBACK",
                        "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                        + "; Source=SelectedDriverAndVehicleTransform; ContactPreserved=true");
            }
            return directFallback;
        }

        private bool EnsureTrafficDriverWindowGeometry(DateTime now)
        {
            if (!IsUsable(_subjectVehicle) || !IsUsable(_subject)
                || !IsDriver(_subject, _subjectVehicle))
                return false;

            Vehicle vehicle = _subjectVehicle;
            TrafficDriverWindowGeometry geometry = _trafficDriverWindowGeometry;
            bool vehicleChanged = geometry.VehicleHandle != vehicle.Handle;
            bool vehicleMoved = !vehicleChanged
                && geometry.VehiclePosition.DistanceTo(vehicle.Position)
                    > TrafficWindowGeometryMoveInvalidation;
            bool vehicleTurned = !vehicleChanged
                && GetHeadingDifference(geometry.VehicleHeading, vehicle.Heading)
                    > TrafficWindowGeometryHeadingInvalidation;
            bool retryFailedResolution = !geometry.Resolved
                && !vehicleChanged && !vehicleMoved && !vehicleTurned
                && (geometry.LastAttemptAt == DateTime.MinValue
                    || now >= geometry.LastAttemptAt.AddMilliseconds(
                        TrafficWindowGeometryRetryMilliseconds));

            if (!vehicleChanged && !vehicleMoved && !vehicleTurned
                && !retryFailedResolution)
                return geometry.Resolved;

            if (vehicleChanged || vehicleMoved || vehicleTurned)
                _trafficDirectFallbackLogged = false;

            geometry = new TrafficDriverWindowGeometry
            {
                VehicleHandle = vehicle.Handle,
                VehiclePosition = vehicle.Position,
                VehicleHeading = vehicle.Heading,
                LastAttemptAt = now
            };

            string geometrySource;
            float vehicleHeight;
            Vector3 windowPosition;
            Vector3 officerGroundPosition;
            float maximumWindowSeparation;
            if (!TryResolveTrafficDriverWindowGeometry(
                vehicle,
                _subject,
                out windowPosition,
                out officerGroundPosition,
                out maximumWindowSeparation,
                out vehicleHeight,
                out geometrySource))
            {
                _trafficDriverWindowGeometry = geometry;
                return false;
            }

            geometry.WindowPosition = windowPosition;
            geometry.OfficerGroundPosition = officerGroundPosition;
            geometry.MaximumWindowSeparation = maximumWindowSeparation;
            geometry.Resolved = true;
            _trafficDriverWindowGeometry = geometry;
            _trafficDirectFallbackLogged = false;
            if (_log != null)
            {
                _log.Runtime("NPC_TRAFFIC_DRIVER_WINDOW_GEOMETRY_RESOLVED",
                    "Driver=" + _subject.Handle
                    + "; Vehicle=" + vehicle.Handle
                    + "; Source=" + geometrySource
                    + "; WindowElevation=" + Math.Abs(
                        windowPosition.Z - officerGroundPosition.Z).ToString("0.0"));
                if (Math.Abs(windowPosition.Z - officerGroundPosition.Z) > 2.25f)
                    _log.Runtime("NPC_TRAFFIC_ELEVATED_CONTACT_ANCHOR_RESOLVED",
                        "Driver=" + _subject.Handle
                        + "; Vehicle=" + vehicle.Handle
                        + "; Source=" + geometrySource
                        + "; ModelHeight=" + vehicleHeight.ToString("0.0"));
            }
            return true;
        }

        private bool TryResolveTrafficDriverWindowGeometry(
            Vehicle vehicle,
            Ped driver,
            out Vector3 windowPosition,
            out Vector3 officerGroundPosition,
            out float maximumWindowSeparation,
            out float vehicleHeight,
            out string geometrySource)
        {
            windowPosition = Vector3.Zero;
            officerGroundPosition = Vector3.Zero;
            maximumWindowSeparation = 0f;
            vehicleHeight = 0f;
            geometrySource = string.Empty;

            try
            {
                if (!IsUsable(vehicle) || !IsUsable(driver)
                    || !IsDriver(driver, vehicle))
                    return false;

                Vector3 right;
                Vector3 forward;
                Vector3 up;
                if (!TryGetTrafficVehicleAxes(vehicle, out right, out forward, out up))
                    return false;

                Vector3 vehiclePosition = vehicle.Position;
                Vector3 seatRelative = driver.Position - vehiclePosition;
                Vector3 driverLocal = new Vector3(
                    DotProduct(seatRelative, right),
                    DotProduct(seatRelative, forward),
                    DotProduct(seatRelative, up));
                if (!HasFiniteComponents(driverLocal))
                    return false;

                float driverSide = Math.Abs(driverLocal.X) >= 0.20f
                    ? (driverLocal.X < 0f ? -1f : 1f)
                    : -1f;
                Vector3 doorLocal = Vector3.Zero;
                Vector3 doorWorld = Vector3.Zero;
                bool doorBoneFound = false;

                // Prefer the model's actual driver door before asking for
                // dimensions. A valid door is sufficient for the window point;
                // dimension lookup belongs only to the transform fallback.
                try
                {
                    if (vehicle.Bones != null
                        && vehicle.Bones.Contains("door_dside_f"))
                    {
                        EntityBone driverDoor = vehicle.Bones["door_dside_f"];
                        if (driverDoor != null && driverDoor.IsValid)
                        {
                            Vector3 candidateDoorWorld = driverDoor.Position;
                            Vector3 doorRelative = candidateDoorWorld - vehicle.Position;
                            Vector3 candidate = new Vector3(
                                DotProduct(doorRelative, right),
                                DotProduct(doorRelative, forward),
                                DotProduct(doorRelative, up));
                            bool onDriverSide = HasFiniteComponents(candidate)
                                && Math.Abs(candidate.X) >= 0.25f
                                && (Math.Abs(driverLocal.X) < 0.20f
                                    || Math.Sign(candidate.X) == Math.Sign(driverSide));
                            bool boundedDoor = Math.Abs(candidate.X) <= 4.5f
                                && Math.Abs(candidate.Y) <= 14.0f
                                && Math.Abs(candidate.Z) <= 8.0f;
                            if (HasFiniteComponents(candidate)
                                && boundedDoor && onDriverSide
                                && HasFiniteComponents(candidateDoorWorld))
                            {
                                doorLocal = candidate;
                                doorWorld = candidateDoorWorld;
                                driverSide = candidate.X < 0f ? -1f : 1f;
                                doorBoneFound = true;
                            }
                        }
                    }
                }
                catch
                {
                    // A missing or invalid optional bone falls through to the
                    // bounded transform/seat estimate below.
                }

                Vector3 modelMinimum = new Vector3(-1.0f, -2.2f, -0.5f);
                Vector3 modelMaximum = new Vector3(1.0f, 2.2f, 2.5f);
                bool dimensionsAvailable = false;
                float width = 2.0f;
                float length = 4.4f;
                float minX = -1.0f;
                float maxX = 1.0f;
                float minY = -2.2f;
                float maxY = 2.2f;
                float minZ = -0.5f;
                float maxZ = 2.5f;
                vehicleHeight = 3.0f;

                if (!doorBoneFound)
                {
                    try
                    {
                        Vector3 candidateMinimum;
                        Vector3 candidateMaximum;
                        vehicle.Model.GetDimensions(out candidateMinimum, out candidateMaximum);
                        float candidateWidth = candidateMaximum.X - candidateMinimum.X;
                        float candidateLength = candidateMaximum.Y - candidateMinimum.Y;
                        float candidateHeight = candidateMaximum.Z - candidateMinimum.Z;
                        if (HasFiniteComponents(candidateMinimum)
                            && HasFiniteComponents(candidateMaximum)
                            && candidateWidth > 0.4f && candidateLength > 0.8f
                            && candidateHeight > 0.6f)
                        {
                            modelMinimum = candidateMinimum;
                            modelMaximum = candidateMaximum;
                            dimensionsAvailable = true;
                        }
                    }
                    catch
                    {
                        // Some addon/streaming models do not expose dimensions.
                        // The selected seated Driver and transform remain usable.
                    }

                    if (dimensionsAvailable)
                    {
                        float rawWidth = modelMaximum.X - modelMinimum.X;
                        float rawLength = modelMaximum.Y - modelMinimum.Y;
                        float rawHeight = modelMaximum.Z - modelMinimum.Z;
                        width = ClampTrafficGeometry(rawWidth, 1.4f, 6.0f);
                        length = ClampTrafficGeometry(rawLength, 2.2f, 24.0f);
                        vehicleHeight = ClampTrafficGeometry(rawHeight, 1.2f, 7.0f);
                        minX = ClampTrafficGeometry(modelMinimum.X, -3.5f, 3.5f);
                        maxX = ClampTrafficGeometry(modelMaximum.X, -3.5f, 3.5f);
                        minY = ClampTrafficGeometry(modelMinimum.Y, -12.0f, 12.0f);
                        maxY = ClampTrafficGeometry(modelMaximum.Y, -12.0f, 12.0f);
                        minZ = ClampTrafficGeometry(modelMinimum.Z, -1.5f, 4.0f);
                        maxZ = ClampTrafficGeometry(modelMaximum.Z, 0.5f, 7.0f);
                        if (maxX <= minX || maxY <= minY || maxZ <= minZ + 0.8f)
                        {
                            minX = -1.0f;
                            maxX = 1.0f;
                            minY = -2.2f;
                            maxY = 2.2f;
                            minZ = -0.5f;
                            maxZ = 2.5f;
                            width = 2.0f;
                            length = 4.4f;
                            vehicleHeight = 3.0f;
                            dimensionsAvailable = false;
                        }
                    }

                    float sideX = driverSide < 0f ? minX : maxX;
                    if (Math.Abs(sideX) < 0.35f)
                        sideX = driverSide * width * 0.5f;

                    float frontStart = minY + (maxY - minY) * 0.55f;
                    float frontEnd = maxY - Math.Min(0.25f, length * 0.03f);
                    if (frontEnd < frontStart)
                        frontStart = minY;
                    float seatY = ClampTrafficGeometry(driverLocal.Y, minY, maxY);
                    float seatZ = ClampTrafficGeometry(driverLocal.Z, minZ, maxZ);
                    float localY = ClampTrafficGeometry(seatY, frontStart, frontEnd);
                    float localZ = ClampTrafficGeometry(
                        seatZ + 0.35f, minZ + 0.25f, maxZ - 0.10f);
                    doorLocal = new Vector3(sideX, localY, localZ);
                }

                Vector3 window;
                if (doorBoneFound)
                    window = doorWorld + up * 0.25f;
                else
                {
                    float driverSeatZ = ClampTrafficGeometry(
                        driverLocal.Z, minZ, maxZ);
                    float windowZ = ClampTrafficGeometry(
                        Math.Max(driverSeatZ + 0.35f, doorLocal.Z + 0.25f),
                        minZ + 0.55f,
                        maxZ - 0.15f);
                    Vector3 windowLocal = new Vector3(
                        doorLocal.X, doorLocal.Y, windowZ);
                    window = GetTrafficVehicleOffsetPosition(
                        vehicle, windowLocal, vehiclePosition, right, forward, up);
                }

                if (doorBoneFound)
                {
                    width = ClampTrafficGeometry(
                        Math.Abs(doorLocal.X) * 2.0f, 1.4f, 6.0f);
                    vehicleHeight = ClampTrafficGeometry(
                        Math.Abs(doorLocal.Z) + 1.5f, 1.8f, 7.0f);
                }
                float sideOffset = ClampTrafficGeometry(width * 0.12f, 0.45f, 0.90f);
                Vector3 groundLocal = new Vector3(
                    doorLocal.X + driverSide * sideOffset,
                    doorLocal.Y,
                    0f);
                Vector3 groundProbe = GetTrafficVehicleOffsetPosition(
                    vehicle, groundLocal, vehiclePosition, right, forward, up);
                groundProbe.Z = vehiclePosition.Z;
                Vector3 groundPosition = groundProbe;

                // Ground and safe-ped lookups are best-effort refinements. A
                // nearby occupied vehicle can make either native reject a
                // perfectly usable driver-side point, so retain the bounded
                // vehicle-transform anchor when they do.
                try
                {
                    float groundHeight;
                    Vector3 groundNormal;
                    if (World.GetGroundHeightAndNormal(
                        groundProbe, out groundHeight, out groundNormal)
                        && HasFiniteComponents(groundNormal)
                        && !float.IsNaN(groundHeight) && !float.IsInfinity(groundHeight))
                    {
                        groundPosition.Z = groundHeight;
                        Vector3 groundedPoint = new Vector3(
                            groundProbe.X, groundProbe.Y, groundHeight);
                        Vector3 safePoint;
                        GetSafePositionFlags safeFlags = GetSafePositionFlags.NotWater
                            | GetSafePositionFlags.NotInterior;
                        if (World.GetSafePositionForPed(
                            groundedPoint, out safePoint, safeFlags)
                            && HasFiniteComponents(safePoint)
                            && HorizontalDistance(safePoint, groundedPoint) <= 1.75f
                            && Math.Abs(safePoint.Z - groundHeight) <= 1.75f)
                            groundPosition = safePoint;
                    }
                }
                catch
                {
                    // Keep the selected vehicle's ground-level side estimate.
                }

                if (doorBoneFound && !dimensionsAvailable)
                    vehicleHeight = ClampTrafficGeometry(
                        Math.Max(vehicleHeight,
                            Math.Abs(window.Z - groundPosition.Z) + 0.75f),
                        1.8f, 7.0f);
                maximumWindowSeparation = ClampTrafficGeometry(
                    vehicleHeight * 0.95f + 0.5f, 2.25f, 5.5f);
                if (!HasFiniteComponents(window) || !HasFiniteComponents(groundPosition))
                    return false;

                windowPosition = window;
                officerGroundPosition = groundPosition;
                geometrySource = doorBoneFound
                    ? "DriverDoorBone"
                    : dimensionsAvailable
                        ? "DriverSeatAndClampedModelDimensions"
                        : "DriverSeatTransformFallback";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetTrafficVehicleAxes(
            Vehicle vehicle,
            out Vector3 right,
            out Vector3 forward,
            out Vector3 up)
        {
            right = Vector3.Zero;
            forward = Vector3.Zero;
            up = new Vector3(0f, 0f, 1f);
            if (!IsUsable(vehicle))
                return false;

            bool nativeAxesValid = false;
            try
            {
                right = vehicle.RightVector;
                forward = vehicle.ForwardVector;
                up = vehicle.UpVector;
                nativeAxesValid = TryNormalize(ref right)
                    && TryNormalize(ref forward);
                if (!TryNormalize(ref up))
                    up = new Vector3(0f, 0f, 1f);
            }
            catch
            {
                nativeAxesValid = false;
            }

            if (nativeAxesValid)
                return true;

            try
            {
                double radians = vehicle.Heading * Math.PI / 180.0;
                float cosine = (float)Math.Cos(radians);
                float sine = (float)Math.Sin(radians);
                right = new Vector3(cosine, sine, 0f);
                forward = new Vector3(-sine, cosine, 0f);
                up = new Vector3(0f, 0f, 1f);
                return TryNormalize(ref right) && TryNormalize(ref forward);
            }
            catch
            {
                return false;
            }
        }

        private static Vector3 GetTrafficVehicleOffsetPosition(
            Vehicle vehicle,
            Vector3 localOffset,
            Vector3 vehiclePosition,
            Vector3 right,
            Vector3 forward,
            Vector3 up)
        {
            try
            {
                Vector3 offsetPosition = vehicle.GetOffsetPosition(localOffset);
                if (HasFiniteComponents(offsetPosition))
                    return offsetPosition;
            }
            catch
            {
                // Use the already resolved bounded transform below.
            }

            return vehiclePosition
                + right * localOffset.X
                + forward * localOffset.Y
                + up * localOffset.Z;
        }

        private bool IsPlayerAtDirectTrafficDriverSide(
            Ped player,
            Vehicle vehicle,
            Ped driver,
            float maximumDistance,
            bool requireGroundLevel)
        {
            if (!IsUsable(player) || !IsUsable(vehicle) || !IsUsable(driver)
                || player.IsInVehicle() || !IsDriver(driver, vehicle))
                return false;

            Vector3 right;
            Vector3 forward;
            Vector3 up;
            if (!TryGetTrafficVehicleAxes(vehicle, out right, out forward, out up))
                return false;

            Vector3 vehiclePosition = vehicle.Position;
            Vector3 driverPosition = driver.Position;
            Vector3 playerPosition = player.Position;
            Vector3 driverRelative = driverPosition - vehiclePosition;
            float driverSideCoordinate = DotProduct(driverRelative, right);
            float driverSide = Math.Abs(driverSideCoordinate) >= 0.20f
                ? (driverSideCoordinate < 0f ? -1f : 1f)
                : -1f;
            Vector3 officerRelative = playerPosition - driverPosition;
            float lateralFromDriver = DotProduct(officerRelative, right) * driverSide;
            float longitudinalFromDriver = Math.Abs(DotProduct(officerRelative, forward));
            float horizontalFromDriver = HorizontalDistance(playerPosition, driverPosition);
            float groundElevation = Math.Abs(playerPosition.Z - vehiclePosition.Z);

            return HasFiniteComponents(playerPosition)
                && lateralFromDriver >= -0.75f
                && lateralFromDriver <= maximumDistance
                && longitudinalFromDriver <= 3.75f
                && horizontalFromDriver <= maximumDistance + 1.75f
                && (!requireGroundLevel
                    || groundElevation <= TrafficDriverFallbackElevationTolerance);
        }

        private void SuppressTrafficContactVehicleControls(Ped player)
        {
            if (!_settings.InteractionsEnabled || !HasActiveInteraction
                || !IsUsable(player) || player.IsInVehicle()
                || !IsUsable(_subjectVehicle) || !IsUsable(_subject)
                || (_stage != InteractionStage.TrafficPullingOver
                    && _stage != InteractionStage.TrafficPreparing
                    && _stage != InteractionStage.TrafficDocuments)
                || !IsSelectedTrafficDriverStillSeated())
                return;

            bool besideSelectedDriver = IsAtTrafficDriverWindow(player)
                || IsPlayerAtDirectTrafficDriverSide(
                    player, _subjectVehicle, _subject, TrafficWindowContactRadius, true);
            if (!besideSelectedDriver)
                return;

            // These controls are masked only at this selected driver's
            // ground-side contact point. The configured Citizen key is read
            // separately with GetAsyncKeyState, so the request remains usable.
            Game.DisableControlThisFrame(GTA.Control.Context);
            Game.DisableControlThisFrame(GTA.Control.Enter);
            Game.DisableControlThisFrame(GTA.Control.Jump);
        }

        private static float HorizontalDistance(Vector3 first, Vector3 second)
        {
            float x = first.X - second.X;
            float y = first.Y - second.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        private static float DotProduct(Vector3 first, Vector3 second)
        {
            return first.X * second.X + first.Y * second.Y + first.Z * second.Z;
        }

        private static bool TryNormalize(ref Vector3 vector)
        {
            if (!HasFiniteComponents(vector))
                return false;
            float length = (float)Math.Sqrt(
                vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
            if (length <= 0.001f)
                return false;
            vector = new Vector3(
                vector.X / length, vector.Y / length, vector.Z / length);
            return true;
        }

        private static bool HasFiniteComponents(Vector3 value)
        {
            return !float.IsNaN(value.X) && !float.IsInfinity(value.X)
                && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y)
                && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
        }

        private static float ClampTrafficGeometry(float value, float minimum, float maximum)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static float GetHeadingDifference(float first, float second)
        {
            float difference = Math.Abs(first - second) % 360f;
            return difference > 180f ? 360f - difference : difference;
        }

        private static bool IsFiniteWorldPosition(Vector3 position)
        {
            return position != Vector3.Zero
                && !float.IsNaN(position.X) && !float.IsInfinity(position.X)
                && !float.IsNaN(position.Y) && !float.IsInfinity(position.Y)
                && !float.IsNaN(position.Z) && !float.IsInfinity(position.Z);
        }

        private void MaintainTrafficPullOver(DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle)
                || !IsDriver(_subject, _subjectVehicle))
            {
                ReleaseSubject(true);
                ClearInteractionState();
                Notify("Traffic contact ended because the selected driver is unavailable.");
                return;
            }

            float distanceToRoadside = _subjectVehicle.Position.DistanceTo(_trafficPullOverPosition);
            float speed = Math.Abs(_subjectVehicle.Speed);
            bool atSelectedRoadside = !_trafficPullOverFallbackTarget
                && distanceToRoadside <= 6.0f;
            bool nearContactArea = IsTrafficVehicleNearContactArea(_subjectVehicle);
            bool physicallyStopped = speed <= 0.45f;
            if ((atSelectedRoadside || nearContactArea) && physicallyStopped)
            {
                if (_trafficStoppedSince == DateTime.MinValue)
                    _trafficStoppedSince = now;
                else if (now - _trafficStoppedSince >= TimeSpan.FromSeconds(1.0))
                {
                    _trafficStopConfirmed = true;
                    _trafficStopConfirmedPosition = _subjectVehicle.Position;
                    _trafficStopConfirmedAtTarget = atSelectedRoadside;
                    _stage = InteractionStage.TrafficPreparing;
                    _readyAt = now.AddMilliseconds(ContactPreparationMilliseconds);
                    _nextSubjectHold = DateTime.MinValue;
                    HoldTrafficSubject();
                    _log.Runtime("NPC_TRAFFIC_PULL_OVER_COMPLETED",
                        "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                        + "; RoadsideDistance=" + distanceToRoadside.ToString("0.0")
                        + "; Speed=" + speed.ToString("0.00")
                        + "; StopBasis=" + (atSelectedRoadside
                            ? "GeneratedTarget" : "ExistingContactArea")
                        + "; SameDriver=true; ContinuousStoppedMilliseconds=1000");
                    Notify("Driver stopped near the contact area. Walk to the window to investigate.");
                    return;
                }
            }
            else
                _trafficStoppedSince = DateTime.MinValue;

            if (now >= _pullOverDeadline)
            {
                int driverHandle = _subject.Handle;
                int vehicleHandle = _subjectVehicle.Handle;
                ReleaseSubject(false);
                ClearInteractionState();
                _log.Runtime("NPC_TRAFFIC_PULL_OVER_NOT_CONFIRMED",
                    "Driver=" + driverHandle + "; Vehicle=" + vehicleHandle
                    + "; Distance=" + distanceToRoadside.ToString("0.0")
                    + "; Speed=" + speed.ToString("0.00")
                    + "; ContactStarted=false");
                Notify("The selected driver did not complete the roadside stop. Contact ended without document or custody actions.");
                return;
            }

            // GTA keeps the original parking task. One recovery is allowed only
            // after a real stall window; continuously clearing and replacing
            // this task is what produced the old stop/move/snail behavior.
            if (!_pullOverRecoveryIssued && now >= _nextPullOverTaskAt
                && Math.Abs(_subjectVehicle.Speed) < 0.75f
                && distanceToRoadside > 9.0f)
            {
                _pullOverRecoveryIssued = true;
                IssueTrafficPullOverTask(now);
                _log.Runtime("NPC_TRAFFIC_PULL_OVER_RECOVERY",
                    "Driver=" + _subject.Handle + "; Distance=" + distanceToRoadside.ToString("0.0"));
            }
        }

        private void IssueTrafficPullOverTask(DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle))
                return;

            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, false);
                _subject.Task.ClearAll();
                _subject.Task.ParkVehicle(_subjectVehicle, _trafficPullOverPosition,
                    _subjectVehicle.Heading, ParkType.PullOver, 8.0f, false);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_PULL_OVER_TASK_FAILED", ex);
            }

            _nextPullOverTaskAt = now.AddSeconds(_settings.TrafficTaskRecoverySeconds);
        }

        private void MaintainInteraction(Ped player, DateTime now)
        {
            if (!HasActiveInteraction)
                return;

            bool subjectExists = IsUsable(_subject);
            bool vehicleExists = _subjectVehicle == null || IsUsable(_subjectVehicle);
            if (!subjectExists || _subject.IsDead || _subject.Health <= 0 || !vehicleExists)
            {
                bool subjectDown = subjectExists
                    && (_subject.IsDead || _subject.Health <= 0);

                string availability = "Stage=" + _stage
                    + "; PedExists=" + subjectExists
                    + "; VehicleExists=" + vehicleExists;

                if (subjectDown
                    && _subjectVehicle == null
                    && !_backupRequested
                    && IsFootDeadSceneStage(_stage))
                {
                    int downedHandle = _subject.Handle;
                    InteractionStage downedStage = _stage;
                    bool ordinaryContactEnded = IsOrdinaryCitizenPresentationStage();
                    DeferDeadFootSubject(_subject, now);
                    ClearInteractionState();
                    _log.Runtime("NPC_FOOT_SUBJECT_DOWN",
                        availability + "; Ped=" + downedHandle
                        + "; Cleanup=DeferredUntilPlayerLeavesArea");
                    if (ordinaryContactEnded)
                    {
                        _log.Runtime("NPC_FOOT_ORDINARY_CONTACT_ENDED",
                            "Ped=" + downedHandle + "; Stage=" + downedStage
                            + "; Outcome=CitizenDiedDuringContact"
                            + "; DocumentDecisionAvailable=false; ArrestStarted=false"
                            + "; Suspicious=false; Cleanup=DeferredUntilPlayerLeavesArea");
                        Notify("The citizen died during ordinary contact. The contact ended without a document decision or arrest; the scene will remain until you leave the area.");
                    }
                    else
                    {
                        _log.Runtime("NPC_FOOT_MISSION_JUSTIFIED",
                            "Ped=" + downedHandle + "; Outcome="
                            + (downedStage == InteractionStage.FootResisting
                                ? "ResistingCitizenNeutralized"
                                : downedStage == InteractionStage.FootFleeing
                                    ? "FleeingCitizenNeutralized"
                                    : "FootCitizenNeutralized"));
                        Notify("The citizen died. The foot-citizen incident is resolved; the scene will remain until you leave the area.");
                    }
                    return;
                }

                if (subjectDown
                    && _subjectVehicle != null
                    && IsTrafficDeadSceneStage(_stage))
                {
                    int downedHandle = _subject.Handle;
                    InteractionStage downedStage = _stage;
                    DeferDeadFootSubject(_subject, now);
                    ClearInteractionState();
                    _log.Runtime("NPC_TRAFFIC_SUBJECT_DOWN",
                        availability + "; Ped=" + downedHandle
                        + "; Cleanup=DeferredUntilPlayerLeavesArea");
                    _log.Runtime("NPC_TRAFFIC_MISSION_JUSTIFIED",
                        "Ped=" + downedHandle + "; Outcome="
                        + (downedStage == InteractionStage.TrafficResisting
                            ? "ResistingDriverNeutralized"
                            : downedStage == InteractionStage.TrafficFleeing
                                ? "FleeingDriverNeutralized"
                                : "TrafficDriverNeutralized"));
                    Notify("The driver died. The traffic incident is resolved; the scene will remain until you leave the area.");
                    return;
                }

                ReleaseSubject(true);
                ClearInteractionState();
                if (subjectDown)
                    _log.Runtime(
                        "NPC_INTERACTION_ENDED",
                        "Subject was down. " + availability);
                else
                    _log.StateFailure(
                        "NPC_INTERACTION_SUBJECT_UNAVAILABLE",
                        "Subject was no longer available. " + availability);
                Notify(subjectDown
                    ? "NPC interaction closed because the subject died."
                    : "NPC interaction ended because the subject is unavailable.");
                return;
            }

            // A punch reaction temporarily owns the same Ped's physical pose.
            // Keep the interaction and its selected response branch intact;
            // defer task refresh and arrest until the reaction has ended.
            if (IsSubjectMeleeReactionPending(_subject))
                return;

            SubjectInjuryState activeInjury;
            if (_subjectInjuries.TryGetValue(_subject.Handle, out activeInjury)
                && activeInjury.Wounded)
            {
                if (IsOrdinaryCitizenPresentationStage())
                {
                    EndOrdinaryContactForInjury();
                    return;
                }
                bool physicallyDowned = IsLivingDowned(_subject)
                    || IsLivingInWrithe(_subject);
                if (physicallyDowned
                    && _stage != InteractionStage.FootPlayerArresting
                    && !_footPlayerArrested
                    && _stage != InteractionStage.BackupSecuring
                    && _stage != InteractionStage.BackupEscort
                    && _stage != InteractionStage.BackupTransport
                    && !(_stage == InteractionStage.TrafficAwaitingBackup
                        && _trafficComplianceExitRequested))
                    return;
            }

            if (_subjectVehicle != null
                && (_stage == InteractionStage.TrafficPreparing
                    || _stage == InteractionStage.TrafficDocuments)
                && (!IsTrafficStopAtSelectedRoadside()
                    || !IsDriver(_subject, _subjectVehicle)
                    || !IsSelectedTrafficDriverStillSeated()))
            {
                int driverHandle = _subject.Handle;
                int vehicleHandle = IsUsable(_subjectVehicle) ? _subjectVehicle.Handle : 0;
                ReleaseSubject(false);
                ClearInteractionState();
                _log.Runtime("NPC_TRAFFIC_CONTACT_STOP_LOST",
                    "Driver=" + driverHandle + "; Vehicle=" + vehicleHandle
                    + "; DocumentDecisionAvailable=false; ArrestStarted=false");
                Notify("The same driver and vehicle left the confirmed roadside stop. Contact ended without a document decision or arrest.");
                return;
            }

            MaintainPendingAnimation(now);

            if (TryCompleteTrafficPlayerAimSurrender(player, now))
                return;

            if (_backupRequested && _stage == InteractionStage.TrafficAwaitingBackup
                && _trafficContactFled)
                MaintainTrafficBackupPursuitTransition(player, now);

            bool footOrTrafficPursuit = _stage == InteractionStage.FootFleeing
                || _stage == InteractionStage.TrafficFleeing;
            bool trafficPullingOver = _stage == InteractionStage.TrafficPullingOver;
            bool stationFollow = _stage == InteractionStage.FootStationFollow
                || _stage == InteractionStage.TrafficStationFollow;
            bool backupFlow = _stage == InteractionStage.FootAwaitingBackup
                || _stage == InteractionStage.FootBackupContained
                || _stage == InteractionStage.TrafficAwaitingBackup
                || _stage == InteractionStage.TrafficBackupContained
                || _stage == InteractionStage.TrafficResisting
                || _stage == InteractionStage.BackupSecuring
                || _stage == InteractionStage.BackupEscort
                || _stage == InteractionStage.BackupTransport;
            bool backupOwnsPhysicalCustody = _backupRequested && backupFlow;
            float subjectDistance = _subject.Position.DistanceTo(player.Position);
            if ((now >= _expiresAt && !backupOwnsPhysicalCustody)
                || (footOrTrafficPursuit && subjectDistance > (_backupRequested ? 1000.0f : FollowReleaseDistance))
                || (!footOrTrafficPursuit && !trafficPullingOver
                    && !stationFollow && !backupFlow
                    && subjectDistance > InteractionReleaseDistance)
                || (trafficPullingOver
                    && subjectDistance > Math.Max(80.0f, TrafficContactRadius * 2.0f)))
            {
                ReleaseSubject(true);
                ClearInteractionState();
                Notify(footOrTrafficPursuit
                    ? "NPC pursuit contact closed because the subject escaped the local area."
                    : "NPC interaction ended and the subject was released.");
                return;
            }

            if (_stage == InteractionStage.FootFleeing)
            {
                MaintainFootFlee(player, now);
                return;
            }

            if (_stage == InteractionStage.FootStationFollow)
            {
                MaintainFootStationFollow(now);
                return;
            }

            if (_stage == InteractionStage.TrafficFleeing)
            {
                MaintainTrafficFlee(player, now);
                return;
            }

            if (_stage == InteractionStage.TrafficResisting)
            {
                MaintainTrafficResistance(player, now);
                return;
            }

            if (_stage == InteractionStage.TrafficStationFollow)
            {
                MaintainTrafficStationFollow(now);
                return;
            }

            if (_stage == InteractionStage.TrafficPullingOver)
            {
                MaintainTrafficPullOver(now);
                return;
            }

            if (_stage == InteractionStage.FootAwaitingBackup)
            {
                HoldFootSubject(player, now);
                return;
            }

            if (_stage == InteractionStage.FootAwaitingPlayerArrest)
            {
                HoldFootSubject(player, now);
                return;
            }

            if (_stage == InteractionStage.FootPlayerArresting)
            {
                MaintainFootPlayerArrest(now, player);
                return;
            }

            if (_stage == InteractionStage.FootResisting)
            {
                MaintainFootResistance(player, now);
                return;
            }

            if (_stage == InteractionStage.FootBackupContained)
            {
                HoldFootSubject(player, now);
                return;
            }

            if (_stage == InteractionStage.TrafficAwaitingBackup)
            {
                if (_trafficComplianceExitRequested && !_backupRequested)
                    MaintainTrafficComplianceExit(now);
                else if (_trafficPursuitStage == TrafficPursuitStage.VehicleFlight
                    || _trafficPursuitStage == TrafficPursuitStage.OnFootFlight)
                {
                    // Backup owns pursuit pressure while assigned. Do not
                    // brake or hold the selected vehicle during live flight.
                }
                else if (_trafficPursuitStage == TrafficPursuitStage.Contained)
                    HoldTrafficBackupContained(player, now);
                else if (_trafficPursuitStage == TrafficPursuitStage.VehicleStoppedAwaitingExit
                    && IsUsable(_trafficPursuitVehicle))
                    HoldTrafficSubject(_trafficPursuitVehicle);
                else
                    HoldTrafficSubject();
                return;
            }

            if (_stage == InteractionStage.TrafficBackupContained)
            {
                HoldTrafficBackupContained(player, now);
                return;
            }

            if (_stage == InteractionStage.BackupSecuring
                || _stage == InteractionStage.BackupEscort
                || _stage == InteractionStage.BackupTransport)
                return;

            if (now >= _readyAt)
            {
                if (_stage == InteractionStage.FootPreparing)
                {
                    _stage = InteractionStage.FootGreeting;
                    _footGreetingConfirmed = false;
                    _footGreetingAttempts = 1;
                    _log.Runtime("NPC_FOOT_GREETING_STARTED",
                        "Ped=" + _subject.Handle + "; Attempt=" + _footGreetingAttempts
                        + "; Mode=OfficerFacing; WavePlayed=false");
                    if (IsCitizenFacingOfficer(_subject, player, 30f))
                    {
                        _footGreetingConfirmed = true;
                        _readyAt = DateTime.MaxValue;
                        _log.Runtime("NPC_FOOT_GREETING_COMPLETED",
                            "Ped=" + _subject.Handle
                            + "; Mode=OfficerFacing; OrientationConfirmed=true"
                            + "; WavePlayed=false");
                        Notify("Citizen is facing you and ready to provide identification.");
                    }
                    else
                    {
                        OrientSubjectToOfficer(
                            _subject, player, SubjectOrientationMilliseconds);
                        _readyAt = now.AddMilliseconds(SubjectOrientationMilliseconds);
                    }
                    if (!_footGreetingConfirmed)
                        Notify("Citizen stopped and is turning to face the officer.");
                }
                else if (_stage == InteractionStage.FootGreeting
                    && !_footGreetingConfirmed)
                {
                    if (IsCitizenFacingOfficer(_subject, player, 30f))
                    {
                        _footGreetingConfirmed = true;
                        _readyAt = DateTime.MaxValue;
                        _log.Runtime("NPC_FOOT_GREETING_COMPLETED",
                            "Ped=" + _subject.Handle
                            + "; Mode=OfficerFacing; OrientationConfirmed=true"
                            + "; WavePlayed=false");
                        Notify("Citizen is facing you and ready to provide identification.");
                    }
                    else if (_footGreetingAttempts < MaximumFootGreetingAttempts)
                    {
                        _footGreetingAttempts++;
                        OrientSubjectToOfficer(
                            _subject, player, SubjectOrientationMilliseconds);
                        _readyAt = now.AddMilliseconds(SubjectOrientationMilliseconds);
                    }
                    else
                    {
                        _readyAt = DateTime.MaxValue;
                        Notify("The selected citizen is not facing you yet. Press "
                            + _controls.SecureKey + " to retry orientation.");
                    }
                }
                else if (_stage == InteractionStage.FootGreeting
                    && _footGreetingConfirmed)
                {
                    _readyAt = DateTime.MaxValue;
                    _log.Runtime("NPC_FOOT_AWAITING_DOCUMENT_REQUEST", "Ped=" + _subject.Handle);
                    Notify("Citizen is ready to provide identification. Open Citizen Ped Response and select Request the Validation ID/Document.");
                }
            }

            // Do not issue StandStill/HandsUp over a document
            // exchange. The task can otherwise be accepted and then replaced on
            // the same interaction tick, preventing validation from completing.
            if (_pendingAnimation != PendingAnimation.None
                || _animationVerificationAt != DateTime.MinValue
                || now < _animationHoldUntil)
                return;

            if (now < _nextSubjectHold)
                return;
            _nextSubjectHold = now.AddMilliseconds(
                _subjectVehicle == null
                    ? SubjectHoldMilliseconds
                    : TrafficCautionMilliseconds);

            if (_subjectVehicle == null)
                HoldFootSubject(player, now);
            else
                HoldTrafficSubject();
        }

        private void PresentBackupCustodyOffer(Ped player)
        {
            if ((_stage != InteractionStage.FootBackupContained
                    && _stage != InteractionStage.TrafficBackupContained)
                || !HasBackupAssignment)
                return;
            if (!IsUsable(_subject) || !IsUsable(player))
            {
                Notify("Backup containment is active, but the citizen is unavailable.");
                return;
            }
            if (_stage == InteractionStage.TrafficBackupContained
                && IsUsable(_subjectVehicle)
                && _subject.IsInVehicle()
                && _subject.CurrentVehicle.Handle == _subjectVehicle.Handle)
            {
                Notify("Backup stopped the fleeing vehicle. Wait for the driver to exit before opening the custody response.");
                return;
            }
            if (_subject.Position.DistanceTo(player.Position) > BackupContainmentInteractionRadius)
            {
                Notify("Approach the contained citizen before opening the Backup custody response.");
                return;
            }
            if (_backupInterceptionInvestigationRequired)
            {
                if (!_documentsProvided)
                {
                    Notify(_stage == InteractionStage.TrafficBackupContained
                        ? "Driver is contained. Request the driver's documents before the Backup custody decision."
                        : "Citizen is contained. Request the citizen's documents before the Backup custody decision.");
                    return;
                }
                if (!_dispatcherValidationReceived || _currentRecord == null)
                {
                    Notify("The document handoff is complete. Request Dispatcher validation before the Backup custody decision.");
                    return;
                }
                if (CitizenRecordVisible || !_citizenRecordReviewed)
                {
                    Notify("Review the Dispatcher validation in the lower-right display, then select Confirm Review before the Backup custody decision.");
                    return;
                }
            }
            if (_backupCustodyOfferPresented)
            {
                Notify("Backup custody response is ready. " + _controls.AcceptKey
                    + " = accept, " + _controls.RejectKey + " = decline.");
                return;
            }

            _backupInterceptionInvestigationRequired = false;
            _backupCustodyOfferPresented = true;
            _log.Runtime("NPC_BACKUP_CUSTODY_OFFER_PRESENTED",
                "Ped=" + _subject.Handle
                + "; Officer=" + (_backupOfficer == null ? 0 : _backupOfficer.Handle)
                + "; Vehicle=" + (_backupVehicle == null ? 0 : _backupVehicle.Handle)
                + "; SubjectType=" + (_stage == InteractionStage.TrafficBackupContained ? "Traffic" : "Foot")
                + (_currentRecord == null ? string.Empty : "; " + RecordStatus("Record"))
                + "; DocumentReviewConfirmed=" + _citizenRecordReviewed);
            if (_currentRecord != null && !_citizenRecordReviewed)
                CitizenRecordVisible = true;
            Notify((_stage == InteractionStage.TrafficBackupContained
                ? "Backup has stopped the fleeing driver. Backup will escort the driver to its Police vehicle. "
                : "Backup has contained the citizen. Backup will escort the suspect to its Police vehicle. ")
                + (!CitizenRecordVisible
                    ? string.Empty
                    : "Citizen record is shown in the lower-right display. ")
                + _controls.AcceptKey + " = accept, " + _controls.RejectKey + " = decline.");
        }

        private void AcceptBackupCustodyOffer()
        {
            if (!_backupCustodyOfferPresented)
            {
                Notify("Press " + _controls.InteractionKey
                    + " first to review the Backup custody response.");
                return;
            }

            DateTime now = DateTime.UtcNow;
            EnableControlledCitizenInjury(_subject, "BackupAcceptedCustody");
            _backupCustodyOfferPresented = false;
            _stage = InteractionStage.BackupSecuring;
            _backupPhaseDeadline = now.AddSeconds(BackupPhysicalEscortTimeoutSeconds);
            _nextBackupSubjectTask = now;
            _backupArrestTaskIssued = false;
            _backupArrestAttempts = 0;
            _backupArrestPlaybackConfirmed = false;
            _log.Runtime("NPC_BACKUP_CUSTODY_OFFER_ACCEPTED",
                "Ped=" + (_subject == null ? 0 : _subject.Handle)
                + "; Officer=" + (_backupOfficer == null ? 0 : _backupOfficer.Handle));
            _log.Runtime("NPC_BACKUP_FLEEING_ARREST_STARTED",
                "Ped=" + (_subject == null ? 0 : _subject.Handle));
            Notify("Backup custody accepted. The Backup officer is securing the contained citizen.");
        }

        private void DeclineBackupCustodyOffer()
        {
            if (!_backupCustodyOfferPresented)
            {
                Notify("No Backup custody response is open. Press "
                    + _controls.InteractionKey + " when you are ready.");
                return;
            }

            _backupCustodyOfferPresented = false;
            _log.Runtime("NPC_BACKUP_CUSTODY_OFFER_DECLINED",
                "Ped=" + (_subject == null ? 0 : _subject.Handle));
            Notify("Backup will maintain containment. Press " + _controls.InteractionKey
                + " again if you want Backup to escort the citizen.");
        }

        private void AcceptInteraction()
        {
            if (!HasActiveInteraction)
            {
                Notify("No NPC interaction is awaiting a decision.");
                return;
            }
            if (!CanDecideCitizenRecord)
            {
                Notify(CitizenDecisionGateMessage("accept"));
                return;
            }
            if (_stage == InteractionStage.FootBackupContained
                || _stage == InteractionStage.TrafficBackupContained)
            {
                AcceptBackupCustodyOffer();
                return;
            }
            if (_backupRequested)
            {
                Notify("Backup already owns the physical handoff. Wait for transport or use Police recovery if it becomes stuck.");
                return;
            }

            if (_stage == InteractionStage.FootPreparing
                || _stage == InteractionStage.FootGreeting
                || _stage == InteractionStage.TrafficPullingOver
                || _stage == InteractionStage.TrafficPreparing)
            {
                Notify("Wait until the subject has finished preparing identification.");
                return;
            }
            if (_stage == InteractionStage.FootFleeing
                || _stage == InteractionStage.FootStationFollow
                || _stage == InteractionStage.TrafficFleeing
                || _stage == InteractionStage.TrafficStationFollow)
            {
                Notify("The subject is already moving. Finish or recover that contact before clearing it.");
                return;
            }
            if (_stage == InteractionStage.FootResisting)
            {
                Notify("The citizen is actively resisting. Use appropriate force or request Backup; do not clear the active threat with a document decision.");
                return;
            }
            if (_stage == InteractionStage.TrafficResisting)
            {
                Notify("The driver is actively resisting. Use appropriate force or request Backup; do not clear the active threat with a document decision.");
                return;
            }

            bool traffic = _subjectVehicle != null;
            bool trafficFlightContainedOnFoot = traffic
                && _trafficContactFled
                && _trafficPursuitStage == TrafficPursuitStage.Contained
                && IsUsable(_subject) && !_subject.IsInVehicle();
            int subjectHandle = _subject == null ? 0 : _subject.Handle;
            ReportInteractionAudio(
                "lsimmersivelife.police.civilian.released", "citizen-released");
            if (traffic && !trafficFlightContainedOnFoot)
                ReleaseTrafficSubject();
            else
                ReleaseFootSubjectNaturally();
            ClearInteractionState();
            _log.Runtime(traffic ? "NPC_TRAFFIC_CONTACT_RELEASED" : "NPC_FOOT_CONTACT_RELEASED", "Ped=" + subjectHandle);
            Notify(trafficFlightContainedOnFoot
                ? "The contained Citizen was accepted and released on foot."
                : traffic ? "Driver cleared and released naturally."
                    : "Citizen contact completed and released naturally.");
        }

        private void RejectInteraction(Ped player)
        {
            if (!HasActiveInteraction)
            {
                Notify("No NPC interaction is awaiting a decision.");
                return;
            }
            if (!CanDecideCitizenRecord)
            {
                Notify(CitizenDecisionGateMessage("reject"));
                return;
            }
            if (_stage == InteractionStage.FootBackupContained
                || _stage == InteractionStage.TrafficBackupContained)
            {
                DeclineBackupCustodyOffer();
                return;
            }
            if (_backupRequested)
            {
                Notify("Backup already owns the physical handoff. Wait for transport or use Police recovery if it becomes stuck.");
                return;
            }
            if (_stage == InteractionStage.FootPreparing
                || _stage == InteractionStage.FootGreeting
                || _stage == InteractionStage.TrafficPullingOver
                || _stage == InteractionStage.TrafficPreparing)
            {
                Notify("Wait until the subject is ready before making a decision.");
                return;
            }

            if (_stage == InteractionStage.FootFleeing
                || _stage == InteractionStage.FootStationFollow
                || _stage == InteractionStage.TrafficFleeing
                || _stage == InteractionStage.TrafficStationFollow)
            {
                Notify("The subject is already moving. Follow until the current contact reaches an outcome.");
                return;
            }
            if (_stage == InteractionStage.FootResisting)
            {
                Notify("The citizen is actively resisting. Use appropriate force or request Backup; the document decision is already complete.");
                return;
            }
            if (_stage == InteractionStage.TrafficResisting)
            {
                Notify("The driver is actively resisting. Use appropriate force or request Backup; the document decision is already complete.");
                return;
            }

            if (_subjectVehicle != null)
            {
                bool trafficFlightContainedOnFoot = _trafficContactFled
                    && _trafficPursuitStage == TrafficPursuitStage.Contained
                    && IsUsable(_subject) && !_subject.IsInVehicle();
                if (_stage == InteractionStage.TrafficRefused)
                {
                    if (trafficFlightContainedOnFoot)
                    {
                        ResumeContainedTrafficFlightOnFoot(player);
                        return;
                    }
                    _log.Runtime("NPC_TRAFFIC_DOCUMENT_BRANCH_CONTINUED",
                        "Driver=" + _subject.Handle
                        + "; Outcome=Flight; DecisionRerolled=false");
                    StartTrafficFlee(player);
                    return;
                }

                int resistanceChance = Math.Max(0, Math.Min(100,
                    _settings.CitizenResistChancePercent));
                int fleeChance = Math.Max(0, Math.Min(
                    100 - resistanceChance,
                    _settings.TrafficFleeChancePercent));
                int roll = _random.Next(100);
                string outcome = roll < resistanceChance
                    ? "Resistance"
                    : roll < resistanceChance + fleeChance
                        ? "Flight" : "Compliance";
                _citizenDocumentDecisionResolved = true;
                _log.Runtime("NPC_TRAFFIC_NEGATIVE_DECISION",
                    "Driver=" + _subject.Handle
                    + "; Roll=" + roll
                    + "; ResistanceChance=" + resistanceChance
                    + "; FleeChance=" + fleeChance
                    + "; DecisionLocked=true; Outcome=" + outcome);

                if (roll < resistanceChance)
                {
                    StartTrafficResistance(player);
                    return;
                }
                if (roll < resistanceChance + fleeChance)
                {
                    StartTrafficFlee(player);
                    return;
                }

                BeginTrafficComplianceDriverExit(DateTime.UtcNow);
                _log.Runtime("NPC_TRAFFIC_COMPLIANT_RESPONSE_STARTED",
                    "Driver=" + _subject.Handle
                    + "; PhysicalExitConfirmed=" + _trafficDriverExitConfirmed
                    + "; PhysicalExitRequested=" + _trafficComplianceExitRequested);
                Notify("Driver complied after the document disagreement and is exiting the stopped vehicle. Secure the same driver after the exit is confirmed, or request Back Up with "
                    + _controls.EmergencyKey + " for physical transport assistance.");
                return;
            }

            if (_stage != InteractionStage.FootRefused)
            {
                int resistanceChance = Math.Max(0, Math.Min(100,
                    _settings.CitizenResistChancePercent));
                int fleeChance = Math.Max(0, Math.Min(
                    100 - resistanceChance,
                    _settings.CitizenFleeChancePercent));
                int roll = _random.Next(100);
                string outcome = roll < resistanceChance
                    ? "Resistance"
                    : roll < resistanceChance + fleeChance
                        ? "Flight" : "Compliance";
                _citizenDocumentDecisionResolved = true;

                _log.Runtime("NPC_FOOT_NEGATIVE_DECISION",
                    "Ped=" + _subject.Handle
                    + "; Roll=" + roll
                    + "; ResistanceChance=" + resistanceChance
                    + "; FleeChance=" + fleeChance
                    + "; DecisionLocked=true; Outcome=" + outcome);

                if (roll < resistanceChance)
                {
                    StartFootResistance(player);
                    return;
                }
                if (roll < resistanceChance + fleeChance)
                {
                    StartFootFlee(player);
                    return;
                }

                StartFootCompliance();
                return;
            }

            // A subject that already fled keeps its same-branch pursue-again
            // choice after physically stopping. This path never draws a new
            // random outcome.
            _citizenDocumentDecisionResolved = true;
            _log.Runtime("NPC_FOOT_DOCUMENT_BRANCH_CONTINUED",
                "Ped=" + _subject.Handle
                + "; Outcome=Flight; DecisionRerolled=false");
            StartFootFlee(player);
        }

        private void StartFootCompliance()
        {
            if (!IsUsable(_subject))
                return;

            EnableControlledCitizenInjury(_subject, "FootCompliance");
            DateTime now = DateTime.UtcNow;
            _stage = InteractionStage.FootAwaitingPlayerArrest;
            _footPlayerArrested = false;
            _subject.Task.ClearAll();
            _subject.Task.HandsUp(30000);
            _readyAt = DateTime.MaxValue;
            _expiresAt = now.AddMinutes(6);
            EnsureSubjectBlip("Compliant Citizen");
            _log.Runtime("NPC_FOOT_COMPLIANCE_STARTED",
                "Ped=" + _subject.Handle
                + "; Behavior=HandsUp; AwaitingPlayerArrest=true");

            if (_random.Next(100) < _settings.CompliantKneelChancePercent)
            {
                BeginPendingAnimation(PendingAnimation.CompliantKneel, 6000, now);
                _log.Runtime("NPC_FOOT_COMPLIANCE_POSE",
                    "Ped=" + _subject.Handle
                    + "; Pose=KneelingHandsUp; Fallback=HandsUp");
            }

            Notify("Citizen complied and raised their hands. Approach the citizen and press "
                + _controls.SecureKey + " to perform the physical arrest.");
        }

        private void StartFootResistance(Ped player)
        {
            if (!IsUsable(_subject) || !IsUsable(player))
            {
                Notify("Citizen resistance could not begin because the subject is unavailable.");
                return;
            }

            EnableControlledCitizenInjury(_subject, "FootResistance");
            DateTime now = DateTime.UtcNow;
            try
            {
                string weaponName = _random.Next(2) == 0
                    ? "WEAPON_KNIFE"
                    : "WEAPON_PISTOL";
                int weaponHash = unchecked((int)StringHash.AtStringHash(weaponName, 0));
                _stage = InteractionStage.FootResisting;
                _expiresAt = now.AddMinutes(6);
                _readyAt = DateTime.MaxValue;
                _nextResistanceTaskAt = now;
                SetSubjectPoliceAware(_subject);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                _subject.Task.ClearAll();
                Function.Call(Hash.GIVE_WEAPON_TO_PED,
                    _subject, weaponHash, weaponName == "WEAPON_KNIFE" ? 1 : 60, true, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, _subject, weaponHash, true);
                Function.Call(Hash.SET_PED_COMBAT_ABILITY, _subject, 1);
                Function.Call(Hash.SET_PED_ACCURACY, _subject, 35);
                _subject.Task.Combat(player);
                EnsureSubjectBlip("Resisting Citizen");
                _log.Runtime("NPC_FOOT_RESISTANCE_STARTED",
                    "Ped=" + _subject.Handle
                    + "; Weapon=" + weaponName
                    + "; Behavior=CombatAgainstOfficer");
                Notify("The citizen resisted and is attacking the officer. Use appropriate force to end the threat.");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_RESISTANCE_START_FAILED", ex);
                StartFootFlee(player);
            }
        }

        private void BeginFootPlayerArrest(Ped player, DateTime now)
        {
            LSPDPlayerControlSuppression.SuppressMeleeThisFrame();
            if (!CanArrestCitizen)
            {
                Notify("This Citizen is not at a documented arrest stage. Complete the interaction decision or wait until the same suspect is physically subdued.");
                return;
            }
            if (IsSubjectMeleeReactionPending(_subject))
            {
                Notify("Wait for the citizen to recover from the physical reaction, then secure the same subject.");
                return;
            }
            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture =
                LSImmersiveDictionaryAnimation.ClassifyArrestTarget(_subject);
            bool downedAlive = IsLivingDowned(_subject)
                || IsLivingInWrithe(_subject);
            if (!IsUsable(player) || !IsUsable(_subject)
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Dead
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Cuffed)
            {
                Notify("The compliant citizen is no longer available for arrest.");
                return;
            }
            bool trafficFlightContainedOnFoot = _subjectVehicle != null
                && _trafficContactFled
                && _trafficPursuitStage == TrafficPursuitStage.Contained
                && IsUsable(_subject) && !_subject.IsInVehicle();
            if (_subjectVehicle != null && !trafficFlightContainedOnFoot
                && !IsSelectedTrafficDriverOutside())
            {
                Notify("The selected driver must physically exit the original stopped vehicle before arrest.");
                return;
            }
            if (_subjectVehicle != null)
                _trafficDriverExitConfirmed = true;
            if (player.IsInVehicle())
            {
                Notify("Exit your vehicle and stand beside the compliant citizen before pressing "
                    + _controls.SecureKey + ".");
                return;
            }
            if (player.Position.DistanceTo(_subject.Position) > FootPlayerArrestInteractionRadius)
            {
                Notify("Move within " + FootPlayerArrestInteractionRadius.ToString("0.0")
                    + " metres of the compliant citizen before pressing "
                    + _controls.SecureKey + ".");
                return;
            }

            try
            {
                _stage = InteractionStage.FootPlayerArresting;
                _readyAt = DateTime.MaxValue;
                _footPlayerArrestPlaybackConfirmed = false;
                _footPlayerArrestAttempts = 0;
                _footPlayerArrestDeadline = now.AddSeconds(30);
                _animationHoldUntil = now;
                SetSubjectPoliceAware(_subject);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                if (downedAlive)
                    Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, _subject, true);

                bool canUsePairedAnimation = posture !=
                        LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                    && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Dead
                    && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Cuffed
                    && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling
                    && !_subject.IsInVehicle();
                string requestedDictionary = posture ==
                        LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded
                    ? "mp_arresting" : "mp_arrest_paired";
                if (canUsePairedAnimation)
                {
                    LSImmersiveDictionaryAnimation.RequestDictionary(
                        requestedDictionary);
                    if (!LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                        requestedDictionary))
                    {
                        _animationHoldUntil = now.AddMilliseconds(250);
                        _log.Runtime("NPC_FOOT_PLAYER_ARREST_ANIMATION_WAITING",
                            "Ped=" + _subject.Handle + "; Player=" + player.Handle
                            + "; Dictionary=" + requestedDictionary
                            + "; DictionaryLoaded=false; FallbackNotStarted=true");
                        Notify("Preparing the physical arrest animation. Stay beside the same citizen.");
                        return;
                    }
                }

                int arrestHoldMilliseconds = FootPlayerArrestMilliseconds;
                int arrestScene = -1;
                float arrestDuration = 0f;
                Vector3 sceneOrigin = Vector3.Zero;
                float sceneHeading = 0f;
                bool groundArrestAnimation = false;
                bool preserveTargetPosture = downedAlive
                    || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling;
                bool pairedArrest = TryStartPairedCustodyArrest(
                    player, _subject, out arrestScene, out arrestDuration,
                    out groundArrestAnimation, out sceneOrigin, out sceneHeading);
                if (!pairedArrest)
                {
                    if (!preserveTargetPosture)
                    {
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            player, _subject, 1000);
                        _subject.Task.ClearAll();
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            _subject, player, 1000);
                    }
                    Function.Call(Hash.TASK_ARREST_PED, player, _subject);
                }
                if (pairedArrest)
                {
                    arrestHoldMilliseconds = Math.Max(FootPlayerArrestMilliseconds,
                        (int)Math.Ceiling(arrestDuration * 1000.0f) + 500);
                    string dictionary = groundArrestAnimation ? "mp_arresting" : "mp_arrest_paired";
                    string officerClip = groundArrestAnimation
                        ? "arrest_on_floor_front_right_a" : "cop_p2_back_right";
                    string subjectClip = groundArrestAnimation
                        ? "arrest_on_floor_front_right_b" : "crook_p2_back_right";
                    SchedulePendingAnimationVerification(
                        now, PendingAnimation.PairedArrest, dictionary,
                        subjectClip, _subject, officerClip, player, arrestScene);
                }
                _footPlayerArrestAttempts = 1;
                _footPlayerArrestDeadline = now.AddSeconds(30);
                _animationHoldUntil = now.AddMilliseconds(arrestHoldMilliseconds);
                _log.Runtime("NPC_FOOT_PLAYER_ARREST_STARTED",
                    "Ped=" + _subject.Handle + "; Player=" + player.Handle
                    + "; Method=" + (pairedArrest
                        ? groundArrestAnimation
                            ? "SynchronizedGroundArrest"
                            : "SynchronizedPairedAnimation"
                        : downedAlive ? "GroundedTaskArrestPed" : "TaskArrestPedFallback")
                    + "; SynchronizedScene=" + pairedArrest
                    + "; Scene=" + (pairedArrest ? arrestScene.ToString() : "none")
                    + "; ClipDuration=" + arrestDuration.ToString("0.000")
                    + "; Posture=" + posture
                    + "; Dictionary=" + (pairedArrest
                        ? groundArrestAnimation ? "mp_arresting" : "mp_arrest_paired"
                        : "none")
                    + "; OfficerClip=" + (pairedArrest
                        ? groundArrestAnimation
                            ? "arrest_on_floor_front_right_a" : "cop_p2_back_right"
                        : "TASK_ARREST_PED")
                    + "; SubjectClip=" + (pairedArrest
                        ? groundArrestAnimation
                            ? "arrest_on_floor_front_right_b" : "crook_p2_back_right"
                        : "TASK_ARREST_PED")
                    + "; SceneOrigin=" + sceneOrigin.X.ToString("0.00") + ","
                        + sceneOrigin.Y.ToString("0.00") + ","
                        + sceneOrigin.Z.ToString("0.00")
                    + "; SceneHeading=" + sceneHeading.ToString("0.0")
                    + "; ScenePhase=" + GetScenePhaseText(arrestScene)
                    + "; HoldMilliseconds=" + arrestHoldMilliseconds
                    + "; Attempt=1"
                    + "; DownedAlive=" + downedAlive);
                Notify("Physical arrest in progress. Stay beside the citizen until the handcuffs are secured.");
            }
            catch (Exception ex)
            {
                _stage = _subjectVehicle == null
                    ? InteractionStage.FootAwaitingPlayerArrest
                    : InteractionStage.TrafficAwaitingBackup;
                _animationHoldUntil = DateTime.MinValue;
                _footPlayerArrestAttempts = 0;
                _footPlayerArrestDeadline = DateTime.MinValue;
                _footPlayerArrestPlaybackConfirmed = false;
                _log.Exception("NPC_FOOT_PLAYER_ARREST_START_FAILED", ex);
                if (!downedAlive
                    && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling)
                {
                    try { _subject.Task.HandsUp(15000); } catch { }
                }
                Notify("The physical arrest could not be started. Try again while standing beside the citizen.");
            }
        }

        private void MaintainFootPlayerArrest(DateTime now, Ped player)
        {
            if (!IsUsable(_subject) || _subject.IsDead || _subject.Health <= 0)
                return;
            if (now < _animationHoldUntil)
                return;

            bool cuffed = IsPedPhysicallyCuffed(_subject);
            if (!cuffed && _footPlayerArrestPlaybackConfirmed)
            {
                cuffed = TryApplyPhysicalHandcuffs(_subject);
                if (cuffed)
                    _log.Runtime("NPC_FOOT_PLAYER_HANDCUFF_STATE_COMMITTED",
                        "Ped=" + _subject.Handle + "; RuntimeOwner=Citizen"
                        + "; ConfigFlag=" + HandcuffedPedConfigFlag
                        + "; Cuffed=" + IsPedPhysicallyCuffed(_subject)
                        + "; PairedPlaybackConfirmed=true"
                        + "; PhysicalHoldElapsed=true; SamePed=true");
            }
            if (!cuffed)
            {
                bool playerCanContinue = IsUsable(player)
                    && !player.IsInVehicle()
                    && player.Position.DistanceTo(_subject.Position)
                        <= FootPlayerArrestInteractionRadius + 1.5f;
                if (playerCanContinue
                    && _footPlayerArrestAttempts < FootPlayerArrestMaximumAttempts
                    && now < _footPlayerArrestDeadline)
                {
                    try
                    {
                        bool downedAlive = IsLivingDowned(_subject)
                            || IsLivingInjuredSubject(_subject);
                        LSImmersiveDictionaryAnimation.ArrestTargetPosture posture =
                            LSImmersiveDictionaryAnimation.ClassifyArrestTarget(_subject);
                        int arrestScene = -1;
                        float arrestDuration = 0f;
                        Vector3 sceneOrigin = Vector3.Zero;
                        float sceneHeading = 0f;
                        bool groundArrestAnimation = false;
                        bool preserveTargetPosture = downedAlive
                            || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling;
                        bool canUsePairedAnimation = posture !=
                                LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                            && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Dead
                            && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Cuffed
                            && posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling
                            && !_subject.IsInVehicle();
                        string requestedDictionary = posture ==
                                LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded
                            ? "mp_arresting" : "mp_arrest_paired";
                        if (canUsePairedAnimation)
                        {
                            LSImmersiveDictionaryAnimation.RequestDictionary(
                                requestedDictionary);
                            if (!LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                                requestedDictionary))
                            {
                                _animationHoldUntil = now.AddMilliseconds(250);
                                return;
                            }
                        }
                        _footPlayerArrestPlaybackConfirmed = false;
                        bool pairedArrest = TryStartPairedCustodyArrest(
                            player, _subject, out arrestScene, out arrestDuration,
                            out groundArrestAnimation, out sceneOrigin, out sceneHeading);
                        if (!pairedArrest)
                        {
                            if (!preserveTargetPosture)
                            {
                                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                    player, _subject, 1000);
                                _subject.Task.ClearAll();
                                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                    _subject, player, 1000);
                            }
                                Function.Call(Hash.TASK_ARREST_PED, player, _subject);
                        }
                        else
                        {
                            string dictionary = groundArrestAnimation
                                ? "mp_arresting" : "mp_arrest_paired";
                            string officerClip = groundArrestAnimation
                                ? "arrest_on_floor_front_right_a" : "cop_p2_back_right";
                            string subjectClip = groundArrestAnimation
                                ? "arrest_on_floor_front_right_b" : "crook_p2_back_right";
                            SchedulePendingAnimationVerification(
                                now, PendingAnimation.PairedArrest, dictionary,
                                subjectClip, _subject, officerClip, player, arrestScene);
                        }
                        _footPlayerArrestAttempts++;
                        int arrestHoldMilliseconds = pairedArrest
                            ? Math.Max(FootPlayerArrestMilliseconds,
                                (int)Math.Ceiling(arrestDuration * 1000.0f) + 500)
                            : FootPlayerArrestMilliseconds;
                        _animationHoldUntil = now.AddMilliseconds(arrestHoldMilliseconds);
                        _log.Runtime("NPC_FOOT_PLAYER_ARREST_RETRIED",
                            "Ped=" + _subject.Handle + "; Player=" + player.Handle
                            + "; Attempt=" + _footPlayerArrestAttempts
                            + "; MaximumAttempts=" + FootPlayerArrestMaximumAttempts
                            + "; DownedAlive=" + downedAlive
                            + "; Posture=" + posture
                            + "; Method=" + (pairedArrest
                            ? groundArrestAnimation
                                ? "SynchronizedGroundArrest"
                                : "SynchronizedPairedAnimation"
                                : downedAlive ? "GroundedTaskArrestPed" : "TaskArrestPedFallback")
                            + "; Scene=" + (pairedArrest ? arrestScene.ToString() : "none")
                            + "; ClipDuration=" + arrestDuration.ToString("0.000")
                            + "; Dictionary=" + (pairedArrest
                                ? groundArrestAnimation ? "mp_arresting" : "mp_arrest_paired"
                                : "none")
                            + "; OfficerClip=" + (pairedArrest
                                ? groundArrestAnimation
                                    ? "arrest_on_floor_front_right_a" : "cop_p2_back_right"
                                : "TASK_ARREST_PED")
                            + "; SubjectClip=" + (pairedArrest
                                ? groundArrestAnimation
                                    ? "arrest_on_floor_front_right_b" : "crook_p2_back_right"
                                : "TASK_ARREST_PED")
                            + "; SceneOrigin=" + sceneOrigin.X.ToString("0.00") + ","
                                + sceneOrigin.Y.ToString("0.00") + ","
                                + sceneOrigin.Z.ToString("0.00")
                            + "; SceneHeading=" + sceneHeading.ToString("0.0")
                            + "; ScenePhase=" + GetScenePhaseText(arrestScene));
                        Notify("Stay beside the citizen while the physical handcuffing finishes.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log.Exception("NPC_FOOT_PLAYER_ARREST_RETRY_FAILED", ex);
                    }
                }

                _stage = _subjectVehicle == null
                    ? InteractionStage.FootAwaitingPlayerArrest
                    : InteractionStage.TrafficAwaitingBackup;
                _animationHoldUntil = DateTime.MinValue;
                _readyAt = DateTime.MaxValue;
                _footPlayerArrestAttempts = 0;
                _footPlayerArrestDeadline = DateTime.MinValue;
                _footPlayerArrestPlaybackConfirmed = false;
                if (!IsLivingDowned(_subject)
                    && !IsLivingInjuredSubject(_subject))
                {
                    try { _subject.Task.HandsUp(15000); } catch { }
                }
                _log.StateFailure("NPC_FOOT_PLAYER_ARREST_NOT_CONFIRMED",
                    "Ped=" + _subject.Handle
                    + "; Cuffed=" + IsPedPhysicallyCuffed(_subject)
                    + "; PlayerAvailable=" + playerCanContinue);
                Notify("The game did not confirm the handcuffs. The citizen remains available; move beside them and press "
                    + _controls.SecureKey + " to retry.");
                return;
            }

            try
            {
                _subject.Task.ClearAll();
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, true);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                _footPlayerArrested = true;
                _footPlayerArrestAttempts = 0;
                _footPlayerArrestDeadline = DateTime.MinValue;
                _footPlayerArrestPlaybackConfirmed = false;
                _stage = InteractionStage.FootAwaitingBackup;
                _readyAt = DateTime.MaxValue;
                _expiresAt = now.AddMinutes(6);
                EnsureSubjectBlip("Handcuffed Citizen");
                BeginPendingAnimation(PendingAnimation.HandcuffedIdle, 1800, now);
                _log.Runtime("NPC_FOOT_PLAYER_ARREST_COMPLETED",
                    "Ped=" + _subject.Handle + "; Method=TaskArrestPed");
                Notify("Citizen is handcuffed and secured. Open Citizen Ped Response and select "
                    + "Request a Back Up For Citizen Transport for a nearby Backup escort.");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_PLAYER_ARREST_CONFIRM_FAILED", ex);
                _stage = _subjectVehicle == null
                    ? InteractionStage.FootAwaitingPlayerArrest
                    : InteractionStage.TrafficAwaitingBackup;
                _footPlayerArrested = false;
                _footPlayerArrestAttempts = 0;
                _footPlayerArrestDeadline = DateTime.MinValue;
                _footPlayerArrestPlaybackConfirmed = false;
                _animationHoldUntil = DateTime.MinValue;
                try { _subject.Task.HandsUp(15000); } catch { }
                Notify("The handcuff state was not confirmed. The citizen remains compliant; press "
                    + _controls.SecureKey + " to try the arrest again.");
            }
        }

        private void HoldFootSubject(Ped player, DateTime now)
        {
            if (!IsUsable(_subject))
                return;
            try
            {
                if (now < _animationHoldUntil
                    || _pendingAnimation == PendingAnimation.Documents)
                    return;
                if (_stage == InteractionStage.FootAwaitingPlayerArrest)
                {
                    if (Math.Abs(_subject.Speed) > 0.35f)
                        _subject.Task.HandsUp(8000);
                }
                else if (_stage == InteractionStage.FootAwaitingBackup
                    && _footPlayerArrested)
                {
                    MaintainFootArrestedSubject();
                }
                else if (_stage == InteractionStage.FootRefused
                    || _stage == InteractionStage.FootAwaitingBackup
                    || _stage == InteractionStage.FootBackupContained)
                {
                    if (Math.Abs(_subject.Speed) > 0.35f)
                        _subject.Task.HandsUp(8000);
                }
                else if (_subject.Position.DistanceTo(_interactionAnchor) > 1.75f
                    || Math.Abs(_subject.Speed) > 0.45f)
                {
                    _interactionAnchor = _subject.Position;
                    _subject.Task.StandStill(5000);
                    OrientSubjectToOfficer(_subject, player, 1400);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_HOLD_FAILED", ex);
            }
        }

        private void MaintainFootArrestedSubject()
        {
            if (!IsUsable(_subject) || _subject.IsDead || _subject.Health <= 0)
                return;
            try
            {
                SetSubjectPoliceAware(_subject);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, true);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                if (Math.Abs(_subject.Speed) > 0.45f)
                    _subject.Task.StandStill(5000);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_CUFFED_SUBJECT_MAINTENANCE_FAILED", ex);
            }
        }

        private void BeginTrafficComplianceDriverExit(DateTime now)
        {
            EnableControlledCitizenInjury(_subject, "TrafficCompliance");
            _stage = InteractionStage.TrafficAwaitingBackup;
            _readyAt = DateTime.MaxValue;
            _expiresAt = now.AddMinutes(6);
            _trafficComplianceExitRequested = IsUsable(_subject)
                && IsUsable(_subjectVehicle)
                && _trafficStopConfirmed
                && IsSelectedTrafficDriverStillSeated();
            _trafficDriverExitConfirmed = false;
            _trafficDriverExitAttempts = 0;
            _trafficDriverExitDeadline = now.AddSeconds(12);
            _nextTrafficDriverExitAttemptAt = now;
            if (!_trafficComplianceExitRequested)
            {
                _log.Runtime("NPC_TRAFFIC_COMPLIANCE_EXIT_NOT_STARTED",
                    "Driver=" + (_subject == null ? 0 : _subject.Handle)
                    + "; Vehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                    + "; StopConfirmed=" + _trafficStopConfirmed
                    + "; SameDriverSeated=" + IsSelectedTrafficDriverStillSeated());
                return;
            }

            EnsureSubjectBlip("Compliant Driver");
            MaintainTrafficComplianceExit(now);
        }

        private void MaintainTrafficComplianceExit(DateTime now)
        {
            if (_backupRequested || !_trafficComplianceExitRequested)
                return;
            if (now < _nextTrafficDriverExitCheckAt)
            {
                HoldTrafficSubject();
                return;
            }
            _nextTrafficDriverExitCheckAt = now.AddMilliseconds(200);
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle)
                || !IsTrafficStopAtSelectedRoadside())
            {
                _trafficComplianceExitRequested = false;
                _log.Runtime("NPC_TRAFFIC_COMPLIANCE_EXIT_NOT_CONFIRMED",
                    "Driver=" + (_subject == null ? 0 : _subject.Handle)
                    + "; Vehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                    + "; Reason=SelectedRoadsideStopLost");
                Notify("The selected roadside stop was lost. No arrest was started; request Back Up if the driver is not safely available.");
                return;
            }

            if (!_subject.IsInVehicle() && IsSelectedTrafficDriverOutside())
            {
                _trafficComplianceExitRequested = false;
                _trafficDriverExitConfirmed = true;
                _trafficDriverExitDeadline = DateTime.MinValue;
                _nextTrafficDriverExitCheckAt = DateTime.MinValue;
                _nextTrafficDriverExitAttemptAt = DateTime.MinValue;
                SetSubjectPoliceAware(_subject);
                if (!IsLivingDowned(_subject) && !IsLivingInWrithe(_subject)
                    && !IsLivingInjuredSubject(_subject))
                {
                    try { _subject.Task.HandsUp(30000); }
                    catch (Exception ex) { _log.Exception("NPC_TRAFFIC_COMPLIANCE_HANDSUP_FAILED", ex); }
                }
                _log.Runtime("NPC_TRAFFIC_DRIVER_EXIT_CONFIRMED",
                    "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                    + "; SameDriver=true; OutsideSelectedVehicle=true; NearVehicle=true"
                    + "; PhysicalExitConfirmed=true");
                Notify("The selected driver physically exited the stopped vehicle. You may now perform the arrest or request Back Up.");
                return;
            }

            Vehicle currentVehicle = _subject.CurrentVehicle;
            bool sameDriverStillSeated = IsUsable(currentVehicle)
                && currentVehicle.Handle == _subjectVehicle.Handle
                && IsDriver(_subject, _subjectVehicle);
            if (!sameDriverStillSeated)
            {
                _trafficComplianceExitRequested = false;
                _log.Runtime("NPC_TRAFFIC_DRIVER_EXIT_NOT_CONFIRMED",
                    "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                    + "; Reason=SelectedDriverOrVehicleChanged");
                Notify("The selected driver did not exit the original stopped vehicle. Arrest remains unavailable; request Back Up if needed.");
                return;
            }

            HoldTrafficSubject();
            if (_trafficDriverExitAttempts < 2
                && now >= _nextTrafficDriverExitAttemptAt)
            {
                try
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE,
                        _subject, _subjectVehicle, 0);
                    _trafficDriverExitAttempts++;
                    _nextTrafficDriverExitAttemptAt = now.AddSeconds(4);
                    _log.Runtime("NPC_TRAFFIC_DRIVER_EXIT_REQUESTED",
                        "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                        + "; Attempt=" + _trafficDriverExitAttempts
                        + "; PhysicalExitConfirmed=false");
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_TRAFFIC_DRIVER_EXIT_REQUEST_FAILED", ex);
                }
            }

            if (now >= _trafficDriverExitDeadline)
            {
                _trafficComplianceExitRequested = false;
                _trafficDriverExitDeadline = DateTime.MinValue;
                _log.Runtime("NPC_TRAFFIC_DRIVER_EXIT_NOT_CONFIRMED",
                    "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                    + "; Attempts=" + _trafficDriverExitAttempts
                    + "; PhysicalExitConfirmed=false");
                Notify("The driver exit was not physically confirmed. No arrest was started; request Back Up for assistance.");
            }
        }

        private void HoldTrafficSubject()
        {
            HoldTrafficSubject(_subjectVehicle);
        }

        private void HoldTrafficSubject(Vehicle vehicle)
        {
            if (!IsUsable(vehicle))
                return;
            DateTime now = DateTime.UtcNow;
            if (now < _nextTrafficHoldAt)
                return;
            _nextTrafficHoldAt = now.AddMilliseconds(250);
            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, true);
                Function.Call(Hash.SET_VEHICLE_BRAKE, vehicle, true);
                if (Math.Abs(vehicle.Speed) > 0.25f
                    && now >= _nextTrafficBrakeTaskAt)
                {
                    // A bounded brake task may slow this selected vehicle.
                    // Never write entity velocity or report a stop until the
                    // driver and vehicle satisfy the physical stop checks.
                    Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION,
                        _subject, vehicle, 27, 1200);
                    _nextTrafficBrakeTaskAt = now.AddMilliseconds(1200);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_HOLD_FAILED", ex);
            }
        }

        private void HoldTrafficBackupContained(Ped player, DateTime now)
        {
            if (!IsUsable(_subject))
                return;
            try
            {
                SetSubjectPoliceAware(_subject);
                Vehicle currentVehicle = _subject.CurrentVehicle;
                if (IsUsable(currentVehicle))
                {
                    // The fleeing driver must leave the original vehicle before
                    // the player is offered custody. Preserve the actual car
                    // the selected Citizen currently occupies.
                    HoldTrafficSubject(currentVehicle);
                    return;
                }

                if (now < _animationHoldUntil
                    || _pendingAnimation == PendingAnimation.Documents)
                    return;
                if (now < _nextSubjectHold)
                    return;
                _nextSubjectHold = now.AddSeconds(6);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                _subject.Task.HandsUp(15000);
                if (IsUsable(player))
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        _subject, player, 1200);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_BACKUP_CONTAINMENT_HOLD_FAILED", ex);
            }
        }

        private void StartFootFlee(Ped player)
        {
            if (!IsUsable(_subject) || !IsUsable(player))
            {
                ReleaseSubject(true);
                ClearInteractionState();
                Notify("Citizen interaction ended because the subject is unavailable.");
                return;
            }

            EnableControlledCitizenInjury(_subject, "FootFlight");
            DateTime now = DateTime.UtcNow;
            try
            {
                ReleaseSubjectInteractionLockForMovement(_subject);
                _subject.Task.ClearAll();
                Function.Call(Hash.TASK_SMART_FLEE_PED, _subject, player, 70.0f, -1, false, false);
                _footContactFled = true;
                _stage = InteractionStage.FootFleeing;
                _fleeStartedAt = now;
                _lastFleeSampleAt = DateTime.MinValue;
                _nextFleeTaskAt = now.AddSeconds(_settings.TrafficTaskRecoverySeconds);
                _fleeStoppedAt = DateTime.MinValue;
                _fleeLastPosition = _subject.Position;
                _fleeMovementConfirmed = false;
                _fleeRecoveryIssued = false;
                _log.Runtime("NPC_FOOT_FLEE_ATTEMPT", "Ped=" + _subject.Handle);
                EnsureSubjectBlip("Fleeing Citizen");
                Notify("Citizen did not comply and is attempting to leave the contact.");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_FLEE_FAILED", ex);
                SetSubjectPoliceAware(_subject);
                _stage = InteractionStage.FootRefused;
                _subject.Task.HandsUp(5000);
                Notify("Citizen could not leave and remains at the contact.");
            }
        }

        private void StartTrafficFlee(Ped player)
        {
            Ped driver = _subject;
            Vehicle vehicle = _subjectVehicle;
            if (!IsUsable(driver) || !IsUsable(vehicle))
            {
                ClearInteractionState();
                Notify("Traffic interaction ended because the driver is unavailable.");
                return;
            }

            EnableControlledCitizenInjury(driver, "TrafficFlight");
            try
            {
                DateTime now = DateTime.UtcNow;
                ReleaseSubjectInteractionLockForMovement(driver);
                _stage = InteractionStage.TrafficFleeing;
                _trafficPursuitVehicle = vehicle;
                _trafficPursuitStage = TrafficPursuitStage.VehicleFlight;
                _trafficVehicleTransitionStartedAt = DateTime.MinValue;
                _trafficSubjectLostSince = DateTime.MinValue;
                _nextTrafficBackupPursuitSampleAt = DateTime.MinValue;
                _nextTrafficPursuitExitAt = DateTime.MinValue;
                _trafficPursuitExitAttempts = 0;
                _trafficPursuitExitFailureNotified = false;
                _trafficContactFled = true;
                _fleeStartedAt = now;
                _lastFleeSampleAt = DateTime.MinValue;
                _nextFleeTaskAt = now;
                _fleeStoppedAt = DateTime.MinValue;
                _fleeLastPosition = vehicle.Position;
                _fleeMovementConfirmed = false;
                _fleeRecoveryIssued = false;
                _recentlyReleasedVehicles[vehicle.Handle] = now.AddMilliseconds(FleeMemoryMilliseconds);
                LSPDNPCRecord adverseRecord;
                if (_currentRecord == null && _database != null
                    && _database.TryGetAdverseRecord(driver, true, out adverseRecord))
                {
                    _currentRecord = adverseRecord;
                    _log.Runtime("NPC_TRAFFIC_FLEE_RECORD_READY",
                        "Driver=" + driver.Handle + "; " + RecordStatus("Database adverse record selected"));
                }
                else
                {
                    _log.Runtime("NPC_TRAFFIC_FLEE_RECORD_UNAVAILABLE",
                        "Driver=" + driver.Handle + "; Reason=NPC database could not provide an adverse record");
                }
                IssueTrafficFleeTask(player, now, 28.0f);
                _log.Runtime("NPC_TRAFFIC_FLEE_ATTEMPT", "Driver=" + driver.Handle + "; Vehicle=" + vehicle.Handle);
                _trafficRefusalAudioSequence++;
                ReportInteractionAudio(
                    "lsimmersivelife.police.traffic.refusing",
                    "traffic-refusal-" + _trafficRefusalAudioSequence);
                EnsureSubjectBlip("Fleeing Driver");
                Notify("Driver refused the stop and is attempting to leave.");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_FLEE_FAILED", ex);
                SetSubjectPoliceAware(driver);
                _stage = InteractionStage.TrafficRefused;
                HoldTrafficSubject();
                Notify("The driver could not leave and remains stopped for contact.");
            }
        }

        private void ResumeContainedTrafficFlightOnFoot(Ped player)
        {
            if (!IsUsable(_subject) || _subject.IsDead || _subject.Health <= 0
                || _subject.IsInVehicle() || !IsUsable(player))
            {
                Notify("The contained Citizen is not available to continue the same flight branch.");
                return;
            }

            DateTime now = DateTime.UtcNow;
            ReleaseSubjectInteractionLockForMovement(_subject);
            _stage = InteractionStage.TrafficFleeing;
            _trafficPursuitStage = TrafficPursuitStage.OnFootFlight;
            _trafficPursuitVehicle = null;
            _trafficVehicleTransitionStartedAt = DateTime.MinValue;
            _fleeStartedAt = now;
            _lastFleeSampleAt = DateTime.MinValue;
            _nextFleeTaskAt = now;
            _fleeStoppedAt = DateTime.MinValue;
            _fleeLastPosition = _subject.Position;
            _fleeMovementConfirmed = false;
            _fleeRecoveryIssued = false;
            _trafficPlayerAimStartedAt = DateTime.MinValue;
            _trafficPlayerAimSubjectHandle = 0;
            _expiresAt = now.AddMinutes(6);
            SetSubjectPoliceAware(_subject);
            IssueFootFleeTask(player, now);
            EnsureSubjectBlip("Fleeing Driver");
            _log.Runtime("NPC_TRAFFIC_DOCUMENT_BRANCH_CONTINUED",
                "Driver=" + _subject.Handle
                + "; Outcome=Flight; DecisionRerolled=false"
                + "; Mode=OnFoot; SameCitizen=true"
                + "; RecordPreserved=" + (_currentRecord != null)
                + "; ControlledInjuryPreserved=" + _subjectInjuries.ContainsKey(_subject.Handle));
            Notify("The same Citizen continued the flight response on foot. Pursue until they stop or yield.");
        }

        private void StartTrafficResistance(Ped player)
        {
            Ped driver = _subject;
            Vehicle vehicle = _subjectVehicle;
            if (!IsUsable(driver) || !IsUsable(player))
            {
                Notify("Driver resistance could not begin because the subject is unavailable.");
                return;
            }

            EnableControlledCitizenInjury(driver, "TrafficResistance");
            DateTime now = DateTime.UtcNow;
            try
            {
                string weaponName = _random.Next(2) == 0
                    ? "WEAPON_KNIFE"
                    : "WEAPON_PISTOL";
                int weaponHash = unchecked((int)StringHash.AtStringHash(weaponName, 0));
                _stage = InteractionStage.TrafficResisting;
                _expiresAt = now.AddMinutes(6);
                _readyAt = DateTime.MaxValue;
                _nextResistanceTaskAt = now;
                SetSubjectPoliceAware(driver);
                driver.BlockPermanentEvents = true;
                driver.CanSwitchWeapons = false;
                driver.Task.ClearAll();
                Function.Call(Hash.GIVE_WEAPON_TO_PED,
                    driver, weaponHash, weaponName == "WEAPON_KNIFE" ? 1 : 60, true, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, driver, weaponHash, true);
                Function.Call(Hash.SET_PED_COMBAT_ABILITY, driver, 1);
                Function.Call(Hash.SET_PED_ACCURACY, driver, 35);

                if (IsUsable(vehicle) && driver.IsInVehicle()
                    && driver.CurrentVehicle != null
                    && driver.CurrentVehicle.Exists()
                    && driver.CurrentVehicle.Handle == vehicle.Handle)
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, true);
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, driver, vehicle, 0);
                }
                else
                    driver.Task.Combat(player);

                EnsureSubjectBlip("Resisting Driver");
                _log.Runtime("NPC_TRAFFIC_RESISTANCE_STARTED",
                    "Driver=" + driver.Handle
                    + "; Vehicle=" + (IsUsable(vehicle) ? vehicle.Handle.ToString() : "0")
                    + "; Weapon=" + weaponName
                    + "; Behavior=CombatAgainstOfficer");
                _trafficRefusalAudioSequence++;
                ReportInteractionAudio(
                    "lsimmersivelife.police.traffic.refusing",
                    "traffic-refusal-" + _trafficRefusalAudioSequence);
                Notify("The driver resisted and is attacking the officer. Use appropriate force to end the threat or request Backup.");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_RESISTANCE_START_FAILED", ex);
                StartTrafficFlee(player);
            }
        }

        private void ReleaseTrafficSubject()
        {
            Ped driver = _subject;
            Vehicle vehicle = _subjectVehicle;
            if (!IsUsable(driver) || !IsUsable(vehicle))
            {
                ReleaseSubject(true);
                return;
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, false);
                Function.Call(Hash.SET_VEHICLE_BRAKE, vehicle, false);
                ReleaseSubjectPoliceAwareness(driver, false);
                driver.Task.ClearAll();
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, driver, vehicle, 14.0f, TrafficDrivingStyle);
                _recentlyReleasedVehicles[vehicle.Handle] = DateTime.UtcNow.AddMilliseconds(FleeMemoryMilliseconds);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_RELEASE_FAILED", ex);
                ReleaseSubject(true);
            }
        }

        private void MaintainFootFlee(Ped player, DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(player))
            {
                ReleaseSubject(true);
                ClearInteractionState();
                Notify("Citizen pursuit ended because the subject is unavailable.");
                return;
            }

            if (now >= _lastFleeSampleAt.AddSeconds(1))
            {
                float moved = _subject.Position.DistanceTo(_fleeLastPosition);
                _fleeLastPosition = _subject.Position;
                _lastFleeSampleAt = now;
                if (!_fleeMovementConfirmed && moved >= 1.5f)
                {
                    _fleeMovementConfirmed = true;
                    _log.Runtime("NPC_FOOT_FLEE_MOVEMENT_CONFIRMED", "Ped=" + _subject.Handle);
                    ReportInteractionAudio(
                        "lsimmersivelife.police.civilian.refusing", "foot-refusal-confirmed");
                    ReportInteractionAudio(
                        "lsimmersivelife.police.civilian.fleeing", "foot-flee-movement-confirmed");
                    Notify("Citizen fled from Police contact. Pursue on foot until the person stops or is down.");
                }
                if (_fleeMovementConfirmed && moved < 0.35f)
                {
                    if (_fleeStoppedAt == DateTime.MinValue)
                        _fleeStoppedAt = now;
                }
                else
                    _fleeStoppedAt = DateTime.MinValue;
            }

            if (!_fleeMovementConfirmed && now >= _nextFleeTaskAt)
            {
                if (!_fleeRecoveryIssued)
                {
                    _fleeRecoveryIssued = true;
                    IssueFootFleeTask(player, now);
                }
                if (now >= _fleeStartedAt.AddSeconds(_settings.TrafficTaskRecoverySeconds + 4))
                {
                    SetSubjectPoliceAware(_subject);
                    _stage = _backupRequested
                        ? InteractionStage.FootAwaitingBackup
                        : InteractionStage.FootRefused;
                    _subject.Task.HandsUp(_backupRequested ? 30000 : 5000);
                    Notify(_backupRequested
                        ? "The fleeing citizen stopped and is waiting for the arriving Backup unit."
                        : "Citizen did not get away and remains at the contact.");
                }
                return;
            }

            if (_fleeMovementConfirmed && _fleeStoppedAt != DateTime.MinValue
                && now >= _fleeStoppedAt.AddSeconds(2))
            {
                if (_backupRequested && _stage == InteractionStage.FootFleeing)
                {
                    // A fleeing subject must remain mobile while Backup is
                    // still travelling. The old transition forced HandsUp as
                    // soon as the flee task paused, which made the citizen
                    // surrender before the assigned unit could intercept.
                    IssueFootFleeTask(player, now);
                    _fleeStoppedAt = DateTime.MinValue;
                    _fleeLastPosition = _subject.Position;
                    _lastFleeSampleAt = now;
                    _nextFleeTaskAt = now.AddSeconds(2);
                    _log.Runtime("NPC_FOOT_FLEE_CONTINUED_FOR_BACKUP",
                        "Ped=" + _subject.Handle);
                    return;
                }
                SetSubjectPoliceAware(_subject);
                _stage = _backupRequested
                    ? InteractionStage.FootAwaitingBackup
                    : InteractionStage.FootRefused;
                _subject.Task.HandsUp(_backupRequested ? 30000 : 5000);
                Notify(_backupRequested
                    ? "The fleeing citizen stopped and is waiting for the arriving Backup unit."
                    : "The fleeing citizen stopped. " + DecisionHint("clear", "pursue again"));
            }
        }

        private void MaintainFootResistance(Ped player, DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(player) || _subject.IsDead)
                return;
            if (IsLivingDowned(_subject))
                return;
            if (now < _nextResistanceTaskAt)
                return;

            _nextResistanceTaskAt = now.AddMilliseconds(FootResistanceTaskRecoveryMilliseconds);
            try
            {
                SetSubjectPoliceAware(_subject);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                bool attacking = _subject.IsInCombatAgainst(player) || _subject.IsShooting;
                if (attacking && !_footAggressionAudioReported)
                {
                    _footAggressionAudioReported = true;
                    ReportInteractionAudio(
                        "lsimmersivelife.police.civilian.refusing", "foot-refusal-confirmed");
                    ReportInteractionAudio(
                        "lsimmersivelife.police.civilian.escalated", "foot-aggression-confirmed");
                }
                if (!attacking)
                {
                    _subject.Task.Combat(player);
                    ObserveDeveloperTask(
                        "TASK_COMBAT",
                        _subject.Handle,
                        player.Handle,
                        _subjectVehicle == null ? 0 : _subjectVehicle.Handle,
                        "FootResistance");
                    _log.RuntimeThrottled(
                        "NPC_FOOT_RESISTANCE_TASK_REFRESHED",
                        "Ped=" + _subject.Handle + "; Player=" + player.Handle,
                        TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_RESISTANCE_TASK_FAILED", ex);
            }
        }

        private bool IsFootDeadSceneStage(InteractionStage stage)
        {
            return stage == InteractionStage.FootPreparing
                || stage == InteractionStage.FootGreeting
                || stage == InteractionStage.FootDocuments
                || stage == InteractionStage.FootRefused
                || stage == InteractionStage.FootStationFollow
                || stage == InteractionStage.FootFleeing
                || stage == InteractionStage.FootResisting
                || stage == InteractionStage.FootAwaitingBackup
                || stage == InteractionStage.FootAwaitingPlayerArrest
                || stage == InteractionStage.FootPlayerArresting;
        }

        private bool IsTrafficDeadSceneStage(InteractionStage stage)
        {
            return stage == InteractionStage.TrafficPullingOver
                || stage == InteractionStage.TrafficPreparing
                || stage == InteractionStage.TrafficDocuments
                || stage == InteractionStage.TrafficRefused
                || stage == InteractionStage.TrafficFleeing
                || stage == InteractionStage.TrafficResisting
                || stage == InteractionStage.TrafficAwaitingBackup
                || stage == InteractionStage.TrafficBackupContained;
        }

        private static bool IsLivingDowned(Ped subject)
        {
            if (!IsUsable(subject) || subject.IsDead || subject.Health <= 0)
                return false;
            try
            {
                return Function.Call<bool>(Hash.IS_PED_RAGDOLL, subject);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLivingInWrithe(Ped subject)
        {
            if (!IsUsable(subject) || subject.IsDead || subject.Health <= 0)
                return false;
            try { return Function.Call<bool>(Hash.IS_PED_IN_WRITHE, subject); }
            catch { return false; }
        }

        private bool IsLivingInjuredSubject(Ped subject)
        {
            if (!IsUsable(subject) || subject.IsDead || subject.Health <= 0)
                return false;
            SubjectInjuryState state;
            return _subjectInjuries.TryGetValue(subject.Handle, out state)
                && state.Wounded && !state.Fatal;
        }

        private static bool IsCitizenPresentationStage(InteractionStage stage)
        {
            return stage == InteractionStage.FootPreparing
                || stage == InteractionStage.FootGreeting
                || stage == InteractionStage.FootDocuments
                || stage == InteractionStage.TrafficPullingOver
                || stage == InteractionStage.TrafficPreparing
                || stage == InteractionStage.TrafficDocuments;
        }

        private bool IsOrdinaryCitizenPresentationStage()
        {
            return IsCitizenPresentationStage(_stage)
                && !_suspiciousFootContact
                && !_suspiciousTrafficContact
                && !_backupInterceptionInvestigationRequired;
        }

        private static bool IsPedPhysicallyCuffed(Ped subject)
        {
            if (!IsUsable(subject))
                return false;
            try { return Function.Call<bool>(Hash.IS_PED_CUFFED, subject); }
            catch { return false; }
        }

        private static bool TryApplyPhysicalHandcuffs(Ped subject)
        {
            if (!IsUsable(subject) || subject.IsDead || subject.Health <= 0)
                return false;
            try
            {
                Function.Call(Hash.SET_PED_CONFIG_FLAG,
                    subject, HandcuffedPedConfigFlag, true);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, subject, true);
                return IsPedPhysicallyCuffed(subject);
            }
            catch
            {
                return false;
            }
        }

        private bool TryStartPairedCustodyArrest(
            Ped officer,
            Ped subject,
            out int sceneId,
            out float clipDuration,
            out bool grounded,
            out Vector3 sceneOrigin,
            out float sceneHeading)
        {
            sceneId = -1;
            clipDuration = 0f;
            grounded = false;
            sceneOrigin = Vector3.Zero;
            sceneHeading = 0f;
            if (!IsUsable(officer) || !IsUsable(subject)
                || subject.IsInVehicle())
                return false;

            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture =
                LSImmersiveDictionaryAnimation.ClassifyArrestTarget(subject);
            if (posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Dead
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Cuffed
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling)
                return false;

            grounded = posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded;
            string dictionary = grounded ? "mp_arresting" : "mp_arrest_paired";
            LSImmersiveDictionaryAnimation.RequestDictionary(dictionary);
            if (!LSImmersiveDictionaryAnimation.IsDictionaryLoaded(dictionary))
                return false;

            return grounded
                ? LSImmersiveDictionaryAnimation.TryPlaySynchronizedGroundArrest(
                    officer, subject, out sceneId, out clipDuration,
                    out sceneOrigin, out sceneHeading)
                : LSImmersiveDictionaryAnimation.TryPlaySynchronizedArrest(
                    officer, subject, out sceneId, out clipDuration,
                    out sceneOrigin, out sceneHeading);
        }

        private void MaintainBackupEscortCustody(DateTime now)
        {
            if (!IsUsable(_subject) || _subject.IsDead)
                return;

            if (now >= _nextBackupCustodyRefreshAt)
            {
                try
                {
                    Function.Call(Hash.SET_PED_CONFIG_FLAG,
                        _subject, HandcuffedPedConfigFlag, true);
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, true);
                    _subject.BlockPermanentEvents = true;
                    _subject.CanSwitchWeapons = false;
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_BACKUP_ESCORT_CUSTODY_REFRESH_FAILED", ex);
                }
                _nextBackupCustodyRefreshAt = now.AddSeconds(1);
            }

            if (_backupMovementClipsetApplied || _backupMovementClipsetUnavailableLogged
                || now < _nextBackupMovementClipsetCheckAt)
                return;

            _nextBackupMovementClipsetCheckAt = now.AddMilliseconds(
                BackupMovementClipsetCheckMilliseconds);
            try
            {
                if (Function.Call<bool>(Hash.HAS_ANIM_SET_LOADED,
                    BackupPrisonerMovementClipset))
                {
                    Function.Call(Hash.SET_PED_MOVEMENT_CLIPSET,
                        _subject, BackupPrisonerMovementClipset, 0.25f);
                    _backupMovementClipsetApplied = true;
                    _log.Runtime("NPC_BACKUP_RESTRAINED_MOVEMENT_APPLIED",
                        "Ped=" + _subject.Handle
                        + "; Officer=" + (_backupOfficer == null ? 0 : _backupOfficer.Handle)
                        + "; Clipset=" + BackupPrisonerMovementClipset
                        + "; Owner=CitizenBackupHandoff");
                    return;
                }

                if (_backupMovementClipsetDeadline != DateTime.MinValue
                    && now >= _backupMovementClipsetDeadline)
                {
                    _backupMovementClipsetUnavailableLogged = true;
                    _log.Runtime("NPC_BACKUP_RESTRAINED_MOVEMENT_UNAVAILABLE",
                        "Ped=" + _subject.Handle
                        + "; Clipset=" + BackupPrisonerMovementClipset
                        + "; Fallback=NaturalWalkWithOfficerFollowing");
                    return;
                }

                Function.Call(Hash.REQUEST_ANIM_SET,
                    BackupPrisonerMovementClipset);
            }
            catch (Exception ex)
            {
                _backupMovementClipsetUnavailableLogged = true;
                _log.Exception("NPC_BACKUP_RESTRAINED_MOVEMENT_FAILED", ex);
            }
        }

        private void RestoreBackupCustodyMovementStyle()
        {
            if (_backupMovementClipsetApplied && IsUsable(_subject))
            {
                try
                {
                    Function.Call(Hash.RESET_PED_MOVEMENT_CLIPSET, _subject, 0.25f);
                    _log.Runtime("NPC_BACKUP_RESTRAINED_MOVEMENT_RESTORED",
                        "Ped=" + _subject.Handle + "; Owner=CitizenBackupHandoff");
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_BACKUP_RESTRAINED_MOVEMENT_RESTORE_FAILED", ex);
                }
            }
            _backupMovementClipsetApplied = false;
            _nextBackupMovementClipsetCheckAt = DateTime.MinValue;
            _backupMovementClipsetDeadline = DateTime.MinValue;
            _nextBackupCustodyRefreshAt = DateTime.MinValue;
            _backupMovementClipsetUnavailableLogged = false;
        }

        private void CaptureSubjectInjuryState(Ped subject)
        {
            if (!IsUsable(subject) || subject.IsDead || subject.Health <= 0
                || _subjectInjuries.ContainsKey(subject.Handle))
                return;

            int originalHealth = Math.Max(1, subject.Health);
            int originalMaximumHealth = Math.Max(1, subject.MaxHealth);
            SubjectInjuryState state = new SubjectInjuryState
            {
                OriginalHealth = originalHealth,
                OriginalMaximumHealth = originalMaximumHealth,
                LastHealth = originalHealth,
                InteractionId = (_log == null ? string.Empty : _log.SessionId)
                    + ":Citizen:" + subject.Handle + ":" + DateTime.UtcNow.Ticks,
                OriginalCanSufferCriticalHits = subject.CanSufferCriticalHits,
                OriginalCanSwitchWeapons = subject.CanSwitchWeapons
            };
            try
            {
                state.OriginalSelectedWeaponHash = Function.Call<int>(
                    Hash.GET_SELECTED_PED_WEAPON, subject);
                try { Function.Call(Hash.CLEAR_PED_LAST_DAMAGE_BONE, subject); }
                catch { }
                try { Function.Call(Hash.CLEAR_ENTITY_LAST_WEAPON_DAMAGE, subject); }
                catch { }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_SUBJECT_INJURY_MONITOR_START_FAILED", ex);
            }
            _subjectInjuries[subject.Handle] = state;
            _log.Runtime("NPC_SUBJECT_INJURY_MONITOR_STARTED",
                "Session=" + (_log == null ? string.Empty : _log.SessionId)
                + "; Interaction=" + state.InteractionId
                + "; RuntimeOwner=Citizen; Ped=" + subject.Handle
                + "; HealthBefore=" + originalHealth
                + "; MaximumHealth=" + originalMaximumHealth
                + "; HealthWritten=false; MaximumHealthWritten=false"
                + "; CriticalHitSettingChanged=false; ReusedPed=true");
        }

        private void EnableControlledCitizenInjury(Ped subject, string branch)
        {
            if (!IsUsable(subject) || subject.IsDead || subject.Health <= 0)
                return;

            SubjectInjuryState state;
            if (!_subjectInjuries.TryGetValue(subject.Handle, out state)
                || state == null || state.ControlledInjuryActive)
                return;

            int healthBefore = Math.Max(1, subject.Health);
            int maximumBefore = Math.Max(1, subject.MaxHealth);
            int managedMaximum = Math.Max(
                state.OriginalMaximumHealth,
                Math.Max(maximumBefore,
                    _injurySettings == null
                        ? 2000 : _injurySettings.ControlledMaximumHealth));
            int managedHealth = Math.Max(1, Math.Min(
                managedMaximum,
                (int)Math.Round((double)healthBefore * managedMaximum / maximumBefore)));
            try
            {
                subject.CanSufferCriticalHits = false;
                subject.MaxHealth = managedMaximum;
                subject.Health = managedHealth;
                state.ManagedMaximumHealth = managedMaximum;
                state.ControlledInjuryActive = true;
                state.LastHealth = Math.Max(1, subject.Health);
                state.ManagedHealthAtProtectionStart = state.LastHealth;
                state.HasLastProcessedDamageRecord = false;
                ClearSubjectDamageRecords(subject);
                _log.Runtime("NPC_SUBJECT_CONTROLLED_INJURY_ENABLED",
                    "Interaction=" + state.InteractionId
                    + "; RuntimeOwner=Citizen; Ped=" + subject.Handle
                    + "; Branch=" + (branch ?? "Unknown")
                    + "; HealthBefore=" + healthBefore
                    + "; MaximumBefore=" + maximumBefore
                    + "; ManagedHealth=" + state.LastHealth
                    + "; ManagedMaximum=" + state.ManagedMaximumHealth
                    + "; StartingHealthRatioPreserved=true"
                    + "; CanSufferCriticalHits=" + subject.CanSufferCriticalHits
                    + "; CooperativeContact=false");
            }
            catch (Exception ex)
            {
                try { subject.MaxHealth = state.OriginalMaximumHealth; }
                catch { }
                try { subject.CanSufferCriticalHits = state.OriginalCanSufferCriticalHits; }
                catch { }
                state.ControlledInjuryActive = false;
                state.ManagedMaximumHealth = 0;
                state.LastHealth = Math.Max(1, subject.Health);
                _log.Exception("NPC_SUBJECT_CONTROLLED_INJURY_ENABLE_FAILED", ex);
            }
        }

        private void MaintainSubjectInjury(Ped subject, DateTime now, Ped player)
        {
            if (!IsUsable(subject))
                return;

            SubjectInjuryState state;
            if (!_subjectInjuries.TryGetValue(subject.Handle, out state))
                return;
            if (state.Fatal)
                return;

            int currentHealth = Math.Max(0, subject.Health);
            if (currentHealth < state.LastHealth)
            {
                int damage = state.LastHealth - currentHealth;
                // Capture the native bone immediately after observing the
                // health drop. Retain a recent nonzero bone when this hit
                // reports Bone 0, then classify before clearing the records.
                int rawBone = ReadSubjectLastDamageBone(subject);
                int bone = rawBone;
                bool retainedBone = false;
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
                bool damageRecordAvailable = TryReadSubjectDamageRecord(
                    subject, out damageWeaponHash, out damageAttackerHandle,
                    out damageGameTime);
                state.LastDamageWeaponHash = damageWeaponHash;
                state.LastDamageAttackerHandle = damageAttackerHandle;
                state.LastDamageGameTime = damageGameTime;
                ControlledDamageKind damageKind = ReadSubjectDamageKind(
                    subject, damageWeaponHash, damageRecordAvailable);
                int currentMaximumHealth = Math.Max(1, subject.MaxHealth);
                if (TryReconcileCitizenHealthReset(
                    subject, state, currentHealth, currentMaximumHealth,
                    rawBone, damageRecordAvailable, damageKind))
                    return;
                bool duplicateDamageEvent = IsDuplicateDamageRecord(
                    state, damageRecordAvailable, damageWeaponHash,
                    damageAttackerHandle, damageGameTime, rawBone);
                SuspectHitZone hitZone = ClassifySuspectHitZone(bone);
                SuspectHitZone rawHitZone = ClassifySuspectHitZone(rawBone);
                bool physicalDown = false;
                try
                {
                    physicalDown = Function.Call<bool>(Hash.IS_PED_RAGDOLL, subject)
                        || Function.Call<bool>(Hash.IS_PED_IN_WRITHE, subject);
                }
                catch { }
                bool wasDowned = state.Wounded && physicalDown;
                ClearSubjectDamageRecords(subject);

                if (!duplicateDamageEvent)
                {
                    state.DamageTaken += damage;
                    state.RegisteredHits++;
                    if (damageKind == ControlledDamageKind.Weapon)
                        state.WeaponHits++;
                    if (damageKind == ControlledDamageKind.Melee)
                        state.MeleeHits++;
                    if (wasDowned)
                    {
                        state.DownedHits++;
                        if (damageKind == ControlledDamageKind.Weapon)
                            state.FatalDownedHits++;
                    }
                    if (hitZone == SuspectHitZone.Head)
                        state.HeadHits++;
                    else if (hitZone == SuspectHitZone.Chest)
                        state.ChestHits++;
                    else if (hitZone == SuspectHitZone.Stomach)
                        state.StomachHits++;

                    if (damageRecordAvailable && damageGameTime > 0)
                    {
                        state.HasLastProcessedDamageRecord = true;
                        state.LastProcessedDamageWeaponHash = damageWeaponHash;
                        state.LastProcessedDamageAttackerHandle = damageAttackerHandle;
                        state.LastProcessedDamageGameTime = damageGameTime;
                        state.LastProcessedDamageBone = rawBone;
                    }
                }

                bool engineTerminal = subject.IsDead || currentHealth <= 0;
                bool fatalHeadOrNeck = !duplicateDamageEvent
                    && damageKind == ControlledDamageKind.Weapon
                    && rawHitZone == SuspectHitZone.Head;
                bool fatalRepeatedMelee = !duplicateDamageEvent
                    && damageKind == ControlledDamageKind.Melee
                    && state.MeleeHits >= _injurySettings.CumulativeFatalHitThreshold;
                bool fatalCumulative = !duplicateDamageEvent
                    && damageKind == ControlledDamageKind.Weapon
                    && state.WeaponHits >= _injurySettings.CumulativeFatalHitThreshold;
                bool fatalAfterDownedHits = !duplicateDamageEvent && wasDowned
                    && damageKind == ControlledDamageKind.Weapon
                    && state.FatalDownedHits
                    >= _injurySettings.RepeatedDamageAfterDownedThreshold;
                state.LastDamageKind = damageKind.ToString();
                state.Fatal = engineTerminal || fatalHeadOrNeck
                    || fatalRepeatedMelee || fatalCumulative || fatalAfterDownedHits;

                _log.Runtime("NPC_SUBJECT_INJURY_RECORDED",
                    "Session=" + (_log == null ? string.Empty : _log.SessionId)
                    + "; Interaction=" + state.InteractionId
                    + "; RuntimeOwner=Citizen; Ped=" + subject.Handle
                    + "; PreviousState=" + _stage
                    + "; HealthBefore=" + state.LastHealth
                    + "; HealthAfter=" + currentHealth
                    + "; DamageDelta=" + damage
                    + "; RawBone=" + rawBone
                    + "; EffectiveBone=" + bone
                    + "; RetainedBone=" + retainedBone
                    + "; Zone=" + hitZone
                    + "; DamageKind=" + damageKind
                    + "; DamageRecordAvailable=" + damageRecordAvailable
                    + "; DamageWeaponHash=" + damageWeaponHash
                    + "; DamageAttackerHandle=" + damageAttackerHandle
                    + "; DamageGameTime=" + damageGameTime
                    + "; DamageEventDuplicate=" + duplicateDamageEvent
                    + "; HeadHits=" + state.HeadHits
                    + "; ChestHits=" + state.ChestHits
                    + "; StomachHits=" + state.StomachHits
                    + "; RegisteredHits=" + state.RegisteredHits
                    + "; WeaponHits=" + state.WeaponHits
                    + "; DownedHits=" + state.DownedHits
                    + "; FatalDownedHits=" + state.FatalDownedHits
                    + "; CumulativeDamage=" + state.DamageTaken
                    + "; WoundedBefore=" + state.Wounded
                    + "; Fatal=" + state.Fatal);

                if (state.Fatal)
                {
                    if (!engineTerminal)
                    {
                        try { subject.Health = 0; } catch { }
                    }
                    state.LastHealth = 0;
                    _log.Runtime("NPC_SUBJECT_FATAL_INJURY",
                        "Session=" + (_log == null ? string.Empty : _log.SessionId)
                        + "; Interaction=" + state.InteractionId
                        + "; RuntimeOwner=Citizen; Ped=" + subject.Handle
                        + "; DamageKind=" + damageKind
                        + "; Zone=" + hitZone
                        + "; Reason=" + (engineTerminal ? "EngineTerminal"
                            : fatalHeadOrNeck ? "ConfirmedHeadOrNeckWeaponHit"
                            : fatalRepeatedMelee ? "RepeatedMeleeHits"
                            : fatalAfterDownedHits ? "RepeatedDamageWhileDowned"
                            : "CumulativeHitThreshold")
                        + "; HeadHits=" + state.HeadHits
                        + "; ChestHits=" + state.ChestHits
                        + "; StomachHits=" + state.StomachHits
                        + "; RegisteredHits=" + state.RegisteredHits
                        + "; DownedHits=" + state.DownedHits);
                    return;
                }

                if (duplicateDamageEvent)
                {
                    // A repeated native record cannot restart a reaction task
                    // or count as another hit. The original event already owns
                    // its injury response and the current health is still
                    // reconciled below.
                }
                else if (damageKind == ControlledDamageKind.Melee
                    && !IsCitizenPresentationStage(_stage))
                {
                    state.MeleeReactionPending = true;
                    state.MeleeReactionUntil = now.AddMilliseconds(
                        _injurySettings.MeleeReactionRecoveryMilliseconds);
                    state.NextMeleeRecoveryCheckAt = state.MeleeReactionUntil;
                    _log.Runtime("NPC_SUBJECT_MELEE_REACTION",
                        "Session=" + (_log == null ? string.Empty : _log.SessionId)
                        + "; Interaction=" + state.InteractionId
                        + "; Ped=" + subject.Handle
                        + "; MeleeHits=" + state.MeleeHits
                        + "; PreviousState=" + _stage
                        + "; TemporaryReaction=true; Wounded=" + state.Wounded
                        + "; AutoArrest=false; TaskClear=false; SceneReset=false");
                }
                else
                {
                    state.Wounded = true;
                    DisableWoundedSubjectWeapon(subject, state);
                    InterruptCitizenPresentationForInjury(subject, state);
                }

                if (state.ControlledInjuryActive
                    && currentHealth < _injurySettings.ArrestableWoundedHealthFloor)
                    subject.Health = _injurySettings.ArrestableWoundedHealthFloor;
                state.LastHealth = Math.Max(1, subject.Health);
            }

            MaintainSubjectMeleeReaction(subject, now, state);

            if (state.Wounded && !state.Fatal && IsUsable(subject)
                && !subject.IsDead && subject.Health > 0
                && !subject.IsInVehicle()
                && _stage != InteractionStage.FootPlayerArresting
                && !_footPlayerArrested
                && _stage != InteractionStage.BackupSecuring
                && _stage != InteractionStage.BackupEscort
                && _stage != InteractionStage.BackupTransport)
            {
                MaintainWoundedSubjectRagdoll(subject, now, player, state);
            }
        }

        private bool TryReconcileCitizenHealthReset(
            Ped subject,
            SubjectInjuryState state,
            int currentHealth,
            int currentMaximumHealth,
            int rawBone,
            bool damageRecordAvailable,
            ControlledDamageKind damageKind)
        {
            if (state == null || !IsUsable(subject)
                || currentHealth <= 0 || subject.IsDead
                || rawBone != 0 || damageRecordAvailable
                || damageKind != ControlledDamageKind.Unknown)
                return false;

            int expectedMaximumHealth = state.ControlledInjuryActive
                ? state.ManagedMaximumHealth : state.OriginalMaximumHealth;
            int expectedHealth = state.ControlledInjuryActive
                ? state.ManagedHealthAtProtectionStart : state.OriginalHealth;
            if (expectedMaximumHealth <= 0 || expectedHealth <= 0
                || state.Wounded || state.RegisteredHits > 0
                || state.LastHealth < expectedHealth)
                return false;

            // Recognize the observed initialization/persistence signature in
            // either form: health returns exactly to the value captured before
            // this Citizen branch, or both current health and maximum fall to
            // the same lower native default. No damage bone, record, or weapon
            // classification may accompany this correction.
            bool returnedToCapturedHealth = currentHealth == state.OriginalHealth;
            bool maximumAndHealthFellTogether = currentMaximumHealth < expectedMaximumHealth
                && currentHealth == currentMaximumHealth;
            if (!returnedToCapturedHealth && !maximumAndHealthFellTogether)
                return false;

            if (state.ControlledInjuryActive)
            {
                int originalMaximumHealth = Math.Max(1, state.OriginalMaximumHealth);
                int reconciledHealth = Math.Max(1, Math.Min(
                    state.ManagedMaximumHealth,
                    (int)Math.Round((double)state.OriginalHealth
                        * state.ManagedMaximumHealth / originalMaximumHealth)));
                try
                {
                    subject.CanSufferCriticalHits = false;
                    if (currentMaximumHealth != state.ManagedMaximumHealth)
                        subject.MaxHealth = state.ManagedMaximumHealth;
                    if (currentHealth != reconciledHealth)
                        subject.Health = reconciledHealth;
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_SUBJECT_HEALTH_RECONCILIATION_FAILED", ex);
                }
            }

            int observedMaximum = Math.Max(1, subject.MaxHealth);
            int observedHealth = Math.Max(1, subject.Health);
            state.LastHealth = observedHealth;
            ClearSubjectDamageRecords(subject);
            state.HasLastProcessedDamageRecord = false;
            if (!state.HealthReconciliationLogged)
            {
                state.HealthReconciliationLogged = true;
                _log.Runtime(state.ControlledInjuryActive
                        ? "NPC_SUBJECT_HEALTH_RESET_RECONCILED"
                        : "NPC_SUBJECT_HEALTH_RESET_IGNORED",
                    "Interaction=" + state.InteractionId
                    + "; RuntimeOwner=Citizen; Ped=" + subject.Handle
                    + "; HealthBeforeReset=" + currentHealth
                    + "; MaximumBeforeReset=" + currentMaximumHealth
                    + "; ExpectedMaximum=" + expectedMaximumHealth
                    + "; ManagedMaximum=" + state.ManagedMaximumHealth
                    + "; HealthAfter=" + observedHealth
                    + "; MaximumAfter=" + observedMaximum
                    + "; Bone=0; DamageRecordAvailable=false"
                    + "; DamageKind=Unknown; DamageDeltaNotClassifiedAsInjury=true"
                    + "; ControlledHealthWritten=" + state.ControlledInjuryActive
                    + "; ContactContinues=true; ArrestAuthorized=false"
                    + "; Reconciled=" + (!state.ControlledInjuryActive
                        || observedMaximum >= state.ManagedMaximumHealth));
            }
            return true;
        }

        private static bool IsDuplicateDamageRecord(
            SubjectInjuryState state,
            bool damageRecordAvailable,
            int weaponHash,
            int attackerHandle,
            int gameTime,
            int rawBone)
        {
            return state != null
                && damageRecordAvailable
                && gameTime > 0
                && state.HasLastProcessedDamageRecord
                && state.LastProcessedDamageWeaponHash == weaponHash
                && state.LastProcessedDamageAttackerHandle == attackerHandle
                && state.LastProcessedDamageGameTime == gameTime
                && state.LastProcessedDamageBone == rawBone;
        }

        private void InterruptCitizenPresentationForInjury(
            Ped subject,
            SubjectInjuryState state)
        {
            if (state == null || state.WritheSuppressedForCitizenContact
                || !IsOrdinaryCitizenPresentationStage())
                return;

            state.WritheSuppressedForCitizenContact = true;
            _log.Runtime("NPC_CITIZEN_PRESENTATION_INJURY_RECORDED",
                "Interaction=" + state.InteractionId
                + "; Ped=" + subject.Handle
                + "; Stage=" + _stage
                + "; ContactWillEnd=true; WritheTaskIssued=false");
        }

        private void EndOrdinaryContactForInjury()
        {
            if (!HasActiveInteraction || !IsUsable(_subject))
                return;

            Ped injuredCitizen = _subject;
            Vehicle contactVehicle = _subjectVehicle;
            int pedHandle = injuredCitizen.Handle;
            int vehicleHandle = IsUsable(contactVehicle) ? contactVehicle.Handle : 0;
            InteractionStage injuredStage = _stage;

            // End only this ordinary contact. Keep the same injured entity and
            // its game-owned reaction at the scene; no arrest, task clear, or
            // replacement movement task is issued here.
            ReleaseSubjectPoliceAwareness(injuredCitizen, true);
            if (IsUsable(contactVehicle))
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, contactVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_BRAKE, contactVehicle, false);
                }
                catch (Exception ex) { _log.Exception("NPC_INJURED_TRAFFIC_RELEASE_FAILED", ex); }
            }

            ClearInteractionState();
            _log.Runtime("NPC_ORDINARY_CONTACT_ENDED_INJURY",
                "Ped=" + pedHandle
                + "; Vehicle=" + vehicleHandle
                + "; Stage=" + injuredStage
                + "; InjuryRecorded=true; ArrestStarted=false"
                + "; EntityDeleted=false; TaskCleared=false; HealthRestored=false");
            Notify("The Citizen was injured during ordinary contact. Contact ended; the injured person remains at the scene. No arrest was made.");
        }

        private ControlledDamageKind ReadSubjectDamageKind(
            Ped subject,
            int damageWeaponHash,
            bool damageRecordAvailable)
        {
            if (!IsUsable(subject))
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
                if (subject.HasBeenDamagedByAnyMeleeWeapon())
                    return ControlledDamageKind.Melee;
                if (subject.HasBeenDamagedByAnyWeapon())
                    return ControlledDamageKind.Weapon;
            }
            catch { }
            return ControlledDamageKind.Unknown;
        }

        private static bool TryReadSubjectDamageRecord(
            Ped subject,
            out int weaponHash,
            out int attackerHandle,
            out int gameTime)
        {
            weaponHash = 0;
            attackerHandle = 0;
            gameTime = 0;
            if (!IsUsable(subject))
                return false;
            try
            {
                EntityDamageRecord[] records = subject.DamageRecords.GetAllDamageRecords();
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

        private static int ReadSubjectLastDamageBone(Ped subject)
        {
            int bone = 0;
            try
            {
                using (OutputArgument output = new OutputArgument())
                {
                    if (Function.Call<bool>(
                        Hash.GET_PED_LAST_DAMAGE_BONE, subject, output))
                        bone = output.GetResult<int>();
                }
            }
            catch { }
            return bone;
        }

        private static void ClearSubjectDamageRecords(Ped subject)
        {
            try { Function.Call(Hash.CLEAR_PED_LAST_DAMAGE_BONE, subject); }
            catch { }
            try { Function.Call(Hash.CLEAR_ENTITY_LAST_WEAPON_DAMAGE, subject); }
            catch { }
        }

        private void DisableWoundedSubjectWeapon(
            Ped subject,
            SubjectInjuryState state)
        {
            if (!IsUsable(subject) || state == null || state.WeaponDisabledByInjury)
                return;
            try
            {
                subject.CanSwitchWeapons = false;
                Function.Call(Hash.SET_CURRENT_PED_WEAPON,
                    subject, WeaponHash.Unarmed, true);
                state.WeaponDisabledByInjury = true;
                _log.Runtime("NPC_SUBJECT_WEAPON_DISABLED_AFTER_WOUND",
                    "Interaction=" + state.InteractionId
                    + "; Ped=" + subject.Handle
                    + "; CurrentWeapon=Unarmed; InventoryRemoved=false");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_SUBJECT_WEAPON_DISABLE_FAILED", ex);
            }
        }

        private bool IsSubjectMeleeReactionPending(Ped subject)
        {
            SubjectInjuryState state;
            return IsUsable(subject)
                && _subjectInjuries.TryGetValue(subject.Handle, out state)
                && state.MeleeReactionPending;
        }

        private void MaintainSubjectMeleeReaction(
            Ped subject,
            DateTime now,
            SubjectInjuryState state)
        {
            if (!IsUsable(subject) || state == null || !state.MeleeReactionPending
                || now < state.MeleeReactionUntil
                || now < state.NextMeleeRecoveryCheckAt)
                return;

            state.NextMeleeRecoveryCheckAt = now.AddMilliseconds(200);
            bool ragdoll = false;
            try { ragdoll = Function.Call<bool>(Hash.IS_PED_RAGDOLL, subject); }
            catch { }
            if (ragdoll)
                return;

            state.MeleeReactionPending = false;
            state.MeleeReactionUntil = DateTime.MinValue;
            bool arrestAvailable = !_backupRequested
                && (_stage == InteractionStage.FootAwaitingPlayerArrest
                    || state.Wounded || _footPlayerArrested);
            _log.Runtime("NPC_SUBJECT_MELEE_RECOVERED",
                "Session=" + (_log == null ? string.Empty : _log.SessionId)
                + "; Interaction=" + state.InteractionId
                + "; RuntimeOwner=Citizen; Ped=" + subject.Handle
                + "; RestoredState=" + _stage
                + "; Wounded=" + state.Wounded
                + "; ArrestActionAvailable=" + arrestAvailable
                + "; SamePed=true; SceneReset=false; PhysicalRagdoll=false");
        }

        private void MaintainWoundedSubjectRagdoll(
            Ped subject,
            DateTime now,
            Ped player,
            SubjectInjuryState state)
        {
            if (state == null || state.WritheTaskStarted
                || state.WritheSuppressedForCitizenContact || !IsUsable(subject)
                || subject.IsDead || subject.Health <= 0
                || _stage == InteractionStage.FootPlayerArresting
                || _footPlayerArrested
                || _stage == InteractionStage.FootBackupContained
                || _stage == InteractionStage.TrafficBackupContained
                || _stage == InteractionStage.BackupSecuring
                || _stage == InteractionStage.BackupEscort
                || _stage == InteractionStage.BackupTransport)
                return;

            try
            {
                if (Function.Call<bool>(Hash.IS_PED_IN_WRITHE, subject))
                {
                    state.WritheTaskStarted = true;
                    _log.Runtime("NPC_SUBJECT_WRITHE_TASK_PRESERVED",
                        "Interaction=" + state.InteractionId
                        + "; Ped=" + subject.Handle
                        + "; ExistingWrithe=true; TaskReissued=false");
                    return;
                }

                // Issue the downed task once without clearing the Ped's full
                // task stack, which could cancel surrender, custody or scene
                // ownership before the injury result is known.
                Function.Call(Hash.TASK_WRITHE,
                    subject,
                    IsUsable(player) ? player : subject,
                    1,
                    0,
                    false,
                    0);
                state.WritheTaskStarted = true;
                _log.Runtime("NPC_SUBJECT_WRITHE_TASK_ISSUED",
                    "Interaction=" + state.InteractionId
                    + "; Ped=" + subject.Handle + "; Target="
                    + (IsUsable(player) ? player.Handle : subject.Handle)
                    + "; Arrestable=true; Teleport=false; Attachment=false"
                    + "; ClearAll=false; OneShot=true");
            }
            catch (Exception ex)
            {
                try
                {
                    Function.Call(Hash.SET_PED_TO_RAGDOLL,
                        subject, 3500, 3500, 0, false, false, false);
                    state.WritheTaskStarted = true;
                }
                catch (Exception ragdollException)
                {
                    _log.Exception("NPC_SUBJECT_WRITHE_FALLBACK_FAILED", ragdollException);
                }
                _log.Exception("NPC_SUBJECT_WRITHE_TASK_FAILED", ex);
            }
        }

        private static SuspectHitZone ClassifySuspectHitZone(int bone)
        {
            switch (bone)
            {
                case 31086: // SKEL_Head
                case 39317: // SKEL_Neck_1
                case 65068: // SKEL_Neck_2
                    return SuspectHitZone.Head;
                case 64729: // SKEL_L_Clavicle
                case 10706: // SKEL_R_Clavicle
                case 24817: // SKEL_Spine2
                case 24818: // SKEL_Spine3
                    return SuspectHitZone.Chest;
                case 11816: // SKEL_Pelvis
                case 57597: // SKEL_Spine_Root
                case 23553: // SKEL_Spine0
                case 24816: // SKEL_Spine1
                    return SuspectHitZone.Stomach;
                case 45509: // SKEL_L_Hand
                case 40269: // SKEL_R_Hand
                case 61163: // SKEL_L_Forearm
                case 43810: // SKEL_R_Forearm
                case 28252: // SKEL_L_UpperArm
                case 18905: // SKEL_R_UpperArm
                case 57005: // SKEL_R_Hand
                case 58271: // SKEL_L_Thigh
                case 51826: // SKEL_R_Thigh
                case 63931: // SKEL_L_Calf
                case 36864: // SKEL_R_Calf
                case 14201: // SKEL_L_Foot
                case 52301: // SKEL_R_Foot
                case 2108:  // SKEL_L_Toe0
                case 20781: // SKEL_R_Toe0
                    return SuspectHitZone.Limb;
                default:
                    return SuspectHitZone.Unknown;
            }
        }

        private void RestoreSubjectInjuryState(Ped subject)
        {
            if (subject == null)
                return;

            SubjectInjuryState state;
            if (!_subjectInjuries.TryGetValue(subject.Handle, out state))
                return;
            if (!IsUsable(subject))
            {
                _subjectInjuries.Remove(subject.Handle);
                return;
            }

            try
            {
                bool custodyOwned = IsPedPhysicallyCuffed(subject)
                    || _footPlayerArrested
                    || _stage == InteractionStage.BackupSecuring
                    || _stage == InteractionStage.BackupEscort
                    || _stage == InteractionStage.BackupTransport;
                if (state.ControlledInjuryActive)
                {
                    int healthBeforeRestore = Math.Max(0, subject.Health);
                    subject.MaxHealth = Math.Max(1, state.OriginalMaximumHealth);
                    if (!state.Fatal && !subject.IsDead && subject.Health > 0)
                    {
                        int restoredHealth = Math.Max(1,
                            state.OriginalHealth - state.DamageTaken);
                        if (custodyOwned && state.Wounded)
                            restoredHealth = Math.Max(restoredHealth,
                                _injurySettings.ArrestableWoundedHealthFloor);
                        subject.Health = Math.Min(
                            state.OriginalMaximumHealth, restoredHealth);
                    }
                    subject.CanSufferCriticalHits = state.OriginalCanSufferCriticalHits;
                    _log.Runtime("NPC_SUBJECT_CONTROLLED_INJURY_RESTORED",
                        "Interaction=" + state.InteractionId
                        + "; RuntimeOwner=Citizen; Ped=" + subject.Handle
                        + "; Branch=" + _stage
                        + "; HealthBefore=" + healthBeforeRestore
                        + "; HealthAfter=" + Math.Max(0, subject.Health)
                        + "; OriginalMaximum=" + state.OriginalMaximumHealth
                        + "; CriticalHits=" + subject.CanSufferCriticalHits
                        + "; CustodyOwned=" + custodyOwned
                        + "; Fatal=" + state.Fatal);
                }
                if (custodyOwned)
                    subject.CanSwitchWeapons = false;
                else
                {
                    subject.CanSwitchWeapons = state.OriginalCanSwitchWeapons;
                    if (state.WeaponDisabledByInjury && !subject.IsDead
                        && subject.Health > 0
                        && state.OriginalSelectedWeaponHash != 0)
                        Function.Call(Hash.SET_CURRENT_PED_WEAPON,
                            subject, state.OriginalSelectedWeaponHash, true);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_SUBJECT_INJURY_STATE_RESTORE_FAILED", ex);
            }
            _subjectInjuries.Remove(subject.Handle);
        }

        private void DeferDeadFootSubject(Ped subject, DateTime now)
        {
            if (!IsUsable(subject))
                return;

            _deferredDeadSubject = subject;
            _deferredDeadSubjectCreatedAt = now;
            _nextDeferredDeadSubjectScanAt = now;
            _deferredDeadSubjectPersistenceCaptured = _subjectPersistenceCaptured;
            _deferredDeadSubjectWasPersistent = _subjectPersistenceCaptured
                ? _subjectWasPersistent
                : subject.IsPersistent;

            // Do not clear the dead ped's task here. GTA's ragdoll/death
            // aftermath is part of the visible result. Keep it mission-owned
            // only for the short local scene, then release it without deleting
            // the entity so the game can perform its normal cleanup.
            try
            {
                subject.IsPersistent = true;
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, subject, true, false);
                Function.Call(Hash.SET_ENTITY_VISIBLE, subject, true, false);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_DEAD_SUBJECT_HOLD_FAILED", ex);
            }

            // ClearInteractionState must not restore the captured ambient
            // persistence immediately after the handoff to the deferred scene.
            _subjectPersistenceCaptured = false;
            _subjectWasPersistent = false;
        }

        private void MaintainDeferredDeadSubject(Ped player, DateTime now)
        {
            if (!IsUsable(_deferredDeadSubject))
            {
                _deferredDeadSubject = null;
                _deferredDeadSubjectCreatedAt = DateTime.MinValue;
                _nextDeferredDeadSubjectScanAt = DateTime.MinValue;
                _deferredDeadSubjectPersistenceCaptured = false;
                _deferredDeadSubjectWasPersistent = false;
                return;
            }
            if (now < _nextDeferredDeadSubjectScanAt)
                return;

            _nextDeferredDeadSubjectScanAt = now.AddSeconds(1);
            float distance = IsUsable(player)
                ? player.Position.DistanceTo(_deferredDeadSubject.Position)
                : 0.0f;
            bool minimumHoldComplete = now >= _deferredDeadSubjectCreatedAt
                .AddMilliseconds(DeadSubjectMinimumHoldMilliseconds);
            float releaseDistance = _settings == null
                ? DefaultDeadSubjectReleaseDistance
                : Math.Max(40.0f, _settings.DeadSubjectReleaseDistance);
            if (!minimumHoldComplete || distance < releaseDistance)
            {
                try
                {
                    _deferredDeadSubject.IsPersistent = true;
                    Function.Call(Hash.SET_ENTITY_VISIBLE, _deferredDeadSubject, true, false);
                }
                catch { }
                return;
            }

            int handle = _deferredDeadSubject.Handle;
            try
            {
                if (_deferredDeadSubjectPersistenceCaptured)
                    _deferredDeadSubject.IsPersistent = _deferredDeadSubjectWasPersistent;
                else
                    _deferredDeadSubject.IsPersistent = false;
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY,
                    _deferredDeadSubject, false, false);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_DEAD_SUBJECT_RELEASE_FAILED", ex);
            }

            _log.Runtime("NPC_FOOT_DEAD_SUBJECT_RELEASED",
                "Ped=" + handle + "; Distance=" + distance.ToString("0.0")
                + "; Cleanup=NaturalWorldCleanup");
            _deferredDeadSubject = null;
            _deferredDeadSubjectCreatedAt = DateTime.MinValue;
            _nextDeferredDeadSubjectScanAt = DateTime.MinValue;
            _deferredDeadSubjectPersistenceCaptured = false;
            _deferredDeadSubjectWasPersistent = false;
        }

        private void ReleaseDeferredDeadSubject(bool force, string reason)
        {
            if (!IsUsable(_deferredDeadSubject))
            {
                _deferredDeadSubject = null;
                return;
            }
            if (!force)
                return;

            int handle = _deferredDeadSubject.Handle;
            try
            {
                _deferredDeadSubject.IsPersistent = _deferredDeadSubjectPersistenceCaptured
                    ? _deferredDeadSubjectWasPersistent
                    : false;
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY,
                    _deferredDeadSubject, false, false);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_DEAD_SUBJECT_FORCE_RELEASE_FAILED", ex);
            }
            _log.Runtime("NPC_FOOT_DEAD_SUBJECT_RELEASED",
                "Ped=" + handle + "; Reason=" + (reason ?? string.Empty)
                + "; Cleanup=NaturalWorldCleanup");
            _deferredDeadSubject = null;
            _deferredDeadSubjectCreatedAt = DateTime.MinValue;
            _nextDeferredDeadSubjectScanAt = DateTime.MinValue;
            _deferredDeadSubjectPersistenceCaptured = false;
            _deferredDeadSubjectWasPersistent = false;
        }

        /// <summary>
        /// A cooperative pedestrian may leave the immediate contact by walking
        /// to the selected station. This remains an ambient NPC task: it does
        /// not create custody, a Convoy, or a Dispatch case.
        /// </summary>
        private bool BeginFootStationFollow()
        {
            Vector3 destination;
            string stationName;
            if (!TryGetSelectedStation(out destination, out stationName) || !IsUsable(_subject))
                return false;

            DateTime now = DateTime.UtcNow;
            try
            {
                ReleaseSubjectInteractionLockForMovement(_subject);
                _stage = InteractionStage.FootStationFollow;
                _footStationDestination = destination;
                _footStationName = stationName;
                _expiresAt = now.AddMilliseconds(StationFollowTimeoutMilliseconds);
                _nextStationTaskAt = now;
                IssueFootStationTask(now);
                _log.Runtime("NPC_FOOT_STATION_FOLLOW_STARTED",
                    "Ped=" + _subject.Handle + "; Station=" + stationName);
                Notify("Citizen complied and is walking to " + stationName + ".");
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_STATION_FOLLOW_FAILED", ex);
                return false;
            }
        }

        private void MaintainFootStationFollow(DateTime now)
        {
            if (!IsUsable(_subject))
            {
                ClearInteractionState();
                Notify("Station direction ended because the citizen is unavailable.");
                return;
            }

            if (_subject.Position.DistanceTo(_footStationDestination) <= StationArrivalRadius)
            {
                string stationName = _footStationName;
                int handle = _subject.Handle;
                ReleaseSubject(true);
                ClearInteractionState();
                _log.Runtime("NPC_FOOT_STATION_FOLLOW_COMPLETED",
                    "Ped=" + handle + "; Station=" + stationName);
                Notify("Citizen arrived at " + stationName + ". Foot contact is complete.");
                return;
            }

            if (now >= _nextStationTaskAt)
                IssueFootStationTask(now);
        }

        private void IssueFootStationTask(DateTime now)
        {
            if (!IsUsable(_subject))
                return;
            try
            {
                _subject.Task.ClearAll();
                _subject.Task.FollowNavMeshTo(_footStationDestination,
                    PedMoveBlendRatio.Walk, -1, 2.0f,
                    FollowNavMeshFlags.KeepToPavements, 0.0f);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_STATION_TASK_FAILED", ex);
            }
            _nextStationTaskAt = now.AddSeconds(5);
        }

        private void MaintainTrafficFlee(Ped player, DateTime now)
        {
            if (!IsUsable(player))
                return;
            if (!IsUsable(_subject))
            {
                if (_trafficSubjectLostSince == DateTime.MinValue)
                {
                    _trafficSubjectLostSince = now;
                    _log.Runtime("NPC_TRAFFIC_PURSUIT_SUBJECT_LOST_PENDING",
                        "OriginalVehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                        + "; RecoveryMilliseconds=" + TrafficSubjectLostRecoveryMilliseconds);
                }
                if (now < _trafficSubjectLostSince.AddMilliseconds(
                        TrafficSubjectLostRecoveryMilliseconds))
                    return;

                _log.Runtime("NPC_TRAFFIC_PURSUIT_TERMINAL",
                    "Reason=SelectedCitizenUnavailableAfterBoundedRecovery"
                    + "; OriginalVehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle));
                ReleaseSubject(true);
                ClearInteractionState();
                Notify("Traffic pursuit ended because the selected Citizen was lost after recovery.");
                return;
            }
            _trafficSubjectLostSince = DateTime.MinValue;
            if (_subject.IsDead || _subject.Health <= 0)
            {
                _trafficPursuitStage = TrafficPursuitStage.Contained;
                _log.Runtime("NPC_TRAFFIC_PURSUIT_TERMINAL",
                    "Reason=ConfirmedCitizenDeath; Ped=" + _subject.Handle);
                ReleaseSubject(true);
                ClearInteractionState();
                return;
            }

            if (_subject.IsInVehicle())
            {
                Vehicle currentVehicle = null;
                try { currentVehicle = _subject.CurrentVehicle; }
                catch { }
                if (!IsUsable(currentVehicle))
                {
                    if (_trafficVehicleTransitionStartedAt == DateTime.MinValue)
                    {
                        _trafficVehicleTransitionStartedAt = now;
                        _log.Runtime("NPC_TRAFFIC_PURSUIT_VEHICLE_TRANSITION_PENDING",
                            "Ped=" + _subject.Handle
                            + "; OriginalVehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                            + "; RecoveryMilliseconds=" + TrafficVehicleTransitionRecoveryMilliseconds);
                    }
                    if (now < _trafficVehicleTransitionStartedAt.AddMilliseconds(
                            TrafficVehicleTransitionRecoveryMilliseconds))
                        return;

                    _log.Runtime("NPC_TRAFFIC_PURSUIT_TERMINAL",
                        "Reason=CurrentVehicleInvalidAfterBoundedRecovery"
                        + "; Ped=" + _subject.Handle
                        + "; OriginalVehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle));
                    ReleaseSubject(true);
                    ClearInteractionState();
                    Notify("Traffic pursuit ended because the Citizen's vehicle could not be recovered.");
                    return;
                }

                _trafficVehicleTransitionStartedAt = DateTime.MinValue;
                TrackTrafficPursuitVehicle(currentVehicle, now);
                MaintainTrafficVehicleFlee(player, currentVehicle, now);
                return;
            }

            _trafficVehicleTransitionStartedAt = DateTime.MinValue;
            if (_trafficPursuitStage == TrafficPursuitStage.VehicleFlight
                || _trafficPursuitStage == TrafficPursuitStage.VehicleStoppedAwaitingExit)
                BeginTrafficOnFootPursuit(player, now);
            if (_trafficPursuitStage == TrafficPursuitStage.OnFootFlight)
                MaintainTrafficOnFootFlee(player, now);
        }

        private void TrackTrafficPursuitVehicle(Vehicle currentVehicle, DateTime now)
        {
            if (!IsUsable(currentVehicle))
                return;

            int previousHandle = IsUsable(_trafficPursuitVehicle)
                ? _trafficPursuitVehicle.Handle : 0;
            bool vehicleChanged = previousHandle != currentVehicle.Handle;
            bool reenteredOriginal = vehicleChanged && IsUsable(_subjectVehicle)
                && currentVehicle.Handle == _subjectVehicle.Handle;
            bool resumedFromFoot = _trafficPursuitStage == TrafficPursuitStage.OnFootFlight;
            bool resumedAfterStop = _trafficPursuitStage
                == TrafficPursuitStage.VehicleStoppedAwaitingExit;

            _trafficPursuitVehicle = currentVehicle;
            _trafficPursuitStage = TrafficPursuitStage.VehicleFlight;
            if (vehicleChanged || resumedFromFoot || resumedAfterStop)
            {
                _fleeLastPosition = currentVehicle.Position;
                _lastFleeSampleAt = DateTime.MinValue;
                _fleeStoppedAt = DateTime.MinValue;
                _trafficPursuitExitAttempts = 0;
                _trafficPursuitExitFailureNotified = false;
                _nextTrafficPursuitExitAt = DateTime.MinValue;
                _fleeStartedAt = now;
                _fleeMovementConfirmed = true;
                _fleeRecoveryIssued = false;
                if (vehicleChanged)
                    _log.Runtime("NPC_TRAFFIC_PURSUIT_VEHICLE_CHANGED",
                        "Ped=" + _subject.Handle
                        + "; PreviousVehicle=" + previousHandle
                        + "; CurrentVehicle=" + currentVehicle.Handle
                        + "; OriginalContactVehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                        + "; ReenteredOriginal=" + reenteredOriginal
                        + "; SameCitizen=true; DecisionLocked=" + _citizenDocumentDecisionResolved);
            }
        }

        private void BeginTrafficOnFootPursuit(Ped player, DateTime now)
        {
            TrafficPursuitStage previousStage = _trafficPursuitStage;
            InteractionStage previousInteractionStage = _stage;
            bool backupEnRoute = _backupRequested
                && previousInteractionStage != InteractionStage.TrafficBackupContained;
            bool backupAtScene = _backupRequested
                && previousInteractionStage == InteractionStage.TrafficBackupContained;
            int previousVehicle = IsUsable(_trafficPursuitVehicle)
                ? _trafficPursuitVehicle.Handle
                : (IsUsable(_subjectVehicle) ? _subjectVehicle.Handle : 0);
            _trafficPursuitVehicle = null;
            _nextTrafficPursuitExitAt = DateTime.MinValue;
            _trafficPursuitStage = previousStage
                == TrafficPursuitStage.VehicleStoppedAwaitingExit
                ? TrafficPursuitStage.Contained
                : TrafficPursuitStage.OnFootFlight;
            _fleeLastPosition = _subject.Position;
            _lastFleeSampleAt = now;
            _fleeStoppedAt = DateTime.MinValue;
            _fleeStartedAt = now;
            _fleeMovementConfirmed = false;
            _fleeRecoveryIssued = false;
            if (_trafficPursuitStage == TrafficPursuitStage.Contained)
            {
                SetSubjectPoliceAware(_subject);
                if (!IsLivingDowned(_subject) && !IsLivingInWrithe(_subject))
                    _subject.Task.HandsUp(_backupRequested ? 30000 : 20000);
                if (backupAtScene)
                    _stage = InteractionStage.TrafficBackupContained;
                else if (backupEnRoute)
                    _stage = InteractionStage.TrafficAwaitingBackup;
                else
                    _stage = InteractionStage.TrafficRefused;
                _documentsProvided = false;
                _dispatcherValidationReceived = false;
                _citizenRecordReviewed = false;
                CitizenRecordVisible = false;
                _log.Runtime("NPC_TRAFFIC_PURSUIT_CONTAINED",
                    "Ped=" + _subject.Handle
                    + "; OriginalVehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                    + "; StoppedVehicle=" + previousVehicle
                    + "; SameCitizen=true; RecordPreserved=" + (_currentRecord != null)
                    + "; DecisionLocked=" + _citizenDocumentDecisionResolved
                    + "; BackupAssigned=" + _backupRequested);
                Notify(_backupRequested
                    ? "The fleeing driver exited after the vehicle stopped and is contained for Backup."
                    : "The fleeing driver exited after stopping. The same Citizen remains in contact for document revalidation.");
                return;
            }

            _stage = backupAtScene
                ? InteractionStage.TrafficBackupContained
                : backupEnRoute ? InteractionStage.TrafficAwaitingBackup
                    : InteractionStage.TrafficFleeing;
            SetSubjectPoliceAware(_subject);
            if (!backupAtScene)
                IssueFootFleeTask(player, now);
            _log.Runtime("NPC_TRAFFIC_PURSUIT_CHANGED_TO_FOOT",
                "Ped=" + _subject.Handle
                + "; OriginalVehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                + "; PreviousVehicle=" + previousVehicle
                + "; PreviousInteractionStage=" + previousInteractionStage
                + "; NewInteractionStage=" + _stage
                + "; SameCitizen=true; RecordPreserved=" + (_currentRecord != null)
                + "; DecisionLocked=" + _citizenDocumentDecisionResolved
                + "; InjuryTracked=" + _subjectInjuries.ContainsKey(_subject.Handle)
                + "; BackupAssigned=" + _backupRequested);
            if (!backupAtScene)
                Notify("The same fleeing Citizen left the vehicle. Continue the on-foot pursuit; the contact and selected record remain active.");
        }

        private void MaintainTrafficBackupPursuitTransition(Ped player, DateTime now)
        {
            if (!_backupRequested || !_trafficContactFled || !IsUsable(_subject)
                || now < _nextTrafficBackupPursuitSampleAt)
                return;
            _nextTrafficBackupPursuitSampleAt = now.AddMilliseconds(
                TrafficBackupPursuitSampleMilliseconds);

            bool isInVehicle;
            try { isInVehicle = _subject.IsInVehicle(); }
            catch { return; }

            if (isInVehicle)
            {
                Vehicle currentVehicle = null;
                try { currentVehicle = _subject.CurrentVehicle; }
                catch { }
                if (!IsUsable(currentVehicle))
                {
                    if (_trafficVehicleTransitionStartedAt == DateTime.MinValue)
                    {
                        _trafficVehicleTransitionStartedAt = now;
                        _log.Runtime("NPC_TRAFFIC_BACKUP_VEHICLE_STATE_PENDING",
                            "Ped=" + _subject.Handle
                            + "; CurrentVehicleAvailable=false; ContactPreserved=true");
                    }
                    return;
                }

                _trafficVehicleTransitionStartedAt = DateTime.MinValue;
                if (_trafficPursuitStage == TrafficPursuitStage.OnFootFlight
                    || _trafficPursuitStage == TrafficPursuitStage.VehicleFlight
                    || _trafficPursuitStage == TrafficPursuitStage.VehicleStoppedAwaitingExit)
                {
                    bool wasStopped = _trafficPursuitStage
                        == TrafficPursuitStage.VehicleStoppedAwaitingExit;
                    bool vehicleChanged = !IsUsable(_trafficPursuitVehicle)
                        || _trafficPursuitVehicle.Handle != currentVehicle.Handle;
                    bool vehicleResumed = wasStopped
                        && Math.Abs(currentVehicle.Speed) > 1.0f;
                    if (_trafficPursuitStage == TrafficPursuitStage.OnFootFlight
                        || vehicleChanged || vehicleResumed)
                        TrackTrafficPursuitVehicle(currentVehicle, now);
                    if (vehicleResumed)
                        _log.Runtime("NPC_TRAFFIC_PURSUIT_VEHICLE_RESUMED",
                            "Ped=" + _subject.Handle + "; Vehicle=" + currentVehicle.Handle
                            + "; BackupAssigned=true; SameCitizen=true");
                }
                return;
            }

            _trafficVehicleTransitionStartedAt = DateTime.MinValue;
            if (_trafficPursuitStage == TrafficPursuitStage.VehicleFlight
                || _trafficPursuitStage == TrafficPursuitStage.VehicleStoppedAwaitingExit)
                BeginTrafficOnFootPursuit(player, now);
        }

        private void MaintainTrafficVehicleFlee(Ped player, Vehicle pursuitVehicle, DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(pursuitVehicle))
                return;
            float speed = Math.Abs(pursuitVehicle.Speed);
            if (now >= _lastFleeSampleAt.AddSeconds(1))
            {
                float moved = pursuitVehicle.Position.DistanceTo(_fleeLastPosition);
                _fleeLastPosition = pursuitVehicle.Position;
                _lastFleeSampleAt = now;
                if (!_fleeMovementConfirmed
                    && (speed >= FleeMovementSpeed || moved >= FleeMovementDistance))
                {
                    _fleeMovementConfirmed = true;
                    _log.Runtime("NPC_TRAFFIC_FLEE_MOVEMENT_CONFIRMED",
                        "Driver=" + _subject.Handle + "; Vehicle=" + pursuitVehicle.Handle
                        + "; Speed=" + speed.ToString("0.0"));
                    ReportInteractionAudio(
                        "lsimmersivelife.police.traffic.fleeing", "traffic-flee-movement-confirmed");
                    ReportInteractionAudio(
                        "lsimmersivelife.police.traffic.pursuit", "traffic-pursuit-movement-confirmed");
                    Notify("Traffic flee confirmed. Pursue the moving vehicle until it stops or the driver exits.");
                }
                _fleeStoppedAt = _fleeMovementConfirmed && speed <= 0.45f
                    ? (_fleeStoppedAt == DateTime.MinValue ? now : _fleeStoppedAt)
                    : DateTime.MinValue;
            }

            if (_trafficPursuitStage == TrafficPursuitStage.VehicleStoppedAwaitingExit
                && speed > 1.0f)
            {
                _trafficPursuitStage = TrafficPursuitStage.VehicleFlight;
                _fleeStoppedAt = DateTime.MinValue;
                _trafficPursuitExitAttempts = 0;
                _trafficPursuitExitFailureNotified = false;
                _nextTrafficPursuitExitAt = DateTime.MinValue;
                _log.Runtime("NPC_TRAFFIC_PURSUIT_VEHICLE_RESUMED",
                    "Ped=" + _subject.Handle + "; Vehicle=" + pursuitVehicle.Handle);
            }

            if (!_fleeMovementConfirmed && now >= _nextFleeTaskAt)
            {
                if (!_fleeRecoveryIssued)
                {
                    _fleeRecoveryIssued = true;
                    IssueTrafficFleeTask(player, now, 30.0f);
                }
                if (now >= _fleeStartedAt.AddSeconds(_settings.TrafficTaskRecoverySeconds + 4))
                {
                    SetSubjectPoliceAware(_subject);
                    if (_trafficPursuitStage != TrafficPursuitStage.VehicleStoppedAwaitingExit)
                    {
                        _trafficPursuitStage = TrafficPursuitStage.VehicleStoppedAwaitingExit;
                        _fleeStoppedAt = now;
                        _log.Runtime("NPC_TRAFFIC_FLEE_NOT_CONFIRMED",
                            "Driver=" + _subject.Handle + "; Vehicle=" + pursuitVehicle.Handle
                            + "; ContactPreserved=true");
                        Notify("The driver did not get away. The stopped Citizen contact remains available.");
                    }
                    if (_backupRequested)
                        _stage = InteractionStage.TrafficAwaitingBackup;
                    RequestStoppedTrafficDriverExit(pursuitVehicle, now);
                }
                return;
            }

            if (_fleeMovementConfirmed && _fleeStoppedAt != DateTime.MinValue
                && now >= _fleeStoppedAt.AddSeconds(2))
            {
                bool stopConfirmedNow = _trafficPursuitStage
                    != TrafficPursuitStage.VehicleStoppedAwaitingExit;
                if (stopConfirmedNow)
                {
                    _trafficPursuitStage = TrafficPursuitStage.VehicleStoppedAwaitingExit;
                    _nextTrafficPursuitExitAt = DateTime.MinValue;
                    _trafficPursuitExitAttempts = 0;
                    _trafficPursuitExitFailureNotified = false;
                    _log.Runtime("NPC_TRAFFIC_PURSUIT_VEHICLE_STOP_CONFIRMED",
                        "Ped=" + _subject.Handle + "; Vehicle=" + pursuitVehicle.Handle
                        + "; SameCitizen=true; BackupAssigned=" + _backupRequested);
                    ReportInteractionAudio(
                        "lsimmersivelife.police.traffic.vehicle_stopped",
                        "traffic-pursuit-stop-confirmed");
                }
                if (_backupRequested)
                {
                    _stage = InteractionStage.TrafficAwaitingBackup;
                    SetSubjectPoliceAware(_subject);
                    HoldTrafficSubject(pursuitVehicle);
                    if (stopConfirmedNow)
                        Notify("The fleeing vehicle stopped. Backup is continuing to the same tracked Citizen.");
                    return;
                }
                RequestStoppedTrafficDriverExit(pursuitVehicle, now);
            }
        }

        private void RequestStoppedTrafficDriverExit(Vehicle pursuitVehicle, DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(pursuitVehicle)
                || !_subject.IsInVehicle())
                return;
            try
            {
                HoldTrafficSubject(pursuitVehicle);
                if (_trafficPursuitExitAttempts < TrafficPursuitExitMaximumAttempts
                    && now >= _nextTrafficPursuitExitAt)
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE,
                        _subject, pursuitVehicle, 0);
                    _trafficPursuitExitAttempts++;
                    _nextTrafficPursuitExitAt = now.AddMilliseconds(
                        TrafficPursuitExitRetryMilliseconds);
                    _log.Runtime("NPC_TRAFFIC_PURSUIT_DRIVER_EXIT_REQUESTED",
                        "Ped=" + _subject.Handle + "; Vehicle=" + pursuitVehicle.Handle
                        + "; Attempt=" + _trafficPursuitExitAttempts
                        + "; SameCitizen=true");
                    return;
                }
                if (_trafficPursuitExitAttempts >= TrafficPursuitExitMaximumAttempts
                    && !_trafficPursuitExitFailureNotified)
                {
                    _trafficPursuitExitFailureNotified = true;
                    _log.Runtime("NPC_TRAFFIC_PURSUIT_DRIVER_EXIT_PENDING",
                        "Ped=" + _subject.Handle + "; Vehicle=" + pursuitVehicle.Handle
                        + "; Attempts=" + _trafficPursuitExitAttempts
                        + "; ContactPreserved=true");
                    Notify("The vehicle is stopped, but the driver has not exited. The same Citizen remains selected; reposition or continue the stop.");
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_PURSUIT_DRIVER_EXIT_FAILED", ex);
            }
        }

        private void MaintainTrafficOnFootFlee(Ped player, DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(player) || _subject.IsInVehicle())
                return;
            if (now >= _lastFleeSampleAt.AddSeconds(1))
            {
                float moved = _subject.Position.DistanceTo(_fleeLastPosition);
                _fleeLastPosition = _subject.Position;
                _lastFleeSampleAt = now;
                if (!_fleeMovementConfirmed && moved >= 1.5f)
                {
                    _fleeMovementConfirmed = true;
                    _log.Runtime("NPC_TRAFFIC_FOOT_FLEE_MOVEMENT_CONFIRMED",
                        "Ped=" + _subject.Handle + "; SameCitizen=true");
                }
                _fleeStoppedAt = _fleeMovementConfirmed && moved < 0.35f
                    ? (_fleeStoppedAt == DateTime.MinValue ? now : _fleeStoppedAt)
                    : DateTime.MinValue;
            }

            if (!_fleeMovementConfirmed
                && now >= _fleeStartedAt.AddSeconds(_settings.TrafficTaskRecoverySeconds + 4))
            {
                if (_backupRequested)
                {
                    if (now >= _nextFleeTaskAt)
                    {
                        IssueFootFleeTask(player, now);
                        _nextFleeTaskAt = now.AddSeconds(2);
                    }
                    return;
                }
                EnterTrafficPursuitContainment("NaturalStop");
                return;
            }

            if (_fleeMovementConfirmed && _fleeStoppedAt != DateTime.MinValue
                && now >= _fleeStoppedAt.AddSeconds(2))
            {
                if (_backupRequested)
                {
                    if (now >= _nextFleeTaskAt)
                    {
                        IssueFootFleeTask(player, now);
                        _nextFleeTaskAt = now.AddSeconds(2);
                    }
                    _fleeStoppedAt = now;
                    return;
                }
                EnterTrafficPursuitContainment("NaturalStop");
            }
        }

        private bool TryCompleteTrafficPlayerAimSurrender(Ped player, DateTime now)
        {
            bool eligible = _trafficContactFled
                && _trafficPursuitStage == TrafficPursuitStage.OnFootFlight
                && (_stage == InteractionStage.TrafficFleeing
                    || _stage == InteractionStage.TrafficAwaitingBackup)
                && IsUsable(_subject) && !_subject.IsDead && _subject.Health > 0
                && IsUsable(player)
                && !_subject.IsInVehicle()
                && _trafficVehicleTransitionStartedAt == DateTime.MinValue
                && player.Position.DistanceTo(_subject.Position)
                    <= TrafficAimSurrenderMaximumDistance;
            if (!eligible)
            {
                _trafficPlayerAimStartedAt = DateTime.MinValue;
                _trafficPlayerAimSubjectHandle = 0;
                return false;
            }

            bool enteringVehicle;
            bool activeAttack;
            bool aimingAtSubject;
            try
            {
                enteringVehicle = Function.Call<bool>(
                    Hash.IS_PED_GETTING_INTO_A_VEHICLE, _subject);
                activeAttack = _subject.IsShooting || _subject.IsInCombatAgainst(player);
                aimingAtSubject = Function.Call<bool>(
                    Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY,
                    Game.Player.Handle,
                    _subject);
            }
            catch (Exception ex)
            {
                _trafficPlayerAimStartedAt = DateTime.MinValue;
                _trafficPlayerAimSubjectHandle = 0;
                if (!_trafficPlayerAimCheckFailureLogged)
                {
                    _trafficPlayerAimCheckFailureLogged = true;
                    _log.Exception("NPC_TRAFFIC_PLAYER_AIM_SURRENDER_CHECK_FAILED", ex);
                }
                return false;
            }

            // A Citizen still entering a vehicle or actively attacking remains
            // in its existing flight/aggression branch. Aim must be sustained
            // after the selected driver is physically on foot and nonattacking.
            if (enteringVehicle || activeAttack || !aimingAtSubject)
            {
                _trafficPlayerAimStartedAt = DateTime.MinValue;
                _trafficPlayerAimSubjectHandle = 0;
                return false;
            }

            if (_trafficPlayerAimSubjectHandle != _subject.Handle
                || _trafficPlayerAimStartedAt == DateTime.MinValue)
            {
                _trafficPlayerAimSubjectHandle = _subject.Handle;
                _trafficPlayerAimStartedAt = now;
                return false;
            }

            int holdMilliseconds = LSConfig.Clamp(
                _settings.GunpointSurrenderHoldMilliseconds, 500, 5000);
            if (now < _trafficPlayerAimStartedAt.AddMilliseconds(holdMilliseconds))
                return false;

            int subjectHandle = _subject.Handle;
            EnterTrafficPursuitContainment("PlayerSustainedAim");
            if (_trafficPursuitStage != TrafficPursuitStage.Contained)
                return false;

            _log.Runtime("NPC_TRAFFIC_PURSUIT_PLAYER_GUNPOINT_SURRENDER",
                "Ped=" + subjectHandle
                + "; Player=" + player.Handle
                + "; AimHoldMilliseconds=" + holdMilliseconds
                + "; Distance=" + player.Position.DistanceTo(_subject.Position).ToString("0.0")
                + "; ActiveAttack=false; SameCitizen=true"
                + "; RecordPreserved=" + (_currentRecord != null)
                + "; BackupAssigned=" + _backupRequested);
            _trafficPlayerAimStartedAt = DateTime.MinValue;
            _trafficPlayerAimSubjectHandle = 0;
            return true;
        }

        private void EnterTrafficPursuitContainment(string reason)
        {
            if (!IsUsable(_subject) || _subject.IsDead
                || _trafficPursuitStage == TrafficPursuitStage.Contained)
                return;
            _trafficPursuitStage = TrafficPursuitStage.Contained;
            _stage = InteractionStage.TrafficRefused;
            SetSubjectPoliceAware(_subject);
            if (!IsLivingDowned(_subject) && !IsLivingInWrithe(_subject))
                _subject.Task.HandsUp(20000);
            _documentsProvided = false;
            _dispatcherValidationReceived = false;
            _citizenRecordReviewed = false;
            CitizenRecordVisible = false;
            _log.Runtime("NPC_TRAFFIC_PURSUIT_CONTAINED",
                "Ped=" + _subject.Handle
                + "; OriginalVehicle=" + (_subjectVehicle == null ? 0 : _subjectVehicle.Handle)
                + "; SameCitizen=true; RecordPreserved=" + (_currentRecord != null)
                + "; DecisionLocked=" + _citizenDocumentDecisionResolved
                + "; BackupAssigned=" + _backupRequested
                + "; Reason=" + (reason ?? "Unknown"));
            Notify(_backupRequested
                ? "The same fleeing Citizen yielded on foot. Backup remains assigned; continue the documented validation and response."
                : "The same fleeing Citizen stopped on foot and is contained. Continue the documented validation and response.");
        }

        private void MaintainTrafficResistance(Ped player, DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(player) || _subject.IsDead)
                return;
            if (now < _nextResistanceTaskAt)
                return;

            _nextResistanceTaskAt = now.AddMilliseconds(FootResistanceTaskRecoveryMilliseconds);
            try
            {
                SetSubjectPoliceAware(_subject);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;

                if (IsUsable(_subjectVehicle) && _subject.IsInVehicle()
                    && _subject.CurrentVehicle != null
                    && _subject.CurrentVehicle.Exists()
                    && _subject.CurrentVehicle.Handle == _subjectVehicle.Handle)
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, true);
                    Function.Call(Hash.TASK_LEAVE_VEHICLE,
                        _subject, _subjectVehicle, 0);
                    _log.RuntimeThrottled(
                        "NPC_TRAFFIC_RESISTANCE_EXIT_REFRESHED",
                        "Driver=" + _subject.Handle
                            + "; Vehicle=" + _subjectVehicle.Handle,
                        TimeSpan.FromSeconds(3));
                    return;
                }

                if (!_subject.IsInCombatAgainst(player) && !_subject.IsShooting)
                {
                    _subject.Task.Combat(player);
                    ObserveDeveloperTask(
                        "TASK_COMBAT",
                        _subject.Handle,
                        player.Handle,
                        _subjectVehicle == null ? 0 : _subjectVehicle.Handle,
                        "TrafficResistance");
                    _log.RuntimeThrottled(
                        "NPC_TRAFFIC_RESISTANCE_TASK_REFRESHED",
                        "Driver=" + _subject.Handle
                            + "; Player=" + player.Handle,
                        TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_RESISTANCE_TASK_FAILED", ex);
            }
        }

        private bool BeginTrafficStationFollow()
        {
            Vector3 destination;
            string stationName;
            if (!TryGetSelectedStation(out destination, out stationName)
                || !IsUsable(_subject) || !IsUsable(_subjectVehicle))
                return false;

            DateTime now = DateTime.UtcNow;
            try
            {
                ReleaseSubjectInteractionLockForMovement(_subject);
                _stage = InteractionStage.TrafficStationFollow;
                _trafficStationDestination = destination;
                _trafficStationName = stationName;
                _expiresAt = now.AddMilliseconds(StationFollowTimeoutMilliseconds);
                _nextStationTaskAt = now;
                IssueTrafficStationTask(now);
                _log.Runtime("NPC_TRAFFIC_STATION_FOLLOW_STARTED",
                    "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                    + "; Station=" + stationName);
                Notify("Driver remained compliant and is travelling to " + stationName + ". Follow the vehicle if you choose.");
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_STATION_FOLLOW_FAILED", ex);
                return false;
            }
        }

        private void MaintainTrafficStationFollow(DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle) || !IsDriver(_subject, _subjectVehicle))
            {
                ReleaseSubject(true);
                ClearInteractionState();
                Notify("Station direction ended because the driver or vehicle is unavailable.");
                return;
            }

            if (_subjectVehicle.Position.DistanceTo(_trafficStationDestination) <= StationArrivalRadius)
            {
                string stationName = _trafficStationName;
                int handle = _subject.Handle;
                ReleaseSubject(true);
                ClearInteractionState();
                _log.Runtime("NPC_TRAFFIC_STATION_FOLLOW_COMPLETED",
                    "Driver=" + handle + "; Station=" + stationName);
                Notify("Driver arrived at " + stationName + ". Traffic contact is complete.");
                return;
            }

            if (now >= _nextStationTaskAt)
                IssueTrafficStationTask(now);
        }

        private void IssueTrafficStationTask(DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle))
                return;
            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, false);
                _subject.Task.ClearAll();
                Function.Call(Hash.SET_DRIVER_ABILITY, _subject, 0.85f);
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, _subject, 0.15f);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    _subject, _subjectVehicle,
                    _trafficStationDestination.X, _trafficStationDestination.Y, _trafficStationDestination.Z,
                    14.0f, TrafficDrivingStyle, 8.0f);
                ObserveDeveloperTask(
                    "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE",
                    _subject.Handle,
                    0,
                    _subjectVehicle.Handle,
                    "TrafficStationFollow");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_STATION_TASK_FAILED", ex);
            }
            _nextStationTaskAt = now.AddSeconds(4);
        }

        private void IssueFootFleeTask(Ped player, DateTime now)
        {
            if (!IsUsable(_subject) || !IsUsable(player))
                return;
            try
            {
                ReleaseSubjectInteractionLockForMovement(_subject);
                _subject.Task.ClearAll();
                Function.Call(Hash.TASK_SMART_FLEE_PED, _subject, player, 70.0f, -1, false, false);
                ObserveDeveloperTask(
                    "TASK_SMART_FLEE_PED",
                    _subject.Handle,
                    player.Handle,
                    _subjectVehicle == null ? 0 : _subjectVehicle.Handle,
                    "FootFlee");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_FLEE_REISSUE_FAILED", ex);
            }
            _nextFleeTaskAt = now.AddSeconds(_settings.TrafficTaskRecoverySeconds);
        }

        private void IssueTrafficFleeTask(Ped player, DateTime now, float speed)
        {
            if (!IsUsable(_subject) || !IsUsable(player))
                return;
            Vehicle pursuitVehicle = IsUsable(_trafficPursuitVehicle)
                ? _trafficPursuitVehicle : _subjectVehicle;
            if (IsUsable(_subject) && _subject.IsInVehicle())
            {
                try
                {
                    Vehicle current = _subject.CurrentVehicle;
                    if (IsUsable(current))
                        pursuitVehicle = current;
                }
                catch { }
            }
            if (!IsUsable(pursuitVehicle))
                return;
            try
            {
                Vector3 destination = FindRoadDestinationAwayFrom(pursuitVehicle.Position, player.Position, 90.0f);
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, pursuitVehicle, false);
                pursuitVehicle.Speed = Math.Max(4.0f, Math.Abs(pursuitVehicle.Speed));
                _subject.Task.ClearAll();
                Function.Call(Hash.SET_DRIVER_ABILITY, _subject, 1.0f);
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, _subject, 0.75f);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    _subject, pursuitVehicle,
                    destination.X, destination.Y, destination.Z,
                    speed, TrafficDrivingStyle, 8.0f);
                ObserveDeveloperTask(
                    "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE",
                    _subject.Handle,
                    player.Handle,
                    pursuitVehicle.Handle,
                    "TrafficFlee");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_FLEE_TASK_FAILED", ex);
            }
            _nextFleeTaskAt = now.AddSeconds(_settings.TrafficTaskRecoverySeconds);
        }

        private static Vector3 FindRoadDestinationAwayFrom(Vector3 from, Vector3 threat, float distance)
        {
            float x = from.X - threat.X;
            float y = from.Y - threat.Y;
            float horizontalLength = (float)Math.Sqrt(x * x + y * y);
            if (horizontalLength < 0.5f)
            {
                x = 1.0f;
                y = 0.0f;
                horizontalLength = 1.0f;
            }
            Vector3 candidate = new Vector3(
                from.X + x / horizontalLength * distance,
                from.Y + y / horizontalLength * distance,
                from.Z);
            try { return World.GetNextPositionOnStreet(candidate); }
            catch { return candidate; }
        }

        private bool TryGetSelectedStation(out Vector3 destination, out string stationName)
        {
            destination = Vector3.Zero;
            stationName = string.Empty;
            if (_profile == null || _profile.Selection == null)
                return false;
            LSPDPoliceStationDefinition station = _profile.FindStation(_profile.Selection.StationId);
            if (station == null)
                return false;
            destination = new Vector3(station.ExteriorX, station.ExteriorY, station.ExteriorZ);
            stationName = station.DisplayName;
            return true;
        }

        private void ApplyLocalTrafficCaution(
            Ped player,
            DateTime now,
            bool activeRoadScene,
            bool protectInteraction)
        {
            // The player sample protects an ordinary foot/road patrol. One
            // additional sample rotates through current Backup, PoliceResponse,
            // Dispatch-scene, and custody anchors so traffic awareness follows
            // the actual local operation without a city-wide vehicle scan.
            if (!_trafficSettings.Enabled || now < _nextTrafficCaution)
                return;
            _nextTrafficCaution = now.AddMilliseconds(TrafficCautionMilliseconds);
            TrimTrafficApproachBrakeMemory(now);

            Ped operationAnchor;
            bool hasOperationAnchor = TryGetNextTrafficOperationAnchor(player, out operationAnchor);
            bool operationAnchorNearPlayer = hasOperationAnchor
                && IsUsable(player)
                && player.Position.DistanceTo(operationAnchor.Position)
                    <= ActiveSceneTrafficCautionRadius;
            // A remote escort or response unit gets its own local sample below.
            // It must not enlarge the Player-centered radius merely because the
            // operation exists elsewhere in the world.
            bool localPoliceScene = activeRoadScene || protectInteraction
                || operationAnchorNearPlayer;
            float radius = localPoliceScene
                ? ActiveSceneTrafficCautionRadius
                : LocalTrafficCautionRadius;
            int maximumInterventions = _settings.MaximumAffectedTrafficVehicles;
            if (maximumInterventions <= 0)
                return;

            _trafficDriversSeen.Clear();
            int interventions = 0;
            int anticipatoryBrakes = 0;
            // Give an active custody/response position a bounded share of the
            // intervention budget first, then reserve any remaining capacity
            // for the Player's immediate area. Quiet patrol still caps at one.
            int operationBudget = hasOperationAnchor
                ? Math.Min(2, maximumInterventions)
                : 0;
            if (hasOperationAnchor && operationBudget > 0)
            {
                Ped[] operationSample = World.GetNearbyPeds(
                    operationAnchor, ActiveSceneTrafficCautionRadius);
                if (operationSample != null)
                    interventions += ApplyTrafficSample(
                        operationSample, player, ActiveSceneTrafficCautionRadius,
                        now, true, false, operationBudget, ref anticipatoryBrakes);
            }

            int playerBudget = maximumInterventions - interventions;
            if (!localPoliceScene)
                playerBudget = Math.Min(1, playerBudget);
            if (playerBudget > 0)
            {
                Ped[] playerSample = World.GetNearbyPeds(player, radius);
                if (playerSample != null)
                    interventions += ApplyTrafficSample(
                        playerSample, player, radius, now, localPoliceScene,
                        activeRoadScene,
                        playerBudget, ref anticipatoryBrakes);
            }
        }

        private int ApplyTrafficSample(
            Ped[] nearby,
            Ped player,
            float radius,
            DateTime now,
            bool localPoliceScene,
            bool activeRoadScene,
            int maximumInterventions,
            ref int anticipatoryBrakes)
        {
            int interventions = 0;
            foreach (Ped ped in nearby)
            {
                if (interventions >= maximumInterventions)
                    break;
                if (!IsUsable(ped) || ped.Handle == player.Handle
                    || !ped.IsInVehicle() || !CanUseAsCivilian(ped, player)
                    || !_trafficDriversSeen.Add(ped.Handle))
                    continue;
                Vehicle vehicle = ped.CurrentVehicle;
                if (!IsUsable(vehicle) || vehicle == _subjectVehicle
                    || vehicle == player.CurrentVehicle || IsProtectedSceneVehicle(vehicle)
                    || !IsDriver(ped, vehicle) || WasRecentlyReleased(vehicle))
                    continue;

                Entity collisionTarget;
                float distance;
                if (!TryGetNearestProtectedCollisionTarget(
                    player, vehicle, radius, out collisionTarget, out distance))
                    continue;

                float impactSeconds = 0.0f;
                float predictedMissDistance = float.MaxValue;
                float requiredClearance = 0.0f;
                bool hasPredictedConflict = distance > ImmediateCollisionGuardRadius
                    && TryEstimateTrafficConflict(
                        vehicle, collisionTarget, out impactSeconds,
                        out predictedMissDistance, out requiredClearance);
                bool brakeIssued = false;
                if (hasPredictedConflict)
                {
                    DateTime nextBrake;
                    if (!_recentTrafficApproachBrakes.TryGetValue(vehicle.Handle, out nextBrake)
                        || now >= nextBrake)
                    {
                        try
                        {
                            // Action 27 is the existing short brake action used
                            // by this owner. It ends on its own; ambient driving
                            // is never permanently speed-capped or rerouted here.
                            Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION,
                                ped, vehicle, 27, TrafficApproachBrakeMilliseconds);
                            _recentTrafficApproachBrakes[vehicle.Handle] =
                                now.AddMilliseconds(TrafficApproachBrakeCooldownMilliseconds);
                            brakeIssued = true;
                            anticipatoryBrakes++;
                            _log.RuntimeThrottled(
                                "NPC_TRAFFIC_APPROACH_BRAKE",
                                "Driver=" + ped.Handle
                                + "; Vehicle=" + vehicle.Handle
                                + "; ModelHash=" + vehicle.Model.Hash
                                + "; Target=" + collisionTarget.Handle
                                + "; TargetType=" + (collisionTarget is Vehicle ? "Vehicle" : "Ped")
                                + "; Distance=" + distance.ToString("0.0")
                                + "; Speed=" + Math.Abs(vehicle.Speed).ToString("0.0")
                                + "; TimeToClosest=" + impactSeconds.ToString("0.00")
                                + "; MissDistance=" + predictedMissDistance.ToString("0.0")
                                + "; RequiredClearance=" + requiredClearance.ToString("0.0")
                                + "; Task=TASK_VEHICLE_TEMP_ACTION:27; DurationMs="
                                + TrafficApproachBrakeMilliseconds,
                                TimeSpan.FromSeconds(1));
                        }
                        catch (Exception ex)
                        {
                            _log.Exception("NPC_TRAFFIC_APPROACH_BRAKE_FAILED", ex);
                        }
                    }
                }

                bool imminentPredictedConflict = hasPredictedConflict
                    && impactSeconds <= ImminentTrafficImpactWindowSeconds;
                if (!imminentPredictedConflict && !IsTrafficCollisionRisk(
                    collisionTarget, vehicle, distance, localPoliceScene))
                {
                    if (brakeIssued)
                        interventions++;
                    continue;
                }
                ApplyTrafficCollisionGuard(
                    collisionTarget, ped, vehicle, distance, now,
                    activeRoadScene, imminentPredictedConflict,
                    impactSeconds, predictedMissDistance, requiredClearance);
                interventions++;
            }
            return interventions;
        }

        private bool TryGetNextTrafficOperationAnchor(Ped player, out Ped anchor)
        {
            anchor = null;
            int total = _trafficOperationActors.Count + _trafficOperationVehicles.Count;
            if (total <= 0)
                return false;
            for (int attempt = 0; attempt < total; attempt++)
            {
                int index = _trafficOperationAnchorCursor % total;
                _trafficOperationAnchorCursor = (index + 1) % total;
                if (index < _trafficOperationActors.Count)
                {
                    Ped candidate = _trafficOperationActors[index];
                    if (IsUsable(candidate)
                        && (player == null || candidate.Handle != player.Handle))
                    {
                        anchor = candidate;
                        return true;
                    }
                    continue;
                }

                Vehicle vehicle = _trafficOperationVehicles[index - _trafficOperationActors.Count];
                if (!IsUsable(vehicle))
                    continue;
                Ped driver = vehicle.Driver;
                if (IsUsable(driver) && (player == null || driver.Handle != player.Handle))
                {
                    anchor = driver;
                    return true;
                }
            }
            return false;
        }

        private bool TryEstimateTrafficConflict(
            Vehicle trafficVehicle,
            Entity target,
            out float timeToClosest,
            out float missDistance,
            out float requiredClearance)
        {
            timeToClosest = 0.0f;
            missDistance = float.MaxValue;
            requiredClearance = 0.0f;
            if (!IsUsable(trafficVehicle) || target == null || !target.Exists())
                return false;

            try
            {
                Vector3 relativeVelocity = trafficVehicle.Velocity - target.Velocity;
                float relativeSpeedSquared = relativeVelocity.X * relativeVelocity.X
                    + relativeVelocity.Y * relativeVelocity.Y;
                if (relativeSpeedSquared < 4.0f)
                    return false;

                Vector3 separation = target.Position - trafficVehicle.Position;
                float approachDot = separation.X * relativeVelocity.X
                    + separation.Y * relativeVelocity.Y;
                timeToClosest = approachDot / relativeSpeedSquared;
                if (timeToClosest < 0.1f
                    || timeToClosest > MaximumTrafficImpactLookAheadSeconds)
                    return false;

                float missX = separation.X - relativeVelocity.X * timeToClosest;
                float missY = separation.Y - relativeVelocity.Y * timeToClosest;
                missDistance = (float)Math.Sqrt(missX * missX + missY * missY);
                float verticalSeparation = separation.Z
                    - relativeVelocity.Z * timeToClosest;
                if (Math.Abs(verticalSeparation) > MaximumTrafficVerticalSeparation)
                    return false;
                requiredClearance = GetTrafficSafetyRadius(trafficVehicle)
                    + GetTrafficSafetyRadius(target) + 0.75f;
                return missDistance <= requiredClearance;
            }
            catch
            {
                return false;
            }
        }

        private float GetTrafficSafetyRadius(Entity entity)
        {
            Vehicle vehicle = entity as Vehicle;
            if (vehicle == null)
            {
                Ped ped = entity as Ped;
                if (IsUsable(ped) && ped.IsInVehicle())
                    vehicle = ped.CurrentVehicle;
            }
            if (!IsUsable(vehicle))
                return 1.0f;

            int modelHash = vehicle.Model.Hash;
            float cached;
            if (_trafficVehicleSafetyRadii.TryGetValue(modelHash, out cached))
                return cached;

            float radius = vehicle.Model.IsBike ? 1.8f : 3.5f;
            try
            {
                using (OutputArgument minimumOutput = new OutputArgument())
                using (OutputArgument maximumOutput = new OutputArgument())
                {
                    Function.Call(Hash.GET_MODEL_DIMENSIONS,
                        modelHash, minimumOutput, maximumOutput);
                    Vector3 minimum = minimumOutput.GetResult<Vector3>();
                    Vector3 maximum = maximumOutput.GetResult<Vector3>();
                    float width = Math.Abs(maximum.X - minimum.X);
                    float length = Math.Abs(maximum.Y - minimum.Y);
                    float measured = 0.5f * (float)Math.Sqrt(width * width + length * length);
                    if (!float.IsNaN(measured) && !float.IsInfinity(measured) && measured > 0.0f)
                        radius = Math.Max(radius, Math.Min(8.0f, measured));
                }
            }
            catch
            {
                // Keep the conservative bike/car fallback if model bounds are
                // unavailable; a failed optional size probe is not a hard gate.
            }

            if (_trafficVehicleSafetyRadii.Count < 64)
                _trafficVehicleSafetyRadii[modelHash] = radius;
            return radius;
        }

        private bool TryGetNearestProtectedCollisionTarget(
            Ped player,
            Vehicle trafficVehicle,
            float radius,
            out Entity target,
            out float distance)
        {
            target = null;
            distance = float.MaxValue;
            foreach (Entity candidate in ProtectedCollisionTargets(player))
            {
                if (candidate == null || !candidate.Exists())
                    continue;
                float candidateDistance = trafficVehicle.Position.DistanceTo(candidate.Position);
                if (candidateDistance > radius || candidateDistance >= distance)
                    continue;
                target = candidate;
                distance = candidateDistance;
            }
            return target != null;
        }

        private IEnumerable<Entity> ProtectedCollisionTargets(Ped player)
        {
            if (IsUsable(player))
                yield return player;
            if (IsUsable(player) && IsUsable(player.CurrentVehicle))
                yield return player.CurrentVehicle;
            // The active NPC contact is part of the same small local safety
            // bubble as the officer. Without this boundary, ordinary traffic
            // can hit a compliant citizen while Backup is staging or loading.
            if (IsUsable(_subject))
                yield return _subject;
            if (IsUsable(_subjectVehicle))
                yield return _subjectVehicle;
            foreach (Ped actor in _protectedSceneActors.Values)
                if (IsUsable(actor) && (player == null || actor.Handle != player.Handle))
                    yield return actor;
            foreach (Vehicle vehicle in _protectedSceneVehicles.Values)
                if (IsUsable(vehicle))
                    yield return vehicle;
        }

        private void ApplyPolicePresenceReaction(Ped player, DateTime now, bool activeRoadScene)
        {
            // Aiming alone is a calm stop/look response, not a random flee roll.
            // Only nearby ordinary civilians are considered; protected incident
            // actors, Police units, and active Gang threats remain with owners.
            if (HasActiveInteraction
                || now < _nextSceneReactionScan)
                return;
            _nextSceneReactionScan = now.AddMilliseconds(SceneReactionScanMilliseconds);

            float radius = activeRoadScene
                ? ActiveSceneCivilianRadius
                : Math.Min(9.0f, ActiveSceneCivilianRadius);
            Ped[] nearby = World.GetNearbyPeds(player, radius);
            if (nearby == null)
                return;

            Ped gunfireThreat = FindNearestGunfireSource(player, nearby);
            bool nearbyGunfire = gunfireThreat != null;
            bool aimingWithoutFire = !nearbyGunfire && player.IsAiming && !player.IsShooting;
            _sceneReactionPedsSeen.Clear();

            int budget = nearbyGunfire
                ? Math.Min(4, _settings.MaximumAffectedPedestrians)
                : Math.Min(2, _settings.MaximumAffectedPedestrians);
            foreach (Ped ped in nearby)
            {
                if (budget <= 0)
                    break;
                if (!CanUseAsCivilian(ped, player) || ped.IsInVehicle()
                    || !_sceneReactionPedsSeen.Add(ped.Handle))
                    continue;
                if (_subject != null && ped.Handle == _subject.Handle)
                    continue;

                bool attackedOfficer = ped.IsInCombatAgainst(player);
                bool closeApproach = !nearbyGunfire && !aimingWithoutFire
                    && !attackedOfficer && IsCivilianApproachingOfficer(ped, player);
                float officerDistance = ped.Position.DistanceTo(player.Position);
                bool presenceNotice = !nearbyGunfire && !aimingWithoutFire
                    && !attackedOfficer && !closeApproach && !ped.IsInCombat
                    && officerDistance <= CivilianPresenceNoticeRadius;
                if (!nearbyGunfire && !aimingWithoutFire
                    && !attackedOfficer && !closeApproach && !presenceNotice)
                    continue;
                if (ped.IsInCombat && !attackedOfficer)
                    continue;

                DateTime nextReaction;
                if (_recentSceneReactions.TryGetValue(ped.Handle, out nextReaction)
                    && now < nextReaction)
                    continue;
                _recentSceneReactions[ped.Handle] = now.AddMilliseconds(SceneReactionCooldownMilliseconds);
                try
                {
                    string action;
                    int duration;
                    if (nearbyGunfire && ped.Handle != gunfireThreat.Handle)
                    {
                        duration = CivilianRetreatMilliseconds;
                        Function.Call(Hash.TASK_SMART_FLEE_PED,
                            ped, gunfireThreat, 35.0f, duration, false, false);
                        action = "RetreatFromGunfire";
                    }
                    else if (aimingWithoutFire || attackedOfficer)
                    {
                        duration = CalmCivilianPauseMilliseconds;
                        ped.Task.LookAt(player, duration);
                        ped.Task.StandStill(duration);
                        action = "CalmPauseAndAcknowledge";
                    }
                    else if (presenceNotice)
                    {
                        duration = CalmCivilianNoticeMilliseconds;
                        ped.Task.LookAt(player, duration);
                        action = "BriefPolicePresenceNotice";
                    }
                    else
                    {
                        duration = CalmCivilianStepBackMilliseconds;
                        MoveCivilianOneStepBack(ped, player);
                        action = "WalkOutOfOfficerSpace";
                    }
                    _log.Runtime(
                        "NPC_AUTHORITY_CIVILIAN_RESPONSE",
                        "Ped=" + ped.Handle
                        + "; Trigger=" + (nearbyGunfire ? "Gunfire" : aimingWithoutFire
                            ? "AimWithoutFire" : attackedOfficer ? "CivilianAttack"
                            : "CloseApproach")
                        + "; Action=" + action
                        + "; Threat=" + (gunfireThreat == null ? 0 : gunfireThreat.Handle)
                        + "; TaskDurationMs=" + duration
                        + "; Context=" + (activeRoadScene ? "ActiveRoadScene" : "PoliceDuty"));
                    budget--;
                }
                catch (Exception ex)
                {
                    _log.Exception(
                        "NPC_AUTHORITY_CIVILIAN_RESPONSE_FAILED_PED_" + ped.Handle,
                        ex);
                }
            }
        }

        private Ped FindNearestGunfireSource(Ped player, Ped[] nearby)
        {
            if (IsUsable(player) && player.IsShooting)
                return player;
            Ped result = null;
            float nearest = float.MaxValue;
            foreach (Ped ped in nearby)
            {
                if (!IsUsable(ped) || !ped.IsShooting)
                    continue;
                float distance = player.Position.DistanceTo(ped.Position);
                if (distance >= nearest)
                    continue;
                nearest = distance;
                result = ped;
            }
            return result;
        }

        private bool IsCivilianApproachingOfficer(Ped ped, Ped player)
        {
            if (!IsUsable(ped) || !IsUsable(player) || player.IsInVehicle())
                return false;
            Vector3 towardPlayer = player.Position - ped.Position;
            float distance = (float)Math.Sqrt(
                towardPlayer.X * towardPlayer.X + towardPlayer.Y * towardPlayer.Y);
            if (distance > CivilianInterferenceRadius || distance < 0.1f)
                return false;
            Vector3 velocity = ped.Velocity;
            float speed = (float)Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
            if (speed < 0.35f)
                return false;
            float toward = (velocity.X * towardPlayer.X + velocity.Y * towardPlayer.Y)
                / (speed * distance);
            return toward >= 0.20f;
        }

        private void MoveCivilianOneStepBack(Ped ped, Ped player)
        {
            Vector3 away = ped.Position - player.Position;
            float length = (float)Math.Sqrt(away.X * away.X + away.Y * away.Y);
            if (length < 0.1f)
            {
                Vector3 forward = player.ForwardVector;
                away = new Vector3(-forward.X, -forward.Y, 0.0f);
                length = (float)Math.Sqrt(away.X * away.X + away.Y * away.Y);
            }
            if (length < 0.1f)
                return;
            Vector3 destination = ped.Position + new Vector3(
                away.X / length * CivilianStepBackDistance,
                away.Y / length * CivilianStepBackDistance,
                0.0f);
            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                ped,
                destination.X, destination.Y, destination.Z,
                0.9f,
                CalmCivilianStepBackMilliseconds,
                0.35f,
                false,
                ped.Heading);
        }

        private void ApplyPoliceDeescalation(Ped player, DateTime now)
        {
            Ped[] nearby = World.GetNearbyPeds(player, 9.0f);
            if (nearby == null)
                return;
            foreach (Ped ped in nearby)
            {
                if (!CanUseAsCivilian(ped, player) || ped.IsDead || !ped.IsInCombatAgainst(player))
                    continue;
                DateTime until;
                if (_recentDeescalations.TryGetValue(ped.Handle, out until) && now < until)
                    continue;
                _recentDeescalations[ped.Handle] = now.AddSeconds(6);
                try
                {
                    if (player.IsAiming && !player.IsShooting)
                    {
                        ped.Task.LookAt(player, CalmCivilianPauseMilliseconds);
                        ped.Task.StandStill(CalmCivilianPauseMilliseconds);
                        _log.Runtime("NPC_POLICE_DEESCALATION",
                            "Ped=" + ped.Handle + "; Trigger=AttackStoppedByAim"
                            + "; Action=CalmPause; TaskDurationMs="
                            + CalmCivilianPauseMilliseconds);
                    }
                    else
                    {
                        Function.Call(Hash.TASK_SMART_FLEE_PED,
                            ped, player, 25.0f, CivilianRetreatMilliseconds, false, false);
                        _log.Runtime("NPC_POLICE_DEESCALATION",
                            "Ped=" + ped.Handle + "; Trigger=ActualCivilianAttack"
                            + "; Action=BoundedRetreat; TaskDurationMs="
                            + CivilianRetreatMilliseconds);
                    }
                }
                catch (Exception ex) { _log.Exception("NPC_POLICE_DEESCALATION_FAILED", ex); }
            }
        }

        private void MaintainApproachCandidate(Ped player, DateTime now, bool contactBlocked)
        {
            if (contactBlocked || HasActiveInteraction
                || player.IsAiming || player.IsShooting)
            {
                ReleaseApproachCandidate();
                return;
            }

            if (player.IsInVehicle())
            {
                MaintainTrafficApproachCandidate(player, now);
                return;
            }

            if (IsUsable(_approachCandidate) && now < _approachCandidateUntil)
            {
                if (_approachCandidateSuspicious)
                {
                    MaintainSuspiciousApproachCandidate(player, now);
                    return;
                }
                if (!_approachCandidateStopped
                    && _approachCandidate.Position.DistanceTo(player.Position) <= 4.5f)
                {
                    try
                    {
                        _approachCandidate.Task.StandStill(4200);
                        OrientSubjectToOfficer(_approachCandidate, player, 1600);
                        _approachCandidateStopped = true;
                    }
                    catch (Exception ex) { _log.Exception("NPC_APPROACH_AWARENESS_FAILED", ex); }
                }
                return;
            }

            ReleaseApproachCandidate();
            if (now < _nextApproachScan)
                return;
            _nextApproachScan = now.AddMilliseconds(ApproachScanMilliseconds);

            Ped best = null;
            float bestDistance = float.MaxValue;
            Ped[] nearby = World.GetNearbyPeds(player, 8.0f);
            if (nearby == null)
                return;
            foreach (Ped ped in nearby)
            {
                if (!CanUseAsCivilian(ped, player) || ped.IsInVehicle() || ped.IsInCombat)
                    continue;
                DateTime cooldown;
                if (_recentApproachCandidates.TryGetValue(ped.Handle, out cooldown) && now < cooldown)
                    continue;
                float distance = ped.Position.DistanceTo(player.Position);
                if (distance < 2.0f || distance > 7.5f || distance >= bestDistance)
                    continue;
                best = ped;
                bestDistance = distance;
            }
            if (best == null)
                return;

            _approachCandidate = best;
            _approachCandidateStopped = false;
            _approachCandidateTraffic = false;
            _approachCandidateTrafficTaskIssued = false;
            _approachCandidateSuspicious = _random.Next(100)
                < _settings.SuspiciousEncounterChancePercent;
            _nextSuspiciousApproachActionAt = now;
            _approachCandidateUntil = now.AddMilliseconds(ApproachCandidateMilliseconds);
            _recentApproachCandidates[best.Handle] = now.AddSeconds(10);
            try
            {
                if (!_approachCandidateSuspicious && bestDistance <= 4.5f)
                {
                    best.Task.StandStill(4200);
                    _approachCandidateStopped = true;
                }
                OrientSubjectToOfficer(best, player, 1800);
            }
            catch (Exception ex) { _log.Exception("NPC_APPROACH_CANDIDATE_FAILED", ex); }
            _log.Runtime("NPC_APPROACH_CANDIDATE",
                "Ped=" + best.Handle + "; Suspicious=" + _approachCandidateSuspicious);
        }

        private void MaintainTrafficApproachCandidate(Ped player, DateTime now)
        {
            Vehicle patrolVehicle = player.CurrentVehicle;
            if (!IsUsable(patrolVehicle) || !IsDriver(player, patrolVehicle)
                || Math.Abs(patrolVehicle.Speed) > 14.0f)
            {
                ReleaseApproachCandidate();
                return;
            }

            if (IsUsable(_approachCandidate) && _approachCandidateTraffic
                && now < _approachCandidateUntil
                && _approachCandidate.IsInVehicle()
                && IsUsable(_approachCandidate.CurrentVehicle))
            {
                MaintainSuspiciousTrafficApproachCandidate(player, now);
                return;
            }

            ReleaseApproachCandidate();
            if (now < _nextApproachScan)
                return;
            _nextApproachScan = now.AddMilliseconds(ApproachScanMilliseconds);

            Ped best = null;
            Vehicle bestVehicle = null;
            float bestDistance = float.MaxValue;
            Ped[] nearby = World.GetNearbyPeds(player, 18.0f);
            if (nearby == null)
                return;

            foreach (Ped ped in nearby)
            {
                if (!CanUseAsCivilian(ped, player) || !ped.IsInVehicle()
                    || ped.IsInCombat)
                    continue;
                Vehicle vehicle = ped.CurrentVehicle;
                if (!IsUsable(vehicle) || vehicle.Handle == patrolVehicle.Handle
                    || !IsDriver(ped, vehicle))
                    continue;
                float distance = vehicle.Position.DistanceTo(player.Position);
                float speed = Math.Abs(vehicle.Speed);
                if (distance < 5.0f || distance > 18.0f
                    || speed < 1.0f || speed > 22.0f
                    || distance >= bestDistance)
                    continue;
                DateTime cooldown;
                if (_recentApproachCandidates.TryGetValue(ped.Handle, out cooldown)
                    && now < cooldown)
                    continue;
                best = ped;
                bestVehicle = vehicle;
                bestDistance = distance;
            }

            if (best == null
                || _random.Next(100) >= _settings.SuspiciousEncounterChancePercent)
                return;

            _approachCandidate = best;
            _approachCandidateTraffic = true;
            _approachCandidateTrafficTaskIssued = false;
            _approachCandidateSuspicious = true;
            _approachCandidateStopped = false;
            _nextSuspiciousApproachActionAt = now;
            _approachCandidateUntil = now.AddMilliseconds(ApproachCandidateMilliseconds);
            _recentApproachCandidates[best.Handle] = now.AddSeconds(10);
            _log.Runtime("NPC_TRAFFIC_SUSPICIOUS_APPROACH_CANDIDATE",
                "Driver=" + best.Handle + "; Vehicle=" + bestVehicle.Handle
                + "; Distance=" + bestDistance.ToString("0.0")
                + "; Behavior=SlowDepartureLookBack");
            MaintainSuspiciousTrafficApproachCandidate(player, now);
        }

        private void MaintainSuspiciousTrafficApproachCandidate(Ped player, DateTime now)
        {
            Ped driver = _approachCandidate;
            Vehicle vehicle = driver == null ? null : driver.CurrentVehicle;
            if (!IsUsable(driver) || !IsUsable(vehicle) || !driver.IsInVehicle()
                || !IsDriver(driver, vehicle) || !IsUsable(player))
            {
                ReleaseApproachCandidate();
                return;
            }

            float distance = vehicle.Position.DistanceTo(player.Position);
            if (now >= _nextSuspiciousApproachActionAt)
            {
                _nextSuspiciousApproachActionAt = now.AddMilliseconds(
                    SuspiciousApproachTaskRecoveryMilliseconds);
                try
                {
                    Function.Call(Hash.SET_PED_MOVE_RATE_OVERRIDE, driver, 1.08f);
                    if (!_approachCandidateTrafficTaskIssued)
                    {
                        Vector3 destination = FindRoadDestinationAwayFrom(
                            vehicle.Position, player.Position, 65.0f);
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, false);
                        Function.Call(Hash.SET_DRIVER_ABILITY, driver, 0.82f);
                        Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 0.18f);
                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                            driver, vehicle,
                            destination.X, destination.Y, destination.Z,
                            11.0f, TrafficDrivingStyle, 8.0f);
                        _approachCandidateTrafficTaskIssued = true;
                        _log.Runtime("NPC_TRAFFIC_SUSPICIOUS_DRIVER_MOVED_AWAY",
                            "Driver=" + driver.Handle + "; Vehicle=" + vehicle.Handle
                            + "; RoadTarget=" + destination);
                    }
                    _log.RuntimeThrottled(
                        "NPC_TRAFFIC_SUSPICIOUS_DRIVER_WATCHING",
                        "Driver=" + driver.Handle + "; Distance=" + distance.ToString("0.0"),
                        TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_TRAFFIC_SUSPICIOUS_BEHAVIOR_FAILED", ex);
                }
            }

            // Suspicious traffic can behave cautiously during normal patrol,
            // but proximity alone is not the Player's decision to start a stop.
            // BeginTrafficInteraction remains on the explicit E/controller/
            // horn paths so a passing driver cannot claim Citizen ownership
            // and block a Dispatch request.
        }

        private void MaintainSuspiciousApproachCandidate(Ped player, DateTime now)
        {
            if (!IsUsable(_approachCandidate) || !IsUsable(player))
                return;

            float distance = _approachCandidate.Position.DistanceTo(player.Position);
            if (now >= _nextSuspiciousApproachActionAt)
            {
                _nextSuspiciousApproachActionAt = now.AddMilliseconds(
                    SuspiciousApproachTaskRecoveryMilliseconds);
                try
                {
                    // Keep the ambient walking task alive, but make the
                    // selected citizen visibly uneasy: a faster walk rate and
                    // a bounded look-back. No per-frame task replacement.
                    Function.Call(Hash.SET_PED_MOVE_RATE_OVERRIDE,
                        _approachCandidate, 1.18f);
                    Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                        _approachCandidate, player, 700);
                    _approachCandidate.Task.LookAt(player, 900);
                    _log.RuntimeThrottled(
                        "NPC_FOOT_SUSPICIOUS_LOOK_BACK",
                        "Ped=" + _approachCandidate.Handle
                            + "; Distance=" + distance.ToString("0.0"),
                        TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_FOOT_SUSPICIOUS_BEHAVIOR_FAILED", ex);
                }
            }

            // Keep this as ambient awareness only. A nearby suspicious
            // pedestrian does not become an active Citizen interaction unless
            // the Player explicitly starts contact through the existing input.
        }

        private void ReleaseApproachCandidate()
        {
            if (_approachCandidateSuspicious && IsUsable(_approachCandidate))
            {
                try
                {
                    Function.Call(Hash.SET_PED_MOVE_RATE_OVERRIDE,
                        _approachCandidate, 1.0f);
                }
                catch { }
            }
            _approachCandidate = null;
            _approachCandidateUntil = DateTime.MinValue;
            _approachCandidateStopped = false;
            _approachCandidateSuspicious = false;
            _approachCandidateTraffic = false;
            _approachCandidateTrafficTaskIssued = false;
            _nextSuspiciousApproachActionAt = DateTime.MinValue;
        }

        private bool IsTrafficCollisionRisk(
            Entity target,
            Vehicle vehicle,
            float distance,
            bool activeRoadScene)
        {
            if (target == null || !target.Exists() || !IsUsable(vehicle))
                return false;
            float speed = Math.Abs(vehicle.Speed);
            if (speed < 1.5f)
                return false;
            if (distance > (activeRoadScene ? ActiveSceneTrafficCautionRadius : LocalTrafficCautionRadius))
                return false;
            try
            {
                Vector3 towardTarget = target.Position - vehicle.Position;
                float towardLength = (float)Math.Sqrt(
                    towardTarget.X * towardTarget.X + towardTarget.Y * towardTarget.Y);
                if (towardLength < 0.25f)
                    return distance <= ImmediateCollisionGuardRadius;
                Vector3 velocity = vehicle.Velocity;
                float velocityLength = (float)Math.Sqrt(
                    velocity.X * velocity.X + velocity.Y * velocity.Y);
                if (velocityLength < 0.25f)
                {
                    velocity = vehicle.ForwardVector;
                    velocityLength = (float)Math.Sqrt(
                        velocity.X * velocity.X + velocity.Y * velocity.Y);
                }
                if (velocityLength < 0.25f)
                    return false;
                float dot = (velocity.X * towardTarget.X + velocity.Y * towardTarget.Y)
                    / (velocityLength * towardLength);

                // Nearness alone is not a collision risk. The old eight-metre
                // unconditional branch repeatedly guarded ordinary passing
                // traffic. Require real closing motion; the tiny emergency
                // radius allows a slightly wider angle only at imminent impact.
                if (distance <= ImmediateCollisionGuardRadius)
                    return dot >= 0.00f;
                return dot >= (activeRoadScene ? 0.20f : 0.45f);
            }
            catch
            {
                return distance <= ImmediateCollisionGuardRadius;
            }
        }

        private void ApplyTrafficCollisionGuard(
            Entity target,
            Ped driver,
            Vehicle vehicle,
            float distance,
            DateTime now,
            bool activeRoadScene,
            bool imminentPredictedConflict,
            float impactSeconds,
            float predictedMissDistance,
            float requiredClearance)
        {
            try
            {
                if (distance <= ImmediateCollisionGuardRadius
                    || imminentPredictedConflict)
                {
                    // Do not slow every vehicle in the awareness radius.  A
                    // speed cap on the whole twelve-to-twenty-eight metre
                    // sample made normal traffic crawl whenever Police had an
                    // active remote owner. Use the existing target-specific
                    // guard either at immediate proximity or when the local
                    // path prediction says impact is within 1.5 seconds.
                    // A predicted convoy conflict creeps at walking pace until
                    // the 500 ms sample no longer sees the protected target in
                    // its path; ordinary traffic remains untouched.
                    float cappedSpeed = imminentPredictedConflict
                        ? PredictedTrafficCautionSpeed
                        : activeRoadScene
                            ? ActiveSceneTrafficCautionSpeed
                            : LocalTrafficCautionSpeed;
                    vehicle.Speed = Math.Min(Math.Abs(vehicle.Speed), cappedSpeed);
                    Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, vehicle, target, true);
                    _collisionGuards[vehicle.Handle] = new CollisionGuard
                    {
                        Vehicle = vehicle,
                        Target = target,
                        ExpiresAt = now.AddMilliseconds(CollisionGuardMilliseconds)
                    };
                    // This guard may be re-applied while traffic remains close
                    // to the player. Preserve its evidence without synchronously
                    // writing a disk record every scan/tick.
                    _log.RuntimeThrottled(
                        "NPC_TRAFFIC_COLLISION_GUARD",
                        "Driver=" + driver.Handle
                        + "; Vehicle=" + vehicle.Handle
                        + "; ModelHash=" + vehicle.Model.Hash
                        + "; Target=" + target.Handle
                        + "; TargetType=" + (target is Vehicle ? "Vehicle" : "Ped")
                        + "; Distance=" + distance.ToString("0.0")
                        + "; Speed=" + Math.Abs(vehicle.Speed).ToString("0.0")
                        + "; Trigger=" + (imminentPredictedConflict
                            ? "PredictedImpact" : "ImmediateProximity")
                        + (imminentPredictedConflict
                            ? "; TimeToClosest=" + impactSeconds.ToString("0.00")
                                + "; MissDistance=" + predictedMissDistance.ToString("0.0")
                                + "; RequiredClearance=" + requiredClearance.ToString("0.0")
                            : string.Empty)
                        + "; Task=EmergencySpeedCap+NoCollision; DurationMs="
                        + CollisionGuardMilliseconds,
                        TimeSpan.FromSeconds(2));
                }

            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_COLLISION_GUARD_FAILED", ex);
            }
        }

        private void MaintainCollisionGuards(Ped player, DateTime now)
        {
            if (_collisionGuards.Count == 0)
                return;
            var expired = new List<int>();
            foreach (KeyValuePair<int, CollisionGuard> pair in _collisionGuards)
            {
                CollisionGuard guard = pair.Value;
                if (guard == null || !IsUsable(guard.Vehicle)
                    || guard.Target == null || !guard.Target.Exists())
                {
                    expired.Add(pair.Key);
                    continue;
                }
                if (now < guard.ExpiresAt)
                {
                    // The native's final argument makes the relationship valid
                    // for the current frame only. Refreshing it here creates a
                    // short local shield without permanently disabling vehicle
                    // collision after Police Authority ends.
                    try
                    {
                        if (guard.Target != null && guard.Target.Exists())
                            Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY,
                                guard.Vehicle, guard.Target, true);
                    }
                    catch (Exception ex)
                    {
                        _log.Exception("NPC_TRAFFIC_COLLISION_GUARD_REFRESH_FAILED", ex);
                    }
                    continue;
                }
                expired.Add(pair.Key);
            }
            foreach (int handle in expired)
                _collisionGuards.Remove(handle);
        }

        private static float TrafficDirectionPenalty(Vehicle playerVehicle, Vehicle candidateVehicle)
        {
            if (!IsUsable(playerVehicle) || !IsUsable(candidateVehicle))
                return 0.0f;
            try
            {
                Vector3 towardCandidate = candidateVehicle.Position - playerVehicle.Position;
                float length = (float)Math.Sqrt(
                    towardCandidate.X * towardCandidate.X + towardCandidate.Y * towardCandidate.Y);
                if (length < 0.25f)
                    return 0.0f;
                Vector3 forward = playerVehicle.ForwardVector;
                float forwardLength = (float)Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y);
                if (forwardLength < 0.25f)
                    return 0.0f;
                float dot = (forward.X * towardCandidate.X + forward.Y * towardCandidate.Y)
                    / (forwardLength * length);
                return dot >= 0.15f ? 0.0f : dot >= -0.25f ? 8.0f : 18.0f;
            }
            catch
            {
                return 0.0f;
            }
        }

        private void ReleaseFootSubjectNaturally()
        {
            Ped citizen = _subject;
            ReleaseSubject(true);
            if (!IsUsable(citizen))
                return;
            try { Function.Call(Hash.TASK_WANDER_STANDARD, citizen, 10.0f, 10); }
            catch (Exception ex) { _log.Exception("NPC_FOOT_NATURAL_RELEASE_FAILED", ex); }
        }

        private void ReleaseSubject(bool clearTrafficTask)
        {
            // Release is an ownership boundary. Restore this Ped's saved
            // injury/weapon controls before it resumes ordinary ambient life;
            // physically cuffed custody keeps the disarmed state.
            RestoreSubjectInjuryState(_subject);
            try
            {
                if (IsUsable(_subjectVehicle))
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_BRAKE, _subjectVehicle, false);
                }
                if (IsUsable(_subject))
                {
                    Function.Call(Hash.SET_PED_CONFIG_FLAG,
                        _subject, HandcuffedPedConfigFlag, false);
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, false);
                    _subject.CanSwitchWeapons = true;
                    if (_subjectVehicle == null || clearTrafficTask)
                        _subject.Task.ClearAll();
                    ReleaseSubjectPoliceAwareness(_subject, false);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_SUBJECT_RELEASE_FAILED", ex);
            }
            finally
            {
                RestoreInteractionPersistence();
            }
        }

        /// <summary>
        /// Takes a temporary mission-style reference to the single ambient
        /// contact. This prevents GTA's ambient cleanup from deleting a driver
        /// during a pull-over or document exchange. The prior flags are kept
        /// so the NPC layer never permanently claims somebody else's entity.
        /// </summary>
        private void CaptureInteractionPersistence()
        {
            _subjectPersistenceCaptured = false;
            _subjectVehiclePersistenceCaptured = false;
            try
            {
                if (IsUsable(_subject))
                {
                    _subjectWasPersistent = _subject.IsPersistent;
                    _subjectPersistenceCaptured = true;
                    _subject.IsPersistent = true;
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_SUBJECT_PERSISTENCE_ACQUIRE_FAILED", ex);
            }

            try
            {
                if (IsUsable(_subjectVehicle))
                {
                    _subjectVehicleWasPersistent = _subjectVehicle.IsPersistent;
                    _subjectVehiclePersistenceCaptured = true;
                    _subjectVehicle.IsPersistent = true;
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_PERSISTENCE_ACQUIRE_FAILED", ex);
            }
        }

        private void RestoreInteractionPersistence()
        {
            try
            {
                if (_subjectPersistenceCaptured && IsUsable(_subject))
                    _subject.IsPersistent = _subjectWasPersistent;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_SUBJECT_PERSISTENCE_RELEASE_FAILED", ex);
            }

            try
            {
                if (_subjectVehiclePersistenceCaptured && IsUsable(_subjectVehicle))
                    _subjectVehicle.IsPersistent = _subjectVehicleWasPersistent;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_PERSISTENCE_RELEASE_FAILED", ex);
            }

            _subjectPersistenceCaptured = false;
            _subjectWasPersistent = false;
            RestoreBackupTransportProtection();
            _subjectVehiclePersistenceCaptured = false;
            _subjectVehicleWasPersistent = false;
        }

        private void CaptureBackupTransportProtection(Ped subject)
        {
            if (!IsUsable(subject) || _subjectInvincibilityCaptured)
                return;
            try
            {
                _subjectWasInvincible = subject.IsInvincible;
                _subjectInvincibilityCaptured = true;
                subject.IsInvincible = true;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_BACKUP_TRANSPORT_PROTECTION_FAILED", ex);
            }
        }

        private void RestoreBackupTransportProtection()
        {
            if (!_subjectInvincibilityCaptured)
                return;
            try
            {
                if (IsUsable(_subject))
                    _subject.IsInvincible = _subjectWasInvincible;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_BACKUP_TRANSPORT_PROTECTION_RESTORE_FAILED", ex);
            }
            finally
            {
                _subjectInvincibilityCaptured = false;
                _subjectWasInvincible = false;
            }
        }

        private static void SetSubjectPoliceAware(Ped ped)
        {
            if (!IsUsable(ped))
                return;
            try
            {
                ped.BlockPermanentEvents = true;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped, true);
                Function.Call(Hash.SET_PED_KEEP_TASK, ped, true);
            }
            catch { }
        }

        private static void ReleaseSubjectInteractionLockForMovement(Ped ped)
        {
            ReleaseSubjectPoliceAwareness(ped, true);
        }

        private static void ReleaseSubjectPoliceAwareness(Ped ped, bool keepTask)
        {
            if (!IsUsable(ped))
                return;
            try
            {
                ped.BlockPermanentEvents = false;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped, false);
                Function.Call(Hash.SET_PED_KEEP_TASK, ped, keepTask);
            }
            catch { }
        }

        private void BeginPendingAnimation(
            PendingAnimation animation,
            int duration,
            DateTime now)
        {
            _pendingAnimation = animation;
            _pendingAnimationDuration = Math.Max(700, duration);
            _nextAnimationRequest = now;
            _animationLoadDeadline = now.AddSeconds(3);
            RequestPendingAnimation(now);
        }

        private void MaintainPendingAnimation(DateTime now)
        {
            VerifyPendingAnimationPlayback(now);
            if (_pendingAnimation == PendingAnimation.None || !IsUsable(_subject))
                return;

            string dictionary;
            string clip;
            ResolvePendingAnimation(out dictionary, out clip);
            try
            {
                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dictionary))
                {
                    if (now >= _animationLoadDeadline)
                    {
                        FailPendingAnimation(now,
                            "NPC_ANIMATION_STREAM_TIMEOUT",
                            "Dictionary=" + dictionary + "; Clip=" + clip);
                        return;
                    }
                    if (now >= _nextAnimationRequest)
                        RequestPendingAnimation(now);
                    return;
                }

                PendingAnimation requested = _pendingAnimation;
                bool loopingPose = requested == PendingAnimation.HandcuffedIdle
                    || requested == PendingAnimation.CompliantKneel;
                bool citizenInteraction = requested == PendingAnimation.Documents;
                bool twoActorFootDocumentHandoff = requested == PendingAnimation.Documents
                    && !_subject.IsInVehicle()
                    && (_subjectVehicle == null
                        || (IsBackupInterceptionInvestigationActive
                            && _stage == InteractionStage.TrafficBackupContained)
                        || IsTrafficOnFootInterceptionInvestigationActive);
                bool seatedTrafficDocumentPresentation = requested == PendingAnimation.Documents
                    && IsSelectedTrafficDriverStillSeated();
                int taskDurationMilliseconds = loopingPose || citizenInteraction
                    ? -1 : _pendingAnimationDuration;
                float playbackDuration = 0f;
                float subjectClipDuration = 0f;
                float playerClipDuration = 0f;
                int sceneId = -1;
                Ped playerPed = null;
                string playerDictionary = string.Empty;
                string playerClip = string.Empty;
                string pairedStartDetails = string.Empty;

                if (twoActorFootDocumentHandoff)
                {
                    playerPed = Game.Player.Character;
                    playerDictionary = "mp_common";
                    playerClip = "givetake1_a";
                    if (!IsUsable(playerPed) || playerPed.IsDead
                        || !IsCitizenDocumentHandoffReady(playerPed, _subject)
                        || !LSImmersiveDictionaryAnimation.TryGetClipDuration(
                            dictionary, clip, out subjectClipDuration))
                    {
                        FailPendingAnimation(now,
                            "NPC_CITIZEN_DOCUMENT_HANDOFF_NOT_READY",
                            "Dictionary=" + dictionary + "; Clip=" + clip
                            + "; Reason=ActorsOutsideGroundedArmReach");
                        return;
                    }
                    if (!LSImmersiveDictionaryAnimation.TryGetClipDuration(
                        playerDictionary, playerClip, out playerClipDuration))
                    {
                        FailPendingAnimation(now,
                            "NPC_ANIMATION_PARTNER_CLIP_UNAVAILABLE",
                            "Dictionary=" + playerDictionary + "; Clip=" + playerClip);
                        return;
                    }

                    if (!OrientActorsForCitizenDocumentHandoff(playerPed, _subject))
                    {
                        FailPendingAnimation(now,
                            "NPC_CITIZEN_DOCUMENT_HANDOFF_ORIENTATION_FAILED",
                            "Action=" + requested
                            + "; Ped=" + _subject.Handle
                            + "; Player=" + playerPed.Handle);
                        return;
                    }

                    float subjectTaskDuration = 0f;
                    float playerTaskDuration = 0f;
                    bool subjectTaskStarted = false;
                    bool playerTaskStarted = false;
                    try
                    {
                        Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, _subject);
                        Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, playerPed);
                        subjectTaskStarted = LSImmersiveDictionaryAnimation.TryPlay(
                            _subject, dictionary, clip, 3.0f, -2.0f, -1,
                            49, 1.0f, out subjectTaskDuration);
                        if (subjectTaskStarted)
                            playerTaskStarted = LSImmersiveDictionaryAnimation.TryPlay(
                                playerPed, playerDictionary, playerClip,
                                3.0f, -2.0f, -1, 49, 1.0f,
                                out playerTaskDuration);
                    }
                    catch
                    {
                        subjectTaskDuration = 0f;
                        playerTaskDuration = 0f;
                    }

                    if (!subjectTaskStarted || !playerTaskStarted)
                    {
                        try
                        {
                            if (IsUsable(_subject))
                                Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, _subject);
                            if (IsUsable(playerPed))
                                Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, playerPed);
                        }
                        catch { }
                        FailPendingAnimation(now,
                            "NPC_CITIZEN_DOCUMENT_HANDOFF_TASK_REJECTED",
                            "Ped=" + _subject.Handle
                            + "; Player=" + playerPed.Handle
                            + "; CitizenTaskStarted=" + subjectTaskStarted
                            + "; PlayerTaskStarted=" + playerTaskStarted);
                        return;
                    }

                    subjectClipDuration = subjectTaskDuration;
                    playerClipDuration = playerTaskDuration;
                    playbackDuration = Math.Max(
                        subjectTaskDuration, playerTaskDuration);
                    Vector3 playerPosition = playerPed.Position;
                    Vector3 subjectPosition = _subject.Position;
                    pairedStartDetails = "; SubjectClipDuration="
                        + subjectClipDuration.ToString("0.000")
                        + "; PlayerClipDuration="
                        + playerClipDuration.ToString("0.000")
                        + "; Playback=ActorLocalUpperBodyPair; Scene=none"
                        + "; PlayerPosition=" + playerPosition.X.ToString("0.00")
                        + "," + playerPosition.Y.ToString("0.00")
                        + "," + playerPosition.Z.ToString("0.00")
                        + "; PlayerHeading=" + playerPed.Heading.ToString("0.0")
                        + "; CitizenPosition=" + subjectPosition.X.ToString("0.00")
                        + "," + subjectPosition.Y.ToString("0.00")
                        + "," + subjectPosition.Z.ToString("0.00")
                        + "; CitizenHeading=" + _subject.Heading.ToString("0.0");
                }
                else
                {
                    if (!LSImmersiveDictionaryAnimation.TryGetClipDuration(
                        dictionary, clip, out playbackDuration))
                    {
                        FailPendingAnimation(now,
                            "NPC_ANIMATION_CLIP_UNAVAILABLE",
                            "Dictionary=" + dictionary + "; Clip=" + clip);
                        return;
                    }

                    int flags = loopingPose ? 1 : citizenInteraction ? 49 : 0;
                    if (seatedTrafficDocumentPresentation)
                    {
                        // Preserve the selected driver's seat/vehicle task.
                        // The Citizen presents the existing document gesture
                        // while the officer remains grounded beside the cab.
                        Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, _subject);
                        Ped inspectingOfficer = Game.Player.Character;
                        if (IsUsable(inspectingOfficer) && !inspectingOfficer.IsInVehicle())
                            inspectingOfficer.Task.LookAt(_subject, 1800);
                    }
                    else
                        _subject.Task.ClearAll();
                    if (!LSImmersiveDictionaryAnimation.TryPlay(
                        _subject,
                        dictionary,
                        clip,
                        3.0f,
                        -2.0f,
                        taskDurationMilliseconds,
                        flags,
                        1.0f,
                        out playbackDuration))
                    {
                        FailPendingAnimation(now,
                            "NPC_ANIMATION_TASK_REJECTED",
                            "Ped=" + _subject.Handle + "; Dictionary=" + dictionary
                            + "; Clip=" + clip);
                        return;
                    }
                }

                SchedulePendingAnimationVerification(
                    now, requested, dictionary, clip, _subject,
                    playerClip, playerPed, sceneId);
                _pendingAnimation = PendingAnimation.None;
                int holdMilliseconds = _pendingAnimationDuration;
                if (citizenInteraction)
                    holdMilliseconds = Math.Max(holdMilliseconds,
                        (int)Math.Ceiling(playbackDuration * 1000.0f));
                _animationHoldUntil = now.AddMilliseconds(holdMilliseconds);
                if (requested == PendingAnimation.Documents)
                    _readyAt = DateTime.MaxValue;
                _log.Runtime("NPC_ANIMATION_TASK_STARTED",
                    "Action=" + requested + "; Ped=" + _subject.Handle
                    + "; Dictionary=" + dictionary + "; Clip=" + clip
                    + "; ClipDuration=" + playbackDuration.ToString("0.000")
                    + "; Flags=" + (twoActorFootDocumentHandoff
                        ? "48" : (loopingPose ? "1" : citizenInteraction ? "48" : "0"))
                    + "; PlaybackRate=1.0"
                    + "; TaskDurationMs=" + taskDurationMilliseconds
                    + "; Playback=" + (twoActorFootDocumentHandoff
                        ? "ActorLocalUpperBodyPair"
                        : seatedTrafficDocumentPresentation
                            ? "SeatedDriverPresentation" : "CitizenOnlyTask")
                    + "; PlayerAnimation=" + (playerPed != null)
                    + "; Scene=" + (sceneId < 0 ? "none" : sceneId.ToString())
                    + (playerPed == null ? string.Empty
                        : "; Player=" + playerPed.Handle + "; PlayerClip=" + playerClip)
                    + pairedStartDetails);
                if (requested == PendingAnimation.Documents)
                {
                    _documentsProvided = false;
                    Notify(twoActorFootDocumentHandoff
                        ? "Grounded document exchange started. Waiting for both actors to complete the handoff."
                        : seatedTrafficDocumentPresentation
                            ? "The driver remains seated and presents identification for the officer to inspect."
                            : "Citizen document gesture started. Waiting for the physical exchange to play.");
                }
                else if (requested == PendingAnimation.CompliantKneel)
                    Notify("Citizen is kneeling with hands visible and awaiting the physical arrest.");
            }
            catch (Exception ex)
            {
                _pendingAnimation = PendingAnimation.None;
                if (_log != null)
                    _log.Exception("NPC_ANIMATION_START_FAILED", ex);
                if (_stage == InteractionStage.FootDocuments
                    || _stage == InteractionStage.TrafficDocuments)
                {
                    _documentsProvided = false;
                    _readyAt = now.AddMilliseconds(700);
                    _animationHoldUntil = now.AddMilliseconds(700);
                    Notify("The document handoff could not play. Select Request the Validation ID/Document to retry it.");
                }
                else if (_backupInterceptionInvestigationRequired
                    && (_stage == InteractionStage.FootBackupContained
                        || _stage == InteractionStage.TrafficBackupContained))
                {
                    _documentsProvided = false;
                    _readyAt = now.AddMilliseconds(700);
                    _animationHoldUntil = now.AddMilliseconds(700);
                    Notify("The post-interception document handoff could not play. Select Request the Validation ID/Document to retry it.");
                }
            }
        }

        private void SchedulePendingAnimationVerification(
            DateTime now,
            PendingAnimation action,
            string dictionary,
            string subjectClip,
            Ped subject,
            string playerClip,
            Ped player,
            int sceneId)
        {
            _animationVerificationAt = now.AddMilliseconds(350);
            _animationVerificationDeadline = action == PendingAnimation.PairedArrest
                ? now.AddMilliseconds(900)
                : now.AddSeconds(2);
            _animationVerificationAction = action;
            _animationVerificationPassed = false;
            _animationVerificationSceneId = sceneId;
            _animationVerificationDictionary = dictionary ?? string.Empty;
            _animationVerificationSubjectClip = subjectClip ?? string.Empty;
            _animationVerificationPlayerClip = playerClip ?? string.Empty;
            _animationVerificationSubject = subject;
            _animationVerificationPlayer = player;
        }

        private void VerifyPendingAnimationPlayback(DateTime now)
        {
            if (_animationVerificationAt == DateTime.MinValue
                || now < _animationVerificationAt)
                return;

            bool subjectPlaying = LSImmersiveDictionaryAnimation.IsPlaying(
                _animationVerificationSubject,
                _animationVerificationDictionary,
                _animationVerificationSubjectClip);
            bool playerExpected = !string.IsNullOrEmpty(
                _animationVerificationPlayerClip);
            bool playerPlaying = playerExpected
                && LSImmersiveDictionaryAnimation.IsPlaying(
                    _animationVerificationPlayer,
                    _animationVerificationDictionary,
                    _animationVerificationPlayerClip);
            bool playbackConfirmed = subjectPlaying && (!playerExpected || playerPlaying);
            if (playbackConfirmed
                && _animationVerificationAction == PendingAnimation.PairedArrest
                && GetAnimationVerificationHandle(_animationVerificationSubject)
                    == GetAnimationVerificationHandle(_subject))
            {
                if (_stage == InteractionStage.BackupSecuring
                    && GetAnimationVerificationHandle(_animationVerificationPlayer)
                        == GetAnimationVerificationHandle(_backupOfficer))
                {
                    _backupArrestPlaybackConfirmed = true;
                    _log.Runtime("NPC_BACKUP_ARREST_PLAYBACK_CONFIRMED",
                        "Ped=" + GetAnimationVerificationHandle(_subject)
                        + "; Officer=" + GetAnimationVerificationHandle(_backupOfficer)
                        + "; Dictionary=" + _animationVerificationDictionary
                        + "; SubjectClip=" + _animationVerificationSubjectClip
                        + "; OfficerClip=" + _animationVerificationPlayerClip
                        + "; BothActorsPlaying=true");
                }
                else if (_stage == InteractionStage.FootPlayerArresting
                    && GetAnimationVerificationHandle(_animationVerificationPlayer)
                        == GetAnimationVerificationHandle(Game.Player.Character))
                {
                    _footPlayerArrestPlaybackConfirmed = true;
                    _log.Runtime("NPC_FOOT_PLAYER_ARREST_PLAYBACK_CONFIRMED",
                        "Ped=" + GetAnimationVerificationHandle(_subject)
                        + "; Player=" + GetAnimationVerificationHandle(Game.Player.Character)
                        + "; Dictionary=" + _animationVerificationDictionary
                        + "; SubjectClip=" + _animationVerificationSubjectClip
                        + "; OfficerClip=" + _animationVerificationPlayerClip
                        + "; BothActorsPlaying=true");
                }
            }
            if (!playbackConfirmed && now < _animationVerificationDeadline)
            {
                // Give the selected Citizen and Player a short start window
                // before treating the physical interaction as unavailable.
                _animationVerificationAt = now.AddMilliseconds(150);
                return;
            }
            float scenePhase;
            bool scenePhaseAvailable = LSImmersiveDictionaryAnimation
                .TryGetSynchronizedScenePhase(_animationVerificationSceneId, out scenePhase);
            _log.Runtime(playbackConfirmed
                    ? "NPC_ANIMATION_PLAYING_CONFIRMED"
                    : "NPC_ANIMATION_PLAYBACK_NOT_CONFIRMED",
                "Ped=" + GetAnimationVerificationHandle(_animationVerificationSubject)
                + "; Action=" + _animationVerificationAction
                + "; Dictionary=" + _animationVerificationDictionary
                + "; Clip=" + _animationVerificationSubjectClip
                + "; IsPlaying=" + subjectPlaying
                + "; Scene=" + (_animationVerificationSceneId < 0
                    ? "none" : _animationVerificationSceneId.ToString())
                + (playerExpected
                    ? "; Player=" + GetAnimationVerificationHandle(_animationVerificationPlayer)
                        + "; PlayerClip=" + _animationVerificationPlayerClip
                        + "; PlayerIsPlaying=" + playerPlaying
                    : string.Empty)
                + "; ScenePhase=" + (scenePhaseAvailable
                    ? scenePhase.ToString("0.000") : "unavailable"));

            if (_animationVerificationAction == PendingAnimation.Documents
                && _animationVerificationPassed
                && now >= _animationHoldUntil)
            {
                if (playbackConfirmed)
                {
                    ClearCitizenDocumentAnimationTasks(
                        _animationVerificationSubject,
                        _animationVerificationPlayer);
                    _documentsProvided = true;
                    _readyAt = now;
                    _animationHoldUntil = now;
                    _log.Runtime("NPC_DOCUMENT_HANDOFF_COMPLETED",
                        "Ped=" + GetAnimationVerificationHandle(_animationVerificationSubject)
                        + "; Player=" + GetAnimationVerificationHandle(_animationVerificationPlayer)
                        + "; Contact=" + (_subjectVehicle == null ? "Foot" : "Traffic")
                        + "; CitizenGesturePlaybackConfirmed=true; HoldElapsed=true"
                        + "; BothActorPlaybackConfirmed="
                        + !string.IsNullOrEmpty(_animationVerificationPlayerClip)
                        + "; PlaybackReconfirmedAtHoldEnd=true"
                        + "; ActorLocalUpperBodyTasks="
                        + !string.IsNullOrEmpty(_animationVerificationPlayerClip)
                        + "; Scene=" + (_animationVerificationSceneId < 0
                            ? "none" : _animationVerificationSceneId.ToString())
                        + "; PlayerAnimation="
                        + !string.IsNullOrEmpty(_animationVerificationPlayerClip)
                        + "; PlayerPosition=" + FormatAnimationPosition(
                            _animationVerificationPlayer)
                        + "; CitizenPosition=" + FormatAnimationPosition(
                            _animationVerificationSubject));
                    Notify("Document received. Select Request Dispatcher For Validation Request to open the citizen record.");
                    ClearAnimationVerification();
                    return;
                }

                ClearCitizenDocumentAnimationTasks(
                    _animationVerificationSubject,
                    _animationVerificationPlayer);
                _documentsProvided = false;
                _readyAt = DateTime.MaxValue;
                _animationHoldUntil = now.AddMilliseconds(700);
                Notify("The document exchange did not remain active through the handoff. Select Request the Validation ID/Document to retry it.");
                ClearAnimationVerification();
                return;
            }

            if (_animationVerificationAction == PendingAnimation.PairedArrest
                && !playbackConfirmed)
            {
                Ped arrestingOfficer = _animationVerificationPlayer;
                Ped arrestSubject = _animationVerificationSubject;
                try
                {
                    if (IsUsable(arrestingOfficer) && IsUsable(arrestSubject)
                        && !arrestSubject.IsDead && arrestSubject.Health > 0)
                    {
                        Function.Call(Hash.TASK_ARREST_PED,
                            arrestingOfficer, arrestSubject);
                        _log.Runtime("NPC_PAIRED_ARREST_TASK_FALLBACK",
                            "Ped=" + arrestSubject.Handle
                            + "; Officer=" + arrestingOfficer.Handle
                            + "; Scene=" + _animationVerificationSceneId
                            + "; Reason=PAIR_CLIP_NOT_PLAYING"
                            + "; Fallback=TASK_ARREST_PED");
                    }
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_PAIRED_ARREST_TASK_FALLBACK_FAILED", ex);
                }
            }

            if (_animationVerificationAction == PendingAnimation.Documents)
            {
                if (playbackConfirmed)
                {
                    _animationVerificationPassed = true;
                    _animationVerificationAt = _animationHoldUntil;
                    return;
                }
                else
                {
                    ClearCitizenDocumentAnimationTasks(
                        _animationVerificationSubject,
                        _animationVerificationPlayer);
                    _documentsProvided = false;
                    _readyAt = DateTime.MaxValue;
                    _animationHoldUntil = now.AddMilliseconds(700);
                    Notify("The document exchange did not play correctly. Select Request the Validation ID/Document to retry it.");
                }
            }
            ClearAnimationVerification();
        }

        private static void ClearCitizenDocumentAnimationTasks(
            Ped citizen,
            Ped player)
        {
            try
            {
                if (IsUsable(citizen))
                    Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, citizen);
            }
            catch { }
            try
            {
                if (IsUsable(player))
                    Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, player);
            }
            catch { }
        }

        private static int GetAnimationVerificationHandle(Ped ped)
        {
            try { return ped != null && ped.Exists() ? ped.Handle : 0; }
            catch { return 0; }
        }

        private static string FormatAnimationPosition(Ped ped)
        {
            try
            {
                if (ped == null || !ped.Exists())
                    return "unavailable";
                Vector3 position = ped.Position;
                return position.X.ToString("0.00") + ","
                    + position.Y.ToString("0.00") + ","
                    + position.Z.ToString("0.00");
            }
            catch
            {
                return "unavailable";
            }
        }

        private static string GetScenePhaseText(int sceneId)
        {
            float phase;
            return LSImmersiveDictionaryAnimation.TryGetSynchronizedScenePhase(
                sceneId, out phase) ? phase.ToString("0.000") : "unavailable";
        }

        private void ClearAnimationVerification()
        {
            _animationVerificationAt = DateTime.MinValue;
            _animationVerificationDeadline = DateTime.MinValue;
            _animationVerificationAction = PendingAnimation.None;
            _animationVerificationPassed = false;
            _animationVerificationSceneId = -1;
            _animationVerificationDictionary = string.Empty;
            _animationVerificationSubjectClip = string.Empty;
            _animationVerificationPlayerClip = string.Empty;
            _animationVerificationSubject = null;
            _animationVerificationPlayer = null;
        }

        private void FailPendingAnimation(DateTime now, string category, string reason)
        {
            PendingAnimation failed = _pendingAnimation;
            _pendingAnimation = PendingAnimation.None;
            _animationHoldUntil = now.AddMilliseconds(700);
            ClearAnimationVerification();
            if (_log != null)
                _log.StateFailure(category,
                    "Animation=" + failed + "; Ped="
                    + (_subject == null ? 0 : _subject.Handle)
                    + "; Reason=" + (reason ?? string.Empty));

            if (failed == PendingAnimation.CompliantKneel)
            {
                try { _subject.Task.HandsUp(15000); } catch { }
                _log.Runtime("NPC_FOOT_COMPLIANCE_POSE_FALLBACK",
                    "Ped=" + _subject.Handle + "; Fallback=HandsUp");
            }
            else if (failed == PendingAnimation.Documents)
            {
                _readyAt = _animationHoldUntil;
                _documentsProvided = false;
                Notify("The document handoff did not finish. Select Request the Validation ID/Document to retry it.");
            }
        }

        private void RequestPendingAnimation(DateTime now)
        {
            string dictionary;
            string clip;
            ResolvePendingAnimation(out dictionary, out clip);
            try
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, dictionary);
            }
            catch { }
            _nextAnimationRequest = now.AddMilliseconds(250);
        }

        private void ResolvePendingAnimation(out string dictionary, out string clip)
        {
            if (_pendingAnimation == PendingAnimation.HandcuffedIdle)
            {
                dictionary = "mp_arresting";
                clip = "idle";
                return;
            }
            if (_pendingAnimation == PendingAnimation.CompliantKneel)
            {
                dictionary = "random@arrests";
                clip = "kneeling_arrest_idle";
                return;
            }
            dictionary = "mp_common";
            clip = "givetake1_b";
        }

        private void PresentCitizenRecord()
        {
            if (IsBackupInterceptionInvestigationActive)
            {
                LSPDNPCRecord revalidatedRecord;
                if (_database == null || !_database.TryApplyAdverseRevalidation(
                    _subject, _subjectVehicle != null, _currentRecord, out revalidatedRecord))
                {
                    CitizenRecordVisible = false;
                    _dispatcherValidationReceived = false;
                    _citizenRecordReviewed = false;
                    _log.Runtime("NPC_BACKUP_INTERCEPTION_VALIDATION_UNAVAILABLE",
                        "Ped=" + (_subject == null ? 0 : _subject.Handle)
                        + "; Database=" + (_database == null ? "Unavailable" : "NoAdverseStatus"));
                    Notify("The document handoff completed, but Dispatcher could not return the required post-flight status. Retry validation when the citizen database is available.");
                    return;
                }
                _currentRecord = revalidatedRecord;
            }
            else if (_currentRecord == null && _database != null)
                _database.TryGetRecord(_subject, _subjectVehicle != null, out _currentRecord);

            if (_currentRecord == null)
            {
                CitizenRecordVisible = false;
                _dispatcherValidationReceived = false;
                _citizenRecordReviewed = false;
                Notify("Identification was presented, but the citizen database is unavailable. "
                    + "The document decision remains unavailable until Dispatcher validation returns a record.");
                return;
            }

            _dispatcherValidationReceived = true;
            CitizenRecordVisible = true;
            _citizenRecordReviewed = false;
            Notify("Dispatcher validation received for " + _currentRecord.FullName
                + ". Review the record in the lower-right display while the Citizen framework stays open, then select Confirm Review.");
            _log.Runtime("NPC_DOCUMENT_CHECK_COMPLETED",
                "Ped=" + _subject.Handle + "; " + _currentRecord.DetailText
                + "; Recommended=" + _currentRecord.RecommendedResponse);
        }

        private string RecordStatus(string prefix)
        {
            return _currentRecord == null
                ? prefix
                : prefix + " | " + _currentRecord.FullName
                    + " | " + _currentRecord.DocumentStatus
                    + " | " + _currentRecord.StatusId
                    + " | Warrant=" + (_currentRecord.WarrantActive
                        ? _currentRecord.WarrantLabel : "None");
        }

        private string DecisionHint(string acceptAction, string rejectAction)
        {
            return _controls.AcceptKey + "/X = " + acceptAction
                + ", " + _controls.RejectKey + "/O = " + rejectAction + ".";
        }

        private string CitizenDocumentStatusText(string contact)
        {
            if (!_documentsProvided)
                return contact + " is waiting for the physical document handoff to complete.";
            if (!_dispatcherValidationReceived)
                return contact + " received identification. Request Dispatcher validation.";
            if (CitizenRecordVisible)
                return RecordStatus(contact + " validation is displayed. Review it and select Confirm Review.");
            if (_citizenRecordReviewed)
                return RecordStatus(contact + " document review is confirmed. Choose Accept or Reject.");
            return contact + " is awaiting Dispatcher validation.";
        }

        private string CitizenDecisionGateMessage(string decision)
        {
            if (_citizenDocumentDecisionResolved)
                return "This Citizen's document disagreement already selected the response branch. Continue that branch; do not repeat the document decision.";
            return CitizenRecordVisible && _currentRecord != null
                ? "Review the Dispatcher validation in the lower-right display and select Confirm Review before you "
                    + decision + " the document."
                : "Request the citizen's document and Dispatcher validation before you "
                    + decision + " the document.";
        }

        private void ClearInteractionState()
        {
            RestoreBackupCustodyMovementStyle();
            RestoreSubjectInjuryState(_subject);
            DetachBackupSubjectFromOfficer("InteractionStateCleared");
            CleanupSubjectBlip();
            RestoreInteractionPersistence();
            _stage = InteractionStage.None;
            _subject = null;
            _subjectVehicle = null;
            _readyAt = DateTime.MinValue;
            _expiresAt = DateTime.MinValue;
            _nextSubjectHold = DateTime.MinValue;
            _animationHoldUntil = DateTime.MinValue;
            _fleeStartedAt = DateTime.MinValue;
            _lastFleeSampleAt = DateTime.MinValue;
            _nextFleeTaskAt = DateTime.MinValue;
            _nextResistanceTaskAt = DateTime.MinValue;
            _nextSuspiciousApproachActionAt = DateTime.MinValue;
            _fleeStoppedAt = DateTime.MinValue;
            _nextStationTaskAt = DateTime.MinValue;
            _nextPullOverTaskAt = DateTime.MinValue;
            _pullOverDeadline = DateTime.MinValue;
            _trafficStoppedSince = DateTime.MinValue;
            _nextTrafficBrakeTaskAt = DateTime.MinValue;
            _nextTrafficHoldAt = DateTime.MinValue;
            _nextTrafficDriverExitCheckAt = DateTime.MinValue;
            _nextTrafficDriverExitAttemptAt = DateTime.MinValue;
            _trafficDriverExitDeadline = DateTime.MinValue;
            _nextAnimationRequest = DateTime.MinValue;
            _animationLoadDeadline = DateTime.MinValue;
            _animationVerificationAt = DateTime.MinValue;
            _animationVerificationDeadline = DateTime.MinValue;
            _animationVerificationAction = PendingAnimation.None;
            _animationVerificationPassed = false;
            _animationVerificationSceneId = -1;
            _animationVerificationDictionary = string.Empty;
            _animationVerificationSubjectClip = string.Empty;
            _animationVerificationPlayerClip = string.Empty;
            _animationVerificationSubject = null;
            _animationVerificationPlayer = null;
            _backupPhaseDeadline = DateTime.MinValue;
            _nextBackupSubjectTask = DateTime.MinValue;
            _fleeLastPosition = Vector3.Zero;
            _trafficPullOverPosition = Vector3.Zero;
            _trafficContactVehiclePosition = Vector3.Zero;
            _trafficContactPlayerPosition = Vector3.Zero;
            _trafficStopConfirmedPosition = Vector3.Zero;
            _trafficDriverWindowGeometry = default(TrafficDriverWindowGeometry);
            _trafficDirectFallbackLogged = false;
            _trafficPursuitVehicle = null;
            _trafficPursuitStage = TrafficPursuitStage.None;
            _trafficVehicleTransitionStartedAt = DateTime.MinValue;
            _trafficSubjectLostSince = DateTime.MinValue;
            _nextTrafficBackupPursuitSampleAt = DateTime.MinValue;
            _nextTrafficPursuitExitAt = DateTime.MinValue;
            _trafficPursuitExitAttempts = 0;
            _trafficPursuitExitFailureNotified = false;
            _trafficPlayerAimStartedAt = DateTime.MinValue;
            _trafficPlayerAimSubjectHandle = 0;
            _trafficPlayerAimCheckFailureLogged = false;
            _interactionAnchor = Vector3.Zero;
            _footStationDestination = Vector3.Zero;
            _trafficStationDestination = Vector3.Zero;
            _footStationName = string.Empty;
            _trafficStationName = string.Empty;
            _fleeMovementConfirmed = false;
            _footContactFled = false;
            _trafficContactFled = false;
            _fleeRecoveryIssued = false;
            _suspiciousFootContact = false;
            _suspiciousTrafficContact = false;
            _pullOverRecoveryIssued = false;
            _trafficPullOverFallbackTarget = false;
            _trafficStopConfirmed = false;
            _trafficStopConfirmedAtTarget = false;
            _trafficComplianceExitRequested = false;
            _trafficDriverExitConfirmed = false;
            _trafficDriverExitAttempts = 0;
            _approachCandidateStopped = false;
            _backupRequested = false;
            _backupInterceptionInvestigationRequired = false;
            _documentsProvided = false;
            _citizenDocumentDecisionResolved = false;
            _backupBackgroundTransport = false;
            _footPlayerArrested = false;
            _footPlayerArrestAttempts = 0;
            _footPlayerArrestDeadline = DateTime.MinValue;
            _footPlayerArrestPlaybackConfirmed = false;
            _secureKeyDown = false;
            _backupEscortTaskIssued = false;
            _backupEntryTaskIssued = false;
            _backupVehicleDoorOpened = false;
            _backupVehicleDoorTaskIssued = false;
            _backupVehicleDoorFallbackIssued = false;
            _backupVehicleDoorTaskIssuedAt = DateTime.MinValue;
            _backupArrestTaskIssued = false;
            _backupArrestAttempts = 0;
            _backupArrestAnimationHoldUntil = DateTime.MinValue;
            _backupArrestPlaybackConfirmed = false;
            _backupCustodyOfferPresented = false;
            _backupOfficer = null;
            _backupVehicle = null;
            _footGreetingConfirmed = false;
            _footGreetingAttempts = 0;
            _interactionAudioScope = string.Empty;
            _interactionAudioOccurrence = string.Empty;
            _documentAudioSequence = 0;
            _trafficRefusalAudioSequence = 0;
            _footAggressionAudioReported = false;
            _pendingAnimation = PendingAnimation.None;
            _pendingAnimationDuration = 0;
            _currentRecord = null;
            _dispatcherValidationReceived = false;
            _citizenRecordReviewed = false;
            CitizenRecordVisible = false;
        }

        private void EnsureSubjectBlip(string name)
        {
            if (_subjectBlip != null && _subjectBlip.Exists())
            {
                _subjectBlip.Name = name;
                return;
            }
            if (!IsUsable(_subject))
                return;
            try
            {
                _subjectBlip = _subject.AddBlip();
                if (_subjectBlip != null && _subjectBlip.Exists())
                {
                    _subjectBlip.Name = name;
                    _subjectBlip.IsShortRange = false;
                }
            }
            catch (Exception ex) { _log.Exception("NPC_SUBJECT_BLIP_FAILED", ex); }
        }

        private void CleanupSubjectBlip()
        {
            try
            {
                if (_subjectBlip != null && _subjectBlip.Exists())
                    _subjectBlip.Delete();
            }
            catch (Exception ex) { _log.Exception("NPC_SUBJECT_BLIP_CLEANUP_FAILED", ex); }
            _subjectBlip = null;
        }

        private bool WasRecentlyReleased(Vehicle vehicle)
        {
            if (!IsUsable(vehicle))
                return true;
            DateTime until;
            if (!_recentlyReleasedVehicles.TryGetValue(vehicle.Handle, out until))
                return false;
            if (DateTime.UtcNow < until)
                return true;
            _recentlyReleasedVehicles.Remove(vehicle.Handle);
            return false;
        }

        private void TrimReleasedVehicleMemory(DateTime now)
        {
            if (_recentlyReleasedVehicles.Count == 0)
                return;
            var expired = new List<int>();
            foreach (KeyValuePair<int, DateTime> pair in _recentlyReleasedVehicles)
                if (now >= pair.Value)
                    expired.Add(pair.Key);
            foreach (int handle in expired)
                _recentlyReleasedVehicles.Remove(handle);
        }

        private void TrimTrafficApproachBrakeMemory(DateTime now)
        {
            if (now < _nextTrafficApproachBrakeTrim)
                return;
            _nextTrafficApproachBrakeTrim = now.AddSeconds(10);
            if (_recentTrafficApproachBrakes.Count == 0)
                return;

            var expired = new List<int>();
            foreach (KeyValuePair<int, DateTime> pair in _recentTrafficApproachBrakes)
                if (now >= pair.Value)
                    expired.Add(pair.Key);
            foreach (int handle in expired)
                _recentTrafficApproachBrakes.Remove(handle);
        }

        private void TrimSceneReactionMemory(DateTime now)
        {
            if (_recentSceneReactions.Count == 0)
                return;
            var expired = new List<int>();
            foreach (KeyValuePair<int, DateTime> pair in _recentSceneReactions)
                if (now >= pair.Value)
                    expired.Add(pair.Key);
            foreach (int handle in expired)
                _recentSceneReactions.Remove(handle);
        }

        private void TrimDeescalationMemory(DateTime now)
        {
            if (_recentDeescalations.Count == 0)
                return;
            var expired = new List<int>();
            foreach (KeyValuePair<int, DateTime> pair in _recentDeescalations)
                if (now >= pair.Value)
                    expired.Add(pair.Key);
            foreach (int handle in expired)
                _recentDeescalations.Remove(handle);
        }

        private void TrimApproachMemory(DateTime now)
        {
            if (_recentApproachCandidates.Count == 0)
                return;
            var expired = new List<int>();
            foreach (KeyValuePair<int, DateTime> pair in _recentApproachCandidates)
                if (now >= pair.Value)
                    expired.Add(pair.Key);
            foreach (int handle in expired)
                _recentApproachCandidates.Remove(handle);
        }

        /// <summary>
        /// One diagnostic line per deliberate interaction attempt. This is not
        /// a background scan log: it exists specifically so a screenshot that
        /// visibly shows a nearby pedestrian/driver can be reconciled with the
        /// target-selection rules without flooding Runtime.log.
        /// </summary>
        private void LogContactSearch(
            Ped player,
            string mode,
            Ped selectedFoot,
            Ped selectedDriver)
        {
            if (_log == null || !IsUsable(player))
                return;
            try
            {
                float radius = Math.Max(FootContactRadius, TrafficContactRadius);
                Ped[] nearby = World.GetNearbyPeds(player, radius);
                int scanned = 0;
                int eligibleFoot = 0;
                int eligibleDrivers = 0;
                int police = 0;
                int protectedActors = 0;
                int gangThreats = 0;
                int otherRejected = 0;

                if (nearby != null)
                {
                    foreach (Ped ped in nearby)
                    {
                        if (!IsUsable(ped) || ped.Handle == player.Handle)
                            continue;
                        scanned++;
                        if (ped.IsDead || !ped.IsHuman)
                        {
                            otherRejected++;
                            continue;
                        }
                        if (IsPolicePed(ped))
                        {
                            police++;
                            continue;
                        }
                        if (IsProtectedSceneActor(ped))
                        {
                            protectedActors++;
                            continue;
                        }
                        if (!CanUseAsCivilian(ped, player))
                        {
                            if (IsPotentialGangThreat(ped))
                                gangThreats++;
                            else
                                otherRejected++;
                            continue;
                        }
                        if (!ped.IsInVehicle())
                            eligibleFoot++;
                        else
                        {
                            Vehicle vehicle = ped.CurrentVehicle;
                            if (IsUsable(vehicle) && IsDriver(ped, vehicle))
                                eligibleDrivers++;
                        }
                    }
                }

                _log.Runtime(
                    "NPC_CONTACT_SCAN",
                    "Mode=" + mode
                    + "; Scanned=" + scanned
                    + "; EligibleFoot=" + eligibleFoot
                    + "; EligibleDrivers=" + eligibleDrivers
                    + "; Police=" + police
                    + "; Protected=" + protectedActors
                    + "; GangThreat=" + gangThreats
                    + "; OtherRejected=" + otherRejected
                    + "; SelectedFoot=" + (selectedFoot == null ? "none" : selectedFoot.Handle.ToString())
                    + "; SelectedDriver=" + (selectedDriver == null ? "none" : selectedDriver.Handle.ToString())
                    + "; FootRadius=" + FootContactRadius.ToString("0.0")
                    + "; TrafficRadius=" + TrafficContactRadius.ToString("0.0"));
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_CONTACT_SCAN_FAILED", ex);
            }
        }

        private bool CanUseAsCivilian(Ped ped, Ped player)
        {
            if (!IsUsable(ped) || ped.IsDead || !ped.IsHuman
                || !IsUsable(player) || ped.Handle == player.Handle
                || IsPolicePed(ped) || IsProtectedSceneActor(ped))
                return false;
            try
            {
                int model = ped.Model.Hash;
                // Friendly Anyiii companions remain outside the ambient
                // civilian owner. Gang-associated *models*, however, are not
                // automatically hostile. The old exact-model exclusion made
                // perfectly calm pedestrians/drivers impossible to contact in
                // areas where Gang & Turf reuses common GTA models. Let the
                // Gang owner claim them only when they are actually aggressive.
                if (LSPDVanillaGangResponse.IsFriendlyAnyiii(ped))
                    return false;

                bool vanillaGangModel = LSPDVanillaGangResponse.IsVanillaGangHash(model);
                if (_gangData == null)
                    return !vanillaGangModel || !IsPotentialGangThreat(ped);

                LSPDGangDefinition gang = _gangData.FindGangForModel(model);
                if (gang != null && (gang.IsPlayerOwned || gang.CreatedByPlayer))
                    return false;

                bool gangAssociated = vanillaGangModel
                    || gang != null
                    || _gangData.IsMemberPoolModel(model);
                return !gangAssociated || !IsPotentialGangThreat(ped);
            }
            catch
            {
                return false;
            }
        }

        private bool IsProtectedSceneActor(Ped ped)
        {
            return IsUsable(ped) && _protectedSceneActorHandles.Contains(ped.Handle);
        }

        private bool IsProtectedSceneVehicle(Vehicle vehicle)
        {
            return IsUsable(vehicle) && _protectedSceneVehicleHandles.Contains(vehicle.Handle);
        }

        private void AddProtectedSceneActor(Ped ped)
        {
            if (IsUsable(ped))
            {
                _protectedSceneActorHandles.Add(ped.Handle);
                _protectedSceneActors[ped.Handle] = ped;
            }
        }

        private void AddProtectedSceneVehicle(Vehicle vehicle)
        {
            if (IsUsable(vehicle))
            {
                _protectedSceneVehicleHandles.Add(vehicle.Handle);
                _protectedSceneVehicles[vehicle.Handle] = vehicle;
            }
        }

        private static bool IsPotentialGangThreat(Ped ped)
        {
            if (!IsUsable(ped))
                return false;
            try
            {
                return ped.IsInCombat || ped.IsShooting
                    || Function.Call<bool>(Hash.IS_PED_ARMED, ped, 7);
            }
            catch
            {
                return ped.IsInCombat || ped.IsShooting;
            }
        }

        private static bool IsPolicePed(Ped ped)
        {
            if (!IsUsable(ped))
                return false;
            try
            {
                int model = ped.Model.Hash;
                return ped.IsInPoliceVehicle ||
                    model == unchecked((int)StringHash.AtStringHash("s_m_y_cop_01", 0)) ||
                    model == unchecked((int)StringHash.AtStringHash("s_f_y_cop_01", 0)) ||
                    model == unchecked((int)StringHash.AtStringHash("s_m_y_sheriff_01", 0)) ||
                    model == unchecked((int)StringHash.AtStringHash("s_f_y_sheriff_01", 0));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDriver(Ped ped, Vehicle vehicle)
        {
            if (!IsUsable(ped) || !IsUsable(vehicle))
                return false;
            try
            {
                int driver = Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, -1);
                return driver == ped.Handle;
            }
            catch
            {
                return false;
            }
        }

        private bool IsSelectedTrafficDriverStillSeated()
        {
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle)
                || !_subject.IsInVehicle())
                return false;
            try
            {
                Vehicle currentVehicle = _subject.CurrentVehicle;
                return IsUsable(currentVehicle)
                    && currentVehicle.Handle == _subjectVehicle.Handle
                    && IsDriver(_subject, _subjectVehicle);
            }
            catch { return false; }
        }

        private bool IsTrafficStopAtSelectedRoadside()
        {
            if (!_trafficStopConfirmed || !IsUsable(_subjectVehicle)
                || !IsSelectedTrafficDriverStillSeated()
                || Math.Abs(_subjectVehicle.Speed) > 0.75f)
                return false;

            bool nearConfirmedStop = HasFiniteComponents(_trafficStopConfirmedPosition)
                && _subjectVehicle.Position.DistanceTo(_trafficStopConfirmedPosition)
                    <= TrafficStopPositionDriftRadius;
            bool hasValidStopBasis = _trafficStopConfirmedAtTarget
                || IsTrafficVehicleNearContactArea(_subjectVehicle);
            return nearConfirmedStop && hasValidStopBasis;
        }

        private bool IsSelectedTrafficDriverOutside()
        {
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle)
                || _subject.IsInVehicle())
                return false;
            try
            {
                Vehicle currentVehicle = _subject.CurrentVehicle;
                return (currentVehicle == null || !currentVehicle.Exists())
                    && _subject.Position.DistanceTo(_subjectVehicle.Position) <= 12.0f;
            }
            catch { return false; }
        }

        private static bool IsUsable(Ped ped)
        {
            return ped != null && ped.Exists();
        }

        private static bool IsUsable(Vehicle vehicle)
        {
            return vehicle != null && vehicle.Exists();
        }

        private void ObserveDeveloperTask(
            string task,
            int actorHandle,
            int targetHandle,
            int vehicleHandle,
            string reason)
        {
            LSDeveloperRuntime.TaskIssued(
                _log == null ? string.Empty : _log.SessionId,
                "NPCResponse",
                task,
                actorHandle,
                targetHandle,
                vehicleHandle,
                reason);
        }

        private bool WasPressed(Keys key, ref bool wasDown)
        {
            bool isDown = (GetAsyncKeyState((int)key) & 0x8000) != 0;
            bool pressed = isDown && !wasDown;
            wasDown = isDown;
            return pressed;
        }

        private void SynchronizeKeyStates()
        {
            _acceptKeyDown = (GetAsyncKeyState((int)_controls.AcceptKey) & 0x8000) != 0;
            _rejectKeyDown = (GetAsyncKeyState((int)_controls.RejectKey) & 0x8000) != 0;
            _secureKeyDown = (GetAsyncKeyState((int)_controls.SecureKey) & 0x8000) != 0;
        }

        private void ResetKeyStates()
        {
            _acceptKeyDown = false;
            _rejectKeyDown = false;
            _secureKeyDown = false;
        }

        private static void Notify(string message)
        {
            Notification.PostTicker(message, false, false);
        }
    }
}
