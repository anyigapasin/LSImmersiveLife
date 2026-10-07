using System;
using System.Collections.Generic;
using System.Globalization;
using GTA;
using GTA.UI;
using LemonUI.Menus;

namespace LSImmersiveLife
{
    /// <summary>
    /// In-game editor for the typed universal and Police behaviour sections in
    /// LSImmersiveMainUI.xml. This menu changes no gameplay state itself.
    /// Owners apply the settings they support from LSImmersiveMainConfig.
    /// </summary>
    internal sealed class LSMainSetting
    {
        private readonly LSImmersiveLog _log;
        private readonly List<Action> _numericSettingRefreshers = new List<Action>();
        private bool _hasUnsavedChanges;

        internal NativeMenu Menu { get; private set; }
        internal LSImmersiveMainConfig Configuration { get; private set; }
        internal string ConfigurationPath { get; private set; }
        internal bool HasUnsavedChanges { get { return _hasUnsavedChanges; } }

        internal LSMainSetting(
            LSImmersiveMainConfig configuration,
            string configurationPath,
            LSImmersiveLog log = null)
        {
            Configuration = configuration ?? throw new ArgumentNullException("configuration");
            ConfigurationPath = configurationPath;
            _log = log;
            Menu = LSImmersiveMenuFactory.Create("Settings", string.Empty);
            Menu.NoItemsText = string.Empty;
            Menu.Opening += delegate { RefreshNumericSettingItems(); };
            BuildMenu();
        }

        private void BuildMenu()
        {
            BuildUniversalSettings();
            BuildPoliceSettings();

            NativeItem save = new NativeItem("Save Settings", "Write all supported settings to LSImmersiveMainUI.xml.");
            save.Activated += delegate { TrySave(); };
            Menu.Add(save);
        }

        private void BuildUniversalSettings()
        {
            AddHeader("Universal Interface");
            AddCheck("Show Waypoint", "Show supported route guidance.", Configuration.Ui.ShowWaypoint, delegate(bool value) { Configuration.Ui.ShowWaypoint = value; });
            AddCheck("Show Vehicle Meter", "Show supported vehicle information.", Configuration.Ui.ShowVehicleMeter, delegate(bool value) { Configuration.Ui.ShowVehicleMeter = value; });

            AddHeader("Universal Audio");
            AddCheck("Enable Audio", "Enable Dispatch and Police status audio.", Configuration.Audio.Enabled, delegate(bool value) { Configuration.SetAudio(value, Configuration.Audio.Volume); });
            AddNumber("Audio Volume", "0 to 100 percent.", 0, 100, delegate { return Configuration.Audio.Volume; }, delegate(int value) { Configuration.SetAudio(Configuration.Audio.Enabled, value); });

            AddHeader("Environment");
            AddCheck("Ambient Traffic", "Allow the environment owner to manage ambient traffic.", Configuration.Environment.AmbientTrafficEnabled, delegate(bool value) { Configuration.Environment.AmbientTrafficEnabled = value; });
            AddCheck("Ambient Pedestrians", "Allow the environment owner to manage ambient pedestrians.", Configuration.Environment.AmbientPedestriansEnabled, delegate(bool value) { Configuration.Environment.AmbientPedestriansEnabled = value; });
            AddCheck("Dynamic Weather", "Allow the environment owner to use dynamic weather.", Configuration.Environment.DynamicWeatherEnabled, delegate(bool value) { Configuration.Environment.DynamicWeatherEnabled = value; });
            AddSlider("Traffic Density", "0 to 200 percent.", 200, Configuration.Environment.TrafficDensityPercent, delegate(int value) { Configuration.Environment.TrafficDensityPercent = value; });
            AddSlider("Pedestrian Density", "0 to 200 percent.", 200, Configuration.Environment.PedestrianDensityPercent, delegate(int value) { Configuration.Environment.PedestrianDensityPercent = value; });

            AddHeader("Logging");
            AddCheck("Runtime Log", "Record normal gameplay actions in LSRuntime.log.", Configuration.Logging.RuntimeEnabled, delegate(bool value) { Configuration.Logging.RuntimeEnabled = value; });
            AddCheck("Debug Log", "Record faults and unexpected behaviour in LSDebug.log.", Configuration.Logging.DebugEnabled, delegate(bool value) { Configuration.Logging.DebugEnabled = value; });
            AddCheck("Verbose Diagnostics", "Include extra diagnostic detail when supported.", Configuration.Logging.VerboseDiagnostics, delegate(bool value) { Configuration.Logging.VerboseDiagnostics = value; });
            AddCheck("Session Headers", "Record a readable header for each game session.", Configuration.Logging.SessionHeaders, delegate(bool value) { Configuration.Logging.SessionHeaders = value; });

            AddHeader("Performance");
            AddSlider("Managed Entity Budget", "16 to 256 entities.", 240, Configuration.Performance.MaxManagedEntities - 16, delegate(int value) { Configuration.Performance.MaxManagedEntities = value + 16; });
            AddSlider("Cleanup Interval", "5 to 600 seconds.", 595, Configuration.Performance.CleanupIntervalSeconds - 5, delegate(int value) { Configuration.Performance.CleanupIntervalSeconds = value + 5; });
            AddSlider("Heavy Scan Interval", "50 to 5000 milliseconds.", 4950, Configuration.Performance.HeavyScanIntervalMilliseconds - 50, delegate(int value) { Configuration.Performance.HeavyScanIntervalMilliseconds = value + 50; });
            AddCheck("Throttle Heavy Scans", "Let supported systems spread expensive scans over time.", Configuration.Performance.ThrottleHeavyScans, delegate(bool value) { Configuration.Performance.ThrottleHeavyScans = value; });
        }

        private void BuildPoliceSettings()
        {
            BuildPoliceAuthority();
            BuildPolicePatrol();
            BuildPoliceDispatch();
            BuildPoliceCrimeActivity();
            BuildPoliceNpcResponse();
            BuildPoliceTraffic();
            BuildPoliceResponse();
            BuildPoliceBackup();
            BuildPoliceConvoy();
            BuildPoliceCleanup();
        }

        private void BuildPoliceAuthority()
        {
            AddHeader("Police Authority");
            AddCheck("Protect From Vanilla Police", "Player-scoped Police escalation protection.", Configuration.PoliceAuthoritySettings.ProtectPlayerFromVanillaPoliceEscalation, delegate(bool value) { Configuration.PoliceAuthoritySettings.ProtectPlayerFromVanillaPoliceEscalation = value; });
            AddCheck("Suppress Wanted Escalation", "Temporarily cap wanted level at zero while Police Authority is active.", Configuration.PoliceAuthoritySettings.SuppressVanillaWantedEscalation, delegate(bool value) { Configuration.PoliceAuthoritySettings.SuppressVanillaWantedEscalation = value; });
            AddCheck("Suppress Ambient Police Hostility", "Clear only nearby Police hostility directed at the active officer.", Configuration.PoliceAuthoritySettings.SuppressAmbientPoliceHostility, delegate(bool value) { Configuration.PoliceAuthoritySettings.SuppressAmbientPoliceHostility = value; });
            AddCheck("Disable Vanilla Police Dispatch", "Stop GTA from dispatching Police against the active officer.", Configuration.PoliceAuthoritySettings.SuppressVanillaDispatch, delegate(bool value) { Configuration.PoliceAuthoritySettings.SuppressVanillaDispatch = value; });
            AddCheck("Prevent Military Base Hostility", "Let nearby Army/base personnel de-escalate only when targeting the officer.", Configuration.PoliceAuthoritySettings.SuppressMilitaryHostility, delegate(bool value) { Configuration.PoliceAuthoritySettings.SuppressMilitaryHostility = value; });
            AddNumber("Authority Refresh", "Authority control refresh interval in milliseconds.", 250, 5000, delegate { return Configuration.PoliceAuthoritySettings.AuthorityControlRefreshMilliseconds; }, delegate(int value) { Configuration.PoliceAuthoritySettings.AuthorityControlRefreshMilliseconds = value; });
            AddCheck("Gang Reaction Control", "Allow supported gang reaction control.", Configuration.PoliceAuthoritySettings.EnableGangReactionControl, delegate(bool value) { Configuration.PoliceAuthoritySettings.EnableGangReactionControl = value; });
            AddCheck("Civilian Reaction Control", "Allow supported civilian reaction control.", Configuration.PoliceAuthoritySettings.EnableCivilianReactionControl, delegate(bool value) { Configuration.PoliceAuthoritySettings.EnableCivilianReactionControl = value; });
            AddCheck("World Behaviour Changes", "Allow the Authority owner to apply approved player-scoped world behavior changes.", Configuration.PoliceAuthoritySettings.EnableWorldBehaviorChanges, delegate(bool value) { Configuration.PoliceAuthoritySettings.EnableWorldBehaviorChanges = value; });
            AddCheck("Verified Interiors Only", "Use only verified Police interiors.", Configuration.PoliceAuthoritySettings.UseVerifiedInteriorsOnly, delegate(bool value) { Configuration.PoliceAuthoritySettings.UseVerifiedInteriorsOnly = value; });
        }

        private void BuildPolicePatrol()
        {
            AddHeader("Police Patrol");
            AddCheck("Patrol Enabled", "Allow the Police patrol owner to operate.", Configuration.Police.Patrol.Enabled, delegate(bool value) { Configuration.Police.Patrol.Enabled = value; });
            AddCheck("Reuse Current Police Vehicle", "Prefer the current qualified Police vehicle.", Configuration.Police.Patrol.ReuseCurrentPoliceVehicle, delegate(bool value) { Configuration.Police.Patrol.ReuseCurrentPoliceVehicle = value; });
            AddCheck("Prevent Duplicate Personal Vehicle", "Avoid creating duplicate personal Police vehicles.", Configuration.Police.Patrol.PreventDuplicatePersonalVehicle, delegate(bool value) { Configuration.Police.Patrol.PreventDuplicatePersonalVehicle = value; });
        }

        private void BuildPoliceDispatch()
        {
            AddHeader("Police Dispatch");
            AddCheck("Dispatch Enabled", "Allow Dispatch offers during patrol.", Configuration.Police.Dispatch.Enabled, delegate(bool value) { Configuration.Police.Dispatch.Enabled = value; });
            AddNumber("Minimum Quiet Patrol", "Minimum delay between eligible automatic Dispatch offers, in seconds.", 30, 1800, delegate { return Configuration.Police.Dispatch.MinimumQuietPatrolSeconds; }, delegate(int value) { Configuration.Police.Dispatch.MinimumQuietPatrolSeconds = value; });
            AddNumber("Maximum Quiet Patrol", "Maximum delay between eligible automatic Dispatch offers, in seconds.", 30, 3600, delegate { return Configuration.Police.Dispatch.MaximumQuietPatrolSeconds; }, delegate(int value) { Configuration.Police.Dispatch.MaximumQuietPatrolSeconds = value; });
            AddNumber("Repeat Protection", "Minutes before the same authored Dispatch definition may repeat.", 0, 240, delegate { return Configuration.Police.Dispatch.RepeatProtectionMinutes; }, delegate(int value) { Configuration.Police.Dispatch.RepeatProtectionMinutes = value; });
            AddReadOnly("Active Incident Limit", "One active Dispatch incident by design.");
            AddNumber("Audio Minimum Gap", "Minimum spacing between Dispatch audio responses, in seconds.", 0, 60, delegate { return Configuration.Police.Dispatch.AudioMinimumGapSeconds; }, delegate(int value) { Configuration.Police.Dispatch.AudioMinimumGapSeconds = value; });
            AddNumber("Context Clear Quiet", "Quiet interval after a Gang or NPC contact ends, in seconds.", 0, 300, delegate { return Configuration.Police.Dispatch.ContextClearQuietSeconds; }, delegate(int value) { Configuration.Police.Dispatch.ContextClearQuietSeconds = value; });
        }

        private void BuildPoliceCrimeActivity()
        {
            AddHeader("Police Crime Activity");
            AddCheck("Crime Activity Enabled", "Allow quiet, player-led criminal activity during Police patrol.", Configuration.Police.CrimeActivity.Enabled, delegate(bool value) { Configuration.Police.CrimeActivity.Enabled = value; });
            AddCheck("Discover Nearby Activity", "Prepare nearby Crime Activity intelligence without creating a Dispatch call.", Configuration.Police.CrimeActivity.AutoDiscover, delegate(bool value) { Configuration.Police.CrimeActivity.AutoDiscover = value; });
            AddCheck("Nearby Crime Tips", "Show one quiet warning when an available Crime Activity is nearby.", Configuration.Police.CrimeActivity.NearbyTipNotifications, delegate(bool value) { Configuration.Police.CrimeActivity.NearbyTipNotifications = value; });
            AddSlider("Crime Intelligence Scan", "2 to 120 seconds between quiet nearby checks.", 118, Configuration.Police.CrimeActivity.DiscoveryScanSeconds - 2, delegate(int value) { Configuration.Police.CrimeActivity.DiscoveryScanSeconds = value + 2; });
            AddSlider("Crime Activity Range", "100 to 600 metres from the officer.", 500, Configuration.Police.CrimeActivity.MaximumDeploymentDistance - 100, delegate(int value) { Configuration.Police.CrimeActivity.MaximumDeploymentDistance = value + 100; });
            AddSlider("Crime Tip Distance", "25 to 250 metres before the quiet nearby warning.", 225, Configuration.Police.CrimeActivity.NearbyTipDistance - 25, delegate(int value) { Configuration.Police.CrimeActivity.NearbyTipDistance = value + 25; });
            AddSlider("Crime Scene Activation", "40 to 250 metres before an investigation scene can be created.", 210, Configuration.Police.CrimeActivity.SceneActivationDistance - 40, delegate(int value) { Configuration.Police.CrimeActivity.SceneActivationDistance = value + 40; });
            AddSlider("Crime Scene Participants", "2 to 6 owned criminal participants.", 4, Configuration.Police.CrimeActivity.ParticipantCount - 2, delegate(int value) { Configuration.Police.CrimeActivity.ParticipantCount = value + 2; });
            AddSlider("Same Location Cooldown", "60 to 7200 seconds before the same area can be selected again.", 7140, Configuration.Police.CrimeActivity.SameLocationCooldownSeconds - 60, delegate(int value) { Configuration.Police.CrimeActivity.SameLocationCooldownSeconds = value + 60; });
            AddSlider("New Activity Cooldown", "15 to 1800 seconds before another Crime Activity is prepared.", 1785, Configuration.Police.CrimeActivity.RedeploymentCooldownSeconds - 15, delegate(int value) { Configuration.Police.CrimeActivity.RedeploymentCooldownSeconds = value + 15; });
            AddSlider("Crime Scene Abandon Range", "150 to 1000 metres before an untouched scene can close.", 850, Configuration.Police.CrimeActivity.AbandonDistance - 150, delegate(int value) { Configuration.Police.CrimeActivity.AbandonDistance = value + 150; });
            AddSlider("Crime Asset Preparation", "5 to 60 seconds for scene models to stream.", 55, Configuration.Police.CrimeActivity.AssetPreparationTimeoutSeconds - 5, delegate(int value) { Configuration.Police.CrimeActivity.AssetPreparationTimeoutSeconds = value + 5; });
        }

        private void BuildPoliceNpcResponse()
        {
            AddHeader("Police NPC Response");
            AddCheck("NPC Response Enabled", "Allow the NPC Response owner to operate.", Configuration.Police.NpcResponse.Enabled, delegate(bool value) { Configuration.Police.NpcResponse.Enabled = value; });
            AddCheck("NPC Interactions", "Allow player-led NPC interactions.", Configuration.Police.NpcResponse.InteractionsEnabled, delegate(bool value) { Configuration.Police.NpcResponse.InteractionsEnabled = value; });
            AddNumber("Interaction Radius", "Distance for a nearby pedestrian interaction, in metres.", 1, 25, delegate { return Configuration.Police.NpcResponse.InteractionRadius; }, delegate(int value) { Configuration.Police.NpcResponse.InteractionRadius = value; });
            AddNumber("Traffic Awareness Radius", "Distance for nearby traffic awareness, in metres.", 1, 120, delegate { return Configuration.Police.NpcResponse.TrafficAwarenessRadius; }, delegate(int value) { Configuration.Police.NpcResponse.TrafficAwarenessRadius = value; });
            AddNumber("Collision Guard Cooldown", "Time between collision guard responses, in milliseconds.", 0, 10000, delegate { return Configuration.Police.NpcResponse.CollisionGuardCooldownMilliseconds; }, delegate(int value) { Configuration.Police.NpcResponse.CollisionGuardCooldownMilliseconds = value; });
            AddNumber("Affected Traffic Vehicles", "Maximum traffic vehicles managed by a contact.", 0, 20, delegate { return Configuration.Police.NpcResponse.MaximumAffectedTrafficVehicles; }, delegate(int value) { Configuration.Police.NpcResponse.MaximumAffectedTrafficVehicles = value; });
            AddNumber("Interaction Timeout", "Time before an abandoned contact closes, in seconds.", 20, 300, delegate { return Configuration.Police.NpcResponse.InteractionTimeoutSeconds; }, delegate(int value) { Configuration.Police.NpcResponse.InteractionTimeoutSeconds = value; });
            AddNumber("Document Gesture Time", "Time for the visible identification gesture, in seconds.", 1, 8, delegate { return Configuration.Police.NpcResponse.DocumentPresentationSeconds; }, delegate(int value) { Configuration.Police.NpcResponse.DocumentPresentationSeconds = value; });
            AddNumber("Citizen Flee Chance", "Chance after a negative officer decision, in percent.", 0, 100, delegate { return Configuration.Police.NpcResponse.CitizenFleeChancePercent; }, delegate(int value) { Configuration.Police.NpcResponse.CitizenFleeChancePercent = value; });
            AddNumber("Citizen Resistance Chance", "Chance of resistance after a negative foot-citizen decision, in percent.", 0, 100, delegate { return Configuration.Police.NpcResponse.CitizenResistChancePercent; }, delegate(int value) { Configuration.Police.NpcResponse.CitizenResistChancePercent = value; });
            AddNumber("Suspicious Citizen Chance", "Chance for a nearby foot citizen to show suspicious approach behaviour, in percent.", 0, 100, delegate { return Configuration.Police.NpcResponse.SuspiciousEncounterChancePercent; }, delegate(int value) { Configuration.Police.NpcResponse.SuspiciousEncounterChancePercent = value; });
            AddNumber("Compliant Kneeling Chance", "Chance for a compliant citizen to use the kneeling surrender pose, in percent.", 0, 100, delegate { return Configuration.Police.NpcResponse.CompliantKneelChancePercent; }, delegate(int value) { Configuration.Police.NpcResponse.CompliantKneelChancePercent = value; });
            AddNumber("Dead Citizen Release Distance", "Distance before a resolved dead citizen is released to natural world cleanup, in metres.", 40, 250, delegate { return Configuration.Police.NpcResponse.DeadSubjectReleaseDistance; }, delegate(int value) { Configuration.Police.NpcResponse.DeadSubjectReleaseDistance = value; });
            AddNumber("Driver Flee Chance", "Chance after a negative traffic-stop decision, in percent.", 0, 100, delegate { return Configuration.Police.NpcResponse.TrafficFleeChancePercent; }, delegate(int value) { Configuration.Police.NpcResponse.TrafficFleeChancePercent = value; });
            AddNumber("Pull Over Timeout", "Time for the selected driver to reach the roadside, in seconds.", 5, 45, delegate { return Configuration.Police.NpcResponse.PullOverTimeoutSeconds; }, delegate(int value) { Configuration.Police.NpcResponse.PullOverTimeoutSeconds = value; });
            AddNumber("Traffic Task Recovery", "Time before a stalled driver task is retried, in seconds.", 4, 30, delegate { return Configuration.Police.NpcResponse.TrafficTaskRecoverySeconds; }, delegate(int value) { Configuration.Police.NpcResponse.TrafficTaskRecoverySeconds = value; });
            AddNumber("Scene Ped Reaction Radius", "Radius around an active Police scene, in metres.", 5, 60, delegate { return Configuration.Police.NpcResponse.ScenePedReactionRadius; }, delegate(int value) { Configuration.Police.NpcResponse.ScenePedReactionRadius = value; });
            AddNumber("Affected Scene Pedestrians", "Maximum nearby civilians managed per reaction scan.", 0, 12, delegate { return Configuration.Police.NpcResponse.MaximumAffectedPedestrians; }, delegate(int value) { Configuration.Police.NpcResponse.MaximumAffectedPedestrians = value; });
            AddNumber("Scene Reaction Cooldown", "Time before the same civilian can be retasked, in seconds.", 5, 120, delegate { return Configuration.Police.NpcResponse.SceneReactionCooldownSeconds; }, delegate(int value) { Configuration.Police.NpcResponse.SceneReactionCooldownSeconds = value; });
        }

        private void BuildPoliceTraffic()
        {
            AddHeader("Police Traffic");
            AddCheck("Traffic Control Enabled", "Allow supported scene traffic control.", Configuration.Police.Traffic.Enabled, delegate(bool value) { Configuration.Police.Traffic.Enabled = value; });
            AddCheck("Stop Traffic at Active Scenes", "Hold nearby traffic at active scenes when supported.", Configuration.Police.Traffic.StopTrafficAtActiveScenes, delegate(bool value) { Configuration.Police.Traffic.StopTrafficAtActiveScenes = value; });
            AddNumber("Scene Control Radius", "Traffic control radius, in metres.", 5, 150, delegate { return Configuration.Police.Traffic.SceneControlRadius; }, delegate(int value) { Configuration.Police.Traffic.SceneControlRadius = value; });
        }

        private void BuildPoliceResponse()
        {
            AddHeader("Police Response Units");
            AddCheck("Response Units Enabled", "Allow supported response units.", Configuration.Police.Response.Enabled, delegate(bool value) { Configuration.Police.Response.Enabled = value; });
            AddCheck("Allow Backup", "Allow player-requested backup.", Configuration.Police.Response.AllowBackup, delegate(bool value) { Configuration.Police.Response.AllowBackup = value; });
            AddCheck("Automatic Pursuit Support", "Allow automatic support when a supported pursuit is active.", Configuration.Police.Response.AutomaticPursuitSupport, delegate(bool value) { Configuration.Police.Response.AutomaticPursuitSupport = value; });
            AddNumber("Maximum Units Per Incident", "Maximum ordinary Police response units.", 0, 5, delegate { return Configuration.Police.Response.MaximumUnitsPerIncident; }, delegate(int value) { Configuration.Police.Response.MaximumUnitsPerIncident = value; });
            AddCheck("Nearby Officer Support", "Let nearby Police or military personnel acknowledge and de-escalate toward Police Anyi.", Configuration.Police.Response.AuthoritySupportEnabled, delegate(bool value) { Configuration.Police.Response.AuthoritySupportEnabled = value; });
            AddCheck("Officer Acknowledgement", "Allow idle nearby allied officers to briefly recognize the player.", Configuration.Police.Response.AuthorityGreetingEnabled, delegate(bool value) { Configuration.Police.Response.AuthorityGreetingEnabled = value; });
            AddNumber("Officer Support Radius", "Nearby officer support radius, in metres.", 10, 120, delegate { return Configuration.Police.Response.AuthoritySupportRadius; }, delegate(int value) { Configuration.Police.Response.AuthoritySupportRadius = value; });
            AddNumber("Officer Support Scan", "Time between nearby officer scans, in milliseconds.", 250, 10000, delegate { return Configuration.Police.Response.AuthoritySupportScanMilliseconds; }, delegate(int value) { Configuration.Police.Response.AuthoritySupportScanMilliseconds = value; });
            AddNumber("Nearby Officer Limit", "Maximum nearby officers managed per scan.", 0, 12, delegate { return Configuration.Police.Response.MaximumAuthoritySupportPeds; }, delegate(int value) { Configuration.Police.Response.MaximumAuthoritySupportPeds = value; });
        }

        private void BuildPoliceConvoy()
        {
            AddHeader("Police Convoy and Custody");
            AddCheck("Convoy Enabled", "Allow prisoner custody and Convoy flows.", Configuration.Police.Convoy.Enabled, delegate(bool value) { Configuration.Police.Convoy.Enabled = value; });
            AddCheck("Terminal Completion", "Allow a confirmed terminal custody completion.", Configuration.Police.Convoy.TerminalCompletionEnabled, delegate(bool value) { Configuration.Police.Convoy.TerminalCompletionEnabled = value; });
            AddCheck("Player Convoy Request", "Allow a separate player-requested prisoner Convoy activity.", Configuration.Police.Convoy.RequestedConvoyEnabled, delegate(bool value) { Configuration.Police.Convoy.RequestedConvoyEnabled = value; });
            AddCheck("Requested Convoy Route Threat", "Allow a staged hostile road threat during the separate Convoy activity.", Configuration.Police.Convoy.RequestedConvoyRouteThreatEnabled, delegate(bool value) { Configuration.Police.Convoy.RequestedConvoyRouteThreatEnabled = value; });
            AddNumber("Station Arrival Radius", "Custody station arrival radius, in metres.", 4, 100, delegate { return Configuration.Police.Convoy.StationArrivalRadius; }, delegate(int value) { Configuration.Police.Convoy.StationArrivalRadius = value; });
            AddNumber("Prison Arrival Radius", "Prison transfer arrival radius, in metres.", 4, 100, delegate { return Configuration.Police.Convoy.PrisonArrivalRadius; }, delegate(int value) { Configuration.Police.Convoy.PrisonArrivalRadius = value; });
            AddNumber("Prisoner Recovery Timeout", "Timeout for recovering a custody actor, in seconds.", 5, 600, delegate { return Configuration.Police.Convoy.PrisonerRecoveryTimeoutSeconds; }, delegate(int value) { Configuration.Police.Convoy.PrisonerRecoveryTimeoutSeconds = value; });
            AddNumber("Convoy Staging Distance", "Transport staging distance from the officer, in metres.", 35, 250, delegate { return Configuration.Police.Convoy.MinimumStagingDistance; }, delegate(int value) { Configuration.Police.Convoy.MinimumStagingDistance = value; });
            AddNumber("Route Threat Staging Distance", "Distance ahead of transport for a requested Convoy road threat, in metres.", 75, 400, delegate { return Configuration.Police.Convoy.RequestedRouteThreatDistance; }, delegate(int value) { Configuration.Police.Convoy.RequestedRouteThreatDistance = value; });
            AddNumber("Route Threat Members", "Number of hostile members in a requested Convoy road threat.", 1, 3, delegate { return Configuration.Police.Convoy.RequestedRouteThreatCount; }, delegate(int value) { Configuration.Police.Convoy.RequestedRouteThreatCount = value; });
        }

        private void BuildPoliceBackup()
        {
            AddHeader("Police Backup Force");
            AddCheck("Backup Force Enabled", "Allow player-requested support units to stage and travel to an active Police scene.", Configuration.Police.Backup.Enabled, delegate(bool value) { Configuration.Police.Backup.Enabled = value; });
            AddCheck("Automatic Group Support", "Automatically request backup for a group Dispatch and let arriving officers help contain it.", Configuration.Police.Backup.AutomaticGroupSupport, delegate(bool value) { Configuration.Police.Backup.AutomaticGroupSupport = value; });
            AddCheck("Use Saved Backup Favorite", "Use the selected saved backup officer favorite when one is available.", Configuration.Police.Backup.PreferSavedFavoritePed, delegate(bool value) { Configuration.Police.Backup.PreferSavedFavoritePed = value; });
            AddNumber("Backup Units Per Request", "Number of Police vehicles in a support request.", 1, 4, delegate { return Configuration.Police.Backup.UnitsPerRequest; }, delegate(int value) { Configuration.Police.Backup.UnitsPerRequest = value; });
            AddNumber("Officers Per Backup Unit", "Number of officers assigned to each support vehicle.", 1, 2, delegate { return Configuration.Police.Backup.OfficersPerUnit; }, delegate(int value) { Configuration.Police.Backup.OfficersPerUnit = value; });
            AddNumber("Backup Staging Distance", "Distance from the player when the station is too close, in metres.", 35, 250, delegate { return Configuration.Police.Backup.MinimumStagingDistance; }, delegate(int value) { Configuration.Police.Backup.MinimumStagingDistance = value; });
            AddNumber("Backup Arrival Radius", "Distance from the active situation for unit arrival, in metres.", 8, 60, delegate { return Configuration.Police.Backup.ArrivalRadius; }, delegate(int value) { Configuration.Police.Backup.ArrivalRadius = value; });
            AddNumber("Fleeing Suspect Intercept Lead", "Distance ahead of a fleeing suspect, in metres.", 25, 300, delegate { return Configuration.Police.Backup.InterceptionLeadDistance; }, delegate(int value) { Configuration.Police.Backup.InterceptionLeadDistance = value; });
            AddNumber("Backup Stand Down", "Delay before a finished support force is cleaned up, in seconds.", 5, 300, delegate { return Configuration.Police.Backup.StandDownSeconds; }, delegate(int value) { Configuration.Police.Backup.StandDownSeconds = value; });
        }

        private void BuildPoliceCleanup()
        {
            AddHeader("Police Cleanup");
            AddNumber("Completed Scene Grace", "Grace period before a completed scene is eligible for cleanup, in seconds.", 0, 3600, delegate { return Configuration.Police.Cleanup.CompletedSceneGraceSeconds; }, delegate(int value) { Configuration.Police.Cleanup.CompletedSceneGraceSeconds = value; });
            AddNumber("Hard Cleanup", "Maximum time before an owned Police scene is eligible for cleanup, in seconds.", 30, 7200, delegate { return Configuration.Police.Cleanup.HardCleanupSeconds; }, delegate(int value) { Configuration.Police.Cleanup.HardCleanupSeconds = value; });
        }

        private void AddHeader(string title)
        {
            NativeItem header = new NativeItem(title);
            header.Enabled = false;
            Menu.Add(header);
        }

        private void AddReadOnly(string title, string description)
        {
            NativeItem item = new NativeItem(title, description);
            item.Enabled = false;
            Menu.Add(item);
        }

        private void AddCheck(string title, string description, bool value, Action<bool> changed)
        {
            NativeCheckboxItem item = new NativeCheckboxItem(title, description, value);
            item.CheckboxChanged += delegate
            {
                changed(item.Checked);
                Changed(title, item.Checked ? "Enabled" : "Disabled");
            };
            Menu.Add(item);
        }

        private void AddNumber(
            string title,
            string description,
            int minimum,
            int maximum,
            Func<int> current,
            Action<int> changed)
        {
            NativeItem item = new NativeItem(title, description + " Select to type a value from "
                + minimum.ToString(CultureInfo.InvariantCulture) + " to "
                + maximum.ToString(CultureInfo.InvariantCulture) + ".");
            Action refresh = delegate
            {
                item.Title = title + " (" + current().ToString(CultureInfo.InvariantCulture) + ")";
            };
            _numericSettingRefreshers.Add(refresh);
            refresh();

            item.Activated += delegate
            {
                string input;
                try
                {
                    input = Game.GetUserInput(
                        WindowTitle.EnterMessage60,
                        current().ToString(CultureInfo.InvariantCulture),
                        10);
                }
                catch (Exception ex)
                {
                    if (_log != null)
                        _log.Exception("SETTINGS_NUMERIC_INPUT_FAILED", ex);
                    Notification.PostTicker(title + " could not be edited.", false, false);
                    return;
                }

                int value;
                if (string.IsNullOrWhiteSpace(input))
                    return;
                if (!int.TryParse(
                    input.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value)
                    || value < minimum
                    || value > maximum)
                {
                    Notification.PostTicker(
                        title + " must be a whole number from "
                        + minimum.ToString(CultureInfo.InvariantCulture) + " to "
                        + maximum.ToString(CultureInfo.InvariantCulture) + ".",
                        false,
                        false);
                    if (_log != null)
                        _log.Debug("SETTINGS_NUMERIC_INPUT_REJECTED",
                            title + " = " + (input ?? string.Empty));
                    return;
                }

                changed(value);
                Configuration.NormalizeSettings();
                int effectiveValue = current();
                Changed(title, effectiveValue.ToString(CultureInfo.InvariantCulture));
                RefreshNumericSettingItems();
                Notification.PostTicker(
                    title + " set to " + effectiveValue.ToString(CultureInfo.InvariantCulture) + ". Save Settings to keep it.",
                    false,
                    false);
            };
            Menu.Add(item);
        }

        private void RefreshNumericSettingItems()
        {
            foreach (Action refresh in _numericSettingRefreshers)
                refresh();
        }

        private void AddSlider(string title, string description, int maximum, int value, Action<int> changed)
        {
            NativeSliderItem item = new NativeSliderItem(title, description, maximum, Math.Max(0, Math.Min(maximum, value)));
            item.ValueChanged += delegate
            {
                changed(item.Value);
                Changed(title, item.Value.ToString());
            };
            Menu.Add(item);
        }

        private void Changed(string setting, string value)
        {
            Configuration.NormalizeSettings();
            Configuration.NotifySettingsChanged();
            _hasUnsavedChanges = true;
            if (_log != null) _log.Runtime("SETTINGS_CHANGED", setting + " = " + value + ".");
        }

        internal void SaveSupportedValues()
        {
            Configuration.Save(ConfigurationPath);
        }

        private void TrySave()
        {
            try
            {
                SaveSupportedValues();
                _hasUnsavedChanges = false;
                if (_log != null) _log.Runtime("SETTINGS_SAVED", "Universal settings saved to LSImmersiveMainUI.xml.");
                Notification.PostTicker("LS Immersive settings saved.", false, false);
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Exception("SETTINGS_SAVE_FAILED", ex);
                Notification.PostTicker("LS Immersive settings could not be saved.", false, false);
            }
        }
    }
}
