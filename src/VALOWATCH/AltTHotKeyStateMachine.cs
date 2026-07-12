namespace VALOWATCH;

internal sealed class AltTHotKeyStateMachine
{
    private bool altKeyIsDown;
    private bool tKeyIsDown;

    public bool Process(uint virtualKeyCode, bool keyDown, bool keyUp, bool altIsCurrentlyDown)
    {
        bool isAltKey = virtualKeyCode is
            NativeMethods.VirtualKeyMenu or
            NativeMethods.VirtualKeyLeftMenu or
            NativeMethods.VirtualKeyRightMenu;
        if (isAltKey)
        {
            if (keyDown)
            {
                altKeyIsDown = true;
            }
            else if (keyUp)
            {
                altKeyIsDown = false;
            }
        }

        if (virtualKeyCode != NativeMethods.VirtualKeyT)
        {
            return false;
        }

        if (keyUp)
        {
            tKeyIsDown = false;
            return false;
        }

        if (!keyDown || tKeyIsDown || (!altKeyIsDown && !altIsCurrentlyDown))
        {
            return false;
        }

        tKeyIsDown = true;
        return true;
    }
}
