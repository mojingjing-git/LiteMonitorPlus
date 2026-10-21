using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Gpu.Device;

public sealed record GpuDeviceTransition(
    RenderLifecycleState Previous,
    RenderLifecycleState Current,
    ulong DeviceGeneration,
    int RecoveryAttempt,
    string? Code);

public sealed class GpuDeviceLifecycle
{
    private readonly object _gate = new();
    private readonly int _maximumAutomaticRecoveryAttempts;
    private RenderLifecycleState _state = RenderLifecycleState.Disabled;
    private ulong _deviceGeneration;
    private int _recoveryAttempt;

    public GpuDeviceLifecycle(int maximumAutomaticRecoveryAttempts = 1)
    {
        if (maximumAutomaticRecoveryAttempts < 0 || maximumAutomaticRecoveryAttempts > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAutomaticRecoveryAttempts),
                "The frozen contract permits zero or one automatic recovery attempt.");
        }

        _maximumAutomaticRecoveryAttempts = maximumAutomaticRecoveryAttempts;
    }

    public RenderLifecycleState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public ulong DeviceGeneration
    {
        get
        {
            lock (_gate)
            {
                return _deviceGeneration;
            }
        }
    }

    public GpuDeviceTransition Probe()
    {
        lock (_gate)
        {
            RequireState(RenderLifecycleState.Disabled, RenderLifecycleState.Fallback);
            return Move(RenderLifecycleState.Probing, null);
        }
    }

    public GpuDeviceTransition Initialize()
    {
        lock (_gate)
        {
            RequireState(RenderLifecycleState.Probing);
            return Move(RenderLifecycleState.Initializing, null);
        }
    }

    public GpuDeviceTransition MarkRunning(string? adapterDiagnostic = null)
    {
        lock (_gate)
        {
            if (_state is not RenderLifecycleState.Initializing and
                not RenderLifecycleState.Recovering)
            {
                throw InvalidTransition(RenderLifecycleState.Initializing);
            }

            _deviceGeneration++;
            return Move(RenderLifecycleState.Running, adapterDiagnostic);
        }
    }

    public GpuDeviceTransition Suspend()
    {
        lock (_gate)
        {
            RequireState(RenderLifecycleState.Running);
            return Move(RenderLifecycleState.Suspended, "gpu_session_suspended");
        }
    }

    public GpuDeviceTransition Resume()
    {
        lock (_gate)
        {
            RequireState(RenderLifecycleState.Suspended);
            return Move(RenderLifecycleState.Running, null);
        }
    }

    public GpuDeviceTransition Fail(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        lock (_gate)
        {
            if (_state is RenderLifecycleState.Disabled or
                RenderLifecycleState.Fallback or
                RenderLifecycleState.Stopping)
            {
                throw InvalidTransition(RenderLifecycleState.Recovering);
            }

            if (_recoveryAttempt < _maximumAutomaticRecoveryAttempts)
            {
                _recoveryAttempt++;
                return Move(RenderLifecycleState.Recovering, code);
            }

            return Move(RenderLifecycleState.Fallback, code);
        }
    }

    public GpuDeviceTransition ResetRecoveryBudget()
    {
        lock (_gate)
        {
            if (_state is not RenderLifecycleState.Running)
            {
                throw InvalidTransition(RenderLifecycleState.Running);
            }

            _recoveryAttempt = 0;
            return new GpuDeviceTransition(_state, _state, _deviceGeneration, _recoveryAttempt, null);
        }
    }

    public GpuDeviceTransition BeginStop()
    {
        lock (_gate)
        {
            return Move(RenderLifecycleState.Stopping, "gpu_session_stopping");
        }
    }

    public GpuDeviceTransition CompleteStop()
    {
        lock (_gate)
        {
            RequireState(RenderLifecycleState.Stopping);
            _deviceGeneration = 0;
            return Move(RenderLifecycleState.Disabled, null);
        }
    }

    private GpuDeviceTransition Move(RenderLifecycleState next, string? code)
    {
        RenderLifecycleState previous = _state;
        _state = next;
        return new GpuDeviceTransition(previous, next, _deviceGeneration, _recoveryAttempt, code);
    }

    private void RequireState(params RenderLifecycleState[] allowed)
    {
        if (allowed.Contains(_state))
        {
            return;
        }

        throw InvalidTransition(allowed[0]);
    }

    private InvalidOperationException InvalidTransition(RenderLifecycleState expected) =>
        new($"GPU lifecycle transition from {_state} requires state {expected}.");
}
