using System;
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
            AddSlider("Audio Volume", "0 to 100 percent.", 100, Configuration.Audio.Volume, delegate(int value) { Configuration.SetAudio(Configuration.Audio.Enabled, value); });

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
            AddSlider("Authority Refresh", "250 to 5000 milliseconds.", 4750, Configuration.PoliceAuthoritySettings.AuthorityControlRefreshMilliseconds - 250, delegate(int value) { Configuration.PoliceAuthoritySettings.AuthorityControlRefreshMilliseconds = value + 250; });
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
            AddSlider("Minimum Quiet Patrol", "30 to 1800 seconds.", 1770, Configuration.Police.Dispatch.MinimumQuietPatrolSeconds - 30, delegate(int value) { Configuration.Police.Dispatch.MinimumQuietPatrolSeconds = value + 30; });
            AddSlider("Maximum Quiet Patrol", "30 to 3600 seconds.", 3570, Configuration.Police.Dispatch.MaximumQuietPatrolSeconds - 30, delegate(int value) { Configuration.Police.Dispatch.MaximumQuietPatrolSeconds = value + 30; });
            AddSlider("Repeat Protection", "0 to 240 minutes.", 240, Configuration.Police.Dispatch.RepeatProtectionMinutes, delegate(int value) { Configuration.Police.Dispatch.RepeatProtectionMinutes = value; });
            AddReadOnly("Active Incident Limit", "One active Dispatch incident by design.");
            AddSlider("Audio Minimum Gap", "0 to 60 seconds.", 60, Configuration.Police.Dispatch.AudioMinimumGapSeconds, delegate(int value) { Configuration.Police.Dispatch.AudioMinimumGapSeconds = value; });
            AddSlider("Context Clear Quiet", "0 to 300 seconds after a Gang or NPC contact ends.", 300, Configuration.Police.Dispatch.ContextClearQuietSeconds, delegate(int value) { Configuration.Police.Dispatch.ContextClearQuietSeconds = value; });
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
            AddSlider("Interaction Radius", "1 to 25 metres.", 24, Configuration.Police.NpcResponse.InteractionRadius - 1, delegate(int value) { Configuration.Police.NpcResponse.InteractionRadius = value + 1; });
            AddSlider("Traffic Awareness Radius", "1 to 120 metres.", 119, Configuration.Police.NpcResponse.TrafficAwarenessRadius - 1, delegate(int value) { Configuration.Police.NpcResponse.TrafficAwarenessRadius = value + 1; });
            AddSlider("Collision Guard Cooldown", "0 to 10000 milliseconds.", 10000, Configuration.Police.NpcResponse.CollisionGuardCooldownMilliseconds, delegate(int value) { Configuration.Police.NpcResponse.CollisionGuardCooldownMilliseconds = value; });
            AddSlider("Affected Traffic Vehicles", "0 to 20 vehicles.", 20, Configuration.Police.NpcResponse.MaximumAffectedTrafficVehicles, delegate(int value) { Configuration.Police.NpcResponse.MaximumAffectedTrafficVehicles = value; });
            AddSlider("Interaction Timeout", "20 to 300 seconds before an abandoned contact closes.", 280, Configuration.Police.NpcResponse.InteractionTimeoutSeconds - 20, delegate(int value) { Configuration.Police.NpcResponse.InteractionTimeoutSeconds = value + 20; });
            AddSlider("Document Gesture Time", "1 to 8 seconds for the visible identification gesture.", 7, Configuration.Police.NpcResponse.DocumentPresentationSeconds - 1, delegate(int value) { Configuration.Police.NpcResponse.DocumentPresentationSeconds = value + 1; });
            AddSlider("Citizen Flee Chance", "0 to 100 percent after a negative officer decision.", 100, Configuration.Police.NpcResponse.CitizenFleeChancePercent, delegate(int value) { Configuration.Police.NpcResponse.CitizenFleeChancePercent = value; });
            AddSlider("Citizen Resistance Chance", "0 to 100 percent after a negative foot-citizen decision.", 100, Configuration.Police.NpcResponse.CitizenResistChancePercent, delegate(int value) { Configuration.Police.NpcResponse.CitizenResistChancePercent = value; });
            AddSlider("Suspicious Citizen Chance", "0 to 100 percent for a nearby foot citizen to show suspicious approach behavior.", 100, Configuration.Police.NpcResponse.SuspiciousEncounterChancePercent, delegate(int value) { Configuration.Police.NpcResponse.SuspiciousEncounterChancePercent = value; });
            AddSlider("Compliant Kneeling Chance", "0 to 100 percent for a compliant citizen to use the kneeling surrender pose.", 100, Configuration.Police.NpcResponse.CompliantKneelChancePercent, delegate(int value) { Configuration.Police.NpcResponse.CompliantKneelChancePercent = value; });
            AddSlider("Dead Citizen Release Distance", "40 to 250 metres before a resolved dead citizen is released to natural world cleanup.", 210, Configuration.Police.NpcResponse.DeadSubjectReleaseDistance - 40, delegate(int value) { Configuration.Police.NpcResponse.DeadSubjectReleaseDistance = value + 40; });
            AddSlider("Driver Flee Chance", "0 to 100 percent after a negative traffic-stop decision.", 100, Configuration.Police.NpcResponse.TrafficFleeChancePercent, delegate(int value) { Configuration.Police.NpcResponse.TrafficFleeChancePercent = value; });
            AddSlider("Pull Over Timeout", "5 to 45 seconds for the selected driver to reach the roadside.", 40, Configuration.Police.NpcResponse.PullOverTimeoutSeconds - 5, delegate(int value) { Configuration.Police.NpcResponse.PullOverTimeoutSeconds = value + 5; });
            AddSlider("Traffic Task Recovery", "4 to 30 seconds before one truly stalled driver task is retried.", 26, Configuration.Police.NpcResponse.TrafficTaskRecoverySeconds - 4, delegate(int value) { Configuration.Police.NpcResponse.TrafficTaskRecoverySeconds = value + 4; });
            AddSlider("Scene Ped Reaction Radius", "5 to 60 metres around a real active Police scene.", 55, Configuration.Police.NpcResponse.ScenePedReactionRadius - 5, delegate(int value) { Configuration.Police.NpcResponse.ScenePedReactionRadius = value + 5; });
            AddSlider("Affected Scene Pedestrians", "0 to 12 nearby civilians per reaction scan.", 12, Configuration.Police.NpcResponse.MaximumAffectedPedestrians, delegate(int value) { Configuration.Police.NpcResponse.MaximumAffectedPedestrians = value; });
            AddSlider("Scene Reaction Cooldown", "5 to 120 seconds before the same civilian can be retasked.", 115, Configuration.Police.NpcResponse.SceneReactionCooldownSeconds - 5, delegate(int value) { Configuration.Police.NpcResponse.SceneReactionCooldownSeconds = value + 5; });
        }

        private void BuildPoliceTraffic()
        {
            AddHeader("Police Traffic");
            AddCheck("Traffic Control Enabled", "Allow supported scene traffic control.", Configuration.Police.Traffic.Enabled, delegate(bool value) { Configuration.Police.Traffic.Enabled = value; });
            AddCheck("Stop Traffic at Active Scenes", "Hold nearby traffic at active scenes when supported.", Configuration.Police.Traffic.StopTrafficAtActiveScenes, delegate(bool value) { Configuration.Police.Traffic.StopTrafficAtActiveScenes = value; });
            AddSlider("Scene Control Radius", "5 to 150 metres.", 145, Configuration.Police.Traffic.SceneControlRadius - 5, delegate(int value) { Configuration.Police.Traffic.SceneControlRadius = value + 5; });
        }

        private void BuildPoliceResponse()
        {
            AddHeader("Police Response Units");
            AddCheck("Response Units Enabled", "Allow supported response units.", Configuration.Police.Response.Enabled, delegate(bool value) { Configuration.Police.Response.Enabled = value; });
            AddCheck("Allow Backup", "Allow player-requested backup.", Configuration.Police.Response.AllowBackup, delegate(bool value) { Configuration.Police.Response.AllowBackup = value; });
            AddCheck("Automatic Pursuit Support", "Allow automatic support when a supported pursuit is active.", Configuration.Police.Response.AutomaticPursuitSupport, delegate(bool value) { Configuration.Police.Response.AutomaticPursuitSupport = value; });
            AddSlider("Maximum Units Per Incident", "0 to 5 units.", 5, Configuration.Police.Response.MaximumUnitsPerIncident, delegate(int value) { Configuration.Police.Response.MaximumUnitsPerIncident = value; });
            AddCheck("Nearby Officer Support", "Let nearby Police or military personnel acknowledge and de-escalate toward Police Anyi.", Configuration.Police.Response.AuthoritySupportEnabled, delegate(bool value) { Configuration.Police.Response.AuthoritySupportEnabled = value; });
            AddCheck("Officer Acknowledgement", "Allow idle nearby allied officers to briefly recognize the player.", Configuration.Police.Response.AuthorityGreetingEnabled, delegate(bool value) { Configuration.Police.Response.AuthorityGreetingEnabled = value; });
            AddSlider("Officer Support Radius", "10 to 120 metres.", 110, Configuration.Police.Response.AuthoritySupportRadius - 10, delegate(int value) { Configuration.Police.Response.AuthoritySupportRadius = value + 10; });
            AddSlider("Officer Support Scan", "250 to 10000 milliseconds.", 9750, Configuration.Police.Response.AuthoritySupportScanMilliseconds - 250, delegate(int value) { Configuration.Police.Response.AuthoritySupportScanMilliseconds = value + 250; });
            AddSlider("Nearby Officer Limit", "0 to 12 peds per scan.", 12, Configuration.Police.Response.MaximumAuthoritySupportPeds, delegate(int value) { Configuration.Police.Response.MaximumAuthoritySupportPeds = value; });
        }

        private void BuildPoliceConvoy()
        {
            AddHeader("Police Convoy and Custody");
            AddCheck("Convoy Enabled", "Allow prisoner custody and Convoy flows.", Configuration.Police.Convoy.Enabled, delegate(bool value) { Configuration.Police.Convoy.Enabled = value; });
            AddCheck("Terminal Completion", "Allow a confirmed terminal custody completion.", Configuration.Police.Convoy.TerminalCompletionEnabled, delegate(bool value) { Configuration.Police.Convoy.TerminalCompletionEnabled = value; });
            AddCheck("Player Convoy Request", "Allow a separate player-requested prisoner Convoy activity.", Configuration.Police.Convoy.RequestedConvoyEnabled, delegate(bool value) { Configuration.Police.Convoy.RequestedConvoyEnabled = value; });
            AddCheck("Requested Convoy Route Threat", "Allow a staged hostile road threat during the separate Convoy activity.", Configuration.Police.Convoy.RequestedConvoyRouteThreatEnabled, delegate(bool value) { Configuration.Police.Convoy.RequestedConvoyRouteThreatEnabled = value; });
            AddSlider("Station Arrival Radius", "4 to 100 metres.", 96, Configuration.Police.Convoy.StationArrivalRadius - 4, delegate(int value) { Configuration.Police.Convoy.StationArrivalRadius = value + 4; });
            AddSlider("Prison Arrival Radius", "4 to 100 metres.", 96, Configuration.Police.Convoy.PrisonArrivalRadius - 4, delegate(int value) { Configuration.Police.Convoy.PrisonArrivalRadius = value + 4; });
            AddSlider("Prisoner Recovery Timeout", "5 to 600 seconds.", 595, Configuration.Police.Convoy.PrisonerRecoveryTimeoutSeconds - 5, delegate(int value) { Configuration.Police.Convoy.PrisonerRecoveryTimeoutSeconds = value + 5; });
            AddSlider("Convoy Staging Distance", "35 to 250 metres from the officer.", 215, Configuration.Police.Convoy.MinimumStagingDistance - 35, delegate(int value) { Configuration.Police.Convoy.MinimumStagingDistance = value + 35; });
            AddSlider("Route Threat Staging Distance", "75 to 400 metres ahead of the transport.", 325, Configuration.Police.Convoy.RequestedRouteThreatDistance - 75, delegate(int value) { Configuration.Police.Convoy.RequestedRouteThreatDistance = value + 75; });
            AddSlider("Route Threat Members", "1 to 3 hostile road members.", 2, Configuration.Police.Convoy.RequestedRouteThreatCount - 1, delegate(int value) { Configuration.Police.Convoy.RequestedRouteThreatCount = value + 1; });
        }

        private void BuildPoliceBackup()
        {
            AddHeader("Police Backup Force");
            AddCheck("Backup Force Enabled", "Allow player-requested support units to stage and travel to an active Police scene.", Configuration.Police.Backup.Enabled, delegate(bool value) { Configuration.Police.Backup.Enabled = value; });
            AddCheck("Automatic Group Support", "Automatically request backup for a group Dispatch and let arriving officers help contain it.", Configuration.Police.Backup.AutomaticGroupSupport, delegate(bool value) { Configuration.Police.Backup.AutomaticGroupSupport = value; });
            AddCheck("Use Saved Backup Favorite", "Use the selected saved backup officer favorite when one is available.", Configuration.Police.Backup.PreferSavedFavoritePed, delegate(bool value) { Configuration.Police.Backup.PreferSavedFavoritePed = value; });
            AddSlider("Backup Units Per Request", "1 to 4 Police vehicles.", 3, Configuration.Police.Backup.UnitsPerRequest - 1, delegate(int value) { Configuration.Police.Backup.UnitsPerRequest = value + 1; });
            AddSlider("Officers Per Backup Unit", "1 or 2 officers per vehicle.", 1, Configuration.Police.Backup.OfficersPerUnit - 1, delegate(int value) { Configuration.Police.Backup.OfficersPerUnit = value + 1; });
            AddSlider("Backup Staging Distance", "35 to 250 metres from the player when the station is too close.", 215, Configuration.Police.Backup.MinimumStagingDistance - 35, delegate(int value) { Configuration.Police.Backup.MinimumStagingDistance = value + 35; });
            AddSlider("Backup Arrival Radius", "8 to 60 metres from the active situation.", 52, Configuration.Police.Backup.ArrivalRadius - 8, delegate(int value) { Configuration.Police.Backup.ArrivalRadius = value + 8; });
            AddSlider("Fleeing Suspect Intercept Lead", "25 to 300 metres ahead of a fleeing suspect.", 275, Configuration.Police.Backup.InterceptionLeadDistance - 25, delegate(int value) { Configuration.Police.Backup.InterceptionLeadDistance = value + 25; });
            AddSlider("Backup Stand Down", "5 to 300 seconds before a finished support force is cleaned up.", 295, Configuration.Police.Backup.StandDownSeconds - 5, delegate(int value) { Configuration.Police.Backup.StandDownSeconds = value + 5; });
        }

        private void BuildPoliceCleanup()
        {
            AddHeader("Police Cleanup");
            AddSlider("Completed Scene Grace", "0 to 3600 seconds.", 3600, Configuration.Police.Cleanup.CompletedSceneGraceSeconds, delegate(int value) { Configuration.Police.Cleanup.CompletedSceneGraceSeconds = value; });
            AddSlider("Hard Cleanup", "30 to 7200 seconds.", 7170, Configuration.Police.Cleanup.HardCleanupSeconds - 30, delegate(int value) { Configuration.Police.Cleanup.HardCleanupSeconds = value + 30; });
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
