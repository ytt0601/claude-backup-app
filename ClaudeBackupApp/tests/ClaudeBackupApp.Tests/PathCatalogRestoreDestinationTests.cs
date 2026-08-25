using ClaudeBackupApp.Services;
using Xunit;

namespace ClaudeBackupApp.Tests;

/// <summary>
/// 復元先の許可判定(パストラバーサル対策)。
/// manifest.json は第三者が作ったZIPの中身でありうるため、書き込み前に必ずここを通す。
/// </summary>
public class PathCatalogRestoreDestinationTests
{
    public static TheoryData<string> AllowedDestinations() => new()
    {
        TestPaths.ClaudeAppData("claude_desktop_config.json"),
        TestPaths.ClaudeAppData("claude-code-sessions", "session.json"),
        TestPaths.ClaudeHome("settings.json"),
        TestPaths.ClaudeHome("projects", "sample", "conversation.jsonl"),
        TestPaths.ClaudeJson,
    };

    [Theory]
    [MemberData(nameof(AllowedDestinations))]
    public void 正規のClaude関連パスは許可する(string destination)
    {
        Assert.True(PathCatalog.IsAllowedRestoreDestination(destination));
    }

    [Fact]
    public void 親ディレクトリを遡るパスを拒否する()
    {
        var traversal = Path.Combine(TestPaths.ClaudeHome(), "..", "..", "..", "Windows", "System32", "evil.dll");

        Assert.False(PathCatalog.IsAllowedRestoreDestination(traversal));
    }

    [Fact]
    public void 許可範囲外の絶対パスを拒否する()
    {
        Assert.False(PathCatalog.IsAllowedRestoreDestination(@"C:\Windows\System32\evil.dll"));
    }

    /// <summary>
    /// 前方一致の判定は必ず区切り文字を伴わせる。伴わせないと、名前が似ているだけの
    /// 別フォルダ(%APPDATA%\ClaudeEvil など)が許可範囲に入ってしまう。
    /// </summary>
    [Fact]
    public void 名前が前方一致するだけの別フォルダを拒否する()
    {
        Assert.False(PathCatalog.IsAllowedRestoreDestination(
            Path.Combine(TestPaths.AppData, "ClaudeEvil", "payload.json")));

        Assert.False(PathCatalog.IsAllowedRestoreDestination(
            Path.Combine(TestPaths.UserProfile, ".claudeEvil", "payload.json")));
    }

    /// <summary>
    /// .claude.json は「そのファイル自身」のみが許可対象。名前を伸ばした別ファイルは対象外。
    /// </summary>
    [Fact]
    public void ドットclaudeJsonに似た名前の別ファイルを拒否する()
    {
        Assert.False(PathCatalog.IsAllowedRestoreDestination(TestPaths.ClaudeJson + ".evil"));
        Assert.False(PathCatalog.IsAllowedRestoreDestination(TestPaths.ClaudeJson + "evil"));
    }

    [Fact]
    public void ユーザープロファイル直下の無関係なファイルを拒否する()
    {
        Assert.False(PathCatalog.IsAllowedRestoreDestination(
            Path.Combine(TestPaths.UserProfile, "evil.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\a\0b")]
    public void パスとして解釈できない入力を例外ではなく拒否として扱う(string destination)
    {
        Assert.False(PathCatalog.IsAllowedRestoreDestination(destination));
    }

    /// <summary>
    /// バックアップ対象として定義されたパスは、すべて復元先としても許可されなければならない。
    /// 新しいルート配下を対象に加えたのに IsAllowedRestoreDestination への追加を忘れると、
    /// そのファイルだけ復元先が黙って拒否され、エラーにもならずに復元されない。
    /// </summary>
    [Fact]
    public void バックアップ対象のパスはすべて復元先として許可される()
    {
        var sources = PathCatalog.Categories
            .SelectMany(category => category.Sources)
            .Select(source => source.AbsolutePath)
            .ToArray();

        Assert.NotEmpty(sources);

        foreach (var absolutePath in sources)
        {
            Assert.True(
                PathCatalog.IsAllowedRestoreDestination(absolutePath),
                $"バックアップ対象なのに復元先として拒否される: {absolutePath}");
        }
    }
}
