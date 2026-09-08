namespace Skies;

public enum BattleState : byte
{
    Initializing = 0,
    ActionSelect = 1,
    FinishActionSelect = 2,
    CameraTransition = 3,
    PlayAction = 4,
    WaitForAnimFinish = 5,
    TurnEnd = 6,
    Victory = 7,
    GameOver = 8,
    Escape = 9,
    TryAgain = 13,
    Unknown = byte.MaxValue
}
