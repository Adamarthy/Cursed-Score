using System.Reflection;
using MelonLoader;

[assembly: AssemblyTitle(CursedScore.BuildInfo.Description)]
[assembly: AssemblyDescription(CursedScore.BuildInfo.Description)]
[assembly: AssemblyCompany(CursedScore.BuildInfo.Company)]
[assembly: AssemblyProduct(CursedScore.BuildInfo.Name)]
[assembly: AssemblyCopyright("Created by " + CursedScore.BuildInfo.Author)]
[assembly: AssemblyTrademark(CursedScore.BuildInfo.Company)]
[assembly: AssemblyVersion(CursedScore.BuildInfo.Version)]
[assembly: AssemblyFileVersion(CursedScore.BuildInfo.Version)]
[assembly: MelonInfo(typeof(CursedScore.Main), CursedScore.BuildInfo.Name, CursedScore.BuildInfo.Version, CursedScore.BuildInfo.Author, CursedScore.BuildInfo.DownloadLink)]
[assembly: MelonColor()]

// Create and Setup a MelonGame Attribute to mark a Melon as Universal or Compatible with specific Games.
// If no MelonGame Attribute is found or any of the Values for any MelonGame Attribute on the Melon is null or empty it will be assumed the Melon is Universal.
// Values for MelonGame Attribute can be found in the Game's app.info file or printed at the top of every log directly beneath the Unity version.
[assembly: MelonGame("Buried Things", "Cursed Words")]