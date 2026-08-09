# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## プロジェクト概要

Windows版 Claude Desktop / Claude Code / Cowork の設定・データをワンボタンでバックアップ/リストアするWPFデスクトップアプリ(C# / .NET 8)。詳細な要件は [要件定義書.md](要件定義書.md) を参照。単一exe配布・管理者権限不要・完全ローカル動作(ネットワーク通信なし)が設計上の制約。

## コマンド

作業ディレクトリはすべて `ClaudeBackupApp/`(ソリューションファイルの場所)。

```bash
# ビルド
dotnet build ClaudeBackupApp.slnx

# 実行
dotnet run --project src/ClaudeBackupApp

# 単一exe発行(自己完結・single-file、csprojに設定済み)
dotnet publish src/ClaudeBackupApp -c Release
```

テストプロジェクトは現状存在しない。

## アーキテクチャ

MVVM構成(CommunityToolkit.Mvvm使用)。`MainViewModel` が `BackupViewModel` と `RestoreViewModel` を保持し、`MainWindow.xaml` がタブでそれぞれにバインドする。

**バックアップ/リストアの対称性が非対称な点が重要**:
- バックアップは `PathCatalog`(`Services/PathCatalog.cs`)にハードコードされたカテゴリ定義(`desktop-settings` / `code-settings` / `history` / `credentials`)に基づいてファイルを収集する。
- リストアは `PathCatalog` に一切依存せず、ZIP内の `manifest.json`(バックアップ実行時に埋め込まれる)のみを頼りに復元する。これは将来 `PathCatalog` の定義が変わっても過去のバックアップを正しく復元できるようにするため。カテゴリ定義を変更する際は、この非対称性を崩さないこと。

**新しいバックアップ対象を追加する際の分類基準**:
- `ContainsSensitiveData` はファイルがトークン・APIキー・認証情報を含み得るか(環境変数経由など間接的な場合も含む)で判定する。判断に迷う場合は `true` 側に倒す。
- 既存カテゴリに含めるか新カテゴリを作るかは、そのファイルの**生成元アプリケーション**(Desktop本体 / Claude Code CLI / Cowork)を基準に決める。`%APPDATA%\Claude` 配下かどうかといった**物理的なパスの近さだけでは判断しない**こと(例: `%LOCALAPPDATA%\Claude\config.json` はパス上は `desktop-settings` に近く見えても、Claude Codeが書き込むファイルなら `code-settings` に分類する)。

**復元先パスの解決とポータビリティ**: `manifest.json` の各ファイルには絶対パス(`Destination`)に加え、`RootKind`(`"AppData"` または `"UserProfile"`)+ `RelativePath` が記録される(`PathCatalog.ToPortableRoot`)。リストア時は `PathCatalog.ResolvePortablePath` で現在の環境の `%APPDATA%` / `%USERPROFILE%` を基準に再解決し、別マシン・別ユーザーアカウントへのリストアに対応する。`RootKind`/`RelativePath` が無い旧形式バックアップでは `Destination` にフォールバックする。

- **特殊フォルダのネストに注意**: `%LOCALAPPDATA%` や `%APPDATA%`(Roaming)は物理的に `%USERPROFILE%` 配下にネストしている。新しい `RootKind` を追加する場合は、`ToPortableRoot`/`IsAllowedRestoreDestination` のprefix一致チェックにおいて、より深い(具体的な)パスを持つルートを先にチェックする実装にすること。順序を誤ると、狭いルート配下のファイルが広いルートの `RootKind` として誤分類される(例: `UserProfile` チェックを先に行うと `%LOCALAPPDATA%\...` が `RootKind="UserProfile"` と誤判定される)。

**セキュリティ境界**: `manifest.json` は第三者から渡されたZIPの中身である可能性があり信用できない。そのため `RestoreService` は書き込み前に必ず `PathCatalog.IsAllowedRestoreDestination` で復元先が正規のClaude関連パス(`%APPDATA%\Claude`, `%USERPROFILE%\.claude`, `%USERPROFILE%\.claude.json`)配下であることを検証する(パストラバーサル対策)。新しい復元先パスを扱うコードを書く場合は必ずこの検証を通すこと。

**リストアの安全な差し替え手順**: `RestoreService.RestoreCore` は「一時ファイルへ展開→成功したら既存ファイルを退避フォルダ(`%LOCALAPPDATA%\ClaudeBackupApp\PreRestoreEvacuation\yyyyMMdd_HHmmss\`)へ移動→一時ファイルを本来の場所へ移動」の順で行う。ZIPエントリ破損などで展開が失敗しても既存ファイルを失わないようにするための順序であり、変更しないこと。

**Claude Desktop起動中の検知**: `ClaudeProcessService` はプロセス名 `claude` で列挙した上で、`FileVersionInfo.ProductName` が `"Claude"`(Electron製Desktop本体)のものだけを数える。ターミナルの `claude` CLIやDesktop内蔵の `claude-code` バイナリも同じ実行ファイル名 `claude.exe` だが製品名が異なる(`"Claude Code"`)ため区別できる。モジュール情報取得に失敗した場合は「起動中かもしれない」側(true)に倒す安全側フォールバックになっている。

**進捗・ログ**: `OperationLogger` が `IProgress<OperationProgress>` へのUI通知と `%APPDATA%\ClaudeBackupApp\Logs\` へのログファイル出力を一箇所に集約する。`BackupService` / `RestoreService` の重複ロジックをまとめたものなので、進捗報告を追加する場合はここを経由させる。ログ書き込みは `AutoFlush = true`(1件ごとに同期フラッシュ)で、クラッシュ時にもログを失わないための意図的な設計。大量ファイル処理時のパフォーマンス調査をする際は、退避処理などのファイル操作そのものより、まずここを疑うこと(こちらは「リストアの安全な差し替え手順」とは異なり変更禁止ではないので、`BackupService` との共有箇所であることに注意すれば見直して構わない)。

**除外対象の二重防御**: `PathCatalog.ExcludedDirectoryNames` はキャッシュ・診断ログ等(要件定義書4章)を列挙する。現状はSourcesの対象パス外にあるため実害はないが、将来Claude側の内部構成が変わりこれらが対象パス配下にネストされても黙って取り込まれないようにするための保険。

## その他の制約

- 日本語(全角文字)を含むパスで正しく動作する必要がある(要件定義書6章)。
- 使用中で読み取れない/書き込めないファイルはスキップしてログに警告を記録し、処理全体は継続する(バックアップ・リストアどちらも一部失敗で全体を止めない設計)。
