// DiskBar - cienki pasek dyskow dokowany do gornej (lub dolnej) krawedzi ekranu.
// Pokazuje kazdy dysk staly osobno w zwartej formie: litera, pasek zajetosci,
// wolne/calkowite (GB) i odczyt/zapis (MB/s). Szczegoly w dymku po najechaniu mysza.
// Liczba pozycji dopasowuje sie sama do liczby dyskow (1..N); gdy nie mieszcza sie
// w jednym rzedzie, pasek zawija je na kolejne rzedy i sam sie powieksza.
// Jesli dziala SysBar, mechanizm AppBar sam ustawia DiskBar pod nim (bez nakladania).
// Kompilacja: build.cmd (csc.exe z .NET Framework 4.x, nic nie trzeba instalowac).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace DiskBar
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
        public const uint ABE_TOP = 1, ABE_BOTTOM = 3;
        public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
        public const int WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGED = 0x0047, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;
        public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string msg);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        public const uint SPI_SETWORKAREA = 0x002F;
        public const uint SPIF_SENDCHANGE = 0x0002;
    }

    class DiskInfo
    {
        public string Name;     // "C:"
        public string Label;    // etykieta woluminu (moze byc pusta)
        public double UsedPct = double.NaN;
        public long Free, Total;
        public double ReadBps, WriteBps;
    }

    class Snapshot
    {
        public List<DiskInfo> Disks = new List<DiskInfo>();
        public long TotalFree, TotalAll;
    }

    // Zbiera pomiary dyskow w osobnym watku, zeby pasek nigdy sie nie przycinal.
    class Sampler
    {
        readonly Action<Snapshot> callback;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread thread;

        readonly Dictionary<string, PerformanceCounter> readCtrs = new Dictionary<string, PerformanceCounter>();
        readonly Dictionary<string, PerformanceCounter> writeCtrs = new Dictionary<string, PerformanceCounter>();

        public Sampler(Action<Snapshot> callback) { this.callback = callback; }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop() { stop.Set(); }

        void EnsureCounter(string driveLetter)
        {
            if (readCtrs.ContainsKey(driveLetter)) return;
            try
            {
                var rc = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", driveLetter, true);
                var wc = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", driveLetter, true);
                try { rc.NextValue(); wc.NextValue(); } catch { }
                readCtrs[driveLetter] = rc;
                writeCtrs[driveLetter] = wc;
            }
            catch { }
        }

        void Run()
        {
            // pierwszy pomiar od razu, potem co 1 s
            do
            {
                Snapshot s = new Snapshot();
                try
                {
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                            long total = d.TotalSize;
                            long free = d.TotalFreeSpace;
                            if (total <= 0) continue;
                            if (total < 1024L * 1024 * 1024) continue; // pomin < 1 GB (czytniki kart itp.)
                            DiskInfo di = new DiskInfo();
                            di.Name = d.Name.TrimEnd('\\');
                            try { di.Label = d.VolumeLabel; } catch { di.Label = ""; }
                            di.Total = total;
                            di.Free = free;
                            di.UsedPct = 100.0 * (total - free) / total;

                            EnsureCounter(di.Name);
                            PerformanceCounter rc, wc;
                            if (readCtrs.TryGetValue(di.Name, out rc))
                                try { di.ReadBps = Math.Max(0, rc.NextValue()); } catch { }
                            if (writeCtrs.TryGetValue(di.Name, out wc))
                                try { di.WriteBps = Math.Max(0, wc.NextValue()); } catch { }

                            s.Disks.Add(di);
                            s.TotalAll += total;
                            s.TotalFree += free;
                        }
                        catch { }
                    }
                }
                catch { }
                callback(s);
            }
            while (!stop.WaitOne(1000));
        }
    }

    // Jedna pozycja paska (kompaktowa): litera, miernik zajetosci, wolne/calkowite, odczyt/zapis.
    // Szczegoly (etykieta, %, jednostki) w dymku po najechaniu mysza.
    class Cell
    {
        public readonly StackPanel Root = new StackPanel();
        readonly TextBlock size = new TextBlock();
        readonly TextBlock speed = new TextBlock();
        readonly Border fill = new Border();
        const double MeterWidth = 22;
        public const double Gap = 12; // odstep miedzy pozycjami

        static readonly Brush Dim = Frozen("#9A9AA2");
        static readonly Brush Text = Frozen("#F2F2F4");
        static readonly Brush Track = Frozen("#3A3A42");
        static readonly Brush Low = Frozen("#5FCB6C");
        static readonly Brush Mid = Frozen("#F2C94C");
        static readonly Brush High = Frozen("#FF6B6B");

        public static Brush Frozen(string hex)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        // big = dysk od 1000 GB (rozmiar zapisywany w TB, np. 1.3T/1.9T - szersze pole).
        public Cell(string label, bool big)
        {
            Root.Orientation = Orientation.Horizontal;
            Root.VerticalAlignment = VerticalAlignment.Center;
            Root.Margin = new Thickness(0, 0, Gap, 0);
            Root.Height = BarWindow.RowHeight;
            Root.Background = Brushes.Transparent; // dymek na calej pozycji

            // Stale szerokosci pol (liczone dla najdluzszego mozliwego zapisu danego dysku),
            // zeby uklad nie zalezal od biezacych wartosci i pozycje nie przeskakiwaly miedzy rzedami.
            double labelW = TextWidth(FontWeights.SemiBold, label);
            double sizeW = Bars.ShowMB
                ? TextWidth(FontWeights.SemiBold, big ? "8888888/8888888" : "888888/888888")
                : big ? TextWidth(FontWeights.SemiBold, "8.8T/8.8T", "88T/88T", "888/8.8T", "888/88T")
                      : TextWidth(FontWeights.SemiBold, "888/888");
            double speedW = TextWidth(FontWeights.Normal, "888/888");

            TextBlock l = new TextBlock();
            l.Text = label;
            l.Width = labelW;
            l.Foreground = Dim;
            l.FontWeight = FontWeights.SemiBold;
            l.VerticalAlignment = VerticalAlignment.Center;
            Root.Children.Add(l);

            Border track = new Border();
            track.Width = MeterWidth;
            track.Height = 5;
            track.CornerRadius = new CornerRadius(2.5);
            track.Background = Track;
            track.Margin = new Thickness(Space, 1, 0, 0);
            track.VerticalAlignment = VerticalAlignment.Center;
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            fill.CornerRadius = new CornerRadius(2.5);
            fill.Width = 0;
            track.Child = fill;
            Root.Children.Add(track);

            size.Width = sizeW;
            size.Foreground = Text;
            size.FontWeight = FontWeights.SemiBold;
            size.Margin = new Thickness(Space, 0, 0, 0);
            size.VerticalAlignment = VerticalAlignment.Center;
            size.Text = "–";
            Root.Children.Add(size);

            speed.Width = speedW;
            speed.Foreground = Dim;
            speed.Margin = new Thickness(Space, 0, 0, 0);
            speed.VerticalAlignment = VerticalAlignment.Center;
            Root.Children.Add(speed);

            Width = labelW + Space + MeterWidth + Space + sizeW + Space + speedW;
            Root.Width = Width;
        }

        public readonly double Width; // stala szerokosc pozycji (bez odstepu Gap)
        const double Space = 4;

        public static double TextWidth(FontWeight weight, params string[] samples)
        {
            double w = 0;
            foreach (string s in samples)
            {
                TextBlock t = new TextBlock();
                t.Text = s;
                t.FontFamily = new FontFamily(BarWindow.Font);
                t.FontSize = BarWindow.FontPx;
                t.FontWeight = weight;
                t.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                w = Math.Max(w, t.DesiredSize.Width);
            }
            return Math.Ceiling(w) + 1;
        }

        public void Set(double pct, string sizeText, string speedText, string tip)
        {
            if (double.IsNaN(pct)) fill.Width = 0;
            else
            {
                fill.Width = MeterWidth * Math.Max(0, Math.Min(100, pct)) / 100.0;
                fill.Background = pct >= 90 ? High : pct >= 75 ? Mid : Low;
            }
            size.Text = sizeText;
            speed.Text = speedText;
            Root.ToolTip = tip;
        }
    }

    class BarWindow : Window
    {
        // Jeden rzad = 30 px. Gdy dyski nie mieszcza sie w jednym rzedzie, pasek zawija je
        // na kolejne rzedy (po RowHeight px) i sam rezerwuje wiecej miejsca na ekranie.
        const double SingleRowHeight = 30;
        public const double RowHeight = 20;
        const double RowPadding = 8;
        public const string Font = "Segoe UI Variable Text, Segoe UI";
        public const double FontPx = 12.5;

        readonly WrapPanel items = new WrapPanel();
        readonly TextBlock summary = new TextBlock();
        readonly List<Cell> cells = new List<Cell>();
        string layoutKey;
        static bool IsBig(DiskInfo d) { return d.Total / 1073741824.0 >= 999.5; }
        int rows = 1;

        public void ResetLayout() { layoutKey = null; } // przebudowa przy nastepnym pomiarze

        // Automatyczne skalowanie: na ekranie wezszym niz 1920 punktow pasek zmniejsza sie
        // proporcjonalnie (min. MinScale), a jesli dalsze lekkie zmniejszenie oszczedzi rzad - tez.
        const double RefWidth = 1920;
        const double MinScale = 0.75;
        readonly ScaleTransform zoom = new ScaleTransform(1, 1);
        double scale = 1;

        double BarHeight { get { return (rows <= 1 ? SingleRowHeight : rows * RowHeight + RowPadding) * scale; } }

        IntPtr hwnd;
        uint callbackMsg, taskbarCreatedMsg;
        bool registered, dockQueued, reorderQueued;
        readonly string monitorName;

        public string MonitorName { get { return monitorName; } }

        public BarWindow(string monitorName)
        {
            this.monitorName = monitorName;

            Title = "DiskBar";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0; Width = 800; Height = BarHeight;
            Background = Cell.Frozen("#1C1C20");
            FontFamily = new FontFamily(Font);
            FontSize = FontPx;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            DockPanel root = new DockPanel();
            root.Margin = new Thickness(12, 0, 12, 0);

            // podsumowanie po prawej: laczne wolne / calkowite
            summary.Foreground = Cell.Frozen("#F2F2F4");
            summary.FontWeight = FontWeights.SemiBold;
            summary.VerticalAlignment = VerticalAlignment.Center;
            summary.Margin = new Thickness(16, 0, 0, 0);
            summary.MinWidth = Cell.TextWidth(FontWeights.SemiBold, "Σ 88T/88T", "Σ 8.8T/8.8T");
            summary.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(summary, Dock.Right);
            root.Children.Add(summary);

            items.Orientation = Orientation.Horizontal;
            items.VerticalAlignment = VerticalAlignment.Center;
            items.Margin = new Thickness(0, 0, 16, 0);
            root.Children.Add(items);
            root.LayoutTransform = zoom;
            Content = root;
            SizeChanged += delegate { UpdateRows(); };

            BuildMenu();

            SourceInitialized += OnSourceInitialized;
            ContentRendered += delegate { DockBar(); };
            Closed += OnClosed;
        }

        // ---------- dane ----------

        static string Size(long bytes)
        {
            if (Bars.ShowMB) return (bytes / 1048576.0).ToString("#,0") + " MB";
            double gb = bytes / 1073741824.0;
            if (gb < 1000) return gb.ToString(gb < 100 ? "0.0" : "0") + " GB";
            return (gb / 1024.0).ToString("0.00") + " TB";
        }

        static string Speed(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024.0;
            if (kb < 1000) return kb.ToString("0", CultureInfo.InvariantCulture) + " KB/s";
            double mb = kb / 1024.0;
            if (mb < 1000) return mb.ToString(mb < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB/s";
            return (mb / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB/s";
        }

        // Krotki zapis na pasek: GB bez jednostki, od 1000 GB z "T" (np. 1.8T).
        static string ShortSize(long bytes)
        {
            if (Bars.ShowMB) return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture);
            double gb = bytes / 1073741824.0;
            if (gb < 1000) return gb.ToString("0", CultureInfo.InvariantCulture);
            double tb = gb / 1024.0;
            return tb.ToString(tb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "T";
        }

        // Krotki zapis predkosci: MB/s bez jednostki (ponizej 10 z jednym miejscem po przecinku).
        static string ShortSpeed(double bytesPerSec)
        {
            double mb = bytesPerSec / 1048576.0;
            if (mb < 0.05) return "0";
            if (mb >= 999.5) return Math.Max(1, Math.Round(mb / 1024.0)).ToString("0", CultureInfo.InvariantCulture) + "G"; // szybkie NVMe: 3G (dokladnie w dymku)
            return mb.ToString(mb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
        }

        void Rebuild(Snapshot s)
        {
            items.Children.Clear();
            cells.Clear();
            foreach (DiskInfo d in s.Disks)
            {
                Cell c = new Cell(d.Name, IsBig(d));
                cells.Add(c);
                items.Children.Add(c.Root);
            }
        }

        public void Apply(Snapshot s)
        {
            StringBuilder key = new StringBuilder();
            foreach (DiskInfo d in s.Disks) key.Append(d.Name).Append(IsBig(d) ? "+" : "").Append('|');
            if (key.ToString() != layoutKey)
            {
                layoutKey = key.ToString();
                Rebuild(s);
            }

            for (int i = 0; i < cells.Count && i < s.Disks.Count; i++)
            {
                DiskInfo d = s.Disks[i];
                string tip = d.Name + (string.IsNullOrEmpty(d.Label) ? "" : "  " + d.Label) + "\n"
                    + "Zajęte: " + Math.Round(d.UsedPct).ToString("0") + "%\n"
                    + "Wolne: " + Size(d.Free) + " / " + Size(d.Total) + "\n"
                    + "Odczyt: " + Speed(d.ReadBps) + "\n"
                    + "Zapis: " + Speed(d.WriteBps);
                cells[i].Set(d.UsedPct,
                    ShortSize(d.Free) + "/" + ShortSize(d.Total),
                    ShortSpeed(d.ReadBps) + "/" + ShortSpeed(d.WriteBps),
                    tip);
            }

            summary.Text = "Σ " + ShortSize(s.TotalFree) + "/" + ShortSize(s.TotalAll);
            UpdateRows();
        }

        // Ile rzedow potrzeba przy danej skali (szerokosci liczone w punktach przed skalowaniem).
        int RowsAt(double s, double summaryWidth)
        {
            // 24 = margines paska (2x12), 32 = odstepy miedzy dyskami a podsumowaniem (2x16)
            double avail = ActualWidth / s - 24 - summaryWidth - 32;
            // tak samo jak WrapPanel: pozycja, ktora sie nie miesci, przechodzi do nowego rzedu
            int n = 1;
            double line = 0;
            foreach (Cell c in cells)
            {
                double w = c.Width + Cell.Gap;
                if (line > 0 && line + w > avail) { n++; line = 0; }
                line += w;
            }
            return n;
        }

        // Dobiera skale i liczy rzedy; przy zmianie zmienia wysokosc paska.
        // Szerokosci pozycji sa stale, wiec wynik nie zalezy od biezacych wartosci (brak migotania).
        public void UpdateRows()
        {
            if (cells.Count == 0 || ActualWidth <= 0) return;

            summary.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double sw = summary.DesiredSize.Width;

            double best = 1;
            if (Bars.AutoScale) best = Math.Max(MinScale, Math.Min(1, ActualWidth / RefWidth));
            int bestRows = RowsAt(best, sw);
            if (Bars.AutoScale)
            {
                double floor = Math.Max(MinScale, best * 0.85);
                for (double s = best - 0.05; s >= floor - 0.001; s -= 0.05)
                {
                    int r = RowsAt(s, sw);
                    if (r < bestRows) { best = s; bestRows = r; }
                }
            }

            if (Math.Abs(best - scale) > 0.001 || bestRows != rows)
            {
                scale = best;
                zoom.ScaleX = zoom.ScaleY = best;
                rows = bestRows;
                QueueDock();
            }
        }

        // ---------- ustawienia i menu ----------

        void BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            menu.Opened += delegate
            {
                menu.Items.Clear();

                MenuItem top = new MenuItem();
                top.Header = "Na górze ekranu";
                top.IsChecked = !Bars.Bottom;
                top.Click += delegate { Bars.SetBottom(false); };
                menu.Items.Add(top);

                MenuItem bot = new MenuItem();
                bot.Header = "Na dole ekranu";
                bot.IsChecked = Bars.Bottom;
                bot.Click += delegate { Bars.SetBottom(true); };
                menu.Items.Add(bot);

                menu.Items.Add(new Separator());

                MenuItem mb = new MenuItem();
                mb.Header = "Rozmiary w MB";
                mb.IsChecked = Bars.ShowMB;
                mb.Click += delegate { Bars.SetShowMB(!Bars.ShowMB); };
                menu.Items.Add(mb);

                MenuItem sc = new MenuItem();
                sc.Header = "Automatyczne skalowanie";
                sc.IsChecked = Bars.AutoScale;
                sc.Click += delegate { Bars.SetAutoScale(!Bars.AutoScale); };
                menu.Items.Add(sc);

                MenuItem auto = new MenuItem();
                auto.Header = "Uruchamiaj z Windows";
                auto.IsChecked = Autostart.Wanted();
                auto.Click += delegate { Autostart.Set(!Autostart.Wanted()); };
                menu.Items.Add(auto);

                MenuItem exit = new MenuItem();
                exit.Header = "Zamknij";
                exit.Click += delegate { Bars.Exit(); };
                menu.Items.Add(exit);
            };
            menu.Items.Add(new MenuItem());
            ContextMenu = menu;
        }

        // ---------- dokowanie (AppBar) ----------

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

            callbackMsg = Native.RegisterWindowMessage("DiskBarAppBarMessage");
            taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);

            // Opoznienie pierwszej rejestracji: jezeli SysBar startuje rownoczesnie,
            // 2 s wystarczy, zeby zdazyl zarezerwowac gorny pas pierwszy.
            // DiskBar ABM_QUERYPOS automatycznie doklada sie ponizej.
            DispatcherTimer delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            delay.Tick += delegate { delay.Stop(); Register(); };
            delay.Start();

            DispatcherTimer retry = new DispatcherTimer();
            retry.Interval = TimeSpan.FromSeconds(3);
            retry.Tick += delegate { if (!registered) Register(); };
            retry.Start();
        }

        void Register()
        {
            Native.APPBARDATA d = NewData();
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
            d.uCallbackMessage = callbackMsg;
            registered = Native.SHAppBarMessage(Native.ABM_NEW, ref d) != UIntPtr.Zero;
            if (registered) DockBar();
        }

        Native.APPBARDATA NewData()
        {
            Native.APPBARDATA d = new Native.APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
            d.hWnd = hwnd;
            return d;
        }

        System.Windows.Forms.Screen PickScreen()
        {
            foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
                if (s.DeviceName == monitorName) return s;
            return null;
        }

        // Gdy SysBar zarejestruje sie po DiskBar, DiskBar trafi wyzej niz SysBar.
        // Ta metoda wykrywa zla kolejnosc i wymusza re-rejestracje DiskBar ponizej SysBar.
        void QueueReorderIfNeeded()
        {
            if (reorderQueued) return;
            System.Windows.Forms.Screen screen = PickScreen();
            if (screen == null) return;
            System.Drawing.Rectangle b = screen.Bounds;

            // Znajdz okno SysBar na tym samym monitorze
            IntPtr sysHwnd = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                StringBuilder sb = new StringBuilder(16);
                Native.GetWindowText(h, sb, 16);
                if (sb.ToString() == "SysBar")
                {
                    Native.RECT r;
                    if (Native.GetWindowRect(h, out r) && r.left >= b.Left - 2 && r.left < b.Right)
                    { sysHwnd = h; return false; }
                }
                return true;
            }, IntPtr.Zero);

            if (sysHwnd == IntPtr.Zero) return;

            Native.RECT sr, dr;
            if (!Native.GetWindowRect(sysHwnd, out sr)) return;
            if (!Native.GetWindowRect(hwnd, out dr)) return;

            // Zla kolejnosc: DiskBar blizej krawedzi ekranu niz SysBar
            bool wrongOrder = Bars.Bottom ? dr.bottom > sr.bottom : dr.top < sr.top;
            if (!wrongOrder) return;

            reorderQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                reorderQueued = false;
                Register(); // ABM_REMOVE + ABM_NEW: swieza pozycja, ponizej SysBar
            }));
        }

        public void DockBar()
        {
            if (!registered) return;
            System.Windows.Forms.Screen screen = PickScreen();
            if (screen == null) return;
            System.Drawing.Rectangle b = screen.Bounds;
            bool bottom = Bars.Bottom;

            Native.POINT pt = new Native.POINT();
            pt.x = b.Left + b.Width / 2;
            pt.y = b.Top + b.Height / 2;
            uint dpiX = 96, dpiY = 96;
            try { Native.GetDpiForMonitor(Native.MonitorFromPoint(pt, 2), 0, out dpiX, out dpiY); } catch { }
            int h = (int)Math.Round(BarHeight * dpiY / 96.0);

            // ABM_QUERYPOS uwzglednia miejsce juz zarezerwowane przez inne paski (np. SysBar),
            // wiec gdy SysBar dziala, DiskBar sam ustawia sie pod nim - bez nakladania.
            Native.APPBARDATA d = NewData();
            d.uEdge = bottom ? Native.ABE_BOTTOM : Native.ABE_TOP;
            d.rc.left = b.Left;
            d.rc.right = b.Right;
            d.rc.top = bottom ? b.Bottom - h : b.Top;
            d.rc.bottom = bottom ? b.Bottom : b.Top + h;
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref d);
            if (bottom) d.rc.top = d.rc.bottom - h; else d.rc.bottom = d.rc.top + h;
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref d);

            Native.SetWindowPos(hwnd, IntPtr.Zero, d.rc.left, d.rc.top, d.rc.right - d.rc.left, d.rc.bottom - d.rc.top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            EnforceWorkArea(d.rc, bottom);
        }

        // Windows honoruje rezerwację obszaru roboczego tylko dla pierwszego AppBara na danej krawędzi.
        // Gdy SysBar jest zarejestrowany pierwszy, DiskBar musi wymusić obszar roboczy ręcznie.
        void EnforceWorkArea(Native.RECT reserved, bool bottom)
        {
            System.Windows.Forms.Screen screen = PickScreen();
            if (screen == null) return;
            System.Drawing.Rectangle b = screen.Bounds;
            Native.POINT pt = new Native.POINT { x = b.Left + b.Width / 2, y = b.Top + b.Height / 2 };
            IntPtr hmon = Native.MonitorFromPoint(pt, 2);
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(hmon, ref mi)) return;
            if (!bottom)
            {
                if (mi.rcWork.top >= reserved.bottom) return;
                mi.rcWork.top = reserved.bottom;
            }
            else
            {
                if (mi.rcWork.bottom <= reserved.top) return;
                mi.rcWork.bottom = reserved.top;
            }
            Native.SystemParametersInfo(Native.SPI_SETWORKAREA, 0, ref mi.rcWork, Native.SPIF_SENDCHANGE);
        }

        void QueueDock()
        {
            if (dockQueued) return;
            dockQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                dockQueued = false;
                DockBar();
            }));
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)taskbarCreatedMsg)
            {
                registered = false;
                Register();
            }
            else if (msg == (int)callbackMsg)
            {
                int code = wParam.ToInt32();
                if (code == Native.ABN_POSCHANGED) { QueueDock(); QueueReorderIfNeeded(); }
                else if (code == Native.ABN_FULLSCREENAPP)
                {
                    bool full = lParam != IntPtr.Zero;
                    Topmost = !full;
                    if (full) Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }
                handled = true;
            }
            else if (msg == Native.WM_DISPLAYCHANGE) { Bars.QueueReconcile(); }
            else if (msg == Native.WM_DPICHANGED) { QueueDock(); }
            else if (msg == Native.WM_ACTIVATE && registered)
            {
                Native.APPBARDATA d = NewData();
                Native.SHAppBarMessage(Native.ABM_ACTIVATE, ref d);
            }
            else if (msg == Native.WM_WINDOWPOSCHANGED && registered)
            {
                Native.APPBARDATA d = NewData();
                Native.SHAppBarMessage(Native.ABM_WINDOWPOSCHANGED, ref d);
            }
            return IntPtr.Zero;
        }

        void OnClosed(object sender, EventArgs e)
        {
            if (registered)
            {
                Native.APPBARDATA d = NewData();
                Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
                registered = false;
            }
        }
    }

    // Start z Windows. Zamiar uzytkownika (wlaczony / wylaczony) jest zapisany w Software\DiskBar\Autostart
    // (ustawia go instalator i menu). Pasek pilnuje, zeby wpis w ...\Run sie z nim zgadzal - jesli wpis
    // zniknie (inny instalator, "sprzatacz", przerwana instalacja), pasek odtworzy go sam.
    // Wylaczenie w Menedzerze zadan (StartupApproved) nie usuwa wpisu Run, wiec jest respektowane.
    static class Autostart
    {
        const string AppName = "DiskBar";
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string SettingsPath = @"Software\DiskBar";

        static string ExePath() { return Process.GetCurrentProcess().MainModule.FileName; }
        static string RunValue() { return "\"" + ExePath() + "\""; }

        static bool RunEntryExists()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath))
                return k != null && k.GetValue(AppName) != null;
        }

        static void SaveWanted(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsPath))
                k.SetValue("Autostart", on ? 1 : 0, RegistryValueKind.DWord);
        }

        public static bool Wanted()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsPath))
                {
                    object v = k == null ? null : k.GetValue("Autostart");
                    if (v != null) return Convert.ToInt32(v) != 0;
                }
                return RunEntryExists(); // starsza instalacja bez zapisanego zamiaru
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                SaveWanted(on);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunPath))
                {
                    if (on) k.SetValue(AppName, RunValue());
                    else k.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        public static void Ensure()
        {
            try
            {
                bool want = Wanted();
                SaveWanted(want); // utrwala zamiar takze dla starszych instalacji
                if (!want) return;
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunPath))
                {
                    string cur = k.GetValue(AppName) as string;
                    // Brak wpisu - odtworz. Wpis wskazujacy na nieistniejacy plik (program przeniesiony) - popraw.
                    // Wpis wskazujacy na inny istniejacy plik zostaje (np. kopia testowa uruchomiona z innego folderu).
                    if (cur == null || !File.Exists(cur.Trim('"')))
                        k.SetValue(AppName, RunValue());
                }
            }
            catch { }
        }
    }

    static class Bars
    {
        const string RegPath = @"Software\DiskBar";
        static readonly List<BarWindow> windows = new List<BarWindow>();
        static Sampler sampler;
        static Dispatcher dispatcher;
        static bool reconcileQueued, exiting;

        public static bool Bottom;
        public static bool ShowMB;
        public static bool AutoScale = true;

        public static void Start()
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegPath))
                    if (k != null)
                    {
                        Bottom = Convert.ToInt32(k.GetValue("Bottom", 0)) != 0;
                        ShowMB = Convert.ToInt32(k.GetValue("ShowMB", 0)) != 0;
                        AutoScale = Convert.ToInt32(k.GetValue("AutoScale", 1)) != 0;
                    }
            }
            catch { }

            Autostart.Ensure();
            DispatcherTimer autoCheck = new DispatcherTimer();
            autoCheck.Interval = TimeSpan.FromMinutes(10);
            autoCheck.Tick += delegate { Autostart.Ensure(); };
            autoCheck.Start();

            Reconcile();
            sampler = new Sampler(delegate(Snapshot s)
            {
                dispatcher.BeginInvoke(new Action<Snapshot>(ApplyAll), s);
            });
            sampler.Start();
        }

        static void ApplyAll(Snapshot s)
        {
            foreach (BarWindow w in windows) w.Apply(s);
        }

        public static void SetBottom(bool bottom)
        {
            Bottom = bottom;
            Save("Bottom", bottom);
            foreach (BarWindow w in windows) w.DockBar();
        }

        public static void SetShowMB(bool on)
        {
            ShowMB = on;
            Save("ShowMB", on);
            foreach (BarWindow w in windows) w.ResetLayout();
        }

        public static void SetAutoScale(bool on)
        {
            AutoScale = on;
            Save("AutoScale", on);
            foreach (BarWindow w in windows) w.UpdateRows();
        }

        static void Save(string name, bool val)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegPath))
                    k.SetValue(name, val ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        static void Reconcile()
        {
            if (exiting) return;
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
            HashSet<string> present = new HashSet<string>();
            foreach (System.Windows.Forms.Screen s in screens) present.Add(s.DeviceName);

            foreach (BarWindow w in windows.ToArray())
            {
                if (present.Contains(w.MonitorName)) continue;
                windows.Remove(w);
                w.Close();
            }
            foreach (System.Windows.Forms.Screen s in screens)
            {
                string name = s.DeviceName;
                if (windows.Exists(delegate(BarWindow w) { return w.MonitorName == name; })) continue;
                BarWindow bar = new BarWindow(name);
                windows.Add(bar);
                bar.Show();
            }
            foreach (BarWindow w in windows) w.DockBar();
        }

        public static void QueueReconcile()
        {
            if (reconcileQueued) return;
            reconcileQueued = true;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                reconcileQueued = false;
                Reconcile();
            }));
        }

        public static void Exit()
        {
            exiting = true;
            foreach (BarWindow w in windows.ToArray()) w.Close();
            windows.Clear();
            if (sampler != null) sampler.Stop();
            Application.Current.Shutdown();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "DiskBar_SingleInstance", out created))
            {
                if (!created) return;
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Bars.Start();
                app.Run();
            }
        }
    }
}
