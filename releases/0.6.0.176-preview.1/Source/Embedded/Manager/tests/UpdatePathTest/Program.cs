using System;
using System.IO;
using System.Security.Cryptography;
using GameTranslatorManager;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: test gameRoot baselineDll");
    return 2;
}

const long ExpectedSize = 19456;
const string ExpectedHash =
    "D5C1547CEC6374C0FA875563AAC43EA2C6FE2D043F54D46621567CC14C795EDC";

var root = Path.GetFullPath(args[0]);
var plugin = Path.Combine(
    root, "BepInEx", "plugins", "MGITranslator", "MGITranslator.dll");
var cache = Path.Combine(
    root, "BepInEx", "config", "MGITranslator.cache.tsv");
Directory.CreateDirectory(root);
File.WriteAllBytes(Path.Combine(root, "MGI.exe"), Array.Empty<byte>());

var game = new GameInfo(
    Path.Combine(root, "MGI.exe"),
    "MGI",
    "Unity Mono",
    "64 位",
    "test",
    SupportLevel.Supported,
    null);
var settings = new TranslatorSettings(
    "https://api.deepseek.com",
    "test-key",
    "test-model",
    "Simplified Chinese");
var installer = new AdapterInstaller();

// Fresh install, then simulate an existing 0.3.2 plug-in before updating.
installer.Install(game, settings);
File.Copy(args[1], plugin, true);
File.SetAttributes(plugin, FileAttributes.Normal);
File.WriteAllText(cache, "cache-must-survive");
installer.UpdateInstalled(game, settings);

var installedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(plugin)));
var installedSize = new FileInfo(plugin).Length;
var cachePreserved = File.ReadAllText(cache) == "cache-must-survive";
Console.WriteLine("InstalledHash=" + installedHash);
Console.WriteLine("InstalledSize=" + installedSize);
Console.WriteLine("CachePreserved=" + cachePreserved);
Console.WriteLine(installer.LastVerificationReport);
var passed =
    installedHash == ExpectedHash &&
    installedSize == ExpectedSize &&
    cachePreserved;
Console.WriteLine("UpdateIntegrationPassed=" + passed);

File.WriteAllBytes(plugin, new byte[] { 1, 2, 3, 4 });
var invalidDllRejected = false;
try
{
    installer.SaveSettings(game, settings);
}
catch (InvalidDataException)
{
    invalidDllRejected = true;
}
Console.WriteLine("InvalidDllRejected=" + invalidDllRejected);

installer.Uninstall(game);
var restorePassed =
    File.Exists(Path.Combine(root, "MGI.exe")) &&
    !File.Exists(Path.Combine(root, ".game-translator-manager.json")) &&
    !Directory.Exists(Path.Combine(root, "BepInEx"));
Console.WriteLine("RestoreOriginalPassed=" + restorePassed);
return passed && invalidDllRejected && restorePassed ? 0 : 1;
