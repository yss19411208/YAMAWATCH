# VALOWATCH

VALOWATCH は、VALORANT 起動中に `Alt + T` で strats.gg のラインナップページを表示/非表示する Windows 常駐アプリです。

重要: VALORANT の実ゲーム画面の上に安定して出す本命は、通常 exe 版ではなく [Overwolf版](overwolf/VALOWATCH/README.md) です。Tracker.gg も Overwolf 系のゲーム内オーバーレイとして提供されています。

## いま入っている動作

- 起動すると通常画面を出さず、タスクトレイでバックグラウンド常駐します。
- `Alt + T` は VALORANT が起動している時だけ反応します。
- オーバーレイには `https://strats.gg/valorant/lineups` だけを表示します。
- 初回表示後は同じ WebView2 ウィンドウを `Show` / `Hide` するため、表示切替のたびに新しい画面を作り直しません。
- 非表示直後は短時間だけページを保持します。高メモリ時、または非表示が続いた時は WebView2 を破棄して軽量化します。
- オーバーレイ表示時は VALORANT のフォーカスを奪わないように、非アクティブ表示を使います。
- オーバーレイ全体を少し透過させています。
- VALORANT ウィンドウの位置が取れる場合は、その上に寄せてオーバーレイを出します。
- インストーラーを実行すると `%LocalAppData%\VALOWATCH\app\VALOWATCH.exe` に本体を配置し、ユーザー単位の Windows スタートアップに登録します。
- Discord bot 設定が有効な場合、VALORANT 起動検知で指定 VC に入り、PC 出力音声のリレーを開始します。
- `VALOWATCH_UPDATE_REPOSITORY` が設定されている場合、VALORANT 起動時に GitHub Releases の更新確認を行います。
- インストーラーは `%LocalAppData%\VALOWATCH\config\.env` と `%LocalAppData%\VALOWATCH\overwolf\VALOWATCH` も作成します。
- インストーラーは Overwolf 本体が見つからない場合、公式 Overwolf インストーラーをダウンロードし、Windows 実行ファイルと署名を確認してから起動します。

## 使い方

配布用インストーラー:

```powershell
.\installer\VALOWATCH_Setup.exe
```

## 全自動化される範囲

インストーラー実行後、VALOWATCH 本体は `%LocalAppData%\VALOWATCH\app\VALOWATCH.exe` に配置され、すぐ起動します。次回以降の Windows ログオンでも起動するように、`Run` レジストリとスタートアップフォルダの `VALOWATCH.cmd` の両方に登録します。

Overwolf 本体が無い場合は、公式 Overwolf インストーラーをダウンロードし、Windows 実行ファイルと署名を確認してから起動します。Overwolf 本体が入った後、VALOWATCH の Overwolf 版アプリは次の場所へ展開済みになります。

```text
%LocalAppData%\VALOWATCH\overwolf\VALOWATCH
```

ただし、未公開の自作 Overwolf アプリを Overwolf クライアントへ完全無操作で登録する公式手順は、現在確認できていません。公開アプリとして配布するか、Overwolf の開発者向け機能で unpacked extension として読み込む必要があります。

Overwolfで公開した場合に自動化されるのは、VALORANT上に strats.gg を出すゲーム内オーバーレイ部分です。Discord bot、PC音声リレー、Google Driveアップロードは Windows常駐アプリ `VALOWATCH.exe` 側の機能なので、引き続きこのインストーラーと `.env` が必要です。公開手順は [Overwolf公開メモ](docs/OVERWOLF_PUBLISH_JA.md) を参照してください。

Overwolf 本体のインストールは公式インストーラーを自動起動して行います。Overwolf 公式インストーラーが画面を出す場合は、その画面を完了してください。自作 Overwolf アプリを Overwolf に読み込ませる手順は、[Overwolf版 README](overwolf/VALOWATCH/README.md) を確認してください。

開発用に直接起動する場合:

```powershell
.\exe\VALOWATCH.exe
```

Discord への自動接続を止めて起動する場合:

```powershell
.\exe\VALOWATCH.exe --no-discord
```

## Discord bot 設定

`.env` は次の場所に書きます。

```text
%LocalAppData%\VALOWATCH\config\.env
```

別の方法として、インストーラーと同じフォルダに `.env` を置いてから `VALOWATCH_Setup.exe` を実行すると、初回だけ `%LocalAppData%\VALOWATCH\config\.env` にコピーします。配布フォルダには編集用の `installer\.env.example` を置いています。

1. `%LocalAppData%\VALOWATCH\config\.env` を開きます。
2. `.env` に `DISCORD_BOT_TOKEN`, `DISCORD_GUILD_ID`, `DISCORD_VOICE_CHANNEL_ID` を設定します。
3. `DISCORD_BOT_ENABLED=true` にします。
4. VALOWATCH を再起動します。
5. VALORANT を起動すると、bot が指定 VC に入ります。

VALORANT 起動中に通信状態が悪く Discord 接続に失敗した場合、VALOWATCH は一定間隔で VC 接続を再試行します。

`.env` は `%LocalAppData%\VALOWATCH\config` に保存されます。bot token は秘密情報なので Git に入れないでください。

`.env` の例:

```dotenv
DISCORD_BOT_ENABLED=true
DISCORD_BOT_TOKEN=PASTE_BOT_TOKEN_HERE
DISCORD_GUILD_ID=123456789012345678
DISCORD_VOICE_CHANNEL_ID=123456789012345678
DISCORD_STREAM_PC_AUDIO=true
DISCORD_TRY_SCREEN_SHARE=false
VALOWATCH_UPDATE_CHECK_ENABLED=true
VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH
VALOWATCH_UPDATE_CURRENT_VERSION=0.1.0
VALOWATCH_UPDATE_BRANCH=main
VALOWATCH_UPDATE_CURRENT_COMMIT=
VALOWATCH_GITHUB_TOKEN=
```

## Git update check

VALORANT を起動したタイミングで、GitHub Releases の最新公開リリースを確認できます。

`.env` に次のように設定します。

```dotenv
VALOWATCH_UPDATE_CHECK_ENABLED=true
VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH
VALOWATCH_UPDATE_CURRENT_VERSION=0.1.0
VALOWATCH_UPDATE_BRANCH=main
VALOWATCH_UPDATE_CURRENT_COMMIT=
VALOWATCH_GITHUB_TOKEN=
```

`VALOWATCH_UPDATE_REPOSITORY` は `owner/repo`、`https://github.com/owner/repo`、`git@github.com:owner/repo.git` のどれでも指定できます。

公開リポジトリなら `VALOWATCH_GITHUB_TOKEN` は空で構いません。非公開リポジトリを見る場合だけ、読み取り権限のある GitHub token を入れてください。

GitHub Releases が無い場合は、`VALOWATCH_UPDATE_BRANCH` の最新commit確認に切り替えます。`VALOWATCH_UPDATE_CURRENT_COMMIT` が空の場合、branchにcommitが存在した時点で更新ありとして扱います。

通信環境が悪い場合は、VALORANT 起動中に5分間隔で再試行します。更新が見つかった場合だけ通知を出し、通知をクリックするとリリースページ、配布ファイル、またはbranchのzipを開きます。

## 注意点

- ダウンロード完了だけで任意の exe が勝手に実行される前提にはしていません。ユーザーがインストーラーを1回実行した後、次回以降は Windows ログオン時に常駐します。
- Microsoft は `Run` レジストリキーのプログラムがユーザーのログオン時に実行されると説明しています。出典: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
- WebView2 アプリ配布時は WebView2 Runtime が必要です。Windows 11 では含まれますが、Windows 10 では未導入の端末があり得ます。出典: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- オーバーレイは非アクティブ表示に変更しています。ただしフルスクリーン排他モードのゲームは通常の最前面ウィンドウより前に出ることがあります。まだ最小化される場合は `設定 → グラフィック → 一般 → 画面モード → ウィンドウフルスクリーン → 適用` にしてください。
- Discord.Net の音声ガイドは `libsodium` と `opus` のネイティブ DLL が必要だと説明しています。音声送信で DLL ロードエラーが出る場合は Windows 用 DLL を `VALOWATCH.exe` と同じ場所へ置いてください。出典: https://docs.discordnet.dev/guides/voice/sending-voice.html
- Discord bot による Go Live 相当の画面共有は未実装です。安定した公式 bot ワークフローは今回確認できませんでした。
- ユーザー発言中の `stats.gg` は、直前に指定された確定 URL `https://strats.gg/valorant/lineups` の意味として扱っています。

## 関連資料

- [実現可能性メモ](docs/FEASIBILITY_JA.md)
- [Discord とオーバーレイの実現可能性メモ](docs/DISCORD_AND_OVERLAY_FEASIBILITY_JA.md)
- [段階実装メモ](docs/STAGED_IMPLEMENTATION_JA.md)
- [Overwolf公開メモ](docs/OVERWOLF_PUBLISH_JA.md)
