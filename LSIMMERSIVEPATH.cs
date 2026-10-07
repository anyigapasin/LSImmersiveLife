using System;
using System.IO;
using System.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Central runtime path collection for LS Immersive Life.
    /// It derives all persistent project paths from the GTA V Enhanced scripts
    /// directory and never creates a directory tree by itself.
    /// </summary>
    internal sealed class LSIMMERSIVEPATH
    {
        internal string BaseDirectory { get; private set; }
        internal string ScriptsDirectory { get; private set; }
        internal string LSImmersiveDirectory { get; private set; }
        internal string AudioDirectory { get; private set; }
        internal string XmlDirectory { get; private set; }
        internal string LogDirectory { get; private set; }
        internal string PluginDirectory { get; private set; }
        internal string DeveloperXmlPath { get; private set; }
        internal string DeveloperBranchesXmlPath { get; private set; }
        internal string RuntimeLogPath { get; private set; }
        internal string DebugLogPath { get; private set; }
        internal string DeveloperTraceLogPath { get; private set; }
        internal string MainUiXmlPath { get; private set; }
        internal string ResponseAudioXmlPath { get; private set; }
        internal string ResponseAudioXmlFallbackPath { get; private set; }
        internal string PoliceProfileXmlPath { get; private set; }
        internal string DispatchEventXmlPath { get; private set; }
        internal string CrimeActivityEventXmlPath { get; private set; }
        internal string CriminalProfileXmlPath { get; private set; }
        internal string NpcDatabaseXmlPath { get; private set; }
        internal string LocationCatalogXmlPath { get; private set; }

        // These paths describe the Police data contracts requested for the
        // mapping foundation. They stay below the one LSImmersive directory so
        // profile data cannot create another scripts tree. Profile catalogs are
        // read through these paths; they never apply gameplay by themselves.
        internal string PoliceUtilityXmlPath { get; private set; }
        internal string PoliceWeaponDataXmlPath { get; private set; }
        internal string PoliceModelPedXmlPath { get; private set; }
        internal string PoliceVehicleXmlPath { get; private set; }
        internal string PolicePersonalWeaponXmlPath { get; private set; }
        internal string GangDataXmlPath { get; private set; }
        internal string GangMemberPoolXmlPath { get; private set; }
        internal string GangTurfZoneXmlPath { get; private set; }
        internal string ExternalGangDataDirectory { get; private set; }
        internal string ExternalGangDataXmlPath { get; private set; }
        internal string ExternalGangMemberPoolXmlPath { get; private set; }
        internal string ExternalGangTurfZoneXmlPath { get; private set; }

        /*
         Runtime inventory and ownership map for the supplied project inventory.
         The original supplied inventory described 35 files; the workspace had
         34 actual project files because LSPDStation.cs is currently absent. This
         controlled revision adds the five explicitly requested Police data
         contracts, so the tracked project inventory becomes 39 files. The
         missing LSPDStation.cs source is documented below rather than fabricated.

         Universal framework and UI files:
         - LSIMMERSIVEUI.cs: universal root menu, LemonUI pool, and navigation.
         - LSRoleplay.cs: Citizen, Police Authority, and Gang Leader selection.
         - LSAuthorityRole.cs: contextual route into the selected role authority.
         - LSImmersivePoliceUI.cs: Police Authority presentation boundary.
         - LSImmersivePoliceCore.cs: controlled Police core connection boundary.
         - LSMainSetting.cs: separate universal Settings UI boundary.
         - LSImmersiveHotkeys.cs: separate Customization and hotkey boundary.
         - LSImmersiveDeveloper.cs: separate Developer UI boundary.
         - LSImmersiveMainConfig.cs: central configuration loading/saving owner.
         - LSImmersiveLog.cs: the two event/diagnostic log writers.
         - LSIMMERSIVEPATH.cs: this centralized runtime path map.

         Existing Police and integration files:
         - LSPDAuthority.cs and LSPDProfile.cs: Police authority and profile
           boundaries. The Police Stations UI category is present in the mapping
           layer, but no current LSPDStation.cs source file exists in this
           workspace yet, so it is not invented or loaded in this revision.
         - LSPDPatrol.cs, LSPDPoliceResponse.cs, and LSPDNPCResponse.cs: patrol,
           response, and NPC-response boundaries.
         - LSPDDispatch.cs, LSPDDispatchEvent.cs, and LSPDCrimeActivity.cs:
           dispatch and crime-activity boundaries.
         - LSPDCrimeActivityEvent.cs, LSPDConvoy.cs, and LSPDAudioDispatch.cs:
           event, convoy/transport, and response-audio boundaries.
         - LSPDGangDataIntegration.cs, LSPDVanillaGangResponse.cs, and
           LSPDGangAdapterResponse.cs: existing gang/external integration points.

         Runtime XML assets:
         - LSImmersiveMainUI.xml: current universal control configuration.
         - LSImmersiveLocation.xml: shared candidate-only world-location catalog
           in Plugin; it does not replace Police stations/utility ownership.
         - LSPDImmersiveProfile.xml: Police profile data.
         - LSPDDispatchEvent.xml: dispatch event data.
         - LSPDCrimeActivityEvent.xml: crime activity event data.
         - LSPDCriminalProfile.xml: criminal profile data.
         - ResponseAudio.xml: response-audio data.

         Police data-contract templates added for the future profile/data pass:
         - LSPDImmersiveUtility.xml: agency, station, location, and coordination
           references that do not belong in the profile document.
         - LSPoliceWeaponData.xml: named Police weapon data.
         - LSPoliceModelPed.xml: named Police ped-model data.
         - LSPoliceVehicle.xml: named Police vehicle-model data.
         - LSPolicePersonalWeapons.xml: an optional personal weapon reload set.

         Build/project files:
         - LSImmersiveLife.csproj, LSImmersiveLife.sln, and
           Properties\AssemblyInfo.cs: build and assembly metadata.

         Only the XML assets and persistent log files receive runtime paths below.
         The C# files are compiled into LSImmersiveLife.dll, so assigning them
         filesystem paths here would be misleading and would not connect them to
         GTA V at runtime. Each future subsystem can receive the relevant path
         property when its XML configuration is actually implemented.
        */


        internal LSIMMERSIVEPATH()
        {
            // SHVDN may report scripts, scripts\LSImmersiveLife, or an older
            // scripts\scripts location as the AppDomain base. Walk upward to
            // the real scripts root and collapse adjacent duplicate segments;
            // only a non-runtime development fallback appends one scripts child.
            DirectoryInfo runtimeDirectory =
                new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            DirectoryInfo scriptsRoot = runtimeDirectory;

            while (scriptsRoot != null
                && !string.Equals(scriptsRoot.Name, "scripts",
                    StringComparison.OrdinalIgnoreCase))
            {
                scriptsRoot = scriptsRoot.Parent;
            }

            while (scriptsRoot != null
                && scriptsRoot.Parent != null
                && string.Equals(scriptsRoot.Parent.Name, "scripts",
                    StringComparison.OrdinalIgnoreCase))
            {
                scriptsRoot = scriptsRoot.Parent;
            }

            if (scriptsRoot != null)
            {
                ScriptsDirectory = scriptsRoot.FullName;
                BaseDirectory = scriptsRoot.Parent == null
                    ? scriptsRoot.FullName
                    : scriptsRoot.Parent.FullName;
            }
            else
            {
                BaseDirectory = runtimeDirectory.FullName;
                ScriptsDirectory = Path.Combine(BaseDirectory, "scripts");
            }

            // LS Immersive's persistent files stay in one child directory under
            // scripts. This class only describes the path; the logger creates
            // the one allowed Log child when it is initialized.
            LSImmersiveDirectory = Path.Combine(
                ScriptsDirectory,
                "LSImmersiveLife");

            AudioDirectory = Path.Combine(LSImmersiveDirectory, "Audio");
            XmlDirectory = Path.Combine(LSImmersiveDirectory, "XML");
            LogDirectory = Path.Combine(LSImmersiveDirectory, "Log");
            PluginDirectory = Path.Combine(LSImmersiveDirectory, "Plugin");
            DeveloperXmlPath = Path.Combine(PluginDirectory, "LSDeveloper.xml");
            DeveloperBranchesXmlPath = Path.Combine(PluginDirectory, "LSDeveloperBranches.xml");
            LocationCatalogXmlPath = Path.Combine(
                PluginDirectory, "LSImmersiveLocation.xml");
            RuntimeLogPath = Path.Combine(LogDirectory, "LSRuntime.log");
            DebugLogPath = Path.Combine(LogDirectory, "LSDebug.log");
            DeveloperTraceLogPath = Path.Combine(LogDirectory, "LSDeveloperTrace.log");

            // Runtime XML files are loaded from the real scripts directory.
            // These properties are a shared connection map only; each owning
            // subsystem remains responsible for parsing its own configuration.
            MainUiXmlPath = Path.Combine(
                ScriptsDirectory,
                "LSImmersiveMainUI.xml");

            ResponseAudioXmlPath = Path.Combine(
                LSImmersiveDirectory,
                "ResponseAudio.xml");
            ResponseAudioXmlFallbackPath = Path.Combine(
                XmlDirectory,
                "ResponseAudio.xml");

            PoliceProfileXmlPath = Path.Combine(
                XmlDirectory,
                "LSPDImmersiveProfile.xml");

            DispatchEventXmlPath = Path.Combine(
                XmlDirectory,
                "LSPDDispatchEvent.xml");

            CrimeActivityEventXmlPath = Path.Combine(
                XmlDirectory,
                "LSPDCrimeActivityEvent.xml");

            CriminalProfileXmlPath = Path.Combine(
                XmlDirectory,
                "LSPDCriminalProfile.xml");

            NpcDatabaseXmlPath = Path.Combine(
                XmlDirectory,
                "LSNPCDatabase.xml");

            // Future Police data contracts live inside the already-derived
            // LSImmersive directory. Keeping these paths here gives the Police
            // core/profile boundary one authoritative map without making the UI
            // guess filenames or forcing the current pass to parse gameplay data.
            PoliceUtilityXmlPath = Path.Combine(
                XmlDirectory,
                "LSPDImmersiveUtility.xml");

            PoliceWeaponDataXmlPath = Path.Combine(
                XmlDirectory,
                "LSPoliceWeaponData.xml");

            PoliceModelPedXmlPath = Path.Combine(
                XmlDirectory,
                "LSPoliceModelPed.xml");

            PoliceVehicleXmlPath = Path.Combine(
                XmlDirectory,
                "LSPoliceVehicle.xml");

            PolicePersonalWeaponXmlPath = Path.Combine(
                XmlDirectory,
                "LSPolicePersonalWeapons.xml");

            // Gang & Turf remains an external, read-only data source. These
            // optional paths stay in the existing Plugin directory so the
            // Police system never copies, rewrites, or creates a second gang
            // data tree. The gang owner may place its current exports here.
            GangDataXmlPath = Path.Combine(PluginDirectory, "GangData.xml");
            GangMemberPoolXmlPath = Path.Combine(PluginDirectory, "MemberPool.xml");
            GangTurfZoneXmlPath = Path.Combine(PluginDirectory, "TurfZoneData.xml");
            ExternalGangDataDirectory = Path.Combine(BaseDirectory, "gangModData");
            ExternalGangDataXmlPath = Path.Combine(ExternalGangDataDirectory, "GangData.xml");
            ExternalGangMemberPoolXmlPath = Path.Combine(ExternalGangDataDirectory, "MemberPool.xml");
            ExternalGangTurfZoneXmlPath = Path.Combine(ExternalGangDataDirectory, "TurfZoneData.xml");
        }

        // Isolated source tests use a temporary GTA-like root.  Keeping this
        // explicit overload separate from the runtime constructor lets tests
        // exercise the same path contract without changing AppDomain state or
        // creating files in the installed game directory.
        internal LSIMMERSIVEPATH(string developmentBase)
            : this()
        {
            if (string.IsNullOrWhiteSpace(developmentBase))
                throw new ArgumentException("A development base directory is required.", "developmentBase");
            if (!Path.IsPathRooted(developmentBase))
                throw new ArgumentException("The development base directory must be absolute.", "developmentBase");
            BaseDirectory = Path.GetFullPath(developmentBase);
            ScriptsDirectory = Path.Combine(BaseDirectory, "scripts");
            LSImmersiveDirectory = Path.Combine(ScriptsDirectory, "LSImmersiveLife");
            AudioDirectory = Path.Combine(LSImmersiveDirectory, "Audio");
            XmlDirectory = Path.Combine(LSImmersiveDirectory, "XML");
            LogDirectory = Path.Combine(LSImmersiveDirectory, "Log");
            PluginDirectory = Path.Combine(LSImmersiveDirectory, "Plugin");
            DeveloperXmlPath = Path.Combine(PluginDirectory, "LSDeveloper.xml");
            DeveloperBranchesXmlPath = Path.Combine(PluginDirectory, "LSDeveloperBranches.xml");
            LocationCatalogXmlPath = Path.Combine(
                PluginDirectory, "LSImmersiveLocation.xml");
            RuntimeLogPath = Path.Combine(LogDirectory, "LSRuntime.log");
            DebugLogPath = Path.Combine(LogDirectory, "LSDebug.log");
            DeveloperTraceLogPath = Path.Combine(LogDirectory, "LSDeveloperTrace.log");
            MainUiXmlPath = Path.Combine(ScriptsDirectory, "LSImmersiveMainUI.xml");
            ResponseAudioXmlPath = Path.Combine(LSImmersiveDirectory, "ResponseAudio.xml");
            ResponseAudioXmlFallbackPath = Path.Combine(XmlDirectory, "ResponseAudio.xml");
            PoliceProfileXmlPath = Path.Combine(XmlDirectory, "LSPDImmersiveProfile.xml");
            DispatchEventXmlPath = Path.Combine(XmlDirectory, "LSPDDispatchEvent.xml");
            CrimeActivityEventXmlPath = Path.Combine(XmlDirectory, "LSPDCrimeActivityEvent.xml");
            CriminalProfileXmlPath = Path.Combine(XmlDirectory, "LSPDCriminalProfile.xml");
            NpcDatabaseXmlPath = Path.Combine(XmlDirectory, "LSNPCDatabase.xml");
            PoliceUtilityXmlPath = Path.Combine(XmlDirectory, "LSPDImmersiveUtility.xml");
            PoliceWeaponDataXmlPath = Path.Combine(XmlDirectory, "LSPoliceWeaponData.xml");
            PoliceModelPedXmlPath = Path.Combine(XmlDirectory, "LSPoliceModelPed.xml");
            PoliceVehicleXmlPath = Path.Combine(XmlDirectory, "LSPoliceVehicle.xml");
            PolicePersonalWeaponXmlPath = Path.Combine(XmlDirectory, "LSPolicePersonalWeapons.xml");
            GangDataXmlPath = Path.Combine(PluginDirectory, "GangData.xml");
            GangMemberPoolXmlPath = Path.Combine(PluginDirectory, "MemberPool.xml");
            GangTurfZoneXmlPath = Path.Combine(PluginDirectory, "TurfZoneData.xml");
            ExternalGangDataDirectory = Path.Combine(BaseDirectory, "gangModData");
            ExternalGangDataXmlPath = Path.Combine(ExternalGangDataDirectory, "GangData.xml");
            ExternalGangMemberPoolXmlPath = Path.Combine(ExternalGangDataDirectory, "MemberPool.xml");
            ExternalGangTurfZoneXmlPath = Path.Combine(ExternalGangDataDirectory, "TurfZoneData.xml");
        }

        /// <summary>
        /// Creates only the project-owned runtime directories. This is an
        /// explicit resource operation, never a side effect of constructing
        /// the path map.
        /// </summary>
        internal void EnsureResourceDirectories()
        {
            Directory.CreateDirectory(LSImmersiveDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(PluginDirectory);
            Directory.CreateDirectory(XmlDirectory);
            Directory.CreateDirectory(AudioDirectory);
        }

        /// <summary>
        /// Resolves a catalog path beneath the single LS Immersive Audio
        /// directory. Absolute paths and directory traversal are rejected so
        /// ResponseAudio.xml cannot redirect playback outside the project.
        /// </summary>
        internal string ResolveAudioFile(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidDataException("Audio path is empty.");

            string normalized = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
                throw new InvalidDataException("Audio paths must be relative.");

            string[] segments = normalized.Split(new[] { Path.DirectorySeparatorChar },
                StringSplitOptions.None);
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."))
                throw new InvalidDataException("Audio paths must contain normal relative segments.");

            string root = Path.GetFullPath(AudioDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(AudioDirectory, normalized));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Audio path is not a valid relative path.", ex);
            }

            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Audio path escapes the project Audio directory.");

            if (!string.Equals(Path.GetExtension(fullPath), ".wav",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only WAV audio assets are supported.");

            return fullPath;
        }
    }
}
