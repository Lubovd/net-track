using System.Text;

namespace NetTrack.Core.Locking;

public enum CollectorLockStatus
{
    Acquired,
    AlreadyHeld,
    AccessError
}

public sealed record CollectorLockAcquireResult(
    CollectorLockStatus Status,
    CollectorFileLock? LockHandle,
    string? ErrorMessage);

/// <summary>
/// 基于数据目录内 collector.lock 独占句柄的单实例采集写入锁。
/// 仅允许在启动采集时获取；只读查询端禁止打开或探测此文件。
/// </summary>
public sealed class CollectorFileLock : IDisposable
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    private FileStream? _stream;
    private bool _disposed;

    public string LockFilePath { get; }

    private CollectorFileLock(string lockFilePath, FileStream stream)
    {
        LockFilePath = lockFilePath;
        _stream = stream;
    }

    /// <summary>
    /// 尝试独占获取采集写入锁，并严格区分「锁被占用」与「其他文件访问错误」。
    /// </summary>
    public static CollectorLockAcquireResult TryAcquire(string lockFilePath, string entryName)
    {
        try
        {
            var dir = Path.GetDirectoryName(lockFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.None);

            // 写入诊断信息（仅作排障参考，不用于判锁）
            try
            {
                stream.SetLength(0);
                var info = $"pid={Environment.ProcessId}\nentry={entryName}\nstarted_utc={DateTimeOffset.UtcNow:O}\n";
                var bytes = Encoding.UTF8.GetBytes(info);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch
            {
                // 诊断文本写入失败不影响锁持有本身
            }

            return new CollectorLockAcquireResult(
                CollectorLockStatus.Acquired,
                new CollectorFileLock(lockFilePath, stream),
                null);
        }
        catch (IOException ex) when (IsSharingOrLockViolation(ex))
        {
            return new CollectorLockAcquireResult(
                CollectorLockStatus.AlreadyHeld,
                null,
                "已有采集实例正在运行。");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new CollectorLockAcquireResult(
                CollectorLockStatus.AccessError,
                null,
                $"无法访问采集锁文件（权限不足）：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new CollectorLockAcquireResult(
                CollectorLockStatus.AccessError,
                null,
                $"无法打开采集锁文件：{ex.Message}");
        }
    }

    private static bool IsSharingOrLockViolation(IOException ex) =>
        ex.HResult == ErrorSharingViolation || ex.HResult == ErrorLockViolation;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 释放锁句柄时不抛出异常
        }
        finally
        {
            _stream = null;
        }
    }
}
