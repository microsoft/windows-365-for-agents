# Model request resilience

The Responses API loop bounds each **model step**, not the whole user turn:

- First HTTP attempt: 60 seconds; later attempts: 120 seconds.
- Total model-step budget: 180 seconds, including retry delays.
- At most one retry for an owned attempt timeout, HTTP 408/500/502/503/504,
  or a classified connection/response-ended transport failure.
- HTTP 429 retains at most four total attempts within that same budget.
- Retry-After delta-seconds or HTTP-date is honored, with nonnegative delay and
  up to one additional second of jitter. Otherwise, backoff is 2/4/8 seconds
  plus jitter. A server delay that cannot fit is not shortened.
- Caller cancellation is propagated without retry. Budget exhaustion is
  reported as a timeout, distinct from caller cancellation.

Each retry sends the same serialized model request. It does not restart the
turn, reacquire MCP tools, repeat completed tool calls, or allocate another PC.
A timed-out inference can still finish remotely, so duplicate inference cost
remains possible.

The named model HTTP client's built-in timeout is disabled because these
explicit cancellation deadlines own request timing. A model step can consume
its full budget; user-turn and external test deadlines need to account for
multiple model steps and tool latency.

This is not blanket recovery for every `HttpRequestException`. Wrapped upload
I/O failures classified as `Unknown` remain outside the selected retry filter.
Malformed responses and nontransient statuses are not retried.

Regression coverage: [model-step behavior](../tests/ModelResilienceTests.cs) and
[Retry-After handling](../tests/ModelRetryDelayTests.cs).
