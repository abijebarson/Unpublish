using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Entities.TV;
using Unpublish.Configuration;

namespace Unpublish.Middleware
{
    public class SpoilerHidingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authContext;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<SpoilerHidingMiddleware> _logger;

        public SpoilerHidingMiddleware(
            RequestDelegate next,
            ILibraryManager libraryManager,
            IAuthorizationContext authContext,
            ISessionManager sessionManager,
            ILogger<SpoilerHidingMiddleware> logger)
        {
            _next = next;
            _libraryManager = libraryManager;
            _authContext = authContext;
            _sessionManager = sessionManager;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || config.LockedItemIds == null || !config.LockedItemIds.Any())
            {
                await _next(context);
                return;
            }

            var lockedSet = new HashSet<string>(
                config.LockedItemIds.Select(NormId).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase
            );

            if (lockedSet.Count == 0)
            {
                await _next(context);
                return;
            }

            var immuneSet = new HashSet<string>(
                config.ImmuneUserIds.Select(NormId).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase
            );

            var path = context.Request.Path.Value ?? string.Empty;
            var method = context.Request.Method;

            // 1. Resolve User & Check Immunity
            var userId = await GetUserIdAsync(context).ConfigureAwait(false);
            bool isImmune = !string.IsNullOrEmpty(userId) && immuneSet.Contains(userId);

            int mode = config.ProtectionMode; // 0 = HideCompletely, 1 = HideSpoilers, 2 = BlockPlaybackOnly

            if (isImmune)
            {
                await _next(context);
                return;
            }

            // 2. Block direct playback requests for locked items
            if (IsPlaybackRequest(path, context, out var playbackItemId))
            {
                if (IsItemLocked(playbackItemId, lockedSet))
                {
                    _logger.LogInformation("[EpisodeControl] Blocking Playback for item {Item} in mode {Mode}", playbackItemId, mode);
                    context.Response.StatusCode = mode == 0 ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"ErrorMessage\":\"This episode is locked.\",\"ErrorCode\":\"ItemLocked\"}");
                    return;
                }
            }

            // 3. Handle Image requests in Mode 1 (return padlock placeholder)
            if (mode == 1 && IsImageRequest(path, out var imageItemId))
            {
                if (IsItemLocked(imageItemId, lockedSet))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "image/svg+xml";
                    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                    var svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 160 90"" width=""160"" height=""90"">
  <rect width=""160"" height=""90"" fill=""#141417""/>
  <g fill=""#4e4e58"" transform=""translate(68, 33) scale(1.0)"">
    <path d=""M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm3.1-9H8.9V6c0-1.71 1.39-3.1 3.1-3.1 1.71 0 3.1 1.39 3.1 3.1v2z""/>
  </g>
</svg>";
                    await context.Response.WriteAsync(svg);
                    return;
                }
            }

            // 4. Intercept API metadata queries or Web UI HTML
            var isGet = HttpMethods.IsGet(method);
            var isMetadataPath = path.StartsWith("/Items", StringComparison.OrdinalIgnoreCase) ||
                                 path.StartsWith("/Shows", StringComparison.OrdinalIgnoreCase) ||
                                 path.StartsWith("/LiveTv", StringComparison.OrdinalIgnoreCase) ||
                                 (path.StartsWith("/Users", StringComparison.OrdinalIgnoreCase) && path.Contains("/Items", StringComparison.OrdinalIgnoreCase));
            
            var isWebHtmlPath = (mode == 1 || mode == 2) &&
                                (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                                 path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/web", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/", StringComparison.OrdinalIgnoreCase));

            if (!isGet || (!isMetadataPath && !isWebHtmlPath))
            {
                await _next(context);
                return;
            }

            // Disable compression on intercepted responses
            context.Request.Headers["Accept-Encoding"] = "identity";

            var originalBodyStream = context.Response.Body;
            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            try
            {
                await _next(context);
            }
            finally
            {
                context.Response.Body = originalBodyStream;
            }

            var contentType = context.Response.ContentType ?? string.Empty;

            // Handle API JSON metadata responses
            if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                byte[] responseBytes = responseBody.ToArray();
                string json;

                var encoding = context.Response.Headers["Content-Encoding"].ToString();
                if (string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream(responseBytes);
                    using var decompressor = new GZipStream(ms, CompressionMode.Decompress);
                    using var reader = new StreamReader(decompressor, System.Text.Encoding.UTF8);
                    json = await reader.ReadToEndAsync();
                }
                else if (string.Equals(encoding, "br", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream(responseBytes);
                    using var decompressor = new BrotliStream(ms, CompressionMode.Decompress);
                    using var reader = new StreamReader(decompressor, System.Text.Encoding.UTF8);
                    json = await reader.ReadToEndAsync();
                }
                else
                {
                    json = System.Text.Encoding.UTF8.GetString(responseBytes);
                }

                try
                {
                    var parsedJson = JsonNode.Parse(json);
                    bool modified = false;

                    if (parsedJson is JsonObject jsonObj)
                    {
                        if (jsonObj.ContainsKey("Items") && jsonObj["Items"] is JsonArray itemsArray)
                        {
                            if (mode == 0) // Mode 0: Hide Completely
                            {
                                var filteredList = new List<JsonNode>();
                                foreach (var item in itemsArray)
                                {
                                    if (item is JsonObject itemObj)
                                    {
                                        if (!IsItemJsonLocked(itemObj, lockedSet))
                                        {
                                            filteredList.Add(item);
                                        }
                                        else
                                        {
                                            modified = true;
                                        }
                                    }
                                    else if (item != null)
                                    {
                                        filteredList.Add(item);
                                    }
                                }

                                if (modified)
                                {
                                    var newArray = new JsonArray();
                                    foreach (var node in filteredList)
                                    {
                                        newArray.Add(JsonNode.Parse(node.ToJsonString()));
                                    }
                                    jsonObj["Items"] = newArray;
                                    if (jsonObj.ContainsKey("TotalRecordCount"))
                                    {
                                        jsonObj["TotalRecordCount"] = filteredList.Count;
                                    }
                                }
                            }
                            else // Mode 1 (Hide Spoilers) or Mode 2 (Block Playback Only)
                            {
                                foreach (var item in itemsArray)
                                {
                                    if (item is JsonObject itemObj && IsItemJsonLocked(itemObj, lockedSet))
                                    {
                                        ApplyLockModifications(itemObj, mode);
                                        modified = true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Single item lookup
                            if (IsItemJsonLocked(jsonObj, lockedSet))
                            {
                                if (mode == 0)
                                {
                                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                                    return;
                                }
                                else
                                {
                                    ApplyLockModifications(jsonObj, mode);
                                    modified = true;
                                }
                            }
                        }
                    }
                    else if (parsedJson is JsonArray jsonArray)
                    {
                        if (mode == 0) // Mode 0: Hide Completely
                        {
                            var filteredList = new List<JsonNode>();
                            foreach (var item in jsonArray)
                            {
                                if (item is JsonObject itemObj)
                                {
                                    if (!IsItemJsonLocked(itemObj, lockedSet))
                                    {
                                        filteredList.Add(item);
                                    }
                                    else
                                    {
                                        modified = true;
                                    }
                                }
                                else if (item != null)
                                {
                                    filteredList.Add(item);
                                }
                            }

                            if (modified)
                            {
                                var newArray = new JsonArray();
                                foreach (var node in filteredList)
                                {
                                    newArray.Add(JsonNode.Parse(node.ToJsonString()));
                                }
                                parsedJson = newArray;
                            }
                        }
                        else // Mode 1 or Mode 2
                        {
                            foreach (var item in jsonArray)
                            {
                                if (item is JsonObject itemObj && IsItemJsonLocked(itemObj, lockedSet))
                                {
                                    ApplyLockModifications(itemObj, mode);
                                    modified = true;
                                }
                            }
                        }
                    }

                    if (modified && parsedJson != null)
                    {
                        var newJson = parsedJson.ToJsonString();
                        context.Response.Headers.Remove("Content-Encoding");
                        context.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(newJson);
                        await context.Response.WriteAsync(newJson);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EpisodeControl] JSON parsing error in {Path}", path);
                }
            }
            // Handle Web HTML responses in Mode 1 & Mode 2: Inject Lock Button UI & Alert logic
            else if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase) && (mode == 1 || mode == 2))
            {
                byte[] responseBytes = responseBody.ToArray();
                var html = System.Text.Encoding.UTF8.GetString(responseBytes);

                if (html.Contains("</body>", StringComparison.OrdinalIgnoreCase) && !html.Contains("ec-injected-script"))
                {
                    var hideThumbCss = mode == 1 ? @"
  .ec-locked-item .listItemImage,
  .ec-locked-item .cardImage,
  .ec-locked-item .cardImageContainer,
  .ec-locked-item .cardPadder {
    background-image: none !important;
    background: #141417 !important;
    background-color: #141417 !important;
  }
  .ec-locked-item img {
    display: none !important;
  }
  .ec-locked-thumb-badge {
    position: absolute;
    inset: 0;
    background: #141417 !important;
    display: flex !important;
    align-items: center !important;
    justify-content: center !important;
    color: #5e5e6c !important;
    pointer-events: none !important;
    z-index: 10 !important;
  }" : "";

                    var clientScript = @"
<style id=""ec-injected-style"">
  " + hideThumbCss + @"
  .ec-locked-item .cardPlayButton,
  .ec-locked-item .listItemImageButton,
  .ec-locked-item .cardOverlayButton,
  .ec-locked-item .playButton,
  .ec-locked-item [data-action=""play""],
  .ec-locked-item [data-action=""resume""] {
    display: none !important;
    pointer-events: none !important;
  }
  .ec-lock-btn {
    background: rgba(239, 83, 80, 0.15) !important;
    border: 1px solid #ef5350 !important;
    color: #ef5350 !important;
    cursor: not-allowed !important;
    display: inline-flex !important;
    align-items: center !important;
    gap: 8px !important;
    padding: 0.6em 1.2em !important;
    border-radius: 6px !important;
    font-weight: 600 !important;
    user-select: none !important;
  }
  .ec-lock-btn svg {
    flex-shrink: 0;
  }
</style>
<script id=""ec-injected-script"">
(function() {
  var currentMode = " + mode + @";
  var padlockSvg = '<svg width=""28"" height=""28"" viewBox=""0 0 24 24"" fill=""currentColor""><path d=""M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm3.1-9H8.9V6c0-1.71 1.39-3.1 3.1-3.1 1.71 0 3.1 1.39 3.1 3.1v2z""/></svg>';

  function showAlert() {
    if (window.Dashboard && window.Dashboard.alert) {
      window.Dashboard.alert({ title: ""Content Locked"", message: ""This episode is locked. You do not have permission to play it."" });
    } else {
      alert(""This episode is locked. You do not have permission to play it."");
    }
  }

  function processLockedElements() {
    // 1. Transform Episode list rows (.listItem, .card)
    var listItems = document.querySelectorAll('.listItem, .card, [data-itemid]');
    listItems.forEach(function(itemEl) {
      var overviewEl = itemEl.querySelector('.listItem-overview, .overview, .cardText-secondary, [data-overview]');
      var text = (overviewEl ? overviewEl.textContent : '') + ' ' + itemEl.textContent;
      var isLocked = text.indexOf('to prevent spoilers') !== -1 || text.indexOf('episode is locked') !== -1 || itemEl.dataset.locked === 'true';
      
      if (isLocked) {
        if (!itemEl.classList.contains('ec-locked-item')) {
          itemEl.classList.add('ec-locked-item');
        }
        
        var playBtns = itemEl.querySelectorAll('.listItemImageButton, .cardPlayButton, .cardOverlayButton, .playButton, [data-action=""play""], [data-action=""resume""]');
        playBtns.forEach(function(btn) {
          btn.style.setProperty('display', 'none', 'important');
          btn.style.setProperty('pointer-events', 'none', 'important');
        });

        if (currentMode === 1) {
          if (overviewEl) {
            overviewEl.style.setProperty('color', '#888896', 'important');
            overviewEl.style.setProperty('font-style', 'italic', 'important');
          }

          var thumbContainers = itemEl.querySelectorAll('.listItemImage, .cardImageContainer, .cardPadder, .listItemImageContainer');
          thumbContainers.forEach(function(thumbContainer) {
            thumbContainer.style.setProperty('background-image', 'none', 'important');
            thumbContainer.style.setProperty('background', '#141417', 'important');
            thumbContainer.style.setProperty('background-color', '#141417', 'important');
            thumbContainer.style.position = 'relative';
            
            var img = thumbContainer.querySelector('img');
            if (img) img.style.setProperty('display', 'none', 'important');

            if (!thumbContainer.querySelector('.ec-locked-thumb-badge')) {
              var badge = document.createElement('div');
              badge.className = 'ec-locked-thumb-badge';
              badge.innerHTML = padlockSvg;
              thumbContainer.appendChild(badge);
            }
          });
        }
      }
    });

    // 2. Transform Item Details Page
    var detailButtons = document.querySelector('.mainDetailButtons');
    if (detailButtons && !detailButtons.querySelector('.ec-lock-btn')) {
      var overviewEl = document.querySelector('.overview, .itemOverview, [data-overview]');
      var text = (overviewEl ? overviewEl.textContent : '') + ' ' + document.body.textContent;
      var isLocked = text.indexOf('to prevent spoilers') !== -1 || text.indexOf('episode is locked') !== -1 || document.querySelector('.ec-locked-item');
      
      if (isLocked) {
        var playBtns = detailButtons.querySelectorAll('.btnPlay, .btnResume, .btnPlayAll, button[data-action=""play""], button[data-action=""resume""]');
        playBtns.forEach(function(btn) { btn.style.setProperty('display', 'none', 'important'); });
        
        var lockBtn = document.createElement('button');
        lockBtn.className = 'ec-lock-btn emby-button';
        lockBtn.type = 'button';
        lockBtn.innerHTML = padlockSvg + '<span>Episode Locked</span>';
        lockBtn.addEventListener('click', function(e) {
          e.preventDefault();
          e.stopPropagation();
          showAlert();
        });
        detailButtons.insertBefore(lockBtn, detailButtons.firstChild);
      }
    }
  }

  window.addEventListener('hashchange', function() { setTimeout(processLockedElements, 200); });
  document.addEventListener('viewshow', function() { setTimeout(processLockedElements, 200); });
  var observer = new MutationObserver(function() { processLockedElements(); });
  observer.observe(document.body, { childList: true, subtree: true });
})();
</script>";
                    var injectedHtml = html.Replace("</body>", clientScript + "</body>", StringComparison.OrdinalIgnoreCase);
                    context.Response.Headers.Remove("Content-Encoding");
                    context.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(injectedHtml);
                    await context.Response.WriteAsync(injectedHtml);
                    return;
                }
            }

            responseBody.Seek(0, SeekOrigin.Begin);
            await responseBody.CopyToAsync(originalBodyStream);
        }

        private void ApplyLockModifications(JsonObject item, int mode)
        {
            if (mode == 1) // Mode 1: Hide Spoilers (Keep title/number, hide overview & images)
            {
                item["Overview"] = "This episode is locked to prevent spoilers.";
                item["ImageTags"] = new JsonObject();
                item["BackdropImageTags"] = new JsonArray();
                item.Remove("PrimaryImageAspectRatio");
                item["Chapters"] = new JsonArray();
            }

            // In Mode 1 and Mode 2: Lock Playback DTO
            item["PlayAccess"] = "None";
            item["LocationType"] = "Virtual";
            item["IsPlaceHolder"] = true;
            item["CanDownload"] = false;
            item["CanDelete"] = false;
            item["CanResume"] = false;
            item["MediaSources"] = new JsonArray();
            item["MediaStreams"] = new JsonArray();
        }

        private bool IsItemJsonLocked(JsonObject item, HashSet<string> lockedSet)
        {
            if (item.TryGetPropertyValue("Id", out var idNode) && idNode != null)
            {
                var itemId = NormId(idNode.ToString());
                if (!string.IsNullOrEmpty(itemId))
                {
                    if (lockedSet.Contains(itemId)) return true;

                    var seriesId = string.Empty;
                    var seasonId = string.Empty;

                    if (item.TryGetPropertyValue("SeriesId", out var seriesNode) && seriesNode != null)
                    {
                        seriesId = NormId(seriesNode.ToString());
                    }
                    if (item.TryGetPropertyValue("SeasonId", out var seasonNode) && seasonNode != null)
                    {
                        seasonId = NormId(seasonNode.ToString());
                    }

                    if (!string.IsNullOrEmpty(seriesId) && lockedSet.Contains(seriesId)) return true;
                    if (!string.IsNullOrEmpty(seasonId) && lockedSet.Contains(seasonId)) return true;

                    return IsItemLocked(itemId, lockedSet);
                }
            }
            return false;
        }

        private bool IsPlaybackRequest(string path, HttpContext context, out string itemId)
        {
            itemId = string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            if (segments.Length >= 3 &&
                string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[2], "PlaybackInfo", StringComparison.OrdinalIgnoreCase))
            {
                itemId = NormId(segments[1]);
                return !string.IsNullOrEmpty(itemId);
            }

            if (segments.Length >= 2 && string.Equals(segments[0], "Videos", StringComparison.OrdinalIgnoreCase))
            {
                itemId = NormId(segments[1]);
                return !string.IsNullOrEmpty(itemId);
            }

            if (segments.Length >= 2 && string.Equals(segments[0], "Audio", StringComparison.OrdinalIgnoreCase))
            {
                itemId = NormId(segments[1]);
                return !string.IsNullOrEmpty(itemId);
            }

            if (context.Request.Query.TryGetValue("ItemId", out var queryItemId) ||
                context.Request.Query.TryGetValue("itemId", out queryItemId) ||
                context.Request.Query.TryGetValue("Id", out queryItemId) ||
                context.Request.Query.TryGetValue("id", out queryItemId))
            {
                itemId = NormId(queryItemId.ToString());
                if (!string.IsNullOrEmpty(itemId))
                {
                    if (path.Contains("Playback", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("Stream", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsImageRequest(string path, out string itemId)
        {
            itemId = string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            for (int i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "Items", StringComparison.OrdinalIgnoreCase) && i + 2 < segments.Length)
                {
                    if (string.Equals(segments[i + 2], "Images", StringComparison.OrdinalIgnoreCase))
                    {
                        itemId = NormId(segments[i + 1]);
                        return !string.IsNullOrEmpty(itemId);
                    }
                }
                else if (string.Equals(segments[i], "Images", StringComparison.OrdinalIgnoreCase) && i > 0)
                {
                    itemId = NormId(segments[i - 1]);
                    if (!string.IsNullOrEmpty(itemId))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool IsItemLocked(string itemId, HashSet<string> lockedSet)
        {
            if (string.IsNullOrEmpty(itemId) || lockedSet.Count == 0)
            {
                return false;
            }

            if (lockedSet.Contains(itemId))
            {
                return true;
            }

            if (Guid.TryParse(itemId, out var guid))
            {
                try
                {
                    var item = _libraryManager.GetItemById(guid);
                    if (item != null)
                    {
                        if (item is Episode ep)
                        {
                            if (ep.SeriesId != Guid.Empty && lockedSet.Contains(NormId(ep.SeriesId))) return true;
                            if (ep.SeasonId != Guid.Empty && lockedSet.Contains(NormId(ep.SeasonId))) return true;
                        }
                        else if (item is Season season)
                        {
                            if (season.SeriesId != Guid.Empty && lockedSet.Contains(NormId(season.SeriesId))) return true;
                        }
                        else if (item.ParentId != Guid.Empty && lockedSet.Contains(NormId(item.ParentId)))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static string NormId(Guid guid)
        {
            if (guid == Guid.Empty) return string.Empty;
            return guid.ToString("N").ToLowerInvariant();
        }

        private static string NormId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var clean = value.Trim('"', ' ', '\'').Replace("-", "").ToLowerInvariant();
            return clean;
        }

        private async Task<string> GetUserIdAsync(HttpContext context)
        {
            // 1. Check Claims
            var claim = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.User?.FindFirst("UserId")?.Value;
            if (!string.IsNullOrEmpty(claim))
            {
                var norm = NormId(claim);
                if (!string.IsNullOrEmpty(norm)) return norm;
            }

            // 2. Check AuthorizationContext
            try
            {
                var authInfo = await _authContext.GetAuthorizationInfo(context.Request).ConfigureAwait(false);
                if (authInfo != null && authInfo.UserId != Guid.Empty)
                {
                    return NormId(authInfo.UserId);
                }
            }
            catch
            {
            }

            // 3. Check Query string
            if (context.Request.Query.TryGetValue("UserId", out var queryUserId) ||
                context.Request.Query.TryGetValue("userId", out queryUserId))
            {
                var norm = NormId(queryUserId.ToString());
                if (!string.IsNullOrEmpty(norm)) return norm;
            }

            // 4. Check Path: /Users/{id}/...
            var pathSegments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments != null)
            {
                for (int i = 0; i < pathSegments.Length - 1; i++)
                {
                    if (string.Equals(pathSegments[i], "Users", StringComparison.OrdinalIgnoreCase))
                    {
                        var norm = NormId(pathSegments[i + 1]);
                        if (!string.IsNullOrEmpty(norm)) return norm;
                    }
                }
            }

            return string.Empty;
        }
    }
}
