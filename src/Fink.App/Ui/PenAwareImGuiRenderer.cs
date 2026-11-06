using Fade.MonoGame.Core;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Fink.App;

/// <summary>
/// The engine's ImGui renderer, except that a pen touching down with its barrel (side) button
/// held is a right-click everywhere: context menus, pinning frames, filling with the secondary
/// color. Many tablet drivers report the barrel button only on the pen's own messages, so to the
/// mouse it's a plain left click. The press stays a right-click until the pen lifts, even if the
/// button is let go first.
///
/// The mouse button is read straight from the hardware state, which can run ahead of the pen's
/// window messages: the button is down a frame before the pen-down message (the one that says
/// whether the barrel is held) has arrived. So while a pen is near the screen, a new press is
/// held back from ImGui until that message comes in (a frame or two, at most a few), and only
/// then let through as a left or a right press. A tap that's over before then still clicks.
/// </summary>
internal sealed class PenAwareImGuiRenderer : ImGuiRenderer
{
    private const int MaxWaitFrames = 4;

    private bool _leftWasDown, _asRight, _pending;
    private int _waited;

    public PenAwareImGuiRenderer(Game game) : base(game) { }

    /// <summary>A script's mouse (window coordinates, left button), used instead of the real one.</summary>
    public (System.Numerics.Vector2 Pos, bool Left)? ScriptMouse { get; set; }

    protected override void UpdateInput()
    {
        base.UpdateInput();
        if (ScriptMouse is { } sm)
        {
            var sio = ImGui.GetIO();
            sio.ConfigInputTrickleEventQueue = false;
            sio.AddMousePosEvent(sm.Pos.X, sm.Pos.Y);
            sio.AddMouseButtonEvent(0, sm.Left);
            sio.AddMouseButtonEvent(1, false);
            return;
        }
        bool left = Mouse.GetState().LeftButton == ButtonState.Pressed;
        var io = ImGui.GetIO();

        if (left && !_leftWasDown)
        {
            _asRight = false;
            _waited = 0;
            // a mouse, or a pen whose down message is already in: decide now
            _pending = PenInput.InRange;
            if (PenInput.Downs != PenInput.DownsSeen) Decide();
        }
        else if (_pending && (PenInput.Downs != PenInput.DownsSeen || ++_waited >= MaxWaitFrames || !left))
            Decide();
        _leftWasDown = left;
        // idle, pen lifted: any pen-downs so far belonged to other presses (a driver's own right-click...)
        if (!left && !_pending && !PenInput.Contact) PenInput.DownsSeen = PenInput.Downs;

        if (_pending)
        {
            // not yet known: ImGui sees nothing pressed
            io.ConfigInputTrickleEventQueue = false;
            io.AddMouseButtonEvent(0, false);
        }
        else if (_asRight && (left || io.MouseDown[1]))
        {
            // the base already queued "left down"; with trickling off, these later events win
            io.ConfigInputTrickleEventQueue = false;
            io.AddMouseButtonEvent(0, false);
            io.AddMouseButtonEvent(1, left);
        }
        else io.ConfigInputTrickleEventQueue = true;
        if (!left && !_pending) _asRight = false;
    }

    private void Decide()
    {
        bool fresh = PenInput.Downs != PenInput.DownsSeen;
        _asRight = fresh ? PenInput.BarrelAtDown || PenInput.Barrel : PenInput.Barrel;
        PenInput.DownsSeen = PenInput.Downs;
        bool held = Mouse.GetState().LeftButton == ButtonState.Pressed;
        bool wasPending = _pending;
        _pending = false;
        PenInput.LastPress = _asRight ? "right-click (barrel button)" : "left-click";
        if (wasPending && !held)
        {
            // a quick tap, over before we knew which button: replay it, down now and up next frame
            var io = ImGui.GetIO();
            io.ConfigInputTrickleEventQueue = true;
            io.AddMouseButtonEvent(_asRight ? 1 : 0, true);
            io.AddMouseButtonEvent(_asRight ? 1 : 0, false);
            _asRight = false;
        }
    }

    /// <summary>True when the current press is a barrel-button right-click.</summary>
    public bool PenRightClick => _asRight;
}
