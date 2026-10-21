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

public sealed class GpuStartupAttempt : IDisposable
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}
