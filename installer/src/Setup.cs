// Installer entry point. Sits beside package.vault and data/, or alone (a GitHub release: it then downloads the package),
// and writes nothing to the retail install.
//   BattleTechUCUSetup                                        opens the window; with no package beside it, on the newest
//                                                            published release (Remote.DefaultSource)
//   BattleTechUCUSetup --retail <folder> --dest <folder>      installs with no window; see the exit codes in InstallResult
//   BattleTechUCUSetup --no-detect                            opens the window without searching Steam's libraries
//   --package <zip>                                          reads the package from that zip (the itch.io website
//                                                            download) without extracting it; with any of the above
//   --remote [<source>]                                      installs or updates from a release online, downloading only
//                                                            the files the install folder doesn't already have. <source> is
//                                                            github:<owner>/<repo>[@<tag>] or a URL of JSON in the shape of
//                                                            GitHub's release API; default Remote.DefaultSource, newest
//                                                            published release. With or without the window
//   --dry-run                                                with --remote and no window: says what would be downloaded
//                                                            and written, and changes nothing
//   --code <code> | --code-file <file>                       the access code a v3 package's unlock server checks; the
//                                                            window remembers the last accepted one for this Windows user.
//                                                            Without one, the installer proves ownership through Steam
//   --steam-app <id>                                         the Steam app the ownership ticket is for (tests: 480)
//   --share-file <file>                                      unlocks a v3 package with its share, asking no server (tests)
//   --unlock-url <url>                                       asks this unlock server instead of the package's (tests)
//   --mods | --no-mods                                       turns mod support (ModTek) on or off with no window; without
//                                                            either, the install keeps its last choice, on for a new one
//   BattleTechUCUSetup --verify <install folder>             checks every installed file against the install's copy of
//                                                            the public manifest (ucu-manifest.tsv); no window, no network
//   BattleTechUCUSetup --backup-saves                        only the one-time save backup (SaveBackup.cs), no window; an
//                                                            install that succeeds runs it too
//   --steam-root <folder> | --backup-root <folder>           tests: the backup reads that Steam folder / writes there
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ReleaseGate
{
    static class Setup
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        /// <summary>This run's arguments, for starting a newer installer the same way.</summary>
        public static string[] Arguments = new string[0];
        /// <summary>Started by an older installer for a release it couldn't read; never hands on again.</summary>
        public static bool Handed;
        const string HandedFlag = "--handed-on";

        [STAThread]
        static int Main(string[] a)
        {
            Arguments = a;
            int v = Array.IndexOf(a, "--verify");
            if (v >= 0 && v + 1 < a.Length)
            {
                AttachConsole(-1);
                string message;
                InstallResult r = Installer.Verify(Path.GetFullPath(a[v + 1]), new InstallProgress(), CancellationToken.None, out message);
                (r == InstallResult.Installed ? Console.Out : Console.Error).WriteLine(message);
                return (int)r;
            }
            string dest = null, retail = null, remote = null;
            bool detect = true, dryRun = false, backupOnly = false;
            bool? mods = null;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == "--backup-saves") backupOnly = true;
                else if (a[i] == "--steam-root" && i + 1 < a.Length) SaveBackup.SteamRootOverride = Path.GetFullPath(a[++i]);
                else if (a[i] == "--backup-root" && i + 1 < a.Length) SaveBackup.BackupRootOverride = Path.GetFullPath(a[++i]);
                else if (a[i] == "--no-detect") detect = false;
                else if (a[i] == "--dry-run") dryRun = true;
                else if (a[i] == "--mods") mods = true;
                else if (a[i] == "--no-mods") mods = false;
                else if (a[i] == HandedFlag) Handed = true;
                else if (a[i] == "--dest" && i + 1 < a.Length) dest = a[++i];
                else if (a[i] == "--retail" && i + 1 < a.Length) retail = a[++i];
                else if (a[i] == "--package" && i + 1 < a.Length) Installer.UseZip(Path.GetFullPath(a[++i]));
                else if (a[i] == "--code" && i + 1 < a.Length) Unlock.Code = a[++i];
                else if (a[i] == "--code-file" && i + 1 < a.Length) Unlock.Code = ReadArgFile(a[++i]);
                else if (a[i] == "--share-file" && i + 1 < a.Length) Unlock.ShareOverride = Gate.FromHex(ReadArgFile(a[++i]) ?? "");
                else if (a[i] == "--unlock-url" && i + 1 < a.Length) Unlock.UrlOverride = a[++i];
                else if (a[i] == "--steam-app" && i + 1 < a.Length) SteamTicket.AppId = uint.Parse(a[++i]);
                else if (a[i] == "--remote") remote = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : Remote.DefaultSource;
            }
            if (backupOnly)
            {
                AttachConsole(-1);
                string b = SaveBackup.Run();
                Console.Out.WriteLine(b ?? "No save backup needed: no BATTLETECH saves found, or every account already has one in " + SaveBackup.BackupRoot + ".");
                return b != null && b.Contains("couldn't") ? (int)InstallResult.Failed : 0;
            }
            if (dest == null && retail == null)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Alone, as downloaded from a GitHub release: install from the release online. Run out of the itch.io
                // website zip without the zip being found: ask for it.
                if (remote == null && !Installer.HasPackage)
                {
                    if (Installer.ZipNameRunFrom() != null) AskForZip();
                    else remote = Remote.DefaultSource;
                }
                Application.Run(new SetupForm(detect ? Gate.FindRetail() : null, remote));
                return 0;
            }
            AttachConsole(-1);
            if (Unlock.Code == null) Unlock.Code = Unlock.LoadSaved();
            return (int)Headless(retail ?? Gate.FindRetail(), dest, remote, dryRun, mods);
        }

        static string ReadArgFile(string path)
        {
            try { return Unlock.ReadFile(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// No package beside the installer and no zip found: most likely it was run out of the downloaded zip, or copied
        /// out of it alone. Ask for the zip, which is then read in place.
        /// </summary>
        static void AskForZip()
        {
            string zipName = Installer.ZipNameRunFrom();
            using (var d = new OpenFileDialog
            {
                Title = "Choose the BattleTech UCU zip you downloaded",
                Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*",
                FileName = zipName ?? "",
            })
            {
                foreach (string dir in Installer.DownloadFolders())
                    if (Directory.Exists(dir)) { d.InitialDirectory = dir; break; }
                if (d.ShowDialog() == DialogResult.OK) Installer.UseZip(d.FileName);
            }
        }

        /// <summary>This run's arguments, with --remote set to the source that was used, quoted for a command line.</summary>
        public static string ArgumentsFor(string source)
        {
            var l = new List<string>();
            for (int i = 0; i < Arguments.Length; i++)
            {
                if (Arguments[i] == "--remote")
                {
                    if (i + 1 < Arguments.Length && !Arguments[i + 1].StartsWith("--")) i++;
                    continue;
                }
                if (Arguments[i] != HandedFlag) l.Add(Arguments[i]);
            }
            l.Add("--remote");
            l.Add(source);
            l.Add(HandedFlag);
            return string.Join(" ", l.Select(Quote));
        }

        /// <summary>Quotes an argument the way the C runtime splits a command line: backslashes double only before a quote.</summary>
        static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            var b = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in s)
            {
                if (ch == '\\') { slashes++; continue; }
                b.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes).Append(ch);
                slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }

        static InstallResult Headless(string retail, string dest, string remote, bool dryRun, bool? mods)
        {
            if (remote != null)
            {
                Console.Out.WriteLine("Reading " + remote + "...");
                RemotePackage p;
                try { p = Remote.Open(remote, CancellationToken.None); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(e.Message);
                    return InstallResult.Failed;
                }
                if (p.NeedsNewerInstaller && Handed)
                {
                    Console.Error.WriteLine("Release " + p.Version + " needs a newer installer than the one it ships.");
                    return InstallResult.Failed;
                }
                if (p.NeedsNewerInstaller)
                {
                    Console.Out.WriteLine("Release " + p.Version + " needs a newer installer; starting the one it ships.");
                    try
                    {
                        using (var newer = Remote.StartNewerInstaller(p, ArgumentsFor(remote), true, CancellationToken.None))
                        {
                            newer.WaitForExit();
                            return (InstallResult)newer.ExitCode;
                        }
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception)
                    {
                        Console.Error.WriteLine(e.Message);
                        return InstallResult.Failed;
                    }
                }
                Installer.UseRemote(p);
                Console.Out.WriteLine(string.Format("Release {0}: {1} package files in {2} data parts.", p.Version, p.Blobs.Count, p.Blobs.Values.Select(b => b.Part).Distinct().Count()));
            }
            if (retail == null)
            {
                Console.Error.WriteLine("BATTLETECH was not found in your Steam libraries. Pass --retail <folder>.");
                return InstallResult.NoRetail;
            }
            RetailCheck c = Installer.Check(retail);
            if (c.Problem != null)
            {
                Console.Error.WriteLine(c.Problem);
                return InstallResult.RetailRefused;
            }
            dest = Path.GetFullPath(dest ?? SetupForm.DefaultDest(c.Folder));
            long space;
            string problem = Installer.CheckDest(dest, c, out space);
            if (problem != null)
            {
                Console.Error.WriteLine(problem);
                return InstallResult.Failed;
            }
            string message;
            if (remote != null)
            {
                Installer.Plan plan = Installer.PlanInstall(c, dest);
                Console.Out.WriteLine(string.Format("{0} files to write ({1:F2} GiB), {2} up to date, {3} to remove; {4} package files to download ({5:F2} GiB).",
                    plan.Files, plan.WriteBytes / 1073741824.0, plan.Same, plan.Remove, plan.Fetch.Count, plan.FetchBytes / 1073741824.0));
                if (dryRun) return InstallResult.Installed;
                problem = Installer.ProbeWrite(dest);
                if (problem != null)
                {
                    Console.Error.WriteLine(problem);
                    return InstallResult.Failed;
                }
                InstallResult f = Installer.Fetch(plan, dest, new InstallProgress(), CancellationToken.None, out message);
                if (f != InstallResult.Installed)
                {
                    Console.Error.WriteLine(message);
                    return f;
                }
            }
            else
            {
                if (dryRun)
                {
                    Console.Error.WriteLine("--dry-run needs --remote.");
                    return InstallResult.Failed;
                }
                problem = Installer.ProbeWrite(dest);
                if (problem != null)
                {
                    Console.Error.WriteLine(problem);
                    return InstallResult.Failed;
                }
            }
            InstallResult r = Installer.Install(c, dest, new InstallProgress(), CancellationToken.None, out message);
            (r == InstallResult.Installed ? Console.Out : Console.Error).WriteLine(message);
            if (r == InstallResult.Installed)
            {
                string m = Installer.ApplyMods(dest, mods ?? Installer.ModsWanted(dest));
                if (m != null) Console.Out.WriteLine(m);
                string b = SaveBackup.Run();
                if (b != null) Console.Out.WriteLine(b);
            }
            return r;
        }
    }
}
