using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Soundboard;

public partial class MainWindow : Window
{
    private readonly Library library;
    private readonly AudioEngine engine = new();
    private readonly DispatcherTimer transport = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private Shell? shell;
    private ListCollectionView? view;
    private Sound? active;
    private int generation, lastPosition, activeFrames;
    private long lastAdvance;
    private bool loading, importing, quitAfterImport, quitting, previewing;
    private Sound? Selected => Sounds.SelectedItem as Sound;

    public MainWindow(Library library)
    {
        this.library = library;
        InitializeComponent();
        Rebuild();
        Activated += (_, _) => { if (shell != null && active == null) RefreshRoute(); };
        Sounds.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (panel == "" && edit == "") Search.Focus(); });
        transport.Tick += (_, _) => Tick();
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); Notice.Visibility = Visibility.Collapsed; };
        ContentRendered += FirstRender;
    }

    private void FirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= FirstRender;
        RefreshRoute(); FocusSearch();
        if (shell?.Unavailable.Count > 0) Report("Shortcut already in use: " + string.Join(", ", shell.Unavailable));
        else if (shell?.LauncherShortcut == "Ctrl+Shift+Space") Report("Ctrl+Alt+Space is in use. Press Ctrl+Shift+Space to open Soundboard.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        shell = new Shell(this, GlobalShortcut);
        if (!shell.Acrylic) Chrome.Background = new SolidColorBrush(Color.FromRgb(35, 34, 38));
        foreach (var sound in library.State.Sounds.Where(s => s.Slot != 0))
            if (!shell.BindSlot(sound.Slot)) shell.Unavailable.Add(sound.Shortcut);
    }

    public void FocusSearch() { ClosePanel(); Search.Focus(); Keyboard.Focus(Search); }
    public void HidePalette() { ClosePanel(); Hide(); }
    public void Quit()
    {
        if (importing) { quitAfterImport = true; Report("Finishing the current import before quitting…"); return; }
        quitting = true;
        Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!quitting) { e.Cancel = true; HidePalette(); return; }
        ++generation;
        transport.Stop(); noticeTimer.Stop();
        engine.Dispose(); shell?.Dispose();
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e) { base.OnClosed(e); Application.Current.Shutdown(); }

    private void Rebuild(Guid? select = null)
    {
        select ??= Selected?.Id;
        view = new ListCollectionView(library.State.Sounds);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Add(new(nameof(Sound.Favorite), ListSortDirection.Descending));
            view.SortDescriptions.Add(new(nameof(Sound.Name), ListSortDirection.Ascending));
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Sound.Section)));
            view.Filter = x => x is Sound sound && Library.Matches(sound.Name, Search.Text.Trim());
        }
        Sounds.ItemsSource = view;
        SelectResult(select);
    }

    private void SelectResult(Guid? id)
    {
        Sounds.SelectedItem = view?.Cast<Sound>().FirstOrDefault(s => s.Id == id);
        if (Sounds.SelectedIndex < 0 && Sounds.Items.Count > 0) Sounds.SelectedIndex = 0;
        bool empty = Sounds.Items.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = library.State.Sounds.Count == 0 ? "Drop your first sound here" : "No matching sounds";
        EmptyHint.Text = library.State.Sounds.Count == 0 ? "Drag in a clip or choose Import." : "Try another name or import a clip.";
        PrimaryButton.IsEnabled = !empty || active != null;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (view == null) return;
        view.Refresh(); SelectResult(null);
    }

    private void Report(string text)
    {
        if (quitting) return;
        if (!IsVisible && shell != null) { shell.Notify(text); return; }
        NoticeText.Text = text; Notice.Visibility = Visibility.Visible;
        noticeTimer.Stop(); noticeTimer.Start();
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ExternalException or BadImageFormatException or EntryPointNotFoundException or DllNotFoundException)
        { Report(ex.Message); }
    }
    private void RefreshRoute()
    {
        Guard(() =>
        {
            var status = engine.Probe();
            RouteDot.Fill = new SolidColorBrush(engine.Ready ? Color.FromRgb(125, 197, 158) : Color.FromRgb(221, 177, 104));
            RouteStatus.Text = engine.Ready ? "Out B1" : "Offline";
            RoutingButton.ToolTip = status;
        });
    }

    private async Task Play(Sound sound, bool preview)
    {
        int request = ++generation;
        loading = true;
        try
        {
            var path = library.FileFor(sound);
            Wave wave = await Task.Run(() => Wave.Read(path));
            if (request != generation || quitting) return;
            engine.Play(wave, library.State.Routing, preview);
            active = sound; activeFrames = wave.Frames; previewing = preview;
            lastAdvance = Stopwatch.GetTimestamp(); lastPosition = -1;
            ProgressLine.Width = 0; ProgressLine.Visibility = Visibility.Visible;
            transport.Start();
            RefreshPlaybackText();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ExternalException or BadImageFormatException or EntryPointNotFoundException or DllNotFoundException)
        {
            if (request == generation) { Stop(); RefreshRoute(); Report(ex.Message); }
        }
        finally { if (request == generation) loading = false; }
    }

    private void Tick()
    {
        int pos = engine.Position;
        if (pos < 0) { EndPlayback(); return; }
        if (pos != lastPosition) { lastAdvance = Stopwatch.GetTimestamp(); lastPosition = pos; }
        else if (Stopwatch.GetElapsedTime(lastAdvance) > TimeSpan.FromSeconds(2))
        {
            EndPlayback(); RefreshRoute(); Report("No audio from Potato. Check A1 or restart its audio engine."); return;
        }
        ProgressLine.Width = Math.Clamp(pos / (double)activeFrames, 0, 1) * Math.Max(0, ActualWidth - 2);
    }

    private void EndPlayback()
    {
        engine.Stop(); transport.Stop(); active = null;
        ProgressLine.Visibility = Visibility.Collapsed; RefreshPlaybackText();
    }
    private void Stop() { ++generation; loading = false; EndPlayback(); }
    private void RefreshPlaybackText()
    {
        PrimaryText.Text = active == null || active.Id != Selected?.Id ? "Play Sound" : "Stop Sound";
        if (active != null) RouteStatus.Text = (previewing ? "Preview · " : "B1 · ") + active.Name;
        else RouteStatus.Text = engine.Ready ? "Out B1" : "Offline";
    }
    private void Primary()
    {
        if (Selected is not { } sound) return;
        if (active?.Id == sound.Id) Stop(); else _ = Play(sound, false);
    }
    private void GlobalShortcut(int id)
    {
        if (id == 2) { Stop(); return; }
        var sound = library.State.Sounds.FirstOrDefault(s => s.Slot == id - 10);
        if (sound != null) _ = Play(sound, false);
    }

    private void ImportClick(object sender, RoutedEventArgs e) => ChooseFiles();
    private void ChooseFiles()
    {
        ClosePanel();
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import sounds", Multiselect = true,
            Filter = $"Audio clips|{string.Join(';', Library.Extensions.Select(x => "*" + x))}|All files|*.*" };
        if (dialog.ShowDialog(this) == true) _ = Import(dialog.FileNames);
    }
    private async Task Import(string[] files)
    {
        if (importing) { Report("An import is already running."); return; }
        importing = true;
        int added = 0, skipped = 0;
        string firstError = "";
        Guid? first = null;
        try
        {
            foreach (string source in files)
            {
                if (quitting || quitAfterImport) return;
                var id = Guid.NewGuid();
                var path = Path.Combine(library.ClipsDirectory, id.ToString("N") + ".wav");
                var temp = path + ".part";
                try
                {
                    var file = new FileInfo(source);
                    if (!file.Exists || !Library.Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException("Choose a supported audio file.");
                    if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("The source must be under 256 MB.");
                    Report($"Importing {file.Name}…");
                    int ms = await Task.Run(() =>
                    {
                        int result = NativeAudio.sb_decode(file.FullName, temp, out int duration);
                        if (result == unchecked((int)0x800700DF)) throw new InvalidDataException("Clips must fit within five minutes and 128 MB of decoded audio.");
                        if (result < 0) throw new InvalidDataException("Windows could not decode this audio format.");
                        return duration;
                    });
                    if (quitting || quitAfterImport) return;
                    File.Move(temp, path);
                    var name = Path.GetFileNameWithoutExtension(file.Name).Trim();
                    if (name.Length == 0) name = "Untitled sound";
                    var sound = new Sound(id, name, ms, id.ToByteArray()[0] % 6, file.Extension[1..].ToUpperInvariant());
                    try { library.Add(sound); } catch { File.Delete(path); throw; }
                    first ??= id;
                    added++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ExternalException or DllNotFoundException or BadImageFormatException)
                {
                    if (skipped++ == 0) firstError = $"{Path.GetFileName(source)}: {ex.Message}";
                }
                finally { if (File.Exists(temp)) Guard(() => File.Delete(temp)); }
            }
            if (added > 0) { Search.Clear(); Rebuild(first); }
            var message = added == 1 ? "Imported 1 sound." : $"Imported {added} sounds.";
            Report(skipped == 0 ? message : $"{message} {skipped} skipped. {firstError}");
        }
        finally { importing = false; if (quitAfterImport) Quit(); }
    }

    private void DragOverFiles(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropLayer.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }
    private void DragLeaveFiles(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (pos.X <= 0 || pos.X >= ActualWidth - 1 || pos.Y <= 0 || pos.Y >= ActualHeight - 1) DropLayer.Visibility = Visibility.Collapsed;
    }
    private void DropFiles(object sender, DragEventArgs e)
    {
        DropLayer.Visibility = Visibility.Collapsed;
        ClosePanel();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = Import(files);
        e.Handled = true;
    }
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBox && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void SoundDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Sounds, e.OriginalSource as DependencyObject) is ListBoxItem) Primary();
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PrimaryText != null) RefreshPlaybackText(); }
    private void PlayClick(object sender, RoutedEventArgs e) => Primary();
    private void ActionsClick(object sender, RoutedEventArgs e) => ToggleActions();
    private void RoutingClick(object sender, RoutedEventArgs e) => ShowMenu("Audio routing");
    private void RowMenu(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Sounds, e.OriginalSource as DependencyObject) is ListBoxItem item)
        { Sounds.SelectedItem = item.Content; ShowMenu("Actions"); e.Handled = true; }
    }
}
