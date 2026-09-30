using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LSImmersiveLife
{
    /// <summary>
    /// Coordinates the Police Authority session, profile, patrol shift, and
    /// specialized Dispatch, response, Gang, Crime Activity, NPC, audio, and
    /// custody owners. Each owner keeps its own state machine and entity
    /// ownership; this class supplies ordering and lifecycle gates.
    /// </summary>
    // The original coordination note is retained above. The local NPC owner is
    // now connected below and remains explicitly gated by Authority + Patrol.
    /// <summary>
    /// Keeps the active Police owners in one deterministic runtime order while
    /// leaving Dispatch, Gang, Crime Activity, NPC, and Convoy state in their
    /// specialized classes.
    /// </summary>
    internal sealed class LSImmersivePoliceCore : IDisposable
    {
        // Construction stays passive. Runtime ownership begins only after
        // EnterPoliceAuthority and Process receives an active game tick.
        private readonly LSImmersiveLog _log;
        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveMainConfig _config;
        private readonly LSPDAuthority _authority;
        private readonly LSPDGangDataIntegration _gangData;
        private readonly LSNPCDatabase _npcDatabase;
        private readonly List<Blip> _stationMarkers = new List<Blip>();
        private PlayerStateSnapshot _normalPlayerState;
        private Vehicle _managedPoliceVehicle;
        private readonly LSPDGangAdapterResponse _gangResponse;
        private DateTime _nextGangScan = DateTime.MinValue;
        private Ped[] _nearbyGangPeds = new Ped[0];
        private bool _gangIncidentWasActive;
        private bool _crimeActivityWasActive;
        private bool _npcInteractionWasActive;
        // One automatic group-support request per accepted Dispatch incident.
        // This makes the central AutomaticGroupSupport setting a real gameplay
        // decision without turning every Core tick into another backup spawn.
        private string _automaticGroupBackupIncidentId = string.Empty;
        private bool _patrolKeyDown;
        private bool _emergencyKeyDown;
        private bool _resetKeyDown;
        private bool _transportKeyDown;
        private bool _transportCompleteKeyDown;
        private bool _convoyAcceptKeyDown;
        private bool _convoyRejectKeyDown;
        private bool _configurationRefreshPending = true;
        private bool _lastAppliedAudioEnabled;
        private int _lastAppliedAudioVolume = -1;
        private PlayerStateSnapshot _pendingNormalPlayerRestore;
        private Model _pendingRestoreModel;
        private DateTime _pendingRestoreDeadline = DateTime.MinValue;
        private bool _pendingWeaponsRestored;
        // The selected Police profile may reference streamed addon assets. A
        // menu callback may request those assets but must not wait for them.
        // Keep the unfinished portions here and apply each one once from the
        // normal Script tick after GTA has loaded it.
        private bool _pendingProfilePedApply;
        private bool _pendingProfileVehicleApply;
        private bool _pendingProfileWeaponsApply;
        private DateTime _nextPendingProfileApply = DateTime.MinValue;
        private DateTime _pendingProfileApplyDeadline = DateTime.MinValue;
        private long _radioTest;

        internal LSImmersivePoliceCore(
            LSImmersiveLog log,
            LSIMMERSIVEPATH paths,
            LSImmersiveMainConfig config,
            LSPDAudioDispatch audio = null)
        {
            _log = log ?? throw new ArgumentNullException("log");
            _paths = paths ?? throw new ArgumentNullException("paths");
            _config = config ?? throw new ArgumentNullException("config");
            _profile = new LSPDProfile(paths, log);
            // Authority behavior flags are owned by the one universal main
            // configuration document. The Police profile remains a personal
            // selection/favourites data document, not a second settings file.
            _authority = new LSPDAuthority(_profile, _config, log);

            // Construction is passive. Audio stays inactive and no model,
            // vehicle, weapon, wanted state, or world relationship is changed.
            Audio = audio ?? new LSPDAudioDispatch(paths, log);
            Audio.SetOptions(config.EnableAudio, config.AudioVolume);
            _lastAppliedAudioEnabled = config.EnableAudio;
            _lastAppliedAudioVolume = config.AudioVolume;
            Audio.SetActive(false);
            Patrol = new LSPDPatrol(Audio, _config.Police.Patrol);
            Dispatch = new LSPDDispatch(
                Audio,
                _paths,
                _log,
                _config.PoliceControls,
                _config.Police.Dispatch,
                _config.Police.Cleanup,
                _config.Ui);
            Response = new LSPDPoliceResponse(
                Audio,
                _profile,
                _log,
                _config.Police.Response);
            Backup = new LSPDBackUp(
                Audio,
                _profile,
                _log,
                _config.Police.Backup);
            _gangData = new LSPDGangDataIntegration(_paths, _log);
            _npcDatabase = new LSNPCDatabase(_paths, _log);
            _gangResponse = new LSPDGangAdapterResponse(Audio, _log, _gangData);
            NpcResponse = new LSPDNPCResponse(
                log,
                _gangData,
                _config.PoliceControls,
                _profile,
                _config.Police.NpcResponse,
                _config.Police.Traffic,
                _npcDatabase);
            CrimeActivity = new LSPDCrimeActivity(
                Audio,
                paths,
                log,
                _config.Police.CrimeActivity,
                _config.Police.Cleanup,
                Dispatch.CriminalAssets);
            Convoy = new LSPDConvoy(
                Audio,
                _profile,
                _log,
                _config.PoliceControls,
                _config.Police.Convoy,
                _config.Police.Cleanup,
                _config.Ui);
            _config.SettingsChanged += QueueConfigurationRefresh;
            LastOperationMessage = "Police Authority is inactive.";
        }

        internal LSPDAudioDispatch Audio { get; private set; }
        // One Police session shares one radio queue. These are the existing
        // module boundaries, not copies of the abandoned project's gameplay.
        // Owners report confirmed transitions; audio never creates those facts.
        internal LSPDPatrol Patrol { get; private set; }
        internal LSPDDispatch Dispatch { get; private set; }
        internal LSPDPoliceResponse Response { get; private set; }
        internal LSPDBackUp Backup { get; private set; }
        internal LSPDGangDataIntegration GangData { get { return _gangData; } }
        internal LSPDGangAdapterResponse GangResponse { get { return _gangResponse; } }
        internal LSPDNPCResponse NpcResponse { get; private set; }
        internal LSNPCDatabase NpcDatabase { get { return _npcDatabase; } }
        internal LSPDCrimeActivity CrimeActivity { get; private set; }
        internal LSPDConvoy Convoy { get; private set; }
        internal LSPDProfile Profile { get { return _profile; } }
        internal LSPDAuthority Authority { get { return _authority; } }
        internal LSIMMERSIVEPATH Paths { get { return _paths; } }
        internal bool IsPoliceAuthorityActive { get { return _authority.IsActive; } }
        internal string LastOperationMessage { get; private set; }
        internal int NearbyCrimeActivityCount { get { return CrimeActivity.NearbyIntel.Count; } }
        internal string CrimeActivityStatus
        {
            get
            {
                if (!CrimeActivity.IsLoaded) return "Crime intelligence is not loaded.";
                if (CrimeActivity.Active) return CrimeActivity.StatusText;
                if (CrimeActivity.HasAvailableActivity) return CrimeActivity.StatusText;
                if (CrimeActivity.NearbyIntel.Count == 0) return "No nearby intelligence observed.";
                return CrimeActivity.NearbyIntel.Count + " nearby intelligence tip(s) available.";
            }
        }

        internal void SetRoleActive(bool active)
        {
            // Selecting another Roleplay category releases Police Authority
            // through its existing UI/Core boundary. Police selection itself
            // enters through LSAuthorityRole and the existing Open path.
            if (!active)
                ExitPoliceAuthority();
        }

        internal bool EnterPoliceAuthority()
        {
            if (IsPoliceAuthorityActive)
            {
                LastOperationMessage = "Police Authority is already active.";
                return true;
            }

            CaptureNormalPlayerState();
            bool catalogsLoaded = _authority.Enter();
            Audio.SetActive(true);
            _gangData.Reload();
            if (!_npcDatabase.IsLoaded && !_npcDatabase.Reload())
            {
                _log.Debug("NPC_DATABASE_UNAVAILABLE",
                    _npcDatabase.LastLoadError ?? "NPC citizen database could not be loaded.");
            }
            if (!CrimeActivity.IsLoaded)
            {
                if (CrimeActivity.Reload())
                {
                    _log.Runtime("POLICE_CRIME_INTELLIGENCE_CATALOG_LOADED",
                        "Activities=" + CrimeActivity.ActivityCount
                        + "; Locations=" + CrimeActivity.LocationCount
                        + "; Groups=" + CrimeActivity.GroupCount);
                }
                else
                {
                    _log.Debug("POLICE_CRIME_INTELLIGENCE_CATALOG_LOAD_FAILED",
                        CrimeActivity.LastLoadError ?? "Crime Activity catalog load failed without a reported error.");
                }
            }

            _log.Runtime(
                _profile.LastProfileLoaded
                    ? "POLICE_PROFILE_LOADED"
                    : "POLICE_PROFILE_DEFAULTS_LOADED",
                _profile.LastProfileLoaded
                    ? "Saved Police profile and personal weapon collection loaded."
                    : "Police profile defaults loaded because no valid saved profile was available.");

            LSPDPoliceAgencyDefinition agency =
                _profile.FindAgency(_profile.Selection.AgencyId);
            LSPDPoliceStationDefinition station =
                _profile.FindStation(_profile.Selection.StationId);
            _log.Runtime(
                "POLICE_AUTHORITY_ACTIVATED",
                "Agency=" + (agency == null ? "Unselected" : agency.CallSign)
                + "; Station=" + (station == null ? "Unselected" : station.DisplayName)
                + "; SavedProfile=" + _profile.LastProfileLoaded.ToString(CultureInfo.InvariantCulture));

            bool pedApplied;
            bool vehicleApplied;
            bool weaponsApplied;
            bool profileApplied = ApplySavedProfile(
                false,
                out pedApplied,
                out vehicleApplied,
                out weaponsApplied);
            if (!profileApplied)
                QueuePendingSavedProfileApply(pedApplied, vehicleApplied, weaponsApplied);
            if (catalogsLoaded)
                ActivateStationMarkers();
            if (!catalogsLoaded)
            {
                LastOperationMessage = "Police Authority activated, but Police XML data could not be loaded.";
                return false;
            }

            LastOperationMessage = profileApplied
                ? "Police Authority activated. Saved Police profile applied."
                : "Police Authority activated. Some saved assets could not be applied.";
            return true;
        }

        internal void ExitPoliceAuthority()
        {
            if (!IsPoliceAuthorityActive)
                return;

            Audio.SetActive(false);
            Patrol.Reset();
            Dispatch.Reset();
            _automaticGroupBackupIncidentId = string.Empty;
            Convoy.Reset();
            Response.Reset();
            Backup.Reset();
            _gangResponse.Reset();
            ResetStationMarkers();
            CrimeActivity.Reset();
            _gangData.Reset();
            NpcResponse.Reset();
            _authority.Exit();
            ReleaseManagedVehicle();
            QueueNormalPlayerRestore();
            LastOperationMessage = "Police Authority deactivated.";
            _patrolKeyDown = false;
            _emergencyKeyDown = false;
            _resetKeyDown = false;
            _transportKeyDown = false;
            _transportCompleteKeyDown = false;
            _convoyAcceptKeyDown = false;
            _convoyRejectKeyDown = false;
            _nearbyGangPeds = new Ped[0];
            _gangIncidentWasActive = false;
            _crimeActivityWasActive = false;
            _npcInteractionWasActive = false;
            ClearPendingSavedProfileApply();
            _log.Runtime(
                "POLICE_AUTHORITY_DEACTIVATED",
                "Normal player session restored where possible.");
        }

        internal void Process(bool paused)
        {
            Process(paused, true);
        }

        internal void Process(bool paused, bool allowGameplayInput)
        {
            // Player restoration is queued by Authority exit and completed from
            // the normal Script tick. This avoids Model.Request waiting inside a
            // LemonUI callback, which caused the old Script.Wait crash.
            ProcessPendingNormalPlayerRestore();
            ApplyPendingConfiguration();

            if (!IsPoliceAuthorityActive)
            {
                SynchronizePoliceKeyStates();
                return;
            }

            _authority.Process(paused);
            ProcessPendingSavedProfileApply(paused);
            bool profileAssetsPending = HasPendingSavedProfileAssets;
            if (allowGameplayInput && !paused)
                ProcessPoliceHotkeys();

            Ped player = Game.Player.Character;

            // Authority owns player-scoped vanilla protection. PoliceResponse
            // owns this separate local officer recognition/de-escalation pass;
            // Core only provides the active Authority context and runtime order.
            // This remains active outside Dispatch so ordinary patrol does not
            // revert to hostile vanilla Police behavior between incidents.
            if (!paused)
                Response.MaintainAuthoritySupport(
                    player,
                    _authority.Settings,
                    Convoy.ProtectedActors.Concat(Backup.ProtectedActors));

            if (Patrol.IsPatrolling && !paused)
                UpdateGangSample(player);

            // Gang activity is its own local layer. It gets first refusal over a
            // new dispatch call, while an already active Dispatch remains owned by
            // Dispatch until it reaches a terminal state.
            // A paused game must not start or resolve a gang incident from the
            // last cached sample. Keep an existing incident visible, but leave
            // its world tasks untouched until the game resumes.
            if (!paused)
                _gangResponse.Process(
                    Patrol.IsPatrolling,
                    Dispatch.HasIncident || Convoy.Active
                        || CrimeActivity.BlocksOtherPoliceActivities
                    || profileAssetsPending,
                    NpcResponse.HasActiveInteraction,
                    player,
                    _nearbyGangPeds,
                    Backup.Active && Backup.State != LSPDBackupAssignmentState.StandingDown);
            bool gangActive = _gangResponse.HasActiveIncident;
            if (_gangIncidentWasActive && !gangActive)
                Dispatch.DeferOffersForContextActivity("gang incident");
            _gangIncidentWasActive = gangActive;

            // Crime Activity is a quiet player-led owner. It can prepare an
            // intelligence lead only during a genuinely quiet patrol, while an
            // already active Crime scene continues to own its actors until it
            // resolves. It never produces a Dispatch offer.
            if (!paused)
            {
                bool mayDiscoverCrime = Patrol.IsPatrolling
                    && !profileAssetsPending
                    && !Dispatch.HasIncident
                    && !Convoy.Active
                    && !gangActive
                    && !NpcResponse.HasActiveInteraction
                    && !Backup.BlocksPlayerActivities;
                CrimeActivity.Process(
                    player,
                    Patrol.IsPatrolling,
                    mayDiscoverCrime,
                    _gangData,
                    false);
            }
            bool crimeActivityActive = CrimeActivity.BlocksOtherPoliceActivities;
            if (_crimeActivityWasActive && !crimeActivityActive)
                Dispatch.DeferOffersForContextActivity("Crime Activity");
            _crimeActivityWasActive = crimeActivityActive;

            // Establish and maintain a civilian/traffic contact before the
            // Dispatch producer evaluates a new offer. This closes the former
            // same-tick race where a G press could lose to a freshly-created
            // callout. Dispatch actor handles are supplied only as a protection
            // boundary; NPCResponse never owns or mutates those actors.
            // IMPORTANT: _nearbyGangPeds is the 75 m observation sample used
            // by GangAdapterResponse to *find* a real attacker. It contains all
            // nearby peds, not only gang actors. Passing that whole sample into
            // NPCResponse used to mark every civilian/driver around the player
            // as a protected scene actor, so the UI could see a person directly
            // in front of Anyi yet still report "No safe civilian...". Protect
            // only actors that a specialized owner actually owns.
            var protectedActors = new List<Ped>();
            protectedActors.AddRange(CrimeActivity.ActiveCriminals);
            Ped activeGangThreat = _gangResponse.ActiveThreat;
            if (activeGangThreat != null && activeGangThreat.Exists())
                protectedActors.Add(activeGangThreat);
            protectedActors.AddRange(_gangResponse.ActiveThreats);
            protectedActors.AddRange(Convoy.ProtectedActors);
            protectedActors.AddRange(Backup.ProtectedActors);
            NpcResponse.SetProtectedSceneActors(Dispatch.Current, protectedActors);
            NpcResponse.SetProtectedSceneVehicles(
                Convoy.ProtectedVehicles.Concat(Backup.ProtectedVehicles));
            bool dispatchBlocksNpcContact = Dispatch.HasIncident || gangActive
                || Convoy.Active || crimeActivityActive;
            // NPCResponse's traffic/crowd reactions are deliberately local to
            // the physical scene.  The old global activity flag made every
            // ordinary vehicle around the player behave as scene traffic while
            // a remote Dispatch, Convoy, or transport was active.  That was
            // the source of repeated departure tasks, slow traffic, and noisy
            // runtime output during otherwise unrelated travel.
            bool activeRoadScene = IsPlayerAtActiveRoadScene(
                player, gangActive, crimeActivityActive);
            NpcResponse.Process(
                Patrol.IsPatrolling,
                dispatchBlocksNpcContact,
                activeRoadScene,
                paused,
                allowGameplayInput);

            bool npcInteractionActive = NpcResponse.HasActiveInteraction;
            if (_npcInteractionWasActive && !npcInteractionActive)
                Dispatch.DeferOffersForContextActivity("NPC interaction");
            _npcInteractionWasActive = npcInteractionActive;

            // A visible Police menu suppresses ordinary gameplay hotkeys, but
            // an already-delivered Dispatch offer still owns its dedicated
            // Y/N or Circle/X decision controls. Without this narrow exception
            // a player-requested offer could be visible in the menu while its
            // documented response keys were silently ignored.
            bool allowDispatchDecisionInput = allowGameplayInput || Dispatch.HasOffer;
            Dispatch.Process(
                Patrol.IsPatrolling,
                profileAssetsPending || npcInteractionActive || gangActive || Convoy.Active || crimeActivityActive,
                paused,
                allowDispatchDecisionInput,
                gangActive || Convoy.Active || crimeActivityActive);

            if (!paused)
                EnsureAutomaticGroupBackup(player);

            // Backup is a dedicated player-requested support owner. It follows
            // the same Dispatch case without sharing Response's restrained
            // one-unit ambient support lifecycle.
            Backup.Process(
                Dispatch,
                Convoy,
                CrimeActivity,
                NpcResponse,
                _gangResponse,
                player,
                paused);

            // Do not issue response-unit tasks while GTA is paused. Keeping
            // the unit alive across the pause avoids a release/recreate cycle
            // and preserves the one-unit cap.
            if (!paused && _config.Police.Response.Enabled)
            {
                if (Dispatch.HasIncident && !Convoy.Active && !Backup.BlocksPlayerActivities)
                {
                    LSPDDispatchEvent incident = Dispatch.Current;
                    Vector3? responseDestination = incident == null
                        ? (Vector3?)null
                        : incident.Origin;
                    if (incident != null
                        && incident.Suspect != null
                        && incident.Suspect.Exists()
                        && (incident.State == LSPDDispatchState.SuspectFleeing
                            || incident.State == LSPDDispatchState.SuspectResisting))
                        responseDestination = incident.Suspect.Position;

                    bool activePursuit = incident != null
                        && (incident.State == LSPDDispatchState.SuspectFleeing
                            || incident.State == LSPDDispatchState.SuspectResisting);
                    if (activePursuit
                        && _config.Police.Response.AutomaticPursuitSupport
                        && Response.ActiveUnitCount == 0
                        && responseDestination.HasValue)
                    {
                        // The response owner enforces its own configured cap;
                        // this only asks for one restrained support unit during
                        // a confirmed active pursuit.
                        Response.EnsureResponseUnit(responseDestination.Value);
                    }
                    Response.Update(responseDestination);
                }
                else if (gangActive && !Convoy.Active && !crimeActivityActive)
                {
                    // Gang Threat support is a player-requested LSPDBackUp
                    // assignment. Do not keep the old one-car ambient
                    // response alive beside it; that unit only drove to the
                    // scene and never received the hostile gang target list.
                    Response.ReleaseAll();
                }
                else
                {
                    Response.ReleaseAll();
                }
            }
            else if (!paused)
            {
                Response.ReleaseAll();
            }

            string convoyMessage = Convoy.Process(
                DateTime.UtcNow,
                allowGameplayInput && !paused);
            if (!string.IsNullOrWhiteSpace(convoyMessage))
                LastOperationMessage = convoyMessage;

            // Convoy owns the physical custody trip. Mirror only its terminal
            // transport states into Dispatch so the two owners remain separate
            // while the player drives the prisoner to the chosen destination.
            if (Dispatch.HasConvoyCustodyHandoff
                && Convoy.State == LSPDDispatchState.HoldingAtStation
                && Dispatch.State == LSPDDispatchState.AwaitingTransport)
                Dispatch.SetConvoyState(LSPDDispatchState.HoldingAtStation);
            else if (Dispatch.HasConvoyCustodyHandoff
                && Convoy.State == LSPDDispatchState.PrisonTransfer
                && Dispatch.State == LSPDDispatchState.HoldingAtStation)
                Dispatch.SetConvoyState(LSPDDispatchState.PrisonTransfer);

            string prisonTransferDecision = ProcessStationPrisonTransferInput(
                allowGameplayInput,
                paused);
            if (!string.IsNullOrWhiteSpace(prisonTransferDecision))
                LastOperationMessage = prisonTransferDecision;

            ReconcileConvoyTerminalState();
            if (allowGameplayInput && !paused
                && Game.IsControlJustPressed(GTA.Control.VehicleHandbrake))
            {
                string transportMessage = Convoy.HoldingAtStation
                    ? RequestPrisonerTransport()
                    : Convoy.State == LSPDDispatchState.PrisonTransfer
                        ? CompletePrisonerTransport()
                        : string.Empty;
                if (!string.IsNullOrWhiteSpace(transportMessage))
                    LastOperationMessage = transportMessage;
            }

            Audio.Process(paused);
        }

        private void EnsureAutomaticGroupBackup(Ped player)
        {
            if (player == null || !player.Exists())
                return;
            if (!Dispatch.HasIncident)
            {
                _automaticGroupBackupIncidentId = string.Empty;
                return;
            }
            LSPDDispatchEvent incident = Dispatch.Current;
            if (!_config.Police.Backup.Enabled
                || !_config.Police.Backup.AutomaticGroupSupport
                || Convoy.Active
                || incident == null
                || incident.State == LSPDDispatchState.Offered
                || incident.SuspectCount <= 1)
                return;
            if (string.Equals(_automaticGroupBackupIncidentId, incident.Id, StringComparison.Ordinal))
                return;

            // Record the attempted incident even if streaming fails. The
            // officer can still request Backup manually with B, while the
            // automatic producer never retries every frame.
            _automaticGroupBackupIncidentId = incident.Id ?? string.Empty;
            string result = Backup.Request(Dispatch, player, true);
            _log.Runtime(
                "POLICE_GROUP_BACKUP_AUTO_REQUEST",
                "Incident=" + _automaticGroupBackupIncidentId
                + "; Suspects=" + incident.SuspectCount
                + "; Result=" + result);
        }

        /// <summary>
        /// Convoy terminal states belong to one custody handoff only. The old
        /// condition cancelled whichever Dispatch happened to be active after a
        /// past transport failure, which is why fresh offers immediately died.
        /// Reconcile and clear each terminal state once. Scene-transport
        /// failures return valid custody to Dispatch; a prison-transfer
        /// failure keeps already-received station custody in Convoy so a
        /// retry cannot create another scene-transport leg.
        /// </summary>
        private void ReconcileConvoyTerminalState()
        {
            if (Convoy.Failed)
            {
                if (Convoy.IsRequestedConvoyActivity)
                {
                    string requestReason = Convoy.LastFailureReason;
                    Backup.BeginStandDown("Requested Convoy activity ended: " + requestReason);
                    Convoy.Reset();
                    Response.ReleaseAll();
                    Dispatch.DeferOffersForContextActivity("Convoy Request");
                    LastOperationMessage = "Convoy Request closed: " + requestReason;
                    _log.Runtime("POLICE_CONVOY_REQUEST_CLOSED", "Reason=" + requestReason);
                    return;
                }

                if (!Dispatch.HasConvoyCustodyHandoff)
                {
                    Convoy.Reset();
                    _log.Runtime("POLICE_CONVOY_STALE_FAILURE_CLEARED",
                        "A completed or failed Convoy state had no linked Dispatch custody handoff.");
                    return;
                }

                string reason = Convoy.LastFailureReason;
                // A prison-transfer route failure occurs after the prisoner
                // has already been physically received at the station. Keep
                // that custody phase in Convoy so a retry starts a new prison
                // leg instead of incorrectly creating another scene-transport
                // van back to the station.
                if (Convoy.FailedAfterStationHandoff
                    && Convoy.RestoreFailedPrisonTransferForRetry())
                {
                    Dispatch.SetConvoyState(LSPDDispatchState.HoldingAtStation);
                    LastOperationMessage =
                        "Prison transfer was interrupted. The prisoner remains secured at the station; approve prison transfer again when ready.";
                    Response.ReleaseAll();
                    Backup.BeginStandDown("Prison transfer interrupted; station custody retained.");
                    _log.Runtime(
                        "POLICE_CUSTODY_STATION_RETRY_READY",
                        "Living station custody retained after prison-transfer failure; Reason="
                        + (reason ?? string.Empty));
                    return;
                }

                IList<Ped> prisoners = Convoy.ConsumeFailedPrisoners();
                if (Dispatch.RecoverCustodyFromConvoy(prisoners, reason))
                {
                    LastOperationMessage = "Transport was interrupted. The prisoner remains secured; request transport again when ready.";
                    Response.ReleaseAll();
                    Backup.BeginStandDown("Convoy transport interrupted; scene support released.");
                    return;
                }

                LastOperationMessage = Dispatch.Cancel(
                    "Prisoner custody failed and could not be recovered ("
                    + (reason ?? "unknown custody failure") + ").");
                _log.Runtime(
                    "POLICE_CUSTODY_TERMINAL_FAILURE",
                    "Dispatch custody ended after Convoy failure; Reason="
                    + (reason ?? "unknown custody failure"));
                Response.ReleaseAll();
                Backup.BeginStandDown("Convoy failure closed the linked Dispatch case.");
                return;
            }

            if (!Convoy.Completed)
                return;

            if (Convoy.IsRequestedConvoyActivity)
            {
                Convoy.ConsumeCompletedState();
                Response.ReleaseAll();
                Backup.BeginStandDown("Requested Convoy activity completed.");
                Dispatch.DeferOffersForContextActivity("Convoy Request");
                LastOperationMessage = "Convoy Request completed. Return to patrol.";
                _log.Runtime("POLICE_CONVOY_REQUEST_COMPLETED", "Player-requested Convoy completed.");
                return;
            }

            if (Dispatch.HasConvoyCustodyHandoff)
                LastOperationMessage = Dispatch.CompleteTransport();
            else
                _log.Runtime("POLICE_CONVOY_STALE_COMPLETION_CLEARED",
                    "A completed Convoy state had no linked Dispatch custody handoff.");
            Convoy.ConsumeCompletedState();
            Response.ReleaseAll();
            Backup.BeginStandDown("Custody operation completed.");
        }

        private static bool IsActiveRoadScene(LSPDDispatchState state)
        {
            switch (state)
            {
                case LSPDDispatchState.OnScene:
                case LSPDDispatchState.Investigating:
                case LSPDDispatchState.SuspectFleeing:
                case LSPDDispatchState.SuspectCompliant:
                case LSPDDispatchState.SuspectResisting:
                case LSPDDispatchState.Arrested:
                case LSPDDispatchState.AwaitingTransport:
                    return true;
                default:
                    return false;
            }
        }

        private bool IsPlayerAtActiveRoadScene(
            Ped player,
            bool gangActive,
            bool crimeActivityActive)
        {
            if (player == null || !player.Exists())
                return false;

            const float sceneTrafficRadius = 35.0f;
            try
            {
                if (gangActive)
                {
                    Ped threat = _gangResponse.ActiveThreat;
                    if (threat != null && threat.Exists()
                        && threat.Position.DistanceTo(player.Position) <= sceneTrafficRadius)
                        return true;
                }

                if (crimeActivityActive
                    && CrimeActivity.ActiveCriminals.Any(ped =>
                        ped != null && ped.Exists()
                        && ped.Position.DistanceTo(player.Position) <= sceneTrafficRadius))
                    return true;

                // A Convoy is a transport owner, not a city-wide traffic
                // scene.  Only a physically nearby route threat can qualify
                // for the local scene reaction while the player handles it.
                if (Convoy.Active
                    && Convoy.ActiveRouteThreats.Any(ped =>
                        ped != null && ped.Exists()
                        && ped.Position.DistanceTo(player.Position) <= sceneTrafficRadius))
                    return true;

                // Convoy transport is a local physical scene even when there
                // is no Dispatch incident left at the current Core state.
                // This keeps the existing traffic response around the actual
                // transport/prisoner without turning the whole patrol into a
                // city-wide traffic slowdown.
                if (Convoy.Active
                    && Convoy.SupportPosition != Vector3.Zero
                    && Convoy.SupportPosition.DistanceTo(player.Position) <= sceneTrafficRadius)
                    return true;

                if (!IsActiveRoadScene(Dispatch.State) || Dispatch.Current == null)
                    return false;

                if (Dispatch.Current.Origin.DistanceTo(player.Position) <= sceneTrafficRadius)
                    return true;

                return Dispatch.CurrentSuspects.Any(ped =>
                    ped != null && ped.Exists()
                    && ped.Position.DistanceTo(player.Position) <= sceneTrafficRadius);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_ACTIVE_SCENE_SCOPE_CHECK_FAILED", ex);
                return false;
            }
        }

        private void UpdateGangSample(Ped player)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextGangScan)
                return;
            _nextGangScan = _config.Performance.ThrottleHeavyScans
                ? now.AddMilliseconds(_config.Performance.HeavyScanIntervalMilliseconds)
                : now;
            if (player == null || !player.Exists())
            {
                _nearbyGangPeds = new Ped[0];
                return;
            }
            try
            {
                _nearbyGangPeds = World.GetNearbyPeds(player, 75f)
                    ?? new Ped[0];
            }
            catch (Exception ex)
            {
                _nearbyGangPeds = new Ped[0];
                _log.Exception("POLICE_GANG_SCAN_FAILED", ex);
            }
        }

        private void ProcessPoliceHotkeys()
        {
            LSPDControlBindings controls = _config.PoliceControls;
            if (WasPoliceKey(controls.PatrolKey, ref _patrolKeyDown))
            {
                bool wasPatrolling = Patrol.IsPatrolling;
                TogglePatrol();
                if (wasPatrolling && Patrol.IsPatrolling && !string.IsNullOrWhiteSpace(LastOperationMessage))
                    Notify(LastOperationMessage);
                return;
            }
            if (WasPoliceKey(controls.EmergencyKey, ref _emergencyKeyDown))
            {
                LastOperationMessage = CallBackup();
                Notify(LastOperationMessage);
            }
            if (WasPoliceKey(controls.ResetKey, ref _resetKeyDown))
            {
                LastOperationMessage = ResetPoliceRuntime();
                Notify(LastOperationMessage);
            }
            if (WasPoliceKey(controls.TransportKey, ref _transportKeyDown))
            {
                // R is an approval action only when the prisoner is waiting at
                // the station or the arrest has just been secured. Do not emit
                // a misleading rejection while the officer is still driving.
                if (Convoy.HoldingAtStation || Dispatch.State == LSPDDispatchState.Arrested)
                {
                    LastOperationMessage = RequestPrisonerTransport();
                    Notify(LastOperationMessage);
                }
            }
            if (WasPoliceKey(
                controls.TransportCompleteKey,
                ref _transportCompleteKeyDown))
            {
                if (Convoy.CanUseTerminalCompletion)
                {
                    LastOperationMessage = CompletePrisonerTransport();
                    Notify(LastOperationMessage);
                }
            }
        }

        private string ProcessStationPrisonTransferInput(bool allowGameplayInput, bool paused)
        {
            LSPDControlBindings controls = _config.PoliceControls;
            bool acceptKeyPressed = WasPoliceKey(
                controls.AcceptKey,
                ref _convoyAcceptKeyDown);
            bool rejectKeyPressed = WasPoliceKey(
                controls.RejectKey,
                ref _convoyRejectKeyDown);

            // Dispatch already owns the same Y/N pair while a callout offer is
            // active. This second, narrow gate is only for the distinct station
            // prison-transfer offer. Keep the keyboard edge states synchronized
            // even while no offer exists so a held key cannot approve a future
            // transfer accidentally.
            bool decisionInputAllowed = allowGameplayInput || Convoy.HoldingAtStation;
            bool controllerInput = decisionInputAllowed
                && !paused
                && IsControllerInputActive();
            bool controllerAccept = controllerInput
                && Game.IsControlJustPressed(GTA.Control.FrontendCancel);
            bool controllerReject = controllerInput
                && Game.IsControlJustPressed(GTA.Control.FrontendAccept);

            if (paused || !Convoy.HoldingAtStation || !decisionInputAllowed)
                return string.Empty;

            if (controllerAccept || acceptKeyPressed)
            {
                string result = RequestPrisonerTransport();
                Notify(result);
                return result;
            }
            if (controllerReject || rejectKeyPressed)
            {
                string result = DeclinePrisonerTransport();
                Notify(result);
                return result;
            }
            return string.Empty;
        }

        private void QueueConfigurationRefresh()
        {
            _configurationRefreshPending = true;
        }

        /// <summary>
        /// Applies only settings that the existing owners support at runtime.
        /// Each owner keeps the typed configuration reference supplied at
        /// construction; this method handles the few settings that require a
        /// stateful refresh or resource release on the game tick.
        /// </summary>
        private void ApplyPendingConfiguration()
        {
            if (!_configurationRefreshPending)
                return;

            _configurationRefreshPending = false;
            _config.NormalizeSettings();
            _log.Configure(
                _config.Logging.RuntimeEnabled,
                _config.Logging.DebugEnabled,
                _config.Logging.VerboseDiagnostics);

            if (_lastAppliedAudioEnabled != _config.EnableAudio
                || _lastAppliedAudioVolume != _config.AudioVolume)
            {
                Audio.SetOptions(_config.EnableAudio, _config.AudioVolume);
                _lastAppliedAudioEnabled = _config.EnableAudio;
                _lastAppliedAudioVolume = _config.AudioVolume;
            }

            if (IsPoliceAuthorityActive)
                _authority.RefreshSettings();
            if (!_config.Police.Response.Enabled)
                Response.ReleaseAll();
            if (!_config.Police.Backup.Enabled)
                Backup.Reset();
            if (!_config.Police.NpcResponse.Enabled)
                NpcResponse.Reset();

            _log.Runtime(
                "POLICE_RUNTIME_SETTINGS_APPLIED",
                "Central settings refresh applied to Authority, audio, Dispatch, NPC, response, backup, convoy, and patrol owners.");
        }

        private void SynchronizePoliceKeyStates()
        {
            if (_config == null || _config.PoliceControls == null)
                return;
            _patrolKeyDown = Game.IsKeyPressed(_config.PoliceControls.PatrolKey);
            _emergencyKeyDown = Game.IsKeyPressed(_config.PoliceControls.EmergencyKey);
            _resetKeyDown = Game.IsKeyPressed(_config.PoliceControls.ResetKey);
            _transportKeyDown = Game.IsKeyPressed(_config.PoliceControls.TransportKey);
            _transportCompleteKeyDown = Game.IsKeyPressed(
                _config.PoliceControls.TransportCompleteKey);
            _convoyAcceptKeyDown = Game.IsKeyPressed(_config.PoliceControls.AcceptKey);
            _convoyRejectKeyDown = Game.IsKeyPressed(_config.PoliceControls.RejectKey);
        }

        private static bool WasPoliceKey(Keys key, ref bool wasDown)
        {
            bool isDown = Game.IsKeyPressed(key);
            bool pressed = isDown && !wasDown;
            wasDown = isDown;
            return pressed;
        }

        private static bool IsControllerInputActive()
        {
            try
            {
                return !Function.Call<bool>(Hash.IS_USING_KEYBOARD_AND_MOUSE, 2);
            }
            catch
            {
                // Keep the keyboard Y/N path unambiguous if input-method
                // detection is unavailable; Escape/Backspace must never become
                // a prison-transfer decision by accident.
                return false;
            }
        }

        private void Notify(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                try { Notification.PostTicker(message, false, false); } catch { }
            }
        }
        internal bool ReloadPoliceData()
        {
            if (!RequireActive())
                return false;

            bool loaded = _profile.ReloadAll();
            bool npcLoaded = _npcDatabase.Reload();
            _authority.RefreshSettings();
            if (!loaded || !npcLoaded)
                return Fail("POLICE_DATA_RELOAD_FAILED", "Police XML data could not be reloaded.");
            _gangData.Reload();
            ActivateStationMarkers();

            _log.Runtime(
                "POLICE_DATA_RELOADED",
                "Agencies=" + _profile.Agencies.Count()
                + "; Stations=" + _profile.Stations.Count()
                + "; Peds=" + _profile.PedModels.Count()
                + "; Vehicles=" + _profile.Vehicles.Count()
                + "; Weapons=" + _profile.Weapons.Count()
                + "; CitizenModels=" + _npcDatabase.ModelCount);
            LastOperationMessage = "Police profile, citizen database, and XML collections reloaded.";
            return true;
        }

        internal bool ApplySavedProfile()
        {
            bool pedApplied;
            bool vehicleApplied;
            bool weaponsApplied;
            bool applied = ApplySavedProfile(
                true,
                out pedApplied,
                out vehicleApplied,
                out weaponsApplied);
            if (!applied)
                QueuePendingSavedProfileApply(pedApplied, vehicleApplied, weaponsApplied);
            return applied;
        }

        internal bool TogglePatrol()
        {
            if (!RequireActive())
                return false;
            if (!_config.Police.Patrol.Enabled)
                return Fail(
                    "POLICE_PATROL_DISABLED",
                    "Police Patrol is disabled in LS Immersive settings.");
            if (Patrol.IsPatrolling)
            {
                if (Dispatch.HasIncident
                    || Convoy.Active
                    || CrimeActivity.BlocksOtherPoliceActivities
                    || _gangResponse.HasActiveIncident
                    || NpcResponse.HasActiveInteraction
                    || (Backup.BlocksPlayerActivities
                        && Backup.State != LSPDBackupAssignmentState.StandingDown))
                {
                    LastOperationMessage = "Finish or cancel the active Police assignment before ending Patrol.";
                    _log.Runtime(
                        "POLICE_PATROL_END_BLOCKED",
                        "Patrol end ignored because an active Police assignment still owns the session.");
                    return false;
                }

                Patrol.End();
                Dispatch.Reset();
                _automaticGroupBackupIncidentId = string.Empty;
                Convoy.Reset();
                Response.Reset();
                Backup.Reset();
                _gangResponse.Reset();
                NpcResponse.Reset();
                CrimeActivity.Reset();
                _crimeActivityWasActive = false;
            }
            else
            {
                Patrol.Start();
                bool vehicleReady = PreparePatrolVehicle();
                string firstDispatch = Dispatch.RequestOfferNow(Game.Player.Character);
                bool dispatchReady = Dispatch.HasOffer;
                _log.Runtime(
                    "POLICE_PATROL_INITIAL_ACTIVITY",
                    "DispatchReady=" + dispatchReady + "; Result=" + firstDispatch);
                return Succeed("POLICE_PATROL_AVAILABILITY",
                    (vehicleReady
                        ? "Patrol active. Your current Police vehicle was retained or prepared."
                        : "Patrol active. A Police vehicle could not be prepared; you may continue on foot.")
                    + (dispatchReady
                        ? " A Dispatch call is ready for your response."
                        : " Dispatch is standing by for the next eligible XML activity."));
            }
            return Succeed("POLICE_PATROL_AVAILABILITY",
                "Patrol availability ended.");
        }

        internal bool StartPatrolTowardSelectedStation()
        {
            if (!RequireActive())
                return false;
            if (!_config.Police.Patrol.Enabled)
                return Fail(
                    "POLICE_PATROL_DISABLED",
                    "Police Patrol is disabled in LS Immersive settings.");
            if (Dispatch.HasIncident || Convoy.Active || CrimeActivity.BlocksOtherPoliceActivities)
                return Fail(
                    "POLICE_PATROL_ROUTE_BLOCKED",
                    "The active Police assignment already owns the current GPS route.");

            LSPDPoliceStationDefinition station =
                _profile.FindStation(_profile.Selection.StationId);
            if (station == null)
                return Fail(
                    "POLICE_PATROL_STATION_ROUTE_FAILED",
                    "The selected Police station is unavailable.");

            bool routeSet = _config.ShowWaypoint && TrySetStationWaypoint(station);

            if (!Patrol.IsPatrolling)
                Patrol.Start();
            bool vehicleReady = PreparePatrolVehicle();

            return Succeed(
                "POLICE_PATROL_STATION_ROUTE",
                "Patrol active." + (routeSet
                    ? " GPS set to " + station.DisplayName + "."
                    : " Route guidance is disabled.") + (vehicleReady
                    ? " with your Police vehicle ready."
                    : ". A Police vehicle could not be prepared.") );
        }

        internal bool ReceiveDispatch(LSPDDispatchEvent incident)
        {
            // A real incident producer must supply the offer. This bridge will
            // not manufacture calls or interrupt a player who never started
            // patrol; accepted calls remain owned by LSPDDispatch.
            return IsPoliceAuthorityActive && Patrol.IsPatrolling
                && !NpcResponse.HasActiveInteraction
                && !_gangResponse.HasActiveIncident
                && !CrimeActivity.BlocksOtherPoliceActivities
                && incident != null && Dispatch.Receive(incident);
        }

        internal LSPDAudioResult DecideDispatch(bool? accept)
        {
            if (!IsPoliceAuthorityActive || !Patrol.IsPatrolling || !Dispatch.HasOffer)
                return LSPDAudioResult.Inactive;
            LSPDAudioResult result = !accept.HasValue ? Dispatch.Acknowledge()
                : accept.Value ? Dispatch.Accept() : Dispatch.Decline();
            LastOperationMessage = !accept.HasValue
                ? "Dispatch information acknowledged."
                : accept.Value
                    ? Dispatch.HasIncident
                        ? "Dispatch accepted. Proceed to the marked scene."
                        : "The Dispatch scene could not be created safely. The assignment was cancelled."
                    : "Dispatch declined. You remain available for patrol.";
            return result;
        }

        internal string RequestDispatch()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before requesting a Dispatch.";
                return LastOperationMessage;
            }
            if (Dispatch.HasIncident)
            {
                LastOperationMessage = "Finish or cancel the active Dispatch before requesting another one.";
                return LastOperationMessage;
            }
            if (Convoy.Active || CrimeActivity.BlocksOtherPoliceActivities
                || _gangResponse.HasActiveIncident || NpcResponse.HasActiveInteraction
                || (Backup.BlocksPlayerActivities
                    && Backup.State != LSPDBackupAssignmentState.StandingDown))
            {
                LastOperationMessage = "Finish the current Police activity before requesting a Dispatch.";
                return LastOperationMessage;
            }

            LastOperationMessage = Dispatch.RequestOfferNow(Game.Player.Character);
            return LastOperationMessage;
        }

        internal string InvestigateDispatch()
        {
            if (!RequireActive())
                return LastOperationMessage;
            string message = Dispatch.Investigate(Game.Player.Character);
            LastOperationMessage = message;
            return message;
        }

        internal string SecureDispatchSuspect()
        {
            if (!RequireActive())
                return LastOperationMessage;
            string message = Dispatch.SecureSuspect(Game.Player.Character);
            LastOperationMessage = message;
            return message;
        }

        internal string RequestPrisonerTransport()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!_config.Police.Convoy.Enabled)
            {
                LastOperationMessage = "Prisoner transport is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }

            if (Dispatch.HasConvoyCustodyHandoff)
            {
                if (Convoy.HoldingAtStation)
                {
                    string transfer = Convoy.ContinueToPrison();
                    if (Convoy.State == LSPDDispatchState.PrisonTransfer)
                        Dispatch.SetConvoyState(LSPDDispatchState.PrisonTransfer);
                    LastOperationMessage = transfer;
                    return transfer;
                }

                LastOperationMessage = Convoy.Active
                    ? "Prisoner custody is already in transport. Continue following the active Convoy route."
                    : "Prisoner custody is already owned by Convoy and is being reconciled.";
                return LastOperationMessage;
            }

            // Independent Convoy deliberately has no Dispatch incident. Once
            // its physical station handoff is complete, the same approval
            // control must continue that Convoy directly instead of routing
            // through Dispatch's Arrested-state requirement.
            if (Convoy.IsRequestedConvoyActivity && Convoy.HoldingAtStation)
            {
                LastOperationMessage = Convoy.ContinueToPrison();
                return LastOperationMessage;
            }

            if (Dispatch.State != LSPDDispatchState.Arrested)
            {
                LastOperationMessage = "Secure and arrest the suspect before requesting transport.";
                return LastOperationMessage;
            }

            List<Ped> arrestedSuspects = Dispatch.CurrentArrestedSuspects.ToList();
            Ped pickupPrisoner = arrestedSuspects.FirstOrDefault();
            Vector3 pickupPosition = pickupPrisoner != null && pickupPrisoner.Exists()
                ? pickupPrisoner.Position
                : Dispatch.Current == null
                    ? Game.Player.Character.Position
                    : Dispatch.Current.Origin;
            Ped player = Game.Player.Character;
            Vehicle playerVehicle = player == null ? null : player.CurrentVehicle;
            string custody = arrestedSuspects.Count == 1
                && Convoy.CanUsePlayerVehicleCustody(player, playerVehicle)
                ? Convoy.StartPlayerVehicleCustody(
                    pickupPrisoner, pickupPosition, playerVehicle)
                : Convoy.Start(arrestedSuspects, pickupPosition);
            if (Convoy.Active)
            {
                if (Dispatch.TransferCustodyOwnershipToConvoy())
                {
                    Dispatch.SetAwaitingTransport();
                    Backup.BeginStandDown("Dispatch suspects entered Convoy custody.");
                }
                else
                {
                    Convoy.Reset();
                    custody = "Prisoner custody could not be handed to Convoy safely.";
                }
            }
            LastOperationMessage = custody;
            return custody;
        }

        /// <summary>
        /// Starts the player-selected Convoy activity without fabricating a
        /// Dispatch incident. Dispatch only supplies an existing criminal
        /// profile; Convoy owns the staged prisoner, transport, road threat,
        /// prison handoff, and terminal cleanup from this point onward.
        /// </summary>
        internal string RequestConvoyActivity()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before requesting a Convoy activity.";
                return LastOperationMessage;
            }
            if (!_config.Police.Convoy.Enabled || !_config.Police.Convoy.RequestedConvoyEnabled)
            {
                LastOperationMessage = "Player Convoy Request is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            if (Dispatch.HasIncident)
            {
                LastOperationMessage = "Finish or cancel the active Dispatch assignment before requesting a Convoy.";
                return LastOperationMessage;
            }
            if (Convoy.Active || (Backup.BlocksPlayerActivities
                && Backup.State != LSPDBackupAssignmentState.StandingDown))
            {
                LastOperationMessage = "A Police custody or backup assignment is already active.";
                return LastOperationMessage;
            }
            if (_gangResponse.HasActiveIncident)
            {
                LastOperationMessage = "Resolve the active gang threat before requesting a Convoy.";
                return LastOperationMessage;
            }
            if (CrimeActivity.BlocksOtherPoliceActivities)
            {
                LastOperationMessage = "Resolve or close the active Crime Activity before requesting a Convoy.";
                return LastOperationMessage;
            }
            if (NpcResponse.HasActiveInteraction)
            {
                LastOperationMessage = "Finish the active NPC interaction before requesting a Convoy.";
                return LastOperationMessage;
            }

            LSPDCriminalProfileDefinition prisonerProfile;
            LSPDCriminalProfileDefinition routeThreatProfile;
            if (!Dispatch.TryChooseConvoyRequestProfiles(out prisonerProfile, out routeThreatProfile))
            {
                LastOperationMessage = "Convoy Request needs at least one usable criminal profile in LSPDCriminalProfile.xml.";
                return LastOperationMessage;
            }

            string result = Convoy.StartRequestedConvoy(prisonerProfile, routeThreatProfile);
            if (Convoy.Active)
            {
                Dispatch.DeferOffersForContextActivity("Convoy Request");
                _log.Runtime(
                    "POLICE_CONVOY_REQUESTED",
                    "PrisonerProfile=" + prisonerProfile.Id
                    + "; RouteThreatProfile=" + (routeThreatProfile == null ? "none" : routeThreatProfile.Id));
            }
            LastOperationMessage = result;
            return result;
        }

        internal string DeclinePrisonerTransport()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Convoy.HoldingAtStation)
            {
                LastOperationMessage = "No prisoner is waiting at the selected Police station.";
                return LastOperationMessage;
            }

            string result = Convoy.DeclineAtStation();
            if (Convoy.Completed && Dispatch.HasConvoyCustodyHandoff)
            {
                result = Dispatch.CompleteTransport();
                Response.ReleaseAll();
                Backup.BeginStandDown("Custody was completed at the station.");
            }
            if (Convoy.Completed)
                Convoy.ConsumeCompletedState();
            LastOperationMessage = result;
            return result;
        }

        internal string CompletePrisonerTransport()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!_config.Police.Convoy.TerminalCompletionEnabled)
            {
                LastOperationMessage = "Terminal transport completion is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            string result = Convoy.CompleteNow();
            if (Convoy.Completed && Dispatch.HasConvoyCustodyHandoff)
            {
                result = Dispatch.CompleteTransport();
                Response.ReleaseAll();
                Backup.BeginStandDown("Custody transport was completed by the recovery control.");
            }
            else if (Convoy.Completed && Convoy.IsRequestedConvoyActivity)
            {
                result = "Convoy Request completed by the officer recovery control.";
                Response.ReleaseAll();
                Backup.BeginStandDown("Requested Convoy completed by the recovery control.");
                Dispatch.DeferOffersForContextActivity("Convoy Request");
                _log.Runtime("POLICE_CONVOY_REQUEST_TERMINAL_COMPLETION", "Officer used terminal completion.");
            }
            if (Convoy.Completed)
                Convoy.ConsumeCompletedState();
            LastOperationMessage = result;
            return result;
        }

        internal string CallBackup()
        {
            return CallBackup(true);
        }

        internal string CallDefaultBackup()
        {
            return CallBackup(false);
        }

        internal string CallFavoriteBackup()
        {
            return CallBackup(true);
        }

        private string TryStartNpcPlayerVehicleCustody(Ped player)
        {
            if (!NpcResponse.CanBeginPlayerVehicleCustody)
                return null;

            Vehicle vehicle = FindNpcPlayerCustodyVehicle(player);
            if (vehicle == null)
                return null;

            Ped prisoner = NpcResponse.ActiveSubject;
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                return "The compliant citizen is no longer available for personal custody.";

            string custody = Convoy.StartPlayerVehicleCustodyFromNpc(
                prisoner,
                prisoner.Position,
                vehicle);
            if (!Convoy.Active)
                return custody;

            if (!NpcResponse.TransferArrestedSubjectToConvoy())
            {
                Convoy.Reset();
                _log.StateFailure(
                    "NPC_CUSTODY_TRANSFER_TO_CONVOY_FAILED",
                    "Convoy started, but the arrested NPC owner could not release custody safely. Ped="
                    + prisoner.Handle);
                return "The arrested citizen could not be transferred safely to Convoy custody.";
            }

            Backup.BeginStandDown("NPC citizen custody transferred to player Convoy.");
            _log.Runtime(
                "NPC_PLAYER_VEHICLE_CUSTODY_STARTED",
                "Ped=" + prisoner.Handle + "; Vehicle=" + vehicle.Handle
                + "; Model=" + vehicle.Model.Hash + "; Motorcycle=false");
            return custody;
        }

        private Vehicle FindNpcPlayerCustodyVehicle(Ped player)
        {
            if (player == null || !player.Exists())
                return null;
            if (player.IsInVehicle())
                return null;

            Vehicle current = player.CurrentVehicle;
            if (current != null && current.Exists()
                && IsKnownPoliceVehicle(current)
                && !current.Model.IsBike)
            {
                if (Convoy.HasRearCustodySeat(current))
                    return current;
                _log.Runtime(
                    "NPC_PLAYER_CUSTODY_VEHICLE_REJECTED",
                    "Vehicle=" + current.Handle + "; Model=" + current.Model.Hash
                    + "; Reason=NO_REAR_CUSTODY_SEAT");
            }

            Vehicle managed = _managedPoliceVehicle;
            if (managed == null || !managed.Exists()
                || !IsKnownPoliceVehicle(managed)
                || managed.Model.IsBike)
                return null;

            if (!Convoy.HasRearCustodySeat(managed))
            {
                _log.Runtime(
                    "NPC_PLAYER_CUSTODY_VEHICLE_REJECTED",
                    "Vehicle=" + managed.Handle + "; Model=" + managed.Model.Hash
                    + "; Reason=NO_REAR_CUSTODY_SEAT");
                return null;
            }

            // The player has already left the patrol car to make the arrest.
            // Keep personal custody available only while that same managed
            // vehicle remains a practical walkable distance from the scene.
            return managed.Position.DistanceTo(player.Position) <= 75.0f
                ? managed : null;
        }

        private string CallBackup(bool preferSavedFavorite)
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Dispatch.HasIncident && !Convoy.Active
                && !CrimeActivity.BlocksOtherPoliceActivities
                && !_gangResponse.HasActiveIncident
                && !NpcResponse.CanRequestBackup
                && !NpcResponse.HasBackupAssignment)
            {
                LastOperationMessage = "Backup is available after a Dispatch assignment, Crime Activity, Convoy, gang threat, or detained/fleeing NPC contact is active.";
                return LastOperationMessage;
            }
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                LastOperationMessage = "The player character is unavailable.";
                return LastOperationMessage;
            }

            // A compliant NPC contact uses the already-managed Police car or
            // van for personal custody. Only a motorcycle (or no usable
            // custody vehicle) continues into the existing Backup owner.
            string playerCustody = TryStartNpcPlayerVehicleCustody(player);
            if (!string.IsNullOrWhiteSpace(playerCustody))
            {
                LastOperationMessage = playerCustody;
                return LastOperationMessage;
            }

            if (!_config.Police.Backup.Enabled)
            {
                LastOperationMessage = "Backup is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }

            if (Dispatch.HasIncident)
            {
                LastOperationMessage = Backup.Request(Dispatch, player, preferSavedFavorite);
                return LastOperationMessage;
            }

            if (Convoy.Active)
            {
                LastOperationMessage = Backup.RequestConvoySupport(Convoy, player, preferSavedFavorite);
                return LastOperationMessage;
            }

            if (CrimeActivity.BlocksOtherPoliceActivities)
            {
                // Keep the Crime Activity in its normal investigation state
                // until the separate Backup owner has actually accepted the
                // assignment. A failed request must not leave the scene
                // falsely marked as awaiting support.
                LastOperationMessage = Backup.RequestCrimeActivitySupport(
                    CrimeActivity, player, preferSavedFavorite);
                if (!LastOperationMessage.StartsWith("Backup requested", StringComparison.OrdinalIgnoreCase)
                    && !LastOperationMessage.StartsWith("Existing backup", StringComparison.OrdinalIgnoreCase)
                    && !LastOperationMessage.StartsWith("Backup is already", StringComparison.OrdinalIgnoreCase))
                    return LastOperationMessage;
                string supportState = CrimeActivity.RequestBackupSupport();
                if (!string.IsNullOrWhiteSpace(supportState))
                    LastOperationMessage = supportState + " " + LastOperationMessage;
                return LastOperationMessage;
            }

            if (NpcResponse.CanRequestBackup)
            {
                string backupResult = Backup.RequestNpcSupport(
                    NpcResponse, player, preferSavedFavorite);
                if (!backupResult.StartsWith("Backup requested", StringComparison.OrdinalIgnoreCase)
                    && !backupResult.StartsWith("Existing backup", StringComparison.OrdinalIgnoreCase)
                    && !backupResult.StartsWith("Backup is already", StringComparison.OrdinalIgnoreCase))
                {
                    LastOperationMessage = backupResult;
                    return LastOperationMessage;
                }
                string handoff = NpcResponse.RequestBackupSupportCommand();
                LastOperationMessage = handoff + " " + backupResult;
                return LastOperationMessage;
            }

            if (NpcResponse.HasBackupAssignment)
            {
                LastOperationMessage = "Backup is already assigned to the active NPC contact.";
                return LastOperationMessage;
            }

            // Gang Threats now use the same bounded, road-safe Backup owner as
            // the other player-requested support paths. The Gang Adapter still
            // owns the hostile actors; LSPDBackUp owns only its Police units
            // and their combat tasks.
            Response.ReleaseAll();
            LastOperationMessage = Backup.RequestGangSupport(
                _gangResponse,
                player,
                preferSavedFavorite);
            return LastOperationMessage;
        }

        internal string CancelActiveDispatch()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (CrimeActivity.BlocksOtherPoliceActivities
                && !Dispatch.HasIncident && !Convoy.Active)
            {
                Backup.BeginStandDown("Crime Activity cancelled by officer recovery control.");
                LastOperationMessage = CrimeActivity.IgnoreCurrentActivity(Game.Player.Character);
                Response.ReleaseAll();
                Dispatch.DeferOffersForContextActivity("Crime Activity cancellation");
                _log.Runtime("POLICE_CRIME_ACTIVITY_CANCELLED", LastOperationMessage);
                return LastOperationMessage;
            }
            if (Convoy.Active && Convoy.IsRequestedConvoyActivity && !Dispatch.HasIncident)
            {
                Backup.BeginStandDown("Requested Convoy cancelled by officer recovery control.");
                Convoy.Reset();
                _gangResponse.Reset();
                Response.ReleaseAll();
                Dispatch.DeferOffersForContextActivity("Convoy Request cancellation");
                LastOperationMessage = "Convoy Request cancelled. Owned Convoy state was cleared.";
                _log.Runtime("POLICE_CONVOY_REQUEST_CANCELLED", "Officer cancelled player-requested Convoy activity.");
                return LastOperationMessage;
            }
            Convoy.Reset();
            Backup.Reset();
            _gangResponse.Reset();
            string result = Dispatch.Cancel("Cancelled by officer recovery control.");
            _automaticGroupBackupIncidentId = string.Empty;
            Response.ReleaseAll();
            LastOperationMessage = result;
            return result;
        }

        internal string ResetPoliceRuntime()
        {
            if (!RequireActive())
                return LastOperationMessage;
            Convoy.Reset();
            Dispatch.Reset();
            _automaticGroupBackupIncidentId = string.Empty;
            Response.Reset();
            Backup.Reset();
            _gangResponse.Reset();
            NpcResponse.Reset();
            CrimeActivity.Reset();
            _gangIncidentWasActive = false;
            _crimeActivityWasActive = false;
            _npcInteractionWasActive = false;
            ClearPendingSavedProfileApply();
            LastOperationMessage = "Police runtime recovered. Authority remains active.";
            _log.Runtime("POLICE_RUNTIME_RECOVERED", "Dispatch, Convoy, Backup, Response, Gang, Crime Activity, and NPC-owned state reset.");
            return LastOperationMessage;
        }

        internal string BeginNpcInteraction()
        {
            if (!CanBeginNpcContact())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.BeginInteractionCommand();
            return LastOperationMessage;
        }

        internal string BeginNpcFootInteraction()
        {
            if (!CanBeginNpcContact())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.BeginFootInteractionCommand();
            return LastOperationMessage;
        }

        internal string BeginNpcTrafficInteraction()
        {
            if (!CanBeginNpcContact())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.BeginTrafficInteractionCommand();
            return LastOperationMessage;
        }

        private bool CanBeginNpcContact()
        {
            if (!RequireActive() || !Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before beginning an NPC interaction.";
                return false;
            }
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || CrimeActivity.BlocksOtherPoliceActivities)
            {
                LastOperationMessage = Dispatch.HasIncident
                    ? "NPC contact is paused while a Dispatch scene is active."
                    : Convoy.Active
                        ? "NPC contact is paused while a Convoy activity is active."
                        : _gangResponse.HasActiveIncident
                            ? "NPC contact is paused while a gang threat is active."
                            : "NPC contact is paused while a Crime Activity investigation is active.";
                return false;
            }
            return true;
        }

        internal string AcceptNpcInteraction()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.AcceptInteractionCommand();
            return LastOperationMessage;
        }

        internal string RejectNpcInteraction()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = NpcResponse.RejectInteractionCommand();
            return LastOperationMessage;
        }

        internal bool ObserveCrimeIntelligence()
        {
            if (!RequireActive())
                return false;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before reviewing nearby Crime Activity intelligence.";
                return false;
            }
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || NpcResponse.HasActiveInteraction || CrimeActivity.BlocksOtherPoliceActivities
                || (Backup.BlocksPlayerActivities
                    && Backup.State != LSPDBackupAssignmentState.StandingDown))
            {
                LastOperationMessage = "Nearby Crime Activity observation waits until the current Police activity is clear.";
                return false;
            }
            Ped player = Game.Player.Character;
            int count = CrimeActivity.Observe(player, _gangData);
            LastOperationMessage = count == 0
                ? "No authored criminal activity tip was observed nearby."
                : count + " criminal activity tip(s) are available for review.";
            _log.Runtime(
                "POLICE_CRIME_INTELLIGENCE_OBSERVED",
                "Count=" + count + "; DispatchActive=" + Dispatch.HasIncident
                + "; GangIncidentActive=" + _gangResponse.HasActiveIncident);
            return true;
        }

        internal string RequestCrimeActivity()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before requesting a Crime Activity.";
                return LastOperationMessage;
            }
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || NpcResponse.HasActiveInteraction || CrimeActivity.BlocksOtherPoliceActivities
                || (Backup.BlocksPlayerActivities
                    && Backup.State != LSPDBackupAssignmentState.StandingDown))
            {
                LastOperationMessage = "Crime Activity request waits until the current Police activity is clear.";
                return LastOperationMessage;
            }

            LastOperationMessage = CrimeActivity.RequestNearbyActivity(
                Game.Player.Character, _gangData);
            _log.Runtime(
                "POLICE_CRIME_ACTIVITY_REQUESTED",
                "Available=" + CrimeActivity.HasAvailableActivity
                + "; Message=" + LastOperationMessage);
            return LastOperationMessage;
        }

        internal string SetCrimeIntelWaypoint(string activityId)
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || NpcResponse.HasActiveInteraction || CrimeActivity.BlocksOtherPoliceActivities)
            {
                LastOperationMessage = "The current Police assignment already owns the active GPS route.";
                return LastOperationMessage;
            }
            LSPDActivityIntel intel;
            if (!CrimeActivity.TryGetIntel(activityId, out intel))
            {
                LastOperationMessage = "Observe nearby crime intelligence before setting a route.";
                return LastOperationMessage;
            }
            if (!_config.ShowWaypoint)
            {
                LastOperationMessage = "Route guidance is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            try
            {
                World.WaypointPosition = intel.Position;
                LastOperationMessage = "GPS set to " + intel.Location.Name + ".";
                _log.Runtime(
                    "POLICE_CRIME_INTELLIGENCE_GPS",
                    "Activity=" + intel.Id + "; Location=" + intel.Location.Id);
            }
            catch (Exception ex)
            {
                LastOperationMessage = "The intelligence location could not be placed on the GPS.";
                _log.Exception("POLICE_CRIME_INTELLIGENCE_GPS_FAILED", ex);
            }
            return LastOperationMessage;
        }

        internal string BeginCrimeActivity(string activityId)
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (!Patrol.IsPatrolling)
            {
                LastOperationMessage = "Start Police Patrol before investigating Criminal Activity.";
                return LastOperationMessage;
            }
            if (!_config.Police.CrimeActivity.Enabled)
            {
                LastOperationMessage = "Crime Activity is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            if (Dispatch.HasIncident || Convoy.Active || _gangResponse.HasActiveIncident
                || NpcResponse.HasActiveInteraction)
            {
                LastOperationMessage = Dispatch.HasIncident
                    ? "Finish or cancel the active Dispatch before investigating Crime Activity."
                    : Convoy.Active
                        ? "Finish or cancel the active Convoy before investigating Crime Activity."
                        : _gangResponse.HasActiveIncident
                            ? "Resolve the active gang threat before investigating Crime Activity."
                            : "Finish the active NPC interaction before investigating Crime Activity.";
                return LastOperationMessage;
            }
            if (Backup.BlocksPlayerActivities
                && Backup.State != LSPDBackupAssignmentState.StandingDown)
            {
                LastOperationMessage = "A previous Backup assignment must stand down before a Crime Activity scene starts.";
                return LastOperationMessage;
            }

            Ped player = Game.Player.Character;
            string result = CrimeActivity.BeginInvestigation(player, activityId);
            if (CrimeActivity.Active)
            {
                Dispatch.DeferOffersForContextActivity("Crime Activity investigation");
                _log.Runtime("POLICE_CRIME_ACTIVITY_PLAYER_STARTED",
                    "Activity=" + (CrimeActivity.CurrentIntel == null ? string.Empty : CrimeActivity.CurrentIntel.Id));
            }
            else if (result.StartsWith("Travel within", StringComparison.OrdinalIgnoreCase))
            {
                // A player-selected lead is not an active assignment yet. Set
                // GPS only as a convenience and leave other patrol layers free.
                string route = SetCrimeIntelWaypoint(activityId);
                if (route.StartsWith("GPS set", StringComparison.OrdinalIgnoreCase))
                    result += " " + route;
            }
            LastOperationMessage = result;
            return result;
        }

        internal string ConfrontCrimeActivity()
        {
            if (!RequireActive())
                return LastOperationMessage;
            LastOperationMessage = CrimeActivity.Confront(Game.Player.Character);
            return LastOperationMessage;
        }

        internal string IgnoreCrimeActivity()
        {
            if (!RequireActive())
                return LastOperationMessage;
            bool wasActive = CrimeActivity.BlocksOtherPoliceActivities;
            LastOperationMessage = CrimeActivity.IgnoreCurrentActivity(Game.Player.Character);
            if (wasActive)
            {
                Backup.BeginStandDown("Crime Activity closed by the officer.");
                Response.ReleaseAll();
                Dispatch.DeferOffersForContextActivity("Crime Activity");
            }
            return LastOperationMessage;
        }

        internal bool ReloadCrimeIntelligence()
        {
            if (!RequireActive())
                return false;
            if (CrimeActivity.BlocksOtherPoliceActivities)
                return Fail("CRIME_INTELLIGENCE_RELOAD_BLOCKED",
                    "Finish or close the active Crime Activity before reloading its XML catalog.");
            if (!CrimeActivity.Reload())
                return Fail("CRIME_INTELLIGENCE_LOAD_FAILED", CrimeActivity.LastLoadError);
            CrimeActivity.ClearObservations();
            return Succeed("CRIME_INTELLIGENCE_LOADED",
                "Crime intelligence loaded: " + CrimeActivity.ActivityCount + " activities.");
        }

        internal bool ReloadGangIntelligence()
        {
            if (!RequireActive())
                return false;
            bool loaded = _gangData.Reload();
            if (!loaded && !string.IsNullOrWhiteSpace(_gangData.LastLoadError))
                return Fail("POLICE_GANG_DATA_LOAD_FAILED", _gangData.LastLoadError);
            LastOperationMessage = loaded
                ? "Read-only Gang & Turf intelligence loaded from the installed export or Plugin fallback."
                : "No optional Gang & Turf export was found in gangModData or the Police Plugin folder.";
            _log.Runtime(
                "POLICE_GANG_INTELLIGENCE_STATUS",
                "Loaded=" + loaded + "; Gangs=" + _gangData.Gangs.Count()
                + "; TurfZones=" + _gangData.TurfZones.Count()
                + "; MemberModels=" + _gangData.MemberModelCount
                + "; VehicleModels=" + _gangData.VehicleModelCount);
            return true;
        }

        internal bool SaveCurrentPersonalLoadout()
        {
            if (!RequireActive())
                return false;
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return Fail("POLICE_PLAYER_UNAVAILABLE", "The player character is unavailable.");
                var weapons = new List<LSPDPoliceFavoriteWeapon>();
                string activeId = null;
                Weapon current = player.Weapons.Current;
                foreach (WeaponHash hash in player.Weapons.GetAllWeaponHashes())
                {
                    Weapon weapon = player.Weapons[hash];
                    if (hash == WeaponHash.Unarmed || weapon == null || !weapon.IsPresent)
                        continue;
                    LSPDPoliceWeaponDefinition library = _profile.Weapons.FirstOrDefault(
                        item => ResolveWeaponHash(item.WeaponName) == unchecked((int)hash));
                    string identifier = library == null
                        ? "0x" + unchecked((uint)hash).ToString("X8", CultureInfo.InvariantCulture)
                        : library.WeaponName;
                    var components = new List<string>();
                    foreach (WeaponComponent component in weapon.Components)
                        if (component.Active)
                            components.Add("0x" + unchecked((uint)component.ComponentHash).ToString("X8", CultureInfo.InvariantCulture));
                    var saved = LSPDPoliceFavoriteWeapon.FromCaptured(
                        library == null ? "personal-" + identifier : library.Id,
                        library == null ? hash.ToString() : library.DisplayName,
                        identifier, weapon.Ammo, (int)weapon.Tint,
                        components);
                    weapons.Add(saved);
                    if (current != null && current.Hash == hash)
                        activeId = saved.Id;
                }
                if (weapons.Count == 0)
                    return Fail("PERSONAL_LOADOUT_EMPTY", "No carried weapons were found; the saved collection was not changed.");
                if (!_profile.SaveCapturedLoadout(weapons, activeId))
                    return Fail("PERSONAL_LOADOUT_SAVE_FAILED", "The personal weapon collection could not be saved.");
                return Succeed("PERSONAL_LOADOUT_SAVED", "Personal weapon collection saved: " + weapons.Count + " weapons.");
            }
            catch (Exception ex)
            {
                return Fail("PERSONAL_LOADOUT_CAPTURE_FAILED", "The current weapon collection could not be read.", ex);
            }
        }

        private bool ApplySavedProfile(bool announce)
        {
            bool pedApplied;
            bool vehicleApplied;
            bool weaponsApplied;
            return ApplySavedProfile(
                announce,
                out pedApplied,
                out vehicleApplied,
                out weaponsApplied);
        }

        private bool ApplySavedProfile(
            bool announce,
            out bool pedApplied,
            out bool vehicleApplied,
            out bool weaponsApplied)
        {
            if (!RequireActive())
            {
                pedApplied = false;
                vehicleApplied = false;
                weaponsApplied = false;
                return false;
            }

            pedApplied = true;
            vehicleApplied = true;
            weaponsApplied = true;

            if (!string.IsNullOrWhiteSpace(_profile.SelectedPedModelName))
                pedApplied = ApplyPedModel(_profile.SelectedPedModelName, false, false);
            if (!string.IsNullOrWhiteSpace(_profile.SelectedVehicleModelName))
                vehicleApplied = SpawnPoliceVehicle(_profile.SelectedVehicleModelName, false);
            // Startup/profile restoration only needs the selected duty weapon
            // to become playable. The personal weapon collection can contain
            // a large library (102 entries in the latest runtime); requesting
            // every weapon asset at once creates unnecessary streaming pressure
            // and can starve unrelated Police models such as Backup units.
            // Explicit Apply Personal Loadout still restores the full collection.
            weaponsApplied = ApplySelectedPoliceWeaponInternal(false);

            bool success = pedApplied && vehicleApplied && weaponsApplied;
            if (announce)
            {
                LastOperationMessage = success
                    ? "Saved Police profile applied."
                    : "Saved Police profile was only partially applied.";
                _log.Runtime(
                    "POLICE_PROFILE_APPLIED",
                    "Ped=" + pedApplied + "; Vehicle=" + vehicleApplied + "; Weapons=" + weaponsApplied);
            }
            return success;
        }

        private void QueuePendingSavedProfileApply(
            bool pedApplied,
            bool vehicleApplied,
            bool weaponsApplied)
        {
            _pendingProfilePedApply = !pedApplied
                && !string.IsNullOrWhiteSpace(_profile.SelectedPedModelName);
            _pendingProfileVehicleApply = !vehicleApplied
                && !string.IsNullOrWhiteSpace(_profile.SelectedVehicleModelName);
            _pendingProfileWeaponsApply = !weaponsApplied;
            if (!_pendingProfilePedApply
                && !_pendingProfileVehicleApply
                && !_pendingProfileWeaponsApply)
                return;

            DateTime now = DateTime.UtcNow;
            _nextPendingProfileApply = now.AddMilliseconds(250);
            _pendingProfileApplyDeadline = now.AddSeconds(20);
            _log.Runtime(
                "POLICE_PROFILE_ASSETS_QUEUED",
                "Ped=" + _pendingProfilePedApply
                + "; Vehicle=" + _pendingProfileVehicleApply
                + "; Weapons=" + _pendingProfileWeaponsApply);
        }

        private bool HasPendingSavedProfileAssets
        {
            get
            {
                return _pendingProfilePedApply
                    || _pendingProfileVehicleApply
                    || _pendingProfileWeaponsApply;
            }
        }

        private bool HasActivePoliceGameplayOwner
        {
            get
            {
                return Dispatch.HasIncident
                    || Convoy.Active
                    || Backup.BlocksPlayerActivities
                    || _gangResponse.HasActiveIncident
                    || CrimeActivity.BlocksOtherPoliceActivities
                    || NpcResponse.HasActiveInteraction;
            }
        }

        private void ProcessPendingSavedProfileApply(bool paused)
        {
            if (paused || !IsPoliceAuthorityActive
                || (!_pendingProfilePedApply
                    && !_pendingProfileVehicleApply
                    && !_pendingProfileWeaponsApply))
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _nextPendingProfileApply)
                return;

            // Never change the player's model, force a saved vehicle, or walk
            // a large weapon collection while another Police owner is already
            // conducting gameplay. Resume the same pending restore after the
            // scene/contact ends instead of allowing profile streaming to race
            // Dispatch, Convoy, Backup, Gang, Crime Activity, or NPC contact.
            if (HasActivePoliceGameplayOwner)
            {
                _nextPendingProfileApply = now.AddMilliseconds(750);
                _pendingProfileApplyDeadline = now.AddSeconds(20);
                return;
            }

            if (now >= _pendingProfileApplyDeadline)
            {
                _log.Debug(
                    "POLICE_PROFILE_ASSETS_TIMEOUT",
                    "Saved Police assets did not finish streaming before the retry deadline. "
                    + "Ped=" + _pendingProfilePedApply
                    + "; Vehicle=" + _pendingProfileVehicleApply
                    + "; SelectedWeapon=" + (_profile.SelectedWeaponName ?? string.Empty)
                    + "; Weapons=" + _pendingProfileWeaponsApply
                    + "; PersonalWeapons=" + _profile.PersonalWeapons.Count() + ".");
                ClearPendingSavedProfileApply();
                return;
            }

            // Individual flags prevent a vehicle that has already spawned
            // successfully from being created again while a separate weapon or
            // ped asset is still streaming.
            string previousMessage = LastOperationMessage;
            if (_pendingProfilePedApply)
                _pendingProfilePedApply = !ApplyPedModel(
                    _profile.SelectedPedModelName,
                    false,
                    false);
            if (_pendingProfileVehicleApply)
                _pendingProfileVehicleApply = !SpawnPoliceVehicle(
                    _profile.SelectedVehicleModelName,
                    false);
            if (_pendingProfileWeaponsApply)
                _pendingProfileWeaponsApply = !ApplySelectedPoliceWeaponInternal(false);
            LastOperationMessage = previousMessage;

            if (!_pendingProfilePedApply
                && !_pendingProfileVehicleApply
                && !_pendingProfileWeaponsApply)
            {
                ClearPendingSavedProfileApply();
                _log.Runtime(
                    "POLICE_PROFILE_ASSETS_APPLIED",
                    "Saved Police profile completed after streamed assets became available.");
                return;
            }

            _nextPendingProfileApply = now.AddMilliseconds(750);
        }

        private void ClearPendingSavedProfileApply()
        {
            _pendingProfilePedApply = false;
            _pendingProfileVehicleApply = false;
            _pendingProfileWeaponsApply = false;
            _nextPendingProfileApply = DateTime.MinValue;
            _pendingProfileApplyDeadline = DateTime.MinValue;
        }

        internal bool SelectAgency(string agencyId)
        {
            if (!RequireActive())
                return false;
            if (!_profile.TrySelectAgency(agencyId))
                return Fail("POLICE_AGENCY_INVALID", "That Police department is unavailable.");

            LSPDPoliceAgencyDefinition value = _profile.FindAgency(agencyId);
            return Succeed(
                "POLICE_AGENCY_SELECTED",
                "Police department selected: " + value.DisplayName + ".");
        }

        internal bool SelectStation(string stationId)
        {
            if (!RequireActive())
                return false;
            if (!_profile.TrySelectStation(stationId))
                return Fail("POLICE_STATION_INVALID", "That Police station is unavailable.");

            LSPDPoliceStationDefinition value = _profile.FindStation(stationId);
            return Succeed(
                "POLICE_STATION_SELECTED",
                "Police station selected: " + value.DisplayName + ".");
        }

        internal string SetSelectedStationWaypoint()
        {
            if (!RequireActive())
                return LastOperationMessage;
            if (Dispatch.HasIncident || Convoy.Active)
            {
                LastOperationMessage = "The active Police assignment already owns the current GPS route.";
                return LastOperationMessage;
            }
            if (!_config.ShowWaypoint)
            {
                LastOperationMessage = "Route guidance is disabled in LS Immersive settings.";
                return LastOperationMessage;
            }
            LSPDPoliceStationDefinition station =
                _profile.FindStation(_profile.Selection.StationId);
            if (station == null || !TrySetStationWaypoint(station))
            {
                LastOperationMessage = "The selected Police station could not be placed on the GPS.";
                return LastOperationMessage;
            }
            LastOperationMessage = "GPS set to " + station.DisplayName + ".";
            return LastOperationMessage;
        }

        internal bool SelectPolicePed(string idOrModel)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceModelDefinition value = _profile.FindPed(idOrModel);
            if (value == null)
                return Fail("POLICE_PED_INVALID", "That Police character is unavailable.");
            if (!ApplyPedModel(value.ModelName, true))
                return false;
            if (!_profile.TrySelectPed(value.Id))
                return Fail("POLICE_PED_SELECTION_FAILED", "The Police character could not be selected.");

            return Succeed(
                "POLICE_PED_SELECTED",
                "Police character selected: " + value.DisplayName + ".");
        }

        internal bool SelectPoliceVehicle(string idOrModel)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceVehicleDefinition value = _profile.FindVehicle(idOrModel);
            if (value == null)
                return Fail("POLICE_VEHICLE_INVALID", "That Police vehicle is unavailable.");
            if (!SpawnPoliceVehicle(value.ModelName, true))
                return false;
            if (!_profile.TrySelectVehicle(value.Id))
                return Fail("POLICE_VEHICLE_SELECTION_FAILED", "The Police vehicle could not be selected.");

            return Succeed(
                "POLICE_VEHICLE_SELECTED",
                "Police vehicle selected: " + value.DisplayName + ".");
        }

        internal bool SelectPoliceWeapon(string idOrWeapon)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceWeaponDefinition value = _profile.FindWeapon(idOrWeapon);
            if (value == null)
                return Fail("POLICE_WEAPON_INVALID", "That Police weapon is unavailable.");
            if (!GiveWeapon(value.WeaponName, value.DefaultAmmo, true))
                return false;
            if (!_profile.TrySelectWeapon(value.Id))
                return Fail("POLICE_WEAPON_SELECTION_FAILED", "The Police weapon could not be selected.");

            return Succeed(
                "POLICE_WEAPON_SELECTED",
                "Police weapon selected: " + value.DisplayName + ".");
        }

        internal bool SetSelectedPedAsFavourite()
        {
            if (!RequireActive())
                return false;
            string model = _profile.SelectedPedModelName;
            if (string.IsNullOrWhiteSpace(model))
                return Fail("PERSONAL_PED_EMPTY", "Select a Police character first.");
            return SaveFavouritePed(model);
        }

        internal bool SetCustomFavouritePed(string modelName)
        {
            if (!RequireActive())
                return false;
            string normalized = NormalizeModelName(modelName);
            if (string.IsNullOrEmpty(normalized))
                return Fail("PERSONAL_PED_EMPTY", "No addon ped model was entered.");
            if (!ApplyPedModel(normalized, true))
                return false;
            return SaveFavouritePed(normalized);
        }

        private bool SaveFavouritePed(string modelName)
        {
            if (!_profile.SetFavouritePed(modelName))
                return Fail("PERSONAL_PED_INVALID", "The personal Police character was not accepted.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The personal Police character could not be saved.");
            return Succeed(
                "PERSONAL_PED_SAVED",
                "Personal Police character saved: " + _profile.SelectedPedDisplayName + ".");
        }

        internal bool SetSelectedPedAsBackupFavourite()
        {
            if (!RequireActive())
                return false;
            string model = _profile.SelectedPedModelName;
            if (string.IsNullOrWhiteSpace(model))
                return Fail("BACKUP_PED_EMPTY", "Select a Police character before saving a Backup favorite.");
            return SaveFavouriteBackupPed(model);
        }

        internal bool SetCustomBackupFavourite(string modelName)
        {
            if (!RequireActive())
                return false;
            string normalized = NormalizeModelName(modelName);
            if (string.IsNullOrEmpty(normalized))
                return Fail("BACKUP_PED_EMPTY", "No addon Backup officer model was entered.");

            Model model = new Model(normalized);
            try
            {
                // Saving a support favorite must not change the player model.
                // Validate only the model type here; the Backup owner streams it
                // asynchronously when the player actually requests support.
                if (!model.IsValid || !model.IsPed)
                    return Fail("BACKUP_PED_INVALID", "Backup officer model is not available in this game build: " + normalized + ".");
            }
            catch (Exception ex)
            {
                return Fail("BACKUP_PED_INVALID", "Backup officer model could not be checked: " + normalized + ".", ex);
            }
            finally
            {
                try { model.MarkAsNoLongerNeeded(); } catch { }
            }
            return SaveFavouriteBackupPed(normalized);
        }

        internal bool SelectBackupFavourite(string idOrModel)
        {
            if (!RequireActive())
                return false;
            if (!_profile.SelectFavouriteBackupPed(idOrModel))
                return Fail("BACKUP_PED_SELECTION_FAILED", "That saved Backup officer is unavailable.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The selected Backup officer could not be saved.");
            LSPDPoliceFavoriteModel favorite = _profile.ActiveFavoriteBackupPed;
            return Succeed(
                "BACKUP_PED_SELECTED",
                "Preferred Backup officer: " + (favorite == null ? "None" : favorite.DisplayName) + ".");
        }

        private bool SaveFavouriteBackupPed(string modelName)
        {
            if (!_profile.SetFavouriteBackupPed(modelName))
                return Fail("BACKUP_PED_INVALID", "The Backup officer was not accepted.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The Backup officer could not be saved.");
            LSPDPoliceFavoriteModel favorite = _profile.ActiveFavoriteBackupPed;
            return Succeed(
                "BACKUP_PED_SAVED",
                "Backup officer saved: " + (favorite == null ? modelName : favorite.DisplayName) + ".");
        }

        internal bool SetSelectedVehicleAsFavourite()
        {
            if (!RequireActive())
                return false;
            string model = _profile.SelectedVehicleModelName;
            if (string.IsNullOrWhiteSpace(model))
                return Fail("PERSONAL_VEHICLE_EMPTY", "Select a Police vehicle first.");
            return SaveFavouriteVehicle(model);
        }

        internal bool SetCustomFavouriteVehicle(string modelName)
        {
            if (!RequireActive())
                return false;
            string normalized = NormalizeModelName(modelName);
            if (string.IsNullOrEmpty(normalized))
                return Fail("PERSONAL_VEHICLE_EMPTY", "No addon vehicle model was entered.");
            if (!SpawnPoliceVehicle(normalized, true))
                return false;
            return SaveFavouriteVehicle(normalized);
        }

        private bool SaveFavouriteVehicle(string modelName)
        {
            if (!_profile.SetFavouriteVehicle(modelName))
                return Fail("PERSONAL_VEHICLE_INVALID", "The personal Police vehicle was not accepted.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The personal Police vehicle could not be saved.");
            return Succeed(
                "PERSONAL_VEHICLE_SAVED",
                "Personal Police vehicle saved: " + _profile.SelectedVehicleDisplayName + ".");
        }

        internal bool AddPersonalWeapon(string idOrWeapon)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceWeaponDefinition value = _profile.FindWeapon(idOrWeapon);
            if (value == null)
                return Fail("PERSONAL_WEAPON_INVALID", "Select a weapon from the Police weapon library.");
            if (!GiveWeapon(value.WeaponName, value.DefaultAmmo, true))
                return false;
            if (!_profile.AddPersonalWeapon(value.Id))
                return Fail("PERSONAL_WEAPON_ADD_FAILED", "The weapon could not be added to the personal loadout.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The personal weapon collection could not be saved.");

            return Succeed(
                "PERSONAL_WEAPON_ADDED",
                value.DisplayName + " added to Personal Police Weapons.");
        }

        internal bool SelectPersonalWeapon(string idOrWeapon)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceFavoriteWeapon value = _profile.FindPersonalWeapon(idOrWeapon);
            if (value == null)
                return Fail("PERSONAL_WEAPON_INVALID", "That personal Police weapon is unavailable.");
            if (!GiveWeapon(value.WeaponName, value.Ammo, true, value))
                return false;
            _profile.SelectPersonalWeapon(value.Id);
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The active Police weapon could not be saved.");
            return Succeed(
                "PERSONAL_WEAPON_SELECTED",
                "Active Police weapon: " + value.DisplayName + ".");
        }

        internal bool RemovePersonalWeapon(string idOrWeapon)
        {
            if (!RequireActive())
                return false;
            LSPDPoliceFavoriteWeapon value = _profile.FindPersonalWeapon(idOrWeapon);
            if (value == null || !_profile.RemovePersonalWeapon(idOrWeapon))
                return Fail("PERSONAL_WEAPON_REMOVE_FAILED", "That personal Police weapon could not be removed.");
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "The personal weapon collection could not be saved.");
            return Succeed(
                "PERSONAL_WEAPON_REMOVED",
                value.DisplayName + " removed from Personal Police Weapons.");
        }

        internal bool CyclePersonalWeapon()
        {
            if (!RequireActive())
                return false;
            List<LSPDPoliceFavoriteWeapon> values = _profile.PersonalWeapons.ToList();
            if (values.Count == 0)
                return Fail("PERSONAL_WEAPON_EMPTY", "Personal Police Weapons is empty.");

            int current = values.FindIndex(value =>
                string.Equals(value.Id, _profile.Selection.WeaponId, StringComparison.OrdinalIgnoreCase));
            LSPDPoliceFavoriteWeapon next = values[(current + 1 + values.Count) % values.Count];
            return SelectPersonalWeapon(next.Id);
        }

        internal bool ReloadPersonalWeapons()
        {
            if (!RequireActive())
                return false;
            if (!_profile.ReloadPersonalWeapons())
                return Fail(
                    "PERSONAL_WEAPON_RELOAD_FAILED",
                    "Personal Police Weapons XML could not be reloaded.");
            if (!ApplyPersonalLoadoutInternal(false))
                return false;
            return Succeed(
                "PERSONAL_WEAPON_RELOADED",
                "Personal Police Weapons reloaded: " + _profile.PersonalWeapons.Count() + ".");
        }

        internal bool ApplyPersonalLoadout()
        {
            if (!RequireActive())
                return false;
            bool applied = ApplyPersonalLoadoutInternal(true);
            if (applied)
                Succeed("PERSONAL_LOADOUT_APPLIED", "Personal Police weapon collection applied.");
            return applied;
        }

        /// <summary>
        /// Restores only the currently selected Police weapon during automatic
        /// profile activation. The full personal collection remains persisted
        /// and can still be applied explicitly from the Profile UI. Keeping the
        /// automatic path to one asset prevents a large saved collection from
        /// monopolizing GTA streaming while Patrol/Backup are starting.
        /// </summary>
        private bool ApplySelectedPoliceWeaponInternal(bool reportPendingFailure)
        {
            LSPDPoliceFavoriteWeapon personal = _profile.SelectedPersonalWeapon;
            if (personal != null)
            {
                bool applied = GiveWeapon(
                    personal.WeaponName,
                    personal.Ammo,
                    true,
                    personal,
                    false);
                if (!applied && reportPendingFailure)
                    return Fail(
                        "POLICE_LOADOUT_ASSETS_PENDING",
                        "The selected Police weapon is still loading or unavailable. Retry after GTA finishes streaming it.");
                return applied;
            }

            string selected = _profile.SelectedWeaponName;
            if (string.IsNullOrWhiteSpace(selected))
                return true;

            bool selectedApplied = GiveWeapon(
                selected,
                _profile.SelectedWeaponAmmo,
                true,
                null,
                false);
            if (!selectedApplied && reportPendingFailure)
                return Fail(
                    "POLICE_LOADOUT_ASSETS_PENDING",
                    "The selected Police weapon is still loading or unavailable. Retry after GTA finishes streaming it.");
            return selectedApplied;
        }

        private bool ApplyPersonalLoadoutInternal(
            bool requirePersonalSet,
            bool reportPendingFailure = true)
        {
            List<LSPDPoliceFavoriteWeapon> personal = _profile.PersonalWeapons.ToList();
            if (personal.Count == 0)
            {
                if (requirePersonalSet)
                    return Fail("PERSONAL_WEAPON_EMPTY", "Personal Police Weapons is empty.");

                string selected = _profile.SelectedWeaponName;
                if (string.IsNullOrWhiteSpace(selected))
                    return true;
                bool selectedApplied = GiveWeapon(
                    selected,
                    _profile.SelectedWeaponAmmo,
                    true,
                    null,
                    false);
                if (!selectedApplied && reportPendingFailure)
                    return Fail(
                        "POLICE_LOADOUT_ASSETS_PENDING",
                        "The selected Police weapon is still loading or unavailable. Retry after GTA finishes streaming it.");
                return selectedApplied;
            }

            bool allApplied = true;
            bool activeWeaponFound = false;
            foreach (LSPDPoliceFavoriteWeapon weapon in personal)
            {
                bool equip = string.Equals(
                    weapon.Id,
                    _profile.Selection.WeaponId,
                    StringComparison.OrdinalIgnoreCase);
                if (equip)
                    activeWeaponFound = true;
                if (!GiveWeapon(weapon.WeaponName, weapon.Ammo, equip, weapon, false))
                    allApplied = false;
            }

            // A player may select a weapon from the main library without adding
            // it to the personal collection. Keep that valid profile choice as
            // the active weapon while still restoring every personal entry.
            if (!activeWeaponFound)
            {
                string selected = _profile.SelectedWeaponName;
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    activeWeaponFound = true;
                    if (!GiveWeapon(selected, _profile.SelectedWeaponAmmo, true, null, false))
                        allApplied = false;
                }
            }

            if (!activeWeaponFound && personal.Count > 0)
            {
                _profile.SelectPersonalWeapon(personal[0].Id);
                if (!GiveWeapon(personal[0].WeaponName, personal[0].Ammo, true, personal[0], false))
                    allApplied = false;
            }
            if (!allApplied && reportPendingFailure)
                return Fail(
                    "POLICE_LOADOUT_ASSETS_PENDING",
                    "One or more saved Police weapons are still loading or unavailable. The collection remains saved and may be retried.");
            return allApplied;
        }

        internal bool SavePoliceProfile()
        {
            if (!RequireActive())
                return false;
            if (!_profile.Save())
                return Fail("POLICE_PROFILE_SAVE_FAILED", "Police profile could not be saved.");
            return Succeed("POLICE_PROFILE_SAVED", "Police profile saved.");
        }

        private bool ApplyPedModel(
            string modelName,
            bool reportFailure,
            bool restoreLoadout = true)
        {
            Model model = new Model(modelName);
            bool releaseModel = false;
            try
            {
                // Personal addon models are valid streamed assets even when
                // they are not part of the vanilla CD image. Model validity,
                // type, request, and load state are the meaningful checks here.
                if (!model.IsValid || !model.IsPed)
                {
                    if (reportFailure)
                        Fail("POLICE_PED_MODEL_INVALID", "Ped model is not available in this game build: " + modelName + ".");
                    else
                        _log.Debug("POLICE_PED_MODEL_INVALID", modelName);
                    return false;
                }
                if (!model.IsLoaded)
                {
                    // This method can be reached from a LemonUI profile action.
                    // Request only; the timeout overload calls Script.Wait and
                    // is not legal outside ScriptHook's normal game tick.
                    model.Request();
                    // Keep the request alive across normal Script ticks. The
                    // former finally block released it immediately, causing the
                    // same addon ped to remain permanently "pending".
                    if (reportFailure)
                        Fail("POLICE_PED_MODEL_LOAD_PENDING", "Ped model is loading. Select it again in a moment: " + modelName + ".");
                    return false;
                }

                releaseModel = true;
                Ped current = Game.Player.Character;
                if (current == null || !current.Exists())
                    return Fail("POLICE_PLAYER_UNAVAILABLE", "The player character is unavailable.");
                if (current.Model.Hash != model.Hash && !Game.Player.ChangeModel(model))
                    return Fail("POLICE_PED_MODEL_APPLY_FAILED", "Ped model could not be applied: " + modelName + ".");

                // Model changes clear weapons in GTA. Restore the selected duty
                // weapon immediately; the complete personal collection remains
                // saved and can still be applied explicitly from Profile. This
                // avoids requesting a large weapon library simply because the
                // officer changed character model.
                if (restoreLoadout
                    && IsPoliceAuthorityActive
                    && !ApplySelectedPoliceWeaponInternal(false))
                    _log.Debug(
                        "POLICE_LOADOUT_RESTORE_PARTIAL",
                        "The Police character changed, but the selected duty weapon could not yet be restored.");
                return true;
            }
            catch (Exception ex)
            {
                return Fail("POLICE_PED_MODEL_ERROR", "Ped model could not be applied: " + modelName + ".", ex);
            }
            finally
            {
                // A pending streamed model must remain requested until a later
                // Process tick observes IsLoaded. Release only after it became
                // usable (or after a successful application).
                if (releaseModel)
                {
                    try { model.MarkAsNoLongerNeeded(); } catch { }
                }
            }
        }

        private bool PreparePatrolVehicle()
        {
            string modelName = _profile.SelectedVehicleModelName;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                _log.Debug("POLICE_PATROL_VEHICLE_UNSELECTED",
                    "Patrol started without a selected Police vehicle.");
                return false;
            }

            // reportFailure=false keeps a valid occupied Police vehicle and
            // refuses to replace it. A new owned vehicle is created only when
            // the player is on foot or in an unrelated vehicle.
            bool ready = SpawnPoliceVehicle(modelName, false);
            if (ready)
                _log.Runtime(
                    "POLICE_PATROL_VEHICLE_READY",
                    "Model=" + modelName + "; ReusedOrPrepared=true");
            return ready;
        }
        private bool SpawnPoliceVehicle(string modelName, bool reportFailure)
        {
            Model model = new Model(modelName);
            bool releaseModel = false;
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return Fail("POLICE_PLAYER_UNAVAILABLE", "The player character is unavailable.");

                // Check the occupied vehicle before requesting a model. An addon
                // model may be valid only because it is already streamed in; a
                // failed request must never force a second patrol vehicle.
                Vehicle currentVehicle = player.CurrentVehicle;
                bool retainCurrentPatrolVehicle = !reportFailure
                    && (_config.Police.Patrol.ReuseCurrentPoliceVehicle
                        || _config.Police.Patrol.PreventDuplicatePersonalVehicle);
                if (retainCurrentPatrolVehicle
                    && currentVehicle != null && currentVehicle.Exists()
                    && IsKnownPoliceVehicle(currentVehicle))
                {
                    Vehicle previousManaged = _managedPoliceVehicle;
                    if (previousManaged != null && previousManaged.Exists()
                        && previousManaged.Handle != currentVehicle.Handle)
                        RetireOwnedVehicle(previousManaged, currentVehicle);
                    _managedPoliceVehicle = currentVehicle;
                    _log.Runtime(
                        "POLICE_PATROL_VEHICLE_REUSED",
                        "Existing Police vehicle retained; Handle=" + currentVehicle.Handle);
                    return true;
                }

                if (!model.IsValid || !model.IsVehicle)
                {
                    if (reportFailure)
                        Fail("POLICE_VEHICLE_MODEL_INVALID", "Vehicle model is not available in this game build: " + modelName + ".");
                    else
                        _log.Debug("POLICE_VEHICLE_MODEL_INVALID", modelName);
                    return false;
                }

                if (currentVehicle != null && currentVehicle.Exists()
                    && currentVehicle.Model.Hash == model.Hash
                    && (reportFailure
                        || _config.Police.Patrol.ReuseCurrentPoliceVehicle
                        || _config.Police.Patrol.PreventDuplicatePersonalVehicle))
                    return true;

                if (_managedPoliceVehicle != null && _managedPoliceVehicle.Exists()
                    && _managedPoliceVehicle.Model.Hash == model.Hash
                    && (reportFailure
                        || _config.Police.Patrol.PreventDuplicatePersonalVehicle))
                {
                    try { player.SetIntoVehicle(_managedPoliceVehicle, VehicleSeat.Driver); }
                    catch (Exception ex) { _log.Exception("POLICE_MANAGED_VEHICLE_REUSE_FAILED", ex); }
                    return true;
                }

                if (!model.IsLoaded)
                {
                    // Patrol/menu actions must never block the ScriptHook UI
                    // thread while an addon model streams.
                    model.Request();
                    // Keep the streamed request alive for the pending-profile
                    // retry. Releasing it here made addon vehicles repeatedly
                    // fail to become ready for Patrol.
                    if (reportFailure)
                        Fail("POLICE_VEHICLE_MODEL_LOAD_PENDING", "Police vehicle is loading. Select it again in a moment: " + modelName + ".");
                    return false;
                }

                releaseModel = true;
                Vector3 spawn = player.GetOffsetPosition(new Vector3(0f, 5f, 0f));
                Vehicle created = World.CreateVehicle(model, spawn, player.Heading);
                if (created == null || !created.Exists())
                    return Fail("POLICE_VEHICLE_SPAWN_FAILED", "Police vehicle could not be created: " + modelName + ".");

                created.IsPersistent = true;
                Vehicle previous = _managedPoliceVehicle;
                _managedPoliceVehicle = created;
                player.SetIntoVehicle(created, VehicleSeat.Driver);

                if (previous != null && previous.Exists() && previous.Handle != created.Handle)
                    RetireOwnedVehicle(previous, player.CurrentVehicle);
                return true;
            }
            catch (Exception ex)
            {
                return Fail("POLICE_VEHICLE_ERROR", "Police vehicle could not be created: " + modelName + ".", ex);
            }
            finally
            {
                try
                {
                    if (releaseModel && model.IsValid)
                        model.MarkAsNoLongerNeeded();
                }
                catch { }
            }
        }

        private bool IsKnownPoliceVehicle(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
                return false;
            int hash = vehicle.Model.Hash;
            foreach (LSPDPoliceVehicleDefinition definition in _profile.Vehicles)
                if (ResolveModelHash(definition.ModelName) == hash)
                    return true;
            foreach (LSPDPoliceFavoriteModel favorite in _profile.FavoriteVehicles)
                if (ResolveModelHash(favorite.ModelName) == hash)
                    return true;
            return false;
        }

        private static int ResolveModelHash(string modelName)
        {
            return unchecked((int)StringHash.AtStringHash(modelName, 0));
        }

        private static int ResolveWeaponHash(string name)
        {
            uint numeric;
            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(name.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out numeric))
                return unchecked((int)numeric);
            if (uint.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                return unchecked((int)numeric);
            return unchecked((int)StringHash.AtStringHash(name, 0));
        }

        private bool GiveWeapon(
            string weaponName,
            int ammo,
            bool equip,
            LSPDPoliceFavoriteWeapon saved = null,
            bool reportFailure = true)
        {
            int hash = ResolveWeaponHash(weaponName);
            var asset = new WeaponAsset(hash);
            bool releaseAsset = false;
            try
            {
                if (!asset.IsValid || !asset.IsValidAsWeaponHash)
                {
                    if (reportFailure)
                        return Fail("POLICE_WEAPON_ASSET_INVALID", "Weapon is not available in this game build: " + weaponName + ".");
                    return false;
                }
                if (!asset.IsLoaded)
                {
                    // Weapon selection may be called by the UI. Queue the
                    // asset without Script.Wait; a deliberate retry after GTA
                    // streams it is safer than freezing or aborting the script.
                    asset.Request();
                    // Background profile restoration is expected to encounter
                    // pending assets. Do not record hundreds of identical
                    // DEBUG failures while GTA is simply streaming them.
                    if (reportFailure)
                        return Fail("POLICE_WEAPON_ASSET_LOAD_PENDING", "Weapon is loading. Select it again in a moment: " + weaponName + ".");
                    return false;
                }

                releaseAsset = true;
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return Fail("POLICE_PLAYER_UNAVAILABLE", "The player character is unavailable.");
                Weapon given = player.Weapons.Give((WeaponHash)hash, Math.Max(0, ammo), equip, true);
                if (given == null || !given.IsPresent)
                    return Fail("POLICE_WEAPON_GIVE_FAILED", "The weapon could not be equipped.");
                if (saved != null)
                {
                    if (saved.Tint >= 0 && saved.Tint < given.TintCount)
                        given.Tint = (WeaponTint)saved.Tint;
                    var components = new HashSet<int>(saved.Components.Select(ResolveWeaponHash));
                    foreach (WeaponComponent component in given.Components)
                        component.Active = components.Contains(unchecked((int)component.ComponentHash));
                }
                return true;
            }
            catch (Exception ex)
            {
                return Fail("POLICE_WEAPON_ERROR", "Weapon could not be applied: " + weaponName + ".", ex);
            }
            finally
            {
                // Do not cancel a request that is still pending. A later
                // normal Script tick will see the loaded asset, apply it, and
                // then release the streaming reference here.
                if (releaseAsset)
                {
                    try { asset.MarkAsNoLongerNeeded(); } catch { }
                }
            }
        }

        private void CaptureNormalPlayerState()
        {
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return;

                var snapshot = new PlayerStateSnapshot { ModelHash = player.Model.Hash };
                Weapon current = player.Weapons.Current;
                snapshot.CurrentWeapon = current == null ? (WeaponHash?)null : current.Hash;
                foreach (WeaponHash hash in player.Weapons.GetAllWeaponHashes())
                {
                    Weapon weapon = player.Weapons[hash];
                    if (weapon != null && weapon.IsPresent)
                    {
                        var savedWeapon = new PlayerWeaponSnapshot(
                            hash,
                            weapon.Ammo,
                            weapon.Tint);
                        foreach (WeaponComponent component in weapon.Components)
                            if (component.Active)
                                savedWeapon.Components.Add(component.ComponentHash);
                        snapshot.Weapons.Add(savedWeapon);
                    }
                }
                _normalPlayerState = snapshot;
            }
            catch (Exception ex)
            {
                _normalPlayerState = null;
                _log.Exception("NORMAL_PLAYER_STATE_CAPTURE_FAILED", ex);
            }
        }

        private void QueueNormalPlayerRestore()
        {
            if (_normalPlayerState == null)
                return;
            _pendingNormalPlayerRestore = _normalPlayerState;
            _normalPlayerState = null;
            _pendingRestoreModel = new Model(_pendingNormalPlayerRestore.ModelHash);
            _pendingRestoreDeadline = DateTime.UtcNow.AddSeconds(30);
            _pendingWeaponsRestored = false;
            _log.Runtime(
                "NORMAL_PLAYER_STATE_RESTORE_QUEUED",
                "Player model and weapon collection will be restored from the next Script tick.");
        }

        private void ProcessPendingNormalPlayerRestore()
        {
            PlayerStateSnapshot snapshot = _pendingNormalPlayerRestore;
            if (snapshot == null)
                return;
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return;

                bool modelReady = player.Model.Hash == snapshot.ModelHash;
                if (!modelReady)
                {
                    Model model = _pendingRestoreModel;
                    if (model == null || !model.IsValid || !model.IsPed)
                    {
                        LogNormalRestoreFailure("The saved player model is unavailable.");
                        return;
                    }
                    if (!model.IsLoaded)
                    {
                        // The no-argument request never calls Script.Wait. The
                        // next game ticks will continue loading this model.
                        model.Request();
                        if (DateTime.UtcNow < _pendingRestoreDeadline)
                            return;
                    }
                    if (!model.IsLoaded)
                    {
                        LogNormalRestoreFailure("The saved player model did not load before timeout.");
                        return;
                    }
                    if (!Game.Player.ChangeModel(model))
                        return;
                    player = Game.Player.Character;
                    if (player == null || !player.Exists())
                        return;
                    modelReady = player.Model.Hash == snapshot.ModelHash;
                }

                if (!modelReady || _pendingWeaponsRestored)
                    return;
                player.Weapons.RemoveAll();
                foreach (PlayerWeaponSnapshot weapon in snapshot.Weapons)
                {
                    try
                    {
                        Weapon restored = player.Weapons.Give(
                            weapon.Hash, weapon.Ammo, false, true);
                        if (restored != null)
                        {
                            restored.Tint = weapon.Tint;
                            foreach (WeaponComponentHash component in weapon.Components)
                                restored.Components[component].Active = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Exception("NORMAL_PLAYER_WEAPON_RESTORE_FAILED", ex);
                    }
                }
                if (snapshot.CurrentWeapon.HasValue)
                    player.Weapons.Select(snapshot.CurrentWeapon.Value, true);
                _pendingWeaponsRestored = true;
                _log.Runtime("NORMAL_PLAYER_STATE_RESTORED", "Normal player model and weapon collection restored.");
                _pendingNormalPlayerRestore = null;
            }
            catch (Exception ex)
            {
                _log.Exception("NORMAL_PLAYER_STATE_RESTORE_FAILED", ex);
                if (DateTime.UtcNow >= _pendingRestoreDeadline)
                    _pendingNormalPlayerRestore = null;
            }
            finally
            {
                if (_pendingNormalPlayerRestore == null && _pendingRestoreModel != null)
                {
                    try { _pendingRestoreModel.MarkAsNoLongerNeeded(); } catch { }
                    _pendingRestoreModel = null;
                    _pendingRestoreDeadline = DateTime.MinValue;
                    _pendingWeaponsRestored = false;
                }
            }
        }

        private void LogNormalRestoreFailure(string message)
        {
            _log.Debug("NORMAL_PLAYER_STATE_RESTORE_SKIPPED", message);
            _pendingNormalPlayerRestore = null;
        }
        private void ReleaseManagedVehicle()
        {
            try
            {
                if (_managedPoliceVehicle == null || !_managedPoliceVehicle.Exists())
                    return;
                Ped player = Game.Player.Character;
                Vehicle current = player == null ? null : player.CurrentVehicle;
                RetireOwnedVehicle(_managedPoliceVehicle, current);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_VEHICLE_RELEASE_FAILED", ex);
            }
            finally
            {
                _managedPoliceVehicle = null;
            }
        }

        private static void RetireOwnedVehicle(Vehicle owned, Vehicle playerVehicle)
        {
            bool playerInside = playerVehicle != null && playerVehicle.Exists()
                && playerVehicle.Handle == owned.Handle;
            Ped[] occupants = owned.Occupants;
            bool occupied = occupants != null
                && occupants.Any(value => value != null && value.Exists());
            if (!playerInside && !occupied)
            {
                owned.Delete();
                return;
            }

            // The vehicle is ours, but its current occupants may not be. Let GTA
            // manage it after the Police session instead of deleting those peds.
            owned.IsPersistent = false;
            owned.MarkAsNoLongerNeeded();
        }

        private bool RequireActive()
        {
            if (IsPoliceAuthorityActive)
                return true;
            LastOperationMessage = "Open Police Authority before changing the Police profile.";
            return false;
        }

        private bool Succeed(string category, string message)
        {
            LastOperationMessage = message;
            _log.Runtime(category, message);
            return true;
        }

        private bool Fail(string category, string message, Exception ex = null)
        {
            LastOperationMessage = message;
            if (ex == null)
                _log.Debug(category, message);
            else
                _log.Exception(category, ex);
            return false;
        }

        private static string NormalizeModelName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value.Trim().ToLowerInvariant();
        }

        internal LSPDAudioResult TestRadio()
        {
            return Audio.Report(
                "lsimmersivelife.police.radio.check",
                "radio-test",
                (++_radioTest).ToString(CultureInfo.InvariantCulture));
        }

        private void ActivateStationMarkers()
        {
            ResetStationMarkers();
            foreach (LSPDPoliceStationDefinition station in _profile.Stations)
            {
                try
                {
                    Blip marker = World.CreateBlip(new Vector3(
                        station.ExteriorX,
                        station.ExteriorY,
                        station.ExteriorZ));
                    if (marker == null || !marker.Exists())
                        continue;
                    marker.Name = "Police - " + station.DisplayName;
                    marker.Sprite = BlipSprite.PoliceStation;
                    marker.Color = BlipColor.Blue;
                    marker.IsShortRange = false;
                    _stationMarkers.Add(marker);
                }
                catch (Exception ex)
                {
                    _log.Exception("POLICE_STATION_MARKER_FAILED", ex);
                }
            }
            _log.Runtime("POLICE_STATION_MARKERS_READY", "Markers=" + _stationMarkers.Count);
        }

        private void ResetStationMarkers()
        {
            foreach (Blip marker in _stationMarkers.ToArray())
            {
                try { if (marker != null && marker.Exists()) marker.Delete(); } catch { }
            }
            _stationMarkers.Clear();
        }

        private bool TrySetStationWaypoint(LSPDPoliceStationDefinition station)
        {
            if (station == null)
                return false;
            try
            {
                World.WaypointPosition = new Vector3(
                    station.ExteriorX,
                    station.ExteriorY,
                    station.ExteriorZ);
                _log.Runtime("POLICE_STATION_GPS_SET", "Station=" + station.DisplayName);
                return true;
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_STATION_GPS_FAILED", ex);
                return false;
            }
        }

        internal void PrepareAuthorityUi()
        {
            _log.Runtime("POLICE_AUTHORITY_UI_OPENED", "Police Authority menu opened.");
        }

        public void Dispose()
        {
            _config.SettingsChanged -= QueueConfigurationRefresh;
            ExitPoliceAuthority();
            ResetStationMarkers();
            _gangData.Reset();
            Audio.Dispose();
        }

        private sealed class PlayerStateSnapshot
        {
            internal int ModelHash { get; set; }
            internal WeaponHash? CurrentWeapon { get; set; }
            internal List<PlayerWeaponSnapshot> Weapons { get; private set; }

            internal PlayerStateSnapshot()
            {
                Weapons = new List<PlayerWeaponSnapshot>();
            }
        }

        private sealed class PlayerWeaponSnapshot
        {
            internal PlayerWeaponSnapshot(WeaponHash hash, int ammo, WeaponTint tint)
            {
                Hash = hash;
                Ammo = ammo;
                Tint = tint;
                Components = new List<WeaponComponentHash>();
            }

            internal WeaponHash Hash { get; private set; }
            internal int Ammo { get; private set; }
            internal WeaponTint Tint { get; private set; }
            internal List<WeaponComponentHash> Components { get; private set; }
        }
    }
}
