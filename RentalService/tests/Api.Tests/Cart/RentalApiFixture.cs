using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Kafka.Producers;

namespace Dprental.RentalService.Api.Tests.Cart;

public class RentalApiFixture : WebApplicationFactory<Program>
{
    public HttpClient CreateAuthorizedClient(Guid renterId)
    {
        var client = CreateClient();

        // Truyền ID giả lập và chỉ định dùng Scheme TestAuth
        client.DefaultRequestHeaders.Add("X-Test-Renter-Id", renterId.ToString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestAuth");

        return client;
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", "dev-internal-api-key-dprental-2026");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", PostgresTestDatabaseHelper.BaseConnectionString);
        builder.UseSetting("InternalAuthentication:ApiKey", "dev-internal-api-key-dprental-2026");
        builder.ConfigureTestServices(services =>
        {
            // 1. Vượt qua [Authorize] bằng TestAuthHandler
            services.AddAuthentication("TestAuth")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestAuth", options => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = "TestAuth";
                options.DefaultAuthenticateScheme = "TestAuth";
                options.DefaultChallengeScheme = "TestAuth";
            });

            // 2. Mock CurrentRenter lấy từ Header
            services.AddHttpContextAccessor();
            services.AddScoped<ICurrentRenter, FakeCurrentRenter>();

            // 3. Mock Repository (Lưu trữ RAM bằng Singleton Dictionary)
            services.AddSingleton<IRentalCartRepository, FakeRentalCartRepository>();

            // 4. Mock Inventory Providers (Trả về dữ liệu dummy)
            services.AddSingleton<IRentalListingReader>(Dependencies);
            services.AddSingleton<IRentalAvailabilityReader>(Dependencies);
            services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
            services.AddSingleton<IUnitOfWork, FakeUnitOfWork>();
            services.AddSingleton<IRentalEventProducer, FakeRentalEventProducer>();
            services.AddSingleton<IRentalCartPolicy, RentalCartOptions>();

            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddScoped<RentalCartItemEvaluator>();
            services.AddScoped<RentalCartUseCases>();
            services.AddScoped<GetRentalCart>();
        });
    }

    public FakeDependencies Dependencies { get; } = new();

    // ==========================================
    // CÁC CLASS MOCK DÙNG RIÊNG CHO MÔI TRƯỜNG TEST
    // ==========================================

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Context.Request.Headers.TryGetValue("X-Test-Renter-Id", out var renterIdStr))
            {
                var claims = new[] { new Claim("sub", renterIdStr.ToString()) };
                var identity = new ClaimsIdentity(claims, "TestAuth");
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, "TestAuth");

                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
            return Task.FromResult(AuthenticateResult.Fail("Unauthorized"));
        }
    }

    private sealed class FakeCurrentRenter : ICurrentRenter
    {
        private readonly IHttpContextAccessor _accessor;
        public FakeCurrentRenter(IHttpContextAccessor accessor) => _accessor = accessor;

        public Guid RenterId
        {
            get
            {
                var val = _accessor.HttpContext?.User?.FindFirst("sub")?.Value;
                return Guid.TryParse(val, out var id) ? id : Guid.Empty;
            }
        }
    }

    private sealed class FakeRentalCartRepository : IRentalCartRepository
    {
        // Dùng ConcurrentDictionary để Thread-Safe cho Integration Tests
        private readonly ConcurrentDictionary<Guid, RentalCart> _carts = new();

        public Task<RentalCart?> GetActiveByRenterIdAsync(Guid renterId, CancellationToken token = default)
        {
            var cart = _carts.Values.FirstOrDefault(c => c.RenterId == renterId && c.Status == RentalCartStatus.Active);
            return Task.FromResult(cart);
        }

        public Task<RentalCart?> GetByRenterIdAsync(Guid renterId, CancellationToken token = default)
        {
            var cart = _carts.Values.FirstOrDefault(c => c.RenterId == renterId);
            return Task.FromResult(cart);
        }

        // Sửa kiểu trả về thành Task<RentalCart> cho khớp với Interface
        public Task<RentalCart> CreateAsync(RentalCart cart, CancellationToken token = default)
        {
            _carts.TryAdd(cart.Id, cart);
            return Task.FromResult(cart);
        }

        public Task<bool> UpdateAsync(RentalCart cart, CancellationToken token = default)
        {
            _carts[cart.Id] = cart;
            return Task.FromResult(true);
        }

        // Bổ sung hàm GetByIdAsync bị thiếu
        public Task<RentalCart?> GetByIdAsync(Guid id, CancellationToken token = default)
        {
            _carts.TryGetValue(id, out var cart);
            return Task.FromResult(cart);
        }

        // Bổ sung hàm DeleteAsync bị thiếu
        public Task<bool> DeleteAsync(Guid id, CancellationToken token = default)
        {
            return Task.FromResult(_carts.TryRemove(id, out _));
        }
    }

    public sealed class FakeDependencies : IRentalListingReader, IRentalAvailabilityReader
    {
        public Func<Guid, Task<Result<RentalListingSnapshot>>>? ListingHandler { get; set; }
        public Func<Guid, DateOnly, DateOnly, int, Task<Result<RentalAvailabilityResult>>>? AvailabilityHandler { get; set; }

        public Task<Result<RentalListingSnapshot>> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default)
            => ListingHandler?.Invoke(listingId)
               ?? Task.FromResult(Result.Success(new RentalListingSnapshot(
                   listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1")));

        public Task<Result<RentalAvailabilityResult>> GetAvailableQuantityAsync(
            Guid listingId, DateOnly startDate, DateOnly endDate, int requestedQuantity, CancellationToken cancellationToken = default)
            => AvailabilityHandler?.Invoke(listingId, startDate, endDate, requestedQuantity)
               ?? Task.FromResult(Result.Success(new RentalAvailabilityResult(999, true)));
    }
}
