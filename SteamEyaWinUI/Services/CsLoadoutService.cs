using System.Globalization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class CsLoadoutService
{
    private static readonly TimeSpan EquipSoWaitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LoadoutRefreshTimeout = TimeSpan.FromSeconds(8);
    private static readonly SemaphoreSlim EquipGate = new(1, 1);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<CsLoadoutEquipResult> EquipR8Async(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken = default)
    {
        if (!await EquipGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new InvalidOperationException("已有 R8 配装任务正在执行，请等待完成后再试。");
        }

        try
        {
            return await EquipR8CoreAsync(refreshToken, steamId, cancellationToken);
        }
        finally
        {
            EquipGate.Release();
        }
    }

    private async Task<CsLoadoutEquipResult> EquipR8CoreAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ulong.TryParse(steamId, CultureInfo.InvariantCulture, out var steamId64))
            {
                throw new InvalidOperationException("Steam64 格式不正确，无法修改配装。");
            }

            var accountId = CsGcSession.GetAccountId(steamId64);

            await using var cmClient = new SteamCmClient(HttpClient);
            await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);

            try
            {
                await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
                var welcomePayload = await CsGcSession.ConnectAsync(cmClient, cancellationToken);

                var loadoutMap = ReadLoadoutFromWelcome(welcomePayload, accountId);
                if (loadoutMap.Count == 0)
                {
                    var refreshed = await RefreshLoadoutAsync(
                        cmClient,
                        welcomePayload,
                        steamId64,
                        accountId,
                        cancellationToken);
                    CsSoCacheParser.MergeEntries(loadoutMap, refreshed);
                }

                var entries = loadoutMap.Values.ToList();
                var terroristPlan = CsLoadoutPlanner.PlanR8Slot(CsLoadoutConstants.TeamTerrorist, entries);
                var counterTerroristPlan = CsLoadoutPlanner.PlanR8Slot(
                    CsLoadoutConstants.TeamCounterTerrorist,
                    entries);

                var slotsToEquip = BuildSlotsToEquip(terroristPlan, counterTerroristPlan);
                if (slotsToEquip.Count == 0)
                {
                    var noChangeResult = new CsLoadoutEquipResult(
                        terroristPlan.Status,
                        counterTerroristPlan.Status);
                    LogOutcome(noChangeResult);
                    return noChangeResult;
                }

                var tappedMessages = new List<SteamCmClient.SteamGcClientMessage>();
                var tappedMessagesLock = new object();
                cmClient.SetGcMessageTap(message =>
                {
                    lock (tappedMessagesLock)
                    {
                        tappedMessages.Add(message);
                    }
                });

                List<CsLoadoutEntry> verifiedEntries;
                try
                {
                    CsSoCacheParser.TryGetSoCacheVersionFromWelcome(welcomePayload, out var soCacheVersion);
                    var changeNum = BuildChangeNum(soCacheVersion);
                    var defaultItemId = CsLoadoutConstants.BuildDefaultBaseItemId(
                        CsLoadoutConstants.ItemDefinitionRevolver);

                    await cmClient.SendGcProtobufMessageAsync(
                        CsGcSession.Cs2AppId,
                        CsLoadoutConstants.AdjustEquipSlotsManual,
                        EncodeAdjustEquipSlots(slotsToEquip, defaultItemId, changeNum),
                        cancellationToken);

                    verifiedEntries = await WaitForEquipSoUpdateAsync(
                        cmClient,
                        tappedMessages,
                        tappedMessagesLock,
                        accountId,
                        cancellationToken);
                }
                finally
                {
                    cmClient.SetGcMessageTap(null);
                }

                var terroristStatus = ResolveTeamStatus(
                    terroristPlan,
                    CsLoadoutConstants.TeamTerrorist,
                    verifiedEntries);
                var counterTerroristStatus = ResolveTeamStatus(
                    counterTerroristPlan,
                    CsLoadoutConstants.TeamCounterTerrorist,
                    verifiedEntries);

                var result = new CsLoadoutEquipResult(
                    terroristStatus,
                    counterTerroristStatus,
                    BuildFailureMessage(terroristStatus, counterTerroristStatus));

                LogOutcome(result);
                return result;
            }
            finally
            {
                try
                {
                    await cmClient.SetGamesPlayedAsync([], CancellationToken.None);
                }
                catch
                {

                }
            }
        }
        catch (Exception ex)
        {
            if (IsSteamSessionConflict(ex))
            {
                var conflict = new InvalidOperationException(
                    ex.Message.Contains("CM 连接已被顶替", StringComparison.Ordinal)
                        ? ex.Message
                        : "Steam CM 连接被断开：请尝试先完全退出 CS2 和 Steam，再在 SteamEYA 中执行 R8 配装。",
                    ex);
                AppLog.Error($"R8 配装失败：{conflict.Message}");
                throw conflict;
            }

            AppLog.Error($"R8 配装失败：{ex.Message}");
            throw;
        }
    }

    private static void LogOutcome(CsLoadoutEquipResult result)
    {
        if (result.IsSuccess)
        {
            AppLog.Info("R8 配装成功。");
            return;
        }

        AppLog.Error($"R8 配装失败：{result.ErrorMessage ?? result.FormatSummary()}");
    }

    private static Dictionary<(uint ClassId, uint SlotId), CsLoadoutEntry> ReadLoadoutFromWelcome(
        byte[] welcomePayload,
        uint accountId)
    {
        var loadoutMap = new Dictionary<(uint ClassId, uint SlotId), CsLoadoutEntry>();
        CsSoCacheParser.MergeEntries(
            loadoutMap,
            CsSoCacheParser.ParseLoadoutFromWelcome(welcomePayload, accountId));
        return loadoutMap;
    }

    private static List<(uint ClassId, uint SlotId)> BuildSlotsToEquip(
        CsR8EquipPlan terroristPlan,
        CsR8EquipPlan counterTerroristPlan)
    {
        var slots = new List<(uint ClassId, uint SlotId)>();

        if (terroristPlan.Status == CsLoadoutTeamStatus.Equipped && terroristPlan.SlotId.HasValue)
        {
            slots.Add((CsLoadoutConstants.TeamTerrorist, terroristPlan.SlotId.Value));
        }

        if (counterTerroristPlan.Status == CsLoadoutTeamStatus.Equipped &&
            counterTerroristPlan.SlotId.HasValue)
        {
            slots.Add((CsLoadoutConstants.TeamCounterTerrorist, counterTerroristPlan.SlotId.Value));
        }

        return slots;
    }

    private static CsLoadoutTeamStatus ResolveTeamStatus(
        CsR8EquipPlan plan,
        uint teamClassId,
        IReadOnlyList<CsLoadoutEntry> verifiedEntries)
    {
        if (plan.Status != CsLoadoutTeamStatus.Equipped)
        {
            return plan.Status;
        }

        return CsLoadoutPlanner.HasR8Equipped(teamClassId, verifiedEntries)
            ? CsLoadoutTeamStatus.Equipped
            : CsLoadoutTeamStatus.Failed;
    }

    private static async Task<List<CsLoadoutEntry>> RefreshLoadoutAsync(
        SteamCmClient cmClient,
        byte[] welcomePayload,
        ulong steamId64,
        uint accountId,
        CancellationToken cancellationToken)
    {
        if (!CsSoCacheParser.TryGetOwnerSoidFromWelcome(welcomePayload, out var ownerType, out var ownerId))
        {
            ownerType = CsLoadoutConstants.SoOwnerTypeIndividual;
            ownerId = steamId64;
        }

        try
        {
            var refreshTask = cmClient.WaitForGcMessageAsync(
                CsGcSession.Cs2AppId,
                CsLoadoutConstants.SoCacheSubscribed,
                LoadoutRefreshTimeout,
                cancellationToken,
                cacheUnmatched: true);

            await cmClient.SendGcProtobufMessageAsync(
                CsGcSession.Cs2AppId,
                CsLoadoutConstants.SoCacheSubscriptionRefresh,
                EncodeSoCacheSubscriptionRefresh(ownerType, ownerId),
                cancellationToken);

            var message = await refreshTask;
            return CsSoCacheParser.ParseLoadoutFromGcMessage(
                message.MessageType,
                message.Payload,
                accountId);
        }
        catch (TimeoutException)
        {
            return [];
        }
    }

    private static async Task<List<CsLoadoutEntry>> WaitForEquipSoUpdateAsync(
        SteamCmClient cmClient,
        List<SteamCmClient.SteamGcClientMessage> tappedMessages,
        object tappedMessagesLock,
        uint accountId,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForSoUpdateMessageAsync(
                cmClient,
                tappedMessages,
                tappedMessagesLock,
                cancellationToken);
        }
        catch (TimeoutException)
        {

        }

        return MergeLoadoutFromTappedMessages(tappedMessages, tappedMessagesLock, accountId);
    }

    private static async Task WaitForSoUpdateMessageAsync(
        SteamCmClient cmClient,
        List<SteamCmClient.SteamGcClientMessage> tappedMessages,
        object tappedMessagesLock,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + EquipSoWaitTimeout;
        var processedCount = 0;
        var waitTypes = new[]
        {
            CsLoadoutConstants.SoUpdateMultiple,
            CsLoadoutConstants.SoCacheSubscribed,
            CsLoadoutConstants.SoUpdate,
            CsLoadoutConstants.SoCreate
        };

        while (DateTimeOffset.UtcNow < deadline)
        {
            List<SteamCmClient.SteamGcClientMessage> pendingMessages;
            lock (tappedMessagesLock)
            {
                pendingMessages = tappedMessages
                    .Skip(processedCount)
                    .ToList();
                processedCount = tappedMessages.Count;
            }

            foreach (var message in pendingMessages)
            {
                if (CsSoCacheParser.IsLoadoutSoMessage(message.MessageType))
                {
                    return;
                }
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var waitSlice = remaining < TimeSpan.FromSeconds(1)
                ? remaining
                : TimeSpan.FromSeconds(1);

            foreach (var msgType in waitTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await cmClient.WaitForGcMessageAsync(
                        CsGcSession.Cs2AppId,
                        msgType,
                        waitSlice,
                        cancellationToken,
                        cacheUnmatched: true);
                    return;
                }
                catch (TimeoutException)
                {

                }
            }
        }

        throw new TimeoutException("等待 SO 配装更新超时。");
    }

    private static List<CsLoadoutEntry> MergeLoadoutFromTappedMessages(
        IReadOnlyList<SteamCmClient.SteamGcClientMessage> tappedMessages,
        object tappedMessagesLock,
        uint accountId)
    {
        var loadoutMap = new Dictionary<(uint ClassId, uint SlotId), CsLoadoutEntry>();
        List<SteamCmClient.SteamGcClientMessage> messages;
        lock (tappedMessagesLock)
        {
            messages = tappedMessages.ToList();
        }

        foreach (var message in messages)
        {
            if (!CsSoCacheParser.IsLoadoutSoMessage(message.MessageType))
            {
                continue;
            }

            CsSoCacheParser.MergeEntries(
                loadoutMap,
                CsSoCacheParser.ParseLoadoutFromGcMessage(message.MessageType, message.Payload, accountId));
        }

        return loadoutMap.Values.ToList();
    }

    private static bool IsSteamSessionConflict(Exception ex) =>
        ex.Message.Contains("CM 连接已被顶替", StringComparison.Ordinal) ||
        ex.Message.Contains("Steam 账号已下线", StringComparison.Ordinal);

    private static string? BuildFailureMessage(
        CsLoadoutTeamStatus terroristStatus,
        CsLoadoutTeamStatus counterTerroristStatus)
    {
        if (terroristStatus is CsLoadoutTeamStatus.Equipped or CsLoadoutTeamStatus.AlreadyEquipped &&
            counterTerroristStatus is CsLoadoutTeamStatus.Equipped or CsLoadoutTeamStatus.AlreadyEquipped)
        {
            return null;
        }

        if (terroristStatus != CsLoadoutTeamStatus.Failed &&
            counterTerroristStatus != CsLoadoutTeamStatus.Failed)
        {
            return null;
        }

        return "R8 配装未生效。请确认已完全退出 CS2 和 Steam 后重试。";
    }

    private static uint BuildChangeNum(ulong soCacheVersion) =>
        soCacheVersion != 0
            ? (uint)((soCacheVersion + 1) & 0xFFFFFFFF)
            : 1;

    private static byte[] EncodeSoCacheSubscriptionRefresh(uint ownerType, ulong ownerId) =>
        SteamProtoWriter.Build(writer =>
        {
            writer.WriteBytes(2, SteamProtoWriter.Build(ownerWriter =>
            {
                ownerWriter.WriteUInt32(1, ownerType);
                ownerWriter.WriteUInt64(2, ownerId);
            }));
        });

    private static byte[] EncodeAdjustEquipSlots(
        IReadOnlyList<(uint ClassId, uint SlotId)> slots,
        ulong itemId,
        uint changeNum) =>
        SteamProtoWriter.Build(writer =>
        {
            foreach (var (classId, slotId) in slots)
            {
                writer.WriteBytes(1, SteamProtoWriter.Build(slotWriter =>
                {
                    slotWriter.WriteUInt32(1, classId);
                    slotWriter.WriteUInt32(2, slotId);
                    slotWriter.WriteUInt64(3, itemId);
                }));
            }

            writer.WriteUInt32(2, changeNum);
        });
}
