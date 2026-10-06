# Catalog import from an asynchronous source

`POST /catalog-imports` simulates importing a supplier catalog received in successive pages.
It uses `IRabbitFlowTemporary.RunAsync<T, TResult>(IAsyncEnumerable<T>, ...)`: each product
is published as it becomes available, and workers import products while later pages are loading.
The simulation performs no external HTTP calls or database writes.

Run the sample API and send a request from [CatalogImport.http](CatalogImport.http), or use Swagger.
With the default three pages of four products, expect `sourceCompleted: true`, `success: true`,
12 observed/published/processed messages and 12 results. Logs show products being imported between
successive page arrivals, followed by source exhaustion and run completion.

There is one exclusive, non-durable, auto-delete queue per request, named
`catalog-import-temp-queue-{guid}`. No registration or topology declaration is required upfront.
The queue is removed when the run releases its connection.

The source ends explicitly after its last page. Empty periods between pages do not end the run.
The response waits for the source and outstanding handlers; results are collected in memory and
may be returned in processing completion order. The example bounds its input to 500 products.
Each handler has a five-second cooperative timeout, and the whole run has a two-minute timeout.
Disconnecting the HTTP request propagates cancellation to the source and workers.

This is a finite import within one request, not a webhook session shared across requests.
The temporary queue retains the existing best-effort processing semantics, including acknowledgement
before handler execution; it is not durable storage. Persist important business work separately.
