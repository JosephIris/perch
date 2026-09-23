using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// The email inbox: emails you label `claude` in Gmail, exported by an Apps
/// Script into a Drive folder (see InboxModel), shown in Perch with a state
/// each — new, read, pending, done — and a one-click session that puts the
/// email beside a Claude primed to deal with it.
///
/// Off unless Settings.InboxEnabled. It reads Drive as a service account
/// whose JSON key comes from a command (Settings.InboxKeyCommand), so no
/// secret is ever stored by Perch.
///
/// Where things live:
///   Drive  <folder>/perch-state.json  — the states, shared by every PC
///   local  <data>/perch/inbox/<threadId>/  — a copy of each thread
///          (thread.md + attachments) for the email pane and for Claude
///   local  <data>/perch/inbox/state.json  — last known states, so the list
///          works offline and an edit made offline is pushed on next sync
///   local  <data>/perch/inbox/links.json  — thread → the tab opened for it;
///          tabs are per PC, so this is deliberately not shared
internal sealed class InboxController : IDisposable
{
    private readonly IUiThread _ui;
    private readonly Settings _settings;
    private readonly Action<object> _push;
    private readonly Func<Guid, bool> _sessionAlive;

    private readonly object _gate = new();
    private InboxModel.StateFile _state;
    private Dictionary<string, Guid> _links;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private IUiTimer? _timer;
    private bool _syncing;
    private bool _disposed;
    private DriveClient? _client;
    private string _clientKeyCommand = "";
    private string? _stateFileId;
    private string _status = "idle";
    private string _message = "";
    private DateTimeOffset? _lastSync;

    /// Threads found in the last sync, id → (folder name, thread.md modified).
    private Dictionary<string, (string Folder, DateTimeOffset Modified)> _threads = new();

    /// Once a minute: the Gmail export also runs every minute, so a labelled
    /// email shows up within about two. A quiet sync is three small Drive
    /// calls, well inside Drive's free quota.
    private const int SyncMinutes = 1;
    /// A single attachment above this is listed but not copied down.
    private const long MaxAttachmentBytes = 25L * 1024 * 1024;

    public static string Root => Path.Combine(AppPaths.DataRoot, "perch", "inbox");
    public static string ThreadDir(string threadId) => Path.Combine(Root, SafeName(threadId));
    private static string StatePath => Path.Combine(Root, "state.json");
    private static string LinksPath => Path.Combine(Root, "links.json");

    public InboxController(IUiThread ui, Settings settings, Action<object> push, Func<Guid, bool> sessionAlive)
    {
        _ui = ui;
        _settings = settings;
        _push = push;
        _sessionAlive = sessionAlive;
        _state = InboxModel.ParseState(ReadOrNull(StatePath));
        _links = LoadLinks();
    }

    public bool Enabled => _settings.InboxEnabled && !string.IsNullOrWhiteSpace(_settings.InboxDriveFolderId);

    public void Start()
    {
        _timer = _ui.CreateTimer(TimeSpan.FromMinutes(SyncMinutes), () => _ = SyncAsync());
        _timer.Start();
        _ = SyncAsync();
    }

    /// Settings changed: drop the cached client (the key command may be new)
    /// and sync straight away, or clear the view when switched off.
    public void OnSettingsChanged()
    {
        _client = null;
        _stateFileId = null;
        if (Enabled) _ = SyncAsync();
        else { _status = "idle"; _message = ""; PushView(); }
    }

    // ---- sync -------------------------------------------------------------

    public async Task SyncAsync()
    {
        if (_disposed || _syncing) return;
        if (!Enabled) { PushView(); return; }
        _syncing = true;
        _status = "syncing";
        _message = "";
        PushView();
        var folderId = _settings.InboxDriveFolderId.Trim();
        var keyCommand = _settings.InboxKeyCommand.Trim();
        try
        {
            var client = await ClientAsync(keyCommand);
            var found = await Task.Run(() => PullAsync(client, folderId));
            lock (_gate) _threads = found;
            await PushStateAsync(client, pullFirst: true);
            _status = "ok";
            if (_stateFileId == null)
                _message = "States are only saved on this PC. Create the state file in the Drive folder to share them across PCs.";
            _lastSync = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _status = "error";
            _message = ex is DriveException de && de.Status == 404
                ? "Drive can't find that folder — check the folder id, and that it's shared with the service account."
                : ex.Message;
            Log.Info("Inbox.sync", $"failed: {ex.Message}");
        }
        finally
        {
            _syncing = false;
            PushView();
        }
    }

    private async Task<DriveClient> ClientAsync(string keyCommand)
    {
        if (_client != null && _clientKeyCommand == keyCommand) return _client;
        if (keyCommand.Length == 0) throw new InvalidOperationException("Set the key command in Settings → Inbox.");
        var (code, stdout, stderr) = OperatingSystem.IsWindows()
            ? await ProcRunner.RunAsync("cmd.exe", $"/c {keyCommand}", "inbox.key", timeoutMs: 30000)
            : await ProcRunner.RunAsync("/bin/sh", "", "inbox.key", timeoutMs: 30000, argumentList: new[] { "-c", keyCommand });
        if (code != 0) throw new InvalidOperationException("The key command failed: " + FirstLine(stderr.Length > 0 ? stderr : stdout));
        try { _client = DriveClient.FromKeyJson(stdout.Trim()); }
        catch (Exception ex) { throw new InvalidOperationException("The key command didn't print a service-account key: " + ex.Message); }
        _clientKeyCommand = keyCommand;
        return _client;
    }

    /// Copy every exported thread that changed since the last sync down to
    /// the local cache. Three Drive calls when nothing changed: the root
    /// listing, one query for every thread.md, and the state file.
    private async Task<Dictionary<string, (string, DateTimeOffset)>> PullAsync(DriveClient client, string folderId)
    {
        var ct = CancellationToken.None;
        var children = await client.ListChildrenAsync(folderId, ct);
        _stateFileId = children.FirstOrDefault(f => !f.IsFolder && f.Name == InboxModel.StateFileName)?.Id;

        var folders = children.Where(f => f.IsFolder)
            .Select(f => (f, tid: InboxModel.ThreadIdFromFolder(f.Name)))
            .Where(x => x.tid != null)
            .ToDictionary(x => x.f.Id, x => (x.f, tid: x.tid!));
        var mds = await client.QueryAsync($"name = '{InboxModel.ThreadFileName}'", ct);

        var found = new Dictionary<string, (string, DateTimeOffset)>();
        foreach (var md in mds)
        {
            var parent = md.Parents.FirstOrDefault(folders.ContainsKey);
            if (parent == null) continue;
            var (folder, tid) = folders[parent];
            found[tid] = (folder.Name, md.Modified);

            var dir = ThreadDir(tid);
            var meta = Path.Combine(dir, ".perch-meta");
            var stamp = $"{md.Id} {md.Modified:O}";
            if (File.Exists(Path.Combine(dir, InboxModel.ThreadFileName)) && ReadOrNull(meta) == stamp) continue;

            Directory.CreateDirectory(dir);
            foreach (var f in await client.ListChildrenAsync(folder.Id, ct))
            {
                if (f.IsFolder) continue;
                var local = Path.Combine(dir, SafeName(f.Name));
                if (f.Name == InboxModel.ThreadFileName)
                {
                    File.WriteAllBytes(local, await client.DownloadAsync(f.Id, ct));
                    continue;
                }
                if (f.Size > MaxAttachmentBytes) continue;
                if (File.Exists(local) && new FileInfo(local).Length == f.Size) continue;
                File.WriteAllBytes(local, await client.DownloadAsync(f.Id, ct));
            }
            File.WriteAllText(meta, stamp);
            Log.Info("Inbox.pull", $"thread={tid}");
        }
        return found;
    }

    /// Merge the Drive copy of the states with ours and write the result to
    /// both. One writer at a time; each write re-reads first so a change made
    /// on another PC since our last look isn't overwritten.
    private async Task PushStateAsync(DriveClient client, bool pullFirst)
    {
        await _writeLock.WaitAsync();
        try
        {
            InboxModel.StateFile local;
            lock (_gate) local = _state;
            var merged = local;
            string? remoteJson = null;
            if (_stateFileId != null && pullFirst)
            {
                remoteJson = Encoding.UTF8.GetString(await client.DownloadAsync(_stateFileId, CancellationToken.None));
                merged = InboxModel.Merge(InboxModel.ParseState(remoteJson), local);
            }
            var json = InboxModel.SerializeState(merged);
            lock (_gate) _state = InboxModel.Merge(_state, merged);
            SaveLocal(StatePath, json);
            if (_stateFileId != null && NormalizeJson(remoteJson) != NormalizeJson(json))
            {
                try
                {
                    await client.UpdateContentAsync(_stateFileId, Encoding.UTF8.GetBytes(json), "application/json", CancellationToken.None);
                }
                catch (DriveException ex) when (ex.Status == 403)
                {
                    _message = "Can't save states to Drive: share the inbox folder with the service account as Editor.";
                    Log.Info("Inbox.state.push", ex.Message);
                }
            }
        }
        finally { _writeLock.Release(); }
    }

    /// The "Create state file" button: make perch-state.json in the folder so
    /// states are shared. Needs the folder shared as Editor, and Drive may
    /// still refuse a service account a new file in someone's My Drive — both
    /// come back as a plain instruction instead of an error code.
    public async Task CreateStateFileAsync()
    {
        if (!Enabled || _stateFileId != null) return;
        try
        {
            var client = await ClientAsync(_settings.InboxKeyCommand.Trim());
            InboxModel.StateFile snapshot;
            lock (_gate) snapshot = _state;
            _stateFileId = await client.CreateFileAsync(_settings.InboxDriveFolderId.Trim(), InboxModel.StateFileName,
                Encoding.UTF8.GetBytes(InboxModel.SerializeState(snapshot)), "application/json", CancellationToken.None);
            _message = "";
            Log.Info("Inbox.stateFile", "created");
        }
        catch (DriveException ex) when (ex.Status == 403 && ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            _message = $"Google won't let the service account create files in your Drive. The Gmail inbox script creates {InboxModel.StateFileName} for you on its next run (update it to the latest version), or upload an empty one to the folder yourself.";
        }
        catch (DriveException ex) when (ex.Status is 403 or 404)
        {
            _message = "The service account can only view this folder. Share it with the service account as Editor, then try again.";
        }
        catch (Exception ex)
        {
            _message = "Couldn't create the state file: " + ex.Message;
        }
        PushView();
    }

    // ---- page requests ----------------------------------------------------

    public void SetState(string threadId, string state)
    {
        if (!InboxModel.States.Contains(state)) return;
        lock (_gate)
        {
            // Copy-on-write: a push in flight holds the old object.
            var next = InboxModel.Merge(_state, new InboxModel.StateFile());
            next.Threads[threadId] = new InboxModel.Entry { State = state, UpdatedAt = DateTimeOffset.UtcNow, By = Environment.MachineName };
            _state = next;
        }
        SaveLocal(StatePath, InboxModel.SerializeState(_state));
        PushView();
        var client = _client;
        if (client != null && Enabled)
            _ = Task.Run(async () =>
            {
                try { await PushStateAsync(client, pullFirst: true); }
                catch (Exception ex) { Log.Info("Inbox.state.push", ex.Message); }
                _ui.Post(PushView);
            });
    }

    /// The first open of a new thread is its read receipt.
    public void MarkReadIfNew(string threadId)
    {
        if (CurrentState(threadId) == "new") SetState(threadId, "read");
    }

    public string CurrentState(string threadId)
    {
        lock (_gate)
        {
            _state.Threads.TryGetValue(threadId, out var e);
            var mod = _threads.TryGetValue(threadId, out var t) ? t.Modified : DateTimeOffset.MinValue;
            return InboxModel.Effective(e, mod);
        }
    }

    public Guid? LinkedSession(string threadId)
    {
        lock (_gate)
            return _links.TryGetValue(threadId, out var id) && _sessionAlive(id) ? id : null;
    }

    public void Link(string threadId, Guid sessionId)
    {
        lock (_gate) _links[threadId] = sessionId;
        try { SaveLocal(LinksPath, JsonSerializer.Serialize(_links)); } catch (Exception ex) { Log.Error("Inbox.links", ex); }
        PushView();
    }

    public InboxModel.Thread? LoadThread(string threadId)
    {
        var text = ReadOrNull(Path.Combine(ThreadDir(threadId), InboxModel.ThreadFileName));
        return text == null ? null : InboxModel.ParseThread(text);
    }

    public void PostMail(Guid paneId, string threadId)
    {
        var t = LoadThread(threadId);
        var dir = ThreadDir(threadId);
        _push(new
        {
            type = "inbox.mail",
            paneId = paneId.ToString("D"),
            threadId,
            found = t != null,
            subject = t?.Subject ?? "",
            state = CurrentState(threadId),
            messages = (t?.Messages ?? Array.Empty<InboxModel.Message>()).Select(m => new
            {
                from = m.From,
                to = m.To,
                cc = m.Cc,
                date = m.Date?.ToString("O") ?? "",
                body = m.Body,
                attachments = m.Attachments.Select(a => new
                {
                    name = a,
                    isImage = InboxModel.IsImage(a),
                    present = File.Exists(Path.Combine(dir, SafeName(a))),
                }).ToArray(),
            }).ToArray(),
        });
    }

    public void PostImage(Guid paneId, string threadId, string name)
    {
        try
        {
            var path = Path.Combine(ThreadDir(threadId), SafeName(name));
            if (!File.Exists(path) || new FileInfo(path).Length > 12L * 1024 * 1024) return;
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = ext == "jpg" ? "image/jpeg" : "image/" + ext;
            _push(new
            {
                type = "inbox.image",
                paneId = paneId.ToString("D"),
                name,
                dataUrl = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}",
            });
        }
        catch (Exception ex) { Log.Error("Inbox.image", ex); }
    }

    public string? AttachmentPath(string threadId, string name)
    {
        var path = Path.Combine(ThreadDir(threadId), SafeName(name));
        return File.Exists(path) ? path : null;
    }

    /// The whole list, newest first. Only threads the last sync saw on Drive;
    /// a thread deleted from Drive leaves the list (its local copy stays, so
    /// an open email pane keeps working).
    /// Call on the UI thread (from elsewhere, go through _ui.Post).
    public void PushView()
    {
        var items = new List<object>();
        var counts = new Dictionary<string, int> { ["new"] = 0, ["read"] = 0, ["pending"] = 0, ["done"] = 0 };
        if (Enabled)
        {
            Dictionary<string, (string Folder, DateTimeOffset Modified)> threads;
            lock (_gate) threads = new(_threads);
            var rows = new List<(DateTimeOffset Sort, object Row)>();
            foreach (var (tid, info) in threads)
            {
                var t = LoadThread(tid);
                if (t == null) continue;
                var last = t.Messages.LastOrDefault();
                var state = CurrentState(tid);
                counts[state]++;
                var date = last?.Date ?? info.Modified;
                rows.Add((date, new
                {
                    id = tid,
                    subject = t.Subject.Length > 0 ? t.Subject : "(no subject)",
                    from = InboxModel.DisplayName(last?.From ?? ""),
                    date = date.ToString("O"),
                    messageCount = t.Messages.Count,
                    attachmentCount = t.Messages.Sum(m => m.Attachments.Count),
                    snippet = InboxModel.Snippet(t),
                    state,
                    sessionId = LinkedSession(tid)?.ToString("D"),
                }));
            }
            items = rows.OrderByDescending(r => r.Sort).Select(r => r.Row).ToList();
        }
        _push(new
        {
            type = "inbox.state",
            enabled = Enabled,
            status = _status,
            message = _message,
            lastSync = _lastSync?.ToString("O"),
            shared = _stateFileId != null,
            counts,
            items,
        });
    }

    // ---- helpers ------------------------------------------------------------

    /// Drive names can hold anything; a local file name can't. The export
    /// already strips most of it, this catches the rest.
    internal static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        var s = new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 || s == "." || s == ".." ? "_" : s;
    }

    private static string? ReadOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void SaveLocal(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, text);
    }

    private static Dictionary<string, Guid> LoadLinks()
    {
        try
        {
            var json = ReadOrNull(LinksPath);
            if (json != null)
                return JsonSerializer.Deserialize<Dictionary<string, Guid>>(json) ?? new();
        }
        catch (Exception ex) { Log.Error("Inbox.links.load", ex); }
        return new();
    }

    private static string NormalizeJson(string? json)
        => json == null ? "" : InboxModel.SerializeState(InboxModel.ParseState(json));

    private static string FirstLine(string s)
    {
        var l = (s ?? "").Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        return l.Length > 200 ? l[..200] : l;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _client?.Dispose();
    }
}
