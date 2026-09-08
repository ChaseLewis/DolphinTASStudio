namespace Skies;

public readonly record struct Area(int FileId, char SubId);
public readonly record struct EncounterIdentifier(Area Area, short StageId, short EncounterId);

/// <summary>A candidate encounter snapshot using SoAManips' pointer and HP checks.</summary>
public sealed class EnemyEncounter
{
    internal EnemyEncounter(EncounterIdentifier encounterId, IEnumerable<EnemyStats> enemies)
    {
        EncounterId = encounterId;
        Enemies = Array.AsReadOnly(enemies.ToArray());
    }

    public EncounterIdentifier EncounterId { get; }
    public IReadOnlyList<EnemyStats> Enemies { get; }
    public bool IsAtFullHealth => Enemies.All(enemy => enemy.IsAtFullHealth);

    /// <summary>Matches area, encounter/stage IDs, enemy count and ordered maximum HP;
    /// current HP can change during the battle.</summary>
    public bool IsSameEncounter(EnemyEncounter? other) => other is not null &&
        EncounterId == other.EncounterId && Enemies.Count == other.Enemies.Count &&
        Enemies.Zip(other.Enemies).All(pair => pair.First.MaxHp == pair.Second.MaxHp);
}
