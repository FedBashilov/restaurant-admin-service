﻿// Copyright (c) Fedor Bashilov. All rights reserved.

namespace Menu.Service
{
    using System.Linq;
    using CloudStorage.Service.Interfaces;
    using Infrastructure.Core.Models;
    using Infrastructure.Database;
    using Menu.Service.Exceptions;
    using Menu.Service.Interfaces;
    using Menu.Service.Models.DTOs;
    using Messaging.Service.Interfaces;
    using Microsoft.EntityFrameworkCore;

    public class MenuService : IMenuService
    {
        private readonly ICloudStorageService cloudStorageService;
        private readonly INewsMessagingService newsMessagingService;
        private readonly IDbContextFactory<MenuServiceDatabaseContext> dbCxtFactory;

        public MenuService(
            ICloudStorageService cloudStorageService,
            INewsMessagingService newsMessagingService,
            IDbContextFactory<MenuServiceDatabaseContext> dbCxtFactory)
        {
            this.cloudStorageService = cloudStorageService;
            this.newsMessagingService = newsMessagingService;
            this.dbCxtFactory = dbCxtFactory;
        }

        public async Task<List<MenuItemResponse>> GetMenu(
            int offset = 0,
            int count = 100,
            IEnumerable<int>? categories = default,
            IEnumerable<int>? ids = default,
            bool orderDesc = false,
            bool onlyVisible = true)
        {
            await using var dbContext = await this.dbCxtFactory.CreateDbContextAsync();

            var selectQuery =
                dbContext.Menu.Select(m => new MenuItemResponse
                {
                    Id = m.Id,
                    Name = m.Name,
                    Description = m.Description,
                    Price = m.Price,
                    Visible = m.Visible,
                    ImageUrl = m.ImageUrl,
                    Categories = dbContext.MenuItemCategories
                        .Where(c => c.MenuItemId == m.Id)
                        .Select(c => c.CategoryId)
                        .ToList(),
                });

            var whereQuery = selectQuery.Where(x =>
                    (onlyVisible == default || x.Visible == true) &&
                    (ids == default || ids.Any(id => id == x.Id)) &&
                    (categories == default || x.Categories!.Any(x => categories.Any(y => y == x))));

            var orderQuery = orderDesc ?
                whereQuery.OrderByDescending(x => x.Id) :
                whereQuery.OrderBy(x => x.Id);

            var pageQuery = orderQuery.Skip(offset).Take(count);

            var menu = await pageQuery.ToListAsync();

            return menu;
        }

        public async Task<MenuItemResponse> GetMenuItem(int id)
        {
            await using var dbContext = await this.dbCxtFactory.CreateDbContextAsync();

            var menuItemQuery = dbContext.Menu.Select(m => new MenuItemResponse
            {
                Id = m.Id,
                Name = m.Name,
                Description = m.Description,
                Price = m.Price,
                Visible = m.Visible,
                ImageUrl = m.ImageUrl,
                Categories = dbContext.MenuItemCategories
                    .Where(c => c.MenuItemId == m.Id)
                    .Select(c => c.CategoryId)
                    .ToList(),
            });

            var menuItem = await menuItemQuery.FirstOrDefaultAsync(x => x.Id == id);

            if (menuItem == null)
            {
                throw new MenuItemNotFoundException($"Not found menuItem with id = {id} while executing GetMenuItem method");
            }

            return menuItem;
        }

        public async Task<MenuItemResponse> CreateMenuItem(MenuItemDTO newItemDto)
        {
            await using var dbContext = await this.dbCxtFactory.CreateDbContextAsync();
            await using var transaction = dbContext.Database.BeginTransaction();

            try
            {
                var newItem = new MenuItem()
                {
                    Name = newItemDto.Name,
                    Description = newItemDto.Description,
                    Price = newItemDto.Price,
                    Visible = newItemDto.Visible,
                };

                if (newItemDto.Image != default && newItemDto.Image.Length != 0)
                {
                    var imageUrl = await this.cloudStorageService.UploadFile(newItemDto.Image, "menuItem", "/menuItems");
                    newItem.ImageUrl = imageUrl.ToString();
                }

                var menuItem = (await dbContext.Menu.AddAsync(newItem)).Entity;
                await dbContext.SaveChangesAsync();

                var menuItemCategories = new List<MenuItemCategory>();
                foreach (var item in newItemDto.Categories)
                {
                    menuItemCategories.Add(
                        new MenuItemCategory
                        {
                            CategoryId = item,
                            MenuItemId = menuItem.Id,
                        });
                }

                // TODO: проверить есть ли такая категория ? или отловить эксепшен
                await dbContext.MenuItemCategories.AddRangeAsync(menuItemCategories);
                await dbContext.SaveChangesAsync();

                transaction.Commit();

                var menuItemResponse = new MenuItemResponse()
                {
                    Id = menuItem.Id,
                    Name = menuItem.Name,
                    Description = menuItem.Description,
                    Price = menuItem.Price,
                    Visible = menuItem.Visible,
                    ImageUrl = menuItem.ImageUrl,
                    Categories = newItemDto.Categories,
                };

                this.newsMessagingService.SendMessage(menuItemResponse);

                return menuItemResponse;
            }
            catch (Exception ex)
            {
                throw new CreateMenuItemFailedException(ex.Message);
            }
        }

        public async Task<MenuItemResponse> UpdateMenuItem(int id, MenuItemDTO menutemDto)
        {
            await using var dbContext = await this.dbCxtFactory.CreateDbContextAsync();
            await using var transaction = dbContext.Database.BeginTransaction();

            try
            {
                if (!await dbContext.Menu.AsNoTracking().AnyAsync(x => x.Id == id))
                {
                    throw new MenuItemNotFoundException();
                }

                var menuItem = new MenuItem()
                {
                    Id = id,
                    Name = menutemDto.Name,
                    Description = menutemDto.Description,
                    Price = menutemDto.Price,
                    Visible = menutemDto.Visible,
                };

                if (menutemDto.Image != default && menutemDto.Image.Length != 0)
                {
                    var imageUrl = await this.cloudStorageService.UploadFile(menutemDto.Image, "menuItem", "/menuItems");
                    menuItem.ImageUrl = imageUrl.ToString();
                }

                var menuItemCategories = dbContext.Menu.AsNoTracking()
                    .Include(m => m.MenuItemCategories)
                    .FirstOrDefault(m => m.Id == id)?.MenuItemCategories;

                dbContext.TryUpdateManyToMany(
                    menuItemCategories,
                    menutemDto.Categories
                    .Select(c => new MenuItemCategory
                    {
                        MenuItemId = menuItem.Id,
                        CategoryId = c,
                    }),
                    x => x.CategoryId);

                dbContext.Menu.Update(menuItem);
                await dbContext.SaveChangesAsync();

                transaction.Commit();

                var menuItemResponse = new MenuItemResponse()
                {
                    Id = menuItem.Id,
                    Name = menuItem.Name,
                    Description = menuItem.Description,
                    Price = menuItem.Price,
                    Visible = menuItem.Visible,
                    ImageUrl = menuItem.ImageUrl,
                    Categories = menutemDto.Categories,
                };

                return menuItemResponse;
            }
            catch (Exception ex)
            {
                throw new UpdateMenuItemFailedException(ex.Message);
            }
        }
    }
}
