using ClaudeBackupApp.Services;
using Xunit;

namespace ClaudeBackupApp.Tests;

/// <summary>
/// 別マシン・別ユーザーアカウントへのリストアを支える、パスの可搬形式への変換。
/// </summary>
public class PathCatalogPortabilityTests
{
    [Fact]
    public void ToPortableRoot_AppData配下はAppDataとして分類する()
    {
        var (rootKind, relativePath) = PathCatalog.ToPortableRoot(
            TestPaths.ClaudeAppData("claude_desktop_config.json"));

        Assert.Equal("AppData", rootKind);
        Assert.Equal("Claude/claude_desktop_config.json", relativePath);
    }

    /// <summary>
    /// %APPDATA%(Roaming)は物理的に %USERPROFILE% 配下にネストしている。
    /// UserProfile を先に判定する実装に変えると、AppData 配下のファイルが
    /// RootKind="UserProfile" と誤分類され、フォルダリダイレクトされた環境で復元先が壊れる。
    /// </summary>
    [Fact]
    public void ToPortableRoot_AppData配下をUserProfileと誤分類しない()
    {
        Assert.StartsWith(
            TestPaths.UserProfile,
            TestPaths.AppData,
            StringComparison.OrdinalIgnoreCase);

        var (rootKind, _) = PathCatalog.ToPortableRoot(TestPaths.ClaudeAppData("config.json"));

        Assert.Equal("AppData", rootKind);
    }

    [Fact]
    public void ToPortableRoot_UserProfile直下のファイルはUserProfileとして分類する()
    {
        var (rootKind, relativePath) = PathCatalog.ToPortableRoot(TestPaths.ClaudeJson);

        Assert.Equal("UserProfile", rootKind);
        Assert.Equal(".claude.json", relativePath);
    }

    [Fact]
    public void ToPortableRoot_どのルートにも属さないパスはnullを返す()
    {
        var (rootKind, relativePath) = PathCatalog.ToPortableRoot(@"C:\Windows\System32\drivers\etc\hosts");

        Assert.Null(rootKind);
        Assert.Null(relativePath);
    }

    [Fact]
    public void ToPortableRoot_相対パスの区切りをスラッシュに正規化する()
    {
        var (_, relativePath) = PathCatalog.ToPortableRoot(TestPaths.ClaudeHome("plugins", "sample", "index.js"));

        Assert.NotNull(relativePath);
        Assert.DoesNotContain('\\', relativePath);
    }

    [Theory]
    [InlineData(null, "Claude/config.json")]
    [InlineData("AppData", null)]
    [InlineData("", "")]
    [InlineData("LocalAppData", "Claude/config.json")]
    public void ResolvePortablePath_未知または欠落したルート種別はnullを返す(string? rootKind, string? relativePath)
    {
        // 旧形式のバックアップや未知の RootKind は Destination へフォールバックさせる
        Assert.Null(PathCatalog.ResolvePortablePath(rootKind, relativePath));
    }

    [Fact]
    public void ResolvePortablePath_スラッシュ区切りの相対パスを解決できる()
    {
        var resolved = PathCatalog.ResolvePortablePath("UserProfile", ".claude/settings.json");

        Assert.Equal(TestPaths.ClaudeHome("settings.json"), resolved);
    }

    /// <summary>
    /// バックアップ対象として定義されているパスは、すべて可搬形式へ変換でき、
    /// かつ元のパスへ戻せなければならない。変換できないパスが混ざると、
    /// そのファイルだけ別マシンへ復元できなくなる。
    /// カテゴリ定義を増やしたとき、このテストが自動で新しいパスも検査する。
    /// </summary>
    [Fact]
    public void バックアップ対象のパスはすべて可搬形式へ往復変換できる()
    {
        var sources = PathCatalog.Categories
            .SelectMany(category => category.Sources)
            .Select(source => source.AbsolutePath)
            .ToArray();

        Assert.NotEmpty(sources);

        foreach (var absolutePath in sources)
        {
            var (rootKind, relativePath) = PathCatalog.ToPortableRoot(absolutePath);

            Assert.True(rootKind is not null, $"可搬形式へ変換できない: {absolutePath}");

            var restored = PathCatalog.ResolvePortablePath(rootKind, relativePath);

            Assert.Equal(
                Path.GetFullPath(absolutePath).TrimEnd(Path.DirectorySeparatorChar),
                restored?.TrimEnd(Path.DirectorySeparatorChar));
        }
    }
}
