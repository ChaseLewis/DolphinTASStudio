using System.Buffers.Binary;
using Skies;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class SkiesDecisionTests
{
    private sealed class Memory
    {
        public byte[] Bytes { get; } = new byte[0x1800000];
        public List<(uint Address, int Count)> Reads { get; } = [];
        public SkiesGame Game => new(new GameCubeMemoryReader((address, count) =>
        {
            Reads.Add((address, count));
            return Task.FromResult(Bytes.AsSpan((int)(address - 0x80000000), count).ToArray());
        }));
        public Span<byte> At(uint address, int count) => Bytes.AsSpan((int)(address - 0x80000000), count);
        public void Word(uint address, int value) => BinaryPrimitives.WriteInt32BigEndian(At(address, 4), value);
        public void Decision(int slot, int command, byte target, sbyte rule, short ability)
        {
            var address = SkiesAddresses.BattleDecisions + (uint)(slot * 32);
            Word(address, command);
            At(address + 4, 1)[0] = target;
            At(address + 5, 1)[0] = unchecked((byte)rule);
            BinaryPrimitives.WriteInt16BigEndian(At(address + 6, 2), ability);
        }
    }

    [Fact]
    public async Task DecodesEnemySlotAbilityTargetAndSignedFieldsWithoutConflatingVariants()
    {
        var m = new Memory();
        m.Decision(4, 12, 1, -1, 5);
        var d = await m.Game.ReadBattleDecisionAsync(4);
        Assert.Equal((0x803091F4u, 32), Assert.Single(m.Reads));
        Assert.True(d.IsEnemy);
        Assert.Equal(BattleCommand.SuperMove, d.Command);
        Assert.Equal(5, d.AbilityId);
        Assert.Equal(1, d.TargetSlot);
        Assert.Equal((sbyte)-1, d.TargetRule);
        Assert.Null(d.AttackVariant);
        m.Decision(4, 3, 255, 2, 1);
        var attack = await m.Game.ReadBattleDecisionAsync(4);
        Assert.Equal(1, attack.AttackVariant);
        Assert.Null(attack.AbilityId);
        Assert.Null(attack.TargetSlot);
        Assert.Equal((byte)255, attack.RawTargetSlot);
        Assert.Equal(12, d.RawCommand); // frozen snapshot
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)d.RawBytes)[0] = 1);
    }

    [Fact]
    public async Task UnknownCommandsAndInactiveSentinelsSurviveAndBadSlotsFailBeforeReading()
    {
        var m = new Memory();
        m.Decision(11, 0x12345678, 254, -128, -1);
        var d = await m.Game.ReadBattleDecisionAsync(11);
        Assert.Null(d.Command);
        Assert.Equal(0x12345678, d.RawCommand);
        Assert.Equal((short)-1, d.AbilityOrVariant);
        m.Decision(0, -1, 0, -1, -1);
        Assert.Equal(BattleCommand.None, (await m.Game.ReadBattleDecisionAsync(0)).Command);
        m.Reads.Clear();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadBattleDecisionAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadBattleDecisionAsync(12));
        Assert.Empty(m.Reads);
    }

    [Fact]
    public async Task PlanCopiesContiguousDecisionAndOrderDataAndFiltersAbsentEnemySlots()
    {
        var m = new Memory();
        m.At(SkiesAddresses.BattleState, 1)[0] = 3;
        m.Word(SkiesAddresses.CharacterBattlePointers, unchecked((int)0x80001000));
        m.Word(SkiesAddresses.EnemyBattlePointers, unchecked((int)0x80002000));
        m.Decision(4, 4, 4, -1, -1);
        m.Decision(5, 12, 0, 2, 5); // absent slot retains stale attack
        m.At(SkiesAddresses.BattleActionOrder, 12).Fill(255);
        m.At(SkiesAddresses.BattleActionOrder, 1)[0] = 0; // Guard is not queued
        var plan = await m.Game.ReadBattlePlanAsync();
        Assert.False(plan.PhaseChangedDuringRead);
        Assert.Equal(BattleState.CameraTransition, plan.PhaseAfter);
        Assert.Equal(new[] { 0, 4 }, plan.PresentActorSlots);
        Assert.Equal(4, Assert.Single(plan.EnemyDecisions).ActorSlot);
        Assert.Equal(new[] { 0 }, plan.Order.ActorSlots);
        Assert.True(plan.Order.IsWellFormed);
        Assert.Equal(12, plan.Decisions.Count);
        Assert.Contains((SkiesAddresses.BattleDecisions, 396), m.Reads);
    }

    [Theory]
    [InlineData(255, true, 0)]
    [InlineData(12, false, 0)]
    [InlineData(4, false, 1)]
    public async Task QueueHonorsTerminatorAndFlagsInvalidOrDuplicateSlots(byte first, bool valid, int count)
    {
        var m = new Memory();
        m.At(SkiesAddresses.BattleActionOrder, 12).Fill(255);
        m.At(SkiesAddresses.BattleActionOrder, 2)[0] = first;
        m.At(SkiesAddresses.BattleActionOrder, 2)[1] = 4;
        var order = await m.Game.ReadBattleActionOrderAsync();
        Assert.Equal(valid, order.IsWellFormed);
        Assert.Equal(count, order.ActorSlots.Count);
        Assert.Equal((byte)4, order.RawBytes[1]);
    }

    [Fact]
    public async Task PhaseChangesAreExposedInsteadOfClaimingAnAtomicPlan()
    {
        var reads = 0;
        var game = new SkiesGame(new GameCubeMemoryReader((address, count) =>
        {
            var bytes = new byte[count];
            if (address == SkiesAddresses.BattleState) bytes[0] = ++reads == 1 ? (byte)2 : (byte)3;
            return Task.FromResult(bytes);
        }));
        Assert.True((await game.ReadBattlePlanAsync()).PhaseChangedDuringRead);
    }

    private const string Header = "Entry ID,[Filter],[EC ID],[EC US Name],Type ID,Task ID,[Task Name],Param ID,[Param Name]\n";

    [Fact]
    public async Task CatalogReadsQuotedLabelsKeepsScriptVariantsAndMapsCommandPlusAbility()
    {
        var catalog = EnemyAiCatalog.Read(new StringReader(Header +
            "20,*,128,Antonio,1,5,Thunder of Fury,2,Random PC\n" +
            "1,*,128,Antonio,0,11,Turn 1,9,Go to 9\n" +
            "20,boss.evp,128,Antonio,1,5,Thunder of Fury,2,Random PC\n" +
            "2,*,129,Sentinel,1,505,\"Magic, with \"\"quotes\"\"\",2,Random PC\n"));
        Assert.Equal(new[] { 1, 20 }, catalog.GetScript(128).Select(i => i.EntryId));
        Assert.Single(catalog.GetScript(128, "boss.evp"));
        var m = new Memory();
        m.Decision(4, 12, 1, 2, 5);
        Assert.Equal("Thunder of Fury", catalog.Describe(await m.Game.ReadBattleDecisionAsync(4)));
        m.Decision(4, 1, 1, 2, 5);
        Assert.Equal("Magic, with \"quotes\"", catalog.Describe(await m.Game.ReadBattleDecisionAsync(4)));
        m.Decision(4, 12, 1, 2, 99);
        Assert.Equal("SuperMove 99", catalog.Describe(await m.Game.ReadBattleDecisionAsync(4)));
        m.Decision(4, 99, 1, 2, 5);
        Assert.Equal("Command 99", catalog.Describe(await m.Game.ReadBattleDecisionAsync(4)));
    }

    [Fact]
    public async Task CatalogDoesNotGuessConflictingNamesAndRejectsMalformedSchemaOrNumbers()
    {
        var catalog = EnemyAiCatalog.Read(new StringReader(Header +
            "1,*,1,One,1,5,First,2,Random PC\n1,*,2,Two,1,5,Second,2,Random PC\n"));
        var m = new Memory();
        m.Decision(4, 12, 0, 2, 5);
        Assert.Equal("SuperMove 5", catalog.Describe(await m.Game.ReadBattleDecisionAsync(4)));
        Assert.Throws<InvalidDataException>(() => EnemyAiCatalog.Read(new StringReader("wrong,headers\n")));
        Assert.Throws<InvalidDataException>(() => EnemyAiCatalog.Read(new StringReader(Header + "bad,*,1,One,1,5,Name,2,Random PC\n")));
    }
}
