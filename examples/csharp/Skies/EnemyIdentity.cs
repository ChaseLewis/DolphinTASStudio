namespace Skies;

/// <summary>One present enemy slot. EnemyIndex is 0–7; ActorSlot is 4–11.
/// Name is null for unknown IDs. A defeated enemy can remain present in battle memory.</summary>
public sealed record EnemyIdentity(int EnemyIndex, short EnemyId, string? Name, int Hp, int MaxHp)
{
    public int ActorSlot => SkiesGame.MaximumPartyMembers + EnemyIndex;
    public bool IsDefeated => Hp == 0;
}
