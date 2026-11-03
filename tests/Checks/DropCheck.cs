using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Soundboard;
using ComData = System.Runtime.InteropServices.ComTypes.IDataObject;

internal static class DropCheck
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Transfer(IntPtr self, IntPtr data, uint keys, long point, ref uint effect);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Over(IntPtr self, uint keys, long point, ref uint effect);
    private static T Method<T>(IntPtr target, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(target), slot * IntPtr.Size));
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr window, string name);
    internal static void Run(string? corpus = null, string? proof = null)
    {
        string root = Directory.CreateTempSubdirectory("SoundboardDrop-").FullName;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/Soundboard;component/Theme.xaml") };
        var library = new Library(root, Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds"));
        var window = new MainWindow(library) { ShowInTaskbar = false, Opacity = 0, ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var source = Path.Combine(root, "Drop check.wav");
            var unnamed = Path.Combine(root, ".wav");
            ImportLevelsCheck.WriteQuietVoice(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds"), source);
            File.Copy(source, unnamed);
            var folder = Directory.CreateDirectory(Path.Combine(root, "drop folder")).FullName;
            var webm = Path.Combine(folder, "clip.webm");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Media", "clip.webm"), webm);
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "Ignored when a folder is dropped.");
            Drop(window, corpus == null ? new[] { source, unnamed, folder, webm } : new[] { corpus });
            if (corpus != null)
            {
                var imported = library.State.Sounds.Skip(6).ToList();
                var formats = imported.Select(s => "." + s.Format).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!formats.SetEquals(Library.Extensions) || imported.Count != Library.Extensions.Length)
                    throw new Exception("Corpus mismatch. Missing: " + string.Join(", ", Library.Extensions.Where(ext => !formats.Contains(ext))));
                foreach (var sound in imported)
                    if (!Wave.Read(library.FileFor(sound)).Bytes.Any(b => b != 0)) throw new Exception($"Empty waveform: {sound.Name}");
                Console.WriteLine($"OLE folder import passed for all {formats.Count} extensions with nonzero stereo waveforms.");
                return;
            }
            var added = library.State.Sounds.Single(s => s.Name == "Drop check");
            var restored = new Library(root, Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds"));
            if (!restored.State.Sounds.Any(s => s.Name == "Untitled sound")) throw new Exception("A nameless file needs a usable library name.");
            if (!restored.State.Sounds.Any(s => s.Name == "clip" && s.Format == "WEBM")) throw new Exception("Dropping a video must import its audio track.");
            if (restored.State.Sounds.Count != 9) throw new Exception("A file dropped alongside its folder must appear only once.");
            var search = (TextBox)window.FindName("Search");
            search.Text = "dck";
            var list = (ListBox)window.FindName("Sounds");
            if (list.Items.Count != 1 || !Equals(list.SelectedItem, added)) throw new Exception("Imported clip was not searchable and selected.");
            if (!File.Exists(source) || Wave.Read(library.FileFor(added)).Frames == 0) throw new Exception("The dropped clip did not import.");
            if (proof != null) ImportLevelsCheck.Export(proof, source, library.FileFor(added));
            CheckBatchCancel(window, library, webm, source);
            CheckPalette(window, library, added);
            Console.WriteLine("Desktop passed: OLE import, search recovery, menu navigation and safe cancellation.");
        }
        finally { window.Quit(); Directory.Delete(root, true); }
    }

    private static void Drop(MainWindow window, string[] paths)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        // Exercise the OLE target registered by the real WPF window. No global mouse input.
        var targetPtr = GetProp(hwnd, "OleDropTargetInterface");
        if (targetPtr == IntPtr.Zero) throw new Exception("The launcher did not register a Windows file drop target.");
        Guid iid = new("00000122-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(targetPtr, in iid, out var target));
        var pt = window.PointToScreen(new Point(100, 115));
        long point = ((long)(int)pt.Y << 32) | (uint)(int)pt.X;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        var transfer = Marshal.GetComInterfaceForObject(data, typeof(ComData));
        try
        {
            uint effect = 1;
            Marshal.ThrowExceptionForHR(Method<Transfer>(target, 3)(target, transfer, 1, point, ref effect));
            effect = 1;
            Marshal.ThrowExceptionForHR(Method<Over>(target, 4)(target, 1, point, ref effect));
            if (effect != 1) throw new Exception("The launcher did not advertise a copy drop.");
            effect = 1;
            Marshal.ThrowExceptionForHR(Method<Transfer>(target, 6)(target, transfer, 0, point, ref effect));
        }
        finally { Marshal.Release(transfer); Marshal.Release(target); }
        var clock = Stopwatch.StartNew();
        var importing = (TextBlock)window.FindName("ImportLabel");
        while (importing.Text != "Import" && clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(10);
        }
        if (importing.Text != "Import") throw new Exception("The import did not finish.");
    }

    private static void CheckBatchCancel(MainWindow window, Library library, string first, string second)
    {
        int before = library.State.Sounds.Count;
        var progress = (TextBlock)window.FindName("ImportLabel");
        var button = (Button)window.FindName("ImportButton");
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        void StopAtSecond(object? sender, EventArgs e)
        {
            if (progress.Text == "Importing 2 of 2")
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        descriptor.AddValueChanged(progress, StopAtSecond);
        try { Drop(window, [first, second]); }
        finally { descriptor.RemoveValueChanged(progress, StopAtSecond); }
        if (library.State.Sounds.Count != before + 1 || Directory.EnumerateFiles(library.ClipsDirectory).Any(p => p.EndsWith(".part") || p.EndsWith(".decode")))
            throw new Exception("Cancelling a batch must keep completed pads and remove unfinished files.");
    }

    private static void CheckPalette(MainWindow window, Library library, Sound added)
    {
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool KeyFrom(UIElement source, Key key)
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            source.RaiseEvent(input);
            return input.Handled;
        }

        var search = Find<TextBox>("Search");
        search.Text = "no-such-sound";
        Click("EmptyAction");
        if (search.Text != "" || Find<ListBox>("Sounds").Items.Count == 0) throw new Exception("Clear search must restore the library.");
        var pads = Find<ListBox>("Sounds");
        window.UpdateLayout();
        Point Position(int index) => ((ListBoxItem)pads.ItemContainerGenerator.ContainerFromIndex(index)).TranslatePoint(new Point(), window);
        if (Position(0).Y != Position(1).Y || Position(1).X <= Position(0).X || Position(3).Y <= Position(0).Y)
            throw new Exception("Sound pads must form a three-column grid.");
        pads.SelectedIndex = 0;
        KeyFrom(pads, Key.Down); KeyFrom(pads, Key.Right);
        if (pads.SelectedIndex != 4) throw new Exception("Arrow keys must move between rows and columns.");
        var favoriteTab = Find<RadioButton>("FavoriteTab");
        if (KeyFrom(favoriteTab, Key.Enter) || KeyFrom(favoriteTab, Key.Down) || window.CanStopPlayback || pads.SelectedIndex != 4)
            throw new Exception("Keys on a filter must not play a sound or move the selected pad.");
        favoriteTab.IsChecked = true;
        if (pads.Items.Cast<Sound>().Any(s => !s.Favorite)) throw new Exception("Selecting Favorites must filter the board.");
        Find<RadioButton>("AllTab").IsChecked = true;

        Click("RoutingButton");
        KeyFrom(Find<TextBox>("MenuSearch"), Key.Escape);
        if (Find<Border>("MenuPanel").Visibility != Visibility.Collapsed) throw new Exception("Esc from the footer route should return to the library.");
        Click("ActionsButton");
        var filter = Find<TextBox>("MenuSearch");
        filter.Text = "no-such-action";
        if (Find<TextBlock>("NoCommands").Visibility != Visibility.Visible) throw new Exception("A missing action needs a recovery state.");
        Click("ClearMenuFilter");
        filter.Text = "audio routing";
        KeyFrom(filter, Key.Enter);
        Click("MenuBack");
        if (Find<TextBlock>("MenuHint").Text != "Search actions...") throw new Exception("Back must return to the menu that opened routing.");
        window.FocusSearch();

        search.Text = added.Name;
        search.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Find<ListBox>("Sounds").Focus();
        KeyFrom(Find<ListBox>("Sounds"), Key.Delete);
        var cancel = Find<Button>("CancelButton");
        if (Find<Border>("EditPanel").Visibility != Visibility.Visible || KeyFrom(cancel, Key.Enter))
            throw new Exception("Enter on Cancel must reach the Cancel button.");
        Click("CancelButton");
        if (!library.State.Sounds.Any(s => s.Id == added.Id)) throw new Exception("Cancelling remove must preserve the clip.");

        // Populate this disposable library in memory to test scrolling without copying thousands of audio files.
        library.State.Sounds.AddRange(Enumerable.Range(1, 4096).Select(i => added with { Id = Guid.NewGuid(), Name = $"Large library pad {i}" }));
        search.Clear(); window.UpdateLayout();
        int realized = Enumerable.Range(0, pads.Items.Count).Count(i => pads.ItemContainerGenerator.ContainerFromIndex(i) != null);
        if (pads.Items.Count < 4096 || realized is 0 or > 100) throw new Exception($"A large library realized {realized} pad controls.");
        Console.WriteLine($"Grid rendered {realized} pad controls for {pads.Items.Count} clips.");
    }
}
