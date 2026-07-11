# VALOWATCH 段階実装メモ

作成日: 2026-07-10
対象: Windows 10/11, .NET 8 WinForms

## Phase 0: 土台

目的: Windows 常駐アプリとして動く最小構成を作る。

完了:

- .NET 8 WinForms プロジェクト作成
- アプリ用ディレクトリ作成
- `.gitignore` に秘密情報とローカル生成物を追加
- NuGet 依存関係追加

検証:

- `dotnet build` が成功する

## Phase 1: VALORANT 検知

目的: VALORANT 本体の起動中だけ機能を有効にする。

完了:

- `VALORANT-Win64-Shipping`
- `VALORANT`

上記プロセス名を監視します。

注意:

プロセス名は Riot 側の更新で変わる可能性があります。最新版では異なる可能性があります。

## Phase 2: Alt + T オーバーレイ

目的: VALORANT 起動中に `Alt + T` で strats.gg を表示/非表示する。

完了:

- `RegisterHotKey` による `Alt + T`
- WebView2 の strats.gg 表示
- オーバーレイの `Show` / `Hide`
- 非表示直後だけ短時間ページを保持
- 高メモリ時、または非表示が続いた時に WebView2 を完全解放
- `SW_SHOWNOACTIVATE` / `SWP_NOACTIVATE` による非アクティブ表示
- 透過度を少し下げたオーバーレイ表示
- VALORANT ウィンドウ矩形に合わせた表示位置調整

注意:

フルスクリーン排他モードでは重ならない可能性があります。ボーダーレス/ウィンドウ表示で確認してください。

## Phase 3: Discord bot 音声

目的: VALORANT 起動時に bot が VC に入り、既定マイク入力を流す。

完了:

- `.env.example` の自動生成
- exe 埋め込み `.env` と外部 `.env` による Discord token / guild ID / VC ID 読み込み
- `discord_bot.json` の後方互換読み込み
- Discord.Net による VC 接続
- NAudio による既定マイク入力取得
- 48kHz/16bit/stereo PCM への変換
- LINE起動中の既定出力音声ミックス
- VALORANT終了後のDiscord MP3/MP4添付共有
- 通信状態が悪い場合、VALORANT 起動中は一定間隔で Discord VC 接続を再試行

`.env` のキー:

```dotenv
DISCORD_BOT_ENABLED=true
DISCORD_BOT_TOKEN=PASTE_BOT_TOKEN_HERE
DISCORD_GUILD_ID=123456789012345678
DISCORD_VOICE_CHANNEL_ID=123456789012345678
DISCORD_TEXT_CHANNEL_ID=123456789012345678
DISCORD_STREAM_MIC_AUDIO=true
DISCORD_MIC_DEVICE_NAME=
DISCORD_MIC_VOLUME=0.85
DISCORD_MIC_NOISE_GATE=0
DISCORD_STREAM_LINE_AUDIO=true
DISCORD_LINE_PROCESS_NAMES=LINE,Line,line
DISCORD_LINE_AUDIO_VOLUME=0.45
DISCORD_SHARE_MEDIA_FILES=true
DISCORD_SHARE_AUDIO_MP3=true
DISCORD_SHARE_VIDEO_MP4=true
DISCORD_FILE_SHARE_MAX_MB=24
DISCORD_SHARE_AUDIO_BITRATE_KBPS=128
DISCORD_TRY_SCREEN_SHARE=false
```

注意:

Discord.Net の音声送信には `libsodium` と `opus` が必要とされています。Discord.Net 3.20.1 の DAVE 対応では `libdave` も必要です。出典: https://docs.discordnet.dev/guides/voice/sending-voice.html / `.nuget\packages\discord.net.websocket\3.20.1\lib\net8.0\Discord.Net.WebSocket.xml`

## Phase 4: インストーラー

目的: セットアップ exe から本体を配置し、インストール後はいつでもバックグラウンドで起動する。

完了:

- `installer\VALOWATCH_Setup.exe` 生成
- 本体 exe をセットアップ exe に埋め込み
- ビルド時の `installer\.env` を本体 exe に埋め込み
- `C:\Users\p159yusuke\Documents\VALOWATCH\app\VALOWATCH.exe` へ展開
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` へ登録
- インストール直後に本体を起動

注意:

Microsoft は `Run` レジストリキーのプログラムがユーザーログオン時に実行されると説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys

## Phase 5: 後回し

## Phase 5: Git update check

目的: VALORANT 起動時に GitHub Releases の更新を自動確認する。

完了:

- `.env` による更新確認設定
- GitHub Releases latest API による最新公開リリース確認
- release が無い場合の branch 最新commit確認
- 通信失敗時の5分間隔再試行
- GitHub Release の `VALOWATCH_Setup.exe` asset が新しい場合の自動ダウンロード
- silent installer による無人更新

`.env` のキー:

```dotenv
VALOWATCH_UPDATE_CHECK_ENABLED=true
VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH
VALOWATCH_UPDATE_CURRENT_VERSION=0.1.2
VALOWATCH_UPDATE_BRANCH=main
VALOWATCH_UPDATE_CURRENT_COMMIT=
VALOWATCH_GITHUB_TOKEN=
```

注意:

公開リポジトリなら `VALOWATCH_GITHUB_TOKEN` は空で構いません。非公開リポジトリを確認する場合だけ、読み取り権限のある token が必要です。

自動更新は `.exe` の release asset のみ実行します。branch zip はソースコードなので、自動実行対象にはしません。

## Phase 6: 後回し

未完了:

- Discord bot による画面共有
- 試合開始/終了の安定検知
- tracker.gg 風のランク/名前パネル
- `Alt + T` のキー変更UI
- オーバーレイ位置/サイズ保存

これらは、公式API・利用規約・実機検証が必要なため、今のMVPから分けています。
