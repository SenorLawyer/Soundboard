using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Soundboard;

public partial class MainWindow
{
    private sealed record Command(string Title, string Glyph, string Keys, Action Run);
    private List<Command> choices = [];
    private string panel = "";
    private string edit = "";
    private Sound? editing;
    private bool routingFromActions;

    private string? ParentPage => panel switch
    {
        "Preview output" or "Clip volume" => "Audio routing",
        "Global shortcut" => "Actions",
        "Audio routing" when routingFromActions => "Actions",
        _ => null
    };
    private void OpenRouting(bool fromActions)
    {
        routingFromActions = fromActions; ShowMenu("Audio routing");
    }
    private void BackClick(object sender, RoutedEventArgs e) => Back();
    private void Back() { if (ParentPage is { } parent) ShowMenu(parent); else FocusSearch(); }

    private void ClosePanel()
    {
        DismissNotice();
        panel = ""; edit = ""; editing = null;
        DismissLayer.Visibility = MenuPanel.Visibility = EditPanel.Visibility = Visibility.Collapsed;
    }
    private void DismissClick(object sender, MouseButtonEventArgs e) { FocusSearch(); e.Handled = true; }
    private void ToggleActions() { if (panel != "" || edit != "") FocusSearch(); else ShowMenu("Actions"); }

    private void ShowMenu(string page)
    {
        ClosePanel(); panel = page;
        var sound = Selected;
        choices = [];
        void Add(string title, string glyph, string keys, Action action) => choices.Add(new(title, glyph, keys, action));
        switch (page)
        {
            case "Actions":
                MenuHeading.Text = sound?.Name ?? "Soundboard";
                if (sound != null)
                {
                    Add("Play to microphone", "\uE768", "Enter", () => _ = Play(sound, false));
                    Add("Preview privately", "\uE7F5", "Ctrl P", Preview);
                    Add(sound.Favorite ? "Remove from favorites" : "Add to favorites", "\uE734", "Ctrl D", () => Favorite(sound));
                    Add("Assign global shortcut", "\uE765", "", () => ShowMenu("Global shortcut"));
                    Add("Rename sound", "\uE8AC", "F2", () => BeginEdit(sound, "Rename"));
                    Add("Remove sound", "\uE74D", "Delete", () => BeginEdit(sound, "Remove"));
                }
                if (CanStopPlayback) Add("Stop playback", "\uE71A", "Ctrl Alt ⌫", Stop);
                Add("Import sounds…", "\uE710", "Ctrl O", ChooseFiles);
                Add("Audio routing", "\uE9E9", "", () => OpenRouting(true));
                Add("Hide Soundboard", "\uE738", shell?.LauncherShortcut ?? "Esc", HidePalette);
                Add("Quit Soundboard", "\uE7E8", "", Quit);
                break;
            case "Audio routing":
                RefreshRoute();
                var routing = library.State.Routing;
                MenuHeading.Text = engine.Ready ? "Microphone · Voicemeeter Out B1" : "Voicemeeter Potato is offline";
                Add($"Preview output · A{routing.PreviewBus + 1}", "\uE7F5", "›", () => ShowMenu("Preview output"));
                Add("Hear clips while sending", "\uE767", routing.Monitor ? "On" : "Off", () =>
                { SetRouting(routing with { Monitor = !routing.Monitor }); ShowMenu(page); });
                Add($"Clip volume · {routing.Volume}%", "\uE995", "›", () => ShowMenu("Clip volume"));
                Add("Reconnect to Potato", "\uE72C", "", () => { Stop(); RefreshRoute(); Report(RoutingButton.ToolTip?.ToString() ?? ""); });
                break;
            case "Preview output":
                RefreshRoute();
                MenuHeading.Text = "Preview output";
                for (int i = 0; i < 2; ++i)
                {
                    int bus = i;
                    Add($"A{i + 1} · {engine.Devices[i]}", "\uE7F5", library.State.Routing.PreviewBus == i ? "✓" : "", () =>
                    { SetRouting(library.State.Routing with { PreviewBus = bus }); ShowMenu("Audio routing"); });
                }
                break;
            case "Clip volume":
                MenuHeading.Text = "Clip volume";
                foreach (int v in new[] { 10, 25, 50, 70, 85, 100 })
                    Add($"{v}%", "\uE995", v == library.State.Routing.Volume ? "✓" : "", () =>
                    { SetRouting(library.State.Routing with { Volume = v }); ShowMenu("Audio routing"); });
                break;
            case "Global shortcut" when sound != null:
                MenuHeading.Text = "Global shortcut · " + sound.Name;
                Add("Clear shortcut", "\uE894", sound.Slot == 0 ? "✓" : "", () => SetSlot(sound, 0));
                for (int i = 1; i <= 9; ++i)
                {
                    int slot = i;
                    var owner = library.State.Sounds.FirstOrDefault(s => s.Slot == slot);
                    if (owner != null && owner.Id != sound.Id) continue;
                    Add($"Ctrl + Alt + {i}", "\uE765", sound.Slot == i ? "✓" : "", () => SetSlot(sound, slot));
                }
                break;
        }
        MenuHint.Text = page == "Actions" ? "Search actions..." : "Filter options...";
        MenuBack.Visibility = ParentPage == null ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRowSpan(DismissLayer, 2);
        MenuSearch.Clear(); FilterCommands();
        DismissLayer.Visibility = MenuPanel.Visibility = Visibility.Visible;
        MenuPanel.UpdateLayout(); MenuSearch.Focus(); Keyboard.Focus(MenuSearch);
    }
    private void SetRouting(Routing routing)
    {
        library.SetRouting(routing);
        if (active != null) NativeAudio.sb_gain(routing.Volume / 100f);
    }
    private void SetSlot(Sound sound, int slot)
    {
        if (sound.Slot == slot) return;
        if (slot != 0 && shell?.BindSlot(slot) != true) { Report("That global shortcut is already in use."); return; }
        try { library.Replace(sound with { Slot = slot }); }
        catch { if (slot != 0) shell?.UnbindSlot(slot); throw; }
        if (sound.Slot != 0) shell?.UnbindSlot(sound.Slot);
        Rebuild(sound.Id);
        Report(slot == 0 ? "Shortcut cleared." : $"Press Ctrl+Alt+{slot} to play {sound.Name}.");
    }
    private void Favorite(Sound sound)
    {
        library.Replace(sound with { Favorite = !sound.Favorite }); Rebuild(sound.Id);
    }
    private void FilterCommands()
    {
        var filter = MenuSearch.Text.Trim();
        MenuHint.Visibility = filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearMenuFilter.Visibility = filter.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Commands.ItemsSource = choices.Where(c => Library.Matches(c.Title, filter)).ToList();
        NoCommands.Text = panel == "Actions" ? "No matching actions" : "No matching options";
        NoCommands.Visibility = Commands.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Commands.SelectedItem = filter.Length == 0 ? choices.FirstOrDefault(c => c.Keys == "✓") : null;
        if (Commands.SelectedIndex < 0 && Commands.Items.Count > 0) Commands.SelectedIndex = 0;
        if (Commands.SelectedItem != null) Commands.ScrollIntoView(Commands.SelectedItem);
    }
    private void ClearMenuClick(object sender, RoutedEventArgs e) { MenuSearch.Clear(); MenuSearch.Focus(); }
    private void MenuSearchChanged(object sender, TextChangedEventArgs e) { if (Commands != null) FilterCommands(); }
    private void ExecuteCommand()
    {
        if (Commands.SelectedItem is not Command command) return;
        FocusSearch(); Guard(command.Run);
    }
    private void CommandClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Commands, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        Commands.SelectedItem = item.Content; e.Handled = true; ExecuteCommand();
    }
    private void BeginEdit(Sound sound, string action)
    {
        ClosePanel(); edit = action; editing = sound;
        bool rename = action == "Rename";
        EditTitle.Text = rename ? "Rename sound" : "Remove sound?";
        EditHint.Text = rename ? "Sound name" : $"Remove “{sound.Name}” from your library? Your original file stays on disk.";
        EditText.Text = sound.Name; ConfirmEdit.Content = rename ? "Save name" : "Remove";
        EditInputBorder.Visibility = rename ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRowSpan(DismissLayer, 3);
        DismissLayer.Visibility = EditPanel.Visibility = Visibility.Visible;
        EditPanel.UpdateLayout();
        if (rename) { EditText.Focus(); EditText.SelectAll(); } else CancelButton.Focus();
    }
    private void CancelEdit(object sender, RoutedEventArgs e) => FocusSearch();
    private void CommitEdit(object sender, RoutedEventArgs e) => FinishEdit();
    private void FinishEdit()
    {
        if (editing is not { } sound) return;
        Guard(() =>
        {
            if (edit == "Rename")
            {
                var name = EditText.Text.Trim();
                if (name.Length == 0) { EditHint.Text = "Enter a sound name."; return; }
                library.Replace(sound with { Name = name });
                if (!Library.Matches(name, Search.Text.Trim())) Search.Clear();
            }
            else
            {
                if (active?.Id == sound.Id || loading) Stop();
                library.Remove(sound);
                if (sound.Slot > 0) shell?.UnbindSlot(sound.Slot);
            }
            FocusSearch(); Rebuild(sound.Id);
        });
    }

    private static void Move(ListBox list, int direction)
    {
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + direction, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        bool boardInput = e.OriginalSource is UIElement source && source is not ButtonBase &&
            (source == Search || source == Sounds || Search.IsAncestorOf(source) || Sounds.IsAncestorOf(source));
        if (key == Key.Escape)
        {
            if (edit != "") FocusSearch();
            else if (panel != "") Back();
            else if (active != null || loading) Stop();
            else if (Search.Text.Length != 0) Search.Clear();
            else HidePalette();
        }
        else if (edit != "") { if (key == Key.Enter && EditText.IsKeyboardFocused) FinishEdit(); else return; }
        else if (ctrl && key == Key.K) ToggleActions();
        else if (panel != "")
        {
            if (key is Key.Up or Key.Down) Move(Commands, key == Key.Up ? -1 : 1);
            else if (key == Key.Enter && e.OriginalSource is not ButtonBase) ExecuteCommand();
            else return;
        }
        else if (ctrl && key == Key.O) ChooseFiles();
        else if (boardInput && (key is Key.Up or Key.Down || (key is Key.Left or Key.Right && (!Search.IsKeyboardFocused || Search.Text.Length == 0))))
        {
            Move(Sounds, key switch { Key.Up => -PadColumns, Key.Down => PadColumns, Key.Left => -1, _ => 1 });
            if (Sounds.SelectedItem is { } selected && Sounds.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem pad) pad.Focus();
        }
        else if (ctrl && key == Key.F) { FocusSearch(); Search.SelectAll(); }
        else if (key == Key.Back && Sounds.IsKeyboardFocusWithin && Search.Text.Length > 0)
        { Search.Focus(); Search.Text = Search.Text[..^1]; Search.CaretIndex = Search.Text.Length; }
        else if (key == Key.Enter && boardInput) Primary();
        else if (Selected is { } sound)
        {
            if (ctrl && key == Key.P) Preview();
            else if (ctrl && key == Key.D) Guard(() => Favorite(sound));
            else if (boardInput && key == Key.F2) BeginEdit(sound, "Rename");
            else if (boardInput && key == Key.Delete && (Search.Text == "" || !Search.IsKeyboardFocusWithin)) BeginEdit(sound, "Remove");
            else return;
        }
        else return;
        e.Handled = true;
    }
}
