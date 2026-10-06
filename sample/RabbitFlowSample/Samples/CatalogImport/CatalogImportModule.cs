using System.Runtime.CompilerServices;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;

namespace RabbitFlowSample.Samples.CatalogImport;

// A finite, paginated supplier feed: products become available one page at a time.
// No predeclared topology and no long-lived session spanning HTTP requests.
public static class CatalogImportModule
{
    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/catalog-imports", async (
            [FromBody] CatalogImportRequest request,
            IRabbitFlowTemporary temporary,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (request.Pages is < 1 or > 20 || request.PageSize is < 1 or > 25 ||
                request.PageDelayMs is < 0 or > 2000)
            {
                return Results.BadRequest(new { error = "Use 1–20 pages, 1–25 products per page and a page delay of 0–2000 ms." });
            }

            var run = await temporary.RunAsync<CatalogProduct, ImportedProduct>(
                messages: ReadSupplierPagesAsync(request, logger),
                onMessageReceived: async (product, workerCt) =>
                {
                    // Simulate a database upsert while subsequent supplier pages are still loading.
                    await Task.Delay(150, workerCt);
                    logger.LogInformation("[CatalogImport] Imported {Sku}", product.Sku);
                    return new ImportedProduct(product.Sku, DateTime.UtcNow);
                },
                onCompletedAsync: (result, _) =>
                {
                    logger.LogInformation(
                        "[CatalogImport] Finished. SourceCompleted={SourceCompleted}, Observed={Observed}, Imported={Imported}, Failed={Failed}",
                        result.SourceCompleted, result.TotalMessages, result.SucceededMessages, result.FailedMessages);
                    return Task.CompletedTask;
                },
                options: new RunTemporaryOptions
                {
                    QueuePrefixName = "catalog-import",
                    PrefetchCount = 4,
                    Timeout = TimeSpan.FromSeconds(5),
                    RunTimeout = TimeSpan.FromMinutes(2)
                },
                cancellationToken: ct);

            // This endpoint waits for the finite import, returning counters and collected results.
            // It is not a durable webhook acceptance endpoint.
            return Results.Ok(run);
        })
        .WithTags("Catalog Import")
        .WithName("ImportStreamingCatalog")
        .WithSummary("Imports products from a simulated paginated supplier feed through a temporary queue.")
        .Produces<TemporaryRunResult<ImportedProduct>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);
    }

    private static async IAsyncEnumerable<CatalogProduct> ReadSupplierPagesAsync(
        CatalogImportRequest request,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            for (var page = 1; page <= request.Pages; page++)
            {
                // Replace this delay with an HTTP page fetch or an asynchronous database cursor.
                await Task.Delay(request.PageDelayMs, ct);
                logger.LogInformation("[CatalogImport] Supplier page {Page} arrived", page);

                for (var item = 1; item <= request.PageSize; item++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new CatalogProduct($"SKU-{page:D2}-{item:D2}");
                }
            }
            logger.LogInformation("[CatalogImport] Supplier source exhausted; waiting for remaining handlers");
        }
        finally
        {
            logger.LogInformation("[CatalogImport] Supplier enumerator released");
        }
    }
}

public sealed record CatalogImportRequest(int Pages = 3, int PageSize = 4, int PageDelayMs = 750);
public sealed record CatalogProduct(string Sku);
public sealed record ImportedProduct(string Sku, DateTime ImportedUtc);
