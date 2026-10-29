using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

namespace LSImmersiveLife
{
    internal enum AmbientDispatchAnimationStartResult
    {
        Pending,
        Started,
        Failed
    }

    internal enum AmbientInteriorAccessStatus
    {
        None,
        Started,
        DoorOpening,
        ReadyToCross,
        Completed,
        Failed
    }

    /// <summary>
    /// Ambient-world owner for dispatch location assessment, runtime placement
    /// validation, paired Dispatch scene setup, and behavior planning for explicitly
    /// registered actors.
    ///
    /// PoliceCore creates this owner and Dispatch consumes it. It reads the existing
    /// shared location catalog and never creates a parallel database.
    ///
    /// The class is deliberately centered around registered actors rather than a
    /// city-wide ped scan. Other owners register eligible entities and retain
    /// their existing gameplay owner remain authoritative.
    /// </summary>
    internal sealed class LSWorldAmbientBehavior
    {
        private const float InteriorAccessUseRadius = 7f;
        private const float InteriorAccessDestinationRadius = 3.25f;
        private const float ConfirmedInteriorIdentityMaximumRange = 35f;
        private const float ConfirmedInteriorIdentityMaximumFloorOffset = 3f;
        private const float InteriorAccessDoorOpenRatio = 0.95f;
        private const float InteriorAccessDoorMotionPositionThreshold = 0.12f;
        private const float InteriorAccessDoorMotionAngleThreshold = 6f;
        private const float InteriorAccessDoorPropSearchRadius = 2.75f;
        private const float InteriorAccessDoorMaxSegmentDistance = 1.75f;
        private const float MappedEndpointDoorPropSearchRadius = 4f;
        private const float MappedEndpointDoorMaxDistance = 2.5f;
        private const int InteriorAccessTimeoutMilliseconds = 30000;
        private const int InteriorAccessDoorOpenStepMilliseconds = 80;
        private const float InteriorAccessDoorOpenStep = 0.10f;
        private const int InteriorAccessDoorUnlockRetryMilliseconds = 250;
        private const int InteriorAccessDoorUnlockMaximumAttempts = 8;
        private const float InteriorActivityMinimumDepth = 1.75f;
        private const float InteriorActivityMaximumDepth = 7f;
        private const float InteriorFloorAlignmentTolerance = 1.5f;
        private const int UnlockedDoorSystemState = 0;
        private const int UnlockedThisFrameDoorSystemState = 3;
        private const float SurveyPointAreaRadius = 650f;
        private const float MaximumSurveyPedSnapDistance = 4f;
        private const float MaximumAreaAnchorSnapDistance = 90f;
        private const float DispatchStreetHeightReferenceRadius = 45f;
        private const float DispatchExteriorStreetHeightTolerance = 4.5f;
        private const float DispatchRuralStreetHeightTolerance = 8f;
        private const int MaximumSurveyCandidatesToValidate = 20;
        private const int MaximumDispatchLocationCandidateFailureExamples = 3;
        private const int MaximumDispatchLocationCandidateFailureStages = 8;
        private const int MaximumDispatchLocationFailureTokenLength = 80;
        private const int MaximumMappedInteriorDispatchCandidates = 3;
        private const int MaximumInsideOnlyInteriorSceneCandidates = 2;
        private const int MaximumDispatchInteriorPins = 6;
        private const int DispatchInteriorIdentityCacheLifetimeMilliseconds = 3000;
        private const float MaximumInteriorPlayerFallbackDistance = 18f;
        private const ulong PinInteriorInMemoryNativeHash = 0x2CA429C029CCF247;
        private const ulong UnpinInteriorNativeHash = 0x261CCE7EED010641;
        private const ulong GetNthClosestVehicleNodeWithHeadingNativeHash = 0x80CA6A8B6C094CC4;
        private const ulong AddDoorToSystemNativeHash = 0x6F8838D03D1DC226;
        private const int RecentLocationHistoryLimit = 8;
        private const int MaximumMovingRoadCandidateAnchorsToValidate = 3;
        private const int MaximumPlayerRadiusRoadNodeCandidates = 6;
        private const float MovingRoadReferenceSnapDistance = 45f;
        private const float MovingRoadMinimumPlayerDistance = 55f;
        private const float MovingRoadMaximumPlayerDistance = 100f;
        private const float MovingRoadMinimumRouteDistance = 20f;
        private const float MovingRoadMaximumRouteDistance = 60f;
        private const float MovingRoadRouteProbeDistance = 35f;
        private const float MovingRoadVehicleOccupancyRadius = 3.5f;
        private const float MovingRoadPedOccupancyRadius = 2.5f;
        private const float MovingRoadElevationTolerance = 8f;
        private const float DispatchVehicleGroundFallbackRoadNodeSnapDistance = 2f;
        private const float DispatchVehicleGroundFallbackElevationTolerance = 1.5f;
        private const float DispatchVehicleGroundFallbackHeadingTolerance = 35f;
        private static readonly float[] PlayerRadiusRoadsideLateralOffsets =
        {
            4f,
            7f
        };
        private static readonly Vector2[] PlayerRadiusOutdoorFootProbeDirections =
        {
            new Vector2(1f, 0f),
            new Vector2(-1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(0f, -1f),
            new Vector2(0.70710678f, 0.70710678f),
            new Vector2(0.70710678f, -0.70710678f),
            new Vector2(-0.70710678f, 0.70710678f),
            new Vector2(-0.70710678f, -0.70710678f)
        };
        private const float DispatchCivilianSearchRadius = 10f;
        private const float DispatchCivilianMinimumSuspectDistance = 2.25f;
        private const int MaximumDispatchCivilianCandidatesToInspect = 24;
        private const float DispatchParticipantFallbackOffset = 1.25f;
        private const float DispatchParticipantDiagonalFallbackOffset = 0.88388348f;
        private const float DispatchParticipantMaximumTotalOffset = 4f;
        private const float DispatchParticipantMinimumSpacing = 1.5f;
        private const float DispatchExteriorFootprintProbeRadius = 3.5f;
        private const int DispatchExteriorMinimumOpenFootprintDirections = 3;
        private const float DispatchExteriorMinimumApproachDistance = 1.25f;
        private const float DispatchExteriorMaximumApproachDistance = 28f;
        private const float DispatchExteriorApproachAnchorSnapDistance = 1.5f;
        private const float DispatchExteriorNarrowApproachAnchorSnapDistance = 3f;
        private const float DispatchExteriorFootCatalogNavigationSnapDistance = 3f;
        private const float DispatchExteriorSameSiteFallbackOffset = 1.25f;
        private const float DispatchExteriorFootCatalogSameSiteFallbackOffset = 4f;
        private const float DispatchExteriorFootCatalogSceneOffsetLimit = 6f;
        private const float DispatchExteriorActorMaximumHorizontalGroundOffset = 0.5f;
        private const float DispatchExteriorActorMaximumOriginHeightAboveGround = 3f;
        private const float DispatchExteriorActorMaximumOriginBelowGround = 0.35f;
        private const int DispatchInteriorReadinessCacheMilliseconds = 2000;
        private const int DispatchInteriorCollisionRequestIntervalMilliseconds = 3000;
        private static readonly Vector2[] DispatchExteriorSameSiteFallbackDirections =
        {
            new Vector2(DispatchExteriorSameSiteFallbackOffset, 0f),
            new Vector2(-DispatchExteriorSameSiteFallbackOffset, 0f),
            new Vector2(0f, DispatchExteriorSameSiteFallbackOffset),
            new Vector2(0f, -DispatchExteriorSameSiteFallbackOffset)
        };
        private const float DispatchVictimFallbackCorrectionDistance = 2.75f;
        private const float DispatchVictimMaximumSceneDistance = 5.25f;
        private static readonly Vector2[] DispatchParticipantFallbackDirections =
        {
            new Vector2(DispatchParticipantFallbackOffset, 0f),
            new Vector2(-DispatchParticipantFallbackOffset, 0f),
            new Vector2(0f, DispatchParticipantFallbackOffset),
            new Vector2(0f, -DispatchParticipantFallbackOffset),
            new Vector2(DispatchParticipantDiagonalFallbackOffset, DispatchParticipantDiagonalFallbackOffset),
            new Vector2(DispatchParticipantDiagonalFallbackOffset, -DispatchParticipantDiagonalFallbackOffset),
            new Vector2(-DispatchParticipantDiagonalFallbackOffset, DispatchParticipantDiagonalFallbackOffset),
            new Vector2(-DispatchParticipantDiagonalFallbackOffset, -DispatchParticipantDiagonalFallbackOffset)
        };
        private static readonly Vector2[] DispatchVehicleStagingProbeOffsets =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 4f),
            new Vector2(0f, -4f)
        };
        private static readonly float[] DispatchMovingRoadSceneOffsets =
        {
            60f,
            -60f,
            80f,
            -80f
        };
        private static readonly Vector2[] DispatchVictimFallbackDirections =
        {
            new Vector2(2.5f, 0f),
            new Vector2(-2.5f, 0f),
            new Vector2(0f, 2.5f),
            new Vector2(0f, -2.5f),
            new Vector2(1.75f, 1.75f),
            new Vector2(1.75f, -1.75f),
            new Vector2(-1.75f, 1.75f),
            new Vector2(-1.75f, -1.75f)
        };

        private readonly Dictionary<int, AmbientActorContext> _actors =
            new Dictionary<int, AmbientActorContext>();

        private readonly Dictionary<int, DispatchCivilianClaim> _dispatchCivilianClaims =
            new Dictionary<int, DispatchCivilianClaim>();

        private readonly Dictionary<int, long> _nextEvaluationAt =
            new Dictionary<int, long>();

        private readonly int _maintenanceIntervalMilliseconds;
        private readonly int _minimumEvaluationIntervalMilliseconds;
        private readonly Random _locationRandom = new Random();
        private readonly Queue<string> _recentLocationIds = new Queue<string>();
        private readonly Dictionary<string, DispatchFootSearchProgress> _dispatchFootSearches =
            new Dictionary<string, DispatchFootSearchProgress>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _recentLocationIdSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, AmbientInteriorAccessSession> _interiorAccessSessions =
            new Dictionary<int, AmbientInteriorAccessSession>();
        private readonly Dictionary<int, AmbientConfirmedInteriorCrossing>
            _confirmedInteriorCrossingsByActor =
                new Dictionary<int, AmbientConfirmedInteriorCrossing>();
        private readonly HashSet<int> _dispatchPinnedInteriorIds = new HashSet<int>();
        private readonly Dictionary<int, Tuple<bool, DateTime>> _dispatchInteriorReadinessCache =
            new Dictionary<int, Tuple<bool, DateTime>>();
        private readonly Dictionary<string, DateTime> _lastDispatchInteriorCollisionRequestAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private LSImmersiveLocationCatalog _locationCatalog;
        private LSNPCDatabase _npcDatabase;
        private LSImmersiveLocationCatalog _dispatchInteriorPointsCacheCatalog;
        private List<DispatchInteriorPointReference> _dispatchInteriorPointReferences;
        private LSImmersiveLocationCatalog _mappedInteriorIdentityCacheCatalog;
        private int _mappedInteriorIdentityCachePlayerLiveId;
        private DateTime _mappedInteriorIdentityCacheCapturedAtUtc;
        private Vector3 _mappedInteriorIdentityCachePlayerPosition;
        private Dictionary<LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair, int>
            _mappedInteriorLiveIdsByPair;
        private Dictionary<LSImmersiveLocationCatalog.LSImmersiveLocationPoint, int>
            _mappedInteriorLiveIdsByPoint;
        private LSImmersiveLog _log;
        private LSPoliceDispatchSettings _dispatchDistanceSettings;

        internal LSWorldAmbientBehavior(
            int maintenanceIntervalMilliseconds = 750,
            int minimumEvaluationIntervalMilliseconds = 1000)
        {
            _maintenanceIntervalMilliseconds = Math.Max(100, maintenanceIntervalMilliseconds);
            _minimumEvaluationIntervalMilliseconds = Math.Max(250, minimumEvaluationIntervalMilliseconds);
            _dispatchDistanceSettings = LSPoliceDispatchSettings.Default();
        }

        internal LSWorldAmbientBehavior(
            LSImmersiveLocationCatalog locationCatalog,
            LSImmersiveLog log,
            int maintenanceIntervalMilliseconds = 750,
            int minimumEvaluationIntervalMilliseconds = 1000,
            LSPoliceDispatchSettings dispatchSettings = null)
            : this(maintenanceIntervalMilliseconds, minimumEvaluationIntervalMilliseconds)
        {
            _locationCatalog = locationCatalog;
            _log = log;
            if (dispatchSettings != null)
                _dispatchDistanceSettings = dispatchSettings;
        }

        private void GetPlayerRadiusFallbackDistanceRange(
            out float minimumDistance,
            out float maximumDistance)
        {
            LSPoliceDispatchSettings settings = _dispatchDistanceSettings
                ?? LSPoliceDispatchSettings.Default();
            minimumDistance = Math.Max(20, Math.Min(250,
                settings.PlayerRadiusFallbackMinimumDistanceMeters));
            maximumDistance = Math.Max((int)minimumDistance, Math.Min(250,
                settings.PlayerRadiusFallbackMaximumDistanceMeters));
        }

        private static float[] BuildPlayerRadiusFallbackRoadOffsets(
            float minimumDistance,
            float maximumDistance)
        {
            float span = Math.Max(0f, maximumDistance - minimumDistance);
            float inset = Math.Min(10f, span / 4f);
            float nearerTarget = minimumDistance + inset;
            float fartherTarget = maximumDistance - inset;
            if (Math.Abs(fartherTarget - nearerTarget) < 0.1f)
                return new[] { nearerTarget, -nearerTarget };
            return new[] { nearerTarget, -nearerTarget, fartherTarget, -fartherTarget };
        }

        private static float[] BuildPlayerRadiusOutdoorFootProbeDistances(
            float minimumDistance,
            float maximumDistance)
        {
            float span = Math.Max(0f, maximumDistance - minimumDistance);
            if (span < 0.1f)
                return new[] { minimumDistance };

            float inset = Math.Min(10f, span / 4f);
            return new[]
            {
                minimumDistance + inset,
                minimumDistance + (span / 2f),
                maximumDistance - inset
            };
        }

        internal void AttachNpcDatabase(LSNPCDatabase npcDatabase)
        {
            _npcDatabase = npcDatabase;
        }

        internal bool TryChooseDispatchVictimModel(string poolId, out string modelName)
        {
            modelName = string.Empty;
            return _npcDatabase != null
                && _npcDatabase.TryChooseDispatchPedModel(poolId, out modelName);
        }

        internal bool IsDispatchVictimModelInPool(string poolId, int modelHash)
        {
            return _npcDatabase != null
                && _npcDatabase.IsDispatchPedModelInPool(poolId, modelHash);
        }

        internal void UpdateLocationCatalog(LSImmersiveLocationCatalog locationCatalog)
        {
            _locationCatalog = locationCatalog;
            _dispatchFootSearches.Clear();
            _dispatchInteriorPointsCacheCatalog = null;
            _dispatchInteriorPointReferences = null;
            _mappedInteriorIdentityCacheCatalog = null;
            _mappedInteriorIdentityCachePlayerLiveId = 0;
            _mappedInteriorIdentityCacheCapturedAtUtc = DateTime.MinValue;
            _mappedInteriorIdentityCachePlayerPosition = Vector3.Zero;
            _mappedInteriorLiveIdsByPair = null;
            _mappedInteriorLiveIdsByPoint = null;
        }

        internal void ClearDispatchFootSearchProgress()
        {
            _dispatchFootSearches.Clear();
        }

        /// <summary>
        /// Shows and handles the Player's physical threshold action. A mapped
        /// coordinate pair only identifies a candidate threshold. A live,
        /// Door-System-registered door must be found at that threshold, and the
        /// actor must physically reach the other interior context before this
        /// operation completes.
        /// </summary>
        internal AmbientInteriorAccessStatus ProcessPlayerInteriorAccess(
            Ped player,
            bool allowInput,
            bool enterPressed,
            out string message,
            out bool inputConsumed)
        {
            message = string.Empty;
            inputConsumed = false;
            if (player == null || !player.Exists())
            {
                ClearInteriorAccessPromptState();
                CancelAllInteriorAccessSessions("PlayerUnavailable");
                return AmbientInteriorAccessStatus.None;
            }

            AmbientInteriorAccessSession activeSession;
            if (_interiorAccessSessions.TryGetValue(player.Handle, out activeSession))
            {
                ClearInteriorAccessPromptState();
                return MaintainInteriorAccess(player, out message);
            }

            if (!allowInput || player.IsDead || player.IsInVehicle())
            {
                ClearInteriorAccessPromptState();
                return AmbientInteriorAccessStatus.None;
            }

            bool nearMappedPair = IsNearMappedInteriorPair(player.Position);
            if (!nearMappedPair)
            {
                ClearInteriorAccessPromptState();
                return AmbientInteriorAccessStatus.None;
            }

            AmbientInteriorAccessRoute route;
            string routeFailure;
            if (!TryResolveInteriorAccessRoute(player, out route, out routeFailure))
            {
                if (enterPressed)
                {
                    int rejectedEntityInteriorId;
                    TryGetActorInteriorId(player, out rejectedEntityInteriorId);
                    int rejectedCoordinateInteriorId = GetInteriorAt(player.Position);
                    Log("AMBIENT_WORLD_INTERIOR_ACCESS_REJECTED",
                        "Actor=" + player.Handle
                        + "; EntityInteriorId=" + rejectedEntityInteriorId
                        + "; CoordinateInteriorId=" + rejectedCoordinateInteriorId
                        + "; Position=" + player.Position
                        + "; Reason=" + routeFailure);
                }
                ClearInteriorAccessPromptState();
                return AmbientInteriorAccessStatus.None;
            }

            if (!enterPressed)
            {
                Screen.ShowHelpTextThisFrame(
                    "Press Enter to unlock and open the mapped door.",
                    false);
                if (_log != null)
                    _log.RuntimeTransition(
                        "AMBIENT_WORLD_INTERIOR_ACCESS_PROMPT_AVAILABLE",
                        player.Handle + "|" + route.Pair.InsideLocation.Id,
                        "Actor=" + player.Handle
                        + "; Location=" + route.Pair.InsideLocation.Id
                        + "; InputOwner=AmbientWorld; Key=Enter"
                        + "; DispatchSecureKey=E; InvestigateKey=I");
                return AmbientInteriorAccessStatus.None;
            }

            ClearInteriorAccessPromptState();
            inputConsumed = true;

            int requestedInteriorId;
            TryGetActorInteriorId(player, out requestedInteriorId);
            Log("AMBIENT_WORLD_INTERIOR_ACCESS_INPUT_RECEIVED",
                "Actor=" + player.Handle
                + "; EntityInteriorId=" + requestedInteriorId
                + "; CoordinateInteriorId=" + GetInteriorAt(player.Position)
                + "; Position=" + player.Position
                + "; Entering=" + route.EnteringInterior
                + "; InputOwner=AmbientWorld; InputKey=Enter; Consumed=true"
                + "; MappingBasis=" + route.Pair.MappingBasis
                + "; CatalogInteriorId=" + route.CatalogInteriorId
                + "; TargetLiveInteriorId=" + route.TargetLiveInteriorId
                + "; OutsideLiveInteriorId=" + GetInteriorAt(
                    ResolveOutsideInteriorProbe(route.Pair))
                + "; InsideLiveInteriorId=" + GetInteriorAt(
                    route.Pair.InsidePoint.Position));

            if (!TryBeginInteriorAccess(player, route, out routeFailure))
            {
                message = routeFailure;
                Log("AMBIENT_WORLD_INTERIOR_ACCESS_REJECTED",
                    "Actor=" + player.Handle
                    + "; Location=" + route.Pair.InsideLocation.Id
                    + "; OutsideCapture=" + route.Pair.OutsidePoint.SourceRecord
                    + "; InsideCapture=" + route.Pair.InsidePoint.SourceRecord
                    + "; Reason=" + message);
                Log("AMBIENT_WORLD_INTERIOR_ACCESS_INPUT_CONSUMED",
                    "Actor=" + player.Handle
                    + "; InputOwner=AmbientWorld; InputKey=Enter; Consumed=true"
                    + "; Result=Rejected; Reason=" + message);
                return AmbientInteriorAccessStatus.Failed;
            }

            Log("AMBIENT_WORLD_INTERIOR_ACCESS_INPUT_CONSUMED",
                "Actor=" + player.Handle
                + "; InputOwner=AmbientWorld; InputKey=Enter; Consumed=true"
                + "; Result=DoorSessionStarted; Location=" + route.Pair.InsideLocation.Id);
            message = route.EnteringInterior
                ? "The mapped door is unlocking and opening. Walk through it to enter."
                : "The mapped door is unlocking and opening. Walk through it to exit.";
            return AmbientInteriorAccessStatus.Started;
        }

        /// <summary>
        /// Resolves the documented near-duplicate outside/inside capture pair
        /// for an actor without issuing movement tasks. Existing gameplay owners
        /// use the returned destination for their own navigation tasks.
        /// </summary>
        internal bool TryResolveInteriorAccessRoute(
            Ped actor,
            out AmbientInteriorAccessRoute route,
            out string reason)
        {
            route = null;
            reason = string.Empty;
            if (actor == null || !actor.Exists() || actor.IsDead || actor.IsInVehicle())
            {
                reason = "Interior access requires a living actor on foot.";
                return false;
            }
            if (_locationCatalog == null)
            {
                reason = "The existing LSImmersiveLocation catalog is unavailable.";
                return false;
            }

            int actorCoordinateInteriorId = GetInteriorAt(actor.Position);
            AmbientConfirmedInteriorCrossing confirmedCrossing;
            bool hasConfirmedInteriorSide = _confirmedInteriorCrossingsByActor.TryGetValue(
                actor.Handle, out confirmedCrossing);

            AmbientInteriorAccessRoute nearest = null;
            float nearestDistance = float.MaxValue;
            string walkablePositionFailure = string.Empty;
            string nearestSideFailure = string.Empty;
            float nearestSideFailureDistance = float.MaxValue;
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in GetInteriorAccessPairs())
            {
                if (pair == null || pair.OutsidePoint == null || pair.InsidePoint == null
                    || !HasCatalogedMappedInteriorReference(pair))
                    continue;

                float outsideDistance = actor.Position.DistanceTo(
                    pair.OutsidePoint.Position);
                float insideDistance = actor.Position.DistanceTo(
                    pair.InsidePoint.Position);
                if (outsideDistance > InteriorAccessUseRadius
                    && insideDistance > InteriorAccessUseRadius)
                    continue;
                float pairDistanceToActor = Math.Min(
                    outsideDistance, insideDistance);

                Vector3 outsideProbe = ResolveOutsideInteriorProbe(pair);
                int liveOutsideInteriorId = GetInteriorAt(outsideProbe);
                int liveInsideInteriorId = GetInteriorAt(pair.InsidePoint.Position);
                bool confirmedInsideAtThisPair = hasConfirmedInteriorSide
                    && IsActorOnConfirmedInteriorSide(actor, pair, confirmedCrossing);
                if (confirmedInsideAtThisPair)
                {
                    // GTA can transiently report zero for a player who has
                    // already physically crossed this exact mapped threshold.
                    // Keep the confirmed live identity while the actor remains
                    // beside the same supported interior floor.
                    if (liveInsideInteriorId == 0)
                        liveInsideInteriorId = confirmedCrossing.InteriorId;
                    if (liveOutsideInteriorId != 0
                        && liveOutsideInteriorId != confirmedCrossing.InteriorId)
                        continue;
                }
                if (!IsMappedInteriorThresholdRuntimeValid(
                    liveInsideInteriorId,
                    liveOutsideInteriorId,
                    liveInsideInteriorId))
                {
                    if (pairDistanceToActor < nearestSideFailureDistance)
                    {
                        nearestSideFailureDistance = pairDistanceToActor;
                        nearestSideFailure = "Location=" + pair.InsideLocation.Id
                            + "; CatalogInteriorId=" + pair.InteriorId
                            + "; TargetLiveInteriorId=" + liveInsideInteriorId
                            + "; OutsideLiveInteriorId=" + liveOutsideInteriorId
                            + "; InsideLiveInteriorId=" + liveInsideInteriorId
                            + "; OutsideDistance=" + outsideDistance.ToString("0.00", CultureInfo.InvariantCulture)
                            + "; InsideDistance=" + insideDistance.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    continue;
                }

                // Some storefronts report the same non-zero live interior ID on
                // both sides. In that case the mapped threshold endpoints, not
                // the interior ID, determine which side the actor occupies.
                bool sameNonZeroInteriorOnBothSides = liveOutsideInteriorId != 0
                    && liveOutsideInteriorId == liveInsideInteriorId;
                bool enteringInterior;
                bool exitingInterior;
                if (confirmedInsideAtThisPair)
                {
                    // A completed physical crossing owns the side identity while
                    // the player remains on the mapped interior floor. Temporary
                    // zero or same-ID native results cannot start a second entry.
                    enteringInterior = false;
                    exitingInterior = insideDistance <= InteriorAccessUseRadius;
                }
                else if (sameNonZeroInteriorOnBothSides)
                {
                    enteringInterior = outsideDistance <= InteriorAccessUseRadius
                        && outsideDistance + 0.35f < insideDistance;
                    exitingInterior = insideDistance <= InteriorAccessUseRadius
                        && insideDistance + 0.35f < outsideDistance;
                }
                else
                {
                    enteringInterior = outsideDistance <= InteriorAccessUseRadius
                        && (outsideDistance + 0.35f < insideDistance
                            || actorCoordinateInteriorId == liveOutsideInteriorId);
                    exitingInterior = insideDistance <= InteriorAccessUseRadius
                        && (insideDistance + 0.35f < outsideDistance
                            || actorCoordinateInteriorId == liveInsideInteriorId);
                }
                if (!enteringInterior && !exitingInterior)
                {
                    if (pairDistanceToActor < nearestSideFailureDistance)
                    {
                        nearestSideFailureDistance = pairDistanceToActor;
                        nearestSideFailure = "Location=" + pair.InsideLocation.Id
                            + "; CatalogInteriorId=" + pair.InteriorId
                            + "; TargetLiveInteriorId=" + liveInsideInteriorId
                            + "; OutsideLiveInteriorId=" + liveOutsideInteriorId
                            + "; InsideLiveInteriorId=" + liveInsideInteriorId
                            + "; OutsideDistance=" + outsideDistance.ToString("0.00", CultureInfo.InvariantCulture)
                            + "; InsideDistance=" + insideDistance.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    continue;
                }
                if (enteringInterior && exitingInterior)
                    enteringInterior = outsideDistance <= insideDistance;

                Vector3 entryPosition = enteringInterior
                    ? pair.OutsidePoint.Position
                    : pair.InsidePoint.Position;
                Vector3 destinationPosition = enteringInterior
                    ? Vector3.Zero
                    : outsideProbe;
                int currentLiveInteriorId = enteringInterior
                    ? liveOutsideInteriorId
                    : liveInsideInteriorId;
                int targetLiveInteriorId = enteringInterior
                    ? liveInsideInteriorId
                    : liveOutsideInteriorId;
                float distance = actor.Position.DistanceTo(entryPosition);

                if (enteringInterior
                    && !TryResolveInteriorWalkablePosition(
                        pair.InsidePoint.Position,
                        liveInsideInteriorId,
                        entryPosition,
                        out destinationPosition,
                        out walkablePositionFailure))
                    continue;

                AmbientInteriorAccessRoute candidateRoute = new AmbientInteriorAccessRoute(
                    pair,
                    entryPosition,
                    destinationPosition,
                    currentLiveInteriorId,
                    targetLiveInteriorId,
                    enteringInterior);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = candidateRoute;
                }
            }

            if (nearest == null)
            {
                reason = !string.IsNullOrWhiteSpace(walkablePositionFailure)
                    ? walkablePositionFailure
                    : !string.IsNullOrWhiteSpace(nearestSideFailure)
                        ? "No live mapped doorway side is close enough to this actor. "
                            + nearestSideFailure
                        : "No live mapped doorway side is close enough to this actor.";
                return false;
            }

            route = nearest;
            return true;
        }

        /// <summary>
        /// Resolves a route to or from one already identified interior without
        /// moving the actor. Owners may navigate to EntryPosition, open the real
        /// mapped door when close, and then continue to DestinationPosition.
        /// </summary>
        internal bool TryResolveInteriorAccessRouteForInterior(
            Ped actor,
            int targetLiveInteriorId,
            string requiredLocationId,
            string requiredCatalogInteriorId,
            bool enteringInterior,
            out AmbientInteriorAccessRoute route,
            out string reason)
        {
            route = null;
            reason = string.Empty;
            if (actor == null || !actor.Exists() || actor.IsDead || actor.IsInVehicle())
            {
                reason = "Interior access requires a living actor on foot.";
                return false;
            }
            if (targetLiveInteriorId == 0 || _locationCatalog == null)
            {
                reason = targetLiveInteriorId == 0
                    ? "The requested interior ID is invalid."
                    : "The existing LSImmersiveLocation catalog is unavailable.";
                return false;
            }

            int actorEntityInteriorId = 0;
            int actorCoordinateInteriorId = 0;
            bool actorEntityInteriorIdAvailable = false;
            bool actorCoordinateInteriorIdAvailable = false;
            try
            {
                actorEntityInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_FROM_ENTITY, actor);
                actorEntityInteriorIdAvailable = true;
            }
            catch { }
            try
            {
                actorCoordinateInteriorId = GetInteriorAt(actor.Position);
                actorCoordinateInteriorIdAvailable = true;
            }
            catch { }
            int actorLiveInteriorId = actorCoordinateInteriorId != 0
                ? actorCoordinateInteriorId : actorEntityInteriorId;
            if (!enteringInterior
                && ((!actorEntityInteriorIdAvailable && !actorCoordinateInteriorIdAvailable)
                    || actorLiveInteriorId != targetLiveInteriorId))
            {
                reason = "Stage=InteriorActorIdentity; ExpectedLiveInteriorId="
                    + targetLiveInteriorId
                    + "; EntityLiveInteriorId="
                    + (actorEntityInteriorIdAvailable
                        ? actorEntityInteriorId.ToString(CultureInfo.InvariantCulture)
                        : "Unavailable")
                    + "; CoordinateLiveInteriorId="
                    + (actorCoordinateInteriorIdAvailable
                        ? actorCoordinateInteriorId.ToString(CultureInfo.InvariantCulture)
                        : "Unavailable")
                    + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                    + "; Result=ActorNotInsideRequestedLiveInterior";
                return false;
            }

            AmbientInteriorAccessRoute nearest = null;
            float nearestDistance = float.MaxValue;
            string walkableFailure = string.Empty;
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in GetInteriorAccessPairs())
            {
                if (pair == null || pair.InsideLocation == null
                    || pair.OutsidePoint == null || pair.InsidePoint == null
                    || !HasCatalogedMappedInteriorReference(pair)
                    || (!string.IsNullOrWhiteSpace(requiredLocationId)
                        && !string.Equals(pair.InsideLocation.Id, requiredLocationId,
                            StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(requiredCatalogInteriorId)
                        && !string.Equals(pair.InteriorId, requiredCatalogInteriorId,
                            StringComparison.OrdinalIgnoreCase)))
                    continue;

                bool interiorReady;
                try
                {
                    interiorReady = IsInteriorReady(targetLiveInteriorId);
                }
                catch (Exception ex)
                {
                    walkableFailure = "Stage=InteriorDoorwayReadiness; Location="
                        + pair.InsideLocation.Id
                        + "; CatalogInteriorId=" + pair.InteriorId
                        + "; LiveInteriorId=" + targetLiveInteriorId
                        + "; Native=IS_INTERIOR_READY; Result=Exception:" + ex.GetType().Name;
                    continue;
                }
                if (!interiorReady)
                {
                    try { RequestInteriorCollision(pair.InsidePoint.Position); }
                    catch (Exception ex)
                    {
                        walkableFailure = "Stage=InteriorDoorwayReadiness; Location="
                            + pair.InsideLocation.Id
                            + "; CatalogInteriorId=" + pair.InteriorId
                            + "; LiveInteriorId=" + targetLiveInteriorId
                            + "; Native=IS_INTERIOR_READY:false; CollisionRequest=Exception:"
                            + ex.GetType().Name;
                        continue;
                    }
                    walkableFailure = "PENDING:Stage=InteriorDoorwayReadiness; Location="
                        + pair.InsideLocation.Id
                        + "; CatalogInteriorId=" + pair.InteriorId
                        + "; LiveInteriorId=" + targetLiveInteriorId
                        + "; Native=IS_INTERIOR_READY:false; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    continue;
                }

                Vector3 outsideProbe = ResolveOutsideInteriorProbe(pair);
                int liveOutsideInteriorId = GetInteriorAt(outsideProbe);
                int liveInsideInteriorId = GetInteriorAt(pair.InsidePoint.Position);
                if (!IsMappedInteriorThresholdRuntimeValid(
                    targetLiveInteriorId,
                    liveOutsideInteriorId,
                    liveInsideInteriorId))
                {
                    walkableFailure = "Stage=MappedThresholdIdentity; Location="
                        + pair.InsideLocation.Id
                        + "; CatalogInteriorId=" + pair.InteriorId
                        + "; ExpectedLiveInteriorId=" + targetLiveInteriorId
                        + "; OutsideLiveInteriorId=" + liveOutsideInteriorId
                        + "; InsideLiveInteriorId=" + liveInsideInteriorId
                        + "; Native=GET_INTERIOR_AT_COORDS; Result=ThresholdIdentityMismatch";
                    continue;
                }

                Vector3 entryPosition = enteringInterior
                    ? pair.OutsidePoint.Position
                    : pair.InsidePoint.Position;
                Vector3 destinationPosition = enteringInterior
                    ? Vector3.Zero
                    : outsideProbe;
                if (enteringInterior
                    && !TryResolveInteriorWalkablePosition(
                        pair.InsidePoint.Position,
                        liveInsideInteriorId,
                        outsideProbe,
                        out destinationPosition,
                        out walkableFailure))
                    continue;

                float distance = actor.Position.DistanceTo(entryPosition);
                if (float.IsNaN(distance) || float.IsInfinity(distance))
                    continue;
                var candidate = new AmbientInteriorAccessRoute(
                    pair,
                    entryPosition,
                    destinationPosition,
                    enteringInterior ? liveOutsideInteriorId : liveInsideInteriorId,
                    enteringInterior ? liveInsideInteriorId : liveOutsideInteriorId,
                    enteringInterior);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = candidate;
                }
            }

            if (nearest == null)
            {
                reason = !string.IsNullOrWhiteSpace(walkableFailure)
                    ? walkableFailure
                    : "No live mapped door pair matches the requested interior.";
                return false;
            }
            route = nearest;
            return true;
        }

        private static bool IsMappedInteriorThresholdRuntimeValid(
            int targetLiveInteriorId,
            int liveOutsideInteriorId,
            int liveInsideInteriorId)
        {
            // Some GTA storefront shells report the same nonzero interior ID
            // on both surveyed sides. The catalog pair and physical door/path
            // checks disambiguate that threshold; a different nonzero outside
            // ID remains an incompatible adjacent interior.
            return targetLiveInteriorId != 0
                && liveInsideInteriorId == targetLiveInteriorId
                && (liveOutsideInteriorId == 0
                    || liveOutsideInteriorId == liveInsideInteriorId);
        }

        private static bool IsInteriorReady(int liveInteriorId)
        {
            return liveInteriorId != 0
                && Function.Call<bool>((Hash)0x6726BDCCC1932F0E, liveInteriorId);
        }

        private bool IsDispatchInteriorReadyCached(int liveInteriorId)
        {
            if (liveInteriorId == 0)
                return false;

            DateTime now = DateTime.UtcNow;
            Tuple<bool, DateTime> cached;
            if (_dispatchInteriorReadinessCache.TryGetValue(liveInteriorId, out cached)
                && cached != null && now < cached.Item2)
                return cached.Item1;

            bool ready = IsInteriorReady(liveInteriorId);
            foreach (int expiredInteriorId in _dispatchInteriorReadinessCache
                .Where(pair => pair.Value == null || now >= pair.Value.Item2)
                .Select(pair => pair.Key).ToArray())
                _dispatchInteriorReadinessCache.Remove(expiredInteriorId);
            _dispatchInteriorReadinessCache[liveInteriorId] = Tuple.Create(
                ready, now.AddMilliseconds(DispatchInteriorReadinessCacheMilliseconds));
            return ready;
        }

        private void RequestDispatchInteriorCollision(
            int liveInteriorId,
            Vector3 position)
        {
            int cellX = (int)Math.Floor(position.X / 40f);
            int cellY = (int)Math.Floor(position.Y / 40f);
            string key = liveInteriorId.ToString(CultureInfo.InvariantCulture)
                + ":" + cellX.ToString(CultureInfo.InvariantCulture)
                + ":" + cellY.ToString(CultureInfo.InvariantCulture);
            DateTime now = DateTime.UtcNow;
            DateTime lastRequestedAt;
            if (_lastDispatchInteriorCollisionRequestAt.TryGetValue(key, out lastRequestedAt)
                && now < lastRequestedAt.AddMilliseconds(
                    DispatchInteriorCollisionRequestIntervalMilliseconds))
                return;

            RequestInteriorCollision(position);
            _lastDispatchInteriorCollisionRequestAt[key] = now;
            foreach (string expired in _lastDispatchInteriorCollisionRequestAt
                .Where(pair => now >= pair.Value.AddSeconds(30))
                .Select(pair => pair.Key).ToArray())
                _lastDispatchInteriorCollisionRequestAt.Remove(expired);
        }

        private bool TryRequestDispatchInteriorStreaming(
            int liveInteriorId,
            Vector3 insidePosition,
            string locationId,
            string catalogInteriorId,
            out string reason)
        {
            reason = string.Empty;
            if (liveInteriorId == 0)
            {
                reason = "Stage=InteriorStreamingRequest; Location=" + (locationId ?? string.Empty)
                    + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                    + "; LiveInteriorId=0; Result=NoLiveInteriorIdentity";
                return false;
            }

            if (!_dispatchPinnedInteriorIds.Contains(liveInteriorId))
            {
                if (_dispatchPinnedInteriorIds.Count >= MaximumDispatchInteriorPins)
                {
                    reason = "Stage=InteriorStreamingRequest; Location=" + (locationId ?? string.Empty)
                        + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                        + "; LiveInteriorId=" + liveInteriorId
                        + "; Native=PIN_INTERIOR_IN_MEMORY; Result=BoundedPinCapacityReached"
                        + "; MaximumPinnedInteriors=" + MaximumDispatchInteriorPins;
                    return false;
                }

                try
                {
                    Function.Call((Hash)PinInteriorInMemoryNativeHash, liveInteriorId);
                    _dispatchPinnedInteriorIds.Add(liveInteriorId);
                    LogDispatchInteriorPinTransition(liveInteriorId, "Pinned",
                        "Location=" + (locationId ?? string.Empty)
                        + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                        + "; LiveInteriorId=" + liveInteriorId
                        + "; Native=PIN_INTERIOR_IN_MEMORY; Result=Requested");
                }
                catch (Exception ex)
                {
                    LogDispatchInteriorPinTransition(liveInteriorId,
                        "PinFailed:" + ex.GetType().Name,
                        "Location=" + (locationId ?? string.Empty)
                        + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                        + "; LiveInteriorId=" + liveInteriorId
                        + "; Native=PIN_INTERIOR_IN_MEMORY; Result=Exception:" + ex.GetType().Name);
                    reason = "Stage=InteriorStreamingRequest; Location=" + (locationId ?? string.Empty)
                        + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                        + "; LiveInteriorId=" + liveInteriorId
                        + "; Native=PIN_INTERIOR_IN_MEMORY; Result=Exception:" + ex.GetType().Name;
                    return false;
                }
            }

            try
            {
                RequestDispatchInteriorCollision(liveInteriorId, insidePosition);
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorStreamingRequest; Location=" + (locationId ?? string.Empty)
                    + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                    + "; LiveInteriorId=" + liveInteriorId
                    + "; Native=REQUEST_COLLISION_AT_COORD; Result=Exception:" + ex.GetType().Name;
                return false;
            }

            reason = "PENDING:Status=InteriorNotReady; Stage=InteriorReadiness; Location=" + (locationId ?? string.Empty)
                + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                + "; LiveInteriorId=" + liveInteriorId
                + "; Native=IS_INTERIOR_READY:false"
                + "; StreamingRequest=PIN_INTERIOR_IN_MEMORY:Requested"
                + "; CollisionRequest=REQUEST_COLLISION_AT_COORD";
            return true;
        }

        internal void ReleaseDispatchInteriorPinsExcept(
            IEnumerable<int> retainedLiveInteriorIds,
            string reason)
        {
            HashSet<int> retained = new HashSet<int>(retainedLiveInteriorIds
                ?? Enumerable.Empty<int>());
            int[] releaseCandidates = _dispatchPinnedInteriorIds
                .Where(value => !retained.Contains(value)).ToArray();
            if (releaseCandidates.Length == 0)
                return;

            int playerLiveInteriorId = 0;
            bool playerInteriorKnown = false;
            try
            {
                Ped player = Game.Player.Character;
                if (player != null && player.Exists())
                {
                    playerLiveInteriorId = GetInteriorAt(player.Position);
                    playerInteriorKnown = true;
                }
            }
            catch
            {
                // Keep pins when Player interior identity cannot be checked.
            }

            foreach (int liveInteriorId in releaseCandidates)
            {
                // UNPIN_INTERIOR can make an interior invisible. Keep its
                // stream request while the Player is physically inside it.
                if (!playerInteriorKnown || playerLiveInteriorId == liveInteriorId)
                    continue;

                try
                {
                    Function.Call((Hash)UnpinInteriorNativeHash, liveInteriorId);
                    _dispatchPinnedInteriorIds.Remove(liveInteriorId);
                    LogDispatchInteriorPinTransition(liveInteriorId, "Released",
                        "LiveInteriorId=" + liveInteriorId
                        + "; Native=UNPIN_INTERIOR; Reason=" + (reason ?? string.Empty)
                        + "; Result=Released");
                }
                catch (Exception ex)
                {
                    LogDispatchInteriorPinTransition(liveInteriorId,
                        "ReleaseFailed:" + ex.GetType().Name,
                        "LiveInteriorId=" + liveInteriorId
                        + "; Native=UNPIN_INTERIOR; Reason=" + (reason ?? string.Empty)
                        + "; Result=Exception:" + ex.GetType().Name);
                }
            }
        }

        private bool IsPlayerInsideLiveInterior(int liveInteriorId)
        {
            if (liveInteriorId == 0)
                return false;
            try
            {
                Ped player = Game.Player.Character;
                int playerLiveInteriorId;
                return player != null && player.Exists()
                    && TryGetDispatchPlayerInteriorId(
                        player, out playerLiveInteriorId, false)
                    && playerLiveInteriorId == liveInteriorId;
            }
            catch
            {
                return false;
            }
        }

        private static void RequestInteriorCollision(Vector3 position)
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                position.X, position.Y, position.Z);
        }

        private static bool IsMappedDoorwayFloorSupported(
            Vector3 position,
            out string evidence)
        {
            evidence = "Position=" + position;
            if (position == Vector3.Zero
                || float.IsNaN(position.X) || float.IsInfinity(position.X)
                || float.IsNaN(position.Y) || float.IsInfinity(position.Y)
                || float.IsNaN(position.Z) || float.IsInfinity(position.Z))
            {
                evidence += "; Result=InvalidPosition";
                return false;
            }

            float groundZ;
            Vector3 groundNormal;
            try
            {
                if (!World.GetGroundHeightAndNormal(
                    position, out groundZ, out groundNormal))
                {
                    evidence += "; Result=GroundPending";
                    return false;
                }
            }
            catch (Exception ex)
            {
                evidence += "; Result=GroundQueryFailed:" + ex.GetType().Name;
                return false;
            }

            float floorDelta = Math.Abs(groundZ - position.Z);
            bool supported = !float.IsNaN(groundZ) && !float.IsInfinity(groundZ)
                && !float.IsNaN(groundNormal.Z) && !float.IsInfinity(groundNormal.Z)
                && groundNormal.Z >= 0.55f
                && floorDelta <= InteriorFloorAlignmentTolerance;
            evidence += "; GroundZ=" + groundZ.ToString("0.00", CultureInfo.InvariantCulture)
                + "; NormalZ=" + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                + "; FloorDelta=" + floorDelta.ToString("0.00", CultureInfo.InvariantCulture)
                + "; Result=" + (supported ? "Supported" : "Unsupported");
            return supported;
        }

        private bool TryResolveInteriorWalkablePosition(
            Vector3 insideThreshold,
            int expectedInteriorId,
            Vector3 outsideEntry,
            out Vector3 safePosition,
            out string reason,
            Vector3? activePlayerPosition = null,
            bool requireMappedThresholdDepth = true)
        {
            safePosition = Vector3.Zero;
            reason = "No nearby walkable position was found inside the mapped interior.";
            if (expectedInteriorId == 0)
            {
                reason = "Stage=InteriorIdentity; Result=InvalidLiveInteriorId; LiveInteriorId=0";
                return false;
            }

            bool interiorReady;
            try
            {
                interiorReady = IsInteriorReady(expectedInteriorId);
            }
            catch (Exception ex)
            {
                reason = "PENDING:Status=InteriorNotReady; Stage=InteriorReadiness; LiveInteriorId=" + expectedInteriorId
                    + "; Native=IS_INTERIOR_READY; Result=Exception:" + ex.GetType().Name;
                return false;
            }
            bool playerInside = IsPlayerInsideLiveInterior(expectedInteriorId);
            if (!interiorReady)
            {
                try { RequestInteriorCollision(insideThreshold); }
                catch (Exception ex)
                {
                    reason = "PENDING:Status=CollisionPending; Stage=InteriorReadiness; LiveInteriorId=" + expectedInteriorId
                        + "; Native=IS_INTERIOR_READY:false; CollisionRequest=Exception:"
                        + ex.GetType().Name;
                    return false;
                }
                if (!playerInside)
                {
                    reason = "PENDING:Status=InteriorNotReady; Stage=InteriorReadiness; LiveInteriorId=" + expectedInteriorId
                        + "; Native=IS_INTERIOR_READY:false; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    return false;
                }
                // The Player is inside this exact live interior. Continue only
                // through the safe-coordinate, floor-height, identity, spacing,
                // and occupancy probes below; do not accept coordinates alone.
            }

            try
            {
                Vector3 best = Vector3.Zero;
                bool foundBest = false;
                float bestDistance = float.MaxValue;
                int safePositionProbeCount = 0;
                int safePositionFoundCount = 0;
                int floorProbeCount = 0;
                int floorResultCount = 0;
                int safeConstraintRejectedCount = 0;
                int floorConstraintRejectedCount = 0;
                int occupiedPositionRejectedCount = 0;
                int interiorIdentityRejectedCount = 0;
                bool playerFallbackUsed = false;
                int passCount = activePlayerPosition.HasValue && playerInside ? 2 : 1;
                for (int pass = 0; pass < passCount; pass++)
                {
                    if (pass > 0 && foundBest)
                        break;

                    bool usePlayerFallback = pass == 1;
                    Vector3 probeCenter = usePlayerFallback
                        ? activePlayerPosition.Value : insideThreshold;
                    if (usePlayerFallback)
                        playerFallbackUsed = true;

                    for (float radius = InteriorActivityMinimumDepth;
                        radius <= InteriorActivityMaximumDepth;
                        radius += 1.25f)
                    {
                        for (int direction = 0; direction < 8; direction++)
                        {
                            double angle = direction * (Math.PI / 4.0);
                            Vector3 probe = new Vector3(
                                probeCenter.X + ((float)Math.Cos(angle) * radius),
                                probeCenter.Y + ((float)Math.Sin(angle) * radius),
                                probeCenter.Z);
                            safePositionProbeCount++;
                            Vector3 safe = probe;
                            if (!World.GetSafePositionForPed(
                                probe,
                                out safe,
                                (GetSafePositionFlags)0))
                                continue;
                            safePositionFoundCount++;

                            float correctionDistance = safe.DistanceTo(probe);
                            float anchorDistance = safe.DistanceTo(probeCenter);
                            float authoredDistance = safe.DistanceTo(insideThreshold);
                            if (float.IsNaN(correctionDistance)
                                || float.IsInfinity(correctionDistance)
                                || correctionDistance > 2.25f
                                || anchorDistance < InteriorActivityMinimumDepth
                                || anchorDistance > InteriorActivityMaximumDepth
                                || (usePlayerFallback
                                    && authoredDistance > MaximumInteriorPlayerFallbackDistance)
                                || (requireMappedThresholdDepth
                                    && safe.DistanceTo(outsideEntry) < InteriorActivityMinimumDepth))
                            {
                                safeConstraintRejectedCount++;
                                continue;
                            }
                            if (GetInteriorAt(safe) != expectedInteriorId)
                            {
                                interiorIdentityRejectedCount++;
                                continue;
                            }

                            float groundZ;
                            Vector3 groundNormal;
                            floorProbeCount++;
                            if (!World.GetGroundHeightAndNormal(safe, out groundZ, out groundNormal))
                                continue;
                            floorResultCount++;
                            if (float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                                || groundNormal.Z < 0.55f
                                || Math.Abs(safe.Z - groundZ) > 3f
                                || Math.Abs(groundZ - probeCenter.Z)
                                    > InteriorFloorAlignmentTolerance)
                            {
                                floorConstraintRejectedCount++;
                                continue;
                            }
                            safe.Z = groundZ;
                            if (GetInteriorAt(safe) != expectedInteriorId)
                            {
                                interiorIdentityRejectedCount++;
                                continue;
                            }
                            if (HasOverlappingPed(safe, 1.35f)
                                || HasOverlappingVehicle(safe, 1.8f))
                            {
                                occupiedPositionRejectedCount++;
                                continue;
                            }

                            float distance = safe.DistanceTo(insideThreshold);
                            if (distance >= bestDistance)
                                continue;
                            best = safe;
                            bestDistance = distance;
                            foundBest = true;
                        }
                    }
                }

                if (!foundBest)
                {
                    if (safePositionFoundCount == 0
                        || (floorProbeCount > 0 && floorResultCount == 0))
                    {
                        try { RequestInteriorCollision(insideThreshold); }
                        catch (Exception ex)
                        {
                            reason = "PENDING:Status=CollisionPending; Stage=InteriorFloorNavigation; LiveInteriorId=" + expectedInteriorId
                                + "; SafePositionFound=" + safePositionFoundCount + "/" + safePositionProbeCount
                                + "; GroundHeightFound=" + floorResultCount + "/" + floorProbeCount
                                + "; CollisionRequest=Exception:" + ex.GetType().Name;
                            return false;
                        }
                        reason = "PENDING:Status=CollisionPending; Stage=InteriorFloorNavigation; LiveInteriorId=" + expectedInteriorId
                            + "; SafePositionFound=" + safePositionFoundCount + "/" + safePositionProbeCount
                            + "; GroundHeightFound=" + floorResultCount + "/" + floorProbeCount
                            + "; Native=GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                        return false;
                    }
                    string probeResults = "; SafePositionFound=" + safePositionFoundCount
                        + "/" + safePositionProbeCount
                        + "; GroundHeightFound=" + floorResultCount + "/" + floorProbeCount
                        + "; SafeConstraintRejected=" + safeConstraintRejectedCount
                        + "; FloorConstraintRejected=" + floorConstraintRejectedCount
                        + "; InteriorIdentityRejected=" + interiorIdentityRejectedCount
                        + "; OccupiedPositionRejected=" + occupiedPositionRejectedCount
                        + "; PlayerFallbackUsed=" + playerFallbackUsed;
                    if (!interiorReady && playerInside)
                    {
                        try { RequestInteriorCollision(insideThreshold); }
                        catch (Exception ex)
                        {
                            reason = "PENDING:Status=CollisionPending; Stage=InteriorFloorNavigation; LiveInteriorId="
                                + expectedInteriorId + probeResults
                                + "; Native=IS_INTERIOR_READY:false; CollisionRequest=Exception:"
                                + ex.GetType().Name;
                            return false;
                        }
                        reason = "PENDING:Status=InteriorNotReady; Stage=InteriorFloorNavigation; LiveInteriorId="
                            + expectedInteriorId + probeResults
                            + "; Native=IS_INTERIOR_READY:false; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                        return false;
                    }
                    reason = "Stage=InteriorFloorNavigation; LiveInteriorId=" + expectedInteriorId
                        + probeResults
                        + "; Native=GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL"
                        + "; Status=" + (floorConstraintRejectedCount > 0
                            || floorResultCount > 0 ? "FloorUnsupported" : "SceneGeometryRejected")
                        + "; Result=NoClearWalkableGroundWithinMappedInteriorBounds";
                    return false;
                }

                safePosition = best;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorFloorNavigation; LiveInteriorId="
                    + expectedInteriorId
                    + "; Native=GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL"
                    + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        internal bool TryResolvePoliceResponseRoute(
            LSPDDispatchEvent incident,
            out Vector3 destination,
            out string responseMode,
            out string reason)
        {
            destination = Vector3.Zero;
            responseMode = string.Empty;
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff)
            {
                reason = "Dispatch no longer owns an active scene response.";
                return false;
            }

            bool suspectPursuit = incident.State == LSPDDispatchState.SuspectFleeing
                || incident.State == LSPDDispatchState.SuspectResisting;
            Ped suspect = incident.Suspect;
            if (suspectPursuit && suspect != null && suspect.Exists() && !suspect.IsDead)
            {
                int suspectInterior = GetInteriorAt(suspect.Position);
                if (suspectInterior == 0)
                {
                    destination = suspect.Position;
                    responseMode = incident.State == LSPDDispatchState.SuspectResisting
                        ? "ActiveThreat"
                        : "SuspectPursuit";
                    return true;
                }

                // Ordinary vehicle response holds at the exterior approach while
                // the Player works inside. It follows the suspect only after the
                // live position is outside; it does not drive to an interior point.
                if (incident.SceneResolution != null
                    && incident.SceneResolution.HasVehicleStagingPosition)
                {
                    destination = incident.SceneResolution.VehicleStagingPosition;
                    responseMode = "InteriorPerimeter";
                    return true;
                }
            }

            if (incident.SceneResolution != null
                && incident.SceneResolution.HasVehicleStagingPosition)
            {
                destination = incident.SceneResolution.VehicleStagingPosition;
                responseMode = "ExteriorPerimeter";
                return true;
            }

            if (IsFinite(incident.Origin))
            {
                destination = incident.Origin;
                responseMode = "SceneSupport";
                return true;
            }

            reason = "The Dispatch scene has no validated exterior response position.";
            return false;
        }

        /// <summary>
        /// Starts only the physical door portion of actor access. The caller
        /// retains movement ownership and must use Route.DestinationPosition in
        /// its own documented navigation flow.
        /// </summary>
        internal bool TryBeginInteriorAccess(
            Ped actor,
            AmbientInteriorAccessRoute route,
            out string reason)
        {
            reason = string.Empty;
            if (actor == null || !actor.Exists() || actor.IsDead || actor.IsInVehicle()
                || route == null || route.Pair == null)
            {
                reason = "The actor or mapped threshold is no longer available.";
                return false;
            }
            float entryDistance = actor.Position.DistanceTo(route.EntryPosition);
            Vector3 oppositeSide = route.EnteringInterior
                ? route.Pair.InsidePoint.Position
                : route.Pair.OutsidePoint.Position;
            float oppositeDistance = actor.Position.DistanceTo(oppositeSide);
            if (float.IsNaN(entryDistance) || float.IsInfinity(entryDistance)
                || entryDistance > InteriorAccessUseRadius
                || entryDistance > oppositeDistance + 0.35f)
            {
                reason = "The actor must be on foot beside the mapped threshold before opening it.";
                return false;
            }

            AmbientInteriorAccessSession existing;
            if (_interiorAccessSessions.TryGetValue(actor.Handle, out existing))
                return true;

            Entity doorEntity;
            int doorHash;
            if (!TryFindThresholdDoor(actor, route, out doorEntity, out doorHash, out reason))
                return false;

            try
            {
                int currentDoorState = Function.Call<int>(
                    Hash.DOOR_SYSTEM_GET_DOOR_STATE, doorHash);
                AmbientInteriorAccessSession existingDoorSession =
                    _interiorAccessSessions.Values.FirstOrDefault(candidate =>
                        candidate != null && candidate.DoorHash == doorHash);
                float currentOpenRatio = Function.Call<float>(
                    Hash.DOOR_SYSTEM_GET_OPEN_RATIO, doorHash);
                if (float.IsNaN(currentOpenRatio) || float.IsInfinity(currentOpenRatio))
                {
                    reason = "GTA did not return a valid physical door state.";
                    return false;
                }

                int now = Game.GameTime;
                var session = new AmbientInteriorAccessSession
                {
                    ActorHandle = actor.Handle,
                    Route = route,
                    DoorEntityHandle = doorEntity.Handle,
                    DoorEntityModelHash = doorEntity.Model.Hash,
                    DoorEntityOriginalPosition = doorEntity.Position,
                    DoorEntityOriginalRotation = doorEntity.Rotation,
                    DoorHash = doorHash,
                    OriginalDoorState = existingDoorSession == null
                        ? currentDoorState : existingDoorSession.OriginalDoorState,
                    OriginalOpenRatio = existingDoorSession == null
                        ? currentOpenRatio : existingDoorSession.OriginalOpenRatio,
                    StartedAtGameTime = now,
                    LastOpenRequestGameTime = now,
                    LastRequestedOpenRatio = Math.Max(0f, currentOpenRatio),
                    NextDoorUnlockAttemptAtGameTime = now,
                    NextFloorPreflightAtGameTime = now,
                    NextInteriorStreamingRequestAtGameTime = now,
                    NextInteriorReadinessPollAtGameTime = now,
                    InteriorReadinessConfirmed = !route.EnteringInterior,
                    CrossingSinceGameTime = -1,
                    LastObservedPosition = actor.Position,
                    LastObservedProgress = GetInteriorDoorwayProgress(
                        route, actor.Position),
                    LastObservedAtGameTime = now
                };
                RequestInteriorCollision(route.EntryPosition);
                RequestInteriorCollision(route.DestinationPosition);
                _interiorAccessSessions[actor.Handle] = session;

                Log("AMBIENT_WORLD_INTERIOR_DOOR_OPEN_REQUESTED",
                    "Actor=" + actor.Handle
                    + "; DoorEntity=" + doorEntity.Handle
                    + "; DoorEntityModelHash=" + doorEntity.Model.Hash
                    + "; DoorEntityOriginalPosition=" + DescribePosition(doorEntity.Position)
                    + "; DoorEntityOriginalRotation=" + doorEntity.Rotation
                    + "; DoorHash=" + doorHash
                    + "; Location=" + route.Pair.InsideLocation.Id
                    + "; PhysicalType=" + route.Pair.InsideLocation.PhysicalType
                    + "; ActorModelHash=" + actor.Model.Hash
                    + "; ActorIsPlayer=" + (actor.Handle == Game.Player.Character.Handle)
                    + "; OutsideCapture=" + route.Pair.OutsidePoint.SourceRecord
                    + "; InsideCapture=" + route.Pair.InsidePoint.SourceRecord
                    + "; MappingBasis=" + route.Pair.MappingBasis
                    + "; CurrentLiveInteriorId=" + route.CurrentLiveInteriorId
                    + "; TargetLiveInteriorId=" + route.TargetLiveInteriorId
                    + "; StoreControllerState=NotExposedByMappedDoorwayNatives"
                    + "; OriginalDoorState=" + session.OriginalDoorState
                    + "; OriginalOpenRatio=" + session.OriginalOpenRatio.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; DistanceMeters=" + route.Pair.DistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Entering=" + route.EnteringInterior);
                return true;
            }
            catch (Exception ex)
            {
                reason = "GTA could not open the mapped door: " + ex.Message;
                Log("AMBIENT_WORLD_INTERIOR_DOOR_OPEN_FAILED",
                    "Actor=" + actor.Handle + "; DoorHash=" + doorHash + "; Reason=" + reason);
                return false;
            }
        }

        /// <summary>
        /// Keeps an owned physical doorway open while the caller's actor crosses,
        /// then restores the captured door ratio after the expected interior
        /// change is confirmed. It never moves or teleports the actor.
        /// </summary>
        internal AmbientInteriorAccessStatus MaintainInteriorAccess(
            Ped actor,
            out string message)
        {
            message = string.Empty;
            if (actor == null)
                return AmbientInteriorAccessStatus.None;

            AmbientInteriorAccessSession session;
            if (!_interiorAccessSessions.TryGetValue(actor.Handle, out session))
                return AmbientInteriorAccessStatus.None;
            if (!actor.Exists() || actor.IsDead || actor.IsInVehicle())
            {
                ReleaseInteriorAccessSession(actor.Handle, "ActorUnavailable");
                message = "Interior access ended because the actor is no longer on foot.";
                return AmbientInteriorAccessStatus.Failed;
            }

            int now = Game.GameTime;
            int elapsed = unchecked(now - session.StartedAtGameTime);
            if (elapsed < 0)
                elapsed = 0;
            if (elapsed > InteriorAccessTimeoutMilliseconds)
            {
                string timeoutStatus = !session.InteriorReadinessConfirmed
                    ? "InteriorNotReady"
                    : !session.DestinationFloorPreflightReady
                        ? (!string.IsNullOrWhiteSpace(session.FloorPreflightLastFailure)
                            && (session.FloorPreflightLastFailure.IndexOf(
                                    "GroundPending", StringComparison.OrdinalIgnoreCase) >= 0
                                || session.FloorPreflightLastFailure.IndexOf(
                                    "GroundQueryFailed", StringComparison.OrdinalIgnoreCase) >= 0)
                            ? "CollisionPending" : "FloorUnsupported")
                    : !session.DoorPhysicsReadyConfirmed
                        ? "DoorPhysicsUnavailable"
                        : !session.DoorPhysicallyOpenConfirmed
                            ? "DoorOpenUnconfirmed"
                            : "DoorPassageUnconfirmed";
                ReleaseInteriorAccessSession(actor.Handle,
                    "InteriorAccessTimeout:" + timeoutStatus);
                message = "Interior access is not ready (" + timeoutStatus
                    + "). The incident remains available for a later retry.";
                Log("AMBIENT_WORLD_INTERIOR_ACCESS_TIMEOUT",
                    "Actor=" + actor.Handle
                    + "; Location=" + session.Route.Pair.InsideLocation.Id
                    + "; PhysicalType=" + session.Route.Pair.InsideLocation.PhysicalType
                    + "; DoorEntity=" + session.DoorEntityHandle
                    + "; DoorHash=" + session.DoorHash
                    + "; Status=" + timeoutStatus
                    + "; FloorEvidence=" + (session.FloorPreflightLastFailure ?? string.Empty)
                    + "; InteriorReadinessEvidence=" + (session.InteriorReadinessLastFailure ?? string.Empty)
                    + "; DoorPhysicsReady=" + session.DoorPhysicsReadyConfirmed
                    + "; DoorEntityMotionConfirmed=" + session.DoorPhysicallyOpenConfirmed
                    + "; DoorOpenRatioAtLastCheck=" + session.LastObservedDoorOpenRatio.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; DoorEntityMotionEvidence=" + (session.DoorEntityMotionEvidence ?? string.Empty)
                    + "; StoreControllerState=NotExposedByMappedDoorwayNatives");
                return AmbientInteriorAccessStatus.Failed;
            }

            if (!session.InteriorReadinessConfirmed
                && unchecked(now - session.NextInteriorReadinessPollAtGameTime) >= 0)
            {
                session.NextInteriorReadinessPollAtGameTime = unchecked(now + 400);
                if (unchecked(now - session.NextInteriorStreamingRequestAtGameTime) >= 0)
                {
                    session.NextInteriorStreamingRequestAtGameTime = unchecked(now + 1200);
                    string streamingReason;
                    bool streamingRequested = TryRequestDispatchInteriorStreaming(
                        session.Route.TargetLiveInteriorId,
                        session.Route.DestinationPosition,
                        session.Route.Pair.InsideLocation.Id,
                        session.Route.Pair.InteriorId,
                        out streamingReason);
                    if (!streamingRequested
                        || string.IsNullOrWhiteSpace(session.InteriorReadinessLastFailure)
                        || session.InteriorReadinessLastFailure.StartsWith(
                            "PENDING:", StringComparison.OrdinalIgnoreCase))
                        session.InteriorReadinessLastFailure = streamingReason;
                }

                bool interiorReady = false;
                try
                {
                    interiorReady = session.Route.TargetLiveInteriorId > 0
                        && IsInteriorReady(session.Route.TargetLiveInteriorId);
                }
                catch (Exception ex)
                {
                    session.InteriorReadinessLastFailure = ex.GetType().Name;
                }
                if (interiorReady)
                {
                    session.InteriorReadinessConfirmed = true;
                    session.InteriorReadinessLastFailure = string.Empty;
                    Log("AMBIENT_WORLD_INTERIOR_ACCESS_INTERIOR_READY",
                        "Actor=" + actor.Handle
                        + "; Location=" + session.Route.Pair.InsideLocation.Id
                        + "; PhysicalType=" + session.Route.Pair.InsideLocation.PhysicalType
                        + "; LiveInteriorId=" + session.Route.TargetLiveInteriorId
                        + "; Streaming=PIN_INTERIOR_IN_MEMORY"
                        + "; Collision=Requested");
                }
                else if (!session.InteriorReadinessWaitLogged)
                {
                    session.InteriorReadinessWaitLogged = true;
                    Log("AMBIENT_WORLD_INTERIOR_ACCESS_INTERIOR_PENDING",
                        "Actor=" + actor.Handle
                        + "; Location=" + session.Route.Pair.InsideLocation.Id
                        + "; PhysicalType=" + session.Route.Pair.InsideLocation.PhysicalType
                        + "; LiveInteriorId=" + session.Route.TargetLiveInteriorId
                        + "; Status=InteriorNotReady"
                        + "; RequestResult=" + (session.InteriorReadinessLastFailure ?? string.Empty)
                        + "; Native=IS_INTERIOR_READY:false"
                        + "; Streaming=PIN_INTERIOR_IN_MEMORY:Requested"
                        + "; Collision=REQUEST_COLLISION_AT_COORD:Requested");
                }
                if (!session.InteriorReadinessConfirmed)
                    return AmbientInteriorAccessStatus.DoorOpening;
            }
            if (!session.InteriorReadinessConfirmed)
                return AmbientInteriorAccessStatus.DoorOpening;

            if (!session.DestinationFloorPreflightReady
                && unchecked(now - session.NextFloorPreflightAtGameTime) >= 0)
            {
                session.NextFloorPreflightAtGameTime = unchecked(now + 400);
                RequestInteriorCollision(session.Route.EntryPosition);
                RequestInteriorCollision(session.Route.DestinationPosition);
                string entryFloorEvidence;
                string destinationFloorEvidence;
                bool entryFloorReady = IsMappedDoorwayFloorSupported(
                    session.Route.EntryPosition, out entryFloorEvidence);
                bool destinationFloorReady = IsMappedDoorwayFloorSupported(
                    session.Route.DestinationPosition, out destinationFloorEvidence);
                session.FloorPreflightLastFailure = entryFloorReady && destinationFloorReady
                    ? string.Empty
                    : "Entry=" + entryFloorEvidence + "; Destination=" + destinationFloorEvidence;
                if (entryFloorReady && destinationFloorReady)
                {
                    session.DestinationFloorPreflightReady = true;
                    Log("AMBIENT_WORLD_INTERIOR_DOORWAY_FLOOR_READY",
                        "Actor=" + actor.Handle
                        + "; DoorHash=" + session.DoorHash
                        + "; Entry=" + entryFloorEvidence
                        + "; Destination=" + destinationFloorEvidence
                        + "; CollisionRequested=true");
                }
                else if (!session.FloorPreflightWaitLogged)
                {
                    session.FloorPreflightWaitLogged = true;
                    Log("AMBIENT_WORLD_INTERIOR_DOORWAY_FLOOR_PENDING",
                        "Actor=" + actor.Handle
                        + "; DoorHash=" + session.DoorHash
                        + "; Entry=" + entryFloorEvidence
                        + "; Destination=" + destinationFloorEvidence
                        + "; CollisionRequested=true; DoorOpeningBlocked=true");
                }
            }
            if (!session.DestinationFloorPreflightReady)
                return AmbientInteriorAccessStatus.DoorOpening;

            int actorInteriorId = GetInteriorAt(actor.Position);
            float entryDistance = actor.Position.DistanceTo(session.Route.EntryPosition);
            float destinationDistance = actor.Position.DistanceTo(
                session.Route.DestinationPosition);
            ObserveInteriorDoorwayProgress(session, actor, now);
            bool crossedThreshold = session.DoorPhysicallyOpenConfirmed
                && session.CrossingIntermediateObserved
                && (actorInteriorId == session.Route.TargetLiveInteriorId
                    || (session.Route.EnteringInterior && actorInteriorId == 0))
                && HasReachedInteriorDoorwayDestination(session, actor.Position);
            if (crossedThreshold)
            {
                if (!session.CrossingInteriorObserved)
                {
                    session.CrossingInteriorObserved = true;
                    session.CrossingSinceGameTime = now;
                }
                else if (unchecked(now - session.CrossingSinceGameTime) >= 1200)
                {
                    int entityInteriorId;
                    bool entityInteriorIdAvailable = TryGetActorInteriorId(
                        actor, out entityInteriorId);
                    int coordinateInteriorId = GetInteriorAt(actor.Position);
                    if (session.Route.EnteringInterior
                        && session.Route.TargetLiveInteriorId > 0)
                    {
                        _confirmedInteriorCrossingsByActor[actor.Handle] =
                            new AmbientConfirmedInteriorCrossing
                            {
                                Pair = session.Route.Pair,
                                InteriorId = session.Route.TargetLiveInteriorId,
                                CrossingPosition = actor.Position,
                                InteriorFloorPosition = session.Route.DestinationPosition,
                                ConfirmedAtGameTime = now
                            };
                    }
                    else
                    {
                        _confirmedInteriorCrossingsByActor.Remove(actor.Handle);
                    }
                    Log("AMBIENT_WORLD_INTERIOR_THRESHOLD_CROSSING_CONFIRMED",
                        "Actor=" + actor.Handle
                        + "; Direction=" + (session.Route.EnteringInterior
                            ? "OutsideToInside" : "InsideToOutside")
                        + "; Position=" + actor.Position
                        + "; EntityInteriorId=" + (entityInteriorIdAvailable
                            ? entityInteriorId.ToString(CultureInfo.InvariantCulture)
                            : "Unavailable")
                        + "; CoordinateInteriorId=" + coordinateInteriorId
                        + "; TargetInteriorId=" + session.Route.TargetLiveInteriorId
                        + "; PhysicalDoorOpenConfirmed=" + session.DoorPhysicallyOpenConfirmed
                        + "; Location=" + session.Route.Pair.InsideLocation.Id
                        + "; Evidence=MappedDoorAndStablePhysicalCrossing");
                    ReleaseInteriorAccessSession(actor.Handle, "ThresholdCrossingConfirmed");
                    message = session.Route.EnteringInterior
                        ? "The actor crossed the mapped entrance into the interior."
                        : "The actor crossed the mapped entrance back outside.";
                    return AmbientInteriorAccessStatus.Completed;
                }
            }
            else
            {
                session.CrossingInteriorObserved = false;
                session.CrossingSinceGameTime = -1;
            }

            if (float.IsNaN(entryDistance) || float.IsInfinity(entryDistance)
                || (entryDistance > 9f && destinationDistance > 9f))
            {
                ReleaseInteriorAccessSession(actor.Handle, "ActorLeftThresholdArea");
                message = "Interior access ended because the actor left the mapped doorway area.";
                return AmbientInteriorAccessStatus.Failed;
            }

            try
            {
                bool doorPhysicsLoaded = Function.Call<bool>(
                    Hash.DOOR_SYSTEM_GET_IS_PHYSICS_LOADED,
                    session.DoorHash);
                if (!doorPhysicsLoaded)
                {
                    if (!session.DoorPhysicsWaitLogged)
                    {
                        session.DoorPhysicsWaitLogged = true;
                        Log("AMBIENT_WORLD_INTERIOR_DOOR_PHYSICS_WAIT",
                            "Actor=" + actor.Handle
                            + "; DoorHash=" + session.DoorHash
                            + "; Location=" + session.Route.Pair.InsideLocation.Id);
                    }
                    return AmbientInteriorAccessStatus.DoorOpening;
                }
                session.DoorPhysicsReadyConfirmed = true;

                int doorState = Function.Call<int>(
                    Hash.DOOR_SYSTEM_GET_DOOR_STATE,
                    session.DoorHash);
                if (doorState != UnlockedDoorSystemState
                    && doorState != UnlockedThisFrameDoorSystemState)
                {
                    if (session.DoorUnlockAttempts
                            < InteriorAccessDoorUnlockMaximumAttempts
                        && unchecked(now - session.NextDoorUnlockAttemptAtGameTime) >= 0)
                    {
                        Function.Call(
                            Hash.DOOR_SYSTEM_SET_DOOR_STATE,
                            session.DoorHash,
                            UnlockedDoorSystemState,
                            true,
                            true);
                        session.DoorUnlockAttempts++;
                        session.NextDoorUnlockAttemptAtGameTime = unchecked(
                            now + InteriorAccessDoorUnlockRetryMilliseconds);
                        Log("AMBIENT_WORLD_INTERIOR_DOOR_UNLOCK_REQUESTED",
                            "Actor=" + actor.Handle
                            + "; DoorHash=" + session.DoorHash
                            + "; PreviousState=" + doorState
                            + "; Attempt=" + session.DoorUnlockAttempts
                            + "; Location=" + session.Route.Pair.InsideLocation.Id);
                    }

                    if (session.DoorUnlockAttempts
                            >= InteriorAccessDoorUnlockMaximumAttempts
                        && !session.DoorUnlockFailureLogged
                        && unchecked(now - session.NextDoorUnlockAttemptAtGameTime) >= 0)
                    {
                        session.DoorUnlockFailureLogged = true;
                        Log("STATE_FAILURE_AMBIENT_INTERIOR_DOOR_UNLOCK_NOT_CONFIRMED",
                            "Actor=" + actor.Handle
                            + "; DoorHash=" + session.DoorHash
                            + "; DoorState=" + doorState
                            + "; Attempts=" + session.DoorUnlockAttempts);
                        ReleaseInteriorAccessSession(actor.Handle,
                            "DoorUnlockNotConfirmed");
                        message = "GTA kept the mapped door locked. No crossing was attempted.";
                        return AmbientInteriorAccessStatus.Failed;
                    }

                    return AmbientInteriorAccessStatus.DoorOpening;
                }

                if (!session.DoorUnlockConfirmed)
                {
                    session.DoorUnlockConfirmed = true;
                    Function.Call(Hash.DOOR_SYSTEM_SET_HOLD_OPEN,
                        session.DoorHash, true);
                    Log("AMBIENT_WORLD_INTERIOR_DOOR_UNLOCK_CONFIRMED",
                        "Actor=" + actor.Handle
                        + "; DoorHash=" + session.DoorHash
                        + "; DoorState=" + doorState
                        + "; Location=" + session.Route.Pair.InsideLocation.Id);
                }

                float openRatio = Function.Call<float>(
                    Hash.DOOR_SYSTEM_GET_OPEN_RATIO, session.DoorHash);
                if (float.IsNaN(openRatio) || float.IsInfinity(openRatio))
                {
                    ReleaseInteriorAccessSession(actor.Handle, "DoorStateUnavailable");
                    message = "The physical door state became unavailable before the actor crossed.";
                    return AmbientInteriorAccessStatus.Failed;
                }

                if (openRatio < InteriorAccessDoorOpenRatio
                    && unchecked(now - session.LastOpenRequestGameTime)
                        >= InteriorAccessDoorOpenStepMilliseconds)
                {
                    Function.Call(Hash.DOOR_SYSTEM_SET_HOLD_OPEN, session.DoorHash, true);
                    float nextOpenRatio = Math.Min(
                        InteriorAccessDoorOpenRatio,
                        Math.Max(openRatio, session.LastRequestedOpenRatio)
                            + InteriorAccessDoorOpenStep);
                    Function.Call(
                        Hash.DOOR_SYSTEM_SET_OPEN_RATIO,
                        session.DoorHash,
                        nextOpenRatio,
                        true,
                        false);
                    session.LastOpenRequestGameTime = now;
                    session.LastRequestedOpenRatio = nextOpenRatio;
                }

                session.LastObservedDoorOpenRatio = openRatio;
                if (openRatio >= InteriorAccessDoorOpenRatio)
                {
                    string motionEvidence;
                    bool doorEntityMoved = HasMappedDoorEntityPhysicallyMoved(
                        session, out motionEvidence);
                    session.DoorEntityMotionEvidence = motionEvidence;
                    if (doorEntityMoved)
                    {
                        session.DoorPhysicallyOpenConfirmed = true;
                        if (!session.ReadyReported)
                        {
                            session.ReadyReported = true;
                            Log("AMBIENT_WORLD_INTERIOR_DOOR_OPEN_CONFIRMED",
                                "Actor=" + actor.Handle
                                + "; DoorEntity=" + session.DoorEntityHandle
                                + "; DoorEntityModelHash=" + session.DoorEntityModelHash
                                + "; DoorHash=" + session.DoorHash
                                + "; DoorPhysicsLoaded=true"
                                + "; OpenRatio=" + openRatio.ToString("0.00", CultureInfo.InvariantCulture)
                                + "; PhysicalEntityMotion=" + motionEvidence
                                + "; MappingBasis=" + session.Route.Pair.MappingBasis);
                            message = "The mapped door is open. Walk through it to continue.";
                            return AmbientInteriorAccessStatus.ReadyToCross;
                        }
                    }
                    else if (!session.DoorEntityMotionWaitLogged)
                    {
                        session.DoorEntityMotionWaitLogged = true;
                        Log("AMBIENT_WORLD_INTERIOR_DOOR_RATIO_WITHOUT_PHYSICAL_MOTION",
                            "Actor=" + actor.Handle
                            + "; DoorEntity=" + session.DoorEntityHandle
                            + "; DoorEntityModelHash=" + session.DoorEntityModelHash
                            + "; DoorHash=" + session.DoorHash
                            + "; DoorPhysicsLoaded=true"
                            + "; OpenRatio=" + openRatio.ToString("0.00", CultureInfo.InvariantCulture)
                            + "; PhysicalEntityMotion=" + motionEvidence
                            + "; Result=RemainPending; Location=" + session.Route.Pair.InsideLocation.Id);
                    }
                }

                return session.DoorPhysicallyOpenConfirmed
                    ? AmbientInteriorAccessStatus.ReadyToCross
                    : AmbientInteriorAccessStatus.DoorOpening;
            }
            catch (Exception ex)
            {
                ReleaseInteriorAccessSession(actor.Handle, "DoorMaintenanceFailed");
                message = "Interior access ended because GTA could not maintain the physical door: "
                    + ex.Message;
                return AmbientInteriorAccessStatus.Failed;
            }
        }

        private static bool IsActorOnConfirmedInteriorSide(
            Ped actor,
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair,
            AmbientConfirmedInteriorCrossing crossing)
        {
            if (actor == null || !actor.Exists() || pair == null || crossing == null
                || crossing.Pair == null || crossing.InteriorId <= 0
                || pair.OutsidePoint == null || pair.InsidePoint == null
                || crossing.Pair.OutsidePoint == null || crossing.Pair.InsidePoint == null
                || !string.Equals(pair.InsideLocation == null ? string.Empty : pair.InsideLocation.Id,
                    crossing.Pair.InsideLocation == null ? string.Empty : crossing.Pair.InsideLocation.Id,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(pair.OutsidePoint.Id, crossing.Pair.OutsidePoint.Id,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(pair.InsidePoint.Id, crossing.Pair.InsidePoint.Id,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            Vector3 position = actor.Position;
            float distanceFromConfirmedFloor = Distance2D(
                position, crossing.InteriorFloorPosition);
            float floorOffset = Math.Abs(position.Z - crossing.InteriorFloorPosition.Z);
            float insideDistance = position.DistanceTo(pair.InsidePoint.Position);
            float outsideDistance = position.DistanceTo(pair.OutsidePoint.Position);
            return !float.IsNaN(distanceFromConfirmedFloor)
                && !float.IsInfinity(distanceFromConfirmedFloor)
                && !float.IsNaN(floorOffset) && !float.IsInfinity(floorOffset)
                && distanceFromConfirmedFloor <= ConfirmedInteriorIdentityMaximumRange
                && floorOffset <= ConfirmedInteriorIdentityMaximumFloorOffset
                && !float.IsNaN(insideDistance) && !float.IsInfinity(insideDistance)
                && !float.IsNaN(outsideDistance) && !float.IsInfinity(outsideDistance)
                && insideDistance <= InteriorAccessUseRadius
                && insideDistance <= outsideDistance + 0.35f;
        }

        private static bool HasReachedInteriorDoorwayDestination(
            AmbientInteriorAccessSession session,
            Vector3 position)
        {
            if (session == null || session.Route == null
                || !session.DestinationFloorPreflightReady)
                return false;

            Vector3 entry = session.Route.EntryPosition;
            Vector3 destination = session.Route.DestinationPosition;
            float dx = destination.X - entry.X;
            float dy = destination.Y - entry.Y;
            float dz = destination.Z - entry.Z;
            float segmentLengthSquared = dx * dx + dy * dy + dz * dz;
            if (segmentLengthSquared < 0.25f
                || float.IsNaN(segmentLengthSquared)
                || float.IsInfinity(segmentLengthSquared))
                return false;

            float progress = ((position.X - entry.X) * dx
                + (position.Y - entry.Y) * dy
                + (position.Z - entry.Z) * dz) / segmentLengthSquared;
            float destinationDistance = position.DistanceTo(destination);
            float entryDistance = position.DistanceTo(entry);
            return !float.IsNaN(progress) && !float.IsInfinity(progress)
                && progress >= 0.72f
                && destinationDistance <= InteriorAccessDestinationRadius
                && destinationDistance + 0.45f < entryDistance;
        }

        private void ObserveInteriorDoorwayProgress(
            AmbientInteriorAccessSession session,
            Ped actor,
            int now)
        {
            if (session == null || actor == null || !actor.Exists()
                || !session.DoorPhysicallyOpenConfirmed
                || !session.DestinationFloorPreflightReady)
                return;

            Vector3 position = actor.Position;
            int elapsedMilliseconds = unchecked(now - session.LastObservedAtGameTime);
            if (elapsedMilliseconds < 0)
                elapsedMilliseconds = 0;
            float movementDistance = position.DistanceTo(session.LastObservedPosition);
            float progress = GetInteriorDoorwayProgress(session.Route, position);
            float maximumMovement = 0.75f
                + elapsedMilliseconds * 0.018f;
            bool continuousMovement = !float.IsNaN(movementDistance)
                && !float.IsInfinity(movementDistance)
                && movementDistance <= maximumMovement;
            if (!continuousMovement)
            {
                session.CrossingIntermediateObserved = false;
                session.CrossingInteriorObserved = false;
                session.CrossingSinceGameTime = -1;
                if (!session.CrossingDiscontinuityLogged)
                {
                    session.CrossingDiscontinuityLogged = true;
                    Log("AMBIENT_WORLD_INTERIOR_CROSSING_PROGRESS_RESET",
                        "Actor=" + actor.Handle
                        + "; Location=" + session.Route.Pair.InsideLocation.Id
                        + "; DoorHash=" + session.DoorHash
                        + "; MovementMeters=" + movementDistance.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; MaximumContinuousMovementMeters=" + maximumMovement.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Result=PhysicalCrossingMustBeObservedAgain");
                }
                session.LastObservedPosition = position;
                session.LastObservedProgress = progress;
                session.LastObservedAtGameTime = now;
                return;
            }

            float previousProgress = session.LastObservedProgress;
            float expectedFloorZ = session.Route.EntryPosition.Z
                + (session.Route.DestinationPosition.Z
                    - session.Route.EntryPosition.Z) * progress;
            bool onMappedFloorBand = !float.IsNaN(progress)
                && !float.IsInfinity(progress)
                && !float.IsNaN(expectedFloorZ) && !float.IsInfinity(expectedFloorZ)
                && Math.Abs(position.Z - expectedFloorZ)
                    <= InteriorFloorAlignmentTolerance + 0.5f;
            bool movedTowardDestination = progress >= previousProgress - 0.02f;
            bool reachedIntermediate = progress >= 0.12f && progress < 0.72f;
            bool crossedIntermediateBetweenUpdates = previousProgress < 0.72f
                && progress >= 0.72f
                && progress - previousProgress <= 0.82f;
            if (!session.CrossingIntermediateObserved
                && onMappedFloorBand && movedTowardDestination
                && (reachedIntermediate || crossedIntermediateBetweenUpdates))
            {
                session.CrossingIntermediateObserved = true;
                Log("AMBIENT_WORLD_INTERIOR_CROSSING_PROGRESS_OBSERVED",
                    "Actor=" + actor.Handle
                    + "; Location=" + session.Route.Pair.InsideLocation.Id
                    + "; DoorHash=" + session.DoorHash
                    + "; Progress=" + progress.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Position=" + DescribePosition(position)
                    + "; MappedFloorBand=true");
            }

            session.LastObservedPosition = position;
            session.LastObservedProgress = progress;
            session.LastObservedAtGameTime = now;
        }

        private static bool HasMappedDoorEntityPhysicallyMoved(
            AmbientInteriorAccessSession session,
            out string evidence)
        {
            evidence = "DoorEntityUnavailable";
            if (session == null || session.DoorEntityHandle <= 0)
                return false;

            try
            {
                Entity doorEntity = Entity.FromHandle(session.DoorEntityHandle);
                if (doorEntity == null || !doorEntity.Exists())
                {
                    evidence = "DoorEntityMissing; Handle=" + session.DoorEntityHandle;
                    return false;
                }

                int currentModelHash = doorEntity.Model.Hash;
                if (currentModelHash != session.DoorEntityModelHash)
                {
                    evidence = "DoorEntityModelChanged; Expected="
                        + session.DoorEntityModelHash + "; Current=" + currentModelHash;
                    return false;
                }

                Vector3 currentPosition = doorEntity.Position;
                Vector3 currentRotation = doorEntity.Rotation;
                float positionDelta = currentPosition.DistanceTo(
                    session.DoorEntityOriginalPosition);
                float xRotationDelta = GetWrappedAngleDifference(
                    currentRotation.X, session.DoorEntityOriginalRotation.X);
                float yRotationDelta = GetWrappedAngleDifference(
                    currentRotation.Y, session.DoorEntityOriginalRotation.Y);
                float zRotationDelta = GetWrappedAngleDifference(
                    currentRotation.Z, session.DoorEntityOriginalRotation.Z);
                float maximumRotationDelta = Math.Max(xRotationDelta,
                    Math.Max(yRotationDelta, zRotationDelta));
                bool validEvidence = !float.IsNaN(positionDelta)
                    && !float.IsInfinity(positionDelta)
                    && !float.IsNaN(maximumRotationDelta)
                    && !float.IsInfinity(maximumRotationDelta);
                bool moved = validEvidence
                    && (positionDelta >= InteriorAccessDoorMotionPositionThreshold
                        || maximumRotationDelta >= InteriorAccessDoorMotionAngleThreshold);
                evidence = "PositionDeltaMeters="
                    + positionDelta.ToString("0.000", CultureInfo.InvariantCulture)
                    + "; RotationDeltaDegrees="
                    + maximumRotationDelta.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; RequiredPositionMeters="
                    + InteriorAccessDoorMotionPositionThreshold.ToString("0.000", CultureInfo.InvariantCulture)
                    + "; RequiredRotationDegrees="
                    + InteriorAccessDoorMotionAngleThreshold.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Result=" + (moved ? "Moved" : "NoObservedMovement");
                return moved;
            }
            catch (Exception ex)
            {
                evidence = "DoorEntityMotionQueryFailed=" + ex.GetType().Name;
                return false;
            }
        }

        private static float GetWrappedAngleDifference(float first, float second)
        {
            float difference = Math.Abs(first - second) % 360f;
            return difference > 180f ? 360f - difference : difference;
        }

        private static float GetInteriorDoorwayProgress(
            AmbientInteriorAccessRoute route,
            Vector3 position)
        {
            if (route == null)
                return float.NaN;
            Vector3 entry = route.EntryPosition;
            Vector3 destination = route.DestinationPosition;
            float dx = destination.X - entry.X;
            float dy = destination.Y - entry.Y;
            float dz = destination.Z - entry.Z;
            float segmentLengthSquared = dx * dx + dy * dy + dz * dz;
            if (segmentLengthSquared < 0.25f
                || float.IsNaN(segmentLengthSquared)
                || float.IsInfinity(segmentLengthSquared))
                return float.NaN;
            return ((position.X - entry.X) * dx
                + (position.Y - entry.Y) * dy
                + (position.Z - entry.Z) * dz) / segmentLengthSquared;
        }

        internal void CancelAllInteriorAccessSessions(string reason)
        {
            foreach (int actorHandle in _interiorAccessSessions.Keys.ToArray())
                ReleaseInteriorAccessSession(actorHandle,
                    string.IsNullOrWhiteSpace(reason) ? "Cancelled" : reason);
            _confirmedInteriorCrossingsByActor.Clear();
        }

        private void ClearInteriorAccessPromptState()
        {
            if (_log != null)
                _log.ClearRuntimeTransition(
                    "AMBIENT_WORLD_INTERIOR_ACCESS_PROMPT_AVAILABLE");
        }

        internal void CancelInteriorAccess(Ped actor, string reason)
        {
            if (actor == null)
                return;
            ReleaseInteriorAccessSession(
                actor.Handle,
                string.IsNullOrWhiteSpace(reason) ? "Cancelled" : reason);
        }

        private bool IsNearMappedInteriorPair(Vector3 position)
        {
            if (_locationCatalog == null)
                return false;
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in GetInteriorAccessPairs())
            {
                if (pair == null || pair.OutsidePoint == null || pair.InsidePoint == null)
                    continue;
                if (position.DistanceTo(pair.OutsidePoint.Position) <= InteriorAccessUseRadius
                    || position.DistanceTo(pair.InsidePoint.Position) <= InteriorAccessUseRadius)
                    return true;
            }
            return false;
        }

        private IEnumerable<LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair>
            GetInteriorAccessPairs()
        {
            if (_locationCatalog == null)
                return Enumerable.Empty<LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair>();

            return _locationCatalog.InteriorAccessPairs;
        }

        private List<DispatchInteriorPointReference> GetDispatchInteriorPointReferences()
        {
            if (_locationCatalog == null)
                return new List<DispatchInteriorPointReference>();
            if (ReferenceEquals(_dispatchInteriorPointsCacheCatalog, _locationCatalog)
                && _dispatchInteriorPointReferences != null)
                return _dispatchInteriorPointReferences;

            var references = new List<DispatchInteriorPointReference>();
            const string spawnPointRole = "InteriorPointCandidate";
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location
                in _locationCatalog.Locations)
            {
                if (location == null)
                    continue;
                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationPoint point
                    in location.FindPoints(spawnPointRole, false))
                {
                    if (point == null || !HasObservedNonZeroInterior(point.InteriorId))
                        continue;
                    references.Add(new DispatchInteriorPointReference
                    {
                        Location = location,
                        Point = point
                    });
                }
            }

            _dispatchInteriorPointsCacheCatalog = _locationCatalog;
            _dispatchInteriorPointReferences = references;
            return _dispatchInteriorPointReferences;
        }

        internal bool TryGetActorInteriorId(Ped actor, out int interiorId)
        {
            interiorId = 0;
            if (actor == null || !actor.Exists())
                return false;
            try
            {
                interiorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, actor);
                if (interiorId == 0)
                    interiorId = GetInteriorAt(actor.Position);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal bool TryValidateDispatchInteriorActor(
            Ped actor,
            string actorRole,
            string ownership,
            string occurrenceId,
            AmbientDispatchSceneResolution scene,
            bool requireMappedDoorway,
            out string evidence,
            out string reason)
        {
            evidence = string.Empty;
            reason = "The Dispatch actor or mapped interior scene is unavailable.";
            if (actor == null || !actor.Exists() || scene == null
                || !scene.IsInteriorScene || scene.LiveInteriorId == 0)
            {
                reason = "Stage=InteriorActorIdentity; Result=ActorOrMappedSceneUnavailable";
                return false;
            }

            Vector3 position;
            try
            {
                position = actor.Position;
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorActorIdentity; Handle=" + actor.Handle
                    + "; Result=PositionUnavailable; Exception=" + ex.GetType().Name;
                return false;
            }
            if (float.IsNaN(position.X) || float.IsInfinity(position.X)
                || float.IsNaN(position.Y) || float.IsInfinity(position.Y)
                || float.IsNaN(position.Z) || float.IsInfinity(position.Z))
            {
                reason = "Stage=InteriorActorIdentity; Handle=" + actor.Handle
                    + "; Result=NonFinitePosition";
                return false;
            }

            float sceneCenterDistance = Distance2D(position, scene.SceneCenter);
            if (float.IsNaN(sceneCenterDistance) || float.IsInfinity(sceneCenterDistance)
                || sceneCenterDistance > DispatchVictimMaximumSceneDistance)
            {
                reason = "Stage=InteriorActorSceneRange; Handle=" + actor.Handle
                    + "; Role=" + (actorRole ?? "Actor")
                    + "; Position=" + DescribePosition(position)
                    + "; SceneCenter=" + DescribePosition(scene.SceneCenter)
                    + "; SceneCenterDistance=" + sceneCenterDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; Maximum=" + DispatchVictimMaximumSceneDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; Result=ActorOutsideSceneActivityRadius";
                return false;
            }

            int entityInteriorId = 0;
            bool entityInteriorAvailable = false;
            try
            {
                entityInteriorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, actor);
                entityInteriorAvailable = true;
            }
            catch
            {
                // Coordinate identity remains required and is kept separate
                // from this optional entity-native diagnostic.
            }

            int coordinateInteriorId;
            try
            {
                coordinateInteriorId = GetInteriorAt(position);
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorActorIdentity; Handle=" + actor.Handle
                    + "; EntityLiveInteriorId=" + (entityInteriorAvailable
                        ? entityInteriorId.ToString(CultureInfo.InvariantCulture)
                        : "Unavailable")
                    + "; CoordinateLiveInteriorId=Unavailable"
                    + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                    + "; Result=CoordinateIdentityUnavailable; Exception=" + ex.GetType().Name;
                return false;
            }
            if (coordinateInteriorId != scene.LiveInteriorId
                || !entityInteriorAvailable
                || entityInteriorId != scene.LiveInteriorId)
            {
                reason = "Stage=InteriorActorIdentity; Handle=" + actor.Handle
                    + "; ExpectedLiveInteriorId=" + scene.LiveInteriorId
                    + "; EntityLiveInteriorId=" + (entityInteriorAvailable
                        ? entityInteriorId.ToString(CultureInfo.InvariantCulture)
                        : "Unavailable")
                    + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                    + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                    + "; Result=" + (coordinateInteriorId != scene.LiveInteriorId
                        ? "ActorCoordinatesOutsideSelectedInterior"
                        : !entityInteriorAvailable
                            ? "EntityInteriorIdentityUnavailable"
                            : entityInteriorId == 0
                                ? "ActorEntityOutsideSelectedInterior"
                                : "EntityCoordinateInteriorMismatch");
                return false;
            }

            float groundZ;
            Vector3 groundNormal;
            bool groundFound;
            try
            {
                groundFound = World.GetGroundHeightAndNormal(
                    position, out groundZ, out groundNormal);
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorActorFloor; Handle=" + actor.Handle
                    + "; LiveInteriorId=" + scene.LiveInteriorId
                    + "; Native=GET_GROUND_HEIGHT_AND_NORMAL"
                    + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }
            if (!groundFound)
            {
                try { RequestInteriorCollision(position); }
                catch (Exception ex)
                {
                    reason = "Stage=InteriorActorFloor; Handle=" + actor.Handle
                        + "; LiveInteriorId=" + scene.LiveInteriorId
                        + "; GroundFound=false; CollisionRequest=Exception:" + ex.GetType().Name;
                    return false;
                }
                reason = "PENDING:Stage=InteriorActorFloor; Handle=" + actor.Handle
                    + "; LiveInteriorId=" + scene.LiveInteriorId
                    + "; GroundFound=false; Native=GET_GROUND_HEIGHT_AND_NORMAL"
                    + "; CollisionRequest=REQUEST_COLLISION_AT_COORD:SharedBoundedProbe";
                return false;
            }

            bool validGroundZ = !float.IsNaN(groundZ) && !float.IsInfinity(groundZ);
            float floorDelta = validGroundZ
                ? Math.Abs(groundZ - scene.ActorPosition.Z) : float.PositiveInfinity;
            float actorGroundDelta = validGroundZ
                ? Math.Abs(position.Z - groundZ) : float.PositiveInfinity;
            if (!validGroundZ || float.IsNaN(groundNormal.Z)
                || float.IsInfinity(groundNormal.Z) || groundNormal.Z < 0.55f
                || floorDelta > InteriorFloorAlignmentTolerance
                || actorGroundDelta > 3f)
            {
                reason = "Stage=InteriorActorFloor; Handle=" + actor.Handle
                    + "; LiveInteriorId=" + scene.LiveInteriorId
                    + "; Position=" + DescribePosition(position)
                    + "; GroundZ=" + (validGroundZ
                        ? groundZ.ToString("0.00", CultureInfo.InvariantCulture) : "Invalid")
                    + "; GroundNormalZ=" + groundNormal.Z.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; ExpectedSceneFloorZ=" + scene.ActorPosition.Z.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; FloorDelta=" + floorDelta.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; ActorGroundDelta=" + actorGroundDelta.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; Result=FloorOrGroundAlignmentRejected";
                return false;
            }

            int roomKey = 0;
            bool roomKeyAvailable = false;
            try
            {
                roomKey = Function.Call<int>(Hash.GET_ROOM_KEY_FROM_ENTITY, actor);
                roomKeyAvailable = true;
            }
            catch
            {
                // Current location catalogs do not author an expected room key.
            }
            if (!roomKeyAvailable || roomKey == 0)
            {
                reason = "Stage=InteriorActorRoom; Handle=" + actor.Handle
                    + "; Role=" + (actorRole ?? "Actor")
                    + "; EntityLiveInteriorId=" + entityInteriorId
                    + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                    + "; RoomKey=" + (roomKeyAvailable
                        ? roomKey.ToString(CultureInfo.InvariantCulture) : "Unavailable")
                    + "; Native=GET_ROOM_KEY_FROM_ENTITY"
                    + "; Result=ActorNotConfirmedInsideMappedRoom";
                return false;
            }

            int weaponHash = 0;
            try
            {
                weaponHash = Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, actor);
            }
            catch
            {
                // Keep role and placement evidence when the weapon native is unavailable.
            }

            bool actorArmed;
            try
            {
                actorArmed = Function.Call<bool>(Hash.IS_PED_ARMED, actor, 7);
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorActorRoleWeapons; Handle=" + actor.Handle
                    + "; Role=" + (actorRole ?? "Actor")
                    + "; Native=IS_PED_ARMED; Result=Unavailable; Exception="
                    + ex.GetType().Name;
                return false;
            }
            if (!string.Equals(actorRole, "Suspect", StringComparison.OrdinalIgnoreCase)
                && actorArmed)
            {
                reason = "Stage=InteriorActorRoleWeapons; Handle=" + actor.Handle
                    + "; Role=" + (actorRole ?? "Actor")
                    + "; SelectedWeaponHash=" + weaponHash
                    + "; Native=IS_PED_ARMED(7); Result=NonSuspectArmedBeforeOffer";
                return false;
            }

            AmbientInteriorAccessRoute doorwayRoute = null;
            string doorwayResult = requireMappedDoorway
                ? "NotResolved" : "NotRequiredForDeadActor";
            float actorToThresholdStraightDistance = 0f;
            if (requireMappedDoorway)
            {
                string doorwayFailure;
                if (!TryResolveInteriorAccessRouteForInterior(
                    actor,
                    scene.LiveInteriorId,
                    scene.LocationId,
                    scene.CatalogInteriorId,
                    false,
                    out doorwayRoute,
                    out doorwayFailure))
                {
                    reason = doorwayFailure != null
                        && doorwayFailure.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase)
                            ? doorwayFailure
                            : "Stage=InteriorDoorwayAccess; Handle=" + actor.Handle
                                + "; Location=" + (scene.LocationId ?? string.Empty)
                                + "; CatalogInteriorId=" + (scene.CatalogInteriorId ?? string.Empty)
                                + "; LiveInteriorId=" + scene.LiveInteriorId
                                + "; Result=NoMappedDoorwayCatalogPair; Detail="
                                + (doorwayFailure ?? string.Empty);
                    return false;
                }

                Entity preflightDoor;
                int preflightDoorHash;
                if (!TryFindThresholdDoor(
                    actor, doorwayRoute, out preflightDoor,
                    out preflightDoorHash, out doorwayFailure))
                {
                    reason = "Stage=InteriorPhysicalDoorPreflight; Handle=" + actor.Handle
                        + "; Location=" + (scene.LocationId ?? string.Empty)
                        + "; CatalogInteriorId=" + (scene.CatalogInteriorId ?? string.Empty)
                        + "; LiveInteriorId=" + scene.LiveInteriorId
                        + "; Result=MappedPhysicalDoorUnavailable; Detail="
                        + (doorwayFailure ?? string.Empty);
                    return false;
                }

                actorToThresholdStraightDistance = position.DistanceTo(doorwayRoute.EntryPosition);
                if (float.IsNaN(actorToThresholdStraightDistance)
                    || float.IsInfinity(actorToThresholdStraightDistance)
                    || actorToThresholdStraightDistance > MaximumInteriorPlayerFallbackDistance)
                {
                    reason = "Stage=InteriorDoorwayAccess; Handle=" + actor.Handle
                        + "; Location=" + (scene.LocationId ?? string.Empty)
                        + "; DoorPair=" + doorwayRoute.Pair.InsidePoint.SourceRecord
                        + "; ActorToThresholdStraightDistance="
                        + actorToThresholdStraightDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; Maximum=" + MaximumInteriorPlayerFallbackDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; Result=ActorOutsideMappedDoorwayRange";
                    return false;
                }
                doorwayResult = "MappedPhysicalDoorPreflightConfirmed:Door="
                    + preflightDoor.Handle + ";Model=" + preflightDoor.Model.Hash
                    + ";DoorHash=" + preflightDoorHash;
            }

            evidence = "Role=" + (actorRole ?? "Actor")
                + "; Handle=" + actor.Handle
                + "; Owner=" + (ownership ?? "Dispatch")
                + "; Model=" + actor.Model.Hash
                + "; WeaponHash=" + weaponHash
                + "; Position=" + DescribePosition(position)
                + "; SceneCenter=" + DescribePosition(scene.SceneCenter)
                + "; SceneCenterDistance=" + sceneCenterDistance.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; EntityLiveInteriorId=" + (entityInteriorAvailable
                    ? entityInteriorId.ToString(CultureInfo.InvariantCulture)
                    : "Unavailable")
                + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                + "; ExpectedLiveInteriorId=" + scene.LiveInteriorId
                + "; GroundZ=" + groundZ.ToString("0.00", CultureInfo.InvariantCulture)
                + "; GroundNormalZ=" + groundNormal.Z.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; FloorDelta=" + floorDelta.ToString("0.00", CultureInfo.InvariantCulture)
                + "; RoomKey=" + (roomKeyAvailable
                    ? roomKey.ToString(CultureInfo.InvariantCulture) : "Unavailable")
                + "; IsArmed=" + actorArmed
                + "; ExpectedRoomKey=NotAuthoredInLocationCatalog"
                + "; DoorwayCatalogAccess=" + doorwayResult
                + "; DoorwayTraversal=NotProbed"
                + (doorwayRoute == null ? string.Empty
                    : "; DoorPair=" + doorwayRoute.Pair.InsidePoint.SourceRecord
                        + "; DoorwayEntry=" + DescribePosition(doorwayRoute.EntryPosition)
                        + "; DoorwayExit=" + DescribePosition(doorwayRoute.DestinationPosition)
                        + "; ActorToThresholdStraightDistance="
                        + actorToThresholdStraightDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture));
            reason = string.Empty;
            return true;
        }

        private static string DescribePosition(Vector3 position)
        {
            return position.X.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + position.Y.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + position.Z.ToString("0.00", CultureInfo.InvariantCulture);
        }

        internal bool TryGetDispatchPlayerInteriorId(
            Ped player,
            out int interiorId)
        {
            return TryGetDispatchPlayerInteriorId(player, out interiorId, true);
        }

        private bool TryGetDispatchPlayerInteriorId(
            Ped player,
            out int interiorId,
            bool logIdentityMismatch)
        {
            interiorId = 0;
            if (player == null || !player.Exists())
                return false;

            int entityInteriorId = 0;
            bool entityInteriorIdAvailable = false;
            try
            {
                entityInteriorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player);
                entityInteriorIdAvailable = true;
            }
            catch
            {
                // Coordinate identity remains usable when the entity native is unavailable.
            }

            int coordinateInteriorId;
            try
            {
                coordinateInteriorId = GetInteriorAt(player.Position);
            }
            catch (Exception ex)
            {
                if (!entityInteriorIdAvailable)
                {
                    if (logIdentityMismatch && _log != null)
                        _log.ClearRuntimeTransition(
                            "AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY");
                    return false;
                }
                interiorId = entityInteriorId;
                if (logIdentityMismatch && _log != null)
                    _log.RuntimeTransition(
                        "AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY",
                        "EntityLiveInteriorId=" + entityInteriorId
                        + "; CoordinateLiveInteriorId=Unavailable"
                        + "; Result=EntityFallback"
                        + "; CoordinateFailure=" + ex.GetType().Name,
                        "Player=" + player.Handle
                        + "; EntityLiveInteriorId=" + entityInteriorId
                        + "; CoordinateLiveInteriorId=Unavailable"
                        + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                        + "; CoordinateResult=Exception:" + ex.GetType().Name
                        + "; SelectedLiveInteriorId=" + interiorId
                        + "; Result=EntityFallback");
                return true;
            }

            // Around a mapped threshold, the entity and coordinate natives can
            // briefly disagree while the Player is already inside. Reuse the
            // coordinate identity only after AmbientWorld has confirmed the
            // physical crossing and the Player remains on the mapped inside side.
            if (entityInteriorIdAvailable)
            {
                if (entityInteriorId == coordinateInteriorId)
                {
                    int confirmedInteriorId;
                    string crossingEvidence;
                    if (entityInteriorId != 0
                        && _confirmedInteriorCrossingsByActor.ContainsKey(player.Handle))
                    {
                        if (TryUseConfirmedInteriorCrossing(
                            player,
                            entityInteriorId,
                            coordinateInteriorId,
                            out confirmedInteriorId,
                            out crossingEvidence))
                        {
                            interiorId = confirmedInteriorId;
                        }
                        else
                        {
                            interiorId = 0;
                        }
                    }
                    else
                    {
                        interiorId = entityInteriorId;
                    }

                    if (logIdentityMismatch && _log != null)
                    {
                        if (interiorId == 0 && entityInteriorId != 0)
                            _log.RuntimeTransition(
                                "AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY",
                                "Player=" + player.Handle
                                    + "|ConfirmedCrossingOutsideFloorEnvelope|"
                                    + entityInteriorId,
                                "Player=" + player.Handle
                                + "; EntityLiveInteriorId=" + entityInteriorId
                                + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                                + "; SelectedLiveInteriorId=0"
                                + "; Result=ConfirmedCrossingFloorEnvelopeRejected");
                        else
                            _log.ClearRuntimeTransition(
                                "AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY");
                    }
                }
                else
                {
                    int confirmedInteriorId;
                    string crossingEvidence;
                    if (TryUseConfirmedInteriorCrossing(
                        player,
                        entityInteriorId,
                        coordinateInteriorId,
                        out confirmedInteriorId,
                        out crossingEvidence))
                    {
                        interiorId = confirmedInteriorId;
                        if (logIdentityMismatch && _log != null)
                            _log.RuntimeTransition(
                                "AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY",
                                "Player=" + player.Handle
                                    + "|ConfirmedCrossingEvidenceFallback|"
                                    + confirmedInteriorId,
                                "Player=" + player.Handle
                                + "; EntityLiveInteriorId=" + entityInteriorId
                                + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                                + "; SelectedLiveInteriorId=" + interiorId
                                + "; Result=ConfirmedCrossingEvidenceFallback"
                                + "; Evidence=" + crossingEvidence);
                    }
                    else
                    {
                        interiorId = 0;
                        if (logIdentityMismatch && _log != null)
                            _log.RuntimeTransition(
                                "AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY",
                                "EntityLiveInteriorId=" + entityInteriorId
                                    + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                                    + "; Result=EntityIdentityRejectedCoordinateFallback",
                                "Player=" + player.Handle
                                + "; EntityLiveInteriorId=" + entityInteriorId
                                + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                                + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                                + "; SelectedLiveInteriorId=" + interiorId
                                + "; Result=EntityIdentityRejectedCoordinateFallback");
                    }
                }
                return true;
            }

            interiorId = coordinateInteriorId;
            if (logIdentityMismatch && _log != null)
                _log.RuntimeTransition(
                    "AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY",
                    "EntityLiveInteriorId=Unavailable"
                    + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                    + "; Result=CoordinateFallbackNativeUnavailable",
                    "Player=" + player.Handle
                    + "; EntityLiveInteriorId=Unavailable"
                    + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                    + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                    + "; SelectedLiveInteriorId=" + interiorId
                    + "; Result=CoordinateFallbackNativeUnavailable");
            return true;
        }

        private bool TryUseConfirmedInteriorCrossing(
            Ped player,
            int entityInteriorId,
            int coordinateInteriorId,
            out int interiorId,
            out string evidence)
        {
            interiorId = 0;
            evidence = string.Empty;
            AmbientConfirmedInteriorCrossing crossing;
            if (player == null || !player.Exists()
                || !_confirmedInteriorCrossingsByActor.TryGetValue(
                    player.Handle, out crossing)
                || crossing == null || crossing.Pair == null
                || crossing.Pair.InsideLocation == null
                || crossing.Pair.InsidePoint == null
                || crossing.Pair.OutsidePoint == null
                || crossing.InteriorId <= 0)
                return false;

            bool bothNativesConfirmInterior = entityInteriorId == crossing.InteriorId
                && coordinateInteriorId == crossing.InteriorId;
            bool oneNativeConfirmsInterior = entityInteriorId == crossing.InteriorId
                || coordinateInteriorId == crossing.InteriorId;
            bool otherNativeIsTemporarilyExterior = entityInteriorId == 0
                || coordinateInteriorId == 0;
            if (!oneNativeConfirmsInterior
                || (!otherNativeIsTemporarilyExterior && !bothNativesConfirmInterior))
                return false;

            float insideDistance = player.Position.DistanceTo(
                crossing.Pair.InsidePoint.Position);
            float outsideDistance = player.Position.DistanceTo(
                crossing.Pair.OutsidePoint.Position);
            float verticalFloorOffset = Math.Abs(
                player.Position.Z - crossing.InteriorFloorPosition.Z);
            if (float.IsNaN(insideDistance) || float.IsInfinity(insideDistance)
                || float.IsNaN(outsideDistance) || float.IsInfinity(outsideDistance)
                || float.IsNaN(verticalFloorOffset) || float.IsInfinity(verticalFloorOffset)
                || verticalFloorOffset > ConfirmedInteriorIdentityMaximumFloorOffset
                || insideDistance > ConfirmedInteriorIdentityMaximumRange
                || insideDistance + 0.25f >= outsideDistance)
                return false;

            interiorId = crossing.InteriorId;
            evidence = "Location=" + crossing.Pair.InsideLocation.Id
                + "; CrossingPosition=" + crossing.CrossingPosition
                + "; ConfirmedAtGameTime=" + crossing.ConfirmedAtGameTime
                + "; CurrentPosition=" + player.Position
                + "; InsideDistance=" + insideDistance.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; OutsideDistance=" + outsideDistance.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; InteriorFloorPosition=" + crossing.InteriorFloorPosition
                + "; VerticalFloorOffset=" + verticalFloorOffset.ToString(
                    "0.00", CultureInfo.InvariantCulture);
            return true;
        }

        private bool TryGetMappedInteriorPairLiveId(
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair,
            int playerLiveInteriorId,
            Vector3 playerPosition,
            out int mappedLiveInteriorId,
            out string nativeResult)
        {
            mappedLiveInteriorId = 0;
            nativeResult = "GET_INTERIOR_AT_COORDS:NotProbed";
            if (pair == null || pair.InsidePoint == null
                || !HasCatalogedMappedInteriorReference(pair)
                || _locationCatalog == null || playerLiveInteriorId == 0)
                return false;

            DateTime now = DateTime.UtcNow;
            PrepareMappedInteriorIdentityCache(playerLiveInteriorId, playerPosition, now);

            if (!_mappedInteriorLiveIdsByPair.TryGetValue(pair, out mappedLiveInteriorId))
            {
                try
                {
                    mappedLiveInteriorId = GetInteriorAt(pair.InsidePoint.Position);
                    nativeResult = "GET_INTERIOR_AT_COORDS:" + mappedLiveInteriorId;
                }
                catch (Exception ex)
                {
                    mappedLiveInteriorId = int.MinValue;
                    nativeResult = "GET_INTERIOR_AT_COORDS:Exception:" + ex.GetType().Name;
                    Log("AMBIENT_WORLD_MAPPED_INTERIOR_IDENTITY_PROBE_FAILED",
                        "Location=" + (pair.InsideLocation == null
                            ? string.Empty : pair.InsideLocation.Id)
                        + "; CatalogInteriorId=" + pair.InteriorId
                        + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                        + "; Native=" + nativeResult);
                }
                _mappedInteriorLiveIdsByPair[pair] = mappedLiveInteriorId;
            }
            else
            {
                nativeResult = "GET_INTERIOR_AT_COORDS:Cached:" + mappedLiveInteriorId
                    + "; CacheAgeMilliseconds="
                    + Math.Max(0d, (now - _mappedInteriorIdentityCacheCapturedAtUtc).TotalMilliseconds)
                        .ToString("0", CultureInfo.InvariantCulture);
            }

            return mappedLiveInteriorId == playerLiveInteriorId;
        }

        private bool TryGetMappedInteriorPointLiveId(
            LSImmersiveLocationCatalog.LSImmersiveLocationPoint point,
            string locationId,
            int playerLiveInteriorId,
            Vector3 playerPosition,
            out int mappedLiveInteriorId,
            out string nativeResult)
        {
            mappedLiveInteriorId = 0;
            nativeResult = "GET_INTERIOR_AT_COORDS:NotProbed";
            if (point == null || !HasObservedNonZeroInterior(point.InteriorId)
                || _locationCatalog == null || playerLiveInteriorId == 0)
                return false;

            DateTime now = DateTime.UtcNow;
            PrepareMappedInteriorIdentityCache(playerLiveInteriorId, playerPosition, now);
            if (!_mappedInteriorLiveIdsByPoint.TryGetValue(point, out mappedLiveInteriorId))
            {
                try
                {
                    mappedLiveInteriorId = GetInteriorAt(point.Position);
                    nativeResult = "GET_INTERIOR_AT_COORDS:" + mappedLiveInteriorId;
                }
                catch (Exception ex)
                {
                    mappedLiveInteriorId = int.MinValue;
                    nativeResult = "GET_INTERIOR_AT_COORDS:Exception:" + ex.GetType().Name;
                    Log("AMBIENT_WORLD_MAPPED_INTERIOR_POINT_IDENTITY_PROBE_FAILED",
                        "Location=" + (locationId ?? string.Empty)
                        + "; CatalogInteriorId=" + point.InteriorId
                        + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                        + "; Native=" + nativeResult);
                }
                _mappedInteriorLiveIdsByPoint[point] = mappedLiveInteriorId;
            }
            else
            {
                nativeResult = "GET_INTERIOR_AT_COORDS:Cached:" + mappedLiveInteriorId
                    + "; CacheAgeMilliseconds="
                    + Math.Max(0d, (now - _mappedInteriorIdentityCacheCapturedAtUtc).TotalMilliseconds)
                        .ToString("0", CultureInfo.InvariantCulture);
            }

            return mappedLiveInteriorId == playerLiveInteriorId;
        }

        private void PrepareMappedInteriorIdentityCache(
            int playerLiveInteriorId,
            Vector3 playerPosition,
            DateTime now)
        {
            bool cacheExpired = _mappedInteriorIdentityCacheCapturedAtUtc != DateTime.MinValue
                && (now - _mappedInteriorIdentityCacheCapturedAtUtc).TotalMilliseconds
                    >= DispatchInteriorIdentityCacheLifetimeMilliseconds;
            bool playerMoved = _mappedInteriorIdentityCacheCapturedAtUtc != DateTime.MinValue
                && _mappedInteriorIdentityCachePlayerPosition.DistanceTo(playerPosition) > 1.5f;
            if (!ReferenceEquals(_mappedInteriorIdentityCacheCatalog, _locationCatalog)
                || _mappedInteriorIdentityCachePlayerLiveId != playerLiveInteriorId
                || _mappedInteriorLiveIdsByPair == null
                || _mappedInteriorLiveIdsByPoint == null
                || cacheExpired
                || playerMoved)
            {
                _mappedInteriorIdentityCacheCatalog = _locationCatalog;
                _mappedInteriorIdentityCachePlayerLiveId = playerLiveInteriorId;
                _mappedInteriorIdentityCacheCapturedAtUtc = now;
                _mappedInteriorIdentityCachePlayerPosition = playerPosition;
                _mappedInteriorLiveIdsByPair = new Dictionary<
                    LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair, int>();
                _mappedInteriorLiveIdsByPoint = new Dictionary<
                    LSImmersiveLocationCatalog.LSImmersiveLocationPoint, int>();
            }
        }

        private int GetInteriorAt(Vector3 position)
        {
            return Function.Call<int>(
                Hash.GET_INTERIOR_AT_COORDS,
                position.X,
                position.Y,
                position.Z);
        }

        private static Vector3 ResolveOutsideInteriorProbe(
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair)
        {
            if (pair == null || pair.OutsidePoint == null || pair.InsidePoint == null)
                return Vector3.Zero;

            Vector3 outside = pair.OutsidePoint.Position;
            Vector3 inside = pair.InsidePoint.Position;
            float deltaX = outside.X - inside.X;
            float deltaY = outside.Y - inside.Y;
            float horizontalLength = (float)Math.Sqrt(
                deltaX * deltaX + deltaY * deltaY);
            if (horizontalLength < 0.05f)
                return outside;

            const float outsideProbeDistance = 1.15f;
            return new Vector3(
                outside.X + deltaX / horizontalLength * outsideProbeDistance,
                outside.Y + deltaY / horizontalLength * outsideProbeDistance,
                outside.Z);
        }

        private bool TryFindThresholdDoor(
            Ped actor,
            AmbientInteriorAccessRoute route,
            out Entity doorEntity,
            out int doorHash,
            out string reason)
        {
            doorEntity = null;
            doorHash = 0;
            reason = string.Empty;

            if (route != null && route.Pair != null
                && route.Pair.InsidePoint != null
                && string.Equals(route.Pair.InsidePoint.Role,
                    "InteriorEgressCandidate", StringComparison.OrdinalIgnoreCase))
            {
                return TryFindMappedEndpointDoor(
                    actor, route, out doorEntity, out doorHash, out reason);
            }

            float[] rayHeights = { 0.82f, 1.08f, 1.42f };
            for (int index = 0; index < rayHeights.Length; index++)
            {
                Vector3 from = route.Pair.OutsidePoint.Position;
                Vector3 to = route.Pair.InsidePoint.Position;
                from.Z += rayHeights[index];
                to.Z += rayHeights[index];

                RaycastResult hit;
                try
                {
                    hit = World.Raycast(
                        from,
                        to,
                        IntersectFlags.Objects | IntersectFlags.Glass,
                        actor);
                }
                catch (Exception ex)
                {
                    reason = "The threshold raycast failed: " + ex.Message;
                    return false;
                }

                if (!hit.DidHit || hit.HitEntity == null
                    || !hit.HitEntity.Exists())
                    continue;

                Entity candidate = hit.HitEntity;
                try
                {
                    int candidateDoorHash;
                    bool registered;
                    string doorEvidence;
                    if (!TryGetOrRegisterMappedPhysicalDoor(
                        candidate, route, InteriorAccessDoorMaxSegmentDistance,
                        out candidateDoorHash, out registered, out doorEvidence))
                        continue;

                    doorEntity = candidate;
                    doorHash = candidateDoorHash;
                    if (registered)
                        LogRegisteredPhysicalDoor(candidate, route, doorHash,
                            doorEvidence);
                    return true;
                }
                catch (Exception ex)
                {
                    reason = "GTA could not identify the hit object as a door: " + ex.Message;
                    return false;
                }
            }

            Vector3 outsidePoint = route.Pair.OutsidePoint.Position;
            Vector3 insidePoint = route.Pair.InsidePoint.Position;
            Vector3 thresholdCenter = new Vector3(
                (outsidePoint.X + insidePoint.X) * 0.5f,
                (outsidePoint.Y + insidePoint.Y) * 0.5f,
                (outsidePoint.Z + insidePoint.Z) * 0.5f);
            Prop[] nearbyProps;
            try
            {
                nearbyProps = World.GetNearbyProps(
                    thresholdCenter,
                    InteriorAccessDoorPropSearchRadius);
            }
            catch (Exception ex)
            {
                reason = "The nearby doorway-object scan failed: " + ex.Message;
                return false;
            }

            if (nearbyProps == null)
                nearbyProps = new Prop[0];

            int registeredDoorCount = 0;
            float nearestRegisteredDoorDistance = float.MaxValue;
            Prop nearestRegisteredDoor = null;
            int nearestRegisteredDoorHash = 0;
            string nearestDoorEvidence = string.Empty;
            string doorLookupFailure = string.Empty;
            for (int index = 0; index < nearbyProps.Length; index++)
            {
                Prop candidate = nearbyProps[index];
                if (candidate == null || !candidate.Exists())
                    continue;

                float distanceToThreshold = DistanceToSegment(
                    candidate.Position,
                    outsidePoint,
                    insidePoint);
                if (distanceToThreshold > InteriorAccessDoorMaxSegmentDistance)
                    continue;

                int candidateDoorHash;
                bool registered;
                string candidateEvidence;
                try
                {
                    if (!TryGetOrRegisterMappedPhysicalDoor(
                        candidate, route, InteriorAccessDoorMaxSegmentDistance,
                        out candidateDoorHash, out registered, out candidateEvidence))
                        continue;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrWhiteSpace(doorLookupFailure))
                        doorLookupFailure = ex.Message;
                    continue;
                }

                registeredDoorCount++;
                if (distanceToThreshold < nearestRegisteredDoorDistance)
                {
                    nearestRegisteredDoorDistance = distanceToThreshold;
                    nearestRegisteredDoor = candidate;
                    nearestRegisteredDoorHash = candidateDoorHash;
                    nearestDoorEvidence = candidateEvidence;
                    if (registered)
                        LogRegisteredPhysicalDoor(candidate, route,
                            candidateDoorHash, candidateEvidence);
                }
            }

            if (nearestRegisteredDoor != null
                && nearestRegisteredDoorDistance <= InteriorAccessDoorMaxSegmentDistance)
            {
                doorEntity = nearestRegisteredDoor;
                doorHash = nearestRegisteredDoorHash;
                Log("AMBIENT_WORLD_INTERIOR_DOOR_FOUND_NEAR_THRESHOLD",
                    "Actor=" + actor.Handle
                    + "; DoorEntity=" + doorEntity.Handle
                    + "; DoorHash=" + doorHash
                    + "; Location=" + route.Pair.InsideLocation.Id
                    + "; NearbyProps=" + nearbyProps.Length
                    + "; RegisteredDoorCandidates=" + registeredDoorCount
                    + "; SegmentDistance=" + nearestRegisteredDoorDistance.ToString("0.00", CultureInfo.InvariantCulture));
                return true;
            }

            reason = "Status=DoorUnresolved; No registered physical door was found across the mapped outside/inside threshold. "
                + "NearbyProps=" + nearbyProps.Length
                + "; RegisteredDoorCandidates=" + registeredDoorCount
                + "; NearestRegisteredDoorDistance="
                + (nearestRegisteredDoor == null
                    ? "none"
                    : nearestRegisteredDoorDistance.ToString("0.00", CultureInfo.InvariantCulture))
                + (string.IsNullOrWhiteSpace(doorLookupFailure)
                    ? string.Empty
                    : "; DoorLookupError=" + doorLookupFailure);
            return false;
        }

        private bool TryFindMappedEndpointDoor(
            Ped actor,
            AmbientInteriorAccessRoute route,
            out Entity doorEntity,
            out int doorHash,
            out string reason)
        {
            doorEntity = null;
            doorHash = 0;
            reason = string.Empty;
            if (actor == null || !actor.Exists() || route == null
                || route.Pair == null || route.Pair.OutsidePoint == null
                || route.Pair.InsidePoint == null
                || route.Pair.InsideLocation == null)
            {
                reason = "The mapped interior door candidate is incomplete.";
                return false;
            }

            Vector3 entranceAnchor = route.EntryPosition;
            Prop[] nearbyProps;
            try
            {
                nearbyProps = World.GetNearbyProps(
                    entranceAnchor,
                    MappedEndpointDoorPropSearchRadius);
            }
            catch (Exception ex)
            {
                reason = "The mapped entrance-object scan failed: " + ex.Message;
                return false;
            }

            if (nearbyProps == null)
                nearbyProps = new Prop[0];

            int registeredDoorCount = 0;
            float nearestRegisteredDoorDistance = float.MaxValue;
            Prop nearestRegisteredDoor = null;
            int nearestRegisteredDoorHash = 0;
            string nearestDoorEvidence = string.Empty;
            string doorLookupFailure = string.Empty;
            for (int index = 0; index < nearbyProps.Length; index++)
            {
                Prop candidate = nearbyProps[index];
                if (candidate == null || !candidate.Exists())
                    continue;

                int candidateDoorHash;
                bool registered;
                string candidateEvidence;
                try
                {
                    if (candidate.Position.DistanceTo(entranceAnchor)
                            > MappedEndpointDoorMaxDistance
                        || !TryGetOrRegisterMappedPhysicalDoor(
                            candidate, route, MappedEndpointDoorMaxDistance,
                            out candidateDoorHash, out registered,
                            out candidateEvidence))
                        continue;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrWhiteSpace(doorLookupFailure))
                        doorLookupFailure = ex.Message;
                    continue;
                }

                registeredDoorCount++;
                float distanceToAnchor = candidate.Position.DistanceTo(entranceAnchor);
                if (distanceToAnchor < nearestRegisteredDoorDistance)
                {
                    nearestRegisteredDoorDistance = distanceToAnchor;
                    nearestRegisteredDoor = candidate;
                    nearestRegisteredDoorHash = candidateDoorHash;
                    nearestDoorEvidence = candidateEvidence;
                    if (registered)
                        LogRegisteredPhysicalDoor(candidate, route,
                            candidateDoorHash, candidateEvidence);
                }
            }

            if (nearestRegisteredDoor != null
                && nearestRegisteredDoorDistance <= MappedEndpointDoorMaxDistance)
            {
                doorEntity = nearestRegisteredDoor;
                doorHash = nearestRegisteredDoorHash;
                Log("AMBIENT_WORLD_MAPPED_DOOR_FOUND_AT_ENDPOINT",
                    "Actor=" + actor.Handle
                    + "; DoorEntity=" + doorEntity.Handle
                    + "; DoorHash=" + doorHash
                    + "; Location=" + route.Pair.InsideLocation.Id
                    + "; CatalogInteriorId=" + route.Pair.InteriorId
                    + "; Endpoint=" + (route.EnteringInterior
                        ? "ExteriorEntrance" : "InteriorEgress")
                    + "; NearbyProps=" + nearbyProps.Length
                    + "; RegisteredDoorCandidates=" + registeredDoorCount
                    + "; EndpointDistance=" + nearestRegisteredDoorDistance.ToString("0.00", CultureInfo.InvariantCulture));
                return true;
            }

            reason = "Status=DoorUnresolved; No registered physical mapped-interior door was found at the entrance/egress anchor. "
                + "NearbyProps=" + nearbyProps.Length
                + "; RegisteredDoorCandidates=" + registeredDoorCount
                + "; NearestRegisteredDoorDistance="
                + (nearestRegisteredDoor == null
                    ? "none"
                    : nearestRegisteredDoorDistance.ToString("0.00", CultureInfo.InvariantCulture))
                + (string.IsNullOrWhiteSpace(doorLookupFailure)
                    ? string.Empty
                    : "; DoorLookupError=" + doorLookupFailure);
            Log("AMBIENT_WORLD_MAPPED_DOOR_NOT_FOUND_AT_ENDPOINT",
                "Actor=" + actor.Handle
                + "; Location=" + route.Pair.InsideLocation.Id
                + "; CatalogInteriorId=" + route.Pair.InteriorId
                + "; Endpoint=" + (route.EnteringInterior
                    ? "ExteriorEntrance" : "InteriorEgress")
                + "; NearbyProps=" + nearbyProps.Length
                + "; RegisteredDoorCandidates=" + registeredDoorCount
                + "; NearestRegisteredDoorDistance="
                + (nearestRegisteredDoor == null
                    ? "none"
                    : nearestRegisteredDoorDistance.ToString("0.00", CultureInfo.InvariantCulture))
                + "; Reason=" + reason);
            return false;
        }

        private static bool TryGetOrRegisterMappedPhysicalDoor(
            Entity candidate,
            AmbientInteriorAccessRoute route,
            float maximumThresholdDistance,
            out int doorHash,
            out bool registered,
            out string evidence)
        {
            doorHash = 0;
            registered = false;
            evidence = string.Empty;
            if (candidate == null || !candidate.Exists() || route == null
                || route.Pair == null || route.Pair.OutsidePoint == null
                || route.Pair.InsidePoint == null
                || Function.Call<int>(Hash.GET_ENTITY_TYPE, candidate) != 3)
            {
                evidence = "Result=NotAPropOrMappedThreshold";
                return false;
            }

            try
            {
                if (TryGetExistingDoorHash(candidate, out doorHash))
                {
                    evidence = "Registration=ExistingDoorSystemEntry"
                        + "; Entity=" + candidate.Handle
                        + "; Model=" + candidate.Model.Hash
                        + "; Position=" + DescribePosition(candidate.Position);
                    return true;
                }
            }
            catch (Exception ex)
            {
                evidence = "ExistingDoorLookup=" + ex.GetType().Name;
            }

            Vector3 position = candidate.Position;
            float segmentDistance = DistanceToSegment(
                position,
                route.Pair.OutsidePoint.Position,
                route.Pair.InsidePoint.Position);
            if (float.IsNaN(segmentDistance) || float.IsInfinity(segmentDistance)
                || segmentDistance > maximumThresholdDistance)
            {
                evidence = "Result=PropOutsideMappedDoorwayThreshold"
                    + "; SegmentDistance="
                    + segmentDistance.ToString("0.00", CultureInfo.InvariantCulture);
                return false;
            }

            Vector3 minimum;
            Vector3 maximum;
            try
            {
                using (var minimumOutput = new OutputArgument())
                using (var maximumOutput = new OutputArgument())
                {
                    Function.Call(Hash.GET_MODEL_DIMENSIONS,
                        candidate.Model.Hash, minimumOutput, maximumOutput);
                    minimum = minimumOutput.GetResult<Vector3>();
                    maximum = maximumOutput.GetResult<Vector3>();
                }
            }
            catch (Exception ex)
            {
                evidence = "Result=DoorPropDimensionsUnavailable"
                    + "; Exception=" + ex.GetType().Name;
                return false;
            }

            float width = Math.Abs(maximum.X - minimum.X);
            float depth = Math.Abs(maximum.Y - minimum.Y);
            float height = Math.Abs(maximum.Z - minimum.Z);
            float narrowDimension = Math.Min(width, depth);
            float wideDimension = Math.Max(width, depth);
            if (float.IsNaN(width) || float.IsInfinity(width)
                || float.IsNaN(depth) || float.IsInfinity(depth)
                || float.IsNaN(height) || float.IsInfinity(height)
                || height < 1.45f || height > 5.5f
                || narrowDimension < 0.08f || wideDimension > 5.0f)
            {
                evidence = "Result=PropDimensionsNotDoorLike"
                    + "; Width=" + width.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Depth=" + depth.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Height=" + height.ToString("0.00", CultureInfo.InvariantCulture);
                return false;
            }

            int x = (int)Math.Round(position.X * 10.0f);
            int y = (int)Math.Round(position.Y * 10.0f);
            int z = (int)Math.Round(position.Z * 10.0f);
            string doorIdentity = "LSIL_MAPPED_DOOR_" + candidate.Model.Hash.ToString(
                "X8", CultureInfo.InvariantCulture) + "_" + x.ToString(
                CultureInfo.InvariantCulture) + "_" + y.ToString(
                CultureInfo.InvariantCulture) + "_" + z.ToString(
                CultureInfo.InvariantCulture);
            int generatedDoorHash = unchecked((int)StringHash.AtStringHash(
                doorIdentity, 0));
            if (generatedDoorHash == 0)
                generatedDoorHash = unchecked(candidate.Model.Hash ^ 0x4C53494C);
            if (generatedDoorHash == 0)
                generatedDoorHash = int.MinValue + 1;

            try
            {
                // Register only the actual prop intersecting this authored
                // doorway. The model, transform, and tight door-like bounds
                // came from the live entity; no synthetic door position is used.
                Function.Call((Hash)AddDoorToSystemNativeHash,
                    generatedDoorHash, candidate.Model.Hash,
                    position.X, position.Y, position.Z,
                    true, false, false);
                if (!TryGetExistingDoorHash(candidate, out doorHash))
                {
                    evidence = "Result=DoorSystemRegistrationNotResolvable"
                        + "; Entity=" + candidate.Handle
                        + "; Model=" + candidate.Model.Hash
                        + "; Position=" + DescribePosition(position)
                        + "; RequestedDoorHash=" + generatedDoorHash;
                    return false;
                }
                registered = true;
                evidence = "Registration=ADD_DOOR_TO_SYSTEM"
                    + "; Entity=" + candidate.Handle
                    + "; Model=" + candidate.Model.Hash
                    + "; Position=" + DescribePosition(position)
                    + "; RequestedDoorHash=" + generatedDoorHash
                    + "; ResolvedDoorHash=" + doorHash
                    + "; SegmentDistance="
                    + segmentDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Bounds=" + width.ToString("0.00", CultureInfo.InvariantCulture)
                    + "x" + depth.ToString("0.00", CultureInfo.InvariantCulture)
                    + "x" + height.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                evidence = "Result=DoorSystemRegistrationFailed"
                    + "; Entity=" + candidate.Handle
                    + "; Model=" + candidate.Model.Hash
                    + "; Position=" + DescribePosition(position)
                    + "; Exception=" + ex.GetType().Name;
                return false;
            }
        }

        private void LogRegisteredPhysicalDoor(
            Entity doorEntity,
            AmbientInteriorAccessRoute route,
            int doorHash,
            string evidence)
        {
            Log("AMBIENT_WORLD_MAPPED_PHYSICAL_DOOR_REGISTERED",
                "DoorEntity=" + (doorEntity == null || !doorEntity.Exists()
                    ? 0 : doorEntity.Handle)
                + "; Model=" + (doorEntity == null || !doorEntity.Exists()
                    ? 0 : doorEntity.Model.Hash)
                + "; Position=" + (doorEntity == null || !doorEntity.Exists()
                    ? "Unavailable" : DescribePosition(doorEntity.Position))
                + "; DoorHash=" + doorHash
                + "; Location=" + (route == null || route.Pair == null
                    || route.Pair.InsideLocation == null
                    ? string.Empty : route.Pair.InsideLocation.Id)
                + "; Outside=" + (route == null || route.Pair == null
                    || route.Pair.OutsidePoint == null
                    ? "Unavailable" : DescribePosition(route.Pair.OutsidePoint.Position))
                + "; Inside=" + (route == null || route.Pair == null
                    || route.Pair.InsidePoint == null
                    ? "Unavailable" : DescribePosition(route.Pair.InsidePoint.Position))
                + "; Evidence=" + (evidence ?? string.Empty));
        }

        private static bool TryGetExistingDoorHash(Entity candidate, out int doorHash)
        {
            doorHash = 0;
            if (candidate == null || !candidate.Exists()
                || Function.Call<int>(Hash.GET_ENTITY_TYPE, candidate) != 3)
                return false;

            bool found;
            using (var doorOutput = new OutputArgument())
            {
                found = Function.Call<bool>(
                    Hash.DOOR_SYSTEM_FIND_EXISTING_DOOR,
                    candidate.Position.X,
                    candidate.Position.Y,
                    candidate.Position.Z,
                    candidate.Model.Hash,
                    doorOutput);
                doorHash = doorOutput.GetResult<int>();
            }
            return found && doorHash != 0;
        }

        private static float DistanceToSegment(
            Vector3 point,
            Vector3 segmentStart,
            Vector3 segmentEnd)
        {
            float segmentX = segmentEnd.X - segmentStart.X;
            float segmentY = segmentEnd.Y - segmentStart.Y;
            float segmentZ = segmentEnd.Z - segmentStart.Z;
            float segmentLengthSquared = segmentX * segmentX
                + segmentY * segmentY + segmentZ * segmentZ;
            float projection = segmentLengthSquared <= 0.0001f
                ? 0f
                : ((point.X - segmentStart.X) * segmentX
                    + (point.Y - segmentStart.Y) * segmentY
                    + (point.Z - segmentStart.Z) * segmentZ)
                    / segmentLengthSquared;
            projection = Math.Max(0f, Math.Min(1f, projection));

            Vector3 closestPoint = new Vector3(
                segmentStart.X + projection * segmentX,
                segmentStart.Y + projection * segmentY,
                segmentStart.Z + projection * segmentZ);
            return point.DistanceTo(closestPoint);
        }

        private void ReleaseInteriorAccessSession(int actorHandle, string reason)
        {
            AmbientInteriorAccessSession session;
            if (!_interiorAccessSessions.TryGetValue(actorHandle, out session))
                return;
            _interiorAccessSessions.Remove(actorHandle);

            bool anotherActorUsesDoor = _interiorAccessSessions.Values.Any(existing =>
                existing.DoorHash == session.DoorHash);
            if (!anotherActorUsesDoor)
            {
                try
                {
                    Function.Call(Hash.DOOR_SYSTEM_SET_HOLD_OPEN, session.DoorHash, false);
                    Function.Call(
                        Hash.DOOR_SYSTEM_SET_OPEN_RATIO,
                        session.DoorHash,
                        session.OriginalOpenRatio,
                        true,
                        false);
                    if (session.OriginalDoorState != UnlockedDoorSystemState)
                    {
                        Function.Call(
                            Hash.DOOR_SYSTEM_SET_DOOR_STATE,
                            session.DoorHash,
                            session.OriginalDoorState,
                            true,
                            true);
                    }
                    Log("AMBIENT_WORLD_INTERIOR_DOOR_RESTORED",
                        "Actor=" + actorHandle
                        + "; DoorHash=" + session.DoorHash
                        + "; DoorState=" + session.OriginalDoorState
                        + "; OpenRatio=" + session.OriginalOpenRatio.ToString("0.00", CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    Log("AMBIENT_WORLD_INTERIOR_DOOR_RESTORE_FAILED",
                        "Actor=" + actorHandle
                        + "; DoorHash=" + session.DoorHash
                        + "; Reason=" + ex.Message);
                }
            }

            Log("AMBIENT_WORLD_INTERIOR_ACCESS_SESSION_ENDED",
                "Actor=" + actorHandle
                + "; DoorHash=" + session.DoorHash
                + "; Result=" + reason);
        }

        /// <summary>
        /// Resolves an event's broad area references to one surveyed foot-activity
        /// candidate and validates it against the currently streamed Enhanced world.
        /// The result is a plan only: this method never creates or adopts an actor.
        /// </summary>
        internal bool TryResolveDispatchScene(
            IEnumerable<LSPDDispatchLocationDefinition> eventAreas,
            string incidentType,
            Vector3 playerPosition,
            bool requiresVehicle,
            out AmbientDispatchSceneResolution scene,
            out string reason,
            Vector3 playerRadiusRoadAnchor,
            string requiredInteriorLocationId = null,
            string requiredPhysicalType = null,
            string requiredLocationContext = null,
            bool requiresMappedInterior = false,
            int playerLiveInteriorId = 0,
            bool playerLiveInteriorIdAvailable = false,
            string vehicleActivity = "none",
            bool allowPlayerRadiusOutdoorScene = false,
            ISet<string> excludedLocationIds = null,
            Vector3? pendingRoadsideCandidatePosition = null,
            string definitionId = null,
            bool requiresExteriorScene = false)
        {
            scene = null;
            reason = string.Empty;
            int resolvedPlayerInteriorId = playerLiveInteriorIdAvailable
                ? playerLiveInteriorId : GetInteriorAt(playerPosition);
            if (requiresExteriorScene && resolvedPlayerInteriorId != 0)
            {
                reason = "Stage=ExteriorSceneContext; Result=PlayerInsideInterior";
                return false;
            }

            List<LSPDDispatchLocationDefinition> registeredAreas = (eventAreas
                ?? Enumerable.Empty<LSPDDispatchLocationDefinition>())
                .Where(area => area != null)
                .GroupBy(area => area.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            List<LSPDDispatchLocationDefinition> eligibleAreas = registeredAreas
                .Where(area => area.IsEligibleFor(playerPosition))
                .ToList();
            bool movingRoadVehicleScene = string.Equals(
                vehicleActivity, "moving_road", StringComparison.OrdinalIgnoreCase);
            if (movingRoadVehicleScene && !requiresVehicle)
            {
                reason = "Stage=MovingRoadSceneRequirements; RequiresVehicle=false; Result=Rejected";
                return false;
            }
            bool playerRadiusVehicleActivity = requiresVehicle
                && (string.Equals(vehicleActivity, "moving_road",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(vehicleActivity, "parked_theft",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(vehicleActivity, "carjacking",
                        StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(requiredLocationContext)
                    || string.Equals(requiredLocationContext, "any",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(requiredLocationContext, "road_vehicle",
                        StringComparison.OrdinalIgnoreCase));
            bool playerRadiusRoadFootActivity = !requiresVehicle
                && string.Equals(vehicleActivity, "none",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(requiredLocationContext, "road_foot",
                    StringComparison.OrdinalIgnoreCase);
            bool playerRadiusGenericFootActivity = !requiresVehicle
                && !string.IsNullOrWhiteSpace(requiredLocationContext)
                && string.Equals(requiredLocationContext, "any",
                    StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(vehicleActivity)
                    || string.Equals(vehicleActivity, "none",
                        StringComparison.OrdinalIgnoreCase));
            if (allowPlayerRadiusOutdoorScene
                && resolvedPlayerInteriorId == 0
                && !requiresMappedInterior
                && string.IsNullOrWhiteSpace(requiredInteriorLocationId)
                && string.IsNullOrWhiteSpace(requiredPhysicalType)
                && (playerRadiusVehicleActivity || playerRadiusRoadFootActivity
                    || playerRadiusGenericFootActivity))
            {
                if (playerRadiusVehicleActivity)
                    return TryResolvePlayerRadiusVehicleScene(
                        incidentType,
                        vehicleActivity,
                        playerPosition,
                        playerRadiusRoadAnchor,
                        out scene,
                        out reason,
                        pendingRoadsideCandidatePosition);
                if (playerRadiusRoadFootActivity)
                    return TryResolvePlayerRadiusRoadFootScene(
                        incidentType,
                        playerPosition,
                        playerRadiusRoadAnchor,
                        out scene,
                        out reason);
                return TryResolvePlayerRadiusOutdoorFootScene(
                    incidentType,
                    playerPosition,
                    out scene,
                    out reason);
            }
            if (_locationCatalog == null)
            {
                reason = "The existing LSImmersiveLocation catalog is unavailable.";
                return false;
            }

            // Exterior dispatch can resolve directly from any compatible
            // catalog site near the Player. This runs before geographic anchor
            // fallback; named interiors remain bound to their authored access
            // pair and are never inferred from an exterior point.
            bool useNearbyCatalogFallback = string.IsNullOrWhiteSpace(requiredInteriorLocationId);

            if (!string.IsNullOrWhiteSpace(requiredInteriorLocationId))
            {
                var namedInteriorAreas = new List<LSPDDispatchLocationDefinition>(eligibleAreas);
                int namedMappedPairCount = 0;
                int namedLiveIdentityMatches = 0;
                int firstNamedMappedLiveId = int.MinValue;
                string firstNamedMappedNativeResult = string.Empty;
                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                    in GetInteriorAccessPairs())
                {
                    if (pair == null || pair.InsideLocation == null
                        || pair.OutsidePoint == null || pair.InsidePoint == null
                        || !string.Equals(pair.InsideLocation.Id, requiredInteriorLocationId,
                            StringComparison.OrdinalIgnoreCase)
                        || IsRejectedSurveyRecord(pair.InsideLocation)
                        || !MatchesRequiredPhysicalType(pair.InsideLocation, requiredPhysicalType)
                        || !IsCompatibleWithMappedInteriorIncident(
                            pair.InsideLocation, incidentType, requiredPhysicalType)
                        || !MatchesMappedInteriorDispatchLocationContextMetadata(
                            pair.InsideLocation, requiredLocationContext))
                        continue;
                    namedMappedPairCount++;

                    int pairLiveInteriorId = 0;
                    string pairIdentityResult = "GET_INTERIOR_AT_COORDS:NotProbed";
                    bool pairMatchesActiveInterior = resolvedPlayerInteriorId != 0
                        && TryGetMappedInteriorPairLiveId(
                            pair,
                            resolvedPlayerInteriorId,
                            playerPosition,
                            out pairLiveInteriorId,
                            out pairIdentityResult);
                    if (resolvedPlayerInteriorId != 0)
                    {
                        if (firstNamedMappedLiveId == int.MinValue)
                        {
                            firstNamedMappedLiveId = pairLiveInteriorId;
                            firstNamedMappedNativeResult = pairIdentityResult;
                        }
                        if (pairMatchesActiveInterior)
                            namedLiveIdentityMatches++;
                    }
                    if (resolvedPlayerInteriorId != 0 && !pairMatchesActiveInterior)
                        continue;
                    float nearbyInteriorDistance = Math.Min(
                        Distance2D(pair.OutsidePoint.Position, playerPosition),
                        Distance2D(pair.InsidePoint.Position, playerPosition));
                    if (!pairMatchesActiveInterior
                        && nearbyInteriorDistance
                            > LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance)
                        continue;

                    bool hasAuthoredAreaForThreshold = eligibleAreas.Any(area =>
                        IsWithinPlayerDistance(area, pair.InsidePoint.Position, playerPosition));
                    if (pairMatchesActiveInterior || !hasAuthoredAreaForThreshold)
                    {
                        // A named interior is intentionally requestable from
                        // its mapped doorway or from the exact live interior
                        // already occupied by the Player. The runtime identity
                        // match avoids a false coordinate-distance rejection;
                        // doorway, floor, and navigation validation still run.
                        namedInteriorAreas.Add(
                            LSPDDispatchLocationDefinition.ForNearbyCatalogCandidate(
                                requiredInteriorLocationId,
                                pair.InsidePoint.Position,
                                requiredLocationContext));
                    }
                }

                if (namedInteriorAreas.Count == 0
                    || (resolvedPlayerInteriorId != 0
                        && namedLiveIdentityMatches == 0))
                {
                    string namedFailure = "Stage=CatalogAreaEligibility; FirstRejectedGate="
                        + (namedMappedPairCount == 0
                            ? "MappedPairCompatibility"
                            : resolvedPlayerInteriorId != 0 && namedLiveIdentityMatches == 0
                                ? "LiveInteriorIdentity" : "PlayerDistance")
                        + "; Location=" + requiredInteriorLocationId
                        + "; PlayerLiveInteriorId=" + resolvedPlayerInteriorId
                        + "; NamedMappedPairs=" + namedMappedPairCount
                        + "; LiveIdentityMatches=" + namedLiveIdentityMatches
                        + "; FirstMappedInsideLiveInteriorId="
                        + (firstNamedMappedLiveId == int.MinValue
                            ? "NotProbed" : firstNamedMappedLiveId.ToString(CultureInfo.InvariantCulture))
                        + "; FirstMappedInsideNativeResult="
                        + (firstNamedMappedNativeResult ?? string.Empty)
                        + "; Result=NoCompatibleNamedThresholdWithinRange";
                    string insideOnlyFailure = string.Empty;
                    if (resolvedPlayerInteriorId != 0 && !requiresVehicle
                        && TryResolveInsideOnlyInteriorSceneFromCatalog(
                            incidentType,
                            requiredInteriorLocationId,
                            requiredPhysicalType,
                            requiredLocationContext,
                            playerPosition,
                            resolvedPlayerInteriorId,
                            false,
                            null,
                            out scene,
                            out insideOnlyFailure,
                            excludedLocationIds))
                    {
                        LogInsideOnlyInteriorSceneSelected(scene, "NamedInteriorFallback");
                        RememberLocation(scene.LocationId);
                        return true;
                    }
                    if (insideOnlyFailure.StartsWith(
                        "PENDING:", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = insideOnlyFailure;
                        return false;
                    }
                    reason = namedFailure
                        + (string.IsNullOrWhiteSpace(insideOnlyFailure)
                            ? string.Empty : "; InsideOnlyFallback=" + insideOnlyFailure);
                    return false;
                }

                if (TryResolveNamedInteriorDispatchScene(
                    namedInteriorAreas,
                    incidentType,
                    playerPosition,
                    requiredInteriorLocationId,
                    requiresVehicle,
                    false,
                    out scene,
                    out reason,
                    requiredPhysicalType,
                    excludedLocationIds))
                    return true;

                string namedPointFailure = string.Empty;
                if (resolvedPlayerInteriorId != 0 && !requiresVehicle
                    && TryResolveInsideOnlyInteriorSceneFromCatalog(
                        incidentType,
                        requiredInteriorLocationId,
                        requiredPhysicalType,
                        requiredLocationContext,
                        playerPosition,
                        resolvedPlayerInteriorId,
                        false,
                        null,
                        out scene,
                        out namedPointFailure,
                        excludedLocationIds))
                {
                    LogInsideOnlyInteriorSceneSelected(scene, "NamedInteriorFallbackAfterPairValidation");
                    RememberLocation(scene.LocationId);
                    return true;
                }
                if (namedPointFailure.StartsWith(
                    "PENDING:", StringComparison.OrdinalIgnoreCase))
                {
                    reason = namedPointFailure;
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(namedPointFailure))
                    reason += "; InsideOnlyFallback=" + namedPointFailure;
                return false;
            }

            if (requiresMappedInterior)
            {
                return TryResolveMappedInteriorDispatchScene(
                    incidentType,
                    playerPosition,
                    requiresVehicle,
                    requiredPhysicalType,
                    requiredLocationContext,
                    resolvedPlayerInteriorId,
                    out scene,
                    out reason,
                    excludedLocationIds);
            }

            // When the Player is already inside a matched location, resolve a
            // scene against that exact interior before considering exterior
            // survey points. The Player crosses the door themselves; this only
            // validates the floor and supplies a placement plan for Dispatch.
            int playerInteriorId = resolvedPlayerInteriorId;
            bool mayUseCurrentInterior = playerInteriorId != 0
                && (string.IsNullOrWhiteSpace(requiredPhysicalType)
                    || !string.IsNullOrWhiteSpace(requiredInteriorLocationId));
            if (mayUseCurrentInterior)
            {
                AmbientDispatchSceneResolution interiorScene;
                string interiorFailure;
                if (TryResolveInteriorDispatchScene(
                    eligibleAreas,
                    incidentType,
                    playerPosition,
                    playerInteriorId,
                    requiresVehicle,
                    null,
                    false,
                    false,
                    out interiorScene,
                    out interiorFailure,
                    requiredPhysicalType,
                    requiredLocationContext,
                    false,
                    false,
                    excludedLocationIds))
                {
                    scene = interiorScene;
                    RememberLocation(interiorScene.LocationId);
                    LogLocationResolution(scene, requiresVehicle);
                    return true;
                }
                Log("AMBIENT_WORLD_INTERIOR_SCENE_REJECTED",
                    "InteriorId=" + playerInteriorId
                    + "; IncidentType=" + (incidentType ?? string.Empty)
                    + "; Reason=" + interiorFailure);
            }

            var candidates = new List<DispatchLocationCandidate>();
            var nearbyFallbackCandidates = new List<DispatchLocationCandidate>();
            int entranceReferenceCandidatesAvailable = 0;
            string firstCandidateFailure = string.Empty;
            var candidateFailureStageCounts = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var candidateFailureExamples = new List<string>();
            int candidatePlacementAttemptCount = 0;
            IEnumerable<LSImmersiveLocationCatalog.LSImmersiveLocationDefinition> dispatchCatalogLocations =
                _locationCatalog.FindByCategory("SurveyedCandidate");
            if (useNearbyCatalogFallback)
                dispatchCatalogLocations = dispatchCatalogLocations.Concat(
                    _locationCatalog.FindByCategory("AreaCandidate"));

            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location
                in dispatchCatalogLocations)
            {
                if ((excludedLocationIds != null
                        && excludedLocationIds.Contains(location.Id))
                    || IsRejectedSurveyRecord(location)
                    || !IsCompatibleWithIncident(location, incidentType)
                    || (requiresVehicle
                        && string.Equals(location.Category, "SurveyedCandidate",
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(location.IntendedUse, "FootActivityCandidate",
                            StringComparison.OrdinalIgnoreCase))
                    || !MatchesRequiredPhysicalType(location, requiredPhysicalType))
                    continue;

                bool surveyedLocation = string.Equals(
                    location.Category, "SurveyedCandidate", StringComparison.OrdinalIgnoreCase);
                string pointRole = surveyedLocation
                    ? "ObservedCoordinateCandidate" : "AreaSearchAnchor";
                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationPoint point
                    in location.FindPoints(pointRole, false))
                {
                    if (HasObservedNonZeroInterior(point.InteriorId))
                        continue;
                    // Owner-confirmed entrance coordinates identify a location,
                    // not a verified actor floor. Keep the site eligible, but
                    // require TryBuildDispatchScene's live navigation, ground,
                    // street-height, and clearance probes before using it.
                    if (IsExteriorEntranceOrThresholdReference(point))
                        entranceReferenceCandidatesAvailable++;

                    float playerPointDistance = Distance2D(point.Position, playerPosition);
                    bool nearbyContextMatch = useNearbyCatalogFallback
                        && playerPointDistance <= LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance
                        && MatchesDispatchLocationContextMetadata(location, requiredLocationContext);

                    LSPDDispatchLocationDefinition nearestArea = null;
                    float nearestAreaDistance = float.MaxValue;
                    foreach (LSPDDispatchLocationDefinition area in registeredAreas)
                    {
                        float distance = Distance2D(point.Position, area.Position);
                        if (distance <= SurveyPointAreaRadius
                            && area.IsEligibleFor(playerPosition)
                            && IsWithinPlayerDistance(area, point.Position, playerPosition)
                            && distance < nearestAreaDistance)
                        {
                            nearestArea = area;
                            nearestAreaDistance = distance;
                        }

                    }

                    if (nearestArea != null)
                    {
                        candidates.Add(new DispatchLocationCandidate
                        {
                            Location = location,
                            Point = point,
                            Area = nearestArea,
                            AreaDistance = nearestAreaDistance
                        });
                    }

                    if (nearbyContextMatch)
                    {
                        nearbyFallbackCandidates.Add(new DispatchLocationCandidate
                        {
                            Location = location,
                            Point = point,
                            Area = LSPDDispatchLocationDefinition.ForNearbyCatalogCandidate(
                                location.Id,
                                point.Position,
                                requiredLocationContext),
                            AreaDistance = playerPointDistance
                        });
                    }
                }
            }

            int checkedNearbyFallbackCandidates = 0;
            if (useNearbyCatalogFallback)
            {
                // Keep a bounded pass resumable. Exhausting this update's native
                // probe budget is not evidence that the remaining sites failed.
                DispatchFootSearchProgress footSearch = null;
                if (!requiresVehicle && !string.IsNullOrWhiteSpace(definitionId))
                {
                    DateTime searchNow = DateTime.UtcNow;
                    foreach (string expired in _dispatchFootSearches.Where(pair =>
                        searchNow >= pair.Value.ExpiresAt).Select(pair => pair.Key).ToList())
                        _dispatchFootSearches.Remove(expired);
                    if (!_dispatchFootSearches.TryGetValue(definitionId, out footSearch)
                        || Distance2D(footSearch.PlayerPosition, playerPosition) >= 20f)
                    {
                        if (_dispatchFootSearches.Count >= 128)
                            _dispatchFootSearches.Clear();
                        footSearch = new DispatchFootSearchProgress
                        {
                            PlayerPosition = playerPosition,
                            ExpiresAt = searchNow.AddSeconds(90)
                        };
                        _dispatchFootSearches[definitionId] = footSearch;
                    }
                }
                List<DispatchLocationCandidate> fallbackCandidates = nearbyFallbackCandidates
                    .Where(candidate => !_recentLocationIdSet.Contains(candidate.Location.Id))
                    .ToList();
                if (fallbackCandidates.Count == 0)
                    fallbackCandidates = nearbyFallbackCandidates;
                if (movingRoadVehicleScene)
                    fallbackCandidates = fallbackCandidates
                        .OrderBy(candidate => Distance2D(
                            candidate.Point.Position, playerPosition))
                        .ToList();
                else
                    Shuffle(fallbackCandidates);

                // Recent resolution is a variety preference, not proof that a
                // different site will work. Retain eligible recent foot sites
                // after fresh candidates, before abandoning the nearby catalog.
                // Dispatch's explicit repeat/failure exclusions were applied above.
                if (!requiresVehicle && fallbackCandidates.Count < nearbyFallbackCandidates.Count)
                {
                    List<DispatchLocationCandidate> recentFootCandidates = nearbyFallbackCandidates
                        .Where(candidate => _recentLocationIdSet.Contains(candidate.Location.Id)
                            && IsFootActivityOnlySurveyCandidate(
                                candidate.Location.Id, candidate.Location.Category))
                        .ToList();
                    Shuffle(recentFootCandidates);
                    fallbackCandidates.AddRange(recentFootCandidates);
                }

                foreach (DispatchLocationCandidate candidate in fallbackCandidates)
                {
                    string candidateKey = candidate.Location.Id + "|" + candidate.Point.Id;
                    if (footSearch != null && footSearch.Attempted.Contains(candidateKey))
                        continue;
                    if (footSearch != null
                        ? !footSearch.TryBeginProbe(candidateKey, ref checkedNearbyFallbackCandidates,
                            MaximumSurveyCandidatesToValidate)
                        : checkedNearbyFallbackCandidates++ >= (movingRoadVehicleScene
                            ? MaximumMovingRoadCandidateAnchorsToValidate
                            : MaximumSurveyCandidatesToValidate))
                    {
                        if (footSearch != null)
                        {
                            reason = "PENDING:Stage=NearbyFootCatalogBudget; Definition="
                                + definitionId + "; Attempted=" + footSearch.Attempted.Count
                                + "; Candidates=" + fallbackCandidates.Count
                                + "; Result=UnattemptedCandidatesRemain";
                            return false;
                        }
                        break;
                    }
                    if (!MatchesDispatchLocationContext(
                        candidate.Location,
                        requiredLocationContext,
                        candidate.Point.Position))
                        continue;

                    candidatePlacementAttemptCount++;
                    bool surveyedPoint = string.Equals(
                        candidate.Point.Role, "ObservedCoordinateCandidate",
                        StringComparison.OrdinalIgnoreCase);

                    AmbientDispatchSceneResolution resolved;
                    string candidateFailure;
                    if (!TryBuildDispatchScene(
                        candidate.Area,
                        candidate.Location.Id,
                        candidate.Location.Name,
                        candidate.Location.Category,
                        candidate.Point.Id,
                        candidate.Point.SourceRecord,
                        candidate.Point.Position,
                        candidate.Point.HasHeading ? candidate.Point.Heading : 0f,
                        surveyedPoint,
                        surveyedPoint
                            ? MaximumSurveyPedSnapDistance
                            : MaximumAreaAnchorSnapDistance,
                        playerPosition,
                        false,
                        requiresVehicle,
                        movingRoadVehicleScene,
                        GetDispatchExteriorRoadHeightTolerance(candidate.Area),
                        out resolved,
                        out candidateFailure)
                        || resolved == null
                        || Distance2D(resolved.SceneCenter, playerPosition)
                            > LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance)
                    {
                        string recordedFailure = string.IsNullOrWhiteSpace(candidateFailure)
                            ? (resolved == null
                                ? "Stage=SceneResolution; Result=NoResolution"
                                : "Stage=PlayerDistance; Result=ResolvedOutsideNearbyWindow")
                            : candidateFailure;
                        RecordDispatchLocationCandidateFailure(
                            candidate.Location.Id,
                            candidate.Point.Id,
                            recordedFailure,
                            candidateFailureStageCounts,
                            candidateFailureExamples);
                        if (string.IsNullOrWhiteSpace(firstCandidateFailure))
                            firstCandidateFailure = "Location=" + candidate.Location.Id
                                + "; Point=" + candidate.Point.Id
                                + "; " + candidateFailure;
                        continue;
                    }

                    scene = resolved;
                    if (footSearch != null)
                        _dispatchFootSearches.Remove(definitionId);
                    scene.LocationReviewStatus = candidate.Location.ReviewStatus;
                    scene.Area = LSPDDispatchLocationDefinition.ForNearbyCatalogCandidate(
                        candidate.Location.Id,
                        resolved.SceneCenter,
                        requiredLocationContext);
                    scene.AreaLocationId = candidate.Location.Id;
                    scene.AreaAnchorPosition = resolved.SceneCenter;
                    RememberLocation(candidate.Location.Id);
                    LogDispatchCandidateFailureSummaryIfAny(
                        incidentType,
                        "NearbyCatalogSelected",
                        candidate.Location.Id,
                        candidate.Point.Id,
                        candidatePlacementAttemptCount,
                        candidateFailureStageCounts,
                        candidateFailureExamples);
                    Log("AMBIENT_WORLD_NEARBY_DISPATCH_CATALOG_FALLBACK",
                        "IncidentType=" + (incidentType ?? string.Empty)
                        + "; Location=" + candidate.Location.Id
                        + "; Point=" + candidate.Point.Id
                        + "; PlayerDistance=" + Distance2D(scene.SceneCenter, playerPosition).ToString("0.0")
                        + "; MaximumPlayerDistance=" + LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance
                        + "; ReviewStatus=" + (candidate.Location.ReviewStatus ?? "Unknown"));
                    LogLocationResolution(scene, requiresVehicle);
                    return true;
                }

                if (footSearch != null)
                    _dispatchFootSearches.Remove(definitionId);
                Log("AMBIENT_WORLD_NEARBY_DISPATCH_CATALOG_FALLBACK_UNAVAILABLE",
                    "IncidentType=" + (incidentType ?? string.Empty)
                    + "; CompatibleCatalogPointCount=" + nearbyFallbackCandidates.Count
                    + "; MaximumPlayerDistance=" + LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance
                    + "; EntranceReferenceCandidatesAvailable=" + entranceReferenceCandidatesAvailable
                    + "; FirstPlacementFailure=" + (firstCandidateFailure.Length == 0
                        ? "None" : firstCandidateFailure)
                    + "; Reason=NoNearCatalogPointPassedRuntimeValidation");
            }

            if (eligibleAreas.Count == 0)
            {
                reason = "No compatible surveyed Dispatch catalog point within 100m of the Player passed runtime scene validation.";
                Log("AMBIENT_WORLD_LOCATION_QUERY_REJECTED",
                    "Candidates=0; Areas=0"
                    + "; " + BuildDispatchLocationCandidateFailureSummary(
                        candidatePlacementAttemptCount,
                        candidateFailureStageCounts,
                        candidateFailureExamples)
                    + "; Reason=" + reason);
                return false;
            }

            // Prefer a different surveyed physical site when the catalog has one;
            // selection is still random among eligible sites, not capture-order based.
            List<DispatchLocationCandidate> freshCandidates = candidates
                .Where(candidate => !_recentLocationIdSet.Contains(candidate.Location.Id))
                .ToList();
            if (freshCandidates.Count > 0)
                candidates = freshCandidates;
            if (movingRoadVehicleScene)
                candidates = candidates
                    .OrderBy(candidate => Distance2D(
                        candidate.Point.Position, playerPosition))
                    .ToList();
            else
                Shuffle(candidates);

            if (RequiresNamedLocation(incidentType, requiredPhysicalType) && candidates.Count == 0)
            {
                reason = "No surveyed point matched the exact physical type required for this named-site event.";
                Log("AMBIENT_WORLD_LOCATION_QUERY_REJECTED",
                    "IncidentType=" + (incidentType ?? string.Empty)
                    + "; Reason=" + reason
                    + "; RequiredIdentity=NamedBankOrStore"
                    + "; CandidateSiteCount=0");
                return false;
            }

            int checkedAreaCandidates = 0;
            foreach (DispatchLocationCandidate candidate in candidates)
            {
                if (checkedAreaCandidates++ >= (movingRoadVehicleScene
                    ? MaximumMovingRoadCandidateAnchorsToValidate
                    : MaximumSurveyCandidatesToValidate))
                    break;

                candidatePlacementAttemptCount++;
                AmbientDispatchSceneResolution resolved;
                string candidateFailure;
                if (!TryBuildDispatchScene(
                    candidate.Area,
                    candidate.Location.Id,
                    candidate.Location.Name,
                    candidate.Location.Category,
                    candidate.Point.Id,
                    candidate.Point.SourceRecord,
                    candidate.Point.Position,
                    candidate.Point.HasHeading ? candidate.Point.Heading : 0f,
                    true,
                    MaximumSurveyPedSnapDistance,
                    playerPosition,
                    true,
                    requiresVehicle,
                    movingRoadVehicleScene,
                    GetDispatchExteriorRoadHeightTolerance(candidate.Area),
                    out resolved,
                    out candidateFailure))
                {
                    RecordDispatchLocationCandidateFailure(
                        candidate.Location.Id,
                        candidate.Point.Id,
                        candidateFailure,
                        candidateFailureStageCounts,
                        candidateFailureExamples);
                    if (string.IsNullOrWhiteSpace(firstCandidateFailure))
                        firstCandidateFailure = "Location=" + candidate.Location.Id
                            + "; Point=" + candidate.Point.Id
                            + "; " + candidateFailure;
                    continue;
                }

                scene = resolved;
                scene.LocationReviewStatus = candidate.Location.ReviewStatus;
                RememberLocation(candidate.Location.Id);
                LogDispatchCandidateFailureSummaryIfAny(
                    incidentType,
                    "SurveyedCatalogSelected",
                    candidate.Location.Id,
                    candidate.Point.Id,
                    candidatePlacementAttemptCount,
                    candidateFailureStageCounts,
                    candidateFailureExamples);
                LogLocationResolution(scene, requiresVehicle);
                return true;
            }

            // Some authored areas have not yet been surveyed, and some observed
            // points may fail current nav/ground checks. A safe, local runtime
            // position around the existing AreaSearchAnchor is an explicitly
            // lower-confidence fallback, never mislabeled as a surveyed site.
            foreach (LSPDDispatchLocationDefinition area in RequiresNamedLocation(
                incidentType, requiredPhysicalType)
                ? Enumerable.Empty<LSPDDispatchLocationDefinition>()
                : ShuffleCopy(eligibleAreas))
            {
                if (excludedLocationIds != null
                    && excludedLocationIds.Contains(area.Id))
                    continue;
                LSImmersiveLocationCatalog.LSImmersiveLocationDefinition anchorLocation;
                string anchorName = _locationCatalog.TryGetLocation(area.Id, out anchorLocation)
                    ? anchorLocation.Name : area.Id;
                candidatePlacementAttemptCount++;
                AmbientDispatchSceneResolution resolved;
                string fallbackFailure;
                if (!TryBuildDispatchScene(
                    area,
                    area.Id,
                    anchorName,
                    "AreaCandidate",
                    "AreaSearchAnchor",
                    string.Empty,
                    area.Position,
                    0f,
                    false,
                    MaximumAreaAnchorSnapDistance,
                    playerPosition,
                    true,
                    requiresVehicle,
                movingRoadVehicleScene,
                    GetDispatchExteriorRoadHeightTolerance(area),
                    out resolved,
                    out fallbackFailure))
                {
                    RecordDispatchLocationCandidateFailure(
                        area.Id,
                        "AreaSearchAnchor",
                        fallbackFailure,
                        candidateFailureStageCounts,
                        candidateFailureExamples);
                    if (string.IsNullOrWhiteSpace(firstCandidateFailure))
                        firstCandidateFailure = "Location=" + area.Id
                            + "; Point=AreaSearchAnchor; " + fallbackFailure;
                    continue;
                }

                scene = resolved;
                scene.LocationReviewStatus = "AreaCandidate_NotNamedSite";
                LogDispatchCandidateFailureSummaryIfAny(
                    incidentType,
                    "AreaAnchorSelected",
                    area.Id,
                    "AreaSearchAnchor",
                    candidatePlacementAttemptCount,
                    candidateFailureStageCounts,
                    candidateFailureExamples);
                LogLocationResolution(scene, requiresVehicle);
                return true;
            }

            reason = candidates.Count == 0
                ? "No surveyed foot candidate matched the event area; no nearby AreaSearchAnchor resolved to a safe ped position."
                : "Surveyed candidates and their AreaSearchAnchor fallbacks failed runtime ground, exterior, occupancy, or vehicle-staging checks.";
            Log("AMBIENT_WORLD_LOCATION_QUERY_REJECTED",
                "Candidates=" + candidates.Count
                + "; Areas=" + eligibleAreas.Count
                + "; " + BuildDispatchLocationCandidateFailureSummary(
                    candidatePlacementAttemptCount,
                    candidateFailureStageCounts,
                    candidateFailureExamples)
                + "; EntranceReferenceCandidatesAvailable=" + entranceReferenceCandidatesAvailable
                + "; FirstPlacementFailure=" + (firstCandidateFailure.Length == 0
                    ? "None" : firstCandidateFailure)
                + "; Reason=" + reason);
            return false;
        }

        /// <summary>
        /// Re-checks the selected point immediately before scene creation. GTA
        /// streaming and nearby occupants can change between the offer and acceptance.
        /// </summary>
        internal bool TryRevalidateDispatchScene(
            AmbientDispatchSceneResolution previous,
            Vector3 playerPosition,
            bool requiresVehicle,
            out AmbientDispatchSceneResolution refreshed,
            out string reason,
            string requiredInteriorLocationId = null,
            string requiredPhysicalType = null,
            string requiredLocationContext = null,
            IEnumerable<int> excludedPreparedPedHandles = null)
        {
            refreshed = null;
            reason = string.Empty;
            if (previous == null)
            {
                reason = "The dispatch offer has no AmbientWorld location plan.";
                return false;
            }

            if (previous.IsInteriorScene)
            {
                if (previous.IsInsideOnlyInteriorScene)
                {
                    if (previous.InteriorAccessPair != null
                        || previous.InsideOnlyLocation == null
                        || previous.InsideOnlyPoint == null
                        || !string.Equals(previous.InsideOnlyLocation.Id,
                            previous.LocationId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(previous.InsideOnlyPoint.Id,
                            previous.PointId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(previous.InsideOnlyPoint.InteriorId,
                            previous.CatalogInteriorId, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "Stage=SceneRevalidationIdentity; Location=" + previous.LocationId
                            + "; CatalogInteriorId=" + previous.CatalogInteriorId
                            + "; Result=SelectedInsideOnlyCatalogPointIdentityChanged";
                        return false;
                    }

                    return TryResolveInsideOnlyInteriorScene(
                        new DispatchInteriorPointReference
                        {
                            Location = previous.InsideOnlyLocation,
                            Point = previous.InsideOnlyPoint
                        },
                        previous.IncidentType,
                        requiredInteriorLocationId,
                        requiredPhysicalType,
                        requiredLocationContext,
                        playerPosition,
                        previous.LiveInteriorId,
                        requiresVehicle,
                        previous.Area,
                        out refreshed,
                        out reason);
                }

                LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair previousPair =
                    previous.InteriorAccessPair;
                if (previousPair == null
                    || previousPair.InsideLocation == null
                    || !string.Equals(previousPair.InsideLocation.Id, previous.LocationId,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(previousPair.InteriorId, previous.CatalogInteriorId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Stage=SceneRevalidationIdentity; Location=" + previous.LocationId
                        + "; CatalogInteriorId=" + previous.CatalogInteriorId
                        + "; Reason=SelectedMappedPairIdentityChanged";
                    return false;
                }

                return TryResolveInteriorDispatchScene(
                    new[] { previous.Area },
                    previous.IncidentType,
                    playerPosition,
                    previous.LiveInteriorId,
                    requiresVehicle,
                    previousPair,
                    true,
                    !string.IsNullOrWhiteSpace(requiredInteriorLocationId),
                    out refreshed,
                    out reason,
                    requiredPhysicalType,
                    requiredLocationContext,
                    previous.IsMappedInteriorDispatchScene);
            }

            if (string.Equals(previous.LocationCategory, "PlayerRadiusOutdoorFoot",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TryRevalidatePlayerRadiusOutdoorFootScene(
                    previous,
                    playerPosition,
                    requiresVehicle,
                    requiredPhysicalType,
                    requiredLocationContext,
                    excludedPreparedPedHandles,
                    out refreshed,
                    out reason);
            }

            if (string.Equals(previous.LocationCategory, "PlayerRadiusRoadsideFoot",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TryRevalidatePlayerRadiusRoadFootScene(
                    previous,
                    playerPosition,
                    requiresVehicle,
                    requiredPhysicalType,
                    requiredLocationContext,
                    excludedPreparedPedHandles,
                    out refreshed,
                    out reason);
            }

            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition previousLocation;
            if (!string.IsNullOrWhiteSpace(requiredPhysicalType)
                && (_locationCatalog == null
                    || !_locationCatalog.TryGetLocation(previous.LocationId, out previousLocation)
                    || !MatchesRequiredPhysicalType(previousLocation, requiredPhysicalType)))
            {
                reason = "The selected site no longer matches the event's required physical type.";
                return false;
            }

            if (IsFootActivityOnlySurveyCandidate(
                    previous.LocationId, previous.LocationCategory))
            {
                return TryRevalidateFootCatalogScene(
                    previous,
                    requiresVehicle,
                    excludedPreparedPedHandles,
                    out refreshed,
                    out reason);
            }

            if (string.Equals(previous.LocationCategory, "PlayerRadiusRoadNode",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(previous.VehicleStagingMode, "moving_road",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TryRevalidateVehicleScene(
                    previous,
                    playerPosition,
                    requiresVehicle,
                    out refreshed,
                    out reason);
            }

            if (!requiresVehicle && !previous.HasVehicleStagingPosition)
            {
                return TryRevalidatePreparedExteriorScene(
                    previous,
                    excludedPreparedPedHandles,
                    out refreshed,
                    out reason);
            }

            bool resolved = TryBuildDispatchScene(
                previous.Area,
                previous.LocationId,
                previous.LocationName,
                previous.LocationCategory,
                previous.PointId,
                previous.SourceCaptureId,
                previous.AuthoredPosition,
                previous.Heading,
                previous.IsSurveyedPoint,
                previous.MaximumSnapDistance,
                playerPosition,
                false,
                requiresVehicle,
                false,
                previous.ExteriorRoadHeightTolerance,
                out refreshed,
                out reason);
            if (resolved && refreshed != null)
                refreshed.LocationReviewStatus = previous.LocationReviewStatus;
            return resolved;
        }

        private bool TryRevalidatePreparedExteriorScene(
            AmbientDispatchSceneResolution previous,
            IEnumerable<int> excludedPreparedPedHandles,
            out AmbientDispatchSceneResolution refreshed,
            out string reason)
        {
            refreshed = null;
            reason = string.Empty;
            if (previous == null || previous.Area == null || previous.IsInteriorScene
                || previous.HasVehicleStagingPosition)
            {
                reason = "Stage=PreparedExteriorSceneIdentity; Result=SelectedExteriorSceneContractChanged";
                return false;
            }

            float selectedSceneOffset = Distance2D(
                previous.AuthoredPosition, previous.SceneCenter);
            float maximumSceneOffset = GetDispatchExteriorSceneCenterOffsetLimit(
                previous.MaximumSnapDistance, false);
            if (float.IsNaN(selectedSceneOffset) || float.IsInfinity(selectedSceneOffset)
                || !IsDispatchExteriorLocationOffsetValid(
                    previous.AuthoredPosition, previous.SceneCenter, maximumSceneOffset))
            {
                reason = "Stage=PreparedExteriorSceneRange; Location=" + previous.LocationId
                    + "; SelectedSceneOffset=" + selectedSceneOffset.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; Maximum=" + maximumSceneOffset.ToString(
                        "0.00", CultureInfo.InvariantCulture);
                return false;
            }

            var excludedHandles = new HashSet<int>((excludedPreparedPedHandles
                ?? Enumerable.Empty<int>()).Where(handle => handle > 0));
            Vector3 safePosition;
            Vector3 approachPosition;
            string placementEvidence;
            if (!TryValidatePreparedDispatchExteriorScenePoint(
                    previous.SceneCenter,
                    previous.ExteriorRoadHeightTolerance,
                    previous.AuthoredPosition,
                    previous.AreaAnchorPosition,
                    previous.MinimumExteriorOpenFootprintDirections,
                    maximumSceneOffset,
                    excludedHandles,
                    out safePosition,
                    out approachPosition,
                    out placementEvidence,
                    out reason))
                return false;

            float horizontalDrift = Distance2D(safePosition, previous.SceneCenter);
            float verticalDrift = Math.Abs(safePosition.Z - previous.SceneCenter.Z);
            if (float.IsNaN(horizontalDrift) || float.IsInfinity(horizontalDrift)
                || horizontalDrift > 0.75f || verticalDrift > 1f
                || !IsDispatchExteriorLocationOffsetValid(
                    previous.AuthoredPosition, safePosition, maximumSceneOffset))
            {
                reason = "Stage=PreparedExteriorSceneContinuity; Location=" + previous.LocationId
                    + "; Point=" + previous.PointId
                    + "; Selected=" + previous.SceneCenter
                    + "; Revalidated=" + safePosition
                    + "; HorizontalDrift=" + horizontalDrift.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; VerticalDrift=" + verticalDrift.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; MaximumHorizontalDrift=0.75; MaximumVerticalDrift=1.00";
                return false;
            }

            previous.SceneCenter = safePosition;
            previous.ActorPosition = safePosition;
            previous.ExteriorApproachPosition = approachPosition;
            previous.ExteriorPlacementEvidence = placementEvidence
                + "; Revalidation=SelectedSceneCenter"
                + "; HorizontalDrift=" + horizontalDrift.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; VerticalDrift=" + verticalDrift.ToString(
                    "0.00", CultureInfo.InvariantCulture);
            previous.ValidationStatus = "RuntimeRevalidatedSelectedExteriorScene";
            refreshed = previous;
            return true;
        }

        private bool TryValidatePreparedDispatchExteriorScenePoint(
            Vector3 selectedPosition,
            float maximumStreetHeightDelta,
            Vector3 authoredAnchor,
            Vector3 areaAnchor,
            int minimumOpenFootprintDirections,
            float maximumNavigationSampleDistance,
            ISet<int> excludedPedHandles,
            out Vector3 safePosition,
            out Vector3 approachPosition,
            out string evidence,
            out string reason)
        {
            safePosition = selectedPosition;
            approachPosition = Vector3.Zero;
            evidence = string.Empty;
            reason = string.Empty;
            try
            {
                int selectedInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    selectedPosition.X,
                    selectedPosition.Y,
                    selectedPosition.Z);
                if (selectedInteriorId != 0)
                {
                    reason = "Stage=PreparedExteriorSceneInteriorIdentity; Native=GET_INTERIOR_AT_COORDS"
                        + "; InteriorId=" + selectedInteriorId;
                    return false;
                }

                Vector3 navigationSample;
                if (!World.GetSafePositionForPed(
                        selectedPosition,
                        out navigationSample,
                        GetSafePositionFlags.NotInterior | GetSafePositionFlags.NotWater))
                {
                    reason = "Stage=PreparedExteriorSceneNavigation; Native=GET_SAFE_COORD_FOR_PED; Result=false";
                    return false;
                }
                float navigationSampleDistance = Distance2D(
                    navigationSample, selectedPosition);
                if (float.IsNaN(navigationSampleDistance)
                    || float.IsInfinity(navigationSampleDistance)
                    || navigationSampleDistance > maximumNavigationSampleDistance)
                {
                    reason = "Stage=PreparedExteriorSceneNavigationRange; Native=GET_SAFE_COORD_FOR_PED"
                        + "; Selected=" + selectedPosition
                        + "; Sample=" + navigationSample
                        + "; Distance=" + navigationSampleDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; Maximum=" + maximumNavigationSampleDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture);
                    return false;
                }
                int navigationSampleInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    navigationSample.X,
                    navigationSample.Y,
                    navigationSample.Z);
                if (navigationSampleInteriorId != 0)
                {
                    reason = "Stage=PreparedExteriorSceneNavigationInteriorIdentity; Native=GET_INTERIOR_AT_COORDS"
                        + "; SampleInteriorId=" + navigationSampleInteriorId;
                    return false;
                }

                float groundZ;
                Vector3 groundNormal;
                bool groundHeightFound = World.GetGroundHeightAndNormal(
                    selectedPosition, out groundZ, out groundNormal);
                if (!groundHeightFound
                    || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || groundNormal.Z < 0.55f
                    || Math.Abs(selectedPosition.Z - groundZ) > 1f)
                {
                    reason = "Stage=PreparedExteriorSceneGround; Native=GET_GROUND_HEIGHT_AND_NORMAL"
                        + "; GroundHeightFound=" + groundHeightFound
                        + "; GroundZ=" + groundZ.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; NormalZ=" + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; SelectedZ=" + selectedPosition.Z.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; MaximumVerticalDrift=1.00";
                    return false;
                }

                safePosition = new Vector3(selectedPosition.X, selectedPosition.Y, groundZ);
                Vector3 nearestStreet = World.GetNextPositionOnStreet(safePosition);
                float streetDistance = Distance2D(safePosition, nearestStreet);
                float streetHeightDelta = safePosition.Z - nearestStreet.Z;
                if (maximumStreetHeightDelta > 0f
                    && nearestStreet != Vector3.Zero
                    && streetDistance <= DispatchStreetHeightReferenceRadius
                    && Math.Abs(streetHeightDelta) > maximumStreetHeightDelta)
                {
                    reason = "Stage=PreparedExteriorSceneStreetHeight; Native=GET_NEXT_POSITION_ON_STREET"
                        + "; Candidate=" + safePosition
                        + "; StreetNode=" + nearestStreet
                        + "; VerticalDelta=" + streetHeightDelta.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; Maximum=" + maximumStreetHeightDelta.ToString(
                            "0.00", CultureInfo.InvariantCulture);
                    return false;
                }
                if (streetDistance < 1.25f)
                {
                    reason = "Stage=PreparedExteriorSceneRoadLane; Native=GET_NEXT_POSITION_ON_STREET"
                        + "; HorizontalDistance=" + streetDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture);
                    return false;
                }
                if (HasOverlappingPed(safePosition, 1.35f, excludedPedHandles))
                {
                    reason = "Stage=PreparedExteriorScenePedOccupancy; Native=ENTITY_CLEARANCE";
                    return false;
                }
                if (HasOverlappingVehicle(safePosition, 1.8f))
                {
                    reason = "Stage=PreparedExteriorSceneVehicleOccupancy; Native=ENTITY_CLEARANCE";
                    return false;
                }

                if (!TryValidateDispatchExteriorApproach(
                        safePosition,
                        authoredAnchor,
                        areaAnchor,
                        minimumOpenFootprintDirections,
                        out approachPosition,
                        out evidence,
                        out reason))
                    return false;

                evidence += "; Revalidation=SelectedSceneCenter"
                    + "; NavigationSampleDistance=" + navigationSampleDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; FreshSafeCoordinateDidNotMoveScene=true";
                return true;
            }
            catch (Exception ex)
            {
                reason = "Stage=PreparedExteriorSceneRuntimeProbe; Native=GET_INTERIOR_AT_COORDS/"
                    + "GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL/"
                    + "GET_NEXT_POSITION_ON_STREET; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        private bool TryRevalidateFootCatalogScene(
            AmbientDispatchSceneResolution previous,
            bool requiresVehicle,
            IEnumerable<int> excludedPreparedPedHandles,
            out AmbientDispatchSceneResolution refreshed,
            out string reason)
        {
            refreshed = null;
            reason = string.Empty;
            if (previous == null || previous.Area == null || previous.IsInteriorScene
                || !previous.IsSurveyedPoint || requiresVehicle
                || !IsFootActivityOnlySurveyCandidate(
                    previous.LocationId, previous.LocationCategory))
            {
                reason = "Stage=FootCatalogSceneRevalidationIdentity; Result=CandidateContractChanged";
                return false;
            }

            float selectedSceneOffset = Distance2D(
                previous.AuthoredPosition, previous.SceneCenter);
            if (float.IsNaN(selectedSceneOffset) || float.IsInfinity(selectedSceneOffset)
                || selectedSceneOffset > DispatchExteriorFootCatalogSceneOffsetLimit)
            {
                reason = "Stage=FootCatalogSceneRevalidationRange; Location="
                    + previous.LocationId
                    + "; SelectedSceneOffset=" + selectedSceneOffset.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; Maximum=" + DispatchExteriorFootCatalogSceneOffsetLimit.ToString(
                        "0.00", CultureInfo.InvariantCulture);
                return false;
            }

            var excludedHandles = new HashSet<int>((excludedPreparedPedHandles
                ?? Enumerable.Empty<int>()).Where(handle => handle > 0));
            Vector3 safePosition;
            Vector3 approachPosition;
            string placementEvidence;
            if (!TryValidateDispatchExteriorPosition(
                    previous.SceneCenter,
                    0.75f,
                    previous.ExteriorRoadHeightTolerance,
                    previous.AuthoredPosition,
                    previous.AreaAnchorPosition,
                    previous.MinimumExteriorOpenFootprintDirections,
                    excludedHandles,
                    out safePosition,
                    out approachPosition,
                    out placementEvidence,
                    out reason,
                    true)
                || Distance2D(safePosition, previous.SceneCenter) > 0.75f
                || !IsDispatchExteriorLocationOffsetValid(
                    previous.AuthoredPosition,
                    safePosition,
                    DispatchExteriorFootCatalogSceneOffsetLimit))
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = "Stage=FootCatalogSceneRevalidationSnap; Selected="
                        + previous.SceneCenter
                        + "; Resolved=" + safePosition
                        + "; MaximumSelectedPointOffset=0.75m";
                }
                return false;
            }

            float selectedPointOffset = Distance2D(
                safePosition, previous.SceneCenter);
            previous.SceneCenter = safePosition;
            previous.ActorPosition = safePosition;
            previous.ExteriorApproachPosition = approachPosition;
            previous.ExteriorPlacementEvidence = placementEvidence
                + "; Revalidation=SelectedFootScenePoint"
                + "; SelectedPointOffset=" + selectedPointOffset.ToString(
                        "0.00", CultureInfo.InvariantCulture);
            previous.ValidationStatus = "RuntimeRevalidatedSelectedFootCatalogScene";
            refreshed = previous;
            reason = string.Empty;
            return true;
        }

        private bool TryRevalidatePlayerRadiusOutdoorFootScene(
            AmbientDispatchSceneResolution previous,
            Vector3 playerPosition,
            bool requiresVehicle,
            string requiredPhysicalType,
            string requiredLocationContext,
            IEnumerable<int> excludedPreparedPedHandles,
            out AmbientDispatchSceneResolution refreshed,
            out string reason)
        {
            refreshed = null;
            reason = string.Empty;
            if (previous == null || previous.IsInteriorScene || requiresVehicle
                || !string.IsNullOrWhiteSpace(previous.LocationId)
                || !string.IsNullOrWhiteSpace(requiredPhysicalType)
                || !string.Equals(requiredLocationContext, "any",
                    StringComparison.OrdinalIgnoreCase)
                || previous.Area == null
                || !string.Equals(previous.Area.Id,
                    "runtime.player-radius-outdoor-foot-scene",
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "Stage=PlayerRadiusOutdoorFootRevalidationIdentity; Result=RuntimeOutdoorFootContractChanged";
                return false;
            }

            float playerDistance = Distance2D(previous.SceneCenter, playerPosition);
            Vector3 selectionAnchor = previous.AreaAnchorPosition == Vector3.Zero
                ? playerPosition : previous.AreaAnchorPosition;
            float selectionDistance = Distance2D(previous.SceneCenter, selectionAnchor);
            float minimumPlayerDistance;
            float maximumPlayerDistance;
            GetPlayerRadiusFallbackDistanceRange(
                out minimumPlayerDistance, out maximumPlayerDistance);
            if (selectionDistance < minimumPlayerDistance
                || selectionDistance > maximumPlayerDistance
                || playerDistance > maximumPlayerDistance)
            {
                reason = "Stage=PlayerRadiusOutdoorFootRevalidationRange; PlayerDistance="
                    + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; SelectionDistance=" + selectionDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Result=Rejected";
                return false;
            }

            var excludedHandles = new HashSet<int>((excludedPreparedPedHandles
                ?? Enumerable.Empty<int>()).Where(handle => handle > 0));
            Vector3 safePosition;
            Vector3 approachPosition;
            string exteriorEvidence;
            string placementFailure;
            if (!TryValidateDispatchExteriorPosition(
                previous.SceneCenter,
                9f,
                MovingRoadElevationTolerance,
                previous.AuthoredPosition,
                selectionAnchor,
                previous.MinimumExteriorOpenFootprintDirections,
                excludedHandles,
                out safePosition,
                out approachPosition,
                out exteriorEvidence,
                out placementFailure)
                || Distance2D(safePosition, previous.SceneCenter) > 9f)
            {
                reason = string.IsNullOrWhiteSpace(placementFailure)
                    ? "Stage=PlayerRadiusOutdoorFootRevalidationSnap; Native=GET_SAFE_COORD_FOR_PED; Result=Rejected"
                    : placementFailure;
                return false;
            }

            float refreshedPlayerDistance = Distance2D(safePosition, playerPosition);
            float refreshedSelectionDistance = Distance2D(safePosition, selectionAnchor);
            if (refreshedSelectionDistance < minimumPlayerDistance
                || refreshedSelectionDistance > maximumPlayerDistance
                || refreshedPlayerDistance > maximumPlayerDistance)
            {
                reason = "Stage=PlayerRadiusOutdoorFootRevalidationResolvedRange; PlayerDistance="
                    + refreshedPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; SelectionDistance=" + refreshedSelectionDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Result=Rejected";
                return false;
            }

            previous.SceneCenter = safePosition;
            previous.ActorPosition = safePosition;
            previous.Heading = (float)(Math.Atan2(
                playerPosition.X - safePosition.X,
                playerPosition.Y - safePosition.Y) * 180d / Math.PI);
            refreshed = previous;
            Log("AMBIENT_WORLD_PLAYER_RADIUS_OUTDOOR_FOOT_SCENE_REVALIDATED",
                "ScenePosition=" + safePosition
                + "; PlayerDistance=" + Distance2D(safePosition, playerPosition)
                    .ToString("0.0", CultureInfo.InvariantCulture)
                + "; ExcludedPreparedPeds=" + excludedHandles.Count
                + "; Native=GET_INTERIOR_AT_COORDS/GET_SAFE_COORD_FOR_PED/"
                    + "GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD/GET_NEXT_POSITION_ON_STREET"
                + "; OwnershipPreserved=true");
            return true;
        }

        private bool TryRevalidatePlayerRadiusRoadFootScene(
            AmbientDispatchSceneResolution previous,
            Vector3 playerPosition,
            bool requiresVehicle,
            string requiredPhysicalType,
            string requiredLocationContext,
            IEnumerable<int> excludedPreparedPedHandles,
            out AmbientDispatchSceneResolution refreshed,
            out string reason)
        {
            refreshed = null;
            reason = string.Empty;
            if (previous == null || previous.IsInteriorScene || requiresVehicle
                || !string.IsNullOrWhiteSpace(previous.LocationId)
                || !string.IsNullOrWhiteSpace(requiredPhysicalType)
                || !string.Equals(requiredLocationContext, "road_foot",
                    StringComparison.OrdinalIgnoreCase)
                || previous.Area == null
                || !string.Equals(previous.Area.Id,
                    "runtime.player-radius-road-foot-scene",
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "Stage=PlayerRadiusRoadFootRevalidationIdentity; Result=RuntimeRoadFootContractChanged";
                return false;
            }

            float playerDistance = Distance2D(previous.SceneCenter, playerPosition);
            Vector3 selectionAnchor = previous.AreaAnchorPosition == Vector3.Zero
                ? playerPosition : previous.AreaAnchorPosition;
            float selectionDistance = Distance2D(previous.SceneCenter, selectionAnchor);
            float minimumPlayerDistance;
            float maximumPlayerDistance;
            GetPlayerRadiusFallbackDistanceRange(
                out minimumPlayerDistance, out maximumPlayerDistance);
            if (selectionDistance < minimumPlayerDistance
                || selectionDistance > maximumPlayerDistance
                || playerDistance > maximumPlayerDistance)
            {
                reason = "Stage=PlayerRadiusRoadFootRevalidationRange; PlayerDistance="
                    + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; SelectionDistance=" + selectionDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Result=Rejected";
                return false;
            }

            var excludedHandles = new HashSet<int>((excludedPreparedPedHandles
                ?? Enumerable.Empty<int>()).Where(handle => handle > 0));
            Vector3 safePedPosition;
            Vector3 approachPosition;
            string exteriorEvidence;
            string placementFailure;
            if (!TryValidateDispatchExteriorPosition(
                previous.SceneCenter,
                10f,
                MovingRoadElevationTolerance,
                previous.AuthoredPosition,
                selectionAnchor,
                previous.MinimumExteriorOpenFootprintDirections,
                excludedHandles,
                out safePedPosition,
                out approachPosition,
                out exteriorEvidence,
                out placementFailure))
            {
                reason = placementFailure;
                return false;
            }
            if (Distance2D(safePedPosition, previous.SceneCenter) > 10f)
            {
                reason = "Stage=PlayerRadiusRoadFootRevalidationSnap; Native=GET_SAFE_COORD_FOR_PED; Result=Rejected";
                return false;
            }
            float refreshedPlayerDistance = Distance2D(safePedPosition, playerPosition);
            float refreshedSelectionDistance = Distance2D(safePedPosition, selectionAnchor);
            if (refreshedSelectionDistance < minimumPlayerDistance
                || refreshedSelectionDistance > maximumPlayerDistance
                || refreshedPlayerDistance > maximumPlayerDistance)
            {
                reason = "Stage=PlayerRadiusRoadFootRevalidationResolvedRange; PlayerDistance="
                    + refreshedPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; SelectionDistance=" + refreshedSelectionDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Result=Rejected";
                return false;
            }

            previous.SceneCenter = safePedPosition;
            previous.ActorPosition = safePedPosition;
            refreshed = previous;
            Log("AMBIENT_WORLD_PLAYER_RADIUS_ROAD_FOOT_SCENE_REVALIDATED",
                "ScenePosition=" + safePedPosition
                + "; PlayerDistance=" + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + "; ExcludedPreparedPeds=" + excludedHandles.Count
                + "; Native=GET_INTERIOR_AT_COORDS/GET_SAFE_COORD_FOR_PED/GET_GROUND_Z_AND_NORMAL"
                + "; OwnershipPreserved=true");
            return true;
        }

        /// <summary>
        /// Revalidates the exact prepared interior plan without searching for a
        /// replacement spawn point around Dispatch-owned participants. This is
        /// used only while the same occurrence is being resumed after a partial
        /// actor-preparation retry.
        /// </summary>
        internal bool TryRevalidatePreparedInteriorScene(
            AmbientDispatchSceneResolution previous,
            bool requiresVehicleStaging,
            string requiredPhysicalType,
            string requiredLocationContext,
            out string reason)
        {
            reason = string.Empty;
            if (previous == null || !previous.IsInteriorScene
                || previous.LiveInteriorId == 0)
            {
                reason = "Stage=SceneRevalidationIdentity; Result=SelectedInteriorSceneIdentityChanged";
                return false;
            }

            bool insideOnly = previous.IsInsideOnlyInteriorScene;
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair accessPair =
                previous.InteriorAccessPair;
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location = insideOnly
                ? previous.InsideOnlyLocation
                : accessPair == null ? null : accessPair.InsideLocation;
            LSImmersiveLocationCatalog.LSImmersiveLocationPoint interiorPoint = insideOnly
                ? previous.InsideOnlyPoint
                : accessPair == null ? null : accessPair.InsidePoint;
            bool selectedCatalogIdentityValid = insideOnly
                ? accessPair == null
                    && location != null
                    && interiorPoint != null
                    && string.Equals(interiorPoint.Role, "InteriorPointCandidate",
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(location.Id, previous.LocationId,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(interiorPoint.Id, previous.PointId,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(interiorPoint.InteriorId, previous.CatalogInteriorId,
                        StringComparison.OrdinalIgnoreCase)
                : accessPair != null
                    && location != null
                    && interiorPoint != null
                    && accessPair.OutsidePoint != null
                    && HasCatalogedMappedInteriorReference(accessPair)
                    && string.Equals(location.Id, previous.LocationId,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(accessPair.InteriorId, previous.CatalogInteriorId,
                        StringComparison.OrdinalIgnoreCase);
            if (!selectedCatalogIdentityValid)
            {
                reason = "Stage=SceneRevalidationIdentity; Location=" + previous.LocationId
                    + "; CatalogInteriorId=" + previous.CatalogInteriorId
                    + "; Result=SelectedCatalogPointOrPairIdentityChanged";
                return false;
            }

            bool compatibleInterior = previous.IsMappedInteriorDispatchScene
                ? IsCompatibleWithMappedInteriorIncident(
                    location, previous.IncidentType, requiredPhysicalType)
                    && MatchesMappedInteriorDispatchLocationContextMetadata(
                        location, requiredLocationContext)
                : IsCompatibleWithIncident(location, previous.IncidentType)
                    && MatchesDispatchLocationContextMetadata(
                        location, requiredLocationContext);
            if (IsRejectedSurveyRecord(location)
                || !MatchesRequiredPhysicalType(location, requiredPhysicalType)
                || !compatibleInterior)
            {
                reason = "Stage=SceneRevalidationEligibility; Location=" + location.Id
                    + "; PhysicalType=" + location.PhysicalType
                    + "; IncidentType=" + previous.IncidentType
                    + "; Result=CatalogCompatibilityChanged";
                return false;
            }
            if (requiresVehicleStaging && !previous.HasVehicleStagingPosition)
            {
                reason = "Stage=PlayerUtilityStaging; Result=ExteriorUtilityPositionMissing";
                return false;
            }

            bool interiorReady;
            try
            {
                interiorReady = IsInteriorReady(previous.LiveInteriorId);
            }
            catch (Exception ex)
            {
                reason = "Stage=SceneRevalidationReadiness; LiveInteriorId="
                    + previous.LiveInteriorId + "; Native=IS_INTERIOR_READY; Result=Exception:"
                    + ex.GetType().Name;
                return false;
            }
            bool playerInsideLiveInterior = IsPlayerInsideLiveInterior(previous.LiveInteriorId);
            if (!interiorReady)
            {
                try { RequestInteriorCollision(previous.SceneCenter); }
                catch (Exception ex)
                {
                    reason = "Stage=SceneRevalidationReadiness; LiveInteriorId="
                        + previous.LiveInteriorId
                        + "; Native=IS_INTERIOR_READY:false; CollisionRequest=Exception:"
                        + ex.GetType().Name;
                    return false;
                }
                if (!playerInsideLiveInterior)
                {
                    reason = "PENDING:Stage=SceneRevalidationReadiness; LiveInteriorId="
                        + previous.LiveInteriorId
                        + "; Native=IS_INTERIOR_READY:false; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    return false;
                }
            }

            if (insideOnly)
            {
                int currentPlayerLiveInteriorId;
                bool playerIdentityAvailable = TryGetDispatchPlayerInteriorId(
                    Game.Player.Character, out currentPlayerLiveInteriorId);
                int pointLiveInteriorId;
                try
                {
                    pointLiveInteriorId = GetInteriorAt(interiorPoint.Position);
                }
                catch (Exception ex)
                {
                    reason = "Stage=SceneRevalidationInteriorIdentity; Location=" + location.Id
                        + "; CatalogInteriorId=" + previous.CatalogInteriorId
                        + "; ExpectedLiveInteriorId=" + previous.LiveInteriorId
                        + "; Native=GET_INTERIOR_AT_COORDS; Result=Exception:" + ex.GetType().Name;
                    return false;
                }
                if (!playerIdentityAvailable)
                {
                    reason = "Stage=SceneRevalidationInteriorIdentity; Location=" + location.Id
                        + "; CatalogInteriorId=" + previous.CatalogInteriorId
                        + "; ExpectedLiveInteriorId=" + previous.LiveInteriorId
                        + "; PlayerLiveInteriorId=Unavailable"
                        + "; PointLiveInteriorId=" + pointLiveInteriorId
                        + "; Native=GET_INTERIOR_AT_COORDS/GET_INTERIOR_FROM_ENTITY"
                        + "; Result=PlayerIdentityProbeUnavailable";
                    return false;
                }
                if (currentPlayerLiveInteriorId == 0 || pointLiveInteriorId == 0)
                {
                    try { RequestInteriorCollision(previous.SceneCenter); }
                    catch (Exception ex)
                    {
                        reason = "Stage=SceneRevalidationInteriorIdentity; Location=" + location.Id
                            + "; CatalogInteriorId=" + previous.CatalogInteriorId
                            + "; ExpectedLiveInteriorId=" + previous.LiveInteriorId
                            + "; PlayerLiveInteriorId=" + currentPlayerLiveInteriorId
                            + "; PointLiveInteriorId=" + pointLiveInteriorId
                            + "; Native=GET_INTERIOR_AT_COORDS:0"
                            + "; CollisionRequest=Exception:" + ex.GetType().Name;
                        return false;
                    }
                    reason = "PENDING:Stage=SceneRevalidationInteriorIdentity; Location=" + location.Id
                        + "; CatalogInteriorId=" + previous.CatalogInteriorId
                        + "; ExpectedLiveInteriorId=" + previous.LiveInteriorId
                        + "; PlayerLiveInteriorId=" + currentPlayerLiveInteriorId
                        + "; PointLiveInteriorId=" + pointLiveInteriorId
                        + "; Native=GET_INTERIOR_AT_COORDS:0"
                        + "; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    return false;
                }
                if (currentPlayerLiveInteriorId != previous.LiveInteriorId
                    || pointLiveInteriorId != previous.LiveInteriorId)
                {
                    reason = "Stage=SceneRevalidationInteriorIdentity; Location=" + location.Id
                        + "; CatalogInteriorId=" + previous.CatalogInteriorId
                        + "; ExpectedLiveInteriorId=" + previous.LiveInteriorId
                        + "; PlayerLiveInteriorId=" + (playerIdentityAvailable
                            ? currentPlayerLiveInteriorId.ToString(CultureInfo.InvariantCulture)
                            : "Unavailable")
                        + "; PointLiveInteriorId=" + pointLiveInteriorId
                        + "; Native=GET_INTERIOR_AT_COORDS/GET_INTERIOR_FROM_ENTITY"
                        + "; Result=InsideOnlyIdentityChanged";
                    return false;
                }
            }
            else
            {
                Vector3 outsideProbe = ResolveOutsideInteriorProbe(accessPair);
                int liveOutsideInteriorId = GetInteriorAt(outsideProbe);
                int liveInsideInteriorId = GetInteriorAt(interiorPoint.Position);
                if (!IsMappedInteriorThresholdRuntimeValid(
                    previous.LiveInteriorId, liveOutsideInteriorId, liveInsideInteriorId))
                {
                    reason = "Stage=SceneRevalidationThreshold; Location=" + location.Id
                        + "; CatalogInteriorId=" + previous.CatalogInteriorId
                        + "; ExpectedLiveInteriorId=" + previous.LiveInteriorId
                        + "; OutsideLiveInteriorId=" + liveOutsideInteriorId
                        + "; InsideLiveInteriorId=" + liveInsideInteriorId;
                    return false;
                }
            }

            Vector3 safePosition = previous.SceneCenter;
            bool safePositionFound;
            bool groundHeightFound;
            float groundZ = 0f;
            Vector3 groundNormal = Vector3.Zero;
            try
            {
                safePositionFound = World.GetSafePositionForPed(
                    previous.SceneCenter, out safePosition, (GetSafePositionFlags)0);
                groundHeightFound = World.GetGroundHeightAndNormal(
                    safePositionFound ? safePosition : previous.SceneCenter,
                    out groundZ, out groundNormal);
            }
            catch (Exception ex)
            {
                reason = "Stage=SceneRevalidationFloor; LiveInteriorId="
                    + previous.LiveInteriorId + "; Native=GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL"
                    + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }
            if (!safePositionFound || !groundHeightFound)
            {
                try { RequestInteriorCollision(previous.SceneCenter); }
                catch (Exception ex)
                {
                    reason = "Stage=SceneRevalidationFloor; LiveInteriorId="
                        + previous.LiveInteriorId
                        + "; SafePositionFound=" + safePositionFound
                        + "; GroundHeightFound=" + groundHeightFound
                        + "; Native=GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL"
                        + "; CollisionRequest=Exception:" + ex.GetType().Name;
                    return false;
                }
                reason = "PENDING:Stage=SceneRevalidationFloor; LiveInteriorId="
                    + previous.LiveInteriorId
                    + "; SafePositionFound=" + safePositionFound
                    + "; GroundHeightFound=" + groundHeightFound
                    + "; Native=GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL"
                    + "; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                return false;
            }

            int sceneCenterInteriorId = GetInteriorAt(previous.SceneCenter);
            int safePositionInteriorId = GetInteriorAt(safePosition);
            float navigationCorrection = safePosition.DistanceTo(previous.SceneCenter);
            bool validGroundHeight = !float.IsNaN(groundZ) && !float.IsInfinity(groundZ);
            float floorDelta = validGroundHeight
                ? Math.Abs(previous.SceneCenter.Z - groundZ) : float.PositiveInfinity;
            string floorFailureResult = sceneCenterInteriorId != previous.LiveInteriorId
                ? "SceneCenterInteriorIdentityMismatch"
                : safePositionInteriorId != previous.LiveInteriorId
                    ? "SafePositionInteriorIdentityMismatch"
                    : float.IsNaN(navigationCorrection) || float.IsInfinity(navigationCorrection)
                        || navigationCorrection > 2.25f
                        ? "SafeNavigationCorrectionExceeded"
                        : !validGroundHeight
                            ? "GroundHeightInvalid"
                            : groundNormal.Z < 0.55f
                                ? "GroundNormalBelowMinimum"
                                : floorDelta > InteriorFloorAlignmentTolerance
                                    ? "FloorAlignmentExceeded"
                                    : string.Empty;
            if (!string.IsNullOrWhiteSpace(floorFailureResult))
            {
                string floorEvidence = "; SceneCenterLiveInteriorId=" + sceneCenterInteriorId
                    + "; SafePositionLiveInteriorId=" + safePositionInteriorId
                    + "; NavigationCorrection=" + navigationCorrection.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; GroundZ=" + (validGroundHeight
                        ? groundZ.ToString("0.00", CultureInfo.InvariantCulture) : "Invalid")
                    + "; GroundNormalZ=" + groundNormal.Z.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; FloorDelta=" + (validGroundHeight
                        ? floorDelta.ToString("0.00", CultureInfo.InvariantCulture) : "Invalid")
                    + "; IsInteriorReady=" + interiorReady
                    + "; PlayerInsideLiveInterior=" + playerInsideLiveInterior
                    + "; Native=GET_INTERIOR_AT_COORDS/GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL"
                    + "; Result=" + floorFailureResult;
                if (!interiorReady && playerInsideLiveInterior)
                {
                    try { RequestInteriorCollision(previous.SceneCenter); }
                    catch (Exception ex)
                    {
                        reason = "Stage=SceneRevalidationFloor; CatalogInteriorId="
                            + previous.CatalogInteriorId + "; LiveInteriorId="
                            + previous.LiveInteriorId + floorEvidence
                            + "; CollisionRequest=Exception:" + ex.GetType().Name;
                        return false;
                    }
                    reason = "PENDING:Stage=SceneRevalidationFloor; CatalogInteriorId="
                        + previous.CatalogInteriorId + "; LiveInteriorId="
                        + previous.LiveInteriorId + floorEvidence
                        + "; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    return false;
                }

                reason = "Stage=SceneRevalidationFloor; CatalogInteriorId="
                    + previous.CatalogInteriorId + "; LiveInteriorId="
                    + previous.LiveInteriorId + floorEvidence;
                return false;
            }

            if (!interiorReady && playerInsideLiveInterior)
            {
                Log("AMBIENT_WORLD_PREPARED_INTERIOR_FLOOR_REVALIDATED_WITH_ACTIVE_PLAYER",
                    "CatalogInteriorId=" + previous.CatalogInteriorId
                    + "; LiveInteriorId=" + previous.LiveInteriorId
                    + "; Native=IS_INTERIOR_READY:false"
                    + "; Evidence=" + (insideOnly
                        ? "CatalogInteriorPoint+SafePedNavigation+GroundHeight+InteriorIdentity"
                        : "MappedThreshold+SafePedNavigation+GroundHeight+InteriorIdentity"));
            }
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Validates one nearby role-specific foot point for a victim or additional
        /// participant. It never searches beyond the supplied small correction radius.
        /// </summary>
        internal bool TryResolveNearbyPedPosition(
            Vector3 preferredPosition,
            out Vector3 safePosition,
            out string reason)
        {
            return TryValidateOutdoorPedPosition(
                preferredPosition,
                2.75f,
                0f,
                out safePosition,
                out reason);
        }

        internal bool TryResolveDispatchParticipantPosition(
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            out Vector3 safePosition,
            out string reason)
        {
            return TryResolveDispatchParticipantPosition(
                preferredPosition, scene, null, out safePosition, out reason);
        }

        internal bool TryResolveDispatchParticipantPosition(
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            IEnumerable<Vector3> reservedPositions,
            out Vector3 safePosition,
            out string reason)
        {
            if (scene != null && scene.IsInteriorScene)
                return TryResolveInteriorNearbyPedPosition(
                    preferredPosition,
                    scene.LiveInteriorId,
                    reservedPositions,
                    out safePosition,
                    out reason);

            // Try the authored participant point first, then a small fixed
            // eight-direction ring around that point. Tight urban/alley scenes
            // often have a wall, prop, or ambient Ped exactly on one formation point even
            // though a nearby walkable point is clear. Keep every fallback
            // inside the same local scene and run the same ground, road,
            // interior, slope, and occupancy checks before returning it.
            safePosition = preferredPosition;
            string lastFailure = "No nearby pedestrian position was checked.";
            string firstFailure = string.Empty;
            int placementProbeCount = 0;
            for (int index = -1; index < DispatchParticipantFallbackDirections.Length; index++)
            {
                Vector3 candidate = preferredPosition;
                float correctionLimit = DispatchParticipantMaximumTotalOffset;
                if (index >= 0)
                {
                    Vector2 offset = DispatchParticipantFallbackDirections[index];
                    candidate = new Vector3(
                        preferredPosition.X + offset.X,
                        preferredPosition.Y + offset.Y,
                        preferredPosition.Z);
                    correctionLimit = DispatchParticipantFallbackOffset;
                }

                Vector3 resolved;
                Vector3 approachPosition;
                string exteriorEvidence;
                string candidateFailure;
                placementProbeCount++;
                if (!TryValidateDispatchExteriorPosition(
                        candidate,
                        correctionLimit,
                        scene == null ? 0f : scene.ExteriorRoadHeightTolerance,
                        scene == null ? candidate : scene.AuthoredPosition,
                        scene == null ? Vector3.Zero : scene.AreaAnchorPosition,
                        scene == null
                            ? DispatchExteriorMinimumOpenFootprintDirections
                            : scene.MinimumExteriorOpenFootprintDirections,
                        null,
                        out resolved,
                        out approachPosition,
                        out exteriorEvidence,
                        out candidateFailure))
                {
                    lastFailure = candidateFailure;
                    if (string.IsNullOrWhiteSpace(firstFailure))
                        firstFailure = candidateFailure;
                    continue;
                }

                if (resolved.DistanceTo(preferredPosition)
                    > DispatchParticipantMaximumTotalOffset)
                {
                    lastFailure = "Stage=ParticipantPlacementRadius; Native=Vector3.Distance"
                        + "; Result=Safe point exceeds "
                        + DispatchParticipantMaximumTotalOffset.ToString(
                            "0.##", CultureInfo.InvariantCulture) + "m.";
                    continue;
                }
                if (!IsSeparatedFromReservedPositions(
                    resolved, reservedPositions, DispatchParticipantMinimumSpacing))
                {
                    lastFailure = "Stage=ParticipantSpacing; Native=LocalVector2Distance"
                        + "; Result=Safe point overlaps a reserved participant position.";
                    continue;
                }

                safePosition = resolved;
                reason = string.Empty;
                Log("AMBIENT_WORLD_DISPATCH_PARTICIPANT_POSITION_VALIDATED",
                    "Location=" + (scene == null ? string.Empty : scene.LocationId)
                    + "; Point=" + (scene == null ? string.Empty : scene.PointId)
                    + "; Position=" + resolved
                    + "; OutdoorApproach=" + approachPosition
                    + "; Evidence=" + exteriorEvidence);
                return true;
            }

            reason = "Stage=ParticipantPlacement; Probes=" + placementProbeCount
                + "; MaximumLocalOffset=" + DispatchParticipantMaximumTotalOffset.ToString(
                    "0.##", CultureInfo.InvariantCulture) + "m"
                + "; FirstFailure=" + (string.IsNullOrWhiteSpace(firstFailure)
                    ? "None" : firstFailure)
                + "; LastFailure=" + lastFailure;
            return false;
        }

        internal bool TryValidateDispatchExteriorActor(
            Ped actor,
            string role,
            AmbientDispatchSceneResolution scene,
            IEnumerable<int> excludedPedHandles,
            out Vector3 validatedGroundPosition,
            out string evidence,
            out string reason)
        {
            validatedGroundPosition = Vector3.Zero;
            evidence = string.Empty;
            reason = string.Empty;
            if (actor == null || !actor.Exists() || scene == null || scene.IsInteriorScene)
            {
                reason = "Stage=ExteriorActorIdentity; Result=ActorOrExteriorSceneUnavailable";
                return false;
            }

            int entityInteriorId;
            int coordinateInteriorId;
            int roomKey;
            try
            {
                entityInteriorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, actor);
                coordinateInteriorId = GetInteriorAt(actor.Position);
                roomKey = Function.Call<int>(Hash.GET_ROOM_KEY_FROM_ENTITY, actor);
            }
            catch (Exception ex)
            {
                reason = "Stage=ExteriorActorIdentity; Native=GET_INTERIOR_FROM_ENTITY/"
                    + "GET_INTERIOR_AT_COORDS/GET_ROOM_KEY_FROM_ENTITY; Result=Exception:"
                    + ex.GetType().Name;
                return false;
            }

            if (!IsDispatchExteriorEntityIdentityValid(
                    entityInteriorId, coordinateInteriorId, roomKey))
            {
                reason = "Stage=ExteriorActorIdentity; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; EntityInteriorId=" + entityInteriorId
                    + "; CoordinateInteriorId=" + coordinateInteriorId
                    + "; RoomKey=" + roomKey
                    + "; Result=ActorIsNotInAnExteriorRoom";
                return false;
            }

            Vector3 actorPosition = actor.Position;
            float sceneDistance = Distance2D(actorPosition, scene.SceneCenter);
            if (float.IsNaN(sceneDistance) || float.IsInfinity(sceneDistance)
                || sceneDistance > DispatchVictimMaximumSceneDistance + 6f)
            {
                reason = "Stage=ExteriorActorSceneRange; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; Position=" + actorPosition
                    + "; SceneCenter=" + scene.SceneCenter
                    + "; Distance=" + sceneDistance.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; Result=ActorOutsideLocalDispatchSite";
                return false;
            }

            if (actor.IsInVehicle())
            {
                Vehicle actorVehicle = null;
                try { actorVehicle = actor.CurrentVehicle; }
                catch { }
                if (actorVehicle == null || !actorVehicle.Exists())
                {
                    reason = "Stage=ExteriorActorVehicle; Role=" + (role ?? string.Empty)
                        + "; Handle=" + actor.Handle
                        + "; Result=VehicleOccupantIdentityUnavailable";
                    return false;
                }
                evidence = "Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; Position=" + actorPosition
                    + "; EntityInteriorId=0; CoordinateInteriorId=0; RoomKey=0"
                    + "; VehicleOccupant=true; Vehicle=" + actorVehicle.Handle
                    + "; VehiclePlacement=ValidatedByDispatchVehicleOwner";
                validatedGroundPosition = actorPosition;
                return true;
            }

            var excludedHandles = new HashSet<int>((excludedPedHandles
                ?? Enumerable.Empty<int>()).Where(handle => handle > 0));
            excludedHandles.Add(actor.Handle);
            Vector3 safePosition;
            Vector3 approachPosition;
            string placementEvidence;
            if (!TryValidateDispatchExteriorPosition(
                actorPosition,
                0.75f,
                scene.ExteriorRoadHeightTolerance,
                scene.AuthoredPosition,
                scene.AreaAnchorPosition,
                scene.MinimumExteriorOpenFootprintDirections,
                excludedHandles,
                out safePosition,
                out approachPosition,
                out placementEvidence,
                out reason,
                true))
            {
                reason = "Stage=ExteriorActorPlacement; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle + "; Position=" + actorPosition
                    + "; " + reason;
                return false;
            }
            validatedGroundPosition = safePosition;

            float heightAboveGround;
            bool inAir;
            try
            {
                heightAboveGround = actor.HeightAboveGround;
                inAir = actor.IsInAir;
            }
            catch (Exception ex)
            {
                reason = "Stage=ExteriorActorGroundAlignment; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; Native=GET_ENTITY_HEIGHT_ABOVE_GROUND/IS_ENTITY_IN_AIR"
                    + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }

            if (!IsDispatchExteriorActorGroundAlignmentValid(
                    actorPosition,
                    safePosition,
                    heightAboveGround,
                    inAir,
                    DispatchExteriorActorMaximumHorizontalGroundOffset,
                    DispatchExteriorActorMaximumOriginHeightAboveGround,
                    DispatchExteriorActorMaximumOriginBelowGround))
            {
                reason = "Stage=ExteriorActorGroundAlignment; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; Position=" + actorPosition
                    + "; GroundPosition=" + safePosition
                    + "; HorizontalDisplacement=" + Distance2D(actorPosition, safePosition).ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; OriginGroundOffset=" + (actorPosition.Z - safePosition.Z).ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; HeightAboveGround=" + heightAboveGround.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; IsInAir=" + inAir
                    + "; MaximumHorizontalOffset="
                    + DispatchExteriorActorMaximumHorizontalGroundOffset.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "; MaximumOriginHeightAboveGround="
                    + DispatchExteriorActorMaximumOriginHeightAboveGround.ToString(
                        "0.00", CultureInfo.InvariantCulture);
                return false;
            }

            evidence = "Role=" + (role ?? string.Empty)
                + "; Handle=" + actor.Handle
                + "; Position=" + actorPosition
                + "; EntityInteriorId=0; CoordinateInteriorId=0; RoomKey=0"
                + "; SceneDistance=" + sceneDistance.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; HeightAboveGround=" + heightAboveGround.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; IsInAir=" + inAir
                + "; GroundPosition=" + safePosition
                + "; OutdoorApproach=" + approachPosition
                + "; " + placementEvidence;
            return true;
        }

        internal bool TryValidatePreparedDispatchExteriorActor(
            Ped actor,
            string role,
            AmbientDispatchSceneResolution scene,
            Vector3 preparedPosition,
            Vector3 preparedGroundPosition,
            out string evidence,
            out string reason)
        {
            evidence = string.Empty;
            reason = string.Empty;
            if (actor == null || !actor.Exists() || scene == null || scene.IsInteriorScene)
            {
                reason = "Stage=PreparedExteriorActorIdentity; Result=ActorOrExteriorSceneUnavailable";
                return false;
            }

            Vector3 actorPosition = actor.Position;
            float horizontalDrift = Distance2D(actorPosition, preparedPosition);
            float verticalDrift = Math.Abs(actorPosition.Z - preparedPosition.Z);
            if (float.IsNaN(horizontalDrift) || float.IsInfinity(horizontalDrift)
                || horizontalDrift > 0.75f
                || float.IsNaN(verticalDrift) || float.IsInfinity(verticalDrift)
                || verticalDrift > 1.0f)
            {
                reason = "Stage=PreparedExteriorActorContinuity; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; PreparedPosition=" + preparedPosition
                    + "; CurrentPosition=" + actorPosition
                    + "; HorizontalDrift=" + horizontalDrift.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; VerticalDrift=" + verticalDrift.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Result=PreparedActorMoved";
                return false;
            }

            int entityInteriorId;
            int coordinateInteriorId;
            int roomKey;
            try
            {
                entityInteriorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, actor);
                coordinateInteriorId = GetInteriorAt(actorPosition);
                roomKey = Function.Call<int>(Hash.GET_ROOM_KEY_FROM_ENTITY, actor);
            }
            catch (Exception ex)
            {
                reason = "Stage=PreparedExteriorActorIdentity; Native=GET_INTERIOR_FROM_ENTITY/"
                    + "GET_INTERIOR_AT_COORDS/GET_ROOM_KEY_FROM_ENTITY; Result=Exception:"
                    + ex.GetType().Name;
                return false;
            }
            if (!IsDispatchExteriorEntityIdentityValid(
                entityInteriorId, coordinateInteriorId, roomKey))
            {
                reason = "Stage=PreparedExteriorActorIdentity; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; EntityInteriorId=" + entityInteriorId
                    + "; CoordinateInteriorId=" + coordinateInteriorId
                    + "; RoomKey=" + roomKey
                    + "; Result=PreparedActorLeftExterior";
                return false;
            }

            float sceneDistance = Distance2D(actorPosition, scene.SceneCenter);
            if (float.IsNaN(sceneDistance) || float.IsInfinity(sceneDistance)
                || sceneDistance > DispatchVictimMaximumSceneDistance + 6f)
            {
                reason = "Stage=PreparedExteriorActorSceneRange; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; Distance=" + sceneDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Result=ActorOutsideLocalDispatchSite";
                return false;
            }

            if (actor.IsInVehicle())
            {
                Vehicle actorVehicle = null;
                try { actorVehicle = actor.CurrentVehicle; }
                catch { }
                if (actorVehicle == null || !actorVehicle.Exists())
                {
                    reason = "Stage=PreparedExteriorActorVehicle; Role=" + (role ?? string.Empty)
                        + "; Handle=" + actor.Handle
                        + "; Result=VehicleOccupantIdentityUnavailable";
                    return false;
                }
                evidence = "Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; Position=" + actorPosition
                    + "; Vehicle=" + actorVehicle.Handle
                    + "; PreparedSnapshot=true; FreshSafeCoordinateQuery=false";
                return true;
            }

            float heightAboveGround;
            bool inAir;
            try
            {
                heightAboveGround = actor.HeightAboveGround;
                inAir = actor.IsInAir;
            }
            catch (Exception ex)
            {
                reason = "Stage=PreparedExteriorActorGroundAlignment; Handle=" + actor.Handle
                    + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }
            if (!IsDispatchExteriorActorGroundAlignmentValid(
                actorPosition,
                preparedGroundPosition,
                heightAboveGround,
                inAir,
                DispatchExteriorActorMaximumHorizontalGroundOffset,
                DispatchExteriorActorMaximumOriginHeightAboveGround,
                DispatchExteriorActorMaximumOriginBelowGround))
            {
                reason = "Stage=PreparedExteriorActorGroundAlignment; Role=" + (role ?? string.Empty)
                    + "; Handle=" + actor.Handle
                    + "; PreparedGroundPosition=" + preparedGroundPosition
                    + "; Position=" + actorPosition
                    + "; HeightAboveGround=" + heightAboveGround.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; IsInAir=" + inAir
                    + "; Result=PreparedActorNoLongerGrounded";
                return false;
            }

            evidence = "Role=" + (role ?? string.Empty)
                + "; Handle=" + actor.Handle
                + "; Position=" + actorPosition
                + "; PreparedPosition=" + preparedPosition
                + "; PreparedGroundPosition=" + preparedGroundPosition
                + "; EntityInteriorId=0; CoordinateInteriorId=0; RoomKey=0"
                + "; SceneDistance=" + sceneDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + "; HeightAboveGround=" + heightAboveGround.ToString("0.00", CultureInfo.InvariantCulture)
                + "; IsInAir=" + inAir
                + "; PreparedSnapshot=true; FreshSafeCoordinateQuery=false";
            return true;
        }

        private static bool IsSeparatedFromReservedPositions(
            Vector3 candidate,
            IEnumerable<Vector3> reservedPositions,
            float minimumSpacing)
        {
            return reservedPositions == null || !reservedPositions.Any(position =>
                Distance2D(candidate, position) < minimumSpacing);
        }

        private bool TryResolveDispatchVictimPosition(
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            IEnumerable<Vector3> reservedSuspectPositions,
            out Vector3 safePosition,
            out string reason)
        {
            if (TryResolveDispatchParticipantPosition(
                    preferredPosition, scene, reservedSuspectPositions,
                    out safePosition, out reason))
                return true;

            // Keep interior victims on the validated mapped interior path. The
            // wider search is only for exterior scenes and remains anchored to
            // the already validated scene center; each point still passes the
            // same ground, slope, road, interior, and occupancy checks.
            if (scene == null || scene.IsInteriorScene)
                return false;

            string lastFailure = reason;
            for (int index = 0; index < DispatchVictimFallbackDirections.Length; index++)
            {
                Vector2 offset = DispatchVictimFallbackDirections[index];
                Vector3 candidate = new Vector3(
                    preferredPosition.X + offset.X,
                    preferredPosition.Y + offset.Y,
                    preferredPosition.Z);
                Vector3 resolved;
                Vector3 approachPosition;
                string exteriorEvidence;
                string candidateFailure;
                if (!TryValidateDispatchExteriorPosition(
                        candidate,
                        DispatchVictimFallbackCorrectionDistance,
                        scene == null ? 0f : scene.ExteriorRoadHeightTolerance,
                        scene.AuthoredPosition,
                        scene.AreaAnchorPosition,
                        scene.MinimumExteriorOpenFootprintDirections,
                        null,
                        out resolved,
                        out approachPosition,
                        out exteriorEvidence,
                        out candidateFailure))
                {
                    lastFailure = candidateFailure;
                    continue;
                }

                if (Distance2D(resolved, scene.SceneCenter)
                    > DispatchVictimMaximumSceneDistance)
                {
                    lastFailure = "The safe victim point is outside the validated local scene radius.";
                    continue;
                }
                if (!IsSeparatedFromReservedPositions(
                    resolved, reservedSuspectPositions, DispatchCivilianMinimumSuspectDistance))
                {
                    lastFailure = "The safe victim point is too close to a planned suspect position.";
                    continue;
                }

                safePosition = resolved;
                reason = string.Empty;
                return true;
            }

            safePosition = preferredPosition;
            reason = "No safe exterior victim point was found within "
                + DispatchVictimMaximumSceneDistance.ToString("0.##", CultureInfo.InvariantCulture)
                + "m of the validated scene. Last check: " + lastFailure;
            return false;
        }

        internal bool TryCreateDispatchSceneVictim(
            string incidentId,
            string victimPoolId,
            Model model,
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            bool startsDead,
            IEnumerable<Vector3> reservedSuspectPositions,
            out Ped victim,
            out string reason)
        {
            return TryCreateDispatchSceneParticipant(
                incidentId,
                "Victim",
                victimPoolId,
                model,
                preferredPosition,
                scene,
                startsDead,
                reservedSuspectPositions,
                out victim,
                out reason);
        }

        internal bool TryCreateDispatchSceneVehicle(
            string incidentId,
            Model model,
            AmbientDispatchSceneResolution scene,
            out Vehicle vehicle,
            out string reason)
        {
            vehicle = null;
            reason = "The occurrence, loaded vehicle model, or validated roadside scene is unavailable.";
            if (string.IsNullOrWhiteSpace(incidentId)
                || scene == null
                || scene.IsInteriorScene
                || !scene.HasVehicleStagingPosition
                || !model.IsValid
                || !model.IsInCdImage
                || !model.IsLoaded)
            {
                reason = "Stage=VehicleModelOrScene; Model="
                    + (model == null ? "null" : model.Hash.ToString(CultureInfo.InvariantCulture))
                    + "; Loaded=" + (model != null && model.IsLoaded)
                    + "; HasStaging=" + (scene != null && scene.HasVehicleStagingPosition)
                    + "; Interior=" + (scene != null && scene.IsInteriorScene);
                return false;
            }

            bool movingRoadStaging = string.Equals(scene.VehicleStagingMode,
                "moving_road", StringComparison.OrdinalIgnoreCase);
            bool movingRoadRouteConfirmed = !movingRoadStaging
                || (scene.HasVehicleRoute
                    && scene.VehicleRoutePosition != Vector3.Zero);
            if (!movingRoadRouteConfirmed)
            {
                reason = "Stage=VehicleRouteEvidence; RoutePosition="
                    + scene.VehicleRoutePosition
                    + "; HasVehicleRoute=" + scene.HasVehicleRoute
                    + "; Result=Rejected";
                Log("AMBIENT_WORLD_DISPATCH_VEHICLE_PLACEMENT_REJECTED",
                    "Incident=" + incidentId + "; " + reason);
                return false;
            }

            Vector3 staging = scene.VehicleStagingPosition;
            int interiorId;
            bool stagingOnRoad;
            float groundZ;
            Vector3 groundNormal;
            bool groundFound;
            try
            {
                interiorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS, staging.X, staging.Y, staging.Z);
                stagingOnRoad = Function.Call<bool>(
                    Hash.IS_POINT_ON_ROAD, staging.X, staging.Y, staging.Z, 0);
                groundFound = World.GetGroundHeightAndNormal(
                    staging, out groundZ, out groundNormal);
            }
            catch (Exception ex)
            {
                reason = "Stage=VehicleGroundProbe; Native=GET_INTERIOR_AT_COORDS/"
                    + "GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; Result=Exception:"
                    + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_VEHICLE_PLACEMENT_REJECTED",
                    "Incident=" + incidentId + "; " + reason);
                return false;
            }

            bool playerRadiusParkedStaging = !movingRoadStaging
                && string.Equals(scene.LocationCategory, "PlayerRadiusRoadNode",
                    StringComparison.OrdinalIgnoreCase);
            Vector3 confirmedRoadNode = Vector3.Zero;
            float confirmedRoadHeading = 0f;
            float confirmedRoadSnapDistance = float.MaxValue;
            string roadNodeConfirmationFailure = "NotRequired";
            bool nativeRoadNodeConfirmed = false;
            if (movingRoadStaging && (!stagingOnRoad || !groundFound))
            {
                nativeRoadNodeConfirmed = TryGetClosestDispatchVehicleRoadNode(
                    staging,
                    DispatchVehicleGroundFallbackRoadNodeSnapDistance,
                    DispatchVehicleGroundFallbackElevationTolerance,
                    "VehiclePlacementRoadNodeConfirmation",
                    out confirmedRoadNode,
                    out confirmedRoadHeading,
                    out confirmedRoadSnapDistance,
                    out roadNodeConfirmationFailure,
                    true)
                    && confirmedRoadNode != Vector3.Zero
                    && !float.IsNaN(confirmedRoadHeading)
                    && !float.IsInfinity(confirmedRoadHeading)
                    && confirmedRoadSnapDistance
                        <= DispatchVehicleGroundFallbackRoadNodeSnapDistance
                    && Math.Abs(confirmedRoadNode.Z - staging.Z)
                        <= DispatchVehicleGroundFallbackElevationTolerance
                    && GetDispatchHeadingDifferenceDegrees(
                        scene.VehicleHeading, confirmedRoadHeading)
                        <= DispatchVehicleGroundFallbackHeadingTolerance;
            }
            bool movingRoadPositionConfirmed = movingRoadStaging
                && movingRoadRouteConfirmed
                && (stagingOnRoad || nativeRoadNodeConfirmed);
            bool nativeRoadNodeGroundFallbackConfirmed = movingRoadStaging
                && movingRoadRouteConfirmed
                && !groundFound
                && interiorId == 0
                && nativeRoadNodeConfirmed;
            if (movingRoadStaging && interiorId == 0
                && movingRoadPositionConfirmed && !groundFound)
            {
                bool collisionRequested = false;
                string collisionRequestFailure = string.Empty;
                try
                {
                    Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                        staging.X, staging.Y, staging.Z);
                    collisionRequested = true;
                }
                catch (Exception ex)
                {
                    collisionRequestFailure = "Exception:" + ex.GetType().Name;
                }

                if (!nativeRoadNodeGroundFallbackConfirmed && collisionRequested)
                {
                    reason = "Stage=VehicleGroundProbe; Native=GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD/"
                        + "REQUEST_COLLISION_AT_COORD; Position=" + staging
                        + "; OnRoad=" + stagingOnRoad
                        + "; NativeRoadNodeConfirmed=" + nativeRoadNodeConfirmed
                        + "; RoadNode=" + (nativeRoadNodeConfirmed
                            ? confirmedRoadNode.ToString() : "Unavailable")
                        + "; RoadNodeSnapDistance="
                        + (nativeRoadNodeConfirmed
                            ? confirmedRoadSnapDistance.ToString("0.00", CultureInfo.InvariantCulture)
                            : "Unavailable")
                        + "; RoadNodeHeading="
                        + (nativeRoadNodeConfirmed
                            ? confirmedRoadHeading.ToString("0.0", CultureInfo.InvariantCulture)
                            : "Unavailable")
                        + "; GroundFound=false; Result=CollisionPending";
                    LogDebugTransition(
                        "AMBIENT_WORLD_DISPATCH_VEHICLE_PLACEMENT_PENDING",
                        "Stage=VehicleGroundProbe;OnRoad=" + stagingOnRoad
                            + ";NativeRoadNodeConfirmed=false;GroundFound=false",
                        "Incident=" + incidentId + "; " + reason);
                    return false;
                }

                if (!nativeRoadNodeGroundFallbackConfirmed)
                {
                    reason = "Stage=VehicleGroundProbe; Native=REQUEST_COLLISION_AT_COORD; Position="
                        + staging + "; OnRoad=" + stagingOnRoad
                        + "; NativeRoadNodeConfirmed=" + nativeRoadNodeConfirmed
                        + "; RoadNodeConfirmation=" + roadNodeConfirmationFailure
                        + "; Result=Rejected; CollisionRequest=" + collisionRequestFailure;
                    Log("AMBIENT_WORLD_DISPATCH_VEHICLE_PLACEMENT_REJECTED",
                        "Incident=" + incidentId + "; " + reason);
                    return false;
                }
            }

            bool groundedByConfirmedRoadNode = nativeRoadNodeGroundFallbackConfirmed
                && confirmedRoadNode != Vector3.Zero
                && confirmedRoadSnapDistance
                    <= DispatchVehicleGroundFallbackRoadNodeSnapDistance
                && Math.Abs(confirmedRoadNode.Z - staging.Z)
                    <= DispatchVehicleGroundFallbackElevationTolerance
                && GetDispatchHeadingDifferenceDegrees(
                    scene.VehicleHeading, confirmedRoadHeading)
                    <= DispatchVehicleGroundFallbackHeadingTolerance;
            float validatedGroundZ = groundFound ? groundZ : confirmedRoadNode.Z;
            if (interiorId != 0
                || !movingRoadRouteConfirmed
                || (!groundFound && !groundedByConfirmedRoadNode)
                || (groundFound && (float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || float.IsNaN(groundNormal.Z) || float.IsInfinity(groundNormal.Z)
                    || groundNormal.Z < 0.65f
                    || Math.Abs(staging.Z - groundZ) > 3f))
                || (movingRoadStaging && !movingRoadPositionConfirmed))
            {
                reason = "Stage=VehicleGroundProbe; Native=GET_INTERIOR_AT_COORDS/"
                    + "IS_POINT_ON_ROAD/GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; InteriorId=" + interiorId
                    + "; OnRoad=" + stagingOnRoad
                    + "; NativeRoadNodeConfirmed=" + nativeRoadNodeConfirmed
                    + "; RoadNodeConfirmation=" + roadNodeConfirmationFailure
                    + "; Position=" + staging
                    + "; GroundFound=" + groundFound
                    + "; GroundedByFreshRoadNode=" + groundedByConfirmedRoadNode
                    + "; ValidatedGroundZ=" + validatedGroundZ.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; GroundZ=" + groundZ.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; NormalZ=" + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Result=Rejected";
                Log("AMBIENT_WORLD_DISPATCH_VEHICLE_PLACEMENT_REJECTED",
                    "Incident=" + incidentId + "; " + reason);
                return false;
            }

            ClearDebugTransition("AMBIENT_WORLD_DISPATCH_VEHICLE_PLACEMENT_PENDING");
            staging.Z = validatedGroundZ;
            bool overlappingVehicle = HasOverlappingVehicle(staging, 2.35f);
            bool overlappingPed = HasOverlappingPed(staging, 2.0f);
            if (overlappingVehicle || overlappingPed)
            {
                reason = "Stage=VehicleOccupancy; Result="
                    + (overlappingVehicle ? "NearbyVehicle" : "NearbyPed")
                    + "; ProbeRadius=2.35/2.0";
                Log("AMBIENT_WORLD_DISPATCH_VEHICLE_PLACEMENT_REJECTED",
                    "Incident=" + incidentId + "; " + reason);
                return false;
            }

            try
            {
                vehicle = World.CreateVehicle(model, staging, scene.VehicleHeading);
                if (vehicle == null || !vehicle.Exists())
                {
                    vehicle = null;
                    reason = "Stage=VehicleCreation; Native=CREATE_VEHICLE; Result=NoEntity";
                    Log("AMBIENT_WORLD_DISPATCH_VEHICLE_CREATE_FAILED",
                        "Incident=" + incidentId + "; Model=" + model.Hash + "; " + reason);
                    return false;
                }

                vehicle.IsPersistent = true;
                vehicle.PlaceOnGround();
                int placedInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    vehicle.Position.X, vehicle.Position.Y, vehicle.Position.Z);
                bool placedOnRoad = Function.Call<bool>(
                    Hash.IS_POINT_ON_ROAD,
                    vehicle.Position.X, vehicle.Position.Y, vehicle.Position.Z, 0);
                Vector3 confirmedPlacedRoadNode = Vector3.Zero;
                float confirmedPlacedRoadHeading = 0f;
                float confirmedPlacedRoadSnapDistance = float.MaxValue;
                string placedRoadNodeFailure = "NotRequired";
                bool placedRoadNodeConfirmed = false;
                if (movingRoadStaging && !placedOnRoad)
                {
                    placedRoadNodeConfirmed = TryGetClosestDispatchVehicleRoadNode(
                        vehicle.Position,
                        DispatchVehicleGroundFallbackRoadNodeSnapDistance,
                        DispatchVehicleGroundFallbackElevationTolerance,
                        "PlacedVehicleRoadNodeConfirmation",
                        out confirmedPlacedRoadNode,
                        out confirmedPlacedRoadHeading,
                        out confirmedPlacedRoadSnapDistance,
                        out placedRoadNodeFailure,
                        true)
                        && movingRoadRouteConfirmed
                        && confirmedPlacedRoadNode != Vector3.Zero
                        && !float.IsNaN(confirmedPlacedRoadHeading)
                        && !float.IsInfinity(confirmedPlacedRoadHeading)
                        && confirmedPlacedRoadSnapDistance
                            <= DispatchVehicleGroundFallbackRoadNodeSnapDistance
                        && Math.Abs(confirmedPlacedRoadNode.Z - vehicle.Position.Z)
                            <= DispatchVehicleGroundFallbackElevationTolerance
                        && GetDispatchHeadingDifferenceDegrees(
                            scene.VehicleHeading, confirmedPlacedRoadHeading)
                            <= DispatchVehicleGroundFallbackHeadingTolerance;
                }
                float placedRoadsideSeparation = float.MaxValue;
                if (playerRadiusParkedStaging)
                {
                    float placedRoadHeading;
                    Vector3 placedRoadNode = World.GetNextPositionOnStreetWithHeading(
                        vehicle.Position, out placedRoadHeading);
                    if (placedRoadNode != Vector3.Zero)
                        placedRoadsideSeparation = Distance2D(vehicle.Position, placedRoadNode);
                }
                if (placedInteriorId != 0
                    || (movingRoadStaging && !placedOnRoad && !placedRoadNodeConfirmed)
                    || (playerRadiusParkedStaging
                        && (placedRoadsideSeparation < 1.25f
                            || placedRoadsideSeparation > 14f))
                    || vehicle.Position.DistanceTo(staging) > 6f)
                {
                    int rejectedHandle = vehicle.Handle;
                    float stagingDistance = vehicle.Position.DistanceTo(staging);
                    try { vehicle.Delete(); } catch { }
                    vehicle = null;
                    reason = "Stage=VehiclePlacementRevalidation; Native=GET_INTERIOR_AT_COORDS/IS_POINT_ON_ROAD/"
                        + "ENTITY_COORDS; PlacedInteriorId=" + placedInteriorId
                        + "; PlacedOnRoad=" + placedOnRoad
                        + "; PlacedRoadNodeConfirmed=" + placedRoadNodeConfirmed
                        + "; PlacedRoadNode=" + (placedRoadNodeConfirmed
                            ? confirmedPlacedRoadNode.ToString() : "Unavailable")
                        + "; PlacedRoadNodeConfirmation=" + placedRoadNodeFailure
                        + "; PlacedRoadsideSeparation=" + placedRoadsideSeparation
                        + "; StagingDistance=" + stagingDistance
                            .ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Vehicle=" + rejectedHandle + "; Result=Rejected";
                    Log("AMBIENT_WORLD_DISPATCH_VEHICLE_PLACEMENT_REJECTED",
                        "Incident=" + incidentId + "; " + reason);
                    return false;
                }

                reason = string.Empty;
                Log("AMBIENT_WORLD_DISPATCH_VEHICLE_CREATED",
                    "Incident=" + incidentId
                    + "; Vehicle=" + vehicle.Handle
                    + "; Model=" + model.Hash
                    + "; Position=" + vehicle.Position
                    + "; Heading=" + scene.VehicleHeading.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; OnRoad=" + placedOnRoad
                    + "; NativeRoadNodeConfirmed=" + placedRoadNodeConfirmed
                    + "; StagingGroundEvidence=" + (groundFound
                        ? "GroundProbe" : groundedByConfirmedRoadNode
                            ? "FreshNativeRoadNode" : "Unavailable")
                    + "; CatalogLocation=" + (scene.LocationId ?? string.Empty)
                    + "; Owner=DispatchOccurrence");
                return true;
            }
            catch (Exception ex)
            {
                if (vehicle != null)
                {
                    try { if (vehicle.Exists()) vehicle.Delete(); } catch { }
                    vehicle = null;
                }
                reason = "Stage=VehicleCreation; Native=CREATE_VEHICLE/PLACE_VEHICLE_ON_GROUND; Result=Exception:"
                    + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_VEHICLE_CREATE_FAILED",
                    "Incident=" + incidentId + "; Model=" + model.Hash + "; " + reason);
                return false;
            }
        }

        private static float GetDispatchHeadingDifferenceDegrees(
            float firstHeading,
            float secondHeading)
        {
            if (float.IsNaN(firstHeading) || float.IsInfinity(firstHeading)
                || float.IsNaN(secondHeading) || float.IsInfinity(secondHeading))
                return float.MaxValue;
            float difference = Math.Abs((firstHeading - secondHeading) % 360f);
            return difference > 180f ? 360f - difference : difference;
        }

        internal bool TryCreateDispatchSceneParticipant(
            string incidentId,
            string participantRole,
            string participantPoolId,
            Model model,
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            bool startsDead,
            IEnumerable<Vector3> reservedParticipantPositions,
            out Ped participant,
            out string reason)
        {
            participant = null;
            string role = string.IsNullOrWhiteSpace(participantRole)
                ? "Participant" : participantRole.Trim();
            reason = "The participant model, pool, or validated scene is unavailable.";
            if (string.IsNullOrWhiteSpace(incidentId)
                || string.IsNullOrWhiteSpace(participantPoolId)
                || _npcDatabase == null
                || !_npcDatabase.IsLoaded
                || !model.IsValid
                || !model.IsInCdImage
                || !model.IsLoaded
                || !_npcDatabase.IsDispatchPedModelInPool(participantPoolId, model.Hash)
                || scene == null)
                return false;

            Vector3 safePosition;
            if (!TryResolveDispatchVictimPosition(
                preferredPosition,
                scene,
                reservedParticipantPositions,
                out safePosition,
                out reason))
                return false;

            try
            {
                participant = World.CreatePed(model, safePosition);
                if (participant == null || !participant.Exists())
                {
                    reason = "GTA did not create the validated Dispatch scene participant.";
                    return false;
                }

                Function.Call(Hash.SET_ENTITY_VISIBLE, participant, false, false);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, participant, true);
                participant.BlockPermanentEvents = true;
                participant.IsPositionFrozen = true;

                if (scene.IsInteriorScene)
                {
                    int actorLiveInteriorId;
                    bool actorInteriorResult = TryGetActorInteriorId(
                        participant, out actorLiveInteriorId);
                    if (!actorInteriorResult
                        || actorLiveInteriorId != scene.LiveInteriorId)
                    {
                        reason = "Stage=InteriorParticipantIdentity; ExpectedLiveInteriorId="
                            + scene.LiveInteriorId
                            + "; ActorLiveInteriorId=" + actorLiveInteriorId
                            + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                            + "; Result=" + (actorInteriorResult
                                ? actorLiveInteriorId.ToString(CultureInfo.InvariantCulture)
                                : "Unavailable")
                            + "; CatalogInteriorId=" + scene.CatalogInteriorId;
                        try { if (participant.Exists()) participant.Delete(); } catch { }
                        participant = null;
                        return false;
                    }
                }

                try
                {
                    Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, participant, false);
                    if (Function.Call<bool>(Hash.IS_PED_ARMED, participant, 7))
                    {
                        reason = "Stage=ParticipantRoleWeapons; Role=" + role
                            + "; Handle=" + participant.Handle
                            + "; Native=REMOVE_ALL_PED_WEAPONS/IS_PED_ARMED(7)"
                            + "; Result=GeneratedNonSuspectStillArmed";
                        try { if (participant.Exists()) participant.Delete(); } catch { }
                        participant = null;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    reason = "Stage=ParticipantRoleWeapons; Role=" + role
                        + "; Native=REMOVE_ALL_PED_WEAPONS/IS_PED_ARMED(7)"
                        + "; Result=VerificationUnavailable; Exception=" + ex.GetType().Name;
                    try { if (participant.Exists()) participant.Delete(); } catch { }
                    participant = null;
                    return false;
                }

                participant.IsPersistent = true;
                participant.BlockPermanentEvents = true;
                if (startsDead)
                    Function.Call(Hash.SET_ENTITY_HEALTH, participant, 0, 0);

                if (!LSPDDispatchEventDefinition.IsVictimStateReady(
                    startsDead, participant.IsDead, participant.Health))
                {
                    reason = startsDead
                        ? "The created scene participant did not enter the catalog-defined dead state."
                        : "The created scene participant is not alive.";
                    try { if (participant.Exists()) participant.Delete(); } catch { }
                    participant = null;
                    return false;
                }

                Log(string.Equals(role, "Victim", StringComparison.OrdinalIgnoreCase)
                        ? "AMBIENT_WORLD_DISPATCH_SCENE_VICTIM_CREATED"
                        : "AMBIENT_WORLD_DISPATCH_SCENE_PARTICIPANT_CREATED",
                    "Incident=" + incidentId
                    + "; Role=" + role
                    + "; Ped=" + participant.Handle
                    + "; Pool=" + participantPoolId
                    + "; Model=" + participant.Model.Hash
                    + "; WeaponState=VerifiedUnarmed"
                    + "; StartsDead=" + startsDead
                    + "; Interior=" + scene.IsInteriorScene
                    + "; CatalogInteriorId=" + scene.CatalogInteriorId
                    + "; LiveInteriorId=" + scene.LiveInteriorId
                    + "; ActorLiveInteriorId=" + (scene.IsInteriorScene
                        ? scene.LiveInteriorId : 0)
                    + "; Position=" + participant.Position
                    + "; HiddenUntilAcceptance=true");
                return true;
            }
            catch (Exception ex)
            {
                if (participant != null)
                {
                    try { if (participant.Exists()) participant.Delete(); } catch { }
                    participant = null;
                }
                reason = "Dispatch scene participant creation failed: " + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_SCENE_PARTICIPANT_CREATE_FAILED",
                    "Incident=" + incidentId + "; Role=" + role + "; Pool=" + participantPoolId
                    + "; Reason=" + reason);
                return false;
            }
        }

        /// <summary>
        /// Claims one safe, nearby ambient civilian as a Dispatch scene victim.
        /// The scan is limited to a single scene-preparation call and excludes
        /// registered actors, mission entities, active scenarios, and actors in
        /// protected gameplay states. The incident's assigned owner controls
        /// behavior while the claim is active; ReleaseDispatchCivilian restores
        /// the original entity flags without deleting the ambient Ped.
        /// </summary>
        internal bool TryClaimDispatchCivilian(
            string incidentId,
            string victimPoolId,
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            IEnumerable<Ped> protectedActors,
            IEnumerable<Vector3> reservedSuspectPositions,
            bool keepWithinPreparedScene,
            out Ped civilian,
            out string reason)
        {
            civilian = null;
            reason = "No safe ambient civilian was found at the prepared scene.";
            if (string.IsNullOrWhiteSpace(incidentId)
                || string.IsNullOrWhiteSpace(victimPoolId)
                || _npcDatabase == null
                || !_npcDatabase.IsLoaded
                || scene == null)
            {
                reason = "The Dispatch occurrence, victim pool, loaded NPC catalog, or validated scene is missing.";
                return false;
            }

            if (scene.IsInteriorScene)
            {
                if (scene.LiveInteriorId == 0
                    || GetInteriorAt(preferredPosition) != scene.LiveInteriorId)
                {
                    reason = "The requested victim position is outside the validated interior.";
                    return false;
                }
            }
            else if (GetInteriorAt(preferredPosition) != 0)
            {
                reason = "The requested victim position is inside an unvalidated interior.";
                return false;
            }

            var protectedHandles = new HashSet<int>();
            var protectedPositions = new List<Vector3>();
            if (protectedActors != null)
            {
                foreach (Ped actor in protectedActors)
                {
                    if (actor == null || !actor.Exists())
                        continue;
                    protectedHandles.Add(actor.Handle);
                    protectedPositions.Add(actor.Position);
                }
            }
            if (reservedSuspectPositions != null)
                protectedPositions.AddRange(reservedSuspectPositions);

            Ped[] nearby;
            try
            {
                nearby = World.GetNearbyPeds(preferredPosition, DispatchCivilianSearchRadius);
            }
            catch (Exception ex)
            {
                reason = "The local ambient Ped query failed: " + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_QUERY_FAILED", reason);
                return false;
            }

            if (nearby == null || nearby.Length == 0)
            {
                Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_UNAVAILABLE",
                    "Incident=" + incidentId + "; Reason=NO_LOCAL_PED_CANDIDATES");
                return false;
            }

            Ped selected = null;
            float selectedDistance = float.MaxValue;
            int inspected = 0;
            int candidatesOutsideInteriorActivityRadius = 0;
            int visibleCandidatesSkipped = 0;
            foreach (Ped candidate in nearby)
            {
                if (inspected++ >= MaximumDispatchCivilianCandidatesToInspect)
                    break;
                if (!IsEligibleDispatchCivilian(
                    candidate,
                    preferredPosition,
                    scene,
                    protectedHandles,
                    protectedPositions))
                    continue;
                if ((scene.IsInteriorScene || keepWithinPreparedScene)
                    && Distance2D(candidate.Position, scene.SceneCenter)
                        > DispatchVictimMaximumSceneDistance)
                {
                    candidatesOutsideInteriorActivityRadius++;
                    continue;
                }
                if (!_npcDatabase.IsDispatchPedModelInPool(victimPoolId, candidate.Model.Hash))
                    continue;
                try
                {
                    if (Function.Call<bool>(Hash.IS_ENTITY_ON_SCREEN, candidate))
                    {
                        visibleCandidatesSkipped++;
                        continue;
                    }
                }
                catch
                {
                    // If visibility cannot be queried, retain the existing
                    // role and distance gates instead of rejecting a valid Ped.
                }

                float distance = Distance2D(candidate.Position, preferredPosition);
                if (distance < selectedDistance)
                {
                    selected = candidate;
                    selectedDistance = distance;
                }
            }

            if (selected == null)
            {
                Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_UNAVAILABLE",
                    "Incident=" + incidentId
                    + "; CandidatesInspected=" + inspected
                    + "; OutsideInteriorActivityRadius=" + candidatesOutsideInteriorActivityRadius
                    + "; VisibleCandidatesSkipped=" + visibleCandidatesSkipped
                    + "; Reason=NO_SAFE_UNOWNED_CIVILIAN");
                return false;
            }

            DispatchCivilianClaim claim = null;
            try
            {
                bool wasVisible = Function.Call<bool>(Hash.IS_ENTITY_VISIBLE, selected);
                claim = new DispatchCivilianClaim
                {
                    IncidentId = incidentId,
                    PedHandle = selected.Handle,
                    WasPersistent = selected.IsPersistent,
                    BlockedPermanentEvents = selected.BlockPermanentEvents,
                    WasPositionFrozen = selected.IsPositionFrozen,
                    WasVisible = wasVisible
                };
                _dispatchCivilianClaims.Add(selected.Handle, claim);
                selected.IsPersistent = true;
                selected.BlockPermanentEvents = true;
                Function.Call(Hash.SET_ENTITY_VISIBLE, selected, false, false);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, selected, true);
                selected.IsPositionFrozen = true;
                selected.Task.ClearAll();
                civilian = selected;
                Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_CLAIMED",
                    "Incident=" + incidentId
                    + "; Ped=" + selected.Handle
                    + "; Distance=" + selectedDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; OutsideInteriorActivityRadius=" + candidatesOutsideInteriorActivityRadius
                    + "; VisibleCandidatesSkipped=" + visibleCandidatesSkipped
                    + "; CatalogInteriorId=" + scene.CatalogInteriorId
                    + "; LiveInteriorId=" + scene.LiveInteriorId
                    + "; Location=" + (scene.LocationId ?? string.Empty));
                return true;
            }
            catch (Exception ex)
            {
                if (claim != null)
                {
                    _dispatchCivilianClaims.Remove(selected.Handle);
                    try
                    {
                        if (selected.Exists())
                        {
                            Function.Call(Hash.SET_ENTITY_VISIBLE, selected, claim.WasVisible, false);
                            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS,
                                selected, claim.BlockedPermanentEvents);
                            selected.IsPositionFrozen = claim.WasPositionFrozen;
                            selected.BlockPermanentEvents = claim.BlockedPermanentEvents;
                            selected.IsPersistent = claim.WasPersistent;
                        }
                    }
                    catch { }
                }
                reason = "The ambient civilian could not be claimed: " + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_CLAIM_FAILED", reason);
                return false;
            }
        }

        internal bool CompleteDispatchCivilianPreparation(Ped civilian, string incidentId)
        {
            if (civilian == null || !civilian.Exists()
                || string.IsNullOrWhiteSpace(incidentId))
                return false;

            DispatchCivilianClaim claim;
            if (!_dispatchCivilianClaims.TryGetValue(civilian.Handle, out claim)
                || claim == null
                || !string.Equals(claim.IncidentId, incidentId, StringComparison.Ordinal)
                || claim.PedHandle != civilian.Handle)
                return false;

            try
            {
                civilian.IsPositionFrozen = claim.WasPositionFrozen;
                civilian.BlockPermanentEvents = claim.BlockedPermanentEvents;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS,
                    civilian, claim.BlockedPermanentEvents);
                Function.Call(Hash.SET_ENTITY_VISIBLE, civilian, claim.WasVisible, false);
                return true;
            }
            catch (Exception ex)
            {
                Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_PREPARATION_RESTORE_FAILED",
                    "Incident=" + incidentId
                    + "; Ped=" + claim.PedHandle
                    + "; Reason=" + ex.GetType().Name);
                return false;
            }
        }

        internal bool ReleaseDispatchCivilian(Ped civilian, string incidentId)
        {
            if (civilian == null || string.IsNullOrWhiteSpace(incidentId))
                return false;

            DispatchCivilianClaim claim;
            if (!_dispatchCivilianClaims.TryGetValue(civilian.Handle, out claim)
                || claim == null
                || !string.Equals(claim.IncidentId, incidentId, StringComparison.Ordinal)
                || claim.PedHandle != civilian.Handle)
                return false;

            _dispatchCivilianClaims.Remove(civilian.Handle);
            if (civilian.Exists())
            {
                try
                {
                    if (!civilian.IsDead)
                        civilian.Task.ClearAll();
                    Function.Call(Hash.SET_ENTITY_VISIBLE, civilian, claim.WasVisible, false);
                    Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS,
                        civilian, claim.BlockedPermanentEvents);
                    civilian.IsPositionFrozen = claim.WasPositionFrozen;
                    civilian.BlockPermanentEvents = claim.BlockedPermanentEvents;
                    civilian.IsPersistent = claim.WasPersistent;
                }
                catch (Exception ex)
                {
                    Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_RESTORE_FAILED",
                        "Incident=" + incidentId
                        + "; Ped=" + claim.PedHandle
                        + "; Reason=" + ex.GetType().Name);
                    return false;
                }
            }

            Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_RELEASED",
                "Incident=" + incidentId + "; Ped=" + claim.PedHandle);
            return true;
        }

        private bool IsEligibleDispatchCivilian(
            Ped ped,
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            HashSet<int> protectedHandles,
            List<Vector3> protectedPositions)
        {
            if (ped == null || !ped.Exists() || ped.IsDead || !ped.IsHuman
                || ped.Health <= 0 || ped.IsPersistent || ped.BlockPermanentEvents
                || ped.IsInVehicle() || ped.IsInCombat || ped.IsShooting || ped.IsFleeing
                || ped.IsInPoliceVehicle || _actors.ContainsKey(ped.Handle)
                || _dispatchCivilianClaims.ContainsKey(ped.Handle)
                || protectedHandles.Contains(ped.Handle))
                return false;

            if (Distance2D(ped.Position, preferredPosition) > DispatchCivilianSearchRadius)
                return false;
            foreach (Vector3 protectedPosition in protectedPositions)
            {
                if (Distance2D(ped.Position, protectedPosition)
                    < DispatchCivilianMinimumSuspectDistance)
                    return false;
            }

            int interiorId = GetInteriorAt(ped.Position);
            if (scene.IsInteriorScene
                ? interiorId != scene.LiveInteriorId
                : interiorId != 0)
                return false;

            try
            {
                int model = ped.Model.Hash;
                if (model == unchecked((int)StringHash.AtStringHash("s_m_y_cop_01", 0))
                    || model == unchecked((int)StringHash.AtStringHash("s_f_y_cop_01", 0))
                    || model == unchecked((int)StringHash.AtStringHash("s_m_y_sheriff_01", 0))
                    || model == unchecked((int)StringHash.AtStringHash("s_f_y_sheriff_01", 0))
                    || Function.Call<bool>(Hash.IS_PED_ARMED, ped, 7))
                    return false;

                // Mission actors and scenario workers belong to the game or a
                // separate mission/script, even when their visible behavior is idle.
                if (Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, ped)
                    || Function.Call<bool>(Hash.IS_PED_USING_ANY_SCENARIO, ped))
                    return false;

                Vector3 safePosition;
                if (!World.GetSafePositionForPed(
                    ped.Position,
                    out safePosition,
                    (GetSafePositionFlags)0)
                    || safePosition.DistanceTo(ped.Position) > 1.25f
                    || GetInteriorAt(safePosition) != interiorId)
                    return false;
            }
            catch
            {
                // A candidate is skipped if its protection/navigation state
                // cannot be checked reliably.
                return false;
            }

            return true;
        }

        internal bool TryResolveInteriorNearbyPedPosition(
            Vector3 preferredPosition,
            int expectedInteriorId,
            IEnumerable<Vector3> reservedPositions,
            out Vector3 safePosition,
            out string reason)
        {
            safePosition = Vector3.Zero;
            reason = "No clear walkable floor was found at the requested interior participant position.";
            if (expectedInteriorId == 0)
            {
                reason = "The requested participant position is not inside the expected live interior.";
                return false;
            }
            int preferredInteriorId = GetInteriorAt(preferredPosition);
            if (preferredInteriorId == 0)
            {
                try { RequestInteriorCollision(preferredPosition); }
                catch (Exception ex)
                {
                    reason = "Stage=InteriorParticipantIdentity; ExpectedLiveInteriorId="
                        + expectedInteriorId
                        + "; Native=GET_INTERIOR_AT_COORDS:0; CollisionRequest=Exception:"
                        + ex.GetType().Name;
                    return false;
                }
                reason = "PENDING:Stage=InteriorParticipantIdentity; ExpectedLiveInteriorId="
                    + expectedInteriorId
                    + "; Native=GET_INTERIOR_AT_COORDS:0; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                return false;
            }
            if (preferredInteriorId != expectedInteriorId)
            {
                reason = "Stage=InteriorParticipantIdentity; ExpectedLiveInteriorId="
                    + expectedInteriorId + "; ActualLiveInteriorId=" + preferredInteriorId;
                return false;
            }

            bool interiorReady;
            try
            {
                interiorReady = IsInteriorReady(expectedInteriorId);
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorParticipantReadiness; LiveInteriorId=" + expectedInteriorId
                    + "; Native=IS_INTERIOR_READY; Result=Exception:" + ex.GetType().Name;
                return false;
            }
            if (!interiorReady)
            {
                try { RequestInteriorCollision(preferredPosition); }
                catch (Exception ex)
                {
                    reason = "Stage=InteriorParticipantReadiness; LiveInteriorId=" + expectedInteriorId
                        + "; Native=IS_INTERIOR_READY:false; CollisionRequest=Exception:"
                        + ex.GetType().Name;
                    return false;
                }
                if (!IsPlayerInsideLiveInterior(expectedInteriorId))
                {
                    reason = "PENDING:Stage=InteriorParticipantReadiness; LiveInteriorId=" + expectedInteriorId
                        + "; Native=IS_INTERIOR_READY:false; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    return false;
                }
            }

            try
            {
                // A store clerk or customer near the mapped threshold may have
                // no nearby walkable node even though the same live interior
                // has a clear counter or aisle position a few metres farther in.
                // Expand the local search, but keep each accepted point bound to
                // this live interior, floor, spacing, slope, and occupancy.
                float[] radii = { 0f, 0.6f, 1.15f, 1.7f, 2.25f, 2.75f, 3.5f, 4.25f };
                Vector3 best = Vector3.Zero;
                float bestDistance = float.MaxValue;
                bool found = false;
                int safePositionProbeCount = 0;
                int safePositionFoundCount = 0;
                int navigationCorrectionRejectedCount = 0;
                float largestRejectedCorrection = 0f;
                int safeInteriorIdentityRejectedCount = 0;
                int groundProbeCount = 0;
                int groundResultCount = 0;
                int groundNormalRejectedCount = 0;
                int safeGroundSeparationRejectedCount = 0;
                int authoredFloorAlignmentRejectedCount = 0;
                int finalInteriorIdentityRejectedCount = 0;
                int participantSpacingRejectedCount = 0;
                int overlappingPedRejectedCount = 0;
                int overlappingVehicleRejectedCount = 0;
                foreach (float radius in radii)
                {
                    int directions = radius == 0f ? 1 : 8;
                    for (int direction = 0; direction < directions; direction++)
                    {
                        double angle = direction * (Math.PI / 4.0);
                        Vector3 probe = radius == 0f
                            ? preferredPosition
                            : new Vector3(
                                preferredPosition.X + (float)Math.Cos(angle) * radius,
                                preferredPosition.Y + (float)Math.Sin(angle) * radius,
                                preferredPosition.Z);
                        Vector3 safe = probe;
                        safePositionProbeCount++;
                        if (!World.GetSafePositionForPed(probe, out safe, (GetSafePositionFlags)0))
                            continue;
                        safePositionFoundCount++;
                        float navigationCorrection = safe.DistanceTo(probe);
                        if (navigationCorrection > 1.25f)
                        {
                            navigationCorrectionRejectedCount++;
                            largestRejectedCorrection = Math.Max(
                                largestRejectedCorrection, navigationCorrection);
                            continue;
                        }
                        if (GetInteriorAt(safe) != expectedInteriorId)
                        {
                            safeInteriorIdentityRejectedCount++;
                            continue;
                        }

                        float groundZ;
                        Vector3 groundNormal;
                        groundProbeCount++;
                        if (!World.GetGroundHeightAndNormal(safe, out groundZ, out groundNormal))
                            continue;
                        groundResultCount++;
                        if (float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                            || groundNormal.Z < 0.55f)
                        {
                            groundNormalRejectedCount++;
                            continue;
                        }
                        if (Math.Abs(safe.Z - groundZ) > 2.5f)
                        {
                            safeGroundSeparationRejectedCount++;
                            continue;
                        }
                        if (Math.Abs(groundZ - preferredPosition.Z)
                            > InteriorFloorAlignmentTolerance)
                        {
                            authoredFloorAlignmentRejectedCount++;
                            continue;
                        }
                        safe.Z = groundZ;
                        if (GetInteriorAt(safe) != expectedInteriorId)
                        {
                            finalInteriorIdentityRejectedCount++;
                            continue;
                        }
                        if (!IsSeparatedFromReservedPositions(
                            safe, reservedPositions, DispatchParticipantMinimumSpacing))
                        {
                            participantSpacingRejectedCount++;
                            continue;
                        }
                        if (HasOverlappingPed(safe, 1.2f))
                        {
                            overlappingPedRejectedCount++;
                            continue;
                        }
                        if (HasOverlappingVehicle(safe, 1.6f))
                        {
                            overlappingVehicleRejectedCount++;
                            continue;
                        }

                        float distance = safe.DistanceTo(preferredPosition);
                        if (distance >= bestDistance)
                            continue;
                        best = safe;
                        bestDistance = distance;
                        found = true;
                    }
                    if (found && bestDistance <= radius + 0.1f)
                        break;
                }

                string nativeEvidence = "; NativeResults=GET_SAFE_COORD_FOR_PED:" + safePositionFoundCount
                    + "/" + safePositionProbeCount
                    + "; GET_INTERIOR_AT_COORDS:SafeMismatch=" + safeInteriorIdentityRejectedCount
                    + ",GroundMismatch=" + finalInteriorIdentityRejectedCount
                    + "; GET_GROUND_HEIGHT_AND_NORMAL:" + groundResultCount + "/" + groundProbeCount
                    + "; FloorRejected=NormalOrHeight=" + groundNormalRejectedCount
                    + ",SafeGroundSeparation=" + safeGroundSeparationRejectedCount
                    + ",AuthoredFloorAlignment=" + authoredFloorAlignmentRejectedCount
                    + "; ClearanceRejected=Spacing=" + participantSpacingRejectedCount
                    + ",Ped=" + overlappingPedRejectedCount
                    + ",Vehicle=" + overlappingVehicleRejectedCount;
                if (!found)
                {
                    if (safePositionFoundCount == 0
                        || (groundProbeCount > 0 && groundResultCount == 0))
                    {
                        try { RequestInteriorCollision(preferredPosition); }
                        catch (Exception ex)
                        {
                            reason = "Stage=InteriorParticipantNavigation; LiveInteriorId=" + expectedInteriorId
                                + "; SafePositionFound=" + safePositionFoundCount + "/" + safePositionProbeCount
                                + "; NavigationCorrectionRejected=" + navigationCorrectionRejectedCount
                                + "; LargestRejectedCorrection=" + largestRejectedCorrection.ToString(
                                    "0.00", CultureInfo.InvariantCulture)
                                + "; GroundHeightFound=" + groundResultCount + "/" + groundProbeCount
                                + nativeEvidence
                                + "; CollisionRequest=Exception:" + ex.GetType().Name;
                            return false;
                        }
                        reason = "PENDING:Stage=InteriorParticipantNavigation; LiveInteriorId=" + expectedInteriorId
                            + "; SafePositionFound=" + safePositionFoundCount + "/" + safePositionProbeCount
                            + "; NavigationCorrectionRejected=" + navigationCorrectionRejectedCount
                            + "; LargestRejectedCorrection=" + largestRejectedCorrection.ToString(
                                "0.00", CultureInfo.InvariantCulture)
                            + "; GroundHeightFound=" + groundResultCount + "/" + groundProbeCount
                            + nativeEvidence
                            + "; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                        return false;
                    }
                    reason = "Stage=InteriorParticipantPlacement; LiveInteriorId=" + expectedInteriorId
                        + "; SafePositionFound=" + safePositionFoundCount + "/" + safePositionProbeCount
                        + "; NavigationCorrectionRejected=" + navigationCorrectionRejectedCount
                        + "; LargestRejectedCorrection=" + largestRejectedCorrection.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; GroundHeightFound=" + groundResultCount + "/" + groundProbeCount
                        + nativeEvidence
                        + "; MaximumProbeRadius=4.25m"
                        + "; Result=NoSeparatedWalkableParticipantPosition";
                    return false;
                }
                safePosition = best;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorParticipantNavigation; LiveInteriorId="
                    + expectedInteriorId
                    + "; Native=GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL"
                    + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        private bool TryResolveMappedInteriorDispatchScene(
            string incidentType,
            Vector3 playerPosition,
            bool requiresVehicle,
            string requiredPhysicalType,
            string requiredLocationContext,
            int playerInteriorId,
            out AmbientDispatchSceneResolution scene,
            out string reason,
            ISet<string> excludedLocationIds = null)
        {
            scene = null;
            reason = "No nearby catalog location has a runtime-valid mapped interior and entrance.";
            string pendingReason = string.Empty;
            if (_locationCatalog == null)
            {
                reason = "The shared location catalog is unavailable.";
                return false;
            }

            var candidates = new List<Tuple<
                LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair,
                float>>();
            int accessPairCount = 0;
            int surveyEligiblePairCount = 0;
            int physicalTypeEligiblePairCount = 0;
            int incidentEligiblePairCount = 0;
            int contextEligiblePairCount = 0;
            int catalogIdentityEligiblePairCount = 0;
            int activeInteriorIdentityCheckCount = 0;
            int activeInteriorIdentityMatchCount = 0;
            int firstMappedInsideLiveId = int.MinValue;
            string firstMappedInsideNativeResult = string.Empty;
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in GetInteriorAccessPairs())
            {
                if (pair == null || pair.InsideLocation == null
                    || pair.OutsidePoint == null || pair.InsidePoint == null)
                    continue;
                if (excludedLocationIds != null
                    && excludedLocationIds.Contains(pair.InsideLocation.Id))
                    continue;
                accessPairCount++;

                if (IsRejectedSurveyRecord(pair.InsideLocation))
                    continue;
                surveyEligiblePairCount++;

                if (!MatchesRequiredPhysicalType(pair.InsideLocation, requiredPhysicalType))
                    continue;
                physicalTypeEligiblePairCount++;

                if (!IsCompatibleWithMappedInteriorIncident(
                    pair.InsideLocation, incidentType, requiredPhysicalType))
                    continue;
                incidentEligiblePairCount++;

                if (!MatchesMappedInteriorDispatchLocationContextMetadata(
                    pair.InsideLocation, requiredLocationContext))
                    continue;
                contextEligiblePairCount++;

                if (!HasCatalogedMappedInteriorReference(pair))
                    continue;
                catalogIdentityEligiblePairCount++;

                float distance;
                if (playerInteriorId != 0)
                {
                    int mappedLiveInteriorId;
                    string nativeResult;
                    activeInteriorIdentityCheckCount++;
                    bool pairMatchesActiveInterior = TryGetMappedInteriorPairLiveId(
                        pair,
                        playerInteriorId,
                        playerPosition,
                        out mappedLiveInteriorId,
                        out nativeResult);
                    if (firstMappedInsideLiveId == int.MinValue)
                    {
                        firstMappedInsideLiveId = mappedLiveInteriorId;
                        firstMappedInsideNativeResult = nativeResult;
                    }
                    if (!pairMatchesActiveInterior)
                        continue;
                    activeInteriorIdentityMatchCount++;
                    // Live identity proves that the Player occupies this exact
                    // mapped interior. Keep distance only for ordering its
                    // authored points; it is not an eligibility gate inside.
                    distance = Distance2D(pair.InsidePoint.Position, playerPosition);
                }
                else
                {
                    distance = Distance2D(pair.OutsidePoint.Position, playerPosition);
                    if (distance
                        > LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance)
                        continue;
                }

                candidates.Add(Tuple.Create(pair, distance));
            }

            if (candidates.Count == 0)
            {
                string insideOnlyFailure = string.Empty;
                if (playerInteriorId != 0 && !requiresVehicle
                    && TryResolveInsideOnlyInteriorSceneFromCatalog(
                        incidentType,
                        null,
                        requiredPhysicalType,
                        requiredLocationContext,
                        playerPosition,
                        playerInteriorId,
                        false,
                        null,
                        out scene,
                        out insideOnlyFailure,
                        excludedLocationIds))
                {
                    RememberLocation(scene.LocationId);
                    LogInsideOnlyInteriorSceneSelected(scene, "MappedInteriorNoPairMatch");
                    return true;
                }
                if (insideOnlyFailure.StartsWith(
                    "PENDING:", StringComparison.OrdinalIgnoreCase))
                {
                    reason = insideOnlyFailure;
                    return false;
                }

                string firstRejectedGate = accessPairCount == 0
                    ? "CatalogPairAvailability"
                    : surveyEligiblePairCount == 0
                        ? "SurveyReviewStatus"
                        : physicalTypeEligiblePairCount == 0
                            ? "RequiredPhysicalType"
                            : incidentEligiblePairCount == 0
                                ? "IncidentCompatibility"
                                : contextEligiblePairCount == 0
                                    ? "LocationContext"
                                    : catalogIdentityEligiblePairCount == 0
                                        ? "CatalogInteriorReference"
                                        : playerInteriorId != 0
                                            ? "LiveInteriorIdentity"
                                            : "PlayerDistance";
                reason = "Stage=CatalogEligibility; FirstRejectedGate=" + firstRejectedGate
                    + "; AccessPairs=" + accessPairCount
                    + "; SurveyEligible=" + surveyEligiblePairCount
                    + "; PhysicalTypeEligible=" + physicalTypeEligiblePairCount
                    + "; IncidentEligible=" + incidentEligiblePairCount
                    + "; ContextEligible=" + contextEligiblePairCount
                    + "; CatalogIdentityEligible=" + catalogIdentityEligiblePairCount
                    + "; RequiredPhysicalType=" + (requiredPhysicalType ?? string.Empty)
                    + "; RequiredContext=" + (requiredLocationContext ?? string.Empty)
                    + "; IncidentType=" + (incidentType ?? string.Empty)
                    + "; MaximumPlayerDistance="
                    + LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance
                    + "; PlayerLiveInteriorId=" + playerInteriorId
                    + "; MappedInsideIdentityChecks=" + activeInteriorIdentityCheckCount
                    + "; MappedInsideIdentityMatches=" + activeInteriorIdentityMatchCount
                    + "; FirstMappedInsideLiveInteriorId="
                    + (firstMappedInsideLiveId == int.MinValue
                        ? "NotProbed" : firstMappedInsideLiveId.ToString(CultureInfo.InvariantCulture))
                    + "; FirstMappedInsideNativeResult="
                    + (firstMappedInsideNativeResult ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(insideOnlyFailure))
                    reason += "; InsideOnlyFallback=" + insideOnlyFailure;
                return false;
            }

            // Interior floor probes call several GTA natives per candidate.
            // Keep live validation bounded to the nearest three pairs during
            // offer preparation; this resolver is never called from the tick.
            // A building can contribute several door points to the catalog.
            // Do not spend the bounded live-validation budget on multiple doors
            // from the same site before trying another nearby compatible site.
            foreach (var candidate in candidates
                .GroupBy(value => value.Item1.InsideLocation.Id,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(value => value.Item2)
                    .ThenBy(value => value.Item1.DistanceMeters)
                    .First())
                .OrderBy(value => value.Item2)
                .Take(MaximumMappedInteriorDispatchCandidates))
            {
                int liveInteriorId = GetInteriorAt(candidate.Item1.InsidePoint.Position);
                if (liveInteriorId == 0)
                {
                    try
                    {
                        RequestDispatchInteriorCollision(
                            liveInteriorId,
                            candidate.Item1.InsidePoint.Position);
                    }
                    catch (Exception ex)
                    {
                        pendingReason = "Stage=InteriorIdentity; Location="
                            + candidate.Item1.InsideLocation.Id
                            + "; CatalogInteriorId=" + candidate.Item1.InteriorId
                            + "; Native=GET_INTERIOR_AT_COORDS:0; CollisionRequest=Exception:"
                            + ex.GetType().Name;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(pendingReason))
                        pendingReason = "PENDING:Stage=InteriorIdentity; Location="
                            + candidate.Item1.InsideLocation.Id
                            + "; CatalogInteriorId=" + candidate.Item1.InteriorId
                            + "; Native=GET_INTERIOR_AT_COORDS:0; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    continue;
                }
                if (playerInteriorId != 0 && liveInteriorId != playerInteriorId)
                {
                    if (reason.StartsWith("No nearby catalog", StringComparison.Ordinal))
                        reason = "Stage=PlayerInteriorIdentity; Location="
                            + candidate.Item1.InsideLocation.Id
                            + "; CatalogInteriorId=" + candidate.Item1.InteriorId
                            + "; PlayerLiveInteriorId=" + playerInteriorId
                            + "; MappedInsideLiveInteriorId=" + liveInteriorId
                            + "; Native=GET_INTERIOR_AT_COORDS; Result=DifferentNonzeroInterior";
                    continue;
                }
                // A matching interior ID does not prove that GTA streamed the
                // room. Keep the exact candidate pending until readiness is true,
                // even when the Player is already at its coordinates.
                bool interiorReady;
                try
                {
                    interiorReady = IsDispatchInteriorReadyCached(liveInteriorId);
                }
                catch (Exception ex)
                {
                    pendingReason = "Stage=InteriorReadiness; Location="
                        + candidate.Item1.InsideLocation.Id
                        + "; CatalogInteriorId=" + candidate.Item1.InteriorId
                        + "; LiveInteriorId=" + liveInteriorId
                        + "; Native=IS_INTERIOR_READY; Result=Exception:"
                        + ex.GetType().Name;
                    continue;
                }
                if (!interiorReady)
                {
                    string streamingFailure;
                    if (!TryRequestDispatchInteriorStreaming(
                        liveInteriorId,
                        candidate.Item1.InsidePoint.Position,
                        candidate.Item1.InsideLocation.Id,
                        candidate.Item1.InteriorId,
                        out streamingFailure))
                    {
                        pendingReason = streamingFailure;
                        continue;
                    }
                    pendingReason = "PENDING:" + streamingFailure;
                    continue;
                }
                if (playerInteriorId != 0 && liveInteriorId != playerInteriorId)
                {
                    if (reason.StartsWith("No nearby catalog", StringComparison.Ordinal))
                        reason = "Stage=PlayerInteriorIdentity; Location="
                            + candidate.Item1.InsideLocation.Id
                            + "; CatalogInteriorId=" + candidate.Item1.InteriorId
                            + "; PlayerLiveInteriorId=" + playerInteriorId
                            + "; MappedInsideLiveInteriorId=" + liveInteriorId
                            + "; Native=GET_INTERIOR_AT_COORDS; Result=DifferentNonzeroInterior";
                    continue;
                }

                LSPDDispatchLocationDefinition area =
                    LSPDDispatchLocationDefinition.ForNearbyCatalogCandidate(
                        candidate.Item1.InsideLocation.Id,
                        candidate.Item1.OutsidePoint.Position,
                        requiredLocationContext);
                AmbientDispatchSceneResolution candidateScene;
                string candidateFailure;
                if (!TryResolveInteriorDispatchScene(
                    new[] { area },
                    incidentType,
                    playerPosition,
                    liveInteriorId,
                    requiresVehicle,
                    candidate.Item1,
                    false,
                    false,
                    out candidateScene,
                    out candidateFailure,
                    requiredPhysicalType,
                    requiredLocationContext,
                    true,
                    interiorReady,
                    excludedLocationIds))
                {
                    if (candidateFailure.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase))
                        pendingReason = candidateFailure;
                    else if (string.IsNullOrWhiteSpace(reason)
                        || reason.StartsWith("No nearby catalog", StringComparison.Ordinal))
                        reason = candidateFailure;
                    continue;
                }

                candidateScene.IsMappedInteriorDispatchScene = true;
                scene = candidateScene;
                RememberLocation(candidateScene.LocationId);
                Log("AMBIENT_WORLD_MAPPED_INTERIOR_DISPATCH_SELECTED",
                    "Location=" + candidateScene.LocationId
                    + "; InsidePoint=" + candidateScene.PointId
                    + "; OutsidePoint=" + candidateScene.InteriorAccessPair.OutsidePoint.Id
                    + "; CatalogInteriorId=" + candidateScene.CatalogInteriorId
                    + "; LiveInteriorId=" + candidateScene.LiveInteriorId
                    + "; MappingBasis=" + candidateScene.InteriorAccessPair.MappingBasis
                    + "; PlayerDistance=" + candidate.Item2.ToString(
                        "0.0", CultureInfo.InvariantCulture)
                    + "; VehicleStaging=" + candidateScene.HasVehicleStagingPosition);
                return true;
            }

            string insideOnlyPointFailure = string.Empty;
            if (playerInteriorId != 0 && !requiresVehicle
                && TryResolveInsideOnlyInteriorSceneFromCatalog(
                    incidentType,
                    null,
                    requiredPhysicalType,
                    requiredLocationContext,
                    playerPosition,
                    playerInteriorId,
                    false,
                    null,
                    out scene,
                    out insideOnlyPointFailure,
                    excludedLocationIds))
            {
                RememberLocation(scene.LocationId);
                LogInsideOnlyInteriorSceneSelected(scene, "MappedInteriorAfterPairSceneValidation");
                return true;
            }

            if (!string.IsNullOrWhiteSpace(insideOnlyPointFailure)
                && insideOnlyPointFailure.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase))
                pendingReason = insideOnlyPointFailure;
            if (!string.IsNullOrWhiteSpace(pendingReason))
                reason = pendingReason.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase)
                    ? pendingReason : "PENDING:" + pendingReason;
            if (!string.IsNullOrWhiteSpace(insideOnlyPointFailure)
                && !insideOnlyPointFailure.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase))
                reason += "; InsideOnlyFallback=" + insideOnlyPointFailure;
            return false;
        }

        private bool TryResolveNamedInteriorDispatchScene(
            IEnumerable<LSPDDispatchLocationDefinition> eventAreas,
            string incidentType,
            Vector3 playerPosition,
            string requiredInteriorLocationId,
            bool requiresVehicle,
            bool previouslySelected,
            out AmbientDispatchSceneResolution scene,
            out string reason,
            string requiredPhysicalType = null,
            ISet<string> excludedLocationIds = null)
        {
            scene = null;
            reason = "The authored interior site has no usable mapped threshold pair.";
            string pendingReason = string.Empty;
            if (_locationCatalog == null
                || string.IsNullOrWhiteSpace(requiredInteriorLocationId))
                return false;

            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in GetInteriorAccessPairs().Where(value => value != null
                    && value.InsideLocation != null
                    && value.InsidePoint != null
                    && value.OutsidePoint != null
                    && HasCatalogedMappedInteriorReference(value)
                    && (excludedLocationIds == null
                        || !excludedLocationIds.Contains(value.InsideLocation.Id))
                    && string.Equals(value.InsideLocation.Id, requiredInteriorLocationId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                int liveInteriorId = GetInteriorAt(pair.InsidePoint.Position);
                if (liveInteriorId == 0)
                {
                    try { RequestInteriorCollision(pair.InsidePoint.Position); }
                    catch (Exception ex)
                    {
                        pendingReason = "Stage=InteriorIdentity; Location=" + requiredInteriorLocationId
                            + "; CatalogInteriorId=" + pair.InteriorId
                            + "; Native=GET_INTERIOR_AT_COORDS:0; CollisionRequest=Exception:"
                            + ex.GetType().Name;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(pendingReason))
                        pendingReason = "PENDING:Stage=InteriorIdentity; Location=" + requiredInteriorLocationId
                            + "; CatalogInteriorId=" + pair.InteriorId
                            + "; Native=GET_INTERIOR_AT_COORDS:0; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    continue;
                }

                string candidateFailure;
                if (TryResolveInteriorDispatchScene(
                    eventAreas,
                    incidentType,
                    playerPosition,
                    liveInteriorId,
                    requiresVehicle,
                    pair,
                    previouslySelected,
                    true,
                    out scene,
                    out candidateFailure,
                    requiredPhysicalType,
                    null,
                    true,
                    false,
                    excludedLocationIds))
                {
                    Log("AMBIENT_WORLD_MAPPED_INTERIOR_DISPATCH_SELECTED",
                        "Location=" + requiredInteriorLocationId
                        + "; Point=" + pair.InsidePoint.Id
                        + "; Threshold=" + pair.OutsidePoint.Id
                        + "; CatalogInteriorId=" + pair.InteriorId
                        + "; LiveInteriorId=" + liveInteriorId
                        + "; MappingBasis=" + pair.MappingBasis
                        + "; PairDistance=" + pair.DistanceMeters
                        + "; PreviouslySelected=" + previouslySelected);
                    return true;
                }
                if (candidateFailure.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase))
                    pendingReason = candidateFailure;
                else
                    reason = candidateFailure;
            }

            if (!string.IsNullOrWhiteSpace(pendingReason))
                reason = pendingReason.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase)
                    ? pendingReason : "PENDING:" + pendingReason;
            return false;
        }

        private bool TryResolveInteriorDispatchScene(
            IEnumerable<LSPDDispatchLocationDefinition> eventAreas,
            string incidentType,
            Vector3 playerPosition,
            int interiorId,
            bool requiresVehicle,
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair requiredPair,
            bool previouslySelected,
            bool allowUnclassifiedRequiredSite,
            out AmbientDispatchSceneResolution scene,
            out string reason,
            string requiredPhysicalType = null,
            string requiredLocationContext = null,
            bool allowMappedThresholdProximity = false,
            bool interiorReadinessGateSatisfied = false,
            ISet<string> excludedLocationIds = null)
        {
            scene = null;
            reason = "No validated interior threshold and eligible event area matched the Player's current location.";
            string pendingFloorReason = string.Empty;
            int currentPlayerLiveInteriorId = 0;
            bool playerIdentityAvailable = false;
            try
            {
                playerIdentityAvailable = TryGetDispatchPlayerInteriorId(
                    Game.Player.Character, out currentPlayerLiveInteriorId);
            }
            catch { }
            bool playerInside = interiorId != 0 && playerIdentityAvailable
                && currentPlayerLiveInteriorId == interiorId;
            if (interiorId != 0 && !playerIdentityAvailable)
            {
                try
                {
                    currentPlayerLiveInteriorId = GetInteriorAt(playerPosition);
                    playerIdentityAvailable = true;
                    playerInside = currentPlayerLiveInteriorId == interiorId;
                }
                catch { playerInside = false; }
            }
            if (currentPlayerLiveInteriorId != 0
                && currentPlayerLiveInteriorId != interiorId)
            {
                reason = "Stage=PlayerInteriorIdentity; CatalogInteriorId="
                    + (requiredPair == null ? string.Empty : requiredPair.InteriorId)
                    + "; ExpectedLiveInteriorId=" + interiorId
                    + "; PlayerLiveInteriorId=" + currentPlayerLiveInteriorId
                    + "; PlayerPosition=" + playerPosition
                    + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                    + "; Result=CoordinateIdentityDifferentFromSelectedInterior";
                return false;
            }
            if (_locationCatalog == null || interiorId == 0
                || (!playerInside && !allowUnclassifiedRequiredSite
                    && !allowMappedThresholdProximity))
            {
                reason = "The Player is not inside the requested mapped interior.";
                return false;
            }

            if (!interiorReadinessGateSatisfied)
            {
                bool interiorReady;
                try
                {
                    interiorReady = IsDispatchInteriorReadyCached(interiorId);
                }
                catch (Exception ex)
                {
                    reason = "Stage=InteriorReadiness; Location="
                        + (requiredPair == null || requiredPair.InsideLocation == null
                            ? string.Empty : requiredPair.InsideLocation.Id)
                        + "; CatalogInteriorId="
                        + (requiredPair == null ? string.Empty : requiredPair.InteriorId)
                        + "; LiveInteriorId=" + interiorId
                        + "; Native=IS_INTERIOR_READY; Result=Exception:"
                        + ex.GetType().Name;
                    return false;
                }
                if (!interiorReady)
                {
                    Vector3 readinessProbe = requiredPair == null
                        || requiredPair.InsidePoint == null
                            ? playerPosition : requiredPair.InsidePoint.Position;
                    if (requiredPair == null
                        || requiredPair.InsideLocation == null
                        || !HasCatalogedMappedInteriorReference(requiredPair))
                    {
                        reason = "Stage=InteriorStreamingRequest; Location="
                            + (requiredPair == null || requiredPair.InsideLocation == null
                                ? string.Empty : requiredPair.InsideLocation.Id)
                            + "; CatalogInteriorId="
                            + (requiredPair == null ? string.Empty : requiredPair.InteriorId)
                            + "; LiveInteriorId=" + interiorId
                            + "; Result=NoOwnerConfirmedMappedPairForStreaming";
                        return false;
                    }

                    string streamingFailure;
                    if (!TryRequestDispatchInteriorStreaming(
                        interiorId,
                        readinessProbe,
                        requiredPair.InsideLocation.Id,
                        requiredPair.InteriorId,
                        out streamingFailure))
                    {
                        reason = streamingFailure;
                        return false;
                    }
                    reason = "PENDING:" + streamingFailure;
                    return false;
                }
            }

            List<LSPDDispatchLocationDefinition> eligibleAreas = (eventAreas
                ?? Enumerable.Empty<LSPDDispatchLocationDefinition>())
                .Where(area => area != null
                    && (previouslySelected
                        || area.IsEligibleFor(playerPosition)
                        || (allowMappedThresholdProximity
                            && requiredPair != null
                            && (playerInside
                                || area.IsWithinNearbyCatalogFallbackDistance(
                                    requiredPair.OutsidePoint.Position,
                                    playerPosition)))))
                .ToList();
            if (eligibleAreas.Count == 0)
            {
                reason = "Stage=CatalogAreaEligibility; Location="
                    + (requiredPair == null || requiredPair.InsideLocation == null
                        ? string.Empty : requiredPair.InsideLocation.Id)
                    + "; CatalogInteriorId="
                    + (requiredPair == null ? string.Empty : requiredPair.InteriorId)
                    + "; LiveInteriorId=" + interiorId
                    + "; EligibleEventAreas=0"
                    + "; PlayerInside=" + playerInside
                    + "; Result=NoEventAreaWithinAuthoredPlayerOrMappedDoorwayRange";
                return false;
            }

            var candidates = new List<Tuple<
                LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair,
                LSPDDispatchLocationDefinition,
                float>>();
            List<LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair> accessPairs =
                requiredPair == null ? GetInteriorAccessPairs().ToList() : null;
            int accessPairCount = requiredPair == null ? accessPairs.Count : 1;
            for (int accessPairIndex = 0; accessPairIndex < accessPairCount; accessPairIndex++)
            {
                LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair =
                    requiredPair ?? accessPairs[accessPairIndex];
                if (pair == null || pair.InsideLocation == null || pair.InsidePoint == null
                    || (requiredPair != null && !ReferenceEquals(pair, requiredPair))
                    || (excludedLocationIds != null
                        && excludedLocationIds.Contains(pair.InsideLocation.Id))
                    || !HasCatalogedMappedInteriorReference(pair)
                    || IsRejectedSurveyRecord(pair.InsideLocation)
                    || !MatchesRequiredPhysicalType(pair.InsideLocation, requiredPhysicalType)
                    || !MatchesMappedInteriorDispatchLocationContextMetadata(
                        pair.InsideLocation, requiredLocationContext)
                    || (!IsCompatibleWithMappedInteriorIncident(
                            pair.InsideLocation, incidentType, requiredPhysicalType)
                        && !(allowUnclassifiedRequiredSite && requiredPair != null
                            && ReferenceEquals(pair, requiredPair))))
                    continue;

                Vector3 outsideProbe = ResolveOutsideInteriorProbe(pair);
                int liveOutsideInteriorId = GetInteriorAt(outsideProbe);
                int liveInsideInteriorId = GetInteriorAt(pair.InsidePoint.Position);
                if (!IsMappedInteriorThresholdRuntimeValid(
                    interiorId,
                    liveOutsideInteriorId,
                    liveInsideInteriorId))
                {
                    if (reason.StartsWith(
                        "No validated interior threshold", StringComparison.Ordinal))
                        reason = "Stage=MappedThresholdIdentity; Location="
                            + pair.InsideLocation.Id
                            + "; CatalogInteriorId=" + pair.InteriorId
                            + "; ExpectedLiveInteriorId=" + interiorId
                            + "; OutsideLiveInteriorId=" + liveOutsideInteriorId
                            + "; InsideLiveInteriorId=" + liveInsideInteriorId
                            + "; Native=GET_INTERIOR_AT_COORDS; Result=ThresholdIdentityMismatch";
                    continue;
                }

                foreach (LSPDDispatchLocationDefinition area in eligibleAreas)
                {
                    float areaDistance = Distance2D(pair.OutsidePoint.Position, area.Position);
                    Vector3 playerDistanceReference = allowMappedThresholdProximity
                        && !playerInside
                        ? pair.OutsidePoint.Position
                        : pair.InsidePoint.Position;
                    float playerDistance = playerDistanceReference.DistanceTo(playerPosition);
                    bool playerWithinArea = allowMappedThresholdProximity
                        && requiredPair != null
                        ? playerInside || area.IsWithinNearbyCatalogFallbackDistance(
                            playerDistanceReference, playerPosition)
                        : previouslySelected
                            ? playerDistance <= area.MaximumPlayerDistance
                            : IsWithinPlayerDistance(
                                area, playerDistanceReference, playerPosition);
                    if ((!playerInside && areaDistance > SurveyPointAreaRadius)
                        || !playerWithinArea)
                        continue;
                    candidates.Add(Tuple.Create(pair, area, areaDistance));
                }
            }

            if (candidates.Count == 0
                && reason.StartsWith(
                    "No validated interior threshold", StringComparison.Ordinal))
            {
                LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair =
                    requiredPair;
                reason = "Stage=CatalogAreaEligibility; Location="
                    + (pair == null || pair.InsideLocation == null
                        ? string.Empty : pair.InsideLocation.Id)
                    + "; CatalogInteriorId=" + (pair == null ? string.Empty : pair.InteriorId)
                    + "; LiveInteriorId=" + interiorId
                    + "; EligibleEventAreas=" + eligibleAreas.Count
                    + "; AreaRadius=" + SurveyPointAreaRadius
                    + "; Result=NoMappedThresholdAreaPairPassedRangeChecks";
            }

            foreach (var candidate in candidates.OrderBy(value => value.Item3))
            {
                Vector3 walkable;
                string floorFailure;
                if (!TryResolveInteriorWalkablePosition(
                    candidate.Item1.InsidePoint.Position,
                    interiorId,
                    ResolveOutsideInteriorProbe(candidate.Item1),
                    out walkable,
                    out floorFailure,
                    playerInside ? (Vector3?)playerPosition : null))
                {
                    if (floorFailure.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase))
                        pendingFloorReason = floorFailure;
                    else
                        reason = floorFailure;
                    continue;
                }

                Vector3 vehiclePosition = Vector3.Zero;
                float vehicleHeading = 0f;
                bool hasVehiclePosition = false;
                if (requiresVehicle)
                {
                    if (!TryResolveVehicleStaging(
                        candidate.Item1.OutsidePoint.Position,
                        candidate.Item2,
                        playerPosition,
                        !allowMappedThresholdProximity,
                        out vehiclePosition,
                        out vehicleHeading,
                        out reason))
                        continue;
                    hasVehiclePosition = true;
                }

                scene = new AmbientDispatchSceneResolution
                {
                    Area = candidate.Item2,
                    AreaLocationId = candidate.Item2.Id,
                    AreaAnchorPosition = candidate.Item2.Position,
                    LocationId = candidate.Item1.InsideLocation.Id,
                    LocationName = candidate.Item1.InsideLocation.Name,
                    LocationCategory = candidate.Item1.InsideLocation.Category,
                    PointId = candidate.Item1.InsidePoint.Id,
                    SourceCaptureId = candidate.Item1.InsidePoint.SourceRecord,
                    AuthoredPosition = candidate.Item1.InsidePoint.Position,
                    SceneCenter = walkable,
                    ActorPosition = walkable,
                    Heading = candidate.Item1.InsidePoint.HasHeading
                        ? candidate.Item1.InsidePoint.Heading : 0f,
                    VehicleStagingPosition = vehiclePosition,
                    VehicleHeading = vehicleHeading,
                    HasVehicleStagingPosition = hasVehiclePosition,
                    IsSurveyedPoint = true,
                    MaximumSnapDistance = InteriorActivityMaximumDepth,
                    ValidationStatus = "RuntimeValidatedInsideMappedInterior",
                    LocationReviewStatus = candidate.Item1.InsideLocation.ReviewStatus,
                    IsInteriorScene = true,
                    IsMappedInteriorDispatchScene = allowMappedThresholdProximity,
                    CatalogInteriorId = candidate.Item1.InteriorId,
                    LiveInteriorId = interiorId,
                    InteriorAccessPair = candidate.Item1,
                    IncidentType = incidentType ?? string.Empty
                };
                reason = string.Empty;
                return true;
            }
            if (!string.IsNullOrWhiteSpace(pendingFloorReason))
                reason = pendingFloorReason;
            return false;
        }

        private bool TryBuildDispatchScene(
            LSPDDispatchLocationDefinition area,
            string locationId,
            string locationName,
            string locationCategory,
            string pointId,
            string captureId,
            Vector3 authoredPosition,
            float heading,
            bool surveyedPoint,
            float maximumSnapDistance,
            Vector3 playerPosition,
            bool enforcePlayerDistanceWindow,
            bool requiresVehicle,
            bool movingRoadVehicleScene,
            float maximumStreetHeightDelta,
            out AmbientDispatchSceneResolution scene,
            out string reason)
        {
            scene = null;
            reason = string.Empty;
            bool footActivityCandidate = IsFootActivityOnlySurveyCandidate(
                locationId, locationCategory);
            if (footActivityCandidate && requiresVehicle)
            {
                reason = "Stage=CatalogEventFamily; IntendedUse=FootActivityCandidate"
                    + "; RequiresVehicle=true; Result=Rejected";
                return false;
            }
            int minimumOpenFootprintDirections =
                GetDispatchExteriorMinimumOpenFootprintDirections(
                    area, null, locationCategory, null, footActivityCandidate);
            float sameSiteFallbackOffset = footActivityCandidate
                ? DispatchExteriorFootCatalogSameSiteFallbackOffset
                : DispatchExteriorSameSiteFallbackOffset;
            float candidateLocationOffsetLimit =
                GetDispatchExteriorSceneCenterOffsetLimit(
                    maximumSnapDistance, footActivityCandidate);
            Vector3 safePedPosition = authoredPosition;
            Vector3 vehiclePosition = Vector3.Zero;
            float vehicleHeading = 0f;
            Vector3 vehicleRoutePosition = Vector3.Zero;
            float vehicleRouteHeading = 0f;
            bool hasVehiclePosition = false;
            Vector3 exteriorApproachPosition = Vector3.Zero;
            string exteriorPlacementEvidence = movingRoadVehicleScene
                ? "RoadVehicleScene=RoadNodeValidated"
                : string.Empty;

            if (movingRoadVehicleScene)
            {
                if (!requiresVehicle)
                {
                    reason = "Stage=MovingRoadSceneRequirements; RequiresVehicle=false; Result=Rejected";
                    return false;
                }
                if (!TryResolveMovingRoadVehicleStaging(
                    authoredPosition,
                    area,
                    playerPosition,
                    enforcePlayerDistanceWindow,
                    out vehiclePosition,
                    out vehicleHeading,
                    out vehicleRoutePosition,
                    out vehicleRouteHeading,
                    out reason))
                    return false;
                safePedPosition = vehiclePosition;
                hasVehiclePosition = true;
            }
            else
            {
                string firstPlacementFailure = string.Empty;
                string lastPlacementFailure = string.Empty;
                int placementProbeCount = 0;
                bool exteriorPositionReady = false;
                for (int index = -1;
                    index < DispatchExteriorSameSiteFallbackDirections.Length;
                    index++)
                {
                    Vector3 requestedPosition = authoredPosition;
                    float correctionLimit = maximumSnapDistance;
                    if (index >= 0)
                    {
                        Vector2 offset = DispatchExteriorSameSiteFallbackDirections[index]
                            * (sameSiteFallbackOffset
                                / DispatchExteriorSameSiteFallbackOffset);
                        requestedPosition = new Vector3(
                            authoredPosition.X + offset.X,
                            authoredPosition.Y + offset.Y,
                            authoredPosition.Z);
                        correctionLimit = sameSiteFallbackOffset;
                    }
                    correctionLimit = GetDispatchExteriorNavigationSnapDistanceLimit(
                        correctionLimit, footActivityCandidate);

                    placementProbeCount++;
                    Vector3 candidatePosition;
                    Vector3 candidateApproachPosition;
                    string candidateEvidence;
                    string candidateFailure;
                    if (!TryValidateDispatchExteriorPosition(
                        requestedPosition,
                        correctionLimit,
                        maximumStreetHeightDelta,
                        authoredPosition,
                        area == null ? Vector3.Zero : area.Position,
                        minimumOpenFootprintDirections,
                        null,
                        out candidatePosition,
                        out candidateApproachPosition,
                        out candidateEvidence,
                        out candidateFailure))
                    {
                        lastPlacementFailure = candidateFailure;
                        if (string.IsNullOrWhiteSpace(firstPlacementFailure))
                            firstPlacementFailure = candidateFailure;
                        continue;
                    }

                    if (!IsDispatchExteriorLocationOffsetValid(
                            authoredPosition, candidatePosition,
                            candidateLocationOffsetLimit))
                    {
                        lastPlacementFailure = "Stage=ExteriorLocationSnap; Native=Vector3.Distance"
                            + "; Requested=" + authoredPosition
                            + "; Resolved=" + candidatePosition
                            + "; Maximum=" + candidateLocationOffsetLimit.ToString(
                                "0.00", CultureInfo.InvariantCulture);
                        if (string.IsNullOrWhiteSpace(firstPlacementFailure))
                            firstPlacementFailure = lastPlacementFailure;
                        continue;
                    }

                    safePedPosition = candidatePosition;
                    exteriorApproachPosition = candidateApproachPosition;
                    exteriorPlacementEvidence = candidateEvidence
                        + "; SameLocationProbe=" + placementProbeCount;
                    exteriorPositionReady = true;
                    break;
                }

                if (!exteriorPositionReady)
                {
                    reason = "Stage=ExteriorApproach; SameLocationProbes=" + placementProbeCount
                        + "; MaximumSameLocationOffset="
                        + sameSiteFallbackOffset.ToString(
                                "0.00", CultureInfo.InvariantCulture)
                        + "m; MaximumSceneOffset="
                        + candidateLocationOffsetLimit.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "m; FirstFailure=" + (string.IsNullOrWhiteSpace(firstPlacementFailure)
                            ? "None" : firstPlacementFailure)
                        + "; LastFailure=" + (string.IsNullOrWhiteSpace(lastPlacementFailure)
                            ? "None" : lastPlacementFailure);
                    return false;
                }

                if (requiresVehicle)
                {
                    if (!TryResolveVehicleStaging(
                        safePedPosition,
                        area,
                        playerPosition,
                        enforcePlayerDistanceWindow,
                        out vehiclePosition,
                        out vehicleHeading,
                        out reason))
                        return false;
                    hasVehiclePosition = true;
                }
            }

            if (area == null
                || (enforcePlayerDistanceWindow
                    && (!area.IsEligibleFor(playerPosition)
                        || !IsWithinPlayerDistance(area, safePedPosition, playerPosition))))
            {
                reason = "Stage=SceneAreaRange; Result=Runtime-safe point falls outside the event's authored player-distance window.";
                return false;
            }

            scene = new AmbientDispatchSceneResolution
            {
                Area = area,
                AreaLocationId = area.Id,
                AreaAnchorPosition = area.Position,
                LocationId = locationId ?? string.Empty,
                LocationName = locationName ?? string.Empty,
                LocationCategory = locationCategory ?? "Unknown",
                PointId = pointId ?? string.Empty,
                SourceCaptureId = captureId ?? string.Empty,
                AuthoredPosition = authoredPosition,
                SceneCenter = safePedPosition,
                ActorPosition = safePedPosition,
                Heading = movingRoadVehicleScene ? vehicleHeading : heading,
                VehicleStagingPosition = vehiclePosition,
                VehicleHeading = vehicleHeading,
                VehicleRoutePosition = vehicleRoutePosition,
                VehicleRouteHeading = vehicleRouteHeading,
                VehicleStagingMode = movingRoadVehicleScene
                    ? "moving_road" : requiresVehicle ? "parked_ground" : "none",
                HasVehicleStagingPosition = hasVehiclePosition,
                HasVehicleRoute = movingRoadVehicleScene,
                IsSurveyedPoint = surveyedPoint,
                MaximumSnapDistance = maximumSnapDistance,
                ExteriorRoadHeightTolerance = maximumStreetHeightDelta,
                MinimumExteriorOpenFootprintDirections =
                    minimumOpenFootprintDirections,
                IsInteriorScene = false,
                ExteriorApproachPosition = exteriorApproachPosition,
                ExteriorPlacementEvidence = exteriorPlacementEvidence,
                CatalogInteriorId = string.Empty,
                LiveInteriorId = 0,
                InteriorAccessPair = null,
                ValidationStatus = surveyedPoint
                    ? "RuntimeValidatedForCurrentScene"
                    : "RuntimeResolvedAreaCandidate"
            };
            return true;
        }

        private bool TryResolvePlayerRadiusVehicleScene(
            string incidentType,
            string vehicleActivity,
            Vector3 playerPosition,
            Vector3 playerRadiusRoadAnchor,
            out AmbientDispatchSceneResolution scene,
            out string reason,
            Vector3? pendingRoadsideCandidatePosition = null)
        {
            scene = null;
            reason = string.Empty;
            float fallbackMinimumDistance;
            float fallbackMaximumDistance;
            GetPlayerRadiusFallbackDistanceRange(
                out fallbackMinimumDistance, out fallbackMaximumDistance);
            float[] fallbackRoadOffsets = BuildPlayerRadiusFallbackRoadOffsets(
                fallbackMinimumDistance, fallbackMaximumDistance);
            LSPDDispatchLocationDefinition runtimeArea =
                LSPDDispatchLocationDefinition.ForPlayerRadiusVehicleScene(playerPosition);
            Vector3 vehiclePosition;
            float vehicleHeading;
            Vector3 routePosition = Vector3.Zero;
            float routeHeading = 0f;
            Vector3 actorPosition;
            bool movingRoad = string.Equals(vehicleActivity, "moving_road",
                StringComparison.OrdinalIgnoreCase);

            if (movingRoad)
            {
                if (!TryResolveMovingRoadVehicleStaging(
                    playerRadiusRoadAnchor == Vector3.Zero
                        ? playerPosition : playerRadiusRoadAnchor,
                    runtimeArea,
                    playerPosition,
                    false,
                    out vehiclePosition,
                    out vehicleHeading,
                    out routePosition,
                    out routeHeading,
                    out reason,
                    true,
                    fallbackMinimumDistance,
                    fallbackMaximumDistance,
                    fallbackRoadOffsets))
                    return false;
                actorPosition = vehiclePosition;
            }
            else if (string.Equals(vehicleActivity, "parked_theft",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(vehicleActivity, "carjacking",
                    StringComparison.OrdinalIgnoreCase))
            {
                Vector3 pendingCandidatePosition;
                if (!TryResolvePlayerRadiusParkedVehicleStaging(
                    playerPosition,
                    playerRadiusRoadAnchor,
                    out actorPosition,
                    out vehiclePosition,
                    out vehicleHeading,
                    out reason,
                    true,
                    pendingRoadsideCandidatePosition,
                    out pendingCandidatePosition))
                {
                    if (reason.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase)
                        && pendingCandidatePosition != Vector3.Zero)
                    {
                        scene = new AmbientDispatchSceneResolution
                        {
                            Area = runtimeArea,
                            AreaLocationId = runtimeArea.Id,
                            AreaAnchorPosition = playerPosition,
                            LocationId = string.Empty,
                            LocationName = "Pending player-radius roadside candidate",
                            LocationCategory = "PlayerRadiusRoadNode",
                            PointId = "player-radius-roadside-pending",
                            AuthoredPosition = playerPosition,
                            SceneCenter = pendingCandidatePosition,
                            ActorPosition = pendingCandidatePosition,
                            VehicleStagingPosition = pendingCandidatePosition,
                            VehicleStagingMode = "pending_ground_collision",
                            HasVehicleStagingPosition = false,
                            IsInteriorScene = false,
                            IncidentType = incidentType ?? string.Empty,
                            ValidationStatus = "PendingRoadsideGroundReadiness",
                            IsPendingSceneResolution = true,
                            PendingResolutionPosition = pendingCandidatePosition
                        };
                    }
                    return false;
                }
            }
            else
            {
                reason = "Stage=PlayerRadiusVehicleActivity; VehicleActivity="
                    + (vehicleActivity ?? string.Empty) + "; Result=Unsupported";
                return false;
            }

            scene = new AmbientDispatchSceneResolution
            {
                Area = runtimeArea,
                AreaLocationId = runtimeArea.Id,
                AreaAnchorPosition = playerPosition,
                LocationId = string.Empty,
                LocationName = movingRoad
                    ? "Player-radius validated road nodes"
                    : "Player-radius validated roadside ground",
                LocationCategory = "PlayerRadiusRoadNode",
                PointId = movingRoad
                    ? "player-radius-road-node"
                    : "player-radius-roadside-position",
                SourceCaptureId = string.Empty,
                AuthoredPosition = playerPosition,
                SceneCenter = actorPosition,
                ActorPosition = actorPosition,
                Heading = vehicleHeading,
                VehicleStagingPosition = vehiclePosition,
                VehicleHeading = vehicleHeading,
                VehicleRoutePosition = routePosition,
                VehicleRouteHeading = routeHeading,
                VehicleStagingMode = movingRoad ? "moving_road" : "parked_ground",
                HasVehicleStagingPosition = true,
                HasVehicleRoute = movingRoad,
                IsSurveyedPoint = false,
                MaximumSnapDistance = 0f,
                ExteriorRoadHeightTolerance = MovingRoadElevationTolerance,
                IsInteriorScene = false,
                CatalogInteriorId = string.Empty,
                LiveInteriorId = 0,
                IncidentType = incidentType ?? string.Empty,
                ValidationStatus = movingRoad
                    ? "RuntimeValidatedPlayerRadiusRoadNodes_NoLocationRecord"
                    : "RuntimeValidatedPlayerRadiusRoadsideGround_NoLocationRecord",
                LocationReviewStatus = "RuntimePlayerRadius_NoSurveyedLocation"
            };
            Log("AMBIENT_WORLD_PLAYER_RADIUS_VEHICLE_SCENE_RESOLVED",
                "IncidentType=" + (incidentType ?? string.Empty)
                + "; VehicleActivity=" + vehicleActivity
                + "; LocationCatalogSite=None"
                + "; PlayerPosition=" + playerPosition
                + "; ScenePosition=" + actorPosition
                + "; VehiclePosition=" + vehiclePosition
                + "; VehicleHeading=" + vehicleHeading.ToString("0.0", CultureInfo.InvariantCulture)
                + "; RoutePosition=" + (movingRoad ? routePosition.ToString() : "NotRequired")
                + "; PlayerDistance=" + Distance2D(vehiclePosition, playerPosition)
                    .ToString("0.0", CultureInfo.InvariantCulture)
                + "; RequiredPlayerDistance=" + fallbackMinimumDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "-" + fallbackMaximumDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + "; Validation=" + scene.ValidationStatus);
            LogLocationResolution(scene, true);
            return true;
        }

        private bool TryResolvePlayerRadiusOutdoorFootScene(
            string incidentType,
            Vector3 playerPosition,
            out AmbientDispatchSceneResolution scene,
            out string reason)
        {
            scene = null;
            reason = "Stage=PlayerRadiusOutdoorFootReference; Result=NoSafeCandidate";
            if (playerPosition == Vector3.Zero)
            {
                reason = "Stage=PlayerRadiusOutdoorFootIdentity; Native=PlayerPosition; Result=Invalid";
                return false;
            }

            LSPDDispatchLocationDefinition runtimeArea =
                LSPDDispatchLocationDefinition.ForPlayerRadiusOutdoorFootScene(playerPosition);
            float minimumPlayerDistance;
            float maximumPlayerDistance;
            GetPlayerRadiusFallbackDistanceRange(
                out minimumPlayerDistance, out maximumPlayerDistance);
            float[] probeDistances = BuildPlayerRadiusOutdoorFootProbeDistances(
                minimumPlayerDistance, maximumPlayerDistance);
            List<Vector2> directions = PlayerRadiusOutdoorFootProbeDirections.ToList();
            Shuffle(directions);
            string lastFailure = reason;
            foreach (float distance in probeDistances)
            {
                foreach (Vector2 direction in directions)
                {
                    Vector3 query = playerPosition + new Vector3(
                        direction.X * distance, direction.Y * distance, 0f);
                    Vector3 safePosition;
                    Vector3 approachPosition;
                    string exteriorEvidence;
                    string placementFailure;
                    if (!TryValidateDispatchExteriorPosition(
                        query,
                        9f,
                        MovingRoadElevationTolerance,
                        playerPosition,
                        playerPosition,
                        GetDispatchExteriorMinimumOpenFootprintDirections(
                            null, incidentType, "PlayerRadiusOutdoorFoot"),
                        null,
                        out safePosition,
                        out approachPosition,
                        out exteriorEvidence,
                        out placementFailure))
                    {
                        lastFailure = placementFailure;
                        continue;
                    }

                    float playerDistance = Distance2D(safePosition, playerPosition);
                    if (playerDistance < minimumPlayerDistance
                        || playerDistance > maximumPlayerDistance)
                    {
                        lastFailure = "Stage=PlayerRadiusOutdoorFootRange; PlayerDistance="
                            + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Result=Rejected";
                        continue;
                    }

                    float heading = (float)(Math.Atan2(
                        playerPosition.X - safePosition.X,
                        playerPosition.Y - safePosition.Y) * 180d / Math.PI);
                    scene = new AmbientDispatchSceneResolution
                    {
                        Area = runtimeArea,
                        AreaLocationId = runtimeArea.Id,
                        AreaAnchorPosition = playerPosition,
                        LocationId = string.Empty,
                        LocationName = "Player-radius validated outdoor scene",
                        LocationCategory = "PlayerRadiusOutdoorFoot",
                        PointId = "player-radius-outdoor-foot-position",
                        SourceCaptureId = string.Empty,
                        AuthoredPosition = playerPosition,
                        SceneCenter = safePosition,
                        ActorPosition = safePosition,
                        Heading = heading,
                        VehicleStagingPosition = Vector3.Zero,
                        VehicleHeading = 0f,
                        VehicleRoutePosition = Vector3.Zero,
                        VehicleRouteHeading = 0f,
                        VehicleStagingMode = "none",
                        HasVehicleStagingPosition = false,
                        HasVehicleRoute = false,
                        IsSurveyedPoint = false,
                        MaximumSnapDistance = 0f,
                        ExteriorRoadHeightTolerance = MovingRoadElevationTolerance,
                        MinimumExteriorOpenFootprintDirections =
                            GetDispatchExteriorMinimumOpenFootprintDirections(
                                runtimeArea, incidentType, "PlayerRadiusOutdoorFoot"),
                        IsInteriorScene = false,
                        CatalogInteriorId = string.Empty,
                        LiveInteriorId = 0,
                        IncidentType = incidentType ?? string.Empty,
                        ValidationStatus = "RuntimeValidatedPlayerRadiusOutdoorFoot_NoLocationRecord",
                        LocationReviewStatus = "RuntimePlayerRadius_NoSurveyedLocation"
                    };
                    Log("AMBIENT_WORLD_PLAYER_RADIUS_OUTDOOR_FOOT_SCENE_RESOLVED",
                        "IncidentType=" + (incidentType ?? string.Empty)
                        + "; LocationCatalogSite=None"
                        + "; PlayerPosition=" + playerPosition
                        + "; ScenePosition=" + safePosition
                        + "; PlayerDistance=" + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; RequiredPlayerDistance=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Native=GET_INTERIOR_AT_COORDS/GET_SAFE_COORD_FOR_PED/"
                            + "GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD/GET_NEXT_POSITION_ON_STREET"
                        + "; Validation=" + scene.ValidationStatus);
                    ClearDebugTransition("AMBIENT_WORLD_PLAYER_RADIUS_OUTDOOR_FOOT_SCENE_REJECTED");
                    LogLocationResolution(scene, false);
                    reason = string.Empty;
                    return true;
                }
            }

            reason = lastFailure;
            LogDebugTransition("AMBIENT_WORLD_PLAYER_RADIUS_OUTDOOR_FOOT_SCENE_REJECTED",
                (incidentType ?? string.Empty) + "|" + GetAmbientFailureStage(reason),
                "IncidentType=" + (incidentType ?? string.Empty)
                + "; PlayerPosition=" + playerPosition
                + "; ProbeCount=" + (probeDistances.Length
                    * PlayerRadiusOutdoorFootProbeDirections.Length)
                + "; FirstRejectedStage=" + reason);
            return false;
        }

        private List<Vector3> BuildPlayerRadiusRoadsideCandidates(
            Vector3 query,
            float roadHeading)
        {
            var candidates = new List<Vector3>();
            try
            {
                Vector3 roadside;
                if (World.GetPositionOnRoadside(query, Direction.Any, out roadside)
                    && roadside != Vector3.Zero)
                    candidates.Add(roadside);
            }
            catch
            {
            }

            float radians = roadHeading * (float)Math.PI / 180f;
            Vector3 right = new Vector3(
                (float)Math.Cos(radians), (float)-Math.Sin(radians), 0f);
            var lateralCandidates = new List<Vector3>();
            foreach (float offset in PlayerRadiusRoadsideLateralOffsets)
            {
                lateralCandidates.Add(query + (right * offset));
                lateralCandidates.Add(query - (right * offset));
            }
            Shuffle(lateralCandidates);
            candidates.AddRange(lateralCandidates);
            return candidates;
        }

        private bool TryResolvePlayerRadiusRoadFootScene(
            string incidentType,
            Vector3 playerPosition,
            Vector3 playerRadiusRoadAnchor,
            out AmbientDispatchSceneResolution scene,
            out string reason)
        {
            scene = null;
            reason = "Stage=PlayerRadiusRoadFootReference; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; Result=false";
            LSPDDispatchLocationDefinition runtimeArea =
                LSPDDispatchLocationDefinition.ForPlayerRadiusRoadFootScene(playerPosition);
            Vector3 roadQuery = playerRadiusRoadAnchor == Vector3.Zero
                ? playerPosition : playerRadiusRoadAnchor;
            Vector3 roadReference;
            float referenceHeading;
            float referenceDistance;
            string referenceFailure;
            if (!TryGetClosestDispatchVehicleRoadNode(
                roadQuery,
                MovingRoadReferenceSnapDistance,
                MovingRoadElevationTolerance,
                "PlayerRadiusRoadFootReference",
                out roadReference,
                out referenceHeading,
                out referenceDistance,
                out referenceFailure,
                true))
            {
                reason = referenceFailure;
                return false;
            }

            float radians = referenceHeading * (float)Math.PI / 180f;
            Vector3 forward = new Vector3(
                (float)Math.Sin(radians), (float)Math.Cos(radians), 0f);
            float minimumPlayerDistance;
            float maximumPlayerDistance;
            GetPlayerRadiusFallbackDistanceRange(
                out minimumPlayerDistance, out maximumPlayerDistance);
            float[] fallbackRoadOffsets = BuildPlayerRadiusFallbackRoadOffsets(
                minimumPlayerDistance, maximumPlayerDistance);
            string lastFailure = reason;
            foreach (float offset in fallbackRoadOffsets)
            {
                Vector3 query = roadReference + (forward * offset);
                List<Vector3> roadsideCandidates = BuildPlayerRadiusRoadsideCandidates(
                    query, referenceHeading);
                if (roadsideCandidates.Count == 0)
                {
                    lastFailure = "Stage=PlayerRadiusRoadFootPosition; Native=GET_POSITION_ON_ROADSIDE; Query="
                        + query + "; Result=false";
                    continue;
                }
                foreach (Vector3 roadsideCandidate in roadsideCandidates)
                {
                Vector3 roadside = roadsideCandidate;

                float playerDistance = Distance2D(roadside, playerPosition);
                if (playerDistance < minimumPlayerDistance
                    || playerDistance > maximumPlayerDistance)
                {
                    lastFailure = "Stage=PlayerRadiusRoadFootRange; PlayerDistance="
                        + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Result=Rejected";
                    continue;
                }

                int interiorId;
                bool onRoad;
                float groundZ;
                Vector3 groundNormal;
                bool groundFound;
                try
                {
                    interiorId = Function.Call<int>(
                        Hash.GET_INTERIOR_AT_COORDS,
                        roadside.X, roadside.Y, roadside.Z);
                    onRoad = Function.Call<bool>(
                        Hash.IS_POINT_ON_ROAD,
                        roadside.X, roadside.Y, roadside.Z, 0);
                    groundFound = World.GetGroundHeightAndNormal(
                        roadside, out groundZ, out groundNormal);
                }
                catch (Exception ex)
                {
                    lastFailure = "Stage=PlayerRadiusRoadFootGround; Native=GET_INTERIOR_AT_COORDS/"
                        + "GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; Position=" + roadside
                        + "; Result=Exception:" + ex.GetType().Name;
                    continue;
                }
                if (!groundFound)
                {
                    try
                    {
                        Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                            roadside.X, roadside.Y, roadside.Z);
                    }
                    catch { }
                    lastFailure = "Stage=PlayerRadiusRoadFootGround; Native=GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; Position="
                        + roadside + "; Result=false; CollisionRequested=true";
                    continue;
                }
                if (interiorId != 0 || onRoad
                    || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || float.IsNaN(groundNormal.Z) || float.IsInfinity(groundNormal.Z)
                    || groundNormal.Z < 0.65f || Math.Abs(roadside.Z - groundZ) > 3f)
                {
                    lastFailure = "Stage=PlayerRadiusRoadFootGround; Native=GET_INTERIOR_AT_COORDS/"
                        + "IS_POINT_ON_ROAD/GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; InteriorId=" + interiorId
                        + "; OnRoad=" + onRoad
                        + "; GroundZ=" + groundZ.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; NormalZ=" + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Result=Rejected";
                    continue;
                }
                roadside.Z = groundZ;

                Vector3 roadNode;
                float roadHeading;
                float roadSeparation;
                string roadNodeFailure;
                if (!TryGetClosestDispatchVehicleRoadNode(
                    roadside,
                    14f,
                    MovingRoadElevationTolerance,
                    "PlayerRadiusRoadFootOffset",
                    out roadNode,
                    out roadHeading,
                    out roadSeparation,
                    out roadNodeFailure,
                    true)
                    || roadSeparation < 1.25f || roadSeparation > 14f
                    || Math.Abs(roadside.Z - roadNode.Z) > MovingRoadElevationTolerance)
                {
                    lastFailure = string.IsNullOrWhiteSpace(roadNodeFailure)
                        ? "Stage=PlayerRadiusRoadFootLaneClearance; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; RoadSeparation="
                            + roadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; RequiredRange=1.25-14; Result=Rejected"
                        : roadNodeFailure;
                    continue;
                }

                Vector3 safePedPosition;
                Vector3 approachPosition;
                string exteriorEvidence;
                string pedFailure;
                if (!TryValidateDispatchExteriorPosition(
                    roadside,
                    10f,
                    MovingRoadElevationTolerance,
                    playerRadiusRoadAnchor,
                    playerRadiusRoadAnchor,
                    GetDispatchExteriorMinimumOpenFootprintDirections(
                        null, incidentType, "road_foot"),
                    null,
                    out safePedPosition,
                    out approachPosition,
                    out exteriorEvidence,
                    out pedFailure))
                {
                    lastFailure = pedFailure;
                    continue;
                }
                float participantDistance = Distance2D(safePedPosition, roadside);
                if (participantDistance > 10f)
                {
                    lastFailure = "Stage=PlayerRadiusRoadFootParticipantSpacing; Native=GET_SAFE_COORD_FOR_PED; ParticipantDistance="
                        + participantDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Maximum=10; Result=Rejected";
                    continue;
                }
                float resolvedPlayerDistance = Distance2D(safePedPosition, playerPosition);
                if (resolvedPlayerDistance < minimumPlayerDistance
                    || resolvedPlayerDistance > maximumPlayerDistance)
                {
                    lastFailure = "Stage=PlayerRadiusRoadFootResolvedRange; PlayerDistance="
                        + resolvedPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Result=Rejected";
                    continue;
                }

                scene = new AmbientDispatchSceneResolution
                {
                    Area = runtimeArea,
                    AreaLocationId = runtimeArea.Id,
                    AreaAnchorPosition = playerPosition,
                    LocationId = string.Empty,
                    LocationName = "Player-radius validated roadside foot scene",
                    LocationCategory = "PlayerRadiusRoadsideFoot",
                    PointId = "player-radius-road-foot-position",
                    SourceCaptureId = string.Empty,
                    AuthoredPosition = playerPosition,
                    SceneCenter = safePedPosition,
                    ActorPosition = safePedPosition,
                    Heading = roadHeading,
                    VehicleStagingPosition = Vector3.Zero,
                    VehicleHeading = 0f,
                    VehicleRoutePosition = Vector3.Zero,
                    VehicleRouteHeading = 0f,
                    VehicleStagingMode = "none",
                    HasVehicleStagingPosition = false,
                    HasVehicleRoute = false,
                    IsSurveyedPoint = false,
                    MaximumSnapDistance = 0f,
                    ExteriorRoadHeightTolerance = MovingRoadElevationTolerance,
                    MinimumExteriorOpenFootprintDirections =
                        GetDispatchExteriorMinimumOpenFootprintDirections(
                            runtimeArea, incidentType, "road_foot"),
                    IsInteriorScene = false,
                    CatalogInteriorId = string.Empty,
                    LiveInteriorId = 0,
                    IncidentType = incidentType ?? string.Empty,
                    ValidationStatus = "RuntimeValidatedPlayerRadiusRoadFoot_NoLocationRecord",
                    LocationReviewStatus = "RuntimePlayerRadius_NoSurveyedLocation"
                };
                Log("AMBIENT_WORLD_PLAYER_RADIUS_ROAD_FOOT_SCENE_RESOLVED",
                    "IncidentType=" + (incidentType ?? string.Empty)
                    + "; LocationCatalogSite=None"
                    + "; PlayerPosition=" + playerPosition
                    + "; ScenePosition=" + safePedPosition
                    + "; RoadReference=" + roadReference
                    + "; RoadSeparation=" + roadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; PlayerDistance=" + Distance2D(safePedPosition, playerPosition)
                        .ToString("0.0", CultureInfo.InvariantCulture)
                    + "; RequiredPlayerDistance=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Validation=" + scene.ValidationStatus);
                ClearDebugTransition("AMBIENT_WORLD_PLAYER_RADIUS_ROAD_FOOT_SCENE_REJECTED");
                LogLocationResolution(scene, false);
                reason = string.Empty;
                return true;
                }
            }

            reason = lastFailure;
            LogDebugTransition("AMBIENT_WORLD_PLAYER_RADIUS_ROAD_FOOT_SCENE_REJECTED",
                (incidentType ?? string.Empty) + "|" + GetAmbientFailureStage(reason),
                "IncidentType=" + (incidentType ?? string.Empty)
                + "; PlayerPosition=" + playerPosition
                + "; RoadReference=" + roadReference
                + "; FirstRejectedStage=" + reason);
            return false;
        }

        private bool TryResolvePlayerRadiusParkedVehicleStaging(
            Vector3 playerPosition,
            Vector3 playerRadiusRoadAnchor,
            out Vector3 actorPosition,
            out Vector3 vehiclePosition,
            out float vehicleHeading,
            out string reason,
            bool allowNativeRoadNodeWhenRoadFlagFails,
            Vector3? fixedRoadsideCandidatePosition,
            out Vector3 pendingCandidatePosition)
        {
            actorPosition = Vector3.Zero;
            vehiclePosition = Vector3.Zero;
            vehicleHeading = 0f;
            pendingCandidatePosition = Vector3.Zero;
            reason = "Stage=PlayerRadiusRoadsideReference; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; Result=false";
            try
            {
                Vector3 roadQuery = playerRadiusRoadAnchor == Vector3.Zero
                    ? playerPosition : playerRadiusRoadAnchor;
                Vector3 roadReference;
                float referenceHeading;
                float referenceDistance;
                string referenceFailure;
                if (!TryGetClosestDispatchVehicleRoadNode(
                    roadQuery,
                    MovingRoadReferenceSnapDistance,
                    MovingRoadElevationTolerance,
                    "PlayerRadiusRoadsideReference",
                    out roadReference,
                    out referenceHeading,
                    out referenceDistance,
                    out referenceFailure,
                    allowNativeRoadNodeWhenRoadFlagFails))
                {
                    reason = referenceFailure;
                    return false;
                }

                float radians = referenceHeading * (float)Math.PI / 180f;
                Vector3 forward = new Vector3(
                    (float)Math.Sin(radians), (float)Math.Cos(radians), 0f);
                float minimumPlayerDistance;
                float maximumPlayerDistance;
                GetPlayerRadiusFallbackDistanceRange(
                    out minimumPlayerDistance, out maximumPlayerDistance);
                float[] fallbackRoadOffsets = BuildPlayerRadiusFallbackRoadOffsets(
                    minimumPlayerDistance, maximumPlayerDistance);
                string lastFailure = reason;
                string pendingCollisionFailure = string.Empty;
                var roadsideCandidatesToValidate = new List<Vector3>();
                if (fixedRoadsideCandidatePosition.HasValue)
                {
                    roadsideCandidatesToValidate.Add(fixedRoadsideCandidatePosition.Value);
                }
                else
                {
                    foreach (float offset in fallbackRoadOffsets)
                    {
                        Vector3 query = roadReference + (forward * offset);
                        List<Vector3> roadsideCandidates = BuildPlayerRadiusRoadsideCandidates(
                            query, referenceHeading);
                        if (roadsideCandidates.Count == 0)
                        {
                            lastFailure = "Stage=PlayerRadiusRoadside; Native=GET_POSITION_ON_ROADSIDE; Result=false";
                            continue;
                        }
                        roadsideCandidatesToValidate.AddRange(roadsideCandidates);
                    }
                }
                foreach (Vector3 roadsideCandidate in roadsideCandidatesToValidate)
                {
                    Vector3 roadside = roadsideCandidate;

                    float playerDistance = Distance2D(roadside, playerPosition);
                    if (playerDistance < minimumPlayerDistance
                        || playerDistance > maximumPlayerDistance)
                    {
                        lastFailure = "Stage=PlayerRadiusRoadsideRange; PlayerDistance="
                            + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Result=Rejected";
                        continue;
                    }

                    int interiorId = Function.Call<int>(
                        Hash.GET_INTERIOR_AT_COORDS, roadside.X, roadside.Y, roadside.Z);
                    bool onRoad = Function.Call<bool>(
                        Hash.IS_POINT_ON_ROAD, roadside.X, roadside.Y, roadside.Z, 0);
                    float groundZ;
                    Vector3 groundNormal;
                    bool groundFound = World.GetGroundHeightAndNormal(
                        roadside, out groundZ, out groundNormal);
                    if (interiorId != 0 || onRoad || !groundFound
                        || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                        || groundNormal.Z < 0.65f || Math.Abs(roadside.Z - groundZ) > 3f)
                    {
                        lastFailure = "Stage=PlayerRadiusRoadsideGround; Native=GET_INTERIOR_AT_COORDS/"
                            + "IS_POINT_ON_ROAD/GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; InteriorId="
                            + interiorId + "; OnRoad=" + onRoad + "; GroundFound=" + groundFound
                            + "; Position=" + roadside
                            + "; NormalZ=" + (groundFound ? groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture) : "Unavailable")
                            + "; Result=Rejected";
                        if (!groundFound && interiorId == 0 && !onRoad)
                        {
                            try { Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                                roadside.X, roadside.Y, roadside.Z); }
                            catch { }
                            if (string.IsNullOrWhiteSpace(pendingCollisionFailure))
                            {
                                pendingCollisionFailure = "Stage=PlayerRadiusRoadsideGround; Native=GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD"
                                    + "; Position=" + roadside
                                    + "; InteriorId=" + interiorId
                                    + "; OnRoad=" + onRoad
                                    + "; CollisionRequested=true; Result=CollisionPending";
                                pendingCandidatePosition = roadside;
                            }
                            break;
                        }
                        continue;
                    }
                    roadside.Z = groundZ;

                    Vector3 roadNode;
                    float roadHeading;
                    float roadSeparation;
                    string roadNodeFailure;
                    if (!TryGetClosestDispatchVehicleRoadNode(
                        roadside,
                        14f,
                        MovingRoadElevationTolerance,
                        "PlayerRadiusRoadsideOffset",
                        out roadNode,
                        out roadHeading,
                        out roadSeparation,
                        out roadNodeFailure,
                        allowNativeRoadNodeWhenRoadFlagFails)
                        || roadSeparation < 1.25f || roadSeparation > 14f
                        || Math.Abs(roadside.Z - roadNode.Z) > MovingRoadElevationTolerance)
                    {
                        lastFailure = string.IsNullOrWhiteSpace(roadNodeFailure)
                            ? "Stage=PlayerRadiusRoadsideOffset; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; "
                                + "RoadSeparation=" + roadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                                + "; RequiredRange=1.25-14; Result=Rejected"
                            : roadNodeFailure;
                        continue;
                    }
                    if (HasOverlappingVehicle(roadside, 2.35f)
                        || HasOverlappingPed(roadside, 2.0f))
                    {
                        lastFailure = "Stage=PlayerRadiusRoadsideOccupancy; Native=GET_NEARBY_VEHICLES/GET_NEARBY_PEDS; Result=Occupied";
                        continue;
                    }

                    Vector3 safePedPosition;
                    Vector3 approachPosition;
                    string exteriorEvidence;
                    string pedFailure;
                    if (!TryValidateDispatchExteriorPosition(
                        roadside,
                        10f,
                        MovingRoadElevationTolerance,
                        playerPosition,
                        playerRadiusRoadAnchor,
                        GetDispatchExteriorMinimumOpenFootprintDirections(
                            null, null, "road_foot"),
                        null,
                        out safePedPosition,
                        out approachPosition,
                        out exteriorEvidence,
                        out pedFailure))
                    {
                        lastFailure = pedFailure;
                        continue;
                    }
                    if (Distance2D(safePedPosition, roadside) > 12f)
                    {
                        lastFailure = "Stage=PlayerRadiusRoadsideParticipantSpacing; Native=GET_SAFE_COORD_FOR_PED; "
                            + "ParticipantDistance=" + Distance2D(safePedPosition, roadside)
                                .ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Maximum=12; Result=Rejected";
                        continue;
                    }

                    actorPosition = safePedPosition;
                    vehiclePosition = roadside;
                    vehicleHeading = roadHeading;
                    reason = string.Empty;
                    return true;
                }

                reason = !string.IsNullOrWhiteSpace(pendingCollisionFailure)
                    ? "PENDING:" + pendingCollisionFailure
                        + "; RetryRequiresGroundNormalAndClearanceRevalidation=true"
                    : lastFailure;
                return false;
            }
            catch (Exception ex)
            {
                reason = "Stage=PlayerRadiusRoadsideProbe; Native=GET_POSITION_ON_ROADSIDE/"
                    + "GET_INTERIOR_AT_COORDS/IS_POINT_ON_ROAD/GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD/"
                    + "GET_CLOSEST_VEHICLE_NODE_WITH_HEADING/GET_SAFE_COORD_FOR_PED; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        private bool TryValidateOutdoorPedPosition(
            Vector3 authoredPosition,
            float maximumCorrectionDistance,
            float maximumStreetHeightDelta,
            out Vector3 safePosition,
            out string reason,
            ISet<int> excludedPedHandles = null,
            bool allowBoundedVerticalOriginOffset = false)
        {
            safePosition = authoredPosition;
            reason = string.Empty;
            try
            {
                // This helper validates exterior pedestrian placements only.
                // Captured interiors must use mapped access and live-interior
                // validation rather than being promoted through this path.
                int authoredInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    authoredPosition.X,
                    authoredPosition.Y,
                    authoredPosition.Z);
                if (authoredInteriorId != 0)
                {
                    reason = "Stage=ExteriorInteriorIdentity; Native=GET_INTERIOR_AT_COORDS"
                        + "; AuthoredInteriorId=" + authoredInteriorId;
                    return false;
                }

                Vector3 safe = authoredPosition;
                GetSafePositionFlags flags = GetSafePositionFlags.NotInterior
                    | GetSafePositionFlags.NotWater;
                if (!World.GetSafePositionForPed(authoredPosition, out safe, flags))
                {
                    reason = "Stage=ExteriorNavigation; Native=GET_SAFE_COORD_FOR_PED; Result=false";
                    return false;
                }
                if (!IsDispatchExteriorNavigationSnapValid(
                    authoredPosition,
                    safe,
                    maximumCorrectionDistance,
                    allowBoundedVerticalOriginOffset,
                    DispatchExteriorActorMaximumOriginHeightAboveGround))
                {
                    reason = "Stage=ExteriorNavigationSnap; Native=GET_SAFE_COORD_FOR_PED"
                        + "; Requested=" + authoredPosition
                        + "; Resolved=" + safe
                        + "; Distance=" + safe.DistanceTo(authoredPosition).ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; HorizontalDistance=" + Distance2D(safe, authoredPosition).ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; VerticalDistance=" + Math.Abs(safe.Z - authoredPosition.Z).ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; BoundedVerticalOriginOffset=" + allowBoundedVerticalOriginOffset
                        + "; Maximum=" + maximumCorrectionDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture);
                    return false;
                }

                int safeInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS, safe.X, safe.Y, safe.Z);
                if (safeInteriorId != 0)
                {
                    reason = "Stage=ExteriorInteriorIdentity; Native=GET_INTERIOR_AT_COORDS"
                        + "; ResolvedInteriorId=" + safeInteriorId;
                    return false;
                }

                float groundZ;
                Vector3 groundNormal;
                bool groundHeightFound = World.GetGroundHeightAndNormal(
                    safe, out groundZ, out groundNormal);
                if (!groundHeightFound
                    || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || groundNormal.Z < 0.55f
                    || Math.Abs(safe.Z - groundZ) > 3f)
                {
                    reason = "Stage=ExteriorGroundNormal; Native=GET_GROUND_HEIGHT_AND_NORMAL"
                        + "; GroundHeightFound=" + groundHeightFound
                        + "; GroundZ=" + groundZ.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; NormalZ=" + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; SafeZ=" + safe.Z.ToString("0.00", CultureInfo.InvariantCulture);
                    return false;
                }
                safe.Z = groundZ;

                Vector3 nearestStreet = World.GetNextPositionOnStreet(safe);
                float streetDistance = Distance2D(safe, nearestStreet);
                float streetHeightDelta = safe.Z - nearestStreet.Z;
                if (maximumStreetHeightDelta > 0f
                    && nearestStreet != Vector3.Zero
                    && streetDistance <= DispatchStreetHeightReferenceRadius
                    && Math.Abs(streetHeightDelta) > maximumStreetHeightDelta)
                {
                    reason = "Stage=ExteriorStreetHeight; Native=GET_NEXT_POSITION_ON_STREET"
                        + "; Candidate=" + safe
                        + "; StreetNode=" + nearestStreet
                        + "; HorizontalDistance=" + streetDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; VerticalDelta=" + streetHeightDelta.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; Maximum=" + maximumStreetHeightDelta.ToString(
                            "0.00", CultureInfo.InvariantCulture);
                    return false;
                }
                if (streetDistance < 1.25f)
                {
                    reason = "Stage=ExteriorRoadLane; Native=GET_NEXT_POSITION_ON_STREET"
                        + "; HorizontalDistance=" + streetDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture);
                    return false;
                }

                if (HasOverlappingPed(safe, 1.35f, excludedPedHandles))
                {
                    reason = "Stage=ExteriorPedOccupancy; Native=ENTITY_CLEARANCE"
                        + "; Result=Another pedestrian occupies the candidate point.";
                    return false;
                }
                if (HasOverlappingVehicle(safe, 1.8f))
                {
                    reason = "Stage=ExteriorVehicleOccupancy; Native=ENTITY_CLEARANCE"
                        + "; Result=A vehicle overlaps the candidate pedestrian point.";
                    return false;
                }

                safePosition = safe;
                return true;
            }
            catch (Exception ex)
            {
                reason = "Stage=ExteriorRuntimeProbe; Native=GET_INTERIOR_AT_COORDS/"
                    + "GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL/"
                    + "GET_NEXT_POSITION_ON_STREET; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        internal static bool IsDispatchExteriorGeometryEvidenceValid(
            int unobstructedDirections,
            bool outdoorApproachConnected)
        {
            return IsDispatchExteriorGeometryEvidenceValid(
                unobstructedDirections,
                outdoorApproachConnected,
                DispatchExteriorMinimumOpenFootprintDirections);
        }

        internal static bool IsDispatchExteriorGeometryEvidenceValid(
            int unobstructedDirections,
            bool outdoorApproachConnected,
            int minimumOpenDirections)
        {
            int requiredDirections = Math.Max(2, Math.Min(8, minimumOpenDirections));
            return unobstructedDirections >= requiredDirections
                && outdoorApproachConnected;
        }

        internal static bool IsDispatchExteriorEntityIdentityValid(
            int entityInteriorId,
            int coordinateInteriorId,
            int roomKey)
        {
            return entityInteriorId == 0 && coordinateInteriorId == 0 && roomKey == 0;
        }

        internal static bool IsDispatchExteriorLocationOffsetValid(
            Vector3 authoredPosition,
            Vector3 candidatePosition,
            float maximumOffset)
        {
            float distance = candidatePosition.DistanceTo(authoredPosition);
            return !float.IsNaN(distance) && !float.IsInfinity(distance)
                && distance <= maximumOffset;
        }

        internal static float GetDispatchExteriorSceneCenterOffsetLimit(
            float existingLimit,
            bool footActivityCandidate)
        {
            return footActivityCandidate
                ? DispatchExteriorFootCatalogSceneOffsetLimit
                : existingLimit;
        }

        internal static bool IsDispatchExteriorActorGroundAlignmentValid(
            Vector3 actorPosition,
            Vector3 validatedGroundPosition,
            float heightAboveGround,
            bool inAir,
            float maximumHorizontalOffset,
            float maximumOriginHeightAboveGround,
            float maximumOriginBelowGround)
        {
            float horizontalOffset = Distance2D(actorPosition, validatedGroundPosition);
            float originGroundOffset = actorPosition.Z - validatedGroundPosition.Z;
            // The resolved ground/approach/clearance checks establish a real
            // walkable surface below this exact outdoor point. Permit a short
            // controlled staging drop above that surface; do not mistake the
            // Ped's raised origin for a shell/void placement failure.
            bool groundedOrBoundedStagingDrop = !inAir
                || heightAboveGround <= maximumOriginHeightAboveGround;
            return groundedOrBoundedStagingDrop
                && !float.IsNaN(heightAboveGround)
                && !float.IsInfinity(heightAboveGround)
                && heightAboveGround >= -maximumOriginBelowGround
                && heightAboveGround <= maximumOriginHeightAboveGround
                && !float.IsNaN(horizontalOffset)
                && !float.IsInfinity(horizontalOffset)
                && horizontalOffset <= maximumHorizontalOffset
                && !float.IsNaN(originGroundOffset)
                && !float.IsInfinity(originGroundOffset)
                && originGroundOffset >= -maximumOriginBelowGround
                && originGroundOffset <= maximumOriginHeightAboveGround;
        }

        internal static bool IsDispatchExteriorNavigationSnapValid(
            Vector3 requestedPosition,
            Vector3 resolvedPosition,
            float maximumCorrectionDistance,
            bool allowBoundedVerticalOriginOffset,
            float maximumVerticalOffset)
        {
            float horizontalDistance = Distance2D(requestedPosition, resolvedPosition);
            float verticalDistance = Math.Abs(
                resolvedPosition.Z - requestedPosition.Z);
            float fullDistance = requestedPosition.DistanceTo(resolvedPosition);
            float maximumHorizontalDistance = allowBoundedVerticalOriginOffset
                ? Math.Min(maximumCorrectionDistance,
                    DispatchExteriorActorMaximumHorizontalGroundOffset)
                : maximumCorrectionDistance;
            bool validHorizontal = !float.IsNaN(horizontalDistance)
                && !float.IsInfinity(horizontalDistance)
                && horizontalDistance <= maximumHorizontalDistance;
            if (allowBoundedVerticalOriginOffset)
                return validHorizontal
                    && !float.IsNaN(verticalDistance)
                    && !float.IsInfinity(verticalDistance)
                    && verticalDistance <= maximumVerticalOffset;
            return !float.IsNaN(fullDistance)
                && !float.IsInfinity(fullDistance)
                && fullDistance <= maximumCorrectionDistance;
        }

        internal static float GetDispatchExteriorNavigationSnapDistanceLimit(
            float existingLimit,
            bool footActivityCandidate)
        {
            return footActivityCandidate
                ? Math.Max(existingLimit,
                    DispatchExteriorFootCatalogNavigationSnapDistance)
                : existingLimit;
        }

        private static int GetDispatchExteriorMinimumOpenFootprintDirections(
            LSPDDispatchLocationDefinition area,
            string incidentType,
            string locationCategory,
            string locationContext = null,
            bool footActivityCandidate = false)
        {
            bool narrowOutdoorScene = footActivityCandidate
                || (area != null
                    && (area.SupportsContext("alley")
                        || area.SupportsContext("road_foot")
                        || area.SupportsContext("parking_lot")))
                || new[] { incidentType, locationCategory, locationContext }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Any(value => value.IndexOf("alley", StringComparison.OrdinalIgnoreCase) >= 0
                        || value.IndexOf("road_foot", StringComparison.OrdinalIgnoreCase) >= 0
                        || value.IndexOf("parking", StringComparison.OrdinalIgnoreCase) >= 0);
            return narrowOutdoorScene
                ? 2 : DispatchExteriorMinimumOpenFootprintDirections;
        }

        private bool IsFootActivityOnlySurveyCandidate(
            string locationId,
            string locationCategory)
        {
            if (_locationCatalog == null
                || !string.Equals(locationCategory, "SurveyedCandidate",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location;
            return _locationCatalog.TryGetLocation(locationId, out location)
                && string.Equals(location.IntendedUse, "FootActivityCandidate",
                    StringComparison.OrdinalIgnoreCase);
        }

        private bool TryValidateDispatchExteriorPosition(
            Vector3 requestedPosition,
            float maximumCorrectionDistance,
            float maximumStreetHeightDelta,
            Vector3 authoredAnchor,
            Vector3 areaAnchor,
            int minimumOpenFootprintDirections,
            ISet<int> excludedPedHandles,
            out Vector3 safePosition,
            out Vector3 approachPosition,
            out string evidence,
            out string reason,
            bool allowBoundedVerticalOriginOffset = false)
        {
            safePosition = requestedPosition;
            approachPosition = Vector3.Zero;
            evidence = string.Empty;
            if (!TryValidateOutdoorPedPosition(
                    requestedPosition,
                    maximumCorrectionDistance,
                    maximumStreetHeightDelta,
                    out safePosition,
                    out reason,
                    excludedPedHandles,
                    allowBoundedVerticalOriginOffset))
                return false;

            return TryValidateDispatchExteriorApproach(
                safePosition,
                authoredAnchor,
                areaAnchor,
                minimumOpenFootprintDirections,
                out approachPosition,
                out evidence,
                out reason);
        }

        private bool TryValidateDispatchExteriorApproach(
            Vector3 position,
            Vector3 authoredAnchor,
            Vector3 areaAnchor,
            int minimumOpenFootprintDirections,
            out Vector3 approachPosition,
            out string evidence,
            out string reason)
        {
            approachPosition = Vector3.Zero;
            evidence = string.Empty;
            reason = string.Empty;
            int clearDirections = 0;
            int validFootprintPoints = 0;
            int directionsProbed = 0;
            int requiredOpenDirections = Math.Max(2, Math.Min(
                8, minimumOpenFootprintDirections));
            bool minimumSatisfiedBeforeAllDirections = false;
            string lastFootprintFailure = "No exterior walk probes were available.";

            for (int index = 0; index < DispatchParticipantFallbackDirections.Length; index++)
            {
                Vector2 direction = DispatchParticipantFallbackDirections[index];
                float length = (float)Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
                if (length <= 0.001f)
                    continue;

                Vector3 requested = new Vector3(
                    position.X + direction.X / length * DispatchExteriorFootprintProbeRadius,
                    position.Y + direction.Y / length * DispatchExteriorFootprintProbeRadius,
                    position.Z);
                directionsProbed++;
                Vector3 probe;
                string probeFailure;
                if (!TryResolveDispatchExteriorFootPoint(
                        requested,
                        DispatchExteriorApproachAnchorSnapDistance,
                        out probe,
                        out probeFailure))
                {
                    lastFootprintFailure = probeFailure;
                    continue;
                }

                validFootprintPoints++;
                if (HasClearDispatchExteriorSegment(position, probe, out probeFailure))
                    clearDirections++;
                else
                    lastFootprintFailure = probeFailure;

                if (clearDirections >= requiredOpenDirections)
                {
                    minimumSatisfiedBeforeAllDirections = directionsProbed
                        < DispatchParticipantFallbackDirections.Length;
                    break;
                }
            }

            var anchors = new List<Vector3>();
            AddDispatchExteriorAnchor(anchors, authoredAnchor, position);
            AddDispatchExteriorAnchor(anchors, areaAnchor, position);
            try
            {
                Vector3 nearestStreet = World.GetNextPositionOnStreet(position);
                AddDispatchExteriorAnchor(anchors, nearestStreet, position);
            }
            catch (Exception ex)
            {
                reason = "Stage=ExteriorApproachAnchor; Native=GET_NEXT_POSITION_ON_STREET"
                    + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }

            Vector3 connectedAnchor = Vector3.Zero;
            string lastApproachFailure = "No outdoor entrance, pavement, area, or roadside anchor was within range.";
            float approachAnchorSnapDistance = minimumOpenFootprintDirections <= 2
                ? DispatchExteriorNarrowApproachAnchorSnapDistance
                : DispatchExteriorApproachAnchorSnapDistance;
            for (int index = 0; index < anchors.Count; index++)
            {
                Vector3 safeAnchor;
                string anchorFailure;
                if (!TryResolveDispatchExteriorFootPoint(
                        anchors[index],
                        approachAnchorSnapDistance,
                        out safeAnchor,
                        out anchorFailure))
                {
                    lastApproachFailure = anchorFailure;
                    continue;
                }

                float distance = Distance2D(position, safeAnchor);
                if (distance < DispatchExteriorMinimumApproachDistance
                    || distance > DispatchExteriorMaximumApproachDistance)
                {
                    lastApproachFailure = "Stage=ExteriorApproachRange; Distance="
                        + distance.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Allowed=" + DispatchExteriorMinimumApproachDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "-" + DispatchExteriorMaximumApproachDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture);
                    continue;
                }

                if (!TryValidateDispatchExteriorCorridor(position, safeAnchor, out anchorFailure))
                {
                    lastApproachFailure = anchorFailure;
                    continue;
                }

                connectedAnchor = safeAnchor;
                break;
            }

            bool approachConnected = connectedAnchor != Vector3.Zero;
            if (!IsDispatchExteriorGeometryEvidenceValid(
                    clearDirections, approachConnected, minimumOpenFootprintDirections))
            {
                reason = "Stage=ExteriorApproachGeometry; Native=GET_SAFE_COORD_FOR_PED/"
                    + "GET_GROUND_HEIGHT_AND_NORMAL/WORLD_RAYCAST; Position=" + position
                    + "; OpenFootprintDirections=" + clearDirections
                    + "; FootprintDirectionsProbed=" + directionsProbed
                    + "; FootprintMinimumSatisfiedEarly=" + minimumSatisfiedBeforeAllDirections
                    + "; ValidFootprintPoints=" + validFootprintPoints
                    + "; RequiredOpenDirections=" + requiredOpenDirections
                    + "; OutdoorApproachConnected=" + approachConnected
                    + "; LastFootprintFailure=" + lastFootprintFailure
                    + "; LastApproachFailure=" + lastApproachFailure;
                return false;
            }

            approachPosition = connectedAnchor;
            evidence = "ExteriorGround=Validated; OpenFootprintDirections=" + clearDirections
                + "/" + DispatchParticipantFallbackDirections.Length
                + "; FootprintDirectionsProbed=" + directionsProbed
                + "/" + DispatchParticipantFallbackDirections.Length
                + "; FootprintMinimumSatisfiedEarly=" + minimumSatisfiedBeforeAllDirections
                + "; RequiredOpenDirections=" + requiredOpenDirections
                + "; OutdoorApproach=" + connectedAnchor
                + "; ApproachRoute=SafePedSamplesAndClearMapObjectGlassRays"
                + "; InteriorIdZeroIsNotSufficient=true";
            return true;
        }

        private static void AddDispatchExteriorAnchor(
            IList<Vector3> anchors,
            Vector3 candidate,
            Vector3 position)
        {
            if (anchors == null || candidate == Vector3.Zero)
                return;
            float distance = Distance2D(candidate, position);
            if (float.IsNaN(distance) || float.IsInfinity(distance)
                || distance < DispatchExteriorMinimumApproachDistance
                || distance > DispatchExteriorMaximumApproachDistance)
                return;
            if (anchors.Any(anchor => Distance2D(anchor, candidate) < 1f))
                return;
            anchors.Add(candidate);
        }

        private bool TryResolveDispatchExteriorFootPoint(
            Vector3 requested,
            float maximumSnapDistance,
            out Vector3 safe,
            out string reason)
        {
            safe = requested;
            reason = string.Empty;
            try
            {
                int requestedInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS, requested.X, requested.Y, requested.Z);
                if (requestedInteriorId != 0)
                {
                    reason = "Stage=ExteriorApproachInterior; RequestedInteriorId="
                        + requestedInteriorId;
                    return false;
                }

                GetSafePositionFlags flags = GetSafePositionFlags.NotInterior
                    | GetSafePositionFlags.NotWater;
                if (!World.GetSafePositionForPed(requested, out safe, flags))
                {
                    reason = "Stage=ExteriorApproachNavigation; Native=GET_SAFE_COORD_FOR_PED; Result=false";
                    return false;
                }
                int safeInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS, safe.X, safe.Y, safe.Z);
                if (safeInteriorId != 0)
                {
                    reason = "Stage=ExteriorApproachInterior; ResolvedInteriorId="
                        + safeInteriorId;
                    return false;
                }
                float groundZ;
                Vector3 groundNormal;
                if (!World.GetGroundHeightAndNormal(safe, out groundZ, out groundNormal)
                    || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || groundNormal.Z < 0.55f || Math.Abs(safe.Z - groundZ) > 1.5f)
                {
                    reason = "Stage=ExteriorApproachGround; Native=GET_GROUND_HEIGHT_AND_NORMAL"
                        + "; GroundZ=" + groundZ.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; NormalZ=" + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture);
                    return false;
                }
                safe.Z = groundZ;
                // The safe-Ped native may return a Ped origin above its floor.
                // Compare ground with ground after validating that floor; a
                // real horizontal or terrain-height displacement still fails.
                float snap = safe.DistanceTo(requested);
                if (float.IsNaN(snap) || float.IsInfinity(snap) || snap > maximumSnapDistance)
                {
                    reason = "Stage=ExteriorApproachSnap; Native=GET_SAFE_COORD_FOR_PED"
                        + "; Requested=" + requested + "; Resolved=" + safe
                        + "; Distance=" + snap.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; Maximum=" + maximumSnapDistance.ToString(
                            "0.00", CultureInfo.InvariantCulture)
                        + "; Comparison=ValidatedGround";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "Stage=ExteriorApproachProbe; Native=GET_INTERIOR_AT_COORDS/"
                    + "GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL; Result=Exception:"
                    + ex.GetType().Name;
                return false;
            }
        }

        private bool TryValidateDispatchExteriorCorridor(
            Vector3 start,
            Vector3 destination,
            out string reason)
        {
            reason = string.Empty;
            float distance = Distance2D(start, destination);
            int segments = Math.Max(1, Math.Min(4, (int)Math.Ceiling(distance / 7.5f)));
            Vector3 previous = start;
            for (int index = 1; index <= segments; index++)
            {
                float fraction = index / (float)segments;
                Vector3 requested = new Vector3(
                    start.X + (destination.X - start.X) * fraction,
                    start.Y + (destination.Y - start.Y) * fraction,
                    start.Z + (destination.Z - start.Z) * fraction);
                Vector3 safe;
                if (!TryResolveDispatchExteriorFootPoint(
                        requested,
                        DispatchExteriorApproachAnchorSnapDistance,
                        out safe,
                        out reason))
                    return false;
                if (!HasClearDispatchExteriorSegment(previous, safe, out reason))
                    return false;
                previous = safe;
            }
            return true;
        }

        private static bool HasClearDispatchExteriorSegment(
            Vector3 fromGround,
            Vector3 toGround,
            out string reason)
        {
            reason = string.Empty;
            float[] rayHeights = { 0.85f, 1.35f };
            for (int index = 0; index < rayHeights.Length; index++)
            {
                Vector3 from = fromGround;
                Vector3 to = toGround;
                from.Z += rayHeights[index];
                to.Z += rayHeights[index];
                try
                {
                    RaycastResult hit = World.Raycast(
                        from,
                        to,
                        IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Glass,
                        null);
                    if (hit.DidHit)
                    {
                        reason = "Stage=ExteriorApproachObstructed; Native=WORLD_RAYCAST"
                            + "; Height=" + rayHeights[index].ToString(
                                "0.00", CultureInfo.InvariantCulture)
                            + "; From=" + fromGround + "; To=" + toGround
                            + "; HitEntity=" + (hit.HitEntity == null ? "WorldGeometry" : hit.HitEntity.Handle.ToString());
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    reason = "Stage=ExteriorApproachRaycast; Native=WORLD_RAYCAST"
                        + "; Result=Exception:" + ex.GetType().Name;
                    return false;
                }
            }
            return true;
        }

        private bool TryResolveVehicleStaging(
            Vector3 scenePosition,
            LSPDDispatchLocationDefinition area,
            Vector3 playerPosition,
            bool enforcePlayerDistanceWindow,
            out Vector3 stagingPosition,
            out float heading,
            out string reason)
        {
            stagingPosition = Vector3.Zero;
            heading = 0f;
            reason = "No nearby roadside candidate passed vehicle-staging validation.";
            string lastFailure = reason;
            Vector3 previousRoadside = Vector3.Zero;
            bool hasPreviousRoadside = false;
            try
            {
                // A single GTA roadside result may be blocked by a Ped/vehicle,
                // sit on a steep curb, or land outside the event area. Probe two
                // nearby origins as a bounded recovery while keeping the van
                // close to the validated scene and away from alley obstacles.
                for (int probeIndex = 0;
                    probeIndex < DispatchVehicleStagingProbeOffsets.Length;
                    probeIndex++)
                {
                    Vector2 offset = DispatchVehicleStagingProbeOffsets[probeIndex];
                    Vector3 probeOrigin = new Vector3(
                        scenePosition.X + offset.X,
                        scenePosition.Y + offset.Y,
                        scenePosition.Z);
                    Vector3 roadside = Vector3.Zero;
                    if (!World.GetPositionOnRoadside(
                        probeOrigin, Direction.Any, out roadside))
                    {
                        lastFailure = "GTA did not provide a roadside vehicle-staging position.";
                        continue;
                    }
                    if (hasPreviousRoadside
                        && Distance2D(previousRoadside, roadside) < 1f)
                    {
                        lastFailure = "Nearby roadside probes resolved to the same blocked position.";
                        continue;
                    }
                    previousRoadside = roadside;
                    hasPreviousRoadside = true;

                    int interiorId = Function.Call<int>(
                        Hash.GET_INTERIOR_AT_COORDS, roadside.X, roadside.Y, roadside.Z);
                    float groundZ;
                    Vector3 groundNormal;
                    if (interiorId != 0
                        || !World.GetGroundHeightAndNormal(
                            roadside, out groundZ, out groundNormal)
                        || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                        || groundNormal.Z < 0.65f
                        || Math.Abs(roadside.Z - groundZ) > 3f)
                    {
                        lastFailure = "The roadside candidate is interior or lacks suitable ground support.";
                        continue;
                    }
                    roadside.Z = groundZ;

                    float roadHeading;
                    Vector3 roadNode = World.GetNextPositionOnStreetWithHeading(
                        roadside, out roadHeading);
                    float roadSeparation = Distance2D(roadside, roadNode);
                    if (roadSeparation < 1.25f || roadSeparation > 14f
                        || Math.Abs(roadside.Z - roadNode.Z) > 3f)
                    {
                        lastFailure = "No distinct nearby roadside position was found away from the road node.";
                        continue;
                    }
                    if (area == null
                        || (enforcePlayerDistanceWindow
                            && (!area.IsEligibleFor(playerPosition)
                                || !IsWithinPlayerDistance(area, roadside, playerPosition))))
                    {
                        lastFailure = "The vehicle staging point falls outside the event area distance window.";
                        continue;
                    }
                    if (HasOverlappingVehicle(roadside, 2.35f))
                    {
                        lastFailure = "A vehicle currently occupies the roadside staging position.";
                        continue;
                    }
                    if (HasOverlappingPed(roadside, 2.0f))
                    {
                        lastFailure = "A pedestrian currently occupies the roadside staging position.";
                        continue;
                    }

                    stagingPosition = roadside;
                    heading = roadHeading;
                    reason = string.Empty;
                    return true;
                }

                reason = lastFailure;
                return false;
            }
            catch (Exception ex)
            {
                reason = "Runtime vehicle staging validation failed: " + ex.GetType().Name;
                return false;
            }
        }

        private static bool TryGetClosestDispatchVehicleRoadNode(
            Vector3 query,
            float maximumSnapDistance,
            float maximumElevationDelta,
            string stageName,
            out Vector3 roadNode,
            out float heading,
            out float snapDistance,
            out string reason,
            bool allowNativeRoadNodeWhenRoadFlagFails = false,
            int maximumNodeCandidates = 1)
        {
            roadNode = Vector3.Zero;
            heading = 0f;
            snapDistance = float.MaxValue;
            string stage = string.IsNullOrWhiteSpace(stageName)
                ? "VehicleRoadNode" : stageName.Trim();
            reason = "Stage=" + stage
                + "; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; Query=" + query
                + "; Result=false";
            if (query == Vector3.Zero || maximumSnapDistance <= 0f)
            {
                reason = "Stage=" + stage
                    + "; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; Query=" + query
                    + "; Result=InvalidQuery";
                return false;
            }

            int candidateCount = Math.Max(1, Math.Min(
                MaximumPlayerRadiusRoadNodeCandidates, maximumNodeCandidates));
            string lastFailure = reason;
            try
            {
                for (int candidateOrdinal = 1;
                    candidateOrdinal <= candidateCount;
                    candidateOrdinal++)
                {
                    using (OutputArgument nodeOutput = new OutputArgument())
                    using (OutputArgument headingOutput = new OutputArgument())
                    {
                        bool found;
                        bool enumerateAlternateNode = candidateCount > 1
                            && candidateOrdinal > 1;
                        if (enumerateAlternateNode)
                        {
                            using (OutputArgument laneCountOutput = new OutputArgument())
                            {
                                found = Function.Call<bool>(
                                    (Hash)GetNthClosestVehicleNodeWithHeadingNativeHash,
                                    query.X,
                                    query.Y,
                                    query.Z,
                                    candidateOrdinal,
                                    nodeOutput,
                                    headingOutput,
                                    laneCountOutput,
                                    0,
                                    3.0f,
                                    0f);
                            }
                        }
                        else
                        {
                            found = Function.Call<bool>(
                                Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                                query.X,
                                query.Y,
                                query.Z,
                                nodeOutput,
                                headingOutput,
                                1,
                                3.0f,
                                0);
                        }
                        if (!found)
                        {
                            lastFailure = "Stage=" + stage
                                + "; Native=" + (enumerateAlternateNode
                                    ? "GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING"
                                    : "GET_CLOSEST_VEHICLE_NODE_WITH_HEADING")
                                + "; Query=" + query
                                + "; CandidateOrdinal=" + candidateOrdinal
                                + "; CandidateCount=" + candidateCount
                                + "; Result=false";
                            break;
                        }

                        Vector3 candidate = nodeOutput.GetResult<Vector3>();
                        float candidateHeading = headingOutput.GetResult<float>();
                        float candidateDistance = candidate == Vector3.Zero
                            ? float.MaxValue : Distance2D(query, candidate);
                        float elevationDelta = candidate == Vector3.Zero
                            ? float.MaxValue : Math.Abs(candidate.Z - query.Z);
                        bool onRoad = candidate != Vector3.Zero
                            && Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                                candidate.X, candidate.Y, candidate.Z, 0);
                        if (candidate == Vector3.Zero
                            || float.IsNaN(candidateHeading) || float.IsInfinity(candidateHeading)
                            || candidateDistance > maximumSnapDistance
                            || elevationDelta > maximumElevationDelta
                            || (!onRoad && !allowNativeRoadNodeWhenRoadFlagFails))
                        {
                            lastFailure = "Stage=" + stage
                                + "; Native=" + (enumerateAlternateNode
                                    ? "GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING"
                                    : "GET_CLOSEST_VEHICLE_NODE_WITH_HEADING")
                                + "/IS_POINT_ON_ROAD"
                                + "; Query=" + query
                                + "; Candidate=" + candidate
                                + "; CandidateOrdinal=" + candidateOrdinal
                                + "; CandidateCount=" + candidateCount
                                + "; Heading=" + candidateHeading.ToString("0.0", CultureInfo.InvariantCulture)
                                + "; SnapDistance=" + candidateDistance.ToString("0.0", CultureInfo.InvariantCulture)
                                + "; MaximumSnapDistance=" + maximumSnapDistance.ToString("0.0", CultureInfo.InvariantCulture)
                                + "; ElevationDelta=" + elevationDelta.ToString("0.0", CultureInfo.InvariantCulture)
                                + "; MaximumElevationDelta=" + maximumElevationDelta.ToString("0.0", CultureInfo.InvariantCulture)
                                + "; OnRoad=" + onRoad
                                + "; NativeVehicleNodeAllowance=" + allowNativeRoadNodeWhenRoadFlagFails
                                + "; Result=Rejected";
                            continue;
                        }

                        roadNode = candidate;
                        heading = candidateHeading;
                        snapDistance = candidateDistance;
                        reason = string.Empty;
                        return true;
                    }
                }
                reason = lastFailure;
                return false;
            }
            catch (Exception ex)
            {
                reason = "Stage=" + stage
                    + "; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING/IS_POINT_ON_ROAD"
                    + "; Query=" + query + "; Result=Exception:" + ex.GetType().Name;
                return false;
            }
        }

        private bool TryResolveMovingRoadVehicleStaging(
            Vector3 catalogAnchor,
            LSPDDispatchLocationDefinition area,
            Vector3 playerPosition,
            bool enforcePlayerDistanceWindow,
            out Vector3 stagingPosition,
            out float heading,
            out Vector3 routePosition,
            out float routeHeading,
            out string reason,
            bool allowNativeRoadNodeWhenRoadFlagFails = false,
            float minimumPlayerDistance = MovingRoadMinimumPlayerDistance,
            float maximumPlayerDistance = MovingRoadMaximumPlayerDistance,
            float[] sceneOffsets = null)
        {
            stagingPosition = Vector3.Zero;
            heading = 0f;
            routePosition = Vector3.Zero;
            routeHeading = 0f;
            reason = "Stage=MovingRoadReference; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; Result=false";
            if (catalogAnchor == Vector3.Zero || area == null
                || !area.IsEligibleFor(playerPosition))
            {
                reason = "Stage=MovingRoadCatalogArea; Result=AreaNotEligibleForPlayer";
                return false;
            }

            try
            {
                int anchorInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    catalogAnchor.X, catalogAnchor.Y, catalogAnchor.Z);
                if (anchorInteriorId != 0)
                {
                    reason = "Stage=MovingRoadCatalogAnchor; Native=GET_INTERIOR_AT_COORDS; InteriorId="
                        + anchorInteriorId + "; Result=Rejected";
                    return false;
                }

                Vector3 roadReference;
                float referenceHeading;
                float referenceSnapDistance;
                string referenceFailure;
                if (!TryGetClosestDispatchVehicleRoadNode(
                    catalogAnchor,
                    MovingRoadReferenceSnapDistance,
                    MovingRoadElevationTolerance,
                    "MovingRoadReference",
                    out roadReference,
                    out referenceHeading,
                    out referenceSnapDistance,
                    out referenceFailure,
                    allowNativeRoadNodeWhenRoadFlagFails))
                {
                    reason = referenceFailure;
                    return false;
                }

                Vector3 validatedReference;
                string validatedReferenceFailure;
                if (!TryValidateMovingRoadPoint(
                    roadReference, "Reference", false,
                    out validatedReference, out validatedReferenceFailure,
                    allowNativeRoadNodeWhenRoadFlagFails))
                {
                    reason = validatedReferenceFailure;
                    return false;
                }

                float radians = referenceHeading * (float)Math.PI / 180f;
                Vector3 forward = new Vector3(
                    (float)Math.Sin(radians), (float)Math.Cos(radians), 0f);
                string lastFailure = "Stage=MovingRoadNode; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; Result=false";
                foreach (float offset in sceneOffsets ?? DispatchMovingRoadSceneOffsets)
                {
                    Vector3 query = validatedReference + (forward * offset);
                    Vector3 candidate;
                    float candidateHeading;
                    float querySnapDistance;
                    string candidateNodeFailure;
                    if (!TryGetClosestDispatchVehicleRoadNode(
                        query,
                        18f,
                        MovingRoadElevationTolerance,
                        "MovingRoadNode",
                        out candidate,
                        out candidateHeading,
                        out querySnapDistance,
                        out candidateNodeFailure,
                        allowNativeRoadNodeWhenRoadFlagFails,
                        allowNativeRoadNodeWhenRoadFlagFails
                            ? MaximumPlayerRadiusRoadNodeCandidates : 1))
                    {
                        lastFailure = candidateNodeFailure;
                        continue;
                    }

                    Vector3 validatedCandidate;
                    string candidateFailure;
                    if (!TryValidateMovingRoadPoint(
                        candidate, "VehicleNode", true,
                        out validatedCandidate, out candidateFailure,
                        allowNativeRoadNodeWhenRoadFlagFails))
                    {
                        lastFailure = candidateFailure;
                        continue;
                    }

                    float playerDistance = Distance2D(validatedCandidate, playerPosition);
                    if (playerDistance < minimumPlayerDistance
                        || playerDistance > maximumPlayerDistance)
                    {
                        lastFailure = "Stage=MovingRoadPlayerRange; PlayerDistance="
                            + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Result=Rejected";
                        continue;
                    }
                    if (enforcePlayerDistanceWindow
                        && (!area.IsEligibleFor(playerPosition)
                            || !IsWithinPlayerDistance(area, validatedCandidate, playerPosition)))
                    {
                        lastFailure = "Stage=MovingRoadCatalogRange; Area=" + area.Id
                            + "; PlayerDistance=" + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Result=Rejected";
                        continue;
                    }

                    float candidateRadians = candidateHeading * (float)Math.PI / 180f;
                    Vector3 candidateForward = new Vector3(
                        (float)Math.Sin(candidateRadians), (float)Math.Cos(candidateRadians), 0f);
                    Vector3 routeQuery = validatedCandidate
                        + (candidateForward * MovingRoadRouteProbeDistance);
                    Vector3 rawRoutePosition;
                    float nextHeading;
                    float routeQuerySnapDistance;
                    string routeNodeFailure;
                    if (!TryGetClosestDispatchVehicleRoadNode(
                        routeQuery,
                        18f,
                        MovingRoadElevationTolerance,
                        "MovingRoadRoute",
                        out rawRoutePosition,
                        out nextHeading,
                        out routeQuerySnapDistance,
                        out routeNodeFailure,
                        allowNativeRoadNodeWhenRoadFlagFails,
                        allowNativeRoadNodeWhenRoadFlagFails
                            ? MaximumPlayerRadiusRoadNodeCandidates : 1))
                    {
                        lastFailure = routeNodeFailure;
                        continue;
                    }
                    Vector3 validatedRoutePosition;
                    string routeFailure;
                    if (!TryValidateMovingRoadPoint(
                        rawRoutePosition, "RouteNode", false,
                        out validatedRoutePosition, out routeFailure,
                        allowNativeRoadNodeWhenRoadFlagFails))
                    {
                        lastFailure = routeFailure;
                        continue;
                    }

                    float routeDistance = Distance2D(validatedCandidate, validatedRoutePosition);
                    if (routeDistance < MovingRoadMinimumRouteDistance
                        || routeDistance > MovingRoadMaximumRouteDistance
                        || Math.Abs(validatedRoutePosition.Z - validatedCandidate.Z)
                            > MovingRoadElevationTolerance)
                    {
                        lastFailure = "Stage=MovingRoadRoute; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; "
                            + "RouteDistance=" + routeDistance.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; ElevationDelta=" + Math.Abs(validatedRoutePosition.Z - validatedCandidate.Z)
                                .ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Result=Rejected";
                        continue;
                    }

                    stagingPosition = validatedCandidate;
                    heading = candidateHeading;
                    routePosition = validatedRoutePosition;
                    routeHeading = nextHeading;
                    reason = string.Empty;
                    return true;
                }

                reason = lastFailure;
                return false;
            }
            catch (Exception ex)
            {
                reason = "Stage=MovingRoadProbe; Native=GET_INTERIOR_AT_COORDS/"
                    + "IS_POINT_ON_ROAD/GET_GROUND_HEIGHT_AND_NORMAL/"
                    + "GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; Result=Exception:"
                    + ex.GetType().Name;
                return false;
            }
        }

        private static bool TryValidateMovingRoadPoint(
            Vector3 candidate,
            string stageName,
            bool checkOccupancy,
            out Vector3 validatedPosition,
            out string reason,
            bool allowNativeRoadNodeWhenRoadFlagFails = false)
        {
            validatedPosition = candidate;
            reason = string.Empty;
            int interiorId;
            bool onRoad;
            try
            {
                interiorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS, candidate.X, candidate.Y, candidate.Z);
                if (interiorId != 0)
                {
                    reason = "Stage=MovingRoad" + stageName
                        + "; Native=GET_INTERIOR_AT_COORDS; Position=" + candidate
                        + "; InteriorId=" + interiorId
                        + "; Result=Rejected";
                    return false;
                }
                onRoad = Function.Call<bool>(
                    Hash.IS_POINT_ON_ROAD, candidate.X, candidate.Y, candidate.Z, 0);
                if (!onRoad && allowNativeRoadNodeWhenRoadFlagFails)
                {
                    Vector3 confirmedRoadNode;
                    float confirmedHeading;
                    float confirmationDistance;
                    string confirmationFailure;
                    bool nativeRoadNodeConfirmed = TryGetClosestDispatchVehicleRoadNode(
                        candidate,
                        2f,
                        MovingRoadElevationTolerance,
                        "MovingRoad" + stageName + "RoadNodeConfirmation",
                        out confirmedRoadNode,
                        out confirmedHeading,
                        out confirmationDistance,
                        out confirmationFailure,
                        true)
                        && confirmedRoadNode != Vector3.Zero
                        && !float.IsNaN(confirmedHeading)
                        && !float.IsInfinity(confirmedHeading)
                        && confirmationDistance <= 2f
                        && Math.Abs(confirmedRoadNode.Z - candidate.Z)
                            <= MovingRoadElevationTolerance;
                    if (!nativeRoadNodeConfirmed)
                    {
                        reason = "Stage=MovingRoad" + stageName
                            + "; Native=IS_POINT_ON_ROAD=false/GET_CLOSEST_VEHICLE_NODE_WITH_HEADING"
                            + "; Position=" + candidate
                            + "; RoadNodeConfirmation=" + confirmationFailure
                            + "; Result=Rejected";
                        return false;
                    }
                    onRoad = true;
                }
                if (!onRoad)
                {
                    reason = "Stage=MovingRoad" + stageName
                        + "; Native=IS_POINT_ON_ROAD; Position=" + candidate
                        + "; Result=false";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = "Stage=MovingRoad" + stageName
                    + "; Native=GET_INTERIOR_AT_COORDS/IS_POINT_ON_ROAD; Position=" + candidate
                    + "; Result=Exception:"
                    + ex.GetType().Name;
                return false;
            }

            float groundZ;
            Vector3 groundNormal;
            bool groundFound;
            try
            {
                groundFound = World.GetGroundHeightAndNormal(
                    candidate, out groundZ, out groundNormal);
            }
            catch (Exception ex)
            {
                reason = "Stage=MovingRoad" + stageName
                    + "; Native=GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; Position=" + candidate
                    + "; Result=Exception:"
                    + ex.GetType().Name;
                return false;
            }
            Vector3 confirmedRoadNodeForGroundFallback = Vector3.Zero;
            float confirmedRoadHeadingForGroundFallback = 0f;
            float confirmedRoadSnapDistanceForGroundFallback = float.MaxValue;
            string groundFallbackFailure = string.Empty;
            bool nativeRoadSurfaceConfirmed = !groundFound
                && allowNativeRoadNodeWhenRoadFlagFails
                && TryGetClosestDispatchVehicleRoadNode(
                    candidate,
                    2f,
                    MovingRoadElevationTolerance,
                    "MovingRoad" + stageName + "GroundNodeConfirmation",
                    out confirmedRoadNodeForGroundFallback,
                    out confirmedRoadHeadingForGroundFallback,
                    out confirmedRoadSnapDistanceForGroundFallback,
                    out groundFallbackFailure,
                    true)
                && confirmedRoadNodeForGroundFallback != Vector3.Zero
                && !float.IsNaN(confirmedRoadHeadingForGroundFallback)
                && !float.IsInfinity(confirmedRoadHeadingForGroundFallback)
                && confirmedRoadSnapDistanceForGroundFallback <= 2f
                && Math.Abs(confirmedRoadNodeForGroundFallback.Z - candidate.Z)
                    <= MovingRoadElevationTolerance;
            if (!groundFound && !nativeRoadSurfaceConfirmed)
            {
                try
                {
                    Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                        candidate.X, candidate.Y, candidate.Z);
                }
                catch { }
                reason = "Stage=MovingRoad" + stageName
                    + "; Native=GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; Position=" + candidate
                    + "; Result=false"
                    + "; CollisionRequested=true"
                    + "; NativeRoadNodeConfirmation="
                    + (string.IsNullOrWhiteSpace(groundFallbackFailure)
                        ? "Unavailable" : groundFallbackFailure);
                return false;
            }
            if (groundFound
                && (float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || float.IsNaN(groundNormal.Z) || float.IsInfinity(groundNormal.Z)
                    || groundNormal.Z < 0.65f
                    || Math.Abs(candidate.Z - groundZ) > 3f))
            {
                reason = "Stage=MovingRoad" + stageName
                    + "; Native=GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD; Position=" + candidate
                    + "; GroundZ="
                    + groundZ.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; NormalZ=" + groundNormal.Z.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Result=Rejected";
                return false;
            }

            candidate.Z = groundFound
                ? groundZ
                : confirmedRoadNodeForGroundFallback.Z;
            if (checkOccupancy)
            {
                bool occupiedVehicle = HasOverlappingVehicle(
                    candidate, MovingRoadVehicleOccupancyRadius);
                bool occupiedPed = HasOverlappingPed(
                    candidate, MovingRoadPedOccupancyRadius);
                if (occupiedVehicle || occupiedPed)
                {
                    reason = "Stage=MovingRoad" + stageName
                        + "; Native=GET_NEARBY_VEHICLES/GET_NEARBY_PEDS; Position=" + candidate
                        + "; Vehicle="
                        + occupiedVehicle + "; Ped=" + occupiedPed
                        + "; Result=Occupied";
                    return false;
                }
            }

            validatedPosition = candidate;
            return true;
        }

        private bool TryRevalidateVehicleScene(
            AmbientDispatchSceneResolution previous,
            Vector3 playerPosition,
            bool requiresVehicle,
            out AmbientDispatchSceneResolution refreshed,
            out string reason)
        {
            refreshed = null;
            reason = string.Empty;
            if (!requiresVehicle || previous == null
                || previous.Area == null
                || !previous.HasVehicleStagingPosition
                || previous.VehicleStagingPosition == Vector3.Zero)
            {
                reason = "Stage=VehicleSceneRevalidationIdentity; Result=MissingVehicleOrStagingPlan";
                return false;
            }

            bool runtimePlayerRadiusScene = string.Equals(
                previous.LocationCategory, "PlayerRadiusRoadNode",
                StringComparison.OrdinalIgnoreCase);
            if (!runtimePlayerRadiusScene)
            {
                LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location;
                if (_locationCatalog == null
                    || !_locationCatalog.TryGetLocation(previous.LocationId, out location)
                    || IsRejectedSurveyRecord(location)
                    || !IsCompatibleWithIncident(location, previous.IncidentType))
                {
                    reason = "Stage=VehicleSceneRevalidationIdentity; Location="
                        + previous.LocationId + "; Result=CatalogReferenceChanged";
                    return false;
                }
            }

            float playerDistance = Distance2D(
                previous.VehicleStagingPosition, playerPosition);
            Vector3 selectionAnchor = previous.AreaAnchorPosition == Vector3.Zero
                ? playerPosition : previous.AreaAnchorPosition;
            float selectionDistance = Distance2D(
                previous.VehicleStagingPosition, selectionAnchor);
            float minimumPlayerDistance;
            float maximumPlayerDistance;
            GetPlayerRadiusFallbackDistanceRange(
                out minimumPlayerDistance, out maximumPlayerDistance);
            if (runtimePlayerRadiusScene
                && (selectionDistance < minimumPlayerDistance
                    || selectionDistance > maximumPlayerDistance
                    || playerDistance > maximumPlayerDistance))
            {
                reason = "Stage=PlayerRadiusVehicleRevalidationRange; PlayerDistance="
                    + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; SelectionDistance=" + selectionDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; RequiredRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Result=Rejected";
                return false;
            }

            Vector3 validatedVehicle = previous.VehicleStagingPosition;
            Vector3 validatedRoute = previous.VehicleRoutePosition;
            if (string.Equals(previous.VehicleStagingMode, "moving_road",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!previous.HasVehicleRoute || previous.VehicleRoutePosition == Vector3.Zero)
                {
                    reason = "Stage=MovingRoadRevalidationRoute; Result=MissingRoutePlan";
                    return false;
                }
                string vehicleFailure;
                if (!TryValidateMovingRoadPoint(
                    previous.VehicleStagingPosition, "VehicleNode", false,
                    out validatedVehicle, out vehicleFailure,
                    runtimePlayerRadiusScene))
                {
                    reason = vehicleFailure;
                    return false;
                }
                string routeFailure;
                if (!TryValidateMovingRoadPoint(
                    previous.VehicleRoutePosition, "RouteNode", false,
                    out validatedRoute, out routeFailure,
                    runtimePlayerRadiusScene))
                {
                    reason = routeFailure;
                    return false;
                }

                float routeDistance = Distance2D(validatedVehicle, validatedRoute);
                if (routeDistance < MovingRoadMinimumRouteDistance
                    || routeDistance > MovingRoadMaximumRouteDistance
                    || Math.Abs(validatedRoute.Z - validatedVehicle.Z)
                        > MovingRoadElevationTolerance)
                {
                    reason = "Stage=MovingRoadRevalidationRoute; RouteDistance="
                        + routeDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; ElevationDelta=" + Math.Abs(validatedRoute.Z - validatedVehicle.Z)
                            .ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Result=Rejected";
                    return false;
                }
            }
            else if (runtimePlayerRadiusScene)
            {
                int vehicleInterior = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                    validatedVehicle.X, validatedVehicle.Y, validatedVehicle.Z);
                int actorInterior = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                    previous.SceneCenter.X, previous.SceneCenter.Y, previous.SceneCenter.Z);
                bool vehicleOnRoad = Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                    validatedVehicle.X, validatedVehicle.Y, validatedVehicle.Z, 0);
                float vehicleGroundZ;
                Vector3 vehicleGroundNormal;
                bool vehicleGroundFound = World.GetGroundHeightAndNormal(
                    validatedVehicle, out vehicleGroundZ, out vehicleGroundNormal);
                float roadHeading;
                Vector3 roadNode;
                float roadSeparation;
                string roadNodeFailure;
                bool roadNodeFound = TryGetClosestDispatchVehicleRoadNode(
                    validatedVehicle,
                    14f,
                    MovingRoadElevationTolerance,
                    "PlayerRadiusParkedRevalidationRoadNode",
                    out roadNode,
                    out roadHeading,
                    out roadSeparation,
                    out roadNodeFailure,
                    runtimePlayerRadiusScene);
                bool parkedRoadClearanceConfirmed = roadNodeFound
                    && roadNode != Vector3.Zero
                    && !float.IsNaN(roadHeading)
                    && !float.IsInfinity(roadHeading)
                    && roadSeparation >= 1.25f
                    && roadSeparation <= 14f
                    && Distance2D(previous.SceneCenter, validatedVehicle) <= 12f;
                if (runtimePlayerRadiusScene
                    && vehicleInterior == 0
                    && actorInterior == 0
                    && !vehicleOnRoad
                    && !vehicleGroundFound
                    && parkedRoadClearanceConfirmed)
                {
                    try
                    {
                        Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                            validatedVehicle.X, validatedVehicle.Y, validatedVehicle.Z);
                    }
                    catch (Exception ex)
                    {
                        reason = "Stage=PlayerRadiusParkedRevalidationGround; Native=GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD/"
                            + "REQUEST_COLLISION_AT_COORD; Position=" + validatedVehicle
                            + "; VehicleInterior=" + vehicleInterior
                            + "; ActorInterior=" + actorInterior
                            + "; VehicleOnRoad=" + vehicleOnRoad
                            + "; GroundFound=false; RoadSeparation="
                            + roadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; CollisionRequest=Exception:" + ex.GetType().Name
                            + "; Result=Rejected";
                        return false;
                    }

                    reason = "PENDING:Stage=PlayerRadiusParkedRevalidationGround; Native=GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD/"
                        + "REQUEST_COLLISION_AT_COORD; Position=" + validatedVehicle
                        + "; VehicleInterior=" + vehicleInterior
                        + "; ActorInterior=" + actorInterior
                        + "; VehicleOnRoad=" + vehicleOnRoad
                        + "; GroundFound=false; RoadSeparation="
                        + roadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; CollisionRequest=REQUEST_COLLISION_AT_COORD"
                        + "; Result=CollisionPending";
                    return false;
                }
                if (vehicleInterior != 0 || actorInterior != 0 || vehicleOnRoad
                    || !vehicleGroundFound || vehicleGroundNormal.Z < 0.65f
                    || Math.Abs(validatedVehicle.Z - vehicleGroundZ) > 3f
                    || !roadNodeFound || roadNode == Vector3.Zero || float.IsNaN(roadHeading)
                    || float.IsInfinity(roadHeading)
                    || roadSeparation < 1.25f || roadSeparation > 14f
                    || Distance2D(previous.SceneCenter, validatedVehicle) > 12f)
                {
                    reason = "Stage=PlayerRadiusParkedRevalidation; Native=GET_INTERIOR_AT_COORDS/"
                        + "IS_POINT_ON_ROAD/GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD/"
                        + "GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; VehicleInterior=" + vehicleInterior
                        + "; ActorInterior=" + actorInterior + "; VehicleOnRoad=" + vehicleOnRoad
                        + "; GroundFound=" + vehicleGroundFound + "; RoadSeparation="
                        + roadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; RoadNodeFailure=" + (roadNodeFound ? "None" : roadNodeFailure)
                        + "; Result=Rejected";
                    return false;
                }
                validatedVehicle.Z = vehicleGroundZ;
            }

            previous.VehicleStagingPosition = validatedVehicle;
            previous.VehicleRoutePosition = validatedRoute;
            refreshed = previous;
            reason = string.Empty;
            Log("AMBIENT_WORLD_VEHICLE_SCENE_REVALIDATED",
                "Location=" + (runtimePlayerRadiusScene
                    ? "NoSurveyedLocation" : previous.LocationId)
                + "; Mode=" + previous.VehicleStagingMode
                + "; VehicleNode=" + validatedVehicle
                + "; RouteNode=" + (previous.HasVehicleRoute
                    ? validatedRoute.ToString() : "NotRequired")
                + "; PlayerDistance=" + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + "; Native=IS_POINT_ON_ROAD/GET_GROUND_Z_AND_NORMAL_FOR_3D_COORD"
                + "; OccupancyDeferredToVehicleCreation=true");
            return true;
        }

        /// <summary>
        /// Resolves one additional, physically valid roadside position for a
        /// bounded Dispatch multi-vehicle test scene. The caller owns the
        /// returned vehicle; this method only validates a separate staging
        /// point and never scans beyond the nearby ground/occupancy queries.
        /// </summary>
        internal bool TryResolveAdditionalDispatchVehicleStaging(
            AmbientDispatchSceneResolution scene,
            out Vector3 stagingPosition,
            out float heading,
            out string reason)
        {
            stagingPosition = Vector3.Zero;
            heading = 0f;
            reason = string.Empty;
            if (scene == null || scene.Area == null || !scene.HasVehicleStagingPosition)
            {
                reason = "The first Dispatch roadside staging point is unavailable.";
                return false;
            }

            if (string.Equals(scene.VehicleStagingMode, "moving_road",
                    StringComparison.OrdinalIgnoreCase))
                return TryResolveAdditionalMovingRoadVehicleStaging(
                    scene,
                    out stagingPosition,
                    out heading,
                    out reason,
                    string.Equals(scene.LocationCategory, "PlayerRadiusRoadNode",
                        StringComparison.OrdinalIgnoreCase));

            float radians = scene.VehicleHeading * (float)Math.PI / 180f;
            Vector3 forward = new Vector3((float)Math.Sin(radians),
                (float)Math.Cos(radians), 0f);
            Vector3 lateral = new Vector3(forward.Y, -forward.X, 0f);
            Vector3[] offsets =
            {
                forward * 7f,
                forward * -7f,
                lateral * 6f,
                lateral * -6f
            };
            for (int index = 0; index < offsets.Length; index++)
            {
                Vector3 candidate = scene.VehicleStagingPosition + offsets[index];
                Vector3 resolved;
                float resolvedHeading;
                string candidateReason;
                if (!TryResolveVehicleStaging(
                    candidate,
                    scene.Area,
                    scene.SceneCenter,
                    false,
                    out resolved,
                    out resolvedHeading,
                    out candidateReason))
                    continue;
                if (Distance2D(resolved, scene.VehicleStagingPosition) < 5f)
                    continue;
                stagingPosition = resolved;
                heading = resolvedHeading;
                reason = string.Empty;
                return true;
            }

            reason = "No separate roadside point passed ground, road-offset, pedestrian, vehicle, and interior checks.";
            return false;
        }

        private bool TryResolveAdditionalMovingRoadVehicleStaging(
            AmbientDispatchSceneResolution scene,
            out Vector3 stagingPosition,
            out float heading,
            out string reason,
            bool allowNativeRoadNodeWhenRoadFlagFails)
        {
            stagingPosition = Vector3.Zero;
            heading = 0f;
            reason = "Stage=AdditionalMovingRoadVehicle; Native=GET_CLOSEST_VEHICLE_NODE_WITH_HEADING; Result=false";
            float minimumPlayerDistance = MovingRoadMinimumRouteDistance;
            float maximumPlayerDistance = MovingRoadMaximumPlayerDistance;
            if (allowNativeRoadNodeWhenRoadFlagFails)
                GetPlayerRadiusFallbackDistanceRange(
                    out minimumPlayerDistance, out maximumPlayerDistance);
            float radians = scene.VehicleHeading * (float)Math.PI / 180f;
            Vector3 forward = new Vector3(
                (float)Math.Sin(radians), (float)Math.Cos(radians), 0f);
            float[] offsets = { 30f, -30f, 45f, -45f };
            string lastFailure = reason;
            for (int index = 0; index < offsets.Length; index++)
            {
                Vector3 query = scene.VehicleStagingPosition + (forward * offsets[index]);
                Vector3 rawCandidate;
                float candidateHeading;
                float snapDistance;
                string candidateNodeFailure;
                if (!TryGetClosestDispatchVehicleRoadNode(
                    query,
                    18f,
                    MovingRoadElevationTolerance,
                    "AdditionalMovingRoadVehicleNode",
                    out rawCandidate,
                    out candidateHeading,
                    out snapDistance,
                    out candidateNodeFailure,
                    allowNativeRoadNodeWhenRoadFlagFails,
                    allowNativeRoadNodeWhenRoadFlagFails
                        ? MaximumPlayerRadiusRoadNodeCandidates : 1))
                {
                    lastFailure = candidateNodeFailure;
                    continue;
                }

                Vector3 candidate;
                string candidateFailure;
                if (!TryValidateMovingRoadPoint(rawCandidate, "AdditionalVehicleNode",
                    true, out candidate, out candidateFailure,
                    allowNativeRoadNodeWhenRoadFlagFails))
                {
                    lastFailure = candidateFailure;
                    continue;
                }
                float vehicleSeparation = Distance2D(
                    scene.VehicleStagingPosition, candidate);
                float playerDistance = Distance2D(scene.Area.Position, candidate);
                if (vehicleSeparation < 18f || vehicleSeparation > 60f
                    || playerDistance < minimumPlayerDistance
                    || playerDistance > maximumPlayerDistance
                    || Math.Abs(candidate.Z - scene.VehicleStagingPosition.Z)
                        > MovingRoadElevationTolerance)
                {
                    lastFailure = "Stage=AdditionalMovingRoadVehicleSpacing; VehicleSeparation="
                        + vehicleSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; PlayerDistance=" + playerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; RequiredPlayerRange=" + minimumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "-" + maximumPlayerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; ElevationDelta=" + Math.Abs(candidate.Z - scene.VehicleStagingPosition.Z)
                            .ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Result=Rejected";
                    continue;
                }

                float routeRadians = candidateHeading * (float)Math.PI / 180f;
                Vector3 routeForward = new Vector3(
                    (float)Math.Sin(routeRadians), (float)Math.Cos(routeRadians), 0f);
                Vector3 routeQuery = candidate + (routeForward * MovingRoadRouteProbeDistance);
                Vector3 rawRoute;
                float routeHeading;
                float routeSnapDistance;
                string routeNodeFailure;
                if (!TryGetClosestDispatchVehicleRoadNode(
                    routeQuery,
                    18f,
                    MovingRoadElevationTolerance,
                    "AdditionalMovingRoadRoute",
                    out rawRoute,
                    out routeHeading,
                    out routeSnapDistance,
                    out routeNodeFailure,
                    allowNativeRoadNodeWhenRoadFlagFails,
                    allowNativeRoadNodeWhenRoadFlagFails
                        ? MaximumPlayerRadiusRoadNodeCandidates : 1))
                {
                    lastFailure = routeNodeFailure;
                    continue;
                }
                Vector3 validatedRoute;
                string routeFailure;
                if (!TryValidateMovingRoadPoint(rawRoute, "AdditionalVehicleRouteNode",
                    false, out validatedRoute, out routeFailure,
                    allowNativeRoadNodeWhenRoadFlagFails))
                {
                    lastFailure = routeFailure;
                    continue;
                }
                float routeDistance = Distance2D(candidate, validatedRoute);
                if (routeDistance < MovingRoadMinimumRouteDistance
                    || routeDistance > MovingRoadMaximumRouteDistance
                    || Math.Abs(validatedRoute.Z - candidate.Z) > MovingRoadElevationTolerance)
                {
                    lastFailure = "Stage=AdditionalMovingRoadRoute; RouteDistance="
                        + routeDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + "; ElevationDelta=" + Math.Abs(validatedRoute.Z - candidate.Z)
                            .ToString("0.0", CultureInfo.InvariantCulture)
                        + "; Result=Rejected";
                    continue;
                }

                stagingPosition = candidate;
                heading = candidateHeading;
                reason = string.Empty;
                return true;
            }

            reason = lastFailure;
            return false;
        }

        private static bool HasOverlappingPed(
            Vector3 position,
            float minimumSeparation,
            ISet<int> excludedPedHandles = null)
        {
            foreach (Ped ped in World.GetNearbyPeds(position, minimumSeparation))
            {
                if (ped == null || !ped.Exists())
                    continue;
                if (excludedPedHandles != null
                    && excludedPedHandles.Contains(ped.Handle))
                    continue;
                if (ped.Position.DistanceTo(position) < minimumSeparation)
                    return true;
            }
            return false;
        }

        private static bool HasOverlappingVehicle(Vector3 position, float minimumSeparation)
        {
            foreach (Vehicle vehicle in World.GetNearbyVehicles(position, minimumSeparation))
            {
                if (vehicle == null || !vehicle.Exists())
                    continue;
                if (vehicle.Position.DistanceTo(position) < minimumSeparation)
                    return true;
            }
            return false;
        }

        private static bool IsRejectedSurveyRecord(
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location)
        {
            if (location == null)
                return true;
            return IsRejectedSurveyStatus(location.ReviewStatus)
                || IsRejectedSurveyStatus(location.PotentialMisclick);
        }

        private static void RecordDispatchLocationCandidateFailure(
            string locationId,
            string pointId,
            string failure,
            IDictionary<string, int> failureStageCounts,
            IList<string> failureExamples)
        {
            if (failureStageCounts == null || failureExamples == null)
                return;

            string stage = GetDispatchLocationCandidateFailureStage(failure);
            stage = LimitDispatchLocationFailureToken(stage, 48);
            if (!failureStageCounts.ContainsKey(stage)
                && failureStageCounts.Count >= MaximumDispatchLocationCandidateFailureStages - 1)
                stage = "Other";
            int count;
            failureStageCounts.TryGetValue(stage, out count);
            failureStageCounts[stage] = count + 1;

            if (failureExamples.Count >= MaximumDispatchLocationCandidateFailureExamples)
                return;

            failureExamples.Add(LimitDispatchLocationFailureToken(
                locationId, MaximumDispatchLocationFailureTokenLength)
                + "/" + LimitDispatchLocationFailureToken(
                    pointId, MaximumDispatchLocationFailureTokenLength)
                + "=" + stage);
        }

        private static string LimitDispatchLocationFailureToken(string value, int maximumLength)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
            if (maximumLength <= 3 || token.Length <= maximumLength)
                return token;
            return token.Substring(0, maximumLength - 3) + "...";
        }

        private static string GetDispatchLocationCandidateFailureStage(string failure)
        {
            if (string.IsNullOrWhiteSpace(failure))
                return "Unclassified";

            string detail = failure;
            int firstFailureIndex = detail.IndexOf(
                "FirstFailure=", StringComparison.OrdinalIgnoreCase);
            if (firstFailureIndex >= 0)
                detail = detail.Substring(firstFailureIndex + "FirstFailure=".Length);

            int stageIndex = detail.IndexOf("Stage=", StringComparison.OrdinalIgnoreCase);
            if (stageIndex >= 0)
            {
                string stage = detail.Substring(stageIndex + "Stage=".Length);
                int separator = stage.IndexOfAny(new[] { ';', '|', ' ' });
                if (separator >= 0)
                    stage = stage.Substring(0, separator);
                return string.IsNullOrWhiteSpace(stage) ? "Unclassified" : stage;
            }

            return detail.IndexOf("CollisionPending", StringComparison.OrdinalIgnoreCase) >= 0
                ? "CollisionPending" : "Unclassified";
        }

        private static string BuildDispatchLocationCandidateFailureSummary(
            int candidateAttempts,
            IDictionary<string, int> failureStageCounts,
            IEnumerable<string> failureExamples)
        {
            string stageSummary = failureStageCounts == null
                ? string.Empty
                : string.Join(",", failureStageCounts
                    .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => entry.Key + ":" + entry.Value));
            string examples = failureExamples == null
                ? string.Empty
                : string.Join("|", failureExamples.Take(
                    MaximumDispatchLocationCandidateFailureExamples));
            return "CandidatePlacementAttempts=" + candidateAttempts
                + "; CandidateFailureStages="
                    + (stageSummary.Length == 0 ? "None" : stageSummary)
                + "; CandidateFailureExamples="
                    + (examples.Length == 0 ? "None" : examples);
        }

        private void LogDispatchCandidateFailureSummaryIfAny(
            string incidentType,
            string result,
            string selectedLocationId,
            string selectedPointId,
            int candidateAttempts,
            IDictionary<string, int> failureStageCounts,
            IEnumerable<string> failureExamples)
        {
            if (failureStageCounts == null || failureStageCounts.Count == 0)
                return;

            Log("AMBIENT_WORLD_DISPATCH_CANDIDATE_SEARCH_SUMMARY",
                "IncidentType=" + LimitDispatchLocationFailureToken(incidentType, 80)
                + "; Result=" + result
                + "; SelectedLocation=" + LimitDispatchLocationFailureToken(
                    selectedLocationId, MaximumDispatchLocationFailureTokenLength)
                + "; SelectedPoint=" + LimitDispatchLocationFailureToken(
                    selectedPointId, MaximumDispatchLocationFailureTokenLength)
                + "; " + BuildDispatchLocationCandidateFailureSummary(
                    candidateAttempts, failureStageCounts, failureExamples));
        }

        private static bool RequiresNamedLocation(
            string incidentType,
            string requiredPhysicalType = null)
        {
            return !string.IsNullOrWhiteSpace(requiredPhysicalType)
                || string.Equals(incidentType, "bank_robbery", StringComparison.OrdinalIgnoreCase)
                || string.Equals(incidentType, "store_robbery", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesRequiredPhysicalType(
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location,
            string requiredPhysicalType)
        {
            return string.IsNullOrWhiteSpace(requiredPhysicalType)
                || (location != null && string.Equals(
                    location.PhysicalType,
                    requiredPhysicalType,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCompatibleWithIncident(
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location,
            string incidentType)
        {
            if (!RequiresNamedLocation(incidentType))
                return true;

            string[] evidence =
            {
                location.PhysicalType,
                location.Category,
                location.Subcategory
            };
            if (string.Equals(incidentType, "bank_robbery", StringComparison.OrdinalIgnoreCase))
            {
                return evidence.Any(value =>
                    string.Equals(value, "Bank", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "BankBranch", StringComparison.OrdinalIgnoreCase));
            }

            return evidence.Any(value =>
                string.Equals(value, "Store", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Storefront", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "ConvenienceStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "ClothingStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "GunStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "LiquorStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "PawnShop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "HardwareStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "SportingGoodsStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Pharmacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Retail", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "GasStation", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCompatibleWithMappedInteriorIncident(
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location,
            string incidentType,
            string requiredPhysicalType)
        {
            if (IsCompatibleWithIncident(location, incidentType))
                return true;
            if (location != null
                && string.Equals(location.PhysicalType, "TattooShop",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(requiredPhysicalType, "TattooShop",
                    StringComparison.OrdinalIgnoreCase))
                    return false;

                // Tattoo rooms are deliberately limited to their authored
                // mapped indoor robbery, assault, snatch, and murder branches.
                return string.Equals(incidentType, "store_robbery",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(incidentType, "violent_disturbance",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(incidentType, "kidnapping",
                        StringComparison.OrdinalIgnoreCase);
            }
            if (location == null
                || !string.Equals(location.PhysicalType, "BarberShop",
                    StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(requiredPhysicalType)
                    && !string.Equals(requiredPhysicalType, "BarberShop",
                        StringComparison.OrdinalIgnoreCase)))
                return false;

            // The official catalog confirms these sites as BarberShop interiors,
            // not generic exterior retail. Admit them only through an authored
            // mapped-interior event; exterior store selection keeps the narrower
            // commercial type list above.
            return string.Equals(incidentType, "store_robbery",
                    StringComparison.OrdinalIgnoreCase)
                || (string.Equals(requiredPhysicalType, "BarberShop",
                        StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(incidentType, "violent_disturbance",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(incidentType, "kidnapping",
                            StringComparison.OrdinalIgnoreCase)));
        }

        private static bool MatchesMappedInteriorDispatchLocationContextMetadata(
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location,
            string context)
        {
            if (MatchesDispatchLocationContextMetadata(location, context))
                return true;
            return location != null
                && string.Equals(context, "commercial", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(location.PhysicalType, "BarberShop",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(location.PhysicalType, "TattooShop",
                        StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesDispatchLocationContext(
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location,
            string context,
            Vector3 candidatePosition)
        {
            if (!MatchesDispatchLocationContextMetadata(location, context))
                return false;

            string normalizedContext = (context ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedContext != "road_foot"
                && normalizedContext != "road_vehicle"
                || IsNearValidatedDispatchRoad(candidatePosition);
        }

        private static bool MatchesDispatchLocationContextMetadata(
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location,
            string context)
        {
            if (location == null || string.IsNullOrWhiteSpace(context)
                || string.Equals(context, "any", StringComparison.OrdinalIgnoreCase))
                return location != null;

            string[] types =
            {
                location.PhysicalType,
                location.Category,
                location.Subcategory
            };
            switch (context.Trim().ToLowerInvariant())
            {
                case "financial":
                    return types.Any(value =>
                        string.Equals(value, "Bank", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "BankBranch", StringComparison.OrdinalIgnoreCase));

                case "commercial":
                    return types.Any(IsCommercialDispatchLocationType);

                case "residential":
                    return types.Any(value =>
                        string.Equals(value, "ForSaleRental", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "ApartmentBuilding", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "Residential", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "House", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "Apartment", StringComparison.OrdinalIgnoreCase));

                case "alley":
                    return new[]
                    {
                        location.Name, location.Street, location.Environment,
                        location.PhysicalType, location.Category, location.Subcategory
                    }.Any(value => !string.IsNullOrWhiteSpace(value)
                        && value.IndexOf("alley", StringComparison.OrdinalIgnoreCase) >= 0);

                case "road_foot":
                case "road_vehicle":
                    // Road proximity is checked only after candidates are
                    // bounded to the Player's local catalog window.
                    return true;

                default:
                    return false;
            }
        }

        private static bool IsExteriorEntranceOrThresholdReference(
            LSImmersiveLocationCatalog.LSImmersiveLocationPoint point)
        {
            if (point == null)
                return true;

            string positionContext = point.PositionContext ?? string.Empty;
            return positionContext.IndexOf(
                    "EntranceReference", StringComparison.OrdinalIgnoreCase) >= 0
                || positionContext.IndexOf(
                    "ThresholdReference", StringComparison.OrdinalIgnoreCase) >= 0
                || positionContext.IndexOf(
                    "DoorReference", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static float GetDispatchExteriorRoadHeightTolerance(
            LSPDDispatchLocationDefinition area)
        {
            if (area == null || area.Contexts == null)
                return DispatchExteriorStreetHeightTolerance;

            foreach (string context in area.Contexts)
            {
                if (IsHeightSpecificDispatchContext(context))
                    return 0f;
            }

            foreach (string context in area.Contexts)
            {
                if (string.Equals(context, "rural", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(context, "hills", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(context, "canyon", StringComparison.OrdinalIgnoreCase))
                    return DispatchRuralStreetHeightTolerance;
            }

            return DispatchExteriorStreetHeightTolerance;
        }

        private static bool IsHeightSpecificDispatchContext(string context)
        {
            return !string.IsNullOrWhiteSpace(context)
                && (context.IndexOf("underbridge", StringComparison.OrdinalIgnoreCase) >= 0
                    || context.IndexOf("under_bridge", StringComparison.OrdinalIgnoreCase) >= 0
                    || context.IndexOf("under bridge", StringComparison.OrdinalIgnoreCase) >= 0
                    || context.IndexOf("underground", StringComparison.OrdinalIgnoreCase) >= 0
                    || context.IndexOf("under_ground", StringComparison.OrdinalIgnoreCase) >= 0
                    || context.IndexOf("under ground", StringComparison.OrdinalIgnoreCase) >= 0
                    || context.IndexOf("tunnel", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(context, "bridge", StringComparison.OrdinalIgnoreCase)
                    || context.IndexOf("overpass", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasCatalogedMappedInteriorReference(
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair)
        {
            if (pair == null || pair.OutsideLocation == null || pair.InsideLocation == null
                || pair.OutsidePoint == null || pair.InsidePoint == null
                || IsRejectedSurveyRecord(pair.OutsideLocation)
                || IsRejectedSurveyRecord(pair.InsideLocation))
                return false;

            // Catalog metadata establishes a bounded route candidate only.
            // Live interior readiness, collision, floor, physical door, and
            // sustained crossing checks still decide whether it can be used.
            return HasVerifiedMappedInteriorReference(pair)
                || HasCapturedInteriorAccessCandidate(pair);
        }

        private static bool HasVerifiedMappedInteriorReference(
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair)
        {
            if (pair == null || pair.OutsideLocation == null || pair.InsideLocation == null
                || pair.OutsidePoint == null || pair.InsidePoint == null
                || !pair.OutsideLocation.ExteriorVerified
                || !pair.InsideLocation.InteriorVerified
                || !string.Equals(pair.OutsidePoint.Role,
                    "ObservedCoordinateCandidate", StringComparison.OrdinalIgnoreCase)
                || !IsMappedInteriorPointRole(pair.InsidePoint.Role)
                || !HasCompatibleMappedPointSource(pair.OutsidePoint, pair.OutsideLocation)
                || !HasCompatibleMappedPointSource(pair.InsidePoint, pair.InsideLocation))
                return false;

            int outsideInteriorId;
            int insideInteriorId;
            int mappedInteriorId;
            float distance = pair.DistanceMeters;
            return int.TryParse(pair.OutsidePoint.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out outsideInteriorId)
                && outsideInteriorId == 0
                && int.TryParse(pair.InsidePoint.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out insideInteriorId)
                && insideInteriorId != 0
                && int.TryParse(pair.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out mappedInteriorId)
                && mappedInteriorId == insideInteriorId
                && (string.Equals(pair.OutsideLocation.Id, pair.InsideLocation.Id,
                        StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(pair.OutsideLocation.Street,
                            pair.InsideLocation.Street, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(pair.OutsideLocation.Zone,
                            pair.InsideLocation.Zone, StringComparison.OrdinalIgnoreCase)))
                && !float.IsNaN(distance) && !float.IsInfinity(distance)
                && distance >= 0.05f && distance <= 8f;
        }

        private static bool HasCapturedInteriorAccessCandidate(
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair)
        {
            if (pair == null || pair.OutsideLocation == null || pair.InsideLocation == null
                || pair.OutsidePoint == null || pair.InsidePoint == null
                || IsRejectedSurveyRecord(pair.OutsideLocation)
                || IsRejectedSurveyRecord(pair.InsideLocation))
                return false;

            string mappingBasis = pair.MappingBasis ?? string.Empty;
            bool catalogMappingCandidate =
                string.Equals(mappingBasis, "NearbyDifferentInteriorContext",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(mappingBasis, "BuildingCandidateEvidenceSameCapture",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(mappingBasis, "NearbyBuildingCandidateSameStreet",
                    StringComparison.OrdinalIgnoreCase);
            if (!catalogMappingCandidate)
                return false;

            string outsideRole = pair.OutsidePoint.Role ?? string.Empty;
            bool capturedOutside =
                string.Equals(outsideRole, "ObservedCoordinateCandidate",
                     StringComparison.OrdinalIgnoreCase)
                || (string.Equals(outsideRole, "BuildingCandidatePosition",
                     StringComparison.OrdinalIgnoreCase));

            string insideRole = pair.InsidePoint.Role ?? string.Empty;
            int outsideInteriorId;
            int insideInteriorId;
            int pairInteriorId;
            float distance = pair.DistanceMeters;
            bool capturedInside = IsMappedInteriorPointRole(insideRole)
                && int.TryParse(pair.InsidePoint.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out insideInteriorId)
                && insideInteriorId != 0
                && int.TryParse(pair.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out pairInteriorId)
                && pairInteriorId == insideInteriorId;

            bool sameLocation = string.Equals(pair.OutsideLocation.Id,
                pair.InsideLocation.Id, StringComparison.OrdinalIgnoreCase);
            bool sameStreet = !string.IsNullOrWhiteSpace(pair.OutsideLocation.Street)
                && !string.IsNullOrWhiteSpace(pair.OutsideLocation.Zone)
                && string.Equals(pair.OutsideLocation.Street,
                    pair.InsideLocation.Street, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.OutsideLocation.Zone,
                    pair.InsideLocation.Zone, StringComparison.OrdinalIgnoreCase);

            return capturedOutside
                && capturedInside
                && int.TryParse(pair.OutsidePoint.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out outsideInteriorId)
                && outsideInteriorId == 0
                && HasCompatibleMappedPointSource(
                    pair.OutsidePoint, pair.OutsideLocation)
                && HasCompatibleMappedPointSource(
                    pair.InsidePoint, pair.InsideLocation)
                && (sameLocation || sameStreet)
                && !float.IsNaN(distance) && !float.IsInfinity(distance)
                && distance >= 0.05f && distance <= 8f;
        }

        private static bool IsMappedInteriorPointRole(string role)
        {
            return string.Equals(role, "InteriorDoorCandidate",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "InteriorEgressCandidate",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "InteriorPointCandidate",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "LinkedInteriorCoordinateCandidate",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasCompatibleMappedPointSource(
            LSImmersiveLocationCatalog.LSImmersiveLocationPoint point,
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location)
        {
            return point != null && location != null
                && (string.IsNullOrWhiteSpace(point.SourceRef)
                    || string.IsNullOrWhiteSpace(location.SourceRef)
                    || string.Equals(point.SourceRef, location.SourceRef,
                        StringComparison.OrdinalIgnoreCase));
        }

        private bool IsDispatchInteriorPairWithinPlayerRange(
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair,
            Vector3 playerPosition,
            int playerInteriorId,
            out int mappedLiveInteriorId,
            out string nativeResult)
        {
            mappedLiveInteriorId = 0;
            nativeResult = "GET_INTERIOR_AT_COORDS:NotRequired";
            if (pair == null || pair.OutsidePoint == null || pair.InsidePoint == null
                || !HasCatalogedMappedInteriorReference(pair))
                return false;

            if (playerInteriorId != 0)
                return TryGetMappedInteriorPairLiveId(
                    pair, playerInteriorId, playerPosition,
                    out mappedLiveInteriorId, out nativeResult);

            float distance = Math.Min(
                Distance2D(pair.OutsidePoint.Position, playerPosition),
                Distance2D(pair.InsidePoint.Position, playerPosition));
            return distance
                <= LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance;
        }

        private bool TryFindInsideOnlyInteriorPoint(
            string incidentType,
            string requiredInteriorLocationId,
            string requiredPhysicalType,
            string requiredLocationContext,
            Vector3 playerPosition,
            int playerInteriorId,
            out DispatchInteriorPointReference selected,
            out string reason,
            ISet<string> excludedPointKeys = null,
            string preferredInteriorLocationId = null,
            ISet<string> excludedLocationIds = null)
        {
            selected = null;
            int catalogPointCount = 0;
            int compatiblePointCount = 0;
            int probedPointCount = 0;
            int firstMappedLiveInteriorId = int.MinValue;
            string firstMappedNativeResult = string.Empty;
            string pendingIdentityFailure = string.Empty;
            if (_locationCatalog == null || playerInteriorId == 0)
            {
                reason = "Stage=InteriorIdentity; FirstRejectedGate="
                    + (_locationCatalog == null ? "LocationCatalogUnavailable" : "PlayerLiveInteriorId")
                    + "; PlayerLiveInteriorId=" + playerInteriorId;
                return false;
            }

            IEnumerable<DispatchInteriorPointReference> candidates =
                GetDispatchInteriorPointReferences()
                    .Where(reference => reference != null
                        && reference.Location != null
                        && reference.Point != null
                        && (!IsRejectedSurveyRecord(reference.Location))
                        && (excludedLocationIds == null
                            || !excludedLocationIds.Contains(reference.Location.Id))
                        && (string.IsNullOrWhiteSpace(requiredInteriorLocationId)
                            || string.Equals(reference.Location.Id,
                                requiredInteriorLocationId,
                                StringComparison.OrdinalIgnoreCase))
                        && MatchesRequiredPhysicalType(
                            reference.Location, requiredPhysicalType)
                        && IsCompatibleWithIncident(
                            reference.Location, incidentType)
                        && MatchesDispatchLocationContextMetadata(
                            reference.Location, requiredLocationContext)
                        && HasObservedNonZeroInterior(reference.Point.InteriorId)
                        && (excludedPointKeys == null
                            || !excludedPointKeys.Contains(
                                GetInsideOnlyInteriorPointKey(reference)))
                        && Distance3D(reference.Point.Position, playerPosition)
                            <= MaximumInteriorPlayerFallbackDistance);
            catalogPointCount = GetDispatchInteriorPointReferences().Count;
            List<DispatchInteriorPointReference> bounded = candidates
                .GroupBy(reference => reference.Location.Id,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(reference => Distance3D(
                    reference.Point.Position, playerPosition)).First())
                .OrderBy(reference => string.Equals(
                    reference.Location.Id,
                    preferredInteriorLocationId,
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(reference => Distance3D(
                    reference.Point.Position, playerPosition))
                .Take(MaximumSurveyCandidatesToValidate)
                .ToList();
            compatiblePointCount = bounded.Count;

            foreach (DispatchInteriorPointReference candidate in bounded)
            {
                int mappedLiveInteriorId;
                string nativeResult;
                probedPointCount++;
                bool matches = TryGetMappedInteriorPointLiveId(
                    candidate.Point,
                    candidate.Location.Id,
                    playerInteriorId,
                    playerPosition,
                    out mappedLiveInteriorId,
                    out nativeResult);
                if (firstMappedLiveInteriorId == int.MinValue)
                {
                    firstMappedLiveInteriorId = mappedLiveInteriorId;
                    firstMappedNativeResult = nativeResult;
                }
                if (!matches)
                {
                    if (mappedLiveInteriorId == 0)
                    {
                        try
                        {
                            RequestInteriorCollision(candidate.Point.Position);
                            if (string.IsNullOrWhiteSpace(pendingIdentityFailure))
                                pendingIdentityFailure = "PENDING:Stage=InteriorIdentity; Location="
                                    + candidate.Location.Id
                                    + "; Point=" + candidate.Point.Id
                                    + "; CatalogInteriorId=" + candidate.Point.InteriorId
                                    + "; PlayerLiveInteriorId=" + playerInteriorId
                                    + "; Native=" + nativeResult
                                    + "; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                        }
                        catch (Exception ex)
                        {
                            if (string.IsNullOrWhiteSpace(pendingIdentityFailure))
                                pendingIdentityFailure = "Stage=InteriorIdentity; Location="
                                    + candidate.Location.Id
                                    + "; Point=" + candidate.Point.Id
                                    + "; CatalogInteriorId=" + candidate.Point.InteriorId
                                    + "; PlayerLiveInteriorId=" + playerInteriorId
                                    + "; Native=" + nativeResult
                                    + "; CollisionRequest=Exception:" + ex.GetType().Name;
                        }
                    }
                    continue;
                }

                selected = candidate;
                reason = string.Empty;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(pendingIdentityFailure)
                && pendingIdentityFailure.StartsWith("PENDING:", StringComparison.OrdinalIgnoreCase))
            {
                reason = pendingIdentityFailure
                    + "; CatalogInteriorPoints=" + catalogPointCount
                    + "; CompatibleNearbyPoints=" + compatiblePointCount
                    + "; ProbedPoints=" + probedPointCount;
                return false;
            }

            reason = "Stage=InteriorIdentity; FirstRejectedGate="
                + (compatiblePointCount == 0
                    ? "CatalogInteriorPointOrLocationContext" : "LiveInteriorIdentity")
                + "; CatalogInteriorPoints=" + catalogPointCount
                + "; CompatibleNearbyPoints=" + compatiblePointCount
                + "; ProbedPoints=" + probedPointCount
                + "; PlayerLiveInteriorId=" + playerInteriorId
                + "; FirstMappedInsideLiveInteriorId="
                + (firstMappedLiveInteriorId == int.MinValue
                    ? "NotProbed" : firstMappedLiveInteriorId.ToString(CultureInfo.InvariantCulture))
                + "; FirstMappedInsideNativeResult=" + firstMappedNativeResult
                + "; MaximumPlayerDistance=" + MaximumInteriorPlayerFallbackDistance
                + "; Result=NoInsideOnlyCatalogPointMatched";
            return false;
        }

        private bool TryResolveInsideOnlyInteriorSceneFromCatalog(
            string incidentType,
            string requiredInteriorLocationId,
            string requiredPhysicalType,
            string requiredLocationContext,
            Vector3 playerPosition,
            int playerLiveInteriorId,
            bool requiresVehicleStaging,
            LSPDDispatchLocationDefinition areaOverride,
            out AmbientDispatchSceneResolution scene,
            out string reason,
            ISet<string> excludedLocationIds = null)
        {
            scene = null;
            var rejectedPointKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string firstCandidateFailure = string.Empty;
            string firstPendingFailure = string.Empty;
            int scenePointAttempts = 0;
            string preferredInteriorLocationId = string.Empty;
            for (int attempt = 0;
                attempt < MaximumInsideOnlyInteriorSceneCandidates;
                attempt++)
            {
                DispatchInteriorPointReference pointReference;
                string pointFailure;
                if (!TryFindInsideOnlyInteriorPoint(
                    incidentType,
                    requiredInteriorLocationId,
                    requiredPhysicalType,
                    requiredLocationContext,
                    playerPosition,
                    playerLiveInteriorId,
                    out pointReference,
                    out pointFailure,
                    rejectedPointKeys,
                    preferredInteriorLocationId,
                    excludedLocationIds))
                {
                    if (pointFailure.StartsWith(
                        "PENDING:", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(firstPendingFailure))
                        firstPendingFailure = pointFailure;
                    else if (string.IsNullOrWhiteSpace(firstCandidateFailure))
                        firstCandidateFailure = pointFailure;
                    break;
                }

                scenePointAttempts++;
                rejectedPointKeys.Add(GetInsideOnlyInteriorPointKey(pointReference));
                preferredInteriorLocationId = pointReference.Location.Id;
                LSPDDispatchLocationDefinition area = areaOverride
                    ?? LSPDDispatchLocationDefinition.ForNearbyCatalogCandidate(
                        pointReference.Location.Id,
                        pointReference.Point.Position,
                        requiredLocationContext);
                string sceneFailure;
                if (TryResolveInsideOnlyInteriorScene(
                    pointReference,
                    incidentType,
                    requiredInteriorLocationId,
                    requiredPhysicalType,
                    requiredLocationContext,
                    playerPosition,
                    playerLiveInteriorId,
                    requiresVehicleStaging,
                    area,
                    out scene,
                    out sceneFailure))
                {
                    reason = string.Empty;
                    return true;
                }

                if (sceneFailure.StartsWith(
                    "PENDING:", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(firstPendingFailure))
                        firstPendingFailure = sceneFailure;
                }
                else if (string.IsNullOrWhiteSpace(firstCandidateFailure))
                {
                    firstCandidateFailure = sceneFailure;
                }
            }

            if (!string.IsNullOrWhiteSpace(firstPendingFailure))
            {
                reason = firstPendingFailure
                    + "; RuntimeValidatedPointAttempts=" + scenePointAttempts
                    + "; MaximumPointAttempts=" + MaximumInsideOnlyInteriorSceneCandidates;
                return false;
            }
            reason = (string.IsNullOrWhiteSpace(firstCandidateFailure)
                    ? "Stage=InteriorPointCandidateSelection; Result=NoCompatiblePoint"
                    : firstCandidateFailure)
                + "; RuntimeValidatedPointAttempts=" + scenePointAttempts
                + "; MaximumPointAttempts=" + MaximumInsideOnlyInteriorSceneCandidates;
            return false;
        }

        private static string GetInsideOnlyInteriorPointKey(
            DispatchInteriorPointReference reference)
        {
            return reference == null || reference.Location == null || reference.Point == null
                ? string.Empty
                : reference.Location.Id + "\0" + reference.Point.Id;
        }

        private bool TryResolveInsideOnlyInteriorScene(
            DispatchInteriorPointReference pointReference,
            string incidentType,
            string requiredInteriorLocationId,
            string requiredPhysicalType,
            string requiredLocationContext,
            Vector3 playerPosition,
            int playerLiveInteriorId,
            bool requiresVehicleStaging,
            LSPDDispatchLocationDefinition area,
            out AmbientDispatchSceneResolution scene,
            out string reason)
        {
            scene = null;
            reason = string.Empty;
            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location =
                pointReference == null ? null : pointReference.Location;
            LSImmersiveLocationCatalog.LSImmersiveLocationPoint point =
                pointReference == null ? null : pointReference.Point;
            if (location == null || point == null
                || !string.Equals(point.Role, "InteriorPointCandidate",
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "Stage=InteriorCatalogPoint; FirstRejectedGate=InteriorPointCandidate"
                    + "; Result=DoorAndLinkedReferencePointsCannotBeSceneAnchors";
                return false;
            }
            if (requiresVehicleStaging)
            {
                reason = "Stage=PlayerUtilityStaging; Location=" + location.Id
                    + "; CatalogInteriorId=" + point.InteriorId
                    + "; LiveInteriorId=" + playerLiveInteriorId
                    + "; Result=InsideOnlyInteriorHasNoVerifiedExteriorStagingPoint";
                return false;
            }
            if (playerLiveInteriorId == 0 || !HasObservedNonZeroInterior(point.InteriorId))
            {
                reason = "Stage=InteriorIdentity; Location=" + location.Id
                    + "; CatalogInteriorId=" + (point.InteriorId ?? string.Empty)
                    + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                    + "; Result=NonzeroCatalogAndRuntimeIdentitiesRequired";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(requiredInteriorLocationId)
                && !string.Equals(location.Id, requiredInteriorLocationId,
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "Stage=CatalogLocationIdentity; RequiredLocation="
                    + requiredInteriorLocationId + "; CandidateLocation=" + location.Id;
                return false;
            }
            if (IsRejectedSurveyRecord(location)
                || !MatchesRequiredPhysicalType(location, requiredPhysicalType)
                || !IsCompatibleWithIncident(location, incidentType)
                || !MatchesDispatchLocationContextMetadata(location, requiredLocationContext))
            {
                reason = "Stage=CatalogEligibility; Location=" + location.Id
                    + "; PhysicalType=" + location.PhysicalType
                    + "; IncidentType=" + (incidentType ?? string.Empty)
                    + "; RequiredPhysicalType=" + (requiredPhysicalType ?? string.Empty)
                    + "; RequiredContext=" + (requiredLocationContext ?? string.Empty)
                    + "; Result=CatalogCompatibilityChanged";
                return false;
            }
            if (Distance3D(point.Position, playerPosition)
                > MaximumInteriorPlayerFallbackDistance)
            {
                reason = "Stage=PlayerDistance; Location=" + location.Id
                    + "; Point=" + point.Id
                    + "; Distance=" + Distance3D(point.Position, playerPosition)
                        .ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Maximum=" + MaximumInteriorPlayerFallbackDistance;
                return false;
            }

            int currentPlayerLiveInteriorId;
            bool currentPlayerIdentityAvailable = TryGetDispatchPlayerInteriorId(
                Game.Player.Character, out currentPlayerLiveInteriorId);
            if (!currentPlayerIdentityAvailable)
            {
                reason = "Stage=PlayerInteriorIdentity; Location=" + location.Id
                    + "; CatalogInteriorId=" + point.InteriorId
                    + "; ExpectedPlayerLiveInteriorId=" + playerLiveInteriorId
                    + "; CurrentPlayerLiveInteriorId=Unavailable"
                    + "; Native=GET_INTERIOR_AT_COORDS/GET_INTERIOR_FROM_ENTITY"
                    + "; Result=PlayerIdentityProbeUnavailable";
                return false;
            }
            if (currentPlayerLiveInteriorId == 0)
            {
                try
                {
                    RequestInteriorCollision(playerPosition);
                }
                catch (Exception ex)
                {
                    reason = "Stage=PlayerInteriorIdentity; Location=" + location.Id
                        + "; CatalogInteriorId=" + point.InteriorId
                        + "; ExpectedPlayerLiveInteriorId=" + playerLiveInteriorId
                        + "; CurrentPlayerLiveInteriorId=0"
                        + "; Native=GET_INTERIOR_AT_COORDS:0"
                        + "; CollisionRequest=Exception:" + ex.GetType().Name;
                    return false;
                }
                reason = "PENDING:Stage=PlayerInteriorIdentity; Location=" + location.Id
                    + "; CatalogInteriorId=" + point.InteriorId
                    + "; ExpectedPlayerLiveInteriorId=" + playerLiveInteriorId
                    + "; CurrentPlayerLiveInteriorId=0"
                    + "; Native=GET_INTERIOR_AT_COORDS:0"
                    + "; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                return false;
            }
            if (currentPlayerLiveInteriorId != playerLiveInteriorId)
            {
                reason = "Stage=PlayerInteriorIdentity; Location=" + location.Id
                    + "; CatalogInteriorId=" + point.InteriorId
                    + "; ExpectedPlayerLiveInteriorId=" + playerLiveInteriorId
                    + "; CurrentPlayerLiveInteriorId=" + currentPlayerLiveInteriorId
                    + "; Native=GET_INTERIOR_AT_COORDS/GET_INTERIOR_FROM_ENTITY"
                    + "; Result=PlayerNoLongerInsideSelectedInterior";
                return false;
            }

            int pointLiveInteriorId;
            try
            {
                pointLiveInteriorId = GetInteriorAt(point.Position);
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorIdentity; Location=" + location.Id
                    + "; Point=" + point.Id
                    + "; CatalogInteriorId=" + point.InteriorId
                    + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                    + "; Native=GET_INTERIOR_AT_COORDS; Result=Exception:" + ex.GetType().Name;
                return false;
            }
            if (pointLiveInteriorId != playerLiveInteriorId)
            {
                if (pointLiveInteriorId == 0)
                {
                    try
                    {
                        RequestInteriorCollision(point.Position);
                    }
                    catch (Exception ex)
                    {
                        reason = "Stage=InteriorIdentity; Location=" + location.Id
                            + "; Point=" + point.Id
                            + "; CatalogInteriorId=" + point.InteriorId
                            + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                            + "; PointLiveInteriorId=0"
                            + "; Native=GET_INTERIOR_AT_COORDS:0"
                            + "; CollisionRequest=Exception:" + ex.GetType().Name;
                        return false;
                    }
                    reason = "PENDING:Stage=InteriorIdentity; Location=" + location.Id
                        + "; Point=" + point.Id
                        + "; CatalogInteriorId=" + point.InteriorId
                        + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                        + "; PointLiveInteriorId=0"
                        + "; Native=GET_INTERIOR_AT_COORDS:0"
                        + "; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                    return false;
                }
                reason = "Stage=InteriorIdentity; Location=" + location.Id
                    + "; Point=" + point.Id
                    + "; CatalogInteriorId=" + point.InteriorId
                    + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                    + "; PointLiveInteriorId=" + pointLiveInteriorId
                    + "; Native=GET_INTERIOR_AT_COORDS; Result=InteriorIdentityMismatch";
                return false;
            }

            Vector3 walkable;
            string floorFailure;
            if (!TryResolveInteriorWalkablePosition(
                point.Position,
                playerLiveInteriorId,
                point.Position,
                out walkable,
                out floorFailure,
                playerPosition,
                false))
            {
                reason = floorFailure
                    + "; Location=" + location.Id
                    + "; Point=" + point.Id
                    + "; CatalogInteriorId=" + point.InteriorId
                    + "; LiveInteriorId=" + playerLiveInteriorId
                    + "; ThresholdPair=Unavailable";
                return false;
            }

            scene = new AmbientDispatchSceneResolution
            {
                Area = area,
                AreaLocationId = area == null ? location.Id : area.Id,
                AreaAnchorPosition = point.Position,
                LocationId = location.Id,
                LocationName = location.Name,
                LocationCategory = location.Category,
                PointId = point.Id,
                SourceCaptureId = point.SourceRecord,
                AuthoredPosition = point.Position,
                SceneCenter = walkable,
                ActorPosition = walkable,
                Heading = point.HasHeading ? point.Heading : 0f,
                VehicleStagingPosition = Vector3.Zero,
                VehicleHeading = 0f,
                HasVehicleStagingPosition = false,
                IsSurveyedPoint = true,
                MaximumSnapDistance = InteriorActivityMaximumDepth,
                IsInteriorScene = true,
                IsMappedInteriorDispatchScene = false,
                IsInsideOnlyInteriorScene = true,
                CatalogInteriorId = point.InteriorId,
                LiveInteriorId = playerLiveInteriorId,
                IncidentType = incidentType ?? string.Empty,
                InteriorAccessPair = null,
                InsideOnlyLocation = location,
                InsideOnlyPoint = point,
                ValidationStatus = "RuntimeValidatedInsideCatalogPointWithoutMappedThreshold",
                LocationReviewStatus = location.ReviewStatus
            };
            reason = string.Empty;
            return true;
        }

        private void LogInsideOnlyInteriorSceneSelected(
            AmbientDispatchSceneResolution scene,
            string resolutionPath)
        {
            if (scene == null || !scene.IsInsideOnlyInteriorScene)
                return;
            Log("AMBIENT_WORLD_INSIDE_ONLY_INTERIOR_DISPATCH_SELECTED",
                "ResolutionPath=" + (resolutionPath ?? string.Empty)
                + "; Location=" + scene.LocationId
                + "; Point=" + scene.PointId
                + "; PointRole=InteriorPointCandidate"
                + "; CatalogSpawnEligible="
                + (scene.InsideOnlyPoint != null && scene.InsideOnlyPoint.SpawnEligible)
                + "; CatalogInteriorId=" + scene.CatalogInteriorId
                + "; LiveInteriorId=" + scene.LiveInteriorId
                + "; ReviewStatus=" + scene.LocationReviewStatus
                + "; SceneCenter=" + scene.SceneCenter
                + "; VehicleStaging=False"
                + "; DoorPair=Unavailable"
                + "; SuspectDoorwayEscape=UnavailableWithoutMappedPair"
                + "; Validation=" + scene.ValidationStatus);
        }

        private static float Distance3D(Vector3 left, Vector3 right)
        {
            float distance = left.DistanceTo(right);
            return float.IsNaN(distance) || float.IsInfinity(distance)
                ? float.MaxValue : distance;
        }

        internal bool HasNearbyCompatibleDispatchCatalogLocation(
            LSPDDispatchEventDefinition definition,
            Vector3 playerPosition,
            int playerInteriorId,
            bool playerInteriorIdAvailable,
            ISet<string> excludedLocationIds = null)
        {
            string ignoredReason;
            return HasNearbyCompatibleDispatchCatalogLocation(
                definition,
                playerPosition,
                playerInteriorId,
                playerInteriorIdAvailable,
                out ignoredReason,
                excludedLocationIds);
        }

        internal bool HasNearbyCompatibleDispatchCatalogLocation(
            LSPDDispatchEventDefinition definition,
            Vector3 playerPosition,
            int playerInteriorId,
            bool playerInteriorIdAvailable,
            out string reason,
            ISet<string> excludedLocationIds = null)
        {
            reason = string.Empty;
            if (definition == null
                || _locationCatalog == null)
            {
                reason = "Stage=CatalogAvailability; FirstRejectedGate=LocationCatalogUnavailable";
                return false;
            }

            bool interiorDefinition = definition.RequiresMappedInterior
                || !string.IsNullOrWhiteSpace(definition.InteriorLocationId);
            if (interiorDefinition && !playerInteriorIdAvailable)
            {
                reason = "Stage=PlayerInteriorIdentity; FirstRejectedGate=PlayerInteriorNativeUnavailable";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(definition.InteriorLocationId))
            {
                int exactLocationPairCount = 0;
                int surveyEligiblePairCount = 0;
                int physicalTypeEligiblePairCount = 0;
                int incidentEligiblePairCount = 0;
                int contextEligiblePairCount = 0;
                int catalogIdentityEligiblePairCount = 0;
                int liveInteriorIdentityChecks = 0;
                int liveInteriorIdentityMatches = 0;
                int repeatProtectedPairCount = 0;
                int firstMappedInsideLiveId = int.MinValue;
                string firstMappedInsideNativeResult = string.Empty;
                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                    in GetInteriorAccessPairs())
                {
                    if (pair == null || pair.InsideLocation == null
                        || pair.OutsidePoint == null || pair.InsidePoint == null
                        || !string.Equals(pair.InsideLocation.Id,
                            definition.InteriorLocationId,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    exactLocationPairCount++;
                    if (excludedLocationIds != null
                        && excludedLocationIds.Contains(pair.InsideLocation.Id))
                    {
                        repeatProtectedPairCount++;
                        continue;
                    }
                    if (IsRejectedSurveyRecord(pair.InsideLocation))
                        continue;
                    surveyEligiblePairCount++;
                    if (!MatchesRequiredPhysicalType(
                        pair.InsideLocation, definition.RequiredPhysicalType))
                        continue;
                    physicalTypeEligiblePairCount++;
                    if (!IsCompatibleWithMappedInteriorIncident(
                        pair.InsideLocation, definition.IncidentType,
                        definition.RequiredPhysicalType))
                        continue;
                    incidentEligiblePairCount++;
                    if (!MatchesMappedInteriorDispatchLocationContextMetadata(
                        pair.InsideLocation, definition.LocationContext))
                        continue;
                    contextEligiblePairCount++;
                    if (!HasCatalogedMappedInteriorReference(pair))
                        continue;
                    catalogIdentityEligiblePairCount++;

                    int mappedLiveInteriorId;
                    string nativeResult;
                    bool pairEligible = IsDispatchInteriorPairWithinPlayerRange(
                        pair, playerPosition, playerInteriorId,
                        out mappedLiveInteriorId, out nativeResult);
                    if (playerInteriorId != 0)
                    {
                        liveInteriorIdentityChecks++;
                        if (firstMappedInsideLiveId == int.MinValue)
                        {
                            firstMappedInsideLiveId = mappedLiveInteriorId;
                            firstMappedInsideNativeResult = nativeResult;
                        }
                        if (pairEligible)
                            liveInteriorIdentityMatches++;
                    }
                    if (pairEligible)
                        return true;
                }

                if (playerInteriorId != 0
                    && !definition.RequiresVehicle
                    && !definition.VehicleStagingRequired)
                {
                    DispatchInteriorPointReference insideOnlyPoint;
                    string insideOnlyFailure = string.Empty;
                    if (TryFindInsideOnlyInteriorPoint(
                        definition.IncidentType,
                        definition.InteriorLocationId,
                        definition.RequiredPhysicalType,
                        definition.LocationContext,
                        playerPosition,
                        playerInteriorId,
                        out insideOnlyPoint,
                        out insideOnlyFailure,
                        null,
                        null,
                        excludedLocationIds))
                    {
                        reason = "Stage=CatalogEligibility; EligibleFrom=InsideOnlyInteriorPoint"
                            + "; Location=" + insideOnlyPoint.Location.Id
                            + "; Point=" + insideOnlyPoint.Point.Id
                            + "; CatalogInteriorId=" + insideOnlyPoint.Point.InteriorId
                            + "; PlayerLiveInteriorId=" + playerInteriorId
                            + "; EgressPair=Unavailable";
                        return true;
                    }
                    if (insideOnlyFailure.StartsWith(
                        "PENDING:", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = insideOnlyFailure;
                        return true;
                    }
                }

                string firstRejectedGate = exactLocationPairCount == 0
                    ? "RequiredInteriorLocationId"
                    : repeatProtectedPairCount == exactLocationPairCount
                        ? "LocationRepeatProtection"
                    : surveyEligiblePairCount == 0
                        ? "SurveyReviewStatus"
                        : physicalTypeEligiblePairCount == 0
                            ? "RequiredPhysicalType"
                            : incidentEligiblePairCount == 0
                                ? "IncidentCompatibility"
                                : contextEligiblePairCount == 0
                                    ? "LocationContext"
                                    : catalogIdentityEligiblePairCount == 0
                                            ? "CatalogInteriorReference"
                                        : playerInteriorId != 0
                                            ? "LiveInteriorIdentity"
                                            : "PlayerDistance";
                reason = "Stage=CatalogEligibility; FirstRejectedGate=" + firstRejectedGate
                    + "; Location=" + definition.InteriorLocationId
                    + "; RequiredPhysicalType=" + (definition.RequiredPhysicalType ?? string.Empty)
                    + "; RequiredContext=" + (definition.LocationContext ?? string.Empty)
                    + "; IncidentType=" + (definition.IncidentType ?? string.Empty)
                    + "; ExactLocationPairs=" + exactLocationPairCount
                    + "; RepeatProtectedPairs=" + repeatProtectedPairCount
                    + "; SurveyEligible=" + surveyEligiblePairCount
                    + "; PhysicalTypeEligible=" + physicalTypeEligiblePairCount
                    + "; IncidentEligible=" + incidentEligiblePairCount
                    + "; ContextEligible=" + contextEligiblePairCount
                    + "; CatalogIdentityEligible=" + catalogIdentityEligiblePairCount
                    + "; PlayerLiveInteriorId=" + playerInteriorId
                    + "; LiveInteriorIdentityChecks=" + liveInteriorIdentityChecks
                    + "; LiveInteriorIdentityMatches=" + liveInteriorIdentityMatches
                    + "; FirstMappedInsideLiveInteriorId="
                    + (firstMappedInsideLiveId == int.MinValue
                        ? "NotProbed" : firstMappedInsideLiveId.ToString(CultureInfo.InvariantCulture))
                    + "; FirstMappedInsideNativeResult="
                    + (firstMappedInsideNativeResult ?? string.Empty);
                return false;
            }

            if (definition.RequiresMappedInterior)
            {
                // Eligibility checks run before Dispatch selects an event.
                // Include every cataloged threshold pair here so a mapped
                // bank, store, home, or other accessible interior is not
                // hidden by the event's broad geographic area references.
                // This is metadata-only; live interior, floor, and doorway
                // validation remains capped in TryResolveMappedInteriorDispatchScene.
                int accessPairCount = 0;
                int surveyEligiblePairCount = 0;
                int physicalTypeEligiblePairCount = 0;
                int incidentEligiblePairCount = 0;
                int contextEligiblePairCount = 0;
                int catalogIdentityEligiblePairCount = 0;
                int liveInteriorIdentityChecks = 0;
                int liveInteriorIdentityMatches = 0;
                int repeatProtectedPairCount = 0;
                int firstMappedInsideLiveId = int.MinValue;
                string firstMappedInsideNativeResult = string.Empty;
                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                    in GetInteriorAccessPairs())
                {
                    if (pair == null || pair.InsideLocation == null
                        || pair.OutsidePoint == null || pair.InsidePoint == null
                        || !HasCatalogedMappedInteriorReference(pair))
                        continue;
                    accessPairCount++;
                    if (excludedLocationIds != null
                        && excludedLocationIds.Contains(pair.InsideLocation.Id))
                    {
                        repeatProtectedPairCount++;
                        continue;
                    }
                    if (IsRejectedSurveyRecord(pair.InsideLocation))
                        continue;
                    surveyEligiblePairCount++;
                    if (!MatchesRequiredPhysicalType(
                        pair.InsideLocation, definition.RequiredPhysicalType))
                        continue;
                    physicalTypeEligiblePairCount++;
                    if (!IsCompatibleWithMappedInteriorIncident(
                        pair.InsideLocation, definition.IncidentType,
                        definition.RequiredPhysicalType))
                        continue;
                    incidentEligiblePairCount++;
                    if (!MatchesMappedInteriorDispatchLocationContextMetadata(
                        pair.InsideLocation, definition.LocationContext))
                        continue;
                    contextEligiblePairCount++;
                    catalogIdentityEligiblePairCount++;

                    int mappedLiveInteriorId;
                    string nativeResult;
                    bool pairEligible = IsDispatchInteriorPairWithinPlayerRange(
                        pair, playerPosition, playerInteriorId,
                        out mappedLiveInteriorId, out nativeResult);
                    if (playerInteriorId != 0)
                    {
                        liveInteriorIdentityChecks++;
                        if (firstMappedInsideLiveId == int.MinValue)
                        {
                            firstMappedInsideLiveId = mappedLiveInteriorId;
                            firstMappedInsideNativeResult = nativeResult;
                        }
                        if (pairEligible)
                            liveInteriorIdentityMatches++;
                    }
                    if (pairEligible)
                        return true;
                }

                if (playerInteriorId != 0
                    && !definition.RequiresVehicle
                    && !definition.VehicleStagingRequired)
                {
                    DispatchInteriorPointReference insideOnlyPoint;
                    string insideOnlyFailure = string.Empty;
                    if (TryFindInsideOnlyInteriorPoint(
                        definition.IncidentType,
                        definition.InteriorLocationId,
                        definition.RequiredPhysicalType,
                        definition.LocationContext,
                        playerPosition,
                        playerInteriorId,
                        out insideOnlyPoint,
                        out insideOnlyFailure,
                        null,
                        null,
                        excludedLocationIds))
                    {
                        reason = "Stage=CatalogEligibility; EligibleFrom=InsideOnlyInteriorPoint"
                            + "; Location=" + insideOnlyPoint.Location.Id
                            + "; Point=" + insideOnlyPoint.Point.Id
                            + "; CatalogInteriorId=" + insideOnlyPoint.Point.InteriorId
                            + "; PlayerLiveInteriorId=" + playerInteriorId
                            + "; EgressPair=Unavailable";
                        return true;
                    }
                    if (insideOnlyFailure.StartsWith(
                        "PENDING:", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = insideOnlyFailure;
                        return true;
                    }
                }

                string firstRejectedGate = accessPairCount == 0
                    ? "CatalogInteriorReference"
                    : repeatProtectedPairCount == accessPairCount
                        ? "LocationRepeatProtection"
                    : surveyEligiblePairCount == 0
                        ? "SurveyReviewStatus"
                        : physicalTypeEligiblePairCount == 0
                            ? "RequiredPhysicalType"
                            : incidentEligiblePairCount == 0
                                ? "IncidentCompatibility"
                                : contextEligiblePairCount == 0
                            ? "LocationContext"
                            : playerInteriorId != 0
                                ? "LiveInteriorIdentity"
                                : "PlayerDistance";
                reason = "Stage=CatalogEligibility; FirstRejectedGate=" + firstRejectedGate
                    + "; RequiredPhysicalType=" + (definition.RequiredPhysicalType ?? string.Empty)
                    + "; RequiredContext=" + (definition.LocationContext ?? string.Empty)
                    + "; IncidentType=" + (definition.IncidentType ?? string.Empty)
                    + "; CatalogPairs=" + accessPairCount
                    + "; RepeatProtectedPairs=" + repeatProtectedPairCount
                    + "; SurveyEligible=" + surveyEligiblePairCount
                    + "; PhysicalTypeEligible=" + physicalTypeEligiblePairCount
                    + "; IncidentEligible=" + incidentEligiblePairCount
                    + "; ContextEligible=" + contextEligiblePairCount
                    + "; CatalogIdentityEligible=" + catalogIdentityEligiblePairCount
                    + "; PlayerLiveInteriorId=" + playerInteriorId
                    + "; LiveInteriorIdentityChecks=" + liveInteriorIdentityChecks
                    + "; LiveInteriorIdentityMatches=" + liveInteriorIdentityMatches
                    + "; FirstMappedInsideLiveInteriorId="
                    + (firstMappedInsideLiveId == int.MinValue
                        ? "NotProbed" : firstMappedInsideLiveId.ToString(CultureInfo.InvariantCulture))
                    + "; FirstMappedInsideNativeResult="
                    + (firstMappedInsideNativeResult ?? string.Empty);
                return false;
            }

            IEnumerable<LSImmersiveLocationCatalog.LSImmersiveLocationDefinition> locations =
                _locationCatalog.FindByCategory("SurveyedCandidate").Concat(
                    _locationCatalog.FindByCategory("AreaCandidate"));
            bool requiresVehicleScene = definition.RequiresVehicle
                || definition.VehicleStagingRequired;
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location in locations)
            {
                if ((excludedLocationIds != null
                        && excludedLocationIds.Contains(location.Id))
                    || IsRejectedSurveyRecord(location)
                    || !IsCompatibleWithIncident(location, definition.IncidentType)
                    || (requiresVehicleScene
                        && string.Equals(location.Category, "SurveyedCandidate",
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(location.IntendedUse, "FootActivityCandidate",
                            StringComparison.OrdinalIgnoreCase))
                    || !MatchesRequiredPhysicalType(location, definition.RequiredPhysicalType)
                    || !MatchesDispatchLocationContextMetadata(
                        location, definition.LocationContext))
                    continue;

                string pointRole = string.Equals(
                    location.Category, "SurveyedCandidate", StringComparison.OrdinalIgnoreCase)
                    ? "ObservedCoordinateCandidate" : "AreaSearchAnchor";
                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationPoint point
                    in location.FindPoints(pointRole, false))
                {
                    if (HasObservedNonZeroInterior(point.InteriorId)
                        || Distance2D(point.Position, playerPosition)
                            > LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance)
                        continue;
                    return true;
                }
            }
            reason = "Stage=CatalogEligibility; FirstRejectedGate=NoCompatibleNearbyCatalogPoint"
                + "; IncidentType=" + (definition.IncidentType ?? string.Empty)
                + "; RequiredPhysicalType=" + (definition.RequiredPhysicalType ?? string.Empty)
                + "; RequiredContext=" + (definition.LocationContext ?? string.Empty);
            return false;
        }

        private static bool IsCommercialDispatchLocationType(string value)
        {
            return string.Equals(value, "Store", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Storefront", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "ConvenienceStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "ClothingStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "GunStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "LiquorStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "GasStation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "PawnShop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "HardwareStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "SportingGoodsStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Pharmacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Retail", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Business", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Restaurant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Bar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "FoodStand", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Dealership", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "ModShop", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNearValidatedDispatchRoad(Vector3 position)
        {
            try
            {
                float heading;
                Vector3 roadNode = World.GetNextPositionOnStreetWithHeading(
                    position, out heading);
                return roadNode != Vector3.Zero
                    && !float.IsNaN(heading)
                    && !float.IsInfinity(heading)
                    && Distance2D(position, roadNode) <= 45f
                    && Math.Abs(position.Z - roadNode.Z) <= 8f
                    && Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                        roadNode.X, roadNode.Y, roadNode.Z, 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRejectedSurveyStatus(string status)
        {
            return string.Equals(status, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "PotentialMisclick", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "InvalidCandidate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Anomalous", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasObservedNonZeroInterior(string interiorId)
        {
            int value;
            return int.TryParse(interiorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value)
                && value != 0;
        }

        private void LogLocationResolution(
            AmbientDispatchSceneResolution scene,
            bool requiresVehicle)
        {
            if (scene == null)
                return;
            string identity = string.Equals(scene.LocationReviewStatus, "OwnerConfirmed",
                    StringComparison.OrdinalIgnoreCase)
                ? "OwnerConfirmedMapReference"
                : scene.IsSurveyedPoint
                    ? "SurveyedCandidate" : "AreaCandidate_NotNamedSite";
            Log("AMBIENT_WORLD_LOCATION_RESOLVED",
                "Location=" + scene.LocationId
                + "; Name=" + scene.LocationName
                + "; Category=" + scene.LocationCategory
                + "; Point=" + scene.PointId
                + "; Capture=" + scene.SourceCaptureId
                + "; Area=" + scene.AreaLocationId
                + "; Mode=" + scene.ValidationStatus
                + "; Ped=" + scene.ActorPosition
                + "; AuthoredPosition=" + scene.AuthoredPosition
                + "; ResolvedSnapDistance=" + scene.ActorPosition.DistanceTo(
                    scene.AuthoredPosition).ToString("0.00", CultureInfo.InvariantCulture)
                + "; ExteriorRoadHeightTolerance=" + scene.ExteriorRoadHeightTolerance.ToString(
                    "0.00", CultureInfo.InvariantCulture)
                + "; Area=" + (scene.Area == null ? string.Empty : scene.Area.Id)
                + "; AreaContexts=" + (scene.Area == null || scene.Area.Contexts == null
                    ? string.Empty : string.Join(",", scene.Area.Contexts))
                + "; ReviewStatus=" + (scene.LocationReviewStatus ?? "Unknown")
                + "; Interior=" + scene.IsInteriorScene
                + "; CatalogInteriorId=" + scene.CatalogInteriorId
                + "; LiveInteriorId=" + scene.LiveInteriorId
                + "; ExteriorApproachPosition=" + (scene.IsInteriorScene
                    ? "NotApplicable" : scene.ExteriorApproachPosition.ToString())
                + "; ExteriorPlacementEvidence=" + (scene.IsInteriorScene
                    ? "NotApplicable" : (scene.ExteriorPlacementEvidence ?? "Missing"))
                + "; VehicleRequired=" + requiresVehicle
                + "; VehicleStaging=" + (scene.HasVehicleStagingPosition
                    ? scene.VehicleStagingPosition.ToString() : "Unavailable")
                + "; VehicleStagingMode=" + (scene.VehicleStagingMode ?? "none")
                + "; VehicleRoute=" + (scene.HasVehicleRoute
                    ? scene.VehicleRoutePosition.ToString() : "NotRequired")
                + "; LocationCatalogSite=" + (scene.LocationCategory == "PlayerRadiusRoadNode"
                    ? "None" : scene.LocationId)
                + "; Identity=" + identity
                + "; RuntimeValidation=" + (scene.ValidationStatus ?? "Unknown")
                + "; InteriorPair=" + (scene.InteriorAccessPair == null
                    ? "None" : scene.InteriorAccessPair.MappingBasis));
        }

        private void RememberLocation(string locationId)
        {
            if (string.IsNullOrWhiteSpace(locationId))
                return;
            if (_recentLocationIdSet.Add(locationId))
                _recentLocationIds.Enqueue(locationId);
            while (_recentLocationIds.Count > RecentLocationHistoryLimit)
                _recentLocationIdSet.Remove(_recentLocationIds.Dequeue());
        }

        private void Shuffle<T>(IList<T> values)
        {
            for (int index = values.Count - 1; index > 0; index--)
            {
                int swap = _locationRandom.Next(index + 1);
                T value = values[index];
                values[index] = values[swap];
                values[swap] = value;
            }
        }

        private List<T> ShuffleCopy<T>(IEnumerable<T> values)
        {
            var result = (values ?? Enumerable.Empty<T>()).ToList();
            Shuffle(result);
            return result;
        }

        private static float Distance2D(Vector3 first, Vector3 second)
        {
            float x = first.X - second.X;
            float y = first.Y - second.Y;
            return (float)Math.Sqrt((x * x) + (y * y));
        }

        private static bool IsWithinPlayerDistance(
            LSPDDispatchLocationDefinition area,
            Vector3 position,
            Vector3 playerPosition)
        {
            if (area == null)
                return false;
            float distance = position.DistanceTo(playerPosition);
            return distance >= area.MinimumPlayerDistance
                && distance <= area.MaximumPlayerDistance;
        }

        private void Log(string category, string message)
        {
            if (_log != null)
            {
                bool recurringLocationFailure =
                    string.Equals(category,
                        "AMBIENT_WORLD_INTERIOR_SCENE_REJECTED",
                        StringComparison.Ordinal)
                    || string.Equals(category,
                        "AMBIENT_WORLD_NEARBY_DISPATCH_CATALOG_FALLBACK_UNAVAILABLE",
                        StringComparison.Ordinal)
                    || string.Equals(category,
                        "AMBIENT_WORLD_LOCATION_QUERY_REJECTED",
                        StringComparison.Ordinal);
                if (recurringLocationFailure)
                    _log.RuntimeThrottled(category, message,
                        TimeSpan.FromSeconds(30));
                else
                    _log.Runtime(category, message);
            }
        }

        private void LogDebugTransition(string category, string state, string message)
        {
            if (_log != null)
                _log.DebugTransition(category, state, message);
        }

        private void ClearDebugTransition(string category)
        {
            if (_log != null)
                _log.ClearDebugTransition(category);
        }

        private static string GetAmbientFailureStage(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "Unknown";
            int stageStart = reason.IndexOf("Stage=", StringComparison.OrdinalIgnoreCase);
            if (stageStart < 0)
                return reason.Split(';')[0].Trim();
            int valueStart = stageStart + "Stage=".Length;
            int stageEnd = reason.IndexOf(';', valueStart);
            return (stageEnd < 0
                ? reason.Substring(valueStart)
                : reason.Substring(valueStart, stageEnd - valueStart)).Trim();
        }

        private void LogDispatchInteriorPinTransition(
            int liveInteriorId,
            string state,
            string message)
        {
            if (_log != null)
                _log.RuntimeTransition(
                    "AMBIENT_WORLD_DISPATCH_INTERIOR_PIN_" + liveInteriorId,
                    state,
                    message);
        }

        internal sealed class DispatchFootSearchProgress
        {
            internal Vector3 PlayerPosition;
            internal DateTime ExpiresAt;
            internal readonly HashSet<string> Attempted =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal bool TryBeginProbe(string key, ref int probes, int maximum)
            {
                if (Attempted.Contains(key) || probes >= maximum)
                    return false;
                Attempted.Add(key);
                probes++;
                return true;
            }
        }

        private sealed class DispatchLocationCandidate
        {
            internal LSImmersiveLocationCatalog.LSImmersiveLocationDefinition Location;
            internal LSImmersiveLocationCatalog.LSImmersiveLocationPoint Point;
            internal LSPDDispatchLocationDefinition Area;
            internal float AreaDistance;
        }

        private sealed class DispatchInteriorPointReference
        {
            internal LSImmersiveLocationCatalog.LSImmersiveLocationDefinition Location;
            internal LSImmersiveLocationCatalog.LSImmersiveLocationPoint Point;
        }

        private sealed class DispatchCivilianClaim
        {
            internal string IncidentId;
            internal int PedHandle;
            internal bool WasPersistent;
            internal bool BlockedPermanentEvents;
            internal bool WasPositionFrozen;
            internal bool WasVisible;
        }

        internal int RegisteredActorCount
        {
            get { return _actors.Count; }
        }

        internal IEnumerable<AmbientActorContext> Actors
        {
            get { return _actors.Values; }
        }

        internal bool IsDispatchAmbientWorldSceneActor(
            LSPDDispatchEvent incident,
            Ped actor)
        {
            if (incident == null || actor == null || !actor.Exists())
                return false;
            int handle = actor.Handle;
            if (incident.AdditionalParticipants != null
                && incident.AdditionalParticipants.Any(participant =>
                    participant != null && participant.Exists()
                    && participant.Handle == handle))
                return true;

            DispatchCivilianClaim claim;
            return _dispatchCivilianClaims.TryGetValue(handle, out claim)
                && claim != null && claim.PedHandle == handle
                && string.Equals(claim.IncidentId, incident.Id,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Registers one actor that another system has deliberately made eligible
        /// for ambient behavior management. This does not scan the world and does
        /// not spawn anything.
        /// </summary>
        internal bool RegisterActor(Ped ped, AmbientActorContext context)
        {
            if (ped == null || !ped.Exists() || context == null)
                return false;

            int handle = ped.Handle;
            context.EntityHandle = handle;
            _actors[handle] = context;
            _nextEvaluationAt[handle] = 0;
            return true;
        }

        internal bool UnregisterActor(Ped ped)
        {
            if (ped == null)
                return false;

            return UnregisterActor(ped.Handle);
        }

        internal bool UnregisterActor(int entityHandle)
        {
            _nextEvaluationAt.Remove(entityHandle);
            return _actors.Remove(entityHandle);
        }

        internal bool TryCreateDispatchSceneSuspect(
            string incidentId,
            Model model,
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            out Ped suspect,
            out string reason)
        {
            suspect = null;
            reason = "The criminal model or validated Dispatch scene is unavailable.";
            if (string.IsNullOrWhiteSpace(incidentId) || scene == null
                || !model.IsValid || !model.IsInCdImage || !model.IsLoaded)
            {
                reason = "Stage=SuspectModelOrScene; Model=" + model.Hash
                    + "; Loaded=" + model.IsLoaded + "; SceneAvailable=" + (scene != null);
                return false;
            }

            int expectedInteriorId = scene.IsInteriorScene ? scene.LiveInteriorId : 0;
            try
            {
                int requestedInteriorId = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                    preferredPosition.X, preferredPosition.Y, preferredPosition.Z);
                if ((scene.IsInteriorScene && expectedInteriorId == 0)
                    || requestedInteriorId != expectedInteriorId)
                {
                    reason = "Stage=SuspectSceneIdentity; ExpectedLiveInteriorId="
                        + expectedInteriorId + "; CoordinateInteriorId=" + requestedInteriorId
                        + "; CatalogInteriorId=" + scene.CatalogInteriorId + "; Result=Rejected";
                    return false;
                }

                suspect = World.CreatePed(model, preferredPosition);
                if (suspect == null || !suspect.Exists() || suspect.IsDead || suspect.Health <= 0)
                {
                    reason = "Stage=SuspectEntityCreation; Result=NoLivingEntity";
                    suspect = null;
                    return false;
                }

                Vector3 placed = suspect.Position;
                float horizontalDrift = Distance2D(placed, preferredPosition);
                float verticalDrift = Math.Abs(placed.Z - preferredPosition.Z);
                int entityInteriorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, suspect);
                int placedInteriorId = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS,
                    placed.X, placed.Y, placed.Z);
                if (horizontalDrift > 0.75f || verticalDrift > 1.0f
                    || entityInteriorId != expectedInteriorId
                    || placedInteriorId != expectedInteriorId)
                {
                    reason = "Stage=SuspectPlacementRevalidation; Preferred=" + preferredPosition
                        + "; Placed=" + placed
                        + "; HorizontalDrift=" + horizontalDrift.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; VerticalDrift=" + verticalDrift.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; EntityInteriorId=" + entityInteriorId
                        + "; CoordinateInteriorId=" + placedInteriorId
                        + "; ExpectedLiveInteriorId=" + expectedInteriorId + "; Result=Rejected";
                    suspect.Delete();
                    suspect = null;
                    return false;
                }

                Function.Call(Hash.SET_ENTITY_VISIBLE, suspect, false, false);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, suspect, true);
                suspect.IsPersistent = true;
                suspect.BlockPermanentEvents = true;
                suspect.IsPositionFrozen = true;
                Log("AMBIENT_WORLD_DISPATCH_SCENE_SUSPECT_CREATED",
                    "Incident=" + incidentId + "; Ped=" + suspect.Handle
                    + "; Model=" + model.Hash + "; Position=" + suspect.Position
                    + "; CatalogInteriorId=" + scene.CatalogInteriorId
                    + "; LiveInteriorId=" + expectedInteriorId
                    + "; HiddenUntilAcceptance=true; Owner=AmbientWorldStaging");
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                if (suspect != null)
                {
                    try { if (suspect.Exists()) suspect.Delete(); } catch { }
                    suspect = null;
                }
                reason = "Stage=SuspectEntityCreation; Result=Exception:" + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_SCENE_SUSPECT_CREATE_FAILED",
                    "Incident=" + incidentId + "; Model=" + model.Hash + "; " + reason);
                return false;
            }
        }

        // Dispatch retains the occurrence and custody roster; AmbientWorld owns
        // physical scene tasks. Sampling is bounded to this roster at 750 ms.
        internal bool PrepareDispatchActivityResources(LSPDDispatchEvent incident)
        {
            try { return PrepareDispatchActivityResourcesCore(incident); }
            catch (Exception ex)
            {
                Log("AMBIENT_WORLD_DISPATCH_RESOURCE_PENDING", "Occurrence=" + (incident == null ? "" : incident.Id)
                    + "; NativeResourceFailure=" + ex.GetType().Name);
                return false;
            }
        }

        private bool PrepareDispatchActivityResourcesCore(LSPDDispatchEvent incident)
        {
            if (incident == null || string.IsNullOrEmpty(incident.PreparedActivity)) return true;
            string action = incident.PreparedActivity;
            if ((action == "ramming" || action == "driver_assault") && !PrepareDispatchRammingTarget(incident)) return false;
            if ((action == "exchange" || action == "theft") && !PrepareDispatchExchangeProp(incident)) return false;
            if (action == "exchange")
                return LSImmersiveDictionaryAnimation.RequestDispatchAction("exchange_giver")
                    & LSImmersiveDictionaryAnimation.RequestDispatchAction("exchange_receiver");
            if (action == "theft") return LSImmersiveDictionaryAnimation.RequestDispatchAction("exchange_giver");
            if (new[] { "camera", "intoxicated", "lying" }.Contains(action))
                return LSImmersiveDictionaryAnimation.RequestDispatchAction(action);
            return LSImmersiveDictionaryAnimation.RequestDispatchAction("conversation")
                & LSImmersiveDictionaryAnimation.RequestDispatchAction("cower");
        }

        internal bool MaintainPreparedDispatchActivity(LSPDDispatchEvent incident, DateTime now, out string reason)
        {
            try { return MaintainPreparedDispatchActivityCore(incident, now, out reason); }
            catch (Exception ex)
            {
                reason = "FAILED:Activity native failure: " + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_ACTIVITY_FAILED", "Occurrence=" + (incident == null ? "" : incident.Id) + "; " + reason);
                return false;
            }
        }

        private bool MaintainPreparedDispatchActivityCore(LSPDDispatchEvent incident, DateTime now, out string reason)
        {
            reason = string.Empty;
            if (incident == null || string.IsNullOrEmpty(incident.PreparedActivity)) return true;
            if (!incident.OwnedByDispatch || incident.HasConvoyCustodyHandoff) return true;
            if (now < incident.PreparedActivityNextSampleAt) return incident.PreparedActivityConfirmed;
            incident.PreparedActivityNextSampleAt = now.AddMilliseconds(750);
            List<Ped> suspects = incident.Suspects.Where(p => p != null && p.Exists())
                .Concat(new[] { incident.Suspect }).Where(p => p != null && p.Exists())
                .GroupBy(p => p.Handle).Select(g => g.First()).ToList();
            if (!incident.PreparedActivityStarted)
            {
                if (suspects.Count != incident.SuspectCount || suspects.Any(p => p.IsDead))
                { reason = "Required suspect roster unavailable"; return false; }
                if (!PrepareDispatchActivityResources(incident))
                { reason = "Animation dictionary/clip is unavailable or still streaming"; return false; }
                incident.PreparedActivityStarted = true;
                incident.PreparedActivityStartedAt = now;
                incident.PreparedActivitySamples = 0;
            }
            bool encountered = incident.PlayerEncounterResponseStarted || incident.SuspectResponses.Count > 0
                || incident.SurrenderRequested || incident.PlayerHandcuffInProgress;
            bool allActive = suspects.Count == incident.SuspectCount - incident.ResolvedSuspectHandles.Count;
            var civilians = incident.AdditionalParticipants.Where(p => p != null && p.Exists()).ToList();
            if (incident.Victim != null && incident.Victim.Exists()) civilians.Insert(0, incident.Victim);
            allActive &= civilians.Count == incident.SceneParticipantCount + incident.VictimCount;
            if (incident.PreparedActivity == "exchange" && !encountered && suspects.Count >= 2)
                AttachDispatchExchangeProp(incident,
                    ((int)(now - incident.PreparedActivityStartedAt).TotalSeconds % 8) < 4 ? suspects[0] : suspects[1]);
            // Civilian roles remain independent of criminal response decisions.
            for (int i = 0; i < civilians.Count; i++)
            {
                Ped civilian = civilians[i];
                if (civilian.IsDead) { allActive &= incident.PreparedActivityConfirmed || (incident.VictimStartsDead && (civilian == incident.Victim
                    || incident.AdditionalParticipants.IndexOf(civilian) < incident.AdditionalDeadVictims)); continue; }
                string role = CivilianActivityRole(incident, i);
                allActive &= MaintainDispatchRole(incident, civilian, role, suspects.FirstOrDefault(), now);
            }
            for (int i = 0; i < suspects.Count; i++)
            {
                Ped actor = suspects[i];
                if (encountered || incident.ConvoyCustodySuspectHandles.Contains(actor.Handle) || incident.ArrestedSuspects.Any(p => p != null && p.Handle == actor.Handle)
                    || incident.CompliantSuspects.Any(p => p != null && p.Handle == actor.Handle)
                    || actor.IsDead || actor.Health < actor.MaxHealth - 15
                    || Function.Call<bool>(Hash.IS_PED_RAGDOLL, actor))
                    continue; // Injury, arrest, Backup and encounter tasks keep priority.
                string role = SuspectActivityRole(incident, i);
                if (role == "ramming" && incident.RammingImpactConfirmed && !actor.IsInVehicle()) role = "lookout";
                if (incident.Victim != null && incident.Victim.Exists() && incident.Victim.IsDead
                    && (role == "assault" || role == "shooting" || role == "driver_assault")) role = "lookout";
                allActive &= MaintainDispatchRole(incident, actor, role,
                    role == "exchange_giver" ? suspects.ElementAtOrDefault(1)
                    : role == "exchange_receiver" ? suspects[0] : incident.Victim, now);
            }
            if (encountered) return incident.PreparedActivityConfirmed;
            incident.PreparedActivitySamples++;
            if (allActive && incident.PreparedActivitySamples >= 2)
            {
                if (!incident.PreparedActivityConfirmed)
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVITY_CONFIRMED", "Occurrence=" + incident.Id
                        + "; Activity=" + incident.PreparedActivity + "; Evidence="
                        + string.Join(",", incident.ActivityEvidenceHandles) + "; Owner=AmbientWorld");
                incident.PreparedActivityConfirmed = true;
                return true;
            }
            reason = "Awaiting physical activity: " + incident.PreparedActivity + "; confirmed actors="
                + incident.ActivityEvidenceHandles.Count;
            // Only bounded, targeted recovery. Never repeatedly reset progressing tasks.
            if (!encountered && now > incident.PreparedActivityStartedAt.AddSeconds(12))
            {
                if (incident.PreparedActivityRecoveryCount < 2)
                {
                    incident.PreparedActivityRecoveryCount++;
                    incident.PreparedActivityStartedAt = now;
                    incident.PreparedActivityConfirmed = false;
                    foreach (int handle in incident.ActivityTaskIssuedAt.Keys.ToArray())
                    {
                        DateTime last;
                        if (!incident.ActivityLastEvidenceAt.TryGetValue(handle, out last) || now > last.AddSeconds(5))
                        {
                            incident.ActivityTaskIssuedAt.Remove(handle);
                            incident.ActivityEvidenceHandles.Remove(handle);
                        }
                    }
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVITY_RETRY", "Occurrence=" + incident.Id
                        + "; Attempt=" + incident.PreparedActivityRecoveryCount + "; " + reason);
                }
                else
                {
                    incident.PreparedActivityConfirmed = false;
                    reason = "FAILED:" + reason;
                }
            }
            return incident.PreparedActivityConfirmed;
        }

        private static string SuspectActivityRole(LSPDDispatchEvent incident, int index)
        {
            string action = incident.PreparedActivity;
            if (action.StartsWith("driving_") || action == "ramming") return action;
            if (action == "exchange") return index == 0 ? "exchange_giver" : index == 1
                ? "exchange_receiver" : index % 2 == 0 ? "lookout" : "conversation";
            if (action == "gathering") return index % 3 == 0 ? "conversation" : "lookout";
            if (action == "murder") return index % 2 == 0 ? "lookout" : "conversation";
            if (index > 1) return index % 2 == 0 ? "lookout" : "conversation";
            if (index == 1 && action != "assault" && action != "shooting") return "lookout";
            return action;
        }

        private static string CivilianActivityRole(LSPDDispatchEvent incident, int index)
        {
            string action = incident.PreparedActivity;
            if (index == 0 && (action == "stalking" || action == "theft")) return "walk";
            if (action.StartsWith("driving_") || action == "ramming") return "witness";
            if (action == "gathering" || action == "exchange" || action == "intoxicated" || action == "lying")
                return index % 2 == 0 ? "witness" : "walk";
            return index % 3 == 0 ? "cower" : index % 3 == 1 ? "witness" : "retreat";
        }

        private bool MaintainDispatchRole(LSPDDispatchEvent incident, Ped actor, string role, Ped target, DateTime now)
        {
            if (actor == null || !actor.Exists() || actor.IsDead) return false;
            actor.IsPositionFrozen = false;
            string previousRole;
            DateTime issuedAt;
            bool issued = incident.ActivityTaskIssuedAt.TryGetValue(actor.Handle, out issuedAt)
                && incident.ActivityRoles.TryGetValue(actor.Handle, out previousRole) && previousRole == role;
            Vector3 previous;
            bool moved = incident.ActivitySamplePositions.TryGetValue(actor.Handle, out previous)
                && actor.Position.DistanceTo(previous) > 0.25f;
            incident.ActivitySamplePositions[actor.Handle] = actor.Position;
            bool active = false;
            if (role.StartsWith("driving_") || role == "ramming")
                active = MaintainDispatchDriving(incident, actor, role, issued, moved);
            else if ((role == "threat" || role == "carjacking") && incident.Armed
                && target != null && target.Exists() && !target.IsDead)
            {
                // Script-task hashes are joaat(SCRIPT_TASK_*), not native hashes.
                int status = Function.Call<int>(Hash.GET_SCRIPT_TASK_STATUS, actor, 0x6134071B);
                active = issued && (status == 0 || status == 1)
                    && Function.Call<bool>(Hash.IS_PED_ARMED, actor, 4);
                if (!issued || (!active && now > issuedAt.AddSeconds(6)))
                {
                    Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, actor, target, -1, true);
                    issuedAt = now;
                }
            }
            else if (role == "driver_assault" && target != null && target.Exists()
                && incident.VictimVehicle != null && incident.VictimVehicle.Exists()
                && target.Position.DistanceTo(incident.VictimVehicle.Position) > 5f)
            {
                if (!issued)
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY, target, incident.VictimVehicle, -1, 3f, 1f, 0f, 0);
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, actor, target, 0f, -1f, 0f, 1.4f, -1, 1f, true);
                }
                active = moved;
            }
            else if (role == "assault" || role == "driver_assault" || role == "shooting")
            {
                if (target == null || !target.Exists() || target.IsDead) return false;
                active = actor.IsInCombatAgainst(target) && (role != "shooting" || actor.IsShooting
                    || incident.ActivityEvidenceHandles.Contains(actor.Handle));
                if (!issued || (role == "driver_assault" && !actor.IsInCombatAgainst(target))) actor.Task.Combat(target);
            }
            else if (role == "stalking" || role == "theft")
            {
                if (target == null || !target.Exists() || target.IsDead) return false;
                int stage;
                incident.ActivityStages.TryGetValue(actor.Handle, out stage);
                if (role == "theft" && stage == 2)
                {
                    active = moved;
                    if (!issued)
                    {
                        Vector3 center = incident.Origin;
                        Function.Call(Hash.TASK_WANDER_IN_AREA, actor, center.X, center.Y, center.Z, 7f, 2, 3);
                    }
                }
                else if (role == "theft" && stage == 1)
                {
                    active = LSImmersiveDictionaryAnimation.IsDispatchActionPlaying(actor, "exchange_giver");
                    if (now > issuedAt.AddSeconds(2))
                    {
                        incident.ActivityStages[actor.Handle] = 2;
                        incident.ActivityTaskIssuedAt.Remove(actor.Handle);
                        return active;
                    }
                }
                else
                {
                    active = moved && actor.Position.DistanceTo(target.Position) < 30f;
                    if (!issued) Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, actor, target,
                        0f, role == "theft" ? -0.6f : -3f, 0f, role == "theft" ? 1.8f : 1.1f, -1, 0.6f, true);
                    if (role == "theft" && actor.Position.DistanceTo(target.Position) < 1.6f)
                    {
                        actor.Task.ClearAll();
                        if (!LSImmersiveDictionaryAnimation.TryPlayDispatchAction(actor, "exchange_giver", false)) return false;
                        AttachDispatchExchangeProp(incident, actor);
                        incident.ActivityStages[actor.Handle] = 1;
                        issuedAt = now;
                    }
                }
            }
            else if (role == "walk" || role == "lookout" || role == "retreat")
            {
                active = moved;
                if (!issued || (!moved && now > issuedAt.AddSeconds(7)))
                {
                    Vector3 point = incident.Origin;
                    if (role == "retreat" && target != null && target.Exists()
                        && !incident.SceneResolution.IsInteriorScene) actor.Task.ReactAndFlee(target);
                    else Function.Call(Hash.TASK_WANDER_IN_AREA, actor, point.X, point.Y, point.Z,
                        incident.SceneResolution.IsInteriorScene ? 2.5f : 7f, 2, 3);
                    issuedAt = now;
                }
            }
            else
            {
                string action = role == "witness" ? "conversation" : role == "carjacking" ? "threat" : role;
                active = LSImmersiveDictionaryAnimation.IsDispatchActionPlaying(actor, action);
                // Exchange clips alternate naturally with watch/conversation time.
                bool shortClip = action.StartsWith("exchange_");
                if (!issued || (!active && now > issuedAt.AddSeconds(shortClip ? 4 : 6)))
                {
                    if (target != null && target.Exists()) actor.Task.LookAt(target, 3000);
                    if (action == "camera" && !EnsureDispatchCamera(incident, actor)) return false;
                    if (!LSImmersiveDictionaryAnimation.TryPlayDispatchAction(actor, action, !shortClip)) return false;
                    issuedAt = now;
                }
            }
            if (!issued) issuedAt = now;
            incident.ActivityTaskIssuedAt[actor.Handle] = issuedAt;
            incident.ActivityRoles[actor.Handle] = role;
            if (active)
            {
                incident.ActivityEvidenceHandles.Add(actor.Handle);
                incident.ActivityLastEvidenceAt[actor.Handle] = now;
            }
            DateTime lastEvidence;
            return active || (incident.ActivityLastEvidenceAt.TryGetValue(actor.Handle, out lastEvidence)
                && now < lastEvidence.AddSeconds(5));
        }

        private bool MaintainDispatchDriving(LSPDDispatchEvent incident, Ped actor, string role, bool issued, bool moved)
        {
            Vehicle vehicle = incident.VehicleForSuspect(actor);
            if (vehicle == null || !vehicle.Exists() || !actor.IsInVehicle(vehicle)) return false;
            if (role == "ramming" && incident.VictimVehicle != null && incident.VictimVehicle.Exists()
                && !incident.RammingImpactConfirmed
                && vehicle.Position.DistanceTo(incident.VictimVehicle.Position) < 10f
                && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, incident.VictimVehicle, vehicle, true))
            {
                incident.RammingImpactConfirmed = true;
                Log("AMBIENT_WORLD_DISPATCH_RAMMING_IMPACT_CONFIRMED", "Occurrence=" + incident.Id
                    + "; Vehicle=" + vehicle.Handle + "; Target=" + incident.VictimVehicle.Handle
                    + "; Native=HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY; TaskAssignmentAlone=false");
            }
            if (role == "ramming" && incident.RammingImpactConfirmed)
            {
                if (!Function.Call<bool>(Hash.IS_VEHICLE_DRIVEABLE, vehicle, false)) actor.Task.LeaveVehicle();
                return true;
            }
            if (!Function.Call<bool>(Hash.IS_VEHICLE_DRIVEABLE, vehicle, false)) return false;
            int driver = Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, -1, false);
            if (driver != actor.Handle)
                return driver != 0 && moved && vehicle.Speed > 1f; // Actual occupied passenger role.
            if (!issued)
            {
                Vector3 road = World.GetNextPositionOnStreet(vehicle.Position);
                if (road == Vector3.Zero || road.DistanceTo(vehicle.Position) > 40f) return false;
                vehicle.IsPositionFrozen = false;
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, vehicle, true, true, false);
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, false);
                Function.Call(Hash.SET_VEHICLE_BRAKE, vehicle, false);
                float speed = role == "driving_slow" ? 3.5f : 28f;
                if (role == "ramming" && incident.VictimVehicle != null && incident.VictimVehicle.Exists())
                    Function.Call(Hash.TASK_VEHICLE_MISSION, actor, vehicle, incident.VictimVehicle,
                        2, 20f, 786468, 2f, 1f, true); // MISSION_RAM targets an actual catalog vehicle.
                else Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, actor, vehicle, speed,
                    role == "driving_slow" ? 786603 : 786468);
            }
            // A task assignment or a running engine is never movement evidence.
            return role != "ramming" && moved && vehicle.Speed > (role == "driving_slow" ? 0.8f : 5f);
        }

        private bool PrepareDispatchRammingTarget(LSPDDispatchEvent incident)
        {
            if (incident.VictimVehicle != null && incident.VictimVehicle.Exists()) return true;
            Vector3 position;
            float heading;
            string reason;
            if (!TryResolveAdditionalDispatchVehicleStaging(incident.SceneResolution, out position, out heading, out reason))
                return false;
            string name = (incident.VehicleModelCandidates ?? new List<string>()).LastOrDefault();
            if (string.IsNullOrEmpty(name)) name = incident.VehicleModel;
            var model = new Model(name);
            if (!model.IsValid || !model.IsInCdImage) return false;
            model.Request();
            if (!model.IsLoaded) return false;
            Vehicle target = World.CreateVehicle(model, position, heading);
            model.MarkAsNoLongerNeeded();
            if (target == null || !target.Exists()) return false;
            target.IsPersistent = true;
            target.IsPositionFrozen = false;
            incident.VictimVehicle = target;
            return true;
        }

        private bool PrepareDispatchExchangeProp(LSPDDispatchEvent incident)
        {
            if (incident.ActivityProps.Any(p => p != null && p.Exists())) return true;
            Model model;
            if (incident.IncidentType == "weapons_incident")
            {
                int weapon = unchecked((int)StringHash.AtStringHash(incident.WeaponName, 0));
                model = new Model(Function.Call<int>(Hash.GET_WEAPONTYPE_MODEL, weapon));
            }
            else model = new Model(incident.PreparedActivity == "theft" ? "prop_ld_wallet_01" : "prop_cash_pile_01");
            if (!model.IsValid || !model.IsInCdImage) return false;
            model.Request();
            if (!model.IsLoaded) return false;
            Prop prop = World.CreateProp(model, incident.Suspect.Position, false, false);
            model.MarkAsNoLongerNeeded();
            if (prop == null || !prop.Exists()) return false;
            Function.Call(Hash.SET_ENTITY_VISIBLE, prop, false, false);
            incident.ActivityProps.Add(prop);
            return true;
        }

        private void AttachDispatchExchangeProp(LSPDDispatchEvent incident, Ped actor)
        {
            Prop prop = incident.ActivityProps.FirstOrDefault(p => p != null && p.Exists());
            if (prop == null || actor == null || !actor.Exists()) return;
            Function.Call(Hash.SET_ENTITY_VISIBLE, prop, true, false);
            if (Function.Call<bool>(Hash.IS_ENTITY_ATTACHED_TO_ENTITY, prop, actor)) return;
            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, prop, actor,
                Function.Call<int>(Hash.GET_PED_BONE_INDEX, actor, 28422),
                0.05f, 0f, 0f, 0f, 0f, 0f, false, false, false, false, 2, true);
        }

        private bool EnsureDispatchCamera(LSPDDispatchEvent incident, Ped actor)
        {
            if (incident.ActivityProps.Any(p => p != null && p.Exists())) return true;
            var model = new Model("prop_pap_camera_01");
            if (!model.IsValid || !model.IsInCdImage) return false;
            model.Request();
            if (!model.IsLoaded) return false;
            Prop prop = World.CreateProp(model, actor.Position, false, false);
            model.MarkAsNoLongerNeeded();
            if (prop == null || !prop.Exists()) return false;
            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, prop, actor,
                Function.Call<int>(Hash.GET_PED_BONE_INDEX, actor, 28422),
                0f, 0f, 0f, 0f, 0f, 0f, false, false, false, false, 2, true);
            incident.ActivityProps.Add(prop);
            return true;
        }

        internal void ReleasePreparedDispatchActivity(LSPDDispatchEvent incident)
        {
            if (incident == null) return;
            foreach (Prop prop in incident.ActivityProps)
                if (prop != null && prop.Exists())
                {
                    Function.Call(Hash.DETACH_ENTITY, prop, true, true);
                    prop.MarkAsNoLongerNeeded();
                }
            incident.ActivityProps.Clear();
            if (incident.PreparedActivityStarted && incident.OwnedByDispatch)
                foreach (Ped actor in incident.Suspects.Where(p => p != null && p.Exists() && !p.IsDead
                    && !incident.ConvoyCustodySuspectHandles.Contains(p.Handle)
                    && !incident.ArrestedSuspects.Any(a => a != null && a.Handle == p.Handle)))
                {
                    actor.IsPositionFrozen = false;
                    actor.BlockPermanentEvents = false;
                    Function.Call(Hash.TASK_WANDER_STANDARD, actor, 10f, 10);
                }
        }

        internal bool TryStartDispatchPreArrivalActivity(
            LSPDDispatchEvent incident,
            Ped suspect,
            string activity,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || suspect == null || !suspect.Exists() || suspect.IsDead
                || string.IsNullOrWhiteSpace(activity))
            {
                reason = "The Dispatch actor or authored pre-arrival activity is unavailable.";
                return false;
            }

            string normalized = activity.Trim().ToLowerInvariant();
            try
            {
                if (normalized == "active_shooter")
                    return TryStartDispatchActiveShooterActivity(incident, suspect, out reason);
                if (normalized == "group_shootout")
                    return TryStartDispatchGroupShootoutActivity(incident, out reason);
                if (suspect.IsInVehicle() && normalized != "victim_threat")
                    return true;

                Ped victim = incident.Victim;
                if (normalized == "guard")
                {
                    suspect.BlockPermanentEvents = true;
                    Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE,
                        suspect, "WORLD_HUMAN_GUARD_STAND", 0, true);
                    Log("AMBIENT_WORLD_DISPATCH_PREARRIVAL_ACTIVITY_STARTED",
                        "Occurrence=" + incident.Id + "; Activity=guard; Suspect="
                        + suspect.Handle + "; Task=TASK_START_SCENARIO_IN_PLACE; Owner=AmbientWorld");
                    return true;
                }
                if (normalized == "murder_scene")
                {
                    if (victim == null || !victim.Exists() || !victim.IsDead)
                    {
                        reason = "The authored dead victim is unavailable for the murder-scene activity.";
                        return false;
                    }
                    suspect.BlockPermanentEvents = true;
                    Vector3 position = victim.Position;
                    Function.Call(Hash.TASK_WANDER_IN_AREA, suspect,
                        position.X, position.Y, position.Z, 2.25f, 3, 6);
                    Log("AMBIENT_WORLD_DISPATCH_PREARRIVAL_ACTIVITY_STARTED",
                        "Occurrence=" + incident.Id + "; Activity=murder_scene; Suspect="
                        + suspect.Handle + "; Task=TASK_WANDER_IN_AREA; Owner=AmbientWorld");
                    return true;
                }
                if (normalized == "victim_threat")
                {
                    if (victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0)
                    {
                        reason = "The authored living victim is unavailable for the threat activity.";
                        return false;
                    }
                    if (incident.SceneResolution != null
                        && incident.SceneResolution.IsMappedInteriorDispatchScene)
                    {
                        int suspectInterior;
                        int victimInterior;
                        if (!TryGetActorInteriorId(suspect, out suspectInterior)
                            || !TryGetActorInteriorId(victim, out victimInterior)
                            || suspectInterior != incident.SceneResolution.LiveInteriorId
                            || victimInterior != incident.SceneResolution.LiveInteriorId)
                        {
                            reason = "The suspect and victim are not both inside the validated incident interior.";
                            return false;
                        }
                    }
                    suspect.BlockPermanentEvents = true;
                    victim.BlockPermanentEvents = true;
                    if (!incident.PreArrivalVictimThreatStarted)
                    {
                        victim.Task.HandsUp(60000);
                        incident.PreArrivalVictimThreatStarted = true;
                    }
                    Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, suspect, victim, -1, true);
                    Log("AMBIENT_WORLD_DISPATCH_PREARRIVAL_ACTIVITY_STARTED",
                        "Occurrence=" + incident.Id + "; Activity=victim_threat; Suspect="
                        + suspect.Handle + "; Victim=" + victim.Handle
                        + "; Tasks=TASK_HANDS_UP+TASK_AIM_GUN_AT_ENTITY; Owner=AmbientWorld");
                    return true;
                }
                if (normalized == "victim_attack")
                {
                    if (victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0
                        || incident.SceneResolution == null
                        || !incident.SceneResolution.IsMappedInteriorDispatchScene)
                    {
                        reason = "The authored mapped-interior living victim is unavailable.";
                        return false;
                    }
                    int suspectInterior;
                    int victimInterior;
                    if (!TryGetActorInteriorId(suspect, out suspectInterior)
                        || !TryGetActorInteriorId(victim, out victimInterior)
                        || suspectInterior != incident.SceneResolution.LiveInteriorId
                        || victimInterior != incident.SceneResolution.LiveInteriorId)
                    {
                        reason = "The suspect and victim are not both inside the validated incident interior.";
                        return false;
                    }
                    suspect.BlockPermanentEvents = true;
                    victim.BlockPermanentEvents = true;
                    TryStartDispatchVictimCower(incident, victim);
                    suspect.Task.Combat(victim);
                    Log("AMBIENT_WORLD_DISPATCH_PREARRIVAL_CLERK_ASSAULT_STARTED",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Victim=" + victim.Handle + "; Task=TASK_COMBAT_PED"
                        + "; WeaponRequirement=false; Owner=AmbientWorld; TerminalOutcome=false");
                    return true;
                }
                if (normalized == "disturbance"
                    && !string.IsNullOrWhiteSpace(incident.DisturbanceVariant))
                    return TryStartDispatchDisturbanceActivity(
                        incident, suspect, out reason);
                if (normalized == "disturbance")
                {
                    suspect.BlockPermanentEvents = true;
                    if (incident.SceneResolution != null
                        && (incident.SceneResolution.IsInteriorScene
                            || (string.Equals(incident.Id, "disturbance",
                                    StringComparison.OrdinalIgnoreCase)
                                && string.Equals(incident.CriminalDisposition, "reluctant",
                                    StringComparison.OrdinalIgnoreCase))))
                        Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE,
                            suspect, "WORLD_HUMAN_STAND_IMPATIENT", 0, true);
                    else
                    {
                        Vector3 center = suspect.Position;
                        Function.Call(Hash.TASK_WANDER_IN_AREA, suspect,
                            center.X, center.Y, center.Z, 3.0f, 3, 6);
                    }
                    Log("AMBIENT_WORLD_DISPATCH_PREARRIVAL_ACTIVITY_STARTED",
                        "Occurrence=" + incident.Id + "; Activity=disturbance; Suspect="
                        + suspect.Handle + "; Owner=AmbientWorld");
                    return true;
                }

                reason = "The activity is not an AmbientWorld-authored pre-arrival task: " + normalized;
                return false;
            }
            catch (Exception ex)
            {
                reason = "AmbientWorld could not start the authored scene activity: "
                    + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_PREARRIVAL_ACTIVITY_FAILED",
                    "Occurrence=" + incident.Id + "; Activity=" + normalized
                    + "; Suspect=" + suspect.Handle + "; Reason=" + ex.GetType().Name);
                return false;
            }
        }

        internal bool TryStartDispatchGroupShootoutActivity(
            LSPDDispatchEvent incident,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || !string.Equals(incident.PreArrivalActivity, "group_shootout",
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "The occurrence is not an authored group-shootout scene.";
                return false;
            }
            if (incident.PreArrivalVictimThreatStarted)
                return incident.ActiveShooterActivityStartedAt != DateTime.MinValue;

            Ped victim = incident.Victim;
            List<Ped> suspects = (incident.Suspects ?? new List<Ped>())
                .Concat(new[] { incident.Suspect })
                .Where(value => value != null && value.Exists() && !value.IsDead
                    && value.Health > 0 && !value.IsInVehicle())
                .GroupBy(value => value.Handle)
                .Select(group => group.First())
                .Where(value => incident.ArrestedSuspects == null
                    || !incident.ArrestedSuspects.Any(arrested => arrested != null
                        && arrested.Exists() && arrested.Handle == value.Handle))
                .OrderBy(value => incident.Suspect != null
                    && value.Handle == incident.Suspect.Handle ? 0 : 1)
                .ToList();
            if (victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0
                || suspects.Count < 2)
            {
                reason = "The living victim and at least two available suspects are required.";
                Log("AMBIENT_WORLD_DISPATCH_GROUP_SHOOTOUT_PREFLIGHT_FAILED",
                    "Occurrence=" + incident.Id + "; Victim="
                    + (victim == null || !victim.Exists() ? 0 : victim.Handle)
                    + "; LivingSuspects=" + suspects.Count
                    + "; Result=RequiredSceneRoleMissing; Owner=AmbientWorld");
                return false;
            }

            try
            {
                TryStartDispatchVictimCower(incident, victim);
                foreach (Ped suspect in suspects)
                {
                    suspect.BlockPermanentEvents = true;
                    if (suspect.Handle == suspects[0].Handle)
                        continue;
                    Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY,
                        suspect, victim, -1, true);
                }
                if (!TryStartDispatchActiveShooterActivity(
                    incident, suspects[0], out reason))
                    return false;

                incident.PreArrivalVictimThreatStarted = true;
                Log("AMBIENT_WORLD_DISPATCH_GROUP_SHOOTOUT_ACTIVITY_STARTED",
                    "Occurrence=" + incident.Id + "; Suspects=" + string.Join(",",
                        suspects.Select(value => value.Handle.ToString(CultureInfo.InvariantCulture)))
                    + "; Shooter=" + suspects[0].Handle + "; Victim=" + victim.Handle
                    + "; VictimTask=TASK_COWER; OtherSuspectTask=TASK_AIM_GUN_AT_ENTITY"
                    + "; ShooterActivity=RealFirearm; Confirmation=IS_PED_SHOOTING"
                    + "; SeparateAudioClip=false; Owner=AmbientWorld; TerminalOutcome=false");
                return true;
            }
            catch (Exception ex)
            {
                reason = "AmbientWorld could not start the group-shootout presentation: "
                    + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_GROUP_SHOOTOUT_START_FAILED",
                    "Occurrence=" + incident.Id + "; Reason=" + ex.GetType().Name);
                return false;
            }
        }

        internal bool TryStartDispatchVictimCower(LSPDDispatchEvent incident, Ped victim)
        {
            if (incident == null || !incident.OwnedByDispatch
                || victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0)
                return false;
            victim.BlockPermanentEvents = true;
            try
            {
                Function.Call((Hash)0x3EB1FE9E8E908E15UL, victim, -1);
                Log("AMBIENT_WORLD_DISPATCH_VICTIM_COWER_STARTED",
                    "Occurrence=" + incident.Id + "; Role=Victim; Victim=" + victim.Handle
                    + "; Native=TASK_COWER; Duration=-1; Owner=AmbientWorld");
                return true;
            }
            catch (Exception ex)
            {
                try { victim.Task.HandsUp(60000); } catch { }
                Log("AMBIENT_WORLD_DISPATCH_VICTIM_COWER_FALLBACK",
                    "Occurrence=" + incident.Id + "; Victim=" + victim.Handle
                    + "; Native=TASK_COWER; Result=Failed; Fallback=HandsUp"
                    + "; Exception=" + ex.GetType().Name + "; Owner=AmbientWorld");
                return false;
            }
        }

        internal bool TryApplyDispatchParticipantThreatPose(
            LSPDDispatchEvent incident,
            Ped participant,
            int durationMilliseconds,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || participant == null || !participant.Exists()
                || participant.IsDead || participant.Health <= 0)
            {
                reason = "The Dispatch participant is unavailable.";
                return false;
            }
            try
            {
                participant.BlockPermanentEvents = true;
                participant.Task.HandsUp(Math.Max(1000, durationMilliseconds));
                if (incident.SceneParticipantBehaviorInitializedHandles != null)
                    incident.SceneParticipantBehaviorInitializedHandles.Add(participant.Handle);
                Log("AMBIENT_WORLD_DISPATCH_PARTICIPANT_THREAT_POSE_STARTED",
                    "Occurrence=" + incident.Id + "; Handle=" + participant.Handle
                    + "; Task=TASK_HANDS_UP; Owner=AmbientWorld");
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_PARTICIPANT_THREAT_POSE_FAILED",
                    "Occurrence=" + incident.Id + "; Handle=" + participant.Handle
                    + "; Reason=" + reason + "; Owner=AmbientWorld");
                return false;
            }
        }

        internal void StartDispatchVictimInitialSceneActivity(
            LSPDDispatchEvent incident,
            Ped victim)
        {
            if (incident == null || !incident.OwnedByDispatch
                || victim == null || !victim.Exists() || victim.IsDead
                || victim.Health <= 0)
                return;
            try
            {
                Ped player = Game.Player == null ? null : Game.Player.Character;
                if (player != null && player.Exists() && !player.IsDead)
                    victim.Task.LookAt(player, 3000);
                string type = incident.IncidentType ?? string.Empty;
                if (string.Equals(type, "carjacking", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "bank_robbery", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "store_robbery", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "kidnapping", StringComparison.OrdinalIgnoreCase))
                    victim.Task.HandsUp(8000);
                Log("AMBIENT_WORLD_DISPATCH_VICTIM_INITIAL_ACTIVITY_STARTED",
                    "Occurrence=" + incident.Id + "; Victim=" + victim.Handle
                    + "; IncidentType=" + type + "; HandsUp="
                    + (string.Equals(type, "carjacking", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "bank_robbery", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "store_robbery", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "kidnapping", StringComparison.OrdinalIgnoreCase))
                    + "; Owner=AmbientWorld");
            }
            catch (Exception ex)
            {
                Log("AMBIENT_WORLD_DISPATCH_VICTIM_INITIAL_ACTIVITY_FAILED",
                    "Occurrence=" + incident.Id + "; Victim=" + victim.Handle
                    + "; Reason=" + ex.GetType().Name);
            }
        }

        internal bool TryStartDispatchSuspectVehicleExit(
            LSPDDispatchEvent incident,
            Ped suspect,
            Vehicle vehicle,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || suspect == null || !suspect.Exists() || suspect.IsDead
                || vehicle == null || !vehicle.Exists()
                || !suspect.IsInVehicle() || suspect.CurrentVehicle == null
                || !suspect.CurrentVehicle.Exists()
                || suspect.CurrentVehicle.Handle != vehicle.Handle)
            {
                reason = "The authored suspect or escape vehicle is unavailable for scene exit.";
                return false;
            }
            try
            {
                Function.Call(Hash.TASK_LEAVE_VEHICLE, suspect, vehicle, 0);
                Log("AMBIENT_WORLD_DISPATCH_SUSPECT_VEHICLE_EXIT_STARTED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Vehicle=" + vehicle.Handle + "; Task=TASK_LEAVE_VEHICLE"
                    + "; Owner=AmbientWorld; Teleport=false");
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_SUSPECT_VEHICLE_EXIT_FAILED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Vehicle=" + vehicle.Handle + "; Reason=" + reason);
                return false;
            }
        }

        internal bool TryStartDispatchRobberyActivity(
            LSPDDispatchEvent incident,
            bool threatPose,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff)
            {
                reason = "The robbery occurrence is unavailable.";
                return false;
            }
            try
            {
                List<Ped> suspects = (incident.Suspects ?? new List<Ped>())
                    .Concat(new[] { incident.Suspect })
                    .Where(value => value != null && value.Exists() && !value.IsDead
                        && value.Health > 0 && !value.IsInVehicle())
                    .GroupBy(value => value.Handle)
                    .Select(group => group.First())
                    .ToList();
                if (suspects.Count == 0)
                {
                    reason = "No living on-foot suspect remains for the robbery scene.";
                    return false;
                }
                foreach (Ped suspect in suspects)
                {
                    suspect.BlockPermanentEvents = true;
                    if (threatPose)
                    {
                        if (incident.Victim == null || !incident.Victim.Exists()
                            || incident.Victim.IsDead || incident.Victim.Health <= 0)
                            continue;
                        Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY,
                            suspect, incident.Victim, -1, true);
                    }
                    else
                    {
                        int index = suspects.IndexOf(suspect);
                        float lateral = index == 0 ? 0f : index % 2 == 0 ? 2.2f : -2.2f;
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD,
                            suspect, incident.Origin.X + 1.5f,
                            incident.Origin.Y + lateral, incident.Origin.Z,
                            1.0f, -1, 0f, 0f);
                    }
                }
                Log("AMBIENT_WORLD_DISPATCH_ROBBERY_ACTIVITY_STARTED",
                    "Occurrence=" + incident.Id + "; Stage="
                    + (threatPose ? "VictimThreat" : "SceneMovement")
                    + "; Suspects=" + suspects.Count + "; Owner=AmbientWorld");
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_ROBBERY_ACTIVITY_FAILED",
                    "Occurrence=" + incident.Id + "; Stage="
                    + (threatPose ? "VictimThreat" : "SceneMovement")
                    + "; Reason=" + reason);
                return false;
            }
        }

        internal void StartDispatchPassiveSceneActivity(
            LSPDDispatchEvent incident,
            Ped suspect,
            string incidentType)
        {
            if (incident == null || !incident.OwnedByDispatch
                || suspect == null || !suspect.Exists() || suspect.IsDead)
                return;
            try
            {
                suspect.BlockPermanentEvents = true;
                suspect.Task.LookAt(Game.Player.Character, 2500);
                string type = (incidentType ?? string.Empty).ToLowerInvariant();
                string scenario = type == "disturbance"
                    ? "WORLD_HUMAN_STAND_IMPATIENT"
                    : type == "shots_fired" ? "WORLD_HUMAN_SMOKING" : null;
                if (!string.IsNullOrWhiteSpace(scenario))
                    Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE,
                        suspect, scenario, 0, true);
                Log("AMBIENT_WORLD_DISPATCH_PASSIVE_SCENE_ACTIVITY_STARTED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; IncidentType=" + type + "; Scenario="
                    + (scenario ?? "None") + "; Owner=AmbientWorld");
            }
            catch (Exception ex)
            {
                Log("AMBIENT_WORLD_DISPATCH_PASSIVE_SCENE_ACTIVITY_FAILED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Reason=" + ex.GetType().Name);
            }
        }

        internal bool TryStartDispatchActiveShooterActivity(
            LSPDDispatchEvent incident,
            Ped suspect,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || (!string.Equals(incident.PreArrivalActivity, "active_shooter",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(incident.PreArrivalActivity, "group_shootout",
                        StringComparison.OrdinalIgnoreCase))
                || suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.IsInVehicle())
            {
                reason = "The authored solo or group shooter occurrence is not available for physical staging.";
                return false;
            }
            if (incident.ActiveShooterActivityStartedAt != DateTime.MinValue)
                return true;

            string weaponName = incident.WeaponName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(weaponName)
                || (incident.WeaponNameCandidates != null
                    && incident.WeaponNameCandidates.Count > 0
                    && !incident.WeaponNameCandidates.Contains(
                        weaponName, StringComparer.OrdinalIgnoreCase))
                || !IsDispatchFirearmName(weaponName))
            {
                reason = "The selected criminal-profile asset is not an authored firearm: "
                    + (string.IsNullOrWhiteSpace(weaponName) ? "Unavailable" : weaponName);
                Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_WEAPON_REJECTED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; SelectedWeapon=" + weaponName
                    + "; WeaponCandidates=" + string.Join(",",
                        incident.WeaponNameCandidates ?? new List<string>())
                    + "; Result=Rejected; Owner=AmbientWorld");
                return false;
            }

            int weaponHash = unchecked((int)StringHash.AtStringHash(weaponName, 0));
            try
            {
                if (!Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON,
                    suspect, weaponHash, false))
                    Function.Call(Hash.GIVE_WEAPON_TO_PED,
                        suspect, weaponHash, 180, true, false);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON,
                    suspect, weaponHash, true);
                bool hasWeapon = Function.Call<bool>(
                    Hash.HAS_PED_GOT_WEAPON, suspect, weaponHash, false);
                int selectedWeapon = Function.Call<int>(
                    Hash.GET_SELECTED_PED_WEAPON, suspect);
                if (!hasWeapon || selectedWeapon != weaponHash)
                {
                    reason = "GTA did not confirm the selected firearm on the active-shooter suspect.";
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_WEAPON_UNCONFIRMED",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Weapon=" + weaponName + "; HasWeapon=" + hasWeapon
                        + "; SelectedWeaponHash=" + selectedWeapon
                        + "; ExpectedWeaponHash=" + weaponHash);
                    return false;
                }

                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                incident.ActiveShooterWeaponName = weaponName;
                incident.ActiveShooterActivityStartedAt = DateTime.UtcNow;
                incident.ActiveShooterNextShotAt = DateTime.UtcNow;
                incident.ActiveShooterLastShotAt = DateTime.MinValue;
                incident.ActiveShooterActivityStopped = false;
                Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_ACTIVITY_STARTED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Weapon=" + weaponName + "; HasWeapon=" + hasWeapon
                    + "; SelectedWeaponConfirmed=true; Task=TASK_SHOOT_AT_COORD"
                    + "; FiringPattern=FIRING_PATTERN_SINGLE_SHOT"
                    + "; RealWeaponAudio=true; SeparateAudioClip=false; Activity="
                    + incident.PreArrivalActivity + "; Owner=AmbientWorld");
                MaintainDispatchActiveShooterActivity(
                    incident, DateTime.UtcNow, false);
                return true;
            }
            catch (Exception ex)
            {
                reason = "The active-shooter weapon task could not be prepared: "
                    + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_START_FAILED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Weapon=" + weaponName + "; Reason=" + ex.GetType().Name);
                return false;
            }
        }

        internal void MaintainDispatchActiveShooterActivity(
            LSPDDispatchEvent incident,
            DateTime now,
            bool suspectWounded)
        {
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || incident.ActiveShooterActivityStartedAt == DateTime.MinValue
                || incident.ActiveShooterActivityStopped)
                return;

            Ped suspect = incident.Suspect;
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspectWounded || incident.PlayerEncounterResponseStarted
                || incident.SurrenderRequested || incident.State == LSPDDispatchState.Arrested
                || incident.State == LSPDDispatchState.AwaitingTransport
                || incident.State == LSPDDispatchState.HoldingAtStation
                || incident.State == LSPDDispatchState.PrisonTransfer)
            {
                incident.ActiveShooterActivityStopped = true;
                Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_ACTIVITY_STOPPED",
                    "Occurrence=" + incident.Id + "; Suspect="
                    + (suspect == null || !suspect.Exists() ? 0 : suspect.Handle)
                    + "; Reason=" + (suspectWounded ? "SuspectWounded"
                        : incident.PlayerEncounterResponseStarted ? "ResponseBranchStarted"
                        : incident.SurrenderRequested ? "SecureCommand"
                        : "IncidentOrActorStateChanged")
                    + "; ConfirmedShots=" + incident.ActiveShooterShotCount
                    + "; Owner=AmbientWorld");
                return;
            }

            if (incident.ActiveShooterFireTaskIssuedAt != DateTime.MinValue
                && !incident.ActiveShooterFireConfirmationLogged)
            {
                bool shooting = false;
                try { shooting = Function.Call<bool>(Hash.IS_PED_SHOOTING, suspect); }
                catch { }
                if (shooting)
                {
                    incident.ActiveShooterLastShotAt = now;
                    incident.ActiveShooterShotCount++;
                    incident.ActiveShooterFireConfirmationLogged = true;
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_FIRE_CONFIRMED",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Weapon=" + incident.ActiveShooterWeaponName
                        + "; Shot=" + incident.ActiveShooterShotCount
                        + "; Native=IS_PED_SHOOTING; Task=TASK_SHOOT_AT_COORD"
                        + "; SeparateAudioClip=false; Target="
                        + incident.ActiveShooterLastShotTarget);
                }
                else if (now >= incident.ActiveShooterFireTaskIssuedAt
                    .AddMilliseconds(1200))
                {
                    incident.ActiveShooterFireConfirmationLogged = true;
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_FIRE_UNCONFIRMED",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Weapon=" + incident.ActiveShooterWeaponName
                        + "; Native=IS_PED_SHOOTING; Result=NoShotObserved"
                        + "; NextAttempt=true");
                }
            }

            if (incident.ActiveShooterFireTaskIssuedAt == DateTime.MinValue
                && now >= incident.ActiveShooterNextShotAt)
                TryIssueDispatchActiveShooterShot(incident, suspect, now);
            else if (incident.ActiveShooterFireConfirmationLogged
                && now >= incident.ActiveShooterNextShotAt)
                TryIssueDispatchActiveShooterShot(incident, suspect, now);
        }

        private bool TryIssueDispatchActiveShooterShot(
            LSPDDispatchEvent incident,
            Ped suspect,
            DateTime now)
        {
            Vector3 shotTarget;
            if (!TryFindDispatchActiveShooterShotLane(
                incident, suspect, out shotTarget))
            {
                incident.ActiveShooterNextShotAt = now.AddSeconds(2);
                if (incident.ActiveShooterShotCount == 0)
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_LANE_UNAVAILABLE",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Result=NoSafeEnvironmentLane; RetrySeconds=2");
                return false;
            }

            int weaponHash = unchecked((int)StringHash.AtStringHash(
                incident.ActiveShooterWeaponName, 0));
            try
            {
                if (Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, suspect)
                    != weaponHash)
                    Function.Call(Hash.SET_CURRENT_PED_WEAPON,
                        suspect, weaponHash, true);
                if (!Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON,
                    suspect, weaponHash, false))
                {
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_WEAPON_LOST",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Weapon=" + incident.ActiveShooterWeaponName
                        + "; Result=ActivityStopped");
                    incident.ActiveShooterActivityStopped = true;
                    return false;
                }

                float angle = (float)Math.Atan2(
                    shotTarget.Y - suspect.Position.Y,
                    shotTarget.X - suspect.Position.X);
                suspect.Heading = angle * 180f / (float)Math.PI;
                int singleShotPattern = unchecked((int)StringHash.AtStringHash(
                    "FIRING_PATTERN_SINGLE_SHOT", 0));
                Function.Call(Hash.TASK_SHOOT_AT_COORD,
                    suspect, shotTarget.X, shotTarget.Y, shotTarget.Z,
                    850, singleShotPattern);
                incident.ActiveShooterLastShotTarget = shotTarget;
                incident.ActiveShooterFireTaskIssuedAt = now;
                incident.ActiveShooterFireConfirmationLogged = false;
                incident.ActiveShooterNextShotAt = now.AddSeconds(2.5f);
                incident.ActiveShooterDirectionIndex =
                    (incident.ActiveShooterDirectionIndex + 1) % 12;
                Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_FIRE_TASK_ISSUED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Weapon=" + incident.ActiveShooterWeaponName
                    + "; Task=TASK_SHOOT_AT_COORD; DurationMs=850"
                    + "; FiringPattern=FIRING_PATTERN_SINGLE_SHOT"
                    + "; Target=" + shotTarget
                    + "; Confirmation=AwaitingIS_PED_SHOOTING"
                    + "; SeparateAudioClip=false");
                return true;
            }
            catch (Exception ex)
            {
                incident.ActiveShooterNextShotAt = now.AddSeconds(2.5f);
                Log("AMBIENT_WORLD_DISPATCH_ACTIVE_SHOOTER_FIRE_TASK_FAILED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Weapon=" + incident.ActiveShooterWeaponName
                    + "; Reason=" + ex.GetType().Name);
                return false;
            }
        }

        private bool TryFindDispatchActiveShooterShotLane(
            LSPDDispatchEvent incident,
            Ped shooter,
            out Vector3 shotTarget)
        {
            shotTarget = Vector3.Zero;
            if (incident == null || shooter == null || !shooter.Exists())
                return false;

            Ped player = Game.Player == null ? null : Game.Player.Character;
            IEnumerable<Ped> protectedActors =
                (incident.Suspects ?? new List<Ped>())
                    .Concat(incident.AdditionalParticipants ?? new List<Ped>())
                    .Concat(new[] { incident.Victim, player })
                    .Where(value => value != null && value.Exists()
                        && value.Handle != shooter.Handle);
            Vector3 start = shooter.Position;
            for (int offset = 0; offset < 12; offset++)
            {
                int direction = (incident.ActiveShooterDirectionIndex + offset) % 12;
                float angle = (shooter.Heading + direction * 30f)
                    * (float)Math.PI / 180f;
                Vector3 candidate = new Vector3(
                    start.X + (float)Math.Cos(angle) * 18f,
                    start.Y + (float)Math.Sin(angle) * 18f,
                    start.Z + 0.25f);
                if (protectedActors.Any(actor =>
                    IsAmbientPositionNearHorizontalSegment(
                        actor.Position, start, candidate, 3f)))
                    continue;

                try
                {
                    Vector3 rayStart = start;
                    Vector3 rayEnd = candidate;
                    rayStart.Z += 0.85f;
                    rayEnd.Z += 0.85f;
                    RaycastResult obstruction = World.Raycast(
                        rayStart, rayEnd,
                        IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Glass,
                        null);
                    if (obstruction.DidHit)
                        continue;
                }
                catch
                {
                    continue;
                }

                shotTarget = candidate;
                return true;
            }
            return false;
        }

        private static bool IsAmbientPositionNearHorizontalSegment(
            Vector3 point,
            Vector3 start,
            Vector3 end,
            float clearance)
        {
            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.001f)
                return point.DistanceTo(start) < clearance;
            float projection = ((point.X - start.X) * dx
                + (point.Y - start.Y) * dy) / lengthSquared;
            projection = Math.Max(0f, Math.Min(1f, projection));
            float offsetX = point.X - (start.X + projection * dx);
            float offsetY = point.Y - (start.Y + projection * dy);
            return offsetX * offsetX + offsetY * offsetY
                < clearance * clearance;
        }

        private static bool IsDispatchFirearmName(string weaponName)
        {
            string normalized = weaponName ?? string.Empty;
            if (!normalized.StartsWith("WEAPON_", StringComparison.OrdinalIgnoreCase))
                return false;
            string[] nonFirearms =
            {
                "WEAPON_UNARMED", "WEAPON_KNIFE", "WEAPON_BAT",
                "WEAPON_CROWBAR", "WEAPON_HAMMER", "WEAPON_GOLFCLUB",
                "WEAPON_BOTTLE", "WEAPON_DAGGER", "WEAPON_MACHETE",
                "WEAPON_SWITCHBLADE", "WEAPON_KNUCKLE"
            };
            return !nonFirearms.Contains(normalized,
                StringComparer.OrdinalIgnoreCase);
        }

        internal bool TryPrepareDispatchDisturbanceAssets(
            LSPDDispatchEvent incident,
            out bool pending,
            out string reason)
        {
            pending = false;
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || incident.Suspect == null || !incident.Suspect.Exists()
                || incident.Victim == null || !incident.Victim.Exists()
                || incident.Victim.IsDead || incident.Victim.Health <= 0)
            {
                reason = "The Disturbance occurrence, suspect, or living victim is not ready for AmbientWorld asset staging.";
                return false;
            }

            string variant = incident.DisturbanceVariant ?? string.Empty;
            if (string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(variant, "beer_harassment", StringComparison.OrdinalIgnoreCase))
            {
                Ped suspect = incident.Suspect;
                string modelName = string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                    ? "prop_pap_camera_01" : "prop_beer_bottle";
                string dictionary = string.Equals(
                    variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                    ? "amb@world_human_paparazzi@male@base"
                    : "amb@world_human_drinking@beer@male@base";
                if (incident.DisturbanceProp != null && incident.DisturbanceProp.Exists())
                {
                    bool attached = Function.Call<bool>(
                        Hash.IS_ENTITY_ATTACHED_TO_ENTITY, incident.DisturbanceProp, suspect);
                    if (attached)
                        return TryPrepareDispatchDisturbanceAnimation(
                            incident, dictionary, "base", out pending, out reason);
                    CleanupDispatchDisturbanceProp(incident, "SuspectChangedDuringPreflight");
                }

                EnsureDispatchDisturbanceProp(incident, suspect, modelName,
                    string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                        ? 0.035f : 0.09f,
                    string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                        ? 0.025f : 0.02f,
                    string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                        ? 0.02f : -0.015f,
                    string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                        ? 92f : 105f,
                    string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                        ? 0f : 5f,
                    string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                        ? -90f : 0f,
                    DateTime.UtcNow);
                if (incident.DisturbanceProp != null && incident.DisturbanceProp.Exists()
                    && Function.Call<bool>(Hash.IS_ENTITY_ATTACHED_TO_ENTITY,
                        incident.DisturbanceProp, suspect))
                {
                    return TryPrepareDispatchDisturbanceAnimation(
                        incident, dictionary, "base", out pending, out reason);
                }
                if (incident.DisturbancePropFailureLogged)
                {
                    reason = "The authored hand prop could not be loaded or attached: " + modelName;
                    return false;
                }
                pending = true;
                reason = "Stage=DisturbanceProp; Model=" + modelName
                    + "; Attempts=" + incident.DisturbancePropLoadAttempts
                    + "; Result=StreamingOrAttachmentPending";
                return false;
            }

            if (IsDisturbanceWeaponThreat(variant))
            {
                Ped suspect = incident.Suspect;
                bool knifeThreat = string.Equals(variant, "knife_threat",
                    StringComparison.OrdinalIgnoreCase);
                string weaponName = knifeThreat
                    ? (incident.WeaponNameCandidates ?? new List<string>())
                        .FirstOrDefault(value => string.Equals(value,
                            "WEAPON_KNIFE", StringComparison.OrdinalIgnoreCase))
                    : incident.DisturbanceGunWeapon;
                if (string.IsNullOrWhiteSpace(weaponName))
                {
                    reason = knifeThreat
                        ? "The criminal-profile weapon candidates do not contain WEAPON_KNIFE."
                        : "The event catalog does not provide a firearm for armed_threat.";
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_WEAPON_REJECTED",
                        "Occurrence=" + incident.Id + "; Variant=" + variant
                        + "; CriminalProfile=" + incident.CriminalProfileId
                        + "; WeaponCandidates=" + string.Join(",",
                            incident.WeaponNameCandidates ?? new List<string>())
                        + "; CatalogGunWeapon=" + incident.DisturbanceGunWeapon
                        + "; Result=Rejected");
                    return false;
                }
                int weaponHash = unchecked((int)StringHash.AtStringHash(weaponName, 0));
                int unarmedHash = unchecked((int)StringHash.AtStringHash("WEAPON_UNARMED", 0));
                try
                {
                    if (!Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, suspect, weaponHash, false))
                        Function.Call(Hash.GIVE_WEAPON_TO_PED, suspect, weaponHash, 120, true, false);
                    Function.Call(Hash.SET_CURRENT_PED_WEAPON, suspect, unarmedHash, true);
                    bool hasWeapon = Function.Call<bool>(
                        Hash.HAS_PED_GOT_WEAPON, suspect, weaponHash, false);
                    int selectedWeapon = Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, suspect);
                    incident.DisturbanceWeaponName = weaponName;
                    incident.DisturbanceWeaponPrepared = hasWeapon && selectedWeapon != weaponHash;
                    if (incident.DisturbanceWeaponPrepared)
                    {
                        Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_WEAPON_PREPARED",
                            "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                            + "; Weapon=" + weaponName + "; Prepared=true"
                            + "; CatalogSource=" + (knifeThreat
                                ? "LSPDCriminalProfile.xml/WeaponNameCandidates"
                                : "LSPDDispatchEvent.xml/disturbanceGunWeapon")
                            + "; SelectedBeforeThreat=false; Owner=AmbientWorld");
                        return true;
                    }
                    reason = "Stage=DisturbanceWeapon; HasWeapon=" + hasWeapon
                        + "; SelectedBeforeThreat=" + (selectedWeapon == weaponHash)
                        + "; Result=Rejected";
                    return false;
                }
                catch (Exception ex)
                {
                    reason = "Stage=DisturbanceWeapon; Result=Exception:" + ex.GetType().Name;
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_WEAPON_PREPARATION_FAILED",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Reason=" + ex.GetType().Name);
                    return false;
                }
            }

            reason = "The Event owner selected an unsupported Disturbance asset variation: " + variant;
            return false;
        }

        private bool TryPrepareDispatchDisturbanceAnimation(
            LSPDDispatchEvent incident,
            string dictionary,
            string clip,
            out bool pending,
            out string reason)
        {
            pending = false;
            reason = string.Empty;
            if (incident == null || string.IsNullOrWhiteSpace(dictionary)
                || string.IsNullOrWhiteSpace(clip))
            {
                reason = "Stage=DisturbanceAnimation; Result=MissingAnimationReference";
                return false;
            }
            if (string.Equals(incident.DisturbanceAnimationDictionary, dictionary,
                    StringComparison.Ordinal)
                && string.Equals(incident.DisturbanceAnimationClip, clip,
                    StringComparison.Ordinal))
                return true;

            float duration;
            bool clipReady = LSImmersiveDictionaryAnimation.TryGetClipDuration(
                dictionary, clip, out duration);
            bool dictionaryLoaded = clipReady
                || LSImmersiveDictionaryAnimation.IsDictionaryLoaded(dictionary);
            if (!clipReady && dictionaryLoaded)
                clipReady = LSImmersiveDictionaryAnimation.TryGetClipDuration(
                    dictionary, clip, out duration);
            if (!clipReady)
            {
                pending = !dictionaryLoaded;
                reason = "Stage=DisturbanceAnimation; Dictionary=" + dictionary
                    + "; Clip=" + clip
                    + "; Loaded=" + dictionaryLoaded
                    + "; Result=" + (pending
                        ? "AnimationDictionaryStreamingPending"
                        : "AnimationClipUnavailable");
                if (!pending)
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ANIMATION_PREFLIGHT_FAILED",
                        "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                        + "; " + reason + "; ReadyBeforeOffer=false");
                return false;
            }

            incident.DisturbanceAnimationDictionary = dictionary;
            incident.DisturbanceAnimationClip = clip;
            Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ANIMATION_PREFLIGHTED",
                "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                + "; Dictionary=" + dictionary + "; Clip=" + clip
                + "; ClipDuration=" + duration.ToString("0.000", CultureInfo.InvariantCulture)
                + "; ReadyBeforeOffer=true; Owner=AmbientWorld");
            return true;
        }

        internal bool TryStartDispatchDisturbanceActivity(
            LSPDDispatchEvent incident,
            Ped suspect,
            out string reason)
        {
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || suspect == null || !suspect.Exists() || suspect.IsDead)
            {
                reason = "The Dispatch occurrence or suspect is no longer available for AmbientWorld staging.";
                return false;
            }
            if (incident.DisturbanceActivityStartedAt != DateTime.MinValue)
                return true;

            Ped victim = incident.Victim;
            if (victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0)
            {
                reason = "The authored living civilian victim is unavailable.";
                Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_PREFLIGHT_LOST",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Victim=" + (victim == null || !victim.Exists() ? 0 : victim.Handle)
                    + "; RequiredLivingVictim=true; TerminalOutcome=false");
                return false;
            }

            string variant = incident.DisturbanceVariant ?? string.Empty;
            if (string.IsNullOrWhiteSpace(variant)
                || incident.DisturbanceActivities == null
                || !incident.DisturbanceActivities.Contains(
                    variant, StringComparer.OrdinalIgnoreCase))
            {
                reason = "The event owner did not select an authored Disturbance activity before staging.";
                Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ACTIVITY_UNAVAILABLE",
                    "Occurrence=" + incident.Id + "; Variant=" + variant
                    + "; Reason=MissingOrUnauthoredActivity");
                return false;
            }

            bool propVariant = string.Equals(variant, "paparazzi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(variant, "beer_harassment", StringComparison.OrdinalIgnoreCase);
            if ((propVariant && (incident.DisturbanceProp == null
                    || !incident.DisturbanceProp.Exists()
                    || !Function.Call<bool>(Hash.IS_ENTITY_ATTACHED_TO_ENTITY,
                        incident.DisturbanceProp, suspect)
                    || string.IsNullOrWhiteSpace(incident.DisturbanceAnimationDictionary)
                    || string.IsNullOrWhiteSpace(incident.DisturbanceAnimationClip)))
                || (IsDisturbanceWeaponThreat(variant)
                    && !incident.DisturbanceWeaponPrepared))
            {
                reason = "AmbientWorld assets were not physically prepared before Dispatch offered this scene.";
                return false;
            }

            DateTime now = DateTime.UtcNow;
            incident.DisturbanceActivityStartedAt = now;
            incident.DisturbanceNextTaskAt = now.AddSeconds(
                IsDisturbanceWeaponThreat(variant) ? 1 : 4);
            suspect.BlockPermanentEvents = true;
            victim.BlockPermanentEvents = true;
            suspect.Task.ClearAll();
            victim.Task.ClearAll();
            try
            {
                if (IsDisturbanceWeaponThreat(variant))
                {
                    Vector3 center = incident.Origin;
                    Function.Call(Hash.TASK_WANDER_IN_AREA,
                        victim, center.X, center.Y, center.Z, 2.0f, 2, 5);
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                        suspect, victim, -1, -0.7f, -0.25f, 0f, 1.0f, -1, true);
                    suspect.CanSwitchWeapons = false;
                    int weaponHash = unchecked((int)StringHash.AtStringHash(
                        incident.DisturbanceWeaponName, 0));
                    Function.Call(Hash.SET_CURRENT_PED_WEAPON,
                        suspect, weaponHash, true);
                    if (string.Equals(variant, "knife_threat",
                        StringComparison.OrdinalIgnoreCase))
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            suspect, victim, 1000);
                    else
                        Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY,
                            suspect, victim, -1, true);
                    incident.DisturbanceWeaponDrawn =
                        Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, suspect)
                            == weaponHash;
                    incident.Armed = true;
                }
                else
                {
                    Vector3 center = incident.Origin;
                    Function.Call(Hash.TASK_WANDER_IN_AREA,
                        victim, center.X, center.Y, center.Z, 3.0f, 2, 5);
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                        suspect,
                        victim,
                        -1,
                        string.Equals(variant, "beer_harassment", StringComparison.OrdinalIgnoreCase)
                            ? -0.55f : -1.2f,
                        string.Equals(variant, "beer_harassment", StringComparison.OrdinalIgnoreCase)
                            ? 0.2f : -0.4f,
                        0f,
                        1.0f,
                        -1,
                        true);
                    StartDispatchDisturbanceAnimation(
                        incident,
                        suspect,
                        incident.DisturbanceAnimationDictionary,
                        incident.DisturbanceAnimationClip,
                        now);
                    incident.DisturbanceNextTaskAt = now.AddSeconds(1);
                }
            }
            catch (Exception ex)
            {
                reason = "Scene task setup failed: " + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ACTIVITY_TASK_FAILED",
                    "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                    + "; Victim=" + victim.Handle + "; Reason=" + ex.GetType().Name);
                return false;
            }

            Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ACTIVITY_STARTED",
                "Occurrence=" + incident.Id + "; Variant=" + variant
                + "; Suspect=" + suspect.Handle + "; Victim=" + victim.Handle
                + "; SuspectPosition=" + suspect.Position
                + "; VictimPosition=" + victim.Position
                + "; SeparationMeters=" + suspect.Position.DistanceTo(victim.Position)
                    .ToString("0.00", CultureInfo.InvariantCulture)
                + "; InitialTasks=" + (IsDisturbanceWeaponThreat(variant)
                    ? "VictimWanderInArea+SuspectFacingVictimWithSelectedWeapon"
                    : string.Equals(variant, "beer_harassment", StringComparison.OrdinalIgnoreCase)
                        ? "VictimWanderInArea+AttachedBottleAndDrinkAnimation"
                        : "VictimWanderInArea+AttachedCameraAndPhotographAnimation")
                + "; ResponseGraceOwner=Dispatch; ResponseGraceStartsAt=FirstCloseVisibleObservation"
                + "; Weapon=" + (IsDisturbanceWeaponThreat(variant)
                    ? incident.DisturbanceWeaponName : "None")
                + "; WeaponDrawn=" + incident.DisturbanceWeaponDrawn
                + "; TerminalOutcome=false; Owner=AmbientWorld");
            return true;
        }

        internal void MaintainDispatchDisturbanceActivity(
            LSPDDispatchEvent incident,
            DateTime now,
            bool suspectWounded)
        {
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff
                || incident.DisturbanceActivityStartedAt == DateTime.MinValue
                || incident.PlayerEncounterResponseStarted
                || incident.SurrenderRequested || suspectWounded)
                return;

            Ped suspect = incident.Suspect;
            Ped victim = incident.Victim;
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || victim == null || !victim.Exists() || victim.IsDead || victim.Health <= 0)
                return;
            suspect.BlockPermanentEvents = true;
            victim.BlockPermanentEvents = true;

            bool paparazzi = string.Equals(incident.DisturbanceVariant,
                "paparazzi", StringComparison.OrdinalIgnoreCase);
            bool beerHarassment = string.Equals(incident.DisturbanceVariant,
                "beer_harassment", StringComparison.OrdinalIgnoreCase);
            if (paparazzi || beerHarassment)
            {
                string propModel = paparazzi ? "prop_pap_camera_01" : "prop_beer_bottle";
                if (incident.DisturbanceProp == null || !incident.DisturbanceProp.Exists())
                    EnsureDispatchDisturbanceProp(incident, suspect, propModel,
                        paparazzi ? 0.035f : 0.09f,
                        paparazzi ? 0.025f : 0.02f,
                        paparazzi ? 0.02f : -0.015f,
                        paparazzi ? 92f : 105f,
                        paparazzi ? 0f : 5f,
                        paparazzi ? -90f : 0f,
                        now);

                if (now < incident.DisturbanceNextTaskAt)
                    return;
                if (incident.DisturbanceAnimationConfirmedPlaying)
                {
                    try
                    {
                        suspect.Task.ClearAll();
                        Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                            suspect,
                            victim,
                            -1,
                            beerHarassment ? -0.55f : -1.2f,
                            beerHarassment ? 0.2f : -0.4f,
                            0f,
                            1.0f,
                            -1,
                            true);
                    }
                    catch (Exception ex)
                    {
                        Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_FOLLOW_RESUME_FAILED",
                            "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                            + "; Suspect=" + suspect.Handle + "; Victim=" + victim.Handle
                            + "; Reason=" + ex.GetType().Name);
                    }
                    incident.DisturbanceAnimationRequested = false;
                    incident.DisturbanceAnimationConfirmedPlaying = false;
                    incident.DisturbanceAnimationStartAttempts = 0;
                    incident.DisturbanceNextTaskAt = now.AddSeconds(4);
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_FOLLOW_RESUMED",
                        "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                        + "; Suspect=" + suspect.Handle + "; Victim=" + victim.Handle
                        + "; FollowOffset=" + (beerHarassment ? "CloseIntrusive" : "Stalking")
                        + "; NextActivity=RepeatedPropAnimation; Owner=AmbientWorld");
                    return;
                }
                string dictionary = incident.DisturbanceAnimationDictionary;
                string clip = incident.DisturbanceAnimationClip;
                if (string.IsNullOrWhiteSpace(dictionary)
                    || string.IsNullOrWhiteSpace(clip))
                {
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ANIMATION_REFERENCE_LOST",
                        "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                        + "; ReadyBeforeOffer=" + incident.ScenePreparationCompleted);
                    return;
                }
                if (!incident.DisturbanceAnimationRequested
                    && !incident.DisturbanceAnimationConfirmedPlaying
                    && incident.DisturbanceProp != null && incident.DisturbanceProp.Exists())
                {
                    StartDispatchDisturbanceAnimation(incident, suspect, dictionary, clip, now);
                    return;
                }
                if (incident.DisturbanceAnimationRequested
                    && !incident.DisturbanceAnimationConfirmedPlaying)
                {
                    if (LSImmersiveDictionaryAnimation.IsPlaying(
                        suspect, incident.DisturbanceAnimationDictionary,
                        incident.DisturbanceAnimationClip))
                    {
                        incident.DisturbanceAnimationConfirmedPlaying = true;
                        incident.DisturbancePresentationReady = true;
                        incident.DisturbanceNextTaskAt = now.AddSeconds(3);
                        StartDispatchDisturbanceVictimReaction(
                            incident,
                            suspect,
                            victim,
                            paparazzi ? "StalkingVictimResponds" : "HarassmentVictimResponds");
                        Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_PRESENTATION_CONFIRMED",
                            "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                            + "; Suspect=" + suspect.Handle + "; Victim=" + victim.Handle
                            + "; Dictionary=" + incident.DisturbanceAnimationDictionary
                            + "; Clip=" + incident.DisturbanceAnimationClip
                            + "; Prop=" + incident.DisturbanceProp.Handle
                            + "; PropAttached=" + Function.Call<bool>(
                                Hash.IS_ENTITY_ATTACHED_TO_ENTITY, incident.DisturbanceProp, suspect)
                            + "; AnimationPlaying=true; NextStage=ResumeCloseFollowAfter3Seconds");
                    }
                    else if (now >= incident.DisturbanceNextTaskAt)
                    {
                        if (incident.DisturbanceAnimationStartAttempts >= 3)
                        {
                            incident.DisturbanceAnimationRequested = false;
                            incident.DisturbanceNextTaskAt = DateTime.MaxValue;
                            Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ANIMATION_NOT_PLAYING",
                                "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                                + "; Dictionary=" + incident.DisturbanceAnimationDictionary
                                + "; Clip=" + incident.DisturbanceAnimationClip
                                + "; Attempts=" + incident.DisturbanceAnimationStartAttempts
                                + "; PropAttached=true; PresentationReady=false");
                        }
                        else
                        {
                            incident.DisturbanceAnimationRequested = false;
                            StartDispatchDisturbanceAnimation(incident, suspect, dictionary, clip, now);
                        }
                    }
                }
                return;
            }

            if (!IsDisturbanceWeaponThreat(incident.DisturbanceVariant))
                return;
            if (!incident.DisturbanceWeaponDrawn
                && now >= incident.DisturbanceNextTaskAt
                && incident.DisturbanceWeaponPrepared)
            {
                int weaponHash = unchecked((int)StringHash.AtStringHash(
                    incident.DisturbanceWeaponName, 0));
                try
                {
                    suspect.CanSwitchWeapons = false;
                    Function.Call(Hash.SET_CURRENT_PED_WEAPON, suspect, weaponHash, true);
                    if (string.Equals(incident.DisturbanceVariant, "knife_threat",
                        StringComparison.OrdinalIgnoreCase))
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            suspect, victim, 1000);
                    else
                        Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, suspect, victim, -1, true);
                    incident.Armed = true;
                    incident.DisturbanceWeaponDrawn = true;
                    incident.DisturbanceNextTaskAt = now.AddSeconds(1);
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_WEAPON_DRAWN",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Victim=" + victim.Handle + "; Weapon=" + incident.DisturbanceWeaponName
                        + "; SelectedWeaponHash=" + Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, suspect)
                        + "; Task=" + (string.Equals(incident.DisturbanceVariant,
                            "knife_threat", StringComparison.OrdinalIgnoreCase)
                            ? "TASK_TURN_PED_TO_FACE_ENTITY" : "TASK_AIM_GUN_AT_ENTITY"));
                }
                catch (Exception ex)
                {
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_WEAPON_DRAW_FAILED",
                        "Occurrence=" + incident.Id + "; Reason=" + ex.GetType().Name);
                }
                return;
            }
            if (incident.DisturbanceWeaponDrawn
                && !incident.DisturbanceWeaponStageConfirmed
                && now >= incident.DisturbanceNextTaskAt)
            {
                int weaponHash = unchecked((int)StringHash.AtStringHash(
                    incident.DisturbanceWeaponName, 0));
                bool selected = false;
                bool clearLineOfSight = false;
                try
                {
                    selected = Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, suspect) == weaponHash;
                    clearLineOfSight = Function.Call<bool>(
                        Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, suspect, victim, 17);
                    if (!selected)
                    {
                        Function.Call(Hash.SET_CURRENT_PED_WEAPON, suspect, weaponHash, true);
                        selected = Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, suspect) == weaponHash;
                    }
                    if (!selected || !clearLineOfSight)
                    {
                        if (string.Equals(incident.DisturbanceVariant, "knife_threat",
                            StringComparison.OrdinalIgnoreCase))
                            Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                suspect, victim, 1000);
                        else
                        Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, suspect, victim, -1, true);
                    }
                }
                catch { }
                incident.DisturbanceNextTaskAt = now.AddSeconds(1);
                if (selected && clearLineOfSight)
                {
                    incident.DisturbanceWeaponStageConfirmed = true;
                    incident.DisturbancePresentationReady = true;
                    incident.DisturbanceNextTaskAt = DateTime.MaxValue;
                    try { victim.Task.HandsUp(60000); } catch { }
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_PRESENTATION_CONFIRMED",
                        "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                        + "; Suspect=" + suspect.Handle + "; Victim=" + victim.Handle
                        + "; Weapon=" + incident.DisturbanceWeaponName
                        + "; SelectedWeapon=true; TargetClearLineOfSight=true"
                        + "; Task=" + (string.Equals(incident.DisturbanceVariant,
                            "knife_threat", StringComparison.OrdinalIgnoreCase)
                            ? "TASK_TURN_PED_TO_FACE_ENTITY" : "TASK_AIM_GUN_AT_ENTITY")
                        + "; VictimHandsUpTaskIssued=true"
                        + "; VisibleWeaponAndLineOfSightConfirmed=true");
                }
                else
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_WEAPON_STAGE_WAITING",
                        "Occurrence=" + incident.Id + "; Suspect=" + suspect.Handle
                        + "; Victim=" + victim.Handle + "; SelectedWeapon=" + selected
                        + "; TargetClearLineOfSight=" + clearLineOfSight
                        + "; Task=" + (string.Equals(incident.DisturbanceVariant,
                            "knife_threat", StringComparison.OrdinalIgnoreCase)
                            ? "TASK_TURN_PED_TO_FACE_ENTITY" : "TASK_AIM_GUN_AT_ENTITY")
                        + "; PresentationReady=false");
            }
        }

        private static bool IsDisturbanceWeaponThreat(string variant)
        {
            return string.Equals(variant, "armed_threat",
                       StringComparison.OrdinalIgnoreCase)
                || string.Equals(variant, "knife_threat",
                    StringComparison.OrdinalIgnoreCase);
        }

        private void StartDispatchDisturbanceAnimation(
            LSPDDispatchEvent incident,
            Ped suspect,
            string dictionary,
            string clip,
            DateTime now)
        {
            if (incident == null || suspect == null || !suspect.Exists())
                return;
            incident.DisturbanceAnimationStartAttempts++;
            incident.DisturbanceAnimationDictionary = dictionary;
            incident.DisturbanceAnimationClip = clip;
            incident.DisturbanceNextTaskAt = now.AddSeconds(1);
            float duration = 0f;
            bool started = false;
            try
            {
                started = LSImmersiveDictionaryAnimation.TryPlay(
                    suspect, dictionary, clip, -8f, 8f, -1, 1, 1f, out duration);
            }
            catch { }
            incident.DisturbanceAnimationRequested = started;
            Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ANIMATION_REQUESTED",
                "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                + "; Suspect=" + suspect.Handle + "; Dictionary=" + dictionary
                + "; Clip=" + clip + "; RequestAccepted=" + started
                + "; ClipDuration=" + duration.ToString("0.000", CultureInfo.InvariantCulture)
                + "; Attempt=" + incident.DisturbanceAnimationStartAttempts
                + "; Prop=" + (incident.DisturbanceProp == null
                    || !incident.DisturbanceProp.Exists() ? 0 : incident.DisturbanceProp.Handle));
            if (!started && incident.DisturbanceAnimationStartAttempts >= 3)
            {
                incident.DisturbanceNextTaskAt = DateTime.MaxValue;
                Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_ANIMATION_UNAVAILABLE",
                    "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                    + "; Dictionary=" + dictionary + "; Clip=" + clip
                    + "; Attempts=" + incident.DisturbanceAnimationStartAttempts
                    + "; PresentationReady=false; ExplicitInteractionStillAvailable=true");
            }
        }

        private void StartDispatchDisturbanceVictimReaction(
            LSPDDispatchEvent incident,
            Ped suspect,
            Ped victim,
            string reaction)
        {
            if (incident == null || incident.DisturbanceVictimReactionStarted
                || suspect == null || !suspect.Exists()
                || victim == null || !victim.Exists() || victim.IsDead)
                return;

            try
            {
                victim.BlockPermanentEvents = true;
                victim.Task.ReactAndFlee(suspect);
                incident.DisturbanceVictimReactionStarted = true;
                Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_VICTIM_REACTED",
                    "Occurrence=" + incident.Id
                    + "; Variant=" + incident.DisturbanceVariant
                    + "; Suspect=" + suspect.Handle
                    + "; Victim=" + victim.Handle
                    + "; Reaction=" + reaction
                    + "; VictimTask=ReactAndFleeFromSuspect"
                    + "; Trigger=ConfirmedPropAnimation"
                    + "; SceneResponsePending=true; Owner=AmbientWorld");
            }
            catch (Exception ex)
            {
                if (incident.DisturbanceVictimReactionFailureLogged)
                    return;
                incident.DisturbanceVictimReactionFailureLogged = true;
                Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_VICTIM_REACTION_FAILED",
                    "Occurrence=" + incident.Id
                    + "; Variant=" + incident.DisturbanceVariant
                    + "; Suspect=" + (suspect == null || !suspect.Exists() ? 0 : suspect.Handle)
                    + "; Victim=" + (victim == null || !victim.Exists() ? 0 : victim.Handle)
                    + "; Reaction=" + reaction
                    + "; Reason=" + ex.GetType().Name
                    + "; TerminalOutcome=false; Owner=AmbientWorld");
            }
        }

        private void EnsureDispatchDisturbanceProp(
            LSPDDispatchEvent incident,
            Ped suspect,
            string modelName,
            float offsetX,
            float offsetY,
            float offsetZ,
            float rotationX,
            float rotationY,
            float rotationZ,
            DateTime now)
        {
            if (incident == null || suspect == null || !suspect.Exists()
                || incident.DisturbancePropFailureLogged
                || now < incident.DisturbancePropNextAttemptAt)
                return;
            incident.DisturbancePropNextAttemptAt = now.AddMilliseconds(750);
            incident.DisturbancePropLoadAttempts++;
            Model model = new Model(modelName);
            if (!model.IsValid || !model.IsInCdImage)
            {
                if (incident.DisturbancePropLoadAttempts >= 12)
                {
                    incident.DisturbancePropFailureLogged = true;
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_PROP_UNAVAILABLE",
                        "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                        + "; Model=" + modelName + "; Valid=" + model.IsValid
                        + "; InCdImage=" + model.IsInCdImage + "; Loaded=" + model.IsLoaded
                        + "; Attempts=" + incident.DisturbancePropLoadAttempts
                        + "; PresentationReady=false");
                }
                return;
            }
            if (!model.IsLoaded)
                model.Request();
            if (!model.IsLoaded)
            {
                if (incident.DisturbancePropLoadAttempts >= 12)
                {
                    incident.DisturbancePropFailureLogged = true;
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_PROP_UNAVAILABLE",
                        "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                        + "; Model=" + modelName + "; Valid=" + model.IsValid
                        + "; InCdImage=" + model.IsInCdImage + "; Loaded=false"
                        + "; Attempts=" + incident.DisturbancePropLoadAttempts
                        + "; PresentationReady=false");
                }
                return;
            }

            Prop prop = null;
            try
            {
                prop = World.CreateProp(model, suspect.Position, false, true);
                if (prop == null || !prop.Exists())
                    throw new InvalidOperationException("GTA did not create a valid disturbance prop.");
                int handBoneIndex = Function.Call<int>(Hash.GET_PED_BONE_INDEX, suspect, 57005);
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                    prop, suspect, handBoneIndex, offsetX, offsetY, offsetZ,
                    rotationX, rotationY, rotationZ, false, false, false, false, 2, true);
                if (!Function.Call<bool>(Hash.IS_ENTITY_ATTACHED_TO_ENTITY, prop, suspect))
                    throw new InvalidOperationException("GTA did not attach the disturbance prop to the suspect hand.");
                incident.DisturbanceProp = prop;
                Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_PROP_ATTACHED",
                    "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                    + "; Suspect=" + suspect.Handle + "; Prop=" + prop.Handle
                    + "; Model=" + modelName + "; ModelHash=" + model.Hash
                    + "; HandBoneIndex=" + handBoneIndex + "; Attached=true");
            }
            catch (Exception ex)
            {
                if (prop != null)
                {
                    try { if (prop.Exists()) prop.Delete(); } catch { }
                }
                if (!incident.DisturbancePropFailureLogged)
                {
                    incident.DisturbancePropFailureLogged = true;
                    Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_PROP_ATTACH_FAILED",
                        "Occurrence=" + incident.Id + "; Variant=" + incident.DisturbanceVariant
                        + "; Model=" + modelName + "; Reason=" + ex.GetType().Name
                        + "; PresentationReady=false");
                }
            }
            finally
            {
                model.MarkAsNoLongerNeeded();
            }
        }

        internal void CleanupDispatchDisturbanceProp(LSPDDispatchEvent incident, string reason)
        {
            if (incident == null || incident.DisturbanceProp == null)
                return;
            Prop prop = incident.DisturbanceProp;
            incident.DisturbanceProp = null;
            bool deleted = false;
            try
            {
                if (prop.Exists())
                {
                    prop.Delete();
                    deleted = true;
                }
            }
            catch { }
            Log("AMBIENT_WORLD_DISPATCH_DISTURBANCE_PROP_CLEANUP",
                "Occurrence=" + incident.Id + "; Prop=" + prop.Handle
                + "; Owner=AmbientWorld; Reason=" + (reason ?? string.Empty)
                + "; Deleted=" + deleted);
        }

        /// <summary>
        /// Starts a catalog-authored paired animation for two live Dispatch-owned
        /// scene actors. This is an explicit scene setup call: it never scans or
        /// adopts ambient peds, and it leaves Dispatch in charge of the actors.
        /// </summary>
        internal AmbientDispatchAnimationStartResult TryPrepareDispatchPairAnimation(
            LSPDDispatchEvent incident,
            Ped primaryActor,
            Ped secondaryActor,
            out string reason)
        {
            float actorDistance;
            return PrepareDispatchPairAnimationFormation(
                incident, primaryActor, secondaryActor, out actorDistance, out reason);
        }

        internal AmbientDispatchAnimationStartResult TryStartDispatchPairAnimation(
            LSPDDispatchEvent incident,
            Ped primaryActor,
            Ped secondaryActor,
            out string reason)
        {
            float actorDistance;
            AmbientDispatchAnimationStartResult preparation =
                PrepareDispatchPairAnimationFormation(
                    incident, primaryActor, secondaryActor, out actorDistance, out reason);
            if (preparation != AmbientDispatchAnimationStartResult.Started)
                return preparation;

            try
            {
                // Play each authored dictionary clip once. The caller controls
                // timing: ordinary paired meetings start on approach, while the
                // authorized mapped shop snatch starts during the en-route scene
                // so the victim threat is visible before the officer arrives.
                Function.Call(
                    Hash.TASK_PLAY_ANIM,
                    primaryActor,
                    incident.SceneAnimationDictionary,
                    incident.SceneAnimationPrimaryClip,
                    4.0f,
                    -4.0f,
                    -1,
                    0,
                    1.0f,
                    false,
                    false,
                    false);
                Function.Call(
                    Hash.TASK_PLAY_ANIM,
                    secondaryActor,
                    incident.SceneAnimationDictionary,
                    incident.SceneAnimationSecondaryClip,
                    4.0f,
                    -4.0f,
                    -1,
                    0,
                    1.0f,
                    false,
                    false,
                    false);

                Log("AMBIENT_WORLD_DISPATCH_PAIR_ANIMATION_STARTED",
                    "Event=" + incident.Id
                    + "; Behavior=" + incident.SceneBehavior
                    + "; Dictionary=" + incident.SceneAnimationDictionary
                    + "; Primary=" + incident.SceneAnimationPrimaryClip
                    + "; Secondary=" + incident.SceneAnimationSecondaryClip
                    + "; Actors=" + primaryActor.Handle + "," + secondaryActor.Handle
                    + "; Distance=" + actorDistance.ToString("0.00", CultureInfo.InvariantCulture));
                return AmbientDispatchAnimationStartResult.Started;
            }
            catch (Exception ex)
            {
                reason = "The animation dictionary could not start: " + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_PAIR_ANIMATION_FAILED",
                    "Event=" + incident.Id + "; Reason=" + reason);
                return AmbientDispatchAnimationStartResult.Failed;
            }
        }

        private AmbientDispatchAnimationStartResult PrepareDispatchPairAnimationFormation(
            LSPDDispatchEvent incident,
            Ped primaryActor,
            Ped secondaryActor,
            out float actorDistance,
            out string reason)
        {
            actorDistance = 0f;
            reason = string.Empty;
            if (incident == null || !incident.OwnedByDispatch
                || incident.HasConvoyCustodyHandoff)
            {
                reason = "Dispatch no longer owns this scene.";
                return AmbientDispatchAnimationStartResult.Failed;
            }

            bool pairedScene = string.Equals(
                    incident.SceneBehavior, "paired_meeting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    incident.SceneBehavior, "paired_exchange", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    incident.SceneBehavior, "interior_clerk_snatch", StringComparison.OrdinalIgnoreCase);
            if (!pairedScene
                || string.IsNullOrWhiteSpace(incident.SceneAnimationDictionary)
                || string.IsNullOrWhiteSpace(incident.SceneAnimationPrimaryClip)
                || string.IsNullOrWhiteSpace(incident.SceneAnimationSecondaryClip))
            {
                reason = "The Dispatch event has no complete supported paired or mapped shop-snatch animation definition.";
                return AmbientDispatchAnimationStartResult.Failed;
            }

            if (primaryActor == null || secondaryActor == null
                || !primaryActor.Exists() || !secondaryActor.Exists()
                || primaryActor.Handle == secondaryActor.Handle
                || primaryActor.IsDead || secondaryActor.IsDead
                || primaryActor.IsInVehicle() || secondaryActor.IsInVehicle())
            {
                reason = "Both paired scene actors must be separate, living pedestrians on foot.";
                return AmbientDispatchAnimationStartResult.Failed;
            }

            actorDistance = primaryActor.Position.DistanceTo(secondaryActor.Position);
            if (actorDistance < 0.65f || actorDistance > 2.5f)
            {
                reason = "Stage=PairedSceneFormation; Primary=" + primaryActor.Handle
                    + "; Secondary=" + secondaryActor.Handle
                    + "; Distance=" + actorDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Allowed=0.65-2.50m; Result=ActorsOutsideAuthoredFormation";
                return AmbientDispatchAnimationStartResult.Failed;
            }

            try
            {
                if (!incident.SceneAnimationRequested)
                {
                    Function.Call(Hash.REQUEST_ANIM_DICT, incident.SceneAnimationDictionary);
                    incident.SceneAnimationRequested = true;
                }
                if (!Function.Call<bool>(
                    Hash.HAS_ANIM_DICT_LOADED,
                    incident.SceneAnimationDictionary))
                {
                    reason = "Stage=PairedSceneAnimation; Dictionary="
                        + incident.SceneAnimationDictionary + "; Result=StreamingPending";
                    return AmbientDispatchAnimationStartResult.Pending;
                }

                Function.Call(
                    Hash.SET_ENTITY_HEADING,
                    primaryActor,
                    HeadingToward(primaryActor.Position, secondaryActor.Position));
                Function.Call(
                    Hash.SET_ENTITY_HEADING,
                    secondaryActor,
                    HeadingToward(secondaryActor.Position, primaryActor.Position));
                reason = "Stage=PairedSceneFormation; Primary=" + primaryActor.Handle
                    + "; Secondary=" + secondaryActor.Handle
                    + "; Distance=" + actorDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + "; Facing=Mutual; AnimationDictionary="
                    + incident.SceneAnimationDictionary + "; AnimationReady=true";
                return AmbientDispatchAnimationStartResult.Started;
            }
            catch (Exception ex)
            {
                reason = "Stage=PairedSceneAnimation; Result=Exception:" + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_PAIR_ANIMATION_FAILED",
                    "Event=" + incident.Id + "; Reason=" + reason);
                return AmbientDispatchAnimationStartResult.Failed;
            }
        }

        private static float HeadingToward(Vector3 from, Vector3 to)
        {
            double x = to.X - from.X;
            double y = to.Y - from.Y;
            double heading = Math.Atan2(x, y) * (180.0 / Math.PI);
            if (heading < 0.0)
                heading += 360.0;
            return (float)heading;
        }

        /// <summary>
        /// Removes stale registered actor references only. It does not search for new
        /// actors and it does not delete GTA entities.
        /// </summary>
        internal void PruneStaleActors()
        {
            var stale = new List<int>();

            foreach (KeyValuePair<int, AmbientActorContext> pair in _actors)
            {
                Ped ped = pair.Value.TryGetPed();
                if (ped == null || !ped.Exists())
                    stale.Add(pair.Key);
            }

            foreach (int handle in stale)
                UnregisterActor(handle);
        }

        /// <summary>
        /// Bounded maintenance entry point for a future project owner.
        /// The method only considers actors already registered with this framework.
        /// It never performs a city-wide World.GetAllPeds scan.
        ///
        /// No GTA task is issued here. The result is a behavior plan which a future
        /// gameplay owner may deliberately execute after validating ownership and
        /// runtime navigation.
        /// </summary>
        internal IReadOnlyList<AmbientBehaviorPlan> EvaluateRegisteredActors(
            long gameTimeMilliseconds,
            Vector3 observerPosition)
        {
            PruneStaleActors();

            var plans = new List<AmbientBehaviorPlan>();

            foreach (KeyValuePair<int, AmbientActorContext> pair in _actors)
            {
                AmbientActorContext actor = pair.Value;

                long nextEvaluation;
                if (_nextEvaluationAt.TryGetValue(pair.Key, out nextEvaluation)
                    && gameTimeMilliseconds < nextEvaluation)
                {
                    continue;
                }

                _nextEvaluationAt[pair.Key] =
                    gameTimeMilliseconds + Math.Max(
                        _minimumEvaluationIntervalMilliseconds,
                        _maintenanceIntervalMilliseconds);

                float distance = actor.TryGetDistance(observerPosition);
                AmbientBehaviorPlan plan = PlanBehavior(actor, distance);
                if (plan != null)
                    plans.Add(plan);
            }

            return plans;
        }

        /// <summary>
        /// Produces an ambient behavior decision without taking ownership of the actor.
        /// Protected/project-owned entities intentionally return a defer plan.
        /// </summary>
        internal AmbientBehaviorPlan PlanBehavior(
            AmbientActorContext actor,
            float distanceFromObserver)
        {
            if (actor == null)
                return AmbientBehaviorPlan.Deferred("No actor context was supplied.");

            if (actor.PedIsMissing())
                return AmbientBehaviorPlan.Deferred("Ped entity no longer exists.");

            if (actor.Ownership != AmbientBehaviorOwnership.Ambient)
            {
                return AmbientBehaviorPlan.Deferred(
                    "Actor is owned by another gameplay system: " + actor.Ownership);
            }

            if (actor.ProtectionStatus != AmbientProtectionStatus.None)
            {
                return AmbientBehaviorPlan.Deferred(
                    "Actor is protected from unrelated ambient control: " + actor.ProtectionStatus);
            }

            if (!actor.Location.IsBehaviorEligible())
            {
                return AmbientBehaviorPlan.Deferred(
                    "Location capabilities are insufficient for the actor's requested ambient behavior.");
            }

            AmbientBehaviorState desiredState = ResolveAmbientState(actor);

            return AmbientBehaviorPlan.Create(
                actor,
                desiredState,
                distanceFromObserver,
                BuildReason(actor, desiredState));
        }

        /// <summary>
        /// Requests a clean ownership handoff from ambient behavior to a specialized
        /// gameplay owner. This is only a state/ownership transition; the specialized
        /// owner remains responsible for actual gameplay behavior.
        /// </summary>
        internal bool TryTransferOwnership(
            Ped ped,
            AmbientBehaviorOwnership newOwner,
            string reason)
        {
            if (ped == null || !ped.Exists())
                return false;

            AmbientActorContext actor;
            if (!_actors.TryGetValue(ped.Handle, out actor))
                return false;

            if (newOwner == AmbientBehaviorOwnership.Ambient)
                return false;

            if (actor.Ownership != AmbientBehaviorOwnership.Ambient)
                return false;

            actor.Ownership = newOwner;
            actor.CurrentActivity = string.IsNullOrWhiteSpace(reason)
                ? "Ownership transferred"
                : reason;
            actor.CurrentState = AmbientBehaviorState.Protected;
            return true;
        }

        private static bool IsFinite(Vector3 position)
        {
            return !float.IsNaN(position.X) && !float.IsInfinity(position.X)
                && !float.IsNaN(position.Y) && !float.IsInfinity(position.Y)
                && !float.IsNaN(position.Z) && !float.IsInfinity(position.Z);
        }

        /// <summary>
        /// Releases an actor back to ambient ownership after the specialized owner
        /// explicitly says the actor is no longer needed.
        /// </summary>
        internal bool ReleaseToAmbient(Ped ped, string reason)
        {
            if (ped == null || !ped.Exists())
                return false;

            AmbientActorContext actor;
            if (!_actors.TryGetValue(ped.Handle, out actor))
                return false;

            actor.Ownership = AmbientBehaviorOwnership.Ambient;
            actor.ProtectionStatus = AmbientProtectionStatus.None;
            actor.CurrentActivity = string.IsNullOrWhiteSpace(reason)
                ? "Released to ambient world"
                : reason;
            actor.CurrentState = AmbientBehaviorState.AwaitDispatch;
            return true;
        }

        private static AmbientBehaviorState ResolveAmbientState(AmbientActorContext actor)
        {
            if (actor.AllowedStates == null || actor.AllowedStates.Count == 0)
                return AmbientBehaviorState.Idle;

            if (actor.Location.IsIndoorRestrictionActive)
            {
                if (actor.AllowedStates.Contains(AmbientBehaviorState.Work))
                    return AmbientBehaviorState.Work;
                if (actor.AllowedStates.Contains(AmbientBehaviorState.Stand))
                    return AmbientBehaviorState.Stand;
                if (actor.AllowedStates.Contains(AmbientBehaviorState.Wait))
                    return AmbientBehaviorState.Wait;
            }

            if (actor.AllowedStates.Contains(actor.CurrentState)
                && actor.CurrentState != AmbientBehaviorState.Protected)
            {
                return actor.CurrentState;
            }

            AmbientBehaviorState[] priority =
            {
                AmbientBehaviorState.Work,
                AmbientBehaviorState.Guard,
                AmbientBehaviorState.Patrol,
                AmbientBehaviorState.Wander,
                AmbientBehaviorState.Walk,
                AmbientBehaviorState.Talk,
                AmbientBehaviorState.Wait,
                AmbientBehaviorState.Stand,
                AmbientBehaviorState.Idle,
                AmbientBehaviorState.AwaitDispatch
            };

            foreach (AmbientBehaviorState candidate in priority)
            {
                if (actor.AllowedStates.Contains(candidate))
                    return candidate;
            }

            return AmbientBehaviorState.Idle;
        }

        private static string BuildReason(
            AmbientActorContext actor,
            AmbientBehaviorState state)
        {
            string place = string.IsNullOrWhiteSpace(actor.Location.Place)
                ? "surveyed location"
                : actor.Location.Place;

            return "Ambient " + state + " planned for " + place + ".";
        }
    }

    internal enum AmbientBehaviorRole
    {
        Unknown,
        Civilian,
        Resident,
        StoreClerk,
        ShopWorker,
        Security,
        PoliceOfficer,
        Witness,
        Victim,
        CriminalCandidate,
        CriminalActor,
        ProjectActor
    }

    internal enum AmbientBehaviorState
    {
        Idle,
        Stand,
        Wander,
        Walk,
        Talk,
        Work,
        Shop,
        Sit,
        Wait,
        Guard,
        Patrol,
        Observe,
        Investigate,
        Approach,
        Enter,
        Exit,
        Flee,
        Hide,
        Comply,
        Resist,
        AwaitDispatch,
        AwaitPolice,
        Protected,
        Released
    }

    internal enum AmbientBehaviorOwnership
    {
        Ambient,
        PoliceResponse,
        Dispatch,
        CrimeActivity,
        Gang,
        Backup,
        Convoy,
        OtherProtected
    }

    internal enum AmbientProtectionStatus
    {
        None,
        ProtectedGameplayActor,
        ProtectedVehicleOccupant,
        ActivePoliceInteraction,
        ActiveCrimeScene,
        ActiveDispatch,
        ActiveBackup,
        ActiveConvoy
    }

    internal enum AmbientCapabilityStatus
    {
        Confirmed,
        Observed,
        Candidate,
        Blocked,
        Unverified,
        Unknown
    }

    internal enum AmbientNavigationMode
    {
        Unknown,
        NativePedNavigation,
        PlacementOnly,
        TeleportRequired,
        PlayerOnly,
        Unverified
    }

    internal enum AmbientVerticalContext
    {
        Unknown,
        Surface,
        InteriorFloor,
        Underground,
        UnderBridge,
        Tunnel,
        Rooftop,
        MultiLevel
    }

    internal enum AmbientPointRole
    {
        ActorPoint,
        ExteriorPoint,
        ThresholdPoint,
        InteriorPoint,
        SceneCenter,
        PlayerApproachPoint,
        GpsPoint,
        WorldBlipPoint,
        VictimCandidate,
        CriminalCandidate,
        WitnessCandidate,
        AmbientPedReference
    }

    internal enum AmbientWeaponAccess
    {
        Unknown,
        Allowed,
        Restricted,
        Blocked
    }

    internal enum AmbientCombatAccess
    {
        Unknown,
        Allowed,
        Restricted,
        Blocked
    }

    /// <summary>
    /// Capability-aware runtime view of a surveyed physical location. The shared
    /// XML remains the source catalog; this type does not replace it.
    /// </summary>
    internal sealed class AmbientLocationDescriptor
    {
        internal string SiteId { get; set; }
        internal string Region { get; set; }
        internal string Zone { get; set; }
        internal string Street { get; set; }
        internal string Place { get; set; }
        internal string EnvironmentType { get; set; }

        internal AmbientCapabilityStatus PlayerFootAccess { get; set; }
        internal AmbientCapabilityStatus PedFootAccess { get; set; }
        internal AmbientCapabilityStatus VehicleAccess { get; set; }
        internal AmbientCapabilityStatus InteriorAccess { get; set; }
        internal AmbientCapabilityStatus ThresholdAccess { get; set; }

        internal AmbientWeaponAccess WeaponAccess { get; set; }
        internal AmbientCombatAccess CombatAccess { get; set; }
        internal AmbientNavigationMode NavigationMode { get; set; }
        internal AmbientVerticalContext VerticalContext { get; set; }

        internal bool IsIndoorRestrictionActive
        {
            get
            {
                return WeaponAccess == AmbientWeaponAccess.Restricted
                    || WeaponAccess == AmbientWeaponAccess.Blocked
                    || CombatAccess == AmbientCombatAccess.Restricted
                    || CombatAccess == AmbientCombatAccess.Blocked;
            }
        }

        internal string InteriorId { get; set; }
        internal string InteriorName { get; set; }
        internal string RoomName { get; set; }

        internal AmbientLocationPoint Exterior { get; set; }
        internal AmbientLocationPoint Threshold { get; set; }
        internal AmbientLocationPoint Interior { get; set; }
        internal AmbientLocationPoint ActorPoint { get; set; }
        internal AmbientLocationPoint PlayerApproach { get; set; }
        internal AmbientLocationPoint GpsPoint { get; set; }
        internal AmbientLocationPoint WorldBlipPoint { get; set; }

        internal bool IsBehaviorEligible()
        {
            return PedFootAccess == AmbientCapabilityStatus.Confirmed
                || PedFootAccess == AmbientCapabilityStatus.Observed;
        }

        internal AmbientLocationPoint GetPoint(AmbientPointRole role)
        {
            switch (role)
            {
                case AmbientPointRole.ExteriorPoint:
                    return Exterior;
                case AmbientPointRole.ThresholdPoint:
                    return Threshold;
                case AmbientPointRole.InteriorPoint:
                    return Interior;
                case AmbientPointRole.PlayerApproachPoint:
                    return PlayerApproach;
                case AmbientPointRole.GpsPoint:
                    return GpsPoint;
                case AmbientPointRole.WorldBlipPoint:
                    return WorldBlipPoint;
                case AmbientPointRole.ActorPoint:
                case AmbientPointRole.SceneCenter:
                case AmbientPointRole.VictimCandidate:
                case AmbientPointRole.CriminalCandidate:
                case AmbientPointRole.WitnessCandidate:
                case AmbientPointRole.AmbientPedReference:
                default:
                    return ActorPoint;
            }
        }
    }

    internal sealed class AmbientLocationPoint
    {
        internal AmbientPointRole Role { get; set; }
        internal Vector3 Position { get; set; }
        internal float Heading { get; set; }
        internal AmbientCapabilityStatus Status { get; set; }
        internal AmbientNavigationMode NavigationMode { get; set; }
        internal AmbientVerticalContext VerticalContext { get; set; }
        internal string SourceCaptureId { get; set; }
        internal bool IsSpawnEligible { get; set; }
        internal bool IsPlayerAccessible { get; set; }
        internal bool IsPedAccessible { get; set; }
    }

    internal sealed class AmbientInteriorAccessRoute
    {
        internal AmbientInteriorAccessRoute(
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair,
            Vector3 entryPosition,
            Vector3 destinationPosition,
            int currentLiveInteriorId,
            int targetLiveInteriorId,
            bool enteringInterior)
        {
            Pair = pair;
            EntryPosition = entryPosition;
            DestinationPosition = destinationPosition;
            CurrentLiveInteriorId = currentLiveInteriorId;
            TargetLiveInteriorId = targetLiveInteriorId;
            EnteringInterior = enteringInterior;
        }

        internal LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair Pair { get; private set; }
        internal Vector3 EntryPosition { get; private set; }
        internal Vector3 DestinationPosition { get; private set; }
        internal int CurrentLiveInteriorId { get; private set; }
        internal int TargetLiveInteriorId { get; private set; }
        internal string CatalogInteriorId
        {
            get { return Pair == null ? string.Empty : Pair.InteriorId; }
        }
        internal string LocationId
        {
            get { return Pair == null || Pair.InsideLocation == null
                ? string.Empty : Pair.InsideLocation.Id; }
        }
        internal bool EnteringInterior { get; private set; }
    }

    internal sealed class AmbientInteriorAccessSession
    {
        internal int ActorHandle { get; set; }
        internal AmbientInteriorAccessRoute Route { get; set; }
        internal int DoorEntityHandle { get; set; }
        internal int DoorEntityModelHash { get; set; }
        internal Vector3 DoorEntityOriginalPosition { get; set; }
        internal Vector3 DoorEntityOriginalRotation { get; set; }
        internal int DoorHash { get; set; }
        internal int OriginalDoorState { get; set; }
        internal float OriginalOpenRatio { get; set; }
        internal float LastRequestedOpenRatio { get; set; }
        internal int StartedAtGameTime { get; set; }
        internal int LastOpenRequestGameTime { get; set; }
        internal int NextFloorPreflightAtGameTime { get; set; }
        internal int NextDoorUnlockAttemptAtGameTime { get; set; }
        internal int NextInteriorStreamingRequestAtGameTime { get; set; }
        internal int NextInteriorReadinessPollAtGameTime { get; set; }
        internal int DoorUnlockAttempts { get; set; }
        internal int CrossingSinceGameTime { get; set; }
        internal int LastObservedAtGameTime { get; set; }
        internal float LastObservedProgress { get; set; }
        internal float LastObservedDoorOpenRatio { get; set; }
        internal Vector3 LastObservedPosition { get; set; }
        internal string DoorEntityMotionEvidence { get; set; }
        internal string FloorPreflightLastFailure { get; set; }
        internal string InteriorReadinessLastFailure { get; set; }
        internal bool ReadyReported { get; set; }
        internal bool InteriorReadinessConfirmed { get; set; }
        internal bool InteriorReadinessWaitLogged { get; set; }
        internal bool CrossingInteriorObserved { get; set; }
        internal bool CrossingIntermediateObserved { get; set; }
        internal bool CrossingDiscontinuityLogged { get; set; }
        internal bool DoorPhysicsWaitLogged { get; set; }
        internal bool DoorPhysicsReadyConfirmed { get; set; }
        internal bool FloorPreflightWaitLogged { get; set; }
        internal bool DestinationFloorPreflightReady { get; set; }
        internal bool DoorUnlockConfirmed { get; set; }
        internal bool DoorPhysicallyOpenConfirmed { get; set; }
        internal bool DoorEntityMotionWaitLogged { get; set; }
        internal bool DoorUnlockFailureLogged { get; set; }
    }

    internal sealed class AmbientConfirmedInteriorCrossing
    {
        internal LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair Pair { get; set; }
        internal int InteriorId { get; set; }
        internal Vector3 CrossingPosition { get; set; }
        internal Vector3 InteriorFloorPosition { get; set; }
        internal int ConfirmedAtGameTime { get; set; }
    }

    internal sealed class AmbientActorContext
    {
        internal int EntityHandle { get; set; }
        internal AmbientBehaviorRole Role { get; set; }
        internal AmbientBehaviorState CurrentState { get; set; }
        internal AmbientBehaviorOwnership Ownership { get; set; }
        internal AmbientProtectionStatus ProtectionStatus { get; set; }
        internal AmbientLocationDescriptor Location { get; set; }
        internal string CurrentActivity { get; set; }
        internal HashSet<AmbientBehaviorState> AllowedStates { get; private set; }

        internal AmbientActorContext()
        {
            Role = AmbientBehaviorRole.Unknown;
            CurrentState = AmbientBehaviorState.Idle;
            Ownership = AmbientBehaviorOwnership.Ambient;
            ProtectionStatus = AmbientProtectionStatus.None;
            Location = new AmbientLocationDescriptor();
            CurrentActivity = string.Empty;
            AllowedStates = new HashSet<AmbientBehaviorState>();
        }

        internal bool PedIsMissing()
        {
            Ped ped = TryGetPed();
            return ped == null || !ped.Exists();
        }

        internal Ped TryGetPed()
        {
            if (EntityHandle <= 0)
                return null;

            try
            {
                Ped ped = EntityHandle == Game.Player.Character.Handle
                    ? Game.Player.Character
                    : Entity.FromHandle(EntityHandle) as Ped;

                return ped;
            }
            catch
            {
                return null;
            }
        }

        internal float TryGetDistance(Vector3 observerPosition)
        {
            Ped ped = TryGetPed();
            if (ped == null || !ped.Exists())
                return float.MaxValue;

            try
            {
                return ped.Position.DistanceTo(observerPosition);
            }
            catch
            {
                return float.MaxValue;
            }
        }
    }

    internal sealed class AmbientBehaviorPlan
    {
        private AmbientBehaviorPlan()
        {
        }

        internal AmbientActorContext Actor { get; private set; }
        internal AmbientBehaviorState DesiredState { get; private set; }
        internal AmbientBehaviorOwnership Ownership { get; private set; }
        internal float DistanceFromObserver { get; private set; }
        internal string Reason { get; private set; }
        internal bool IsDeferred { get; private set; }

        internal static AmbientBehaviorPlan Create(
            AmbientActorContext actor,
            AmbientBehaviorState desiredState,
            float distanceFromObserver,
            string reason)
        {
            return new AmbientBehaviorPlan
            {
                Actor = actor,
                DesiredState = desiredState,
                Ownership = actor.Ownership,
                DistanceFromObserver = distanceFromObserver,
                Reason = reason ?? string.Empty,
                IsDeferred = false
            };
        }

        internal static AmbientBehaviorPlan Deferred(string reason)
        {
            return new AmbientBehaviorPlan
            {
                Actor = null,
                DesiredState = AmbientBehaviorState.Protected,
                Ownership = AmbientBehaviorOwnership.OtherProtected,
                DistanceFromObserver = float.MaxValue,
                Reason = reason ?? string.Empty,
                IsDeferred = true
            };
        }
    }

    /// <summary>
    /// A transient runtime resolution of one catalog site. This is not written
    /// back to LSImmersiveLocation.xml and does not upgrade the site's authored
    /// confidence or Enhanced verification flags.
    /// </summary>
    internal sealed class AmbientDispatchSceneResolution
    {
        internal LSPDDispatchLocationDefinition Area { get; set; }
        internal string AreaLocationId { get; set; }
        internal Vector3 AreaAnchorPosition { get; set; }
        internal string LocationId { get; set; }
        internal string LocationName { get; set; }
        internal string LocationCategory { get; set; }
        internal string PointId { get; set; }
        internal string SourceCaptureId { get; set; }
        internal Vector3 AuthoredPosition { get; set; }
        internal Vector3 SceneCenter { get; set; }
        internal Vector3 ActorPosition { get; set; }
        internal float Heading { get; set; }
        internal Vector3 VehicleStagingPosition { get; set; }
        internal float VehicleHeading { get; set; }
        internal Vector3 VehicleRoutePosition { get; set; }
        internal float VehicleRouteHeading { get; set; }
        internal string VehicleStagingMode { get; set; }
        internal bool HasVehicleStagingPosition { get; set; }
        internal bool HasVehicleRoute { get; set; }
        internal bool IsSurveyedPoint { get; set; }
        internal float MaximumSnapDistance { get; set; }
        internal float ExteriorRoadHeightTolerance { get; set; }
        internal int MinimumExteriorOpenFootprintDirections { get; set; }
        internal Vector3 ExteriorApproachPosition { get; set; }
        internal string ExteriorPlacementEvidence { get; set; }
        internal bool IsInteriorScene { get; set; }
        internal bool IsMappedInteriorDispatchScene { get; set; }
        internal bool IsInsideOnlyInteriorScene { get; set; }
        internal string CatalogInteriorId { get; set; }
        internal int LiveInteriorId { get; set; }
        internal string IncidentType { get; set; }
        internal LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair InteriorAccessPair { get; set; }
        internal LSImmersiveLocationCatalog.LSImmersiveLocationDefinition InsideOnlyLocation { get; set; }
        internal LSImmersiveLocationCatalog.LSImmersiveLocationPoint InsideOnlyPoint { get; set; }
        internal string ValidationStatus { get; set; }
        internal string LocationReviewStatus { get; set; }
        internal bool IsPendingSceneResolution { get; set; }
        internal Vector3 PendingResolutionPosition { get; set; }
    }
}
