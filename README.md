# Claude Backup App

[![CI](https://github.com/ytt0601/claude-backup-app/actions/workflows/ci.yml/badge.svg)](https://github.com/ytt0601/claude-backup-app/actions/workflows/ci.yml)
[![CodeQL](https://github.com/ytt0601/claude-backup-app/actions/workflows/codeql.yml/badge.svg)](https://github.com/ytt0601/claude-backup-app/actions/workflows/codeql.yml)

Windows 版の **Claude Desktop / Claude Code / Cowork** の設定・データを、ワンボタンでバックアップし、
バックアップファイルから元の状態にリストア（復元）できるデスクトップアプリです。

- 単一の exe ファイルで動作（.NET ランタイムのインストール不要）
- 管理者権限は不要（`%APPDATA%` / `%USERPROFILE%` 配下のユーザー領域のみを扱います）
- ネットワーク通信は一切行いません（完全ローカル動作）

> [!IMPORTANT]
> 本ソフトウェアは個人が開発した **非公式ツール** であり、Anthropic PBC とは一切関係ありません。
> Anthropic PBC による承認・提供・サポートを受けたものではありません。
> "Claude" は Anthropic PBC の商標です。

---

## ⚠️ 使う前に必ず読んでください

このアプリは認証情報を含みうるファイルを扱います。以下を理解したうえで使用してください。

### 1. バックアップ ZIP は暗号化されません

生成される ZIP にパスワードや暗号化はかかりません。「認証情報・トークン」カテゴリを含めてバックアップすると、
**API トークン等が平文のまま ZIP に格納されます**（ファイル名に `_Credentials` が付きます）。

- ZIP をメールやチャット、共有ストレージに置かないでください
- 既定の保存先は `%LOCALAPPDATA%\ClaudeBackupApp\Backups\` です。OneDrive などのクラウド同期フォルダや
  ネットワーク共有を保存先に選ぶと、アプリが警告を表示します

### 2. 出所の分からないバックアップ ZIP をリストアしないでください

リストア先は正規の Claude 関連パスに限定されていますが、**その範囲の中には Claude が起動時に読み込んで
外部プロセスを起動する定義が含まれます**（`claude_desktop_config.json` / `.claude.json` の MCP サーバー定義、
`.claude/settings.json` の hooks、`.claude/plugins`・`.claude/skills` のコード）。

つまり、第三者が細工した ZIP のリストアは **実質的に任意コード実行になりえます**。
アプリは実行前に出所の確認と、復元対象の分類別リスク一覧を提示しますが、
最終的な判断はご自身で行ってください。**自分で作成したバックアップ以外はリストアしない**のが安全です。

### 3. 上書きされる元ファイルは退避されますが、無期限には残りません

リストア時、上書き対象の既存ファイルは
`%LOCALAPPDATA%\ClaudeBackupApp\PreRestoreEvacuation\yyyyMMdd_HHmmss\` へ退避されます。
ここには元の認証情報が平文で残るため、**直近 5 世代のみ保持し、それより古い世代は自動削除されます**。

---

## ダウンロード

[Releases](https://github.com/ytt0601/claude-backup-app/releases/latest) から
`ClaudeBackupApp.exe`（約 69 MB）をダウンロードしてそのまま実行してください。
インストール作業も .NET ランタイムも不要です。

サイズが大きいのは、.NET ランタイムごと同梱した self-contained な単一 exe だからです
（WPF アプリのためトリミングによる縮小はできません）。

### ダウンロードしたファイルの検証

exe には**コード署名をしていません**。初回起動時に Windows SmartScreen の警告が出ます
（「詳細情報」→「実行」で起動できます）。

各リリースには `ClaudeBackupApp.exe.sha256` を同梱しています。改ざんされていないことは
PowerShell で確認できます。

```powershell
Get-FileHash .\ClaudeBackupApp.exe -Algorithm SHA256
```

出力された値が `.sha256` ファイルおよびリリースノートに記載の値と一致することを確認してください。
署名がないことが気になる場合は、[ご自身でソースからビルド](#ビルドと実行)してください。

## 動作環境

| 項目 | 内容 |
|---|---|
| OS | Windows 10 / 11 (x64) |
| 実行時の前提 | なし（self-contained single-file exe） |
| 権限 | 管理者権限不要 |

日本語（全角文字）を含むパスでも動作します。

## バックアップ対象

| カテゴリ | 既定 | 内容 |
|---|---|---|
| Claude Desktop 設定 | ON | `%APPDATA%\Claude\` 配下の設定 JSON（MCP サーバー定義を含む） |
| Claude Code 設定 | ON | `%USERPROFILE%\.claude.json`、`.claude\settings.json`、`plugins\`、`skills\` |
| プロジェクト・会話履歴 | ON | `.claude\projects\`、`sessions\`、`history.jsonl`、`tasks\`、`plans\` ほか |
| 認証情報・トークン | **OFF** | `.claude\.credentials.json`、`buddy-tokens.json`、`ant-did` |

キャッシュ・診断ログ・再生成可能なデータ（`Cache`、`GPUCache`、`debug`、`telemetry`、`shell-snapshots` など）は
常に対象外です。対象パスと除外対象の完全な一覧は [要件定義書.md](要件定義書.md) の 4 章を参照してください。

> [!NOTE]
> 「Claude Desktop 設定」「Claude Code 設定」も、MCP サーバー定義や環境変数を通じて
> API キーを含みうるため、機微情報を含むカテゴリとして扱われます。
> 平文であることの注意喚起は、「認証情報・トークン」以外を選んだ場合にも表示されます。

## ビルドと実行

[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。
作業ディレクトリは `ClaudeBackupApp/`（ソリューションファイルの場所）です。

```bash
# ビルド
dotnet build ClaudeBackupApp.slnx

# テスト
dotnet test ClaudeBackupApp.slnx

# 実行
dotnet run --project src/ClaudeBackupApp

# 単一 exe を発行（self-contained / single-file、設定は csproj に記述済み）
dotnet publish src/ClaudeBackupApp -c Release
```

発行された exe は `src/ClaudeBackupApp/bin/Release/net8.0-windows/win-x64/publish/ClaudeBackupApp.exe` に出力されます。

> [!NOTE]
> exe にコード署名はしていないため、初回起動時に Windows SmartScreen の警告が表示されます。
> 「詳細情報」→「実行」で起動できます。気になる場合はご自身でソースからビルドしてください。

## 使い方

### バックアップ

1. 「バックアップ」タブで対象カテゴリをチェック
2. 必要なら「変更」ボタンで保存先フォルダを変更
3. 「バックアップ実行」をクリック
4. Claude Desktop が起動中の場合は警告が出ます（強制終了はしません。手動で終了してから再試行してください）
5. `ClaudeBackup_yyyyMMdd_HHmmss.zip` が保存先に出力されます

### リストア

1. 「リストア」タブでバックアップ ZIP を選択
2. 作成日時・含まれるカテゴリが表示されるので、復元したいカテゴリを選択（部分リストア可）
3. 「リストア実行」をクリック
4. 出所確認とリスク分類の警告を確認して続行
5. 既存ファイルは退避フォルダへ移動されたうえで復元されます

使用中で読み取れない／書き込めないファイルはスキップしてログに警告を記録し、処理全体は継続します。

## ファイルの保存場所

| 用途 | パス |
|---|---|
| バックアップ ZIP（既定） | `%LOCALAPPDATA%\ClaudeBackupApp\Backups\` |
| リストア時の退避データ | `%LOCALAPPDATA%\ClaudeBackupApp\PreRestoreEvacuation\` |
| 実行ログ | `%APPDATA%\ClaudeBackupApp\Logs\` |
| アプリ設定 | `%APPDATA%\ClaudeBackupApp\settings.json` |

## 開発者向け

- 構成: C# / .NET 8 / WPF、MVVM（[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)）
- 設計上の不変条件・セキュリティ境界の意図は [CLAUDE.md](CLAUDE.md) にまとめています。
  変更を加える前に一読してください（特にリストア処理の順序、パス検証、リスク分類の向き）
- 仕様は [要件定義書.md](要件定義書.md) を参照
- テストは `tests/ClaudeBackupApp.Tests/`。パス検証・復元リスク分類・表示用エスケープなど、
  ファイルシステムに触れない純粋な判定ロジックを対象にしています
- CI では毎 push・PR でビルド・テスト・単一 exe の発行を検証し、CodeQL による静的解析（`security-extended` スイート）を実行しています

## セキュリティ上の問題を見つけた場合

公開の Issue ではなく、リポジトリの Security タブ（Private vulnerability reporting）からご連絡ください。

## ライセンス

[MIT License](LICENSE)
