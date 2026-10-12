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
        private const float InteriorAccessDoorOpenRatio = 0.95f;
        private const float InteriorAccessDoorPropSearchRadius = 2.75f;
        private const float InteriorAccessDoorMaxSegmentDistance = 1.75f;
        private const float GunStoreEndpointDoorPropSearchRadius = 4f;
        private const float GunStoreEndpointDoorMaxDistance = 2.5f;
        private const string RuntimeGunStoreDoorMappingBasis =
            "RuntimeGunStoreEntranceEgress_DoorValidatedOnUse";
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
        private const int MaximumMappedInteriorDispatchCandidates = 3;
        private const int MaximumInsideOnlyInteriorSceneCandidates = 2;
        private const int MaximumDispatchInteriorPins = 6;
        private const int DispatchInteriorIdentityCacheLifetimeMilliseconds = 3000;
        private const float MaximumInteriorPlayerFallbackDistance = 18f;
        private const ulong PinInteriorInMemoryNativeHash = 0x2CA429C029CCF247;
        private const ulong UnpinInteriorNativeHash = 0x261CCE7EED010641;
        private const int RecentLocationHistoryLimit = 8;
        private const float DispatchCivilianSearchRadius = 10f;
        private const float DispatchCivilianMinimumSuspectDistance = 2.25f;
        private const int MaximumDispatchCivilianCandidatesToInspect = 24;
        private const float DispatchParticipantFallbackOffset = 1.25f;
        private const float DispatchParticipantDiagonalFallbackOffset = 0.88388348f;
        private const float DispatchParticipantMaximumTotalOffset = 2.75f;
        private const float DispatchParticipantMinimumSpacing = 1.5f;
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
        private readonly HashSet<string> _recentLocationIdSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, AmbientInteriorAccessSession> _interiorAccessSessions =
            new Dictionary<int, AmbientInteriorAccessSession>();
        private readonly HashSet<int> _dispatchPinnedInteriorIds = new HashSet<int>();
        private LSImmersiveLocationCatalog _locationCatalog;
        private LSNPCDatabase _npcDatabase;
        private LSImmersiveLocationCatalog _interiorAccessPairsCacheCatalog;
        private List<LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair>
            _interiorAccessPairsIncludingGunStores;
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

        internal LSWorldAmbientBehavior(
            int maintenanceIntervalMilliseconds = 750,
            int minimumEvaluationIntervalMilliseconds = 1000)
        {
            _maintenanceIntervalMilliseconds = Math.Max(100, maintenanceIntervalMilliseconds);
            _minimumEvaluationIntervalMilliseconds = Math.Max(250, minimumEvaluationIntervalMilliseconds);
        }

        internal LSWorldAmbientBehavior(
            LSImmersiveLocationCatalog locationCatalog,
            LSImmersiveLog log,
            int maintenanceIntervalMilliseconds = 750,
            int minimumEvaluationIntervalMilliseconds = 1000)
            : this(maintenanceIntervalMilliseconds, minimumEvaluationIntervalMilliseconds)
        {
            _locationCatalog = locationCatalog;
            _log = log;
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
            _interiorAccessPairsCacheCatalog = null;
            _interiorAccessPairsIncludingGunStores = null;
            _dispatchInteriorPointsCacheCatalog = null;
            _dispatchInteriorPointReferences = null;
            _mappedInteriorIdentityCacheCatalog = null;
            _mappedInteriorIdentityCachePlayerLiveId = 0;
            _mappedInteriorIdentityCacheCapturedAtUtc = DateTime.MinValue;
            _mappedInteriorIdentityCachePlayerPosition = Vector3.Zero;
            _mappedInteriorLiveIdsByPair = null;
            _mappedInteriorLiveIdsByPoint = null;
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
            bool contextPressed,
            out string message)
        {
            message = string.Empty;
            if (player == null || !player.Exists())
            {
                CancelAllInteriorAccessSessions("PlayerUnavailable");
                return AmbientInteriorAccessStatus.None;
            }

            AmbientInteriorAccessSession activeSession;
            if (_interiorAccessSessions.TryGetValue(player.Handle, out activeSession))
            {
                return MaintainInteriorAccess(player, out message);
            }

            if (!allowInput || player.IsDead || player.IsInVehicle())
                return AmbientInteriorAccessStatus.None;

            bool nearMappedPair = IsNearMappedInteriorPair(player.Position);
            if (!nearMappedPair)
                return AmbientInteriorAccessStatus.None;

            AmbientInteriorAccessRoute route;
            string routeFailure;
            if (!TryResolveInteriorAccessRoute(player, out route, out routeFailure))
            {
                if (contextPressed)
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
                return AmbientInteriorAccessStatus.None;
            }

            if (!contextPressed)
            {
                Screen.ShowHelpTextThisFrame(
                    "Press Enter or ~INPUT_CONTEXT~ to unlock and open the mapped door.",
                    false);
                return AmbientInteriorAccessStatus.None;
            }

            int requestedInteriorId;
            TryGetActorInteriorId(player, out requestedInteriorId);
            Log("AMBIENT_WORLD_INTERIOR_ACCESS_INPUT_RECEIVED",
                "Actor=" + player.Handle
                + "; EntityInteriorId=" + requestedInteriorId
                + "; CoordinateInteriorId=" + GetInteriorAt(player.Position)
                + "; Position=" + player.Position
                + "; Entering=" + route.EnteringInterior
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
                return AmbientInteriorAccessStatus.Failed;
            }

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

            AmbientInteriorAccessRoute nearest = null;
            float nearestDistance = float.MaxValue;
            string walkablePositionFailure = string.Empty;
            string nearestSideFailure = string.Empty;
            float nearestSideFailureDistance = float.MaxValue;
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in GetInteriorAccessPairsIncludingGunStores())
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

                // GTA may report one non-zero interior ID on both sides of a
                // storefront threshold. The mapped points distinguish the sides;
                // runtime door lookup and physical crossing still validate entry.

                bool enteringInterior = outsideDistance <= InteriorAccessUseRadius
                    && (outsideDistance + 0.35f < insideDistance
                        || actorCoordinateInteriorId == liveOutsideInteriorId);
                bool exitingInterior = insideDistance <= InteriorAccessUseRadius
                    && (insideDistance + 0.35f < outsideDistance
                        || actorCoordinateInteriorId == liveInsideInteriorId);
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
                in GetInteriorAccessPairsIncludingGunStores())
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
                    Log("AMBIENT_WORLD_DISPATCH_INTERIOR_PIN_REQUESTED",
                        "Location=" + (locationId ?? string.Empty)
                        + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                        + "; LiveInteriorId=" + liveInteriorId
                        + "; Native=PIN_INTERIOR_IN_MEMORY; Result=Requested");
                }
                catch (Exception ex)
                {
                    reason = "Stage=InteriorStreamingRequest; Location=" + (locationId ?? string.Empty)
                        + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                        + "; LiveInteriorId=" + liveInteriorId
                        + "; Native=PIN_INTERIOR_IN_MEMORY; Result=Exception:" + ex.GetType().Name;
                    return false;
                }
            }

            try
            {
                RequestInteriorCollision(insidePosition);
            }
            catch (Exception ex)
            {
                reason = "Stage=InteriorStreamingRequest; Location=" + (locationId ?? string.Empty)
                    + "; CatalogInteriorId=" + (catalogInteriorId ?? string.Empty)
                    + "; LiveInteriorId=" + liveInteriorId
                    + "; Native=REQUEST_COLLISION_AT_COORD; Result=Exception:" + ex.GetType().Name;
                return false;
            }

            reason = "Stage=InteriorReadiness; Location=" + (locationId ?? string.Empty)
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
                    Log("AMBIENT_WORLD_DISPATCH_INTERIOR_PIN_RELEASED",
                        "LiveInteriorId=" + liveInteriorId
                        + "; Native=UNPIN_INTERIOR; Reason=" + (reason ?? string.Empty)
                        + "; Result=Released");
                }
                catch (Exception ex)
                {
                    Log("AMBIENT_WORLD_DISPATCH_INTERIOR_PIN_RELEASE_FAILED",
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
                reason = "Stage=InteriorReadiness; LiveInteriorId=" + expectedInteriorId
                    + "; Native=IS_INTERIOR_READY; Result=Exception:" + ex.GetType().Name;
                return false;
            }
            bool playerInside = IsPlayerInsideLiveInterior(expectedInteriorId);
            if (!interiorReady)
            {
                try { RequestInteriorCollision(insideThreshold); }
                catch (Exception ex)
                {
                    reason = "Stage=InteriorReadiness; LiveInteriorId=" + expectedInteriorId
                        + "; Native=IS_INTERIOR_READY:false; CollisionRequest=Exception:"
                        + ex.GetType().Name;
                    return false;
                }
                if (!playerInside)
                {
                    reason = "PENDING:Stage=InteriorReadiness; LiveInteriorId=" + expectedInteriorId
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
                            reason = "Stage=InteriorFloorNavigation; LiveInteriorId=" + expectedInteriorId
                                + "; SafePositionFound=" + safePositionFoundCount + "/" + safePositionProbeCount
                                + "; GroundHeightFound=" + floorResultCount + "/" + floorProbeCount
                                + "; CollisionRequest=Exception:" + ex.GetType().Name;
                            return false;
                        }
                        reason = "PENDING:Stage=InteriorFloorNavigation; LiveInteriorId=" + expectedInteriorId
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
                            reason = "Stage=InteriorFloorNavigation; LiveInteriorId="
                                + expectedInteriorId + probeResults
                                + "; Native=IS_INTERIOR_READY:false; CollisionRequest=Exception:"
                                + ex.GetType().Name;
                            return false;
                        }
                        reason = "PENDING:Stage=InteriorFloorNavigation; LiveInteriorId="
                            + expectedInteriorId + probeResults
                            + "; Native=IS_INTERIOR_READY:false; CollisionRequest=REQUEST_COLLISION_AT_COORD";
                        return false;
                    }
                    reason = "Stage=InteriorFloorNavigation; LiveInteriorId=" + expectedInteriorId
                        + probeResults
                        + "; Native=GET_SAFE_COORD_FOR_PED/GET_GROUND_HEIGHT_AND_NORMAL"
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
                    DoorHash = doorHash,
                    OriginalDoorState = existingDoorSession == null
                        ? currentDoorState : existingDoorSession.OriginalDoorState,
                    OriginalOpenRatio = existingDoorSession == null
                        ? currentOpenRatio : existingDoorSession.OriginalOpenRatio,
                    StartedAtGameTime = now,
                    LastOpenRequestGameTime = now,
                    LastRequestedOpenRatio = Math.Max(0f, currentOpenRatio),
                    NextDoorUnlockAttemptAtGameTime = now,
                    CrossingSinceGameTime = -1
                };
                _interiorAccessSessions[actor.Handle] = session;

                Log("AMBIENT_WORLD_INTERIOR_DOOR_OPEN_REQUESTED",
                    "Actor=" + actor.Handle
                    + "; DoorEntity=" + doorEntity.Handle
                    + "; DoorHash=" + doorHash
                    + "; Location=" + route.Pair.InsideLocation.Id
                    + "; OutsideCapture=" + route.Pair.OutsidePoint.SourceRecord
                    + "; InsideCapture=" + route.Pair.InsidePoint.SourceRecord
                    + "; MappingBasis=" + route.Pair.MappingBasis
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
                ReleaseInteriorAccessSession(actor.Handle, "ThresholdCrossingTimeout");
                message = "The mapped door remained open, but the actor did not cross the threshold in time.";
                return AmbientInteriorAccessStatus.Failed;
            }

            int actorInteriorId = GetInteriorAt(actor.Position);
            float destinationDistance = actor.Position.DistanceTo(
                session.Route.DestinationPosition);
            float entryDistance = actor.Position.DistanceTo(session.Route.EntryPosition);
            bool crossedThreshold = actorInteriorId == session.Route.TargetLiveInteriorId
                && entryDistance >= Math.Min(1.25f,
                    session.Route.Pair.DistanceMeters * 0.55f)
                && destinationDistance <= InteriorAccessDestinationRadius;
            if (crossedThreshold)
            {
                if (!session.CrossingInteriorObserved)
                {
                    session.CrossingInteriorObserved = true;
                    session.CrossingSinceGameTime = now;
                }
                else if (unchecked(now - session.CrossingSinceGameTime) >= 1200)
                {
                    ReleaseInteriorAccessSession(actor.Handle, "ThresholdCrossingConfirmed");
                    message = session.Route.EnteringInterior
                        ? "The actor crossed the mapped entrance into the interior."
                        : "The actor crossed the mapped entrance back outside.";
                    return AmbientInteriorAccessStatus.Completed;
                }
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

                if (openRatio >= InteriorAccessDoorOpenRatio && !session.ReadyReported)
                {
                    session.ReadyReported = true;
                    Log("AMBIENT_WORLD_INTERIOR_DOOR_OPEN_CONFIRMED",
                        "Actor=" + actor.Handle
                        + "; DoorHash=" + session.DoorHash
                        + "; OpenRatio=" + openRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + "; MappingBasis=" + session.Route.Pair.MappingBasis);
                    message = "The door is open. Walk through it to continue.";
                    return AmbientInteriorAccessStatus.ReadyToCross;
                }

                return openRatio >= InteriorAccessDoorOpenRatio
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

        internal void CancelAllInteriorAccessSessions(string reason)
        {
            foreach (int actorHandle in _interiorAccessSessions.Keys.ToArray())
                ReleaseInteriorAccessSession(actorHandle,
                    string.IsNullOrWhiteSpace(reason) ? "Cancelled" : reason);
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
                in GetInteriorAccessPairsIncludingGunStores())
            {
                if (pair == null || pair.OutsidePoint == null || pair.InsidePoint == null)
                    continue;
                if (position.DistanceTo(pair.OutsidePoint.Position) <= InteriorAccessUseRadius
                    || position.DistanceTo(pair.InsidePoint.Position) <= InteriorAccessUseRadius)
                    return true;
            }
            return false;
        }

        private List<LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair>
            GetInteriorAccessPairsIncludingGunStores()
        {
            if (_locationCatalog == null)
                return new List<LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair>();

            if (ReferenceEquals(_interiorAccessPairsCacheCatalog, _locationCatalog)
                && _interiorAccessPairsIncludingGunStores != null)
                return _interiorAccessPairsIncludingGunStores;

            var accessPairs = new List<LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair>();

            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in _locationCatalog.InteriorAccessPairs)
            {
                if (pair != null)
                    accessPairs.Add(pair);
            }

            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location
                in _locationCatalog.Locations)
            {
                if (location == null
                    || !string.Equals(location.PhysicalType, "GunStore",
                        StringComparison.OrdinalIgnoreCase)
                    || !location.ExteriorVerified
                    || !location.InteriorVerified
                    || !string.Equals(location.IdentityStatus,
                        "OwnerConfirmedReference", StringComparison.OrdinalIgnoreCase))
                    continue;

                LSImmersiveLocationCatalog.LSImmersiveLocationPoint outsidePoint;
                LSImmersiveLocationCatalog.LSImmersiveLocationPoint insidePoint;
                int mappedInteriorId;
                if (!location.TryGetPoint(
                        "ObservedCoordinateCandidate", false, out outsidePoint)
                    || !location.TryGetPoint(
                        "InteriorEgressCandidate", false, out insidePoint)
                    || outsidePoint == null || insidePoint == null
                    || !string.Equals(outsidePoint.PositionContext,
                        "OwnerConfirmedExteriorEntranceReference",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(outsidePoint.InteriorId, "0",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(insidePoint.PositionContext,
                        "OwnerConfirmedInteriorCoordinateReference",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(outsidePoint.SourceRef, location.SourceRef,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(insidePoint.SourceRef, location.SourceRef,
                        StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(insidePoint.InteriorId,
                        NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out mappedInteriorId)
                    || mappedInteriorId == 0)
                    continue;

                // Gun Store egress records identify the GTA interior destination,
                // not a paired physical doorway. Resolve the live registered door
                // at the entrance/egress anchor before exposing the interaction.
                accessPairs.Add(new LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair(
                    location,
                    outsidePoint,
                    location,
                    insidePoint,
                    outsidePoint.Position.DistanceTo(insidePoint.Position),
                    RuntimeGunStoreDoorMappingBasis));
            }

            _interiorAccessPairsCacheCatalog = _locationCatalog;
            _interiorAccessPairsIncludingGunStores = accessPairs;
            return _interiorAccessPairsIncludingGunStores;
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
                    return false;
                interiorId = entityInteriorId;
                if (logIdentityMismatch)
                    Log("AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY",
                        "Player=" + player.Handle
                        + "; EntityLiveInteriorId=" + entityInteriorId
                        + "; CoordinateLiveInteriorId=Unavailable"
                        + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                        + "; CoordinateResult=Exception:" + ex.GetType().Name
                        + "; SelectedLiveInteriorId=" + interiorId
                        + "; Result=EntityFallback");
                return true;
            }

            // Dispatch uses the Player's coordinate identity to match mapped
            // inside points. The entity native is retained as diagnostic
            // evidence because it can report a stale nonzero ID at an exterior
            // threshold.
            interiorId = coordinateInteriorId;
            if (logIdentityMismatch && entityInteriorIdAvailable
                && entityInteriorId != coordinateInteriorId)
            {
                Log("AMBIENT_WORLD_DISPATCH_PLAYER_INTERIOR_IDENTITY",
                    "Player=" + player.Handle
                    + "; EntityLiveInteriorId=" + entityInteriorId
                    + "; CoordinateLiveInteriorId=" + coordinateInteriorId
                    + "; Native=GET_INTERIOR_FROM_ENTITY/GET_INTERIOR_AT_COORDS"
                    + "; SelectedLiveInteriorId=" + interiorId
                    + "; Result=CoordinateIdentitySelected");
            }
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
                && string.Equals(route.Pair.MappingBasis,
                    RuntimeGunStoreDoorMappingBasis,
                    StringComparison.OrdinalIgnoreCase))
            {
                return TryFindGunStoreEndpointDoor(
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
                    if (!TryGetExistingDoorHash(candidate, out candidateDoorHash))
                        continue;

                    doorEntity = candidate;
                    doorHash = candidateDoorHash;
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
            string doorLookupFailure = string.Empty;
            for (int index = 0; index < nearbyProps.Length; index++)
            {
                Prop candidate = nearbyProps[index];
                if (candidate == null || !candidate.Exists())
                    continue;

                int candidateDoorHash;
                try
                {
                    if (!TryGetExistingDoorHash(candidate, out candidateDoorHash))
                        continue;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrWhiteSpace(doorLookupFailure))
                        doorLookupFailure = ex.Message;
                    continue;
                }

                registeredDoorCount++;
                float distanceToThreshold = DistanceToSegment(
                    candidate.Position,
                    outsidePoint,
                    insidePoint);
                if (distanceToThreshold < nearestRegisteredDoorDistance)
                {
                    nearestRegisteredDoorDistance = distanceToThreshold;
                    nearestRegisteredDoor = candidate;
                    nearestRegisteredDoorHash = candidateDoorHash;
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

            reason = "No registered physical door was found across the mapped outside/inside threshold. "
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

        private bool TryFindGunStoreEndpointDoor(
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
                || route.Pair.InsideLocation == null
                || !string.Equals(route.Pair.InsideLocation.PhysicalType,
                    "GunStore", StringComparison.OrdinalIgnoreCase))
            {
                reason = "The Gun Store door candidate is incomplete.";
                return false;
            }

            Vector3 entranceAnchor = route.EntryPosition;
            Prop[] nearbyProps;
            try
            {
                nearbyProps = World.GetNearbyProps(
                    entranceAnchor,
                    GunStoreEndpointDoorPropSearchRadius);
            }
            catch (Exception ex)
            {
                reason = "The Gun Store endpoint-object scan failed: " + ex.Message;
                return false;
            }

            if (nearbyProps == null)
                nearbyProps = new Prop[0];

            int registeredDoorCount = 0;
            float nearestRegisteredDoorDistance = float.MaxValue;
            Prop nearestRegisteredDoor = null;
            int nearestRegisteredDoorHash = 0;
            string doorLookupFailure = string.Empty;
            for (int index = 0; index < nearbyProps.Length; index++)
            {
                Prop candidate = nearbyProps[index];
                if (candidate == null || !candidate.Exists())
                    continue;

                int candidateDoorHash;
                try
                {
                    if (!TryGetExistingDoorHash(candidate, out candidateDoorHash))
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
                }
            }

            if (nearestRegisteredDoor != null
                && nearestRegisteredDoorDistance <= GunStoreEndpointDoorMaxDistance)
            {
                doorEntity = nearestRegisteredDoor;
                doorHash = nearestRegisteredDoorHash;
                Log("AMBIENT_WORLD_GUN_STORE_DOOR_FOUND_AT_ENDPOINT",
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

            reason = "No registered physical Gun Store door was found at the actor's catalogued entrance/egress anchor. "
                + "NearbyProps=" + nearbyProps.Length
                + "; RegisteredDoorCandidates=" + registeredDoorCount
                + "; NearestRegisteredDoorDistance="
                + (nearestRegisteredDoor == null
                    ? "none"
                    : nearestRegisteredDoorDistance.ToString("0.00", CultureInfo.InvariantCulture))
                + (string.IsNullOrWhiteSpace(doorLookupFailure)
                    ? string.Empty
                    : "; DoorLookupError=" + doorLookupFailure);
            Log("AMBIENT_WORLD_GUN_STORE_DOOR_NOT_FOUND_AT_ENDPOINT",
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
            string requiredInteriorLocationId = null,
            string requiredPhysicalType = null,
            string requiredLocationContext = null,
            bool requiresMappedInterior = false,
            int playerLiveInteriorId = 0,
            bool playerLiveInteriorIdAvailable = false)
        {
            scene = null;
            reason = string.Empty;
            if (_locationCatalog == null)
            {
                reason = "The existing LSImmersiveLocation catalog is unavailable.";
                return false;
            }

            int resolvedPlayerInteriorId = playerLiveInteriorIdAvailable
                ? playerLiveInteriorId : GetInteriorAt(playerPosition);

            List<LSPDDispatchLocationDefinition> registeredAreas = (eventAreas
                ?? Enumerable.Empty<LSPDDispatchLocationDefinition>())
                .Where(area => area != null)
                .GroupBy(area => area.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            List<LSPDDispatchLocationDefinition> eligibleAreas = registeredAreas
                .Where(area => area.IsEligibleFor(playerPosition))
                .ToList();
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
                    in GetInteriorAccessPairsIncludingGunStores())
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
                            out insideOnlyFailure))
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
                    requiredPhysicalType))
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
                        out namedPointFailure))
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
                    out reason);
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
                    false))
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
            IEnumerable<LSImmersiveLocationCatalog.LSImmersiveLocationDefinition> dispatchCatalogLocations =
                _locationCatalog.FindByCategory("SurveyedCandidate");
            if (useNearbyCatalogFallback)
                dispatchCatalogLocations = dispatchCatalogLocations.Concat(
                    _locationCatalog.FindByCategory("AreaCandidate"));

            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location
                in dispatchCatalogLocations)
            {
                if (IsRejectedSurveyRecord(location)
                    || !IsCompatibleWithIncident(location, incidentType)
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
                List<DispatchLocationCandidate> fallbackCandidates = nearbyFallbackCandidates
                    .Where(candidate => !_recentLocationIdSet.Contains(candidate.Location.Id))
                    .ToList();
                if (fallbackCandidates.Count == 0)
                    fallbackCandidates = nearbyFallbackCandidates;
                Shuffle(fallbackCandidates);

                foreach (DispatchLocationCandidate candidate in fallbackCandidates)
                {
                    if (checkedNearbyFallbackCandidates++ >= MaximumSurveyCandidatesToValidate)
                        break;

                    if (!MatchesDispatchLocationContext(
                        candidate.Location,
                        requiredLocationContext,
                        candidate.Point.Position))
                        continue;

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
                        GetDispatchExteriorRoadHeightTolerance(candidate.Area),
                        out resolved,
                        out candidateFailure)
                        || resolved == null
                        || Distance2D(resolved.SceneCenter, playerPosition)
                            > LSPDDispatchLocationDefinition.NearbyCatalogFallbackMaximumDistance)
                    {
                        if (string.IsNullOrWhiteSpace(firstCandidateFailure))
                            firstCandidateFailure = "Location=" + candidate.Location.Id
                                + "; Point=" + candidate.Point.Id
                                + "; " + candidateFailure;
                        continue;
                    }

                    scene = resolved;
                    scene.LocationReviewStatus = candidate.Location.ReviewStatus;
                    scene.Area = LSPDDispatchLocationDefinition.ForNearbyCatalogCandidate(
                        candidate.Location.Id,
                        resolved.SceneCenter,
                        requiredLocationContext);
                    scene.AreaLocationId = candidate.Location.Id;
                    scene.AreaAnchorPosition = resolved.SceneCenter;
                    RememberLocation(candidate.Location.Id);
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
                return false;
            }

            // Prefer a different surveyed physical site when the catalog has one;
            // selection is still random among eligible sites, not capture-order based.
            List<DispatchLocationCandidate> freshCandidates = candidates
                .Where(candidate => !_recentLocationIdSet.Contains(candidate.Location.Id))
                .ToList();
            if (freshCandidates.Count > 0)
                candidates = freshCandidates;
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
                if (checkedAreaCandidates++ >= MaximumSurveyCandidatesToValidate)
                    break;

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
                    GetDispatchExteriorRoadHeightTolerance(candidate.Area),
                    out resolved,
                    out candidateFailure))
                {
                    if (string.IsNullOrWhiteSpace(firstCandidateFailure))
                        firstCandidateFailure = "Location=" + candidate.Location.Id
                            + "; Point=" + candidate.Point.Id
                            + "; " + candidateFailure;
                    continue;
                }

                scene = resolved;
                scene.LocationReviewStatus = candidate.Location.ReviewStatus;
                RememberLocation(candidate.Location.Id);
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
                LSImmersiveLocationCatalog.LSImmersiveLocationDefinition anchorLocation;
                string anchorName = _locationCatalog.TryGetLocation(area.Id, out anchorLocation)
                    ? anchorLocation.Name : area.Id;
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
                    GetDispatchExteriorRoadHeightTolerance(area),
                    out resolved,
                    out fallbackFailure))
                {
                    if (string.IsNullOrWhiteSpace(firstCandidateFailure))
                        firstCandidateFailure = "Location=" + area.Id
                            + "; Point=AreaSearchAnchor; " + fallbackFailure;
                    continue;
                }

                scene = resolved;
                scene.LocationReviewStatus = "AreaCandidate_NotNamedSite";
                LogLocationResolution(scene, requiresVehicle);
                return true;
            }

            reason = candidates.Count == 0
                ? "No surveyed foot candidate matched the event area; no nearby AreaSearchAnchor resolved to a safe ped position."
                : "Surveyed candidates and their AreaSearchAnchor fallbacks failed runtime ground, exterior, occupancy, or vehicle-staging checks.";
            Log("AMBIENT_WORLD_LOCATION_QUERY_REJECTED",
                "Candidates=" + candidates.Count
                + "; Areas=" + eligibleAreas.Count
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
            string requiredLocationContext = null)
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

            LSImmersiveLocationCatalog.LSImmersiveLocationDefinition previousLocation;
            if (!string.IsNullOrWhiteSpace(requiredPhysicalType)
                && (_locationCatalog == null
                    || !_locationCatalog.TryGetLocation(previous.LocationId, out previousLocation)
                    || !MatchesRequiredPhysicalType(previousLocation, requiredPhysicalType)))
            {
                reason = "The selected site no longer matches the event's required physical type.";
                return false;
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
                previous.ExteriorRoadHeightTolerance,
                out refreshed,
                out reason);
            if (resolved && refreshed != null)
                refreshed.LocationReviewStatus = previous.LocationReviewStatus;
            return resolved;
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
                float correctionLimit = 2.75f;
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
                string candidateFailure;
                placementProbeCount++;
                if (!TryValidateOutdoorPedPosition(
                        candidate,
                        correctionLimit,
                        scene == null ? 0f : scene.ExteriorRoadHeightTolerance,
                        out resolved,
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
                string candidateFailure;
                if (!TryValidateOutdoorPedPosition(
                        candidate,
                        DispatchVictimFallbackCorrectionDistance,
                        scene == null ? 0f : scene.ExteriorRoadHeightTolerance,
                        out resolved,
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
                    + "; StartsDead=" + startsDead
                    + "; Interior=" + scene.IsInteriorScene
                    + "; CatalogInteriorId=" + scene.CatalogInteriorId
                    + "; LiveInteriorId=" + scene.LiveInteriorId
                    + "; ActorLiveInteriorId=" + (scene.IsInteriorScene
                        ? scene.LiveInteriorId : 0));
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
        /// protected gameplay states. Dispatch remains the behavior owner while
        /// the claim is active; ReleaseDispatchCivilian restores the original
        /// entity flags without deleting the ambient Ped.
        /// </summary>
        internal bool TryClaimDispatchCivilian(
            string incidentId,
            string victimPoolId,
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            IEnumerable<Ped> protectedActors,
            IEnumerable<Vector3> reservedSuspectPositions,
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
                if (!_npcDatabase.IsDispatchPedModelInPool(victimPoolId, candidate.Model.Hash))
                    continue;

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
                    + "; Reason=NO_SAFE_UNOWNED_CIVILIAN");
                return false;
            }

            DispatchCivilianClaim claim = null;
            try
            {
                claim = new DispatchCivilianClaim
                {
                    IncidentId = incidentId,
                    PedHandle = selected.Handle,
                    WasPersistent = selected.IsPersistent,
                    BlockedPermanentEvents = selected.BlockPermanentEvents
                };
                _dispatchCivilianClaims.Add(selected.Handle, claim);
                selected.IsPersistent = true;
                selected.BlockPermanentEvents = true;
                civilian = selected;
                Log("AMBIENT_WORLD_DISPATCH_CIVILIAN_CLAIMED",
                    "Incident=" + incidentId
                    + "; Ped=" + selected.Handle
                    + "; Distance=" + selectedDistance.ToString("0.0", CultureInfo.InvariantCulture)
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
            out string reason)
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
                in GetInteriorAccessPairsIncludingGunStores())
            {
                if (pair == null || pair.InsideLocation == null
                    || pair.OutsidePoint == null || pair.InsidePoint == null)
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
                        out insideOnlyFailure))
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
                    try { RequestInteriorCollision(candidate.Item1.InsidePoint.Position); }
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
                // An active Player inside the matching live interior can be
                // validated through its authored floor and navigation below.
                // Off-site candidates still wait for GTA's readiness result.
                bool playerInsideMappedInterior = playerInteriorId != 0
                    && liveInteriorId == playerInteriorId;
                bool interiorReady;
                try
                {
                    interiorReady = IsInteriorReady(liveInteriorId);
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
                    if (!playerInsideMappedInterior)
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

                    try { RequestInteriorCollision(candidate.Item1.InsidePoint.Position); }
                    catch (Exception ex)
                    {
                        pendingReason = "Stage=InteriorReadiness; Location="
                            + candidate.Item1.InsideLocation.Id
                            + "; CatalogInteriorId=" + candidate.Item1.InteriorId
                            + "; LiveInteriorId=" + liveInteriorId
                            + "; Native=REQUEST_COLLISION_AT_COORD; Result=Exception:"
                            + ex.GetType().Name;
                        continue;
                    }
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
                    interiorReady || playerInsideMappedInterior))
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
                if (!interiorReady && playerInsideMappedInterior)
                {
                    Log("AMBIENT_WORLD_INTERIOR_FLOOR_VALIDATED_WITH_ACTIVE_PLAYER",
                        "Location=" + candidateScene.LocationId
                        + "; CatalogInteriorId=" + candidateScene.CatalogInteriorId
                        + "; LiveInteriorId=" + candidateScene.LiveInteriorId
                        + "; PlayerLiveInteriorId=" + playerInteriorId
                        + "; Native=IS_INTERIOR_READY:false"
                        + "; Evidence=MappedThreshold+SafePedNavigation+GroundHeight+InteriorIdentity");
                }
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
                    out insideOnlyPointFailure))
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
            string requiredPhysicalType = null)
        {
            scene = null;
            reason = "The authored interior site has no usable mapped threshold pair.";
            string pendingReason = string.Empty;
            if (_locationCatalog == null
                || string.IsNullOrWhiteSpace(requiredInteriorLocationId))
                return false;

            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in GetInteriorAccessPairsIncludingGunStores().Where(value => value != null
                    && value.InsideLocation != null
                    && value.InsidePoint != null
                    && value.OutsidePoint != null
                    && HasCatalogedMappedInteriorReference(value)
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
                    true))
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
            bool interiorReadinessGateSatisfied = false)
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
                    interiorReady = IsInteriorReady(interiorId);
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
                    if (!playerInside)
                    {
                        if (requiredPair == null
                            || !HasCatalogedMappedInteriorReference(requiredPair))
                        {
                            reason = "Stage=InteriorStreamingRequest; Location="
                                + (requiredPair == null || requiredPair.InsideLocation == null
                                    ? string.Empty : requiredPair.InsideLocation.Id)
                                + "; CatalogInteriorId="
                                + (requiredPair == null ? string.Empty : requiredPair.InteriorId)
                                + "; LiveInteriorId=" + interiorId
                                + "; Result=NoValidatedMappedPairForStreaming";
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

                    try { RequestInteriorCollision(readinessProbe); }
                    catch (Exception ex)
                    {
                        reason = "Stage=InteriorReadiness; Location="
                            + (requiredPair == null || requiredPair.InsideLocation == null
                                ? string.Empty : requiredPair.InsideLocation.Id)
                            + "; CatalogInteriorId="
                            + (requiredPair == null ? string.Empty : requiredPair.InteriorId)
                            + "; LiveInteriorId=" + interiorId
                            + "; Native=IS_INTERIOR_READY:false; CollisionRequest=Exception:"
                            + ex.GetType().Name;
                        return false;
                    }
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
                requiredPair == null ? GetInteriorAccessPairsIncludingGunStores() : null;
            int accessPairCount = requiredPair == null ? accessPairs.Count : 1;
            for (int accessPairIndex = 0; accessPairIndex < accessPairCount; accessPairIndex++)
            {
                LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair =
                    requiredPair ?? accessPairs[accessPairIndex];
                if (pair == null || pair.InsideLocation == null || pair.InsidePoint == null
                    || (requiredPair != null && !ReferenceEquals(pair, requiredPair))
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
            float maximumStreetHeightDelta,
            out AmbientDispatchSceneResolution scene,
            out string reason)
        {
            scene = null;
            reason = string.Empty;
            Vector3 safePedPosition;
            if (!TryValidateOutdoorPedPosition(
                authoredPosition,
                maximumSnapDistance,
                maximumStreetHeightDelta,
                out safePedPosition,
                out reason))
                return false;

            if (area == null
                || (enforcePlayerDistanceWindow
                    && (!area.IsEligibleFor(playerPosition)
                        || !IsWithinPlayerDistance(area, safePedPosition, playerPosition))))
            {
                reason = "Stage=SceneAreaRange; Result=Runtime-safe point falls outside the event's authored player-distance window.";
                return false;
            }

            Vector3 vehiclePosition = Vector3.Zero;
            float vehicleHeading = 0f;
            bool hasVehiclePosition = false;
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
                Heading = heading,
                VehicleStagingPosition = vehiclePosition,
                VehicleHeading = vehicleHeading,
                HasVehicleStagingPosition = hasVehiclePosition,
                IsSurveyedPoint = surveyedPoint,
                MaximumSnapDistance = maximumSnapDistance,
                ExteriorRoadHeightTolerance = maximumStreetHeightDelta,
                IsInteriorScene = false,
                CatalogInteriorId = string.Empty,
                LiveInteriorId = 0,
                InteriorAccessPair = null,
                ValidationStatus = surveyedPoint
                    ? "RuntimeValidatedForCurrentScene"
                    : "RuntimeResolvedAreaCandidate"
            };
            return true;
        }

        private bool TryValidateOutdoorPedPosition(
            Vector3 authoredPosition,
            float maximumCorrectionDistance,
            float maximumStreetHeightDelta,
            out Vector3 safePosition,
            out string reason)
        {
            safePosition = authoredPosition;
            reason = string.Empty;
            try
            {
                // Interior captures are not promoted to AI-accessible interiors.
                // The current dispatch path is deliberately exterior-only.
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
                if (safe.DistanceTo(authoredPosition) > maximumCorrectionDistance)
                {
                    reason = "Stage=ExteriorNavigationSnap; Native=GET_SAFE_COORD_FOR_PED"
                        + "; Requested=" + authoredPosition
                        + "; Resolved=" + safe
                        + "; Distance=" + safe.DistanceTo(authoredPosition).ToString(
                            "0.00", CultureInfo.InvariantCulture)
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

                if (HasOverlappingPed(safe, 1.35f))
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

        private static bool HasOverlappingPed(Vector3 position, float minimumSeparation)
        {
            foreach (Ped ped in World.GetNearbyPeds(position, minimumSeparation))
            {
                if (ped == null || !ped.Exists())
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
            int mappedInteriorId;
            return pair != null
                && int.TryParse(pair.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out mappedInteriorId)
                && mappedInteriorId != 0;
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
            string preferredInteriorLocationId = null)
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
            out string reason)
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
                    preferredInteriorLocationId))
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
            bool playerInteriorIdAvailable)
        {
            string ignoredReason;
            return HasNearbyCompatibleDispatchCatalogLocation(
                definition,
                playerPosition,
                playerInteriorId,
                playerInteriorIdAvailable,
                out ignoredReason);
        }

        internal bool HasNearbyCompatibleDispatchCatalogLocation(
            LSPDDispatchEventDefinition definition,
            Vector3 playerPosition,
            int playerInteriorId,
            bool playerInteriorIdAvailable,
            out string reason)
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
                int firstMappedInsideLiveId = int.MinValue;
                string firstMappedInsideNativeResult = string.Empty;
                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                    in GetInteriorAccessPairsIncludingGunStores())
                {
                    if (pair == null || pair.InsideLocation == null
                        || pair.OutsidePoint == null || pair.InsidePoint == null
                        || !string.Equals(pair.InsideLocation.Id,
                            definition.InteriorLocationId,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    exactLocationPairCount++;
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
                        out insideOnlyFailure))
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
                int firstMappedInsideLiveId = int.MinValue;
                string firstMappedInsideNativeResult = string.Empty;
                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                    in GetInteriorAccessPairsIncludingGunStores())
                {
                    if (pair == null || pair.InsideLocation == null
                        || pair.OutsidePoint == null || pair.InsidePoint == null
                        || !HasCatalogedMappedInteriorReference(pair))
                        continue;
                    accessPairCount++;
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
                        out insideOnlyFailure))
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
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location in locations)
            {
                if (IsRejectedSurveyRecord(location)
                    || !IsCompatibleWithIncident(location, definition.IncidentType)
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
                + "; VehicleRequired=" + requiresVehicle
                + "; VehicleStaging=" + (scene.HasVehicleStagingPosition
                    ? scene.VehicleStagingPosition.ToString() : "Unavailable")
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
        }

        internal int RegisteredActorCount
        {
            get { return _actors.Count; }
        }

        internal IEnumerable<AmbientActorContext> Actors
        {
            get { return _actors.Values; }
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

        /// <summary>
        /// Starts a catalog-authored paired animation for two live Dispatch-owned
        /// scene actors. This is an explicit scene setup call: it never scans or
        /// adopts ambient peds, and it leaves Dispatch in charge of the actors.
        /// </summary>
        internal AmbientDispatchAnimationStartResult TryStartDispatchPairAnimation(
            LSPDDispatchEvent incident,
            Ped primaryActor,
            Ped secondaryActor,
            out string reason)
        {
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

            float actorDistance = primaryActor.Position.DistanceTo(secondaryActor.Position);
            if (actorDistance < 0.65f || actorDistance > 2.5f)
            {
                reason = "The paired scene actors are not close enough for the authored interaction.";
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
                    reason = "Waiting for animation dictionary " + incident.SceneAnimationDictionary + ".";
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
        internal int DoorHash { get; set; }
        internal int OriginalDoorState { get; set; }
        internal float OriginalOpenRatio { get; set; }
        internal float LastRequestedOpenRatio { get; set; }
        internal int StartedAtGameTime { get; set; }
        internal int LastOpenRequestGameTime { get; set; }
        internal int NextDoorUnlockAttemptAtGameTime { get; set; }
        internal int DoorUnlockAttempts { get; set; }
        internal int CrossingSinceGameTime { get; set; }
        internal bool ReadyReported { get; set; }
        internal bool CrossingInteriorObserved { get; set; }
        internal bool DoorPhysicsWaitLogged { get; set; }
        internal bool DoorUnlockConfirmed { get; set; }
        internal bool DoorUnlockFailureLogged { get; set; }
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
        internal bool HasVehicleStagingPosition { get; set; }
        internal bool IsSurveyedPoint { get; set; }
        internal float MaximumSnapDistance { get; set; }
        internal float ExteriorRoadHeightTolerance { get; set; }
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
    }
}
