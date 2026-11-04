using System.Text.Json;

namespace Fink.Editor;

/// <summary>
/// The bindings in effect: each command's defaults, unless the user changed them. Only the changed
/// commands are saved, so new defaults still reach a user who customized other things.
/// </summary>
public sealed class Keymap
{
    private readonly CommandRegistry _commands;
    private readonly Dictionary<string, List<Binding>> _overrides = new();

    public Keymap(CommandRegistry commands) => _commands = commands;

    /// <summary>Bumps on every change, so the dispatcher can rebuild its lookup table.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<Binding> BindingsOf(string commandId) =>
        _overrides.TryGetValue(commandId, out var list) ? list : _commands.Get(commandId).DefaultBindings;

    public bool IsCustomized(string commandId) => _overrides.ContainsKey(commandId);

    public void SetBindings(string commandId, IEnumerable<Binding> bindings)
    {
        var list = bindings.Distinct().ToList();
        if (list.SequenceEqual(_commands.Get(commandId).DefaultBindings)) _overrides.Remove(commandId);
        else _overrides[commandId] = list;
        Version++;
    }

    public void AddBinding(string commandId, Binding binding) =>
        SetBindings(commandId, BindingsOf(commandId).Append(binding));

    public void RemoveBinding(string commandId, Binding binding) =>
        SetBindings(commandId, BindingsOf(commandId).Where(b => b != binding));

    public void ReplaceBinding(string commandId, Binding old, Binding replacement) =>
        SetBindings(commandId, BindingsOf(commandId).Select(b => b == old ? replacement : b));

    public void Reset(string commandId)
    {
        if (_overrides.Remove(commandId)) Version++;
    }

    public void ResetAll()
    {
        _overrides.Clear();
        Version++;
    }

    /// <summary>Other commands bound to the same chord in the same scope.</summary>
    public IEnumerable<Command> Conflicts(string commandId, Binding binding) =>
        _commands.All.Where(c => c.Id != commandId && BindingsOf(c.Id).Contains(binding));

    /// <summary>The commands bound to a chord in a scope, in registration order.</summary>
    public IEnumerable<Command> CommandsFor(Binding binding) =>
        _commands.All.Where(c => BindingsOf(c.Id).Contains(binding));

    // ------------------------------------------------------------------ persistence

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(
        _overrides.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value.Select(b => b.Serialize()).ToList()),
        JsonOptions);

    /// <summary>Loads overrides; unknown commands and unparseable chords are skipped and reported.</summary>
    public List<string> LoadJson(string json)
    {
        var problems = new List<string>();
        var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new();
        _overrides.Clear();
        foreach (var (id, list) in data)
        {
            if (_commands.Find(id) == null) { problems.Add($"unknown command {id}"); continue; }
            var bindings = new List<Binding>();
            foreach (var s in list)
            {
                try { bindings.Add(Binding.Parse(s)); }
                catch (Exception) { problems.Add($"{id}: can't read \"{s}\""); }
            }
            _overrides[id] = bindings;
        }
        Version++;
        return problems;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }

    public List<string> Load(string path) => File.Exists(path) ? LoadJson(File.ReadAllText(path)) : new();
}
