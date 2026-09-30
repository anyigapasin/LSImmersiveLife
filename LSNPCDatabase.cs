using GTA;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Reads the authored citizen database once, then creates one stable record
    /// for the ambient ped selected by the NPC interaction owner. This class
    /// owns data selection only; it never assigns GTA tasks or starts Police
    /// activity.
    /// </summary>
    internal sealed class LSNPCDatabase
    {
        private readonly string _path;
        private readonly LSImmersiveLog _log;
        private readonly Random _random = new Random();
        private readonly Dictionary<int, LSPDNPCRecord> _sessionRecords =
            new Dictionary<int, LSPDNPCRecord>();
        private readonly Dictionary<int, XElement> _modelsByHash =
            new Dictionary<int, XElement>();
        private XDocument _document;
        private int _sequence;

        internal LSNPCDatabase(LSIMMERSIVEPATH paths, LSImmersiveLog log)
        {
            if (paths == null)
                throw new ArgumentNullException("paths");
            _path = paths.NpcDatabaseXmlPath;
            _log = log;
        }

        internal bool IsLoaded { get { return _document != null; } }
        internal int ModelCount { get { return _modelsByHash.Count; } }
        internal string LastLoadError { get; private set; }

        internal bool Reload()
        {
            LastLoadError = null;
            _document = null;
            _modelsByHash.Clear();
            _sessionRecords.Clear();
            try
            {
                if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
                    throw new FileNotFoundException("The NPC citizen database was not found.", _path);

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 2 * 1024 * 1024
                };
                using (XmlReader reader = XmlReader.Create(_path, settings))
                    _document = XDocument.Load(reader);

                XElement root = _document.Root;
                if (root == null || root.Name != "LSNPCDatabase")
                    throw new InvalidDataException("Unexpected NPC database root element.");

                XElement catalog = root.Element("PedModelCatalog");
                if (catalog != null)
                {
                    foreach (XElement model in catalog.Elements("Ped"))
                    {
                        int hash;
                        if (TryReadModelHash(model, out hash) && !_modelsByHash.ContainsKey(hash))
                            _modelsByHash.Add(hash, model);
                    }
                }

                if (_modelsByHash.Count == 0)
                    throw new InvalidDataException("NPC database has no readable ped model entries.");
                if (Pool("StatusPools", "Status").Count == 0)
                    throw new InvalidDataException("NPC database has no citizen status entries.");

                LogRuntime("NPC_DATABASE_LOADED",
                    "Models=" + _modelsByHash.Count
                    + "; Statuses=" + Pool("StatusPools", "Status").Count
                    + "; Path=" + _path);
                return true;
            }
            catch (Exception ex)
            {
                LastLoadError = ex.Message;
                _document = null;
                _modelsByHash.Clear();
                if (_log != null)
                    _log.Exception("NPC_DATABASE_LOAD_FAILED", ex);
                return false;
            }
        }

        internal void ResetSession()
        {
            _sessionRecords.Clear();
        }

        internal bool TryGetRecord(Ped ped, bool trafficContact, out LSPDNPCRecord record)
        {
            record = null;
            if (ped == null || !ped.Exists())
                return false;
            if (!IsLoaded && !Reload())
                return false;

            if (_sessionRecords.TryGetValue(ped.Handle, out record)
                && record != null && record.PedModelHash == ped.Model.Hash)
                return true;

            try
            {
                record = GenerateRecord(ped, trafficContact);
                _sessionRecords[ped.Handle] = record;
                TrimSessionRecords();
                LogRuntime("NPC_DATABASE_RECORD_ASSIGNED",
                    "Ped=" + ped.Handle
                    + "; Citizen=" + record.CitizenId
                    + "; Model=" + record.PedModelName
                    + "; Status=" + record.StatusId
                    + "; Warrant=" + record.WarrantId);
                return true;
            }
            catch (Exception ex)
            {
                if (_log != null)
                    _log.Exception("NPC_DATABASE_RECORD_FAILED", ex);
                record = null;
                return false;
            }
        }

        /// <summary>
        /// Assigns an authored adverse record to a suspicious foot contact.
        /// This deliberately uses the same XML status pools as an ordinary
        /// document check; it only narrows the family to the database's
        /// suspicious/high-alert records. No status strings are hard-coded in
        /// the gameplay owner and no second citizen database is created.
        /// </summary>
        internal bool TryGetAdverseRecord(Ped ped, bool trafficContact, out LSPDNPCRecord record)
        {
            record = null;
            if (ped == null || !ped.Exists())
                return false;
            if (!IsLoaded && !Reload())
                return false;

            if (_sessionRecords.TryGetValue(ped.Handle, out record)
                && record != null
                && (string.Equals(record.StatusFamily, "suspicious", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(record.StatusFamily, "high_alert", StringComparison.OrdinalIgnoreCase)))
                return true;

            try
            {
                record = GenerateRecord(ped, trafficContact, true);
                _sessionRecords[ped.Handle] = record;
                TrimSessionRecords();
                LogRuntime("NPC_DATABASE_ADVERSE_RECORD_ASSIGNED",
                    "Ped=" + ped.Handle
                    + "; Citizen=" + record.CitizenId
                    + "; Model=" + record.PedModelName
                    + "; Status=" + record.StatusId
                    + "; Family=" + record.StatusFamily
                    + "; Warrant=" + record.WarrantId);
                return true;
            }
            catch (Exception ex)
            {
                if (_log != null)
                    _log.Exception("NPC_DATABASE_ADVERSE_RECORD_FAILED", ex);
                record = null;
                return false;
            }
        }

        private LSPDNPCRecord GenerateRecord(
            Ped ped,
            bool trafficContact,
            bool forceAdverseStatus = false)
        {
            XElement model;
            _modelsByHash.TryGetValue(ped.Model.Hash, out model);
            string gender = Attribute(model, "gender", _random.Next(2) == 0 ? "male" : "female");
            string modelName = Attribute(model, "model", "model_" + ped.Model.Hash.ToString(CultureInfo.InvariantCulture));

            XElement firstName = WeightedElement(IdentityNames(gender));
            XElement lastName = WeightedElement(IdentityPool("LastNames"));
            XElement region = WeightedElement(Pool("LifeContextPools", "Regions", null));
            XElement occupation = WeightedElement(Pool("LifeContextPools", "Occupations", null));
            XElement behavior = WeightedElement(Pool("FactPools", "Behaviors", null));
            XElement contactReason = trafficContact
                ? FindById(Pool("FactPools", "ContactReasons", null), "traffic_stop")
                : WeightedElement(Pool("FactPools", "ContactReasons", null));

            string family = WeightedFamily();
            List<XElement> statusPool = Pool("StatusPools", "Status");
            XElement status = null;
            if (forceAdverseStatus)
            {
                List<XElement> adverseStatuses = statusPool.Where(item =>
                    string.Equals(Attribute(item, "family", string.Empty), "suspicious",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Attribute(item, "family", string.Empty), "high_alert",
                        StringComparison.OrdinalIgnoreCase)).ToList();
                XElement adverseStatus = WeightedElement(adverseStatuses);
                if (adverseStatus != null)
                {
                    status = adverseStatus;
                    family = Attribute(status, "family", "suspicious");
                }
                else
                    status = null;
            }
            else
                status = null;

            if (status == null)
                status = WeightedElement(statusPool
                    .Where(item => string.Equals(Attribute(item, "family", string.Empty), family,
                        StringComparison.OrdinalIgnoreCase)));
            if (status == null)
                status = WeightedElement(statusPool);
            family = Attribute(status, "family", "other");

            XElement document = SelectDocument(status, trafficContact);
            XElement warrant = SelectWarrant(status, family);
            XElement disposition = SelectDisposition(family, warrant);

            _sequence++;
            string citizenId = "LSC-" + DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture)
                + "-" + RegionCode(region)
                + "-" + _sequence.ToString("000000", CultureInfo.InvariantCulture);
            bool activeWarrant = warrant != null
                && string.Equals(Attribute(warrant, "active", "false"), "true",
                    StringComparison.OrdinalIgnoreCase);
            string severity = Attribute(status, "severity", "routine");

            return new LSPDNPCRecord
            {
                CitizenId = citizenId,
                PedModelHash = ped.Model.Hash,
                PedModelName = modelName,
                FullName = Attribute(firstName, "value", "Unknown") + " "
                    + Attribute(lastName, "value", "Citizen"),
                Occupation = Attribute(occupation, "name", Humanize(Attribute(occupation, "id", "unavailable"))),
                Region = Attribute(region, "name", Humanize(Attribute(region, "id", "los_santos"))),
                DocumentName = Attribute(document, "name", "Identification"),
                DocumentStatus = Attribute(document, "status", "unknown"),
                StatusId = Attribute(status, "id", "record_unavailable"),
                StatusFamily = family,
                StatusDescription = ChildText(status, "Description", "text",
                    "Database status requires officer review."),
                RecommendedResponse = ChildText(status, "Response", "value",
                    "Use officer judgment and complete the contact safely."),
                WarrantId = Attribute(warrant, "id", "none"),
                WarrantLabel = Attribute(warrant, "label", "None"),
                WarrantActive = activeWarrant,
                Behavior = Attribute(behavior, "text", Humanize(Attribute(behavior, "id", "calm"))),
                Disposition = Attribute(disposition, "text", Humanize(Attribute(disposition, "id", "officer_review"))),
                ContactReason = Attribute(contactReason, "text", trafficContact ? "traffic stop" : "consensual Police contact"),
                RequiresCustody = activeWarrant || string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase)
            };
        }

        private IEnumerable<XElement> IdentityNames(string gender)
        {
            XElement root = _document.Root == null ? null : _document.Root.Element("IdentityPools");
            if (root == null)
                return Enumerable.Empty<XElement>();
            XElement pool = root.Elements("FirstNames").FirstOrDefault(item =>
                string.Equals(Attribute(item, "gender", string.Empty), gender,
                    StringComparison.OrdinalIgnoreCase));
            if (pool == null)
                pool = root.Elements("FirstNames").FirstOrDefault();
            return pool == null ? Enumerable.Empty<XElement>() : pool.Elements("Name");
        }

        private IEnumerable<XElement> IdentityPool(string name)
        {
            XElement root = _document.Root == null ? null : _document.Root.Element("IdentityPools");
            XElement pool = root == null ? null : root.Element(name);
            return pool == null ? Enumerable.Empty<XElement>() : pool.Elements();
        }

        private List<XElement> Pool(string section, string element)
        {
            XElement root = _document == null ? null : _document.Root;
            XElement pool = root == null ? null : root.Element(section);
            return pool == null ? new List<XElement>() : pool.Elements(element).ToList();
        }

        private List<XElement> Pool(string section, string poolName, string element)
        {
            XElement root = _document == null ? null : _document.Root;
            XElement sectionNode = root == null ? null : root.Element(section);
            XElement pool = sectionNode == null ? null : sectionNode.Element(poolName);
            if (pool == null)
                return new List<XElement>();
            return string.IsNullOrWhiteSpace(element)
                ? pool.Elements().ToList()
                : pool.Elements(element).ToList();
        }

        private string WeightedFamily()
        {
            XElement profile = _document.Root == null ? null : _document.Root.Element("GeneratorProfile");
            XElement weights = profile == null ? null : profile.Element("StatusFamilyWeights");
            var values = new List<KeyValuePair<string, int>>
            {
                new KeyValuePair<string, int>("clean", IntAttribute(weights, "clean", 58)),
                new KeyValuePair<string, int>("suspicious", IntAttribute(weights, "suspicious", 28)),
                new KeyValuePair<string, int>("high_alert", IntAttribute(weights, "high_alert", 9)),
                new KeyValuePair<string, int>("other", IntAttribute(weights, "other", 5))
            };
            int total = Math.Max(1, values.Sum(item => Math.Max(0, item.Value)));
            int roll = _random.Next(total);
            foreach (KeyValuePair<string, int> item in values)
            {
                roll -= Math.Max(0, item.Value);
                if (roll < 0)
                    return item.Key;
            }
            return "clean";
        }

        private XElement SelectDocument(XElement status, bool trafficContact)
        {
            List<XElement> documents = Pool("LifeContextPools", "Documents", null);
            string statusId = Attribute(status, "id", string.Empty);
            if (statusId == "expired_document")
                return documents.FirstOrDefault(item => Attribute(item, "status", string.Empty) == "expired")
                    ?? WeightedElement(documents);
            if (statusId == "inconsistent_id")
                return documents.FirstOrDefault(item => Attribute(item, "status", string.Empty) == "questioned")
                    ?? WeightedElement(documents);
            if (trafficContact)
            {
                List<XElement> driverDocuments = documents.Where(item =>
                    Attribute(item, "id", string.Empty).StartsWith("drivers_license", StringComparison.OrdinalIgnoreCase)
                    || Attribute(item, "id", string.Empty) == "commercial_license").ToList();
                if (driverDocuments.Count > 0)
                    return WeightedElement(driverDocuments);
            }
            return WeightedElement(documents);
        }

        private XElement SelectWarrant(XElement status, string family)
        {
            List<XElement> warrants = Pool("WarrantPools", "Warrant");
            XElement none = FindById(warrants, "none");
            string statusId = Attribute(status, "id", string.Empty);
            int chance = string.Equals(family, "high_alert", StringComparison.OrdinalIgnoreCase) ? 55
                : string.Equals(family, "suspicious", StringComparison.OrdinalIgnoreCase) ? 8
                : string.Equals(family, "other", StringComparison.OrdinalIgnoreCase) ? 5 : 1;
            if (statusId == "active_warrant")
                chance = 100;
            if (_random.Next(100) >= chance)
                return none;
            XElement active = WeightedElement(warrants.Where(item =>
                string.Equals(Attribute(item, "active", "false"), "true",
                    StringComparison.OrdinalIgnoreCase)));
            return active ?? none;
        }

        private XElement SelectDisposition(string family, XElement warrant)
        {
            List<XElement> values = Pool("FactPools", "DispositionOutcomes", null);
            bool activeWarrant = warrant != null
                && string.Equals(Attribute(warrant, "active", "false"), "true",
                    StringComparison.OrdinalIgnoreCase);
            if (activeWarrant)
                return FindById(values, "detained") ?? WeightedElement(values);
            List<XElement> compatible = values.Where(item =>
                Attribute(item, "family", string.Empty).IndexOf(family,
                    StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return WeightedElement(compatible.Count == 0 ? values : compatible);
        }

        private XElement WeightedElement(IEnumerable<XElement> source)
        {
            List<XElement> values = source == null
                ? new List<XElement>() : source.Where(item => item != null).ToList();
            if (values.Count == 0)
                return null;
            int total = values.Sum(item => Math.Max(1, IntAttribute(item, "weight", 1)));
            int roll = _random.Next(Math.Max(1, total));
            foreach (XElement item in values)
            {
                roll -= Math.Max(1, IntAttribute(item, "weight", 1));
                if (roll < 0)
                    return item;
            }
            return values[values.Count - 1];
        }

        private void TrimSessionRecords()
        {
            if (_sessionRecords.Count <= 96)
                return;
            foreach (int handle in _sessionRecords.Keys.Take(_sessionRecords.Count - 64).ToArray())
                _sessionRecords.Remove(handle);
        }

        private static bool TryReadModelHash(XElement model, out int hash)
        {
            hash = 0;
            string signed = Attribute(model, "hashSigned", string.Empty);
            if (int.TryParse(signed, NumberStyles.Integer, CultureInfo.InvariantCulture, out hash))
                return true;
            string unsigned = Attribute(model, "hashUnsigned", string.Empty);
            uint value;
            if (!uint.TryParse(unsigned, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return false;
            hash = unchecked((int)value);
            return true;
        }

        private static XElement FindById(IEnumerable<XElement> values, string id)
        {
            return values == null ? null : values.FirstOrDefault(item =>
                string.Equals(Attribute(item, "id", string.Empty), id,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string ChildText(XElement parent, string child, string attribute, string fallback)
        {
            XElement value = parent == null ? null : parent.Element(child);
            return Attribute(value, attribute, fallback);
        }

        private static string Attribute(XElement element, string name, string fallback)
        {
            XAttribute value = element == null ? null : element.Attribute(name);
            return value == null || string.IsNullOrWhiteSpace(value.Value) ? fallback : value.Value.Trim();
        }

        private static int IntAttribute(XElement element, string name, int fallback)
        {
            int value;
            return int.TryParse(Attribute(element, name, string.Empty), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static string Humanize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unavailable";
            string text = value.Replace('_', ' ').Trim();
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
        }

        private static string RegionCode(XElement region)
        {
            string id = Attribute(region, "id", "LS").Replace("_", string.Empty).ToUpperInvariant();
            return id.Length <= 3 ? id : id.Substring(0, 3);
        }

        private void LogRuntime(string category, string message)
        {
            if (_log != null)
                _log.Runtime(category, message);
        }
    }

    /// <summary>One stable, session-scoped citizen check returned to Police UI.</summary>
    internal sealed class LSPDNPCRecord
    {
        internal string CitizenId { get; set; }
        internal int PedModelHash { get; set; }
        internal string PedModelName { get; set; }
        internal string FullName { get; set; }
        internal string Occupation { get; set; }
        internal string Region { get; set; }
        internal string DocumentName { get; set; }
        internal string DocumentStatus { get; set; }
        internal string StatusId { get; set; }
        internal string StatusFamily { get; set; }
        internal string StatusDescription { get; set; }
        internal string RecommendedResponse { get; set; }
        internal string WarrantId { get; set; }
        internal string WarrantLabel { get; set; }
        internal bool WarrantActive { get; set; }
        internal string Behavior { get; set; }
        internal string Disposition { get; set; }
        internal string ContactReason { get; set; }
        internal bool RequiresCustody { get; set; }

        internal string ScreenText
        {
            get
            {
                return "~b~LSPD CITIZEN CHECK~s~\n"
                    + FullName + " | " + CitizenId + "\n"
                    + DocumentName + ": " + DocumentStatus + "\n"
                    + "Status: " + Humanize(StatusId) + "\n"
                    + "Warrant: " + (WarrantActive ? WarrantLabel : "None");
            }
        }

        internal string DetailText
        {
            get
            {
                return FullName + " | " + CitizenId
                    + " | Document=" + DocumentName + " (" + DocumentStatus + ")"
                    + " | Status=" + Humanize(StatusId)
                    + " | Warrant=" + (WarrantActive ? WarrantLabel : "None")
                    + " | Occupation=" + Occupation
                    + " | Area=" + Region;
            }
        }

        private static string Humanize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unavailable";
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
        }
    }
}
