using ClaudeBackupApp.Services;
using Xunit;

namespace ClaudeBackupApp.Tests;

/// <summary>
/// カテゴリ定義の性質。
/// 「カテゴリが4つある」のような定義そのものを固定する検査は書かない。
/// 定義を変えるたびにテストを直す作業が増えるだけで、不具合を防がないため。
/// ここで固定するのは、定義をどう変えても保たれるべき性質だけ。
/// </summary>
public class BackupCategoryTests
{
    [Fact]
    public void カテゴリIDは重複しない()
    {
        var ids = PathCatalog.Categories.Select(category => category.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ZIP内のフォルダ名は重複しない()
    {
        // 重複すると、別カテゴリのファイルが同じ場所へ展開されて衝突する
        var folders = PathCatalog.Categories.Select(category => category.ZipFolderName).ToArray();

        Assert.Equal(folders.Length, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void すべてのカテゴリが対象パスを持つ()
    {
        foreach (var category in PathCatalog.Categories)
        {
            Assert.NotEmpty(category.Sources);
            Assert.False(string.IsNullOrWhiteSpace(category.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(category.ZipFolderName));
        }
    }

    [Fact]
    public void 対象パスはすべて絶対パスで重複しない()
    {
        var paths = PathCatalog.Categories
            .SelectMany(category => category.Sources)
            .Select(source => source.AbsolutePath)
            .ToArray();

        foreach (var path in paths)
        {
            Assert.True(Path.IsPathFullyQualified(path), $"絶対パスではない: {path}");
        }

        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// 認証情報カテゴリは既定で選択されない。
    /// バックアップZIPは暗号化されないため、明示的に選ばれたときだけ含める。
    /// </summary>
    [Fact]
    public void 認証情報カテゴリは既定で無効()
    {
        var credentials = PathCatalog.Categories.Single(category => category.Id == "credentials");

        Assert.False(credentials.DefaultEnabled);
        Assert.True(credentials.ContainsSensitiveData);
    }

    /// <summary>
    /// 機微情報を含むカテゴリが1つも無い状態は、分類の付け忘れを疑うべき状態。
    /// (この判定は「迷ったら true 側に倒す」運用になっている)
    /// </summary>
    [Fact]
    public void 機微情報を含むカテゴリが存在する()
    {
        Assert.Contains(PathCatalog.Categories, category => category.ContainsSensitiveData);
    }
}
