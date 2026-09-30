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
    /// Local Police civilian and traffic-contact owner. Construction is passive;
    /// runtime contacts are gated by Police Authority and Patrol.
    /// </summary>
    // The original boundary note is retained above. The owner below now runs
    // only through the explicit Authority and Patrol gates described here.
    /// <summary>
    /// Owns the small, Police-only civilian contact loop.
    ///
    /// This is deliberately local: it considers one nearby foot pedestrian or
    /// one nearby driver, never scans the city, never owns Gang data, and never
    /// creates a Dispatch incident. PoliceCore calls it only during an active
    /// Police Authority patrol.
    /// </summary>
    internal sealed class LSPDNPCResponse
    {
        private const float DefaultFootContactRadius = 6.5f;
        private const float DefaultTrafficContactRadius = 28.0f;
        private const float FootTrafficContactSpeed = 7.5f;
        private const float DefaultLocalTrafficCautionRadius = 12.0f;
        private const float DefaultActiveSceneTrafficCautionRadius = 18.0f;
        // Eight metres is useful as a caution area, but was far too large for
        // a frame-refreshed no-collision shield. Keep a small emergency bubble
        // only for vehicles that are actually closing on the officer.
        private const float ImmediateCollisionGuardRadius = 3.25f;
        private const float StoppedTrafficSpeed = 6.0f;
        private const float LocalTrafficCautionSpeed = 4.0f;
        private const float ActiveSceneTrafficCautionSpeed = 7.0f;
        private const float ActiveSceneTrafficDepartureRadius = 7.0f;
        private const float TrafficWindowContactRadius = 5.5f;
        private const float TrafficDriverWindowContactRadius = 4.25f;
        private const float InteractionReleaseDistance = 32.0f;
        private const float MaximumPullOverTargetDistance = 45.0f;
        private const float MaximumPullOverElevationDifference = 8.0f;
        private const float StationArrivalRadius = 24.0f;
        private const float FollowReleaseDistance = 350.0f;
        private const float DefaultDeadSubjectReleaseDistance = 80.0f;
        private const float BackupContainmentInteractionRadius = 12.0f;
        private const float FleeMovementSpeed = 3.0f;
        private const float FleeMovementDistance = 2.5f;
        private const int ContactPreparationMilliseconds = 700;
        private const int FootGreetingMilliseconds = 1150;
        private const int StationFollowTimeoutMilliseconds = 180000;
        private const int SubjectHoldMilliseconds = 3000;
        private const int TrafficCautionMilliseconds = 500;
        private const int FleeMemoryMilliseconds = 30000;
        private const int SceneReactionScanMilliseconds = 1000;
        private const int DeescalationScanMilliseconds = 1250;
        private const int ApproachScanMilliseconds = 1000;
        private const int ApproachCandidateMilliseconds = 4500;
        private const int DefaultCollisionGuardMilliseconds = 900;
        private const int TrafficDrivingStyle = 786603;
        private const int FootPlayerArrestMilliseconds = 2600;
        private const float FootPlayerArrestInteractionRadius = 3.5f;
        private const int FootResistanceTaskRecoveryMilliseconds = 1800;
        private const int SuspiciousApproachTaskRecoveryMilliseconds = 1400;
        private const int DeadSubjectMinimumHoldMilliseconds = 15000;
        // Backup's physical handoff is deliberately bounded, but it needs a
        // separate window from the officer's initial approach.  The previous
        // 45-second shared window expired while the subject was still being
        // navigated to the rear door.
        private const int BackupPhysicalEscortTimeoutSeconds = 90;
        private const int BackupPhysicalEscortAttachRetryMilliseconds = 900;
        private const int BackupPhysicalEscortMaximumAttachAttempts = 3;
        private const int BackupCuffPoseRefreshMilliseconds = 2500;
        private const float BackupPhysicalEscortAttachRadius = 1.9f;
        private const float BackupEscortDoorRadius = 2.8f;

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
            Greeting,
            Documents,
            HandcuffedIdle,
            CompliantKneel
        }

        private sealed class CollisionGuard
        {
            internal Vehicle Vehicle;
            internal Entity Target;
            internal DateTime ExpiresAt;
        }

        private readonly LSImmersiveLog _log;
        private readonly LSPDGangDataIntegration _gangData;
        private readonly LSPDProfile _profile;
        private readonly LSNPCDatabase _database;
        private readonly Dictionary<int, DateTime> _recentlyReleasedVehicles =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _recentSceneReactions =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _recentDeescalations =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _recentApproachCandidates =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _recentSceneVehicleDepartures =
            new Dictionary<int, DateTime>();
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
        private readonly Random _random = new Random();
        private readonly LSPDControlBindings _controls;
        private readonly LSPoliceNpcResponseSettings _settings;
        private readonly LSPoliceTrafficSettings _trafficSettings;
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
        private Vehicle _subjectVehicle;
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
        private DateTime _nextAnimationRequest = DateTime.MinValue;
        private DateTime _animationLoadDeadline = DateTime.MinValue;
        private DateTime _backupPhaseDeadline = DateTime.MinValue;
        private DateTime _nextBackupSubjectTask = DateTime.MinValue;
        private DateTime _lastBackupPhysicalEscortAttachAt = DateTime.MinValue;
        private DateTime _lastBackupEscortAnimationAt = DateTime.MinValue;
        private DateTime _deferredDeadSubjectCreatedAt = DateTime.MinValue;
        private DateTime _nextDeferredDeadSubjectScanAt = DateTime.MinValue;
        private Vector3 _fleeLastPosition = Vector3.Zero;
        private Vector3 _trafficPullOverPosition = Vector3.Zero;
        private Vector3 _interactionAnchor = Vector3.Zero;
        private Vector3 _footStationDestination = Vector3.Zero;
        private Vector3 _trafficStationDestination = Vector3.Zero;
        private Vector3 _backupEscortRootOffset = Vector3.Zero;
        private string _footStationName = string.Empty;
        private string _trafficStationName = string.Empty;
        private string _backupEscortAnimationClip = string.Empty;
        private Ped _deferredDeadSubject;
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
        private bool _approachCandidateStopped;
        private bool _backupRequested;
        private bool _backupBackgroundTransport;
        private bool _backupEscortTaskIssued;
        private bool _backupEntryTaskIssued;
        private bool _backupVehicleDoorOpened;
        private bool _backupPhysicalEscortAttached;
        private int _backupPhysicalEscortAttachAttempts;
        private bool _deferredDeadSubjectWasPersistent;
        private bool _backupCustodyOfferPresented;
        private bool _footPlayerArrested;
        private bool _secureKeyDown;
        private PendingAnimation _pendingAnimation;
        private int _pendingAnimationDuration;
        private LSPDNPCRecord _currentRecord;
        private bool _interactionKeyDown;
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
            : this(log, null, null, null, null, null, null)
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
            : this(log, gangData, controls, null, null, null, null)
        {
        }

        internal LSPDNPCResponse(
            LSImmersiveLog log,
            LSPDGangDataIntegration gangData,
            LSPDControlBindings controls,
            LSPDProfile profile)
            : this(log, gangData, controls, profile, null, null, null)
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
        {
            _log = log ?? throw new ArgumentNullException("log");
            _gangData = gangData;
            _controls = controls ?? LSPDControlBindings.Default();
            _profile = profile;
            _settings = settings ?? LSPoliceNpcResponseSettings.Default();
            _trafficSettings = trafficSettings ?? LSPoliceTrafficSettings.Default();
            _database = database;
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
                        || _stage == InteractionStage.FootFleeing
                        || _stage == InteractionStage.TrafficFleeing
                        || _stage == InteractionStage.TrafficResisting
                        || _stage == InteractionStage.FootRefused
                        || _stage == InteractionStage.TrafficRefused);
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
                if (IsUsable(_subjectVehicle) && _stage == InteractionStage.TrafficFleeing)
                    return _subjectVehicle.Position;
                return IsUsable(_subject) ? _subject.Position : Vector3.Zero;
            }
        }

        internal Ped ActiveSubject { get { return IsUsable(_subject) ? _subject : null; } }
        internal Vehicle ActiveSubjectVehicle { get { return IsUsable(_subjectVehicle) ? _subjectVehicle : null; } }

        internal bool CanBeginPlayerVehicleCustody
        {
            get
            {
                return HasActiveInteraction
                    && _subjectVehicle == null
                    && _footPlayerArrested
                    && !_backupRequested
                    && _stage == InteractionStage.FootAwaitingBackup
                    && IsUsable(_subject)
                    && !_subject.IsDead;
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
                    && _backupPhysicalEscortAttached
                    && IsUsable(_subject)
                    && IsUsable(_backupOfficer)
                && IsBackupSubjectAttachedToOfficer(_subject, _backupOfficer);
            }
        }

        /// <summary>
        /// True for the whole pre-door escort phase, including the short
        /// interval before the measured attachment is established. Backup
        /// must not replace the escort officer's navigation task with a
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
            _subjectPersistenceCaptured = false;
            _subjectVehiclePersistenceCaptured = false;
            ClearInteractionState();
            _log.Runtime(
                "NPC_CUSTODY_TRANSFERRED_TO_CONVOY",
                "Ped=" + handle + "; Owner=Convoy; ArrestAlreadyCompleted=true");
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
                : "Backup requested for the compliant citizen. Keep the subject at the contact until the unit arrives.";
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
            _backupOfficer = officer;
            _backupVehicle = vehicle;
            _stage = subjectWasFleeing
                ? (_subjectVehicle == null
                    ? InteractionStage.FootBackupContained
                    : InteractionStage.TrafficBackupContained)
                : InteractionStage.BackupSecuring;
            _backupPhaseDeadline = now.AddSeconds(45);
            _nextBackupSubjectTask = now;
            _backupEscortTaskIssued = false;
            _backupEntryTaskIssued = false;
            _backupVehicleDoorOpened = false;
            _backupPhysicalEscortAttached = false;
            _lastBackupPhysicalEscortAttachAt = DateTime.MinValue;
            _lastBackupEscortAnimationAt = DateTime.MinValue;
            _backupPhysicalEscortAttachAttempts = 0;
            _backupEscortRootOffset = Vector3.Zero;
            _backupEscortAnimationClip = string.Empty;
            SetSubjectPoliceAware(_subject);
            try
            {
                Vehicle occupiedVehicle = _subject.CurrentVehicle;
                if (IsUsable(occupiedVehicle)
                    && occupiedVehicle.Handle != vehicle.Handle)
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, occupiedVehicle, true);
                    occupiedVehicle.Speed = 0.0f;
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, _subject, occupiedVehicle, 0);
                }
                else if (!_subject.IsInVehicle() && !_footPlayerArrested)
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
                            Notify("Backup has contained the fleeing citizen. Approach and press "
                                + _controls.InteractionKey + " to review the custody response.");
                        }
                        else
                        {
                            _log.Runtime("NPC_BACKUP_TRAFFIC_FLEEING_CONTAINMENT_CONFIRMED",
                                "Driver=" + _subject.Handle + "; SubjectVehicle="
                                + _subjectVehicle.Handle + "; Officer=" + officer.Handle
                                + "; BackupVehicle=" + vehicle.Handle);
                            Notify("Backup stopped the fleeing driver. Approach and press "
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
                if (_subject.CurrentVehicle != null && _subject.CurrentVehicle.Exists()
                    && _subjectVehicle != null && _subjectVehicle.Exists()
                    && _subject.CurrentVehicle.Handle == _subjectVehicle.Handle
                    && now >= _nextBackupSubjectTask)
                {
                    _nextBackupSubjectTask = now.AddSeconds(5);
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, true);
                        Function.Call(Hash.TASK_LEAVE_VEHICLE, _subject, _subjectVehicle, 0);
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
                bool playerAlreadySecured = _subjectVehicle == null && _footPlayerArrested;
                try
                {
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, true);
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
                    else
                    {
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            officer, _subject, 1200);
                        Function.Call(Hash.TASK_ARREST_PED, officer, _subject);
                        ObserveDeveloperTask(
                            "TASK_ARREST_PED",
                            officer.Handle,
                            _subject.Handle,
                            _subjectVehicle == null ? 0 : _subjectVehicle.Handle,
                            "BackupSecuring");
                    }
                }
                catch (Exception ex) { _log.Exception("NPC_BACKUP_HANDCUFF_FLAG_FAILED", ex); }
                BeginPendingAnimation(PendingAnimation.HandcuffedIdle, 1800, now);
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
                _backupVehicleDoorOpened = false;
                _backupPhysicalEscortAttached = false;
                _lastBackupPhysicalEscortAttachAt = DateTime.MinValue;
                _lastBackupEscortAnimationAt = DateTime.MinValue;
                _backupPhysicalEscortAttachAttempts = 0;
                _backupEscortRootOffset = Vector3.Zero;
                _backupEscortAnimationClip = string.Empty;
                _log.Runtime("NPC_BACKUP_SUBJECT_SECURED",
                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle);
                Notify("Citizen secured. Backup is escorting the subject to the Police vehicle.");
                return false;
            }

            if (_stage == InteractionStage.BackupEscort)
            {
                if (now < _animationHoldUntil)
                    return false;

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

                // The officer, not the prisoner, owns movement during the
                // escort.  Keeping the prisoner attached at the measured
                // world offset prevents an independent FollowNavMesh task
                // from making the subject wander, open the door, or lose the
                // officer around the vehicle.
                if (!_backupVehicleDoorOpened
                    && (!_backupPhysicalEscortAttached
                    || !IsBackupSubjectAttachedToOfficer(_subject, officer))
                    )
                {
                    if (_subject.Position.DistanceTo(officer.Position)
                        > BackupPhysicalEscortAttachRadius)
                    {
                        return false;
                    }

                    if (!TryAttachBackupSubjectToOfficer(officer, now))
                        return false;

                    _backupPhysicalEscortAttached = true;
                    _backupEscortTaskIssued = false;
                    _nextBackupSubjectTask = now;
                    MaintainBackupEscortAnimation(officer, now, true);
                    _log.Runtime("NPC_BACKUP_ESCORT_MOVEMENT_STARTED",
                        "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                        + "; Vehicle=" + vehicle.Handle
                        + "; Attachment=MeasuredEscortRoot");
                }

                MaintainBackupEscortAnimation(officer, now, true);
                Vector3 entry = vehicle.GetOffsetPosition(new Vector3(2.0f, -1.0f, 0.0f));
                Vector3 escortTarget = BackupEscortOfficerDoorTarget(entry, officer);
                bool escortAtDoor = officer.Position.DistanceTo(escortTarget)
                    <= BackupEscortDoorRadius;

                if (!escortAtDoor)
                {
                    if (!_backupEscortTaskIssued || now >= _nextBackupSubjectTask)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                officer, escortTarget.X, escortTarget.Y, escortTarget.Z,
                                1.15f, -1, 1.0f, 1, 0f);
                            bool firstEscortTask = !_backupEscortTaskIssued;
                            _backupEscortTaskIssued = true;
                            _nextBackupSubjectTask = now.AddSeconds(6);
                            if (firstEscortTask)
                                _log.Runtime("NPC_BACKUP_ESCORT_TO_VEHICLE_STARTED",
                                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                                    + "; Vehicle=" + vehicle.Handle + "; Door=2");
                        }
                        catch (Exception ex) { _log.Exception("NPC_BACKUP_ESCORT_TO_VEHICLE_FAILED", ex); }
                    }
                    return false;
                }

                if (!_backupVehicleDoorOpened)
                {
                    try
                    {
                        MaintainBackupEscortAnimation(officer, now, false);
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            officer, _subject, 1000);
                        Function.Call(Hash.TASK_OPEN_VEHICLE_DOOR,
                            officer, vehicle, 4000, 2, 1.0f);
                        Function.Call(Hash.SET_VEHICLE_DOOR_OPEN,
                            vehicle, 2, false, false);
                        _backupVehicleDoorOpened = true;
                        _nextBackupSubjectTask = now.AddMilliseconds(1200);
                        _log.Runtime("NPC_BACKUP_ESCORT_REACHED_VEHICLE",
                            "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                            + "; Vehicle=" + vehicle.Handle + "; Door=2");
                        _log.Runtime("NPC_BACKUP_PRISONER_DOOR_OPENED",
                            "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                            + "; Vehicle=" + vehicle.Handle + "; Door=2");
                    }
                    catch (Exception ex)
                    {
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

        private bool TryAttachBackupSubjectToOfficer(Ped officer, DateTime now)
        {
            if (!IsUsable(_subject) || _subject.IsDead
                || !IsUsable(officer) || officer.IsDead)
                return false;
            if (IsBackupSubjectAttachedToOfficer(_subject, officer))
                return true;

            if (_lastBackupPhysicalEscortAttachAt != DateTime.MinValue
                && now < _lastBackupPhysicalEscortAttachAt.AddMilliseconds(
                    BackupPhysicalEscortAttachRetryMilliseconds))
                return false;
            if (_backupPhysicalEscortAttachAttempts
                >= BackupPhysicalEscortMaximumAttachAttempts)
                return false;

            _backupPhysicalEscortAttachAttempts++;
            _lastBackupPhysicalEscortAttachAt = now;
            try
            {
                // Keep the existing world spacing instead of inventing a
                // wrist offset. The officer becomes the sole movement owner;
                // the cuffed citizen travels with that officer until the rear
                // door is reached.
                Vector3 relativeOffset = Function.Call<Vector3>(
                    Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS,
                    officer,
                    _subject.Position.X,
                    _subject.Position.Y,
                    _subject.Position.Z);
                _backupEscortRootOffset = relativeOffset;
                int rootBone = Function.Call<int>(Hash.GET_PED_BONE_INDEX, officer, 0);
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                    _subject,
                    officer,
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

                if (!IsBackupSubjectAttachedToOfficer(_subject, officer))
                {
                    _log.Runtime("NPC_BACKUP_PHYSICAL_ESCORT_ATTACH_REJECTED",
                        "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                        + "; Attempt=" + _backupPhysicalEscortAttachAttempts
                        + "; Bone=SKEL_ROOT; Offset=" + relativeOffset);
                    return false;
                }

                _log.Runtime("NPC_BACKUP_PHYSICAL_ESCORT_ATTACHED",
                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                    + "; Attempt=" + _backupPhysicalEscortAttachAttempts
                    + "; Bone=SKEL_ROOT; Offset=" + relativeOffset
                    + "; CollisionWithEscort=false");
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_BACKUP_PHYSICAL_ESCORT_ATTACH_FAILED", ex);
                return false;
            }
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

            _backupPhysicalEscortAttached = false;
            _backupEscortRootOffset = Vector3.Zero;
            _lastBackupPhysicalEscortAttachAt = DateTime.MinValue;
            _lastBackupEscortAnimationAt = DateTime.MinValue;
            _backupPhysicalEscortAttachAttempts = 0;
            _backupEscortAnimationClip = string.Empty;
        }

        private void MaintainBackupEscortAnimation(Ped officer, DateTime now, bool walking)
        {
            if (!IsUsable(_subject) || _subject.IsDead
                || !IsUsable(officer)
                || !IsBackupSubjectAttachedToOfficer(_subject, officer))
                return;

            const string dictionary = "anim@move_m@prisoner_cuffed";
            string clip = walking ? "walk" : "idle";
            bool clipChanged = !string.Equals(
                _backupEscortAnimationClip, clip, StringComparison.Ordinal);
            if (!clipChanged
                && _lastBackupEscortAnimationAt != DateTime.MinValue
                && now < _lastBackupEscortAnimationAt.AddMilliseconds(
                    BackupCuffPoseRefreshMilliseconds))
                return;

            try
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, dictionary);
                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dictionary))
                {
                    _lastBackupEscortAnimationAt = now;
                    return;
                }
                if (!clipChanged && Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM,
                    _subject, dictionary, clip, 3))
                {
                    _lastBackupEscortAnimationAt = now;
                    return;
                }

                Function.Call(Hash.TASK_PLAY_ANIM,
                    _subject,
                    dictionary,
                    clip,
                    3.0f,
                    -2.0f,
                    -1,
                    walking ? 1 : 49,
                    1.0f,
                    false,
                    false,
                    false);
                _backupEscortAnimationClip = clip;
                _lastBackupEscortAnimationAt = now;
                _log.Runtime("NPC_BACKUP_ESCORT_ANIMATION",
                    "Ped=" + _subject.Handle + "; Officer=" + officer.Handle
                    + "; Dictionary=" + dictionary + "; Clip=" + clip);
            }
            catch (Exception ex)
            {
                _lastBackupEscortAnimationAt = now;
                _log.Exception("NPC_BACKUP_ESCORT_ANIMATION_FAILED", ex);
            }
        }

        private void MaintainBackupLoadingState()
        {
            if (!IsUsable(_subject) || _subject.IsDead)
                return;
            try
            {
                // Loading owns the subject after the measured escort is
                // released. Keep custody active while GTA performs the real
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

        private Vector3 BackupEscortOfficerDoorTarget(Vector3 doorPosition, Ped officer)
        {
            if (!IsUsable(_subject) || !IsUsable(officer))
                return doorPosition;

            try
            {
                Vector3 doorOffset = Function.Call<Vector3>(
                    Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS,
                    officer,
                    doorPosition.X,
                    doorPosition.Y,
                    doorPosition.Z);
                Vector3 officerOffset = new Vector3(
                    doorOffset.X - _backupEscortRootOffset.X,
                    doorOffset.Y - _backupEscortRootOffset.Y,
                    doorOffset.Z - _backupEscortRootOffset.Z);
                return officer.GetOffsetPosition(officerOffset);
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_BACKUP_ESCORT_DOOR_TARGET_FAILED", ex);
                return doorPosition;
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
            Notify("Citizen entered the Backup vehicle. Backup is leaving the scene; you may resume patrol.");
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
            Notify("Backup has custody and is leaving the scene. You may resume patrol.");
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
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, false);
                    _subject.CanSwitchWeapons = true;
                    // The Backup vehicle is already leaving the player's
                    // scene. Keep the current vehicle task intact while the
                    // ambient subject is released from Police ownership.
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
            _backupPhysicalEscortAttached = false;
            _backupPhysicalEscortAttachAttempts = 0;
            _backupEscortRootOffset = Vector3.Zero;
            _backupEscortAnimationClip = string.Empty;
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

        internal string StatusText
        {
            get
            {
                switch (_stage)
                {
                    case InteractionStage.FootPreparing:
                        return "Citizen noticed the officer and is stopping for contact.";
                    case InteractionStage.FootGreeting:
                        return "Citizen acknowledged Police Anyi and is greeting the officer.";
                    case InteractionStage.FootDocuments:
                        return RecordStatus("Citizen contact active. " + DecisionHint("release", "detain / pursue"));
                    case InteractionStage.FootRefused:
                        return "Citizen did not comply. Y = release, N = pursue.";
                    case InteractionStage.FootStationFollow:
                        return "Citizen complied and is walking to the selected Police station.";
                    case InteractionStage.FootFleeing:
                        return _fleeMovementConfirmed
                            ? "Citizen fled. Pursue on foot until the person stops or is down."
                            : "Citizen is attempting to leave. Waiting for real movement.";
                    case InteractionStage.FootResisting:
                        return "Citizen is resisting the Police contact. Use appropriate force to end the threat or request Backup.";
                    case InteractionStage.TrafficPullingOver:
                        return "Driver is pulling over for Police contact.";
                    case InteractionStage.TrafficPreparing:
                        return "Driver is pulled over. Approach the window and press Interact to investigate.";
                    case InteractionStage.TrafficDocuments:
                        return RecordStatus("Traffic stop active. " + DecisionHint("release", "detain / pursue"));
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
                                + _controls.EmergencyKey + " to request Backup."
                            : "Subject is compliant and waiting for Police Backup transport.";
                    case InteractionStage.FootAwaitingPlayerArrest:
                        return "Citizen complied and is waiting for your physical arrest. Press "
                            + _controls.SecureKey + " while standing beside the citizen.";
                    case InteractionStage.FootPlayerArresting:
                        return "Physical arrest is in progress. Stay beside the citizen.";
                    case InteractionStage.FootBackupContained:
                        return "Backup has contained the fleeing citizen. Press "
                            + _controls.InteractionKey + " to review the custody response.";
                    case InteractionStage.TrafficAwaitingBackup:
                        return "Subject is compliant and waiting for Police Backup transport.";
                    case InteractionStage.TrafficBackupContained:
                        return "Backup has stopped the fleeing driver. Approach and press "
                            + _controls.InteractionKey + " to review the custody response.";
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

        /// <summary>
        /// Runs only while PoliceCore has an explicitly active Authority session
        /// and Patrol is on. G selects one valid subject, Y releases/completes,
        /// and N refuses/escalates. All key handling is edge-triggered.
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

            MaintainDeferredDeadSubject(player, now);

            // Once Backup owns a loaded prisoner, the player-facing contact
            // is released, but the subject reference must remain alive until
            // Backup confirms station delivery. Do not let the next Patrol
            // scan reclaim that subject as a new ambient contact.
            if (_backupBackgroundTransport)
            {
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
                if (HasActiveInteraction)
                {
                    ReleaseSubject(true);
                    ClearInteractionState();
                }
                ReleaseApproachCandidate();
                ApplyLocalTrafficCaution(player, now, false, false);
                if (now >= _nextDeescalationScan)
                {
                    _nextDeescalationScan = now.AddMilliseconds(DeescalationScanMilliseconds);
                    ApplyPoliceDeescalation(player, now);
                }
                MaintainCollisionGuards(player, now);
                TrimReleasedVehicleMemory(now);
                TrimSceneReactionMemory(now);
                TrimDeescalationMemory(now);
                TrimApproachMemory(now);
                TrimSceneVehicleDepartureMemory(now);
                ResetKeyStates();
                return;
            }

            // The current Dispatch owner remains independent. While it has an
            // offer or active scene, no new civilian/traffic contact is started.
            // PoliceCore also refuses a new dispatch handoff while this owner has
            // a contact. A quiet foot patrol may keep one short-lived approach
            // candidate so a civilian can notice the officer before G begins a
            // real interaction.
            if (_settings.InteractionsEnabled)
                MaintainApproachCandidate(player, now, contactBlocked);
            else
                ReleaseApproachCandidate();

            bool interactionKeyPressed = _settings.InteractionsEnabled
                && allowGameplayInput
                && WasPressed(_controls.InteractionKey, ref _interactionKeyDown);
            bool secureKeyPressed = _settings.InteractionsEnabled
                && allowGameplayInput
                && WasPressed(_controls.SecureKey, ref _secureKeyDown);
            bool controllerInteraction = _settings.InteractionsEnabled
                && allowGameplayInput
                && Game.IsControlJustPressed(GTA.Control.Attack)
                && !player.IsAiming
                && !player.IsShooting;
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
                && (interactionKeyPressed
                    || controllerInteraction || trafficSignal))
            {
                if (HasActiveInteraction
                    && (_stage == InteractionStage.FootBackupContained
                        || _stage == InteractionStage.TrafficBackupContained)
                    && (interactionKeyPressed || controllerInteraction))
                    PresentBackupCustodyOffer(player);
                else if (contactBlocked && !HasActiveInteraction)
                    Notify("Dispatch is active. NPC contact is paused until the Police scene is clear.");
                else
                    BeginNearestInteraction(player, trafficSignal);
            }
            if (_settings.InteractionsEnabled && allowGameplayInput && HasActiveInteraction
                && secureKeyPressed && _stage == InteractionStage.FootAwaitingPlayerArrest)
                BeginFootPlayerArrest(player, now);
            bool controllerAccept = _settings.InteractionsEnabled && allowGameplayInput
                && Game.IsControlJustPressed(GTA.Control.FrontendAccept);
            bool controllerReject = _settings.InteractionsEnabled && allowGameplayInput
                && Game.IsControlJustPressed(GTA.Control.FrontendCancel);
            if (_settings.InteractionsEnabled && allowGameplayInput && HasActiveInteraction
                && (WasPressed(_controls.AcceptKey, ref _acceptKeyDown) || controllerAccept))
                AcceptInteraction();
            if (_settings.InteractionsEnabled && allowGameplayInput && HasActiveInteraction
                && (WasPressed(_controls.RejectKey, ref _rejectKeyDown) || controllerReject))
                RejectInteraction(player);

            MaintainInteraction(player, now);
            ApplyActiveSceneCivilianReaction(player, now, activeRoadScene);
            ApplyLocalTrafficCaution(
                player,
                now,
                activeRoadScene && _trafficSettings.StopTrafficAtActiveScenes,
                HasActiveInteraction);
            if (now >= _nextDeescalationScan)
            {
                _nextDeescalationScan = now.AddMilliseconds(DeescalationScanMilliseconds);
                ApplyPoliceDeescalation(player, now);
            }
            MaintainCollisionGuards(player, now);
            TrimReleasedVehicleMemory(now);
            TrimSceneReactionMemory(now);
            TrimDeescalationMemory(now);
            TrimApproachMemory(now);
            TrimSceneVehicleDepartureMemory(now);
        }

        internal string BeginInteractionCommand()
        {
            if (!_settings.Enabled || !_settings.InteractionsEnabled)
                return "NPC interactions are disabled in LS Immersive settings.";
            Ped player = Game.Player.Character;
            if (!IsUsable(player))
                return "The player character is unavailable.";
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
            AcceptInteraction();
            return HasActiveInteraction
                ? StatusText
                : "NPC interaction completed and the subject was released.";
        }

        internal string RejectInteractionCommand()
        {
            if (!HasActiveInteraction)
                return "No NPC interaction is awaiting a decision.";
            RejectInteraction(Game.Player.Character);
            return HasActiveInteraction
                ? StatusText
                : "NPC interaction ended and the subject was released or fled.";
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
            _recentSceneVehicleDepartures.Clear();
            _collisionGuards.Clear();
            _nextDeescalationScan = DateTime.MinValue;
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

        /// <summary>
        /// The stop signal chooses one driver; it does not silently finish the
        /// roadside investigation. The officer must leave the patrol vehicle,
        /// walk to the driver's window, and deliberately press Interact before
        /// the identification gesture begins.
        /// </summary>
        private bool TryBeginTrafficWindowInvestigation(Ped player)
        {
            if (_stage != InteractionStage.TrafficPreparing)
                return false;
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle))
                return false;

            DateTime now = DateTime.UtcNow;
            if (now < _readyAt)
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
                Notify("Move closer to the driver's window, then press Interact to investigate.");
                return true;
            }

            _stage = InteractionStage.TrafficDocuments;
            _readyAt = DateTime.MaxValue;
            _nextSubjectHold = DateTime.MinValue;
            HoldTrafficSubject();
            try { _subject.Task.LookAt(player, 3500); }
            catch (Exception ex) { _log.Exception("NPC_TRAFFIC_DRIVER_LOOK_FAILED", ex); }
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
            _subjectVehicle = null;
            _currentRecord = null;
            _backupRequested = false;
            _suspiciousFootContact = IsUsable(_approachCandidate)
                && _approachCandidate.Handle == ped.Handle
                && _approachCandidateSuspicious;
            CaptureInteractionPersistence();
            _stage = InteractionStage.FootPreparing;
            DateTime now = DateTime.UtcNow;
            _readyAt = now.AddMilliseconds(ContactPreparationMilliseconds);
            _expiresAt = now.AddMilliseconds(InteractionTimeoutMilliseconds);
            _nextSubjectHold = DateTime.MinValue;
            SetSubjectPoliceAware(ped);
            _animationHoldUntil = DateTime.MinValue;
            _interactionAnchor = ped.Position;
            ped.Task.StandStill(6000);
            OrientSubjectToOfficer(ped, player, 1800);
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

        private void BeginTrafficInteraction(Ped player, Ped driver, Vehicle vehicle)
        {
            if (!IsUsable(vehicle))
            {
                Notify("The selected driver vehicle is no longer available.");
                return;
            }

            _subject = driver;
            _subjectVehicle = vehicle;
            _currentRecord = null;
            _backupRequested = false;
            _suspiciousTrafficContact = IsUsable(_approachCandidate)
                && _approachCandidateTraffic
                && _approachCandidateSuspicious
                && _approachCandidate.IsInVehicle();
            CaptureInteractionPersistence();
            DateTime now = DateTime.UtcNow;
            _readyAt = now.AddMilliseconds(ContactPreparationMilliseconds);
            _expiresAt = now.AddMilliseconds(InteractionTimeoutMilliseconds);
            _nextSubjectHold = DateTime.MinValue;
            _animationHoldUntil = DateTime.MinValue;
            SetSubjectPoliceAware(driver);
            if (!BeginTrafficPullOver(now))
            {
                _stage = InteractionStage.TrafficPreparing;
                HoldTrafficSubject();
                _log.Runtime("NPC_TRAFFIC_PULL_OVER_UNAVAILABLE",
                    "Driver=" + driver.Handle + "; Vehicle=" + vehicle.Handle
                    + "; Fallback=HoldCurrentPosition");
            }
            if (_suspiciousTrafficContact)
            {
                LSPDNPCRecord adverseRecord;
                if (_database != null && _database.TryGetAdverseRecord(driver, true, out adverseRecord))
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

            Vector3 roadside;
            try
            {
                if (!TryGetValidatedPullOverPosition(out roadside))
                    return false;

                _stage = InteractionStage.TrafficPullingOver;
                _trafficPullOverPosition = roadside;
                _pullOverDeadline = now.AddSeconds(_settings.PullOverTimeoutSeconds);
                _pullOverRecoveryIssued = false;
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
                // Probe only the selected vehicle's immediate road space. This
                // avoids a distant/invalid result on elevated roads while still
                // giving ParkVehicle a real local street target.
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
            // otherwise valid road locations. Try both, but accept only a
            // candidate that remains close to the live vehicle and on the same
            // elevation.
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

                    roadside = candidate;
                    _log.Runtime("NPC_TRAFFIC_PULL_OVER_TARGET_STREET",
                        "Driver=" + (_subject == null ? 0 : _subject.Handle)
                        + "; Vehicle=" + _subjectVehicle.Handle
                        + "; Probe=" + probeIndex
                        + "; Unoccupied=" + unoccupied
                        + "; Offset=" + distance.ToString("0.0")
                        + "; Elevation=" + elevation.ToString("0.0"));
                    return true;
                }
            }

            _log.Runtime("NPC_TRAFFIC_PULL_OVER_TARGET_UNAVAILABLE",
                "Driver=" + (_subject == null ? 0 : _subject.Handle)
                + "; Vehicle=" + _subjectVehicle.Handle
                + "; Reason=NoLocalStreetCandidate");
            return false;
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

            Vector3 windowPosition;
            try
            {
                if (_subjectVehicle.Bones != null
                    && _subjectVehicle.Bones.Contains("door_dside_f"))
                {
                    EntityBone driverDoor = _subjectVehicle.Bones["door_dside_f"];
                    if (driverDoor != null && driverDoor.IsValid)
                    {
                        windowPosition = driverDoor.Position;
                        return player.Position.DistanceTo(windowPosition)
                            <= TrafficDriverWindowContactRadius;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_DRIVER_WINDOW_QUERY_FAILED", ex);
            }

            // Addon vehicles do not always expose the standard driver-door
            // bone. The local left-side offset is the bounded compatibility
            // fallback for those models.
            try
            {
                windowPosition = _subjectVehicle.GetOffsetPosition(
                    new Vector3(-1.35f, 0.2f, 0.65f));
                if (IsFiniteWorldPosition(windowPosition)
                    && player.Position.DistanceTo(windowPosition)
                        <= TrafficDriverWindowContactRadius)
                    return true;
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_TRAFFIC_DRIVER_WINDOW_FALLBACK_FAILED", ex);
            }

            // Keep the existing close-driver tolerance for unusual custom
            // models whose door geometry is unavailable, but do not accept a
            // front-bumper interaction when the driver-side point is known.
            return player.Position.DistanceTo(_subject.Position)
                <= TrafficWindowContactRadius;
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
            bool yieldedAtRoadside = distanceToRoadside <= 9.0f
                || (Math.Abs(_subjectVehicle.Speed) <= 1.0f && now >= _readyAt);
            if (yieldedAtRoadside)
            {
                _stage = InteractionStage.TrafficPreparing;
                _readyAt = now.AddMilliseconds(ContactPreparationMilliseconds);
                _nextSubjectHold = DateTime.MinValue;
                HoldTrafficSubject();
                _log.Runtime("NPC_TRAFFIC_PULL_OVER_COMPLETED",
                    "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle);
                Notify("Driver pulled over and remains seated. Walk to the window to investigate.");
                return;
            }

            if (now >= _pullOverDeadline)
            {
                // The driver did not reach the calculated shoulder in time.
                // Keep the interaction truthful: hold the driver at the actual
                // position instead of claiming a completed roadside pull-over.
                _stage = InteractionStage.TrafficPreparing;
                _readyAt = now.AddMilliseconds(ContactPreparationMilliseconds);
                _nextSubjectHold = DateTime.MinValue;
                HoldTrafficSubject();
                _log.Runtime("NPC_TRAFFIC_PULL_OVER_TIMEOUT",
                    "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle
                    + "; Distance=" + distanceToRoadside.ToString("0.0"));
                Notify("Driver yielded at the current position and remains seated for contact.");
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

            MaintainPendingAnimation(now);

            bool subjectExists = IsUsable(_subject);
            bool vehicleExists = _subjectVehicle == null || IsUsable(_subjectVehicle);
            if (!subjectExists || _subject.IsDead || !vehicleExists)
            {
                bool subjectDown = subjectExists && _subject.IsDead;
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
                    DeferDeadFootSubject(_subject, now);
                    ClearInteractionState();
                    _log.Runtime("NPC_FOOT_SUBJECT_DOWN",
                        availability + "; Ped=" + downedHandle
                        + "; Cleanup=DeferredUntilPlayerLeavesArea");
                    _log.Runtime("NPC_FOOT_MISSION_JUSTIFIED",
                        "Ped=" + downedHandle + "; Outcome="
                        + (downedStage == InteractionStage.FootResisting
                            ? "ResistingCitizenNeutralized"
                            : downedStage == InteractionStage.FootFleeing
                                ? "FleeingCitizenNeutralized"
                                : "FootCitizenNeutralized"));
                    Notify("The citizen is down. The foot-citizen incident is resolved; the scene will remain until you leave the area.");
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
                    Notify("The driver is down. The traffic incident is resolved; the scene will remain until you leave the area.");
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
                    ? "NPC interaction closed because the subject is down."
                    : "NPC interaction ended because the subject is unavailable.");
                return;
            }

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
                MaintainFootPlayerArrest(now);
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
                    OrientSubjectToOfficer(_subject, player, 1400);
                    BeginPendingAnimation(PendingAnimation.Greeting, FootGreetingMilliseconds, now);
                    _readyAt = DateTime.MaxValue;
                    _log.Runtime("NPC_FOOT_GREETING", "Ped=" + _subject.Handle);
                    Notify("Citizen acknowledged Police Anyi and is preparing to greet the officer.");
                }
                else if (_stage == InteractionStage.FootGreeting)
                {
                    _stage = InteractionStage.FootDocuments;
                    OrientSubjectToOfficer(_subject, player, 1400);
                    BeginPendingAnimation(PendingAnimation.Documents, DocumentPresentationMilliseconds, now);
                    _readyAt = DateTime.MaxValue;
                    _log.Runtime("NPC_FOOT_DOCUMENTS_READY", "Ped=" + _subject.Handle);
                    Notify("Citizen is providing identification.");
                }
            }

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
            if (_backupCustodyOfferPresented)
            {
                Notify("Backup custody response is ready. " + _controls.AcceptKey
                    + " = accept, " + _controls.RejectKey + " = decline.");
                return;
            }

            _backupCustodyOfferPresented = true;
            _log.Runtime("NPC_BACKUP_CUSTODY_OFFER_PRESENTED",
                "Ped=" + _subject.Handle
                + "; Officer=" + (_backupOfficer == null ? 0 : _backupOfficer.Handle)
                + "; Vehicle=" + (_backupVehicle == null ? 0 : _backupVehicle.Handle)
                + "; SubjectType=" + (_stage == InteractionStage.TrafficBackupContained ? "Traffic" : "Foot")
                + (_currentRecord == null ? string.Empty : "; " + RecordStatus("Record")));
            string recordText = _currentRecord == null
                ? string.Empty
                : "\n" + _currentRecord.ScreenText;
            Notify((_stage == InteractionStage.TrafficBackupContained
                ? "Backup has stopped the fleeing driver. Backup will escort the driver to its Police vehicle. "
                : "Backup has contained the citizen. Backup will escort the suspect to its Police vehicle. ")
                + recordText + "\n"
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
            _backupCustodyOfferPresented = false;
            _stage = InteractionStage.BackupSecuring;
            _backupPhaseDeadline = now.AddSeconds(45);
            _nextBackupSubjectTask = now;
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
            int subjectHandle = _subject == null ? 0 : _subject.Handle;
            if (traffic)
                ReleaseTrafficSubject();
            else
                ReleaseFootSubjectNaturally();
            ClearInteractionState();
            _log.Runtime(traffic ? "NPC_TRAFFIC_CONTACT_RELEASED" : "NPC_FOOT_CONTACT_RELEASED", "Ped=" + subjectHandle);
            Notify(traffic ? "Driver cleared and released naturally." : "Citizen contact completed and released naturally.");
        }

        private void RejectInteraction(Ped player)
        {
            if (!HasActiveInteraction)
            {
                Notify("No NPC interaction is awaiting a decision.");
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
                if (_stage == InteractionStage.TrafficRefused)
                {
                    StartTrafficFlee(player);
                    return;
                }

                int resistanceChance = Math.Max(0, Math.Min(100,
                    _settings.CitizenResistChancePercent));
                int fleeChance = Math.Max(0, Math.Min(
                    100 - resistanceChance,
                    _settings.TrafficFleeChancePercent));
                int roll = _random.Next(100);
                _log.Runtime("NPC_TRAFFIC_NEGATIVE_DECISION",
                    "Driver=" + _subject.Handle
                    + "; Roll=" + roll
                    + "; ResistanceChance=" + resistanceChance
                    + "; FleeChance=" + fleeChance);

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

                _stage = InteractionStage.TrafficAwaitingBackup;
                HoldTrafficSubject();
                _log.Runtime("NPC_TRAFFIC_DETENTION_READY", "Driver=" + _subject.Handle);
                Notify("Driver remained compliant. Request Backup with " + _controls.EmergencyKey + " for physical station transport.");
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

                _log.Runtime("NPC_FOOT_NEGATIVE_DECISION",
                    "Ped=" + _subject.Handle
                    + "; Roll=" + roll
                    + "; ResistanceChance=" + resistanceChance
                    + "; FleeChance=" + fleeChance);

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

            // A subject that already refused once keeps the existing
            // documented second-decision path: the next negative decision
            // becomes a real foot pursuit rather than silently changing to a
            // different outcome.
            StartFootFlee(player);
        }

        private void StartFootCompliance()
        {
            if (!IsUsable(_subject))
                return;

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
            if (_stage != InteractionStage.FootAwaitingPlayerArrest)
                return;
            if (!IsUsable(player) || !IsUsable(_subject) || _subject.IsDead)
            {
                Notify("The compliant citizen is no longer available for arrest.");
                return;
            }
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
                _animationHoldUntil = now.AddMilliseconds(FootPlayerArrestMilliseconds);
                SetSubjectPoliceAware(_subject);
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, true);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                _subject.Task.ClearAll();
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    player, _subject, 1000);
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    _subject, player, 1000);
                Function.Call(Hash.TASK_ARREST_PED, player, _subject);
                _log.Runtime("NPC_FOOT_PLAYER_ARREST_STARTED",
                    "Ped=" + _subject.Handle + "; Player=" + player.Handle
                    + "; Method=TaskArrestPed");
                Notify("Physical arrest in progress. Stay beside the citizen until the handcuffs are secured.");
            }
            catch (Exception ex)
            {
                _stage = InteractionStage.FootAwaitingPlayerArrest;
                _animationHoldUntil = DateTime.MinValue;
                _log.Exception("NPC_FOOT_PLAYER_ARREST_START_FAILED", ex);
                try { _subject.Task.HandsUp(15000); } catch { }
                Notify("The physical arrest could not be started. Try again while standing beside the citizen.");
            }
        }

        private void MaintainFootPlayerArrest(DateTime now)
        {
            if (!IsUsable(_subject) || _subject.IsDead)
                return;
            if (now < _animationHoldUntil)
                return;

            try
            {
                _subject.Task.ClearAll();
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, _subject, true);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                _footPlayerArrested = true;
                _stage = InteractionStage.FootAwaitingBackup;
                _readyAt = DateTime.MaxValue;
                _expiresAt = now.AddMinutes(6);
                EnsureSubjectBlip("Handcuffed Citizen");
                BeginPendingAnimation(PendingAnimation.HandcuffedIdle, 1800, now);
                _log.Runtime("NPC_FOOT_PLAYER_ARREST_COMPLETED",
                    "Ped=" + _subject.Handle + "; Method=TaskArrestPed");
                Notify("Citizen is handcuffed and secured. Press "
                    + _controls.EmergencyKey
                    + " to use your nearby Police car/van for personal custody, or request Backup when you are on a Police motorcycle.");
            }
            catch (Exception ex)
            {
                _log.Exception("NPC_FOOT_PLAYER_ARREST_CONFIRM_FAILED", ex);
                _stage = InteractionStage.FootAwaitingPlayerArrest;
                _footPlayerArrested = false;
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
                if (now < _animationHoldUntil)
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
            if (!IsUsable(_subject) || _subject.IsDead)
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

        private void HoldTrafficSubject()
        {
            if (!IsUsable(_subjectVehicle))
                return;
            try
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, true);
                Function.Call(Hash.SET_VEHICLE_BRAKE, _subjectVehicle, true);
                if (Math.Abs(_subjectVehicle.Speed) > 0.25f)
                {
                    // The handbrake alone does not cancel an ambient driving
                    // task. A bounded brake action stops the selected driver
                    // without clearing the driver's seated state or touching
                    // unrelated traffic.
                    Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION,
                        _subject, _subjectVehicle, 27, 1200);
                    _subjectVehicle.Speed = 0.0f;
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
                if (IsUsable(currentVehicle) && IsUsable(_subjectVehicle)
                    && currentVehicle.Handle == _subjectVehicle.Handle)
                {
                    // The fleeing driver must leave the original vehicle before
                    // the player is offered custody. Keep the stopped vehicle
                    // held without turning the driver into a wandering ped.
                    HoldTrafficSubject();
                    return;
                }

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

            try
            {
                DateTime now = DateTime.UtcNow;
                ReleaseSubjectInteractionLockForMovement(driver);
                _stage = InteractionStage.TrafficFleeing;
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
                if (_database != null && _database.TryGetAdverseRecord(driver, true, out adverseRecord))
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

        private void StartTrafficResistance(Ped player)
        {
            Ped driver = _subject;
            Vehicle vehicle = _subjectVehicle;
            if (!IsUsable(driver) || !IsUsable(player))
            {
                Notify("Driver resistance could not begin because the subject is unavailable.");
                return;
            }

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
            if (now < _nextResistanceTaskAt)
                return;

            _nextResistanceTaskAt = now.AddMilliseconds(FootResistanceTaskRecoveryMilliseconds);
            try
            {
                SetSubjectPoliceAware(_subject);
                _subject.BlockPermanentEvents = true;
                _subject.CanSwitchWeapons = false;
                if (!_subject.IsInCombatAgainst(player) && !_subject.IsShooting)
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
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle) || !IsDriver(_subject, _subjectVehicle))
            {
                ReleaseSubject(true);
                ClearInteractionState();
                Notify("Traffic pursuit ended because the driver or vehicle is unavailable.");
                return;
            }

            float speed = Math.Abs(_subjectVehicle.Speed);
            if (!_fleeMovementConfirmed && now >= _lastFleeSampleAt.AddSeconds(1))
            {
                float moved = _subjectVehicle.Position.DistanceTo(_fleeLastPosition);
                _fleeLastPosition = _subjectVehicle.Position;
                _lastFleeSampleAt = now;
                if (speed >= FleeMovementSpeed || moved >= FleeMovementDistance)
                {
                    _fleeMovementConfirmed = true;
                    _log.Runtime("NPC_TRAFFIC_FLEE_MOVEMENT_CONFIRMED",
                        "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle +
                        "; Speed=" + speed.ToString("0.0"));
                    Notify("Traffic flee confirmed. Pursue the moving vehicle until it stops or crashes.");
                }
                if (_fleeMovementConfirmed && speed < 1.0f)
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
                    IssueTrafficFleeTask(player, now, 30.0f);
                }
                if (now >= _fleeStartedAt.AddSeconds(_settings.TrafficTaskRecoverySeconds + 4))
                {
                    _recentlyReleasedVehicles.Remove(_subjectVehicle.Handle);
                    SetSubjectPoliceAware(_subject);
                    _stage = InteractionStage.TrafficRefused;
                    HoldTrafficSubject();
                    _log.Runtime("NPC_TRAFFIC_FLEE_NOT_CONFIRMED", "Driver=" + _subject.Handle + "; Vehicle=" + _subjectVehicle.Handle);
                    Notify("Driver did not get away and remains stopped for Police contact.");
                }
                return;
            }

            if (_fleeMovementConfirmed && _fleeStoppedAt != DateTime.MinValue
                && now >= _fleeStoppedAt.AddSeconds(2))
            {
                _recentlyReleasedVehicles.Remove(_subjectVehicle.Handle);
                if (_backupRequested)
                {
                    _stage = InteractionStage.TrafficAwaitingBackup;
                    SetSubjectPoliceAware(_subject);
                    HoldTrafficSubject();
                    _log.Runtime("NPC_TRAFFIC_FLEE_CONTAINED", "Driver=" + _subject.Handle);
                    Notify("The fleeing vehicle stopped. Backup is continuing to the tracked driver.");
                }
                else
                {
                    int handle = _subject.Handle;
                    ReleaseSubject(true);
                    ClearInteractionState();
                    _log.Runtime("NPC_TRAFFIC_FLEE_STOPPED", "Driver=" + handle);
                    Notify("The fleeing vehicle stopped. Reopen NPC interaction to investigate the driver.");
                }
            }
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
            if (!IsUsable(_subject) || !IsUsable(_subjectVehicle) || !IsUsable(player))
                return;
            try
            {
                Vector3 destination = FindRoadDestinationAwayFrom(_subjectVehicle.Position, player.Position, 90.0f);
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, false);
                _subjectVehicle.Speed = Math.Max(4.0f, Math.Abs(_subjectVehicle.Speed));
                _subject.Task.ClearAll();
                Function.Call(Hash.SET_DRIVER_ABILITY, _subject, 1.0f);
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, _subject, 0.75f);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    _subject, _subjectVehicle,
                    destination.X, destination.Y, destination.Z,
                    speed, TrafficDrivingStyle, 8.0f);
                ObserveDeveloperTask(
                    "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE",
                    _subject.Handle,
                    player.Handle,
                    _subjectVehicle.Handle,
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
            // This is a short, local safety bubble. It is not a global traffic
            // override: only a vehicle demonstrably travelling into the officer's
            // space is affected, and only the configured small cap is touched
            // in a live scene.
            if (!_trafficSettings.Enabled || now < _nextTrafficCaution)
                return;
            _nextTrafficCaution = now.AddMilliseconds(TrafficCautionMilliseconds);

            bool localPoliceScene = activeRoadScene || protectInteraction;
            float radius = localPoliceScene
                ? ActiveSceneTrafficCautionRadius
                : LocalTrafficCautionRadius;
            Ped[] nearby = World.GetNearbyPeds(player, radius);
            if (nearby == null)
                return;

            Vehicle playerVehicle = player.CurrentVehicle;
            int interventions = 0;
            int maximumInterventions = localPoliceScene
                ? _settings.MaximumAffectedTrafficVehicles
                : Math.Min(1, _settings.MaximumAffectedTrafficVehicles);
            if (maximumInterventions <= 0)
                return;
            foreach (Ped ped in nearby)
            {
                if (!IsUsable(ped) || ped.Handle == player.Handle || !ped.IsInVehicle())
                    continue;
                Vehicle vehicle = ped.CurrentVehicle;
                if (!IsUsable(vehicle) || vehicle == _subjectVehicle
                    || vehicle == playerVehicle || IsProtectedSceneVehicle(vehicle)
                    || !IsDriver(ped, vehicle)
                    || WasRecentlyReleased(vehicle))
                    continue;
                Entity collisionTarget;
                float distance;
                if (!TryGetNearestProtectedCollisionTarget(
                    player,
                    vehicle,
                    radius,
                    out collisionTarget,
                    out distance)
                    || !IsTrafficCollisionRisk(
                        collisionTarget,
                        vehicle,
                        distance,
                        localPoliceScene))
                    continue;

                ApplyTrafficCollisionGuard(
                    collisionTarget,
                    ped,
                    vehicle,
                    distance,
                    now,
                    localPoliceScene);
                interventions++;
                if (interventions >= maximumInterventions)
                    break;
            }
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

        private void ApplyActiveSceneCivilianReaction(Ped player, DateTime now, bool activeRoadScene)
        {
            // An offer or an en-route call is not a physical crowd scene. Only a
            // confirmed roadside scene may cause a small number of ordinary
            // civilians to react, and a held weapon alone never rolls the whole
            // nearby crowd into a run animation.
            if (!activeRoadScene || HasActiveInteraction
                || now < _nextSceneReactionScan)
                return;
            _nextSceneReactionScan = now.AddMilliseconds(SceneReactionScanMilliseconds);

            Ped[] nearby = World.GetNearbyPeds(player, ActiveSceneCivilianRadius);
            if (nearby == null)
                return;

            bool nearbyGunfire = player.IsShooting || nearby.Any(ped =>
                IsUsable(ped) && ped.IsShooting);
            if (!nearbyGunfire && !player.IsAiming)
                return;

            int budget = nearbyGunfire
                ? _settings.MaximumAffectedPedestrians
                : Math.Min(2, _settings.MaximumAffectedPedestrians);
            foreach (Ped ped in nearby)
            {
                if (budget <= 0)
                    break;
                if (!CanUseAsCivilian(ped, player) || ped.IsInVehicle() || ped.IsInCombat)
                    continue;
                if (_subject != null && ped.Handle == _subject.Handle)
                    continue;

                DateTime nextReaction;
                if (_recentSceneReactions.TryGetValue(ped.Handle, out nextReaction)
                    && now < nextReaction)
                    continue;
                _recentSceneReactions[ped.Handle] = now.AddMilliseconds(SceneReactionCooldownMilliseconds);

                int reaction = _random.Next(100);
                try
                {
                    if ((nearbyGunfire && reaction < 60)
                        || (!nearbyGunfire && reaction < 25))
                    {
                        Function.Call(Hash.TASK_SMART_FLEE_PED, ped, player, 55.0f, -1, false, false);
                        _log.Runtime("NPC_ACTIVE_SCENE_CIVILIAN_MOVED_AWAY", "Ped=" + ped.Handle);
                    }
                    else if (reaction < 70)
                    {
                        ped.Task.LookAt(player, 1100);
                        _log.Runtime("NPC_ACTIVE_SCENE_CIVILIAN_WATCHING", "Ped=" + ped.Handle);
                    }
                    else
                    {
                        ped.Task.ReactAndFlee(player);
                        _log.Runtime("NPC_ACTIVE_SCENE_CIVILIAN_FLED", "Ped=" + ped.Handle);
                    }
                    budget--;
                }
                catch (Exception ex)
                {
                    _log.Exception("NPC_ACTIVE_SCENE_REACTION_FAILED", ex);
                }
            }
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
                    ped.Task.ClearAll();
                    if (player.IsAiming && !player.IsShooting)
                    {
                        ped.Task.HandsUp(5500);
                        ped.Task.LookAt(player, 2500);
                        _log.Runtime("NPC_POLICE_DEESCALATION", "Ped=" + ped.Handle + "; Civilian surrendered to the armed officer.");
                    }
                    else
                    {
                        Function.Call(Hash.TASK_SMART_FLEE_PED, ped, player, 45.0f, -1, false, false);
                        _log.Runtime("NPC_POLICE_DEESCALATION", "Ped=" + ped.Handle + "; Civilian aggression redirected into retreat.");
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

            // Once the Police vehicle closes the gap, convert the ambient
            // suspicion into the normal seated traffic-contact flow. This is
            // still the same pull-over, document, and outcome pipeline; the
            // awareness layer does not decide guilt or start an arrest.
            if (distance <= 8.0f)
                BeginTrafficInteraction(player, driver, vehicle);
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

            if (distance <= 3.8f)
            {
                Ped suspicious = _approachCandidate;
                BeginFootInteraction(player, suspicious);
            }
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
            bool activeRoadScene)
        {
            try
            {
                if (distance <= ImmediateCollisionGuardRadius)
                {
                    // Do not slow every vehicle in the awareness radius.  A
                    // speed cap on the whole twelve-to-twenty-eight metre
                    // sample made normal traffic crawl whenever Police had an
                    // active remote owner.  Only an imminent collision gets a
                    // brief physical speed intervention.
                    float cappedSpeed = activeRoadScene
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
                        "Vehicle=" + vehicle.Handle + "; Distance=" + distance.ToString("0.0"),
                        TimeSpan.FromSeconds(2));
                }

                // Traffic departure is an emergency scene-edge response, not a
                // general active-activity behavior.  Keep it inside the close
                // roadside space so ordinary passing cars retain their native
                // route and are not repeatedly cleared/reassigned.
                if (activeRoadScene && distance <= ActiveSceneTrafficDepartureRadius)
                {
                    DateTime nextDeparture;
                    if (!_recentSceneVehicleDepartures.TryGetValue(vehicle.Handle, out nextDeparture)
                        || now >= nextDeparture)
                    {
                        Vector3 destination = FindRoadDestinationAwayFrom(vehicle.Position, target.Position, 45.0f);
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, false);
                        driver.Task.ClearAll();
                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                            driver, vehicle,
                            destination.X, destination.Y, destination.Z,
                            10.0f, TrafficDrivingStyle, 8.0f);
                        _recentSceneVehicleDepartures[vehicle.Handle] =
                            now.AddSeconds(_settings.SceneReactionCooldownSeconds);
                        _log.Runtime("NPC_ACTIVE_SCENE_TRAFFIC_DEPARTURE", "Vehicle=" + vehicle.Handle);
                    }
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
            try
            {
                if (IsUsable(_subjectVehicle))
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _subjectVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_BRAKE, _subjectVehicle, false);
                }
                if (IsUsable(_subject))
                {
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
            if (_pendingAnimation == PendingAnimation.None || !IsUsable(_subject))
                return;

            string dictionary;
            string clip;
            ResolvePendingAnimation(out dictionary, out clip);
            try
            {
                if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dictionary))
                {
                    int flags = _pendingAnimation == PendingAnimation.HandcuffedIdle
                        || _pendingAnimation == PendingAnimation.CompliantKneel
                        ? 49 : 48;
                    Function.Call(Hash.TASK_PLAY_ANIM, _subject, dictionary, clip,
                        3.0f, -2.0f, _pendingAnimationDuration, flags, 0f,
                        false, false, false);
                    PendingAnimation completed = _pendingAnimation;
                    _pendingAnimation = PendingAnimation.None;
                    _animationHoldUntil = now.AddMilliseconds(_pendingAnimationDuration);
                    if (completed == PendingAnimation.Greeting)
                    {
                        _readyAt = _animationHoldUntil;
                        Notify("Citizen greeted Police Anyi and remains for identification.");
                    }
                    else if (completed == PendingAnimation.Documents)
                    {
                        _readyAt = DateTime.MaxValue;
                        PresentCitizenRecord();
                    }
                    else if (completed == PendingAnimation.CompliantKneel)
                    {
                        Notify("Citizen is kneeling with hands visible and awaiting the physical arrest.");
                    }
                    return;
                }

                if (now >= _animationLoadDeadline)
                {
                    PendingAnimation timedOut = _pendingAnimation;
                    _pendingAnimation = PendingAnimation.None;
                    _animationHoldUntil = now.AddMilliseconds(700);
                    if (_log != null)
                    {
                        if (timedOut == PendingAnimation.CompliantKneel)
                            _log.Runtime("NPC_FOOT_COMPLIANCE_POSE_FALLBACK",
                                "Ped=" + _subject.Handle + "; Fallback=HandsUp");
                        else
                            _log.StateFailure("NPC_ANIMATION_STREAM_TIMEOUT",
                                "Animation=" + timedOut + "; Ped=" + _subject.Handle);
                    }
                    try { _subject.Task.LookAt(Game.Player.Character, 1400); } catch { }
                    if (timedOut == PendingAnimation.Greeting)
                        _readyAt = _animationHoldUntil;
                    else if (timedOut == PendingAnimation.Documents)
                    {
                        _readyAt = DateTime.MaxValue;
                        PresentCitizenRecord();
                    }
                    return;
                }

                if (now >= _nextAnimationRequest)
                    RequestPendingAnimation(now);
            }
            catch (Exception ex)
            {
                _pendingAnimation = PendingAnimation.None;
                if (_log != null)
                    _log.Exception("NPC_ANIMATION_START_FAILED", ex);
                if (_stage == InteractionStage.FootGreeting)
                    _readyAt = now.AddMilliseconds(700);
                else if (_stage == InteractionStage.FootDocuments
                    || _stage == InteractionStage.TrafficDocuments)
                    PresentCitizenRecord();
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
            if (_pendingAnimation == PendingAnimation.Greeting)
            {
                dictionary = "gestures@m@standing@casual";
                clip = "gesture_hello";
                return;
            }
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
            clip = "givetake1_a";
        }

        private void PresentCitizenRecord()
        {
            if (_currentRecord == null && _database != null)
                _database.TryGetRecord(_subject, _subjectVehicle != null, out _currentRecord);

            if (_currentRecord == null)
            {
                Notify("Identification was presented, but the citizen database is unavailable. "
                    + DecisionHint("release", "detain / pursue"));
                return;
            }

            Notify(_currentRecord.ScreenText + "\n" + DecisionHint("release", "detain / pursue"));
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

        private void ClearInteractionState()
        {
            if (_backupPhysicalEscortAttached)
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
            _nextAnimationRequest = DateTime.MinValue;
            _animationLoadDeadline = DateTime.MinValue;
            _backupPhaseDeadline = DateTime.MinValue;
            _nextBackupSubjectTask = DateTime.MinValue;
            _lastBackupPhysicalEscortAttachAt = DateTime.MinValue;
            _lastBackupEscortAnimationAt = DateTime.MinValue;
            _fleeLastPosition = Vector3.Zero;
            _trafficPullOverPosition = Vector3.Zero;
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
            _approachCandidateStopped = false;
            _backupRequested = false;
            _backupBackgroundTransport = false;
            _footPlayerArrested = false;
            _secureKeyDown = false;
            _backupEscortTaskIssued = false;
            _backupEntryTaskIssued = false;
            _backupVehicleDoorOpened = false;
            _backupPhysicalEscortAttached = false;
            _backupPhysicalEscortAttachAttempts = 0;
            _backupEscortRootOffset = Vector3.Zero;
            _backupEscortAnimationClip = string.Empty;
            _backupCustodyOfferPresented = false;
            _backupOfficer = null;
            _backupVehicle = null;
            _pendingAnimation = PendingAnimation.None;
            _pendingAnimationDuration = 0;
            _currentRecord = null;
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

        private void TrimSceneVehicleDepartureMemory(DateTime now)
        {
            if (_recentSceneVehicleDepartures.Count == 0)
                return;
            var expired = new List<int>();
            foreach (KeyValuePair<int, DateTime> pair in _recentSceneVehicleDepartures)
                if (now >= pair.Value)
                    expired.Add(pair.Key);
            foreach (int handle in expired)
                _recentSceneVehicleDepartures.Remove(handle);
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
            _interactionKeyDown = (GetAsyncKeyState((int)_controls.InteractionKey) & 0x8000) != 0;
            _acceptKeyDown = (GetAsyncKeyState((int)_controls.AcceptKey) & 0x8000) != 0;
            _rejectKeyDown = (GetAsyncKeyState((int)_controls.RejectKey) & 0x8000) != 0;
            _secureKeyDown = (GetAsyncKeyState((int)_controls.SecureKey) & 0x8000) != 0;
        }

        private void ResetKeyStates()
        {
            _interactionKeyDown = false;
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
