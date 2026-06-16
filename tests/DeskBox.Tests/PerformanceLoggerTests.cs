using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class PerformanceLoggerTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("enabled", true)]
    public void IsEnabledSetting_ParsesOptInValues(string? value, bool expected)
    {
        Assert.Equal(expected, PerformanceLogger.IsEnabledSetting(value));
    }
}
