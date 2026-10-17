using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 「本机录制动态」复制单号的接线约束：单击单号和右键菜单两条入口都要有，
/// 而且必须走同一个处理器 —— XAML 里少写一处只会表现为"点了没反应"，编译期看不出来。
/// </summary>
public sealed class RecentRecordingCopyTests
{
    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring", relativePath),
            Encoding.UTF8);

    [Fact]
    public void RecentList_OffersBothClickAndContextMenuCopy()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        // 右键菜单是窗口级的一个实例，行模板里引用它
        Assert.Contains("x:Key=\"RecentRecordingContextMenu\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"复制单号\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ContextMenu=\"{StaticResource RecentRecordingContextMenu}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("MouseLeftButtonUp=\"CopyRecentOrderId_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentListCopy_ClickAndMenuShareOneHandler()
    {
        string codeBehind = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        Assert.Contains("private void CopyRecentOrderId_Click(", codeBehind, StringComparison.Ordinal);
        // 单击与右键菜单项指向同一个处理器，行为不会各写一套
        int handlerReferences = xaml.Split("CopyRecentOrderId_Click").Length - 1;
        Assert.Equal(2, handlerReferences);
        Assert.Contains("ClipboardHelper.TryCopyText", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AppLanguage.Get(\"单号已复制\")", codeBehind, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
