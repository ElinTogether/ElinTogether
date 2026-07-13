using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Helper.String;
using Steamworks;

namespace ElinTogether.Net.Steam;

public class SteamNetConfig
{
    public static readonly long ConnectionKey = BuildVersionIntegrity.VersionStringToLong(ModInfo.BuildVersion);

    private readonly Dictionary<ESteamNetworkingConfigValue, SteamNetworkingConfigValue_t> _configs = [];

    public static SteamNetConfig Default => field ??= new SteamNetConfig()
        .Set(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_ConnectionUserData, ConnectionKey);

    public SteamNetworkingConfigValue_t[] Create()
    {
        return _configs.Values.ToArray();
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