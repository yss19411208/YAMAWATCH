# VALOWATCH Overwolf 公開メモ

## 結論

VALOWATCH のゲーム内オーバーレイを Overwolf に追加できるようにするには、Overwolf 側で公開/承認されたアプリにする必要があります。

このPCでは `OverwolfLauncher.exe -install-opk VALOWATCH.opk` を試しましたが、Overwolfログで `Block unauthorized extension [silent: False]: VALOWATCH` と記録され、未承認OPKとして拒否されました。

## .env の扱い

`.env` は Overwolf 公開アプリではなく、Windows常駐アプリ `VALOWATCH.exe` が読みます。

```text
%LocalAppData%\VALOWATCH\config\.env
```

Overwolf公開アプリだけをユーザーが入れた場合、この `.env` は自動作成されません。Discord bot / PC音声リレーを使う場合は、引き続き `VALOWATCH_Setup.exe` で Windows常駐アプリを入れる必要があります。

Overwolf公開アプリで担当する範囲は、VALORANT上に `https://strats.gg/valorant/lineups` を出す in-game overlay です。

## 公開前の成果物

提出用OPK:

```text
C:\Users\p159yusuke\Documents\VALOWATCH\overwolf\VALOWATCH.opk
```

ソースフォルダ:

```text
C:\Users\p159yusuke\Documents\VALOWATCH\overwolf\VALOWATCH
```

## 公開で必要になりやすい作業

1. Overwolf Developer Console にログインします。
2. 新しいアプリを作成します。
3. アプリ名、説明、カテゴリ、対象ゲームを設定します。
4. パッケージとして `VALOWATCH.opk` をアップロードします。
5. スクリーンショット、説明文、アイコン類、権限の理由を登録します。
6. レビューへ提出します。

## 現在のmanifest

`manifest.json` には公開で必要になりやすい次の項目を入れています。

- `dock_button_title`
- `icon`
- `icon_gray`
- `launcher_icon`
- `window_icon`
- `game_targeting`
- `launch_events`
- `hotkeys`

Overwolf公式docsでは、Appstore submission には `dock_button_title`, `icon_gray`, `launcher_icon`, `window_icon` も必要と説明されています。

## 注意

VALOWATCH の Overwolf版は strats.gg を表示するだけです。Discord bot、PC音声配信、Google Drive アップロードは Windows常駐アプリ側の機能です。
