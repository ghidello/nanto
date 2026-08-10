using AwesomeAssertions;

namespace Nanto.Testing.Tests;

public sealed class FailurePlanTests
{
    [Fact]
    public void StrictPlanMatchesOperationsInOrderAndTriggersScriptedFailure()
    {
        var failure = new InvalidOperationException("planned");
        var plan = new FailurePlan(
        [
            new FailurePlanStep { Operation = "first" },
            new FailurePlanStep { Operation = "second", Failure = failure },
        ]);

        plan.Observe("first");
        var action = () => plan.Observe("second");

        action.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);
        plan.RemainingSteps.Should().Be(0);
        plan.ObservedOperations.Should().Equal("first", "second");
        plan.VerifyComplete();
    }

    [Fact]
    public void StrictPlanDiagnosesUnexpectedOperationAndExhaustion()
    {
        var plan = new FailurePlan([new FailurePlanStep { Operation = "expected" }]);

        var unexpected = () => plan.Observe("other");
        unexpected.Should().Throw<InvalidOperationException>().WithMessage("*expected*other*");

        plan.Observe("expected");
        var exhausted = () => plan.Observe("extra");
        exhausted.Should().Throw<InvalidOperationException>().WithMessage("*exhausted*");
    }

    [Fact]
    public void VerificationReportsUnobservedOperations()
    {
        var plan = new FailurePlan([new FailurePlanStep { Operation = "pending" }]);

        var action = plan.VerifyComplete;

        action.Should().Throw<InvalidOperationException>().WithMessage("*1 unobserved*");
    }
}