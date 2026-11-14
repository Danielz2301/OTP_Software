using System.Globalization;
using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class ConvertToLogDateTimeTests
{
    private readonly LogParserService _parser = new();

    private static void InvokeWithCulture(string cultureName, Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ParsesIsoFormatWithMillisecondComponent()
    {
        var d = _parser.ParseLogDateTime("2026-11-06 14:32:36.123");
        Assert.NotNull(d);
        Assert.Equal(2026, d.Value.Year);
        Assert.Equal(11, d.Value.Month);
        Assert.Equal(6, d.Value.Day);
        Assert.Equal(14, d.Value.Hour);
        Assert.Equal(32, d.Value.Minute);
        Assert.Equal(36, d.Value.Second);
        Assert.Equal(123, d.Value.Millisecond);
    }

    [Fact]
    public void ParsesDateOnlyOrDateTimeWithoutSeconds()
    {
        var d1 = _parser.ParseLogDateTime("2026-10-23");
        Assert.NotNull(d1);
        Assert.Equal(23, d1.Value.Day);

        var d2 = _parser.ParseLogDateTime("2026-10-23 04:00");
        Assert.NotNull(d2);
        Assert.Equal(4, d2.Value.Hour);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void IsoResultDoesNotChangeWithHostCulture(string cultureName)
    {
        InvokeWithCulture(cultureName, () =>
        {
            var d = _parser.ParseLogDateTime("2026-11-06 14:32:36.123");
            Assert.NotNull(d);
            Assert.Equal(11, d.Value.Month);
            Assert.Equal(6, d.Value.Day);
        });
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("2026-13-45")]
    public void UnparseableStringReturnsNullWithoutThrowing(string? input)
    {
        var result = _parser.ParseLogDateTime(input);
        Assert.Null(result);
    }

    [Fact]
    public void NonIsoFormatsAreRejectedUnlessCurrentCultureIsAllowed()
    {
        var result = _parser.ParseLogDateTime("06/11/2026", allowCurrentCulture: false);
        Assert.Null(result);
    }

    [Fact]
    public void NonIsoFormatWithAllowCurrentCultureParsesAccordingToUserCulture()
    {
        DateTime? gbResult = null;
        DateTime? usResult = null;

        InvokeWithCulture("en-GB", () =>
        {
            gbResult = _parser.ParseLogDateTime("06/11/2026", allowCurrentCulture: true);
        });

        InvokeWithCulture("en-US", () =>
        {
            usResult = _parser.ParseLogDateTime("06/11/2026", allowCurrentCulture: true);
        });

        Assert.NotNull(gbResult);
        Assert.Equal(11, gbResult.Value.Month); // Nov 6

        Assert.NotNull(usResult);
        Assert.Equal(6, usResult.Value.Month);  // June 11
    }
}
