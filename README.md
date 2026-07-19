# VALOWATCH

VALOWATCH は、VALORANT 起動中の strats.gg オーバーレイと、Discord VCへの物理マイク＋LINE音声中継を行う Windows 10/11・.NET 8 WinForms 常駐アプリです。

## 動作

- 通常画面、設定画面、タスクトレイアイコンは表示しません。
- VALORANT 起動中だけ `Alt + T` で strats.gg を表示・非表示にします。
- WebView2は表示後も保持し、`Alt + T` では同じページを切り替えます。
- `Alt + T` は通常のホットキー登録と、VALORANT起動中だけ動く軽量キー状態監視で検知します。
- ホットキー登録が競合した場合も代替経路で動作し、5秒ごとに通常登録へ復旧します。
- ゲーム安定性を優先し、バックグラウンドRaw Input登録と低レベルキーボードフックは既定で使いません。
- 表示時はオーバーレイへ操作フォーカスを移し、非表示時はVALORANTへ戻します。
- PCログイン後にVALOWATCHが起動した時点でDiscord botをオンラインにします。VALORANT未起動時はVCへ入らず、Gateway接続だけを維持します。
- VALORANT起動時、Discord botが指定VCへ入り、物理マイクとLINEプロセス音声を送ります。
- Discordアプリ音声の中継は既定OFFです。必要な時だけDiscordの `/valowatch-discord-audio enabled:true` でON、`enabled:false` でOFFにできます。
- Discordへ送る物理マイク＋LINE＋任意のDiscordアプリ音声を、無料のローカルVoskモデルで文字起こしし、入室中VCのテキストチャットへ投稿します。
- 文字起こしには、LINE捕捉中は `LINE会話`、Discordアプリ音声中継中はbotが入っているサーバー名とVC名を表示します。
- PCのスピーカー音量、LINEの出力先、Windowsのマイク音量は変更しません。
- Discord音声フレームが5秒停止した場合はVCを自動再接続します。
- 小さいマイク音だけを自動増幅し、無音レベルのノイズは増幅しません。
- 本体とは別の `GITHUB.exe` が常駐し、5分ごとにGitHub Releasesを確認します。
- `VALOWATCH.exe` が異常終了しても、独立した `GITHUB.exe` が5秒間隔で検知して再起動します。
- `GITHUB.exe` 自身もReleaseのSHA-256が異なる場合は、検証済みの新しい監視プロセスへ自己置換します。
- 本体や更新監視が落ちた場合に備え、別プロセスの `VALOWATCH_Start.exe` がDiscordの `/start` を受け取り、`GITHUB.exe` と `VALOWATCH.exe` だけを起動します。
- `/start` は設定済みサーバー内で、サーバー管理権限またはManage Server権限を持つユーザーだけが使えます。
- Windowsログオン時の自動起動はユーザー権限で動く `HKCU Run` とスタートアップフォルダに登録し、5分間隔の監視プロセス生存確認だけをタスクスケジューラへ登録します。

## 削除した機能

次の機能は実装と配布物から削除しています。

- WAV / MP3録音
- VALORANT画面録画
- カメラ録画
- MP3 / MP4のDiscord添付共有
- FFmpeg同梱

文字起こしは録音ファイルを保存しません。短い音声チャンクをメモリ上で処理し、exeに埋め込んだ無料のVosk日本語モデルを初回起動時に自動展開してローカル認識します。外部の有料APIへ音声を送信しません。

過去に作られた録音ファイルはユーザーデータなので自動削除しませんが、今後VALOWATCHが新しく作ることはありません。更新時にはアプリ配下の旧FFmpegツールを削除します。

## インストール

用途ごとにファイルを分けます。

```text
release\VALOWATCH_Complete.exe  新規導入用。Discord設定を埋め込んだ完全インストーラー
release\VALOWATCH_Update.exe    旧版から独立監視方式へ移行する互換用プログラム
release\VALOWATCH_Reinstall.exe 既存環境を一度削除して入れ直す復旧用
```

今回のように、友達のPCですでにVALOWATCHが入っているもののAlt+Tなどが動かない場合は、`VALOWATCH_Reinstall.exe` だけを渡します。実行すると旧app、旧監視プロセス、自動起動、5分生存確認を解除してから再配置します。既存の場所が標準フォルダーと違う場合でも、標準の `Documents\VALOWATCH\app` を新しく作ってそこへ再配置し、自動起動もその場所へ向け直します。`installer\.env` と `data` は保持します。再配置後は、埋め込んだ本体、`GITHUB.exe`、音声DLL、`.env`、自動起動登録、5分生存確認タスク、常駐プロセスを自己診断し、`GITHUB.exe` が起動していなければ再起動を試します。それでも起動しない場合は `VALOWATCH.exe` 本体のフォールバック起動を試します。

`VALOWATCH_Reinstall.exe` はダブルクリック直後に無表示で処理を開始します。インストール画面、確認画面、追加ボタン操作はありません。開発ソースが同居するこのPCでは、ソースを保護するため実行本体を `data\installed\VALOWATCH\app` へ自動配置します。

再インストール結果は、個人フォルダーを置換した小さな診断ログとして `data\logs\installer-result.pending.log` に保留します。`installer\.env` にDiscord bot tokenと `DISCORD_TEXT_CHANNEL_ID` がある場合は、インストーラー自身もDiscordへ直接結果を送ります。直接送信に失敗した場合でもpendingログを残し、VALOWATCHがDiscordへ接続できた時に再送します。通信できない場合は送信位置を進めず、次回接続時に再送します。GitHub書き込みtoken、Discord token、`.env` の内容は診断ログへ含めません。

Windowsのアプリケーション制御で、インストーラー起動後の `GITHUB.exe` または `VALOWATCH.exe` 起動がブロックされた場合は、`WindowsApplicationControlBlocked=true` とブロックされた実行ファイル名、Win32エラー番号をDiscord通知とpendingログへ残します。`VALOWATCH_Reinstall.exe` 自体がWindowsに起動前ブロックされた場合は、プログラムの処理が開始されないためDiscord通知は送れません。

VALORANT起動後は、音声ピーク診断に加えて `data\logs` と `%TEMP%\VALOWATCH` の `.log` / `.txt` をDiscordのコードブロックへ分割して送ります。初回接続時、起動20秒後、以後5分ごと、終了時に未送信行だけを送り、通信失敗時は次回接続で再送します。`.env`、Bot token、暗号化設定、ユーザープロファイルの実パスは送信しません。ログ送信は音声開始後に並行実行するため、過去ログが多くてもマイク開始を待たせません。

Discordで `/app` を実行すると、VALORANTを起動していない時でも、タスクバー以外を含む実行中プログラム名をephemeral応答で表示します。サーバー管理権限を持つユーザーだけが実行できます。プライバシー保護のため、送信するのはプロセス名と個数だけです。フルパス、ウィンドウタイトル、起動引数、PID、Windowsユーザー名、Windows内部プロセスやサービスホスト系は送信しません。

音声DLLが欠落してVCへ参加できない場合も、先にDiscord Gatewayとテキストチャンネルへ接続し、欠落理由とログのコードブロックを送ってから切断します。

GitHub Releaseでは、`GITHUB.exe`、`VALOWATCH_Start.exe`、旧版移行用の `VALOWATCH_Update.exe`、tokenを含まない `VALOWATCH_App.exe` を別アセットとして公開します。`GITHUB.exe` が本体、監視アプリ、Discord起動受付アプリをダウンロードしてSHA-256を検証し、既存の暗号化設定を残したまま置換します。

既定では次へ本体を配置します。

```text
C:\Users\<Windowsユーザー名>\Documents\VALOWATCH\app\VALOWATCH.exe
C:\Users\<Windowsユーザー名>\Documents\VALOWATCH\GITHUB.exe
C:\Users\<Windowsユーザー名>\Documents\VALOWATCH\VALOWATCH_Start.exe
```

インストール直後に起動し、次回以降は自動起動します。PCログイン後、VALORANTを開いていなくてもbotはオンラインになります。VALORANTを開いたまま更新できますが、更新中はbotが短時間VCから抜けて再接続します。

## Discord設定

配布用ビルドの秘密設定は次へ置きます。

```text
C:\Users\p159yusuke\Documents\VALOWATCH\installer\.env
```

最終配布用EXEはこの設定を本体へ埋め込みます。初回起動後、同じWindowsユーザーだけが復号できるDPAPI暗号化設定を `data\config\settings.protected` へ保存します。GitHubの公開更新版にはDiscord tokenを含めず、この暗号化設定から復元します。

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
DISCORD_LINE_AUDIO_VOLUME=1.35
DISCORD_STREAM_DISCORD_AUDIO=false
DISCORD_AUDIO_PROCESS_NAMES=Discord,DiscordCanary,DiscordPTB
DISCORD_AUDIO_VOLUME=0.45
DISCORD_AUDIO_COMMAND_ENABLED=true
VALOWATCH_TRANSCRIPTION_ENABLED=false
VALOWATCH_TRANSCRIPTION_ENGINE=vosk
VALOWATCH_TRANSCRIPTION_MODEL_PATH=
VALOWATCH_TRANSCRIPTION_CHUNK_SECONDS=12
VALOWATCH_TRANSCRIPTION_MIN_PEAK=0.006
VALOWATCH_SCREENSHOT_COMMAND_ENABLED=false
VALOWATCH_UPDATE_CHECK_ENABLED=true
VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH
VALOWATCH_UPDATE_CURRENT_VERSION=0.1.2
VALOWATCH_UPDATE_BRANCH=main
VALOWATCH_UPDATE_CURRENT_COMMIT=
VALOWATCH_GITHUB_TOKEN=
```

`DISCORD_MIC_DEVICE_NAME` が空の場合は、Windowsの既定の通話用・コンソール用マイクを優先します。仮想入力、ステレオミキサー、スピーカー入力は自動選択しません。

LINEが起動している間だけ、`DISCORD_LINE_PROCESS_NAMES` に一致するプロセスのApplication Loopback音声をマイクへ追加します。LINEが無い間はマイクだけを送ります。

Discordアプリ音声は `DISCORD_STREAM_DISCORD_AUDIO=false` が既定です。Discord上で `/valowatch-discord-audio enabled:true` を実行すると、`DISCORD_AUDIO_PROCESS_NAMES` に一致するDiscordデスクトップアプリの音声をマイクへ追加します。OFFに戻す場合は `/valowatch-discord-audio enabled:false` を実行します。このコマンドはサーバー管理権限を持つユーザーだけが使えます。

Discordアプリ音声はプロセス単位で捕捉します。Discord内の話者や、配布先ユーザーがDiscord画面上でどのサーバーを見ているかまでは分離しません。文字起こしに表示するサーバー名とVC名は、botが接続している指定VCの情報です。

スクショ送信は初期OFFです。Discordで `/screenshot on` を実行すると手動送信を許可し、`/screenshot now` でその時点の画面全体をDiscordへ1枚だけ送信します。送信時はDiscordに「スクショ送信中」と表示し、PC画面上には追加UIを出しません。止める場合は `/screenshot off` を実行してください。スクショ画像は送信後にローカル一時ファイルから削除します。5分ごとの自動スクショ送信は行いません。

文字起こしは `VALOWATCH_TRANSCRIPTION_ENABLED=true` で有効化します。GitHub公開更新版では `VALOWATCH_TRANSCRIPTION_ENGINE=vosk` を使い、Vosk日本語smallモデルをexeから `data\models` へ自動展開します。VCがText-In-Voice対応なら入室中VCのテキストチャットへ投稿し、使えない場合だけ `DISCORD_TEXT_CHANNEL_ID` へ投稿します。

## 自動更新

公開更新版は `GITHUB.exe` と `VALOWATCH_App.exe` に分離しています。旧版との互換用に同じ監視プログラムを `VALOWATCH_Update.exe` 名でも公開します。監視アプリは通信断時に `.download` ファイルから再開し、Windows PE形式とGitHub ReleaseのSHA-256 digestが一致したファイルだけを実行します。更新に失敗しても現在の本体と5秒生存監視を継続します。

`GITHUB.exe` は設置済み本体、自分自身、`VALOWATCH_Start.exe` のSHA-256を最新Releaseと比較します。同じファイルは再配置せず、異なるファイルだけを更新するため更新ループを防ぎます。GitHub通信は `VALOWATCH.exe` では行いません。

診断結果は `data\logs\valowatch.log` に保存します。

```powershell
.\VALOWATCH.exe --check-update-schedule
.\VALOWATCH.exe --check-alt-t-input
.\VALOWATCH.exe --check-git-update
.\VALOWATCH.exe --check-update-download
.\VALOWATCH.exe --check-discord-voice-native
.\VALOWATCH.exe --check-microphone
.\VALOWATCH.exe --check-line-loopback
.\VALOWATCH.exe --check-discord-audio-mix
.\VALOWATCH.exe --check-transcription-local
.\VALOWATCH.exe --check-runtime-log-messages
.\VALOWATCH.exe --check-running-process-snapshot
.\VALOWATCH.exe --check-screenshot-capture
.\VALOWATCH.exe --check-screenshot-command
.\VALOWATCH.exe --check-update-identity --expected-current-commit=<commit SHA>
.\VALOWATCH_Start.exe --check-start-agent
```

更新用EXEの同一本体判定は `VALOWATCH_Update.exe --check-installed-app-hash --install-dir <appフォルダー> --expected-sha256 <SHA-256>` でネット接続なしに検証できます。

## 注意

- bot tokenを埋め込んだローカル配布EXEは公開しないでください。
- 友達のPCからGitHubへ直接ログをpushする構成にはしません。配布EXEへ書き込み用GitHub tokenを埋め込むと、抽出されたtokenでリポジトリを変更される危険があるためです。
- Windowsユーザーを変更した場合や `settings.protected` を削除した場合は、秘密設定入りの最終配布用EXEを再実行する必要があります。
- フルスクリーン排他モードでは通常のWindowsオーバーレイを重ねられません。VALORANTは `設定 → グラフィック → 一般 → 画面モード → ウィンドウフルスクリーン → 適用` にします。
