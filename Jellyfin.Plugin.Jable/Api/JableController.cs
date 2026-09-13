using System.Security.Claims;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.Jable.Models;
using Jellyfin.Plugin.Jable.ScheduledTasks;
using Jellyfin.Plugin.Jable.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.Api;

public static class JableAuthorization
{
    public static Guid? GetUserId(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && Guid.TryParse(principal.FindFirst("Jellyfin-UserId")?.Value, out var id) && id != Guid.Empty ? id : null;

    public static bool IsAdministrator(UserPolicy? policy) => policy?.IsAdministrator == true;
}

[ApiController]
[Authorize]
[Route("Jable")]
public sealed class JableController(JableCatalogService catalog, LibraryAccessService access, IUserManager users,
    ITaskManager tasks, JableHttpClient client) : ControllerBase
{
    private const int MaxImageBytes = 30 * 1024 * 1024;

    [AllowAnonymous]
    [HttpGet("Page")]
    public IActionResult GetPage()
    {
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src blob:; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'self'";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Resource("jable.html", "text/html; charset=utf-8");
    }

    [AllowAnonymous]
    [HttpGet("Assets/{name}")]
    public IActionResult GetAsset(string name) => name switch
    {
        "jable.css" => Resource(name, "text/css; charset=utf-8"),
        "jable.mjs" => Resource(name, "text/javascript; charset=utf-8"),
        _ => NotFound(),
    };

    [HttpGet("Catalog")]
    public async Task<IActionResult> GetCatalog([FromQuery] CatalogQuery query, CancellationToken cancellationToken)
    {
        if (AuthorizeUser(out var user) is { } rejection) return rejection;
        return Ok(await catalog.QueryAsync(query, access.BuildLocalNumberIndex(user), cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("Works/{number}")]
    public async Task<IActionResult> GetWork(string number, CancellationToken cancellationToken)
    {
        if (AuthorizeUser(out var user) is { } rejection) return rejection;
        var local = access.BuildLocalNumberIndex(user);
        try
        {
            var work = await catalog.GetExactAsync(number, cancellationToken).ConfigureAwait(false);
            return work is null ? NotFound() : Ok(JableCatalogService.ToDto(work, local));
        }
        catch (Exception exception) when (exception is JableRequestException or HttpRequestException)
        {
            var cached = await catalog.GetCachedAsync(number, cancellationToken).ConfigureAwait(false);
            return cached is null ? StatusCode(StatusCodes.Status502BadGateway) : Ok(JableCatalogService.ToDto(cached, local));
        }
    }

    [HttpGet("Images/{number}")]
    public async Task<IActionResult> GetImage(string number, CancellationToken cancellationToken)
    {
        if (AuthorizeUser(out _) is { } rejection) return rejection;
        var work = await catalog.GetCachedAsync(number, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(work?.PosterUrl, UriKind.Absolute, out var uri) || !JableHttpClient.IsAllowedJableUri(uri)) return NotFound();

        try
        {
            // GetImageAsync returns after headers; keep the body read bounded in size and time too.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await client.GetImageAsync(uri, timeout.Token).ConfigureAwait(false);
            var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (!response.IsSuccessStatusCode || mime is not ("image/jpeg" or "image/png" or "image/webp")
                || response.Content.Headers.ContentLength is > MaxImageBytes) return StatusCode(StatusCodes.Status502BadGateway);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxImageBytes + 1 - buffer.Length)), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxImageBytes) return StatusCode(StatusCodes.Status502BadGateway);
            }

            Response.Headers.CacheControl = "private, max-age=86400";
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.Vary = "X-Emby-Token";
            return File(buffer.ToArray(), mime);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (Exception exception) when (exception is JableRequestException or HttpRequestException or IOException)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    [HttpGet("Status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        if (AuthorizeUser(out var user) is { } rejection) return rejection;
        var status = await catalog.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        status.CanManage = JableAuthorization.IsAdministrator(users.GetUserDto(user!).Policy);
        if (status.CanManage)
        {
            var worker = tasks.ScheduledTasks.FirstOrDefault(task => task.ScheduledTask is JableCatalogSyncTask);
            status.SyncTaskId = worker?.Id ?? string.Empty;
            status.IsSyncRunning = worker?.State is TaskState.Running or TaskState.Cancelling;
        }
        return Ok(status);
    }

    [HttpPost("Sync")]
    public IActionResult Sync()
    {
        if (AuthorizeUser(out _, administrator: true) is { } rejection) return rejection;
        tasks.QueueIfNotRunning<JableCatalogSyncTask>();
        return Accepted();
    }

    [HttpPost("Cache/ClearSearch")]
    public async Task<IActionResult> ClearSearch(CancellationToken cancellationToken)
    {
        if (AuthorizeUser(out _, administrator: true) is { } rejection) return rejection;
        await catalog.ClearSearchAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private IActionResult? AuthorizeUser(out User? user, bool administrator = false)
    {
        Response.Headers.CacheControl = "no-store";
        user = JableAuthorization.GetUserId(User) is { } id ? users.GetUserById(id) : null;
        if (user is null) return Unauthorized();
        if (!access.CanAccess(user) || (administrator && !JableAuthorization.IsAdministrator(users.GetUserDto(user).Policy))) return Forbid();
        return null;
    }

    private IActionResult Resource(string name, string contentType)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        var stream = typeof(JableController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Jable.Web." + name);
        return stream is null ? NotFound() : File(stream, contentType);
    }
}
