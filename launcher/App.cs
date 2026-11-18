// Order Up VR launcher: WPF window (XAML embedded as resources), setup wizard, home, settings and game watcher.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace OrderUpVR
{
    public class LauncherWindow : Window
    {
        public enum Mode { Setup, Main }

        Settings s;
        readonly Mode mode;
        readonly bool askUninstall;
        Manifest manifest;
        bool busy;
        FrameworkElement root, currentPage;

        // Game watcher
        Process game;
        DateTime gameStart;
        DispatcherTimer watch, updateTimer;
        bool sawGameWindow, broughtToFront, minimizedForGame;
        string lastNote = "";
        readonly StringBuilder watchLog = new StringBuilder();
        string liveGame, liveVr;
        int liveVrLevel;

        static readonly Brush GoodBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)));
        static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x47)));
        static readonly Brush BadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x6C)));
        static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x00)));
        static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAF)));
        static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF7)));

        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        public LauncherWindow(Settings settings, Mode startMode, bool uninstallRequest)
        {
            s = settings;
            mode = startMode;
            askUninstall = uninstallRequest;

            Title = "Order Up VR";
            Width = 1080; Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanMinimize;
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x15));
            UseLayoutRounding = true;
            TaskbarItemInfo = new TaskbarItemInfo();

            var chrome = new WindowChrome();
            chrome.CaptionHeight = 46;
            chrome.ResizeBorderThickness = new Thickness(0);
            chrome.GlassFrameThickness = new Thickness(0, 0, 0, 1);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);

            root = (FrameworkElement)LoadXaml("Window.xaml");
            Content = root;
            WindowChrome.SetIsHitTestVisibleInChrome(Get<Button>("MinBtn"), true);
            WindowChrome.SetIsHitTestVisibleInChrome(Get<Button>("CloseBtn"), true);
            Get<Button>("MinBtn").Click += delegate { WindowState = WindowState.Minimized; };
            Get<Button>("CloseBtn").Click += delegate { Close(); };

            SourceInitialized += delegate { Native.StyleWindow(new WindowInteropHelper(this).Handle); };
            Loaded += OnLoaded;

            Get<Grid>("Hero").SizeChanged += delegate(object o, SizeChangedEventArgs e)
            {
                ((Grid)o).Clip = new RectangleGeometry(new Rect(e.NewSize), 20, 20);
            };

            if (mode == Mode.Setup) BuildSetup(); else BuildMain();
        }

        // ------------------------------------------------------------------ helpers
        public static object LoadXaml(string name)
        {
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("OrderUpVR." + name))
                return XamlReader.Load(st);
        }

        T Get<T>(string name) where T : class { return root.FindName(name) as T; }

        void Ui(Action a) { Dispatcher.BeginInvoke(a); }

        static IEasingFunction EaseOut() { var e = new CubicEase(); e.EasingMode = EasingMode.EaseOut; return e; }

        static void Animate(UIElement el, DependencyProperty prop, double to, int ms, int delayMs)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = EaseOut();
            a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            el.BeginAnimation(prop, a);
        }

        static TranslateTransform Shift(UIElement el)
        {
            var t = el.RenderTransform as TranslateTransform;
            if (t == null || t.IsFrozen) { t = new TranslateTransform(); el.RenderTransform = t; }
            return t;
        }

        // Fades and slides an element in (used for pages, list items, panels).
        static void Reveal(UIElement el, double dx, double dy, int delayMs)
        {
            el.Opacity = 0;
            TranslateTransform t = Shift(el);
            t.X = dx; t.Y = dy;
            Animate(el, UIElement.OpacityProperty, 1, 320, delayMs);
            var ax = new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)); ax.EasingFunction = EaseOut(); ax.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            var ay = new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)); ay.EasingFunction = EaseOut(); ay.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            t.BeginAnimation(TranslateTransform.XProperty, ax);
            t.BeginAnimation(TranslateTransform.YProperty, ay);
        }

        void ShowPage(string name, bool forward)
        {
            var page = Get<FrameworkElement>(name);
            if (page == currentPage) return;
            FrameworkElement old = currentPage;
            currentPage = page;
            if (old != null)
            {
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
                fade.Completed += delegate { if (old != currentPage) old.Visibility = Visibility.Collapsed; };
                old.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            page.Visibility = Visibility.Visible;
            Reveal(page, forward ? 28 : -28, 0, old != null ? 90 : 0);
        }

        static void Drift(UIElement el, double dx, double dy, double seconds)
        {
            TranslateTransform t = Shift(el);
            var ax = new DoubleAnimation(-dx, dx, TimeSpan.FromSeconds(seconds)); ax.AutoReverse = true; ax.RepeatBehavior = RepeatBehavior.Forever;
            var ay = new DoubleAnimation(-dy, dy, TimeSpan.FromSeconds(seconds * 1.3)); ay.AutoReverse = true; ay.RepeatBehavior = RepeatBehavior.Forever;
            var se = new SineEase(); se.EasingMode = EasingMode.EaseInOut; ax.EasingFunction = se; ay.EasingFunction = se;
            t.BeginAnimation(TranslateTransform.XProperty, ax);
            t.BeginAnimation(TranslateTransform.YProperty, ay);
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Drift(Get<UIElement>("AmbientA"), 60, 40, 11);
            Drift(Get<UIElement>("AmbientB"), 70, 50, 13);
            Drift(Get<UIElement>("HeroGlowA"), 50, 30, 9);
            Drift(Get<UIElement>("HeroGlowB"), 60, 35, 12);
            Drift(Get<UIElement>("HeroGlowC"), 80, 25, 15);
            Reveal(root, 0, 14, 0);

            if (mode == Mode.Main)
            {
                if (!s.NoAutoUpdate || !ModInstalled) CheckUpdates(false);
                if (askUninstall) AskUninstall();
            }
        }

        public void BringToFront()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
            Focus();
        }

        // ------------------------------------------------------------------ toast + dialog
        DispatcherTimer toastTimer;

        void ShowToast(string text, bool ok)
        {
            var toast = Get<Border>("Toast");
            Get<TextBlock>("ToastText").Text = text;
            var icon = Get<TextBlock>("ToastIcon");
            icon.Text = ok ? "" : "";
            icon.Foreground = ok ? GoodBrush : WarnBrush;
            toast.Visibility = Visibility.Visible;
            Reveal(toast, 40, 0, 0);
            if (toastTimer != null) toastTimer.Stop();
            toastTimer = new DispatcherTimer();
            toastTimer.Interval = TimeSpan.FromSeconds(5);
            toastTimer.Tick += delegate
            {
                toastTimer.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
                fade.Completed += delegate { toast.Visibility = Visibility.Collapsed; };
                toast.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            toastTimer.Start();
        }

        // Shows a modal card. buttons: label -> action (null action just closes). The first button is the main one.
        int dialogGen;

        void ShowDialog(string title, string text, string checkText, bool checkDefault, params KeyValuePair<string, Action<bool>>[] buttons)
        {
            dialogGen++;
            var layer = Get<Grid>("DialogLayer");
            Get<TextBlock>("DialogTitle").Text = title;
            Get<TextBlock>("DialogText").Text = text;
            Get<TextBlock>("DialogMoreLink").Visibility = Visibility.Collapsed;
            Get<ScrollViewer>("DialogMoreScroll").Visibility = Visibility.Collapsed;
            var check = Get<CheckBox>("DialogCheck");
            check.Visibility = checkText == null ? Visibility.Collapsed : Visibility.Visible;
            var ct = new TextBlock(); ct.Text = checkText; ct.TextWrapping = TextWrapping.Wrap; ct.MaxWidth = 370;
            check.Content = ct;
            check.IsChecked = checkDefault;

            var panel = Get<StackPanel>("DialogButtons");
            panel.Children.Clear();
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                KeyValuePair<string, Action<bool>> b = buttons[i];
                var btn = new Button();
                btn.Content = b.Key;
                bool main = i == 0;
                btn.Style = (Style)FindResource(main ? (b.Key.StartsWith("Uninstall") || b.Key.StartsWith("Remove") ? "Danger" : "Ghost") : "Ghost");
                btn.Padding = new Thickness(20, 10, 20, 10);
                btn.Margin = new Thickness(10, 0, 0, 0);
                if (main && btn.Style == FindResource("Ghost")) { btn.Background = AccentBrush; btn.Foreground = Brushes.Black; btn.FontWeight = FontWeights.SemiBold; }
                btn.Click += delegate
                {
                    bool c = check.IsChecked == true;
                    CloseDialog();
                    if (b.Value != null) b.Value(c);
                };
                panel.Children.Add(btn);
            }

            layer.Visibility = Visibility.Visible;
            layer.Opacity = 0;
            Animate(layer, UIElement.OpacityProperty, 1, 180, 0);
            var card = Get<Border>("DialogCard");
            var sc = (ScaleTransform)card.RenderTransform;
            var a = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)); a.EasingFunction = EaseOut();
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        void CloseDialog()
        {
            var layer = Get<Grid>("DialogLayer");
            int gen = dialogGen;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += delegate { if (gen == dialogGen) layer.Visibility = Visibility.Collapsed; };
            layer.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        static KeyValuePair<string, Action<bool>> Btn(string label, Action<bool> action) { return new KeyValuePair<string, Action<bool>>(label, action); }

        string PickFolder(string title, string start)
        {
            try { return Native.PickFolder(new WindowInteropHelper(this).Handle, title, start); }
            catch { return null; }
        }

        // ================================================================== SETUP
        void BuildSetup()
        {
            Get<TextBlock>("CaptionTitle").Text = "Order Up VR Setup";
            bool returning = s != null && s.Uninstalled;
            if (returning) Get<Border>("WelcomeBack").Visibility = Visibility.Visible;

            string rp = returning && Core.IsRpcs3Dir(s.Rpcs3Dir) ? s.Rpcs3Dir : Core.FindRpcs3();
            string gm = returning && Core.IsGameDir(s.GameDir) ? s.GameDir : Core.FindGame(rp);
            string ins = returning && !string.IsNullOrEmpty(s.InstallDir) ? s.InstallDir
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\OrderUpVR");

            var rb = Get<TextBox>("Rpcs3Box"); rb.Text = rp;
            var gb = Get<TextBox>("GameBox"); gb.Text = gm;
            var ib = Get<TextBox>("InstallBox"); ib.Text = ins;
            rb.TextChanged += delegate { ValidateSetup(); };
            gb.TextChanged += delegate { ValidateSetup(); };
            ib.TextChanged += delegate { ValidateSetup(); };
            Get<Button>("Rpcs3Browse").Click += delegate
            {
                string p = PickFolder("Select your RPCS3 folder (the one with rpcs3.exe)", rb.Text);
                if (p != null) { rb.Text = p; if (!Core.IsGameDir(gb.Text)) { string g = Core.FindGame(p); if (g.Length > 0) gb.Text = g; } }
            };
            Get<Button>("GameBrowse").Click += delegate { string p = PickFolder("Select the Order Up!! game folder (the one with PS3_GAME)", gb.Text); if (p != null) gb.Text = p; };
            Get<Button>("InstallBrowse").Click += delegate { string p = PickFolder("Where should Order Up VR be installed?", ib.Text); if (p != null) ib.Text = p; };

            Get<Button>("SetupCancelBtn").Click += delegate { Close(); };
            Get<Button>("GetStartedBtn").Click += delegate { SetupStep(2); };
            Get<Button>("FoldersBackBtn").Click += delegate { SetupStep(1); };
            Get<Button>("InstallBtn").Click += delegate { RunInstall(); };

            ShowPage("SetupPage", true);
            SetupStep(1);
            ValidateSetup();
        }

        int setupStep;

        void SetupStep(int step)
        {
            bool forward = step >= setupStep;
            setupStep = step;
            string[] names = { "StepWelcome", "StepFolders", "StepInstall" };
            for (int i = 0; i < names.Length; i++)
            {
                var p = Get<FrameworkElement>(names[i]);
                if (i == step - 1) { p.Visibility = Visibility.Visible; Reveal(p, forward ? 30 : -30, 0, 0); }
                else p.Visibility = Visibility.Collapsed;
            }
            var dots = Get<StackPanel>("StepDots");
            dots.Children.Clear();
            for (int i = 1; i <= 3; i++)
            {
                var d = new Border();
                d.Height = 6; d.CornerRadius = new CornerRadius(3); d.Margin = new Thickness(0, 0, 6, 0);
                d.Width = i == step ? 28 : 6;
                d.Background = i <= step ? AccentBrush : new SolidColorBrush(Color.FromRgb(0x34, 0x3A, 0x46));
                dots.Children.Add(d);
                if (i == step) { d.Width = 6; Animate(d, FrameworkElement.WidthProperty, 28, 300, 0); }
            }
        }

        static void SetCheck(TextBlock t, bool ok, string text)
        {
            t.Text = (ok ? "✓  " : "✕  ") + text;
            t.Foreground = ok ? GoodBrush : BadBrush;
        }

        bool ValidateSetup()
        {
            string rp = Get<TextBox>("Rpcs3Box").Text.Trim(), gm = Get<TextBox>("GameBox").Text.Trim(), ins = Get<TextBox>("InstallBox").Text.Trim();
            bool okR = Core.IsRpcs3Dir(rp), okG = Core.IsGameDir(gm), okI = false;
            SetCheck(Get<TextBlock>("Rpcs3Check"), okR, okR ? "RPCS3 with installed firmware found" : "Needs rpcs3.exe and installed PS3 firmware (dev_flash)");
            SetCheck(Get<TextBlock>("GameCheck"), okG, okG ? "Order Up!! (Europe, " + Core.TitleId + ") found" : "Choose the folder that contains PS3_GAME (European version " + Core.TitleId + ")");
            string installMsg;
            try
            {
                if (ins.Length < 4 || !System.IO.Path.IsPathRooted(ins)) installMsg = "Choose a folder";
                else if (okR && string.Equals(System.IO.Path.GetFullPath(ins).TrimEnd('\\'), System.IO.Path.GetFullPath(rp).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    installMsg = "Use a separate folder, not your RPCS3 folder";
                else
                {
                    string final = Core.SafeInstallDir(ins);
                    okI = true;
                    installMsg = final.Equals(System.IO.Path.GetFullPath(ins).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                        ? "About 220 MB. Nothing else is installed on your PC."
                        : "That folder has other files, so it installs into " + final;
                }
            }
            catch { installMsg = "That is not a valid folder"; }
            SetCheck(Get<TextBlock>("InstallCheck"), okI, installMsg);
            Get<Button>("InstallBtn").IsEnabled = okR && okG && okI;
            return okR && okG && okI;
        }

        TextBlock AddInstallStep(string text)
        {
            var row = new StackPanel(); row.Orientation = Orientation.Horizontal; row.Margin = new Thickness(0, 0, 0, 12);
            var icon = new TextBlock(); icon.Style = (Style)FindResource("Icon"); icon.Text = ""; icon.Foreground = AccentBrush; icon.Width = 26;
            var spin = new RotateTransform(); icon.RenderTransform = spin; icon.RenderTransformOrigin = new Point(0.35, 0.5);
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)); anim.RepeatBehavior = RepeatBehavior.Forever;
            spin.BeginAnimation(RotateTransform.AngleProperty, anim);
            var label = new TextBlock(); label.Style = (Style)FindResource("Body"); label.Text = text; label.FontSize = 14.5;
            row.Children.Add(icon); row.Children.Add(label);
            Get<StackPanel>("InstallSteps").Children.Add(row);
            Reveal(row, 0, 8, 0);
            return icon;
        }

        static void StepDone(TextBlock icon, bool ok)
        {
            ((RotateTransform)icon.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
            ((RotateTransform)icon.RenderTransform).Angle = 0;
            icon.Text = ok ? "" : "";
            icon.Foreground = ok ? GoodBrush : BadBrush;
        }

        void RunInstall()
        {
            if (!ValidateSetup()) return;
            var result = s != null && s.Uninstalled ? s : new Settings();
            result.Rpcs3Dir = Get<TextBox>("Rpcs3Box").Text.Trim();
            result.GameDir = Get<TextBox>("GameBox").Text.Trim();
            result.InstallDir = Core.SafeInstallDir(Get<TextBox>("InstallBox").Text.Trim());
            result.Uninstalled = false;
            foreach (string a in Environment.GetCommandLineArgs()) if (a.StartsWith("--manifest=")) result.ManifestUrl = a.Substring(11);
            s = result;

            SetupStep(3);
            Get<StackPanel>("InstallSteps").Children.Clear();
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;

            Action<int> next = null;
            TextBlock icon = null;
            next = delegate(int i)
            {
                if (icon != null) StepDone(icon, true);
                string[] labels = { "Copying the launcher", "Creating Start menu and desktop shortcuts", "Adding Order Up VR to Windows' installed apps" };
                if (i >= labels.Length) { FinishInstall(); return; }
                icon = AddInstallStep(labels[i]);
                Task.Factory.StartNew(delegate
                {
                    string error = null;
                    try
                    {
                        Thread.Sleep(350); // let each step be seen
                        if (i == 0)
                        {
                            Directory.CreateDirectory(s.InstallDir);
                            File.WriteAllText(System.IO.Path.Combine(s.InstallDir, Core.MarkerName), "Order Up VR install folder\r\n");
                            if (!string.Equals(System.IO.Path.GetFullPath(self), System.IO.Path.GetFullPath(Core.LauncherPath(s)), StringComparison.OrdinalIgnoreCase))
                                File.Copy(self, Core.LauncherPath(s), true);
                            s.Save();
                        }
                        else if (i == 1)
                        {
                            Core.Shortcut(Core.DesktopLink, Core.LauncherPath(s), s.InstallDir);
                            Core.Shortcut(Core.StartLink, Core.LauncherPath(s), s.InstallDir);
                        }
                        else Core.RegisterApp(s);
                    }
                    catch (Exception ex) { error = ex.Message; }
                    Ui(delegate
                    {
                        if (error != null)
                        {
                            StepDone(icon, false);
                            var err = Get<TextBlock>("InstallError");
                            err.Text = "Setup could not finish: " + error;
                            err.Visibility = Visibility.Visible;
                            return;
                        }
                        next(i + 1);
                    });
                });
            };
            next(0);
        }

        void FinishInstall()
        {
            Get<TextBlock>("InstallTitle").Text = "All set";
            AddInstallStep("Opening Order Up VR. It downloads the mod next.");
            var t = new DispatcherTimer(); t.Interval = TimeSpan.FromMilliseconds(900);
            t.Tick += delegate
            {
                t.Stop();
                Program.ReleaseInstanceLock();
                try { Process.Start(new ProcessStartInfo(Core.LauncherPath(s)) { WorkingDirectory = s.InstallDir, UseShellExecute = false, Arguments = "--handover" }); } catch { }
                Close();
            };
            t.Start();
        }

        // ================================================================== MAIN
        string ManifestUrl { get { return string.IsNullOrEmpty(s.ManifestUrl) ? Core.DefaultManifestUrl : s.ManifestUrl; } }
        bool ModInstalled { get { return !string.IsNullOrEmpty(s.InstalledVersion) && File.Exists(System.IO.Path.Combine(Core.EmuDir(s), "rpcs3.exe")); } }
        bool UpdateAvailable { get { return manifest != null && (!ModInstalled || Core.IsNewer(manifest.version, s.InstalledVersion)); } }

        void BuildMain()
        {
            Get<Button>("PlayBtn").Click += delegate { Play(); };
            Get<Button>("UpdateBtn").Click += delegate { CheckUpdates(true); };
            Get<Button>("SettingsBtn").Click += delegate { OpenSettings(); };
            Get<Button>("LogsBtn").Click += delegate { CollectLogs(); };
            Get<Button>("HelpBtn").Click += delegate { ShowHowTo(); };
            Get<Button>("ReleasesBtn").Click += delegate { OpenUrl(Core.RepoUrl + "/releases"); };
            Get<Button>("BackBtn").Click += delegate { ShowPage("MainPage", false); RefreshAll(); };
            Get<Button>("GithubBtn").Click += delegate { OpenUrl(Core.RepoUrl); };
            Get<Button>("UninstallBtn").Click += delegate { AskUninstall(); };

            var au = Get<CheckBox>("AutoUpdateToggle");
            au.IsChecked = !s.NoAutoUpdate;
            au.Click += delegate { s.NoAutoUpdate = au.IsChecked != true; s.Save(); };
            var mt = Get<CheckBox>("MinimizeToggle");
            mt.IsChecked = !s.KeepLauncherOpen;
            mt.Click += delegate { s.KeepLauncherOpen = mt.IsChecked != true; s.Save(); };

            BuildVrRows();

            watch = new DispatcherTimer(); watch.Interval = TimeSpan.FromSeconds(1); watch.Tick += delegate { WatchGame(); };
            updateTimer = new DispatcherTimer(); updateTimer.Interval = TimeSpan.FromMinutes(30);
            updateTimer.Tick += delegate { if (!s.NoAutoUpdate) CheckUpdates(false); };
            updateTimer.Start();

            ShowPage("MainPage", true);
            RenderChangelog();
            RefreshAll();
            SetStatus(ModInstalled ? "Ready to play" : "Getting the mod ready...", ModInstalled ? 0 : 1, "");
        }

        // VR comfort and body choices: key, label, hint, then value/display pairs (the first is the default)
        static readonly string[][] VrOptions =
        {
            new[] { "chef", "Your chef", "The game's own chef is your body (rebuilt from your copy of the game). Quick Play uses the male chef", "male", "Male chef", "female", "Female chef", "off", "Hands only" },
            new[] { "playmode", "Play position", "Standing: legs stand and step when you walk", "standing", "Standing", "seated", "Seated" },
            new[] { "turn", "Turning", "Right stick left/right inside the game", "snap", "Snap turn", "smooth", "Smooth turn" },
            new[] { "snapangle", "Snap turn angle", "How far one snap turns you", "45", "45 degrees", "30", "30 degrees", "60", "60 degrees", "90", "90 degrees", "15", "15 degrees" },
            new[] { "turnspeed", "Smooth turn speed", "How fast smooth turning rotates you", "120", "Medium (120 degrees/s)", "180", "Fast (180 degrees/s)", "240", "Very fast (240 degrees/s)", "60", "Slow (60 degrees/s)", "90", "Gentle (90 degrees/s)" },
            new[] { "movespeed", "Walking speed", "Left stick walking", "1.4", "Normal", "1.0", "Slow", "2.0", "Fast" },
            new[] { "vignette", "Comfort vignette", "Darkens the edges of your view while the stick moves or turns you", "on", "On", "off", "Off" },
            new[] { "huddist", "Menus and HUD distance", "Where the order tickets, timers and menus float", "1.5", "1.5 m", "1.0", "1.0 m", "2.0", "2.0 m", "3.0", "3.0 m" },
            new[] { "stereo", "3D depth", "Off shows one picture to both eyes: smoother, but flat", "on", "On", "off", "Off" },
            new[] { "pointing", "Pointing in the kitchen", "Hands: the pointer is where your hand covers things", "hand", "Hands", "laser", "Laser" },
            new[] { "kitchen", "Kitchen world", "Room: the kitchen stays still around you and every station is where it really is", "room", "Room (true VR)", "follow", "Follows the game camera" },
            new[] { "lookswitch", "Look to change station", "Turn your head toward the next station and the game moves there (A and B still work)", "on", "On", "off", "Off" },
        };

        string VrGet(string key, string def) { string v; return s.Vr != null && s.Vr.TryGetValue(key, out v) ? v : def; }

        void BuildVrRows()
        {
            var rows = Get<StackPanel>("VrRows");
            rows.Children.Clear();
            foreach (var opt in VrOptions)
            {
                var o = opt;
                var g = new Grid(); g.Margin = new Thickness(0, 6, 0, 6);
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var texts = new StackPanel();
                var label = new TextBlock(); label.Text = o[1]; label.Foreground = Brushes.White; label.FontSize = 14; label.FontWeight = FontWeights.SemiBold;
                var hint = new TextBlock(); hint.Text = o[2]; hint.Foreground = DimBrush; hint.FontSize = 12; hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 2, 12, 0);
                texts.Children.Add(label); texts.Children.Add(hint);
                var b = new Button(); b.Style = (Style)FindResource("Ghost"); b.MinWidth = 190; b.Padding = new Thickness(14, 8, 14, 8); b.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(b, 1);
                Action show = delegate
                {
                    string cur = VrGet(o[0], o[3]);
                    string disp = o[4];
                    for (int i = 3; i + 1 < o.Length; i += 2) if (o[i] == cur) disp = o[i + 1];
                    b.Content = disp;
                };
                b.Click += delegate
                {
                    string cur = VrGet(o[0], o[3]);
                    int at = 3;
                    for (int i = 3; i + 1 < o.Length; i += 2) if (o[i] == cur) at = i;
                    int next = at + 2 < o.Length ? at + 2 : 3;
                    if (s.Vr == null) s.Vr = new Dictionary<string, string>();
                    s.Vr[o[0]] = o[next];
                    s.Save();
                    WriteVrFile();
                    show();
                };
                show();
                g.Children.Add(texts); g.Children.Add(b);
                rows.Children.Add(g);
            }
        }

        void WriteVrFile()
        {
            try
            {
                string dir = System.IO.Path.Combine(Core.EmuDir(s), "config");
                if (!Directory.Exists(dir)) return;
                var sb = new System.Text.StringBuilder();
                foreach (var o in VrOptions) sb.Append(o[0]).Append('=').Append(VrGet(o[0], o[3])).AppendLine();
                File.WriteAllText(System.IO.Path.Combine(dir, "orderupvr_comfort.txt"), sb.ToString());
            }
            catch { }
        }

        const int HowToRevision = 5; // raise when the controls change, so players see the guide again

        void ShowHowTo()
        {
            s.SeenHowTo = true; s.HowToSeen = HowToRevision; s.Save();
            ShowDialog("How to play in VR",
                "1.  Connect your headset (Quest Link, Air Link, Virtual Desktop or SteamVR), then press PLAY.\n" +
                "2.  Menus hang as a menu board in a diner around you. In the kitchen and the restaurant you are inside the game in 3D with your own body and hands: look around and lean in.\n" +
                "    The kitchen stays still around you. Press A or B once to move to the next station; after that, just turn your head toward a station and the game goes there.\n" +
                "3.  Reach for things: inside the game the pointer is wherever your hand covers them. On the big screen, aim the laser.\n\n" +
                "GRIP: grab and hold (the game's T button)\n" +
                "TRIGGER: select and confirm (the Move button)\n" +
                "A / B / X / Y: Cross / Circle / Square / Triangle (the pink square is X)\n" +
                "Left MENU: Start (pause).  Right stick click: Select\n" +
                "Flipping and other motions: hold GRIP and flick the controller, or flick that hand's thumbstick up\n" +
                "Inside the game: left stick walks, right stick turns (left/right) and moves you higher or lower (up/down)\n" +
                "Menu board: hold both GRIPs, then the right stick moves it nearer or farther and the left stick resizes it\n" +
                "Left stick click: recentre.  Hold it for 1 second: switch between 3D and the big screen\n\n" +
                "Start-up screen asking for the pink SQUARE: point at the middle of the screen and press X (or pull the trigger).\n" +
                "In the headset, hold Left MENU for 2 seconds to see the controls again.",
                null, false, Btn("Got it", null));
        }

        static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        void SetStatus(string main, int level, string sub)
        {
            Get<TextBlock>("StatusText").Text = main;
            Get<TextBlock>("SubText").Text = sub ?? "";
            var dot = Get<Ellipse>("StatusDot");
            dot.Fill = level == 0 ? GoodBrush : level == 1 ? AccentBrush : level == 2 ? WarnBrush : BadBrush;
            if (level == 1)
            {
                var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)); pulse.AutoReverse = true; pulse.RepeatBehavior = RepeatBehavior.Forever;
                dot.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            else { dot.BeginAnimation(UIElement.OpacityProperty, null); dot.Opacity = 1; }
        }

        // progress: <0 hides, 0..1 shows that fraction, double.NaN shows an indeterminate sweep
        void SetProgress(double fraction)
        {
            var track = Get<Grid>("ProgressTrack");
            var fill = Get<Border>("ProgressFill");
            if (fraction < 0)
            {
                track.Visibility = Visibility.Collapsed;
                Shift(fill).BeginAnimation(TranslateTransform.XProperty, null);
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                return;
            }
            track.Visibility = Visibility.Visible;
            double width = Math.Max(1, track.ActualWidth > 0 ? track.ActualWidth : 600);
            if (double.IsNaN(fraction))
            {
                fill.BeginAnimation(FrameworkElement.WidthProperty, null);
                fill.Width = width * 0.3;
                var sweep = new DoubleAnimation(-width * 0.3, width, TimeSpan.FromSeconds(1.3)); sweep.RepeatBehavior = RepeatBehavior.Forever;
                var ease = new SineEase(); ease.EasingMode = EasingMode.EaseInOut; sweep.EasingFunction = ease;
                Shift(fill).BeginAnimation(TranslateTransform.XProperty, sweep);
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
                return;
            }
            Shift(fill).BeginAnimation(TranslateTransform.XProperty, null);
            Shift(fill).X = 0;
            var grow = new DoubleAnimation(width * Math.Max(0, Math.Min(1, fraction)), TimeSpan.FromMilliseconds(250));
            fill.BeginAnimation(FrameworkElement.WidthProperty, grow);
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
            TaskbarItemInfo.ProgressValue = fraction;
        }

        void RefreshAll()
        {
            if (Get<StackPanel>("Chips") == null) return;
            RenderChips();
            RenderStatus();
            RenderPlay();
        }

        Border Chip(string text, Brush fg, Brush bg)
        {
            var b = new Border();
            b.CornerRadius = new CornerRadius(10); b.Padding = new Thickness(11, 5, 11, 5); b.Margin = new Thickness(0, 0, 8, 0);
            b.Background = bg; b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); b.BorderThickness = new Thickness(1);
            var t = new TextBlock(); t.Text = text; t.Foreground = fg; t.FontSize = 12; t.FontWeight = FontWeights.SemiBold;
            b.Child = t;
            return b;
        }

        void RenderChips()
        {
            var chips = Get<StackPanel>("Chips");
            chips.Children.Clear();
            var glass = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            chips.Children.Add(Chip("Mod " + (ModInstalled ? s.InstalledVersion : "not installed"), Brushes.White, glass));
            chips.Children.Add(Chip("Launcher " + Core.LauncherVersion, Brushes.White, glass));
            if (manifest != null)
            {
                if (UpdateAvailable) chips.Children.Add(Chip("Update " + manifest.version + " available", Brushes.Black, AccentBrush));
                else chips.Children.Add(Chip("✓ Up to date", GoodBrush, new SolidColorBrush(Color.FromArgb(0x26, 0x3D, 0xDC, 0x97))));
            }
        }

        void StatusRow(string label, string value, Brush dot, bool pulse)
        {
            var g = new Grid(); g.Margin = new Thickness(0, 7, 0, 7);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var e = new Ellipse(); e.Width = 8; e.Height = 8; e.Fill = dot; e.VerticalAlignment = VerticalAlignment.Top; e.Margin = new Thickness(0, 6, 12, 0);
            if (pulse)
            {
                var a = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(800)); a.AutoReverse = true; a.RepeatBehavior = RepeatBehavior.Forever;
                e.BeginAnimation(UIElement.OpacityProperty, a);
            }
            var sp = new StackPanel(); Grid.SetColumn(sp, 1);
            var l = new TextBlock(); l.Text = label; l.FontSize = 11.5; l.Foreground = DimBrush;
            var v = new TextBlock(); v.Text = value; v.FontSize = 13.5; v.Foreground = TextBrush; v.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(l); sp.Children.Add(v);
            g.Children.Add(e); g.Children.Add(sp);
            Get<StackPanel>("StatusPanel").Children.Add(g);
        }

        void RenderStatus()
        {
            Get<StackPanel>("StatusPanel").Children.Clear();
            string runtime = Core.RuntimeName(Core.ActiveOpenXrRuntime());
            if (game != null)
            {
                StatusRow("Game", liveGame ?? "Starting...", AccentBrush, true);
                StatusRow("Headset", liveVr ?? "Waiting for the game", liveVrLevel == 0 ? GoodBrush : liveVrLevel == 1 ? AccentBrush : WarnBrush, liveVrLevel == 1);
            }
            else
            {
                StatusRow("VR software", runtime ?? "None active. Start Quest Link, Air Link, Virtual Desktop or SteamVR.", runtime != null ? GoodBrush : WarnBrush, false);
                bool r = Core.IsRpcs3Dir(s.Rpcs3Dir), g = Core.IsGameDir(s.GameDir);
                StatusRow("RPCS3 and firmware", r ? "Found" : "Folder not found. Open Settings to fix it.", r ? GoodBrush : BadBrush, false);
                StatusRow("Game", g ? "Order Up!! (" + Core.TitleId + ")" : "Folder not found. Open Settings to fix it.", g ? GoodBrush : BadBrush, false);
            }
            string mod = !ModInstalled ? "Not installed yet" : UpdateAvailable ? s.InstalledVersion + ", update " + manifest.version + " ready" : s.InstalledVersion + (manifest != null ? ", up to date" : "");
            StatusRow("Mod", mod, !ModInstalled ? AccentBrush : UpdateAvailable ? WarnBrush : GoodBrush, false);
        }

        void RenderPlay()
        {
            var btn = Get<Button>("PlayBtn");
            string text = "PLAY", icon = "";
            if (game != null) { text = "RUNNING"; icon = ""; }
            else if (busy && !ModInstalled) { text = "INSTALLING"; icon = ""; }
            else if (busy) { text = "UPDATING"; icon = ""; }
            Get<TextBlock>("PlayText").Text = text;
            Get<TextBlock>("PlayIcon").Text = icon;
            btn.IsEnabled = ModInstalled && !busy && game == null;
            Get<Button>("UpdateBtn").IsEnabled = !busy;
        }

        void RenderChangelog()
        {
            var panel = Get<StackPanel>("ChangelogPanel");
            panel.Children.Clear();
            if (manifest == null || manifest.changelog == null || manifest.changelog.Count == 0)
            {
                var t = new TextBlock(); t.Style = (Style)FindResource("DimText");
                t.Text = manifest == null ? "Checking for the latest release notes..." : "No release notes.";
                panel.Children.Add(t);
                return;
            }
            int n = 0;
            foreach (ChangelogEntry e in manifest.changelog)
            {
                var block = new StackPanel(); block.Margin = new Thickness(0, 0, 0, 22);
                var head = new StackPanel(); head.Orientation = Orientation.Horizontal; head.Margin = new Thickness(0, 0, 0, 8);
                var pill = new Border(); pill.CornerRadius = new CornerRadius(8); pill.Padding = new Thickness(9, 3, 9, 3);
                pill.Background = n == 0 ? AccentBrush : new SolidColorBrush(Color.FromRgb(0x27, 0x2C, 0x37));
                var pt = new TextBlock(); pt.Text = "v" + e.version; pt.FontWeight = FontWeights.Bold; pt.FontSize = 12.5; pt.Foreground = n == 0 ? Brushes.Black : TextBrush;
                pill.Child = pt;
                head.Children.Add(pill);
                var date = new TextBlock(); date.Text = (n == 0 ? "Latest  ·  " : "") + (e.date ?? ""); date.Foreground = DimBrush; date.FontSize = 12.5;
                date.Margin = new Thickness(10, 0, 0, 0); date.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(date);
                if (ModInstalled && e.version == s.InstalledVersion)
                {
                    var inst = Chip("Installed", GoodBrush, new SolidColorBrush(Color.FromArgb(0x22, 0x3D, 0xDC, 0x97)));
                    inst.Margin = new Thickness(10, 0, 0, 0); inst.Padding = new Thickness(8, 2, 8, 2);
                    head.Children.Add(inst);
                }
                block.Children.Add(head);
                if (e.notes != null)
                {
                    foreach (string note in e.notes)
                    {
                        var row = new Grid(); row.Margin = new Thickness(2, 3, 0, 3);
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                        row.ColumnDefinitions.Add(new ColumnDefinition());
                        var dot = new Ellipse(); dot.Width = 5; dot.Height = 5; dot.Fill = n == 0 ? AccentBrush : DimBrush; dot.VerticalAlignment = VerticalAlignment.Top; dot.Margin = new Thickness(0, 8, 0, 0);
                        var txt = new TextBlock(); txt.Style = (Style)FindResource("Body"); txt.Text = note; txt.Foreground = n == 0 ? TextBrush : DimBrush;
                        Grid.SetColumn(txt, 1);
                        row.Children.Add(dot); row.Children.Add(txt);
                        block.Children.Add(row);
                    }
                }
                panel.Children.Add(block);
                Reveal(block, 0, 10, 60 * n);
                n++;
            }
        }

        // ------------------------------------------------------------------ updates
        void CheckUpdates(bool userAsked)
        {
            if (busy || game != null) { if (userAsked && game != null) ShowToast("Close the game first to update.", false); return; }
            busy = true; RefreshAll();
            SetStatus("Checking for updates...", 1, "");
            SetProgress(double.NaN);
            string url = ManifestUrl;
            Task.Factory.StartNew(delegate
            {
                try
                {
                    Manifest m = Core.FetchManifest(url);
                    Ui(delegate { OnManifest(m, userAsked); });
                }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        busy = false; SetProgress(-1); RefreshAll();
                        SetStatus("Could not check for updates", 2, (ModInstalled ? "You can still play the installed version. " : "Check your internet connection and try again. ") + "(" + ex.Message + ")");
                    });
                }
            });
        }

        void OnManifest(Manifest m, bool userAsked)
        {
            manifest = m; busy = false; SetProgress(-1);
            RenderChangelog(); RefreshAll();
            bool launcherNewer = m.launcher != null && Core.IsNewer(m.launcher.version, Core.LauncherVersion);
            if (!ModInstalled) { if (launcherNewer) UpdateLauncher(m.launcher); else DownloadMod(m); return; }   // first install: no question
            if (launcherNewer || UpdateAvailable)
            {
                SetStatus("Update " + m.version + " available", 2, "Press Update now when you're ready.");
                if (userAsked || !updatePromptShown) { updatePromptShown = true; ShowUpdatePrompt(m, launcherNewer); }
            }
            else
            {
                SetStatus("Ready to play", 0, "You have the latest version (" + s.InstalledVersion + ").");
                if (userAsked) ShowToast("You have the latest version.", true);
            }
        }

        bool updatePromptShown;

        // Asks before updating, with the changes in plain words behind "What's new"
        void ShowUpdatePrompt(Manifest m, bool launcherNewer)
        {
            ShowDialog("Update available",
                "Order Up VR " + m.version + " is ready (you have " + (string.IsNullOrEmpty(s.InstalledVersion) ? "an older version" : s.InstalledVersion) + "). Updating takes about a minute.",
                null, false,
                Btn("Update now", delegate { if (launcherNewer) UpdateLauncher(m.launcher); else DownloadMod(m); }),
                Btn("Later", null));
            var more = Get<StackPanel>("DialogMore");
            more.Children.Clear();
            if (m.changelog != null)
                foreach (ChangelogEntry e in m.changelog)
                {
                    if (!string.IsNullOrEmpty(s.InstalledVersion) && !Core.IsNewer(e.version, s.InstalledVersion)) continue;
                    var head = new TextBlock(); head.Text = "Version " + e.version; head.FontWeight = FontWeights.SemiBold; head.Foreground = Brushes.White; head.Margin = new Thickness(0, 6, 0, 4);
                    more.Children.Add(head);
                    List<string> lines = e.simple != null && e.simple.Count > 0 ? e.simple : e.notes;
                    if (lines == null) continue;
                    foreach (string l in lines)
                    {
                        var t = new TextBlock(); t.Text = "•  " + l; t.TextWrapping = TextWrapping.Wrap; t.Foreground = DimBrush; t.FontSize = 13; t.Margin = new Thickness(0, 0, 0, 5);
                        more.Children.Add(t);
                    }
                }
            var link = Get<TextBlock>("DialogMoreLink");
            var scroll = Get<ScrollViewer>("DialogMoreScroll");
            link.Text = "What's new  ▾";
            link.Visibility = more.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            link.MouseLeftButtonUp -= ToggleWhatsNew;
            link.MouseLeftButtonUp += ToggleWhatsNew;
        }

        void ToggleWhatsNew(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var scroll = Get<ScrollViewer>("DialogMoreScroll");
            bool open = scroll.Visibility != Visibility.Visible;
            scroll.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            Get<TextBlock>("DialogMoreLink").Text = open ? "What's new  ▴" : "What's new  ▾";
        }

        void DownloadMod(Manifest m)
        {
            if (Core.EmuRunning(s)) { SetStatus("Version " + m.version + " is ready to install", 2, "Close the game to install it."); return; }
            busy = true; RefreshAll();
            string zip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrderUpVR_" + m.version + ".zip");
            var wc = Core.NewClient();
            SetStatus((ModInstalled ? "Downloading update " : "Downloading Order Up VR ") + m.version, 1, "");
            SetProgress(0);
            wc.DownloadProgressChanged += delegate(object o, DownloadProgressChangedEventArgs e)
            {
                long total = Math.Max(e.TotalBytesToReceive, m.size);
                Ui(delegate
                {
                    SetProgress(total > 0 ? (double)e.BytesReceived / total : 0);
                    Get<TextBlock>("SubText").Text = (e.BytesReceived / 1048576) + " of " + (total / 1048576) + " MB";
                });
            };
            wc.DownloadFileCompleted += delegate(object o, System.ComponentModel.AsyncCompletedEventArgs e)
            {
                wc.Dispose();
                Ui(delegate
                {
                    if (e.Error != null)
                    {
                        busy = false; SetProgress(-1); RefreshAll();
                        SetStatus("Download failed", 3, e.Error.Message + "  Press Check for updates to try again.");
                        try { File.Delete(zip); } catch { }
                        return;
                    }
                    SetStatus("Installing " + m.version + "...", 1, "Almost done.");
                    SetProgress(double.NaN);
                    Task.Factory.StartNew(delegate
                    {
                        string error = null;
                        try
                        {
                            if (string.IsNullOrEmpty(m.sha256) || !string.Equals(Core.Sha256(zip), m.sha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("the download was damaged (checksum mismatch). Please try again.");
                            Core.ApplyPackage(s, zip, m.version);
                        }
                        catch (Exception ex) { error = ex.Message; }
                        try { File.Delete(zip); } catch { }
                        Ui(delegate
                        {
                            busy = false; SetProgress(-1);
                            RenderChangelog(); RefreshAll();
                            if (error == null)
                            {
                                SetStatus("Ready to play", 0, "Version " + m.version + " installed.");
                                ShowToast("Updated to " + m.version, true);
                                if (!Core.VcRedistOk(VcMinimum)) EnsureVcRuntime(null);
                                if (WindowState == WindowState.Minimized) TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                            }
                            else SetStatus("Install failed", 3, error);
                        });
                    });
                });
            };
            try { wc.DownloadFileAsync(Core.ToUri(Core.ResolveUrl(ManifestUrl, m.url)), zip); }
            catch (Exception ex) { busy = false; SetProgress(-1); RefreshAll(); SetStatus("Download failed", 3, ex.Message); }
        }

        void UpdateLauncher(LauncherInfo info)
        {
            busy = true; RefreshAll();
            SetStatus("Updating the launcher to " + info.version + "...", 1, "It restarts by itself in a moment.");
            SetProgress(double.NaN);
            string self = Assembly.GetExecutingAssembly().Location;
            string fresh = self + ".new";
            string url = Core.ResolveUrl(ManifestUrl, info.url);
            Task.Factory.StartNew(delegate
            {
                string error = null;
                try
                {
                    using (var wc = Core.NewClient()) wc.DownloadFile(Core.ToUri(url), fresh);
                    if (string.IsNullOrEmpty(info.sha256) || !string.Equals(Core.Sha256(fresh), info.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("checksum mismatch");
                    // Swap the exe after this process exits, then start the new one.
                    string cmd = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrderUpVR_swap.cmd");
                    File.WriteAllText(cmd, "@echo off\r\nping -n 3 127.0.0.1 >nul\r\nmove /y \"" + fresh + "\" \"" + self + "\" >nul\r\nstart \"\" \"" + self + "\" --handover\r\ndel \"%~f0\"\r\n");
                    var psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\""); psi.CreateNoWindow = true; psi.UseShellExecute = false;
                    psi.WorkingDirectory = System.IO.Path.GetTempPath();
                    Process.Start(psi);
                }
                catch (Exception ex) { error = ex.Message; try { File.Delete(fresh); } catch { } }
                Ui(delegate
                {
                    if (error == null) { Program.ReleaseInstanceLock(); Application.Current.Shutdown(); return; }
                    busy = false; SetProgress(-1); RefreshAll();
                    SetStatus("Launcher update failed", 2, error + ". Continuing with this version.");
                    if (UpdateAvailable) DownloadMod(manifest);
                });
            });
        }

        // ------------------------------------------------------------------ play + watcher
        void Play()
        {
            if (Core.EmuRunning(s)) { ShowToast("The game is already running.", false); return; }
            if (!Core.IsGameDir(s.GameDir) || !Core.IsRpcs3Dir(s.Rpcs3Dir))
            {
                ShowDialog("A folder has moved", "Order Up VR cannot find your RPCS3 or game folder any more. Open Settings to choose them again.", null, false,
                    Btn("Open settings", delegate { OpenSettings(); }), Btn("Cancel", null));
                return;
            }
            if (Core.ActiveOpenXrRuntime() == null)
            {
                ShowDialog("No VR software is running",
                    "Start Quest Link, Air Link, Virtual Desktop or SteamVR first and make it the active OpenXR runtime. Without it the game only shows on the monitor.",
                    null, false, Btn("Play on monitor", delegate { EnsureVcRuntime(StartGame); }), Btn("Cancel", null));
                return;
            }
            EnsureVcRuntime(StartGame);
        }

        // ------------------------------------------------------------------ Microsoft Visual C++ runtime
        // RPCS3 needs Microsoft's VC++ 2015-2022 runtime installed in Windows (it refuses private copies).
        string VcMinimum { get { return manifest != null && !string.IsNullOrEmpty(manifest.vcredistMin) ? manifest.vcredistMin : "14.44.0.0"; } }

        static string ShortVersion(string v) { Version x; return Version.TryParse(v ?? "", out x) ? x.Major + "." + x.Minor : v; }

        void EnsureVcRuntime(Action then)
        {
            if (Core.VcRedistOk(VcMinimum)) { if (then != null) then(); return; }
            Version have = Core.VcRedistVersion();
            ShowDialog(have == null ? "One component from Microsoft is needed" : "A Microsoft component needs an update",
                "RPCS3 needs the Microsoft Visual C++ Runtime" + (have == null ? "" : " (this PC has " + have.Major + "." + have.Minor + ", it needs " + ShortVersion(VcMinimum) + " or newer)") +
                ". It is free, about 25 MB, and downloaded directly from Microsoft. Windows will ask for permission to install it.",
                null, false, Btn("Install it", delegate { InstallVcRuntime(then); }), Btn("Not now", null));
        }

        void InstallVcRuntime(Action then)
        {
            busy = true; RefreshAll();
            SetStatus("Downloading the Microsoft Visual C++ Runtime...", 1, "From microsoft.com");
            SetProgress(double.NaN);
            string url = manifest != null && !string.IsNullOrEmpty(manifest.vcredistUrl) ? manifest.vcredistUrl : Core.DefaultVcRedistUrl;
            string exe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrderUpVR_vc_redist.x64.exe");
            Task.Factory.StartNew(delegate
            {
                string error = null;
                int code = 0;
                try
                {
                    using (var wc = Core.NewClient()) wc.DownloadFile(new Uri(url), exe);
                    if (!Core.SignedByMicrosoft(exe)) throw new InvalidDataException("the download is not signed by Microsoft, so it was not run");
                    Ui(delegate { SetStatus("Installing the Microsoft Visual C++ Runtime...", 1, "Allow it in the Windows permission prompt."); });
                    var psi = new ProcessStartInfo(exe, "/install /passive /norestart");
                    psi.UseShellExecute = true; // lets Windows show its permission prompt
                    using (Process p = Process.Start(psi)) { p.WaitForExit(); code = p.ExitCode; }
                }
                catch (System.ComponentModel.Win32Exception) { error = "the Windows permission prompt was declined"; }
                catch (Exception ex) { error = ex.Message; }
                try { File.Delete(exe); } catch { }
                Ui(delegate
                {
                    busy = false; SetProgress(-1); RefreshAll();
                    // 0 = installed, 3010 = installed (restart suggested), 1638 = a newer version is already there
                    if (error == null && Core.VcRedistOk(VcMinimum))
                    {
                        SetStatus("Ready to play", 0, "Microsoft Visual C++ Runtime installed.");
                        ShowToast("Microsoft Visual C++ Runtime installed." + (code == 3010 ? " Windows suggests a restart, but you can play now." : ""), true);
                        if (then != null) then();
                    }
                    else
                    {
                        string why = error ?? ("the installer ended with code " + code);
                        SetStatus("The Microsoft component was not installed", 2, why);
                        ShowDialog("Could not install the Microsoft component", "Reason: " + why + ".\n\nYou can also install it yourself from Microsoft (search for \"Visual C++ Redistributable latest\"), then press Play again.",
                            null, false, Btn("Open Microsoft's download", delegate { OpenUrl(url); }), Btn("Close", null));
                    }
                });
            });
        }

        void StartGame()
        {
            string emu = Core.EmuDir(s);
            var psi = new ProcessStartInfo(System.IO.Path.Combine(emu, "rpcs3.exe"), "--no-gui \"" + System.IO.Path.Combine(s.GameDir, @"PS3_GAME\USRDIR\EBOOT.BIN") + "\"");
            psi.WorkingDirectory = emu; psi.UseShellExecute = false;
            psi.EnvironmentVariables["ORDERUP_VR_MODVERSION"] = s.InstalledVersion ?? "";
            WriteVrFile();
            try
            {
                try { File.Delete(System.IO.Path.Combine(emu, @"log\orderup_vr.log")); } catch { }
                game = Process.Start(psi);
                gameStart = DateTime.Now;
                sawGameWindow = false; broughtToFront = false; minimizedForGame = false; lastNote = "";
                liveGame = null; liveVr = null; liveVrLevel = 1;
                watchLog.Length = 0;
                Note("Started " + psi.FileName + " " + psi.Arguments);
                SetStatus("Starting the game...", 1, "The first start after an update prepares the game for this PC and can take a few minutes.");
                RefreshAll();
                watch.Start();
            }
            catch (Exception ex) { SetStatus("Could not start the game", 3, ex.Message); }
        }

        void Note(string text)
        {
            if (text == lastNote) return;
            lastNote = text;
            watchLog.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
        }

        static string ReadTail(string path, int maxBytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - maxBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int n = fs.Read(buf, 0, buf.Length);
                    return Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch { return ""; }
        }

        // Headset state from the VR module's log. level: 0 good, 1 in progress, 2 needs attention
        string VrSummary(out int level)
        {
            level = 1;
            string text = ReadTail(System.IO.Path.Combine(Core.EmuDir(s), @"log\orderup_vr.log"), 256 * 1024);
            if (text.Length == 0) return "Waiting for the game to start VR";
            if (text.Contains("No usable OpenXR runtime")) { level = 2; return "No VR software found. Playing on the monitor only."; }
            if (text.Contains("Disabled by ORDERUP_VR=0")) { level = 2; return "VR is switched off"; }
            string headset = null, state = null, lastError = null;
            bool live = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int i;
                if ((i = line.IndexOf("Headset: ")) >= 0) headset = line.Substring(i + 9);
                if ((i = line.IndexOf("Session state -> ")) >= 0) state = line.Substring(i + 17);
                if (line.Contains("Panel hidden: game picture is live")) live = true;
                if (line.Contains("Panel: ")) live = false;
                if ((i = line.IndexOf("ERROR: ")) >= 0) lastError = line.Substring(i + 7);
            }
            if (lastError != null && state == null) { level = 2; return "VR error: " + lastError; }
            if (headset == null) return "Waiting for the headset. Is Quest Link or Air Link connected?";
            if (state == null) return headset + " found, starting VR";
            switch (state)
            {
                case "1": case "2": case "3": return headset + ": put the headset on";
                case "4": level = 2; return headset + ": close the headset menu to continue";
                case "5": level = live ? 0 : 1; return headset + (live ? ": showing the game" : ": showing the loading panel");
                case "6": case "7": case "8": level = 2; return headset + ": the VR session ended";
            }
            return headset + " (state " + state + ")";
        }

        bool preparingShown;

        // Reads the VR module's "Progress: N% text (a of b)" lines. True while preparation is still running.
        bool Preparing(out int percent, out string what)
        {
            percent = 0; what = "";
            string text = ReadTail(System.IO.Path.Combine(Core.EmuDir(s), @"log\orderup_vr.log"), 64 * 1024);
            int i = text.LastIndexOf("Progress: ");
            if (i < 0) return false;
            if (text.LastIndexOf("Panel hidden: game picture is live") > i) return false;
            int nl = text.IndexOf('\n', i);
            string line = (nl < 0 ? text.Substring(i + 10) : text.Substring(i + 10, nl - i - 10)).Trim();
            int pct = line.IndexOf('%');
            if (pct < 0 || !int.TryParse(line.Substring(0, pct), out percent)) return false;
            what = line.Substring(pct + 1).Trim();
            if (what.Length == 0) what = "Compiling";
            return percent < 100;
        }

        void WatchGame()
        {
            if (game == null) { watch.Stop(); return; }
            int secs = (int)(DateTime.Now - gameStart).TotalSeconds;

            bool exited;
            try { exited = game.HasExited; } catch { exited = true; }
            if (exited)
            {
                watch.Stop();
                int code = 0;
                try { code = game.ExitCode; } catch { }
                Note("Game process ended after " + secs + " s, exit code " + code);
                game = null;
                if (minimizedForGame) BringToFront();
                if (preparingShown) { preparingShown = false; SetProgress(-1); }
                RefreshAll();
                if (!sawGameWindow)
                {
                    SetStatus("The game closed before its window appeared", 3, "Press Collect logs for support and send the file from your Desktop.");
                    ShowDialog("The game closed early", "The game closed before its window appeared. Collect logs and send the file so the problem can be found.", null, false,
                        Btn("Collect logs", delegate { CollectLogs(); }), Btn("Close", null));
                }
                else if (secs < 60) SetStatus("The game closed after " + secs + " seconds", 2, "If you did not close it, press Collect logs for support.");
                else SetStatus("Ready to play", 0, "The game has closed.");
                if (UpdateAvailable && !busy) DownloadMod(manifest);
                return;
            }

            string gameTitle = null, errorTitle = null;
            IntPtr gameWnd = IntPtr.Zero, errorWnd = IntPtr.Zero;
            bool minimized = false;
            foreach (var w in Native.WindowsOf(game.Id))
            {
                if (w.Value.StartsWith("FPS:")) { gameTitle = w.Value; gameWnd = w.Key; minimized = Native.IsIconic(w.Key); }
                else if (w.Value.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || w.Value.IndexOf("fatal", StringComparison.OrdinalIgnoreCase) >= 0)
                { errorTitle = w.Value; errorWnd = w.Key; }
                else if (w.Value.Length > 0) Note("Window: " + w.Value);
            }

            if (errorTitle != null)
            {
                Note("Error window: " + errorTitle);
                liveGame = "RPCS3 reported a problem";
                SetStatus("RPCS3 reported a problem", 3, "\"" + errorTitle + "\". Read the message on the PC, close it, then press Collect logs.");
                if (!broughtToFront) { Native.ShowWindow(errorWnd, 9); Native.SetForegroundWindow(errorWnd); broughtToFront = true; }
                RenderStatus();
                return;
            }

            int level;
            liveVr = VrSummary(out level);
            liveVrLevel = level;
            Note("VR: " + liveVr);

            // First start after an update: RPCS3 prepares the game for this PC. Show its real progress.
            int prepPercent; string prepWhat;
            if (Preparing(out prepPercent, out prepWhat))
            {
                if (!sawGameWindow && gameTitle != null) { sawGameWindow = true; Note("Game window: " + gameTitle); }
                liveGame = "Preparing for this PC: " + prepPercent + "%";
                Note("Preparing " + prepPercent + "% " + prepWhat);
                bool firstPrep = s.PreparedVersion != s.InstalledVersion;
                SetStatus((firstPrep ? "Preparing the game for this PC: " : "Loading: ") + prepPercent + "%", 1,
                    prepWhat + (firstPrep ? ". This one-time step runs after installing or updating the mod." : ""));
                SetProgress(prepPercent / 100.0);
                preparingShown = true;
                RenderStatus();
                return;
            }
            if (preparingShown) { preparingShown = false; SetProgress(-1); }

            // The game picture is up: this version is prepared on this PC.
            if (gameTitle != null && secs > 5 && s.PreparedVersion != s.InstalledVersion) { s.PreparedVersion = s.InstalledVersion; s.Save(); }

            if (gameTitle != null)
            {
                if (!sawGameWindow)
                {
                    sawGameWindow = true;
                    Note("Game window: " + gameTitle);
                    if (!s.KeepLauncherOpen) { minimizedForGame = true; WindowState = WindowState.Minimized; }
                }
                if (!broughtToFront) { Native.ShowWindow(gameWnd, 9); Native.SetForegroundWindow(gameWnd); broughtToFront = true; }
                string fps = gameTitle.Split('|')[0].Replace("FPS:", "").Trim();
                liveGame = minimized ? "Window minimized. Restore it so the headset gets a picture." : "Running at " + fps + " FPS";
                SetStatus(minimized ? "Restore the game window" : "Playing", minimized ? 2 : 0, liveVr);
            }
            else
            {
                liveGame = "Starting... " + secs + " s";
                if (secs < 120) SetStatus("Starting the game... " + secs + " s", 1, liveVr);
                else SetStatus("Still no game window after " + secs + " s", 2, "Look for an RPCS3 window in the taskbar. If there is none, close the game and collect logs.");
            }
            RenderStatus();
        }

        // ------------------------------------------------------------------ logs
        static void CopyTail(string src, string dst, long maxBytes)
        {
            using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var outf = File.Create(dst))
            {
                if (fs.Length > maxBytes) fs.Seek(fs.Length - maxBytes, SeekOrigin.Begin);
                fs.CopyTo(outf);
            }
        }

        void CollectLogs()
        {
            try
            {
                string emu = Core.EmuDir(s);
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrderUpVR_logs_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                foreach (string rel in new[] { @"log\orderup_vr.log", @"log\RPCS3.log", @"log\RPCS3.log.gz", @"log\TTY.log", @"config\custom_configs\config_" + Core.TitleId + ".yml", @"config\config.yml", @"config\vfs.yml", @"log\orderup_research.log", @"log\orderup_camdump.bin.z", @"config\orderupvr_state.txt" })
                {
                    string p = System.IO.Path.Combine(emu, rel);
                    try { if (File.Exists(p)) CopyTail(p, System.IO.Path.Combine(tmp, System.IO.Path.GetFileName(p)), 20L * 1048576); } catch { }
                }
                // Vertex shader programs (for finding the game camera for true VR), capped at 15 MB
                try
                {
                    string shaders = System.IO.Path.Combine(emu, @"cache\shaderlog");
                    if (Directory.Exists(shaders))
                    {
                        string dst = System.IO.Path.Combine(tmp, "shaderlog"); Directory.CreateDirectory(dst);
                        long budget = 15L * 1048576;
                        foreach (string f in Directory.GetFiles(shaders, "*", SearchOption.AllDirectories))
                        {
                            string n = System.IO.Path.GetFileName(f).ToLowerInvariant();
                            if (!(n.Contains("vp") || n.Contains("vertex"))) continue;
                            long len = new FileInfo(f).Length; if (len > budget) break; budget -= len;
                            File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
                        }
                    }
                }
                catch { }
                File.WriteAllText(System.IO.Path.Combine(tmp, "launcher_watch.txt"), watchLog.Length > 0 ? watchLog.ToString() : "(the game was not started from this launcher window)\r\n");
                File.WriteAllText(System.IO.Path.Combine(tmp, "system.txt"), "Mod version: " + s.InstalledVersion + "\r\nLauncher: " + Core.LauncherVersion +
                    "\r\nOpenXR runtime: " + (Core.ActiveOpenXrRuntime() ?? "none") + "\r\nWindows: " + Environment.OSVersion.VersionString + "\r\n");
                string zip = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OrderUpVR_logs_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
                ZipFile.CreateFromDirectory(tmp, zip);
                Directory.Delete(tmp, true);
                ShowToast("Logs saved to your Desktop as " + System.IO.Path.GetFileName(zip) + ". No passwords or personal files are included.", true);
                try { Process.Start("explorer.exe", "/select,\"" + zip + "\""); } catch { }
            }
            catch (Exception ex) { ShowToast("Could not collect logs: " + ex.Message, false); }
        }

        // ------------------------------------------------------------------ settings page
        void OpenSettings()
        {
            RenderFolderRows();
            Get<TextBlock>("AboutText").Text = "Launcher " + Core.LauncherVersion + "   ·   Mod " + (ModInstalled ? s.InstalledVersion : "not installed") +
                "\nInstalled in " + s.InstallDir;
            ShowPage("SettingsPage", true);
        }

        void RenderFolderRows()
        {
            var rows = Get<StackPanel>("FolderRows");
            rows.Children.Clear();
            FolderRow(rows, "RPCS3 folder", s.Rpcs3Dir, Core.IsRpcs3Dir(s.Rpcs3Dir), "Needs rpcs3.exe and installed firmware", delegate
            {
                string p = PickFolder("Select your RPCS3 folder", s.Rpcs3Dir);
                if (p == null) return;
                if (!Core.IsRpcs3Dir(p)) { ShowToast("That folder has no rpcs3.exe with installed firmware.", false); return; }
                s.Rpcs3Dir = p; s.Save(); ShowToast("RPCS3 folder saved.", true); RenderFolderRows();
            });
            FolderRow(rows, "Order Up!! game folder", s.GameDir, Core.IsGameDir(s.GameDir), "Not the European " + Core.TitleId + " game folder", delegate
            {
                string p = PickFolder("Select the Order Up!! game folder", s.GameDir);
                if (p == null) return;
                if (!Core.IsGameDir(p)) { ShowToast("That is not the European Order Up!! (" + Core.TitleId + ") folder.", false); return; }
                s.GameDir = p; s.Save(); ShowToast("Game folder saved.", true); RenderFolderRows();
            });
            FolderRow(rows, "Order Up VR install folder", s.InstallDir, true, "", null);
        }

        void FolderRow(StackPanel parent, string label, string path, bool ok, string problem, Action change)
        {
            var g = new Grid(); g.Margin = new Thickness(0, 12, 0, 0);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            var l = new TextBlock(); l.Text = label; l.FontSize = 12; l.Foreground = DimBrush;
            var p = new TextBlock(); p.Text = string.IsNullOrEmpty(path) ? "(not set)" : path; p.FontSize = 13.5; p.Foreground = TextBrush; p.TextTrimming = TextTrimming.CharacterEllipsis; p.Margin = new Thickness(0, 2, 0, 0);
            var c = new TextBlock(); c.FontSize = 12; c.Margin = new Thickness(0, 2, 0, 0);
            SetCheck(c, ok, ok ? "OK" : problem);
            sp.Children.Add(l); sp.Children.Add(p); sp.Children.Add(c);
            var buttons = new StackPanel(); buttons.Orientation = Orientation.Horizontal; buttons.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(buttons, 1);
            var open = new Button(); open.Style = (Style)FindResource("Ghost"); open.Content = "Open"; open.Margin = new Thickness(10, 0, 0, 0);
            open.Click += delegate { if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\""); };
            buttons.Children.Add(open);
            if (change != null)
            {
                var ch = new Button(); ch.Style = (Style)FindResource("Ghost"); ch.Content = "Change"; ch.Margin = new Thickness(8, 0, 0, 0);
                ch.Click += delegate { change(); };
                buttons.Children.Add(ch);
            }
            g.Children.Add(sp); g.Children.Add(buttons);
            parent.Children.Add(g);
        }

        // ------------------------------------------------------------------ uninstall
        public void RequestUninstall() { if (mode == Mode.Main) AskUninstall(); }

        void AskUninstall()
        {
            if (Core.EmuRunning(s)) { ShowToast("Close the game before uninstalling.", false); return; }
            long kept = Core.KeptDataBytes(s);
            string size = kept > 1073741824 ? (kept / 1073741824.0).ToString("0.0") + " GB" : Math.Max(1, kept / 1048576) + " MB";
            ShowDialog("Uninstall Order Up VR?",
                "This removes the mod, the launcher, its shortcuts and its entry in Windows' installed apps.\n\n" +
                "Your own RPCS3, PS3 firmware, game saves and game files are never touched.",
                "Keep my settings and game cache (" + size + ") so a reinstall later starts quickly", true,
                Btn("Uninstall", delegate(bool keep) { DoUninstall(keep); }), Btn("Cancel", null));
        }

        string pendingCleanup;

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            RunCleanup();
        }

        void RunCleanup()
        {
            if (pendingCleanup == null) return;
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + pendingCleanup + "\""); psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WorkingDirectory = System.IO.Path.GetTempPath(); // never inside the folder being removed
            pendingCleanup = null;
            Program.ReleaseInstanceLock();
            try { Process.Start(psi); } catch { }
        }

        void DoUninstall(bool keepData)
        {
            SetStatus("Removing Order Up VR...", 1, "");
            try
            {
                Core.Uninstall(s, keepData);
                // The launcher exe (and the install folder, if now empty) are removed after this process exits.
                string self = Assembly.GetExecutingAssembly().Location;
                string cmd = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrderUpVR_remove.cmd");
                var sb = new StringBuilder();
                sb.Append("@echo off\r\nping -n 3 127.0.0.1 >nul\r\n");
                sb.Append("del /f /q \"" + self + "\" >nul 2>&1\r\n");
                sb.Append("del /f /q \"" + self + ".new\" >nul 2>&1\r\n");
                if (!keepData) sb.Append("rmdir \"" + Core.EmuDir(s) + "\" >nul 2>&1\r\n");
                sb.Append("rmdir \"" + s.InstallDir + "\" >nul 2>&1\r\n");
                sb.Append("del \"%~f0\"\r\n");
                File.WriteAllText(cmd, sb.ToString());
                pendingCleanup = cmd;
                foreach (string b in new[] { "PlayBtn", "UpdateBtn", "LogsBtn", "SettingsBtn", "UninstallBtn" }) { var x = Get<Button>(b); if (x != null) x.IsEnabled = false; }
                SetStatus("Order Up VR was removed", 0, "");
                ShowDialog("Order Up VR was removed",
                    keepData ? "Your settings and game cache were kept in " + s.InstallDir + ". Run the setup again any time to reinstall." :
                               "Everything Order Up VR added to this PC has been removed.",
                    null, false, Btn("Close", delegate { Close(); }));
            }
            catch (Exception ex) { SetStatus("Uninstall failed", 3, ex.Message); }
        }
    }

    // ====================================================================== entry point
    static class Program
    {
        static Mutex instance;
        static EventWaitHandle showSignal, uninstallSignal;

        public static void ReleaseInstanceLock()
        {
            try { if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); instance = null; } } catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { } // TLS 1.3 where available

            // One launcher at a time: a second start brings the open one to the front.
            bool first;
            instance = new Mutex(true, @"Local\OrderUpVR.Launcher", out first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OrderUpVR.Show");
            uninstallSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OrderUpVR.Uninstall");
            if (!first)
            {
                // Updates and setup hand over to a new instance while the old one is closing: wait briefly for it.
                bool acquired;
                // Only a hand-over (after an update or setup) waits for the previous launcher to close.
                try { acquired = instance.WaitOne(TimeSpan.FromSeconds(Array.IndexOf(args, "--handover") >= 0 ? 8 : 0)); }
                catch (AbandonedMutexException) { acquired = true; } // previous launcher ended without cleaning up
                if (!acquired)
                {
                    // Windows' "Uninstall" button while the launcher is open: ask the open one.
                    if (Array.IndexOf(args, "--uninstall") >= 0) uninstallSignal.Set(); else showSignal.Set();
                    return 0;
                }
            }

            Settings s = Settings.Load();
            bool installed = s != null && !s.Uninstalled && !string.IsNullOrEmpty(s.InstallDir) && File.Exists(Core.LauncherPath(s));
            bool uninstall = Array.IndexOf(args, "--uninstall") >= 0;

            // Setup started again on a PC that already has the mod: hand over to the installed launcher.
            if (installed && Array.IndexOf(args, "--here") < 0 &&
                !string.Equals(System.IO.Path.GetFullPath(Assembly.GetExecutingAssembly().Location), System.IO.Path.GetFullPath(Core.LauncherPath(s)), StringComparison.OrdinalIgnoreCase))
            {
                ReleaseInstanceLock();
                Process.Start(new ProcessStartInfo(Core.LauncherPath(s)) { WorkingDirectory = s.InstallDir, UseShellExecute = false, Arguments = uninstall ? "--uninstall --handover" : "--handover" });
                return 0;
            }
            if (!installed && uninstall) return 0;

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Resources.MergedDictionaries.Add((ResourceDictionary)LauncherWindow.LoadXaml("Theme.xaml"));
            var win = new LauncherWindow(installed ? s : (s != null && s.Uninstalled ? s : null), installed ? LauncherWindow.Mode.Main : LauncherWindow.Mode.Setup, uninstall);

            var listener = new Thread(delegate()
            {
                while (true)
                {
                    int which;
                    try { which = WaitHandle.WaitAny(new WaitHandle[] { showSignal, uninstallSignal }); } catch { return; }
                    try
                    {
                        app.Dispatcher.BeginInvoke(new Action(win.BringToFront));
                        if (which == 1) app.Dispatcher.BeginInvoke(new Action(win.RequestUninstall));
                    }
                    catch { return; }
                }
            });
            listener.IsBackground = true;
            listener.Start();

            return app.Run(win);
        }
    }
}
