using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTA.UI;
using LemonUI.Menus;

namespace LSImmersiveLife
{
    /// <summary>
    /// The in-game control editor. It changes only the shared main config and
    /// saves only LSImmersiveMainUI.xml; no role creates an extra control file.
    /// </summary>
    internal sealed class LSImmersiveHotkeys
    {
        private delegate bool KeyAssignment(Keys key, out string reason);

        private readonly LSImmersiveLog _log;
        private bool _hasUnsavedChanges;

        internal NativeMenu Menu { get; private set; }
        internal LSImmersiveMainConfig Configuration { get; private set; }
        internal string ConfigurationPath { get; private set; }
        internal bool HasUnsavedChanges { get { return _hasUnsavedChanges; } }

        internal LSImmersiveHotkeys(
            LSImmersiveMainConfig configuration,
            string configurationPath,
            LSImmersiveLog log = null)
        {
            Configuration = configuration ?? throw new ArgumentNullException("configuration");
            ConfigurationPath = configurationPath;
            _log = log;

            Menu = LSImmersiveMenuFactory.Create("Customization", string.Empty);
            Menu.NoItemsText = string.Empty;
            BuildMenu();
        }

        private void BuildMenu()
        {
            AddHeader("Universal Controls");
            AddKeyItem(
                "Open / Close LS Immersive Menu",
                "Use left or right to select the F-key or keyboard key used for the main menu.",
                delegate { return Configuration.MenuToggleKey; },
                delegate(Keys key, out string reason) { return Configuration.TrySetMenuToggleKey(key, out reason); });

            AddHeader("Police Authority Controls");
            AddPoliceKey("Patrol Toggle", LSPDControlAction.Patrol);
            AddPoliceKey("Accept Dispatch", LSPDControlAction.Accept);
            AddPoliceKey("Reject Dispatch", LSPDControlAction.Reject);
            AddPoliceKey("Investigate Dispatch", LSPDControlAction.Investigate);
            AddPoliceKey("Secure Suspect", LSPDControlAction.Secure);
            AddPoliceKey("Request Prisoner Transport", LSPDControlAction.Transport);
            AddPoliceKey("Complete Prisoner Transport", LSPDControlAction.TransportComplete);
            AddPoliceKey("NPC Interaction", LSPDControlAction.Interaction);
            AddPoliceKey("Request Backup", LSPDControlAction.Emergency);
            AddPoliceKey("Reset Police Runtime", LSPDControlAction.Reset);

            NativeItem save = new NativeItem("Save Control Changes", "Write all customized keys to LSImmersiveMainUI.xml.");
            save.Activated += delegate { Save(); };
            Menu.Add(save);
        }

        private void AddPoliceKey(string title, LSPDControlAction action)
        {
            AddKeyItem(
                title,
                "Use left or right to choose a unique keyboard key.",
                delegate { return Configuration.PoliceControls.Get(action); },
                delegate(Keys key, out string reason) { return Configuration.TrySetPoliceControl(action, key, out reason); });
        }

        private void AddKeyItem(string title, string description, Func<Keys> current, KeyAssignment assign)
        {
            bool synchronizing = false;
            NativeListItem<Keys> item = new NativeListItem<Keys>(
                title,
                description,
                Choices(current()));
            item.SelectedItem = current();
            item.ItemChanged += delegate(object sender, ItemChangedEventArgs<Keys> args)
            {
                if (synchronizing)
                    return;

                string reason;
                if (!assign(args.Object, out reason))
                {
                    synchronizing = true;
                    item.SelectedItem = current();
                    synchronizing = false;
                    Notify(reason);
                    LogDebug("HOTKEY_REJECTED", title + ": " + reason);
                    return;
                }

                _hasUnsavedChanges = true;
                Configuration.NotifySettingsChanged();
                LogRuntime("HOTKEY_CHANGED", title + " = " + current());
            };
            Menu.Add(item);
        }

        private static Keys[] Choices(Keys selected)
        {
            List<Keys> values = new List<Keys>(LSImmersiveMainConfig.SupportedBindingKeys);
            if (!values.Contains(selected))
                values.Insert(0, selected);
            return values.ToArray();
        }

        private void AddHeader(string title)
        {
            NativeItem header = new NativeItem(title);
            header.Enabled = false;
            Menu.Add(header);
        }

        private void Save()
        {
            try
            {
                Configuration.Save(ConfigurationPath);
                _hasUnsavedChanges = false;
                LogRuntime("HOTKEYS_SAVED", "Customization controls saved to LSImmersiveMainUI.xml.");
                Notify("LS Immersive control changes saved.");
            }
            catch (Exception ex)
            {
                LogException("HOTKEYS_SAVE_FAILED", ex);
                Notify("LS Immersive control changes could not be saved.");
            }
        }

        private void Notify(string message)
        {
            Notification.PostTicker(message, false, false);
        }

        private void LogRuntime(string eventName, string detail)
        {
            if (_log != null) _log.Runtime(eventName, detail);
        }

        private void LogDebug(string eventName, string detail)
        {
            if (_log != null) _log.Debug(eventName, detail);
        }

        private void LogException(string eventName, Exception exception)
        {
            if (_log != null) _log.Exception(eventName, exception);
        }
    }
}
