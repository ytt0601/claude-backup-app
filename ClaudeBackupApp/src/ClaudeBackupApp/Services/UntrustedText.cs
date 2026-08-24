using System.Text;

namespace ClaudeBackupApp.Services;

/// <summary>
/// 信用できない入力に由来する文字列(manifest.json の復元先パス・カテゴリ表示名、
/// settings.json の保存先など)を、ログ1行・UI1行として安全に表示できる形へ変換する。
///
/// これらの値は攻撃者が自由に決められる。改行を含む値をそのまま描画すると、
/// 「1行1エントリ」のログには偽の完了行を、警告ダイアログには偽の注記を差し込める。
/// 特に <see cref="ViewModels.RestoreViewModel"/> の警告ダイアログは
/// 「信用できないZIPの復元による任意コード実行」に対する最後の防御線であり、
/// そこに攻撃者が任意の行を書き足せる状態では警告として機能しない。
/// 表示経路は必ずここを通すこと。
/// </summary>
public static class UntrustedText
{
    /// <summary>UI表示で1つの値に許す既定の最大長。</summary>
    public const int DefaultMaxLength = 160;

    /// <summary>
    /// 改行・制御文字をエスケープ表記へ置き換える(長さは変えない)。
    /// 元の文字が何であったかは残す(削除ではなく可視化する)ため、調査時の情報は失わない。
    /// アプリ自身が組み立てた文言と信用できない値が混ざった文字列(ログのメッセージ全体など)は、
    /// 途中で切ると文意が変わるため、こちらを使って長さは変えない。
    /// </summary>
    public static string Escape(string value)
    {
        var needsEscape = false;
        foreach (var ch in value)
        {
            if (IsLineBreaking(ch))
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (IsLineBreaking(ch))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 信用できない値<b>単体</b>をUIへ埋め込むために、エスケープしたうえで長さも打ち切る。
    /// 長さを制限するのは、極端に長い値1つでダイアログを画面外まで押し広げ、
    /// 後続の警告文をユーザーに読ませないようにする手口を防ぐため。
    /// </summary>
    public static string ForDisplay(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(不明)";
        }

        var escaped = Escape(value);
        return escaped.Length <= maxLength
            ? escaped
            : escaped[..maxLength] + "…(以下省略)";
    }

    /// <summary>
    /// 「1行1エントリ」を壊しうる文字かどうか。
    /// char.IsControl だけでは不十分な点に注意: U+2028(LINE SEPARATOR)・U+2029(PARAGRAPH SEPARATOR)は
    /// 制御文字ではないが、WPFのテキストレイアウトはこれらを改行として扱うため、
    /// エスケープしないとアプリ内ログ(ListBox)や警告ダイアログに偽の行を差し込まれる。
    /// </summary>
    private static bool IsLineBreaking(char ch)
        => char.IsControl(ch) || ch == '\u2028' || ch == '\u2029';
}
