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
- `ContainsSensitiveData` を `true` から `false` へ**緩和してよいのは、対象ファイルの実データを確認して機微情報を含まないと確証を得た場合だけ**。「中身を確認できないので安全側に倒して `true`」という経緯で付いた値を、後から推測だけで `false` に戻さないこと(緩和は一方向)。

**コードとドキュメントの同期**: `PathCatalog` のカテゴリ定義と除外対象は `要件定義書.md` 4章を実装したもの。どちらか一方だけを変えないこと。カテゴリを追加・変更したら `要件定義書.md` の**3章**(バックアップ画面のカテゴリ件数の表記)と**4章の対象パス表**を、除外対象を追加したら**4章「除外対象」の一覧**を、いずれも同じコミットで更新する。これは「余裕があれば」の推奨ではなく、カテゴリ定義の変更に必ず含める作業。**この CLAUDE.md 自身**も同期対象で、上の「バックアップ/リストアの対称性」がカテゴリIDを列挙しているため、カテゴリを増減したらここも直す。

**復元先パスの解決とポータビリティ**: `manifest.json` の各ファイルには絶対パス(`Destination`)に加え、`RootKind`(`"AppData"` または `"UserProfile"`)+ `RelativePath` が記録される(`PathCatalog.ToPortableRoot`)。リストア時は `PathCatalog.ResolvePortablePath` で現在の環境の `%APPDATA%` / `%USERPROFILE%` を基準に再解決し、別マシン・別ユーザーアカウントへのリストアに対応する。`RootKind`/`RelativePath` が無い旧形式バックアップでは `Destination` にフォールバックする。

- **新しい `RootKind` を足すべきかの判断**: 「既存ルートのprefix一致で結果的に解決できてしまうか」ではなく、**そのルートが環境ごとに独立して移動しうるか**で決める。例えば `%LOCALAPPDATA%` 配下のファイルは `UserProfile` ルートでも一応解決できるが、それは `%LOCALAPPDATA%` が `%USERPROFILE%\AppData\Local` にあるという既定構成に依存した偶然であり、フォルダリダイレクトされた環境では壊れる。新しい物理ルート配下を対象にするなら `RootKind` を足すこと。
- **特殊フォルダのネストに注意**: `%LOCALAPPDATA%` や `%APPDATA%`(Roaming)は物理的に `%USERPROFILE%` 配下にネストしている。`ToPortableRoot` のように**どのルートに属するかを1つ選ぶ**処理では、より深い(具体的な)パスを持つルートを先にチェックすること。順序を誤ると、狭いルート配下のファイルが広いルートの `RootKind` として誤分類される(例: `UserProfile` チェックを先に行うと `%LOCALAPPDATA%\...` が `RootKind="UserProfile"` と誤判定される)。一方 `IsAllowedRestoreDestination` は複数prefixのOR判定なので順序は結果に影響しない — こちらで注意すべきは順序ではなく**新しいルートの追加漏れ**(漏らすと復元先が黙って拒否され、そのファイルだけ復元されない)。`ResolvePortablePath` の `rootKind` switch への追加も同時に必要。

**セキュリティ境界**: `manifest.json` は第三者から渡されたZIPの中身である可能性があり信用できない。そのため `RestoreService` は書き込み前に必ず `PathCatalog.IsAllowedRestoreDestination` で復元先が正規のClaude関連パス(`%APPDATA%\Claude`, `%USERPROFILE%\.claude`, `%USERPROFILE%\.claude.json`)配下であることを検証する(パストラバーサル対策)。新しい復元先パスを扱うコードを書く場合は必ずこの検証を通すこと。

- **この検証はコード実行を防がない**: 許可範囲の中には、Claudeが起動時に読み込んで**外部プロセスを起動する**定義が含まれる(`claude_desktop_config.json` と `.claude.json` のMCPサーバー定義、`.claude/settings.json` のhooks、`.claude/plugins`・`.claude/skills` のコード)。つまり `IsAllowedRestoreDestination` は「システム領域を壊さない」ための境界であって、**信用できないZIPの復元は実質的に任意コード実行になりうる**。この残余リスクに対する防御はパス検証ではなく、`RestoreViewModel` の2段階の警告ダイアログ(出所の確認 + `PathCatalog.IsCodeExecutionRisk` に該当するファイルの列挙)である。コード実行につながる新しいファイルを対象に加えるときは `IsCodeExecutionRisk` にも追加すること。判定は警告が目的なので、迷ったら「リスクあり」に倒す(過検出は許容)。
- **リンク解決込みで検証する**: `Path.GetFullPath` は `..\` を畳むだけでシンボリックリンク・ジャンクション(Windowsでは非管理者でも `mklink /J` で作成可能)を解決しない。そのため `IsAllowedRestoreDestination` は正規化パスに加え、`ResolveLinkChain` で各階層のリンクを解決した実パスでも許可範囲内であることを要求する。解決は階層ごとにファイルシステムを叩くため `RestoreCore` が実行単位のキャッシュを渡す。キャッシュを `static` にしないこと(検証と書き込みの間隔が開きTOCTOUが広がる)。
- **信用できない入力にはサイズ・件数の上限をかける**: `ReadManifestCoreAsync` は宣言ファイル件数を `MaxRestoreFileCount` で、`ExtractWithLimit` は展開バイト数を `MaxEntryBytes` / `MaxTotalBytes` で打ち切る(zip bomb対策)。展開量は `ZipArchiveEntry.Length`(ヘッダの宣言値、偽装可能)ではなく**実際に書き出したバイト数**で数えること。
- **manifest由来の文字列はログに直接書かない**: 復元先パスやカテゴリ表示名はmanifest由来＝攻撃者制御であり、改行を含む値をそのまま書くと「1行1エントリ」のログに偽の完了行を捏造できる。`OperationLogger.Report` が `SanitizeForLog` で制御文字をエスケープするので、ログ出力は必ずここを経由させること。

**リストアの安全な差し替え手順**: `RestoreService.RestoreCore` は「一時ファイルへ展開→成功したら既存ファイルを退避フォルダ(`%LOCALAPPDATA%\ClaudeBackupApp\PreRestoreEvacuation\yyyyMMdd_HHmmss\`)へ移動→一時ファイルを本来の場所へ移動」の順で行う。ZIPエントリ破損などで展開が失敗しても既存ファイルを失わないようにするための順序であり、変更しないこと。

- **退避データは機密であり無期限に残さない**: 退避されるのは復元先の**元ファイル**であり、`credentials` カテゴリを復元した場合は認証情報が平文でここに残る。`PruneOldEvacuations` が直近 `EvacuationGenerationsToKeep` 世代のみを保持し、それより古い世代を削除する。世代整理は**リストア本体が終わったあと**(`finally`)に行うこと。先に消すと、今回のリストアが失敗したときに古い世代まで失われる。削除は必ずログに記録する(気づけない消失を作らないため)。
- **退避先フォルダの実在＝退避成功ではない**: `EvacuateIfExists` は `File.Move` の**前**に退避先の親フォルダを作るため、退避が全件失敗しても(復元先が使用中でロックされている場合など)フォルダだけは残る。復元失敗時に退避ファイルを元の場所へ戻す経路もあるため、フォルダが空になることもある。したがって「退避された元ファイルがあるか」を `Directory.Exists` で判定してはならない。`RestoreCore` が保持する退避件数(`onEvacuationUpdated` コールバックでUIへ通知)で判断すること。誤ると、元ファイルが保全されていないのに「退避済みの元ファイルは次の場所にあります」と案内してしまう。

**Claude Desktop起動中の検知**: `ClaudeProcessService` はプロセス名 `claude` で列挙した上で、`FileVersionInfo.ProductName` が `"Claude Code"`(ターミナルの `claude` CLIやDesktop内蔵の `claude-code` バイナリ)と一致するものだけを除外し、それ以外はすべてDesktop本体(Electron製、製品名 `"Claude"`)として数える。積極的に「CLIだ」と判定できたものだけを除外し、それ以外(将来Desktop側の製品名表記が変わった場合や、モジュール情報取得に失敗した場合を含む)は「起動中かもしれない」側(true)に倒す安全側の実装になっている。

**進捗・ログ**: `OperationLogger` が `IProgress<OperationProgress>` へのUI通知と `%APPDATA%\ClaudeBackupApp\Logs\` へのログファイル出力を一箇所に集約する。`BackupService` / `RestoreService` の重複ロジックをまとめたものなので、進捗報告を追加する場合はここを経由させる。ログ書き込みは `AutoFlush = true`(1件ごとに同期フラッシュ)で、クラッシュ時にもログを失わないための意図的な設計。大量ファイル処理時のパフォーマンス調査をする際は、退避処理などのファイル操作そのものより、まずここを疑うこと(こちらは「リストアの安全な差し替え手順」とは異なり変更禁止ではないので、`BackupService` との共有箇所であることに注意すれば見直して構わない)。

**除外対象の二重防御**: `PathCatalog` 内の `ExcludedDirectoryNames`(private)はキャッシュ・診断ログ等(要件定義書4章)を列挙する。現状はSourcesの対象パス外にあるため実害はないが、将来Claude側の内部構成が変わりこれらが対象パス配下にネストされても黙って取り込まれないようにするための保険。

## その他の制約

- 日本語(全角文字)を含むパスで正しく動作する必要がある(要件定義書6章)。
- 使用中で読み取れない/書き込めないファイルはスキップしてログに警告を記録し、処理全体は継続する(バックアップ・リストアどちらも一部失敗で全体を止めない設計)。
