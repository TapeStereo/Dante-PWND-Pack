using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DvsRepair
{
    internal static class SetupTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Fails(Action action)
        {
            try { action(); } catch (IOException) { return; } catch (InvalidDataException) { return; } catch (InvalidOperationException) { return; }
            throw new Exception("Expected workflow failure.");
        }
        private sealed class Host : ISetupHost
        {
            internal SetupInventory Inventory = new SetupInventory();
            internal readonly List<string> Events = new List<string>();
            internal int DvsCode, ControllerCode;
            internal bool MissingDvs, PendingRename, DamagedController;
            internal Version ControllerResult = InstallerBuildInfo.Controller.Version;
            public SetupInventory Observe() { return Inventory; }
            public string Extract(InstallerSpec spec, Action<int> progress)
            {
                Events.Add("extract:" + spec.Name);
                if (DamagedController && spec == InstallerBuildInfo.Controller) throw new InvalidDataException("Damaged installer");
                progress(100);
                return spec.FileName;
            }
            public int Execute(InstallerSpec spec, string file, bool repair, Action<TimeSpan> tick)
            {
                Events.Add((repair ? "repair:" : "install:") + spec.Name);
                tick(TimeSpan.FromSeconds(61));
                int code = spec == InstallerBuildInfo.Dvs ? DvsCode : ControllerCode;
                if (code == 0 || code == 3010)
                {
                    if (spec == InstallerBuildInfo.Dvs && !MissingDvs) { Inventory.Dvs.Include(spec.Version.ToString()); Inventory.DvsReady = true; }
                    if (spec == InstallerBuildInfo.Controller) Inventory.Controller.Include(ControllerResult.ToString());
                }
                return code;
            }
            public void CheckDvsReady() { Events.Add("verify"); if (PendingRename) throw new IOException("Pending reboot"); }
            public void Repair(IProgress<RepairProgress> progress) { Events.Add("replace"); progress.Report(new RepairProgress(100, "Done", "Done")); }
            public void Note(string message) { }
        }
        private static void Run(Host host) { SetupWorkflow.Run(host, new InlineProgress(p => { Assert(p.Percent >= 0 && p.Percent <= 100, "Invalid progress."); })); }
        internal static void RunAll(string root, Action<string, Action> test)
        {
            test("Offline installers: streamed extraction, exact lengths and SHA-256 for both executables", () => {
                string folder = Path.Combine(root, "embedded-installers");
                Directory.CreateDirectory(folder);
                foreach (InstallerSpec spec in new[] { InstallerBuildInfo.Dvs, InstallerBuildInfo.Controller })
                {
                    int last = -1;
                    string file = EmbeddedInstallers.Extract(spec, folder, p => { Assert(p >= last && p <= 100, "Extraction progress is invalid."); last = p; });
                    Assert(last == 100 && new FileInfo(file).Length == spec.Length, "Installer extraction incomplete.");
                    File.Delete(file);
                }
            });
            test("Fresh install: stage both resources, DVS, Controller, verify, then replace", () => {
                var host = new Host(); Run(host);
                Assert(host.Events.SequenceEqual(new[] { "extract:Dante Virtual Soundcard", "extract:Dante Controller", "install:Dante Virtual Soundcard", "install:Dante Controller", "verify", "replace" }), "Wrong installation order.");
            });
            test("Existing DVS and newer Controller are preserved without reinstallation or downgrade", () => {
                var host = new Host(); host.Inventory.Dvs.Include("4.4.1.3"); host.Inventory.DvsReady = true; host.Inventory.Controller.Include("4.18.1.2"); Run(host);
                Assert(host.Events.SequenceEqual(new[] { "verify", "replace" }), "Installed versions were not skipped.");
            });
            test("Older versions are upgraded before replacement", () => {
                var host = new Host(); host.Inventory.Dvs.Include("4.3.0.0"); host.Inventory.DvsReady = true; host.Inventory.Controller.Include("4.14.0.0"); Run(host);
                Assert(host.Events.Contains("install:Dante Virtual Soundcard") && host.Events.Contains("install:Dante Controller") && host.Events.Last() == "replace", "Upgrade missing.");
            });
            test("Same DVS version with missing service selects vendor repair", () => {
                var host = new Host(); host.Inventory.Dvs.Include("4.4.1.3"); Run(host);
                Assert(host.Events.Contains("repair:Dante Virtual Soundcard"), "Broken installation did not select /repair.");
            });
            test("Newer or unknown DVS and unknown Controller block all mutations", () => {
                foreach (string version in new[] { "4.5.0.0", "unknown" })
                {
                    var host = new Host(); host.Inventory.Dvs.Include(version); Fails(() => Run(host)); Assert(host.Events.Count == 0, "Unsupported DVS caused changes.");
                }
                var unknown = new Host(); unknown.Inventory.Controller.Include("unknown"); Fails(() => Run(unknown)); Assert(unknown.Events.Count == 0, "Unknown Controller caused changes.");
            });
            test("DVS installer failure prevents Controller launch and file replacement", () => {
                var host = new Host { DvsCode = 1603 }; Fails(() => Run(host));
                Assert(!host.Events.Contains("install:Dante Controller") && !host.Events.Contains("replace"), "Workflow continued after DVS failure.");
            });
            test("Controller failure preserves installed DVS and prevents file replacement", () => {
                var host = new Host { ControllerCode = 1603 }; Fails(() => Run(host));
                Assert(host.Inventory.DvsReady && !host.Events.Contains("replace"), "Replacement ran after Controller failure.");
            });
            test("Cancellation, installer busy, unexpected reboot and HRESULT errors stop the workflow", () => {
                foreach (int code in new[] { 1602, 1618, 1641, unchecked((int)0x80070643), unchecked((int)0x80070000), unchecked((int)0x80070bc2) })
                {
                    var host = new Host { DvsCode = code }; Fails(() => Run(host)); Assert(!host.Events.Contains("replace"), "An error code was accepted: " + code);
                }
            });
            test("3010 allows verified installation to continue without initiating a reboot", () => {
                var host = new Host { DvsCode = 3010, ControllerCode = 3010 }; Run(host);
                Assert(host.Events.Last() == "replace", "Reboot-required success rejected.");
            });
            test("Zero exit with missing DVS or incomplete Controller is rejected", () => {
                var missing = new Host { MissingDvs = true }; Fails(() => Run(missing)); Assert(!missing.Events.Contains("replace") && !missing.Events.Contains("install:Dante Controller"), "Unverified DVS accepted.");
                var wrongVersion = new Host { ControllerResult = new Version("4.15.0.8") }; Fails(() => Run(wrongVersion)); Assert(!wrongVersion.Events.Contains("replace"), "MSI version was confused with bundle version.");
            });
            test("Damaged second installer stops before either vendor installer starts", () => {
                var host = new Host { DamagedController = true }; Fails(() => Run(host)); Assert(!host.Events.Any(e => e.StartsWith("install:")), "Installer launched before resource verification.");
            });
            test("Pending DVS replacement requires reboot before patching", () => {
                var host = new Host { PendingRename = true }; Fails(() => Run(host)); Assert(!host.Events.Contains("replace"), "Pending reboot ignored.");
            });
            test("Burn command line is quiet, suppresses reboot, quotes logs and selects repair", () => {
                string log = Path.Combine(root, "folder with spaces", "installer.log");
                Assert(EmbeddedInstallers.Arguments(false, log) == "/install /quiet /norestart /log \"" + log + "\"", "Wrong install arguments.");
                Assert(EmbeddedInstallers.Arguments(true, log).StartsWith("/repair /quiet /norestart /log "), "Wrong repair arguments.");
                bool rejected = false;
                try { EmbeddedInstallers.Arguments(false, log + "\" /restart"); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Unsafe log arguments accepted.");
            });
            test("Version detection selects the highest bundle/MSI version and retains unknown-version warnings", () => {
                var product = new ProductVersion(); product.Include("4.15.0.8"); product.Include("4.15.0.10"); product.Include("4.14.0.0");
                Assert(product.Version == new Version("4.15.0.10"), "Maximum version not selected."); product.Include("bad"); Assert(product.Unknown, "Unknown version hidden.");
            });
        }
    }
}
