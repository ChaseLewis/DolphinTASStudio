namespace Skies;

/// <summary>Guest addresses from SkiesScanner and SoAManips; revision-specific.</summary>
public static class SkiesAddresses
{
    public const uint RngSeed = 0x803469A8;
    public const uint BattleState = 0x8034733F;
    public const uint FrameCounter = 0x8034768C;
    public const uint InventoryPointer = 0x80347390;
    public const uint AreaId = 0x80311AC4;
    public const uint AreaSubId = 0x80311AC8;
    public const uint EncounterId = 0x803097F2;
    public const uint StageId = 0x803097F4;
    public const uint CharacterBattlePointers = 0x80309DE4;
    public const uint EnemyBattlePointers = 0x80309DF4;
    public const uint EnemyHealthOffset = 0x14;
    public const uint CharacterQuickOffset = 0x88;
    public const uint CharacterHitOffset = 0x9A;
    public const uint FirstStrikeChance = 0x8030B7A9;
    public const uint BackAttackChance = 0x8030B7AA;
    public const uint EncounterModifier = 0x8030B7AD;
    public const uint EscapeChanceOffset = 0x02;
    public const uint ShipBattlePointer = 0x8034727C;
    public const uint PlayerShipChainOffset = 0x20;
    public const uint EnemyShipChainOffset = 0x24;
    public const uint ShipMaxHpOffset = 0x14;
    public const uint ShipHpOffset = 0x18;
}
