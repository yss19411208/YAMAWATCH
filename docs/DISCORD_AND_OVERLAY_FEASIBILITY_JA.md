# Discord とオーバーレイの実現可能性メモ

作成日: 2026-07-10
対象: Windows 10/11, .NET 8 WinForms, VALORANT PC版, Discord.Net 3.20.1

## 現在の方針

現在のMVPは、VALORANT 起動中に `Alt + T` を押した時だけ `https://strats.gg/valorant/lineups` をオーバーレイ表示することです。録音中表示、bot状態、履歴、tracker.gg のURL表示はオーバーレイに出しません。

Discord bot は VALORANT 起動検知をトリガーに指定 VC へ接続し、物理マイク入力を外部音としてリレーします。LINE音声中継が有効な場合は、Windowsのprocess loopbackでLINEプロセス音声だけを追加ミックスできます。このミックスはDiscord bot送信用であり、Windowsの再生デバイスやPCから聞こえる音は変更しません。録音、録画、ファイル共有、画面共有は行いません。`DISCORD_TEXT_CHANNEL_ID` は、VALORANT起動、使用マイク機種、更新完了の通知先として使います。

## 根拠

- Microsoft は WinForms で WebView2 を使う手順として `Microsoft.Web.WebView2` SDK パッケージを追加すると説明しています。出典: https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winforms
- Microsoft は WebView2 アプリ配布時に WebView2 Runtime がクライアント端末に必要だと説明しています。出典: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- Microsoft は `RegisterHotKey` が、同じ組み合わせを別のホットキーが登録済みの場合に失敗し得ると説明しています。出典: https://learn.microsoft.com/ja-jp/windows/win32/api/winuser/nf-winuser-registerhotkey
- Microsoft は `RIDEV_INPUTSINK` を指定すると、対象ウィンドウがフォアグラウンドでなくてもRaw Inputを受信できると説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice
- Microsoft は最前面ウィンドウを実際に操作対象へする場合、`SetForegroundWindow` にWindows側の制限があると説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow
- Microsoft は `Run` レジストリキーのプログラムがユーザーのログオン時に実行されると説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
- Discord.Net の音声送信ガイドは、音声に `libsodium` と `opus` のネイティブライブラリが必要だと説明しています。Discord.Net 3.20.1 の `EnableVoiceDaveEncryption` は、音声暗号化に `libdave` を使う場合、実行ディレクトリに `libdave` のビルドが必要だと説明しています。出典: https://docs.discordnet.dev/guides/voice/sending-voice.html / `.nuget\packages\discord.net.websocket\3.20.1\lib\net8.0\Discord.Net.WebSocket.xml`
- strats.gg の対象ページは `Valorant Lineups, Stats Tracker & More!` のページとして確認しました。出典: https://strats.gg/valorant/lineups

## 実装済み

- UIを出さないバックグラウンド常駐
- VALORANT 本体のプロセス検知
- `Alt + T` のグローバルホットキー
- 専用10msキー状態監視とバックグラウンドRaw Inputによる代替検知
- WebView2 による strats.gg ラインナップ表示
- オーバーレイの表示/非表示切替
- 表示時の操作フォーカス取得と、非表示時のVALORANTフォーカス復帰
- 非表示中のWebView2ページ保持
- 少し透過したオーバーレイ表示
- VALORANT ウィンドウ位置に合わせたオーバーレイ配置
- exe 埋め込み `.env` と外部 `.env` による Discord bot 設定読み込み
- Discord bot の VC 接続設定ファイル生成
- 既定マイク入力の Discord bot へのリレー処理
- 外部音としての既定マイク入力 + LINEプロセス音声だけのDiscord送信用ミックス
- インストーラーによる `C:\Users\p159yusuke\Documents\VALOWATCH\app` への配置
- ユーザー単位の Windows スタートアップ登録

## 未実装・保留

- 試合開始/終了の安定検知
- tracker.gg からの自動ランク取得
- オーバーレイ上のランク/名前パネル

## 注意点

フルスクリーン排他モードでは、通常の Windows 最前面ウィンドウがゲームより前に出ない場合があります。この点は DirectX フック型の本格的なゲームオーバーレイとは違います。現MVPでは安全性と実装速度を優先し、通常の WinForms 最前面ウィンドウを使っています。

Discord.Net の該当ガイド自体に「情報が古い可能性がある」という警告があります。そのため、音声送信まわりは実機 Discord VC での追加検証が必要です。`data\logs\valowatch.log` に `E2EE/DAVE protocol required` または `OpusDecoder` の `DllNotFoundException` が出る場合は、配布 exe に `libdave` / `opus` / `libsodium` が入っていない状態です。
