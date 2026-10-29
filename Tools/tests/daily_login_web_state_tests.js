"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const indexPath = path.join(root, "tools-temp/icc-rk-local-web/daily/index.app");
const indexSource = fs.readFileSync(indexPath, "utf8");
const script = indexSource.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
const today = new Date().toISOString().slice(0, 10);

class Element {
  constructor(tag) {
    this.tagName = tag;
    this.children = [];
    this.attributes = {};
    this.style = {};
    this.innerHTML = "";
  }
  get firstChild() { return this.children[0] || null; }
  appendChild(child) { this.children.push(child); }
  removeChild(child) { this.children.splice(this.children.indexOf(child), 1); }
  setAttribute(key, value) { this.attributes[key] = value; }
  getAttribute(key) { return this.attributes[key]; }
  getElementsByTagName(tag) {
    return this.children.flatMap(child => (child.tagName === tag ? [child] : []).concat(child.getElementsByTagName(tag)));
  }
}

function browser(seed, identity, withoutNative) {
  const elements = {};
  elements.claimBtn = new Element("a");
  elements.claimBtn.innerHTML = indexSource.match(/<a[^>]*id="claimBtn"[^>]*>([\s\S]*?)<\/a>/)[1];
  const requests = [];
  const timeouts = [];
  const intervals = [];
  const stored = new Map(Object.entries(seed || {}));
  const nativeCalls = [];
  const localStorage = {
    getItem: key => stored.has(key) ? stored.get(key) : null,
    setItem: (key, value) => stored.set(key, String(value)),
    removeItem: key => stored.delete(key)
  };
  const native = { CharacterID: identity || 101, CharacterName: "First", SendCharacterAction: action => nativeCalls.push(action) };
  class Request {
    open(method, url, async) { this.method = method; this.url = url; this.async = async; }
    setRequestHeader() {}
    send(body) {
      this.body = body;
      requests.push(this);
      if (this.async === false && this.method === "POST") {
        this.status = 200;
        this.responseText = JSON.stringify({ ok: true, armed: true, claimToken: JSON.parse(body).claimToken });
        this.readyState = 4;
      }
    }
  }
  const context = {
    document: {
      getElementById: id => elements[id] || (elements[id] = new Element("div")),
      createElement: tag => new Element(tag),
      createTextNode: text => Object.assign(new Element("#text"), { textContent: text })
    },
    localStorage,
    XMLHttpRequest: Request,
    Date,
    Math,
    encodeURIComponent,
    setTimeout: (fn, delay) => { timeouts.push({ fn, delay }); return timeouts.length; },
    clearTimeout() {},
    setInterval: (fn, delay) => { intervals.push({ fn, delay }); return intervals.length; }
  };
  context.window = { localStorage };
  if (!withoutNative) context.window.AnarchyOnline = native;
  vm.runInNewContext(script, context, { filename: indexPath });
  function response(request, payload) {
    assert.ok(request, "Expected a pending HTTP request");
    request.status = 200;
    request.readyState = 4;
    request.responseText = JSON.stringify(payload);
    if (request.onreadystatechange) request.onreadystatechange();
  }
  function state(payload) {
    const request = requests.filter(item => item.method === "GET" && item.url.startsWith("claim.php?month=") && item.readyState !== 4).pop();
    response(request, Object.assign({ ok: true, hasIdentity: true, accountKey: "first", taken: [], lastGrantedUtc: "", lastClaimUtc: "", claimedCount: 0 }, payload));
  }
  function poll(payload) {
    const request = requests.filter(item => item.method === "GET" && item.url.startsWith("claim.php?token=") && item.readyState !== 4).pop();
    response(request, payload);
  }
  const slots = () => elements.grid.children.flatMap(row => row.children.map(cell => cell.children[0]));
  const taken = () => slots().filter(slot => slot.className === "slot taken").length;
  return { elements, requests, timeouts, intervals, stored, native, nativeCalls, response, state, poll, taken };
}

function test(name, run) {
  run();
  process.stdout.write("PASS " + name + "\n");
}

test("Old optimistic browser progress never renders or locks a day before authoritative synchronization", () => {
  const prefix = "aorebirth.daily.acct.v1." + today.slice(0, 7) + ".first";
  const page = browser({
    "aorebirth.daily.identity.v1.c101": "first",
    "aorebirth.daily.acct.v1.accountKey": "first",
    [prefix + ".claimedCount"]: "1",
    [prefix + ".taken"]: "[1]",
    [prefix + ".lastClaimUtc"]: today,
    [prefix + ".lastGrantedUtc"]: today
  });
  assert.equal(page.taken(), 0, "Historic optimistic cache must not paint Taken");
  assert.doesNotMatch(page.elements.claimStatus.innerHTML, /already claimed/i, "Historic optimistic dates must not establish a daily lock");
  assert.match(page.elements.claimStatus.innerHTML, /Waiting for the game server/);
  assert.equal(page.stored.get(prefix + ".taken"), "[1]", "Untrusted historical cache is preserved until authoritative synchronization");
  page.state({ taken: [], lastGrantedUtc: "", lastClaimUtc: "" });
  assert.equal(page.taken(), 0);
  assert.equal(page.elements.claimBtn.style.display, "inline-block");
  page.intervals[0].fn();
  page.state({ taken: [1], lastGrantedUtc: today, lastClaimUtc: today });
  assert.equal(page.taken(), 1, "Server progress remains authoritative");
});

test("A queued claim never marks Taken before a committed grant", () => {
  const page = browser();
  page.state();
  page.elements.claimBtn.onclick();
  assert.equal(page.taken(), 0);
  assert.equal(page.nativeCalls.length, 0, "A queued request must use normal window closure, not guessed JavaScript calls");
  assert.match(page.elements.claimStatus.innerHTML, /Close the Daily Login window/);
  assert.ok([...page.stored.values()].some(value => /^c\d+$/.test(value)), "Pending token must survive closing the page");
  page.poll({ ok: true, armed: true });
  assert.equal(page.taken(), 0, "An armed or older success response is not a committed grant");
});

test("A committed result marks the authoritative board and clears only its pending pointer", () => {
  const page = browser();
  page.state();
  page.elements.claimBtn.onclick();
  page.poll({ ok: true, committed: true, hasIdentity: true, accountKey: "first", taken: [1], lastGrantedUtc: today, lastClaimUtc: today, message: "Reward delivered." });
  assert.equal(page.taken(), 1);
  assert.equal(page.elements.claimBtn.style.display, "none");
  assert.equal([...page.stored.keys()].filter(key => key.startsWith("aorebirth.daily.pending.")).length, 0);
});

test("An empty authoritative board clears Taken and both dates", () => {
  const page = browser();
  page.state({ taken: [1, 2], lastGrantedUtc: today, lastClaimUtc: today });
  assert.equal(page.taken(), 2);
  page.intervals[0].fn();
  page.state({ taken: [], lastGrantedUtc: "", lastClaimUtc: "" });
  assert.equal(page.taken(), 0);
  assert.equal(page.elements.claimBtn.style.display, "inline-block");
});

test("A completed board unlocks day one when the server supplies its UTC rollover projection", () => {
  const page = browser();
  const yesterday = new Date(Date.now() - 86400000).toISOString().slice(0, 10);
  page.state({ taken: Array.from({ length: 28 }, (_, index) => index + 1), lastGrantedUtc: yesterday, lastClaimUtc: yesterday, cycleCompletedOn: yesterday });
  assert.equal(page.taken(), 28);
  assert.equal(page.elements.claimBtn.style.display, "none");
  page.intervals[0].fn();
  page.state({ taken: [], lastGrantedUtc: "", lastClaimUtc: "", cycleCompletedOn: "", nextDay: 1, claimedToday: false });
  assert.equal(page.taken(), 0);
  assert.equal(page.elements.claimBtn.style.display, "inline-block", "Authoritative rollover must restore the day-one claim control");
  assert.match(page.elements.claimStatus.innerHTML, /day 1/);
});

test("A failed grant preserves the request, leaves Taken untouched, and retains its error", () => {
  const page = browser();
  page.state();
  page.elements.claimBtn.onclick();
  const firstToken = JSON.parse(page.requests.find(item => item.method === "POST").body).claimToken;
  page.poll({ ok: false, message: "Free an inventory slot and retry." });
  page.state();
  assert.equal(page.taken(), 0);
  assert.match(page.elements.claimStatus.innerHTML, /Free an inventory slot/);
  assert.match(page.elements.claimBtn.innerHTML, /CLAIM REWARD/, "Claim must retain a text label without background artwork");
  assert.equal(page.elements.claimBtn.style.display, "inline-block", "A failed grant must retain a usable retry control");
  page.elements.claimBtn.onclick();
  const posts = page.requests.filter(item => item.method === "POST");
  assert.equal(JSON.parse(posts[1].body).claimToken, firstToken, "Retry must reuse the unresolved request");
});

test("Reopening resumes a saved token without creating another pending request", () => {
  const first = browser();
  first.state();
  first.elements.claimBtn.onclick();
  const reopened = browser(Object.fromEntries(first.stored));
  reopened.state();
  reopened.timeouts.find(timer => timer.delay === 1000).fn();
  reopened.poll({ ok: null, pending: true, armed: true });
  assert.equal(reopened.requests.filter(item => item.method === "POST").length, 0);
  assert.equal(reopened.requests.filter(item => item.url.startsWith("claim.php?token=")).length, 1);
  assert.equal(reopened.nativeCalls.length, 0, "Reopening must not invoke an unproven native JavaScript method");
  assert.match(reopened.elements.claimStatus.innerHTML, /Close the Daily Login window/);
  reopened.timeouts.filter(timer => timer.delay === 400).pop().fn();
  reopened.poll({ ok: null, pending: true, armed: true });
  assert.equal(reopened.nativeCalls.length, 0, "Result polling must not trigger native actions");
});

test("Reopening requeues an absent saved request with the original token before normal window closure", () => {
  const originalToken = "csaved123";
  const page = browser({ "aorebirth.daily.pending.v1.c101": originalToken });
  page.state();
  page.timeouts.find(timer => timer.delay === 1000).fn();
  page.poll({ ok: null, pending: true, armed: false });
  const posts = page.requests.filter(item => item.method === "POST");
  assert.equal(posts.length, 1);
  assert.equal(JSON.parse(posts[0].body).claimToken, originalToken);
  assert.equal(page.nativeCalls.length, 0);
  assert.match(page.elements.claimStatus.innerHTML, /Close the Daily Login window/);
  assert.equal(page.taken(), 0);
});

test("A legacy successful receipt resolves saved pending without replaying native or asserting a new grant", () => {
  const page = browser({ "aorebirth.daily.pending.v1.c101": "clegacy" });
  page.state();
  page.timeouts.find(timer => timer.delay === 1000).fn();
  page.poll({ ok: true, day: 1, taken: [1], lastGrantedUtc: today });
  assert.equal(page.nativeCalls.length, 0, "An old successful receipt must never re-trigger the native grant");
  assert.equal(page.taken(), 0, "The legacy receipt does not prove a new committed grant");
  assert.equal([...page.stored.keys()].filter(key => key.startsWith("aorebirth.daily.pending.")).length, 0);
  assert.match(page.elements.claimStatus.innerHTML, /prior claim/);
  page.state({ taken: [1], lastGrantedUtc: today, lastClaimUtc: today });
  assert.equal(page.taken(), 1, "Only the preserved authoritative board restores the old Taken state");
  const firstSlot = page.elements.grid.children[0].children[0].children[0];
  const labels = firstSlot.getElementsByTagName("span");
  assert.equal(labels[0].children[0].textContent, "Taken", "Taken text must exist independently of its missing image");
});

test("A character switch clears the old board and ignores its in-flight state response", () => {
  const page = browser();
  page.state({ taken: [1], lastGrantedUtc: today, lastClaimUtc: today });
  page.intervals[0].fn();
  const oldRequest = page.requests.filter(item => item.url.startsWith("claim.php?month=") && item.readyState !== 4).pop();
  page.native.CharacterID = 202;
  page.native.CharacterName = "Second";
  page.intervals[0].fn();
  assert.equal(page.taken(), 0);
  page.response(oldRequest, { ok: true, hasIdentity: true, accountKey: "first", taken: [1], lastGrantedUtc: today });
  assert.equal(page.taken(), 0);
  page.state({ accountKey: "second", taken: [], lastGrantedUtc: "", lastClaimUtc: "" });
  assert.equal(page.taken(), 0);
  assert.equal(page.elements.claimBtn.style.display, "inline-block");
});

test("A failed board verification disables claiming without discarding the last trusted board", () => {
  const page = browser();
  page.state({ taken: [1], lastGrantedUtc: "2020-01-01", lastClaimUtc: "2020-01-01" });
  page.intervals[0].fn();
  page.state({ ok: false, message: "Claim roots differ; originals preserved." });
  assert.equal(page.taken(), 1);
  assert.equal(page.elements.claimBtn.style.display, "none");
  assert.match(page.elements.claimStatus.innerHTML, /originals preserved/);
});

test("A native header binds the character before any saved token is loaded without a JavaScript identity API", () => {
  const page = browser({
    "aorebirth.daily.acct.v1.accountKey": "unrelated",
    "aorebirth.daily.pending.v1.pending": "anonymous-old-token",
    "aorebirth.daily.pending.v1.c606": "saved-606"
  }, null, true);
  assert.equal(page.requests.filter(item => item.url.startsWith("claim.php?token=")).length, 0);
  const initial = page.requests.find(item => item.url.startsWith("claim.php?month="));
  assert.match(initial.url, /&accountKey=&/);
  page.state({ nativeCharacterHint: true, characterId: 606, accountKey: "", taken: [] });
  assert.equal(page.elements.claimBtn.style.display, "inline-block");
  page.timeouts.find(timer => timer.delay === 1000).fn();
  const polling = page.requests.filter(item => item.url.startsWith("claim.php?token=")).pop();
  assert.match(polling.url, /token=saved-606&/);
  assert.equal(page.requests.filter(item => item.method === "POST").length, 0);
  assert.equal(page.nativeCalls.length, 0);
  assert.equal(page.stored.get("aorebirth.daily.pending.v1.pending"), "anonymous-old-token", "Anonymous history is retained but never resumed");
});

test("A changed native character repins storage and rejects the previous character's in-flight result", () => {
  const page = browser({ "aorebirth.daily.pending.v1.c606": "saved-606" }, null, true);
  page.state({ nativeCharacterHint: true, characterId: 606, accountKey: "first", taken: [] });
  page.timeouts.find(timer => timer.delay === 1000).fn();
  const oldPoll = page.requests.filter(item => item.url.startsWith("claim.php?token=")).pop();
  page.intervals[0].fn();
  const refresh = page.requests.filter(item => item.url.startsWith("claim.php?month=") && item.readyState !== 4).pop();
  assert.match(refresh.url, /&characterId=&/);
  assert.match(refresh.url, /&accountKey=&/);
  page.state({ nativeCharacterHint: true, characterId: 607, accountKey: "second", taken: [] });
  page.response(oldPoll, { ok: true, committed: true, accountKey: "first", taken: [1], lastGrantedUtc: today });
  assert.equal(page.taken(), 0);
  page.elements.claimBtn.onclick();
  const body = JSON.parse(page.requests.filter(item => item.method === "POST").pop().body);
  assert.equal(body.characterId, 607);
  assert.equal(body.accountKey, "second");
  assert.equal(page.stored.get("aorebirth.daily.pending.v1.c606"), "saved-606");
  assert.ok(page.stored.get("aorebirth.daily.pending.v1.c607"));
  assert.equal(page.nativeCalls.length, 0);
});

test("Both entry pages contain the same repaired claim logic", () => {
  assert.equal(indexSource, fs.readFileSync(path.join(path.dirname(indexPath), "index.html"), "utf8"));
});
