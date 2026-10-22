namespace DataManager.Core.Editing;

public interface IEditCommand
{
    string Description { get; }
    void Undo();
    void Redo();
}

public sealed class DelegateEditCommand(string description, Action undo, Action redo) : IEditCommand
{
    public string Description { get; } = description;
    public void Undo() => undo();
    public void Redo() => redo();
}

public sealed class EditCommandManager
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();
    private bool _isApplying;
    private CommandGroup? _group;

    public bool IsApplying => _isApplying;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(IEditCommand command)
    {
        if (_isApplying) return;
        if (_group != null) { _group.Commands.Add(command); return; }
        _undo.Push(command);
        _redo.Clear();
    }

    public string? Undo()
    {
        if (_undo.Count == 0) return null;
        var command = _undo.Peek();
        _isApplying = true;
        try { command.Undo(); _undo.Pop(); _redo.Push(command); }
        finally { _isApplying = false; }
        return command.Description;
    }

    public string? Redo()
    {
        if (_redo.Count == 0) return null;
        var command = _redo.Peek();
        _isApplying = true;
        try { command.Redo(); _redo.Pop(); _undo.Push(command); }
        finally { _isApplying = false; }
        return command.Description;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public CommandGroup BeginGroup(string description)
    {
        if (_group != null || _isApplying) throw new InvalidOperationException("不能嵌套草稿命令组。");
        return _group = new CommandGroup(this, description);
    }

    public sealed class CommandGroup : IDisposable
    {
        private readonly EditCommandManager _owner;
        private readonly string _description;
        internal readonly List<IEditCommand> Commands = [];
        private bool _complete, _disposed;
        internal CommandGroup(EditCommandManager owner, string description) { _owner = owner; _description = description; }
        public void Complete() => _complete = true;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _owner._group = null;
            if (!_complete)
            {
                _owner._isApplying = true;
                try { for (var i = Commands.Count - 1; i >= 0; i--) Commands[i].Undo(); }
                finally { _owner._isApplying = false; }
                return;
            }
            if (Commands.Count == 0) return;
            var commands = Commands.ToArray();
            _owner.Push(new DelegateEditCommand(_description,
                () => { for (var i = commands.Length - 1; i >= 0; i--) commands[i].Undo(); },
                () => { foreach (var command in commands) command.Redo(); }));
        }
    }
}
