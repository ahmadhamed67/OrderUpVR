// Order Up VR launcher / setup. One exe: the first run asks for folders and installs itself; later runs check
// GitHub for a new release, install it, show the changelog and start the game.
// Built with the .NET Framework C# compiler that ships with Windows (C# 5 syntax only, no extra dependencies).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Order Up VR")]
[assembly: AssemblyProduct("Order Up VR")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace OrderUpVR
{
    public class ChangelogEntry
    {
        public string version { get; set; }
        public string date { get; set; }
        public List<string> notes { get; set; }
    }

    public class LauncherInfo
    {
        public string version { get; set; }
        public string url { get; set; }
        public string sha256 { get; set; }
    }

    public class Manifest
    {
        public string version { get; set; }
        public string url { get; set; }
        public string sha256 { get; set; }
        public long size { get; set; }
        public LauncherInfo launcher { get; set; }
        public List<ChangelogEntry> changelog { get; set; }
    }

    public class Settings
    {
        public string Rpcs3Dir { get; set; }
        public string GameDir { get; set; }
        public string InstallDir { get; set; }
        public string InstalledVersion { get; set; }
        public string ManifestUrl { get; set; }

        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrderUpVR"); } }
        public static string FilePath { get { return Path.Combine(Folder, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
            }
            catch { return null; }
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this));
        }
    }

    public static class Core
    {
        public const string LauncherVersion = "@LAUNCHER_VERSION@";
        public const string TitleId = "BLES01470";
        // Both placeholders are filled in by launcher/Build.ps1. A local path also works as a manifest, for testing.
        public const string DefaultManifestUrl = "@MANIFEST_URL@";

        public static string EmuDir(Settings s) { return Path.Combine(s.InstallDir, "emu"); }
        public static string LauncherPath(Settings s) { return Path.Combine(s.InstallDir, "OrderUpVR.exe"); }

        public static bool IsRpcs3Dir(string p)
        {
            return !string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "rpcs3.exe")) && Directory.Exists(Path.Combine(p, "dev_flash"));
        }

        public static bool IsGameDir(string p)
        {
            if (string.IsNullOrEmpty(p) || !File.Exists(Path.Combine(p, @"PS3_GAME\USRDIR\EBOOT.BIN"))) return false;
            try
            {
                string sfo = Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(p, @"PS3_GAME\PARAM.SFO")));
                return sfo.Contains(TitleId);
            }
            catch { return false; }
        }

        public static string FindRpcs3()
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] guesses = { @"C:\Emulator\RPCS3", @"C:\RPCS3", @"D:\RPCS3", @"C:\Games\RPCS3", @"D:\Games\RPCS3", @"D:\Emulator\RPCS3",
                Path.Combine(user, "RPCS3"), Path.Combine(user, @"Desktop\RPCS3"), Path.Combine(user, @"Downloads\RPCS3"), Path.Combine(user, @"Documents\RPCS3") };
            foreach (string g in guesses) if (IsRpcs3Dir(g)) return g;
            return "";
        }

        public static string FindGame(string rpcs3Dir)
        {
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(rpcs3Dir))
            {
                roots.Add(Path.Combine(rpcs3Dir, "games"));
                string parent = Path.GetDirectoryName(rpcs3Dir.TrimEnd('\\'));
                if (parent != null) { roots.Add(parent); roots.Add(Path.Combine(parent, "Games")); }
            }
            foreach (string root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string d in Directory.GetDirectories(root)) if (IsGameDir(d)) return d;
                }
                catch { }
            }
            return "";
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
            {
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
        }

        public static bool IsNewer(string candidate, string current)
        {
            Version a, b;
            if (!Version.TryParse(candidate ?? "", out a)) return false;
            if (!Version.TryParse(current ?? "", out b)) return true;
            return a > b;
        }

        public static string ResolveUrl(string manifestUrl, string url)
        {
            if (url.Contains("://") || Path.IsPathRooted(url)) return url;
            if (manifestUrl.Contains("://")) return new Uri(new Uri(manifestUrl), url).ToString();
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestUrl)), url);
        }

        public static Manifest FetchManifest(string url)
        {
            using (var wc = NewClient())
            {
                string json = wc.DownloadString(ToUri(url));
                return new JavaScriptSerializer().Deserialize<Manifest>(json);
            }
        }

        public static WebClient NewClient()
        {
            var wc = new WebClient();
            wc.Encoding = Encoding.UTF8;
            wc.Headers[HttpRequestHeader.UserAgent] = "OrderUpVR-Launcher";
            wc.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            return wc;
        }

        public static Uri ToUri(string url)
        {
            return url.Contains("://") ? new Uri(url) : new Uri(Path.GetFullPath(url));
        }

        public static bool EmuRunning(Settings s)
        {
            string exe = Path.Combine(EmuDir(s), "rpcs3.exe");
            foreach (Process p in Process.GetProcessesByName("rpcs3"))
            {
                try { if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            }
            return false;
        }

        static void CopyTree(string src, string dst, bool overwrite)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                string target = Path.Combine(dst, Path.GetFileName(f));
                if (overwrite || !File.Exists(target)) File.Copy(f, target, true);
            }
            foreach (string d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)), overwrite);
        }

        static void Junction(string link, string target)
        {
            if (Directory.Exists(link) || !Directory.Exists(target)) return;
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            using (Process p = Process.Start(psi)) p.WaitForExit();
        }

        // Installs a downloaded mod package into <InstallDir>\emu without touching the user's own RPCS3.
        public static void ApplyPackage(Settings s, string zipPath, string version)
        {
            string emu = EmuDir(s);
            string tmp = Path.Combine(Path.GetTempPath(), "OrderUpVR_pkg_" + Guid.NewGuid().ToString("N"));
            try
            {
                ZipFile.ExtractToDirectory(zipPath, tmp);
                CopyTree(Path.Combine(tmp, "rpcs3"), emu, true);

                // Firmware and saves stay in the user's RPCS3; the VR copy only links to them.
                foreach (string d in new[] { "dev_flash", "dev_flash2", "dev_flash3", "dev_hdd0", "dev_hdd1", "dev_bdvd", "dev_usb000" })
                    Junction(Path.Combine(emu, d), Path.Combine(s.Rpcs3Dir, d));

                // Settings start as a copy of the user's, taken once.
                if (!Directory.Exists(Path.Combine(emu, "config")) && Directory.Exists(Path.Combine(s.Rpcs3Dir, "config")))
                    CopyTree(Path.Combine(s.Rpcs3Dir, "config"), Path.Combine(emu, "config"), false);
                if (!File.Exists(Path.Combine(emu, @"GuiConfigs\CurrentSettings.ini")) && Directory.Exists(Path.Combine(s.Rpcs3Dir, "GuiConfigs")))
                    CopyTree(Path.Combine(s.Rpcs3Dir, "GuiConfigs"), Path.Combine(emu, "GuiConfigs"), false);

                // The per-game settings the mod needs are refreshed with every update.
                string custom = Path.Combine(emu, @"config\custom_configs");
                Directory.CreateDirectory(custom);
                File.Copy(Path.Combine(tmp, @"config\config_" + TitleId + ".yml"), Path.Combine(custom, "config_" + TitleId + ".yml"), true);
                string input = Path.Combine(emu, @"config\input_configs\" + TitleId);
                Directory.CreateDirectory(input);
                File.Copy(Path.Combine(tmp, @"config\input_" + TitleId + ".yml"), Path.Combine(input, "Default.yml"), true);

                s.InstalledVersion = version;
                s.Save();
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        public static void Shortcut(string lnk, string target, string workDir)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Order Up VR" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        public static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Order Up VR.lnk"); } }
        public static string StartLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Order Up VR.lnk"); } }

        public static string ActiveOpenXrRuntime()
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1"))
                {
                    string v = k == null ? null : k.GetValue("ActiveRuntime") as string;
                    return (v != null && File.Exists(v)) ? v : null;
                }
            }
            catch { return null; }
        }

        public static void Uninstall(Settings s)
        {
            string emu = EmuDir(s);
            if (Directory.Exists(emu))
            {
                // Remove links first so nothing they point to can be deleted.
                foreach (string d in Directory.GetDirectories(emu))
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, false);
                foreach (string d in Directory.GetDirectories(emu))
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) throw new IOException("A link could not be removed: " + d);
                Directory.Delete(emu, true);
            }
            try { File.Delete(DesktopLink); } catch { }
            try { File.Delete(StartLink); } catch { }
            try { File.Delete(Settings.FilePath); } catch { }
        }
    }

    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(24, 26, 32);
        public static readonly Color Panel = Color.FromArgb(34, 37, 46);
        public static readonly Color Text = Color.FromArgb(235, 236, 240);
        public static readonly Color Dim = Color.FromArgb(150, 155, 168);
        public static readonly Color Accent = Color.FromArgb(255, 153, 0);

        public static Button MakeButton(string text, bool primary)
        {
            var b = new Button();
            b.Text = text; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.BackColor = primary ? Accent : Panel; b.ForeColor = primary ? Color.Black : Text;
            b.Font = new Font("Segoe UI", primary ? 14f : 9.5f, primary ? FontStyle.Bold : FontStyle.Regular);
            b.Cursor = Cursors.Hand;
            return b;
        }
    }

    public class SetupForm : Form
    {
        TextBox rpcs3Box, gameBox, installBox;
        Label status;
        public Settings Result;

        public SetupForm(Settings existing)
        {
            Text = "Order Up VR Setup"; ClientSize = new Size(640, 380); StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; BackColor = Theme.Back; ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);

            var title = new Label(); title.Text = "Order Up!! VR"; title.Font = new Font("Segoe UI", 20f, FontStyle.Bold); title.ForeColor = Theme.Accent;
            title.SetBounds(24, 16, 590, 40); Controls.Add(title);
            var sub = new Label(); sub.Text = "Choose three folders. Your own RPCS3, saves and game files are not changed."; sub.ForeColor = Theme.Dim;
            sub.SetBounds(26, 58, 590, 22); Controls.Add(sub);

            string rp = existing != null ? existing.Rpcs3Dir : Core.FindRpcs3();
            string gm = existing != null ? existing.GameDir : Core.FindGame(rp);
            string ins = existing != null ? existing.InstallDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\OrderUpVR");

            rpcs3Box = Row("Your RPCS3 folder (contains rpcs3.exe)", rp, 96);
            gameBox = Row("Order Up!! game folder (contains PS3_GAME)", gm, 164);
            installBox = Row("Where to install Order Up VR", ins, 232);
            if (existing != null) installBox.Enabled = false;

            status = new Label(); status.ForeColor = Color.FromArgb(255, 120, 120); status.SetBounds(26, 296, 590, 22); Controls.Add(status);

            Button ok = Theme.MakeButton(existing != null ? "Save" : "Install", true);
            ok.Font = new Font("Segoe UI", 11f, FontStyle.Bold); ok.SetBounds(476, 324, 140, 40); ok.Click += OnOk; Controls.Add(ok);
            Button cancel = Theme.MakeButton("Cancel", false); cancel.SetBounds(366, 324, 100, 40);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; }; Controls.Add(cancel);
        }

        TextBox Row(string caption, string value, int y)
        {
            var l = new Label(); l.Text = caption; l.SetBounds(26, y, 590, 20); Controls.Add(l);
            var t = new TextBox(); t.Text = value ?? ""; t.SetBounds(26, y + 24, 486, 26); t.BackColor = Theme.Panel; t.ForeColor = Theme.Text; t.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(t);
            Button b = Theme.MakeButton("Browse...", false); b.SetBounds(520, y + 23, 96, 27);
            b.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = caption;
                    if (Directory.Exists(t.Text)) d.SelectedPath = t.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.SelectedPath;
                }
            };
            Controls.Add(b);
            return t;
        }

        void OnOk(object sender, EventArgs e)
        {
            string rp = rpcs3Box.Text.Trim(), gm = gameBox.Text.Trim(), ins = installBox.Text.Trim();
            if (!Core.IsRpcs3Dir(rp)) { status.Text = "That RPCS3 folder needs rpcs3.exe and a dev_flash folder (installed PS3 firmware)."; return; }
            if (!Core.IsGameDir(gm)) { status.Text = "That is not the European Order Up!! (" + Core.TitleId + ") game folder."; return; }
            if (ins.Length < 4) { status.Text = "Choose an install folder."; return; }
            if (string.Equals(Path.GetFullPath(ins).TrimEnd('\\'), Path.GetFullPath(rp).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            { status.Text = "Install to a separate folder, not into your RPCS3 folder."; return; }
            try { Directory.CreateDirectory(ins); }
            catch (Exception ex) { status.Text = "Cannot create the install folder: " + ex.Message; return; }

            Result = new Settings();
            Result.Rpcs3Dir = rp; Result.GameDir = gm; Result.InstallDir = ins;
            DialogResult = DialogResult.OK;
        }
    }

    public class MainForm : Form
    {
        Settings s;
        Manifest manifest;
        Label versionLabel, statusLabel;
        RichTextBox log;
        ProgressBar bar;
        Button playBtn, updateBtn;
        Timer timer;
        bool busy;

        public MainForm(Settings settings)
        {
            s = settings;
            Text = "Order Up VR"; ClientSize = new Size(760, 540); StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; BackColor = Theme.Back; ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);

            var title = new Label(); title.Text = "Order Up!! VR"; title.Font = new Font("Segoe UI", 24f, FontStyle.Bold); title.ForeColor = Theme.Accent;
            title.SetBounds(24, 14, 480, 48); Controls.Add(title);
            versionLabel = new Label(); versionLabel.ForeColor = Theme.Dim; versionLabel.SetBounds(28, 64, 700, 22); Controls.Add(versionLabel);

            var head = new Label(); head.Text = "What's new"; head.Font = new Font("Segoe UI", 11f, FontStyle.Bold); head.SetBounds(26, 98, 300, 24); Controls.Add(head);
            log = new RichTextBox(); log.ReadOnly = true; log.BorderStyle = BorderStyle.None; log.BackColor = Theme.Panel; log.ForeColor = Theme.Text;
            log.Font = new Font("Segoe UI", 10f); log.SetBounds(26, 126, 708, 280); log.TabStop = false; Controls.Add(log);

            bar = new ProgressBar(); bar.SetBounds(26, 418, 708, 8); bar.Style = ProgressBarStyle.Continuous; Controls.Add(bar);
            statusLabel = new Label(); statusLabel.ForeColor = Theme.Dim; statusLabel.SetBounds(26, 432, 708, 22); Controls.Add(statusLabel);

            playBtn = Theme.MakeButton("PLAY", true); playBtn.SetBounds(534, 468, 200, 54); playBtn.Click += delegate { Play(); }; Controls.Add(playBtn);
            updateBtn = Theme.MakeButton("Check for updates", false); updateBtn.SetBounds(26, 478, 150, 36); updateBtn.Click += delegate { CheckUpdates(); }; Controls.Add(updateBtn);
            Button folders = Theme.MakeButton("Folders...", false); folders.SetBounds(184, 478, 100, 36); folders.Click += delegate { EditFolders(); }; Controls.Add(folders);
            Button logs = Theme.MakeButton("Collect logs", false); logs.SetBounds(292, 478, 110, 36); logs.Click += delegate { CollectLogs(); }; Controls.Add(logs);
            Button un = Theme.MakeButton("Uninstall", false); un.SetBounds(410, 478, 100, 36); un.Click += delegate { DoUninstall(); }; Controls.Add(un);

            RefreshLabels();
            Shown += delegate { CheckUpdates(); };

            // Keep checking while the launcher stays open.
            timer = new Timer(); timer.Interval = 30 * 60 * 1000; timer.Tick += delegate { CheckUpdates(); }; timer.Start();
        }

        string ManifestUrl { get { return string.IsNullOrEmpty(s.ManifestUrl) ? Core.DefaultManifestUrl : s.ManifestUrl; } }
        bool ModInstalled { get { return !string.IsNullOrEmpty(s.InstalledVersion) && File.Exists(Path.Combine(Core.EmuDir(s), "rpcs3.exe")); } }

        void RefreshLabels()
        {
            string latest = manifest != null ? manifest.version : "?";
            versionLabel.Text = "Installed mod: " + (ModInstalled ? s.InstalledVersion : "none") + "     Latest: " + latest + "     Launcher: " + Core.LauncherVersion;
            playBtn.Enabled = ModInstalled && !busy;
            updateBtn.Enabled = !busy;
        }

        void SetStatus(string text) { statusLabel.Text = text; }

        void ShowChangelog()
        {
            log.Clear();
            if (manifest == null || manifest.changelog == null) return;
            foreach (ChangelogEntry e in manifest.changelog)
            {
                log.SelectionFont = new Font("Segoe UI", 11f, FontStyle.Bold);
                log.SelectionColor = Theme.Accent;
                log.AppendText("Version " + e.version + (string.IsNullOrEmpty(e.date) ? "" : "   " + e.date) + "\n");
                log.SelectionFont = new Font("Segoe UI", 10f);
                log.SelectionColor = Theme.Text;
                if (e.notes != null) foreach (string n in e.notes) log.AppendText("   \u2022 " + n + "\n");
                log.AppendText("\n");
            }
            log.SelectionStart = 0; log.ScrollToCaret();
        }

        void CheckUpdates()
        {
            if (busy) return;
            busy = true; RefreshLabels(); SetStatus("Checking for updates...");
            string url = ManifestUrl;
            Task.Factory.StartNew(delegate
            {
                try
                {
                    Manifest m = Core.FetchManifest(url);
                    BeginInvoke((MethodInvoker)delegate { OnManifest(m); });
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false; RefreshLabels();
                        SetStatus("Could not check for updates (" + ex.Message + ")." + (ModInstalled ? " You can still play the installed version." : ""));
                    });
                }
            });
        }

        void OnManifest(Manifest m)
        {
            manifest = m; busy = false; ShowChangelog(); RefreshLabels();

            if (m.launcher != null && Core.IsNewer(m.launcher.version, Core.LauncherVersion)) { UpdateLauncher(m.launcher); return; }

            if (!ModInstalled || Core.IsNewer(m.version, s.InstalledVersion)) DownloadMod(m);
            else SetStatus("You have the latest version.");
        }

        void DownloadMod(Manifest m)
        {
            if (Core.EmuRunning(s)) { SetStatus("Version " + m.version + " is available. Close the game to install it."); return; }
            busy = true; RefreshLabels();
            string zip = Path.Combine(Path.GetTempPath(), "OrderUpVR_" + m.version + ".zip");
            var wc = Core.NewClient();
            wc.DownloadProgressChanged += delegate(object o, DownloadProgressChangedEventArgs e)
            {
                bar.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                SetStatus("Downloading version " + m.version + "...  " + (e.BytesReceived / 1048576) + " / " + (Math.Max(e.TotalBytesToReceive, m.size) / 1048576) + " MB");
            };
            wc.DownloadFileCompleted += delegate(object o, System.ComponentModel.AsyncCompletedEventArgs e)
            {
                wc.Dispose();
                if (e.Error != null) { busy = false; bar.Value = 0; RefreshLabels(); SetStatus("Download failed: " + e.Error.Message); return; }
                SetStatus("Installing version " + m.version + "...");
                Task.Factory.StartNew(delegate
                {
                    string error = null;
                    try
                    {
                        if (string.IsNullOrEmpty(m.sha256) || !string.Equals(Core.Sha256(zip), m.sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("the downloaded file is damaged (checksum mismatch)");
                        Core.ApplyPackage(s, zip, m.version);
                    }
                    catch (Exception ex) { error = ex.Message; }
                    try { File.Delete(zip); } catch { }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false; bar.Value = 0; RefreshLabels();
                        SetStatus(error == null ? "Version " + m.version + " installed. Ready to play." : "Install failed: " + error);
                    });
                });
            };
            try { wc.DownloadFileAsync(Core.ToUri(Core.ResolveUrl(ManifestUrl, m.url)), zip); }
            catch (Exception ex) { busy = false; RefreshLabels(); SetStatus("Download failed: " + ex.Message); }
        }

        void UpdateLauncher(LauncherInfo info)
        {
            busy = true; RefreshLabels(); SetStatus("Updating the launcher to " + info.version + "...");
            string self = Application.ExecutablePath;
            string fresh = self + ".new";
            Task.Factory.StartNew(delegate
            {
                string error = null;
                try
                {
                    using (var wc = Core.NewClient()) wc.DownloadFile(Core.ToUri(Core.ResolveUrl(ManifestUrl, info.url)), fresh);
                    if (string.IsNullOrEmpty(info.sha256) || !string.Equals(Core.Sha256(fresh), info.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("checksum mismatch");
                    // Swap the exe after this process exits, then start the new one.
                    string cmd = Path.Combine(Path.GetTempPath(), "OrderUpVR_swap.cmd");
                    File.WriteAllText(cmd, "@echo off\r\nping -n 3 127.0.0.1 >nul\r\nmove /y \"" + fresh + "\" \"" + self + "\" >nul\r\nstart \"\" \"" + self + "\"\r\ndel \"%~f0\"\r\n");
                    var psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\""); psi.CreateNoWindow = true; psi.UseShellExecute = false;
                    Process.Start(psi);
                }
                catch (Exception ex) { error = ex.Message; try { File.Delete(fresh); } catch { } }
                BeginInvoke((MethodInvoker)delegate
                {
                    if (error == null) { Application.Exit(); return; }
                    busy = false; RefreshLabels(); SetStatus("Launcher update failed (" + error + "). Continuing with this version.");
                    if (manifest != null && (!ModInstalled || Core.IsNewer(manifest.version, s.InstalledVersion))) DownloadMod(manifest);
                });
            });
        }

        void Play()
        {
            if (Core.EmuRunning(s)) { SetStatus("The game is already running."); return; }
            if (!Core.IsGameDir(s.GameDir) || !Core.IsRpcs3Dir(s.Rpcs3Dir)) { SetStatus("A folder has moved. Click Folders... to fix it."); return; }
            if (Core.ActiveOpenXrRuntime() == null)
            {
                DialogResult r = MessageBox.Show(this, "No PC VR software is active on this PC, so the game will start on the monitor only.\n\n" +
                    "For VR: connect the headset with Quest Link, Air Link, Virtual Desktop or SteamVR first, and make that app the active OpenXR runtime in its settings.\n\nStart anyway?",
                    "Order Up VR", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r != DialogResult.Yes) return;
            }
            string emu = Core.EmuDir(s);
            var psi = new ProcessStartInfo(Path.Combine(emu, "rpcs3.exe"), "--no-gui \"" + Path.Combine(s.GameDir, @"PS3_GAME\USRDIR\EBOOT.BIN") + "\"");
            psi.WorkingDirectory = emu; psi.UseShellExecute = false;
            try { Process.Start(psi); SetStatus("Starting the game. The first start after an update takes a few minutes."); }
            catch (Exception ex) { SetStatus("Could not start: " + ex.Message); }
        }

        void EditFolders()
        {
            using (var f = new SetupForm(s))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                s.Rpcs3Dir = f.Result.Rpcs3Dir; s.GameDir = f.Result.GameDir; s.Save();
                SetStatus("Folders saved.");
            }
        }

        void CollectLogs()
        {
            try
            {
                string emu = Core.EmuDir(s);
                string tmp = Path.Combine(Path.GetTempPath(), "OrderUpVR_logs_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                foreach (string rel in new[] { @"log\orderup_vr.log", @"log\RPCS3.log", @"log\TTY.log", @"config\custom_configs\config_" + Core.TitleId + ".yml" })
                {
                    string p = Path.Combine(emu, rel);
                    if (File.Exists(p) && new FileInfo(p).Length < 50 * 1048576) File.Copy(p, Path.Combine(tmp, Path.GetFileName(p)), true);
                }
                File.WriteAllText(Path.Combine(tmp, "system.txt"), "Mod version: " + s.InstalledVersion + "\r\nLauncher: " + Core.LauncherVersion +
                    "\r\nOpenXR runtime: " + (Core.ActiveOpenXrRuntime() ?? "none") + "\r\nWindows: " + Environment.OSVersion.VersionString + "\r\n");
                string zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OrderUpVR_logs_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
                ZipFile.CreateFromDirectory(tmp, zip);
                Directory.Delete(tmp, true);
                SetStatus("Logs saved to your Desktop: " + Path.GetFileName(zip));
            }
            catch (Exception ex) { SetStatus("Could not collect logs: " + ex.Message); }
        }

        void DoUninstall()
        {
            if (Core.EmuRunning(s)) { SetStatus("Close the game first."); return; }
            if (MessageBox.Show(this, "Remove Order Up VR from this PC?\n\nYour own RPCS3, firmware, saves and game files are kept.", "Order Up VR",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                Core.Uninstall(s);
                // The launcher removes itself after it exits.
                string cmd = Path.Combine(Path.GetTempPath(), "OrderUpVR_remove.cmd");
                File.WriteAllText(cmd, "@echo off\r\nping -n 3 127.0.0.1 >nul\r\ndel \"" + Application.ExecutablePath + "\" >nul 2>&1\r\nrmdir \"" + s.InstallDir + "\" >nul 2>&1\r\ndel \"%~f0\"\r\n");
                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\""); psi.CreateNoWindow = true; psi.UseShellExecute = false;
                Process.Start(psi);
                Application.Exit();
            }
            catch (Exception ex) { SetStatus("Uninstall failed: " + ex.Message); }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2

            Settings s = Settings.Load();
            bool installed = s != null && !string.IsNullOrEmpty(s.InstallDir) && File.Exists(Core.LauncherPath(s));

            if (!installed)
            {
                using (var setup = new SetupForm(null))
                {
                    if (setup.ShowDialog() != DialogResult.OK) return;
                    s = setup.Result;
                }
                foreach (string a in args) if (a.StartsWith("--manifest=")) s.ManifestUrl = a.Substring(11);
                try
                {
                    File.Copy(Application.ExecutablePath, Core.LauncherPath(s), true);
                    s.Save();
                    Core.Shortcut(Core.DesktopLink, Core.LauncherPath(s), s.InstallDir);
                    Core.Shortcut(Core.StartLink, Core.LauncherPath(s), s.InstallDir);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Setup could not finish: " + ex.Message, "Order Up VR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Process.Start(new ProcessStartInfo(Core.LauncherPath(s)) { WorkingDirectory = s.InstallDir });
                return;
            }

            // The setup file was started again on a PC that already has the mod: hand over to the installed launcher.
            if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Core.LauncherPath(s)), StringComparison.OrdinalIgnoreCase)
                && Array.IndexOf(args, "--here") < 0)
            {
                Process.Start(new ProcessStartInfo(Core.LauncherPath(s)) { WorkingDirectory = s.InstallDir });
                return;
            }

            Application.Run(new MainForm(s));
        }
    }
}
