using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace DaveTheDiverMP;

// Online play through Steam: a friends-only lobby tells friends who is hosting, and the mod's datagrams travel as
// SteamNetworkingMessages (SteamNetworkingSockets P2P, relayed by Valve's Steam Datagram Relay, so no ports or IPs).
// Called straight from steam_api64.dll, which the game has already initialised: the game's IL2CPP Steamworks.NET
// wrapper strips methods it never uses, and Steam callbacks can't be registered from here, so every async result is
// polled (SteamUtils call results, lobby members, friends' game info).
internal static unsafe class SteamP2P
{
    internal const uint AppId = 1868140;
    internal const string AddressPrefix = "steam";
    private const string Lib = "steam_api64";
    private const int Channel = 0;
    private const int SendUnreliableNoNagle = 1 | 4; // k_nSteamNetworkingSend_Unreliable | NoNagle
    private const int SendNoDelay = 4 | 1;
    private const int LobbyTypeFriendsOnly = 1;
    private const int LobbyCreatedCallback = 513;
    private const int LobbyEnterCallback = 504;
    private const int FriendFlagImmediate = 4;
    private const string LobbyTag = "dtmp";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Identity
    {
        public int Type;
        public int Size;
        public fixed byte Raw[128];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct FriendGameInfo
    {
        public ulong GameId;
        public uint GameIp;
        public ushort GamePort, QueryPort;
        public ulong Lobby;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct LobbyCreated { public int Result; public ulong Lobby; }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct LobbyEnter { public ulong Lobby; public uint Permissions; public byte Locked; public uint Response; }

    [DllImport(Lib)] private static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Lib)] private static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
    [DllImport(Lib)] private static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport(Lib)] private static extern IntPtr SteamAPI_SteamFriends_v017();
    [DllImport(Lib)] private static extern IntPtr SteamAPI_SteamUser_v021();
    [DllImport(Lib)] private static extern IntPtr SteamAPI_SteamUtils_v010();
    [DllImport(Lib)] private static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Lib)] private static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int value, int setting);
    private const int ConfigSendBufferSize = 9;
    private const int ConfigSendRateMin = 10;
    private const int ConfigSendRateMax = 11;
    [DllImport(Lib)] private static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(ref Identity identity, ulong id);
    [DllImport(Lib)] private static extern ulong SteamAPI_SteamNetworkingIdentity_GetSteamID64(IntPtr identity);
    [DllImport(Lib)] private static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, ref Identity remote, byte* data, uint size, int flags, int channel);
    [DllImport(Lib)] private static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* messages, int max);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, ref Identity remote);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, ref Identity remote);
    [DllImport(Lib)] private static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
    [DllImport(Lib)] private static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int type, int maxMembers);
    [DllImport(Lib)] private static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
    [DllImport(Lib)] private static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
    [DllImport(Lib)] private static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
    [DllImport(Lib)] private static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int index);
    [DllImport(Lib)] private static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, byte[] key, byte[] value);
    [DllImport(Lib)] private static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, byte[] key);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr self, ulong call, void* result, int size, int callback, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport(Lib)] private static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
    [DllImport(Lib)] private static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong friend, out FriendGameInfo info);
    [DllImport(Lib)] private static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong friend);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, byte[] key, byte[] value);
    [DllImport(Lib)] private static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong lobby);

    private static IntPtr _messages, _utils, _matchmaking, _friends, _user, _steamUtils;
    private static bool _resolverSet;
    private static float _retryAt;
    internal static string Problem = string.Empty;
    // Lab only (-dtmp-steam-mock <slot>): the calls below go to SteamMock (localhost) instead of steam_api64.
    internal static SteamMock Mock;

    internal static bool Ready
    {
        get
        {
            if (Mock != null || _messages != IntPtr.Zero && _matchmaking != IntPtr.Zero && _friends != IntPtr.Zero && _user != IntPtr.Zero && _steamUtils != IntPtr.Zero)
                return true;
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _retryAt)
                return false;
            _retryAt = now + 5f;
            try
            {
                if (!_resolverSet)
                {
                    _resolverSet = true;
                    // steam_api64.dll lives in DaveTheDiver_Data/Plugins/x86_64, outside the default search path.
                    _steamDll = Path.Combine(UnityEngine.Application.dataPath, "Plugins", "x86_64", "steam_api64.dll");
                    NativeLibrary.SetDllImportResolver(typeof(SteamP2P).Assembly, ResolveSteam);
                }
                _messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
                _utils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
                _matchmaking = SteamAPI_SteamMatchmaking_v009();
                _friends = SteamAPI_SteamFriends_v017();
                _user = SteamAPI_SteamUser_v021();
                _steamUtils = SteamAPI_SteamUtils_v010();
                if (_messages == IntPtr.Zero || _matchmaking == IntPtr.Zero || _friends == IntPtr.Zero || _user == IntPtr.Zero || _steamUtils == IntPtr.Zero)
                {
                    Problem = "Steam is not running for this game";
                    _messages = IntPtr.Zero;
                    return false;
                }
                if (_utils != IntPtr.Zero)
                {
                    SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(_utils);
                    // Room for a save transfer next to game traffic: 2 MB buffer, 256 KB/s to 2 MB/s.
                    SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, ConfigSendBufferSize, 2 * 1024 * 1024);
                    SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, ConfigSendRateMin, 256 * 1024);
                    SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, ConfigSendRateMax, 2 * 1024 * 1024);
                }
                return true;
            }
            catch (Exception exception)
            {
                Problem = "Steam API unavailable: " + exception.Message;
                _messages = IntPtr.Zero;
                return false;
            }
        }
    }

    private static string _steamDll = string.Empty;

    private static IntPtr ResolveSteam(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path) =>
        name == Lib && File.Exists(_steamDll) ? NativeLibrary.Load(_steamDll) : IntPtr.Zero;

    internal static ulong Self => Mock != null ? Mock.Self : Ready ? SteamAPI_ISteamUser_GetSteamID(_user) : 0;

    internal static bool IsSteamAddress(string address) =>
        address != null && address.StartsWith(AddressPrefix, StringComparison.OrdinalIgnoreCase);

    internal static string AddressOf(ulong host) => $"{AddressPrefix}:{host}";

    // SteamID account type lives in bits 52-55: 1 = individual, 8 = chat (lobbies). Mock lobbies are 1.7e9+.
    internal static bool IsLobbyId(ulong id) =>
        Mock != null ? id >= 800000000UL + SteamMock.Base : id != 0 && ((id >> 52) & 0xF) == 8;

    internal static bool TryParseAddress(string address, out ulong host)
    {
        host = 0;
        if (!IsSteamAddress(address))
            return false;
        var rest = address.Substring(AddressPrefix.Length).TrimStart(':');
        return rest.Length == 0 || ulong.TryParse(rest, out host);
    }

    private static string Utf8(IntPtr text) => text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text) ?? string.Empty;
    private static byte[] Z(string text) => Encoding.UTF8.GetBytes((text ?? string.Empty) + "\0");

    private static Identity IdentityOf(ulong id)
    {
        var identity = default(Identity);
        SteamAPI_SteamNetworkingIdentity_SetSteamID64(ref identity, id);
        return identity;
    }

    internal static bool Send(ulong to, byte[] data, int length)
    {
        if (Mock != null)
            return Mock.Send(to, data, length);
        if (!Ready)
            return false;
        var identity = IdentityOf(to);
        fixed (byte* bytes = data)
            return SteamAPI_ISteamNetworkingMessages_SendMessageToUser(
                _messages, ref identity, bytes, (uint)length, SendUnreliableNoNagle, Channel) == 1;
    }

    // Drains waiting messages into the callback (sender, bytes); returns how many were read.
    internal static int Receive(Action<ulong, byte[]> deliver, int max)
    {
        if (Mock != null)
            return Mock.Receive(deliver, max);
        if (!Ready)
            return 0;
        var batch = stackalloc IntPtr[32];
        var total = 0;
        while (total < max)
        {
            var count = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(_messages, Channel, batch, Math.Min(32, max - total));
            if (count <= 0)
                break;
            for (var i = 0; i < count; i++)
            {
                var message = batch[i];
                try
                {
                    // SteamNetworkingMessage_t: m_pData at 0, m_cbSize at 8, m_identityPeer at 16.
                    var data = *(IntPtr*)message;
                    var size = *(int*)((byte*)message + 8);
                    var from = SteamAPI_SteamNetworkingIdentity_GetSteamID64(message + 16);
                    if (size > 0 && size <= Protocol.MaxDatagramSize)
                    {
                        var copy = new byte[size];
                        Marshal.Copy(data, copy, 0, size);
                        deliver(from, copy);
                    }
                }
                finally
                {
                    SteamAPI_SteamNetworkingMessage_t_Release(message);
                }
            }
            total += count;
        }
        return total;
    }

    // A host accepts a pending session from a peer it expects (a member of its lobby). Returns false when the peer
    // has no pending session.
    internal static bool Accept(ulong peer)
    {
        if (Mock != null)
            return Mock.Accept(peer);
        if (!Ready)
            return false;
        var identity = IdentityOf(peer);
        return SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(_messages, ref identity);
    }

    internal static void Close(ulong peer)
    {
        if (Mock != null)
        {
            Mock.Close(peer);
            return;
        }
        if (!Ready)
            return;
        var identity = IdentityOf(peer);
        SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(_messages, ref identity);
    }

    internal static void SetPresence(string key, string value)
    {
        if (Mock != null || !Ready)
            return;
        SteamAPI_ISteamFriends_SetRichPresence(_friends, Z(key), Z(value));
    }

    internal static void InviteOverlay(ulong lobby)
    {
        if (Mock == null && Ready && lobby != 0)
            SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(_friends, lobby);
    }

    internal static ulong CreateLobbyCall() =>
        Mock != null ? Mock.CreateLobby() : Ready ? SteamAPI_ISteamMatchmaking_CreateLobby(_matchmaking, LobbyTypeFriendsOnly, 2) : 0;

    internal static ulong JoinLobbyCall(ulong lobby) =>
        Mock != null ? Mock.JoinLobby(lobby) : Ready ? SteamAPI_ISteamMatchmaking_JoinLobby(_matchmaking, lobby) : 0;

    internal static void LeaveLobby(ulong lobby)
    {
        if (lobby == 0)
            return;
        if (Mock != null)
            Mock.LeaveLobby(lobby);
        else if (Ready)
            SteamAPI_ISteamMatchmaking_LeaveLobby(_matchmaking, lobby);
    }

    // 0 = still running, 1 = done (lobby id out), -1 = failed.
    internal static int PollLobbyCall(ulong call, bool create, out ulong lobby)
    {
        lobby = 0;
        if (Mock != null)
            return Mock.PollCall(call, out lobby);
        if (!Ready || call == 0)
            return -1;
        if (!SteamAPI_ISteamUtils_IsAPICallCompleted(_steamUtils, call, out var failed))
            return 0;
        if (failed)
            return -1;
        if (create)
        {
            LobbyCreated result;
            if (!SteamAPI_ISteamUtils_GetAPICallResult(_steamUtils, call, &result, sizeof(LobbyCreated), LobbyCreatedCallback, out failed) || failed || result.Result != 1)
                return -1;
            lobby = result.Lobby;
        }
        else
        {
            LobbyEnter result;
            if (!SteamAPI_ISteamUtils_GetAPICallResult(_steamUtils, call, &result, sizeof(LobbyEnter), LobbyEnterCallback, out failed) || failed || result.Response != 1)
                return -1;
            lobby = result.Lobby;
        }
        return 1;
    }

    internal static ulong LobbyOwner(ulong lobby) =>
        Mock != null ? Mock.LobbyOwner(lobby) : Ready && lobby != 0 ? SteamAPI_ISteamMatchmaking_GetLobbyOwner(_matchmaking, lobby) : 0;

    internal static List<ulong> LobbyMembers(ulong lobby)
    {
        var members = new List<ulong>();
        if (Mock != null)
            return Mock.LobbyMembers(lobby);
        if (!Ready || lobby == 0)
            return members;
        var count = SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(_matchmaking, lobby);
        for (var i = 0; i < count && i < 16; i++)
            members.Add(SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(_matchmaking, lobby, i));
        return members;
    }

    internal static void TagLobby(ulong lobby, uint buildId)
    {
        if (Mock != null || !Ready || lobby == 0)
            return;
        SteamAPI_ISteamMatchmaking_SetLobbyData(_matchmaking, lobby, Z(LobbyTag), Z(buildId.ToString("X8")));
    }

    internal static string LobbyBuild(ulong lobby) =>
        Mock != null ? Mock.LobbyBuild(lobby) : Ready && lobby != 0 ? Utf8(SteamAPI_ISteamMatchmaking_GetLobbyData(_matchmaking, lobby, Z(LobbyTag))) : string.Empty;

    internal sealed class HostingFriend
    {
        public ulong Id;
        public ulong Lobby;
        public string Name;
    }

    // Friends in Dave the Diver who are in a lobby right now (a friends-only lobby is visible to friends).
    internal static List<HostingFriend> FriendsHosting()
    {
        if (Mock != null)
            return Mock.FriendsHosting();
        var list = new List<HostingFriend>();
        if (!Ready)
            return list;
        var count = SteamAPI_ISteamFriends_GetFriendCount(_friends, FriendFlagImmediate);
        for (var i = 0; i < count && i < 500; i++)
        {
            var id = SteamAPI_ISteamFriends_GetFriendByIndex(_friends, i, FriendFlagImmediate);
            if (!SteamAPI_ISteamFriends_GetFriendGamePlayed(_friends, id, out var info) ||
                (info.GameId & 0xFFFFFF) != AppId || info.Lobby == 0)
                continue;
            list.Add(new HostingFriend { Id = id, Lobby = info.Lobby, Name = Utf8(SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, id)) });
        }
        return list;
    }

    // "+connect_lobby <id>" is what Steam passes when a friend clicks Join Game or accepts an invite before the game
    // is running.
    internal static ulong LobbyFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out var lobby))
                return lobby;
        return 0;
    }

    internal static void SelfTest()
    {
        if (!TryParseAddress("steam:76561198000000000", out var host) || host != 76561198000000000UL ||
            !TryParseAddress("steam", out host) || host != 0 ||
            TryParseAddress("127.0.0.1", out _) || TryParseAddress("steam:x", out _) ||
            AddressOf(5) != "steam:5" || sizeof(Identity) != 136 ||
            !IsLobbyId(109775241000000000UL) || IsLobbyId(76561198000000000UL) ||
            Marshal.SizeOf<FriendGameInfo>() != 24 || sizeof(LobbyCreated) != 16 || sizeof(LobbyEnter) != 24)
            throw new InvalidOperationException("Steam P2P self-test failed");
    }
}

// Lab-only stand-in for Steam (-dtmp-steam-mock <slot>) so the Steam path (lobby, owner lookup, session accept,
// datagrams) runs between game copies on one Steam account, which can't P2P to itself. Each copy is fake account
// 900000000 + slot. Lobbies are files in BepInEx/../../steam-mock (shared by the lab copies); datagrams go over
// localhost UDP port 27300 + slot. Like Steam, datagrams from a peer are held until Accept() or until we have sent
// to that peer first.
internal sealed class SteamMock
{
    internal const ulong Base = 900000000;
    private const int BasePort = 27300;
    internal readonly ulong Self;
    private readonly string _folder;
    private readonly UdpClient _udp;
    private readonly HashSet<ulong> _open = new();
    private readonly Dictionary<ulong, Queue<byte[]>> _held = new();
    private readonly Dictionary<ulong, ulong> _calls = new();
    private ulong _nextCall = 1;

    internal SteamMock(int slot, string folder)
    {
        Self = Base + (ulong)slot;
        _folder = folder;
        Directory.CreateDirectory(folder);
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, BasePort + slot));
        _udp.Client.Blocking = false;
    }

    private string LobbyFile(ulong lobby) => Path.Combine(_folder, $"lobby-{lobby}.txt");

    internal ulong CreateLobby()
    {
        var lobby = 800000000UL + Self;
        File.WriteAllText(LobbyFile(lobby), Self.ToString());
        _calls[_nextCall] = lobby;
        return _nextCall++;
    }

    internal ulong JoinLobby(ulong lobby)
    {
        var file = LobbyFile(lobby);
        if (!File.Exists(file))
            return 0;
        File.AppendAllText(file, "\n" + Self);
        _calls[_nextCall] = lobby;
        return _nextCall++;
    }

    internal int PollCall(ulong call, out ulong lobby) => _calls.TryGetValue(call, out lobby) ? 1 : -1;

    internal void LeaveLobby(ulong lobby)
    {
        var file = LobbyFile(lobby);
        try
        {
            if (LobbyOwner(lobby) == Self)
                File.Delete(file);
            else if (File.Exists(file))
                File.WriteAllLines(file, Array.FindAll(File.ReadAllLines(file), line => line != Self.ToString()));
        }
        catch (IOException)
        {
        }
    }

    internal List<ulong> LobbyMembers(ulong lobby)
    {
        var members = new List<ulong>();
        try
        {
            foreach (var line in File.ReadAllLines(LobbyFile(lobby)))
                if (ulong.TryParse(line, out var id))
                    members.Add(id);
        }
        catch (IOException)
        {
        }
        return members;
    }

    internal ulong LobbyOwner(ulong lobby)
    {
        var members = LobbyMembers(lobby);
        return members.Count > 0 ? members[0] : 0;
    }

    internal string LobbyBuild(ulong lobby) => string.Empty;

    internal List<SteamP2P.HostingFriend> FriendsHosting()
    {
        var list = new List<SteamP2P.HostingFriend>();
        foreach (var file in Directory.GetFiles(_folder, "lobby-*.txt"))
        {
            if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file).Substring(6), out var lobby))
                continue;
            var owner = LobbyOwner(lobby);
            if (owner != 0 && owner != Self)
                list.Add(new SteamP2P.HostingFriend { Id = owner, Lobby = lobby, Name = "Mock " + (owner - Base) });
        }
        return list;
    }

    internal bool Send(ulong to, byte[] data, int length)
    {
        if (to < Base || to > Base + 64)
            return false;
        _open.Add(to);
        var frame = new byte[length + 8];
        BitConverter.GetBytes(Self).CopyTo(frame, 0);
        Buffer.BlockCopy(data, 0, frame, 8, length);
        try
        {
            _udp.Send(frame, frame.Length, new IPEndPoint(IPAddress.Loopback, BasePort + (int)(to - Base)));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal bool Accept(ulong peer)
    {
        if (!_held.TryGetValue(peer, out var waiting))
            return false;
        _open.Add(peer);
        return waiting.Count > 0;
    }

    internal void Close(ulong peer)
    {
        _open.Remove(peer);
        _held.Remove(peer);
    }

    internal int Receive(Action<ulong, byte[]> deliver, int max)
    {
        var total = 0;
        foreach (var pair in _held)
            while (_open.Contains(pair.Key) && pair.Value.Count > 0 && total < max)
            {
                deliver(pair.Key, pair.Value.Dequeue());
                total++;
            }
        while (total < max && _udp.Available > 0)
        {
            IPEndPoint from = null;
            byte[] frame;
            try
            {
                frame = _udp.Receive(ref from);
            }
            catch (SocketException)
            {
                continue;
            }
            if (frame.Length <= 8)
                continue;
            var sender = BitConverter.ToUInt64(frame, 0);
            var data = new byte[frame.Length - 8];
            Buffer.BlockCopy(frame, 8, data, 0, data.Length);
            if (_open.Contains(sender))
            {
                deliver(sender, data);
                total++;
                continue;
            }
            if (!_held.TryGetValue(sender, out var waiting))
                _held[sender] = waiting = new Queue<byte[]>();
            if (waiting.Count < 64)
                waiting.Enqueue(data);
        }
        return total;
    }
}
