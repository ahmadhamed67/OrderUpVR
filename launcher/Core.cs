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
        public bool NoAutoUpdate { get; set; }       // default false = check automatically
        public bool KeepLauncherOpen { get; set; }   // default false = minimize while playing
        public bool Uninstalled { get; set; }        // removed with "keep my data"; setup offers to reuse it

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
            string emu = EmuDir(s);
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

            try { File.Delete(DesktopLink); } catch { }
            try { File.Delete(StartLink); } catch { }
            UnregisterApp();
            CleanTemp();

            if (keepData)
            {
                s.Uninstalled = true;
                s.InstalledVersion = null;
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
