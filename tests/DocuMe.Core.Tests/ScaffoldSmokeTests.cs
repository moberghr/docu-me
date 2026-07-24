using Shouldly;

namespace DocuMe.Core.Tests;

// Placeholder smoke test proving the xUnit + Shouldly stack is wired and green.
// Replaced by real Core tests once config/state land (PLAN.md §5.1, §5.3).
public class ScaffoldSmokeTests
{
    [Fact]
    public void TestHarness_IsWired()
    {
        var toolCommandName = "docume";

        toolCommandName.ShouldBe("docume");
    }
}
