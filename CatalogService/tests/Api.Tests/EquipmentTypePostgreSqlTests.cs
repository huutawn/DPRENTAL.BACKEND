using System.Net;
using System.Net.Http.Json;
using Dprental.CatalogService.Application.DTOs.EquipmentType;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

/// <summary>
/// Chạy luồng equipment type trên PostgreSQL thật, để kiểm tra những thứ InMemory bỏ qua:
/// migration, unique index, khóa ngoại và trường hợp hai request ghi cùng lúc.
/// </summary>
[Collection(PostgreSqlTestGroup.Name)]
public sealed class EquipmentTypePostgreSqlTests : IAsyncLifetime, IDisposable
{
    private const string BaseUrl = "/api/v1/catalog/equipment-types";

    // Database riêng trên cùng server của fixture: API lưu lịch sử migration trong schema "catalog",
    // khác với database dùng chung của các test repository khác, nên không dùng lẫn được.
    private static readonly string ConnectionString = new NpgsqlConnectionStringBuilder(PostgreSqlFixture.ConnectionString)
    {
        Database = "catalogservice_equipment_type_test"
    }.ConnectionString;

    private readonly EquipmentTypeApiFactory factory = new(ConnectionString);

    public async Task InitializeAsync() =>
        await factory.WithDbContextAsync(async db =>
        {
            // Migrate tự tạo database nếu chưa có, nên đây cũng là phép thử "migrate từ database trống".
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE \"catalog\".\"EquipmentTypes\", \"catalog\".\"EquipmentMedia\", \"catalog\".\"Equipments\", \"catalog\".\"Categories\" CASCADE;");
        });

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task FullLifecycle_PersistsStatusAsText_AndTimestampsInUtc()
    {
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        using HttpClient anonymous = factory.CreateClient();

        EquipmentTypeManagementResponse created = await EquipmentTypeManagementEndpointTests.CreateAsync(admin, category.Id);
        using HttpResponseMessage rename = await admin.PatchAsJsonAsync($"{BaseUrl}/{created.Id}", new { name = "Forklift 2–3T", description = "Mô tả mới" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        using HttpResponseMessage publish = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        using HttpResponseMessage publicDetail = await anonymous.GetAsync($"{BaseUrl}/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, publicDetail.StatusCode);

        using HttpResponseMessage disable = await admin.PostAsync($"{BaseUrl}/{created.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        using HttpResponseMessage hiddenDetail = await anonymous.GetAsync($"{BaseUrl}/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, hiddenDetail.StatusCode);

        await factory.WithDbContextAsync(async db =>
        {
            string rawStatus = await db.Database
                .SqlQuery<string>($"SELECT \"Status\" AS \"Value\" FROM catalog.\"EquipmentTypes\"")
                .SingleAsync();
            Assert.Equal("Disabled", rawStatus);

            EquipmentType stored = await db.EquipmentTypes.SingleAsync();
            Assert.Equal(EquipmentTypeStatus.Disabled, stored.Status);
            Assert.Equal("Forklift 2–3T", stored.Name);
            Assert.Equal("FORKLIFT 2–3T", stored.NormalizedName);
            Assert.Equal(DateTimeKind.Utc, stored.PublishedAtUtc!.Value.Kind);
            Assert.NotNull(stored.DisabledAtUtc);
        });
    }

    [Fact]
    public async Task ConcurrentCreates_OfSameCanonicalType_YieldExactlyOneSuccess()
    {
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();

        // Chạy song song để vượt qua bước kiểm tra trùng ở service; unique index phải chặn phần còn lại.
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            admin.PostAsJsonAsync(BaseUrl, EquipmentTypeManagementEndpointTests.ValidRequest(category.Id, code: "RACE-1", name: "Race type"))));

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.All(
                responses.Where(response => response.StatusCode != HttpStatusCode.Created),
                response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
            await factory.WithDbContextAsync(async db => Assert.Equal(1, await db.EquipmentTypes.CountAsync()));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Database_RejectsDuplicateCodeAndDuplicateNameInCategory()
    {
        Category category = await factory.SeedCategoryAsync();
        await InsertAsync(category.Id, code: "DUP-1", name: "Type A");

        PostgresException duplicateCode = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(category.Id, code: "DUP-1", name: "Type B"));
        Assert.Equal(EquipmentTypeRepository.CodeIndexName, duplicateCode.ConstraintName);

        PostgresException duplicateName = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(category.Id, code: "DUP-2", name: "type  a"));
        Assert.Equal(EquipmentTypeRepository.NameIndexName, duplicateName.ConstraintName);
    }

    [Fact]
    public async Task Database_RejectsDeletingCategoryThatHasEquipmentTypes()
    {
        Category category = await factory.SeedCategoryAsync();
        await InsertAsync(category.Id, code: "KEEP-1", name: "Type A");

        await factory.WithDbContextAsync(async db =>
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => db.Categories.Where(c => c.Id == category.Id).ExecuteDeleteAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        });
    }

    [Fact]
    public async Task Update_ToCodeTakenConcurrently_ReturnsConflict()
    {
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        await EquipmentTypeManagementEndpointTests.CreateAsync(admin, category.Id, code: "TAKEN-1", name: "Type A");
        EquipmentTypeManagementResponse second = await EquipmentTypeManagementEndpointTests.CreateAsync(admin, category.Id, code: "FREE-1", name: "Type B");

        using HttpResponseMessage response = await admin.PatchAsJsonAsync($"{BaseUrl}/{second.Id}", new { code = "taken-1" });

        await EquipmentTypeManagementEndpointTests.AssertProblemAsync(response, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.CodeAlreadyExists);
    }

    [Fact]
    public async Task Save_OfTypeChangedByAnotherRequest_IsRejected_AndKeepsCodeLocked()
    {
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        EquipmentTypeManagementResponse created = await EquipmentTypeManagementEndpointTests.CreateAsync(admin, category.Id, code: "STALE-1", name: "Type A");

        using IServiceScope staleRequest = factory.Services.CreateScope();
        IEquipmentTypeRepository repository = staleRequest.ServiceProvider.GetRequiredService<IEquipmentTypeRepository>();

        // Request thứ nhất đọc type khi còn Draft (mã chưa bị khóa)...
        EquipmentType stale = (await repository.GetForUpdateAsync(created.Id))!;
        Assert.False(stale.IsCodeLocked);

        // ...request thứ hai publish xong trước khi request thứ nhất kịp lưu.
        using HttpResponseMessage publish = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        stale.Code = "STALE-2";
        EquipmentTypeSaveOutcome outcome = await repository.SaveChangesAsync();

        Assert.Equal(EquipmentTypeSaveOutcome.ConcurrentUpdate, outcome);
        EquipmentTypeManagementResponse current = await EquipmentTypeManagementEndpointTests.GetAsync(admin, created.Id);
        Assert.Equal("STALE-1", current.Code);
        Assert.Equal(nameof(EquipmentTypeStatus.Published), current.Status);
    }

    /// <summary>Ghi thẳng qua DbContext, bỏ qua kiểm tra của service, để thử ràng buộc của database.</summary>
    private async Task InsertAsync(Guid categoryId, string code, string name)
    {
        EquipmentType type = new() { Id = Guid.NewGuid(), CategoryId = categoryId, Code = code };
        type.Rename(name);

        await factory.WithDbContextAsync(async db =>
        {
            db.EquipmentTypes.Add(type);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgresException)
            {
                throw postgresException;
            }
        });
    }
}
