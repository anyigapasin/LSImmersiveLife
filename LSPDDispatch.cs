using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Owns the sparse, XML-defined universal Police callout loop. It offers
    /// one incident at a time during an explicitly started patrol, creates only
    /// the scene actors it owns, and advances the incident through arrival,
    /// investigation, compliance, arrest, custody handoff, and terminal cleanup.
    /// Crime Activity and Gang response remain separate owners.
    /// </summary>
    internal sealed class LSPDDispatch
    {
        // This owner creates at most one sparse, XML-defined universal dispatch
        // assignment. It is active only while Police Authority and Patrol are
        // active, never reads Crime Activity as a callout source, and never
        // takes ownership of entities it did not create.
        // Investigation is a local scene action, not a marker-precision test.
        // Keep the player within the authored area while allowing a practical
        // approach radius before the officer reaches the exact blip position.
        private const int SceneArrivalRadius = 100;
        private const float SceneApproachAudioRadius = 50f;
        internal const int InitialPatrolOfferDelaySeconds = 30;
        private const int ArrestRadius = 5;
        private const int SurrenderSettleSeconds = 2;
        // Keep GTA's authored arrest task alive through the visible handcuff
        // motion before Dispatch takes custody of the secured suspect.
        private const int PlayerHandcuffAnimationMilliseconds = 6500;
        private const int PlayerHandcuffMaximumAttempts = 2;
        private const int CompliantTaskRefreshMilliseconds = 7000;
        private const int SecuredPoseRefreshMilliseconds = 2500;
        private const int DefaultDeferredCleanupGraceSeconds = 8;
        private const int DefaultDeferredCleanupMaximumSeconds = 60;
        private const float DefaultDeferredCleanupDistance = 200f;
        private const int MaintenanceMilliseconds = 750;
        private const int ScenePreparationTimeoutSeconds = 15;
        private const int PairedSceneAnimationLoadTimeoutSeconds = 10;
        private const float PairedSceneAnimationApproachDistance = 38f;
        private const int MaxCriminalAssetSelectionAttempts = 12;
        private const int InteriorExitMaximumRecoveries = 3;
        private const int InteriorExitNavigationTimeoutSeconds = 60;
        private const int InteriorExitVehicleEntryTimeoutSeconds = 12;

        private readonly LSPDAudioDispatch _audio;
        private readonly LSImmersiveLog _log;
        private readonly string _catalogPath;
        private readonly string _locationCatalogPath;
        private readonly Random _random = new Random();
        private readonly LSPDControlBindings _controls;
        private readonly LSPoliceDispatchSettings _settings;
        private readonly LSPoliceCleanupSettings _cleanupSettings;
        private readonly LSUniversalUiSettings _uiSettings;
        private readonly List<LSPDDispatchEventDefinition> _definitions =
            new List<LSPDDispatchEventDefinition>();
        private readonly Dictionary<string, LSPDCriminalProfileDefinition> _criminalProfiles =
            new Dictionary<string, LSPDCriminalProfileDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly LSPDCriminalAssetCatalog _criminalAssets =
            new LSPDCriminalAssetCatalog();
        private readonly Queue<string> _recentCriminalAssetIds = new Queue<string>();
        private readonly HashSet<string> _recentCriminalAssetIdSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LSPDDispatchLocationDefinition> _locations =
            new Dictionary<string, LSPDDispatchLocationDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LSPDDispatchBranchDefinition> _branches =
            new Dictionary<string, LSPDDispatchBranchDefinition>(StringComparer.OrdinalIgnoreCase);
        private LSImmersiveLocationCatalog _locationCatalog;
        private LSWorldAmbientBehavior _ambientWorld;
        private readonly List<DeferredCleanup> _deferredCleanup =
            new List<DeferredCleanup>();
        private readonly Dictionary<string, DateTime> _definitionOfferedAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, DateTime> _lastCompliantTaskAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastSecuredPoseTaskAt =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DispatchSuspectInjuryState> _suspectInjuries =
            new Dictionary<int, DispatchSuspectInjuryState>();

        private LSPDDispatchEvent _incident;
        private Blip _sceneBlip;
        private Blip _suspectBlip;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private DateTime _nextOfferAt = DateTime.MinValue;
        private DateTime _nextMaintenance = DateTime.MinValue;
        private DateTime _playerHandcuffHoldUntil = DateTime.MinValue;
        private int _playerHandcuffAttempts;
        private DateTime _lastPatrolObserved = DateTime.MinValue;
        private DateTime _lastOfferAudioAt = DateTime.MinValue;
        private bool _patrolSessionStarted;
        private bool _dispatchProducerEnabled;
        private bool _acceptKeyDown;
        private bool _rejectKeyDown;
        private bool _investigateKeyDown;
        private bool _secureKeyDown;
        private bool _hadPreviousWaypoint;
        private Vector3 _previousWaypoint = Vector3.Zero;
        private readonly string _criminalProfilePath;

        private int DeferredCleanupGraceSeconds
        {
            get
            {
                return _cleanupSettings == null
                    ? DefaultDeferredCleanupGraceSeconds
                    : _cleanupSettings.CompletedSceneGraceSeconds;
            }
        }

        private int DeferredCleanupMaximumSeconds
        {
            get
            {
                return _cleanupSettings == null
                    ? DefaultDeferredCleanupMaximumSeconds
                    : _cleanupSettings.HardCleanupSeconds;
            }
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

        private sealed class DeferredCleanup
        {
            internal Ped Ped;
            internal Vehicle Vehicle;
            internal DateTime Earliest;
            internal DateTime Expires;
            internal bool PreserveUntilPlayerLeavesArea;
        }

        private enum DispatchHitZone
        {
            Unknown,
            Limb,
            Head,
            Chest,
            Stomach
        }

        private sealed class DispatchSuspectInjuryState
        {
            internal Ped Actor;
            internal int OriginalHealth;
            internal int OriginalMaximumHealth;
            internal int LastHealth;
            internal int DamageTaken;
            internal int HeadHits;
            internal int ChestHits;
            internal int StomachHits;
            internal bool OriginalNoCriticalHits;
            internal bool Wounded;
            internal bool Fatal;
            internal bool WritheTaskStarted;
            internal DateTime NextWritheAt = DateTime.MinValue;
            internal int NonfatalRecoveryAttempts;
            internal DateTime NextNonfatalRecoveryAt = DateTime.MinValue;
            internal bool NonfatalRecoveryExhaustionLogged;
            internal bool NonfatalResurrectionAttempted;
        }

        private const int NonfatalInjuryRecoveryMaximumAttempts = 8;

        internal LSPDDispatch(LSPDAudioDispatch audio)
            : this(audio, null, null, null, null, null)
        {
        }

        internal LSPDDispatch(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log)
            : this(audio, paths, log, null, null, null)
        {
        }

        internal LSPDDispatch(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log,
            LSPDControlBindings controls)
            : this(audio, paths, log, controls, null, null)
        {
        }

        internal LSPDDispatch(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log,
            LSPDControlBindings controls,
            LSPoliceDispatchSettings settings,
            LSPoliceCleanupSettings cleanupSettings)
            : this(audio, paths, log, controls, settings, cleanupSettings, null)
        {
        }

        internal LSPDDispatch(
            LSPDAudioDispatch audio,
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log,
            LSPDControlBindings controls,
            LSPoliceDispatchSettings settings,
            LSPoliceCleanupSettings cleanupSettings,
            LSUniversalUiSettings uiSettings)
        {
            _audio = audio ?? throw new ArgumentNullException("audio");
            _log = log;
            _controls = controls ?? LSPDControlBindings.Default();
            _settings = settings ?? LSPoliceDispatchSettings.Default();
            _cleanupSettings = cleanupSettings ?? LSPoliceCleanupSettings.Default();
            _uiSettings = uiSettings ?? LSUniversalUiSettings.Default();
            _catalogPath = paths == null ? null : paths.DispatchEventXmlPath;
            _locationCatalogPath = paths == null ? null : paths.LocationCatalogXmlPath;
            _criminalProfilePath = paths == null ? null : paths.CriminalProfileXmlPath;
            ReloadDefinitions();
        }

        internal LSImmersiveLocationCatalog LocationCatalog
        {
            get { return _locationCatalog; }
        }

        internal void AttachAmbientWorld(LSWorldAmbientBehavior ambientWorld)
        {
            _ambientWorld = ambientWorld;
            if (_ambientWorld != null)
                _ambientWorld.UpdateLocationCatalog(_locationCatalog);
        }

        internal bool HasOffer
        {
            get { return _incident != null && _incident.State == LSPDDispatchState.Offered; }
        }

        internal bool HasIncident
        {
            get { return _incident != null; }
        }

        internal LSPDDispatchState State
        {
            get { return _incident == null ? LSPDDispatchState.None : _incident.State; }
        }

        internal LSPDDispatchEvent Current
        {
            get { return _incident; }
        }

        internal Ped CurrentSuspect
        {
            get { return _incident == null ? null : _incident.Suspect; }
        }

        /// <summary>
        /// Read-only runtime view used by Backup and Convoy coordination. Each
        /// ped in this collection is a real Dispatch-owned criminal, unlike
        /// witnesses or activity-only scene participants.
        /// </summary>
        internal IEnumerable<Ped> CurrentSuspects
        {
            get
            {
                return _incident == null || _incident.Suspects == null
                    ? Enumerable.Empty<Ped>()
                    : _incident.Suspects.Where(ped => ped != null && ped.Exists());
            }
        }

        internal IEnumerable<Ped> CurrentArrestedSuspects
        {
            get
            {
                return _incident == null || _incident.ArrestedSuspects == null
                    ? Enumerable.Empty<Ped>()
                    : _incident.ArrestedSuspects.Where(ped => ped != null && ped.Exists() && !ped.IsDead);
            }
        }

        internal bool CanReleaseCurrentSuspect
        {
            get
            {
                Ped suspect = CurrentSuspect;
                return _incident != null
                    && _incident.OwnedByDispatch
                    && !_incident.HasConvoyCustodyHandoff
                    && !_incident.PlayerHandcuffInProgress
                    && suspect != null && suspect.Exists() && !suspect.IsDead
                    && !suspect.IsInVehicle()
                    && IsArrested(_incident, suspect);
            }
        }

        internal int ActiveSuspectCount
        {
            get { return CurrentSuspects.Count(ped => !ped.IsDead); }
        }

        internal bool HasSuspectGroup
        {
            get { return ActiveSuspectCount > 1; }
        }

        internal string CurrentTitle
        {
            get { return _incident == null ? string.Empty : _incident.Title; }
        }

        internal string CurrentBriefing
        {
            get { return _incident == null ? string.Empty : _incident.Briefing; }
        }

        internal string CurrentStatus
        {
            get
            {
                if (_incident == null)
                    return "No active dispatch.";
                return _incident.Title + " | " + _incident.State;
            }
        }

        internal int EventDefinitionCount { get { return _definitions.Count; } }
        internal bool IsEnabled { get { return _settings.Enabled; } }
        internal int CriminalProfileCount { get { return _criminalProfiles.Count; } }
        internal LSPDCriminalAssetCatalog CriminalAssets { get { return _criminalAssets; } }
        internal int CriminalPedAssetCount { get { return _criminalAssets.PedCount; } }
        internal int CriminalVehicleAssetCount { get { return _criminalAssets.VehicleCount; } }
        internal int CriminalWeaponAssetCount { get { return _criminalAssets.WeaponCount; } }
        internal int AuthoredLocationCount { get { return _locations.Count; } }

        internal bool HasConvoyCustodyHandoff
        {
            get { return _incident != null && _incident.HasConvoyCustodyHandoff; }
        }

        /// <summary>
        /// Supplies profile data for the explicit Convoy Request activity. This
        /// is deliberately data-only: Convoy owns every spawned prisoner and
        /// route threat, while Dispatch remains free of a second active case.
        /// </summary>
        internal bool TryChooseConvoyRequestProfiles(
            out LSPDCriminalProfileDefinition prisonerProfile,
            out LSPDCriminalProfileDefinition routeThreatProfile)
        {
            prisonerProfile = null;
            routeThreatProfile = null;
            List<LSPDCriminalProfileDefinition> usable = _criminalProfiles.Values
                .Where(profile => profile != null && profile.IsEligibleFor("convoy")
                    && !string.IsNullOrWhiteSpace(profile.ModelName))
                .ToList();
            if (usable.Count == 0)
                return false;

            List<LSPDCriminalProfileDefinition> custodyCandidates = usable
                .Where(profile => !profile.Armed).ToList();
            if (custodyCandidates.Count == 0)
                custodyCandidates = usable;
            List<LSPDCriminalProfileDefinition> threatCandidates = usable
                .Where(profile => profile.Armed && !string.IsNullOrWhiteSpace(profile.WeaponName))
                .ToList();

            string prisonerFailure;
            if (!TryResolveUsableCriminalProfile(
                custodyCandidates,
                "convoy",
                false,
                out prisonerProfile,
                out prisonerFailure))
            {
                LogDebug(
                    "POLICE_CONVOY_CRIMINAL_ASSET_SELECTION_FAILED",
                    "Role=prisoner; Reason=" + prisonerFailure);
                return false;
            }

            string threatFailure;
            if (threatCandidates.Count > 0
                && !TryResolveUsableCriminalProfile(
                    threatCandidates,
                    "convoy",
                    true,
                    out routeThreatProfile,
                    out threatFailure))
            {
                // The prisoner remains a valid independent request. The
                // route threat is optional in the authored catalog, but its
                // failure must be visible rather than converted to a custody
                // failure or a fabricated fallback actor.
                routeThreatProfile = null;
                LogDebug(
                    "POLICE_CONVOY_ROUTE_THREAT_ASSET_SELECTION_FAILED",
                    "Reason=" + threatFailure);
            }
            RememberProfileAssets(prisonerProfile);
            RememberProfileAssets(routeThreatProfile);
            return true;
        }

        internal string AvailabilityStatus
        {
            get
            {
                if (_incident != null)
                    return CurrentStatus;
                if (!_settings.Enabled)
                    return "Dispatch is disabled in LS Immersive settings.";
                if (!_patrolSessionStarted)
                    return "Start Patrol to receive universal Dispatch calls.";
                if (DateTime.UtcNow < _nextOfferAt)
                    return "Quiet patrol. Dispatch remains available when the next call is due.";
                return "Quiet patrol. Dispatch is checking its authored callout catalog.";
            }
        }

        internal bool IsInTransportLifecycle
        {
            get
            {
                return _incident != null &&
                    (_incident.State == LSPDDispatchState.Arrested
                     || _incident.State == LSPDDispatchState.AwaitingTransport
                     || _incident.State == LSPDDispatchState.HoldingAtStation
                     || _incident.State == LSPDDispatchState.PrisonTransfer);
            }
        }

        /// <summary>
        /// Advances the universal callout producer and its active incident.
        /// Offers are sparse and patrol-gated; no callout exists before the
        /// officer explicitly starts Patrol.
        /// </summary>
        internal void Process(bool isPatrolling, bool npcInteractionActive, bool paused)
        {
            Process(isPatrolling, npcInteractionActive, paused, true, false);
        }

        internal void Process(
            bool isPatrolling,
            bool npcInteractionActive,
            bool paused,
            bool allowGameplayInput)
        {
            Process(isPatrolling, npcInteractionActive, paused, allowGameplayInput, false);
        }

        internal void Process(
            bool isPatrolling,
            bool npcInteractionActive,
            bool paused,
            bool allowGameplayInput,
            bool otherPoliceIncidentActive)
        {
            Process(
                isPatrolling,
                npcInteractionActive,
                paused,
                allowGameplayInput,
                otherPoliceIncidentActive,
                false);
        }

        internal void Process(
            bool isPatrolling,
            bool npcInteractionActive,
            bool paused,
            bool allowGameplayInput,
            bool otherPoliceIncidentActive,
            bool suppressEnterInput)
        {
            DateTime now = DateTime.UtcNow;
            ProcessDeferredCleanup(now);

            // Pause can happen while the player is holding a decision key. Keep
            // the edge tracker aligned with the real keyboard state before the
            // next unpaused tick, otherwise a held key could be interpreted as a
            // fresh dispatch decision after the game resumes.
            if (paused)
            {
                SynchronizeKeyboardInputStates();
                return;
            }

            if (!allowGameplayInput || _incident == null)
                SynchronizeKeyboardInputStates();

            if (!isPatrolling)
            {
                MaintainTrackedSuspectInjuries(now, Game.Player.Character);
                _patrolSessionStarted = false;
                _dispatchProducerEnabled = false;
                return;
            }

            if (!_patrolSessionStarted)
            {
                _patrolSessionStarted = true;
                _lastPatrolObserved = now;
                _dispatchProducerEnabled = _settings.Enabled;
                _nextOfferAt = _settings.Enabled
                    ? now.AddSeconds(InitialPatrolOfferDelaySeconds)
                    : DateTime.MaxValue;
                LogRuntime(
                    "POLICE_DISPATCH_PATROL_READY",
                    "Universal dispatch producer armed; initial automatic offer scheduled after "
                    + InitialPatrolOfferDelaySeconds + " seconds.");
            }

            if (_incident != null)
            {
                if (allowGameplayInput)
                    ProcessControllerInput(suppressEnterInput);
                if (now < _nextMaintenance)
                {
                    MaintainTrackedSuspectInjuries(now, Game.Player.Character);
                    return;
                }
                _nextMaintenance = now.AddMilliseconds(MaintenanceMilliseconds);
                MaintainIncident(now, Game.Player.Character);
                MaintainTrackedSuspectInjuries(now, Game.Player.Character);
                return;
            }

            if (!_settings.Enabled)
            {
                _dispatchProducerEnabled = false;
                return;
            }
            if (!_dispatchProducerEnabled)
            {
                _dispatchProducerEnabled = true;
                _nextOfferAt = now.AddSeconds(NextQuietPatrolSeconds());
                LogRuntime("POLICE_DISPATCH_ENABLED", "Dispatch producer re-armed after configuration enabled it.");
                return;
            }

            if (npcInteractionActive || otherPoliceIncidentActive || now < _cooldownUntil || now < _nextOfferAt)
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            TryCreateOffer(player, now, false);
        }

        internal string RequestOfferNow(Ped player)
        {
            DateTime now = DateTime.UtcNow;
            if (!_settings.Enabled)
                return "Dispatch is disabled in LS Immersive settings.";
            if (!_patrolSessionStarted)
            {
                // A menu action can arrive in the same frame as Patrol.Start,
                // before the normal Police Core tick has armed this producer.
                // The explicit request is already patrol-gated by Police Core,
                // so arm this existing session instead of rejecting the action.
                _patrolSessionStarted = true;
                _dispatchProducerEnabled = true;
            }
            if (_incident != null)
                return "A Dispatch assignment is already active.";
            if (player == null || !player.Exists())
                return "The player character is unavailable.";

            if (!TryCreateOffer(player, now, true))
                return _definitions.Count == 0
                    ? "Dispatch has no usable callout definitions in its XML catalog."
                    : "No new Dispatch callout is currently eligible from the authored catalog.";
            LogRuntime("POLICE_DISPATCH_MANUAL_REQUESTED", "A player-requested Dispatch offer was created.");
            return "Dispatch request received. Review the new call in Police Radio.";
        }

        private bool TryCreateOffer(Ped player, DateTime now, bool bypassAutomaticQuietPeriod)
        {
            LSPDDispatchEventDefinition definition = ChooseDefinition(player);
            if (definition == null)
            {
                _nextOfferAt = _definitions.Count == 0
                    ? now.AddSeconds(30)
                    : NextDefinitionEligibleAt(now);
                if (_definitions.Count == 0)
                    LogDebug("POLICE_DISPATCH_NO_DEFINITIONS", "Dispatch XML contained no usable callout definitions.");
                return false;
            }

            string assetFailureReason;
            LSPDCriminalProfileDefinition criminalProfile;
            if (!TryResolveDispatchProfile(
                definition,
                out criminalProfile,
                out assetFailureReason))
            {
                _nextOfferAt = now.AddSeconds(30);
                LogDebug(
                    "POLICE_DISPATCH_CRIMINAL_ASSET_SELECTION_FAILED",
                    "Event=" + definition.Id + "; Reason=" + assetFailureReason);
                return false;
            }

            List<LSPDDispatchLocationDefinition> eventAreas = new List<LSPDDispatchLocationDefinition>();
            if (definition.LocationIds != null)
            {
                foreach (string locationId in definition.LocationIds)
                {
                    LSPDDispatchLocationDefinition area;
                    if (_locations.TryGetValue(locationId, out area)
                        && definition.CanUseArea(area, player.Position))
                        eventAreas.Add(area);
                }
            }

            AmbientDispatchSceneResolution sceneResolution;
            string locationFailure = "AmbientWorld is not attached by PoliceCore.";
            if (_ambientWorld == null
                || !_ambientWorld.TryResolveDispatchScene(
                    eventAreas,
                    definition.IncidentType,
                    player.Position,
                    definition.RequiresVehicle,
                    out sceneResolution,
                    out locationFailure))
            {
                _nextOfferAt = now.AddSeconds(30);
                LogDebug(
                    "POLICE_DISPATCH_AMBIENT_WORLD_LOCATION_FAILED",
                    "Event=" + definition.Id
                    + "; Reason=" + (_ambientWorld == null
                        ? "AmbientWorld is not attached by PoliceCore."
                        : locationFailure));
                return false;
            }

            LSPDDispatchEvent incident = LSPDDispatchEvent.FromDefinition(
                definition,
                sceneResolution.SceneCenter,
                criminalProfile);
            if (incident == null)
            {
                _nextOfferAt = now.AddSeconds(30);
                LogDebug(
                    "POLICE_DISPATCH_INCIDENT_NOT_CREATED",
                    "Event=" + definition.Id + "; Reason=AUTHORITATIVE_CRIMINAL_ASSET_SNAPSHOT_INVALID");
                return false;
            }
            incident.SceneResolution = sceneResolution;
            if (Receive(incident, bypassAutomaticQuietPeriod))
            {
                _cooldownUntil = now.AddSeconds(Math.Max(
                    definition.MinimumCooldownSeconds,
                    _settings.MinimumQuietPatrolSeconds));
                _nextOfferAt = now.AddSeconds(NextQuietPatrolSeconds());
                return true;
            }

            _nextOfferAt = now.AddSeconds(30);
            return false;
        }

        /// <summary>
        /// A completed gang threat or civilian contact is contextual gameplay,
        /// not another Dispatch producer. Give its local aftermath a short
        /// quiet period before this owner can make a new universal offer.
        /// </summary>
        internal void DeferOffersForContextActivity(string context)
        {
            if (_incident != null || !_patrolSessionStarted)
                return;
            DateTime now = DateTime.UtcNow;
            DateTime deferredUntil = now.AddSeconds(_settings.ContextClearQuietSeconds);
            if (_nextOfferAt < deferredUntil)
                _nextOfferAt = deferredUntil;
            if (_cooldownUntil < deferredUntil)
                _cooldownUntil = deferredUntil;
            LogRuntime(
                "POLICE_DISPATCH_CONTEXT_QUIET",
                "Source=" + (string.IsNullOrWhiteSpace(context) ? "contextual activity" : context)
                + "; Seconds=" + _settings.ContextClearQuietSeconds);
        }

        private void ProcessControllerInput(bool suppressEnterInput)
        {
            if (_incident == null)
                return;

            // Dispatch decisions use the authored Y/N keyboard pair. GTA maps
            // FrontendCancel to both controller Circle and keyboard Escape /
            // Backspace, so only read the frontend controls while the gamepad
            // is the active input method. This lets Circle accept and X/Cross
            // decline without closing a Police menu also declining the call.
            if (HasOffer)
            {
                bool acceptKeyPressed = WasKeyPressed(
                    _controls.AcceptKey,
                    ref _acceptKeyDown);
                bool rejectKeyPressed = WasKeyPressed(
                    _controls.RejectKey,
                    ref _rejectKeyDown);
                bool controllerInput = IsControllerInputActive();
                bool controllerAccept = controllerInput
                    && Game.IsControlJustPressed(GTA.Control.FrontendCancel);
                bool controllerReject = controllerInput
                    && Game.IsControlJustPressed(GTA.Control.FrontendAccept);

                if (controllerAccept || acceptKeyPressed)
                    Accept();
                else if (controllerReject || rejectKeyPressed)
                    Decline();
                return;
            }

            if ((!suppressEnterInput && Game.IsControlJustPressed(GTA.Control.Enter))
                || WasKeyPressed(_controls.InvestigateKey, ref _investigateKeyDown))
                Notify(Investigate(Game.Player.Character));
            else
            {
                // Reload is also the normal keyboard R action. Core consumes
                // the configured transport key before Dispatch is processed,
                // so do not let the same physical R press fall through as a
                // second secure command and undo Dispatch -> Convoy custody
                // ownership. Controller Reload remains available when no
                // configured transport key is physically held.
                bool configuredSecurePressed =
                    WasKeyPressed(_controls.SecureKey, ref _secureKeyDown);
                bool controllerReloadPressed =
                    Game.IsControlJustPressed(GTA.Control.Reload)
                    && !Game.IsKeyPressed(_controls.TransportKey);
                // Convoy owns the physical prisoner interaction after the
                // custody handoff. Let its E-controlled escort/loading path
                // consume that key instead of showing the old Dispatch
                // "Convoy already owns" response on every player interaction.
                if ((controllerReloadPressed || configuredSecurePressed)
                    && !_incident.HasConvoyCustodyHandoff)
                    Notify(SecureSuspect(Game.Player.Character));
            }
        }

        private static bool IsKeyDown(Keys key)
        {
            return Game.IsKeyPressed(key);
        }

        private static bool IsControllerInputActive()
        {
            try
            {
                return !Function.Call<bool>(Hash.IS_USING_KEYBOARD_AND_MOUSE, 2);
            }
            catch
            {
                // If input-method detection is unavailable, preserve the
                // unambiguous Y/N keyboard path and do not treat menu-back as
                // a Dispatch decision.
                return false;
            }
        }

        private static bool WasKeyPressed(Keys key, ref bool wasDown)
        {
            bool isDown = IsKeyDown(key);
            bool pressed = isDown && !wasDown;
            wasDown = isDown;
            return pressed;
        }

        private void SynchronizeKeyboardInputStates()
        {
            _acceptKeyDown = IsKeyDown(_controls.AcceptKey);
            _rejectKeyDown = IsKeyDown(_controls.RejectKey);
            _investigateKeyDown = IsKeyDown(_controls.InvestigateKey);
            _secureKeyDown = IsKeyDown(_controls.SecureKey);
        }

        /// <summary>
        /// Accepts a callout supplied by another Police producer. The local
        /// patrol producer uses this same boundary, so UI and external
        /// integrations share one ownership and state-transition path.
        /// </summary>
        internal bool Receive(LSPDDispatchEvent incident, bool bypassAutomaticQuietPeriod = false)
        {
            if (!_settings.Enabled || incident == null || _incident != null
                || (!bypassAutomaticQuietPeriod && DateTime.UtcNow < _cooldownUntil))
                return false;

            incident.State = LSPDDispatchState.Offered;
            incident.StateChangedAt = DateTime.UtcNow;
            _incident = incident;
            _definitionOfferedAt[DefinitionId(incident)] = DateTime.UtcNow;
            if (CanPlayOfferAudio())
                ReportAudioStage("lsimmersivelife.police.dispatch.received", incident, "received");
            Notify(FormatOfferNotification(incident));
            LogRuntime(
                "POLICE_DISPATCH_OFFERED",
                incident.Title + " | Type=" + incident.IncidentType
                + " | Context=" + incident.LocationContext
                + " | Branches=" + string.Join(",", incident.BranchIds ?? new List<string>())
                + " | Origin=" + incident.Origin);
            return true;
        }

        private static string FormatOfferNotification(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return "~b~911 DISPATCH~s~\nNo call details are currently available.";

            string title = string.IsNullOrWhiteSpace(incident.Title)
                ? "Emergency call"
                : incident.Title;
            string briefing = string.IsNullOrWhiteSpace(incident.Briefing)
                ? "Details unavailable."
                : incident.Briefing;

            return "~b~911 DISPATCH~s~\n"
                + "~y~CALL:~s~ " + title + "\n"
                + "~c~REPORT:~s~ " + briefing + "\n"
                + "~c~RESPOND?~s~  ~g~Y / Circle ACCEPT~s~  ~r~N / X DECLINE~s~";
        }

        internal LSPDAudioResult Acknowledge()
        {
            if (!HasOffer)
                return LSPDAudioResult.Inactive;
            Notify("~b~POLICE DISPATCH~s~\nInformation received: " + _incident.Briefing);
            LogRuntime("POLICE_DISPATCH_ACKNOWLEDGED", _incident.Title);
            return ReportAudioStage(
                "lsimmersivelife.police.dispatch.acknowledged",
                _incident,
                "acknowledged");
        }

        internal LSPDAudioResult Accept()
        {
            if (!HasOffer)
                return LSPDAudioResult.Inactive;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return LSPDAudioResult.Unavailable;

            if (!BeginScenePreparation(_incident))
            {
                Fail("The dispatch scene has an unavailable model definition.");
                return LSPDAudioResult.Unavailable;
            }

            SetState(LSPDDispatchState.Accepted);
            SetState(LSPDDispatchState.EnRoute);
            CaptureAndSetWaypoint(_incident.Origin);
            SetSceneBlip(_incident.Origin, _incident.Title);
            LSPDAudioResult accepted = ReportAudioStage(
                "lsimmersivelife.police.player.accept_dispatch",
                _incident,
                "accepted");
            Notify("~b~POLICE DISPATCH~s~\nAssignment accepted. Proceed to the marked scene.");
            LogRuntime("POLICE_DISPATCH_ACCEPTED", _incident.Title + " | Scene preparation requested.");
            return accepted;
        }

        internal LSPDAudioResult Decline()
        {
            if (!HasOffer)
                return LSPDAudioResult.Inactive;
            LSPDDispatchEvent incident = _incident;
            LSPDAudioResult result = Terminate(
                incident,
                LSPDDispatchState.Cancelled,
                "Declined by officer.",
                "lsimmersivelife.police.player.decline_dispatch",
                "declined",
                false);
            Notify("~b~POLICE DISPATCH~s~\nCall declined. You remain available for patrol.");
            return result;
        }

        internal string Investigate(Ped player)
        {
            if (_incident == null)
                return "No active dispatch is waiting for investigation.";
            if (_incident.State == LSPDDispatchState.Offered)
                return "Accept the dispatch before investigating the scene.";
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
                return "Prisoner transport currently owns this dispatch custody.";
            if (player == null || !player.Exists())
                return "Player character unavailable.";

            if (_incident.Suspect == null || !_incident.Suspect.Exists())
            {
                if (!EnsureScene(player))
                {
                    if (_incident == null)
                        return "The dispatch scene could not be prepared and the assignment has ended.";
                    if (_incident.ScenePreparationRequested
                        && !_incident.ScenePreparationCompleted)
                        return "Dispatch is preparing the scene. Keep the assignment active for a moment.";
                    return "The active dispatch scene is temporarily unavailable.";
                }
            }

            bool pursuit = _incident.State == LSPDDispatchState.SuspectFleeing
                || _incident.State == LSPDDispatchState.SuspectResisting;
            Vector3 target = pursuit && _incident.Suspect != null && _incident.Suspect.Exists()
                ? _incident.Suspect.Position : _incident.Origin;
            if (player.Position.DistanceTo(target) > (pursuit ? ArrestRadius + 4f : SceneArrivalRadius))
                return pursuit
                    ? "Follow the active suspect marker before investigating."
                    : "Proceed to the marked scene before investigating.";

            // A dead suspect is only a completed scene after the officer has
            // actually reached the scene or the confirmed pursuit position.
            // The old ordering closed a callout from anywhere in the city as
            // soon as the suspect died, which made Dispatch feel like a log
            // simulator and removed the visible aftermath.
            if (LiveSuspects(_incident).Any() == false)
            {
                if (HasUnfinishedArrestedCustody(_incident))
                {
                    Fail("An arrested suspect was lost before prisoner transport completed.");
                    return "Prisoner custody failed because the arrested suspect was lost before transport completed.";
                }
                string completion = CompletionMessage(_incident, true);
                Complete(completion);
                return completion;
            }

            if (_incident.State == LSPDDispatchState.EnRoute)
                SetState(LSPDDispatchState.OnScene);

            StartSceneBehavior(player);
            if (_incident == null || !_incident.OwnedByDispatch)
                return "The dispatch scene is no longer available for investigation.";
            if (_incident.State == LSPDDispatchState.OnScene)
                SetState(LSPDDispatchState.Investigating);

            if (!_incident.SceneArrivalReported)
            {
                _incident.SceneArrivalReported = true;
                ReportAudioStage("lsimmersivelife.police.scene.on_scene", _incident, "arrived", true);
            }

            if (_incident.Suspect.IsInCombatAgainst(player) || _incident.Suspect.IsShooting)
            {
                SetState(LSPDDispatchState.SuspectResisting);
                Notify("~r~POLICE DISPATCH~s~\nSuspect is resisting. Use Secure & Comply when safe.");
                return "Suspect is resisting. Continue the response and secure the suspect when safe.";
            }

            if (_incident.Suspect.IsFleeing)
            {
                SetState(LSPDDispatchState.SuspectFleeing);
                return "Suspect is fleeing. Follow the active suspect marker.";
            }

            if (!_incident.InvestigationReported)
            {
                _incident.InvestigationReported = true;
                ReportAudioStage("lsimmersivelife.police.investigation.started", _incident, "investigation", true);
            }

            Notify("~b~POLICE DISPATCH~s~\nScene reached. Suspect is present. Use Secure & Comply.");
            return "Scene reached. Suspect is present and ready for Police interaction.";
        }

        private static string CompletionMessage(LSPDDispatchEvent incident, bool suspectNeutralized)
        {
            string prefix = suspectNeutralized ? "Suspect neutralized. " : string.Empty;
            switch ((incident == null ? string.Empty : incident.IncidentType ?? string.Empty).ToLowerInvariant())
            {
                case "carjacking":
                    return prefix + "Carjacking scene secured; victim and vehicle details recorded.";
                case "vehicle_theft":
                    return prefix + "Stolen vehicle scene secured and recovery recorded.";
                case "vehicle_pursuit":
                case "reckless_driver":
                    return prefix + "Vehicle pursuit scene secured and traffic hazard contained.";
                case "bank_robbery":
                    return prefix + "Bank robbery scene secured and evidence handoff recorded.";
                case "store_robbery":
                    return prefix + "Store robbery scene secured and witness area cleared.";
                case "kidnapping":
                    return prefix + "Kidnapping scene secured and victim recovery recorded.";
                case "shots_fired":
                    return prefix + "Shots-fired scene secured and weapon threat contained.";
                case "violent_disturbance":
                    return prefix + "Violent disturbance contained and scene secured.";
                default:
                    return prefix + "Scene investigation completed.";
            }
        }

        internal string SecureSuspect(Ped player)
        {
            if (_incident == null)
                return "There is no living suspect owned by the active dispatch.";
            if (player == null || !player.Exists())
                return "Player character unavailable.";
            if (_incident.State == LSPDDispatchState.Offered || _incident.State == LSPDDispatchState.EnRoute)
                return "Arrive at the dispatch scene first.";
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
                return "Convoy already owns this secured prisoner. Continue the active transport operation.";
            if (_incident.PlayerHandcuffInProgress)
                return "Physical handcuffing is already in progress. Keep close to the suspect.";

            List<Ped> living = LiveSuspects(_incident).ToList();
            if (living.Count == 0)
            {
                if (HasUnfinishedArrestedCustody(_incident))
                {
                    Fail("An arrested suspect was lost before prisoner transport completed.");
                    return "Prisoner custody failed because the arrested suspect was lost before transport completed.";
                }
                string completed = CompletionMessage(_incident, true);
                Complete(completed);
                return completed;
            }

            Ped target = living
                .Where(ped => !IsArrested(_incident, ped))
                .OrderBy(ped => ped.Position.DistanceTo(player.Position))
                .FirstOrDefault();
            if (target == null)
            {
                SetState(LSPDDispatchState.Arrested);
                return "All living suspects are secured. Request prisoner transport when ready.";
            }
            if (player.Position.DistanceTo(target.Position) > ArrestRadius + 1f)
                return "Move closer to the next unsecured suspect before issuing the Police command.";

            bool downedAlive = IsDownedOrInjured(target);
            if (!IsCompliant(_incident, target) && !downedAlive)
                return OrderSuspectToComply(target, player, false);

            if (!downedAlive
                && DateTime.UtcNow < _incident.SurrenderRequestedAt.AddSeconds(SurrenderSettleSeconds))
                return "Surrender command issued. Wait for visible compliance, then secure again.";

            return BeginPlayerHandcuff(player, target);
        }

        internal string ReleaseCurrentSuspect()
        {
            if (!CanReleaseCurrentSuspect)
                return "The active Dispatch suspect is not ready for release. Secure the suspect on foot first.";

            Ped suspect = CurrentSuspect;
            int handle = suspect.Handle;
            try
            {
                suspect.Task.ClearAll();
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, suspect, false);
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    suspect, false, false, false, false, false, false, false, false);
                suspect.CanSwitchWeapons = true;
                suspect.BlockPermanentEvents = false;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, suspect, false);
                Function.Call(Hash.SET_PED_KEEP_TASK, suspect, false);
                Function.Call(Hash.TASK_WANDER_STANDARD, suspect, 10.0f, 10);
                suspect.IsPersistent = false;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SUSPECT_RELEASE_FAILED", ex);
                return "The suspect could not be released safely. Dispatch custody remains active.";
            }

            return CompletePhysicalSuspectRelease(suspect, handle);
        }

        /// <summary>
        /// Convoy calls this only after its owned physical exit/release has
        /// completed. Dispatch updates the incident's exact suspect references
        /// but does not change the Ped's physical state a second time.
        /// </summary>
        internal string CompletePhysicalSuspectRelease(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.IsInVehicle())
                return "The suspect has not physically exited and cannot be released yet.";
            return CompletePhysicalSuspectRelease(suspect, suspect.Handle);
        }

        private string CompletePhysicalSuspectRelease(Ped suspect, int releasedHandle)
        {
            LSPDDispatchEvent incident = _incident;
            if (incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || suspect.Handle != releasedHandle
                || !IsArrested(incident, suspect))
                return "The released suspect no longer belongs to the active Dispatch custody.";

            incident.Suspects.RemoveAll(ped => ped == null || !ped.Exists()
                || ped.Handle == releasedHandle);
            incident.ArrestedSuspects.RemoveAll(ped => ped == null || !ped.Exists()
                || ped.Handle == releasedHandle);
            incident.CompliantSuspects.RemoveAll(ped => ped == null || !ped.Exists()
                || ped.Handle == releasedHandle);
            _lastCompliantTaskAt.Remove(releasedHandle);
            _lastSecuredPoseTaskAt.Remove(releasedHandle);

            if (incident.Suspect != null && incident.Suspect.Handle == releasedHandle)
            {
                incident.Suspect = LiveSuspects(incident).FirstOrDefault();
                CleanupBlip(_suspectBlip);
                _suspectBlip = null;
                if (incident.Suspect != null
                    && (incident.State == LSPDDispatchState.SuspectFleeing
                        || incident.State == LSPDDispatchState.SuspectResisting))
                    SetSuspectBlip(incident.Suspect);
            }

            int remaining = LiveSuspects(incident).Count();
            LogRuntime("POLICE_DISPATCH_SUSPECT_RELEASED",
                "Ped=" + releasedHandle + "; Remaining=" + remaining
                + "; DispatchOwned=" + incident.OwnedByDispatch
                + "; ConvoyHandoff=" + incident.HasConvoyCustodyHandoff);

            if (remaining == 0)
            {
                const string completed = "The secured suspect was released. Dispatch is closed.";
                Complete(completed);
                return completed;
            }

            const string continued = "The secured suspect was released. Other suspects remain in custody, so Dispatch stays active.";
            CloseActiveAudio(incident);
            Notify("~b~DISPATCH~s~\n" + continued);
            return continued;
        }

        private string BeginPlayerHandcuff(Ped player, Ped target)
        {
            if (_incident == null || player == null || !player.Exists()
                || target == null || !target.Exists()
                || target.IsDead || target.Health <= 0)
                return "The physical handcuff interaction is no longer available.";

            DateTime now = DateTime.UtcNow;
            try
            {
                // Mark the target as controlled before assigning the native
                // arrest task. This closes the small window where a collision
                // or ambient task could make a compliant suspect walk away
                // while the visible player interaction is starting.
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    target, false, false, false, true, false, false, false, false);
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                bool targetDowned = IsDownedOrInjured(target);
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    player, target, 1000);
                Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                    target, player, 1000);
                Function.Call(Hash.TASK_ARREST_PED, player, target);
                int handcuffDuration = PlayerHandcuffAnimationMilliseconds;
                _playerHandcuffHoldUntil = now.AddMilliseconds(handcuffDuration);
                _playerHandcuffAttempts = 1;
                _incident.PlayerHandcuffInProgress = true;
                _incident.PlayerHandcuffTargetHandle = target.Handle;
                _incident.PlayerHandcuffStartedAt = now;
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_STARTED",
                    "Ped=" + target.Handle + "; Player=" + player.Handle
                    + "; Method=GroundedTaskArrestPed"
                    + "; SynchronizedScene=false"
                    + "; DownedOrInjured=" + targetDowned
                    + "; HoldMilliseconds=" + handcuffDuration);
                Notify("~b~POLICE CUSTODY~s~\nPhysically handcuffing the compliant suspect. Keep close until secure.");
                return "Physically handcuffing the compliant suspect...";
            }
            catch (Exception ex)
            {
                ClearPlayerHandcuffState();
                LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_START_FAILED", ex);
                return "The physical handcuff interaction could not be started safely.";
            }
        }

        private void MaintainPlayerHandcuff(DateTime now, Ped player)
        {
            if (_incident == null || !_incident.PlayerHandcuffInProgress)
                return;

            Ped target = CurrentSuspects.FirstOrDefault(ped =>
                ped != null && ped.Exists()
                && ped.Handle == _incident.PlayerHandcuffTargetHandle);
            if (target == null || target.IsDead || target.Health <= 0)
            {
                if (target != null && IsDispatchNonfatalRecoveryPending(target))
                    return;

                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ABORTED",
                    "Ped=" + _incident.PlayerHandcuffTargetHandle
                    + "; Reason=" + (target == null ? "PRISONER_REFERENCE_INVALID" : "PRISONER_DIED"));
                ClearPlayerHandcuffState();
                return;
            }

            try
            {
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    target, false, false, false, true, false, false, false, false);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_MAINTENANCE_FAILED", ex);
            }

            if (now < _playerHandcuffHoldUntil)
                return;

            if (player == null || !player.Exists()
                || player.Position.DistanceTo(target.Position) > ArrestRadius + 2f)
            {
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_ABORTED",
                    "Ped=" + target.Handle + "; Reason=PLAYER_LEFT_INTERACTION_RADIUS");
                ClearPlayerHandcuffState();
                Notify("The suspect remains compliant. Move close and press E again to finish handcuffing.");
                return;
            }

            if (!IsPedPhysicallyCuffed(target))
            {
                if (_playerHandcuffAttempts < PlayerHandcuffMaximumAttempts)
                {
                    try
                    {
                        if (!IsDownedOrInjured(target))
                        {
                            Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                player, target, 1000);
                            Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                                target, player, 1000);
                        }
                        Function.Call(Hash.TASK_ARREST_PED, player, target);
                        _playerHandcuffAttempts++;
                        _playerHandcuffHoldUntil = now.AddMilliseconds(
                            PlayerHandcuffAnimationMilliseconds);
                        LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_RETRIED",
                            "Ped=" + target.Handle + "; Player=" + player.Handle
                            + "; Attempt=" + _playerHandcuffAttempts
                            + "; MaximumAttempts=" + PlayerHandcuffMaximumAttempts
                            + "; DownedOrInjured=" + IsDownedOrInjured(target));
                        Notify("Keep beside the suspect while the physical handcuffing finishes.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_RETRY_FAILED", ex);
                    }
                }

                ClearPlayerHandcuffState();
                if (!IsDownedOrInjured(target))
                {
                    try { target.Task.HandsUp(15000); } catch { }
                }
                Notify("The handcuff state was not confirmed. The suspect remains available; move close and press E to retry.");
                return;
            }

            try { player.Task.ClearAll(); } catch { }
            string result = CompletePlayerHandcuff(target);
            if (!string.IsNullOrWhiteSpace(result))
                Notify(result);
        }

        private string CompletePlayerHandcuff(Ped target)
        {
            if (_incident == null || target == null || !target.Exists()
                || target.IsDead || target.Health <= 0
                || !IsPedPhysicallyCuffed(target))
            {
                ClearPlayerHandcuffState();
                return "The suspect could not be retained in physical custody because the game did not confirm the handcuffs.";
            }

            try
            {
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, target, true);
                Function.Call(Hash.SET_ENTITY_PROOFS,
                    target, false, false, false, true, false, false, false, false);
                target.BlockPermanentEvents = true;
                target.CanSwitchWeapons = false;
                RestoreSuspectInjuryState(target);
                AddArrestedSuspect(_incident, target);
                ClearPlayerHandcuffState();
                _lastSecuredPoseTaskAt.Remove(target.Handle);
                MaintainHandcuffedPose(target, DateTime.UtcNow, true);

                int remaining = LiveSuspects(_incident).Count(ped => !IsArrested(_incident, ped));
                if (remaining > 0)
                {
                    SetState(LSPDDispatchState.SuspectCompliant);
                    Notify("~g~SUSPECT SECURED~s~\n" + remaining
                        + " suspect(s) still require physical custody.");
                    LogRuntime("POLICE_GROUP_SUSPECT_SECURED",
                        "Ped=" + target.Handle + "; Remaining=" + remaining
                        + "; Method=PlayerHandcuff");
                    return "Suspect physically handcuffed. " + remaining
                        + " living suspect(s) still require custody.";
                }

                _incident.ArrestSecured = true;
                SetState(LSPDDispatchState.Arrested);
                ReportAudioStage("lsimmersivelife.police.arrest.successful", _incident, "arrested");
                int custodyCount = CurrentArrestedSuspects.Count();
                Notify("~g~ARREST SUCCESS~s~\n" + custodyCount
                    + " suspect(s) physically secured. Request prisoner transport when ready.");
                LogRuntime("POLICE_DISPATCH_PLAYER_HANDCUFF_COMPLETED",
                    "Ped=" + target.Handle + "; Prisoners=" + custodyCount);
                return custodyCount == 1
                    ? "Suspect handcuffed and secured. Prisoner custody is ready for transport."
                    : custodyCount + " suspects handcuffed and secured. Group custody is ready for transport.";
            }
            catch (Exception ex)
            {
                ClearPlayerHandcuffState();
                LogException("POLICE_DISPATCH_PLAYER_HANDCUFF_COMPLETE_FAILED", ex);
                return "The suspect could not be secured physically; keep the suspect compliant and try E again.";
            }
        }

        private void ClearPlayerHandcuffState()
        {
            _playerHandcuffHoldUntil = DateTime.MinValue;
            _playerHandcuffAttempts = 0;
            if (_incident == null)
                return;
            _incident.PlayerHandcuffInProgress = false;
            _incident.PlayerHandcuffTargetHandle = 0;
            _incident.PlayerHandcuffStartedAt = DateTime.MinValue;
        }

        /// <summary>
        /// Called by the dedicated Backup owner only after its real support
        /// units arrive at the assignment. This never creates or teleports
        /// suspects; it turns a reachable, non-combat group into a visible
        /// hands-up state so the player can still perform each final secure.
        /// </summary>
        internal int RequestBackupGroupCompliance(Ped player)
        {
            if (_incident == null || player == null || !player.Exists())
                return 0;

            int ordered = 0;
            foreach (Ped suspect in LiveSuspects(_incident))
            {
                if (IsArrested(_incident, suspect)
                    || IsDownedOrInjured(suspect)
                    || suspect.IsInCombatAgainst(player) || suspect.IsShooting)
                    continue;
                if (suspect.Position.DistanceTo(_incident.Origin) > 75f)
                    continue;
                if (!IsCompliant(_incident, suspect))
                {
                    try
                    {
                        suspect.Task.ClearAll();
                        suspect.BlockPermanentEvents = true;
                        suspect.CanSwitchWeapons = false;
                        suspect.Task.HandsUp(12000);
                        AddCompliantSuspect(_incident, suspect);
                        ordered++;
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_BACKUP_GROUP_COMPLIANCE_FAILED", ex);
                    }
                }
            }
            if (ordered > 0)
            {
                _incident.SurrenderRequested = true;
                _incident.GroupComplianceRequested = true;
                _incident.SurrenderRequestedAt = DateTime.UtcNow;
                SetState(LSPDDispatchState.SuspectCompliant);
                LogRuntime("POLICE_BACKUP_GROUP_COMPLIANCE",
                    "Ordered=" + ordered + "; Suspects=" + ActiveSuspectCount);
            }
            return ordered;
        }

        /// <summary>
        /// An interception unit can reach a fleeing target before the player.
        /// It pins the actual tracked suspect in a compliant state; the player
        /// still owns the final arrest/Convoy handoff.
        /// </summary>
        internal bool RequestBackupInterception(Ped suspect, Ped player)
        {
            if (_incident == null || suspect == null || !suspect.Exists()
                || suspect.IsDead || !CurrentSuspects.Any(ped => ped.Handle == suspect.Handle))
                return false;
            if (IsArrested(_incident, suspect))
                return true;
            if (IsDownedOrInjured(suspect))
                return true;
            // Backup.Process keeps tracking an intercepted suspect while the
            // officer travels to the marked position. Do not clear and restart
            // the hands-up task every game tick once the real suspect is
            // already compliant; that interrupted the visible surrender pose.
            if (IsCompliant(_incident, suspect))
                return true;
            try
            {
                suspect.Task.ClearAll();
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                suspect.Task.HandsUp(15000);
                if (player != null && player.Exists())
                    suspect.Task.LookAt(player, 2000);
                AddCompliantSuspect(_incident, suspect);
                _incident.SurrenderRequested = true;
                _incident.SurrenderRequestedAt = DateTime.UtcNow;
                SetState(LSPDDispatchState.SuspectCompliant);
                LogRuntime("POLICE_BACKUP_INTERCEPTION_CONFIRMED",
                    "Ped=" + suspect.Handle + "; Position=" + suspect.Position);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_BACKUP_INTERCEPTION_FAILED", ex);
                return false;
            }
        }

        private string OrderSuspectToComply(Ped suspect, Ped player, bool groupCommand)
        {
            try
            {
                suspect.Task.ClearAll();
                suspect.BlockPermanentEvents = true;
                suspect.CanSwitchWeapons = false;
                suspect.Task.HandsUp(10000);
                suspect.Task.LookAt(player, 2500);
                AddCompliantSuspect(_incident, suspect);
                _incident.SurrenderRequested = true;
                _incident.SurrenderRequestedAt = DateTime.UtcNow;
                Notify("~b~POLICE COMMAND~s~\nSuspect ordered to show hands.\n~c~Wait, then secure again.");
                LogRuntime("POLICE_SUSPECT_SURRENDER_REQUESTED",
                    "Ped=" + suspect.Handle + "; Group=" + groupCommand);
                return "Surrender command issued. Wait for visible compliance, then secure again.";
            }
            catch (Exception ex)
            {
                LogException("POLICE_SUSPECT_SURRENDER_FAILED", ex);
                return "The surrender command could not be assigned safely.";
            }
        }

        private static bool IsCompliant(LSPDDispatchEvent incident, Ped suspect)
        {
            return incident != null && suspect != null && suspect.Exists()
                && !suspect.IsDead && suspect.Health > 0
                && incident.CompliantSuspects != null
                && incident.CompliantSuspects.Any(value => value != null && value.Exists()
                    && !value.IsDead && value.Health > 0
                    && value.Handle == suspect.Handle);
        }

        private static bool IsLivingDowned(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.Health <= 0)
                return false;
            try
            {
                return Function.Call<bool>(Hash.IS_PED_RAGDOLL, suspect);
            }
            catch
            {
                return false;
            }
        }

        private bool IsWoundedSuspect(Ped suspect)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead
                || suspect.Health <= 0)
                return false;
            DispatchSuspectInjuryState state;
            return _suspectInjuries.TryGetValue(suspect.Handle, out state)
                && state.Wounded && !state.Fatal;
        }

        private bool IsDownedOrInjured(Ped suspect)
        {
            return IsLivingDowned(suspect) || IsWoundedSuspect(suspect);
        }

        private static bool IsPedPhysicallyCuffed(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return false;
            try { return Function.Call<bool>(Hash.IS_PED_CUFFED, suspect); }
            catch { return false; }
        }

        private void MaintainTrackedSuspectInjuries(DateTime now, Ped player)
        {
            if (_incident == null)
            {
                RestoreAllSuspectInjuryStates();
                return;
            }
            if (!_incident.OwnedByDispatch || _incident.HasConvoyCustodyHandoff)
            {
                // Convoy is the sole physical owner after its explicit
                // handoff. Do not keep applying Dispatch injury tasks then.
                RestoreAllSuspectInjuryStates();
                return;
            }

            if (_incident.CompliantSuspects != null)
            {
                List<int> removed = _incident.CompliantSuspects
                    .Where(value => value == null || !value.Exists()
                        || value.IsDead || value.Health <= 0)
                    .Select(value => value == null || !value.Exists()
                        ? 0 : value.Handle)
                    .ToList();
                _incident.CompliantSuspects.RemoveAll(value => value == null
                    || !value.Exists() || value.IsDead || value.Health <= 0);
                foreach (int handle in removed)
                {
                    _lastCompliantTaskAt.Remove(handle);
                    _lastSecuredPoseTaskAt.Remove(handle);
                }
            }

            foreach (Ped suspect in TrackedSuspects(_incident).ToArray())
            {
                if (suspect == null || !suspect.Exists())
                    continue;
                if (IsArrested(_incident, suspect))
                {
                    RestoreSuspectInjuryState(suspect);
                    continue;
                }

                CaptureDispatchSuspectInjuryState(suspect);
                MaintainDispatchSuspectInjury(suspect, now, player);
            }
        }

        private void CaptureDispatchSuspectInjuryState(Ped suspect)
        {
            if (suspect == null || !suspect.Exists()
                || _suspectInjuries.ContainsKey(suspect.Handle))
                return;

            DispatchSuspectInjuryState state = new DispatchSuspectInjuryState
            {
                Actor = suspect,
                OriginalHealth = Math.Max(1, suspect.Health),
                OriginalMaximumHealth = Math.Max(1, suspect.MaxHealth),
                LastHealth = Math.Max(1, suspect.Health),
                OriginalNoCriticalHits = false
            };
            try
            {
                state.OriginalNoCriticalHits = Function.Call<bool>(
                    Hash.GET_PED_CONFIG_FLAG, suspect, 2, true);
                Function.Call(Hash.SET_PED_CONFIG_FLAG, suspect, 2, true);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_CRITICAL_HIT_CONTROL_FAILED", ex);
            }
            _suspectInjuries[suspect.Handle] = state;
        }

        private void MaintainDispatchSuspectInjury(
            Ped suspect,
            DateTime now,
            Ped player)
        {
            DispatchSuspectInjuryState state;
            if (suspect == null || !suspect.Exists()
                || !_suspectInjuries.TryGetValue(suspect.Handle, out state))
                return;

            int currentHealth = Math.Max(0, suspect.Health);
            if (currentHealth < state.LastHealth)
            {
                int damage = state.LastHealth - currentHealth;
                state.DamageTaken += damage;
                int bone = ReadDispatchLastDamageBone(suspect);
                DispatchHitZone zone = ClassifyDispatchHitZone(bone);
                if (zone == DispatchHitZone.Head)
                    state.HeadHits++;
                else if (zone == DispatchHitZone.Chest)
                    state.ChestHits++;
                else if (zone == DispatchHitZone.Stomach)
                    state.StomachHits++;

                state.Fatal = state.HeadHits >= 1
                    || state.ChestHits >= 3
                    || state.StomachHits >= 3;
                // Only the explicitly configured head/chest/stomach hit counts
                // can be fatal. Unmapped impact bones still need managed
                // nonfatal recovery so native health loss cannot end custody.
                state.Wounded = true;

                LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_RECORDED",
                    "Incident=" + (_incident == null ? string.Empty : _incident.Id)
                    + "; Ped=" + suspect.Handle + "; Bone=" + bone
                    + "; Zone=" + zone
                    + "; HeadHits=" + state.HeadHits
                    + "; ChestHits=" + state.ChestHits
                    + "; StomachHits=" + state.StomachHits
                    + "; Damage=" + damage + "; Fatal=" + state.Fatal);

                if (state.Fatal)
                {
                    try { suspect.Health = 0; } catch { }
                    state.LastHealth = 0;
                    LogRuntime("POLICE_DISPATCH_SUSPECT_FATAL_INJURY",
                        "Incident=" + (_incident == null ? string.Empty : _incident.Id)
                        + "; Ped=" + suspect.Handle + "; Zone=" + zone
                        + "; HeadHits=" + state.HeadHits
                        + "; ChestHits=" + state.ChestHits
                        + "; StomachHits=" + state.StomachHits);
                    return;
                }

                state.LastHealth = Math.Max(0, suspect.Health);
            }

            MaintainNonfatalDispatchInjuryRecovery(suspect, now, state);

            bool handcuffing = _incident != null
                && _incident.PlayerHandcuffInProgress
                && _incident.PlayerHandcuffTargetHandle == suspect.Handle;
            if (!state.Wounded || state.Fatal || handcuffing
                || IsArrested(_incident, suspect)
                || suspect.IsDead || suspect.Health <= 0
                || suspect.IsInVehicle()
                || now < state.NextWritheAt)
                return;

            state.NextWritheAt = now.AddSeconds(3);
            try
            {
                if (Function.Call<bool>(Hash.IS_PED_IN_WRITHE, suspect))
                {
                    state.WritheTaskStarted = true;
                    return;
                }
                if (!state.WritheTaskStarted)
                    suspect.Task.ClearAll();
                Function.Call(Hash.TASK_WRITHE,
                    suspect,
                    player != null && player.Exists() ? player : suspect,
                    1, 0, false, 0);
                state.WritheTaskStarted = true;
                LogRuntime("POLICE_DISPATCH_SUSPECT_WRITHE_TASK_ISSUED",
                    "Incident=" + (_incident == null ? string.Empty : _incident.Id)
                    + "; Ped=" + suspect.Handle
                    + "; Arrestable=true; Teleport=false; Attachment=false");
            }
            catch (Exception ex)
            {
                try
                {
                    Function.Call(Hash.SET_PED_TO_RAGDOLL,
                        suspect, 3500, 3500, 0, false, false, false);
                    state.WritheTaskStarted = true;
                }
                catch (Exception ragdollException)
                {
                    LogException("POLICE_DISPATCH_SUSPECT_WRITHE_FALLBACK_FAILED",
                        ragdollException);
                }
                LogException("POLICE_DISPATCH_SUSPECT_WRITHE_TASK_FAILED", ex);
            }
        }

        private void MaintainNonfatalDispatchInjuryRecovery(
            Ped suspect,
            DateTime now,
            DispatchSuspectInjuryState state)
        {
            if (state == null || !state.Wounded || state.Fatal
                || (!suspect.IsDead && suspect.Health > 0))
                return;

            if (state.NonfatalRecoveryAttempts >= NonfatalInjuryRecoveryMaximumAttempts)
            {
                if (!state.NonfatalRecoveryExhaustionLogged)
                {
                    state.NonfatalRecoveryExhaustionLogged = true;
                    LogRuntime("STATE_FAILURE_POLICE_DISPATCH_SUSPECT_INJURY_RECOVERY_EXHAUSTED",
                        "Incident=" + (_incident == null ? string.Empty : _incident.Id)
                        + "; Ped=" + suspect.Handle
                        + "; Attempts=" + state.NonfatalRecoveryAttempts
                        + "; Health=" + suspect.Health
                        + "; Dead=" + suspect.IsDead);
                }
                return;
            }
            if (now < state.NextNonfatalRecoveryAt)
                return;

            state.NextNonfatalRecoveryAt = now.AddMilliseconds(150);
            state.NonfatalRecoveryAttempts++;
            try
            {
                if (suspect.IsDead && !state.NonfatalResurrectionAttempted)
                {
                    Function.Call(Hash.RESURRECT_PED, suspect);
                    state.NonfatalResurrectionAttempted = true;
                }

                suspect.MaxHealth = state.OriginalMaximumHealth;
                int healthToRestore = Math.Max(50,
                    Math.Min(state.OriginalMaximumHealth,
                        state.OriginalHealth - state.DamageTaken));
                suspect.Health = healthToRestore;
                Function.Call(Hash.SET_PED_CONFIG_FLAG, suspect, 2, true);

                int healthAfter = Math.Max(0, suspect.Health);
                bool aliveAfter = !suspect.IsDead && healthAfter > 0;
                if (aliveAfter)
                {
                    int recoveryAttempts = state.NonfatalRecoveryAttempts;
                    state.LastHealth = healthAfter;
                    state.NonfatalRecoveryAttempts = 0;
                    state.NonfatalRecoveryExhaustionLogged = false;
                    state.NonfatalResurrectionAttempted = false;
                    LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_RETAINED",
                        "Incident=" + (_incident == null ? string.Empty : _incident.Id)
                        + "; Ped=" + suspect.Handle
                        + "; Health=" + healthAfter
                        + "; Alive=true; Arrestable=true; Attempts="
                        + recoveryAttempts);
                    return;
                }

                LogRuntime("POLICE_DISPATCH_SUSPECT_INJURY_RECOVERY_RETRY",
                    "Incident=" + (_incident == null ? string.Empty : _incident.Id)
                    + "; Ped=" + suspect.Handle
                    + "; Attempt=" + state.NonfatalRecoveryAttempts
                    + "; Health=" + healthAfter
                    + "; Dead=" + suspect.IsDead);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SUSPECT_INJURY_RECOVERY_FAILED", ex);
            }
        }

        private bool IsDispatchNonfatalRecoveryPending(Ped suspect)
        {
            DispatchSuspectInjuryState state;
            return suspect != null && suspect.Exists()
                && _suspectInjuries.TryGetValue(suspect.Handle, out state)
                && state.Wounded && !state.Fatal
                && state.NonfatalRecoveryAttempts < NonfatalInjuryRecoveryMaximumAttempts;
        }

        private static int ReadDispatchLastDamageBone(Ped suspect)
        {
            int bone = 0;
            try
            {
                using (OutputArgument output = new OutputArgument())
                {
                    if (Function.Call<bool>(
                        Hash.GET_PED_LAST_DAMAGE_BONE, suspect, output))
                        bone = output.GetResult<int>();
                }
                Function.Call(Hash.CLEAR_PED_LAST_DAMAGE_BONE, suspect);
            }
            catch { }
            return bone;
        }

        private static DispatchHitZone ClassifyDispatchHitZone(int bone)
        {
            switch (bone)
            {
                case 31086:
                case 39317:
                case 65068:
                    return DispatchHitZone.Head;
                case 64729:
                case 10706:
                case 24817:
                case 24818:
                    return DispatchHitZone.Chest;
                case 11816:
                case 57597:
                case 23553:
                case 24816:
                    return DispatchHitZone.Stomach;
                case 45509:
                case 40269:
                case 61163:
                case 43810:
                case 28252:
                case 18905:
                case 57005:
                case 58271:
                case 51826:
                case 63931:
                case 36864:
                case 14201:
                case 52301:
                case 2108:
                case 20781:
                    return DispatchHitZone.Limb;
                default:
                    return DispatchHitZone.Unknown;
            }
        }

        private void RestoreSuspectInjuryState(Ped suspect)
        {
            if (suspect == null || !suspect.Exists())
                return;
            DispatchSuspectInjuryState state;
            if (!_suspectInjuries.TryGetValue(suspect.Handle, out state))
                return;
            try
            {
                suspect.MaxHealth = Math.Max(1, state.OriginalMaximumHealth);
                Function.Call(Hash.SET_PED_CONFIG_FLAG,
                    suspect, 2, state.OriginalNoCriticalHits);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SUSPECT_INJURY_RESTORE_FAILED", ex);
            }
            _suspectInjuries.Remove(suspect.Handle);
        }

        private void RestoreAllSuspectInjuryStates()
        {
            foreach (DispatchSuspectInjuryState state in _suspectInjuries.Values.ToArray())
                if (state != null)
                    RestoreSuspectInjuryState(state.Actor);
            _suspectInjuries.Clear();
        }

        private static bool IsArrested(LSPDDispatchEvent incident, Ped suspect)
        {
            return incident != null && suspect != null && incident.ArrestedSuspects != null
                && incident.ArrestedSuspects.Any(value => value != null && value.Exists()
                    && value.Handle == suspect.Handle);
        }

        private static bool HasUnfinishedArrestedCustody(LSPDDispatchEvent incident)
        {
            return incident != null
                && incident.ArrestSecured
                && !incident.HasConvoyCustodyHandoff
                && incident.ArrestedSuspects != null
                && incident.ArrestedSuspects.Count > 0;
        }

        private static void AddCompliantSuspect(LSPDDispatchEvent incident, Ped suspect)
        {
            if (incident == null || suspect == null || incident.CompliantSuspects == null
                || IsCompliant(incident, suspect))
                return;
            incident.CompliantSuspects.Add(suspect);
        }

        private static void AddArrestedSuspect(LSPDDispatchEvent incident, Ped suspect)
        {
            if (incident == null || suspect == null || incident.ArrestedSuspects == null
                || IsArrested(incident, suspect))
                return;
            incident.ArrestedSuspects.Add(suspect);
        }

        internal bool TransferCustodyOwnershipToConvoy()
        {
            List<Ped> prisoners = CurrentArrestedSuspects.ToList();
            if (_incident == null || prisoners.Count == 0)
                return false;
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
            {
                // Custody transfer is a one-way ownership boundary. Repeated
                // R/handbrake input or a second Core pass must not move the
                // Dispatch state back and forth or emit another handoff.
                LogDebug(
                    "POLICE_CUSTODY_HANDOFF_DUPLICATE_IGNORED",
                    "Dispatch already transferred custody; Prisoners="
                    + string.Join(",", prisoners.Select(ped => ped.Handle.ToString()).ToArray()));
                return true;
            }
            _incident.OwnedByDispatch = false;
            _incident.SurrenderRequested = false;
            _incident.HasConvoyCustodyHandoff = true;
            // Convoy is now the sole owner of the prisoner, transport, and
            // custody markers. Leaving the Dispatch scene/suspect markers in
            // place created duplicate blue targets when the station handoff
            // immediately opened the prison-transfer phase.
            CleanupBlip(_sceneBlip);
            CleanupBlip(_suspectBlip);
            _sceneBlip = null;
            _suspectBlip = null;
            LogRuntime(
                "POLICE_DISPATCH_CUSTODY_MARKERS_RELEASED",
                "Dispatch markers released to Convoy ownership; Prisoners="
                + string.Join(",", prisoners.Select(ped => ped.Handle.ToString()).ToArray()));
            LogRuntime("POLICE_CUSTODY_HANDOFF", "Dispatch -> Convoy | Prisoners="
                + string.Join(",", prisoners.Select(ped => ped.Handle.ToString()).ToArray()));
            return true;
        }

        /// <summary>
        /// Convoy reports its own physical failure. When the arrested person is
        /// still valid, return ownership to Dispatch so the player can retry
        /// transport instead of losing the whole case or cancelling later calls.
        /// </summary>
        internal bool RecoverCustodyFromConvoy(Ped prisoner, string reason)
        {
            return RecoverCustodyFromConvoy(
                prisoner == null ? Enumerable.Empty<Ped>() : new[] { prisoner },
                reason);
        }

        internal bool RecoverCustodyFromConvoy(
            IEnumerable<Ped> prisoners,
            string reason)
        {
            List<Ped> valid = prisoners == null
                ? new List<Ped>()
                : prisoners.Where(ped => ped != null && ped.Exists() && !ped.IsDead).ToList();
            if (_incident == null || valid.Count == 0)
                return false;
            try
            {
                List<Ped> retainedDead = TrackedSuspects(_incident)
                    .Where(ped => ped != null && ped.Exists() && ped.IsDead)
                    .ToList();
                _incident.Suspect = valid[0];
                _incident.Suspects.Clear();
                _incident.Suspects.AddRange(valid);
                foreach (Ped dead in retainedDead)
                    if (!valid.Any(ped => ped.Handle == dead.Handle))
                        _incident.Suspects.Add(dead);
                _incident.ArrestedSuspects.Clear();
                _incident.ArrestedSuspects.AddRange(valid);
                _incident.CompliantSuspects.Clear();
                _incident.OwnedByDispatch = true;
                _incident.HasConvoyCustodyHandoff = false;
                _incident.ArrestSecured = true;
                foreach (Ped prisoner in valid)
                {
                    prisoner.IsPersistent = true;
                    prisoner.BlockPermanentEvents = true;
                    prisoner.CanSwitchWeapons = false;
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, prisoner, true);
                }
                SetState(LSPDDispatchState.Arrested);
                SetSuspectBlip(valid[0]);
                CaptureAndSetWaypoint(valid[0].Position);
                Notify("~y~PRISONER CUSTODY~s~\nTransport was interrupted. The arrested suspect group remains secured; request transport again when ready.");
                if (retainedDead.Count > 0)
                    LogRuntime(
                        "POLICE_CUSTODY_DEAD_PRISONER_RETAINED",
                        "Dead aftermath retained with Dispatch while living prisoners were recovered. Peds="
                        + string.Join(",", retainedDead.Select(ped => ped.Handle.ToString()).ToArray()));
                LogRuntime(
                    "POLICE_CUSTODY_RETURNED_TO_DISPATCH",
                    "Prisoners=" + string.Join(",", valid.Select(ped => ped.Handle.ToString()).ToArray())
                    + "; Reason=" + (reason ?? string.Empty));
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_CUSTODY_RECOVERY_FAILED", ex);
                return false;
            }
        }

        internal void SetAwaitingTransport()
        {
            if (_incident != null)
                SetState(LSPDDispatchState.AwaitingTransport);
        }

        internal void SetConvoyState(LSPDDispatchState state)
        {
            if (_incident == null || state == LSPDDispatchState.None)
                return;
            SetState(state);
        }

        internal string CompleteTransport()
        {
            if (_incident == null)
                return "No active Police dispatch remains.";
            if (!_incident.HasConvoyCustodyHandoff)
                return "No active prisoner convoy is linked to this Dispatch case.";
            Complete("Prisoner delivered and booking completed.");
            return "Prisoner delivered and booking completed. Dispatch completed.";
        }

        internal string Cancel(string reason)
        {
            if (_incident == null)
                return "No active dispatch.";

            LSPDDispatchEvent incident = _incident;
            bool preserveDeadAftermath = TrackedSuspects(incident)
                .Any(ped => ped != null && ped.Exists() && ped.IsDead);
            Terminate(
                incident,
                LSPDDispatchState.Cancelled,
                reason,
                "lsimmersivelife.police.dispatch.cancelled",
                "cancelled",
                preserveDeadAftermath);
            LogRuntime("POLICE_DISPATCH_CANCELLED", incident.Title + " | Reason=" + reason);
            return "Dispatch cancelled and Police-owned scene state cleaned.";
        }

        internal void Reset()
        {
            RestoreAllSuspectInjuryStates();
            if (_incident != null)
            {
                CloseActiveAudio(_incident);
                CleanupIncident(_incident, false);
                _incident = null;
            }
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
                DeleteDeferred(item);
            _deferredCleanup.Clear();
            CleanupBlipsAndWaypoint();
            _cooldownUntil = DateTime.MinValue;
            _nextOfferAt = DateTime.MinValue;
            _nextMaintenance = DateTime.MinValue;
            _playerHandcuffHoldUntil = DateTime.MinValue;
            _playerHandcuffAttempts = 0;
            _patrolSessionStarted = false;
            _acceptKeyDown = false;
            _rejectKeyDown = false;
            _investigateKeyDown = false;
            _secureKeyDown = false;
            _lastCompliantTaskAt.Clear();
            _lastSecuredPoseTaskAt.Clear();
        }

        private void MaintainIncident(DateTime now, Ped player)
        {
            if (_incident == null)
                return;

            // An offered call is only information. The scene must not start
            // streaming or create owned actors until the officer explicitly
            // accepts it; declining therefore leaves no world event behind.
            if (_incident.State == LSPDDispatchState.Offered)
                return;

            // GTA can clear the visible cuff/AI flags after an unrelated
            // collision or ambient task. Reassert them at the existing
            // maintenance cadence without issuing a new movement task; the
            // transport owner remains free to run the physical loading flow.
            MaintainPlayerHandcuff(now, player);
            if (_incident == null)
                return;

            // Convoy owns both the prisoner tasks and the transport entity
            // after Dispatch has explicitly handed custody over. In particular,
            // Dispatch must not keep reissuing robbery or pursuit tasks against
            // a handcuffed prisoner while the player is driving to station.
            if (_incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch)
                return;

            MaintainSecuredSuspects(now);
            MaintainCompliantSuspects(now);

            if (!_incident.ScenePreparationCompleted)
            {
                if (!EnsureScene(player))
                {
                    if (_incident == null)
                        return;
                    if (_incident.ScenePreparationRequested
                        && _incident.ScenePreparationDeadline != DateTime.MinValue
                        && now >= _incident.ScenePreparationDeadline)
                    {
                        Fail("The Dispatch scene could not finish loading safely.");
                    }
                    return;
                }
            }

            // The accepted Dispatch scene is a live world activity during
            // travel. Previously EnsureScene was reached only through the
            // manual Investigate command, leaving the suspect frozen until
            // the officer reopened Police UI at the destination. Start the
            // authored behavior once, after the owned entities exist; the
            // state guard inside StartSceneBehavior prevents task spam.
            if (_incident.ScenePreparationCompleted
                && !_incident.SceneBehaviorInitialized)
            {
                StartSceneBehavior(player);
                // StartSceneBehavior can fail and terminate an invalid scene
                // (for example, a paired event with fewer than two living
                // suspects). Terminate clears _incident, so stop this tick
                // before reading its suspect or state again.
                if (_incident == null || !_incident.OwnedByDispatch)
                    return;
            }

            ReportSceneApproach(player);

            Ped suspect = _incident.Suspect;
            if (suspect != null && suspect.Exists())
            {
                TryStartInteriorStoreRobberyFlight(player);
                MaintainStagedSceneBehavior(now);
                bool visibleCompliance = _incident.CompliantSuspects != null
                    && _incident.CompliantSuspects.Any(value => value != null && value.Exists()
                        && !value.IsDead && value.Health > 0
                        && !IsWoundedSuspect(value)
                        && !value.IsInCombatAgainst(player) && !value.IsShooting);
                if (_incident.SurrenderRequested && visibleCompliance &&
                    _incident.State != LSPDDispatchState.Arrested &&
                    _incident.State != LSPDDispatchState.AwaitingTransport &&
                    _incident.State != LSPDDispatchState.HoldingAtStation &&
                    _incident.State != LSPDDispatchState.PrisonTransfer &&
                    now >= _incident.SurrenderRequestedAt.AddSeconds(SurrenderSettleSeconds))
                {
                    SetState(LSPDDispatchState.SuspectCompliant);
                    Notify("~g~POLICE COMPLIANCE~s~\nSuspect has put their hands up. Secure the suspect now.");
                    ReportAudioStage("lsimmersivelife.police.compliance.confirmed", _incident, "compliant");
                }

                if ((_incident.State == LSPDDispatchState.SuspectFleeing ||
                     _incident.State == LSPDDispatchState.SuspectResisting) &&
                    _suspectBlip == null)
                    SetSuspectBlip(suspect);
                if (_suspectBlip != null && _suspectBlip.Exists())
                    _suspectBlip.Position = suspect.Position;

                bool interiorEscapeManaged = MaintainInteriorSuspectEscape(now, suspect, player);

                // Do not report a pursuit as physically fled until the target
                // has moved.  If the task stalled, reissue it at a low rate so
                // the vehicle/foot suspect remains an actual world actor.
                if (_incident.State == LSPDDispatchState.SuspectFleeing
                    && !IsWoundedSuspect(suspect))
                {
                    if (now >= _incident.LastMovementSampleAt.AddSeconds(2))
                    {
                        float moved = suspect.Position.DistanceTo(_incident.LastSuspectPosition);
                        if (moved >= 3f)
                        {
                            if (!_incident.SuspectMovementConfirmed)
                            {
                                _incident.SuspectMovementConfirmed = true;
                                LogRuntime("POLICE_DISPATCH_PURSUIT_MOVEMENT_CONFIRMED",
                                    "Type=" + _incident.IncidentType + "; Distance=" + moved.ToString("0.0", CultureInfo.InvariantCulture));
                            }
                        }
                        else if (!_incident.SuspectMovementConfirmed && !suspect.IsDead
                            && !interiorEscapeManaged)
                        {
                            try
                            {
                                Ped playerTarget = player != null && player.Exists() ? player : Game.Player.Character;
                                if (_incident.SuspectVehicle != null && _incident.SuspectVehicle.Exists() && playerTarget != null)
                                    suspect.Task.VehicleChase(playerTarget);
                                else if (playerTarget != null)
                                    suspect.Task.ReactAndFlee(playerTarget);
                            }
                            catch (Exception ex) { LogException("POLICE_DISPATCH_PURSUIT_REISSUE_FAILED", ex); }
                        }
                        _incident.LastSuspectPosition = suspect.Position;
                        _incident.LastMovementSampleAt = now;
                    }
                }
            }
        }

        private void TryStartInteriorStoreRobberyFlight(Ped player)
        {
            if (_incident == null
                || _incident.State != LSPDDispatchState.Investigating
                || !string.Equals(_incident.IncidentType, "store_robbery",
                    StringComparison.OrdinalIgnoreCase)
                || _incident.SceneResolution == null
                || !_incident.SceneResolution.IsInteriorScene
                || player == null || !player.Exists() || player.IsDead || player.IsInVehicle())
                return;

            int playerInteriorId;
            if (_ambientWorld == null
                || !_ambientWorld.TryGetActorInteriorId(player, out playerInteriorId)
                || playerInteriorId != _incident.SceneResolution.InteriorId)
                return;

            foreach (Ped suspect in LiveSuspects(_incident))
            {
                int suspectInteriorId;
                if (!_ambientWorld.TryGetActorInteriorId(suspect, out suspectInteriorId)
                    || suspectInteriorId != playerInteriorId
                    || player.Position.DistanceTo(suspect.Position) > SceneArrivalRadius)
                    continue;

                bool aimingAtSuspect;
                try
                {
                    aimingAtSuspect = Function.Call<bool>(
                        Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY,
                        Game.Player.Handle,
                        suspect);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_INTERIOR_AIM_CHECK_FAILED", ex);
                    return;
                }
                if (!aimingAtSuspect)
                    continue;

                SetState(LSPDDispatchState.SuspectFleeing);
                OrderAdditionalSuspectsToFlee(player);
                Notify("~r~POLICE DISPATCH~s~\nThe suspect is fleeing through the mapped entrance.");
                ReportAudioStage(
                    "lsimmersivelife.police.pursuit.continuing",
                    _incident,
                    "interior-robbery-flight");
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_ROBBERY_FLIGHT_TRIGGERED",
                    "Type=" + _incident.IncidentType
                    + "; Player=" + player.Handle
                    + "; Suspect=" + suspect.Handle
                    + "; InteriorId=" + playerInteriorId
                    + "; Trigger=PlayerAimingAtSuspect");
                return;
            }
        }

        private bool MaintainInteriorSuspectEscape(DateTime now, Ped primarySuspect, Ped player)
        {
            if (_incident == null || _ambientWorld == null
                || _incident.SceneResolution == null
                || !_incident.SceneResolution.IsInteriorScene)
                return false;

            if (_incident.State != LSPDDispatchState.SuspectFleeing)
            {
                Ped pendingActor = TrackedSuspects(_incident).FirstOrDefault(value =>
                    value.Handle == _incident.InteriorExitActorHandle);
                if (_incident.InteriorExitActorHandle != 0)
                {
                    if (pendingActor != null && pendingActor.Exists())
                        _ambientWorld.CancelInteriorAccess(
                            pendingActor,
                            "DispatchPursuitStateChanged");
                    ResetInteriorExitAttempt(_incident);
                }
                return false;
            }

            Ped exitingSuspect = null;
            if (_incident.InteriorExitActorHandle != 0)
                exitingSuspect = LiveSuspects(_incident).FirstOrDefault(value =>
                    value.Handle == _incident.InteriorExitActorHandle);
            if (exitingSuspect == null)
            {
                ResetInteriorExitAttempt(_incident);
                foreach (Ped candidate in LiveSuspects(_incident))
                {
                    if (_incident.InteriorExitCompletedHandles.Contains(candidate.Handle)
                        || _incident.InteriorExitFailedHandles.Contains(candidate.Handle)
                        || IsWoundedSuspect(candidate))
                        continue;

                    int candidateInteriorId;
                    if (!_ambientWorld.TryGetActorInteriorId(candidate, out candidateInteriorId))
                        continue;
                    if (candidateInteriorId == 0)
                    {
                        _incident.InteriorExitCompletedHandles.Add(candidate.Handle);
                        LogRuntime(
                            "POLICE_DISPATCH_INTERIOR_EXIT_OBSERVED",
                            "Suspect=" + candidate.Handle
                            + "; InteriorId=0; Crossing=ObservedFromLiveEntityContext");
                        continue;
                    }
                    if (candidateInteriorId != _incident.SceneResolution.InteriorId)
                        continue;

                    exitingSuspect = candidate;
                    _incident.InteriorExitActorHandle = candidate.Handle;
                    _incident.InteriorExitStartedAt = now;
                    _incident.InteriorExitRecoveryCount = 0;
                    break;
                }
            }

            if (exitingSuspect == null)
                return false;

            if (IsWoundedSuspect(exitingSuspect))
            {
                _ambientWorld.CancelInteriorAccess(exitingSuspect, "DispatchSuspectWounded");
                _incident.InteriorExitFailedHandles.Add(exitingSuspect.Handle);
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_EXIT_HELD_FOR_INJURY",
                    "Suspect=" + exitingSuspect.Handle
                    + "; State=InjuredOrDowned; InteriorId=" + _incident.SceneResolution.InteriorId);
                ResetInteriorExitAttempt(_incident);
                return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
            }

            int liveInteriorId;
            if (!_ambientWorld.TryGetActorInteriorId(exitingSuspect, out liveInteriorId))
            {
                RetryInteriorSuspectExit(exitingSuspect, "GTA could not identify the suspect's live interior.");
                return true;
            }
            if (liveInteriorId == 0 && _incident.InteriorExitPhase < 3)
            {
                // The actor physically reached an exterior context before this
                // route session started (for example, through another live
                // doorway). Do not claim the mapped door was used.
                _incident.InteriorExitCompletedHandles.Add(exitingSuspect.Handle);
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_EXIT_OBSERVED",
                    "Suspect=" + exitingSuspect.Handle
                    + "; Crossing=ObservedOutsideWithoutMappedDoorSession");
                ResetInteriorExitAttempt(_incident);
                return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
            }

            if (_incident.InteriorExitStartedAt != DateTime.MinValue
                && now >= _incident.InteriorExitStartedAt.AddSeconds(InteriorExitNavigationTimeoutSeconds)
                && _incident.InteriorExitPhase < 4)
            {
                RetryInteriorSuspectExit(exitingSuspect,
                    "The suspect did not physically reach the mapped exterior threshold in time.");
                return true;
            }

            if (_incident.InteriorExitPhase == 0)
            {
                if (_incident.InteriorExitNextTaskAt != DateTime.MinValue
                    && now < _incident.InteriorExitNextTaskAt)
                    return true;

                AmbientInteriorAccessRoute route;
                string routeFailure;
                if (!_ambientWorld.TryResolveInteriorAccessRouteForInterior(
                    exitingSuspect,
                    _incident.SceneResolution.InteriorId,
                    false,
                    out route,
                    out routeFailure))
                {
                    RetryInteriorSuspectExit(exitingSuspect, routeFailure);
                    return true;
                }

                _incident.InteriorExitRoute = route;
                _incident.InteriorExitPhase = 1;
                _incident.InteriorExitStartedAt = now;
                _incident.InteriorExitNextTaskAt = DateTime.MinValue;
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_EXIT_ROUTE_RESOLVED",
                    "Suspect=" + exitingSuspect.Handle
                    + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord
                    + "; InteriorId=" + route.TargetInteriorId
                    + "; Entry=" + route.EntryPosition
                    + "; Destination=" + route.DestinationPosition);
            }

            if (_incident.InteriorExitPhase == 1)
            {
                AmbientInteriorAccessRoute route = _incident.InteriorExitRoute;
                if (route == null)
                {
                    RetryInteriorSuspectExit(exitingSuspect, "The resolved interior exit route was lost.");
                    return true;
                }

                if (exitingSuspect.Position.DistanceTo(route.EntryPosition) > 1.35f)
                {
                    if (now >= _incident.InteriorExitNextTaskAt)
                    {
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                exitingSuspect,
                                route.EntryPosition.X,
                                route.EntryPosition.Y,
                                route.EntryPosition.Z,
                                2.2f,
                                -1,
                                0.75f,
                                1,
                                route.Pair.InsidePoint.HasHeading
                                    ? route.Pair.InsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, exitingSuspect, true);
                            _incident.InteriorExitNextTaskAt = now.AddSeconds(4);
                            LogRuntime(
                                "POLICE_DISPATCH_INTERIOR_EXIT_NAVIGATION_STARTED",
                                "Suspect=" + exitingSuspect.Handle
                                + "; Entry=" + route.EntryPosition
                                + "; Distance=" + exitingSuspect.Position.DistanceTo(route.EntryPosition).ToString("0.0", CultureInfo.InvariantCulture));
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_DISPATCH_INTERIOR_EXIT_NAVIGATION_FAILED", ex);
                            RetryInteriorSuspectExit(exitingSuspect, ex.Message);
                        }
                    }
                    return true;
                }

                string doorFailure;
                if (!_ambientWorld.TryBeginInteriorAccess(
                    exitingSuspect,
                    route,
                    out doorFailure))
                {
                    RetryInteriorSuspectExit(exitingSuspect, doorFailure);
                    return true;
                }
                _incident.InteriorExitPhase = 2;
                _incident.InteriorExitNextTaskAt = DateTime.MinValue;
                LogRuntime(
                    "POLICE_DISPATCH_INTERIOR_DOOR_OPENING_REQUESTED",
                    "Suspect=" + exitingSuspect.Handle
                    + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord
                    + "; DoorHashPending=true");
            }

            if (_incident.InteriorExitPhase == 2 || _incident.InteriorExitPhase == 3)
            {
                string accessMessage;
                AmbientInteriorAccessStatus accessStatus = _ambientWorld.MaintainInteriorAccess(
                    exitingSuspect,
                    out accessMessage);
                if (accessStatus == AmbientInteriorAccessStatus.Failed)
                {
                    RetryInteriorSuspectExit(exitingSuspect, accessMessage);
                    return true;
                }
                if (accessStatus == AmbientInteriorAccessStatus.ReadyToCross)
                {
                    _incident.InteriorExitPhase = 3;
                    if (now >= _incident.InteriorExitNextTaskAt)
                    {
                        AmbientInteriorAccessRoute route = _incident.InteriorExitRoute;
                        if (route == null)
                        {
                            RetryInteriorSuspectExit(exitingSuspect, "The door opened but its mapped route was lost.");
                            return true;
                        }
                        try
                        {
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                                exitingSuspect,
                                route.DestinationPosition.X,
                                route.DestinationPosition.Y,
                                route.DestinationPosition.Z,
                                2.2f,
                                -1,
                                0.35f,
                                1,
                                route.Pair.OutsidePoint.HasHeading
                                    ? route.Pair.OutsidePoint.Heading : 0f);
                            Function.Call(Hash.SET_PED_KEEP_TASK, exitingSuspect, true);
                            _incident.InteriorExitNextTaskAt = now.AddSeconds(3);
                            LogRuntime(
                                "POLICE_DISPATCH_INTERIOR_DOOR_CROSSING_TASK_STARTED",
                                "Suspect=" + exitingSuspect.Handle
                                + "; Destination=" + route.DestinationPosition
                                + "; DoorPair=" + route.Pair.InsidePoint.SourceRecord);
                        }
                        catch (Exception ex)
                        {
                            LogException("POLICE_DISPATCH_INTERIOR_DOOR_CROSSING_TASK_FAILED", ex);
                            RetryInteriorSuspectExit(exitingSuspect, ex.Message);
                            return true;
                        }
                    }
                    return true;
                }
                if (accessStatus == AmbientInteriorAccessStatus.DoorOpening)
                    return true;
                if (accessStatus == AmbientInteriorAccessStatus.Completed)
                {
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_DOOR_CROSSING_CONFIRMED",
                        "Suspect=" + exitingSuspect.Handle
                        + "; InteriorId=" + _incident.SceneResolution.InteriorId
                        + "; DoorPair=" + (_incident.InteriorExitRoute == null
                            ? string.Empty : _incident.InteriorExitRoute.Pair.InsidePoint.SourceRecord));
                    if (exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle)
                        && _incident.SuspectVehicle != null
                        && _incident.SuspectVehicle.Exists())
                    {
                        _incident.InteriorExitPhase = 4;
                        _incident.InteriorExitVehicleEntryStartedAt = now;
                        _incident.InteriorExitNextTaskAt = DateTime.MinValue;
                    }
                    else
                    {
                        CompleteInteriorSuspectExit(exitingSuspect, player);
                    }
                }
                else if (accessStatus == AmbientInteriorAccessStatus.None)
                {
                    RetryInteriorSuspectExit(exitingSuspect,
                        "The physical door session ended before the crossing was confirmed.");
                    return true;
                }
            }

            if (_incident.InteriorExitPhase == 4)
            {
                Vehicle escapeVehicle = _incident.SuspectVehicle;
                if (escapeVehicle == null || !escapeVehicle.Exists())
                {
                    CompleteInteriorSuspectExit(exitingSuspect, player);
                    return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
                }
                if (exitingSuspect.IsInVehicle()
                    && exitingSuspect.CurrentVehicle != null
                    && exitingSuspect.CurrentVehicle.Exists()
                    && exitingSuspect.CurrentVehicle.Handle == escapeVehicle.Handle)
                {
                    try
                    {
                        Ped target = player != null && player.Exists() ? player : Game.Player.Character;
                        if (target != null)
                            exitingSuspect.Task.VehicleChase(target);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_INTERIOR_GETAWAY_CHASE_FAILED", ex);
                    }
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_GETAWAY_VEHICLE_ENTERED",
                        "Suspect=" + exitingSuspect.Handle + "; Vehicle=" + escapeVehicle.Handle);
                    CompleteInteriorSuspectExit(exitingSuspect, player);
                    return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
                }

                if (now >= _incident.InteriorExitVehicleEntryStartedAt
                    .AddSeconds(InteriorExitVehicleEntryTimeoutSeconds))
                {
                    LogRuntime(
                        "POLICE_DISPATCH_INTERIOR_GETAWAY_ENTRY_TIMED_OUT",
                        "Suspect=" + exitingSuspect.Handle
                        + "; Vehicle=" + escapeVehicle.Handle
                        + "; Action=ContinueOnFoot");
                    CompleteInteriorSuspectExit(exitingSuspect, player);
                    return exitingSuspect.Handle == (primarySuspect == null ? 0 : primarySuspect.Handle);
                }

                if (now >= _incident.InteriorExitNextTaskAt)
                {
                    try
                    {
                        Function.Call(Hash.TASK_ENTER_VEHICLE,
                            exitingSuspect,
                            escapeVehicle,
                            InteriorExitVehicleEntryTimeoutSeconds * 1000,
                            -1,
                            2.0f,
                            1,
                            0);
                        Function.Call(Hash.SET_PED_KEEP_TASK, exitingSuspect, true);
                        _incident.InteriorExitNextTaskAt = now.AddSeconds(3);
                    }
                    catch (Exception ex)
                    {
                        LogException("POLICE_DISPATCH_INTERIOR_GETAWAY_ENTRY_FAILED", ex);
                        _incident.InteriorExitVehicleEntryStartedAt = now
                            .AddSeconds(-InteriorExitVehicleEntryTimeoutSeconds);
                    }
                }
                return true;
            }

            return _incident.InteriorExitActorHandle != 0;
        }

        private void RetryInteriorSuspectExit(Ped suspect, string reason)
        {
            if (_incident == null)
                return;
            if (suspect != null && suspect.Exists())
                _ambientWorld?.CancelInteriorAccess(suspect, "DispatchInteriorExitRetry");
            _incident.InteriorExitRecoveryCount++;
            bool exhausted = _incident.InteriorExitRecoveryCount >= InteriorExitMaximumRecoveries;
            int suspectHandle = suspect == null || !suspect.Exists() ? 0 : suspect.Handle;
            int recoveryCount = _incident.InteriorExitRecoveryCount;
            if (exhausted && suspectHandle != 0)
                _incident.InteriorExitFailedHandles.Add(suspectHandle);
            LogDebug(
                exhausted
                    ? "POLICE_DISPATCH_INTERIOR_EXIT_FAILED"
                    : "POLICE_DISPATCH_INTERIOR_EXIT_RETRY",
                "Suspect=" + suspectHandle
                + "; Recovery=" + _incident.InteriorExitRecoveryCount
                + "; Reason=" + (reason ?? string.Empty));
            ResetInteriorExitAttempt(_incident);
            if (!exhausted)
            {
                _incident.InteriorExitActorHandle = suspectHandle;
                _incident.InteriorExitRecoveryCount = recoveryCount;
                _incident.InteriorExitStartedAt = DateTime.UtcNow;
                _incident.InteriorExitNextTaskAt = DateTime.UtcNow.AddSeconds(1.5);
            }
        }

        private void CompleteInteriorSuspectExit(Ped suspect, Ped player)
        {
            if (_incident == null || suspect == null || !suspect.Exists())
                return;
            _incident.InteriorExitCompletedHandles.Add(suspect.Handle);
            if (_incident.SuspectVehicle == null || !_incident.SuspectVehicle.Exists()
                || suspect.Handle != (_incident.Suspect == null ? 0 : _incident.Suspect.Handle)
                || !suspect.IsInVehicle())
            {
                try
                {
                    Ped target = player != null && player.Exists() ? player : Game.Player.Character;
                    if (target != null && !suspect.IsDead && !IsWoundedSuspect(suspect))
                        suspect.Task.ReactAndFlee(target);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_INTERIOR_FOOT_PURSUIT_STARTED_FAILED", ex);
                }
            }
            LogRuntime(
                "POLICE_DISPATCH_INTERIOR_SUSPECT_EXIT_COMPLETED",
                "Suspect=" + suspect.Handle
                + "; LiveInteriorId=0; Vehicle="
                + (_incident.SuspectVehicle == null ? 0 : _incident.SuspectVehicle.Handle));
            ResetInteriorExitAttempt(_incident);
        }

        private static void ResetInteriorExitAttempt(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return;
            incident.InteriorExitRoute = null;
            incident.InteriorExitActorHandle = 0;
            incident.InteriorExitPhase = 0;
            incident.InteriorExitRecoveryCount = 0;
            incident.InteriorExitStartedAt = DateTime.MinValue;
            incident.InteriorExitNextTaskAt = DateTime.MinValue;
            incident.InteriorExitVehicleEntryStartedAt = DateTime.MinValue;
        }

        private void MaintainCompliantSuspects(DateTime now)
        {
            if (_incident == null || _incident.CompliantSuspects == null)
                return;

            foreach (Ped suspect in _incident.CompliantSuspects
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead
                    && !IsArrested(_incident, ped)))
            {
                if (_incident.PlayerHandcuffInProgress
                    && suspect.Handle == _incident.PlayerHandcuffTargetHandle)
                    continue;
                if (IsWoundedSuspect(suspect))
                    continue;
                try
                {
                    suspect.BlockPermanentEvents = true;
                    suspect.CanSwitchWeapons = false;
                    DateTime lastTask;
                    if (!_lastCompliantTaskAt.TryGetValue(suspect.Handle, out lastTask)
                        || now >= lastTask.AddMilliseconds(CompliantTaskRefreshMilliseconds))
                    {
                        suspect.Task.HandsUp(10000);
                        _lastCompliantTaskAt[suspect.Handle] = now;
                    }
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_COMPLIANCE_MAINTENANCE_FAILED", ex);
                }
            }
        }

        private void MaintainSecuredSuspects(DateTime now)
        {
            if (_incident == null || _incident.ArrestedSuspects == null)
                return;
            foreach (Ped suspect in _incident.ArrestedSuspects
                .Where(ped => ped != null && ped.Exists() && !ped.IsDead))
            {
                try
                {
                    suspect.BlockPermanentEvents = true;
                    suspect.CanSwitchWeapons = false;
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, suspect, true);
                    Function.Call(Hash.SET_ENTITY_PROOFS,
                        suspect, false, false, false, true, false, false, false, false);
                    if (!_incident.HasConvoyCustodyHandoff && !suspect.IsInVehicle())
                        MaintainHandcuffedPose(suspect, now, false);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_CUSTODY_MAINTENANCE_FAILED", ex);
                }
            }
        }

        private void MaintainHandcuffedPose(Ped suspect, DateTime now, bool force)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead || suspect.IsInVehicle())
                return;
            DateTime lastTask;
            bool hasPreviousAttempt = _lastSecuredPoseTaskAt.TryGetValue(
                suspect.Handle, out lastTask);
            if (!force && hasPreviousAttempt
                && now < lastTask.AddMilliseconds(SecuredPoseRefreshMilliseconds))
                return;

            const string handcuffDictionary = "mp_arresting";
            try
            {
                if (LSImmersiveDictionaryAnimation.IsPlaying(
                    suspect, handcuffDictionary, "idle"))
                {
                    _lastSecuredPoseTaskAt[suspect.Handle] = now;
                    return;
                }

                if (!LSImmersiveDictionaryAnimation.IsDictionaryLoaded(
                    handcuffDictionary))
                {
                    _lastSecuredPoseTaskAt[suspect.Handle] =
                        now.AddMilliseconds(750);
                    return;
                }

                float clipDuration;
                if (!LSImmersiveDictionaryAnimation.TryPlay(
                    suspect,
                    handcuffDictionary,
                    "idle",
                    3.0f,
                    -2.0f,
                    -1,
                    1,
                    1.0f,
                    out clipDuration))
                {
                    _lastSecuredPoseTaskAt[suspect.Handle] = now.AddMilliseconds(750);
                    if (!hasPreviousAttempt)
                        LogRuntime("POLICE_DISPATCH_HANDCUFF_CLIP_UNAVAILABLE",
                        "Ped=" + suspect.Handle + "; Dictionary=" + handcuffDictionary
                        + "; Clip=idle; Action=PhysicalCustodyRetained");
                    return;
                }
                _lastSecuredPoseTaskAt[suspect.Handle] = now;
                if (!hasPreviousAttempt || force)
                    LogRuntime("POLICE_DISPATCH_HANDCUFF_CLIP_REQUESTED",
                    "Ped=" + suspect.Handle + "; Dictionary=" + handcuffDictionary
                    + "; Clip=idle; Duration=" + clipDuration.ToString("0.000")
                    + "; Flags=1; PlaybackRate=1.0; TaskDurationMs=-1");
            }
            catch (Exception ex)
            {
                _lastSecuredPoseTaskAt[suspect.Handle] = now.AddMilliseconds(1000);
                LogException("POLICE_DISPATCH_HANDCUFF_POSE_FAILED", ex);
            }
        }

        private void MaintainStagedSceneBehavior(DateTime now)
        {
            if (_incident == null || !_incident.SceneBehaviorInitialized
                || _incident.SurrenderRequested
                || _incident.State != LSPDDispatchState.Investigating
                || _incident.Suspect == null
                || !_incident.Suspect.Exists()
                || IsWoundedSuspect(_incident.Suspect))
                return;

            string type = (_incident.IncidentType ?? string.Empty).ToLowerInvariant();
            if (type != "bank_robbery" && type != "store_robbery")
                return;

            if (_incident.SceneBehaviorStage == 1
                && now >= _incident.SceneBehaviorNextStepAt)
            {
                IssueRobberyMovement(now);
            }
            else if (_incident.SceneBehaviorStage == 2
                && now >= _incident.SceneBehaviorNextStepAt)
            {
                IssueRobberyThreatPose();
            }
        }

        private void IssueRobberyMovement(DateTime now)
        {
            if (_incident == null || _incident.Suspect == null
                || !_incident.Suspect.Exists())
                return;
            try
            {
                int index = 0;
                foreach (Ped suspect in LiveSuspects(_incident))
                {
                    if (IsWoundedSuspect(suspect))
                        continue;
                    float lateral = index == 0 ? 0f : index % 2 == 0 ? 2.2f : -2.2f;
                    Function.Call(
                        Hash.TASK_GO_STRAIGHT_TO_COORD,
                        suspect,
                        _incident.Origin.X + 1.5f,
                        _incident.Origin.Y + lateral,
                        _incident.Origin.Z,
                        1.0f,
                        -1,
                        0f,
                        0f);
                    index++;
                }
                _incident.SceneBehaviorStage = 2;
                _incident.SceneBehaviorNextStepAt = now.AddMilliseconds(1800);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_ROBBERY_MOVEMENT_FAILED", ex);
            }
        }

        private void IssueRobberyThreatPose()
        {
            if (_incident == null || _incident.Suspect == null
                || !_incident.Suspect.Exists())
                return;
            try
            {
                foreach (Ped suspect in LiveSuspects(_incident))
                {
                    if (IsWoundedSuspect(suspect)
                        || _incident.Victim == null || !_incident.Victim.Exists())
                        continue;
                    Function.Call(
                        Hash.TASK_AIM_GUN_AT_ENTITY,
                        suspect,
                        _incident.Victim,
                        -1,
                        true);
                }
                _incident.SceneBehaviorStage = 3;
                _incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_ROBBERY_THREAT_POSE_FAILED", ex);
            }
        }

        private void StartSceneBehavior(Ped player)
        {
            if (_incident == null || _incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch
                || _incident.Suspect == null || !_incident.Suspect.Exists() || _incident.Suspect.IsDead ||
                _incident.SceneBehaviorInitialized)
                return;

            bool pairedScene = string.Equals(
                    _incident.SceneBehavior, "paired_meeting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    _incident.SceneBehavior, "paired_exchange", StringComparison.OrdinalIgnoreCase);
            if (pairedScene)
            {
                if (player == null || !player.Exists()
                    || player.Position.DistanceTo(_incident.Origin)
                        > PairedSceneAnimationApproachDistance)
                    return;

                bool responseAlreadyStarted = _incident.SurrenderRequested
                    || _incident.PlayerHandcuffInProgress
                    || (_incident.CompliantSuspects != null
                        && _incident.CompliantSuspects.Any())
                    || (_incident.ArrestedSuspects != null
                        && _incident.ArrestedSuspects.Any())
                    || LiveSuspects(_incident).Any(suspect =>
                        suspect.IsInCombatAgainst(player) || suspect.IsShooting || suspect.IsFleeing);
                if (responseAlreadyStarted)
                {
                    _incident.SceneBehaviorInitialized = true;
                    LogRuntime(
                        "POLICE_DISPATCH_SCENE_ANIMATION_SKIPPED",
                        "Event=" + _incident.Id + "; Reason=Player response already began.");
                    return;
                }

                if (_incident.SceneAnimationDeadline == DateTime.MinValue)
                    _incident.SceneAnimationDeadline = DateTime.UtcNow.AddSeconds(
                        PairedSceneAnimationLoadTimeoutSeconds);

                List<Ped> actors = LiveSuspects(_incident).Take(2).ToList();
                if (actors.Count < 2)
                {
                    bool memberWasLost = TrackedSuspects(_incident).Any(suspect =>
                        suspect == null || !suspect.Exists() || suspect.IsDead);
                    if (memberWasLost)
                    {
                        _incident.SceneBehaviorInitialized = true;
                        LogRuntime(
                            "POLICE_DISPATCH_SCENE_ANIMATION_SKIPPED",
                            "Event=" + _incident.Id + "; Reason=A group member was already lost.");
                        return;
                    }
                    Fail("The paired alley scene could not start because two living suspects were not available.");
                    return;
                }

                string animationReason = _ambientWorld == null
                    ? "AmbientWorld is not attached by PoliceCore."
                    : string.Empty;
                AmbientDispatchAnimationStartResult animation = _ambientWorld == null
                    ? AmbientDispatchAnimationStartResult.Failed
                    : _ambientWorld.TryStartDispatchPairAnimation(
                        _incident,
                        actors[0],
                        actors[1],
                        out animationReason);

                if (animation == AmbientDispatchAnimationStartResult.Pending
                    && DateTime.UtcNow < _incident.SceneAnimationDeadline)
                    return;
                if (animation != AmbientDispatchAnimationStartResult.Started)
                {
                    Fail("The paired alley scene could not start its authored animation. "
                        + animationReason);
                    return;
                }

                _incident.SceneBehaviorInitialized = true;
                _incident.SceneBehaviorStage = 1;
                _incident.SceneBehaviorNextStepAt = DateTime.MinValue;
                foreach (Ped suspect in LiveSuspects(_incident))
                    suspect.BlockPermanentEvents = true;
                SetState(LSPDDispatchState.Investigating);
                LogRuntime(
                    "POLICE_DISPATCH_SCENE_ANIMATION_STARTED",
                    "Event=" + _incident.Id
                    + "; Behavior=" + _incident.SceneBehavior
                    + "; Dictionary=" + _incident.SceneAnimationDictionary
                    + "; Actors=" + actors[0].Handle + "," + actors[1].Handle);
                return;
            }

            _incident.SceneBehaviorInitialized = true;
            _incident.SceneBehaviorStage = 0;
            _incident.SceneBehaviorNextStepAt = DateTime.MinValue;
            string type = (_incident.IncidentType ?? string.Empty).ToLowerInvariant();
            try
            {
                foreach (Ped suspect in LiveSuspects(_incident))
                    suspect.BlockPermanentEvents = true;

                // Robbery callouts are a live scene at the location. Let the
                // suspect finish leaving the escape vehicle before assigning
                // the walk and threat pose; consecutive tasks would otherwise
                // cancel the physical robbery setup on the same tick.
                if (type == "bank_robbery" || type == "store_robbery")
                {
                    bool leavingVehicle = _incident.SuspectVehicle != null
                        && _incident.SuspectVehicle.Exists()
                        && _incident.Suspect.IsInVehicle();
                    if (leavingVehicle)
                    {
                        Function.Call(
                            Hash.TASK_LEAVE_VEHICLE,
                            _incident.Suspect,
                            _incident.SuspectVehicle,
                            0);
                        _incident.SceneBehaviorStage = 1;
                        _incident.SceneBehaviorNextStepAt =
                            DateTime.UtcNow.AddMilliseconds(1200);
                    }
                    else
                    {
                        IssueRobberyMovement(DateTime.UtcNow);
                    }
                    SetState(LSPDDispatchState.Investigating);
                    ReportAudioStage(
                        "lsimmersivelife.police.scene.active",
                        _incident,
                        "robbery-active",
                        true);
                }
                else if (type == "kidnapping")
                {
                    bool interiorScene = _incident.SceneResolution != null
                        && _incident.SceneResolution.IsInteriorScene;
                    if (!interiorScene)
                    {
                        if (_incident.SuspectVehicle != null
                            && _incident.SuspectVehicle.Exists())
                            _incident.Suspect.Task.VehicleChase(player);
                        else
                            _incident.Suspect.Task.ReactAndFlee(player);
                    }
                    OrderAdditionalSuspectsToFlee(player);
                    SetState(LSPDDispatchState.SuspectFleeing);
                    ReportAudioStage(
                        "lsimmersivelife.police.pursuit.continuing",
                        _incident,
                        "kidnapping-pursuit",
                        true);
                }
                else if (type == "carjacking")
                {
                    bool interiorScene = _incident.SceneResolution != null
                        && _incident.SceneResolution.IsInteriorScene;
                    if (!interiorScene)
                    {
                        if (_incident.SuspectVehicle != null
                            && _incident.SuspectVehicle.Exists())
                            _incident.Suspect.Task.VehicleChase(player);
                        else
                            _incident.Suspect.Task.ReactAndFlee(player);
                    }
                    OrderAdditionalSuspectsToFlee(player);
                    SetState(LSPDDispatchState.SuspectFleeing);
                    ReportAudioStage(
                        "lsimmersivelife.police.pursuit.continuing",
                        _incident,
                        "carjacking-pursuit",
                        true);
                }
                else if (_incident.Mobile)
                {
                    bool interiorScene = _incident.SceneResolution != null
                        && _incident.SceneResolution.IsInteriorScene;
                    if (!interiorScene)
                    {
                        if (_incident.SuspectVehicle != null
                            && _incident.SuspectVehicle.Exists())
                            _incident.Suspect.Task.VehicleChase(player);
                        else
                            _incident.Suspect.Task.ReactAndFlee(player);
                    }
                    OrderAdditionalSuspectsToFlee(player);
                    SetState(LSPDDispatchState.SuspectFleeing);
                    ReportAudioStage(
                        "lsimmersivelife.police.pursuit.continuing",
                        _incident,
                        "pursuit",
                        true);
                }
                else if (_incident.Armed || _incident.Severity >= 4)
                {
                    foreach (Ped suspect in LiveSuspects(_incident))
                        suspect.Task.Combat(player);
                    SetState(LSPDDispatchState.SuspectResisting);
                    ReportAudioStage(
                        "lsimmersivelife.police.resistance.resisting",
                        _incident,
                        "resisting",
                        true);
                }
                else
                {
                    foreach (Ped suspect in LiveSuspects(_incident))
                    {
                        suspect.Task.LookAt(player, 2500);
                        PlaySceneActivity(suspect, type);
                    }
                    SetState(LSPDDispatchState.Investigating);
                }
                LogRuntime(
                    "POLICE_DISPATCH_SCENE_STARTED",
                    _incident.Title + " | State=" + _incident.State
                    + " | Behavior=" + type);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SCENE_BEHAVIOR_FAILED", ex);
            }
        }
        /// <summary>
        /// Begins model streaming without Script.Wait. This method is safe for
        /// a LemonUI action because it only asks GTA to load assets and lets the
        /// normal PoliceCore tick finish scene creation later.
        /// </summary>
        private bool BeginScenePreparation(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return false;
            Model pedModel = new Model(incident.SuspectModel);
            Model vehicleModel = new Model(incident.VehicleModel);
            if (!IsUsableModel(pedModel, false)
                || (incident.RequiresVehicle && !IsUsableModel(vehicleModel, true)))
                return false;
            try
            {
                if (!pedModel.IsLoaded)
                    pedModel.Request();
                if (incident.RequiresVehicle && !vehicleModel.IsLoaded)
                    vehicleModel.Request();
                foreach (string candidateName in RequiredAdditionalSuspectModels(incident))
                {
                    Model candidate = new Model(candidateName);
                    if (IsUsableModel(candidate, false) && !candidate.IsLoaded)
                    {
                        // Keep this asynchronous request alive until the full
                        // authored group can be created or the incident reaches
                        // terminal cleanup. Releasing it here reduced group
                        // Dispatch scenes to whichever primary model happened
                        // to be loaded on the next tick.
                        candidate.Request();
                    }
                }
                incident.ScenePreparationRequested = true;
                incident.ScenePreparationDeadline = DateTime.UtcNow.AddSeconds(
                    ScenePreparationTimeoutSeconds);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SCENE_PREPARATION_FAILED", ex);
                return false;
            }
        }

        private void OrderAdditionalSuspectsToFlee(Ped player)
        {
            if (_incident == null || player == null || !player.Exists())
                return;
            foreach (Ped suspect in LiveSuspects(_incident))
            {
                if (_incident.Suspect != null && suspect.Handle == _incident.Suspect.Handle)
                    continue;
                if (_incident.SceneResolution != null
                    && _incident.SceneResolution.IsInteriorScene
                    && _ambientWorld != null)
                {
                    int suspectInteriorId;
                    if (_ambientWorld.TryGetActorInteriorId(suspect, out suspectInteriorId)
                        && suspectInteriorId == _incident.SceneResolution.InteriorId)
                        continue;
                }
                try { suspect.Task.ReactAndFlee(player); }
                catch (Exception ex) { LogException("POLICE_DISPATCH_GROUP_FLEE_TASK_FAILED", ex); }
            }
        }

        private bool EnsureScene(Ped player)
        {
            if (_incident == null)
                return false;
            if (_incident.Suspect != null && _incident.Suspect.Exists())
            {
                _incident.ScenePreparationCompleted = true;
                return true;
            }

            if (!_incident.ScenePreparationRequested
                && !BeginScenePreparation(_incident))
                return false;
            if (!AreAdditionalSuspectModelsLoaded(_incident))
                return false;

            AmbientDispatchSceneResolution refreshedScene;
            string sceneValidationFailure = "AmbientWorld is not attached by PoliceCore.";
            if (_ambientWorld == null
                || !_ambientWorld.TryRevalidateDispatchScene(
                    _incident.SceneResolution,
                    player == null || !player.Exists()
                        ? _incident.Origin : player.Position,
                    _incident.RequiresVehicle,
                    out refreshedScene,
                    out sceneValidationFailure))
            {
                LogDebug(
                    "POLICE_DISPATCH_SCENE_REVALIDATION_FAILED",
                    "Event=" + _incident.Id + "; Reason=" + sceneValidationFailure);
                return false;
            }
            _incident.SceneResolution = refreshedScene;
            _incident.Origin = refreshedScene.SceneCenter;

            Model pedModel = new Model(_incident.SuspectModel);
            Model vehicleModel = new Model(_incident.VehicleModel);
            bool pedLoaded = false;
            bool vehicleLoaded = false;
            try
            {
                if (!RequestModel(pedModel, false))
                    return false;
                pedLoaded = true;
                if (_incident.RequiresVehicle && !RequestModel(vehicleModel, true))
                    return false;
                vehicleLoaded = _incident.RequiresVehicle;

                Vector3 spawn = refreshedScene.ActorPosition;

                if (_incident.RequiresVehicle)
                {
                    if (!refreshedScene.HasVehicleStagingPosition)
                        return false;
                    Vehicle vehicle = World.CreateVehicle(
                        vehicleModel,
                        refreshedScene.VehicleStagingPosition,
                        refreshedScene.VehicleHeading);
                    if (vehicle == null || !vehicle.Exists())
                        return false;
                    vehicle.IsPersistent = true;
                    vehicle.PlaceOnGround();
                    _incident.SuspectVehicle = vehicle;
                    _incident.OwnedByDispatch = true;
                    Ped suspect = refreshedScene.IsInteriorScene
                        ? World.CreatePed(pedModel, spawn)
                        : vehicle.CreatePedOnSeat(VehicleSeat.Driver, pedModel);
                    if (suspect == null || !suspect.Exists())
                    {
                        vehicle.Delete();
                        _incident.SuspectVehicle = null;
                        return false;
                    }
                    suspect.IsPersistent = true;
                    suspect.BlockPermanentEvents = true;
                    _incident.Suspect = suspect;
                    RegisterSuspect(_incident, suspect);
                    ApplySceneWeapon(suspect);
                    CreateSceneVictim(_incident, spawn, pedModel);
                }
                else
                {
                    Ped suspect = World.CreatePed(pedModel, spawn);
                    if (suspect == null || !suspect.Exists())
                        return false;
                    suspect.IsPersistent = true;
                    suspect.BlockPermanentEvents = true;
                    _incident.Suspect = suspect;
                    RegisterSuspect(_incident, suspect);
                    _incident.OwnedByDispatch = true;
                    ApplySceneWeapon(suspect);
                    CreateSceneVictim(_incident, spawn, pedModel);
                }

                CreateAdditionalSuspects(_incident, spawn, pedModel);

                SetSuspectBlip(_incident.Suspect);
                _incident.ScenePreparationCompleted = true;
                StartSceneBehavior(player);
                if (_incident == null || !_incident.OwnedByDispatch)
                    return false;
                LogRuntime(
                    "POLICE_DISPATCH_SCENE_ENTITIES_CREATED",
                    "Ped=" + _incident.Suspect.Handle
                    + "; Suspects=" + _incident.Suspects.Count +
                    "; Vehicle=" + (_incident.SuspectVehicle == null ? 0 : _incident.SuspectVehicle.Handle)
                    + "; Interior=" + refreshedScene.IsInteriorScene
                    + "; InteriorId=" + refreshedScene.InteriorId
                    + "; Location=" + refreshedScene.LocationId);
                return true;
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SCENE_CREATE_FAILED", ex);
                CleanupIncident(_incident, false);
                return false;
            }
            finally
            {
                if (pedLoaded) pedModel.MarkAsNoLongerNeeded();
                if (vehicleLoaded) vehicleModel.MarkAsNoLongerNeeded();
            }
        }

        private void ApplySceneWeapon(Ped suspect, int suspectIndex = 0)
        {
            if (suspect == null || !suspect.Exists() || !_incident.Armed)
                return;
            try
            {
                string weaponName = _incident.WeaponName;
                if (_incident.WeaponNameCandidates != null
                    && _incident.WeaponNameCandidates.Count > 0)
                    weaponName = _incident.WeaponNameCandidates[
                        suspectIndex % _incident.WeaponNameCandidates.Count];
                if (string.IsNullOrWhiteSpace(weaponName))
                    return;
                int hash = unchecked((int)StringHash.AtStringHash(weaponName, 0));
                Function.Call(Hash.GIVE_WEAPON_TO_PED, suspect, hash, 120, false, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, suspect, hash, true);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_SCENE_WEAPON_FAILED", ex);
            }
        }

        private void CreateSceneVictim(LSPDDispatchEvent incident, Vector3 spawn, Model fallbackModel)
        {
            if (incident == null || incident.Victim != null ||
                (incident.IncidentType != "carjacking" && incident.IncidentType != "bank_robbery" &&
                 incident.IncidentType != "store_robbery" && incident.IncidentType != "kidnapping" &&
                 incident.IncidentType != "violent_disturbance"))
                return;
            try
            {
                Vector3 preferredVictimPosition = spawn + new Vector3(-2f, -1f, 0f);
                Vector3 victimPosition;
                string validationReason = "AmbientWorld is not attached by PoliceCore.";
                if (_ambientWorld == null
                    || !_ambientWorld.TryResolveDispatchParticipantPosition(
                        preferredVictimPosition,
                        incident.SceneResolution,
                        out victimPosition,
                        out validationReason))
                {
                    LogDebug(
                        "POLICE_DISPATCH_VICTIM_POSITION_REJECTED",
                        "Type=" + incident.IncidentType + "; Reason="
                        + (_ambientWorld == null
                            ? "AmbientWorld is not attached by PoliceCore."
                            : validationReason));
                    return;
                }

                Ped victim = World.CreatePed(fallbackModel, victimPosition);
                if (victim == null || !victim.Exists())
                    return;
                victim.IsPersistent = true;
                victim.BlockPermanentEvents = true;
                // Register ownership before assigning optional scene tasks so
                // a failed animation or vehicle-seat native still gets cleaned.
                incident.Victim = victim;
                victim.Task.LookAt(Game.Player.Character, 3000);
                if (incident.IncidentType == "carjacking" || incident.IncidentType == "bank_robbery" || incident.IncidentType == "store_robbery" || incident.IncidentType == "kidnapping")
                    victim.Task.HandsUp(8000);
                if (incident.IncidentType == "kidnapping"
                    && (incident.SceneResolution == null
                        || !incident.SceneResolution.IsInteriorScene)
                    && incident.SuspectVehicle != null
                    && incident.SuspectVehicle.Exists())
                {
                    victim.SetIntoVehicle(incident.SuspectVehicle, VehicleSeat.RightRear);
                    victim.BlockPermanentEvents = true;
                }
                LogRuntime("POLICE_DISPATCH_SCENE_VICTIM_CREATED", "Type=" + incident.IncidentType + "; Ped=" + victim.Handle);
            }
            catch (Exception ex) { LogException("POLICE_DISPATCH_VICTIM_CREATE_FAILED", ex); }
        }

        private void CreateAdditionalSuspects(
            LSPDDispatchEvent incident,
            Vector3 spawn,
            Model fallbackModel)
        {
            if (incident == null || fallbackModel == null || !fallbackModel.IsLoaded)
                return;
            int required = Math.Max(1, incident.SuspectCount);
            for (int index = incident.Suspects == null ? 0 : incident.Suspects.Count;
                index < required;
                index++)
            {
                Model suspectModel = null;
                try
                {
                    bool pairedScene = string.Equals(
                            incident.SceneBehavior, "paired_meeting", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            incident.SceneBehavior, "paired_exchange", StringComparison.OrdinalIgnoreCase);
                    Vector3 offset;
                    if (pairedScene && index == 1)
                    {
                        // Start the second participant within hand-off range of
                        // the primary instead of spreading the pair like props.
                        offset = new Vector3(1.55f, 0f, 0f);
                    }
                    else if (pairedScene && index == 2)
                    {
                        // A third participant stands nearby as a lookout/witness.
                        offset = new Vector3(0.4f, 2.4f, 0f);
                    }
                    else
                    {
                        float angle = (float)(index * Math.PI * 2.0 / Math.Max(2, required));
                        offset = new Vector3(
                            (float)Math.Cos(angle) * 3.5f,
                            (float)Math.Sin(angle) * 3.5f,
                            0f);
                    }
                    string modelName = SuspectModelForIndex(incident, index);
                    suspectModel = new Model(modelName);
                    if (!RequestModel(suspectModel, false))
                        continue;
                    Vector3 preferredSuspectPosition = spawn + offset;
                    Vector3 suspectPosition;
                    string validationReason = "AmbientWorld is not attached by PoliceCore.";
                    if (_ambientWorld == null
                        || !_ambientWorld.TryResolveDispatchParticipantPosition(
                            preferredSuspectPosition,
                            incident.SceneResolution,
                            out suspectPosition,
                            out validationReason))
                    {
                        LogDebug(
                            "POLICE_DISPATCH_GROUP_POSITION_REJECTED",
                            "Type=" + incident.IncidentType + "; Index=" + index
                            + "; Reason=" + (_ambientWorld == null
                                ? "AmbientWorld is not attached by PoliceCore."
                                : validationReason));
                        continue;
                    }

                    Ped suspect = World.CreatePed(suspectModel, suspectPosition);
                    if (suspect == null || !suspect.Exists())
                        continue;
                    suspect.IsPersistent = true;
                    suspect.BlockPermanentEvents = true;
                    ApplySceneWeapon(suspect, index);
                    RegisterSuspect(incident, suspect);
                    LogRuntime(
                        "POLICE_DISPATCH_GROUP_SUSPECT_CREATED",
                        "Type=" + incident.IncidentType + "; Ped=" + suspect.Handle
                        + "; Index=" + (index + 1) + "; Total=" + required);
                }
                catch (Exception ex)
                {
                    LogException("POLICE_DISPATCH_GROUP_SUSPECT_FAILED", ex);
                }
                finally
                {
                    if (suspectModel != null)
                    {
                        try { suspectModel.MarkAsNoLongerNeeded(); } catch { }
                    }
                }
            }
        }

        private static bool AreAdditionalSuspectModelsLoaded(
            LSPDDispatchEvent incident)
        {
            bool ready = true;
            foreach (string modelName in RequiredAdditionalSuspectModels(incident))
            {
                Model model = new Model(modelName);
                if (!IsUsableModel(model, false))
                    continue;
                if (!model.IsLoaded)
                {
                    model.Request();
                    ready = false;
                }
            }
            return ready;
        }

        private static IEnumerable<string> RequiredAdditionalSuspectModels(
            LSPDDispatchEvent incident)
        {
            if (incident == null)
                yield break;
            int required = Math.Max(1, incident.SuspectCount);
            for (int index = 1; index < required; index++)
                yield return SuspectModelForIndex(incident, index);
        }

        private static string SuspectModelForIndex(
            LSPDDispatchEvent incident,
            int index)
        {
            string modelName = incident == null
                ? string.Empty : incident.SuspectModel;
            if (incident == null || incident.SuspectModelCandidates == null
                || incident.SuspectModelCandidates.Count == 0)
                return modelName;
            string candidateName = incident.SuspectModelCandidates[
                Math.Max(0, index) % incident.SuspectModelCandidates.Count];
            Model candidate = new Model(candidateName);
            return IsUsableModel(candidate, false) ? candidateName : modelName;
        }

        private static void RegisterSuspect(LSPDDispatchEvent incident, Ped suspect)
        {
            if (incident == null || suspect == null || !suspect.Exists())
                return;
            if (incident.Suspects == null)
                return;
            if (!incident.Suspects.Any(value => value != null && value.Exists()
                && value.Handle == suspect.Handle))
                incident.Suspects.Add(suspect);
        }

        private static IEnumerable<Ped> LiveSuspects(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return Enumerable.Empty<Ped>();
            if (incident.Suspects != null && incident.Suspects.Count > 0)
                return incident.Suspects.Where(ped => ped != null && ped.Exists()
                    && !ped.IsDead && ped.Health > 0);
            return incident.Suspect != null && incident.Suspect.Exists()
                && !incident.Suspect.IsDead && incident.Suspect.Health > 0
                ? new[] { incident.Suspect }
                : Enumerable.Empty<Ped>();
        }

        private static IEnumerable<Ped> TrackedSuspects(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return Enumerable.Empty<Ped>();
            if (incident.Suspects != null && incident.Suspects.Count > 0)
                return incident.Suspects.Where(ped => ped != null && ped.Exists());
            return incident.Suspect != null && incident.Suspect.Exists()
                ? new[] { incident.Suspect }
                : Enumerable.Empty<Ped>();
        }
        private static void PlaySceneActivity(Ped suspect, string type)
        {
            if (suspect == null || !suspect.Exists())
                return;
            try
            {
                string scenario = string.Equals(type, "disturbance", StringComparison.OrdinalIgnoreCase)
                    ? "WORLD_HUMAN_STAND_IMPATIENT"
                    : string.Equals(type, "shots_fired", StringComparison.OrdinalIgnoreCase)
                        ? "WORLD_HUMAN_SMOKING"
                        : null;
                if (!string.IsNullOrWhiteSpace(scenario))
                    Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, suspect, scenario, 0, true);
            }
            catch { }
        }

        private static bool RequestModel(Model model, bool vehicle)
        {
            if (!IsUsableModel(model, vehicle))
                return false;
            if (!model.IsLoaded)
                model.Request();
            return model.IsLoaded;
        }

        private static bool IsUsableModel(Model model, bool vehicle)
        {
            return model != null && model.IsValid && model.IsInCdImage
                && (vehicle ? model.IsVehicle : model.IsPed);
        }

        private LSPDDispatchEventDefinition ChooseDefinition(Ped player)
        {
            if (_definitions.Count == 0 || player == null || !player.Exists())
                return null;

            DateTime repeatAfter = DateTime.UtcNow.AddMinutes(
                -_settings.RepeatProtectionMinutes);
            List<LSPDDispatchEventDefinition> eligible = _definitions
                .Where(definition =>
                {
                    if (!definition.Enabled)
                        return false;
                    DateTime lastOffered;
                    bool outsideRepeatWindow = !_definitionOfferedAt.TryGetValue(
                        definition.Id, out lastOffered) || lastOffered <= repeatAfter;
                    return outsideRepeatWindow
                        && definition.LocationIds != null
                        && definition.LocationIds.Any(locationId =>
                        {
                            LSPDDispatchLocationDefinition area;
                            return _locations.TryGetValue(locationId, out area)
                                && definition.CanUseArea(area, player.Position);
                        });
                })
                .ToList();
            if (eligible.Count == 0)
            {
                LogRuntime(
                    "POLICE_DISPATCH_NO_LOCATION_ELIGIBLE",
                    "No Dispatch event matches the Player's distance, authored area, and location context.");
                return null;
            }
            return eligible[_random.Next(0, eligible.Count)];
        }

        private void SetState(LSPDDispatchState state)
        {
            if (_incident == null || _incident.State == state)
                return;

            LSDeveloperRuntime.StateTransition(
                _log == null ? string.Empty : _log.SessionId,
                "Dispatch",
                "POLICE_DISPATCH_STATE",
                _incident.State.ToString(),
                state.ToString(),
                _incident.Suspect == null ? 0 : _incident.Suspect.Handle,
                _incident.Suspect == null ? 0 : _incident.Suspect.Handle,
                _incident.SuspectVehicle == null ? 0 : _incident.SuspectVehicle.Handle,
                "Type=" + _incident.IncidentType + "; Title=" + _incident.Title);

            _incident.State = state;
            _incident.StateChangedAt = DateTime.UtcNow;
            LogRuntime(
                "POLICE_DISPATCH_STATE",
                "State=" + state + " | Type=" + _incident.IncidentType + " | Title=" + _incident.Title);
        }

        private void Complete(string reason)
        {
            if (_incident == null)
                return;
            LSPDDispatchEvent incident = _incident;
            Terminate(
                incident,
                LSPDDispatchState.Completed,
                reason,
                "lsimmersivelife.police.dispatch.closed",
                "completed",
                true);
            Notify("~g~POLICE DISPATCH~s~\n" + reason + "\n~c~Unit clear.");
            LogRuntime("POLICE_DISPATCH_COMPLETED", incident.Title + " | " + reason);
        }

        private void Fail(string reason)
        {
            if (_incident == null)
                return;
            LSPDDispatchEvent incident = _incident;
            bool preserveDeadAftermath = TrackedSuspects(incident)
                .Any(ped => ped != null && ped.Exists() && ped.IsDead);
            string diagnostic = "Title=" + (incident.Title ?? string.Empty)
                + "; Id=" + (incident.Id ?? string.Empty)
                + "; Type=" + (incident.IncidentType ?? string.Empty)
                + "; State=" + incident.State
                + "; OwnedByDispatch=" + incident.OwnedByDispatch
                + "; Suspect=" + DescribePed(incident.Suspect)
                + "; Vehicle=" + DescribeVehicle(incident.SuspectVehicle)
                + "; Reason=" + (reason ?? string.Empty);
            Terminate(
                incident,
                LSPDDispatchState.Failed,
                reason,
                "lsimmersivelife.police.dispatch.cancelled",
                "failed",
                preserveDeadAftermath);
            Notify("~r~POLICE DISPATCH~s~\n" + reason);
            LogStateFailure("POLICE_DISPATCH_FAILED", diagnostic);
        }

        /// <summary>
        /// Closes the live assignment exactly once. Terminal audio uses a fresh
        /// scope after stale progress speech has been cancelled, so an old
        /// "received" or "en route" line cannot play after the case ended.
        /// </summary>
        private LSPDAudioResult Terminate(
            LSPDDispatchEvent incident,
            LSPDDispatchState state,
            string reason,
            string terminalAudioEvent,
            string terminalStage,
            bool deferSceneCleanup)
        {
            if (incident == null || _incident != incident)
                return LSPDAudioResult.Inactive;

            CancelIncidentInteriorAccess(incident, "DispatchTerminated");
            RestoreAllSuspectInjuryStates();
            SetState(state);
            if (deferSceneCleanup)
                QueueCleanup(incident);
            else
                CleanupIncident(incident, false);
            CleanupBlipsAndWaypoint();
            _lastCompliantTaskAt.Clear();
            _lastSecuredPoseTaskAt.Clear();
            _incident = null;
            ScheduleNextOffer(DateTime.UtcNow);
            LSPDAudioResult audio = ReportTerminalAudio(
                terminalAudioEvent,
                incident,
                terminalStage);
            LogRuntime(
                "POLICE_DISPATCH_TERMINAL",
                "State=" + state + "; Title=" + incident.Title
                + "; Reason=" + (reason ?? string.Empty));
            return audio;
        }

        private void CancelIncidentInteriorAccess(LSPDDispatchEvent incident, string reason)
        {
            if (_ambientWorld == null || incident == null)
                return;
            foreach (Ped suspect in TrackedSuspects(incident))
                if (suspect != null)
                    _ambientWorld.CancelInteriorAccess(suspect, reason);
            if (incident.Victim != null)
                _ambientWorld.CancelInteriorAccess(incident.Victim, reason);
            if (incident.AdditionalParticipants != null)
                foreach (Ped participant in incident.AdditionalParticipants)
                    if (participant != null)
                        _ambientWorld.CancelInteriorAccess(participant, reason);
            ResetInteriorExitAttempt(incident);
        }

        private void QueueCleanup(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return;
            ReleaseIncidentModelRequests(incident);
            DateTime now = DateTime.UtcNow;

            // The arrested suspect and escape vehicle move to Convoy ownership
            // during custody. Scene support actors remain Dispatch-owned and
            // receive the same grace/distance cleanup after the case closes.
            if (incident.OwnedByDispatch)
            {
                foreach (Ped suspect in TrackedSuspects(incident)
                    .Where(ped => ped != null && ped.Exists()))
                {
                    bool deadAftermath = suspect.IsDead;
                    _deferredCleanup.Add(new DeferredCleanup
                    {
                        Ped = suspect,
                        Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds),
                        PreserveUntilPlayerLeavesArea = deadAftermath
                    });
                    if (deadAftermath)
                        LogRuntime("POLICE_DISPATCH_DEAD_SCENE_RETAINED",
                            "Ped=" + suspect.Handle
                            + "; Cleanup=AfterPlayerLeavesSafeRadius"
                            + "; Radius=" + DeferredCleanupDistance.ToString("0.0"));
                }
                if (incident.SuspectVehicle != null && incident.SuspectVehicle.Exists())
                    _deferredCleanup.Add(new DeferredCleanup
                    {
                        Vehicle = incident.SuspectVehicle,
                        Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds)
                    });
            }
            if (incident.Victim != null && incident.Victim.Exists())
                _deferredCleanup.Add(new DeferredCleanup
                {
                    Ped = incident.Victim,
                    Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                    Expires = now.AddSeconds(DeferredCleanupMaximumSeconds)
                });
            foreach (Ped participant in incident.AdditionalParticipants)
                if (participant != null && participant.Exists())
                    _deferredCleanup.Add(new DeferredCleanup
                    {
                        Ped = participant,
                        Earliest = now.AddSeconds(DeferredCleanupGraceSeconds),
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds)
                    });
        }
        private void ProcessDeferredCleanup(DateTime now)
        {
            Ped player = Game.Player.Character;
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
                bool playerLeftActiveArea = distance >= DeferredCleanupDistance;
                bool ordinaryCleanupExpired = !item.PreserveUntilPlayerLeavesArea
                    && now >= item.Expires;
                if (now >= item.Earliest && (playerLeftActiveArea || ordinaryCleanupExpired))
                {
                    if (item.PreserveUntilPlayerLeavesArea)
                        LogRuntime("POLICE_DISPATCH_DEAD_SCENE_CLEANUP",
                            "Ped=" + (item.Ped == null ? 0 : item.Ped.Handle)
                            + "; Distance=" + distance.ToString("0.0")
                            + "; Radius=" + DeferredCleanupDistance.ToString("0.0")
                            + "; Reason=PlayerLeftActiveArea");
                    DeleteDeferred(item);
                    _deferredCleanup.Remove(item);
                }
            }
        }

        private static void DeleteDeferred(DeferredCleanup item)
        {
            try { if (item.Ped != null && item.Ped.Exists()) item.Ped.Delete(); } catch { }
            try { if (item.Vehicle != null && item.Vehicle.Exists()) item.Vehicle.Delete(); } catch { }
        }

        private static void CleanupIncident(LSPDDispatchEvent incident, bool defer)
        {
            if (incident == null || defer)
                return;

            ReleaseIncidentModelRequests(incident);

            // Convoy owns the arrested suspect and its transport after the
            // custody handoff. Dispatch still owns the temporary victim and
            // support actors, so those are always cleaned here.
            if (incident.OwnedByDispatch)
            {
                foreach (Ped suspect in TrackedSuspects(incident).ToArray())
                    try { if (suspect != null && suspect.Exists()) suspect.Delete(); } catch { }
                try { if (incident.SuspectVehicle != null && incident.SuspectVehicle.Exists()) incident.SuspectVehicle.Delete(); } catch { }
                incident.Suspect = null;
                incident.SuspectVehicle = null;
                if (incident.Suspects != null) incident.Suspects.Clear();
                if (incident.ArrestedSuspects != null) incident.ArrestedSuspects.Clear();
                if (incident.CompliantSuspects != null) incident.CompliantSuspects.Clear();
            }
            try { if (incident.Victim != null && incident.Victim.Exists()) incident.Victim.Delete(); } catch { }
            foreach (Ped participant in incident.AdditionalParticipants)
                try { if (participant != null && participant.Exists()) participant.Delete(); } catch { }
            incident.Victim = null;
            incident.AdditionalParticipants.Clear();
        }

        private static void ReleaseIncidentModelRequests(LSPDDispatchEvent incident)
        {
            if (incident == null)
                return;
            var modelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(incident.SuspectModel))
                modelNames.Add(incident.SuspectModel);
            if (incident.SuspectModelCandidates != null)
                foreach (string candidate in incident.SuspectModelCandidates)
                    if (!string.IsNullOrWhiteSpace(candidate))
                        modelNames.Add(candidate);
            if (incident.RequiresVehicle
                && !string.IsNullOrWhiteSpace(incident.VehicleModel))
                modelNames.Add(incident.VehicleModel);
            foreach (string modelName in modelNames)
            {
                try
                {
                    Model model = new Model(modelName);
                    if (model.IsValid)
                        model.MarkAsNoLongerNeeded();
                }
                catch { }
            }
        }
        private void SetSceneBlip(Vector3 position, string title)
        {
            CleanupBlip(_sceneBlip);
            try
            {
                _sceneBlip = World.CreateBlip(position);
                if (_sceneBlip != null && _sceneBlip.Exists())
                {
                    _sceneBlip.Name = title;
                    _sceneBlip.IsShortRange = false;
                }
            }
            catch (Exception ex) { LogException("POLICE_DISPATCH_BLIP_FAILED", ex); }
        }

        private void SetSuspectBlip(Ped suspect)
        {
            CleanupBlip(_suspectBlip);
            if (suspect == null || !suspect.Exists())
                return;
            try
            {
                _suspectBlip = suspect.AddBlip();
                if (_suspectBlip != null && _suspectBlip.Exists())
                {
                    _suspectBlip.Name = "Dispatch Suspect";
                    _suspectBlip.IsShortRange = false;
                }
            }
            catch (Exception ex) { LogException("POLICE_DISPATCH_SUSPECT_BLIP_FAILED", ex); }
        }

        private void CleanupBlipsAndWaypoint()
        {
            CleanupBlip(_sceneBlip);
            CleanupBlip(_suspectBlip);
            try
            {
                if (_hadPreviousWaypoint)
                    World.WaypointPosition = _previousWaypoint;
                else if (Game.IsWaypointActive)
                    World.RemoveWaypoint();
            }
            catch { }
            _hadPreviousWaypoint = false;
            _previousWaypoint = Vector3.Zero;
        }

        private static void CleanupBlip(Blip blip)
        {
            try { if (blip != null && blip.Exists()) blip.Delete(); } catch { }
        }

        private void CaptureAndSetWaypoint(Vector3 position)
        {
            if (!_uiSettings.ShowWaypoint)
            {
                LogRuntime("POLICE_DISPATCH_GPS_SUPPRESSED",
                    "Dispatch route guidance is disabled in LSImmersiveMainUI.xml.");
                return;
            }
            try
            {
                if (!_hadPreviousWaypoint && Game.IsWaypointActive)
                {
                    _previousWaypoint = World.WaypointPosition;
                    _hadPreviousWaypoint = true;
                }
                World.WaypointPosition = position;
            }
            catch (Exception ex) { LogException("POLICE_DISPATCH_GPS_FAILED", ex); }
        }

        /// <summary>
        /// Reports a single current Dispatch transmission. Replacing the prior
        /// stage prevents queued offer/route lines from being heard after the
        /// officer has already reached the scene or resolved the assignment.
        /// </summary>
        internal void ReportTransportAudio(string eventId, string stage)
        {
            if (_incident == null || string.IsNullOrWhiteSpace(eventId)
                || string.IsNullOrWhiteSpace(stage))
                return;
            ReportAudioStage(
                eventId,
                _incident,
                "transport-" + stage);
        }

        private void ReportSceneApproach(Ped player)
        {
            if (_incident == null || _incident.SceneApproachReported || _incident.SceneArrivalReported
                || _incident.HasConvoyCustodyHandoff || !_incident.OwnedByDispatch
                || player == null || !player.Exists())
                return;
            switch (_incident.State)
            {
                case LSPDDispatchState.Accepted:
                case LSPDDispatchState.EnRoute:
                case LSPDDispatchState.OnScene:
                case LSPDDispatchState.Investigating:
                case LSPDDispatchState.SuspectFleeing:
                case LSPDDispatchState.SuspectResisting:
                    break;
                default:
                    return;
            }
            if (player.Position.DistanceTo(_incident.Origin) > SceneApproachAudioRadius)
                return;

            // Scene AI may already have changed the state during travel. This
            // physical, incident-level guard also covers accepting a nearby call.
            _incident.SceneApproachReported = true;
            ReportAudioStage("lsimmersivelife.police.scene.approaching", _incident, "approaching", true);
            ReportAudioStage("lsimmersivelife.police.detail.safety_warning", _incident, "approach-caution", true);
        }

        private LSPDAudioResult ReportAudioStage(
            string eventId,
            LSPDDispatchEvent incident,
            string stage,
            bool retainCurrentScope = false)
        {
            if (incident == null || string.IsNullOrWhiteSpace(stage))
                return LSPDAudioResult.Inactive;
            if (string.Equals(incident.LastAudioStage, stage, StringComparison.Ordinal))
                return LSPDAudioResult.Duplicate;

            // The accepted/approach/investigation conversation shares a scope:
            // scene AI must not cancel the Player's acceptance, and paired lines
            // must remain FIFO. Later custody/outcome stages still replace it.
            if (!retainCurrentScope || string.IsNullOrWhiteSpace(incident.ActiveAudioScope))
            {
                CloseActiveAudio(incident);
                incident.ActiveAudioScope = "police-dispatch-" + incident.Id + "-" + stage;
            }
            string scope = incident.ActiveAudioScope;
            incident.LastAudioStage = stage;
            return _audio.Report(
                eventId,
                scope,
                stage + "-" + incident.Id,
                incident.AudioIncidentType);
        }

        private LSPDAudioResult ReportTerminalAudio(
            string eventId,
            LSPDDispatchEvent incident,
            string stage)
        {
            if (incident == null)
                return LSPDAudioResult.Inactive;
            CloseActiveAudio(incident);
            string scope = "police-dispatch-terminal-" + incident.Id + "-" + stage;
            incident.ActiveAudioScope = scope;
            incident.LastAudioStage = stage;
            return _audio.Report(
                eventId,
                scope,
                stage + "-" + incident.Id,
                incident.AudioIncidentType);
        }

        private void CloseActiveAudio(LSPDDispatchEvent incident)
        {
            if (incident == null || string.IsNullOrWhiteSpace(incident.ActiveAudioScope))
                return;
            try { _audio.CancelScope(incident.ActiveAudioScope); }
            catch { }
            incident.ActiveAudioScope = string.Empty;
        }

        private int NextQuietPatrolSeconds()
        {
            int minimum = Math.Max(30, _settings.MinimumQuietPatrolSeconds);
            int maximum = Math.Max(minimum, _settings.MaximumQuietPatrolSeconds);
            return minimum == maximum
                ? minimum
                : _random.Next(minimum, maximum + 1);
        }

        private void ScheduleNextOffer(DateTime now)
        {
            _cooldownUntil = now.AddSeconds(_settings.MinimumQuietPatrolSeconds);
            _nextOfferAt = now.AddSeconds(NextQuietPatrolSeconds());
        }

        private DateTime NextDefinitionEligibleAt(DateTime now)
        {
            if (_settings.RepeatProtectionMinutes <= 0 || _definitionOfferedAt.Count == 0)
                return now.AddSeconds(30);

            DateTime earliest = DateTime.MaxValue;
            foreach (LSPDDispatchEventDefinition definition in _definitions)
            {
                DateTime lastOffered;
                if (!_definitionOfferedAt.TryGetValue(definition.Id, out lastOffered))
                    return now;
                DateTime eligibleAt = lastOffered.AddMinutes(_settings.RepeatProtectionMinutes);
                if (eligibleAt < earliest)
                    earliest = eligibleAt;
            }
            if (earliest == DateTime.MaxValue)
                return now.AddSeconds(30);
            return earliest <= now ? now.AddSeconds(1) : earliest;
        }

        private bool CanPlayOfferAudio()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _lastOfferAudioAt.AddSeconds(_settings.AudioMinimumGapSeconds))
                return false;
            _lastOfferAudioAt = now;
            return true;
        }

        private static string DefinitionId(LSPDDispatchEvent incident)
        {
            if (incident == null || string.IsNullOrWhiteSpace(incident.Id))
                return string.Empty;
            int separator = incident.Id.LastIndexOf('-');
            return separator > 0 ? incident.Id.Substring(0, separator) : incident.Id;
        }

        private LSPDCriminalProfileDefinition WeightedProfile(
            IList<LSPDCriminalProfileDefinition> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return null;
            int totalWeight = candidates.Sum(value => Math.Max(1, value.Weight));
            int roll = _random.Next(totalWeight);
            foreach (LSPDCriminalProfileDefinition candidate in candidates)
            {
                roll -= Math.Max(1, candidate.Weight);
                if (roll < 0)
                    return candidate;
            }
            return candidates[candidates.Count - 1];
        }

        private bool TryResolveDispatchProfile(
            LSPDDispatchEventDefinition definition,
            out LSPDCriminalProfileDefinition resolved,
            out string failureReason)
        {
            resolved = null;
            failureReason = string.Empty;
            if (definition == null || definition.CriminalProfileIds == null)
            {
                failureReason = "EVENT_HAS_NO_CRIMINAL_PROFILE_IDS";
                return false;
            }

            var candidates = new List<LSPDCriminalProfileDefinition>();
            foreach (string profileId in definition.CriminalProfileIds)
            {
                LSPDCriminalProfileDefinition profile;
                if (string.IsNullOrWhiteSpace(profileId)
                    || !_criminalProfiles.TryGetValue(profileId, out profile)
                    || profile == null
                    || !profile.IsEligibleFor("dispatch"))
                    continue;
                candidates.Add(profile);
            }
            if (candidates.Count == 0)
            {
                failureReason = "EVENT_HAS_NO_ELIGIBLE_XML_CRIMINAL_PROFILE";
                return false;
            }

            return TryResolveUsableCriminalProfile(
                candidates,
                "dispatch",
                definition.RequiresVehicle,
                out resolved,
                out failureReason);
        }

        private bool TryResolveUsableCriminalProfile(
            IList<LSPDCriminalProfileDefinition> candidates,
            string activity,
            bool requireVehicle,
            out LSPDCriminalProfileDefinition resolved,
            out string failureReason)
        {
            resolved = null;
            failureReason = string.Empty;
            if (candidates == null || candidates.Count == 0)
            {
                failureReason = "NO_XML_CRIMINAL_PROFILE_CANDIDATES";
                return false;
            }

            var attemptedSelections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string lastFailure = "XML_CRIMINAL_ASSET_SELECTION_EXHAUSTED";
            for (int attempt = 0; attempt < MaxCriminalAssetSelectionAttempts; attempt++)
            {
                LSPDCriminalProfileDefinition source = WeightedProfile(candidates);
                LSPDCriminalProfileDefinition selected;
                string catalogFailure;
                if (!_criminalAssets.TryResolveProfile(
                    source,
                    activity,
                    _random,
                    _recentCriminalAssetIdSet,
                    out selected,
                    out catalogFailure))
                {
                    lastFailure = catalogFailure;
                    LogDebug(
                        "POLICE_CRIMINAL_ASSET_CANDIDATE_REJECTED",
                        "Activity=" + activity + "; Profile="
                        + (source == null ? string.Empty : source.Id)
                        + "; Reason=" + catalogFailure);
                    continue;
                }

                string selectionKey = selected.Id + "|" + selected.PedAssetId
                    + "|" + selected.VehicleAssetId + "|" + selected.WeaponAssetId;
                if (!attemptedSelections.Add(selectionKey))
                    continue;

                string runtimeFailure;
                if (!ValidateCriminalProfileAssets(
                    selected,
                    requireVehicle,
                    out runtimeFailure))
                {
                    lastFailure = runtimeFailure;
                    LogDebug(
                        "POLICE_CRIMINAL_ASSET_CANDIDATE_REJECTED",
                        "Activity=" + activity + "; Profile=" + selected.Id
                        + "; PedAsset=" + selected.PedAssetId
                        + "; VehicleAsset=" + selected.VehicleAssetId
                        + "; WeaponAsset=" + selected.WeaponAssetId
                        + "; Reason=" + runtimeFailure);
                    continue;
                }

                resolved = selected;
                // Record every successful concrete XML selection, including
                // Dispatch selections.  Without this, the recent-use filter
                // was updated for Convoy only, so repeated Dispatch offers
                // could keep selecting the same profile assets.
                RememberProfileAssets(selected);
                LogRuntime(
                    "POLICE_CRIMINAL_ASSETS_SELECTED",
                    DescribeCriminalProfileAssets(activity, selected)
                    + "; Attempt=" + (attempt + 1));
                return true;
            }

            failureReason = lastFailure
                + "; Attempts=" + MaxCriminalAssetSelectionAttempts;
            return false;
        }

        private static bool ValidateCriminalProfileAssets(
            LSPDCriminalProfileDefinition profile,
            bool requireVehicle,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (profile == null)
            {
                failureReason = "PROFILE_REFERENCE_MISSING";
                return false;
            }
            if (string.IsNullOrWhiteSpace(profile.ModelName))
            {
                failureReason = "PED_MODEL_NAME_EMPTY";
                return false;
            }

            Model pedModel = new Model(profile.ModelName);
            if (!IsUsableModel(pedModel, false))
            {
                failureReason = "PED_MODEL_INVALID:" + profile.ModelName;
                return false;
            }

            if (requireVehicle)
            {
                if (string.IsNullOrWhiteSpace(profile.VehicleModelName))
                {
                    failureReason = "VEHICLE_MODEL_NAME_EMPTY";
                    return false;
                }
                Model vehicleModel = new Model(profile.VehicleModelName);
                if (!IsUsableModel(vehicleModel, true))
                {
                    failureReason = "VEHICLE_MODEL_INVALID:" + profile.VehicleModelName;
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(profile.WeaponName))
            {
                failureReason = "WEAPON_NAME_EMPTY";
                return false;
            }
            int weaponHash = ResolveWeaponHash(profile.WeaponName);
            var weaponAsset = new WeaponAsset(weaponHash);
            if (!weaponAsset.IsValid || !weaponAsset.IsValidAsWeaponHash)
            {
                failureReason = "WEAPON_ASSET_INVALID:" + profile.WeaponName;
                return false;
            }
            return true;
        }

        private static string DescribeCriminalProfileAssets(
            string activity,
            LSPDCriminalProfileDefinition profile)
        {
            return "Activity=" + activity
                + "; Profile=" + (profile == null ? string.Empty : profile.Id)
                + "; PedAsset=" + (profile == null ? string.Empty : profile.PedAssetId)
                + "/" + (profile == null ? string.Empty : profile.ModelName)
                + "; VehicleAsset=" + (profile == null ? string.Empty : profile.VehicleAssetId)
                + "/" + (profile == null ? string.Empty : profile.VehicleModelName)
                + "; WeaponAsset=" + (profile == null ? string.Empty : profile.WeaponAssetId)
                + "/" + (profile == null ? string.Empty : profile.WeaponName)
                + "; Pools=ped/" + (profile == null ? string.Empty : profile.PedPoolId)
                + ",vehicle/" + (profile == null ? string.Empty : profile.VehiclePoolId)
                + ",weapon/" + (profile == null ? string.Empty : profile.WeaponPoolId);
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

        private void RememberProfileAssets(LSPDCriminalProfileDefinition profile)
        {
            if (profile == null)
                return;
            RememberAsset(profile.PedAssetId);
            RememberAsset(profile.VehicleAssetId);
            RememberAsset(profile.WeaponAssetId);
        }

        private void RememberAsset(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
                return;
            if (_recentCriminalAssetIdSet.Contains(assetId))
                return;
            _recentCriminalAssetIdSet.Add(assetId);
            _recentCriminalAssetIds.Enqueue(assetId);
            while (_recentCriminalAssetIds.Count > 12)
                _recentCriminalAssetIdSet.Remove(_recentCriminalAssetIds.Dequeue());
        }

        private void ReloadDefinitions()
        {
            _definitions.Clear();
            _criminalProfiles.Clear();
            _locations.Clear();
            _branches.Clear();
            _locationCatalog = null;
            if (_ambientWorld != null)
                _ambientWorld.UpdateLocationCatalog(null);
            _recentCriminalAssetIds.Clear();
            _recentCriminalAssetIdSet.Clear();
            _criminalAssets.Clear();

            if (!string.IsNullOrWhiteSpace(_criminalProfilePath)
                && File.Exists(_criminalProfilePath))
            {
                try
                {
                    XDocument profileDocument = LoadXmlDocument(_criminalProfilePath);
                    XElement profiles = profileDocument.Root == null
                        ? null : profileDocument.Root.Element("Profiles");
                    if (profiles == null)
                        throw new InvalidDataException("Criminal profile XML does not contain Profiles.");
                    foreach (XElement node in profiles.Elements("Profile"))
                    {
                        LSPDCriminalProfileDefinition profile =
                            LSPDCriminalProfileDefinition.FromXml(node);
                        if (_criminalProfiles.ContainsKey(profile.Id))
                            throw new InvalidDataException("Duplicate criminal profile id: " + profile.Id);
                        _criminalProfiles.Add(profile.Id, profile);
                    }

                    XElement peds = profileDocument.Root.Element("Peds");
                    if (peds != null)
                        foreach (XElement node in peds.Elements("Ped"))
                            _criminalAssets.AddPed(LSPDCriminalAssetDefinition.FromXml(
                                node, "model", "Ped"));

                    XElement vehicles = profileDocument.Root.Element("Vehicles");
                    if (vehicles != null)
                        foreach (XElement node in vehicles.Elements("Vehicle"))
                            _criminalAssets.AddVehicle(LSPDCriminalAssetDefinition.FromXml(
                                node, "model", "Vehicle"));

                    XElement weapons = profileDocument.Root.Element("Weapons");
                    if (weapons != null)
                        foreach (XElement node in weapons.Elements("Weapon"))
                            _criminalAssets.AddWeapon(LSPDCriminalAssetDefinition.FromXml(
                                node, "weapon", "Weapon"));

                    XElement pools = profileDocument.Root.Element("AssetPools");
                    if (pools != null)
                        foreach (XElement node in pools.Elements("Pool"))
                            _criminalAssets.AddPool(
                                LSPDCriminalAssetPoolDefinition.FromXml(node));

                    XElement mappings = profileDocument.Root.Element("ActivityMappings");
                    if (mappings != null)
                        foreach (XElement node in mappings.Elements("Mapping"))
                            _criminalAssets.AddActivityMapping(
                                LSPDCriminalActivityMappingDefinition.FromXml(node));
                    _criminalAssets.Validate();
                    foreach (LSPDCriminalProfileDefinition profile in _criminalProfiles.Values)
                    {
                        if (!string.IsNullOrWhiteSpace(profile.PedPoolId)
                            && !_criminalAssets.HasPool(profile.PedPoolId, "ped"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid ped pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.VehiclePoolId)
                            && !_criminalAssets.HasPool(profile.VehiclePoolId, "vehicle"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid vehicle pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.WeaponPoolId)
                            && !_criminalAssets.HasPool(profile.WeaponPoolId, "weapon"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid weapon pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.ConvoyPedPoolId)
                            && !_criminalAssets.HasPool(profile.ConvoyPedPoolId, "ped"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid convoy ped pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.ConvoyVehiclePoolId)
                            && !_criminalAssets.HasPool(profile.ConvoyVehiclePoolId, "vehicle"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid convoy vehicle pool: " + profile.Id);
                        if (!string.IsNullOrWhiteSpace(profile.ConvoyWeaponPoolId)
                            && !_criminalAssets.HasPool(profile.ConvoyWeaponPoolId, "weapon"))
                            throw new InvalidDataException(
                                "Criminal profile references an invalid convoy weapon pool: " + profile.Id);
                    }
                }
                catch (Exception ex)
                {
                    _criminalProfiles.Clear();
                    _criminalAssets.Clear();
                    LogException("POLICE_CRIMINAL_PROFILE_CATALOG_FAILED", ex);
                }
            }
            else if (!string.IsNullOrWhiteSpace(_criminalProfilePath))
            {
                LogDebug("POLICE_CRIMINAL_PROFILE_CATALOG_MISSING",
                    "LSPDCriminalProfile.xml was not found; Dispatch criminal asset selection is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(_catalogPath) || !File.Exists(_catalogPath))
                return;

            LSImmersiveLocationCatalog locationCatalog;
            try
            {
                locationCatalog = LSImmersiveLocationCatalog.Load(
                    _locationCatalogPath);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_LOCATION_CATALOG_FAILED", ex);
                return;
            }

            _locationCatalog = locationCatalog;
            if (_ambientWorld != null)
                _ambientWorld.UpdateLocationCatalog(locationCatalog);
            LogRuntime(
                "DISPATCH_LOCATION_CATALOG_LOADED",
                "LocationRecords=" + locationCatalog.LocationCount);

            try
            {
                XDocument document = LoadXmlDocument(_catalogPath);
                XElement branchCatalog = document.Root == null
                    ? null : document.Root.Element("Branches");
                if (branchCatalog != null)
                {
                    foreach (XElement node in branchCatalog.Elements("Branch"))
                    {
                        LSPDDispatchBranchDefinition branch =
                            LSPDDispatchBranchDefinition.FromXml(node);
                        if (_branches.ContainsKey(branch.Id))
                            throw new InvalidDataException(
                                "Duplicate Dispatch branch reference: " + branch.Id);
                        _branches.Add(branch.Id, branch);
                    }
                }
                XElement locationRefs = document.Root == null
                    ? null : document.Root.Element("LocationRefs");
                if (locationRefs != null)
                {
                    foreach (XElement node in locationRefs.Elements("LocationRef"))
                    {
                        LSPDDispatchLocationDefinition location =
                            LSPDDispatchLocationDefinition.FromXml(
                                node, locationCatalog);
                        if (_locations.ContainsKey(location.Id))
                            throw new InvalidDataException("Duplicate Dispatch location id: " + location.Id);
                        _locations.Add(location.Id, location);
                    }
                }
                XElement events = document.Root == null ? null : document.Root.Element("Events");
                if (events == null)
                    return;
                foreach (XElement node in events.Elements("Event"))
                {
                    try
                    {
                        LSPDDispatchEventDefinition definition =
                            LSPDDispatchEventDefinition.FromXml(node);
                        ValidateDefinitionReferences(definition);
                        _definitions.Add(definition);
                    }
                    catch (Exception ex) { LogException("POLICE_DISPATCH_DEFINITION_INVALID", ex); }
                }
                LogRuntime(
                    "POLICE_DISPATCH_CATALOG_LOADED",
                    "Definitions=" + _definitions.Count
                    + "; CriminalProfiles=" + _criminalProfiles.Count
                    + "; CriminalPeds=" + _criminalAssets.PedCount
                    + "; CriminalVehicles=" + _criminalAssets.VehicleCount
                    + "; CriminalWeapons=" + _criminalAssets.WeaponCount
                    + "; AuthoredLocations=" + _locations.Count
                    + "; Branches=" + _branches.Count);
            }
            catch (Exception ex)
            {
                LogException("POLICE_DISPATCH_CATALOG_FAILED", ex);
            }
        }

        private void ValidateDefinitionReferences(LSPDDispatchEventDefinition definition)
        {
            if (definition == null)
                throw new InvalidDataException("Dispatch definition is null.");
            if (definition.BranchIds == null || definition.BranchIds.Count == 0)
                throw new InvalidDataException(
                    "Dispatch event has no documented response-branch reference: " + definition.Id);
            foreach (string branchId in definition.BranchIds)
            {
                LSPDDispatchBranchDefinition branch;
                if (!_branches.TryGetValue(branchId, out branch))
                    throw new InvalidDataException(
                        "Dispatch event references an unknown branch: "
                        + definition.Id + " -> " + branchId);
                if (string.Equals(branch.Phase, "custody_continuation",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "A custody continuation cannot be offered as a new Dispatch callout: "
                        + definition.Id + " -> " + branchId);
            }
            if (definition.CriminalProfileIds == null
                || definition.CriminalProfileIds.Count == 0)
                throw new InvalidDataException(
                    "Dispatch event has no authoritative criminal profile reference: "
                    + definition.Id);
            else
            {
                bool hasProfile = definition.CriminalProfileIds.Any(
                    id => _criminalProfiles.ContainsKey(id)
                        && _criminalProfiles[id].IsEligibleFor("dispatch"));
                if (!hasProfile)
                    throw new InvalidDataException(
                        "Dispatch event has no eligible criminal profile reference: " + definition.Id);
            }
            if (definition.LocationIds == null || definition.LocationIds.Count == 0
                || definition.LocationIds.Any(id => !_locations.ContainsKey(id)))
                throw new InvalidDataException(
                    "Dispatch event has a missing or invalid authored location reference: "
                    + definition.Id);
        }

        private static XDocument LoadXmlDocument(string path)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using (XmlReader reader = XmlReader.Create(path, settings))
                return XDocument.Load(reader, LoadOptions.None);
        }

        private void Notify(string message)
        {
            try { Notification.PostTicker(message, false, false); } catch { }
        }

        private void LogRuntime(string category, string message)
        {
            if (_log != null) _log.Runtime(category, message);
        }

        private void LogStateFailure(string category, string message)
        {
            if (_log != null) _log.StateFailure(category, message);
        }

        private void LogDebug(string category, string message)
        {
            if (_log != null) _log.Debug(category, message);
        }

        private void LogException(string category, Exception ex)
        {
            if (_log != null) _log.Exception(category, ex);
        }

        private static string DescribePed(Ped ped)
        {
            if (ped == null)
                return "null";
            try
            {
                return "Handle=" + ped.Handle
                    + "; Exists=" + ped.Exists()
                    + "; Dead=" + (ped.Exists() && ped.IsDead)
                    + "; Position=" + (ped.Exists() ? ped.Position.ToString() : "unavailable");
            }
            catch
            {
                return "Handle unavailable";
            }
        }

        private static string DescribeVehicle(Vehicle vehicle)
        {
            if (vehicle == null)
                return "null";
            try
            {
                return "Handle=" + vehicle.Handle
                    + "; Exists=" + vehicle.Exists()
                    + "; Position=" + (vehicle.Exists() ? vehicle.Position.ToString() : "unavailable");
            }
            catch
            {
                return "Handle unavailable";
            }
        }
    }
}




