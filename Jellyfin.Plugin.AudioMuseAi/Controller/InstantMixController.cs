using System;
using System.Linq;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioMuseAi.Controller
{
    /// <summary>
    /// Overrides the default Jellyfin Instant Mix HTTP endpoint. The actual mix generation
    /// (AudioMuse AI + native fallback) is handled by the <see cref="Services.AudioMuseMusicManagerDecorator"/>
    /// which is registered as <see cref="IMusicManager"/> in the DI container. This ensures
    /// AudioMuse logic runs for both web clients (this endpoint) and DLNA casting
    /// (which calls <see cref="IMusicManager"/> directly via <c>SessionManager</c>).
    /// </summary>
    [ApiController]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class InstantMixController : ControllerBase
    {
        private readonly ILogger<InstantMixController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IDtoService _dtoService;
        private readonly IMusicManager _musicManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="InstantMixController"/> class.
        /// </summary>
        public InstantMixController(
            ILogger<InstantMixController> logger,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IDtoService dtoService,
            IMusicManager musicManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _dtoService = dtoService;
            _musicManager = musicManager;
        }

        /// <summary>
        /// Gets a sonic-similarity-based instant mix.
        /// The Order = -1 gives this endpoint priority over the default one.
        /// </summary>
        [HttpGet("Items/{itemId}/InstantMix", Order = -1)]
        [ProducesResponseType(typeof(QueryResult<BaseItemDto>), 200)]
        public ActionResult<QueryResult<BaseItemDto>> GetInstantMix(
            [FromRoute] Guid itemId,
            [FromQuery] Guid? userId,
            [FromQuery] int? limit,
            [FromQuery] ItemFields[] fields,
            [FromQuery] bool? enableImages,
            [FromQuery] int? imageTypeLimit,
            [FromQuery] ImageType[] enableImageTypes,
            [FromQuery] bool? enableUserData)
        {
            var user = userId.HasValue ? _userManager.GetUserById(userId.Value) : null;
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _logger.LogError("AudioMuseAI: Item with ID {ItemId} not found.", itemId);
                return new QueryResult<BaseItemDto>();
            }

            var dtoOptions = new DtoOptions
            {
                Fields = fields,
                EnableImages = enableImages ?? false,
                EnableUserData = enableUserData ?? false,
                ImageTypeLimit = imageTypeLimit ?? 1,
                ImageTypes = enableImageTypes
            };

            var resultLimit = limit ?? 200;

            // Delegate entirely to IMusicManager, which is the AudioMuseMusicManagerDecorator.
            // The decorator handles AudioMuse AI similarity and falls back to native Jellyfin.
            var items = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);
            var dtoList = _dtoService.GetBaseItemDtos(items.Take(resultLimit).ToList(), dtoOptions, user);

            _logger.LogInformation(
                "AudioMuseAI: Returning Instant Mix with {Count} items for '{ItemName}'.",
                dtoList.Count,
                item.Name);

            return new QueryResult<BaseItemDto>
            {
                Items = dtoList.ToArray(),
                TotalRecordCount = dtoList.Count
            };
        }
    }
}
