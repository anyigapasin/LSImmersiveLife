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
    /// Owns the XML-backed Police identity, catalog, and personal favourites.
    /// Loading this class is passive. It never changes the GTA player or world.
    /// </summary>
    internal sealed class LSPDProfile
    {
        private const string PersonalPedId = "personal-ped";
        private const string PersonalVehicleId = "personal-vehicle";
        private const string PersonalBackupPedId = "personal-backup-ped";
        private const string DefaultWeaponSetId = "personal-police-loadout";

        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSImmersiveLog _log;
        private readonly List<LSPDPoliceAgencyDefinition> _agencies =
            new List<LSPDPoliceAgencyDefinition>();
        private readonly List<LSPDPoliceStationDefinition> _stations =
            new List<LSPDPoliceStationDefinition>();
        private readonly List<LSPDPoliceLocationDefinition> _locations =
            new List<LSPDPoliceLocationDefinition>();
        private readonly List<LSPDPoliceCoordinationDefinition> _coordination =
            new List<LSPDPoliceCoordinationDefinition>();
        private readonly List<LSPDPoliceModelDefinition> _pedModels =
            new List<LSPDPoliceModelDefinition>();
        private readonly List<LSPDPoliceVehicleDefinition> _vehicles =
            new List<LSPDPoliceVehicleDefinition>();
        private readonly List<LSPDPoliceWeaponDefinition> _weapons =
            new List<LSPDPoliceWeaponDefinition>();
        private readonly List<LSPDPoliceFavoriteModel> _favoritePeds =
            new List<LSPDPoliceFavoriteModel>();
        private readonly List<LSPDPoliceFavoriteModel> _favoriteVehicles =
            new List<LSPDPoliceFavoriteModel>();
        // Backup favourites belong to the player profile, not the universal
        // Police-model library.  They allow Anyi to save a preferred support
        // officer without changing the available global Police models.
        private readonly List<LSPDPoliceFavoriteModel> _favoriteBackupPeds =
            new List<LSPDPoliceFavoriteModel>();
        private readonly List<LSPDPoliceFavoriteWeapon> _personalWeapons =
            new List<LSPDPoliceFavoriteWeapon>();

        private LSPDPoliceProfileSelection _selection =
            new LSPDPoliceProfileSelection();

        internal LSPDProfile(LSIMMERSIVEPATH paths, LSImmersiveLog log = null)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _log = log;

            ProfileXmlPath = paths.PoliceProfileXmlPath;
            UtilityXmlPath = paths.PoliceUtilityXmlPath;
            WeaponDataXmlPath = paths.PoliceWeaponDataXmlPath;
            ModelPedXmlPath = paths.PoliceModelPedXmlPath;
            VehicleXmlPath = paths.PoliceVehicleXmlPath;
            PersonalWeaponXmlPath = paths.PolicePersonalWeaponXmlPath;
            // Catalog/profile reads are data-only. Police Authority remains off
            // until the explicit role boundary calls Activate().
            ReloadCatalogs();
            LastProfileLoaded = LoadProfile();
        }

        internal string ProfileXmlPath { get; private set; }
        internal string UtilityXmlPath { get; private set; }
        internal string WeaponDataXmlPath { get; private set; }
        internal string ModelPedXmlPath { get; private set; }
        internal string VehicleXmlPath { get; private set; }
        internal string PersonalWeaponXmlPath { get; private set; }
        internal bool IsActive { get; private set; }
        internal bool CatalogsLoaded { get; private set; }
        internal bool LastProfileLoaded { get; private set; }
        internal IEnumerable<LSPDPoliceAgencyDefinition> Agencies { get { return _agencies; } }
        internal IEnumerable<LSPDPoliceStationDefinition> Stations { get { return _stations; } }
        internal IEnumerable<LSPDPoliceLocationDefinition> Locations { get { return _locations; } }
        internal IEnumerable<LSPDPoliceCoordinationDefinition> Coordination { get { return _coordination; } }
        internal IEnumerable<LSPDPoliceModelDefinition> PedModels { get { return _pedModels; } }
        internal IEnumerable<LSPDPoliceVehicleDefinition> Vehicles { get { return _vehicles; } }
        internal IEnumerable<LSPDPoliceWeaponDefinition> Weapons { get { return _weapons; } }
        internal IEnumerable<LSPDPoliceFavoriteModel> FavoritePeds { get { return _favoritePeds; } }
        internal IEnumerable<LSPDPoliceFavoriteModel> FavoriteVehicles { get { return _favoriteVehicles; } }
        internal IEnumerable<LSPDPoliceFavoriteModel> FavoriteBackupPeds { get { return _favoriteBackupPeds; } }
        internal IEnumerable<LSPDPoliceFavoriteWeapon> PersonalWeapons { get { return _personalWeapons; } }
        internal LSPDPoliceProfileSelection Selection { get { return _selection; } }

        internal bool Activate()
        {
            bool catalogsLoaded = ReloadCatalogs();
            LastProfileLoaded = LoadProfile();
            IsActive = true;
            return catalogsLoaded;
        }

        internal void Deactivate()
        {
            IsActive = false;
        }

        internal bool ReloadAll()
        {
            bool catalogsLoaded = ReloadCatalogs();
            LastProfileLoaded = LoadProfile();
            return catalogsLoaded;
        }

        internal bool ReloadCatalogs()
        {
            var agencies = new List<LSPDPoliceAgencyDefinition>();
            var stations = new List<LSPDPoliceStationDefinition>();
            var locations = new List<LSPDPoliceLocationDefinition>();
            var coordination = new List<LSPDPoliceCoordinationDefinition>();
            var pedModels = new List<LSPDPoliceModelDefinition>();
            var vehicles = new List<LSPDPoliceVehicleDefinition>();
            var weapons = new List<LSPDPoliceWeaponDefinition>();

            try
            {
                XDocument utility = LoadDocument(UtilityXmlPath);
                if (utility == null)
                    throw new FileNotFoundException("Police utility XML was not found.", UtilityXmlPath);
                ParseUtility(utility.Root, agencies, stations, locations, coordination);

                XDocument peds = LoadDocument(ModelPedXmlPath);
                if (peds == null)
                    throw new FileNotFoundException("Police ped XML was not found.", ModelPedXmlPath);
                ParsePeds(peds.Root, pedModels);

                XDocument vehicleData = LoadDocument(VehicleXmlPath);
                if (vehicleData == null)
                    throw new FileNotFoundException("Police vehicle XML was not found.", VehicleXmlPath);
                ParseVehicles(vehicleData.Root, vehicles);

                XDocument weaponData = LoadDocument(WeaponDataXmlPath);
                if (weaponData == null)
                    throw new FileNotFoundException("Police weapon XML was not found.", WeaponDataXmlPath);
                ParseWeapons(weaponData.Root, weapons);

                ValidateUniqueIds(agencies.Select(value => value.Id), "agency");
                ValidateUniqueIds(stations.Select(value => value.Id), "station");
                ValidateUniqueIds(locations.Select(value => value.Id), "location");
                ValidateUniqueIds(coordination.Select(value => value.Id), "coordination route");
                ValidateUniqueIds(pedModels.Select(value => value.Id), "ped");
                ValidateUniqueIds(vehicles.Select(value => value.Id), "vehicle");
                ValidateUniqueIds(weapons.Select(value => value.Id), "weapon");
            }
            catch (Exception ex)
            {
                CatalogsLoaded = false;
                LogException("POLICE_CATALOG_LOAD_FAILED", ex);
                return false;
            }

            _agencies.Clear();
            _agencies.AddRange(agencies);
            _stations.Clear();
            _stations.AddRange(stations);
            _locations.Clear();
            _locations.AddRange(locations);
            _coordination.Clear();
            _coordination.AddRange(coordination);
            _pedModels.Clear();
            _pedModels.AddRange(pedModels);
            _vehicles.Clear();
            _vehicles.AddRange(vehicles);
            _weapons.Clear();
            _weapons.AddRange(weapons);

            CatalogsLoaded = _agencies.Count > 0
                && _stations.Count > 0
                && _pedModels.Count > 0
                && _vehicles.Count > 0
                && _weapons.Count > 0;
            return CatalogsLoaded;
        }

        internal bool TrySelectAgency(string id)
        {
            LSPDPoliceAgencyDefinition value = FindAgency(id);
            if (value == null)
                return false;
            _selection.AgencyId = value.Id;
            return true;
        }

        internal bool TrySelectStation(string id)
        {
            LSPDPoliceStationDefinition value = FindStation(id);
            if (value == null)
                return false;
            _selection.StationId = value.Id;
            return true;
        }

        internal bool TrySelectPed(string idOrModel)
        {
            LSPDPoliceModelDefinition value = FindPed(idOrModel);
            if (value == null)
                return false;
            _selection.PedId = value.Id;
            return true;
        }

        internal bool TrySelectVehicle(string idOrModel)
        {
            LSPDPoliceVehicleDefinition value = FindVehicle(idOrModel);
            if (value == null)
                return false;
            _selection.VehicleId = value.Id;
            return true;
        }

        internal bool TrySelectWeapon(string idOrWeapon)
        {
            LSPDPoliceWeaponDefinition value = FindWeapon(idOrWeapon);
            if (value == null)
                return false;
            _selection.WeaponId = value.Id;
            return true;
        }

        internal bool SetFavouritePed(string idOrModel)
        {
            string normalized = NormalizeIdentifier(idOrModel);
            if (string.IsNullOrEmpty(normalized) || IsLegacyPromptValue(normalized))
                return false;

            LSPDPoliceModelDefinition library = FindPed(normalized);
            string favoriteId = FavoriteModelId(_favoritePeds, PersonalPedId,
                library == null ? normalized : library.ModelName);
            LSPDPoliceFavoriteModel favorite = library == null
                ? LSPDPoliceFavoriteModel.Custom(
                    favoriteId,
                    FriendlyModelName(normalized),
                    normalized,
                    "Personal Police Officer",
                    "Player Addon")
                : LSPDPoliceFavoriteModel.FromPed(library, favoriteId);

            UpsertSingleModel(_favoritePeds, favorite);
            _selection.FavoritePedId = favorite.Id;
            _selection.PedId = favorite.Id;
            return true;
        }

        internal bool SetFavouriteVehicle(string idOrModel)
        {
            string normalized = NormalizeIdentifier(idOrModel);
            if (string.IsNullOrEmpty(normalized) || IsLegacyPromptValue(normalized))
                return false;

            LSPDPoliceVehicleDefinition library = FindVehicle(normalized);
            string favoriteId = FavoriteModelId(_favoriteVehicles, PersonalVehicleId,
                library == null ? normalized : library.ModelName);
            LSPDPoliceFavoriteModel favorite = library == null
                ? LSPDPoliceFavoriteModel.Custom(
                    favoriteId,
                    FriendlyModelName(normalized),
                    normalized,
                    "Personal Police Vehicle",
                    "Player Addon")
                : LSPDPoliceFavoriteModel.FromVehicle(library, favoriteId);

            UpsertSingleModel(_favoriteVehicles, favorite);
            _selection.FavoriteVehicleId = favorite.Id;
            _selection.VehicleId = favorite.Id;
            return true;
        }

        internal bool SetFavouriteBackupPed(string idOrModel)
        {
            string normalized = NormalizeIdentifier(idOrModel);
            if (string.IsNullOrEmpty(normalized) || IsLegacyPromptValue(normalized))
                return false;

            LSPDPoliceModelDefinition library = FindPed(normalized);
            string favoriteId = FavoriteModelId(_favoriteBackupPeds,
                PersonalBackupPedId,
                library == null ? normalized : library.ModelName);
            LSPDPoliceFavoriteModel favorite = library == null
                ? LSPDPoliceFavoriteModel.Custom(
                    favoriteId,
                    FriendlyModelName(normalized),
                    normalized,
                    "Personal Backup Officer",
                    "Player Addon")
                : LSPDPoliceFavoriteModel.FromPed(library, favoriteId);

            UpsertSingleModel(_favoriteBackupPeds, favorite);
            _selection.BackupPedId = favorite.Id;
            return true;
        }

        internal bool SelectFavouriteBackupPed(string idOrModel)
        {
            LSPDPoliceFavoriteModel favorite = FindFavoriteModel(_favoriteBackupPeds, idOrModel);
            if (favorite == null)
                return false;
            _selection.BackupPedId = favorite.Id;
            return true;
        }

        internal bool AddPersonalWeapon(string idOrWeapon)
        {
            // Menu additions come from the authored weapon library. Legitimate
            // hand-authored entries in the personal XML are accepted on reload.
            LSPDPoliceWeaponDefinition library = FindWeapon(idOrWeapon);
            if (library == null)
                return false;

            LSPDPoliceFavoriteWeapon favorite = LSPDPoliceFavoriteWeapon.FromWeapon(library);
            LSPDPoliceFavoriteWeapon existing = FindPersonalWeapon(favorite.Id)
                ?? FindPersonalWeapon(favorite.WeaponName);
            if (existing == null)
                _personalWeapons.Add(favorite);

            _selection.WeaponId = existing == null ? favorite.Id : existing.Id;
            if (string.IsNullOrWhiteSpace(_selection.WeaponSetId))
                _selection.WeaponSetId = DefaultWeaponSetId;
            return true;
        }

        internal bool SelectPersonalWeapon(string idOrWeapon)
        {
            LSPDPoliceFavoriteWeapon value = FindPersonalWeapon(idOrWeapon);
            if (value == null)
                return false;
            _selection.WeaponId = value.Id;
            if (string.IsNullOrWhiteSpace(_selection.WeaponSetId))
                _selection.WeaponSetId = DefaultWeaponSetId;
            return true;
        }

        internal bool RemovePersonalWeapon(string idOrWeapon)
        {
            LSPDPoliceFavoriteWeapon value = FindPersonalWeapon(idOrWeapon);
            if (value == null)
                return false;

            _personalWeapons.Remove(value);
            if (Matches(_selection.WeaponId, value.Id)
                || Matches(_selection.WeaponId, value.WeaponName))
            {
                _selection.WeaponId = _personalWeapons.Count > 0
                    ? _personalWeapons[0].Id
                    : (_weapons.Count > 0 ? _weapons[0].Id : string.Empty);
            }
            return true;
        }

        internal bool ReloadPersonalWeapons()
        {
            try
            {
                XDocument document = LoadDocument(PersonalWeaponXmlPath);
                if (document == null || document.Root == null)
                    return false;

                XElement set = FindSelectedWeaponSet(document.Root);
                if (set == null)
                    throw new InvalidDataException("Personal weapon XML has no WeaponSet element.");

                var loaded = new List<LSPDPoliceFavoriteWeapon>();
                ParsePersonalWeapons(set, loaded);
                _personalWeapons.Clear();
                _personalWeapons.AddRange(loaded);
                ReadWeaponSetSelection(set);
                EnsureSelection();
                return true;
            }
            catch (Exception ex)
            {
                LogException("PERSONAL_WEAPON_RELOAD_FAILED", ex);
                return false;
            }
        }

        internal bool Save()
        {
            try
            {
                // Merge only owned values. Authored comments, future sections,
                // unknown attributes and other weapon sets survive a profile save.
                XDocument profile = MergeDocument(ProfileXmlPath, BuildProfileXml());
                // Authority behavior moved to the one universal
                // LSImmersiveMainUI.xml configuration. Remove a legacy copied
                // settings node if an older saved profile still contains one.
                XElement legacyAuthoritySettings = profile.Root == null
                    ? null
                    : profile.Root.Element("AuthoritySettings");
                if (legacyAuthoritySettings != null)
                    legacyAuthoritySettings.Remove();
                XDocument personalWeapons = MergeDocument(PersonalWeaponXmlPath, BuildPersonalWeaponsXml());
                SaveAtomic(PersonalWeaponXmlPath, personalWeapons);
                SaveAtomic(ProfileXmlPath, profile);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_PROFILE_SAVE_FAILED", ex);
                return false;
            }
        }

        internal LSPDPoliceAgencyDefinition FindAgency(string id)
        {
            return _agencies.FirstOrDefault(value =>
                Matches(value.Id, id) || Matches(value.CallSign, id));
        }

        internal bool SaveCapturedLoadout(IEnumerable<LSPDPoliceFavoriteWeapon> weapons, string activeId)
        {
            var captured = weapons == null
                ? new List<LSPDPoliceFavoriteWeapon>()
                : weapons.Where(value => value != null).ToList();
            if (captured.Count == 0)
                return false;
            ValidateUniqueIds(captured.Select(value => value.Id), "personal weapon");
            var previous = _personalWeapons.ToList();
            string previousActive = _selection.WeaponId;
            _personalWeapons.Clear();
            _personalWeapons.AddRange(captured);
            _selection.WeaponId = activeId ?? captured[0].Id;
            if (Save())
                return true;
            _personalWeapons.Clear();
            _personalWeapons.AddRange(previous);
            _selection.WeaponId = previousActive;
            return false;
        }

        private XElement FindSelectedWeaponSet(XElement root)
        {
            if (root == null)
                return null;
            if (root.Name == "WeaponSet")
                return root;
            return root.Elements("WeaponSet").FirstOrDefault(set =>
                Matches((string)set.Attribute("id"), _selection.WeaponSetId))
                ?? root.Element("WeaponSet");
        }

        private static XDocument MergeDocument(string path, XElement authored)
        {
            XDocument document = LoadDocument(path);
            if (document == null)
                return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), authored);
            if (document.Root == null)
                throw new InvalidDataException("Cannot save an XML document without a root.");
            if (document.Root.Name == "WeaponSet" && authored.Name == "LSPDImmersivePersonalWeapons")
                MergeOwnedXml(document.Root, authored.Element("WeaponSet"));
            else if (document.Root.Name != authored.Name)
                throw new InvalidDataException("XML root does not match; existing file was not replaced: " + path);
            else
                MergeOwnedXml(document.Root, authored);
            return document;
        }

        private static void MergeOwnedXml(XElement target, XElement values)
        {
            foreach (XAttribute attribute in values.Attributes())
                target.SetAttributeValue(attribute.Name, attribute.Value);
            // These three collections are authoritative only for their known
            // entries. Never remove comments or unrelated extension elements.
            XName entryName = target.Name == "PedFavorites" ? "Ped"
                : target.Name == "BackupPedFavorites" ? "Ped"
                : target.Name == "VehicleFavorites" ? "Vehicle"
                : target.Name == "WeaponSet" ? "WeaponRef"
                : target.Name == "Components" ? "Component" : null;
            if (entryName != null)
            {
                string key = target.Name == "Components" ? "name" : "id";
                foreach (XElement old in target.Elements(entryName).ToList())
                    if (!values.Elements(entryName).Any(value =>
                        Matches((string)value.Attribute(key), (string)old.Attribute(key))))
                        old.Remove();
            }
            foreach (XElement child in values.Elements())
            {
                XAttribute id = child.Attribute("id") ?? child.Attribute("name");
                XElement existing = target.Elements(child.Name).FirstOrDefault(value =>
                    id == null || Matches((string)value.Attribute(id.Name), id.Value));
                if (existing == null)
                    target.Add(new XElement(child));
                else
                    MergeOwnedXml(existing, child);
            }
        }

        internal LSPDPoliceStationDefinition FindStation(string id)
        {
            return _stations.FirstOrDefault(value =>
                Matches(value.Id, id) || Matches(value.DisplayName, id));
        }

        internal LSPDPoliceModelDefinition FindPed(string idOrModel)
        {
            return _pedModels.FirstOrDefault(value =>
                Matches(value.Id, idOrModel) || Matches(value.ModelName, idOrModel));
        }

        internal LSPDPoliceVehicleDefinition FindVehicle(string idOrModel)
        {
            return _vehicles.FirstOrDefault(value =>
                Matches(value.Id, idOrModel) || Matches(value.ModelName, idOrModel));
        }

        internal LSPDPoliceWeaponDefinition FindWeapon(string idOrWeapon)
        {
            return _weapons.FirstOrDefault(value =>
                Matches(value.Id, idOrWeapon) || Matches(value.WeaponName, idOrWeapon));
        }

        internal LSPDPoliceFavoriteWeapon FindPersonalWeapon(string idOrWeapon)
        {
            return _personalWeapons.FirstOrDefault(value =>
                Matches(value.Id, idOrWeapon) || Matches(value.WeaponName, idOrWeapon));
        }

        internal LSPDPoliceModelDefinition SelectedPed { get { return FindPed(_selection.PedId); } }
        internal LSPDPoliceVehicleDefinition SelectedVehicle { get { return FindVehicle(_selection.VehicleId); } }
        internal LSPDPoliceWeaponDefinition SelectedWeapon { get { return FindWeapon(_selection.WeaponId); } }
        internal LSPDPoliceFavoriteModel SelectedFavoritePed
        {
            get { return FindFavoriteModel(_favoritePeds, _selection.PedId); }
        }
        internal LSPDPoliceFavoriteModel SelectedFavoriteVehicle
        {
            get { return FindFavoriteModel(_favoriteVehicles, _selection.VehicleId); }
        }
        internal LSPDPoliceFavoriteModel ActiveFavoritePed
        {
            get { return FindFavoriteModel(_favoritePeds, _selection.FavoritePedId); }
        }
        internal LSPDPoliceFavoriteModel ActiveFavoriteVehicle
        {
            get { return FindFavoriteModel(_favoriteVehicles, _selection.FavoriteVehicleId); }
        }
        internal LSPDPoliceFavoriteModel ActiveFavoriteBackupPed
        {
            get { return FindFavoriteModel(_favoriteBackupPeds, _selection.BackupPedId); }
        }
        internal LSPDPoliceFavoriteWeapon SelectedPersonalWeapon
        {
            get { return FindPersonalWeapon(_selection.WeaponId); }
        }

        /// <summary>
        /// The Backup owner uses the selected saved backup officer as a
        /// personal preference only when its central setting enables it.  It
        /// does not replace the Police model catalog or the player's selected
        /// officer.
        /// </summary>
        internal string PreferredBackupPedModelName
        {
            get
            {
                LSPDPoliceFavoriteModel favorite = ActiveFavoriteBackupPed
                    ?? _favoriteBackupPeds.FirstOrDefault();
                return favorite == null ? string.Empty : favorite.ModelName;
            }
        }

        internal string SelectedPedModelName
        {
            get
            {
                LSPDPoliceModelDefinition library = SelectedPed;
                if (library != null)
                    return library.ModelName;
                LSPDPoliceFavoriteModel favorite = SelectedFavoritePed;
                return favorite == null ? string.Empty : favorite.ModelName;
            }
        }

        internal string SelectedPedDisplayName
        {
            get
            {
                LSPDPoliceModelDefinition library = SelectedPed;
                if (library != null)
                    return library.DisplayName;
                LSPDPoliceFavoriteModel favorite = SelectedFavoritePed;
                return favorite == null ? "Not Selected" : favorite.DisplayName;
            }
        }

        internal string SelectedVehicleModelName
        {
            get
            {
                LSPDPoliceVehicleDefinition library = SelectedVehicle;
                if (library != null)
                    return library.ModelName;
                LSPDPoliceFavoriteModel favorite = SelectedFavoriteVehicle;
                return favorite == null ? string.Empty : favorite.ModelName;
            }
        }

        internal string SelectedVehicleDisplayName
        {
            get
            {
                LSPDPoliceVehicleDefinition library = SelectedVehicle;
                if (library != null)
                    return library.DisplayName;
                LSPDPoliceFavoriteModel favorite = SelectedFavoriteVehicle;
                return favorite == null ? "Not Selected" : favorite.DisplayName;
            }
        }

        internal string SelectedWeaponName
        {
            get
            {
                LSPDPoliceFavoriteWeapon personal = SelectedPersonalWeapon;
                if (personal != null)
                    return personal.WeaponName;
                LSPDPoliceWeaponDefinition library = SelectedWeapon;
                return library == null ? string.Empty : library.WeaponName;
            }
        }

        internal string SelectedWeaponDisplayName
        {
            get
            {
                LSPDPoliceFavoriteWeapon personal = SelectedPersonalWeapon;
                if (personal != null)
                    return personal.DisplayName;
                LSPDPoliceWeaponDefinition library = SelectedWeapon;
                return library == null ? "Not Selected" : library.DisplayName;
            }
        }

        internal int SelectedWeaponAmmo
        {
            get
            {
                LSPDPoliceFavoriteWeapon personal = SelectedPersonalWeapon;
                if (personal != null)
                    return personal.Ammo;
                LSPDPoliceWeaponDefinition library = SelectedWeapon;
                return library == null ? 0 : library.DefaultAmmo;
            }
        }

        private bool LoadProfile()
        {
            _selection = NewDefaultSelection();
            _favoritePeds.Clear();
            _favoriteVehicles.Clear();
            _favoriteBackupPeds.Clear();
            _personalWeapons.Clear();
            bool loadedSavedProfile = false;

            try
            {
                XDocument document = LoadDocument(ProfileXmlPath);
                XElement root = document == null ? null : document.Root;
                if (root != null)
                {
                    if (root.Name != "LSPDImmersiveProfile")
                        throw new InvalidDataException("Police profile XML root is invalid.");

                    loadedSavedProfile = true;
                    XElement selected = root.Element("Profile") ?? root.Element("Selected");
                    if (selected != null)
                    {
                        _selection.AgencyId = FirstAttribute(selected, "agencyId", "agency");
                        _selection.StationId = FirstAttribute(selected, "stationId", "station");
                        _selection.PedId = FirstAttribute(selected, "pedId", "characterModel");
                        _selection.VehicleId = FirstAttribute(selected, "vehicleId", "vehicleModel");
                        _selection.WeaponId = FirstAttribute(selected, "weaponId", "weaponData");
                        _selection.WeaponSetId = AttributeOrDefault(selected, "weaponSetId", DefaultWeaponSetId);
                        _selection.FavoritePedId = FirstAttribute(selected, "favoritePedId", "favouritePedId");
                        _selection.FavoriteVehicleId = FirstAttribute(selected, "favoriteVehicleId", "favouriteVehicleId");
                        _selection.BackupPedId = FirstAttribute(selected, "backupPedId", "backupFavouritePedId");
                    }

                    XElement favorites = root.Element("Favorites") ?? root.Element("FavouriteSet");
                    if (favorites != null)
                    {
                        ParseFavoriteModels(favorites, "PedFavorites", _favoritePeds, false);
                        ParseFavoriteModels(favorites, "VehicleFavorites", _favoriteVehicles, true);
                        ParseFavoriteModels(favorites, "BackupPedFavorites", _favoriteBackupPeds, false);

                        // Accept the old embedded format, then prefer the
                        // dedicated personal weapon XML when it is present.
                        XElement oldWeaponSet = favorites.Element("WeaponSet");
                        if (oldWeaponSet != null)
                        {
                            ParsePersonalWeapons(oldWeaponSet, _personalWeapons);
                            ReadWeaponSetSelection(oldWeaponSet);
                        }

                        XElement reference = favorites.Element("PersonalWeaponCollection");
                        if (reference != null)
                        {
                            _selection.WeaponSetId = AttributeOrDefault(reference, "setId", _selection.WeaponSetId);
                            string active = AttributeOrDefault(reference, "activeWeaponId", string.Empty);
                            if (!string.IsNullOrWhiteSpace(active))
                                _selection.WeaponId = active;
                        }
                    }
                }

                XDocument personal = LoadDocument(PersonalWeaponXmlPath);
                if (personal != null && personal.Root != null)
                {
                    XElement set = FindSelectedWeaponSet(personal.Root);
                    if (set != null)
                    {
                        _personalWeapons.Clear();
                        ParsePersonalWeapons(set, _personalWeapons);
                        ReadWeaponSetSelection(set);
                    }
                }
            }
            catch (Exception ex)
            {
                LogException("POLICE_PROFILE_LOAD_FAILED", ex);
                loadedSavedProfile = false;
            }

            RemoveLegacyPromptValues();
            EnsureSelection();
            return loadedSavedProfile;
        }

        private LSPDPoliceProfileSelection NewDefaultSelection()
        {
            return new LSPDPoliceProfileSelection
            {
                AgencyId = DefaultAgencyId(),
                StationId = DefaultStationId(),
                PedId = _pedModels.Count > 0 ? _pedModels[0].Id : string.Empty,
                VehicleId = _vehicles.Count > 0 ? _vehicles[0].Id : string.Empty,
                WeaponId = _weapons.Count > 0 ? _weapons[0].Id : string.Empty,
                WeaponSetId = DefaultWeaponSetId,
                FavoritePedId = string.Empty,
                FavoriteVehicleId = string.Empty,
                BackupPedId = string.Empty
            };
        }

        private void EnsureSelection()
        {
            if (FindAgency(_selection.AgencyId) == null)
                _selection.AgencyId = DefaultAgencyId();
            if (FindStation(_selection.StationId) == null)
                _selection.StationId = DefaultStationId();

            if (FindPed(_selection.PedId) == null
                && FindFavoriteModel(_favoritePeds, _selection.PedId) == null)
            {
                _selection.PedId = _favoritePeds.Count > 0
                    ? _favoritePeds[0].Id
                    : (_pedModels.Count > 0 ? _pedModels[0].Id : string.Empty);
            }

            if (FindVehicle(_selection.VehicleId) == null
                && FindFavoriteModel(_favoriteVehicles, _selection.VehicleId) == null)
            {
                _selection.VehicleId = _favoriteVehicles.Count > 0
                    ? _favoriteVehicles[0].Id
                    : (_vehicles.Count > 0 ? _vehicles[0].Id : string.Empty);
            }

            if (FindWeapon(_selection.WeaponId) == null
                && FindPersonalWeapon(_selection.WeaponId) == null)
            {
                _selection.WeaponId = _personalWeapons.Count > 0
                    ? _personalWeapons[0].Id
                    : (_weapons.Count > 0 ? _weapons[0].Id : string.Empty);
            }

            if (string.IsNullOrWhiteSpace(_selection.WeaponSetId))
                _selection.WeaponSetId = DefaultWeaponSetId;
            if (FindFavoriteModel(_favoritePeds, _selection.FavoritePedId) == null)
                _selection.FavoritePedId = _favoritePeds.Count > 0
                    ? _favoritePeds[0].Id
                    : string.Empty;
            if (FindFavoriteModel(_favoriteVehicles, _selection.FavoriteVehicleId) == null)
                _selection.FavoriteVehicleId = _favoriteVehicles.Count > 0
                    ? _favoriteVehicles[0].Id
                    : string.Empty;
            if (FindFavoriteModel(_favoriteBackupPeds, _selection.BackupPedId) == null)
                _selection.BackupPedId = _favoriteBackupPeds.Count > 0
                    ? _favoriteBackupPeds[0].Id
                    : string.Empty;
        }

        private string DefaultAgencyId()
        {
            LSPDPoliceAgencyDefinition value = _agencies.FirstOrDefault(item => item.IsDefault)
                ?? _agencies.FirstOrDefault();
            return value == null ? string.Empty : value.Id;
        }

        private string DefaultStationId()
        {
            // Mission Row is the globally authored default. Agency selection is
            // intentionally not part of this lookup.
            LSPDPoliceStationDefinition value = _stations.FirstOrDefault(item => item.IsDefault)
                ?? _stations.FirstOrDefault();
            return value == null ? string.Empty : value.Id;
        }

        private void ReadWeaponSetSelection(XElement set)
        {
            _selection.WeaponSetId = AttributeOrDefault(set, "id", DefaultWeaponSetId);
            string active = AttributeOrDefault(set, "activeWeaponId", string.Empty);
            if (!string.IsNullOrWhiteSpace(active))
                _selection.WeaponId = active;
        }

        private void RemoveLegacyPromptValues()
        {
            _favoritePeds.RemoveAll(value => IsLegacyPromptValue(value.ModelName));
            _favoriteVehicles.RemoveAll(value => IsLegacyPromptValue(value.ModelName));
            _favoriteBackupPeds.RemoveAll(value => IsLegacyPromptValue(value.ModelName));
            _personalWeapons.RemoveAll(value => IsLegacyPromptValue(value.WeaponName));
            if (IsLegacyPromptValue(_selection.PedId))
                _selection.PedId = string.Empty;
            if (IsLegacyPromptValue(_selection.VehicleId))
                _selection.VehicleId = string.Empty;
            if (IsLegacyPromptValue(_selection.WeaponId))
                _selection.WeaponId = string.Empty;
            if (IsLegacyPromptValue(_selection.BackupPedId))
                _selection.BackupPedId = string.Empty;
        }

        private XElement BuildProfileXml()
        {
            return new XElement(
                "LSPDImmersiveProfile",
                new XAttribute("version", "2.0"),
                new XElement(
                    "Profile",
                    new XAttribute("agencyId", _selection.AgencyId ?? string.Empty),
                    new XAttribute("stationId", _selection.StationId ?? string.Empty),
                    new XAttribute("pedId", _selection.PedId ?? string.Empty),
                    new XAttribute("vehicleId", _selection.VehicleId ?? string.Empty),
                    new XAttribute("weaponId", _selection.WeaponId ?? string.Empty),
                    new XAttribute("weaponSetId", _selection.WeaponSetId ?? DefaultWeaponSetId),
                    new XAttribute("favoritePedId", _selection.FavoritePedId ?? string.Empty),
                    new XAttribute("favoriteVehicleId", _selection.FavoriteVehicleId ?? string.Empty),
                    new XAttribute("backupPedId", _selection.BackupPedId ?? string.Empty)),
                new XElement(
                    "Favorites",
                    new XElement("PedFavorites", _favoritePeds.Select(item => item.ToXml("Ped"))),
                    new XElement("VehicleFavorites", _favoriteVehicles.Select(item => item.ToXml("Vehicle"))),
                    new XElement("BackupPedFavorites", _favoriteBackupPeds.Select(item => item.ToXml("Ped"))),
                    new XElement(
                        "PersonalWeaponCollection",
                        new XAttribute("file", Path.GetFileName(PersonalWeaponXmlPath)),
                        new XAttribute("setId", _selection.WeaponSetId ?? DefaultWeaponSetId),
                        new XAttribute("activeWeaponId", _selection.WeaponId ?? string.Empty))));
        }

        private XElement BuildPersonalWeaponsXml()
        {
            return new XElement(
                "LSPDImmersivePersonalWeapons",
                new XAttribute("version", "2.0"),
                new XElement(
                    "WeaponSet",
                    new XAttribute("id", _selection.WeaponSetId ?? DefaultWeaponSetId),
                    new XAttribute("activeWeaponId", _selection.WeaponId ?? string.Empty),
                    _personalWeapons.Select(item => item.ToXml())));
        }

        private static void ParseUtility(
            XElement root,
            ICollection<LSPDPoliceAgencyDefinition> agencies,
            ICollection<LSPDPoliceStationDefinition> stations,
            ICollection<LSPDPoliceLocationDefinition> locations,
            ICollection<LSPDPoliceCoordinationDefinition> coordination)
        {
            if (root == null || root.Name != "LSPDImmersiveUtility")
                throw new InvalidDataException("Police utility XML root is invalid.");

            XElement parent = root.Element("Agencies");
            if (parent != null)
                foreach (XElement node in parent.Elements("Agency"))
                    agencies.Add(LSPDPoliceAgencyDefinition.FromXml(node));

            parent = root.Element("Stations");
            if (parent != null)
                foreach (XElement node in parent.Elements("Station"))
                    stations.Add(LSPDPoliceStationDefinition.FromXml(node));

            parent = root.Element("Locations");
            if (parent != null)
                foreach (XElement node in parent.Elements("Location"))
                    locations.Add(LSPDPoliceLocationDefinition.FromXml(node));

            parent = root.Element("Coordination");
            if (parent != null)
                foreach (XElement node in parent.Elements("Route"))
                    coordination.Add(LSPDPoliceCoordinationDefinition.FromXml(node));
        }

        private static void ParsePeds(XElement root, ICollection<LSPDPoliceModelDefinition> values)
        {
            if (root == null || root.Name != "LSPoliceModelPed")
                throw new InvalidDataException("Police ped XML root is invalid.");
            XElement parent = root.Element("PedModels");
            if (parent != null)
                foreach (XElement node in parent.Elements("Ped"))
                    values.Add(LSPDPoliceModelDefinition.FromXml(node));
        }

        private static void ParseVehicles(XElement root, ICollection<LSPDPoliceVehicleDefinition> values)
        {
            if (root == null || root.Name != "LSPoliceVehicle")
                throw new InvalidDataException("Police vehicle XML root is invalid.");
            XElement parent = root.Element("Vehicles");
            if (parent != null)
                foreach (XElement node in parent.Elements("Vehicle"))
                    values.Add(LSPDPoliceVehicleDefinition.FromXml(node));
        }

        private static void ParseWeapons(XElement root, ICollection<LSPDPoliceWeaponDefinition> values)
        {
            if (root == null || root.Name != "LSPoliceWeaponData")
                throw new InvalidDataException("Police weapon XML root is invalid.");
            XElement parent = root.Element("Weapons");
            if (parent != null)
                foreach (XElement node in parent.Elements("Weapon"))
                    values.Add(LSPDPoliceWeaponDefinition.FromXml(node));
        }

        private static void ParseFavoriteModels(
            XElement favorites,
            string parentName,
            ICollection<LSPDPoliceFavoriteModel> destination,
            bool vehicle)
        {
            XElement parent = favorites.Element(parentName);
            if (parent == null)
                return;
            foreach (XElement node in parent.Elements(vehicle ? "Vehicle" : "Ped"))
            {
                LSPDPoliceFavoriteModel value = LSPDPoliceFavoriteModel.FromXml(node);
                if (!IsLegacyPromptValue(value.ModelName))
                    destination.Add(value);
            }
        }

        private static void ParsePersonalWeapons(
            XElement set,
            ICollection<LSPDPoliceFavoriteWeapon> destination)
        {
            foreach (XElement node in set.Elements("WeaponRef"))
            {
                LSPDPoliceFavoriteWeapon value = LSPDPoliceFavoriteWeapon.FromXml(node);
                if (IsLegacyPromptValue(value.WeaponName))
                    continue;
                if (!destination.Any(item =>
                    Matches(item.Id, value.Id) || Matches(item.WeaponName, value.WeaponName)))
                {
                    destination.Add(value);
                }
            }
        }

        private static XDocument LoadDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2 * 1024 * 1024
            };
            using (XmlReader reader = XmlReader.Create(path, settings))
                return XDocument.Load(reader);
        }

        private static void SaveAtomic(string path, XDocument document)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                document.Save(temporary);
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static void ValidateUniqueIds(IEnumerable<string> ids, string category)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ids)
                if (!found.Add(id))
                    throw new InvalidDataException("Duplicate Police " + category + " id: " + id);
        }

        private static void UpsertSingleModel(
            ICollection<LSPDPoliceFavoriteModel> destination,
            LSPDPoliceFavoriteModel value)
        {
            // Update one personal entry without clearing the player's collection.
            LSPDPoliceFavoriteModel existing = destination.FirstOrDefault(item => Matches(item.Id, value.Id));
            if (existing != null)
                destination.Remove(existing);
            destination.Add(value);
        }

        private static string FavoriteModelId(IEnumerable<LSPDPoliceFavoriteModel> values, string prefix, string model)
        {
            LSPDPoliceFavoriteModel existing = values.FirstOrDefault(item => Matches(item.ModelName, model));
            return existing == null ? prefix + "-" + model.ToLowerInvariant() : existing.Id;
        }

        private static LSPDPoliceFavoriteModel FindFavoriteModel(
            IEnumerable<LSPDPoliceFavoriteModel> values,
            string idOrModel)
        {
            return values.FirstOrDefault(value =>
                Matches(value.Id, idOrModel) || Matches(value.ModelName, idOrModel));
        }

        private static string FirstAttribute(XElement node, params string[] names)
        {
            foreach (string name in names)
            {
                string value = (string)node.Attribute(name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return string.Empty;
        }

        private static string AttributeOrDefault(XElement node, string name, string fallback)
        {
            string value = (string)node.Attribute(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string NormalizeIdentifier(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string FriendlyModelName(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return "Personal Model";
            string normalized = modelName.Trim().Replace('_', ' ');
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
        }

        private static bool IsLegacyPromptValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            string normalized = value.Trim().Replace('_', ' ').Replace('-', ' ');
            return normalized.IndexOf(
                "enter model or weapon name",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Matches(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void LogException(string category, Exception ex)
        {
            if (_log != null)
                _log.Exception(category, ex);
        }
    }

    internal sealed class LSPDPoliceProfileSelection
    {
        internal string AgencyId { get; set; }
        internal string StationId { get; set; }
        internal string PedId { get; set; }
        internal string VehicleId { get; set; }
        internal string WeaponId { get; set; }
        internal string WeaponSetId { get; set; }
        internal string FavoritePedId { get; set; }
        internal string FavoriteVehicleId { get; set; }
        internal string BackupPedId { get; set; }
    }

    internal sealed class LSPDPoliceAgencyDefinition
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string CallSign { get; private set; }
        internal string AuthorityType { get; private set; }
        internal bool IsDefault { get; private set; }

        internal static LSPDPoliceAgencyDefinition FromXml(XElement node)
        {
            return new LSPDPoliceAgencyDefinition
            {
                Id = XmlValue.Required(node, "id", "Police agency"),
                DisplayName = XmlValue.Required(node, "name", "Police agency"),
                CallSign = XmlValue.Required(node, "callSign", "Police agency"),
                AuthorityType = XmlValue.Optional(node, "authorityType", "Local Police"),
                IsDefault = XmlValue.Bool(node, "default")
            };
        }
    }

    internal sealed class LSPDPoliceStationDefinition
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string AgencyId { get; private set; }
        internal string LocationId { get; private set; }
        internal string CoordinateKey { get; private set; }
        internal float ExteriorX { get; private set; }
        internal float ExteriorY { get; private set; }
        internal float ExteriorZ { get; private set; }
        internal float ExteriorHeading { get; private set; }
        internal float VehicleX { get; private set; }
        internal float VehicleY { get; private set; }
        internal float VehicleZ { get; private set; }
        internal float VehicleHeading { get; private set; }
        internal bool ExteriorSafe { get; private set; }
        internal bool InteriorVerified { get; private set; }
        internal bool IsDefault { get; private set; }

        internal static LSPDPoliceStationDefinition FromXml(XElement node)
        {
            return new LSPDPoliceStationDefinition
            {
                Id = XmlValue.Required(node, "id", "Police station"),
                DisplayName = XmlValue.Required(node, "name", "Police station"),
                AgencyId = XmlValue.Required(node, "agencyId", "Police station"),
                LocationId = XmlValue.Required(node, "locationId", "Police station"),
                CoordinateKey = XmlValue.Required(node, "coordinateKey", "Police station"),
                ExteriorX = XmlValue.Float(node, "x"),
                ExteriorY = XmlValue.Float(node, "y"),
                ExteriorZ = XmlValue.Float(node, "z"),
                ExteriorHeading = XmlValue.Float(node, "heading"),
                VehicleX = XmlValue.Float(node, "vehicleX"),
                VehicleY = XmlValue.Float(node, "vehicleY"),
                VehicleZ = XmlValue.Float(node, "vehicleZ"),
                VehicleHeading = XmlValue.Float(node, "vehicleHeading"),
                ExteriorSafe = XmlValue.Bool(node, "exteriorSafe"),
                InteriorVerified = XmlValue.Bool(node, "interiorVerified"),
                IsDefault = XmlValue.Bool(node, "default")
            };
        }
    }

    internal sealed class LSPDPoliceLocationDefinition
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string District { get; private set; }
        internal string CoordinateKey { get; private set; }
        internal float X { get; private set; }
        internal float Y { get; private set; }
        internal float Z { get; private set; }
        internal bool ExteriorSafe { get; private set; }

        internal static LSPDPoliceLocationDefinition FromXml(XElement node)
        {
            return new LSPDPoliceLocationDefinition
            {
                Id = XmlValue.Required(node, "id", "Police location"),
                DisplayName = XmlValue.Required(node, "name", "Police location"),
                District = XmlValue.Required(node, "district", "Police location"),
                CoordinateKey = XmlValue.Required(node, "coordinateKey", "Police location"),
                X = XmlValue.Float(node, "x"),
                Y = XmlValue.Float(node, "y"),
                Z = XmlValue.Float(node, "z"),
                ExteriorSafe = XmlValue.Bool(node, "exteriorSafe")
            };
        }
    }

    internal sealed class LSPDPoliceCoordinationDefinition
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string FromLocationId { get; private set; }
        internal string ToLocationId { get; private set; }
        internal string RouteKey { get; private set; }

        internal static LSPDPoliceCoordinationDefinition FromXml(XElement node)
        {
            return new LSPDPoliceCoordinationDefinition
            {
                Id = XmlValue.Required(node, "id", "Coordination route"),
                DisplayName = XmlValue.Required(node, "name", "Coordination route"),
                FromLocationId = XmlValue.Required(node, "fromLocationId", "Coordination route"),
                ToLocationId = XmlValue.Required(node, "toLocationId", "Coordination route"),
                RouteKey = XmlValue.Required(node, "routeKey", "Coordination route")
            };
        }
    }

    internal sealed class LSPDPoliceModelDefinition
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string ModelName { get; private set; }
        internal string ModelType { get; private set; }
        internal string AgencyId { get; private set; }
        internal string Source { get; private set; }
        internal bool IsAddon { get; private set; }

        internal static LSPDPoliceModelDefinition FromXml(XElement node)
        {
            return new LSPDPoliceModelDefinition
            {
                Id = XmlValue.Required(node, "id", "Police ped"),
                DisplayName = XmlValue.Required(node, "name", "Police ped"),
                ModelName = XmlValue.Required(node, "model", "Police ped"),
                ModelType = XmlValue.Required(node, "modelType", "Police ped"),
                AgencyId = XmlValue.Optional(node, "agencyId", string.Empty),
                Source = XmlValue.Required(node, "source", "Police ped"),
                IsAddon = XmlValue.Bool(node, "addon")
            };
        }
    }

    internal sealed class LSPDPoliceVehicleDefinition
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string ModelName { get; private set; }
        internal string VehicleType { get; private set; }
        internal string AgencyId { get; private set; }
        internal string Source { get; private set; }
        internal bool IsAddon { get; private set; }

        internal static LSPDPoliceVehicleDefinition FromXml(XElement node)
        {
            return new LSPDPoliceVehicleDefinition
            {
                Id = XmlValue.Required(node, "id", "Police vehicle"),
                DisplayName = XmlValue.Required(node, "name", "Police vehicle"),
                ModelName = XmlValue.Required(node, "model", "Police vehicle"),
                VehicleType = XmlValue.Required(node, "vehicleType", "Police vehicle"),
                AgencyId = XmlValue.Optional(node, "agencyId", string.Empty),
                Source = XmlValue.Required(node, "source", "Police vehicle"),
                IsAddon = XmlValue.Bool(node, "addon")
            };
        }
    }

    internal sealed class LSPDPoliceWeaponDefinition
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string WeaponName { get; private set; }
        internal string WeaponType { get; private set; }
        internal string Collection { get; private set; }
        internal int DefaultAmmo { get; private set; }

        internal static LSPDPoliceWeaponDefinition FromXml(XElement node)
        {
            return new LSPDPoliceWeaponDefinition
            {
                Id = XmlValue.Required(node, "id", "Police weapon"),
                DisplayName = XmlValue.Required(node, "name", "Police weapon"),
                WeaponName = XmlValue.Required(node, "weapon", "Police weapon"),
                WeaponType = XmlValue.Required(node, "weaponType", "Police weapon"),
                Collection = XmlValue.Required(node, "collection", "Police weapon"),
                DefaultAmmo = XmlValue.NonNegativeInt(node, "defaultAmmo")
            };
        }
    }

    internal sealed class LSPDPoliceFavoriteModel
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string ModelName { get; private set; }
        internal string ModelType { get; private set; }
        internal string Source { get; private set; }

        internal static LSPDPoliceFavoriteModel FromPed(LSPDPoliceModelDefinition model, string id)
        {
            return Custom(id, model.DisplayName, model.ModelName, model.ModelType, model.Source);
        }

        internal static LSPDPoliceFavoriteModel FromVehicle(LSPDPoliceVehicleDefinition vehicle, string id)
        {
            return Custom(id, vehicle.DisplayName, vehicle.ModelName, vehicle.VehicleType, vehicle.Source);
        }

        internal static LSPDPoliceFavoriteModel Custom(
            string id,
            string displayName,
            string modelName,
            string modelType,
            string source)
        {
            return new LSPDPoliceFavoriteModel
            {
                Id = id,
                DisplayName = displayName,
                ModelName = modelName,
                ModelType = modelType,
                Source = source
            };
        }

        internal static LSPDPoliceFavoriteModel FromXml(XElement node)
        {
            return Custom(
                XmlValue.Required(node, "id", "Favourite model"),
                XmlValue.Required(node, "name", "Favourite model"),
                XmlValue.Required(node, "model", "Favourite model"),
                XmlValue.Required(node, "modelType", "Favourite model"),
                XmlValue.Required(node, "source", "Favourite model"));
        }

        internal XElement ToXml(string elementName)
        {
            return new XElement(
                elementName,
                new XAttribute("id", Id),
                new XAttribute("name", DisplayName),
                new XAttribute("model", ModelName),
                new XAttribute("modelType", ModelType),
                new XAttribute("source", Source));
        }
    }

    internal sealed class LSPDPoliceFavoriteWeapon
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string WeaponName { get; private set; }
        internal string WeaponType { get; private set; }
        internal string Source { get; private set; }
        internal int Ammo { get; private set; }
        internal int Tint { get; private set; }
        internal IReadOnlyList<string> Components { get; private set; } = new List<string>().AsReadOnly();

        internal static LSPDPoliceFavoriteWeapon FromCaptured(
            string id, string name, string weapon, int ammo, int tint, IEnumerable<string> components)
        {
            return new LSPDPoliceFavoriteWeapon
            {
                Id = id, DisplayName = name, WeaponName = weapon,
                WeaponType = "Personal", Source = "Player", Ammo = Math.Max(0, ammo),
                Tint = Math.Max(0, tint), Components = components.Distinct().ToList().AsReadOnly()
            };
        }

        internal static LSPDPoliceFavoriteWeapon FromWeapon(LSPDPoliceWeaponDefinition weapon)
        {
            return new LSPDPoliceFavoriteWeapon
            {
                Id = weapon.Id,
                DisplayName = weapon.DisplayName,
                WeaponName = weapon.WeaponName,
                WeaponType = weapon.WeaponType,
                Source = weapon.Collection,
                Ammo = weapon.DefaultAmmo
            };
        }

        internal static LSPDPoliceFavoriteWeapon FromXml(XElement node)
        {
            return new LSPDPoliceFavoriteWeapon
            {
                Id = XmlValue.Required(node, "id", "Personal weapon"),
                DisplayName = XmlValue.Required(node, "name", "Personal weapon"),
                WeaponName = XmlValue.Required(node, "weapon", "Personal weapon"),
                WeaponType = XmlValue.Required(node, "weaponType", "Personal weapon"),
                Source = XmlValue.Required(node, "source", "Personal weapon"),
                Ammo = XmlValue.NonNegativeInt(node, "ammo"),
                Tint = node.Attribute("tint") == null ? 0 : XmlValue.NonNegativeInt(node, "tint"),
                Components = (node.Element("Components") == null
                    ? new List<string>()
                    : node.Element("Components").Elements("Component")
                        .Select(item => XmlValue.Required(item, "name", "Weapon component")).ToList()).AsReadOnly()
            };
        }

        internal XElement ToXml()
        {
            return new XElement(
                "WeaponRef",
                new XAttribute("id", Id),
                new XAttribute("name", DisplayName),
                new XAttribute("weapon", WeaponName),
                new XAttribute("weaponType", WeaponType),
                new XAttribute("source", Source),
                new XAttribute("ammo", Ammo),
                new XAttribute("tint", Tint),
                new XElement("Components", Components.Select(value =>
                    new XElement("Component", new XAttribute("name", value)))));
        }
    }

    internal static class XmlValue
    {
        internal static string Required(XElement node, string attribute, string category)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(category + " attribute is missing: " + attribute);
            return value.Trim();
        }

        internal static string Optional(XElement node, string attribute, string fallback)
        {
            string value = (string)node.Attribute(attribute);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        internal static bool Bool(XElement node, string attribute)
        {
            bool result;
            return bool.TryParse((string)node.Attribute(attribute), out result) && result;
        }

        internal static int NonNegativeInt(XElement node, string attribute)
        {
            int result;
            if (!int.TryParse(
                    Required(node, attribute, "Numeric Police value"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out result)
                || result < 0)
            {
                throw new InvalidDataException("Police numeric attribute is invalid: " + attribute);
            }
            return result;
        }

        internal static float Float(XElement node, string attribute)
        {
            string raw = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(raw))
                return 0f;
            float result;
            if (!float.TryParse(
                    raw,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out result))
            {
                throw new InvalidDataException("Police coordinate is invalid: " + attribute);
            }
            return result;
        }
    }
}
