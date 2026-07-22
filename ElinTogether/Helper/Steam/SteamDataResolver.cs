using HeathenEngineering.SteamworksIntegration;
using MessagePack;
using MessagePack.Formatters;

namespace ElinTogether.Helper.Steam;

public sealed class SteamDataResolver : IFormatterResolver
{
    private SteamDataResolver() { }
    public static IFormatterResolver Default => field ??= new SteamDataResolver();

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        if (typeof(T) == typeof(UserData)) {
            return SteamUserDataFormatter.Formatter as IMessagePackFormatter<T>;
        }
        if (typeof(T) == typeof(LobbyData)) {
            return SteamLobbyDataFormatter.Formatter as IMessagePackFormatter<T>;
        }
        return null;
    }

    public class SteamUserDataFormatter : IMessagePackFormatter<UserData>
    {
        public static SteamUserDataFormatter Formatter => field ??= new();

        public void Serialize(ref MessagePackWriter writer, UserData value, MessagePackSerializerOptions options)
        {
            writer.Write(value);
        }

        public UserData Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            return reader.ReadUInt64();
        }
    }

    public class SteamLobbyDataFormatter : IMessagePackFormatter<LobbyData>
    {
        public static SteamLobbyDataFormatter Formatter => field ??= new();

        public void Serialize(ref MessagePackWriter writer, LobbyData value, MessagePackSerializerOptions options)
        {
            writer.Write(value);
        }

        public LobbyData Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            return reader.ReadUInt64();
        }
    }
}