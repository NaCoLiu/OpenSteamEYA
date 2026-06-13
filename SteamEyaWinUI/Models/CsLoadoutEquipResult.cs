namespace SteamEyaWinUI.Models;

internal enum CsLoadoutTeamStatus
{
    Equipped,
    AlreadyEquipped,
    NoFreeSlot,
    Failed
}

internal sealed record CsLoadoutEquipResult(
    CsLoadoutTeamStatus TerroristStatus,
    CsLoadoutTeamStatus CounterTerroristStatus,
    string? ErrorMessage = null)
{
    public string FormatSummary()
    {
        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            return ErrorMessage;
        }

        return $"T 侧：{FormatTeamStatus(TerroristStatus)}；CT 侧：{FormatTeamStatus(CounterTerroristStatus)}。";
    }

    public bool IsSuccess =>
        string.IsNullOrWhiteSpace(ErrorMessage) &&
        TerroristStatus is CsLoadoutTeamStatus.Equipped or CsLoadoutTeamStatus.AlreadyEquipped &&
        CounterTerroristStatus is CsLoadoutTeamStatus.Equipped or CsLoadoutTeamStatus.AlreadyEquipped;

    private static string FormatTeamStatus(CsLoadoutTeamStatus status) =>
        status switch
        {
            CsLoadoutTeamStatus.Equipped => "已装备 R8",
            CsLoadoutTeamStatus.AlreadyEquipped => "已包含 R8",
            CsLoadoutTeamStatus.NoFreeSlot => "无可用手枪槽位",
            CsLoadoutTeamStatus.Failed => "失败",
            _ => status.ToString()
        };
}
