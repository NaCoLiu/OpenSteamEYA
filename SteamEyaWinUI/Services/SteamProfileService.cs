using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 把「个性化」面板里设置的昵称 / 头像应用到目标 Steam 账号——全程走 Steam Web API / steamcommunity，
/// 不连 CM WebSocket：用 EYA refresh token 经 web 换 access token（<see cref="SteamWebSession.BuildViaWebApiAsync"/>），
/// 昵称走 profile 表单（/edit/），头像走 FileUploader。
/// 注意：web 改名提交的是整张 profile 表单，故先读回当前资料以保留简介 / 真名 / 自定义 URL，避免被清空。
/// </summary>
internal sealed partial class SteamProfileService
{
    private const string FileUploaderUrl = "https://steamcommunity.com/actions/FileUploader";

    private readonly JwtTokenService _jwtTokenService = new();

    // 与 SteamWorkshopService 同样的强约束 client：steamLoginSecure（含 access token）手动挂在 Cookie 头，
    // 关闭自动重定向，避免 3xx 指向外部域名时 token 随 Cookie 头外泄。
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public async Task<SteamProfileApplyResult> ApplyAsync(
        string eyaToken,
        string? personaName,
        string? avatarImagePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = personaName?.Trim();
        var hasName = !string.IsNullOrWhiteSpace(trimmedName);
        var hasAvatar = !string.IsNullOrWhiteSpace(avatarImagePath) && File.Exists(avatarImagePath);

        if (!hasName && !hasAvatar)
        {
            throw new InvalidOperationException(Loc.T("Profile_Error_NothingToApply"));
        }

        progress?.Report(Loc.T("Profile_Progress_ValidatingToken"));
        var token = _jwtTokenService.Validate(eyaToken);

        // 必须经 CM 已登录会话换 web access token：GenerateAccessTokenForApp / finalizelogin 在匿名 web 上
        // 一律 AccessDenied(15)，这类 token 需要一个已认证会话来激活。与「清创意工坊订阅」同一套机制。
        progress?.Report(Loc.T("Profile_Progress_Connecting"));
        await using var cmClient = new SteamCmClient(HttpClient);
        await cmClient.ConnectAndLogOnAsync(eyaToken, token.SteamId, cancellationToken);

        progress?.Report(Loc.T("Profile_Progress_GettingWebSession"));
        var session = await SteamWebSession.BuildAsync(cmClient, eyaToken, token.SteamId, cancellationToken);

        var nameApplied = false;
        string? nameError = null;
        if (hasName)
        {
            progress?.Report(Loc.T("Profile_Progress_SettingName"));
            (nameApplied, nameError) = await SetPersonaNameAsync(token.SteamId, trimmedName!, session, cancellationToken);
        }

        var avatarApplied = false;
        string? avatarError = null;
        if (hasAvatar)
        {
            progress?.Report(Loc.T("Profile_Progress_UploadingAvatar"));
            (avatarApplied, avatarError) = await UploadAvatarAsync(
                token.SteamId, avatarImagePath!, session, cancellationToken);
        }

        return new SteamProfileApplyResult(hasName, nameApplied, nameError, hasAvatar, avatarApplied, avatarError);
    }

    // ---- 昵称（profile 表单）----

    private static async Task<(bool Success, string? Error)> SetPersonaNameAsync(
        string steamId,
        string personaName,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        // 先读当前资料，尽量保留其它字段：web 改名走整张 profile 表单，漏填会清空简介 / 真名 / 自定义 URL。
        var current = await TryGetCurrentProfileAsync(steamId, session, cancellationToken);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sessionID", session.SessionId),
            new KeyValuePair<string, string>("type", "profileSave"),
            new KeyValuePair<string, string>("personaName", personaName),
            new KeyValuePair<string, string>("real_name", current.RealName),
            new KeyValuePair<string, string>("customURL", current.CustomUrl),
            new KeyValuePair<string, string>("summary", current.Summary),
            new KeyValuePair<string, string>("hide_profile_awards", "0"),
            new KeyValuePair<string, string>("json", "1")
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://steamcommunity.com/profiles/{steamId}/edit/") { Content = form };
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        using var response = await HttpClient.SendAsync(request, cancellationToken);

        // 3xx：会话不被接受时会被导向登录页等；按会话失效处理，绝不跟随到外部域名。
        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
        {
            return (false, Loc.T("Profile_Error_NameSessionRejected"));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, Loc.Tf("Profile_Error_NameHttp_Format", (int)response.StatusCode));
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseProfileSaveResponse(responseText);
    }

    private static async Task<ProfileFields> TryGetCurrentProfileAsync(
        string steamId, SteamWebSession session, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetFollowingRedirectsAsync(
                new Uri($"https://steamcommunity.com/profiles/{steamId}/edit/info"),
                session.CookieHeader,
                cancellationToken);
            return ParseProfileFields(html);
        }
        catch (Exception ex)
        {
            // 读不到当前资料时不阻断改名，但记录——此时其它资料字段可能被清空。
            AppLog.Error("读取当前 Steam 资料失败，改名将不保留其它资料字段。", ex);
            return ProfileFields.Empty;
        }
    }

    private static ProfileFields ParseProfileFields(string html)
    {
        string Extract(Regex regex)
        {
            var match = regex.Match(html);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;
        }

        return new ProfileFields(
            RealName: Extract(RealNameRegex()),
            Summary: Extract(SummaryRegex()),
            CustomUrl: Extract(CustomUrlRegex()));
    }

    private static (bool Success, string? Error) ParseProfileSaveResponse(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successElement) &&
                (successElement.ValueKind == JsonValueKind.True ||
                    (successElement.ValueKind == JsonValueKind.Number &&
                        successElement.TryGetInt32(out var flag) && flag == 1));

            if (success)
            {
                return (true, null);
            }

            var message = root.TryGetProperty("errmsg", out var messageElement)
                ? messageElement.GetString()
                : null;

            return (false, string.IsNullOrWhiteSpace(message) ? Loc.T("Profile_Error_NameRejected") : message);
        }
        catch (JsonException)
        {
            // 非 JSON（多半是被重定向到登录页的 HTML）→ 视为失败。
            return (false, Loc.T("Profile_Error_NameRejected"));
        }
    }

    // ---- 头像（FileUploader）----

    private static async Task<(bool Success, string? Error)> UploadAvatarAsync(
        string steamId,
        string imagePath,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(fileContent, "avatar", "avatar.jpg");
        form.Add(new StringContent("player_avatar_image"), "type");
        form.Add(new StringContent(steamId), "sId");
        form.Add(new StringContent(session.SessionId), "sessionid");
        form.Add(new StringContent("1"), "doSub");
        form.Add(new StringContent("1"), "json");

        using var request = new HttpRequestMessage(HttpMethod.Post, FileUploaderUrl) { Content = form };
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId}/edit/avatar");

        using var response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest)
        {
            return (false, Loc.T("Profile_Error_AvatarSessionRejected"));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, Loc.Tf("Profile_Error_AvatarHttp_Format", (int)response.StatusCode));
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseUploadResponse(responseText);
    }

    private static (bool Success, string? Error) ParseUploadResponse(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            var success = root.TryGetProperty("success", out var successElement) &&
                (successElement.ValueKind == JsonValueKind.True ||
                    (successElement.ValueKind == JsonValueKind.Number &&
                        successElement.TryGetInt32(out var flag) && flag != 0));

            if (success)
            {
                return (true, null);
            }

            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;

            return (false, string.IsNullOrWhiteSpace(message) ? Loc.T("Profile_Error_AvatarRejected") : message);
        }
        catch (JsonException)
        {
            return (false, Loc.T("Profile_Error_AvatarBadResponse"));
        }
    }

    // ---- 共用：手动跟随 steamcommunity 内部重定向（带 vanity 的账号 GET 资料页会 302）----

    private static async Task<string> GetFollowingRedirectsAsync(
        Uri uri, string cookieHeader, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Add("Cookie", cookieHeader);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            var response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is not (>= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                return body;
            }

            var location = response.Headers.Location;
            response.Dispose();

            if (location is null)
            {
                throw new InvalidOperationException("redirect without location");
            }

            var target = new Uri(current, location);
            if (!IsTrustedSteamCommunityUri(target))
            {
                throw new InvalidOperationException("redirected to external host");
            }

            current = target;
        }

        throw new InvalidOperationException("too many redirects");
    }

    private static bool IsTrustedSteamCommunityUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase);
    }

    // profile 编辑页里这几个字段的现值（保留用，避免改名时清空）。
    [GeneratedRegex(@"id=""real_name""[^>]*?\bvalue=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex RealNameRegex();

    [GeneratedRegex(@"id=""customURL""[^>]*?\bvalue=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex CustomUrlRegex();

    [GeneratedRegex(@"id=""summary""[^>]*?>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryRegex();

    private sealed record ProfileFields(string RealName, string Summary, string CustomUrl)
    {
        public static ProfileFields Empty { get; } = new(string.Empty, string.Empty, string.Empty);
    }
}

/// <summary>个性化应用结果：分别记录昵称 / 头像是否被请求、是否成功，以及各自的失败原因。</summary>
internal sealed record SteamProfileApplyResult(
    bool NameRequested,
    bool NameApplied,
    string? NameError,
    bool AvatarRequested,
    bool AvatarApplied,
    string? AvatarError)
{
    public bool IsFullSuccess =>
        (!NameRequested || NameApplied) && (!AvatarRequested || AvatarApplied);
}
