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
    /// Read-only bridge to an existing Gang & Turf export.
    ///
    /// Police Authority may use this information to avoid treating a known
    /// gang member as an ordinary civilian and to display territorial context.
    /// It never writes gang files, changes relationships, spawns gang actors,
    /// or decides whether a gang incident should become Dispatch.
    /// </summary>
    internal sealed class LSPDGangDataIntegration
    {
        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSImmersiveLog _log;
        private readonly List<LSPDGangDefinition> _gangs = new List<LSPDGangDefinition>();
        private readonly List<LSPDTurfZoneDefinition> _turfZones = new List<LSPDTurfZoneDefinition>();
        private readonly HashSet<int> _memberModelHashes = new HashSet<int>();
        private readonly HashSet<int> _vehicleModelHashes = new HashSet<int>();

        internal LSPDGangDataIntegration(LSIMMERSIVEPATH paths, LSImmersiveLog log)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _log = log;
        }

        internal bool GangDataLoaded { get; private set; }
        internal bool MemberPoolLoaded { get; private set; }
        internal bool TurfDataLoaded { get; private set; }
        internal string LastLoadError { get; private set; }
        internal IEnumerable<LSPDGangDefinition> Gangs { get { return _gangs; } }
        internal IEnumerable<LSPDTurfZoneDefinition> TurfZones { get { return _turfZones; } }
        internal int MemberModelCount { get { return _memberModelHashes.Count; } }
        internal int VehicleModelCount { get { return _vehicleModelHashes.Count; } }

        internal bool Reload()
        {
            var gangs = new List<LSPDGangDefinition>();
            var zones = new List<LSPDTurfZoneDefinition>();
            var members = new HashSet<int>();
            var vehicles = new HashSet<int>();
            bool gangLoaded = false;
            bool memberLoaded = false;
            bool turfLoaded = false;
            bool memberPoolHasEntries = false;
            try
            {
                // Prefer the external Gang & Turf export used by the installed
                // mod, then retain the project Plugin fallback for portable
                // development copies. All three files are read-only inputs.
                string gangPath = FindOptionalPath(_paths.GangDataXmlPath,
                    _paths.ExternalGangDataXmlPath, "GangData.xml");
                if (gangPath != null)
                {
                    XDocument document = Load(gangPath);
                    XElement gangsNode = document.Root == null
                        ? null : document.Root.Element("gangs");
                    if (gangsNode == null)
                        throw new InvalidDataException("GangData.xml has no gangs section.");
                    foreach (XElement node in gangsNode.Elements("Gang"))
                    {
                        LSPDGangDefinition gang = new LSPDGangDefinition
                        {
                            Name = Required(node, "name"),
                            IsPlayerOwned = Bool(node, "isPlayerOwned"),
                            CreatedByPlayer = Bool(node, "hasBeenCreatedByPlayer")
                        };
                        XElement memberVariations = node.Element("memberVariations");
                        if (memberVariations != null)
                            foreach (XElement value in memberVariations.Descendants("modelHash"))
                            {
                                int hash;
                                if (TryHash(value.Value, out hash))
                                {
                                    gang.MemberModelHashes.Add(hash);
                                    members.Add(hash);
                                }
                            }
                        XElement carVariations = node.Element("carVariations");
                        if (carVariations != null)
                            foreach (XElement value in carVariations.Descendants("modelHash"))
                            {
                                int hash;
                                if (TryHash(value.Value, out hash))
                                {
                                    gang.VehicleModelHashes.Add(hash);
                                    vehicles.Add(hash);
                                }
                            }
                        gangs.Add(gang);
                    }
                    gangLoaded = gangs.Count > 0;
                }

                string memberPath = FindOptionalPath(_paths.GangMemberPoolXmlPath,
                    _paths.ExternalGangMemberPoolXmlPath, "MemberPool.xml");
                if (memberPath != null)
                {
                    XDocument document = Load(memberPath);
                    XElement list = document.Root == null
                        ? null : document.Root.Element("memberList");
                    if (list == null)
                        throw new InvalidDataException("MemberPool.xml has no memberList section.");
                    foreach (XElement node in list.Elements("PotentialGangMember"))
                    {
                        int hash;
                        XElement modelHash = node.Element("modelHash");
                        if (modelHash != null && TryHash(modelHash.Value, out hash))
                        {
                            members.Add(hash);
                            memberPoolHasEntries = true;
                        }
                    }
                    memberLoaded = memberPoolHasEntries;
                }

                string turfPath = FindOptionalPath(_paths.GangTurfZoneXmlPath,
                    _paths.ExternalGangTurfZoneXmlPath, "TurfZoneData.xml");
                if (turfPath != null)
                {
                    XDocument document = Load(turfPath);
                    XElement list = document.Root == null
                        ? null : document.Root.Element("zoneList");
                    if (list == null)
                        throw new InvalidDataException("TurfZoneData.xml has no zoneList section.");
                    foreach (XElement node in list.Elements("TurfZone"))
                    {
                        XElement point = node.Element("zoneBlipPosition");
                        float x;
                        float y;
                        float z;
                        if (point == null
                            || !Float(point, "X", out x)
                            || !Float(point, "Y", out y)
                            || !Float(point, "Z", out z))
                            continue;
                        float radius = 60f;
                        float authoredRadius;
                        if (Float(node, "areaRadius", out authoredRadius)
                            && authoredRadius > 0f)
                            radius = authoredRadius;
                        zones.Add(new LSPDTurfZoneDefinition
                        {
                            Name = Required(node, "zoneName"),
                            OwnerGangName = Required(node, "ownerGangName"),
                            Center = new Vector3(x, y, z),
                            Radius = Math.Max(10f, radius)
                        });
                    }
                    turfLoaded = zones.Count > 0;
                }
            }
            catch (Exception ex)
            {
                LastLoadError = ex.Message;
                if (_log != null)
                    _log.Exception("POLICE_GANG_DATA_LOAD_FAILED", ex);
                return false;
            }

            _gangs.Clear();
            _gangs.AddRange(gangs);
            _turfZones.Clear();
            _turfZones.AddRange(zones);
            _memberModelHashes.Clear();
            foreach (int hash in members)
                _memberModelHashes.Add(hash);
            _vehicleModelHashes.Clear();
            foreach (int hash in vehicles)
                _vehicleModelHashes.Add(hash);
            GangDataLoaded = gangLoaded;
            MemberPoolLoaded = memberLoaded;
            TurfDataLoaded = turfLoaded;
            LastLoadError = string.Empty;
            if (_log != null)
                _log.Runtime(
                    "POLICE_GANG_DATA_RELOADED",
                    "Gangs=" + _gangs.Count + "; Members=" + _memberModelHashes.Count
                    + "; Vehicles=" + _vehicleModelHashes.Count
                    + "; TurfZones=" + _turfZones.Count + "; ReadOnly=true");
            return gangLoaded || memberLoaded || turfLoaded;
        }
        internal bool IsKnownGangMember(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return false;
            try { return IsKnownGangModel(ped.Model.Hash); }
            catch { return false; }
        }

        internal bool IsKnownGangModel(int modelHash)
        {
            return FindGangForModel(modelHash) != null
                || _memberModelHashes.Contains(modelHash);
        }

        internal bool IsMemberPoolModel(int modelHash)
        {
            return _memberModelHashes.Contains(modelHash)
                && FindGangForModel(modelHash) == null;
        }

        internal LSPDGangDefinition FindGangForModel(int modelHash)
        {
            return _gangs.FirstOrDefault(gang =>
                gang.MemberModelHashes.Contains(modelHash));
        }

        internal LSPDGangDefinition FindGangForVehicle(int modelHash)
        {
            return _gangs.FirstOrDefault(gang =>
                gang.VehicleModelHashes.Contains(modelHash));
        }

        internal string FindGangNameForVehicle(int modelHash)
        {
            LSPDGangDefinition gang = FindGangForVehicle(modelHash);
            return gang == null ? string.Empty : gang.Name;
        }

        internal LSPDGangDefinition PlayerGang
        {
            get
            {
                return _gangs.FirstOrDefault(gang =>
                    gang.IsPlayerOwned || gang.CreatedByPlayer);
            }
        }

        internal string GetTerritoryOwner(Vector3 position)
        {
            LSPDTurfZoneDefinition zone = FindNearbyTurf(position);
            return zone == null ? string.Empty : zone.OwnerGangName;
        }

        internal LSPDTurfZoneDefinition GetNearestTurf(
            Vector3 position,
            float maxDistance = 250f)
        {
            LSPDTurfZoneDefinition closest = null;
            float distance = float.MaxValue;
            foreach (LSPDTurfZoneDefinition zone in _turfZones)
            {
                float current = zone.Center.DistanceTo(position);
                if (current <= maxDistance && current < distance)
                {
                    closest = zone;
                    distance = current;
                }
            }
            return closest;
        }

        internal string DescribePedGangContext(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return "No valid pedestrian.";
            int hash;
            try { hash = ped.Model.Hash; }
            catch { return "Pedestrian model unavailable."; }
            LSPDGangDefinition gang = FindGangForModel(hash);
            if (gang != null)
                return gang.Name + (gang.IsPlayerOwned || gang.CreatedByPlayer
                    ? " (player-owned / allied)" : " (external Gang & Turf)");
            if (IsMemberPoolModel(hash))
                return "Gang & Turf member pool (gang not assigned)";
            return "No recognized external gang identity";
        }

        internal LSPDTurfZoneDefinition FindNearbyTurf(Vector3 position)
        {
            LSPDTurfZoneDefinition closest = null;
            float distance = float.MaxValue;
            foreach (LSPDTurfZoneDefinition zone in _turfZones)
            {
                float current = zone.Center.DistanceTo(position);
                if (current <= zone.Radius && current < distance)
                {
                    closest = zone;
                    distance = current;
                }
            }
            return closest;
        }

        internal void Reset()
        {
            _gangs.Clear();
            _turfZones.Clear();
            _memberModelHashes.Clear();
            _vehicleModelHashes.Clear();
            GangDataLoaded = false;
            MemberPoolLoaded = false;
            TurfDataLoaded = false;
            LastLoadError = string.Empty;
        }

        private static XDocument Load(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (var reader = System.Xml.XmlReader.Create(stream,
                new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                    IgnoreComments = false,
                    IgnoreWhitespace = true
                }))
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }

        private string FindOptionalPath(
            string primary,
            string external,
            string fileName)
        {
            foreach (string candidate in new[]
            {
                // The installed Gang & Turf export is authoritative when it is
                // present. A local Plugin copy remains a portable fallback.
                external,
                primary,
                Path.Combine(_paths.PluginDirectory, "GangData", "gangModData", fileName),
                Path.Combine(_paths.PluginDirectory, fileName)
            })
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static string Required(XElement node, string name)
        {
            if (node == null)
                throw new InvalidDataException("Gang data section is missing: " + name);
            XElement child = node.Element(name);
            if (child == null || string.IsNullOrWhiteSpace(child.Value))
                throw new InvalidDataException("Gang data value is missing: " + name);
            return child.Value.Trim();
        }

        private static bool Bool(XElement node, string name)
        {
            bool value;
            return bool.TryParse(
                node.Element(name) == null ? null : node.Element(name).Value,
                out value) && value;
        }

        private static bool TryHash(string raw, out int hash)
        {
            hash = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            string value = raw.Trim();
            uint unsigned;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(value.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out unsigned))
            {
                hash = unchecked((int)unsigned);
                return hash != 0;
            }
            if (int.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out hash))
                return hash != 0;
            if (uint.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out unsigned))
            {
                hash = unchecked((int)unsigned);
                return hash != 0;
            }
            return false;
        }

        private static bool Float(XElement node, string name, out float value)        {
            value = 0f;
            return float.TryParse(
                node.Element(name) == null ? null : node.Element(name).Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }
    }

    internal sealed class LSPDGangDefinition
    {
        internal string Name { get; set; }
        internal bool IsPlayerOwned { get; set; }
        internal bool CreatedByPlayer { get; set; }
        internal HashSet<int> MemberModelHashes { get; private set; } = new HashSet<int>();
        internal HashSet<int> VehicleModelHashes { get; private set; } = new HashSet<int>();
    }

    internal sealed class LSPDTurfZoneDefinition
    {
        internal string Name { get; set; }
        internal string OwnerGangName { get; set; }
        internal Vector3 Center { get; set; }
        internal float Radius { get; set; }
    }
}
