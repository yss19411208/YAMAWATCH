# Discord とオーバーレイの実現可能性メモ

作成日: 2026-07-10
対象: Windows 10/11, .NET 8 WinForms, VALORANT PC版, Discord.Net 3.20.1

## 現在の方針

現在のMVPは、VALORANT 起動中に `Alt + T` を押した時だけ `https://strats.gg/valorant/lineups` をオーバーレイ表示することです。録音中表示、bot状態、履歴、tracker.gg のURL表示はオーバーレイに出しません。

Discord bot は VALORANT 起動検知をトリガーに指定 VC へ接続し、既定マイク入力をリレーします。LINEプロセスが起動している間は、既定出力音声もミックスできます。`DISCORD_TEXT_CHANNEL_ID` は、VALORANT終了後のMP3/MP4添付共有先として使います。起動通知テキストは送信しません。Discord token などの設定は、配布用ビルド時に `C:\Users\p159yusuke\Documents\VALOWATCH\installer\.env` から `VALOWATCH.exe` へ埋め込みます。

## 根拠

- Microsoft は WinForms で WebView2 を使う手順として `Microsoft.Web.WebView2` SDK パッケージを追加すると説明しています。出典: https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winforms
- Microsoft は WebView2 アプリ配布時に WebView2 Runtime がクライアント端末に必要だと説明しています。出典: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- Microsoft は `SW_SHOWNOACTIVATE` を「ウィンドウを表示するがアクティブ化しない」値として説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow
- Microsoft は `SWP_NOACTIVATE` を「ウィンドウをアクティブ化しない」フラグとして説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
- Microsoft は `Run` レジストリキーのプログラムがユーザーのログオン時に実行されると説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
- Discord.Net の音声送信ガイドは、音声に `libsodium` と `opus` のネイティブライブラリが必要だと説明しています。Discord.Net 3.20.1 の `EnableVoiceDaveEncryption` は、音声暗号化に `libdave` を使う場合、実行ディレクトリに `libdave` のビルドが必要だと説明しています。出典: https://docs.discordnet.dev/guides/voice/sending-voice.html / `.nuget\packages\discord.net.websocket\3.20.1\lib\net8.0\Discord.Net.WebSocket.xml`
- strats.gg の対象ページは `Valorant Lineups, Stats Tracker & More!` のページとして確認しました。出典: https://strats.gg/valorant/lineups

## 実装済み

- タスクトレイ常駐
- VALORANT 本体のプロセス検知
- `Alt + T` のグローバルホットキー
- WebView2 による strats.gg ラインナップ表示
- オーバーレイの表示/非表示切替
- `SW_SHOWNOACTIVATE` / `SWP_NOACTIVATE` による非アクティブ表示
- 少し透過したオーバーレイ表示
- VALORANT ウィンドウ位置に合わせたオーバーレイ配置
- exe 埋め込み `.env` と外部 `.env` による Discord bot 設定読み込み
- Discord bot の VC 接続設定ファイル生成
- 既定マイク入力の Discord bot へのリレー処理
- LINE起動中の既定出力音声ミックス
- VALORANT終了後のDiscord MP3/MP4添付共有
- インストーラーによる `C:\Users\p159yusuke\Documents\VALOWATCH\app` への配置
- ユーザー単位の Windows スタートアップ登録

## 未実装・保留

- Discord bot による Go Live 相当の VALORANT 画面共有
- 試合開始/終了の安定検知
- tracker.gg からの自動ランク取得
- オーバーレイ上のランク/名前パネル
- Google Drive への自動アップロード連携の最終UI

## 注意点

フルスクリーン排他モードでは、通常の Windows 最前面ウィンドウがゲームより前に出ない場合があります。この点は DirectX フック型の本格的なゲームオーバーレイとは違います。現MVPでは安全性と実装速度を優先し、通常の WinForms 最前面ウィンドウを使っています。

Discord.Net の該当ガイド自体に「情報が古い可能性がある」という警告があります。そのため、音声送信まわりは実機 Discord VC での追加検証が必要です。`data\logs\valowatch.log` に `E2EE/DAVE protocol required` または `OpusDecoder` の `DllNotFoundException` が出る場合は、配布 exe に `libdave` / `opus` / `libsodium` が入っていない状態です。
