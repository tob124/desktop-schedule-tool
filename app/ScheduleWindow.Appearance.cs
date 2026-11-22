using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ScdDesktop
{
    internal sealed partial class ScheduleWindow
    {
        private readonly StartupRegistration startupRegistration =
            new StartupRegistration(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Application.ExecutablePath);
        private bool titleHidden;
        private bool settingsOpen;
        private IntPtr appearanceWindow;
        private bool backdropRequested;
        private bool glass;
        private bool backdropFocused;
        private HostBackdrop hostBackdrop;

        private void ApplyAppearance()
        {
            var appearance = state.Appearance;
            bool hide = appearance.HideTitleBar || appearance.BrowseOnly;
            if (titleHidden != hide)
            {
                titleHidden = hide;
                Padding = hide ? new Padding((int)Math.Round(5 * DeviceDpi / 96.0)) : Padding.Empty;
                NativeWindow.SetCaptionVisible(Handle, !hide);
            }
            Color paper = ColorTranslator.FromHtml(appearance.Color);
            if (appearanceWindow != Handle || backdropRequested != appearance.Blur)
            {
                appearanceWindow = Handle;
                backdropRequested = appearance.Blur;
                glass = ApplyBackdrop(appearanceWindow, backdropRequested);
            }
            Opacity = glass ? 1 : appearance.Opacity / 100.0;
            BackColor = glass ? Color.Black : paper;
            Color browserBackground = glass ? Color.Transparent : paper;
            if (browser.DefaultBackgroundColor != browserBackground)
                browser.DefaultBackgroundColor = browserBackground;
            Send(new { type = "appearance", preferences = appearance, glass = glass,
                titleHidden = titleHidden, focused = backdropFocused });
        }

        private bool ApplyBackdrop(IntPtr window, bool blur)
        {
            ReleaseBackdrop();
            NativeWindow.SetBackdrop(window, false);
            if (!blur)
            {
                NativeWindow.SetHostBackdrop(window, false);
                return false;
            }

            Opacity = 1;
            // The system Acrylic material can become opaque on deactivation.
            // A host backdrop brush samples behind the same HWND regardless of input focus.
            if (NativeWindow.SetHostBackdrop(window, true)) hostBackdrop = HostBackdrop.TryCreate(window);
            if (hostBackdrop != null) return true;
            NativeWindow.SetHostBackdrop(window, false);
            return NativeWindow.SetBackdrop(window, true);
        }

        private void ReleaseBackdrop()
        {
            if (hostBackdrop == null) return;
            hostBackdrop.Dispose();
            hostBackdrop = null;
        }

        private void UpdateBackdropFocus(bool focused)
        {
            if (backdropFocused == focused) return;
            backdropFocused = focused;
            if (glass) Send(new { type = "backdrop-focus", focused = focused });
        }

        private void ToggleBrowse()
        {
            state.Appearance.BrowseOnly = !state.Appearance.BrowseOnly;
            ApplyAppearance();
            ScheduleSave();
        }

        private void OpenPreferences()
        {
            if (!ready || exitRequested) return;
            var startupEnabled = ReadStartupStatus();
            settingsOpen = true;
            DetachFromDesktop();
            Show();
            BringToFront();
            Activate();
            Send(new { type = "open-settings", preferences = state.Appearance, startOnLogin = startupEnabled });
        }

        private bool? ReadStartupStatus()
        {
            try { return startupRegistration.IsEnabled(); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Runtime.InteropServices.COMException) { return null; }
        }

        private void ClosePreferences()
        {
            if (!settingsOpen) return;
            settingsOpen = false;
            AttachToDesktop();
        }

        private void SavePreferences(AppearancePreferences requested, bool? startOnLogin)
        {
            if (!settingsOpen || requested == null) return;
            var previous = state.Appearance;
            bool? oldStartup = ReadStartupStatus();
            bool startupChanged = false;
            try
            {
                if (startOnLogin.HasValue && startOnLogin != oldStartup)
                {
                    if (!oldStartup.HasValue) throw new IOException("暂时无法读取 Windows 启动设置。");
                    startupRegistration.SetEnabled(startOnLogin.Value);
                    startupChanged = true;
                }
                state.Appearance = AppearancePreferences.Normalize(requested);
                if (!store.Save(state)) throw new IOException("无法保存本机设置。");
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException ||
                    error is System.Runtime.InteropServices.COMException)) throw;
                state.Appearance = previous;
                string text = error.Message;
                if (startupChanged)
                {
                    try { startupRegistration.SetEnabled(oldStartup.Value); }
                    catch (IOException) { text += " 启动项回退失败。"; }
                    catch (UnauthorizedAccessException) { text += " 启动项回退失败。"; }
                    catch (System.Runtime.InteropServices.COMException) { text += " 启动项回退失败。"; }
                }
                Send(new { type = "settings-error", text = text });
                return;
            }
            ApplyAppearance();
            Send(new { type = "settings-saved" });
        }
    }
}
