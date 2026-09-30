using System;
using LemonUI.Menus;

namespace LSImmersiveLife
{
    /// <summary>
    /// Routes the role selected in LSRoleplay to that role's authority UI.
    /// This class owns role selection state and navigation only. It never owns
    /// Dispatch, Convoy, NPC, Gang, or Police-world gameplay rules.
    /// </summary>
    internal sealed class LSAuthorityRole
    {
        private readonly LSImmersiveLog _log;
        private readonly LSImmersivePoliceUI _policeUi;
        private LSRoleplayMode _selectedMode;

        internal LSAuthorityRole(
            LSImmersiveLog log,
            LSImmersivePoliceUI policeUi)
        {
            _log = log ?? throw new ArgumentNullException("log");
            _policeUi = policeUi ?? throw new ArgumentNullException("policeUi");
            _selectedMode = LSRoleplayMode.NoneSelected;

            Menu = LSImmersiveMenuFactory.Create("Authority", string.Empty);
            Menu.NoItemsText = "Select a Roleplay mode first.";
            RebuildAuthorityMenu();
        }

        internal NativeMenu Menu { get; private set; }

        internal LSRoleplayMode SelectedMode
        {
            get { return _selectedMode; }
        }

        internal bool HasSelectedRole
        {
            get { return _selectedMode != LSRoleplayMode.NoneSelected; }
        }

        /// <summary>
        /// Receives the category selection from LSRoleplay. Selecting Police
        /// activates the existing Police Authority path and routes directly
        /// to its gameplay UI.
        /// </summary>
        internal void SelectRoleplayMode(LSRoleplayMode mode)
        {
            _selectedMode = mode;

            // Leaving the Police category must release Police Authority through
            // its existing UI/Core boundary. AuthorityRole does not reach into
            // individual Police systems to do that cleanup itself.
            _policeUi.SetRoleActive(mode == LSRoleplayMode.PoliceAuthority);

            _log.Runtime("ROLEPLAY_MODE_SELECTED", DisplayName(mode));
            RebuildAuthorityMenu();

            // Police Authority is the selected role's gameplay surface. Route
            // directly through this authority boundary so the player does not
            // have to open an otherwise empty intermediate screen first. The
            // Authority menu still retains its explicit reopen action.
            if (mode == LSRoleplayMode.PoliceAuthority)
            {
                OpenActiveRole();
                return;
            }

            // Roleplay immediately forwards the player to the selected role's
            // Authority surface. This makes the chosen mode clear without
            // making the universal Main UI a second authority menu.
            if (Menu.Parent != null)
                Menu.Parent.Visible = false;
            Menu.Visible = true;
        }

        internal void OpenActiveRole()
        {
            if (_selectedMode != LSRoleplayMode.PoliceAuthority)
            {
                _log.Debug(
                    "AUTHORITY_ROUTE_UNAVAILABLE",
                    "No implemented authority module is registered for "
                    + DisplayName(_selectedMode) + ".");
                return;
            }

            // Police UI owns activation and calls Police Core. This router only
            // transfers navigation to that boundary.
            Menu.Visible = false;
            if (Menu.Parent != null)
                Menu.Parent.Visible = false;
            _policeUi.Open();
            _log.Runtime("AUTHORITY_ROUTE_OPENED", "Police Authority");
        }

        internal void Process(bool paused)
        {
            // Police Core also completes a queued normal-player restoration after
            // Police Authority is turned off. Keeping this lightweight lifecycle
            // call here allows that safe recovery to finish even if the user has
            // already selected another role. Police gameplay remains gated by
            // Police Core's own active Authority state.
            _policeUi.Process(paused);
        }

        internal void Shutdown()
        {
            // Script shutdown is distinct from normal Back navigation. The
            // selected role UI owns its cleanup and disposal behavior.
            _policeUi.Shutdown();
        }

        private void RebuildAuthorityMenu()
        {
            Menu.Clear();

            switch (_selectedMode)
            {
                case LSRoleplayMode.PoliceAuthority:
                    AddReadOnlyRow("Selected Role: Police Authority");
                    LSImmersiveMenuFactory.AddAction(
                        Menu,
                        "Open Police Gameplay",
                        OpenActiveRole);
                    break;

                case LSRoleplayMode.Citizen:
                    AddReadOnlyRow("Selected Role: Citizen");
                    AddReadOnlyRow("Citizen Authority is reserved for future development.");
                    break;

                case LSRoleplayMode.GangLeader:
                    AddReadOnlyRow("Selected Role: Gang Leader");
                    AddReadOnlyRow("Gang Leader Authority is reserved for future development.");
                    break;

                default:
                    AddReadOnlyRow("Choose Roleplay, then select an Authority.");
                    break;
            }
        }

        private void AddReadOnlyRow(string title)
        {
            NativeItem row = new NativeItem(title);
            row.Enabled = false;
            Menu.Add(row);
        }

        private static string DisplayName(LSRoleplayMode mode)
        {
            switch (mode)
            {
                case LSRoleplayMode.PoliceAuthority:
                    return "Police Authority";
                case LSRoleplayMode.Citizen:
                    return "Citizen";
                case LSRoleplayMode.GangLeader:
                    return "Gang Leader";
                default:
                    return "No Roleplay Mode";
            }
        }
    }
}

