using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DR.Title;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DaveTheDiverMP;

// Lab mode, only with -dtmp-lab <name> on the command line (scripts\launch-lab.ps1). Never active for players.
//   - 30 fps, lowest quality, keeps running in the background;
//   - the game's saves go to <lab copy>\lab-saves instead of LocalLow, so two copies on one PC never share a slot;
//   - -dtmp-host [lan|steam] / -dtmp-join <address|steam> drive the title room like a player would;
//   - -dtmp-steam-mock <slot> swaps Steam for SteamMock (one Steam account can't P2P to itself);
//   - -dtmp-tour <name> runs LabTour after the campaign starts; -dtmp-mutant <name> turns one sync rule off.
// Every milestone is a "LAB <name> ..." log line that tests\lab-suite.ps1 asserts on.
internal static class LabMode
{
    internal static bool Active { get; private set; }
    internal static string Name { get; private set; } = string.Empty;
    internal static string HostMode { get; private set; } = string.Empty;
    internal static string JoinAddress { get; private set; } = string.Empty;
    internal static string Tour { get; private set; } = string.Empty;
    internal static string Mutant { get; private set; } = string.Empty;
    internal static string StopAfter { get; private set; } = string.Empty;
    internal static string Port { get; private set; } = "27877";
    internal static string SaveFolder { get; private set; } = string.Empty;
    private static ManualLogSource _log;
    private static float _titleSeenAt = -1f;
    private static string _titleStep = string.Empty;
    private static float _nextTitleAction;
    private static string _lastScene = string.Empty;
    private static bool _loggedConnected;
    private static bool _loggedSave;
    private static string _lastSaveStatus = string.Empty;

    // -dtmp-mutant takes one name or a comma-separated list (mutants that target different checks).
    internal static bool IsMutant(string name) =>
        Active && Array.Exists(Mutant.Split(','), part => string.Equals(part.Trim(), name, StringComparison.OrdinalIgnoreCase));

    internal static void Init(ManualLogSource log, Harmony harmony)
    {
        var args = Environment.GetCommandLineArgs();
        string Arg(string key)
        {
            for (var i = 0; i < args.Length; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[i + 1] : string.Empty;
            return null;
        }

        Name = Arg("-dtmp-lab");
        if (Name == null)
            return;
        Active = true;
        _log = log;
        HostMode = Arg("-dtmp-host") ?? string.Empty;
        if (Arg("-dtmp-host") != null && HostMode.Length == 0)
            HostMode = "lan";
        JoinAddress = Arg("-dtmp-join") ?? string.Empty;
        Tour = Arg("-dtmp-tour") ?? string.Empty;
        Mutant = Arg("-dtmp-mutant") ?? string.Empty;
        StopAfter = Arg("-dtmp-stop-after") ?? string.Empty;
        Port = Arg("-dtmp-port") is { Length: > 0 } port ? port : Port;
        var mock = Arg("-dtmp-steam-mock");
        if (int.TryParse(mock, out var slot))
            SteamP2P.Mock = new SteamMock(slot, Path.Combine(Paths.GameRootPath, "..", "steam-mock"));

        SaveFolder = Path.Combine(Paths.GameRootPath, "lab-saves");
        Directory.CreateDirectory(SaveFolder);
        RedirectSaves(harmony);
        Log($"start host={HostMode} join={JoinAddress} tour={Tour} mutant={Mutant} steamMock={mock ?? "off"}");
    }

    internal static void Log(string message) => _log?.LogInfo($"LAB {Name} {message}");

    // The game builds every save path from Application.persistentDataPath (LocalLow\nexon\DAVE THE DIVER).
    private static void RedirectSaves(Harmony harmony)
    {
        var names = new HashSet<string>
        {
            // Computed paths only: auto-property getters can run before their backing string exists.
            "GetSaveFolder", "GetSaveFilePath", "GetFailedSaveFilePath", "GetOldSaveFilePath"
        };
        var postfix = new HarmonyMethod(typeof(LabMode).GetMethod(nameof(RedirectPath), BindingFlags.NonPublic | BindingFlags.Static));
        var patched = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(TitleManager).Assembly))
        {
            if (type.Namespace == null || !type.Namespace.StartsWith("DR") && !type.Namespace.StartsWith("Save"))
                continue;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (method.ReturnType != typeof(string) || !names.Contains(method.Name) || method.IsAbstract)
                    continue;
                try
                {
                    harmony.Patch(method, postfix: postfix);
                    patched++;
                }
                catch (Exception exception)
                {
                    _log.LogWarning($"LAB save redirect skipped {type.Name}.{method.Name}: {exception.Message}");
                }
            }
        }
        Log($"save redirect patched={patched} folder={SaveFolder}");
    }

    private static void RedirectPath(ref string __result)
    {
        if (string.IsNullOrEmpty(__result))
            return;
        var root = Application.persistentDataPath.Replace('\\', '/');
        var value = __result.Replace('\\', '/');
        if (value.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            __result = SaveFolder.Replace('\\', '/') + value.Substring(root.Length);
    }

    internal static void Apply()
    {
        if (!Active)
            return;
        Application.targetFrameRate = 30;
        QualitySettings.vSyncCount = 0;
        QualitySettings.SetQualityLevel(0, true);
        Application.runInBackground = true;
    }

    internal static void Tick(ProbeBehaviour probe)
    {
        if (!Active || probe == null)
            return;
        var scene = SceneManager.GetActiveScene().name;
        if (scene != _lastScene)
        {
            _lastScene = scene;
            Log($"scene {scene}");
        }
        var session = probe._session;
        if (session != null && session.Connected && !_loggedConnected)
        {
            _loggedConnected = true;
            Log($"connected remote={session.RemoteName} steam={session.IsSteam}");
        }
        else if (session != null && !session.Connected && _loggedConnected)
        {
            _loggedConnected = false;
            Log("disconnected");
        }
        if (MultiplayerSaveSync.Status != _lastSaveStatus)
        {
            _lastSaveStatus = MultiplayerSaveSync.Status;
            Log($"save-status {_lastSaveStatus}");
        }
        if (!_loggedSave && (MultiplayerSaveSync.ClientLoaded || MultiplayerSaveSync.HostRemoteLoaded))
        {
            _loggedSave = true;
            Log($"save-synced status={MultiplayerSaveSync.Status}");
        }
        TickTitle(probe, session);
        LabTour.Tick(probe);
    }

    private static void TickTitle(ProbeBehaviour probe, UdpSession session)
    {
        if (HostMode.Length == 0 && JoinAddress.Length == 0)
            return;
        var manager = UnityEngine.Object.FindFirstObjectByType<TitleManager>();
        if (manager == null)
        {
            _titleSeenAt = -1f;
            return;
        }
        var now = Time.realtimeSinceStartup;
        if (_titleSeenAt < 0f)
        {
            _titleSeenAt = now;
            Log("title");
        }
        if (now - _titleSeenAt < 4f || now < _nextTitleAction || _titleStep == "started")
            return;
        _nextTitleAction = now + 1f;

        if (!TitleOnlineMenu.LabVisible)
        {
            TitleOnlineMenu.LabPress(manager, "online", null, null, null);
            return;
        }
        if (ProbeBehaviour.Role == SessionRole.Offline)
        {
            if (HostMode.Length > 0)
            {
                var address = HostMode == "steam" ? SteamP2P.AddressPrefix : "127.0.0.1";
                if (TitleOnlineMenu.LabPress(manager, "host", address, Port, "Host"))
                    Log($"hosting via={HostMode}");
                return;
            }
            var join = JoinAddress;
            if (join == "steam")
            {
                // Like picking a friend from the Steam join list: the first friend in a lobby.
                var friends = SteamP2P.FriendsHosting();
                if (friends.Count == 0)
                    return;
                join = SteamP2P.AddressOf(friends[0].Lobby);
            }
            if (TitleOnlineMenu.LabPress(manager, "join", join, Port, Name))
                Log($"joining {join}");
            return;
        }
        if (session == null || !session.Connected)
            return;
        if (ProbeBehaviour.Role == SessionRole.Client && MultiplayerSaveSync.ClientLoaded && !TitleOnlineMenu.LabClientReady)
        {
            TitleOnlineMenu.LabPress(manager, "ready", null, null, null);
            Log("ready");
        }
        else if (ProbeBehaviour.Role == SessionRole.Host && TitleOnlineMenu.LabRemoteReady && MultiplayerSaveSync.HostRemoteLoaded)
        {
            TitleOnlineMenu.LabPress(manager, "start", null, null, null);
            _titleStep = "started";
            Log("campaign-start");
        }
    }
}
