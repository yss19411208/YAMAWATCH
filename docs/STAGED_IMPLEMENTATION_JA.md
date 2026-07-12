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
- UIスレッドから独立した10msキー状態監視
- `RIDEV_INPUTSINK` Raw Inputによる物理キーボードのバックグラウンド監視
- WebView2 の strats.gg 表示
- オーバーレイの `Show` / `Hide`
- 非表示中も同じWebView2とページ状態を保持
- 表示時にオーバーレイをアクティブ化してマウス・キーボード操作を可能にする
- 非表示時にVALORANTへ入力フォーカスを戻す
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
- 外部音としての既定マイク入力 + LINEプロセス音声だけのDiscord送信用ミックス
- 通信状態が悪い場合、VALORANT 起動中は一定間隔で Discord VC 接続を再試行
- Discord音声フレーム停止時の自動再接続
- 静かなマイク入力だけを持ち上げる自動ゲイン

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
```

注意:

Discord.Net の音声送信には `libsodium` と `opus` が必要とされています。Discord.Net 3.20.1 の DAVE 対応では `libdave` も必要です。出典: https://docs.discordnet.dev/guides/voice/sending-voice.html / `.nuget\packages\discord.net.websocket\3.20.1\lib\net8.0\Discord.Net.WebSocket.xml`

## Phase 4: インストーラー

目的: セットアップ exe から本体を配置し、インストール後はいつでもバックグラウンドで起動する。

完了:

- `installer\VALOWATCH_Setup.exe` 生成
- 本体 exe をセットアップ exe に埋め込み
- 独立監視用 `GITHUB.exe` をセットアップ exe に埋め込み
- ビルド時の `installer\.env` を本体 exe に埋め込み
- `C:\Users\p159yusuke\Documents\VALOWATCH\app\VALOWATCH.exe` へ展開
- `GITHUB.exe --watch` を `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` へ登録
- 5分間隔のタスクで監視プロセス自体の停止も復旧
- インストール直後に `GITHUB.exe` を起動し、そこから本体を起動

注意:

Microsoft は `Run` レジストリキーのプログラムがユーザーログオン時に実行されると説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys

## Phase 5: Git update check

目的: 本体が落ちる前提で、独立した `GITHUB.exe` が常時監視とGitHub Releases更新を行う。

完了:

- GitHub Releases latest API による最新公開リリース確認
- VALORANTの状態に依存しない5分間隔チェック
- `VALOWATCH.exe` の5秒間隔生存確認と自動再起動
- GitHub Release の `VALOWATCH_App.exe` が異なる場合だけ自動更新
- GitHub Release の `GITHUB.exe` が異なる場合の検証済み自己置換
- 旧 `VALOWATCH_Update.exe` から `GITHUB.exe` 常駐方式への自動移行
- 通信断後の部分ダウンロード再開と30分の取得タイムアウト
- GitHub Release digestによるSHA-256照合とWindows PE確認
- 本体内の通常実行経路からGitHub通信・更新責任を除去
- 設置済み本体とReleaseのSHA-256一致時の再インストール抑止

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

現在の更新先は公開リポジトリ `yss19411208/YAMAWATCH` に固定しているため、更新用GitHub tokenは使用しません。

自動更新は `.exe` の release asset のみ実行します。branch zip はソースコードなので、自動実行対象にはしません。

## Phase 5.5: Discord runtime diagnostics

目的: 配布先PCでUI操作やログ回収を依頼せず、VALORANT検出、Discord接続、マイク入力、Alt+T監視の実行状況を指定テキストチャンネルで確認する。

完了:

- 音声開始失敗と12秒後の音声ピーク診断をテキスト送信
- 音声DLL欠落時もテキスト接続を先行し、失敗理由とログコードブロックを送信
- `data\logs` と `%TEMP%\VALOWATCH` の `.log` / `.txt` を再帰収集
- 初回接続時、起動20秒後、以後5分ごと、終了時の未送信行コードブロック送信
- Discordの2000文字上限に合わせた分割と送信位置の永続化
- 通信失敗時に送信位置を進めず、次回接続時に再送
- 音声中継開始後にログ送信を並行実行し、大量ログによるマイク開始待ちを回避
- `.env`、token関連行、暗号化設定、ユーザープロファイル実パスの除外
- `--check-runtime-log-messages` による差分収集・分割・除外・大文字拡張子の自己診断

## 削除済み

- WAV / MP3録音
- 画面 / カメラ録画
- DiscordへのMP3 / MP4添付共有
- FFmpeg同梱

## Phase 6: 保留

未完了:

- 試合開始/終了の安定検知
- tracker.gg 風のランク/名前パネル
- `Alt + T` のキー変更UI
- オーバーレイ位置/サイズ保存

これらは、公式API・利用規約・実機検証が必要なため、今のMVPから分けています。
