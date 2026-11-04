using System.Collections.Generic;
using ElinTogether.Common;
using ElinTogether.Helper.String;
using Steamworks;

namespace ElinTogether.Net.Steam;

public class SteamNetConfig
{
    private readonly Dictionary<ESteamNetworkingConfigValue, SteamNetworkingConfigValue_t> _configs = [];

    /// <summary>
    ///     How long a connected link may stay silent before Steam gives it up (Steam's own is 10 s). A wifi that drops
    ///     for 10 s was a lost link: the guest's game went to the title and joined again, 70 s without playing
    ///     (net_fault_check N2, 2026-11-04). A silent link of that long now comes back as it was. A game that closes
    ///     says so and is let go at once; only one that vanishes without a word is waited for that long
    /// </summary>
    internal const int ConnectedTimeoutMs = 30000;

    // (the listen socket hands its settings to the links it accepts: both ends wait the same)
    public static SteamNetConfig Default => new SteamNetConfig()
        .Set(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_ConnectionUserData,
            BuildVersionIntegrity.VersionStringToLong())
        .Set(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, ConnectedTimeoutMs);

    public SteamNetworkingConfigValue_t[] Create()
    {
        return [.._configs.Values];
    }

    public SteamNetConfig Set(ESteamNetworkingConfigValue value, int data)
    {
        _configs[value] = new() {
            m_eValue = value,
            m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            m_val = new() {
                m_int32 = data,
            },
        };
        return this;
    }

    public SteamNetConfig Set(ESteamNetworkingConfigValue value, long data)
    {
        _configs[value] = new() {
            m_eValue = value,
            m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int64,
            m_val = new() {
                m_int64 = data,
            },
        };
        return this;
    }

    public SteamNetConfig Set(ESteamNetworkingConfigValue value, float data)
    {
        _configs[value] = new() {
            m_eValue = value,
            m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Float,
            m_val = new() {
                m_float = data,
            },
        };
        return this;
    }

    public SteamNetConfig Set(ESteamNetworkingConfigValue value, string data)
    {
        _configs[value] = new() {
            m_eValue = value,
            m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_String,
            m_val = new() {
                m_string = StringAllocator.Shared.Pin(data),
            },
        };
        return this;
    }

    public void Remove(ESteamNetworkingConfigValue value)
    {
        _configs.Remove(value);
    }

    public SteamNetworkingConfigValue_t? Get(ESteamNetworkingConfigValue value)
    {
        return _configs.GetValueOrDefault(value);
    }
}