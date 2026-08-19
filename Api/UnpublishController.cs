using System;
using System.Collections.Generic;
using System.Linq;
using Unpublish.Configuration;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;

namespace Unpublish.Api
{
    [ApiController]
    [Route("Unpublish")]
    [Authorize]
    public class UnpublishController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;

        public UnpublishController(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        [HttpGet("Config")]
        public ActionResult<PluginConfiguration> GetConfig()
        {
            return Ok(Plugin.Instance.Configuration);
        }

        [HttpPost("Config")]
        public ActionResult SaveConfig([FromBody] PluginConfiguration newConfig)
        {
            var config = Plugin.Instance.Configuration;
            config.LockedItemIds = newConfig.LockedItemIds ?? new List<string>();
            config.ImmuneUserIds = newConfig.ImmuneUserIds ?? new List<string>();
            config.ProtectionMode = newConfig.ProtectionMode;
            
            Plugin.Instance.SaveConfiguration();
            return Ok();
        }

        [HttpGet("Library/Shows")]
        public ActionResult GetShows()
        {
            try
            {
                var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Series },
                    Recursive = true,
                    IsFolder = true
                });

                var result = items.Select(i => new
                {
                    Id = i.Id,
                    Name = i.Name,
                    Type = i.GetType().Name
                }).OrderBy(i => i.Name);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.ToString());
            }
        }

        [HttpGet("Library/Children")]
        public ActionResult GetChildren([FromQuery] Guid parentId)
        {
            try
            {
                var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = parentId,
                    IncludeItemTypes = new[] { 
                        Jellyfin.Data.Enums.BaseItemKind.Season, 
                        Jellyfin.Data.Enums.BaseItemKind.Episode 
                    }
                });

                var result = items.Select(i =>
                {
                    var ep = i as MediaBrowser.Controller.Entities.TV.Episode;
                    var season = i as MediaBrowser.Controller.Entities.TV.Season;
                    var index = i.IndexNumber ?? ep?.IndexNumber ?? season?.IndexNumber;

                    string displayName = i.Name;
                    if (ep != null && index.HasValue)
                    {
                        displayName = $"Episode {index.Value}: {i.Name}";
                    }
                    else if (season != null && index.HasValue)
                    {
                        displayName = $"Season {index.Value}";
                        if (!string.IsNullOrWhiteSpace(i.Name) && !i.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                        {
                            displayName = $"{displayName} - {i.Name}";
                        }
                    }

                    return new
                    {
                        Id = i.Id,
                        Name = displayName,
                        IndexNumber = index,
                        Type = i.GetType().Name
                    };
                })
                .OrderBy(i => i.IndexNumber ?? int.MaxValue)
                .ThenBy(i => i.Name);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.ToString());
            }
        }
    }
}
