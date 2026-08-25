## ダウンロード

`ClaudeBackupApp.exe` (約 {{SIZE_MB}} MB) をダウンロードしてそのまま実行してください。
.NET ランタイムのインストールや管理者権限は不要です。

### ファイルの検証

この exe には**コード署名をしていません**。初回起動時に Windows SmartScreen の警告が表示されます
(「詳細情報」→「実行」で起動できます)。

ダウンロードしたファイルが正規のものか、PowerShell で確認できます:

```powershell
Get-FileHash .\ClaudeBackupApp.exe -Algorithm SHA256
```

期待値 (SHA256):

```
{{SHA256}}
```

## ⚠️ 使用前の注意

- **バックアップ ZIP は暗号化されません。** 認証情報を含めると平文で格納されます
  (ファイル名に `_Credentials` が付きます)。共有ストレージやチャットに置かないでください。
- **出所の分からないバックアップ ZIP はリストアしないでください。** 復元先は正規の Claude 関連
  パスに限定されていますが、その範囲には MCP サーバー定義・hooks・plugins/skills が含まれるため、
  細工された ZIP のリストアは実質的に任意コード実行になりえます。

詳細は [README](https://github.com/{{REPO}}/blob/main/README.md) を参照してください。
