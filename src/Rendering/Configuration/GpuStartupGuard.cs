using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiteMonitor.Rendering.Configuration;

public sealed class GpuStartupGuard
{
    private static readonly Lazy<GpuStartupGuard> SharedFactory = new(
        () => new GpuStartupGuard(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LiteMonitor++",
                "gpu-startup"),
            AppContext.BaseDirectory));

    private readonly object _gate = new();
    private readonly string _pendingPath;
    private readonly string _recoveryEvidencePath;
    private readonly HashSet<GpuStartupAttempt> _attempts = [];
    private bool _storageAvailable;
    private bool _conservative;
    private bool _attemptWritten;
    private string? _statusCode;
    private string? _statusMessage;

    public GpuStartupGuard(string stateDirectory, string installationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationIdentity);

        string digest = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    Path.GetFullPath(installationIdentity)
                        .ToUpperInvariant())));
        string stateRoot = Path.GetFullPath(stateDirectory);
        _pendingPath = Path.Combine(stateRoot, $"{digest}.pending");
        _recoveryEvidencePath = Path.Combine(stateRoot, $"{digest}.recovery-required");

        try
        {
            Directory.CreateDirectory(stateRoot);
            string probePath = Path.Combine(
                stateRoot,
                $".write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            _storageAvailable = true;

            if (File.Exists(_pendingPath))
            {
                _conservative = true;
                _statusCode = "gpu_startup_guard_previous_attempt_unclean";
                _statusMessage =
                    "The previous GPU attempt did not report its first successful frame.";
                File.Move(
                    _pendingPath,
                    _recoveryEvidencePath,
                    overwrite: true);
            }

            if (File.Exists(_recoveryEvidencePath))
            {
                _conservative = true;
                _statusCode ??=
                    "gpu_startup_guard_previous_attempt_unclean";
                _statusMessage ??=
                    "GPU rendering remains disabled until the user explicitly retries.";
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            _storageAvailable = false;
            _conservative = true;
            _statusCode = "gpu_startup_guard_storage_unavailable";
            _statusMessage =
                "GPU startup state cannot be recorded; automatic GPU rendering is disabled.";
        }
    }

    public static GpuStartupGuard Shared => SharedFactory.Value;

    public bool ConservativeForThisProcess
    {
        get
        {
            lock (_gate)
            {
                return _conservative;
            }
        }
    }

    public string? StatusCode
    {
        get
        {
            lock (_gate)
            {
                return _statusCode;
            }
        }
    }

    public string? StatusMessage
    {
        get
        {
            lock (_gate)
            {
                return _statusMessage;
            }
        }
    }

    public bool TryBeginAttempt(
        out GpuStartupAttempt? attempt,
        out string? errorCode,
        out string? errorMessage)
    {
        lock (_gate)
        {
            attempt = null;
            if (!_storageAvailable)
            {
                errorCode = "gpu_startup_guard_storage_unavailable";
                errorMessage =
                    _statusMessage ??
                    "GPU startup state cannot be recorded.";
                return false;
            }

            if (_conservative)
            {
                errorCode = _statusCode ??
                    "gpu_startup_guard_previous_attempt_unclean";
                errorMessage =
                    _statusMessage ??
                    "The previous GPU attempt was not clean.";
                return false;
            }

            try
            {
                if (!_attemptWritten)
                {
                    WritePendingMarker();
                    _attemptWritten = true;
                }

                attempt = new GpuStartupAttempt(this);
                _attempts.Add(attempt);
                errorCode = null;
                errorMessage = null;
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException)
            {
                _storageAvailable = false;
                _conservative = true;
                _statusCode = "gpu_startup_guard_storage_unavailable";
                _statusMessage = exception.Message;
                errorCode = _statusCode;
                errorMessage = exception.Message;
                return false;
            }
        }
    }

    public bool AllowExplicitManualRetry(
        out string? errorCode,
        out string? errorMessage)
    {
        lock (_gate)
        {
            if (!_storageAvailable)
            {
                errorCode = "gpu_startup_guard_storage_unavailable";
                errorMessage =
                    _statusMessage ??
                    "GPU startup state cannot be recorded.";
                return false;
            }

            try
            {
                if (File.Exists(_recoveryEvidencePath))
                {
                    File.Delete(_recoveryEvidencePath);
                }

                _conservative = false;
                _statusCode = null;
                _statusMessage = null;
                errorCode = null;
                errorMessage = null;
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException)
            {
                _storageAvailable = false;
                _conservative = true;
                _statusCode = "gpu_startup_guard_storage_unavailable";
                _statusMessage = exception.Message;
                errorCode = _statusCode;
                errorMessage = exception.Message;
                return false;
            }
        }
    }

    internal void MarkFirstFrame(GpuStartupAttempt attempt)
    {
        lock (_gate)
        {
            if (!_attempts.Contains(attempt))
            {
                return;
            }

            attempt.MarkSucceededCore();
            CompleteAttemptCore(attempt);
        }
    }

    internal void CompleteAttempt(GpuStartupAttempt attempt)
    {
        lock (_gate)
        {
            CompleteAttemptCore(attempt);
        }
    }

    private void CompleteAttemptCore(GpuStartupAttempt attempt)
    {
        if (!_attempts.Remove(attempt))
        {
            return;
        }

        if (_attempts.Count != 0)
        {
            return;
        }

        try
        {
            if (File.Exists(_pendingPath))
            {
                File.Delete(_pendingPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            _statusCode = "gpu_startup_guard_marker_cleanup_failed";
            _statusMessage = exception.Message;
        }
        finally
        {
            _attemptWritten = false;
        }
    }

    /// <summary>
    /// 【合同 §10 GPU-005 / 裁决 D6】放弃某个启动尝试，但**保留** pending 标记。
    /// <para>与 <see cref="CompleteAttemptCore"/> 的关键差别：后者在尝试集合清空后会
    /// <b>删除</b> pending 标记文件（那是"首帧成功"或"受控放弃且停止流程干净"的路径）；
    /// 本方法**只**把该尝试从集合里注销，<b>刻意不删除</b> pending 标记文件、
    /// 也<b>不复位</b> <c>_attemptWritten</c> ⇒ 该标记会在<b>下次进程启动</b>时被读到
    /// ⇒ 进入 conservative、等待用户显式重试（<c>AllowExplicitManualRetry</c>）。</para>
    /// <para>同时把该尝试从集合里移除是必要的：否则它会一直占着集合，
    /// 导致后续任何一次 <see cref="CompleteAttemptCore"/> 都因"集合非空"而提前返回、
    /// <b>永远清不掉</b> pending 标记 —— 哪怕之后真的成功了首帧。</para>
    /// </summary>
    internal void AbandonAttemptPreservingPending(GpuStartupAttempt attempt)
    {
        lock (_gate)
        {
            // ★ 只注销该尝试；**故意不碰** _pendingPath，也**故意不复位** _attemptWritten。
            _attempts.Remove(attempt);
        }
    }

    private void WritePendingMarker()
    {
        string temporaryPath = _pendingPath + ".tmp";
        string json = JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            startedAt = DateTimeOffset.UtcNow,
            executable = Path.GetFullPath(AppContext.BaseDirectory)
        });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _pendingPath, overwrite: true);
    }
}

public sealed class GpuStartupAttempt : IDisposable, IGpuStartupAttemptAbandon
{
    private readonly GpuStartupGuard _owner;
    private bool _succeeded;
    private int _disposed;

    internal GpuStartupAttempt(GpuStartupGuard owner)
    {
        _owner = owner;
    }

    public bool Succeeded => _succeeded;

    public void MarkFirstSuccessfulFrame()
    {
        ThrowIfDisposed();
        if (_succeeded)
        {
            return;
        }

        _owner.MarkFirstFrame(this);
    }

    internal void MarkSucceededCore() => _succeeded = true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.CompleteAttempt(this);
    }

    /// <summary>
    /// 【合同 §10 GPU-005 / 裁决 D6】放弃该尝试，但**保留** guard 的 pending 标记。
    /// <para>与 <see cref="Dispose"/> 的差别：<see cref="Dispose"/> 走
    /// <c>CompleteAttempt</c> ⇒ 尝试集合清空时 pending 标记**被删除**（正常路径）；
    /// 本方法只把该尝试注销，pending 标记**原样留在磁盘上** ⇒ 下次进程启动时
    /// guard 进入 conservative、等待用户显式重试。</para>
    /// <para>与 <see cref="Dispose"/> 一样是**幂等**的：已终结时直接返回。
    /// 终结后该尝试不可再标记成功（<c>ThrowIfDisposed</c>）。</para>
    /// </summary>
    public void AbandonPreservingPending()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.AbandonAttemptPreservingPending(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}
