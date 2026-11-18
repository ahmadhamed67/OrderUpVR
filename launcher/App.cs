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
using System.Windows.Controls.Primitives;
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

        // ------------------------------------------------------------------ development: pictures of every screen
        // --render-ui <out dir> <game dir> <manifest.json>: test settings that are never saved, no network
        public static int DevShots(string outDir, string gameDir, string manifestPath)
        {
            Settings.NoSave = true;
            string fake = System.IO.Path.Combine(outDir, "install");
            Directory.CreateDirectory(System.IO.Path.Combine(fake, "emu"));
            File.WriteAllText(System.IO.Path.Combine(fake, "emu", "rpcs3.exe"), "");
            var st = new Settings { Rpcs3Dir = @"C:\Emulator\RPCS3", GameDir = gameDir, InstallDir = fake, InstalledVersion = "0.11.0", NoAutoUpdate = true, ShowGameArt = false };
            var app = new Application();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)LoadXaml("Theme.xaml"));
            var win = new LauncherWindow(st, Mode.Main, false);
            var steps = new List<Action>();
            Action<string> shot = delegate(string name)
            {
                var r = (FrameworkElement)win.Content;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)r.ActualWidth, (int)r.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(r);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var f = File.Create(System.IO.Path.Combine(outDir, name + ".png"))) enc.Save(f);
            };
            steps.Add(delegate { win.manifest = Core.FetchManifest(manifestPath); win.manifest.version = "0.11.0"; win.manifest.changelog[0].version = "0.11.0"; win.RenderChangelog(); win.RefreshAll(); });
            steps.Add(delegate { shot("1_home"); win.s.ShowGameArt = true; win.ApplyGameArt(false); });
            steps.Add(delegate { shot("2_home_art"); win.ShowWhatsNew(); });
            steps.Add(delegate { shot("3_whatsnew"); win.CloseDialog(); win.ShowHowTo(); });
            steps.Add(delegate { shot("4_help1"); win.ShowHelpSlide(1); });
            steps.Add(delegate { shot("5_help2"); win.ShowHelpSlide(3); });
            steps.Add(delegate { shot("6_help4"); win.CloseHelp(); win.s.Channel = "beta"; win.OpenSettings(); });
            steps.Add(delegate { shot("7_settings"); var sv = FindChild<ScrollViewer>(win.Get<Grid>("SettingsPage")); if (sv != null) sv.ScrollToVerticalOffset(520); });
            steps.Add(delegate { shot("8_settings_vr"); win.Close(); });
            int k = 0;
            var t = new DispatcherTimer(); t.Interval = TimeSpan.FromMilliseconds(1300);
            t.Tick += delegate { if (k < steps.Count) steps[k++](); else t.Stop(); };
            win.Loaded += delegate { t.Start(); };
            return app.Run(win);
        }

        static T FindChild<T>(DependencyObject d) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is T) return (T)c;
                var r = FindChild<T>(c);
                if (r != null) return r;
            }
            return null;
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
        string ManifestUrl { get { return string.IsNullOrEmpty(s.ManifestUrl) ? Core.ChannelManifestUrl(Core.ChannelOf(s)) : s.ManifestUrl; } }
        // The installed mod is from the other channel than the one picked: installing the picked one counts as an update
        bool ChannelSwitch { get { return manifest != null && manifest.channel != null && ModInstalled && manifest.channel != (s.InstalledChannel ?? "beta"); } }
        bool ModInstalled { get { return !string.IsNullOrEmpty(s.InstalledVersion) && File.Exists(System.IO.Path.Combine(Core.EmuDir(s), "rpcs3.exe")); } }
        bool UpdateAvailable { get { return manifest != null && (!ModInstalled || Core.IsNewer(manifest.version, s.InstalledVersion) || ChannelSwitch); } }

        void BuildMain()
        {
            Get<Button>("PlayBtn").Click += delegate { Play(); };
            Get<Button>("UpdateBtn").Click += delegate { CheckUpdates(true); };
            Get<Button>("SettingsBtn").Click += delegate { OpenSettings(); };
            Get<Button>("LogsBtn").Click += delegate { CollectLogs(); };
            Get<Button>("HelpBtn").Click += delegate { ShowHowTo(); };
            Get<Button>("WhatsNewBtn").Click += delegate { ShowWhatsNew(); };
            Get<Button>("ArtBtn").Click += delegate { ToggleGameArt(); };
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
            ApplyGameArt(false);
            RenderHeroChannel();
            WriteVrFile();
            SetStatus(ModInstalled ? "Ready to play" : "Getting the mod ready...", ModInstalled ? 0 : 1, "");
        }

        // VR choices, grouped. Each: group, key, label, hint, default, then value/display pairs in the order shown.
        // Your chef is not a choice: the mod uses the chef of the career the game loads (the male chef in the menus).
        static readonly string[][] VrOptions =
        {
            new[] { "Comfort", "turn", "Turning", "Right stick left and right", "snap", "snap", "Snap", "smooth", "Smooth" },
            new[] { "Comfort", "snapangle", "Snap angle", "How far one snap turns you", "45", "15", "15°", "30", "30°", "45", "45°", "60", "60°", "90", "90°" },
            new[] { "Comfort", "turnspeed", "Turn speed", "How fast smooth turning goes", "120", "60", "Slow", "90", "Gentle", "120", "Medium", "180", "Fast", "240", "Very fast" },
            new[] { "Comfort", "movespeed", "Walking speed", "Left stick walking", "1.4", "1.0", "Slow", "1.4", "Normal", "2.0", "Fast" },
            new[] { "Comfort", "vignette", "Comfort vignette", "Darkens the edges of your view while the stick moves you", "on", "on", "On", "off", "Off" },
            new[] { "You and the view", "playmode", "Play position", "Seated keeps your chef standing while you sit", "standing", "standing", "Standing", "seated", "Seated" },
            new[] { "You and the view", "stereo", "3D depth", "Off shows one picture to both eyes: smoother, but flat", "on", "on", "On", "off", "Off" },
            new[] { "You and the view", "huddist", "Order tickets distance", "How far the game's tickets and timers float (never inside the counter)", "1.5", "1.0", "Near", "1.5", "Normal", "2.0", "Far" },
            new[] { "You and the view", "cutscenes", "Cutscenes", "Big screen: cutscenes and camera fly-throughs play on the diner screen", "screen", "screen", "Big screen", "3d", "Inside the game" },
            new[] { "In the kitchen", "kitchen", "Kitchen", "Room: the kitchen stays still around you, every station where it really is", "room", "room", "Room (true VR)", "follow", "Follows the camera" },
            new[] { "In the kitchen", "lookswitch", "Look to change station", "Turn your head toward the next station and the game moves there", "on", "on", "On", "off", "Off" },
            new[] { "In the kitchen", "wideview", "Wide view", "The game draws the whole restaurant around you, not just what its camera faces", "on", "on", "On", "off", "Off" },
            new[] { "In the kitchen", "pointing", "Pointing", "Hands: reach for things. Laser: point from a distance", "hand", "hand", "Hands", "laser", "Laser" },
        };

        string VrGet(string key, string def) { string v; return s.Vr != null && s.Vr.TryGetValue(key, out v) ? v : def; }

        void BuildVrRows()
        {
            var rows = Get<StackPanel>("VrRows");
            rows.Children.Clear();
            string group = null;
            foreach (var opt in VrOptions)
            {
                var o = opt;
                // Rows that only matter for one turning style
                if (o[1] == "snapangle" && VrGet("turn", "snap") != "snap") continue;
                if (o[1] == "turnspeed" && VrGet("turn", "snap") != "smooth") continue;
                if (o[0] != group)
                {
                    group = o[0];
                    var gh = new TextBlock(); gh.Text = group.ToUpperInvariant(); gh.Style = (Style)FindResource("Overline");
                    gh.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC5, 0x8A)); gh.Margin = new Thickness(0, rows.Children.Count == 0 ? 10 : 24, 0, 4);
                    rows.Children.Add(gh);
                }
                var g = new Grid(); g.Margin = new Thickness(0, 8, 0, 8);
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var texts = new StackPanel(); texts.VerticalAlignment = VerticalAlignment.Center;
                var label = new TextBlock(); label.Text = o[2]; label.Foreground = Brushes.White; label.FontSize = 14; label.FontWeight = FontWeights.SemiBold;
                var hint = new TextBlock(); hint.Text = o[3]; hint.Foreground = DimBrush; hint.FontSize = 12; hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 2, 16, 0);
                texts.Children.Add(label); texts.Children.Add(hint);
                // Segmented choice: every value visible, the chosen one lit
                var seg = new Border(); seg.CornerRadius = new CornerRadius(12); seg.Padding = new Thickness(3);
                seg.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2B)); seg.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x3E)); seg.BorderThickness = new Thickness(1);
                seg.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(seg, 1);
                var pills = new StackPanel(); pills.Orientation = Orientation.Horizontal;
                seg.Child = pills;
                string cur = VrGet(o[1], o[4]);
                for (int i = 5; i + 1 < o.Length; i += 2)
                {
                    string val = o[i];
                    bool on = val == cur;
                    var pill = new Border(); pill.CornerRadius = new CornerRadius(9); pill.Padding = new Thickness(13, 7, 13, 7); pill.Cursor = Cursors.Hand;
                    pill.Background = on ? AccentBrush : Brushes.Transparent;
                    var pt = new TextBlock(); pt.Text = o[i + 1]; pt.FontSize = 12.5; pt.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                    pt.Foreground = on ? Brushes.Black : TextBrush;
                    pill.Child = pt;
                    if (!on)
                    {
                        pill.MouseEnter += delegate { pill.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3C)); };
                        pill.MouseLeave += delegate { pill.Background = Brushes.Transparent; };
                    }
                    pill.MouseLeftButtonUp += delegate
                    {
                        if (s.Vr == null) s.Vr = new Dictionary<string, string>();
                        s.Vr[o[1]] = val;
                        s.Save();
                        WriteVrFile();
                        BuildVrRows();
                    };
                    pills.Children.Add(pill);
                }
                g.Children.Add(texts); g.Children.Add(seg);
                rows.Children.Add(g);
            }
            var note = new Border(); note.Margin = new Thickness(0, 22, 0, 0); note.CornerRadius = new CornerRadius(12); note.Padding = new Thickness(14, 10, 14, 10);
            note.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0x8A, 0x00)); note.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x8A, 0x00)); note.BorderThickness = new Thickness(1);
            var nt = new TextBlock(); nt.TextWrapping = TextWrapping.Wrap; nt.Foreground = TextBrush; nt.FontSize = 12.5;
            nt.Text = "Your chef is picked by the game: you are the male chef in the menus, and when you start or load a career you become that career's chef.";
            note.Child = nt;
            rows.Children.Add(note);
        }

        void WriteVrFile()
        {
            try
            {
                string dir = System.IO.Path.Combine(Core.EmuDir(s), "config");
                if (!Directory.Exists(dir)) return;
                var sb = new System.Text.StringBuilder();
                sb.Append("chef=auto").AppendLine();
                foreach (var o in VrOptions) sb.Append(o[1]).Append('=').Append(VrGet(o[1], o[4])).AppendLine();
                File.WriteAllText(System.IO.Path.Combine(dir, "orderupvr_comfort.txt"), sb.ToString());
            }
            catch { }
        }

        // ------------------------------------------------------------------ How to play: one idea per card
        const int HowToRevision = 6; // raise when the controls change, so players see the guide again

        class HelpSlide
        {
            public string Glyph, Step, Title, Lead;
            public string[][] Rows;   // key, text
            public bool Controller;   // draw the controller with pointers instead of a picture
        }

        static readonly HelpSlide[] HelpSlides =
        {
            new HelpSlide { Glyph = "", Step = "BEFORE YOU START", Title = "Headset first, then PLAY",
                Lead = "Start your VR app, put the headset on, and press PLAY here. The game opens inside the headset.",
                Rows = new[] { new[] { "1", "Start Quest Link, Air Link, Virtual Desktop or SteamVR" }, new[] { "2", "Press PLAY in this window" }, new[] { "3", "In the headset, pick Flat screen or True VR" }, new[] { "4", "The game asks for the pink SQUARE: press X on your left controller" } } },
            new HelpSlide { Controller = true, Step = "YOUR HANDS", Title = "Your controllers are the Move",
                Lead = "Your hands are the chef's hands. They grab, hold and point just like you do.",
                Rows = new[] { new[] { "TRIGGER", "Choose and confirm. Curls the chef's fingers" }, new[] { "GRIP", "Grab and hold food. Folds the chef's thumb" }, new[] { "A B X Y", "Cross, Circle, Square, Triangle" }, new[] { "MENU", "Pause the game (left controller)" } } },
            new HelpSlide { Glyph = "", Step = "THE DINER", Title = "Menus live in a diner",
                Lead = "Between shifts you stand in a 50s diner. The game's menus hang on the menu board.",
                Rows = new[] { new[] { "POINT", "Aim at the board and pull TRIGGER to choose" }, new[] { "STICKS", "Walk and turn around the diner" }, new[] { "MIRROR", "On the left wall: wave at yourself" }, new[] { "BOTH GRIPS", "Hold them, then the sticks move or resize the board" } } },
            new HelpSlide { Glyph = "", Step = "THE KITCHEN", Title = "You're inside the kitchen",
                Lead = "The kitchen is all around you in 3D and stays still, like a real room.",
                Rows = new[] { new[] { "LOOK", "Turn your head toward the next station and the game goes there" }, new[] { "A / B", "Change station by hand" }, new[] { "REACH", "Your hand is the pointer: touch the food" } } },
            new HelpSlide { Glyph = "", Step = "MOVING", Title = "Walk and turn",
                Lead = "Walk around for real, or use the sticks. Walls keep you inside the restaurant.",
                Rows = new[] { new[] { "LEFT STICK", "Walk where you look" }, new[] { "RIGHT STICK", "Turn (snap or smooth, in Settings)" }, new[] { "STICK CLICK", "Bring everything back in front of you" } } },
            new HelpSlide { Glyph = "", Step = "COOKING", Title = "Cook with your hands",
                Lead = "Do the moves for real: the game feels your hands like a PlayStation Move.",
                Rows = new[] { new[] { "FLIP", "Hold GRIP and flick your wrist up" }, new[] { "EASY FLIP", "Or flick that hand's thumbstick up" }, new[] { "CUTSCENES", "Play on the diner's big screen, then you're back" } } },
            new HelpSlide { Glyph = "", Step = "NEED A HAND?", Title = "Help is always close",
                Lead = "You can open this guide any time with the ? button.",
                Rows = new[] { new[] { "HOLD MENU", "Hold left MENU for 2 seconds in the headset to see the controls" }, new[] { "LOGS", "Something wrong? Press the logs button and send us the zip" } } },
        };

        int helpIndex;
        bool helpBuilt;

        void ShowHowTo()
        {
            s.SeenHowTo = true; s.HowToSeen = HowToRevision; s.Save();
            if (!helpBuilt)
            {
                helpBuilt = true;
                Get<Button>("HelpClose").Click += delegate { CloseHelp(); };
                Get<Button>("HelpBack").Click += delegate { if (helpIndex > 0) ShowHelpSlide(helpIndex - 1); };
                Get<Button>("HelpNext").Click += delegate { if (helpIndex + 1 < HelpSlides.Length) ShowHelpSlide(helpIndex + 1); else CloseHelp(); };
                Get<Grid>("HelpLayer").MouseLeftButtonUp += delegate(object o, MouseButtonEventArgs e) { if (e.OriginalSource == o) CloseHelp(); };
            }
            var layer = Get<Grid>("HelpLayer");
            layer.Visibility = Visibility.Visible;
            layer.Opacity = 0;
            Animate(layer, UIElement.OpacityProperty, 1, 200, 0);
            var sc = (ScaleTransform)Get<Border>("HelpCard").RenderTransform;
            var a = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(280)); a.EasingFunction = EaseOut();
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            ShowHelpSlide(0);
        }

        void CloseHelp()
        {
            var layer = Get<Grid>("HelpLayer");
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += delegate { layer.Visibility = Visibility.Collapsed; };
            layer.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        void ShowHelpSlide(int i)
        {
            bool forward = i >= helpIndex;
            helpIndex = i;
            HelpSlide h = HelpSlides[i];
            Get<TextBlock>("HelpStep").Text = h.Step + "   ·   " + (i + 1) + " OF " + HelpSlides.Length;
            Get<TextBlock>("HelpTitle").Text = h.Title;
            Get<TextBlock>("HelpLead").Text = h.Lead;
            var rows = Get<StackPanel>("HelpRows");
            rows.Children.Clear();
            Grid.SetIsSharedSizeScope(rows, true);
            int n = 0;
            foreach (string[] r in h.Rows)
            {
                var g = new Grid(); g.Margin = new Thickness(0, 0, 0, 12);
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "HelpKey" });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var key = new Border(); key.CornerRadius = new CornerRadius(8); key.Padding = new Thickness(9, 4, 9, 4); key.HorizontalAlignment = HorizontalAlignment.Left; key.VerticalAlignment = VerticalAlignment.Top;
                key.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x8A, 0x00)); key.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x8A, 0x00)); key.BorderThickness = new Thickness(1);
                var kt = new TextBlock(); kt.Text = r[0]; kt.FontSize = 11; kt.FontWeight = FontWeights.Bold; kt.HorizontalAlignment = HorizontalAlignment.Center; kt.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x5C));
                key.Child = kt;
                key.MinWidth = 30; key.Margin = new Thickness(0, 0, 16, 0);
                var tx = new TextBlock(); tx.Text = r[1]; tx.TextWrapping = TextWrapping.Wrap; tx.FontSize = 14; tx.Foreground = TextBrush; tx.Margin = new Thickness(0, 2, 0, 0);
                Grid.SetColumn(tx, 1);
                g.Children.Add(key); g.Children.Add(tx);
                rows.Children.Add(g);
                Reveal(g, forward ? 18 : -18, 0, 60 + 50 * n++);
            }
            Reveal(Get<TextBlock>("HelpTitle"), forward ? 22 : -22, 0, 0);
            Reveal(Get<TextBlock>("HelpLead"), forward ? 22 : -22, 0, 30);

            var dots = Get<StackPanel>("HelpDots");
            dots.Children.Clear();
            for (int k = 0; k < HelpSlides.Length; k++)
            {
                int kk = k;
                var d = new Border(); d.Height = 7; d.CornerRadius = new CornerRadius(4); d.Margin = new Thickness(0, 0, 6, 0); d.Cursor = Cursors.Hand;
                d.Width = k == i ? 26 : 7;
                d.Background = k == i ? AccentBrush : new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4C));
                d.MouseLeftButtonUp += delegate { ShowHelpSlide(kk); };
                dots.Children.Add(d);
            }
            Get<Button>("HelpBack").Visibility = i == 0 ? Visibility.Hidden : Visibility.Visible;
            Get<Button>("HelpNext").Content = i + 1 < HelpSlides.Length ? "Next" : "Let's cook";

            var art = Get<Canvas>("HelpArt");
            art.Children.Clear();
            if (h.Controller) DrawController(art);
            else DrawGlyphArt(art, h.Glyph);
        }

        static Ellipse Blob(double size, Color c, double opacity)
        {
            var e = new Ellipse(); e.Width = e.Height = size; e.Opacity = opacity; e.IsHitTestVisible = false;
            var g = new RadialGradientBrush(); g.GradientStops.Add(new GradientStop(c, 0)); g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            e.Fill = g;
            return e;
        }

        void DrawGlyphArt(Canvas art, string glyph)
        {
            var a = Blob(360, Color.FromRgb(0xFF, 0x8A, 0x00), 0.35); Canvas.SetLeft(a, -60); Canvas.SetTop(a, 20); art.Children.Add(a); Drift(a, 30, 20, 7);
            var b = Blob(300, Color.FromRgb(0xFF, 0x3D, 0x6E), 0.28); Canvas.SetLeft(b, 150); Canvas.SetTop(b, 230); art.Children.Add(b); Drift(b, 25, 30, 9);
            // Orbit rings
            for (int k = 0; k < 2; k++)
            {
                var ring = new Ellipse(); ring.Width = ring.Height = 230 + k * 80; ring.Stroke = new SolidColorBrush(Color.FromArgb((byte)(k == 0 ? 0x40 : 0x22), 0xFF, 0xFF, 0xFF)); ring.StrokeThickness = 1;
                ring.StrokeDashArray = new DoubleCollection(new double[] { 2, 6 });
                Canvas.SetLeft(ring, 185 - ring.Width / 2); Canvas.SetTop(ring, 260 - ring.Height / 2);
                art.Children.Add(ring);
            }
            var circle = new Border(); circle.Width = circle.Height = 150; circle.CornerRadius = new CornerRadius(75);
            circle.Background = (Brush)FindResource("AccentGrad");
            circle.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xFF, 0x7A, 0x00), BlurRadius = 50, ShadowDepth = 0, Opacity = 0.55 };
            var t = new TextBlock(); t.Text = glyph; t.Style = (Style)FindResource("Icon"); t.FontSize = 64; t.Foreground = Brushes.White;
            t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
            circle.Child = t;
            Canvas.SetLeft(circle, 110); Canvas.SetTop(circle, 185);
            art.Children.Add(circle);
            Reveal(circle, 0, 16, 0);
            // Small sparks on the rings
            double[][] sparks = { new[] { 70.0, 200.0, 10 }, new[] { 290.0, 160.0, 8 }, new[] { 300.0, 350.0, 12 }, new[] { 90.0, 380.0, 7 } };
            foreach (var sp in sparks)
            {
                var dot = new Ellipse(); dot.Width = dot.Height = sp[2]; dot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC5, 0x8A));
                Canvas.SetLeft(dot, sp[0]); Canvas.SetTop(dot, sp[1]);
                art.Children.Add(dot);
                Drift(dot, 6, 8, 3 + sp[2] * 0.2);
            }
        }

        // A VR controller seen from the side, with pointers to the buttons the card talks about
        void DrawController(Canvas art)
        {
            var glow = Blob(380, Color.FromRgb(0xFF, 0x8A, 0x00), 0.25); Canvas.SetLeft(glow, -10); Canvas.SetTop(glow, 70); art.Children.Add(glow);
            var body = new Canvas(); body.Width = 370; body.Height = 520;
            var tilt = new RotateTransform(-14, 185, 270); body.RenderTransform = tilt;
            var shell = new LinearGradientBrush(Color.FromRgb(0x3A, 0x40, 0x4D), Color.FromRgb(0x1C, 0x1F, 0x27), 90);
            var edge = new SolidColorBrush(Color.FromRgb(0x55, 0x5C, 0x6B));
            // Tracking ring
            var ring = new Ellipse(); ring.Width = 210; ring.Height = 130; ring.Stroke = shell; ring.StrokeThickness = 18; Canvas.SetLeft(ring, 95); Canvas.SetTop(ring, 95); body.Children.Add(ring);
            var ringEdge = new Ellipse(); ringEdge.Width = 228; ringEdge.Height = 148; ringEdge.Stroke = edge; ringEdge.StrokeThickness = 1; Canvas.SetLeft(ringEdge, 86); Canvas.SetTop(ringEdge, 86); body.Children.Add(ringEdge);
            // Handle
            var handle = new Rectangle(); handle.Width = 82; handle.Height = 210; handle.RadiusX = 40; handle.RadiusY = 40; handle.Fill = shell; handle.Stroke = edge; handle.StrokeThickness = 1;
            Canvas.SetLeft(handle, 140); Canvas.SetTop(handle, 205); body.Children.Add(handle);
            // Face plate
            var face = new Ellipse(); face.Width = 150; face.Height = 92; face.Fill = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x33)); face.Stroke = edge; face.StrokeThickness = 1;
            Canvas.SetLeft(face, 106); Canvas.SetTop(face, 170); body.Children.Add(face);
            // Thumbstick, A, B, menu
            var stick = new Ellipse(); stick.Width = stick.Height = 40; stick.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x17)); stick.Stroke = edge; Canvas.SetLeft(stick, 128); Canvas.SetTop(stick, 192); body.Children.Add(stick);
            var stickTop = new Ellipse(); stickTop.Width = stickTop.Height = 26; stickTop.Fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A)); Canvas.SetLeft(stickTop, 135); Canvas.SetTop(stickTop, 199); body.Children.Add(stickTop);
            Action<string, double, double> button = delegate(string l, double x, double y)
            {
                var bt = new Border(); bt.Width = bt.Height = 26; bt.CornerRadius = new CornerRadius(13); bt.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1D)); bt.BorderBrush = edge; bt.BorderThickness = new Thickness(1);
                var tl = new TextBlock(); tl.Text = l; tl.FontSize = 11; tl.FontWeight = FontWeights.Bold; tl.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE3)); tl.HorizontalAlignment = HorizontalAlignment.Center; tl.VerticalAlignment = VerticalAlignment.Center;
                bt.Child = tl; Canvas.SetLeft(bt, x); Canvas.SetTop(bt, y); body.Children.Add(bt);
            };
            button("A", 192, 214);
            button("B", 212, 184);
            // Trigger (front) and grip (side): lit
            var trig = new Rectangle(); trig.Width = 30; trig.Height = 62; trig.RadiusX = 15; trig.RadiusY = 15; trig.Fill = AccentBrush; Canvas.SetLeft(trig, 222); Canvas.SetTop(trig, 228); body.Children.Add(trig);
            var grip = new Rectangle(); grip.Width = 24; grip.Height = 78; grip.RadiusX = 12; grip.RadiusY = 12; grip.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0x6E)); Canvas.SetLeft(grip, 132); Canvas.SetTop(grip, 262); body.Children.Add(grip);
            art.Children.Add(body);
            Reveal(body, 0, 18, 0);
            // Pointers: from a spot on the controller (in body coordinates) to a label
            Action<double, double, double, double, string, Brush, int> pointer = delegate(double bx, double by, double lx, double ly, string text, Brush col, int delay)
            {
                Point p = tilt.Transform(new Point(bx, by));
                var line = new Line(); line.X1 = p.X; line.Y1 = p.Y; line.X2 = lx; line.Y2 = ly; line.Stroke = col; line.StrokeThickness = 1.5; line.StrokeDashArray = new DoubleCollection(new double[] { 3, 3 });
                art.Children.Add(line);
                var spot = new Ellipse(); spot.Width = spot.Height = 10; spot.Fill = col; Canvas.SetLeft(spot, p.X - 5); Canvas.SetTop(spot, p.Y - 5); art.Children.Add(spot);
                var pulse = new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(900)); pulse.AutoReverse = true; pulse.RepeatBehavior = RepeatBehavior.Forever;
                spot.BeginAnimation(UIElement.OpacityProperty, pulse);
                var lab = new Border(); lab.CornerRadius = new CornerRadius(8); lab.Padding = new Thickness(9, 4, 9, 4); lab.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x20)); lab.BorderBrush = col; lab.BorderThickness = new Thickness(1);
                var lt = new TextBlock(); lt.Text = text; lt.FontSize = 11.5; lt.FontWeight = FontWeights.Bold; lt.Foreground = col;
                lab.Child = lt;
                lab.Measure(new Size(400, 100));
                Canvas.SetLeft(lab, lx < p.X ? lx - lab.DesiredSize.Width : lx); Canvas.SetTop(lab, ly - lab.DesiredSize.Height / 2);
                art.Children.Add(lab);
                Reveal(lab, 0, 8, delay);
                Reveal(line, 0, 0, delay);
            };
            var white = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE3));
            pointer(236, 262, 300, 330, "TRIGGER", AccentBrush, 120);
            pointer(144, 300, 70, 380, "GRIP", new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0x6E)), 200);
            pointer(214, 200, 300, 120, "A  B", white, 280);
            pointer(148, 212, 70, 150, "STICK", white, 360);
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
            chips.Children.Add(Chip(ModInstalled ? "Version " + s.InstalledVersion : "Not installed yet", Brushes.White, glass));
            if (Core.ChannelOf(s) == "beta") chips.Children.Add(Chip("EXPERIMENTAL", Brushes.White, new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0x6E))));
            if (manifest != null)
            {
                if (ChannelSwitch) chips.Children.Add(Chip((manifest.channel == "beta" ? "Experimental " : "Stable ") + manifest.version + " ready", Brushes.Black, AccentBrush));
                else if (UpdateAvailable) chips.Children.Add(Chip("Update " + manifest.version + " ready", Brushes.Black, AccentBrush));
                else chips.Children.Add(Chip("✓ Up to date", GoodBrush, new SolidColorBrush(Color.FromArgb(0x26, 0x3D, 0xDC, 0x97))));
            }
        }

        // One small tile per thing the game needs
        void StatusRow(string label, string value, Brush dot, bool pulse)
        {
            var tile = new Border(); tile.CornerRadius = new CornerRadius(16); tile.Padding = new Thickness(16, 12, 14, 12);
            tile.Margin = new Thickness(Get<UniformGrid>("StatusPanel").Children.Count == 0 ? 0 : 12, 0, 0, 0);
            tile.Background = (Brush)FindResource("Surface"); tile.BorderBrush = (Brush)FindResource("Stroke"); tile.BorderThickness = new Thickness(1);
            tile.ToolTip = value;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var e = new Ellipse(); e.Width = 9; e.Height = 9; e.Fill = dot; e.VerticalAlignment = VerticalAlignment.Top; e.Margin = new Thickness(0, 5, 11, 0);
            if (pulse)
            {
                var a = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(800)); a.AutoReverse = true; a.RepeatBehavior = RepeatBehavior.Forever;
                e.BeginAnimation(UIElement.OpacityProperty, a);
            }
            var sp = new StackPanel(); Grid.SetColumn(sp, 1);
            var l = new TextBlock(); l.Text = label; l.FontSize = 11.5; l.Foreground = DimBrush;
            var v = new TextBlock(); v.Text = value; v.FontSize = 13.5; v.FontWeight = FontWeights.SemiBold; v.Foreground = TextBrush; v.TextTrimming = TextTrimming.CharacterEllipsis; v.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(l); sp.Children.Add(v);
            g.Children.Add(e); g.Children.Add(sp);
            tile.Child = g;
            Get<UniformGrid>("StatusPanel").Children.Add(tile);
        }

        void RenderStatus()
        {
            Get<UniformGrid>("StatusPanel").Children.Clear();
            string runtime = Core.RuntimeName(Core.ActiveOpenXrRuntime());
            if (game != null)
            {
                StatusRow("Game", liveGame ?? "Starting...", AccentBrush, true);
                StatusRow("Headset", liveVr ?? "Waiting for the game", liveVrLevel == 0 ? GoodBrush : liveVrLevel == 1 ? AccentBrush : WarnBrush, liveVrLevel == 1);
            }
            else
            {
                StatusRow("VR app", runtime ?? "Start Quest Link, Air Link, Virtual Desktop or SteamVR", runtime != null ? GoodBrush : WarnBrush, false);
                bool r = Core.IsRpcs3Dir(s.Rpcs3Dir), g = Core.IsGameDir(s.GameDir);
                StatusRow("RPCS3", r ? "Ready" : "Not found: open Settings", r ? GoodBrush : BadBrush, false);
                StatusRow("Game", g ? "Order Up!! (" + Core.TitleId + ")" : "Not found: open Settings", g ? GoodBrush : BadBrush, false);
            }
            string mod = !ModInstalled ? "Installing..." : UpdateAvailable ? "Update " + manifest.version + " ready" : manifest != null ? "Up to date" : "Installed";
            StatusRow("Order Up VR", mod, !ModInstalled ? AccentBrush : UpdateAvailable ? WarnBrush : GoodBrush, false);
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

        // The hero's "What's new" button shows the newest changes in plain words
        void RenderChangelog()
        {
            var btn = Get<TextBlock>("WhatsNewText");
            if (btn == null) return;
            btn.Text = manifest != null && manifest.changelog != null && manifest.changelog.Count > 0 ? "What's new in " + manifest.changelog[0].version : "What's new";
        }

        void ShowWhatsNew()
        {
            if (manifest == null || manifest.changelog == null || manifest.changelog.Count == 0)
            {
                ShowToast(manifest == null ? "Still checking for the latest notes..." : "No release notes yet.", false);
                return;
            }
            ChangelogEntry e = manifest.changelog[0];
            ShowDialog("What's new in " + e.version, (e.date ?? "") + (ModInstalled && e.version == s.InstalledVersion ? "   ·   installed" : ""), null, false,
                Btn("Nice", null), Btn("All releases", delegate { OpenUrl(Core.RepoUrl + "/releases"); }));
            var more = Get<StackPanel>("DialogMore");
            more.Children.Clear();
            List<string> lines = e.simple != null && e.simple.Count > 0 ? e.simple : e.notes;
            if (lines != null)
                foreach (string l in lines)
                {
                    var row = new Grid(); row.Margin = new Thickness(0, 0, 0, 9);
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    var dot = new TextBlock(); dot.Text = "✦"; dot.Foreground = AccentBrush; dot.FontSize = 12; dot.Margin = new Thickness(0, 1, 0, 0);
                    var t = new TextBlock(); t.Text = l; t.TextWrapping = TextWrapping.Wrap; t.Foreground = TextBrush; t.FontSize = 13.5;
                    Grid.SetColumn(t, 1);
                    row.Children.Add(dot); row.Children.Add(t);
                    more.Children.Add(row);
                }
            Get<ScrollViewer>("DialogMoreScroll").Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------ the game's own art, from the player's copy
        static string GameArtPath(Settings st)
        {
            if (st == null || string.IsNullOrEmpty(st.GameDir)) return null;
            foreach (string n in new[] { "PIC1.PNG", "PIC0.PNG", "ICON0.PNG" })
            {
                string p = System.IO.Path.Combine(st.GameDir, "PS3_GAME", n);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        void ApplyGameArt(bool animate)
        {
            var img = Get<Image>("HeroArt");
            string path = s.ShowGameArt ? GameArtPath(s) : null;
            bool on = false;
            if (path != null)
            {
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // the file is not kept open
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                    img.Source = bi;
                    on = true;
                }
                catch { img.Source = null; }
            }
            int ms = animate ? 450 : 0;
            Animate(img, UIElement.OpacityProperty, on ? 1 : 0, ms, 0);
            Animate(Get<UIElement>("HeroShade"), UIElement.OpacityProperty, on ? 1 : 0, ms, 0);
            Animate(Get<UIElement>("HeroGlows"), UIElement.OpacityProperty, on ? 0.25 : 1, ms, 0);
            Get<TextBlock>("ArtText").Text = on ? "Hide game art" : "Game art";
        }

        void ToggleGameArt()
        {
            if (!s.ShowGameArt && GameArtPath(s) == null) { ShowToast("No artwork found in your game folder. Check the folder in Settings.", false); return; }
            s.ShowGameArt = !s.ShowGameArt;
            s.Save();
            ApplyGameArt(true);
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
            bool sw = ChannelSwitch;
            ShowDialog(sw ? (m.channel == "beta" ? "Switch to Experimental" : "Switch to Stable") : "Update available",
                sw ? (m.channel == "beta" ? "Experimental " + m.version + " has the newest VR features and is updated constantly. Things may break. Installing takes about a minute."
                                          : "Stable " + m.version + " is the dependable version. Installing takes about a minute.")
                   : "Order Up VR " + m.version + " is ready (you have " + (string.IsNullOrEmpty(s.InstalledVersion) ? "an older version" : s.InstalledVersion) + "). Updating takes about a minute.",
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
            psi.EnvironmentVariables["ORDERUP_VR_CHANNEL"] = s.InstalledChannel ?? "stable";
            WriteVrFile();
            try
            {
                // The installed build's channel, for the mod (Stable keeps true VR locked)
                string ch = s.InstalledChannel ?? "stable";
                File.WriteAllText(System.IO.Path.Combine(emu, @"config\orderupvr_channel.txt"), "channel=" + ch + "\ntruevr=" + (ch == "beta" ? "1" : "0"));
            }
            catch { }
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
        // Release channel: Stable (dependable) or Experimental (internally "beta": newest, updated constantly)
        static string ChannelName(string ch) { return ch == "beta" ? "Experimental" : "Stable"; }

        Border MakeChannelSwitch()
        {
            string cur = Core.ChannelOf(s);
            var seg = new Border(); seg.CornerRadius = new CornerRadius(12); seg.Padding = new Thickness(3); seg.VerticalAlignment = VerticalAlignment.Center;
            seg.Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x14, 0x17, 0x20)); seg.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x3E)); seg.BorderThickness = new Thickness(1);
            var pills = new StackPanel(); pills.Orientation = Orientation.Horizontal; seg.Child = pills;
            foreach (string val in new[] { "stable", "beta" })
            {
                bool on = val == cur;
                var pill = new Border(); pill.CornerRadius = new CornerRadius(9); pill.Padding = new Thickness(16, 7, 16, 7); pill.Cursor = Cursors.Hand;
                pill.Background = on ? (val == "beta" ? new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0x6E)) : AccentBrush) : Brushes.Transparent;
                pill.ToolTip = val == "beta" ? "Experimental: the newest VR features. Updated constantly; things can break." : "Stable: the dependable version.";
                var pt = new TextBlock(); pt.Text = ChannelName(val); pt.FontSize = 12.5; pt.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal; pt.Foreground = on ? (val == "beta" ? Brushes.White : Brushes.Black) : TextBrush;
                pill.Child = pt;
                string v = val;
                pill.MouseLeftButtonUp += delegate { SwitchChannel(v); };
                pills.Children.Add(pill);
            }
            return seg;
        }

        void SwitchChannel(string val)
        {
            if (val == Core.ChannelOf(s) || busy || game != null) { if (game != null) ShowToast("Close the game first to switch versions.", false); return; }
            s.Channel = val; s.Save();
            BuildChannelRows();
            RenderHeroChannel();
            manifest = null;
            updatePromptShown = false;
            RefreshAll();
            CheckUpdates(true);
        }

        void RenderHeroChannel()
        {
            var host = Get<StackPanel>("HeroChannel");
            if (host == null) return;
            host.Children.Clear();
            var cap = new TextBlock(); cap.Text = "VERSION"; cap.FontSize = 10.5; cap.FontWeight = FontWeights.Bold; cap.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC5, 0x8A)); cap.Margin = new Thickness(4, 0, 0, 6); cap.HorizontalAlignment = HorizontalAlignment.Right;
            host.Children.Add(cap);
            host.Children.Add(MakeChannelSwitch());
            if (Core.ChannelOf(s) == "beta")
            {
                var w = new TextBlock(); w.Text = "⚠ Updated constantly, may break"; w.FontSize = 11.5; w.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8F, 0xAB)); w.Margin = new Thickness(0, 6, 4, 0); w.HorizontalAlignment = HorizontalAlignment.Right;
                host.Children.Add(w);
            }
        }

        void BuildChannelRows()
        {
            var rows = Get<StackPanel>("ChannelRows");
            rows.Children.Clear();
            string cur = Core.ChannelOf(s);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel(); texts.VerticalAlignment = VerticalAlignment.Center;
            var label = new TextBlock(); label.Text = "Version to play"; label.Foreground = Brushes.White; label.FontSize = 14; label.FontWeight = FontWeights.SemiBold;
            var hint = new TextBlock(); hint.Text = "Stable: the dependable version (flat screen for now). Experimental: the newest VR features, updated constantly."; hint.Foreground = DimBrush; hint.FontSize = 12; hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 2, 16, 0);
            texts.Children.Add(label); texts.Children.Add(hint);
            var seg = MakeChannelSwitch(); Grid.SetColumn(seg, 1);
            g.Children.Add(texts); g.Children.Add(seg);
            rows.Children.Add(g);
            if (cur == "beta")
            {
                var warn = new Border(); warn.Margin = new Thickness(0, 12, 0, 0); warn.CornerRadius = new CornerRadius(12); warn.Padding = new Thickness(14, 10, 14, 10);
                warn.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0x3D, 0x6E)); warn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x3D, 0x6E)); warn.BorderThickness = new Thickness(1);
                var wt = new TextBlock(); wt.TextWrapping = TextWrapping.Wrap; wt.Foreground = TextBrush; wt.FontSize = 12.5;
                wt.Text = "⚠  Experimental is for testing. It's updated constantly and things can break. To just play, use Stable.";
                warn.Child = wt;
                rows.Children.Add(warn);
            }
        }

        void OpenSettings()
        {
            BuildChannelRows();
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
            if (args.Length >= 4 && args[0] == "--render-ui") return LauncherWindow.DevShots(args[1], args[2], args[3]);
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
