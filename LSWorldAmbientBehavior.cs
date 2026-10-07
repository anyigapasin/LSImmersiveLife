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
        private const int InteriorAccessTimeoutMilliseconds = 30000;
        private const int InteriorAccessDoorOpenStepMilliseconds = 80;
        private const float InteriorAccessDoorOpenStep = 0.10f;
        private const int InteriorAccessDoorUnlockRetryMilliseconds = 250;
        private const int InteriorAccessDoorUnlockMaximumAttempts = 8;
        private const float InteriorActivityMinimumDepth = 1.75f;
        private const float InteriorActivityMaximumDepth = 7f;
        private const int UnlockedDoorSystemState = 0;
        private const int UnlockedThisFrameDoorSystemState = 3;
        private const float SurveyPointAreaRadius = 650f;
        private const float MaximumSurveyPedSnapDistance = 4f;
        private const float MaximumAreaAnchorSnapDistance = 90f;
        private const int MaximumSurveyCandidatesToValidate = 20;
        private const int RecentLocationHistoryLimit = 8;

        private readonly Dictionary<int, AmbientActorContext> _actors =
            new Dictionary<int, AmbientActorContext>();

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
        private LSImmersiveLocationCatalog _locationCatalog;
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

        internal void UpdateLocationCatalog(LSImmersiveLocationCatalog locationCatalog)
        {
            _locationCatalog = locationCatalog;
        }

        /// <summary>
        /// Shows and handles the Player's physical threshold action. A mapped
        /// coordinate pair only points to the candidate threshold; the door must
        /// be found by raycast and the actor must physically reach the other
        /// interior context before this operation completes.
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
                + "; MappingBasis=" + route.Pair.MappingBasis);

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
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in _locationCatalog.InteriorAccessPairs)
            {
                int pairInteriorId;
                if (pair == null || !int.TryParse(
                    pair.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out pairInteriorId)
                    || pairInteriorId == 0)
                    continue;

                Vector3 outsideProbe = ResolveOutsideInteriorProbe(pair);
                int liveOutsideInteriorId = GetInteriorAt(outsideProbe);
                int liveInsideInteriorId = GetInteriorAt(pair.InsidePoint.Position);
                if (liveInsideInteriorId == 0
                    || liveOutsideInteriorId == liveInsideInteriorId)
                    continue;

                float outsideDistance = actor.Position.DistanceTo(
                    pair.OutsidePoint.Position);
                float insideDistance = actor.Position.DistanceTo(
                    pair.InsidePoint.Position);
                bool enteringInterior = outsideDistance <= InteriorAccessUseRadius
                    && (outsideDistance + 0.35f < insideDistance
                        || actorCoordinateInteriorId == liveOutsideInteriorId);
                bool exitingInterior = insideDistance <= InteriorAccessUseRadius
                    && (insideDistance + 0.35f < outsideDistance
                        || actorCoordinateInteriorId == liveInsideInteriorId);
                if (!enteringInterior && !exitingInterior)
                    continue;
                if (enteringInterior && exitingInterior)
                    enteringInterior = outsideDistance <= insideDistance;

                Vector3 entryPosition = enteringInterior
                    ? pair.OutsidePoint.Position
                    : pair.InsidePoint.Position;
                Vector3 destinationPosition = enteringInterior
                    ? Vector3.Zero
                    : outsideProbe;
                int currentInteriorId = enteringInterior
                    ? liveOutsideInteriorId
                    : liveInsideInteriorId;
                int targetInteriorId = enteringInterior
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
                    currentInteriorId,
                    targetInteriorId,
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
            int targetInteriorId,
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
            if (targetInteriorId == 0 || _locationCatalog == null)
            {
                reason = targetInteriorId == 0
                    ? "The requested interior ID is invalid."
                    : "The existing LSImmersiveLocation catalog is unavailable.";
                return false;
            }

            int actorCoordinateInteriorId = GetInteriorAt(actor.Position);
            if (!enteringInterior && actorCoordinateInteriorId != targetInteriorId)
            {
                reason = "The actor is not inside the requested interior.";
                return false;
            }

            AmbientInteriorAccessRoute nearest = null;
            float nearestDistance = float.MaxValue;
            string walkableFailure = string.Empty;
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in _locationCatalog.InteriorAccessPairs)
            {
                if (pair == null || pair.OutsidePoint == null || pair.InsidePoint == null)
                    continue;

                Vector3 outsideProbe = ResolveOutsideInteriorProbe(pair);
                int liveOutsideInteriorId = GetInteriorAt(outsideProbe);
                int liveInsideInteriorId = GetInteriorAt(pair.InsidePoint.Position);
                if (liveInsideInteriorId != targetInteriorId
                    || liveOutsideInteriorId == liveInsideInteriorId)
                    continue;

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

        private bool TryResolveInteriorWalkablePosition(
            Vector3 insideThreshold,
            int expectedInteriorId,
            Vector3 outsideEntry,
            out Vector3 safePosition,
            out string reason)
        {
            safePosition = Vector3.Zero;
            reason = "No nearby walkable position was found inside the mapped interior.";
            if (expectedInteriorId == 0)
            {
                reason = "The mapped interior has no valid interior ID.";
                return false;
            }

            try
            {
                var probes = new List<Vector3>();
                for (float radius = InteriorActivityMinimumDepth;
                    radius <= InteriorActivityMaximumDepth;
                    radius += 1.25f)
                {
                    for (int direction = 0; direction < 8; direction++)
                    {
                        double angle = direction * (Math.PI / 4.0);
                        probes.Add(new Vector3(
                            insideThreshold.X + ((float)Math.Cos(angle) * radius),
                            insideThreshold.Y + ((float)Math.Sin(angle) * radius),
                            insideThreshold.Z));
                    }
                }

                Vector3 best = Vector3.Zero;
                bool foundBest = false;
                float bestDistance = float.MaxValue;
                foreach (Vector3 probe in probes)
                {
                    Vector3 safe = probe;
                    if (!World.GetSafePositionForPed(
                        probe,
                        out safe,
                        (GetSafePositionFlags)0))
                        continue;

                    float correctionDistance = safe.DistanceTo(probe);
                    float interiorDepth = safe.DistanceTo(insideThreshold);
                    if (float.IsNaN(correctionDistance)
                        || float.IsInfinity(correctionDistance)
                        || correctionDistance > 2.25f
                        || interiorDepth < InteriorActivityMinimumDepth
                        || interiorDepth > InteriorActivityMaximumDepth
                        || safe.DistanceTo(outsideEntry) < InteriorActivityMinimumDepth
                        || GetInteriorAt(safe) != expectedInteriorId)
                        continue;

                    float groundZ;
                    Vector3 groundNormal;
                    if (!World.GetGroundHeightAndNormal(safe, out groundZ, out groundNormal)
                        || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                        || groundNormal.Z < 0.55f
                        || Math.Abs(safe.Z - groundZ) > 3f)
                        continue;
                    safe.Z = groundZ;
                    if (GetInteriorAt(safe) != expectedInteriorId
                        || HasOverlappingPed(safe, 1.35f)
                        || HasOverlappingVehicle(safe, 1.8f))
                        continue;

                    float distance = safe.DistanceTo(insideThreshold);
                    if (distance >= bestDistance)
                        continue;
                    best = safe;
                    bestDistance = distance;
                    foundBest = true;
                }

                if (!foundBest)
                {
                    reason = "GTA found no clear walkable ground 1.75 to 7 metres inside the mapped doorway.";
                    return false;
                }

                safePosition = best;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = "Interior walkable-position search failed: " + ex.GetType().Name;
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
            bool crossedThreshold = actorInteriorId == session.Route.TargetInteriorId
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
                in _locationCatalog.InteriorAccessPairs)
            {
                if (pair == null || pair.OutsidePoint == null || pair.InsidePoint == null)
                    continue;
                if (position.DistanceTo(pair.OutsidePoint.Position) <= InteriorAccessUseRadius
                    || position.DistanceTo(pair.InsidePoint.Position) <= InteriorAccessUseRadius)
                    return true;
            }
            return false;
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
                    if (Function.Call<int>(Hash.GET_ENTITY_TYPE, candidate) != 3)
                        continue;

                    int candidateDoorHash;
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
                        candidateDoorHash = doorOutput.GetResult<int>();
                    }
                    if (!found || candidateDoorHash == 0)
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

            reason = "No registered physical door was found across the mapped outside/inside threshold.";
            return false;
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
            out string reason)
        {
            scene = null;
            reason = string.Empty;
            if (_locationCatalog == null)
            {
                reason = "The existing LSImmersiveLocation catalog is unavailable.";
                return false;
            }

            List<LSPDDispatchLocationDefinition> eligibleAreas = (eventAreas
                ?? Enumerable.Empty<LSPDDispatchLocationDefinition>())
                .Where(area => area != null && area.IsEligibleFor(playerPosition))
                .ToList();
            if (eligibleAreas.Count == 0)
            {
                reason = "The selected event has no area anchor in its player-distance window.";
                return false;
            }

            // When the Player is already inside a matched location, resolve a
            // scene against that exact interior before considering exterior
            // survey points. The Player crosses the door themselves; this only
            // validates the floor and supplies a placement plan for Dispatch.
            int playerInteriorId = GetInteriorAt(playerPosition);
            if (playerInteriorId != 0)
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
                    out interiorScene,
                    out interiorFailure))
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
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationDefinition location
                in _locationCatalog.FindByCategory("SurveyedCandidate"))
            {
                if (IsRejectedSurveyRecord(location)
                    || !IsCompatibleWithIncident(location, incidentType))
                    continue;

                foreach (LSImmersiveLocationCatalog.LSImmersiveLocationPoint point
                    in location.FindPoints("ObservedCoordinateCandidate", false))
                {
                    if (HasObservedNonZeroInterior(point.InteriorId))
                        continue;

                    LSPDDispatchLocationDefinition nearestArea = null;
                    float nearestAreaDistance = float.MaxValue;
                    foreach (LSPDDispatchLocationDefinition area in eligibleAreas)
                    {
                        float distance = Distance2D(point.Position, area.Position);
                        if (distance > SurveyPointAreaRadius
                            || !IsWithinPlayerDistance(area, point.Position, playerPosition)
                            || distance >= nearestAreaDistance)
                            continue;

                        nearestArea = area;
                        nearestAreaDistance = distance;
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
                }
            }

            // Prefer a different surveyed physical site when the catalog has one;
            // selection is still random among eligible sites, not capture-order based.
            List<DispatchLocationCandidate> freshCandidates = candidates
                .Where(candidate => !_recentLocationIdSet.Contains(candidate.Location.Id))
                .ToList();
            if (freshCandidates.Count > 0)
                candidates = freshCandidates;
            Shuffle(candidates);

            if (RequiresNamedLocation(incidentType) && candidates.Count == 0)
            {
                reason = "No surveyed point matched the exact physical type required for this named-site event.";
                Log("AMBIENT_WORLD_LOCATION_QUERY_REJECTED",
                    "IncidentType=" + (incidentType ?? string.Empty)
                    + "; Reason=" + reason
                    + "; RequiredIdentity=NamedBankOrStore"
                    + "; CandidateSiteCount=0");
                return false;
            }

            int checkedCandidates = 0;
            foreach (DispatchLocationCandidate candidate in candidates)
            {
                if (checkedCandidates++ >= MaximumSurveyCandidatesToValidate)
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
                    out resolved,
                    out candidateFailure))
                {
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
            foreach (LSPDDispatchLocationDefinition area in RequiresNamedLocation(incidentType)
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
                    out resolved,
                    out fallbackFailure))
                    continue;

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
            out string reason)
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
                return TryResolveInteriorDispatchScene(
                    new[] { previous.Area },
                    previous.IncidentType,
                    playerPosition,
                    previous.InteriorId,
                    requiresVehicle,
                    previous.InteriorAccessPair,
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
                out refreshed,
                out reason);
            if (resolved && refreshed != null)
                refreshed.LocationReviewStatus = previous.LocationReviewStatus;
            return resolved;
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
                out safePosition,
                out reason);
        }

        internal bool TryResolveDispatchParticipantPosition(
            Vector3 preferredPosition,
            AmbientDispatchSceneResolution scene,
            out Vector3 safePosition,
            out string reason)
        {
            if (scene != null && scene.IsInteriorScene)
                return TryResolveInteriorNearbyPedPosition(
                    preferredPosition,
                    scene.InteriorId,
                    out safePosition,
                    out reason);
            return TryResolveNearbyPedPosition(preferredPosition, out safePosition, out reason);
        }

        internal bool TryResolveInteriorNearbyPedPosition(
            Vector3 preferredPosition,
            int expectedInteriorId,
            out Vector3 safePosition,
            out string reason)
        {
            safePosition = Vector3.Zero;
            reason = "No clear walkable floor was found at the requested interior participant position.";
            if (expectedInteriorId == 0 || GetInteriorAt(preferredPosition) != expectedInteriorId)
            {
                reason = "The requested participant position is not inside the expected live interior.";
                return false;
            }

            try
            {
                float[] radii = { 0f, 0.6f, 1.15f, 1.7f, 2.25f, 2.75f };
                Vector3 best = Vector3.Zero;
                float bestDistance = float.MaxValue;
                bool found = false;
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
                        if (!World.GetSafePositionForPed(probe, out safe, (GetSafePositionFlags)0)
                            || safe.DistanceTo(probe) > 1.25f
                            || GetInteriorAt(safe) != expectedInteriorId)
                            continue;

                        float groundZ;
                        Vector3 groundNormal;
                        if (!World.GetGroundHeightAndNormal(safe, out groundZ, out groundNormal)
                            || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                            || groundNormal.Z < 0.55f || Math.Abs(safe.Z - groundZ) > 2.5f)
                            continue;
                        safe.Z = groundZ;
                        if (GetInteriorAt(safe) != expectedInteriorId
                            || HasOverlappingPed(safe, 1.2f)
                            || HasOverlappingVehicle(safe, 1.6f))
                            continue;

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

                if (!found)
                    return false;
                safePosition = best;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = "Interior participant ground validation failed: " + ex.GetType().Name;
                return false;
            }
        }

        private bool TryResolveInteriorDispatchScene(
            IEnumerable<LSPDDispatchLocationDefinition> eventAreas,
            string incidentType,
            Vector3 playerPosition,
            int interiorId,
            bool requiresVehicle,
            LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair requiredPair,
            out AmbientDispatchSceneResolution scene,
            out string reason)
        {
            scene = null;
            reason = "No validated interior threshold and eligible event area matched the Player's current location.";
            if (_locationCatalog == null || interiorId == 0
                || GetInteriorAt(playerPosition) != interiorId)
            {
                reason = "The Player is no longer in the requested mapped interior.";
                return false;
            }

            List<LSPDDispatchLocationDefinition> eligibleAreas = (eventAreas
                ?? Enumerable.Empty<LSPDDispatchLocationDefinition>())
                .Where(area => area != null && area.IsEligibleFor(playerPosition))
                .ToList();
            if (eligibleAreas.Count == 0)
            {
                reason = "The event has no eligible area anchor for this interior.";
                return false;
            }

            var candidates = new List<Tuple<
                LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair,
                LSPDDispatchLocationDefinition,
                float>>();
            foreach (LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair pair
                in _locationCatalog.InteriorAccessPairs)
            {
                if (pair == null || pair.InsideLocation == null || pair.InsidePoint == null
                    || (requiredPair != null && !ReferenceEquals(pair, requiredPair))
                    || IsRejectedSurveyRecord(pair.InsideLocation)
                    || !IsCompatibleWithIncident(pair.InsideLocation, incidentType))
                    continue;

                Vector3 outsideProbe = ResolveOutsideInteriorProbe(pair);
                int liveOutsideInteriorId = GetInteriorAt(outsideProbe);
                int liveInsideInteriorId = GetInteriorAt(pair.InsidePoint.Position);
                if (liveInsideInteriorId != interiorId
                    || liveOutsideInteriorId == liveInsideInteriorId)
                    continue;

                foreach (LSPDDispatchLocationDefinition area in eligibleAreas)
                {
                    float areaDistance = Distance2D(pair.OutsidePoint.Position, area.Position);
                    if (areaDistance > SurveyPointAreaRadius
                        || !IsWithinPlayerDistance(area, pair.InsidePoint.Position, playerPosition))
                        continue;
                    candidates.Add(Tuple.Create(pair, area, areaDistance));
                }
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
                    out floorFailure))
                {
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
                        true,
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
                    InteriorId = interiorId,
                    InteriorAccessPair = candidate.Item1,
                    IncidentType = incidentType ?? string.Empty
                };
                reason = string.Empty;
                return true;
            }
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
            out AmbientDispatchSceneResolution scene,
            out string reason)
        {
            scene = null;
            reason = string.Empty;
            Vector3 safePedPosition;
            if (!TryValidateOutdoorPedPosition(
                authoredPosition,
                maximumSnapDistance,
                out safePedPosition,
                out reason))
                return false;

            if (area == null
                || (enforcePlayerDistanceWindow
                    && (!area.IsEligibleFor(playerPosition)
                        || !IsWithinPlayerDistance(area, safePedPosition, playerPosition))))
            {
                reason = "The runtime-safe point falls outside the event's authored player-distance window.";
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
                IsInteriorScene = false,
                InteriorId = 0,
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
                    reason = "The coordinate resolves inside an interior; dispatch AI access is not established.";
                    return false;
                }

                Vector3 safe = authoredPosition;
                GetSafePositionFlags flags = GetSafePositionFlags.NotInterior
                    | GetSafePositionFlags.NotWater;
                if (!World.GetSafePositionForPed(authoredPosition, out safe, flags))
                {
                    reason = "GTA did not return a safe pedestrian navigation position.";
                    return false;
                }
                if (safe.DistanceTo(authoredPosition) > maximumCorrectionDistance)
                {
                    reason = "The nearest safe pedestrian position is too far from the authored point.";
                    return false;
                }

                int safeInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS, safe.X, safe.Y, safe.Z);
                if (safeInteriorId != 0)
                {
                    reason = "The resolved pedestrian position is inside an interior.";
                    return false;
                }

                float groundZ;
                Vector3 groundNormal;
                if (!World.GetGroundHeightAndNormal(safe, out groundZ, out groundNormal)
                    || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || groundNormal.Z < 0.55f
                    || Math.Abs(safe.Z - groundZ) > 3f)
                {
                    reason = "Ground height or slope could not be validated at the pedestrian point.";
                    return false;
                }
                safe.Z = groundZ;

                Vector3 nearestStreet = World.GetNextPositionOnStreet(safe);
                if (Distance2D(safe, nearestStreet) < 1.25f)
                {
                    reason = "The candidate is too close to the nearest road node and may occupy a traffic lane.";
                    return false;
                }

                if (HasOverlappingPed(safe, 1.35f))
                {
                    reason = "Another pedestrian currently occupies the candidate point; ambient actors are not adopted.";
                    return false;
                }
                if (HasOverlappingVehicle(safe, 1.8f))
                {
                    reason = "A vehicle currently overlaps the candidate pedestrian point.";
                    return false;
                }

                safePosition = safe;
                return true;
            }
            catch (Exception ex)
            {
                reason = "Runtime coordinate validation failed: " + ex.GetType().Name;
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
            reason = string.Empty;
            try
            {
                Vector3 roadside = Vector3.Zero;
                if (!World.GetPositionOnRoadside(scenePosition, Direction.Any, out roadside))
                {
                    reason = "GTA did not provide a roadside vehicle-staging position.";
                    return false;
                }

                int interiorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS, roadside.X, roadside.Y, roadside.Z);
                float groundZ;
                Vector3 groundNormal;
                if (interiorId != 0
                    || !World.GetGroundHeightAndNormal(roadside, out groundZ, out groundNormal)
                    || groundNormal.Z < 0.65f
                    || Math.Abs(roadside.Z - groundZ) > 3f)
                {
                    reason = "The roadside candidate is interior or lacks suitable ground support.";
                    return false;
                }
                roadside.Z = groundZ;

                float roadHeading;
                Vector3 roadNode = World.GetNextPositionOnStreetWithHeading(
                    roadside, out roadHeading);
                float roadSeparation = Distance2D(roadside, roadNode);
                if (roadSeparation < 1.25f || roadSeparation > 14f
                    || Math.Abs(roadside.Z - roadNode.Z) > 3f)
                {
                    reason = "No distinct nearby roadside position was found away from the road node.";
                    return false;
                }
                if (area == null
                    || (enforcePlayerDistanceWindow
                        && (!area.IsEligibleFor(playerPosition)
                            || !IsWithinPlayerDistance(area, roadside, playerPosition))))
                {
                    reason = "The vehicle staging point falls outside the event area distance window.";
                    return false;
                }
                if (HasOverlappingVehicle(roadside, 2.35f))
                {
                    reason = "A vehicle currently occupies the roadside staging position.";
                    return false;
                }
                if (HasOverlappingPed(roadside, 2.0f))
                {
                    reason = "A pedestrian currently occupies the roadside staging position.";
                    return false;
                }

                stagingPosition = roadside;
                heading = roadHeading;
                return true;
            }
            catch (Exception ex)
            {
                reason = "Runtime vehicle staging validation failed: " + ex.GetType().Name;
                return false;
            }
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

        private static bool RequiresNamedLocation(string incidentType)
        {
            return string.Equals(incidentType, "bank_robbery", StringComparison.OrdinalIgnoreCase)
                || string.Equals(incidentType, "store_robbery", StringComparison.OrdinalIgnoreCase);
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
                || string.Equals(value, "Retail", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "GasStation", StringComparison.OrdinalIgnoreCase));
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
            Log("AMBIENT_WORLD_LOCATION_RESOLVED",
                "Location=" + scene.LocationId
                + "; Name=" + scene.LocationName
                + "; Category=" + scene.LocationCategory
                + "; Point=" + scene.PointId
                + "; Capture=" + scene.SourceCaptureId
                + "; Area=" + scene.AreaLocationId
                + "; Mode=" + scene.ValidationStatus
                + "; Ped=" + scene.ActorPosition
                + "; Area=" + (scene.Area == null ? string.Empty : scene.Area.Id)
                + "; AreaContexts=" + (scene.Area == null || scene.Area.Contexts == null
                    ? string.Empty : string.Join(",", scene.Area.Contexts))
                + "; ReviewStatus=" + (scene.LocationReviewStatus ?? "Unknown")
                + "; Interior=" + scene.IsInteriorScene
                + "; InteriorId=" + scene.InteriorId
                + "; VehicleRequired=" + requiresVehicle
                + "; VehicleStaging=" + (scene.HasVehicleStagingPosition
                    ? scene.VehicleStagingPosition.ToString() : "Unavailable")
                + "; Identity=" + (scene.IsSurveyedPoint ? "SurveyedCandidate" : "AreaCandidate_NotNamedSite"));
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
                _log.Runtime(category, message);
        }

        private sealed class DispatchLocationCandidate
        {
            internal LSImmersiveLocationCatalog.LSImmersiveLocationDefinition Location;
            internal LSImmersiveLocationCatalog.LSImmersiveLocationPoint Point;
            internal LSPDDispatchLocationDefinition Area;
            internal float AreaDistance;
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
                    incident.SceneBehavior, "paired_exchange", StringComparison.OrdinalIgnoreCase);
            if (!pairedScene
                || string.IsNullOrWhiteSpace(incident.SceneAnimationDictionary)
                || string.IsNullOrWhiteSpace(incident.SceneAnimationPrimaryClip)
                || string.IsNullOrWhiteSpace(incident.SceneAnimationSecondaryClip))
            {
                reason = "The Dispatch event has no complete supported paired animation definition.";
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

                // Play each authored dictionary clip once. The dispatch scene
                // starts only as the officer approaches, so the full exchange is
                // visible and player response tasks can interrupt it naturally.
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
            int currentInteriorId,
            int targetInteriorId,
            bool enteringInterior)
        {
            Pair = pair;
            EntryPosition = entryPosition;
            DestinationPosition = destinationPosition;
            CurrentInteriorId = currentInteriorId;
            TargetInteriorId = targetInteriorId;
            EnteringInterior = enteringInterior;
        }

        internal LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair Pair { get; private set; }
        internal Vector3 EntryPosition { get; private set; }
        internal Vector3 DestinationPosition { get; private set; }
        internal int CurrentInteriorId { get; private set; }
        internal int TargetInteriorId { get; private set; }
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
        internal bool IsInteriorScene { get; set; }
        internal int InteriorId { get; set; }
        internal string IncidentType { get; set; }
        internal LSImmersiveLocationCatalog.LSImmersiveLocationInteriorAccessPair InteriorAccessPair { get; set; }
        internal string ValidationStatus { get; set; }
        internal string LocationReviewStatus { get; set; }
    }
}
