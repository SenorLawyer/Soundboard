using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
    private const int PadColumns = 3;
    public static readonly DependencyProperty PlayingPadProperty = DependencyProperty.Register(nameof(PlayingPad), typeof(PadPlayback), typeof(MainWindow));
    public PadPlayback? PlayingPad { get => (PadPlayback?)GetValue(PlayingPadProperty); private set => SetValue(PlayingPadProperty, value); }
    private readonly Library library;
    private readonly AudioEngine engine = new();
    private readonly DispatcherTimer transport = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private Shell? shell;
    private ListCollectionView? view;
    private Sound? active;
    private int generation, lastPosition, activeFrames, pendingLoads;
    private long lastAdvance;
    private bool loading, quitRequested, quitting, previewing;
    private CancellationTokenSource? importCancellation, playbackLoad;
    private Sound? Selected => Sounds.SelectedItem as Sound;
    internal bool CanStopPlayback => active != null || loading;

    public MainWindow(Library library)
    {
        this.library = library;
        InitializeComponent();
        Rebuild();
        Activated += (_, _) => { if (shell != null && active == null) RefreshRoute(); };
        PreviewTextInput += (_, input) =>
        {
            if (panel != "" || edit != "" || input.Text.Length == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
                !(Sounds.IsKeyboardFocusWithin || AllTab.IsKeyboardFocused || FavoriteTab.IsKeyboardFocused)) return;
            Search.Focus(); Search.AppendText(input.Text); Search.CaretIndex = Search.Text.Length;
            input.Handled = true;
        };
        transport.Tick += (_, _) => Tick();
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); Notice.Visibility = Visibility.Collapsed; };
        ContentRendered += FirstRender;
    }

    private void FirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= FirstRender;
        FocusSearch(); RefreshRoute();
        if (shell?.Unavailable.Count > 0) Report("Shortcut already in use: " + string.Join(", ", shell.Unavailable));
        else if (shell?.LauncherShortcut == "Ctrl+Shift+Space" && !library.State.FallbackHintSeen)
        {
            Report("Press Ctrl+Shift+Space to open Soundboard. Ctrl+Alt+Space is already in use.");
            // A read-only library can still play and show its active shortcut.
            try { library.RememberFallbackHint(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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
        quitRequested = true;
        playbackLoad?.Cancel();
        if (importCancellation != null) CancelImport();
        if (pendingLoads != 0 || importCancellation != null) return;
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
        view.Filter = CurrentFilter();
        AllCount.Text = library.State.Sounds.Count.ToString();
        FavoriteCount.Text = library.State.Sounds.Count(s => s.Favorite).ToString();
        Sounds.ItemsSource = view;
        SelectResult(select);
    }
    private Predicate<object> CurrentFilter()
    {
        string query = Search.Text.Trim();
        bool favorites = FavoriteTab.IsChecked == true;
        return item => item is Sound sound && (!favorites || sound.Favorite) && Library.Matches(sound.Name, query);
    }

    private void SelectResult(Guid? id)
    {
        Sounds.SelectedItem = id.HasValue ? view?.Cast<Sound>().FirstOrDefault(s => s.Id == id) : null;
        if (Sounds.SelectedIndex < 0 && Sounds.Items.Count > 0) Sounds.SelectedIndex = 0;
        if (Sounds.SelectedItem != null) Sounds.ScrollIntoView(Sounds.SelectedItem);
        bool empty = Sounds.Items.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        PadHint.Visibility = empty ? Visibility.Hidden : Visibility.Visible;
        bool firstSound = library.State.Sounds.Count == 0;
        bool noFavorites = !firstSound && FavoriteTab.IsChecked == true && Search.Text.Length == 0;
        EmptyTitle.Text = firstSound ? "Add your first sound" : noFavorites ? "No favorites yet" : "No matching sounds";
        EmptyHint.Text = firstSound ? "Drop audio or video files here to make sound pads." : noFavorites ? "Star a pad to keep it here." : "Try a shorter name or clear the search.";
        EmptyAction.Content = firstSound ? "Choose files…" : noFavorites ? "Show all sounds" : "Clear search";
        DropHint.Visibility = !empty && Search.Text.Length == 0 && Sounds.Items.Count <= 6 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPlaybackText();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = Search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (view == null) return;
        DismissNotice();
        view.Filter = CurrentFilter(); SelectResult(null);
    }
    private void ClearSearchClick(object sender, RoutedEventArgs e) { Search.Clear(); FocusSearch(); }
    private void FilterTabChanged(object sender, RoutedEventArgs e)
    {
        if (view == null) return;
        DismissNotice();
        var id = Selected?.Id; view.Filter = CurrentFilter(); SelectResult(id);
    }

    private void Report(string text)
    {
        if (quitting) return;
        if (!IsVisible && shell != null) { shell.Notify(text); return; }
        NoticeText.Text = text; Notice.Visibility = Visibility.Visible;
        noticeTimer.Stop(); noticeTimer.Start();
    }
    private void DismissNotice() { noticeTimer.Stop(); Notice.Visibility = Visibility.Collapsed; }
    private static bool UserFacingError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ExternalException or
        BadImageFormatException or EntryPointNotFoundException or DllNotFoundException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (UserFacingError(ex))
        { Report(ex.Message); }
    }
    private void RefreshRoute()
    {
        Guard(() =>
        {
            var status = engine.Probe();
            RouteDot.Fill = new SolidColorBrush(engine.Ready ? Color.FromRgb(125, 197, 158) : Color.FromRgb(221, 177, 104));
            RoutingButton.ToolTip = status;
            PreviewButton.ToolTip = $"Preview on A{library.State.Routing.PreviewBus + 1} · {engine.Devices[library.State.Routing.PreviewBus]} (Ctrl+P)";
            RefreshPlaybackText();
        });
    }

    private async Task Play(Sound sound, bool preview)
    {
        if (quitRequested) return;
        int request = ++generation;
        ++pendingLoads;
        playbackLoad?.Cancel();
        using var load = new CancellationTokenSource();
        playbackLoad = load;
        loading = true;
        RefreshPlaybackText();
        try
        {
            Wave wave = await library.ReadForPlaybackAsync(sound, load.Token);
            if (request != generation || quitRequested) return;
            engine.Play(wave, library.State.Routing, preview);
            active = sound; activeFrames = wave.Frames; previewing = preview;
            PlayingPad = new(sound.Id, preview ? "Previewing" : "On mic");
            lastAdvance = Stopwatch.GetTimestamp(); lastPosition = -1;
            ProgressLine.Width = 0; ProgressLine.Visibility = Visibility.Visible;
            transport.Start();
            RefreshPlaybackText();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception ex) when (UserFacingError(ex))
        {
            if (request == generation) { Stop(); RefreshRoute(); Report(ex.Message); }
        }
        finally
        {
            if (playbackLoad == load) playbackLoad = null;
            --pendingLoads;
            if (request == generation) { loading = false; RefreshPlaybackText(); }
            if (quitRequested) Quit();
        }
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
        engine.Stop(); transport.Stop(); active = null; PlayingPad = null;
        ProgressLine.Visibility = Visibility.Collapsed; RefreshPlaybackText();
    }
    private void Stop() { ++generation; playbackLoad?.Cancel(); loading = false; EndPlayback(); }
    private void RefreshPlaybackText()
    {
        bool same = active != null && active.Id == Selected?.Id;
        PrimaryText.Text = same && !previewing ? "Stop sound" : "Play to mic";
        PrimaryButton.ToolTip = same && !previewing ? "Stop playback (Enter)" : "Send this sound to Voicemeeter Out B1 (Enter)";
        PreviewText.Text = same && previewing ? "Stop preview" : "Preview";
        PrimaryButton.IsEnabled = PreviewButton.IsEnabled = Selected != null;
        StopButton.Visibility = CanStopPlayback ? Visibility.Visible : Visibility.Collapsed;
        if (active != null) RouteStatus.Text = (previewing ? "Preview · " : "B1 · ") + active.Name;
        else RouteStatus.Text = engine.Ready ? "Out B1" : "Offline";
    }
    private void Primary()
    {
        if (Selected is not { } sound) return;
        if (!previewing && active?.Id == sound.Id) Stop(); else _ = Play(sound, false);
    }
    private void Preview()
    {
        if (Selected is not { } sound) return;
        if (previewing && active?.Id == sound.Id) Stop(); else _ = Play(sound, true);
    }
    private void GlobalShortcut(int id)
    {
        if (id == 2) { Stop(); return; }
        var sound = library.State.Sounds.FirstOrDefault(s => s.Slot == id - 10);
        if (sound != null) _ = Play(sound, false);
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        if (importCancellation != null) CancelImport(); else ChooseFiles();
    }
    private void CancelImport()
    {
        ImportLabel.Text = "Stopping…"; ImportButton.IsEnabled = false;
        importCancellation?.Cancel();
    }
    private void EmptyClick(object sender, RoutedEventArgs e)
    {
        if (library.State.Sounds.Count == 0) ChooseFiles();
        else if (Search.Text.Length != 0) { Search.Clear(); FocusSearch(); }
        else { AllTab.IsChecked = true; FocusSearch(); }
    }
    private void ChooseFiles()
    {
        if (importCancellation != null || quitRequested) return;
        ClosePanel();
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import sounds", Multiselect = true,
            Filter = $"Audio and video files|{string.Join(';', Library.Extensions.Select(x => "*" + x))}|All files|*.*" };
        if (dialog.ShowDialog(this) == true) _ = Import(dialog.FileNames);
    }
    private static (List<string> Files, List<string> Errors) ExpandSources(string[] paths, CancellationToken token)
    {
        var files = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path) { if (seen.Add(path)) files.Add(path); }
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) { Add(full); continue; }
                var children = new List<string>();
                foreach (var child in Directory.EnumerateFiles(full, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (Library.Supports(child)) children.Add(child);
                }
                if (children.Count == 0) errors.Add($"No supported files in {Path.GetFileName(full)}.");
                children.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string child in children) Add(child);
            }
            catch (Exception ex) when (UserFacingError(ex)) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        return (files, errors);
    }

    private async Task Import(string[] paths)
    {
        if (importCancellation != null || quitRequested) return;
        using var cancel = new CancellationTokenSource();
        importCancellation = cancel;
        var token = cancel.Token;
        ImportIcon.Text = "\uE711"; ImportLabel.Text = "Preparing…";
        ImportButton.ToolTip = "Cancel import and keep completed sounds";
        System.Windows.Automation.AutomationProperties.SetName(ImportButton, "Cancel import");
        var pending = new List<Sound>();
        int skipped = 0, total = 0;
        string? firstError = null;
        try
        {
            try
            {
                var selection = await Task.Run(() => ExpandSources(paths, token), token).WaitAsync(token);
                total = selection.Files.Count; skipped = selection.Errors.Count; firstError = selection.Errors.FirstOrDefault();
                for (int i = 0; i < total; ++i)
                {
                    token.ThrowIfCancellationRequested();
                    string source = selection.Files[i];
                    var id = Guid.NewGuid();
                    string temp = library.StagingFile(id);
                    bool ready = false;
                    try
                    {
                        if (!Library.Supports(source)) throw new InvalidDataException("Choose a supported audio or video file.");
                        ImportLabel.Text = total == 1 ? "Importing…" : $"Importing {i + 1} of {total}";
                        int ms = await ClipImporter.ImportAsync(source, temp, token);
                        string name = Path.GetFileNameWithoutExtension(source).Trim();
                        pending.Add(new(id, name.Length == 0 ? "Untitled sound" : name, ms, id.ToByteArray()[0] % 6, Path.GetExtension(source)[1..].ToUpperInvariant()));
                        ready = true;
                    }
                    catch (Exception ex) when (UserFacingError(ex))
                    { skipped++; firstError ??= $"{Path.GetFileName(source)}: {ex.Message}"; }
                    finally { if (!ready) Guard(() => File.Delete(temp)); }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            if (pending.Count > 0)
            {
                library.CommitImports(pending);
                AllTab.IsChecked = true; Search.Clear(); Rebuild(pending[0].Id);
            }
            if (quitRequested) return;
            var message = pending.Count == 1 ? "Imported 1 sound." : $"Imported {pending.Count} sounds.";
            if (cancel.IsCancellationRequested) Report(pending.Count == 0 ? "Import cancelled." : $"Import cancelled. {message}");
            else if (skipped > 0) Report(pending.Count == 0 && skipped == 1 ? firstError! : $"{message} {skipped} skipped. {firstError}");
            else Report(message);
        }
        catch (Exception ex) when (UserFacingError(ex)) { Report(ex.Message); }
        finally
        {
            foreach (var sound in pending) Guard(() => File.Delete(library.StagingFile(sound.Id)));
            importCancellation = null;
            ImportIcon.Text = "\uE710"; ImportButton.IsEnabled = true; ImportLabel.Text = "Import";
            ImportButton.ToolTip = "Import sounds (Ctrl+O)";
            System.Windows.Automation.AutomationProperties.SetName(ImportButton, "Import sounds");
            if (quitRequested) Quit();
        }
    }

    private void DragOverFiles(object sender, DragEventArgs e)
    {
        bool files = importCancellation == null && e.Data.GetDataPresent(DataFormats.FileDrop);
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
    private void PadMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) e.Handled = true;
    }
    private void PadClick(object sender, RoutedEventArgs e)
    {
        Sounds.SelectedItem = ((Button)sender).DataContext; FocusSearch(); Primary();
    }
    private void PadPreviewClick(object sender, RoutedEventArgs e)
    {
        Sounds.SelectedItem = ((Button)sender).DataContext; FocusSearch(); Preview();
    }
    private void PadFavoriteClick(object sender, RoutedEventArgs e)
    {
        var sound = (Sound)((Button)sender).DataContext;
        Guard(() => Favorite(sound)); FocusSearch();
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PrimaryText != null) RefreshPlaybackText(); }
    private void PlayClick(object sender, RoutedEventArgs e) { FocusSearch(); Primary(); }
    private void PreviewClick(object sender, RoutedEventArgs e) { FocusSearch(); Preview(); }
    private void StopClick(object sender, RoutedEventArgs e) { FocusSearch(); Stop(); }
    private void ActionsClick(object sender, RoutedEventArgs e) => ToggleActions();
    private void RoutingClick(object sender, RoutedEventArgs e)
    {
        if (panel == "Audio routing") FocusSearch(); else OpenRouting(false);
    }
    private void RowMenu(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Sounds, e.OriginalSource as DependencyObject) is ListBoxItem item)
        { Sounds.SelectedItem = item.Content; ShowMenu("Actions"); e.Handled = true; }
    }
}
