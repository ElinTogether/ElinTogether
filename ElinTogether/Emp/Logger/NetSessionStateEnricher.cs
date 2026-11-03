using ElinTogether.Net;
using Serilog.Core;
using Serilog.Events;

namespace ElinTogether;

/// <summary>
///     The session a line was written in. Warning and up: the whole state (role, session, tick). Below: the role and
///     the session only, one property made again only when they change; every Debug line used to destructure the
///     session into a new structure (efficiency audit 2026-11-03). The bench reads NetSession.Role on Debug lines
/// </summary>
internal class NetSessionStateEnricher : ILogEventEnricher
{
    private sealed class Cached(string role, ulong sessionId, LogEventProperty property)
    {
        public readonly string Role = role;
        public readonly ulong SessionId = sessionId;
        public readonly LogEventProperty Property = property;
    }

    // (lines are written from several threads: a whole object swapped at once)
    private static volatile Cached? _cached;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var session = NetSession.Instance;
        if (!session.HasActiveConnection) {
            return;
        }

        if (logEvent.Level >= LogEventLevel.Warning) {
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("NetSession", session, true));
            return;
        }

        var role = session.IsAway ? "Away" : session.IsHost ? "Host" : "Client";
        var id = session.SessionId;
        var cached = _cached;
        if (cached is null || cached.SessionId != id || !string.Equals(cached.Role, role)) {
            cached = new(role, id, new("NetSession", new StructureValue([
                new LogEventProperty("Role", new ScalarValue(role)),
                new LogEventProperty("SessionId", new ScalarValue(id)),
            ])));
            _cached = cached;
        }

        logEvent.AddOrUpdateProperty(cached.Property);
    }
}
