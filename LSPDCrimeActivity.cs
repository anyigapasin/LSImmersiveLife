using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

namespace LSImmersiveLife
{
    /// <summary>
    /// Owns the player-led Crime Activity gameplay layer. It loads the
    /// authored XML catalog, keeps quiet nearby intelligence separate from
    /// Dispatch, stages a single owned scene only after the officer chooses to
    /// investigate, gives its actors real GTA tasks, and cleans only its own
    /// entities after a valid completion, decline, failure, or recovery.
    /// </summary>
    internal sealed class LSPDCrimeActivity
    {
        private const float DefaultDeferredCleanupDistance = 200f;
        private const int AmbientTaskRefreshMilliseconds = 6500;
        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSPDAudioDispatch _audio;
        private readonly LSImmersiveLog _log;
        private readonly LSPoliceCrimeActivitySettings _settings;
        private readonly LSPoliceCleanupSettings _cleanupSettings;
        private readonly LSPDCrimeActivityEvent _events;
        private readonly LSPDCriminalAssetCatalog _criminalAssets;
        private readonly Random _random = new Random();
        private Dictionary<string, LSPDGroupCrimeActivityDefinition> _activities =
            new Dictionary<string, LSPDGroupCrimeActivityDefinition>(
                StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, LSPDCrimeActivityLocationDefinition> _locations =
            new Dictionary<string, LSPDCrimeActivityLocationDefinition>(
                StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, LSPDCrimeActivityGroupDefinition> _groups =
            new Dictionary<string, LSPDCrimeActivityGroupDefinition>(
                StringComparer.OrdinalIgnoreCase);
        private readonly List<LSPDActivityIntel> _nearbyIntel = new List<LSPDActivityIntel>();
        private readonly List<Ped> _sceneCriminals = new List<Ped>();
        private readonly List<Vehicle> _sceneVehicles = new List<Vehicle>();
        private readonly List<DeferredCleanup> _deferredCleanup = new List<DeferredCleanup>();
        private readonly Queue<string> _recentSceneAssetValues = new Queue<string>();
        private readonly HashSet<string> _recentSceneAssetValueSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _locationCooldowns =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastObservedAt = DateTime.MinValue;
        private DateTime _nextDiscoveryAt = DateTime.MinValue;
        private DateTime _redeploymentAvailableAt = DateTime.MinValue;
        private DateTime _scenePreparationDeadline = DateTime.MinValue;
        private DateTime _sceneActivatedAt = DateTime.MinValue;
        private DateTime _lastAmbientTaskAt = DateTime.MinValue;
        private LSPDActivityIntel _availableIntel;
        private LSPDActivityIntel _activeIntel;
        private LSPDCrimeSceneResources _sceneResources;
        private LSPDCrimeActivityState _state = LSPDCrimeActivityState.None;
        private Blip _sceneBlip;
        private string _activityOperationId = string.Empty;
        private string _audioScope = string.Empty;
        private bool _nearbyTipReported;
        private bool _sceneEscalated;
        private bool _backupRequested;
        private string _lastOutcome = string.Empty;

        private sealed class DeferredCleanup
        {
            internal Ped Ped;
            internal Vehicle Vehicle;
            internal DateTime Earliest;
            internal DateTime Expires;
        }

        private float DeferredCleanupDistance
        {
            get
            {
                return _cleanupSettings == null
                    ? DefaultDeferredCleanupDistance
                    : _cleanupSettings.SafeCleanupDistance;
            }
        }

        internal LSPDCrimeActivity(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log,
            LSPoliceCrimeActivitySettings settings,
            LSPoliceCleanupSettings cleanupSettings,
            LSPDCriminalAssetCatalog criminalAssets = null)
        {
            if (audio == null)
                throw new ArgumentNullException("audio");
            _audio = audio;
            _paths = paths ?? throw new ArgumentNullException("paths");
            _log = log;
            _settings = settings ?? LSPoliceCrimeActivitySettings.Default();
            _cleanupSettings = cleanupSettings ?? LSPoliceCleanupSettings.Default();
            _criminalAssets = criminalAssets;
            _events = new LSPDCrimeActivityEvent();
        }

        internal bool IsLoaded { get; private set; }
        internal string LastLoadError { get; private set; }

        internal int ActivityCount
        {
            get { return _activities.Count; }
        }

        internal int LocationCount
        {
            get { return _locations.Count; }
        }

        internal int GroupCount
        {
            get { return _groups.Count; }
        }

        internal IEnumerable<LSPDGroupCrimeActivityDefinition> Activities
        {
            get { return _activities.Values; }
        }

        internal IEnumerable<LSPDCrimeActivityLocationDefinition> Locations
        {
            get { return _locations.Values; }
        }

        internal IEnumerable<LSPDCrimeActivityGroupDefinition> Groups
        {
            get { return _groups.Values; }
        }
        internal IReadOnlyList<LSPDActivityIntel> NearbyIntel { get { return _nearbyIntel; } }
        internal DateTime LastObservedAt { get { return _lastObservedAt; } }
        internal LSPDCrimeActivityState State { get { return _state; } }
        internal bool Active
        {
            get
            {
                return _state == LSPDCrimeActivityState.PreparingScene
                    || _state == LSPDCrimeActivityState.Observing
                    || _state == LSPDCrimeActivityState.Investigating
                    || _state == LSPDCrimeActivityState.BackupRequested
                    || _state == LSPDCrimeActivityState.Resolving;
            }
        }
        internal bool BlocksOtherPoliceActivities { get { return Active; } }
        internal bool HasAvailableActivity { get { return _availableIntel != null; } }
        internal bool CanConfront
        {
            get
            {
                if (_state != LSPDCrimeActivityState.Observing
                    && _state != LSPDCrimeActivityState.Investigating
                    && _state != LSPDCrimeActivityState.BackupRequested)
                    return false;
                return _sceneCriminals.Any(ped => ped != null
                    && ped.Exists() && !ped.IsDead);
            }
        }
        internal LSPDActivityIntel CurrentIntel
        {
            get { return _activeIntel ?? _availableIntel; }
        }
        internal IReadOnlyList<Ped> ActiveCriminals
        {
            get
            {
                return _sceneCriminals.Where(ped => ped != null && ped.Exists()
                    && !ped.IsDead).ToList();
            }
        }
        internal Vector3 SupportPosition
        {
            get
            {
                LSPDActivityIntel intel = CurrentIntel;
                return intel == null ? Vector3.Zero : intel.Position;
            }
        }
        internal string StatusText
        {
            get
            {
                if (Active && _activeIntel != null)
                    return _state + ": " + _activeIntel.Definition.Name
                        + (_backupRequested ? " | Backup requested" : string.Empty);
                if (_availableIntel != null)
                    return "Intelligence available: " + _availableIntel.Definition.Name;
                return string.IsNullOrWhiteSpace(_lastOutcome)
                    ? "No Criminal Activity is active."
                    : _lastOutcome;
            }
        }

        /// <summary>
        /// Loads the database explicitly. It is never called from construction,
        /// role selection, or the normal game tick.
        /// </summary>
        internal bool Reload()
        {
            try
            {
                LSPDCrimeActivityCatalog catalog;
                string error;
                if (!_events.TryLoadCatalog(_paths, out catalog, out error))
                {
                    LastLoadError = error;
                    return false;
                }

                _locations = catalog.Locations;
                _groups = catalog.Groups;
                _activities = catalog.Activities;
                IsLoaded = true;
                LastLoadError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                // Failed reloads retain the last validated catalog. No caller
                // should lose usable intelligence because an XML edit is incomplete.
                LastLoadError = ex.Message;
                return false;
            }
        }

        internal bool TryGet(
            string activityId,
            out LSPDGroupCrimeActivityDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(activityId))
            {
                definition = null;
                return false;
            }

            return _activities.TryGetValue(activityId, out definition);
        }

        internal bool TryGetLocation(
            string locationId,
            out LSPDCrimeActivityLocationDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(locationId))
            {
                definition = null;
                return false;
            }

            return _locations.TryGetValue(locationId, out definition);
        }

        internal bool TryGetGroup(
            string groupId,
            out LSPDCrimeActivityGroupDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                definition = null;
                return false;
            }

            return _groups.TryGetValue(groupId, out definition);
        }

        /// <summary>
        /// Observes authored intelligence at the player's current location. This
        /// is intentionally separate from Dispatch: it never spawns actors,
        /// requests backup, plays audio, or changes the active case. The UI can
        /// review the resulting tips and choose its own follow-up.
        /// </summary>
        internal int Observe(
            Ped player,
            LSPDGangDataIntegration gangData)
        {
            _nearbyIntel.Clear();
            _lastObservedAt = DateTime.UtcNow;
            if (!IsLoaded || player == null || !player.Exists())
                return 0;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LSPDCrimeActivityLocationDefinition location in _locations.Values)
            {
                Vector3 position;
                if (!TryResolveLocation(location, out position))
                    continue;
                float distance = player.Position.DistanceTo(position);
                if (distance > _settings.MaximumDeploymentDistance)
                    continue;
                foreach (LSPDGroupCrimeActivityDefinition activity in _activities.Values)
                {
                    if (!activity.LocationIds.Contains(
                        location.Id,
                        StringComparer.OrdinalIgnoreCase)
                        || !seen.Add(activity.Id))
                        continue;
                    LSPDTurfZoneDefinition turf = gangData == null
                        ? null : gangData.FindNearbyTurf(position);
                    string territory = turf == null
                        ? string.Empty
                        : turf.Name + " / " + turf.OwnerGangName;
                    _nearbyIntel.Add(new LSPDActivityIntel
                    {
                        Id = activity.Id,
                        Definition = activity,
                        Location = location,
                        Position = position,
                        DistanceMeters = distance,
                        ObservedAt = _lastObservedAt,
                        Territory = territory,
                        Summary = activity.IntelSummary
                    });
                }
            }

            _nearbyIntel.Sort((left, right) =>
                left.DistanceMeters.CompareTo(right.DistanceMeters));
            return _nearbyIntel.Count;
        }

        /// <summary>
        /// Creates one player-requested intelligence lead without turning it
        /// into Dispatch or spawning scene actors from the UI callback. Event
        /// selection remains XML-owned; this gameplay owner only places a
        /// distant authored context into a safe, relevant patrol area when no
        /// authored coordinate is already within deployment range.
        /// </summary>
        internal string RequestNearbyActivity(
            Ped player,
            LSPDGangDataIntegration gangData)
        {
            if (!_settings.Enabled)
                return "Crime Activity is disabled in LS Immersive settings.";
            if (!IsLoaded)
                return "Crime Activity intelligence is not loaded.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (Active)
                return "A Crime Activity investigation is already active.";
            if (_availableIntel != null)
                return "Crime Activity intelligence is already available: "
                    + _availableIntel.Definition.Name + ".";

            DateTime now = DateTime.UtcNow;
            if (now < _redeploymentAvailableAt)
            {
                int seconds = Math.Max(1, (int)Math.Ceiling(
                    (_redeploymentAvailableAt - now).TotalSeconds));
                return "Crime Activity deployment is cooling down for "
                    + seconds + " more second(s).";
            }

            LSPDActivityIntel selected;
            if (!_events.TrySelectRequested(this, player, gangData, _settings,
                _locationCooldowns, now, out selected))
            {
                return "No valid off-cooldown Crime Activity could be selected from the XML database.";
            }

            bool placedNearOfficer = selected.DistanceMeters
                > _settings.MaximumDeploymentDistance;
            if (placedNearOfficer)
                selected.Position = FindRequestedScenePosition(player, selected.Position);
            selected.DistanceMeters = player.Position.DistanceTo(selected.Position);
            selected.ObservedAt = now;
            SetAvailableIntel(selected, placedNearOfficer
                ? "Officer requested XML Crime Activity intelligence placed near the current patrol area."
                : "Officer requested XML Crime Activity intelligence already near the current patrol area.");
            _nextDiscoveryAt = now.AddSeconds(_settings.DiscoveryScanSeconds);
            return "Crime Activity intelligence requested: "
                + selected.Definition.Name + " near " + selected.Location.Name
                + " (" + selected.DistanceMeters.ToString("0", CultureInfo.InvariantCulture)
                + " m). Review the lead or set its GPS from Police UI.";
        }

        internal bool TryGetIntel(
            string activityId,
            out LSPDActivityIntel intel)
        {
            intel = _nearbyIntel.FirstOrDefault(value =>
                string.Equals(value.Id, activityId,
                    StringComparison.OrdinalIgnoreCase));
            return intel != null;
        }

        internal bool TryResolveLocation(
            LSPDCrimeActivityLocationDefinition location,
            out Vector3 position)
        {
            position = Vector3.Zero;
            if (location == null || string.IsNullOrWhiteSpace(location.CoordinateKey))
                return false;
            return KnownCoordinates.TryGetValue(
                location.CoordinateKey,
                out position);
        }

        internal void ClearObservations()
        {
            _nearbyIntel.Clear();
            _lastObservedAt = DateTime.MinValue;
        }

        /// <summary>
        /// Runs the Crime Activity owner. Available intelligence is harmless
        /// and does not block Dispatch; only a deliberately activated scene
        /// blocks other Police activity while it is being investigated.
        /// </summary>
        internal void Process(
            Ped player,
            bool isPatrolling,
            bool mayDiscover,
            LSPDGangDataIntegration gangData,
            bool paused)
        {
            DateTime now = DateTime.UtcNow;
            ProcessDeferredCleanup(now, player);
            TrimLocationCooldowns(now);
            if (paused || player == null || !player.Exists())
                return;

            if (!_settings.Enabled)
            {
                if (Active)
                    FinishActivity(player, false, "Crime Activity was disabled in LS Immersive settings.", false);
                return;
            }

            if (Active)
            {
                ProcessActiveScene(player, now);
                return;
            }

            if (!isPatrolling)
                return;

            if (_availableIntel != null)
            {
                // A passive lead may remain in the UI while another Police
                // owner is active, but it must not post a nearby tip or audio
                // on top of Dispatch, Convoy, gang, NPC, or Backup gameplay.
                if (mayDiscover)
                    ProcessAvailableIntel(player, now);
                return;
            }

            if (!mayDiscover || !_settings.AutoDiscover
                || now < _nextDiscoveryAt || now < _redeploymentAvailableAt)
                return;

            _nextDiscoveryAt = now.AddSeconds(_settings.DiscoveryScanSeconds);
            LSPDActivityIntel selected;
            if (!_events.TrySelectNearby(
                this,
                player,
                gangData,
                _settings,
                _locationCooldowns,
                now,
                out selected))
                return;

            SetAvailableIntel(selected, "A quiet Crime Activity lead became available during patrol.");
        }

        /// <summary>
        /// Starts a scene only after an officer selects a nearby piece of
        /// intelligence. The XML entry remains data; GTA models are streamed
        /// asynchronously from Process instead of waiting in the UI callback.
        /// </summary>
        internal string BeginInvestigation(Ped player, string activityId)
        {
            if (!_settings.Enabled)
                return "Crime Activity is disabled in LS Immersive settings.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (!IsLoaded)
                return "Crime Activity intelligence is not loaded.";
            if (Active)
                return "A Crime Activity investigation is already active.";

            if (!EnsureAvailableIntel(activityId))
                return "Choose a nearby Crime Activity tip before starting an investigation.";

            _availableIntel.DistanceMeters = player.Position.DistanceTo(_availableIntel.Position);
            if (_availableIntel.DistanceMeters > _settings.SceneActivationDistance)
            {
                return "Travel within " + _settings.SceneActivationDistance
                    + " m of " + _availableIntel.Location.Name
                    + " before beginning the investigation.";
            }

            LSPDCrimeSceneResources resources;
            if (!_events.TryResolveSceneResources(
                this, _availableIntel, _criminalAssets, _random, out resources))
            {
                MarkLocationCooldown(_availableIntel.Location, DateTime.UtcNow);
                _lastOutcome = "Crime Activity could not resolve its XML scene resources.";
                _availableIntel = null;
                LogDebug("POLICE_CRIME_ACTIVITY_RESOURCES_INVALID", _lastOutcome);
                return _lastOutcome;
            }

            int desiredParticipants = Math.Max(2, _settings.ParticipantCount);
            // Keep a small XML-backed alternate set so one candidate that
            // cannot stream does not decide the outcome for the whole scene.
            // The catalog itself is already bounded; this only adds a few
            // candidates beyond the number of actors that will be created.
            int pedCandidateLimit = Math.Min(
                resources.PedModels == null ? 0 : resources.PedModels.Count,
                desiredParticipants + 4);
            List<LSPDCrimeResourceDefinition> selectedPeds = SelectUsableModels(
                resources.PedModels, pedCandidateLimit, false);
            if (selectedPeds.Count == 0)
            {
                MarkLocationCooldown(_availableIntel.Location, DateTime.UtcNow);
                _lastOutcome = "Crime Activity criminal ped models are unavailable in this GTA build.";
                _availableIntel = null;
                LogDebug("POLICE_CRIME_ACTIVITY_MODELS_INVALID", _lastOutcome);
                return _lastOutcome;
            }

            List<LSPDCrimeResourceDefinition> selectedWeapons =
                SelectUsableWeapons(resources.Weapons, desiredParticipants);
            if (selectedWeapons.Count == 0)
            {
                MarkLocationCooldown(_availableIntel.Location, DateTime.UtcNow);
                _lastOutcome = "Crime Activity criminal weapon assets are unavailable in this GTA build.";
                _availableIntel = null;
                LogDebug("POLICE_CRIME_ACTIVITY_WEAPONS_INVALID", _lastOutcome);
                return _lastOutcome;
            }

            List<LSPDCrimeResourceDefinition> selectedVehicles = SelectUsableModels(
                resources.VehicleModels, 1, true);

            // Resolve one stable, usable scene subset before requesting GTA
            // models. The XML catalog can expose many varied candidates, but
            // streaming every candidate for a three-person scene adds needless
            // work and makes scene creation depend on models it will not use.
            _sceneResources = new LSPDCrimeSceneResources
            {
                PedModels = selectedPeds,
                Weapons = selectedWeapons,
                VehicleModels = selectedVehicles,
                PedPoolId = resources.PedPoolId,
                VehiclePoolId = resources.VehiclePoolId,
                WeaponPoolId = resources.WeaponPoolId
            };
            _activeIntel = _availableIntel;
            _availableIntel = null;
            _scenePreparationDeadline = DateTime.UtcNow.AddSeconds(
                _settings.AssetPreparationTimeoutSeconds);
            _sceneActivatedAt = DateTime.MinValue;
            _lastAmbientTaskAt = DateTime.MinValue;
            _sceneEscalated = false;
            _backupRequested = false;
            _nearbyTipReported = true;
            if (string.IsNullOrWhiteSpace(_activityOperationId))
                _activityOperationId = Guid.NewGuid().ToString("N");
            SetState(LSPDCrimeActivityState.PreparingScene);
            RequestSceneModels();
            LogRuntime(
                "POLICE_CRIME_ACTIVITY_INVESTIGATION_STARTED",
                "Activity=" + _activeIntel.Id + "; Location=" + _activeIntel.Location.Id
                + "; Distance=" + _activeIntel.DistanceMeters.ToString("0", CultureInfo.InvariantCulture));
            LogRuntime(
                "POLICE_CRIME_ACTIVITY_ASSETS_SELECTED",
                "Activity=" + _activeIntel.Id
                + "; Pools=Ped:" + (_sceneResources.PedPoolId ?? string.Empty)
                + ",Vehicle:" + (_sceneResources.VehiclePoolId ?? string.Empty)
                + ",Weapon:" + (_sceneResources.WeaponPoolId ?? string.Empty)
                + "; Peds=" + DescribeResources(selectedPeds)
                + "; Vehicles=" + DescribeResources(selectedVehicles)
                + "; Weapons=" + DescribeResources(selectedWeapons));
            return "Crime Activity investigation started. Scene assets are preparing near "
                + _activeIntel.Location.Name + ".";
        }

        internal string IgnoreCurrentActivity(Ped player)
        {
            if (Active)
            {
                FinishActivity(player, false,
                    "Crime Activity investigation closed by the officer.", false);
                return _lastOutcome;
            }

            if (_availableIntel == null)
                return "No Crime Activity intelligence is currently available.";

            LSPDCrimeActivityLocationDefinition location = _availableIntel.Location;
            CloseActiveAudio();
            MarkLocationCooldown(location, DateTime.UtcNow);
            _lastOutcome = "Crime Activity intelligence ignored at " + location.Name + ".";
            LogRuntime("POLICE_CRIME_ACTIVITY_IGNORED",
                "Activity=" + _availableIntel.Id + "; Location=" + location.Id);
            _availableIntel = null;
            _nearbyTipReported = false;
            _redeploymentAvailableAt = DateTime.UtcNow.AddSeconds(
                _settings.RedeploymentCooldownSeconds);
            _nextDiscoveryAt = DateTime.UtcNow.AddSeconds(_settings.RedeploymentCooldownSeconds);
            return _lastOutcome;
        }

        /// <summary>
        /// Records the request while Backup travels. The Crime Activity still
        /// owns every suspect and its scene; Backup only receives its target.
        /// </summary>
        internal string RequestBackupSupport()
        {
            if (!Active)
                return "Backup requires an active Crime Activity investigation.";
            _backupRequested = true;
            if (_state == LSPDCrimeActivityState.Observing
                || _state == LSPDCrimeActivityState.Investigating)
                SetState(LSPDCrimeActivityState.BackupRequested);
            return "Backup is assigned to the Crime Activity and will stage before approaching the scene.";
        }

        /// <summary>
        /// Called only after the separate Backup owner has arrived. It turns a
        /// quiet meeting into an actual criminal reaction without assigning
        /// Backup ownership to this scene.
        /// </summary>
        internal void BeginBackupIntervention(Ped player)
        {
            if (!CanConfront)
                return;
            _backupRequested = true;
            EscalateScene(player, true);
        }

        internal string Confront(Ped player)
        {
            if (!Active)
                return "Begin a Crime Activity investigation before confronting the scene.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";
            if (_state == LSPDCrimeActivityState.PreparingScene)
                return "Crime Activity scene assets are still preparing. Wait until the suspects are established before confronting the scene.";
            if (_state == LSPDCrimeActivityState.Resolving)
                return "Crime Activity is already resolving. Continue handling the criminal scene.";
            if (!CanConfront)
                return "Crime Activity suspects are not ready to confront yet.";
            EscalateScene(player, false);
            return "Crime Activity suspects reacted to the Police presence. Resolve the scene or request Backup.";
        }

        internal void Reset()
        {
            CloseActiveAudio();
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
                DeleteDeferred(item);
            _deferredCleanup.Clear();
            DeleteOwnedSceneImmediately();
            CleanupSceneBlip();
            ReleaseSceneModelRequests();
            _availableIntel = null;
            _activeIntel = null;
            _sceneResources = null;
            _activityOperationId = string.Empty;
            _audioScope = string.Empty;
            _nextDiscoveryAt = DateTime.MinValue;
            _redeploymentAvailableAt = DateTime.MinValue;
            _scenePreparationDeadline = DateTime.MinValue;
            _sceneActivatedAt = DateTime.MinValue;
            _lastAmbientTaskAt = DateTime.MinValue;
            _nearbyTipReported = false;
            _sceneEscalated = false;
            _backupRequested = false;
            _lastOutcome = string.Empty;
            _state = LSPDCrimeActivityState.None;
            _events.ResetRecentSelection();
            ClearObservations();
        }

        private void ProcessAvailableIntel(Ped player, DateTime now)
        {
            if (_availableIntel == null)
                return;

            _availableIntel.DistanceMeters = player.Position.DistanceTo(_availableIntel.Position);
            _availableIntel.ObservedAt = now;
            if (_availableIntel.DistanceMeters
                > _settings.MaximumDeploymentDistance + 75f)
            {
                _lastOutcome = "Crime Activity lead expired after the officer left the area.";
                LogRuntime("POLICE_CRIME_ACTIVITY_LEAD_EXPIRED",
                    "Activity=" + _availableIntel.Id + "; Location=" + _availableIntel.Location.Id);
                CloseActiveAudio();
                _availableIntel = null;
                _nearbyTipReported = false;
                _redeploymentAvailableAt = now.AddSeconds(
                    _settings.RedeploymentCooldownSeconds);
                _nextDiscoveryAt = now.AddSeconds(_settings.RedeploymentCooldownSeconds);
                return;
            }

            if (_settings.NearbyTipNotifications
                && !_nearbyTipReported
                && _availableIntel.DistanceMeters <= _settings.NearbyTipDistance)
            {
                _nearbyTipReported = true;
                Notify("~b~CRIME ACTIVITY~s~\nThere is criminal activity ahead near "
                    + _availableIntel.Location.Name + ". Be cautious and investigate if you choose.");
                ReportAvailableTip();
                LogRuntime("POLICE_CRIME_ACTIVITY_NEARBY_TIP",
                    "Activity=" + _availableIntel.Id + "; Location=" + _availableIntel.Location.Id
                    + "; Distance=" + _availableIntel.DistanceMeters.ToString("0", CultureInfo.InvariantCulture));
            }
        }

        private void ProcessActiveScene(Ped player, DateTime now)
        {
            if (_activeIntel == null)
            {
                FinishActivity(player, false, "Crime Activity state lost its selected intelligence.", false);
                return;
            }

            if (_state == LSPDCrimeActivityState.PreparingScene)
            {
                ProcessScenePreparation(player, now);
                return;
            }

            if (_sceneCriminals.Count == 0)
            {
                FinishActivity(player, false, "Crime Activity scene did not retain any owned criminal actors.", false);
                return;
            }

            // A player, Backup unit, or unrelated world event can remove the
            // final owned actor before an explicit Confront action reaches the
            // next tick. Treat that as a real terminal result instead of
            // leaving a quiet scene alive forever with only dead handles.
            List<Ped> living = _sceneCriminals.Where(ped => ped != null
                && ped.Exists() && !ped.IsDead).ToList();
            if (living.Count == 0)
            {
                FinishActivity(player, true,
                    "Crime Activity resolved. The owned criminal scene was neutralized.", true);
                return;
            }

            float distance = player.Position.DistanceTo(_activeIntel.Position);
            if (!_nearbyTipReported && distance <= _settings.NearbyTipDistance)
            {
                _nearbyTipReported = true;
                Notify("~b~CRIME ACTIVITY~s~\nThere is Crime Activity ahead. Observe the area or request Backup.");
                ReportAvailableTip();
            }

            if (!_sceneEscalated)
            {
                MaintainAmbientScene(now);
                if (player.IsShooting)
                    EscalateScene(player, false);
                else if (_state == LSPDCrimeActivityState.Observing
                    && distance <= Math.Max(12f, _settings.NearbyTipDistance * 0.4f))
                    SetState(LSPDCrimeActivityState.Investigating);

                if (distance > _settings.AbandonDistance
                    && _sceneActivatedAt != DateTime.MinValue
                    && now >= _sceneActivatedAt.AddSeconds(20))
                {
                    FinishActivity(player, false,
                        "Crime Activity closed after the officer left the scene before confronting it.", false);
                }
                return;
            }

            bool allEscaped = living.All(ped =>
                ped.Position.DistanceTo(_activeIntel.Position) > _settings.AbandonDistance);
            if (allEscaped)
            {
                FinishActivity(player, true,
                    "Crime Activity resolved. The criminal group dispersed from the area.", true);
            }
        }

        private void ProcessScenePreparation(Ped player, DateTime now)
        {
            string reason;
            bool ready = AreSceneModelsReady(out reason);
            if (!ready)
            {
                if (!string.IsNullOrWhiteSpace(reason)
                    || now >= _scenePreparationDeadline)
                {
                    FinishActivity(player, false,
                        string.IsNullOrWhiteSpace(reason)
                            ? "Crime Activity scene assets did not finish streaming in time."
                            : reason,
                        false);
                }
                return;
            }

            bool created;
            try
            {
                created = CreateScene(player);
            }
            finally
            {
                ReleaseSceneModelRequests();
            }
            if (!created)
            {
                FinishActivity(player, false,
                    "Crime Activity scene could not be created safely.", false);
                return;
            }

            _sceneActivatedAt = now;
            _lastAmbientTaskAt = DateTime.MinValue;
            SetState(LSPDCrimeActivityState.Observing);
            CreateSceneBlip(_activeIntel.Position, _activeIntel.Definition.Name);
            Notify("~b~CRIME ACTIVITY~s~\nSuspicious activity is established near "
                + _activeIntel.Location.Name + ". Observe, confront, or request Backup.");
            ReportSceneStarted();
            LogRuntime("POLICE_CRIME_ACTIVITY_SCENE_CREATED",
                "Activity=" + _activeIntel.Id + "; Location=" + _activeIntel.Location.Id
                + "; Criminals=" + _sceneCriminals.Count + "; Vehicles=" + _sceneVehicles.Count);
        }

        private void SetAvailableIntel(LSPDActivityIntel intel, string reason)
        {
            if (intel == null)
                return;
            CloseActiveAudio();
            _availableIntel = intel;
            _nearbyTipReported = false;
            _activityOperationId = Guid.NewGuid().ToString("N");
            _lastOutcome = string.Empty;
            int existingIndex = _nearbyIntel.FindIndex(item => item != null
                && string.Equals(item.Id, intel.Id, StringComparison.OrdinalIgnoreCase)
                && item.Location != null && intel.Location != null
                && string.Equals(item.Location.Id, intel.Location.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                _nearbyIntel[existingIndex] = intel;
            else
                _nearbyIntel.Add(intel);
            _nearbyIntel.Sort((left, right) =>
                left.DistanceMeters.CompareTo(right.DistanceMeters));
            LogRuntime("POLICE_CRIME_ACTIVITY_AVAILABLE",
                "Activity=" + intel.Id + "; Location=" + intel.Location.Id
                + "; Reason=" + (reason ?? string.Empty));
        }

        private Vector3 FindRequestedScenePosition(Ped player, Vector3 authoredPosition)
        {
            Vector3 origin = player.Position;
            Vector3 delta = authoredPosition - origin;
            float planarLength = (float)Math.Sqrt(
                delta.X * delta.X + delta.Y * delta.Y);
            float directionX;
            float directionY;
            if (planarLength > 1f)
            {
                directionX = delta.X / planarLength;
                directionY = delta.Y / planarLength;
            }
            else
            {
                double heading = player.Heading * Math.PI / 180.0;
                directionX = (float)-Math.Sin(heading);
                directionY = (float)Math.Cos(heading);
            }

            float maximum = Math.Max(60f, _settings.MaximumDeploymentDistance - 20f);
            float minimum = Math.Min(maximum,
                Math.Max(45f, _settings.SceneActivationDistance + 20f));
            float targetDistance = Math.Min(maximum,
                Math.Max(minimum, _settings.MaximumDeploymentDistance * 0.8f));
            Vector3 fallback = origin + new Vector3(
                directionX * targetDistance,
                directionY * targetDistance,
                0f);

            // Try the authored direction first, then small side offsets so a
            // nearby street can be found without spawning the scene on top of
            // the officer or outside the configured deployment distance.
            float[] angleOffsets = { 0f, 35f, -35f, 70f, -70f };
            foreach (float angleOffset in angleOffsets)
            {
                double radians = angleOffset * Math.PI / 180.0;
                float rotatedX = directionX * (float)Math.Cos(radians)
                    - directionY * (float)Math.Sin(radians);
                float rotatedY = directionX * (float)Math.Sin(radians)
                    + directionY * (float)Math.Cos(radians);
                Vector3 intended = origin + new Vector3(
                    rotatedX * targetDistance,
                    rotatedY * targetDistance,
                    0f);
                try
                {
                    Vector3 street = World.GetNextPositionOnStreet(intended);
                    float distance = street.DistanceTo(origin);
                    if (street != Vector3.Zero && distance >= minimum
                        && distance <= _settings.MaximumDeploymentDistance)
                        return street;
                }
                catch { }
            }
            return fallback;
        }

        private bool EnsureAvailableIntel(string activityId)
        {
            if (_availableIntel != null
                && (string.IsNullOrWhiteSpace(activityId)
                    || string.Equals(_availableIntel.Id, activityId,
                        StringComparison.OrdinalIgnoreCase)))
                return true;

            LSPDActivityIntel observed;
            if (!TryGetIntel(activityId, out observed))
                return false;
            SetAvailableIntel(observed, "Officer selected an authored Crime Activity tip from the Police UI.");
            return true;
        }

        private void RequestSceneModels()
        {
            string ignored;
            AreSceneModelsReady(out ignored);
        }

        private bool AreSceneModelsReady(out string failure)
        {
            failure = string.Empty;
            if (_sceneResources == null || _sceneResources.PedModels == null
                || _sceneResources.PedModels.Count == 0)
            {
                failure = "Crime Activity XML did not provide any criminal ped models.";
                return false;
            }

            var usablePeds = new List<LSPDCrimeResourceDefinition>();
            var loadedPeds = new List<LSPDCrimeResourceDefinition>();
            bool waitingForPed = false;
            foreach (LSPDCrimeResourceDefinition resource in _sceneResources.PedModels)
            {
                if (resource == null || string.IsNullOrWhiteSpace(resource.Value))
                    continue;
                Model model = new Model(resource.Value);
                try
                {
                    if (!IsUsableModel(model, false))
                    {
                        ReleaseModel(model);
                        continue;
                    }
                    usablePeds.Add(resource);
                    if (!model.IsLoaded)
                    {
                        model.Request();
                        waitingForPed = true;
                    }
                    else
                        loadedPeds.Add(resource);
                }
                catch { waitingForPed = true; }
            }

            if (usablePeds.Count == 0)
            {
                failure = "Crime Activity criminal ped models are unavailable in this GTA build.";
                return false;
            }
            _sceneResources.PedModels = usablePeds;

            // A valid candidate that never becomes resident is an asset
            // failure, not a reason to hold the activity forever. At the
            // bounded deadline retain only loaded XML candidates and let the
            // scene use those alternatives. If none loaded, report the
            // actual streaming failure before any actor is created.
            if (waitingForPed && DateTime.UtcNow >= _scenePreparationDeadline)
            {
                if (loadedPeds.Count == 0)
                {
                    failure = "Crime Activity XML criminal ped assets did not produce a loaded candidate before timeout.";
                    LogDebug("POLICE_CRIME_ACTIVITY_ASSET_SELECTION_FAILED",
                        "Type=ped; Reason=MODEL_NOT_LOADED_BEFORE_TIMEOUT; Candidates="
                        + DescribeResources(usablePeds));
                    return false;
                }

                foreach (LSPDCrimeResourceDefinition resource in usablePeds)
                {
                    if (!loadedPeds.Contains(resource))
                    {
                        ReleaseModel(new Model(resource.Value));
                        LogDebug("POLICE_CRIME_ACTIVITY_ASSET_RETRY",
                            "Type=ped; Asset=" + (resource.Id ?? string.Empty)
                            + "; Value=" + resource.Value
                            + "; Reason=MODEL_NOT_LOADED_BEFORE_TIMEOUT; Replacement=LOADED_XML_CANDIDATE");
                    }
                }
                _sceneResources.PedModels = loadedPeds;
                waitingForPed = false;
            }

            // A vehicle is a scene enhancement rather than a reason to throw
            // away a valid criminal meeting. Request configured vehicles, but
            // never make their streaming state block a valid XML ped scene.
            if (_sceneResources.VehicleModels != null)
            {
                var usableVehicles = new List<LSPDCrimeResourceDefinition>();
                foreach (LSPDCrimeResourceDefinition vehicleResource
                    in _sceneResources.VehicleModels)
                {
                    if (vehicleResource == null)
                        continue;
                    Model vehicle = new Model(vehicleResource.Value);
                    try
                    {
                        if (IsUsableModel(vehicle, true) && !vehicle.IsLoaded)
                        {
                            vehicle.Request();
                            usableVehicles.Add(vehicleResource);
                        }
                        else if (IsUsableModel(vehicle, true))
                            usableVehicles.Add(vehicleResource);
                        else
                            ReleaseModel(vehicle);
                    }
                    catch { }
                }
                _sceneResources.VehicleModels = usableVehicles;
            }
            return !waitingForPed;
        }

        private List<LSPDCrimeResourceDefinition> SelectUsableModels(
            IReadOnlyList<LSPDCrimeResourceDefinition> source,
            int maximum,
            bool vehicle)
        {
            var usable = new List<LSPDCrimeResourceDefinition>();
            if (source != null)
            {
                foreach (LSPDCrimeResourceDefinition resource in source)
                {
                    if (resource == null || string.IsNullOrWhiteSpace(resource.Value))
                        continue;
                    Model model = new Model(resource.Value);
                    if (IsUsableModel(model, vehicle))
                        usable.Add(resource);
                    else
                        LogDebug(
                            "POLICE_CRIME_ACTIVITY_ASSET_REJECTED",
                            "Type=" + (vehicle ? "vehicle" : "ped")
                            + "; Asset=" + (resource.Id ?? string.Empty)
                            + "; Value=" + resource.Value
                            + "; Reason=MODEL_INVALID_OR_UNAVAILABLE");
                }
            }
            return SelectResources(usable, maximum);
        }

        private List<LSPDCrimeResourceDefinition> SelectUsableWeapons(
            IReadOnlyList<LSPDCrimeResourceDefinition> source,
            int maximum)
        {
            var usable = new List<LSPDCrimeResourceDefinition>();
            if (source != null)
            {
                foreach (LSPDCrimeResourceDefinition resource in source)
                {
                    if (resource == null || string.IsNullOrWhiteSpace(resource.Value))
                        continue;
                    if (IsUsableWeapon(resource.Value))
                        usable.Add(resource);
                    else
                        LogDebug(
                            "POLICE_CRIME_ACTIVITY_ASSET_REJECTED",
                            "Type=weapon; Asset=" + (resource.Id ?? string.Empty)
                            + "; Value=" + resource.Value
                            + "; Reason=WEAPON_INVALID_OR_UNAVAILABLE");
                }
            }
            return SelectResources(usable, maximum);
        }

        private static bool IsUsableWeapon(string weaponName)
        {
            if (string.IsNullOrWhiteSpace(weaponName))
                return false;
            try
            {
                int hash = ResolveWeaponHash(weaponName);
                WeaponAsset asset = new WeaponAsset(hash);
                return asset.IsValid && asset.IsValidAsWeaponHash;
            }
            catch
            {
                return false;
            }
        }

        private void ReleaseSceneModelRequests()
        {
            if (_sceneResources == null)
                return;
            ReleaseSceneModelRequests(_sceneResources.PedModels);
            ReleaseSceneModelRequests(_sceneResources.VehicleModels);
        }

        private static void ReleaseSceneModelRequests(
            IReadOnlyList<LSPDCrimeResourceDefinition> resources)
        {
            if (resources == null)
                return;
            foreach (LSPDCrimeResourceDefinition resource in resources)
            {
                if (resource == null || string.IsNullOrWhiteSpace(resource.Value))
                    continue;
                ReleaseModel(new Model(resource.Value));
            }
        }

        private bool CreateScene(Ped player)
        {
            if (_activeIntel == null || _sceneResources == null)
                return false;
            try
            {
                Vector3 anchor = _activeIntel.Position;
                CreateSceneVehicle(anchor, player == null ? 0f : player.Heading);
                int desired = Math.Max(2, _settings.ParticipantCount);
                List<LSPDCrimeResourceDefinition> pedResources = SelectResources(
                    _sceneResources.PedModels, desired);
                List<LSPDCrimeResourceDefinition> weaponResources = SelectResources(
                    _sceneResources.Weapons, desired);
                if (pedResources.Count == 0)
                    return false;
                for (int index = 0; index < desired; index++)
                {
                    LSPDCrimeResourceDefinition resource = pedResources[
                        index % pedResources.Count];
                    Model model = new Model(resource.Value);
                    try
                    {
                        if (!IsUsableModel(model, false) || !model.IsLoaded)
                            continue;
                        Ped criminal = World.CreatePed(model, ParticipantPosition(anchor, index, desired));
                        if (criminal == null || !criminal.Exists())
                            continue;
                        criminal.IsPersistent = true;
                        criminal.BlockPermanentEvents = true;
                        try { Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, criminal, true); }
                        catch { }
                        ApplySceneWeapon(criminal, index, weaponResources);
                        _sceneCriminals.Add(criminal);
                    }
                    finally { ReleaseModel(model); }
                }

                if (_sceneCriminals.Count < 2)
                {
                    // A partial scene is not an activity. Remove only the
                    // actors this owner created rather than leaving one
                    // stranded criminal or scene vehicle after a model failure.
                    DeleteOwnedSceneImmediately();
                    return false;
                }

                MaintainAmbientScene(DateTime.MinValue);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CRIME_ACTIVITY_SCENE_CREATE_FAILED", ex);
                DeleteOwnedSceneImmediately();
                return false;
            }
        }

        private void CreateSceneVehicle(Vector3 anchor, float heading)
        {
            if (_sceneResources == null || _sceneResources.VehicleModels == null)
                return;
            List<LSPDCrimeResourceDefinition> candidates = SelectResources(
                _sceneResources.VehicleModels, 1);
            LSPDCrimeResourceDefinition resource = candidates.FirstOrDefault();
            if (resource == null)
                return;
            Model model = new Model(resource.Value);
            try
            {
                if (!IsUsableModel(model, true) || !model.IsLoaded)
                    return;
                Vector3 position = anchor + new Vector3(8f, -4f, 0f);
                try { position = World.GetNextPositionOnStreet(position); } catch { }
                Vehicle vehicle = World.CreateVehicle(model, position, heading);
                if (vehicle == null || !vehicle.Exists())
                    return;
                vehicle.IsPersistent = true;
                vehicle.PlaceOnGround();
                _sceneVehicles.Add(vehicle);
            }
            catch (Exception ex) { LogException("POLICE_CRIME_ACTIVITY_VEHICLE_CREATE_FAILED", ex); }
            finally { ReleaseModel(model); }
        }

        private static Vector3 ParticipantPosition(Vector3 anchor, int index, int count)
        {
            float angle = (float)(index * Math.PI * 2.0 / Math.Max(2, count));
            float radius = index % 2 == 0 ? 3.5f : 5.2f;
            return anchor + new Vector3(
                (float)Math.Cos(angle) * radius,
                (float)Math.Sin(angle) * radius,
                0f);
        }

        private void ApplySceneWeapon(
            Ped criminal,
            int index,
            IReadOnlyList<LSPDCrimeResourceDefinition> selectedWeapons = null)
        {
            if (criminal == null || !criminal.Exists() || _sceneResources == null
                || _sceneResources.Weapons == null || _sceneResources.Weapons.Count == 0)
                return;
            IReadOnlyList<LSPDCrimeResourceDefinition> source = selectedWeapons == null
                || selectedWeapons.Count == 0 ? _sceneResources.Weapons : selectedWeapons;
            LSPDCrimeResourceDefinition weapon = source[index % source.Count];
            try
            {
                int hash = unchecked((int)StringHash.AtStringHash(weapon.Value, 0));
                Function.Call(Hash.GIVE_WEAPON_TO_PED, criminal, hash, 90, false, false);
            }
            catch (Exception ex) { LogException("POLICE_CRIME_ACTIVITY_WEAPON_FAILED", ex); }
        }

        private List<LSPDCrimeResourceDefinition> SelectResources(
            IReadOnlyList<LSPDCrimeResourceDefinition> source,
            int maximum)
        {
            var candidates = source == null
                ? new List<LSPDCrimeResourceDefinition>()
                : source.Where(value => value != null
                    && !string.IsNullOrWhiteSpace(value.Value)).ToList();
            if (candidates.Count > 1)
            {
                List<LSPDCrimeResourceDefinition> varied = candidates
                    .Where(value => !_recentSceneAssetValueSet.Contains(value.Value))
                    .ToList();
                if (varied.Count > 0)
                    candidates = varied;
            }
            var selected = new List<LSPDCrimeResourceDefinition>();
            int target = Math.Min(Math.Max(1, maximum), candidates.Count);
            while (candidates.Count > 0 && selected.Count < target)
            {
                int index = _random.Next(candidates.Count);
                selected.Add(candidates[index]);
                RememberSceneAsset(candidates[index].Value);
                candidates.RemoveAt(index);
            }
            return selected;
        }

        private void RememberSceneAsset(string value)
        {
            if (string.IsNullOrWhiteSpace(value)
                || _recentSceneAssetValueSet.Contains(value))
                return;
            _recentSceneAssetValueSet.Add(value);
            _recentSceneAssetValues.Enqueue(value);
            while (_recentSceneAssetValues.Count > 18)
                _recentSceneAssetValueSet.Remove(_recentSceneAssetValues.Dequeue());
        }

        private void MaintainAmbientScene(DateTime now)
        {
            if (now != DateTime.MinValue
                && now < _lastAmbientTaskAt.AddMilliseconds(AmbientTaskRefreshMilliseconds))
                return;
            _lastAmbientTaskAt = now == DateTime.MinValue ? DateTime.UtcNow : now;
            List<Ped> participants = _sceneCriminals.Where(ped => ped != null
                && ped.Exists() && !ped.IsDead).ToList();
            for (int index = 0; index < participants.Count; index++)
            {
                Ped criminal = participants[index];
                try
                {
                    string scenario = SceneScenario(index);
                    Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, criminal,
                        scenario, 0, true);
                    Ped partner = participants.Count > 1
                        ? participants[(index + 1) % participants.Count] : null;
                    if (partner != null && partner.Exists())
                        criminal.Task.LookAt(partner, 3500);
                }
                catch { }
            }
        }

        private string SceneScenario(int index)
        {
            string category = _activeIntel == null || _activeIntel.Definition == null
                ? string.Empty : _activeIntel.Definition.Category ?? string.Empty;
            string stage = SceneStage(index);
            string role = _sceneResources == null || _sceneResources.PedModels == null
                || _sceneResources.PedModels.Count == 0
                ? string.Empty
                : _sceneResources.PedModels[index % _sceneResources.PedModels.Count].Role
                    ?? string.Empty;

            // Stage and role are authored in LSPDCrimeActivityEvent.xml. Use
            // only scenario names already exercised by the existing Police
            // project so each activity looks different without introducing a
            // guessed animation dictionary or unsupported native.
            if (Contains(stage, "recon") || Contains(stage, "planning")
                || Contains(stage, "observation") || Contains(stage, "equipment")
                || Contains(stage, "identity") || Contains(stage, "route"))
            {
                return index % 2 == 0 ? "WORLD_HUMAN_CLIPBOARD" : "WORLD_HUMAN_STAND_IMPATIENT";
            }
            if (Contains(stage, "cargo") || Contains(stage, "transfer")
                || Contains(stage, "delivery") || Contains(stage, "package")
                || Contains(stage, "storage") || Contains(stage, "cash")
                || Contains(stage, "vehicle"))
            {
                return Contains(role, "lookout") || Contains(role, "guard")
                    || Contains(role, "security")
                    ? "WORLD_HUMAN_STAND_IMPATIENT"
                    : "WORLD_HUMAN_CLIPBOARD";
            }
            if (Contains(role, "lookout") || Contains(role, "guard")
                || Contains(role, "security"))
                return "WORLD_HUMAN_STAND_IMPATIENT";
            if (Contains(role, "driver") || Contains(role, "courier")
                || Contains(role, "cargo") || Contains(role, "technical"))
                return "WORLD_HUMAN_CLIPBOARD";
            if (category.IndexOf("smuggling", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("vehicle", StringComparison.OrdinalIgnoreCase) >= 0)
                return index % 2 == 0 ? "WORLD_HUMAN_CLIPBOARD" : "WORLD_HUMAN_STAND_IMPATIENT";
            if (category.IndexOf("weapons", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("gang", StringComparison.OrdinalIgnoreCase) >= 0)
                return index % 2 == 0 ? "WORLD_HUMAN_SMOKING" : "WORLD_HUMAN_STAND_IMPATIENT";
            return index % 3 == 0 ? "WORLD_HUMAN_SMOKING" : "WORLD_HUMAN_STAND_IMPATIENT";
        }

        private string SceneStage(int index)
        {
            if (_activeIntel == null || _activeIntel.Definition == null
                || _activeIntel.Definition.StageIds == null
                || _activeIntel.Definition.StageIds.Count == 0)
                return string.Empty;
            return _activeIntel.Definition.StageIds[
                index % _activeIntel.Definition.StageIds.Count] ?? string.Empty;
        }

        private static bool Contains(string value, string fragment)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void EscalateScene(Ped player, bool backupIntervention)
        {
            if (_sceneEscalated)
                return;
            _sceneEscalated = true;
            SetState(LSPDCrimeActivityState.Resolving);
            List<Ped> participants = _sceneCriminals.Where(ped => ped != null
                && ped.Exists() && !ped.IsDead).ToList();
            bool highSeverity = _activeIntel != null && _activeIntel.Definition != null
                && (string.Equals(_activeIntel.Definition.Severity, "high", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_activeIntel.Definition.Severity, "critical", StringComparison.OrdinalIgnoreCase));
            for (int index = 0; index < participants.Count; index++)
            {
                Ped criminal = participants[index];
                try
                {
                    criminal.Task.ClearAll();
                    if (!highSeverity && index == 0)
                        criminal.Task.ReactAndFlee(player);
                    else if (index % 3 == 0)
                        criminal.Task.ReactAndFlee(player);
                    else if (player != null && player.Exists())
                        criminal.Task.Combat(player);
                }
                catch { }
            }
            string source = backupIntervention ? "Backup intervention" : "Officer confrontation";
            Notify("~b~CRIME ACTIVITY~s~\n" + source
                + " changed the scene. Resolve the criminal group.");
            ReportSceneEscalated(backupIntervention ? "backup" : "officer");
            LogRuntime("POLICE_CRIME_ACTIVITY_ESCALATED",
                "Activity=" + (_activeIntel == null ? string.Empty : _activeIntel.Id)
                + "; Source=" + source + "; Participants=" + participants.Count);
        }

        private void FinishActivity(Ped player, bool completed, string outcome, bool reportCompletion)
        {
            LSPDActivityIntel intel = _activeIntel ?? _availableIntel;
            if (reportCompletion && intel != null)
                ReportTerminal("lsimmersivelife.police.activity.contained", completed ? "contained" : "closed");
            else
                CloseActiveAudio();

            if (intel != null)
                MarkLocationCooldown(intel.Location, DateTime.UtcNow);
            QueueOwnedSceneForCleanup(DateTime.UtcNow);
            CleanupSceneBlip();
            ReleaseSceneModelRequests();
            _lastOutcome = outcome ?? (completed
                ? "Crime Activity resolved."
                : "Crime Activity closed.");
            _availableIntel = null;
            _activeIntel = null;
            _sceneResources = null;
            _scenePreparationDeadline = DateTime.MinValue;
            _sceneActivatedAt = DateTime.MinValue;
            _lastAmbientTaskAt = DateTime.MinValue;
            _nearbyTipReported = false;
            _sceneEscalated = false;
            _backupRequested = false;
            _activityOperationId = string.Empty;
            _audioScope = string.Empty;
            _redeploymentAvailableAt = DateTime.UtcNow.AddSeconds(
                _settings.RedeploymentCooldownSeconds);
            _nextDiscoveryAt = DateTime.UtcNow.AddSeconds(_settings.RedeploymentCooldownSeconds);
            _state = LSPDCrimeActivityState.None;
            LogRuntime(completed
                ? "POLICE_CRIME_ACTIVITY_COMPLETED"
                : "POLICE_CRIME_ACTIVITY_CLOSED", _lastOutcome);
            if (!string.IsNullOrWhiteSpace(_lastOutcome))
                Notify("~b~CRIME ACTIVITY~s~\n" + _lastOutcome);
        }

        private void MarkLocationCooldown(LSPDCrimeActivityLocationDefinition location, DateTime now)
        {
            if (location == null || string.IsNullOrWhiteSpace(location.Id))
                return;
            _locationCooldowns[location.Id] = now.AddSeconds(
                _settings.SameLocationCooldownSeconds);
        }

        private void TrimLocationCooldowns(DateTime now)
        {
            foreach (string id in _locationCooldowns.Where(value => value.Value <= now)
                .Select(value => value.Key).ToArray())
                _locationCooldowns.Remove(id);
        }

        private void QueueOwnedSceneForCleanup(DateTime now)
        {
            foreach (Ped criminal in _sceneCriminals.Where(ped => ped != null && ped.Exists()))
                _deferredCleanup.Add(new DeferredCleanup
                {
                    Ped = criminal,
                    Earliest = now.AddSeconds(_cleanupSettings.CompletedSceneGraceSeconds),
                    Expires = now.AddSeconds(_cleanupSettings.HardCleanupSeconds)
                });
            foreach (Vehicle vehicle in _sceneVehicles.Where(vehicle => vehicle != null && vehicle.Exists()))
                _deferredCleanup.Add(new DeferredCleanup
                {
                    Vehicle = vehicle,
                    Earliest = now.AddSeconds(_cleanupSettings.CompletedSceneGraceSeconds),
                    Expires = now.AddSeconds(_cleanupSettings.HardCleanupSeconds)
                });
            _sceneCriminals.Clear();
            _sceneVehicles.Clear();
        }

        private void ProcessDeferredCleanup(DateTime now, Ped player)
        {
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
            {
                Entity entity = item.Ped != null && item.Ped.Exists()
                    ? (Entity)item.Ped : item.Vehicle;
                if (entity == null || !entity.Exists())
                {
                    _deferredCleanup.Remove(item);
                    continue;
                }
                float distance = player == null || !player.Exists()
                    ? float.MaxValue : entity.Position.DistanceTo(player.Position);
                if (now >= item.Earliest
                    && (distance >= DeferredCleanupDistance || now >= item.Expires))
                {
                    DeleteDeferred(item);
                    _deferredCleanup.Remove(item);
                }
            }
        }

        private void DeleteOwnedSceneImmediately()
        {
            foreach (Ped criminal in _sceneCriminals.ToArray())
                try { if (criminal != null && criminal.Exists()) criminal.Delete(); } catch { }
            foreach (Vehicle vehicle in _sceneVehicles.ToArray())
                try { if (vehicle != null && vehicle.Exists()) vehicle.Delete(); } catch { }
            _sceneCriminals.Clear();
            _sceneVehicles.Clear();
        }

        private static void DeleteDeferred(DeferredCleanup item)
        {
            try { if (item.Ped != null && item.Ped.Exists()) item.Ped.Delete(); } catch { }
            try { if (item.Vehicle != null && item.Vehicle.Exists()) item.Vehicle.Delete(); } catch { }
        }

        private void CreateSceneBlip(Vector3 position, string title)
        {
            CleanupSceneBlip();
            try
            {
                _sceneBlip = World.CreateBlip(position);
                if (_sceneBlip != null && _sceneBlip.Exists())
                {
                    _sceneBlip.Name = "Crime Activity: " + title;
                    _sceneBlip.IsShortRange = false;
                }
            }
            catch (Exception ex) { LogException("POLICE_CRIME_ACTIVITY_BLIP_FAILED", ex); }
        }

        private void CleanupSceneBlip()
        {
            try { if (_sceneBlip != null && _sceneBlip.Exists()) _sceneBlip.Delete(); } catch { }
            _sceneBlip = null;
        }

        private void ReportAvailableTip()
        {
            if (_availableIntel == null && _activeIntel == null)
                return;
            LSPDActivityIntel intel = _activeIntel ?? _availableIntel;
            string eventId = string.IsNullOrWhiteSpace(intel.Definition.AudioEventId)
                ? "lsimmersivelife.police.activity.observed"
                : intel.Definition.AudioEventId;
            try
            {
                CloseActiveAudio();
                _audioScope = "crime-activity-tip-" + _activityOperationId;
                _audio.Report(eventId,
                    _audioScope,
                    "nearby-" + intel.Id + "-" + intel.Location.Id,
                    "activity");
            }
            catch { }
        }

        private void ReportSceneStarted()
        {
            if (_activeIntel == null)
                return;
            CloseActiveAudio();
            _audioScope = "crime-activity-scene-" + _activityOperationId;
            try
            {
                _audio.Report("lsimmersivelife.police.activity.observed", _audioScope,
                    "scene-started-" + _activeIntel.Id, "activity");
            }
            catch { }
        }

        private void ReportSceneEscalated(string source)
        {
            if (_activeIntel == null)
                return;
            CloseActiveAudio();
            _audioScope = "crime-activity-escalated-" + _activityOperationId;
            try
            {
                _audio.Report("lsimmersivelife.police.activity.escalating", _audioScope,
                    "escalated-" + source + "-" + _activeIntel.Id, "activity");
            }
            catch { }
        }

        private void ReportTerminal(string eventId, string stage)
        {
            if (string.IsNullOrWhiteSpace(_activityOperationId))
                return;
            CloseActiveAudio();
            _audioScope = "crime-activity-terminal-" + _activityOperationId;
            try { _audio.Report(eventId, _audioScope, stage + "-" + _activityOperationId, "activity"); }
            catch { }
        }

        private void CloseActiveAudio()
        {
            if (string.IsNullOrWhiteSpace(_audioScope))
                return;
            try { _audio.CancelScope(_audioScope); } catch { }
            _audioScope = string.Empty;
        }

        private void SetState(LSPDCrimeActivityState state)
        {
            if (_state == state)
                return;

            Ped actor = _sceneCriminals.FirstOrDefault();
            LSDeveloperRuntime.StateTransition(
                _log == null ? string.Empty : _log.SessionId,
                "CrimeActivity",
                "POLICE_CRIME_ACTIVITY_STATE",
                _state.ToString(),
                state.ToString(),
                actor == null ? 0 : actor.Handle,
                0,
                0,
                "Activity=" + (_activeIntel == null ? string.Empty : _activeIntel.Id));

            _state = state;
            LogRuntime("POLICE_CRIME_ACTIVITY_STATE", "State=" + state
                + "; Activity=" + (_activeIntel == null ? string.Empty : _activeIntel.Id));
        }

        private static bool IsUsableModel(Model model, bool vehicle)
        {
            try { return model.IsValid && model.IsInCdImage && (vehicle ? model.IsVehicle : model.IsPed); }
            catch { return false; }
        }

        private static int ResolveWeaponHash(string name)
        {
            uint numeric;
            if (!string.IsNullOrWhiteSpace(name)
                && name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(name.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out numeric))
                return unchecked((int)numeric);
            if (!string.IsNullOrWhiteSpace(name)
                && uint.TryParse(name, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out numeric))
                return unchecked((int)numeric);
            return unchecked((int)StringHash.AtStringHash(name ?? string.Empty, 0));
        }

        private static string DescribeResources(
            IEnumerable<LSPDCrimeResourceDefinition> resources)
        {
            if (resources == null)
                return "none";
            string result = string.Join(",", resources.Select(resource =>
                (resource == null ? string.Empty : (resource.Id ?? string.Empty))
                + "/" + (resource == null ? string.Empty : resource.Value)).ToArray());
            return string.IsNullOrWhiteSpace(result) ? "none" : result;
        }

        private static void ReleaseModel(Model model) { try { if (model.IsValid) model.MarkAsNoLongerNeeded(); } catch { } }
        private static void Notify(string message) { try { Notification.PostTicker(message, false, false); } catch { } }
        private void LogRuntime(string category, string message) { if (_log != null) _log.Runtime(category, message); }
        private void LogDebug(string category, string message) { if (_log != null) _log.Debug(category, message); }
        private void LogException(string category, Exception ex) { if (_log != null) _log.Exception(category, ex); }

        private static readonly Dictionary<string, Vector3> KnownCoordinates =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase)
            {
                { "los_santos.downtown.financial", new Vector3(150f, -1040f, 29f) },
                { "los_santos.vespucci.commercial", new Vector3(-1180f, -890f, 13f) },
                { "los_santos.south.industrial", new Vector3(280f, -1800f, 28f) },
                { "los_santos.strawberry.storage", new Vector3(300f, -1400f, 29f) },
                { "los_santos.la_mesa.garages", new Vector3(950f, -1000f, 39f) },
                { "los_santos.east_vinewood.hills", new Vector3(850f, 500f, 120f) },
                { "los_santos.vinewood.nightlife", new Vector3(300f, 200f, 104f) },
                { "los_santos.mirror_park.edge", new Vector3(1100f, -500f, 65f) },
                { "blaine.paleto.logging", new Vector3(-100f, 6200f, 31f) },
                { "blaine.sandy.trailer_yards", new Vector3(1800f, 3700f, 34f) },
                { "blaine.grand_senora.airstrip", new Vector3(1700f, 3300f, 42f) },
                { "blaine.chumash.coast", new Vector3(-3150f, 1100f, 20f) },
                { "blaine.zancudo.supply_route", new Vector3(-2200f, 3000f, 32f) },
                { "blaine.harmony.compound", new Vector3(600f, 2700f, 40f) },
                { "los_santos.banham.canyon", new Vector3(-2500f, 1000f, 180f) },
                { "los_santos.pacific_bluffs.estate", new Vector3(-3000f, 300f, 15f) }
            };
        internal IEnumerable<LSPDGroupCrimeActivityDefinition> FindByLocation(
            string locationId)
        {
            if (string.IsNullOrWhiteSpace(locationId))
                return Enumerable.Empty<LSPDGroupCrimeActivityDefinition>();

            return _activities.Values.Where(activity =>
                activity.LocationIds.Contains(
                    locationId,
                    StringComparer.OrdinalIgnoreCase));
        }

        internal static Dictionary<string, LSPDCrimeActivityLocationDefinition>
            ParseLocations(XElement parent)
        {
            if (parent == null)
                throw new InvalidDataException("Crime activity locations are missing.");

            var values = new Dictionary<string, LSPDCrimeActivityLocationDefinition>(
                StringComparer.OrdinalIgnoreCase);
            foreach (XElement node in parent.Elements("Location"))
            {
                LSPDCrimeActivityLocationDefinition definition =
                    LSPDCrimeActivityLocationDefinition.FromXml(node);
                AddUnique(values, definition.Id, definition);
            }

            if (values.Count == 0)
                throw new InvalidDataException("Crime activity locations are empty.");

            return values;
        }

        internal static Dictionary<string, LSPDCrimeActivityGroupDefinition>
            ParseGroups(XElement parent)
        {
            if (parent == null)
                throw new InvalidDataException("Criminal groups are missing.");

            var values = new Dictionary<string, LSPDCrimeActivityGroupDefinition>(
                StringComparer.OrdinalIgnoreCase);
            foreach (XElement node in parent.Elements("Group"))
            {
                LSPDCrimeActivityGroupDefinition definition =
                    LSPDCrimeActivityGroupDefinition.FromXml(node);
                AddUnique(values, definition.Id, definition);
            }

            if (values.Count == 0)
                throw new InvalidDataException("Criminal groups are empty.");

            return values;
        }

        internal static Dictionary<string, LSPDGroupCrimeActivityDefinition>
            ParseActivities(XElement parent)
        {
            if (parent == null)
                throw new InvalidDataException("Crime activities are missing.");

            var values = new Dictionary<string, LSPDGroupCrimeActivityDefinition>(
                StringComparer.OrdinalIgnoreCase);
            foreach (XElement node in parent.Elements("Activity"))
            {
                LSPDGroupCrimeActivityDefinition definition =
                    LSPDGroupCrimeActivityDefinition.FromXml(node);
                AddUnique(values, definition.Id, definition);
            }

            if (values.Count == 0)
                throw new InvalidDataException("Crime activities are empty.");

            return values;
        }

        internal static void ValidateReferences(
            IDictionary<string, LSPDGroupCrimeActivityDefinition> activities,
            IDictionary<string, LSPDCrimeActivityLocationDefinition> locations,
            IDictionary<string, LSPDCrimeActivityGroupDefinition> groups)
        {
            var resourceSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LSPDCrimeActivityGroupDefinition group in groups.Values)
            {
                AddResourceSet(resourceSets, group.PedSet.Id);
                AddResourceSet(resourceSets, group.WeaponSet.Id);
                AddResourceSet(resourceSets, group.VehicleSet.Id);
            }

            foreach (LSPDGroupCrimeActivityDefinition activity in activities.Values)
            {
                if (activity.AutoDispatch || !activity.IntelOnly)
                    throw new InvalidDataException(
                        "Crime activities must remain manual-review intelligence.");

                foreach (string locationId in activity.LocationIds)
                {
                    if (!locations.ContainsKey(locationId))
                        throw new InvalidDataException(
                            "Unknown crime activity location: " + locationId);
                }

                foreach (string groupId in activity.GroupIds)
                {
                    if (!groups.ContainsKey(groupId))
                        throw new InvalidDataException(
                            "Unknown criminal group: " + groupId);
                }

                if (!resourceSets.Contains(activity.PedSetId)
                    || !resourceSets.Contains(activity.WeaponSetId)
                    || !resourceSets.Contains(activity.VehicleSetId))
                {
                    throw new InvalidDataException(
                        "Crime activity resource set is not defined.");
                }
            }
        }

        private static void AddResourceSet(
            ISet<string> resourceSets,
            string resourceSetId)
        {
            if (!resourceSets.Add(resourceSetId))
                throw new InvalidDataException(
                    "Duplicate crime activity resource set: " + resourceSetId);
        }

        private static void AddUnique<T>(
            IDictionary<string, T> values,
            string id,
            T value)
        {
            if (values.ContainsKey(id))
                throw new InvalidDataException(
                    "Duplicate crime activity identifier: " + id);

            values.Add(id, value);
        }
    }

    /// <summary>
    /// Runtime lifecycle of the one owned Crime Activity scene. An available
    /// tip is deliberately not an active scene and does not block Dispatch.
    /// </summary>
    internal enum LSPDCrimeActivityState
    {
        None,
        PreparingScene,
        Observing,
        Investigating,
        BackupRequested,
        Resolving
    }

    internal sealed class LSPDCrimeActivityLocationDefinition
    {
        private LSPDCrimeActivityLocationDefinition()
        {
        }

        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal string District { get; private set; }
        internal string Terrain { get; private set; }
        internal string CoordinateKey { get; private set; }
        internal string ObservationPointKey { get; private set; }
        internal string ApproachKey { get; private set; }
        internal string EscapeRouteKey { get; private set; }
        internal int RadiusMeters { get; private set; }

        internal static LSPDCrimeActivityLocationDefinition FromXml(XElement node)
        {
            if (node == null)
                throw new ArgumentNullException("node");

            return new LSPDCrimeActivityLocationDefinition
            {
                Id = Required(node, "id"),
                Name = Required(node, "name"),
                District = Required(node, "district"),
                Terrain = Required(node, "terrain"),
                CoordinateKey = Required(node, "coordinateKey"),
                ObservationPointKey = Required(node, "observationPointKey"),
                ApproachKey = Required(node, "approachKey"),
                EscapeRouteKey = Required(node, "escapeRouteKey"),
                RadiusMeters = RequiredInt(node, "radiusMeters")
            };
        }

        private static string Required(XElement node, string attribute)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(
                    "Crime activity attribute is missing: " + attribute);

            return value.Trim();
        }

        private static int RequiredInt(XElement node, string attribute)
        {
            int value;
            if (!int.TryParse(
                    Required(node, attribute),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value)
                || value <= 0)
            {
                throw new InvalidDataException(
                    "Crime activity integer attribute is invalid: " + attribute);
            }

            return value;
        }
    }

    internal sealed class LSPDCrimeActivityGroupDefinition
    {
        private LSPDCrimeActivityGroupDefinition()
        {
        }

        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal string Type { get; private set; }
        internal string OperatingStyle { get; private set; }
        internal string PreferredActivity { get; private set; }
        internal LSPDCrimeResourceSetDefinition PedSet { get; private set; }
        internal LSPDCrimeResourceSetDefinition WeaponSet { get; private set; }
        internal LSPDCrimeResourceSetDefinition VehicleSet { get; private set; }

        internal static LSPDCrimeActivityGroupDefinition FromXml(XElement node)
        {
            if (node == null)
                throw new ArgumentNullException("node");

            return new LSPDCrimeActivityGroupDefinition
            {
                Id = Required(node, "id"),
                Name = Required(node, "name"),
                Type = Required(node, "type"),
                OperatingStyle = Required(node, "operatingStyle"),
                PreferredActivity = Required(node, "preferredActivity"),
                PedSet = LSPDCrimeResourceSetDefinition.FromXml(
                    node.Element("PedSet"), "Ped"),
                WeaponSet = LSPDCrimeResourceSetDefinition.FromXml(
                    node.Element("WeaponSet"), "Weapon"),
                VehicleSet = LSPDCrimeResourceSetDefinition.FromXml(
                    node.Element("VehicleSet"), "Vehicle")
            };
        }

        private static string Required(XElement node, string attribute)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(
                    "Criminal group attribute is missing: " + attribute);

            return value.Trim();
        }
    }

    internal sealed class LSPDCrimeResourceSetDefinition
    {
        private LSPDCrimeResourceSetDefinition()
        {
        }

        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal string ItemName { get; private set; }
        internal IReadOnlyList<LSPDCrimeResourceDefinition> Items { get; private set; }

        internal static LSPDCrimeResourceSetDefinition FromXml(
            XElement node,
            string itemName)
        {
            if (node == null)
                throw new InvalidDataException("Criminal group resource set is missing.");

            List<LSPDCrimeResourceDefinition> items = node.Elements(itemName)
                .Select(item => LSPDCrimeResourceDefinition.FromXml(item))
                .ToList();
            if (items.Count == 0)
                throw new InvalidDataException(
                    "Criminal group resource set is empty: " + itemName);

            return new LSPDCrimeResourceSetDefinition
            {
                Id = Required(node, "id"),
                Name = Required(node, "name"),
                ItemName = itemName,
                Items = items
            };
        }

        private static string Required(XElement node, string attribute)
        {
            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(
                    "Resource set attribute is missing: " + attribute);

            return value.Trim();
        }
    }

    internal sealed class LSPDCrimeResourceDefinition
    {
        private LSPDCrimeResourceDefinition()
        {
        }

        internal string Id { get; private set; }
        internal string Value { get; private set; }
        internal string Role { get; private set; }

        internal static LSPDCrimeResourceDefinition Create(string value, string role)
        {
            return Create(value, value, role);
        }

        internal static LSPDCrimeResourceDefinition Create(
            string id,
            string value,
            string role)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Crime catalog resource value is missing.");
            return new LSPDCrimeResourceDefinition
            {
                Id = string.IsNullOrWhiteSpace(id) ? value.Trim() : id.Trim(),
                Value = value.Trim(),
                Role = string.IsNullOrWhiteSpace(role) ? "catalog" : role.Trim()
            };
        }

        internal static LSPDCrimeResourceDefinition FromXml(XElement node)
        {
            if (node == null)
                throw new ArgumentNullException("node");

            string value = (string)node.Attribute("model")
                ?? (string)node.Attribute("name");
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(
                    "Crime resource model/name is missing.");

            string role = (string)node.Attribute("role");
            if (string.IsNullOrWhiteSpace(role))
                throw new InvalidDataException(
                    "Crime resource role is missing.");

            return new LSPDCrimeResourceDefinition
            {
                Id = value.Trim(),
                Value = value.Trim(),
                Role = role.Trim()
            };
        }
    }

    /// <summary>
    /// Authored data for one quiet intelligence activity. The flags are
    /// intentionally explicit so a future gameplay system cannot mistake this
    /// catalog for an automatic dispatch queue.
    /// </summary>
    internal sealed class LSPDGroupCrimeActivityDefinition
    {
        private LSPDGroupCrimeActivityDefinition()
        {
        }

        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal string Category { get; private set; }
        internal string DiscoveryMode { get; private set; }
        internal string Severity { get; private set; }
        internal bool AutoDispatch { get; private set; }
        internal bool RequiresPatrol { get; private set; }
        internal bool IntelOnly { get; private set; }
        internal string Confidence { get; private set; }
        internal string IntelSummary { get; private set; }
        internal string AudioEventId { get; private set; }
        internal string PedSetId { get; private set; }
        internal string WeaponSetId { get; private set; }
        internal string VehicleSetId { get; private set; }
        internal IReadOnlyList<string> LocationIds { get; private set; }
        internal IReadOnlyList<string> GroupIds { get; private set; }
        internal IReadOnlyList<string> StageIds { get; private set; }

        internal static LSPDGroupCrimeActivityDefinition FromXml(XElement node)
        {
            if (node == null)
                throw new ArgumentNullException("node");

            bool autoDispatch;
            bool requiresPatrol;
            bool intelOnly;

            if (!bool.TryParse(
                    (string)node.Attribute("autoDispatch"),
                    out autoDispatch)
                || !bool.TryParse(
                    (string)node.Attribute("requiresPatrol"),
                    out requiresPatrol)
                || !bool.TryParse(
                    (string)node.Attribute("intelOnly"),
                    out intelOnly))
            {
                throw new InvalidDataException(
                    "Crime activity flags are invalid.");
            }

            XElement intel = node.Element("Intel");
            XElement resources = node.Element("Resources");

            return new LSPDGroupCrimeActivityDefinition
            {
                Id = Required(node, "id"),
                Name = Required(node, "name"),
                Category = Required(node, "category"),
                DiscoveryMode = Required(node, "discovery"),
                Severity = Required(node, "severity"),
                AutoDispatch = autoDispatch,
                RequiresPatrol = requiresPatrol,
                IntelOnly = intelOnly,
                Confidence = Required(intel, "confidence"),
                IntelSummary = Required(intel, "summary"),
                AudioEventId = (string)intel.Attribute("audioEventId") ?? string.Empty,
                PedSetId = Required(resources, "pedSetId"),
                WeaponSetId = Required(resources, "weaponSetId"),
                VehicleSetId = Required(resources, "vehicleSetId"),
                LocationIds = References(node.Element("Locations"), "LocationRef"),
                GroupIds = References(node.Element("Groups"), "GroupRef"),
                StageIds = References(node.Element("Stages"), "Stage")
            };
        }

        private static string Required(XElement node, string attribute)
        {
            if (node == null)
                throw new InvalidDataException(
                    "Crime activity section is missing.");

            string value = (string)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(
                    "Crime activity attribute is missing: " + attribute);

            return value.Trim();
        }

        private static IReadOnlyList<string> References(
            XElement parent,
            string elementName)
        {
            if (parent == null)
                throw new InvalidDataException(
                    "Crime activity references are missing.");

            List<string> values = parent.Elements(elementName)
                .Select(node => Required(node, "id"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.Count == 0)
                throw new InvalidDataException(
                    "Crime activity references are empty.");

            return values;
        }
    }

    /// <summary>
    /// One observation result shown in the Police Crime Activity report. It is
    /// a read-only tip until the player deliberately follows it.
    /// </summary>
    internal sealed class LSPDActivityIntel
    {
        internal string Id { get; set; }
        internal LSPDGroupCrimeActivityDefinition Definition { get; set; }
        internal LSPDCrimeActivityLocationDefinition Location { get; set; }
        internal Vector3 Position { get; set; }
        internal float DistanceMeters { get; set; }
        internal DateTime ObservedAt { get; set; }
        internal string Territory { get; set; }
        internal string Summary { get; set; }
    }
}
