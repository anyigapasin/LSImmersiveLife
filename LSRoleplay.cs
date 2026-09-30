using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Elements;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;


namespace LSImmersiveLife
{
    /// <summary>
    /// Roleplay selection UI.
    /// This class owns the three role choices only. It does not create Police,
    /// Citizen, or Gang gameplay; it forwards the player's choice to Authority.
    /// </summary>
    internal sealed class LSRoleplay
    {
   


        private readonly Action<LSRoleplayMode> _selectRoleplayMode;

        internal NativeMenu Menu { get; private set; }

        internal LSRoleplay(Action<LSRoleplayMode> selectRoleplayMode)
        {
            // Build the separate Roleplay screen rather than adding role rows
            // to the Main UI. An empty subtitle avoids duplicate descriptive
            // text when LemonUI renders this menu in the root navigation.
            Menu = LSImmersiveMenuFactory.Create("Roleplay", string.Empty);

            _selectRoleplayMode = selectRoleplayMode;

            // These rows choose a roleplay direction. The actual authority UI
            // and core for the selected role remain outside this selection UI.
            AddRoleplayMode("Citizen", LSRoleplayMode.Citizen);
            AddRoleplayMode(
                "Police Authority",
                LSRoleplayMode.PoliceAuthority);
            AddRoleplayMode("Gang Leader", LSRoleplayMode.GangLeader);
        }

        private void AddRoleplayMode(string label, LSRoleplayMode mode)
        {
            // Close the selection surface before handing the result to the
            // authority router. That router then opens only the selected role's
            // own UI; the three role systems are never mixed into one menu.
            LSImmersiveMenuFactory.AddAction(
                Menu,
                label,
                delegate
                {
                    Menu.Visible = false;
                    _selectRoleplayMode(mode);
                });
        }
    }

    /// <summary>
    /// Internal selection state shared only between Roleplay and Authority.
    /// NoneSelected is not a visible Vanilla mode; GTA remains vanilla until
    /// an LS Immersive role is deliberately selected by the player.
    /// </summary>
    internal enum LSRoleplayMode
    {
        NoneSelected,
        Citizen,
        PoliceAuthority,
        GangLeader
    }
}
