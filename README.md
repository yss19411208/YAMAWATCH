# VALOWATCH

VALOWATCH は、VALORANT 起動中に `Alt + T` で strats.gg のラインナップページを表示/非表示する Windows 常駐アプリです。

## いま入っている動作

- 起動すると通常画面を出さず、タスクトレイでバックグラウンド常駐します。
- `Alt + T` は VALORANT が起動している時だけ反応します。
- オーバーレイには `https://strats.gg/valorant/lineups` だけを表示します。
- 通常の設定画面や履歴画面は表示しません。表に出るUIは `Alt + T` の strats.gg オーバーレイだけです。
- 初回表示後は同じ WebView2 ウィンドウを保持し、`Alt + T` は表示/非表示だけを切り替えます。
- 非表示中も WebView2 を破棄せず、strats.gg のページ状態を保持します。
- オーバーレイ表示時は VALORANT のフォーカスを奪わないように、非アクティブ表示を使います。
- オーバーレイ全体を少し透過させています。
- VALORANT ウィンドウの位置が取れる場合は、その上に寄せてオーバーレイを出します。
- インストーラーを実行すると `C:\Users\p159yusuke\Documents\VALOWATCH\app\VALOWATCH.exe` に本体を配置し、ユーザー単位の Windows スタートアップに登録します。
- Discord bot 設定が有効な場合、VALORANT 起動検知で指定 VC に入り、既定マイク入力のリレーを開始します。
- VALORANT 終了検知後、録音WAVをMP3へ変換し、Discordの指定テキストチャンネルへ通知抑制付きで添付します。
- 動画録画設定を明示的に有効化した場合、VALORANT 起動中の画面MP4とカメラMP4を保存し、Discordの指定テキストチャンネルへ通知抑制付きで添付します。
- `DISCORD_STREAM_LINE_AUDIO=true` の場合、LINEプロセス起動中だけPCの既定出力音声をDiscord音声へミックスします。
- `VALOWATCH_UPDATE_REPOSITORY` が設定されている場合、VALORANT 起動時に GitHub Releases の更新確認を行い、`VALOWATCH_Setup.exe` の release asset が新しければ自動でダウンロードしてサイレント更新します。
- 配布用ビルド時に `C:\Users\p159yusuke\Documents\VALOWATCH\installer\.env` を `VALOWATCH.exe` へ埋め込みます。

## 使い方

配布用インストーラー:

```powershell
.\installer\VALOWATCH_Setup.exe
```

## 全自動化される範囲

インストーラー実行後、VALOWATCH 本体は `C:\Users\p159yusuke\Documents\VALOWATCH\app\VALOWATCH.exe` に配置され、すぐ起動します。次回以降の Windows ログオンでも起動するように、`Run` レジストリとスタートアップフォルダの `VALOWATCH.cmd` の両方に登録します。

外部オーバーレイ基盤は使いません。VALOWATCH 単体の Windows 常駐アプリとして動作します。

開発用に直接起動する場合:

```powershell
.\exe\VALOWATCH.exe
```

Discord への自動接続を止めて起動する場合:

```powershell
.\exe\VALOWATCH.exe --no-discord
```

## Discord bot 設定

配布用に固定したい Discord bot 設定は、ビルド前に次の場所へ書きます。

```text
C:\Users\p159yusuke\Documents\VALOWATCH\installer\.env
```

`installer\VALOWATCH_Setup.exe` を作るとき、この `.env` は `VALOWATCH.exe` に埋め込まれます。配布先の相手は `.env` を配置しなくても、埋め込まれた設定で動きます。

インストーラー実行後は `C:\Users\p159yusuke\Documents\VALOWATCH\installer\.env.example` も作成されます。配布先に実体の `.env` がある場合だけ、その外部 `.env` が埋め込み設定を上書きします。

1. `C:\Users\p159yusuke\Documents\VALOWATCH\installer\.env` を開きます。
2. `.env` に `DISCORD_BOT_TOKEN`, `DISCORD_GUILD_ID`, `DISCORD_VOICE_CHANNEL_ID`, `DISCORD_TEXT_CHANNEL_ID` を設定します。
3. `DISCORD_BOT_ENABLED=true` にします。
4. VALOWATCH を再起動します。
5. VALORANT を起動すると、bot が指定 VC に入ります。

VALORANT 起動中に通信状態が悪く Discord 接続に失敗した場合、VALOWATCH は一定間隔で VC 接続を再試行します。
`DISCORD_TEXT_CHANNEL_ID` を設定すると、VALORANT 終了後のMP3/MP4共有先になります。通常の起動通知テキストは送信しません。
Discord の接続や音声送信で失敗した場合は、`C:\Users\p159yusuke\Documents\VALOWATCH\data\logs\valowatch.log` に理由を記録します。

`.env` は `C:\Users\p159yusuke\Documents\VALOWATCH\installer` に置きます。bot token は秘密情報なので Git に入れないでください。exe へ埋め込んでも完全な秘匿にはならず、配布先が解析すれば token を取り出せる可能性があります。

`.env` の例:

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
VALOWATCH_UPDATE_CHECK_ENABLED=true
VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH
VALOWATCH_UPDATE_CURRENT_VERSION=0.1.2
VALOWATCH_UPDATE_BRANCH=main
VALOWATCH_UPDATE_CURRENT_COMMIT=
VALOWATCH_GITHUB_TOKEN=
VALOWATCH_VIDEO_CAPTURE_ENABLED=false
VALOWATCH_VIDEO_CAPTURE_SCREEN=true
VALOWATCH_VIDEO_CAPTURE_CAMERA=true
VALOWATCH_FFMPEG_PATH=
VALOWATCH_SCREEN_CAPTURE_INPUT=desktop
VALOWATCH_CAMERA_DEVICE_NAME=
VALOWATCH_SCREEN_FPS=20
VALOWATCH_CAMERA_FPS=20
VALOWATCH_VIDEO_QUALITY=5
```

## Discord MP3 / MP4 sharing

VALORANT 終了後の共有先として、Discordの指定テキストチャンネルへファイル添付します。Google Drive 連携は使いません。

```dotenv
DISCORD_TEXT_CHANNEL_ID=123456789012345678
DISCORD_SHARE_MEDIA_FILES=true
DISCORD_SHARE_AUDIO_MP3=true
DISCORD_SHARE_VIDEO_MP4=true
DISCORD_FILE_SHARE_MAX_MB=24
DISCORD_SHARE_AUDIO_BITRATE_KBPS=128
```

Discordの通常メッセージ作成APIは、ファイルを `multipart/form-data` の添付として送る仕様で、最大リクエストサイズは25MiBです。VALOWATCHはmultipartの余白を見て既定上限を24MiBにしています。出典: https://docs.discord.com/developers/resources/message

録音WAVはFFmpegでMP3へ変換してから共有します。MP4は画面/カメラ録画設定が有効な場合だけ共有されます。上限を超えるファイルはDiscordへ送らず、`data\logs\valowatch.log` に理由を残します。

Discordメッセージには `SuppressNotification` フラグを付けます。チャンネルには投稿されますが、通常の通知を鳴らさない運用です。

## LINE audio mix

`DISCORD_STREAM_LINE_AUDIO=true` の場合、VALOWATCHはLINEプロセスの起動を数秒おきに確認します。LINEが起動している間だけ、Windowsの既定出力デバイスの音をDiscord bot音声へミックスします。

```dotenv
DISCORD_STREAM_LINE_AUDIO=true
DISCORD_LINE_PROCESS_NAMES=LINE,Line,line
DISCORD_LINE_AUDIO_VOLUME=0.45
```

注意: この方式はLINEだけをアプリ単位で分離して録音するものではありません。LINEが起動中に同じスピーカー/ヘッドホンへ流れている他アプリ音も一緒に入る可能性があります。不要な場合は `DISCORD_STREAM_LINE_AUDIO=false` にしてください。

## MP4 capture

画面/カメラMP4保存は、本人が内容を理解しているPCでだけ有効化してください。既定では無効です。

```dotenv
VALOWATCH_VIDEO_CAPTURE_ENABLED=true
VALOWATCH_VIDEO_CAPTURE_SCREEN=true
VALOWATCH_VIDEO_CAPTURE_CAMERA=true
VALOWATCH_SCREEN_CAPTURE_INPUT=desktop
VALOWATCH_CAMERA_DEVICE_NAME=
VALOWATCH_SCREEN_FPS=20
VALOWATCH_CAMERA_FPS=20
VALOWATCH_VIDEO_QUALITY=5
```

`VALOWATCH_SCREEN_CAPTURE_INPUT=desktop` は、ウィンドウフルスクリーンのVALORANTを含むデスクトップを録画します。FFmpegの `gdigrab` は `title=window_title` も使えますが、VALORANTのタイトル取得は環境差があるため、既定は `desktop` です。

`VALOWATCH_CAMERA_DEVICE_NAME` が空の場合、FFmpegのDirectShowデバイス一覧から最初のvideoデバイスを使います。複数カメラがあるPCでは、カメラ名の一部を指定してください。

## Git update check

VALORANT を起動したタイミングで、GitHub Releases の最新公開リリースを確認できます。

`.env` に次のように設定します。

```dotenv
VALOWATCH_UPDATE_CHECK_ENABLED=true
VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH
VALOWATCH_UPDATE_CURRENT_VERSION=0.1.2
VALOWATCH_UPDATE_BRANCH=main
VALOWATCH_UPDATE_CURRENT_COMMIT=
VALOWATCH_GITHUB_TOKEN=
```

`VALOWATCH_UPDATE_REPOSITORY` は `owner/repo`、`https://github.com/owner/repo`、`git@github.com:owner/repo.git` のどれでも指定できます。

公開リポジトリなら `VALOWATCH_GITHUB_TOKEN` は空で構いません。非公開リポジトリを見る場合だけ、読み取り権限のある GitHub token を入れてください。

GitHub Releases が無い場合は、`VALOWATCH_UPDATE_BRANCH` の最新commit確認に切り替えます。`VALOWATCH_UPDATE_CURRENT_COMMIT` が空の場合、branchにcommitが存在した時点で更新ありとして扱います。

通信環境が悪い場合は、VALORANT 起動中に5分間隔で再試行します。更新が見つかった場合、GitHub Release の `VALOWATCH_Setup.exe` asset を自動でダウンロードし、`--silent` で起動して現在のインストール先を置き換えます。ユーザー操作は不要です。

GitHub Releases が無い場合や、release asset が `.exe` インストーラーではなく branch zip だけの場合、自動更新は実行しません。branch zip はソースコードであり、実行中アプリが安全に自己更新できる配布物ではないためです。

## 注意点

- ダウンロード完了だけで任意の exe が勝手に実行される前提にはしていません。ユーザーがインストーラーを1回実行した後、次回以降は Windows ログオン時に常駐します。
- Microsoft は `Run` レジストリキーのプログラムがユーザーのログオン時に実行されると説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
- WebView2 アプリ配布時は WebView2 Runtime が必要です。Windows 11 では含まれますが、Windows 10 では未導入の端末があり得ます。出典: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- オーバーレイは非アクティブ表示に変更しています。ただしフルスクリーン排他モードのゲームは通常の最前面ウィンドウより前に出ることがあります。まだ最小化される場合は `設定 → グラフィック → 一般 → 画面モード → ウィンドウフルスクリーン → 適用` にしてください。
- Discord.Net の音声ガイドは `libsodium` と `opus` のネイティブ DLL が必要だと説明しています。さらに Discord.Net 3.20.1 の `EnableVoiceDaveEncryption` は、音声暗号化に `libdave` を使う場合、実行ディレクトリに `libdave` のビルドが必要だと説明しています。今回の配布ビルドには `libdave` / `opus` / `libsodium` を同梱します。出典: https://docs.discordnet.dev/guides/voice/sending-voice.html / `.nuget\packages\discord.net.websocket\3.20.1\lib\net8.0\Discord.Net.WebSocket.xml`
- Discord bot による Go Live 相当の画面共有は未実装です。DiscordのVoice Gatewayにはvideo世代の記述がありますが、Discord.Net 3.20.1 の公開 `IAudioClient` API はPCM/Opusの音声ストリーム向けで、VALOWATCHでbotから安定してカメラ映像/画面共有を出す実装はまだ採用していません。出典: https://docs.discord.com/developers/topics/voice-connections / https://docs.discordnet.dev/api/Discord.Audio.IAudioClient.html
- 画面/カメラ録画はFFmpegを使います。配布用GitHub ActionsではBtbN/FFmpeg-BuildsのWindows LGPL sharedビルドを同梱します。
- ユーザー発言中の `stats.gg` は、直前に指定された確定 URL `https://strats.gg/valorant/lineups` の意味として扱っています。

## 関連資料

- [実現可能性メモ](docs/FEASIBILITY_JA.md)
- [Discord とオーバーレイの実現可能性メモ](docs/DISCORD_AND_OVERLAY_FEASIBILITY_JA.md)
- [Discord向け説明文と音声調整メモ](docs/DISCORD_APP_DESCRIPTION_JA.md)
- [段階実装メモ](docs/STAGED_IMPLEMENTATION_JA.md)
