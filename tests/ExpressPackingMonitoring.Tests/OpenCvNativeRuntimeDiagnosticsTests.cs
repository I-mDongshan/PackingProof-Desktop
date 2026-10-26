using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class OpenCvNativeRuntimeDiagnosticsTests
{
    [Theory]
    [InlineData(true, true, false, false, false, "packingproof-core-imgproc")]
    [InlineData(true, true, true, false, false, "slim-with-image-codecs")]
    [InlineData(true, true, true, true, true, "official-full")]
    [InlineData(true, true, true, true, false, "custom")]
    [InlineData(false, true, false, false, false, "unsupported")]
    public void ClassifiesNativeBuildFromExports(
        bool hasCore,
        bool hasImgproc,
        bool hasImageCodecs,
        bool hasDnn,
        bool hasVideoIo,
        string expected)
    {
        Assert.Equal(
            expected,
            OpenCvNativeRuntimeDiagnostics.ClassifyNativeBuild(
                hasCore,
                hasImgproc,
                hasImageCodecs,
                hasDnn,
                hasVideoIo));
    }

    [Fact]
    public void DescribesOnlyAvailableModules()
    {
        Assert.Equal(
            "core+imgproc",
            OpenCvNativeRuntimeDiagnostics.DescribeModules(false, false, false));
        Assert.Equal(
            "core+imgproc+imgcodecs+dnn",
            OpenCvNativeRuntimeDiagnostics.DescribeModules(true, true, false));
    }
}
