using GTA;
using LemonUI;
using LemonUI.Elements;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LSImmersiveLife
{
    /// <summary>
    /// The universal gateway for LS Immersive Life.
    ///
    /// This script presents the top-level navigation only. It routes a player
    /// to Roleplay, Authority, Customization, Settings, and Developer modules;
    /// it does not decide Police gameplay, Dispatch state, NPC behavior, or any
    /// other role-specific rule.
    /// </summary>
    public sealed class LSIMMERSIVEUI : Script
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        private readonly ObjectPool _pool = new ObjectPool();
        private readonly List<NativeMenu> _menus = new List<NativeMenu>();
        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSImmersiveLog _log;
        private readonly LSImmersiveMainConfig _config;
        private readonly LSRoleplay _roleplay;
        private readonly LSAuthorityRole _authority;
        private readonly LSImmersivePoliceUI _policeUi;
        private readonly LSImmersiveHotkeys _hotkeys;
        private readonly LSMainSetting _settings;
        private readonly LSImmersiveDeveloper _developer;

        private bool _menuToggleWasDown;
        private NativeMenu _root;

        public LSIMMERSIVEUI()
        {
            _paths = new LSIMMERSIVEPATH();
            // Queue live records for the logger's background batch writer so a
            // response does not open/close a file on the SHVDN tick. The
            // logger's default remains the synchronous writer used by its
            // isolated regression fixtures.
            _log = new LSImmersiveLog(_paths.LSImmersiveDirectory, true);

            // MainConfig is the sole persisted owner of editable settings.
            // Its data catalogs are deliberately loaded by the specialized
            // systems that own them, not by this universal navigation shell.
            _config = LSImmersiveMainConfig.Load(
                _paths.MainUiXmlPath,
                LogConfigurationFallback);
            _log.Configure(
                _config.Logging.RuntimeEnabled,
                _config.Logging.DebugEnabled,
                _config.Logging.VerboseDiagnostics);

            LSImmersivePoliceCore policeCore = new LSImmersivePoliceCore(
                _log,
                _paths,
                _config);

            _policeUi = new LSImmersivePoliceUI(policeCore);
            _authority = new LSAuthorityRole(_log, _policeUi);
            _roleplay = new LSRoleplay(SelectRoleplayMode);
            _hotkeys = new LSImmersiveHotkeys(
                _config,
                _paths.MainUiXmlPath,
                _log);
            _settings = new LSMainSetting(
                _config,
                _paths.MainUiXmlPath,
                _log);
            _developer = new LSImmersiveDeveloper(
                _paths,
                _log);

            BuildMenus();

            Interval = 0;
            Tick += OnTick;
            Aborted += OnAborted;

            if (_config.Logging.SessionHeaders)
            {
                _log.BeginSession(
                    "Universal gateway ready; Configuration="
                    + LSImmersiveMainConfig.FileName + ".");
            }
            _log.Runtime(
                "MAIN_UI_BOOT",
                "Universal UI, role router, settings, and customization modules initialized.");
        }

        /// <summary>
        /// Registers menus for rendering and connects only the universal
        /// navigation hierarchy. Specialized menus retain their own actions.
        /// </summary>
        private void BuildMenus()
        {
            _root = LSImmersiveMenuFactory.Create(
                "LS Immersive Life",
                "Immersive Los Santos Life");

            Add(_root);
            Add(_roleplay.Menu);
            Add(_authority.Menu);
            Add(_hotkeys.Menu);
            Add(_settings.Menu);
            Add(_developer.Menu);

            // The Police UI has nested category menus of its own. Registering
            // them is a LemonUI requirement; it does not make this class the
            // owner of their controls or gameplay commands.
            foreach (NativeMenu policeMenu in _policeUi.Menus)
                Add(policeMenu);

            _root.AddSubMenu(_roleplay.Menu);
            _root.AddSubMenu(_authority.Menu);
            _root.AddSubMenu(_hotkeys.Menu);
            _root.AddSubMenu(_settings.Menu);
            _root.AddSubMenu(_developer.Menu);

            // Police Authority is reached through Authority, never as a second
            // root menu item. The parent gives Back a clear route to the
            // selected role's Authority screen.
            _policeUi.Menu.Parent = _authority.Menu;
        }

        private void SelectRoleplayMode(LSRoleplayMode mode)
        {
            // Roleplay owns role selection. Authority decides which selected
            // role can open its corresponding authority UI and core.
            _authority.SelectRoleplayMode(mode);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                // Authority retains lifecycle ownership and delegates active
                // Police work to PoliceCore. This gateway never polls Police
                // incidents, spawns entities, or changes the world itself.
                _authority.Process(Game.IsPaused);
                _developer.Process();

                bool menuToggleDown =
                    (GetAsyncKeyState((int)_config.MenuToggleKey) & 0x8000) != 0;
                if (menuToggleDown && !_menuToggleWasDown)
                {
                    if (_pool.AreAnyVisible)
                        CloseAllMenus();
                    else
                        _root.Visible = true;
                }

                _menuToggleWasDown = menuToggleDown;
                _pool.Process();
            }
            catch (Exception error)
            {
                // An unhandled gateway fault must be diagnostic evidence. The
                // logger protects the game thread if the log path is unavailable.
                _log.Exception("MAIN_UI_TICK_ERROR", error);
            }
        }

        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                // Script shutdown is the only place the gateway directly asks
                // Authority to release its selected role. Opening or closing a
                // menu never performs this gameplay cleanup.
                _authority.Shutdown();
                CloseAllMenus();
                _developer.Shutdown();
                _log.Runtime("MAIN_UI_STOP", "Universal gateway stopped.");
            }
            catch (Exception error)
            {
                _log.Exception("MAIN_UI_ABORT_ERROR", error);
            }
            finally
            {
                _log.EndSession("Universal gateway aborted.");
            }
        }

        private void Add(NativeMenu menu)
        {
            if (menu == null)
                return;

            _menus.Add(menu);
            _pool.Add(menu);
        }

        private void CloseAllMenus()
        {
            foreach (NativeMenu menu in _menus)
                menu.Visible = false;
        }

        private void LogConfigurationFallback(string text)
        {
            // Configuration parsing is a fallback/diagnostic condition; it is
            // kept out of Runtime so normal gameplay history stays readable.
            _log.Debug("MAIN_CONFIG", text);
        }
    }

    /// <summary>
    /// Shared presentation helper for separately-owned LS Immersive menus.
    /// It standardizes menu presentation without taking ownership of a menu's
    /// gameplay actions or persisted settings.
    /// </summary>
    internal static class LSImmersiveMenuFactory
    {
        internal static NativeMenu Create(string title, string subtitle)
        {
            NativeMenu menu = new NativeMenu(title, subtitle);
            if (string.IsNullOrEmpty(subtitle))
                menu.Name = title;

            ScaledRectangle banner = new ScaledRectangle(
                new PointF(0, 0),
                new SizeF(432, 105));
            banner.Color = Color.FromArgb(255, 47, 111, 190);
            menu.Banner = banner;
            return menu;
        }

        internal static void AddAction(
            NativeMenu menu,
            string label,
            Action action)
        {
            NativeItem item = new NativeItem(label);
            item.Activated += delegate
            {
                if (action != null)
                    action();
            };
            menu.Add(item);
        }
    }
}

