using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    internal static void Run()
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
            var hwnd = new WindowInteropHelper(window).Handle;
            // Exercise the OLE target registered by the real WPF window. No global mouse input.
            var targetPtr = GetProp(hwnd, "OleDropTargetInterface");
            if (targetPtr == IntPtr.Zero) throw new Exception("The launcher did not register a Windows file drop target.");
            Guid iid = new("00000122-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(targetPtr, in iid, out var target));
            var pt = window.PointToScreen(new Point(100, 115));
            long point = ((long)(int)pt.Y << 32) | (uint)(int)pt.X;
            var data = new DataObject();
            var source = Path.Combine(root, "Drop check.wav");
            var unnamed = Path.Combine(root, ".wav");
            File.Copy(library.FileFor(library.State.Sounds[0]), source);
            File.Copy(source, unnamed);
            data.SetData(DataFormats.FileDrop, new[] { source, unnamed });
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
            while (library.State.Sounds.Count < 8 && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                System.Threading.Thread.Sleep(10);
            }
            var added = library.State.Sounds.Single(s => s.Name == "Drop check");
            var restored = new Library(root, Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds"));
            if (!restored.State.Sounds.Any(s => s.Name == "Untitled sound")) throw new Exception("A nameless file needs a usable library name.");
            var search = (TextBox)window.FindName("Search");
            search.Text = "dck";
            var list = (ListBox)window.FindName("Sounds");
            if (list.Items.Count != 1 || !Equals(list.SelectedItem, added)) throw new Exception("Imported clip was not searchable and selected.");
            if (!File.Exists(source) || Wave.Read(library.FileFor(added)).Frames == 0) throw new Exception("The dropped clip did not import.");
            Console.WriteLine("Desktop passed: OLE drop, real decoder, persistent copy, fuzzy search and selection.");
        }
        finally { window.Quit(); Directory.Delete(root, true); }
    }
}
