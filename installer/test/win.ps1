# Window-message helpers for testing the installer window without physical input (lessons/winforms-test-driving-window-messages).
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
using System.Drawing; using System.Drawing.Imaging;
public static class Win {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, StringBuilder l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, int flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    public static int Start(string exe, string args, string dir) {
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.dwFlags = 1; si.wShowWindow = 4; // SW_SHOWNOACTIVATE
        PROCESS_INFORMATION pi;
        if (!CreateProcess(null, new StringBuilder("\"" + exe + "\" " + args), IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dir, ref si, out pi)) throw new System.ComponentModel.Win32Exception();
        return pi.dwProcessId;
    }
    public static List<IntPtr> Tops(int pid) {
        var l = new List<IntPtr>();
        EnumWindows((h, x) => { int p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero);
        return l;
    }
    public static List<IntPtr> Children(IntPtr parent) {
        var l = new List<IntPtr>();
        EnumChildWindows(parent, (h, x) => { l.Add(h); return true; }, IntPtr.Zero);
        return l;
    }
    public static string Text(IntPtr h) {
        int n = (int)SendMessage(h, 0x0E, IntPtr.Zero, IntPtr.Zero);
        var sb = new StringBuilder(n + 1); SendMessage(h, 0x0D, (IntPtr)(n + 1), sb); return sb.ToString();
    }
    public static string Class(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static bool Visible(IntPtr h) { return IsWindowVisible(h); }
    public static void SetText(IntPtr h, string s) { SendMessage(h, 0x0C, IntPtr.Zero, s); }
    public static void Click(IntPtr h) { PostMessage(h, 0xF5, IntPtr.Zero, IntPtr.Zero); }
    public static void Shot(IntPtr h, string path) {
        RECT r; GetWindowRect(h, out r);
        using (var b = new Bitmap(r.R - r.L, r.B - r.T)) {
            using (var g = Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
            b.Save(path, ImageFormat.Png);
        }
    }
}
'@

function Dump([int]$ProcessId) {
    foreach ($t in [Win]::Tops($ProcessId)) {
        "top {0} [{1}] '{2}'" -f $t, [Win]::Class($t), [Win]::Text($t)
        $i = 0
        foreach ($c in [Win]::Children($t)) {
            $cls = [Win]::Class($c) -replace '^WindowsForms10\.([A-Za-z]+)\..*', '$1'
            "  {0,2} {1,-8} vis={2} en={3} '{4}'" -f $i, $cls, [int][Win]::Visible($c), [int][Win]::IsWindowEnabled($c), ([Win]::Text($c) -replace "`r?`n", ' | ')
            $i++
        }
    }
}

function Control([int]$ProcessId, [string]$Kind, [int]$Nth) {
    $t = [Win]::Tops($ProcessId) | Where-Object { [Win]::Class($_) -like 'WindowsForms10.Window*' } | Select-Object -First 1
    @([Win]::Children($t) | Where-Object { [Win]::Class($_) -like "WindowsForms10.$Kind*" })[$Nth]
}

function WaitFor([scriptblock]$Cond, [int]$Seconds = 60) {
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) { if (& $Cond) { return $true }; Start-Sleep -Milliseconds 300 }
    $false
}
