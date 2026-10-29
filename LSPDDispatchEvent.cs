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
        internal string VehicleType { get; private set; }
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
                VehicleType = Optional(node, "vehicleType", string.Empty),
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
            return TryResolveProfile(
                source, activity, random, recentAssetIds, null,
                out resolved, out failureReason);
        }

        internal bool TryResolveProfile(
            LSPDCriminalProfileDefinition source,
            string activity,
            Random random,
            ISet<string> recentAssetIds,
            IEnumerable<string> requiredVehicleTypes,
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
            ISet<string> vehicleTypes = requiredVehicleTypes == null
                ? null
                : new HashSet<string>(requiredVehicleTypes.Where(value =>
                    !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
            LSPDCriminalAssetDefinition ped = Select(
                pedPoolId, "ped", random, recentAssetIds);
            LSPDCriminalAssetDefinition vehicle = Select(
                vehiclePoolId, "vehicle", random, recentAssetIds, vehicleTypes);
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
                Values(vehiclePoolId, "vehicle", random, 8, vehicleTypes),
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
            return Values(poolId, assetType, random, maximum, null);
        }

        private List<string> Values(
            string poolId,
            string assetType,
            Random random,
            int maximum,
            ISet<string> requiredVehicleTypes)
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
                .Where(value => IsVehicleAssetTypeAllowed(
                    value, assetType, requiredVehicleTypes))
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
            ISet<string> recentAssetIds,
            ISet<string> requiredVehicleTypes = null)
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
                .Where(value => IsVehicleAssetTypeAllowed(
                    value, assetType, requiredVehicleTypes))
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

        private static bool IsVehicleAssetTypeAllowed(
            LSPDCriminalAssetDefinition asset,
            string assetType,
            ISet<string> requiredVehicleTypes)
        {
            return requiredVehicleTypes == null || requiredVehicleTypes.Count == 0
                || !string.Equals(assetType, "vehicle", StringComparison.OrdinalIgnoreCase)
                || (asset != null && !string.IsNullOrWhiteSpace(asset.VehicleType)
                    && requiredVehicleTypes.Contains(asset.VehicleType));
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
        internal const float NearbyCatalogFallbackTriggerDistance = 250f;
        internal const float NearbyCatalogFallbackMaximumDistance = 100f;

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

        internal static LSPDDispatchLocationDefinition ForNearbyCatalogCandidate(
            string id,
            Vector3 position,
            string context)
        {
            return new LSPDDispatchLocationDefinition
            {
                Id = string.IsNullOrWhiteSpace(id) ? "nearby-catalog-candidate" : id.Trim(),
                Position = position,
                MinimumPlayerDistance = 0f,
                MaximumPlayerDistance = NearbyCatalogFallbackMaximumDistance,
                Contexts = string.IsNullOrWhiteSpace(context)
                    ? new List<string>()
                    : new List<string> { context.Trim() }
            };
        }

        internal static LSPDDispatchLocationDefinition ForPlayerRadiusVehicleScene(
            Vector3 playerPosition)
        {
            return new LSPDDispatchLocationDefinition
            {
                Id = "runtime.player-radius-vehicle-scene",
                Position = playerPosition,
                MinimumPlayerDistance = 0f,
                MaximumPlayerDistance = 100f,
                Contexts = new List<string> { "road_vehicle" }
            };
        }

        internal static LSPDDispatchLocationDefinition ForPlayerRadiusRoadFootScene(
            Vector3 playerPosition)
        {
            return new LSPDDispatchLocationDefinition
            {
                Id = "runtime.player-radius-road-foot-scene",
                Position = playerPosition,
                MinimumPlayerDistance = 0f,
                MaximumPlayerDistance = 100f,
                Contexts = new List<string> { "road_foot" }
            };
        }

        internal static LSPDDispatchLocationDefinition ForPlayerRadiusOutdoorFootScene(
            Vector3 playerPosition)
        {
            return new LSPDDispatchLocationDefinition
            {
                Id = "runtime.player-radius-outdoor-foot-scene",
                Position = playerPosition,
                MinimumPlayerDistance = 0f,
                MaximumPlayerDistance = 100f,
                Contexts = new List<string> { "any" }
            };
        }

        internal bool IsEligibleFor(Vector3 playerPosition)
        {
            float distance = Position.DistanceTo(playerPosition);
            return distance >= MinimumPlayerDistance && distance <= MaximumPlayerDistance;
        }

        internal bool IsFarFromDispatchArea(Vector3 playerPosition)
        {
            return HorizontalDistance(Position, playerPosition)
                >= NearbyCatalogFallbackTriggerDistance;
        }

        internal bool IsWithinNearbyCatalogFallbackDistance(
            Vector3 position,
            Vector3 playerPosition)
        {
            float distance = HorizontalDistance(position, playerPosition);
            return distance >= 0f
                && distance <= NearbyCatalogFallbackMaximumDistance
                && distance <= MaximumPlayerDistance;
        }

        private static float HorizontalDistance(Vector3 first, Vector3 second)
        {
            float x = first.X - second.X;
            float y = first.Y - second.Y;
            return (float)Math.Sqrt((x * x) + (y * y));
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
        internal bool RequiresExteriorScene { get; private set; }
        internal string RequiredPhysicalType { get; private set; }
        internal string SceneBehavior { get; private set; }
        internal string InteriorLocationId { get; private set; }
        internal bool RequiresMappedInterior { get; private set; }
        internal string SceneAnimationDictionary { get; private set; }
        internal string SceneAnimationPrimaryClip { get; private set; }
        internal string SceneAnimationSecondaryClip { get; private set; }
        internal string PreparedActivity { get; private set; }
        internal string PreArrivalActivity { get; private set; }
        internal List<string> SuspectResponseBranches { get; private set; }
        internal List<string> VictimResponseBranches { get; private set; }
        internal bool Enabled { get; private set; }
        internal bool RequiresVehicle { get; private set; }
        internal bool VehicleStagingRequired { get; private set; }
        internal string VehicleActivity { get; private set; }
        internal List<string> VehicleAssetTypes { get; private set; }
        internal bool Mobile { get; private set; }
        internal bool StartPursuitOnAccept { get; private set; }
        internal bool Armed { get; private set; }
        // Authored group size. A Dispatch group is not represented by idle
        // decoration peds: every counted member is a real suspect tracked by
        // Dispatch and can enter compliance, custody, or a terminal outcome.
        internal int SuspectCount { get; private set; }
        internal int MinimumSuspects { get; private set; }
        internal int MaximumSuspects { get; private set; }
        internal string VictimPoolId { get; private set; }
        internal int MinimumVictims { get; private set; }
        internal int MaximumVictims { get; private set; }
        internal List<string> DisturbanceActivities { get; private set; }
        internal string DisturbanceGunWeapon { get; private set; }
        internal string SceneParticipantPoolId { get; private set; }
        internal int MinimumSceneParticipants { get; private set; }
        internal int MaximumSceneParticipants { get; private set; }
        internal int AdditionalDeadVictims { get; private set; }
        internal bool VictimStartsDead { get; private set; }
        internal int Severity { get; private set; }
        internal int SelectionWeight { get; private set; }
        internal int MinimumCooldownSeconds { get; private set; }
        internal float SpawnDistance { get; private set; }
        internal int MinimumSuspectVehicles { get; private set; }
        internal int MaximumSuspectVehicles { get; private set; }
        internal Dictionary<string, int> SuspectResponseWeights { get; private set; }
        internal Dictionary<string, int> VictimResponseWeights { get; private set; }

        internal static LSPDDispatchEventDefinition FromXml(XElement node)
        {
            string locationContext = Optional(node, "locationContext", "any");
            if (!new[] { "any", "commercial", "financial", "residential", "alley", "road_foot", "road_vehicle" }
                .Contains(locationContext, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported locationContext: " + locationContext);

            string sceneBehavior = Optional(node, "sceneBehavior", "standard");
            if (!new[] { "standard", "interior_activity", "paired_meeting", "paired_exchange", "interior_store_custody", "interior_robbery_custody", "interior_attack_attempt", "interior_clerk_assault", "interior_clerk_snatch", "murder_victim_alley", "murder_victim_exterior", "murder_victim_interior" }
                .Contains(sceneBehavior, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported sceneBehavior: " + sceneBehavior);

            string requiredPhysicalType = Optional(node, "requiredPhysicalType", string.Empty);
            if (!string.IsNullOrWhiteSpace(requiredPhysicalType)
                && !new[] { "Bank", "BankBranch", "Store", "Storefront",
                    "ConvenienceStore", "ClothingStore", "GunStore", "LiquorStore",
                    "GasStation", "PawnShop", "HardwareStore", "SportingGoodsStore", "Pharmacy", "BarberShop", "TattooShop" }
                    .Contains(requiredPhysicalType, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported requiredPhysicalType: " + requiredPhysicalType);
            bool barberMappedStoreRobbery = string.Equals(
                    requiredPhysicalType, "BarberShop", StringComparison.OrdinalIgnoreCase)
                && string.Equals(sceneBehavior, "interior_robbery_custody",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(Required(node, "type"), "store_robbery",
                    StringComparison.OrdinalIgnoreCase);
            bool barberMappedAssault = string.Equals(
                    sceneBehavior, "interior_clerk_assault", StringComparison.OrdinalIgnoreCase)
                && string.Equals(requiredPhysicalType, "BarberShop", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Required(node, "type"), "violent_disturbance",
                    StringComparison.OrdinalIgnoreCase);
            bool barberMappedSnatch = string.Equals(
                    sceneBehavior, "interior_clerk_snatch", StringComparison.OrdinalIgnoreCase)
                && string.Equals(requiredPhysicalType, "BarberShop", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Required(node, "type"), "kidnapping",
                    StringComparison.OrdinalIgnoreCase);
            bool tattooMappedInteriorScene = string.Equals(
                    requiredPhysicalType, "TattooShop", StringComparison.OrdinalIgnoreCase)
                && ((string.Equals(sceneBehavior, "interior_robbery_custody", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Required(node, "type"), "store_robbery", StringComparison.OrdinalIgnoreCase))
                    || ((string.Equals(sceneBehavior, "interior_attack_attempt", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(sceneBehavior, "interior_clerk_assault", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(sceneBehavior, "murder_victim_interior", StringComparison.OrdinalIgnoreCase))
                        && string.Equals(Required(node, "type"), "violent_disturbance", StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(sceneBehavior, "interior_clerk_snatch", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Required(node, "type"), "kidnapping", StringComparison.OrdinalIgnoreCase)));
            if (string.Equals(requiredPhysicalType, "TattooShop", StringComparison.OrdinalIgnoreCase)
                && !tattooMappedInteriorScene)
                throw new InvalidDataException(
                    "TattooShop physical type is reserved for mapped interior robbery, attack, assault, snatch, and murder events.");
            bool mappedRetailAssault = string.Equals(
                    sceneBehavior, "interior_clerk_assault", StringComparison.OrdinalIgnoreCase)
                && IsMappedRetailParticipantType(requiredPhysicalType)
                && string.Equals(Required(node, "type"), "violent_disturbance",
                    StringComparison.OrdinalIgnoreCase);
            bool mappedRetailSnatch = string.Equals(
                    sceneBehavior, "interior_clerk_snatch", StringComparison.OrdinalIgnoreCase)
                && IsMappedRetailParticipantType(requiredPhysicalType)
                && string.Equals(Required(node, "type"), "kidnapping",
                    StringComparison.OrdinalIgnoreCase);
            bool mappedRetailMurder = string.Equals(
                    sceneBehavior, "murder_victim_interior", StringComparison.OrdinalIgnoreCase)
                && IsMappedRetailParticipantType(requiredPhysicalType)
                && string.Equals(Required(node, "type"), "violent_disturbance",
                    StringComparison.OrdinalIgnoreCase);
            bool mappedRetailAttack = (string.Equals(requiredPhysicalType, "GunStore",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(requiredPhysicalType, "ClothingStore",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(requiredPhysicalType, "TattooShop",
                    StringComparison.OrdinalIgnoreCase))
                && string.Equals(sceneBehavior, "interior_attack_attempt",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(Required(node, "type"), "violent_disturbance",
                    StringComparison.OrdinalIgnoreCase);
            bool requiresBarberInterior = string.Equals(
                requiredPhysicalType, "BarberShop", StringComparison.OrdinalIgnoreCase);
            if (requiresBarberInterior
                && !barberMappedStoreRobbery && !barberMappedAssault && !barberMappedSnatch)
                throw new InvalidDataException(
                    "BarberShop physical type is reserved for mapped interior robbery, clerk assault, and clerk snatch events.");
            if (!string.IsNullOrWhiteSpace(requiredPhysicalType)
                && !string.Equals(Required(node, "type"), "bank_robbery", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Required(node, "type"), "store_robbery", StringComparison.OrdinalIgnoreCase)
                && !mappedRetailAssault
                && !mappedRetailSnatch
                && !mappedRetailMurder
                && !mappedRetailAttack
                && !string.Equals(sceneBehavior, "interior_activity", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "A required physical store or bank type can only be used by a matching robbery or supported mapped retail scene.");

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
            bool pairedScene = string.Equals(sceneBehavior, "paired_meeting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneBehavior, "paired_exchange", StringComparison.OrdinalIgnoreCase);
            if (pairedScene
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
            if (string.Equals(sceneBehavior, "interior_clerk_snatch", StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(sceneAnimationDictionary, "anim@gangops@hostage@", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(sceneAnimationPrimaryClip, "perp_idle", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(sceneAnimationSecondaryClip, "victim_idle", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "A mapped interior shop snatch must define the paired suspect and victim animation clips.");

            int suspectCount = Math.Max(1, Integer(node, "suspectCount", 1));
            if (suspectCount > 15)
                throw new InvalidDataException(
                    "Dispatch suspect count cannot exceed the configured maximum of fifteen.");
            int minimumSuspects = IntegerInRange(
                node, "minimumSuspects", suspectCount, 1, 15);
            int maximumSuspects = IntegerInRange(
                node, "maximumSuspects", suspectCount, minimumSuspects, 15);
            if (pairedScene && minimumSuspects < 2)
                throw new InvalidDataException(
                    "A paired Dispatch scene requires at least two tracked suspects.");

            string victimPoolId = Optional(node, "victimPool", string.Empty);
            int minimumVictims = IntegerInRange(node, "minimumVictims", 0, 0, 1);
            int maximumVictims = IntegerInRange(
                node, "maximumVictims", minimumVictims, minimumVictims, 1);
            string sceneParticipantPoolId = Optional(node, "sceneParticipantPool", string.Empty);
            int minimumSceneParticipants = IntegerInRange(
                node, "minimumSceneParticipants", 0, 0, 5);
            int maximumSceneParticipants = IntegerInRange(
                node, "maximumSceneParticipants", minimumSceneParticipants,
                minimumSceneParticipants, 5);
            if (maximumSceneParticipants > 0
                && string.IsNullOrWhiteSpace(sceneParticipantPoolId))
                throw new InvalidDataException(
                    "A Dispatch event requiring additional scene participants must declare its sceneParticipantPool.");
            if (maximumSceneParticipants == 0
                && !string.IsNullOrWhiteSpace(sceneParticipantPoolId))
                throw new InvalidDataException(
                    "A Dispatch event cannot declare a sceneParticipantPool without scene participants.");
            if (!string.IsNullOrWhiteSpace(sceneParticipantPoolId)
                && !new[] { "shop_clerk", "shop_customer", "gun_store_clerk", "tattoo_store_clerk", "bank_clerk", "ambient_civilian" }.Contains(
                    sceneParticipantPoolId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Mapped retail scene participants must use an authored shop_clerk, shop_customer, gun_store_clerk, or tattoo_store_clerk NPC pool.");
            bool victimStartsDead = Bool(node, "victimStartsDead", false);
            int additionalDeadVictims = IntegerInRange(node, "additionalDeadVictims", 0, 0, 4);
            if (additionalDeadVictims > minimumSceneParticipants || (additionalDeadVictims > 0 && !victimStartsDead))
                throw new InvalidDataException("Extra authored murder victims require reserved civilian slots and a murder scene.");
            if (maximumVictims > 0 && string.IsNullOrWhiteSpace(victimPoolId))
                throw new InvalidDataException(
                    "A Dispatch event requiring a victim must declare its victimPool.");
            if (maximumVictims == 0 && !string.IsNullOrWhiteSpace(victimPoolId))
                throw new InvalidDataException(
                    "A Dispatch event cannot declare a victimPool when it has no victim role.");
            bool alleyMurderScene = string.Equals(
                sceneBehavior, "murder_victim_alley", StringComparison.OrdinalIgnoreCase);
            bool interiorMurderScene = string.Equals(
                sceneBehavior, "murder_victim_interior", StringComparison.OrdinalIgnoreCase);
            bool exteriorMurderScene = string.Equals(
                sceneBehavior, "murder_victim_exterior", StringComparison.OrdinalIgnoreCase);
            bool murderVictimScene = alleyMurderScene || exteriorMurderScene || interiorMurderScene;
            if (exteriorMurderScene
                && ((!string.Equals(locationContext, "any", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(locationContext, "road_foot", StringComparison.OrdinalIgnoreCase))
                    || Bool(node, "requiresVehicle", false)
                    || Bool(node, "vehicleStagingRequired", false)
                    || Bool(node, "requiresMappedInterior", false) || !string.IsNullOrWhiteSpace(requiredPhysicalType)))
                throw new InvalidDataException(
                    "Exterior murder requires a generic or roadside foot scene without interior or vehicle staging requirements.");
            if (victimStartsDead != murderVictimScene
                || (victimStartsDead
                    && (minimumVictims != 1 || maximumVictims != 1
                        || (alleyMurderScene
                            && !string.Equals(locationContext, "alley", StringComparison.OrdinalIgnoreCase))
                        || !string.Equals(Required(node, "type"), "violent_disturbance", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "A pre-existing dead victim requires a documented one-victim exterior, alley, or mapped-interior murder scene.");

            string preparedActivity = Optional(node, "preparedActivity", string.Empty);
            string[] supportedPreparedActivities = { "threat", "assault", "driver_assault", "shooting", "stalking", "camera", "intoxicated", "lying", "theft", "exchange", "gathering", "murder", "driving_fast", "driving_slow", "ramming", "carjacking" };
            if (!string.IsNullOrEmpty(preparedActivity)
                && !supportedPreparedActivities.Contains(preparedActivity, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported preparedActivity: " + preparedActivity);
            if ((preparedActivity == "exchange" || preparedActivity == "gathering") && minimumSuspects < 2)
                throw new InvalidDataException("Exchange/gathering needs at least two criminal participants.");
            if (preparedActivity.StartsWith("driving_", StringComparison.OrdinalIgnoreCase) || preparedActivity == "ramming")
                if (!Bool(node, "requiresVehicle", false) || !Bool(node, "mobile", false))
                    throw new InvalidDataException("A driving activity requires mobile vehicles.");
            string preArrivalActivity = Optional(node, "preArrivalActivity", "none");
            if (!new[] { "none", "guard", "disturbance", "murder_scene", "victim_threat", "victim_attack", "victim_snatch", "group_shootout", "active_shooter" }
                .Contains(preArrivalActivity, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported preArrivalActivity: " + preArrivalActivity);

            string disturbanceActivitiesText = Optional(
                node, "disturbanceActivities", string.Empty);
            List<string> disturbanceActivities = string.IsNullOrWhiteSpace(
                    disturbanceActivitiesText)
                ? new List<string>()
                : disturbanceActivitiesText.Split(',')
                    .Select(value => value.Trim())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            string[] supportedDisturbanceActivities =
            {
                "paparazzi", "beer_harassment", "armed_threat", "knife_threat"
            };
            if (disturbanceActivities.Count > 0
                && (!string.Equals(Required(node, "type"), "disturbance",
                    StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(preArrivalActivity, "disturbance",
                        StringComparison.OrdinalIgnoreCase)
                    || disturbanceActivities.Any(value =>
                        !supportedDisturbanceActivities.Contains(value,
                            StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "disturbanceActivities requires a disturbance pre-arrival event and supported authored activity names.");
            if (string.Equals(Required(node, "id"), "disturbance",
                    StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(Required(node, "type"), "disturbance",
                    StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(preArrivalActivity, "disturbance",
                        StringComparison.OrdinalIgnoreCase)
                    || minimumVictims != 1 || maximumVictims != 1
                    || !string.Equals(victimPoolId, "ambient_civilian",
                        StringComparison.OrdinalIgnoreCase)
                    || supportedDisturbanceActivities.Any(value =>
                        !disturbanceActivities.Contains(value,
                            StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "The generic disturbance event requires one ambient civilian and all three authored scene activities.");
            string disturbanceGunWeapon = Optional(
                node, "disturbanceGunWeapon", string.Empty);
            if (!string.IsNullOrWhiteSpace(disturbanceGunWeapon)
                && (!disturbanceGunWeapon.StartsWith("WEAPON_",
                    StringComparison.OrdinalIgnoreCase)
                    || !disturbanceActivities.Contains("armed_threat",
                        StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "disturbanceGunWeapon requires the authored armed_threat variation and a weapon name.");
            if (disturbanceActivities.Contains("armed_threat",
                    StringComparer.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(disturbanceGunWeapon))
                throw new InvalidDataException(
                    "The armed_threat variation requires an authored disturbanceGunWeapon.");
            if (string.Equals(preArrivalActivity, "murder_scene", StringComparison.OrdinalIgnoreCase)
                != murderVictimScene)
                throw new InvalidDataException(
                    "The murder_scene pre-arrival activity requires a documented exterior, alley, or mapped-interior murder scene.");
            if (string.Equals(preArrivalActivity, "victim_threat", StringComparison.OrdinalIgnoreCase)
                && (minimumVictims != 1 || maximumVictims != 1 || victimStartsDead
                    || string.IsNullOrWhiteSpace(victimPoolId)
                    || !Bool(node, "armed", false)))
                throw new InvalidDataException(
                    "The victim_threat activity requires one living victim and an armed suspect profile.");
            List<string> suspectResponseBranches = IdList(node, "suspectResponseBranches");
            if (string.Equals(preArrivalActivity, "group_shootout", StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(Required(node, "type"), "shots_fired", StringComparison.OrdinalIgnoreCase)
                    || (!string.Equals(locationContext, "road_foot", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(locationContext, "alley", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(locationContext, "any", StringComparison.OrdinalIgnoreCase))
                    || Bool(node, "requiresVehicle", false) || Bool(node, "vehicleStagingRequired", false)
                    || Bool(node, "requiresMappedInterior", false)
                    || minimumSuspects < 2 || !Bool(node, "armed", false)
                    || minimumVictims != 1 || maximumVictims != 1 || victimStartsDead
                    || !string.Equals(victimPoolId, "ambient_civilian", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "The group_shootout activity requires an armed group shots-fired scene with one living ambient civilian victim.");
            if (string.Equals(preArrivalActivity, "active_shooter", StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(Required(node, "type"), "shots_fired", StringComparison.OrdinalIgnoreCase)
                    || !new[] { "road_foot", "alley", "any" }.Contains(
                        locationContext, StringComparer.OrdinalIgnoreCase)
                    || Bool(node, "requiresVehicle", false)
                    || Bool(node, "vehicleStagingRequired", false)
                    || Bool(node, "requiresMappedInterior", false)
                    || minimumSuspects != 1 || maximumSuspects != 1
                    || !Bool(node, "armed", false)
                    || !new[] { "comply", "resist", "flee" }.All(value =>
                        suspectResponseBranches.Contains(value, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "The active_shooter activity requires one armed exterior foot suspect and the documented compliance, resistance, and flight outcomes.");
            if (string.Equals(preArrivalActivity, "victim_attack", StringComparison.OrdinalIgnoreCase)
                && (!mappedRetailAssault
                    || minimumVictims != 1 || maximumVictims != 1 || victimStartsDead
                    || (!string.Equals(victimPoolId, "shop_clerk", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "shop_customer", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "gun_store_clerk", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "tattoo_store_clerk", StringComparison.OrdinalIgnoreCase))
                    || Bool(node, "armed", false)))
                throw new InvalidDataException(
                    "The victim_attack activity requires one living shop participant and an unarmed mapped retail assault.");
            if (string.Equals(preArrivalActivity, "victim_snatch", StringComparison.OrdinalIgnoreCase)
                != string.Equals(sceneBehavior, "interior_clerk_snatch", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The victim_snatch activity must match the mapped interior clerk-snatch behavior.");

            List<string> victimResponseBranches = IdList(node, "victimResponseBranches");
            var supportedVictimResponses = new HashSet<string>(
                new[] { "attempt_escape", "remain_threatened" },
                StringComparer.OrdinalIgnoreCase);
            if (victimResponseBranches.Any(value => !supportedVictimResponses.Contains(value)))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported victim response branch: "
                    + victimResponseBranches.First(value => !supportedVictimResponses.Contains(value)));
            bool interiorClerkSnatchResponses = mappedRetailSnatch
                && string.Equals(locationContext, "commercial", StringComparison.OrdinalIgnoreCase)
                && string.Equals(preArrivalActivity, "victim_snatch", StringComparison.OrdinalIgnoreCase)
                && minimumVictims == 1 && maximumVictims == 1 && !victimStartsDead
                && !Bool(node, "requiresVehicle", false);
            if (victimResponseBranches.Count > 0
                && (!interiorClerkSnatchResponses
                    && (!string.Equals(Required(node, "type"), "kidnapping", StringComparison.OrdinalIgnoreCase)
                        || (!string.Equals(locationContext, "alley", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(locationContext, "any", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(locationContext, "road_foot", StringComparison.OrdinalIgnoreCase))
                        || !string.Equals(preArrivalActivity, "victim_threat", StringComparison.OrdinalIgnoreCase)
                        || minimumVictims != 1 || maximumVictims != 1 || victimStartsDead
                        || Bool(node, "requiresVehicle", false))))
                throw new InvalidDataException(
                    "Victim escape responses require one living victim in a documented foot kidnapping or authorized mapped retail snatch scene.");

            var supportedSuspectResponses = new HashSet<string>(
                new[] { "comply", "resist", "flee" },
                StringComparer.OrdinalIgnoreCase);
            if (suspectResponseBranches.Any(value => !supportedSuspectResponses.Contains(value)))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported suspect response branch: "
                    + suspectResponseBranches.First(value => !supportedSuspectResponses.Contains(value)));
            if ((alleyMurderScene || exteriorMurderScene)
                && !new[] { "comply", "resist", "flee" }.All(value =>
                    suspectResponseBranches.Contains(value, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "The documented alley-murder scene must retain compliance, armed resistance, and flight outcomes.");
            if (victimResponseBranches.Count > 0
                && !suspectResponseBranches.Contains("flee", StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Victim escape responses require a documented suspect flight branch.");

            string interiorLocationId = Optional(node, "interiorLocationId", string.Empty);
            bool vehicleStagingRequired = Bool(node, "vehicleStagingRequired", false);
            bool requiresVehicle = Bool(node, "requiresVehicle", false);
            string vehicleActivity = Optional(node, "vehicleActivity", "none").Trim().ToLowerInvariant();
            List<string> vehicleAssetTypes = IdList(node, "vehicleAssetTypes");
            if (!new[] { "none", "carjacking", "parked_theft", "moving_road" }
                .Contains(vehicleActivity, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Dispatch event has an unsupported vehicleActivity: " + vehicleActivity);
            if (vehicleActivity != "none" && !requiresVehicle)
                throw new InvalidDataException(
                    "A vehicle activity requires an authored suspect vehicle.");
            if (vehicleActivity != "none" && vehicleAssetTypes.Count == 0)
                throw new InvalidDataException(
                    "A vehicle entry activity requires authored Criminal Profile vehicle asset types.");
            if (vehicleAssetTypes.Count > 0 && !requiresVehicle)
                throw new InvalidDataException(
                    "Vehicle asset type filters require an authored suspect vehicle.");
            if ((vehicleActivity == "carjacking"
                    && !string.Equals(Required(node, "type"), "carjacking", StringComparison.OrdinalIgnoreCase))
                || (vehicleActivity == "parked_theft"
                    && !string.Equals(Required(node, "type"), "vehicle_theft", StringComparison.OrdinalIgnoreCase))
                || (vehicleActivity == "moving_road"
                    && !new[] { "reckless_driver", "vehicle_pursuit" }.Contains(
                        Required(node, "type"), StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "Vehicle activity must match its criminal Dispatch event type.");
            int minimumSuspectVehicles = IntegerInRange(
                node, "minimumSuspectVehicles", requiresVehicle ? 1 : 0, 0, 2);
            int maximumSuspectVehicles = IntegerInRange(
                node, "maximumSuspectVehicles", minimumSuspectVehicles, minimumSuspectVehicles, 2);
            if ((requiresVehicle && minimumSuspectVehicles < 1)
                || (!requiresVehicle && maximumSuspectVehicles > 0)
                || minimumSuspects < minimumSuspectVehicles)
                throw new InvalidDataException(
                    "Dispatch suspect-vehicle counts must be supported by the vehicle requirement and suspect range.");
            if (maximumSuspectVehicles > 1 && (!requiresVehicle || !Bool(node, "mobile", false)))
                throw new InvalidDataException(
                    "Multi-vehicle Dispatch scenes must be mobile vehicle incidents.");

            var responseWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string branch in supportedSuspectResponses)
            {
                bool allowed = suspectResponseBranches.Contains(branch, StringComparer.OrdinalIgnoreCase);
                string attribute = "response" + char.ToUpperInvariant(branch[0]) + branch.Substring(1) + "Weight";
                int weight = IntegerInRange(node, attribute, allowed ? 1 : 0, 0, 1000);
                if (!allowed && weight > 0)
                    throw new InvalidDataException(
                        "A Dispatch response weight cannot enable a behavior outside suspectResponseBranches: " + branch);
                if (allowed)
                    responseWeights[branch] = weight;
            }
            if (responseWeights.Count > 0 && responseWeights.Values.All(value => value == 0))
                throw new InvalidDataException(
                    "A Dispatch event with suspect response branches requires at least one positive response weight.");

            var victimResponseWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string branch in supportedVictimResponses)
            {
                bool allowed = victimResponseBranches.Contains(branch, StringComparer.OrdinalIgnoreCase);
                string attribute = branch == "attempt_escape"
                    ? "victimAttemptEscapeWeight" : "victimRemainThreatenedWeight";
                int weight = IntegerInRange(node, attribute, allowed ? 1 : 0, 0, 1000);
                if (!allowed && weight > 0)
                    throw new InvalidDataException(
                        "A Dispatch victim response weight cannot enable a behavior outside victimResponseBranches: " + branch);
                if (allowed)
                    victimResponseWeights[branch] = weight;
            }
            if (victimResponseWeights.Count > 0
                && victimResponseWeights.Values.All(value => value == 0))
                throw new InvalidDataException(
                    "A Dispatch event with victim response branches requires at least one positive response weight.");

            bool interiorStoreCustody = string.Equals(
                sceneBehavior, "interior_store_custody", StringComparison.OrdinalIgnoreCase);
            bool mappedInteriorRobberyCustody = string.Equals(
                sceneBehavior, "interior_robbery_custody", StringComparison.OrdinalIgnoreCase);
            bool mappedInteriorAttackAttempt = string.Equals(
                sceneBehavior, "interior_attack_attempt", StringComparison.OrdinalIgnoreCase);
            bool mappedInteriorClerkAssault = string.Equals(
                sceneBehavior, "interior_clerk_assault", StringComparison.OrdinalIgnoreCase);
            bool mappedInteriorClerkSnatch = string.Equals(
                sceneBehavior, "interior_clerk_snatch", StringComparison.OrdinalIgnoreCase);
            bool mappedInteriorMurderScene = interiorMurderScene;
            bool interiorActivity = string.Equals(sceneBehavior, "interior_activity", StringComparison.OrdinalIgnoreCase);
            if (interiorActivity && (string.IsNullOrEmpty(preparedActivity)
                || string.IsNullOrEmpty(requiredPhysicalType) || Bool(node, "requiresVehicle", false)))
                throw new InvalidDataException("An authored interior activity needs a physical site, activity, and foot actors.");
            bool inferredMappedInterior = interiorActivity || mappedInteriorRobberyCustody
                || mappedInteriorAttackAttempt
                || mappedInteriorClerkAssault
                || mappedInteriorClerkSnatch
                || mappedInteriorMurderScene;
            bool requiresMappedInterior = Bool(
                node, "requiresMappedInterior", inferredMappedInterior);
            bool requiresExteriorScene = Bool(node, "requiresExteriorScene", false);
            if (requiresExteriorScene && (requiresMappedInterior
                || !string.IsNullOrWhiteSpace(interiorLocationId)))
                throw new InvalidDataException("An exterior-only Dispatch event cannot require an interior.");
            if (requiresMappedInterior != inferredMappedInterior)
                throw new InvalidDataException(
                    "requiresMappedInterior must match a mapped interior robbery, attack, assault, snatch, or murder scene behavior.");
            if (interiorStoreCustody
                && (!string.Equals(Required(node, "type"), "store_robbery", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(interiorLocationId)
                    || suspectCount != 1
                    || minimumVictims != 1
                    || maximumVictims != 1
                    || !string.Equals(victimPoolId, "shop_clerk", StringComparison.OrdinalIgnoreCase)
                    || !Bool(node, "armed", false)
                    || Bool(node, "requiresVehicle", false)
                    || hasSceneAnimation))
                throw new InvalidDataException(
                    "An interior store-custody scene requires one armed store suspect, one shop-clerk victim, an exact interior site, no suspect vehicle, and no paired animation.");
            string mappedInteriorRobberyVictimPool =
                string.Equals(Required(node, "type"), "bank_robbery", StringComparison.OrdinalIgnoreCase)
                    ? "bank_clerk"
                    : string.Equals(requiredPhysicalType, "GunStore", StringComparison.OrdinalIgnoreCase)
                        ? "gun_store_clerk"
                        : string.Equals(requiredPhysicalType, "TattooShop", StringComparison.OrdinalIgnoreCase)
                            ? "tattoo_store_clerk" : "shop_clerk";
            if (mappedInteriorRobberyCustody
                && ((!string.Equals(Required(node, "type"), "store_robbery", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Required(node, "type"), "bank_robbery", StringComparison.OrdinalIgnoreCase))
                    || !string.IsNullOrWhiteSpace(interiorLocationId)
                    || suspectCount < minimumSuspects
                    || suspectCount > maximumSuspects
                    || minimumSuspects < 1
                    || maximumSuspects > 5
                    || minimumVictims != 1
                    || maximumVictims != 1
                    || !string.Equals(victimPoolId, mappedInteriorRobberyVictimPool,
                        StringComparison.OrdinalIgnoreCase)
                    || !Bool(node, "armed", false)
                    || Bool(node, "requiresVehicle", false)
                    || vehicleStagingRequired
                    || hasSceneAnimation
                    || !new[] { "comply", "resist" }.All(value =>
                        suspectResponseBranches.Contains(value, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "A mapped interior robbery requires one to five armed bank/store suspects, one matching clerk or civilian victim, compliance and resistance branches, no vehicle or exterior staging, and no paired animation.");
            if (mappedInteriorAttackAttempt
                && (!string.Equals(Required(node, "type"), "violent_disturbance", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(interiorLocationId)
                    || minimumSuspects < 1
                    || maximumSuspects > 5
                    || minimumVictims != 1
                    || maximumVictims != 1
                    || victimStartsDead
                    || !Bool(node, "armed", false)
                    || Bool(node, "requiresVehicle", false)
                    || hasSceneAnimation
                    || !new[] { "comply", "resist" }.All(value =>
                        suspectResponseBranches.Contains(value, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "A mapped interior attack-attempt scene requires an armed suspect group, one living victim, compliance and resistance branches, no suspect vehicle, and no paired animation.");
            if (mappedInteriorClerkAssault
                && (!mappedRetailAssault
                    || !string.Equals(locationContext, "commercial", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(interiorLocationId)
                    || minimumSuspects < 1
                    || maximumSuspects > 5
                    || minimumVictims != 1
                    || maximumVictims != 1
                    || victimStartsDead
                    || (!string.Equals(victimPoolId, "shop_clerk", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "shop_customer", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "gun_store_clerk", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "tattoo_store_clerk", StringComparison.OrdinalIgnoreCase))
                    || Bool(node, "armed", false)
                    || Bool(node, "requiresVehicle", false)
                    || vehicleStagingRequired
                    || hasSceneAnimation
                    || !string.Equals(preArrivalActivity, "victim_attack", StringComparison.OrdinalIgnoreCase)
                    || !new[] { "comply", "resist", "flee" }.All(value =>
                        suspectResponseBranches.Contains(value, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "A mapped retail assault scene requires an unarmed suspect, one living shop participant, physical response branches, and no vehicle, staging, or paired animation.");
            if (mappedInteriorClerkSnatch
                && (!interiorClerkSnatchResponses
                    || !string.Equals(locationContext, "commercial", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(interiorLocationId)
                    || minimumSuspects < 1
                    || maximumSuspects > 2
                    || minimumVictims != 1
                    || maximumVictims != 1
                    || victimStartsDead
                    || (!string.Equals(victimPoolId, "shop_clerk", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "shop_customer", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "gun_store_clerk", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(victimPoolId, "tattoo_store_clerk", StringComparison.OrdinalIgnoreCase))
                    || !Bool(node, "armed", false)
                    || Bool(node, "requiresVehicle", false)
                    || vehicleStagingRequired
                    || !hasSceneAnimation
                    || !new[] { "comply", "resist", "flee" }.All(value =>
                        suspectResponseBranches.Contains(value, StringComparer.OrdinalIgnoreCase))
                    || !new[] { "attempt_escape", "remain_threatened" }.All(value =>
                        victimResponseBranches.Contains(value, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "A mapped retail snatch scene requires one living shop participant, an armed foot suspect, suspect and victim response branches, paired hostage animation, and no vehicle or exterior staging.");
            if (mappedInteriorMurderScene
                && (!string.Equals(Required(node, "type"), "violent_disturbance", StringComparison.OrdinalIgnoreCase)
                    || minimumSuspects < 1
                    || maximumSuspects > 5
                    || minimumVictims != 1
                    || maximumVictims != 1
                    || !victimStartsDead
                    || !Bool(node, "armed", false)
                    || Bool(node, "requiresVehicle", false)
                    || hasSceneAnimation
                    || !new[] { "comply", "resist" }.All(value =>
                        suspectResponseBranches.Contains(value, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "A mapped interior murder scene requires an armed suspect group, one catalog-defined dead victim, compliance and resistance branches, no suspect vehicle, and no paired animation.");
            bool mappedCommercialSceneParticipants = string.Equals(
                    locationContext, "commercial", StringComparison.OrdinalIgnoreCase)
                && IsMappedRetailParticipantType(requiredPhysicalType);
            bool mappedFinancialSceneParticipants = string.Equals(
                    locationContext, "financial", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(requiredPhysicalType, "Bank", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(requiredPhysicalType, "BankBranch", StringComparison.OrdinalIgnoreCase));
            if (maximumSceneParticipants > 0
                && string.IsNullOrEmpty(preparedActivity)
                && (!requiresMappedInterior
                    || (!mappedInteriorRobberyCustody && !mappedInteriorClerkAssault
                        && !mappedInteriorClerkSnatch && !mappedInteriorMurderScene
                        && !mappedRetailAttack)
                    || (!mappedCommercialSceneParticipants && !mappedFinancialSceneParticipants)
                    || Bool(node, "requiresVehicle", false)
                    || vehicleStagingRequired))
                throw new InvalidDataException(
                    "Additional store or bank participants require a mapped commercial/financial scene without vehicle staging.");
            if (!interiorActivity && !interiorStoreCustody && !mappedInteriorRobberyCustody
                && !mappedInteriorAttackAttempt && !mappedInteriorClerkAssault
                && !mappedInteriorClerkSnatch && !mappedInteriorMurderScene
                && (!string.IsNullOrWhiteSpace(interiorLocationId) || vehicleStagingRequired))
                throw new InvalidDataException(
                    "Only a scene with an explicit interior behavior may declare an interior site or separate vehicle staging.");

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
                RequiresExteriorScene = requiresExteriorScene,
                RequiredPhysicalType = requiredPhysicalType,
                SceneBehavior = sceneBehavior,
                InteriorLocationId = interiorLocationId,
                RequiresMappedInterior = requiresMappedInterior,
                SceneAnimationDictionary = sceneAnimationDictionary,
                SceneAnimationPrimaryClip = sceneAnimationPrimaryClip,
                SceneAnimationSecondaryClip = sceneAnimationSecondaryClip,
                PreparedActivity = preparedActivity,
                PreArrivalActivity = preArrivalActivity,
                SuspectResponseBranches = suspectResponseBranches,
                VictimResponseBranches = victimResponseBranches,
                SuspectResponseWeights = responseWeights,
                VictimResponseWeights = victimResponseWeights,
                Enabled = Bool(node, "enabled", true),
                RequiresVehicle = requiresVehicle,
                VehicleStagingRequired = vehicleStagingRequired,
                VehicleActivity = vehicleActivity,
                VehicleAssetTypes = vehicleAssetTypes,
                Mobile = Bool(node, "mobile", false),
                StartPursuitOnAccept = Bool(node, "startPursuitOnAccept", false),
                Armed = Bool(node, "armed", false),
                SuspectCount = suspectCount,
                MinimumSuspects = minimumSuspects,
                MaximumSuspects = maximumSuspects,
                VictimPoolId = victimPoolId,
                MinimumVictims = minimumVictims,
                MaximumVictims = maximumVictims,
                DisturbanceActivities = disturbanceActivities,
                DisturbanceGunWeapon = disturbanceGunWeapon,
                SceneParticipantPoolId = sceneParticipantPoolId,
                MinimumSceneParticipants = minimumSceneParticipants,
                MaximumSceneParticipants = maximumSceneParticipants,
                AdditionalDeadVictims = additionalDeadVictims,
                VictimStartsDead = victimStartsDead,
                Severity = Integer(node, "severity", 1),
                SelectionWeight = IntegerInRange(node, "selectionWeight", 1, 1, 1000),
                MinimumCooldownSeconds = Integer(node, "minimumCooldownSeconds", 90),
                SpawnDistance = Float(node, "spawnDistance", 90f),
                MinimumSuspectVehicles = minimumSuspectVehicles,
                MaximumSuspectVehicles = maximumSuspectVehicles
            };
        }

        internal int ChooseSuspectCount(Random random, int maximumAllowed)
        {
            int maximum = Math.Min(MaximumSuspects, Math.Max(1, Math.Min(15, maximumAllowed)));
            if (MinimumSuspects > maximum)
                return 0;
            return MinimumSuspects >= maximum || random == null
                ? MinimumSuspects
                : random.Next(MinimumSuspects, maximum + 1);
        }

        internal int ChooseVictimCount(Random random)
        {
            if (MaximumVictims <= MinimumVictims || random == null)
                return MinimumVictims;
            return random.Next(MinimumVictims, MaximumVictims + 1);
        }

        internal string ChooseDisturbanceActivity(Random random)
        {
            if (DisturbanceActivities == null || DisturbanceActivities.Count == 0)
                return string.Empty;
            int index = random == null ? 0 : random.Next(DisturbanceActivities.Count);
            return DisturbanceActivities[index];
        }

        internal int ChooseSceneParticipantCount(Random random)
        {
            if (MaximumSceneParticipants <= MinimumSceneParticipants || random == null)
                return MinimumSceneParticipants;
            return random.Next(MinimumSceneParticipants, MaximumSceneParticipants + 1);
        }

        private static bool IsMappedRetailParticipantType(string physicalType)
        {
            return string.Equals(physicalType, "BarberShop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(physicalType, "GunStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(physicalType, "TattooShop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(physicalType, "ConvenienceStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(physicalType, "ClothingStore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(physicalType, "LiquorStore", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsVictimStateReady(
            bool victimStartsDead,
            bool victimIsDead,
            int victimHealth)
        {
            return victimStartsDead
                ? victimIsDead || victimHealth <= 0
                : !victimIsDead && victimHealth > 0;
        }

        internal int ChooseSuspectVehicleCount(Random random, int maximumAllowed)
        {
            int maximum = Math.Min(MaximumSuspectVehicles, Math.Max(0, Math.Min(2, maximumAllowed)));
            if (MinimumSuspectVehicles > maximum)
                return -1;
            return MinimumSuspectVehicles >= maximum || random == null
                ? MinimumSuspectVehicles
                : random.Next(MinimumSuspectVehicles, maximum + 1);
        }

        internal string ChooseSuspectResponse(Random random, IEnumerable<string> eligibleBranches)
        {
            if (eligibleBranches == null || SuspectResponseWeights == null)
                return string.Empty;
            List<string> candidates = eligibleBranches
                .Where(value => !string.IsNullOrWhiteSpace(value)
                    && SuspectResponseWeights.ContainsKey(value)
                    && SuspectResponseWeights[value] > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int total = candidates.Sum(value => SuspectResponseWeights[value]);
            if (total <= 0)
                return string.Empty;
            int roll = random == null ? 0 : random.Next(total);
            foreach (string candidate in candidates)
            {
                roll -= SuspectResponseWeights[candidate];
                if (roll < 0)
                    return candidate;
            }
            return candidates[candidates.Count - 1];
        }

        internal string ChooseVictimResponse(Random random, IEnumerable<string> eligibleBranches)
        {
            if (eligibleBranches == null || VictimResponseWeights == null)
                return string.Empty;
            List<string> candidates = eligibleBranches
                .Where(value => !string.IsNullOrWhiteSpace(value)
                    && VictimResponseWeights.ContainsKey(value)
                    && VictimResponseWeights[value] > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int total = candidates.Sum(value => VictimResponseWeights[value]);
            if (total <= 0)
                return string.Empty;
            int roll = random == null ? 0 : random.Next(total);
            foreach (string candidate in candidates)
            {
                roll -= VictimResponseWeights[candidate];
                if (roll < 0)
                    return candidate;
            }
            return candidates[candidates.Count - 1];
        }

        internal bool CanUseArea(
            LSPDDispatchLocationDefinition area,
            Vector3 playerPosition)
        {
            return CanUseAreaContext(area)
                && area.IsEligibleFor(playerPosition);
        }

        internal bool CanUseAreaContext(LSPDDispatchLocationDefinition area)
        {
            return area != null
                && LocationIds != null
                && LocationIds.Contains(area.Id, StringComparer.OrdinalIgnoreCase)
                && area.SupportsContext(LocationContext);
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

        private static int IntegerInRange(
            XElement node,
            string attribute,
            int fallback,
            int minimum,
            int maximum)
        {
            int value;
            if (!int.TryParse((string)node.Attribute(attribute), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value))
                return fallback;
            if (value < minimum || value > maximum)
                throw new InvalidDataException(
                    "Dispatch event attribute is outside range: " + attribute);
            return value;
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
        internal string RequiredPhysicalType { get; set; }
        internal string SceneBehavior { get; set; }
        internal string InteriorLocationId { get; set; }
        internal string SceneAnimationDictionary { get; set; }
        internal string SceneAnimationPrimaryClip { get; set; }
        internal string SceneAnimationSecondaryClip { get; set; }
        internal string PreparedActivity { get; set; }
        internal bool PreparedActivityStarted { get; set; }
        internal bool PreparedActivityConfirmed { get; set; }
        internal bool PreparedActivityAudioReported { get; set; }
        internal HashSet<int> ResolvedSuspectHandles { get; private set; } = new HashSet<int>();
        internal Dictionary<int, DateTime> ActivityLastEvidenceAt { get; private set; } = new Dictionary<int, DateTime>();
        internal DateTime PreparedActivityStartedAt { get; set; }
        internal DateTime PreparedActivityNextSampleAt { get; set; }
        internal int PreparedActivitySamples { get; set; }
        internal bool RammingImpactConfirmed { get; set; }
        internal Dictionary<int, int> ActivityStages { get; private set; } = new Dictionary<int, int>();
        internal int PreparedActivityRecoveryCount { get; set; }
        internal Dictionary<int, Vector3> ActivitySamplePositions { get; private set; } = new Dictionary<int, Vector3>();
        internal Dictionary<int, DateTime> ActivityTaskIssuedAt { get; private set; } = new Dictionary<int, DateTime>();
        internal Dictionary<int, string> ActivityRoles { get; private set; } = new Dictionary<int, string>();
        internal HashSet<int> ActivityEvidenceHandles { get; private set; } = new HashSet<int>();
        internal List<Prop> ActivityProps { get; private set; } = new List<Prop>();
        internal string PreArrivalActivity { get; set; }
        internal List<string> SuspectResponseBranches { get; set; }
        internal List<string> VictimResponseBranches { get; set; }
        internal string CriminalProfileId { get; set; }
        internal string CriminalDisposition { get; set; }
        internal string SuspectModel { get; set; }
        internal List<string> SuspectModelCandidates { get; set; }
        internal string VehicleModel { get; set; }
        internal List<string> VehicleModelCandidates { get; set; }
        internal string WeaponName { get; set; }
        internal List<string> WeaponNameCandidates { get; set; }
        internal bool RequiresVehicle { get; set; }
        internal bool VehicleStagingRequired { get; set; }
        internal string VehicleActivity { get; set; }
        internal bool Mobile { get; set; }
        internal bool StartPursuitOnAccept { get; set; }
        internal bool Armed { get; set; }
        internal int Severity { get; set; }
        internal Dictionary<string, int> SuspectResponseWeights { get; set; }
        internal Dictionary<string, int> VictimResponseWeights { get; set; }
        internal int SuspectCount { get; set; }
        internal int SuspectVehicleCount { get; set; }
        internal string VictimPoolId { get; set; }
        internal int MinimumVictims { get; set; }
        internal int MaximumVictims { get; set; }
        internal int VictimCount { get; set; }
        internal List<string> DisturbanceActivities { get; set; }
        internal string DisturbanceVariant { get; set; }
        internal string DisturbanceGunWeapon { get; set; }
        internal string DisturbanceWeaponName { get; set; }
        internal DateTime DisturbanceActivityStartedAt { get; set; }
        internal DateTime DisturbanceObservationStartedAt { get; set; }
        internal DateTime DisturbanceResponseEarliestAt { get; set; }
        internal DateTime DisturbanceNextTaskAt { get; set; }
        internal string DisturbanceAnimationDictionary { get; set; }
        internal string DisturbanceAnimationClip { get; set; }
        internal int DisturbanceAnimationStartAttempts { get; set; }
        internal bool DisturbanceAnimationRequested { get; set; }
        internal bool DisturbanceAnimationConfirmedPlaying { get; set; }
        internal bool DisturbancePresentationReady { get; set; }
        internal bool DisturbanceVictimReactionStarted { get; set; }
        internal bool DisturbanceVictimReactionFailureLogged { get; set; }
        internal bool DisturbanceWeaponPrepared { get; set; }
        internal bool DisturbanceWeaponDrawn { get; set; }
        internal bool DisturbanceWeaponStageConfirmed { get; set; }
        internal DateTime ActiveShooterActivityStartedAt { get; set; }
        internal DateTime ActiveShooterLastShotAt { get; set; }
        internal DateTime ActiveShooterNextShotAt { get; set; }
        internal DateTime ActiveShooterFireTaskIssuedAt { get; set; }
        internal bool ActiveShooterFireConfirmationLogged { get; set; }
        internal bool ActiveShooterActivityStopped { get; set; }
        internal string ActiveShooterWeaponName { get; set; }
        internal Vector3 ActiveShooterLastShotTarget { get; set; }
        internal int ActiveShooterDirectionIndex { get; set; }
        internal int ActiveShooterShotCount { get; set; }
        internal int ActiveShooterCommandTriggerSuspectHandle { get; set; }
        internal bool DisturbancePropFailureLogged { get; set; }
        internal int DisturbancePropLoadAttempts { get; set; }
        internal DateTime DisturbancePropNextAttemptAt { get; set; }
        internal Prop DisturbanceProp { get; set; }
        internal string SceneParticipantPoolId { get; set; }
        internal int SceneParticipantCount { get; set; }
        internal List<string> SceneParticipantModelNames { get; private set; } = new List<string>();
        internal HashSet<int> SceneParticipantAmbientClaimAttemptedIndexes { get; private set; } = new HashSet<int>();
        internal HashSet<int> ExistingAmbientSceneParticipantHandles { get; private set; } = new HashSet<int>();
        internal HashSet<int> SceneParticipantBehaviorInitializedHandles { get; private set; } = new HashSet<int>();
        internal int AdditionalDeadVictims { get; set; }
        internal HashSet<int> InvestigatedVictimHandles { get; private set; } = new HashSet<int>();
        internal bool VictimStartsDead { get; set; }
        internal bool VictimInvestigated { get; set; }
        internal bool VictimEncounterResponseStarted { get; set; }
        internal string VictimEncounterResponse { get; set; }
        internal string VictimModelName { get; set; }
        internal bool PreArrivalSceneBehaviorInitialized { get; set; }
        internal bool PreArrivalVictimThreatStarted { get; set; }
        internal DateTime NextGroupShootoutShotAt { get; set; }
        internal bool VictimAmbientReuseEvaluated { get; set; }
        internal bool PlayerEncounterResponseStarted { get; set; }
        internal int DisturbanceCommandTriggerSuspectHandle { get; set; }
        internal DateTime LastPursuitWaypointAt { get; set; }
        internal DateTime LastPursuitRadioUpdateAt { get; set; }
        internal Vector3 LastPursuitWaypointPosition { get; set; }
        internal bool MappedInteriorEntryTrackingInitialized { get; set; }
        internal bool MappedInteriorPlayerStartedInside { get; set; }
        internal bool MappedInteriorOutsideDoorwayObserved { get; set; }
        internal bool MappedInteriorDoorwayCrossingObserved { get; set; }
        internal DateTime LastMappedInteriorEntrySampleAt { get; set; }
        internal DateTime LastMappedInteriorOutsideDoorwayAt { get; set; }
        internal DateTime MappedInteriorDoorwayCrossedAt { get; set; }
        internal DateTime LastMappedInteriorEscalationCheckAt { get; set; }
        internal string MappedInteriorEntryEvidence { get; set; }
        internal int PursuitLocationUpdateCount { get; set; }
        internal DateTime LastEncounterResponseCheckAt { get; set; }
        internal Vector3 Origin { get; set; }
        internal AmbientDispatchSceneResolution SceneResolution { get; set; }
        internal Dictionary<int, Vector3> PreparedExteriorActorPositions { get; private set; } =
            new Dictionary<int, Vector3>();
        internal Dictionary<int, Vector3> PreparedExteriorActorGroundPositions { get; private set; } =
            new Dictionary<int, Vector3>();
        internal HashSet<int> PreparedExteriorActorCorrectionAttempted { get; private set; } =
            new HashSet<int>();
        internal bool PairedSceneFormationCorrectionAttempted { get; set; }
        internal Ped Suspect { get; set; }
        // Outdoor authored suspect groups receive a private occurrence group
        // so their members do not treat one another as valid targets. Dispatch
        // retains each Ped's prior group for physical custody restoration.
        internal int SuspectOccurrenceRelationshipGroupHash { get; set; }
        internal string SuspectOccurrenceRelationshipGroupName { get; set; }
        internal bool SuspectOccurrenceRelationshipGroupConfigured { get; set; }
        internal bool SuspectOccurrenceRelationshipGroupFailureLogged { get; set; }
        internal Dictionary<int, int> OriginalSuspectRelationshipGroupByHandle { get; private set; } =
            new Dictionary<int, int>();
        // Suspect remains the primary target for legacy UI/status access.
        // Suspects contains every live criminal owned by this Dispatch event,
        // while ArrestedSuspects is the exact list handed to Convoy.
        internal List<Ped> Suspects { get; private set; } = new List<Ped>();
        internal List<Ped> ArrestedSuspects { get; private set; } = new List<Ped>();
        internal List<Ped> CompliantSuspects { get; private set; } = new List<Ped>();
        // A weighted response or Backup's compliance task is not a Player
        // Secure command. Keep the explicit E interaction separate so injury
        // handling does not mistake an authored hands-up response for custody.
        internal HashSet<int> PlayerCommandedSecureSuspectHandles { get; private set; } =
            new HashSet<int>();
        // A wounded/downed suspect is still owned by Dispatch and remains
        // arrestable, but must no longer be selected as a live pursuit target.
        internal HashSet<int> PursuitContainedSuspectHandles { get; private set; } =
            new HashSet<int>();
        internal Dictionary<int, string> SuspectResponses { get; private set; } = new Dictionary<int, string>();
        // Some urgent callouts have a second Police-owned participant.  The
        // victim/witness stays in the scene until the officer has investigated
        // it, so a carjacking or robbery is visibly different from a lone
        // fleeing suspect.
        internal Ped Victim { get; set; }
        internal bool VictimWasExistingAmbient { get; set; }
        internal Vehicle VictimVehicle { get; set; }
        internal bool VictimVehicleRecovered { get; set; }
        internal List<Ped> AdditionalParticipants { get; private set; } = new List<Ped>();
        internal Vehicle SuspectVehicle { get; set; }
        internal List<Vehicle> SuspectVehicles { get; private set; } = new List<Vehicle>();
        internal Dictionary<int, int> SuspectVehicleByPedHandle { get; private set; } = new Dictionary<int, int>();
        internal Dictionary<int, int> VehicleEntrySeatByPedHandle { get; private set; } = new Dictionary<int, int>();
        internal HashSet<int> VehicleEncounterResponseHandles { get; private set; } = new HashSet<int>();
        internal int VehicleActivityStage { get; set; }
        internal bool VehicleActivityThreatStarted { get; set; }
        internal int VehicleActivityEntryAttempts { get; set; }
        internal DateTime VehicleActivityStartedAt { get; set; }
        internal DateTime VehicleActivityNextTaskAt { get; set; }
        internal DateTime VehicleActivityNextCheckAt { get; set; }
        internal DateTime VehicleActivityEntryDeadlineAt { get; set; }
        internal Vehicle ParkedPlayerUtility { get; set; }
        internal bool InteriorUtilityRequirementReported { get; set; }
        internal bool InteriorUtilityArrivalValidated { get; set; }
        internal bool InteriorUtilityStagingFailureReported { get; set; }
        internal bool MurderVictimInvestigationWaitReported { get; set; }
        internal bool OwnedByDispatch { get; set; }
        internal bool HasConvoyCustodyHandoff { get; set; }
        internal HashSet<int> ConvoyCustodySuspectHandles { get; private set; } =
            new HashSet<int>();
        internal bool ScenePreparationRequested { get; set; }
        internal bool ScenePreparationCompleted { get; set; }
        internal DateTime ScenePreparationDeadline { get; set; }
        internal DateTime ScenePreparationNextRetryAt { get; set; }
        internal int ScenePreparationActorRetryCount { get; set; }
        internal int ScenePreparationVehicleGroundRetryCount { get; set; }
        internal string ScenePreparationFailureStage { get; set; }
        internal string ScenePreparationFailureReason { get; set; }
        internal bool SceneBehaviorInitialized { get; set; }
        internal bool SuspectEscalationTriggered { get; set; }
        internal bool SuspectGunfireReported { get; set; }
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
        internal bool PlayerHandcuffAnimationStarted { get; set; }
        internal int PlayerHandcuffTargetHandle { get; set; }
        internal DateTime PlayerHandcuffStartedAt { get; set; }
        internal HashSet<int> BackupArrestInProgressSuspectHandles { get; private set; } =
            new HashSet<int>();
        internal HashSet<int> BackupArrestInProgressOfficerHandles { get; private set; } =
            new HashSet<int>();
        internal HashSet<int> BackupArrestAttemptedSuspectHandles { get; private set; } =
            new HashSet<int>();
        internal bool ArrestSecured { get; set; }
        internal bool GroupComplianceRequested { get; set; }
        internal string ActiveAudioScope { get; set; }
        internal string LastAudioStage { get; set; }
        internal bool SceneApproachReported { get; set; }
        internal bool SceneArrivalReported { get; set; }
        internal DateTime NextEnRouteStatusAt { get; set; }
        internal int EnRouteStatusUpdateCount { get; set; }
        internal bool InvestigationReported { get; set; }
        internal LSPDDispatchState State { get; set; }
        internal DateTime CreatedAt { get; set; }
        internal DateTime StateChangedAt { get; set; }
        internal Vector3 LastSuspectPosition { get; set; }
        internal DateTime LastMovementSampleAt { get; set; }
        internal bool SuspectMovementConfirmed { get; set; }
        internal int LastMovementSampleTargetHandle { get; set; }
        internal Dictionary<int, int> PursuitVehicleHealthAtStart { get; private set; } =
            new Dictionary<int, int>();
        internal Dictionary<int, DateTime> PursuitVehicleStoppedAt { get; private set; } =
            new Dictionary<int, DateTime>();
        internal Dictionary<int, DateTime> PursuitVehicleExitTaskAt { get; private set; } =
            new Dictionary<int, DateTime>();
        internal HashSet<int> PursuitVehicleExitRequestedHandles { get; private set; } =
            new HashSet<int>();
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
        // The interior clerk-snatch branch routes its victim through the same
        // authored doorway after the suspect. These fields retain one bounded
        // victim route session across Dispatch ticks and retries.
        internal AmbientInteriorAccessRoute InteriorVictimExitRoute { get; set; }
        internal int InteriorVictimExitPhase { get; set; }
        internal int InteriorVictimExitRecoveryCount { get; set; }
        internal DateTime InteriorVictimExitStartedAt { get; set; }
        internal DateTime InteriorVictimExitNextTaskAt { get; set; }
        internal bool InteriorVictimExitCompleted { get; set; }

        internal static bool RequiresOutdoorSuspectOccurrenceRelationshipGroup(
            int suspectCount,
            bool isInteriorScene)
        {
            return suspectCount > 1 && !isInteriorScene;
        }

        internal static int ResolveDispatchCustodyOriginalRelationshipGroup(
            int suspectHandle,
            int currentGroup,
            int occurrenceGroup,
            IDictionary<int, int> originalGroups)
        {
            if (occurrenceGroup == 0 || currentGroup != occurrenceGroup
                || originalGroups == null)
                return currentGroup;

            int originalGroup;
            return originalGroups.TryGetValue(suspectHandle, out originalGroup)
                ? originalGroup : currentGroup;
        }

        internal static bool IsPedInDispatchSuspectOccurrenceGroup(
            int currentGroup,
            int occurrenceGroup)
        {
            return occurrenceGroup != 0 && currentGroup == occurrenceGroup;
        }

        internal static LSPDDispatchEvent FromDefinition(
            LSPDDispatchEventDefinition definition,
            Vector3 origin,
            LSPDCriminalProfileDefinition criminalProfile,
            int suspectCount,
            int victimCount,
            int sceneParticipantCount,
            int suspectVehicleCount,
            Random random)
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
                RequiredPhysicalType = definition.RequiredPhysicalType,
                SceneBehavior = definition.SceneBehavior,
                InteriorLocationId = definition.InteriorLocationId,
                SceneAnimationDictionary = definition.SceneAnimationDictionary,
                SceneAnimationPrimaryClip = definition.SceneAnimationPrimaryClip,
                SceneAnimationSecondaryClip = definition.SceneAnimationSecondaryClip,
                PreparedActivity = definition.PreparedActivity,
                PreArrivalActivity = definition.PreArrivalActivity,
                SuspectResponseBranches = definition.SuspectResponseBranches == null
                    ? new List<string>() : new List<string>(definition.SuspectResponseBranches),
                VictimResponseBranches = definition.VictimResponseBranches == null
                    ? new List<string>() : new List<string>(definition.VictimResponseBranches),
                SuspectResponseWeights = definition.SuspectResponseWeights == null
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(definition.SuspectResponseWeights, StringComparer.OrdinalIgnoreCase),
                VictimResponseWeights = definition.VictimResponseWeights == null
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(definition.VictimResponseWeights, StringComparer.OrdinalIgnoreCase),
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
                VehicleStagingRequired = definition.VehicleStagingRequired,
                VehicleActivity = definition.VehicleActivity,
                Mobile = definition.Mobile,
                StartPursuitOnAccept = definition.StartPursuitOnAccept,
                Armed = definition.Armed || profileArmed,
                SuspectCount = Math.Max(1, Math.Min(15, suspectCount)),
                SuspectVehicleCount = Math.Max(0, Math.Min(2, suspectVehicleCount)),
                VictimPoolId = definition.VictimPoolId,
                MinimumVictims = definition.MinimumVictims,
                MaximumVictims = definition.MaximumVictims,
                VictimCount = Math.Max(
                    definition.MinimumVictims,
                    Math.Min(definition.MaximumVictims, victimCount)),
                DisturbanceActivities = definition.DisturbanceActivities == null
                    ? new List<string>() : new List<string>(definition.DisturbanceActivities),
                DisturbanceVariant = definition.ChooseDisturbanceActivity(random),
                DisturbanceGunWeapon = definition.DisturbanceGunWeapon ?? string.Empty,
                DisturbanceWeaponName = string.Empty,
                DisturbanceActivityStartedAt = DateTime.MinValue,
                DisturbanceObservationStartedAt = DateTime.MinValue,
                DisturbanceResponseEarliestAt = DateTime.MinValue,
                DisturbanceNextTaskAt = DateTime.MinValue,
                DisturbanceAnimationDictionary = string.Empty,
                DisturbanceAnimationClip = string.Empty,
                DisturbanceAnimationStartAttempts = 0,
                DisturbanceAnimationRequested = false,
                DisturbanceAnimationConfirmedPlaying = false,
                DisturbancePresentationReady = false,
                DisturbanceVictimReactionStarted = false,
                DisturbanceVictimReactionFailureLogged = false,
                DisturbanceWeaponPrepared = false,
                DisturbanceWeaponDrawn = false,
                DisturbanceWeaponStageConfirmed = false,
                ActiveShooterActivityStartedAt = DateTime.MinValue,
                ActiveShooterLastShotAt = DateTime.MinValue,
                ActiveShooterNextShotAt = DateTime.MinValue,
                ActiveShooterFireTaskIssuedAt = DateTime.MinValue,
                ActiveShooterFireConfirmationLogged = false,
                ActiveShooterActivityStopped = false,
                ActiveShooterWeaponName = string.Empty,
                ActiveShooterLastShotTarget = Vector3.Zero,
                ActiveShooterDirectionIndex = 0,
                ActiveShooterShotCount = 0,
                ActiveShooterCommandTriggerSuspectHandle = 0,
                DisturbancePropFailureLogged = false,
                DisturbancePropLoadAttempts = 0,
                DisturbancePropNextAttemptAt = DateTime.MinValue,
                DisturbanceProp = null,
                SceneParticipantPoolId = definition.SceneParticipantPoolId,
                SceneParticipantCount = Math.Max(
                    definition.MinimumSceneParticipants,
                    Math.Min(definition.MaximumSceneParticipants, sceneParticipantCount)),
                SceneParticipantModelNames = new List<string>(),
                SceneParticipantAmbientClaimAttemptedIndexes = new HashSet<int>(),
                ExistingAmbientSceneParticipantHandles = new HashSet<int>(),
                SceneParticipantBehaviorInitializedHandles = new HashSet<int>(),
                AdditionalDeadVictims = definition.AdditionalDeadVictims,
                VictimStartsDead = definition.VictimStartsDead,
                VictimInvestigated = false,
                VictimEncounterResponseStarted = false,
                VictimEncounterResponse = string.Empty,
                Severity = definition.Severity,
                Origin = origin,
                // Dispatch owns the live assignment before its physical scene entities exist.
                OwnedByDispatch = true,
                State = LSPDDispatchState.Offered,
                CreatedAt = now,
                StateChangedAt = now,
                ScenePreparationDeadline = DateTime.MinValue,
                ScenePreparationNextRetryAt = DateTime.MinValue,
                ScenePreparationActorRetryCount = 0,
                ScenePreparationVehicleGroundRetryCount = 0,
                ScenePreparationFailureStage = string.Empty,
                ScenePreparationFailureReason = string.Empty,
                SceneAnimationRequested = false,
                PreparedExteriorActorPositions = new Dictionary<int, Vector3>(),
                PreparedExteriorActorGroundPositions = new Dictionary<int, Vector3>(),
                PreparedExteriorActorCorrectionAttempted = new HashSet<int>(),
                PairedSceneFormationCorrectionAttempted = false,
                SceneAnimationDeadline = DateTime.MinValue,
                PreArrivalSceneBehaviorInitialized = false,
                PreArrivalVictimThreatStarted = false,
                NextGroupShootoutShotAt = DateTime.MinValue,
                PlayerEncounterResponseStarted = false,
                LastPursuitWaypointAt = DateTime.MinValue,
                LastPursuitRadioUpdateAt = DateTime.MinValue,
                LastPursuitWaypointPosition = origin,
                MappedInteriorEntryTrackingInitialized = false,
                MappedInteriorPlayerStartedInside = false,
                MappedInteriorOutsideDoorwayObserved = false,
                MappedInteriorDoorwayCrossingObserved = false,
                LastMappedInteriorEntrySampleAt = DateTime.MinValue,
                LastMappedInteriorOutsideDoorwayAt = DateTime.MinValue,
                MappedInteriorDoorwayCrossedAt = DateTime.MinValue,
                LastMappedInteriorEscalationCheckAt = DateTime.MinValue,
                MappedInteriorEntryEvidence = string.Empty,
                PursuitLocationUpdateCount = 0,
                LastEncounterResponseCheckAt = DateTime.MinValue,
                SceneBehaviorStage = 0,
                SceneBehaviorNextStepAt = DateTime.MinValue,
                SurrenderRequestedAt = DateTime.MinValue,
                NextEnRouteStatusAt = DateTime.MinValue,
                EnRouteStatusUpdateCount = 0,
                LastMovementSampleAt = now,
                LastSuspectPosition = origin,
                InteriorExitPhase = 0,
                InteriorExitStartedAt = DateTime.MinValue,
                InteriorExitNextTaskAt = DateTime.MinValue,
                InteriorExitVehicleEntryStartedAt = DateTime.MinValue,
                InteriorVictimExitRoute = null,
                InteriorVictimExitPhase = 0,
                InteriorVictimExitRecoveryCount = 0,
                InteriorVictimExitStartedAt = DateTime.MinValue,
                InteriorVictimExitNextTaskAt = DateTime.MinValue,
                InteriorVictimExitCompleted = false,
                InteriorExitCompletedHandles = new HashSet<int>(),
                InteriorExitFailedHandles = new HashSet<int>(),
                AdditionalParticipants = new List<Ped>(),
                Suspects = new List<Ped>(),
                ArrestedSuspects = new List<Ped>(),
                CompliantSuspects = new List<Ped>()
            };
        }

        internal string ChooseSuspectResponse(Random random, IEnumerable<string> eligibleBranches)
        {
            if (eligibleBranches == null || SuspectResponseWeights == null)
                return string.Empty;
            List<string> candidates = eligibleBranches
                .Where(value => !string.IsNullOrWhiteSpace(value)
                    && SuspectResponseWeights.ContainsKey(value)
                    && GetSuspectResponseWeight(value) > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int total = candidates.Sum(GetSuspectResponseWeight);
            if (total <= 0)
                return string.Empty;
            int roll = random == null ? 0 : random.Next(total);
            foreach (string candidate in candidates)
            {
                roll -= GetSuspectResponseWeight(candidate);
                if (roll < 0)
                    return candidate;
            }
            return candidates[candidates.Count - 1];
        }

        private int GetSuspectResponseWeight(string response)
        {
            int weight;
            if (SuspectResponseWeights == null
                || !SuspectResponseWeights.TryGetValue(response, out weight)
                || weight <= 0)
                return 0;

            if (!string.Equals(Id, "disturbance", StringComparison.OrdinalIgnoreCase))
                return weight;

            // Keep the event-authored base probabilities, while letting the
            // selected criminal profile affect how this subject reacts.
            if (string.Equals(CriminalDisposition, "situational",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(response, "comply", StringComparison.OrdinalIgnoreCase))
                return weight * 2;
            if (string.Equals(CriminalDisposition, "reluctant",
                    StringComparison.OrdinalIgnoreCase)
                && (string.Equals(response, "resist", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(response, "flee", StringComparison.OrdinalIgnoreCase)))
                return weight * 2;
            return weight;
        }

        internal bool TryChooseDisturbanceFallback(
            string rejectedVariant,
            Random random,
            out string selectedVariant)
        {
            selectedVariant = string.Empty;
            if (!string.Equals(Id, "disturbance", StringComparison.OrdinalIgnoreCase)
                || DisturbanceActivities == null)
                return false;
            List<string> candidates = DisturbanceActivities
                .Where(value => !string.IsNullOrWhiteSpace(value)
                    && !string.Equals(value, rejectedVariant,
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 0)
                return false;
            int index = random == null ? 0 : random.Next(candidates.Count);
            selectedVariant = candidates[index];
            DisturbanceVariant = selectedVariant;
            DisturbanceWeaponName = string.Empty;
            DisturbanceWeaponPrepared = false;
            DisturbanceWeaponDrawn = false;
            DisturbanceWeaponStageConfirmed = false;
            return true;
        }

        internal string ChooseVictimResponse(Random random, IEnumerable<string> eligibleBranches)
        {
            if (eligibleBranches == null || VictimResponseWeights == null)
                return string.Empty;
            List<string> candidates = eligibleBranches
                .Where(value => !string.IsNullOrWhiteSpace(value)
                    && VictimResponseWeights.ContainsKey(value)
                    && VictimResponseWeights[value] > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int total = candidates.Sum(value => VictimResponseWeights[value]);
            if (total <= 0)
                return string.Empty;
            int roll = random == null ? 0 : random.Next(total);
            foreach (string candidate in candidates)
            {
                roll -= VictimResponseWeights[candidate];
                if (roll < 0)
                    return candidate;
            }
            return candidates[candidates.Count - 1];
        }

        internal Vehicle VehicleForSuspect(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return null;
            int vehicleHandle;
            if (!SuspectVehicleByPedHandle.TryGetValue(suspect.Handle, out vehicleHandle))
                return null;
            return SuspectVehicles.FirstOrDefault(value => value != null
                && value.Exists() && value.Handle == vehicleHandle);
        }

        internal Ped CurrentPursuitTarget
        {
            get
            {
                IEnumerable<Ped> active = Suspects.Where(ped => ped != null
                    && ped.Exists() && !ped.IsDead && ped.Health > 0
                    && !ArrestedSuspects.Any(arrested => arrested != null
                        && arrested.Exists() && arrested.Handle == ped.Handle)
                    && !PursuitContainedSuspectHandles.Contains(ped.Handle)
                    && !CompliantSuspects.Any(compliant => compliant != null
                        && compliant.Exists() && compliant.Handle == ped.Handle));
                Ped vehicleRunner = active.FirstOrDefault(ped =>
                    string.Equals(SuspectResponses.ContainsKey(ped.Handle)
                            ? SuspectResponses[ped.Handle] : string.Empty,
                        "flee", StringComparison.OrdinalIgnoreCase)
                        && VehicleForSuspect(ped) != null && ped.IsInVehicle());
                if (vehicleRunner != null)
                    return vehicleRunner;
                Ped fleeing = active.FirstOrDefault(ped =>
                    string.Equals(SuspectResponses.ContainsKey(ped.Handle)
                            ? SuspectResponses[ped.Handle] : string.Empty,
                        "flee", StringComparison.OrdinalIgnoreCase));
                if (fleeing != null)
                    return fleeing;
                return null;
            }
        }

        internal IReadOnlyList<Ped> CurrentVehiclePursuitTargets
        {
            get
            {
                var targets = new List<Ped>();
                var seenVehicles = new HashSet<int>();
                if (Suspects == null)
                    return targets;

                foreach (Ped ped in Suspects)
                {
                    if (ped == null || !ped.Exists() || ped.IsDead || ped.Health <= 0
                        || !ped.IsInVehicle()
                        || !string.Equals(SuspectResponses.ContainsKey(ped.Handle)
                                ? SuspectResponses[ped.Handle] : string.Empty,
                            "flee", StringComparison.OrdinalIgnoreCase)
                        || ArrestedSuspects.Any(arrested => arrested != null
                            && arrested.Exists() && arrested.Handle == ped.Handle)
                        || PursuitContainedSuspectHandles.Contains(ped.Handle)
                        || CompliantSuspects.Any(compliant => compliant != null
                            && compliant.Exists() && compliant.Handle == ped.Handle))
                        continue;

                    Vehicle vehicle = VehicleForSuspect(ped);
                    if (vehicle == null || !vehicle.Exists()
                        || !seenVehicles.Add(vehicle.Handle))
                        continue;
                    targets.Add(ped);
                }

                return targets;
            }
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

