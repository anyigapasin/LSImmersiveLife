using GTA;
using GTA.Math;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Selects an authored Crime Activity Event from the validated
    /// LSPDCrimeActivityEvent.xml catalog. It owns event/data selection only:
    /// it never creates GTA entities, starts Dispatch, plays audio, or asks
    /// for Backup. LSPDCrimeActivity owns the live scene that consumes the
    /// selected definition.
    /// </summary>
    internal sealed class LSPDCrimeActivityEvent
    {
        private readonly Random _random = new Random();
        private string _lastSelectedActivityId = string.Empty;
        private string _lastSelectedLocationId = string.Empty;

        /// <summary>
        /// Reads and validates the authored LSPDCrimeActivityEvent.xml catalog.
        /// This is deliberately event/data work: it builds no GTA entities and
        /// changes no Police activity state. The live scene owner receives the
        /// resulting immutable-by-convention dictionaries after a successful
        /// reload and keeps the prior catalog when an XML edit is invalid.
        /// </summary>
        internal bool TryLoadCatalog(
            LSIMMERSIVEPATH paths,
            out LSPDCrimeActivityCatalog catalog,
            out string error)
        {
            catalog = null;
            error = string.Empty;
            try
            {
                if (paths == null || string.IsNullOrWhiteSpace(paths.CrimeActivityEventXmlPath))
                    throw new InvalidDataException("Crime Activity XML path is unavailable.");
                if (!File.Exists(paths.CrimeActivityEventXmlPath))
                    throw new FileNotFoundException(
                        "Crime intelligence XML was not found.",
                        paths.CrimeActivityEventXmlPath);

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 4 * 1024 * 1024
                };

                XDocument document;
                using (XmlReader reader = XmlReader.Create(
                    paths.CrimeActivityEventXmlPath,
                    settings))
                {
                    document = XDocument.Load(reader);
                }

                XElement root = document.Root;
                if (root == null
                    || root.Name != "LSImmersiveLifeCrimeActivityDatabase"
                    || (string)root.Attribute("version") != "1")
                {
                    throw new InvalidDataException(
                        "Crime intelligence XML root or version is invalid.");
                }

                Dictionary<string, LSPDCrimeActivityLocationDefinition> locations =
                    LSPDCrimeActivity.ParseLocations(root.Element("Locations"));
                Dictionary<string, LSPDCrimeActivityGroupDefinition> groups =
                    LSPDCrimeActivity.ParseGroups(root.Element("CriminalGroups"));
                Dictionary<string, LSPDGroupCrimeActivityDefinition> activities =
                    LSPDCrimeActivity.ParseActivities(root.Element("Activities"));
                LSPDCrimeActivity.ValidateReferences(activities, locations, groups);

                catalog = new LSPDCrimeActivityCatalog
                {
                    Locations = locations,
                    Groups = groups,
                    Activities = activities
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Selects one geographic, off-cooldown activity that is actually
        /// relevant to the officer's current patrol position. A data record is
        /// returned; nothing has been spawned or activated at this point.
        /// </summary>
        internal bool TrySelectNearby(
            LSPDCrimeActivity catalog,
            Ped player,
            LSPDGangDataIntegration gangData,
            LSPoliceCrimeActivitySettings settings,
            IDictionary<string, DateTime> locationCooldowns,
            DateTime now,
            out LSPDActivityIntel intel)
        {
            intel = null;
            if (catalog == null || !catalog.IsLoaded
                || player == null || !player.Exists()
                || settings == null)
                return false;

            var candidates = new List<LSPDActivityIntel>();
            foreach (LSPDCrimeActivityLocationDefinition location in catalog.Locations)
            {
                DateTime cooldownUntil;
                if (locationCooldowns != null
                    && locationCooldowns.TryGetValue(location.Id, out cooldownUntil)
                    && now < cooldownUntil)
                    continue;

                Vector3 position;
                if (!catalog.TryResolveLocation(location, out position))
                    continue;

                float distance = player.Position.DistanceTo(position);
                if (distance > settings.MaximumDeploymentDistance)
                    continue;

                foreach (LSPDGroupCrimeActivityDefinition definition in catalog.FindByLocation(location.Id))
                {
                    if (definition == null || definition.AutoDispatch || !definition.IntelOnly)
                        continue;

                    candidates.Add(CreateIntel(
                        definition,
                        location,
                        position,
                        distance,
                        gangData,
                        now));
                }
            }

            if (candidates.Count == 0)
                return false;

            // Do not repeat the last exact activity/location pair where the
            // XML gives another nearby option. This protects variation without
            // hiding a valid sole local activity from the player.
            List<LSPDActivityIntel> varied = candidates.Where(value =>
                !string.Equals(value.Id, _lastSelectedActivityId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(value.Location.Id, _lastSelectedLocationId,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (varied.Count > 0)
                candidates = varied;

            // Prefer local intelligence without deterministically selecting
            // the first XML row every patrol. Choose from the three closest
            // viable scenes (or fewer when the player has fewer options).
            List<LSPDActivityIntel> nearest = candidates
                .OrderBy(value => value.DistanceMeters)
                .Take(Math.Min(3, candidates.Count))
                .ToList();
            intel = nearest[_random.Next(nearest.Count)];
            _lastSelectedActivityId = intel.Id;
            _lastSelectedLocationId = intel.Location.Id;
            return true;
        }

        /// <summary>
        /// Resolves an officer-requested activity from the same XML catalog.
        /// An authored point already near the officer remains preferred. When
        /// none is in range, this method selects the nearest viable authored
        /// context and leaves the gameplay owner to place its scene safely near
        /// the officer; this selector still creates no GTA entities.
        /// </summary>
        internal bool TrySelectRequested(
            LSPDCrimeActivity catalog,
            Ped player,
            LSPDGangDataIntegration gangData,
            LSPoliceCrimeActivitySettings settings,
            IDictionary<string, DateTime> locationCooldowns,
            DateTime now,
            out LSPDActivityIntel intel)
        {
            if (TrySelectNearby(catalog, player, gangData, settings,
                locationCooldowns, now, out intel))
                return true;

            intel = null;
            if (catalog == null || !catalog.IsLoaded
                || player == null || !player.Exists()
                || settings == null)
                return false;

            var candidates = new List<LSPDActivityIntel>();
            foreach (LSPDCrimeActivityLocationDefinition location in catalog.Locations)
            {
                DateTime cooldownUntil;
                if (locationCooldowns != null
                    && locationCooldowns.TryGetValue(location.Id, out cooldownUntil)
                    && now < cooldownUntil)
                    continue;

                Vector3 position;
                if (!catalog.TryResolveLocation(location, out position))
                    continue;
                float distance = player.Position.DistanceTo(position);
                foreach (LSPDGroupCrimeActivityDefinition definition
                    in catalog.FindByLocation(location.Id))
                {
                    if (definition == null || definition.AutoDispatch
                        || !definition.IntelOnly)
                        continue;
                    candidates.Add(CreateIntel(definition, location, position,
                        distance, gangData, now));
                }
            }

            if (candidates.Count == 0)
                return false;

            List<LSPDActivityIntel> varied = candidates.Where(value =>
                !string.Equals(value.Id, _lastSelectedActivityId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(value.Location.Id, _lastSelectedLocationId,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (varied.Count > 0)
                candidates = varied;

            // Keep a requested activity geographically credible by varying
            // among the closest authored contexts rather than selecting an
            // unrelated point from the opposite side of the map.
            List<LSPDActivityIntel> nearest = candidates
                .OrderBy(value => value.DistanceMeters)
                .Take(Math.Min(6, candidates.Count))
                .ToList();
            intel = nearest[_random.Next(nearest.Count)];
            _lastSelectedActivityId = intel.Id;
            _lastSelectedLocationId = intel.Location.Id;
            return true;
        }

        internal bool TryResolveSceneResources(
            LSPDCrimeActivity catalog,
            LSPDActivityIntel intel,
            out LSPDCrimeSceneResources resources)
        {
            return TryResolveSceneResources(
                catalog, intel, null, null, out resources);
        }

        internal bool TryResolveSceneResources(
            LSPDCrimeActivity catalog,
            LSPDActivityIntel intel,
            LSPDCriminalAssetCatalog criminalAssets,
            Random random,
            out LSPDCrimeSceneResources resources)
        {
            resources = null;
            if (catalog == null || intel == null || intel.Definition == null)
                return false;

            LSPDGroupCrimeActivityDefinition definition = intel.Definition;
            // LSPDCriminalProfile.xml is the single criminal asset source.
            // Crime Activity XML still owns activity/location metadata, but
            // its legacy group resource sets must never become a second
            // offender database when the shared catalog is unavailable.
            if (criminalAssets == null)
                return false;
            return criminalAssets.TryBuildCrimeActivityResources(
                definition.Id,
                definition.Category,
                random,
                out resources);
        }

        internal void ResetRecentSelection()
        {
            _lastSelectedActivityId = string.Empty;
            _lastSelectedLocationId = string.Empty;
        }

        private static LSPDActivityIntel CreateIntel(
            LSPDGroupCrimeActivityDefinition definition,
            LSPDCrimeActivityLocationDefinition location,
            Vector3 position,
            float distance,
            LSPDGangDataIntegration gangData,
            DateTime observedAt)
        {
            LSPDTurfZoneDefinition turf = gangData == null
                ? null : gangData.FindNearbyTurf(position);
            string territory = turf == null ? string.Empty
                : turf.Name + " / " + turf.OwnerGangName;
            return new LSPDActivityIntel
            {
                Id = definition.Id,
                Definition = definition,
                Location = location,
                Position = position,
                DistanceMeters = distance,
                ObservedAt = observedAt,
                Territory = territory,
                Summary = definition.IntelSummary
            };
        }
    }

    /// <summary>Resolved XML resources for one live Crime Activity scene.</summary>
    internal sealed class LSPDCrimeSceneResources
    {
        internal string PedPoolId { get; set; }
        internal string VehiclePoolId { get; set; }
        internal string WeaponPoolId { get; set; }
        internal IReadOnlyList<LSPDCrimeResourceDefinition> PedModels { get; set; }
        internal IReadOnlyList<LSPDCrimeResourceDefinition> Weapons { get; set; }
        internal IReadOnlyList<LSPDCrimeResourceDefinition> VehicleModels { get; set; }
    }

    /// <summary>
    /// One validated snapshot of the authored Crime Activity database. It is
    /// carried from the Event/data owner to the live Activity owner without
    /// giving the Event selector any world-entity responsibilities.
    /// </summary>
    internal sealed class LSPDCrimeActivityCatalog
    {
        internal Dictionary<string, LSPDCrimeActivityLocationDefinition> Locations { get; set; }
        internal Dictionary<string, LSPDCrimeActivityGroupDefinition> Groups { get; set; }
        internal Dictionary<string, LSPDGroupCrimeActivityDefinition> Activities { get; set; }
    }
}
