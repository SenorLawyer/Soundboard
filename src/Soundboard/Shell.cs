using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Soundboard;

internal sealed class Shell : IDisposable
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private static readonly int ShowMessage = RegisterWindowMessage("SenorLawyer.Soundboard.Show.1");
    private readonly MainWindow window;
    private readonly HwndSource source;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Icon icon;
    private readonly HashSet<int> ids = [];
    private readonly Action<int> shortcut;
    public bool Acrylic { get; }
    public List<string> Unavailable { get; } = [];

    public Shell(MainWindow window, Action<int> shortcut)
    {
        this.window = window;
        this.shortcut = shortcut;
        source = (HwndSource)PresentationSource.FromVisual(window)!;
        source.AddHook(Hook);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int dark = 1, rounded = 2, acrylic = 3;
            DwmSetWindowAttribute(source.Handle, 20, ref dark, 4);
            DwmSetWindowAttribute(source.Handle, 33, ref rounded, 4);
            Acrylic = DwmSetWindowAttribute(source.Handle, 38, ref acrylic, 4) == 0;
            if (Acrylic) source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Soundboard;component/Assets/soundboard.ico")).Stream;
        using var original = new Icon(stream);
        icon = (Icon)original.Clone();
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Soundboard", null, (_, _) => Show());
        menu.Items.Add("Stop sound", null, (_, _) => shortcut(2));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Soundboard", null, (_, _) => window.Quit());
        tray = new Forms.NotifyIcon { Text = "Soundboard", Icon = icon, ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Show(); };
        if (!Bind(1, 0x20)) Unavailable.Add("Ctrl+Alt+Space");
        if (!Bind(2, 0x08)) Unavailable.Add("Ctrl+Alt+Backspace");
    }

    public static void SignalExisting()
    {
        AllowSetForegroundWindow(uint.MaxValue);
        PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private bool Bind(int id, uint key)
    {
        if (ids.Contains(id)) return true;
        if (!RegisterHotKey(source.Handle, id, 0x4003 /* no repeat, Ctrl+Alt */, key)) return false;
        ids.Add(id);
        return true;
    }
    public bool BindSlot(int slot) => slot is >= 1 and <= 9 && Bind(10 + slot, (uint)(0x30 + slot));
    public void UnbindSlot(int slot)
    {
        if (ids.Remove(10 + slot)) UnregisterHotKey(source.Handle, 10 + slot);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == ShowMessage) { Show(); handled = true; }
        else if (msg == 0x0312 && ids.Contains(w.ToInt32()))
        {
            if (w.ToInt32() == 1)
            {
                if (window.IsVisible && window.IsActive) window.HidePalette(); else Show();
            }
            else shortcut(w.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Show()
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        SetForegroundWindow(source.Handle);
        window.Activate();
        window.FocusSearch();
    }

    public void Notify(string message) => tray.ShowBalloonTip(3500, "Soundboard", message, Forms.ToolTipIcon.Info);
    public void Dispose()
    {
        foreach (int id in ids) UnregisterHotKey(source.Handle, id);
        source.RemoveHook(Hook);
        tray.Visible = false;
        tray.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}
