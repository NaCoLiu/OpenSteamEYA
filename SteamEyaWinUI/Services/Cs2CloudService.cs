using System.Globalization;
using System.IO;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>一份要强推的 CS2 配置文件（云文件名 + 内容字节）。</summary>
internal sealed record Cs2CfgFile(string Name, byte[] Data);

/// <summary>候选「设置来源」账号：其本地 userdata 下存在 CS2 配置。</summary>
internal sealed record Cs2SettingsSource(string SteamId64, uint AccountId);

/// <summary>强推结果：是否成功、写入文件数、失败原因、「该账号未开启账号级 Steam 云」标记，以及部分写入失败的文件数。</summary>
internal sealed record Cs2CloudPushResult(
    bool Ok, int Pushed, string? Error, bool AccountCloudDisabled = false, int PartialFailed = 0);

/// <summary>
/// CS2（AppID 730）设置的「云强推」同步（issue #10）。
///
/// 思路：准星/灵敏度/视角/键位等是 Source2 客户端 convar，跨账号复制走官方 SDK 云强推：
///   1. 从「来源账号」的 userdata/&lt;accountId&gt;/730/<b>remote</b> 读取 cs2_user_*.vcfg——这里是云端文件的本地镜像，
///      文件名即云端文件名（实测：云端为 cs2_user_convars.vcfg / cs2_user_keys.vcfg，与 local/cfg 里的
///      cs2_user_convars_0_slot0.vcfg 名字不同，故必须读 remote 而不是 local/cfg）；
///   2. 目标账号登录后，用 Steamworks 云 API（<see cref="SteamworksNative"/>）以相同文件名 FileWrite 强推到
///      「当前登录账号」的 CS2 云端——由 Steam 负责下发落地，我们不手搓云逻辑。
/// 注意：画面设置 cs2_video.txt 不在云端（属整机本地文件），云同步无法覆盖它。
///
/// 前提：目标账号拥有 CS2、Steam 已登录、运行目录有 steam_api64.dll（见 SteamworksNative）。
/// 全程 best-effort：任何失败只记日志/提示，绝不中断上号主流程。
/// </summary>
internal sealed class Cs2CloudService
{
    private const string Cs2AppFolder = "730";
    private const uint Cs2AppId = 730;
    // 个人账号 SteamID64 = 该基准 + 32 位 accountId（accountId 即 userdata 目录名）。
    private const ulong SteamId64Base = 76561197960265728UL;

    // SteamAPI_Init/Shutdown 是进程级全局状态，跨本类所有实例串行化，避免并发 init 崩溃。
    private static readonly object InitGate = new();

    /// <summary>枚举「已上云过 CS2 设置」的候选来源账号（remote 下有 cs2_user_*.vcfg），供设置页来源下拉。</summary>
    public IReadOnlyList<Cs2SettingsSource> EnumerateSources(string? userdataPath)
    {
        var result = new List<Cs2SettingsSource>();
        if (string.IsNullOrWhiteSpace(userdataPath) || !Directory.Exists(userdataPath))
        {
            return result;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(userdataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"枚举 userdata 目录失败：{userdataPath}，{ex.Message}");
            return result;
        }

        foreach (var dir in dirs)
        {
            if (!uint.TryParse(Path.GetFileName(dir), out var accountId) || accountId == 0)
            {
                continue;
            }

            if (!HasCs2Config(RemoteDir(userdataPath, accountId)))
            {
                continue;
            }

            var steamId64 = (SteamId64Base + accountId).ToString(CultureInfo.InvariantCulture);
            result.Add(new Cs2SettingsSource(steamId64, accountId));
        }

        return result;
    }

    /// <summary>
    /// 读取来源账号 remote 下的云端 CS2 设置文件（cs2_user_convars.vcfg / cs2_user_keys.vcfg）作为待强推内容；
    /// 文件名即云端名，直接原样 FileWrite 即可。无来源/无配置返回空。
    /// </summary>
    public IReadOnlyList<Cs2CfgFile> ReadSourceCfgFiles(string userdataPath, string? sourceSteamId64)
    {
        if (string.IsNullOrWhiteSpace(sourceSteamId64) || !TryAccountId(sourceSteamId64, out var accountId))
        {
            return Array.Empty<Cs2CfgFile>();
        }

        var remoteDir = RemoteDir(userdataPath, accountId);
        if (!Directory.Exists(remoteDir))
        {
            return Array.Empty<Cs2CfgFile>();
        }

        var result = new List<Cs2CfgFile>();
        try
        {
            // 只取用户设置（convars=准星/灵敏度/视角等、keys=键位）；跳过 socache.dt/voice_ban.dt 等账号级状态。
            foreach (var path in Directory.GetFiles(remoteDir, "cs2_user_*.vcfg"))
            {
                try
                {
                    result.Add(new Cs2CfgFile(Path.GetFileName(path), File.ReadAllBytes(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"读取来源 CS2 云文件失败：{path}，{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"枚举来源 CS2 remote 目录失败：{remoteDir}，{ex.Message}");
        }

        return result;
    }

    /// <summary>登录时调用（放后台线程）：把来源账号配置强推到刚登录的目标账号云端。全程吞异常。</summary>
    public void PushSourceForLogin(
        SteamPaths paths, string? sourceSteamId64, string targetSteamId64, IProgress<string>? progress)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceSteamId64) ||
                !TryAccountId(sourceSteamId64, out var sourceId) ||
                !TryAccountId(targetSteamId64, out var targetId) ||
                sourceId == targetId)
            {
                return;
            }

            var files = ReadSourceCfgFiles(paths.UserdataPath, sourceSteamId64);
            if (files.Count == 0)
            {
                return;
            }

            progress?.Report(Loc.T("Cs2Cloud_Progress_Pushing"));
            // 登录后 Steam 需数秒才登好，ForcePush 内部会重试等待；
            // 传入目标 SteamID：写云前核对当前登录账号确实是它，登错/登成别的账号时宁可放弃也不覆盖别人云端。
            var result = ForcePush(files, maxWaitSeconds: 40, expectedSteamId64: targetSteamId64);
            progress?.Report(DescribeResult(result));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"CS2 云推送（登录时）失败，忽略：{ex.Message}");
        }
    }

    /// <summary>设置页「立即推送」：把来源账号配置强推到当前登录账号云端（假设 Steam 已在运行/登录）。</summary>
    public Cs2CloudPushResult PushSourceNow(SteamPaths paths, string? sourceSteamId64)
    {
        if (string.IsNullOrWhiteSpace(sourceSteamId64))
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSourceSelected"));
        }

        var files = ReadSourceCfgFiles(paths.UserdataPath, sourceSteamId64);
        if (files.Count == 0)
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSource"));
        }

        return ForcePush(files, maxWaitSeconds: 4);
    }

    /// <summary>把推送结果转成用户可见文案（区分成功 / 成功但账号云关闭 / 失败）。</summary>
    public static string DescribeResult(Cs2CloudPushResult result)
    {
        if (!result.Ok)
        {
            return Loc.Tf("Cs2Cloud_Progress_Failed_Format", result.Error ?? string.Empty);
        }

        if (result.PartialFailed > 0)
        {
            return Loc.Tf("Cs2Cloud_Progress_Partial_Format", result.Pushed, result.Pushed + result.PartialFailed);
        }

        return result.AccountCloudDisabled
            ? Loc.Tf("Cs2Cloud_Progress_DoneNoCloud_Format", result.Pushed)
            : Loc.Tf("Cs2Cloud_Progress_Done_Format", result.Pushed);
    }

    /// <summary>
    /// 用 Steamworks 云 API 把给定文件强推到「当前登录账号」的 CS2(730) 云端。
    /// <paramref name="maxWaitSeconds"/> 内以 2s 间隔重试 SteamAPI_Init（等 Steam 登录就绪）。
    /// <paramref name="expectedSteamId64"/> 非空时，写云前核对当前登录账号确为该 SteamID，
    /// 不符则视为「还没登到目标账号」继续等待，超时也绝不写到别的账号（防覆盖他人云端设置）。
    /// 「立即推送」传 null，语义即推给当前登录的任意账号。
    /// </summary>
    public Cs2CloudPushResult ForcePush(
        IReadOnlyList<Cs2CfgFile> files, int maxWaitSeconds, string? expectedSteamId64 = null)
    {
        if (files.Count == 0)
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSource"));
        }

        ulong? expectedId = null;
        if (!string.IsNullOrWhiteSpace(expectedSteamId64) && ulong.TryParse(expectedSteamId64.Trim(), out var parsed))
        {
            expectedId = parsed;
        }

        // 每次尝试只在锁内做一次 init+写入+shutdown（原子），Thread.Sleep 放锁外，
        // 避免长时间独占进程级 InitGate 拖死并发的另一次推送/「立即推送」。
        var attempts = Math.Max(1, maxWaitSeconds / 2);
        var lastRetryResult = new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_InitFailed"));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var (retry, result) = TryPushOnce(files, expectedId);
            if (!retry)
            {
                return result;
            }

            lastRetryResult = result; // 保留最后一次重试原因（init 失败 / 账号不符），超时后据此告知用户。
            if (attempt < attempts - 1)
            {
                Thread.Sleep(2000);
            }
        }

        return lastRetryResult;
    }

    // 单次尝试。返回 (retry, result)：retry=true 表示「值得等 Steam 登好/登到目标账号再试」
    //（SteamAPI_Init 失败，或当前登录账号还不是目标账号）；
    // 其余情况（DLL 缺失/接口拿不到/写入完成/异常）都 retry=false，直接以 result 返回，不再重试。
    private static (bool Retry, Cs2CloudPushResult Result) TryPushOnce(
        IReadOnlyList<Cs2CfgFile> files, ulong? expectedSteamId)
    {
        lock (InitGate)
        {
            EnsureAppIdContext();

            var inited = false;
            try
            {
                var errMsg = new byte[1024]; // SteamErrMsg = char[k_cchMaxSteamErrMsg=1024]
                var initResult = SteamworksNative.SteamAPI_InitFlat(errMsg);
                inited = initResult == 0; // 0 = k_ESteamAPIInitResult_OK
                if (!inited)
                {
                    AppLog.Warn($"SteamAPI_InitFlat 失败（result={initResult}）：{DecodeErrMsg(errMsg)}");
                    return (true, new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_InitFailed")));
                }

                // 登录时推送：核对当前登录账号确为目标账号，不符则继续等（宁可超时放弃也不写错账号）。
                if (expectedSteamId is { } expected && !IsLoggedInAs(expected))
                {
                    return (true, new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_WrongAccount")));
                }

                var remoteStorage = SteamworksNative.SteamAPI_SteamRemoteStorage_v016();
                if (remoteStorage == IntPtr.Zero)
                {
                    return (false, new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoInterface")));
                }

                SteamworksNative.SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp(remoteStorage, true);

                // 账号级云是用户在 Steam 设置里的总开关，SDK 无法代开；关着时 FileWrite 只写本地、不上云。
                var accountCloudDisabled =
                    !SteamworksNative.SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount(remoteStorage);
                if (accountCloudDisabled)
                {
                    AppLog.Warn("当前账号未开启账号级 Steam 云（Steam 设置→云同步），FileWrite 只写本地、不会上云。");
                }

                var pushed = 0;
                foreach (var file in files)
                {
                    if (SteamworksNative.SteamAPI_ISteamRemoteStorage_FileWrite(
                            remoteStorage, file.Name, file.Data, file.Data.Length))
                    {
                        pushed++;
                    }
                    else
                    {
                        AppLog.Warn($"CS2 云 FileWrite 失败：{file.Name}");
                    }
                }

                AppLog.Info($"CS2 云强推：成功写入 {pushed}/{files.Count} 个文件（账号云禁用={accountCloudDisabled}）。");
                if (pushed == 0)
                {
                    return (false, new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_WriteFailed")));
                }

                // 部分文件写入失败：仍算已推送但明确告知用户不完整（原实现只要 pushed>0 就报“全部成功”）。
                var partialFailed = files.Count - pushed;
                return (false, new Cs2CloudPushResult(true, pushed, null, accountCloudDisabled, partialFailed));
            }
            catch (DllNotFoundException)
            {
                return (false, new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_DllMissing")));
            }
            catch (EntryPointNotFoundException)
            {
                return (false, new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_DllVersion")));
            }
            catch (Exception ex)
            {
                AppLog.Error("CS2 云强推异常。", ex);
                return (false, new Cs2CloudPushResult(false, 0, ex.Message));
            }
            finally
            {
                if (inited)
                {
                    SteamworksNative.SteamAPI_Shutdown();
                }

                // 清理本次为初始化 CS2 身份设的进程级环境变量，避免其被后续 login 启动的 steam.exe 等子进程继承。
                ClearAppIdContext();
            }
        }
    }

    // 核对当前登录 Steam 账号的 SteamID64 是否等于期望目标。拿不到用户接口（DLL 版本不符等）时返回 true 放行，
    // 保持“不因核对能力缺失而彻底失效”，此时退化为原“推给当前登录账号”的行为。
    private static bool IsLoggedInAs(ulong expectedSteamId64)
    {
        try
        {
            var user = SteamworksNative.SteamAPI_SteamUser_v023();
            if (user == IntPtr.Zero)
            {
                AppLog.Warn("拿不到 ISteamUser 接口，跳过登录账号核对。");
                return true;
            }

            var actual = SteamworksNative.SteamAPI_ISteamUser_GetSteamID(user);
            if (actual == expectedSteamId64)
            {
                return true;
            }

            AppLog.Warn($"当前登录账号（{actual}）与目标账号（{expectedSteamId64}）不符，暂不写云、继续等待。");
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            AppLog.Warn("steam_api64.dll 无 ISteamUser v023 导出，跳过登录账号核对。");
            return true;
        }
    }

    // 账号的 CS2 云文件本地镜像目录：userdata/<accountId>/730/remote（文件名即云端名）。
    private static string RemoteDir(string userdataPath, uint accountId) => Path.Combine(
        userdataPath, accountId.ToString(CultureInfo.InvariantCulture), Cs2AppFolder, "remote");

    private static bool HasCs2Config(string remoteDir)
    {
        try
        {
            return Directory.Exists(remoteDir) && Directory.EnumerateFiles(remoteDir, "cs2_user_*.vcfg").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // userdata 目录名 = SteamID64 低 32 位（accountId）。
    private static bool TryAccountId(string steamId64, out uint accountId)
    {
        accountId = 0;
        if (!ulong.TryParse(steamId64.Trim(), out var id))
        {
            return false;
        }

        accountId = (uint)(id & 0xFFFFFFFF);
        return accountId != 0;
    }

    // 从 SteamErrMsg（char[1024]，ASCII、null 结尾）解出错误文字，用于日志诊断。
    private static string DecodeErrMsg(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return length == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(buffer, 0, length);
    }

    // 让本进程以 CS2 身份初始化 Steamworks：设置 SteamAppId 环境变量（SDK 优先读它，最可靠），
    // 并在 exe 目录写一份 steam_appid.txt 兜底（部分 SDK 版本按工作目录读该文件）。
    private static void EnsureAppIdContext()
    {
        var appId = Cs2AppId.ToString(CultureInfo.InvariantCulture);
        try
        {
            Environment.SetEnvironmentVariable("SteamAppId", appId);
            Environment.SetEnvironmentVariable("SteamGameId", appId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置 SteamAppId 环境变量失败：{ex.Message}");
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, appId);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写 steam_appid.txt 失败：{ex.Message}");
        }
    }

    // 清掉进程级 SteamAppId/SteamGameId，避免后续 login 启动的 steam.exe 及其子进程继承到 730 身份。
    // steam_appid.txt 保留不动（只在本进程 exe 目录，下次推送 EnsureAppIdContext 会照常复用）。
    private static void ClearAppIdContext()
    {
        try
        {
            Environment.SetEnvironmentVariable("SteamAppId", null);
            Environment.SetEnvironmentVariable("SteamGameId", null);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"清理 SteamAppId 环境变量失败：{ex.Message}");
        }
    }
}
