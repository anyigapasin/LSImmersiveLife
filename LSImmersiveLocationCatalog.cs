using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using GTA.Math;

namespace LSImmersiveLife
{
    /// <summary>
    /// Loads the shared geographic knowledge catalogue. The loader is data-only:
    /// it does not stream map assets, query GTA natives, create entities, or
    /// promote candidate points to spawn-safe positions.
    /// </summary>
    internal sealed class LSImmersiveLocationCatalog
    {
        private readonly Dictionary<string, LSImmersiveLocationDefinition> _locations;
        private readonly List<LSImmersiveLocationInteriorAccessPair> _interiorAccessPairs;

        private LSImmersiveLocationCatalog(
            Dictionary<string, LSImmersiveLocationDefinition> locations,
            List<LSImmersiveLocationInteriorAccessPair> interiorAccessPairs)
        {
            _locations = locations;
            _interiorAccessPairs = interiorAccessPairs
                ?? new List<LSImmersiveLocationInteriorAccessPair>();
        }

        internal int LocationCount { get { return _locations.Count; } }

        internal IEnumerable<LSImmersiveLocationDefinition> Locations
        {
            get { return _locations.Values; }
        }

        /// <summary>
        /// Owner-interpreted access candidates from a survey relationship that
        /// pairs a zero-interior outside observation with a non-zero interior
        /// observation at the same named street and within the owner's stated
        /// one-to-two-metre threshold gap. The catalog's general proximity
        /// relationships remain review hints; only this narrowly matched shape
        /// is surfaced here, and AmbientWorld still validates it in GTA.
        /// </summary>
        internal IEnumerable<LSImmersiveLocationInteriorAccessPair> InteriorAccessPairs
        {
            get { return _interiorAccessPairs; }
        }

        internal static LSImmersiveLocationCatalog Load(string path)
        {
            return LoadSingleCatalog(path);
        }

        private static LSImmersiveLocationCatalog LoadSingleCatalog(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("LS Immersive location XML path is unavailable.");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "LSImmersiveLocation.xml was not found.", path);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 24 * 1024 * 1024
            };

            XDocument document;
            using (XmlReader reader = XmlReader.Create(path, settings))
            {
                document = XDocument.Load(reader);
            }

            XElement root = document.Root;
            if (root == null
                || root.Name != "LSImmersiveLocationDatabase"
                || !string.Equals((string)root.Attribute("version"), "1.0",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "LSImmersiveLocation.xml root or version is invalid.");
            }

            XElement sourceRoot = root.Element("Sources");
            XElement locationsRoot = root.Element("Locations");
            if (sourceRoot == null || locationsRoot == null)
                throw new InvalidDataException(
                    "LSImmersiveLocation.xml requires Sources and Locations sections.");

            var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement source in sourceRoot.Elements("Source"))
            {
                string sourceId = Required(source, "id", "Location source");
                if (!sourceIds.Add(sourceId))
                    throw new InvalidDataException(
                        "Duplicate location source id: " + sourceId);
            }
            if (sourceIds.Count == 0)
                throw new InvalidDataException("Location source catalogue is empty.");

            var locations = new Dictionary<string, LSImmersiveLocationDefinition>(
                StringComparer.OrdinalIgnoreCase);
            foreach (XElement node in locationsRoot.Elements("Location"))
            {
                LSImmersiveLocationDefinition location =
                    LSImmersiveLocationDefinition.FromXml(node, sourceIds);
                if (locations.ContainsKey(location.Id))
                    throw new InvalidDataException(
                        "Duplicate location id: " + location.Id);
                locations.Add(location.Id, location);
            }

            if (locations.Count == 0)
                throw new InvalidDataException("Location catalogue is empty.");

            List<LSImmersiveLocationInteriorAccessPair> interiorAccessPairs =
                BuildInteriorAccessPairs(
                    root.Element("SurveyRelationshipIndex"), locations);

            // Survey observations are now ordinary Location/Point records in
            // this same file; the raw LSCoordinate survey is never loaded here.
            return new LSImmersiveLocationCatalog(locations, interiorAccessPairs);
        }

        private static List<LSImmersiveLocationInteriorAccessPair> BuildInteriorAccessPairs(
            XElement relationshipsRoot,
            IDictionary<string, LSImmersiveLocationDefinition> locations)
        {
            var result = new List<LSImmersiveLocationInteriorAccessPair>();
            if (relationshipsRoot == null || locations == null)
                return result;

            var capturePoints = new Dictionary<string, LocationPointReference>(
                StringComparer.OrdinalIgnoreCase);
            var ambiguousCaptures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LSImmersiveLocationDefinition location in locations.Values)
            {
                foreach (LSImmersiveLocationPoint point in location.Points)
                {
                    if (point == null || string.IsNullOrWhiteSpace(point.SourceRecord))
                        continue;
                    if (capturePoints.ContainsKey(point.SourceRecord))
                    {
                        ambiguousCaptures.Add(point.SourceRecord);
                        capturePoints.Remove(point.SourceRecord);
                        continue;
                    }
                    if (!ambiguousCaptures.Contains(point.SourceRecord))
                        capturePoints.Add(point.SourceRecord,
                            new LocationPointReference(location, point));
                }
            }

            var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buildingCandidateReferences = new List<BuildingCandidateReference>();
            foreach (LSImmersiveLocationDefinition location in locations.Values)
            {
                if (location == null)
                    continue;
                foreach (LSImmersiveLocationBuildingCandidate candidate
                    in location.BuildingCandidates)
                {
                    if (candidate != null)
                    {
                        buildingCandidateReferences.Add(
                            new BuildingCandidateReference(location, candidate));
                    }
                }
            }

            foreach (XElement relationship in relationshipsRoot.Elements("Relationship"))
            {
                if (!string.Equals(Optional(relationship, "type", string.Empty),
                        "NearbyDifferentInteriorContext", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Optional(relationship, "sameInteriorIdContext", string.Empty),
                        "false", StringComparison.OrdinalIgnoreCase))
                    continue;

                double declaredDistance;
                if (!double.TryParse((string)relationship.Attribute("distanceMeters"),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out declaredDistance)
                    || double.IsNaN(declaredDistance) || double.IsInfinity(declaredDistance)
                    || declaredDistance < 0.05d || declaredDistance > 2.25d)
                    continue;

                string leftCapture = Optional(relationship, "leftCaptureId", string.Empty);
                string rightCapture = Optional(relationship, "rightCaptureId", string.Empty);
                if (string.IsNullOrWhiteSpace(leftCapture)
                    || string.IsNullOrWhiteSpace(rightCapture))
                    continue;

                LocationPointReference left;
                LocationPointReference right;
                if (!capturePoints.TryGetValue(leftCapture, out left)
                    || !capturePoints.TryGetValue(rightCapture, out right))
                    continue;

                LocationPointReference outside = IsOutsideAccessObservation(left)
                    ? left : IsOutsideAccessObservation(right) ? right : null;
                LocationPointReference inside = IsInteriorAccessObservation(left)
                    ? left : IsInteriorAccessObservation(right) ? right : null;
                if (outside == null || inside == null
                    || ReferenceEquals(outside, inside)
                    || !HasMatchingAccessStreet(outside.Location, inside.Location))
                    continue;

                float actualDistance = outside.Point.Position.DistanceTo(inside.Point.Position);
                if (float.IsNaN(actualDistance) || float.IsInfinity(actualDistance)
                    || actualDistance > 2.25f
                    || Math.Abs(actualDistance - declaredDistance) > 0.35d)
                    continue;

                string pairKey = outside.Point.SourceRecord + "|" + inside.Point.SourceRecord;
                if (!seenPairs.Add(pairKey))
                    continue;

                result.Add(new LSImmersiveLocationInteriorAccessPair(
                    outside.Location,
                    outside.Point,
                    inside.Location,
                    inside.Point,
                    actualDistance,
                    "NearbyDifferentInteriorContext"));
            }

            // BuildingCandidateEvidence records the surveyed building anchor. A
            // storefront's exterior capture and interior capture can be adjacent
            // records, so pair every nearby anchor on the same authored street
            // and zone with the interior observation. AmbientWorld still has to
            // resolve the live sides and raycast a registered physical door.
            foreach (LSImmersiveLocationDefinition location in locations.Values)
            {
                if (location == null)
                    continue;

                foreach (LSImmersiveLocationPoint insidePoint
                    in location.FindPoints("InteriorPointCandidate", false))
                {
                    int interiorId;
                    if (insidePoint == null
                        || !int.TryParse(insidePoint.InteriorId,
                            NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out interiorId)
                        || interiorId == 0
                        || string.IsNullOrWhiteSpace(insidePoint.SourceRecord))
                        continue;

                    foreach (BuildingCandidateReference candidateReference
                        in buildingCandidateReferences)
                    {
                        if (candidateReference == null
                            || candidateReference.Candidate == null
                            || !HasMatchingAccessStreet(
                                candidateReference.Location, location))
                            continue;

                        LSImmersiveLocationBuildingCandidate buildingCandidate =
                            candidateReference.Candidate;
                        float separation = buildingCandidate.Position.DistanceTo(
                            insidePoint.Position);
                        if (float.IsNaN(separation) || float.IsInfinity(separation)
                            || separation < 0.05f || separation > 8f)
                            continue;

                        var thresholdCandidate = new LSImmersiveLocationPoint(
                            "building-threshold."
                                + candidateReference.Location.Id + "."
                                + buildingCandidate.SourceCaptureId,
                            "BuildingCandidatePosition",
                            buildingCandidate.Position,
                            false,
                            0f,
                            false,
                            "Candidate",
                            "Unknown",
                            "Unknown",
                            "Unknown",
                            "0",
                            "BuildingCandidateEvidence_RuntimeValidationRequired",
                            "Unknown",
                            "Unknown",
                            candidateReference.Location.SourceRef,
                            buildingCandidate.SourceCaptureId);

                        string pairKey = candidateReference.Location.Id + "|"
                            + thresholdCandidate.Id + "|" + insidePoint.SourceRecord;
                        if (!seenPairs.Add(pairKey))
                            continue;

                        result.Add(new LSImmersiveLocationInteriorAccessPair(
                            candidateReference.Location,
                            thresholdCandidate,
                            location,
                            insidePoint,
                            separation,
                            string.Equals(buildingCandidate.SourceCaptureId,
                                insidePoint.SourceRecord,
                                StringComparison.OrdinalIgnoreCase)
                                ? "BuildingCandidateEvidenceSameCapture"
                                : "NearbyBuildingCandidateSameStreet"));
                    }
                }
            }

            return result;
        }

        private static bool IsOutsideAccessObservation(LocationPointReference reference)
        {
            return reference != null
                && string.Equals(reference.Point.Role, "ObservedCoordinateCandidate",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(reference.Point.InteriorId, "0",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInteriorAccessObservation(LocationPointReference reference)
        {
            int interiorId;
            return reference != null
                && string.Equals(reference.Point.Role, "InteriorPointCandidate",
                    StringComparison.OrdinalIgnoreCase)
                && int.TryParse(reference.Point.InteriorId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out interiorId)
                && interiorId != 0;
        }

        private static bool HasMatchingAccessStreet(
            LSImmersiveLocationDefinition outside,
            LSImmersiveLocationDefinition inside)
        {
            return outside != null && inside != null
                && !string.IsNullOrWhiteSpace(outside.Street)
                && !string.IsNullOrWhiteSpace(outside.Zone)
                && string.Equals(outside.Street, inside.Street,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(outside.Zone, inside.Zone,
                    StringComparison.OrdinalIgnoreCase);
        }

        private sealed class LocationPointReference
        {
            internal LocationPointReference(
                LSImmersiveLocationDefinition location,
                LSImmersiveLocationPoint point)
            {
                Location = location;
                Point = point;
            }

            internal LSImmersiveLocationDefinition Location { get; private set; }
            internal LSImmersiveLocationPoint Point { get; private set; }
        }

        private sealed class BuildingCandidateReference
        {
            internal BuildingCandidateReference(
                LSImmersiveLocationDefinition location,
                LSImmersiveLocationBuildingCandidate candidate)
            {
                Location = location;
                Candidate = candidate;
            }

            internal LSImmersiveLocationDefinition Location { get; private set; }
            internal LSImmersiveLocationBuildingCandidate Candidate { get; private set; }
        }

        internal bool TryGetLocation(
            string locationId,
            out LSImmersiveLocationDefinition location)
        {
            location = null;
            return !string.IsNullOrWhiteSpace(locationId)
                && _locations.TryGetValue(locationId, out location);
        }

        internal bool TryGetPoint(
            string locationId,
            string role,
            bool requireSpawnEligible,
            out LSImmersiveLocationPoint point)
        {
            point = null;
            LSImmersiveLocationDefinition location;
            if (!TryGetLocation(locationId, out location))
                return false;

            return location.TryGetPoint(role, requireSpawnEligible, out point);
        }

        internal IEnumerable<LSImmersiveLocationDefinition> FindByCategory(
            string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return Enumerable.Empty<LSImmersiveLocationDefinition>();
            return _locations.Values.Where(location =>
                string.Equals(location.Category, category,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal IEnumerable<LSImmersiveLocationDefinition> FindByRegion(
            string region)
        {
            if (string.IsNullOrWhiteSpace(region))
                return Enumerable.Empty<LSImmersiveLocationDefinition>();
            return _locations.Values.Where(location =>
                string.Equals(location.Region, region,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal IEnumerable<LSImmersiveLocationDefinition> FindByZone(string zone)
        {
            if (string.IsNullOrWhiteSpace(zone))
                return Enumerable.Empty<LSImmersiveLocationDefinition>();
            return _locations.Values.Where(location =>
                string.Equals(location.Zone, zone,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal IEnumerable<LSImmersiveLocationDefinition> FindByStreet(string street)
        {
            if (string.IsNullOrWhiteSpace(street))
                return Enumerable.Empty<LSImmersiveLocationDefinition>();
            return _locations.Values.Where(location =>
                string.Equals(location.Street, street,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal IEnumerable<LSImmersiveLocationDefinition> FindByMainPlace(string mainPlace)
        {
            if (string.IsNullOrWhiteSpace(mainPlace))
                return Enumerable.Empty<LSImmersiveLocationDefinition>();
            return _locations.Values.Where(location =>
                string.Equals(location.MainPlace, mainPlace,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string Required(
            XElement node,
            string attribute,
            string owner)
        {
            string value = node == null
                ? null : (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(
                    owner + " attribute is missing: " + attribute);
            return value.Trim();
        }

        private static string Optional(
            XElement node,
            string attribute,
            string fallback)
        {
            string value = node == null
                ? null : (string)node.Attribute(attribute);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static bool Boolean(
            XElement node,
            string attribute,
            bool fallback,
            string owner)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            bool parsed;
            if (!bool.TryParse(value, out parsed))
                throw new InvalidDataException(
                    owner + " attribute is not true/false: " + attribute);
            return parsed;
        }

        private static float Number(
            XElement node,
            string attribute,
            float minimum,
            float maximum,
            string owner)
        {
            float value;
            if (!float.TryParse((string)node.Attribute(attribute),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || float.IsNaN(value) || float.IsInfinity(value)
                || value < minimum || value > maximum)
            {
                throw new InvalidDataException(
                    owner + " coordinate is invalid or outside range: " + attribute);
            }
            return value;
        }

        internal sealed class LSImmersiveLocationDefinition
        {
            private readonly Dictionary<string, LSImmersiveLocationPoint> _points;
            private readonly List<LSImmersiveLocationBuildingCandidate> _buildingCandidates;

            private LSImmersiveLocationDefinition(
                string id,
                string name,
                string category,
                string region,
                string district,
                string environment,
                string intendedUse,
                string confidence,
                string validationStatus,
                bool enhancedVerified,
                bool exteriorVerified,
                bool interiorVerified,
                bool safeForPed,
                bool safeForVehicle,
                bool roadSafe,
                bool navigationSafe,
                string sourceRef,
                string notes,
                Dictionary<string, LSImmersiveLocationPoint> points,
                List<LSImmersiveLocationBuildingCandidate> buildingCandidates)
            {
                Id = id;
                Name = name;
                Category = category;
                Region = region;
                District = district;
                Environment = environment;
                IntendedUse = intendedUse;
                Confidence = confidence;
                ValidationStatus = validationStatus;
                EnhancedVerified = enhancedVerified;
                ExteriorVerified = exteriorVerified;
                InteriorVerified = interiorVerified;
                SafeForPed = safeForPed;
                SafeForVehicle = safeForVehicle;
                RoadSafe = roadSafe;
                NavigationSafe = navigationSafe;
                SourceRef = sourceRef;
                Notes = notes;
                _points = points;
                _buildingCandidates = buildingCandidates
                    ?? new List<LSImmersiveLocationBuildingCandidate>();
            }

            internal string Id { get; private set; }
            internal string Name { get; private set; }
            internal string Category { get; private set; }
            internal string Region { get; private set; }
            internal string District { get; private set; }
            internal string MainPlace { get; private set; }
            internal string Zone { get; private set; }
            internal string Street { get; private set; }
            internal string Environment { get; private set; }
            internal string IntendedUse { get; private set; }
            internal string Confidence { get; private set; }
            internal string ValidationStatus { get; private set; }
            internal bool EnhancedVerified { get; private set; }
            internal bool ExteriorVerified { get; private set; }
            internal bool InteriorVerified { get; private set; }
            internal bool SafeForPed { get; private set; }
            internal bool SafeForVehicle { get; private set; }
            internal bool RoadSafe { get; private set; }
            internal bool NavigationSafe { get; private set; }
            internal string PlayerFootAccess { get; private set; }
            internal string PedFootAccess { get; private set; }
            internal string VehicleAccess { get; private set; }
            internal string InteriorAccess { get; private set; }
            internal string ExteriorFootAccess { get; private set; }
            internal string SourceRef { get; private set; }
            internal string Notes { get; private set; }
            internal string PhysicalType { get; private set; }
            internal string Subcategory { get; private set; }
            internal string IdentityStatus { get; private set; }
            internal string ReviewStatus { get; private set; }
            internal string PotentialMisclick { get; private set; }
            internal string NameConfidence { get; private set; }
            internal string SurveySiteId { get; private set; }
            internal IEnumerable<LSImmersiveLocationPoint> Points
            {
                get { return _points.Values; }
            }
            internal IEnumerable<LSImmersiveLocationBuildingCandidate> BuildingCandidates
            {
                get { return _buildingCandidates; }
            }

            internal bool TryGetPoint(
                string role,
                bool requireSpawnEligible,
                out LSImmersiveLocationPoint point)
            {
                point = null;
                if (string.IsNullOrWhiteSpace(role))
                    return false;

                point = _points.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.Role, role,
                        StringComparison.OrdinalIgnoreCase)
                    && (!requireSpawnEligible || candidate.SpawnEligible));
                return point != null;
            }

            internal IEnumerable<LSImmersiveLocationPoint> FindPoints(
                string role,
                bool requireSpawnEligible)
            {
                if (string.IsNullOrWhiteSpace(role))
                    return Enumerable.Empty<LSImmersiveLocationPoint>();
                return _points.Values.Where(candidate =>
                    string.Equals(candidate.Role, role,
                        StringComparison.OrdinalIgnoreCase)
                    && (!requireSpawnEligible || candidate.SpawnEligible));
            }

            internal static LSImmersiveLocationDefinition FromXml(
                XElement node,
                ISet<string> sourceIds)
            {
                if (node == null)
                    throw new ArgumentNullException("node");

                string id = Required(node, "id", "Location");
                string name = Required(node, "name", "Location " + id);
                string sourceRef = Required(node, "sourceRef", "Location " + id);
                if (!sourceIds.Contains(sourceRef))
                    throw new InvalidDataException(
                        "Location references an unknown source: " + id + "/" + sourceRef);

                var points = new Dictionary<string, LSImmersiveLocationPoint>(
                    StringComparer.OrdinalIgnoreCase);
                XElement pointsRoot = node.Element("Points");
                if (pointsRoot == null)
                {
                    // Version 1.0 currently uses Point elements directly under
                    // Location so the XML remains compact and editable.
                    foreach (XElement pointNode in node.Elements("Point"))
                        AddPoint(points, pointNode, sourceIds, id);
                }
                else
                {
                    foreach (XElement pointNode in pointsRoot.Elements("Point"))
                        AddPoint(points, pointNode, sourceIds, id);
                }

                if (points.Count == 0)
                    throw new InvalidDataException(
                        "Location has no coordinate points: " + id);

                var buildingCandidates = new List<LSImmersiveLocationBuildingCandidate>();
                foreach (XElement candidateNode in node.Elements("BuildingCandidateEvidence"))
                {
                    string sourceCaptureId = Optional(
                        candidateNode, "sourceCaptureId", string.Empty);
                    if (string.IsNullOrWhiteSpace(sourceCaptureId))
                        continue;

                    buildingCandidates.Add(new LSImmersiveLocationBuildingCandidate(
                        sourceCaptureId,
                        new Vector3(
                            Number(candidateNode, "x", -10000f, 10000f,
                                "Building candidate in " + id),
                            Number(candidateNode, "y", -10000f, 10000f,
                                "Building candidate in " + id),
                            Number(candidateNode, "z", -1000f, 3000f,
                                "Building candidate in " + id)),
                        Optional(candidateNode, "evidenceStatus", "Unknown"),
                        Optional(candidateNode, "modelName", "Unknown"),
                        Optional(candidateNode, "modelHash", string.Empty),
                        Boolean(candidateNode, "identityConfirmed", false,
                            "Building candidate in " + id)));
                }

                var definition = new LSImmersiveLocationDefinition(
                    id,
                    name,
                    Required(node, "category", "Location " + id),
                    Optional(node, "region", string.Empty),
                    Optional(node, "district", string.Empty),
                    Optional(node, "environment", string.Empty),
                    Optional(node, "intendedUse", string.Empty),
                    Optional(node, "confidence", "Unknown"),
                    Optional(node, "validationStatus", "Unknown"),
                    Boolean(node, "enhancedVerified", false, "Location " + id),
                    Boolean(node, "exteriorVerified", false, "Location " + id),
                    Boolean(node, "interiorVerified", false, "Location " + id),
                    Boolean(node, "safeForPed", false, "Location " + id),
                    Boolean(node, "safeForVehicle", false, "Location " + id),
                    Boolean(node, "roadSafe", false, "Location " + id),
                    Boolean(node, "navigationSafe", false, "Location " + id),
                    sourceRef,
                    node.Element("Notes") == null
                        ? string.Empty : node.Element("Notes").Value.Trim(),
                    points,
                    buildingCandidates);
                definition.MainPlace = Optional(node, "mainPlace", string.Empty);
                definition.Zone = Optional(node, "zone", string.Empty);
                definition.Street = Optional(node, "street", string.Empty);
                definition.PlayerFootAccess = Optional(node, "playerFootAccess", "Unknown");
                definition.PedFootAccess = Optional(node, "pedFootAccess", "Unknown");
                definition.VehicleAccess = Optional(node, "vehicleAccess", "Unknown");
                definition.InteriorAccess = Optional(node, "interiorAccess", "Unknown");
                definition.ExteriorFootAccess = Optional(node, "exteriorFootAccess", "Unknown");
                definition.Subcategory = Optional(node, "subcategory", "Unknown");
                definition.PhysicalType = Optional(node, "physicalType", "Unknown");
                definition.IdentityStatus = Optional(node, "identityStatus", "Unknown");
                definition.ReviewStatus = Optional(node, "reviewStatus", "Unknown");
                definition.PotentialMisclick = Optional(node, "potentialMisclick", "Unknown");
                definition.NameConfidence = Optional(node, "nameConfidence", "Unknown");
                definition.SurveySiteId = Optional(node, "surveySiteId", string.Empty);
                return definition;
            }

            private static void AddPoint(
                IDictionary<string, LSImmersiveLocationPoint> points,
                XElement pointNode,
                ISet<string> sourceIds,
                string locationId)
            {
                string role = Required(pointNode, "role", "Point in " + locationId);
                string sourceRef = Required(pointNode, "sourceRef", "Point in " + locationId);
                if (!sourceIds.Contains(sourceRef))
                    throw new InvalidDataException(
                        "Point references an unknown source: " + locationId + "/" + sourceRef);

                float heading = 0f;
                string headingText = (string)pointNode.Attribute("heading");
                bool hasHeading = !string.IsNullOrWhiteSpace(headingText);
                if (hasHeading)
                    heading = Number(pointNode, "heading", -360f, 360f,
                        "Point in " + locationId);
                XElement ground = pointNode.Element("GroundObservation");

                var point = new LSImmersiveLocationPoint(
                    Required(pointNode, "id", "Point in " + locationId),
                    role,
                    new Vector3(
                        Number(pointNode, "x", -10000f, 10000f,
                            "Point in " + locationId),
                        Number(pointNode, "y", -10000f, 10000f,
                            "Point in " + locationId),
                        Number(pointNode, "z", -1000f, 3000f,
                            "Point in " + locationId)),
                    hasHeading,
                    heading,
                    Boolean(pointNode, "spawnEligible", false,
                        "Point in " + locationId),
                    Optional(pointNode, "validationStatus", "Unknown"),
                    Optional(pointNode, "groundStatus", "Unknown"),
                    Optional(pointNode, "roadStatus", "Unknown"),
                    Optional(pointNode, "interiorStatus", "Unknown"),
                    Optional(pointNode, "interiorId", "Unknown"),
                    Optional(pointNode, "positionContext", "Unknown"),
                    ground == null ? "Unknown" : Optional(ground, "z", "Unknown"),
                    ground == null ? "Unknown" : Optional(ground, "delta", "Unknown"),
                    sourceRef,
                    Optional(pointNode, "sourceRecord", string.Empty));

                if (points.ContainsKey(point.Id))
                    throw new InvalidDataException(
                        "Duplicate point id in location: " + locationId + "/" + point.Id);
                points.Add(point.Id, point);
            }
        }

        internal sealed class LSImmersiveLocationPoint
        {
            internal LSImmersiveLocationPoint(
                string id,
                string role,
                Vector3 position,
                bool hasHeading,
                float heading,
                bool spawnEligible,
                string validationStatus,
                string groundStatus,
                string roadStatus,
                string interiorStatus,
                string interiorId,
                string positionContext,
                string groundZ,
                string groundDelta,
                string sourceRef,
                string sourceRecord)
            {
                Id = id;
                Role = role;
                Position = position;
                HasHeading = hasHeading;
                Heading = heading;
                SpawnEligible = spawnEligible;
                ValidationStatus = validationStatus;
                GroundStatus = groundStatus;
                RoadStatus = roadStatus;
                InteriorStatus = interiorStatus;
                InteriorId = interiorId;
                PositionContext = positionContext;
                GroundZ = groundZ;
                GroundDelta = groundDelta;
                SourceRef = sourceRef;
                SourceRecord = sourceRecord;
            }

            internal string Id { get; private set; }
            internal string Role { get; private set; }
            internal Vector3 Position { get; private set; }
            internal bool HasHeading { get; private set; }
            internal float Heading { get; private set; }
            internal bool SpawnEligible { get; private set; }
            internal string ValidationStatus { get; private set; }
            internal string GroundStatus { get; private set; }
            internal string RoadStatus { get; private set; }
            internal string InteriorStatus { get; private set; }
            internal string InteriorId { get; private set; }
            internal string PositionContext { get; private set; }
            internal string GroundZ { get; private set; }
            internal string GroundDelta { get; private set; }
            internal string SourceRef { get; private set; }
            internal string SourceRecord { get; private set; }
        }

        internal sealed class LSImmersiveLocationBuildingCandidate
        {
            internal LSImmersiveLocationBuildingCandidate(
                string sourceCaptureId,
                Vector3 position,
                string evidenceStatus,
                string modelName,
                string modelHash,
                bool identityConfirmed)
            {
                SourceCaptureId = sourceCaptureId;
                Position = position;
                EvidenceStatus = evidenceStatus;
                ModelName = modelName;
                ModelHash = modelHash;
                IdentityConfirmed = identityConfirmed;
            }

            internal string SourceCaptureId { get; private set; }
            internal Vector3 Position { get; private set; }
            internal string EvidenceStatus { get; private set; }
            internal string ModelName { get; private set; }
            internal string ModelHash { get; private set; }
            internal bool IdentityConfirmed { get; private set; }
        }

        /// <summary>
        /// A narrowly resolved outside/inside coordinate pair. The pair is a
        /// threshold candidate from the Owner's survey convention, not proof
        /// that a physical door exists or is traversable in the current game.
        /// </summary>
        internal sealed class LSImmersiveLocationInteriorAccessPair
        {
            internal LSImmersiveLocationInteriorAccessPair(
                LSImmersiveLocationDefinition outsideLocation,
                LSImmersiveLocationPoint outsidePoint,
                LSImmersiveLocationDefinition insideLocation,
                LSImmersiveLocationPoint insidePoint,
                float distanceMeters,
                string mappingBasis)
            {
                OutsideLocation = outsideLocation;
                OutsidePoint = outsidePoint;
                InsideLocation = insideLocation;
                InsidePoint = insidePoint;
                InteriorId = insidePoint == null ? string.Empty : insidePoint.InteriorId;
                DistanceMeters = distanceMeters;
                MappingBasis = mappingBasis ?? string.Empty;
            }

            internal LSImmersiveLocationDefinition OutsideLocation { get; private set; }
            internal LSImmersiveLocationPoint OutsidePoint { get; private set; }
            internal LSImmersiveLocationDefinition InsideLocation { get; private set; }
            internal LSImmersiveLocationPoint InsidePoint { get; private set; }
            internal string InteriorId { get; private set; }
            internal float DistanceMeters { get; private set; }
            internal string MappingBasis { get; private set; }
        }
    }
}
