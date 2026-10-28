using System;
using System.Collections.Generic;
using System.Linq;
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
        private string _lastPoliceRelationshipRepairSignature = string.Empty;
        private bool _changedMaximumWantedLevel;
        private bool _clearedWantedLevel;
        private int _lastObservedWantedLevel;
        private int _lastObservedMaximumWantedLevel;
        private string _lastWantedCapContextSignature = string.Empty;
        private DateTime _lastWantedCapContextLogAt = DateTime.MinValue;
        private bool _controlFaulted;
        private int _lastControlCheck = int.MinValue;
        private int _lastWantedCheck = int.MinValue;
        private const int WantedProtectionRefreshMilliseconds = 150;
        private const int InteriorDispatchWeaponEvidenceIntervalMilliseconds = 250;
        private const int InteriorDispatchWeaponFailureGraceMilliseconds = 2500;
        private static readonly Control[] InteriorDispatchWeaponControls =
        {
            Control.SelectWeapon,
            Control.WeaponWheelUpDown,
            Control.WeaponWheelLeftRight,
            Control.WeaponWheelNext,
            Control.WeaponWheelPrev,
            Control.SelectNextWeapon,
            Control.SelectPrevWeapon,
            Control.Aim,
            Control.Attack,
            Control.Reload
        };
        private static readonly Control[] InteriorDispatchMeleeControls =
        {
            Control.MeleeAttackLight,
            Control.MeleeAttackHeavy,
            Control.MeleeAttackAlternate,
            Control.MeleeAttack1,
            Control.MeleeAttack2
        };

        private bool _interiorDispatchWeaponAccessActive;
        private bool _hasInteriorDispatchCanSwitchSnapshot;
        private bool _changedInteriorDispatchCanSwitchWeapons;
        private bool _previousInteriorDispatchCanSwitch;
        private bool _interiorDispatchRestrictionDetected;
        private bool _interiorDispatchUnarmedTransitionLogged;
        private bool _interiorDispatchRestrictionRestorationLogged;
        private bool _interiorDispatchAccessFailureLogged;
        private bool _interiorDispatchFailureGraceElapsedLogged;
        private int _interiorDispatchWeaponEvidenceTick = int.MinValue;
        private int _interiorDispatchWeaponRestrictionSinceTick = int.MinValue;
        private int _lastInteriorDispatchWeaponHash = int.MinValue;
        private InteriorDispatchWeaponControlAvailability _lastInteriorDispatchControlsBefore =
            InteriorDispatchWeaponControlAvailability.Unavailable();
        private InteriorDispatchWeaponControlAvailability _lastInteriorDispatchControlsAfter =
            InteriorDispatchWeaponControlAvailability.Unavailable();
        private string _interiorDispatchWeaponOccurrenceId = string.Empty;
        private string _interiorDispatchWeaponLocationId = string.Empty;
        private LSPDDispatchState _interiorDispatchWeaponDispatchState;
        private int _interiorDispatchWeaponSceneInteriorId;
        private int _interiorDispatchWeaponPlayerInteriorId;

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

        internal bool HasInteriorDispatchWeaponAccess
        {
            get { return _interiorDispatchWeaponAccessActive; }
        }

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
            _lastObservedWantedLevel = 0;
            _lastObservedMaximumWantedLevel = 0;
            _lastWantedCapContextSignature = string.Empty;
            _lastWantedCapContextLogAt = DateTime.MinValue;
            _log.Runtime(
                "POLICE_AUTHORITY_STATE",
                "Police Authority entered. Player-scoped vanilla Police protection is pending the next game tick.");
            return loaded;
        }

        internal void Exit()
        {
            RestorePlayerControls(clearPoliceModeWantedLevel: true);
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
            _lastWantedCapContextSignature = string.Empty;
            _lastWantedCapContextLogAt = DateTime.MinValue;

            // Do not restore all player state merely because a Main Settings
            // row changed. The next Process call reconciles only the changed
            // Authority controls, avoiding a visible wanted-level flicker.
            _log.Runtime(
                "POLICE_AUTHORITY_SETTINGS",
                "Central Police Authority settings refreshed for the active session.");
        }

        internal void Process(
            bool paused,
            LSPDDispatchEvent activeIncident,
            bool patrolActive,
            IEnumerable<Ped> gangPedCandidates)
        {
            if (!IsActive || paused)
                return;

            int now = Environment.TickCount;
            bool wantedRefreshDue = _lastWantedCheck == int.MinValue
                || unchecked(now - _lastWantedCheck) < 0
                || unchecked(now - _lastWantedCheck)
                    >= WantedProtectionRefreshMilliseconds;
            if (_controlFaulted)
            {
                // Keep the Police wanted-level suppression independent of a
                // transient failure in another Authority control. The normal
                // control pass remains faulted until settings are refreshed.
                if (wantedRefreshDue)
                {
                    _lastWantedCheck = now;
                    ReconcileWantedProtectionDuringControlWait(
                        activeIncident, patrolActive, gangPedCandidates);
                }
                return;
            }

            int refreshMilliseconds = Settings.AuthorityControlRefreshMilliseconds;
            bool controlRefreshDue = _lastControlCheck == int.MinValue
                || unchecked(now - _lastControlCheck) < 0
                || unchecked(now - _lastControlCheck) >= refreshMilliseconds;
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
                    ReconcileWantedProtectionDuringControlWait(
                        activeIncident, patrolActive, gangPedCandidates);
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
                    // Wanted suppression is a Police-role control, not an
                    // ambient-world behavior toggle. Restore the ordinary
                    // Police reactions while continuing the independently
                    // configured wanted protection.
                    RestorePoliceProtection(player);
                    CapturePlayerSnapshot(player);
                    ReconcileWantedProtection(
                        player, activeIncident, patrolActive, gangPedCandidates);
                    return;
                }

                CapturePlayerSnapshot(player);
                ReconcilePoliceProtection(player);
                ReconcileWantedProtection(
                    player, activeIncident, patrolActive, gangPedCandidates);
            }
            catch (Exception ex)
            {
                _controlFaulted = true;
                _log.Exception("POLICE_AUTHORITY_CONTROL_FAILED", ex);
                RestorePlayerControls();
            }
        }

        /// <summary>
        /// Maintains a temporary Player weapon-control exception for one
        /// already-validated Interior-Type Dispatch occurrence. PoliceCore
        /// supplies the active Dispatch/interior decision; this owner captures
        /// and restores the Player switch capability and reconciles only the
        /// weapon controls for the current frame. It never grants or selects a
        /// weapon.
        /// </summary>
        internal void ProcessInteriorDispatchWeaponAccess(
            bool contextAuthorized,
            Ped playerPed,
            bool paused,
            string occurrenceId,
            LSPDDispatchState dispatchState,
            string locationId,
            int sceneLiveInteriorId,
            int playerLiveInteriorId,
            bool playerInteriorIdentityAvailable,
            string contextDecision)
        {
            bool exactInteriorMatch = playerInteriorIdentityAvailable
                && sceneLiveInteriorId != 0
                && playerLiveInteriorId == sceneLiveInteriorId;
            bool playerAvailable = playerPed != null && playerPed.Exists();
            bool playerAlive = playerAvailable && !playerPed.IsDead;
            bool authorized = IsActive
                && contextAuthorized
                && !paused
                && playerAlive
                && exactInteriorMatch;

            string normalizedOccurrenceId = occurrenceId ?? string.Empty;
            string normalizedLocationId = locationId ?? string.Empty;
            bool sameContext = _interiorDispatchWeaponAccessActive
                && string.Equals(_interiorDispatchWeaponOccurrenceId,
                    normalizedOccurrenceId, StringComparison.Ordinal)
                && _interiorDispatchWeaponSceneInteriorId == sceneLiveInteriorId
                && string.Equals(_interiorDispatchWeaponLocationId,
                    normalizedLocationId, StringComparison.Ordinal);

            if (!authorized)
            {
                RestoreInteriorDispatchWeaponAccess(
                    playerPed,
                    string.IsNullOrWhiteSpace(contextDecision)
                        ? paused ? "GameplayPaused" : "DispatchInteriorAuthorizationEnded"
                        : contextDecision,
                    dispatchState,
                    playerLiveInteriorId,
                    playerInteriorIdentityAvailable);
                return;
            }

            if (!sameContext)
            {
                RestoreInteriorDispatchWeaponAccess(
                    playerPed,
                    "DispatchInteriorContextChanged",
                    dispatchState,
                    playerLiveInteriorId,
                    playerInteriorIdentityAvailable);
                try
                {
                    BeginInteriorDispatchWeaponAccess(
                        playerPed,
                        normalizedOccurrenceId,
                        dispatchState,
                        normalizedLocationId,
                        sceneLiveInteriorId,
                        playerLiveInteriorId);
                }
                catch (Exception ex)
                {
                    if (!_interiorDispatchAccessFailureLogged)
                    {
                        _interiorDispatchAccessFailureLogged = true;
                        _log.Exception(
                            "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_ACCESS_FAILED",
                            ex);
                    }
                    RestoreInteriorDispatchWeaponAccess(
                        playerPed,
                        "WeaponAuthorizationInitializationFailed",
                        dispatchState,
                        playerLiveInteriorId,
                        playerInteriorIdentityAvailable);
                    return;
                }

                // Begin already performs the one-time native reconciliation
                // and captures its evidence. The steady-state check starts on
                // the next frame, avoiding duplicate work on authorization.
                return;
            }

            if (!_interiorDispatchWeaponAccessActive || !playerAvailable)
                return;

            _interiorDispatchWeaponDispatchState = dispatchState;

            try
            {
                int now = Environment.TickCount;
                int previousWeaponHash = _lastInteriorDispatchWeaponHash;
                int currentWeaponHash = Function.Call<int>(
                    Hash.GET_SELECTED_PED_WEAPON, playerPed);
                bool changedToUnarmed = previousWeaponHash != int.MinValue
                    && previousWeaponHash != unchecked((int)WeaponHash.Unarmed)
                    && currentWeaponHash == unchecked((int)WeaponHash.Unarmed);
                bool sampleEvidence = _interiorDispatchWeaponEvidenceTick == int.MinValue
                    || unchecked(now - _interiorDispatchWeaponEvidenceTick) < 0
                    || unchecked(now - _interiorDispatchWeaponEvidenceTick)
                        >= InteriorDispatchWeaponEvidenceIntervalMilliseconds;

                bool canSwitchBefore = playerPed.CanSwitchWeapons;
                bool meleeControlsRequired =
                    IsInteriorDispatchMeleeAccessState(dispatchState);
                InteriorDispatchWeaponControlAvailability controlsBefore =
                    sampleEvidence
                        ? ReadInteriorDispatchWeaponControlAvailability(
                            meleeControlsRequired)
                        : _lastInteriorDispatchControlsBefore;
                int savedWeaponsPresent = -1;
                int savedWeaponsExpected = -1;
                bool inventoryKnown = sampleEvidence
                    && TryCountSavedPoliceWeapons(
                        playerPed, out savedWeaponsPresent, out savedWeaponsExpected);
                if (sampleEvidence)
                {
                    _interiorDispatchWeaponEvidenceTick = now;
                    _lastInteriorDispatchControlsBefore = controlsBefore;
                }

                if (!canSwitchBefore)
                    playerPed.CanSwitchWeapons = true;

                EnableInteriorDispatchWeaponControls();

                bool canSwitchAfter = playerPed.CanSwitchWeapons;
                InteriorDispatchWeaponControlAvailability controlsAfter =
                    sampleEvidence
                        ? ReadInteriorDispatchWeaponControlAvailability(
                            meleeControlsRequired)
                        : _lastInteriorDispatchControlsAfter;
                if (sampleEvidence)
                    _lastInteriorDispatchControlsAfter = controlsAfter;
                bool currentWeaponUnarmed =
                    currentWeaponHash == unchecked((int)WeaponHash.Unarmed);
                bool savedWeaponMissing = inventoryKnown
                    && savedWeaponsExpected > 0
                    && savedWeaponsPresent < savedWeaponsExpected;
                bool restrictionObserved = currentWeaponUnarmed
                    || changedToUnarmed
                    || !canSwitchBefore
                    || (sampleEvidence && !controlsBefore.AllEnabled)
                    || savedWeaponMissing;
                bool firstRestrictionDetected = restrictionObserved
                    && !_interiorDispatchRestrictionDetected;

                if (firstRestrictionDetected)
                {
                    _interiorDispatchRestrictionDetected = true;
                    _interiorDispatchWeaponRestrictionSinceTick = now;
                    LogInteriorDispatchWeaponAccessTransition(
                        "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_RESTRICTION_DETECTED",
                        "FirstRestrictedState",
                        dispatchState,
                        playerLiveInteriorId,
                        true,
                        currentWeaponHash,
                        previousWeaponHash,
                        changedToUnarmed,
                        canSwitchBefore,
                        canSwitchAfter,
                        controlsBefore,
                        controlsAfter,
                        inventoryKnown,
                        savedWeaponsPresent,
                        savedWeaponsExpected,
                        contextDecision);
                }

                if (changedToUnarmed
                    && !_interiorDispatchUnarmedTransitionLogged
                    && !firstRestrictionDetected)
                {
                    _interiorDispatchUnarmedTransitionLogged = true;
                    LogInteriorDispatchWeaponAccessTransition(
                        "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_RESTRICTION_DETECTED",
                        "NativeOrExternalScriptChangedWeaponToUnarmed",
                        dispatchState,
                        playerLiveInteriorId,
                        true,
                        currentWeaponHash,
                        previousWeaponHash,
                        true,
                        canSwitchBefore,
                        canSwitchAfter,
                        controlsBefore,
                        controlsAfter,
                        inventoryKnown,
                        savedWeaponsPresent,
                        savedWeaponsExpected,
                        contextDecision);
                }

                bool controlFailure = sampleEvidence
                    && (!canSwitchAfter || !controlsAfter.AllEnabled);
                if (controlFailure && !_interiorDispatchAccessFailureLogged)
                {
                    _interiorDispatchAccessFailureLogged = true;
                    LogInteriorDispatchWeaponAccessTransition(
                        "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_ACCESS_FAILURE",
                        !canSwitchAfter ? "WeaponSwitchCapabilityNotRestored"
                            : "RequiredControlNotEnabledAfterReconcile",
                        dispatchState,
                        playerLiveInteriorId,
                        true,
                        currentWeaponHash,
                        previousWeaponHash,
                        changedToUnarmed,
                        canSwitchBefore,
                        canSwitchAfter,
                        controlsBefore,
                        controlsAfter,
                        inventoryKnown,
                        savedWeaponsPresent,
                        savedWeaponsExpected,
                        contextDecision);
                }

                if (_interiorDispatchRestrictionDetected
                    && !_interiorDispatchRestrictionRestorationLogged
                    && !currentWeaponUnarmed
                    && canSwitchAfter
                    && (!sampleEvidence || controlsAfter.AllEnabled)
                    && (!inventoryKnown || savedWeaponsPresent >= savedWeaponsExpected))
                {
                    _interiorDispatchRestrictionRestorationLogged = true;
                    LogInteriorDispatchWeaponAccessTransition(
                        "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_RESTRICTION_RESTORED",
                        "PlayerCanSelectSavedLoadoutAgain",
                        dispatchState,
                        playerLiveInteriorId,
                        true,
                        currentWeaponHash,
                        previousWeaponHash,
                        changedToUnarmed,
                        canSwitchBefore,
                        canSwitchAfter,
                        controlsBefore,
                        controlsAfter,
                        inventoryKnown,
                        savedWeaponsPresent,
                        savedWeaponsExpected,
                        contextDecision);
                }

                if (_interiorDispatchRestrictionDetected
                    && !_interiorDispatchAccessFailureLogged
                    && !_interiorDispatchFailureGraceElapsedLogged
                    && _interiorDispatchWeaponRestrictionSinceTick != int.MinValue
                    && unchecked(now - _interiorDispatchWeaponRestrictionSinceTick)
                        >= InteriorDispatchWeaponFailureGraceMilliseconds
                    && (!canSwitchAfter
                        || (sampleEvidence && !controlsAfter.AllEnabled)
                        || savedWeaponMissing))
                {
                    _interiorDispatchFailureGraceElapsedLogged = true;
                    _interiorDispatchAccessFailureLogged = true;
                    LogInteriorDispatchWeaponAccessTransition(
                        "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_ACCESS_FAILURE",
                        !canSwitchAfter
                            ? "WeaponSwitchCapabilityNotRestored"
                            : sampleEvidence && !controlsAfter.AllEnabled
                                ? "RequiredControlNotEnabledAfterReconcile"
                                : "SavedPoliceWeaponMissingFromPlayerInventory",
                        dispatchState,
                        playerLiveInteriorId,
                        true,
                        currentWeaponHash,
                        previousWeaponHash,
                        changedToUnarmed,
                        canSwitchBefore,
                        canSwitchAfter,
                        controlsBefore,
                        controlsAfter,
                        inventoryKnown,
                        savedWeaponsPresent,
                        savedWeaponsExpected,
                        contextDecision);
                }

                _lastInteriorDispatchWeaponHash = currentWeaponHash;
            }
            catch (Exception ex)
            {
                if (!_interiorDispatchAccessFailureLogged)
                {
                    _interiorDispatchAccessFailureLogged = true;
                    _log.Exception(
                        "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_ACCESS_FAILED",
                        ex);
                }
            }
        }

        private void BeginInteriorDispatchWeaponAccess(
            Ped playerPed,
            string occurrenceId,
            LSPDDispatchState dispatchState,
            string locationId,
            int sceneLiveInteriorId,
            int playerLiveInteriorId)
        {
            _interiorDispatchWeaponAccessActive = true;
            _interiorDispatchWeaponOccurrenceId = occurrenceId ?? string.Empty;
            _interiorDispatchWeaponLocationId = locationId ?? string.Empty;
            _interiorDispatchWeaponSceneInteriorId = sceneLiveInteriorId;
            _interiorDispatchWeaponPlayerInteriorId = playerLiveInteriorId;
            _interiorDispatchWeaponDispatchState = dispatchState;
            _previousInteriorDispatchCanSwitch = playerPed.CanSwitchWeapons;
            _hasInteriorDispatchCanSwitchSnapshot = true;
            _changedInteriorDispatchCanSwitchWeapons = false;
            _interiorDispatchRestrictionDetected = false;
            _interiorDispatchUnarmedTransitionLogged = false;
            _interiorDispatchRestrictionRestorationLogged = false;
            _interiorDispatchAccessFailureLogged = false;
            _interiorDispatchFailureGraceElapsedLogged = false;
            _interiorDispatchWeaponEvidenceTick = int.MinValue;
            _interiorDispatchWeaponRestrictionSinceTick = int.MinValue;
            _lastInteriorDispatchWeaponHash = Function.Call<int>(
                Hash.GET_SELECTED_PED_WEAPON, playerPed);

            int savedWeaponsPresent;
            int savedWeaponsExpected;
            bool inventoryKnown = TryCountSavedPoliceWeapons(
                playerPed, out savedWeaponsPresent, out savedWeaponsExpected);
            bool canSwitchBefore = _previousInteriorDispatchCanSwitch;
            bool meleeControlsRequired =
                IsInteriorDispatchMeleeAccessState(dispatchState);
            InteriorDispatchWeaponControlAvailability controlsBefore =
                ReadInteriorDispatchWeaponControlAvailability(
                    meleeControlsRequired);
            _lastInteriorDispatchControlsBefore = controlsBefore;

            if (!canSwitchBefore)
            {
                playerPed.CanSwitchWeapons = true;
                _changedInteriorDispatchCanSwitchWeapons = true;
            }
            EnableInteriorDispatchWeaponControls();

            bool canSwitchAfter = playerPed.CanSwitchWeapons;
            InteriorDispatchWeaponControlAvailability controlsAfter =
                ReadInteriorDispatchWeaponControlAvailability(
                    meleeControlsRequired);
            _lastInteriorDispatchControlsAfter = controlsAfter;
            int now = Environment.TickCount;
            _interiorDispatchWeaponEvidenceTick = now;
            _log.Runtime(
                "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_ACCESS",
                "Authorization=Granted; Occurrence=" + _interiorDispatchWeaponOccurrenceId
                + "; DispatchState=" + dispatchState
                + "; Location=" + _interiorDispatchWeaponLocationId
                + "; SceneLiveInteriorId=" + _interiorDispatchWeaponSceneInteriorId
                + "; PlayerLiveInteriorId=" + _interiorDispatchWeaponPlayerInteriorId
                + "; InteriorMatch=true; CurrentWeaponHash=" + _lastInteriorDispatchWeaponHash
                + "; CurrentWeaponUnarmed="
                + (_lastInteriorDispatchWeaponHash == unchecked((int)WeaponHash.Unarmed))
                + "; WeaponChangedToUnarmed=NotObservedBeforeAuthorization"
                + "; SavedPoliceWeapons=" + DescribeSavedWeaponInventory(
                    inventoryKnown, savedWeaponsPresent, savedWeaponsExpected)
                + "; CanSwitchWeaponsOriginal=" + _previousInteriorDispatchCanSwitch
                + "; CanSwitchWeaponsBefore=" + canSwitchBefore
                + "; CanSwitchWeaponsAfter=" + canSwitchAfter
                + "; ControlsBefore=" + controlsBefore.Describe()
                + "; ControlsAfter=" + controlsAfter.Describe());

            bool savedWeaponMissing = inventoryKnown
                && savedWeaponsExpected > 0
                && savedWeaponsPresent < savedWeaponsExpected;
            bool restrictedAtAuthorization =
                _lastInteriorDispatchWeaponHash == unchecked((int)WeaponHash.Unarmed)
                || !canSwitchBefore
                || !controlsBefore.AllEnabled
                || savedWeaponMissing;
            if (restrictedAtAuthorization)
            {
                _interiorDispatchRestrictionDetected = true;
                _interiorDispatchWeaponRestrictionSinceTick = now;
                LogInteriorDispatchWeaponAccessTransition(
                    "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_RESTRICTION_DETECTED",
                    "FirstRestrictedStateAtAuthorization",
                    dispatchState,
                    playerLiveInteriorId,
                    true,
                    _lastInteriorDispatchWeaponHash,
                    int.MinValue,
                    false,
                    canSwitchBefore,
                    canSwitchAfter,
                    controlsBefore,
                    controlsAfter,
                    inventoryKnown,
                    savedWeaponsPresent,
                    savedWeaponsExpected,
                    "ValidatedAcceptedInteriorDispatchMatch");
            }

            if ((!canSwitchAfter || !controlsAfter.AllEnabled)
                && !_interiorDispatchAccessFailureLogged)
            {
                _interiorDispatchAccessFailureLogged = true;
                LogInteriorDispatchWeaponAccessTransition(
                    "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_ACCESS_FAILURE",
                    !canSwitchAfter ? "WeaponSwitchCapabilityNotRestored"
                        : "RequiredControlNotEnabledAfterReconcile",
                    dispatchState,
                    playerLiveInteriorId,
                    true,
                    _lastInteriorDispatchWeaponHash,
                    int.MinValue,
                    false,
                    canSwitchBefore,
                    canSwitchAfter,
                    controlsBefore,
                    controlsAfter,
                    inventoryKnown,
                    savedWeaponsPresent,
                    savedWeaponsExpected,
                    "ValidatedAcceptedInteriorDispatchMatch");
            }
        }

        private void RestoreInteriorDispatchWeaponAccess(
            Ped playerPed,
            string reason,
            LSPDDispatchState dispatchState,
            int playerLiveInteriorId,
            bool playerInteriorIdentityAvailable)
        {
            if (!_interiorDispatchWeaponAccessActive
                && !_hasInteriorDispatchCanSwitchSnapshot)
                return;

            int currentWeaponHash = int.MinValue;
            bool canSwitchBeforeRestore = false;
            InteriorDispatchWeaponControlAvailability controls =
                InteriorDispatchWeaponControlAvailability.Unavailable();
            bool inventoryKnown = false;
            int savedWeaponsPresent = -1;
            int savedWeaponsExpected = -1;
            try
            {
                if (playerPed != null && playerPed.Exists())
                {
                    currentWeaponHash = Function.Call<int>(
                        Hash.GET_SELECTED_PED_WEAPON, playerPed);
                    canSwitchBeforeRestore = playerPed.CanSwitchWeapons;
                    controls = ReadInteriorDispatchWeaponControlAvailability(false);
                    inventoryKnown = TryCountSavedPoliceWeapons(
                        playerPed, out savedWeaponsPresent, out savedWeaponsExpected);
                    if (_hasInteriorDispatchCanSwitchSnapshot
                        && _changedInteriorDispatchCanSwitchWeapons
                        && playerPed.CanSwitchWeapons)
                        playerPed.CanSwitchWeapons = _previousInteriorDispatchCanSwitch;
                }
            }
            catch (Exception ex)
            {
                if (!_interiorDispatchAccessFailureLogged)
                {
                    _interiorDispatchAccessFailureLogged = true;
                    _log.Exception(
                        "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_RESTORE_FAILED",
                        ex);
                }
            }

            bool canSwitchAfterRestore = playerPed != null && playerPed.Exists()
                ? playerPed.CanSwitchWeapons
                : false;
            if (_interiorDispatchWeaponAccessActive)
            {
                _log.Runtime(
                    "POLICE_AUTHORITY_INTERIOR_DISPATCH_WEAPON_ACCESS_RESTORED",
                    "Authorization=Revoked; Reason=" + (reason ?? "Unspecified")
                    + "; Occurrence=" + _interiorDispatchWeaponOccurrenceId
                    + "; DispatchState=" + dispatchState
                    + "; Location=" + _interiorDispatchWeaponLocationId
                    + "; SceneLiveInteriorId=" + _interiorDispatchWeaponSceneInteriorId
                    + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                    + "; PlayerInteriorIdentityAvailable=" + playerInteriorIdentityAvailable
                    + "; CurrentWeaponHash=" + currentWeaponHash
                    + "; CurrentWeaponUnarmed="
                    + (currentWeaponHash == unchecked((int)WeaponHash.Unarmed))
                    + "; SavedPoliceWeapons=" + DescribeSavedWeaponInventory(
                        inventoryKnown, savedWeaponsPresent, savedWeaponsExpected)
                    + "; CanSwitchWeaponsBeforeRestore=" + canSwitchBeforeRestore
                    + "; CanSwitchWeaponsOriginal=" + _previousInteriorDispatchCanSwitch
                    + "; CanSwitchWeaponsAfterRestore=" + canSwitchAfterRestore
                    + "; ControlsAtRestore=" + controls.Describe());
            }

            _interiorDispatchWeaponAccessActive = false;
            _hasInteriorDispatchCanSwitchSnapshot = false;
            _changedInteriorDispatchCanSwitchWeapons = false;
            _previousInteriorDispatchCanSwitch = false;
            _interiorDispatchRestrictionDetected = false;
            _interiorDispatchUnarmedTransitionLogged = false;
            _interiorDispatchRestrictionRestorationLogged = false;
            _interiorDispatchAccessFailureLogged = false;
            _interiorDispatchFailureGraceElapsedLogged = false;
            _interiorDispatchWeaponEvidenceTick = int.MinValue;
            _interiorDispatchWeaponRestrictionSinceTick = int.MinValue;
            _lastInteriorDispatchWeaponHash = int.MinValue;
            _interiorDispatchWeaponOccurrenceId = string.Empty;
            _interiorDispatchWeaponLocationId = string.Empty;
            _interiorDispatchWeaponDispatchState = LSPDDispatchState.None;
            _interiorDispatchWeaponSceneInteriorId = 0;
            _interiorDispatchWeaponPlayerInteriorId = 0;
            _lastInteriorDispatchControlsBefore =
                InteriorDispatchWeaponControlAvailability.Unavailable();
            _lastInteriorDispatchControlsAfter =
                InteriorDispatchWeaponControlAvailability.Unavailable();
        }

        private void LogInteriorDispatchWeaponAccessTransition(
            string eventName,
            string reason,
            LSPDDispatchState dispatchState,
            int playerLiveInteriorId,
            bool interiorMatch,
            int currentWeaponHash,
            int previousWeaponHash,
            bool changedToUnarmed,
            bool canSwitchBefore,
            bool canSwitchAfter,
            InteriorDispatchWeaponControlAvailability controlsBefore,
            InteriorDispatchWeaponControlAvailability controlsAfter,
            bool inventoryKnown,
            int savedWeaponsPresent,
            int savedWeaponsExpected,
            string contextDecision)
        {
            _log.Runtime(
                eventName,
                "Reason=" + (reason ?? "Unspecified")
                + "; ContextDecision=" + (contextDecision ?? string.Empty)
                + "; Occurrence=" + _interiorDispatchWeaponOccurrenceId
                + "; DispatchState=" + dispatchState
                + "; Location=" + _interiorDispatchWeaponLocationId
                + "; SceneLiveInteriorId=" + _interiorDispatchWeaponSceneInteriorId
                + "; PlayerLiveInteriorId=" + playerLiveInteriorId
                + "; InteriorMatch=" + interiorMatch
                + "; CurrentWeaponHash=" + currentWeaponHash
                + "; CurrentWeaponUnarmed="
                + (currentWeaponHash == unchecked((int)WeaponHash.Unarmed))
                + "; PreviousWeaponHash=" + previousWeaponHash
                + "; WeaponChangedToUnarmed=" + changedToUnarmed
                + "; SavedPoliceWeapons=" + DescribeSavedWeaponInventory(
                    inventoryKnown, savedWeaponsPresent, savedWeaponsExpected)
                + "; CanSwitchWeaponsBefore=" + canSwitchBefore
                + "; CanSwitchWeaponsAfter=" + canSwitchAfter
                + "; ControlsBefore=" + controlsBefore.Describe()
                + "; ControlsAfter=" + controlsAfter.Describe());
        }

        private bool TryCountSavedPoliceWeapons(
            Ped playerPed,
            out int present,
            out int expected)
        {
            present = 0;
            expected = 0;
            if (playerPed == null || !playerPed.Exists())
                return false;

            try
            {
                var savedWeaponHashes = new HashSet<int>();
                if (_profile.Selection != null
                    && _profile.Selection.UsePersonalLoadout)
                {
                    foreach (LSPDPoliceFavoriteWeapon weapon in _profile.PersonalWeapons)
                    {
                        if (weapon == null || string.IsNullOrWhiteSpace(weapon.WeaponName))
                            continue;
                        savedWeaponHashes.Add(
                            LSPDProfile.ResolveWeaponHash(weapon.WeaponName));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(_profile.SelectedWeaponName))
                {
                    savedWeaponHashes.Add(
                        LSPDProfile.ResolveWeaponHash(
                            _profile.SelectedWeaponName));
                }

                foreach (int weaponHash in savedWeaponHashes)
                {
                    if (weaponHash == unchecked((int)WeaponHash.Unarmed))
                        continue;
                    expected++;
                    if (Function.Call<bool>(
                        Hash.HAS_PED_GOT_WEAPON, playerPed, weaponHash, false))
                        present++;
                }
                return true;
            }
            catch
            {
                present = -1;
                expected = -1;
                return false;
            }
        }

        private void EnableInteriorDispatchWeaponControls()
        {
            for (int i = 0; i < InteriorDispatchWeaponControls.Length; i++)
            {
                Function.Call(
                    Hash.ENABLE_CONTROL_ACTION,
                    0,
                    (int)InteriorDispatchWeaponControls[i],
                    true);
            }
            if (!IsInteriorDispatchMeleeAccessState(
                _interiorDispatchWeaponDispatchState))
                return;
            for (int i = 0; i < InteriorDispatchMeleeControls.Length; i++)
            {
                Function.Call(
                    Hash.ENABLE_CONTROL_ACTION,
                    0,
                    (int)InteriorDispatchMeleeControls[i],
                    true);
            }
        }

        private static InteriorDispatchWeaponControlAvailability
            ReadInteriorDispatchWeaponControlAvailability(
                bool meleeControlsRequired)
        {
            bool wheel = Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.SelectWeapon)
                && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.WeaponWheelUpDown)
                && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.WeaponWheelLeftRight)
                && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.WeaponWheelNext)
                && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.WeaponWheelPrev)
                && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.SelectNextWeapon)
                && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.SelectPrevWeapon);
            return new InteriorDispatchWeaponControlAvailability
            {
                WeaponWheel = wheel,
                Aim = Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.Aim),
                Attack = Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.Attack),
                Reload = Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                    0, (int)Control.Reload),
                MeleeControlsRequired = meleeControlsRequired,
                MeleeAttack = Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                        0, (int)Control.MeleeAttackLight)
                    && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                        0, (int)Control.MeleeAttackHeavy)
                    && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                        0, (int)Control.MeleeAttackAlternate)
                    && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                        0, (int)Control.MeleeAttack1)
                    && Function.Call<bool>(Hash.IS_CONTROL_ENABLED,
                        0, (int)Control.MeleeAttack2)
            };
        }

        private static bool IsInteriorDispatchMeleeAccessState(
            LSPDDispatchState state)
        {
            return state == LSPDDispatchState.Accepted
                || state == LSPDDispatchState.EnRoute
                || state == LSPDDispatchState.OnScene
                || state == LSPDDispatchState.Investigating
                || state == LSPDDispatchState.SuspectFleeing
                || state == LSPDDispatchState.SuspectResisting;
        }

        private static string DescribeSavedWeaponInventory(
            bool inventoryKnown,
            int present,
            int expected)
        {
            return inventoryKnown
                ? present + "/" + expected
                : "Unavailable";
        }

        private struct InteriorDispatchWeaponControlAvailability
        {
            internal bool WeaponWheel;
            internal bool Aim;
            internal bool Attack;
            internal bool Reload;
            internal bool MeleeControlsRequired;
            internal bool MeleeAttack;

            internal bool AllEnabled
            {
                get
                {
                    return WeaponWheel && Aim && Attack && Reload
                        && (!MeleeControlsRequired || MeleeAttack);
                }
            }

            internal string Describe()
            {
                return "WeaponWheel=" + WeaponWheel
                    + ",Aim=" + Aim
                    + ",Attack=" + Attack
                    + ",Reload=" + Reload
                    + ",MeleeAttack=" + MeleeAttack
                    + ",MeleeRequired=" + MeleeControlsRequired;
            }

            internal static InteriorDispatchWeaponControlAvailability Unavailable()
            {
                return new InteriorDispatchWeaponControlAvailability
                {
                    WeaponWheel = false,
                    Aim = false,
                    Attack = false,
                    Reload = false,
                    MeleeControlsRequired = false,
                    MeleeAttack = false
                };
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

        private void ReconcileWantedProtection(
            Player player,
            LSPDDispatchEvent activeIncident,
            bool patrolActive,
            IEnumerable<Ped> gangPedCandidates)
        {
            if (!Settings.SuppressVanillaWantedEscalation)
            {
                RestoreWantedProtection(player);
                _lastObservedWantedLevel = 0;
                _lastObservedMaximumWantedLevel = 0;
                _lastWantedCapContextSignature = string.Empty;
                return;
            }

            bool protectFromPolice = Settings.EnableWorldBehaviorChanges
                && (Settings.ProtectPlayerFromVanillaPoliceEscalation
                    || Settings.SuppressAmbientPoliceHostility);
            if (protectFromPolice)
            {
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player.Handle, true);
                if (!_changedPoliceIgnore)
                {
                    _changedPoliceIgnore = true;
                    _log.Runtime("POLICE_AUTHORITY_POLICE_IGNORE",
                        "Vanilla Police ignore is now asserted throughout the active Police Authority session.");
                }
                if (Settings.SuppressVanillaDispatch)
                {
                    Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER,
                        player.Handle, false);
                    if (!_changedDispatchCops)
                    {
                        _changedDispatchCops = true;
                        _log.Runtime("POLICE_AUTHORITY_DISPATCH_SUPPRESSED",
                            "Vanilla dispatch suppression is now asserted throughout the active Police Authority session.");
                    }
                }
            }

            int maximumWanted = ReadMaximumWantedLevel();
            int currentWanted = ReadWantedLevel(player);
            string context = BuildWantedAttemptContext(
                player, activeIncident, patrolActive, gangPedCandidates);
            if (maximumWanted != 0)
            {
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                _changedMaximumWantedLevel = true;
                bool capAttemptDue = !string.Equals(
                        _lastWantedCapContextSignature,
                        maximumWanted + "|" + context,
                        StringComparison.Ordinal)
                    || _lastWantedCapContextLogAt == DateTime.MinValue
                    || DateTime.UtcNow >= _lastWantedCapContextLogAt
                        .AddSeconds(2);
                if (capAttemptDue)
                {
                    _lastWantedCapContextSignature = maximumWanted + "|" + context;
                    _lastWantedCapContextLogAt = DateTime.UtcNow;
                    int correctedMaximum = ReadMaximumWantedLevel();
                    _log.Runtime(
                        "POLICE_AUTHORITY_WANTED_CAP_OVERWRITE_ATTEMPT",
                        "PreviousMaximumWanted=" + _lastObservedMaximumWantedLevel
                        + "; AttemptedMaximumWanted=" + maximumWanted
                        + "; CorrectedMaximumWanted=" + correctedMaximum
                        + "; CorrectionResult=" + (correctedMaximum == 0
                            ? "ZeroConfirmed" : "StillOverwritten")
                        + "; " + context);
                }
            }

            // Some game-side systems can reassert wanted between the
            // 150 ms checks. Clear on every reconciliation, while only
            // reporting each observed non-zero transition from the preceding
            // native read. This preserves evidence without logging every frame.
            Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, player.Handle);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, player.Handle, false);
            if (currentWanted > 0)
            {
                _clearedWantedLevel = true;
                if (currentWanted != _lastObservedWantedLevel)
                {
                    int correctedWanted = ReadWantedLevel(player);
                    _log.Runtime(
                        "POLICE_AUTHORITY_WANTED_TRANSITION_ATTEMPT",
                        "PreviousWantedLevel=" + _lastObservedWantedLevel
                        + "; AttemptedWantedLevel=" + currentWanted
                        + "; CorrectedWantedLevel=" + correctedWanted
                        + "; CorrectionResult=" + (correctedWanted == 0
                            ? "WantedCleared" : "WantedStillPresent")
                        + "; " + context);
                }
            }
            _lastObservedWantedLevel = currentWanted > 0 ? currentWanted : 0;
            _lastObservedMaximumWantedLevel = maximumWanted;
        }

        private static string BuildWantedAttemptContext(
            Player player,
            LSPDDispatchEvent activeIncident,
            bool patrolActive,
            IEnumerable<Ped> gangPedCandidates)
        {
            Ped character = player == null ? null : player.Character;
            Ped target = null;
            bool aimingAtDispatchSuspect = false;
            if (activeIncident != null && activeIncident.Suspects != null
                && character != null && character.Exists())
            {
                float closestDistance = float.MaxValue;
                foreach (Ped suspect in activeIncident.Suspects)
                {
                    if (suspect == null || !suspect.Exists() || suspect.IsDead)
                        continue;
                    float distance = character.Position.DistanceTo(suspect.Position);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        target = suspect;
                    }
                }
                if (target != null)
                {
                    try
                    {
                        aimingAtDispatchSuspect = Function.Call<bool>(
                            Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY,
                            player.Handle, target);
                    }
                    catch { }
                }
            }

            string action = "PoliceDuty";
            if (activeIncident != null)
            {
                if (activeIncident.PlayerHandcuffInProgress)
                    action = "DispatchPhysicalArrest";
                else if (aimingAtDispatchSuspect)
                    action = "AimingAtActiveDispatchSuspect";
                else
                {
                    bool shooting = false;
                    try
                    {
                        shooting = character != null && character.Exists()
                            && Function.Call<bool>(Hash.IS_PED_SHOOTING, character);
                    }
                    catch { }
                    if (shooting)
                        action = "FiringDuringActiveDispatch";
                    else if (activeIncident.State == LSPDDispatchState.SuspectFleeing)
                        action = "ActiveDispatchPursuit";
                    else if (character != null && character.Exists()
                        && character.IsInVehicle())
                        action = "ActiveDispatchVehicleScene";
                    else
                        action = "ActiveDispatch";
                }
            }
            else if (patrolActive && character != null && character.Exists()
                && character.IsInVehicle())
                action = "PatrolVehicleContactOrMovement";

            int playerVehicleHandle = 0;
            if (character != null && character.Exists() && character.IsInVehicle()
                && character.CurrentVehicle != null
                && character.CurrentVehicle.Exists())
                playerVehicleHandle = character.CurrentVehicle.Handle;

            int gangThreatHandle = 0;
            if (character != null && character.Exists()
                && gangPedCandidates != null)
                foreach (Ped candidate in gangPedCandidates)
                {
                    if (candidate == null || !candidate.Exists()
                        || candidate.IsDead)
                        continue;
                    try
                    {
                        if (candidate.IsInCombatAgainst(character))
                        {
                            gangThreatHandle = candidate.Handle;
                            if (activeIncident != null)
                                action += "+GangCombat";
                            break;
                        }
                    }
                    catch { }
                }

            return "Incident=" + (activeIncident == null
                    ? "None" : activeIncident.Id ?? "Unknown")
                + "; Target=" + (target == null ? 0 : target.Handle)
                + "; Action=" + action
                + "; PatrolActive=" + patrolActive
                + "; PlayerVehicle=" + playerVehicleHandle
                + "; GangThreat=" + gangThreatHandle;
        }

        private void RestorePlayerControls(
            bool restoreInteriorDispatchWeaponAccess = true,
            bool clearPoliceModeWantedLevel = false)
        {
            try
            {
                Player player = Game.Player;
                if (restoreInteriorDispatchWeaponAccess)
                {
                    RestoreInteriorDispatchWeaponAccess(
                        player == null ? null : player.Character,
                        "PoliceAuthorityControlsRestored",
                        _interiorDispatchWeaponDispatchState,
                        _interiorDispatchWeaponPlayerInteriorId,
                        false);
                }

                if (player != null)
                {
                    if (_hasPlayerSnapshot)
                        RestorePoliceProtection(player);
                    RestoreWantedProtection(player, clearPoliceModeWantedLevel);
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

        private void ReconcileWantedProtectionDuringControlWait(
            LSPDDispatchEvent activeIncident,
            bool patrolActive,
            IEnumerable<Ped> gangPedCandidates)
        {
            if (!Settings.SuppressVanillaWantedEscalation)
                return;
            try
            {
                Player player = Game.Player;
                if (player == null || player.Character == null
                    || !player.Character.Exists())
                    return;
                ReconcileWantedProtection(
                    player, activeIncident, patrolActive, gangPedCandidates);
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
                {
                    int currentPlayerToCop = Function.Call<int>(
                        Hash.GET_RELATIONSHIP_BETWEEN_GROUPS,
                        playerGroup, copGroup);
                    int currentCopToPlayer = Function.Call<int>(
                        Hash.GET_RELATIONSHIP_BETWEEN_GROUPS,
                        copGroup, playerGroup);
                    if (currentPlayerToCop != 1 || currentCopToPlayer != 1)
                    {
                        // Preserve the latest observed values if another runtime
                        // owner changed the pair while Authority was active. The
                        // original source still restores only this relationship
                        // pair when Authority ends.
                        _previousPlayerToCopRelationship = currentPlayerToCop;
                        _previousCopToPlayerRelationship = currentCopToPlayer;
                        Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                            1, playerGroup, copGroup);
                        Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                            1, copGroup, playerGroup);
                        string repairSignature = playerGroup + "|" + copGroup
                            + "|" + currentPlayerToCop + "|" + currentCopToPlayer;
                        if (!string.Equals(_lastPoliceRelationshipRepairSignature,
                                repairSignature, StringComparison.Ordinal))
                        {
                            _lastPoliceRelationshipRepairSignature = repairSignature;
                            _log.Runtime(
                                "POLICE_AUTHORITY_POLICE_RELATIONSHIP_REASSERTED",
                                "Reapplied Police respect after the live relationship changed. PlayerGroup="
                                + playerGroup + "; CopGroup=" + copGroup
                                + "; PreviousLivePlayerToCop=" + currentPlayerToCop
                                + "; PreviousLiveCopToPlayer=" + currentCopToPlayer);
                        }
                    }
                    else
                        _lastPoliceRelationshipRepairSignature = string.Empty;
                    return;
                }

                RestorePoliceAllyRelationship();
                _policePlayerRelationshipGroup = playerGroup;
                _policeCopRelationshipGroup = copGroup;
                _lastPoliceRelationshipRepairSignature = string.Empty;
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

        internal bool EnsurePoliceBackupFriendliness(Ped playerPed, Ped officerPed)
        {
            if (!IsActive || playerPed == null || officerPed == null
                || !playerPed.Exists() || !officerPed.Exists()
                || playerPed.IsDead || officerPed.IsDead)
                return false;
            try
            {
                int officerGroup = Function.Call<int>(
                    Hash.GET_PED_RELATIONSHIP_GROUP_HASH, officerPed);
                int copGroup = unchecked((int)StringHash.AtStringHash("COP", 0));
                if (copGroup == 0 || officerGroup != copGroup)
                    return false;

                EnsurePoliceAllyRelationship(playerPed);
                int playerGroup = Function.Call<int>(
                    Hash.GET_PED_RELATIONSHIP_GROUP_HASH, playerPed);
                int playerToCop = Function.Call<int>(
                    Hash.GET_RELATIONSHIP_BETWEEN_GROUPS,
                    playerGroup, officerGroup);
                int copToPlayer = Function.Call<int>(
                    Hash.GET_RELATIONSHIP_BETWEEN_GROUPS,
                    officerGroup, playerGroup);
                return IsPoliceFriendlyRelationship(playerToCop)
                    && IsPoliceFriendlyRelationship(copToPlayer);
            }
            catch (Exception ex)
            {
                _log.Exception("POLICE_AUTHORITY_BACKUP_FRIENDLINESS_CHECK_FAILED", ex);
                return false;
            }
        }

        private static bool IsPoliceFriendlyRelationship(int relationship)
        {
            return relationship >= 0 && relationship <= 2;
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
                _lastPoliceRelationshipRepairSignature = string.Empty;
                _policePlayerRelationshipGroup = 0;
                _policeCopRelationshipGroup = 0;
                _previousPlayerToCopRelationship = 0;
                _previousCopToPlayerRelationship = 0;
            }
        }

        private void RestoreWantedProtection(
            Player player,
            bool clearPoliceModeWantedLevel = false)
        {
            if (_changedMaximumWantedLevel && _hasMaximumWantedSnapshot)
            {
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, _previousMaximumWantedLevel);
                _changedMaximumWantedLevel = false;
            }

            if (clearPoliceModeWantedLevel)
            {
                Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, player.Handle);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, player.Handle, false);
                _log.Runtime(
                    "POLICE_AUTHORITY_WANTED_CLEARED_ON_EXIT",
                    "Police Authority exit cleared wanted status instead of restoring a saved wanted level.");
            }
            else if (_clearedWantedLevel && _hasWantedSnapshot
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
                EnableCivilianReactionControl = true,
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
