﻿using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moonglade.Caching;
using Moonglade.Core;
using Moonglade.Model;
using Moonglade.Model.Settings;
using Moonglade.Web.Filters;
using Moonglade.Web.Models;

namespace Moonglade.Web.Controllers
{
    [Route("page")]
    public class PageController : BlogController
    {
        private readonly IBlogCache _cache;
        private readonly PageService _pageService;
        private static string[] InvalidPageRouteNames => new[] { "index", "manage" };

        public PageController(
            ILogger<PageController> logger,
            IOptions<AppSettings> settings,
            IBlogCache cache,
            PageService pageService) : base(logger, settings)
        {
            _cache = cache;
            _pageService = pageService;
        }

        [HttpGet("{slug:regex(^(?!-)([[a-zA-Z0-9-]]+)$)}")]
        public async Task<IActionResult> Index(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return BadRequest();

            var page = await _cache.GetOrCreateAsync(CacheDivision.Page, slug.ToLower(), async entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(AppSettings.CacheSlidingExpirationMinutes["Page"]);

                var p = await _pageService.GetAsync(slug);
                return p;
            });

            if (page == null)
            {
                Logger.LogWarning($"Page not found. {nameof(slug)}: '{slug}'");
                return NotFound();
            }

            if (!page.IsPublished) return NotFound();

            return View(page);
        }

        [Authorize]
        [Route("preview/{pageId}")]
        public async Task<IActionResult> Preview(Guid pageId)
        {
            var page = await _pageService.GetAsync(pageId);
            if (page == null)
            {
                Logger.LogWarning($"Page not found, parameter '{pageId}'.");
                return NotFound();
            }

            ViewBag.IsDraftPreview = true;
            return View("Index", page);
        }

        [Authorize]
        [HttpGet("manage")]
        public async Task<IActionResult> Manage()
        {
            var pageSegments = await _pageService.ListSegmentAsync();
            return View("~/Views/Admin/ManageCustomPage.cshtml", pageSegments);
        }

        [Authorize]
        [HttpGet("manage/create")]
        public IActionResult Create()
        {
            var model = new PageEditViewModel();
            return View("CreateOrEdit", model);
        }

        [Authorize]
        [HttpGet("manage/edit/{id:guid}")]
        public async Task<IActionResult> Edit(Guid id)
        {
            var page = await _pageService.GetAsync(id);
            if (page == null) return NotFound();

            var model = new PageEditViewModel
            {
                Id = page.Id,
                Title = page.Title,
                Slug = page.Slug,
                MetaDescription = page.MetaDescription,
                CssContent = page.CssContent,
                RawHtmlContent = page.RawHtmlContent,
                HideSidebar = page.HideSidebar,
                IsPublished = page.IsPublished
            };

            return View("CreateOrEdit", model);
        }

        [Authorize]
        [HttpPost("manage/createoredit")]
        [ServiceFilter(typeof(DeleteSiteMapCache))]
        public async Task<IActionResult> CreateOrEdit(PageEditViewModel model)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return Json("Invalid ModelState");
                }

                if (InvalidPageRouteNames.Contains(model.Slug.ToLower()))
                {
                    ModelState.AddModelError(nameof(model.Slug), "Reserved Slug.");
                    return View("CreateOrEdit", model);
                }

                var req = new EditPageRequest(model.Id)
                {
                    HtmlContent = model.RawHtmlContent,
                    CssContent = model.CssContent,
                    HideSidebar = model.HideSidebar,
                    Slug = model.Slug,
                    MetaDescription = model.MetaDescription,
                    Title = model.Title,
                    IsPublished = model.IsPublished
                };

                var uid = model.Id == Guid.Empty ?
                    await _pageService.CreateAsync(req) :
                    await _pageService.UpdateAsync(req);

                Logger.LogInformation($"User '{User.Identity.Name}' updated custom page id '{uid}'");
                _cache.Remove(CacheDivision.Page, req.Slug.ToLower());

                return Json(new { PageId = uid });
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error Create or Edit CustomPage.");
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return Json(e.Message);
            }
        }

        [Authorize]
        [HttpPost("manage/delete")]
        public async Task<IActionResult> Delete(Guid pageId, string slug)
        {
            try
            {
                await _pageService.DeleteAsync(pageId);

                _cache.Remove(CacheDivision.Page, slug.ToLower());
                return Json(pageId);
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Error Delete CustomPage, Id: {pageId}.");
                return ServerError();
            }
        }
    }
}