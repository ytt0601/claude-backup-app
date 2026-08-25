using ClaudeBackupApp.Services;
using Xunit;

namespace ClaudeBackupApp.Tests;

/// <summary>
/// 信用できない文字列の表示用変換。
/// ここが破れると、警告ダイアログとログに攻撃者が任意の「行」を差し込めるようになる。
/// </summary>
public class UntrustedTextTests
{
    [Fact]
    public void Escape_通常の文字列はそのまま返す()
    {
        const string value = @"C:\Users\test\.claude\settings.json";
        Assert.Equal(value, UntrustedText.Escape(value));
    }

    [Theory]
    [InlineData("\r", @"\r")]
    [InlineData("\n", @"\n")]
    [InlineData("\t", @"\t")]
    public void Escape_改行とタブをエスケープ表記へ置き換える(string input, string expected)
    {
        Assert.Equal(expected, UntrustedText.Escape(input));
    }

    /// <summary>
    /// U+2028 / U+2029 は char.IsControl が false を返すが、WPF は改行として描画する。
    /// char.IsControl だけで判定する実装に戻すと、ここが落ちる。
    /// </summary>
    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public void Escape_制御文字ではないが改行として描画される文字もエスケープする(char lineBreaking)
    {
        var escaped = UntrustedText.Escape($"前{lineBreaking}後");

        Assert.DoesNotContain(lineBreaking.ToString(), escaped);
        Assert.Contains($"\\u{(int)lineBreaking:x4}", escaped);
    }

    [Fact]
    public void Escape_文字を削除せず可視化する()
    {
        // 「消す」のではなく「見える形にする」ため、元に何かあった事実は残る
        var escaped = UntrustedText.Escape("a\nb");

        Assert.Equal(@"a\nb", escaped);
    }

    [Fact]
    public void Escape_長さを切り詰めない()
    {
        // アプリ自身の文言と混ざった文字列に使うため、途中で切ると文意が変わる
        var value = new string('あ', 1000);

        Assert.Equal(1000, UntrustedText.Escape(value).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ForDisplay_空の値は不明として表示する(string? value)
    {
        Assert.Equal("(不明)", UntrustedText.ForDisplay(value));
    }

    [Fact]
    public void ForDisplay_極端に長い値を打ち切る()
    {
        // 長い値1つでダイアログを画面外まで押し広げ、後続の警告文を読ませない手口を防ぐ
        var value = new string('X', UntrustedText.DefaultMaxLength * 10);

        var displayed = UntrustedText.ForDisplay(value);

        Assert.StartsWith(new string('X', UntrustedText.DefaultMaxLength), displayed);
        Assert.EndsWith("…(以下省略)", displayed);
    }

    [Fact]
    public void ForDisplay_上限以下の値はそのまま返す()
    {
        var value = new string('X', UntrustedText.DefaultMaxLength);

        Assert.Equal(value, UntrustedText.ForDisplay(value));
    }

    /// <summary>
    /// 中核の不変条件。改行として描画されうる文字は、どれ1つとして出力に残ってはならない。
    /// 個別の文字を列挙するのではなく、対象になりうる文字を全走査して確かめる。
    /// </summary>
    [Fact]
    public void ForDisplay_出力に改行として描画される文字が一切残らない()
    {
        var dangerous = Enumerable.Range(0, char.MaxValue + 1)
            .Select(i => (char)i)
            .Where(ch => char.IsControl(ch) || ch == '\u2028' || ch == '\u2029')
            .ToArray();

        Assert.NotEmpty(dangerous);

        foreach (var ch in dangerous)
        {
            var displayed = UntrustedText.ForDisplay($"復元完了{ch}偽の行");

            Assert.False(
                displayed.Any(c => char.IsControl(c) || c == '\u2028' || c == '\u2029'),
                $"U+{(int)ch:X4} がエスケープされずに残っている: {displayed}");
        }
    }

    /// <summary>
    /// 打ち切りはエスケープの「後」に行う必要がある。順序が逆だと、打ち切り位置の直後に
    /// 置いた改行がエスケープされないまま残りうる。
    /// </summary>
    [Fact]
    public void ForDisplay_打ち切られる位置に改行があってもエスケープされる()
    {
        var value = new string('X', UntrustedText.DefaultMaxLength - 1) + "\n" + new string('Y', 100);

        var displayed = UntrustedText.ForDisplay(value);

        Assert.DoesNotContain("\n", displayed);
    }
}
