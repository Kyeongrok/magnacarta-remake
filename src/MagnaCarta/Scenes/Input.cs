namespace MagnaCarta.Scenes;

/// <summary>한 틀 동안의 입력 — 누르고 있는 키, 막 누른 키, 마우스.</summary>
internal sealed class Input
{
    private readonly HashSet<int> _down = [], _pressed = [];

    public int MouseX, MouseY;
    public bool LeftClicked, RightDown;
    public int DragX, DragY;
    public float Wheel;

    public bool Down(int vk) => _down.Contains(vk);
    public bool Pressed(int vk) => _pressed.Contains(vk);

    public void KeyDown(int vk)
    {
        if (_down.Add(vk)) _pressed.Add(vk);
    }

    public void KeyUp(int vk) => _down.Remove(vk);

    public void ReleaseAll() { _down.Clear(); RightDown = false; }

    /// <summary>틀 끝에 막 누른 것들을 비운다.</summary>
    public void EndFrame()
    {
        _pressed.Clear();
        LeftClicked = false;
        DragX = DragY = 0;
        Wheel = 0;
    }

    public const int Left = 0x25, Up = 0x26, Right = 0x27, DownKey = 0x28, Enter = 0x0D, Escape = 0x1B, Shift = 0x10, Space = 0x20, F12 = 0x7B;
}

internal interface IScene : IDisposable
{
    /// <summary>다음 장면을 돌려주면 바꾼다. 자기 자신이면 그대로.</summary>
    IScene? Update(float seconds, Input input);
    void Draw();
}
