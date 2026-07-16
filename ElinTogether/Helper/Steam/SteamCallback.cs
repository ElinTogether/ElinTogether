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
internal class SteamCallback<T> : IDisposable where T : struct
{
    private static Callback<T>? _callback;
    private static bool _shutdown;

    static SteamCallback()
    {
        _callback = Callback<T>.Create(SafeCallback);

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

    public void Dispose()
    {
        Shutdown();
    }

    private static event Action<T>? OnEvent;

    internal static void Add(Action<T> handler)
    {
        lock (SteamCallback.EventLock) {
            if (_shutdown) {
                return;
            }

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

    internal static void Shutdown()
    {
        lock (SteamCallback.EventLock) {
            if (_shutdown) {
                return;
            }

            _shutdown = true;
            OnEvent = null;

            _callback?.Dispose();
            _callback = null;
        }
    }
}