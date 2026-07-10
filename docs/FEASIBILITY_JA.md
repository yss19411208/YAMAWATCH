# VALOWATCH 実現可能性メモ

作成日: 2026-07-10
対象: Windows 10/11, .NET 8 WinForms, VALORANT PC版

## 前提

現在の要件は、VALORANT 起動中に `Alt + T` で strats.gg のラインナップページを表示/非表示することです。初期案にあった tracker.gg 風のランク表示、試合開始検知、録音状態表示は現MVPの中心から外しています。

## 成立していること

- VALORANT 本体のプロセスが存在するかは Windows のプロセス一覧から確認できます。
- WebView2 を使うことで WinForms アプリ内にWebページを埋め込めます。
- `RegisterHotKey` により、アプリが非表示でも `Alt + T` を受け取れます。
- Windows のユーザー単位 `Run` レジストリキーに登録すれば、次回ログオン時に起動できます。

## 不確かなこと

- ダウンロード完了だけでアプリを自動実行する公式な安全手段は確認できませんでした。現実装では、ユーザーがインストーラーを1回実行する前提です。
- フルスクリーン排他モードの VALORANT 上に通常ウィンドウを必ず重ねられるとは確認できません。ボーダーレス/ウィンドウ表示を推奨します。
- Discord bot がユーザー操作なしで Go Live 相当の画面共有を開始する安定した公式手段は確認できませんでした。
- tracker.gg のページ内容を自動取得してランクを抽出する実装は、利用規約やスクレイピング制限の確認が必要です。現MVPでは行いません。

## 出典

- Microsoft WebView2 WinForms: https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winforms
- Microsoft WebView2 配布: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- Microsoft Run/RunOnce registry keys: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
- Discord.Net Sending Voice: https://docs.discordnet.dev/guides/voice/sending-voice.html
- strats.gg lineups: https://strats.gg/valorant/lineups

## 実装判断

今回の実装は、ゲームメモリや非公式VALORANT内部APIへ触れません。安全寄りのMVPとして、OSプロセス検知、通常ウィンドウの最前面表示、WebView2、ユーザー単位スタートアップ登録だけで構成しています。

このため、Tracker Network からの自動ランク取得や試合中プレイヤーの自動解析よりも制限はありますが、アカウントやゲームクライアントへの干渉リスクを抑えられます。
