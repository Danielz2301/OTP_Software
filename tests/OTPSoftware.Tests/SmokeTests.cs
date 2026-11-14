using OTPSoftware.Core.Models;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void TuningOptions_Defaults_MatchSpecification()
    {
        var tuning = new TuningOptions();

        Assert.Equal(2000, tuning.RealtimePollMs);
        Assert.Equal(3, tuning.ConsecutiveNgThreshold);
        Assert.Equal(30000, tuning.MaxRetainedUnits);
        Assert.Equal(2000, tuning.SlidingWindowTrimBatch);
    }

    [Fact]
    public void UnitRecord_IsNg_ReflectsErrorCount()
    {
        var okUnit = new UnitRecord { Index = 1, Pmnt = "PMNT1029A" };
        var ngUnit = new UnitRecord { Index = 2, Pmnt = "PMNT1029A", Errors = ["XYLV"] };

        Assert.False(okUnit.IsNg);
        Assert.True(ngUnit.IsNg);
    }
}

