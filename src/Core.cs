using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace DvsRepair
{
    internal sealed class PayloadFile
    {
        internal string RelativePath;
        internal string Sha256;
        internal long Length;
        internal PayloadFile(string path, string hash, long length) { RelativePath = path; Sha256 = hash; Length = length; }
    }

    internal sealed class Installation
    {
        internal string DirectoryPath;
        internal string ServiceName;

        internal static string ExecutableFromImagePath(string imagePath)
        {
            string value = Environment.ExpandEnvironmentVariables((imagePath ?? "").Trim());
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = value.IndexOf('"', 1);
                return end > 1 ? value.Substring(1, end - 1) : "";
            }
            int exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd < 0 ? "" : value.Substring(0, exeEnd + 4);
        }

        internal static bool IsAudinateFolder(string folder)
        {
            if (String.IsNullOrWhiteSpace(folder)) return false;
            string normalized = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            return normalized.EndsWith(Path.Combine("Audinate", "Dante Virtual Soundcard"), StringComparison.OrdinalIgnoreCase)
                && String.Equals(new DirectoryInfo(normalized).Parent.Name, "Audinate", StringComparison.OrdinalIgnoreCase);
        }

        internal static Installation Locate()
        {
            var candidates = new List<Installation>();
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey services = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (services == null) continue;
                    foreach (string name in services.GetSubKeyNames())
                    {
                        using (RegistryKey service = services.OpenSubKey(name))
                        {
                            if (service == null) continue;
                            string displayName = Convert.ToString(service.GetValue("DisplayName"));
                            if (!String.Equals(name, "dvs.manager", StringComparison.OrdinalIgnoreCase)
                                && !String.Equals(displayName, "Dante Virtual Soundcard Manager", StringComparison.OrdinalIgnoreCase)) continue;
                            string executable = ExecutableFromImagePath(Convert.ToString(service.GetValue("ImagePath")));
                            if (!String.Equals(Path.GetFileName(executable), "dvs_service.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            string folder = Path.GetDirectoryName(executable);
                            if (IsAudinateFolder(folder) && File.Exists(executable))
                                return new Installation { DirectoryPath = folder, ServiceName = name };
                            candidates.Add(new Installation { DirectoryPath = folder, ServiceName = name });
                        }
                    }
                }
            }
            string[] roots = {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramW6432")
            };
            foreach (string root in roots.Where(r => !String.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string folder = Path.Combine(root, "Audinate", "Dante Virtual Soundcard");
                if (File.Exists(Path.Combine(folder, "dvs_service.exe")))
                {
                    Installation service = candidates.FirstOrDefault(c => String.Equals(c.DirectoryPath, folder, StringComparison.OrdinalIgnoreCase));
                    if (service != null) return service;
                }
            }
            throw new InvalidOperationException("Dante Virtual Soundcard не найдена. Установите DVS и запустите программу снова. Ожидается папка Audinate и зарегистрированная служба Dante Virtual Soundcard Manager.");
        }
    }

    internal static class Files
    {
        internal static string Hash(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static void Check(string path, PayloadFile spec)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != spec.Length || !String.Equals(Hash(path), spec.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Проверка целостности не пройдена: " + spec.RelativePath);
        }

        internal static void NoLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Обнаружена ссылка вместо обычной папки или файла: " + current);
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        internal static string Under(string root, string relative)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("Недопустимое имя встроенного файла.");
            string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Недопустимый путь встроенного файла.");
            return result;
        }

        internal static void AtomicCopy(string source, string destination)
        {
            NoLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temp = destination + ".dvsrepair-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temp, false);
                File.SetAttributes(temp, FileAttributes.Normal);
                if (File.Exists(destination)) File.Replace(temp, destination, null, true);
                else File.Move(temp, destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static void PrivateDirectory(string path)
        {
            NoLinks(path);
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            foreach (SecurityIdentifier sid in new[] { user, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(path, security);
            Directory.SetAccessControl(path, security);
        }
    }

    internal static class Payload
    {
        internal static void Extract(string root)
        {
            Directory.CreateDirectory(root);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DvsRepair.Payload.zip"))
            {
                if (stream == null) throw new InvalidDataException("Встроенные файлы не найдены.");
                using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        PayloadFile spec = BuildInfo.Files.FirstOrDefault(f => String.Equals(f.RelativePath, entry.FullName, StringComparison.Ordinal));
                        if (spec == null || !found.Add(entry.FullName) || spec.Length != entry.Length) throw new InvalidDataException("Повреждён состав встроенного архива.");
                        string destination = Files.Under(root, entry.FullName);
                        Files.NoLinks(destination);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (Stream source = entry.Open())
                        using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                        Files.Check(destination, spec);
                    }
                }
            }
            if (found.Count != BuildInfo.Files.Length) throw new InvalidDataException("Встроенный архив неполный.");
        }
    }

    internal sealed class BackupLayout
    {
        internal readonly string ServiceRoot;
        internal readonly string LicensingRoot;

        internal BackupLayout(string install, string licensing, string runId)
        {
            if (String.IsNullOrEmpty(runId) || runId.Length > 80 || runId.Any(c => !Char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                throw new InvalidDataException("Недопустимое имя резервной копии.");
            ServiceRoot = Files.Under(install, "backup/" + runId);
            LicensingRoot = Files.Under(licensing, "backup/" + runId);
        }

        internal void Create()
        {
            if (Directory.Exists(ServiceRoot) || Directory.Exists(LicensingRoot))
                throw new IOException("Папка этой резервной копии уже существует. Предыдущие копии сохранены.");
            Files.PrivateDirectory(ServiceRoot);
            Files.PrivateDirectory(LicensingRoot);
        }
    }

    internal sealed class Replacement
    {
        internal PayloadFile Spec;
        internal string Source;
        internal string Target;
        internal string Backup;
        internal bool Existed;
        internal string OriginalHash;
    }

    internal sealed class FileTransaction
    {
        internal readonly List<Replacement> Items = new List<Replacement>();
        private readonly List<Replacement> changed = new List<Replacement>();
        private readonly Action<string> log;
        private readonly BackupLayout backups;
        internal FileTransaction(string stage, string install, string licensing, BackupLayout backup, Action<string> logger)
        {
            log = logger;
            backups = backup;
            foreach (PayloadFile file in BuildInfo.Files)
            {
                bool isService = file.RelativePath == "dvs_service.exe";
                string relative = isService ? file.RelativePath : file.RelativePath.Substring("Licensing/".Length);
                Items.Add(new Replacement {
                    Spec = file, Source = Files.Under(stage, file.RelativePath),
                    Target = Files.Under(isService ? install : licensing, relative),
                    Backup = Files.Under(isService ? backup.ServiceRoot : backup.LicensingRoot, relative)
                });
            }
        }

        internal void BackupAll()
        {
            var index = new StringBuilder("Target\tExisted\tSHA256\tBackup\r\n");
            foreach (Replacement item in Items)
            {
                Files.NoLinks(item.Target);
                Files.NoLinks(item.Backup);
                Files.Check(item.Source, item.Spec);
                if (Directory.Exists(item.Target)) throw new IOException("Вместо файла обнаружена папка: " + item.Target);
                item.Existed = File.Exists(item.Target);
                if (item.Existed)
                {
                    item.OriginalHash = Files.Hash(item.Target);
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Backup));
                    File.Copy(item.Target, item.Backup, false);
                    if (Files.Hash(item.Backup) != item.OriginalHash) throw new IOException("Не удалось проверить резервную копию: " + item.Target);
                }
                index.Append(item.Target).Append('\t').Append(item.Existed).Append('\t').Append(item.OriginalHash ?? "").Append('\t').Append(item.Backup).Append("\r\n");
                log("BACKUP " + item.Target + " (existed=" + item.Existed + ")");
            }
            File.WriteAllText(Path.Combine(backups.ServiceRoot, "backup-index.tsv"), index.ToString(), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(backups.LicensingRoot, "backup-index.tsv"), index.ToString(), new UTF8Encoding(true));
        }

        internal void Apply(Action<int> progress, Action<int> afterWrite)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Replacement item = Items[i];
                Files.Check(item.Source, item.Spec);
                changed.Add(item);
                Files.AtomicCopy(item.Source, item.Target);
                Files.Check(item.Target, item.Spec);
                log("INSTALLED " + item.Target + " SHA256=" + item.Spec.Sha256);
                progress(i + 1);
                if (afterWrite != null) afterWrite(i + 1);
            }
        }

        internal void Rollback()
        {
            var failures = new List<string>();
            foreach (Replacement item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    Files.NoLinks(item.Target);
                    if (item.Existed)
                    {
                        if (Files.Hash(item.Backup) != item.OriginalHash) throw new IOException("Резервная копия повреждена.");
                        if (!File.Exists(item.Target) || Files.Hash(item.Target) != item.OriginalHash) Files.AtomicCopy(item.Backup, item.Target);
                        if (Files.Hash(item.Target) != item.OriginalHash) throw new IOException("Проверка восстановления не пройдена.");
                    }
                    else if (File.Exists(item.Target)) File.Delete(item.Target);
                    log("RESTORED " + item.Target);
                }
                catch (Exception ex) { failures.Add(item.Target + ": " + ex.Message); }
            }
            if (failures.Count > 0) throw new IOException("Не удалось полностью восстановить файлы:\r\n" + String.Join("\r\n", failures));
        }
    }

    internal sealed class RepairProgress
    {
        internal int Percent;
        internal string Title;
        internal string Detail;
        internal RepairProgress(int percent, string title, string detail) { Percent = percent; Title = title; Detail = detail; }
    }

    internal sealed class RepairEngine
    {
        internal string BackupPath;
        internal string LicensingBackupPath;
        internal string LogPath;
        private StreamWriter writer;
        private void Log(string message)
        {
            // A log write failure must never prevent rollback of installed files.
            try { if (writer != null) { writer.WriteLine(DateTime.Now.ToString("O") + " " + message); writer.Flush(); } }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (UnauthorizedAccessException) { }
        }
        internal static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        internal void Run(IProgress<RepairProgress> progress)
        {
            if (!IsAdministrator()) throw new UnauthorizedAccessException("Для замены системных файлов нужны права администратора.");
            Installation install = Installation.Locate();
            string licensing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Audinate", "Licensing");
            Files.NoLinks(install.DirectoryPath);
            Files.NoLinks(licensing);
            var backups = new BackupLayout(install.DirectoryPath, licensing, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            backups.Create();
            BackupPath = backups.ServiceRoot;
            LicensingBackupPath = backups.LicensingRoot;
            LogPath = Path.Combine(BackupPath, "repair.log");
            string stage = Path.Combine(BackupPath, "staging");
            FileTransaction transaction = null;
            bool wasRunning = false;
            bool serviceTouched = false;
            bool rollbackOk = true;
            bool committed = false;
            using (writer = new StreamWriter(LogPath, false, new UTF8Encoding(true)))
            using (ServiceController service = new ServiceController(install.ServiceName))
            {
                try
                {
                    Log("DVS Repair v1.0; installed=" + install.DirectoryPath + "; service=" + install.ServiceName);
                    progress.Report(new RepairProgress(5, "Проверяем встроенные файлы", "Подготовка и проверка SHA-256."));
                    Payload.Extract(stage);
                    transaction = new FileTransaction(stage, install.DirectoryPath, licensing, backups, Log);
                    service.Refresh();
                    if (service.Status == ServiceControllerStatus.StartPending) service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    if (service.Status == ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Running && service.Status != ServiceControllerStatus.Stopped)
                        throw new InvalidOperationException("Служба DVS находится в состоянии " + service.Status + ". Дождитесь её запуска или остановки и повторите попытку.");
                    wasRunning = service.Status == ServiceControllerStatus.Running;
                    if (wasRunning && service.DependentServices.Any(d => d.Status != ServiceControllerStatus.Stopped))
                        throw new InvalidOperationException("От DVS зависят другие запущенные службы. Остановите их перед повторной попыткой.");
                    try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "Taskmgr.exe")) { UseShellExecute = true }); Log("Task Manager opened."); }
                    catch (Exception ex) { Log("Task Manager could not be opened: " + ex.Message); }
                    progress.Report(new RepairProgress(18, "Останавливаем DVS Manager", "Ожидаем остановки службы Windows."));
                    if (wasRunning)
                    {
                        serviceTouched = true;
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                    Log("Service stopped.");
                    progress.Report(new RepairProgress(32, "Сохраняем резервную копию", "Исходные файлы будут доступны для восстановления."));
                    transaction.BackupAll();
                    progress.Report(new RepairProgress(45, "Заменяем файлы", "dvs_service.exe и папка Audinate\\Licensing."));
                    transaction.Apply(count => progress.Report(new RepairProgress(45 + count * 10, "Заменяем файлы · " + count + " / " + BuildInfo.Files.Length, "Проверяем каждый скопированный файл.")), null);
                    if (wasRunning)
                    {
                        progress.Report(new RepairProgress(92, "Запускаем DVS Manager", "Возвращаем службу в исходное состояние."));
                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }
                    committed = true;
                    Log("SUCCESS; original service state restored; service backup=" + BackupPath + "; licensing backup=" + LicensingBackupPath);
                    progress.Report(new RepairProgress(100, "Файлы заменены. Требуется перезагрузка", "Обязательно перезагрузите ПК. Исходные файлы сохранены в backup."));
                }
                catch (Exception original)
                {
                    Log("ERROR " + original);
                    progress.Report(new RepairProgress(0, "Восстанавливаем исходные файлы", "Выполнение прервано. Выполняется откат."));
                    var recoveryErrors = new List<string>();
                    try
                    {
                        service.Refresh();
                        if (serviceTouched && service.Status != ServiceControllerStatus.Stopped)
                        {
                            if (service.Status == ServiceControllerStatus.StartPending) service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                            service.Refresh();
                            if (service.Status == ServiceControllerStatus.Running) service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        }
                    }
                    catch (Exception ex) { recoveryErrors.Add("Остановка службы: " + ex.Message); rollbackOk = false; }
                    if (transaction != null && rollbackOk)
                    {
                        try { transaction.Rollback(); }
                        catch (Exception ex) { recoveryErrors.Add(ex.Message); rollbackOk = false; }
                    }
                    if (wasRunning && serviceTouched && rollbackOk)
                    {
                        try { service.Refresh(); if (service.Status == ServiceControllerStatus.Stopped) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); } }
                        catch (Exception ex) { recoveryErrors.Add("Запуск службы: " + ex.Message); }
                    }
                    string message = original.Message + "\r\n\r\n" + (rollbackOk ? "Исходные файлы сохранены или восстановлены." : "Требуется ручное восстановление из резервной копии.");
                    if (recoveryErrors.Count > 0) message += "\r\n" + String.Join("\r\n", recoveryErrors);
                    Log("Recovery: " + message);
                    throw new IOException(message, original);
                }
                finally
                {
                    try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
                    catch (Exception ex) { Log("Staging cleanup: " + ex.Message); }
                    try { File.WriteAllText(Path.Combine(BackupPath, "result.txt"), (committed ? "SUCCESS" : "FAILED") + "\r\nInstall: " + install.DirectoryPath + "\r\nService: " + install.ServiceName + "\r\nService originally running: " + wasRunning + "\r\nService backup: " + BackupPath + "\r\nLicensing backup: " + LicensingBackupPath + "\r\nLog: " + LogPath, new UTF8Encoding(true)); }
                    catch (Exception ex) { Log("Result file: " + ex.Message); }
                }
            }
            writer = null;
        }
    }
}
