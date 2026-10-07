using GTA;
using GTA.Native;
using GTA.UI;
using LemonUI.Elements;
using LemonUI.Menus;
using LemonUI.Tools;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LSImmersiveLife
{
    /// <summary>
    /// User-facing Police Authority configuration surface. It presents profile
    /// data and delegates every state or GTA operation to PoliceCore.
    /// </summary>
    internal sealed class LSImmersivePoliceUI
    {
        private readonly LSImmersivePoliceCore _core;
        private readonly LSPDStation _station = new LSPDStation();
        private readonly LSPDCitizenRecordPanel _citizenRecordPanel = new LSPDCitizenRecordPanel();
        private readonly List<NativeMenu> _menus = new List<NativeMenu>();

        private NativeMenu _stationDatabaseMenu;
        private NativeMenu _policeResponseMenu;
        private NativeMenu _policeResponseDispatchMenu;
        private NativeMenu _backupResponseMenu;
        private NativeMenu _citizenFrameworkMenu;
        private NativeMenu _profileMenu;
        private NativeMenu _stationSetupMenu;
        private NativeMenu _stationGarageMenu;
        private NativeMenu _agencyMenu;
        private NativeMenu _stationMenu;
        private NativeMenu _stationLocationsMenu;
        private NativeMenu _coordinationMenu;
        private NativeMenu _weaponMenu;
        private NativeMenu _favouriteMenu;
        private NativeMenu _personalPedMenu;
        private NativeMenu _citizenBackupPedMenu;
        private NativeMenu _dispatchBackupPedMenu;
        private NativeMenu _personalVehicleMenu;
        private NativeMenu _personalWeaponsMenu;
        private NativeMenu _addPersonalWeaponMenu;
        private NativeMenu _savedPersonalWeaponsMenu;
        private NativeMenu _removePersonalWeaponMenu;
        private NativeMenu _audioMenu;
        private Action _refreshOffer;
        private NativeItem _investigateDispatch;
        private NativeItem _secureDispatch;
        private NativeItem _releaseDispatchSuspect;
        private NativeItem _requestDispatch;
        private NativeItem _approveTransport;
        private NativeItem _callBackup;
        private NativeItem _callDefaultBackup;
        private NativeItem _gangBackupRequest;
        private NativeItem _crimeBackupRequest;
        private NativeItem _acceptNpcInteraction;
        private NativeItem _rejectNpcInteraction;
        private NativeItem _greetNpc;
        private NativeItem _requestNpcDocuments;
        private NativeItem _requestNpcValidation;
        private NativeItem _arrestNpcCitizen;
        private NativeItem _releaseNpcCitizen;
        private NativeItem _requestNpcTransportBackup;
        private NativeItem _convoyStatus;
        private NativeItem _requestConvoyActivity;
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

        private NativeItem _stationAlmanacToggle;
        private NativeListItem<string> _stationSetupPedChoice;
        private NativeListItem<string> _stationSetupVehicleChoice;
        private NativeListItem<string> _stationSetupWeaponChoice;
        private NativeListItem<string> _stationSetupAgencyChoice;
        private PointF _stationSetupBannerTitlePosition;
        private List<LSPDPoliceModelDefinition> _stationSetupPedChoices = new List<LSPDPoliceModelDefinition>();
        private List<LSPDPoliceVehicleDefinition> _stationSetupVehicleChoices = new List<LSPDPoliceVehicleDefinition>();
        private List<LSPDPoliceWeaponDefinition> _stationSetupWeaponChoices = new List<LSPDPoliceWeaponDefinition>();
        private List<LSPDPoliceAgencyDefinition> _stationSetupAgencyChoices = new List<LSPDPoliceAgencyDefinition>();
        private bool _stationSetupSyncing;
        private bool _stationSetupSessionActive;
        private bool _stationSetupFinalizing;
        private bool _stationSetupConfirmKeyDown;
        private bool _backupFrameworkKeyDown;
        private bool _dispatchFrameworkKeyDown;
        private bool _citizenFrameworkKeyDown;
        private bool _responseFrameworkBackKeyDown;
        private NativeMenu _shortcutOpenedResponseFrameworkMenu;
        private NativeMenu _pendingResponseFrameworkMenu;
        private Keys _pendingResponseFrameworkKey;
        private string _pendingResponseFrameworkTitle = string.Empty;
        private string _pendingStationSetupPedId = string.Empty;
        private string _pendingStationSetupVehicleId = string.Empty;
        private string _stationEntrySuppressedUntilLeaveId = string.Empty;

        internal LSImmersivePoliceUI(LSImmersivePoliceCore core)
        {
            _core = core ?? throw new ArgumentNullException("core");
            Menu = Register(LSImmersiveMenuFactory.Create(
                "Immersive Police Authority",
                "Police Response"));
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
            _stationDatabaseMenu = Register(LSImmersiveMenuFactory.Create(
                "LSIMMERSIVELIFE",
                "Police Database"));
            _profileMenu = Register(LSImmersiveMenuFactory.Create(
                "Police Immersive Profile",
                "Choose your Preferred Resources"));
            _stationSetupMenu = Register(LSImmersiveMenuFactory.Create(
                "Customize your Police Roleplay",
                string.Empty));
            // Keep the documented title intact while giving its long text a
            // banner wide enough to fit. No duplicate menu name is needed
            // below the banner on this setup screen.
            _stationSetupMenu.Width = 640f;
            ScaledRectangle stationSetupBanner = new ScaledRectangle(
                new PointF(0f, 0f),
                new SizeF(640f, 105f));
            stationSetupBanner.Color = Color.FromArgb(255, 47, 111, 190);
            _stationSetupMenu.Banner = stationSetupBanner;
            PointF defaultBannerTitlePosition = _stationSetupMenu.BannerText.Position;
            _stationSetupBannerTitlePosition = new PointF(
                defaultBannerTitlePosition.X + (_stationSetupMenu.Width / 2f) - 290f,
                defaultBannerTitlePosition.Y);
            _stationSetupMenu.BannerText.Scale = 0.96f;
            _stationSetupMenu.BannerText.Position = _stationSetupBannerTitlePosition;
            _stationSetupMenu.Name = string.Empty;
            _stationGarageMenu = Register(LSImmersiveMenuFactory.Create(
                "Garage",
                string.Empty));
            _stationGarageMenu.Name = string.Empty;
            _agencyMenu = Register(LSImmersiveMenuFactory.Create("Police Department", string.Empty));
            _stationMenu = Register(LSImmersiveMenuFactory.Create("Police Stations", string.Empty));
            _stationLocationsMenu = Register(LSImmersiveMenuFactory.Create("Station Locations", string.Empty));
            _coordinationMenu = Register(LSImmersiveMenuFactory.Create("Coordination Routes", string.Empty));
            _weaponMenu = Register(LSImmersiveMenuFactory.Create("Weapon Loadout", string.Empty));
            _favouriteMenu = Register(LSImmersiveMenuFactory.Create("Saved Favorite Lists", string.Empty));
            _personalPedMenu = Register(LSImmersiveMenuFactory.Create("DLC Characters", string.Empty));
            _citizenBackupPedMenu = Register(LSImmersiveMenuFactory.Create(
                "Back Up Ped for Citizen Interaction",
                "Select the Character Model"));
            _dispatchBackupPedMenu = Register(LSImmersiveMenuFactory.Create(
                "Back Up Ped for Dispatch and Crime Activity",
                "Select the Character Model"));
            _personalVehicleMenu = Register(LSImmersiveMenuFactory.Create("DLC Vehicles", string.Empty));
            _personalWeaponsMenu = Register(LSImmersiveMenuFactory.Create("Personal Loudout Weapon", string.Empty));
            _addPersonalWeaponMenu = Register(LSImmersiveMenuFactory.Create("Add From Weapon Library", string.Empty));
            _savedPersonalWeaponsMenu = Register(LSImmersiveMenuFactory.Create("DLC Inventory Weapons", string.Empty));
            _removePersonalWeaponMenu = Register(LSImmersiveMenuFactory.Create("Remove Personal Weapon", string.Empty));
            _audioMenu = Register(LSImmersiveMenuFactory.Create("Audio Response", string.Empty));

            BuildStationSetupMenus();
            BuildGameplayMenus();
            AddSubMenuEntry(Menu, _stationDatabaseMenu, "Police Station");
            AddSubMenuEntry(Menu, _profileMenu, "Police Profile");
            AddSubMenuEntry(Menu, _audioMenu, "Audio Response");

            LSImmersiveMenuFactory.AddAction(
                _stationDatabaseMenu,
                "Set GPS to Selected Police Station",
                delegate { RunCoreCommand(_core.SetSelectedStationWaypoint); });
            _stationAlmanacToggle = new NativeItem("Police Almaniac");
            _stationAlmanacToggle.Activated += delegate
            {
                _station.ToggleAlmanac();
            };
            _stationDatabaseMenu.Add(_stationAlmanacToggle);
            AddSubMenuEntry(_stationDatabaseMenu, _stationMenu, "Choose Police Station");

            _stationMenu.AddSubMenu(_stationLocationsMenu);
            _stationMenu.AddSubMenu(_coordinationMenu);

            _favouriteMenu.AddSubMenu(_personalPedMenu);
            _favouriteMenu.AddSubMenu(_personalVehicleMenu);
            _favouriteMenu.AddSubMenu(_savedPersonalWeaponsMenu);

            _personalWeaponsMenu.AddSubMenu(_addPersonalWeaponMenu);
            _personalWeaponsMenu.AddSubMenu(_removePersonalWeaponMenu);

            BuildAudioMenu();

            _agencyMenu.Opening += delegate { RefreshAgencyMenu(); };
            _stationMenu.Opening += delegate { RefreshStationMenu(); };
            _stationSetupMenu.Opening += delegate { RefreshStationSetupSelections(); };
            _profileMenu.Opening += delegate { RefreshProfileMenu(); };
            _weaponMenu.Opening += delegate { RefreshWeaponMenu(); };
            _favouriteMenu.Opening += delegate { RefreshFavouriteMenus(); };
            _personalPedMenu.Opening += delegate { RefreshPersonalPedMenu(); };
            _personalVehicleMenu.Opening += delegate { RefreshPersonalVehicleMenu(); };
            _personalWeaponsMenu.Opening += delegate { RefreshPersonalWeaponsMenu(); };
            _citizenBackupPedMenu.Opening += delegate { RefreshCitizenBackupPedMenu(); };
            _dispatchBackupPedMenu.Opening += delegate { RefreshDispatchBackupPedMenu(); };
            _addPersonalWeaponMenu.Opening += delegate { RefreshAddPersonalWeaponMenu(); };
            _savedPersonalWeaponsMenu.Opening += delegate { RefreshSavedPersonalWeaponsMenu(); };
            _removePersonalWeaponMenu.Opening += delegate { RefreshRemovePersonalWeaponMenu(); };
        }

        private void BuildStationSetupMenus()
        {
            _stationSetupPedChoices = _core.Profile.PedModels
                .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _stationSetupVehicleChoices = _core.Profile.Vehicles
                .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _stationSetupWeaponChoices = _core.Profile.Weapons
                .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _stationSetupAgencyChoices = _core.Profile.Agencies
                .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _stationSetupPedChoice = new NativeListItem<string>(
                "Police Model",
                string.Empty,
                DisplayNames(_stationSetupPedChoices.Select(value => value.DisplayName)));
            _stationSetupPedChoice.Enabled = _stationSetupPedChoices.Count > 0;
            _stationSetupPedChoice.ItemChanged += delegate
            {
                if (!_stationSetupSyncing && _stationSetupPedChoices.Count > 0)
                    StageStationSetupPedSelection();
            };
            _stationSetupMenu.Add(_stationSetupPedChoice);

            AddSubMenuEntry(_stationSetupMenu, _stationGarageMenu, "Garages");
            _stationSetupVehicleChoice = new NativeListItem<string>(
                "Model",
                string.Empty,
                DisplayNames(_stationSetupVehicleChoices.Select(value => value.DisplayName)));
            _stationSetupVehicleChoice.Enabled = _stationSetupVehicleChoices.Count > 0;
            _stationSetupVehicleChoice.ItemChanged += delegate
            {
                if (!_stationSetupSyncing && _stationSetupVehicleChoices.Count > 0)
                    StageStationSetupVehicleSelection();
            };
            _stationGarageMenu.Add(_stationSetupVehicleChoice);

            _stationSetupWeaponChoice = new NativeListItem<string>(
                "Police Armory",
                string.Empty,
                DisplayNames(_stationSetupWeaponChoices.Select(value => value.DisplayName)));
            _stationSetupWeaponChoice.Enabled = _stationSetupWeaponChoices.Count > 0;
            _stationSetupWeaponChoice.ItemChanged += delegate
            {
                if (!_stationSetupSyncing && _stationSetupWeaponChoices.Count > 0)
                    RunStationSetupChoice(delegate
                    {
                        return _core.SelectStationSetupWeapon(
                            _stationSetupWeaponChoices[_stationSetupWeaponChoice.SelectedIndex].Id);
                    });
            };
            _stationSetupMenu.Add(_stationSetupWeaponChoice);

            _stationSetupAgencyChoice = new NativeListItem<string>(
                "Select an Agency",
                string.Empty,
                DisplayNames(_stationSetupAgencyChoices.Select(value =>
                    string.IsNullOrWhiteSpace(value.CallSign)
                        ? value.DisplayName
                        : value.CallSign)));
            _stationSetupAgencyChoice.Enabled = _stationSetupAgencyChoices.Count > 0;
            _stationSetupAgencyChoice.ItemChanged += delegate
            {
                if (!_stationSetupSyncing && _stationSetupAgencyChoices.Count > 0)
                    RunStationSetupChoice(delegate
                    {
                        return _core.SelectStationSetupAgency(
                            _stationSetupAgencyChoices[_stationSetupAgencyChoice.SelectedIndex].Id);
                    });
            };
            _stationSetupMenu.Add(_stationSetupAgencyChoice);
            RefreshStationSetupSelections();
        }

        private static string[] DisplayNames(IEnumerable<string> values)
        {
            string[] items = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            return items.Length > 0 ? items : new[] { "No options available" };
        }

        private void RefreshStationSetupSelections()
        {
            if (_stationSetupMenu == null)
                return;

            _stationSetupSyncing = true;
            try
            {
                string selectedPedId = string.IsNullOrWhiteSpace(_pendingStationSetupPedId)
                    ? _core.Profile.Selection.PedId : _pendingStationSetupPedId;
                string selectedVehicleId = string.IsNullOrWhiteSpace(_pendingStationSetupVehicleId)
                    ? _core.Profile.Selection.VehicleId : _pendingStationSetupVehicleId;
                SelectListIndex(_stationSetupPedChoice, _stationSetupPedChoices.FindIndex(
                    value => string.Equals(value.Id, selectedPedId, StringComparison.OrdinalIgnoreCase)));
                SelectListIndex(_stationSetupVehicleChoice, _stationSetupVehicleChoices.FindIndex(
                    value => string.Equals(value.Id, selectedVehicleId, StringComparison.OrdinalIgnoreCase)));
                SelectListIndex(_stationSetupWeaponChoice, _stationSetupWeaponChoices.FindIndex(
                    value => string.Equals(value.Id, _core.Profile.Selection.WeaponId, StringComparison.OrdinalIgnoreCase)));
                SelectListIndex(_stationSetupAgencyChoice, _stationSetupAgencyChoices.FindIndex(
                    value => string.Equals(value.Id, _core.Profile.Selection.AgencyId, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                _stationSetupSyncing = false;
            }
        }

        private static void SelectListIndex(NativeListItem<string> item, int index)
        {
            if (item == null || item.Items.Count == 0)
                return;
            item.SelectedIndex = index < 0 || index >= item.Items.Count ? 0 : index;
        }

        private void RunStationSetupChoice(Func<bool> action)
        {
            if (!action())
            {
                if (!string.IsNullOrWhiteSpace(_core.LastOperationMessage))
                    Notification.PostTicker(_core.LastOperationMessage, false, false);
                RefreshStationSetupSelections();
            }
        }

        private void StageStationSetupPedSelection()
        {
            LSPDPoliceModelDefinition selected = _stationSetupPedChoices[_stationSetupPedChoice.SelectedIndex];
            bool alreadyPending = !string.IsNullOrWhiteSpace(_pendingStationSetupPedId);
            _pendingStationSetupPedId = string.Equals(
                selected.Id,
                _core.Profile.Selection.PedId,
                StringComparison.OrdinalIgnoreCase)
                ? string.Empty : selected.Id;
            if (!alreadyPending && !string.IsNullOrWhiteSpace(_pendingStationSetupPedId))
                PostStationPreviewConfirmationPrompt(false);
        }

        private void StageStationSetupVehicleSelection()
        {
            LSPDPoliceVehicleDefinition selected = _stationSetupVehicleChoices[_stationSetupVehicleChoice.SelectedIndex];
            bool alreadyPending = !string.IsNullOrWhiteSpace(_pendingStationSetupVehicleId);
            _pendingStationSetupVehicleId = string.Equals(
                selected.Id,
                _core.Profile.Selection.VehicleId,
                StringComparison.OrdinalIgnoreCase)
                ? string.Empty : selected.Id;
            if (!alreadyPending && !string.IsNullOrWhiteSpace(_pendingStationSetupVehicleId))
                PostStationPreviewConfirmationPrompt(true);
        }

        private void PostStationPreviewConfirmationPrompt(bool garage)
        {
            Notification.PostTicker(
                garage
                    ? "Confirm: press Y to use the highlighted Police vehicle."
                    : "Confirm: press Y to use the highlighted Police Model.",
                false,
                false);
        }

        private void ConfirmStationPreviewSelection(LSPDStation.StationPreviewMode mode)
        {
            if (mode == LSPDStation.StationPreviewMode.Garage
                && !string.IsNullOrWhiteSpace(_pendingStationSetupVehicleId))
            {
                LSPDPoliceVehicleDefinition selected = _core.Profile.FindVehicle(_pendingStationSetupVehicleId);
                if (_core.SelectStationSetupVehicle(_pendingStationSetupVehicleId))
                {
                    _pendingStationSetupVehicleId = string.Empty;
                    RefreshStationSetupSelections();
                    Notification.PostTicker("Confirmed: " + (selected == null ? "Police vehicle" : selected.DisplayName)
                        + ". Loading garage preview.", false, false);
                }
                else if (!string.IsNullOrWhiteSpace(_core.LastOperationMessage))
                    Notification.PostTicker(_core.LastOperationMessage, false, false);
                return;
            }

            if (mode == LSPDStation.StationPreviewMode.Wardrobe
                && !string.IsNullOrWhiteSpace(_pendingStationSetupPedId))
            {
                LSPDPoliceModelDefinition selected = _core.Profile.FindPed(_pendingStationSetupPedId);
                if (_core.SelectStationSetupPed(_pendingStationSetupPedId))
                {
                    _pendingStationSetupPedId = string.Empty;
                    RefreshStationSetupSelections();
                    Notification.PostTicker("Confirmed: " + (selected == null ? "Police Model" : selected.DisplayName)
                        + ". Loading wardrobe preview.", false, false);
                }
                else if (!string.IsNullOrWhiteSpace(_core.LastOperationMessage))
                    Notification.PostTicker(_core.LastOperationMessage, false, false);
            }
        }

        private bool HasPendingStationPreviewSelection()
        {
            return !string.IsNullOrWhiteSpace(_pendingStationSetupPedId)
                || !string.IsNullOrWhiteSpace(_pendingStationSetupVehicleId);
        }

        private void RefreshAllMenus()
        {
            RefreshAgencyMenu();
            RefreshStationMenu();
            RefreshLocationMenus();
            RefreshProfileMenu();
            RefreshWeaponMenu();
            RefreshFavouriteMenus();
            RefreshCitizenBackupPedMenu();
            RefreshDispatchBackupPedMenu();
            RefreshPersonalWeaponsMenu();
            RefreshConvoyMenu();
            RefreshNpcInteractionMenu();
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
            // The Authority entry remains named "Police Gameplay" while the
            // opened screen uses the documented Police Response title.
            _policeResponseMenu = Register(LSImmersiveMenuFactory.Create(
                "Police Response",
                "Start your Response Duty"));
            AddSubMenuEntry(Menu, _policeResponseMenu, "Police Gameplay");

            _citizenFrameworkMenu = Register(LSImmersiveMenuFactory.Create(
                "Citizen Ped Response",
                "Resolve the Citizen’s Interaction"));
            NativeMenu intelligence = Register(LSImmersiveMenuFactory.Create(
                "Crime Activity Response",
                string.Empty));
            NativeMenu convoy = Register(LSImmersiveMenuFactory.Create(
                "Convoy Activity Response",
                string.Empty));
            _backupResponseMenu = Register(LSImmersiveMenuFactory.Create(
                "Back Up Response",
                "Call out a Back Up to Assist You"));
            _gangIntelligenceMenu = Register(LSImmersiveMenuFactory.Create(
                "Gang and Turf Response",
                string.Empty));
            _gangTurfMenu = Register(LSImmersiveMenuFactory.Create("Gang Turf Zones", string.Empty));

            BuildResponseMenu(_policeResponseMenu);
            AddSubMenuEntry(_policeResponseMenu, _backupResponseMenu, "Back Up Response");
            AddSubMenuEntry(_policeResponseMenu, _citizenFrameworkMenu, "Ambient Citizen Response");
            AddSubMenuEntry(_policeResponseMenu, intelligence, "Crime Activity Response");
            AddSubMenuEntry(_policeResponseMenu, convoy, "Convoy Activity Response");
            AddSubMenuEntry(_policeResponseMenu, _gangIntelligenceMenu, "Gang and Turf Response");
            BuildBackupResponseMenu(_backupResponseMenu);
            BuildNpcInteractionMenu(_citizenFrameworkMenu);
            BuildCrimeActivityMenu(intelligence);
            _requestCrimeActivity = new NativeItem("Request Crime Activity");
            _requestCrimeActivity.Activated += delegate
            {
                RunCoreCommand(_core.RequestCrimeActivity);
                RefreshCrimeIntelligence();
            };
            intelligence.Add(_requestCrimeActivity);
            BuildGangIntelligenceMenu();
            _convoyStatus = new NativeItem("Convoy Status: No active Convoy operation.");
            _requestConvoyActivity = new NativeItem("Request Prisoner Convoy Activity");
            _continueTransport = new NativeItem("Continue Prisoner Transport");
            _cancelActiveDispatch = new NativeItem("Cancel Active Dispatch");
            _resetPoliceRuntime = new NativeItem("Reset Police Runtime");
            _requestConvoyActivity.Activated += delegate { RunCoreCommand(_core.RequestConvoyActivity); };
            _continueTransport.Activated += delegate { RunCoreCommand(_core.RequestPrisonerTransport); };
            _cancelActiveDispatch.Activated += delegate { RunCoreCommand(_core.CancelActiveDispatch); };
            _resetPoliceRuntime.Activated += delegate { RunCoreCommand(_core.ResetPoliceRuntime); };
            convoy.Add(_convoyStatus);
            convoy.Add(_requestConvoyActivity);
            convoy.Add(_continueTransport);
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
            response.Opening += delegate { refreshPatrol(); };
            _policeResponseDispatchMenu = Register(LSImmersiveMenuFactory.Create(
                "Dispatch Activity",
                "Your Dispatch Authority Response"));
            NativeMenu dispatch = _policeResponseDispatchMenu;
            AddSubMenuEntry(response, dispatch, "Dispatch Activity Response");
            _refreshOffer = delegate
            {
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
                _investigateDispatch.Enabled = _investigateDispatch != null
                    && sceneAvailable
                    && state != LSPDDispatchState.Offered
                    && state != LSPDDispatchState.Completed
                    && state != LSPDDispatchState.Cancelled;
                _secureDispatch.Enabled = _secureDispatch != null
                    && sceneAvailable
                    && state != LSPDDispatchState.Offered
                    && state != LSPDDispatchState.EnRoute
                    && state != LSPDDispatchState.Arrested
                    && state != LSPDDispatchState.AwaitingTransport
                    && state != LSPDDispatchState.HoldingAtStation
                    && state != LSPDDispatchState.PrisonTransfer;
                if (_releaseDispatchSuspect != null)
                    _releaseDispatchSuspect.Enabled = _core.CanReleaseCurrentDispatchSuspect;
                if (_approveTransport != null)
                    _approveTransport.Enabled = _core.HasPendingPreferredUtilityTransportChoice
                        || _core.Convoy.HoldingAtStation
                        || state == LSPDDispatchState.Arrested;
                if (_requestCrimeActivity != null)
                    _requestCrimeActivity.Enabled = quietPatrol;
                if (_requestDispatch != null)
                    _requestDispatch.Enabled = quietPatrol && _core.Dispatch.IsEnabled;
                RefreshBackupResponseMenu();
            };
            dispatch.Opening += delegate { _refreshOffer(); };

            _requestDispatch = new NativeItem(
                "Request a Dispatch",
                "Ask the Call-Out Radio for an eligible incident now.");
            _requestDispatch.Activated += delegate
            {
                RunCoreCommand(_core.RequestDispatch);
            };
            dispatch.Add(_requestDispatch);

            // Each framework choice uses its documented owner or remains
            // visibly unavailable until a matching owner action exists.
            _investigateDispatch = new NativeItem("Arrest the Suspect");
            _secureDispatch = new NativeItem("Secure the Suspect");
            _releaseDispatchSuspect = new NativeItem("Release the Ped");
            _approveTransport = new NativeItem("Request Transport");
            _finishCustody = new NativeItem("Complete the Transport");
            _investigateDispatch.Activated += delegate { RunCoreCommand(_core.InvestigateDispatch); };
            _secureDispatch.Activated += delegate { RunCoreCommand(_core.SecureDispatchSuspect); };
            _releaseDispatchSuspect.Activated += delegate { RunCoreCommand(_core.ReleaseDispatchSuspect); };
            _approveTransport.Activated += delegate { RunCoreCommand(_core.ApproveDispatchTransportAction); };
            _finishCustody.Activated += delegate { RunCoreCommand(_core.CompletePrisonerTransport); };
            dispatch.Add(_investigateDispatch);
            dispatch.Add(_secureDispatch);
            AddUnavailableChoice(dispatch, "Instruct the Back Up Ped to Arrest a Suspect");
            AddUnavailableChoice(dispatch, "Instruct the Back Up Ped to Load the Suspect inside the Utility");
            dispatch.Add(_approveTransport);
            AddUnavailableChoice(dispatch, "Instruct the Escort Ped to Arrest a Suspect");
            dispatch.Add(_finishCustody);
            AddUnavailableChoice(dispatch, "Spawn the Personal Utility");
            dispatch.Add(_releaseDispatchSuspect);
            _refreshOffer();
        }

        private void BuildBackupResponseMenu(NativeMenu backupResponse)
        {
            _callBackup = new NativeItem(
                "Request a Back Up Dispatch Assistance",
                "Available while a Dispatch incident is active.");
            _gangBackupRequest = new NativeItem(
                "Request a Back Up Gang Escalation",
                "Available while a Gang escalation is active.");
            _crimeBackupRequest = new NativeItem(
                "Request a Back Up Crime Activity",
                "Available while a Crime Activity is active.");
            _callDefaultBackup = new NativeItem(
                "Request a Back Up NPC Ped Interaction",
                "Available while a Citizen interaction requires assistance.");

            _callBackup.Activated += delegate { RunCoreCommand(_core.CallFavoriteBackup); };
            _gangBackupRequest.Activated += delegate { RunCoreCommand(_core.CallFavoriteBackup); };
            _crimeBackupRequest.Activated += delegate { RunCoreCommand(_core.CallFavoriteBackup); };
            _callDefaultBackup.Activated += delegate { RunCoreCommand(_core.CallFavoriteBackup); };

            backupResponse.Add(_callBackup);
            backupResponse.Add(_gangBackupRequest);
            backupResponse.Add(_crimeBackupRequest);
            backupResponse.Add(_callDefaultBackup);
            backupResponse.Opening += delegate { RefreshBackupResponseMenu(); };
            RefreshBackupResponseMenu();
        }

        private void RefreshBackupResponseMenu()
        {
            if (_callBackup == null || _gangBackupRequest == null
                || _crimeBackupRequest == null || _callDefaultBackup == null)
                return;

            bool available = _core.IsPoliceAuthorityActive && _core.Patrol.IsPatrolling;
            _callBackup.Enabled = available && _core.Dispatch.HasIncident;
            _gangBackupRequest.Enabled = available && _core.GangResponse.HasActiveIncident;
            _crimeBackupRequest.Enabled = available && _core.CrimeActivity.BlocksOtherPoliceActivities;
            _callDefaultBackup.Enabled = available
                && _core.NpcResponse.CanRequestBackup
                && !_core.NpcResponse.CanBeginPlayerVehicleCustody;
        }

        private void RefreshNpcInteractionMenu()
        {
            if (_acceptNpcInteraction == null)
                return;

            // Keep the documented choices selectable. The NPC owner validates
            // each action against the live contact stage and returns a clear
            // instruction when it is not ready, instead of showing dead rows.
            _acceptNpcInteraction.Enabled = true;
            _rejectNpcInteraction.Enabled = true;
            _greetNpc.Enabled = true;
            _requestNpcDocuments.Enabled = true;
            _requestNpcValidation.Enabled = true;
            _arrestNpcCitizen.Enabled = true;
            _releaseNpcCitizen.Enabled = true;
            _requestNpcTransportBackup.Enabled = true;
        }

        private void BuildNpcInteractionMenu(NativeMenu npcInteraction)
        {
            _acceptNpcInteraction = new NativeItem("Accept");
            _rejectNpcInteraction = new NativeItem("Reject");
            _greetNpc = new NativeItem("Greet The Ped");
            _requestNpcDocuments = new NativeItem("Request the Validation ID/Document");
            _requestNpcValidation = new NativeItem("Request Dispatcher For Validation Request");
            _arrestNpcCitizen = new NativeItem("Arrest the Ped");
            _releaseNpcCitizen = new NativeItem("Release The Ped");
            _requestNpcTransportBackup = new NativeItem(
                "Request a Back Up For Citizen Transport");

            _acceptNpcInteraction.Activated += delegate
            {
                RunNpcCommand(_core.AcceptNpcInteraction);
            };
            _rejectNpcInteraction.Activated += delegate
            {
                RunNpcCommand(_core.RejectNpcInteraction);
            };
            _greetNpc.Activated += delegate
            {
                RunNpcCommand(_core.BeginNpcInteraction);
            };
            _requestNpcDocuments.Activated += delegate
            {
                RunNpcCommand(_core.RequestNpcDocuments);
            };
            _requestNpcValidation.Activated += delegate
            {
                RunNpcCommand(_core.RequestNpcValidation);
            };
            _arrestNpcCitizen.Activated += delegate
            {
                RunNpcCommand(_core.ArrestNpcCitizen);
            };
            _releaseNpcCitizen.Activated += delegate
            {
                RunNpcCommand(_core.ReleaseNpcCitizen);
            };
            _requestNpcTransportBackup.Activated += delegate
            {
                RunCoreCommand(_core.CallFavoriteBackup);
            };

            npcInteraction.Add(_acceptNpcInteraction);
            npcInteraction.Add(_rejectNpcInteraction);
            npcInteraction.Add(_greetNpc);
            npcInteraction.Add(_requestNpcDocuments);
            npcInteraction.Add(_requestNpcValidation);
            npcInteraction.Add(_arrestNpcCitizen);
            npcInteraction.Add(_releaseNpcCitizen);
            npcInteraction.Add(_requestNpcTransportBackup);
            npcInteraction.Opening += delegate { RefreshNpcInteractionMenu(); };
            RefreshNpcInteractionMenu();
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
                LSPDPoliceAgencyDefinition agency = _core.Profile.FindAgency(choice.AgencyId);
                bool canChangeStation = !_core.Dispatch.HasIncident && !_core.Convoy.Active;
                item.Description = canChangeStation
                    ? agency == null ? access : agency.DisplayName
                    : "Finish the active Dispatch or custody handoff first.";
                item.Enabled = canChangeStation;
                item.Activated += delegate
                {
                    bool selected = _core.SelectStation(choice.Id);
                    if (selected)
                        _stationEntrySuppressedUntilLeaveId = string.Empty;
                    string message = _core.LastOperationMessage;
                    if (!string.IsNullOrWhiteSpace(message))
                        Notification.PostTicker(message, false, false);
                    if (selected)
                        RefreshAllMenus();
                };
                _stationMenu.Add(item);
            }
            AddEmptyRow(_stationMenu, "No Police stations available");
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

        private void RefreshProfileMenu()
        {
            _profileMenu.Clear();
            AddReadOnlyValueRow(
                _profileMenu,
                "Character Model",
                _core.Profile.SelectedPedDisplayName);
            AddReadOnlyValueRow(
                _profileMenu,
                "Vehicle Model",
                _core.Profile.SelectedVehicleDisplayName);
            AddSubMenuEntry(_profileMenu, _weaponMenu, "Weapon Loadout");
            AddSubMenuEntry(
                _profileMenu,
                _citizenBackupPedMenu,
                "Back Up Ped for Citizen Interaction");
            AddSubMenuEntry(
                _profileMenu,
                _dispatchBackupPedMenu,
                "Back Up Ped for Dispatch and Crime Activity");
            AddSubMenuEntry(_profileMenu, _favouriteMenu, "Saved Favorite Lists");
            AddSubMenuEntry(
                _profileMenu,
                _personalWeaponsMenu,
                "Personal Loudout Weapon");
            LSImmersiveMenuFactory.AddAction(
                _profileMenu,
                "Save the Profile Data",
                delegate { RunCoreAction(_core.SavePoliceProfile); });
        }

        private void RefreshWeaponMenu()
        {
            _weaponMenu.Clear();
            AddReadOnlyValueRow(
                _weaponMenu,
                "Preferred Weapon Loadout",
                _core.Profile.SelectedWeaponDisplayName);
            LSImmersiveMenuFactory.AddAction(
                _weaponMenu,
                "Enter Weapon ID or Name",
                PromptForProfileWeapon);
        }

        private void RefreshFavouriteMenus()
        {
            RefreshPersonalPedMenu();
            RefreshPersonalVehicleMenu();
            RefreshPersonalWeaponsMenu();
            RefreshAddPersonalWeaponMenu();
            RefreshSavedPersonalWeaponsMenu();
            RefreshRemovePersonalWeaponMenu();
        }

        private void RefreshPersonalPedMenu()
        {
            _personalPedMenu.Clear();
            AddReadOnlyRow(
                _personalPedMenu,
                "Saved Characters: " + _core.Profile.FavoritePeds.Count());
            LSImmersiveMenuFactory.AddAction(
                _personalPedMenu,
                "Save Current Character as Favorite",
                delegate { RunCoreAction(_core.SetSelectedPedAsFavourite); });
            LSImmersiveMenuFactory.AddAction(
                _personalPedMenu,
                "Enter Addon Ped Model",
                PromptForPersonalPed);
            LSPDPoliceFavoriteModel preferred = _core.Profile.ActiveFavoritePed;
            foreach (LSPDPoliceFavoriteModel definition in _core.Profile.FavoritePeds)
            {
                LSPDPoliceFavoriteModel saved = definition;
                NativeItem item = new NativeItem(
                    "Apply " + saved.DisplayName,
                    FormatFavoriteModelDetails(saved, preferred));
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SetCustomFavouritePed(saved.ModelName); });
                };
                _personalPedMenu.Add(item);
            }
        }

        private void RefreshCitizenBackupPedMenu()
        {
            RefreshContextBackupPedMenu(_citizenBackupPedMenu, true);
        }

        private void RefreshDispatchBackupPedMenu()
        {
            RefreshContextBackupPedMenu(_dispatchBackupPedMenu, false);
        }

        private void RefreshContextBackupPedMenu(NativeMenu menu, bool citizenInteraction)
        {
            menu.Clear();
            LSPDPoliceFavoriteModel active = citizenInteraction
                ? _core.Profile.ActiveFavoriteCitizenBackupPed
                : _core.Profile.ActiveFavoriteDispatchBackupPed;
            AddReadOnlyRow(
                menu,
                "Preferred Backup: " + (active == null ? "Default Police Model" : active.DisplayName));
            AddReadOnlyRow(menu, "Saved Backup Officers: " + _core.Profile.FavoriteBackupPeds.Count());
            LSImmersiveMenuFactory.AddAction(
                menu,
                "Use Selected Police Character",
                delegate
                {
                    if (citizenInteraction)
                        RunCoreAction(_core.SetSelectedPedAsCitizenBackupFavourite);
                    else
                        RunCoreAction(_core.SetSelectedPedAsDispatchBackupFavourite);
                });
            LSImmersiveMenuFactory.AddAction(
                menu,
                "Enter Addon Backup Officer Model",
                delegate { PromptForBackupPed(citizenInteraction); });
            foreach (LSPDPoliceFavoriteModel definition in _core.Profile.FavoriteBackupPeds)
            {
                LSPDPoliceFavoriteModel saved = definition;
                NativeItem item = new NativeItem(
                    "Use " + saved.DisplayName,
                    saved.ModelName + (active != null && active.Id == saved.Id ? " | Preferred" : string.Empty));
                item.Activated += delegate
                {
                    if (citizenInteraction)
                        RunCoreAction(delegate { return _core.SelectCitizenBackupFavourite(saved.Id); });
                    else
                        RunCoreAction(delegate { return _core.SelectDispatchBackupFavourite(saved.Id); });
                };
                menu.Add(item);
            }
            AddEmptyRow(menu, "No personal Backup officers saved", 4);
        }

        private void RefreshPersonalVehicleMenu()
        {
            _personalVehicleMenu.Clear();
            AddReadOnlyRow(
                _personalVehicleMenu,
                "Saved Vehicles: " + _core.Profile.FavoriteVehicles.Count());
            LSImmersiveMenuFactory.AddAction(
                _personalVehicleMenu,
                "Save Current Vehicle as Favorite",
                delegate { RunCoreAction(_core.SetSelectedVehicleAsFavourite); });
            LSImmersiveMenuFactory.AddAction(
                _personalVehicleMenu,
                "Enter Addon Vehicle Model",
                PromptForPersonalVehicle);
            LSPDPoliceFavoriteModel preferred = _core.Profile.ActiveFavoriteVehicle;
            foreach (LSPDPoliceFavoriteModel definition in _core.Profile.FavoriteVehicles)
            {
                LSPDPoliceFavoriteModel saved = definition;
                NativeItem item = new NativeItem(
                    "Spawn " + saved.DisplayName,
                    FormatFavoriteModelDetails(saved, preferred));
                item.Activated += delegate
                {
                    RunCoreAction(delegate { return _core.SetCustomFavouriteVehicle(saved.ModelName); });
                };
                _personalVehicleMenu.Add(item);
            }
        }

        private void RefreshPersonalWeaponsMenu()
        {
            _personalWeaponsMenu.Clear();
            bool hasSavedLoadout = _core.Profile.PersonalWeapons.Any();
            NativeItem applyLoadout = new NativeItem("Apply the Personal Loadout Weapon");
            applyLoadout.Enabled = hasSavedLoadout;
            applyLoadout.Activated += delegate { RunCoreAction(_core.ApplyPersonalLoadout); };
            _personalWeaponsMenu.Add(applyLoadout);

            LSImmersiveMenuFactory.AddAction(
                _personalWeaponsMenu,
                hasSavedLoadout ? "Rewrite the Weapon Loadout" : "Save the Current Loadout",
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
            LSPDPoliceFavoriteWeapon preferred = _core.Profile.SelectedPersonalWeapon;
            foreach (LSPDPoliceFavoriteWeapon definition in _core.Profile.PersonalWeapons)
            {
                LSPDPoliceFavoriteWeapon choice = definition;
                NativeItem item = new NativeItem(
                    choice.DisplayName,
                    FormatFavoriteWeaponDetails(choice, preferred));
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
            string value = ReadCustomIdentifier("Enter the Police vehicle model name.");
            if (string.IsNullOrWhiteSpace(value))
                return;
            RunCoreAction(delegate { return _core.SetCustomFavouriteVehicle(value); });
        }

        private void PromptForProfileWeapon()
        {
            string value = ReadCustomIdentifier("Enter a Police weapon name or ID.");
            if (string.IsNullOrWhiteSpace(value))
                return;
            RunCoreAction(delegate { return _core.SelectPoliceWeapon(value); });
        }

        private void PromptForBackupPed(bool citizenInteraction)
        {
            string value = ReadCustomIdentifier("Enter the addon Backup officer model name.");
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (citizenInteraction)
                RunCoreAction(delegate { return _core.SetCustomCitizenBackupFavourite(value); });
            else
                RunCoreAction(delegate { return _core.SetCustomDispatchBackupFavourite(value); });
        }

        private void PromptForLegacyBackupPed()
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

        private NativeMenu Register(NativeMenu menu)
        {
            _menus.Add(menu);
            return menu;
        }

        private static void AddSubMenuEntry(
            NativeMenu parent,
            NativeMenu submenu,
            string title)
        {
            NativeSubmenuItem item = new NativeSubmenuItem(submenu, parent);
            item.Title = title;
            parent.Add(item);
        }

        private static void AddReadOnlyRow(NativeMenu menu, string text)
        {
            NativeItem item = new NativeItem(text);
            item.Enabled = false;
            menu.Add(item);
        }

        private static void AddReadOnlyValueRow(NativeMenu menu, string title, string value)
        {
            NativeItem item = new NativeItem(
                title,
                string.IsNullOrWhiteSpace(value) ? "Not selected" : value);
            item.Enabled = false;
            menu.Add(item);
        }

        private static string FormatFavoriteModelDetails(
            LSPDPoliceFavoriteModel favorite,
            LSPDPoliceFavoriteModel preferred)
        {
            List<string> details = new List<string>();
            if (!string.IsNullOrWhiteSpace(favorite.ModelType))
                details.Add(favorite.ModelType);
            if (!string.IsNullOrWhiteSpace(favorite.Source))
                details.Add(favorite.Source);
            if (!string.IsNullOrWhiteSpace(favorite.ModelName))
                details.Add(favorite.ModelName);
            if (preferred != null && preferred.Id == favorite.Id)
                details.Add("Preferred");
            return string.Join(" | ", details);
        }

        private static string FormatFavoriteWeaponDetails(
            LSPDPoliceFavoriteWeapon favorite,
            LSPDPoliceFavoriteWeapon preferred)
        {
            List<string> details = new List<string>();
            if (!string.IsNullOrWhiteSpace(favorite.WeaponType))
                details.Add(favorite.WeaponType);
            if (!string.IsNullOrWhiteSpace(favorite.Source))
                details.Add(favorite.Source);
            if (!string.IsNullOrWhiteSpace(favorite.WeaponName))
                details.Add(favorite.WeaponName);
            if (preferred != null && preferred.Id == favorite.Id)
                details.Add("Preferred");
            return string.Join(" | ", details);
        }

        private static NativeItem AddUnavailableChoice(NativeMenu menu, string title)
        {
            NativeItem item = new NativeItem(title);
            item.Enabled = false;
            menu.Add(item);
            return item;
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
            {
                _station.CloseAlmanac();
                _station.CancelStationPreview();
                _stationSetupSessionActive = false;
                _stationEntrySuppressedUntilLeaveId = string.Empty;
                foreach (NativeMenu menu in _menus)
                    menu.Visible = false;
            }
        }

        internal void Process(bool paused, bool allowStationSetupEntry)
        {
            bool setupWasComplete = _core.Profile.StationSetupComplete;
            bool setupBlocksGameplay = !_core.Profile.StationSetupComplete || _stationSetupSessionActive;
            ProcessResponseFrameworkHotkeys(paused, setupBlocksGameplay, allowStationSetupEntry);
            bool menuVisible = _menus.Any(menu => menu != null && menu.Visible);
            _core.Process(paused, !menuVisible && !setupBlocksGameplay && allowStationSetupEntry);
            UpdateInitialStationSetup(paused, allowStationSetupEntry);
            if (!setupWasComplete && _core.Profile.StationSetupComplete
                && _core.IsPoliceAuthorityActive)
            {
                if (Menu.Parent != null)
                    Menu.Parent.Visible = false;
                Menu.Visible = true;
            }
            menuVisible = _menus.Any(menu => menu != null && menu.Visible);
            RefreshNpcInteractionMenu();
            LSPDNPCRecord citizenRecord = _core.NpcResponse.CurrentRecord;
            bool citizenRecordVisible = _core.IsPoliceAuthorityActive
                && _core.NpcResponse.CitizenRecordVisible
                && citizenRecord != null;
            bool citizenFrameworkVisible = _citizenFrameworkMenu != null
                && _citizenFrameworkMenu.Visible;
            _station.DrawPoliceAlmanac(
                _core,
                paused || menuVisible || citizenRecordVisible);
            _citizenRecordPanel.Draw(
                citizenRecordVisible ? citizenRecord : null,
                _core.NpcResponse.ActiveSubject,
                _core.NpcResponse.ActiveSubjectVehicle,
                _core.NpcResponse.StatusText,
                paused || !citizenFrameworkVisible);
            // An incident can arrive while Dispatch is already visible. Refresh
            // only changed availability, without rebuilding menus or reattaching
            // handlers on each tick, so controls cannot remain stale until reopen.
            if (_refreshOffer != null) _refreshOffer();
            RefreshConvoyMenu();
        }

        private void ProcessResponseFrameworkHotkeys(
            bool paused,
            bool setupBlocksGameplay,
            bool allowStationSetupEntry)
        {
            LSPDControlBindings controls = _core.PoliceControls;
            if (controls == null)
                return;

            bool backupDown = Game.IsKeyPressed(controls.EmergencyKey);
            bool dispatchDown = Game.IsKeyPressed(controls.DispatchMenuKey);
            bool citizenDown = Game.IsKeyPressed(controls.InteractionKey);
            bool backDown = Game.IsKeyPressed(Keys.Back)
                || Game.IsKeyPressed(Keys.Escape)
                || Game.IsControlJustPressed(GTA.Control.FrontendCancel);
            bool backPressed = backDown && !_responseFrameworkBackKeyDown;
            bool backupPressed = backupDown && !_backupFrameworkKeyDown;
            bool dispatchPressed = dispatchDown && !_dispatchFrameworkKeyDown;
            bool citizenPressed = citizenDown && !_citizenFrameworkKeyDown;
            _responseFrameworkBackKeyDown = backDown;
            _backupFrameworkKeyDown = backupDown;
            _dispatchFrameworkKeyDown = dispatchDown;
            _citizenFrameworkKeyDown = citizenDown;

            if (_shortcutOpenedResponseFrameworkMenu != null)
            {
                if (!_shortcutOpenedResponseFrameworkMenu.Visible)
                {
                    _shortcutOpenedResponseFrameworkMenu = null;
                }
                else if (backPressed)
                {
                    NativeMenu shortcutMenu = _shortcutOpenedResponseFrameworkMenu;
                    shortcutMenu.Visible = false;
                    if (shortcutMenu.Parent != null && shortcutMenu.Parent.Visible)
                        shortcutMenu.Parent.Visible = false;
                    _shortcutOpenedResponseFrameworkMenu = null;
                    ClearPendingResponseFramework();
                    return;
                }
            }

            if (paused || setupBlocksGameplay || !_core.IsPoliceAuthorityActive)
            {
                ClearPendingResponseFramework();
                return;
            }

            // B is also the documented LemonUI Back control. Open the shortcut
            // only from live gameplay, and wait for the key release so the new
            // menu does not consume the same press as a Back command.
            bool policeMenuVisible = _menus.Any(menu => menu != null && menu.Visible);
            if (!allowStationSetupEntry || policeMenuVisible)
            {
                ClearPendingResponseFramework();
                return;
            }

            if (_pendingResponseFrameworkMenu != null)
            {
                if (Game.IsKeyPressed(_pendingResponseFrameworkKey))
                    return;

                NativeMenu menu = _pendingResponseFrameworkMenu;
                string title = _pendingResponseFrameworkTitle;
                ClearPendingResponseFramework();
                OpenResponseFramework(menu, title);
                return;
            }

            if (backupPressed)
            {
                _pendingResponseFrameworkMenu = _backupResponseMenu;
                _pendingResponseFrameworkKey = controls.EmergencyKey;
                _pendingResponseFrameworkTitle = "Back Up Response";
            }
            else if (dispatchPressed)
            {
                _pendingResponseFrameworkMenu = _policeResponseDispatchMenu;
                _pendingResponseFrameworkKey = controls.DispatchMenuKey;
                _pendingResponseFrameworkTitle = "Dispatch Activity";
            }
            else if (citizenPressed)
            {
                _pendingResponseFrameworkMenu = _citizenFrameworkMenu;
                _pendingResponseFrameworkKey = controls.InteractionKey;
                _pendingResponseFrameworkTitle = "Citizen Ped Response";
            }
        }

        private void ClearPendingResponseFramework()
        {
            _pendingResponseFrameworkMenu = null;
            _pendingResponseFrameworkKey = Keys.None;
            _pendingResponseFrameworkTitle = string.Empty;
        }

        private void OpenResponseFramework(NativeMenu menu, string title)
        {
            if (menu == null)
                return;

            foreach (NativeMenu openMenu in _menus)
                openMenu.Visible = false;
            if (menu == _citizenFrameworkMenu
                && _core.NpcResponse.CanReviewBackupCustodyOffer)
                RunNpcCommand(_core.ReviewNpcFramework);
            menu.Visible = true;
            _shortcutOpenedResponseFrameworkMenu = menu;
            Notification.PostTicker(title + " opened.", false, false);
        }

        internal void Shutdown()
        {
            _station.CloseAlmanac();
            _station.CancelStationPreview();
            _citizenRecordPanel.Release();
            _stationSetupSessionActive = false;
            _stationSetupFinalizing = false;
            _core.Dispose();
            foreach (NativeMenu menu in _menus)
                menu.Visible = false;
        }

        private void UpdateInitialStationSetup(bool paused, bool allowStationEntry)
        {
            bool confirmKeyDown = Game.IsKeyPressed(Keys.Y);
            bool confirmKeyPressed = confirmKeyDown && !_stationSetupConfirmKeyDown;
            _stationSetupConfirmKeyDown = confirmKeyDown;

            bool setupMenuVisible = _stationSetupMenu != null && _stationSetupMenu.Visible
                || _stationGarageMenu != null && _stationGarageMenu.Visible;
            if (setupMenuVisible && _stationSetupMenu != null)
                _stationSetupMenu.BannerText.Position = _stationSetupBannerTitlePosition;

            LSPDStation.StationPreviewMode mode = _stationGarageMenu != null && _stationGarageMenu.Visible
                ? LSPDStation.StationPreviewMode.Garage
                : LSPDStation.StationPreviewMode.Wardrobe;

            if (!_core.IsPoliceAuthorityActive)
            {
                if (_station.StationPreviewActive)
                    _station.CancelStationPreview();
                _stationSetupSessionActive = false;
                _stationSetupFinalizing = false;
                return;
            }

            if (_stationSetupSessionActive)
            {
                if (setupMenuVisible)
                {
                    if (!paused && confirmKeyPressed && HasPendingStationPreviewSelection())
                        ConfirmStationPreviewSelection(mode);
                    _station.UpdateStationPreview(
                        paused,
                        mode,
                        _core.Profile.SelectedPedModelName,
                        _core.Profile.SelectedVehicleModelName);
                    return;
                }

                if (HasPendingStationPreviewSelection())
                {
                    bool reopenGarage = !string.IsNullOrWhiteSpace(_pendingStationSetupVehicleId);
                    if (reopenGarage)
                    {
                        _stationGarageMenu.Visible = true;
                        PostStationPreviewConfirmationPrompt(true);
                    }
                    else
                    {
                        _stationSetupMenu.Visible = true;
                        PostStationPreviewConfirmationPrompt(false);
                    }
                    _station.UpdateStationPreview(
                        paused,
                        reopenGarage ? LSPDStation.StationPreviewMode.Garage : LSPDStation.StationPreviewMode.Wardrobe,
                        _core.Profile.SelectedPedModelName,
                        _core.Profile.SelectedVehicleModelName);
                    return;
                }

                _stationSetupSessionActive = false;
                _stationSetupFinalizing = true;
                _stationEntrySuppressedUntilLeaveId = _core.Profile.Selection.StationId;
                _station.EndStationPreview();
                return;
            }

            if (_stationSetupFinalizing)
            {
                if (_station.StationPreviewActive)
                {
                    _station.UpdateStationPreview(
                        paused,
                        mode,
                        _core.Profile.SelectedPedModelName,
                        _core.Profile.SelectedVehicleModelName);
                    return;
                }

                _stationSetupFinalizing = false;
                bool completed = _core.CompleteInitialStationSetup();
                if (!string.IsNullOrWhiteSpace(_core.LastOperationMessage))
                    Notification.PostTicker(_core.LastOperationMessage, false, false);
                if (!completed)
                    RefreshStationSetupSelections();
                return;
            }

            if (_station.StationPreviewActive)
            {
                _station.UpdateStationPreview(
                    paused,
                    mode,
                    _core.Profile.SelectedPedModelName,
                    _core.Profile.SelectedVehicleModelName);
            }

            if (paused || !allowStationEntry)
                return;

            // The introduction marker is only used for first setup or after
            // the Player chooses a different Station in the Police UI.
            if (_core.Profile.StationSetupComplete)
            {
                _stationEntrySuppressedUntilLeaveId = string.Empty;
                return;
            }

            bool otherMenuVisible = _menus.Any(menu => menu != null && menu.Visible
                && menu != _stationSetupMenu
                && menu != _stationGarageMenu);
            if (otherMenuVisible)
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            if (!string.IsNullOrWhiteSpace(_stationEntrySuppressedUntilLeaveId))
            {
                LSPDPoliceStationDefinition suppressedStation = _core.Profile.FindStation(
                    _stationEntrySuppressedUntilLeaveId);
                LSPDPoliceLocationDefinition suppressedLocation = suppressedStation == null
                    ? null : _core.Profile.FindLocation(suppressedStation.LocationId);
                if (suppressedLocation != null
                    && suppressedLocation.ExteriorSafe
                    && player.Position.DistanceTo(new GTA.Math.Vector3(
                        suppressedLocation.X,
                        suppressedLocation.Y,
                        suppressedLocation.Z)) <= 4.5f)
                    return;
                _stationEntrySuppressedUntilLeaveId = string.Empty;
            }

            LSPDPoliceStationDefinition station = _core.Profile.FindStation(
                _core.Profile.Selection.StationId);
            LSPDPoliceLocationDefinition location = station == null
                ? null : _core.Profile.FindLocation(station.LocationId);
            if (location == null || !location.ExteriorSafe
                || !_station.DrawStationEntry(station, location))
                return;

            _stationEntrySuppressedUntilLeaveId = station.Id;
            if (!_core.SelectStationSetupStation(station.Id))
            {
                if (!string.IsNullOrWhiteSpace(_core.LastOperationMessage))
                    Notification.PostTicker(_core.LastOperationMessage, false, false);
                return;
            }

            RefreshStationSetupSelections();
            _stationSetupSessionActive = true;
            _stationSetupMenu.Visible = true;
            _station.BeginStationPreview(
                LSPDStation.StationPreviewMode.Wardrobe,
                _core.Profile.SelectedPedModelName,
                _core.Profile.SelectedVehicleModelName);
        }

        /// <summary>
        /// Draws the selected citizen database record in the lower-left field
        /// area. This is a view only; LSNPCDatabase and LSPDNPCResponse remain
        /// responsible for the record and interaction state.
        /// </summary>
        private sealed class LSPDCitizenRecordPanel
        {
            private const float Left = 0.035f;
            private const float Top = 0.46f;
            private const float Width = 0.45f;
            private const float Height = 0.35f;

            private readonly ScaledRectangle _background;
            private readonly ScaledRectangle _accent;
            private readonly ScaledRectangle _identityBackground;
            private readonly ScaledRectangle _recordBackground;
            private readonly ScaledRectangle _responseBackground;
            private readonly ScaledRectangle _portraitBackground;
            private readonly ScaledRectangle _portraitFrame;
            private readonly ScaledRectangle _resultBackground;
            private readonly ScaledText _title;
            private readonly ScaledText _titleRight;
            private readonly ScaledText _subtitle;
            private readonly ScaledText _identityHeader;
            private readonly ScaledText _recordHeader;
            private readonly ScaledText _responseHeader;
            private readonly ScaledText _result;
            private readonly ScaledText _portraitFallback;
            private readonly ScaledText _identityDetails;
            private readonly ScaledText _recordDetails;
            private readonly ScaledText _responseDetails;
            private readonly ScaledText _contactState;
            private readonly ScaledText _documentCheck;
            private int _headshotHandle;
            private int _headshotPedHandle;
            private int _headshotModelHash;
            private DateTime _headshotRetryAtUtc;

            internal LSPDCitizenRecordPanel()
            {
                _background = Rectangle(Left, Top, Width, Height,
                    Color.FromArgb(224, 12, 22, 34));
                _accent = Rectangle(Left, Top, Width, 0.004f,
                    Color.FromArgb(255, 48, 150, 190));
                _identityBackground = Section(Left + 0.012f, Top + 0.076f,
                    0.207f, 0.186f);
                _recordBackground = Section(Left + 0.231f, Top + 0.076f,
                    0.207f, 0.186f);
                _responseBackground = Section(Left + 0.012f, Top + 0.267f,
                    Width - 0.024f, 0.056f);
                _portraitBackground = Rectangle(Left + 0.020f, Top + 0.114f,
                    0.044f, 0.108f, Color.FromArgb(255, 16, 65, 92));
                _portraitFrame = Rectangle(Left + 0.017f, Top + 0.111f,
                    0.050f, 0.114f, Color.FromArgb(255, 48, 150, 190));
                _resultBackground = Rectangle(Left + Width - 0.112f,
                    Top + 0.047f, 0.094f, 0.027f, Color.FromArgb(255, 48, 150, 190));

                _title = Text(Left + 0.018f, Top + 0.010f,
                    "CITIZEN CONTACT", 0.42f, Color.White);
                _titleRight = RightText(Left + Width - 0.020f, Top + 0.012f,
                    "POLICE ALMANAC", 0.35f, Color.White);
                _subtitle = Text(Left + 0.018f, Top + 0.047f,
                    "ACTIVE FIELD RECORD", 0.24f,
                    Color.FromArgb(255, 88, 191, 227));
                _result = Text(Left + Width - 0.106f, Top + 0.051f,
                    "REVIEW", 0.18f, Color.White);
                _identityHeader = Text(Left + 0.021f, Top + 0.083f,
                    "CITIZEN RECORD", 0.27f, Color.FromArgb(255, 88, 191, 227));
                _recordHeader = Text(Left + 0.241f, Top + 0.083f,
                    "DOCUMENT & STATUS", 0.27f, Color.FromArgb(255, 88, 191, 227));
                _responseHeader = Text(Left + 0.020f, Top + 0.272f,
                    "POLICE RESPONSE", 0.25f,
                    Color.FromArgb(255, 88, 191, 227));
                _portraitFallback = Text(Left + 0.033f, Top + 0.148f,
                    "C", 0.32f, Color.White);
                _identityDetails = Text(Left + 0.074f, Top + 0.116f,
                    string.Empty, 0.245f, Color.White);
                _recordDetails = Text(Left + 0.241f, Top + 0.116f,
                    string.Empty, 0.235f, Color.White);
                _responseDetails = Text(Left + 0.020f, Top + 0.299f,
                    string.Empty, 0.235f, Color.White);
                _contactState = Text(Left + 0.020f, Top + 0.328f,
                    string.Empty, 0.19f, Color.FromArgb(255, 190, 218, 230));
                _documentCheck = RightText(Left + Width - 0.020f, Top + 0.328f,
                    string.Empty, 0.19f, Color.FromArgb(255, 190, 218, 230));

                _identityDetails.WordWrap = ScaledWidth(0.145f);
                _recordDetails.WordWrap = ScaledWidth(0.190f);
                _responseDetails.WordWrap = ScaledWidth(Width - 0.044f);
            }

            internal void Draw(
                LSPDNPCRecord record,
                Ped subject,
                Vehicle subjectVehicle,
                string liveStatus,
                bool hidden)
            {
                if (record == null || hidden)
                {
                    Release();
                    return;
                }

                string resultText = record.WarrantActive || record.RequiresCustody
                    ? "DETENTION"
                    : string.Equals(record.StatusFamily, "clean", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(record.DocumentStatus, "current", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(record.DocumentStatus, "valid", StringComparison.OrdinalIgnoreCase))
                        ? "CLEAR"
                        : "REVIEW";
                Color resultColor = record.WarrantActive || record.RequiresCustody
                    ? Color.FromArgb(255, 163, 48, 55)
                    : string.Equals(resultText, "CLEAR", StringComparison.Ordinal)
                        ? Color.FromArgb(255, 45, 130, 91)
                        : Color.FromArgb(255, 168, 119, 43);
                _resultBackground.Color = resultColor;
                _result.Text = resultText;

                _identityDetails.Text =
                    "NAME: " + Clean(record.FullName) + "\n"
                    + "ID: " + Clean(record.CitizenId) + "\n"
                    + "GENDER / AGE: " + Clean(record.Gender)
                    + " / " + Clean(record.AgeRange) + "\n"
                    + "WORK: " + Clean(record.Occupation) + "\n"
                    + "AREA: " + Clean(record.Region);
                _recordDetails.Text =
                    "DOCUMENT: " + Clean(record.DocumentName) + "\n"
                    + "CONDITION: " + Clean(record.DocumentStatus) + "\n"
                    + "STATUS: " + Clean(record.StatusDescription) + "\n"
                    + "WARRANT: " + Clean(record.WarrantActive ? record.WarrantLabel : "None");
                if (IsUsable(subjectVehicle))
                {
                    _recordDetails.Text += "\nPLATE: " + Clean(ReadPlate(subjectVehicle))
                        + "\nVEHICLE: " + Clean(subjectVehicle.DisplayName)
                        + "\nCOLOR: " + Clean(ReadPrimaryColor(subjectVehicle));
                }
                else
                {
                    _recordDetails.Text += "\nCONTACT: " + Clean(record.ContactReason);
                }
                _responseDetails.Text = Wrap(record.RecommendedResponse, 74, 2);
                _contactState.Text = "CONTACT STATE: " + Compact(liveStatus, 26);
                _documentCheck.Text = "DOCUMENT CHECK: " + Compact(record.DocumentStatus, 14);
                _portraitFallback.Text = Initial(record.FullName);

                _background.Draw();
                _accent.Draw();
                _identityBackground.Draw();
                _recordBackground.Draw();
                _responseBackground.Draw();
                _resultBackground.Draw();
                _portraitFrame.Draw();
                _portraitBackground.Draw();
                if (!TryDrawHeadshot(subject))
                    _portraitFallback.Draw();
                _title.Draw();
                _titleRight.Draw();
                _subtitle.Draw();
                _result.Draw();
                _identityHeader.Draw();
                _recordHeader.Draw();
                _responseHeader.Draw();
                _identityDetails.Draw();
                _recordDetails.Draw();
                _responseDetails.Draw();
                _contactState.Draw();
                _documentCheck.Draw();
            }

            internal void Release()
            {
                if (_headshotHandle != 0)
                {
                    try { Function.Call(Hash.UNREGISTER_PEDHEADSHOT, _headshotHandle); }
                    catch { }
                }
                _headshotHandle = 0;
                _headshotPedHandle = 0;
                _headshotModelHash = 0;
                _headshotRetryAtUtc = DateTime.MinValue;
            }

            private bool TryDrawHeadshot(Ped ped)
            {
                if (!IsUsable(ped))
                {
                    Release();
                    return false;
                }

                int modelHash = ped.Model.Hash;
                if (_headshotHandle != 0
                    && (ped.Handle != _headshotPedHandle || modelHash != _headshotModelHash))
                    Release();

                if (_headshotHandle == 0)
                {
                    if (DateTime.UtcNow < _headshotRetryAtUtc)
                        return false;
                    try
                    {
                        _headshotHandle = Function.Call<int>(Hash.REGISTER_PEDHEADSHOT, ped.Handle);
                        if (_headshotHandle == 0)
                        {
                            _headshotRetryAtUtc = DateTime.UtcNow.AddSeconds(2);
                            return false;
                        }
                        _headshotPedHandle = ped.Handle;
                        _headshotModelHash = modelHash;
                    }
                    catch
                    {
                        _headshotRetryAtUtc = DateTime.UtcNow.AddSeconds(2);
                        return false;
                    }
                }

                try
                {
                    if (!Function.Call<bool>(Hash.IS_PEDHEADSHOT_VALID, _headshotHandle))
                    {
                        Release();
                        _headshotRetryAtUtc = DateTime.UtcNow.AddSeconds(2);
                        return false;
                    }
                    if (!Function.Call<bool>(Hash.IS_PEDHEADSHOT_READY, _headshotHandle))
                        return false;

                    string textureDictionary = Function.Call<string>(
                        Hash.GET_PEDHEADSHOT_TXD_STRING, _headshotHandle);
                    if (string.IsNullOrWhiteSpace(textureDictionary))
                        return false;

                    Function.Call(Hash.DRAW_SPRITE, textureDictionary, textureDictionary,
                        Left + 0.0465f, Top + 0.1635f,
                        0.049f, 0.104f, 0.0f, 255, 255, 255, 255);
                    return true;
                }
                catch { return false; }
            }

            private static ScaledRectangle Section(
                float x, float y, float width, float height)
            {
                return Rectangle(x, y, width, height,
                    Color.FromArgb(175, 9, 20, 34));
            }

            private static ScaledRectangle Rectangle(
                float x, float y, float width, float height, Color color)
            {
                var rectangle = new ScaledRectangle(
                    ScaledPoint(x, y), ScaledSize(width, height));
                rectangle.Color = color;
                return rectangle;
            }

            private static ScaledText Text(
                float x, float y, string value, float scale, Color color)
            {
                var text = new ScaledText(
                    ScaledPoint(x, y), value, scale, GTA.UI.Font.ChaletLondon);
                text.Color = color;
                text.Shadow = true;
                return text;
            }

            private static ScaledText RightText(
                float x, float y, string value, float scale, Color color)
            {
                ScaledText text = Text(x, y, value, scale, color);
                text.Alignment = GTA.UI.Alignment.Right;
                return text;
            }

            private static PointF ScaledPoint(float x, float y)
            {
                return new PointF(x, y).ToScaled();
            }

            private static SizeF ScaledSize(float width, float height)
            {
                return new SizeF(width, height).ToScaled();
            }

            private static float ScaledWidth(float width)
            {
                return width.ToXScaled();
            }

            private static bool IsUsable(Ped ped)
            {
                try { return ped != null && ped.Exists(); }
                catch { return false; }
            }

            private static bool IsUsable(Vehicle vehicle)
            {
                try { return vehicle != null && vehicle.Exists(); }
                catch { return false; }
            }

            private static string ReadPlate(Vehicle vehicle)
            {
                try
                {
                    string plate = Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, vehicle);
                    return string.IsNullOrWhiteSpace(plate) ? "Unavailable" : plate.Trim();
                }
                catch { return "Unavailable"; }
            }

            private static string ReadPrimaryColor(Vehicle vehicle)
            {
                try
                {
                    if (vehicle.Mods.IsPrimaryColorCustom)
                    {
                        Color color = vehicle.Mods.CustomPrimaryColor;
                        return string.Format(CultureInfo.InvariantCulture,
                            "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
                    }
                    return vehicle.Mods.PrimaryColor.ToString();
                }
                catch { return "Unavailable"; }
            }

            private static string Initial(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return "C";
                string name = value.Trim();
                return name.Substring(0, 1).ToUpperInvariant();
            }

            private static string Compact(string value, int maximumLength)
            {
                string text = Clean(value);
                return text.Length <= maximumLength
                    ? text
                    : text.Substring(0, maximumLength - 1) + "…";
            }

            private static string Clean(string value)
            {
                return string.IsNullOrWhiteSpace(value)
                    ? "Unavailable"
                    : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            }

            private static string Wrap(string value, int maximumLineLength, int maximumLines)
            {
                string text = string.IsNullOrWhiteSpace(value)
                    ? "No response guidance is available."
                    : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
                string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var lines = new List<string>();
                string line = string.Empty;
                foreach (string word in words)
                {
                    string candidate = string.IsNullOrEmpty(line) ? word : line + " " + word;
                    if (candidate.Length > maximumLineLength && !string.IsNullOrEmpty(line))
                    {
                        lines.Add(line);
                        if (lines.Count >= maximumLines)
                            return string.Join("\n", lines.ToArray()) + "…";
                        line = word;
                    }
                    else
                    {
                        line = candidate;
                    }
                }
                if (!string.IsNullOrEmpty(line) && lines.Count < maximumLines)
                    lines.Add(line);
                return string.Join("\n", lines.ToArray());
            }
        }
    }
}
