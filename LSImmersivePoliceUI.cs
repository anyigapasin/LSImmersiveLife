using GTA;
using GTA.UI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// User-facing Police Authority configuration surface. It presents profile
    /// data and delegates every state or GTA operation to PoliceCore.
    /// </summary>
    internal sealed class LSImmersivePoliceUI
    {
        private readonly LSImmersivePoliceCore _core;
        private readonly List<NativeMenu> _menus = new List<NativeMenu>();

        private NativeMenu _profileMenu;
        private NativeMenu _agencyMenu;
        private NativeMenu _stationMenu;
        private NativeMenu _stationLocationsMenu;
        private NativeMenu _coordinationMenu;
        private NativeMenu _pedMenu;
        private NativeMenu _vehicleMenu;
        private NativeMenu _weaponMenu;
        private NativeMenu _favouriteMenu;
        private NativeMenu _personalPedMenu;
        private NativeMenu _backupPedMenu;
        private NativeMenu _personalVehicleMenu;
        private NativeMenu _personalWeaponsMenu;
        private NativeMenu _addPersonalWeaponMenu;
        private NativeMenu _savedPersonalWeaponsMenu;
        private NativeMenu _removePersonalWeaponMenu;
        private NativeMenu _savedFavouritesMenu;
        private NativeMenu _authorityStatusMenu;
        private NativeMenu _audioMenu;
        private Action _refreshOffer;
        private NativeItem _dispatchStatus;
        private NativeItem _requestDispatch;
        private NativeItem _acknowledgeDispatch;
        private NativeItem _acceptDispatch;
        private NativeItem _declineDispatch;
        private NativeItem _investigateDispatch;
        private NativeItem _secureDispatch;
        private NativeItem _approveTransport;
        private NativeItem _declineTransport;
        private NativeItem _callBackup;
        private NativeItem _callDefaultBackup;
        private NativeItem _convoyStatus;
        private NativeItem _requestConvoyActivity;
        private NativeItem _requestConvoyShortcut;
        private NativeItem _continueTransport;
        private NativeItem _finishCustody;
        private NativeItem _cancelActiveDispatch;
        private NativeItem _resetPoliceRuntime;
        private NativeMenu _crimeActivitiesMenu;
        private NativeMenu _crimeLocationsMenu;
        private NativeMenu _crimeGroupsMenu;
        private NativeMenu _crimeResourcesMenu;
        private NativeMenu _crimeDetailsMenu;
        private NativeItem _nearbyCrimeStatus;
        private NativeItem _crimeActivityStatus;
        private NativeItem _requestCrimeActivity;
        private NativeItem _beginCrimeActivity;
        private NativeItem _confrontCrimeActivity;
        private NativeItem _ignoreCrimeActivity;
        private NativeMenu _gangIntelligenceMenu;
        private NativeMenu _gangTurfMenu;

        private NativeItem _profileAgencySummary;
        private NativeItem _profileStationSummary;
        private NativeItem _profileOfficerSummary;
        private NativeItem _profileVehicleSummary;
        private NativeItem _profileWeaponSummary;

        internal LSImmersivePoliceUI(LSImmersivePoliceCore core)
        {
            _core = core ?? throw new ArgumentNullException("core");
            Menu = Register(LSImmersiveMenuFactory.Create("Police Authority", string.Empty));
            Menu.NoItemsText = string.Empty;
            BuildMenus();
            RefreshAllMenus();
        }

        internal NativeMenu Menu { get; private set; }

        internal IEnumerable<NativeMenu> Menus
        {
            get { return _menus; }
        }

        private void BuildMenus()
        {
            _profileMenu = Register(LSImmersiveMenuFactory.Create("Police Profile", string.Empty));
            _agencyMenu = Register(LSImmersiveMenuFactory.Create("Police Department", string.Empty));
            _stationMenu = Register(LSImmersiveMenuFactory.Create("Police Stations", string.Empty));
            _stationLocationsMenu = Register(LSImmersiveMenuFactory.Create("Station Locations", string.Empty));
            _coordinationMenu = Register(LSImmersiveMenuFactory.Create("Coordination Routes", string.Empty));
            _pedMenu = Register(LSImmersiveMenuFactory.Create("Police Character Model", string.Empty));
            _vehicleMenu = Register(LSImmersiveMenuFactory.Create("Police Vehicle", string.Empty));
            _weaponMenu = Register(LSImmersiveMenuFactory.Create("Police Weapon", string.Empty));
            _favouriteMenu = Register(LSImmersiveMenuFactory.Create("Favourite Set", string.Empty));
            _personalPedMenu = Register(LSImmersiveMenuFactory.Create("Personal Police Character", string.Empty));
            _backupPedMenu = Register(LSImmersiveMenuFactory.Create("Favourite Backup Officers", string.Empty));
            _personalVehicleMenu = Register(LSImmersiveMenuFactory.Create("Personal Police Vehicle", string.Empty));
            _personalWeaponsMenu = Register(LSImmersiveMenuFactory.Create("Personal Police Weapons", string.Empty));
            _addPersonalWeaponMenu = Register(LSImmersiveMenuFactory.Create("Add From Weapon Library", string.Empty));
            _savedPersonalWeaponsMenu = Register(LSImmersiveMenuFactory.Create("Saved Personal Weapons", string.Empty));
            _removePersonalWeaponMenu = Register(LSImmersiveMenuFactory.Create("Remove Personal Weapon", string.Empty));
            _savedFavouritesMenu = Register(LSImmersiveMenuFactory.Create("Saved Favourites", string.Empty));
            _authorityStatusMenu = Register(LSImmersiveMenuFactory.Create("Authority Status", string.Empty));
            _audioMenu = Register(LSImmersiveMenuFactory.Create("Dispatch Audio", string.Empty));

            Menu.AddSubMenu(_profileMenu);
            BuildGameplayMenus();
            Menu.AddSubMenu(_authorityStatusMenu);
            Menu.AddSubMenu(_audioMenu);
            LSImmersiveMenuFactory.AddAction(Menu, "Deactivate Police Authority", DeactivateAuthority);

            _profileAgencySummary = AddSummaryItem(_profileMenu, "Department");
            _profileStationSummary = AddSummaryItem(_profileMenu, "Station");
            _profileOfficerSummary = AddSummaryItem(_profileMenu, "Officer");
            _profileVehicleSummary = AddSummaryItem(_profileMenu, "Vehicle");
            _profileWeaponSummary = AddSummaryItem(_profileMenu, "Active Weapon");
            _profileMenu.AddSubMenu(_agencyMenu);
            _profileMenu.AddSubMenu(_stationMenu);
            _profileMenu.AddSubMenu(_pedMenu);
            _profileMenu.AddSubMenu(_vehicleMenu);
            _profileMenu.AddSubMenu(_weaponMenu);
            _profileMenu.AddSubMenu(_favouriteMenu);

            _stationMenu.AddSubMenu(_stationLocationsMenu);
            _stationMenu.AddSubMenu(_coordinationMenu);

            _favouriteMenu.AddSubMenu(_personalPedMenu);
            _favouriteMenu.AddSubMenu(_backupPedMenu);
            _favouriteMenu.AddSubMenu(_personalVehicleMenu);
            _favouriteMenu.AddSubMenu(_personalWeaponsMenu);
            _favouriteMenu.AddSubMenu(_savedFavouritesMenu);
            LSImmersiveMenuFactory.AddAction(
                _favouriteMenu,
                "Save Police Profile",
                delegate { RunCoreAction(_core.SavePoliceProfile); });

            _personalWeaponsMenu.AddSubMenu(_addPersonalWeaponMenu);
            _personalWeaponsMenu.AddSubMenu(_savedPersonalWeaponsMenu);
            _personalWeaponsMenu.AddSubMenu(_removePersonalWeaponMenu);

            BuildAudioMenu();

            _profileMenu.Opening += delegate { RefreshProfileSummary(); };
            _agencyMenu.Opening += delegate { RefreshAgencyMenu(); };
            _stationMenu.Opening += delegate { RefreshStationMenu(); };
            _pedMenu.Opening += delegate { RefreshPedMenu(); };
            _vehicleMenu.Opening += delegate { RefreshVehicleMenu(); };
            _weaponMenu.Opening += delegate { RefreshWeaponMenu(); };
            _favouriteMenu.Opening += delegate { RefreshFavouriteMenus(); };
            _personalPedMenu.Opening += delegate { RefreshPersonalPedMenu(); };
            _backupPedMenu.Opening += delegate { RefreshBackupPedMenu(); };
            _personalVehicleMenu.Opening += delegate { RefreshPersonalVehicleMenu(); };
            _personalWeaponsMenu.Opening += delegate { RefreshPersonalWeaponsMenu(); };
            _addPersonalWeaponMenu.Opening += delegate { RefreshAddPersonalWeaponMenu(); };
            _savedPersonalWeaponsMenu.Opening += delegate { RefreshSavedPersonalWeaponsMenu(); };
            _removePersonalWeaponMenu.Opening += delegate { RefreshRemovePersonalWeaponMenu(); };
            _savedFavouritesMenu.Opening += delegate { RefreshSavedFavouritesMenu(); };
            _authorityStatusMenu.Opening += delegate { RefreshAuthorityStatusMenu(); };
        }

        private void RefreshAllMenus()
        {
            RefreshProfileSummary();
            RefreshAgencyMenu();
            RefreshStationMenu();
            RefreshLocationMenus();
            RefreshPedMenu();
            RefreshVehicleMenu();
            RefreshWeaponMenu();
            RefreshFavouriteMenus();
            RefreshAuthorityStatusMenu();
            RefreshConvoyMenu();
            RefreshGangIntelligence();
            RefreshCrimeIntelligence();
            if (_refreshOffer != null)
                _refreshOffer();
        }

        private void RefreshConvoyMenu()
        {
            if (_continueTransport == null)
                return;
            bool quietPatrol = _core.IsPoliceAuthorityActive
                && _core.Patrol.IsPatrolling
                && !_core.Dispatch.HasIncident
                && !_core.Convoy.Active
                && !_core.CrimeActivity.BlocksOtherPoliceActivities
                && (!_core.Backup.BlocksPlayerActivities
                    || _core.Backup.State == LSPDBackupAssignmentState.StandingDown)
                && !_core.GangResponse.HasActiveIncident
                && !_core.NpcResponse.HasActiveInteraction;
            _convoyStatus.Title = "Convoy Status: " + _core.Convoy.StatusText;
            _convoyStatus.Enabled = false;
            _requestConvoyActivity.Enabled = quietPatrol;
            _continueTransport.Enabled = _core.Convoy.HoldingAtStation;
            _finishCustody.Enabled = _core.Convoy.CanUseTerminalCompletion;
            _cancelActiveDispatch.Enabled = _core.Dispatch.HasIncident || _core.Convoy.Active
                || _core.CrimeActivity.BlocksOtherPoliceActivities;
            _cancelActiveDispatch.Title = _core.Convoy.IsRequestedConvoyActivity
                ? "Cancel Convoy Request"
                : _core.CrimeActivity.BlocksOtherPoliceActivities
                    ? "Cancel Crime Activity"
                    : "Cancel Active Dispatch";
            _resetPoliceRuntime.Enabled = _core.IsPoliceAuthorityActive;
        }

        private void BuildGameplayMenus()
        {
            // Profile owns Police identity/data selection, Response owns the
            // command surface, NPC interaction owns local contacts, and GPS &
            // Controls exposes the configured Police shortcuts and navigation.
            NativeMenu gameplay = Register(LSImmersiveMenuFactory.Create("Gameplay Logic", string.Empty));
            Menu.AddSubMenu(gameplay);
            NativeMenu response = Register(LSImmersiveMenuFactory.Create("Response", string.Empty));
            NativeMenu npc = Register(LSImmersiveMenuFactory.Create("NPC Ambient Interaction", string.Empty));
            NativeMenu controls = Register(LSImmersiveMenuFactory.Create("GPS & Controls", string.Empty));
            NativeMenu intelligence = Register(LSImmersiveMenuFactory.Create("Crime Activity Intelligence", string.Empty));
            NativeMenu convoy = Register(LSImmersiveMenuFactory.Create("Convoy", string.Empty));
            _gangIntelligenceMenu = Register(LSImmersiveMenuFactory.Create("Gang & Turf Intelligence", string.Empty));
            _gangTurfMenu = Register(LSImmersiveMenuFactory.Create("Gang Turf Zones", string.Empty));
            gameplay.AddSubMenu(response);
            gameplay.AddSubMenu(npc);
            gameplay.AddSubMenu(controls);
            gameplay.AddSubMenu(intelligence);
            gameplay.AddSubMenu(convoy);
            gameplay.AddSubMenu(_gangIntelligenceMenu);
            BuildResponseMenu(response);
            BuildNpcInteractionMenu(npc);
            BuildGpsAndControlsMenu(controls);
            BuildCrimeActivityMenu(intelligence);
            BuildGangIntelligenceMenu();
            _convoyStatus = new NativeItem("Convoy Status: No active Convoy operation.");
            _requestConvoyActivity = new NativeItem("Request Prisoner Convoy Activity");
            _continueTransport = new NativeItem("Continue Prisoner Transport");
            _finishCustody = new NativeItem("Finish Prisoner Custody");
            _cancelActiveDispatch = new NativeItem("Cancel Active Dispatch");
            _resetPoliceRuntime = new NativeItem("Reset Police Runtime");
            _requestConvoyActivity.Activated += delegate { RunCoreCommand(_core.RequestConvoyActivity); };
            _continueTransport.Activated += delegate { RunCoreCommand(_core.RequestPrisonerTransport); };
            _finishCustody.Activated += delegate { RunCoreCommand(_core.CompletePrisonerTransport); };
            _cancelActiveDispatch.Activated += delegate { RunCoreCommand(_core.CancelActiveDispatch); };
            _resetPoliceRuntime.Activated += delegate { RunCoreCommand(_core.ResetPoliceRuntime); };
            convoy.Add(_convoyStatus);
            convoy.Add(_requestConvoyActivity);
            convoy.Add(_continueTransport);
            convoy.Add(_finishCustody);
            convoy.Add(_cancelActiveDispatch);
            convoy.Add(_resetPoliceRuntime);
            convoy.Opening += delegate { RefreshConvoyMenu(); };
        }

        private void BuildResponseMenu(NativeMenu response)
        {
            // Patrol changes player availability, not NPC AI. Offer decisions
            // operate only on an incident actually delivered to PoliceCore; no
            // menu action fabricates a dispatch, arrival, arrest, or transport.
            NativeItem patrol = new NativeItem("Start Patrol");
            Action refreshPatrol = delegate
            {
                patrol.Title = _core.Patrol.IsPatrolling ? "End Patrol" : "Start Patrol";
                patrol.Enabled = _core.IsPoliceAuthorityActive;
            };
            patrol.Activated += delegate { RunPatrolToggle(); refreshPatrol(); };
            response.Add(patrol);
            LSImmersiveMenuFactory.AddAction(
                response,
                "Start Patrol: Selected Station Route",
                delegate { RunCoreAction(_core.StartPatrolTowardSelectedStation); });
            response.Opening += delegate { refreshPatrol(); };
            NativeMenu dispatch = Register(LSImmersiveMenuFactory.Create("Dispatch", string.Empty));
            response.AddSubMenu(dispatch);
            _dispatchStatus = new NativeItem("Dispatch Status: No active dispatch.");
            _dispatchStatus.Enabled = false;
            _requestDispatch = new NativeItem("Request a Dispatch");
            _acknowledgeDispatch = new NativeItem("Acknowledge");
            _acceptDispatch = new NativeItem("Accept Dispatch");
            _declineDispatch = new NativeItem("Decline Dispatch");
            _refreshOffer = delegate
            {
                bool available = _core.IsPoliceAuthorityActive && _core.Patrol.IsPatrolling && _core.Dispatch.HasOffer;
                bool sceneAvailable = _core.IsPoliceAuthorityActive
                    && _core.Patrol.IsPatrolling
                    && _core.Dispatch.HasIncident;
                bool quietPatrol = _core.IsPoliceAuthorityActive
                    && _core.Patrol.IsPatrolling
                    && !_core.Dispatch.HasIncident
                    && !_core.Convoy.Active
                    && !_core.CrimeActivity.BlocksOtherPoliceActivities
                    && (!_core.Backup.BlocksPlayerActivities
                        || _core.Backup.State == LSPDBackupAssignmentState.StandingDown)
                    && !_core.GangResponse.HasActiveIncident
                    && !_core.NpcResponse.HasActiveInteraction;
                LSPDDispatchState state = _core.Dispatch.State;
                _dispatchStatus.Title = sceneAvailable
                    ? "Dispatch: " + _core.Dispatch.CurrentTitle + " | " + state
                    : "Dispatch: " + _core.Dispatch.AvailabilityStatus
                        + " | Events=" + _core.Dispatch.EventDefinitionCount
                        + ", Profiles=" + _core.Dispatch.CriminalProfileCount;
                _requestDispatch.Enabled = quietPatrol;
                _acknowledgeDispatch.Enabled = available;
                _acceptDispatch.Enabled = available;
                _declineDispatch.Enabled = available;
                _investigateDispatch.Enabled = sceneAvailable
                    && state != LSPDDispatchState.Offered
                    && state != LSPDDispatchState.Completed
                    && state != LSPDDispatchState.Cancelled;
                _secureDispatch.Enabled = sceneAvailable
                    && state != LSPDDispatchState.Offered
                    && state != LSPDDispatchState.EnRoute
                    && state != LSPDDispatchState.Arrested
                    && state != LSPDDispatchState.AwaitingTransport
                    && state != LSPDDispatchState.HoldingAtStation
                    && state != LSPDDispatchState.PrisonTransfer;
                _approveTransport.Enabled = _core.Convoy.HoldingAtStation
                    || state == LSPDDispatchState.Arrested;
                _declineTransport.Enabled = _core.Convoy.HoldingAtStation;
                _callBackup.Enabled = _core.IsPoliceAuthorityActive
                    && _core.Patrol.IsPatrolling
                    && (_core.Dispatch.HasIncident || _core.Convoy.Active
                        || _core.CrimeActivity.BlocksOtherPoliceActivities
                        || _core.GangResponse.HasActiveIncident
                        || _core.NpcResponse.CanRequestBackup
                        || _core.NpcResponse.HasBackupAssignment);
                _callDefaultBackup.Enabled = _callBackup.Enabled;
                if (_requestConvoyShortcut != null)
                    _requestConvoyShortcut.Enabled = quietPatrol;
                if (_requestCrimeActivity != null)
                    _requestCrimeActivity.Enabled = quietPatrol;
            };
            _requestDispatch.Activated += delegate { RunDispatchRequest(); };
            _acknowledgeDispatch.Activated += delegate { RunDispatchDecision(null); };
            _acceptDispatch.Activated += delegate { RunDispatchDecision(true); };
            _declineDispatch.Activated += delegate { RunDispatchDecision(false); };
            dispatch.Add(_dispatchStatus);
            dispatch.Add(_requestDispatch);
            dispatch.Add(_acknowledgeDispatch);
            dispatch.Add(_acceptDispatch);
            dispatch.Add(_declineDispatch);
            dispatch.Opening += delegate { _refreshOffer(); };

            NativeMenu actions = Register(LSImmersiveMenuFactory.Create("Action Dispatch Response", string.Empty));
            response.AddSubMenu(actions);
            // These controls now delegate to their specialized owners. They
            // remain unavailable until the corresponding state exists, so a
            // menu press cannot manufacture an investigation or arrest.
            _investigateDispatch = new NativeItem("Investigate");
            _secureDispatch = new NativeItem("Secure & Comply");
            _approveTransport = new NativeItem("Approve Prisoner Transport");
            _declineTransport = new NativeItem("Disapprove Prisoner Transport");
            _callBackup = new NativeItem("Call Backup: Saved Favorite");
            _callDefaultBackup = new NativeItem("Call Backup: Default Police Model");
            _investigateDispatch.Activated += delegate { RunCoreCommand(_core.InvestigateDispatch); };
            _secureDispatch.Activated += delegate { RunCoreCommand(_core.SecureDispatchSuspect); };
            _approveTransport.Activated += delegate { RunCoreCommand(_core.RequestPrisonerTransport); };
            _declineTransport.Activated += delegate { RunCoreCommand(_core.DeclinePrisonerTransport); };
            _callBackup.Activated += delegate { RunCoreCommand(_core.CallFavoriteBackup); };
            _callDefaultBackup.Activated += delegate { RunCoreCommand(_core.CallDefaultBackup); };
            actions.Add(_investigateDispatch);
            actions.Add(_secureDispatch);
            actions.Add(_approveTransport);
            actions.Add(_declineTransport);
            actions.Add(_callBackup);
            actions.Add(_callDefaultBackup);
            _requestCrimeActivity = new NativeItem("Request Crime Activity");
            _requestCrimeActivity.Activated += delegate
            {
                RunCoreCommand(_core.RequestCrimeActivity);
                RefreshCrimeIntelligence();
            };
            response.Add(_requestCrimeActivity);
            _requestConvoyShortcut = new NativeItem("Request Convoy Activity");
            _requestConvoyShortcut.Activated += delegate { RunCoreCommand(_core.RequestConvoyActivity); };
            response.Add(_requestConvoyShortcut);
            _refreshOffer();
        }

        private void BuildNpcInteractionMenu(NativeMenu npcInteraction)
        {
            // NPC interaction remains separate from Dispatch and Gang owners,
            // but these actions now reach the local foot/traffic contact owner.
            LSImmersiveMenuFactory.AddAction(
                npcInteraction,
                "Interact NPC on Foot",
                delegate { RunNpcCommand(_core.BeginNpcFootInteraction); });
            LSImmersiveMenuFactory.AddAction(
                npcInteraction,
                "Investigate for Legal Verification",
                delegate { RunNpcCommand(_core.BeginNpcInteraction); });
            LSImmersiveMenuFactory.AddAction(
                npcInteraction,
                "Escalate / Pursue",
                delegate { RunNpcCommand(_core.RejectNpcInteraction); });
            LSImmersiveMenuFactory.AddAction(
                npcInteraction,
                "Release",
                delegate { RunNpcCommand(_core.AcceptNpcInteraction); });
            LSImmersiveMenuFactory.AddAction(
                npcInteraction,
                "Vehicle Interaction",
                delegate { RunNpcCommand(_core.BeginNpcTrafficInteraction); });
            LSImmersiveMenuFactory.AddAction(
                npcInteraction,
                "Vehicle Verification",
                delegate { RunNpcCommand(_core.AcceptNpcInteraction); });
            LSImmersiveMenuFactory.AddAction(
                npcInteraction,
                "Request NPC Backup",
                delegate { RunCoreCommand(_core.CallFavoriteBackup); });
        }

        private void BuildGpsAndControlsMenu(NativeMenu gpsAndControls)
        {
            // Decision semantics remain Y / N and controller X / O. The rows
            // delegate to the same owners as the keyboard edges, so UI use does
            // not create a second interaction implementation.
            LSImmersiveMenuFactory.AddAction(
                gpsAndControls,
                "GPS: Selected Police Station",
                delegate { RunCoreCommand(_core.SetSelectedStationWaypoint); });
            LSImmersiveMenuFactory.AddAction(
                gpsAndControls,
                "Investigate",
                delegate { RunCoreCommand(_core.InvestigateDispatch); });
            LSImmersiveMenuFactory.AddAction(
                gpsAndControls,
                "Agree / Disagree",
                delegate { RunNpcCommand(_core.AcceptNpcInteraction); });
            LSImmersiveMenuFactory.AddAction(
                gpsAndControls,
                "Secure",
                delegate { RunCoreCommand(_core.SecureDispatchSuspect); });
            LSImmersiveMenuFactory.AddAction(
                gpsAndControls,
                "Interact",
                delegate { RunNpcCommand(_core.BeginNpcInteraction); });
            LSImmersiveMenuFactory.AddAction(
                gpsAndControls,
                "Reset Bug",
                delegate { RunCoreCommand(_core.ResetPoliceRuntime); });
        }

        private void BuildGangIntelligenceMenu()
        {
            _gangIntelligenceMenu.AddSubMenu(_gangTurfMenu);
            LSImmersiveMenuFactory.AddAction(
                _gangIntelligenceMenu,
                "Reload Gang & Turf Data",
                delegate
                {
                    RunCoreAction(_core.ReloadGangIntelligence);
                    RefreshGangIntelligence();
                });
            _gangIntelligenceMenu.Opening += delegate { RefreshGangIntelligence(); };
        }

        private void RefreshGangIntelligence()
        {
            if (_gangIntelligenceMenu == null || _gangTurfMenu == null)
                return;

            _gangIntelligenceMenu.Clear();
            _gangTurfMenu.Clear();

            // Refreshing a dynamic menu removes its child rows as well. Re-add
            // the stable turf submenu and reload action every time so the
            // intelligence screen remains usable after the first refresh.
            _gangIntelligenceMenu.AddSubMenu(_gangTurfMenu);
            LSImmersiveMenuFactory.AddAction(
                _gangIntelligenceMenu,
                "Reload Gang & Turf Data",
                delegate
                {
                    RunCoreAction(_core.ReloadGangIntelligence);
                    RefreshGangIntelligence();
                });

            bool loaded = _core.GangData.GangDataLoaded
                || _core.GangData.MemberPoolLoaded
                || _core.GangData.TurfDataLoaded;
            AddReadOnlyRow(
                _gangIntelligenceMenu,
                loaded ? "Gang & Turf Data: Loaded" : "Gang & Turf Data: Optional / Not Loaded");
            AddReadOnlyRow(
                _gangIntelligenceMenu,
                "Known Gang Profiles: " + _core.GangData.Gangs.Count());
            AddReadOnlyRow(
                _gangIntelligenceMenu,
                "Known Gang Member Models: " + _core.GangData.MemberModelCount);
            AddReadOnlyRow(
                _gangIntelligenceMenu,
                "Known Gang Vehicle Models: " + _core.GangData.VehicleModelCount);
            AddReadOnlyRow(
                _gangIntelligenceMenu,
                "Local Gang Response: " + _core.GangResponse.StatusText);

            Ped player = Game.Player == null ? null : Game.Player.Character;
            LSPDTurfZoneDefinition nearby = player == null || !player.Exists()
                ? null
                : _core.GangData.FindNearbyTurf(player.Position);
            AddReadOnlyRow(
                _gangIntelligenceMenu,
                "Current Turf: " + (nearby == null
                    ? "No authored zone"
                    : nearby.Name + " | " + nearby.OwnerGangName));

            foreach (LSPDGangDefinition gang in _core.GangData.Gangs.OrderBy(item => item.Name))
            {
                string ownership = gang.IsPlayerOwned || gang.CreatedByPlayer
                    ? "Player-owned / allied data"
                    : "External gang data";
                AddReadOnlyRow(_gangIntelligenceMenu, gang.Name + " | " + ownership);
            }

            foreach (LSPDTurfZoneDefinition zone in _core.GangData.TurfZones.OrderBy(item => item.Name))
                _gangTurfMenu.Add(new NativeItem(
                    zone.Name,
                    zone.OwnerGangName + " | Radius " + zone.Radius.ToString("0") + " m")
                { Enabled = false });
            AddEmptyRow(_gangTurfMenu, "No authored turf zones loaded");
            AddEmptyRow(_gangIntelligenceMenu, "No read-only Gang & Turf export is available", 4);
        }

        private void BuildCrimeActivityMenu(NativeMenu crimeActivity)
        {
            // Crime Activity remains a quiet, player-led Patrol layer. This
            // UI presents the authored intelligence and delegates scene state
            // to CrimeActivity through PoliceCore; it never owns GTA actors.
            _crimeActivitiesMenu = Register(LSImmersiveMenuFactory.Create("Browse Group Crime Database", string.Empty));
            _crimeLocationsMenu = Register(LSImmersiveMenuFactory.Create("Location Coordination", string.Empty));
            _crimeGroupsMenu = Register(LSImmersiveMenuFactory.Create("Criminal Group Profiles", string.Empty));
            _crimeResourcesMenu = Register(LSImmersiveMenuFactory.Create("Scene Resources", string.Empty));
            _crimeDetailsMenu = Register(LSImmersiveMenuFactory.Create("Activity Details", string.Empty));
            _crimeActivityStatus = new NativeItem("Crime Activity: No active intelligence.");
            _crimeActivityStatus.Enabled = false;
            _beginCrimeActivity = new NativeItem("Begin Current Investigation");
            _confrontCrimeActivity = new NativeItem("Confront Current Crime Activity");
            _ignoreCrimeActivity = new NativeItem("Ignore / Close Current Crime Activity");
            _beginCrimeActivity.Activated += delegate
            {
                LSPDActivityIntel current = _core.CrimeActivity.CurrentIntel;
                RunCoreCommand(delegate
                {
                    return _core.BeginCrimeActivity(current == null ? null : current.Id);
                });
                RefreshCrimeIntelligence();
            };
            _confrontCrimeActivity.Activated += delegate
            {
                RunCoreCommand(_core.ConfrontCrimeActivity);
                RefreshCrimeIntelligence();
            };
            _ignoreCrimeActivity.Activated += delegate
            {
                RunCoreCommand(_core.IgnoreCrimeActivity);
                RefreshCrimeIntelligence();
            };
            crimeActivity.Add(_crimeActivityStatus);
            crimeActivity.Add(_beginCrimeActivity);
            crimeActivity.Add(_confrontCrimeActivity);
            crimeActivity.Add(_ignoreCrimeActivity);
            crimeActivity.AddSubMenu(_crimeActivitiesMenu);
            _nearbyCrimeStatus = new NativeItem("Nearby Intelligence");
            _nearbyCrimeStatus.Activated += delegate
            {
                RunCoreAction(_core.ObserveCrimeIntelligence);
                RefreshCrimeIntelligence();
            };
            crimeActivity.Add(_nearbyCrimeStatus);
            crimeActivity.AddSubMenu(_crimeLocationsMenu);
            crimeActivity.AddSubMenu(_crimeGroupsMenu);
            crimeActivity.AddSubMenu(_crimeResourcesMenu);
            LSImmersiveMenuFactory.AddAction(crimeActivity, "Reload Crime Intelligence", delegate
            {
                RunCoreAction(_core.ReloadCrimeIntelligence);
                RefreshCrimeIntelligence();
            });
            crimeActivity.Opening += delegate
            {
                if (_core.IsPoliceAuthorityActive && !_core.CrimeActivity.IsLoaded)
                    RunCoreAction(_core.ReloadCrimeIntelligence);
                RefreshCrimeIntelligence();
            };
        }

        private void RefreshCrimeIntelligence()
        {
            _crimeActivitiesMenu.Clear();
            _crimeLocationsMenu.Clear();
            _crimeGroupsMenu.Clear();
            _crimeResourcesMenu.Clear();
            _crimeDetailsMenu.Clear();
            AddReadOnlyRow(_crimeDetailsMenu, "No Activity Selected");
            bool quietPatrol = _core.IsPoliceAuthorityActive
                && _core.Patrol.IsPatrolling
                && !_core.Dispatch.HasIncident
                && !_core.Convoy.Active
                && !_core.GangResponse.HasActiveIncident
                && !_core.NpcResponse.HasActiveInteraction
                && (!_core.Backup.BlocksPlayerActivities
                    || _core.Backup.State == LSPDBackupAssignmentState.StandingDown);
            if (_crimeActivityStatus != null)
            {
                _crimeActivityStatus.Title = "Crime Activity: " + _core.CrimeActivityStatus;
                _beginCrimeActivity.Enabled = quietPatrol
                    && _core.CrimeActivity.HasAvailableActivity
                    && !_core.CrimeActivity.BlocksOtherPoliceActivities;
                _confrontCrimeActivity.Enabled = _core.IsPoliceAuthorityActive
                    && _core.CrimeActivity.CanConfront;
                _ignoreCrimeActivity.Enabled = _core.IsPoliceAuthorityActive
                    && (_core.CrimeActivity.HasAvailableActivity
                        || _core.CrimeActivity.BlocksOtherPoliceActivities);
            }
            if (_nearbyCrimeStatus != null)
            {
                _nearbyCrimeStatus.Title = "Request Nearby Crime Activity Scan (Tips: "
                    + _core.NearbyCrimeActivityCount + ")";
                _nearbyCrimeStatus.Enabled = quietPatrol
                    && !_core.CrimeActivity.BlocksOtherPoliceActivities;
            }
            _crimeActivitiesMenu.AddSubMenu(_crimeDetailsMenu);
            foreach (LSPDActivityIntel intel in _core.CrimeActivity.NearbyIntel)
            {
                LSPDActivityIntel choice = intel;
                NativeItem row = new NativeItem(
                    "TIP: " + choice.Definition.Name,
                    choice.Location.Name + " | "
                    + choice.DistanceMeters.ToString("0") + " m");
                row.Activated += delegate
                {
                    _crimeDetailsMenu.Clear();
                    AddReadOnlyRow(_crimeDetailsMenu,
                        "Tip: " + choice.Definition.Name);
                    AddReadOnlyRow(_crimeDetailsMenu, choice.Summary);
                    AddReadOnlyRow(_crimeDetailsMenu,
                        "Location: " + choice.Location.Name);
                    AddReadOnlyRow(_crimeDetailsMenu,
                        "Confidence: " + choice.Definition.Confidence);
                    if (!string.IsNullOrWhiteSpace(choice.Territory))
                        AddReadOnlyRow(_crimeDetailsMenu,
                            "Turf context: " + choice.Territory);
                    LSImmersiveMenuFactory.AddAction(
                        _crimeDetailsMenu,
                        "Set GPS to Intelligence",
                        delegate
                        {
                            RunCoreCommand(delegate
                            {
                                return _core.SetCrimeIntelWaypoint(choice.Id);
                            });
                        });
                    LSImmersiveMenuFactory.AddAction(
                        _crimeDetailsMenu,
                        "Begin Investigation When Nearby",
                        delegate
                        {
                            RunCoreCommand(delegate
                            {
                                return _core.BeginCrimeActivity(choice.Id);
                            });
                            RefreshCrimeIntelligence();
                        });
                    _crimeActivitiesMenu.Visible = false;
                    _crimeDetailsMenu.Visible = true;
                };
                _crimeActivitiesMenu.Add(row);
            }
            foreach (LSPDGroupCrimeActivityDefinition definition in _core.CrimeActivity.Activities.OrderBy(item => item.Name))
            {
                LSPDGroupCrimeActivityDefinition choice = definition;
                NativeItem row = new NativeItem(choice.Name, choice.IntelSummary);
                row.Activated += delegate
                {
                    _crimeDetailsMenu.Clear();
                    _crimeDetailsMenu.Add(new NativeItem(choice.Name, choice.IntelSummary) { Enabled = false });
                    AddReadOnlyRow(_crimeDetailsMenu, "Category: " + choice.Category);
                    AddReadOnlyRow(_crimeDetailsMenu, "Severity: " + choice.Severity);
                    AddReadOnlyRow(_crimeDetailsMenu, "Confidence: " + choice.Confidence);
                    foreach (string id in choice.LocationIds)
                    {
                        LSPDCrimeActivityLocationDefinition location = _core.CrimeActivity.Locations.FirstOrDefault(item => item.Id == id);
                        if (location != null) AddReadOnlyRow(_crimeDetailsMenu, location.Name);
                    }
                    foreach (string id in choice.GroupIds)
                    {
                        LSPDCrimeActivityGroupDefinition group = _core.CrimeActivity.Groups.FirstOrDefault(item => item.Id == id);
                        if (group != null) AddReadOnlyRow(_crimeDetailsMenu, group.Name);
                    }
                    _crimeActivitiesMenu.Visible = false;
                    _crimeDetailsMenu.Visible = true;
                };
                _crimeActivitiesMenu.Add(row);
            }
            foreach (LSPDCrimeActivityLocationDefinition location in _core.CrimeActivity.Locations.OrderBy(item => item.Name))
                _crimeLocationsMenu.Add(new NativeItem(location.Name,
                    location.District + " | " + location.Terrain + " | " + location.CoordinateKey) { Enabled = false });
            foreach (LSPDCrimeActivityGroupDefinition group in _core.CrimeActivity.Groups.OrderBy(item => item.Name))
            {
                _crimeGroupsMenu.Add(new NativeItem(group.Name,
                    group.OperatingStyle + " | " + group.PreferredActivity) { Enabled = false });
                foreach (LSPDCrimeResourceSetDefinition set in new[] { group.PedSet, group.WeaponSet, group.VehicleSet })
                    _crimeResourcesMenu.Add(new NativeItem(set.Name,
                        group.Name + ": " + string.Join(", ", set.Items.Select(item => item.Value + " (" + item.Role + ")"))) { Enabled = false });
            }
            AddEmptyRow(_crimeActivitiesMenu, "No Crime Intelligence Loaded", 1);
            AddEmptyRow(_crimeLocationsMenu, "No Locations Loaded");
            AddEmptyRow(_crimeGroupsMenu, "No Group Profiles Loaded");
            AddEmptyRow(_crimeResourcesMenu, "No Scene Resources Loaded");
        }

        private void RefreshProfileSummary()
        {
            if (_profileAgencySummary == null)
                return;

            LSPDProfile profile = _core.Profile;
            LSPDPoliceAgencyDefinition agency = profile.FindAgency(profile.Selection.AgencyId);
            LSPDPoliceStationDefinition station = profile.FindStation(profile.Selection.StationId);
            _profileAgencySummary.Title = "Department: "
                + (agency == null ? "Not Selected" : agency.CallSign);
            _profileStationSummary.Title = "Station: "
                + (station == null ? "Not Selected" : station.DisplayName);
            _profileOfficerSummary.Title = "Officer: " + profile.SelectedPedDisplayName;
            _profileVehicleSummary.Title = "Vehicle: " + profile.SelectedVehicleDisplayName;
            _profileWeaponSummary.Title = "Active Weapon: " + profile.SelectedWeaponDisplayName;
        }

        private void RefreshAgencyMenu()
        {
            _agencyMenu.Clear();
            foreach (LSPDPoliceAgencyDefinition definition in _core.Profile.Agencies)
            {
                LSPDPoliceAgencyDefinition choice = definition;
                NativeItem item = new NativeItem(
                    choice.CallSign + " | " + choice.DisplayName);
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SelectAgency(choice.Id); });
                };
                _agencyMenu.Add(item);
            }
            AddEmptyRow(_agencyMenu, "No Police departments available");
        }

        private void RefreshStationMenu()
        {
            _stationMenu.Clear();
            foreach (LSPDPoliceStationDefinition definition in _core.Profile.Stations)
            {
                LSPDPoliceStationDefinition choice = definition;
                string access = choice.InteriorVerified
                    ? "Verified Interior"
                    : "Exterior Access";
                NativeItem item = new NativeItem(choice.DisplayName, access);
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SelectStation(choice.Id); });
                };
                _stationMenu.Add(item);
            }
            _stationMenu.AddSubMenu(_stationLocationsMenu);
            _stationMenu.AddSubMenu(_coordinationMenu);
            AddEmptyRow(_stationMenu, "No Police stations available", 2);
        }

        private void RefreshLocationMenus()
        {
            _stationLocationsMenu.Clear();
            foreach (LSPDPoliceLocationDefinition location in _core.Profile.Locations)
                AddReadOnlyRow(_stationLocationsMenu, location.DisplayName + " | " + location.District);
            AddEmptyRow(_stationLocationsMenu, "No station locations available");

            _coordinationMenu.Clear();
            foreach (LSPDPoliceCoordinationDefinition route in _core.Profile.Coordination)
                AddReadOnlyRow(_coordinationMenu, route.DisplayName);
            AddEmptyRow(_coordinationMenu, "No coordination routes available");
        }

        private void RefreshPedMenu()
        {
            _pedMenu.Clear();
            foreach (LSPDPoliceModelDefinition definition in _core.Profile.PedModels)
            {
                LSPDPoliceModelDefinition choice = definition;
                NativeItem item = new NativeItem(
                    choice.DisplayName,
                    choice.ModelName + " | " + choice.ModelType);
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SelectPolicePed(choice.Id); });
                };
                _pedMenu.Add(item);
            }
            AddEmptyRow(_pedMenu, "No Police character models available");
        }

        private void RefreshVehicleMenu()
        {
            _vehicleMenu.Clear();
            foreach (LSPDPoliceVehicleDefinition definition in _core.Profile.Vehicles)
            {
                LSPDPoliceVehicleDefinition choice = definition;
                NativeItem item = new NativeItem(
                    choice.DisplayName,
                    choice.ModelName + " | " + choice.VehicleType);
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SelectPoliceVehicle(choice.Id); });
                };
                _vehicleMenu.Add(item);
            }
            AddEmptyRow(_vehicleMenu, "No Police vehicles available");
        }

        private void RefreshWeaponMenu()
        {
            _weaponMenu.Clear();
            foreach (LSPDPoliceWeaponDefinition definition in _core.Profile.Weapons)
            {
                LSPDPoliceWeaponDefinition choice = definition;
                NativeItem item = new NativeItem(
                    choice.DisplayName,
                    choice.WeaponName + " | " + choice.WeaponType);
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SelectPoliceWeapon(choice.Id); });
                };
                _weaponMenu.Add(item);
            }
            AddEmptyRow(_weaponMenu, "No Police weapons available");
        }

        private void RefreshFavouriteMenus()
        {
            RefreshPersonalPedMenu();
            RefreshBackupPedMenu();
            RefreshPersonalVehicleMenu();
            RefreshPersonalWeaponsMenu();
            RefreshAddPersonalWeaponMenu();
            RefreshSavedPersonalWeaponsMenu();
            RefreshRemovePersonalWeaponMenu();
            RefreshSavedFavouritesMenu();
        }

        private void RefreshPersonalPedMenu()
        {
            _personalPedMenu.Clear();
            AddReadOnlyRow(
                _personalPedMenu,
                "Saved Characters: " + _core.Profile.FavoritePeds.Count());
            LSImmersiveMenuFactory.AddAction(
                _personalPedMenu,
                "Use Selected Police Character",
                delegate { RunCoreAction(_core.SetSelectedPedAsFavourite); });
            LSImmersiveMenuFactory.AddAction(
                _personalPedMenu,
                "Enter Addon Ped Model",
                PromptForPersonalPed);
            foreach (LSPDPoliceFavoriteModel definition in _core.Profile.FavoritePeds)
            {
                LSPDPoliceFavoriteModel saved = definition;
                LSImmersiveMenuFactory.AddAction(
                    _personalPedMenu,
                    "Apply " + saved.DisplayName,
                    delegate
                    {
                        RunCoreAction(delegate { return _core.SetCustomFavouritePed(saved.ModelName); });
                    });
            }
        }

        private void RefreshBackupPedMenu()
        {
            _backupPedMenu.Clear();
            LSPDPoliceFavoriteModel active = _core.Profile.ActiveFavoriteBackupPed;
            AddReadOnlyRow(
                _backupPedMenu,
                "Preferred Backup: " + (active == null ? "Default Police Model" : active.DisplayName));
            AddReadOnlyRow(
                _backupPedMenu,
                "Saved Backup Officers: " + _core.Profile.FavoriteBackupPeds.Count());
            LSImmersiveMenuFactory.AddAction(
                _backupPedMenu,
                "Use Selected Police Character as Backup",
                delegate { RunCoreAction(_core.SetSelectedPedAsBackupFavourite); });
            LSImmersiveMenuFactory.AddAction(
                _backupPedMenu,
                "Enter Addon Backup Officer Model",
                PromptForBackupPed);
            foreach (LSPDPoliceFavoriteModel definition in _core.Profile.FavoriteBackupPeds)
            {
                LSPDPoliceFavoriteModel saved = definition;
                NativeItem item = new NativeItem(
                    "Use " + saved.DisplayName,
                    saved.ModelName + (active != null && active.Id == saved.Id ? " | Preferred" : string.Empty));
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SelectBackupFavourite(saved.Id); });
                };
                _backupPedMenu.Add(item);
            }
            AddEmptyRow(_backupPedMenu, "No personal Backup officers saved", 4);
        }

        private void RefreshPersonalVehicleMenu()
        {
            _personalVehicleMenu.Clear();
            AddReadOnlyRow(
                _personalVehicleMenu,
                "Saved Vehicles: " + _core.Profile.FavoriteVehicles.Count());
            LSImmersiveMenuFactory.AddAction(
                _personalVehicleMenu,
                "Use Selected Police Vehicle",
                delegate { RunCoreAction(_core.SetSelectedVehicleAsFavourite); });
            LSImmersiveMenuFactory.AddAction(
                _personalVehicleMenu,
                "Enter Addon Vehicle Model",
                PromptForPersonalVehicle);
            foreach (LSPDPoliceFavoriteModel definition in _core.Profile.FavoriteVehicles)
            {
                LSPDPoliceFavoriteModel saved = definition;
                LSImmersiveMenuFactory.AddAction(
                    _personalVehicleMenu,
                    "Spawn " + saved.DisplayName,
                    delegate
                    {
                        RunCoreAction(delegate { return _core.SetCustomFavouriteVehicle(saved.ModelName); });
                    });
            }
        }

        private void RefreshPersonalWeaponsMenu()
        {
            _personalWeaponsMenu.Clear();
            AddReadOnlyRow(
                _personalWeaponsMenu,
                "Saved Weapons: " + _core.Profile.PersonalWeapons.Count());
            AddReadOnlyRow(
                _personalWeaponsMenu,
                "Active Weapon: " + _core.Profile.SelectedWeaponDisplayName);
            _personalWeaponsMenu.AddSubMenu(_addPersonalWeaponMenu);
            _personalWeaponsMenu.AddSubMenu(_savedPersonalWeaponsMenu);
            _personalWeaponsMenu.AddSubMenu(_removePersonalWeaponMenu);
            LSImmersiveMenuFactory.AddAction(
                _personalWeaponsMenu,
                "Cycle Active Weapon",
                delegate { RunCoreAction(_core.CyclePersonalWeapon); });
            LSImmersiveMenuFactory.AddAction(
                _personalWeaponsMenu,
                "Apply Personal Loadout",
                delegate { RunCoreAction(_core.ApplyPersonalLoadout); });
            LSImmersiveMenuFactory.AddAction(
                _personalWeaponsMenu,
                "Reload Personal Weapons XML",
                delegate { RunCoreAction(_core.ReloadPersonalWeapons); });
            LSImmersiveMenuFactory.AddAction(
                _personalWeaponsMenu,
                "Save Current Weapon Collection",
                delegate { RunCoreAction(_core.SaveCurrentPersonalLoadout); });
        }

        private void RefreshAddPersonalWeaponMenu()
        {
            _addPersonalWeaponMenu.Clear();
            foreach (LSPDPoliceWeaponDefinition definition in _core.Profile.Weapons)
            {
                LSPDPoliceWeaponDefinition choice = definition;
                bool saved = _core.Profile.FindPersonalWeapon(choice.Id) != null;
                NativeItem item = new NativeItem(
                    choice.DisplayName + (saved ? " | Saved" : string.Empty));
                item.Enabled = !saved;
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.AddPersonalWeapon(choice.Id); });
                };
                _addPersonalWeaponMenu.Add(item);
            }
            AddEmptyRow(_addPersonalWeaponMenu, "No Police weapons available");
        }

        private void RefreshSavedPersonalWeaponsMenu()
        {
            _savedPersonalWeaponsMenu.Clear();
            foreach (LSPDPoliceFavoriteWeapon definition in _core.Profile.PersonalWeapons)
            {
                LSPDPoliceFavoriteWeapon choice = definition;
                NativeItem item = new NativeItem(choice.DisplayName, choice.WeaponName);
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SelectPersonalWeapon(choice.Id); });
                };
                _savedPersonalWeaponsMenu.Add(item);
            }
            AddEmptyRow(_savedPersonalWeaponsMenu, "No personal Police weapons saved");
        }

        private void RefreshRemovePersonalWeaponMenu()
        {
            _removePersonalWeaponMenu.Clear();
            foreach (LSPDPoliceFavoriteWeapon definition in _core.Profile.PersonalWeapons)
            {
                LSPDPoliceFavoriteWeapon choice = definition;
                NativeItem item = new NativeItem("Remove " + choice.DisplayName);
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.RemovePersonalWeapon(choice.Id); });
                };
                _removePersonalWeaponMenu.Add(item);
            }
            AddEmptyRow(_removePersonalWeaponMenu, "No personal Police weapons saved");
        }

        private void RefreshSavedFavouritesMenu()
        {
            _savedFavouritesMenu.Clear();
            LSPDPoliceFavoriteModel ped = _core.Profile.ActiveFavoritePed;
            LSPDPoliceFavoriteModel vehicle = _core.Profile.ActiveFavoriteVehicle;
            AddReadOnlyRow(
                _savedFavouritesMenu,
                "Personal Officer: " + (ped == null ? "None" : ped.DisplayName));
            AddReadOnlyRow(
                _savedFavouritesMenu,
                "Personal Vehicle: " + (vehicle == null ? "None" : vehicle.DisplayName));
            AddReadOnlyRow(
                _savedFavouritesMenu,
                "Personal Weapons: " + _core.Profile.PersonalWeapons.Count());
            AddReadOnlyRow(
                _savedFavouritesMenu,
                "Active Weapon: " + _core.Profile.SelectedWeaponDisplayName);
            foreach (LSPDPoliceFavoriteModel definition in _core.Profile.FavoritePeds)
            {
                LSPDPoliceFavoriteModel choice = definition;
                LSImmersiveMenuFactory.AddAction(_savedFavouritesMenu, choice.DisplayName,
                    delegate { RunCoreAction(delegate { return _core.SetCustomFavouritePed(choice.ModelName); }); });
            }
            foreach (LSPDPoliceFavoriteModel definition in _core.Profile.FavoriteVehicles)
            {
                LSPDPoliceFavoriteModel choice = definition;
                LSImmersiveMenuFactory.AddAction(_savedFavouritesMenu, choice.DisplayName,
                    delegate { RunCoreAction(delegate { return _core.SetCustomFavouriteVehicle(choice.ModelName); }); });
            }
        }

        private void RefreshAuthorityStatusMenu()
        {
            _authorityStatusMenu.Clear();
            AddReadOnlyRow(
                _authorityStatusMenu,
                "Police Authority: " + (_core.IsPoliceAuthorityActive ? "Active" : "Inactive"));
            AddReadOnlyRow(
                _authorityStatusMenu,
                "Police Profile: " + (_core.Profile.LastProfileLoaded ? "Loaded" : "Defaults"));
            AddReadOnlyRow(
                _authorityStatusMenu,
                "NPC / Traffic Awareness: Local Patrol Scope");
            AddReadOnlyRow(
                _authorityStatusMenu,
                "Crime Intelligence: " + _core.CrimeActivityStatus);
            AddReadOnlyRow(
                _authorityStatusMenu,
                "Gang Response: " + _core.GangResponse.StatusText);
            AddReadOnlyRow(_authorityStatusMenu,
                "Player Protection: " + (_core.Authority.Settings.EnableWorldBehaviorChanges ? "Enabled" : "Disabled"));
            AddReadOnlyRow(_authorityStatusMenu,
                "Vanilla Police Dispatch: " + (_core.Authority.Settings.SuppressVanillaDispatch ? "Suppressed" : "Allowed"));
            AddReadOnlyRow(_authorityStatusMenu,
                "Military/Base Protection: " + (_core.Authority.Settings.SuppressMilitaryHostility ? "Enabled" : "Disabled"));
            AddReadOnlyRow(_authorityStatusMenu,
                "Nearby Police Allies: " + _core.Response.NearbyAuthorityAllyCount);
            AddReadOnlyRow(
                _authorityStatusMenu,
                "Station Interiors: Verified Only");
            LSImmersiveMenuFactory.AddAction(
                _authorityStatusMenu,
                "Reload Police Data",
                delegate { RunCoreAction(_core.ReloadPoliceData); });
            LSImmersiveMenuFactory.AddAction(
                _authorityStatusMenu,
                "Apply Saved Police Profile",
                delegate { RunCoreAction(_core.ApplySavedProfile); });
        }

        private void BuildAudioMenu()
        {
            AddReadOnlyRow(
                _audioMenu,
                "Audio enable and volume are in Main Settings.");
            LSImmersiveMenuFactory.AddAction(_audioMenu, "Test Radio", delegate
            {
                LSPDAudioResult result = _core.TestRadio();
                if (result != LSPDAudioResult.Queued)
                    Notification.PostTicker("Dispatch audio: " + result, false, false);
            });
            LSImmersiveMenuFactory.AddAction(_audioMenu, "Reload Audio Database", delegate
            {
                bool loaded = _core.Audio.Reload();
                Notification.PostTicker(
                    loaded
                        ? "Dispatch audio database loaded: " + _core.Audio.ReadyClipCount + " recordings."
                        : "Dispatch audio database could not be loaded.",
                    false,
                    false);
            });
            LSImmersiveMenuFactory.AddAction(_audioMenu, "Stop Transmission", _core.Audio.Stop);
        }

        private void PromptForPersonalPed()
        {
            string value = ReadCustomIdentifier("Enter the addon ped model name.");
            if (string.IsNullOrWhiteSpace(value))
                return;
            RunCoreAction(delegate { return _core.SetCustomFavouritePed(value); });
        }

        private void PromptForPersonalVehicle()
        {
            string value = ReadCustomIdentifier("Enter the addon vehicle model name.");
            if (string.IsNullOrWhiteSpace(value))
                return;
            RunCoreAction(delegate { return _core.SetCustomFavouriteVehicle(value); });
        }

        private void PromptForBackupPed()
        {
            string value = ReadCustomIdentifier("Enter the addon Backup officer model name.");
            if (string.IsNullOrWhiteSpace(value))
                return;
            RunCoreAction(delegate { return _core.SetCustomBackupFavourite(value); });
        }

        private static string ReadCustomIdentifier(string instruction)
        {
            // The prompt is shown separately. Passing it as GetUserInput's first
            // string argument would make it the editable default and save the
            // literal prompt, which caused the broken profile shown in testing.
            Notification.PostTicker(instruction, false, false);
            string value = Game.GetUserInput(
                WindowTitle.EnterMessage60,
                string.Empty,
                60);
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private void RunCoreAction(Func<bool> action)
        {
            bool success = action();
            string message = _core.LastOperationMessage;
            if (!string.IsNullOrWhiteSpace(message))
                Notification.PostTicker(message, false, false);
            if (success)
                RefreshAllMenus();
        }

        private void RunPatrolToggle()
        {
            bool hadDispatchOffer = _core.Dispatch.HasOffer;
            bool success = _core.TogglePatrol();
            bool createdDispatchOffer = !hadDispatchOffer && _core.Dispatch.HasOffer;

            if (!createdDispatchOffer && !string.IsNullOrWhiteSpace(_core.LastOperationMessage))
                Notification.PostTicker(_core.LastOperationMessage, false, false);

            if (success)
                RefreshAllMenus();
        }

        private void RunCoreCommand(Func<string> command)
        {
            string message = command();
            if (!string.IsNullOrWhiteSpace(message))
                Notification.PostTicker(message, false, false);
            RefreshAllMenus();
        }

        private void RunDispatchRequest()
        {
            bool hadDispatchOffer = _core.Dispatch.HasOffer;
            string message = _core.RequestDispatch();
            bool createdDispatchOffer = !hadDispatchOffer && _core.Dispatch.HasOffer;

            if (!createdDispatchOffer && !string.IsNullOrWhiteSpace(message))
                Notification.PostTicker(message, false, false);

            RefreshAllMenus();
        }

        private void RunNpcCommand(Func<string> command)
        {
            // NPCResponse owns the visible interaction-stage notification. The
            // UI must not echo the Core status for the same command, or one
            // player action produces two response tickers and their sounds.
            bool wasActive = _core.NpcResponse.HasActiveInteraction;
            string message = command();
            bool isActive = _core.NpcResponse.HasActiveInteraction;
            if (!wasActive && !isActive && !string.IsNullOrWhiteSpace(message))
                Notification.PostTicker(message, false, false);
            RefreshAllMenus();
        }

        private void RunDispatchDecision(bool? accept)
        {
            LSPDAudioResult result = _core.DecideDispatch(accept);
            if (result == LSPDAudioResult.Inactive)
                Notification.PostTicker("No dispatch offer is currently available.", false, false);
            RefreshAllMenus();
        }

        private void DeactivateAuthority()
        {
            _core.ExitPoliceAuthority();
            Notification.PostTicker(_core.LastOperationMessage, false, false);
            foreach (NativeMenu menu in _menus)
                menu.Visible = false;
            if (Menu.Parent != null)
                Menu.Parent.Visible = true;
        }

        private NativeMenu Register(NativeMenu menu)
        {
            _menus.Add(menu);
            return menu;
        }

        private static NativeItem AddSummaryItem(NativeMenu menu, string title)
        {
            NativeItem item = new NativeItem(title + ": Not Selected");
            item.Enabled = false;
            menu.Add(item);
            return item;
        }

        private static void AddReadOnlyRow(NativeMenu menu, string text)
        {
            NativeItem item = new NativeItem(text);
            item.Enabled = false;
            menu.Add(item);
        }

        private static void AddEmptyRow(NativeMenu menu, string text, int fixedRows = 0)
        {
            if (menu.Items.Count <= fixedRows)
                AddReadOnlyRow(menu, text);
        }

        internal void Open()
        {
            bool wasActive = _core.IsPoliceAuthorityActive;
            _core.EnterPoliceAuthority();
            _core.PrepareAuthorityUi();
            RefreshAllMenus();
            Menu.Visible = true;
            if (!wasActive)
                Notification.PostTicker(_core.LastOperationMessage, false, false);
        }

        internal void Close()
        {
            Menu.Visible = false;
        }

        internal void SetRoleActive(bool active)
        {
            _core.SetRoleActive(active);
            if (!active)
                foreach (NativeMenu menu in _menus)
                    menu.Visible = false;
        }

        internal void Process(bool paused)
        {
            bool menuVisible = _menus.Any(menu => menu != null && menu.Visible);
            _core.Process(paused, !menuVisible);
            // An incident can arrive while Dispatch is already visible. Refresh
            // only changed availability, without rebuilding menus or reattaching
            // handlers on each tick, so controls cannot remain stale until reopen.
            if (_refreshOffer != null) _refreshOffer();
            RefreshConvoyMenu();
        }

        internal void Shutdown()
        {
            _core.Dispose();
            foreach (NativeMenu menu in _menus)
                menu.Visible = false;
        }
    }
}
