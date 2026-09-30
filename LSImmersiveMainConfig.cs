using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// The one persisted settings owner for LS Immersive Life. Every editable
    /// control and gameplay behaviour in this class is read from and written to
    /// LSImmersiveMainUI.xml. Content catalogs remain data, not settings.
    /// </summary>
    internal sealed class LSImmersiveMainConfig
    {
        internal const string FileName = "LSImmersiveMainUI.xml";

        private static readonly Keys[] BindingKeys = new[]
        {
            Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6,
            Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12,
            Keys.A, Keys.B, Keys.C, Keys.D, Keys.E, Keys.F, Keys.G,
            Keys.H, Keys.I, Keys.J, Keys.K, Keys.L, Keys.M, Keys.N,
            Keys.O, Keys.P, Keys.Q, Keys.R, Keys.S, Keys.T, Keys.U,
            Keys.V, Keys.W, Keys.X, Keys.Y, Keys.Z,
            Keys.D0, Keys.D1, Keys.D2, Keys.D3, Keys.D4,
            Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9,
            Keys.NumPad0, Keys.NumPad1, Keys.NumPad2, Keys.NumPad3,
            Keys.NumPad4, Keys.NumPad5, Keys.NumPad6, Keys.NumPad7,
            Keys.NumPad8, Keys.NumPad9
        };

        internal Keys MenuToggleKey { get; private set; }
        internal bool ShowWaypoint { get { return Ui.ShowWaypoint; } }
        internal bool ShowVehicleMeter { get { return Ui.ShowVehicleMeter; } }
        internal bool EnableAudio { get { return Audio.Enabled; } }
        internal int AudioVolume { get { return Audio.Volume; } }
        internal LSPDControlBindings PoliceControls { get; private set; }

        internal LSUniversalUiSettings Ui { get; private set; }
        internal LSUniversalAudioSettings Audio { get; private set; }
        internal LSEnvironmentSettings Environment { get; private set; }
        internal LSLoggingSettings Logging { get; private set; }
        internal LSPerformanceSettings Performance { get; private set; }
        internal LSPoliceBehaviorSettings Police { get; private set; }

        /// <summary>
        /// Raised after an in-game settings or control edit has been normalized.
        /// Runtime owners subscribe to this signal and apply a safe refresh on
        /// their next game tick. Persistence remains explicit: this event never
        /// writes the XML document by itself.
        /// </summary>
        internal event Action SettingsChanged;

        /// <summary>
        /// Compatibility bridge for the active Authority owner. This value is
        /// stored in LSImmersiveMainUI.xml at Police/AuthoritySettings.
        /// </summary>
        internal LSPDAuthoritySettings PoliceAuthoritySettings
        {
            get { return Police.Authority; }
        }

        internal static Keys[] SupportedBindingKeys
        {
            get { return (Keys[])BindingKeys.Clone(); }
        }

        private LSImmersiveMainConfig()
        {
            MenuToggleKey = Keys.F4;
            Ui = LSUniversalUiSettings.Default();
            Audio = LSUniversalAudioSettings.Default();
            Environment = LSEnvironmentSettings.Default();
            Logging = LSLoggingSettings.Default();
            Performance = LSPerformanceSettings.Default();
            PoliceControls = LSPDControlBindings.Default();
            Police = LSPoliceBehaviorSettings.Default();
        }

        internal static LSImmersiveMainConfig Load(string path, Action<string> debug)
        {
            LSImmersiveMainConfig result = new LSImmersiveMainConfig();
            try
            {
                if (!File.Exists(path))
                    return result;

                XElement root = XElement.Load(path);
                if (root.Name != "LSImmersiveMainUI")
                    throw new InvalidDataException("Unexpected main configuration root.");

                XElement universal = root.Element("Universal");
                XElement controls = root.Element("Controls");
                XElement newControls = controls == null ? null : controls.Element("Universal");
                string menuKey = First(
                    Attr(newControls, "menuToggle"),
                    Attr(controls, "menuToggle"),
                    Value(root.Element("UniversalSettings"), "MenuToggleKey"));
                result.MenuToggleKey = ParseKey(menuKey, result.MenuToggleKey);

                result.Ui = LSUniversalUiSettings.Read(universal == null ? null : universal.Element("Interface"), result.Ui);
                result.Audio = LSUniversalAudioSettings.Read(
                    universal == null ? root.Element("Audio") : universal.Element("Audio") ?? root.Element("Audio"),
                    result.Audio);
                result.Environment = LSEnvironmentSettings.Read(universal == null ? null : universal.Element("Environment"), result.Environment);
                result.Logging = LSLoggingSettings.Read(universal == null ? null : universal.Element("Logging"), result.Logging);
                result.Performance = LSPerformanceSettings.Read(universal == null ? null : universal.Element("Performance"), result.Performance);
                result.PoliceControls = LSPDControlBindings.Read(
                    controls == null ? root.Element("PoliceControls") : controls.Element("Police") ?? root.Element("PoliceControls"),
                    result.PoliceControls);
                result.Police = LSPoliceBehaviorSettings.Read(root.Element("Police"), result.Police);
                result.Normalize(debug);
            }
            catch (Exception ex)
            {
                if (debug != null)
                    debug(ex.ToString());
            }
            return result;
        }

        /// <summary>
        /// Explicitly saves the complete supported schema in the one main XML.
        /// Startup and Load never create or overwrite a configuration document.
        /// </summary>
        internal void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A main configuration path is required.", "path");

            Normalize(null);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            XDocument document = File.Exists(path)
                ? XDocument.Load(path, LoadOptions.PreserveWhitespace)
                : new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("LSImmersiveMainUI"));
            XElement root = document.Root;
            if (root == null || root.Name != "LSImmersiveMainUI")
                throw new InvalidDataException("Unexpected main configuration root.");

            root.SetAttributeValue("version", "2.0");
            XElement universal = Child(root, "Universal");
            Upsert(universal, Ui.ToXml());
            Upsert(universal, Audio.ToXml());
            Upsert(universal, Environment.ToXml());
            Upsert(universal, Logging.ToXml());
            Upsert(universal, Performance.ToXml());

            XElement controls = Child(root, "Controls");
            controls.SetAttributeValue("menuToggle", null); // v1 migration
            Upsert(controls, new XElement("Universal", new XAttribute("menuToggle", MenuToggleKey)));
            Upsert(controls, PoliceControls.ToXml("Police"));

            XElement police = Child(root, "Police");
            foreach (XElement section in Police.ToXml().Elements())
                Upsert(police, new XElement(section));

            // Version 1 values are removed only after their version 2
            // equivalent has been written. Unknown attributes/comments remain.
            RemoveLegacy(root.Element("Audio"), "enabled", "volume");
            RemoveLegacy(root.Element("PoliceControls"), "patrol", "accept", "reject", "investigate", "secure", "transport", "transportComplete", "interaction", "emergency", "reset");
            RemoveLegacyMenuKey(root.Element("UniversalSettings"));

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

        // Existing Police Core callers retain these two APIs.
        internal void SetAudio(bool enabled, int volume)
        {
            Audio.Enabled = enabled;
            Audio.Volume = LSConfig.Clamp(volume, 0, 100);
        }

        internal void SetUiOptions(bool showWaypoint, bool showVehicleMeter)
        {
            Ui.ShowWaypoint = showWaypoint;
            Ui.ShowVehicleMeter = showVehicleMeter;
        }

        internal void SetPoliceAuthoritySettings(LSPDAuthoritySettings settings)
        {
            Police.Authority = settings ?? LSPDAuthoritySettings.Default();
            Police.Authority.Normalize();
        }

        internal void SetPoliceAuthorityOptions(bool protect, bool suppressWanted, bool suppressAmbient, bool gangReaction, bool civilianReaction, bool worldChanges, bool verifiedInteriors)
        {
            // Compatibility entry point for existing callers and tests. New
            // Authority-specific fields remain unchanged when an older caller
            // edits one of these original settings.
            LSPDAuthoritySettings settings = Police.Authority
                ?? LSPDAuthoritySettings.Default();
            settings.ProtectPlayerFromVanillaPoliceEscalation = protect;
            settings.SuppressVanillaWantedEscalation = suppressWanted;
            settings.SuppressAmbientPoliceHostility = suppressAmbient;
            settings.EnableGangReactionControl = gangReaction;
            settings.EnableCivilianReactionControl = civilianReaction;
            settings.EnableWorldBehaviorChanges = worldChanges;
            settings.UseVerifiedInteriorsOnly = verifiedInteriors;
            settings.Normalize();
            Police.Authority = settings;
        }

        internal void NormalizeSettings()
        {
            Normalize(null);
        }

        /// <summary>
        /// Lets the dedicated Settings and Customization UIs announce a valid
        /// in-memory edit to active runtime owners. This keeps those UIs out of
        /// PoliceCore while still allowing supported values to take effect
        /// without a script restart.
        /// </summary>
        internal void NotifySettingsChanged()
        {
            Action changed = SettingsChanged;
            if (changed != null)
                changed();
        }

        internal bool TrySetMenuToggleKey(Keys key, out string reason)
        {
            Keys normalized;
            if (!ValidKey(key, out normalized))
            {
                reason = "Choose one of the supported keyboard keys.";
                return false;
            }
            if (PoliceControls.Contains(normalized))
            {
                reason = "That key is already assigned to a Police control.";
                return false;
            }
            MenuToggleKey = normalized;
            reason = null;
            return true;
        }

        internal bool TrySetPoliceControl(LSPDControlAction action, Keys key, out string reason)
        {
            Keys normalized;
            if (!ValidKey(key, out normalized))
            {
                reason = "Choose one of the supported keyboard keys.";
                return false;
            }
            if (normalized == MenuToggleKey)
            {
                reason = "That key is reserved for the LS Immersive menu.";
                return false;
            }
            foreach (LSPDControlAction other in LSPDControlBindings.Actions)
            {
                if (other != action && PoliceControls.Get(other) == normalized)
                {
                    reason = "That key is already assigned to another Police control.";
                    return false;
                }
            }
            PoliceControls.Set(action, normalized);
            reason = null;
            return true;
        }

        private void Normalize(Action<string> debug)
        {
            Ui = Ui ?? LSUniversalUiSettings.Default();
            Audio = Audio ?? LSUniversalAudioSettings.Default();
            Environment = Environment ?? LSEnvironmentSettings.Default();
            Logging = Logging ?? LSLoggingSettings.Default();
            Performance = Performance ?? LSPerformanceSettings.Default();
            Police = Police ?? LSPoliceBehaviorSettings.Default();
            PoliceControls = PoliceControls ?? LSPDControlBindings.Default();
            Audio.Normalize();
            Environment.Normalize();
            Performance.Normalize();
            Police.Normalize();

            Keys menu;
            if (!ValidKey(MenuToggleKey, out menu))
                MenuToggleKey = Keys.F4;
            else
                MenuToggleKey = menu;

            HashSet<Keys> claimed = new HashSet<Keys> { MenuToggleKey };
            LSPDControlBindings defaults = LSPDControlBindings.Default();
            foreach (LSPDControlAction action in LSPDControlBindings.Actions)
            {
                Keys current = PoliceControls.Get(action);
                Keys normalized;
                if (!ValidKey(current, out normalized) || claimed.Contains(normalized))
                {
                    Keys replacement = Available(defaults.Get(action), claimed);
                    PoliceControls.Set(action, replacement);
                    claimed.Add(replacement);
                    if (debug != null)
                        debug("MAIN_CONFIG | " + action + " key reset to " + replacement + ".");
                }
                else
                {
                    PoliceControls.Set(action, normalized);
                    claimed.Add(normalized);
                }
            }
        }

        private static Keys Available(Keys preferred, ISet<Keys> claimed)
        {
            Keys key;
            if (ValidKey(preferred, out key) && !claimed.Contains(key))
                return key;
            foreach (Keys candidate in BindingKeys)
            {
                if (!claimed.Contains(candidate))
                    return candidate;
            }
            return Keys.F12;
        }

        private static bool ValidKey(Keys raw, out Keys normalized)
        {
            normalized = raw & Keys.KeyCode;
            return Array.IndexOf(BindingKeys, normalized) >= 0;
        }

        private static Keys ParseKey(string value, Keys fallback)
        {
            Keys key;
            Keys normalized;
            return Enum.TryParse(value, true, out key) && ValidKey(key, out normalized) ? normalized : fallback;
        }

        private static string First(params string[] values)
        {
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return null;
        }

        private static string Attr(XElement element, string name)
        {
            return element == null ? null : (string)element.Attribute(name);
        }

        private static string Value(XElement element, string name)
        {
            XElement child = element == null ? null : element.Element(name);
            return child == null ? null : child.Value;
        }

        private static XElement Child(XElement parent, string name)
        {
            XElement child = parent.Element(name);
            if (child == null) { child = new XElement(name); parent.Add(child); }
            return child;
        }

        private static void Upsert(XElement parent, XElement source)
        {
            XElement target = parent.Element(source.Name);
            if (target == null) { parent.Add(source); return; }
            foreach (XAttribute attribute in source.Attributes())
                target.SetAttributeValue(attribute.Name, attribute.Value);
        }

        private static void RemoveLegacy(XElement element, params string[] attributes)
        {
            if (element == null) return;
            foreach (string attribute in attributes) element.SetAttributeValue(attribute, null);
            if (!element.HasAttributes && !element.Nodes().Any()) element.Remove();
        }

        private static void RemoveLegacyMenuKey(XElement element)
        {
            if (element == null) return;
            XElement menuKey = element.Element("MenuToggleKey");
            if (menuKey != null) menuKey.Remove();
            if (!element.HasAttributes && !element.Nodes().Any()) element.Remove();
        }
    }

    internal enum LSPDControlAction
    {
        Patrol, Accept, Reject, Investigate, Secure, Transport, TransportComplete, Interaction, Emergency, Reset
    }

    internal sealed class LSPDControlBindings
    {
        private static readonly LSPDControlAction[] AllActions = new[]
        {
            LSPDControlAction.Patrol, LSPDControlAction.Accept, LSPDControlAction.Reject,
            LSPDControlAction.Investigate, LSPDControlAction.Secure, LSPDControlAction.Transport,
            LSPDControlAction.TransportComplete, LSPDControlAction.Interaction,
            LSPDControlAction.Emergency, LSPDControlAction.Reset
        };

        internal static IEnumerable<LSPDControlAction> Actions { get { return AllActions; } }
        internal Keys PatrolKey { get; private set; }
        internal Keys AcceptKey { get; private set; }
        internal Keys RejectKey { get; private set; }
        internal Keys InvestigateKey { get; private set; }
        internal Keys SecureKey { get; private set; }
        internal Keys TransportKey { get; private set; }
        internal Keys TransportCompleteKey { get; private set; }
        internal Keys InteractionKey { get; private set; }
        internal Keys EmergencyKey { get; private set; }
        internal Keys ResetKey { get; private set; }

        internal static LSPDControlBindings Default()
        {
            return new LSPDControlBindings
            {
                PatrolKey = Keys.P, AcceptKey = Keys.Y, RejectKey = Keys.N,
                InvestigateKey = Keys.I, SecureKey = Keys.E, TransportKey = Keys.R,
                TransportCompleteKey = Keys.T, InteractionKey = Keys.G,
                // B is the consistent player-facing backup request key. The
                // legacy property name remains so saved emergency bindings
                // continue to load from the one main configuration document.
                EmergencyKey = Keys.B, ResetKey = Keys.F10
            };
        }

        internal static LSPDControlBindings Read(XElement node, LSPDControlBindings fallback)
        {
            LSPDControlBindings result = fallback ?? Default();
            if (node == null) return result;
            return new LSPDControlBindings
            {
                PatrolKey = Key(node, "patrol", result.PatrolKey), AcceptKey = Key(node, "accept", result.AcceptKey),
                RejectKey = Key(node, "reject", result.RejectKey), InvestigateKey = Key(node, "investigate", result.InvestigateKey),
                SecureKey = Key(node, "secure", result.SecureKey), TransportKey = Key(node, "transport", result.TransportKey),
                TransportCompleteKey = Key(node, "transportComplete", result.TransportCompleteKey),
                InteractionKey = Key(node, "interaction", result.InteractionKey), EmergencyKey = Key(node, "emergency", result.EmergencyKey),
                ResetKey = Key(node, "reset", result.ResetKey)
            };
        }

        internal XElement ToXml() { return ToXml("PoliceControls"); }
        internal XElement ToXml(string name)
        {
            return new XElement(name,
                new XAttribute("patrol", PatrolKey), new XAttribute("accept", AcceptKey),
                new XAttribute("reject", RejectKey), new XAttribute("investigate", InvestigateKey),
                new XAttribute("secure", SecureKey), new XAttribute("transport", TransportKey),
                new XAttribute("transportComplete", TransportCompleteKey), new XAttribute("interaction", InteractionKey),
                new XAttribute("emergency", EmergencyKey), new XAttribute("reset", ResetKey));
        }

        internal Keys Get(LSPDControlAction action)
        {
            switch (action)
            {
                case LSPDControlAction.Patrol: return PatrolKey;
                case LSPDControlAction.Accept: return AcceptKey;
                case LSPDControlAction.Reject: return RejectKey;
                case LSPDControlAction.Investigate: return InvestigateKey;
                case LSPDControlAction.Secure: return SecureKey;
                case LSPDControlAction.Transport: return TransportKey;
                case LSPDControlAction.TransportComplete: return TransportCompleteKey;
                case LSPDControlAction.Interaction: return InteractionKey;
                case LSPDControlAction.Emergency: return EmergencyKey;
                case LSPDControlAction.Reset: return ResetKey;
                default: throw new ArgumentOutOfRangeException("action");
            }
        }

        internal void Set(LSPDControlAction action, Keys value)
        {
            switch (action)
            {
                case LSPDControlAction.Patrol: PatrolKey = value; break;
                case LSPDControlAction.Accept: AcceptKey = value; break;
                case LSPDControlAction.Reject: RejectKey = value; break;
                case LSPDControlAction.Investigate: InvestigateKey = value; break;
                case LSPDControlAction.Secure: SecureKey = value; break;
                case LSPDControlAction.Transport: TransportKey = value; break;
                case LSPDControlAction.TransportComplete: TransportCompleteKey = value; break;
                case LSPDControlAction.Interaction: InteractionKey = value; break;
                case LSPDControlAction.Emergency: EmergencyKey = value; break;
                case LSPDControlAction.Reset: ResetKey = value; break;
                default: throw new ArgumentOutOfRangeException("action");
            }
        }

        internal bool Contains(Keys key)
        {
            foreach (LSPDControlAction action in AllActions) if (Get(action) == key) return true;
            return false;
        }

        private static Keys Key(XElement node, string attribute, Keys fallback)
        {
            Keys key;
            return Enum.TryParse((string)node.Attribute(attribute), true, out key) ? key : fallback;
        }
    }

    internal sealed class LSUniversalUiSettings
    {
        internal bool ShowWaypoint { get; set; }
        internal bool ShowVehicleMeter { get; set; }
        internal static LSUniversalUiSettings Default() { return new LSUniversalUiSettings { ShowWaypoint = true, ShowVehicleMeter = true }; }
        internal static LSUniversalUiSettings Read(XElement node, LSUniversalUiSettings fallback)
        {
            LSUniversalUiSettings d = fallback ?? Default();
            return node == null ? d : new LSUniversalUiSettings { ShowWaypoint = LSConfig.Bool(node, "showWaypoint", d.ShowWaypoint), ShowVehicleMeter = LSConfig.Bool(node, "showVehicleMeter", d.ShowVehicleMeter) };
        }
        internal XElement ToXml() { return new XElement("Interface", new XAttribute("showWaypoint", ShowWaypoint), new XAttribute("showVehicleMeter", ShowVehicleMeter)); }
    }

    internal sealed class LSUniversalAudioSettings
    {
        internal bool Enabled { get; set; }
        internal int Volume { get; set; }
        internal static LSUniversalAudioSettings Default() { return new LSUniversalAudioSettings { Enabled = true, Volume = 55 }; }
        internal static LSUniversalAudioSettings Read(XElement node, LSUniversalAudioSettings fallback)
        {
            LSUniversalAudioSettings d = fallback ?? Default();
            return node == null ? d : new LSUniversalAudioSettings { Enabled = LSConfig.Bool(node, "enabled", d.Enabled), Volume = LSConfig.Int(node, "volume", d.Volume, 0, 100) };
        }
        internal void Normalize() { Volume = LSConfig.Clamp(Volume, 0, 100); }
        internal XElement ToXml() { return new XElement("Audio", new XAttribute("enabled", Enabled), new XAttribute("volume", Volume)); }
    }

    internal sealed class LSEnvironmentSettings
    {
        internal bool AmbientTrafficEnabled { get; set; }
        internal bool AmbientPedestriansEnabled { get; set; }
        internal bool DynamicWeatherEnabled { get; set; }
        internal int TrafficDensityPercent { get; set; }
        internal int PedestrianDensityPercent { get; set; }
        internal static LSEnvironmentSettings Default()
        {
            return new LSEnvironmentSettings { AmbientTrafficEnabled = true, AmbientPedestriansEnabled = true, DynamicWeatherEnabled = true, TrafficDensityPercent = 100, PedestrianDensityPercent = 100 };
        }
        internal static LSEnvironmentSettings Read(XElement n, LSEnvironmentSettings f)
        {
            LSEnvironmentSettings d = f ?? Default();
            return n == null ? d : new LSEnvironmentSettings { AmbientTrafficEnabled = LSConfig.Bool(n, "ambientTrafficEnabled", d.AmbientTrafficEnabled), AmbientPedestriansEnabled = LSConfig.Bool(n, "ambientPedestriansEnabled", d.AmbientPedestriansEnabled), DynamicWeatherEnabled = LSConfig.Bool(n, "dynamicWeatherEnabled", d.DynamicWeatherEnabled), TrafficDensityPercent = LSConfig.Int(n, "trafficDensityPercent", d.TrafficDensityPercent, 0, 200), PedestrianDensityPercent = LSConfig.Int(n, "pedestrianDensityPercent", d.PedestrianDensityPercent, 0, 200) };
        }
        internal void Normalize() { TrafficDensityPercent = LSConfig.Clamp(TrafficDensityPercent, 0, 200); PedestrianDensityPercent = LSConfig.Clamp(PedestrianDensityPercent, 0, 200); }
        internal XElement ToXml() { return new XElement("Environment", new XAttribute("ambientTrafficEnabled", AmbientTrafficEnabled), new XAttribute("ambientPedestriansEnabled", AmbientPedestriansEnabled), new XAttribute("dynamicWeatherEnabled", DynamicWeatherEnabled), new XAttribute("trafficDensityPercent", TrafficDensityPercent), new XAttribute("pedestrianDensityPercent", PedestrianDensityPercent)); }
    }

    internal sealed class LSLoggingSettings
    {
        internal bool RuntimeEnabled { get; set; }
        internal bool DebugEnabled { get; set; }
        internal bool VerboseDiagnostics { get; set; }
        internal bool SessionHeaders { get; set; }
        internal static LSLoggingSettings Default() { return new LSLoggingSettings { RuntimeEnabled = true, DebugEnabled = true, VerboseDiagnostics = false, SessionHeaders = true }; }
        internal static LSLoggingSettings Read(XElement n, LSLoggingSettings f)
        {
            LSLoggingSettings d = f ?? Default();
            return n == null ? d : new LSLoggingSettings { RuntimeEnabled = LSConfig.Bool(n, "runtimeEnabled", d.RuntimeEnabled), DebugEnabled = LSConfig.Bool(n, "debugEnabled", d.DebugEnabled), VerboseDiagnostics = LSConfig.Bool(n, "verboseDiagnostics", d.VerboseDiagnostics), SessionHeaders = LSConfig.Bool(n, "sessionHeaders", d.SessionHeaders) };
        }
        internal XElement ToXml() { return new XElement("Logging", new XAttribute("runtimeEnabled", RuntimeEnabled), new XAttribute("debugEnabled", DebugEnabled), new XAttribute("verboseDiagnostics", VerboseDiagnostics), new XAttribute("sessionHeaders", SessionHeaders)); }
    }

    internal sealed class LSPerformanceSettings
    {
        internal int MaxManagedEntities { get; set; }
        internal int CleanupIntervalSeconds { get; set; }
        internal int HeavyScanIntervalMilliseconds { get; set; }
        internal bool ThrottleHeavyScans { get; set; }
        internal static LSPerformanceSettings Default() { return new LSPerformanceSettings { MaxManagedEntities = 64, CleanupIntervalSeconds = 30, HeavyScanIntervalMilliseconds = 1250, ThrottleHeavyScans = true }; }
        internal static LSPerformanceSettings Read(XElement n, LSPerformanceSettings f)
        {
            LSPerformanceSettings d = f ?? Default();
            return n == null ? d : new LSPerformanceSettings { MaxManagedEntities = LSConfig.Int(n, "maxManagedEntities", d.MaxManagedEntities, 16, 256), CleanupIntervalSeconds = LSConfig.Int(n, "cleanupIntervalSeconds", d.CleanupIntervalSeconds, 5, 600), HeavyScanIntervalMilliseconds = LSConfig.Int(n, "heavyScanIntervalMilliseconds", d.HeavyScanIntervalMilliseconds, 50, 5000), ThrottleHeavyScans = LSConfig.Bool(n, "throttleHeavyScans", d.ThrottleHeavyScans) };
        }
        internal void Normalize() { MaxManagedEntities = LSConfig.Clamp(MaxManagedEntities, 16, 256); CleanupIntervalSeconds = LSConfig.Clamp(CleanupIntervalSeconds, 5, 600); HeavyScanIntervalMilliseconds = LSConfig.Clamp(HeavyScanIntervalMilliseconds, 50, 5000); }
        internal XElement ToXml() { return new XElement("Performance", new XAttribute("maxManagedEntities", MaxManagedEntities), new XAttribute("cleanupIntervalSeconds", CleanupIntervalSeconds), new XAttribute("heavyScanIntervalMilliseconds", HeavyScanIntervalMilliseconds), new XAttribute("throttleHeavyScans", ThrottleHeavyScans)); }
    }

    /// <summary>Typed Police behaviour sections for future and active owners.</summary>
    internal sealed class LSPoliceBehaviorSettings
    {
        internal LSPDAuthoritySettings Authority { get; set; }
        internal LSPolicePatrolSettings Patrol { get; set; }
        internal LSPoliceDispatchSettings Dispatch { get; set; }
        // Crime Activity is a quiet, player-led patrol layer. It remains
        // separate from the urgent Dispatch producer and has its own exposed
        // discovery, proximity, scene, and cooldown values.
        internal LSPoliceCrimeActivitySettings CrimeActivity { get; set; }
        internal LSPoliceNpcResponseSettings NpcResponse { get; set; }
        internal LSPoliceTrafficSettings Traffic { get; set; }
        internal LSPoliceResponseSettings Response { get; set; }
        // Player-requested support units are a distinct owner from the
        // nearby-ally / restrained automatic response pass above.  Keeping
        // this section here makes every exposed backup behaviour editable in
        // the one universal configuration document.
        internal LSPoliceBackupSettings Backup { get; set; }
        internal LSPoliceConvoySettings Convoy { get; set; }
        internal LSPoliceCleanupSettings Cleanup { get; set; }
        internal static LSPoliceBehaviorSettings Default()
        {
            return new LSPoliceBehaviorSettings { Authority = LSPDAuthoritySettings.Default(), Patrol = LSPolicePatrolSettings.Default(), Dispatch = LSPoliceDispatchSettings.Default(), CrimeActivity = LSPoliceCrimeActivitySettings.Default(), NpcResponse = LSPoliceNpcResponseSettings.Default(), Traffic = LSPoliceTrafficSettings.Default(), Response = LSPoliceResponseSettings.Default(), Backup = LSPoliceBackupSettings.Default(), Convoy = LSPoliceConvoySettings.Default(), Cleanup = LSPoliceCleanupSettings.Default() };
        }
        internal static LSPoliceBehaviorSettings Read(XElement n, LSPoliceBehaviorSettings f)
        {
            LSPoliceBehaviorSettings d = f ?? Default();
            if (n == null) return d;
            return new LSPoliceBehaviorSettings { Authority = LSPDAuthoritySettings.FromXml(n.Element("AuthoritySettings")), Patrol = LSPolicePatrolSettings.Read(n.Element("Patrol"), d.Patrol), Dispatch = LSPoliceDispatchSettings.Read(n.Element("Dispatch"), d.Dispatch), CrimeActivity = LSPoliceCrimeActivitySettings.Read(n.Element("CrimeActivity"), d.CrimeActivity), NpcResponse = LSPoliceNpcResponseSettings.Read(n.Element("NpcResponse"), d.NpcResponse), Traffic = LSPoliceTrafficSettings.Read(n.Element("Traffic"), d.Traffic), Response = LSPoliceResponseSettings.Read(n.Element("Response"), d.Response), Backup = LSPoliceBackupSettings.Read(n.Element("Backup"), d.Backup), Convoy = LSPoliceConvoySettings.Read(n.Element("Convoy"), d.Convoy), Cleanup = LSPoliceCleanupSettings.Read(n.Element("Cleanup"), d.Cleanup) };
        }
        internal void Normalize()
        {
            Authority = Authority ?? LSPDAuthoritySettings.Default(); Patrol = Patrol ?? LSPolicePatrolSettings.Default(); Dispatch = Dispatch ?? LSPoliceDispatchSettings.Default(); CrimeActivity = CrimeActivity ?? LSPoliceCrimeActivitySettings.Default(); NpcResponse = NpcResponse ?? LSPoliceNpcResponseSettings.Default(); Traffic = Traffic ?? LSPoliceTrafficSettings.Default(); Response = Response ?? LSPoliceResponseSettings.Default(); Backup = Backup ?? LSPoliceBackupSettings.Default(); Convoy = Convoy ?? LSPoliceConvoySettings.Default(); Cleanup = Cleanup ?? LSPoliceCleanupSettings.Default(); Authority.Normalize(); Dispatch.Normalize(); CrimeActivity.Normalize(); NpcResponse.Normalize(); Traffic.Normalize(); Response.Normalize(); Backup.Normalize(); Convoy.Normalize(); Cleanup.Normalize();
        }
        internal XElement ToXml()
        {
            return new XElement("Police", Authority.ToXml(), Patrol.ToXml(), Dispatch.ToXml(), CrimeActivity.ToXml(), NpcResponse.ToXml(), Traffic.ToXml(), Response.ToXml(), Backup.ToXml(), Convoy.ToXml(), Cleanup.ToXml());
        }
    }

    internal sealed class LSPolicePatrolSettings
    {
        internal bool Enabled { get; set; }
        internal bool ReuseCurrentPoliceVehicle { get; set; }
        internal bool PreventDuplicatePersonalVehicle { get; set; }
        internal static LSPolicePatrolSettings Default() { return new LSPolicePatrolSettings { Enabled = true, ReuseCurrentPoliceVehicle = true, PreventDuplicatePersonalVehicle = true }; }
        internal static LSPolicePatrolSettings Read(XElement n, LSPolicePatrolSettings f) { LSPolicePatrolSettings d = f ?? Default(); return n == null ? d : new LSPolicePatrolSettings { Enabled = LSConfig.Bool(n, "enabled", d.Enabled), ReuseCurrentPoliceVehicle = LSConfig.Bool(n, "reuseCurrentPoliceVehicle", d.ReuseCurrentPoliceVehicle), PreventDuplicatePersonalVehicle = LSConfig.Bool(n, "preventDuplicatePersonalVehicle", d.PreventDuplicatePersonalVehicle) }; }
        internal XElement ToXml() { return new XElement("Patrol", new XAttribute("enabled", Enabled), new XAttribute("reuseCurrentPoliceVehicle", ReuseCurrentPoliceVehicle), new XAttribute("preventDuplicatePersonalVehicle", PreventDuplicatePersonalVehicle)); }
    }

    internal sealed class LSPoliceDispatchSettings
    {
        internal bool Enabled { get; set; }
        internal int MinimumQuietPatrolSeconds { get; set; }
        internal int MaximumQuietPatrolSeconds { get; set; }
        internal int RepeatProtectionMinutes { get; set; }
        internal int MaximumActiveIncidents { get; set; }
        internal int AudioMinimumGapSeconds { get; set; }
        internal int ContextClearQuietSeconds { get; set; }
        internal static LSPoliceDispatchSettings Default() { return new LSPoliceDispatchSettings { Enabled = true, MinimumQuietPatrolSeconds = 180, MaximumQuietPatrolSeconds = 600, RepeatProtectionMinutes = 20, MaximumActiveIncidents = 1, AudioMinimumGapSeconds = 5, ContextClearQuietSeconds = 30 }; }
        internal static LSPoliceDispatchSettings Read(XElement n, LSPoliceDispatchSettings f)
        {
            LSPoliceDispatchSettings d = f ?? Default(); if (n == null) return d;
            LSPoliceDispatchSettings r = new LSPoliceDispatchSettings { Enabled = LSConfig.Bool(n, "enabled", d.Enabled), MinimumQuietPatrolSeconds = LSConfig.Int(n, "minimumQuietPatrolSeconds", d.MinimumQuietPatrolSeconds, 30, 1800), MaximumQuietPatrolSeconds = LSConfig.Int(n, "maximumQuietPatrolSeconds", d.MaximumQuietPatrolSeconds, 30, 3600), RepeatProtectionMinutes = LSConfig.Int(n, "repeatProtectionMinutes", d.RepeatProtectionMinutes, 0, 240), MaximumActiveIncidents = 1, AudioMinimumGapSeconds = LSConfig.Int(n, "audioMinimumGapSeconds", d.AudioMinimumGapSeconds, 0, 60), ContextClearQuietSeconds = LSConfig.Int(n, "contextClearQuietSeconds", d.ContextClearQuietSeconds, 0, 300) }; r.Normalize(); return r;
        }
        internal void Normalize() { MinimumQuietPatrolSeconds = LSConfig.Clamp(MinimumQuietPatrolSeconds, 30, 1800); MaximumQuietPatrolSeconds = LSConfig.Clamp(MaximumQuietPatrolSeconds, MinimumQuietPatrolSeconds, 3600); RepeatProtectionMinutes = LSConfig.Clamp(RepeatProtectionMinutes, 0, 240); MaximumActiveIncidents = 1; AudioMinimumGapSeconds = LSConfig.Clamp(AudioMinimumGapSeconds, 0, 60); ContextClearQuietSeconds = LSConfig.Clamp(ContextClearQuietSeconds, 0, 300); }
        internal XElement ToXml() { return new XElement("Dispatch", new XAttribute("enabled", Enabled), new XAttribute("minimumQuietPatrolSeconds", MinimumQuietPatrolSeconds), new XAttribute("maximumQuietPatrolSeconds", MaximumQuietPatrolSeconds), new XAttribute("repeatProtectionMinutes", RepeatProtectionMinutes), new XAttribute("maximumActiveIncidents", MaximumActiveIncidents), new XAttribute("audioMinimumGapSeconds", AudioMinimumGapSeconds), new XAttribute("contextClearQuietSeconds", ContextClearQuietSeconds)); }
    }

    /// <summary>
    /// Settings for the player-led Crime Activity owner. These values control
    /// how quiet intelligence becomes geographically relevant during patrol;
    /// they do not create or tune normal Dispatch incidents.
    /// </summary>
    internal sealed class LSPoliceCrimeActivitySettings
    {
        internal bool Enabled { get; set; }
        internal bool AutoDiscover { get; set; }
        internal bool NearbyTipNotifications { get; set; }
        internal int DiscoveryScanSeconds { get; set; }
        internal int MaximumDeploymentDistance { get; set; }
        internal int NearbyTipDistance { get; set; }
        internal int SceneActivationDistance { get; set; }
        internal int ParticipantCount { get; set; }
        internal int SameLocationCooldownSeconds { get; set; }
        internal int RedeploymentCooldownSeconds { get; set; }
        internal int AbandonDistance { get; set; }
        internal int AssetPreparationTimeoutSeconds { get; set; }

        internal static LSPoliceCrimeActivitySettings Default()
        {
            return new LSPoliceCrimeActivitySettings
            {
                Enabled = true,
                AutoDiscover = true,
                NearbyTipNotifications = true,
                DiscoveryScanSeconds = 8,
                MaximumDeploymentDistance = 300,
                NearbyTipDistance = 100,
                SceneActivationDistance = 120,
                ParticipantCount = 3,
                SameLocationCooldownSeconds = 900,
                RedeploymentCooldownSeconds = 180,
                AbandonDistance = 360,
                AssetPreparationTimeoutSeconds = 20
            };
        }

        internal static LSPoliceCrimeActivitySettings Read(
            XElement node,
            LSPoliceCrimeActivitySettings fallback)
        {
            LSPoliceCrimeActivitySettings d = fallback ?? Default();
            if (node == null)
                return d;

            LSPoliceCrimeActivitySettings value = new LSPoliceCrimeActivitySettings
            {
                Enabled = LSConfig.Bool(node, "enabled", d.Enabled),
                AutoDiscover = LSConfig.Bool(node, "autoDiscover", d.AutoDiscover),
                NearbyTipNotifications = LSConfig.Bool(node, "nearbyTipNotifications", d.NearbyTipNotifications),
                DiscoveryScanSeconds = LSConfig.Int(node, "discoveryScanSeconds", d.DiscoveryScanSeconds, 2, 120),
                MaximumDeploymentDistance = LSConfig.Int(node, "maximumDeploymentDistance", d.MaximumDeploymentDistance, 100, 600),
                NearbyTipDistance = LSConfig.Int(node, "nearbyTipDistance", d.NearbyTipDistance, 25, 250),
                SceneActivationDistance = LSConfig.Int(node, "sceneActivationDistance", d.SceneActivationDistance, 40, 250),
                ParticipantCount = LSConfig.Int(node, "participantCount", d.ParticipantCount, 2, 6),
                SameLocationCooldownSeconds = LSConfig.Int(node, "sameLocationCooldownSeconds", d.SameLocationCooldownSeconds, 60, 7200),
                RedeploymentCooldownSeconds = LSConfig.Int(node, "redeploymentCooldownSeconds", d.RedeploymentCooldownSeconds, 15, 1800),
                AbandonDistance = LSConfig.Int(node, "abandonDistance", d.AbandonDistance, 150, 1000),
                AssetPreparationTimeoutSeconds = LSConfig.Int(node, "assetPreparationTimeoutSeconds", d.AssetPreparationTimeoutSeconds, 5, 60)
            };
            value.Normalize();
            return value;
        }

        internal void Normalize()
        {
            DiscoveryScanSeconds = LSConfig.Clamp(DiscoveryScanSeconds, 2, 120);
            MaximumDeploymentDistance = LSConfig.Clamp(MaximumDeploymentDistance, 100, 600);
            NearbyTipDistance = LSConfig.Clamp(NearbyTipDistance, 25, MaximumDeploymentDistance);
            SceneActivationDistance = LSConfig.Clamp(SceneActivationDistance, 40, MaximumDeploymentDistance);
            ParticipantCount = LSConfig.Clamp(ParticipantCount, 2, 6);
            SameLocationCooldownSeconds = LSConfig.Clamp(SameLocationCooldownSeconds, 60, 7200);
            RedeploymentCooldownSeconds = LSConfig.Clamp(RedeploymentCooldownSeconds, 15, 1800);
            AbandonDistance = LSConfig.Clamp(AbandonDistance, 150, 1000);
            AssetPreparationTimeoutSeconds = LSConfig.Clamp(AssetPreparationTimeoutSeconds, 5, 60);
        }

        internal XElement ToXml()
        {
            return new XElement(
                "CrimeActivity",
                new XAttribute("enabled", Enabled),
                new XAttribute("autoDiscover", AutoDiscover),
                new XAttribute("nearbyTipNotifications", NearbyTipNotifications),
                new XAttribute("discoveryScanSeconds", DiscoveryScanSeconds),
                new XAttribute("maximumDeploymentDistance", MaximumDeploymentDistance),
                new XAttribute("nearbyTipDistance", NearbyTipDistance),
                new XAttribute("sceneActivationDistance", SceneActivationDistance),
                new XAttribute("participantCount", ParticipantCount),
                new XAttribute("sameLocationCooldownSeconds", SameLocationCooldownSeconds),
                new XAttribute("redeploymentCooldownSeconds", RedeploymentCooldownSeconds),
                new XAttribute("abandonDistance", AbandonDistance),
                new XAttribute("assetPreparationTimeoutSeconds", AssetPreparationTimeoutSeconds));
        }
    }

    internal sealed class LSPoliceNpcResponseSettings
    {
        internal bool Enabled { get; set; }
        internal bool InteractionsEnabled { get; set; }
        internal int InteractionRadius { get; set; }
        internal int TrafficAwarenessRadius { get; set; }
        internal int CollisionGuardCooldownMilliseconds { get; set; }
        internal int MaximumAffectedTrafficVehicles { get; set; }
        internal int InteractionTimeoutSeconds { get; set; }
        internal int DocumentPresentationSeconds { get; set; }
        internal int CitizenFleeChancePercent { get; set; }
        internal int CitizenResistChancePercent { get; set; }
        internal int SuspiciousEncounterChancePercent { get; set; }
        internal int CompliantKneelChancePercent { get; set; }
        internal int DeadSubjectReleaseDistance { get; set; }
        internal int TrafficFleeChancePercent { get; set; }
        internal int PullOverTimeoutSeconds { get; set; }
        internal int TrafficTaskRecoverySeconds { get; set; }
        internal int ScenePedReactionRadius { get; set; }
        internal int MaximumAffectedPedestrians { get; set; }
        internal int SceneReactionCooldownSeconds { get; set; }

        internal static LSPoliceNpcResponseSettings Default()
        {
            return new LSPoliceNpcResponseSettings
            {
                Enabled = true,
                InteractionsEnabled = true,
                InteractionRadius = 5,
                TrafficAwarenessRadius = 28,
                CollisionGuardCooldownMilliseconds = 1200,
                MaximumAffectedTrafficVehicles = 3,
                InteractionTimeoutSeconds = 90,
                DocumentPresentationSeconds = 3,
                CitizenFleeChancePercent = 40,
                CitizenResistChancePercent = 18,
                SuspiciousEncounterChancePercent = 12,
                CompliantKneelChancePercent = 35,
                DeadSubjectReleaseDistance = 80,
                TrafficFleeChancePercent = 45,
                PullOverTimeoutSeconds = 12,
                TrafficTaskRecoverySeconds = 8,
                ScenePedReactionRadius = 18,
                MaximumAffectedPedestrians = 3,
                SceneReactionCooldownSeconds = 15
            };
        }

        internal static LSPoliceNpcResponseSettings Read(
            XElement n,
            LSPoliceNpcResponseSettings f)
        {
            LSPoliceNpcResponseSettings d = f ?? Default();
            return n == null ? d : new LSPoliceNpcResponseSettings
            {
                Enabled = LSConfig.Bool(n, "enabled", d.Enabled),
                InteractionsEnabled = LSConfig.Bool(n, "interactionsEnabled", d.InteractionsEnabled),
                InteractionRadius = LSConfig.Int(n, "interactionRadius", d.InteractionRadius, 1, 25),
                TrafficAwarenessRadius = LSConfig.Int(n, "trafficAwarenessRadius", d.TrafficAwarenessRadius, 1, 120),
                CollisionGuardCooldownMilliseconds = LSConfig.Int(n, "collisionGuardCooldownMilliseconds", d.CollisionGuardCooldownMilliseconds, 0, 10000),
                MaximumAffectedTrafficVehicles = LSConfig.Int(n, "maximumAffectedTrafficVehicles", d.MaximumAffectedTrafficVehicles, 0, 20),
                InteractionTimeoutSeconds = LSConfig.Int(n, "interactionTimeoutSeconds", d.InteractionTimeoutSeconds, 20, 300),
                DocumentPresentationSeconds = LSConfig.Int(n, "documentPresentationSeconds", d.DocumentPresentationSeconds, 1, 8),
                CitizenFleeChancePercent = LSConfig.Int(n, "citizenFleeChancePercent", d.CitizenFleeChancePercent, 0, 100),
                CitizenResistChancePercent = LSConfig.Int(n, "citizenResistChancePercent", d.CitizenResistChancePercent, 0, 100),
                SuspiciousEncounterChancePercent = LSConfig.Int(n, "suspiciousEncounterChancePercent", d.SuspiciousEncounterChancePercent, 0, 100),
                CompliantKneelChancePercent = LSConfig.Int(n, "compliantKneelChancePercent", d.CompliantKneelChancePercent, 0, 100),
                DeadSubjectReleaseDistance = LSConfig.Int(n, "deadSubjectReleaseDistance", d.DeadSubjectReleaseDistance, 40, 250),
                TrafficFleeChancePercent = LSConfig.Int(n, "trafficFleeChancePercent", d.TrafficFleeChancePercent, 0, 100),
                PullOverTimeoutSeconds = LSConfig.Int(n, "pullOverTimeoutSeconds", d.PullOverTimeoutSeconds, 5, 45),
                TrafficTaskRecoverySeconds = LSConfig.Int(n, "trafficTaskRecoverySeconds", d.TrafficTaskRecoverySeconds, 4, 30),
                ScenePedReactionRadius = LSConfig.Int(n, "scenePedReactionRadius", d.ScenePedReactionRadius, 5, 60),
                MaximumAffectedPedestrians = LSConfig.Int(n, "maximumAffectedPedestrians", d.MaximumAffectedPedestrians, 0, 12),
                SceneReactionCooldownSeconds = LSConfig.Int(n, "sceneReactionCooldownSeconds", d.SceneReactionCooldownSeconds, 5, 120)
            };
        }

        internal void Normalize()
        {
            InteractionRadius = LSConfig.Clamp(InteractionRadius, 1, 25);
            TrafficAwarenessRadius = LSConfig.Clamp(TrafficAwarenessRadius, 1, 120);
            CollisionGuardCooldownMilliseconds = LSConfig.Clamp(CollisionGuardCooldownMilliseconds, 0, 10000);
            MaximumAffectedTrafficVehicles = LSConfig.Clamp(MaximumAffectedTrafficVehicles, 0, 20);
            InteractionTimeoutSeconds = LSConfig.Clamp(InteractionTimeoutSeconds, 20, 300);
            DocumentPresentationSeconds = LSConfig.Clamp(DocumentPresentationSeconds, 1, 8);
            CitizenFleeChancePercent = LSConfig.Clamp(CitizenFleeChancePercent, 0, 100);
            CitizenResistChancePercent = LSConfig.Clamp(CitizenResistChancePercent, 0, 100);
            SuspiciousEncounterChancePercent = LSConfig.Clamp(SuspiciousEncounterChancePercent, 0, 100);
            CompliantKneelChancePercent = LSConfig.Clamp(CompliantKneelChancePercent, 0, 100);
            DeadSubjectReleaseDistance = LSConfig.Clamp(DeadSubjectReleaseDistance, 40, 250);
            TrafficFleeChancePercent = LSConfig.Clamp(TrafficFleeChancePercent, 0, 100);
            PullOverTimeoutSeconds = LSConfig.Clamp(PullOverTimeoutSeconds, 5, 45);
            TrafficTaskRecoverySeconds = LSConfig.Clamp(TrafficTaskRecoverySeconds, 4, 30);
            ScenePedReactionRadius = LSConfig.Clamp(ScenePedReactionRadius, 5, 60);
            MaximumAffectedPedestrians = LSConfig.Clamp(MaximumAffectedPedestrians, 0, 12);
            SceneReactionCooldownSeconds = LSConfig.Clamp(SceneReactionCooldownSeconds, 5, 120);
        }

        internal XElement ToXml()
        {
            return new XElement("NpcResponse",
                new XAttribute("enabled", Enabled),
                new XAttribute("interactionsEnabled", InteractionsEnabled),
                new XAttribute("interactionRadius", InteractionRadius),
                new XAttribute("trafficAwarenessRadius", TrafficAwarenessRadius),
                new XAttribute("collisionGuardCooldownMilliseconds", CollisionGuardCooldownMilliseconds),
                new XAttribute("maximumAffectedTrafficVehicles", MaximumAffectedTrafficVehicles),
                new XAttribute("interactionTimeoutSeconds", InteractionTimeoutSeconds),
                new XAttribute("documentPresentationSeconds", DocumentPresentationSeconds),
                new XAttribute("citizenFleeChancePercent", CitizenFleeChancePercent),
                new XAttribute("citizenResistChancePercent", CitizenResistChancePercent),
                new XAttribute("suspiciousEncounterChancePercent", SuspiciousEncounterChancePercent),
                new XAttribute("compliantKneelChancePercent", CompliantKneelChancePercent),
                new XAttribute("deadSubjectReleaseDistance", DeadSubjectReleaseDistance),
                new XAttribute("trafficFleeChancePercent", TrafficFleeChancePercent),
                new XAttribute("pullOverTimeoutSeconds", PullOverTimeoutSeconds),
                new XAttribute("trafficTaskRecoverySeconds", TrafficTaskRecoverySeconds),
                new XAttribute("scenePedReactionRadius", ScenePedReactionRadius),
                new XAttribute("maximumAffectedPedestrians", MaximumAffectedPedestrians),
                new XAttribute("sceneReactionCooldownSeconds", SceneReactionCooldownSeconds));
        }
    }

    internal sealed class LSPoliceTrafficSettings
    {
        internal bool Enabled { get; set; }
        internal bool StopTrafficAtActiveScenes { get; set; }
        internal int SceneControlRadius { get; set; }
        internal static LSPoliceTrafficSettings Default() { return new LSPoliceTrafficSettings { Enabled = true, StopTrafficAtActiveScenes = true, SceneControlRadius = 35 }; }
        internal static LSPoliceTrafficSettings Read(XElement n, LSPoliceTrafficSettings f) { LSPoliceTrafficSettings d = f ?? Default(); return n == null ? d : new LSPoliceTrafficSettings { Enabled = LSConfig.Bool(n, "enabled", d.Enabled), StopTrafficAtActiveScenes = LSConfig.Bool(n, "stopTrafficAtActiveScenes", d.StopTrafficAtActiveScenes), SceneControlRadius = LSConfig.Int(n, "sceneControlRadius", d.SceneControlRadius, 5, 150) }; }
        internal void Normalize() { SceneControlRadius = LSConfig.Clamp(SceneControlRadius, 5, 150); }
        internal XElement ToXml() { return new XElement("Traffic", new XAttribute("enabled", Enabled), new XAttribute("stopTrafficAtActiveScenes", StopTrafficAtActiveScenes), new XAttribute("sceneControlRadius", SceneControlRadius)); }
    }

    internal sealed class LSPoliceResponseSettings
    {
        internal bool Enabled { get; set; }
        internal bool AllowBackup { get; set; }
        internal bool AutomaticPursuitSupport { get; set; }
        internal int MaximumUnitsPerIncident { get; set; }
        internal bool AuthoritySupportEnabled { get; set; }
        internal bool AuthorityGreetingEnabled { get; set; }
        internal int AuthoritySupportRadius { get; set; }
        internal int AuthoritySupportScanMilliseconds { get; set; }
        internal int MaximumAuthoritySupportPeds { get; set; }
        internal static LSPoliceResponseSettings Default() { return new LSPoliceResponseSettings { Enabled = true, AllowBackup = true, AutomaticPursuitSupport = true, MaximumUnitsPerIncident = 1, AuthoritySupportEnabled = true, AuthorityGreetingEnabled = true, AuthoritySupportRadius = 60, AuthoritySupportScanMilliseconds = 1250, MaximumAuthoritySupportPeds = 3 }; }
        internal static LSPoliceResponseSettings Read(XElement n, LSPoliceResponseSettings f) { LSPoliceResponseSettings d = f ?? Default(); return n == null ? d : new LSPoliceResponseSettings { Enabled = LSConfig.Bool(n, "enabled", d.Enabled), AllowBackup = LSConfig.Bool(n, "allowBackup", d.AllowBackup), AutomaticPursuitSupport = LSConfig.Bool(n, "automaticPursuitSupport", d.AutomaticPursuitSupport), MaximumUnitsPerIncident = LSConfig.Int(n, "maximumUnitsPerIncident", d.MaximumUnitsPerIncident, 0, 5), AuthoritySupportEnabled = LSConfig.Bool(n, "authoritySupportEnabled", d.AuthoritySupportEnabled), AuthorityGreetingEnabled = LSConfig.Bool(n, "authorityGreetingEnabled", d.AuthorityGreetingEnabled), AuthoritySupportRadius = LSConfig.Int(n, "authoritySupportRadius", d.AuthoritySupportRadius, 10, 120), AuthoritySupportScanMilliseconds = LSConfig.Int(n, "authoritySupportScanMilliseconds", d.AuthoritySupportScanMilliseconds, 250, 10000), MaximumAuthoritySupportPeds = LSConfig.Int(n, "maximumAuthoritySupportPeds", d.MaximumAuthoritySupportPeds, 0, 12) }; }
        internal void Normalize() { MaximumUnitsPerIncident = LSConfig.Clamp(MaximumUnitsPerIncident, 0, 5); AuthoritySupportRadius = LSConfig.Clamp(AuthoritySupportRadius, 10, 120); AuthoritySupportScanMilliseconds = LSConfig.Clamp(AuthoritySupportScanMilliseconds, 250, 10000); MaximumAuthoritySupportPeds = LSConfig.Clamp(MaximumAuthoritySupportPeds, 0, 12); }
        internal XElement ToXml() { return new XElement("Response", new XAttribute("enabled", Enabled), new XAttribute("allowBackup", AllowBackup), new XAttribute("automaticPursuitSupport", AutomaticPursuitSupport), new XAttribute("maximumUnitsPerIncident", MaximumUnitsPerIncident), new XAttribute("authoritySupportEnabled", AuthoritySupportEnabled), new XAttribute("authorityGreetingEnabled", AuthorityGreetingEnabled), new XAttribute("authoritySupportRadius", AuthoritySupportRadius), new XAttribute("authoritySupportScanMilliseconds", AuthoritySupportScanMilliseconds), new XAttribute("maximumAuthoritySupportPeds", MaximumAuthoritySupportPeds)); }
    }

    /// <summary>
    /// Settings for the player-requested LSPDBackUp owner.  They are deliberately
    /// separate from Response because a local authority de-escalation scan is
    /// not the same thing as a staged, travelling support force.
    /// </summary>
    internal sealed class LSPoliceBackupSettings
    {
        internal bool Enabled { get; set; }
        internal bool AutomaticGroupSupport { get; set; }
        internal bool PreferSavedFavoritePed { get; set; }
        internal int UnitsPerRequest { get; set; }
        internal int OfficersPerUnit { get; set; }
        internal int MinimumStagingDistance { get; set; }
        internal int ArrivalRadius { get; set; }
        internal int InterceptionLeadDistance { get; set; }
        internal int StandDownSeconds { get; set; }

        internal static LSPoliceBackupSettings Default()
        {
            return new LSPoliceBackupSettings
            {
                Enabled = true,
                AutomaticGroupSupport = false,
                PreferSavedFavoritePed = true,
                UnitsPerRequest = 2,
                OfficersPerUnit = 2,
                MinimumStagingDistance = 70,
                ArrivalRadius = 22,
                InterceptionLeadDistance = 90,
                StandDownSeconds = 35
            };
        }

        internal static LSPoliceBackupSettings Read(
            XElement node,
            LSPoliceBackupSettings fallback)
        {
            LSPoliceBackupSettings d = fallback ?? Default();
            return node == null ? d : new LSPoliceBackupSettings
            {
                Enabled = LSConfig.Bool(node, "enabled", d.Enabled),
                AutomaticGroupSupport = LSConfig.Bool(node, "automaticGroupSupport", d.AutomaticGroupSupport),
                PreferSavedFavoritePed = LSConfig.Bool(node, "preferSavedFavoritePed", d.PreferSavedFavoritePed),
                UnitsPerRequest = LSConfig.Int(node, "unitsPerRequest", d.UnitsPerRequest, 1, 4),
                OfficersPerUnit = LSConfig.Int(node, "officersPerUnit", d.OfficersPerUnit, 1, 2),
                MinimumStagingDistance = LSConfig.Int(node, "minimumStagingDistance", d.MinimumStagingDistance, 35, 250),
                ArrivalRadius = LSConfig.Int(node, "arrivalRadius", d.ArrivalRadius, 8, 60),
                InterceptionLeadDistance = LSConfig.Int(node, "interceptionLeadDistance", d.InterceptionLeadDistance, 25, 300),
                StandDownSeconds = LSConfig.Int(node, "standDownSeconds", d.StandDownSeconds, 5, 300)
            };
        }

        internal void Normalize()
        {
            UnitsPerRequest = LSConfig.Clamp(UnitsPerRequest, 1, 4);
            OfficersPerUnit = LSConfig.Clamp(OfficersPerUnit, 1, 2);
            MinimumStagingDistance = LSConfig.Clamp(MinimumStagingDistance, 35, 250);
            ArrivalRadius = LSConfig.Clamp(ArrivalRadius, 8, 60);
            InterceptionLeadDistance = LSConfig.Clamp(InterceptionLeadDistance, 25, 300);
            StandDownSeconds = LSConfig.Clamp(StandDownSeconds, 5, 300);
        }

        internal XElement ToXml()
        {
            return new XElement(
                "Backup",
                new XAttribute("enabled", Enabled),
                new XAttribute("automaticGroupSupport", AutomaticGroupSupport),
                new XAttribute("preferSavedFavoritePed", PreferSavedFavoritePed),
                new XAttribute("unitsPerRequest", UnitsPerRequest),
                new XAttribute("officersPerUnit", OfficersPerUnit),
                new XAttribute("minimumStagingDistance", MinimumStagingDistance),
                new XAttribute("arrivalRadius", ArrivalRadius),
                new XAttribute("interceptionLeadDistance", InterceptionLeadDistance),
                new XAttribute("standDownSeconds", StandDownSeconds));
        }
    }

    internal sealed class LSPoliceConvoySettings
    {
        internal bool Enabled { get; set; }
        internal bool TerminalCompletionEnabled { get; set; }
        // A player-requested Convoy is a separate custody activity. It is not
        // an automatic Dispatch producer and can be switched off independently
        // when only normal post-arrest transport is wanted.
        internal bool RequestedConvoyEnabled { get; set; }
        internal bool RequestedConvoyRouteThreatEnabled { get; set; }
        internal int StationArrivalRadius { get; set; }
        internal int PrisonArrivalRadius { get; set; }
        internal int PrisonerRecoveryTimeoutSeconds { get; set; }
        internal int MinimumStagingDistance { get; set; }
        internal int RequestedRouteThreatDistance { get; set; }
        internal int RequestedRouteThreatCount { get; set; }

        internal static LSPoliceConvoySettings Default()
        {
            return new LSPoliceConvoySettings
            {
                Enabled = true,
                TerminalCompletionEnabled = true,
                RequestedConvoyEnabled = true,
                RequestedConvoyRouteThreatEnabled = true,
                StationArrivalRadius = 24,
                PrisonArrivalRadius = 32,
                PrisonerRecoveryTimeoutSeconds = 45,
                MinimumStagingDistance = 70,
                RequestedRouteThreatDistance = 140,
                RequestedRouteThreatCount = 2
            };
        }

        internal static LSPoliceConvoySettings Read(XElement node, LSPoliceConvoySettings fallback)
        {
            LSPoliceConvoySettings d = fallback ?? Default();
            if (node == null)
                return d;
            LSPoliceConvoySettings result = new LSPoliceConvoySettings
            {
                Enabled = LSConfig.Bool(node, "enabled", d.Enabled),
                TerminalCompletionEnabled = LSConfig.Bool(node, "terminalCompletionEnabled", d.TerminalCompletionEnabled),
                RequestedConvoyEnabled = LSConfig.Bool(node, "requestedConvoyEnabled", d.RequestedConvoyEnabled),
                RequestedConvoyRouteThreatEnabled = LSConfig.Bool(node, "requestedConvoyRouteThreatEnabled", d.RequestedConvoyRouteThreatEnabled),
                StationArrivalRadius = LSConfig.Int(node, "stationArrivalRadius", d.StationArrivalRadius, 4, 100),
                PrisonArrivalRadius = LSConfig.Int(node, "prisonArrivalRadius", d.PrisonArrivalRadius, 4, 100),
                PrisonerRecoveryTimeoutSeconds = LSConfig.Int(node, "prisonerRecoveryTimeoutSeconds", d.PrisonerRecoveryTimeoutSeconds, 5, 600),
                MinimumStagingDistance = LSConfig.Int(node, "minimumStagingDistance", d.MinimumStagingDistance, 35, 250),
                RequestedRouteThreatDistance = LSConfig.Int(node, "requestedRouteThreatDistance", d.RequestedRouteThreatDistance, 75, 400),
                RequestedRouteThreatCount = LSConfig.Int(node, "requestedRouteThreatCount", d.RequestedRouteThreatCount, 1, 3)
            };
            result.Normalize();
            return result;
        }

        internal void Normalize()
        {
            StationArrivalRadius = LSConfig.Clamp(StationArrivalRadius, 4, 100);
            PrisonArrivalRadius = LSConfig.Clamp(PrisonArrivalRadius, 4, 100);
            PrisonerRecoveryTimeoutSeconds = LSConfig.Clamp(PrisonerRecoveryTimeoutSeconds, 5, 600);
            MinimumStagingDistance = LSConfig.Clamp(MinimumStagingDistance, 35, 250);
            RequestedRouteThreatDistance = LSConfig.Clamp(RequestedRouteThreatDistance, 75, 400);
            RequestedRouteThreatCount = LSConfig.Clamp(RequestedRouteThreatCount, 1, 3);
        }

        internal XElement ToXml()
        {
            return new XElement(
                "Convoy",
                new XAttribute("enabled", Enabled),
                new XAttribute("terminalCompletionEnabled", TerminalCompletionEnabled),
                new XAttribute("requestedConvoyEnabled", RequestedConvoyEnabled),
                new XAttribute("requestedConvoyRouteThreatEnabled", RequestedConvoyRouteThreatEnabled),
                new XAttribute("stationArrivalRadius", StationArrivalRadius),
                new XAttribute("prisonArrivalRadius", PrisonArrivalRadius),
                new XAttribute("prisonerRecoveryTimeoutSeconds", PrisonerRecoveryTimeoutSeconds),
                new XAttribute("minimumStagingDistance", MinimumStagingDistance),
                new XAttribute("requestedRouteThreatDistance", RequestedRouteThreatDistance),
                new XAttribute("requestedRouteThreatCount", RequestedRouteThreatCount));
        }
    }

    internal sealed class LSPoliceCleanupSettings
    {
        internal int CompletedSceneGraceSeconds { get; set; }
        internal int HardCleanupSeconds { get; set; }
        internal int SafeCleanupDistance { get; set; }
        internal static LSPoliceCleanupSettings Default()
        {
            return new LSPoliceCleanupSettings
            {
                CompletedSceneGraceSeconds = 90,
                HardCleanupSeconds = 900,
                SafeCleanupDistance = 200
            };
        }
        internal static LSPoliceCleanupSettings Read(XElement n, LSPoliceCleanupSettings f)
        {
            LSPoliceCleanupSettings d = f ?? Default();
            if (n == null) return d;
            LSPoliceCleanupSettings r = new LSPoliceCleanupSettings
            {
                CompletedSceneGraceSeconds = LSConfig.Int(n, "completedSceneGraceSeconds", d.CompletedSceneGraceSeconds, 0, 3600),
                HardCleanupSeconds = LSConfig.Int(n, "hardCleanupSeconds", d.HardCleanupSeconds, 30, 7200),
                SafeCleanupDistance = LSConfig.Int(n, "safeCleanupDistance", d.SafeCleanupDistance, 50, 1000)
            };
            r.Normalize();
            return r;
        }
        internal void Normalize()
        {
            CompletedSceneGraceSeconds = LSConfig.Clamp(CompletedSceneGraceSeconds, 0, 3600);
            HardCleanupSeconds = LSConfig.Clamp(HardCleanupSeconds, CompletedSceneGraceSeconds + 30, 7200);
            SafeCleanupDistance = LSConfig.Clamp(SafeCleanupDistance, 50, 1000);
        }
        internal XElement ToXml()
        {
            return new XElement("Cleanup",
                new XAttribute("completedSceneGraceSeconds", CompletedSceneGraceSeconds),
                new XAttribute("hardCleanupSeconds", HardCleanupSeconds),
                new XAttribute("safeCleanupDistance", SafeCleanupDistance));
        }
    }

    internal static class LSConfig
    {
        internal static bool Bool(XElement node, string name, bool fallback)
        {
            bool value;
            return node != null && bool.TryParse((string)node.Attribute(name), out value) ? value : fallback;
        }
        internal static int Int(XElement node, string name, int fallback, int minimum, int maximum)
        {
            int value;
            return node != null && int.TryParse((string)node.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? Clamp(value, minimum, maximum) : fallback;
        }
        internal static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
