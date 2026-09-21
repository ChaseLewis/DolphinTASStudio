namespace Skies;

/// <summary>Guest addresses from SkiesScanner and SoAManips; revision-specific.</summary>
public static class SkiesAddresses
{
    public const uint RngSeed = 0x803469A8;
    public const uint BattleState = 0x8034733F;
    // GEAE8P-r0 decision/order layout recovered in SkiesDecomp/tools/battle_trace.
    public const uint BattleDecisions = 0x80309174;
    public const uint BattleActionOrder = 0x803092F4;
    public const uint FrameCounter = 0x8034768C;
    public const uint InventoryPointer = 0x80347390;
    public const uint OwnedUsableItems = 0x8030BF08;
    public const uint OwnedShipItems = 0x8030C188;
    public const uint AreaId = 0x80311AC4;
    public const uint AreaSubId = 0x80311AC8;
    public const uint EncounterId = 0x803097F2;
    public const uint StageId = 0x803097F4;
    public const uint CharacterBattlePointers = 0x80309DE4;
    public const uint BattleActorIds = 0x80309DCC;
    public const uint EnemyBattlePointers = 0x80309DF4;
    public const uint EnemyHealthOffset = 0x14;
    public const uint ActiveStatusOffset = 0x1C;
    public const uint PersistentStatusOffset = 0x20;
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
