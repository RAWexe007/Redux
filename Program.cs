// Redux by Huighe v9 - real Windows program (.exe). Built and owned by Huighe.
// - true stretch, match res / default res, restore
// - settings are saved on THIS device only: %LOCALAPPDATA%\Redux\settings.json
// - Display settings: saturation, contrast, exposure and temperature (blue <-> orange) sliders, all done with the Windows Magnification API
//   (built into Windows, no NVIDIA / no other software)
// Written in plain C# 5 so it also builds with the compiler that ships with Windows (see build.cmd).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("Redux by Huighe")]
[assembly: AssemblyProduct("Redux")]
[assembly: AssemblyCompany("Huighe")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Huighe. All rights reserved.")]
[assembly: AssemblyVersion("9.0.0.0")]

namespace Redux
{
    // ---------------------------------------------------------------- paths + log + settings
    static class Paths
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Redux");
        // settings folder of the earlier "Valorant Res Changer" builds - read once so nobody loses their saved values
        public static readonly string OldDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValorantRes");
        public static string SettingsFile { get { return Path.Combine(Dir, "settings.json"); } }
        public static string LogFile { get { return Path.Combine(Dir, "debug.log"); } }
    }

    static class Log
    {
        static readonly object L = new object();
        static string path;
        public static void Init()
        {
            try
            {
                Directory.CreateDirectory(Paths.Dir);
                path = Paths.LogFile;
                File.WriteAllText(path, "=== start " + DateTime.Now + Environment.NewLine);   // fresh file every launch
            }
            catch { }
        }
        public static void W(string m)
        {
            try { lock (L) { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + m + Environment.NewLine); } }
            catch { }
        }
    }

    // Saved on this device only (not next to the exe), so every PC keeps its own settings.
    class Settings
    {
        public string defaultRes = "";
        public string matchRes = "1440x1080";
        public bool waitW = true;
        public int saturation = 100;      // percent, 100 = normal
        public int contrast = 100;        // percent, 100 = normal
        public int exposure = 100;        // percent, 100 = normal
        public int temperature = 0;       // -100 (cool / blue) .. +100 (warm / orange), 0 = normal

        public static Settings Load()
        {
            try
            {
                string file = Paths.SettingsFile;
                if (!File.Exists(file)) file = Path.Combine(Paths.OldDir, "settings.json");
                if (File.Exists(file))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    Settings s = js.Deserialize<Settings>(File.ReadAllText(file));
                    if (s != null)
                    {
                        s.saturation = Fx.Clamp(s.saturation, Fx.SatMin, Fx.Max);
                        s.contrast = Fx.Clamp(s.contrast, Fx.ConMin, Fx.Max);
                        s.exposure = Fx.Clamp(s.exposure, Fx.ExpMin, Fx.Max);
                        s.temperature = Fx.Clamp(s.temperature, Fx.TempMin, Fx.TempMax);
                        if (s.defaultRes == null) s.defaultRes = "";
                        if (s.matchRes == null) s.matchRes = "1440x1080";
                        return s;
                    }
                }
            }
            catch (Exception e) { Log.W("settings load error: " + e.Message); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.Dir);
                string tmp = Paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
                if (File.Exists(Paths.SettingsFile)) File.Delete(Paths.SettingsFile);
                File.Move(tmp, Paths.SettingsFile);
            }
            catch (Exception e) { Log.W("settings save error: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- display (same logic as the old script)
    static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplaySettingsEx(string dev, int mode, ref DEVMODE dm, int flags);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool ShutdownBlockReasonCreate(IntPtr h, string reason);
        [DllImport("user32.dll")] public static extern bool ShutdownBlockReasonDestroy(IntPtr h);

        static DEVMODE Current()
        {
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            EnumDisplaySettings(null, -1, ref dm);
            return dm;
        }
        public static string Cur() { DEVMODE dm = Current(); return dm.dmPelsWidth + "x" + dm.dmPelsHeight; }

        // All resolutions the graphics driver knows (EDS_RAWMODE = also custom modes). Sorted big -> small.
        public static string[] Modes()
        {
            List<int[]> list = new List<int[]>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            for (int i = 0; i < 5000; i++)
            {
                if (!EnumDisplaySettingsEx(null, i, ref dm, 2)) break;
                if (dm.dmBitsPerPel < 32 || (dm.dmDisplayFlags & 2) != 0) continue;
                string k = dm.dmPelsWidth + "x" + dm.dmPelsHeight;
                if (!seen.ContainsKey(k)) { seen[k] = true; list.Add(new int[] { dm.dmPelsWidth, dm.dmPelsHeight }); }
            }
            list.Sort(delegate (int[] a, int[] b) { if (a[0] != b[0]) return b[0].CompareTo(a[0]); return b[1].CompareTo(a[1]); });
            string[] res = new string[list.Count];
            for (int i = 0; i < res.Length; i++) res[i] = list[i][0] + "x" + list[i][1];
            return res;
        }

        static bool FindMode(int w, int h, int hz, out DEVMODE best)
        {
            best = new DEVMODE();
            bool found = false; int bestScore = -1;
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            for (int i = 0; i < 5000; i++)
            {
                if (!EnumDisplaySettingsEx(null, i, ref dm, 2)) break;
                if (dm.dmPelsWidth != w || dm.dmPelsHeight != h || dm.dmBitsPerPel < 32 || (dm.dmDisplayFlags & 2) != 0) continue;
                int score = (dm.dmDisplayFrequency == hz) ? 100000 : dm.dmDisplayFrequency;
                if (score > bestScore) { bestScore = score; best = dm; found = true; }
            }
            return found;
        }

        // Asks for full-screen STRETCH first; if the driver refuses, retries without it,
        // and finally with the driver's own mode entry for that size.
        public static int Set(int w, int h, out bool stretched)
        {
            stretched = false;
            DEVMODE dm = Current();
            dm.dmPelsWidth = w; dm.dmPelsHeight = h;
            dm.dmDisplayFixedOutput = 1;                                // DMDFO_STRETCH
            dm.dmFields = 0x80000 | 0x100000 | 0x400000 | 0x20000000;   // width | height | frequency | fixed-output
            int rc = ChangeDisplaySettings(ref dm, 0);
            if (rc == 0) { stretched = true; return 0; }
            DEVMODE d2 = Current();
            d2.dmPelsWidth = w; d2.dmPelsHeight = h;
            d2.dmFields = 0x80000 | 0x100000 | 0x400000;
            rc = ChangeDisplaySettings(ref d2, 0);
            if (rc == 0) return 0;
            DEVMODE m;
            if (FindMode(w, h, Current().dmDisplayFrequency, out m))
            {
                m.dmDisplayFixedOutput = 1;
                m.dmFields = m.dmFields | 0x20000000;
                rc = ChangeDisplaySettings(ref m, 0);
                if (rc == 0) { stretched = true; return 0; }
                m.dmFields = m.dmFields & ~0x20000000;
                rc = ChangeDisplaySettings(ref m, 0);
            }
            return rc;
        }

        public static bool WDown() { return (GetAsyncKeyState(0x57) & 0x8000) != 0; }   // read-only key state
    }

    // ---------------------------------------------------------------- display settings (our own, Windows Magnification API)
    // The Magnification API is part of Windows itself. It lets a program give the DWM (the part of Windows that draws
    // the screen) a 5x5 colour matrix. A gamma ramp can only change each colour on its own; a matrix can MIX colours
    // (saturation) and also scale and shift them (exposure, contrast). Saturation, contrast and exposure are all folded
    // into ONE matrix, so they work together. It does not touch the monitor devices, so it does not care that they are disabled.
    static class Fx
    {
        public const int Max = 300;        // slider top for all three, percent
        public const int SatMin = 0;       // 0% saturation = black and white
        public const int ConMin = 25;      // lower limits keep the screen readable
        public const int ExpMin = 25;
        public const int TempMin = -100;   // full cool (blue)
        public const int TempMax = 100;    // full warm (orange)

        [StructLayout(LayoutKind.Sequential)]
        struct MAGCOLOREFFECT { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] transform; }
        [DllImport("Magnification.dll", SetLastError = true)] static extern bool MagInitialize();
        [DllImport("Magnification.dll", SetLastError = true)] static extern bool MagUninitialize();
        [DllImport("Magnification.dll", SetLastError = true)] static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT e);

        static bool inited;
        public static string Last = "";       // last settings we successfully applied ("sat|con|exp|temp")
        public static bool Failed;            // true once Windows refused
        static string lastErr = "";

        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static string Key(int sat, int con, int exp, int temp) { return sat + "|" + con + "|" + exp + "|" + temp; }

        // Row-vector convention: out = [R G B A 1] x M. Rec.709 luma weights (what Windows uses for sRGB).
        //   1) saturation: blend each pixel between its grey (luma) and itself
        //   2) exposure:   multiply by the exposure percent (100% = unchanged)
        //   3) contrast:   stretch around mid-grey: out = (in - 0.5) * contrast + 0.5
        // Steps 2 and 3 together are one multiply (exposure * contrast) plus one shift (0.5 * (1 - contrast)).
        //   4) temperature: last step, scales each OUTPUT channel on its own. Warm = more red, less blue (orange).
        //      Cool = more blue, less red. Green is only touched a little so the picture does not turn purple or green.
        public static float[] Matrix(int satPct, int conPct, int expPct, int tempPct)
        {
            double s = satPct / 100.0, c = conPct / 100.0, e = expPct / 100.0, t = tempPct / 100.0;
            double[] tg = new double[] { 1 + 0.30 * t, 1 - 0.06 * Math.Abs(t), 1 - 0.30 * t };   // R, G, B gain
            double[] luma = new double[] { 0.2126, 0.7152, 0.0722 };
            double gain = c * e, shift = 0.5 * (1 - c);
            float[] m = new float[25];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i * 5 + j] = (float)((((i == j) ? s : 0.0) + (1 - s) * luma[i]) * gain * tg[j]);
            for (int j = 0; j < 3; j++) m[4 * 5 + j] = (float)(shift * tg[j]);
            m[3 * 5 + 3] = 1f; m[4 * 5 + 4] = 1f;
            return m;
        }

        public static bool Apply(int sat, int con, int exp, int temp)
        {
            try
            {
                if (!inited)
                {
                    inited = MagInitialize();
                    if (!inited) { Fail("MagInitialize failed, win32 error " + Marshal.GetLastWin32Error()); return false; }
                    Log.W("display engine started");
                }
                MAGCOLOREFFECT e = new MAGCOLOREFFECT();
                e.transform = Matrix(sat, con, exp, temp);
                if (!MagSetFullscreenColorEffect(ref e)) { Fail("MagSetFullscreenColorEffect failed, win32 error " + Marshal.GetLastWin32Error()); return false; }
                string k = Key(sat, con, exp, temp);
                if (Last != k) Log.W("display set: saturation " + sat + "%, contrast " + con + "%, exposure " + exp + "%, temperature " + temp);
                Last = k; Failed = false; lastErr = "";
                return true;
            }
            catch (Exception ex) { Fail("display exception: " + ex.Message); return false; }
        }

        static void Fail(string msg)
        {
            Failed = true;
            if (lastErr != msg) { Log.W(msg); lastErr = msg; }
        }

        public static void Shutdown()
        {
            try
            {
                if (inited) { Apply(100, 100, 100, 0); MagUninitialize(); inited = false; Last = ""; Log.W("display engine stopped (back to normal)"); }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- monitors (same as Device Manager > Monitors > Disable device)
    static class Monitors
    {
        static Guid MonitorClass = new Guid("4d36e96e-e325-11ce-bfc1-08002be10318");
        const int DIGCF_PRESENT = 2;
        const int DIF_PROPERTYCHANGE = 0x12;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        struct SP_CLASSINSTALL_HEADER { public int cbSize; public int InstallFunction; }
        [StructLayout(LayoutKind.Sequential)]
        struct SP_PROPCHANGE_PARAMS { public SP_CLASSINSTALL_HEADER ClassInstallHeader; public int StateChange; public int Scope; public int HwProfile; }

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiSetClassInstallParams(IntPtr set, ref SP_DEVINFO_DATA d, ref SP_PROPCHANGE_PARAMS p, int size);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiCallClassInstaller(int fn, IntPtr set, ref SP_DEVINFO_DATA d);
        [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Auto)] static extern int CM_Get_Device_ID(int devInst, StringBuilder buf, int len, int flags);

        static string DevId(int devInst)
        {
            StringBuilder sb = new StringBuilder(512);
            if (CM_Get_Device_ID(devInst, sb, sb.Capacity, 0) != 0) return "";
            return sb.ToString();
        }

        static bool SetState(IntPtr set, ref SP_DEVINFO_DATA d, bool enable)
        {
            SP_PROPCHANGE_PARAMS p = new SP_PROPCHANGE_PARAMS();
            p.ClassInstallHeader.cbSize = Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER));
            p.ClassInstallHeader.InstallFunction = DIF_PROPERTYCHANGE;
            p.StateChange = enable ? 1 : 2;     // DICS_ENABLE / DICS_DISABLE
            p.Scope = 1;                        // DICS_FLAG_GLOBAL
            p.HwProfile = 0;
            if (!SetupDiSetClassInstallParams(set, ref d, ref p, Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS)))) return false;
            return SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref d);
        }

        // Walks the present monitor devices. mode: 0 = only count, 1 = disable the enabled ones, 2 = enable the disabled ones.
        // The 'DEFAULT_MONITOR' entry is a ghost that can never be enabled, so it is skipped.
        static int Walk(int mode, out int on, out int off, out int failed)
        {
            on = 0; off = 0; failed = 0; int changed = 0;
            Guid g = MonitorClass;
            IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, DIGCF_PRESENT);
            if (set == new IntPtr(-1)) { Log.W("monitor query error, win32 " + Marshal.GetLastWin32Error()); failed = 1; return 0; }
            try
            {
                for (int i = 0; ; i++)
                {
                    SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
                    d.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(set, i, ref d)) break;
                    string id = DevId(d.DevInst);
                    if (id.ToUpperInvariant().Contains("DEFAULT_MONITOR")) continue;
                    int status, problem;
                    if (CM_Get_DevNode_Status(out status, out problem, d.DevInst, 0) != 0) continue;
                    bool disabled = (problem == 22);          // CM_PROB_DISABLED
                    if (disabled) off++; else on++;
                    if (mode == 0) continue;
                    bool wantEnable = (mode == 2);
                    if (wantEnable != disabled) continue;      // already in the wanted state
                    if (SetState(set, ref d, wantEnable))
                    {
                        changed++;
                        if (wantEnable) { off--; on++; } else { on--; off++; }
                    }
                    else { failed++; Log.W("monitor " + (wantEnable ? "enable" : "disable") + " FAILED [" + id + "] win32 " + Marshal.GetLastWin32Error()); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return changed;
        }

        // Safety net: if the direct way fails, use PowerShell (the way the old script did it).
        static void PsFallback(bool enable)
        {
            try
            {
                string cmd = enable
                    ? "Get-PnpDevice -Class Monitor | Where-Object { $_.Status -ne 'OK' -and $_.InstanceId -notlike '*DEFAULT_MONITOR*' } | Enable-PnpDevice -Confirm:$false"
                    : "Get-PnpDevice -Class Monitor | Where-Object { $_.Status -eq 'OK' } | Disable-PnpDevice -Confirm:$false";
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi)) { p.WaitForExit(20000); }
                Log.W("PowerShell fallback ran (" + (enable ? "enable" : "disable") + ")");
            }
            catch (Exception e) { Log.W("PowerShell fallback error: " + e.Message); }
        }

        public static int StillOn = -1;

        // returns how many monitors were really switched off now
        public static int DisableAll()
        {
            int on, off, failed;
            int changed = Walk(1, out on, out off, out failed);
            if (failed > 0) { PsFallback(false); changed = 0; Walk(0, out on, out off, out failed); }
            StillOn = on;
            if (changed > 0) { Log.W("disabled " + changed + " monitor device(s); still enabled: " + on); Thread.Sleep(1500); }
            return changed;
        }

        public static void EnableAll()
        {
            int on, off, failed;
            Walk(2, out on, out off, out failed);
            if (failed > 0 || off > 0) { PsFallback(true); Walk(0, out on, out off, out failed); }
            StillOn = on;
            Log.W("monitors enabled (still disabled: " + off + ")");
        }

        public static bool AnyDisabled()
        {
            int on, off, failed;
            Walk(0, out on, out off, out failed);
            return off > 0;
        }
    }

    // ---------------------------------------------------------------- Valorant state (local Riot Client, read only)
    static class Riot
    {
        static string lastLog = "";

        static object GetJson(string url, string auth)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.Headers.Add("Authorization", auth);
            r.Timeout = 2500;
            r.ServerCertificateValidationCallback = delegate { return true; };   // local Riot Client uses a self-made certificate; only 127.0.0.1 is called
            using (WebResponse resp = r.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                return js.DeserializeObject(sr.ReadToEnd());
            }
        }

        static object Dig(object o, params string[] keys)
        {
            foreach (string k in keys)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null || !d.ContainsKey(k)) return null;
                o = d[k];
            }
            return o;
        }
        static string S(object o) { return o == null ? "" : o.ToString(); }

        // returns: closed | lobby | match | nodata
        public static string State(out string why)
        {
            Process[] ps = Process.GetProcessesByName("VALORANT-Win64-Shipping");
            bool running = ps.Length > 0;
            foreach (Process p in ps) p.Dispose();
            if (!running) { why = "no game process"; return "closed"; }
            try
            {
                string lockPath = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), @"Riot Games\Riot Client\Config\lockfile");
                string text;
                using (FileStream fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs)) text = sr.ReadToEnd();
                string[] p = text.Split(new char[] { ':' });
                string auth = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + p[3]));
                string baseUrl = "https://127.0.0.1:" + p[2];
                object me = GetJson(baseUrl + "/chat/v1/session", auth);
                object pres = GetJson(baseUrl + "/chat/v4/presences", auth);
                string myPuuid = S(Dig(me, "puuid"));
                object list = Dig(pres, "presences");
                string priv64 = null;
                if (list is IEnumerable)
                {
                    foreach (object item in (IEnumerable)list)
                    {
                        if (S(Dig(item, "puuid")) == myPuuid && S(Dig(item, "product")) == "valorant")
                        {
                            string pv = S(Dig(item, "private"));
                            if (pv.Length > 0) { priv64 = pv; break; }
                        }
                    }
                }
                if (priv64 == null) { why = "api: no presence yet"; return "nodata"; }
                JavaScriptSerializer js = new JavaScriptSerializer();
                object priv = js.DeserializeObject(Encoding.UTF8.GetString(Convert.FromBase64String(priv64)));
                string loop = S(Dig(priv, "matchPresenceData", "sessionLoopState"));
                if (loop.Length == 0) loop = S(Dig(priv, "partyPresenceData", "partyOwnerSessionLoopState"));
                string flow = S(Dig(priv, "provisioningFlow"));
                why = "loop=" + loop + " flow=" + flow;
                if (why != lastLog)
                {
                    Log.W("api: " + why + " map=" + S(Dig(priv, "matchPresenceData", "matchMap")) + " queue=" + S(Dig(priv, "queueId")));
                    lastLog = why;
                }
                if (loop == "INGAME") return "match";
                if (loop.Length > 0) return "lobby";
                return "nodata";
            }
            catch (Exception e)
            {
                why = "api error";
                if (lastLog != "err") { Log.W("api error: " + e.Message); lastLog = "err"; }
                return "nodata";
            }
        }
    }

    // ---------------------------------------------------------------- dark theme + our own controls
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(13, 14, 18);        // same as the logo background
        public static readonly Color Panel = Color.FromArgb(27, 29, 36);
        public static readonly Color PanelHot = Color.FromArgb(38, 41, 50);
        public static readonly Color Border = Color.FromArgb(52, 55, 66);
        public static readonly Color Text = Color.FromArgb(236, 238, 242);
        public static readonly Color Muted = Color.FromArgb(139, 145, 158);
        public static readonly Color Accent = Color.FromArgb(226, 36, 48);   // logo red
        public static readonly Color AccentHot = Color.FromArgb(244, 62, 74);

        public static void Button(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false; b.Cursor = Cursors.Hand;
            b.BackColor = primary ? Accent : Panel; b.ForeColor = Color.White;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Accent : Border;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentHot : PanelHot;
            b.FlatAppearance.MouseDownBackColor = primary ? Accent : Border;
        }

        public static void Combo(ComboBox c)
        {
            c.FlatStyle = FlatStyle.Flat; c.BackColor = Panel; c.ForeColor = Text;
            c.DrawMode = DrawMode.OwnerDrawFixed; c.ItemHeight = 18;
            c.DrawItem += delegate (object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (SolidBrush bg = new SolidBrush(sel ? Accent : Panel)) e.Graphics.FillRectangle(bg, e.Bounds);
                TextRenderer.DrawText(e.Graphics, ((ComboBox)s).Items[e.Index].ToString(), e.Font, e.Bounds, Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            };
        }
    }

    // Slider drawn by us, so it keeps the dark theme and a fixed size (the Windows trackbar has its own height and ticks).
    class DarkSlider : Control
    {
        int min = 0, max = 100, val = 0; bool drag;
        public int Neutral = 100;        // value that gets the small 'normal' mark
        public bool Gradient;            // true = blue -> orange track (temperature), no red fill
        public event EventHandler ValueChanged;
        public int Minimum { get { return min; } set { min = value; Invalidate(); } }
        public int Maximum { get { return max; } set { max = value; Invalidate(); } }
        public int Value
        {
            get { return val; }
            set
            {
                int v = value < min ? min : (value > max ? max : value);
                if (v == val) return;
                val = v; Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
        public DarkSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand; BackColor = Theme.Bg;
        }
        const int Pad = 9;   // half the thumb width
        int XOf(int v) { return Pad + (int)Math.Round((Width - 2 * Pad) * (double)(v - min) / Math.Max(1, max - min)); }
        void FromX(int x)
        {
            double f = (double)(x - Pad) / Math.Max(1, Width - 2 * Pad);
            if (f < 0) f = 0; if (f > 1) f = 1;
            Value = min + (int)Math.Round(f * (max - min));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            int cy = Height / 2, x = XOf(val), x100 = XOf(Neutral);
            if (Gradient)
            {
                Rectangle tr = new Rectangle(Pad, cy - 2, Math.Max(2, Width - 2 * Pad), 4);
                using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(tr, Color.FromArgb(70, 140, 255), Color.FromArgb(255, 150, 40), 0f))
                    g.FillRectangle(b, tr);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Theme.Border)) g.FillRectangle(b, Pad, cy - 2, Width - 2 * Pad, 4);
                using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, Pad, cy - 2, Math.Max(0, x - Pad), 4);
            }
            using (Pen p = new Pen(Theme.Muted)) g.DrawLine(p, x100, cy - 7, x100, cy - 4);     // small mark at 100% (normal)
            Rectangle t = new Rectangle(x - 8, cy - 8, 16, 16);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, t);
            using (Pen p = new Pen(Focused ? Theme.AccentHot : Theme.Accent, 2f)) g.DrawEllipse(p, t.X + 1, t.Y + 1, t.Width - 2, t.Height - 2);
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); drag = true; Capture = true; FromX(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) FromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Capture = false; }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Value += (e.Delta > 0 ? 5 : -5); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= 5;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += 5;
            else if (e.KeyCode == Keys.PageDown) Value -= 25;
            else if (e.KeyCode == Keys.PageUp) Value += 25;
            else if (e.KeyCode == Keys.Home) Value = min;
            else if (e.KeyCode == Keys.End) Value = max;
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    class DarkCheck : Control
    {
        bool on;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return on; } set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        public DarkCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand; BackColor = Theme.Bg;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(Theme.Bg);
            Rectangle box = new Rectangle(1, (Height - 16) / 2, 16, 16);
            using (SolidBrush b = new SolidBrush(on ? Theme.Accent : Theme.Panel)) g.FillRectangle(b, box);
            using (Pen p = new Pen(Focused ? Theme.AccentHot : (on ? Theme.Accent : Theme.Muted))) g.DrawRectangle(p, box);
            if (on) using (Pen p = new Pen(Color.White, 2f)) g.DrawLines(p, new Point[] { new Point(box.X + 3, box.Y + 8), new Point(box.X + 7, box.Y + 12), new Point(box.X + 13, box.Y + 4) });
            TextRenderer.DrawText(g, Text, Font, new Rectangle(26, 0, Width - 26, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Checked = !on; }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) Checked = !on; }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // ---------------------------------------------------------------- window
    class MainForm : Form
    {
        ComboBox combo, matchBox;
        Button bRefresh, bAuto, bStop, bMatch, bLobby, bRestore, bPreview, bSatReset, bConReset, bExpReset, bTempReset;
        DarkCheck chkW;
        DarkSlider satBar, conBar, expBar, tempBar;
        Label brand, satName, conName, expName, tempName;
        PictureBox logoBox;
        Label status, satLabel, conLabel, expLabel, tempLabel;
        WinTimer timer, wTimer, fxTimer;
        Settings settings;

        bool loading, dirty;
        bool monDisabledByApp, resAppChanged;
        bool auto, firstTick, busy, wPressed, resDone, manualMatch;
        string state = "lobby", pending = "", resWanted = "", why = "";
        int pendN, tick, resTries, fxTick;
        DateTime previewUntil = DateTime.MinValue;

        public MainForm()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Redux by Huighe";
            ClientSize = new Size(380, 824);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = SystemFonts.MessageBoxFont;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font small = new Font(Font.FontFamily, 8.25f, FontStyle.Regular);

            // ---- header: logo (its background colour is the window colour, so it blends in)
            logoBox = (PictureBox)Add(new PictureBox(), 10, 6, 360, 72, "");
            logoBox.SizeMode = PictureBoxSizeMode.Zoom; logoBox.BackColor = Theme.Bg;
            try
            {
                Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
                if (rs != null) logoBox.Image = Image.FromStream(rs);
            }
            catch { }

            // ---- resolution
            Add(new Label(), 16, 97, 130, 20, "Lobby resolution");
            combo = (ComboBox)Add(new ComboBox(), 150, 92, 140, 24, "");
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            bRefresh = (Button)Add(new Button(), 296, 91, 68, 26, "Refresh");
            Add(new Label(), 16, 133, 130, 20, "Match resolution");
            matchBox = (ComboBox)Add(new ComboBox(), 150, 128, 214, 24, "");
            matchBox.DropDownStyle = ComboBoxStyle.DropDown;      // pick from the list or type your own WxH
            chkW = (DarkCheck)Add(new DarkCheck(), 16, 162, 348, 22, "During a match, wait for the W key before applying");
            chkW.Checked = true;
            Sep(192);

            // ---- display settings: four identical rows (name + value, slider, reset)
            satName = (Label)Add(new Label(), 16, 202, 200, 20, "Saturation");
            satLabel = (Label)Add(new Label(), 216, 202, 148, 20, "100%");
            satBar = (DarkSlider)Add(new DarkSlider(), 16, 224, 286, 24, "");
            bSatReset = (Button)Add(new Button(), 310, 222, 54, 26, "Reset");
            conName = (Label)Add(new Label(), 16, 256, 200, 20, "Contrast");
            conLabel = (Label)Add(new Label(), 216, 256, 148, 20, "100%");
            conBar = (DarkSlider)Add(new DarkSlider(), 16, 278, 286, 24, "");
            bConReset = (Button)Add(new Button(), 310, 276, 54, 26, "Reset");
            expName = (Label)Add(new Label(), 16, 310, 200, 20, "Exposure");
            expLabel = (Label)Add(new Label(), 216, 310, 148, 20, "100%");
            expBar = (DarkSlider)Add(new DarkSlider(), 16, 332, 286, 24, "");
            bExpReset = (Button)Add(new Button(), 310, 330, 54, 26, "Reset");
            tempName = (Label)Add(new Label(), 16, 364, 200, 20, "Temperature");
            tempLabel = (Label)Add(new Label(), 216, 364, 148, 20, "Neutral");
            tempBar = (DarkSlider)Add(new DarkSlider(), 16, 386, 286, 24, "");
            bTempReset = (Button)Add(new Button(), 310, 384, 54, 26, "Reset");
            satLabel.TextAlign = conLabel.TextAlign = expLabel.TextAlign = tempLabel.TextAlign = ContentAlignment.TopRight;
            SetupBar(satBar, Fx.SatMin); SetupBar(conBar, Fx.ConMin); SetupBar(expBar, Fx.ExpMin);
            tempBar.Minimum = Fx.TempMin; tempBar.Maximum = Fx.TempMax; tempBar.Neutral = 0; tempBar.Gradient = true; tempBar.Value = 0;
            bPreview = (Button)Add(new Button(), 16, 418, 348, 28, PreviewText);
            Label hint = (Label)Add(new Label(), 16, 452, 348, 16, "100% is the default (temperature: 0). Changes stay active only during a match.");
            hint.Font = small; hint.ForeColor = Theme.Muted;
            Sep(476);

            status = (Label)Add(new Label(), 16, 484, 348, 52, "Status: Idle");
            bAuto = (Button)Add(new Button(), 16, 542, 348, 32, "Start Auto Mode");
            bStop = (Button)Add(new Button(), 16, 578, 348, 28, "Stop Auto Mode");
            bMatch = (Button)Add(new Button(), 16, 612, 170, 28, "Apply Match Resolution");
            bLobby = (Button)Add(new Button(), 194, 612, 170, 28, "Apply Default Resolution");
            bRestore = (Button)Add(new Button(), 16, 648, 348, 38, "Restore Defaults (Monitors + Resolution)");
            Theme.Button(bPreview, false); Theme.Button(bRefresh, false); Theme.Button(bSatReset, false); Theme.Button(bConReset, false); Theme.Button(bExpReset, false); Theme.Button(bTempReset, false);
            Theme.Button(bAuto, false); Theme.Button(bStop, false); Theme.Button(bMatch, false); Theme.Button(bLobby, false); Theme.Button(bRestore, true);
            Theme.Combo(combo); Theme.Combo(matchBox);
            Label info = (Label)Add(new Label(), 16, 694, 348, 80,
                "Redux disables your monitor devices so Valorant can stretch any resolution without black bars. Match settings apply during a match; the default resolution applies in the lobby. Settings are stored on this device only. If you close Redux without restoring, it restores your monitors and default resolution automatically.");
            info.Font = small; info.ForeColor = Theme.Muted;
            Sep(780);

            // ---- footer: small, in its own strip below everything else, so it can never overlap the controls
            brand = (Label)Add(new Label(), 0, 786, 380, 16, "Redux by Huighe   ·   Built and owned by Huighe");
            brand.Font = small; brand.ForeColor = Theme.Text; brand.TextAlign = ContentAlignment.MiddleCenter;
            string lead = "© 2026 Huighe. All rights reserved.   ·   ";
            string linkText = "YouTube: @RAW.exeFPS";
            LinkLabel lk = new LinkLabel(); lk.Text = lead + linkText; lk.Font = small; lk.AutoSize = false;
            lk.TextAlign = ContentAlignment.MiddleCenter; lk.Location = new Point(0, 804); lk.Size = new Size(380, 16);
            lk.ForeColor = Theme.Muted; lk.LinkColor = Theme.AccentHot; lk.ActiveLinkColor = Color.White; lk.VisitedLinkColor = Theme.AccentHot;
            lk.LinkBehavior = LinkBehavior.HoverUnderline; lk.Links.Clear(); lk.Links.Add(lead.Length, linkText.Length);
            lk.LinkClicked += delegate
            {
                try { Process.Start("https://www.youtube.com/@RAW.exeFPS"); } catch (Exception ex) { Log.W("could not open link: " + ex.Message); }
            };
            Controls.Add(lk);

            // ---- load this device's settings, fill the resolution lists
            loading = true;
            settings = Settings.Load();
            string defWanted = Regex.IsMatch(settings.defaultRes, @"^\d+x\d+$") ? settings.defaultRes : null;
            string matchWanted = Regex.IsMatch(settings.matchRes, @"^\d+x\d+$") ? settings.matchRes : null;
            chkW.Checked = settings.waitW;
            satBar.Value = settings.saturation; conBar.Value = settings.contrast; expBar.Value = settings.exposure; tempBar.Value = settings.temperature;
            UpdateFxLabels();
            FillModes(defWanted, matchWanted);
            loading = false;
            if (!File.Exists(Paths.SettingsFile)) { dirty = true; }   // first run on this device -> write its own settings file

            combo.SelectedIndexChanged += delegate { MarkDirty(); };
            matchBox.TextChanged += delegate { MarkDirty(); };
            chkW.CheckedChanged += delegate { MarkDirty(); };
            satBar.ValueChanged += OnFxSlider; conBar.ValueChanged += OnFxSlider; expBar.ValueChanged += OnFxSlider; tempBar.ValueChanged += OnFxSlider;
            bPreview.Click += OnPreview;
            bSatReset.Click += delegate { satBar.Value = 100; };
            bConReset.Click += delegate { conBar.Value = 100; };
            bExpReset.Click += delegate { expBar.Value = 100; };
            bTempReset.Click += delegate { tempBar.Value = 0; };
            bRefresh.Click += delegate
            {
                string d = "" + combo.SelectedItem, m = matchBox.Text.Trim();
                loading = true; FillModes(d, m); loading = false;
                status.Text = "Resolution list refreshed (" + combo.Items.Count + " available).";
            };

            if (Monitors.AnyDisabled())
            {
                monDisabledByApp = true;
                Log.W("monitors were still disabled from last time");
                status.Text = "Monitors are still disabled from the previous session.\nSelect Restore Defaults, or start Auto Mode.";
            }

            timer = new WinTimer(); timer.Interval = 2000; timer.Tick += OnTick;
            wTimer = new WinTimer(); wTimer.Interval = 30; wTimer.Tick += OnWTick;
            fxTimer = new WinTimer(); fxTimer.Interval = 500; fxTimer.Tick += OnFxTick;
            fxTimer.Start();
            ResetAuto();

            bAuto.Click += OnStartAuto;
            bStop.Click += delegate
            {
                timer.Stop(); wTimer.Stop();
                manualMatch = MatchActive();       // keep things as they are right now
                auto = false; status.Text = "Status: Auto Mode stopped"; Log.W("auto mode stopped");
            };
            bMatch.Click += delegate { StopAuto(); manualMatch = true; DoResChange(matchBox.Text.Trim()); SyncFxNow(); status.Text = "Resolution: " + Display.Cur(); };
            bLobby.Click += delegate { StopAuto(); manualMatch = false; DoResChange("" + combo.SelectedItem); SyncFxNow(); status.Text = "Resolution: " + Display.Cur(); };
            bRestore.Click += delegate
            {
                status.Text = "Restoring defaults..."; Refresh();
                RestoreAll();
                SyncFxNow();
                status.Text = "Defaults restored. Resolution: " + Display.Cur();
            };
            FormClosing += OnClosing;
        }

        void Sep(int y)
        {
            Panel p = new Panel(); p.Location = new Point(16, y); p.Size = new Size(348, 1); p.BackColor = Theme.Border; Controls.Add(p);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4); } catch { }   // dark title bar
        }

        Control Add(Control c, int x, int y, int w, int h, string t)
        {
            c.Location = new Point(x, y); c.Size = new Size(w, h); c.Text = t; Controls.Add(c); return c;
        }

        void MarkDirty() { if (!loading) dirty = true; }

        void SaveSettings()
        {
            if (loading) return;
            settings.defaultRes = "" + combo.SelectedItem;
            settings.matchRes = matchBox.Text.Trim();
            settings.waitW = chkW.Checked;
            settings.saturation = satBar.Value; settings.contrast = conBar.Value; settings.exposure = expBar.Value; settings.temperature = tempBar.Value;
            settings.Save();
            dirty = false;
        }

        void FillModes(string defWanted, string matchWanted)
        {
            string[] modes = Display.Modes();
            if (modes.Length == 0) { modes = new string[] { "1920x1200", "1920x1080", "1440x1080" }; Log.W("could not read the resolution list, using a short built-in list"); }
            else Log.W("found " + modes.Length + " resolutions from the driver");
            if (string.IsNullOrEmpty(defWanted)) defWanted = Display.Cur();     // first run on this device: today's desktop res
            if (string.IsNullOrEmpty(matchWanted)) matchWanted = "1440x1080";
            combo.Items.Clear(); matchBox.Items.Clear();
            combo.Items.AddRange(modes); matchBox.Items.AddRange(modes);
            if (combo.Items.IndexOf(defWanted) < 0) combo.Items.Add(defWanted);   // keep a saved res even if the driver no longer lists it
            combo.SelectedItem = defWanted;
            matchBox.Text = matchWanted;
        }

        // ---------------- saturation
        bool MatchActive()
        {
            if (auto) return state == "match" && (wPressed || !chkW.Checked);
            return manualMatch;
        }

        // every slider row is built the same way, so all four behave exactly alike
        const string PreviewText = "Preview All Settings (4 seconds)";

        // shows saturation, contrast, exposure and temperature together for 4 seconds, then returns to normal (unless a match is active)
        void OnPreview(object s, EventArgs e)
        {
            previewUntil = DateTime.UtcNow.AddSeconds(4);
            PushFx(true);
            bPreview.Text = "Previewing...";
            Log.W("preview: all display settings for 4 seconds");
        }

        void SetupBar(DarkSlider t, int min)
        {
            t.Minimum = min; t.Maximum = Fx.Max; t.Value = 100;
        }

        void UpdateFxLabels()
        {
            satLabel.Text = satBar.Value + "%";
            conLabel.Text = conBar.Value + "%";
            expLabel.Text = expBar.Value + "%";
            int tv = tempBar.Value;
            tempLabel.Text = tv == 0 ? "Neutral" : (tv > 0 ? "Warm +" + tv : "Cool " + tv);
        }

        // applies the four slider values (active) or plain normal 100/100/100/0 (not active)
        void PushFx(bool active)
        {
            if (active) Fx.Apply(satBar.Value, conBar.Value, expBar.Value, tempBar.Value);
            else Fx.Apply(100, 100, 100, 0);
        }

        string WantFxKey() { return MatchActive() ? Fx.Key(satBar.Value, conBar.Value, expBar.Value, tempBar.Value) : Fx.Key(100, 100, 100, 0); }

        void OnFxSlider(object s, EventArgs e)
        {
            UpdateFxLabels();
            MarkDirty();
            previewUntil = DateTime.UtcNow.AddSeconds(4);     // show the change right away, even in the lobby
            PushFx(true);
            if (Fx.Failed) satLabel.Text += "  (not applied - see log)";
        }

        void SyncFxNow() { previewUntil = DateTime.MinValue; PushFx(MatchActive()); }

        void OnFxTick(object s, EventArgs e)
        {
            fxTick++;
            if (bPreview.Text != PreviewText && DateTime.UtcNow >= previewUntil) bPreview.Text = PreviewText;
            if (dirty) SaveSettings();
            if (DateTime.UtcNow < previewUntil) return;
            if (WantFxKey() != Fx.Last || fxTick % 20 == 0) PushFx(MatchActive());   // re-apply every ~10 s in case Windows reset it
        }

        // ---------------- resolution
        int ApplyRes(string res)
        {
            Match m = Regex.Match(res ?? "", @"^(\d+)x(\d+)$");
            if (!m.Success) { Log.W("bad res text '" + res + "'"); return -98; }
            bool st;
            int rc = Display.Set(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), out st);
            Log.W("set res " + res + " -> code " + rc + ", stretch=" + st + ", now=" + Display.Cur());
            if (rc == 0) resAppChanged = (res != "" + combo.SelectedItem);
            return rc;
        }

        int DoResChange(string res)
        {
            DisableMonitors();
            int rc = ApplyRes(res);
            Thread.Sleep(800);
            DisableMonitors();
            return rc;
        }

        void DisableMonitors() { if (Monitors.DisableAll() > 0) monDisabledByApp = true; }

        string WantResTarget()
        {
            if (state == "match" && (wPressed || !chkW.Checked)) return matchBox.Text.Trim();
            return "" + combo.SelectedItem;
        }

        void SyncRes()
        {
            string target = WantResTarget();
            if (target != resWanted) { resWanted = target; resDone = false; resTries = 0; }
            if (resDone) return;
            if (Display.Cur() == target) { resDone = true; return; }
            if (resTries >= 3) return;
            resTries++;
            if (DoResChange(target) == 0) resDone = true;
        }

        void RestoreAll()
        {
            timer.Stop(); wTimer.Stop(); auto = false; manualMatch = false;
            Monitors.EnableAll(); Thread.Sleep(2000);
            if (Display.Cur() != "" + combo.SelectedItem) ApplyRes("" + combo.SelectedItem);
            monDisabledByApp = false; resAppChanged = false;
            ResetAuto();
        }

        bool NeedsRestore() { return monDisabledByApp || resAppChanged || Monitors.AnyDisabled(); }

        void ResetAuto()
        {
            auto = false; firstTick = true; state = "lobby"; pending = ""; pendN = 0; tick = 0;
            resWanted = ""; resDone = false; resTries = 0; wPressed = false; busy = false;
        }

        void StopAuto() { timer.Stop(); wTimer.Stop(); auto = false; }

        // ---------------- auto mode
        void OnStartAuto(object s, EventArgs e)
        {
            timer.Stop(); ResetAuto(); auto = true; manualMatch = false;
            status.Text = "Starting Auto Mode..."; Refresh();
            Log.W("auto mode started (wait-for-W=" + chkW.Checked + ")");
            DisableMonitors();
            Thread.Sleep(1500);
            timer.Start(); wTimer.Start();
        }

        // The Riot Client call can take a moment, so it runs in the background; the window never freezes on it.
        void OnTick(object s, EventArgs e)
        {
            timer.Stop();
            if (!auto) return;
            if (busy) { timer.Start(); return; }
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string w = "poll error"; string raw = "nodata";
                try { raw = Riot.State(out w); }
                catch (Exception ex) { Log.W("poll error: " + ex.Message); raw = "nodata"; w = "poll error"; }
                try { BeginInvoke(new Action(delegate { AfterPoll(raw, w); })); }
                catch { busy = false; }
            });
        }

        void AfterPoll(string raw, string w)
        {
            try
            {
                if (!auto) return;
                why = w; tick++;
                if (raw == "nodata") raw = state;                       // no data -> keep last state
                if (raw == pending) pendN++; else { pending = raw; pendN = 1; }
                if (firstTick) { state = raw; firstTick = false; wPressed = false; Log.W("initial state " + raw + " (" + why + ")"); }
                else if (pendN >= 2 && raw != state) { Log.W("state " + state + " -> " + raw + " (" + why + ")"); state = raw; wPressed = false; }

                if (tick % 5 == 0) DisableMonitors();
                SyncRes();
                SyncFxNow2();

                string wait = (state == "match" && chkW.Checked && !wPressed) ? " (waiting for W key)" : "";
                status.Text = "Game state: " + char.ToUpper(state[0]) + state.Substring(1) + "\nResolution: " + Display.Cur() + wait + "\nMonitors still active: " + (Monitors.StillOn < 0 ? "-" : "" + Monitors.StillOn);
            }
            catch (Exception ex) { Log.W("tick error: " + ex.Message); }
            finally { busy = false; if (auto) timer.Start(); }
        }

        void SyncFxNow2() { if (DateTime.UtcNow >= previewUntil) PushFx(MatchActive()); }

        // fast poll: only while a match is running and we are waiting for W
        void OnWTick(object s, EventArgs e)
        {
            if (auto && state == "match" && chkW.Checked && !wPressed && Display.WDown())
            {
                wPressed = true;
                Log.W("W pressed -> applying match res");
                try { SyncRes(); SyncFxNow2(); status.Text = "Game state: Match\nResolution: " + Display.Cur(); }
                catch (Exception ex) { Log.W("W error: " + ex.Message); }
            }
        }

        // closing the window or shutting down Windows: restore ONLY if something was really left changed
        void OnClosing(object s, FormClosingEventArgs e)
        {
            timer.Stop(); wTimer.Stop(); fxTimer.Stop();
            dirty = true; SaveSettings();
            if (NeedsRestore())
            {
                Text = "Restoring, please wait..."; Refresh();
                Display.ShutdownBlockReasonCreate(Handle, "Restoring monitors and resolution");
                try { RestoreAll(); Log.W("closing: you forgot to restore -> monitors enabled + default res restored"); }
                catch (Exception ex) { Log.W("close restore error: " + ex.Message); }
                Display.ShutdownBlockReasonDestroy(Handle);
            }
            else Log.W("closing: already restored, nothing to do");
            Fx.Shutdown();
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        [STAThread]
        static void Main()
        {
            if (!IsAdmin())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                    psi.UseShellExecute = true; psi.Verb = "runas";      // shows the Windows admin (UAC) prompt
                    Process.Start(psi);
                }
                catch { MessageBox.Show("Redux needs administrator rights to disable monitors.\nRight-click the file > Run as administrator.", "Redux by Huighe"); }
                return;
            }
            try { SetProcessDPIAware(); } catch { }
            bool created;
            using (Mutex mtx = new Mutex(true, "Global\\Redux_Huighe_9", out created))
            {
                if (!created) { MessageBox.Show("Redux is already running.", "Redux by Huighe"); return; }
                Log.Init();
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Log.W("ui error: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    Log.W("CRASH: " + e.ExceptionObject + " -> turning monitors back on");
                    try { Monitors.EnableAll(); } catch { }
                };
                Application.Run(new MainForm());
            }
        }
    }
}
