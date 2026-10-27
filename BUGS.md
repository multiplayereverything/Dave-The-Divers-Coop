# Bugs

Found by a subsystem-by-subsystem audit of the S1ii/Coop-Divers code (upstream commit `421bbac`, 2026-07-20) and by
the two-copy lab (`tests\lab-suite.ps1`) on Dave the Diver Steam build 26209260 (Unity 6000.0.52f1, BepInEx 6
be.790). Each fix says how it was checked: **lab** = real game clients on one PC, **self-test** = the plugin's
startup self-tests, **code** = re-read only.

## Fixed

| # | Area | Bug | Fix | Checked |
|---|---|---|---|---|
| 1 | Startup | On this game build, reading a game manager singleton on the boot scene (`DialogueManager.Instance` and others) makes the game try to create it and throw. The exception escaped `Update` before the new scene was recorded, so the "scene changed" block re-ran and threw every frame (2,163 times in one run) and nothing after it ran. | The mod reads Mono `Singleton<T>` managers only when `hasInstance` is true (53 call sites), records the scene first, and `Update` catches and logs (once per 30 s) instead of losing the frame. | lab |
| 2 | Dive | Party wipe soft-lock: when both divers died, the dive waited for an exit confirmation that a dead diver can never send. Both stayed in spectator mode. | Both dead = the dive ends; the host also re-checks the exit when the client's death arrives. Mutant `party-wipe` restores the old rule. | lab (see README test table) |
| 3 | Network | A reliable packet that arrived after falling out of the 1024-sequence receive window was acked, then dropped: the sender thought it was delivered (lost manifests or loot grants under heavy one-way loss). | Out-of-window reliables are no longer acked; the sender retries and, if it never fits, fails loudly (peer reset and recovery). | self-test |
| 4 | Network | A client that timed out locally never told the host it was leaving: the host kept a ghost peer for 5 s and ignored a quick rejoin. | `Dispose` sends `Disconnect` whenever a peer is known, not only while connected. | code |
| 5 | Network | A second client got no answer from a full room and showed "Connecting..." forever. | The host replies with a new `RoomFull` handshake reject; the client shows "Room is full". | code |
| 6 | Story | The full story snapshot (one reliable packet per cleared event and cutscene) was re-sent every 5 s, flooding the reliable lane, and the client briefly saw an empty story after each `ResetEvents`. | One safety keyframe 5 s after the first snapshot, then every 60 s; diffs still go every second. | lab (log volume) |
| 7 | Manager events | One scene-bound event queued before the host changed scene could never send. Every story, day and dialogue event behind it waited until the 256-event overflow reset the peer. Its epoch was also stamped at send time, so a stale event could apply on a later visit of the same scene. | Events carry the epoch they were published in. A host event for a scene the host has left is sent as it is, and the client counts it as applied and moves on. Mutant `manager-stale`. | code |
| 8 | NPC talk | Client NPC talk leases were cleared the frame after they were made: both sides store the client's scene epoch but compared it against opposite counters. Client NPC talks silently did nothing. | Compare with the client's epoch on both sides. | code |
| 9 | Bosses, pickups | `GetType().Name` on Il2CppInterop wrappers names the type that was asked for (`BossControllerBase`), not the real class, so boss families were never recognised, and pickup world ids could differ between host and client. | Use the IL2CPP runtime type (`Il2CppNames`) for boss families, controller checks, world object ids and allocator ids; `TryCast` instead of `is`. | code |
| 11 | Dive | Both players dove into different seas: each side picks the dive's sub-scenes (`B01_01_01` or `B02_01_01` and so on) from its own missions and layer cache in `SceneContext.BuildMapLayerData`. Different spawners meant the client could never bind the fish around the host. Seeding Unity's `Random` the same way was not enough (seen in the lab). | The host sends the scene ids it picked just before its dive transition; the client rebuilds its layout from them with the game's own id-list builder (`BuildMapLayerDataByCheat`). Mutant `layout`. | lab (`sea-layout`) |
| 12 | Dive | When the client was ready first, the host started the dive in the same frame it became ready; its "ready" state could lose the race against its scene change and the client stayed on the boat for good. | The host's dive transition counts as "host ready". Mutant `dive-race`. | lab (one failing run before the fix, passing after) |
| 13 | Dialogue | Shared dialogue deadlock: on this build the game's `StartDialogueV2` throws a native NullReferenceException for tutorial bundles on the client (Il2CppInterop swallows it inside the patched method). The client never showed the dialogue, never voted, and the host waited at 1/2 votes forever, so the story stopped. | A client that can't show a dialogue (spectator fallback, or the start didn't take) sends a one-off spectator vote; the host then advances that bundle on its own vote. Mutant `dialogue-spectator`. | lab (`dialogue`, `story`) |
| 14 | Dive | Party wipe before the host's own death sequence called `GoToLobby` still had no exit route. | When both divers are dead the host ends the dive through `InGameManager` itself, and re-checks on its own death too. | lab (`death`) |
| 15 | Saves | After any shared session the game has saved the campaign into manual slot 7, so the next session refused the host's save with "Manual save slot 7 changed outside DTMP; recovery required" and co-op could not start again (reproduced in the lab after a few runs). | When a valid DTMP marker shows DTMP wrote slot 7, the slot is reclaimed; its current contents are first copied to `BepInEx\config\DaveTheDiverMP.mp-slot.reclaimed-<time>`. Mutant `slot-reclaim`. | lab (`save`) |
| 16 | Network | A game that stalls for more than 5 s (loading, saving, a busy PC) lost the session: the peer timed out after 5 s and reliable packets expired after 15 s, and the client was sent back to the title (seen in the lab: an 18 s stall on the boat). | 20 s peer timeout, 30 s / 40 attempts for reliable delivery. Lab test `stall` freezes the client for 12 s; mutant `short-timeout`. | lab (`stall`) |
| 17 | Saves (online) | First two-account test: joining stalled on "Receiving host save" until the client timed out. Every unacked reliable packet was re-sent after 0.1 s with the whole save in flight, which clogged Steam's relay link so even keepalives stopped. | Retransmit timeout from the measured round trip, at most 32 save chunks in flight, larger Steam send buffer and rate. Mutant `fixed-retry`. | lab (save over the lag/loss proxy); ⚠️ two-account retest pending |
| 18 | Saves (online) | "Could not safely load host save" on a friend's PC: the client's byte-for-byte read-back check can't match a save the game re-encodes for another Steam account. | A valid read-back is accepted; a failure now names its step on screen ("Could not load host save (loading slot 7)"). | ⚠️ two-account retest pending |
| 19 | Saves | A slot 7 the game left half-written (closed or crashed mid-save; empty player data) blocked every later session. | A DTMP-owned incomplete slot is backed up and rewritten. | lab (both copies reclaimed and synced) |
| 20 | Saves | Black screen after a session: when the host went back to the title, the client's own profile could not be restored ("original profile JSON could not be parsed"; the in-memory JSON round trip fails on this game build), so the client kept playing the host's campaign alone. | Falls back to reloading the player's own saves from disk (they are never written during a session). | lab (restore fails the same way, disk reload ok) |
| 10 | Transport | Online play needed a reachable IP and an open UDP port. | Steam transport: friends-only lobby plus SteamNetworkingMessages (SteamNetworkingSockets P2P over Valve's relay), see README. LAN UDP stays as the fallback. | self-test, lab through SteamMock |

## Open

| Area | Bug | Why not fixed yet |
|---|---|---|
| Sushi bar | The host infers "sushi open" whenever a `SushiBarManager` exists (also during prep) and then skips its own native open (`ManagerEventReplicator` "Sushi open inferred"). | Needs a sushi bar night in the lab to check the native order of events. |
| Sushi bar | A stale remote plate (`_remoteSushiPlate`) can lock the client out of serving for the rest of the night after a failed serve. | Same: needs a sushi night in the lab. |
| Missions | Missions the host resets stay "in progress" on the client once a roster is known (`ShouldDeferAbsentMission`). | Needs a mission reset in the lab. |
| Time | Stacked time stops: the client applies a full `ResetTimeScale` for any host resume. | Native behaviour not confirmed. |
| Saves | Client save transfer can stall if `SaveSystem` is not ready when the last chunk arrives (no ack, no retry), and a chunk index is not range-checked. | Not reproduced. |
| Divers | Diver HP, oxygen, weapons and remote hitboxes are not synced: `DiverVitalReplicator`, `DiverWeaponAuthority`, `DiverWeaponReplicator` and `DiverRuntimeAuthority` are never created. Each player runs their own diver. | Dead code upstream; documented in the README, not claimed as a feature. |
| Bosses | Clients can't damage bosses (the host drops `BossDamageRequest`). | Needs a boss fight in the lab. |
| Puppet | A sprite id the client doesn't have triggers a full `Resources.FindObjectsOfTypeAll<Sprite>()` scan on every miss. | Performance only. |
| World state | The client rescans puzzle objects every frame while a host puzzle key has no match. | Performance only. |
| Pickups | Identical items from one spawner share a world id; duplicates can't be picked up by the client. | Needs a spawner with duplicates in the lab. |

## Refuted or not reproduced

| Claim | Result |
|---|---|
| Receive-window wraparound, header bounds, encoder size caps, build-id rejects, cross-thread state | Re-read: correct (only the queue and counters cross threads). |
