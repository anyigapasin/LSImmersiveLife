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
    internal enum DispatchDamageAttackerClass
    {
        PolicePlayer,
        ProjectBackupOfficer,
        ProjectPoliceResponseOfficer,
        DispatchSuspect,
        DispatchVictim,
        AmbientWorldSceneActor,
        UnrelatedCivilian,
        UnrelatedVanillaPolice,
        UnrelatedGangModelActor,
        UnknownExternalActor
    }

    internal sealed class DispatchDamageOwnershipEvidence
    {
        internal DispatchDamageAttackerClass AttackerClass;
        internal int AttackerHandle;
        internal int WeaponHash;
        internal int GameTime;
        internal string DamageKind;
        internal int RawBone;
        internal DateTime ConfirmedAt;
    }

    // A controlled suspect's injury state follows the same Ped across
    // Dispatch/Convoy ownership. The current owner alone may process it.
    internal sealed class LSPDSuspectInjuryState
    {
        internal Ped Actor;
        internal int OriginalHealth;
        internal int OriginalMaximumHealth;
        internal int OriginalSelectedWeaponHash;
        internal int LastHealth;
        internal int LastMaximumHealth;
        internal int DamageTaken;
        internal int RegisteredHits;
        internal int KnownDamageHits;
        internal int DownedHits;
        internal int KnownDownedHits;
        internal int MeleeHits;
        internal int WeaponHits;
        internal int FatalVitalHits;
        internal int FatalDownedHits;
        internal int ManagedMaximumHealth;
        internal int ArrestableWoundedHealthFloor;
        internal int HeadHits;
        internal int FaceHits;
        internal int NeckHits;
        internal int ChestHits;
        internal int StomachHits;
        internal int LastValidDamageBone;
        internal DateTime LastValidDamageBoneAt = DateTime.MinValue;
        internal DateTime LastConvoyHealthCheckAt = DateTime.MinValue;
        internal string OccurrenceId;
        internal string LastDamageKind;
        internal int LastDamageWeaponHash;
        internal int LastDamageAttackerHandle;
        internal string LastDamageAttackerClass;
        internal int LastDamageGameTime;
        internal int LastDamageRecordAgeMilliseconds = -1;
        internal int LastRawDamageBone;
        internal int LastEffectiveDamageBone;
        internal string LastDamageZone;
        internal bool LastDamageRecordAvailable;
        internal bool LastDamageRecordFresh;
        internal bool LastDamageRecordDuplicate;
        internal bool LastNativeDamageCheckAvailable;
        internal bool LastNativeReportsDamageByAttacker;
        internal bool LastDamageEventConfirmed;
        internal bool DamageAttributionBaselineAvailable;
        internal string DamageAttributionBaselineStatus;
        internal HashSet<string> ProcessedDamageRecordKeys = new HashSet<string>(
            StringComparer.Ordinal);
        internal Queue<string> ProcessedDamageRecordOrder = new Queue<string>();
        internal Queue<DispatchDamageOwnershipEvidence> ConfirmedDamageOwnershipHistory =
            new Queue<DispatchDamageOwnershipEvidence>();
        internal DateTime PendingDamageSummaryStartedAt = DateTime.MinValue;
        internal int PendingDamageSamples;
        internal int PendingObservedHealthLoss;
        internal int PendingConfirmedDamageEvents;
        internal int PendingUnconfirmedDamageSamples;
        internal int PendingFloorApplications;
        internal int PendingHealthBefore;
        internal int PendingLatestDamageDelta;
        internal DateTime LastHealthAnomalyEvidenceAt = DateTime.MinValue;
        internal DateTime LastWoundedHealthCorrectionLogAt = DateTime.MinValue;
        internal int WoundedHealthCorrections;
        internal int DispatchWoundedHealthReserve;
        internal bool FatalCauseConfirmed;
        internal bool FatalOutcomeExpected;
        internal bool FatalOutcomeCompromised;
        internal bool FatalOwnershipReconciliationComplete;
        internal bool FatalOwnershipReconciliationPendingLogged;
        internal DateTime FatalOwnershipReconciliationStartedAt = DateTime.MinValue;
        internal string FatalOutcomeOwnerClass;
        internal int FatalOutcomeOwnerHandle;
        internal string FatalOutcomeEvidenceSource;
        internal string FatalOutcomeReason;
        internal string RuntimeOwner = "Dispatch";
        internal bool OriginalCanSwitchWeapons;
        internal bool Wounded;
        internal bool Fatal;
        internal bool WeaponDisabledByInjury;
        internal bool VehiclePursuitDisarmedOutcomeResolved;
        internal bool WritheTaskStarted;
        internal bool MeleeReactionPending;
        internal bool ConvoyTerminalLogged;
        internal DateTime MeleeReactionUntil = DateTime.MinValue;
        internal DateTime NextMeleeRecoveryCheckAt = DateTime.MinValue;
        internal bool OriginalCanSufferCriticalHits;
        internal bool CriticalHitGuardActive;
        internal bool OriginalDamageProofsCaptured;
        internal bool OriginalBulletProof;
        internal bool OriginalFireProof;
        internal bool OriginalExplosionProof;
        internal bool OriginalCollisionProof;
        internal bool OriginalMeleeProof;
        internal bool OriginalSteamProof;
        internal bool OriginalUnknownDamageProof;
        internal bool OriginalDrownProof;
        internal bool CustodyDamageProtectionActive;
        internal bool CustodyProtectionCommitted;
        internal int CriticalHitGuardReassertions;
        internal bool CriticalHitGuardFailureLogged;
        internal bool CompliantPlayerVulnerabilityActive;
        internal bool VehicleFlightOriginalCollisionProof;
        internal bool VehicleFlightCollisionProofGuardActive;
        internal bool VehicleFlightCollisionProofErrorLogged;
        internal DateTime NextPursuitInterventionSampleAt = DateTime.MinValue;
        internal DateTime LastPursuitInterventionAt = DateTime.MinValue;
        internal DateTime LastPursuitInterventionLogAt = DateTime.MinValue;
        internal int LastPursuitInterventionVehicleHandle;
        internal int LastPursuitInterventionSourceHandle;
        internal string LastPursuitInterventionCause;
        internal Vector3 LastKnownAlivePosition;
        internal float LastKnownAliveHeading;
        internal bool LastKnownAlivePositionSupported;
        internal int ManagedRelationshipGroupHash;
        internal int InvalidTerminalRecoveryCount;
        internal bool InvalidTerminalRecoveryAttempted;
        internal int WoundedTaskAttempts;
        internal DateTime NextWoundedTaskCheckAt = DateTime.MinValue;
        internal bool WoundedTaskSurrenderFallbackUsed;
        internal string WoundedTaskPresentation;
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
        private const float MurderVictimInvestigationRadius = 3.0f;
        private const int MappedInteriorEntrySampleMilliseconds = 250;
        private const int MappedInteriorDoorwayEvidenceWindowSeconds = 20;
        private const int MappedInteriorEscalationCheckMilliseconds = 350;
        internal const int InitialPatrolOfferDelaySeconds = 30;
        private const int ArrestRadius = 5;
        private const int SurrenderSettleSeconds = 2;
        private const int HandcuffedPedConfigFlag = 120;
        // Keep GTA's authored arrest task alive through the visible handcuff
        // motion before Dispatch takes custody of the secured suspect.
        private const int PlayerHandcuffAnimationMilliseconds = 6500;
        private const int PlayerHandcuffMaximumAttempts = 2;
        private const int PlayerHandcuffMaximumPairedAnimationAttempts = 2;
        private const int PlayerHandcuffAnimationLoadTimeoutSeconds = 6;
        private const float SynchronizedArrestMaximumDistance = 2.6f;
        private const int PlayerHandcuffSceneProgressStallMilliseconds = 1800;
        private const int PlayerHandcuffSceneClipLossGraceMilliseconds = 1000;
        private const float PlayerHandcuffMinimumProgressPhase = 0.15f;
        private const float PlayerHandcuffCommitPhase = 0.95f;
        private const float PlayerHandcuffMaximumSceneDisplacement = 2.0f;
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
        private const int SceneVehicleGroundRetryLimit = 1;
        private const int SceneVehicleGroundRetryDelayMilliseconds = 3000;
        private const int DispatchSceneActorObservationIntervalMilliseconds = 500;
        private const int DispatchSceneActorObservationWindowSeconds = 15;
        private const int MaximumDispatchSceneActorsToObserve = 32;
        private const int MaximumDispatchSceneActorTransitionRecords = 12;
        private const float DispatchSceneActorObservationMovementMeters = 2.5f;
        private const int ManagedSuspectPresenceCheckMilliseconds = 750;
        private const int ManagedSuspectMissingMinimumSamples = 3;
        private const int ManagedSuspectMissingConfirmationMilliseconds = 1500;
        private const int WoundedSuspectTaskRetryMilliseconds = 1250;
        private const int WoundedSuspectTaskMaximumAttempts = 2;
        private const int SceneResolutionPendingMaximumSeconds = 15;
        private const int SceneResolutionRetryDelayMilliseconds = 3000;
        private const int PlayerRadiusPendingRetryCount = 1;
        private const int PlayerRadiusFallbackFailureCacheSeconds = 20;
        private const float PlayerRadiusFallbackFailureMatchDistanceMeters = 45f;
        private const int DispatchSceneCandidateFailureCacheSeconds = 90;
        private const int DispatchSceneCandidateTransientFailureCacheSeconds = 15;
        private const int MaximumDispatchSceneCandidateFailureCacheEntries = 512;
        private const int MaximumPreparedSceneCandidateFallthroughAttempts = 12;
        private const int MaximumDispatchSceneResolutionsPerUpdate = 1;
        private const int DispatchSceneSearchContinuationDelayMilliseconds = 500;
        private const float DispatchSceneFailureCacheMovementMeters = 20f;
        private const int MaximumPlayerRadiusVehicleDefinitionCandidates = 2;
        private const int MaximumPlayerRadiusRoadFootDefinitionCandidates = 1;
        private const int MaximumPlayerRadiusGenericFootDefinitionCandidates = 1;
        private const float PlayerRadiusVehicleRoadSnapDistance = 45f;
        private const int PairedSceneAnimationLoadTimeoutSeconds = 10;
        private const float PairedSceneAnimationApproachDistance = 38f;
        private const float GroupShootoutOnFootConfrontationRange = 8f;
        private const float DisturbanceOnFootConfrontationRange = 8f;
        private const int DisturbanceObservationGraceSeconds = 12;
        private const int GroupShootoutShotIntervalSeconds = 6;
        private const int GroupShootoutShotDurationMilliseconds = 650;
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
        private const int DispatchDamageRecordMaximumAgeMilliseconds = 1500;
        private const int MaximumTrackedDispatchDamageRecords = 256;
        private const int MaximumConfirmedDamageOwnershipHistory = 128;
        private const int DispatchFatalOwnershipReconciliationMilliseconds = 2500;
        private const int DispatchDamageSummaryIntervalSeconds = 1;
        private const int DispatchHealthAnomalyEvidenceCooldownSeconds = 5;
        private const int DispatchPursuitInterventionSampleMilliseconds = 100;
        private const int DispatchPursuitInterventionEvidenceMilliseconds = 2500;
        private const float DispatchPursuitBlockForwardMeters = 16f;
        private const float DispatchPursuitBlockLateralMeters = 4.5f;
        private const float DispatchPursuitBlockerSpeedLimit = 1.0f;
        private const int MaximumDispatchCollisionEvidenceVehicles = 3;
        private const float DispatchCollisionEvidenceRadius = 12.0f;
        private const int EmptyLocationCandidateRetrySeconds = 5;
        private const int EmptyLocationCandidateInteriorProbeSeconds = 1;
        private const float EmptyLocationCandidateMovementMeters = 20f;
        private const int MaximumDispatchLocationRepeatProtectionEntries = 128;
        private const float NearbyDispatchLocationRepeatProtectionMeters = 24f;
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
        private LSPDBackUp _backup;
        private LSPDPoliceResponse _policeResponse;
        private LSPDGangDataIntegration _gangData;
        private Vehicle _playerPatrolUtility;
        private bool _dispatchLocationRepeatFallbackActive;
        private readonly List<DeferredCleanup> _deferredCleanup =
            new List<DeferredCleanup>();
        private readonly Dictionary<int, DispatchSceneActorObservation>
            _dispatchSceneActorObservations =
                new Dictionary<int, DispatchSceneActorObservation>();
        private readonly Dictionary<int, ManagedSuspectPresenceObservation>
            _managedSuspectPresenceObservations =
                new Dictionary<int, ManagedSuspectPresenceObservation>();
        private readonly HashSet<int> _managedActorLostSuspectHandles =
            new HashSet<int>();
        private readonly Dictionary<string, DateTime> _definitionOfferedAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _completedOrDeclinedLocationAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, DateTime> _lastCompliantTaskAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastSecuredPoseTaskAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _securedPoseRetryAt =
            new Dictionary<int, DateTime>();
        private readonly HashSet<int> _securedPoseConfirmedPlayingHandles =
            new HashSet<int>();
        private readonly HashSet<int> _securedPoseInterruptedHandles =
            new HashSet<int>();
        private readonly Dictionary<int, LSPDSuspectInjuryState> _suspectInjuries =
            new Dictionary<int, LSPDSuspectInjuryState>();
        private readonly Dictionary<int, BackupArrestOperation> _backupArrestOperations =
            new Dictionary<int, BackupArrestOperation>();
        private readonly HashSet<string> _pendingSceneResolutionDefinitionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _pendingSceneResolutionInteriorIds =
            new HashSet<int>();
        private readonly Dictionary<string, PendingPlayerRadiusSceneCandidate>
            _pendingPlayerRadiusSceneCandidates =
                new Dictionary<string, PendingPlayerRadiusSceneCandidate>(
                    StringComparer.OrdinalIgnoreCase);
        private readonly List<PlayerRadiusFallbackCandidateFailure>
            _playerRadiusFallbackCandidateFailures =
                new List<PlayerRadiusFallbackCandidateFailure>();
        private readonly Dictionary<string, DispatchSceneCandidateFailure>
            _dispatchSceneCandidateFailures =
                new Dictionary<string, DispatchSceneCandidateFailure>(StringComparer.OrdinalIgnoreCase);

        private LSPDDispatchEvent _incident;
        private LSPDDispatchEvent _pendingOffer;
        private Blip _sceneBlip;
        private readonly Dictionary<int, Blip> _suspectBlips =
            new Dictionary<int, Blip>();
        private int _primarySuspectBlipHandle;
        private DateTime _nextSuspectBlipMaintenanceAt = DateTime.MinValue;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private DateTime _nextOfferAt = DateTime.MinValue;
        private string _pendingFootCatalogDefinitionId;
        private bool _pendingFootCatalogBypassesQuietPeriod;
        private Vector3 _pendingFootCatalogPlayerPosition;
        private bool _emptyLocationCandidateSearchCached;
        private Vector3 _emptyLocationCandidatePosition = Vector3.Zero;
        private int _emptyLocationCandidateInteriorId;
        private bool _emptyLocationCandidateInteriorIdAvailable;
        private DateTime _emptyLocationCandidateRetryAt = DateTime.MinValue;
        private DateTime _emptyLocationCandidateInteriorProbeAt = DateTime.MinValue;
        private DateTime _manualRequestRetryAt = DateTime.MinValue;
        private DateTime _nextMaintenance = DateTime.MinValue;
        private DateTime _nextInteriorPinMaintenanceAt = DateTime.MinValue;
        private DateTime _nextDispatchSceneActorObservationAt = DateTime.MinValue;
        private DateTime _nextManagedSuspectPresenceCheckAt = DateTime.MinValue;
        private string _managedSuspectPresenceOccurrenceId;
        private string _managedActorLostOccurrenceId;
        private string _observedDispatchSceneOccurrenceId;
        private DateTime _dispatchSceneActorObservationWindowStartedAt = DateTime.MinValue;
        private Vector3 _dispatchSceneFailureCachePlayerPosition = Vector3.Zero;
        private int _dispatchSceneFailureCachePlayerInteriorId;
        private bool _dispatchSceneFailureCachePlayerInteriorKnown;
        private bool _dispatchSceneFailureCacheContextInitialized;
        private int _preparedSceneCandidateFallthroughAttempts;
        private DateTime _playerHandcuffHoldUntil = DateTime.MinValue;
        private int _playerHandcuffAttempts;
        private DateTime _playerHandcuffAnimationLoadUntil = DateTime.MinValue;
        private string _playerHandcuffWaitingDictionary;
        private bool _playerHandcuffWaitingForAnimation;
        private bool _playerHandcuffPairedAnimationStarted;
        private int _playerHandcuffSceneId = -1;
        private DateTime _playerHandcuffAnimationVerifyAt = DateTime.MinValue;
        private bool _playerHandcuffAnimationVerified;
        private DateTime _playerHandcuffSceneStartedAt = DateTime.MinValue;
        private DateTime _playerHandcuffSceneLastProgressAt = DateTime.MinValue;
        private DateTime _playerHandcuffSceneClipInterruptedAt = DateTime.MinValue;
        private float _playerHandcuffSceneLastPhase = -1f;
        private float _playerHandcuffSceneHighestPhase;
        private bool _playerHandcuffSceneProgressConfirmed;
        private bool _playerHandcuffSceneCompleted;
        private Vector3 _playerHandcuffOfficerStartPosition = Vector3.Zero;
        private Vector3 _playerHandcuffSuspectStartPosition = Vector3.Zero;
        private bool _playerHandcuffPreserveTargetPosture;
        private LSImmersiveDictionaryAnimation.ArrestTargetPosture
            _playerHandcuffTargetPosture =
                LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid;
        private int _playerHandcuffPairedAnimationAttempts;
        private string _playerHandcuffDictionary;
        private string _playerHandcuffOfficerClip;
        private string _playerHandcuffSuspectClip;
        private Vector3 _playerHandcuffSceneOrigin = Vector3.Zero;
        private float _playerHandcuffSceneHeading;
        private string _playerHandcuffSceneSpaceFailure;
        private DateTime _lastPatrolObserved = DateTime.MinValue;
        private DateTime _lastOfferAudioAt = DateTime.MinValue;
        private bool _patrolSessionStarted;
        private bool _dispatchProducerEnabled;
        private bool _pendingOfferBypassesAutomaticQuietPeriod;
        private bool _pendingOfferWaitingLogged;
        private int _pendingOfferMinimumCooldownSeconds;
        private bool _dispatchSceneSearchInProgress;
        private bool _dispatchSceneSearchBypassesQuietPeriod;
        private int _dispatchSearchAttemptedResolutionCount;
        private int _dispatchSearchRejectedCandidateCount;
        private string _dispatchSearchFirstFailure = string.Empty;
        private List<LSPDDispatchEventDefinition> _dispatchSearchCachedLocationCandidates;
        private Vector3 _dispatchSearchCandidatePlayerPosition = Vector3.Zero;
        private int _dispatchSearchCandidatePlayerInteriorId;
        private bool _dispatchSearchCandidatePlayerInteriorKnown;
        private int _dispatchSearchCandidateMaximumSuspects;
        private int _dispatchSearchCandidateRepeatProtectionMinutes;
        private readonly HashSet<string> _dispatchSearchCandidateExcludedLocationIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dispatchSearchAttemptedCatalogDefinitions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dispatchSearchAttemptedFallbackDefinitions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        private sealed class DispatchSceneActorObservation
        {
            internal string State;
            internal Vector3 LastPosition;
            internal bool HasPosition;
            internal int MovementRevision;
            internal int DiagnosticRecordCount;
        }

        private sealed class ManagedSuspectPresenceObservation
        {
            internal Ped Actor;
            internal Vector3 LastKnownPosition;
            internal DateTime FirstMissingAt = DateTime.MinValue;
            internal int MissingSamples;
            internal bool PendingRemovalReported;
            internal bool MissingReported;
        }

        private sealed class DispatchSceneCandidateFailure
        {
            internal DateTime ExpiresAt;
            internal string FirstFailureStage;
            internal string PointId;
        }

        private sealed class PendingPlayerRadiusSceneCandidate
        {
            internal Vector3 PlayerPosition;
            internal int PlayerInteriorId;
            internal bool PlayerInteriorKnown;
            internal Vector3 RoadAnchor;
            internal bool RoadAnchorAvailable;
            internal Vector3 ScenePosition;
        }

        private sealed class PlayerRadiusFallbackCandidateFailure
        {
            internal string DefinitionId;
            internal Vector3 PlayerPosition;
            internal Vector3 ScenePosition;
            internal DateTime ExpiresAt;
            internal string FirstFailureStage;
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

        internal enum DispatchHitZone
        {
            Unknown,
            Limb,
            Head,
            Face,
            Neck,
            Chest,
            Stomach
        }

        internal enum ControlledDamageKind
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

        internal void AttachBackup(LSPDBackUp backup)
        {
            _backup = backup;
        }

        internal void AttachPoliceResponse(LSPDPoliceResponse policeResponse)
        {
            _policeResponse = policeResponse;
        }

        internal void AttachGangData(LSPDGangDataIntegration gangData)
        {
            _gangData = gangData;
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

        internal bool OwnsPlayerArrestInteractionInput(Ped player)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || player == null || !player.Exists() || player.IsDead
                || player.IsInVehicle())
                return false;

            if (incident.PlayerHandcuffInProgress)
                return true;

            float interactionRadius = ArrestRadius + 1f;
            return LiveSuspects(incident).Any(suspect =>
                suspect != null && suspect.Exists() && !suspect.IsDead
                && !IsArrested(incident, suspect)
                && !incident.BackupArrestInProgressSuspectHandles.Contains(
                    suspect.Handle)
                && player.Position.DistanceTo(suspect.Position)
                    <= interactionRadius);
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

        internal void SetPlayerPatrolUtility(Vehicle utility)
        {
            _playerPatrolUtility = utility != null && utility.Exists()
                ? utility : null;
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

        internal bool OwnsNearbyCustodyTransportInput(
            Ped player,
            out string response)
        {
            response = string.Empty;
            LSPDDispatchEvent incident = _incident;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || player == null || !player.Exists() || player.IsDead)
                return false;

            float interactionRadius = ArrestRadius + 2f;
            Ped nearbyOwnedSuspect = TrackedSuspects(incident).FirstOrDefault(suspect =>
                suspect != null && suspect.Exists() && !suspect.IsDead
                && player.Position.DistanceTo(suspect.Position) <= interactionRadius
                && ((incident.PlayerHandcuffInProgress
                        && incident.PlayerHandcuffTargetHandle == suspect.Handle)
                    || incident.BackupArrestInProgressSuspectHandles.Contains(
                        suspect.Handle)
                    || (IsArrested(incident, suspect)
                        && IsPedPhysicallyCuffed(suspect))
                    || IsDownedOrInjured(suspect)));
            if (nearbyOwnedSuspect == null)
                return false;

            bool unresolvedSuspects = LiveSuspects(incident)
                .Any(suspect => !IsArrested(incident, suspect));
            response = unresolvedSuspects
                ? "Secure the remaining suspect before requesting transport."
                : "Finish physically securing the suspect before requesting transport.";
            return true;
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
            bool suppressPlayerInteractionInput)
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

            ObserveDispatchSceneActorTransitions(now);
            ObserveManagedDispatchSuspectPresence(now);

            if (_incident != null && _incident.PlayerHandcuffInProgress)
                SuppressPlayerHandcuffControlsThisFrame();

            if (!allowGameplayInput || _incident == null)
                SynchronizeKeyboardInputStates();

            if (!isPatrolling)
            {
                if (!string.IsNullOrWhiteSpace(_pendingFootCatalogDefinitionId)
                    && _ambientWorld != null)
                    _ambientWorld.ClearDispatchFootSearchProgress();
                _pendingFootCatalogDefinitionId = null;
                _pendingFootCatalogBypassesQuietPeriod = false;
                _pendingFootCatalogPlayerPosition = Vector3.Zero;
                ClearDispatchSceneSearchState();
                CancelPendingOffer("Patrol ended before scene preparation completed.");
                ResetPendingSceneResolution();
                ClearEmptyLocationCandidateSearch();
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
                ProcessControllerInput(suppressPlayerInteractionInput);
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
                ClearEmptyLocationCandidateSearch();
                ClearDispatchSceneSearchState();
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

            bool explicitSceneSearchContinuing = _dispatchSceneSearchInProgress
                && _dispatchSceneSearchBypassesQuietPeriod;
            if (npcInteractionActive || otherPoliceIncidentActive
                || (now < _cooldownUntil && !explicitSceneSearchContinuing))
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            InvalidateEmptyLocationCandidateSearch(player, now);
            if (now < _nextOfferAt)
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
            if (_dispatchSceneSearchInProgress)
                return "Dispatch is checking the scene. The call will appear in Police Radio when preparation completes.";
            if (_pendingSceneResolutionDefinitionIds.Count > 0)
            {
                return "Dispatch is checking the scene. The call will appear in Police Radio when preparation completes.";
            }
            if (!string.IsNullOrWhiteSpace(_pendingFootCatalogDefinitionId))
                return "Dispatch is continuing the nearby catalog search. The call will appear when its scene is ready.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (now < _manualRequestRetryAt)
                return "Dispatch just checked nearby locations. Wait a few seconds before requesting again.";

            ClearEmptyLocationCandidateSearch();

            if (!TryCreateOffer(player, now, true))
            {
                if (_pendingOffer != null)
                    return "Dispatch is preparing the scene. The call will appear in Police Radio when it is ready.";
                if (_dispatchSceneSearchInProgress)
                    return "Dispatch is checking the scene. The call will appear in Police Radio when preparation completes.";
                if (!string.IsNullOrWhiteSpace(_pendingFootCatalogDefinitionId))
                    return "Dispatch is continuing the nearby catalog search. The call will appear when its scene is ready.";
                if (_pendingSceneResolutionDefinitionIds.Count > 0)
                    return "Dispatch is checking the scene. The call will appear in Police Radio when preparation completes.";
                _manualRequestRetryAt = now.AddSeconds(5);
                return _definitions.Count == 0
                    ? "Dispatch has no usable callout definitions in its XML catalog."
                    : "Dispatch could not validate a complete compatible scene nearby.";
            }
            return "Dispatch request received. Review the new call in Police Radio.";
        }

        private void BeginDispatchSceneSearch(DateTime now, bool bypassAutomaticQuietPeriod)
        {
            _dispatchSceneSearchInProgress = true;
            _dispatchSceneSearchBypassesQuietPeriod = bypassAutomaticQuietPeriod;
            _dispatchSearchAttemptedCatalogDefinitions.Clear();
            _dispatchSearchAttemptedFallbackDefinitions.Clear();
            _dispatchSearchAttemptedResolutionCount = 0;
            _dispatchSearchRejectedCandidateCount = 0;
            _dispatchSearchFirstFailure = string.Empty;
            ClearDispatchSearchCandidateCache();
            LogRuntimeTransition(
                "POLICE_DISPATCH_SCENE_SEARCH_STARTED",
                "Mode=" + (bypassAutomaticQuietPeriod ? "ExplicitRadio" : "AutomaticPatrol"),
                "Selection=CompatibleCatalogFirstThenBoundedFallback"
                + "; WorkBudget=OneSceneResolutionPerUpdate"
                + "; StartedAt=" + now.ToString("O", CultureInfo.InvariantCulture));
        }

        private void ContinueDispatchSceneSearch(DateTime now, bool bypassAutomaticQuietPeriod)
        {
            if (!_dispatchSceneSearchInProgress)
                BeginDispatchSceneSearch(now, bypassAutomaticQuietPeriod);
            _dispatchSceneSearchBypassesQuietPeriod |= bypassAutomaticQuietPeriod;
            _nextOfferAt = now.AddMilliseconds(
                DispatchSceneSearchContinuationDelayMilliseconds);
            _manualRequestRetryAt = now.AddSeconds(3);
            LogDebugTransition(
                "POLICE_DISPATCH_SEARCH_WORK_BUDGET_YIELDED",
                "SearchInProgress",
                "The combined scene-resolution budget was used; Dispatch will resume on a later update.");
        }

        private void ClearDispatchSceneSearchState()
        {
            _dispatchSceneSearchInProgress = false;
            _dispatchSceneSearchBypassesQuietPeriod = false;
            _dispatchSearchAttemptedCatalogDefinitions.Clear();
            _dispatchSearchAttemptedFallbackDefinitions.Clear();
            _dispatchSearchAttemptedResolutionCount = 0;
            _dispatchSearchRejectedCandidateCount = 0;
            _dispatchSearchFirstFailure = string.Empty;
            ClearDispatchSearchCandidateCache();
            ClearDebugTransition("POLICE_DISPATCH_SEARCH_WORK_BUDGET_YIELDED");
        }

        private void ClearDispatchSearchCandidateCache()
        {
            _dispatchSearchCachedLocationCandidates = null;
            _dispatchSearchCandidatePlayerPosition = Vector3.Zero;
            _dispatchSearchCandidatePlayerInteriorId = 0;
            _dispatchSearchCandidatePlayerInteriorKnown = false;
            _dispatchSearchCandidateMaximumSuspects = 0;
            _dispatchSearchCandidateRepeatProtectionMinutes = 0;
            _dispatchSearchCandidateExcludedLocationIds.Clear();
            ClearDebugTransition("POLICE_DISPATCH_CATALOG_CANDIDATE_CACHE_REUSED");
        }

        private List<LSPDDispatchEventDefinition> GetDispatchSearchLocationCandidates(
            Ped player,
            int playerInteriorId,
            bool playerInteriorIdAvailable,
            ISet<string> excludedLocationIds)
        {
            bool canReuse = _dispatchSceneSearchInProgress
                && _dispatchSearchCachedLocationCandidates != null
                && player != null && player.Exists()
                && HorizontalDistance(
                    _dispatchSearchCandidatePlayerPosition, player.Position)
                    < DispatchSceneFailureCacheMovementMeters
                && _dispatchSearchCandidatePlayerInteriorId == playerInteriorId
                && _dispatchSearchCandidatePlayerInteriorKnown == playerInteriorIdAvailable
                && _settings != null
                && _dispatchSearchCandidateMaximumSuspects == _settings.MaximumSuspects
                && _dispatchSearchCandidateRepeatProtectionMinutes
                    == _settings.RepeatProtectionMinutes
                && _dispatchSearchCandidateExcludedLocationIds.SetEquals(
                    excludedLocationIds == null
                        ? Enumerable.Empty<string>() : excludedLocationIds);
            if (canReuse)
            {
                LogDebugTransition(
                    "POLICE_DISPATCH_CATALOG_CANDIDATE_CACHE_REUSED",
                    "Definitions=" + _dispatchSearchCachedLocationCandidates.Count
                        + ";PlayerInteriorId=" + playerInteriorId
                        + ";PlayerInteriorKnown=" + playerInteriorIdAvailable,
                    "The compatible catalog pass was reused for this active search; each scene still receives fresh physical validation.");
                return new List<LSPDDispatchEventDefinition>(
                    _dispatchSearchCachedLocationCandidates);
            }

            List<LSPDDispatchEventDefinition> candidates = ChooseLocationDefinitionCandidates(
                player,
                Math.Max(1, _definitions.Count),
                playerInteriorId,
                playerInteriorIdAvailable,
                excludedLocationIds);
            if (_dispatchSceneSearchInProgress && player != null && player.Exists())
            {
                _dispatchSearchCachedLocationCandidates =
                    new List<LSPDDispatchEventDefinition>(candidates);
                _dispatchSearchCandidatePlayerPosition = player.Position;
                _dispatchSearchCandidatePlayerInteriorId = playerInteriorId;
                _dispatchSearchCandidatePlayerInteriorKnown = playerInteriorIdAvailable;
                _dispatchSearchCandidateMaximumSuspects = _settings == null
                    ? 0 : _settings.MaximumSuspects;
                _dispatchSearchCandidateRepeatProtectionMinutes = _settings == null
                    ? 0 : _settings.RepeatProtectionMinutes;
                _dispatchSearchCandidateExcludedLocationIds.Clear();
                foreach (string locationId in excludedLocationIds == null
                    ? Enumerable.Empty<string>() : excludedLocationIds)
                {
                    if (!string.IsNullOrWhiteSpace(locationId))
                        _dispatchSearchCandidateExcludedLocationIds.Add(locationId);
                }
            }
            ClearDebugTransition("POLICE_DISPATCH_CATALOG_CANDIDATE_CACHE_REUSED");
            return candidates;
        }

        private void RecordDispatchSceneSearchRejection(
            string definitionId,
            string failure)
        {
            _dispatchSearchRejectedCandidateCount++;
            if (string.IsNullOrWhiteSpace(_dispatchSearchFirstFailure))
                _dispatchSearchFirstFailure = (definitionId ?? "Unknown") + "="
                    + (string.IsNullOrWhiteSpace(failure)
                        ? "Stage=SceneResolution; Result=NoFailureReason"
                        : failure);
        }

        private void CompleteDispatchSceneSearch(string result)
        {
            if (_dispatchSceneSearchInProgress)
                LogRuntimeTransition(
                    "POLICE_DISPATCH_SCENE_SEARCH_COMPLETED",
                    "Result=" + (result ?? "Unknown"),
                    "Result=" + (result ?? "Unknown")
                    + "; SceneResolutionAttempts=" + _dispatchSearchAttemptedResolutionCount
                    + "; RejectedCandidates=" + _dispatchSearchRejectedCandidateCount
                    + "; FirstFailure=" + (string.IsNullOrWhiteSpace(_dispatchSearchFirstFailure)
                        ? "None" : _dispatchSearchFirstFailure));
            ClearDispatchSceneSearchState();
        }

        private bool TryCreateOffer(
            Ped player,
            DateTime now,
            bool bypassAutomaticQuietPeriod)
        {
            bypassAutomaticQuietPeriod |= _pendingFootCatalogBypassesQuietPeriod;
            if (_pendingOffer != null)
                return TryAdvancePendingOffer(player, now);
            if (!_dispatchSceneSearchInProgress)
                BeginDispatchSceneSearch(now, bypassAutomaticQuietPeriod);
            bypassAutomaticQuietPeriod |= _dispatchSceneSearchBypassesQuietPeriod;
            int sceneResolutionAttemptsThisUpdate = 0;

            int playerLiveInteriorId = 0;
            bool playerLiveInteriorIdAvailable = _ambientWorld != null
                && _ambientWorld.TryGetDispatchPlayerInteriorId(
                    player, out playerLiveInteriorId);
            if (!string.IsNullOrWhiteSpace(_pendingFootCatalogDefinitionId)
                && HorizontalDistance(player.Position, _pendingFootCatalogPlayerPosition) >= 20f)
            {
                if (_ambientWorld != null)
                    _ambientWorld.ClearDispatchFootSearchProgress();
                _pendingFootCatalogDefinitionId = null;
                _pendingFootCatalogBypassesQuietPeriod = false;
                _pendingFootCatalogPlayerPosition = Vector3.Zero;
            }
            bool pendingResolutionRetry = _pendingSceneResolutionStartedAt
                    != DateTime.MinValue
                && _pendingSceneResolutionDefinitionIds.Count > 0;
            bool pendingInteriorResolution = pendingResolutionRetry
                && _pendingSceneResolutionInteriorIds.Count > 0;
            bool pendingPlayerRadiusOnly = pendingResolutionRetry
                && _pendingSceneResolutionDefinitionIds.All(id =>
                    _pendingPlayerRadiusSceneCandidates.ContainsKey(id));
            bool rejectedContextChanged = UpdateDispatchSceneCandidateFailureContext(
                player, playerLiveInteriorId, playerLiveInteriorIdAvailable, now);
            if (rejectedContextChanged
                && pendingResolutionRetry
                && !pendingPlayerRadiusOnly)
                ResetPendingSceneResolution();
            PruneDispatchSceneCandidateFailureCache(now);
            PrunePlayerRadiusFallbackCandidateFailures(now);

            Vector3 scenePlayerPosition = player.Position;
            int scenePlayerInteriorId = playerLiveInteriorId;
            bool scenePlayerInteriorIdAvailable = playerLiveInteriorIdAvailable;
            Vector3 playerRoadAnchor = Vector3.Zero;
            bool playerRadiusRoadSceneAvailable = false;
            if (pendingResolutionRetry
                && _pendingPlayerRadiusSceneCandidates.Count > 0)
            {
                PendingPlayerRadiusSceneCandidate pendingContext =
                    _pendingPlayerRadiusSceneCandidates.Values.FirstOrDefault();
                if (pendingContext != null)
                {
                    playerRoadAnchor = pendingContext.RoadAnchor;
                    playerRadiusRoadSceneAvailable = pendingContext.RoadAnchorAvailable;
                    if (pendingPlayerRadiusOnly)
                    {
                        scenePlayerPosition = pendingContext.PlayerPosition;
                        scenePlayerInteriorId = pendingContext.PlayerInteriorId;
                        scenePlayerInteriorIdAvailable = pendingContext.PlayerInteriorKnown;
                    }
                }
            }
            bool playerOutsideLiveInterior = scenePlayerInteriorIdAvailable
                && scenePlayerInteriorId == 0;
            HashSet<string> excludedRepeatProtectedLocationIds =
                GetActiveDispatchLocationRepeatProtectionIds(now);
            List<LSPDDispatchEventDefinition> locationCandidates;
            bool catalogSelectionPerformed = false;
            if (pendingResolutionRetry)
            {
                List<LSPDDispatchEventDefinition> allEligibleCandidates =
                    pendingPlayerRadiusOnly || pendingInteriorResolution
                        ? _definitions.Where(candidate => candidate != null
                            && _pendingSceneResolutionDefinitionIds.Contains(candidate.Id))
                            .ToList()
                        : GetDispatchSearchLocationCandidates(
                            player,
                            playerLiveInteriorId,
                            playerLiveInteriorIdAvailable,
                            excludedRepeatProtectedLocationIds);
                catalogSelectionPerformed = true;
                List<LSPDDispatchEventDefinition> independentCandidates =
                    pendingPlayerRadiusOnly || pendingInteriorResolution
                    ? new List<LSPDDispatchEventDefinition>()
                    : allEligibleCandidates.Where(candidate => candidate != null
                        && !_pendingSceneResolutionDefinitionIds.Contains(candidate.Id)).ToList();
                List<LSPDDispatchEventDefinition> pendingCandidates =
                    allEligibleCandidates.Where(candidate => candidate != null
                        && _pendingSceneResolutionDefinitionIds.Contains(candidate.Id))
                        .ToList();
                locationCandidates = pendingInteriorResolution
                    ? pendingCandidates
                    : independentCandidates.Concat(pendingCandidates).ToList();
                string pendingState = BuildPendingSceneResolutionStateSignature(
                    _pendingSceneResolutionDefinitionIds.Select(id => id + "=PENDING:"))
                    + ";IndependentCandidates=" + independentCandidates.Count;
                LogRuntimeTransition(
                    "POLICE_DISPATCH_PENDING_CANDIDATES_RETRY",
                    pendingState,
                    "PendingDefinitionCount=" + pendingCandidates.Count
                    + "; Retry=" + _pendingSceneResolutionRetryCount
                    + "; DelayMilliseconds=" + SceneResolutionRetryDelayMilliseconds
                    + "; IndependentCandidates=" + independentCandidates.Count
                    + "; PendingInteriorIds=" + string.Join(",",
                        _pendingSceneResolutionInteriorIds.OrderBy(value => value))
                    + "; Selection=" + (pendingPlayerRadiusOnly
                        ? "RetainedPlayerRadiusCandidatesOnly"
                        : pendingInteriorResolution
                            ? "RetainedInteriorCandidatesOnly"
                            : "IndependentCatalogFirstThenPendingCandidates"));
                if (locationCandidates.Count == 0)
                {
                    ResetPendingSceneResolution();
                    pendingResolutionRetry = false;
                    catalogSelectionPerformed = false;
                }
            }
            else
                locationCandidates = null;

            if (!pendingResolutionRetry && !catalogSelectionPerformed)
            {
                // One full candidate pass is followed by retries of only the
                // genuinely pending definitions. Permanent location failures
                // are cached below, so a later pass can move to other sites.
                locationCandidates = GetDispatchSearchLocationCandidates(
                    player,
                    playerLiveInteriorId,
                    playerLiveInteriorIdAvailable,
                    excludedRepeatProtectedLocationIds);
            }
            if (locationCandidates.Count == 0)
            {
                _pendingFootCatalogDefinitionId = null;
                _pendingFootCatalogBypassesQuietPeriod = false;
                _pendingFootCatalogPlayerPosition = Vector3.Zero;
                ResetPendingSceneResolution();
                _nextOfferAt = _definitions.Count == 0
                    ? now.AddSeconds(30)
                    : NextDefinitionEligibleAt(now);
                if (_definitions.Count > 0
                    && _nextOfferAt < now.AddSeconds(EmptyLocationCandidateRetrySeconds))
                    _nextOfferAt = now.AddSeconds(EmptyLocationCandidateRetrySeconds);
                if (_definitions.Count > 0)
                    CacheEmptyLocationCandidateSearch(
                        player, playerLiveInteriorId, playerLiveInteriorIdAvailable, now);
                if (_definitions.Count == 0)
                    LogDebug("POLICE_DISPATCH_NO_DEFINITIONS", "Dispatch XML contained no usable callout definitions.");
                CompleteDispatchSceneSearch("NoCompatibleCatalogCandidates");
                return false;
            }

            LSPDDispatchEventDefinition definition = null;
            AmbientDispatchSceneResolution sceneResolution = null;
            string locationFailure = "AmbientWorld is not attached by PoliceCore.";
            var locationFailures = new List<string>();
            var preferredInteriorFailures = new List<string>();
            var pendingVehicleSceneFailures = new List<string>();
            int fallbackCandidateAttemptCount = 0;
            int fallbackCandidateRejectedCount = 0;
            int fallbackCandidateCachedSkipCount = 0;
            string firstFallbackFailureEvent = string.Empty;
            string firstFallbackFailureReason = string.Empty;
            List<LSPDDispatchEventDefinition> catalogLocationCandidates = locationCandidates
                .Where(candidate => candidate != null
                    && !_dispatchSearchAttemptedCatalogDefinitions.Contains(candidate.Id))
                .ToList();
            foreach (LSPDDispatchEventDefinition candidateDefinition in catalogLocationCandidates
                .OrderByDescending(candidate => string.Equals(candidate.Id,
                    _pendingFootCatalogDefinitionId, StringComparison.OrdinalIgnoreCase)))
            {
                // The retained Player-radius retry is testing one exact
                // roadside coordinate. Its catalog pass already failed in
                // the original search, so do not repeat that geometry work
                // before the exact collision-readiness recheck below.
                if (pendingPlayerRadiusOnly
                    && pendingResolutionRetry
                    && _pendingPlayerRadiusSceneCandidates.ContainsKey(
                        candidateDefinition.Id))
                    continue;

                bool retryingExactPendingCandidate = pendingResolutionRetry
                    && _pendingPlayerRadiusSceneCandidates.ContainsKey(
                        candidateDefinition.Id);
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

                List<LSPDDispatchLocationDefinition> uncachedEventAreas = eventAreas;
                int cachedAreaCount;
                if (retryingExactPendingCandidate)
                {
                    cachedAreaCount = 0;
                }
                else
                {
                    eventAreas = ExcludeRejectedDispatchSceneCandidateAreas(
                        candidateDefinition.Id,
                        uncachedEventAreas,
                        scenePlayerPosition,
                        scenePlayerInteriorId,
                        scenePlayerInteriorIdAvailable,
                        now,
                        out cachedAreaCount);
                }
                if (eventAreas.Count == 0 && uncachedEventAreas.Count > 0)
                {
                    string cachedFailure = "Stage=DispatchSceneCandidateCache; CachedAreas="
                        + cachedAreaCount + "; Result=PreviouslyRejectedForCurrentLocationContext";
                    locationFailures.Add(candidateDefinition.Id + "=" + cachedFailure);
                    if (candidateDefinition.RequiresMappedInterior
                        || !string.IsNullOrWhiteSpace(candidateDefinition.InteriorLocationId))
                        preferredInteriorFailures.Add(candidateDefinition.Id + "=" + cachedFailure);
                    continue;
                }

                locationFailure = "AmbientWorld is not attached by PoliceCore.";
                // RequiresVehicle selects the suspect's criminal vehicle; the
                // separate staging flag requests an exterior Player Utility
                // position. Both need a staging coordinate, but only
                // RequiresVehicle may create or assign a suspect vehicle.
                bool requiresVehicleStaging = candidateDefinition.RequiresVehicle
                    || candidateDefinition.VehicleStagingRequired;
                var candidateExcludedLocationIds = new HashSet<string>(
                    excludedRepeatProtectedLocationIds, StringComparer.OrdinalIgnoreCase);
                candidateExcludedLocationIds.UnionWith(
                    GetActiveRejectedDispatchSceneLocationIds(
                        scenePlayerPosition, scenePlayerInteriorId,
                        scenePlayerInteriorIdAvailable, now, candidateDefinition.Id));
                bool candidateResolved = false;
                if (_ambientWorld != null
                    && sceneResolutionAttemptsThisUpdate
                        < MaximumDispatchSceneResolutionsPerUpdate)
                {
                    sceneResolutionAttemptsThisUpdate++;
                    _dispatchSearchAttemptedResolutionCount++;
                    candidateResolved = _ambientWorld.TryResolveDispatchScene(
                    eventAreas,
                    candidateDefinition.IncidentType,
                    scenePlayerPosition,
                    requiresVehicleStaging,
                    out sceneResolution,
                    out locationFailure,
                    Vector3.Zero,
                    candidateDefinition.InteriorLocationId,
                    candidateDefinition.RequiredPhysicalType,
                    candidateDefinition.LocationContext,
                    candidateDefinition.RequiresMappedInterior,
                    scenePlayerInteriorId,
                    scenePlayerInteriorIdAvailable,
                    candidateDefinition.VehicleActivity,
                    false,
                    candidateExcludedLocationIds,
                    definitionId: candidateDefinition.Id,
                    requiresExteriorScene: candidateDefinition.RequiresExteriorScene);
                }
                if (candidateResolved)
                {
                    _dispatchLocationRepeatFallbackActive = false;
                    _pendingFootCatalogDefinitionId = null;
                    _pendingFootCatalogBypassesQuietPeriod = false;
                    _pendingFootCatalogPlayerPosition = Vector3.Zero;
                    definition = candidateDefinition;
                    break;
                }

                if ((locationFailure ?? string.Empty).StartsWith(
                    "PENDING:Stage=NearbyFootCatalogBudget;", StringComparison.Ordinal))
                {
                    _pendingFootCatalogDefinitionId = candidateDefinition.Id;
                    _pendingFootCatalogBypassesQuietPeriod = bypassAutomaticQuietPeriod;
                    _pendingFootCatalogPlayerPosition = scenePlayerPosition;
                    _nextOfferAt = now.AddSeconds(1);
                    _manualRequestRetryAt = _nextOfferAt;
                    LogDebugTransition("POLICE_DISPATCH_CATALOG_SEARCH_CONTINUING",
                        locationFailure, locationFailure);
                    return false;
                }

                _pendingFootCatalogDefinitionId = null;
                _pendingFootCatalogBypassesQuietPeriod = false;
                _pendingFootCatalogPlayerPosition = Vector3.Zero;
                string candidateFailure = candidateDefinition.Id + "="
                    + (locationFailure ?? "Location validation failed.");
                locationFailures.Add(candidateFailure);
                RecordDispatchSceneSearchRejection(
                    candidateDefinition.Id, locationFailure);
                CacheRejectedDispatchSceneCandidateAreas(
                    candidateDefinition,
                    uncachedEventAreas,
                    locationFailure,
                    scenePlayerPosition,
                    now);
                if (candidateDefinition.RequiresMappedInterior
                    || !string.IsNullOrWhiteSpace(candidateDefinition.InteriorLocationId))
                    preferredInteriorFailures.Add(candidateFailure);
                sceneResolution = null;

                bool candidateReadinessPending = (locationFailure ?? string.Empty)
                    .IndexOf("PENDING:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (TryDeferPendingSceneResolution(
                    now,
                    locationFailures,
                    null))
                    return false;
                if (!candidateReadinessPending)
                {
                    string rejectedLocation = DiagnosticFieldValue(
                        locationFailure ?? string.Empty, "Location=");
                    if (string.IsNullOrWhiteSpace(rejectedLocation)
                        || string.Equals(rejectedLocation, "Unknown", StringComparison.OrdinalIgnoreCase))
                        _dispatchSearchAttemptedCatalogDefinitions.Add(candidateDefinition.Id);
                }

                if (sceneResolutionAttemptsThisUpdate
                    >= MaximumDispatchSceneResolutionsPerUpdate)
                {
                    ContinueDispatchSceneSearch(now, bypassAutomaticQuietPeriod);
                    return false;
                }

                // A pending mapped site does not suppress other catalog
                // definitions. Continue through the bounded candidate list;
                // each alternative still has to resolve and preflight its own
                // physical scene before it can be offered.
            }

            // An explicit request first tries the same randomized mapped/catalog
            // candidates as every other request. If none resolves, a Player
            // outside a live interior may receive a runtime-validated nearby
            // exterior scene. Road definitions are included only when a real
            // road node is available; generic outdoor definitions work away
            // from roads. Mapped-interior events never use this fallback.
            bool pendingPlayerRadiusRetryAvailable = pendingResolutionRetry
                && _pendingPlayerRadiusSceneCandidates.Count > 0;
            if (definition == null
                && (playerOutsideLiveInterior || pendingPlayerRadiusRetryAvailable))
            {
                if (!pendingResolutionRetry)
                    playerRadiusRoadSceneAvailable =
                        TryGetPlayerRadiusRoadVehicleAnchor(player, out playerRoadAnchor);
                if (!pendingResolutionRetry)
                {
                    LogRuntimeTransition("POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_REQUEST",
                        "Mode=" + (bypassAutomaticQuietPeriod ? "ExplicitRadio" : "ScheduledDispatch")
                        + ";PlayerInteriorId=" + scenePlayerInteriorId
                        + ";PlayerInteriorKnown=" + scenePlayerInteriorIdAvailable
                        + ";RoadAnchorAvailable=" + playerRadiusRoadSceneAvailable,
                        "Player=" + player.Handle
                        + "; Vehicle=" + (player.CurrentVehicle == null
                            ? 0 : player.CurrentVehicle.Handle)
                        + "; RoadAnchor=" + (playerRadiusRoadSceneAvailable
                            ? playerRoadAnchor.ToString() : "Unavailable")
                        + "; RequestMode=" + (bypassAutomaticQuietPeriod
                            ? "ExplicitRadio" : "ScheduledDispatch")
                        + "; Resolution=CatalogFirstThenPlayerRadiusOutdoorFallback");
                }
                List<LSPDDispatchEventDefinition> outdoorFallbackCandidates = pendingResolutionRetry
                    ? locationCandidates.Where(candidate => candidate != null
                        && _pendingPlayerRadiusSceneCandidates.ContainsKey(candidate.Id)).ToList()
                    : ChoosePlayerRadiusOutdoorFallbackCandidates(
                        now, playerRadiusRoadSceneAvailable)
                        .Where(candidate => candidate != null
                            && !_dispatchSearchAttemptedFallbackDefinitions.Contains(candidate.Id))
                        .ToList();
                foreach (LSPDDispatchEventDefinition candidateDefinition in outdoorFallbackCandidates)
                {
                    if (IsPlayerRadiusFallbackCandidateFailureCached(
                        candidateDefinition.Id, scenePlayerPosition, now))
                    {
                        fallbackCandidateCachedSkipCount++;
                        continue;
                    }
                    PendingPlayerRadiusSceneCandidate pendingContext = null;
                    bool retryingExactPendingCandidate = pendingResolutionRetry
                        && _pendingPlayerRadiusSceneCandidates.TryGetValue(
                            candidateDefinition.Id, out pendingContext);
                    Vector3 fallbackPlayerPosition = retryingExactPendingCandidate
                        ? pendingContext.PlayerPosition : scenePlayerPosition;
                    int fallbackPlayerInteriorId = retryingExactPendingCandidate
                        ? pendingContext.PlayerInteriorId : scenePlayerInteriorId;
                    bool fallbackPlayerInteriorKnown = retryingExactPendingCandidate
                        ? pendingContext.PlayerInteriorKnown : scenePlayerInteriorIdAvailable;
                    Vector3 fallbackRoadAnchor = retryingExactPendingCandidate
                        ? pendingContext.RoadAnchor : playerRoadAnchor;

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

                    List<LSPDDispatchLocationDefinition> uncachedFallbackAreas = eventAreas;
                    int cachedFallbackAreaCount;
                    if (retryingExactPendingCandidate)
                    {
                        cachedFallbackAreaCount = 0;
                    }
                    else
                    {
                        eventAreas = ExcludeRejectedDispatchSceneCandidateAreas(
                            candidateDefinition.Id,
                            uncachedFallbackAreas,
                            fallbackPlayerPosition,
                            scenePlayerInteriorId,
                            scenePlayerInteriorIdAvailable,
                            now,
                            out cachedFallbackAreaCount);
                    }
                    if (eventAreas.Count == 0 && uncachedFallbackAreas.Count > 0)
                    {
                        locationFailures.Add(candidateDefinition.Id
                            + "@PlayerRadiusOutdoorFallback=Stage=DispatchSceneCandidateCache; CachedAreas="
                            + cachedFallbackAreaCount);
                        continue;
                    }

                    bool requiresVehicleStaging = candidateDefinition.RequiresVehicle
                        || candidateDefinition.VehicleStagingRequired;
                    locationFailure = "AmbientWorld is not attached by PoliceCore.";
                    fallbackCandidateAttemptCount++;
                    bool fallbackCandidateResolved = false;
                    if (_ambientWorld != null
                        && sceneResolutionAttemptsThisUpdate
                            < MaximumDispatchSceneResolutionsPerUpdate)
                    {
                        sceneResolutionAttemptsThisUpdate++;
                        _dispatchSearchAttemptedResolutionCount++;
                        fallbackCandidateResolved = _ambientWorld.TryResolveDispatchScene(
                            eventAreas,
                            candidateDefinition.IncidentType,
                            scenePlayerPosition,
                            requiresVehicleStaging,
                            out sceneResolution,
                            out locationFailure,
                            fallbackRoadAnchor,
                            candidateDefinition.InteriorLocationId,
                            candidateDefinition.RequiredPhysicalType,
                            candidateDefinition.LocationContext,
                            candidateDefinition.RequiresMappedInterior,
                            fallbackPlayerInteriorId,
                            fallbackPlayerInteriorKnown,
                            candidateDefinition.VehicleActivity,
                            true,
                            excludedRepeatProtectedLocationIds,
                            retryingExactPendingCandidate
                                ? (Vector3?)pendingContext.ScenePosition : null);
                    }
                    if (fallbackCandidateResolved)
                    {
                        _dispatchLocationRepeatFallbackActive = false;
                        definition = candidateDefinition;
                        ClearDebugTransition(
                            "POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_REJECTED");
                        LogRuntimeTransition(
                            "POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_SELECTED",
                            "Event=" + definition.Id + ";Location=" + sceneResolution.LocationId,
                            "Event=" + definition.Id
                            + "; LocationCategory=" + sceneResolution.LocationCategory
                            + "; ScenePosition=" + sceneResolution.SceneCenter
                            + "; VehicleActivity=" + definition.VehicleActivity);
                        break;
                    }

                    fallbackCandidateRejectedCount++;
                    RecordDispatchSceneSearchRejection(
                        candidateDefinition.Id, locationFailure);
                    if (string.IsNullOrWhiteSpace(firstFallbackFailureEvent))
                    {
                        firstFallbackFailureEvent = candidateDefinition.Id;
                        firstFallbackFailureReason = locationFailure ?? "Location validation failed.";
                    }
                    locationFailures.Add(candidateDefinition.Id
                        + "@PlayerRadiusOutdoorFallback="
                        + (locationFailure ?? "Location validation failed."));
                    CacheRejectedDispatchSceneCandidateAreas(
                        candidateDefinition,
                        uncachedFallbackAreas,
                        locationFailure,
                        scenePlayerPosition,
                        now);
                    if (candidateDefinition.RequiresVehicle
                        && (locationFailure ?? string.Empty).IndexOf(
                            "CollisionPending", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pendingVehicleSceneFailures.Add(candidateDefinition.Id
                            + "=PENDING:" + locationFailure);
                        if (!pendingResolutionRetry
                            && sceneResolution != null
                            && sceneResolution.IsPendingSceneResolution)
                        {
                            _pendingPlayerRadiusSceneCandidates[candidateDefinition.Id] =
                                new PendingPlayerRadiusSceneCandidate
                                {
                                    PlayerPosition = scenePlayerPosition,
                                    PlayerInteriorId = scenePlayerInteriorId,
                                    PlayerInteriorKnown = scenePlayerInteriorIdAvailable,
                                    RoadAnchor = playerRoadAnchor,
                                    RoadAnchorAvailable = playerRadiusRoadSceneAvailable,
                                    ScenePosition = sceneResolution.PendingResolutionPosition
                                };
                        }
                    }
                    if ((locationFailure ?? string.Empty).IndexOf(
                            "PENDING:", StringComparison.OrdinalIgnoreCase) < 0)
                        _dispatchSearchAttemptedFallbackDefinitions.Add(candidateDefinition.Id);
                    sceneResolution = null;
                    if (sceneResolutionAttemptsThisUpdate
                        >= MaximumDispatchSceneResolutionsPerUpdate)
                        break;
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

            if (definition == null || sceneResolution == null)
            {
                if (fallbackCandidateAttemptCount > 0
                    || fallbackCandidateCachedSkipCount > 0)
                {
                    string firstFailureStage = GetDispatchSceneFailureStage(
                        firstFallbackFailureReason);
                    LogDebugTransition(
                        "POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_REJECTED",
                        "Attempts=" + fallbackCandidateAttemptCount
                            + ";Rejected=" + fallbackCandidateRejectedCount
                            + ";CachedSkips=" + fallbackCandidateCachedSkipCount
                            + ";FirstEvent=" + firstFallbackFailureEvent
                            + ";FirstStage=" + firstFailureStage,
                        "CandidatesAttempted=" + fallbackCandidateAttemptCount
                            + "; CandidatesRejected=" + fallbackCandidateRejectedCount
                            + "; CachedFailureSkips=" + fallbackCandidateCachedSkipCount
                            + "; FirstFailureEvent=" + firstFallbackFailureEvent
                            + "; FirstFailure=" + firstFallbackFailureReason);
                }
                bool continuingPendingSearch = _pendingSceneResolutionStartedAt
                    != DateTime.MinValue;
                if (TryDeferPendingSceneResolution(
                    now,
                    locationFailures.Concat(preferredInteriorFailures)
                        .Concat(pendingVehicleSceneFailures),
                    null))
                    return false;
                if (sceneResolutionAttemptsThisUpdate
                    >= MaximumDispatchSceneResolutionsPerUpdate)
                {
                    ContinueDispatchSceneSearch(now, bypassAutomaticQuietPeriod);
                    return false;
                }
                if (continuingPendingSearch)
                    CachePendingPlayerRadiusCandidateFailures(now);
                ResetPendingSceneResolution();
                if (continuingPendingSearch
                    && ShouldContinueAfterPreparedSceneFailure(
                        _preparedSceneCandidateFallthroughAttempts,
                        MaximumPreparedSceneCandidateFallthroughAttempts))
                {
                    _preparedSceneCandidateFallthroughAttempts++;
                    LogDebugTransition(
                        "POLICE_DISPATCH_PENDING_SEARCH_FELL_THROUGH",
                        "Attempt=" + _preparedSceneCandidateFallthroughAttempts,
                        "The pending scene readiness window ended; Dispatch continues with other "
                            + "eligible catalog candidates.");
                    ContinueDispatchSceneSearch(now, bypassAutomaticQuietPeriod);
                    return false;
                }

                if (!_dispatchLocationRepeatFallbackActive
                    && excludedRepeatProtectedLocationIds.Count > 0)
                {
                    _dispatchLocationRepeatFallbackActive = true;
                    LogRuntime("POLICE_DISPATCH_LOCATION_REPEAT_FALLBACK",
                        "No other prepared catalog scene was usable. Dispatch will retry once without the recent-location exclusion; the location remains temporarily protected when an alternative is available.");
                    CompleteDispatchSceneSearch("RepeatProtectionFallback");
                    _nextOfferAt = now.AddMilliseconds(
                        DispatchSceneSearchContinuationDelayMilliseconds);
                    return false;
                }

                _preparedSceneCandidateFallthroughAttempts = 0;
                _nextOfferAt = now.AddSeconds(30);
                LogDebugTransition(
                    "POLICE_DISPATCH_AMBIENT_WORLD_LOCATION_FAILED",
                    BuildDispatchSceneFailureStateSignature(locationFailures),
                    "Attempts=" + locationFailures.Count
                    + "; MaximumAttempts=" + locationCandidates.Count
                    + "; " + SummarizeDispatchSceneFailures(locationFailures, 3));
                _dispatchLocationRepeatFallbackActive = false;
                CompleteDispatchSceneSearch("CatalogAndFallbackExhausted");
                return false;
            }

            if (!sceneResolution.IsInteriorScene && preferredInteriorFailures.Count > 0)
            {
                LogDebugTransition(
                    "POLICE_DISPATCH_INTERIOR_SCENE_REJECTED_BEFORE_EXTERIOR_OFFER",
                    "SelectedEvent=" + definition.Id
                    + ";SelectedLocation=" + sceneResolution.LocationId
                    + ";Rejected=" + BuildDispatchSceneFailureStateSignature(
                        preferredInteriorFailures),
                    "SelectedEvent=" + definition.Id
                    + "; SelectedLocation=" + sceneResolution.LocationId
                    + "; RejectedInteriorDefinitions=" + preferredInteriorFailures.Count
                    + "; " + SummarizeDispatchSceneFailures(
                        preferredInteriorFailures, 3));
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
                suspectVehicleCount,
                _random);
            if (incident == null)
            {
                ReleaseUnusedDispatchInteriorPins("OccurrenceConstructionFailed");
                _nextOfferAt = now.AddSeconds(30);
                LogDebug(
                    "POLICE_DISPATCH_INCIDENT_NOT_CREATED",
                    "Event=" + definition.Id + "; Reason=AUTHORITATIVE_CRIMINAL_ASSET_SNAPSHOT_INVALID");
                return false;
            }
            if (string.Equals(incident.Id, "disturbance",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(incident.DisturbanceVariant, "knife_threat",
                    StringComparison.OrdinalIgnoreCase)
                && !(incident.WeaponNameCandidates ?? new List<string>())
                    .Any(value => string.Equals(value, "WEAPON_KNIFE",
                        StringComparison.OrdinalIgnoreCase)))
            {
                string rejectedVariant = incident.DisturbanceVariant;
                string replacementVariant;
                bool fallbackSelected = incident.TryChooseDisturbanceFallback(
                    rejectedVariant, _random, out replacementVariant);
                LogDebug(
                    "POLICE_DISPATCH_DISTURBANCE_VARIATION_REJECTED",
                    "Occurrence=" + incident.Id
                    + "; CriminalProfile=" + incident.CriminalProfileId
                    + "; WeaponPool=" + (criminalProfile.WeaponPoolId ?? string.Empty)
                    + "; WeaponCandidates=" + string.Join(",",
                        incident.WeaponNameCandidates ?? new List<string>())
                    + "; RejectedVariation=" + rejectedVariant
                    + "; Replacement=" + (fallbackSelected
                        ? replacementVariant : "None")
                    + "; Result=" + (fallbackSelected
                        ? "AuthoredVariationFallbackSelected"
                        : "CandidateRejectedBeforeOffer"));
                if (!fallbackSelected)
                {
                    ReleaseUnusedDispatchInteriorPins(
                        "KnifeThreatProfileDoesNotProvideKnife");
                    _nextOfferAt = now.AddSeconds(30);
                    return false;
                }
            }
            incident.SceneResolution = sceneResolution;
            _pendingOffer = incident;
            _pendingOfferBypassesAutomaticQuietPeriod = bypassAutomaticQuietPeriod;
            _pendingOfferWaitingLogged = false;
            _pendingOfferMinimumCooldownSeconds = definition.MinimumCooldownSeconds;
            return TryAdvancePendingOffer(player, now);
        }

        private void PrunePlayerRadiusFallbackCandidateFailures(DateTime now)
        {
            _playerRadiusFallbackCandidateFailures.RemoveAll(failure =>
                failure == null || now >= failure.ExpiresAt);
            while (_playerRadiusFallbackCandidateFailures.Count > 128)
                _playerRadiusFallbackCandidateFailures.RemoveAt(0);
        }

        private bool IsPlayerRadiusFallbackCandidateFailureCached(
            string definitionId,
            Vector3 playerPosition,
            DateTime now)
        {
            return _playerRadiusFallbackCandidateFailures.Any(failure =>
                failure != null
                && string.Equals(failure.DefinitionId, definitionId,
                    StringComparison.OrdinalIgnoreCase)
                && now < failure.ExpiresAt
                && (HorizontalDistance(failure.PlayerPosition, playerPosition)
                        <= PlayerRadiusFallbackFailureMatchDistanceMeters
                    || HorizontalDistance(failure.ScenePosition, playerPosition)
                        <= PlayerRadiusFallbackFailureMatchDistanceMeters));
        }

        private void CachePendingPlayerRadiusCandidateFailures(DateTime now)
        {
            foreach (KeyValuePair<string, PendingPlayerRadiusSceneCandidate> entry
                in _pendingPlayerRadiusSceneCandidates)
            {
                PendingPlayerRadiusSceneCandidate candidate = entry.Value;
                if (candidate == null)
                    continue;
                _playerRadiusFallbackCandidateFailures.RemoveAll(failure =>
                    failure == null
                    || (string.Equals(failure.DefinitionId, entry.Key,
                            StringComparison.OrdinalIgnoreCase)
                        && HorizontalDistance(failure.PlayerPosition, candidate.PlayerPosition)
                            <= PlayerRadiusFallbackFailureMatchDistanceMeters));
                _playerRadiusFallbackCandidateFailures.Add(
                    new PlayerRadiusFallbackCandidateFailure
                    {
                        DefinitionId = entry.Key,
                        PlayerPosition = candidate.PlayerPosition,
                        ScenePosition = candidate.ScenePosition,
                        ExpiresAt = now.AddSeconds(
                            PlayerRadiusFallbackFailureCacheSeconds),
                        FirstFailureStage = "PlayerRadiusRoadsideGround"
                    });
            }
            PrunePlayerRadiusFallbackCandidateFailures(now);
        }

        private void CacheFailedPreparedPlayerRadiusCandidate(
            LSPDDispatchEvent incident,
            Ped player,
            string failure,
            DateTime now)
        {
            if (incident == null || incident.SceneResolution == null
                || !string.Equals(
                    incident.SceneResolution.LocationCategory,
                    "PlayerRadiusRoadNode",
                    StringComparison.OrdinalIgnoreCase))
                return;

            string definitionId = DefinitionId(incident);
            if (string.IsNullOrWhiteSpace(definitionId))
                return;

            Vector3 candidatePlayerPosition = incident.SceneResolution.Area == null
                ? player == null || !player.Exists()
                    ? incident.Origin : player.Position
                : incident.SceneResolution.Area.Position;
            Vector3 scenePosition = incident.SceneResolution.SceneCenter;
            _playerRadiusFallbackCandidateFailures.RemoveAll(cached =>
                cached == null
                || (string.Equals(cached.DefinitionId, definitionId,
                        StringComparison.OrdinalIgnoreCase)
                    && (HorizontalDistance(cached.PlayerPosition, candidatePlayerPosition)
                            <= PlayerRadiusFallbackFailureMatchDistanceMeters
                        || HorizontalDistance(cached.ScenePosition, scenePosition)
                            <= PlayerRadiusFallbackFailureMatchDistanceMeters)));
            _playerRadiusFallbackCandidateFailures.Add(
                new PlayerRadiusFallbackCandidateFailure
                {
                    DefinitionId = definitionId,
                    PlayerPosition = candidatePlayerPosition,
                    ScenePosition = scenePosition,
                    ExpiresAt = now.AddSeconds(PlayerRadiusFallbackFailureCacheSeconds),
                    FirstFailureStage = GetDispatchSceneFailureStage(failure)
                });
            PrunePlayerRadiusFallbackCandidateFailures(now);
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
            bool exactPlayerRadiusRetryComplete = _pendingSceneResolutionRetryCount
                    >= PlayerRadiusPendingRetryCount
                && pendingDefinitions.Count > 0
                && pendingDefinitions.All(id =>
                    _pendingPlayerRadiusSceneCandidates.ContainsKey(id));
            if (exactPlayerRadiusRetryComplete
                || !IsDispatchScenePendingWindowActive(
                _pendingSceneResolutionStartedAt,
                now,
                SceneResolutionPendingMaximumSeconds))
            {
                CacheExpiredPendingSceneFailures(pendingFailures, now);
                CachePendingPlayerRadiusCandidateFailures(now);
                string expiredState = BuildPendingSceneResolutionStateSignature(pendingFailures);
                LogDebugTransition(
                    "POLICE_DISPATCH_SCENE_RESOLUTION_EXPIRED",
                    expiredState,
                    "Retries=" + _pendingSceneResolutionRetryCount
                    + "; ElapsedMilliseconds=" + pendingMilliseconds.ToString("0")
                    + "; MaximumPendingSeconds=" + SceneResolutionPendingMaximumSeconds
                    + "; ExactPlayerRadiusRetryComplete=" + exactPlayerRadiusRetryComplete
                    + withheld
                    + "; " + SummarizeDispatchSceneFailures(pendingFailures, 3));
                ResetPendingSceneResolution();
                _nextOfferAt = now;
                return false;
            }

            _pendingSceneResolutionDefinitionIds.Clear();
            _pendingSceneResolutionInteriorIds.Clear();
            foreach (string pendingId in pendingDefinitions)
                _pendingSceneResolutionDefinitionIds.Add(pendingId);
            foreach (string pendingFailure in pendingFailures)
            {
                string liveInteriorValue = DiagnosticFieldValue(
                    pendingFailure, "LiveInteriorId=");
                int liveInteriorId;
                if (int.TryParse(liveInteriorValue, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out liveInteriorId)
                    && liveInteriorId != 0)
                    _pendingSceneResolutionInteriorIds.Add(liveInteriorId);
            }
            _pendingSceneResolutionRetryCount++;
            _nextOfferAt = now.AddMilliseconds(
                SceneResolutionRetryDelayMilliseconds);
            _manualRequestRetryAt = _nextOfferAt;
            LogDebugTransition(
                "POLICE_DISPATCH_SCENE_RESOLUTION_PENDING",
                BuildPendingSceneResolutionStateSignature(pendingFailures),
                "Retry=" + _pendingSceneResolutionRetryCount
                + "; ElapsedMilliseconds=" + pendingMilliseconds.ToString("0")
                + "; MaximumPendingSeconds=" + SceneResolutionPendingMaximumSeconds
                + "; DelayMilliseconds=" + SceneResolutionRetryDelayMilliseconds
                + withheld
                + "; RetainedInteriorPins=" + string.Join(",",
                    _pendingSceneResolutionInteriorIds.OrderBy(value => value))
                + "; " + SummarizeDispatchSceneFailures(pendingFailures, 3));
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
                string failure = string.IsNullOrWhiteSpace(preparationFailure)
                    ? incident.ScenePreparationFailureReason
                    : preparationFailure;
                if (string.IsNullOrWhiteSpace(failure))
                    failure = "Stage=SceneOfferPreflight; Result=FailureReasonUnavailable";
                AmbientDispatchSceneResolution failedScene = incident.SceneResolution;
                CacheFailedPreparedPlayerRadiusCandidate(
                    incident, player, failure, now);
                if (failedScene != null
                    && !string.IsNullOrWhiteSpace(failedScene.LocationId))
                {
                    int cacheSeconds = IsPermanentDispatchLocationFailure(failure)
                        ? DispatchSceneCandidateFailureCacheSeconds
                        : DispatchSceneCandidateTransientFailureCacheSeconds;
                    CacheRejectedDispatchSceneCandidate(
                        DefinitionId(incident),
                        failedScene.LocationId,
                        GetDispatchSceneFailureStage(failure),
                        now,
                        cacheSeconds,
                        failedScene.PointId);
                }
                CleanupPreparedOccurrence(incident, "ScenePreflightFailed");
                LogDebugTransition(
                    "POLICE_DISPATCH_SCENE_PREFLIGHT_FAILED",
                    "Definition=" + DefinitionId(incident)
                    + ";Location=" + (failedScene == null ? "Unknown" : failedScene.LocationId)
                    + ";Point=" + (failedScene == null ? "Unknown" : failedScene.PointId)
                    + ";Stage=" + GetDispatchSceneFailureStage(failure),
                    "Event=" + incident.Id + "; Reason=" + failure);
                _preparedSceneCandidateFallthroughAttempts++;
                LogDebugTransition(
                    "POLICE_DISPATCH_PREPARED_SCENE_FELL_THROUGH",
                    "Event=" + DefinitionId(incident)
                        + ";Stage=" + GetDispatchSceneFailureStage(failure),
                    "The failed hidden scene was cleaned once; Dispatch will continue its bounded search on a later update.");
                ContinueDispatchSceneSearch(now, bypassAutomaticQuietPeriod);
                return false;
            }

            if (Receive(incident, bypassAutomaticQuietPeriod))
            {
                _preparedSceneCandidateFallthroughAttempts = 0;
                CompleteDispatchSceneSearch("OfferPublished");
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
            CompleteDispatchSceneSearch("OfferReceiveRejected");
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
            CompleteDispatchSceneSearch("PendingOfferCancelled");
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
                        && incident.ScenePreparationDeadline != DateTime.MinValue
                        && incident.ScenePreparationVehicleGroundRetryCount
                            < SceneVehicleGroundRetryLimit)
                    {
                        DateTime nextRetry = DateTime.UtcNow.AddMilliseconds(
                            SceneVehicleGroundRetryDelayMilliseconds);
                        incident.ScenePreparationVehicleGroundRetryCount++;
                        incident.ScenePreparationNextRetryAt = nextRetry < incident.ScenePreparationDeadline
                            ? nextRetry : incident.ScenePreparationDeadline;
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; MaximumPendingSeconds=" + ScenePreparationTimeoutSeconds
                            + "; Retry=" + incident.ScenePreparationVehicleGroundRetryCount
                            + "/" + SceneVehicleGroundRetryLimit
                            + "; DelayMilliseconds=" + SceneVehicleGroundRetryDelayMilliseconds
                            + "; Result=WaitingForVehicleRoadCollision";
                        return SceneOfferPreflightStatus.Pending;
                    }
                    if (boundedVehicleGroundPending)
                    {
                        reason = "Stage=" + stage + "; " + stageReason
                            + "; MaximumPendingSeconds=" + ScenePreparationTimeoutSeconds
                            + (incident.ScenePreparationDeadline == DateTime.MinValue
                                || DateTime.UtcNow >= incident.ScenePreparationDeadline
                                ? "; Result=VehicleRoadCollisionDeadlineExpired"
                                : "; Retry=" + incident.ScenePreparationVehicleGroundRetryCount
                                    + "/" + SceneVehicleGroundRetryLimit
                                    + "; Result=VehicleRoadCollisionRetryLimitReached");
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

                if (incident.SceneResolution != null
                    && incident.SceneResolution.IsInteriorScene)
                {
                    string interiorActorEvidence;
                    string interiorActorFailure;
                    if (!TryValidatePreparedInteriorActors(
                        incident,
                        out interiorActorEvidence,
                        out interiorActorFailure))
                    {
                        bool interiorActorPending = !string.IsNullOrWhiteSpace(interiorActorFailure)
                            && interiorActorFailure.StartsWith(
                                "PENDING:Stage=", StringComparison.OrdinalIgnoreCase);
                        string failedStage = interiorActorPending
                            ? interiorActorFailure.Substring("PENDING:Stage=".Length)
                                .Split(';')[0]
                            : "SceneRevalidation";
                        incident.ScenePreparationFailureStage = failedStage;
                        incident.ScenePreparationFailureReason = interiorActorFailure
                            ?? "Interior actor placement or mapped doorway preflight failed without a reason.";
                        string resultState = "Occurrence=" + incident.Id
                            + "; Stage=" + failedStage
                            + "; Result=" + (interiorActorPending ? "Pending" : "Rejected");
                        LogDebugTransition(
                            "POLICE_DISPATCH_INTERIOR_ACTOR_PREFLIGHT",
                            resultState,
                            "Occurrence=" + incident.Id
                            + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                            + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                            + "; Evidence=" + (string.IsNullOrWhiteSpace(interiorActorEvidence)
                                ? "No actor passed preflight."
                                : interiorActorEvidence)
                            + "; Failure=" + incident.ScenePreparationFailureReason);

                        if (interiorActorPending
                            && DateTime.UtcNow < incident.ScenePreparationDeadline
                            && incident.ScenePreparationDeadline != DateTime.MinValue)
                        {
                            DateTime nextRetry = DateTime.UtcNow.AddMilliseconds(
                                SceneActorSetupRetryDelayMilliseconds);
                            incident.ScenePreparationNextRetryAt = nextRetry < incident.ScenePreparationDeadline
                                ? nextRetry : incident.ScenePreparationDeadline;
                            reason = "Stage=" + failedStage + "; "
                                + incident.ScenePreparationFailureReason
                                + "; Result=WaitingForInteriorActorReadiness";
                            return SceneOfferPreflightStatus.Pending;
                        }

                        reason = "Stage=" + failedStage + "; "
                            + incident.ScenePreparationFailureReason
                            + (interiorActorPending
                                ? "; Result=InteriorActorReadinessDeadlineExpired"
                                : "; Result=InteriorActorSceneRejectedBeforeOffer");
                        return SceneOfferPreflightStatus.Failed;
                    }

                    if (_log != null)
                        _log.RuntimeTransition(
                            "POLICE_DISPATCH_INTERIOR_ACTOR_PREFLIGHT",
                            "Occurrence=" + incident.Id + "; Result=Ready",
                            "Occurrence=" + incident.Id
                            + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                            + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                            + "; Actors=" + interiorActorEvidence
                            + "; Owner=Dispatch; ReadyBeforeOffer=true"
                            + "; DoorwayRoute=MappedThresholdResolved; NavmeshTraversal=NotProbed");
                }
                else if (incident.SceneResolution != null)
                {
                    string exteriorActorEvidence;
                    string exteriorActorFailure;
                    if (!TryValidatePreparedExteriorActors(
                        incident,
                        true,
                        out exteriorActorEvidence,
                        out exteriorActorFailure))
                    {
                        SetScenePreparationFailure(
                            incident,
                            "ExteriorParticipantPlacement",
                            exteriorActorFailure ?? "An exterior actor failed hidden placement validation.");
                        LogDebugTransition(
                            "POLICE_DISPATCH_EXTERIOR_ACTOR_PREFLIGHT",
                            "Occurrence=" + incident.Id + "; Result=Rejected; Failure="
                                + incident.ScenePreparationFailureReason,
                            "Occurrence=" + incident.Id
                            + "; Location=" + incident.SceneResolution.LocationId
                            + "; Point=" + incident.SceneResolution.PointId
                            + "; Failure=" + incident.ScenePreparationFailureReason);
                        return SceneOfferPreflightStatus.Failed;
                    }

                    bool pairedFormationCorrected;
                    string pairedFormationEvidence;
                    string pairedFormationFailure;
                    SceneOfferPreflightStatus pairedFormationStatus =
                        TryPreparePairedExteriorSceneFormation(
                        incident,
                        out pairedFormationCorrected,
                        out pairedFormationEvidence,
                        out pairedFormationFailure);
                    if (pairedFormationStatus != SceneOfferPreflightStatus.Ready)
                    {
                        reason = pairedFormationFailure;
                        return pairedFormationStatus;
                    }
                    if (pairedFormationCorrected
                        && !TryValidatePreparedExteriorActors(
                            incident,
                            true,
                            out exteriorActorEvidence,
                            out exteriorActorFailure))
                    {
                        reason = exteriorActorFailure;
                        return SceneOfferPreflightStatus.Failed;
                    }
                    if (!string.IsNullOrWhiteSpace(pairedFormationEvidence))
                        exteriorActorEvidence += " || " + pairedFormationEvidence;

                    if (_log != null)
                        _log.RuntimeTransition(
                            "POLICE_DISPATCH_EXTERIOR_ACTOR_PREFLIGHT",
                            "Occurrence=" + incident.Id + "; Result=Ready",
                            "Occurrence=" + incident.Id
                            + "; Location=" + incident.SceneResolution.LocationId
                            + "; Point=" + incident.SceneResolution.PointId
                            + "; Actors=" + exteriorActorEvidence
                            + "; Owner=Dispatch; ReadyBeforeOffer=true"
                            + "; HiddenUntilAcceptance=true");
                }
                else
                {
                    reason = "Stage=ExteriorParticipantPlacement; Result=SceneResolutionUnavailable";
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

                if (IsDisturbanceActivityScene(incident))
                {
                    bool assetPending = false;
                    string assetFailure = string.Empty;
                    if (_ambientWorld == null
                        || !_ambientWorld.TryPrepareDispatchDisturbanceAssets(
                            incident, out assetPending, out assetFailure))
                    {
                        if (_ambientWorld == null)
                            assetFailure = "AmbientWorld is not attached by PoliceCore.";
                        incident.ScenePreparationFailureStage = "DisturbanceAsset";
                        incident.ScenePreparationFailureReason = assetFailure;
                        if (assetPending && DateTime.UtcNow < incident.ScenePreparationDeadline)
                        {
                            incident.ScenePreparationNextRetryAt = DateTime.UtcNow.AddMilliseconds(1000);
                            reason = "Stage=DisturbanceAsset; " + assetFailure
                                + "; Result=WaitingForAmbientWorldAsset";
                            return SceneOfferPreflightStatus.Pending;
                        }
                        reason = "Stage=DisturbanceAsset; " + assetFailure
                            + "; Result=Rejected";
                        return SceneOfferPreflightStatus.Failed;
                    }
                }

                incident.ScenePreparationCompleted = true;
                LogRuntime(
                    "POLICE_DISPATCH_SCENE_PREFLIGHTED",
                    "Event=" + incident.Id
                    + "; Suspects=" + liveSuspects.Count
                    + "; AdditionalParticipants=" + incident.AdditionalParticipants.Count(value =>
                        value != null && value.Exists())
                    + "; ScenePeople=" + CountDispatchScenePeople(incident)
                    + "; Vehicle=" + (incident.SuspectVehicle == null ? 0 : incident.SuspectVehicle.Handle)
                    + "; Location=" + (incident.SceneResolution == null
                        ? string.Empty : incident.SceneResolution.LocationId)
                    + "; ReadyBeforeOffer=true"
                    + "; SceneActorsHiddenUntilAcceptance=true"
                    + "; InteriorActorsHiddenUntilAcceptance="
                    + (incident.SceneResolution != null
                        && incident.SceneResolution.IsInteriorScene)
                    + "; ExteriorActorsHiddenUntilAcceptance="
                    + (incident.SceneResolution != null
                        && !incident.SceneResolution.IsInteriorScene));
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

        private void ProcessControllerInput(bool suppressPlayerInteractionInput)
        {
            if (_incident == null)
                return;

            if (suppressPlayerInteractionInput)
            {
                SynchronizeKeyboardInputStates();
                return;
            }

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

            if (WasKeyPressed(_controls.InvestigateKey, ref _investigateKeyDown))
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
                    Notify(SecureOrArrestSuspect(Game.Player.Character));
            }
        }

        private static void SuppressPlayerHandcuffControlsThisFrame()
        {
            LSPDPlayerControlSuppression.SuppressMeleeThisFrame();
            Game.DisableControlThisFrame(GTA.Control.Context);
            Game.DisableControlThisFrame(GTA.Control.Enter);
            Game.DisableControlThisFrame(GTA.Control.MoveLeftRight);
            Game.DisableControlThisFrame(GTA.Control.MoveUpDown);
            Game.DisableControlThisFrame(GTA.Control.Sprint);
            Game.DisableControlThisFrame(GTA.Control.Jump);
            Game.DisableControlThisFrame(GTA.Control.Duck);
            Game.DisableControlThisFrame(GTA.Control.Aim);
            Game.DisableControlThisFrame(GTA.Control.Reload);
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

            if (string.Equals(incident.IncidentType, "disturbance",
                StringComparison.OrdinalIgnoreCase))
                incident.Briefing = BuildDisturbanceBriefing(incident);

            incident.State = LSPDDispatchState.Offered;
            incident.StateChangedAt = DateTime.UtcNow;
            _incident = incident;
            _definitionOfferedAt[DefinitionId(incident)] = DateTime.UtcNow;
            if (CanPlayOfferAudio())
                ReportAudioStage("lsimmersivelife.police.dispatch.received", incident, "received");
            // LSImmersivePoliceUI owns the persistent callout while this
            // incident remains Offered. Do not post a second feed ticker over
            // that same screen area; the duplicate surfaces caused the call
            // details to flicker and overlap.
            LogRuntime(
                "POLICE_DISPATCH_OFFERED",
                incident.Title + " | Type=" + incident.IncidentType
                + " | Context=" + incident.LocationContext
                + " | Branches=" + string.Join(",", incident.BranchIds ?? new List<string>())
                + " | Origin=" + incident.Origin);
            return true;
        }

        private static string BuildDisturbanceBriefing(LSPDDispatchEvent incident)
        {
            return "Reported disturbance involving "
                + CountDispatchScenePeople(incident).ToString(CultureInfo.InvariantCulture)
                + " people. Identify everyone involved and check whether anyone needs help.";
        }

        private static int CountDispatchScenePeople(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return 0;
            var handles = new HashSet<int>();
            if (incident.Suspects != null)
                foreach (Ped suspect in incident.Suspects)
                    if (suspect != null && suspect.Exists() && !suspect.IsDead)
                        handles.Add(suspect.Handle);
            if (incident.Victim != null && incident.Victim.Exists()
                && !incident.Victim.IsDead && incident.Victim.Health > 0)
                handles.Add(incident.Victim.Handle);
            if (incident.AdditionalParticipants != null)
                foreach (Ped participant in incident.AdditionalParticipants)
                    if (participant != null && participant.Exists() && !participant.IsDead)
                        handles.Add(participant.Handle);

            return Math.Max(1, handles.Count);
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

            bool interiorScene = _incident.SceneResolution != null
                && _incident.SceneResolution.IsInteriorScene;
            string acceptedSceneFailure;
            if (interiorScene
                && !PrepareAcceptedInteriorScene(_incident, out acceptedSceneFailure))
            {
                LogRuntime(
                    "POLICE_DISPATCH_ACCEPTED_INTERIOR_SCENE_REJECTED",
                    "Occurrence=" + _incident.Id
                    + "; Stage=AcceptanceReadiness; Result=Failed"
                    + "; Failure=" + acceptedSceneFailure);
                Fail("The accepted interior Dispatch scene failed its final hidden-actor readiness check. "
                    + acceptedSceneFailure);
                return LSPDAudioResult.Unavailable;
            }
            if (!interiorScene
                && !PrepareAcceptedExteriorScene(_incident, out acceptedSceneFailure))
            {
                LogRuntime(
                    "POLICE_DISPATCH_ACCEPTED_EXTERIOR_SCENE_REJECTED",
                    "Occurrence=" + _incident.Id
                    + "; Stage=AcceptanceReadiness; Result=Failed"
                    + "; Failure=" + acceptedSceneFailure);
                Fail("The accepted exterior Dispatch scene failed its final hidden-actor readiness check. "
                    + acceptedSceneFailure);
                return LSPDAudioResult.Unavailable;
            }

            SetState(LSPDDispatchState.Accepted);
            SetState(LSPDDispatchState.EnRoute);
            _incident.NextEnRouteStatusAt = DateTime.UtcNow.AddSeconds(30);
            CaptureAndSetWaypoint(_incident.Origin);
            SetSceneBlip(_incident.Origin, _incident.Title);
            if (_incident.Suspect != null && _incident.Suspect.Exists())
                SetSuspectBlip(_incident.Suspect);
            // Start the authored physical activity on acceptance so it is
            // already underway while the Player travels. Process remains the
            // retry owner for activities that need later streamed assets.
            StartSceneBehavior(player);
            if (_incident == null || !_incident.OwnedByDispatch)
                return LSPDAudioResult.Unavailable;
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

        private bool PrepareAcceptedInteriorScene(
            LSPDDispatchEvent incident,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || incident != _incident
                || incident.SceneResolution == null
                || !incident.SceneResolution.IsInteriorScene)
            {
                reason = "The active occurrence no longer owns its selected interior scene.";
                return false;
            }
            if (_ambientWorld == null
                || !_ambientWorld.TryRevalidatePreparedInteriorScene(
                    incident.SceneResolution,
                    incident.RequiresVehicle || incident.VehicleStagingRequired,
                    incident.RequiredPhysicalType,
                    incident.LocationContext,
                    out reason))
            {
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "AmbientWorld could not revalidate the selected interior at acceptance.";
                return false;
            }

            string actorEvidence;
            string actorFailure;
            if (!TryValidatePreparedInteriorActors(
                incident, out actorEvidence, out actorFailure))
            {
                reason = string.IsNullOrWhiteSpace(actorFailure)
                    ? "A required hidden interior actor failed acceptance validation."
                    : actorFailure;
                return false;
            }
            if (!RevealPreparedDispatchSceneActors(incident))
            {
                reason = "One or more validated interior actors could not be revealed as a complete scene.";
                return false;
            }

            LogRuntime(
                "POLICE_DISPATCH_INTERIOR_ACTORS_REVEALED_AFTER_ACCEPT",
                "Occurrence=" + incident.Id
                + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                + "; Actors=" + actorEvidence
                + "; Owner=Dispatch; AfterAcceptance=true; ActivityStartsImmediately=true");
            return true;
        }

        private bool PrepareAcceptedExteriorScene(
            LSPDDispatchEvent incident,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || incident != _incident
                || incident.SceneResolution == null
                || incident.SceneResolution.IsInteriorScene)
            {
                reason = "The active occurrence no longer owns its selected exterior scene.";
                return false;
            }

            string actorEvidence;
            if (!TryValidatePreparedExteriorActors(
                incident, false, out actorEvidence, out reason))
                return false;
            if (!RevealPreparedDispatchSceneActors(incident))
            {
                reason = "One or more validated hidden exterior actors could not be revealed as a complete scene.";
                return false;
            }

            LogRuntime(
                "POLICE_DISPATCH_EXTERIOR_ACTORS_REVEALED_AFTER_ACCEPT",
                "Occurrence=" + incident.Id
                + "; Location=" + incident.SceneResolution.LocationId
                + "; Point=" + incident.SceneResolution.PointId
                + "; Actors=" + actorEvidence
                + "; Owner=Dispatch; AfterAcceptance=true; ActivityStartsImmediately=true");
            return true;
        }

        internal LSPDAudioResult Decline()
        {
            if (!HasOffer)
                return LSPDAudioResult.Inactive;
            LSPDDispatchEvent incident = _incident;
            RecordDispatchLocationRepeatProtection(
                incident, DateTime.UtcNow, "Declined");
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
            if (!HasPendingVictimInvestigation)
                return "This incident branch has no active investigation objective. Use E for suspect interaction when needed.";
            if (!_incident.ScenePreparationCompleted || _incident.SceneResolution == null)
                return "Dispatch is still preparing the murder scene. Wait until the scene is ready before using I.";
            if (_incident.PlayerHandcuffInProgress)
                return "Finish the physical suspect interaction with E before investigating the murder victim.";

            List<Ped> unsecuredSuspects = LiveSuspects(_incident)
                .Where(suspect => !IsArrested(_incident, suspect))
                .ToList();
            if (unsecuredSuspects.Count > 0)
                return "Secure the remaining living suspect(s) with E before investigating the murder victim.";
            if (player.IsInVehicle())
                return "Exit the vehicle and approach the murder victim before using I.";

            Ped investigationVictim = _incident.Victim;
            float victimDistance = investigationVictim != null
                && investigationVictim.Exists()
                ? player.Position.DistanceTo(investigationVictim.Position) : -1f;
            LogRuntime(
                "POLICE_DISPATCH_MURDER_VICTIM_INVESTIGATION_INPUT_RECEIVED",
                "Incident=" + _incident.Id
                + "; InputKey=I; InputOwner=Dispatch; Player=" + player.Handle
                + "; Victim=" + (investigationVictim == null
                    ? 0 : investigationVictim.Handle)
                + "; VictimDistance=" + victimDistance.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; State=" + _incident.State
                + "; VictimInvestigationPending=true"
                + "; UnsecuredSuspects=0; ThreatSecured=true");

            LSPDDispatchEvent incident = _incident;
            if (!TryRecordMurderVictimInvestigation(incident, player))
                return "Move within "
                    + MurderVictimInvestigationRadius.ToString("0.0", CultureInfo.InvariantCulture)
                    + " metres of the murder victim after entering the mapped doorway, if this scene is indoors.";

            List<Ped> arrestedSuspects = CurrentArrestedSuspects.ToList();
            if (arrestedSuspects.Count > 0)
            {
                SetState(LSPDDispatchState.Arrested);
                LogRuntime(
                    "POLICE_DISPATCH_TRANSPORT_BECAME_AVAILABLE",
                    "Incident=" + incident.Id
                    + "; Trigger=MurderVictimInvestigationCompleted"
                    + "; State=" + _incident.State
                    + "; Prisoners=" + arrestedSuspects.Count
                    + "; VictimInvestigationPending=" + HasPendingVictimInvestigation
                    + "; RequestTransport=true");
                Notify("~b~DISPATCH INVESTIGATION~s~\nVictim examination recorded. Request prisoner transport when ready.");
                return "Victim investigation recorded. Request prisoner transport when ready.";
            }

            if (HasUnfinishedArrestedCustody(incident))
                return FailUnfinishedArrestedCustody(incident);

            string completion = CompletionMessage(incident, true);
            Complete(completion);
            return completion;
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
            bool playerAvailable = player != null && player.Exists();
            bool victimAvailable = victim != null && victim.Exists();
            bool playerInteriorIdentityAvailable = mappedInteriorMurder
                && playerAvailable && _ambientWorld != null
                && incident.SceneResolution != null
                && incident.SceneResolution.IsInteriorScene
                && (incident.SceneResolution.IsMappedInteriorDispatchScene
                    || incident.SceneResolution.IsInsideOnlyInteriorScene)
                && _ambientWorld.TryGetDispatchPlayerInteriorId(
                    player, out playerInteriorId);
            bool playerInsideIncidentInterior = !mappedInteriorMurder
                || (playerInteriorIdentityAvailable
                    && incident.SceneResolution != null
                    && playerInteriorId == incident.SceneResolution.LiveInteriorId);
            float victimDistance = playerAvailable && victimAvailable
                ? player.Position.DistanceTo(victim.Position) : -1f;
            bool victimStateReady = victimAvailable
                && LSPDDispatchEventDefinition.IsVictimStateReady(
                    true, victim.IsDead, victim.Health);
            bool withinVictimRange = victimDistance >= 0f
                && victimDistance <= MurderVictimInvestigationRadius;
            string reason = !victimAvailable ? "VictimUnavailable"
                : !victimStateReady ? "VictimNotConfirmedDead"
                : !playerAvailable ? "PlayerUnavailable"
                : !withinVictimRange ? "VictimOutsideInvestigationRange"
                : !playerInsideIncidentInterior ? "MappedInteriorNotConfirmed"
                : string.Empty;
            LogRuntime(
                "POLICE_DISPATCH_MURDER_VICTIM_INVESTIGATION_VALIDATION",
                "Incident=" + incident.Id
                + "; Player=" + (player == null ? 0 : player.Handle)
                + "; Victim=" + (victim == null ? 0 : victim.Handle)
                + "; VictimDistance=" + victimDistance.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; InvestigationRadius=" + MurderVictimInvestigationRadius.ToString(
                    "0.0", CultureInfo.InvariantCulture)
                + "; PlayerInteriorIdentityAvailable="
                + playerInteriorIdentityAvailable
                + "; PlayerInteriorId=" + playerInteriorId
                + "; SceneInteriorId=" + (incident.SceneResolution == null
                    ? 0 : incident.SceneResolution.LiveInteriorId)
                + "; Result=" + (string.IsNullOrEmpty(reason) ? "Accepted" : "Rejected")
                + "; Reason=" + (string.IsNullOrEmpty(reason) ? "None" : reason));
            if (!string.IsNullOrEmpty(reason))
            {
                LogDebug("POLICE_DISPATCH_MURDER_VICTIM_INVESTIGATION_UNAVAILABLE",
                    "Incident=" + incident.Id
                    + "; Victim=" + (victim == null ? 0 : victim.Handle)
                    + "; Player=" + (player == null ? 0 : player.Handle)
                    + "; VictimDistance=" + victimDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; PlayerInteriorId=" + playerInteriorId
                    + "; SceneInteriorId=" + (incident.SceneResolution == null
                        ? 0 : incident.SceneResolution.LiveInteriorId)
                    + "; Reason=" + reason);
                return false;
            }

            incident.VictimInvestigated = true;
            LogRuntime("POLICE_DISPATCH_MURDER_VICTIM_INVESTIGATED",
                "Incident=" + incident.Id
                + "; Victim=" + victim.Handle
                + "; Player=" + player.Handle
                + "; InputKey=I; VictimDistance=" + victimDistance.ToString(
                    "0.00", CultureInfo.InvariantCulture)
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
                if (fleeing && IsDisturbanceActivityScene(_incident))
                    return "The suspect fled after the Police command. Pursue or request Backup before securing them.";
                if (fleeing && !target.IsInVehicle()
                    && targetDistance <= ArrestRadius + 4f
                    && !IsDownedOrInjured(target))
                    return OrderSuspectToComply(target, player, false, true);
                if (fleeing && target.IsInVehicle())
                    return "The fleeing driver is still in the vehicle. Stop the vehicle before issuing the surrender command.";
                return "Move closer to the next unsecured suspect before issuing the Police command.";
            }

            if (IsDispatchMeleeRecoveryPending(target, DateTime.UtcNow))
                return "Wait for the suspect's physical reaction to finish, then secure the same suspect.";

            bool downedAlive = IsDownedOrInjured(target);
            if (!IsPlayerCommandedSecure(_incident, target))
            {
                if (downedAlive)
                {
                    try
                    {
                        target.BlockPermanentEvents = true;
                        target.CanSwitchWeapons = false;
                    }
                    catch { }
                    MarkPlayerCommandedSecure(_incident, target);
                    return "The wounded suspect is secured for arrest. Press E again to begin the ground arrest.";
                }

                if (IsCompliant(_incident, target))
                    return OrderSuspectToComply(target, player, false, true);

                if (IsDisturbanceActivityScene(_incident))
                {
                    string response;
                    if (!_incident.SuspectResponses.TryGetValue(target.Handle, out response))
                    {
                        _incident.DisturbanceCommandTriggerSuspectHandle = target.Handle;
                        StartPlayerEncounterResponse(player);
                        if (_incident != null && _incident.OwnedByDispatch
                            && _incident.SuspectResponses.TryGetValue(target.Handle, out response))
                        {
                            if (string.Equals(response, "comply",
                                StringComparison.OrdinalIgnoreCase))
                                MarkPlayerCommandedSecure(_incident, target);
                            return DisturbanceCommandOutcomeMessage(response);
                        }
                        return "Police command issued. Keep a safe distance and observe the suspect's response.";
                    }
                    return DisturbanceCommandOutcomeMessage(response);
                }
                if (IsActiveShooterScene(_incident))
                {
                    string response;
                    if (!_incident.SuspectResponses.TryGetValue(
                        target.Handle, out response))
                    {
                        _incident.ActiveShooterCommandTriggerSuspectHandle =
                            target.Handle;
                        StartPlayerEncounterResponse(player);
                        if (_incident != null && _incident.OwnedByDispatch
                            && _incident.SuspectResponses.TryGetValue(
                                target.Handle, out response))
                        {
                            if (string.Equals(response, "comply",
                                StringComparison.OrdinalIgnoreCase))
                                MarkPlayerCommandedSecure(_incident, target);
                            return DisturbanceCommandOutcomeMessage(response);
                        }
                        return "Police command issued. Observe the active shooter's response.";
                    }
                    return DisturbanceCommandOutcomeMessage(response);
                }
                return OrderSuspectToComply(target, player, false, true);
            }

            if (!downedAlive
                && DateTime.UtcNow < _incident.SurrenderRequestedAt.AddSeconds(SurrenderSettleSeconds))
                return "Surrender command issued. Wait for visible compliance, then secure again.";

            return "The suspect is under your Secure command and arrestable. Press E again to begin the arrest.";
        }

        internal string SecureOrArrestSuspect(Ped player)
        {
            if (_incident == null || player == null || !player.Exists())
                return SecureSuspect(player);

            // E is contextual only after this Player has issued Secure for the
            // exact suspect. An authored or Backup-created comply response
            // must still receive the first Player E interaction.
            if (FindPlayerArrestTarget(player, true) != null)
                return ArrestSuspect(player);
            return SecureSuspect(player);
        }

        private Ped FindPlayerArrestTarget(Ped player, bool requireReach)
        {
            if (_incident == null || player == null || !player.Exists())
                return null;

            List<Ped> eligible = LiveSuspects(_incident)
                .Where(ped => !IsArrested(_incident, ped)
                    && !_incident.BackupArrestInProgressSuspectHandles.Contains(
                        ped.Handle))
                .ToList();
            IEnumerable<Ped> arrestable = eligible.Where(ped =>
                IsPlayerCommandedSecure(_incident, ped)
                    && (IsCompliant(_incident, ped) || IsDownedOrInjured(ped)));

            if (requireReach)
                return arrestable
                    .Where(ped => ped.Position.DistanceTo(player.Position)
                        <= ArrestRadius + 1f)
                    .OrderBy(ped => ped.Position.DistanceTo(player.Position))
                    .FirstOrDefault();

            Ped arrestableTarget = arrestable
                .OrderBy(ped => ped.Position.DistanceTo(player.Position))
                .FirstOrDefault();
            return arrestableTarget ?? eligible
                .OrderBy(ped => ped.Position.DistanceTo(player.Position))
                .FirstOrDefault();
        }

        internal string ArrestSuspect(Ped player)
        {
            if (_incident == null)
                return "There is no active Dispatch suspect to arrest.";
            if (player == null || !player.Exists())
                return "Player character unavailable.";
            if (_incident.State == LSPDDispatchState.Offered
                || _incident.State == LSPDDispatchState.EnRoute)
                return "Arrive at the dispatch scene before arresting a suspect.";
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
                return "Convoy already owns this secured prisoner. Continue the active transport operation.";
            if (_incident.PlayerHandcuffInProgress)
                return "Physical handcuffing is already in progress. Keep close to the suspect.";

            Ped target = FindPlayerArrestTarget(player, false);
            if (target == null)
                return SecureSuspect(player);

            float distance = target.Position.DistanceTo(player.Position);
            if (distance > ArrestRadius + 1f)
                return "Move closer to the compliant suspect before arresting them.";
            if (!IsPlayerCommandedSecure(_incident, target))
                return "Use E to Secure the suspect before beginning the physical arrest.";
            if (!IsCompliant(_incident, target) && !IsDownedOrInjured(target))
                return "The suspect is not arrestable yet. Secure them and wait for compliance or a documented wounded state.";
            if (IsDispatchMeleeRecoveryPending(target, DateTime.UtcNow))
                return "Wait for the suspect's physical reaction to finish, then arrest the same suspect.";
            return BeginPlayerHandcuff(player, target);
        }

        private static string DisturbanceCommandOutcomeMessage(string response)
        {
            if (string.Equals(response, "comply", StringComparison.OrdinalIgnoreCase))
                return "The suspect is complying. Wait for visible hands-up before securing them.";
            if (string.Equals(response, "resist", StringComparison.OrdinalIgnoreCase))
                return "The suspect resisted the Police command. Keep distance and request Backup if needed.";
            if (string.Equals(response, "flee", StringComparison.OrdinalIgnoreCase))
                return "The suspect fled after the Police command. Pursue or request Backup before securing them.";
            return "Police command issued. Keep a safe distance and observe the suspect's response.";
        }

        private static bool IsDisturbanceActivityScene(LSPDDispatchEvent incident)
        {
            return incident != null
                && string.Equals(incident.Id, "disturbance", StringComparison.OrdinalIgnoreCase)
                && string.Equals(incident.IncidentType, "disturbance",
                    StringComparison.OrdinalIgnoreCase)
                && !incident.RequiresVehicle
                && incident.SuspectCount == 1
                && incident.VictimCount == 1
                && incident.DisturbanceActivities != null
                && incident.DisturbanceActivities.Count > 0;
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
                // The task now owns the paired physical arrest. Keep the
                // suspect open to normal injury before this transition, then
                // protect the exact suspect through animation and confirmed
                // cuffs using Dispatch's captured proof state.
                if (!TryApplyDispatchCustodyDamageProtection(suspect, incident))
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        suspect, true, true, true, true, true, true, true, true);
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
            incident.PlayerCommandedSecureSuspectHandles.Remove(releasedHandle);
            _lastCompliantTaskAt.Remove(releasedHandle);
            ClearSecuredPoseTracking(releasedHandle);
            // Release the injury snapshot only after Dispatch has removed the
            // Ped from its custody collections. This restores the same living
            // actor's original health/weapon controls without reviving it.
            RestoreSuspectInjuryState(suspect);
            LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_STATE_RELEASED",
                "Ped=" + releasedHandle + "; RuntimeOwner=Dispatch"
                + "; InjuryStateRemoved=true; SamePed=true"
                + "; Health=" + suspect.Health
                + "; CriticalHitsRestored=true");

            bool releasedPrimarySuspect = incident.Suspect != null
                && incident.Suspect.Handle == releasedHandle;
            if (releasedPrimarySuspect)
            {
                // LiveSuspects falls back to incident.Suspect when the tracked
                // collection is empty. Clear the released primary first so a
                // one-suspect incident cannot select that same living Ped as
                // its own unresolved replacement.
                incident.Suspect = null;
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

            string sceneSpaceFailure;
            if (!TryValidatePlayerHandcuffSceneSpace(
                player, target, posture, out sceneSpaceFailure))
            {
                ReportPlayerHandcuffSpacePreflight(
                    player, target, posture, sceneSpaceFailure);
                return sceneSpaceFailure
                    + " The suspect remains compliant and arrestable. Reposition to a safe paired position and press E again.";
            }
            ReportPlayerHandcuffSpacePreflight(player, target, posture, string.Empty);

            LSPDPlayerControlSuppression.SuppressMeleeThisFrame();

            DateTime now = DateTime.UtcNow;
            try
            {
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                bool targetDowned = posture
                    == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded;
                _playerHandcuffTargetPosture = posture;
                _playerHandcuffPairedAnimationAttempts = 0;
                _incident.PlayerHandcuffInProgress = true;
                _incident.PlayerHandcuffAnimationStarted = false;
                _incident.PlayerHandcuffTargetHandle = target.Handle;
                _incident.PlayerHandcuffStartedAt = now;

                if (targetDowned)
                {
                    // A living grounded suspect must be arrested in place.
                    // Requiring HandsUp/standing first left injured actors in
                    // an endless writhing loop and never reached physical cuffs.
                    _playerHandcuffAttempts = 0;
                    LogRuntime("POLICE_DISPATCH_PLAYER_GROUNDED_ARREST_STARTED",
                        "Ped=" + target.Handle + "; Player=" + player.Handle
                        + "; Ragdoll=" + IsLivingDowned(target)
                        + "; Writhe=" + IsLivingInWrithe(target)
                        + "; TaskClearedBeforeSpaceValidation=false"
                        + "; SamePed=true");
                }

                // The input or UI caller presents this returned status. Posting
                // another ticker here duplicated the same arrest action.
                bool animationStarted = StartPlayerHandcuffAnimation(
                    player, target, now);
                if (!animationStarted
                    && !string.IsNullOrWhiteSpace(_playerHandcuffSceneSpaceFailure))
                    return ReleasePlayerHandcuffForSpaceRetry();
                if (!animationStarted && _playerHandcuffWaitingForAnimation)
                    return "Preparing the handcuff animation...";
                if (!animationStarted)
                {
                    AbortPlayerHandcuffAnimation(
                        player, target, "PAIRED_ANIMATION_START_REJECTED");
                    return "The paired arrest could not start safely. The suspect remains available; press E to retry when both actors are in position.";
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
            DateTime now)
        {
            if (player == null || !player.Exists()
                || target == null || !target.Exists() || target.IsDead)
                return false;

            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture =
                _playerHandcuffTargetPosture
                    != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                    ? _playerHandcuffTargetPosture
                    : LSImmersiveDictionaryAnimation.ClassifyArrestTarget(target);
            if (posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Dead
                || posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Cuffed)
                return false;

            string sceneSpaceFailure;
            if (!TryValidatePlayerHandcuffSceneSpace(
                player, target, posture, out sceneSpaceFailure))
            {
                _playerHandcuffSceneSpaceFailure = sceneSpaceFailure;
                ReportPlayerHandcuffSpacePreflight(
                    player, target, posture, sceneSpaceFailure);
                return false;
            }
            _playerHandcuffSceneSpaceFailure = null;
            ReportPlayerHandcuffSpacePreflight(player, target, posture, string.Empty);

            int previousSceneId = _playerHandcuffSceneId;
            if (previousSceneId >= 0)
            {
                try { player.Task.ClearAll(); } catch { }
                try { target.Task.ClearAll(); } catch { }
                RestorePlayerHandcuffTargetPosture(
                    player, target, _playerHandcuffTargetPosture);
                LogRuntime("POLICE_DISPATCH_ARREST_PAIR_RECOVERING_FOR_RETRY",
                    "Ped=" + target.Handle + "; Player=" + player.Handle
                    + "; PreviousScene=" + previousSceneId
                    + "; Posture=" + _playerHandcuffTargetPosture);
            }

            _playerHandcuffPairedAnimationStarted = false;
            _playerHandcuffWaitingForAnimation = false;
            _playerHandcuffWaitingDictionary = null;
            _playerHandcuffSceneId = -1;
            _playerHandcuffAnimationVerifyAt = DateTime.MinValue;
            _playerHandcuffAnimationVerified = false;
            _playerHandcuffSceneStartedAt = DateTime.MinValue;
            _playerHandcuffSceneLastProgressAt = DateTime.MinValue;
            _playerHandcuffSceneClipInterruptedAt = DateTime.MinValue;
            _playerHandcuffSceneLastPhase = -1f;
            _playerHandcuffSceneHighestPhase = 0f;
            _playerHandcuffSceneProgressConfirmed = false;
            _playerHandcuffSceneCompleted = false;
            _playerHandcuffOfficerStartPosition = Vector3.Zero;
            _playerHandcuffSuspectStartPosition = Vector3.Zero;
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
            bool groundedArrestTask =
                posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded;
            bool kneelingArrestTask = !groundedArrestTask
                && posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling;
            _playerHandcuffPreserveTargetPosture = groundedArrestTask || kneelingArrestTask;
            string dictionary = groundedArrestTask ? "mp_arresting" : "mp_arrest_paired";
            string officerClip = groundedArrestTask
                ? "arrest_on_floor_front_right_a" : "cop_p2_back_right";
            string suspectClip = groundedArrestTask
                ? "arrest_on_floor_front_right_b" : "crook_p2_back_right";
            if (groundedArrestTask)
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
                    if (_playerHandcuffAnimationLoadUntil == DateTime.MinValue)
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
                if (_playerHandcuffPairedAnimationAttempts
                    >= PlayerHandcuffMaximumPairedAnimationAttempts)
                    return false;
                _playerHandcuffPairedAnimationAttempts++;
                // Space has now passed the physical preflight and the paired
                // clip is loaded. Only at this point may the wounded/writhe
                // task be replaced by synchronized ground arrest.
                target.Task.ClearAll();
                _playerHandcuffPairedAnimationStarted =
                    LSImmersiveDictionaryAnimation.TryPlaySynchronizedGroundArrest(
                        player, target, out sceneId, out clipDuration,
                        out sceneOrigin, out sceneHeading);
            }
            else if (!kneelingArrestTask)
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
                    if (_playerHandcuffAnimationLoadUntil == DateTime.MinValue)
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
                if (_playerHandcuffPairedAnimationAttempts
                    >= PlayerHandcuffMaximumPairedAnimationAttempts)
                    return false;
                _playerHandcuffPairedAnimationAttempts++;
                _playerHandcuffPairedAnimationStarted =
                    LSImmersiveDictionaryAnimation.TryPlaySynchronizedArrest(
                        player, target, out sceneId, out clipDuration,
                        out sceneOrigin, out sceneHeading);
            }

            if (!_playerHandcuffPairedAnimationStarted)
            {
                if (!kneelingArrestTask)
                {
                    if (_playerHandcuffPairedAnimationAttempts
                        < PlayerHandcuffMaximumPairedAnimationAttempts)
                    {
                        _playerHandcuffWaitingForAnimation = true;
                        _playerHandcuffWaitingDictionary = dictionary;
                        if (_playerHandcuffAnimationLoadUntil == DateTime.MinValue)
                            _playerHandcuffAnimationLoadUntil = now.AddSeconds(
                                PlayerHandcuffAnimationLoadTimeoutSeconds);
                        _playerHandcuffHoldUntil = DateTime.MinValue;
                        LogRuntime("POLICE_DISPATCH_ARREST_PAIR_START_RETRY_SCHEDULED",
                            "Ped=" + target.Handle + "; Player=" + player.Handle
                            + "; Posture=" + posture
                            + "; Dictionary=" + dictionary
                            + "; Attempt=" + _playerHandcuffPairedAnimationAttempts
                            + "; MaximumAttempts="
                            + PlayerHandcuffMaximumPairedAnimationAttempts);
                    }
                    else
                    {
                        LogRuntime("POLICE_DISPATCH_ARREST_PAIR_START_FAILED",
                            "Ped=" + target.Handle + "; Player=" + player.Handle
                            + "; Posture=" + posture
                            + "; Dictionary=" + dictionary
                            + "; Attempts=" + _playerHandcuffPairedAnimationAttempts);
                    }
                    return false;
                }

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
                _playerHandcuffAnimationLoadUntil = DateTime.MinValue;
                _playerHandcuffSceneId = sceneId;
                _playerHandcuffAnimationVerifyAt = now.AddMilliseconds(500);
                _playerHandcuffAnimationVerified = false;
                _playerHandcuffSceneStartedAt = now;
                _playerHandcuffSceneLastProgressAt = now;
                _playerHandcuffSceneClipInterruptedAt = DateTime.MinValue;
                _playerHandcuffSceneLastPhase = -1f;
                _playerHandcuffSceneHighestPhase = 0f;
                _playerHandcuffSceneProgressConfirmed = false;
                _playerHandcuffSceneCompleted = false;
                _playerHandcuffOfficerStartPosition = player.Position;
                _playerHandcuffSuspectStartPosition = target.Position;
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
                + "; OfficerStart=" + player.Position.X.ToString("0.00") + ","
                    + player.Position.Y.ToString("0.00") + ","
                    + player.Position.Z.ToString("0.00")
                + "; SuspectStart=" + target.Position.X.ToString("0.00") + ","
                    + target.Position.Y.ToString("0.00") + ","
                    + target.Position.Z.ToString("0.00")
                + "; OfficerHeading=" + player.Heading.ToString("0.0")
                + "; ScenePhase=" + GetScenePhaseText(sceneId)
                + "; Downed=" + groundedArrestTask
                + "; Kneeling=" + kneelingArrestTask
                + "; HoldMilliseconds=" + handcuffDuration);
            if (_incident != null && _incident.PlayerHandcuffInProgress
                && _incident.PlayerHandcuffTargetHandle == target.Handle)
            {
                _incident.PlayerHandcuffAnimationStarted = true;
                if (!TryApplyDispatchCustodyDamageProtection(target, _incident))
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        target, true, true, true, true, true, true, true, true);
            }
            return true;
        }

        private static string GetScenePhaseText(int sceneId)
        {
            float phase;
            return LSImmersiveDictionaryAnimation.TryGetSynchronizedScenePhase(
                sceneId, out phase) ? phase.ToString("0.000") : "unavailable";
        }

        private bool TryValidatePlayerHandcuffSceneSpace(
            Ped player,
            Ped target,
            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture,
            out string reason)
        {
            reason = string.Empty;
            if (player == null || !player.Exists() || player.IsDead
                || target == null || !target.Exists() || target.IsDead
                || player.Handle == target.Handle
                || player.IsInVehicle() || target.IsInVehicle())
            {
                reason = "An arrest actor is unavailable or inside a vehicle.";
                return false;
            }

            Vector3 playerPosition = player.Position;
            Vector3 targetPosition = target.Position;
            if (playerPosition == Vector3.Zero || targetPosition == Vector3.Zero
                || float.IsNaN(playerPosition.X) || float.IsInfinity(playerPosition.X)
                || float.IsNaN(targetPosition.X) || float.IsInfinity(targetPosition.X))
            {
                reason = "An arrest actor position is invalid.";
                return false;
            }

            float actorDistance = playerPosition.DistanceTo(targetPosition);
            float maximumVerticalSeparation =
                posture == LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded
                    ? 1.75f : 0.9f;
            if (actorDistance > SynchronizedArrestMaximumDistance)
            {
                reason = "Move within paired-arrest reach; the suspect remains arrestable.";
                return false;
            }
            if (Math.Abs(playerPosition.Z - targetPosition.Z)
                > maximumVerticalSeparation)
            {
                reason = "The actors are on different levels; move to the suspect's level before arresting.";
                return false;
            }

            try
            {
                if (Function.Call<bool>(Hash.IS_ENTITY_IN_AIR, player)
                    || (posture != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded
                        && Function.Call<bool>(Hash.IS_ENTITY_IN_AIR, target)))
                {
                    reason = "One arrest actor is still falling; wait until both are physically settled.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = "GTA could not confirm stable arrest actor positions: "
                    + ex.GetType().Name + ".";
                return false;
            }

            Vector3 sceneCenter = (playerPosition + targetPosition) * 0.5f;
            try
            {
                Vehicle[] nearbyVehicles = World.GetNearbyVehicles(sceneCenter, 3.0f);
                if (nearbyVehicles == null)
                {
                    reason = "Nearby vehicle clearance could not be confirmed.";
                    return false;
                }
                foreach (Vehicle vehicle in nearbyVehicles)
                {
                    if (vehicle == null || !vehicle.Exists())
                        continue;
                    float playerGap = vehicle.Position.DistanceTo(playerPosition);
                    float targetGap = vehicle.Position.DistanceTo(targetPosition);
                    if (playerGap < 1.65f || targetGap < 1.65f)
                    {
                        reason = "A vehicle overlaps the physical arrest formation.";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "Nearby vehicle clearance query failed: " + ex.GetType().Name + ".";
                return false;
            }

            Vector3 horizontalDirection = targetPosition - playerPosition;
            horizontalDirection.Z = 0f;
            float horizontalLength = (float)Math.Sqrt(
                horizontalDirection.X * horizontalDirection.X
                + horizontalDirection.Y * horizontalDirection.Y);
            if (horizontalLength < 0.1f)
            {
                reason = "Move slightly around the suspect so the paired arrest has a clear approach lane.";
                return false;
            }
            horizontalDirection = new Vector3(
                horizontalDirection.X / horizontalLength,
                horizontalDirection.Y / horizontalLength,
                0f);
            Vector3 lateralDirection = new Vector3(
                -horizontalDirection.Y, horizontalDirection.X, 0f);
            const float actorInset = 0.45f;
            const float clearanceRadius = 0.22f;
            float clearLaneLength = actorDistance - actorInset * 2f;
            Vector3[] lateralOffsets =
            {
                lateralDirection * -0.38f,
                Vector3.Zero,
                lateralDirection * 0.38f
            };
            float[] clearanceHeights = { 0.35f, 0.9f, 1.45f };
            try
            {
                if (clearLaneLength > 0f)
                {
                    bool clearLaneFound = false;
                    string firstBlockedLaneEvidence = string.Empty;
                    foreach (Vector3 lateralOffset in lateralOffsets)
                    {
                        bool laneClear = true;
                        foreach (float height in clearanceHeights)
                        {
                            Vector3 heightOffset = new Vector3(0f, 0f, height);
                            Vector3 lineStart = playerPosition
                                + horizontalDirection * actorInset
                                + lateralOffset + heightOffset;
                            Vector3 lineEnd = targetPosition
                                - horizontalDirection * actorInset
                                + lateralOffset + heightOffset;
                            RaycastResult obstruction = World.RaycastCapsule(
                                lineStart, lineEnd, clearanceRadius,
                                IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Glass,
                                player);
                            if (!obstruction.DidHit)
                                continue;

                            laneClear = false;
                            if (string.IsNullOrEmpty(firstBlockedLaneEvidence))
                            {
                                Entity blocker = obstruction.HitEntity;
                                firstBlockedLaneEvidence = blocker != null && blocker.Exists()
                                    ? "; BlockingEntity=" + blocker.Handle
                                        + "; BlockingModelHash=" + blocker.Model.Hash
                                        + "; BlockingPosition=" + blocker.Position
                                    : "; BlockingEntity=MapGeometry"
                                        + "; HitPosition=" + obstruction.HitPosition;
                                firstBlockedLaneEvidence += "; ClearanceHeight="
                                    + height.ToString("0.00", CultureInfo.InvariantCulture)
                                    + "; LateralOffset=" + lateralOffset;
                            }
                            break;
                        }

                        // These offsets are alternate physical pair formations.
                        // A collision in one lane must not reject another lane
                        // that has complete clearance at every sampled height.
                        if (laneClear)
                        {
                            clearLaneFound = true;
                            break;
                        }
                    }

                    if (!clearLaneFound)
                    {
                        reason = "All paired-arrest approach lanes are obstructed; reposition both actors before trying again."
                            + firstBlockedLaneEvidence
                            + "; TestedLanes=" + lateralOffsets.Length
                            + "; TestedHeightsPerLane=" + clearanceHeights.Length
                            + "; RaycastFlags=Map,Objects,Glass; IgnoredEntity=Player";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "Paired arrest capsule clearance query failed: "
                    + ex.GetType().Name + ".";
                return false;
            }

            return true;
        }

        private bool MaintainPlayerHandcuffScene(DateTime now, Ped player, Ped target)
        {
            if (_playerHandcuffSceneId < 0)
                return true;

            string failure = null;
            if (player == null || !player.Exists() || player.IsDead
                || target == null || !target.Exists() || target.IsDead)
                failure = "ARREST_ACTOR_UNAVAILABLE";
            else if (player.Position.DistanceTo(_playerHandcuffOfficerStartPosition)
                        > PlayerHandcuffMaximumSceneDisplacement
                || target.Position.DistanceTo(_playerHandcuffSuspectStartPosition)
                        > PlayerHandcuffMaximumSceneDisplacement)
                failure = "ARREST_ACTOR_DISPLACED_FROM_AUTHORED_FORMATION";
            else if (_playerHandcuffTargetPosture
                != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded)
            {
                try
                {
                    if (Function.Call<bool>(Hash.IS_PED_RAGDOLL, player)
                        || Function.Call<bool>(Hash.IS_PED_RAGDOLL, target)
                        || Function.Call<bool>(Hash.IS_PED_IN_WRITHE, target))
                        failure = "STANDING_ARREST_ACTOR_FELL_OR_ENTERED_WRITHE";
                }
                catch
                {
                    failure = "ARREST_ACTOR_POSTURE_COULD_NOT_BE_CONFIRMED";
                }
            }

            if (failure == null && _playerHandcuffSceneCompleted)
                return true;

            float phase = -1f;
            bool phaseAvailable = failure == null
                && LSImmersiveDictionaryAnimation.TryGetSynchronizedScenePhase(
                    _playerHandcuffSceneId, out phase);
            bool officerPlaying = failure == null
                && LSImmersiveDictionaryAnimation.IsPlaying(
                    player, _playerHandcuffDictionary, _playerHandcuffOfficerClip);
            bool suspectPlaying = failure == null
                && LSImmersiveDictionaryAnimation.IsPlaying(
                    target, _playerHandcuffDictionary, _playerHandcuffSuspectClip);

            if (failure == null && now >= _playerHandcuffAnimationVerifyAt
                && !_playerHandcuffAnimationVerified)
            {
                if (officerPlaying && suspectPlaying && phaseAvailable)
                {
                    _playerHandcuffAnimationVerified = true;
                    LogRuntime("POLICE_DISPATCH_ARREST_PAIR_PLAYBACK_CONFIRMED",
                        "Incident=" + _incident.Id + "; Ped=" + target.Handle
                        + "; Player=" + player.Handle
                        + "; Dictionary=" + _playerHandcuffDictionary
                        + "; OfficerClip=" + _playerHandcuffOfficerClip
                        + "; SuspectClip=" + _playerHandcuffSuspectClip
                        + "; Scene=" + _playerHandcuffSceneId
                        + "; ScenePhase=" + phase.ToString("0.000")
                        + "; ContinuousMonitoring=true"
                        + "; SceneOrigin=" + _playerHandcuffSceneOrigin.X.ToString("0.00") + ","
                            + _playerHandcuffSceneOrigin.Y.ToString("0.00") + ","
                            + _playerHandcuffSceneOrigin.Z.ToString("0.00")
                        + "; SceneHeading=" + _playerHandcuffSceneHeading.ToString("0.0"));
                }
                else if (now >= _playerHandcuffSceneStartedAt.AddMilliseconds(
                    PlayerHandcuffSceneClipLossGraceMilliseconds))
                    failure = "PAIRED_ARREST_DID_NOT_START_BOTH_CLIPS";
            }

            if (failure == null && phaseAvailable)
            {
                if (_playerHandcuffSceneLastPhase >= 0f
                    && phase + 0.05f < _playerHandcuffSceneLastPhase
                    && _playerHandcuffSceneHighestPhase < PlayerHandcuffCommitPhase)
                    failure = "SYNCHRONIZED_SCENE_PHASE_RESET_BEFORE_COMPLETION";
                else
                {
                    if (phase > _playerHandcuffSceneHighestPhase + 0.01f)
                    {
                        _playerHandcuffSceneHighestPhase = phase;
                        _playerHandcuffSceneLastProgressAt = now;
                    }
                    _playerHandcuffSceneLastPhase = phase;
                    if (phase >= PlayerHandcuffMinimumProgressPhase)
                        _playerHandcuffSceneProgressConfirmed = true;
                    if (phase >= PlayerHandcuffCommitPhase)
                    {
                        _playerHandcuffSceneCompleted = true;
                        _playerHandcuffSceneProgressConfirmed = true;
                        LogRuntime("POLICE_DISPATCH_ARREST_PAIR_SCENE_COMPLETED",
                            "Incident=" + _incident.Id + "; Ped=" + target.Handle
                            + "; Player=" + player.Handle
                            + "; Scene=" + _playerHandcuffSceneId
                            + "; HighestPhase=" + _playerHandcuffSceneHighestPhase.ToString("0.000")
                            + "; BothClipsPlaying=" + (officerPlaying && suspectPlaying)
                            + "; CommitAllowed=true");
                    }
                }
            }

            if (failure == null && !_playerHandcuffSceneCompleted
                && (!officerPlaying || !suspectPlaying))
            {
                if (_playerHandcuffSceneClipInterruptedAt == DateTime.MinValue)
                    _playerHandcuffSceneClipInterruptedAt = now;
                else if (now >= _playerHandcuffSceneClipInterruptedAt.AddMilliseconds(
                    PlayerHandcuffSceneClipLossGraceMilliseconds))
                    failure = !officerPlaying && !suspectPlaying
                        ? "BOTH_PAIRED_CLIPS_STOPPED_BEFORE_SCENE_COMPLETION"
                        : "ONE_PAIRED_CLIP_STOPPED_BEFORE_SCENE_COMPLETION";
            }
            else if (officerPlaying && suspectPlaying)
                _playerHandcuffSceneClipInterruptedAt = DateTime.MinValue;

            if (failure == null && !_playerHandcuffSceneCompleted
                && _playerHandcuffSceneLastProgressAt != DateTime.MinValue
                && now >= _playerHandcuffSceneLastProgressAt.AddMilliseconds(
                    PlayerHandcuffSceneProgressStallMilliseconds))
                failure = phaseAvailable
                    ? "SYNCHRONIZED_SCENE_MADE_NO_MEANINGFUL_PROGRESS"
                    : "SYNCHRONIZED_SCENE_PHASE_BECAME_UNAVAILABLE";

            if (failure == null)
                return true;

            string abortMessage = AbortPlayerHandcuffAnimation(
                player, target, failure);
            Notify(abortMessage);
            return false;
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
                int failedTargetHandle = _incident.PlayerHandcuffTargetHandle;
                try { if (player != null && player.Exists()) player.Task.ClearAll(); }
                catch { }
                try { if (target != null && target.Exists()) target.Task.ClearAll(); }
                catch { }
                ClearPlayerHandcuffState();
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ABORTED",
                    "Ped=" + failedTargetHandle
                    + "; Reason=" + (target == null
                        ? "PRISONER_REFERENCE_INVALID" : "PRISONER_DIED")
                    + "; PlayerTaskCleared=true; SceneStateCleared=true"
                    + "; CustodyAdvanced=false");
                Notify("The arrest was interrupted before physical cuffs were confirmed. The incident remains active; use its status to resolve the suspect.");
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
                if (_incident.PlayerHandcuffAnimationStarted
                    && !TryApplyDispatchCustodyDamageProtection(target, _incident))
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        target, true, true, true, true, true, true, true, true);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_MAINTENANCE_FAILED", ex);
            }

            if (player == null || !player.Exists()
                || player.Position.DistanceTo(target.Position) > ArrestRadius + 2f)
            {
                string abortMessage = AbortPlayerHandcuffAnimation(
                    player, target, "PLAYER_LEFT_INTERACTION_RADIUS");
                Notify(abortMessage);
                return;
            }

            if (_playerHandcuffSceneId >= 0 && !_playerHandcuffSceneCompleted
                && !MaintainPlayerHandcuffScene(now, player, target))
                return;

            if (_playerHandcuffWaitingForAnimation)
            {
                string waitingDictionary = string.IsNullOrWhiteSpace(
                    _playerHandcuffWaitingDictionary)
                    ? "mp_arrest_paired"
                    : _playerHandcuffWaitingDictionary;
                if (now >= _playerHandcuffAnimationLoadUntil)
                {
                    string abortMessage = AbortPlayerHandcuffAnimation(
                        player, target, "PAIRED_ANIMATION_PREPARATION_TIMED_OUT");
                    Notify(abortMessage);
                    return;
                }
                if (player.Position.DistanceTo(target.Position)
                    > SynchronizedArrestMaximumDistance)
                {
                    return;
                }

                bool animationLoaded = LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                    waitingDictionary);
                if (animationLoaded)
                {
                    StartPlayerHandcuffAnimation(
                        player, target, now);
                    if (!string.IsNullOrWhiteSpace(
                        _playerHandcuffSceneSpaceFailure))
                    {
                        string message = ReleasePlayerHandcuffForSpaceRetry();
                        Notify(message);
                        return;
                    }
                    if (!_playerHandcuffWaitingForAnimation
                        && !_playerHandcuffPairedAnimationStarted)
                    {
                        string abortMessage = AbortPlayerHandcuffAnimation(
                            player, target, "PAIRED_ANIMATION_RETRY_START_REJECTED");
                        Notify(abortMessage);
                    }
                    return;
                }

                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ANIMATION_WAITING",
                    "Ped=" + target.Handle + "; Player=" + player.Handle
                    + "; Dictionary=" + waitingDictionary
                    + "; Reason=DictionaryStillStreaming");
                return;
            }

            if (now < _playerHandcuffHoldUntil)
                return;

            if (_playerHandcuffSceneId >= 0 && !_playerHandcuffSceneCompleted)
                return;

            if (_playerHandcuffTargetPosture
                    != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling
                && (!_playerHandcuffAnimationVerified
                    || !_playerHandcuffSceneProgressConfirmed
                    || !_playerHandcuffSceneCompleted))
            {
                string abortMessage = AbortPlayerHandcuffAnimation(
                    player, target, "PAIRED_ANIMATION_PROGRESS_NOT_CONFIRMED_BEFORE_COMMIT");
                Notify(abortMessage);
                return;
            }

            LogRuntime("POLICE_DISPATCH_ARREST_ANIMATION_COMMIT_POINT",
                "Incident=" + _incident.Id + "; Ped=" + target.Handle
                + "; Player=" + player.Handle
                + "; Scene=" + (_playerHandcuffSceneId < 0
                    ? "none" : _playerHandcuffSceneId.ToString())
                + "; ScenePhase=" + GetScenePhaseText(_playerHandcuffSceneId)
                + "; CuffedBeforeCommit=" + IsPedPhysicallyCuffed(target)
                + "; PhysicalHoldElapsed=true"
                + "; PairedSceneCompleted=" + _playerHandcuffSceneCompleted
                + "; SceneProgressConfirmed=" + _playerHandcuffSceneProgressConfirmed);

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
                            player, target, now);
                        if (!retryStarted && _playerHandcuffWaitingForAnimation)
                            return;
                        if (!retryStarted
                            && _playerHandcuffTargetPosture
                                != LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling)
                        {
                            string abortMessage = AbortPlayerHandcuffAnimation(
                                player, target, "PHYSICAL_CUFF_RETRY_PAIR_START_REJECTED");
                            Notify(abortMessage);
                            return;
                        }
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
                if (!TryApplyDispatchCustodyDamageProtection(target, _incident))
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        target, false, false, false, true, false, false, false, false);
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                AddArrestedSuspect(_incident, target);
                ClearPlayerHandcuffState();
                ClearSecuredPoseTracking(target.Handle);
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
                        bool physicallyArrested = IsArrested(_incident, handcuffTarget)
                            && IsPedPhysicallyCuffed(handcuffTarget);
                        if (physicallyArrested)
                        {
                            if (!TryApplyDispatchCustodyDamageProtection(
                                handcuffTarget, _incident))
                                Function.Call(Hash.SET_ENTITY_PROOFS,
                                    handcuffTarget, true, false, false,
                                    true, true, false, false, false);
                        }
                        else if (!TryRestoreDispatchCustodyDamageProtection(handcuffTarget))
                            Function.Call(Hash.SET_ENTITY_PROOFS,
                                handcuffTarget, false, false, false,
                                false, false, false, false, false);
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
            _playerHandcuffSceneStartedAt = DateTime.MinValue;
            _playerHandcuffSceneLastProgressAt = DateTime.MinValue;
            _playerHandcuffSceneClipInterruptedAt = DateTime.MinValue;
            _playerHandcuffSceneLastPhase = -1f;
            _playerHandcuffSceneHighestPhase = 0f;
            _playerHandcuffSceneProgressConfirmed = false;
            _playerHandcuffSceneCompleted = false;
            _playerHandcuffOfficerStartPosition = Vector3.Zero;
            _playerHandcuffSuspectStartPosition = Vector3.Zero;
            _playerHandcuffPreserveTargetPosture = false;
            _playerHandcuffTargetPosture =
                LSImmersiveDictionaryAnimation.ArrestTargetPosture.Invalid;
            _playerHandcuffPairedAnimationAttempts = 0;
            _playerHandcuffDictionary = null;
            _playerHandcuffOfficerClip = null;
            _playerHandcuffSuspectClip = null;
            _playerHandcuffSceneOrigin = Vector3.Zero;
            _playerHandcuffSceneHeading = 0f;
            _playerHandcuffSceneSpaceFailure = null;
            if (_incident == null)
                return;
            _incident.PlayerHandcuffInProgress = false;
            _incident.PlayerHandcuffAnimationStarted = false;
            _incident.PlayerHandcuffTargetHandle = 0;
            _incident.PlayerHandcuffStartedAt = DateTime.MinValue;
        }

        private string ReleasePlayerHandcuffForSpaceRetry()
        {
            string failure = _playerHandcuffSceneSpaceFailure;
            _playerHandcuffSceneSpaceFailure = null;
            ClearPlayerHandcuffState();
            return (string.IsNullOrWhiteSpace(failure)
                    ? "The paired arrest needs more room."
                    : failure)
                + " The suspect's current posture and task were retained. Reposition to a clear area and press E again.";
        }

        private void ReportPlayerHandcuffSpacePreflight(
            Ped player,
            Ped target,
            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture,
            string failure)
        {
            string incidentId = _incident == null ? "none" : _incident.Id;
            bool ready = string.IsNullOrWhiteSpace(failure);
            string state = (ready ? "Ready" : "Rejected")
                + ":" + incidentId
                + ":" + (target == null || !target.Exists()
                    ? 0 : target.Handle)
                + ":" + posture
                + ":" + (failure ?? string.Empty);
            LogRuntimeTransition(
                "POLICE_DISPATCH_ARREST_PAIR_SPACE_PREFLIGHT",
                state,
                "Incident=" + incidentId
                + "; Ped=" + (target == null || !target.Exists()
                    ? 0 : target.Handle)
                + "; Player=" + (player == null || !player.Exists()
                    ? 0 : player.Handle)
                + "; Posture=" + posture
                + "; Result=" + (ready ? "Ready" : "Rejected")
                + "; Reason=" + (failure ?? string.Empty)
                + "; ComplianceRetained=" + !ready
                + "; CurrentPostureRetained=" + !ready
                + "; ActorTaskCleared=false; CustodyAdvanced=false");
        }

        private string AbortPlayerHandcuffAnimation(
            Ped player,
            Ped target,
            string reason)
        {
            int targetHandle = target != null && target.Exists()
                ? target.Handle
                : (_incident == null ? 0 : _incident.PlayerHandcuffTargetHandle);
            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture =
                _playerHandcuffTargetPosture;
            int sceneId = _playerHandcuffSceneId;

            try { if (player != null && player.Exists()) player.Task.ClearAll(); }
            catch { }
            try { if (target != null && target.Exists()) target.Task.ClearAll(); }
            catch { }
            ClearPlayerHandcuffState();

            string recovery = RestorePlayerHandcuffTargetPosture(
                player, target, posture);
            LogRuntime("POLICE_DISPATCH_ARREST_ANIMATION_ABORTED",
                "Ped=" + targetHandle
                + "; Player=" + (player == null || !player.Exists()
                    ? 0 : player.Handle)
                + "; Reason=" + reason
                + "; OriginalPosture=" + posture
                + "; Scene=" + (sceneId < 0 ? "none" : sceneId.ToString())
                + "; Recovery=" + recovery
                + "; CustodyAdvanced=false");
            return "The paired arrest could not be confirmed. The suspect remains available in the original posture; move close and press E to retry.";
        }

        private string RestorePlayerHandcuffTargetPosture(
            Ped player,
            Ped target,
            LSImmersiveDictionaryAnimation.ArrestTargetPosture posture)
        {
            if (target == null || !target.Exists() || target.IsDead)
                return "TargetUnavailable";

            try
            {
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                switch (posture)
                {
                    case LSImmersiveDictionaryAnimation.ArrestTargetPosture.Grounded:
                    {
                        LSPDSuspectInjuryState injuryState;
                        if (_suspectInjuries.TryGetValue(target.Handle, out injuryState)
                            && injuryState.Wounded && !injuryState.Fatal)
                        {
                            injuryState.WritheTaskStarted = false;
                            MaintainDispatchSuspectInjury(
                                target, DateTime.UtcNow, player);
                            return "WoundedWritheReissued";
                        }
                        Function.Call(Hash.SET_PED_TO_RAGDOLL,
                            target, 3500, 3500, 0, false, false, false);
                        return "GroundedRagdoll";
                    }

                    case LSImmersiveDictionaryAnimation.ArrestTargetPosture.Kneeling:
                    {
                        float kneelingDuration;
                        if (LSImmersiveDictionaryAnimation.TryPlay(
                            target,
                            "random@arrests",
                            "kneeling_arrest_idle",
                            2.0f,
                            -2.0f,
                            -1,
                            1,
                            1.0f,
                            out kneelingDuration))
                            return "KneelingArrestIdle";
                        return "KneelingPoseRetainedWithoutAnimation";
                    }

                    case LSImmersiveDictionaryAnimation.ArrestTargetPosture.Standing:
                    default:
                        target.Task.HandsUp(15000);
                        return "HandsUp";
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_ARREST_POSTURE_RECOVERY_FAILED", ex);
                return "RecoveryFailed:" + ex.GetType().Name;
            }
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
                if (!TryApplyDispatchCustodyDamageProtection(suspect, incident))
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
            incident.PlayerCommandedSecureSuspectHandles.Remove(suspect.Handle);
            EndBackupArrestOperation(incident, suspect.Handle, false);
            ClearSecuredPoseTracking(suspect.Handle);
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

        private void RestoreBackupArrestTarget(Ped suspect, bool secured)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead)
                return;
            try
            {
                if (secured)
                {
                    if (!TryApplyDispatchCustodyDamageProtection(suspect, _incident))
                        Function.Call(Hash.SET_ENTITY_PROOFS,
                            suspect, false, false, false, true, false, false, false, false);
                }
                else if (!TryRestoreDispatchCustodyDamageProtection(suspect))
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        suspect, false, false, false, false, false, false, false, false);
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

        private string OrderSuspectToComply(
            Ped suspect,
            Ped player,
            bool groupCommand,
            bool playerCommand)
        {
            try
            {
                suspect.Task.ClearAll();
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                suspect.Task.HandsUp(10000);
                suspect.Task.LookAt(player, 2500);
                AddCompliantSuspect(_incident, suspect);
                if (playerCommand)
                    MarkPlayerCommandedSecure(_incident, suspect);
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

        private static bool IsPlayerCommandedSecure(
            LSPDDispatchEvent incident,
            Ped suspect)
        {
            return incident != null && suspect != null && suspect.Exists()
                && !suspect.IsDead && suspect.Health > 0
                && incident.PlayerCommandedSecureSuspectHandles != null
                && incident.PlayerCommandedSecureSuspectHandles.Contains(
                    suspect.Handle);
        }

        private static void MarkPlayerCommandedSecure(
            LSPDDispatchEvent incident,
            Ped suspect)
        {
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || suspect.Health <= 0
                || incident.PlayerCommandedSecureSuspectHandles == null)
                return;
            incident.PlayerCommandedSecureSuspectHandles.Add(suspect.Handle);
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
                    ClearSecuredPoseTracking(handle);
                }
            }

            foreach (Ped suspect in TrackedSuspects(_incident))
            {
                if (suspect == null)
                    continue;
                if (_incident.ConvoyCustodySuspectHandles.Contains(suspect.Handle))
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
            int managedMaximumHealth = Math.Max(
                Math.Max(originalHealth, originalMaximumHealth),
                Math.Max(10000, _injurySettings.ControlledMaximumHealth));
            LSPDSuspectInjuryState state = new LSPDSuspectInjuryState
            {
                Actor = suspect,
                OriginalHealth = originalHealth,
                OriginalMaximumHealth = originalMaximumHealth,
                LastHealth = originalHealth,
                LastMaximumHealth = originalMaximumHealth,
                // Dispatch uses a separate logical injury counter. A managed
                // physical reserve prevents GTA from entering a terminal Ped
                // state before that policy can evaluate confirmed hits.
                ManagedMaximumHealth = managedMaximumHealth,
                ArrestableWoundedHealthFloor = Math.Min(
                    _injurySettings.ArrestableWoundedHealthFloor,
                    Math.Max(1, managedMaximumHealth / 4)),
                DispatchWoundedHealthReserve = Math.Max(
                    Math.Min(_injurySettings.ArrestableWoundedHealthFloor,
                        Math.Max(1, managedMaximumHealth / 4)),
                    Math.Max(1, (managedMaximumHealth + 1) / 2)),
                OccurrenceId = incident != null ? incident.Id
                    : (_incident == null ? string.Empty : _incident.Id),
                OriginalCanSufferCriticalHits = suspect.CanSufferCriticalHits,
                OriginalCanSwitchWeapons = suspect.CanSwitchWeapons,
                LastKnownAlivePosition = suspect.Position,
                LastKnownAliveHeading = suspect.Heading
            };
            state.LastKnownAlivePositionSupported = IsSupportedDispatchRecoveryPosition(
                suspect, state.LastKnownAlivePosition);
            try
            {
                state.ManagedRelationshipGroupHash = Function.Call<int>(
                    Hash.GET_PED_RELATIONSHIP_GROUP_HASH, suspect);
            }
            catch { state.ManagedRelationshipGroupHash = 0; }
            state.OriginalDamageProofsCaptured = TryCaptureDispatchDamageProofs(
                suspect, state);
            bool physicalReserveApplied = false;
            try
            {
                if (suspect.MaxHealth != state.ManagedMaximumHealth)
                    suspect.MaxHealth = state.ManagedMaximumHealth;
                if (suspect.Health < state.ManagedMaximumHealth)
                    suspect.Health = state.ManagedMaximumHealth;
                physicalReserveApplied = suspect.MaxHealth >= state.ManagedMaximumHealth
                    && suspect.Health > 0;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_PHYSICAL_HEALTH_RESERVE_FAILED", ex);
            }
            try
            {
                // The logical vital-hit policy, not GTA's one-hit critical
                // shortcut, decides whether Police Player damage is fatal.
                // Regular bullet and melee damage remain enabled.
                suspect.CanSufferCriticalHits = false;
                state.CriticalHitGuardActive = !suspect.CanSufferCriticalHits;
                if (!state.CriticalHitGuardActive)
                    throw new InvalidOperationException(
                        "GTA did not retain the Dispatch critical-hit guard.");
            }
            catch (Exception ex)
            {
                state.CriticalHitGuardActive = false;
                LogException("POLICE_DISPATCH_CRITICAL_HIT_CONTROL_FAILED", ex);
            }
            try
            {
                state.OriginalSelectedWeaponHash = Function.Call<int>(
                    Hash.GET_SELECTED_PED_WEAPON, suspect);
                state.LastHealth = Math.Max(1, suspect.Health);
                state.LastMaximumHealth = Math.Max(1, suspect.MaxHealth);
                state.LastKnownAlivePosition = suspect.Position;
                state.LastKnownAliveHeading = suspect.Heading;
                state.DamageAttributionBaselineAvailable =
                    ClearDispatchDamageAttribution(
                        suspect, out state.DamageAttributionBaselineStatus);
            }
            catch (Exception ex)
            {
                state.DamageAttributionBaselineAvailable = false;
                state.DamageAttributionBaselineStatus =
                    "CaptureFailed=" + ex.GetType().Name;
                LogException("POLICE_DISPATCH_DAMAGE_BASELINE_CAPTURE_FAILED", ex);
            }
            _suspectInjuries[suspect.Handle] = state;
            LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_CONTROLLED",
                "Session=" + (_log == null ? string.Empty : _log.SessionId)
                + "; Incident=" + state.OccurrenceId
                + "; RuntimeOwner=Dispatch; Ped=" + suspect.Handle
                + "; HealthBefore=" + originalHealth
                + "; ManagedHealth=" + state.LastHealth
                + "; ManagedMaximum=" + state.ManagedMaximumHealth
                + "; WoundedFloor=" + state.ArrestableWoundedHealthFloor
                + "; WoundedHealthReserve=" + state.DispatchWoundedHealthReserve
                + "; CanSufferCriticalHits=" + suspect.CanSufferCriticalHits
                + "; CriticalHitGuardActive=" + state.CriticalHitGuardActive
                + "; CriticalHitGuardReassertions=" + state.CriticalHitGuardReassertions
                + "; OriginalDamageProofsCaptured=" + state.OriginalDamageProofsCaptured
                + "; FullDamageProof=false"
                + "; PhysicalReserveApplied=" + physicalReserveApplied
                + "; HealthRaisedForManagedReserve=" + physicalReserveApplied
                + "; MaximumHealthRaisedForManagedReserve=" + physicalReserveApplied
                + "; RelationshipGroup=" + state.ManagedRelationshipGroupHash
                + "; DamageAttributionBaselineAvailable="
                + state.DamageAttributionBaselineAvailable
                + "; DamageAttributionBaselineStatus="
                + state.DamageAttributionBaselineStatus
                + "; ReusedPed=true");
        }

        private static bool TryCaptureDispatchDamageProofs(
            Ped suspect,
            LSPDSuspectInjuryState state)
        {
            if (suspect == null || !suspect.Exists() || state == null)
                return false;
            try
            {
                using (OutputArgument bullet = new OutputArgument())
                using (OutputArgument fire = new OutputArgument())
                using (OutputArgument explosion = new OutputArgument())
                using (OutputArgument collision = new OutputArgument())
                using (OutputArgument melee = new OutputArgument())
                using (OutputArgument steam = new OutputArgument())
                using (OutputArgument unknown = new OutputArgument())
                using (OutputArgument drown = new OutputArgument())
                {
                    Function.Call((Hash)0xBE8CD9BE829BBEBFUL,
                        suspect, bullet, fire, explosion, collision,
                        melee, steam, unknown, drown);
                    state.OriginalBulletProof = bullet.GetResult<bool>();
                    state.OriginalFireProof = fire.GetResult<bool>();
                    state.OriginalExplosionProof = explosion.GetResult<bool>();
                    state.OriginalCollisionProof = collision.GetResult<bool>();
                    state.OriginalMeleeProof = melee.GetResult<bool>();
                    state.OriginalSteamProof = steam.GetResult<bool>();
                    state.OriginalUnknownDamageProof = unknown.GetResult<bool>();
                    state.OriginalDrownProof = drown.GetResult<bool>();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool ApplyDispatchCustodyDamageProtection(
            Ped suspect,
            LSPDSuspectInjuryState state)
        {
            if (suspect == null || !suspect.Exists() || state == null)
                return false;
            if (!state.OriginalDamageProofsCaptured)
                state.OriginalDamageProofsCaptured =
                    TryCaptureDispatchDamageProofs(suspect, state);
            if (!state.OriginalDamageProofsCaptured)
                return false;
            try
            {
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    suspect, true, true, true, true, true, true, true, true);
                suspect.CanSufferCriticalHits = false;
                state.CustodyDamageProtectionActive = true;
                state.CustodyProtectionCommitted = true;
                state.CriticalHitGuardActive = !suspect.CanSufferCriticalHits;
                return state.CriticalHitGuardActive;
            }
            catch
            {
                return false;
            }
        }

        internal static bool RestoreDispatchCustodyDamageProtection(
            Ped suspect,
            LSPDSuspectInjuryState state)
        {
            if (suspect == null || !suspect.Exists() || state == null
                || !state.CustodyDamageProtectionActive
                || !state.OriginalDamageProofsCaptured)
                return false;
            try
            {
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    suspect,
                    state.OriginalBulletProof,
                    state.OriginalFireProof,
                    state.OriginalExplosionProof,
                    state.OriginalCollisionProof,
                    state.OriginalMeleeProof,
                    state.OriginalSteamProof,
                    state.OriginalUnknownDamageProof,
                    state.OriginalDrownProof);
                suspect.CanSufferCriticalHits = state.OriginalCanSufferCriticalHits;
                state.CustodyDamageProtectionActive = false;
                state.CustodyProtectionCommitted = false;
                state.CriticalHitGuardActive = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryApplyDispatchCustodyDamageProtection(
            Ped suspect,
            LSPDDispatchEvent incident = null)
        {
            if (suspect == null || !suspect.Exists())
                return false;
            CaptureDispatchSuspectInjuryState(suspect, incident);
            LSPDSuspectInjuryState state;
            return _suspectInjuries.TryGetValue(suspect.Handle, out state)
                && ApplyDispatchCustodyDamageProtection(suspect, state);
        }

        private bool TryRestoreDispatchCustodyDamageProtection(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return false;
            LSPDSuspectInjuryState state;
            return _suspectInjuries.TryGetValue(suspect.Handle, out state)
                && RestoreDispatchCustodyDamageProtection(suspect, state);
        }

        private bool IsDispatchVehicleFlightDriver(
            Ped suspect,
            out Vehicle vehicle)
        {
            vehicle = null;
            if (_incident == null || !_incident.OwnedByDispatch
                || _incident.HasConvoyCustodyHandoff
                || suspect == null || !suspect.Exists() || suspect.IsDead
                || !IsSuspectResponse(_incident, suspect, "flee"))
                return false;

            try
            {
                if (!suspect.IsInVehicle())
                    return false;
                Vehicle current = suspect.CurrentVehicle;
                Vehicle assigned = _incident.VehicleForSuspect(suspect);
                Ped driver = current == null || !current.Exists()
                    ? null : current.Driver;
                if (current == null || !current.Exists()
                    || assigned == null || !assigned.Exists()
                    || current.Handle != assigned.Handle
                    || driver == null || !driver.Exists()
                    || driver.Handle != suspect.Handle)
                    return false;
                vehicle = current;
                return true;
            }
            catch
            {
                vehicle = null;
                return false;
            }
        }

        private void MaintainDispatchVehicleFlightCollisionProtection(
            Ped suspect,
            LSPDSuspectInjuryState state)
        {
            if (state == null)
                return;
            Vehicle vehicle = null;
            bool shouldProtect = !state.Fatal
                && IsDispatchVehicleFlightDriver(suspect, out vehicle);
            if (!shouldProtect)
            {
                RestoreDispatchVehicleFlightCollisionProtection(
                    suspect, state, "DriverExitedOrFlightEnded");
                return;
            }

            bool wasActive = state.VehicleFlightCollisionProofGuardActive;
            bool wasErrorLogged = state.VehicleFlightCollisionProofErrorLogged;
            try
            {
                if (!wasActive)
                {
                    state.VehicleFlightOriginalCollisionProof =
                        suspect.IsCollisionProof;
                    state.VehicleFlightCollisionProofGuardActive = true;
                }
                if (!suspect.IsCollisionProof)
                    suspect.IsCollisionProof = true;
                if (!suspect.IsCollisionProof)
                    throw new InvalidOperationException(
                        "GTA did not retain the Dispatch vehicle-flight collision guard.");

                state.VehicleFlightCollisionProofErrorLogged = false;
                if (!wasActive || wasErrorLogged)
                    LogRuntime("POLICE_DISPATCH_PURSUIT_COLLISION_GUARD_ENABLED",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + suspect.Handle
                        + "; Vehicle=" + vehicle.Handle
                        + "; CollisionProofOnly=true; InvincibilitySet=false");
            }
            catch (Exception ex)
            {
                if (!state.VehicleFlightCollisionProofErrorLogged)
                {
                    state.VehicleFlightCollisionProofErrorLogged = true;
                    LogException(
                        "POLICE_DISPATCH_PURSUIT_COLLISION_GUARD_FAILED", ex);
                }
            }
        }

        private void BeginDispatchVehicleFlightProtection(
            Ped suspect,
            LSPDDispatchEvent incident)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.Health <= 0 || incident == null)
                return;

            CaptureDispatchSuspectInjuryState(suspect, incident);
            LSPDSuspectInjuryState state;
            if (_suspectInjuries.TryGetValue(suspect.Handle, out state))
                MaintainDispatchVehicleFlightCollisionProtection(suspect, state);
        }

        private void RestoreDispatchVehicleFlightCollisionProtection(
            Ped suspect,
            LSPDSuspectInjuryState state,
            string reason)
        {
            if (state == null || !state.VehicleFlightCollisionProofGuardActive
                || suspect == null || !suspect.Exists())
                return;
            try
            {
                suspect.IsCollisionProof = state.VehicleFlightOriginalCollisionProof;
                if (suspect.IsCollisionProof
                    != state.VehicleFlightOriginalCollisionProof)
                    throw new InvalidOperationException(
                        "GTA did not restore the Dispatch collision-proof setting.");
                state.VehicleFlightCollisionProofGuardActive = false;
                state.VehicleFlightCollisionProofErrorLogged = false;
                LogRuntime("POLICE_DISPATCH_PURSUIT_COLLISION_GUARD_RESTORED",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                    + "; OriginalCollisionProof="
                    + state.VehicleFlightOriginalCollisionProof
                    + "; Reason=" + (reason ?? "FlightEnded"));
            }
            catch (Exception ex)
            {
                if (!state.VehicleFlightCollisionProofErrorLogged)
                {
                    state.VehicleFlightCollisionProofErrorLogged = true;
                    LogException(
                        "POLICE_DISPATCH_PURSUIT_COLLISION_RESTORE_FAILED", ex);
                }
            }
        }

        private void ObserveDispatchPursuitIntervention(
            Ped suspect,
            Ped player,
            DateTime now,
            LSPDSuspectInjuryState state)
        {
            if (state == null || now < state.NextPursuitInterventionSampleAt)
                return;
            state.NextPursuitInterventionSampleAt = now.AddMilliseconds(
                DispatchPursuitInterventionSampleMilliseconds);

            Vehicle suspectVehicle;
            if (!IsDispatchVehicleFlightDriver(suspect, out suspectVehicle)
                || player == null || !player.Exists() || player.IsDead)
            {
                state.LastPursuitInterventionAt = DateTime.MinValue;
                state.LastPursuitInterventionVehicleHandle = 0;
                state.LastPursuitInterventionSourceHandle = 0;
                state.LastPursuitInterventionCause = null;
                return;
            }

            Vehicle playerVehicle = null;
            try
            {
                if (player.IsInVehicle())
                    playerVehicle = player.CurrentVehicle;
            }
            catch { }
            if (playerVehicle == null || !playerVehicle.Exists()
                || playerVehicle.Handle == suspectVehicle.Handle)
                return;

            bool touching = false;
            try
            {
                touching = Function.Call<bool>(
                    Hash.IS_ENTITY_TOUCHING_ENTITY,
                    suspectVehicle, playerVehicle);
            }
            catch { }
            bool blocking = !touching
                && IsDispatchVehicleBlockingPursuitPath(
                    suspectVehicle, playerVehicle);
            if (!touching && !blocking)
                return;

            string cause = touching
                ? "PlayerVehiclePhysicalContact" : "PlayerVehicleBlockingPath";
            bool changed = state.LastPursuitInterventionVehicleHandle
                    != suspectVehicle.Handle
                || state.LastPursuitInterventionSourceHandle
                    != playerVehicle.Handle
                || !string.Equals(state.LastPursuitInterventionCause,
                    cause, StringComparison.Ordinal);
            state.LastPursuitInterventionAt = now;
            state.LastPursuitInterventionVehicleHandle = suspectVehicle.Handle;
            state.LastPursuitInterventionSourceHandle = playerVehicle.Handle;
            state.LastPursuitInterventionCause = cause;
            if (changed)
            {
                state.LastPursuitInterventionLogAt = now;
                LogRuntime("POLICE_DISPATCH_PURSUIT_INTERVENTION_OBSERVED",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                    + "; Vehicle=" + suspectVehicle.Handle
                    + "; Source=" + playerVehicle.Handle
                    + "; Cause=" + cause
                    + "; Speed=" + suspectVehicle.Speed.ToString(
                        "0.0", CultureInfo.InvariantCulture));
            }
        }

        private static string DescribeDispatchVehicleDamageSources(
            Vehicle suspectVehicle,
            Ped suspectDriver,
            Ped player)
        {
            if (suspectVehicle == null || !suspectVehicle.Exists())
                return "SuspectVehicleUnavailable; Attribution=Unknown";

            const int maximumVehiclesToInspect = 12;
            const int maximumPedsToInspect = 12;
            const int maximumSourcesToReport = 6;
            const float inspectionRadius = 40f;
            List<string> attributedSources = new List<string>();
            HashSet<int> inspectedHandles = new HashSet<int>();
            int vehicleCandidates = 0;
            int pedCandidates = 0;
            bool nativeProbeFailed = false;

            try
            {
                IEnumerable<Vehicle> nearbyVehicles = World.GetNearbyVehicles(
                    suspectVehicle.Position, inspectionRadius)
                    .Where(source => source != null && source.Exists()
                        && source.Handle != suspectVehicle.Handle)
                    .OrderBy(source => source.Position.DistanceTo(suspectVehicle.Position))
                    .Take(maximumVehiclesToInspect);
                foreach (Vehicle source in nearbyVehicles)
                {
                    inspectedHandles.Add(source.Handle);
                    vehicleCandidates++;
                    if (Function.Call<bool>(
                        Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY,
                        suspectVehicle, source))
                        attributedSources.Add("Vehicle=" + source.Handle
                            + ",ModelHash=" + source.Model.Hash
                            + ",Distance=" + source.Position.DistanceTo(
                                suspectVehicle.Position).ToString("0.0", CultureInfo.InvariantCulture));
                }

                Vehicle playerVehicle = player != null && player.Exists()
                    && player.IsInVehicle() ? player.CurrentVehicle : null;
                if (playerVehicle != null && playerVehicle.Exists()
                    && playerVehicle.Handle != suspectVehicle.Handle
                    && inspectedHandles.Add(playerVehicle.Handle))
                {
                    vehicleCandidates++;
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY,
                            suspectVehicle, playerVehicle))
                        attributedSources.Add("PlayerVehicle=" + playerVehicle.Handle
                            + ",ModelHash=" + playerVehicle.Model.Hash
                            + ",Distance=" + playerVehicle.Position.DistanceTo(
                                suspectVehicle.Position).ToString("0.0", CultureInfo.InvariantCulture));
                }

                IEnumerable<Ped> nearbyPeds = World.GetNearbyPeds(
                    suspectVehicle.Position, inspectionRadius)
                    .Where(source => source != null && source.Exists()
                        && !source.IsDead
                        && (suspectDriver == null || !suspectDriver.Exists()
                            || source.Handle != suspectDriver.Handle)
                        && (player == null || !player.Exists()
                            || source.Handle != player.Handle))
                    .OrderBy(source => source.Position.DistanceTo(suspectVehicle.Position))
                    .Take(maximumPedsToInspect);
                foreach (Ped source in nearbyPeds)
                {
                    inspectedHandles.Add(source.Handle);
                    pedCandidates++;
                    if (Function.Call<bool>(
                        Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY,
                        suspectVehicle, source))
                        attributedSources.Add("Ped=" + source.Handle
                            + ",ModelHash=" + source.Model.Hash
                            + ",Distance=" + source.Position.DistanceTo(
                                suspectVehicle.Position).ToString("0.0", CultureInfo.InvariantCulture));
                }

                if (player != null && player.Exists() && !player.IsDead
                    && inspectedHandles.Add(player.Handle))
                {
                    pedCandidates++;
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY,
                            suspectVehicle, player))
                        attributedSources.Add("PlayerPed=" + player.Handle
                            + "; Distance=" + player.Position.DistanceTo(
                                suspectVehicle.Position).ToString("0.0", CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                nativeProbeFailed = true;
            }

            return "Native=HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY"
                + "; Radius=" + inspectionRadius.ToString("0", CultureInfo.InvariantCulture)
                + "; VehicleCandidates=" + vehicleCandidates
                + "; PedCandidates=" + pedCandidates
                + "; AttributedSources=" + (attributedSources.Count == 0
                    ? "none" : string.Join("|", attributedSources
                        .Take(maximumSourcesToReport)))
                + "; CandidateBound=" + (maximumVehiclesToInspect
                    + maximumPedsToInspect + 2)
                + "; NativeProbeFailed=" + nativeProbeFailed
                + "; AttributionComplete=false";
        }

        private static bool IsDispatchVehicleBlockingPursuitPath(
            Vehicle movingVehicle,
            Vehicle blockingVehicle)
        {
            if (movingVehicle == null || !movingVehicle.Exists()
                || blockingVehicle == null || !blockingVehicle.Exists()
                || Math.Abs(blockingVehicle.Speed)
                    > DispatchPursuitBlockerSpeedLimit)
                return false;

            Vector3 offset = blockingVehicle.Position - movingVehicle.Position;
            Vector3 forward = movingVehicle.ForwardVector;
            float forwardDistance = Vector3.Dot(offset, forward);
            if (forwardDistance < 0f
                || forwardDistance > DispatchPursuitBlockForwardMeters)
                return false;

            float lateralSquared = Math.Max(0f,
                offset.X * offset.X + offset.Y * offset.Y + offset.Z * offset.Z
                - forwardDistance * forwardDistance);
            return lateralSquared <= DispatchPursuitBlockLateralMeters
                * DispatchPursuitBlockLateralMeters;
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

            MaintainDispatchVehicleFlightCollisionProtection(suspect, state);
            ObserveDispatchPursuitIntervention(suspect, player, now, state);

            int currentHealth = Math.Max(0, suspect.Health);
            int currentMaximumHealth = Math.Max(1, suspect.MaxHealth);
            if (!suspect.IsDead && currentHealth > 0)
            {
                Vector3 livePosition = suspect.Position;
                bool stablePosition = IsSupportedDispatchRecoveryPosition(
                    suspect, livePosition);
                if (stablePosition)
                {
                    state.LastKnownAlivePosition = livePosition;
                    state.LastKnownAliveHeading = suspect.Heading;
                    state.LastKnownAlivePositionSupported = true;
                }
            }
            bool custodyDamageGuardActive = state.CustodyProtectionCommitted
                || IsDispatchCustodyDamageGuardActive(_incident, suspect);
            bool playerCommandedSecureBeforePhysicalArrest =
                !custodyDamageGuardActive
                && IsPlayerCommandedSecure(_incident, suspect);
            if (custodyDamageGuardActive)
                ApplyDispatchCustodyDamageProtection(suspect, state);
            else if (state.CustodyDamageProtectionActive)
                RestoreDispatchCustodyDamageProtection(suspect, state);
            MaintainDispatchCriticalHitGuard(suspect, state);
            if (currentHealth < state.LastHealth)
            {
                int healthBefore = state.LastHealth;
                int maximumHealthBefore = state.LastMaximumHealth;
                int damage = state.LastHealth - currentHealth;
                int rawBone = ReadDispatchLastDamageBone(suspect);
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
                }
                int damageWeaponHash;
                int damageAttackerHandle;
                int damageGameTime;
                Entity damageAttacker;
                bool damageRecordAvailable = TryReadDispatchDamageRecord(
                    suspect, out damageWeaponHash, out damageAttackerHandle,
                    out damageGameTime, out damageAttacker);
                int damageSampleGameTime = ReadDispatchGameTime();
                int damageRecordAgeMilliseconds = damageRecordAvailable
                    ? unchecked(damageSampleGameTime - damageGameTime) : -1;
                bool damageRecordFresh = damageRecordAvailable
                    && damageRecordAgeMilliseconds >= 0
                    && damageRecordAgeMilliseconds
                        <= DispatchDamageRecordMaximumAgeMilliseconds;
                bool damageRecordDuplicate = damageRecordFresh
                    && IsDispatchDamageRecordDuplicate(
                        state, damageWeaponHash, damageAttackerHandle,
                        damageGameTime);
                bool nativeDamageCheckAvailable = false;
                bool nativeReportsDamageByAttacker = false;
                if (damageRecordFresh && state.DamageAttributionBaselineAvailable
                    && IsDispatchDamageEntityUsable(damageAttacker))
                {
                    try
                    {
                        nativeReportsDamageByAttacker = Function.Call<bool>(
                            Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY,
                            suspect,
                            damageAttacker);
                        nativeDamageCheckAvailable = true;
                    }
                    catch { }
                }
                bool damageEventConfirmed = damageRecordFresh
                    && !damageRecordDuplicate
                    && state.DamageAttributionBaselineAvailable
                    && nativeDamageCheckAvailable
                    && nativeReportsDamageByAttacker;

                DispatchDamageAttackerClass attackerClass =
                    ClassifyDispatchDamageAttacker(
                        damageAttacker, damageAttackerHandle, player, _incident);
                state.LastDamageAttackerClass = attackerClass.ToString();
                state.LastDamageWeaponHash = damageWeaponHash;
                state.LastDamageAttackerHandle = damageAttackerHandle;
                state.LastDamageGameTime = damageGameTime;
                state.LastDamageRecordAgeMilliseconds = damageRecordAgeMilliseconds;
                state.LastDamageRecordAvailable = damageRecordAvailable;
                state.LastDamageRecordFresh = damageRecordFresh;
                state.LastDamageRecordDuplicate = damageRecordDuplicate;
                state.LastNativeDamageCheckAvailable = nativeDamageCheckAvailable;
                state.LastNativeReportsDamageByAttacker = nativeReportsDamageByAttacker;
                state.LastDamageEventConfirmed = damageEventConfirmed;
                bool directPlayerAttackOnCompliantSuspect =
                    playerCommandedSecureBeforePhysicalArrest
                    && damageRecordFresh
                    && attackerClass == DispatchDamageAttackerClass.PolicePlayer
                    && player != null && player.Exists()
                    && damageAttackerHandle == player.Handle
                    && IsDispatchDamageEntityUsable(damageAttacker);
                Ped sourcePed = damageAttacker as Ped;
                bool projectBranchDamageAuthorized =
                    playerCommandedSecureBeforePhysicalArrest
                    && !directPlayerAttackOnCompliantSuspect
                    && IsDocumentedProjectDamageBranchAuthorized(
                        _incident, suspect, sourcePed, damageAttacker,
                        attackerClass, damageEventConfirmed);
                bool authorizedCompliantDamage = directPlayerAttackOnCompliantSuspect
                    || projectBranchDamageAuthorized;
                if (playerCommandedSecureBeforePhysicalArrest
                    && !authorizedCompliantDamage)
                {
                    try
                    {
                        suspect.Health = Math.Max(1, healthBefore);
                        state.LastHealth = Math.Max(1, suspect.Health);
                        state.LastMaximumHealth = Math.Max(1, suspect.MaxHealth);
                        bool projectActor = attackerClass == DispatchDamageAttackerClass.ProjectBackupOfficer
                            || attackerClass == DispatchDamageAttackerClass.ProjectPoliceResponseOfficer
                            || attackerClass == DispatchDamageAttackerClass.DispatchSuspect
                            || attackerClass == DispatchDamageAttackerClass.DispatchVictim
                            || attackerClass == DispatchDamageAttackerClass.AmbientWorldSceneActor;
                        LogRuntime(
                            projectActor
                                ? "POLICE_DISPATCH_COMPLIANT_SUSPECT_PROJECT_ACTOR_DAMAGE_BLOCKED"
                                : "POLICE_DISPATCH_COMPLIANT_SUSPECT_EXTERNAL_DAMAGE_BLOCKED",
                            "Incident=" + state.OccurrenceId
                            + "; Suspect=" + suspect.Handle
                            + "; Attacker=" + damageAttackerHandle
                            + "; AttackerClass=" + attackerClass
                            + "; Weapon=" + damageWeaponHash
                            + "; Health=" + healthBefore + "->" + currentHealth
                            + "; RestoredTo=" + state.LastHealth
                            + "; DamageRecordFresh=" + damageRecordFresh
                            + "; DamageEventConfirmed=" + damageEventConfirmed
                            + "; ProjectActor=" + projectActor
                            + "; BranchAuthorization=NotConfirmed"
                            + "; PlayerAttack=false; PhysicalCuffs=false"
                            + "; Result=" + (projectActor
                                ? "ProjectBranchAuthorizationMissing"
                                : attackerClass == DispatchDamageAttackerClass.UnknownExternalActor
                                    ? "UnknownExternalDamageRejected"
                                    : "OutsideInterferenceRejected"));
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogException(
                            "POLICE_DISPATCH_COMPLIANT_SUSPECT_EXTERNAL_DAMAGE_RESTORE_FAILED",
                            ex);
                    }
                }
                if (directPlayerAttackOnCompliantSuspect)
                    LogRuntime(
                        "POLICE_DISPATCH_PLAYER_ATTACKED_COMPLIANT_SUSPECT",
                        "Incident=" + state.OccurrenceId
                        + "; Suspect=" + suspect.Handle
                        + "; Player=" + player.Handle
                        + "; Weapon=" + damageWeaponHash
                        + "; Health=" + healthBefore + "->" + currentHealth
                        + "; Damage=" + damage
                        + "; Bone=" + rawBone
                        + "; DamageKind=" + ReadDispatchDamageKind(
                            suspect, damageWeaponHash, damageEventConfirmed)
                        + "; Result=PlayerDamagePermitted; FatalOrInjuryResolution=DispatchPolicy");
                else if (projectBranchDamageAuthorized)
                    LogRuntime(
                        "POLICE_DISPATCH_COMPLIANT_SUSPECT_PROJECT_BRANCH_DAMAGE_PERMITTED",
                        "Incident=" + state.OccurrenceId
                        + "; Suspect=" + suspect.Handle
                        + "; Attacker=" + damageAttackerHandle
                        + "; AttackerClass=" + attackerClass
                        + "; Weapon=" + damageWeaponHash
                        + "; DamageEventConfirmed=" + damageEventConfirmed
                        + "; Result=AuthoredProjectBranchDamagePermitted");

                state.LastDamageWeaponHash = damageWeaponHash;
                state.LastDamageAttackerHandle = damageAttackerHandle;
                state.LastDamageGameTime = damageGameTime;
                state.LastDamageRecordAgeMilliseconds = damageRecordAgeMilliseconds;
                state.LastRawDamageBone = rawBone;
                state.LastEffectiveDamageBone = bone;
                state.LastDamageRecordAvailable = damageRecordAvailable;
                state.LastDamageRecordFresh = damageRecordFresh;
                state.LastDamageRecordDuplicate = damageRecordDuplicate;
                state.LastNativeDamageCheckAvailable = nativeDamageCheckAvailable;
                state.LastNativeReportsDamageByAttacker = nativeReportsDamageByAttacker;
                state.LastDamageEventConfirmed = damageEventConfirmed;
                ControlledDamageKind damageKind = ReadDispatchDamageKind(
                    suspect, damageWeaponHash, damageEventConfirmed);
                bool policePlayerMeleeMotionConfirmed =
                    damageEventConfirmed
                    && attackerClass == DispatchDamageAttackerClass.PolicePlayer
                    && damageAttackerHandle > 0
                    && player != null && player.Exists()
                    && damageAttackerHandle == player.Handle
                    && IsPolicePlayerMeleeMotionConfirmed(player);
                if (policePlayerMeleeMotionConfirmed)
                    damageKind = ControlledDamageKind.Melee;
                DispatchHitZone zone = ClassifyDispatchHitZone(bone);
                DispatchHitZone rawZone = ClassifyDispatchHitZone(rawBone);
                state.LastDamageZone = zone.ToString();
                if (damageEventConfirmed)
                    RecordConfirmedDispatchDamageOwnership(
                        state, attackerClass, damageAttackerHandle,
                        damageWeaponHash, damageGameTime, damageKind,
                        rawBone, now);

                bool wasDowned = state.Wounded
                    && (IsLivingDowned(suspect) || IsLivingInWrithe(suspect));
                bool wasWounded = state.Wounded;
                bool meleeReactionWasPending = state.MeleeReactionPending;
                bool policePlayerWeaponHit = damageEventConfirmed
                    && attackerClass == DispatchDamageAttackerClass.PolicePlayer
                    && damageAttackerHandle > 0
                    && player != null && player.Exists()
                    && damageAttackerHandle == player.Handle
                    && damageKind == ControlledDamageKind.Weapon;
                bool fatalVitalWeaponHit = IsConfirmedDispatchFatalVitalHit(
                    damageEventConfirmed, damageKind, rawBone,
                    policePlayerWeaponHit);
                string previousState = wasDowned
                    ? "DownedArrestable"
                    : (_incident == null ? "Active" : _incident.State.ToString());

                if (damageEventConfirmed)
                {
                    state.RegisteredHits++;
                    state.KnownDamageHits++;
                    if (damageKind == ControlledDamageKind.Melee)
                        state.MeleeHits++;
                    else if (damageKind == ControlledDamageKind.Weapon)
                        state.WeaponHits++;
                    if (wasDowned)
                    {
                        state.DownedHits++;
                        state.KnownDownedHits++;
                        if (fatalVitalWeaponHit)
                            state.FatalDownedHits++;
                    }
                    if (fatalVitalWeaponHit)
                        state.FatalVitalHits++;
                    if (rawZone == DispatchHitZone.Head)
                        state.HeadHits++;
                    else if (rawZone == DispatchHitZone.Face)
                        state.FaceHits++;
                    else if (rawZone == DispatchHitZone.Neck)
                        state.NeckHits++;
                    else if (rawZone == DispatchHitZone.Chest)
                        state.ChestHits++;
                    else if (rawZone == DispatchHitZone.Stomach)
                        state.StomachHits++;
                }

                state.DamageTaken = SaturatingAdd(state.DamageTaken, damage);
                if (state.PendingDamageSamples == 0)
                {
                    state.PendingDamageSummaryStartedAt = now;
                    state.PendingHealthBefore = healthBefore;
                }
                state.PendingDamageSamples++;
                state.PendingObservedHealthLoss = SaturatingAdd(
                    state.PendingObservedHealthLoss, damage);
                state.PendingLatestDamageDelta = damage;
                if (damageEventConfirmed)
                    state.PendingConfirmedDamageEvents++;
                else
                    state.PendingUnconfirmedDamageSamples++;
                if (damageRecordAvailable)
                    state.LastDamageAttackerHandle = damageAttackerHandle;

                state.LastDamageKind = damageKind.ToString();
                if (policePlayerMeleeMotionConfirmed)
                    LogRuntime("POLICE_DISPATCH_POLICE_PLAYER_MELEE_HIT_CONFIRMED",
                        "Incident=" + state.OccurrenceId
                        + "; Suspect=" + suspect.Handle
                        + "; Player=" + player.Handle
                        + "; Weapon=" + damageWeaponHash
                        + "; IS_PED_IN_MELEE_COMBAT=true"
                        + "; IS_PED_SHOOTING=false"
                        + "; DamageKind=Melee; FatalVitalEligible=false");

                string fatalThresholdReason;
                bool fatalThresholdReached = IsDispatchFatalThresholdReached(
                    fatalVitalWeaponHit, wasDowned, state.FatalVitalHits,
                    state.FatalDownedHits,
                    _injurySettings.CumulativeFatalHitThreshold,
                    _injurySettings.RepeatedDamageAfterDownedThreshold,
                    out fatalThresholdReason);

                bool injuryProtectionActive = state.Wounded
                    || damageEventConfirmed || custodyDamageGuardActive
                    || directPlayerAttackOnCompliantSuspect
                    || projectBranchDamageAuthorized;
                int protectedHealth = injuryProtectionActive
                    ? GetDispatchProtectedHealthAfterDamage(
                        healthBefore, currentHealth, currentMaximumHealth,
                        state.DispatchWoundedHealthReserve,
                        damageEventConfirmed, fatalThresholdReached,
                        suspect.IsDead)
                    : currentHealth;
                if (protectedHealth > currentHealth)
                {
                    int observedHealthAfter = currentHealth;
                    try
                    {
                        // Writhe and other un-attributed engine health loss can
                        // drain a living wounded suspect before arrest finishes.
                        // Recover only a living actor, before terminal checks;
                        // confirmed hits still count once and vital hits still
                        // reach the explicit fatal threshold.
                        suspect.Health = protectedHealth;
                        state.LastHealth = Math.Max(1, suspect.Health);
                        state.LastMaximumHealth = currentMaximumHealth;
                        currentHealth = Math.Max(1, suspect.Health);
                        state.WoundedHealthCorrections++;
                        bool guardLogDue = state.WoundedHealthCorrections == 1
                            || state.LastWoundedHealthCorrectionLogAt == DateTime.MinValue
                            || now >= state.LastWoundedHealthCorrectionLogAt.AddSeconds(15);
                        if (guardLogDue)
                        {
                            state.LastWoundedHealthCorrectionLogAt = now;
                            LogRuntime("POLICE_DISPATCH_WOUNDED_HEALTH_CORRECTED",
                                "Incident=" + state.OccurrenceId
                                + "; Ped=" + suspect.Handle
                                + "; Health=" + healthBefore + "->" + observedHealthAfter
                                + "; RestoredTo=" + state.LastHealth
                                + "; MaximumHealth=" + currentMaximumHealth
                                + "; DamageRecordFresh=" + damageRecordFresh
                                + "; DamageRecordDuplicate=" + damageRecordDuplicate
                                + "; DamageEventConfirmed=" + damageEventConfirmed
                                + "; FatalThresholdReached=" + fatalThresholdReached
                                + "; WoundedHealthReserve=" + state.DispatchWoundedHealthReserve
                                + "; Attacker=" + damageAttackerHandle
                                + "; Weapon=" + damageWeaponHash
                                + "; DamageAgeMs=" + damageRecordAgeMilliseconds
                                + "; Bone=" + rawBone
                                + "; Corrections=" + state.WoundedHealthCorrections
                                + "; CustodyDamageGuardActive=" + custodyDamageGuardActive
                                + "; PhysicalCuffs=" + IsPedPhysicallyCuffed(suspect)
                                + "; PlayerHandcuffActive="
                                + (_incident != null
                                    && _incident.PlayerHandcuffInProgress
                                    && _incident.PlayerHandcuffTargetHandle == suspect.Handle)
                                + "; BackupArrestActive="
                                + (_incident != null
                                    && _incident.BackupArrestInProgressSuspectHandles.Contains(
                                        suspect.Handle))
                                + "; EngineDead=" + suspect.IsDead);
                        }
                        if (!damageEventConfirmed)
                            return;
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_WOUNDED_HEALTH_CORRECTION_FAILED", ex);
                    }
                }

                if (fatalThresholdReached && currentHealth > 0)
                {
                    // Only three fresh, unique, native-corroborated Police
                    // Player weapon hits to a vital bone can reach fatality.
                    try
                    {
                        suspect.Health = 0;
                        currentHealth = Math.Max(0, suspect.Health);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_CONFIRMED_FATAL_THRESHOLD_FAILED", ex);
                    }
                }

                bool engineTerminal = suspect.IsDead || currentHealth <= 0;
                if (engineTerminal && !fatalThresholdReached
                    && state.ConfirmedDamageOwnershipHistory.Count == 0
                    && TryRecoverInvalidTerminalDispatchSuspect(
                        suspect, state, player, healthBefore, currentHealth,
                        damageEventConfirmed
                            ? "EngineTerminalBeforeThreePoliceVitalHits"
                            : "UnconfirmedEngineTerminalWithoutFreshHit"))
                    return;

                state.Fatal = engineTerminal;
                if (state.Fatal)
                {
                    bool attackerAttributedTerminalWeaponHit =
                        IsConfirmedDispatchTerminalWeaponHit(
                            engineTerminal, healthBefore, currentHealth, damage,
                            damageEventConfirmed, damageKind,
                            fatalThresholdReached);
                    string recognizedTerminalDamageOwner = string.Empty;
                    state.LastHealth = 0;
                    state.LastMaximumHealth = currentMaximumHealth;
                    bool deathSourceMatchesFreshDamageAttacker;
                    string deathEvidence = CaptureDispatchFatalEvidence(
                        suspect, state, player, healthBefore, currentHealth,
                        maximumHealthBefore, currentMaximumHealth,
                        damageRecordAvailable, damageRecordFresh,
                        damageEventConfirmed, damageWeaponHash,
                        damageAttackerHandle, damageGameTime,
                        damageRecordAgeMilliseconds, rawBone, bone,
                        zone.ToString(), fatalThresholdReason,
                        out _,
                        out deathSourceMatchesFreshDamageAttacker);
                    bool damageOwnerRecognized = engineTerminal
                        && healthBefore > currentHealth
                        && currentHealth <= 0
                        && state.DamageAttributionBaselineAvailable
                        && damageAttackerHandle != 0
                        && IsDispatchDamageEntityUsable(damageAttacker)
                        && TryClassifyDispatchDamageOwner(
                            damageAttacker, player, out recognizedTerminalDamageOwner);
                    bool freshUniqueTerminalDamageRecord = damageRecordAvailable
                        && damageRecordFresh
                        && !damageRecordDuplicate
                        && damageKind != ControlledDamageKind.Unknown;
                    bool recognizedTerminalDamage = damageOwnerRecognized
                        && (freshUniqueTerminalDamageRecord
                            || deathSourceMatchesFreshDamageAttacker);
                    bool fatalOwnershipReconciliationPending;
                    string fatalOwnershipEvidence;
                    ResolveDispatchTerminalOwnership(
                        suspect, state, player, _incident, now,
                        fatalThresholdReached,
                        out fatalOwnershipReconciliationPending,
                        out fatalOwnershipEvidence);
                    LogDispatchInjurySummary(suspect, state, previousState,
                        currentHealth, now, true);
                    LogRuntime(fatalThresholdReached
                            ? "POLICE_DISPATCH_SUSPECT_FATAL_INJURY"
                            : "POLICE_DISPATCH_SUSPECT_TERMINAL_POLICY_VIOLATION",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + suspect.Handle
                        + "; DamageKind=" + damageKind
                        + "; Zone=" + zone
                        + "; Reason=" + state.FatalOutcomeReason
                        + "; Expected=" + state.FatalOutcomeExpected
                        + "; CauseConfirmed=" + state.FatalCauseConfirmed
                        + "; DamageEventConfirmed=" + damageEventConfirmed
                        + "; PolicePlayerWeaponHit=" + policePlayerWeaponHit
                        + "; CompliantPlayerAttack="
                        + directPlayerAttackOnCompliantSuspect
                        + "; RegisteredHits=" + state.RegisteredHits
                        + "; WeaponHits=" + state.WeaponHits
                        + "; MeleeHits=" + state.MeleeHits
                        + "; FatalVitalHits=" + state.FatalVitalHits
                        + "; TerminalAttackerHitConfirmed="
                        + attackerAttributedTerminalWeaponHit
                        + "; BoundedRecoveryAttempted="
                        + state.InvalidTerminalRecoveryAttempted
                        + "; RecoveryLimit=1"
                        + "; RecognizedTerminalDamage=" + recognizedTerminalDamage
                        + "; RecognizedDamageOwner="
                        + (string.IsNullOrWhiteSpace(recognizedTerminalDamageOwner)
                            ? "Unknown" : recognizedTerminalDamageOwner)
                        + "; FatalOutcomeOwnerClass="
                        + (state.FatalOutcomeOwnerClass ?? "Unknown")
                        + "; FatalOutcomeOwnerHandle="
                        + state.FatalOutcomeOwnerHandle
                        + "; FatalOwnershipEvidenceSource="
                        + (state.FatalOutcomeEvidenceSource ?? "none")
                        + "; ConfirmedOwnershipHistoryCount="
                        + state.ConfirmedDamageOwnershipHistory.Count
                        + "; FatalOwnershipReconciliationPending="
                        + fatalOwnershipReconciliationPending
                        + "; FatalOwnershipEvidence="
                        + fatalOwnershipEvidence
                        + "; DeathSourceMatchesFreshDamageAttacker="
                        + deathSourceMatchesFreshDamageAttacker
                        + "; DownedHits=" + state.DownedHits
                        + "; FatalDownedHits=" + state.FatalDownedHits
                        + "; ObservedHealthLoss=" + state.DamageTaken
                        + deathEvidence);
                    NotifyOtherLivingDispatchSuspects(state.OccurrenceId, suspect);
                    return;
                }

                bool unexplainedLargeHealthDrop = !damageEventConfirmed
                    && damage >= Math.Max(100, currentMaximumHealth / 2);
                bool anomalyEvidenceDue = state.LastHealthAnomalyEvidenceAt
                        == DateTime.MinValue
                    || now >= state.LastHealthAnomalyEvidenceAt.AddSeconds(
                        DispatchHealthAnomalyEvidenceCooldownSeconds);
                if (unexplainedLargeHealthDrop && anomalyEvidenceDue)
                {
                    state.LastHealthAnomalyEvidenceAt = now;
                    bool anomalyCauseConfirmed;
                    string anomalyEvidence = CaptureDispatchFatalEvidence(
                        suspect, state, player, healthBefore, currentHealth,
                        maximumHealthBefore, currentMaximumHealth,
                        damageRecordAvailable, damageRecordFresh,
                        damageEventConfirmed, damageWeaponHash,
                        damageAttackerHandle, damageGameTime,
                        damageRecordAgeMilliseconds, rawBone, bone,
                        zone.ToString(), "LargeUnconfirmedHealthDrop",
                        out anomalyCauseConfirmed,
                        out _);
                    LogRuntime("POLICE_DISPATCH_SUSPECT_HEALTH_ANOMALY",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + suspect.Handle
                        + "; Trigger=LargeUnconfirmedHealthDrop"
                        + "; Health=" + healthBefore + "->" + currentHealth
                        + "; MaximumHealth=" + maximumHealthBefore
                        + "->" + currentMaximumHealth
                        + "; DamageDelta=" + damage
                        + "; DamageKind=" + damageKind
                        + "; DamageRecordFresh=" + damageRecordFresh
                        + "; DamageEventConfirmed=" + damageEventConfirmed
                        + "; CauseConfirmed=" + anomalyCauseConfirmed
                        + anomalyEvidence);
                }

                try
                {
                    if (damageKind == ControlledDamageKind.Melee)
                    {
                        state.Wounded = true;
                        state.WritheTaskStarted = false;
                        state.WoundedTaskAttempts = 0;
                        state.NextWoundedTaskCheckAt = now;
                        state.WoundedTaskSurrenderFallbackUsed = false;
                        state.WoundedTaskPresentation = null;
                        state.MeleeReactionPending = true;
                        state.MeleeReactionUntil = now.AddMilliseconds(
                            _injurySettings.MeleeReactionRecoveryMilliseconds);
                        state.NextMeleeRecoveryCheckAt = state.MeleeReactionUntil;
                    }
                    else
                    {
                        state.Wounded = true;
                        state.WritheTaskStarted = false;
                        state.WoundedTaskAttempts = 0;
                        state.NextWoundedTaskCheckAt = now;
                        state.WoundedTaskSurrenderFallbackUsed = false;
                        state.WoundedTaskPresentation = null;
                        if (damageKind == ControlledDamageKind.Weapon)
                            DisableWoundedSuspectWeapon(suspect, state);
                    }

                if (!authorizedCompliantDamage
                    && currentHealth < state.ArrestableWoundedHealthFloor)
                {
                    suspect.Health = state.ArrestableWoundedHealthFloor;
                    state.PendingFloorApplications++;
                    currentHealth = Math.Max(1, suspect.Health);
                }
                    state.LastHealth = Math.Max(1, suspect.Health);
                    state.LastMaximumHealth = Math.Max(1, suspect.MaxHealth);

                    if (directPlayerAttackOnCompliantSuspect)
                        LogRuntime(
                            "POLICE_DISPATCH_PLAYER_COMPLIANT_SUSPECT_INJURY_RESULT",
                            "Incident=" + state.OccurrenceId
                            + "; Suspect=" + suspect.Handle
                            + "; Player=" + player.Handle
                            + "; Weapon=" + damageWeaponHash
                            + "; DamageKind=" + damageKind
                            + "; Health=" + healthBefore + "->" + state.LastHealth
                            + "; Wounded=" + state.Wounded
                            + "; FatalThresholdReached=" + fatalThresholdReached
                            + "; Result=" + (state.Wounded
                                ? "PlayerInjuryRecorded" : "PlayerDamageRecorded"));

                    bool significantDamage = damage >= Math.Max(100,
                        currentMaximumHealth / 2);
                    bool confirmedVitalHit = damageEventConfirmed
                        && policePlayerWeaponHit
                        && damageKind == ControlledDamageKind.Weapon
                        && IsDispatchFatalVitalZone(rawZone);
                    bool logDamageNow = (state.Wounded && !wasWounded)
                        || (damageKind == ControlledDamageKind.Melee
                            && !meleeReactionWasPending)
                        || significantDamage || confirmedVitalHit
                        || now - state.PendingDamageSummaryStartedAt
                            >= TimeSpan.FromSeconds(
                                DispatchDamageSummaryIntervalSeconds);
                    if (logDamageNow)
                        LogDispatchInjurySummary(suspect, state, previousState,
                            state.LastHealth, now, false);

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
            else
            {
                if (suspect.IsDead || currentHealth <= 0)
                {
                    if (TryRecoverInvalidTerminalDispatchSuspect(
                        suspect, state, player, state.LastHealth, currentHealth,
                        "TerminalStateWithoutFreshHealthDamage"))
                        return;

                    state.Fatal = true;
                    int lastObservedHealth = state.LastHealth;
                    int lastObservedMaximumHealth = state.LastMaximumHealth;
                    state.LastHealth = 0;
                    state.LastMaximumHealth = currentMaximumHealth;
                    string deathEvidence = CaptureDispatchFatalEvidence(
                        suspect, state, player, lastObservedHealth, currentHealth,
                        lastObservedMaximumHealth, currentMaximumHealth,
                        false, false, false, 0,
                        state.LastDamageAttackerHandle,
                        state.LastDamageGameTime, -1,
                        state.LastRawDamageBone, state.LastEffectiveDamageBone,
                        string.IsNullOrWhiteSpace(state.LastDamageZone)
                            ? DispatchHitZone.Unknown.ToString()
                            : state.LastDamageZone,
                        string.Empty,
                        out _,
                        out _);
                    bool fatalOwnershipReconciliationPending;
                    string fatalOwnershipEvidence;
                    ResolveDispatchTerminalOwnership(
                        suspect, state, player, _incident, now, false,
                        out fatalOwnershipReconciliationPending,
                        out fatalOwnershipEvidence);
                    LogRuntime("POLICE_DISPATCH_SUSPECT_TERMINAL_POLICY_VIOLATION",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + suspect.Handle
                        + "; DamageKind=" + (state.LastDamageKind ?? "Unknown")
                        + "; Zone=" + (state.LastDamageZone ?? "Unknown")
                        + "; Reason=" + state.FatalOutcomeReason
                        + "; Expected=" + state.FatalOutcomeExpected
                        + "; CauseConfirmed=" + state.FatalCauseConfirmed
                        + "; DamageEventConfirmed=false; RegisteredHits="
                        + state.RegisteredHits
                        + "; BoundedRecoveryAttempted="
                        + state.InvalidTerminalRecoveryAttempted
                        + "; RecoveryLimit=1"
                        + "; FatalOutcomeOwnerClass="
                        + (state.FatalOutcomeOwnerClass ?? "Unknown")
                        + "; FatalOutcomeOwnerHandle="
                        + state.FatalOutcomeOwnerHandle
                        + "; FatalOwnershipEvidenceSource="
                        + (state.FatalOutcomeEvidenceSource ?? "none")
                        + "; ConfirmedOwnershipHistoryCount="
                        + state.ConfirmedDamageOwnershipHistory.Count
                        + "; FatalOwnershipReconciliationPending="
                        + fatalOwnershipReconciliationPending
                        + "; FatalOwnershipEvidence="
                        + fatalOwnershipEvidence + deathEvidence);
                    NotifyOtherLivingDispatchSuspects(state.OccurrenceId, suspect);
                    return;
                }

                if (currentMaximumHealth != state.LastMaximumHealth)
                {
                    LogRuntime("POLICE_DISPATCH_SUSPECT_MAXIMUM_HEALTH_CHANGED",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + suspect.Handle
                        + "; Health=" + currentHealth
                        + "; PreviousMaximumHealth=" + state.LastMaximumHealth
                        + "; CurrentMaximumHealth=" + currentMaximumHealth
                        + "; MaximumHealthWrittenByDispatch=false");
                    state.LastMaximumHealth = currentMaximumHealth;
                }

                if (state.PendingDamageSamples > 0
                    && now - state.PendingDamageSummaryStartedAt
                        >= TimeSpan.FromSeconds(
                            DispatchDamageSummaryIntervalSeconds))
                {
                    LogDispatchInjurySummary(suspect, state,
                        _incident == null ? "Inactive" : _incident.State.ToString(),
                        currentHealth, now, false);
                }
            }

            bool handcuffing = _incident != null
                && _incident.PlayerHandcuffInProgress
                && _incident.PlayerHandcuffTargetHandle == suspect.Handle;
            if (!state.Wounded || state.Fatal || handcuffing
                || IsArrested(_incident, suspect)
                || suspect.IsDead || suspect.Health <= 0
                || suspect.IsInVehicle()
                || IsDispatchMeleeRecoveryPending(suspect, now))
                return;

            MaintainWoundedSuspectPresentation(suspect, state, player, now);
        }

        private void MaintainWoundedSuspectPresentation(
            Ped suspect,
            LSPDSuspectInjuryState state,
            Ped player,
            DateTime now)
        {
            if (suspect == null || state == null || !suspect.Exists()
                || suspect.IsDead || suspect.Health <= 0)
                return;

            bool inWrithe = false;
            bool physicallyDowned = false;
            try
            {
                inWrithe = Function.Call<bool>(Hash.IS_PED_IN_WRITHE, suspect);
                physicallyDowned = IsLivingDowned(suspect);
            }
            catch { }

            if (inWrithe || physicallyDowned)
            {
                state.WritheTaskStarted = inWrithe;
                string presentation = inWrithe ? "Writhe" : "Grounded";
                if (!string.Equals(state.WoundedTaskPresentation, presentation,
                        StringComparison.Ordinal))
                {
                    state.WoundedTaskPresentation = presentation;
                    LogRuntime("POLICE_DISPATCH_WOUNDED_PRESENTATION_CONFIRMED",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + suspect.Handle
                        + "; Presentation=" + presentation
                        + "; Alive=true; Arrestable=true");
                }
                return;
            }

            if (state.WoundedTaskSurrenderFallbackUsed
                || now < state.NextWoundedTaskCheckAt)
                return;

            if (state.WoundedTaskAttempts < WoundedSuspectTaskMaximumAttempts)
            {
                state.WoundedTaskAttempts++;
                state.NextWoundedTaskCheckAt = now.AddMilliseconds(
                    WoundedSuspectTaskRetryMilliseconds);
                state.WritheTaskStarted = true;
                try
                {
                Function.Call(Hash.TASK_WRITHE,
                    suspect,
                    player != null && player.Exists() ? player : suspect,
                    -1, 0, false, 0);
                    state.WoundedTaskPresentation = "WritheRequested";
                LogRuntime("POLICE_DISPATCH_SUSPECT_WRITHE_TASK_ISSUED",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                        + "; Attempt=" + state.WoundedTaskAttempts
                        + "; MaximumAttempts=" + WoundedSuspectTaskMaximumAttempts
                        + "; AwaitingNativeConfirmation=true"
                        + "; Arrestable=true; Teleport=false; Attachment=false");
                }
                catch (Exception ex)
                {
                    state.WritheTaskStarted = false;
                    LogException("POLICE_DISPATCH_SUSPECT_WRITHE_TASK_FAILED", ex);
                }
                return;
            }

            try
            {
                // A failed/unsupported writhe task must not leave a live suspect
                // in GTA's raw model pose. Hands-up is a stable, visible,
                // arrestable fallback that persists until a documented
                // response or physical arrest takes ownership.
                suspect.Task.HandsUp(-1);
                state.WoundedTaskSurrenderFallbackUsed = true;
                state.WritheTaskStarted = false;
                state.WoundedTaskPresentation = "SurrenderFallback";
                LogRuntime("POLICE_DISPATCH_WOUNDED_PRESENTATION_FALLBACK",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                    + "; Presentation=ContinuousHandsUp"
                    + "; WritheAttempts=" + state.WoundedTaskAttempts
                    + "; Alive=true; Arrestable=true; TaskCleared=false");
            }
            catch (Exception ex)
            {
                state.WoundedTaskSurrenderFallbackUsed = true;
                LogException("POLICE_DISPATCH_WOUNDED_PRESENTATION_FALLBACK_FAILED", ex);
            }
        }

        private void MaintainDispatchCriticalHitGuard(
            Ped suspect,
            LSPDSuspectInjuryState state)
        {
            if (suspect == null || state == null || !suspect.Exists())
                return;
            try
            {
                if (state.CompliantPlayerVulnerabilityActive)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_COMPLIANT_PLAYER_VULNERABILITY_ENDED",
                        "Incident=" + state.OccurrenceId
                        + "; Ped=" + suspect.Handle
                        + "; PhysicalCuffs=" + IsPedPhysicallyCuffed(suspect)
                        + "; PlayerDamageStillAllowed=true"
                        + "; NativeCriticalShortcut=false");
                    state.CompliantPlayerVulnerabilityActive = false;
                }
                if (suspect.CanSufferCriticalHits)
                {
                    suspect.CanSufferCriticalHits = false;
                    state.CriticalHitGuardReassertions++;
                }
                state.CriticalHitGuardActive = !suspect.CanSufferCriticalHits;
                if (!state.CriticalHitGuardActive)
                    throw new InvalidOperationException(
                        "GTA did not retain the Dispatch three-hit vital injury guard.");
                state.CriticalHitGuardFailureLogged = false;
            }
            catch (Exception ex)
            {
                state.CriticalHitGuardActive = false;
                if (state.CriticalHitGuardFailureLogged)
                    return;
                state.CriticalHitGuardFailureLogged = true;
                LogException("POLICE_DISPATCH_CRITICAL_HIT_GUARD_FAILED", ex);
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
                    + "; OriginalSelectedWeaponHash="
                    + state.OriginalSelectedWeaponHash
                    + "; CurrentWeapon=Unarmed; InventoryRemoved=false"
                    + "; NextOutcome=WaitForPhysicalVehicleStopAndDriverExit"
                    + "; HealthWritten=false; MaximumHealthWritten=false");
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
            bool damageEventConfirmed)
        {
            if (!damageEventConfirmed || damageWeaponHash == 0
                || suspect == null || !suspect.Exists())
                return ControlledDamageKind.Unknown;
            try
            {
                WeaponGroup group = Function.Call<WeaponGroup>(
                    Hash.GET_WEAPONTYPE_GROUP, unchecked((uint)damageWeaponHash));
                return group == WeaponGroup.Melee || group == WeaponGroup.Unarmed
                    ? ControlledDamageKind.Melee
                    : ControlledDamageKind.Weapon;
            }
            catch { }
            return ControlledDamageKind.Unknown;
        }

        private static bool IsPolicePlayerMeleeMotionConfirmed(Ped player)
        {
            if (player == null || !player.Exists())
                return false;
            try
            {
                // IS_PED_IN_MELEE_COMBAT also covers the GTA pistol-whip
                // animation. Do not let a handgun's weapon group turn a
                // gun-butt strike into a vital firearm hit.
                bool melee = Function.Call<bool>(
                    (Hash)0x4E209B2C1EAD5159UL, player);
                bool shooting = Function.Call<bool>(Hash.IS_PED_SHOOTING, player);
                return melee && !shooting;
            }
            catch { return false; }
        }

        private static bool ClearDispatchDamageAttribution(
            Ped suspect,
            out string status)
        {
            bool boneCleared = false;
            bool weaponCleared = false;
            bool attackerCleared = false;
            if (suspect != null && suspect.Exists())
            {
                try
                {
                    Function.Call(Hash.CLEAR_PED_LAST_DAMAGE_BONE, suspect);
                    boneCleared = true;
                }
                catch { }
                try
                {
                    Function.Call(Hash.CLEAR_ENTITY_LAST_WEAPON_DAMAGE, suspect);
                    weaponCleared = true;
                }
                catch { }
                try
                {
                    Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, suspect);
                    attackerCleared = true;
                }
                catch { }
            }
            status = "Bone=" + boneCleared
                + "; Weapon=" + weaponCleared
                + "; Attacker=" + attackerCleared;
            return boneCleared && weaponCleared && attackerCleared;
        }

        private static int ReadDispatchGameTime()
        {
            try { return Game.GameTime; }
            catch { return 0; }
        }

        private static bool IsDispatchDamageEntityUsable(Entity entity)
        {
            try { return entity != null && entity.Exists() && entity.Handle != 0; }
            catch { return false; }
        }

        private bool TryClassifyDispatchDamageOwner(
            Entity damageAttacker,
            Ped player,
            out string owner)
        {
            owner = string.Empty;
            if (!IsDispatchDamageEntityUsable(damageAttacker)
                || _incident == null || !_incident.OwnedByDispatch)
                return false;

            Ped controller = damageAttacker as Ped;
            Vehicle attackerVehicle = damageAttacker as Vehicle;
            if (controller == null && attackerVehicle != null
                && attackerVehicle.Exists())
            {
                try { controller = attackerVehicle.Driver; }
                catch { controller = null; }
            }

            if (controller != null && controller.Exists()
                && player != null && player.Exists())
            {
                if (controller.Handle == player.Handle)
                {
                    owner = "Player";
                    return true;
                }
                if (player.IsInVehicle())
                {
                    Vehicle playerVehicle = player.CurrentVehicle;
                    if (playerVehicle != null && playerVehicle.Exists()
                        && attackerVehicle != null
                        && attackerVehicle.Handle == playerVehicle.Handle)
                    {
                        owner = "Player";
                        return true;
                    }
                }
            }

            if (_backup != null
                && _backup.IsCurrentDispatchSupportAttacker(
                    _incident, damageAttacker, controller))
            {
                owner = "RequestedBackup";
                return true;
            }

            Ped gangActor = controller;
            if (gangActor == null || !gangActor.Exists() || player == null
                || !player.Exists() || _gangData == null)
                return false;

            try
            {
                int modelHash = gangActor.Model.Hash;
                LSPDGangDefinition gang = _gangData.FindGangForModel(modelHash);
                bool playerOwnedGangModel = gang != null
                    && (gang.IsPlayerOwned || gang.CreatedByPlayer);
                bool playerMemberPoolModel = _gangData.IsMemberPoolModel(modelHash)
                    && _gangData.PlayerGang != null;
                if (!playerOwnedGangModel && !playerMemberPoolModel)
                    return false;

                int actorToPlayer = Function.Call<int>(
                    Hash.GET_RELATIONSHIP_BETWEEN_PEDS, gangActor, player);
                int playerToActor = Function.Call<int>(
                    Hash.GET_RELATIONSHIP_BETWEEN_PEDS, player, gangActor);
                // GTA relationship values 0–2 mean companion, respect, or like.
                // Model membership alone never assigns responsibility.
                if (actorToPlayer < 0 || actorToPlayer > 2
                    || playerToActor < 0 || playerToActor > 2)
                    return false;

                owner = "PlayerAlliedGangPool";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDispatchDamageRecordDuplicate(
            LSPDSuspectInjuryState state,
            int weaponHash,
            int attackerHandle,
            int gameTime)
        {
            if (state == null)
                return true;
            string key = weaponHash.ToString(CultureInfo.InvariantCulture)
                + ":" + attackerHandle.ToString(CultureInfo.InvariantCulture)
                + ":" + gameTime.ToString(CultureInfo.InvariantCulture);
            if (state.ProcessedDamageRecordKeys.Contains(key))
                return true;
            state.ProcessedDamageRecordKeys.Add(key);
            state.ProcessedDamageRecordOrder.Enqueue(key);
            while (state.ProcessedDamageRecordOrder.Count
                > MaximumTrackedDispatchDamageRecords)
            {
                string oldest = state.ProcessedDamageRecordOrder.Dequeue();
                state.ProcessedDamageRecordKeys.Remove(oldest);
            }
            return false;
        }

        private static void RecordConfirmedDispatchDamageOwnership(
            LSPDSuspectInjuryState state,
            DispatchDamageAttackerClass attackerClass,
            int attackerHandle,
            int weaponHash,
            int gameTime,
            ControlledDamageKind damageKind,
            int rawBone,
            DateTime confirmedAt)
        {
            if (state == null)
                return;
            state.ConfirmedDamageOwnershipHistory.Enqueue(
                new DispatchDamageOwnershipEvidence
                {
                    AttackerClass = attackerClass,
                    AttackerHandle = attackerHandle,
                    WeaponHash = weaponHash,
                    GameTime = gameTime,
                    DamageKind = damageKind.ToString(),
                    RawBone = rawBone,
                    ConfirmedAt = confirmedAt
                });
            while (state.ConfirmedDamageOwnershipHistory.Count
                > MaximumConfirmedDamageOwnershipHistory)
                state.ConfirmedDamageOwnershipHistory.Dequeue();
        }

        private static bool IsDispatchFatalVitalZone(DispatchHitZone zone)
        {
            return zone == DispatchHitZone.Head
                || zone == DispatchHitZone.Face
                || zone == DispatchHitZone.Neck
                || zone == DispatchHitZone.Chest;
        }

        private bool TryRecoverInvalidTerminalDispatchSuspect(
            Ped suspect,
            LSPDSuspectInjuryState state,
            Ped player,
            int healthBefore,
            int terminalHealth,
            string terminalCause)
        {
            if (suspect == null || state == null || !suspect.Exists()
                || state.FatalVitalHits >= 3
                || _incident == null || !_incident.OwnedByDispatch
                || _incident.HasConvoyCustodyHandoff
                || !string.Equals(state.OccurrenceId, _incident.Id,
                    StringComparison.Ordinal)
                || _incident.Suspects == null
                || !_incident.Suspects.Any(value => value != null
                    && value.Handle == suspect.Handle)
                || _incident.ConvoyCustodySuspectHandles.Contains(suspect.Handle)
                || IsPedPhysicallyCuffed(suspect)
                || _incident.State == LSPDDispatchState.Completed
                || _incident.State == LSPDDispatchState.Cancelled
                || _incident.State == LSPDDispatchState.Failed
                || !IsFiniteDispatchPosition(state.LastKnownAlivePosition)
                || !state.LastKnownAlivePositionSupported
                || state.InvalidTerminalRecoveryAttempted
                || !IsFiniteDispatchPosition(suspect.Position)
                || suspect.Position.DistanceTo(state.LastKnownAlivePosition) > 3.0f
                || state.LastKnownAlivePosition == Vector3.Zero)
                return false;

            // GTA can keep reporting the same terminal Ped every update. Permit
            // one same-entity recovery at its last confirmed supported position;
            // a second terminal transition becomes a policy failure and never
            // clears or revives the actor again.
            state.InvalidTerminalRecoveryAttempted = true;
            state.InvalidTerminalRecoveryCount++;
            int suspectHandle = suspect.Handle;
            bool arrestWasInProgress = _incident.PlayerHandcuffInProgress
                && _incident.PlayerHandcuffTargetHandle == suspectHandle;
            string recoveryResult = "Failed";
            string failure = string.Empty;
            try
            {
                // RESURRECT_PED is sensitive to an invalid/default world
                // position. Reapply the last live position before invoking it,
                // then clear tasks and restore that position again afterward.
                try { suspect.Task.ClearAll(); } catch { }
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET,
                    suspect, state.LastKnownAlivePosition.X,
                    state.LastKnownAlivePosition.Y, state.LastKnownAlivePosition.Z,
                    false, false, false);
                Function.Call(Hash.RESURRECT_PED, suspect);
                if (!suspect.Exists())
                    throw new InvalidOperationException(
                        "The original Dispatch suspect entity no longer exists.");

                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET,
                    suspect, state.LastKnownAlivePosition.X,
                    state.LastKnownAlivePosition.Y, state.LastKnownAlivePosition.Z,
                    false, false, false);
                suspect.Heading = state.LastKnownAliveHeading;
                Function.Call(Hash.SET_ENTITY_VELOCITY, suspect, 0f, 0f, 0f);
                Function.Call(Hash.SET_ENTITY_COLLISION, suspect, true, true);
                if (state.ManagedMaximumHealth > 0
                    && suspect.MaxHealth != state.ManagedMaximumHealth)
                    suspect.MaxHealth = state.ManagedMaximumHealth;
                int restoredHealth = Math.Max(
                    Math.Max(1, state.ArrestableWoundedHealthFloor),
                    state.DispatchWoundedHealthReserve);
                suspect.Health = Math.Min(
                    Math.Max(1, suspect.MaxHealth), restoredHealth);
                suspect.CanSufferCriticalHits = false;
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                if (state.ManagedRelationshipGroupHash != 0)
                    Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH,
                        suspect, state.ManagedRelationshipGroupHash);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON,
                    suspect, (uint)WeaponHash.Unarmed, true);

                if (suspect.IsDead || suspect.Health <= 0)
                    throw new InvalidOperationException(
                        "GTA still reports the original suspect as terminal after resurrection.");

                state.Fatal = false;
                state.FatalCauseConfirmed = false;
                state.FatalOutcomeExpected = false;
                state.FatalOutcomeCompromised = false;
                state.FatalOwnershipReconciliationComplete = false;
                state.FatalOwnershipReconciliationPendingLogged = false;
                state.FatalOwnershipReconciliationStartedAt = DateTime.MinValue;
                state.FatalOutcomeOwnerClass = string.Empty;
                state.FatalOutcomeOwnerHandle = 0;
                state.FatalOutcomeEvidenceSource = string.Empty;
                state.FatalOutcomeReason = "InvalidEngineTerminalRecoveredWithinDispatchPolicy";
                state.Wounded = true;
                state.WritheTaskStarted = false;
                state.WoundedTaskAttempts = 0;
                state.NextWoundedTaskCheckAt = DateTime.UtcNow;
                state.WoundedTaskSurrenderFallbackUsed = false;
                state.WoundedTaskPresentation = null;
                state.CompliantPlayerVulnerabilityActive = false;
                state.LastHealth = Math.Max(1, suspect.Health);
                state.LastMaximumHealth = Math.Max(1, suspect.MaxHealth);
                state.LastKnownAlivePosition = suspect.Position;
                state.LastKnownAliveHeading = suspect.Heading;
                state.LastValidDamageBone = 0;
                state.LastValidDamageBoneAt = DateTime.MinValue;
                state.LastDamageEventConfirmed = false;
                state.LastDamageRecordFresh = false;
                state.LastDamageRecordDuplicate = false;
                state.DamageAttributionBaselineAvailable =
                    ClearDispatchDamageAttribution(
                        suspect, out state.DamageAttributionBaselineStatus);

                if (arrestWasInProgress)
                {
                    string abort = AbortPlayerHandcuffAnimation(
                        player, suspect,
                        "PolicyInvalidEngineTerminalDuringArrest");
                    recoveryResult = "RecoveredSameSuspect;ArrestAborted;"
                        + abort;
                }
                else
                {
                    MaintainWoundedSuspectPresentation(
                        suspect, state, player, DateTime.UtcNow);
                    recoveryResult = "RecoveredSameSuspect;WoundedAndArrestable;Presentation="
                        + (state.WoundedTaskPresentation ?? "PendingNativeTaskConfirmation");
                }
                LogRuntime("POLICE_DISPATCH_INVALID_TERMINAL_RECOVERED",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspectHandle
                    + "; TerminalCause=" + (terminalCause ?? "Unknown")
                    + "; Health=" + healthBefore + "->" + terminalHealth
                    + "; FatalVitalHits=" + state.FatalVitalHits
                    + "; RequiredVitalHits=3"
                    + "; RecoveryAttempt=" + state.InvalidTerminalRecoveryCount
                    + "; LastKnownAlivePosition="
                    + DescribePosition(state.LastKnownAlivePosition)
                    + "; RelationshipGroup=" + state.ManagedRelationshipGroupHash
                    + "; SameEntity=true; Result=" + recoveryResult);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ":" + ex.Message;
                LogRuntime("POLICE_DISPATCH_INVALID_TERMINAL_RECOVERY_FAILED",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspectHandle
                    + "; TerminalCause=" + (terminalCause ?? "Unknown")
                    + "; Health=" + healthBefore + "->" + terminalHealth
                    + "; FatalVitalHits=" + state.FatalVitalHits
                    + "; RequiredVitalHits=3"
                    + "; RecoveryAttempt=" + state.InvalidTerminalRecoveryCount
                    + "; SameEntity=" + (suspect.Exists()
                        && suspect.Handle == suspectHandle)
                    + "; Failure=" + failure);
                return false;
            }
        }

        private static bool IsFiniteDispatchPosition(Vector3 position)
        {
            const float maximumCoordinateMagnitude = 10000f;
            return position != Vector3.Zero
                && !float.IsNaN(position.X) && !float.IsInfinity(position.X)
                && !float.IsNaN(position.Y) && !float.IsInfinity(position.Y)
                && !float.IsNaN(position.Z) && !float.IsInfinity(position.Z)
                && Math.Abs(position.X) <= maximumCoordinateMagnitude
                && Math.Abs(position.Y) <= maximumCoordinateMagnitude
                && Math.Abs(position.Z) <= maximumCoordinateMagnitude;
        }

        private static bool IsSupportedDispatchRecoveryPosition(
            Ped suspect,
            Vector3 position)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.Health <= 0 || !IsFiniteDispatchPosition(position))
                return false;
            try
            {
                float heightAboveGround = suspect.HeightAboveGround;
                return !suspect.IsInAir
                    && !float.IsNaN(heightAboveGround)
                    && !float.IsInfinity(heightAboveGround)
                    && heightAboveGround >= -0.75f
                    && heightAboveGround <= 1.5f
                    && suspect.Position.DistanceTo(position) <= 1.25f;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsConfirmedDispatchFatalVitalHit(
            bool damageEventConfirmed,
            ControlledDamageKind damageKind,
            int rawBone,
            bool policePlayerWeaponHit)
        {
            return damageEventConfirmed
                && policePlayerWeaponHit
                && damageKind == ControlledDamageKind.Weapon
                && IsDispatchFatalVitalZone(ClassifyDispatchHitZone(rawBone));
        }

        internal static bool IsConfirmedDispatchTerminalWeaponHit(
            bool engineTerminal,
            int healthBefore,
            int currentHealth,
            int observedHealthLoss,
            bool damageEventConfirmed,
            ControlledDamageKind damageKind,
            bool fatalThresholdReached)
        {
            // A weapon event may explain an engine-terminal transition only
            // after the separately tracked fatal threshold was reached. A
            // single lethal health delta cannot bypass the documented hit
            // count, and this helper never writes health to zero.
            return engineTerminal
                && healthBefore > 0
                && currentHealth <= 0
                && observedHealthLoss >= healthBefore
                && damageEventConfirmed
                && damageKind == ControlledDamageKind.Weapon
                && fatalThresholdReached;
        }

        internal static int GetDispatchProtectedHealthAfterDamage(
            int healthBefore,
            int currentHealth,
            int currentMaximumHealth,
            int woundedHealthReserve,
            bool damageEventConfirmed,
            bool fatalThresholdReached,
            bool suspectDead)
        {
            if (suspectDead || fatalThresholdReached || healthBefore <= 0
                || currentMaximumHealth <= 0 || currentHealth >= healthBefore)
                return currentHealth;

            if (!damageEventConfirmed)
                return Math.Max(1, Math.Min(healthBefore, currentMaximumHealth));

            int reserve = Math.Max(1, Math.Min(
                currentMaximumHealth, woundedHealthReserve));
            return currentHealth < reserve ? reserve : currentHealth;
        }

        internal static bool IsDispatchFatalThresholdReached(
            bool fatalVitalWeaponHit,
            bool wasDowned,
            int fatalVitalHits,
            int fatalDownedHits,
            int cumulativeThreshold,
            int repeatedDownedThreshold,
            out string reason)
        {
            reason = string.Empty;
            if (!fatalVitalWeaponHit)
                return false;

            // The documented outcome is fixed: three fresh, unique and
            // confirmed Police Player weapon hits to head, face, neck, or
            // chest. Settings and downed-state repeats cannot lower it.
            if (fatalVitalHits >= 3)
            {
                reason = "ConfirmedVitalWeaponHitThreshold";
                return true;
            }
            return false;
        }

        internal static bool IsAuthorizedDispatchDefeatOwner(
            DispatchDamageAttackerClass ownerClass)
        {
            return ownerClass == DispatchDamageAttackerClass.PolicePlayer
                || ownerClass == DispatchDamageAttackerClass.ProjectBackupOfficer;
        }

        private static int SaturatingAdd(int left, int right)
        {
            long sum = (long)left + right;
            return sum > int.MaxValue ? int.MaxValue
                : sum < int.MinValue ? int.MinValue : (int)sum;
        }

        private static bool TryReadDispatchDamageRecord(
            Ped suspect,
            out int weaponHash,
            out int attackerHandle,
            out int gameTime,
            out Entity attacker)
        {
            weaponHash = 0;
            attackerHandle = 0;
            gameTime = 0;
            attacker = null;
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
                attacker = latest.Attacker;
                attackerHandle = attacker == null ? 0 : attacker.Handle;
                gameTime = latest.GameTime;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private DispatchDamageAttackerClass ClassifyDispatchDamageAttacker(
            Entity attacker,
            int attackerHandle,
            Ped player,
            LSPDDispatchEvent incident)
        {
            if (player != null && player.Exists() && attackerHandle > 0
                && attackerHandle == player.Handle)
                return DispatchDamageAttackerClass.PolicePlayer;

            Ped attackerPed = attacker as Ped;
            if ((attackerPed == null || !attackerPed.Exists()) && attackerHandle > 0)
            {
                try { attackerPed = Entity.FromHandle(attackerHandle) as Ped; }
                catch { attackerPed = null; }
            }

            if (attackerPed == null || !attackerPed.Exists())
            {
                if (attacker != null && attacker.Exists())
                {
                    if (_backup != null && _backup.ProtectedVehicles.Any(vehicle =>
                        vehicle != null && vehicle.Exists()
                        && vehicle.Handle == attacker.Handle))
                        return DispatchDamageAttackerClass.ProjectBackupOfficer;
                    if (_policeResponse != null && _policeResponse.ProtectedVehicles.Any(vehicle =>
                        vehicle != null && vehicle.Exists()
                        && vehicle.Handle == attacker.Handle))
                        return DispatchDamageAttackerClass.ProjectPoliceResponseOfficer;
                }
                return DispatchDamageAttackerClass.UnknownExternalActor;
            }

            if (_backup != null && _backup.ProtectedActors.Any(actor =>
                actor != null && actor.Exists() && actor.Handle == attackerPed.Handle))
                return DispatchDamageAttackerClass.ProjectBackupOfficer;
            if (_policeResponse != null && _policeResponse.ProtectedActors.Any(actor =>
                actor != null && actor.Exists() && actor.Handle == attackerPed.Handle))
                return DispatchDamageAttackerClass.ProjectPoliceResponseOfficer;

            if (incident != null && incident.Suspects != null
                && incident.Suspects.Any(actor => actor != null && actor.Exists()
                    && actor.Handle == attackerPed.Handle))
                return DispatchDamageAttackerClass.DispatchSuspect;
            if (incident != null && incident.Victim != null
                && incident.Victim.Exists()
                && incident.Victim.Handle == attackerPed.Handle)
                return DispatchDamageAttackerClass.DispatchVictim;
            if (_ambientWorld != null
                && _ambientWorld.IsDispatchAmbientWorldSceneActor(incident, attackerPed))
                return DispatchDamageAttackerClass.AmbientWorldSceneActor;

            bool gangModel = false;
            try
            {
                gangModel = (_gangData != null && _gangData.IsKnownGangMember(attackerPed))
                    || LSPDVanillaGangResponse.IsVanillaGangHash(attackerPed.Model.Hash);
            }
            catch { }
            if (gangModel)
                return DispatchDamageAttackerClass.UnrelatedGangModelActor;

            try
            {
                if (attackerPed.IsInPoliceVehicle
                    || attackerPed.RelationshipGroup.Hash
                        == unchecked((int)(uint)RelationshipGroupHash.Cop)
                    || LSPDPoliceResponse.IsKnownPoliceAuthorityModelHash(
                        attackerPed.Model.Hash))
                    return DispatchDamageAttackerClass.UnrelatedVanillaPolice;
            }
            catch
            {
                return DispatchDamageAttackerClass.UnknownExternalActor;
            }

            return DispatchDamageAttackerClass.UnrelatedCivilian;
        }

        private bool IsDocumentedProjectDamageBranchAuthorized(
            LSPDDispatchEvent incident,
            Ped target,
            Ped attacker,
            Entity damageSource,
            DispatchDamageAttackerClass attackerClass,
            bool damageEventConfirmed)
        {
            if (!damageEventConfirmed || incident == null || target == null
                || !target.Exists() || attacker == null || !attacker.Exists()
                || attacker.Handle == target.Handle)
                return false;

            bool attackerEngagingTarget = false;
            try
            {
                attackerEngagingTarget = attacker.IsInCombatAgainst(target)
                    || target.IsInCombatAgainst(attacker);
            }
            catch { }
            if (!attackerEngagingTarget)
                return false;

            // Requested Backup remains a recognized Dispatch actor when it is
            // physically intervening on this exact assigned suspect, even if
            // a Player Secure command has since changed the response label to
            // "comply". This remains stricter than model or combat detection.
            if (attackerClass == DispatchDamageAttackerClass.ProjectBackupOfficer)
            {
                string interventionEvidence;
                return _backup != null
                    && damageSource != null && damageSource.Exists()
                    && _backup.IsCurrentDispatchSupportAttacker(
                        incident, damageSource, attacker)
                    && _backup.IsCurrentDispatchPhysicalIntervention(
                        incident, target, out interventionEvidence);
            }

            if (!IsAggressiveDispatchResponseBranch(incident, target.Handle))
                return false;

            if (attackerClass == DispatchDamageAttackerClass.DispatchSuspect)
                return IsAggressiveDispatchResponseBranch(incident, attacker.Handle);

            if (attackerClass == DispatchDamageAttackerClass.ProjectPoliceResponseOfficer)
                return _policeResponse != null
                    && _policeResponse.IsCurrentDispatchDamageSource(
                        incident, attacker, target);

            return false;
        }

        private static bool IsAggressiveDispatchResponseBranch(
            LSPDDispatchEvent incident,
            int suspectHandle)
        {
            if (incident == null || suspectHandle <= 0
                || incident.SuspectResponses == null)
                return false;
            string response;
            return incident.SuspectResponses.TryGetValue(suspectHandle, out response)
                && (string.Equals(response, "resist", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(response, "attack", StringComparison.OrdinalIgnoreCase));
        }

        private void LogDispatchInjurySummary(
            Ped suspect,
            LSPDSuspectInjuryState state,
            string previousState,
            int healthAfter,
            DateTime now,
            bool terminal)
        {
            if (suspect == null || state == null || state.PendingDamageSamples <= 0)
                return;
            LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_SUMMARY",
                "Incident=" + state.OccurrenceId
                + "; Ped=" + suspect.Handle
                + "; PreviousState=" + (previousState ?? "Unknown")
                + "; Health=" + state.PendingHealthBefore + "->" + healthAfter
                + "; MaximumHealth=" + state.LastMaximumHealth
                + "; ObservedLoss=" + state.PendingObservedHealthLoss
                + "; Samples=" + state.PendingDamageSamples
                + "; ConfirmedEvents=" + state.PendingConfirmedDamageEvents
                + "; UnconfirmedSamples=" + state.PendingUnconfirmedDamageSamples
                + "; FloorApplications=" + state.PendingFloorApplications
                + "; LatestDelta=" + state.PendingLatestDamageDelta
                + "; DamageKind=" + (state.LastDamageKind ?? "Unknown")
                + "; Zone=" + (state.LastDamageZone ?? "Unknown")
                + "; RecordAvailable=" + state.LastDamageRecordAvailable
                + "; RecordFresh=" + state.LastDamageRecordFresh
                + "; RecordDuplicate=" + state.LastDamageRecordDuplicate
                + "; RecordAgeMs=" + state.LastDamageRecordAgeMilliseconds
                + "; Weapon=" + state.LastDamageWeaponHash
                + "; Attacker=" + state.LastDamageAttackerHandle
                + "; AttackerClass=" + (state.LastDamageAttackerClass ?? "UnknownExternalActor")
                + "; GameTime=" + state.LastDamageGameTime
                + "; RawBone=" + state.LastRawDamageBone
                + "; EffectiveBone=" + state.LastEffectiveDamageBone
                + "; BaselineAvailable=" + state.DamageAttributionBaselineAvailable
                + "; NativeAttackerCheck=" + state.LastNativeDamageCheckAvailable
                + "; NativeConfirmsAttacker=" + state.LastNativeReportsDamageByAttacker
                + "; Wounded=" + state.Wounded
                + "; ArrestableFloor=" + state.ArrestableWoundedHealthFloor
                + "; WoundedHealthReserve=" + state.DispatchWoundedHealthReserve
                + "; WoundedHealthCorrections=" + state.WoundedHealthCorrections
                + "; FatalVitalHits=" + state.FatalVitalHits
                + "; HeadHits=" + state.HeadHits
                + "; FaceHits=" + state.FaceHits
                + "; NeckHits=" + state.NeckHits
                + "; ChestHits=" + state.ChestHits
                + "; StomachHits=" + state.StomachHits
                + "; CriticalHitGuardReassertions=" + state.CriticalHitGuardReassertions
                + "; Terminal=" + terminal
                + "; HealthWrittenToZero=" + state.FatalOutcomeExpected);
            state.PendingDamageSamples = 0;
            state.PendingObservedHealthLoss = 0;
            state.PendingConfirmedDamageEvents = 0;
            state.PendingUnconfirmedDamageSamples = 0;
            state.PendingFloorApplications = 0;
            state.PendingLatestDamageDelta = 0;
            state.PendingHealthBefore = healthAfter;
            state.PendingDamageSummaryStartedAt = now;
        }

        private string CaptureDispatchFatalEvidence(
            Ped suspect,
            LSPDSuspectInjuryState state,
            Ped player,
            int healthBefore,
            int healthAfter,
            int maximumHealthBefore,
            int maximumHealthAfter,
            bool damageRecordAvailable,
            bool damageRecordFresh,
            bool damageEventConfirmed,
            int damageWeaponHash,
            int damageAttackerHandle,
            int damageGameTime,
            int damageRecordAgeMilliseconds,
            int rawBone,
            int effectiveBone,
            string zone,
            string fatalThresholdReason,
            out bool causeConfirmed,
            out bool deathSourceMatchesFreshDamageAttacker)
        {
            causeConfirmed = false;
            deathSourceMatchesFreshDamageAttacker = false;
            int deathSourceHandle = 0;
            int causeOfDeathHash = 0;
            int timeOfDeath = 0;
            int entityType = 0;
            int suspectRelationshipGroup = 0;
            int sourceRelationshipGroup = 0;
            int relationshipToSuspect = -1;
            int relationshipToPlayer = -1;
            int damageAttackerRelationshipGroup = 0;
            int damageAttackerRelationshipToSuspect = -1;
            int damageAttackerRelationshipToPlayer = -1;
            bool collisionDisabled = false;
            bool collisionQueryAvailable = false;
            string sourceExists = "unknown";
            string sourceSpeed = "unknown";
            string sourceVehicle = "none";
            int sourceVehicleDriverHandle = 0;
            int sourceVehicleDriverRelationshipGroup = 0;
            int sourceVehicleDriverTempActionTask = int.MinValue;
            int sourceVehicleDriverLongRangeTask = int.MinValue;
            int sourcePedCombatTask = int.MinValue;
            int sourcePedArrestTask = int.MinValue;
            int damageAttackerCombatTask = int.MinValue;
            int damageAttackerArrestTask = int.MinValue;
            int damageAttackerModelHash = 0;
            string sampledDamageAttackerExists = damageAttackerHandle == 0
                ? "none" : "unknown";

            try { deathSourceHandle = Function.Call<int>(Hash.GET_PED_SOURCE_OF_DEATH, suspect); }
            catch { }
            try { causeOfDeathHash = Function.Call<int>(Hash.GET_PED_CAUSE_OF_DEATH, suspect); }
            catch { }
            try { timeOfDeath = Function.Call<int>(Hash.GET_PED_TIME_OF_DEATH, suspect); }
            catch { }
            try
            {
                collisionDisabled = Function.Call<bool>(
                    Hash.GET_ENTITY_COLLISION_DISABLED, suspect);
                collisionQueryAvailable = true;
            }
            catch { }
            try
            {
                suspectRelationshipGroup = Function.Call<int>(
                    Hash.GET_PED_RELATIONSHIP_GROUP_HASH, suspect);
            }
            catch { }
            int playerTaskArrest = ReadDispatchTaskStatus(player, Hash.TASK_ARREST_PED);
            Entity source = null;
            if (deathSourceHandle != 0)
            {
                try
                {
                    source = Entity.FromHandle(deathSourceHandle);
                    sourceExists = source != null && source.Exists() ? "true" : "false";
                }
                catch { sourceExists = "unknown"; }
            }
            if (source != null && source.Exists())
            {
                try { entityType = Function.Call<int>(Hash.GET_ENTITY_TYPE, source); }
                catch { }
                Ped sourcePed = source as Ped;
                if (sourcePed != null && sourcePed.Exists())
                {
                    try
                    {
                        sourceRelationshipGroup = Function.Call<int>(
                            Hash.GET_PED_RELATIONSHIP_GROUP_HASH, sourcePed);
                        relationshipToSuspect = Function.Call<int>(
                            Hash.GET_RELATIONSHIP_BETWEEN_PEDS, sourcePed, suspect);
                        if (player != null && player.Exists())
                            relationshipToPlayer = Function.Call<int>(
                                Hash.GET_RELATIONSHIP_BETWEEN_PEDS, sourcePed, player);
                    }
                    catch { }
                    sourcePedCombatTask = ReadDispatchTaskStatus(
                        sourcePed, Hash.TASK_COMBAT_PED);
                    sourcePedArrestTask = ReadDispatchTaskStatus(
                        sourcePed, Hash.TASK_ARREST_PED);
                }
                Vehicle sourceAsVehicle = source as Vehicle;
                if (sourceAsVehicle != null && sourceAsVehicle.Exists())
                {
                    sourceVehicle = sourceAsVehicle.Handle.ToString(CultureInfo.InvariantCulture);
                    try
                    {
                        sourceSpeed = Function.Call<float>(
                            Hash.GET_ENTITY_SPEED, sourceAsVehicle)
                            .ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    catch { }
                    try
                    {
                        Ped driver = sourceAsVehicle.Driver;
                        if (driver != null && driver.Exists())
                        {
                            sourceVehicleDriverHandle = driver.Handle;
                            sourceVehicleDriverRelationshipGroup = Function.Call<int>(
                                Hash.GET_PED_RELATIONSHIP_GROUP_HASH, driver);
                            sourceVehicleDriverTempActionTask = ReadDispatchTaskStatus(
                                driver, Hash.TASK_VEHICLE_TEMP_ACTION);
                            sourceVehicleDriverLongRangeTask = ReadDispatchTaskStatus(
                                driver, Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE);
                        }
                    }
                    catch { }
                }
            }
            Entity sampledDamageAttacker = null;
            if (damageAttackerHandle != 0)
            {
                try
                {
                    sampledDamageAttacker = Entity.FromHandle(damageAttackerHandle);
                    if (sampledDamageAttacker != null && sampledDamageAttacker.Exists())
                    {
                        sampledDamageAttackerExists = "true";
                        try { damageAttackerModelHash = sampledDamageAttacker.Model.Hash; }
                        catch { damageAttackerModelHash = 0; }
                        Ped attackerPed = sampledDamageAttacker as Ped;
                        if (attackerPed != null && attackerPed.Exists())
                        {
                            damageAttackerRelationshipGroup = Function.Call<int>(
                                Hash.GET_PED_RELATIONSHIP_GROUP_HASH, attackerPed);
                            damageAttackerRelationshipToSuspect = Function.Call<int>(
                                Hash.GET_RELATIONSHIP_BETWEEN_PEDS, attackerPed, suspect);
                            if (player != null && player.Exists())
                                damageAttackerRelationshipToPlayer = Function.Call<int>(
                                    Hash.GET_RELATIONSHIP_BETWEEN_PEDS, attackerPed, player);
                            damageAttackerCombatTask = ReadDispatchTaskStatus(
                                attackerPed, Hash.TASK_COMBAT_PED);
                            damageAttackerArrestTask = ReadDispatchTaskStatus(
                                attackerPed, Hash.TASK_ARREST_PED);
                        }
                        Vehicle attackerVehicle = sampledDamageAttacker as Vehicle;
                        if (attackerVehicle != null && attackerVehicle.Exists()
                            && sourceVehicleDriverHandle == 0)
                        {
                            Ped driver = attackerVehicle.Driver;
                            if (driver != null && driver.Exists())
                            {
                                sourceVehicleDriverHandle = driver.Handle;
                                sourceVehicleDriverRelationshipGroup = Function.Call<int>(
                                    Hash.GET_PED_RELATIONSHIP_GROUP_HASH, driver);
                                sourceVehicleDriverTempActionTask = ReadDispatchTaskStatus(
                                    driver, Hash.TASK_VEHICLE_TEMP_ACTION);
                                sourceVehicleDriverLongRangeTask = ReadDispatchTaskStatus(
                                    driver, Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE);
                            }
                        }
                    }
                    else
                    {
                        sampledDamageAttackerExists = "false";
                    }
                }
                catch { }
            }
            bool engineReportsTerminal = suspect.IsDead || suspect.Health <= 0;
            deathSourceMatchesFreshDamageAttacker = engineReportsTerminal
                && deathSourceHandle != 0
                && deathSourceHandle == damageAttackerHandle
                && damageRecordAvailable
                && damageRecordFresh
                && causeOfDeathHash != 0
                && sourceExists == "true";
            bool sampledAttackerMatchesDeathSource = damageEventConfirmed
                && damageRecordFresh && !state.LastDamageRecordDuplicate
                && damageAttackerHandle != 0
                && deathSourceHandle == damageAttackerHandle;
            bool engineTerminalDamageConfirmed = engineReportsTerminal
                && deathSourceHandle != 0
                && deathSourceHandle != suspect.Handle
                && sourceExists == "true"
                && causeOfDeathHash != 0
                && sampledAttackerMatchesDeathSource;
            causeConfirmed = engineTerminalDamageConfirmed;

            bool playerHandcuffActive = _incident != null
                && _incident.PlayerHandcuffInProgress
                && _incident.PlayerHandcuffTargetHandle == suspect.Handle;
            bool backupOperationPresent;
            BackupArrestOperation backupOperation;
            backupOperationPresent = _backupArrestOperations.TryGetValue(
                suspect.Handle, out backupOperation);
            string backupOwner = backupOperationPresent && backupOperation != null
                ? "Officer=" + (backupOperation.Officer == null
                    ? 0 : backupOperation.Officer.Handle)
                    + ",Paired=" + backupOperation.PairedAnimationStarted
                    + ",Fallback=" + backupOperation.NativeTaskFallbackStarted
                : "none";
            string custodyOwner = _incident == null ? "none"
                : (_incident.HasConvoyCustodyHandoff ? "Convoy"
                    : (_incident.OwnedByDispatch ? "Dispatch" : "external"));

            return "; DeathSourceHandle=" + deathSourceHandle
                + "; DeathSourceEntityExists=" + sourceExists
                + "; DeathSourceEntityType=" + entityType
                + "; CauseOfDeathHash=" + causeOfDeathHash
                + "; TimeOfDeath=" + timeOfDeath
                + "; DeathSourceVehicle=" + sourceVehicle
                + "; DeathSourceVehicleSpeed=" + sourceSpeed
                + "; DeathSourceVehicleDriver=" + sourceVehicleDriverHandle
                + "; DeathSourceVehicleDriverRelationshipGroup="
                + sourceVehicleDriverRelationshipGroup
                + "; DeathSourceVehicleDriverTaskTempAction="
                + sourceVehicleDriverTempActionTask
                + "; DeathSourceVehicleDriverTaskLongRange="
                + sourceVehicleDriverLongRangeTask
                + "; VictimCollisionDisabled="
                + (collisionQueryAvailable ? collisionDisabled.ToString() : "unknown")
                + "; SuspectRelationshipGroup=" + suspectRelationshipGroup
                + "; SourceRelationshipGroup=" + sourceRelationshipGroup
                + "; SourceRelationshipToSuspect=" + relationshipToSuspect
                + "; SourceRelationshipToPlayer=" + relationshipToPlayer
                + "; SourcePedTaskCombat=" + sourcePedCombatTask
                + "; SourcePedTaskArrest=" + sourcePedArrestTask
                + "; DamageAttackerRelationshipGroup="
                + damageAttackerRelationshipGroup
                + "; DamageAttackerRelationshipToSuspect="
                + damageAttackerRelationshipToSuspect
                + "; DamageAttackerRelationshipToPlayer="
                + damageAttackerRelationshipToPlayer
                + "; DamageAttackerTaskCombat=" + damageAttackerCombatTask
                + "; DamageAttackerTaskArrest=" + damageAttackerArrestTask
                + "; DamageAttackerEntityExists=" + sampledDamageAttackerExists
                + "; DamageAttackerModelHash=" + damageAttackerModelHash
                + "; DeathSourceMatchesFreshDamageAttacker="
                + deathSourceMatchesFreshDamageAttacker
                + "; SuspectTaskArrest=" + ReadDispatchTaskStatus(suspect, Hash.TASK_ARREST_PED)
                + "; SuspectTaskCombat=" + ReadDispatchTaskStatus(suspect, Hash.TASK_COMBAT_PED)
                + "; SuspectTaskWrithe=" + ReadDispatchTaskStatus(suspect, Hash.TASK_WRITHE)
                + "; SuspectTaskHandsUp=" + ReadDispatchTaskStatus(suspect, Hash.TASK_HANDS_UP)
                + "; SuspectTaskFlee=" + ReadDispatchTaskStatus(suspect, Hash.TASK_SMART_FLEE_PED)
                + "; PlayerTaskArrest=" + playerTaskArrest
                + "; PlayerHandcuffActive=" + playerHandcuffActive
                + "; PhysicalCuffs=" + IsPedPhysicallyCuffed(suspect)
                + "; BackupArrestOwner=" + backupOwner
                + "; CustodyOwner=" + custodyOwner
                + "; Health=" + healthBefore + "->" + healthAfter
                + "; MaxHealth=" + maximumHealthBefore + "->" + maximumHealthAfter
                + "; DamageRecordAvailable=" + damageRecordAvailable
                + "; DamageRecordFresh=" + damageRecordFresh
                + "; DamageRecordDuplicate="
                + (state == null ? "unknown" : state.LastDamageRecordDuplicate.ToString())
                + "; DamageRecordAgeMs=" + damageRecordAgeMilliseconds
                + "; DamageEventConfirmed=" + damageEventConfirmed
                + "; SampledAttackerMatchesDeathSource="
                + sampledAttackerMatchesDeathSource
                + "; EngineTerminalDamageConfirmed="
                + engineTerminalDamageConfirmed
                + "; DamageWeapon=" + damageWeaponHash
                + "; DamageAttacker=" + damageAttackerHandle
                + "; DamageGameTime=" + damageGameTime
                + "; RawBone=" + rawBone
                + "; EffectiveBone=" + effectiveBone
                + "; Zone=" + (zone ?? "Unknown")
                + "; FatalThreshold=" + (fatalThresholdReason ?? string.Empty)
                + "; FatalCauseConfirmed=" + causeConfirmed
                + "; ExpectedProofOwner=temporary arrest/secured-custody transition only"
                + "; InjuryBaseline=" + (state == null
                    ? "unavailable" : state.DamageAttributionBaselineStatus)
                + "; NearbyVehicleEvidence="
                + CaptureDispatchNearbyVehicleImpactEvidence(suspect);
        }

        private static string CaptureDispatchNearbyVehicleImpactEvidence(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return "unavailable";
            Vehicle[] nearbyVehicles;
            try
            {
                nearbyVehicles = World.GetNearbyVehicles(
                    suspect, DispatchCollisionEvidenceRadius);
            }
            catch
            {
                return "probe-failed";
            }

            if (nearbyVehicles == null || nearbyVehicles.Length == 0)
                return "none";

            List<string> evidence = new List<string>(
                MaximumDispatchCollisionEvidenceVehicles);
            foreach (Vehicle vehicle in nearbyVehicles
                .Where(value => value != null && value.Exists())
                .OrderBy(value => value.Position.DistanceTo(suspect.Position))
                .Take(MaximumDispatchCollisionEvidenceVehicles))
            {
                string touching = "unknown";
                string collisionDisabled = "unknown";
                try
                {
                    touching = Function.Call<bool>(
                        Hash.IS_ENTITY_TOUCHING_ENTITY, suspect, vehicle)
                        .ToString(CultureInfo.InvariantCulture);
                }
                catch { }
                try
                {
                    collisionDisabled = Function.Call<bool>(
                        Hash.GET_ENTITY_COLLISION_DISABLED, vehicle)
                        .ToString(CultureInfo.InvariantCulture);
                }
                catch { }

                int driverHandle = 0;
                int driverRelationshipGroup = 0;
                int driverTempActionTask = int.MinValue;
                int driverLongRangeTask = int.MinValue;
                try
                {
                    Ped driver = vehicle.Driver;
                    if (driver != null && driver.Exists())
                    {
                        driverHandle = driver.Handle;
                        driverRelationshipGroup = Function.Call<int>(
                            Hash.GET_PED_RELATIONSHIP_GROUP_HASH, driver);
                        driverTempActionTask = ReadDispatchTaskStatus(
                            driver, Hash.TASK_VEHICLE_TEMP_ACTION);
                        driverLongRangeTask = ReadDispatchTaskStatus(
                            driver, Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE);
                    }
                }
                catch { }

                evidence.Add("Vehicle=" + vehicle.Handle
                    + ",Model=" + vehicle.Model.Hash
                    + ",Distance=" + vehicle.Position.DistanceTo(suspect.Position)
                        .ToString("0.0", CultureInfo.InvariantCulture)
                    + ",Speed=" + Math.Abs(vehicle.Speed)
                        .ToString("0.0", CultureInfo.InvariantCulture)
                    + ",TouchingSuspect=" + touching
                    + ",CollisionDisabled=" + collisionDisabled
                    + ",Driver=" + driverHandle
                    + ",DriverRelationshipGroup=" + driverRelationshipGroup
                    + ",DriverTaskTempAction=" + driverTempActionTask
                    + ",DriverTaskLongRange=" + driverLongRangeTask);
            }
            return evidence.Count == 0 ? "none" : string.Join("|", evidence.ToArray());
        }

        private static int ReadDispatchTaskStatus(Ped actor, Hash task)
        {
            if (actor == null || !actor.Exists())
                return int.MinValue;
            try { return Function.Call<int>(Hash.GET_SCRIPT_TASK_STATUS, actor, task); }
            catch { return int.MinValue; }
        }

        private void NotifyOtherLivingDispatchSuspects(string occurrenceId, Ped deceased)
        {
            if (_incident == null || !string.Equals(_incident.Id, occurrenceId,
                StringComparison.Ordinal) || _incident.HasConvoyCustodyHandoff)
                return;
            List<Ped> remaining = LiveSuspects(_incident)
                .Where(value => deceased == null || !value.Exists()
                    || value.Handle != deceased.Handle)
                .ToList();
            if (remaining.Count == 0)
                return;
            SetStateForUnresolvedSuspects(LSPDDispatchState.SuspectResisting);
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
                    return DispatchHitZone.Head;
                case 65068:
                    return DispatchHitZone.Face;
                case 39317:
                    return DispatchHitZone.Neck;
                case 64729:
                case 10706:
                    return DispatchHitZone.Limb;
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
            bool criticalPolicyRestored = false;
            try
            {
                bool custodyOwned = IsPedPhysicallyCuffed(suspect)
                    || IsArrested(_incident, suspect);
                RestoreDispatchVehicleFlightCollisionProtection(
                    suspect, state, "InjuryStateCleanup");
                RestoreDispatchCustodyDamageProtection(suspect, state);
                suspect.CanSufferCriticalHits = state.OriginalCanSufferCriticalHits;
                state.CriticalHitGuardActive = false;
                criticalPolicyRestored = true;
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
            if (criticalPolicyRestored)
                LogRuntime("POLICE_DISPATCH_CRITICAL_HIT_GUARD_RESTORED",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                    + "; RuntimeOwner=" + state.RuntimeOwner
                    + "; OriginalCanSufferCriticalHits="
                    + state.OriginalCanSufferCriticalHits
                    + "; HealthWritten=false; MaximumHealthWritten=false");
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

        private static bool IsDispatchCustodyDamageGuardActive(
            LSPDDispatchEvent incident,
            Ped suspect)
        {
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff)
                return false;

            bool playerArresting = incident.PlayerHandcuffInProgress
                && incident.PlayerHandcuffAnimationStarted
                && incident.PlayerHandcuffTargetHandle == suspect.Handle;
            bool backupArresting = incident.BackupArrestInProgressSuspectHandles
                .Contains(suspect.Handle);
            bool physicallySecured = IsArrested(incident, suspect)
                && IsPedPhysicallyCuffed(suspect);
            return playerArresting || backupArresting || physicallySecured;
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
            incident.PlayerCommandedSecureSuspectHandles.Remove(suspect.Handle);
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

        internal IList<LSPDSuspectInjuryState> CreateCustodyInjuryHandoff(Ped prisoner)
        {
            if (_incident == null || prisoner == null || !prisoner.Exists()
                || prisoner.IsDead || prisoner.Health <= 0
                || !CurrentArrestedSuspects.Any(value =>
                    value.Handle == prisoner.Handle))
                return new List<LSPDSuspectInjuryState>();

            CaptureDispatchSuspectInjuryState(prisoner, _incident);
            MaintainDispatchSuspectInjury(
                prisoner, DateTime.UtcNow, Game.Player.Character);
            LSPDSuspectInjuryState state;
            if (!_suspectInjuries.TryGetValue(prisoner.Handle, out state)
                || state == null || state.Fatal
                || prisoner.IsDead || prisoner.Health <= 0)
                return new List<LSPDSuspectInjuryState>();
            return new List<LSPDSuspectInjuryState> { state };
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
            foreach (Ped prisoner in prisoners)
                _incident.ConvoyCustodySuspectHandles.Add(prisoner.Handle);
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

        internal bool TransferCustodyOwnershipToConvoy(
            Ped prisoner,
            IList<LSPDSuspectInjuryState> adoptedInjuryStates)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || prisoner == null || !prisoner.Exists()
                || prisoner.IsDead || prisoner.Health <= 0
                || !incident.OwnedByDispatch
                || !CurrentArrestedSuspects.Any(value =>
                    value.Handle == prisoner.Handle))
                return false;
            if (incident.ConvoyCustodySuspectHandles.Contains(prisoner.Handle))
                return true;
            if (adoptedInjuryStates == null
                || !adoptedInjuryStates.Any(candidateState => candidateState != null
                    && candidateState.Actor != null && candidateState.Actor.Exists()
                    && candidateState.Actor.Handle == prisoner.Handle))
                return false;

            LSPDSuspectInjuryState state;
            if (!_suspectInjuries.TryGetValue(prisoner.Handle, out state)
                || state == null
                || !adoptedInjuryStates.Any(value => object.ReferenceEquals(value, state)))
                return false;

            state.RuntimeOwner = "Convoy";
            _suspectInjuries.Remove(prisoner.Handle);
            incident.ConvoyCustodySuspectHandles.Add(prisoner.Handle);
            List<Ped> livingTrackedSuspects = TrackedSuspects(incident)
                .Where(value => value != null && value.Exists()
                    && !value.IsDead && value.Health > 0)
                .ToList();
            bool allLivingTransferred = livingTrackedSuspects.Count > 0
                && livingTrackedSuspects.All(value =>
                    incident.ConvoyCustodySuspectHandles.Contains(value.Handle));
            if (allLivingTransferred)
            {
                incident.OwnedByDispatch = false;
                incident.SurrenderRequested = false;
                incident.HasConvoyCustodyHandoff = true;
                CleanupBlip(_sceneBlip);
                CleanupSuspectBlips();
                _sceneBlip = null;
                LogRuntime("POLICE_DISPATCH_CUSTODY_MARKERS_RELEASED",
                    "Dispatch markers released after the final scattered-group prisoner entered Convoy custody.");
            }

            LogRuntime("POLICE_DISPATCH_INJURY_OWNERSHIP_TRANSFERRED",
                "Incident=" + state.OccurrenceId
                + "; Ped=" + prisoner.Handle
                + "; From=Dispatch; To=Convoy"
                + "; PhysicalSeatConfirmed=true"
                + "; Health=" + prisoner.Health
                + "; MaximumHealth=" + prisoner.MaxHealth
                + "; CumulativeDamage=" + state.DamageTaken
                + "; LastDamageKind=" + (state.LastDamageKind ?? "Unknown")
                + "; Cuffed=" + IsPedPhysicallyCuffed(prisoner)
                + "; AllLivingTransferred=" + allLivingTransferred);
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
                List<Ped> retainedExisting = TrackedSuspects(_incident)
                    .Where(ped => ped != null && ped.Exists())
                    .Concat(valid)
                    .GroupBy(ped => ped.Handle)
                    .Select(group => group.First())
                    .ToList();
                _incident.Suspect = valid[0];
                _incident.Suspects.Clear();
                _incident.Suspects.AddRange(retainedExisting);
                _incident.ArrestedSuspects.Clear();
                _incident.ArrestedSuspects.AddRange(valid);
                _incident.CompliantSuspects.Clear();
                _incident.PlayerCommandedSecureSuspectHandles.Clear();
                _incident.OwnedByDispatch = true;
                _incident.HasConvoyCustodyHandoff = false;
                _incident.ConvoyCustodySuspectHandles.Clear();
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
                        prisoner.CanSufferCriticalHits = false;
                        injuryState.CriticalHitGuardActive =
                            !prisoner.CanSufferCriticalHits;
                        injuryState.ProcessedDamageRecordKeys.Clear();
                        injuryState.ProcessedDamageRecordOrder.Clear();
                        injuryState.DamageAttributionBaselineAvailable =
                            ClearDispatchDamageAttribution(
                                prisoner,
                                out injuryState.DamageAttributionBaselineStatus);
                        _suspectInjuries[prisoner.Handle] = injuryState;
                        if (injuryState.Wounded
                            && prisoner.Health > 0
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
                            + "; MaximumHealthRaised=false"
                            + "; CriticalHitGuardActive="
                            + injuryState.CriticalHitGuardActive
                            + "; FullDamageProof=false"
                            + "; CumulativeDamage=" + injuryState.DamageTaken
                            + "; LastDamageKind=" + (injuryState.LastDamageKind ?? "Unknown"));
                    }

                    // Convoy has just returned this living prisoner to Dispatch.
                    // Reassert the visible cuff idle in this same ownership
                    // transition instead of waiting for the next maintenance tick.
                    MaintainHandcuffedPose(prisoner, DateTime.UtcNow, true);
                }
                SetState(LSPDDispatchState.Arrested);
                SetSuspectBlip(valid[0]);
                CaptureAndSetWaypoint(valid[0].Position);
                Notify("~y~PRISONER CUSTODY~s~\nTransport was interrupted. The arrested suspect group remains secured; request transport again when ready.");
                List<Ped> retainedDead = retainedExisting
                    .Where(ped => ped != null && ped.Exists() && ped.IsDead)
                    .ToList();
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
            if (!string.IsNullOrWhiteSpace(_pendingFootCatalogDefinitionId)
                && _ambientWorld != null)
                _ambientWorld.ClearDispatchFootSearchProgress();
            _pendingFootCatalogDefinitionId = null;
            _pendingFootCatalogBypassesQuietPeriod = false;
            _pendingFootCatalogPlayerPosition = Vector3.Zero;
            ClearBackupArrestOperations(_incident);
            ClearPlayerHandcuffState();
            RestoreAllSuspectInjuryStates();
            CancelPendingOffer("Dispatch was reset.");
            ResetPendingSceneResolution();
            ClearDispatchSceneSearchState();
            if (_incident != null)
            {
                CloseActiveAudio(_incident);
                CleanupIncident(_incident, false);
                _incident = null;
            }
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
                DeleteDeferred(item);
            _deferredCleanup.Clear();
            ClearDispatchSceneActorObservations();
            ClearManagedSuspectPresenceObservations();
            ReleaseUnusedDispatchInteriorPins("DispatchAuthorityReset");
            _dispatchLocationRepeatFallbackActive = false;
            CleanupBlipsAndWaypoint();
            _cooldownUntil = DateTime.MinValue;
            _nextOfferAt = DateTime.MinValue;
            ClearEmptyLocationCandidateSearch();
            _manualRequestRetryAt = DateTime.MinValue;
            _nextMaintenance = DateTime.MinValue;
            _nextInteriorPinMaintenanceAt = DateTime.MinValue;
            _nextDispatchSceneActorObservationAt = DateTime.MinValue;
            _playerHandcuffHoldUntil = DateTime.MinValue;
            _playerHandcuffAttempts = 0;
            _patrolSessionStarted = false;
            _acceptKeyDown = false;
            _rejectKeyDown = false;
            _investigateKeyDown = false;
            _secureKeyDown = false;
            _lastCompliantTaskAt.Clear();
            ClearSecuredPoseTracking();
        }

        private void ResetPendingSceneResolution(
            AmbientDispatchSceneResolution preserveScene = null)
        {
            _pendingSceneResolutionDefinitionIds.Clear();
            _pendingSceneResolutionInteriorIds.Clear();
            _pendingPlayerRadiusSceneCandidates.Clear();
            _pendingSceneResolutionRetryCount = 0;
            _pendingSceneResolutionStartedAt = DateTime.MinValue;
            _manualRequestRetryAt = DateTime.MinValue;
            ClearDebugTransition("POLICE_DISPATCH_SCENE_RESOLUTION_PENDING");
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
            foreach (int pendingInteriorId in _pendingSceneResolutionInteriorIds)
                if (pendingInteriorId != 0)
                    retainedInteriorIds.Add(pendingInteriorId);
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
                    if (ResolveDisarmedVehiclePursuitStop(
                        incident, suspect, player, vehicle, now))
                        return true;
                    bool authoredVehicleStopResponse = IsVehicleEntryActivity(incident.VehicleActivity)
                        && incident.SuspectResponseBranches != null
                        && incident.SuspectResponseBranches.Count > 0;
                    if (!authoredVehicleStopResponse
                        && player != null && player.Exists()
                        && player.Position.DistanceTo(suspect.Position) <= ArrestRadius + 4f
                        && !IsDownedOrInjured(suspect)
                        && !IsCompliant(incident, suspect))
                    {
                        OrderSuspectToComply(suspect, player, false, false);
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
                LSPDSuspectInjuryState injuryState;
                bool hasInterventionState = _suspectInjuries.TryGetValue(
                    pedHandle, out injuryState) && injuryState != null;
                int interventionAgeMilliseconds = hasInterventionState
                    && injuryState.LastPursuitInterventionAt != DateTime.MinValue
                    ? (int)Math.Max(0, (now
                        - injuryState.LastPursuitInterventionAt).TotalMilliseconds)
                    : -1;
                bool playerVehicleIntervention = hasInterventionState
                    && injuryState.LastPursuitInterventionVehicleHandle
                        == vehicle.Handle
                    && interventionAgeMilliseconds >= 0
                    && interventionAgeMilliseconds
                        <= DispatchPursuitInterventionEvidenceMilliseconds;
                string interventionCause = playerVehicleIntervention
                    ? injuryState.LastPursuitInterventionCause ?? "Unknown"
                    : "None";
                int interventionSourceHandle = playerVehicleIntervention
                    ? injuryState.LastPursuitInterventionSourceHandle : 0;
                if (!collisionDamaged && !undriveable
                    && !playerVehicleIntervention)
                {
                    incident.PursuitVehicleStoppedAt.Remove(pedHandle);
                    if (hasInterventionState)
                    {
                        injuryState.LastPursuitInterventionAt = DateTime.MinValue;
                        injuryState.LastPursuitInterventionVehicleHandle = 0;
                        injuryState.LastPursuitInterventionSourceHandle = 0;
                        injuryState.LastPursuitInterventionCause = null;
                    }
                    return false;
                }

                incident.PursuitVehicleExitRequestedHandles.Add(pedHandle);
                string vehicleDamageSourceEvidence =
                    string.Equals(interventionCause, "None", StringComparison.Ordinal)
                        && (collisionDamaged || undriveable)
                    ? DescribeDispatchVehicleDamageSources(vehicle, suspect, player)
                    : "NotQueried; PlayerVehicleIntervention=" + playerVehicleIntervention;
                LogRuntime("POLICE_DISPATCH_PURSUIT_STOPPED_VEHICLE_EXIT_REQUESTED",
                    "Incident=" + incident.Id + "; Ped=" + pedHandle
                    + "; Vehicle=" + vehicle.Handle
                    + "; Speed=" + speed.ToString("0.0")
                    + "; HealthBaseline=" + baselineHealth
                    + "; HealthNow=" + vehicle.Health
                    + "; CollisionDamaged=" + collisionDamaged
                    + "; Undriveable=" + undriveable
                    + "; PlayerVehicleIntervention=" + playerVehicleIntervention
                    + "; InterventionCause=" + interventionCause
                    + "; InterventionSource=" + interventionSourceHandle
                    + "; InterventionAgeMilliseconds="
                    + interventionAgeMilliseconds
                    + "; VehicleDamageSourceEvidence=" + vehicleDamageSourceEvidence
                    + "; ProximityAloneAccepted=false");
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

        private bool ResolveDisarmedVehiclePursuitStop(
            LSPDDispatchEvent incident,
            Ped suspect,
            Ped player,
            Vehicle vehicle,
            DateTime now)
        {
            if (incident == null || suspect == null || !suspect.Exists()
                || vehicle == null || !vehicle.Exists()
                || !string.Equals(incident.IncidentType, "vehicle_pursuit",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            LSPDSuspectInjuryState injuryState;
            if (!_suspectInjuries.TryGetValue(suspect.Handle, out injuryState)
                || injuryState == null || !injuryState.WeaponDisabledByInjury)
                return false;
            if (!ShouldResolveDisarmedVehiclePursuitStop(
                string.Equals(incident.IncidentType, "vehicle_pursuit",
                    StringComparison.OrdinalIgnoreCase),
                incident.PursuitVehicleStoppedAt.ContainsKey(suspect.Handle)
                    && incident.PursuitVehicleExitRequestedHandles.Contains(
                        suspect.Handle),
                !suspect.IsInVehicle(), injuryState.WeaponDisabledByInjury,
                !suspect.IsDead && suspect.Health > 0))
                return false;
            if (injuryState.VehiclePursuitDisarmedOutcomeResolved)
                return true;

            injuryState.VehiclePursuitDisarmedOutcomeResolved = true;
            bool playerClose = player != null && player.Exists() && !player.IsDead
                && player.Position.DistanceTo(suspect.Position) <= ArrestRadius + 4f;
            bool downed = IsLivingDowned(suspect) || IsLivingInWrithe(suspect);
            bool handsUpAssigned = false;
            try
            {
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                if (playerClose && !downed)
                {
                    suspect.Task.ClearAll();
                    suspect.Task.HandsUp(15000);
                    suspect.Task.LookAt(player, 2500);
                    handsUpAssigned = true;
                    incident.SuspectResponses[suspect.Handle] = "comply";
                    if (incident.CompliantSuspects != null
                        && !incident.CompliantSuspects.Any(value => value != null
                        && value.Exists() && value.Handle == suspect.Handle))
                        incident.CompliantSuspects.Add(suspect);
                    incident.PursuitContainedSuspectHandles.Add(suspect.Handle);
                    incident.SurrenderRequested = true;
                    incident.SurrenderRequestedAt = now;
                }
                else
                {
                    // A disarmed driver who is wounded or not yet confronted
                    // leaves the vehicle-pursuit target set as wounded and
                    // arrestable. Do not label an idle hand gesture as resistance.
                    incident.SuspectResponses[suspect.Handle] = "wounded_arrestable";
                    incident.PursuitContainedSuspectHandles.Add(suspect.Handle);
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_DISARMED_PURSUIT_STOP_TASK_FAILED", ex);
                incident.SuspectResponses[suspect.Handle] = "wounded_arrestable";
                incident.PursuitContainedSuspectHandles.Add(suspect.Handle);
                handsUpAssigned = false;
            }

            SetStateForUnresolvedSuspects(handsUpAssigned
                ? LSPDDispatchState.SuspectCompliant
                : LSPDDispatchState.Investigating);
            LogRuntime("POLICE_DISPATCH_VEHICLE_PURSUIT_DISARMED_STOP_OUTCOME",
                "Incident=" + incident.Id
                + "; Ped=" + suspect.Handle
                + "; Vehicle=" + vehicle.Handle
                + "; WeaponDisabledByInjury=true"
                + "; PreviousResponse=flee"
                + "; Outcome=" + (handsUpAssigned
                    ? "WoundedSurrender" : "WoundedArrestable")
                + "; HandsUpTask=" + handsUpAssigned
                + "; Downed=" + downed
                + "; PlayerDistance=" + (player == null || !player.Exists()
                    ? "unknown" : player.Position.DistanceTo(suspect.Position)
                        .ToString("0.0", CultureInfo.InvariantCulture))
                + "; Health=" + suspect.Health
                + "; MaximumHealth=" + suspect.MaxHealth
                + "; Arrestable=true; WeaponReissued=false");
            if (handsUpAssigned)
                Notify("~b~POLICE DISPATCH~s~\nThe wounded, unarmed driver is surrendering. Secure the suspect now.");
            return true;
        }

        internal static bool ShouldResolveDisarmedVehiclePursuitStop(
            bool vehiclePursuit,
            bool physicalStopConfirmed,
            bool driverExited,
            bool weaponDisabledByInjury,
            bool suspectAlive)
        {
            return vehiclePursuit && physicalStopConfirmed && driverExited
                && weaponDisabledByInjury && suspectAlive;
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

            string terminalSuspectEvidence;
            bool terminalOwnershipReconciliationPending;
            bool terminalSuspectCompromised;
            if (HasUnexpectedDispatchSuspectDeath(
                _incident, out terminalSuspectEvidence,
                out terminalOwnershipReconciliationPending,
                out terminalSuspectCompromised))
            {
                LogRuntime(terminalSuspectCompromised
                        ? "POLICE_DISPATCH_SUSPECT_COMPROMISED"
                        : "POLICE_DISPATCH_SUSPECT_UNKNOWN_FATAL_INJURY",
                    terminalSuspectEvidence);
                Fail(terminalSuspectCompromised
                        ? "Dispatch suspects got compromised. Dispatch Activity ended."
                        : "Dispatch suspect suffered an unknown fatal injury. Dispatch Activity ended.",
                    "lsimmersivelife.police.dispatch.cancelled");
                return;
            }

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

            MaintainMappedInteriorDoorwayEntryEvidence(_incident, player, now);

            // Settle a physically observed terminal scene from the maintenance
            // loop as soon as its final living suspect is gone. Previously the
            // case could remain active until the Player reopened the menu and
            // issued another arrest/investigate command against a corpse.
            if (_incident.ScenePreparationCompleted
                && !LiveSuspects(_incident).Any()
                && IsPlayerAtResolvedDispatchScene(_incident, player))
            {
                if (HasPendingVictimInvestigation)
                {
                    if (!_incident.MurderVictimInvestigationWaitReported)
                    {
                        _incident.MurderVictimInvestigationWaitReported = true;
                        LogRuntime(
                            "POLICE_DISPATCH_MURDER_VICTIM_INVESTIGATION_PENDING",
                            "Incident=" + _incident.Id
                            + "; Player=" + (player == null ? 0 : player.Handle)
                            + "; AwaitingPlayerInput=I"
                            + "; AutoCompletionSuppressed=true"
                            + "; VictimInvestigationPending=true");
                    }
                    return;
                }
                if (HasUnfinishedArrestedCustody(_incident))
                {
                    FailUnfinishedArrestedCustody(_incident);
                    return;
                }

                string completion = CompletionMessage(_incident, true);
                string unexpectedDeathEvidence;
                bool ownershipReconciliationPending;
                bool compromisedDeath;
                if (HasUnexpectedDispatchSuspectDeath(
                    _incident, out unexpectedDeathEvidence,
                    out ownershipReconciliationPending,
                    out compromisedDeath))
                {
                    Complete(completion);
                    return;
                }
                if (ownershipReconciliationPending)
                    return;
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
            if (_ambientWorld != null && _incident != null
                && IsDisturbanceActivityScene(_incident))
                _ambientWorld.MaintainDispatchDisturbanceActivity(
                    _incident, now, IsWoundedSuspect(_incident.Suspect));
            if (_ambientWorld != null && _incident != null
                && (IsActiveShooterScene(_incident) || IsGroupShootoutScene(_incident)))
                _ambientWorld.MaintainDispatchActiveShooterActivity(
                    _incident, now, IsWoundedSuspect(_incident.Suspect));
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
                    bool stoppedVehicleManaged = !interiorScene
                        && MaintainStoppedPursuitVehicle(
                            _incident, pursuitMovementTarget, player, now);
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

        private void MaintainMappedInteriorDoorwayEntryEvidence(
            LSPDDispatchEvent incident,
            Ped player,
            DateTime now)
        {
            if (incident == null || incident.SceneResolution == null
                || !incident.SceneResolution.IsMappedInteriorDispatchScene
                || !IsMappedInteriorDispatchBehavior(incident.SceneBehavior)
                || incident.SceneResolution.InteriorAccessPair == null
                || incident.SceneResolution.InteriorAccessPair.OutsidePoint == null
                || incident.SceneResolution.InteriorAccessPair.InsidePoint == null
                || player == null || !player.Exists() || player.IsDead
                || _ambientWorld == null)
                return;

            if (incident.LastMappedInteriorEntrySampleAt != DateTime.MinValue
                && now < incident.LastMappedInteriorEntrySampleAt.AddMilliseconds(
                    MappedInteriorEntrySampleMilliseconds))
                return;
            incident.LastMappedInteriorEntrySampleAt = now;

            int playerInteriorId;
            if (!_ambientWorld.TryGetDispatchPlayerInteriorId(player, out playerInteriorId))
                return;

            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair =
                incident.SceneResolution.InteriorAccessPair;
            Vector3 position = player.Position;
            float insideDistance = HorizontalDistance(
                position, pair.InsidePoint.Position);
            float outsideDistance = HorizontalDistance(
                position, pair.OutsidePoint.Position);
            bool onOutsideThresholdSide = outsideDistance <= ArrestRadius
                && outsideDistance < insideDistance;
            bool positionOnOutsideSide = outsideDistance < insideDistance;
            bool onInsideThresholdSide = playerInteriorId == incident.SceneResolution.LiveInteriorId
                && insideDistance <= ArrestRadius
                && insideDistance < outsideDistance;

            if (!incident.MappedInteriorEntryTrackingInitialized)
            {
                incident.MappedInteriorEntryTrackingInitialized = true;
                incident.MappedInteriorPlayerStartedInside =
                    playerInteriorId == incident.SceneResolution.LiveInteriorId
                    && !positionOnOutsideSide;
                if (incident.MappedInteriorPlayerStartedInside)
                {
                    incident.MappedInteriorEntryEvidence = "PlayerAlreadyInsideWhenDispatchActivated";
                    if (_log != null)
                        _log.RuntimeTransition(
                            "POLICE_DISPATCH_INTERIOR_PLAYER_ENTRY",
                            "Occurrence=" + incident.Id + "; Result=AlreadyInside",
                            "Occurrence=" + incident.Id
                            + "; Player=" + player.Handle
                            + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                            + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                            + "; InteriorPair=" + pair.InsidePoint.SourceRecord
                            + "; Position=" + DescribePosition(position)
                            + "; EntryEvidence=PlayerAlreadyInsideWhenDispatchActivated");
                }
            }

            if (onOutsideThresholdSide)
            {
                incident.MappedInteriorOutsideDoorwayObserved = true;
                incident.LastMappedInteriorOutsideDoorwayAt = now;
                return;
            }

            if (incident.MappedInteriorOutsideDoorwayObserved
                && now > incident.LastMappedInteriorOutsideDoorwayAt.AddSeconds(
                    MappedInteriorDoorwayEvidenceWindowSeconds))
            {
                incident.MappedInteriorOutsideDoorwayObserved = false;
                incident.LastMappedInteriorOutsideDoorwayAt = DateTime.MinValue;
            }

            if (!onInsideThresholdSide
                || incident.MappedInteriorPlayerStartedInside
                || incident.MappedInteriorDoorwayCrossingObserved
                || !incident.MappedInteriorOutsideDoorwayObserved
                || now > incident.LastMappedInteriorOutsideDoorwayAt.AddSeconds(
                    MappedInteriorDoorwayEvidenceWindowSeconds))
                return;

            incident.MappedInteriorDoorwayCrossingObserved = true;
            incident.MappedInteriorDoorwayCrossedAt = now;
            incident.MappedInteriorEntryEvidence = "MappedOutsideToInsideThresholdCrossingObserved";
            if (_log != null)
                _log.RuntimeTransition(
                    "POLICE_DISPATCH_INTERIOR_PLAYER_ENTRY",
                    "Occurrence=" + incident.Id + "; Result=MappedDoorwayCrossing",
                    "Occurrence=" + incident.Id
                    + "; Player=" + player.Handle
                    + "; CatalogInteriorId=" + incident.SceneResolution.CatalogInteriorId
                    + "; LiveInteriorId=" + incident.SceneResolution.LiveInteriorId
                    + "; InteriorPair=" + pair.InsidePoint.SourceRecord
                    + "; Position=" + DescribePosition(position)
                    + "; OutsideThresholdDistance=" + outsideDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; InsideThresholdDistance=" + insideDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; EntryEvidence=" + incident.MappedInteriorEntryEvidence);
        }

        private bool TryFindMappedInteriorEncounterTrigger(
            LSPDDispatchEvent incident,
            Ped player,
            bool requirePlayerAim,
            out Ped encounteredSuspect,
            out string encounterTrigger)
        {
            encounteredSuspect = null;
            encounterTrigger = string.Empty;
            if (incident == null || incident.SceneResolution == null
                || !incident.SceneResolution.IsMappedInteriorDispatchScene
                || !IsMappedInteriorDispatchBehavior(incident.SceneBehavior)
                || (!incident.MappedInteriorPlayerStartedInside
                    && !incident.MappedInteriorDoorwayCrossingObserved)
                || player == null || !player.Exists() || player.IsDead || player.IsInVehicle()
                || _ambientWorld == null)
                return false;

            int playerInteriorId;
            if (!_ambientWorld.TryGetDispatchPlayerInteriorId(player, out playerInteriorId)
                || playerInteriorId != incident.SceneResolution.LiveInteriorId)
                return false;

            foreach (Ped suspect in LiveSuspects(incident))
            {
                int suspectInteriorId;
                if (!_ambientWorld.TryGetActorInteriorId(suspect, out suspectInteriorId)
                    || suspectInteriorId != playerInteriorId
                    || player.Position.DistanceTo(suspect.Position) > ArrestRadius)
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
                    LogDebugTransition(
                        "POLICE_DISPATCH_INTERIOR_ENCOUNTER_SIGHT_PROBE",
                        "Occurrence=" + incident.Id + "; Stage=PlayerAim; Result=Unavailable"
                            + "; Exception=" + ex.GetType().Name,
                        "Occurrence=" + incident.Id
                        + "; Suspect=" + suspect.Handle
                        + "; Native=IS_PLAYER_FREE_AIMING_AT_ENTITY"
                        + "; Result=Unavailable; Exception=" + ex.GetType().Name);
                    aimingAtSuspect = false;
                }

                bool clearLineOfSight = false;
                if (!requirePlayerAim && !aimingAtSuspect)
                {
                    try
                    {
                        clearLineOfSight = Function.Call<bool>(
                            Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                            player,
                            suspect,
                            17);
                    }
                    catch (Exception ex)
                    {
                        LogDebugTransition(
                            "POLICE_DISPATCH_INTERIOR_ENCOUNTER_SIGHT_PROBE",
                            "Occurrence=" + incident.Id + "; Stage=LineOfSight; Result=Unavailable"
                                + "; Exception=" + ex.GetType().Name,
                            "Occurrence=" + incident.Id
                            + "; Suspect=" + suspect.Handle
                            + "; Native=HAS_ENTITY_CLEAR_LOS_TO_ENTITY"
                            + "; Result=Unavailable; Exception=" + ex.GetType().Name);
                    }
                }

                if (requirePlayerAim ? !aimingAtSuspect : !(aimingAtSuspect || clearLineOfSight))
                    continue;

                encounteredSuspect = suspect;
                encounterTrigger = aimingAtSuspect
                    ? "PlayerAim" : "PlayerClearLineOfSight";
                ClearDebugTransition("POLICE_DISPATCH_INTERIOR_ENCOUNTER_SIGHT_PROBE");
                return true;
            }

            return false;
        }

        private bool TryFindGroupShootoutEncounterTrigger(
            LSPDDispatchEvent incident,
            Ped player,
            out Ped encounteredSuspect,
            out string encounterTrigger)
        {
            encounteredSuspect = null;
            encounterTrigger = string.Empty;
            if (!IsGroupShootoutScene(incident)
                || player == null || !player.Exists() || player.IsDead)
                return false;

            if (incident.GroupComplianceRequested || incident.SurrenderRequested)
            {
                encounteredSuspect = LiveSuspects(incident)
                    .FirstOrDefault(value => !IsArrested(incident, value));
                encounterTrigger = incident.GroupComplianceRequested
                    ? "BackupGroupIntervention" : "PoliceCommand";
                return encounteredSuspect != null;
            }

            if (incident.PursuitContainedSuspectHandles != null)
            {
                encounteredSuspect = LiveSuspects(incident).FirstOrDefault(value =>
                    incident.PursuitContainedSuspectHandles.Contains(value.Handle)
                    || incident.BackupArrestInProgressSuspectHandles.Contains(value.Handle));
                if (encounteredSuspect != null)
                {
                    encounterTrigger = "BackupContainment";
                    return true;
                }
            }

            foreach (Ped suspect in LiveSuspects(incident))
            {
                if (suspect == null || !suspect.Exists() || suspect.IsDead
                    || player.Position.DistanceTo(suspect.Position)
                        > PairedSceneAnimationApproachDistance)
                    continue;

                bool aimingAtSuspect = false;
                try
                {
                    aimingAtSuspect = Function.Call<bool>(
                        Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY,
                        Game.Player.Handle,
                        suspect);
                }
                catch (Exception ex)
                {
                    LogDebugTransition(
                        "POLICE_DISPATCH_GROUP_SHOOTOUT_AIM_PROBE",
                        "Occurrence=" + incident.Id + "; Result=Unavailable"
                            + "; Exception=" + ex.GetType().Name,
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Native=IS_PLAYER_FREE_AIMING_AT_ENTITY; Result=Unavailable"
                        + "; Exception=" + ex.GetType().Name);
                }
                if (aimingAtSuspect)
                {
                    encounteredSuspect = suspect;
                    encounterTrigger = "PlayerAim";
                    ClearDebugTransition("POLICE_DISPATCH_GROUP_SHOOTOUT_AIM_PROBE");
                    return true;
                }

                if (player.IsInVehicle()
                    || player.Position.DistanceTo(suspect.Position)
                        > GroupShootoutOnFootConfrontationRange)
                    continue;

                bool clearLineOfSight = false;
                try
                {
                    clearLineOfSight = Function.Call<bool>(
                        Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                        player,
                        suspect,
                        17);
                }
                catch (Exception ex)
                {
                    LogDebugTransition(
                        "POLICE_DISPATCH_GROUP_SHOOTOUT_SIGHT_PROBE",
                        "Occurrence=" + incident.Id + "; Result=Unavailable"
                            + "; Exception=" + ex.GetType().Name,
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Native=HAS_ENTITY_CLEAR_LOS_TO_ENTITY; Result=Unavailable"
                        + "; Exception=" + ex.GetType().Name);
                }
                if (!clearLineOfSight)
                    continue;

                encounteredSuspect = suspect;
                encounterTrigger = "PlayerOnFootCloseLineOfSight";
                ClearDebugTransition("POLICE_DISPATCH_GROUP_SHOOTOUT_SIGHT_PROBE");
                return true;
            }

            return false;
        }

        private static string DescribePosition(Vector3 position)
        {
            return position.X.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + position.Y.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + position.Z.ToString("0.00", CultureInfo.InvariantCulture);
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

            DateTime now = DateTime.UtcNow;
            if (_incident.LastMappedInteriorEscalationCheckAt != DateTime.MinValue
                && now < _incident.LastMappedInteriorEscalationCheckAt.AddMilliseconds(
                    MappedInteriorEscalationCheckMilliseconds))
                return;
            _incident.LastMappedInteriorEscalationCheckAt = now;

            Ped suspect;
            string trigger;
            if (!TryFindMappedInteriorEncounterTrigger(
                _incident, player, true, out suspect, out trigger))
                return;

            string selectedResponse;
            if (_incident.SuspectResponses.TryGetValue(suspect.Handle, out selectedResponse)
                && string.Equals(selectedResponse, "resist", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                suspect.Task.Combat(player);
                _incident.SuspectEscalationTriggered = true;
                _incident.SuspectResponses[suspect.Handle] = "resist";
                SetStateForUnresolvedSuspects(
                    LSPDDispatchState.SuspectResisting);
                ReportAudioStage(
                    _incident.Armed
                        ? "lsimmersivelife.police.resistance.armed"
                        : "lsimmersivelife.police.resistance.resisting",
                    _incident,
                    "interior-scene-escalation-" + suspect.Handle,
                    true);
                Notify("~r~POLICE DISPATCH~s~\nThe suspect is resisting inside the building.");
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_SUSPECT_ESCALATED",
                    "Incident=" + _incident.Id
                    + "; Player=" + player.Handle
                    + "; Suspect=" + suspect.Handle
                    + "; InteriorId=" + _incident.SceneResolution.LiveInteriorId
                    + "; PlayerDistance=" + player.Position.DistanceTo(suspect.Position).ToString(
                        "0.0", CultureInfo.InvariantCulture)
                    + "; EntryEvidence=" + _incident.MappedInteriorEntryEvidence
                    + "; Trigger=" + trigger);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_INTERIOR_STORE_ESCALATION_FAILED", ex);
            }
        }

        private void MaintainInteriorStoreUtilityParking(Ped player)
        {
            if (_incident == null
                || !IsMappedInteriorDispatchBehavior(_incident.SceneBehavior)
                || _incident.SceneResolution == null
                || player == null || !player.Exists())
                return;

            if (!_incident.InteriorUtilityRequirementReported)
            {
                _incident.InteriorUtilityRequirementReported = true;
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_UTILITY_REQUIREMENT",
                    "Incident=" + _incident.Id
                    + "; AuthoredVehicleStagingRequired="
                    + _incident.VehicleStagingRequired
                    + "; PreparedVehicleStagingPosition="
                    + _incident.SceneResolution.HasVehicleStagingPosition
                    + "; Decision=" + (_incident.VehicleStagingRequired
                        ? "RequireOneTimePatrolUtilityArrival"
                        : "NoPatrolUtilityRequired"));
            }

            if (!_incident.VehicleStagingRequired
                || _incident.InteriorUtilityArrivalValidated)
                return;

            if (!_incident.SceneResolution.HasVehicleStagingPosition)
            {
                if (!_incident.InteriorUtilityStagingFailureReported)
                {
                    _incident.InteriorUtilityStagingFailureReported = true;
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_UTILITY_STAGING_UNAVAILABLE",
                        "Incident=" + _incident.Id
                        + "; AuthoredVehicleStagingRequired=true"
                        + "; PreparedVehicleStagingPosition=false"
                        + "; Result=ScenePreparationContractFailure");
                }
                return;
            }

            if (!player.IsInVehicle())
                return;

            Vehicle vehicle = player.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists()
                || _playerPatrolUtility == null || !_playerPatrolUtility.Exists()
                || vehicle.Handle != _playerPatrolUtility.Handle
                || Math.Abs(vehicle.Speed) > 0.5f
                || vehicle.Position.DistanceTo(
                    _incident.SceneResolution.VehicleStagingPosition) > 35f)
                return;

            _incident.ParkedPlayerUtility = vehicle;
            _incident.InteriorUtilityArrivalValidated = true;
            LogRuntime(
                "POLICE_DISPATCH_INTERIOR_STORE_UTILITY_PARKED",
                "Incident=" + _incident.Id
                + "; Vehicle=" + vehicle.Handle
                + "; DistanceToApproach="
                + vehicle.Position.DistanceTo(
                    _incident.SceneResolution.VehicleStagingPosition).ToString("0.0", CultureInfo.InvariantCulture)
                + "; VehicleIsManagedPatrolUtility=true; FirstArrival=true");
            LogRuntime(
                "POLICE_DISPATCH_INTERIOR_UTILITY_ARRIVAL_VALIDATED",
                "Incident=" + _incident.Id
                + "; Vehicle=" + vehicle.Handle
                + "; FirstArrival=true; Speed=" + vehicle.Speed.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; DistanceToApproach=" + vehicle.Position.DistanceTo(
                    _incident.SceneResolution.VehicleStagingPosition).ToString(
                        "0.00", CultureInfo.InvariantCulture)
                + "; ValidationPersistsAfterPlayerExits=true");
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
                    if (!TryApplyDispatchCustodyDamageProtection(suspect, _incident))
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

            int handle = suspect.Handle;
            DateTime lastTask;
            bool hasPreviousTask = _lastSecuredPoseTaskAt.TryGetValue(handle, out lastTask);
            bool hadPendingPoseRetry = _securedPoseRetryAt.ContainsKey(handle);

            const string handcuffDictionary = "mp_arresting";
            try
            {
                if (LSImmersiveDictionaryAnimation.IsPlaying(
                    suspect, handcuffDictionary, "idle"))
                {
                    _lastSecuredPoseTaskAt[handle] = now;
                    _securedPoseConfirmedPlayingHandles.Add(handle);
                    _securedPoseInterruptedHandles.Remove(handle);
                    _securedPoseRetryAt.Remove(handle);
                    return;
                }

                bool recovering = IsSecuredPoseBlockedByRecovery(suspect);
                if (recovering)
                {
                    if (hasPreviousTask || _securedPoseConfirmedPlayingHandles.Contains(handle))
                        _securedPoseInterruptedHandles.Add(handle);
                    return;
                }

                // A grounded, previously-playing cuff idle that has stopped is
                // treated as an interruption. This catches a ragdoll that began
                // and recovered between maintenance samples as well as one
                // observed directly below.
                if (hasPreviousTask && _securedPoseConfirmedPlayingHandles.Remove(handle))
                    _securedPoseInterruptedHandles.Add(handle);
                bool restoringInterruptedPose = _securedPoseInterruptedHandles.Contains(handle);

                DateTime retryAt;
                if (!force && _securedPoseRetryAt.TryGetValue(handle, out retryAt)
                    && now < retryAt)
                    return;
                if (!force && !restoringInterruptedPose && hasPreviousTask
                    && now < lastTask.AddMilliseconds(SecuredPoseRefreshMilliseconds))
                    return;

                if (!LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                    handcuffDictionary))
                {
                    _securedPoseRetryAt[handle] = now.AddMilliseconds(750);
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
                    _securedPoseRetryAt[handle] = now.AddMilliseconds(750);
                    if (!hasPreviousTask && !hadPendingPoseRetry)
                        LogRuntime("POLICE_DISPATCH_HANDCUFF_CLIP_UNAVAILABLE",
                        "Ped=" + handle + "; Dictionary=" + handcuffDictionary
                        + "; Clip=idle; Action=PhysicalCustodyRetained");
                    return;
                }
                _lastSecuredPoseTaskAt[handle] = now;
                _securedPoseRetryAt[handle] = now.AddMilliseconds(750);
                if (!hasPreviousTask || force || restoringInterruptedPose)
                    LogRuntime("POLICE_DISPATCH_HANDCUFF_CLIP_REQUESTED",
                    "Ped=" + handle + "; Dictionary=" + handcuffDictionary
                    + "; Clip=idle; Duration=" + clipDuration.ToString("0.000")
                    + "; Flags=1; PlaybackRate=1.0; TaskDurationMs=-1"
                    + (restoringInterruptedPose
                        ? "; Recovery=GroundedAfterAnimationInterruption"
                        : string.Empty));
                if (restoringInterruptedPose)
                    _securedPoseInterruptedHandles.Remove(handle);
            }
            catch (Exception ex)
            {
                _securedPoseRetryAt[handle] = now.AddMilliseconds(1000);
                LogException("POLICE_DISPATCH_HANDCUFF_POSE_FAILED", ex);
            }
        }

        private static bool IsSecuredPoseBlockedByRecovery(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return true;
            try
            {
                return Function.Call<bool>(Hash.IS_PED_RAGDOLL, suspect)
                    || Function.Call<bool>(Hash.IS_ENTITY_IN_AIR, suspect);
            }
            catch
            {
                return false;
            }
        }

        private void ClearSecuredPoseTracking(int handle)
        {
            _lastSecuredPoseTaskAt.Remove(handle);
            _securedPoseRetryAt.Remove(handle);
            _securedPoseConfirmedPlayingHandles.Remove(handle);
            _securedPoseInterruptedHandles.Remove(handle);
        }

        private void ClearSecuredPoseTracking()
        {
            _lastSecuredPoseTaskAt.Clear();
            _securedPoseRetryAt.Clear();
            _securedPoseConfirmedPlayingHandles.Clear();
            _securedPoseInterruptedHandles.Clear();
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
            string reason = string.Empty;
            if (_ambientWorld == null
                || !_ambientWorld.TryStartDispatchRobberyActivity(
                    _incident, false, out reason))
                LogDebug("POLICE_DISPATCH_ROBBERY_MOVEMENT_FAILED",
                    "Occurrence=" + _incident.Id + "; Owner=AmbientWorld"
                    + "; Reason=" + (_ambientWorld == null
                        ? "AmbientWorld is not attached by PoliceCore." : reason));
            _incident.SceneBehaviorStage = 2;
            _incident.SceneBehaviorNextStepAt = now.AddMilliseconds(1800);
        }

        private void IssueRobberyThreatPose()
        {
            if (_incident == null || _incident.Suspect == null
                || !_incident.Suspect.Exists())
                return;
            string reason = string.Empty;
            if (_ambientWorld == null
                || !_ambientWorld.TryStartDispatchRobberyActivity(
                    _incident, true, out reason))
                LogDebug("POLICE_DISPATCH_ROBBERY_THREAT_POSE_FAILED",
                    "Occurrence=" + _incident.Id + "; Owner=AmbientWorld"
                    + "; Reason=" + (_ambientWorld == null
                        ? "AmbientWorld is not attached by PoliceCore." : reason));
            _incident.SceneBehaviorStage = 3;
            _incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
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
                string fallbackReason = string.Empty;
                if (_ambientWorld == null
                    || !_ambientWorld.TryStartDispatchPreArrivalActivity(
                        _incident, _incident.Suspect, "victim_threat", out fallbackReason))
                    LogDebug("POLICE_DISPATCH_INTERIOR_SNATCH_FALLBACK_FAILED",
                        "Occurrence=" + _incident.Id + "; Owner=AmbientWorld"
                        + "; Reason=" + (_ambientWorld == null
                            ? "AmbientWorld is not attached by PoliceCore." : fallbackReason));
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
                    string reason = string.Empty;
                    if (_ambientWorld == null
                        || !_ambientWorld.TryApplyDispatchParticipantThreatPose(
                            _incident, participant, 120000, out reason))
                        throw new InvalidOperationException(_ambientWorld == null
                            ? "AmbientWorld is not attached by PoliceCore." : reason);
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
                    && (_incident.Mobile || _incident.Armed || _incident.Severity >= 4
                        || IsDisturbanceActivityScene(_incident))
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
                        if (_incident == null || !_incident.OwnedByDispatch)
                            return;
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

                    StartDispatchVictimCower(_incident.Victim);
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

                    foreach (Ped suspect in LiveSuspects(_incident))
                        PlayConfiguredPreArrivalActivity(suspect, "victim_threat");
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
                        string exitReason = string.Empty;
                        if (_ambientWorld == null
                            || !_ambientWorld.TryStartDispatchSuspectVehicleExit(
                                _incident, _incident.Suspect,
                                _incident.SuspectVehicle, out exitReason))
                        {
                            string failure = _ambientWorld == null
                                ? "AmbientWorld is not attached by PoliceCore." : exitReason;
                            LogRuntime("POLICE_DISPATCH_ROBBERY_VEHICLE_EXIT_FAILED",
                                "Occurrence=" + _incident.Id + "; Owner=AmbientWorld"
                                + "; Reason=" + failure + "; TerminalOutcome=false");
                            Fail("The robbery escape vehicle exit task could not start. " + failure);
                            return;
                        }
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
                        if (_incident == null || !_incident.OwnedByDispatch)
                            return;
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

        private static bool IsGroupShootoutScene(LSPDDispatchEvent incident)
        {
            return incident != null
                && string.Equals(incident.IncidentType, "shots_fired",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(incident.PreArrivalActivity, "group_shootout",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActiveShooterScene(LSPDDispatchEvent incident)
        {
            return incident != null
                && string.Equals(incident.IncidentType, "shots_fired",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(incident.PreArrivalActivity, "active_shooter",
                    StringComparison.OrdinalIgnoreCase);
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

            if (IsDisturbanceActivityScene(_incident))
            {
                string disturbanceReason = string.Empty;
                if (_ambientWorld == null
                    || !_ambientWorld.TryStartDispatchDisturbanceActivity(
                        _incident, suspect, out disturbanceReason))
                {
                    string disturbanceFailure = _ambientWorld == null
                        ? "AmbientWorld is not attached by PoliceCore." : disturbanceReason;
                    LogRuntime("POLICE_DISPATCH_DISTURBANCE_AMBIENT_STAGE_FAILED",
                        "Occurrence=" + _incident.Id + "; Owner=AmbientWorld"
                        + "; Failure=" + disturbanceFailure + "; TerminalOutcome=false");
                    Fail("The Disturbance scene could not be staged by AmbientWorld. "
                        + disturbanceFailure);
                }
                return;
            }

            string reason = string.Empty;
            bool started = _ambientWorld != null
                && _ambientWorld.TryStartDispatchPreArrivalActivity(
                    _incident, suspect, activity, out reason);
            if (started)
                return;

            string failure = _ambientWorld == null
                ? "AmbientWorld is not attached by PoliceCore." : reason;
            LogRuntime("POLICE_DISPATCH_PREARRIVAL_ACTIVITY_START_FAILED",
                "Occurrence=" + _incident.Id + "; Activity=" + activity
                + "; Suspect=" + suspect.Handle + "; Owner=AmbientWorld"
                + "; Failure=" + failure + "; TerminalOutcome=false");
            if (string.Equals(activity, "active_shooter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(activity, "group_shootout", StringComparison.OrdinalIgnoreCase))
                Fail("The Shots Fired scene could not start its authored real weapon activity. "
                    + failure);
            else if (IsDisturbanceActivityScene(_incident))
                Fail("The Disturbance scene could not be staged by AmbientWorld. " + failure);
        }

        private void StartDispatchVictimCower(Ped victim)
        {
            if (_incident == null || victim == null || !victim.Exists()
                || victim.IsDead || victim.Health <= 0)
                return;
            if (_ambientWorld == null
                || !_ambientWorld.TryStartDispatchVictimCower(_incident, victim))
                LogDebug("POLICE_DISPATCH_VICTIM_COWER_START_FAILED",
                    "Occurrence=" + _incident.Id + "; Victim=" + victim.Handle
                    + "; Owner=AmbientWorld; Result=Unavailable");
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
                    BeginDispatchVehicleFlightProtection(driver, incident);
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
                    BeginDispatchVehicleFlightProtection(suspect, incident);
                    CapturePursuitVehicleHealthBaseline(incident, suspect);
                    suspect.Task.VehicleChase(pursuitTarget);
                    started.Add(suspect.Handle + ":" + assignedVehicle.Handle);
                    int selectedWeaponHash = 0;
                    try
                    {
                        selectedWeaponHash = Function.Call<int>(
                            Hash.GET_SELECTED_PED_WEAPON, suspect);
                    }
                    catch { }
                    LogRuntime("POLICE_DISPATCH_VEHICLE_PURSUIT_ARMED_STATE",
                        "Incident=" + incident.Id
                        + "; Ped=" + suspect.Handle
                        + "; Vehicle=" + assignedVehicle.Handle
                        + "; Armed=" + incident.Armed
                        + "; WeaponName=" + incident.WeaponName
                        + "; SelectedWeaponHash=" + selectedWeaponHash
                        + "; WeaponEquipped=" + (incident.Armed
                            && selectedWeaponHash != 0)
                        + "; DriverTask=VehicleChase"
                        + "; ExplicitDriveByTaskIssued=false");
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
            bool genericDisturbance = IsDisturbanceActivityScene(incident);
            bool activeShooter = IsActiveShooterScene(incident);
            int disturbanceCommandTargetHandle = genericDisturbance
                ? incident.DisturbanceCommandTriggerSuspectHandle : 0;
            bool disturbanceCommandTriggerRequested = disturbanceCommandTargetHandle != 0;
            int activeShooterCommandTargetHandle = activeShooter
                ? incident.ActiveShooterCommandTriggerSuspectHandle : 0;
            bool activeShooterCommandTriggerRequested =
                activeShooterCommandTargetHandle != 0;
            bool commandTriggerRequested = disturbanceCommandTriggerRequested
                || activeShooterCommandTriggerRequested;
            int commandTargetHandle = disturbanceCommandTriggerRequested
                ? disturbanceCommandTargetHandle : activeShooterCommandTargetHandle;
            Ped disturbanceEncounterSuspect = null;
            string disturbanceEncounterTrigger = string.Empty;
            Ped activeShooterEncounterSuspect = null;
            string activeShooterEncounterTrigger = string.Empty;
            if (incident == null
                || (incident.PlayerEncounterResponseStarted && !vehicleEncounter)
                || !incident.OwnedByDispatch || incident.HasConvoyCustodyHandoff
                || incident.SuspectResponseBranches == null
                || incident.SuspectResponseBranches.Count == 0
                || player == null || !player.Exists() || player.IsDead
                || (vehicleEncounter
                    ? !IsPlayerNearVehicleEncounter(incident, player)
                    : (!commandTriggerRequested && !activeShooter
                        && player.Position.DistanceTo(incident.Origin)
                            > PairedSceneAnimationApproachDistance)))
                return;

            DateTime now = DateTime.UtcNow;
            if (incident.LastEncounterResponseCheckAt != DateTime.MinValue
                && now < incident.LastEncounterResponseCheckAt.AddSeconds(1)
                && !commandTriggerRequested)
                return;
            incident.LastEncounterResponseCheckAt = now;

            bool mappedInteriorEncounter = incident.SceneResolution != null
                && incident.SceneResolution.IsMappedInteriorDispatchScene
                && IsMappedInteriorDispatchBehavior(incident.SceneBehavior);
            Ped mappedEncounterSuspect = null;
            string mappedEncounterTrigger = string.Empty;
            if (mappedInteriorEncounter
                && !TryFindMappedInteriorEncounterTrigger(
                    incident,
                    player,
                    false,
                    out mappedEncounterSuspect,
                    out mappedEncounterTrigger))
                return;

            Ped groupShootoutEncounterSuspect = null;
            string groupShootoutEncounterTrigger = string.Empty;
            if (IsGroupShootoutScene(incident)
                && !TryFindGroupShootoutEncounterTrigger(
                    incident,
                    player,
                    out groupShootoutEncounterSuspect,
                    out groupShootoutEncounterTrigger))
                return;

            if (genericDisturbance
                && !TryFindDisturbanceEncounterTrigger(
                    incident,
                    player,
                    out disturbanceEncounterSuspect,
                    out disturbanceEncounterTrigger))
                return;

            if (activeShooter
                && !TryFindActiveShooterEncounterTrigger(
                    incident,
                    player,
                    out activeShooterEncounterSuspect,
                    out activeShooterEncounterTrigger))
                return;

            if (!vehicleEncounter && !mappedInteriorEncounter
                && !IsGroupShootoutScene(incident) && !genericDisturbance
                && !activeShooter
                && !HasVisibleDispatchSuspect(incident, player))
                return;

            if (incident.SceneResolution != null
                && incident.SceneResolution.IsInteriorScene
                && !mappedInteriorEncounter)
            {
                int playerInteriorId;
                if (_ambientWorld == null
                    || !_ambientWorld.TryGetActorInteriorId(player, out playerInteriorId)
                    || playerInteriorId != incident.SceneResolution.LiveInteriorId)
                    return;
            }

            List<Ped> suspects = LiveSuspects(incident)
                .Where(suspect => !IsArrested(incident, suspect)
                    && (!commandTriggerRequested
                        || suspect.Handle == commandTargetHandle)
                    && (!IsGroupShootoutScene(incident)
                        || !IsCompliant(incident, suspect)))
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

            if (disturbanceCommandTriggerRequested)
                incident.DisturbanceCommandTriggerSuspectHandle = 0;
            if (activeShooterCommandTriggerRequested)
                incident.ActiveShooterCommandTriggerSuspectHandle = 0;

            if (!vehicleEncounter)
                incident.PlayerEncounterResponseStarted = true;
            if (genericDisturbance)
            {
                if (_ambientWorld != null)
                    _ambientWorld.CleanupDispatchDisturbanceProp(
                        incident, "PlayerEncounterResponseStarted");
            }
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
                            BeginDispatchVehicleFlightProtection(suspect, incident);
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
                + (mappedInteriorEncounter
                    ? "; EncounterSuspect=" + (mappedEncounterSuspect == null
                        ? 0 : mappedEncounterSuspect.Handle)
                        + "; EncounterTrigger=" + mappedEncounterTrigger
                        + "; EntryEvidence=" + incident.MappedInteriorEntryEvidence
                    : string.Empty)
                + (IsGroupShootoutScene(incident)
                    ? "; EncounterSuspect=" + (groupShootoutEncounterSuspect == null
                        ? 0 : groupShootoutEncounterSuspect.Handle)
                        + "; EncounterTrigger=" + groupShootoutEncounterTrigger
                    : string.Empty)
                + (genericDisturbance
                    ? "; CriminalProfile=" + incident.CriminalProfileId
                        + "; Disposition=" + incident.CriminalDisposition
                        + "; EncounterSuspect=" + (disturbanceEncounterSuspect == null
                            ? 0 : disturbanceEncounterSuspect.Handle)
                        + "; EncounterTrigger=" + disturbanceEncounterTrigger
                    : string.Empty)
                + (activeShooter
                    ? "; ActiveShooter=" + incident.ActiveShooterWeaponName
                        + "; EncounterSuspect=" + (activeShooterEncounterSuspect == null
                            ? 0 : activeShooterEncounterSuspect.Handle)
                        + "; EncounterTrigger=" + activeShooterEncounterTrigger
                    : string.Empty)
                + "; Responses=" + string.Join(",", selected)
                + "; TerminalOutcome=false");
        }

        private bool TryFindDisturbanceEncounterTrigger(
            LSPDDispatchEvent incident,
            Ped player,
            out Ped encounteredSuspect,
            out string encounterTrigger)
        {
            encounteredSuspect = null;
            encounterTrigger = string.Empty;
            if (!IsDisturbanceActivityScene(incident)
                || player == null || !player.Exists() || player.IsDead)
                return false;

            List<Ped> suspects = LiveSuspects(incident)
                .Where(value => !IsArrested(incident, value))
                .ToList();
            int commandTargetHandle = incident.DisturbanceCommandTriggerSuspectHandle;
            if (commandTargetHandle != 0)
            {
                encounteredSuspect = suspects.FirstOrDefault(value =>
                    value.Handle == commandTargetHandle);
                if (encounteredSuspect != null)
                {
                    encounterTrigger = "SecureAndComplyCommand";
                    return true;
                }
                incident.DisturbanceCommandTriggerSuspectHandle = 0;
            }

            foreach (Ped suspect in suspects)
            {
                float distance = player.Position.DistanceTo(suspect.Position);
                bool aimingAtSuspect = false;
                try
                {
                    aimingAtSuspect = Function.Call<bool>(
                        Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY,
                        Game.Player.Handle,
                        suspect);
                }
                catch
                {
                    aimingAtSuspect = false;
                }
                if (aimingAtSuspect)
                {
                    encounteredSuspect = suspect;
                    encounterTrigger = "PlayerAim";
                    return true;
                }

                if (distance > PairedSceneAnimationApproachDistance)
                    continue;

                bool clearLineOfSight = false;
                try
                {
                    clearLineOfSight = Function.Call<bool>(
                        Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                        player,
                        suspect,
                        17);
                }
                catch
                {
                    clearLineOfSight = false;
                }
                if (!clearLineOfSight)
                    continue;

                bool dangerousEscalation = suspect.IsShooting
                    || suspect.IsInCombatAgainst(player);
                Ped victim = incident.Victim;
                if (!dangerousEscalation && victim != null && victim.Exists()
                    && !victim.IsDead && victim.Health > 0)
                {
                    try { dangerousEscalation = suspect.IsInCombatAgainst(victim); }
                    catch { }
                }
                if (dangerousEscalation)
                {
                    encounteredSuspect = suspect;
                    encounterTrigger = "VisibleDangerousEscalation";
                    return true;
                }

                string backupEvidence;
                if (_backup != null
                    && _backup.IsCurrentDispatchPhysicalIntervention(
                        incident, suspect, out backupEvidence))
                {
                    encounteredSuspect = suspect;
                    encounterTrigger = "VisibleBackupPhysicalIntervention;"
                        + backupEvidence;
                    return true;
                }

                if (player.IsInVehicle()
                    || distance > DisturbanceOnFootConfrontationRange)
                    continue;

                if (!incident.DisturbancePresentationReady)
                    continue;

                DateTime now = DateTime.UtcNow;
                if (incident.DisturbanceObservationStartedAt == DateTime.MinValue)
                {
                    incident.DisturbanceObservationStartedAt = now;
                    incident.DisturbanceResponseEarliestAt = now.AddSeconds(
                        DisturbanceObservationGraceSeconds);
                    LogRuntime(
                        "POLICE_DISPATCH_DISTURBANCE_OBSERVATION_STARTED",
                        "Occurrence=" + incident.Id
                        + "; Player=" + player.Handle
                        + "; Suspect=" + suspect.Handle
                        + "; Distance=" + distance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; OnFoot=true; ClearLineOfSight=true; PresentationReady=true"
                        + "; ObservationAt=" + incident.DisturbanceObservationStartedAt.ToString("O")
                        + "; ResponseEarliestAt=" + incident.DisturbanceResponseEarliestAt.ToString("O")
                        + "; GraceSeconds=" + DisturbanceObservationGraceSeconds
                        + "; ActivityStartedAt=" + incident.DisturbanceActivityStartedAt.ToString("O")
                        + "; TerminalOutcome=false");
                    continue;
                }

                if (now < incident.DisturbanceResponseEarliestAt)
                    continue;

                encounteredSuspect = suspect;
                encounterTrigger = "PlayerOnFootCloseLineOfSight";
                return true;
            }

            return false;
        }

        private bool TryFindActiveShooterEncounterTrigger(
            LSPDDispatchEvent incident,
            Ped player,
            out Ped encounteredSuspect,
            out string encounterTrigger)
        {
            encounteredSuspect = null;
            encounterTrigger = string.Empty;
            if (!IsActiveShooterScene(incident)
                || player == null || !player.Exists() || player.IsDead)
                return false;

            List<Ped> suspects = LiveSuspects(incident)
                .Where(value => !IsArrested(incident, value)
                    && !IsCompliant(incident, value))
                .ToList();
            int commandTargetHandle = incident.ActiveShooterCommandTriggerSuspectHandle;
            if (commandTargetHandle != 0)
            {
                encounteredSuspect = suspects.FirstOrDefault(value =>
                    value.Handle == commandTargetHandle);
                if (encounteredSuspect != null)
                {
                    encounterTrigger = "SecureAndComplyCommand";
                    return true;
                }
                incident.ActiveShooterCommandTriggerSuspectHandle = 0;
            }

            foreach (Ped suspect in suspects)
            {
                float distance = player.Position.DistanceTo(suspect.Position);
                bool aimingAtSuspect = false;
                try
                {
                    aimingAtSuspect = Function.Call<bool>(
                        Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY,
                        Game.Player.Handle,
                        suspect);
                }
                catch { }
                if (aimingAtSuspect)
                {
                    encounteredSuspect = suspect;
                    encounterTrigger = "PlayerAim";
                    return true;
                }

                if (distance <= PairedSceneAnimationApproachDistance)
                {
                    bool visibleBackupLineOfSight = false;
                    try
                    {
                        visibleBackupLineOfSight = Function.Call<bool>(
                            Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                            player,
                            suspect,
                            17);
                    }
                    catch { }

                    string backupEvidence;
                    if (visibleBackupLineOfSight && _backup != null
                        && _backup.IsCurrentDispatchPhysicalIntervention(
                            incident, suspect, out backupEvidence))
                    {
                        encounteredSuspect = suspect;
                        encounterTrigger = "VisibleBackupPhysicalIntervention;"
                            + backupEvidence;
                        return true;
                    }
                }

                if (distance > PairedSceneAnimationApproachDistance)
                    continue;

                if (player.IsInVehicle()
                    || incident.ActiveShooterLastShotAt == DateTime.MinValue
                    || DateTime.UtcNow < incident.ActiveShooterLastShotAt.AddSeconds(1.25f))
                    continue;

                bool clearLineOfSight = false;
                try
                {
                    clearLineOfSight = Function.Call<bool>(
                        Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                        player,
                        suspect,
                        17);
                }
                catch { }
                if (!clearLineOfSight)
                    continue;

                encounteredSuspect = suspect;
                encounterTrigger = "PlayerOnFootObservedActiveGunfire";
                return true;
            }

            return false;
        }

        private bool HasVisibleDispatchSuspect(
            LSPDDispatchEvent incident,
            Ped player)
        {
            if (incident == null || player == null || !player.Exists() || player.IsDead)
                return false;
            foreach (Ped suspect in LiveSuspects(incident)
                .Where(value => !IsArrested(incident, value)))
            {
                if (player.Position.DistanceTo(suspect.Position)
                    > PairedSceneAnimationApproachDistance)
                    continue;
                try
                {
                    if (Function.Call<bool>(
                        Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                        player,
                        suspect,
                        17))
                        return true;
                }
                catch { }
            }
            return false;
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
                        && !_incident.SceneResolution.IsInteriorScene
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
                    if (!SetPreparedDispatchSceneActorVisible(_incident, suspect, false))
                    {
                        suspect.Delete();
                        SetScenePreparationFailure(_incident, "InteriorActorVisibility",
                            "A prepared interior suspect could not be hidden until preflight completes.");
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
                    Ped suspect = null;
                    if (IsDisturbanceActivityScene(_incident))
                    {
                        string suspectPreparationFailure = string.Empty;
                        if (_ambientWorld == null
                            || !_ambientWorld.TryCreateDispatchSceneSuspect(
                                _incident.Id,
                                pedModel,
                                spawn,
                                refreshedScene,
                                out suspect,
                                out suspectPreparationFailure))
                        {
                            string failure = _ambientWorld == null
                                ? "AmbientWorld is not attached by PoliceCore."
                                : suspectPreparationFailure;
                            SetScenePreparationFailure(
                                _incident, "SuspectEntityCreation", failure);
                            LogDebug("POLICE_DISPATCH_DISTURBANCE_SUSPECT_STAGE_FAILED",
                                "Occurrence=" + _incident.Id
                                + "; Owner=AmbientWorld"
                                + "; Model=" + pedModel.Hash
                                + "; Reason=" + failure);
                            return false;
                        }
                    }
                    else
                    {
                        suspect = World.CreatePed(pedModel, spawn);
                    }
                    if (suspect == null || !suspect.Exists())
                    {
                        SetScenePreparationFailure(
                            _incident, "SuspectEntityCreation", "GTA did not create the suspect at the validated scene position.");
                        return false;
                    }
                    if (!SetPreparedDispatchSceneActorVisible(_incident, suspect, false))
                    {
                        suspect.Delete();
                        SetScenePreparationFailure(_incident, "InteriorActorVisibility",
                            "A prepared interior suspect could not be hidden until preflight completes.");
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
                int selectedHash = Function.Call<int>(
                    Hash.GET_SELECTED_PED_WEAPON, suspect);
                LogRuntime("POLICE_DISPATCH_SCENE_WEAPON_EQUIPPED",
                    "Incident=" + (_incident == null ? string.Empty : _incident.Id)
                    + "; Ped=" + suspect.Handle
                    + "; WeaponName=" + weaponName
                    + "; AssignedWeaponHash=" + hash
                    + "; SelectedWeaponHash=" + selectedHash
                    + "; Equipped=" + (selectedHash == hash));
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
                        IsDisturbanceActivityScene(incident),
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
                if (!incident.VictimStartsDead && _ambientWorld != null)
                    _ambientWorld.StartDispatchVictimInitialSceneActivity(incident, victim);
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

        private bool SetPreparedDispatchSceneActorVisible(
            LSPDDispatchEvent incident,
            Ped actor,
            bool visible)
        {
            if (incident == null || actor == null || !actor.Exists())
                return false;
            if (incident.SceneResolution == null)
                return false;
            try
            {
                if (!visible)
                {
                    Function.Call(Hash.SET_ENTITY_VISIBLE, actor, false, false);
                    actor.BlockPermanentEvents = true;
                    Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, actor, true);
                    if (!actor.IsInVehicle())
                        actor.Task.ClearAll();
                    actor.IsPositionFrozen = true;
                }
                else
                {
                    actor.IsPositionFrozen = false;
                    // Revealing starts the Dispatch-owned scene. Keep ambient
                    // game events blocked so they cannot replace its tasks.
                    actor.BlockPermanentEvents = true;
                    Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, actor, true);
                    Function.Call(Hash.SET_ENTITY_VISIBLE, actor, true, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                LogDebug("POLICE_DISPATCH_SCENE_ACTOR_VISIBILITY_FAILED",
                    "Occurrence=" + incident.Id
                    + "; Handle=" + actor.Handle
                    + "; Visible=" + visible
                    + "; Reason=" + ex.GetType().Name);
                return false;
            }
        }

        private bool RevealPreparedDispatchSceneActors(LSPDDispatchEvent incident)
        {
            if (incident == null || incident.SceneResolution == null)
                return false;

            var actors = TrackedSuspects(incident)
                .Concat(incident.Victim == null
                    ? Enumerable.Empty<Ped>() : new[] { incident.Victim })
                .Concat(incident.AdditionalParticipants ?? new List<Ped>())
                .Where(actor => actor != null && actor.Exists())
                .GroupBy(actor => actor.Handle)
                .Select(group => group.First())
                .ToList();
            foreach (Ped actor in actors)
            {
                bool ambientClaim = incident.VictimWasExistingAmbient
                        && incident.Victim != null
                        && incident.Victim.Handle == actor.Handle
                    || incident.ExistingAmbientSceneParticipantHandles != null
                        && incident.ExistingAmbientSceneParticipantHandles.Contains(actor.Handle);
                bool restored;
                if (ambientClaim && _ambientWorld != null)
                {
                    restored = _ambientWorld.CompleteDispatchCivilianPreparation(actor, incident.Id);
                    if (restored)
                        restored = SetPreparedDispatchSceneActorVisible(incident, actor, true);
                }
                else
                    restored = SetPreparedDispatchSceneActorVisible(incident, actor, true);
                if (!restored)
                {
                    foreach (Ped rollbackActor in actors)
                        SetPreparedDispatchSceneActorVisible(incident, rollbackActor, false);
                    LogRuntime(
                        "POLICE_DISPATCH_SCENE_ACTOR_REVEAL_FAILED",
                        "Occurrence=" + incident.Id
                        + "; FailedHandle=" + actor.Handle
                        + "; ActorCount=" + actors.Count
                        + "; Rollback=AllActorsHidden"
                        + "; AfterAcceptance=true");
                    return false;
                }
            }

            return true;
        }

        private bool TryValidatePreparedInteriorActors(
            LSPDDispatchEvent incident,
            out string evidence,
            out string reason)
        {
            evidence = string.Empty;
            reason = string.Empty;
            if (incident == null || incident.SceneResolution == null
                || !incident.SceneResolution.IsInteriorScene)
                return true;
            if (_ambientWorld == null || incident.SceneResolution.LiveInteriorId == 0)
            {
                reason = "Stage=InteriorActorIdentity; Result=AmbientWorldOrLiveInteriorUnavailable";
                return false;
            }

            var actorEvidence = new List<string>();
            var handles = new HashSet<int>();
            foreach (Ped suspect in TrackedSuspects(incident))
            {
                if (suspect == null || !suspect.Exists() || suspect.IsDead || suspect.Health <= 0)
                {
                    reason = "Stage=InteriorActorIdentity; Role=Suspect; Result=SuspectNotLivingAtPreflight";
                    evidence = string.Join(" || ", actorEvidence);
                    return false;
                }
                if (!handles.Add(suspect.Handle))
                    continue;

                string visibilityFailure;
                if (!TryVerifyPreparedDispatchSceneActorHidden(
                    incident, suspect, "Suspect", out visibilityFailure))
                {
                    reason = visibilityFailure;
                    evidence = string.Join(" || ", actorEvidence);
                    return false;
                }

                string actorRecord;
                string actorFailure;
                if (!_ambientWorld.TryValidateDispatchInteriorActor(
                    suspect,
                    "Suspect",
                    "Dispatch",
                    incident.Id,
                    incident.SceneResolution,
                    true,
                    out actorRecord,
                    out actorFailure))
                {
                    reason = actorFailure + "; Occurrence=" + incident.Id + "; Role=Suspect";
                    evidence = string.Join(" || ", actorEvidence);
                    return false;
                }
                actorEvidence.Add(actorRecord + "; Visible=false");
            }

            Ped victim = incident.Victim;
            if (victim != null && victim.Exists() && handles.Add(victim.Handle))
            {
                string visibilityFailure;
                if (!TryVerifyPreparedDispatchSceneActorHidden(
                    incident, victim, "Victim", out visibilityFailure))
                {
                    reason = visibilityFailure;
                    evidence = string.Join(" || ", actorEvidence);
                    return false;
                }

                bool victimRequiresDoorway = !incident.VictimStartsDead
                    && !victim.IsDead && victim.Health > 0;
                string actorRecord;
                string actorFailure;
                if (!_ambientWorld.TryValidateDispatchInteriorActor(
                    victim,
                    "Victim",
                    incident.VictimWasExistingAmbient
                        ? "DispatchCivilianClaim" : "Dispatch",
                    incident.Id,
                    incident.SceneResolution,
                    victimRequiresDoorway,
                    out actorRecord,
                    out actorFailure))
                {
                    reason = actorFailure + "; Occurrence=" + incident.Id + "; Role=Victim";
                    evidence = string.Join(" || ", actorEvidence);
                    return false;
                }
                actorEvidence.Add(actorRecord + "; Visible=false");
            }

            if (incident.AdditionalParticipants != null)
            {
                bool clerkPool = string.Equals(incident.SceneParticipantPoolId,
                        "shop_clerk", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(incident.SceneParticipantPoolId,
                        "gun_store_clerk", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(incident.SceneParticipantPoolId,
                        "tattoo_store_clerk", StringComparison.OrdinalIgnoreCase);
                string participantRole = clerkPool ? "Clerk" : "Customer";
                foreach (Ped participant in incident.AdditionalParticipants)
                {
                    if (participant == null || !participant.Exists()
                        || participant.IsDead || participant.Health <= 0)
                    {
                        reason = "Stage=InteriorActorIdentity; Role=" + participantRole
                            + "; Result=ParticipantNotLivingAtPreflight";
                        evidence = string.Join(" || ", actorEvidence);
                        return false;
                    }
                    if (!handles.Add(participant.Handle))
                        continue;

                    string visibilityFailure;
                    if (!TryVerifyPreparedDispatchSceneActorHidden(
                        incident, participant, participantRole, out visibilityFailure))
                    {
                        reason = visibilityFailure
                            + "; Pool=" + incident.SceneParticipantPoolId;
                        evidence = string.Join(" || ", actorEvidence);
                        return false;
                    }

                    string actorRecord;
                    string actorFailure;
                    if (!_ambientWorld.TryValidateDispatchInteriorActor(
                        participant,
                        participantRole,
                        "Dispatch",
                        incident.Id,
                        incident.SceneResolution,
                        true,
                        out actorRecord,
                        out actorFailure))
                    {
                        reason = actorFailure + "; Occurrence=" + incident.Id
                            + "; Role=" + participantRole
                            + "; Pool=" + incident.SceneParticipantPoolId;
                        evidence = string.Join(" || ", actorEvidence);
                        return false;
                    }
                    actorEvidence.Add(actorRecord
                        + "; Visible=false"
                        + "; Pool=" + incident.SceneParticipantPoolId);
                }
            }

            evidence = string.Join(" || ", actorEvidence);
            return true;
        }

        private bool TryValidatePreparedExteriorActors(
            LSPDDispatchEvent incident,
            bool capturePreparedPositions,
            out string evidence,
            out string reason)
        {
            evidence = string.Empty;
            reason = string.Empty;
            if (incident == null || incident.SceneResolution == null
                || incident.SceneResolution.IsInteriorScene || _ambientWorld == null)
            {
                reason = "Stage=ExteriorActorPlacement; Result=DispatchOrExteriorSceneUnavailable";
                return false;
            }

            List<Ped> suspects = TrackedSuspects(incident).ToList();
            int requiredSuspects = Math.Max(1, incident.SuspectCount);
            if (suspects.Count < requiredSuspects)
            {
                reason = "Stage=ExteriorActorPlacement; Role=Suspect; Required="
                    + requiredSuspects + "; Ready=" + suspects.Count;
                return false;
            }

            var actors = new List<Tuple<Ped, string>>();
            foreach (Ped suspect in suspects)
                actors.Add(Tuple.Create(suspect, "Suspect"));
            if (incident.VictimCount > 0
                && (incident.Victim == null || !incident.Victim.Exists()))
            {
                reason = "Stage=ExteriorActorPlacement; Role=Victim; Result=RequiredVictimUnavailable";
                return false;
            }
            if (incident.Victim != null && incident.Victim.Exists())
                actors.Add(Tuple.Create(incident.Victim, "Victim"));

            bool clerkPool = string.Equals(incident.SceneParticipantPoolId,
                    "shop_clerk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(incident.SceneParticipantPoolId,
                    "gun_store_clerk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(incident.SceneParticipantPoolId,
                    "tattoo_store_clerk", StringComparison.OrdinalIgnoreCase);
            string participantRole = clerkPool ? "Clerk" : "Customer";
            foreach (Ped participant in incident.AdditionalParticipants
                ?? new List<Ped>())
                actors.Add(Tuple.Create(participant, participantRole));

            Tuple<Ped, string> unavailableActor = actors.FirstOrDefault(entry =>
                entry.Item1 == null || !entry.Item1.Exists());
            if (unavailableActor != null)
            {
                reason = "Stage=ExteriorActorPlacement; Role=" + unavailableActor.Item2
                    + "; Result=ActorMissingBeforeOffer";
                return false;
            }

            actors = actors
                .GroupBy(entry => entry.Item1.Handle)
                .Select(group => group.First())
                .ToList();
            int[] excludedHandles = actors
                .Where(entry => entry.Item1.Exists())
                .Select(entry => entry.Item1.Handle)
                .Distinct()
                .ToArray();
            var records = new List<string>();
            if (capturePreparedPositions)
            {
                incident.PreparedExteriorActorPositions.Clear();
                incident.PreparedExteriorActorGroundPositions.Clear();
            }
            foreach (Tuple<Ped, string> entry in actors)
            {
                Ped actor = entry.Item1;
                string role = entry.Item2;
                if (actor == null || !actor.Exists())
                {
                    reason = "Stage=ExteriorActorPlacement; Role=" + role
                        + "; Result=ActorMissingBeforeOffer";
                    evidence = string.Join(" || ", records);
                    return false;
                }

                string hiddenFailure;
                if (!TryVerifyPreparedDispatchSceneActorHidden(
                    incident, actor, role, out hiddenFailure))
                {
                    reason = hiddenFailure;
                    evidence = string.Join(" || ", records);
                    return false;
                }

                string actorRecord;
                string actorFailure;
                Vector3 validatedGroundPosition = Vector3.Zero;
                bool actorValidated;
                if (capturePreparedPositions)
                {
                    actorValidated = _ambientWorld.TryValidateDispatchExteriorActor(
                        actor,
                        role,
                        incident.SceneResolution,
                        excludedHandles,
                        out validatedGroundPosition,
                        out actorRecord,
                        out actorFailure);
                }
                else
                {
                    Vector3 preparedPosition;
                    if (!incident.PreparedExteriorActorPositions.TryGetValue(
                        actor.Handle, out preparedPosition))
                    {
                        reason = "Stage=PreparedExteriorActorContinuity; Role=" + role
                            + "; Handle=" + actor.Handle
                            + "; Result=PreparedPositionSnapshotUnavailable";
                        evidence = string.Join(" || ", records);
                        return false;
                    }
                    if (!incident.PreparedExteriorActorGroundPositions.TryGetValue(
                        actor.Handle, out validatedGroundPosition))
                    {
                        reason = "Stage=PreparedExteriorActorContinuity; Role=" + role
                            + "; Handle=" + actor.Handle
                            + "; Result=PreparedGroundSnapshotUnavailable";
                        evidence = string.Join(" || ", records);
                        return false;
                    }
                    actorValidated = _ambientWorld.TryValidatePreparedDispatchExteriorActor(
                        actor,
                        role,
                        incident.SceneResolution,
                        preparedPosition,
                        validatedGroundPosition,
                        out actorRecord,
                        out actorFailure);
                }
                if (!actorValidated)
                {
                    bool navigationSnapDrift = capturePreparedPositions
                        && (actorFailure ?? string.Empty).IndexOf(
                            "Stage=ExteriorNavigationSnap",
                            StringComparison.OrdinalIgnoreCase) >= 0;
                    if (navigationSnapDrift
                        && incident.PreparedExteriorActorCorrectionAttempted.Add(actor.Handle))
                    {
                        List<Vector3> reservedPositions = actors
                            .Where(other => other.Item1 != null && other.Item1.Exists()
                                && other.Item1.Handle != actor.Handle)
                            .Select(other => other.Item1.Position)
                            .ToList();
                        Vector3 correctedPosition;
                        string correctionFailure;
                        bool corrected = _ambientWorld.TryResolveDispatchParticipantPosition(
                            actor.Position,
                            incident.SceneResolution,
                            reservedPositions,
                            out correctedPosition,
                            out correctionFailure);
                        if (corrected)
                        {
                            try
                            {
                                actor.Position = correctedPosition;
                                actor.BlockPermanentEvents = true;
                                actor.IsPositionFrozen = true;
                            }
                            catch (Exception ex)
                            {
                                correctionFailure = "SameActorLocalMove=" + ex.GetType().Name;
                                corrected = false;
                            }
                            if (corrected
                                && !SetPreparedDispatchSceneActorVisible(
                                    incident, actor, false))
                            {
                                correctionFailure = "CorrectedActorCouldNotRemainHidden";
                                corrected = false;
                            }
                            if (corrected)
                            {
                                actorValidated = _ambientWorld.TryValidateDispatchExteriorActor(
                                    actor,
                                    role,
                                    incident.SceneResolution,
                                    excludedHandles,
                                    out validatedGroundPosition,
                                    out actorRecord,
                                    out actorFailure);
                                if (actorValidated)
                                    actorRecord += "; SameActorLocalCorrection=true"
                                        + "; CorrectedPosition=" + correctedPosition;
                            }
                        }
                        if (!actorValidated && !string.IsNullOrWhiteSpace(correctionFailure))
                            actorFailure += "; BoundedSameActorCorrection=" + correctionFailure;
                    }

                    if (!actorValidated)
                    {
                        reason = actorFailure + "; Occurrence=" + incident.Id;
                        evidence = string.Join(" || ", records);
                        return false;
                    }
                }
                if (capturePreparedPositions)
                {
                    incident.PreparedExteriorActorPositions[actor.Handle] = actor.Position;
                    incident.PreparedExteriorActorGroundPositions[actor.Handle] =
                        validatedGroundPosition;
                }
                records.Add(actorRecord + "; Visible=false; Frozen=true; BlocksAmbientEvents=true");
            }

            evidence = string.Join(" || ", records);
            return true;
        }

        private SceneOfferPreflightStatus TryPreparePairedExteriorSceneFormation(
            LSPDDispatchEvent incident,
            out bool corrected,
            out string evidence,
            out string reason)
        {
            corrected = false;
            evidence = string.Empty;
            reason = string.Empty;
            if (incident == null
                || (!string.Equals(incident.SceneBehavior, "paired_meeting",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(incident.SceneBehavior, "paired_exchange",
                        StringComparison.OrdinalIgnoreCase)))
                return SceneOfferPreflightStatus.Ready;

            List<Ped> pair = TrackedSuspects(incident)
                .Where(actor => actor != null && actor.Exists() && !actor.IsDead)
                .Take(2)
                .ToList();
            if (pair.Count < 2 || _ambientWorld == null)
            {
                reason = "Stage=PairedSceneFormation; Required=2; Ready=" + pair.Count
                    + "; Result=PairOrAmbientWorldUnavailable";
                return SceneOfferPreflightStatus.Failed;
            }

            float distance = pair[0].Position.DistanceTo(pair[1].Position);
            if (distance < 0.65f || distance > 2.5f)
            {
                if (incident.PairedSceneFormationCorrectionAttempted)
                {
                    reason = "Stage=PairedSceneFormation; Primary=" + pair[0].Handle
                        + "; Secondary=" + pair[1].Handle
                        + "; Distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Result=BoundedLocalCorrectionAlreadyUsed";
                    return SceneOfferPreflightStatus.Failed;
                }

                incident.PairedSceneFormationCorrectionAttempted = true;
                Vector3 primaryPosition = pair[0].Position;
                Vector3 secondaryPosition = pair[1].Position;
                double dx = secondaryPosition.X - primaryPosition.X;
                double dy = secondaryPosition.Y - primaryPosition.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= 0.001d)
                {
                    double heading = pair[0].Heading * (Math.PI / 180.0);
                    dx = Math.Sin(heading);
                    dy = Math.Cos(heading);
                    length = 1.0d;
                }
                Vector3 desiredPosition = new Vector3(
                    primaryPosition.X + (float)(dx / length * 1.85d),
                    primaryPosition.Y + (float)(dy / length * 1.85d),
                    primaryPosition.Z);
                Vector3 correctedPosition;
                string correctionFailure;
                if (!_ambientWorld.TryResolveDispatchParticipantPosition(
                    desiredPosition,
                    incident.SceneResolution,
                    new[] { primaryPosition },
                    out correctedPosition,
                    out correctionFailure))
                {
                    reason = "Stage=PairedSceneFormationCorrection; Primary=" + pair[0].Handle
                        + "; Secondary=" + pair[1].Handle
                        + "; OriginalDistance=" + distance.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Result=NoValidatedLocalFormationPoint; Failure=" + correctionFailure;
                    return SceneOfferPreflightStatus.Failed;
                }

                float correctedDistance = primaryPosition.DistanceTo(correctedPosition);
                if (correctedDistance < 0.65f || correctedDistance > 2.5f)
                {
                    reason = "Stage=PairedSceneFormationCorrection; Primary=" + pair[0].Handle
                        + "; Secondary=" + pair[1].Handle
                        + "; CorrectedPosition=" + correctedPosition
                        + "; CorrectedDistance=" + correctedDistance.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Allowed=0.65-2.50m; Result=ResolvedPointOutsideAnimationFormation";
                    return SceneOfferPreflightStatus.Failed;
                }

                try
                {
                    pair[1].Position = correctedPosition;
                    pair[1].BlockPermanentEvents = true;
                    pair[1].IsPositionFrozen = true;
                }
                catch (Exception ex)
                {
                    reason = "Stage=PairedSceneFormationCorrection; Secondary=" + pair[1].Handle
                        + "; Result=HiddenActorMoveFailed:" + ex.GetType().Name;
                    return SceneOfferPreflightStatus.Failed;
                }
                if (!SetPreparedDispatchSceneActorVisible(incident, pair[1], false))
                {
                    reason = "Stage=PairedSceneFormationCorrection; Secondary=" + pair[1].Handle
                        + "; Result=CorrectedActorCouldNotRemainHidden";
                    return SceneOfferPreflightStatus.Failed;
                }
                corrected = true;
                distance = pair[0].Position.DistanceTo(pair[1].Position);
            }

            string animationEvidence;
            AmbientDispatchAnimationStartResult animation =
                _ambientWorld.TryPrepareDispatchPairAnimation(
                    incident, pair[0], pair[1], out animationEvidence);
            evidence = "Primary=" + pair[0].Handle
                + "; Secondary=" + pair[1].Handle
                + "; Distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture)
                + "; GroundAndExterior=PreflightPassed"
                + "; Corrected=" + corrected
                + "; " + animationEvidence;
            if (animation == AmbientDispatchAnimationStartResult.Started)
                return SceneOfferPreflightStatus.Ready;
            if (animation == AmbientDispatchAnimationStartResult.Pending
                && DateTime.UtcNow < incident.ScenePreparationDeadline)
            {
                incident.ScenePreparationNextRetryAt = DateTime.UtcNow.AddMilliseconds(250);
                reason = animationEvidence;
                return SceneOfferPreflightStatus.Pending;
            }

            reason = string.IsNullOrWhiteSpace(animationEvidence)
                ? "Stage=PairedSceneAnimation; Result=AnimationAssetsOrFormationUnavailable"
                : animationEvidence;
            return SceneOfferPreflightStatus.Failed;
        }

        internal static bool IsPreparedDispatchSceneActorHidden(
            bool visible,
            bool frozen,
            bool blocksAmbientEvents)
        {
            return !visible && frozen && blocksAmbientEvents;
        }

        private static bool TryVerifyPreparedDispatchSceneActorHidden(
            LSPDDispatchEvent incident,
            Ped actor,
            string role,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || actor == null || !actor.Exists())
            {
                reason = "Stage=InteriorActorVisibility; Role=" + (role ?? string.Empty)
                    + "; Result=ActorMissingBeforeAcceptance";
                return false;
            }

            try
            {
                bool visible = Function.Call<bool>(Hash.IS_ENTITY_VISIBLE, actor);
                bool frozen = actor.IsPositionFrozen;
                bool blocksAmbientEvents = actor.BlockPermanentEvents;
                if (IsPreparedDispatchSceneActorHidden(
                    visible, frozen, blocksAmbientEvents))
                    return true;

                reason = "Stage=DispatchSceneActorVisibility; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; Visible=" + visible
                    + "; Frozen=" + frozen
                    + "; BlocksAmbientEvents=" + blocksAmbientEvents
                    + "; Result=ActorNotHiddenAndNonReactiveBeforeAcceptance";
                return false;
            }
            catch (Exception ex)
            {
                reason = "Stage=DispatchSceneActorVisibility; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; Native=IS_ENTITY_VISIBLE; Result=ProbeFailed"
                    + "; Exception=" + ex.GetType().Name;
                return false;
            }
        }

        private void ObserveManagedDispatchSuspectPresence(
            DateTime now,
            bool force = false)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || !incident.ScenePreparationCompleted
                || incident.State == LSPDDispatchState.Offered
                || incident.State == LSPDDispatchState.Completed
                || incident.State == LSPDDispatchState.Cancelled
                || incident.State == LSPDDispatchState.Failed)
            {
                ClearManagedSuspectPresenceObservations();
                return;
            }

            if (!string.Equals(_managedSuspectPresenceOccurrenceId, incident.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                ClearManagedSuspectPresenceObservations();
                _managedSuspectPresenceOccurrenceId = incident.Id;
                _managedActorLostOccurrenceId = incident.Id;
            }
            if (!force && now < _nextManagedSuspectPresenceCheckAt)
                return;
            _nextManagedSuspectPresenceCheckAt = now.AddMilliseconds(
                ManagedSuspectPresenceCheckMilliseconds);

            var managedActors = new Dictionary<int, Ped>();
            IEnumerable<Ped> authoredSuspects = incident.Suspects != null
                ? incident.Suspects.Where(value => value != null)
                : Enumerable.Empty<Ped>();
            if (incident.Suspect != null)
                authoredSuspects = authoredSuspects.Concat(new[] { incident.Suspect });
            foreach (Ped actor in authoredSuspects)
            {
                int handle;
                try { handle = actor.Handle; }
                catch { continue; }
                if (handle > 0 && !managedActors.ContainsKey(handle))
                    managedActors.Add(handle, actor);
            }

            foreach (KeyValuePair<int, Ped> entry in managedActors)
            {
                int handle = entry.Key;
                Ped actor = entry.Value;
                ManagedSuspectPresenceObservation observation;
                if (!_managedSuspectPresenceObservations.TryGetValue(
                        handle, out observation))
                {
                    observation = new ManagedSuspectPresenceObservation
                    {
                        Actor = actor,
                        LastKnownPosition = incident.Origin
                    };
                    _managedSuspectPresenceObservations.Add(handle, observation);
                }
                observation.Actor = actor;

                bool nativeExists;
                bool wrapperExists;
                try
                {
                    nativeExists = Function.Call<bool>(Hash.DOES_ENTITY_EXIST, handle);
                    wrapperExists = actor.Exists();
                }
                catch
                {
                    // A failed query is not evidence that an actor was removed.
                    continue;
                }

                if (nativeExists || wrapperExists)
                {
                    bool presenceRecovered = observation.MissingSamples > 0;
                    observation.MissingSamples = 0;
                    observation.FirstMissingAt = DateTime.MinValue;
                    observation.PendingRemovalReported = false;
                    if (wrapperExists)
                    {
                        try
                        {
                            Vector3 position = actor.Position;
                            if (IsFiniteDispatchPosition(position))
                                observation.LastKnownPosition = position;
                        }
                        catch { }
                    }
                    if (presenceRecovered)
                        LogRuntime("POLICE_DISPATCH_MANAGED_SUSPECT_PRESENCE_RESTORED",
                            "Incident=" + incident.Id
                            + "; Ped=" + handle
                            + "; EntityNoLongerExists=false"
                            + "; Result=TransientQueryOrStreamingState; ActorRetained=true");
                    continue;
                }

                if (observation.FirstMissingAt == DateTime.MinValue)
                    observation.FirstMissingAt = now;
                observation.MissingSamples++;
                if (!observation.PendingRemovalReported)
                {
                    observation.PendingRemovalReported = true;
                    LogRuntime("POLICE_DISPATCH_MANAGED_SUSPECT_REMOVAL_PENDING",
                        "Incident=" + incident.Id
                        + "; Ped=" + handle
                        + "; EntityNoLongerExists=true"
                        + "; DeadState=UnavailableNotClassifiedAsDeath"
                        + "; LastKnownPosition="
                            + DescribePosition(observation.LastKnownPosition)
                        + "; ConfirmationSamplesRequired="
                            + ManagedSuspectMissingMinimumSamples
                        + "; ConfirmationMillisecondsRequired="
                            + ManagedSuspectMissingConfirmationMilliseconds
                        + "; BranchStopped=false");
                }
                if (observation.MissingSamples < ManagedSuspectMissingMinimumSamples
                    || now - observation.FirstMissingAt
                        < TimeSpan.FromMilliseconds(
                            ManagedSuspectMissingConfirmationMilliseconds))
                    continue;

                RecordManagedDispatchSuspectLoss(
                    incident, handle, observation, now);
                if (_incident == null)
                    return;
            }
        }

        private void RecordManagedDispatchSuspectLoss(
            LSPDDispatchEvent incident,
            int suspectHandle,
            ManagedSuspectPresenceObservation observation,
            DateTime now)
        {
            if (incident == null || observation == null
                || observation.MissingReported)
                return;
            observation.MissingReported = true;
            _managedActorLostOccurrenceId = incident.Id;
            _managedActorLostSuspectHandles.Add(suspectHandle);

            bool wasHandcuffTarget = incident.PlayerHandcuffInProgress
                && incident.PlayerHandcuffTargetHandle == suspectHandle;
            if (wasHandcuffTarget)
                AbortPlayerHandcuffAnimation(
                    Game.Player.Character, observation.Actor,
                    "MANAGED_SUSPECT_ENTITY_NO_LONGER_EXISTS");

            if (_backupArrestOperations.ContainsKey(suspectHandle))
                EndBackupArrestOperation(incident, suspectHandle, false);
            incident.BackupArrestInProgressSuspectHandles.Remove(suspectHandle);
            incident.BackupArrestAttemptedSuspectHandles.Remove(suspectHandle);
            incident.PursuitContainedSuspectHandles.Remove(suspectHandle);
            incident.PlayerCommandedSecureSuspectHandles.Remove(suspectHandle);
            incident.OriginalSuspectRelationshipGroupByHandle.Remove(suspectHandle);
            incident.SuspectVehicleByPedHandle.Remove(suspectHandle);
            incident.VehicleEntrySeatByPedHandle.Remove(suspectHandle);
            incident.SuspectResponses.Remove(suspectHandle);
            if (incident.Suspects != null)
                incident.Suspects.RemoveAll(value => value != null
                    && value.Handle == suspectHandle);
            if (incident.ArrestedSuspects != null)
                incident.ArrestedSuspects.RemoveAll(value => value != null
                    && value.Handle == suspectHandle);
            if (incident.CompliantSuspects != null)
                incident.CompliantSuspects.RemoveAll(value => value != null
                    && value.Handle == suspectHandle);
            if (incident.Suspect != null
                && incident.Suspect.Handle == suspectHandle)
                incident.Suspect = LiveSuspects(incident).FirstOrDefault();

            _suspectInjuries.Remove(suspectHandle);
            _lastCompliantTaskAt.Remove(suspectHandle);
            ClearSecuredPoseTracking(suspectHandle);
            Blip suspectBlip;
            if (_suspectBlips.TryGetValue(suspectHandle, out suspectBlip))
            {
                CleanupBlip(suspectBlip);
                _suspectBlips.Remove(suspectHandle);
            }

            LogRuntime("POLICE_DISPATCH_MANAGED_SUSPECT_ACTOR_LOST",
                "Incident=" + incident.Id
                + "; Ped=" + suspectHandle
                + "; IncidentState=" + incident.State
                + "; EntityNoLongerExists=true"
                + "; DeadState=UnavailableNotClassifiedAsDeath"
                + "; MissingSamples=" + observation.MissingSamples
                + "; ConfirmationMilliseconds="
                    + ManagedSuspectMissingConfirmationMilliseconds
                + "; LastKnownPosition="
                    + DescribePosition(observation.LastKnownPosition)
                + "; ArrestAborted=" + wasHandcuffTarget
                + "; SuccessfulArrest=false; PlayerSuccess=false"
                + "; RemovalSource=Unknown; Result=ManagedActorLostBranch");

            List<Ped> remainingSuspects = LiveSuspects(incident).ToList();
            if (remainingSuspects.Count == 0
                && CurrentArrestedSuspects.Count() == 0)
            {
                Fail("A managed Dispatch suspect actor was removed before its branch completed. The actor was not counted as dead, arrested, or a Police Player success.");
                return;
            }

            MaintainSuspectBlips(now, true);
            Notify("A managed Dispatch suspect was lost. That branch is not credited as an arrest or Police Player success; continue with any remaining suspects.");
        }

        private void ClearManagedSuspectPresenceObservations()
        {
            _managedSuspectPresenceObservations.Clear();
            _managedActorLostSuspectHandles.Clear();
            _managedSuspectPresenceOccurrenceId = null;
            _managedActorLostOccurrenceId = null;
            _nextManagedSuspectPresenceCheckAt = DateTime.MinValue;
        }

        private void ObserveDispatchSceneActorTransitions(DateTime now)
        {
            if (now < _nextDispatchSceneActorObservationAt)
                return;
            _nextDispatchSceneActorObservationAt = now.AddMilliseconds(
                DispatchSceneActorObservationIntervalMilliseconds);

            LSPDDispatchEvent incident = _incident ?? _pendingOffer;
            if (incident == null)
            {
                ClearDispatchSceneActorObservations();
                return;
            }

            if (!string.Equals(_observedDispatchSceneOccurrenceId, incident.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                ClearDispatchSceneActorObservations();
                _observedDispatchSceneOccurrenceId = incident.Id;
                _dispatchSceneActorObservationWindowStartedAt = now;
            }
            if (_dispatchSceneActorObservationWindowStartedAt == DateTime.MinValue)
                _dispatchSceneActorObservationWindowStartedAt = now;
            if (now - _dispatchSceneActorObservationWindowStartedAt
                >= TimeSpan.FromSeconds(DispatchSceneActorObservationWindowSeconds))
                return;

            IEnumerable<Ped> suspects = incident.Suspects != null
                && incident.Suspects.Count > 0
                ? incident.Suspects
                : incident.Suspect == null
                    ? Enumerable.Empty<Ped>() : new[] { incident.Suspect };
            var actorEntries = suspects
                .Where(actor => actor != null)
                .Select(actor => Tuple.Create(actor, "Suspect"))
                .Concat(incident.Victim == null
                    ? Enumerable.Empty<Tuple<Ped, string>>()
                    : new[] { Tuple.Create(incident.Victim, "Victim") })
                .Concat((incident.AdditionalParticipants ?? new List<Ped>())
                    .Where(actor => actor != null)
                    .Select(actor => Tuple.Create(actor, "Participant")))
                .GroupBy(entry => entry.Item1.Handle)
                .Select(group => group.First())
                .Take(MaximumDispatchSceneActorsToObserve + 1)
                .ToList();

            if (actorEntries.Count > MaximumDispatchSceneActorsToObserve)
            {
                LogDebugTransition(
                    "POLICE_DISPATCH_SCENE_ACTOR_OBSERVATION_CAP",
                    "Occurrence=" + incident.Id + "; Limit=" + MaximumDispatchSceneActorsToObserve,
                    "Occurrence=" + incident.Id
                    + "; ActorCountExceeded=" + actorEntries.Count
                    + "; ObservationLimit=" + MaximumDispatchSceneActorsToObserve
                    + "; AdditionalActors=NotSampled");
                actorEntries = actorEntries
                    .Take(MaximumDispatchSceneActorsToObserve)
                    .ToList();
            }

            var observedHandles = new HashSet<int>();
            bool occurrenceIsActive = ReferenceEquals(_incident, incident);
            string preparationState = !incident.ScenePreparationCompleted
                ? "Preparing"
                : occurrenceIsActive && incident.State == LSPDDispatchState.Offered
                    ? "OfferedHiddenUntilAcceptance"
                    : occurrenceIsActive ? "AcceptedOrActive" : "PendingOffer";
            string expectedTaskOwner = preparationState == "Preparing"
                || preparationState == "OfferedHiddenUntilAcceptance"
                || preparationState == "PendingOffer"
                ? "DispatchPreparation;ActorFrozenAndAmbientTasksBlocked"
                : "DispatchRuntime";

            foreach (Tuple<Ped, string> entry in actorEntries)
            {
                Ped actor = entry.Item1;
                int handle;
                try { handle = actor.Handle; }
                catch { continue; }
                if (handle <= 0)
                    continue;
                observedHandles.Add(handle);

                bool exists = false;
                bool visible = false;
                bool frozen = false;
                bool blocksAmbientEvents = false;
                bool dead = false;
                bool inVehicle = false;
                int entityInteriorId = int.MinValue;
                int coordinateInteriorId = int.MinValue;
                int roomKey = int.MinValue;
                Vector3 position = Vector3.Zero;
                string captureFailure = string.Empty;
                try
                {
                    exists = actor.Exists();
                    if (exists)
                    {
                        position = actor.Position;
                        visible = Function.Call<bool>(Hash.IS_ENTITY_VISIBLE, actor);
                        frozen = actor.IsPositionFrozen;
                        blocksAmbientEvents = actor.BlockPermanentEvents;
                        dead = actor.IsDead;
                        inVehicle = actor.IsInVehicle();
                        entityInteriorId = Function.Call<int>(
                            Hash.GET_INTERIOR_FROM_ENTITY, actor);
                        coordinateInteriorId = Function.Call<int>(
                            Hash.GET_INTERIOR_AT_COORDS,
                            position.X, position.Y, position.Z);
                        roomKey = Function.Call<int>(Hash.GET_ROOM_KEY_FROM_ENTITY, actor);
                    }
                }
                catch (Exception ex)
                {
                    captureFailure = ex.GetType().Name;
                }

                bool claimedAmbient = incident.VictimWasExistingAmbient
                        && incident.Victim != null
                        && incident.Victim.Handle == handle
                    || incident.ExistingAmbientSceneParticipantHandles != null
                        && incident.ExistingAmbientSceneParticipantHandles.Contains(handle);
                string state = "Exists=" + exists
                    + ";Visible=" + visible
                    + ";Frozen=" + frozen
                    + ";BlocksAmbientEvents=" + blocksAmbientEvents
                    + ";Dead=" + dead
                    + ";InVehicle=" + inVehicle
                    + ";EntityInteriorId=" + (entityInteriorId == int.MinValue
                        ? "Unavailable" : entityInteriorId.ToString(CultureInfo.InvariantCulture))
                    + ";CoordinateInteriorId=" + (coordinateInteriorId == int.MinValue
                        ? "Unavailable" : coordinateInteriorId.ToString(CultureInfo.InvariantCulture))
                    + ";RoomKey=" + (roomKey == int.MinValue
                        ? "Unavailable" : roomKey.ToString(CultureInfo.InvariantCulture))
                    + ";PreparationState=" + preparationState
                    + ";ExpectedTaskOwner=" + expectedTaskOwner
                    + ";ActorOwner=" + (claimedAmbient ? "AmbientWorldClaim" : "Dispatch")
                    + ";NativeTaskOwner=NotExposedBySHVDN"
                    + ";CaptureFailure=" + captureFailure;

                DispatchSceneActorObservation observation;
                bool hasPrevious = _dispatchSceneActorObservations.TryGetValue(
                    handle, out observation);
                if (!hasPrevious)
                    observation = new DispatchSceneActorObservation();
                bool moved = exists && observation.HasPosition
                    && position.DistanceTo(observation.LastPosition)
                        >= DispatchSceneActorObservationMovementMeters;
                if (moved)
                    observation.MovementRevision++;

                bool changed = !hasPrevious
                    || !string.Equals(observation.State, state, StringComparison.Ordinal)
                    || moved;
                if (changed)
                {
                    string transitionState = state
                        + ";MovementRevision=" + observation.MovementRevision;
                    if (observation.DiagnosticRecordCount
                        < MaximumDispatchSceneActorTransitionRecords)
                    {
                        LogDebugTransition(
                            "POLICE_DISPATCH_SCENE_ACTOR_" + handle,
                            transitionState,
                            "Occurrence=" + incident.Id
                            + "; Handle=" + handle
                            + "; Role=" + entry.Item2
                            + "; Exists=" + exists
                            + "; Visible=" + visible
                            + "; Frozen=" + frozen
                            + "; BlocksAmbientEvents=" + blocksAmbientEvents
                            + "; Position=" + (exists ? position.ToString() : "Unavailable")
                            + "; EntityInteriorId=" + (entityInteriorId == int.MinValue
                                ? "Unavailable" : entityInteriorId.ToString(CultureInfo.InvariantCulture))
                            + "; CoordinateInteriorId=" + (coordinateInteriorId == int.MinValue
                                ? "Unavailable" : coordinateInteriorId.ToString(CultureInfo.InvariantCulture))
                            + "; RoomKey=" + (roomKey == int.MinValue
                                ? "Unavailable" : roomKey.ToString(CultureInfo.InvariantCulture))
                            + "; PreparationState=" + preparationState
                            + "; ExpectedTaskOwner=" + expectedTaskOwner
                            + "; ActorOwner=" + (claimedAmbient ? "AmbientWorldClaim" : "Dispatch")
                            + "; NativeTaskOwner=NotExposedBySHVDN"
                            + "; MovementRevision=" + observation.MovementRevision
                            + "; CleanupReason=LoggedByPOLICE_DISPATCH_ACTOR_CLEANUP");
                        observation.DiagnosticRecordCount++;
                    }
                    observation.State = state;
                    if (exists)
                    {
                        observation.LastPosition = position;
                        observation.HasPosition = true;
                    }
                    else
                        observation.HasPosition = false;
                    _dispatchSceneActorObservations[handle] = observation;
                }
            }

            foreach (int previousHandle in _dispatchSceneActorObservations.Keys
                .Where(handle => !observedHandles.Contains(handle)).ToArray())
            {
                ClearDebugTransition("POLICE_DISPATCH_SCENE_ACTOR_" + previousHandle);
                _dispatchSceneActorObservations.Remove(previousHandle);
            }
        }

        private void ClearDispatchSceneActorObservations()
        {
            foreach (int handle in _dispatchSceneActorObservations.Keys.ToArray())
                ClearDebugTransition("POLICE_DISPATCH_SCENE_ACTOR_" + handle);
            _dispatchSceneActorObservations.Clear();
            _observedDispatchSceneOccurrenceId = null;
            _dispatchSceneActorObservationWindowStartedAt = DateTime.MinValue;
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
                        false,
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

                string threatPoseReason = string.Empty;
                if (_ambientWorld == null
                    || !_ambientWorld.TryApplyDispatchParticipantThreatPose(
                        incident, participant, 60000, out threatPoseReason))
                    LogDebug("POLICE_DISPATCH_SCENE_PARTICIPANT_THREAT_POSE_FAILED",
                        "Occurrence=" + incident.Id
                        + "; Handle=" + participant.Handle
                        + "; Pool=" + incident.SceneParticipantPoolId
                        + "; Owner=AmbientWorld; Reason=" + (_ambientWorld == null
                            ? "AmbientWorld is not attached by PoliceCore." : threatPoseReason));
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
                    if (!SetPreparedDispatchSceneActorVisible(incident, suspect, false))
                    {
                        suspect.Delete();
                        failureReason = "SuspectIndex=" + index
                            + "; An interior suspect could not be hidden until preflight completes.";
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

            if (incident.SceneResolution != null
                && LSPDDispatchEvent.RequiresOutdoorSuspectOccurrenceRelationshipGroup(
                    required,
                    incident.SceneResolution.IsInteriorScene)
                && !EnsureDispatchSuspectOccurrenceRelationshipGroup(
                    incident, out failureReason))
                return false;

            return true;
        }

        private bool EnsureDispatchSuspectOccurrenceRelationshipGroup(
            LSPDDispatchEvent incident,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (incident == null)
            {
                failureReason = "The Dispatch occurrence is unavailable.";
                return false;
            }

            try
            {
                if (incident.SuspectOccurrenceRelationshipGroupHash == 0)
                {
                    incident.SuspectOccurrenceRelationshipGroupName =
                        "LSDSP_" + Guid.NewGuid().ToString("N");
                    using (OutputArgument groupOutput = new OutputArgument())
                    {
                        Function.Call<bool>(
                            Hash.ADD_RELATIONSHIP_GROUP,
                            incident.SuspectOccurrenceRelationshipGroupName,
                            groupOutput);
                        incident.SuspectOccurrenceRelationshipGroupHash =
                            groupOutput.GetResult<int>();
                    }
                    if (incident.SuspectOccurrenceRelationshipGroupHash == 0)
                        incident.SuspectOccurrenceRelationshipGroupHash = unchecked((int)
                            StringHash.AtStringHash(
                                incident.SuspectOccurrenceRelationshipGroupName, 0));
                    if (incident.SuspectOccurrenceRelationshipGroupHash == 0)
                        throw new InvalidOperationException(
                            "GTA did not provide a Dispatch suspect relationship group.");
                }

                int groupHash = incident.SuspectOccurrenceRelationshipGroupHash;
                if (!incident.SuspectOccurrenceRelationshipGroupConfigured)
                {
                    Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                        0, groupHash, groupHash);
                    int selfRelationship = Function.Call<int>(
                        Hash.GET_RELATIONSHIP_BETWEEN_GROUPS,
                        groupHash, groupHash);
                    if (selfRelationship != 0)
                        throw new InvalidOperationException(
                            "The occurrence group did not retain its Companion self-relationship.");
                }

                Ped[] suspects = TrackedSuspects(incident).ToArray();
                foreach (Ped suspect in suspects)
                {
                    if (suspect == null || !suspect.Exists())
                        throw new InvalidOperationException(
                            "An authored suspect Ped became invalid before group assignment.");

                    if (!incident.OriginalSuspectRelationshipGroupByHandle.ContainsKey(
                            suspect.Handle))
                        incident.OriginalSuspectRelationshipGroupByHandle[suspect.Handle] =
                            Function.Call<int>(
                                Hash.GET_PED_RELATIONSHIP_GROUP_HASH, suspect);

                    int currentGroup = Function.Call<int>(
                        Hash.GET_PED_RELATIONSHIP_GROUP_HASH, suspect);
                    if (currentGroup != groupHash)
                    {
                        Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH,
                            suspect, groupHash);
                        currentGroup = Function.Call<int>(
                            Hash.GET_PED_RELATIONSHIP_GROUP_HASH, suspect);
                    }
                    if (!LSPDDispatchEvent.IsPedInDispatchSuspectOccurrenceGroup(
                            currentGroup, groupHash))
                        throw new InvalidOperationException(
                            "GTA did not assign suspect " + suspect.Handle
                            + " to the occurrence group.");
                }

                bool newlyConfigured =
                    !incident.SuspectOccurrenceRelationshipGroupConfigured;
                incident.SuspectOccurrenceRelationshipGroupConfigured = true;
                incident.SuspectOccurrenceRelationshipGroupFailureLogged = false;
                if (newlyConfigured)
                    LogRuntime(
                        "POLICE_DISPATCH_OCCURRENCE_SUSPECT_GROUP_RELATIONSHIP_READY",
                        "Occurrence=" + incident.Id
                        + "; Group=" + groupHash
                        + "; SelfRelationship=Companion"
                        + "; Outdoor=true"
                        + "; Suspects=" + string.Join(",",
                            suspects.Select(suspect => suspect.Handle))
                        + "; ExplicitEscalationTasksPreserved=true");
                return true;
            }
            catch (Exception ex)
            {
                failureReason = "Suspect occurrence relationship setup failed: "
                    + ex.GetType().Name;
                if (!incident.SuspectOccurrenceRelationshipGroupFailureLogged)
                {
                    incident.SuspectOccurrenceRelationshipGroupFailureLogged = true;
                    LogException(
                        "POLICE_DISPATCH_OCCURRENCE_SUSPECT_GROUP_RELATIONSHIP_FAILED",
                        ex);
                }
                return false;
            }
        }

        private void RestoreDispatchSuspectOccurrenceRelationshipGroup(
            LSPDDispatchEvent incident,
            string cleanupReason)
        {
            if (incident == null
                || incident.SuspectOccurrenceRelationshipGroupHash == 0)
                return;

            int groupHash = incident.SuspectOccurrenceRelationshipGroupHash;
            bool groupCanBeRemoved = true;
            foreach (KeyValuePair<int, int> entry in
                incident.OriginalSuspectRelationshipGroupByHandle.ToArray())
            {
                try
                {
                    Ped suspect = Entity.FromHandle(entry.Key) as Ped;
                    if (suspect == null || !suspect.Exists())
                        continue;

                    int currentGroup = Function.Call<int>(
                        Hash.GET_PED_RELATIONSHIP_GROUP_HASH, suspect);
                    if (!LSPDDispatchEvent.IsPedInDispatchSuspectOccurrenceGroup(
                            currentGroup, groupHash))
                        continue;

                    Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH,
                        suspect, entry.Value);
                    currentGroup = Function.Call<int>(
                        Hash.GET_PED_RELATIONSHIP_GROUP_HASH, suspect);
                    if (LSPDDispatchEvent.IsPedInDispatchSuspectOccurrenceGroup(
                            currentGroup, groupHash))
                    {
                        groupCanBeRemoved = false;
                        LogRuntime(
                            "POLICE_DISPATCH_OCCURRENCE_SUSPECT_GROUP_RESTORE_FAILED",
                            "Occurrence=" + incident.Id
                            + "; Suspect=" + entry.Key
                            + "; Group=" + groupHash
                            + "; OriginalGroup=" + entry.Value
                            + "; Reason=" + (cleanupReason ?? string.Empty));
                        continue;
                    }
                    LogRuntime(
                        "POLICE_DISPATCH_OCCURRENCE_SUSPECT_GROUP_RESTORED",
                        "Occurrence=" + incident.Id
                        + "; Suspect=" + entry.Key
                        + "; Group=" + groupHash
                        + "; OriginalGroup=" + entry.Value
                        + "; Reason=" + (cleanupReason ?? string.Empty));
                }
                catch (Exception ex)
                {
                    groupCanBeRemoved = false;
                    LogException(
                        "POLICE_DISPATCH_OCCURRENCE_SUSPECT_GROUP_RESTORE_FAILED",
                        ex);
                }
            }

            if (!groupCanBeRemoved)
            {
                LogRuntime(
                    "POLICE_DISPATCH_OCCURRENCE_SUSPECT_GROUP_RETAINED",
                    "Occurrence=" + incident.Id
                    + "; Group=" + groupHash
                    + "; Reason=OneOrMorePedsStillReferenceGroup");
                return;
            }

            try
            {
                Function.Call(Hash.REMOVE_RELATIONSHIP_GROUP, groupHash);
                LogRuntime(
                    "POLICE_DISPATCH_OCCURRENCE_SUSPECT_GROUP_REMOVED",
                    "Occurrence=" + incident.Id
                    + "; Group=" + groupHash
                    + "; Reason=" + (cleanupReason ?? string.Empty));
                incident.SuspectOccurrenceRelationshipGroupHash = 0;
                incident.SuspectOccurrenceRelationshipGroupName = string.Empty;
                incident.SuspectOccurrenceRelationshipGroupConfigured = false;
                incident.OriginalSuspectRelationshipGroupByHandle.Clear();
            }
            catch (Exception ex)
            {
                LogException(
                    "POLICE_DISPATCH_OCCURRENCE_SUSPECT_GROUP_REMOVE_FAILED",
                    ex);
            }
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
        private void PlaySceneActivity(Ped suspect, string type)
        {
            if (_incident == null || _ambientWorld == null
                || suspect == null || !suspect.Exists())
                return;
            _ambientWorld.StartDispatchPassiveSceneActivity(_incident, suspect, type);
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
            LogRuntimeTransition("POLICE_DISPATCH_PLAYER_RADIUS_OUTDOOR_FALLBACK_CANDIDATES_READY",
                "EligibleVehicle=" + eligibleVehicle.Count
                + ";MovingRoad=" + movingRoad.Count
                + ";ParkedOrRoadside=" + parkedVehicle.Count
                + ";EligibleRoadFoot=" + eligibleRoadFoot.Count
                + ";EligibleGenericFoot=" + eligibleGenericFoot.Count
                + ";RoadScenesAvailable=" + includeRoadScenes,
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
            bool playerInteriorIdAvailable,
            ISet<string> excludedLocationIds)
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
                    out eligibilityFailure,
                    excludedLocationIds))
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
                        playerInteriorIdAvailable,
                        excludedLocationIds))
                    nearbyCatalogEligible.Add(definition);
            }
            List<LSPDDispatchEventDefinition> preferredInterior = nearbyCatalogEligible
                .Where(definition => definition.RequiresMappedInterior
                    || !string.IsNullOrWhiteSpace(definition.InteriorLocationId))
                .ToList();
            if (preferredInterior.Count == 0 && interiorEligibilityFailures.Count > 0)
            {
                string stableEligibilityState = BuildInteriorEligibilityStateSignature(
                    interiorEligibilityFailures);
                LogDebugTransition(
                    "POLICE_DISPATCH_INTERIOR_CATALOG_ELIGIBILITY_REJECTED",
                    "PlayerLiveInteriorId=" + playerInteriorId
                    + "; PlayerInteriorNativeAvailable=" + playerInteriorIdAvailable
                    + "; " + stableEligibilityState,
                    "PlayerLiveInteriorId=" + playerInteriorId
                    + "; PlayerInteriorNativeAvailable=" + playerInteriorIdAvailable
                    + "; RejectedDefinitions=" + interiorEligibilityFailures.Count
                    + "; FirstRejected=" + string.Join(" | ",
                        interiorEligibilityFailures.Take(3)));
            }
            else
            {
                ClearDebugTransition(
                    "POLICE_DISPATCH_INTERIOR_CATALOG_ELIGIBILITY_REJECTED");
            }
            bool playerInsideLiveInterior = playerInteriorIdAvailable
                && playerInteriorId != 0;
            if (!playerInsideLiveInterior)
            {
                ClearDebugTransition(
                    "POLICE_DISPATCH_NO_INTERIOR_CANDIDATE_WHILE_INSIDE");
            }
            // Keep compatible local mapped interiors first, then consider the
            // rest of the nearby catalog and authored event set. The selected
            // definition still has to resolve and preflight its own physical
            // scene before Dispatch can offer it.
            if (playerInsideLiveInterior && preferredInterior.Count == 0)
            {
                string stableEligibilityState = BuildInteriorEligibilityStateSignature(
                    interiorEligibilityFailures);
                LogDebugTransition(
                    "POLICE_DISPATCH_NO_INTERIOR_CANDIDATE_WHILE_INSIDE",
                    "PlayerLiveInteriorId=" + playerInteriorId
                    + "; PlayerInteriorNativeAvailable=" + playerInteriorIdAvailable
                    + "; " + stableEligibilityState,
                    "PlayerLiveInteriorId=" + playerInteriorId
                    + "; RejectedInteriorDefinitions=" + interiorEligibilityFailures.Count
                    + "; FirstRejected=" + string.Join(" | ", interiorEligibilityFailures.Take(3))
                    + "; OtherCatalogCandidates=" + eligible.Count);
            }
            else
            {
                ClearDebugTransition(
                    "POLICE_DISPATCH_NO_INTERIOR_CANDIDATE_WHILE_INSIDE");
            }

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
            LogRuntimeTransition(
                "POLICE_DISPATCH_CATALOG_CANDIDATES_READY",
                "EligibleDefinitions=" + eligible.Count
                + ";PreferredMappedInterior=" + preferredInterior.Count
                + ";OtherNearbyCatalog=" + otherNearby.Count
                + ";AuthoredAreaFallback=" + authoredFallback.Count
                + ";PlayerLiveInteriorId=" + playerInteriorId
                + ";PlayerInteriorKnown=" + playerInteriorIdAvailable,
                "EligibleDefinitions=" + eligible.Count
                + "; PreferredMappedInterior=" + preferredInterior.Count
                + "; OtherNearbyCatalog=" + otherNearby.Count
                + "; AuthoredAreaFallback=" + authoredFallback.Count
                + "; PlayerLiveInteriorId=" + playerInteriorId
                + "; SelectionSource=CompleteCompatibleCatalog");
            if (eligible.Count == 0)
            {
                LogRuntimeTransition(
                    "POLICE_DISPATCH_NO_LOCATION_ELIGIBLE",
                    "PlayerLiveInteriorId=" + playerInteriorId
                    + ";PlayerInteriorKnown=" + playerInteriorIdAvailable
                    + ";CatalogDefinitions=" + _definitions.Count,
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
            out string rejectionReason,
            ISet<string> excludedLocationIds = null)
        {
            rejectionReason = string.Empty;
            if (definition == null)
            {
                rejectionReason = "Stage=EventDefinition; FirstRejectedGate=DefinitionUnavailable";
                return false;
            }
            if (definition.RequiresExteriorScene && playerInteriorIdAvailable && playerInteriorId != 0)
            {
                rejectionReason = "Stage=ExteriorSceneContext; Result=PlayerInsideInterior";
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
                        out rejectionReason,
                        excludedLocationIds);
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
                        playerInteriorIdAvailable,
                        excludedLocationIds));
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

        private void ResolveDispatchTerminalOwnership(
            Ped suspect,
            LSPDSuspectInjuryState state,
            Ped player,
            LSPDDispatchEvent incident,
            DateTime now,
            bool fatalThresholdReached,
            out bool reconciliationPending,
            out string evidence)
        {
            reconciliationPending = false;
            evidence = string.Empty;
            if (suspect == null || state == null)
                return;

            if (state.FatalOwnershipReconciliationComplete)
            {
                reconciliationPending = false;
                evidence = "Outcome=" + state.FatalOutcomeReason
                    + "; Owner=" + (state.FatalOutcomeOwnerClass ?? "Unknown")
                    + "; Source=" + (state.FatalOutcomeEvidenceSource ?? "none");
                return;
            }

            if (state.FatalOwnershipReconciliationStartedAt == DateTime.MinValue)
                state.FatalOwnershipReconciliationStartedAt = now;

            DispatchDamageAttackerClass ownerClass;
            int ownerHandle;
            string evidenceSource;
            DispatchDamageOwnershipEvidence latestConfirmedDamage =
                state.ConfirmedDamageOwnershipHistory.LastOrDefault();
            if (TryReadDispatchTerminalSourceOwner(
                suspect, player, incident, out ownerClass, out ownerHandle))
            {
                evidenceSource = "NativeDeathSource";
            }
            else if (latestConfirmedDamage != null)
            {
                ownerClass = latestConfirmedDamage.AttackerClass;
                ownerHandle = latestConfirmedDamage.AttackerHandle;
                evidenceSource = "ConfirmedDamageOwnershipHistory";
            }
            else if (fatalThresholdReached)
            {
                // The explicit threshold is reached only by a fresh,
                // attacker-corroborated Police Player vital weapon hit.
                ownerClass = DispatchDamageAttackerClass.PolicePlayer;
                ownerHandle = state.LastDamageAttackerHandle;
                evidenceSource = "ConfirmedPolicePlayerFatalThreshold";
            }
            else
            {
                ownerClass = DispatchDamageAttackerClass.UnknownExternalActor;
                ownerHandle = 0;
                evidenceSource = null;
            }

            if (!string.IsNullOrWhiteSpace(evidenceSource))
            {
                bool authorizedDefeat = IsAuthorizedDispatchDefeatOwner(ownerClass);
                state.FatalOutcomeOwnerClass = ownerClass.ToString();
                state.FatalOutcomeOwnerHandle = ownerHandle;
                state.FatalOutcomeEvidenceSource = evidenceSource;
                state.FatalOutcomeExpected = authorizedDefeat;
                state.FatalOutcomeCompromised = !authorizedDefeat;
                state.FatalCauseConfirmed = true;
                state.FatalOwnershipReconciliationComplete = true;
                state.FatalOwnershipReconciliationPendingLogged = false;
                state.FatalOutcomeReason = authorizedDefeat
                    ? ownerClass == DispatchDamageAttackerClass.PolicePlayer
                        ? "ConfirmedDefeatByPolicePlayer"
                        : "ConfirmedDefeatByProjectBackup"
                    : "ConfirmedTerminalDamageByUnauthorizedOwner";

                if (latestConfirmedDamage != null
                    && string.Equals(evidenceSource,
                        "ConfirmedDamageOwnershipHistory", StringComparison.Ordinal))
                {
                    evidence = "Source=" + evidenceSource
                        + "; Owner=" + ownerClass
                        + "; Attacker=" + ownerHandle
                        + "; Weapon=" + latestConfirmedDamage.WeaponHash
                        + "; Kind=" + latestConfirmedDamage.DamageKind
                        + "; GameTime=" + latestConfirmedDamage.GameTime
                        + "; ConfirmedAt="
                        + latestConfirmedDamage.ConfirmedAt.ToString(
                            "O", CultureInfo.InvariantCulture)
                        + "; RawBone=" + latestConfirmedDamage.RawBone;
                }
                else
                {
                    evidence = "Source=" + evidenceSource
                        + "; Owner=" + ownerClass
                        + "; Attacker=" + ownerHandle;
                }

                LogRuntime(authorizedDefeat
                        ? "POLICE_DISPATCH_SUSPECT_DEFEATED_BY_AUTHORIZED_POLICE"
                        : "POLICE_DISPATCH_SUSPECT_TERMINAL_OWNER_COMPROMISED",
                    "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                    + "; Outcome=" + state.FatalOutcomeReason
                    + "; " + evidence
                    + "; HistoryCount="
                    + state.ConfirmedDamageOwnershipHistory.Count);
                return;
            }

            double reconciliationAgeMilliseconds = Math.Max(0,
                (now - state.FatalOwnershipReconciliationStartedAt).TotalMilliseconds);
            if (reconciliationAgeMilliseconds
                < DispatchFatalOwnershipReconciliationMilliseconds)
            {
                reconciliationPending = true;
                state.FatalOutcomeExpected = false;
                state.FatalOutcomeCompromised = false;
                state.FatalCauseConfirmed = false;
                state.FatalOutcomeReason = "TerminalOwnershipReconciliationPending";
                evidence = "Incident=" + state.OccurrenceId
                    + "; Ped=" + suspect.Handle
                    + "; ReconciliationAgeMs="
                    + reconciliationAgeMilliseconds.ToString(
                        "0", CultureInfo.InvariantCulture)
                    + "; ReconciliationLimitMs="
                    + DispatchFatalOwnershipReconciliationMilliseconds
                    + "; HistoryCount=0; NativeDeathSource=unavailable";
                if (!state.FatalOwnershipReconciliationPendingLogged)
                {
                    state.FatalOwnershipReconciliationPendingLogged = true;
                    LogRuntime(
                        "POLICE_DISPATCH_SUSPECT_TERMINAL_RECONCILIATION_PENDING",
                        evidence);
                }
                return;
            }

            state.FatalOutcomeExpected = false;
            state.FatalOutcomeCompromised = false;
            state.FatalCauseConfirmed = false;
            state.FatalOutcomeOwnerClass = string.Empty;
            state.FatalOutcomeOwnerHandle = 0;
            state.FatalOutcomeEvidenceSource = "NoUsableOwnershipEvidence";
            state.FatalOutcomeReason = "UnknownFatalInjuryAfterBoundedReconciliation";
            state.FatalOwnershipReconciliationComplete = true;
            evidence = "Incident=" + state.OccurrenceId
                + "; Ped=" + suspect.Handle
                + "; Outcome=UnknownFatalInjuryAfterBoundedReconciliation"
                + "; ReconciliationAgeMs="
                + reconciliationAgeMilliseconds.ToString(
                    "0", CultureInfo.InvariantCulture)
                + "; ReconciliationLimitMs="
                + DispatchFatalOwnershipReconciliationMilliseconds
                + "; HistoryCount=0; NativeDeathSource=unavailable";
            LogRuntime("POLICE_DISPATCH_SUSPECT_UNKNOWN_FATAL_INJURY",
                evidence);
        }

        private bool TryReadDispatchTerminalSourceOwner(
            Ped suspect,
            Ped player,
            LSPDDispatchEvent incident,
            out DispatchDamageAttackerClass ownerClass,
            out int ownerHandle)
        {
            ownerClass = DispatchDamageAttackerClass.UnknownExternalActor;
            ownerHandle = 0;
            if (suspect == null || !suspect.Exists())
                return false;
            try
            {
                ownerHandle = Function.Call<int>(
                    Hash.GET_PED_SOURCE_OF_DEATH, suspect);
                if (ownerHandle <= 0 || ownerHandle == suspect.Handle)
                    return false;
                Entity source = Entity.FromHandle(ownerHandle);
                if (source == null || !source.Exists())
                    return false;
                ownerClass = ClassifyDispatchDamageAttacker(
                    source, ownerHandle, player, incident);
                return true;
            }
            catch
            {
                ownerHandle = 0;
                return false;
            }
        }

        private bool HasUnexpectedDispatchSuspectDeath(
            LSPDDispatchEvent incident,
            out string evidence,
            out bool reconciliationPending,
            out bool compromised)
        {
            evidence = string.Empty;
            reconciliationPending = false;
            compromised = false;
            if (incident == null)
                return false;
            foreach (Ped suspect in TrackedSuspects(incident))
            {
                if (suspect == null || !suspect.Exists()
                    || incident.ConvoyCustodySuspectHandles.Contains(suspect.Handle)
                    || (!suspect.IsDead && suspect.Health > 0))
                    continue;

                LSPDSuspectInjuryState state;
                if (!_suspectInjuries.TryGetValue(suspect.Handle, out state))
                {
                    state = new LSPDSuspectInjuryState
                    {
                        Actor = suspect,
                        OccurrenceId = incident.Id,
                        LastHealth = 0,
                        LastMaximumHealth = Math.Max(1, suspect.MaxHealth),
                        Fatal = true
                    };
                    _suspectInjuries[suspect.Handle] = state;
                }
                else if (!state.Fatal)
                {
                    MaintainDispatchSuspectInjury(
                        suspect, DateTime.UtcNow, Game.Player.Character);
                    if (!suspect.IsDead && suspect.Health > 0)
                        continue;
                    state.Fatal = true;
                }

                bool suspectReconciliationPending;
                string ownershipEvidence;
                ResolveDispatchTerminalOwnership(
                    suspect, state, Game.Player.Character, incident,
                    DateTime.UtcNow, false,
                    out suspectReconciliationPending,
                    out ownershipEvidence);
                if (state.FatalOutcomeExpected)
                    continue;
                if (suspectReconciliationPending)
                {
                    reconciliationPending = true;
                    evidence = ownershipEvidence;
                    continue;
                }

                evidence = "Incident=" + incident.Id
                    + "; Ped=" + suspect.Handle
                    + "; Health=" + Math.Max(0, suspect.Health)
                    + "; IsDead=" + suspect.IsDead
                    + "; FatalOutcomeExpected=false"
                    + "; FatalCauseConfirmed=" + state.FatalCauseConfirmed
                    + "; FatalOutcomeCompromised="
                    + state.FatalOutcomeCompromised
                    + "; FatalOutcomeOwnerClass="
                    + (state.FatalOutcomeOwnerClass ?? "Unknown")
                    + "; FatalOutcomeOwnerHandle="
                    + state.FatalOutcomeOwnerHandle
                    + "; FatalOwnershipEvidenceSource="
                    + (state.FatalOutcomeEvidenceSource ?? "none")
                    + "; FatalOutcomeReason=" + state.FatalOutcomeReason
                    + "; CompletionDenied=true";
                compromised = state.FatalOutcomeCompromised;
                return true;
            }
            return false;
        }

        private void Complete(string reason)
        {
            if (_incident == null)
                return;
            LSPDDispatchEvent incident = _incident;
            ObserveManagedDispatchSuspectPresence(DateTime.UtcNow, true);
            if (_incident != incident)
                return;
            if (_managedSuspectPresenceObservations.Values.Any(value => value != null
                && value.MissingSamples > 0 && !value.MissingReported))
                return;
            if (string.Equals(_managedActorLostOccurrenceId, incident.Id,
                    StringComparison.OrdinalIgnoreCase)
                && _managedActorLostSuspectHandles.Count > 0)
            {
                LogRuntime("POLICE_DISPATCH_COMPLETION_BLOCKED_MANAGED_ACTOR_LOST",
                    "Incident=" + incident.Id
                    + "; LostSuspectHandles=" + string.Join(",",
                        _managedActorLostSuspectHandles.OrderBy(value => value)
                            .Select(value => value.ToString(CultureInfo.InvariantCulture)))
                    + "; SuccessfulCompletion=false");
                Fail("A managed Dispatch suspect branch was lost before completion. The incident was not recorded as successfully completed.");
                return;
            }
            string unexpectedDeathEvidence;
            bool ownershipReconciliationPending;
            bool compromisedDeath;
            if (HasUnexpectedDispatchSuspectDeath(
                incident, out unexpectedDeathEvidence,
                out ownershipReconciliationPending,
                out compromisedDeath))
            {
                LogRuntime(compromisedDeath
                        ? "POLICE_DISPATCH_COMPLETION_BLOCKED_COMPROMISED_SUSPECT"
                        : "POLICE_DISPATCH_COMPLETION_BLOCKED_UNEXPLAINED_DEATH",
                    unexpectedDeathEvidence);
                Fail(compromisedDeath
                        ? "Dispatch suspects got compromised. Dispatch Activity ended."
                        : "Dispatch suspect suffered an unknown fatal injury. Dispatch Activity ended.",
                    "lsimmersivelife.police.dispatch.cancelled");
                return;
            }
            if (ownershipReconciliationPending)
            {
                LogRuntime("POLICE_DISPATCH_COMPLETION_WAITING_FOR_TERMINAL_OWNERSHIP",
                    unexpectedDeathEvidence);
                return;
            }
            RecordDispatchLocationRepeatProtection(
                incident, DateTime.UtcNow, "Completed");
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
            ClearSecuredPoseTracking();
            ClearManagedSuspectPresenceObservations();
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
            if (_ambientWorld != null)
                _ambientWorld.CleanupDispatchDisturbanceProp(incident, cleanupReason);
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

            if (_ambientWorld != null)
                _ambientWorld.CleanupDispatchDisturbanceProp(incident, cleanupReason);
            ReleaseIncidentModelRequests(incident);

            // Convoy owns the arrested suspect and its transport after the
            // custody handoff. Dispatch still owns the temporary victim and
            // support actors, so those are always cleaned here.
            if (incident.OwnedByDispatch)
            {
                foreach (Ped suspect in TrackedSuspects(incident).ToArray())
                {
                    int handle = suspect == null ? 0 : suspect.Handle;
                    if (handle != 0
                        && incident.ConvoyCustodySuspectHandles.Contains(handle))
                    {
                        LogRuntime("POLICE_DISPATCH_ACTOR_CLEANUP_SKIPPED",
                            "Occurrence=" + incident.Id
                            + "; Entity=Ped; Handle=" + handle
                            + "; Owner=Convoy; Reason=" + (cleanupReason ?? string.Empty)
                            + "; Deleted=false");
                        continue;
                    }
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
                if (incident.PlayerCommandedSecureSuspectHandles != null)
                    incident.PlayerCommandedSecureSuspectHandles.Clear();
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
            RestoreDispatchSuspectOccurrenceRelationshipGroup(
                incident, cleanupReason);
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

        private HashSet<string> GetActiveDispatchLocationRepeatProtectionIds(
            DateTime now)
        {
            if (_dispatchLocationRepeatFallbackActive)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int repeatMinutes = _settings == null
                ? 0 : _settings.RepeatProtectionMinutes;
            if (repeatMinutes <= 0)
            {
                _completedOrDeclinedLocationAt.Clear();
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string locationId in _completedOrDeclinedLocationAt.Keys.ToArray())
            {
                if (IsDispatchLocationRepeatProtected(
                    _completedOrDeclinedLocationAt,
                    locationId,
                    now,
                    repeatMinutes))
                    active.Add(locationId);
            }
            return active;
        }

        internal static bool IsDispatchLocationRepeatProtected(
            IDictionary<string, DateTime> locationUseTimes,
            string locationId,
            DateTime now,
            int repeatProtectionMinutes)
        {
            if (locationUseTimes == null
                || string.IsNullOrWhiteSpace(locationId)
                || repeatProtectionMinutes <= 0)
                return false;

            DateTime lastUsedAt;
            if (!locationUseTimes.TryGetValue(locationId, out lastUsedAt))
                return false;
            if (now < lastUsedAt.AddMinutes(repeatProtectionMinutes))
                return true;

            locationUseTimes.Remove(locationId);
            return false;
        }

        private void RecordDispatchLocationRepeatProtection(
            LSPDDispatchEvent incident,
            DateTime now,
            string outcome)
        {
            if (incident == null || incident.SceneResolution == null
                || _settings == null || _settings.RepeatProtectionMinutes <= 0)
                return;

            string locationId = incident.SceneResolution.LocationId;
            if (string.IsNullOrWhiteSpace(locationId))
                return;

            _completedOrDeclinedLocationAt[locationId] = now;
            Vector3 repeatAnchor = incident.SceneResolution.SceneCenter;
            foreach (LSPDDispatchLocationDefinition nearby in _locations.Values)
            {
                if (nearby == null || string.IsNullOrWhiteSpace(nearby.Id))
                    continue;
                float distance = HorizontalDistance(repeatAnchor, nearby.Position);
                if (distance <= NearbyDispatchLocationRepeatProtectionMeters)
                    _completedOrDeclinedLocationAt[nearby.Id] = now;
            }
            foreach (string expiredLocationId in _completedOrDeclinedLocationAt.Keys.ToArray())
                IsDispatchLocationRepeatProtected(
                    _completedOrDeclinedLocationAt,
                    expiredLocationId,
                    now,
                    _settings.RepeatProtectionMinutes);
            while (_completedOrDeclinedLocationAt.Count
                > MaximumDispatchLocationRepeatProtectionEntries)
            {
                string oldestLocationId = _completedOrDeclinedLocationAt
                    .OrderBy(entry => entry.Value)
                    .First().Key;
                _completedOrDeclinedLocationAt.Remove(oldestLocationId);
            }

            LogRuntime("POLICE_DISPATCH_LOCATION_REPEAT_PROTECTION_RECORDED",
                "Incident=" + incident.Id
                + "; Definition=" + DefinitionId(incident)
                + "; Location=" + locationId
                + "; NearbyProtected=" + string.Join(",",
                    _completedOrDeclinedLocationAt
                        .Where(entry => entry.Value == now)
                        .Select(entry => entry.Key))
                + "; NearbyRadiusMeters="
                + NearbyDispatchLocationRepeatProtectionMeters.ToString(
                    "0.0", CultureInfo.InvariantCulture)
                + "; Outcome=" + (outcome ?? "Unknown")
                + "; SuppressedUntil=" + now.AddMinutes(
                    _settings.RepeatProtectionMinutes).ToString("O", CultureInfo.InvariantCulture)
                + "; RepeatProtectionMinutes=" + _settings.RepeatProtectionMinutes);
        }

        private DateTime NextDefinitionEligibleAt(DateTime now)
        {
            if (_settings.RepeatProtectionMinutes <= 0 || _definitionOfferedAt.Count == 0)
                return now.AddSeconds(30);

            DateTime earliest = DateTime.MaxValue;
            bool hasNeverOfferedDefinition = false;
            foreach (LSPDDispatchEventDefinition definition in _definitions)
            {
                DateTime lastOffered;
                if (!_definitionOfferedAt.TryGetValue(definition.Id, out lastOffered))
                {
                    hasNeverOfferedDefinition = true;
                    continue;
                }
                DateTime eligibleAt = lastOffered.AddMinutes(_settings.RepeatProtectionMinutes);
                if (eligibleAt < earliest)
                    earliest = eligibleAt;
            }
            if (hasNeverOfferedDefinition)
                return now.AddSeconds(EmptyLocationCandidateRetrySeconds);
            if (earliest == DateTime.MaxValue)
                return now.AddSeconds(30);
            return earliest <= now
                ? now.AddSeconds(EmptyLocationCandidateRetrySeconds)
                : earliest;
        }

        private void CacheEmptyLocationCandidateSearch(
            Ped player,
            int playerInteriorId,
            bool playerInteriorIdAvailable,
            DateTime now)
        {
            if (player == null || !player.Exists())
                return;

            _emptyLocationCandidateSearchCached = true;
            _emptyLocationCandidatePosition = player.Position;
            _emptyLocationCandidateInteriorId = playerInteriorId;
            _emptyLocationCandidateInteriorIdAvailable = playerInteriorIdAvailable;
            _emptyLocationCandidateRetryAt = _nextOfferAt;
            _emptyLocationCandidateInteriorProbeAt = now.AddSeconds(
                EmptyLocationCandidateInteriorProbeSeconds);
        }

        private void InvalidateEmptyLocationCandidateSearch(
            Ped player,
            DateTime now)
        {
            if (!_emptyLocationCandidateSearchCached)
                return;

            if (now >= _emptyLocationCandidateRetryAt)
            {
                ClearEmptyLocationCandidateSearch();
                return;
            }

            bool changed = player.Position.DistanceTo(
                _emptyLocationCandidatePosition)
                >= EmptyLocationCandidateMovementMeters;
            if (!changed && now >= _emptyLocationCandidateInteriorProbeAt)
            {
                _emptyLocationCandidateInteriorProbeAt = now.AddSeconds(
                    EmptyLocationCandidateInteriorProbeSeconds);
                int currentInteriorId = 0;
                bool currentInteriorIdAvailable = _ambientWorld != null
                    && _ambientWorld.TryGetDispatchPlayerInteriorId(
                        player, out currentInteriorId);
                changed = currentInteriorIdAvailable
                        != _emptyLocationCandidateInteriorIdAvailable
                    || (currentInteriorIdAvailable
                        && currentInteriorId != _emptyLocationCandidateInteriorId);
            }

            if (!changed)
                return;

            ClearEmptyLocationCandidateSearch();
            if (_deferredCleanup.Count == 0)
                _nextOfferAt = now;
        }

        private void ClearEmptyLocationCandidateSearch()
        {
            _emptyLocationCandidateSearchCached = false;
            _emptyLocationCandidatePosition = Vector3.Zero;
            _emptyLocationCandidateInteriorId = 0;
            _emptyLocationCandidateInteriorIdAvailable = false;
            _emptyLocationCandidateRetryAt = DateTime.MinValue;
            _emptyLocationCandidateInteriorProbeAt = DateTime.MinValue;
        }

        private bool UpdateDispatchSceneCandidateFailureContext(
            Ped player,
            int playerInteriorId,
            bool playerInteriorKnown,
            DateTime now)
        {
            if (player == null || !player.Exists())
                return false;

            Vector3 playerPosition = player.Position;
            if (!_dispatchSceneFailureCacheContextInitialized)
            {
                _dispatchSceneFailureCacheContextInitialized = true;
                _dispatchSceneFailureCachePlayerPosition = playerPosition;
                _dispatchSceneFailureCachePlayerInteriorId = playerInteriorId;
                _dispatchSceneFailureCachePlayerInteriorKnown = playerInteriorKnown;
                return false;
            }

            bool contextChanged = playerPosition.DistanceTo(
                    _dispatchSceneFailureCachePlayerPosition)
                    >= DispatchSceneFailureCacheMovementMeters
                || playerInteriorKnown != _dispatchSceneFailureCachePlayerInteriorKnown
                || (playerInteriorKnown
                    && playerInteriorId != _dispatchSceneFailureCachePlayerInteriorId);
            if (!contextChanged)
                return false;

            _dispatchSceneCandidateFailures.Clear();
            _dispatchSceneFailureCachePlayerPosition = playerPosition;
            _dispatchSceneFailureCachePlayerInteriorId = playerInteriorId;
            _dispatchSceneFailureCachePlayerInteriorKnown = playerInteriorKnown;
            LogDebugTransition(
                "POLICE_DISPATCH_SCENE_FAILURE_CACHE_INVALIDATED",
                "PlayerInteriorKnown=" + playerInteriorKnown
                    + ";PlayerInteriorId=" + playerInteriorId,
                "Player movement or live-interior change invalidated cached scene failures; "
                    + "the full compatible catalog is eligible for this location context.");
            return true;
        }

        private void PruneDispatchSceneCandidateFailureCache(DateTime now)
        {
            foreach (string key in _dispatchSceneCandidateFailures
                .Where(pair => pair.Value == null || now >= pair.Value.ExpiresAt)
                .Select(pair => pair.Key).ToArray())
                _dispatchSceneCandidateFailures.Remove(key);
        }

        private HashSet<string> GetActiveRejectedDispatchSceneLocationIds(
            Vector3 playerPosition,
            int playerInteriorId,
            bool playerInteriorKnown,
            DateTime now,
            string definitionId)
        {
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, DispatchSceneCandidateFailure> entry
                in _dispatchSceneCandidateFailures)
            {
                // Capacity/role failure for one authored roster is not evidence
                // that a different incident cannot use this physical site.
                if (!IsDispatchSceneFailureForDefinition(entry.Key, definitionId))
                    continue;
                DispatchSceneCandidateFailure failure = entry.Value;
                if (failure == null
                    || !IsDispatchSceneCandidateRejectionCacheActive(
                        now,
                        failure.ExpiresAt,
                        _dispatchSceneFailureCachePlayerPosition,
                        playerPosition,
                        _dispatchSceneFailureCachePlayerInteriorId,
                        playerInteriorId,
                        _dispatchSceneFailureCachePlayerInteriorKnown,
                        playerInteriorKnown,
                        DispatchSceneFailureCacheMovementMeters))
                    continue;
                int separator = entry.Key.IndexOf('|');
                if (separator >= 0 && separator + 1 < entry.Key.Length)
                    locations.Add(entry.Key.Substring(separator + 1));
            }
            return locations;
        }

        internal static bool IsDispatchSceneFailureForDefinition(string key, string definitionId)
        {
            return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(definitionId)
                && key.StartsWith(definitionId + "|", StringComparison.OrdinalIgnoreCase);
        }

        private List<LSPDDispatchLocationDefinition> ExcludeRejectedDispatchSceneCandidateAreas(
            string definitionId,
            IEnumerable<LSPDDispatchLocationDefinition> areas,
            Vector3 currentPlayerPosition,
            int currentInteriorId,
            bool currentInteriorKnown,
            DateTime now,
            out int rejectedCount)
        {
            rejectedCount = 0;
            var result = new List<LSPDDispatchLocationDefinition>();
            foreach (LSPDDispatchLocationDefinition area in areas
                ?? Enumerable.Empty<LSPDDispatchLocationDefinition>())
            {
                if (area == null)
                    continue;
                string key = BuildDispatchSceneCandidateFailureKey(definitionId, area.Id);
                DispatchSceneCandidateFailure failure;
                if (_dispatchSceneCandidateFailures.TryGetValue(key, out failure)
                    && failure != null
                    && IsDispatchSceneCandidateRejectionCacheActive(
                        now,
                        failure.ExpiresAt,
                        _dispatchSceneFailureCachePlayerPosition,
                        currentPlayerPosition,
                        _dispatchSceneFailureCachePlayerInteriorId,
                        currentInteriorId,
                        _dispatchSceneFailureCachePlayerInteriorKnown,
                        currentInteriorKnown,
                        DispatchSceneFailureCacheMovementMeters))
                {
                    rejectedCount++;
                    continue;
                }
                result.Add(area);
            }
            return result;
        }

        private void CacheRejectedDispatchSceneCandidateAreas(
            LSPDDispatchEventDefinition definition,
            IEnumerable<LSPDDispatchLocationDefinition> areas,
            string failure,
            Vector3 playerPosition,
            DateTime now)
        {
            if (definition == null || string.IsNullOrWhiteSpace(failure)
                || failure.IndexOf("PENDING:", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            List<LSPDDispatchLocationDefinition> eligibleAreas = (areas
                ?? Enumerable.Empty<LSPDDispatchLocationDefinition>())
                .Where(area => area != null && area.IsEligibleFor(playerPosition))
                .ToList();
            string reportedLocationId = DiagnosticFieldValue(failure ?? string.Empty, "Location=");
            List<LSPDDispatchLocationDefinition> affectedAreas = !string.IsNullOrWhiteSpace(reportedLocationId)
                    && !string.Equals(reportedLocationId, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? eligibleAreas.Where(area => string.Equals(
                    area.Id, reportedLocationId, StringComparison.OrdinalIgnoreCase)).ToList()
                : eligibleAreas.Count == 1 ? eligibleAreas : new List<LSPDDispatchLocationDefinition>();

            string firstStage = GetDispatchSceneFailureStage(failure);
            string pointId = DiagnosticFieldValue(failure ?? string.Empty, "Point=");
            int lifetimeSeconds = IsPermanentDispatchLocationFailure(failure)
                ? DispatchSceneCandidateFailureCacheSeconds
                : DispatchSceneCandidateTransientFailureCacheSeconds;
            foreach (LSPDDispatchLocationDefinition area in affectedAreas)
                CacheRejectedDispatchSceneCandidate(
                    definition.Id,
                    area.Id,
                    firstStage,
                    now,
                    lifetimeSeconds,
                    pointId);
        }

        private void CacheRejectedDispatchSceneCandidate(
            string definitionId,
            string locationId,
            string firstFailureStage,
            DateTime now,
            int lifetimeSeconds,
            string pointId = null)
        {
            if (string.IsNullOrWhiteSpace(definitionId)
                || string.IsNullOrWhiteSpace(locationId))
                return;

            string key = BuildDispatchSceneCandidateFailureKey(definitionId, locationId);
            _dispatchSceneCandidateFailures[key] = new DispatchSceneCandidateFailure
            {
                ExpiresAt = now.AddSeconds(Math.Max(1, lifetimeSeconds)),
                FirstFailureStage = string.IsNullOrWhiteSpace(firstFailureStage)
                    ? "UnclassifiedSceneFailure" : firstFailureStage,
                PointId = string.IsNullOrWhiteSpace(pointId)
                    || string.Equals(pointId, "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty : pointId
            };
            while (_dispatchSceneCandidateFailures.Count
                > MaximumDispatchSceneCandidateFailureCacheEntries)
            {
                string oldest = _dispatchSceneCandidateFailures
                    .OrderBy(pair => pair.Value == null ? DateTime.MinValue : pair.Value.ExpiresAt)
                    .Select(pair => pair.Key).FirstOrDefault();
                if (oldest == null)
                    break;
                _dispatchSceneCandidateFailures.Remove(oldest);
            }
        }

        private void CacheExpiredPendingSceneFailures(
            IEnumerable<string> failures,
            DateTime now)
        {
            foreach (string failure in failures ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(failure))
                    continue;
                int separator = failure.IndexOf('=');
                if (separator <= 0)
                    continue;
                string definitionId = failure.Substring(0, separator);
                string locationId = DiagnosticFieldValue(failure, "Location=");
                if (string.IsNullOrWhiteSpace(locationId)
                    || string.Equals(locationId, "Unknown", StringComparison.OrdinalIgnoreCase))
                    continue;
                CacheRejectedDispatchSceneCandidate(
                    definitionId,
                    locationId,
                    GetDispatchSceneFailureStage(failure),
                    now,
                    DispatchSceneCandidateTransientFailureCacheSeconds,
                    DiagnosticFieldValue(failure, "Point="));
            }
        }

        internal static string BuildDispatchSceneCandidateFailureKey(
            string definitionId,
            string locationId)
        {
            return (definitionId ?? string.Empty).Trim()
                + "|" + (locationId ?? string.Empty).Trim();
        }

        internal static bool IsDispatchSceneCandidateRejectionCacheActive(
            DateTime now,
            DateTime expiresAt,
            Vector3 cachedPlayerPosition,
            Vector3 currentPlayerPosition,
            int cachedInteriorId,
            int currentInteriorId,
            bool cachedInteriorKnown,
            bool currentInteriorKnown,
            float movementThreshold)
        {
            float movement = currentPlayerPosition.DistanceTo(cachedPlayerPosition);
            return now < expiresAt
                && !float.IsNaN(movement) && !float.IsInfinity(movement)
                && movement < movementThreshold
                && cachedInteriorKnown == currentInteriorKnown
                && (!currentInteriorKnown || cachedInteriorId == currentInteriorId);
        }

        internal static bool IsPermanentDispatchLocationFailure(string failure)
        {
            if (string.IsNullOrWhiteSpace(failure)
                || failure.IndexOf("PENDING:", StringComparison.OrdinalIgnoreCase) >= 0
                || failure.IndexOf("CollisionPending", StringComparison.OrdinalIgnoreCase) >= 0
                || failure.IndexOf("ModelStreamingPending", StringComparison.OrdinalIgnoreCase) >= 0
                || failure.IndexOf("Result=Occupied", StringComparison.OrdinalIgnoreCase) >= 0
                || failure.IndexOf("ExteriorPedOccupancy", StringComparison.OrdinalIgnoreCase) >= 0
                || failure.IndexOf("ExteriorVehicleOccupancy", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            string[] permanentStages =
            {
                "ExteriorApproachGeometry", "ExteriorApproachCorridor",
                "ExteriorApproachRange", "ExteriorGroundNormal",
                "ExteriorInteriorIdentity", "ExteriorNavigationSnap",
                "ExteriorStreetHeight", "ExteriorRoadLane",
                "ExteriorActorGroundAlignment", "ExteriorActorPlacementSnap",
                "ExteriorActorIdentity", "ExteriorActorSceneRange",
                "ActorEntityOutsideSelectedInterior", "InteriorActorIdentity",
                "InteriorActorFloor", "InteriorFloorAlignment"
            };
            return permanentStages.Any(stage => failure.IndexOf(
                stage, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool ShouldContinueAfterPreparedSceneFailure(
            int previousFallthroughAttempts,
            int maximumAttempts)
        {
            return previousFallthroughAttempts >= 0
                && previousFallthroughAttempts < Math.Max(0, maximumAttempts);
        }

        internal static bool IsDispatchSceneRetryDue(DateTime now, DateTime retryAt)
        {
            return retryAt == DateTime.MinValue || now >= retryAt;
        }

        internal static bool IsDispatchScenePendingWindowActive(
            DateTime startedAt,
            DateTime now,
            int maximumPendingSeconds)
        {
            return startedAt != DateTime.MinValue
                && now < startedAt.AddSeconds(Math.Max(0, maximumPendingSeconds));
        }

        internal static bool IsDispatchInteriorPinRetained(
            int liveInteriorId,
            IEnumerable<int> pendingInteriorIds,
            IEnumerable<int> activeInteriorIds)
        {
            return liveInteriorId != 0
                && ((pendingInteriorIds ?? Enumerable.Empty<int>()).Contains(liveInteriorId)
                    || (activeInteriorIds ?? Enumerable.Empty<int>()).Contains(liveInteriorId));
        }

        private static string GetDispatchSceneFailureStage(string failure)
        {
            string[] stages =
            {
                "ExteriorApproachGeometry", "ExteriorApproachCorridor",
                "ExteriorActorGroundAlignment", "ExteriorActorPlacementSnap",
                "ExteriorNavigationSnap", "ExteriorGroundNormal",
                "ExteriorStreetHeight", "ExteriorInteriorIdentity",
                "ActorEntityOutsideSelectedInterior", "InteriorActorIdentity",
                "InteriorActorFloor", "InteriorFloorAlignment"
            };
            foreach (string stage in stages)
                if (!string.IsNullOrWhiteSpace(failure)
                    && failure.IndexOf(stage, StringComparison.OrdinalIgnoreCase) >= 0)
                    return stage;
            return DiagnosticFieldValue(failure ?? string.Empty, "Stage=");
        }

        internal static string BuildDispatchSceneFailureStateSignature(
            IEnumerable<string> failures)
        {
            var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string failure in failures ?? Enumerable.Empty<string>())
            {
                string value = failure ?? string.Empty;
                int separator = value.IndexOf('=');
                string definitionId = separator > 0
                    ? value.Substring(0, separator) : "UnknownDefinition";
                string state = definitionId
                    + "|" + GetDispatchSceneFailureStage(value)
                    + "|" + DiagnosticFieldValue(value, "Location=")
                    + "|" + DiagnosticFieldValue(value, "Point=");
                states.Add(state);
            }
            return "FailureStates=" + states.Count
                + ";FirstStates=" + string.Join(";", states
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Take(12));
        }

        internal static string BuildPendingSceneResolutionStateSignature(
            IEnumerable<string> pendingFailures)
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string failure in pendingFailures ?? Enumerable.Empty<string>())
            {
                string value = failure ?? string.Empty;
                int separator = value.IndexOf('=');
                string eventId = separator > 0 ? value.Substring(0, separator) : string.Empty;
                string stage = DiagnosticFieldValue(value, "Stage=");
                string location = DiagnosticFieldValue(value, "Location=");
                string catalogInterior = DiagnosticFieldValue(value, "CatalogInteriorId=");
                string liveInterior = DiagnosticFieldValue(value, "LiveInteriorId=");
                string native = DiagnosticFieldValue(value, "Native=");
                string physicalKey = string.Join("|", stage, location,
                    catalogInterior, liveInterior, native);
                if (string.IsNullOrWhiteSpace(location)
                    || string.Equals(location, "Unknown", StringComparison.OrdinalIgnoreCase))
                    physicalKey += "|" + eventId;
                groups.Add(physicalKey);
            }
            return "PendingGroups=" + groups.Count + ";States="
                + string.Join(";", groups.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Take(8));
        }

        internal static string SummarizeDispatchSceneFailures(
            IEnumerable<string> failures,
            int maximumExamples)
        {
            List<string> values = (failures ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            var examples = new List<string>();
            foreach (string failure in values.Take(Math.Max(0, maximumExamples)))
            {
                string example = failure.Length <= 320
                    ? failure : failure.Substring(0, 320) + "…";
                examples.Add(example);
            }
            return "FailureCount=" + values.Count
                + "; ExampleLimit=" + Math.Max(0, maximumExamples)
                + "; Examples=" + string.Join(" | ", examples);
        }

        private static string BuildInteriorEligibilityStateSignature(
            IList<string> failures)
        {
            if (failures == null || failures.Count == 0)
                return "RejectedDefinitions=0";

            var stableFailures = new List<string>();
            foreach (string failure in failures.Take(8))
            {
                string value = failure ?? string.Empty;
                int separator = value.IndexOf('=');
                string definitionId = separator > 0
                    ? value.Substring(0, separator)
                    : "UnknownDefinition";
                string details = separator >= 0
                    ? value.Substring(separator + 1)
                    : value;
                stableFailures.Add(definitionId
                    + ":Stage=" + DiagnosticFieldValue(details, "Stage=")
                    + ":FirstRejectedGate="
                    + DiagnosticFieldValue(details, "FirstRejectedGate="));
            }

            return "RejectedDefinitions=" + failures.Count
                + "; StableFailureStates=" + string.Join(" | ", stableFailures);
        }

        private static string DiagnosticFieldValue(string details, string field)
        {
            int start = details.IndexOf(field, StringComparison.Ordinal);
            if (start < 0)
                return "Unknown";
            start += field.Length;
            int end = details.IndexOf(';', start);
            return end < 0
                ? details.Substring(start)
                : details.Substring(start, end - start);
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

        private void LogRuntimeTransition(
            string category,
            string state,
            string message)
        {
            if (_log != null)
                _log.RuntimeTransition(category, state, message);
        }

        private void LogStateFailure(string category, string message)
        {
            if (_log != null) _log.StateFailure(category, message);
        }

        private void LogDebug(string category, string message)
        {
            if (_log != null) _log.Debug(category, message);
        }

        private void LogDebugTransition(
            string category,
            string state,
            string message)
        {
            if (_log != null)
                _log.DebugTransition(category, state, message);
        }

        private void ClearDebugTransition(string category)
        {
            if (_log != null)
                _log.ClearDebugTransition(category);
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




