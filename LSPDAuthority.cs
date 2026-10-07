using System;
using System.Xml.Linq;
using GTA;
using GTA.Native;

namespace LSImmersiveLife
{
    /// <summary>
    /// Police Authority session boundary.
    ///
    /// This class owns only the player-scoped transition between normal GTA
    /// behavior and Police Authority behavior. It does not scan, spawn, delete,
    /// or task ambient peds. LSPDPoliceResponse owns the nearby officer support
    /// pass, and PoliceCore coordinates the two owners on the game tick.
    /// </summary>
    internal sealed class LSPDAuthority
    {
        private readonly LSPDProfile _profile;
        private readonly LSImmersiveMainConfig _config;
        private readonly LSImmersiveLog _log;

        private bool _hasPlayerSnapshot;
        private bool _hasWantedSnapshot;
        private int _previousWantedLevel;
        private bool _hasMaximumWantedSnapshot;
        private int _previousMaximumWantedLevel;
        private bool _changedPoliceIgnore;
        private bool _changedDispatchCops;
        private bool _hasPoliceRelationshipSnapshot;
        private int _policePlayerRelationshipGroup;
        private int _policeCopRelationshipGroup;
        private int _previousPlayerToCopRelationship;
        private int _previousCopToPlayerRelationship;
        private bool _changedMaximumWantedLevel;
        private bool _clearedWantedLevel;
        private bool _controlFaulted;
        private int _lastControlCheck = int.MinValue;
        private int _lastWantedCheck = int.MinValue;
        private const int WantedProtectionRefreshMilliseconds = 150;

        internal LSPDAuthority(
            LSPDProfile profile,
            LSImmersiveMainConfig config,
            LSImmersiveLog log)
        {
            _profile = profile ?? throw new ArgumentNullException("profile");
            _config = config ?? throw new ArgumentNullException("config");
            _log = log ?? throw new ArgumentNullException("log");
            Settings = _config.PoliceAuthoritySettings
                ?? LSPDAuthoritySettings.Default();
        }

        internal bool IsActive { get; private set; }
        internal LSPDAuthoritySettings Settings { get; private set; }

        internal bool Enter()
        {
            // Profile loading is data activation only. The native authority
            // controls below are applied from Process on the normal game tick.
            bool loaded = _profile.Activate();
            Settings = _config.PoliceAuthoritySettings
                ?? LSPDAuthoritySettings.Default();
            Settings.Normalize();
            IsActive = true;
            _controlFaulted = false;
            _lastControlCheck = int.MinValue;
            _lastWantedCheck = int.MinValue;
            _log.Runtime(
                "POLICE_AUTHORITY_STATE",
                "Police Authority entered. Player-scoped vanilla Police protection is pending the next game tick.");
            return loaded;
        }

        internal void Exit()
        {
            RestorePlayerControls();
            IsActive = false;
            _profile.Deactivate();
            _log.Runtime(
                "POLICE_AUTHORITY_STATE",
                "Police Authority exited and captured GTA player controls were restored.");
        }

        internal void RefreshSettings()
        {
            Settings = _config.PoliceAuthoritySettings
                ?? LSPDAuthoritySettings.Default();
            Settings.Normalize();
            _controlFaulted = false;
            _lastControlCheck = int.MinValue;
            _lastWantedCheck = int.MinValue;

            // Do not restore all player state merely because a Main Settings
            // row changed. The next Process call reconciles only the changed
            // Authority controls, avoiding a visible wanted-level flicker.
            _log.Runtime(
                "POLICE_AUTHORITY_SETTINGS",
                "Central Police Authority settings refreshed for the active session.");
        }

        internal void Process(bool paused)
        {
            if (!IsActive || paused || _controlFaulted)
                return;

            int now = Environment.TickCount;
            int refreshMilliseconds = Settings.AuthorityControlRefreshMilliseconds;
            bool controlRefreshDue = _lastControlCheck == int.MinValue
                || unchecked(now - _lastControlCheck) < 0
                || unchecked(now - _lastControlCheck) >= refreshMilliseconds;
            bool wantedRefreshDue = _lastWantedCheck == int.MinValue
                || unchecked(now - _lastWantedCheck) < 0
                || unchecked(now - _lastWantedCheck)
                    >= WantedProtectionRefreshMilliseconds;
            if (!controlRefreshDue)
            {
                // Wanted escalation is player-visible and can be created by
                // an unrelated ambient/Gang interaction between the broader
                // Authority control passes. Keep this one native check
                // responsive without increasing the cadence of the other
                // Authority work.
                if (wantedRefreshDue)
                {
                    _lastWantedCheck = now;
                    ReconcileWantedProtectionDuringControlWait();
                }
                return;
            }
            _lastControlCheck = now;
            _lastWantedCheck = now;

            try
            {
                Player player = Game.Player;
                if (player == null || player.Character == null || !player.Character.Exists())
                    return;

                if (!Settings.EnableWorldBehaviorChanges)
                {
                    // Turning Authority world behavior off from Main Settings
                    // restores only this Authority session's player controls.
                    RestorePlayerControls();
                    return;
                }

                CapturePlayerSnapshot(player);
                ReconcilePoliceProtection(player);
                ReconcileWantedProtection(player);
            }
            catch (Exception ex)
            {
                _controlFaulted = true;
                _log.Exception("POLICE_AUTHORITY_CONTROL_FAILED", ex);
                RestorePlayerControls();
            }
        }

        private void CapturePlayerSnapshot(Player player)
        {
            if (_hasPlayerSnapshot)
                return;

            _hasPlayerSnapshot = true;
            _previousWantedLevel = ReadWantedLevel(player);
            _hasWantedSnapshot = true;
            _previousMaximumWantedLevel = ReadMaximumWantedLevel();
            _hasMaximumWantedSnapshot = true;
            _log.Runtime(
                "POLICE_AUTHORITY_CONTROLS",
                "Captured wanted state for this Police Authority session. Wanted="
                + _previousWantedLevel + "; MaximumWanted="
                + _previousMaximumWantedLevel + ".");
        }

        private void ReconcilePoliceProtection(Player player)
        {
            bool protectFromPolice = Settings.ProtectPlayerFromVanillaPoliceEscalation
                || Settings.SuppressAmbientPoliceHostility;
            if (protectFromPolice)
            {
                EnsurePoliceAllyRelationship(player.Character);
                if (!_changedPoliceIgnore)
                {
                    Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player.Handle, true);
                    _changedPoliceIgnore = true;
                    _log.Runtime("POLICE_AUTHORITY_POLICE_IGNORE", "Vanilla Police now ignore the active Police player.");
                }

                if (Settings.SuppressVanillaDispatch && !_changedDispatchCops)
                {
                    Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, player.Handle, false);
                    _changedDispatchCops = true;
                    _log.Runtime("POLICE_AUTHORITY_DISPATCH_SUPPRESSED", "Vanilla Police dispatch disabled for the active Police player.");
                }
            }
            else
            {
                RestorePoliceProtection(player);
            }

            if (!Settings.SuppressVanillaDispatch && _changedDispatchCops)
            {
                Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, player.Handle, true);
                _changedDispatchCops = false;
                _log.Runtime("POLICE_AUTHORITY_DISPATCH_RESTORED", "Vanilla Police dispatch restored by Main Settings.");
            }
        }

        private void ReconcileWantedProtection(Player player)
        {
            if (!Settings.SuppressVanillaWantedEscalation)
            {
                RestoreWantedProtection(player);
                return;
            }

            int maximumWanted = ReadMaximumWantedLevel();
            if (maximumWanted != 0)
            {
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                _changedMaximumWantedLevel = true;
                // Another GTA/plugin system may restore the global cap while
                // Police Authority is active. Keep enforcing the protection,
                // but do not turn that external tug-of-war into synchronous
                // disk I/O every authority refresh.
                _log.RuntimeThrottled(
                    "POLICE_AUTHORITY_WANTED_CAP",
                    "Maximum wanted level temporarily set to zero for Police Authority.",
                    TimeSpan.FromSeconds(30));
            }

            int currentWanted = ReadWantedLevel(player);
            if (currentWanted <= 0)
                return;

            Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, player.Handle);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, player.Handle, false);
            _clearedWantedLevel = true;
            _log.RuntimeThrottled(
                "POLICE_AUTHORITY_WANTED_CLEARED",
                "Cleared vanilla wanted level " + currentWanted + " while Police Authority is active.",
                TimeSpan.FromSeconds(4));
        }

        private void RestorePlayerControls()
        {
            if (!_hasPlayerSnapshot)
                return;

            try
            {
                Player player = Game.Player;
                if (player != null)
                {
                    RestorePoliceProtection(player);
                    RestoreWantedProtection(player);
                }
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_AUTHORITY_RESTORE_FAILED", ex);
            }
            finally
            {
                _hasPlayerSnapshot = false;
                _hasWantedSnapshot = false;
                _previousWantedLevel = 0;
                _hasMaximumWantedSnapshot = false;
                _previousMaximumWantedLevel = 0;
                _changedPoliceIgnore = false;
                _changedDispatchCops = false;
                _changedMaximumWantedLevel = false;
                _clearedWantedLevel = false;
                _lastControlCheck = int.MinValue;
                _lastWantedCheck = int.MinValue;
            }
        }

        private void ReconcileWantedProtectionDuringControlWait()
        {
            if (!Settings.EnableWorldBehaviorChanges
                || !Settings.SuppressVanillaWantedEscalation)
                return;
            try
            {
                Player player = Game.Player;
                if (player == null || player.Character == null
                    || !player.Character.Exists())
                    return;
                ReconcileWantedProtection(player);
            }
            catch (Exception ex)
            {
                // A transient native failure must not disable the entire
                // Police Authority session; the normal control pass will
                // retry the protection on its next scheduled refresh.
                _log.Exception("POLICE_AUTHORITY_WANTED_CHECK_FAILED", ex);
            }
        }

        private void RestorePoliceProtection(Player player)
        {
            RestorePoliceAllyRelationship();
            if (_changedPoliceIgnore)
            {
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player.Handle, false);
                _changedPoliceIgnore = false;
            }
            if (_changedDispatchCops)
            {
                Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, player.Handle, true);
                _changedDispatchCops = false;
            }
        }

        private void EnsurePoliceAllyRelationship(Ped playerPed)
        {
            if (playerPed == null || !playerPed.Exists())
                return;
            try
            {
                int playerGroup = Function.Call<int>(
                    Hash.GET_PED_RELATIONSHIP_GROUP_HASH, playerPed);
                int copGroup = unchecked((int)StringHash.AtStringHash("COP", 0));
                if (copGroup == 0)
                    return;
                if (_hasPoliceRelationshipSnapshot
                    && _policePlayerRelationshipGroup == playerGroup
                    && _policeCopRelationshipGroup == copGroup)
                    return;

                RestorePoliceAllyRelationship();
                _policePlayerRelationshipGroup = playerGroup;
                _policeCopRelationshipGroup = copGroup;
                _previousPlayerToCopRelationship = Function.Call<int>(
                    Hash.GET_RELATIONSHIP_BETWEEN_GROUPS,
                    playerGroup, copGroup);
                _previousCopToPlayerRelationship = Function.Call<int>(
                    Hash.GET_RELATIONSHIP_BETWEEN_GROUPS,
                    copGroup, playerGroup);
                _hasPoliceRelationshipSnapshot = true;
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                    1, playerGroup, copGroup);
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                    1, copGroup, playerGroup);
                _log.Runtime("POLICE_AUTHORITY_POLICE_ALLY_RELATIONSHIP",
                    "PlayerGroup=" + playerGroup + "; CopGroup=" + copGroup
                    + "; Relation=Respect; PreviousPlayerToCop="
                    + _previousPlayerToCopRelationship
                    + "; PreviousCopToPlayer=" + _previousCopToPlayerRelationship);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_AUTHORITY_POLICE_ALLY_RELATIONSHIP_FAILED", ex);
            }
        }

        private void RestorePoliceAllyRelationship()
        {
            if (!_hasPoliceRelationshipSnapshot)
                return;
            try
            {
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                    _previousPlayerToCopRelationship,
                    _policePlayerRelationshipGroup,
                    _policeCopRelationshipGroup);
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                    _previousCopToPlayerRelationship,
                    _policeCopRelationshipGroup,
                    _policePlayerRelationshipGroup);
                _log.Runtime("POLICE_AUTHORITY_POLICE_ALLY_RELATIONSHIP_RESTORED",
                    "PlayerGroup=" + _policePlayerRelationshipGroup
                    + "; CopGroup=" + _policeCopRelationshipGroup);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_AUTHORITY_POLICE_ALLY_RELATIONSHIP_RESTORE_FAILED", ex);
            }
            finally
            {
                _hasPoliceRelationshipSnapshot = false;
                _policePlayerRelationshipGroup = 0;
                _policeCopRelationshipGroup = 0;
                _previousPlayerToCopRelationship = 0;
                _previousCopToPlayerRelationship = 0;
            }
        }

        private void RestoreWantedProtection(Player player)
        {
            if (_changedMaximumWantedLevel && _hasMaximumWantedSnapshot)
            {
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, _previousMaximumWantedLevel);
                _changedMaximumWantedLevel = false;
            }

            if (_clearedWantedLevel && _hasWantedSnapshot
                && _previousWantedLevel > 0)
            {
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL,
                    player.Handle, _previousWantedLevel, false);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, player.Handle, false);
            }
            _clearedWantedLevel = false;
        }

        private static int ReadWantedLevel(Player player)
        {
            return Math.Max(0, Math.Min(5,
                Function.Call<int>(Hash.GET_PLAYER_WANTED_LEVEL, player.Handle)));
        }

        private static int ReadMaximumWantedLevel()
        {
            return Math.Max(0, Math.Min(5,
                Function.Call<int>(Hash.GET_MAX_WANTED_LEVEL)));
        }
    }

    /// <summary>
    /// Main-config-backed controls for the Authority transition. These settings
    /// are persisted only under Police/AuthoritySettings in
    /// LSImmersiveMainUI.xml. They never change Gang, Dispatch, or NPC owner
    /// state directly.
    /// </summary>
    internal sealed class LSPDAuthoritySettings
    {
        internal bool ProtectPlayerFromVanillaPoliceEscalation { get; set; }
        internal bool SuppressVanillaWantedEscalation { get; set; }
        internal bool SuppressAmbientPoliceHostility { get; set; }
        internal bool SuppressVanillaDispatch { get; set; }
        internal bool SuppressMilitaryHostility { get; set; }
        internal int AuthorityControlRefreshMilliseconds { get; set; }
        internal bool EnableGangReactionControl { get; set; }
        internal bool EnableCivilianReactionControl { get; set; }
        internal bool EnableWorldBehaviorChanges { get; set; }
        internal bool UseVerifiedInteriorsOnly { get; set; }

        internal static LSPDAuthoritySettings Default()
        {
            return new LSPDAuthoritySettings
            {
                ProtectPlayerFromVanillaPoliceEscalation = true,
                SuppressVanillaWantedEscalation = true,
                SuppressAmbientPoliceHostility = true,
                SuppressVanillaDispatch = true,
                SuppressMilitaryHostility = true,
                AuthorityControlRefreshMilliseconds = 750,
                EnableGangReactionControl = false,
                EnableCivilianReactionControl = false,
                EnableWorldBehaviorChanges = true,
                UseVerifiedInteriorsOnly = true
            };
        }

        internal static LSPDAuthoritySettings FromXml(XElement node)
        {
            LSPDAuthoritySettings result = Default();
            if (node == null)
                return result;

            result.ProtectPlayerFromVanillaPoliceEscalation = Parse(node, "protectPlayerFromVanillaPoliceEscalation", result.ProtectPlayerFromVanillaPoliceEscalation);
            result.SuppressVanillaWantedEscalation = Parse(node, "suppressVanillaWantedEscalation", result.SuppressVanillaWantedEscalation);
            result.SuppressAmbientPoliceHostility = Parse(node, "suppressAmbientPoliceHostility", result.SuppressAmbientPoliceHostility);
            result.SuppressVanillaDispatch = Parse(node, "suppressVanillaDispatch", result.SuppressVanillaDispatch);
            result.SuppressMilitaryHostility = Parse(node, "suppressMilitaryHostility", result.SuppressMilitaryHostility);
            result.AuthorityControlRefreshMilliseconds = ParseInt(node, "authorityControlRefreshMilliseconds", result.AuthorityControlRefreshMilliseconds, 250, 5000);
            result.EnableGangReactionControl = Parse(node, "enableGangReactionControl", result.EnableGangReactionControl);
            result.EnableCivilianReactionControl = Parse(node, "enableCivilianReactionControl", result.EnableCivilianReactionControl);
            result.EnableWorldBehaviorChanges = Parse(node, "enableWorldBehaviorChanges", result.EnableWorldBehaviorChanges);
            result.UseVerifiedInteriorsOnly = Parse(node, "useVerifiedInteriorsOnly", result.UseVerifiedInteriorsOnly);
            result.Normalize();
            return result;
        }

        internal void Normalize()
        {
            AuthorityControlRefreshMilliseconds = Math.Max(250,
                Math.Min(5000, AuthorityControlRefreshMilliseconds));
        }

        internal XElement ToXml()
        {
            Normalize();
            return new XElement(
                "AuthoritySettings",
                new XAttribute("protectPlayerFromVanillaPoliceEscalation", ProtectPlayerFromVanillaPoliceEscalation),
                new XAttribute("suppressVanillaWantedEscalation", SuppressVanillaWantedEscalation),
                new XAttribute("suppressAmbientPoliceHostility", SuppressAmbientPoliceHostility),
                new XAttribute("suppressVanillaDispatch", SuppressVanillaDispatch),
                new XAttribute("suppressMilitaryHostility", SuppressMilitaryHostility),
                new XAttribute("authorityControlRefreshMilliseconds", AuthorityControlRefreshMilliseconds),
                new XAttribute("enableGangReactionControl", EnableGangReactionControl),
                new XAttribute("enableCivilianReactionControl", EnableCivilianReactionControl),
                new XAttribute("enableWorldBehaviorChanges", EnableWorldBehaviorChanges),
                new XAttribute("useVerifiedInteriorsOnly", UseVerifiedInteriorsOnly));
        }

        private static bool Parse(XElement node, string attribute, bool fallback)
        {
            bool value;
            return bool.TryParse((string)node.Attribute(attribute), out value)
                ? value
                : fallback;
        }

        private static int ParseInt(XElement node, string attribute, int fallback, int minimum, int maximum)
        {
            int value;
            if (!int.TryParse((string)node.Attribute(attribute), out value))
                return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
