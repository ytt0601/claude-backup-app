using ClaudeBackupApp.Services;
using Xunit;

using RiskKind = ClaudeBackupApp.Services.PathCatalog.RestoreRiskKind;

namespace ClaudeBackupApp.Tests;

/// <summary>
/// 復元リスクの分類。
/// 復元先を正規のClaude関連パスに限っても、その範囲には Claude が起動時に読み込んで
/// 外部プロセスを起動する定義が含まれる。そのため最後の防御線は警告ダイアログであり、
/// この分類が警告の中身を決める。
/// </summary>
public class PathCatalogRiskClassificationTests
{
    [Theory]
    [InlineData("projects")]
    [InlineData("sessions")]
    [InlineData("tasks")]
    [InlineData("plans")]
    public void 純粋なデータのフォルダ配下は警告不要と分類する(string directoryName)
    {
        var path = TestPaths.ClaudeHome(directoryName, "sample.jsonl");

        Assert.Equal(RiskKind.None, PathCatalog.ClassifyRestoreRisk(path));
    }

    [Fact]
    public void 会話履歴ファイルは警告不要と分類する()
    {
        Assert.Equal(RiskKind.None, PathCatalog.ClassifyRestoreRisk(TestPaths.ClaudeHome("history.jsonl")));
        Assert.Equal(RiskKind.None, PathCatalog.ClassifyRestoreRisk(
            TestPaths.ClaudeAppData("claude-code-sessions", "session.json")));
    }

    [Theory]
    [InlineData(".credentials.json")]
    public void 認証情報ファイルは差し替えリスクとして分類する(string fileName)
    {
        Assert.Equal(RiskKind.CredentialReplacement,
            PathCatalog.ClassifyRestoreRisk(TestPaths.ClaudeHome(fileName)));
    }

    [Theory]
    [InlineData("buddy-tokens.json")]
    [InlineData("ant-did")]
    public void AppData側の認証情報ファイルも差し替えリスクとして分類する(string fileName)
    {
        Assert.Equal(RiskKind.CredentialReplacement,
            PathCatalog.ClassifyRestoreRisk(TestPaths.ClaudeAppData(fileName)));
    }

    [Fact]
    public void MCPサーバー定義を含むファイルはコード実行リスクとして分類する()
    {
        Assert.Equal(RiskKind.McpServerDefinition, PathCatalog.ClassifyRestoreRisk(TestPaths.ClaudeJson));
        Assert.Equal(RiskKind.McpServerDefinition,
            PathCatalog.ClassifyRestoreRisk(TestPaths.ClaudeAppData("claude_desktop_config.json")));
    }

    [Theory]
    [InlineData("plugins")]
    [InlineData("skills")]
    public void プラグインとスキルはコードとして分類する(string directoryName)
    {
        var path = TestPaths.ClaudeHome(directoryName, "sample", "index.js");

        Assert.Equal(RiskKind.PluginOrSkillCode, PathCatalog.ClassifyRestoreRisk(path));
    }

    [Fact]
    public void hooksを含みうる設定ファイルとして分類する()
    {
        Assert.Equal(RiskKind.HookSettings, PathCatalog.ClassifyRestoreRisk(TestPaths.ClaudeHome("settings.json")));
    }

    /// <summary>
    /// 中核の不変条件(fail-closed)。
    /// 分類は「危険なものを列挙する」のではなく「純粋なデータだと確認できたものを除外する」向きで
    /// 書かれている。未知のファイルが None になる実装に変わると、細工されたZIPが
    /// 警告なしでコードを復元できるようになる。
    /// </summary>
    [Theory]
    [InlineData("unknown-new-file.dat")]
    [InlineData("hooks/pre-commit.ps1")]
    [InlineData("projects.evil")]
    public void 分類できないファイルはNoneではなくUnclassifiedにする(string relativePath)
    {
        var path = TestPaths.ClaudeHome(relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(RiskKind.Unclassified, PathCatalog.ClassifyRestoreRisk(path));
    }

    [Fact]
    public void AppData側の未知のファイルもUnclassifiedにする()
    {
        Assert.Equal(RiskKind.Unclassified,
            PathCatalog.ClassifyRestoreRisk(TestPaths.ClaudeAppData("some-new-config.json")));
    }

    /// <summary>
    /// 警告は RestoreRiskOrder の順に分類ごとにまとめて表示する。
    /// 新しい分類を追加したのに RestoreRiskOrder への追加を忘れると、
    /// その分類が警告一覧から抜け落ちる。
    /// </summary>
    [Fact]
    public void 警告対象の分類はすべて表示順に登録されている()
    {
        var missing = Enum.GetValues<RiskKind>()
            .Where(kind => kind != RiskKind.None)
            .Where(kind => !PathCatalog.RestoreRiskOrder.Contains(kind))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void 表示順にNoneを含めない()
    {
        // None は警告に出さないもの。並べる対象に混ざってはならない
        Assert.DoesNotContain(RiskKind.None, PathCatalog.RestoreRiskOrder);
    }

    /// <summary>
    /// 分類を追加したのに説明文を追加し忘れると、警告ダイアログの見出しが
    /// 既定の「その他」になり、何が危険なのかユーザーに伝わらなくなる。
    /// </summary>
    [Fact]
    public void すべての警告分類に固有の説明文がある()
    {
        foreach (var kind in PathCatalog.RestoreRiskOrder)
        {
            var description = PathCatalog.DescribeRestoreRisk(kind);

            Assert.False(string.IsNullOrWhiteSpace(description), $"説明文が空: {kind}");
            Assert.NotEqual("その他", description);
        }
    }

    /// <summary>
    /// 認証情報はコード実行にはならないが、差し替えられると以後の操作が別アカウントで
    /// 行われうるため、警告の対象から外してはならない。
    /// </summary>
    [Fact]
    public void 認証情報カテゴリの対象は警告不要にならない()
    {
        var credentialSources = PathCatalog.Categories
            .Single(category => category.Id == "credentials")
            .Sources;

        Assert.NotEmpty(credentialSources);

        foreach (var source in credentialSources)
        {
            Assert.NotEqual(RiskKind.None, PathCatalog.ClassifyRestoreRisk(source.AbsolutePath));
        }
    }
}
