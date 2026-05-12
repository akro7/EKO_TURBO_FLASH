using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EkoTurboTool
{
    public static class BackupService
    {
        // EKO TURBO BY AHMED YOUNIS
        public static readonly string BackupRoot =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EkoTurboBackups");

        private static string CorePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core");
        private static string AdbPath => Path.Combine(CorePath, "adb.exe");

        // ───────────────────────────────────────────────
        //  DEVICE DETECTION
        // ───────────────────────────────────────────────

        /// <summary>
        /// Returns the serial of the first authorized online device, or null if none found.
        /// </summary>
        public static async Task<string?> GetConnectedDeviceAsync(Action<string>? log)
        {
            var r = await RunRawAdbAsync("devices", 10000, log);
            if (r.Code != 0)
            {
                log?.Invoke("[ADB] Failed to run 'adb devices'.");
                return null;
            }

            var lines = r.Out.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool foundHeader = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                {
                    foundHeader = true;
                    continue;
                }

                if (!foundHeader || string.IsNullOrWhiteSpace(trimmed)) continue;

                var parts = trimmed.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var serial = parts[0];
                var state  = parts[1].ToLowerInvariant();

                if (state == "device")
                {
                    log?.Invoke($"[ADB] Device found: {serial}");
                    return serial;
                }
                else if (state == "unauthorized")
                {
                    log?.Invoke($"[ADB] Device {serial} is UNAUTHORIZED — allow USB debugging on the phone.");
                }
                else if (state == "offline")
                {
                    log?.Invoke($"[ADB] Device {serial} is OFFLINE — try reconnecting the cable.");
                }
                else
                {
                    log?.Invoke($"[ADB] Device {serial} state: {state}");
                }
            }

            log?.Invoke("[ADB] No authorized device found.");
            return null;
        }

        /// <summary>
        /// Waits up to timeoutMs for an authorized device to appear.
        /// </summary>
        public static async Task<string?> WaitForDeviceAsync(int timeoutMs, Action<string>? log)
        {
            log?.Invoke("[ADB] Waiting for device...");
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var serial = await GetConnectedDeviceAsync(null);
                if (serial != null)
                {
                    log?.Invoke($"[ADB] Device ready: {serial}");
                    return serial;
                }
                await Task.Delay(1500);
            }
            log?.Invoke("[ADB] Timeout: no device found.");
            return null;
        }

        // ───────────────────────────────────────────────
        //  PUBLIC API
        // ───────────────────────────────────────────────

        public static async Task<bool> CheckRootAsync(Action<string>? log)
        {
            var serial = await GetConnectedDeviceAsync(log);
            if (serial == null) return false;

            var r = await RunAdbAsync(serial, "shell su -c \"id\"", 20000, log);
            var txt = (r.Out + r.Err).ToLowerInvariant();
            return r.Code == 0 && txt.Contains("uid=0");
        }

        public static async Task<List<AppEntry>> GetInstalledAppsAsync(Action<string>? log)
        {
            var result = new List<AppEntry>();

            var serial = await GetConnectedDeviceAsync(log);
            if (serial == null) return result;

            var r = await RunAdbAsync(serial, "shell su -c \"pm list packages -3\"", 60000, log);
            if (r.Code != 0) return result;

            var lines = r.Out.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var p = line.Trim();
                if (!p.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;

                var pkg = p.Replace("package:", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (string.IsNullOrWhiteSpace(pkg)) continue;

                result.Add(new AppEntry { PackageName = pkg, DisplayName = pkg });
            }

            return result.OrderBy(a => a.DisplayName).ToList();
        }

        public static async Task<bool> BackupAppAsync(
            string packageName,
            string displayName,
            BackupOptions options,
            Action<string>? log)
        {
            var serial = await GetConnectedDeviceAsync(log);
            if (serial == null)
            {
                log?.Invoke("[Backup] Aborted: no device connected.");
                return false;
            }

            try
            {
                Directory.CreateDirectory(BackupRoot);
                var stamp    = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var localDir = Path.Combine(BackupRoot, packageName, stamp);
                Directory.CreateDirectory(localDir);

                var remoteTmp = $"/sdcard/EkoTurbo/tmp_{packageName}_{stamp}";
                await RunAdbAsync(serial, $"shell su -c \"mkdir -p {remoteTmp}\"", 15000, log);

                if (options.BackupApk)
                {
                    log?.Invoke($"[{packageName}] APK backup...");
                    var pmPath = await RunAdbAsync(serial, $"shell su -c \"pm path {packageName}\"", 30000, log);

                    var apkLines = pmPath.Out
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => l.Trim().StartsWith("package:", StringComparison.OrdinalIgnoreCase))
                        .Select(l => l.Trim().Replace("package:", "", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var apkLocalDir = Path.Combine(localDir, "apk");
                    Directory.CreateDirectory(apkLocalDir);

                    int idx = 0;
                    foreach (var remoteApk in apkLines)
                    {
                        idx++;
                        var localApk = Path.Combine(apkLocalDir, idx == 1 ? "base.apk" : $"split_{idx}.apk");
                        var pull = await RunAdbAsync(serial, $"pull \"{remoteApk}\" \"{localApk}\"", 120000, log);
                        if (pull.Code != 0) log?.Invoke($"[{packageName}] APK pull failed: {remoteApk}");
                    }
                }

                if (options.BackupData)
                {
                    log?.Invoke($"[{packageName}] DATA backup...");
                    var remoteTar = $"{remoteTmp}/data.tar";
                    var tar = await RunAdbAsync(serial,
                        $"shell su -c \"tar -cpf {remoteTar} /data/data/{packageName} 2>/dev/null\"",
                        180000, log);

                    if (tar.Code == 0)
                        await RunAdbAsync(serial, $"pull \"{remoteTar}\" \"{Path.Combine(localDir, "data.tar")}\"", 180000, log);
                    else
                        log?.Invoke($"[{packageName}] DATA not backed up.");
                }

                if (options.BackupUserDe)
                {
                    log?.Invoke($"[{packageName}] USER_DE backup...");
                    var remoteTar = $"{remoteTmp}/user_de.tar";
                    var tar = await RunAdbAsync(serial,
                        $"shell su -c \"tar -cpf {remoteTar} /data/user_de/0/{packageName} 2>/dev/null\"",
                        180000, log);

                    if (tar.Code == 0)
                        await RunAdbAsync(serial, $"pull \"{remoteTar}\" \"{Path.Combine(localDir, "user_de.tar")}\"", 180000, log);
                    else
                        log?.Invoke($"[{packageName}] USER_DE not backed up.");
                }

                if (options.BackupObb)
                {
                    log?.Invoke($"[{packageName}] OBB backup...");
                    var remoteTar = $"{remoteTmp}/obb.tar";
                    var tar = await RunAdbAsync(serial,
                        $"shell su -c \"tar -cpf {remoteTar} /sdcard/Android/obb/{packageName} 2>/dev/null\"",
                        180000, log);

                    if (tar.Code == 0)
                        await RunAdbAsync(serial, $"pull \"{remoteTar}\" \"{Path.Combine(localDir, "obb.tar")}\"", 180000, log);
                    else
                        log?.Invoke($"[{packageName}] OBB not backed up.");
                }

                var meta = new BackupMeta
                {
                    PackageName  = packageName,
                    DisplayName  = displayName,
                    BackupDate   = DateTime.Now,
                    BackupApk    = options.BackupApk,
                    BackupData   = options.BackupData,
                    BackupUserDe = options.BackupUserDe,
                    BackupObb    = options.BackupObb
                };

                await File.WriteAllTextAsync(
                    Path.Combine(localDir, "meta.json"),
                    JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

                await RunAdbAsync(serial, $"shell su -c \"rm -rf {remoteTmp}\"", 15000, log);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Backup exception: {ex.Message}");
                return false;
            }
        }

        public static async Task<List<BackupEntry>> GetBackupsAsync(Action<string>? log)
        {
            var result = new List<BackupEntry>();
            try
            {
                if (!Directory.Exists(BackupRoot)) return result;

                foreach (var packageDir in Directory.GetDirectories(BackupRoot))
                {
                    foreach (var backupDir in Directory.GetDirectories(packageDir))
                    {
                        var metaPath = Path.Combine(backupDir, "meta.json");
                        if (!File.Exists(metaPath)) continue;

                        var meta = JsonSerializer.Deserialize<BackupMeta>(await File.ReadAllTextAsync(metaPath));
                        if (meta == null) continue;

                        result.Add(new BackupEntry
                        {
                            BackupPath   = backupDir,
                            PackageName  = meta.PackageName,
                            DisplayName  = string.IsNullOrWhiteSpace(meta.DisplayName) ? meta.PackageName : meta.DisplayName,
                            BackupDate   = meta.BackupDate
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Read backups failed: {ex.Message}");
            }

            return result.OrderByDescending(b => b.BackupDate).ToList();
        }

        public static async Task<bool> RestoreBackupAsync(string backupPath, Action<string>? log)
        {
            var serial = await GetConnectedDeviceAsync(log);
            if (serial == null)
            {
                log?.Invoke("[Restore] Aborted: no device connected.");
                return false;
            }

            try
            {
                var metaPath = Path.Combine(backupPath, "meta.json");
                if (!File.Exists(metaPath)) { log?.Invoke("meta.json missing."); return false; }

                var meta = JsonSerializer.Deserialize<BackupMeta>(await File.ReadAllTextAsync(metaPath));
                if (meta == null) { log?.Invoke("Invalid meta.json."); return false; }

                log?.Invoke($"Restore package: {meta.PackageName}");

                var apkDir = Path.Combine(backupPath, "apk");
                if (Directory.Exists(apkDir))
                {
                    var apks = Directory.GetFiles(apkDir, "*.apk").OrderBy(f => f).ToList();
                    foreach (var apk in apks)
                    {
                        log?.Invoke($"Install APK: {Path.GetFileName(apk)}");
                        var install = await RunAdbAsync(serial, $"install -r \"{apk}\"", 180000, log);
                        if (install.Code != 0) { log?.Invoke("APK install failed."); return false; }
                    }
                }

                var remoteTmp = "/sdcard/EkoTurbo/restore_tmp";
                await RunAdbAsync(serial, $"shell su -c \"mkdir -p {remoteTmp}\"", 15000, log);

                var dataTar = Path.Combine(backupPath, "data.tar");
                if (File.Exists(dataTar))
                {
                    log?.Invoke("Restore DATA...");
                    await RunAdbAsync(serial, $"push \"{dataTar}\" \"{remoteTmp}/data.tar\"", 180000, log);
                    await RunAdbAsync(serial, $"shell su -c \"tar -xpf {remoteTmp}/data.tar -C /\"", 180000, log);
                    await RunAdbAsync(serial, $"shell su -c \"restorecon -R /data/data/{meta.PackageName}\"", 90000, log);
                }

                var userDeTar = Path.Combine(backupPath, "user_de.tar");
                if (File.Exists(userDeTar))
                {
                    log?.Invoke("Restore USER_DE...");
                    await RunAdbAsync(serial, $"push \"{userDeTar}\" \"{remoteTmp}/user_de.tar\"", 180000, log);
                    await RunAdbAsync(serial, $"shell su -c \"tar -xpf {remoteTmp}/user_de.tar -C /\"", 180000, log);
                    await RunAdbAsync(serial, $"shell su -c \"restorecon -R /data/user_de/0/{meta.PackageName}\"", 90000, log);
                }

                var obbTar = Path.Combine(backupPath, "obb.tar");
                if (File.Exists(obbTar))
                {
                    log?.Invoke("Restore OBB...");
                    await RunAdbAsync(serial, $"push \"{obbTar}\" \"{remoteTmp}/obb.tar\"", 180000, log);
                    await RunAdbAsync(serial, $"shell su -c \"tar -xpf {remoteTmp}/obb.tar -C /\"", 180000, log);
                }

                await RunAdbAsync(serial, $"shell su -c \"rm -rf {remoteTmp}\"", 15000, log);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Restore exception: {ex.Message}");
                return false;
            }
        }

        // ───────────────────────────────────────────────
        //  INTERNAL HELPERS
        // ───────────────────────────────────────────────

        /// <summary>Runs ADB targeting a specific device serial (-s flag).</summary>
        private static async Task<(int Code, string Out, string Err)> RunAdbAsync(
            string serial, string arguments, int timeoutMs, Action<string>? log)
        {
            return await RunRawAdbAsync($"-s {serial} {arguments}", timeoutMs, log);
        }

        /// <summary>Runs ADB without targeting a specific device (used for 'adb devices').</summary>
        private static async Task<(int Code, string Out, string Err)> RunRawAdbAsync(
            string arguments, int timeoutMs, Action<string>? log)
        {
            if (!File.Exists(AdbPath))
            {
                log?.Invoke("Error: adb.exe not found in core folder.");
                return (-1, "", "adb.exe missing");
            }

            return await RunProcessAsync(AdbPath, arguments, timeoutMs, log);
        }

        private static async Task<(int Code, string Out, string Err)> RunProcessAsync(
            string fileName, string args, int timeoutMs, Action<string>? log)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = fileName,
                    Arguments              = args,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };

                using var p = new Process { StartInfo = psi };
                var o = new StringBuilder();
                var e = new StringBuilder();

                p.OutputDataReceived += (_, ev) => { if (ev.Data != null) { o.AppendLine(ev.Data); log?.Invoke(ev.Data); } };
                p.ErrorDataReceived  += (_, ev) => { if (ev.Data != null) { e.AppendLine(ev.Data); log?.Invoke(ev.Data); } };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                using var cts = new CancellationTokenSource(timeoutMs);
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    return (-1, o.ToString(), $"Timeout {timeoutMs / 1000}s");
                }

                return (p.ExitCode, o.ToString(), e.ToString());
            }
            catch (Exception ex)
            {
                return (-1, "", ex.Message);
            }
        }
    }

    // ───────────────────────────────────────────────
    //  MODELS
    // ───────────────────────────────────────────────

    public class AppEntry
    {
        public string PackageName { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public class BackupOptions
    {
        public bool BackupApk    { get; set; }
        public bool BackupData   { get; set; }
        public bool BackupUserDe { get; set; }
        public bool BackupObb    { get; set; }
    }

    public class BackupMeta
    {
        public string   PackageName  { get; set; } = "";
        public string   DisplayName  { get; set; } = "";
        public DateTime BackupDate   { get; set; }
        public bool     BackupApk    { get; set; }
        public bool     BackupData   { get; set; }
        public bool     BackupUserDe { get; set; }
        public bool     BackupObb    { get; set; }
    }

    public class BackupEntry
    {
        public string   BackupPath   { get; set; } = "";
        public string   PackageName  { get; set; } = "";
        public string   DisplayName  { get; set; } = "";
        public DateTime BackupDate   { get; set; }
    }
}
