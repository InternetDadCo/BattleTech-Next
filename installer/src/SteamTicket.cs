// A Steam-signed ticket showing that the signed-in Steam account owns BATTLETECH, for the unlock server to confirm with
// Valve (docs/keyserver-design.md). It goes through the steam_api64.dll in the player's own BATTLETECH folder, so nothing
// of Valve's ships with the installer. Steam must be running and signed in. SteamAPI_Init fails for an account without a
// licence, and Valve binds each ticket to its app id and to Identity, so a ticket for another game or service is refused.
// Retail's DLL predates GetAuthTicketForWebApi, so that call goes through the running Steam client's SteamUser023
// interface; a client without it gets the older session ticket, which Valve's Web API also accepts.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ReleaseGate
{
    public sealed class SteamTicketData
    {
        public string Hex;
        public string Kind;     // "webapi" (bound to Identity) or "session"
    }

    public static class SteamTicket
    {
        public const uint BattleTechAppId = 637090;
        /// <summary>The app the ticket is for. --steam-app changes it for tests (480, Spacewar, against a local Worker).</summary>
        public static uint AppId = BattleTechAppId;
        public const string Identity = "btucu-unlock";
        const int CbSessionTicket = 163, CbWebApiTicket = 168;
        const int SlotGetAuthTicketForWebApi = 14;   // ISteamUser, SteamUser023 (isteamuser.h)
        static readonly object Lock = new object();
        static string _loadedFrom;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("steam_api64")] static extern bool SteamAPI_Init();
        [DllImport("steam_api64")] static extern void SteamAPI_Shutdown();
        [DllImport("steam_api64")] static extern int SteamAPI_GetHSteamUser();
        [DllImport("steam_api64")] static extern int SteamAPI_GetHSteamPipe();
        [DllImport("steam_api64")] static extern IntPtr SteamClient();
        [DllImport("steam_api64")] static extern IntPtr SteamAPI_ISteamClient_GetISteamUser(IntPtr client, int user, int pipe, string version);
        [DllImport("steam_api64")] static extern bool SteamAPI_ISteamUser_BLoggedOn(IntPtr user);
        [DllImport("steam_api64")] static extern uint SteamAPI_ISteamUser_GetAuthSessionTicket(IntPtr user, byte[] ticket, int max, out uint size);
        [DllImport("steam_api64")] static extern void SteamAPI_ISteamUser_CancelAuthTicket(IntPtr user, uint ticket);

        [StructLayout(LayoutKind.Sequential)]
        struct CallbackMsg { public int User; public int Callback; public IntPtr Param; public int Size; }

        // The callback pump is in steamclient64.dll; retail's steam_api64 doesn't export it.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool BGetCallbackFn(int pipe, out CallbackMsg msg, out int call);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool FreeLastCallbackFn(int pipe);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)] delegate uint GetAuthTicketForWebApiFn(IntPtr self, string identity);

        /// <summary>
        /// Gets a ticket from Steam and runs use with it. The ticket is valid only while Steam is up in this process, so use
        /// must finish talking to the server before this returns. Throws UnlockException with a message for the player.
        /// </summary>
        public static T With<T>(string retail, Func<SteamTicketData, T> use)
        {
            lock (Lock)
            {
                Load(retail);
                if (!SteamAPI_Init())
                {
                    if (Process.GetProcessesByName("steam").Length == 0)
                        throw new UnlockException("Steam isn't running. Start Steam, sign in to the account that owns BATTLETECH, and try again.");
                    throw new UnlockException("Steam didn't confirm that this Steam account owns BATTLETECH. Sign in to the account that owns it, " +
                        "and try again. If you were given an access code, enter it instead.");
                }
                try
                {
                    int hUser = SteamAPI_GetHSteamUser(), hPipe = SteamAPI_GetHSteamPipe();
                    IntPtr client = SteamClient();
                    IntPtr user20 = SteamAPI_ISteamClient_GetISteamUser(client, hUser, hPipe, "SteamUser020");
                    if (user20 == IntPtr.Zero || !SteamAPI_ISteamUser_BLoggedOn(user20))
                        throw new UnlockException("Steam is offline. Connect Steam to the internet, then try again.");
                    IntPtr sc = GetModuleHandle("steamclient64.dll");
                    IntPtr pGet = GetProcAddress(sc, "Steam_BGetCallback"), pFree = GetProcAddress(sc, "Steam_FreeLastCallback");
                    if (pGet == IntPtr.Zero || pFree == IntPtr.Zero) throw new UnlockException("This version of Steam can't be asked for a sign-in ticket. Update Steam and try again.");
                    var getCallback = (BGetCallbackFn)Marshal.GetDelegateForFunctionPointer(pGet, typeof(BGetCallbackFn));
                    var freeCallback = (FreeLastCallbackFn)Marshal.GetDelegateForFunctionPointer(pFree, typeof(FreeLastCallbackFn));

                    IntPtr user23 = SteamAPI_ISteamClient_GetISteamUser(client, hUser, hPipe, "SteamUser023");
                    var ticket = new SteamTicketData();
                    uint handle;
                    byte[] session = null;
                    if (user23 != IntPtr.Zero)
                    {
                        IntPtr slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(user23), IntPtr.Size * SlotGetAuthTicketForWebApi);
                        handle = ((GetAuthTicketForWebApiFn)Marshal.GetDelegateForFunctionPointer(slot, typeof(GetAuthTicketForWebApiFn)))(user23, Identity);
                        ticket.Kind = "webapi";
                    }
                    else
                    {
                        var buf = new byte[8192];
                        uint size;
                        handle = SteamAPI_ISteamUser_GetAuthSessionTicket(user20, buf, buf.Length, out size);
                        session = new byte[size];
                        Array.Copy(buf, session, (int)size);
                        ticket.Kind = "session";
                    }
                    if (handle == 0) throw new UnlockException("Steam didn't give a sign-in ticket. Try again.");
                    try
                    {
                        // Wait for Steam to register the ticket (and, for the web ticket, to hand over its bytes).
                        var until = DateTime.UtcNow.AddSeconds(15);
                        while (ticket.Hex == null && DateTime.UtcNow < until)
                        {
                            CallbackMsg m;
                            int call;
                            while (ticket.Hex == null && getCallback(hPipe, out m, out call))
                            {
                                try
                                {
                                    if (m.Callback == CbWebApiTicket && ticket.Kind == "webapi" && (uint)Marshal.ReadInt32(m.Param) == handle)
                                    {
                                        int result = Marshal.ReadInt32(m.Param, 4), n = Marshal.ReadInt32(m.Param, 8);
                                        if (result != 1 || n <= 0 || n > 2560) throw new UnlockException("Steam refused a sign-in ticket (result " + result + "). Try again.");
                                        var b = new byte[n];
                                        Marshal.Copy(m.Param + 12, b, 0, n);
                                        ticket.Hex = Hex(b);
                                    }
                                    else if (m.Callback == CbSessionTicket && ticket.Kind == "session" && (uint)Marshal.ReadInt32(m.Param) == handle)
                                    {
                                        int result = Marshal.ReadInt32(m.Param, 4);
                                        if (result != 1) throw new UnlockException("Steam refused a sign-in ticket (result " + result + "). Try again.");
                                        ticket.Hex = Hex(session);
                                    }
                                }
                                finally { freeCallback(hPipe); }
                            }
                            if (ticket.Hex == null) Thread.Sleep(50);
                        }
                        if (ticket.Hex == null) throw new UnlockException("Steam took too long to give a sign-in ticket. Try again.");
                        return use(ticket);
                    }
                    finally
                    {
                        SteamAPI_ISteamUser_CancelAuthTicket(user20, handle);
                        // Leave no message queued for steam_api to find at shutdown.
                        CallbackMsg rest;
                        int restCall;
                        for (int i = 0; i < 1000 && getCallback(hPipe, out rest, out restCall); i++) freeCallback(hPipe);
                    }
                }
                finally { SteamAPI_Shutdown(); }
            }
        }

        // steam_api64 reads the app id from SteamAppId when it starts; the C runtime copies the environment when the DLL
        // loads, so it is set first. One DLL per process: the first BATTLETECH folder checked supplies it.
        static void Load(string retail)
        {
            Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString());
            if (_loadedFrom != null) return;
            string dll = Path.Combine(retail ?? "", "BattleTech_Data", "Plugins", "steam_api64.dll");
            if (!File.Exists(dll)) throw new UnlockException("Steam's library wasn't found in your BATTLETECH folder. Verify the game's files in Steam and try again.");
            if (LoadLibrary(dll) == IntPtr.Zero) throw new UnlockException("Steam's library in your BATTLETECH folder couldn't be loaded (error " + Marshal.GetLastWin32Error() + ").");
            _loadedFrom = dll;
        }

        static string Hex(byte[] b)
        {
            var s = new StringBuilder(b.Length * 2);
            foreach (byte x in b) s.Append(x.ToString("x2"));
            return s.ToString();
        }
    }
}
