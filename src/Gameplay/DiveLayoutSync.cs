using System;
using System.Collections.Generic;
using HarmonyLib;

namespace DaveTheDiverMP;

// A dive area is assembled from sub-scenes picked per dive (for example B01_01_01 or B02_01_01 for the same layer).
// SceneContext.BuildMapLayerData picks them from the active missions and the saved layer cache, separately on each
// side, so host and client could dive into different seas: different spawners, so the client could never bind the
// fish next to the host, and different walls. The host now sends the scene ids it picked (before its dive
// transition), and the client replaces its own pick with them right after building.
internal static class DiveLayoutSync
{
    private const string Prefix = "#layout:";
    private static bool _applying;

    internal static bool IsLayoutMessage(string name) =>
        name != null && name.StartsWith(Prefix, StringComparison.Ordinal);

    internal static string Encode(IReadOnlyList<int> sceneIds) => Prefix + string.Join(",", sceneIds);

    internal static bool TryDecode(string message, out List<int> sceneIds)
    {
        sceneIds = new List<int>();
        if (!IsLayoutMessage(message))
            return false;
        foreach (var part in message.Substring(Prefix.Length).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var id) || id <= 0)
                return false;
            sceneIds.Add(id);
        }
        return sceneIds.Count > 0;
    }

    internal static void AfterBuild(SceneContext context)
    {
        var probe = ProbeBehaviour.Instance;
        var session = probe?._session;
        if (context == null || _applying || session?.Connected != true || LabMode.IsMutant("layout"))
            return;
        if (ProbeBehaviour.Role == SessionRole.Host)
            SendHostLayout(context, session);
        else if (ProbeBehaviour.Role == SessionRole.Client)
            ApplyHostLayout(context, session);
    }

    private static void SendHostLayout(SceneContext context, UdpSession session)
    {
        var selected = context.GetSelectedMapLayerCached;
        if (selected == null || selected.Count == 0)
            return;
        var ids = new List<int>();
        foreach (var cache in selected)
            if (cache != null && cache.SceneID > 0)
                ids.Add(cache.SceneID);
        var message = Encode(ids);
        if (ids.Count == 0 || message.Length > 128)
            return;
        session.SendAdditiveSceneLoad(new AdditiveSceneLoad(message, ids.Count));
        ProbeBehaviour.Logger?.LogInfo($"Dive layout sent: {string.Join(",", ids)}");
    }

    private static void ApplyHostLayout(SceneContext context, UdpSession session)
    {
        if (!session.TryTakeDiveLayout(out var message) || !TryDecode(message, out var ids))
        {
            ProbeBehaviour.Logger?.LogWarning("Dive layout: no host layout before the dive; keeping the local pick");
            return;
        }
        // The client's layer cache only knows its own candidates, so rebuild the layout from the host's scene ids
        // with the game's own id-list builder.
        var tids = new Il2CppSystem.Collections.Generic.List<int>();
        foreach (var id in ids)
            tids.Add(id);
        _applying = true;
        try
        {
            context.BuildMapLayerDataByCheat(
                tids.Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>().GetEnumerator());
        }
        finally
        {
            _applying = false;
        }
        var applied = new List<int>();
        var selected = context.GetSelectedMapLayerCached;
        if (selected != null)
            foreach (var cache in selected)
                if (cache != null)
                    applied.Add(cache.SceneID);
        applied.Sort();
        var expected = new List<int>(ids);
        expected.Sort();
        if (string.Join(",", applied) != string.Join(",", expected))
            ProbeBehaviour.Logger?.LogWarning(
                $"Dive layout: host={string.Join(",", ids)} but built {string.Join(",", applied)}");
        ProbeBehaviour.Logger?.LogInfo($"Dive layout applied from host: {string.Join(",", ids)}");
    }

    internal static void SelfTest()
    {
        if (!TryDecode(Encode(new[] { 14012001, 14013001 }), out var ids) || ids.Count != 2 || ids[1] != 14013001 ||
            TryDecode("B01_01_01", out _) || TryDecode("#layout:x", out _))
            throw new InvalidOperationException("Dive layout self-test failed");
    }
}

[HarmonyPatch(typeof(SceneContext), nameof(SceneContext.BuildMapLayerData))]
internal static class DiveLayoutBuildPatch
{
    private static void Postfix(SceneContext __instance) => DiveLayoutSync.AfterBuild(__instance);
}
