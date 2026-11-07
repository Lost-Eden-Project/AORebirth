using System;
using AORebirth.Core.Text;
var f = NameFilter.Load(@"C:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\Built\Debug\ZoneEngine_New\Config\NameFilter.json");
string[] names = { "Delmus", "Gaylord", "GayPride", "Slayerdroid Alpha 2", "Target-Dummy_99", "Spicy", "Raccoon", "Chinking",
  "Sniggering", "Nazir", "Shitface", "N1gg4", "n.i.g.g.e.r", "NiiiiGGer", "SandN1gger", "Hitler88", "H1tl3r", "Nazi",
  "NaziKiller", "xXnaziXx", "Coon", "BigCoon", "Fag", "Faggot", "Kike", "Spic", "Pedo", "Pedobear", "&lt;font color=#FF0000&gt;Nazi&lt;/font&gt;" };
foreach (var n in names) Console.WriteLine($"{(f.IsAllowed(n) ? "allow " : "REFUSE")} {n}");
