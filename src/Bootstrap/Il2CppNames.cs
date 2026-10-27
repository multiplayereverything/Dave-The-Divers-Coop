
namespace DaveTheDiverMP;

// Il2CppInterop returns wrappers typed as whatever was asked for (FindObjectsByType<BossControllerBase> gives
// BossControllerBase wrappers even for a subclass), so GetType() can name the base class on one peer and the real
// class on the other. The IL2CPP runtime type is the same on every peer.
internal static class Il2CppNames
{
    internal static string Name(object value) =>
        value is Il2CppSystem.Object native ? native.GetIl2CppType().Name : value?.GetType().Name;

    internal static string FullName(object value) =>
        value is Il2CppSystem.Object native ? native.GetIl2CppType().FullName : value?.GetType().FullName;
}
