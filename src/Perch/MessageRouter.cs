using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Perch;

/// Single dispatch table for page → host messages. Both entry points — the
/// WebView2 bridge (OnWebMessage) and the test-harness control pipe
/// (OnControlVerb) — route through the SAME table, so a verb cannot drift
/// between the two paths (the old duplicated switches disagreed on
/// "pane.moveDir" vs "pane.move-dir" and on handler flags).
///
/// Payloads deserialize into the typed records in PageMessages.cs right here;
/// this is the one boundary where a wire/DTO mismatch surfaces. Handlers never
/// see raw JsonElements.
internal sealed class MessageRouter
{
    private readonly Dictionary<string, Action<JsonElement>> _routes = new(StringComparer.Ordinal);

    /// Register a payload-less message.
    public MessageRouter Add(string type, Action handler)
    {
        _routes[type] = _ => handler();
        return this;
    }

    /// Register a message whose payload deserializes to T.
    public MessageRouter Add<T>(string type, Action<T> handler)
    {
        _routes[type] = root => handler(PageJson.Deserialize<T>(root));
        return this;
    }

    /// Dispatch one message. Returns false when the type isn't registered
    /// (caller logs the unknown). A payload that doesn't fit its DTO throws
    /// JsonException out of here — callers catch and log it WITH the payload,
    /// so a protocol mismatch is loud instead of a silent no-op.
    public bool Dispatch(string type, JsonElement root)
    {
        if (!_routes.TryGetValue(type, out var handler)) return false;
        handler(root);
        return true;
    }
}
