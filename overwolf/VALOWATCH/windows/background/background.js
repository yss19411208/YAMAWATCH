"use strict";

const overlayWindowName = "overlay";
const toggleHotkeyName = "toggle_strats";

let overlayVisible = false;

function log(message, payload) {
  if (payload === undefined) {
    console.log(`[VALOWATCH] ${message}`);
    return;
  }

  console.log(`[VALOWATCH] ${message}`, payload);
}

function getOverlayWindow(callback) {
  overwolf.windows.obtainDeclaredWindow(overlayWindowName, (result) => {
    if (!result || result.status !== "success") {
      log("overlay window was not obtained", result);
      return;
    }

    callback(result.window.id);
  });
}

function showOverlay() {
  getOverlayWindow((windowId) => {
    overwolf.windows.restore(windowId, (restoreResult) => {
      if (restoreResult && restoreResult.status === "success") {
        overlayVisible = true;
      }

      log("overlay restore", restoreResult);
    });
  });
}

function hideOverlay() {
  getOverlayWindow((windowId) => {
    overwolf.windows.hide(windowId, (hideResult) => {
      overlayVisible = false;
      log("overlay hide", hideResult);
    });
  });
}

function toggleOverlay() {
  getOverlayWindow((windowId) => {
    overwolf.windows.getWindowState(windowId, (stateResult) => {
      const windowState = stateResult && (stateResult.window_state || stateResult.state);
      const visibleState = windowState === "normal" || windowState === "maximized";

      if (overlayVisible || visibleState) {
        hideOverlay();
        return;
      }

      showOverlay();
    });
  });
}

function registerHotkeyListener() {
  overwolf.settings.hotkeys.onPressed.addListener((hotkeyResult) => {
    if (!hotkeyResult || hotkeyResult.name !== toggleHotkeyName) {
      return;
    }

    toggleOverlay();
  });
}

function registerGameListener() {
  overwolf.games.onGameInfoUpdated.addListener((gameInfoResult) => {
    const gameInfo = gameInfoResult && gameInfoResult.gameInfo;
    if (!gameInfo || gameInfo.isRunning) {
      return;
    }

    hideOverlay();
  });
}

function initialize() {
  registerHotkeyListener();
  registerGameListener();
  hideOverlay();
  log("background initialized");
}

initialize();
