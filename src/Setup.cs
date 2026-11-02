using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace DvsRepair
{
    internal sealed class InstallerSpec
    {
        internal readonly string Name, FileName, Resource, Sha256;
        internal readonly Version Version;
        internal readonly long Length;
        internal InstallerSpec(string name, string file, string version, string hash, long length, string resource)
        { Name = name; FileName = file; Version = new Version(version); Sha256 = hash; Length = length; Resource = resource; }
    }

    internal sealed class ProductVersion
    {
        internal bool Found, Unknown;
        internal Version Version;
        internal void Include(string value)
        {
            Found = true;
            Version parsed;
            if (!System.Version.TryParse(value, out parsed)) { Unknown = true; return; }
            // ARP may contain both a Burn bundle and its MSI package. In particular,
            // Controller 4.15.0.10 contains an application MSI numbered 4.15.0.8.
            if (Version == null || parsed > Version) Version = parsed;
        }
    }

    internal sealed class SetupInventory
    {
        internal readonly ProductVersion Dvs = new ProductVersion();
        internal readonly ProductVersion Controller = new ProductVersion();
        internal bool DvsReady;
        internal static SetupInventory Read()
        {
            var result = new SetupInventory();
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall == null) continue;
                    foreach (string child in uninstall.GetSubKeyNames())
                    using (RegistryKey item = uninstall.OpenSubKey(child))
                    {
                        if (item == null) continue;
                        string name = Convert.ToString(item.GetValue("DisplayName"));
                        ProductVersion product = null;
                        if (String.Equals(name, "Dante Virtual Soundcard", StringComparison.OrdinalIgnoreCase)) product = result.Dvs;
                        if (String.Equals(name, "Dante Controller", StringComparison.OrdinalIgnoreCase)
                            || String.Equals(name, "Dante Controller Package", StringComparison.OrdinalIgnoreCase)) product = result.Controller;
                        if (product != null) product.Include(Convert.ToString(item.GetValue("DisplayVersion")));
                    }
                }
            }
            try { Installation.Locate(); result.DvsReady = true; }
            catch (InvalidOperationException) { result.DvsReady = false; }
            return result;
        }
    }

    internal interface ISetupHost
    {
        SetupInventory Observe();
        string Extract(InstallerSpec spec, Action<int> progress);
        int Execute(InstallerSpec spec, string file, bool repair, Action<TimeSpan> tick);
        void CheckDvsReady();
        void Repair(IProgress<RepairProgress> progress);
        void Note(string message);
    }

    internal sealed class InlineProgress : IProgress<RepairProgress>
    {
        private readonly Action<RepairProgress> report;
        internal InlineProgress(Action<RepairProgress> action) { report = action; }
        public void Report(RepairProgress value) { report(value); }
    }

    internal static class SetupWorkflow
    {
        internal static int NormalizeExitCode(int code)
        {
            uint value = unchecked((uint)code);
            return (value & 0xffff0000U) == 0x80070000U ? (int)(value & 0xffffU) : code;
        }
        internal static void CheckExitCode(string name, int code)
        {
            if (code == 0 || code == 3010) return;
            int normalized = NormalizeExitCode(code);
            string reason;
            if (normalized == 1602) reason = "Установка отменена.";
            else if (normalized == 1618) reason = "Windows уже выполняет другую установку. Дождитесь её завершения и повторите запуск.";
            else if (normalized == 1641) reason = "Установщик сообщил о начавшейся перезагрузке. После перезагрузки запустите комплект снова.";
            else reason = "Проверьте журнал установщика и повторите запуск.";
            throw new IOException(name + ": код " + normalized + " (0x" + unchecked((uint)code).ToString("X8") + "). " + reason + " Замена файлов DVS не запускалась.");
        }
        internal static void CheckCompatibleDvs(ProductVersion product)
        {
            if (product.Unknown) throw new InvalidOperationException("Не удалось определить версию установленной DVS. Замена файлов не запускалась.");
            if (product.Found && product.Version > InstallerBuildInfo.Dvs.Version)
                throw new InvalidOperationException("Установлена DVS " + product.Version + ". Встроенные файлы предназначены для " + InstallerBuildInfo.Dvs.Version + ". Установка более старой версии и замена файлов остановлены.");
        }
        internal static void Run(ISetupHost host, IProgress<RepairProgress> progress)
        {
            progress.Report(new RepairProgress(1, "Проверяем установленные версии", "Подготовка комплекта Dante."));
            SetupInventory before = host.Observe();
            CheckCompatibleDvs(before.Dvs);
            if (before.Controller.Unknown) throw new InvalidOperationException("Не удалось определить версию Dante Controller. Проверьте установленную программу перед повторным запуском.");
            bool installDvs = !before.Dvs.Found || before.Dvs.Version < InstallerBuildInfo.Dvs.Version || !before.DvsReady;
            bool installController = !before.Controller.Found || before.Controller.Version < InstallerBuildInfo.Controller.Version;
            // Verify and stage every required installer before making the first change.
            // A damaged Controller resource must not leave a new DVS installation behind.
            string dvsFile = null, controllerFile = null;
            if (installDvs) dvsFile = host.Extract(InstallerBuildInfo.Dvs, p => progress.Report(new RepairProgress(2 + p * 4 / 100, "Подготавливаем DVS " + InstallerBuildInfo.Dvs.Version, "Извлекаем и проверяем встроенный установщик · " + p + "%")));
            if (installController) controllerFile = host.Extract(InstallerBuildInfo.Controller, p => progress.Report(new RepairProgress(6 + p * 5 / 100, "Подготавливаем Dante Controller", "Извлекаем и проверяем встроенный установщик · " + p + "%")));
            if (installDvs)
            {
                bool repair = before.Dvs.Found && before.Dvs.Version == InstallerBuildInfo.Dvs.Version;
                RunInstaller(host, InstallerBuildInfo.Dvs, dvsFile, repair, 12, progress);
            }
            else host.Note("DVS " + before.Dvs.Version + " already installed; installer skipped.");
            SetupInventory afterDvs = host.Observe();
            CheckCompatibleDvs(afterDvs.Dvs);
            if (!afterDvs.Dvs.Found || afterDvs.Dvs.Version != InstallerBuildInfo.Dvs.Version || !afterDvs.DvsReady)
                throw new IOException("Установка DVS не подтверждена: нужная версия или служба не найдена. Если установщик запросил перезагрузку, перезагрузите ПК и снова нажмите «Сделать красиво». Замена файлов не запускалась.");
            if (installController) RunInstaller(host, InstallerBuildInfo.Controller, controllerFile, false, 40, progress);
            else host.Note("Controller " + before.Controller.Version + " already installed; installer skipped (no downgrade).");
            SetupInventory final = host.Observe();
            CheckCompatibleDvs(final.Dvs);
            if (!final.Dvs.Found || final.Dvs.Version != InstallerBuildInfo.Dvs.Version || !final.DvsReady)
                throw new IOException("Версия или служба DVS изменилась во время установки. Замена файлов не запускалась. Повторите запуск комплекта.");
            if (!final.Controller.Found || final.Controller.Unknown || final.Controller.Version < InstallerBuildInfo.Controller.Version)
                throw new IOException("Установка Dante Controller не подтверждена. Замена файлов DVS не запускалась. Проверьте журнал и повторите запуск.");
            host.CheckDvsReady();
            host.Note("Both applications verified; starting file replacement.");
            host.Repair(new InlineProgress(value => progress.Report(new RepairProgress(value.Percent == 0 ? 70 : 70 + value.Percent * 30 / 100, value.Title, value.Detail))));
        }
        private static void RunInstaller(ISetupHost host, InstallerSpec spec, string file, bool repair, int percent, IProgress<RepairProgress> progress)
        {
            string title = (repair ? "Восстанавливаем " : "Устанавливаем ") + spec.Name + " " + spec.Version;
            progress.Report(new RepairProgress(percent, title, "Ожидаем завершения установщика. Не выключайте ПК."));
            int code = host.Execute(spec, file, repair, elapsed => progress.Report(new RepairProgress(percent, title,
                "Идёт установка · прошло " + ((int)elapsed.TotalMinutes).ToString("00") + ":" + elapsed.Seconds.ToString("00") + ". Не выключайте ПК.")));
            host.Note(spec.Name + " exited with " + code + " / normalized " + NormalizeExitCode(code));
            CheckExitCode(spec.Name, code);
        }
    }

    internal static class EmbeddedInstallers
    {
        internal static string Extract(InstallerSpec spec, string root, Action<int> progress)
        {
            string target = Files.Under(root, spec.FileName);
            Files.NoLinks(target);
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(spec.Resource))
            {
                if (source == null || source.Length != spec.Length) throw new InvalidDataException("Встроенный установщик повреждён: " + spec.Name);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int count, last = -1;
                    long copied = 0;
                    while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        output.Write(buffer, 0, count);
                        copied += count;
                        int value = (int)(copied * 95 / spec.Length);
                        if (value != last) { progress(value); last = value; }
                    }
                }
            }
            Files.Check(target, new PayloadFile(spec.FileName, spec.Sha256, spec.Length));
            progress(100);
            return target;
        }
        internal static string Arguments(bool repair, string log)
        {
            if (!Path.IsPathRooted(log) || log.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new ArgumentException("Invalid installer log path.");
            return (repair ? "/repair" : "/install") + " /quiet /norestart /log \"" + log + "\"";
        }
    }

    internal sealed class SetupEngine : ISetupHost
    {
        internal string LogPath;
        internal readonly RepairEngine Repairer = new RepairEngine();
        private StreamWriter writer;
        private string root;
        private readonly List<string> extracted = new List<string>();
        public void Note(string value)
        {
            try { if (writer != null) { writer.WriteLine(DateTime.Now.ToString("O") + " " + value); writer.Flush(); } }
            catch (IOException) { } catch (ObjectDisposedException) { } catch (UnauthorizedAccessException) { }
        }
        internal void Run(IProgress<RepairProgress> progress)
        {
            if (!RepairEngine.IsAdministrator()) throw new UnauthorizedAccessException("Для установки нужны права администратора.");
            if (!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("Полный комплект требует Windows x64. Установщик Dante Controller содержит компоненты x64.");
            string productRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DantePWNDPack");
            Files.PrivateDirectory(productRoot);
            string parent = Path.Combine(productRoot, "Setup");
            Files.PrivateDirectory(parent);
            root = Path.Combine(parent, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Files.PrivateDirectory(root);
            LogPath = Path.Combine(root, "setup.log");
            using (writer = new StreamWriter(LogPath, false, new UTF8Encoding(true)))
            {
                try
                {
                    Note("Dante PWND Pack v1.1 offline setup; installers DVS " + InstallerBuildInfo.Dvs.Version + "; Controller " + InstallerBuildInfo.Controller.Version);
                    SetupWorkflow.Run(this, progress);
                    Note("SUCCESS; service backup=" + Repairer.BackupPath + "; license backup=" + Repairer.LicensingBackupPath);
                }
                catch (Exception ex) { Note("FAILED " + ex); throw; }
                finally
                {
                    // Only our individual extracted files are deleted. Burn has exited;
                    // its own ProgramData package cache is owned by the vendor installers.
                    foreach (string file in extracted)
                        try { File.Delete(file); } catch (Exception ex) { Note("Installer cleanup: " + ex.Message); }
                    Note("Installer logs retained in " + root);
                }
            }
            writer = null;
        }
        public SetupInventory Observe()
        {
            SetupInventory inventory = SetupInventory.Read();
            Note("Detected DVS=" + inventory.Dvs.Version + "; service=" + inventory.DvsReady + "; Controller=" + inventory.Controller.Version);
            return inventory;
        }
        public string Extract(InstallerSpec spec, Action<int> progress)
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
            if (drive.AvailableFreeSpace < 3L * 1024 * 1024 * 1024 + spec.Length)
                throw new IOException("Для установки освободите не менее 4 ГБ на системном диске и повторите запуск.");
            string target = Files.Under(root, spec.FileName);
            extracted.Add(target);
            return EmbeddedInstallers.Extract(spec, root, progress);
        }
        public int Execute(InstallerSpec spec, string file, bool repair, Action<TimeSpan> tick)
        {
            // Recheck immediately before launching an elevated child process.
            Files.Check(file, new PayloadFile(spec.FileName, spec.Sha256, spec.Length));
            string log = Path.Combine(root, spec == InstallerBuildInfo.Dvs ? "dvs-installer.log" : "controller-installer.log");
            var start = new ProcessStartInfo(file, EmbeddedInstallers.Arguments(repair, log)) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root
            };
            Note("Starting " + spec.Name + "; mode=" + (repair ? "repair" : "install") + "; log=" + log);
            Stopwatch elapsed = Stopwatch.StartNew();
            using (Process process = Process.Start(start))
            {
                if (process == null) throw new IOException("Не удалось запустить установщик " + spec.Name);
                while (!process.WaitForExit(1000)) tick(elapsed.Elapsed);
                return process.ExitCode;
            }
        }
        public void CheckDvsReady()
        {
            Installation installation = Installation.Locate();
            string executable = Path.Combine(installation.DirectoryPath, "dvs_service.exe");
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            {
                string[] pending = key == null ? null : key.GetValue("PendingFileRenameOperations") as string[];
                if (pending != null && pending.Any(p => String.Equals(p.Replace(@"\??\", "").TrimStart('!'), executable, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Windows отложила обновление сервиса DVS до перезагрузки. Перезагрузите ПК и снова нажмите «Сделать красиво». Замена файлов не запускалась.");
            }
        }
        public void Repair(IProgress<RepairProgress> progress) { Repairer.Run(progress); }
    }
}
