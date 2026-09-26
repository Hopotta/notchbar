using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using NotchBar.Core;

namespace NotchBar.Services;

public sealed class ApiService
{
    private readonly StatusStore _store;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _app;

    public ApiService(StatusStore store, SettingsService settings)
    {
        _store = store;
        _settings = settings;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ApiService).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            Args = Array.Empty<string>()
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 64 * 1024;
            options.Listen(IPAddress.Loopback, _settings.ApiPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        var app = builder.Build();
        MapEndpoints(app);

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _app = app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/health", () => Results.Ok(new
        {
            status = "ok",
            service = "NotchBar",
            apiVersion = "v1",
            utc = DateTimeOffset.UtcNow
        }));

        app.MapGet("/api/v1/items", () => Results.Ok(_store.GetActiveItems()));

        app.MapPut("/api/v1/items/{id}", (string id, StatusItemRequest? request) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "request body is required" });
            }

            var error = StatusItemValidation.Validate(request, id);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            if (StatusStore.IsReservedId(id))
            {
                return Results.BadRequest(new { error = $"'{id}' is reserved for a built-in item" });
            }

            try
            {
                return Results.Ok(_store.Put(request, id));
            }
            catch (StatusStoreCapacityException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status429TooManyRequests);
            }
        });

        app.MapDelete("/api/v1/items/{id}", (string id) =>
        {
            var idError = StatusItemValidation.ValidateId(id);
            if (idError is not null)
            {
                return Results.BadRequest(new { error = idError });
            }

            if (StatusStore.IsReservedId(id))
            {
                return Results.BadRequest(new { error = $"'{id}' is reserved for a built-in item" });
            }

            return _store.Delete(id, out _) ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/v1/notify", (NotificationRequest? request) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "request body is required" });
            }

            var error = StatusItemValidation.ValidateNotification(request);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                return Results.Ok(_store.AddNotification(request));
            }
            catch (StatusStoreCapacityException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status429TooManyRequests);
            }
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is null)
            {
                return;
            }

            var app = _app;
            try
            {
                await app.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _app = null;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
}
