using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using DR.AI;

namespace DaveTheDiverMP;

// Scripted steps that run inside lab copies after the shared campaign starts (-dtmp-tour <name>). Each copy only
// does what a player could do on its own machine and logs what it sees as "LAB <name> FACT key=value ...";
// tests\lab-suite.ps1 compares the host's facts with the client's (a copy never reads the other's state).
internal static class LabTour
{
    private sealed class Step
    {
        public string Name;
        public float Timeout;
        public Func<float, bool> Run; // called each frame with seconds since the step began; true = done
    }

    private static readonly List<Step> Steps = new();
    private static int _index = -1;
    private static float _stepStart;
    private static bool _built;
    private static readonly Dictionary<string, float> NextAt = new();

    internal static void Tick(ProbeBehaviour probe)
    {
        if (string.IsNullOrEmpty(LabMode.Tour))
            return;
        if (!_built)
        {
            _built = true;
            Build(probe);
            LabMode.Log($"tour {LabMode.Tour} steps={Steps.Count}");
        }
        var now = Time.realtimeSinceStartup;
        if (_index < 0)
        {
            _index = 0;
            _stepStart = now;
            LabMode.Log($"step {Steps[0].Name}");
        }
        if (_index >= Steps.Count)
            return;
        var step = Steps[_index];
        bool done;
        try
        {
            done = step.Run(now - _stepStart);
        }
        catch (Exception exception)
        {
            LabMode.Log($"STEP-ERROR {step.Name} {exception.GetType().Name}: {exception.Message}");
            done = false;
        }
        if (!done && now - _stepStart < step.Timeout)
            return;
        LabMode.Log(done ? $"STEP-OK {step.Name}" : $"STEP-TIMEOUT {step.Name}");
        _index = step.Name == LabMode.StopAfter ? Steps.Count : _index + 1;
        _stepStart = now;
        NextAt.Clear();
        if (_index < Steps.Count)
            LabMode.Log($"step {Steps[_index].Name}");
        else
            LabMode.Log("tour-done");
    }

    private static void Add(string name, float timeout, Func<float, bool> run) =>
        Steps.Add(new Step { Name = name, Timeout = timeout, Run = run });

    internal static void Fact(string text) => LabMode.Log($"FACT {text}");

    // True at most once per interval for a key (per step).
    private static bool Every(string key, float seconds)
    {
        var now = Time.realtimeSinceStartup;
        if (NextAt.TryGetValue(key, out var next) && now < next)
            return false;
        NextAt[key] = now + seconds;
        return true;
    }

    private static bool IsHost => ProbeBehaviour.Role == SessionRole.Host;

    private static bool IsAlive(List<KeyValuePair<int, FishAISystem>> fish, int id)
    {
        var pair = fish.Find(entry => entry.Key == id);
        return pair.Value != null && !pair.Value.IsDead() && pair.Value.HP > 0 && !pair.Value.IsCorpse;
    }

    private static Transform LocalPlayer()
    {
        var player = UnityEngine.Object.FindFirstObjectByType<PlayerCharacter>();
        if (player != null)
            return player.transform;
        var lobby = UnityEngine.Object.FindFirstObjectByType<LobbyPlayer>();
        if (lobby != null)
            return lobby.transform;
        var sushi = UnityEngine.Object.FindFirstObjectByType<SushiBarPlayerHanlder>();
        return sushi != null ? sushi.transform : null;
    }

    private static Transform Puppet()
    {
        var puppet = GameObject.Find("DTMP Remote Diver");
        return puppet != null && puppet.activeInHierarchy ? puppet.transform : null;
    }

    private static string Scene => SceneManager.GetActiveScene().name;
    private static string Pos(Vector3 p) => $"x={p.x:F2} y={p.y:F2}";

    private static void Build(ProbeBehaviour probe)
    {
        var session = (Func<UdpSession>)(() => probe._session);

        Add("boat", 240f, t =>
        {
            if (session()?.Connected != true || Scene != "DR_Lobby" || LocalPlayer() == null)
                return false;
            Fact($"boat scene={Scene} save={MultiplayerSaveSync.Status}");
            return true;
        });

        // Player puppets: the host steps 2 m to the side; the client reports where it draws the host's puppet.
        var moved = false;
        var hostTarget = Vector3.zero;
        Add("puppet", 40f, t =>
        {
            var puppet = Puppet();
            if (puppet != null && Every("puppet", 0.5f))
                Fact($"puppet {Pos(puppet.position)} t={t:F1}");
            if (IsHost && !moved && t > 6f)
            {
                var me = LocalPlayer();
                if (me != null)
                {
                    hostTarget = me.position + new Vector3(2f, 0f, 0f);
                    me.position = hostTarget;
                    moved = true;
                    Fact($"host-moved {Pos(hostTarget)}");
                }
            }
            if (IsHost && moved && Every("hold", 0.2f))
            {
                var me = LocalPlayer();
                if (me != null && (me.position - hostTarget).sqrMagnitude > 0.01f)
                    me.position = hostTarget;
            }
            return t > 20f;
        });

        // Diving: both players open the boat's dive panel and hold Start, as on the boat's dive point.
        LobbyStartGamePanelUI panel = null;
        Add("dive", 120f, t =>
        {
            var scene = Scene;
            if (scene != "DR_Lobby" && scene != "Empty" && UnityEngine.Object.FindFirstObjectByType<PlayerCharacter>() != null)
            {
                Fact($"dive-scene {scene}");
                return true;
            }
            if (scene != "DR_Lobby")
                return false;
            if (panel == null && Every("trigger", 2f))
            {
                var trigger = UnityEngine.Object.FindFirstObjectByType<DiveTrigger>();
                if (trigger == null)
                {
                    Fact("dive-trigger missing");
                    return false;
                }
                trigger.OnPlayerEnter(true);
                panel = trigger.m_Panel;
                Fact($"dive-panel {(panel != null ? "found" : "missing")}");
            }
            if (panel != null && t > (IsHost ? 8f : 4f))
            {
                if (Every("press", 3f))
                    panel.StartPressGameStart();
                panel.HoldStartGame();
            }
            return false;
        });

        // Leaving the dive: both divers confirm the surface exit (2/2 consent), then everyone is back on the boat.
        var exitPressed = false;
        Add("dive-exit", 120f, t =>
        {
            if (Scene == "DR_Lobby" && LocalPlayer() != null)
            {
                Fact($"exit-boat scene={Scene} t={t:F0}");
                return true;
            }
            if (!exitPressed && t > (IsHost ? 8f : 4f))
            {
                // Surfacing at the boat ends a dive through InGameManager.GoToLobby (the tutorial has no exit trigger).
                var manager = InGameManager.hasInstance ? InGameManager.Instance : null;
                Fact($"exit-request manager={(manager != null ? "found" : "missing")}");
                if (manager != null)
                {
                    exitPressed = true;
                    manager.GoToLobby(default, false);
                }
            }
            return false;
        });

        // Story: shared dialogue only advances when both players vote. Both press Skip whenever a dialogue is
        // playing, until none has played for 8 s.
        var quietSince = -1f;
        var skips = 0;
        Add("story", 180f, t =>
        {
            var manager = DialogueManager.hasInstance ? DialogueManager.Instance : null;
            if (manager != null && manager.IsPlaying)
            {
                quietSince = -1f;
                if (Every("skip", 1.5f))
                {
                    manager.OnSkip();
                    skips++;
                    if (skips % 5 == 1)
                        Fact($"dialogue-skip bundle={manager.CurrentBundleID} n={skips}");
                }
                return false;
            }
            if (quietSince < 0f)
                quietSince = t;
            if (t - quietSince < 8f)
                return false;
            Fact($"dialogue-quiet skips={skips} scene={Scene}");
            return true;
        });

        // Second dive into the normal Blue Hole shallows (A01_01_01): the host picks the area, the client holds Start.
        LobbyStartGamePanelUI panel2 = null;
        Add("dive2", 120f, t =>
        {
            var scene = Scene;
            if (scene != "DR_Lobby" && scene != "Empty" && !scene.StartsWith("DR_") &&
                UnityEngine.Object.FindFirstObjectByType<PlayerCharacter>() != null)
            {
                Fact($"dive2-scene {scene}");
                return true;
            }
            if (scene != "DR_Lobby")
                return false;
            if (panel2 == null && t > 4f && Every("trigger", 2f))
            {
                var trigger = UnityEngine.Object.FindFirstObjectByType<DiveTrigger>();
                if (trigger != null)
                {
                    trigger.OnPlayerEnter(true);
                    panel2 = trigger.m_Panel;
                }
            }
            if (panel2 == null || t < (IsHost ? 10f : 6f))
                return false;
            // Same as the first dive: hold Start on the dive panel; the game picks the area from the story.
            if (Every("press", 3f))
                panel2.StartPressGameStart();
            panel2.HoldStartGame();
            return false;
        });

        Add("dive-world", 60f, t =>
        {
            if (!Every("world", 2f))
                return false;
            var me = LocalPlayer();
            var puppet = Puppet();
            Fact($"dive-world scene={Scene} me={(me != null ? Pos(me.position) : "none")} puppet={(puppet != null ? Pos(puppet.position) : "none")} t={t:F0}");
            return t > 12f;
        });

        // Fish: both sides list the fish they track by shared id; the host kills its lowest id and the client hits
        // its highest id (a client hit is only a request: the host decides). Both then report which ids are dead.
        var hostKill = -1;
        var clientKill = -1;
        var swam = false;
        var hostTried = new HashSet<int>();
        var clientTried = new HashSet<int>();
        var hostTries = 0;
        var clientTries = 0;
        var hostAt = 0f;
        var clientAt = 0f;
        Add("fish", 90f, t =>
        {
            // Both divers swim to the same fish spawner (lowest allocator id), as players meeting at a reef would.
            var fish = new List<KeyValuePair<int, FishAISystem>>(probe.LabFishReplicator?.LabFish(ProbeBehaviour.Role) ?? Array.Empty<KeyValuePair<int, FishAISystem>>());
            fish.Sort((a, b) => a.Key.CompareTo(b.Key));
            // Targets: fish a harpoon can hurt (some species are immune), the same choice on both sides.
            var damageable = fish.FindAll(pair => pair.Value != null && !pair.Value.IsIgnoreDamageState && !pair.Value.IsCanNotDeadByStopEvent());
            if (!swam)
            {
                var allocators = new List<FishAllocator>(UnityEngine.Object.FindObjectsByType<FishAllocator>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
                allocators.Sort((a, b) => string.CompareOrdinal(a.GetAllocatorUID(), b.GetAllocatorUID()));
                var me = LocalPlayer();
                Fact($"fish-spawners count={allocators.Count} fishObjects={UnityEngine.Object.FindObjectsByType<FishAISystem>(FindObjectsSortMode.None).Length}");
                var dump = new List<string>();
                foreach (var allocator in UnityEngine.Object.FindObjectsByType<FishAllocator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    dump.Add($"{allocator.GetAllocatorUID()}	{allocator.gameObject.activeInHierarchy}	{allocator.gameObject.scene.name}");
                dump.Sort(StringComparer.Ordinal);
                var layers = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var allocator in UnityEngine.Object.FindObjectsByType<FishAllocator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    layers.Add(allocator.gameObject.scene.name);
                Fact($"sea-layout scenes={string.Join(",", layers)}");
                System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "lab-allocators.txt"), dump);
                // Both swim to the fish with the lowest shared id (the client places it from the host's manifest).
                if (me != null && damageable.Count > 0)
                {
                    me.position = damageable[0].Value.transform.position + new Vector3(1.5f, 0f, 0f);
                    Fact($"swim-to {Pos(me.position)} fish={damageable[0].Key}");
                    swam = true;
                }
                else if (t > 10f)
                    swam = true;
            }
            if (Every("objects", 5f))
                Fact($"fish-objects count={UnityEngine.Object.FindObjectsByType<FishAISystem>(FindObjectsSortMode.None).Length} t={t:F0}");
            if (t > 14f && Every("list", 100f) || t > 32f && Every("list2", 100f))
            {
                var ids = string.Join(",", fish.ConvertAll(pair => pair.Key));
                Fact($"fish-ids count={fish.Count} ids={ids}");
            }
            // Only fish near the divers are active (the game culls the rest), so both pick from active fish:
            // the host its lowest active id, the client its highest. A client hit is a harpoon attack request.
            var active = fish.FindAll(pair => pair.Value != null && pair.Value.gameObject.activeInHierarchy && pair.Value.HP > 0);
            var hostTarget = damageable.Count > 0 ? damageable[0] : default;
            if (t > 16f && Every("active", 100f) || t > 32f && Every("active2", 100f))
                Fact($"fish-active ids={string.Join(",", active.ConvertAll(pair => pair.Key))}");
            // Some species can't be killed by a harpoon hit; like a player, move on to another fish when a hit
            // doesn't kill within 4 s (up to 6 tries). The host works up from the lowest id, the client down from
            // the highest active id, so they don't hit the same fish.
            if (t > 18f && damageable.Count > 0)
            {
                if (IsHost && hostTries < 6 && (hostKill < 0 || t - hostAt > 4f && IsAlive(fish, hostKill)))
                {
                    var target = damageable.Find(pair => !hostTried.Contains(pair.Key) && pair.Value.HP > 0);
                    if (target.Value != null)
                    {
                        hostKill = target.Key;
                        hostTried.Add(hostKill);
                        hostTries++;
                        hostAt = t;
                        // Like a harpoon hit: the fish wakes, turns on its attacker, then takes the damage.
                        target.Value.IsFishEnable = true;
                        if (target.Value.IsFishSleeped)
                            target.Value.OnForceEndSleepMode();
                        var attacker = LocalPlayer();
                        if (attacker != null)
                            target.Value.OnUnderAttack(attacker);
                        target.Value.SetHPDamage((int)Math.Max(1f, target.Value.HP) + 1000, default, AttackType.Player_Harpoon);
                        Fact($"fish-host-kill id={hostKill}");
                    }
                }
                if (!IsHost && clientTries < 6 && (clientKill < 0 || t - clientAt > 4f && IsAlive(fish, clientKill)))
                {
                    var candidates = active.FindAll(pair => damageable.Contains(pair) && !clientTried.Contains(pair.Key));
                    if (candidates.Count > 1)
                    {
                        var target = candidates[candidates.Count - 1];
                        clientKill = target.Key;
                        clientTried.Add(clientKill);
                        clientTries++;
                        clientAt = t;
                        // What a client harpoon hit sends (OnTakeDamage -> ObserveClientDamage -> RequestDamage).
                        var sent = probe.LabFishReplicator.RequestDamage(session(), probe.LabSceneId, target.Value,
                            (int)Math.Max(1f, target.Value.HP) + 1000, default, AttackType.Player_Harpoon);
                        Fact($"fish-client-hit id={clientKill} sent={sent}");
                    }
                }
            }
            if (t > 52f)
            {
                var dead = new List<int>();
                foreach (var pair in fish)
                    if (pair.Value == null || pair.Value.IsDead() || pair.Value.HP <= 0 || pair.Value.IsCorpse)
                        dead.Add(pair.Key);
                var known = new HashSet<int>(fish.ConvertAll(pair => pair.Key));
                Fact($"fish-dead ids={string.Join(",", dead)} gone={string.Join(",", Array.FindAll(new[] { hostKill, clientKill }, id => id >= 0 && !known.Contains(id)))}");
                return true;
            }
            return false;
        });

        // Death: the client dies first, then the host. Both must end the dive and be back on the boat
        // (a party wipe used to leave both divers in spectator mode forever).
        var died = false;
        Add("death", 90f, t =>
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerCharacter>();
            if (!died && me != null && t > (IsHost ? 12f : 4f))
            {
                died = true;
                // Real damage, so the game runs its own death sequence (oxygen out, result screen, back to boat).
                me.SetHPDamage(100000, default, AttackType.Fish);
                if (!me.IsDead())
                    me.OnDie();
                Fact($"died hp-dead={me.IsDead()}");
            }
            if (died && Scene == "DR_Lobby" && LocalPlayer() != null)
            {
                Fact($"after-death scene={Scene} t={t:F0}");
                return true;
            }
            return false;
        });

        var stalled = false;
        Add("boat-after", 60f, t =>
        {
            // A stalled game (loading, a busy PC): the client's main thread freezes for 12 s once.
            if (!IsHost && !stalled && t > 2f)
            {
                stalled = true;
                Fact("stall-begin seconds=12");
                System.Threading.Thread.Sleep(12000);
                Fact("stall-end");
            }
            var manager = DialogueManager.hasInstance ? DialogueManager.Instance : null;
            if (manager != null && manager.IsPlaying && Every("skip", 1.5f))
                manager.OnSkip();
            if (!Every("boat", 5f))
                return false;
            Fact($"boat-after scene={Scene} connected={session()?.Connected == true}");
            return t > 20f;
        });

        // Day to night: both players pick the sushi bar on the boat's map (travel needs 2/2 consent).
        var clicked = 0f;
        Add("sushi-travel", 150f, t =>
        {
            var scene = Scene;
            if (scene.IndexOf("Sushi", StringComparison.OrdinalIgnoreCase) >= 0 && LocalPlayer() != null)
            {
                Fact($"sushi-scene {scene}");
                return true;
            }
            var manager = DialogueManager.hasInstance ? DialogueManager.Instance : null;
            if (manager != null && manager.IsPlaying)
            {
                if (Every("skip", 1.5f))
                    manager.OnSkip();
                return false;
            }
            if (scene != "DR_Lobby" || t < (IsHost ? 8f : 4f) || t - clicked < 10f)
                return false;
            foreach (var element in UnityEngine.Object.FindObjectsByType<Common.Contents.MoveSceneElement>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (element.sceneName != Common.Contents.MoveSceneElement.SceneName.SuShi)
                    continue;
                clicked = t;
                Fact($"sushi-click locked={element.IsLocked}");
                element.OnClick();
                return false;
            }
            if (Every("nomap", 10f))
                Fact("sushi-element missing");
            return false;
        });

        Add("sushi", 60f, t =>
        {
            var manager = DialogueManager.hasInstance ? DialogueManager.Instance : null;
            if (manager != null && manager.IsPlaying && Every("skip", 1.5f))
                manager.OnSkip();
            if (Every("sushi", 10f))
                Fact($"sushi-state scene={Scene} bar={(SushiBarManager.hasInstance ? "open" : "none")} t={t:F0}");
            return t > 40f;
        });
    }
}
