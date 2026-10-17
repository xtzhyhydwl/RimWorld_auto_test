# Programmatically subscribing to Steam Workshop items (RimWorld, appid 294100) — sourced research

Research date: 2026-10-04. Target: subscribe one specific account to ~141 public Workshop items, Windows, Steam client installed.

**Sandbox limitation up front:** this machine/session has **no direct network egress** (every outbound HTTPS request through proxy `127.0.0.1:7897` fails), so I could not live-probe any HTTP endpoint, and I had no Steam Web API key to test with. Everything below is from (a) official docs (live mirrors / Wayback), (b) Valve's protocol definitions, (c) third-party tool source code, and (d) static inspection of the Steam client installed on this machine. Uncertainty is marked explicitly.

## 0. Local facts verified on this machine (primary evidence)

| Fact | Value / evidence |
|---|---|
| Steam install | `C:\Program Files (x86)\Steam` (`HKCU\Software\Valve\Steam\SteamPath = c:/program files (x86)/steam`) |
| Steam running | `steamwebhelper` processes present; **CEF debug port 8080 closed** (so Steam is *not* in `-dev` mode) |
| RimWorld | installed: `D:\SteamLibrary\steamapps\common\RimWorld` |
| Workshop content | `D:\SteamLibrary\steamapps\workshop\content\294100\` exists, **575** item folders |
| Workshop ACF | `D:\SteamLibrary\steamapps\workshop\appworkshop_294100.acf` (196 KB). Top-level keys: `appid, SizeOnDisk, NeedsUpdate, NeedsDownload, TimeLastUpdated, TimeLastFullCheck, TimeLastAppRan, LastBuildID, WorkshopItemsInstalled, WorkshopItemDetails`. Each `WorkshopItemDetails` entry: `manifest, timeupdated, timetouched, subscribedby, latest_timeupdated, latest_manifest`. **There is no "subscribed list" here** — the ACF is a download/install ledger; `subscribedby` is an accountID (e.g. `1018749175`). |
| Steam client bundle | `C:\Program Files (x86)\Steam\steamui\chunk~2dcc5aaf7.js` contains: `SteamClient.Apps.SubscribeWorkshopItem(appid, publishedfileid, bSubscribe)`; RPC wrappers `PublishedFile.Subscribe#1` `{ePrivilege:1, eWebAPIKeyRequirement:2}`, `PublishedFile.Unsubscribe#1` (same), `PublishedFile.Vote#1` `{…eWebAPIKeyRequirement:5}`, `PublishedFile.SetSubscriptionListFromCollection#1` `{ePrivilege:1}`; page names `CommunityFilePage`, `SteamWorkshopPage`, `SteamWorkshop`, `SteamWorkshopSubscriptions`. |
| No URL subscribe command | grep over all **345** `steamui` JS/CSS files: no `steam://subscribe`, no `subscribe/` pipe command, no `SubscribeWorkshopItem` URL form. |
| steamcmd | **not installed** (`C:\steamcmd\steamcmd.exe` etc. absent, not in PATH) |

---

## 1. Steam Web API `ISteamRemoteStorage/SubscribePublishedFile` / `UnsubscribePublishedFile` / `EnumerateUserSubscribedFiles`

**Verdict: endpoint still exists, but it is PUBLISHER-KEY ONLY. A personal (non-publisher) API key does not work. Not usable for this task.**

Concrete details (current official docs as mirrored by the auto-generated Steam Web API documentation site, which is regenerated from Valve's `GetSupportedAPIList` using **both** a normal key and a partner key):

- `POST https://partner.steam-api.com/ISteamRemoteStorage/SubscribePublishedFile/v1/` — params `key` (required), `steamid`, `appid`, `publishedfileid` — tagged **[PUBLISHER]**
- `POST https://partner.steam-api.com/ISteamRemoteStorage/UnsubscribePublishedFile/v1/` — same params — **[PUBLISHER]**
- `POST https://partner.steam-api.com/ISteamRemoteStorage/EnumerateUserSubscribedFiles/v1/` — params `key` (required), `steamid`, `appid`, `listtype` — **[PUBLISHER]**
- Source: <https://steamapi.xpaw.me/ISteamRemoteStorage> (mirror of `https://partner.steamgames.com/doc/webapi/ISteamRemoteStorage`)

The archived **official** Valve docs state it explicitly (2018 snapshot; wording has been stable for years):

> EnumerateUserSubscribedFiles / EnumerateUserPublishedFiles / SetUGCUsedByGC / SubscribePublishedFile / UnsubscribePublishedFile — `key`: "Steamworks Web API **publisher** authentication key."
> "**NOTE:** This call requires a publisher API key to use this method. As such this API **MUST** be called from a secure server, and **can never be used directly by clients**!"
> — <https://web.archive.org/web/20181126081346/https://partner.steamgames.com/doc/webapi/ISteamRemoteStorage>

Answers to the sub-questions:

- **Auth required:** publisher key (`key=` from the *app's* Steamworks partner backend), plus an explicit `steamid` you are acting on. Because `steamid` is a free input, Valve scoped it to app publishers only. A publisher key for **appid 294100 belongs to Ludeon Studios** — not obtainable.
- **Does a personal key let the owner subscribe their own account?** No per official docs. The mirror generator's README is the structural confirmation: the "official list from `GetSupportedAPIList`" is scraped **twice — "Using normal API key" and "Using partner API key"** (<https://raw.githubusercontent.com/xpaw/SteamWebAPIDocumentation/master/README.md>), i.e. publisher-only methods are not part of a personal key's API surface. **Honest gap:** I could not read the body/answers of the classic report <https://github.com/ValvePython/steamctl/issues/19> ("Subscribe to Workshop Item") because GitHub HTML truncates in my fetcher and the GitHub API was rate-limited (HTTP 403); so I have no first-hand 2015–2018 user report quoted here. I also found no 2019+ tutorial or tool that makes these calls with a personal key.
- **Still functional in 2025/2026?** It is still *documented* (the mirror list is current — it includes 2025-era interfaces), so not removed. Whether a publisher call still succeeds today I could not test.
- **Related unified-service route (unverified):** `IPublishedFileService/Subscribe/v1` and `/Unsubscribe/v1` exist on `https://api.steampowered.com/` and take `key` (<https://steamapi.xpaw.me/IPublishedFileService>, where `CanSubscribe` is flagged "UNDOCUMENTED"). **I could NOT confirm whether a personal key is accepted there** — no test possible. If one wanted to keep looking, this is the only plausible remaining official route.
- **`EnumerateUserSubscribedFiles` does NOT prove a personal key identifies the user:** it takes an explicit `steamid` and requires a publisher key, so it says nothing about key-owner identity. (A normal user can see their own subscriptions on the community site / in the client's "Workshop Subscriptions" page — session-based, not key-based.)

---

## 2. `steam://` URL commands

**Verdict: there is NO `steam://` command that subscribes to a Workshop item. Every Workshop-related command only opens a page.**

Valve's own list ("Steam browser protocol", page last edited 2024-07-14, archived 2024-12-30 — the wiki itself warns "Some commands are no longer functional"):
<https://web.archive.org/web/20241230182028/https://developer.valvesoftware.com/wiki/Steam_browser_protocol>

Workshop/related entries on that list, with behaviour:

| Command | Subscribes? |
|---|---|
| `steam://url/CommunityFilePage/<id>` | **No** — "Opens a Workshop/Greenlight submission" (opens the item page in the Steam client's browser) |
| `steam://url/SteamWorkshop` / `steam://url/SteamWorkshopPage/<id>` | **No** — opens Workshop pages (also present in the local client's page-name table, incl. `SteamWorkshopSubscriptions`) |
| `steam://url/SteamWorkshopPage/<appid>` | No — opens that app's Workshop |
| `steam://subscriptioninstall/<id1>/<id2>/...` | **No** — despite the name, this is a *game purchase/install checklist* dialog for owned subscriptions, not Workshop |
| `steam://installaddon/<addon>` / `steam://removeaddon/<addon>` | No — Source-engine add-ons (HL2 HD pack etc.), not Workshop items |
| `steam://install/<id>`, `steam://open/…`, `steam://nav/…` | No |

No `steam://subscribe/...`, no `steam://url/SubscribeWorkshopItem/...`, no `steam://workshop/subscribe/...` appears on the list. **Independently confirmed locally:** grepping the installed Steam client's 345 UI JS/CSS files finds no `steam://subscribe` and no `subscribe/` pipe command; the only subscribe entry points in the client are the JS API `SteamClient.Apps.SubscribeWorkshopItem(...)` and the `PublishedFile` RPCs. `steam://open/bigpicture/workshopitem/<id>` merely navigates the client browser to the item page.

**Collection in one `steam://` action:** no such command exists. (The client's collection button is a UI action → `PublishedFile.SetSubscriptionListFromCollection#1`; see §5.)

---

## 3. steamcommunity.com web endpoint (logged-in browser session)

**Verdict: YES — a plain HTTP POST from a logged-in browser session subscribes. Per-item only; ~141 items = ~141 POSTs. No credentials beyond the browser session are needed.**

Concrete request (corroborated by three independent userscripts and cross-checked against Valve's protocol field names):

```
POST https://steamcommunity.com/sharedfiles/subscribe
Content-Type: application/x-www-form-urlencoded; charset=UTF-8
X-Requested-With: XMLHttpRequest
Referer: https://steamcommunity.com/sharedfiles/filedetails/?id=<fileid>
Cookie: (logged-in steamcommunity session, incl. sessionid + steamLoginSecure)

id=<publishedfileid>&appid=294100&include_dependencies=false&sessionid=<g_sessionID>
```

Unsubscribe: `POST https://steamcommunity.com/sharedfiles/unsubscribe` with `id=<publishedfileid>&sessionid=<g_sessionID>&appid=<appid>`.

Required fields/cookies: `sessionid` (must equal the `sessionid` cookie; `window.g_sessionID` holds it on the page), the login cookies (`steamLoginSecure`), `id`. `appid` is sent by real scripts; one script hardcodes a wrong appid (`4000`) and reportedly still works, so the server may tolerate/ignore it — **use the real appid 294100**.

Evidence:

- <https://greasyfork.org/en/scripts/592041> ("Steam 创意工坊手动订阅工具", created **2026-08-19**) — reads `sessionid` from `document.cookie`, extracts appid, then does exactly the fetch above with `include_dependencies=false`. (Its own description says it was generated with Gemini — treat as an implementation sketch, not a spec.)
- <https://greasyfork.org/en/scripts/562971> ("Steam Workshop – Ultimate Bulk Sub/Unsub Tool", v4.1) — loops `POST https://steamcommunity.com/sharedfiles/subscribe|unsubscribe` with body `id=…&sessionid=${window.g_sessionID}&appid=…`, **250 ms delay "to respect Steam rate limits and prevent 'excessive traffic'"**, with a progress bar. Independent author, same endpoint.
- <https://greasyfork.org/en/scripts/493550> ("steam-workshop-subscribe-all", by SavageCore) — re-adds Steam's "missing" *Subscribe to all* button by calling the page's **own** functions `SubscribeCollection('<collectionId>','<appId>')` / `UnsubscribeCollection('<collectionId>')`, i.e. Steam's official collection flow is implemented in page JS on top of these per-item requests (client-side loop), not a single batch endpoint.
- Cross-check against Valve's protocol: `CPublishedFile_Subscribe_Request` has fields `publishedfileid, list_type, appid, notify_client, include_dependencies` — the web form's `include_dependencies` matches exactly, which strongly suggests `/sharedfiles/subscribe` maps to `PublishedFile.Subscribe#1`
  (<https://raw.githubusercontent.com/SteamDatabase/Protobufs/master/steam/steammessages_publishedfile.steamclient.proto>).
- `notify_client` in that proto is why a *running* Steam client starts downloading immediately; if the client is closed the subscription is still recorded server-side (it syncs at next client login) — this is inference from the protocol + the existence of `CPublishedFile_FileSubscribed_Notification`, not a quote.

Does the item page show a Subscribe button without the Steam client? **Yes.** Fetching <https://steamcommunity.com/sharedfiles/filedetails/?id=725130005> (P-Music, RimWorld) as an anonymous HTTP client — no Steam client involved — returns the markup `# Subscribe to download … Subscribe / Subscribed / Unsubscribe`. So the *registration* of the subscription is a normal web action; the Steam client is only needed to *download* the content. (While logged out, clicking it leads to login.)

Bulk/batch in one request? **Not found.** All evidence points to N sequential POSTs. Steam's batched RPC `PublishedFile.SetSubscriptionListFromCollection#1` is **not** exposed on the public web API host (it is absent from the generated `IPublishedFileService` method list), so it is client-only.

Rate limiting: many community posts report "**You have made too many requests recently. Please wait and try again later.**" when scripting the community site (e.g. <https://steamcommunity.com/discussions/forum/29/695372460980285973>) — hence delays/retries are mandatory for 141 items.

---

## 4. steamcmd

**Verdict: `workshop_download_item 294100 <fileid>` works with a normal (non-anonymous) login for a game the account owns — it downloads content into steamcmd's own ACF-tracked folder. It does NOT subscribe the account, and it cannot take a collection id.**

- Command form (widely used in server guides): `workshop_download_item <appid> <publishedfileid>`; files land in `<steamcmd>/steamapps/workshop/content/<appid>/<publishedfileid>/` with state in `<steamcmd>/steamapps/workshop/appworkshop_<appid>.acf` — the same layout as the client (verified locally: `…\workshop\content\294100\*` + `appworkshop_294100.acf`).
- **Subscribed or merely downloaded?** Only downloaded. The ACF is an install ledger (`WorkshopItemsInstalled` + `WorkshopItemDetails` with a `subscribedby` accountID; the local 196 KB `appworkshop_294100.acf` has no "subscription list" section). The account's subscription list is a **server-side** list (proto: server-side `Subscribe` with `notify_client`, plus `FileSubscribed` notifications). Valve's `ISteamUGC` docs even note that if the user is not subscribed to an item (e.g. an anonymously logged-in game server) the Workshop item is downloaded **into a temporary cache** — i.e. download ≠ subscribe (<https://partner.steamgames.com/doc/api/ISteamUGC#DownloadItemResult_t>; I could only read this in a localized snippet, English page truncated for me). **I could not find any source claiming steamcmd marks an account-side subscription, and I could not run steamcmd (not installed here) to verify empirically.**
- **Collections:** not supported. Arma 3 SteamCMD thread — asked Jan 2020, answered Jun 2022 "It is sadly not" (<https://steamcommunity.com/app/107410/discussions/16/1736635023060733340/>), and the same limitation is the subject of <https://github.com/ValveSoftware/steam-for-linux/issues/11662> (title only — GitHub body unreachable for me).
- **2FA/Steam Guard friction:** steamcmd supports non-anonymous login; Steam Guard is required the **first** login (and after the cached ssfn files are deleted). Scriptable forms, per steamcmd's own `help` and Stack Overflow: `set_steam_guard_code <code>` before `login`, or `login <username> <password> <Steam guard code>`. Because mobile-authenticator 2FA codes rotate, community tooling exists for automation (`dyc3/steamguard-cli`, `Weilbyte/steamcmd-2fa`). Also: "A user can only be logged in once at any time (counting both graphical client as well as SteamCMD logins)" (<https://web.archive.org/web/20250102230901/https://developer.valvesoftware.com/wiki/SteamCMD>).
- Practical use: if the real acceptance criterion is "the mods are usable by RimWorld on this machine", steamcmd can fetch the 141 items' content, which can then be placed under the client's `steamapps/workshop/content/294100/` (RimWorld reads mod folders there); the account still shows them as *not subscribed*, so no auto-updates and no "Subscribed" flag in-game/on the site.

---

## 5. Steam client's own bulk path ("Subscribe to all")

- What happens in the current client: the collection page's **Subscribe to all** opens `SubscribeCollectionDialog`, whose confirm handler calls `SetSubscriptionsFromCollection(appid, collection, addOnly)` → RPC **`PublishedFile.SetSubscriptionListFromCollection#1`** (`appid, list_type, publishedfileid, add_only`) — one server-side call that adds the whole collection (children included) to the account's subscription list. Verified directly in the local 2026 client bundle `steamui\chunk~2dcc5aaf7.js`.
- In the *web* page the equivalent button loops per item (see §3), per the userscript that re-implements/restores it.
- **Local file or command-line trigger: none supported.** No CLI flag, no documented file. Editing `appworkshop_294100.acf` does not create subscriptions (it is a download ledger, §0/§4). The only programmatic triggers are (a) evaluating the client's own JS/RPC path, or (b) the community web POST per item.

---

## 6. Credentials / security facts worth telling the user

| Approach | Credential needed | Sensitivity |
|---|---|---|
| Personal Steam Web API key (`steamcommunity.com/dev/apikey`) | key string | Tied to your account; **does not** grant login/password-equivalent rights (no purchases, no password change), but it is a bearer secret that identifies your account and can read your public-ish data. Valve added granular per-method permissions for keys in **Jan 2026** ("NEW: Better permissions and controls for Web API Keys"). Crucially: **it does not unlock publisher-only methods**, so it cannot subscribe. |
| Publisher API key | partner key for the app | Needed for `ISteamRemoteStorage/Subscribe*`; only app publishers have one; docs say calls must come from a secure server. Not available for RimWorld (294100). |
| steamcommunity.com session cookie (`steamLoginSecure`) + `sessionid` | session token | This is the strongest of the web credentials: whoever holds it can act as you on the community site for its lifetime. With this approach the cookie need never leave the user's own browser if the loop is executed *inside* the logged-in page (DevTools console / Tampermonkey), so the user shares **nothing** with the agent. |
| Steam password + Steam Guard code (steamcmd) | password (highest value) + one-time 2FA code | Only needed if steamcmd is used. 2FA codes are single-use/short-lived; the password is the crown jewel. |
| **No credentials at all** | — | The local Steam client's cached login session: a helper program using the Steamworks SDK (`ISteamUGC::SubscribeItem`) with `steam_appid.txt=294100` while the client is running, **or** evaluating `SteamClient.Apps.SubscribeWorkshopItem(294100, <id>, true)` in the client's own DevTools (<http://127.0.0.1:8080>, requires restarting Steam with `-dev`; documented for Steam client modding at <https://docs.steambrew.app/developers/devtools>). Uses only on-disk state. **Caveat:** the DevTools-JS-injection route is verified only as "the function exists in the client bundle and dev mode exposes DevTools"; I did not execute it. Currently port 8080 is closed here, so Steam is not in dev mode. |

Things I could NOT confirm (state honestly to the user):

1. Live behaviour of `steamcommunity.com/sharedfiles/subscribe` in 2026 — no network here, and two of the three corroborating scripts are AI-authored (the endpoint string + params are consistent across independent scripts and match Valve's proto field names, which is why I rate it high-confidence but not verified).
2. Whether `ISteamRemoteStorage/SubscribePublishedFile` returns an error for a personal key (only the official "publisher key required / can never be used directly by clients" documentation, no first-hand 2015–2018 report quoted).
3. Whether `IPublishedFileService/Subscribe/v1` accepts a personal key.
4. Whether steamcmd's download changes the account-side subscription list (reasoned as "no" from the ACF ledger layout + `ISteamUGC` caching note, not executed).
5. Whether `SteamClient.Apps.SubscribeWorkshopItem` is callable from a DevTools-evaluated expression in the `SharedJSContext` target.

---

## 推荐方案 / Recommended (ranked by "does it actually work" then "credential exposure")

1. **Browser-session loop on steamcommunity.com — best effective option, minimal exposure.**
   Have the user log into steamcommunity.com in their own browser (or use the already-logged-in client-session browser), then run the ~141-item loop **inside that page** (DevTools console snippet or a Tampermonkey script) posting `id=<fileid>&appid=294100&include_dependencies=false&sessionid=${g_sessionID}` to `https://steamcommunity.com/sharedfiles/subscribe`, with 200–500 ms spacing, retry/back-off on "too many requests", and verification by re-reading each item page's `Subscribed` state (or the account's subscriptions page). Nothing is handed to the agent — the session never leaves the browser. This uses exactly the code path of Steam's own Subscribe button. **Unverified-in-2026 caveat applies.**
2. **Local Steam client, zero credentials (fallback / parallel).**
   (a) A tiny helper using the Steamworks SDK: init with `steam_appid.txt=294100` (RimWorld is owned/installed on this machine) and call `SteamUGC()->SubscribeItem(fileid)` for each id while Steam runs — confirmed to exist as `ISteamUGC::SubscribeItem` / `subscribe_item` (<https://docs.rs/steamworks/latest/steamworks/struct.UGC.html>); or
   (b) restart Steam with `-dev`, open <http://127.0.0.1:8080>, and evaluate `SteamClient.Apps.SubscribeWorkshopItem(294100, <id>, true)` per id in the client's JS context.
   No password, no API key, no cookie export. Downsides: (a) needs a compiled helper + SDK; (b) is unsupported by Valve (local JS injection into the client) and unverified here.
3. **steamcmd `workshop_download_item` — only if "mods usable locally" is enough.**
   Fully scriptable with password + first-time Steam Guard code; content lands in steamcmd's `steamapps/workshop/content/294100/<id>/` and can be copied into the client's library so RimWorld loads the mods. It does **not** create subscriptions, cannot take a collection id, and needs the password (highest-value secret) — so it loses on both criteria unless subscription state is irrelevant.
4. **Web API `ISteamRemoteStorage/SubscribePublishedFile` — rejected.** Publisher key only (official docs, "can never be used directly by clients"); a personal key does not work; the app's publisher key (Ludeon) is not available.
5. **`steam://` URL — rejected.** No subscribe command exists; `steam://url/CommunityFilePage/<id>` only opens the item page, and there is no collection-subscribe URL.

## Source list

- Current (mirrored) Web API docs: <https://steamapi.xpaw.me/ISteamRemoteStorage> · <https://steamapi.xpaw.me/IPublishedFileService> · generator notes: <https://raw.githubusercontent.com/xpaw/SteamWebAPIDocumentation/master/README.md>
- Official (archived) Web API docs with the publisher-key note: <https://web.archive.org/web/20181126081346/https://partner.steamgames.com/doc/webapi/ISteamRemoteStorage>
- Valve protocol (subscribe/unsubscribe/SetSubscriptionListFromCollection, notify_client): <https://raw.githubusercontent.com/SteamDatabase/Protobufs/master/steam/steammessages_publishedfile.steamclient.proto>
- `steam://` command list: <https://web.archive.org/web/20241230182028/https://developer.valvesoftware.com/wiki/Steam_browser_protocol>
- Community web endpoint evidence: <https://greasyfork.org/en/scripts/592041> · <https://greasyfork.org/en/scripts/562971> · <https://greasyfork.org/en/scripts/493550>
- Workshop item page markup (Subscribe button, anonymous fetch): <https://steamcommunity.com/sharedfiles/filedetails/?id=725130005>
- steamcmd: <https://web.archive.org/web/20250102230901/https://developer.valvesoftware.com/wiki/SteamCMD> · Arma 3 thread <https://steamcommunity.com/app/107410/discussions/16/1736635023060733340/> · <https://github.com/ValveSoftware/steam-for-linux/issues/11662> (title) · Steam Guard automation: <https://stackoverflow.com/revisions/ecfb174b-de79-491c-b6ec-0fe7d649eb36/view-source>
- `ISteamUGC::SubscribeItem` binding: <https://docs.rs/steamworks/latest/steamworks/struct.UGC.html> · Steam client DevTools: <https://docs.steambrew.app/developers/devtools>
- Web API key permissions change: <https://steamcommunity.com/groups/steamworks/announcements/detail/493839547938902179> (announcement; summary at <https://gamedev.net/news/1154-new-better-permissions-and-controls-for-web-api-keys/>)
- Local static evidence: `C:\Program Files (x86)\Steam\steamui\chunk~2dcc5aaf7.js`, `D:\SteamLibrary\steamapps\workshop\appworkshop_294100.acf`
