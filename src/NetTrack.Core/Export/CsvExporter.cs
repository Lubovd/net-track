using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NetTrack.Core.Configuration;
using NetTrack.Core.Reporting;

namespace NetTrack.Core.Export;

/// <summary>
/// 按固定 8 列导出 UTF-8 with BOM CSV，含 RFC 4180 转义、Excel 公式注入防护与内部文件覆盖保护。
/// </summary>
public static class CsvExporter
{
    public const string HeaderLine = "date_bj,hour_bj,app,domain,node,upload_bytes,download_bytes,total_bytes";

    /// <summary>
    /// 校验 CSV 导出路径，严禁覆盖 NetTrack 的数据库、WAL/SHM/Journal、配置文件、锁文件或日志目录内文件。
    /// 同时兼容 Windows \\?\、\\.\、8.3 短路径、符号链接、目录联接与硬链接等价路径识别，
    /// 即使内部文件或 logs 目录尚未创建，也能通过父目录 OS 文件身份拦截经由目录联接写入内部文件名。
    /// </summary>
    public static void ValidateOutputPath(
        string outputPath,
        NetTrackPaths? protectedPaths = null,
        Func<string, (uint VolumeSerial, ulong FileIndex)?>? fileIdentityResolver = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("导出文件路径不能为空。", nameof(outputPath));
        }

        var fullOut = NormalizePathForCompare(outputPath);
        if (Directory.Exists(outputPath) || Directory.Exists(fullOut))
        {
            throw new InvalidOperationException($"导出目标不能是已存在的目录：{outputPath}");
        }

        if (protectedPaths is null)
        {
            return;
        }

        var resolveId = fileIdentityResolver ?? TryGetWindowsFileIdentity;
        var outIdentity = resolveId(outputPath) ?? resolveId(fullOut);

        var protectedFiles = new[]
        {
            protectedPaths.DatabasePath,
            protectedPaths.DatabasePath + "-wal",
            protectedPaths.DatabasePath + "-shm",
            protectedPaths.DatabasePath + "-journal",
            protectedPaths.SettingsPath,
            protectedPaths.LockFilePath
        };

        foreach (var pf in protectedFiles)
        {
            if (string.Equals(fullOut, NormalizePathForCompare(pf), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"拒绝覆盖 NetTrack 内部文件：{Path.GetFileName(pf)}");
            }

            if (outIdentity is not null)
            {
                var pfIdentity = resolveId(pf);
                if (pfIdentity is not null && pfIdentity.Value == outIdentity.Value)
                {
                    throw new InvalidOperationException($"拒绝通过等价路径覆盖 NetTrack 内部文件：{Path.GetFileName(pf)}");
                }
            }
        }

        var dataDir = NormalizePathForCompare(protectedPaths.DataDirectory);
        if (string.Equals(fullOut, dataDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"导出目标不能指向数据目录本身：{protectedPaths.DataDirectory}");
        }

        var logsDir = NormalizePathForCompare(protectedPaths.LogsDirectory);
        if (string.Equals(fullOut, logsDir, StringComparison.OrdinalIgnoreCase) ||
            fullOut.StartsWith(logsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝将 CSV 导出到 NetTrack 内部日志目录。");
        }

        // 核对已存在父目录与 DataDirectory 的 OS 身份：
        // 即使内部文件（如 traffic.sqlite3）或 logs 目录尚未创建，只要目标父目录与 DataDirectory 指向同一目录（如目录联接/短路径/subst），即拒绝写入内部文件名
        var dataDirIdentity = resolveId(protectedPaths.DataDirectory);
        if (dataDirIdentity is not null)
        {
            var protectedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFileName(protectedPaths.DatabasePath),
                Path.GetFileName(protectedPaths.DatabasePath) + "-wal",
                Path.GetFileName(protectedPaths.DatabasePath) + "-shm",
                Path.GetFileName(protectedPaths.DatabasePath) + "-journal",
                Path.GetFileName(protectedPaths.SettingsPath),
                Path.GetFileName(protectedPaths.LockFilePath),
                Path.GetFileName(protectedPaths.LogsDirectory)
            };

            var outParent = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(outParent))
            {
                var outParentIdentity = resolveId(outParent);
                if (outParentIdentity is not null && outParentIdentity.Value == dataDirIdentity.Value)
                {
                    var targetName = Path.GetFileName(fullOut);
                    if (protectedFileNames.Contains(targetName))
                    {
                        throw new InvalidOperationException($"拒绝通过等价目录路径写入 NetTrack 内部文件：{targetName}");
                    }
                }
            }

            // 沿目标路径的祖先目录向上排查：若某个祖先指向 DataDirectory，且其下一级子目录名为 logs，则即使 logs 尚未创建也拒绝写入
            var logsDirName = Path.GetFileName(protectedPaths.LogsDirectory);
            var child = fullOut;
            var probe = Path.GetDirectoryName(fullOut);
            while (!string.IsNullOrEmpty(probe))
            {
                var dirIdentity = resolveId(probe);
                if (dirIdentity is not null && dirIdentity.Value == dataDirIdentity.Value)
                {
                    var directChildName = Path.GetFileName(child);
                    if (string.Equals(directChildName, logsDirName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("拒绝通过等价目录路径将 CSV 导出到 NetTrack 内部日志目录。");
                    }
                    break;
                }

                var parent = Path.GetDirectoryName(probe);
                if (string.Equals(parent, probe, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                child = probe;
                probe = parent;
            }
        }

        // 若日志目录已存在，沿目标路径的已存在祖先目录逐级核对 logs 目录本身的 OS 身份（防直接联接到 logs 目录）
        var logsIdentity = resolveId(protectedPaths.LogsDirectory);
        if (logsIdentity is not null)
        {
            var probe = Path.GetDirectoryName(fullOut);
            while (!string.IsNullOrEmpty(probe))
            {
                var dirIdentity = resolveId(probe);
                if (dirIdentity is not null && dirIdentity.Value == logsIdentity.Value)
                {
                    throw new InvalidOperationException("拒绝通过等价路径将 CSV 导出到 NetTrack 内部日志目录。");
                }

                var parent = Path.GetDirectoryName(probe);
                if (string.Equals(parent, probe, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                probe = parent;
            }
        }
    }

    public static void WriteToFile(string outputPath, IReadOnlyList<CsvExportRow> rows, NetTrackPaths? protectedPaths = null)
    {
        ValidateOutputPath(outputPath, protectedPaths);

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var writer = new StreamWriter(
            outputPath,
            append: false,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.NewLine = "\r\n";

        WriteToTextWriter(writer, rows);
    }

    private static string NormalizePathForCompare(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            full = @"\\" + full.Substring(8);
        }
        else if (full.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                 full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            full = full.Substring(4);
        }

        return Path.GetFullPath(full)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    private static (uint VolumeSerial, ulong FileIndex)? TryGetWindowsFileIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        const uint fileReadAttributes = 0x00000080;
        const uint fileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;

        using var handle = CreateFileW(
            path,
            fileReadAttributes,
            fileShareAll,
            IntPtr.Zero,
            openExisting,
            fileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return (info.VolumeSerialNumber, index);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    public static void WriteToTextWriter(TextWriter writer, IReadOnlyList<CsvExportRow> rows)
    {
        writer.WriteLine(HeaderLine);
        foreach (var row in rows)
        {
            writer.Write(row.DateBj);
            writer.Write(',');
            writer.Write(row.HourBj);
            writer.Write(',');
            writer.Write(EscapeTextCell(row.App));
            writer.Write(',');
            writer.Write(EscapeTextCell(row.Domain));
            writer.Write(',');
            writer.Write(EscapeTextCell(row.Node));
            writer.Write(',');
            writer.Write(row.UploadBytes.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(row.DownloadBytes.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(row.TotalBytes.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// 文本字段转义：防公式注入 + RFC 4180 引号转义。
    /// </summary>
    public static string EscapeTextCell(string? raw)
    {
        var value = raw ?? string.Empty;
        if (NeedsFormulaGuard(value))
        {
            value = "'" + value;
        }

        var needsQuotes = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        if (!needsQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static bool NeedsFormulaGuard(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (IsFormulaTriggerChar(value[0]))
        {
            return true;
        }

        var trimmed = value.TrimStart();
        return trimmed.Length > 0 && IsFormulaTriggerChar(trimmed[0]);
    }

    private static bool IsFormulaTriggerChar(char ch) =>
        ch is '=' or '+' or '-' or '@' or '\t' or '\r';
}
