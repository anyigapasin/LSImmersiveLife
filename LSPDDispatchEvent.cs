using GTA;
using GTA.Math;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Runtime states for the universal Police dispatch owner. Crime Activity
    /// has its own player-led scene owner and never enters this state machine
    /// or becomes a Dispatch offer automatically.
    /// </summary>
    internal enum LSPDDispatchState
    {
        None,
        Offered,
        Accepted,
        EnRoute,
        OnScene,
        Investigating,
        SuspectFleeing,
        SuspectCompliant,
        SuspectResisting,
        Arrested,
        AwaitingTransport,
        HoldingAtStation,
        PrisonTransfer,
        Completed,
        Cancelled,
        Failed
    }

    /// <summary>
    /// One reusable criminal profile from LSPDCriminalProfile.xml. Profiles are
    /// gameplay content, not settings: an event chooses one of its approved
    /// profiles when a new Dispatch assignment is created.
    /// </summary>
    internal sealed class LSPDCriminalProfileDefinition
    {
        internal string Id { get; private set; }
        internal string ModelName { get; private set; }
        internal string VehicleModelName { get; private set; }
        internal string WeaponName { get; private set; }
        internal string PedPoolId { get; private set; }
        internal string VehiclePoolId { get; private set; }
        internal string WeaponPoolId { get; private set; }
        internal string ConvoyPedPoolId { get; private set; }
        internal string ConvoyVehiclePoolId { get; private set; }
        internal string ConvoyWeaponPoolId { get; private set; }
        internal string PedAssetId { get; private set; }
        internal string VehicleAssetId { get; private set; }
        internal string WeaponAssetId { get; private set; }
        internal IReadOnlyList<string> PedModelCandidates { get; private set; }
        internal IReadOnlyList<string> VehicleModelCandidates { get; private set; }
        internal IReadOnlyList<string> WeaponNameCandidates { get; private set; }
        internal bool Armed { get; private set; }
        internal string Disposition { get; private set; }
        internal bool DispatchEligible { get; private set; }
        internal bool CrimeActivityEligible { get; private set; }
        internal bool ConvoyEligible { get; private set; }
        internal int Weight { get; private set; }

        internal static LSPDCriminalProfileDefinition FromXml(XElement node)
        {
            return new LSPDCriminalProfileDefinition
            {
                Id = Required(node, "id", "Criminal profile"),
                ModelName = Required(node, "model", "Criminal profile"),
                VehicleModelName = Optional(node, "vehicleModel", string.Empty),
                WeaponName = Optional(node, "weapon", string.Empty),
                PedPoolId = Optional(node, "pedPoolId", string.Empty),
                VehiclePoolId = Optional(node, "vehiclePoolId", string.Empty),
                WeaponPoolId = Optional(node, "weaponPoolId", string.Empty),
                ConvoyPedPoolId = Optional(node, "convoyPedPoolId", string.Empty),
                ConvoyVehiclePoolId = Optional(node, "convoyVehiclePoolId", string.Empty),
                ConvoyWeaponPoolId = Optional(node, "convoyWeaponPoolId", string.Empty),
                Armed = Bool(node, "armed", false),
                Disposition = Optional(node, "disposition", "situational"),
                DispatchEligible = Bool(node, "dispatchEligible", true),
                CrimeActivityEligible = Bool(node, "crimeActivityEligible", true),
                ConvoyEligible = Bool(node, "convoyEligible", true),
                Weight = Integer(node, "weight", 1, 1, 1000),
                PedModelCandidates = new List<string>(),
                VehicleModelCandidates = new List<string>(),
                WeaponNameCandidates = new List<string>()
            };
        }

        internal bool IsEligibleFor(string activity)
        {
            if (string.Equals(activity, "crime_activity", StringComparison.OrdinalIgnoreCase))
                return CrimeActivityEligible;
            if (string.Equals(activity, "convoy", StringComparison.OrdinalIgnoreCase))
                return ConvoyEligible;
            return DispatchEligible;
        }

        internal LSPDCriminalProfileDefinition WithRuntimeAssets(
            LSPDCriminalAssetDefinition ped,
            LSPDCriminalAssetDefinition vehicle,
            LSPDCriminalAssetDefinition weapon,
            IReadOnlyList<string> pedCandidates,
            IReadOnlyList<string> vehicleCandidates,
            IReadOnlyList<string> weaponCandidates)
        {
            return new LSPDCriminalProfileDefinition
            {
                Id = Id,
                // Runtime selections are authoritative. An unresolved pool
                // member must stay empty so callers cannot silently fall back
                // to the profile's legacy model attributes.
                ModelName = ped == null ? string.Empty : ped.Value,
                VehicleModelName = vehicle == null ? string.Empty : vehicle.Value,
                WeaponName = weapon == null ? string.Empty : weapon.Value,
                PedPoolId = PedPoolId,
                VehiclePoolId = VehiclePoolId,
                WeaponPoolId = WeaponPoolId,
                ConvoyPedPoolId = ConvoyPedPoolId,
                ConvoyVehiclePoolId = ConvoyVehiclePoolId,
                ConvoyWeaponPoolId = ConvoyWeaponPoolId,
                PedAssetId = ped == null ? string.Empty : ped.Id,
                VehicleAssetId = vehicle == null ? string.Empty : vehicle.Id,
                WeaponAssetId = weapon == null ? string.Empty : weapon.Id,
                PedModelCandidates = pedCandidates ?? new List<string>(),
                VehicleModelCandidates = vehicleCandidates ?? new List<string>(),
                WeaponNameCandidates = weaponCandidates ?? new List<string>(),
                Armed = Armed,
                Disposition = Disposition,
                DispatchEligible = DispatchEligible,
                CrimeActivityEligible = CrimeActivityEligible,
                ConvoyEligible = ConvoyEligible,
                Weight = Weight
            };
        }

        private static string Required(XElement node, string attribute, string owner)
        {
            string value = node == null ? null : (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(owner + " attribute is missing: " + attribute);
            return value.Trim();
        }

        private static string Optional(XElement node, string attribute, string fallback)
        {
            string value = node == null ? null : (string)node.Attribute(attribute);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static bool Bool(XElement node, string attribute, bool fallback)
        {
            bool value;
            return bool.TryParse(node == null ? null : (string)node.Attribute(attribute), out value)
                ? value : fallback;
        }

        private static int Integer(
            XElement node,
            string attribute,
            int fallback,
            int minimum,
            int maximum)
        {
            int value;
            if (!int.TryParse(node == null ? null : (string)node.Attribute(attribute),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return fallback;
            if (value < minimum || value > maximum)
                throw new InvalidOperationException(
                    "Criminal profile attribute is outside range: " + attribute);
            return value;
        }
    }

    /// <summary>
    /// One model, vehicle, or weapon record from the catalog-only criminal
    /// asset database. The XML never performs selection; the runtime chooses
    /// from these records and can discard a record when GTA cannot stream it.
    /// </summary>
    internal sealed class LSPDCriminalAssetDefinition
    {
        internal string Id { get; private set; }
        internal string Value { get; private set; }
        internal string Role { get; private set; }
        internal string Category { get; private set; }
        internal string FallbackId { get; private set; }
        internal int Weight { get; private set; }

        internal static LSPDCriminalAssetDefinition FromXml(
            XElement node,
            string valueAttribute,
            string kind)
        {
            if (node == null)
                throw new InvalidDataException(kind + " asset is missing.");
            string id = Required(node, "id", kind + " asset");
            string value = Required(node, valueAttribute, kind + " asset");
            return new LSPDCriminalAssetDefinition
            {
                Id = id,
                Value = value,
                Role = Optional(node, "role", kind.ToLowerInvariant()),
                Category = Optional(node, "category", "general"),
                FallbackId = Optional(node, "fallbackId", string.Empty),
                Weight = Integer(node, "weight", 1, 1, 1000)
            };
        }

        private static string Required(XElement node, string attribute, string owner)
        {
            string value = node == null ? null : (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(owner + " attribute is missing: " + attribute);
            return value.Trim();
        }

        private static string Optional(XElement node, string attribute, string fallback)
        {
            string value = node == null ? null : (string)node.Attribute(attribute);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static int Integer(
            XElement node,
            string attribute,
            int fallback,
            int minimum,
            int maximum)
        {
            int value;
            if (!int.TryParse((string)node.Attribute(attribute),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return fallback;
            if (value < minimum || value > maximum)
                throw new InvalidDataException(
                    "Criminal asset attribute is outside range: " + attribute);
            return value;
        }
    }

    internal sealed class LSPDCriminalAssetPoolDefinition
    {
        internal string Id { get; private set; }
        internal string AssetType { get; private set; }
        internal string UseFor { get; private set; }
        internal List<string> AssetIds { get; private set; }

        internal static LSPDCriminalAssetPoolDefinition FromXml(XElement node)
        {
            if (node == null)
                throw new InvalidDataException("Criminal asset pool is missing.");
            string id = Required(node, "id");
            string assetType = Required(node, "assetType");
            if (!string.Equals(assetType, "ped", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(assetType, "vehicle", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(assetType, "weapon", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Criminal asset pool has an invalid assetType: " + id);

            List<string> ids = node.Elements("Ref")
                .Select(value => Required(value, "id"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0)
                throw new InvalidDataException("Criminal asset pool is empty: " + id);

            return new LSPDCriminalAssetPoolDefinition
            {
                Id = id,
                AssetType = assetType,
                UseFor = Optional(node, "useFor", "all"),
                AssetIds = ids
            };
        }

        private static string Required(XElement node, string attribute)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Criminal asset pool attribute is missing: " + attribute);
            return value.Trim();
        }

        private static string Optional(XElement node, string attribute, string fallback)
        {
            string value = (string)node.Attribute(attribute);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }

    internal sealed class LSPDCriminalActivityMappingDefinition
    {
        internal string Id { get; private set; }
        internal string Activity { get; private set; }
        internal string Category { get; private set; }
        internal string PedPoolId { get; private set; }
        internal string VehiclePoolId { get; private set; }
        internal string WeaponPoolId { get; private set; }

        internal static LSPDCriminalActivityMappingDefinition FromXml(XElement node)
        {
            if (node == null)
                throw new InvalidDataException("Criminal activity mapping is missing.");
            return new LSPDCriminalActivityMappingDefinition
            {
                Id = Required(node, "id"),
                Activity = Required(node, "activity"),
                Category = Optional(node, "category", "*"),
                PedPoolId = Required(node, "pedPoolId"),
                VehiclePoolId = Required(node, "vehiclePoolId"),
                WeaponPoolId = Required(node, "weaponPoolId")
            };
        }

        private static string Required(XElement node, string attribute)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Criminal activity mapping attribute is missing: " + attribute);
            return value.Trim();
        }

        private static string Optional(XElement node, string attribute, string fallback)
        {
            string value = (string)node.Attribute(attribute);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }

    /// <summary>
    /// Validated, shared view of LSPDCriminalProfile.xml. It contains only
    /// catalog data and selection helpers. It never creates GTA entities and
    /// never owns activity state.
    /// </summary>
    internal sealed class LSPDCriminalAssetCatalog
    {
        private readonly Dictionary<string, LSPDCriminalAssetDefinition> _peds =
            new Dictionary<string, LSPDCriminalAssetDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LSPDCriminalAssetDefinition> _vehicles =
            new Dictionary<string, LSPDCriminalAssetDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LSPDCriminalAssetDefinition> _weapons =
            new Dictionary<string, LSPDCriminalAssetDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LSPDCriminalAssetPoolDefinition> _pools =
            new Dictionary<string, LSPDCriminalAssetPoolDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly List<LSPDCriminalActivityMappingDefinition> _activityMappings =
            new List<LSPDCriminalActivityMappingDefinition>();

        internal int PedCount { get { return _peds.Count; } }
        internal int VehicleCount { get { return _vehicles.Count; } }
        internal int WeaponCount { get { return _weapons.Count; } }
        internal int PoolCount { get { return _pools.Count; } }
        internal IReadOnlyList<LSPDCriminalActivityMappingDefinition> ActivityMappings
        {
            get { return _activityMappings; }
        }

        internal void Clear()
        {
            _peds.Clear();
            _vehicles.Clear();
            _weapons.Clear();
            _pools.Clear();
            _activityMappings.Clear();
        }

        internal void AddPed(LSPDCriminalAssetDefinition value) { Add(_peds, value, "ped"); }
        internal void AddVehicle(LSPDCriminalAssetDefinition value) { Add(_vehicles, value, "vehicle"); }
        internal void AddWeapon(LSPDCriminalAssetDefinition value) { Add(_weapons, value, "weapon"); }
        internal void AddPool(LSPDCriminalAssetPoolDefinition value)
        {
            if (value == null)
                throw new InvalidDataException("Criminal asset pool is null.");
            if (_pools.ContainsKey(value.Id))
                throw new InvalidDataException("Duplicate criminal asset pool id: " + value.Id);
            _pools.Add(value.Id, value);
        }
        internal void AddActivityMapping(LSPDCriminalActivityMappingDefinition value)
        {
            if (value == null)
                throw new InvalidDataException("Criminal activity mapping is null.");
            if (_activityMappings.Any(existing => string.Equals(
                existing.Id, value.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Duplicate criminal activity mapping id: " + value.Id);
            _activityMappings.Add(value);
        }

        internal void Validate()
        {
            foreach (LSPDCriminalAssetPoolDefinition pool in _pools.Values)
            {
                IDictionary<string, LSPDCriminalAssetDefinition> source = Assets(pool.AssetType);
                foreach (string id in pool.AssetIds)
                    if (!source.ContainsKey(id))
                        throw new InvalidDataException(
                            "Criminal asset pool references an unknown " + pool.AssetType + ": " + id);
            }
            foreach (LSPDCriminalActivityMappingDefinition mapping in _activityMappings)
            {
                RequirePool(mapping.PedPoolId, "ped", mapping.Id);
                RequirePool(mapping.VehiclePoolId, "vehicle", mapping.Id);
                RequirePool(mapping.WeaponPoolId, "weapon", mapping.Id);
            }
        }

        internal LSPDCriminalProfileDefinition ResolveProfile(
            LSPDCriminalProfileDefinition source,
            string activity,
            Random random,
            ISet<string> recentAssetIds)
        {
            LSPDCriminalProfileDefinition resolved;
            string failureReason;
            return TryResolveProfile(
                source,
                activity,
                random,
                recentAssetIds,
                out resolved,
                out failureReason) ? resolved : null;
        }

        internal bool TryResolveProfile(
            LSPDCriminalProfileDefinition source,
            string activity,
            Random random,
            ISet<string> recentAssetIds,
            out LSPDCriminalProfileDefinition resolved,
            out string failureReason)
        {
            resolved = null;
            failureReason = string.Empty;
            if (source == null)
            {
                failureReason = "PROFILE_REFERENCE_MISSING";
                return false;
            }

            string pedPoolId = string.Equals(activity, "convoy", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(source.ConvoyPedPoolId)
                ? source.ConvoyPedPoolId : source.PedPoolId;
            string vehiclePoolId = string.Equals(activity, "convoy", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(source.ConvoyVehiclePoolId)
                ? source.ConvoyVehiclePoolId : source.VehiclePoolId;
            string weaponPoolId = string.Equals(activity, "convoy", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(source.ConvoyWeaponPoolId)
                ? source.ConvoyWeaponPoolId : source.WeaponPoolId;
            LSPDCriminalAssetDefinition ped = Select(
                pedPoolId, "ped", random, recentAssetIds);
            LSPDCriminalAssetDefinition vehicle = Select(
                vehiclePoolId, "vehicle", random, recentAssetIds);
            LSPDCriminalAssetDefinition weapon = Select(
                weaponPoolId, "weapon", random, recentAssetIds);

            if (ped == null)
            {
                failureReason = "PED_POOL_HAS_NO_VALID_XML_CANDIDATE:" + pedPoolId;
                return false;
            }
            if (vehicle == null)
            {
                failureReason = "VEHICLE_POOL_HAS_NO_VALID_XML_CANDIDATE:" + vehiclePoolId;
                return false;
            }
            if (weapon == null)
            {
                failureReason = "WEAPON_POOL_HAS_NO_VALID_XML_CANDIDATE:" + weaponPoolId;
                return false;
            }

            resolved = source.WithRuntimeAssets(
                ped,
                vehicle,
                weapon,
                Values(pedPoolId, "ped", random, 12),
                Values(vehiclePoolId, "vehicle", random, 8),
                Values(weaponPoolId, "weapon", random, 12));
            if (resolved == null
                || string.IsNullOrWhiteSpace(resolved.ModelName)
                || string.IsNullOrWhiteSpace(resolved.VehicleModelName)
                || string.IsNullOrWhiteSpace(resolved.WeaponName))
            {
                resolved = null;
                failureReason = "SELECTED_XML_ASSET_VALUE_MISSING";
                return false;
            }
            return true;
        }

        internal bool TryBuildCrimeActivityResources(
            string activityId,
            string category,
            Random random,
            out LSPDCrimeSceneResources resources)
        {
            resources = null;
            LSPDCriminalActivityMappingDefinition mapping = _activityMappings
                .FirstOrDefault(value => string.Equals(value.Activity, "crime_activity",
                    StringComparison.OrdinalIgnoreCase)
                    && string.Equals(value.Category, category, StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
                mapping = _activityMappings.FirstOrDefault(value =>
                    string.Equals(value.Activity, "crime_activity", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(value.Category, "*", StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
                return false;

            List<string> peds = Values(mapping.PedPoolId, "ped", random, 12);
            List<string> vehicles = Values(mapping.VehiclePoolId, "vehicle", random, 8);
            List<string> weapons = Values(mapping.WeaponPoolId, "weapon", random, 12);
            if (peds.Count == 0 || vehicles.Count == 0 || weapons.Count == 0)
                return false;
            resources = new LSPDCrimeSceneResources
            {
                PedPoolId = mapping.PedPoolId,
                VehiclePoolId = mapping.VehiclePoolId,
                WeaponPoolId = mapping.WeaponPoolId,
                PedModels = peds.Select(value =>
                    LSPDCrimeResourceDefinition.Create(
                        FindAssetId("ped", value), value, "criminal_catalog_ped")).ToList(),
                VehicleModels = vehicles.Select(value =>
                    LSPDCrimeResourceDefinition.Create(
                        FindAssetId("vehicle", value), value, "criminal_catalog_vehicle")).ToList(),
                Weapons = weapons.Select(value =>
                    LSPDCrimeResourceDefinition.Create(
                        FindAssetId("weapon", value), value, "criminal_catalog_weapon")).ToList()
            };
            return true;
        }

        private string FindAssetId(string assetType, string value)
        {
            IDictionary<string, LSPDCriminalAssetDefinition> source = Assets(assetType);
            LSPDCriminalAssetDefinition asset = source.Values.FirstOrDefault(item =>
                item != null && string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));
            return asset == null ? string.Empty : asset.Id;
        }

        internal List<string> Values(
            string poolId,
            string assetType,
            Random random,
            int maximum)
        {
            LSPDCriminalAssetPoolDefinition pool;
            if (string.IsNullOrWhiteSpace(poolId)
                || !_pools.TryGetValue(poolId, out pool)
                || !string.Equals(pool.AssetType, assetType, StringComparison.OrdinalIgnoreCase))
                return new List<string>();
            IDictionary<string, LSPDCriminalAssetDefinition> source = Assets(assetType);
            var candidates = pool.AssetIds
                .Where(id => source.ContainsKey(id))
                .Select(id => source[id])
                .ToList();
            var result = new List<string>();
            int target = Math.Min(Math.Max(1, maximum), candidates.Count);
            while (candidates.Count > 0 && result.Count < target)
            {
                int index = random == null ? 0 : random.Next(candidates.Count);
                result.Add(candidates[index].Value);
                candidates.RemoveAt(index);
            }
            return result;
        }

        internal bool HasPool(string poolId, string assetType)
        {
            LSPDCriminalAssetPoolDefinition pool;
            return !string.IsNullOrWhiteSpace(poolId)
                && _pools.TryGetValue(poolId, out pool)
                && string.Equals(pool.AssetType, assetType, StringComparison.OrdinalIgnoreCase);
        }

        private LSPDCriminalAssetDefinition Select(
            string poolId,
            string assetType,
            Random random,
            ISet<string> recentAssetIds)
        {
            LSPDCriminalAssetPoolDefinition pool;
            if (string.IsNullOrWhiteSpace(poolId)
                || !_pools.TryGetValue(poolId, out pool)
                || !string.Equals(pool.AssetType, assetType, StringComparison.OrdinalIgnoreCase))
                return null;
            IDictionary<string, LSPDCriminalAssetDefinition> source = Assets(assetType);
            List<LSPDCriminalAssetDefinition> candidates = pool.AssetIds
                .Where(id => source.ContainsKey(id))
                .Select(id => source[id])
                .ToList();
            if (recentAssetIds != null && candidates.Count > 1)
            {
                List<LSPDCriminalAssetDefinition> varied = candidates
                    .Where(value => !recentAssetIds.Contains(value.Id))
                    .ToList();
                if (varied.Count > 0)
                    candidates = varied;
            }
            if (candidates.Count == 0)
                return null;
            int totalWeight = candidates.Sum(value => Math.Max(1, value.Weight));
            int roll = random == null ? 0 : random.Next(totalWeight);
            foreach (LSPDCriminalAssetDefinition candidate in candidates)
            {
                roll -= Math.Max(1, candidate.Weight);
                if (roll < 0)
                    return candidate;
            }
            return candidates[candidates.Count - 1];
        }

        private IDictionary<string, LSPDCriminalAssetDefinition> Assets(string assetType)
        {
            if (string.Equals(assetType, "ped", StringComparison.OrdinalIgnoreCase)) return _peds;
            if (string.Equals(assetType, "vehicle", StringComparison.OrdinalIgnoreCase)) return _vehicles;
            return _weapons;
        }

        private void RequirePool(string poolId, string assetType, string owner)
        {
            LSPDCriminalAssetPoolDefinition pool;
            if (!_pools.TryGetValue(poolId, out pool)
                || !string.Equals(pool.AssetType, assetType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Criminal activity mapping references an invalid " + assetType
                    + " pool: " + owner + "/" + poolId);
        }

        private static void Add(
            IDictionary<string, LSPDCriminalAssetDefinition> target,
            LSPDCriminalAssetDefinition value,
            string kind)
        {
            if (value == null)
                throw new InvalidDataException("Criminal " + kind + " asset is null.");
            if (target.ContainsKey(value.Id))
                throw new InvalidDataException("Duplicate criminal " + kind + " asset id: " + value.Id);
            target.Add(value.Id, value);
        }
    }

    /// <summary>
    /// Dispatch selection rules for one shared location record. Coordinates
    /// come only from LSImmersiveLocation.xml; this definition owns the
    /// event-specific officer-distance window, not geographic data.
    /// </summary>
    internal sealed class LSPDDispatchLocationDefinition
    {
        internal string Id { get; private set; }
        internal Vector3 Position { get; private set; }
        internal float MinimumPlayerDistance { get; private set; }
        internal float MaximumPlayerDistance { get; private set; }
        internal List<string> Contexts { get; private set; }

        internal static LSPDDispatchLocationDefinition FromXml(
            XElement node,
            LSImmersiveLocationCatalog locationCatalog)
        {
            string id = Required(node, "id", "Dispatch location reference");
            LSImmersiveLocationCatalog.LSImmersiveLocationPoint point;
            if (locationCatalog == null
                || !locationCatalog.TryGetPoint(
                    id, "AreaSearchAnchor", false, out point))
            {
                throw new InvalidOperationException(
                    "Dispatch location reference has no shared area-search anchor: " + id);
            }

            float minimum = Number(node, "minimumPlayerDistance", 65f, 0f, 10000f);
            float maximum = Number(node, "maximumPlayerDistance", 1200f, minimum, 20000f);
            return new LSPDDispatchLocationDefinition
            {
                Id = id,
                Position = point.Position,
                MinimumPlayerDistance = minimum,
                MaximumPlayerDistance = maximum,
                Contexts = Tokens(node, "contexts")
            };
        }

        internal bool IsEligibleFor(Vector3 playerPosition)
        {
            float distance = Position.DistanceTo(playerPosition);
            return distance >= MinimumPlayerDistance && distance <= MaximumPlayerDistance;
        }

        internal bool SupportsContext(string context)
        {
            return string.IsNullOrWhiteSpace(context)
                || string.Equals(context, "any", StringComparison.OrdinalIgnoreCase)
                || (Contexts != null && Contexts.Contains(
                    context.Trim(), StringComparer.OrdinalIgnoreCase));
        }

        private static List<string> Tokens(XElement node, string attribute)
        {
            string source = node == null ? null : (string)node.Attribute(attribute);
            return string.IsNullOrWhiteSpace(source)
                ? new List<string>()
                : source.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        private static string Required(XElement node, string attribute, string owner)
        {
            string value = node == null ? null : (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(owner + " attribute is missing: " + attribute);
            return value.Trim();
        }

        private static float Number(XElement node, string attribute, float fallback, float minimum, float maximum)
        {
            float value;
            if (!float.TryParse(node == null ? null : (string)node.Attribute(attribute),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return fallback;
            if (value < minimum || value > maximum)
                throw new InvalidOperationException("Dispatch location attribute is outside range: " + attribute);
            return value;
        }
    }

    /// <summary>
    /// A reference to an official Dispatch response or scene branch. Custody
    /// continuation branches are cataloged here but cannot start a new callout.
    /// </summary>
    internal sealed class LSPDDispatchBranchDefinition
    {
        private static readonly HashSet<string> ValidPhases = new HashSet<string>(
            new[] { "response_outcome", "scene_callout", "custody_continuation" },
            StringComparer.OrdinalIgnoreCase);

        internal string Id { get; private set; }
        internal string Title { get; private set; }
        internal string Phase { get; private set; }

        internal static LSPDDispatchBranchDefinition FromXml(XElement node)
        {
            if (node == null)
                throw new InvalidDataException("Dispatch branch reference is missing.");
            string id = (string)node.Attribute("id");
            string title = (string)node.Attribute("title");
            string phase = (string)node.Attribute("phase");
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(title)
                || string.IsNullOrWhiteSpace(phase))
                throw new InvalidDataException(
                    "Dispatch branch reference requires id, title, and phase.");
            if (!ValidPhases.Contains(phase.Trim()))
                throw new InvalidDataException(
                    "Dispatch branch reference has an unsupported phase: " + id);

            return new LSPDDispatchBranchDefinition
            {
                Id = id.Trim(),
                Title = title.Trim(),
                Phase = phase.Trim()
            };
        }
    }

    /// <summary>
    /// A data-driven universal callout definition. It describes a type of
    /// urgent dispatch; it does not create a scene while the player is off duty.
    /// </summary>
    internal sealed class LSPDDispatchEventDefinition
    {
        internal string Id { get; private set; }
        internal string Title { get; private set; }
        internal string Briefing { get; private set; }
        internal string IncidentType { get; private set; }
        internal string AudioIncidentType { get; private set; }
        internal List<string> CriminalProfileIds { get; private set; }
        internal List<string> LocationIds { get; private set; }
        internal List<string> BranchIds { get; private set; }
        internal string LocationContext { get; private set; }
        internal string SceneBehavior { get; private set; }
        internal string SceneAnimationDictionary { get; private set; }
        internal string SceneAnimationPrimaryClip { get; private set; }
        internal string SceneAnimationSecondaryClip { get; private set; }
        internal bool Enabled { get; private set; }
        internal bool RequiresVehicle { get; private set; }
        internal bool Mobile { get; private set; }
        internal bool Armed { get; private set; }
        // Authored group size. A Dispatch group is not represented by idle
        // decoration peds: every counted member is a real suspect tracked by
        // Dispatch and can enter compliance, custody, or a terminal outcome.
        internal int SuspectCount { get; private set; }
        internal int Severity { get; private set; }
        internal int MinimumCooldownSeconds { get; private set; }
        internal float SpawnDistance { get; private set; }

        internal static LSPDDispatchEventDefinition FromXml(XElement node)
        {
            string locationContext = Optional(node, "locationContext", "any");
            if (!new[] { "any", "commercial", "financial", "alley", "road_foot", "road_vehicle" }
                .Contains(locationContext, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported locationContext: " + locationContext);

            string sceneBehavior = Optional(node, "sceneBehavior", "standard");
            if (!new[] { "standard", "paired_meeting", "paired_exchange" }
                .Contains(sceneBehavior, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported sceneBehavior: " + sceneBehavior);

            string sceneAnimationDictionary = Optional(node, "sceneAnimationDictionary", string.Empty);
            string sceneAnimationPrimaryClip = Optional(node, "sceneAnimationPrimaryClip", string.Empty);
            string sceneAnimationSecondaryClip = Optional(node, "sceneAnimationSecondaryClip", string.Empty);
            bool hasSceneAnimation = !string.IsNullOrWhiteSpace(sceneAnimationDictionary)
                || !string.IsNullOrWhiteSpace(sceneAnimationPrimaryClip)
                || !string.IsNullOrWhiteSpace(sceneAnimationSecondaryClip);
            if (string.Equals(sceneBehavior, "standard", StringComparison.OrdinalIgnoreCase)
                && hasSceneAnimation)
                throw new InvalidDataException(
                    "Standard Dispatch scenes cannot declare a paired ambient animation.");
            if (!string.Equals(sceneBehavior, "standard", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(sceneAnimationDictionary)
                    || string.IsNullOrWhiteSpace(sceneAnimationPrimaryClip)
                    || string.IsNullOrWhiteSpace(sceneAnimationSecondaryClip)))
                throw new InvalidDataException(
                    "Paired Dispatch scenes require a dictionary and both actor clips.");
            if (string.Equals(sceneBehavior, "paired_meeting", StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(sceneAnimationDictionary, "mp_ped_interaction", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(sceneAnimationPrimaryClip, "handshake_guy_a", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(sceneAnimationSecondaryClip, "handshake_guy_b", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "The documented paired meeting animation must use the authored handshake clip pair.");
            if (string.Equals(sceneBehavior, "paired_exchange", StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(sceneAnimationDictionary, "mp_common", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(sceneAnimationPrimaryClip, "givetake1_a", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(sceneAnimationSecondaryClip, "givetake1_b", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "The documented paired exchange animation must use the authored giver/receiver clip pair.");

            int suspectCount = Math.Max(1, Integer(node, "suspectCount", 1));
            if (!string.Equals(sceneBehavior, "standard", StringComparison.OrdinalIgnoreCase)
                && suspectCount < 2)
                throw new InvalidDataException(
                    "A paired Dispatch scene requires at least two tracked suspects.");

            return new LSPDDispatchEventDefinition
            {
                Id = Required(node, "id"),
                Title = Required(node, "title"),
                Briefing = Required(node, "briefing"),
                IncidentType = Required(node, "type"),
                AudioIncidentType = Optional(node, "audioIncidentType", "unknown_emergency"),
                CriminalProfileIds = IdList(node, "criminalProfileIds"),
                LocationIds = IdList(node, "locationIds"),
                BranchIds = IdList(node, "branchIds"),
                LocationContext = locationContext,
                SceneBehavior = sceneBehavior,
                SceneAnimationDictionary = sceneAnimationDictionary,
                SceneAnimationPrimaryClip = sceneAnimationPrimaryClip,
                SceneAnimationSecondaryClip = sceneAnimationSecondaryClip,
                Enabled = Bool(node, "enabled", true),
                RequiresVehicle = Bool(node, "requiresVehicle", false),
                Mobile = Bool(node, "mobile", false),
                Armed = Bool(node, "armed", false),
                SuspectCount = suspectCount,
                Severity = Integer(node, "severity", 1),
                MinimumCooldownSeconds = Integer(node, "minimumCooldownSeconds", 90),
                SpawnDistance = Float(node, "spawnDistance", 90f)
            };
        }

        internal bool CanUseArea(
            LSPDDispatchLocationDefinition area,
            Vector3 playerPosition)
        {
            return area != null
                && LocationIds != null
                && LocationIds.Contains(area.Id, StringComparer.OrdinalIgnoreCase)
                && area.SupportsContext(LocationContext)
                && area.IsEligibleFor(playerPosition);
        }

        internal LSPDCriminalProfileDefinition ChooseCriminalProfile(
            IDictionary<string, LSPDCriminalProfileDefinition> profiles,
            Random random)
        {
            if (profiles == null || profiles.Count == 0 || CriminalProfileIds == null)
                return null;

            var candidates = new List<LSPDCriminalProfileDefinition>();
            foreach (string id in CriminalProfileIds)
            {
                LSPDCriminalProfileDefinition profile;
                if (!string.IsNullOrWhiteSpace(id) && profiles.TryGetValue(id, out profile))
                    candidates.Add(profile);
            }
            if (candidates.Count == 0)
                return null;
            int totalWeight = candidates.Sum(profile => Math.Max(1, profile.Weight));
            int roll = random == null ? 0 : random.Next(totalWeight);
            foreach (LSPDCriminalProfileDefinition profile in candidates)
            {
                roll -= Math.Max(1, profile.Weight);
                if (roll < 0)
                    return profile;
            }
            return candidates[candidates.Count - 1];
        }

        private static string Required(XElement node, string attribute)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Dispatch event attribute is missing: " + attribute);
            return value.Trim();
        }

        private static string Optional(XElement node, string attribute, string fallback)
        {
            string value = (string)node.Attribute(attribute);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static List<string> IdList(XElement node, string attribute)
        {
            string source = (string)node.Attribute(attribute);
            var values = new List<string>();
            if (string.IsNullOrWhiteSpace(source))
                return values;
            foreach (string raw in source.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = raw.Trim();
                bool alreadyPresent = false;
                foreach (string existing in values)
                    if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyPresent = true;
                        break;
                    }
                if (value.Length > 0 && !alreadyPresent)
                    values.Add(value);
            }
            return values;
        }

        private static bool Bool(XElement node, string attribute, bool fallback)
        {
            bool value;
            return bool.TryParse((string)node.Attribute(attribute), out value) ? value : fallback;
        }

        private static int Integer(XElement node, string attribute, int fallback)
        {
            int value;
            return int.TryParse((string)node.Attribute(attribute), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) ? Math.Max(0, value) : fallback;
        }

        private static float Float(XElement node, string attribute, float fallback)
        {
            float value;
            return float.TryParse((string)node.Attribute(attribute), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) ? Math.Max(20f, value) : fallback;
        }
    }

    /// <summary>
    /// One live dispatch assignment and the Police-owned scene entities it may
    /// create after the player accepts the call.
    /// </summary>
    internal sealed class LSPDDispatchEvent
    {
        internal string Id { get; set; }
        internal string Title { get; set; }
        internal string Briefing { get; set; }
        internal string IncidentType { get; set; }
        internal string AudioIncidentType { get; set; }
        internal List<string> BranchIds { get; set; }
        internal string LocationContext { get; set; }
        internal string SceneBehavior { get; set; }
        internal string SceneAnimationDictionary { get; set; }
        internal string SceneAnimationPrimaryClip { get; set; }
        internal string SceneAnimationSecondaryClip { get; set; }
        internal string CriminalProfileId { get; set; }
        internal string CriminalDisposition { get; set; }
        internal string SuspectModel { get; set; }
        internal List<string> SuspectModelCandidates { get; set; }
        internal string VehicleModel { get; set; }
        internal List<string> VehicleModelCandidates { get; set; }
        internal string WeaponName { get; set; }
        internal List<string> WeaponNameCandidates { get; set; }
        internal bool RequiresVehicle { get; set; }
        internal bool Mobile { get; set; }
        internal bool Armed { get; set; }
        internal int Severity { get; set; }
        internal int SuspectCount { get; set; }
        internal Vector3 Origin { get; set; }
        internal AmbientDispatchSceneResolution SceneResolution { get; set; }
        internal Ped Suspect { get; set; }
        // Suspect remains the primary target for legacy UI/status access.
        // Suspects contains every live criminal owned by this Dispatch event,
        // while ArrestedSuspects is the exact list handed to Convoy.
        internal List<Ped> Suspects { get; private set; } = new List<Ped>();
        internal List<Ped> ArrestedSuspects { get; private set; } = new List<Ped>();
        internal List<Ped> CompliantSuspects { get; private set; } = new List<Ped>();
        // Some urgent callouts have a second Police-owned participant.  The
        // victim/witness stays in the scene until the officer has investigated
        // it, so a carjacking or robbery is visibly different from a lone
        // fleeing suspect.
        internal Ped Victim { get; set; }
        internal List<Ped> AdditionalParticipants { get; private set; } = new List<Ped>();
        internal Vehicle SuspectVehicle { get; set; }
        internal bool OwnedByDispatch { get; set; }
        internal bool HasConvoyCustodyHandoff { get; set; }
        internal bool ScenePreparationRequested { get; set; }
        internal bool ScenePreparationCompleted { get; set; }
        internal DateTime ScenePreparationDeadline { get; set; }
        internal bool SceneBehaviorInitialized { get; set; }
        internal bool SceneAnimationRequested { get; set; }
        internal DateTime SceneAnimationDeadline { get; set; }
        // Robbery and kidnapping scenes use a short task sequence so a
        // leave-vehicle task is allowed to finish before movement or threat
        // animation is assigned. This keeps the authored scene physical.
        internal int SceneBehaviorStage { get; set; }
        internal DateTime SceneBehaviorNextStepAt { get; set; }
        internal bool SurrenderRequested { get; set; }
        internal DateTime SurrenderRequestedAt { get; set; }
        // Compliance and physical arrest are intentionally separate.  The
        // hands-up state is the suspect's response to the officer's command;
        // this pair records the short, player-owned handcuff animation before
        // the ped is eligible for Convoy custody.
        internal bool PlayerHandcuffInProgress { get; set; }
        internal int PlayerHandcuffTargetHandle { get; set; }
        internal DateTime PlayerHandcuffStartedAt { get; set; }
        internal bool ArrestSecured { get; set; }
        internal bool GroupComplianceRequested { get; set; }
        internal string ActiveAudioScope { get; set; }
        internal string LastAudioStage { get; set; }
        internal LSPDDispatchState State { get; set; }
        internal DateTime CreatedAt { get; set; }
        internal DateTime StateChangedAt { get; set; }
        internal Vector3 LastSuspectPosition { get; set; }
        internal DateTime LastMovementSampleAt { get; set; }
        internal bool SuspectMovementConfirmed { get; set; }
        // Dispatch owns a suspect's interior pursuit until the suspect has
        // physically crossed its mapped door. Each phase is retained here so
        // maintenance cannot replace the navigation/door task with ReactAndFlee.
        internal AmbientInteriorAccessRoute InteriorExitRoute { get; set; }
        internal int InteriorExitActorHandle { get; set; }
        internal int InteriorExitPhase { get; set; }
        internal int InteriorExitRecoveryCount { get; set; }
        internal DateTime InteriorExitStartedAt { get; set; }
        internal DateTime InteriorExitNextTaskAt { get; set; }
        internal DateTime InteriorExitVehicleEntryStartedAt { get; set; }
        internal HashSet<int> InteriorExitCompletedHandles { get; private set; }
        internal HashSet<int> InteriorExitFailedHandles { get; private set; }

        internal static LSPDDispatchEvent FromDefinition(
            LSPDDispatchEventDefinition definition,
            Vector3 origin,
            LSPDCriminalProfileDefinition criminalProfile)
        {
            if (definition == null || criminalProfile == null
                || string.IsNullOrWhiteSpace(criminalProfile.ModelName)
                || string.IsNullOrWhiteSpace(criminalProfile.VehicleModelName)
                || string.IsNullOrWhiteSpace(criminalProfile.WeaponName))
                return null;

            DateTime now = DateTime.UtcNow;
            bool profileArmed = criminalProfile.Armed;
            return new LSPDDispatchEvent
            {
                Id = definition.Id + "-" + Guid.NewGuid().ToString("N"),
                Title = definition.Title,
                Briefing = definition.Briefing,
                IncidentType = definition.IncidentType,
                AudioIncidentType = definition.AudioIncidentType,
                BranchIds = definition.BranchIds == null
                    ? new List<string>() : new List<string>(definition.BranchIds),
                LocationContext = definition.LocationContext,
                SceneBehavior = definition.SceneBehavior,
                SceneAnimationDictionary = definition.SceneAnimationDictionary,
                SceneAnimationPrimaryClip = definition.SceneAnimationPrimaryClip,
                SceneAnimationSecondaryClip = definition.SceneAnimationSecondaryClip,
                CriminalProfileId = criminalProfile.Id,
                CriminalDisposition = criminalProfile.Disposition,
                SuspectModel = criminalProfile.ModelName,
                SuspectModelCandidates = CandidateValues(
                    criminalProfile.PedModelCandidates,
                    criminalProfile.ModelName),
                VehicleModel = criminalProfile.VehicleModelName,
                VehicleModelCandidates = CandidateValues(
                    criminalProfile.VehicleModelCandidates,
                    criminalProfile.VehicleModelName),
                WeaponName = criminalProfile.WeaponName,
                WeaponNameCandidates = CandidateValues(
                    criminalProfile.WeaponNameCandidates,
                    criminalProfile.WeaponName),
                RequiresVehicle = definition.RequiresVehicle,
                Mobile = definition.Mobile,
                Armed = definition.Armed || profileArmed,
                SuspectCount = Math.Max(1, definition.SuspectCount),
                Severity = definition.Severity,
                Origin = origin,
                // Dispatch owns the live assignment before its physical scene entities exist.
                OwnedByDispatch = true,
                State = LSPDDispatchState.Offered,
                CreatedAt = now,
                StateChangedAt = now,
                ScenePreparationDeadline = DateTime.MinValue,
                SceneAnimationRequested = false,
                SceneAnimationDeadline = DateTime.MinValue,
                SceneBehaviorStage = 0,
                SceneBehaviorNextStepAt = DateTime.MinValue,
                SurrenderRequestedAt = DateTime.MinValue,
                LastMovementSampleAt = now,
                LastSuspectPosition = origin,
                InteriorExitPhase = 0,
                InteriorExitStartedAt = DateTime.MinValue,
                InteriorExitNextTaskAt = DateTime.MinValue,
                InteriorExitVehicleEntryStartedAt = DateTime.MinValue,
                InteriorExitCompletedHandles = new HashSet<int>(),
                InteriorExitFailedHandles = new HashSet<int>(),
                AdditionalParticipants = new List<Ped>(),
                Suspects = new List<Ped>(),
                ArrestedSuspects = new List<Ped>(),
                CompliantSuspects = new List<Ped>()
            };
        }

        private static List<string> CandidateValues(
            IReadOnlyList<string> values,
            string primary)
        {
            var candidates = new List<string>();
            if (values != null)
                foreach (string value in values)
                    if (!string.IsNullOrWhiteSpace(value)
                        && !candidates.Contains(value, StringComparer.OrdinalIgnoreCase))
                        candidates.Add(value);
            if (!string.IsNullOrWhiteSpace(primary)
                && !candidates.Contains(primary, StringComparer.OrdinalIgnoreCase))
                candidates.Insert(0, primary);
            return candidates;
        }
    }
}

