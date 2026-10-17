using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class EditionCoexistenceDetectorTests
{
    [Fact]
    public void PackagedEdition_DetectsSetupData()
    {
        using var fixture = new TempLocalAppData();
        fixture.CreateSetupData();

        Assert.True(EditionCoexistenceDetector.HasSetupEditionData(fixture.Root));
        Assert.True(EditionCoexistenceDetector.HasOtherEditionData(packaged: true, fixture.Root));
        Assert.False(EditionCoexistenceDetector.HasOtherEditionData(packaged: false, fixture.Root));
    }

    [Fact]
    public void SetupEdition_DetectsStoreDataAndIgnoresUnrelatedPackages()
    {
        using var fixture = new TempLocalAppData();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Packages", "Unrelated.App_abc", "LocalState"));
        Assert.False(EditionCoexistenceDetector.HasStoreEditionData(fixture.Root));

        fixture.CreateStoreData("PackingProof.PackingProof_abc");

        Assert.True(EditionCoexistenceDetector.HasStoreEditionData(fixture.Root));
        Assert.True(EditionCoexistenceDetector.HasOtherEditionData(packaged: false, fixture.Root));
        Assert.False(EditionCoexistenceDetector.HasOtherEditionData(packaged: true, fixture.Root));
    }

    private sealed class TempLocalAppData : IDisposable
    {
        public TempLocalAppData()
        {
            Root = Path.Combine(Path.GetTempPath(), "epm-edition-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void CreateSetupData()
        {
            string dataRoot = Path.Combine(Root, "ExpressPackingMonitoring");
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "config.json"), "{}");
        }

        public void CreateStoreData(string packageFamilyName)
        {
            string dataRoot = Path.Combine(
                Root,
                "Packages",
                packageFamilyName,
                "LocalState",
                "ExpressPackingMonitoring");
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "videos.db"), "");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
