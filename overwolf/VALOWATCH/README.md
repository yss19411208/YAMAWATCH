# VALOWATCH Overwolf Overlay

VALORANT の上に確実に出すための Overwolf 版です。通常の WinForms 最前面ウィンドウでは、VALORANT の描画方式やフルスクリーン状態によってゲームの下に隠れることがあります。

## 使い方

1. Overwolf をインストールします。
2. Overwolf の開発者向け機能で unpacked extension を読み込みます。
3. このフォルダを指定します。

```text
C:\Users\p159yusuke\Documents\VALOWATCH\overwolf\VALOWATCH
```

インストーラー実行後は、次の場所にも同じOverwolf版が展開されます。

```text
%LocalAppData%\VALOWATCH\overwolf\VALOWATCH
```

`installer\VALOWATCH_Setup.exe` は Overwolf 本体が見つからない場合、公式 Overwolf インストーラーをダウンロードし、Windows 実行ファイルと署名を確認してから起動します。既に Overwolf が入っている場合は、この処理はスキップします。

未公開の自作 Overwolf アプリを Overwolf クライアントへ完全無操作で登録する公式手順は、現在確認できていません。完全自動配布にするには、Overwolf の公開/配布フローに載せる必要があります。

## 動作

- VALORANT 起動時に Overwolf 側でアプリが起動します。
- `Alt + T` で in-game overlay window を表示/非表示します。
- 表示内容は `https://strats.gg/valorant/lineups` です。

## 根拠

- Tracker.gg の VALORANT Tracker は Windows PC 専用で、プレイ中に overlay すると説明されています。出典: https://tracker.gg/valorant/app
- Overwolf は supported game で overlay injection を行い、Overwolf app をゲーム上の overlay として表示すると説明しています。出典: https://dev.overwolf.com/ow-native/guides/dev-tools/games-ids/
- Overwolf manifest では `game_targeting`, `launch_events`, `hotkeys`, `in_game_only` window を設定できます。出典: https://dev.overwolf.com/ow-native/reference/manifest/manifest-json/
- Overwolf の sample app は supported game launch、custom hotkey、in-game window の流れを示しています。出典: https://github.com/overwolf/sample-app

## 注意

Overwolf の公式ドキュメントでは、正確な Game ID はローカルの `%LocalAppData%\Overwolf\gamelist*.xml` で確認すると説明されています。この manifest では VALORANT 用として `21640` を使っています。もし起動しない場合は、ローカルの gamelist で VALORANT の Game ID を確認して `manifest.json` の `21640` を置き換えてください。
