// Version resources for the three exes. An unsigned exe with a blank description, product and company and version 0.0.0.0
// is a strong signal for Defender's cloud ML models (reported 2026-11-13 as Win32/Contebrew.A!ml), so every exe states what it is.
// build.ps1 passes one of SETUP, MANAGER or PACK.
using System.Reflection;
using System.Runtime.InteropServices;

#if SETUP
[assembly: AssemblyTitle("BattleTech UCU Setup")]
[assembly: AssemblyDescription("Installs and updates BattleTech: Unofficial Community Update for owners of BATTLETECH.")]
#elif MANAGER
[assembly: AssemblyTitle("BattleTech UCU Release Manager")]
[assembly: AssemblyDescription("Packs and publishes BattleTech: Unofficial Community Update releases.")]
#elif PACK
[assembly: AssemblyTitle("BattleTech UCU Release Pack")]
[assembly: AssemblyDescription("Packs a BattleTech: Unofficial Community Update release.")]
#endif
[assembly: AssemblyProduct("BattleTech: Unofficial Community Update")]
[assembly: AssemblyCompany("BattleTech UCU community project")]
[assembly: AssemblyCopyright("BattleTech UCU contributors. Unofficial; not affiliated with Paradox Interactive or Harebrained Schemes.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: ComVisible(false)]
