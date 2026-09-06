using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch;

internal sealed class TeamDelivery
{
    public long Seq { get; set; }
    public string Bot { get; set; } = "";
    public string Line { get; set; } = "";
    public string State { get; set; } = "queued";
}

/// Separate from rotating chat history: unfinished delivery must survive
/// rotation and restart. A submitted-but-unconfirmed line is never replayed
/// automatically, since the agent may already have acted on it.
internal sealed class TeamOutbox
{
    private readonly string _path;
    public List<TeamDelivery> Items { get; }
    public bool Readable { get; private set; } = true;
    public TeamOutbox(string path)
    {
        _path = path;
        try
        {
            Items = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), OutboxJsonContext.Default.ListTeamDelivery)
                    ?? throw new JsonException("Outbox document is null")
                : new();
        }
        catch (Exception ex) { Readable = false; Items = new(); Log.Error("TeamOutbox.Load", ex); }
    }
    public void Put(long seq, string bot, string line, string state)
    {
        if (!Readable) throw new IOException($"Cannot send: unreadable outbox preserved at {_path}");
        if (seq <= 0) return;
        var item = Items.FirstOrDefault(x => x.Seq == seq && x.Bot == bot);
        if (item == null) Items.Add(item = new TeamDelivery { Seq = seq, Bot = bot });
        item.Line = line;
        item.State = state;
        Save();
    }
    public void Finish(long seq, string bot)
    {
        if (Items.RemoveAll(x => x.Seq == seq && x.Bot == bot) > 0) Save();
    }
    private void Save() => AtomicFile.WriteAllText(_path,
        JsonSerializer.Serialize(Items, OutboxJsonContext.Default.ListTeamDelivery));
}

[JsonSerializable(typeof(List<TeamDelivery>))]
internal partial class OutboxJsonContext : JsonSerializerContext { }
