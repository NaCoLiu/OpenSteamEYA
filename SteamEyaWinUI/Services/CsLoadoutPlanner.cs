using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal readonly record struct CsLoadoutEntry(uint ClassId, uint SlotId, uint ItemDefinition);

internal readonly record struct CsR8EquipPlan(CsLoadoutTeamStatus Status, uint? SlotId);

internal static class CsLoadoutPlanner
{
    public static CsR8EquipPlan PlanR8Slot(uint teamClassId, IReadOnlyList<CsLoadoutEntry> entries)
    {
        var teamSecondary = entries
            .Where(entry => entry.ClassId == teamClassId && IsSecondarySlot(entry.SlotId))
            .ToList();

        if (teamSecondary.Any(entry => entry.ItemDefinition == CsLoadoutConstants.ItemDefinitionRevolver))
        {
            return new CsR8EquipPlan(CsLoadoutTeamStatus.AlreadyEquipped, null);
        }

        var deagleSlot = teamSecondary
            .FirstOrDefault(entry => entry.ItemDefinition == CsLoadoutConstants.ItemDefinitionDeagle);

        if (deagleSlot != default)
        {
            return new CsR8EquipPlan(CsLoadoutTeamStatus.Equipped, deagleSlot.SlotId);
        }

        if (teamSecondary.Count == 0)
        {
            return new CsR8EquipPlan(
                CsLoadoutTeamStatus.Equipped,
                CsLoadoutConstants.SecondarySlotDeagleDefault);
        }

        foreach (var slotId in CsLoadoutConstants.SecondarySlots)
        {
            if (!teamSecondary.Any(entry => entry.SlotId == slotId))
            {
                return new CsR8EquipPlan(CsLoadoutTeamStatus.Equipped, slotId);
            }
        }

        return new CsR8EquipPlan(CsLoadoutTeamStatus.NoFreeSlot, null);
    }

    public static bool HasR8Equipped(uint teamClassId, IReadOnlyList<CsLoadoutEntry> entries) =>
        entries.Any(entry =>
            entry.ClassId == teamClassId &&
            IsSecondarySlot(entry.SlotId) &&
            entry.ItemDefinition == CsLoadoutConstants.ItemDefinitionRevolver);

    private static bool IsSecondarySlot(uint slotId) =>
        slotId is >= 3 and <= 7;
}
