using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace LSImmersiveLife
{
    /// <summary>
    /// Standalone, future-facing ambient world behavior framework for LSImmersiveLife.
    ///
    /// This file is intentionally NEW-FILE ONLY architecture code. It does not patch
    /// PoliceCore, Dispatch, Crime Activity, NPC Response, the location XML, the NPC
    /// database, MainConfig, MainUI, the project file, or any existing gameplay owner.
    ///
    /// The class is deliberately centered around registered actors rather than a
    /// city-wide ped scan. Future integration can register eligible entities and let
    /// their existing gameplay owner remain authoritative.
    /// </summary>
    internal sealed class LSWorldAmbientBehavior
    {
        private readonly Dictionary<int, AmbientActorContext> _actors =
            new Dictionary<int, AmbientActorContext>();

        private readonly Dictionary<int, long> _nextEvaluationAt =
            new Dictionary<int, long>();

        private readonly int _maintenanceIntervalMilliseconds;
        private readonly int _minimumEvaluationIntervalMilliseconds;

        internal LSWorldAmbientBehavior(
            int maintenanceIntervalMilliseconds = 750,
            int minimumEvaluationIntervalMilliseconds = 1000)
        {
            _maintenanceIntervalMilliseconds = Math.Max(100, maintenanceIntervalMilliseconds);
            _minimumEvaluationIntervalMilliseconds = Math.Max(250, minimumEvaluationIntervalMilliseconds);
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
    /// Capability-aware representation of a surveyed physical location. This is a
    /// future integration abstraction; it does not replace LSImmersiveLocation.xml.
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
                || PedFootAccess == AmbientCapabilityStatus.Observed
                || PedFootAccess == AmbientCapabilityStatus.Candidate;
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
}
