using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ManualRecordingNamePolicyTests
{
    [Fact]
    public void Placeholder_UsesDateAndTime()
    {
        string value = ManualRecordingNamePolicy.CreatePlaceholder(
            new DateTime(2026, 10, 21, 13, 14, 15));

        Assert.Equal("MAN_20261021_131415", value);
        Assert.False(RecordingStartOrderPolicy.ShouldHandle(value));
    }

    [Theory]
    [InlineData("", "20261021_131415_发货.mkv")]
    [InlineData("MAN_20261021_131415", "20261021_131415_发货.mkv")]
    [InlineData("JD000123", "JD000123_20261021_131415_发货.mkv")]
    public void FileName_UsesDateAndTimeWhenNoTrackingNumber(
        string orderId,
        string expected)
    {
        string value = ManualRecordingNamePolicy.BuildFileName(
            orderId,
            new DateTime(2026, 10, 21, 13, 14, 15),
            "发货");

        Assert.Equal(expected, value);
    }
}
