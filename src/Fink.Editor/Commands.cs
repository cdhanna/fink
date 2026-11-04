namespace Fink.Editor;

/// <summary>
/// One thing the editor can do. Hotkeys, menus, buttons, the command palette and automation
/// scripts all run commands by id, so each action exists in exactly one place.
/// </summary>
public sealed class Command
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public string Description { get; init; } = "";
    public required Action Execute { get; init; }
    public Func<bool>? IsEnabled { get; init; }
    /// <summary>For toggles: shown as a check mark in menus.</summary>
    public Func<bool>? IsChecked { get; init; }
    /// <summary>Fires again while the key is held (frame stepping, zoom, undo).</summary>
    public bool Repeat { get; init; }
    /// <summary>
    /// Hold-style command: <see cref="Execute"/> runs on key down and this on key up, e.g. hold
    /// Alt for the eyedropper, release to get the previous tool back.
    /// </summary>
    public Action? OnRelease { get; init; }
    public IReadOnlyList<Binding> DefaultBindings { get; init; } = Array.Empty<Binding>();

    public bool Enabled => IsEnabled?.Invoke() ?? true;
    public bool Checked => IsChecked?.Invoke() ?? false;
    public bool IsHold => OnRelease != null;
}

public sealed class CommandRegistry
{
    private readonly Dictionary<string, Command> _byId = new();
    private readonly List<Command> _ordered = new();

    public IReadOnlyList<Command> All => _ordered;

    public Command Register(Command command)
    {
        if (!_byId.TryAdd(command.Id, command)) throw new InvalidOperationException($"Duplicate command id {command.Id}");
        _ordered.Add(command);
        return command;
    }

    public Command? Find(string id) => _byId.GetValueOrDefault(id);

    public Command Get(string id) => _byId.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"No command \"{id}\"");

    /// <summary>Runs a command if it exists and is enabled. Returns whether it ran.</summary>
    public bool Run(string id)
    {
        var c = Find(id);
        if (c == null || !c.Enabled) return false;
        c.Execute();
        return true;
    }

    public IEnumerable<string> Categories => _ordered.Select(c => c.Category).Distinct();
}
