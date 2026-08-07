# Exception → ProblemDetails Wiring


When domain exceptions need HTTP error responses, each exception requires a **triple** of changes in `DomainExceptionProblemMapper`:

### 1. Add a problem-type constant

```csharp
private const string PracticeSessionNotFoundType = "https://tools.ietf.org/html/rfc7231#section-6.5.4";
private const string InvalidPracticeSessionTransitionType = "https://example.com/problems/invalid-practice-session-transition";
```

Convention: use the RFC 7231 URL for standard HTTP statuses (404, 409), or the project's own domain for domain-specific problems.

### 2. Add a switch-case entry

```csharp
PracticeSessionNotFoundException e => MapPracticeSessionNotFound(e),
```

### 3. Add the mapping method

```csharp
private static ProblemDetails MapPracticeSessionNotFound(PracticeSessionNotFoundException exception)
{
    var problem = new ProblemDetails
    {
        Type = PracticeSessionNotFoundType,
        Title = "Practice Session Not Found",
        Status = StatusCodes.Status404NotFound,
        Detail = exception.Message,
        Extensions = { ["applicationId"] = exception.ApplicationId, ["sessionId"] = exception.SessionId }
    };
    return problem;
}
```

Extension properties should expose every discriminator field the client needs to identify the error (IDs, status values, counts).

### Pitfall: WriteProblemAsync returns void

`HttpContext.WriteProblemAsync(problemDetails, ct)` writes the response but returns `void`, not `HttpResponse`. In Functions that must return `HttpResponse`:

```csharp
// ❌ Won't compile — void can't be returned as HttpResponse
return await req.HttpContext.WriteProblemAsync(problem, ct);

// ✅
await req.HttpContext.WriteProblemAsync(problem, ct);
return req.HttpContext.Response;
```

