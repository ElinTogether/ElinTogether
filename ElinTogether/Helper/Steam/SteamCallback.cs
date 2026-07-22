using System;
using Steamworks;

namespace ElinTogether.Helper.Steam;

internal class SteamCallback
{
    // ReSharper disable once ChangeFieldTypeToSystemThreadingLock
    // shared global event lock - not per instance
    public static readonly object EventLock = new();
}

// ReSharper disable once StaticMemberInGenericType
internal class SteamCallback<T> where T : struct
{
    static SteamCallback()
    {
        Callback<T>.Create(SafeCallback);

        return;

        void SafeCallback(T callbackStruct)
        {
            try {
                OnEvent?.Invoke(callbackStruct);
            } catch (Exception ex) {
                EmpLog.Verbose(ex, "Exception at steam callback {CallbackName}",
                    typeof(T).FullName);
                // noexcept
            }
        }
    }

    private static event Action<T>? OnEvent;

    internal static void Add(Action<T> handler)
    {
        lock (SteamCallback.EventLock) {
            OnEvent -= handler;
            OnEvent += handler;
        }
    }

    internal static void Remove(Action<T> handler)
    {
        lock (SteamCallback.EventLock) {
            OnEvent -= handler;
        }
    }

    internal static void Clear()
    {
        lock (SteamCallback.EventLock) {
            OnEvent = null;
        }
    }
}