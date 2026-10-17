using ExpressPackingMonitoring.Services.Gpu;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// GPU 旋转的逆映射：把每个输出像素映射回旋转前的源坐标。
/// 这组系数同时决定顶点着色器里的 UV 表达式，是"GPU 优先、CPU 回退"里 GPU 那一半的唯一真源；
/// 系数与 HLSL 由同一处生成，测试也核对这组系数在四个角上的取值。
///
/// 约定：正角度表示把源画面顺时针旋转。
/// </summary>
public sealed class GpuRotationTests
{
    [Theory]
    // 不旋转：目标角 == 源角
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 1, 0, 1, 0)]
    [InlineData(0, 0, 1, 0, 1)]
    [InlineData(0, 1, 1, 1, 1)]
    // 顺时针 90：源左下角转到目标左上角
    [InlineData(90, 0, 0, 0, 1)]
    [InlineData(90, 1, 0, 0, 0)]
    [InlineData(90, 0, 1, 1, 1)]
    [InlineData(90, 1, 1, 1, 0)]
    // 180：对角对调
    [InlineData(180, 0, 0, 1, 1)]
    [InlineData(180, 1, 0, 0, 1)]
    [InlineData(180, 0, 1, 1, 0)]
    [InlineData(180, 1, 1, 0, 0)]
    // 顺时针 270（即逆时针 90）：源右上角转到目标左上角
    [InlineData(270, 0, 0, 1, 0)]
    [InlineData(270, 1, 0, 1, 1)]
    [InlineData(270, 0, 1, 0, 0)]
    [InlineData(270, 1, 1, 0, 1)]
    public void InverseMapping_MatchesClockwiseRotation(
        int degrees,
        double cornerU,
        double cornerV,
        double expectedSourceU,
        double expectedSourceV)
    {
        var (a00, a01, b0, a10, a11, b1) = GpuConversionShaders.GetUvTransform(degrees);

        double sourceU = (a00 * cornerU) + (a01 * cornerV) + b0;
        double sourceV = (a10 * cornerU) + (a11 * cornerV) + b1;

        Assert.Equal(expectedSourceU, sourceU, precision: 6);
        Assert.Equal(expectedSourceV, sourceV, precision: 6);
    }

    /// <summary>非法角度按不旋转处理，绝不能因为一个坏值把画面转歪。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(45)]
    [InlineData(360)]
    [InlineData(999)]
    public void InvalidDegrees_FallBackToIdentity(int degrees)
    {
        var (a00, a01, b0, a10, a11, b1) = GpuConversionShaders.GetUvTransform(degrees);

        Assert.Equal(1.0, a00);
        Assert.Equal(0.0, a01);
        Assert.Equal(0.0, b0);
        Assert.Equal(0.0, a10);
        Assert.Equal(1.0, a11);
        Assert.Equal(0.0, b1);
    }

    /// <summary>HLSL 表达式由系数生成，这里钉住四个角度的实际文本，防止生成器被改坏。</summary>
    [Theory]
    [InlineData(0, "float2(corner.x, corner.y)")]
    [InlineData(90, "float2(corner.y, -corner.x + 1.0)")]
    [InlineData(180, "float2(-corner.x + 1.0, -corner.y + 1.0)")]
    [InlineData(270, "float2(-corner.y + 1.0, corner.x)")]
    public void UvExpression_IsGeneratedFromTheSameCoefficients(int degrees, string expected) =>
        Assert.Equal(expected, GpuConversionShaders.BuildRotatedUvExpression(degrees));

    [Fact]
    public void VertexShader_ContainsTheRotationExpression()
    {
        Assert.Contains(
            "output.uv = float2(corner.y, -corner.x + 1.0);",
            GpuConversionShaders.VertexShaderFor(90),
            StringComparison.Ordinal);
        Assert.Contains(
            "output.uv = float2(corner.x, corner.y);",
            GpuConversionShaders.VertexShaderFor(0),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 生成的 HLSL 必须真的能编译：角度写错会表现为画面方向不对，
    /// 语法写错则整路 GPU 采集起不来，两者都得在自动化里拦住。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void VertexShader_CompilesForEveryAngle(int degrees)
    {
        SharpGen.Runtime.Result result = Vortice.D3DCompiler.Compiler.Compile(
            GpuConversionShaders.VertexShaderFor(degrees),
            "main",
            string.Empty,
            "vs_4_0",
            out var code,
            out var errors);

        using (code)
        using (errors)
        {
            Assert.False(result.Failure, errors?.AsString() ?? "着色器编译失败");
            Assert.NotNull(code);
        }
    }
}
