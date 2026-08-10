using System.Runtime.ExceptionServices;

namespace Nanto.Testing;

public sealed class FailurePlan
{
    private readonly Lock _gate = new();
    private readonly List<string> _observedOperations = [];
    private readonly FailurePlanStep[] _steps;
    private readonly bool _strict;
    private int _nextStep;

    public int RemainingSteps
    {
        get
        {
            lock (_gate)
            {
                return _steps.Length - _nextStep;
            }
        }
    }

    public IReadOnlyList<string> ObservedOperations
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly([.. _observedOperations]);
            }
        }
    }

    internal string? LastObservedOperation
    {
        get
        {
            lock (_gate)
            {
                return _observedOperations.Count == 0 ? null : _observedOperations[^1];
            }
        }
    }

    public FailurePlan()
    {
        _steps = [];
    }

    public FailurePlan(IEnumerable<FailurePlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var snapshot = steps.ToArray();
        foreach (var step in snapshot)
        {
            ArgumentNullException.ThrowIfNull(step);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Operation);
        }

        _steps = snapshot;
        _strict = true;
    }

    public void Observe(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        FailurePlanStep? step;
        lock (_gate)
        {
            _observedOperations.Add(operation);
            if (!_strict)
            {
                return;
            }

            if (_nextStep >= _steps.Length)
            {
                throw new InvalidOperationException($"Unexpected operation '{operation}' after the failure plan was exhausted.");
            }

            step = _steps[_nextStep];
            if (!string.Equals(step.Operation, operation, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected operation '{step.Operation}' but observed '{operation}'.");
            }

            _nextStep++;
        }

        if (step.Failure is not null)
        {
            ExceptionDispatchInfo.Capture(step.Failure).Throw();
        }
    }

    public void VerifyComplete()
    {
        lock (_gate)
        {
            if (_nextStep != _steps.Length)
            {
                throw new InvalidOperationException($"The failure plan has {_steps.Length - _nextStep} unobserved operation(s).");
            }
        }
    }
}