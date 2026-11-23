// Order Up VR launcher: settings, update manifest, install/uninstall and Windows helpers (no UI).
// Built with the .NET Framework C# compiler that ships with Windows (C# 5 syntax only).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
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
        public List<string> simple { get; set; }   // the same changes in plain words, for the update prompt
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
        public string channel { get; set; }      // "stable" or "beta" (missing in releases before 0.12.1)
        public List<ChangelogEntry> changelog { get; set; }
        public string vcredistMin { get; set; }   // minimum Microsoft Visual C++ runtime the RPCS3 build needs
        public string vcredistUrl { get; set; }
    }

    public class Settings
    {
        public string Rpcs3Dir { get; set; }
        public string GameDir { get; set; }
        public string InstallDir { get; set; }
        public string InstalledVersion { get; set; }
        public string ManifestUrl { get; set; }
        public bool NoAutoUpdate { get; set; }       // default false = check automatically
        public bool KeepLauncherOpen { get; set; }   // default false = minimize while playing
        public bool NoLiveStream { get; set; }       // default false = Experimental streams live test data to the developer
        public bool Uninstalled { get; set; }        // removed with "keep my data"; setup offers to reuse it
        public string PreparedVersion { get; set; }  // mod version that already finished its first-start preparation
        public bool SeenHowTo { get; set; }          // the controls guide was shown once
        public int HowToSeen { get; set; }
        public bool ShowGameArt { get; set; }
        public string Channel { get; set; }          // release channel the player picked: "stable" (default) or "beta"
        public Dictionary<string, string> Installs { get; set; }   // each channel's installed mod version (they live side by side)
        public bool ModsEnabled { get; set; }                       // the mods master switch (Experimental)
        public Dictionary<string, bool> Mods { get; set; }          // each mod on or off
        public string InstalledChannel { get; set; } // channel of the installed mod (null: installed before channels existed = beta)      // the hero shows the game's own artwork from the player's game folder
        public Dictionary<string, string> Vr { get; set; }   // VR comfort and body choices (written to the mod as orderupvr_comfort.txt)           // revision of the controls guide last shown (shown again when controls change)

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

        public static bool NoSave;   // development pictures: never touch the real settings

        public void Save()
        {
            if (NoSave) return;
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

        // Stable: the newest full release. Beta: a release tagged "beta" whose manifest is replaced with every beta build.
        public static string ChannelManifestUrl(string channel)
        {
            if (channel != "beta") return DefaultManifestUrl;
            const string tail = "/latest/download/manifest.json";
            return DefaultManifestUrl.EndsWith(tail) ? DefaultManifestUrl.Substring(0, DefaultManifestUrl.Length - tail.Length) + "/download/beta/manifest.json" : DefaultManifestUrl;
        }
        public static string ChannelOf(Settings s) { return s.Channel == "beta" ? "beta" : "stable"; }

        // Each channel has its own copy of the mod, so switching back and forth never downloads again
        public static string EmuDir(Settings s) { return EmuDir(s, ChannelOf(s)); }
        public static string EmuDir(Settings s, string channel) { return Path.Combine(s.InstallDir, channel == "beta" ? "emu-experimental" : "emu"); }

        // After loading settings or switching channel: the installed version shown is the picked channel's
        public static void SyncChannel(Settings s)
        {
            if (s.Installs == null)
            {
                s.Installs = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(s.InstalledVersion))
                {
                    // Before side-by-side installs there was one copy in "emu"; an Experimental one moves to its own folder
                    string ch = s.InstalledChannel ?? "beta";
                    if (ch == "beta" && !string.IsNullOrEmpty(s.InstallDir))
                    {
                        string from = Path.Combine(s.InstallDir, "emu"), to = Path.Combine(s.InstallDir, "emu-experimental");
                        try { if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to); } catch { }
                    }
                    s.Installs[ch] = s.InstalledVersion;
                }
            }
            string cur = ChannelOf(s);
            string v;
            s.InstalledVersion = s.Installs.TryGetValue(cur, out v) ? v : null;
            s.InstalledChannel = cur;
        }
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
                RemoveStaleFiles(emu, Path.Combine(tmp, "rpcs3"));
                CopyTree(Path.Combine(tmp, "rpcs3"), emu, true);
                WriteFileList(emu, Path.Combine(tmp, "rpcs3"));

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
                // Which channel this build is (the mod offers true VR only where it is ready)
                string chan = Path.Combine(tmp, @"config\orderupvr_channel.txt");
                s.InstalledChannel = "beta";
                if (File.Exists(chan))
                {
                    File.Copy(chan, Path.Combine(emu, @"config\orderupvr_channel.txt"), true);
                    foreach (string line in File.ReadAllLines(chan)) if (line.StartsWith("channel=")) s.InstalledChannel = line.Substring(8).Trim();
                }

                s.InstalledVersion = version;
                if (s.Installs == null) s.Installs = new Dictionary<string, string>();
                s.Installs[ChannelOf(s)] = version;
                s.Uninstalled = false;
                s.Save();
                try { File.WriteAllText(Path.Combine(s.InstallDir, MarkerName), "Order Up VR install folder\r\n"); } catch { }
                RegisterApp(s);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        const string FileListName = "orderupvr_files.txt";

        // Program files from an older version that the new package no longer contains are deleted, so nothing stale
        // (for example DLLs RPCS3 refuses to run with) survives an update. User data folders are never listed.
        static void RemoveStaleFiles(string emu, string fresh)
        {
            if (!Directory.Exists(emu)) return;
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(fresh, "*", SearchOption.AllDirectories)) keep.Add(f.Substring(fresh.Length + 1));

            var old = new List<string>();
            string list = Path.Combine(emu, FileListName);
            if (File.Exists(list)) old.AddRange(File.ReadAllLines(list));
            // Installs made before the file list existed: their program files are the DLLs next to rpcs3.exe.
            foreach (string f in Directory.GetFiles(emu, "*.dll")) old.Add(Path.GetFileName(f));

            foreach (string rel in old)
            {
                if (string.IsNullOrEmpty(rel) || rel.Contains("..") || Path.IsPathRooted(rel) || keep.Contains(rel)) continue;
                try { File.Delete(Path.Combine(emu, rel)); } catch { }
            }
        }

        static void WriteFileList(string emu, string fresh)
        {
            var lines = new List<string>();
            foreach (string f in Directory.GetFiles(fresh, "*", SearchOption.AllDirectories)) lines.Add(f.Substring(fresh.Length + 1));
            File.WriteAllLines(Path.Combine(emu, FileListName), lines.ToArray());
        }

        // ----------------------------------------------------------------- Microsoft Visual C++ runtime
        public const string DefaultVcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        public static Version VcRedistVersion()
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"))
                {
                    if (k == null || Convert.ToInt32(k.GetValue("Installed", 0)) != 1) return null;
                    return new Version(Convert.ToInt32(k.GetValue("Major", 0)), Convert.ToInt32(k.GetValue("Minor", 0)), Convert.ToInt32(k.GetValue("Bld", 0)), 0);
                }
            }
            catch { return null; }
        }

        public static bool VcRedistOk(string minimum)
        {
            Version have = VcRedistVersion();
            if (have == null) return false;
            Version need;
            if (!Version.TryParse(minimum ?? "", out need)) need = new Version(14, 44, 0, 0);
            return have >= need;
        }

        // The downloaded installer must carry a valid Authenticode signature from Microsoft before it is run.
        public static bool SignedByMicrosoft(string file)
        {
            try
            {
                if (WinTrust.Verify(file) != 0) return false;
                var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file));
                return cert.Subject.Contains("O=Microsoft Corporation");
            }
            catch { return false; }
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

        // Kept on "uninstall but keep my data": per-game settings, GUI settings and the compiled game cache.
        static readonly string[] KeptEntries = { "config", "cache", "GuiConfigs" };

        public static void RemoveLinks(string emu)
        {
            if (!Directory.Exists(emu)) return;
            // Links point into the user's own RPCS3 (firmware, saves). Remove the link itself, never its contents.
            foreach (string d in Directory.GetDirectories(emu))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, false);
            foreach (string d in Directory.GetDirectories(emu))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) throw new IOException("A link could not be removed: " + d);
        }

        public static long KeptDataBytes(Settings s)
        {
            long total = 0;
            string emu = EmuDir(s);
            foreach (string k in KeptEntries)
            {
                string p = Path.Combine(emu, k);
                if (!Directory.Exists(p)) continue;
                try { foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories)) total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }

        // Removes everything the mod put on this PC except, when keepData is set, the data a reinstall can reuse.
        // Only files the launcher created are deleted: the user's own RPCS3, firmware, saves and game are never touched.
        public static void Uninstall(Settings s, bool keepData)
        {
            foreach (string ch in new[] { "stable", "beta" }) UninstallCopy(EmuDir(s, ch), keepData);
            FinishUninstall(s, keepData);
        }

        static void UninstallCopy(string emu, bool keepData)
        {
            RemoveLinks(emu);
            if (Directory.Exists(emu))
            {
                foreach (string d in Directory.GetDirectories(emu))
                {
                    if (keepData && Array.IndexOf(KeptEntries, Path.GetFileName(d)) >= 0)
                    {
                        if (Path.GetFileName(d) == "GuiConfigs")
                            foreach (string f in Directory.GetFiles(d)) if (!f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) File.Delete(f);
                        continue;
                    }
                    Directory.Delete(d, true);
                }
                foreach (string f in Directory.GetFiles(emu)) File.Delete(f);
                if (!keepData) Directory.Delete(emu, false);
            }
        }

        static void FinishUninstall(Settings s, bool keepData)
        {
            try { File.Delete(DesktopLink); } catch { }
            try { File.Delete(StartLink); } catch { }
            UnregisterApp();
            CleanTemp();

            if (keepData)
            {
                s.Uninstalled = true;
                s.InstalledVersion = null;
                s.Installs = new Dictionary<string, string>();
                s.Save();
            }
            else
            {
                try { File.Delete(Path.Combine(s.InstallDir, MarkerName)); } catch { }
                try { Directory.Delete(Settings.Folder, true); } catch { }
            }
        }

        // Leftovers from downloads and updates in %TEMP% (all named OrderUpVR_*).
        public static void CleanTemp()
        {
            try
            {
                string tmp = Path.GetTempPath();
                foreach (string f in Directory.GetFiles(tmp, "OrderUpVR_*")) { try { File.Delete(f); } catch { } }
                foreach (string d in Directory.GetDirectories(tmp, "OrderUpVR_*")) { try { Directory.Delete(d, true); } catch { } }
            }
            catch { }
        }

        // ----------------------------------------------------------------- Windows "Installed apps" entry
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OrderUpVR";
        public const string MarkerName = ".orderupvr";
        public const string RepoUrl = "https://github.com/ahmadhamed67/OrderUpVR";

        public static void RegisterApp(Settings s)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    string exe = LauncherPath(s);
                    k.SetValue("DisplayName", "Order Up VR");
                    k.SetValue("DisplayVersion", string.IsNullOrEmpty(s.InstalledVersion) ? LauncherVersion : s.InstalledVersion);
                    k.SetValue("Publisher", "Order Up VR project");
                    k.SetValue("DisplayIcon", exe + ",0");
                    k.SetValue("InstallLocation", s.InstallDir);
                    k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                    k.SetValue("URLInfoAbout", RepoUrl);
                    k.SetValue("HelpLink", RepoUrl + "/issues");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", 220 * 1024, RegistryValueKind.DWord);
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                }
            }
            catch { }
        }

        public static void UnregisterApp()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        }

        // A non-empty folder that is not ours gets an OrderUpVR subfolder, so uninstall never has to touch other files.
        public static string SafeInstallDir(string chosen)
        {
            string full = Path.GetFullPath(chosen).TrimEnd('\\');
            if (!Directory.Exists(full) || File.Exists(Path.Combine(full, MarkerName))) return full;
            if (Directory.GetFileSystemEntries(full).Length == 0) return full;
            return Path.Combine(full, "OrderUpVR");
        }

        // Every OpenXR runtime installed on this PC (manifest paths)
        public static List<string> AvailableOpenXrRuntimes()
        {
            var list = new List<string>();
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1\AvailableRuntimes"))
                    if (k != null) foreach (string n in k.GetValueNames()) if (File.Exists(n)) list.Add(n);
            }
            catch { }
            // Known places, in case a runtime did not list itself
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (string c in new[] {
                Path.Combine(pf, @"Oculus\Support\oculus-runtime\oculus_openxr_64.json"),
                Path.Combine(pf86, @"Steam\steamapps\common\SteamVR\steamxr_win64.json"),
                Path.Combine(pf, @"Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr.json") })
                if (File.Exists(c) && !list.Exists(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) list.Add(c);
            return list;
        }

        // The VR app that is running right now (its OpenXR manifest), or null
        public static string RunningOpenXrRuntime()
        {
            string want = null;
            try
            {
                if (Process.GetProcessesByName("vrserver").Length > 0) want = "SteamVR";
                else if (Process.GetProcessesByName("VirtualDesktop.Streamer").Length > 0) want = "Virtual Desktop";
                else if (Process.GetProcessesByName("OVRServer_x64").Length > 0) want = "Meta Horizon Link";
            }
            catch { }
            if (want == null) return null;
            foreach (string m in AvailableOpenXrRuntimes()) if (RuntimeName(m) == want) return m;
            return null;
        }

        // Makes a runtime the default OpenXR runtime (Windows asks for permission: it is a machine setting)
        public static bool SetActiveOpenXrRuntime(string manifestPath)
        {
            try
            {
                var psi = new ProcessStartInfo("reg.exe", @"add ""HKLM\SOFTWARE\Khronos\OpenXR\1"" /v ActiveRuntime /t REG_SZ /d """ + manifestPath + @""" /f /reg:64");
                psi.Verb = "runas"; psi.UseShellExecute = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi)) { p.WaitForExit(15000); }
                return string.Equals(ActiveOpenXrRuntime(), manifestPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string RuntimeName(string manifestPath)
        {
            if (string.IsNullOrEmpty(manifestPath)) return null;
            string p = manifestPath.ToLowerInvariant();
            if (p.Contains("oculus") || p.Contains("meta horizon") || p.Contains("metahorizon")) return "Meta Horizon Link";
            if (p.Contains("steamvr") || p.Contains("steam")) return "SteamVR";
            if (p.Contains("virtualdesktop") || p.Contains("virtual desktop")) return "Virtual Desktop";
            if (p.Contains("mixedreality") || p.Contains("wmr")) return "Windows Mixed Reality";
            if (p.Contains("pico")) return "PICO Connect";
            if (p.Contains("simulator")) return "Meta XR Simulator";
            if (p.Contains("vive")) return "VIVE Hub";
            if (p.Contains("alvr")) return "ALVR";
            return Path.GetFileNameWithoutExtension(manifestPath);
        }
    }

    static class WinTrust
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfo { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct Data
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice;
            public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction;
            public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref Data data);

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        // 0 = the file's Authenticode signature is valid and trusted.
        public static int Verify(string path)
        {
            var fi = new FileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(FileInfo)), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var d = new Data { cbStruct = (uint)Marshal.SizeOf(typeof(Data)), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0, dwUnionChoice = 1 /* file */, pFile = pFile, dwStateAction = 0, dwProvFlags = 0x40 /* safer */ };
                return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref d);
            }
            finally { Marshal.FreeHGlobal(pFile); }
        }
    }

    static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Visible top-level windows of a process: handle -> title.
        public static List<KeyValuePair<IntPtr, string>> WindowsOf(int pid)
        {
            var list = new List<KeyValuePair<IntPtr, string>>();
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                uint owner;
                GetWindowThreadProcessId(h, out owner);
                if (owner == (uint)pid && IsWindowVisible(h))
                {
                    var sb = new StringBuilder(512);
                    GetWindowText(h, sb, sb.Capacity);
                    list.Add(new KeyValuePair<IntPtr, string>(h, sb.ToString()));
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // Dark title bar area and rounded corners (Windows 11; ignored elsewhere).
        public static void StyleWindow(IntPtr hwnd)
        {
            try
            {
                int on = 1; DwmSetWindowAttribute(hwnd, 20, ref on, 4);      // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            }
            catch { }
        }

        // ------------------------------------------------------------------ modern folder picker (IFileOpenDialog)
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr f);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr p, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        // Returns the chosen folder, or null if cancelled.
        public static string PickFolder(IntPtr owner, string title, string start)
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogCom();
            try
            {
                dlg.SetOptions(0x20 | 0x40 | 0x800); // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    try
                    {
                        Guid iid = typeof(IShellItem).GUID;
                        IShellItem item;
                        SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out item);
                        dlg.SetFolder(item);
                    }
                    catch { }
                }
                if (dlg.Show(owner) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(0x80058000, out path); // SIGDN_FILESYSPATH
                return path;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }
    }
}
