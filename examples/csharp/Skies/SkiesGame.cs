using TasStudio.Sdk;

namespace Skies;

/// <summary>Read-only game components for the ROM revision used by SkiesScanner.</summary>
public sealed partial class SkiesGame
{
    public const ushort Electri = (ushort)UsableItem.ElectriBox;
    public const ushort Moonberry = (ushort)UsableItem.Moonberry;

    public SkiesGame(IExperimentEmulator emulator) : this(new GameCubeMemoryReader(emulator)) { }
    public SkiesGame(GameCubeMemoryReader memory) => Memory = memory ?? throw new ArgumentNullException(nameof(memory));

    /// <summary>Reusable typed address and pointer reads. No write or execution methods.</summary>
    public GameCubeMemoryReader Memory { get; }
    public Task<int> ReadRngSeedAsync() => Memory.ReadInt32Async(SkiesAddresses.RngSeed);
    /// <summary>The same seed bits as an unsigned value; prefer ReadRngSeedAsync for Rust parity.</summary>
    public Task<uint> ReadRngAsync() => Memory.ReadUInt32Async(SkiesAddresses.RngSeed);
    /// <summary>The game's own 64-bit counter, not Studio's frame-group or input-poll count.</summary>
    public Task<ulong> ReadFrameCounterAsync() => Memory.ReadUInt64Async(SkiesAddresses.FrameCounter);

    public async Task<BattleState> ReadBattleStateAsync()
    {
        var value = await Memory.ReadByteAsync(SkiesAddresses.BattleState);
        return value is <= 9 or 13 ? (BattleState)value : BattleState.Unknown;
    }

    /// <summary>Reads the eight-slot item table counted by the original SkiesScanner.</summary>
    public async Task<InventorySnapshot> ReadInventoryAsync()
    {
        var slots = await Memory.ReadPackedArray<InventorySlot>(SkiesAddresses.InventoryPointer, 8, 0x0e);
        return new InventorySnapshot(slots);
    }

    public async Task<int> ReadElectriCountAsync() => (await ReadInventoryAsync()).ElectriCount;
    public async Task<int> ReadMoonberryCountAsync() => (await ReadInventoryAsync()).MoonberryCount;
    public async Task<int> CountItemAsync(ushort item) => (await ReadInventoryAsync()).CountItem(item);
    /// <summary>Counts this item in the existing eight-slot battle-drop table, not owned inventory.</summary>
    public Task<int> CountItemAsync(UsableItem item) => CountItemAsync((ushort)item);
    /// <summary>Counts this item in the existing eight-slot battle-drop table, not owned inventory.</summary>
    public Task<int> CountItemAsync(ShipItem item) => CountItemAsync((ushort)item);
}
