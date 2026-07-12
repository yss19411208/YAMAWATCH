# VALOWATCH

VALOWATCH は、VALORANT 起動中の strats.gg オーバーレイと、Discord VCへの物理マイク＋LINE音声中継だけを行う Windows 10/11・.NET 8 WinForms 常駐アプリです。

## 動作

- 通常画面、設定画面、タスクトレイアイコンは表示しません。
- VALORANT 起動中だけ `Alt + T` で strats.gg を表示・非表示にします。
- WebView2は表示後も保持し、`Alt + T` では同じページを切り替えます。
- `Alt + T` は通常のホットキー登録、UIから独立した10msキー状態監視、物理キーボードのRaw Inputで多重検知します。
- ホットキー登録が競合した場合も代替経路で動作し、5秒ごとに通常登録へ復旧します。
- 表示時はオーバーレイへ操作フォーカスを移し、非表示時はVALORANTへ戻します。
- VALORANT起動時、Discord botが指定VCへ入り、物理マイクとLINEプロセス音声だけを送ります。
- PCのスピーカー音量、LINEの出力先、Windowsのマイク音量は変更しません。
- Discord音声フレームが5秒停止した場合はVCを自動再接続します。
- 小さいマイク音だけを自動増幅し、無音レベルのノイズは増幅しません。
- 常駐中は5分ごと、VALORANT起動時は即時にGitHub Releasesを確認して自動更新します。
- Windowsログオン時の自動起動と、5分間隔の生存確認タスクを登録します。

## 削除した機能

次の機能は実装と配布物から削除しています。

- WAV / MP3録音
- VALORANT画面録画
- カメラ録画
- MP3 / MP4のDiscord添付共有
- FFmpeg同梱

過去に作られた録音ファイルはユーザーデータなので自動削除しませんが、今後VALOWATCHが新しく作ることはありません。更新時にはアプリ配下の旧FFmpegツールを削除します。

## インストール

用途ごとにファイルを分けます。

```text
release\VALOWATCH_Complete.exe  新規導入用。Discord設定を埋め込んだ完全インストーラー
release\VALOWATCH_Update.exe    既存環境用。GitHub更新だけを行う専用プログラム
```

GitHub Releaseでは、更新専用の `VALOWATCH_Update.exe` と、tokenを含まない `VALOWATCH_App.exe` を別アセットとして公開します。更新専用EXEが本体をダウンロードしてSHA-256を検証し、既存の暗号化設定を残したまま置換します。

既定では次へ本体を配置します。

```text
C:\Users\<Windowsユーザー名>\Documents\VALOWATCH\app\VALOWATCH.exe
```

インストール直後に起動し、次回以降は自動起動します。VALORANTを開いたまま更新できますが、更新中はbotが短時間VCから抜けて再接続します。

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
DISCORD_LINE_AUDIO_VOLUME=0.45
VALOWATCH_UPDATE_CHECK_ENABLED=true
VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH
VALOWATCH_UPDATE_CURRENT_VERSION=0.1.2
VALOWATCH_UPDATE_BRANCH=main
VALOWATCH_UPDATE_CURRENT_COMMIT=
VALOWATCH_GITHUB_TOKEN=
```

`DISCORD_MIC_DEVICE_NAME` が空の場合は、Windowsの既定の通話用・コンソール用マイクを優先します。仮想入力、ステレオミキサー、スピーカー入力は自動選択しません。

LINEが起動している間だけ、`DISCORD_LINE_PROCESS_NAMES` に一致するプロセスのApplication Loopback音声をマイクへ追加します。LINEが無い間はマイクだけを送ります。

## 自動更新

公開更新版は `VALOWATCH_Update.exe` と `VALOWATCH_App.exe` に分離しています。更新専用EXEは通信断時に `.download` ファイルから再開し、Windows PE形式とGitHub ReleaseのSHA-256 digestが一致した本体だけを実行します。更新に失敗した場合は旧本体を再起動します。

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
```

## 注意

- bot tokenを埋め込んだローカル配布EXEは公開しないでください。
- Windowsユーザーを変更した場合や `settings.protected` を削除した場合は、秘密設定入りの最終配布用EXEを再実行する必要があります。
- フルスクリーン排他モードでは通常のWindowsオーバーレイを重ねられません。VALORANTは `設定 → グラフィック → 一般 → 画面モード → ウィンドウフルスクリーン → 適用` にします。
