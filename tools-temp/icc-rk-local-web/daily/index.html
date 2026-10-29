<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <meta http-equiv="Cache-Control" content="no-cache, no-store, must-revalidate">
  <meta http-equiv="Pragma" content="no-cache">
  <meta http-equiv="Expires" content="0">
  <title>Daily Login Rewards</title>
  <style type="text/css">
    /* Inline critical: AO Awesomium drops linked CSS.
       Board matches Funcom SS (Desktop/zadaily/slika4.png + sve.png):
       7x4 cells, CLAIM REWARD under grid, cell click = item popup. */
    html, body {
      margin:0; padding:0; background:#020810; color:#d4d8dc;
      font-family:Verdana,Geneva,Tahoma,sans-serif; font-size:11px;
    }
    .top {
      position:relative; padding:6px 10px 0 10px;
      max-width:780px; margin:0 auto;
    }
    a.help {
      position:absolute; right:12px; top:4px;
      color:#fff; font-weight:bold; font-size:18px;
      text-decoration:none; z-index:5;
      width:22px; height:22px; text-align:center; line-height:22px;
    }
    .board {
      max-width:780px; margin:0 auto; padding:8px 8px 4px 8px;
      background:#020810;
      background-image:
        -webkit-radial-gradient(center, ellipse cover, #0a3a55 0%, #020810 65%);
      background-image: radial-gradient(ellipse at center, #0a3a55 0%, #020810 65%);
    }
    table.grid {
      width:100%; border-collapse:separate; border-spacing:5px;
      table-layout:fixed;
    }
    td { padding:0; vertical-align:middle; text-align:center; width:14.28%; }
    a.slot {
      display:block; position:relative;
      min-height:54px;
      border:2px solid #6a7380; background:#000;
      text-decoration:none; cursor:default; overflow:hidden;
    }
    a.slot img.icon {
      display:block; width:100%; border:0; vertical-align:top;
    }
    a.slot img.taken-mark {
      display:none; position:absolute; left:0; top:0; width:100%; height:100%;
      border:0; z-index:2;
    }
    a.slot.taken img.taken-mark {
      display:block;
    }
    a.slot .taken-label {
      display:none; position:absolute; left:5px; right:5px; top:38%;
      padding:4px 0; background:#0a1520; color:#d4e8f8;
      font-weight:bold; text-align:center; z-index:3;
    }
    a.slot.taken .taken-label {
      display:block;
    }
    a.slot.taken {
      cursor:default;
      border-color:#8a93a0;
    }
    a.slot.next {
      border:2px solid #3dff7a;
      box-shadow:0 0 10px #1aff66;
    }
    a.slot.selected {
      border:2px solid #ffe14a;
      box-shadow:0 0 8px #c9a600;
    }
    .actions {
      max-width:780px; margin:0 auto;
      padding:10px 8px 6px 8px;
      text-align:center;
      background:#020810;
    }
    a.claim {
      display:inline-block;
      width:208px; height:86px;
      background:#0a1520 url(assets/claim-reward.png) center center no-repeat;
      background-size:208px 86px;
      border:0; text-decoration:none; cursor:pointer;
      vertical-align:middle; line-height:86px;
      color:#d4e8f8; font-weight:bold; font-size:14px;
    }
    a.claim.disabled {
      filter:alpha(opacity=45);
      opacity:0.45;
      cursor:default;
    }
    .info {
      max-width:780px; margin:0 auto;
      padding:8px 12px; min-height:48px;
      background:#0a1218; border-top:1px solid #3a6a82;
      color:#d4e8f8;
    }
    .info b { color:#9fdfff; }
    .status { color:#7dff9a; margin-top:4px; }
    .hint { color:#8a93a0; }

    /* Hover tooltip — appears when mouse is over a reward cell */
    .popup-mask {
      display:none;
    }
    .popup {
      display:none; position:fixed; left:12px; top:12px;
      width:280px; margin:0;
      background:#0a1520; border:2px solid #5ec8ff;
      box-shadow:0 0 18px #0a4a6a; z-index:50;
      padding:12px 14px; color:#d4e8f8;
      pointer-events:none;
    }
    .popup h2 {
      margin:0 0 8px 0; font-size:13px; color:#9fdfff;
    }
    .popup .row { margin:0 0 6px 0; line-height:1.35; }
    .popup .icon-wrap {
      text-align:center; margin:0 0 10px 0;
    }
    .popup .icon-wrap img {
      width:72px; height:72px; border:1px solid #6a7380; background:#000;
    }
    .popup .close {
      display:inline-block; margin-top:8px; padding:4px 14px;
      background:#c8d0d8; color:#111; border:1px solid #1a1c1e;
      text-decoration:none; font-weight:bold;
    }
  </style>
</head>
<body>
  <div class="top">
    <a class="help" href="help.app" id="helpBtn" title="Help">?</a>
  </div>
  <div class="board">
    <table class="grid" id="grid" cellspacing="5" cellpadding="0"></table>
  </div>
  <div class="actions">
    <a class="claim" href="#" id="claimBtn" title="CLAIM REWARD">CLAIM REWARD</a>
  </div>
  <div class="info" id="info">
    <div id="infoTitle"><b>Daily Login Rewards</b></div>
    <div id="infoBody" class="hint">Hover a day for details. Claim only the next available reward for this account (once per day).</div>
    <div class="status" id="claimStatus"></div>
  </div>

    <div class="popup-mask" id="popupMask"></div>
  <div class="popup" id="popup">
    <h2 id="popupTitle">Day</h2>
    <div class="icon-wrap"><img id="popupIcon" src="" alt=""></div>
    <div class="row" id="popupBody"></div>
    <div class="row hint" id="popupHint"></div>
  </div>

  <script type="text/javascript">
  (function () {
    // Cell naming: day{N}-{itemId}-{amount}.png
    // Production: account-wide sequential queue, one claim per UTC day.
    // freeTestMode from rewards.json (must stay false for live).
    var TOTAL = 28;
    var COLS = 7;
    var grid = document.getElementById("grid");
    var infoTitle = document.getElementById("infoTitle");
    var infoBody = document.getElementById("infoBody");
    var status = document.getElementById("claimStatus");
    var claimBtn = document.getElementById("claimBtn");
    var popup = document.getElementById("popup");
    var popupMask = document.getElementById("popupMask");
    var popupTitle = document.getElementById("popupTitle");
    var popupBody = document.getElementById("popupBody");
    var popupHint = document.getElementById("popupHint");
    var popupIcon = document.getElementById("popupIcon");
    var claiming = false;
    var claimedCount = 0;
    var lastClaimUtc = "";
    var lastGrantedUtc = "";
    var selectedDay = 1;
    var freeTestMode = false;
    var knownAccountKey = "";
    var slots = [];
    var hoverHideTimer = null;
    var DAY_ITEM = {};
    var DAY_AMOUNT = {};
    var DAY_IMAGE = {};
    var DAY_BUTTON = {};
    var DAY_QUALITY = {};
    var DAY_NAME = {};
    var TAKEN_DAYS = {};
    var TAKEN_IMG = "assets/cells/taken.png";
    var identityKey = "";
    var nativeCharacterId = "";
    var nativeCharacterHint = false;
    var identitySynchronized = false;
    var serverStateLoaded = false;
    var claimMessage = "";
    var pendingClaimToken = "";
    var pendingClaimStorageKey = "";
    var pollingToken = "";
    var resumeAttempted = false;

    function parseCellName(image) {
      if (!image) return null;
      var base = String(image).replace(/\\/g, "/").split("/");
      base = base[base.length - 1] || "";
      var m = /^day(\d+)-(\d+)-(\d+)\.png$/i.exec(base);
      if (!m) return null;
      return { day: parseInt(m[1], 10), itemId: parseInt(m[2], 10), amount: parseInt(m[3], 10) };
    }

    function applyRewardsJson(j) {
      if (!j) return;
      if (j.takenImage) TAKEN_IMG = j.takenImage;
      if (typeof j.freeTestMode === "boolean") freeTestMode = j.freeTestMode;
      if (!j.days) return;
      var key;
      for (key in j.days) {
        if (!j.days.hasOwnProperty(key)) continue;
        var day = parseInt(key, 10);
        var entry = j.days[key];
        if (!entry || isNaN(day) || day < 1 || day > 28) continue;
        if (entry.buttonImage) {
          DAY_BUTTON[day] = entry.buttonImage;
        } else {
          DAY_BUTTON[day] = "assets/buttons/btn-" + (day < 10 ? "0" + day : String(day)) + ".png";
        }
        if (entry.image) {
          DAY_IMAGE[day] = entry.image;
          var parsed = parseCellName(entry.image);
          if (parsed && parsed.itemId > 0) {
            DAY_ITEM[day] = parsed.itemId;
            DAY_AMOUNT[day] = parsed.amount > 0 ? parsed.amount : 1;
          }
        } else if (DAY_BUTTON[day]) {
          DAY_IMAGE[day] = DAY_BUTTON[day];
        }
        if (typeof entry.itemId === "number" && entry.itemId > 0) {
          DAY_ITEM[day] = entry.itemId;
        }
        if (typeof entry.amount === "number" && entry.amount > 0) {
          DAY_AMOUNT[day] = entry.amount;
        } else if (!DAY_AMOUNT[day]) {
          DAY_AMOUNT[day] = 1;
        }
        if (typeof entry.quality === "number" && entry.quality > 0) {
          DAY_QUALITY[day] = entry.quality;
        }
        if (entry.itemName) {
          DAY_NAME[day] = String(entry.itemName);
        }
      }
    }

    function loadRewards() {
      try {
        var x = new XMLHttpRequest();
        x.open("GET", "rewards.json?t=" + new Date().getTime(), true);
        x.onreadystatechange = function () {
          if (x.readyState === 4) {
            try { applyRewardsJson(eval("(" + x.responseText + ")")); } catch (e2) {}
            loadNames();
            paint();
          }
        };
        x.send(null);
      } catch (e3) {
        loadNames();
      }
    }

    function loadNames() {
      try {
        var x = new XMLHttpRequest();
        x.open("GET", "names.php?t=" + new Date().getTime(), true);
        x.onreadystatechange = function () {
          if (x.readyState === 4 && x.status === 200) {
            try {
              var j = eval("(" + x.responseText + ")");
              if (j && j.days) {
                var key;
                for (key in j.days) {
                  if (!j.days.hasOwnProperty(key)) continue;
                  var day = parseInt(key, 10);
                  var entry = j.days[key];
                  if (!entry || isNaN(day)) continue;
                  if (entry.itemName) DAY_NAME[day] = String(entry.itemName);
                  if (entry.itemId) DAY_ITEM[day] = parseInt(entry.itemId, 10);
                  if (entry.amount) DAY_AMOUNT[day] = parseInt(entry.amount, 10);
                }
              }
              paint();
            } catch (e2) {}
          }
        };
        x.send(null);
      } catch (e3) {}
    }

    function utcDay() {
      try { return new Date().toISOString().slice(0, 10); } catch (e) { return ""; }
    }
    function utcMonth() {
      var d = utcDay();
      return d ? d.slice(0, 7) : "";
    }
    function currentIdentityKey() {
      var idn = resolveIdentity();
      if (idn.id) return "c" + String(idn.id);
      if (idn.name) return "n" + String(idn.name).toLowerCase();
      return "";
    }
    function accountStoragePrefix() {
      var m = utcMonth();
      var ak = knownAccountKey || "pending";
      return "aorebirth.daily.acct.v1." + m + "." + ak;
    }
    function storageKeys() {
      var p = accountStoragePrefix();
      return {
        count: p + ".claimedCount",
        last: p + ".lastClaimUtc",
        granted: p + ".lastGrantedUtc",
        taken: p + ".taken",
        account: currentIdentityKey() ? "aorebirth.daily.identity.v1." + currentIdentityKey() : ""
      };
    }
    function resetTakenMap() {
      TAKEN_DAYS = {};
    }
    function resetBoard() {
      claimedCount = 0;
      lastClaimUtc = "";
      lastGrantedUtc = "";
      selectedDay = 1;
      resetTakenMap();
    }
    function pendingStorageKey() {
      var idn = resolveIdentity();
      return idn.id ? "aorebirth.daily.pending.v1.c" + String(idn.id) : "";
    }
    function loadPending() {
      pendingClaimToken = "";
      pendingClaimStorageKey = pendingStorageKey();
      if (!pendingClaimStorageKey) return;
      try {
        if (window.localStorage) pendingClaimToken = localStorage.getItem(pendingClaimStorageKey) || "";
      } catch (ePending) {}
    }
    function savePending() {
      try {
        if (!window.localStorage) return;
        var key = pendingClaimStorageKey || pendingStorageKey();
        if (!key) return;
        if (pendingClaimToken) localStorage.setItem(key, pendingClaimToken);
        else localStorage.removeItem(key);
      } catch (ePending) {}
    }
    function loadStoredAccountKey() {
      try {
        if (!window.localStorage) return;
        var identity = currentIdentityKey();
        if (!identity) return;
        var ak = localStorage.getItem("aorebirth.daily.identity.v1." + identity);
        if (ak) knownAccountKey = String(ak).toLowerCase();
      } catch (eAk) {}
    }
    function setKnownAccountKey(ak) {
      if (!ak) return;
      var prev = knownAccountKey;
      knownAccountKey = String(ak).toLowerCase();
      try {
        if (window.localStorage) {
          var identity = currentIdentityKey();
          if (identity) localStorage.setItem("aorebirth.daily.identity.v1." + identity, knownAccountKey);
        }
      } catch (eSet) {}
      // A different account must never inherit the previous account's board.
      if (prev !== knownAccountKey) {
        resetBoard();
        if (prev !== "") {
          claiming = false;
          pollingToken = "";
          resumeAttempted = false;
          claimMessage = "";
          loadPending();
        }
      }
    }
    function loadLocal() {
      // Older pages persisted optimistic Taken/dates before a grant existed.
      // Retain those cache entries as history, but never use them as progress.
      // Only an authoritative board or committed result may paint Taken.
      resetBoard();
      loadStoredAccountKey();
    }
    function saveLocal() {
      try {
        if (!window.localStorage) return;
        var k = storageKeys();
        if (knownAccountKey && k.account) {
          localStorage.setItem(k.account, knownAccountKey);
        }
        localStorage.setItem(k.count, String(claimedCount));
        localStorage.setItem(k.last, lastClaimUtc || "");
        localStorage.setItem(k.granted, lastGrantedUtc || "");
        var takenArr = [];
        var d;
        for (d = 1; d <= TOTAL; d++) {
          if (TAKEN_DAYS[d]) takenArr.push(d);
        }
        localStorage.setItem(k.taken, "[" + takenArr.join(",") + "]");
      } catch (e3) {}
    }
    function refreshIdentity() {
      var next = currentIdentityKey();
      if (next !== identityKey) {
        var switching = serverStateLoaded || identityKey !== "" || knownAccountKey !== "" || pendingClaimToken !== "";
        identityKey = next;
        if (switching) {
          knownAccountKey = "";
          resetBoard();
          serverStateLoaded = false;
          claimMessage = "";
          claiming = false;
          pollingToken = "";
          resumeAttempted = false;
          if (identitySynchronized) loadPending();
        }
        paint();
      }
      return identityKey;
    }
    function claimedToday() {
      if (freeTestMode) return false;
      var day = utcDay();
      // Only LastGrantedUtc (Zone grant) locks the day — not old free-test LastClaimUtc.
      return lastGrantedUtc !== "" && lastGrantedUtc === day;
    }
    function nextDay() {
      if (freeTestMode) return selectedDay > 0 ? selectedDay : 1;
      var d;
      for (d = 1; d <= TOTAL; d++) {
        if (!isTaken(d)) return d;
      }
      return 0;
    }
    function markTaken(day) {
      if (day > 0 && day <= TOTAL) TAKEN_DAYS[day] = true;
    }
    function isTaken(day) {
      if (TAKEN_DAYS[day]) return true;
      return false;
    }
    function canClaimNow() {
      if (!serverStateLoaded || claiming) return false;
      if (claimedToday()) return false;
      var next = nextDay();
      if (next < 1) return false;
      // If board knows next day and it has no config, still allow — Zone decides.
      return true;
    }
    function padDay(day) {
      return day < 10 ? "0" + day : String(day);
    }

    function buttonIconSrc(day) {
      if (DAY_BUTTON[day]) return DAY_BUTTON[day];
      return "assets/buttons/btn-" + padDay(day) + ".png";
    }

    function rewardIconSrc(day) {
      // Prefer day button art (always present under assets/buttons).
      // Cell PNGs for remapped IDs/amounts are often missing → black popup.
      if (DAY_IMAGE[day] && String(DAY_IMAGE[day]).indexOf("/buttons/") >= 0) {
        return DAY_IMAGE[day];
      }
      if (DAY_IMAGE[day] && String(DAY_IMAGE[day]).indexOf("buttons/") >= 0) {
        return DAY_IMAGE[day];
      }
      return buttonIconSrc(day);
    }

    function bindIconFallback(img, day) {
      if (!img) return;
      img.onerror = function () {
        var btn = buttonIconSrc(day);
        img.onerror = null;
        img.src = btn;
      };
    }
    function itemIdFor(day) {
      if (DAY_ITEM[day]) return DAY_ITEM[day];
      return 0;
    }
    function amountFor(day) {
      if (DAY_AMOUNT[day]) return DAY_AMOUNT[day];
      return 1;
    }

    function itemNameFor(day) {
      if (DAY_NAME[day]) return DAY_NAME[day];
      var id = itemIdFor(day);
      if (id > 0) return "Reward";
      return "Unknown reward";
    }

    function showInfoPanel(day, extra) {
      var name = itemNameFor(day);
      var amt = amountFor(day);
      var id = itemIdFor(day);
      var title = "Day " + day;
      var body = "";
      if (id > 0) {
        title = name;
        body = "Claim <b>" + name + "</b>" + (amt > 1 ? (" x" + amt) : "") + ".";
      } else {
        title = "Day " + day;
        body = "Reward not configured yet.";
      }
      if (isTaken(day)) {
        body += " Already claimed on this account.";
      } else if (day === nextDay() && !claimedToday()) {
        body += " Available to claim today.";
      } else if (day === nextDay() && claimedToday()) {
        body += " Come back tomorrow for this reward.";
      } else {
        body += " Locked — claim earlier days first.";
      }
      if (extra) body += " " + extra;
      if (infoTitle) infoTitle.innerHTML = "<b>" + title + "</b>";
      if (infoBody) infoBody.innerHTML = body;
    }

    function positionPopupNear(el) {
      if (!popup || !el) return;
      try {
        var r = el.getBoundingClientRect ? el.getBoundingClientRect() : null;
        if (!r) return;
        var left = Math.round(r.left + (r.width / 2) - 140);
        var top = Math.round(r.bottom + 6);
        if (left < 8) left = 8;
        if (left + 280 > (window.innerWidth || 800) - 8) {
          left = (window.innerWidth || 800) - 288;
        }
        if (top + 180 > (window.innerHeight || 600) - 8) {
          top = Math.round(r.top - 186);
        }
        if (top < 8) top = 8;
        popup.style.left = left + "px";
        popup.style.top = top + "px";
      } catch (ePos) {}
    }

    function openPopup(day, anchorEl) {
      var name = itemNameFor(day);
      var amt = amountFor(day);
      var id = itemIdFor(day);
      var title = name;
      var body = "";
      var hint = "";
      if (id > 0) {
        body = "You will receive <b>" + name + "</b>" + (amt > 1 ? (" x" + amt) : "") + ".";
        if (day === 14) {
          hint = "Random Prototype Symbiant at character level +-10 QL.";
        }
      } else {
        title = "Day " + day;
        body = "Reward not configured yet.";
      }
      if (isTaken(day)) {
        hint = "Already claimed on this account.";
      } else if (day === nextDay() && !claimedToday()) {
        hint = "Press CLAIM REWARD below.";
      } else if (day === nextDay() && claimedToday()) {
        hint = "Already claimed today — come back tomorrow.";
      } else {
        hint = "Locked — next reward is day " + nextDay() + ".";
      }
      if (popupTitle) popupTitle.innerHTML = title;
      if (popupBody) popupBody.innerHTML = body;
      if (popupHint) popupHint.innerHTML = hint;
      if (popupIcon) {
        popupIcon.src = isTaken(day) ? TAKEN_IMG : rewardIconSrc(day);
        popupIcon.alt = name;
        bindIconFallback(popupIcon, day);
      }
      if (popup) {
        positionPopupNear(anchorEl);
        popup.style.display = "block";
      }
      if (popupMask) popupMask.style.display = "none";
      showInfoPanel(day, "");
    }

    function closePopup() {
      if (popup) popup.style.display = "none";
      if (popupMask) popupMask.style.display = "none";
      return false;
    }

    function scheduleHidePopup() {
      if (hoverHideTimer) {
        try { clearTimeout(hoverHideTimer); } catch (eT) {}
      }
      hoverHideTimer = setTimeout(function () {
        closePopup();
        hoverHideTimer = null;
      }, 120);
    }

    function cancelHidePopup() {
      if (hoverHideTimer) {
        try { clearTimeout(hoverHideTimer); } catch (eT2) {}
        hoverHideTimer = null;
      }
    }

    function buildGrid() {
      if (!grid) return;
      while (grid.firstChild) grid.removeChild(grid.firstChild);
      slots = [];
      var day = 1;
      var r, c, tr, td, a, icon, mark, takenLabel;
      for (r = 0; r < 4; r++) {
        tr = document.createElement("tr");
        for (c = 0; c < COLS; c++) {
          td = document.createElement("td");
          a = document.createElement("a");
          a.className = "slot";
          a.href = "#";
          a.setAttribute("data-day", String(day));
          a.setAttribute("data-reward", rewardIconSrc(day));
          icon = document.createElement("img");
          icon.className = "icon";
          icon.src = rewardIconSrc(day);
          icon.alt = "";
          bindIconFallback(icon, day);
          mark = document.createElement("img");
          mark.className = "taken-mark";
          mark.src = TAKEN_IMG;
          mark.alt = "Taken";
          a.appendChild(icon);
          a.appendChild(mark);
          takenLabel = document.createElement("span");
          takenLabel.className = "taken-label";
          takenLabel.appendChild(document.createTextNode("Taken"));
          a.appendChild(takenLabel);
          a.onmouseover = (function (d, el) {
            return function () {
              cancelHidePopup();
              openPopup(d, el);
            };
          })(day, a);
          a.onmouseout = function () {
            scheduleHidePopup();
          };
          // Days are hover-info only — never clickable / selectable.
          a.onclick = function () { return false; };
          td.appendChild(a);
          tr.appendChild(td);
          slots.push(a);
          day++;
        }
        grid.appendChild(tr);
      }
    }

    function paint() {
      var next = nextDay();
      var i, a, d, imgs, icon, mark, rewardSrc;
      for (i = 0; i < slots.length; i++) {
        a = slots[i];
        d = parseInt(a.getAttribute("data-day"), 10);
        imgs = a.getElementsByTagName("img");
        icon = null;
        mark = null;
        if (imgs.length > 0) icon = imgs[0];
        if (imgs.length > 1) mark = imgs[1];
        rewardSrc = DAY_IMAGE[d] || a.getAttribute("data-reward") || rewardIconSrc(d);
        a.setAttribute("data-reward", rewardSrc);
        if (icon) {
          icon.src = rewardSrc;
          icon.alt = "";
          bindIconFallback(icon, d);
        }
        if (mark) {
          mark.src = TAKEN_IMG;
        }
        if (isTaken(d)) {
          a.className = "slot taken";
        } else if (d === next && !claimedToday()) {
          a.className = "slot next";
        } else {
          a.className = "slot";
        }
      }
      if (next > 0) {
        selectedDay = next;
      }
      if (claimBtn) {
        if (canClaimNow()) {
          claimBtn.className = "claim";
          claimBtn.style.display = "inline-block";
        } else {
          claimBtn.className = "claim disabled";
          claimBtn.style.display = "none";
        }
      }
      if (status) {
        if (claimMessage) {
          status.innerHTML = claimMessage;
        } else if (!serverStateLoaded) {
          status.innerHTML = "Waiting for the game server to identify this account.";
        } else if (claimedToday()) {
          status.innerHTML = "Daily reward already claimed today on this account. Come back tomorrow.";
        } else if (next < 1) {
          status.innerHTML = "All 28 rewards claimed. Board resets tomorrow.";
        } else if (claimedCount <= 0) {
          status.innerHTML = "Next reward: day " + next + ".";
        } else {
          status.innerHTML = claimedCount + " reward" + (claimedCount === 1 ? "" : "s")
            + " claimed on this account. Next: day " + next + ".";
        }
      }
    }

    function applyClaimResult(j) {
      if (!j || j.ok !== true || j.committed !== true) return false;
      applyServerState(j);
      return true;
    }

    function nativeClaimAvailable() {
      try { return !!window.AORebirthDaily && typeof window.AORebirthDaily.Claim === "function"; }
      catch (eNative) { return false; }
    }

    function invokeNativeClaim() {
      if (!nativeClaimAvailable()) return false;
      return window.AORebirthDaily.Claim() !== false;
    }

    function nativeClaimUnavailable() {
      return "Daily Rewards claim control is unavailable. Update the AORebirth client patch, then retry.";
    }

    function queueClaim(token) {
      var idn = resolveIdentity();
      var x = new XMLHttpRequest();
      x.open("POST", "claim.php", false);
      x.setRequestHeader("Content-Type", "application/json");
      x.send(
        "{\"month\":\"" + utcMonth() + "\""
        + ",\"claimToken\":\"" + token + "\""
        + ",\"characterId\":" + (idn.id ? parseInt(idn.id, 10) || 0 : 0)
        + ",\"accountKey\":\"" + String(knownAccountKey || "").replace(/\\/g, "\\\\").replace(/"/g, "\\\"") + "\""
        + ",\"character\":\"" + String(idn.name || "").replace(/\\/g, "\\\\").replace(/"/g, "\\\"") + "\""
        + "}"
      );
      return eval("(" + x.responseText + ")");
    }

    function newClaimToken() {
      try {
        if (window.crypto && window.crypto.getRandomValues && typeof Uint8Array !== "undefined") {
          var bytes = new Uint8Array(24);
          window.crypto.getRandomValues(bytes);
          var token = "c", i;
          for (i = 0; i < bytes.length; i++) token += (bytes[i] < 16 ? "0" : "") + bytes[i].toString(16);
          return token;
        }
      } catch (eRandom) {}
      // The token correlates retries; it is never proof of account ownership.
      return "c" + String(new Date().getTime()) + Math.floor(Math.random() * 100000);
    }

    function pollClaimResult(token, left, done, rearm) {
      if (token !== pendingClaimToken) return;
      if (!token || left <= 0) {
        done("The request is saved, but the game server has not confirmed delivery yet. Retry to check the same claim.", false, false);
        return;
      }
      try {
        var x = new XMLHttpRequest();
        x.open("GET", "claim.php?token=" + encodeURIComponent(token) + "&t=" + new Date().getTime(), true);
        x.onreadystatechange = function () {
          if (x.readyState !== 4) return;
          if (token !== pendingClaimToken) return;
          try {
            var j = eval("(" + x.responseText + ")");
            if (j && j.ok === true && j.committed === true) {
              applyClaimResult(j);
              done(j.message || "Reward delivered.", true, true);
              return;
            }
            if (j && j.ok === true && (j.legacyResolved === true || (typeof j.committed === "undefined" && j.day > 0))) {
              // A pre-repair success is resolved history, not a new atomic grant.
              // Only the published account board can restore its Taken state.
              done("A prior claim is already recorded. Refreshing the saved account board.", false, true);
              return;
            }
            if (j && j.ok === false) {
              done(j.message || "The game server could not grant this reward.", false, j.terminal === true);
              return;
            }
            if (j && j.pending === true && rearm === true) {
              rearm = false;
              if (!nativeClaimAvailable()) {
                done(nativeClaimUnavailable(), false, false);
                return;
              }
              if (j.armed !== true) {
                var acknowledgement;
                try { acknowledgement = queueClaim(token); }
                catch (eQueue) {
                  done("The saved request could not be queued. Retry to check the same claim.", false, false);
                  return;
                }
                if (!acknowledgement || acknowledgement.ok !== true || acknowledgement.armed !== true || (acknowledgement.claimToken && acknowledgement.claimToken !== token)) {
                  done((acknowledgement && acknowledgement.message) || "The saved request could not be queued. Retry to check it again.", false, false);
                  return;
                }
              }
              try {
                if (!invokeNativeClaim()) {
                  done(nativeClaimUnavailable(), false, false);
                  return;
                }
              } catch (eNative) {
                done("The native claim could not be submitted. Your request is saved for retry.", false, false);
                return;
              }
              claimMessage = "Claim submitted. Waiting for the game server to confirm delivery.";
              paint();
            }
          } catch (e2) {}
          setTimeout(function () { pollClaimResult(token, left - 1, done, rearm); }, 400);
        };
        x.send(null);
      } catch (e3) {
        setTimeout(function () { pollClaimResult(token, left - 1, done, rearm); }, 400);
      }
    }

    function doClaim() {
      if (claiming) return false;
      if (!serverStateLoaded) {
        claimMessage = "Wait for the game server to identify this account before claiming.";
        paint();
        return false;
      }
      if (claimedToday()) {
        if (status) status.innerHTML = "Daily reward already claimed today on this account.";
        paint();
        return false;
      }
      var day = nextDay();
      if (day < 1) {
        if (status) status.innerHTML = "All 28 rewards claimed this month.";
        paint();
        return false;
      }
      if (!nativeClaimAvailable()) {
        claimMessage = nativeClaimUnavailable();
        paint();
        return false;
      }

      claiming = true;
      resumeAttempted = true;
      claimMessage = "Waiting for the game server to grant day " + day + ".";
      paint();

      var token = pendingClaimToken || newClaimToken();
      if (!pendingClaimToken) pendingClaimStorageKey = pendingStorageKey();
      pendingClaimToken = token;
      savePending();

      var done = function (msg, ok, terminal) {
        if (token !== pendingClaimToken) return;
        claiming = false;
        pollingToken = "";
        if (terminal) {
          pendingClaimToken = "";
          savePending();
        }
        claimMessage = msg || (ok ? "Reward delivered." : "Claim is awaiting game-server confirmation.");
        syncFromServer();
        paint();
      };
      try {
        try {
          var j = queueClaim(token);
          if (j && j.ok === false) {
            done(j.message || "Claim failed.", false, j.terminal === true);
            return false;
          }
          if (!j || j.ok !== true || j.armed !== true) {
            done("The claim was not acknowledged. The request is saved for retry.", false, false);
            return false;
          }
          if (j.claimToken && String(j.claimToken) !== token) {
            done("The game server returned a different claim token. The original request is saved.", false, false);
            return false;
          }
        } catch (e2) {
          done("The claim response could not be read. The request is saved for retry.", false, false);
          return false;
        }
        try {
          if (!invokeNativeClaim()) {
            done(nativeClaimUnavailable(), false, false);
            return false;
          }
        } catch (eNative) {
          done("The native claim could not be submitted. Your request is saved for retry.", false, false);
          return false;
        }
        claimMessage = "Claim submitted. Waiting for the game server to confirm delivery.";
        paint();
        pollingToken = token;
        pollClaimResult(token, 20, done);
      } catch (e3) {
        done("Claim failed to reach the server. The request is saved for retry.", false, false);
      }
      return false;
    }

    function resolveIdentity() {
      if (nativeCharacterHint) return { id: nativeCharacterId, name: "" };
      var id = "";
      var name = "";
      try {
        var ao = window.AnarchyOnline;
        if (ao) {
          if (ao.CharacterID != null && ao.CharacterID !== "") id = String(ao.CharacterID);
          else if (ao.CharacterId != null && ao.CharacterId !== "") id = String(ao.CharacterId);
          else if (typeof ao.GetCharacterID === "function") id = String(ao.GetCharacterID() || "");
          else if (typeof ao.getCharacterID === "function") id = String(ao.getCharacterID() || "");
          if (ao.CharacterName) name = String(ao.CharacterName);
          else if (typeof ao.GetCharacterName === "function") name = String(ao.GetCharacterName() || "");
          else if (typeof ao.getCharacterName === "function") name = String(ao.getCharacterName() || "");
        }
      } catch (e) {}
      try {
        if (!id && window.external) {
          if (window.external.CharacterID != null) id = String(window.external.CharacterID);
          if (!name && window.external.CharacterName) name = String(window.external.CharacterName);
        }
      } catch (e2) {}
      return { id: id, name: name };
    }

    function applyServerState(j) {
      if (!j) return;
      if (j.ok !== true) {
        serverStateLoaded = false;
        claimMessage = j.message || "The account board could not be verified. The saved request is unchanged.";
        return;
      }
      if (j.nativeCharacterHint === true) {
        var route = String(j.characterId || "");
        if (!/^[1-9][0-9]{0,9}$/.test(route) || Number(route) > 2147483647) {
          serverStateLoaded = false;
          claimMessage = "The character routing hint could not be verified. The saved request is unchanged.";
          return;
        }
        nativeCharacterHint = true;
        nativeCharacterId = route;
        refreshIdentity();
      }
      if (typeof j.freeTestMode === "boolean") freeTestMode = j.freeTestMode;
      if (j.accountKey) setKnownAccountKey(j.accountKey);
      else if (j.boardAccount) setKnownAccountKey(j.boardAccount);
      else knownAccountKey = "";
      serverStateLoaded = j.hasIdentity === true || (j.hasIdentity !== false && !!knownAccountKey);
      if (!serverStateLoaded) {
        resetBoard();
        return;
      }
      if (!identitySynchronized) {
        identitySynchronized = true;
        loadPending();
      }
      if (typeof j.lastGrantedUtc === "string") lastGrantedUtc = j.lastGrantedUtc;
      if (typeof j.lastClaimUtc === "string") lastClaimUtc = j.lastClaimUtc;
      if (j.taken && typeof j.taken.length === "number") {
        resetTakenMap();
        var i;
        for (i = 0; i < j.taken.length; i++) markTaken(parseInt(j.taken[i], 10));
      }
      claimedCount = 0;
      var d;
      for (d = 1; d <= TOTAL; d++) {
        if (TAKEN_DAYS[d]) claimedCount++;
      }
      saveLocal();
    }

    function syncFromServer() {
      if (identitySynchronized) {
        refreshIdentity();
        loadStoredAccountKey();
      }
      var idn = resolveIdentity();
      var requestedIdentity = currentIdentityKey();
      try {
        var x = new XMLHttpRequest();
        var q = "claim.php?month=" + encodeURIComponent(utcMonth())
          + "&characterId=" + encodeURIComponent(nativeCharacterHint ? "" : (idn.id || ""))
          + "&character=" + encodeURIComponent(idn.name || "")
          + "&accountKey=" + encodeURIComponent(nativeCharacterHint ? "" : (knownAccountKey || ""))
          + "&t=" + new Date().getTime();
        x.open("GET", q, true);
        x.onreadystatechange = function () {
          if (x.readyState === 4 && x.status === 200) {
            if (requestedIdentity !== currentIdentityKey()) return;
            try {
              applyServerState(eval("(" + x.responseText + ")"));
              paint();
            } catch (e2) {}
          }
        };
        x.send(null);
      } catch (e3) {}
    }

    function pollServer(left) {
      if (left <= 0) return;
      setTimeout(function () {
        syncFromServer();
        pollServer(left - 1);
      }, 700);
    }

    function resumePendingClaim() {
      if (!pendingClaimToken || pollingToken || claiming || !serverStateLoaded || resumeAttempted) return;
      resumeAttempted = true;
      var token = pendingClaimToken;
      pollingToken = token;
      claiming = true;
      claimMessage = "Checking the saved claim with the game server.";
      paint();
      pollClaimResult(token, 20, function (msg, ok, terminal) {
        if (token !== pendingClaimToken) return;
        claiming = false;
        pollingToken = "";
        if (terminal) {
          pendingClaimToken = "";
          savePending();
        }
        claimMessage = msg;
        syncFromServer();
        paint();
      }, true);
    }

    function onClaimClick() {
      if (claimedToday()) {
        if (status) status.innerHTML = "Daily reward already claimed today on this account.";
        paint();
        return false;
      }
      // Zone picks first untaken day for the account and grants the item.
      return doClaim();
    }

    if (claimBtn) {
      claimBtn.onclick = function () { return onClaimClick(); };
    }

    function msUntilNextUtcMidnight() {
      var now = new Date();
      var next = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1, 0, 0, 0, 0));
      var ms = next.getTime() - now.getTime();
      return ms < 1000 ? 1000 : ms;
    }
    function scheduleMidnightRefresh() {
      setTimeout(function () {
        // 00:00 UTC — unlock CLAIM for the next untaken day.
        if (lastGrantedUtc && lastGrantedUtc !== utcDay()) {
          if (status) status.innerHTML = "New daily reward available.";
        }
        paint();
        syncFromServer();
        scheduleMidnightRefresh();
      }, msUntilNextUtcMidnight() + 250);
    }

    identityKey = currentIdentityKey();
    resetBoard();
    loadRewards();
    buildGrid();
    paint();
    syncFromServer();
    setTimeout(function () { resumePendingClaim(); }, 1000);
    scheduleMidnightRefresh();
    setInterval(function () {
      refreshIdentity();
      // Also re-check day rollover every minute (backup for midnight timer).
      paint();
      syncFromServer();
      resumePendingClaim();
    }, 60000);
  })();
  </script>
</body>
</html>
