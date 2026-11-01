using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DvsRepair
{
    internal static class SelfTests
    {
        private static readonly List<string> results = new List<string>();
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Test(string name, Action action)
        {
            try { action(); results.Add("PASS " + name); }
            catch (Exception ex) { results.Add("FAIL " + name + ": " + ex); }
        }
        private static void ExpectFailure(Action action)
        {
            bool failed = false;
            try { action(); } catch (IOException) { failed = true; } catch (System.IO.InvalidDataException) { failed = true; }
            Assert(failed, "Expected an IO error.");
        }
        private static void Seed(string install, string licensing, Dictionary<string, byte[]> originals)
        {
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(licensing);
            for (int i = 0; i < BuildInfo.Files.Length; i++)
            {
                PayloadFile spec = BuildInfo.Files[i];
                string target = spec.RelativePath == "dvs_service.exe" ? Path.Combine(install, "dvs_service.exe") : Files.Under(licensing, spec.RelativePath.Substring("Licensing/".Length));
                if (i == BuildInfo.Files.Length - 1) continue;
                byte[] bytes = Encoding.UTF8.GetBytes("ORIGINAL\r\n" + spec.RelativePath + "\r\n");
                File.WriteAllBytes(target, bytes);
                originals.Add(target, bytes);
            }
            File.WriteAllText(Path.Combine(licensing, "unrelated-license.txt"), "KEEP THIS");
        }
        private static void CheckOriginals(string install, string licensing, Dictionary<string, byte[]> originals)
        {
            foreach (var pair in originals) Assert(File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value), "Original not restored: " + pair.Key);
            foreach (PayloadFile spec in BuildInfo.Files)
            {
                string target = spec.RelativePath == "dvs_service.exe" ? Path.Combine(install, "dvs_service.exe") : Files.Under(licensing, spec.RelativePath.Substring("Licensing/".Length));
                if (!originals.ContainsKey(target)) Assert(!File.Exists(target), "A new file was not removed.");
            }
            Assert(File.ReadAllText(Path.Combine(licensing, "unrelated-license.txt")) == "KEEP THIS", "An unrelated license changed.");
        }
        private static void InSandbox(string root, string name, Action<string, string, BackupLayout, Dictionary<string, byte[]>> test)
        {
            string folder = Path.Combine(root, name);
            string install = Path.Combine(folder, "Audinate", "Dante Virtual Soundcard");
            string licensing = Path.Combine(folder, "ProgramData", "Audinate", "Licensing");
            var originals = new Dictionary<string, byte[]>();
            Seed(install, licensing, originals);
            var backup = new BackupLayout(install, licensing, "first-run");
            backup.Create();
            test(install, licensing, backup, originals);
        }

        internal static int Run(string output)
        {
            string root = Path.Combine(Path.GetTempPath(), "DVSRepair-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string stage = Path.Combine(root, "stage");
            try
            {
                Test("Embedded payload: extraction, four files, lengths and SHA-256", () => {
                    Payload.Extract(stage);
                    Assert(BuildInfo.Files.Length == 4, "Expected exactly four embedded files.");
                    Assert(Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Length == 4, "Archive has an unexpected file count.");
                    foreach (PayloadFile spec in BuildInfo.Files) Files.Check(Files.Under(stage, spec.RelativePath), spec);
                });
                Test("Service ImagePath parser handles quotes, spaces and arguments", () => {
                    Assert(Installation.ExecutableFromImagePath("\"C:\\Program Files (x86)\\Audinate\\Dante Virtual Soundcard\\dvs_service.exe\" --service") == @"C:\Program Files (x86)\Audinate\Dante Virtual Soundcard\dvs_service.exe", "Quoted path parsing failed.");
                    Assert(Installation.ExecutableFromImagePath(@"C:\Program Files\Audinate\Dante Virtual Soundcard\dvs_service.exe --service") == @"C:\Program Files\Audinate\Dante Virtual Soundcard\dvs_service.exe", "Unquoted path parsing failed.");
                    Assert(Installation.ExecutableFromImagePath("invalid") == "", "Invalid image path was accepted.");
                });
                Test("Folder validation accepts Audinate and rejects Audiante", () => {
                    Assert(Installation.IsAudinateFolder(@"C:\Program Files (x86)\Audinate\Dante Virtual Soundcard"), "Audinate rejected.");
                    Assert(Installation.IsAudinateFolder(@"D:\Audio\Audinate\Dante Virtual Soundcard"), "Custom installation drive rejected.");
                    Assert(!Installation.IsAudinateFolder(@"C:\Program Files (x86)\Audiante\Dante Virtual Soundcard"), "Audiante accepted.");
                    Assert(!Installation.IsAudinateFolder(@"C:\FakeAudinate\Dante Virtual Soundcard"), "False parent name accepted.");
                });
                Test("Archive path traversal, rooted paths and ADS are rejected", () => {
                    ExpectFailure(() => Files.Under(stage, "../outside.exe"));
                    ExpectFailure(() => Files.Under(stage, @"C:\outside.exe"));
                    ExpectFailure(() => Files.Under(stage, "Licensing/file:stream"));
                });
                Test("Native accessibility names follow discovery, progress and terminal label updates", () => {
                    using (var form = new MainForm(false, 1))
                    {
                        foreach (string state in new[] { "idle", "progress", "success", "error" })
                        {
                            form.SetPreviewState(state);
                            foreach (System.Windows.Forms.Label label in form.Controls.OfType<System.Windows.Forms.Label>())
                                Assert(label.AccessibilityObject.Name == label.Text, "Stale accessible label in state " + state + ": " + label.Text);
                        }
                        foreach (System.Windows.Forms.Label label in form.Controls.OfType<System.Windows.Forms.Label>())
                        {
                            label.Text = "Updated label";
                            Assert(label.AccessibilityObject.Name == label.Text, "Terminal label update was not exposed to accessibility.");
                        }
                    }
                });
                Test("Successful copy, verified backup, preserved unrelated file and full rollback", () => InSandbox(root, "success", (install, licensing, backup, originals) => {
                    var transaction = new FileTransaction(stage, install, licensing, backup, ignored => { });
                    transaction.BackupAll();
                    int count = 0;
                    transaction.Apply(value => count = value, null);
                    Assert(count == 4, "Not all files copied.");
                    foreach (Replacement item in transaction.Items) { Files.Check(item.Target, item.Spec); if (item.Existed) Assert(File.ReadAllBytes(item.Backup).SequenceEqual(originals[item.Target]), "Bad backup."); }
                    Assert(File.ReadAllText(Path.Combine(licensing, "unrelated-license.txt")) == "KEEP THIS", "Unrelated file changed.");
                    Assert(File.Exists(Path.Combine(backup.ServiceRoot, "backup-index.tsv")), "Service backup manifest missing.");
                    Assert(File.Exists(Path.Combine(backup.LicensingRoot, "backup-index.tsv")), "Licensing backup manifest missing.");
                    transaction.Rollback();
                    CheckOriginals(install, licensing, originals);
                }));
                for (int point = 1; point <= 4; point++)
                {
                    int failAt = point;
                    Test("Injected failure after file " + failAt + ": complete rollback", () => InSandbox(root, "failure-" + failAt, (install, licensing, backup, originals) => {
                        var transaction = new FileTransaction(stage, install, licensing, backup, ignored => { });
                        transaction.BackupAll();
                        ExpectFailure(() => transaction.Apply(ignored => { }, number => { if (number == failAt) throw new IOException("Injected failure"); }));
                        transaction.Rollback();
                        CheckOriginals(install, licensing, originals);
                    }));
                }
                Test("Real locked destination triggers rollback of previously replaced file", () => InSandbox(root, "locked", (install, licensing, backup, originals) => {
                    var transaction = new FileTransaction(stage, install, licensing, backup, ignored => { });
                    transaction.BackupAll();
                    using (var locked = new FileStream(transaction.Items[1].Target, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        ExpectFailure(() => transaction.Apply(ignored => { }, null));
                        transaction.Rollback();
                    }
                    CheckOriginals(install, licensing, originals);
                }));
                Test("Staged file tampering is rejected before installation", () => InSandbox(root, "tamper", (install, licensing, backup, originals) => {
                    string damagedStage = Path.Combine(root, "damaged-stage");
                    Payload.Extract(damagedStage);
                    var transaction = new FileTransaction(damagedStage, install, licensing, backup, ignored => { });
                    transaction.BackupAll();
                    File.AppendAllText(transaction.Items[0].Source, "tampered");
                    ExpectFailure(() => transaction.Apply(ignored => { }, null));
                    transaction.Rollback();
                    CheckOriginals(install, licensing, originals);
                }));
                Test("Adjacent backup folders and second run preserve the previous originals", () => InSandbox(root, "adjacent", (install, licensing, backup, originals) => {
                    var first = new FileTransaction(stage, install, licensing, backup, ignored => { });
                    first.BackupAll();
                    Assert(backup.ServiceRoot == Path.Combine(install, "backup", "first-run"), "Service backup is not next to its original.");
                    Assert(backup.LicensingRoot == Path.Combine(licensing, "backup", "first-run"), "License backups are not next to their originals.");
                    foreach (Replacement item in first.Items)
                    {
                        string expectedRoot = item.Spec.RelativePath == "dvs_service.exe" ? backup.ServiceRoot : backup.LicensingRoot;
                        Assert(Path.GetDirectoryName(item.Backup) == expectedRoot, "Unexpected backup subfolder.");
                        Assert(Path.GetFileName(item.Backup) == Path.GetFileName(item.Target), "Backup filename changed.");
                    }
                    first.Apply(ignored => { }, null);
                    var secondLayout = new BackupLayout(install, licensing, "second-run");
                    secondLayout.Create();
                    var second = new FileTransaction(stage, install, licensing, secondLayout, ignored => { });
                    second.BackupAll();
                    foreach (Replacement item in first.Items)
                        if (item.Existed) Assert(File.ReadAllBytes(item.Backup).SequenceEqual(originals[item.Target]), "A previous backup was overwritten.");
                    foreach (Replacement item in second.Items) Files.Check(item.Backup, item.Spec);
                    first.Rollback();
                    CheckOriginals(install, licensing, originals);
                }));
                Test("Backup write failure leaves every original untouched", () => InSandbox(root, "backup-failure", (install, licensing, backup, originals) => {
                    var transaction = new FileTransaction(stage, install, licensing, backup, ignored => { });
                    Directory.CreateDirectory(transaction.Items[1].Backup);
                    ExpectFailure(() => transaction.BackupAll());
                    CheckOriginals(install, licensing, originals);
                }));
                try { Installation install = Installation.Locate(); results.Add("INFO Read-only discovery: " + install.DirectoryPath + "; service=" + install.ServiceName); }
                catch (Exception ex) { results.Add("INFO DVS discovery: " + ex.Message); }
            }
            finally
            {
                // Only this unique test directory is ever removed, never an installation folder.
                if (Directory.Exists(root)) Directory.Delete(root, true);
                int failed = results.Count(line => line.StartsWith("FAIL ", StringComparison.Ordinal));
                results.Add("SUMMARY " + results.Count(line => line.StartsWith("PASS ", StringComparison.Ordinal)) + " passed; " + failed + " failed. Installed DVS, services, system licenses and reboot state were not changed.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                File.WriteAllLines(output, results, new UTF8Encoding(true));
            }
            return results.Any(line => line.StartsWith("FAIL ", StringComparison.Ordinal)) ? 1 : 0;
        }
    }
}
