using System.IO;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Models;

public sealed partial class CachedSteamLoginAccount
{
    public string AccountName { get; set; } = "";

    public string SteamId { get; set; } = "";

    public string? PersonaName { get; set; }

    public string? AvatarUrl { get; set; }

    public string? AvatarPath { get; set; }

    public DateTimeOffset CachedAt { get; set; }

    // local.vdf 的 ConnectCache 令牌（crc32(账户名)+"1" → DPAPI 加密的刷新令牌 hex）。
    // EYA 登录会整体覆盖 local.vdf，故在覆盖前抓取并随账号持久化，恢复时原样写回，Steam 才能免密自动登录。
    // 该 blob 已由 Steam 用 DPAPI（当前用户）加密，仅本机本用户可解，敏感度与 local.vdf 本身相当。
    public string? ConnectCacheToken { get; set; }

    [JsonIgnore]
    public string CacheKey => string.IsNullOrWhiteSpace(SteamId) ? $"name:{AccountName}" : $"id:{SteamId}";

    [JsonIgnore]
    public string AccountTitle => string.IsNullOrWhiteSpace(AccountName) ? "未知账号" : AccountName;

    [JsonIgnore]
    public string PersonaDisplayName => string.IsNullOrWhiteSpace(PersonaName) ? "Steam 资料未同步" : PersonaName;

    [JsonIgnore]
    public string SteamIdDisplay => string.IsNullOrWhiteSpace(SteamId) ? "Steam64 未记录" : SteamId;

    [JsonIgnore]
    public string CachedAtText => CachedAt == default
        ? "未知时间"
        : CachedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string CachedAtShortText => CachedAt == default
        ? "未知"
        : CachedAt.LocalDateTime.ToString("MM-dd HH:mm");

    // 进程级头像缓存：列表重建会换新实例，仅靠实例字段无法跨重建复用，必须用静态字典才真正止血。
    // 键 = 完整路径 + 最后写入时间，头像更新（重新下载覆盖同名文件）后键变化自动失效。
    // 仅 UI 线程访问（BitmapImage 也只能在 UI 线程使用），普通 Dictionary 即可。
    private static readonly Dictionary<string, BitmapImage> AvatarCache = new(StringComparer.OrdinalIgnoreCase);

    // PersonPicture 最大显示 92px（账号详情），按 2 倍留 DPI 余量解码。
    private const int AvatarDecodePixelWidth = 184;

    private BitmapImage? _avatarImage;

    [JsonIgnore]
    public BitmapImage? AvatarImage => _avatarImage ??= LoadAvatarImage();

    private BitmapImage? LoadAvatarImage()
    {
        var localPath = AvatarPath;
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            try
            {
                var cacheKey = $"{localPath}|{File.GetLastWriteTimeUtc(localPath):O}";
                if (AvatarCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                // 从字节解码而非 new BitmapImage(Uri)：后者会长期持有文件句柄，导致删除账号时头像删不掉。
                var bytes = File.ReadAllBytes(localPath);
                var bitmap = new BitmapImage { DecodePixelWidth = AvatarDecodePixelWidth };
                using (var stream = new MemoryStream(bytes))
                {
                    // SetSource 同步消费完流后才返回，故 using 释放时机安全。
                    bitmap.SetSource(stream.AsRandomAccessStream());
                }

                AvatarCache[cacheKey] = bitmap;
                return bitmap;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"加载缓存账号头像失败：{localPath}，{ex.Message}");
                // 落到下面的 URL 回退或返回 null（默认头像）。
            }
        }

        if (!string.IsNullOrWhiteSpace(AvatarUrl) &&
            Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var avatarUri))
        {
            return new BitmapImage(avatarUri);
        }

        return null;
    }
}
