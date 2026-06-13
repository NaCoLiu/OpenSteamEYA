namespace SteamEyaWinUI.Models;

internal static class CsLoadoutConstants
{
    public const uint AdjustEquipSlotsManual = 2531;

    public const uint SoCreate = 21;
    public const uint SoUpdate = 22;
    public const uint SoCacheSubscribed = 24;
    public const uint SoUpdateMultiple = 26;
    public const uint SoCacheSubscriptionRefresh = 28;
    public const int SoTypeEquipSlot = 3;
    public const int SoTypeDefaultEquippedDefinition = 43;
    public const uint SoOwnerTypeIndividual = 1;

    public const uint TeamTerrorist = 2;
    public const uint TeamCounterTerrorist = 3;

    public const ulong ItemIdDefaultItemMask = 0xF000000000000000;
    public const uint ItemDefinitionRevolver = 64;
    public const uint ItemDefinitionDeagle = 1;

    public const uint SecondarySlotDeagleDefault = 6;

    public static readonly uint[] SecondarySlots = [3, 4, 5, 6, 7];

    public static ulong BuildDefaultBaseItemId(uint itemDefinition) =>
        ItemIdDefaultItemMask | itemDefinition;
}
